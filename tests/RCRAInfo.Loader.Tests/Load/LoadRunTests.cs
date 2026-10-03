using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// <see cref="LoadRun"/>: the order the stages run in, and what each one's failure means to the run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Almost nothing here re-tests a stage.</b> The lookup refresh, the walk, the resume split and the
/// journal each have their own suite, and every one of them is stubbed. What is left is the two things only
/// the orchestrator holds — the sequence, and the verdict — and they are exactly the two that no stage can
/// assert about itself.
/// </para>
/// <para>
/// <b>One branch is deliberately not covered here, and it is worth naming rather than leaving as a gap
/// somebody discovers.</b> The <c>AlreadyRunning</c> mapping fires on
/// <c>SqlErrorNumbers.IsProcedureRefusal</c>, and <c>Microsoft.Data.SqlClient.SqlException</c> cannot be
/// constructed — the same wall <c>SqlCredentialValidatorTests</c> documents. Faking one by reflection would
/// buy coverage of this mapping at the price of a test that breaks on a package upgrade for reasons having
/// nothing to do with this project, so the refusal path belongs in the integration suite, where a real
/// second run provokes the real error 50000. The same limitation applies to the two <c>SqlException</c>
/// catches around the watermark move and the closing write.
/// </para>
/// </remarks>
public sealed class LoadRunTests
{
    private static readonly HandlerVersion First = new("MDD000000001", "N", 1);
    private static readonly HandlerVersion Second = new("MDD000000002", "N", 1);
    private static readonly HandlerVersion Third = new("MDD000000003", "N", 1);

    /// <summary>
    /// The whole sequence, asserted as a sequence. Three of its steps are load-bearing.
    /// </summary>
    /// <remarks>
    /// The lookups precede the first fetch (G15), the resume read precedes the run's own row (script 510
    /// takes <c>@ResumedFromLoadRunId</c> as an input), and the enumerate precedes the skip (script 520's
    /// <c>Skip</c> mode UPDATEs and never INSERTs, so a skipped version that was never enumerated is a row
    /// that does not exist).
    /// <para>
    /// <b>Both <c>Flush</c> steps above <c>Merge</c> are load-bearing, and this test asserted the wrong order
    /// until F1 measured it.</b> Neither is tidiness. <c>Enumerate</c>, <c>Skip</c> and
    /// <c>RecordAttempt</c> only BUFFER — the journal is write-behind — while
    /// <c>dbo.uspMergeHandlerSourceBatch</c> writes the success half of those same
    /// <c>logs.HandlerLoadStatus</c> rows from OUTSIDE the journal, by UPDATE and never by INSERT. So a
    /// buffered row is a row the merge cannot find, and it costs twice: the merge updates nothing, so a
    /// version commits and its status stays <c>InProgress</c> with a NULL <c>Outcome</c> permanently — the
    /// monitoring app reads that table, and script 525 reads an <c>InProgress</c> row as work to redo — and
    /// a late <c>Attempt</c> write then lands on a row the merge has already marked <c>Succeeded</c>, which
    /// <c>logs.uspUpsertHandlerLoadStatusSet</c> excludes from <c>Attempt</c> mode by design, leaving
    /// <c>AttemptCount</c> at 0 for a version that was fetched once. This suite passed for the whole of that
    /// defect's life, because it asserted the sequence the code produced rather than the sequence the
    /// database needs — the same failure mode as the initial-load refusal in [R35] and the <c>+0000</c>
    /// expiry in [R33]. Only live runs against EPA showed it, and it took two of them: fixing the status
    /// half first exposed the attempt half.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheStagesRunInTheOrderTheDatabaseRequires()
    {
        Harness harness = Harness.For([First]);

        await harness.RunAsync();

        Assert.Equal(
            [
                "ReadWatermark",
                "ReadResume",
                "StartRun",
                "RefreshLookups",
                "Walk",
                "Enumerate(1)",
                "PlanResume",
                "Skip(0)",

                // The status rows reach the database HERE, before any merge can look for them.
                "Flush",

                "RecordAttempt",
                "Conclude",

                // And again HERE, above the merge rather than below it, so the attempt row is in the table
                // before the merge marks it Succeeded. Script 520 excludes a Succeeded row from Attempt mode
                // by design, so a flush on the far side of the merge leaves AttemptCount at 0.
                "Flush",

                "Merge(1)",

                // The CurrentRecord reconciliation (§D4), after the merge and before the watermark. After,
                // because it asserts EPA's version list against rows the merge has already written -- run
                // first it would flag the version this run was about to insert as absent. Before, only in the
                // sense that this run's own report is read before the result is composed: unlike the four
                // steps above it, an incomplete reconcile does NOT hold the bookmark, because a stale flag is
                // findable by one GROUP BY and repairable by a targeted run on that handler alone, where an
                // unwalked date range is neither.
                "Reconcile(1)",

                "Flush",
                "AdvanceWatermark",
                "CompleteRun",
                "DisposeJournal",
            ],
            harness.Log.Entries);
    }

    /// <summary>
    /// The resume answer exists before the row that records it, and it is read without a run number.
    /// </summary>
    /// <remarks>
    /// <b>The ordering is the hazard and the argument is the corollary.</b> A resume read issued after the
    /// new run's row exists would find the new run's own <c>Running</c> row as its candidate; a read given
    /// this run's identifier would resume from itself. Both produce a load that looks like a first run and
    /// re-fetches everything, which costs a night and reports success.
    /// </remarks>
    [Fact]
    public async Task TheResumeReadHappensBeforeTheRunRowExistsAndNamesNoRun()
    {
        Harness harness = Harness.For([First]);

        await harness.RunAsync();

        Assert.True(
            harness.Log.IndexOf("ReadResume") < harness.Log.IndexOf("StartRun"),
            harness.Log.ToString());

        Assert.Null(harness.Resume.AskedForLoadRunId);
    }

    /// <summary>
    /// Script 510 is given the same abandonment threshold the resume read used.
    /// </summary>
    /// <remarks>
    /// Neither procedure can check the other, and a mismatch has no downstream symptom: a resume that
    /// quietly did not happen is indistinguishable from a first run. So the only place it is observable is
    /// in the argument, before the call.
    /// </remarks>
    [Fact]
    public async Task BothProceduresAreGivenTheSameAbandonmentThreshold()
    {
        Harness harness = Harness.For([First]);
        harness.Options.AbandonAfterMinutes = 90;

        await harness.RunAsync();

        Assert.Equal(90, harness.Writer.StartRequest!.AbandonAfterMinutes);
    }

    /// <summary>
    /// The walk is given the range the watermark row recommended, unchanged.
    /// </summary>
    /// <remarks>
    /// <c>config.uspGetLoadWatermark</c> has already chosen the mode and applied the overlap (G25). An
    /// orchestrator that re-derived either would produce a run that works and covers the wrong days, and
    /// <c>/hd/sources/summaries</c> carries no envelope to notice with.
    /// </remarks>
    [Fact]
    public async Task TheRecommendedRangeIsPassedThroughAndNotRederived()
    {
        Harness harness = Harness.For([First]);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal((42, Ran.From, Ran.To), harness.Walk.Asked);
        Assert.Equal("Incremental", result.RunMode);
        Assert.Equal(Ran.From, harness.Writer.StartRequest!.RequestedFromDate);
        Assert.Equal(Ran.To, harness.Writer.StartRequest.RequestedToDate);
        Assert.Equal(2, harness.Writer.StartRequest.OverlapDaysApplied);
    }

    /// <summary>
    /// A disabled feed, an absent row and a row recommending nothing all open no run at all.
    /// </summary>
    /// <remarks>
    /// <b>No run row is the assertion, not the outcome.</b> A <c>Running</c> row for a run that does nothing
    /// is exactly the row that refuses the next one, so a refusal that opened a row first would switch the
    /// feed off for twelve hours rather than for one cycle.
    /// </remarks>
    /// <param name="label">Which refusal, so the four cases get four test names.</param>
    /// <param name="watermark">The row script 512 answered with.</param>
    /// <param name="expected">The outcome.</param>
    [Theory]
    [MemberData(nameof(EveryReasonNoRunOpens))]
    public async Task AFeedThatCannotBeRunOpensNoRunAndFetchesNothing(
        string label,
        LoadWatermark? watermark,
        LoadRunOutcome expected)
    {
        Assert.NotEmpty(label);

        Harness harness = Harness.For([First]);
        harness.Writer.Watermark = watermark;

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.LoadRunId);
        Assert.Null(harness.Writer.StartRequest);
        Assert.Null(harness.Writer.Completed);
        Assert.Equal(0, harness.Journal.CreatedForLoadRunId);
        Assert.Empty(harness.Client.RequestedUris);
        Assert.NotNull(result.FailureMessage);
    }

    /// <summary>The four ways a run does not open, and what each is called.</summary>
    public static TheoryData<string, LoadWatermark?, LoadRunOutcome> EveryReasonNoRunOpens =>
        new()
        {
            { "no row at all", null, LoadRunOutcome.NotConfigured },
            { "no recommended mode", Ran.Watermark(runMode: null), LoadRunOutcome.NotConfigured },
            {
                "an inverted range",
                Ran.Watermark(fromDate: Ran.To, toDate: Ran.From),
                LoadRunOutcome.NotConfigured
            },
            { "the feed switched off", Ran.Watermark(isEnabled: false), LoadRunOutcome.FeedDisabled },
        };

    /// <summary>
    /// A full load with no configured first day is refused, and the message names the setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the path every first run takes, and until <c>InitialLoadFromDate</c> existed it made the
    /// initial load impossible.</b> Script 340 seeds <c>WatermarkDate</c> as <c>NULL</c>; script 512 reads that
    /// as "ask for everything" and recommends <c>Full</c> with no from-date; and an absent from-date was
    /// indistinguishable here from a broken row, so the orchestrator refused with <c>NotConfigured</c> and exit
    /// code 8. The loader could be deployed, configured and scheduled, and still never load anything.
    /// </para>
    /// <para>
    /// <b>Refused rather than defaulted, deliberately.</b> The setting decides how far back the mirror reaches,
    /// and a wrong value is invisible: the walk reports every window it asked for as walked, and it never asked
    /// for the years before the floor. A refusal naming the setting is the only version of this an operator can
    /// act on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFullLoadWithNoConfiguredFirstDayIsRefusedAndNamesTheSetting()
    {
        Harness harness = Harness.For([First]);
        harness.Writer.Watermark = Ran.NeverAdvanced();

        Assert.Null(harness.Options.InitialLoadFromDate);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(LoadRunOutcome.NotConfigured, result.Outcome);
        Assert.Null(harness.Writer.StartRequest);
        Assert.Empty(harness.Client.RequestedUris);

        Assert.Contains("full load needs a first day", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains(
            "RCRAInfoLoad:InitialLoadFromDate", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains(
            LoadRunOptions.RecommendedInitialLoadFromDate,
            result.FailureMessage!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// With the floor configured, the same watermark row starts a full load from it.
    /// </summary>
    /// <remarks>
    /// The other half of the fix, and the one that proves the refusal above is a missing setting rather than a
    /// broken row: nothing about the watermark changes here.
    /// </remarks>
    [Fact]
    public async Task AConfiguredFirstDayIsWhatAFullLoadAsksFrom()
    {
        Harness harness = Harness.For([First]);
        harness.Writer.Watermark = Ran.NeverAdvanced();
        harness.Options.InitialLoadFromDate = new DateOnly(1980, 1, 1);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal("Full", result.RunMode);
        Assert.Equal((42, new DateOnly(1980, 1, 1), Ran.To), harness.Walk.Asked);
        Assert.Equal(new DateOnly(1980, 1, 1), harness.Writer.StartRequest!.RequestedFromDate);
        Assert.Equal(Ran.To, harness.Writer.StartRequest.RequestedToDate);
    }

    /// <summary>
    /// The floor never overrides a recommendation, so it cannot rewind an established mirror.
    /// </summary>
    /// <remarks>
    /// <b>The coalesce is one way round and it matters which.</b> Left the other way, a setting an operator
    /// filled in once for the initial load would keep re-walking the whole notification era on every nightly
    /// run — and re-loading history deliberately belongs to <c>config.uspSetLoadWatermark</c>, which records
    /// who rewound the bookmark and when.
    /// </remarks>
    [Fact]
    public async Task TheConfiguredFirstDayDoesNotRewindAWatermarkThatHasAdvanced()
    {
        Harness harness = Harness.For([First]);
        harness.Options.InitialLoadFromDate = new DateOnly(1980, 1, 1);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal("Incremental", result.RunMode);
        Assert.Equal((42, Ran.From, Ran.To), harness.Walk.Asked);
    }

    /// <summary>
    /// A first day after the recommended end is refused, and blames the setting rather than the watermark.
    /// </summary>
    /// <remarks>
    /// <b>Its own refusal arm because the fix is in a different place.</b> The watermark row here is perfectly
    /// correct; the configured floor is in the future. Sharing the generic inverted-range message would send an
    /// operator to <c>config.uspSetLoadWatermark</c> to fix a row that is right. And the reason it is refused at
    /// all rather than clamped: EPA answers an inverted range with <c>200</c> and an empty array, which reads as
    /// a quiet week and would advance the watermark over the whole range.
    /// </remarks>
    [Fact]
    public async Task AFirstDayAfterTheRecommendedEndIsRefusedAndBlamesTheSetting()
    {
        Harness harness = Harness.For([First]);
        harness.Writer.Watermark = Ran.NeverAdvanced();
        harness.Options.InitialLoadFromDate = Ran.To.AddDays(1);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(LoadRunOutcome.NotConfigured, result.Outcome);
        Assert.Null(harness.Writer.StartRequest);
        Assert.Empty(harness.Client.RequestedUris);

        Assert.Contains(
            "RCRAInfoLoad:InitialLoadFromDate", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("inverted range", result.FailureMessage!, StringComparison.Ordinal);

        // The generic arm's wording, which would send the operator to fix a watermark row that is correct.
        Assert.DoesNotContain(
            "the watermark row recommends a range", result.FailureMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fatal lookup outcome stops the run before a single handler is fetched.
    /// </summary>
    /// <remarks>
    /// G15's whole payoff. A rejected credential on a code list is the same failure the fetch loop would
    /// meet several hundred thousand times; refreshing the lookups first is what makes it met once.
    /// </remarks>
    [Fact]
    public async Task AFatalLookupOutcomeStopsTheRunBeforeAnyHandlerIsFetched()
    {
        Harness harness = Harness.For([First]);
        harness.Lookups = Ran.Lookups(refreshed: 2, fatal: ApiFetchOutcome.Unauthorized);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(LoadRunOutcome.Failed, result.Outcome);
        Assert.Equal(-1, harness.Log.IndexOf("Walk"));
        Assert.Empty(harness.Client.RequestedUris);
        Assert.Empty(harness.Writer.MergedBatches);
        Assert.Null(harness.Writer.Advanced);
        Assert.Equal("Failed", harness.Writer.Completed!.Value.Status);
    }

    /// <summary>
    /// Lookup lists that failed without a fatal outcome downgrade the run and <b>do not</b> hold the
    /// watermark.
    /// </summary>
    /// <remarks>
    /// <b>[R41] The watermark half of this test is inverted from what it asserted, and the inversion is the
    /// point.</b> It used to require the hold, on the reasoning that a code EPA has withdrawn is still live in
    /// this mirror and advancing would skip the range that would have corrected it. That reasoning does not
    /// survive contact with EPA: the lookup stage runs on every run <i>regardless</i> of the watermark, so
    /// holding the bookmark never refreshed anything — and EPA answers <c>200</c> with <c>[]</c> for two
    /// state-scoped lists, an answer this loader must refuse and retrying reproduces exactly. The hold
    /// therefore became permanent, and a permanent hold on a range that grows daily is unbounded cost for no
    /// correction. The verdict still records the shortfall, which is what this test now pins: the run reports
    /// <see cref="LoadRunOutcome.PartiallySucceeded"/> <i>and</i> banks its range.
    /// </remarks>
    [Fact]
    public async Task LookupListsThatFailedDowngradeTheRunButDoNotHoldTheWatermark()
    {
        Harness harness = Harness.For([First]);
        harness.Lookups = Ran.Lookups(refreshed: 22, unrefreshed: 1);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
        Assert.Single(harness.Writer.MergedBatches);
        Assert.Equal("PartiallySucceeded", harness.Writer.Completed!.Value.Status);

        // The range IS banked, and no hold warning is written at all -- the absence matters as much as the
        // advance, because a warning naming a hold that did not happen is the defect this pair replaced.
        Assert.NotNull(harness.Writer.Advanced);
        Assert.NotNull(result.WatermarkAdvancedTo);
        Assert.DoesNotContain(
            harness.RunLog.Entries,
            entry => entry.Message.Contains("watermark was NOT advanced", StringComparison.Ordinal));
    }

    /// <summary>
    /// The watermark-hold warning names the condition that actually held it, and does not name the ones that
    /// were satisfied.
    /// </summary>
    /// <remarks>
    /// <b>This is a regression test for a message, and the message was wrong in production.</b> The hold rule
    /// is a conjunction of independent conditions, and the warning used to report only the walk's window
    /// counts — so runs 2621 and 2622, which held because code lists did not refresh, reported
    /// <i>"0 of 12 window(s) went uncovered"</i> and <i>"0 of 77 window(s) went uncovered"</i>. Arithmetically
    /// true, causally false, and it sends whoever reads it to inspect the one stage that worked.
    /// <para>
    /// <b>[R41] The cause used here is a failed version rather than a stale code list</b>, because the code
    /// lists were removed from the rule in the same revision that fixed the message — a test whose hold cause
    /// no longer holds would assert nothing. The two surviving conditions are the ones a retry can clear, and
    /// those are exactly the ones worth naming.
    /// </para>
    /// <para>
    /// Asserting the <i>absence</i> of the window clause matters as much as the presence of the version one:
    /// a message that named every condition unconditionally would pass a presence-only test while being
    /// exactly as misleading as the message it replaced. This is Revision 25's lesson applied to a warning
    /// rather than to a detector — <b>assert what it reports, not that it reported</b>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheWatermarkHoldWarningNamesTheConditionThatHeldIt()
    {
        Harness harness = Harness.For([First]);
        harness.Answer = _ => Ran.NotFetched(First, ApiFetchOutcome.ServiceFailure, 503);

        await harness.RunAsync();

        string held = Assert.Single(
            harness.RunLog.Entries,
            entry => entry.Message.Contains("watermark was NOT advanced", StringComparison.Ordinal))
            .Message;

        Assert.Contains("1 version(s) failed", held, StringComparison.Ordinal);
        Assert.DoesNotContain("window(s) went uncovered", held, StringComparison.Ordinal);
        Assert.DoesNotContain("code list", held, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stale code list is never named as a reason the watermark was held, because it is no longer one.
    /// </summary>
    /// <remarks>
    /// <b>[R41] Pins the removal itself, and not merely its consequence.</b>
    /// <see cref="LookupListsThatFailedDowngradeTheRunButDoNotHoldTheWatermark"/> proves the range is banked
    /// when only the lookups are short; this proves the reverse direction that a future edit could get wrong
    /// independently — a run held for a real reason must not go on to blame the code lists as well. Without it,
    /// re-adding the lookup clause to the reason string would break no test while sending the operator back to
    /// the stage that cannot be fixed by retrying.
    /// </remarks>
    [Fact]
    public async Task AStaleCodeListIsNeverNamedInTheHoldReason()
    {
        Harness harness = Harness.For([First]);
        harness.Lookups = Ran.Lookups(refreshed: 22, unrefreshed: 1);
        harness.Answer = _ => Ran.NotFetched(First, ApiFetchOutcome.ServiceFailure, 503);

        await harness.RunAsync();

        string held = Assert.Single(
            harness.RunLog.Entries,
            entry => entry.Message.Contains("watermark was NOT advanced", StringComparison.Ordinal))
            .Message;

        Assert.Contains("1 version(s) failed", held, StringComparison.Ordinal);
        Assert.DoesNotContain("code list", held, StringComparison.Ordinal);
        Assert.DoesNotContain("lookup", held, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two conditions holding at once are both named, rather than the first one found.
    /// </summary>
    /// <remarks>
    /// <b>Because the operator who fixes one needs to know about the other before believing the next run will
    /// advance.</b> A message reporting the first failed condition would be correct and would still cost a
    /// second run to discover the rest — which on a catch-up spanning years is hours, not minutes.
    /// </remarks>
    [Fact]
    public async Task EveryConditionHoldingTheWatermarkIsNamed()
    {
        Harness harness = Harness.For([First]);
        harness.Walk = new StubSummaryWalk(
            harness.Log,
            Ran.Walk([Ran.Summary(First.HandlerId)], unwalkedWindows: 1));

        harness.Answer = _ => Ran.NotFetched(First, ApiFetchOutcome.ServiceFailure, 503);

        await harness.RunAsync();

        string held = Assert.Single(
            harness.RunLog.Entries,
            entry => entry.Message.Contains("watermark was NOT advanced", StringComparison.Ordinal))
            .Message;

        Assert.Contains("window(s) went uncovered", held, StringComparison.Ordinal);
        Assert.Contains("1 version(s) failed", held, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fatal walk outcome still enumerates the versions the walk did name, and fetches none of them.
    /// </summary>
    /// <remarks>
    /// <b>The enumerate is the point.</b> A failed window named no versions, so there is nothing this run
    /// could fetch — but the windows that succeeded named real ones, and enumerating them is what lets the
    /// next run resume from this one instead of starting over.
    /// </remarks>
    [Fact]
    public async Task AFatalWalkOutcomeEnumeratesWhatItNamedAndFetchesNothing()
    {
        Harness harness = Harness.For([First, Second]);
        harness.Walk = new StubSummaryWalk(
            harness.Log,
            Ran.Walk(
                [Ran.Summary(First.HandlerId), Ran.Summary(Second.HandlerId)],
                walkedWindows: 1,
                unwalkedWindows: 1,
                fatal: ApiFetchOutcome.AccessDenied));

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(2, harness.Journal.Enumerated.Count);
        Assert.Empty(harness.Client.RequestedUris);
        Assert.Empty(harness.Writer.MergedBatches);
        Assert.Null(harness.Writer.Advanced);
        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
    }

    /// <summary>
    /// A fatal fetch outcome stops the loop and leaves the rest for the next run.
    /// </summary>
    /// <remarks>
    /// A rejected credential, a denied scope or a request EPA will not accept does not become correct on the
    /// next handler, so continuing would spend a night's requests proving the same thing 300,000 times.
    /// </remarks>
    [Fact]
    public async Task AFatalFetchOutcomeStopsTheLoopAndLeavesTheRestForTheNextRun()
    {
        Harness harness = Harness.For([First, Second, Third]);
        harness.Options.FetchBatchSize = 1;
        harness.Answer = request =>
            request.RelativeUri.Contains(Second.HandlerId, StringComparison.Ordinal)
                ? Ran.NotFetched(Second, ApiFetchOutcome.Unauthorized, 401)
                : Ran.Fetched(First);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(2, harness.Client.RequestedUris.Count);
        Assert.Single(harness.Writer.MergedBatches);
        Assert.Null(harness.Writer.Advanced);
        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
        Assert.Contains("Unauthorized", result.FailureMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>404</c> from the source endpoint becomes a soft delete, not a failure.
    /// </summary>
    /// <remarks>
    /// AR7, and the reason has never been the status code: <see cref="ApiFetchResult.IsGone"/> reads the
    /// classified outcome, and the classification is made against the endpoint's own documented status set —
    /// <c>/hd/sources</c> documents <c>404</c> and <c>/hd/other-ids</c> does not.
    /// </remarks>
    [Fact]
    public async Task A404FromTheSourceEndpointBecomesASoftDeleteAndNotAFailure()
    {
        Harness harness = Harness.For([First, Second]);
        harness.Answer = request =>
            request.RelativeUri.Contains(Second.HandlerId, StringComparison.Ordinal)
                ? Ran.NotFetched(Second, ApiFetchOutcome.NotFound, 404)
                : Ran.Fetched(First);

        LoadRunResult result = await harness.RunAsync();

        Assert.Single(harness.Writer.SoftDeletes);
        Assert.Single(harness.Writer.SoftDeletes[0].Keys);
        Assert.Equal(1, result.Counters.SourceRecordsSoftDeleted);
        Assert.Equal(0, result.Counters.SourceRecordsFailed);

        // A withdrawn version is not a gap, so the range is fully accounted for and the bookmark moves.
        Assert.Equal(LoadRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(Ran.To, result.WatermarkAdvancedTo);
    }

    /// <summary>
    /// The soft-delete reason names the run and carries nothing else.
    /// </summary>
    /// <remarks>
    /// Script 522 logs <c>@Reason</c> by name (AR8), and the monitoring web application can read what it
    /// writes — so the reason may carry a run number and must not carry a request URI, which on this API is
    /// where the credential travels.
    /// </remarks>
    [Fact]
    public async Task TheSoftDeleteReasonNamesTheRunAndNoUri()
    {
        Harness harness = Harness.For([First]);
        harness.Answer = _ => Ran.NotFetched(First, ApiFetchOutcome.NotFound, 404);

        await harness.RunAsync();

        string reason = harness.Writer.SoftDeletes[0].Reason;

        Assert.Contains("404", reason, StringComparison.Ordinal);
        Assert.Contains("42", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("http", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/hd/", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(First.HandlerId, reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>200</c> whose body is not a single object is refused, counted as failed, and not merged.
    /// </summary>
    /// <remarks>
    /// <b>Taking element zero of an array would be the tempting reading and the dangerous one.</b> It would
    /// discard every other element while recording the version as loaded, and the resume read would then skip
    /// it forever. The refusal names the JSON value kind and never any part of the body (AR8).
    /// </remarks>
    /// <param name="body">What EPA answered with.</param>
    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"handlerId":"MDD000000001"}]""")]
    [InlineData("\"a string\"")]
    [InlineData("not json at all")]
    public async Task A200WhoseBodyIsNotOneObjectIsRefusedRatherThanGuessedAt(string body)
    {
        Harness harness = Harness.For([First]);
        harness.Answer = _ => Ran.Fetched(First, body);

        LoadRunResult result = await harness.RunAsync();

        Assert.Empty(harness.Writer.MergedBatches);
        Assert.Equal(1, result.Counters.SourceRecordsFailed);
        Assert.Equal(0, result.Counters.SourceRecordsFetched);

        // Recorded as a failure rather than as the success EPA's status line claimed.
        Assert.Equal("Failed", harness.Journal.Concluded[0].Result.Outcome.ToAttemptOutcome());
        Assert.Null(harness.Writer.Advanced);
        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
    }

    /// <summary>
    /// The refusal message names the shape and reproduces no part of the body.
    /// </summary>
    [Fact]
    public async Task TheRefusalMessageNamesTheShapeAndNotTheBody()
    {
        const string Body = """[{"handlerId":"MDD000000001","contactEmail":"someone@example.com"}]""";

        Harness harness = Harness.For([First]);
        harness.Answer = _ => Ran.Fetched(First, Body);

        await harness.RunAsync();

        string? message = harness.Journal.Concluded[0].Result.FailureMessage;

        Assert.NotNull(message);
        Assert.Contains("Array", message, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", message, StringComparison.Ordinal);
        Assert.DoesNotContain("contactEmail", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version that failed holds the watermark entirely, and a clean run moves it to the end of the range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stricter than "the run worked", on purpose.</b> The watermark decides whether a date is ever asked
    /// for again, and a failed version is recoverable from nowhere else: the next scheduled run walks a later
    /// range and never names it, and the resume read reports it as vanished rather than fetching it. Holding
    /// the bookmark costs one re-walk whose successes the resume read then skips; advancing it loses the
    /// record permanently, in a table nothing will ever ask about again.
    /// </para>
    /// <para>
    /// <b>A failed version holds it <i>entirely</i> rather than trimming it, and that asymmetry with a failed
    /// window is deliberate.</b> A window can be trimmed to because the walk report says which windows landed,
    /// in date order; a failed version cannot be attributed to a window at all —
    /// <c>SummaryWindowReport</c> carries counts, not version lists. So the two cases below that involve a
    /// failed version move nothing, and the missed-window cases are
    /// <see cref="TheWatermarkBanksTheWalkedPrefixWhenALaterWindowIsMissed"/> and
    /// <see cref="TheWatermarkDoesNotMoveAtAllWhenTheFirstWindowIsMissed"/>.
    /// </para>
    /// </remarks>
    /// <param name="label">Which case, so each gets its own test name.</param>
    /// <param name="unwalkedWindows">Windows the walk did not cover.</param>
    /// <param name="fetchOutcome">What the one version's fetch returned.</param>
    /// <param name="shouldMove">Whether the bookmark may move.</param>
    [Theory]
    [InlineData("clean", 0, ApiFetchOutcome.Succeeded, true)]
    [InlineData("a version failed", 0, ApiFetchOutcome.ServiceFailure, false)]
    [InlineData("both", 1, ApiFetchOutcome.TimedOut, false)]
    public async Task TheWatermarkMovesOnlyWhenTheWholeRangeIsAccountedFor(
        string label,
        int unwalkedWindows,
        ApiFetchOutcome fetchOutcome,
        bool shouldMove)
    {
        Assert.NotEmpty(label);

        Harness harness = Harness.For([First]);
        harness.Walk = new StubSummaryWalk(
            harness.Log,
            Ran.Walk([Ran.Summary(First.HandlerId)], unwalkedWindows: unwalkedWindows));

        harness.Answer = _ => fetchOutcome == ApiFetchOutcome.Succeeded
            ? Ran.Fetched(First)
            : Ran.NotFetched(First, fetchOutcome, 503);

        LoadRunResult result = await harness.RunAsync();

        if (shouldMove)
        {
            Assert.Equal(
                (LoadRun.HandlerSourceFeed, "MD", Ran.To, 42),
                harness.Writer.Advanced);

            Assert.Equal(Ran.To, result.WatermarkAdvancedTo);
            Assert.Equal(LoadRunOutcome.Succeeded, result.Outcome);
        }
        else
        {
            Assert.Null(harness.Writer.Advanced);
            Assert.Null(result.WatermarkAdvancedTo);
            Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
        }
    }

    /// <summary>
    /// An unreconciled lineage downgrades the run and — alone among the five conditions — lets the bookmark
    /// move anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The §D4 decision, asserted as the pair of facts it is: not <c>Succeeded</c>, and still advanced.</b>
    /// Every other reason a run is downgraded also holds the watermark, so the temptation was to make this one
    /// match. The criterion this codebase uses is not severity, it is <i>discoverability</i>. An advanced
    /// watermark over an unwalked window is dangerous because the gap becomes permanent and nothing downstream
    /// can notice it — <c>/hd/sources/summaries</c> has no envelope and no paging, so the versions that window
    /// would have named are simply never mentioned again. A stale <c>CurrentRecord</c> flag is the opposite on
    /// both counts: one <c>GROUP BY HandlerId, SourceType HAVING SUM(CASE WHEN CurrentRecord = 1 …) &gt; 1</c>
    /// finds it — that query is how <c>MDR000501742/N</c> was found — and one targeted run on that handler
    /// alone repairs it, with no date range involved.
    /// </para>
    /// <para>
    /// Holding the bookmark here would trade a visible, repairable flag for an unloadable date range, and worse:
    /// a handler EPA persistently will not answer for would stall the entire feed forever, because every later
    /// run would re-walk the same windows and fail on the same handler.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnUnreconciledLineageDowngradesTheRunButDoesNotHoldTheWatermark()
    {
        Harness harness = Harness.For([First]);
        harness.Summaries = _ => Ran.Enumerated(
            First.HandlerId, outcome: ApiFetchOutcome.ServiceFailure, httpStatusCode: 503);

        LoadRunResult result = await harness.RunAsync();

        // Downgraded, and the reason is named.
        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
        Assert.Equal(0, result.Reconcile!.ReconciledCount);
        Assert.Contains("CurrentRecord", result.FailureMessage!, StringComparison.Ordinal);

        // And the bookmark moved regardless, which is the half of this that is easy to get wrong.
        Assert.Equal((LoadRun.HandlerSourceFeed, "MD", Ran.To, 42), harness.Writer.Advanced);
        Assert.Equal(Ran.To, result.WatermarkAdvancedTo);

        // The version itself still merged: the reconciliation is a separate assertion about the lineage, not a
        // gate on the data.
        Assert.Single(harness.Writer.MergedBatches);
        Assert.Equal(1, result.Counters.SourceRecordsInserted);
    }

    /// <summary>
    /// A cancelled reconciliation does reach the run's verdict, because cancellation is a fact about the run.
    /// </summary>
    /// <remarks>
    /// The one exception to the paragraph above, and the reason it is an exception rather than an inconsistency:
    /// a stale flag is a statement about one lineage, while a cancellation is a statement about how much of the
    /// run happened at all. Every cancelled run holds its watermark, and a run cancelled during the
    /// reconciliation was cancelled.
    /// </remarks>
    [Fact]
    public async Task ACancelledReconciliationCancelsTheRunAndHoldsTheWatermark()
    {
        Harness harness = Harness.For([First]);
        harness.Summaries = _ => Ran.Enumerated(First.HandlerId, outcome: ApiFetchOutcome.Cancelled);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(LoadRunOutcome.Cancelled, result.Outcome);
        Assert.Null(harness.Writer.Advanced);
        Assert.Null(result.WatermarkAdvancedTo);

        // Still closed, and still flushed. An unclosed run is indistinguishable from one still going.
        Assert.NotNull(harness.Writer.Completed);
    }

    /// <summary>
    /// Every handler the walk NAMED is reconciled, not only the ones whose payload changed.
    /// </summary>
    /// <remarks>
    /// <b>The trigger set is a superset on purpose.</b> The version whose flag is now wrong is a <i>sibling</i>
    /// of the one that changed: EPA adding version 13 is what demotes version 12, and version 12 is not in the
    /// delta feed because it did not change. So a handler whose fetch came back <c>Unchanged</c> — or whose
    /// version this run skipped entirely — can still be the one holding two current flags. Selecting on "the
    /// merge reported a change" would miss exactly that case, and it is the common one.
    /// </remarks>
    [Fact]
    public async Task EveryHandlerTheWalkNamedIsReconciledEvenWhenNothingAboutItChanged()
    {
        Harness harness = Harness.For([First, Second]);

        // Nothing changed: the merge classifies both as unchanged.
        harness.Writer.Merge = envelopes => new MergeCounts(0, 0, envelopes.Count);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(2, result.Counters.SourceRecordsUnchanged);
        Assert.Equal(0, result.Counters.SourceRecordsInserted);

        // Both lineages asserted anyway.
        Assert.Equal(2, result.Reconcile!.ReconciledCount);

        Assert.Equal(
            [First.HandlerId, Second.HandlerId],
            harness.Writer.ReconciledBatches
                .SelectMany(batch => batch)
                .Select(element => element.HandlerId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        Assert.Equal(LoadRunOutcome.Succeeded, result.Outcome);
    }

    /// <summary>
    /// A missed window stops the bookmark at the last window before it, rather than holding it at the start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what lets a catch-up spanning years finish across several runs.</b> Nothing guarantees the
    /// scheduled task ran, so a missed week, a month of a disabled action and a first load covering the whole
    /// notification era all arrive as one wide range — thousands of windows for the last of those. An
    /// all-or-nothing watermark means one transient <c>500</c> in hour six costs the whole night, every night,
    /// and the load never converges.
    /// </para>
    /// <para>
    /// <b>The run is still <c>PartiallySucceeded</c>, and both facts are correct at once.</b> The bookmark
    /// moved and the range was not covered — which is the combination
    /// <c>LoadRunLog.WatermarkAdvancedPartially</c> exists to make legible, because a single line saying
    /// "advanced" would bury it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheWatermarkBanksTheWalkedPrefixWhenALaterWindowIsMissed()
    {
        Harness harness = Harness.For([First]);

        // Windows 0 and 1 land, window 2 does not. Ran.Walk lays them out contiguously from Ran.From at the
        // default seven-day width, so the prefix ends at the last day of window 1.
        harness.Walk = new StubSummaryWalk(
            harness.Log,
            Ran.Walk([Ran.Summary(First.HandlerId)], walkedWindows: 2, unwalkedWindows: 1));

        harness.Answer = _ => Ran.Fetched(First);

        LoadRunResult result = await harness.RunAsync();

        DateOnly prefixEnd = Ran.From.AddDays(13);

        Assert.Equal((LoadRun.HandlerSourceFeed, "MD", prefixEnd, 42), harness.Writer.Advanced);
        Assert.Equal(prefixEnd, result.WatermarkAdvancedTo);

        // Not Succeeded: a window went uncovered, so the days from it onward are asked for again -- including
        // the ones after it that this run did walk.
        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
    }

    /// <summary>
    /// When the <i>first</i> window is the one that failed, there is no prefix and nothing moves.
    /// </summary>
    /// <remarks>
    /// <b>The boundary case, and the one that would be a permanent hole if it were wrong.</b> There is no
    /// walked window before the gap, so no day can be given up — and <c>/hd/sources/summaries</c> has no
    /// envelope, so nothing in any later response would ever reveal the missing range. The version the walk
    /// did name is still fetched and still merged: refusing to move the bookmark is not refusing to work.
    /// </remarks>
    [Fact]
    public async Task TheWatermarkDoesNotMoveAtAllWhenTheFirstWindowIsMissed()
    {
        Harness harness = Harness.For([First]);

        harness.Walk = new StubSummaryWalk(
            harness.Log,
            Ran.Walk([Ran.Summary(First.HandlerId)], walkedWindows: 0, unwalkedWindows: 1));

        harness.Answer = _ => Ran.Fetched(First);

        LoadRunResult result = await harness.RunAsync();

        Assert.Null(harness.Writer.Advanced);
        Assert.Null(result.WatermarkAdvancedTo);

        // The version was still fetched, which is the half that is easy to lose while fixing the other.
        Assert.Equal(1, result.Counters.SourceRecordsFetched);
        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
    }

    /// <summary>
    /// The final flush and the closing write are issued on a token that cannot be cancelled.
    /// </summary>
    /// <remarks>
    /// <b>Asserted in the argument because it is not observable from the result.</b> A cancelled
    /// <c>CompleteRunAsync</c> and one that was never reached both leave <c>logs.LoadRun</c> at
    /// <c>Running</c>, and buffered status rows lost at shutdown are invisible loss in the one table that
    /// exists to make loss visible. The fetch's own token <i>is</i> cancellable, which is what makes this a
    /// distinction rather than a blanket.
    /// </remarks>
    [Fact]
    public async Task NeitherTheFlushNorTheClosingWriteIsCancellable()
    {
        Harness harness = Harness.For([First]);

        using CancellationTokenSource cancellation = new();

        await harness.RunAsync(cancellation.Token);

        Assert.All(
            harness.Journal.Tokens,
            entry => Assert.False(entry.Cancellable, entry.Call));

        Assert.Contains(("CompleteRun", false), harness.Writer.Tokens);
        Assert.Contains(("Merge(1)", false), harness.Writer.Tokens);
        Assert.Contains(("AdvanceWatermark", false), harness.Writer.Tokens);

        // The two reads that precede the run are cancellable, so this is a deliberate line and not an
        // accident of never passing the token anywhere.
        Assert.Contains(("ReadWatermark", true), harness.Writer.Tokens);
        Assert.Contains(("StartRun", true), harness.Writer.Tokens);
    }

    /// <summary>
    /// A cancelled fetch still flushes, still closes the run, and reports itself as cancelled.
    /// </summary>
    /// <remarks>
    /// <c>logs.LoadRun.Status</c> has no <c>Cancelled</c> and inventing one would be a CHECK change for a
    /// distinction the row does not need — <c>Abandoned</c> means a later run found this one dead, which is
    /// not what happened. So the row records whether any of it worked and
    /// <see cref="LoadRunOutcome.Cancelled"/> keeps the distinction where the exit code needs it.
    /// </remarks>
    [Fact]
    public async Task ACancelledFetchStillFlushesAndStillClosesTheRun()
    {
        Harness harness = Harness.For([First]);
        harness.Answer = _ => Ran.NotFetched(First, ApiFetchOutcome.Cancelled);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(LoadRunOutcome.Cancelled, result.Outcome);
        Assert.Equal("Failed", harness.Writer.Completed!.Value.Status);
        Assert.True(harness.Journal.Flushes > 0);
        Assert.True(harness.Journal.Disposed);
        Assert.Null(harness.Writer.Advanced);
    }

    /// <summary>
    /// A cancelled run that already merged something records <c>PartiallySucceeded</c>.
    /// </summary>
    [Fact]
    public async Task ACancelledRunThatLandedDataRecordsPartialRatherThanFailed()
    {
        Harness harness = Harness.For([First, Second]);
        harness.Options.FetchBatchSize = 1;
        harness.Answer = request =>
            request.RelativeUri.Contains(Second.HandlerId, StringComparison.Ordinal)
                ? Ran.NotFetched(Second, ApiFetchOutcome.Cancelled)
                : Ran.Fetched(First);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(LoadRunOutcome.Cancelled, result.Outcome);
        Assert.Equal("PartiallySucceeded", harness.Writer.Completed!.Value.Status);
        Assert.True(result.WroteData);
    }

    /// <summary>
    /// Every version the walk named is enumerated, including the ones a previous run already finished.
    /// </summary>
    /// <remarks>
    /// Script 520's <c>Skip</c> mode UPDATEs and never INSERTs, so a version skipped without being
    /// enumerated leaves no row at all — and script 524 would then count any attempt against it as an
    /// orphan.
    /// </remarks>
    [Fact]
    public async Task EveryVersionIsEnumeratedBeforeAnyIsSkipped()
    {
        Harness harness = Harness.For([First, Second]);
        harness.Point = new LoadResumePoint(
            100,
            "Incremental",
            "Abandoned",
            new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.Zero),
            Ran.From,
            Ran.To,
            new HashSet<HandlerVersion> { First },
            new HashSet<HandlerVersion>());

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(2, harness.Journal.Enumerated.Count);
        Assert.Equal([First], harness.Journal.Skipped);
        Assert.True(
            harness.Log.IndexOf("Enumerate") < harness.Log.IndexOf("Skip"),
            harness.Log.ToString());

        // Only the unfinished one is asked for, and script 510 was told what this resumed from. The payload
        // fetches alone -- the reconcile asks the summaries endpoint about BOTH handlers, including the
        // skipped one, and that is deliberate: a version this run declined to fetch can still hold a stale
        // CurrentRecord flag from an earlier one.
        Assert.Single(harness.Client.VersionUris);
        Assert.Equal(100, harness.Writer.StartRequest!.ResumedFromLoadRunId);
        Assert.Equal(1, result.Counters.SourceRecordsSkipped);
        Assert.Equal(2, result.Counters.SourceRecordsEnumerated);
    }

    /// <summary>
    /// The journal is created for the run the database opened, not for a number this loader chose.
    /// </summary>
    [Fact]
    public async Task TheJournalBelongsToTheRunTheDatabaseOpened()
    {
        Harness harness = Harness.For([First]);
        harness.Writer.LoadRunId = 7;

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(7, harness.Journal.CreatedForLoadRunId);
        Assert.Equal(7, result.LoadRunId);
        Assert.Equal(7, harness.Writer.Completed!.Value.LoadRunId);
    }

    /// <summary>
    /// The batch size is capped by the writer's element limit rather than by a copy of it.
    /// </summary>
    /// <remarks>
    /// The ceiling belongs to whatever validates the table-valued parameter, and duplicating it as a second
    /// setting would be two numbers that have to agree.
    /// </remarks>
    [Fact]
    public async Task TheWritersElementLimitCapsTheConfiguredBatchSize()
    {
        Harness harness = Harness.For([First, Second, Third]);
        harness.Options.FetchBatchSize = 25;
        harness.Writer.MaxElementsPerCall = 2;

        await harness.RunAsync();

        Assert.Equal([2, 1], harness.Writer.MergedBatches.Select(batch => batch.Count));
    }

    /// <summary>
    /// The HTTP request count comes from the pacer and the retry count is derived from it.
    /// </summary>
    /// <remarks>
    /// <b>The pacer is the only honest source.</b> Retries happen in the resilience pipeline below this
    /// class, so one logical fetch can be several requests and the loader cannot see it. The difference
    /// between the pacer's reservations and the calls this loader made is therefore the retry count —
    /// derived, clamped at zero, and documented as a derivation rather than a measurement.
    /// </remarks>
    [Fact]
    public async Task TheRetryCountIsDerivedFromThePacerAndIsNeverNegative()
    {
        Harness harness = Harness.For([First]);

        LoadRunResult result = await harness.RunAsync();

        // Nothing in this harness goes through the pacing handler, so the reservation delta is zero while
        // the loader made 25 logical calls. The derivation must clamp rather than report -25.
        Assert.Equal(0, result.Counters.HttpRequestCount);
        Assert.Equal(0, result.Counters.HttpRetryCount);
    }

    /// <summary>
    /// The counters report what the merge said, and <c>Unchanged</c> is carried rather than recomputed.
    /// </summary>
    [Fact]
    public async Task TheCountersReportWhatTheMergeSaid()
    {
        Harness harness = Harness.For([First, Second, Third]);
        harness.Writer.Merge = _ => new MergeCounts(1, 1, 1);

        LoadRunResult result = await harness.RunAsync();

        Assert.Equal(1, result.Counters.SourceRecordsInserted);
        Assert.Equal(1, result.Counters.SourceRecordsUpdated);
        Assert.Equal(1, result.Counters.SourceRecordsUnchanged);
        Assert.Equal(3, result.Counters.SourceRecordsFetched);
        Assert.Equal(23, result.Counters.LookupListsRefreshed);
    }

    /// <summary>
    /// <c>other-ids</c> is never requested, because there is nowhere to put the answer.
    /// </summary>
    /// <remarks>
    /// <b>A named gap kept as an assertion rather than a comment.</b> <c>dbo.HandlerOtherIdentifier</c>
    /// (script 150) has a table and the client has an endpoint, and there is no write procedure between
    /// them. Fetching the payload anyway would roughly double the initial load's request count (G14, [R8])
    /// to produce data that is then discarded, and the run would report success. When the write path lands,
    /// this test is the one that has to change.
    /// </remarks>
    [Fact]
    public async Task OtherIdsIsNeverFetchedBecauseThereIsNoWritePathForIt()
    {
        Harness harness = Harness.For([First, Second]);

        await harness.RunAsync();

        Assert.All(
            harness.Client.RequestedUris,
            uri => Assert.DoesNotContain("other-ids", uri, StringComparison.OrdinalIgnoreCase));

        // One payload fetch per version and nothing besides. Counted over the payload fetches alone, because
        // the reconcile's per-handler summaries call is §D4's cost and not the fetch loop's -- this test is
        // about the request the loader must NOT make, and folding the two counts together would make it fail
        // the next time another stage legitimately asks EPA something.
        Assert.Equal(2, harness.Client.VersionUris.Count);
    }

    /// <summary>
    /// Unusable options throw rather than opening a run, because that is a loader defect and not a load
    /// failure.
    /// </summary>
    /// <remarks>
    /// An empty <c>ActivityLocation</c> is the one configuration defect that produces a successful
    /// <i>national</i> load rather than an error, so it must not be reachable — and reporting it as a failed
    /// run would put a row in <c>logs.LoadRun</c> blaming EPA for a configuration file.
    /// </remarks>
    [Fact]
    public async Task UnusableOptionsThrowAndOpenNoRun()
    {
        Harness harness = Harness.For([First]);
        harness.Options.ActivityLocation = string.Empty;

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync());

        Assert.Contains("ActivityLocation", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Log.Entries);
        Assert.Null(harness.Writer.StartRequest);
    }

    /// <summary>
    /// A journal defect is reported and does not change the run's verdict.
    /// </summary>
    /// <remarks>
    /// An orphaned row means an attempt was recorded for a version this run never enumerated, and a withheld
    /// value means a <c>RequestPath</c> reached script 524 without a leading slash (AR8). Both are loader
    /// defects; neither is a reason to disbelieve the handler data that landed.
    /// </remarks>
    [Fact]
    public async Task AJournalDefectIsReportedWithoutChangingTheVerdict()
    {
        Harness harness = Harness.For([First]);
        harness.Journal.FlushResult = new LoadJournalFlush(1, 1, 0, 0, 1, 1, 1, 1);

        LoadRunResult result = await harness.RunAsync();

        Assert.True(result.Journal.HasDefects);
        Assert.Equal(LoadRunOutcome.Succeeded, result.Outcome);
    }

    /// <summary>
    /// The result's one-line summary carries counts and no credential, and never an exception's text.
    /// </summary>
    [Fact]
    public async Task TheRunSummaryCarriesCountsAndNoUri()
    {
        Harness harness = Harness.For([First]);

        LoadRunResult result = await harness.RunAsync();

        string summary = result.ToString();

        Assert.Contains("run=42", summary, StringComparison.Ordinal);
        Assert.Contains("watermark=2026-09-05", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("http", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(First.HandlerId, summary, StringComparison.Ordinal);
    }
}

