using System.Globalization;

using Microsoft.Data.SqlClient;

using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// <c>logs.uspRecordHandlerLoadAttemptSet</c> (script 524): the per-request attempt log.
/// </summary>
/// <remarks>
/// <para>
/// The table this procedure writes is the one an operator opens at 03:00, and it is append-only:
/// <c>(HandlerLoadStatusId, AttemptNumber)</c> is its grain and nothing ever updates a row. That shape
/// is what makes the procedure worth its own test class rather than another entry in
/// <see cref="PayloadRefusalTests"/>, because it produces three behaviours the other five payload
/// procedures do not have.
/// </para>
/// <para>
/// <b>First, the caller names a handler version and not a status row.</b> The loader knows what EPA told
/// it — handler, source type, sequence — and which attempt it is on; the procedure resolves that to the
/// row the same run enumerated. So an attempt whose version the run never enumerated cannot be written
/// at all, and the interesting question is what happens to the rest of the flush.
/// </para>
/// <para>
/// <b>Second, refusing is sometimes the wrong answer.</b> Attempts are buffered and flushed in batches,
/// so one bad element among a hundred would take ninety-nine good diagnostic rows with it — on the table
/// whose whole purpose is diagnosing the run that produced it. The procedure therefore splits its
/// reactions: an incoherent element is refused (a retry cannot fix it, and the row would be wrong rather
/// than missing), while an orphan or a withheld value is <i>reported</i> through an output parameter and
/// everything resolvable is still written. Both halves are asserted here, and the reporting half is the
/// one that would rot silently — a procedure that quietly started refusing instead would look stricter
/// and better.
/// </para>
/// <para>
/// <b>Third, it is the first place the <c>RequestPath</c> contract is enforced rather than stated.</b>
/// Scripts 320, 500 and 511 all say the column holds the path and never the query string, because
/// RCRAInfo credentials travel in the auth URL's own path segments and in an <c>Authorization</c> header,
/// and the monitoring web app can read this table. Two of those scripts add that they cannot enforce it.
/// 524 can, and <see cref="ARequestPathThatIsNotABarePathIsWithheld"/> is what keeps it doing so.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class AttemptLogTests
{
    /// <summary>The procedure under test, for <see cref="RawCall"/>.</summary>
    private const string Procedure = "logs.uspRecordHandlerLoadAttemptSet";

    /// <summary>
    /// A fixed instant every attempt in this class is stamped from, so a failure message reads the same
    /// on every run.
    /// </summary>
    private static readonly DateTimeOffset Started =
        new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Attempts are resolved from the handler's natural key, and re-sending the same flush writes
    /// nothing.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The idempotency half is not a nicety. The AR8 completion update runs <i>after</i> the commit, so a
    /// call that committed can still be reported to the caller as failed — and
    /// <see cref="RCRAInfoDataOptions.MaxRetryCount"/> means the caller will send it again. A plain
    /// <c>INSERT</c> would meet <c>UX_logs_HandlerLoadAttempt_Natural</c> on that second send and raise
    /// 2601, turning a successful flush into a failed run.
    /// </remarks>
    [IntegrationFact]
    public async Task AnAttemptIsResolvedFromTheNaturalKeyAndReSendingWritesNothing()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string handlerId = TestData.HandlerId(524001);

        try
        {
            await EnumerateAsync(scope.Context, loadRunId, handlerId);

            HandlerLoadAttemptElement[] flush =
            [
                new()
                {
                    HandlerId = handlerId,
                    SourceType = TestData.SourceType,
                    Sequence = 1,
                    AttemptNumber = 1,
                    StartedDateUtc = Started,
                    CompletedDateUtc = Started.AddMilliseconds(360),
                    Outcome = "Throttled",
                    HttpStatusCode = 429,
                    RequestPath = Path(handlerId),
                    RetryAfterSeconds = 30,
                    ResponseBytes = 214,
                    ApiErrorCode = "E_RateLimitExceeded",
                },
                new()
                {
                    HandlerId = handlerId,
                    SourceType = TestData.SourceType,
                    Sequence = 1,
                    AttemptNumber = 2,
                    StartedDateUtc = Started.AddSeconds(30),
                    CompletedDateUtc = Started.AddSeconds(32),
                    Outcome = "Succeeded",
                    HttpStatusCode = 200,
                    RequestPath = Path(handlerId),
                    ResponseBytes = 48213,
                },
            ];

            AttemptRecordResult first =
                await scope.Context.RecordHandlerLoadAttemptSetAsync(loadRunId, flush);

            Assert.Equal(2, first.RowsAffected);
            Assert.Equal(0, first.RowsOrphaned);
            Assert.Equal(0, first.ValuesWithheld);

            AttemptRecordResult second =
                await scope.Context.RecordHandlerLoadAttemptSetAsync(loadRunId, flush);

            Assert.Equal(0, second.RowsAffected);
            Assert.Equal(0, second.RowsOrphaned);
            Assert.Equal(0, second.ValuesWithheld);

            // The digest is the same both times, which is what lets a log row identify the batch without
            // carrying it. If it were not, the two calls would not be the same call.
            Assert.Equal(first.Batch.Sha256, second.Batch.Sha256);

            await using SqlConnection connection = await IntegrationServer.OpenAsync();

            Assert.Equal(2, await CountAsync(connection, loadRunId, handlerId));

            Assert.Equal(
                "Throttled",
                await ColumnAsync(connection, loadRunId, handlerId, 1, "Outcome"));

            Assert.Equal(
                Path(handlerId),
                await ColumnAsync(connection, loadRunId, handlerId, 2, "RequestPath"));

            // The second attempt's own view of the run, unchanged by the re-send: the retry-after EPA
            // asked for on attempt 1 is what makes a 429 actionable (G21) and it is the one number in
            // this row nothing else in the schema records.
            Assert.Equal(
                30,
                await NumberAsync(connection, loadRunId, handlerId, 1, "RetryAfterSeconds"));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>
    /// The duration is derived from the two stamps when it is omitted, and a supplied value is kept.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The precedence matters in that direction and not the other. The loader times its calls with a
    /// <c>Stopwatch</c>, which is a monotonic count of ticks; the procedure's fallback is a
    /// <c>DATETIME2</c> subtraction of two values that were rounded on the way in. So a supplied value is
    /// the better measurement and the derivation exists for the calls that did not carry one — an attempt
    /// abandoned without a stopwatch reading, most obviously.
    /// </remarks>
    [IntegrationFact]
    public async Task TheDurationIsDerivedWhenOmittedAndTheSuppliedValueWins()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string handlerId = TestData.HandlerId(524002);

        try
        {
            await EnumerateAsync(scope.Context, loadRunId, handlerId);

            AttemptRecordResult result = await scope.Context.RecordHandlerLoadAttemptSetAsync(
                loadRunId,
                [
                    // Both stamps 2400ms apart AND an explicit 999. The explicit value is deliberately
                    // one the derivation could never produce from these stamps, so a test that passed
                    // because the two agreed is not possible.
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 1,
                        StartedDateUtc = Started,
                        CompletedDateUtc = Started.AddMilliseconds(2400),
                        DurationMs = 999,
                        Outcome = "Succeeded",
                        HttpStatusCode = 200,
                        RequestPath = Path(handlerId),
                    },

                    // The same two stamps, no DurationMs.
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 2,
                        StartedDateUtc = Started,
                        CompletedDateUtc = Started.AddMilliseconds(2400),
                        Outcome = "Succeeded",
                        HttpStatusCode = 200,
                        RequestPath = Path(handlerId),
                    },

                    // No completion at all: an attempt still in flight when the buffer flushed. The
                    // duration stays null rather than being derived from SYSUTCDATETIME (), which would
                    // report the age of the flush as the length of the call.
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 3,
                        StartedDateUtc = Started,
                        Outcome = "Cancelled",
                        RequestPath = Path(handlerId),
                    },
                ]);

            Assert.Equal(3, result.RowsAffected);
            Assert.Equal(0, result.ValuesWithheld);

            await using SqlConnection connection = await IntegrationServer.OpenAsync();

            Assert.Equal(999, await NumberAsync(connection, loadRunId, handlerId, 1, "DurationMs"));
            Assert.Equal(2400, await NumberAsync(connection, loadRunId, handlerId, 2, "DurationMs"));
            Assert.Null(await NumberAsync(connection, loadRunId, handlerId, 3, "DurationMs"));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>
    /// An element naming a version the run never enumerated is reported, and the rest of the flush is
    /// still written.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>The count is the assertion, not the failure.</b> An orphan is a loader defect — it recorded an
    /// attempt against a version it never told the database about, which means its enumeration and its
    /// fetch disagree — but it is a defect that costs one diagnostic row, and refusing the flush would
    /// cost the other ninety-nine. So the procedure writes what it can resolve and hands the number back.
    /// </para>
    /// <para>
    /// The good element is placed <i>after</i> the orphan in the array on purpose. A procedure that
    /// stopped at the first unresolvable element would still pass this test if the order were reversed.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task AnOrphanIsReportedAndTheRestOfTheFlushIsStillWritten()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string enumerated = TestData.HandlerId(524003);
        string neverEnumerated = TestData.HandlerId(524004);

        try
        {
            await EnumerateAsync(scope.Context, loadRunId, enumerated);

            AttemptRecordResult result = await scope.Context.RecordHandlerLoadAttemptSetAsync(
                loadRunId,
                [
                    new()
                    {
                        HandlerId = neverEnumerated,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 1,
                        StartedDateUtc = Started,
                        Outcome = "Succeeded",
                        HttpStatusCode = 200,
                        RequestPath = Path(neverEnumerated),
                    },
                    new()
                    {
                        HandlerId = enumerated,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 1,
                        StartedDateUtc = Started,
                        Outcome = "Succeeded",
                        HttpStatusCode = 200,
                        RequestPath = Path(enumerated),
                    },
                ]);

            Assert.Equal(1, result.RowsAffected);
            Assert.Equal(1, result.RowsOrphaned);
            Assert.Equal(0, result.ValuesWithheld);

            await using SqlConnection connection = await IntegrationServer.OpenAsync();

            Assert.Equal(1, await CountAsync(connection, loadRunId, enumerated));
            Assert.Equal(0, await CountAsync(connection, loadRunId, neverEnumerated));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>
    /// A request path that is not a bare path is replaced with a notice, and the row is still written.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The query-string case is the one that will actually happen: <c>/hd/other-ids</c> takes its
    /// <c>handlerId</c> as a query parameter, so the natural thing for a loader to log is
    /// <c>request.RequestUri.PathAndQuery</c> — and that is a habit which, applied one method along to
    /// the auth call, writes the API ID and Key into a table the monitoring web app displays.
    /// </para>
    /// <para>
    /// <b>Withheld, not refused, and the row is still written.</b> Refusing would discard the log to
    /// protect the log. The count comes back so the loader can report the defect it must then fix, and a
    /// non-zero count in a real run is a bug in the loader rather than a rejection by the database.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task ARequestPathThatIsNotABarePathIsWithheld()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string handlerId = TestData.HandlerId(524005);

        try
        {
            await EnumerateAsync(scope.Context, loadRunId, handlerId);

            AttemptRecordResult result = await scope.Context.RecordHandlerLoadAttemptSetAsync(
                loadRunId,
                [
                    // A query string.
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 1,
                        StartedDateUtc = Started,
                        Outcome = "Failed",
                        HttpStatusCode = 400,
                        RequestPath = "/api/v1/hd/other-ids?handlerId=" + handlerId,
                    },

                    // A whole URL, with credentials in the userinfo component -- the shape an
                    // exception.ToString () from the HTTP stack produces.
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 2,
                        StartedDateUtc = Started,
                        Outcome = "Failed",
                        HttpStatusCode = 502,
                        RequestPath = "https://id:key@rcrainfopreprod.epa.gov/api/v1/auth/id/key",
                    },

                    // Longer than the 400 characters the column stores. Not an identifier, so it is
                    // withheld rather than refused: the descriptive columns hold EPA's text, and refusing
                    // on their width would make this log hostage to another system's field sizes.
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 3,
                        StartedDateUtc = Started,
                        Outcome = "Failed",
                        HttpStatusCode = 404,
                        RequestPath = "/api/v1/hd/sources/" + new string('x', 500),
                    },
                ]);

            Assert.Equal(3, result.RowsAffected);
            Assert.Equal(0, result.RowsOrphaned);
            Assert.Equal(3, result.ValuesWithheld);

            await using SqlConnection connection = await IntegrationServer.OpenAsync();

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                string? stored = await ColumnAsync(connection, loadRunId, handlerId, attempt, "RequestPath");

                Assert.NotNull(stored);

                Assert.StartsWith("(withheld", stored, StringComparison.Ordinal);

                // Nothing of the original survives. The two assertions are separate because the first
                // would pass on a value that was prefixed with the notice and then appended verbatim.
                Assert.DoesNotContain("key", stored, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(handlerId, stored, StringComparison.Ordinal);
            }

            // The over-wide path is replaced, not clipped, and its notice says how long the value was --
            // a path cut off at 400 characters reads as a path that ended there.
            string? tooLong = await ColumnAsync(connection, loadRunId, handlerId, 3, "RequestPath");
            Assert.Contains("519", tooLong!, StringComparison.Ordinal);
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>
    /// A message naming the auth path is withheld, and the code and error identifier beside it survive.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The surviving half is the point. The auth URL's path segments <i>are</i> the API ID and Key, so a
    /// gateway or proxy that echoes the path it could not route puts the credential in EPA's own error
    /// text — and that text is not ours to sanitise selectively. What the code and the correlation
    /// identifier carry is different: they are the two fields EPA support asks for, they cannot contain a
    /// path, and losing them along with the message would leave a row that says only that something went
    /// wrong.
    /// </remarks>
    [IntegrationFact]
    public async Task AMessageNamingTheAuthPathIsWithheldButTheCodeAndErrorIdSurvive()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string handlerId = TestData.HandlerId(524006);
        const string ErrorId = "7f1c9d2e-0b44-4a1e-9c8d-2a5b6c7d8e90";

        try
        {
            await EnumerateAsync(scope.Context, loadRunId, handlerId);

            AttemptRecordResult result = await scope.Context.RecordHandlerLoadAttemptSetAsync(
                loadRunId,
                [
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 1,
                        StartedDateUtc = Started,
                        Outcome = "Failed",
                        HttpStatusCode = 502,
                        RequestPath = Path(handlerId),
                        ApiErrorCode = "E_GatewayError",
                        ApiErrorId = ErrorId,
                        ApiErrorMessage = "No route for GET /api/v1/auth/THEIDVALUE/THEKEYVALUE",
                        FailureMessage = "Bad gateway calling /api/v1/auth/THEIDVALUE/THEKEYVALUE.",
                    },
                ]);

            Assert.Equal(1, result.RowsAffected);
            Assert.Equal(2, result.ValuesWithheld);

            await using SqlConnection connection = await IntegrationServer.OpenAsync();

            foreach (string column in new[] { "ApiErrorMessage", "FailureMessage" })
            {
                string? stored = await ColumnAsync(connection, loadRunId, handlerId, 1, column);

                Assert.NotNull(stored);
                Assert.StartsWith("(withheld", stored, StringComparison.Ordinal);
                Assert.DoesNotContain("THEKEYVALUE", stored, StringComparison.Ordinal);
                Assert.DoesNotContain("THEIDVALUE", stored, StringComparison.Ordinal);
            }

            Assert.Equal(
                "E_GatewayError",
                await ColumnAsync(connection, loadRunId, handlerId, 1, "ApiErrorCode"));

            Assert.Equal(
                ErrorId,
                await ColumnAsync(connection, loadRunId, handlerId, 1, "ApiErrorId"));

            // The path itself was already bare, so it is untouched. Without this the test would pass on a
            // procedure that withheld every string it was given.
            Assert.Equal(
                Path(handlerId),
                await ColumnAsync(connection, loadRunId, handlerId, 1, "RequestPath"));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>Every element the procedure must refuse, one test each.</summary>
    /// <remarks>
    /// <para>
    /// Raw JSON rather than <see cref="HandlerLoadAttemptElement"/>, and not merely for brevity: several
    /// of these cannot be expressed through the typed path at all. <c>StartedDateUtc</c> is a
    /// non-nullable <see cref="DateTimeOffset"/>, so "no start time" has no C# spelling — which is the
    /// right design and also the reason the gate behind it would go untested by anything that only used
    /// the data layer. A hand-typed <c>EXEC</c> in a support session reaches this procedure with no C# in
    /// front of it.
    /// </para>
    /// <para>
    /// Every payload here is a well-formed JSON array naming a handler that <i>is</i> enumerated, so each
    /// refusal can only be the rule it is named for. The malformed-JSON and bare-object gates are
    /// <see cref="PayloadRefusalTests"/>'s, not this class's.
    /// </para>
    /// </remarks>
    public static TheoryData<string, string> IncoherentElements => new()
    {
        {
            "an outcome outside the five-value domain",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Retrying","httpStatusCode":500}"""
        },
        {
            "no start time",
            """{"attemptNumber":9,"outcome":"Succeeded","httpStatusCode":200}"""
        },
        {
            "no outcome",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","httpStatusCode":200}"""
        },
        {
            "Succeeded carrying an error",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Succeeded","httpStatusCode":200,"apiErrorCode":"E_Whatever"}"""
        },
        {
            "Failed carrying no detail at all",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Failed"}"""
        },
        {
            "completed before it started",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","completedDateUtc":"2026-09-06T01:59:00","outcome":"Succeeded","httpStatusCode":200}"""
        },
        {
            "a negative duration",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","durationMs":-5,"outcome":"Succeeded","httpStatusCode":200}"""
        },
        {
            "a status code of 0",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Failed","httpStatusCode":0}"""
        },
        {
            "attempt number 0",
            """{"attemptNumber":0,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Succeeded","httpStatusCode":200}"""
        },
        {
            "a negative retry-after",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Throttled","httpStatusCode":429,"retryAfterSeconds":-1}"""
        },
        {
            "negative response bytes",
            """{"attemptNumber":9,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Succeeded","httpStatusCode":200,"responseBytes":-1}"""
        },
    };

    /// <summary>An element that cannot describe a real attempt is refused, and nothing is written.</summary>
    /// <param name="description">What is wrong with it, for the failure message.</param>
    /// <param name="element">The element, as JSON, without its key.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <b>Refused, not withheld</b>, and the boundary is what the value does. None of these is fixable by
    /// a retry and none produces a row that is merely incomplete: a duration of -5 or a completion before
    /// its start is a row an operator would read and believe. The three reported cases —
    /// an orphan, an already-recorded grain, a withheld value — are the ones where refusing would lose
    /// good rows permanently, because the loader does not retry a 50000.
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(IncoherentElements))]
    public async Task AnIncoherentElementIsRefused(string description, string element)
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string handlerId = TestData.HandlerId(524007);

        try
        {
            await EnumerateAsync(scope.Context, loadRunId, handlerId);

            string payload = "[" + WithKey(element, handlerId) + "]";

            SqlException error = await RawCall.ExpectFailureAsync(
                Procedure,
                p =>
                {
                    p.Add(RawCall.Int("LoadRunId", loadRunId));
                    p.Add(RawCall.Payload("Elements", payload));
                });

            Assert.True(
                SqlErrorNumbers.IsProcedureRefusal(error),
                $"An element with {description} failed with error {error.Number} rather than the " +
                $"procedure's own refusal ({SqlErrorNumbers.ProcedureRefusal}). An engine error here " +
                "means the element got past validation and was rejected by a constraint instead, which " +
                $"names no element and no rule: '{error.Message}'");

            Assert.False(SqlErrorNumbers.IsRetryable(error));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));

            await using SqlConnection connection = await IntegrationServer.OpenAsync();

            Assert.Equal(0, await CountAsync(connection, loadRunId, handlerId));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>Two elements sharing one grain are refused rather than one of them being dropped.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Separate from <see cref="AnIncoherentElementIsRefused"/> because it takes two elements, and
    /// separate from the idempotency case because the duplicate is <i>within</i> one flush. Both readings
    /// of the ambiguity are wrong: writing one of the two would pick arbitrarily between two different
    /// descriptions of the same attempt, and letting both through would meet the filtered unique index as
    /// engine error 2601, which names no element.
    /// </remarks>
    [IntegrationFact]
    public async Task TwoElementsSharingOneGrainAreRefused()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string handlerId = TestData.HandlerId(524008);

        try
        {
            await EnumerateAsync(scope.Context, loadRunId, handlerId);

            string payload = string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 [{WithKey("""{"attemptNumber":1,"startedDateUtc":"2026-09-06T02:00:00","outcome":"Succeeded","httpStatusCode":200}""", handlerId)}
                 ,{WithKey("""{"attemptNumber":1,"startedDateUtc":"2026-09-06T02:00:01","outcome":"Failed","httpStatusCode":500}""", handlerId)}]
                 """);

            SqlException error = await RawCall.ExpectFailureAsync(
                Procedure,
                p =>
                {
                    p.Add(RawCall.Int("LoadRunId", loadRunId));
                    p.Add(RawCall.Payload("Elements", payload));
                });

            Assert.True(SqlErrorNumbers.IsProcedureRefusal(error), error.Message);

            await using SqlConnection connection = await IntegrationServer.OpenAsync();

            Assert.Equal(0, await CountAsync(connection, loadRunId, handlerId));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>An attempt against a run that has been closed is refused.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// A closed run's counters have already been written by <c>logs.uspCompleteLoadRun</c>, so an attempt
    /// arriving afterwards would put a row in the attempt log that
    /// <c>logs.uspGetLoadRunSummary</c>'s aggregates disagree with — and the disagreement would look like
    /// a fault in the summary rather than a late write. This is also the one refusal in this class that a
    /// correct loader can trigger by accident: an unflushed buffer at shutdown, flushed after the run was
    /// closed.
    /// </remarks>
    [IntegrationFact]
    public async Task AnAttemptAgainstAClosedRunIsRefused()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        string handlerId = TestData.HandlerId(524009);

        await EnumerateAsync(scope.Context, loadRunId, handlerId);
        await Runs.CompleteAsync(scope.Context, loadRunId);

        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => scope.Context.RecordHandlerLoadAttemptSetAsync(
                loadRunId,
                [
                    new()
                    {
                        HandlerId = handlerId,
                        SourceType = TestData.SourceType,
                        Sequence = 1,
                        AttemptNumber = 1,
                        StartedDateUtc = Started,
                        Outcome = "Succeeded",
                        HttpStatusCode = 200,
                        RequestPath = Path(handlerId),
                    },
                ]));

        Assert.True(SqlErrorNumbers.IsProcedureRefusal(error), error.Message);

        // Not retryable, and that is the load-bearing half: a closed run will never re-open, so a caller
        // that read this as transient would spin until its retry budget ran out and then report a
        // database fault instead of the sequencing mistake it made.
        Assert.False(SqlErrorNumbers.IsRetryable(error));

        await using SqlConnection connection = await IntegrationServer.OpenAsync();

        Assert.Equal(0, await CountAsync(connection, loadRunId, handlerId));
    }

    /// <summary>Enumerates one handler version, so an attempt against it can resolve.</summary>
    private static Task<UpsertStatusResult> EnumerateAsync(
        RCRAInfoContext context, int loadRunId, string handlerId) =>
        context.UpsertHandlerLoadStatusSetAsync(
            loadRunId,
            "Enumerate",
            [
                new()
                {
                    HandlerId = handlerId,
                    ActivityLocation = TestData.ActivityLocation,
                    SourceType = TestData.SourceType,
                    Sequence = 1,
                },
            ]);

    /// <summary>The bare path the loader is meant to log for a detail fetch.</summary>
    private static string Path(string handlerId) =>
        $"/api/v1/hd/sources/{handlerId}/{TestData.SourceType}/1";

    /// <summary>Prepends the natural key to a theory element, which carries only what is under test.</summary>
    /// <remarks>
    /// The key is added here rather than written into each entry so that the theory data reads as the one
    /// thing each case is about. It goes at the front, where JSON property order is irrelevant to
    /// <c>OPENJSON</c> but a reader of a failure message sees the key first.
    /// </remarks>
    private static string WithKey(string element, string handlerId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              {"handlerId":"{{handlerId}}","sourceType":"{{TestData.SourceType}}","sequence":1,{{element[1..]}}
              """);

    /// <summary>How many attempt rows this run holds for one handler.</summary>
    private static async Task<int> CountAsync(
        SqlConnection connection, int loadRunId, string handlerId)
    {
        int? count = await IntegrationServer.ScalarAsync<int>(
            connection, $"SELECT COUNT (*) {From(loadRunId, handlerId)};");

        Assert.NotNull(count);
        return count.Value;
    }

    /// <summary>One text column of one attempt row.</summary>
    private static Task<string?> ColumnAsync(
        SqlConnection connection, int loadRunId, string handlerId, int attemptNumber, string column)
    {
        AssertIsAKnownColumn(column);

        return IntegrationServer.TextAsync(
            connection,
            $"SELECT a.{column} {From(loadRunId, handlerId)} AND a.AttemptNumber = {attemptNumber};");
    }

    /// <summary>One numeric column of one attempt row, null included.</summary>
    private static Task<int?> NumberAsync(
        SqlConnection connection, int loadRunId, string handlerId, int attemptNumber, string column)
    {
        AssertIsAKnownColumn(column);

        return IntegrationServer.ScalarAsync<int>(
            connection,
            $"SELECT a.{column} {From(loadRunId, handlerId)} AND a.AttemptNumber = {attemptNumber};");
    }

    /// <summary>
    /// The join every assertion above reads through: an attempt row, found the way its caller names it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the table rather than through a procedure, because no procedure projects an attempt row.
    /// <c>logs.uspGetLoadRunSummary</c> aggregates this table and nothing exposes the columns
    /// individually — which is right for the monitoring app and leaves this suite reading the catalog
    /// directly, as Windows authentication rather than as either application login.
    /// </para>
    /// <para>
    /// <paramref name="loadRunId"/> is an <see cref="int"/> and <paramref name="handlerId"/> is asserted
    /// to be one of this suite's reserved identifiers before it is interpolated — the same argument
    /// <c>Runs.NextSequenceAsync</c> makes. The assertion is what makes the interpolation defensible, so
    /// it stays ahead of the string.
    /// </para>
    /// </remarks>
    private static string From(int loadRunId, string handlerId)
    {
        Assert.StartsWith(TestData.HandlerIdPrefix, handlerId, StringComparison.Ordinal);
        Assert.Equal(12, handlerId.Length);
        Assert.All(handlerId[TestData.HandlerIdPrefix.Length..], c => Assert.True(char.IsAsciiDigit(c)));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             FROM logs.HandlerLoadAttempt AS a
             JOIN logs.HandlerLoadStatus  AS s ON s.HandlerLoadStatusId = a.HandlerLoadStatusId
             WHERE a.LoadRunId = {loadRunId}
               AND s.HandlerId = N'{handlerId}'
             """);
    }

    /// <summary>
    /// The column names these helpers may interpolate, as a whitelist.
    /// </summary>
    /// <remarks>
    /// A whitelist rather than an escape, for the reason the project has settled on everywhere a
    /// caller-supplied name reaches a statement: <c>@SortBy</c> is validated against a fixed list before
    /// interpolation and there is no third option. Every caller here passes a literal, so this can only
    /// ever fail during editing — which is exactly when it is useful.
    /// </remarks>
    private static void AssertIsAKnownColumn(string column) =>
        Assert.Contains(column, ReadableColumns, StringComparer.Ordinal);

    private static readonly string[] ReadableColumns =
    [
        "Outcome", "HttpStatusCode", "RequestPath", "DurationMs", "ResponseBytes",
        "RetryAfterSeconds", "ApiErrorCode", "ApiErrorMessage", "ApiErrorId", "FailureMessage",
    ];
}
