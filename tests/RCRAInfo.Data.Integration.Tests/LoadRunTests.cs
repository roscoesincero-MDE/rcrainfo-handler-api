using Microsoft.Data.SqlClient;

using RCRAInfo.Data.Results;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The load-run lifecycle: opening one, closing it, and every refusal on the way.
/// </summary>
/// <remarks>
/// <para>
/// Almost everything in this class is a <b>refusal</b> test, and that is deliberate. A load run is the
/// record of what an unattended overnight process did, and every one of these refusals exists because
/// the alternative is a row that reads as a fact and is not one. A run closed twice with two different
/// statuses would make whichever call arrived last the truth; a run reported <c>Succeeded</c> with
/// failures counted against it may advance the watermark and close the gap behind records it never
/// loaded; a run left <c>Running</c> for ever is indistinguishable from one still going.
/// </para>
/// <para>
/// Each of these is tested by calling the procedure, because none of them is visible from the
/// signature: <c>CompleteLoadRunAsync</c> takes a status as a string and eleven counters as integers,
/// and the C# will happily pass any of them.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class LoadRunTests
{
    /// <summary>
    /// A second reserved activity location, for the one test that needs two at once.
    /// </summary>
    /// <remarks>
    /// <c>ZY</c> rather than <c>MD</c>, and the choice is the whole point of
    /// <see cref="TheConcurrencyRefusalIsScopedByActivityLocation"/>: proving the refusal is scoped
    /// requires a run in a <i>different</i> location, and using the real one would mean this suite
    /// opening a run that the monitoring grid displays among genuine Maryland loads. Neither <c>ZZ</c>
    /// nor <c>ZY</c> is a code EPA can issue, and <c>dbo.HandlerSource</c> has no <c>CHECK</c> on the
    /// column, so both are legal values that can never collide with real data.
    /// </remarks>
    private const string SecondLocation = "ZY";

    /// <summary>A run opens, closes, and reads back with the counters it was closed with.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The happy path, and the only test here that is not a refusal. It exists because the refusals
    /// below would all still pass against a procedure that refused everything.
    /// </remarks>
    [IntegrationFact]
    public async Task ARunOpensClosesAndReadsBackWithItsCounters()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        int loadRunId = await Runs.StartAsync(scope.Context);

        Assert.True(
            loadRunId > 0,
            "logs.uspStartLoadRun must return the identity it inserted. A zero would mean the OUTPUT "
            + "parameter came back unset, which is how the missing OUTPUT keyword in the EXEC text "
            + "presented before 2026-09-05.");

        LoadRunSummary? running = await scope.Context.GetLoadRunSummaryAsync(loadRunId);
        Assert.NotNull(running);
        Assert.Equal("Running", running.Status, StringComparer.Ordinal);
        Assert.Equal(Runs.RunMode, running.RunMode, StringComparer.Ordinal);
        Assert.Equal(TestData.ActivityLocation, running.ActivityLocation, StringComparer.Ordinal);
        Assert.Null(running.CompletedDateUtc);

        LoadRunCounters counters = new()
        {
            LookupListsRefreshed = 24,
            SourceRecordsEnumerated = 7,
            SourceRecordsFetched = 7,
            SourceRecordsInserted = 3,
            SourceRecordsUpdated = 2,
            SourceRecordsUnchanged = 2,
            HttpRequestCount = 9,
            HttpRetryCount = 1,
        };

        await scope.Context.CompleteLoadRunAsync(loadRunId, "Succeeded", null, counters);

        LoadRunSummary? closed = await scope.Context.GetLoadRunSummaryAsync(loadRunId);
        Assert.NotNull(closed);
        Assert.Equal("Succeeded", closed.Status, StringComparer.Ordinal);
        Assert.NotNull(closed.CompletedDateUtc);
        Assert.True(closed.CompletedDateUtc >= running.StartedDateUtc);

        Assert.Equal(counters.LookupListsRefreshed, closed.LookupListsRefreshed);
        Assert.Equal(counters.SourceRecordsEnumerated, closed.SourceRecordsEnumerated);
        Assert.Equal(counters.SourceRecordsInserted, closed.SourceRecordsInserted);
        Assert.Equal(counters.SourceRecordsUpdated, closed.SourceRecordsUpdated);
        Assert.Equal(counters.SourceRecordsUnchanged, closed.SourceRecordsUnchanged);
        Assert.Equal(counters.HttpRequestCount, closed.HttpRequestCount);
        Assert.Equal(counters.HttpRetryCount, closed.HttpRetryCount);
    }

    /// <summary>Closing a run twice with the same status is a no-op; with a different one it is refused.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Two halves of one rule, and they must be tested together or the test proves the wrong thing. The
    /// retry has to succeed, because a loader whose <c>CompleteLoadRunAsync</c> call timed out on the
    /// wire after the server committed will call it again and must not then fail a run that finished.
    /// The contradiction has to be refused, because applying the second account would make whichever
    /// call arrived last the truth. A procedure that refused both would break the retry; one that
    /// accepted both would rewrite history.
    /// </remarks>
    [IntegrationFact]
    public async Task ClosingARunTwiceIsARetryOnlyWhenTheStatusAgrees()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        int loadRunId = await Runs.StartAsync(scope.Context);

        await scope.Context.CompleteLoadRunAsync(loadRunId, "Succeeded", null, new LoadRunCounters());

        // The retry: same status, no throw, and the counters are deliberately NOT re-applied -- a
        // second opinion about the totals arriving after the fact is not more accurate than the first.
        await scope.Context.CompleteLoadRunAsync(
            loadRunId, "Succeeded", null, new LoadRunCounters { SourceRecordsInserted = 999 });

        LoadRunSummary? summary = await scope.Context.GetLoadRunSummaryAsync(loadRunId);
        Assert.NotNull(summary);
        Assert.Equal(0, summary.SourceRecordsInserted);

        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => scope.Context.CompleteLoadRunAsync(
                loadRunId, "Failed", "A second, contradictory account of the same run.",
                new LoadRunCounters()));

        Assert.True(SqlErrorNumbers.IsProcedureRefusal(error));

        // Nothing changed. The refusal says "Nothing has been changed", and a refusal that had already
        // written something would be worse than no refusal at all.
        LoadRunSummary? after = await scope.Context.GetLoadRunSummaryAsync(loadRunId);
        Assert.NotNull(after);
        Assert.Equal("Succeeded", after.Status, StringComparer.Ordinal);
    }

    /// <summary>The statuses and counter combinations a run may not be closed with.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Four refusals, one run each, because a refused call leaves the run open and the next assertion
    /// needs a run in a known state.
    /// </para>
    /// <para>
    /// <c>Running</c> is refused because it is not a terminal status and <c>uspCompleteLoadRun</c>'s job
    /// is to end a run. <c>Failed</c> without a message is refused because the monitoring web app shows
    /// the message and a failure with nothing to show is a red row an operator cannot act on.
    /// <c>Succeeded</c> with failures counted is refused for the reason the procedure gives: only a run
    /// reported <c>Succeeded</c> may advance the watermark, so accepting it would let a run skip past
    /// records it failed to load and close the gap behind itself. A negative counter is refused because
    /// every counter is a tally and a negative one is a subtraction where an accumulation was meant.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task ARunCannotBeClosedIntoAStateThatContradictsItself()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        await AssertRefusedAsync(
            scope,
            (context, id) => context.CompleteLoadRunAsync(
                id, "Running", null, new LoadRunCounters()),
            "'Running' is not a terminal status, so it cannot be the status a run is CLOSED with.");

        await AssertRefusedAsync(
            scope,
            (context, id) => context.CompleteLoadRunAsync(
                id, "Failed", null, new LoadRunCounters()),
            "A failed run with no failure message is a red row the monitoring app cannot explain.");

        await AssertRefusedAsync(
            scope,
            (context, id) => context.CompleteLoadRunAsync(
                id, "Succeeded", null, new LoadRunCounters { SourceRecordsFailed = 1 }),
            "'Succeeded' with a failed record is the status that may advance the watermark, applied to "
            + "a run that did not load everything. 'PartiallySucceeded' is the one that describes it.");

        await AssertRefusedAsync(
            scope,
            (context, id) => context.CompleteLoadRunAsync(
                id, "Succeeded", null, new LoadRunCounters { SourceRecordsSkipped = -1 }),
            "A negative counter is a loader defect rather than an operator mistake.");
    }

    /// <summary>A status the table's CHECK constraint does not permit is refused by name.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The procedure lists the four terminal statuses itself rather than letting the row reach
    /// <c>CK_logs_LoadRun_Status</c>, which matters for the message: a constraint violation arrives as
    /// error 547 naming a constraint, and an operator reading the monitoring app's error text would see
    /// the name of a database object instead of the list of statuses they could have used.
    /// </remarks>
    [IntegrationFact]
    public async Task AStatusOutsideTheClosedSetIsRefusedByTheProcedure()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        SqlException error = await AssertRefusedAsync(
            scope,
            (context, id) => context.CompleteLoadRunAsync(
                id, "Finished", null, new LoadRunCounters()),
            "'Finished' is not one of the four terminal statuses.");

        Assert.False(
            SqlErrorNumbers.IsConstraintViolation(error),
            "The procedure must refuse the value itself. Arriving here as a CHECK constraint violation "
            + "would mean the caller is shown a constraint name rather than the statuses available.");
    }

    /// <summary>Closing a run that never existed is refused rather than silently doing nothing.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The interesting half is the reason the procedure gives: an identifier that matches no run means
    /// either it never came from <c>uspStartLoadRun</c>, or the transaction that opened it rolled back
    /// and the loader is holding an id that was never committed. Both are defects, and an update that
    /// affected zero rows and returned quietly would hide either one.
    /// </remarks>
    [IntegrationFact]
    public async Task ClosingARunThatDoesNotExistIsRefused()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => scope.Context.CompleteLoadRunAsync(
                int.MaxValue, "Succeeded", null, new LoadRunCounters()));

        Assert.True(SqlErrorNumbers.IsProcedureRefusal(error));
    }

    /// <summary>A second run in the same location is refused while the first is still open.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Two concurrent loads of the same feed would fetch the same pages from EPA twice and merge them
    /// into the same rows, and the second would advance the watermark past records the first was still
    /// working on. So the default is a refusal, and it is not retryable: a retried start would either
    /// begin the run the refusal existed to prevent or spin until the first finished.
    /// </para>
    /// <para>
    /// <c>AllowConcurrent</c> exists for the case this suite depends on — a run left open by a test that
    /// failed part-way would otherwise refuse every later test until the abandonment sweep caught up,
    /// turning one failure into a suite of them. So the flag is exercised in both positions here rather
    /// than only in the position <see cref="Runs"/> uses.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task ASecondRunInTheSameLocationIsRefusedUnlessConcurrencyIsAllowed()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        int first = await Runs.StartAsync(scope.Context);

        try
        {
            SqlException error = await Assert.ThrowsAsync<SqlException>(
                () => scope.Context.StartLoadRunAsync(Request(TestData.ActivityLocation, allow: false)));

            Assert.True(SqlErrorNumbers.IsProcedureRefusal(error));
            Assert.False(
                SqlErrorNumbers.IsRetryable(error),
                "A concurrency refusal must not be retryable. Retrying it would either start the "
                + "second run the refusal prevents, or spin until the first finished.");

            // And the flag really is the difference, not something about the first run.
            int allowed = await scope.Context.StartLoadRunAsync(
                Request(TestData.ActivityLocation, allow: true));

            Assert.NotEqual(first, allowed);
            await Runs.CompleteAsync(scope.Context, allowed);
        }
        finally
        {
            // Closed even if an assertion failed, because a run left Running would refuse the next
            // test that did not pass AllowConcurrent -- which is the failure this test is about.
            await Runs.CompleteAsync(scope.Context, first);
        }
    }

    /// <summary>The refusal is scoped by activity location, so a test run cannot block a real one.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// This is the property that makes the whole suite safe to run on a workstation that also holds real
    /// data. Every run this suite opens is under <c>ZZ</c>; if the concurrency check were global, a test
    /// run left open would refuse a genuine Maryland load, and a scheduled overnight load would fail
    /// because someone ran the tests.
    /// </para>
    /// <para>
    /// Proved with a second reserved location rather than with <c>MD</c>, for the reason
    /// <see cref="SecondLocation"/> gives: the assertion needs two locations, not the real one.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task TheConcurrencyRefusalIsScopedByActivityLocation()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        int here = await Runs.StartAsync(scope.Context);
        int? elsewhere = null;

        try
        {
            // No AllowConcurrent. It must succeed anyway, because the open run is somewhere else.
            elsewhere = await scope.Context.StartLoadRunAsync(Request(SecondLocation, allow: false));

            Assert.NotEqual(here, elsewhere.Value);

            LoadRunSummary? summary = await scope.Context.GetLoadRunSummaryAsync(elsewhere.Value);
            Assert.NotNull(summary);
            Assert.Equal(SecondLocation, summary.ActivityLocation, StringComparer.Ordinal);
        }
        finally
        {
            if (elsewhere is not null)
            {
                await Runs.CompleteAsync(scope.Context, elsewhere.Value);
            }

            await Runs.CompleteAsync(scope.Context, here);
        }
    }

    /// <summary>A run opened with no activity location is refused.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The parameter defaults to <c>'MD'</c> in the procedure, but the data layer binds it explicitly
    /// from a <c>required</c> property, so the reachable failure is an empty or whitespace value rather
    /// than an omission — and whitespace is what a configuration file supplies when someone leaves the
    /// key present and the value blank.
    /// </remarks>
    [IntegrationFact]
    public async Task ARunWithNoActivityLocationIsRefused()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => scope.Context.StartLoadRunAsync(Request(" ", allow: true)));

        Assert.True(SqlErrorNumbers.IsProcedureRefusal(error));
    }

    /// <summary>A request under the reserved location, with concurrency allowed or not.</summary>
    private static LoadRunRequest Request(string activityLocation, bool allow) =>
        new()
        {
            RunMode = Runs.RunMode,
            ActivityLocation = activityLocation,
            ApplicationVersion = "DA5 tests",
            AllowConcurrent = allow,
        };

    /// <summary>
    /// Opens a run, asserts the given call is refused, and closes the run afterwards.
    /// </summary>
    /// <param name="scope">The scope to work in.</param>
    /// <param name="call">The call that must be refused.</param>
    /// <param name="because">What the refusal is protecting, for the assertion message.</param>
    /// <returns>The exception, for a caller that wants to assert more about it.</returns>
    /// <remarks>
    /// A run per refusal, and cleaned up in a <c>finally</c>: a refused close leaves the run open, and
    /// an open run under this location would refuse the next start that did not allow concurrency.
    /// There is no hard delete here, so an abandoned run is permanent — worth the extra lines.
    /// </remarks>
    private static async Task<SqlException> AssertRefusedAsync(
        IntegrationScope scope,
        Func<RCRAInfoContext, int, Task> call,
        string because)
    {
        int loadRunId = await Runs.StartAsync(scope.Context);

        try
        {
            SqlException error = await Assert.ThrowsAsync<SqlException>(
                () => call(scope.Context, loadRunId));

            Assert.True(
                SqlErrorNumbers.IsProcedureRefusal(error),
                $"Expected the procedure's own refusal ({SqlErrorNumbers.ProcedureRefusal}) but got "
                + $"{error.Number}. {because}");

            return error;
        }
        finally
        {
            await Runs.CompleteAsync(scope.Context, loadRunId);
        }
    }
}
