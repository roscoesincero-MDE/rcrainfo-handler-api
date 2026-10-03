using Microsoft.Data.SqlClient;

using RCRAInfo.Data.Results;

namespace RCRAInfo.Data.Integration.Tests;

/// <summary>
/// The read procedures, called: paging, sorting, the sort whitelist, and the soft-delete opt-in.
/// </summary>
/// <remarks>
/// <para>
/// Four acceptance items from Workstream DA live here, and each one is a test rather than a review
/// because each one fails in a direction that looks like data:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Every read pages and sorts by parameter and returns <c>TotalRows</c>.</b> A read that ignored
///   <c>@Skip</c> would return page one every time, and a grid showing page one under a heading that
///   says page three looks like a database with less data in it than it has.
///   </description></item>
///   <item><description>
///   <b>A deliberately malicious <c>@SortBy</c> cannot alter the query.</b> Checked by sending values
///   that would matter if the parameter were ever concatenated, and by confirming the table is still
///   there afterwards.
///   </description></item>
///   <item><description>
///   <b>Paging across a non-unique sort key returns each row exactly once.</b> The classic paging
///   defect: <c>ORDER BY</c> a column with ties, and the engine is free to break them differently on
///   each page, so a row appears twice and another never appears at all. Nothing errors, both pages
///   are plausible, and the missing row is missing from a screen nobody is comparing against a total.
///   </description></item>
///   <item><description>
///   <b>The two <c>logs</c> reads honour <c>IncludeDeleted</c> as an opt-in.</b> Default false, like
///   every read path in this database; true only when asked.
///   </description></item>
/// </list>
/// <para>
/// And one regression test, for a defect these tests found on 2026-09-05: a read with no
/// <c>@SortBy</c> specified. The procedures refuse a value outside their whitelist rather than falling
/// through to a default, and RCRAInfo.Data binds every parameter explicitly, so an unset sort arrived
/// as <c>NULL</c> and every such read threw. See <see cref="ADefaultSortIsAcceptedByEveryPagedRead"/>.
/// </para>
/// </remarks>
[Collection(IntegrationSuite.Name)]
public sealed class ReadProcedureTests
{
    /// <summary>The handler this class seeds with three tied versions, for the paging tests.</summary>
    private const int PagingOrdinal = 5;

    /// <summary>How many versions <see cref="Seeded"/> writes. Three, so a page size of one ties twice.</summary>
    private const int Versions = 3;

    /// <summary>
    /// <c>@SortBy</c> values that would matter if the parameter were ever concatenated into a query.
    /// </summary>
    /// <remarks>
    /// The last two are the ones worth having beyond the obvious. <c>1</c> is the ordinal form —
    /// <c>ORDER BY 1</c> is legal SQL and sorts by the first projected column, so a procedure that
    /// interpolated the parameter would accept it and silently sort by something. And a value that is a
    /// real column of the underlying view but not of the whitelist proves the whitelist is a list rather
    /// than a check that the name exists.
    /// </remarks>
    public static TheoryData<string> HostileSortValues =>
    [
        "HandlerId; DROP TABLE dbo.HandlerSource --",
        "HandlerId'",
        "(SELECT TOP 1 ContactLastName FROM dbo.HandlerSource)",
        "1",
        "ContactLastName",
    ];

    /// <summary>The three tied versions, seeded once for the whole class.</summary>
    /// <remarks>
    /// Same reasoning as <c>MergeRoundTripTests.Merged</c>: xUnit builds a new instance of this class
    /// per test case, so a constructor would re-seed for every one of them.
    /// </remarks>
    private static Lazy<Task<string>> Seeded { get; } = new(SeedAsync);

    /// <summary>A read with no sort specified succeeds, on all four paged reads.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is a regression test.</b> Until 2026-09-05 every one of these four calls threw, because
    /// the query records left <c>SortBy</c> null, the binder sent <c>NULL</c>, and each procedure's
    /// whitelist refused it with "@SortBy = NULL is not a sortable column of this grid". The procedures
    /// were right to refuse — an unrecognised sort must not fall through to a default, or a grid that
    /// mistypes its sort column gets rows back sorted by something else and the defect surfaces as a
    /// user saying the sort arrows do not work. The defect was that the data layer never let the
    /// procedures' own declared defaults fire.
    /// </para>
    /// <para>
    /// It went unnoticed through every offline test because no offline test opens a connection. It is
    /// the plainest example of what this suite is for: the four reads were correct, the four records
    /// were correct, and the combination did not work.
    /// </para>
    /// <para>
    /// <c>build/check_sort_defaults.py</c> holds the repeated default against the procedure's
    /// declaration. This asserts the thing the script cannot: that the value is actually accepted.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task ADefaultSortIsAcceptedByEveryPagedRead()
    {
        string handlerId = await Seeded.Value;

        await using IntegrationScope scope = IntegrationServer.Connect();

        // Each of the four constructed with nothing but paging. Any one of them throwing is the
        // defect back again.
        Page<HandlerSourceGridRow> grid = await scope.Context
            .GetHandlerSourcePageAsync(new HandlerSourcePageQuery { HandlerId = handlerId, Take = 10 });

        Page<HandlerSourceHistoryRow> history = await scope.Context
            .GetHandlerSourceHistoryPageAsync(new HandlerSourceHistoryQuery { HandlerId = handlerId });

        Page<LoadRunRow> runs = await scope.Context
            .GetLoadRunPageAsync(new LoadRunPageQuery
            {
                ActivityLocation = TestData.ActivityLocation,
                Take = 1,
            });

        Page<HandlerLoadStatusRow> status = await scope.Context
            .GetHandlerLoadStatusPageAsync(new HandlerLoadStatusPageQuery
            {
                ActivityLocation = TestData.ActivityLocation,
                Take = 1,
            });

        // The default is the procedure's, so assert the record agrees rather than restating the string.
        Assert.Equal("HandlerId", HandlerSourcePageQuery.DefaultSortBy, StringComparer.Ordinal);
        Assert.Equal("ReceivedDate", HandlerSourceHistoryQuery.DefaultSortBy, StringComparer.Ordinal);
        Assert.Equal("StartedDateUtc", LoadRunPageQuery.DefaultSortBy, StringComparer.Ordinal);
        Assert.Equal("FirstSeenDateUtc", HandlerLoadStatusPageQuery.DefaultSortBy, StringComparer.Ordinal);

        Assert.Equal(Versions, history.TotalRows);
        Assert.NotEmpty(grid.Items);

        // The suite has opened at least one run of its own by now, so an empty page here would mean the
        // ActivityLocation filter is not matching what StartLoadRunAsync wrote.
        Assert.NotEmpty(runs.Items);

        // Not asserted non-empty: nothing in this class writes a handler load status row, and
        // ExecutionLogTests owns that procedure. Zero rows is a correct answer here, so what is
        // asserted is the invariant that holds either way -- an empty page reports no total, and a
        // non-empty one reports at least as many rows as it returned.
        Assert.Equal(
            status.Items.Count == 0 ? 0 : status.TotalRows,
            status.TotalRows);
        Assert.True(status.TotalRows >= status.Items.Count);
    }

    /// <summary>Every paged read returns the same total on every page, and pages by parameter.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <c>TotalRows</c> is repeated on every row of every one of these projections, which is how one
    /// round trip carries both the page and the count. The assertion that matters is that it does not
    /// change as the page moves: a total computed after paging would come back as the size of the page,
    /// and the grid would report three records and show three records while holding thirty.
    /// </remarks>
    [IntegrationFact]
    public async Task PagingMovesTheWindowAndLeavesTheTotalAlone()
    {
        string handlerId = await Seeded.Value;

        await using IntegrationScope scope = IntegrationServer.Connect();

        Page<HandlerSourceHistoryRow> all = await scope.Context
            .GetHandlerSourceHistoryPageAsync(new HandlerSourceHistoryQuery
            {
                HandlerId = handlerId,
                Skip = 0,
                Take = Versions,
            });

        Assert.Equal(Versions, all.TotalRows);
        Assert.Equal(Versions, all.Items.Count);
        Assert.False(all.HasMore);

        Page<HandlerSourceHistoryRow> firstOnly = await scope.Context
            .GetHandlerSourceHistoryPageAsync(new HandlerSourceHistoryQuery
            {
                HandlerId = handlerId,
                Skip = 0,
                Take = 1,
            });

        Assert.Equal(Versions, firstOnly.TotalRows);
        Assert.Single(firstOnly.Items);
        Assert.True(firstOnly.HasMore);

        Page<HandlerSourceHistoryRow> pastTheEnd = await scope.Context
            .GetHandlerSourceHistoryPageAsync(new HandlerSourceHistoryQuery
            {
                HandlerId = handlerId,
                Skip = Versions,
                Take = Versions,
            });

        // An empty page is Page<T>.Empty, whose TotalRows is 0 because there was no row to read it
        // from. That is the shape the context defines, so it is asserted rather than worked around --
        // a caller past the end has a count already.
        Assert.Empty(pastTheEnd.Items);
        Assert.Equal(0, pastTheEnd.TotalRows);
    }

    /// <summary>Paging a tied sort key returns each row exactly once across the pages.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// The three seeded versions share one <c>ReceivedDate</c>, which is the history read's default
    /// sort column, so every comparison the <c>ORDER BY</c> makes on it is a tie. Read one row at a
    /// time and the three keys must be three distinct keys.
    /// </para>
    /// <para>
    /// Without a tiebreaker the engine is entitled to order the ties differently for each
    /// <c>OFFSET</c>, and it often does once the plan changes shape. The failure is a duplicate on one
    /// page and an absence on another, and both pages look right on their own.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task PagingATiedSortKeyReturnsEachRowExactlyOnce()
    {
        string handlerId = await Seeded.Value;

        await using IntegrationScope scope = IntegrationServer.Connect();

        List<int> seen = [];
        List<DateOnly?> dates = [];

        for (int skip = 0; skip < Versions; skip++)
        {
            Page<HandlerSourceHistoryRow> page = await scope.Context
                .GetHandlerSourceHistoryPageAsync(new HandlerSourceHistoryQuery
                {
                    HandlerId = handlerId,
                    Skip = skip,
                    Take = 1,
                });

            HandlerSourceHistoryRow row = Assert.Single(page.Items);
            seen.Add(row.HandlerSourceId);
            dates.Add(row.ReceivedDate);
        }

        // The premise, asserted rather than assumed. If the fixture ever gives the three versions
        // different received dates this test still passes, but it stops testing ties -- and would then
        // be a test whose name is a claim it no longer checks.
        Assert.Single(dates.Distinct());

        Assert.Equal(Versions, seen.Distinct().Count());
    }

    /// <summary>A hostile <c>@SortBy</c> is refused, and the table is still there afterwards.</summary>
    /// <param name="sortBy">The value to send.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// There is no dynamic SQL in these procedures at all — the <c>ORDER BY</c> is a ladder of
    /// <c>CASE WHEN @SortBy = N'...'</c>, so the parameter is compared rather than executed and none of
    /// these values could run even if the whitelist were absent. That is exactly why the test asserts
    /// the <b>refusal</b> and not merely the absence of damage: "nothing bad happened" would pass
    /// against a procedure that had quietly started concatenating and happened to be sent a value that
    /// parsed.
    /// </para>
    /// <para>
    /// The last two values are the interesting ones. <c>1</c> is legal in a real <c>ORDER BY</c> and
    /// would sort by the first projected column; <c>ContactLastName</c> is a genuine column of the
    /// underlying view, so accepting it would both prove the whitelist is not a list and let a caller
    /// sort a grid by a PII column the projection deliberately excludes.
    /// </para>
    /// </remarks>
    [IntegrationTheory]
    [MemberData(nameof(HostileSortValues))]
    public async Task AHostileSortByIsRefused(string sortBy)
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => scope.Context.GetHandlerSourcePageAsync(new HandlerSourcePageQuery
            {
                SortBy = sortBy,
                Take = 1,
            }));

        Assert.True(
            SqlErrorNumbers.IsProcedureRefusal(error),
            $"A hostile @SortBy must be refused by the procedure, which raises "
            + $"{SqlErrorNumbers.ProcedureRefusal}. This came back as {error.Number}, which means "
            + "something other than the whitelist rejected it -- a syntax error would mean the value "
            + "reached the query text.");

        Assert.False(SqlErrorNumbers.IsRetryable(error));

        // The table named in the first value is still a table. Cheap, and it is the assertion a reader
        // of this test will look for whether or not the procedure could ever have executed the string.
        await using SqlConnection connection = await IntegrationServer.OpenAsync();

        int? exists = await IntegrationServer.ScalarAsync<int>(
            connection,
            "SELECT COUNT (*) FROM sys.tables WHERE object_id = OBJECT_ID (N'dbo.HandlerSource');");

        Assert.Equal(1, exists);
    }

    /// <summary>A sort column in the wrong case is accepted, because the comparison is collation-based.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// The counterpart to <see cref="AHostileSortByIsRefused"/>, and it is not a nicety: the monitoring
    /// web app's grid sends the column name its JavaScript holds, and script 500's header states that
    /// <c>'startedDateUtc'</c> from such a grid is meant to work. A whitelist compared with an ordinal
    /// collation would refuse every sort the front end asked for, and the refusal would look like the
    /// injection defence working.
    /// </remarks>
    [IntegrationFact]
    public async Task ASortColumnInTheWrongCaseIsAccepted()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        string handlerId = await Seeded.Value;

        Page<HandlerSourceGridRow> canonical = await scope.Context
            .GetHandlerSourcePageAsync(new HandlerSourcePageQuery
            {
                HandlerId = handlerId,
                SortBy = HandlerSourcePageQuery.DefaultSortBy,
                Take = 10,
            });

        Page<HandlerSourceGridRow> lowercased = await scope.Context
            .GetHandlerSourcePageAsync(new HandlerSourcePageQuery
            {
                HandlerId = handlerId,
                SortBy = HandlerSourcePageQuery.DefaultSortBy.ToLowerInvariant(),
                Take = 10,
            });

        // Compared against the canonical spelling rather than merely "did not throw": the two must be
        // the same page, because a whitelist that accepted the lower-case value and then failed to
        // match it in the ORDER BY ladder would return rows in some other order without erroring.
        Assert.NotEmpty(canonical.Items);
        Assert.Equal(canonical.TotalRows, lowercased.TotalRows);
        Assert.Equal(
            canonical.Items.Select(r => r.HandlerSourceId),
            lowercased.Items.Select(r => r.HandlerSourceId));
    }

    /// <summary>The detail read returns null for an identifier that does not exist.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// Null rather than an empty row or a throw. The distinction matters to the monitoring app, which
    /// shows "no such version" differently from "a version with no data", and it is the same shape the
    /// soft-delete case produces — see the corresponding assertion in <c>MergeRoundTripTests</c>.
    /// </remarks>
    [IntegrationFact]
    public async Task TheDetailReadReturnsNullForAnIdentifierThatDoesNotExist()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        Assert.Null(await scope.Context.GetHandlerSourceDetailAsync(int.MaxValue));
    }

    /// <summary>An unconfigured feed is refused rather than returned as an absence.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// This test was written expecting <see langword="null"/>, because that is what
    /// <c>GetLoadWatermarkAsync</c>'s summary promised, and it failed. Script 512 raises instead, and
    /// its reasoning is better than the documentation's was: both guesses available to a caller who
    /// received an absence are wrong. Reading "no configuration" as a full load makes a mistyped feed
    /// name re-fetch everything from EPA; reading it as "nothing to do" makes a scheduled load stop
    /// happening and report success. Neither is a state a caller can be trusted to disambiguate, so
    /// the procedure refuses to hand one over.
    /// </para>
    /// <para>
    /// The XML doc was corrected to match, which is the finding: the C# said a caller should test for
    /// null, and a caller who did would have written a branch that never runs and no <c>catch</c> for
    /// the one that does. Recorded here rather than only in the fix, because the next person to read
    /// the signature will have the same expectation this test did.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task AnUnconfiguredFeedIsRefusedRatherThanReturnedAsAnAbsence()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => scope.Context.GetLoadWatermarkAsync(
                "ZZTestFeedThatIsNotConfigured", TestData.ActivityLocation));

        Assert.True(SqlErrorNumbers.IsProcedureRefusal(error));

        // Not retryable, and that is the consequence that matters: an unattended loader retrying a
        // configuration error would sit in a loop all night rather than failing the run at the first
        // attempt with a message naming the feed.
        Assert.False(SqlErrorNumbers.IsRetryable(error));
    }

    /// <summary>The two <c>logs</c> reads hide soft-deleted rows until asked for them.</summary>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// Every read path in this database filters <c>IsDeleted = 0</c>; these two are the only ones that
    /// can be asked not to. The flag exists because retention soft-deletes old runs and an operator
    /// investigating a gap in the history needs to see that the runs existed — which is the opposite of
    /// the usual reason for such a flag, and the reason it is an explicit parameter rather than a
    /// default.
    /// </para>
    /// <para>
    /// Nothing in this suite soft-deletes a load run — there is no procedure to do it, retention has
    /// not been built, and a test that reached into <c>logs.LoadRun</c> with an <c>UPDATE</c> would be
    /// testing its own <c>UPDATE</c>. So this asserts the reachable half: the default excludes nothing
    /// it should include, the opt-in returns at least as much as the default, and both agree about the
    /// run this test just opened. A stricter assertion would need a deletion path that does not exist,
    /// and inventing one here would leave a permanently soft-deleted run in a database that cannot
    /// hard-delete it.
    /// </para>
    /// </remarks>
    [IntegrationFact]
    public async Task TheLogsReadsTreatIncludeDeletedAsAnOptIn()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        int loadRunId = await Runs.StartAsync(scope.Context);
        await Runs.CompleteAsync(scope.Context, loadRunId);

        Page<LoadRunRow> excluded = await scope.Context.GetLoadRunPageAsync(new LoadRunPageQuery
        {
            LoadRunId = loadRunId,
        });

        Page<LoadRunRow> included = await scope.Context.GetLoadRunPageAsync(new LoadRunPageQuery
        {
            LoadRunId = loadRunId,
            IncludeDeleted = true,
        });

        Assert.Equal(1, excluded.TotalRows);
        Assert.Equal(1, included.TotalRows);

        // The summary read takes the same opt-in as a plain argument, and must agree with the page.
        LoadRunSummary? summary = await scope.Context.GetLoadRunSummaryAsync(loadRunId);
        Assert.NotNull(summary);
        Assert.Equal(loadRunId, summary.LoadRunId);

        Page<HandlerLoadStatusRow> statusExcluded = await scope.Context
            .GetHandlerLoadStatusPageAsync(new HandlerLoadStatusPageQuery { LoadRunId = loadRunId });

        Page<HandlerLoadStatusRow> statusIncluded = await scope.Context
            .GetHandlerLoadStatusPageAsync(new HandlerLoadStatusPageQuery
            {
                LoadRunId = loadRunId,
                IncludeDeleted = true,
            });

        // This run wrote no status rows, so both are empty. The assertion worth making is the
        // relationship, which holds whether or not there are rows: the opt-in can only ever add.
        Assert.True(statusIncluded.TotalRows >= statusExcluded.TotalRows);
    }

    /// <summary>Three versions of one handler, all sharing a received date.</summary>
    /// <returns>The handler identifier.</returns>
    /// <remarks>
    /// Retired first, for the reason <see cref="Runs"/> sets out at length: the merge reports nothing
    /// for a record that is already current, so a corpus that cannot be deleted has to be soft-deleted
    /// before it is written or these tests pass once and never again.
    /// </remarks>
    private static async Task<string> SeedAsync()
    {
        await using IntegrationScope scope = IntegrationServer.Connect();

        string handlerId = TestData.HandlerId(PagingOrdinal);
        int loadRunId = await Runs.StartAsync(scope.Context);
        int[] sequences = [.. Enumerable.Range(1, Versions)];

        await Runs.RetireAsync(scope.Context, loadRunId, handlerId, sequences);

        // Every version carries the fixture's received date, unmodified, which is what makes the sort
        // key tied. currentRecord on the last one only, because the natural-key index permits one.
        await scope.Context.MergeHandlerSourceBatchAsync(
            [.. sequences.Select(s => TestData.Envelope(handlerId, s, s == Versions))],
            loadRunId);

        await Runs.CompleteAsync(scope.Context, loadRunId);

        return handlerId;
    }
}
