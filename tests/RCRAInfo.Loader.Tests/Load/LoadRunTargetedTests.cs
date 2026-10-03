using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// <see cref="LoadRun.RunTargetedAsync"/>: the single-handler run — what it does, and the four stages it
/// deliberately does not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Most of what is asserted here is an absence, and that is not a weak assertion — it is the only kind
/// available.</b> A targeted run that read the watermark would still work; a targeted run that moved it would
/// still report success. The damage lands on the <i>next</i> scheduled load, which would skip days this run
/// never asked EPA for and could never notice, because <c>/hd/sources/summaries</c> carries no envelope. So
/// "AdvanceWatermark is not in the call log" is the only place that defect is observable at all.
/// </para>
/// <para>
/// <b>The other half is that nothing landing is a failure.</b> A handler that does not exist, one in another
/// state, and one EPA flags no current record for are all ordinary answers to an ordinary question — and a
/// targeted run is the one shape where "there was nothing to get" happens routinely, which makes it the one
/// shape where exit code 0 from a run that retrieved nothing could arrive by accident.
/// </para>
/// </remarks>
public sealed class LoadRunTargetedTests
{
    private const string Handler = "MDD000000001";

    /// <summary>
    /// The stages a targeted run runs, as a sequence — and the four names that are not in it.
    /// </summary>
    /// <remarks>
    /// <b>Each absence has its own reason and none of them is tidiness.</b> No watermark read, because a run
    /// covering no date range has nothing for one to recommend, and reading it would make a targeted run refuse
    /// itself on a disabled feed — the moment an operator most needs to ask EPA what it holds. No watermark
    /// move, because this run walked no window. No lookup refresh, because nothing in
    /// <c>dbo.HandlerSource</c> has a foreign key to a lookup table (G15's decision), so a missing code cannot
    /// fail the merge, and twenty-four requests to answer a question about one handler is waste. No resume,
    /// because an operator asking for a handler now wants it now.
    /// </remarks>
    [Fact]
    public async Task TheTargetedRunSkipsTheFourStagesItHasNoUseFor()
    {
        Harness harness = Harness.Targeted(Ran.Enumerated(Handler));

        await harness.RunTargetedAsync(new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        Assert.Equal(
            [
                "StartRun",
                "Enumerate(1)",

                // Flushed here, and a targeted run is where its absence bites hardest: one version never
                // approaches FlushRowCount, so without this NOTHING had been written when script 400 went
                // looking for the status row to mark Succeeded. Measured against EPA on 2026-09-06 —
                // runs 2139 and 2140 both closed Succeeded with the handler stuck at InProgress.
                "Flush",

                "RecordAttempt",
                "Conclude",

                // And again above the merge, for the same reason one level down: run 2141 had the status row
                // written by the flush above and the attempt row still buffered, so script 400 marked the row
                // Succeeded first and script 520 -- which excludes Succeeded rows from Attempt mode -- then
                // refused the count. Status right, AttemptCount 0. Run 2142 with this order had both.
                "Flush",

                "Merge(1)",

                // The CurrentRecord reconciliation, and on this path it costs no request: the enumeration
                // above already returned the handler's COMPLETE version list, which is precisely what script
                // 521 demands, so the stage is handed what was read rather than sent to ask again. Note it is
                // present even though the request named CurrentRecord scope and only one version was fetched
                // -- the lineage this repairs may have been wrong before this run started.
                "Reconcile(1)",

                "Flush",
                "CompleteRun",
                "DisposeJournal",
            ],
            harness.Log.Entries);

        Assert.DoesNotContain("ReadWatermark", harness.Log.Entries);
        Assert.DoesNotContain("AdvanceWatermark", harness.Log.Entries);
        Assert.DoesNotContain("RefreshLookups", harness.Log.Entries);
        Assert.DoesNotContain("ReadResume", harness.Log.Entries);
        Assert.DoesNotContain("Walk", harness.Log.Entries);

        Assert.False(harness.Resume.WasAsked);
        Assert.Null(harness.Walk.Asked);
        Assert.Null(harness.Writer.Advanced);
    }

    /// <summary>
    /// The run's own row says <c>Targeted</c>, names no dates, and asks to run beside the scheduled load.
    /// </summary>
    /// <remarks>
    /// <b>Three separate facts, and each is read by a different procedure.</b> The mode is what script 525
    /// filters on so a succeeded diagnostic cannot displace a genuine abandoned run as the resume candidate,
    /// and what script 510 exempts from the in-flight refusal in both directions. The three null dates are what
    /// tell a later reader this row cannot have moved the bookmark. <c>AllowConcurrent</c> is belt and braces:
    /// the exemption is what stops an interrupted targeted run refusing the nightly load, and the flag is what
    /// an older deployment of 510 would honour.
    /// </remarks>
    [Fact]
    public async Task TheRunRowIsTargetedCarriesNoDatesAndDoesNotWaitForTheScheduledLoad()
    {
        Harness harness = Harness.Targeted(Ran.Enumerated(Handler));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        LoadRunRequest opened = Assert.IsType<LoadRunRequest>(harness.Writer.StartRequest);

        Assert.Equal("Targeted", opened.RunMode);
        Assert.Equal("MD", opened.ActivityLocation);
        Assert.True(opened.AllowConcurrent);
        Assert.Null(opened.RequestedFromDate);
        Assert.Null(opened.RequestedToDate);
        Assert.Null(opened.WatermarkBeforeDate);

        Assert.Equal("Targeted", result.RunMode);
        Assert.Equal(42, result.LoadRunId);
        Assert.Null(result.WatermarkAdvancedTo);
        Assert.Null(result.Lookups);
        Assert.Null(result.Walk);
    }

    /// <summary>
    /// <c>--current-record</c> fetches the one version EPA flags, out of a handler with a history.
    /// </summary>
    [Fact]
    public async Task TheCurrentRecordScopeFetchesOnlyTheVersionEpaFlags()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(
                Handler,
                Ran.SummariesBody(Handler, "MD", (1, false), (2, false), (3, true))));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        HandlerLoadStatusElement enumerated = Assert.Single(harness.Journal.Enumerated);

        Assert.Equal(Handler, enumerated.HandlerId);
        Assert.Equal("N", enumerated.SourceType);
        Assert.Equal(3, enumerated.Sequence);
        Assert.Equal(LoadRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.Counters.SourceRecordsFetched);
        Assert.Equal(1, result.Counters.SourceRecordsEnumerated);

        // Two calls, not four: the enumeration and the one version. Asserted on the client rather than on
        // Counters.HttpRequestCount, because that counter comes from the pacer and these doubles bypass it.
        Assert.Equal(2, harness.Client.RequestedUris.Count);
    }

    /// <summary>
    /// <c>--every-version</c> fetches the entire history, including the versions EPA does not flag.
    /// </summary>
    /// <remarks>
    /// <b>This is the path that exists because a version's current flag is not a property of that version.</b>
    /// A new version flips its siblings' flags, and the siblings are not in the delta feed — so the whole
    /// history is the only answer that lets an operator see what this mirror should look like.
    /// </remarks>
    [Fact]
    public async Task TheEveryVersionScopeFetchesTheWholeHistoryIncludingTheUnflaggedVersions()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(
                Handler,
                Ran.SummariesBody(Handler, "MD", (1, false), (2, false), (3, true))));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.EveryVersion));

        Assert.Equal(
            [1, 2, 3],
            harness.Journal.Enumerated.Select(element => element.Sequence).Order());

        Assert.Equal(LoadRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, result.Counters.SourceRecordsFetched);
        Assert.Equal(4, harness.Client.RequestedUris.Count);
    }

    /// <summary>
    /// The reconciliation is handed everything the enumeration read, not the subset the scope selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one mistake script 521's contract punishes, asserted directly.</b> A <c>--current-record</c> run
    /// selects one version out of a history of three, and passing <i>that</i> to the procedure would tell it the
    /// handler's complete list is <c>[N/3]</c> — which is exactly the input that makes it set <c>N/1</c> and
    /// <c>N/2</c> to <c>CurrentRecord = 0</c> and record two <c>VersionNotInSourceSummary</c> observations. The
    /// versions the enumeration read are the complete list; the versions the scope selected are a filter over it,
    /// and the two look identical at the call site.
    /// </para>
    /// <para>
    /// <b>And it costs no request.</b> The targeted path already spent the one call that produced the list, so
    /// it hands over what it read instead of asking again — which is also why <c>Calls</c> stays at 0 and the run
    /// makes exactly two requests, unchanged by §D4.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheReconciliationGetsTheWholeEnumeratedLineageAndNotTheSelectedSubset()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(
                Handler,
                Ran.SummariesBody(Handler, "MD", (1, false), (2, false), (3, true))));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        // One version fetched -- and all three submitted.
        Assert.Equal(1, result.Counters.SourceRecordsFetched);

        IReadOnlyCollection<HandlerVersionElement> batch = Assert.Single(harness.Writer.ReconciledBatches);

        Assert.Equal([1, 2, 3], batch.Select(element => element.Sequence).Order());
        Assert.Equal([false, false, true], batch.Select(element => element.CurrentRecord));

        // No second call to the summaries endpoint: the enumeration's answer was reused.
        Assert.Equal(2, harness.Client.RequestedUris.Count);
        Assert.Equal(0, result.Reconcile!.Calls);
        Assert.Equal(LoadRunOutcome.Succeeded, result.Outcome);
    }

    /// <summary>
    /// A targeted run is the repair path, so an out-of-state or foreign answer downgrades it rather than
    /// reporting success.
    /// </summary>
    /// <remarks>
    /// This run's whole purpose may be to fix a lineage a scheduled run reported unreconciled, so a targeted run
    /// that could not assert the lineage must not close <c>Succeeded</c> — that is the exit code an operator
    /// reads to decide whether the repair worked.
    /// </remarks>
    [Fact]
    public async Task ATargetedRunThatCannotAssertTheLineageIsNotSucceeded()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(Handler, Ran.SummariesBody(Handler, "MD", (1, false), (2, true))));

        // The enumeration is in scope and the fetch proceeds; the reconciliation's own depth guard is what
        // refuses. A limit of one is the smallest lever that reaches only this stage -- the fetch and the merge
        // both batch happily at one element per call.
        harness.Writer.MaxElementsPerCall = 1;

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        Assert.Empty(harness.Writer.ReconciledBatches);
        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);

        // The version still merged: the flag is a separate assertion, not a gate on the data.
        Assert.Single(harness.Writer.MergedBatches);
    }

    /// <summary>
    /// Nothing is skipped on the strength of an older run, whichever scope was asked for.
    /// </summary>
    /// <remarks>
    /// <b>The resume read is not merely absent — the plan is <c>FetchAll</c>, and that is a second decision.</b>
    /// Skipping is what makes a resumed population load affordable; on a targeted run it would mean an operator
    /// investigating a handler was handed the row a previous run already had, which is the one answer the
    /// question was asked to get past. The cost is one request per version and a merge reporting
    /// <c>Unchanged</c>.
    /// </remarks>
    [Fact]
    public async Task NoVersionIsSkippedEvenWhenAnEarlierRunAlreadySucceededOnIt()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(Handler, Ran.SummariesBody(Handler, "MD", (1, true), (2, false))));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.EveryVersion));

        Assert.Empty(harness.Journal.Skipped);
        Assert.DoesNotContain("Skip(0)", harness.Log.Entries);
        Assert.Equal(0, result.Counters.SourceRecordsSkipped);
    }

    /// <summary>
    /// A readable answer that flags no current record fails, and the message says the highest sequence was
    /// not assumed.
    /// </summary>
    /// <remarks>
    /// <b>Guessing here would be invisible afterwards.</b> A guessed current record merges into
    /// <c>dbo.HandlerSource</c> indistinguishably from one EPA vouched for, and
    /// <c>dbo.uspReconcileCurrentRecord</c> would then reconcile <c>IsCurrentRecord</c> against the guess. The
    /// message names <c>--every-version</c> because that is the answer to the question the operator asked.
    /// </remarks>
    [Fact]
    public async Task NoFlaggedCurrentRecordFailsRatherThanTakingTheHighestSequence()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(Handler, Ran.SummariesBody(Handler, "MD", (1, false), (2, false))));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        Assert.Equal(LoadRunOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Writer.MergedBatches);
        Assert.Empty(harness.Journal.Enumerated);

        Assert.Contains("deliberately NOT assumed", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("2 summary row(s)", result.FailureMessage!, StringComparison.Ordinal);

        // Closed, not left Running. A diagnostic that finds nothing still has to release its own row.
        Assert.Equal("Failed", harness.Writer.Completed!.Value.Status);
    }

    /// <summary>
    /// A <c>200</c> with an empty array fails, which is the whole reason this verdict is computed separately.
    /// </summary>
    /// <remarks>
    /// <b>There is no failure anywhere in this run.</b> The request succeeded, the body parsed, the scope check
    /// passed and zero versions were named — so every signal the scheduled run's verdict weighs says success.
    /// <c>TargetedOutcome</c> looks at whether anything landed instead, because exit code 0 from a run that
    /// retrieved nothing is the failure mode the exit codes exist to prevent.
    /// </remarks>
    [Fact]
    public async Task AnEmptyAnswerFailsEvenThoughNothingWentWrong()
    {
        Harness harness = Harness.Targeted(Ran.Enumerated(Handler, "[]"));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.EveryVersion));

        Assert.Equal(LoadRunOutcome.Failed, result.Outcome);
        Assert.False(result.WroteData);
        Assert.Contains(Handler, result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("nothing to fetch", result.FailureMessage!, StringComparison.Ordinal);

        // One request and no more: with no version named there is nothing to ask for.
        Assert.Single(harness.Client.RequestedUris);
    }

    /// <summary>
    /// A <c>404</c> on the enumeration soft-deletes nothing, and the message says so in as many words.
    /// </summary>
    /// <remarks>
    /// <b>This is the most dangerous line in the targeted path.</b> Elsewhere in this client a documented
    /// <c>404</c> drives <c>dbo.uspSoftDeleteHandlerSourceSet</c> — but that applies to a version this loader
    /// had already enumerated from the feed. Here the identifier was typed by a person, and a mistyped one
    /// produces exactly the same <c>404</c>. Reading it as a withdrawal would delete real rows over a typo.
    /// </remarks>
    [Fact]
    public async Task AFourOhFourOnTheEnumerationSoftDeletesNothing()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(Handler, outcome: ApiFetchOutcome.NotFound, httpStatusCode: 404));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        Assert.Equal(LoadRunOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Writer.SoftDeletes);
        Assert.Equal(0, result.Counters.SourceRecordsSoftDeleted);

        Assert.Contains("NOTHING WAS SOFT DELETED", result.FailureMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer naming another state is refused whole, and this is the only place it can be caught.
    /// </summary>
    /// <remarks>
    /// <b>The request carries no <c>activityLocation</c> at all.</b> The spec presents the two summaries forms
    /// as alternatives, and a request satisfying both invites EPA to pick one — so on this form the check has
    /// nowhere to happen except on the answer, through the same <c>SummaryWalk.FindOutOfScope</c> the scheduled
    /// walk uses. Refused whole rather than filtered, because a response that ignored one parameter cannot be
    /// trusted to have applied the rest.
    /// </remarks>
    [Fact]
    public async Task AnAnswerNamingAnotherStateIsRefusedWholeAndNothingIsFetched()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(Handler, Ran.SummariesBody(Handler, "VA", (1, true))));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        Assert.Equal(LoadRunOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Writer.MergedBatches);
        Assert.Single(harness.Client.RequestedUris);

        Assert.Contains("'VA'", result.FailureMessage!, StringComparison.Ordinal);
        Assert.Contains("refused whole", result.FailureMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The identifier is upper-cased before it is sent, because a lower-cased one comes back as an absence.
    /// </summary>
    /// <remarks>
    /// EPA's identifiers are upper case, and a lower-cased one returns either an empty array or a documented
    /// <c>404</c> — both indistinguishable from "no such handler". Left as typed, the answer to a shift key
    /// would be a run that reports the handler does not exist.
    /// </remarks>
    [Fact]
    public async Task TheIdentifierIsUpperCasedBeforeItIsSent()
    {
        Harness harness = Harness.Targeted(Ran.Enumerated(Handler));

        await harness.RunTargetedAsync(
            new TargetedLoadRequest("  mdd000000001  ", TargetedVersionScope.CurrentRecord));

        string enumeration = harness.Client.RequestedUris[0];

        Assert.Contains(Handler, enumeration, StringComparison.Ordinal);
        Assert.DoesNotContain("mdd", enumeration, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version that fails leaves the run <c>PartiallySucceeded</c>, not <c>Succeeded</c>.
    /// </summary>
    [Fact]
    public async Task AVersionThatFailsMakesTheRunPartiallySucceeded()
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(Handler, Ran.SummariesBody(Handler, "MD", (1, true), (2, false))),
            version => version.Sequence == 1
                ? Ran.Fetched(version)
                : Ran.NotFetched(version, ApiFetchOutcome.ServiceFailure, 503));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.EveryVersion));

        Assert.Equal(LoadRunOutcome.PartiallySucceeded, result.Outcome);
        Assert.Equal(1, result.Counters.SourceRecordsFetched);
        Assert.Equal(1, result.Counters.SourceRecordsFailed);
        Assert.Null(harness.Writer.Advanced);
    }

    /// <summary>
    /// An enumeration that came back without a usable body names no version and fetches nothing.
    /// </summary>
    /// <param name="outcome">What the one call came back as.</param>
    /// <param name="httpStatusCode">The status, where there was one.</param>
    [Theory]
    [InlineData(ApiFetchOutcome.ServiceFailure, 503)]
    [InlineData(ApiFetchOutcome.Unauthorized, 401)]
    [InlineData(ApiFetchOutcome.Unexpected, 418)]
    [InlineData(ApiFetchOutcome.Unreachable, null)]
    public async Task AnEnumerationWithNoUsableBodyNamesNoVersion(
        ApiFetchOutcome outcome,
        int? httpStatusCode)
    {
        Harness harness = Harness.Targeted(
            Ran.Enumerated(Handler, outcome: outcome, httpStatusCode: httpStatusCode));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.EveryVersion));

        Assert.Equal(LoadRunOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Journal.Enumerated);
        Assert.Empty(harness.Writer.SoftDeletes);
        Assert.Contains(
            outcome.ToString(), result.FailureMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A body that cannot be read names no version, and the refusal reproduces no part of it.
    /// </summary>
    /// <remarks>
    /// The run's own failure message is written to <c>logs.LoadRun.FailureMessage</c>, which the monitoring web
    /// application reads — so it carries <c>SummaryPayload</c>'s problem text, which names a JSON path, a value
    /// kind or an element index and never a value.
    /// </remarks>
    [Fact]
    public async Task AnUnreadableBodyNamesNoVersionAndIsNotQuotedBack()
    {
        Harness harness = Harness.Targeted(Ran.Enumerated(Handler, """{"unexpected":"object"}"""));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.EveryVersion));

        Assert.Equal(LoadRunOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Journal.Enumerated);
        Assert.Contains("could not be read", result.FailureMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("unexpected", result.FailureMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unusable configuration or an unusable request throws, and no run row is opened.
    /// </summary>
    /// <remarks>
    /// <b>A throw rather than a reported outcome, because there is nothing to report against.</b> The scheduled
    /// run refuses the same way. The activity location is validated even though the request carries no
    /// <c>activityLocation</c>: it governs the scope check on the <i>answer</i>, which is the only thing
    /// standing between a hand-typed identifier and another state's records.
    /// </remarks>
    [Fact]
    public async Task AnUnusableConfigurationOrRequestOpensNoRunAtAll()
    {
        Harness harness = Harness.Targeted(Ran.Enumerated(Handler));
        harness.Options.ActivityLocation = string.Empty;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunTargetedAsync(
                new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord)));

        Assert.Contains("RCRAInfoLoad", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Log.Entries);
        Assert.Null(harness.Writer.StartRequest);
    }

    /// <summary>
    /// The run's one-line summary says <c>Targeted</c> and reports the watermark as not moved.
    /// </summary>
    /// <remarks>
    /// The line an operator reads on the console. <c>watermark=(not moved)</c> is the fact that matters on this
    /// path: the reason a targeted run is safe to run at any hour is that it cannot have moved the bookmark, and
    /// this is where that is visible without opening the database.
    /// </remarks>
    [Fact]
    public async Task TheRunSummarySaysTargetedAndSaysTheWatermarkDidNotMove()
    {
        Harness harness = Harness.Targeted(Ran.Enumerated(Handler));

        LoadRunResult result = await harness.RunTargetedAsync(
            new TargetedLoadRequest(Handler, TargetedVersionScope.CurrentRecord));

        string summary = result.ToString();

        Assert.Contains("mode=Targeted", summary, StringComparison.Ordinal);
        Assert.Contains("watermark=(not moved)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("http", summary, StringComparison.OrdinalIgnoreCase);
    }
}
