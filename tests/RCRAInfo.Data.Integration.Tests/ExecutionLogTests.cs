using System.Globalization;

using Microsoft.Data.SqlClient;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The instrumentation itself: what every procedure writes to <c>logs.ExecutionLog</c>, and what it
/// must never write there.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is measured rather than read.</b> MDE's requirement is that every procedure logs its own
/// execution, and the obvious way to check that is to look at the source for the start call and the
/// completion <c>UPDATE</c>. <c>.claude/hooks/validate-sql.py</c> already does exactly that, and it is
/// worth having — but it is a check on text. It cannot tell a start call that runs from one sitting
/// behind an <c>IF</c> that is never true, and it only ever sees a file as it is written, so a procedure
/// deployed from a script the hook did not validate escapes it entirely. So every assertion in this class
/// counts rows in <c>logs.ExecutionLog</c> before and after a real call. If the row is not there, the
/// instrumentation is not there, whatever the source says.
/// </para>
/// <para>
/// <b>Reads and writes are instrumented differently, deliberately.</b> DA1's review settled it: a read
/// may skip the start row and the completion <c>UPDATE</c>, because a paged grid refreshed on a timer
/// would otherwise write two rows per second forever and the log would cost more than the data. It may
/// <b>not</b> skip the <c>CATCH</c>. That asymmetry is the reason
/// <see cref="TheOptedOutReadsLogNothingOnASuccessfulCall"/> exists as an assertion in its own right
/// rather than as an absence: measured on 2026-09-05, an opted-out read writes <b>no</b> row on success
/// and <b>one</b> on failure. If a read ever started logging its successes the cost decision would have
/// been reversed by accident, and this class is where that shows up.
/// </para>
/// <para>
/// <b>There are nine procedures that log on success, not eight.</b> <c>logs.uspGetLoadRunPage</c> is a
/// read and is instrumented on both paths, and that is the one exception this class had to be corrected
/// to accept: script 500's header says so at length, and calls it DA1's witness that a read shape
/// survives the template at all — a <c>SELECT</c> returning a result set to the caller, from inside
/// <c>TRY</c>/<c>CATCH</c>, without the wrapping breaking the shape EF Core materialises. So the split
/// asserted here is not reads against writes; it is <see cref="ProceduresThatLogOnSuccess"/> against
/// <see cref="ProceduresThatLogOnlyOnFailure"/>, with the witness on the first list and the reason for
/// it recorded on its case.
/// </para>
/// <para>
/// The failing-path half of the suite exists because of the specific mistake MDE described: a procedure
/// containing only a <c>SELECT</c>, with a user-defined function called inside it, where the function
/// errored and nothing recorded it. There was no <c>INSERT</c> or <c>UPDATE</c> in that procedure, so it
/// looked like it had nothing to log. <see cref="EveryProcedureLogsItsFailure"/> is that case, for all
/// eighteen.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class ExecutionLogTests
{
    /// <summary>The handler this class writes under, distinct from every other class's.</summary>
    /// <remarks>
    /// Ordinals 1 through 4 belong to <c>MergeRoundTripTests</c> and 5 to <c>ReadProcedureTests</c>.
    /// Sharing one would make a failure here depend on whether that class had run first, and these
    /// classes are in one collection precisely so that they do not interfere.
    /// </remarks>
    private const int LogOrdinal = 6;

    /// <summary>The one version this class merges, retired before each merge so the merge is a change.</summary>
    private const int LogSequence = 1;

    /// <summary>
    /// A reserved feed for the watermark procedures, which refuse a feed they were not seeded with.
    /// </summary>
    /// <remarks>
    /// <c>config.LoadWatermark</c> holds exactly one real row — <c>HandlerSource</c> / <c>MD</c>, seeded
    /// by script 340 — and <c>config.uspSetLoadWatermark</c> deliberately will not create a second: an
    /// unknown feed is a typo, not a new feed. So this class seeds its own, once, and moves that one
    /// instead. Moving the real row would leave the next genuine incremental load starting from a date a
    /// test chose.
    /// </remarks>
    private const string ReservedFeed = "ZZTestFeed";

    /// <summary>Text that is not JSON, used as the failure trigger for the five payload procedures.</summary>
    private const string NotJson = "this is not JSON";

    /// <summary>
    /// The five procedures the instrumentation rule excepts, named individually.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four of them are the substrate: <c>logs.uspStartExecutionLogging</c> and its <c>Insert</c> half
    /// write the start row, <c>logs.uspRecordExecutionError</c> and its <c>Update</c> half write the
    /// failure. A procedure that logged through itself would recurse, and the outer
    /// <c>BEGIN CATCH … END CATCH</c> in MDE's template swallows everything for the same reason — from
    /// its own comment, "the priority is to not interrupt the calling CATCH block".
    /// </para>
    /// <para>
    /// The fifth, <c>util.uspSetObjectDescription</c>, is the one the plan's wording misses. It says "the
    /// four <c>logs</c> procedures excepted"; there are five exemptions in
    /// <c>.claude/hooks/validate-sql.py</c>, and this is the extra. It runs only from DDL scripts a
    /// developer executes by hand, before <c>logs.ExecutionLog</c> necessarily exists in a fresh
    /// database, so it cannot log through the substrate that the substrate's own descriptions are set by.
    /// Named here rather than matched by a pattern, so that a sixth exemption has to be argued for in a
    /// diff.
    /// </para>
    /// </remarks>
    private static readonly string[] Exempt =
    [
        "logs.uspStartExecutionLogging",
        "logs.uspStartExecutionLoggingInsert",
        "logs.uspRecordExecutionError",
        "logs.uspRecordExecutionErrorUpdate",
        "util.uspSetObjectDescription",
    ];

    /// <summary>
    /// <c>NOT IN (@E1, …)</c>, one placeholder per entry in <see cref="Exempt"/>.
    /// </summary>
    /// <remarks>
    /// Composed from the array's <i>indices</i>, never from its values, so the only thing that reaches
    /// the statement text is <c>@E</c> and a number. The names themselves are bound as parameters. It is
    /// derived rather than written out so that adding an exemption cannot leave the list and the
    /// predicate disagreeing — which would silently examine one procedure fewer than the count this
    /// class asserts.
    /// </remarks>
    private static readonly string ExemptionPredicate =
        "NOT IN (" + string.Join(", ", Exempt.Select((_, i) => $"@E{i + 1}")) + ")";

    private static readonly Dictionary<string, Instrumented> Cases = Build();

    /// <summary>
    /// The ten that log on success as well as on failure: the nine writes, plus the one instrumented
    /// read.
    /// </summary>
    public static TheoryData<string> ProceduresThatLogOnSuccess => Named(logsOnSuccess: true);

    /// <summary>The eight reads that have opted out of success logging and log on failure alone.</summary>
    public static TheoryData<string> ProceduresThatLogOnlyOnFailure => Named(logsOnSuccess: false);

    /// <summary>All eighteen instrumented procedures.</summary>
    public static TheoryData<string> AllProcedures
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string procedure in Cases.Keys.Order(StringComparer.Ordinal))
            {
                data.Add(procedure);
            }

            return data;
        }
    }

    /// <summary>Each of the nine leaves exactly one successful log row behind.</summary>
    /// <param name="procedure">The procedure under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Exactly one, not at least one. Two would mean the start row was written and then a second
    /// inserted instead of the first being updated — which is what the <c>ReCreatedAfterRollback</c>
    /// branch does after a rollback destroys the original, and on a successful call there is nothing to
    /// re-create. A duplicated success row is not harmless: <c>logs.uspGetLoadRunSummary</c> and the
    /// monitoring grid count these.
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(ProceduresThatLogOnSuccess))]
    public async Task EveryProcedureThatLogsOnSuccessLeavesExactlyOneRow(string procedure)
    {
        Instrumented under = Case(procedure);

        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            // After the run is opened, so that logs.uspStartLoadRun's own row is outside the window.
            int before = await CountAsync(probe, procedure);

            await under.Succeed(scope, loadRunId);

            Assert.Equal(before + 1, await CountAsync(probe, procedure));

            LogRow? row = await LatestAsync(probe, procedure);

            Assert.NotNull(row);
            Assert.True(
                row.Successful,
                $"{procedure} succeeded but its log row says otherwise (ErrorNumber " +
                $"{row.ErrorNumber?.ToString(CultureInfo.InvariantCulture) ?? "null"}). " +
                "A start row that is never updated reads as a failure forever, and the monitoring grid " +
                "would show a load that worked as one that did not.");
            Assert.Null(row.ErrorNumber);
            Assert.False(row.ReCreatedAfterRollback);
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>An opted-out read that succeeds writes no log row at all.</summary>
    /// <param name="procedure">The procedure under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The one assertion in this class that would be satisfied by a procedure with no instrumentation
    /// whatsoever, which is why it is never the only thing asserted about a read:
    /// <see cref="EveryProcedureLogsItsFailure"/> covers the same seven and requires a row. Together they
    /// pin the design rather than one half of it — an opted-out read logs nothing when it works and one
    /// row when it does not.
    /// </para>
    /// <para>
    /// It is worth asserting the zero because the pressure runs the other way. Adding a start call to a
    /// read is a one-line change that looks like an improvement, and nothing else in the build would
    /// notice; what it would produce is a monitoring web app whose own grid refresh is the largest
    /// writer in the database. This test failed on its first run for exactly that shape of reason,
    /// against <c>logs.uspGetLoadRunPage</c> — where the extra row turned out to be the deliberate
    /// witness rather than a regression, so the case moved lists and the reason went with it.
    /// </para>
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(ProceduresThatLogOnlyOnFailure))]
    public async Task TheOptedOutReadsLogNothingOnASuccessfulCall(string procedure)
    {
        Instrumented under = Case(procedure);

        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            int before = await CountAsync(probe, procedure);

            await under.Succeed(scope, loadRunId);

            Assert.Equal(before, await CountAsync(probe, procedure));
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>Every procedure records its failure, with the number the caller was given.</summary>
    /// <param name="procedure">The procedure under test.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// This is MDE's original complaint, generalised: a procedure that appears to have nothing to log
    /// still has to log the thing that went wrong. All eighteen, reads included.
    /// </para>
    /// <para>
    /// The log row's <c>ErrorNumber</c> is compared against the number the client actually caught, not
    /// against a literal. Comparing both to <c>50000</c> would pass on a <c>CATCH</c> that recorded one
    /// number and re-raised another, and that divergence is the failure worth catching: the operator
    /// reading the log and the caller branching on the number would be looking at two different
    /// incidents.
    /// </para>
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(AllProcedures))]
    public async Task EveryProcedureLogsItsFailure(string procedure)
    {
        Instrumented under = Case(procedure);

        await using IntegrationScope scope = IntegrationServer.Connect();
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            int before = await CountAsync(probe, procedure);

            SqlException error = await RawCall.ExpectFailureAsync(
                procedure, p => under.Fail(p, loadRunId));

            Assert.Equal(before + 1, await CountAsync(probe, procedure));

            LogRow? row = await LatestAsync(probe, procedure);

            Assert.NotNull(row);
            Assert.False(
                row.Successful,
                $"{procedure} threw error {error.Number} and then recorded the execution as " +
                "successful. A CATCH that records the row but leaves Successful = 1 is worse than one " +
                "that records nothing: the failure is invisible in the grid and in the summary counts.");

            Assert.Equal(error.Number, row.ErrorNumber);

            // Unquoted here, where ProcedureName is quoted. Not a defect -- ERROR_PROCEDURE() returns
            // the bare name and the template writes it through unchanged -- but it is the reason this
            // assertion cannot reuse the comparison CountAsync makes.
            Assert.Equal(procedure, row.ErrorProcedure);
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }

    /// <summary>
    /// An engine error reaches the caller with its own number, and is logged with that same number.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Every refusal the eighteen raise deliberately is <c>50000</c>, so <see cref="EveryProcedureLogsItsFailure"/>
    /// cannot tell a <c>CATCH</c> that re-raises the original error from one that re-raises <c>50000</c>
    /// for everything. That distinction is the whole reason the template ends in a bare <c>THROW;</c>
    /// rather than a <c>RAISERROR</c>: the loader retries on <c>1205</c> and <c>1222</c> and must not
    /// retry on <c>2627</c> or <c>547</c>, and a client cannot make that decision about <c>50000</c>.
    /// </para>
    /// <para>
    /// So the failure here is induced from outside the procedure. A second connection takes an exclusive
    /// table lock on <c>dbo.HandlerSource</c> and holds it; a third sets <c>LOCK_TIMEOUT</c> and calls
    /// the grid read, which blocks and gives up with <b>1222</b>. Deliberately a <i>read</i>, because a
    /// read has no start row: this proves its <c>CATCH</c> creates one from nothing on the failing path,
    /// which is the half of DA1's asymmetry that could quietly stop working.
    /// </para>
    /// <para>
    /// A <c>CommandTimeout</c> would have been easier and is not a substitute. Error <c>-2</c> is raised
    /// by the client after it stops waiting; the procedure never enters its <c>CATCH</c> and no row is
    /// written, so the test would be asserting something about the driver.
    /// </para>
    /// <para>
    /// The blocking transaction is opened in T-SQL text on a connection of its own, and it contains no
    /// procedure call. That is the distinction the eight <c>BannedSymbols.txt</c> entries are about: what
    /// is forbidden is a C#-owned transaction <i>around</i> a procedure that opens its own, because
    /// T-SQL has no nested rollback. Holding a lock on a connection that calls nothing is not that, and
    /// the rollback is in a <c>finally</c> so the lock cannot outlive the test.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task AnEngineErrorReachesTheCallerWithItsOwnNumberAndIsLoggedWithIt()
    {
        const string Grid = "dbo.uspGetHandlerSourcePage";

        await using SqlConnection probe = await IntegrationServer.OpenAsync();
        int before = await CountAsync(probe, Grid);

        await using SqlConnection blocker = await IntegrationServer.OpenAsync();

        await RunAsync(
            blocker,
            """
            BEGIN TRANSACTION;
            SELECT TOP (1) HandlerSourceId FROM dbo.HandlerSource WITH (TABLOCKX, HOLDLOCK);
            """);

        try
        {
            await using SqlConnection victim = await IntegrationServer.OpenAsync();

            // On the victim's own connection, and that is not incidental: session settings do not travel
            // with a pooled connection, so a SET issued anywhere else would not be in effect here.
            await RunAsync(victim, "SET LOCK_TIMEOUT 2000;");

            SqlException error = await RawCall.ExpectFailureAsync(victim, Grid, p =>
            {
                p.Add(RawCall.Int("Skip", 0));
                p.Add(RawCall.Int("Take", 1));
            });

            Assert.Equal(SqlErrorNumbers.LockRequestTimeout, error.Number);
            Assert.True(SqlErrorNumbers.IsRetryable(error));
            Assert.False(
                SqlErrorNumbers.IsProcedureRefusal(error),
                "The lock timeout came back as 50000, which means a CATCH replaced the original number. " +
                "The loader would then treat a transient block it should retry as a payload it must not " +
                "send again, and the retry strategy would never fire.");

            Assert.Equal(before + 1, await CountAsync(probe, Grid));

            LogRow? row = await LatestAsync(probe, Grid);

            Assert.NotNull(row);
            Assert.False(row.Successful);
            Assert.Equal(SqlErrorNumbers.LockRequestTimeout, row.ErrorNumber);

            // Exactly one row, and not a re-created one. A read writes no start row, so there is nothing
            // for a rollback to destroy and nothing for the re-creation branch to rebuild.
            Assert.False(row.ReCreatedAfterRollback);
        }
        finally
        {
            await RunAsync(blocker, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        }
    }

    /// <summary>
    /// Nothing this suite logged carries a credential, a query string, or a payload.
    /// </summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// From MDE's own template comment — "do NOT include parameters such as passwords and Personally
    /// Identifiable Information (PII)" — extended by AR8 to every free-text column on the row. The
    /// obligation is real rather than theoretical: <c>logs.ExecutionLog</c> is readable by the monitoring
    /// web app, and RCRAInfo's credentials travel in headers on requests whose paths these procedures
    /// are handed.
    /// </para>
    /// <para>
    /// <c>build/check_execution_log_privacy.py</c> checks the same thing against the scripts, and this is
    /// not a duplicate of it. That guardrail reads source, and it runs <i>before</i> the round-trip step
    /// in <c>build/guardrails.py</c>, so it never sees a single row this run wrote. What is asserted here
    /// is the corpus: whatever the procedures actually put in those columns when called.
    /// </para>
    /// <para>
    /// The three patterns are the plan's, and the <c>&amp;</c> is how a query string is recognised — a
    /// key-parameter list is <c>Name=value</c> pairs separated by commas, so an ampersand in one means a
    /// URL was concatenated in. The length limit is the same idea from the other end: nothing
    /// identifiers-and-counts-only reaches 4000 characters, so a value that does is a payload.
    /// </para>
    /// <para>
    /// A finding reports at most the first sixteen characters of the offending value, matching the rule
    /// the Python guardrail follows: a check that reproduced the secret it found would put the secret in
    /// the build log, which is the more widely read of the two places.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task NoKeyParametersValueCarriesACredentialOrAPayload()
    {
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        int? populated = await IntegrationServer.ScalarAsync<int>(
            probe,
            "SELECT COUNT (*) FROM logs.ExecutionLog WHERE KeyParameters IS NOT NULL;");

        Assert.True(
            populated > 0,
            "No row in logs.ExecutionLog has any KeyParameters at all, so this test examined nothing. " +
            "Either the instrumentation has stopped recording them or the suite has not run yet, and a " +
            "guardrail never shown to fail is indistinguishable from one examining nothing.");

        string? offenders = await IntegrationServer.TextAsync(
            probe,
            """
            SELECT STRING_AGG (o.Detail, N' | ')
              FROM (SELECT TOP (20)
                           CONCAT (l.ProcedureName, N' #', l.ExecutionLogId, N' -> ',
                                   CASE WHEN LEN (l.KeyParameters) > 4000
                                        THEN CONCAT (N'(', LEN (l.KeyParameters), N' characters)')
                                        ELSE LEFT (l.KeyParameters, 16)
                                   END) AS Detail
                      FROM logs.ExecutionLog AS l
                     WHERE l.KeyParameters IS NOT NULL
                       AND (l.KeyParameters LIKE N'%apiKey%'
                         OR l.KeyParameters LIKE N'%Bearer%'
                         OR l.KeyParameters LIKE N'%&%'
                         OR LEN (l.KeyParameters) > 4000)
                     ORDER BY l.ExecutionLogId DESC) AS o;
            """);

        Assert.True(
            offenders is null,
            "A KeyParameters value looks like a credential, a query string, or a payload. Only the " +
            $"first 16 characters of each are shown, deliberately: {offenders}");
    }

    /// <summary>No procedure's <c>CATCH</c> swallows its error, checked against the deployed object.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Three conditions, and each one is a way the rule has been broken in practice. A procedure with no
    /// <c>BEGIN TRY</c> cannot record anything. A <c>TRY</c>/<c>CATCH</c> that never reaches
    /// <c>uspRecordExecutionError</c> is the case the rule exists for — a <c>CATCH</c> holding only
    /// <c>;THROW;</c> looks handled and records nothing. And one that records but never re-throws
    /// swallows the error after writing it down, which is the failure MDE described: the caller carries
    /// on believing the work was done.
    /// </para>
    /// <para>
    /// <c>.claude/hooks/validate-sql.py</c> enforces the same three, and this is not redundant with it.
    /// The hook reads a file at the moment it is written, so it can only speak for scripts written
    /// through it; this reads <c>sys.sql_modules</c>, so it speaks for what is actually installed. A
    /// procedure deployed by hand from an unvalidated script is exactly the gap between them.
    /// </para>
    /// <para>
    /// The count of procedures examined is asserted against <see cref="Cases"/> rather than against a
    /// literal eighteen. A test that found no offenders because its <c>NOT IN</c> had excluded everything
    /// would otherwise pass loudest of all.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task NoProcedureCatchSwallowsItsError()
    {
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        int? examined = await ScalarWithExemptionsAsync(
            probe,
            $"""
             SELECT COUNT (*)
               FROM sys.procedures AS o
               JOIN sys.schemas    AS s ON s.schema_id = o.schema_id
              WHERE CONCAT (s.name, N'.', o.name) {ExemptionPredicate};
             """);

        Assert.Equal(Cases.Count, examined);

        string? offenders = await TextWithExemptionsAsync(
            probe,
            $"""
             SELECT STRING_AGG (o.Detail, N' | ')
               FROM (SELECT CONCAT (s.name, N'.', p.name, N' [',
                                    CASE WHEN m.definition NOT LIKE N'%BEGIN TRY%'
                                         THEN N'no TRY ' ELSE N'' END,
                                    CASE WHEN m.definition NOT LIKE N'%uspRecordExecutionError%'
                                         THEN N'records nothing ' ELSE N'' END,
                                    CASE WHEN m.definition NOT LIKE N'%THROW;%'
                                         THEN N'never re-throws' ELSE N'' END, N']') AS Detail
                       FROM sys.sql_modules AS m
                       JOIN sys.procedures  AS p ON p.object_id = m.object_id
                       JOIN sys.schemas     AS s ON s.schema_id = p.schema_id
                      WHERE CONCAT (s.name, N'.', p.name) {ExemptionPredicate}
                        AND (m.definition NOT LIKE N'%BEGIN TRY%'
                          OR m.definition NOT LIKE N'%uspRecordExecutionError%'
                          OR m.definition NOT LIKE N'%THROW;%')) AS o;
             """);

        Assert.True(
            offenders is null,
            $"A deployed procedure's error handling is incomplete: {offenders}. A bare THROW; is what " +
            "re-raises the original number; RAISERROR is not a substitute, because it replaces it with " +
            "50000 and the client branches on 1205 against 2627.");
    }

    /// <summary>The eighteen this class tests are exactly the eighteen the database has.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Both directions. A nineteenth procedure added without a case here would otherwise be a silent
    /// gap — instrumented or not, nothing would ask — and a case naming a procedure that has been
    /// renamed away would make every theory in this class report one fewer test than it claims.
    /// </remarks>
    [IntegrationFact]
    public async Task EveryInstrumentedProcedureIsCovered()
    {
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        string? aggregated = await TextWithExemptionsAsync(
            probe,
            $"""
             SELECT STRING_AGG (o.Name, N',')
               FROM (SELECT CONCAT (s.name, N'.', p.name) AS Name
                       FROM sys.procedures AS p
                       JOIN sys.schemas    AS s ON s.schema_id = p.schema_id
                      WHERE CONCAT (s.name, N'.', p.name) {ExemptionPredicate}) AS o;
             """);

        Assert.False(
            string.IsNullOrWhiteSpace(aggregated),
            "No non-exempt procedure exists in the database, which would make every theory in this " +
            "class examine nothing. Either the deployment has not been applied or the exemption list " +
            "has grown to cover everything.");

        HashSet<string> found = [.. aggregated.Split(',', StringSplitOptions.TrimEntries)];

        Assert.Equal(
            Cases.Keys.Order(StringComparer.Ordinal),
            found.Order(StringComparer.Ordinal));
    }

    /// <summary>There is no user-defined table type anywhere in this database.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// G32 chose a JSON string over a table-valued parameter, and this is the assertion that keeps the
    /// choice from being half-made. A TVP requires a user-defined table type; the type needs
    /// <c>EXECUTE</c> granted on it separately from the procedure, which is a permission the deployment
    /// scripts do not grant and the review would have to notice; and a mixed design would leave the two
    /// application logins able to call some set-based procedures and not others for a reason nothing
    /// records.
    /// </para>
    /// <para>
    /// Asserted against the database rather than against the script folder, because the folder is
    /// already searched for <c>CREATE TYPE</c> and a type could have arrived any other way. Zero, not
    /// "none of ours": there is no legitimate table type here, so any is a finding.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task TheDatabaseHasNoUserDefinedTableType()
    {
        await using SqlConnection probe = await IntegrationServer.OpenAsync();

        int? types = await IntegrationServer.ScalarAsync<int>(
            probe,
            "SELECT COUNT (*) FROM sys.table_types;");

        Assert.Equal(0, types);
    }

    private static Instrumented Case(string procedure)
    {
        Assert.True(
            Cases.TryGetValue(procedure, out Instrumented? under),
            $"{procedure} has no case in this class. EveryInstrumentedProcedureIsCovered should have " +
            "failed first; if it did not, the theory data and the dictionary have diverged.");

        return under!;
    }

    private static TheoryData<string> Named(bool logsOnSuccess)
    {
        TheoryData<string> data = [];

        foreach (Instrumented under in Cases.Values
            .Where(c => c.LogsOnSuccess == logsOnSuccess)
            .OrderBy(c => c.Procedure, StringComparer.Ordinal))
        {
            data.Add(under.Procedure);
        }

        return data;
    }

    /// <summary>
    /// How <c>logs.ExecutionLog.ProcedureName</c> spells a procedure: bracket-quoted, both parts.
    /// </summary>
    /// <remarks>
    /// The template derives it from <c>QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))</c>, so the stored value
    /// is <c>[logs].[uspStartLoadRun]</c> and a count filtered on the bare two-part name matches nothing
    /// — which would make every "exactly one row" assertion in this class pass as "exactly zero".
    /// </remarks>
    private static string Quoted(string procedure)
    {
        string[] parts = procedure.Split('.');

        Assert.Equal(2, parts.Length);

        return $"[{parts[0]}].[{parts[1]}]";
    }

    private static async Task<int> CountAsync(SqlConnection connection, string procedure)
    {
        using SqlCommand command = new(
            "SELECT COUNT (*) FROM logs.ExecutionLog WHERE ProcedureName = @ProcedureName;",
            connection);

        command.Parameters.Add(RawCall.Text("ProcedureName", Quoted(procedure), 300));

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<LogRow?> LatestAsync(SqlConnection connection, string procedure)
    {
        using SqlCommand command = new(
            """
            SELECT TOP (1) ExecutionLogId, Successful, ErrorNumber, ErrorProcedure, ReCreatedAfterRollback
              FROM logs.ExecutionLog
             WHERE ProcedureName = @ProcedureName
             ORDER BY ExecutionLogId DESC;
            """,
            connection);

        command.Parameters.Add(RawCall.Text("ProcedureName", Quoted(procedure), 300));

        await using SqlDataReader reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new LogRow(
            reader.GetInt64(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetBoolean(4));
    }

    /// <summary>Runs a literal statement that returns nothing.</summary>
    private static async Task RunAsync(SqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // Literal SQL only; there is no caller-supplied text on this path.
        using SqlCommand command = new(sql, connection);
#pragma warning restore CA2100

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> TextWithExemptionsAsync(SqlConnection connection, string sql)
    {
        object? value = await ScalarWithExemptionsAsync<object>(connection, sql);
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task<int?> ScalarWithExemptionsAsync(SqlConnection connection, string sql)
    {
        object? value = await ScalarWithExemptionsAsync<object>(connection, sql);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<T?> ScalarWithExemptionsAsync<T>(SqlConnection connection, string sql)
        where T : class
    {
        // The only interpolation in these statements is ExemptionPredicate, which is built from array
        // indices; the exempted names themselves arrive as the parameters bound below.
#pragma warning disable CA2100
        using SqlCommand command = new(sql, connection);
#pragma warning restore CA2100

        for (int i = 0; i < Exempt.Length; i++)
        {
            command.Parameters.Add(RawCall.Text($"E{i + 1}", Exempt[i], 300));
        }

        return (T?)await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// Seeds this class's reserved watermark feed, once. Idempotent, because nothing here can be undone.
    /// </summary>
    /// <remarks>
    /// Written to the table directly, for the reason <c>Runs.NextSequenceAsync</c> records: the procedure
    /// deliberately refuses to create a feed, so there is no procedure that can do this. Windows
    /// authentication, so it runs as the developer rather than as either application login — neither of
    /// which holds <c>INSERT</c> on anything.
    /// </remarks>
    private static async Task EnsureReservedWatermarkAsync(SqlConnection connection)
    {
        using SqlCommand command = new(
            """
            IF NOT EXISTS (SELECT 1
                             FROM config.LoadWatermark
                            WHERE FeedName         = @FeedName
                              AND ActivityLocation = @ActivityLocation)
                INSERT config.LoadWatermark (FeedName, ActivityLocation)
                VALUES (@FeedName, @ActivityLocation);
            """,
            connection);

        command.Parameters.Add(RawCall.Text("FeedName", ReservedFeed, 50));
        command.Parameters.Add(RawCall.Text("ActivityLocation", TestData.ActivityLocation, 2));

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The identifier of this class's live handler version, creating it if it is not there.</summary>
    /// <remarks>
    /// <c>dbo.uspGetHandlerSourceDetail</c> takes a surrogate key and there is no procedure that hands
    /// one out, so it has to be resolved. Retire-then-merge rather than merge alone, for the reason
    /// <c>Runs</c> gives at length: the merge reports nothing for a record that is already current, and a
    /// row this suite wrote on a previous run is already current.
    /// </remarks>
    private static async Task<int> ReservedHandlerSourceIdAsync(
        IntegrationScope scope,
        SqlConnection connection,
        int loadRunId)
    {
        string handlerId = TestData.HandlerId(LogOrdinal);

        await Runs.RetireAsync(scope.Context, loadRunId, handlerId, [LogSequence]);

        await scope.Context.MergeHandlerSourceBatchAsync(
            [TestData.Envelope(handlerId, LogSequence)], loadRunId);

        using SqlCommand command = new(
            """
            SELECT MAX (HandlerSourceId)
              FROM dbo.HandlerSource
             WHERE HandlerId  = @HandlerId
               AND SourceType = @SourceType
               AND IsDeleted  = 0;
            """,
            connection);

        command.Parameters.Add(RawCall.Text("HandlerId", handlerId, 12));
        command.Parameters.Add(RawCall.Text("SourceType", TestData.SourceType, 1));

        object? value = await command.ExecuteScalarAsync();

        Assert.False(
            value is null or DBNull,
            $"{handlerId} sequence {LogSequence} is not present after a merge that should have written " +
            "it. Nothing downstream in this class can run without it.");

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, Instrumented> Build()
    {
        List<Instrumented> cases =
        [
            // ---- reads: one row on failure, and no row on success except where noted -------------
            new("config.uspGetLoadWatermark", false,
                async (scope, run) =>
                {
                    await using SqlConnection seed = await IntegrationServer.OpenAsync();
                    await EnsureReservedWatermarkAsync(seed);

                    await RawCall.ExecuteAsync("config.uspGetLoadWatermark", p =>
                    {
                        p.Add(RawCall.Text("FeedName", ReservedFeed, 50));
                        p.Add(RawCall.Text("ActivityLocation", TestData.ActivityLocation, 2));
                    });
                },
                (p, run) =>
                {
                    // An unconfigured feed RAISES rather than returning no row -- deliberately, because a
                    // loader that read "no watermark" as "start from the beginning" would re-download
                    // everything.
                    p.Add(RawCall.Text("FeedName", "ZZNoSuchFeed", 50));
                    p.Add(RawCall.Text("ActivityLocation", TestData.ActivityLocation, 2));
                }),

            new("dbo.uspGetHandlerSourceDetail", false,
                async (scope, run) =>
                {
                    await using SqlConnection resolve = await IntegrationServer.OpenAsync();
                    int id = await ReservedHandlerSourceIdAsync(scope, resolve, run);

                    await RawCall.ExecuteAsync("dbo.uspGetHandlerSourceDetail", p =>
                        p.Add(RawCall.Int("HandlerSourceId", id)));
                },
                (p, run) => p.Add(RawCall.Int("HandlerSourceId", -1))),

            new("dbo.uspGetHandlerSourceHistoryPage", false,
                (scope, run) => RawCall.ExecuteAsync("dbo.uspGetHandlerSourceHistoryPage", p =>
                {
                    // 20, not 12. This procedure's @HandlerId is nvarchar(20) where the column and the
                    // other two procedures are 12 -- recorded rather than corrected, and bound at its own
                    // declared width so the test is not the thing that hides it.
                    p.Add(RawCall.Text("HandlerId", TestData.HandlerId(LogOrdinal), 20));
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Text("HandlerId", null, 20));
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                }),

            new("dbo.uspGetHandlerSourcePage", false,
                (scope, run) => RawCall.ExecuteAsync("dbo.uspGetHandlerSourcePage", p =>
                {
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                }),
                (p, run) =>
                {
                    // The four paged reads REFUSE an unrecognised @SortBy rather than falling back to a
                    // default, which is what makes an unsortable column a failure rather than a silently
                    // different page.
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                    p.Add(RawCall.Text("SortBy", "Nonsense", 50));
                }),

            new("dbo.uspSearchHandlerSource", false,
                (scope, run) => RawCall.ExecuteAsync("dbo.uspSearchHandlerSource", p =>
                {
                    p.Add(RawCall.Text("SearchTerm", TestData.HandlerIdPrefix, 200));
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Text("SearchTerm", string.Empty, 200));
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                }),

            // The eighteenth, and the census tests above are how it was noticed. Script 525 was written in
            // [R34] and deployed by hand in [R36]; the two lists in this class still said seventeen, so the
            // integration suite failed the moment the procedure actually existed in the database. That is
            // exactly the gap EveryInstrumentedProcedureIsCovered exists to close, and it worked -- the
            // stale side was the test, not the deployment. Its failing path is @MaxAgeHours = 0 rather than
            // an unrecognised @SortBy, because this read takes no @SortBy: it answers "what should the next
            // run redo", and the order of that answer is the procedure's business, not a caller's.
            new("logs.uspGetHandlerLoadResumeSet", false,
                (scope, run) => RawCall.ExecuteAsync("logs.uspGetHandlerLoadResumeSet", p =>
                    p.Add(RawCall.Text("ActivityLocation", TestData.ActivityLocation, 2))),
                (p, run) =>
                {
                    p.Add(RawCall.Text("ActivityLocation", TestData.ActivityLocation, 2));

                    // Below the floor of 1. Refused rather than clamped, because @MaxAgeHours is a
                    // correctness guard -- a zero would make every earlier success too old to trust and
                    // silently turn a resume into a full re-download.
                    p.Add(RawCall.Int("MaxAgeHours", 0));
                }),

            new("logs.uspGetHandlerLoadStatusPage", false,
                (scope, run) => RawCall.ExecuteAsync("logs.uspGetHandlerLoadStatusPage", p =>
                {
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                    p.Add(RawCall.Text("SortBy", "Nonsense", 50));
                }),

            // The exception, and the only read on the logs-on-success list. Script 500's header calls it
            // DA1's witness that a read shape survives the template -- a SELECT returning a result set to
            // the caller, from inside TRY/CATCH, without the wrapping breaking what EF Core materializes.
            // If MDE takes DA1's recommendation the other reads stay opted out and this one keeps its
            // instrumentation, so the true flag here is the decision and not an oversight. It is also
            // where the 2026-09-05 correction to that script bites: because this is the one paged read
            // that writes a successful-path row, its old CATCH could roll back the start row and then
            // never reach the re-creation block, so a failure erased its own log entry.
            new("logs.uspGetLoadRunPage", true,
                (scope, run) => RawCall.ExecuteAsync("logs.uspGetLoadRunPage", p =>
                {
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Int("Skip", 0));
                    p.Add(RawCall.Int("Take", 1));
                    p.Add(RawCall.Text("SortBy", "Nonsense", 50));
                }),

            new("logs.uspGetLoadRunSummary", false,
                (scope, run) => RawCall.ExecuteAsync("logs.uspGetLoadRunSummary", p =>
                    p.Add(RawCall.Int("LoadRunId", run))),

                // Negative, not int.MaxValue. LoadRunId is an IDENTITY starting at 1, so a value below it
                // can never identify a run and the procedure says so; a merely absent one is a different
                // refusal and this test is about the log row, not about which message came back.
                (p, run) => p.Add(RawCall.Int("LoadRunId", -1))),

            // ---- writes: one row on success, one row on failure ----------------------------------
            new("config.uspSetLoadWatermark", true,
                async (scope, run) =>
                {
                    await using SqlConnection seed = await IntegrationServer.OpenAsync();
                    await EnsureReservedWatermarkAsync(seed);

                    await RawCall.ExecuteAsync("config.uspSetLoadWatermark", p =>
                    {
                        p.Add(RawCall.Text("FeedName", ReservedFeed, 50));
                        p.Add(RawCall.Text("ActivityLocation", TestData.ActivityLocation, 2));

                        // Today, so the value never moves backwards on a later run: @AllowRewind defaults
                        // off and a rewind is refused, which would otherwise turn this into a test that
                        // passed once.
                        p.Add(RawCall.Date("WatermarkDate", DateOnly.FromDateTime(DateTime.UtcNow)));

                        // Attribution is mandatory: the procedure requires @LoadRunId or @Notes for any
                        // change to WatermarkDate, because a watermark moved by hand with no explanation
                        // is indistinguishable from one moved by mistake.
                        p.Add(RawCall.Int("LoadRunId", run));
                    });
                },
                (p, run) =>
                {
                    p.Add(RawCall.Text("FeedName", "ZZNoSuchFeed", 50));
                    p.Add(RawCall.Text("ActivityLocation", TestData.ActivityLocation, 2));
                    p.Add(RawCall.Date("WatermarkDate", DateOnly.FromDateTime(DateTime.UtcNow)));
                    p.Add(RawCall.Int("LoadRunId", run));
                }),

            new("dbo.uspMergeHandlerSourceBatch", true,
                async (scope, run) =>
                {
                    string handlerId = TestData.HandlerId(LogOrdinal);

                    await Runs.RetireAsync(scope.Context, run, handlerId, [LogSequence]);

                    await scope.Context.MergeHandlerSourceBatchAsync(
                        [TestData.Envelope(handlerId, LogSequence)], run);
                },
                (p, run) =>
                {
                    p.Add(RawCall.Payload("Payload", NotJson));
                    p.Add(RawCall.Int("LoadRunId", run));
                }),

            new("dbo.uspReconcileCurrentRecord", true,
                (scope, run) => RawCall.ExecuteAsync("dbo.uspReconcileCurrentRecord", p =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));

                    // An empty array, and that is enough for what is under test here. The instrumentation
                    // is the procedure's frame -- the start row is written before the payload is looked at
                    // and the completion UPDATE after the commit -- so a call that legitimately does
                    // nothing exercises the same frame. The payload semantics belong to the classes that
                    // assert on the data.
                    p.Add(RawCall.Payload("Summaries", "[]"));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Payload("Summaries", NotJson));
                }),

            new("dbo.uspRefreshLookupSet", true,
                (scope, run) => RawCall.ExecuteAsync("dbo.uspRefreshLookupSet", p =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Text("LookupName", "ContactType", 50));

                    // Upsert, never Full. In Full mode an empty payload means every live code in the list
                    // is absent and must be retired, so this one call in this one mode would empty a
                    // mirrored EPA code list to prove a point about a log row.
                    p.Add(RawCall.Text("Mode", "Upsert", 20));
                    p.Add(RawCall.Payload("Elements", "[]"));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Text("LookupName", "ContactType", 50));
                    p.Add(RawCall.Text("Mode", "Upsert", 20));
                    p.Add(RawCall.Payload("Elements", NotJson));
                }),

            new("dbo.uspSoftDeleteHandlerSourceSet", true,
                (scope, run) => RawCall.ExecuteAsync("dbo.uspSoftDeleteHandlerSourceSet", p =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Payload("Elements", "[]"));
                    p.Add(RawCall.Text("Reason", TestData.SoftDeleteReason, 200));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Payload("Elements", NotJson));
                    p.Add(RawCall.Text("Reason", TestData.SoftDeleteReason, 200));
                }),

            new("logs.uspCompleteLoadRun", true,
                // Closes the harness's own run. The finally that follows closes it again with the same
                // status, which the procedure treats as a retry and does not re-apply -- proved
                // separately in LoadRunTests.
                (scope, run) => Runs.CompleteAsync(scope.Context, run),

                (p, run) =>
                {
                    p.Add(RawCall.Int("LoadRunId", int.MaxValue));
                    p.Add(RawCall.Text("Status", "Succeeded", 20));
                }),

            new("logs.uspStartLoadRun", true,
                async (scope, run) =>
                {
                    // A second run, closed immediately. Its log row is written under this procedure's name
                    // and the closing row under uspCompleteLoadRun's, so the count filtered by name is
                    // still exactly one. It has to be closed: a stranded Running row cannot be deleted.
                    int extra = await Runs.StartAsync(scope.Context);
                    await Runs.CompleteAsync(scope.Context, extra);
                },
                (p, run) =>
                {
                    p.Add(RawCall.Text("RunMode", Runs.RunMode, 20));
                    p.Add(RawCall.Text("ActivityLocation", " ", 2));
                }),

            new("logs.uspUpsertHandlerLoadStatusSet", true,
                (scope, run) => RawCall.ExecuteAsync("logs.uspUpsertHandlerLoadStatusSet", p =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Text("Mode", "Enumerate", 20));
                    p.Add(RawCall.Payload("Elements", "[]"));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Text("Mode", "Enumerate", 20));
                    p.Add(RawCall.Payload("Elements", NotJson));
                }),

            new("logs.uspRecordHandlerLoadAttemptSet", true,
                (scope, run) => RawCall.ExecuteAsync("logs.uspRecordHandlerLoadAttemptSet", p =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));

                    // An empty array, for the reason dbo.uspReconcileCurrentRecord's entry gives: the
                    // instrumentation is the frame around the work, not the work. What this procedure does
                    // with real elements is AttemptLogTests's subject.
                    p.Add(RawCall.Payload("Elements", "[]"));
                }),
                (p, run) =>
                {
                    p.Add(RawCall.Int("LoadRunId", run));
                    p.Add(RawCall.Payload("Elements", NotJson));
                }),
        ];

        return cases.ToDictionary(c => c.Procedure, StringComparer.Ordinal);
    }

    /// <summary>One procedure, how to make it succeed, and how to make it fail.</summary>
    /// <param name="Procedure">The two-part name.</param>
    /// <param name="LogsOnSuccess">
    /// Whether a successful call writes a row. True for the nine writes and for
    /// <c>logs.uspGetLoadRunPage</c>; false for the other seven reads. DA1's cost decision, measured
    /// rather than assumed — and the flag is named for what it observes rather than for the kind of
    /// procedure, because those two turned out not to line up.
    /// </param>
    /// <param name="Succeed">
    /// Makes a real, successful call. Takes the scope as well as the run identifier because three of them
    /// cannot be made through <see cref="RawCall"/> alone: the merge needs a serialised payload, and the
    /// two load-run procedures need a run to open or close.
    /// </param>
    /// <param name="Fail">
    /// Binds parameters that will be refused. Every value other than the one under test is deliberately
    /// valid, so the refusal cannot be about something else — a call carrying two faults would pass on
    /// whichever gate happened to come first, including on a procedure whose real gate had been removed.
    /// </param>
    private sealed record Instrumented(
        string Procedure,
        bool LogsOnSuccess,
        Func<IntegrationScope, int, Task> Succeed,
        Action<SqlParameterCollection, int> Fail);

    /// <summary>The columns of a log row this class asserts on.</summary>
    private sealed record LogRow(
        long Id,
        bool Successful,
        int? ErrorNumber,
        string? ErrorProcedure,
        bool ReCreatedAfterRollback);
}
