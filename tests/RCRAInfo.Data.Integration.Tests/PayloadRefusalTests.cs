using Microsoft.Data.SqlClient;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The six payload procedures, called with JSON they must refuse.
/// </summary>
/// <remarks>
/// <para>
/// G32 chose a JSON string over a table-valued parameter, and this class exists because of what that
/// choice costs. With a TVP a shape mismatch is a driver error before the call leaves the process: the
/// column count is wrong, or a type will not convert, and it fails loudly. With JSON there is no shape.
/// <c>OPENJSON</c> matches property names <b>case-sensitively regardless of collation</b>, and a path
/// that matches nothing yields <c>NULL</c> rather than an error — so the characteristic failure of the
/// JSON design is a call that succeeds, merges nothing or merges nulls, and reports success.
/// </para>
/// <para>
/// So each procedure validates its payload itself, before <c>BEGIN TRANSACTION</c>, and each of those
/// gates is checked here. They are checked through <see cref="RawCall"/> rather than through
/// <c>RCRAInfoContext</c>: the data layer's <c>PayloadJson.AssertJsonArray</c> rejects malformed JSON in
/// C# first, which is right, and which also means the SQL gate is unreachable from the ordinary path
/// and would be untested by anything that only used it.
/// </para>
/// <para>
/// <b>What this class found on 2026-09-05.</b> Four of the five gates were
/// <c>ISJSON (@Elements) = 0</c> where script 400 has always had <c>ISJSON (@Payload, ARRAY) = 0</c>.
/// A caller who sent one element as a bare object rather than a one-element array passed the plain gate,
/// <c>OPENJSON</c> then enumerated the object's <i>properties</i>, and what came back was error 245,
/// "Conversion failed when converting the nvarchar value 'handlerId' to data type int" — from an
/// internal <c>CAST</c>, naming a column the caller never sent, and not classified as a refusal by
/// anything branching on the number. It was also luck that it errored at all: the 245 depends on the
/// procedure casting the <c>OPENJSON</c> key, and a payload procedure whose columns were all strings
/// would have shredded the object to zero rows and reported success. Scripts 520 through 523 now carry
/// the <c>ARRAY</c> constraint and <see cref="ABareObjectIsRefusedRatherThanShreddedToNothing"/> is
/// the test that keeps them carrying it.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class PayloadRefusalTests
{
    /// <summary>The six procedures that take a JSON payload, by name.</summary>
    /// <remarks>
    /// Hand-written and asserted complete by
    /// <see cref="EveryProcedureWithAPayloadParameterIsCovered"/>, which derives the real list from
    /// <c>sys.parameters</c>. A list of this kind drifts in exactly one direction — another payload
    /// procedure is added and nobody remembers this file — and that is the direction the assertion
    /// covers — <c>logs.uspRecordHandlerLoadAttemptSet</c>, added on 2026-09-06, is the sixth.
    /// </remarks>
    public static TheoryData<string> PayloadProcedures => Data(PayloadProcedureNames);

    /// <summary>
    /// The names behind <see cref="PayloadProcedures"/>, as a plain array.
    /// </summary>
    /// <remarks>
    /// The list lives here rather than in the <see cref="TheoryData{T}"/> because
    /// <see cref="EveryProcedureWithAPayloadParameterIsCovered"/> has to compare it as a set of strings,
    /// and a <c>TheoryData</c> is a sequence of boxed argument rows — projecting the names back out of one
    /// is both unreadable and, on xunit 2.9.3, ambiguous enough not to compile.
    /// </remarks>
    private static readonly string[] PayloadProcedureNames =
    [
        "dbo.uspMergeHandlerSourceBatch",
        "logs.uspUpsertHandlerLoadStatusSet",
        "dbo.uspReconcileCurrentRecord",
        "dbo.uspSoftDeleteHandlerSourceSet",
        "dbo.uspRefreshLookupSet",
        "logs.uspRecordHandlerLoadAttemptSet",
    ];

    /// <summary>Text that is not JSON at all is refused by every one of the six.</summary>
    /// <param name="procedure">The procedure under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The cheap half of the pair, and it was already passing. It stays because it is the case that
    /// proves the gate is reached at all: if the <c>ARRAY</c> variant below were the only test, a gate
    /// deleted outright would fail one test rather than two and the diagnosis would be narrower.
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(PayloadProcedures))]
    public async Task TextThatIsNotJsonIsRefused(string procedure)
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            SqlException error = await RawCall.ExpectFailureAsync(
                procedure, p => Bind(p, procedure, loadRunId, "this is not JSON"));

            AssertIsAnExplicitRefusal(error, procedure);
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>A single element sent as a bare object, not a one-element array, is refused.</summary>
    /// <param name="procedure">The procedure under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The regression test for the 2026-09-05 finding described on the class. The payload below is valid
    /// JSON and carries every property the procedure names, which is what made the old gate pass it: a
    /// plain <c>ISJSON</c> asks whether the text parses, not whether it is the array the procedure is
    /// about to enumerate.
    /// </para>
    /// <para>
    /// It is also the mistake a caller is most likely to make, which is why it deserves the procedure's
    /// own message rather than an engine error. Every one of these procedures is set-based by MDE's
    /// requirement — a set of one and a set of many are the same call — and "a set of one" invites
    /// exactly the object a JSON serialiser produces for a single item.
    /// </para>
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(PayloadProcedures))]
    public async Task ABareObjectIsRefusedRatherThanShreddedToNothing(string procedure)
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        // Every property any of the six requires, in one object, so the refusal cannot be about a missing
        // field. Only the brackets are absent. 'Cancelled' is script 524's one outcome that needs no
        // accompanying detail, which keeps this object valid for that procedure too.
        const string BareObject =
            """
            {"handlerId":"ZZTEST000001","activityLocation":"ZZ","sourceType":"Z","sequence":1,
             "currentRecord":true,"code":"ZZ","description":"A bare object, not an array.",
             "attemptNumber":1,"startedDateUtc":"2026-01-02T03:04:05.0000000Z","outcome":"Cancelled",
             "retrievedDateUtc":"2026-01-02T03:04:05.0000000Z","handler":{"handlerId":"ZZTEST000001"}}
            """;

        try
        {
            SqlException error = await RawCall.ExpectFailureAsync(
                procedure, p => Bind(p, procedure, loadRunId, BareObject));

            AssertIsAnExplicitRefusal(error, procedure);

            Assert.Contains(
                "array",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>A NULL payload is refused rather than treated as an empty batch.</summary>
    /// <param name="procedure">The procedure under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Null and <c>[]</c> are different, deliberately: an empty array is a caller saying "nothing to do",
    /// which is permitted and does nothing, while a null is a caller that meant to send something and
    /// sent no value at all. Reading the second as the first is how a load reports success having
    /// merged nothing.
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(PayloadProcedures))]
    public async Task ANullPayloadIsRefused(string procedure)
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            SqlException error = await RawCall.ExpectFailureAsync(
                procedure, p => Bind(p, procedure, loadRunId, null));

            AssertIsAnExplicitRefusal(error, procedure);
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>An empty array is accepted and does nothing.</summary>
    /// <param name="procedure">The procedure under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The other side of the three refusals above, and the reason they can be as strict as they are. If
    /// <c>[]</c> also threw, a loader with a genuinely empty page would have to distinguish "nothing to
    /// send" from "a bad payload" by parsing an error message, and the pressure would be to relax the
    /// refusals rather than to special-case the empty page.
    /// </para>
    /// <para>
    /// <c>dbo.uspRefreshLookupSet</c> is excluded, and its exclusion is the interesting part: in
    /// <c>Full</c> mode an empty list means every live code in the list is absent from the payload and
    /// must therefore be retired, so for that procedure an empty array is not "nothing to do" — it is
    /// the most destructive call it accepts. It is exercised in <c>Upsert</c> mode here, where the
    /// no-op reading is the correct one.
    /// </para>
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(PayloadProcedures))]
    public async Task AnEmptyArrayIsAcceptedAndDoesNothing(string procedure)
    {
        await using IntegrationScope scope = IntegrationServer.Connect();
        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            await RawCall.ExecuteAsync(procedure, p => Bind(p, procedure, loadRunId, "[]"));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>
    /// The four logging procedures, whose <c>NVARCHAR (MAX)</c> parameters are log text and not payloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Excepted <b>by name</b>, one entry each, rather than by a pattern over the schema or the
    /// parameter name. A pattern would have to be something like "in <c>logs</c>" or "named
    /// <c>@KeyParameters</c>", and either one would silently absorb a genuine payload procedure added to
    /// that schema or given that parameter name later — which is the same shape of mistake as the
    /// coverage gap this exception list exists inside.
    /// </para>
    /// <para>
    /// They are the same four the instrumentation rule excepts, for a related reason: these are the
    /// substrate the other seventeen procedures log <i>through</i>. Their wide parameters carry
    /// <c>@KeyParameters</c>, <c>@ErrorMessage</c>, <c>@DynamicSql</c> and <c>@ContextMessage</c>, which
    /// are free text with a privacy obligation rather than JSON with a shape — checked by
    /// <c>build/check_execution_log_privacy.py</c> and by
    /// <see cref="ExecutionLogTests.NoKeyParametersValueCarriesACredentialOrAPayload"/>, not here.
    /// </para>
    /// </remarks>
    private static readonly string[] LoggingProcedureNames =
    [
        "logs.uspStartExecutionLogging",
        "logs.uspStartExecutionLoggingInsert",
        "logs.uspRecordExecutionError",
        "logs.uspRecordExecutionErrorUpdate",
    ];

    /// <summary>Every procedure that takes a payload parameter appears in this class's list.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Derived from <c>sys.parameters</c>, and asserted in <b>both</b> directions. A procedure with an
    /// <c>NVARCHAR (MAX)</c> parameter is either a payload procedure, in which case the four theories
    /// above must cover it, or one of the four logging procedures. There is no third case, and a seventh
    /// payload procedure added without touching this file would otherwise be a silent gap rather than a
    /// failure.
    /// </para>
    /// <para>
    /// Both directions, because either one alone is defeated by the obvious mistake. Comparing only the
    /// database against the lists misses a procedure renamed out of existence — the list would name
    /// something that no longer exists and the theories would be testing five procedures while claiming
    /// six. Comparing only the lists against the database misses the addition. The set comparison
    /// catches both and names which side the difference is on.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task EveryProcedureWithAPayloadParameterIsCovered()
    {
        await using SqlConnection connection = await IntegrationServer.OpenAsync();

        string? aggregated = await IntegrationServer.TextAsync(
            connection,
            """
            SELECT STRING_AGG (n.Name, N',')
              FROM (SELECT DISTINCT CONCAT (s.name, N'.', o.name) AS Name
                      FROM sys.parameters AS p
                      JOIN sys.procedures AS o ON o.object_id = p.object_id
                      JOIN sys.schemas    AS s ON s.schema_id = o.schema_id
                     WHERE p.max_length = -1
                       AND TYPE_NAME (p.user_type_id) = N'nvarchar') AS n;
            """);

        Assert.False(
            string.IsNullOrWhiteSpace(aggregated),
            "No procedure in the database has an NVARCHAR (MAX) parameter. Either the deployment has " +
            "not been applied or this query has stopped describing the payload design, and both make " +
            "every theory in this class examine nothing.");

        HashSet<string> found = [.. aggregated.Split(',', StringSplitOptions.TrimEntries)];

        HashSet<string> expected = [.. PayloadProcedureNames, .. LoggingProcedureNames];

        Assert.Equal(expected.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
    }

    /// <summary>Wraps a list of names as theory data, one test per name.</summary>
    private static TheoryData<string> Data(IEnumerable<string> names)
    {
        TheoryData<string> data = [];

        foreach (string name in names)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// A refusal is 50000, is not retryable, and says something a caller can act on.
    /// </summary>
    /// <remarks>
    /// The number is the load-bearing assertion. <c>THROW 50000</c> is what the loader branches on to
    /// tell "you sent something wrong, do not retry" from 1205 or 1222, which mean "try again". Error
    /// 245 — which is what four of these five produced before 2026-09-05 — satisfies neither reading:
    /// it is not a refusal and it is not retryable, so a caller that classified it either way would be
    /// wrong.
    /// </remarks>
    private static void AssertIsAnExplicitRefusal(SqlException error, string procedure)
    {
        Assert.True(
            SqlErrorNumbers.IsProcedureRefusal(error),
            $"{procedure} failed with error {error.Number} rather than its own refusal " +
            $"({SqlErrorNumbers.ProcedureRefusal}). An engine error here means the payload got past the " +
            $"validation gate and broke something downstream, which is the case script 520's history " +
            $"entry of 2026-09-05 records: '{error.Message}'");

        Assert.False(SqlErrorNumbers.IsRetryable(error));

        // Not a length check for its own sake: THROW 50000 with an empty message is legal and would
        // satisfy every assertion above while telling the caller nothing.
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    /// <summary>Binds the payload plus whatever else the procedure requires.</summary>
    /// <remarks>
    /// The non-payload arguments are all deliberately <b>valid</b>. Each procedure validates its payload
    /// before its other parameters or after, and a test that sent a bad mode alongside bad JSON would
    /// pass on whichever refusal happened to come first — including on a procedure whose payload gate
    /// had been removed.
    /// </remarks>
    private static void Bind(
        Microsoft.Data.SqlClient.SqlParameterCollection parameters,
        string procedure,
        int loadRunId,
        string? payload)
    {
        switch (procedure)
        {
            case "dbo.uspMergeHandlerSourceBatch":
                parameters.Add(RawCall.Payload("Payload", payload));
                parameters.Add(RawCall.Int("LoadRunId", loadRunId));
                break;

            case "logs.uspUpsertHandlerLoadStatusSet":
                parameters.Add(RawCall.Int("LoadRunId", loadRunId));
                parameters.Add(RawCall.Text("Mode", "Enumerate", 20));
                parameters.Add(RawCall.Payload("Elements", payload));
                break;

            case "dbo.uspReconcileCurrentRecord":
                parameters.Add(RawCall.Int("LoadRunId", loadRunId));
                parameters.Add(RawCall.Payload("Summaries", payload));
                break;

            case "dbo.uspSoftDeleteHandlerSourceSet":
                parameters.Add(RawCall.Int("LoadRunId", loadRunId));
                parameters.Add(RawCall.Payload("Elements", payload));
                parameters.Add(RawCall.Text("Reason", TestData.SoftDeleteReason, 200));
                break;

            case "dbo.uspRefreshLookupSet":
                parameters.Add(RawCall.Int("LoadRunId", loadRunId));
                parameters.Add(RawCall.Text("LookupName", "ContactType", 50));

                // Upsert, never Full. See AnEmptyArrayIsAcceptedAndDoesNothing: Full mode reads an empty
                // payload as "retire every live code", so a test that sent [] in Full mode would empty a
                // mirrored EPA code list to prove a point about JSON.
                parameters.Add(RawCall.Text("Mode", "Upsert", 20));
                parameters.Add(RawCall.Payload("Elements", payload));
                break;

            case "logs.uspRecordHandlerLoadAttemptSet":
                parameters.Add(RawCall.Int("LoadRunId", loadRunId));
                parameters.Add(RawCall.Payload("Elements", payload));
                break;

            default:
                Assert.Fail(
                    $"{procedure} is in PayloadProcedures with no parameter binding. Add one here " +
                    "rather than loosening the list — an unbound procedure would fail on a missing " +
                    "parameter (error 201) and the refusal assertions would never be reached.");
                break;
        }
    }
}
