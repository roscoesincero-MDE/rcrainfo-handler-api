using Microsoft.Extensions.Options;

using RCRAInfo.Data.Payloads;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The stage that settles who holds <c>CurrentRecord</c>. Nearly every assertion here is about one sentence in
/// script 521's contract: <b><c>@Summaries</c> must be the COMPLETE version list for every
/// <c>(HandlerId, SourceType)</c> pair it mentions.</b>
/// </summary>
/// <remarks>
/// <para>
/// That sentence is what makes this stage dangerous rather than merely useful. The procedure's purpose is to
/// demote versions EPA no longer calls current, so a live version of a mentioned pair that the submitted list
/// does not name is set to <c>CurrentRecord = 0</c>. A <i>partial</i> list is therefore not a smaller
/// assertion — it is a wrong one, and it is wrong silently: rows change, nothing errors, and the mirror now
/// disagrees with EPA in the opposite direction.
/// </para>
/// <para>
/// So the tests fall into two families. One is about never submitting an incomplete list —
/// <see cref="ALineageIsNeverSplitAcrossTwoCalls"/>, <see cref="AHandlerDeeperThanOneCallIsRefusedWhole"/>,
/// <see cref="AnAnswerNamingAnotherHandlerIsRefusedWhole"/> — and the other is about never <i>claiming</i> to
/// have asserted a lineage nothing examined, which is what <see cref="HandlerReconcileStatus.NoVersions"/> and
/// <see cref="HandlerReconcileReport.NotReached"/> exist to keep apart.
/// </para>
/// <para>
/// The one assertion that is not about the contract is
/// <see cref="AFailedHandlerDoesNotStopTheStageAndDoesNotDiscardTheOnesThatLanded"/>, and it is the reversal
/// this stage makes against <see cref="SummaryWalk"/>'s rule. A failed window is a permanent, undiscoverable
/// hole. A stale flag is one <c>GROUP BY</c> away from being found — which is how <c>MDR000501742/N</c> was
/// found — and one targeted run away from being fixed.
/// </para>
/// </remarks>
public class CurrentRecordReconcileTests
{
    private const int RunId = 77;
    private const string Handler = "MDD000000001";
    private const string Other = "MDD000000002";

    /// <summary>
    /// The ordinary case: one request per handler, one call carrying both complete lineages.
    /// </summary>
    [Fact]
    public async Task EachHandlerIsAskedOnceAndEveryVersionItHoldsIsSubmitted()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, RecordingRunWriter writer, _) = Build(
            request => Ran.Enumerated(
                Ran.HandlerIdIn(request),
                Ran.SummariesBody(
                    Ran.HandlerIdIn(request),
                    "MD",
                    (1, false),
                    (2, true))));

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler, Other]);

        Assert.Equal(2, client.RequestedUris.Count);
        Assert.Equal(2, report.Calls);
        Assert.Equal(2, report.ReconciledCount);
        Assert.True(report.Complete);

        // One call, four elements: two whole lineages fit inside one procedure invocation, and batching them
        // is the point -- 300,000 handlers at one call each is the shape this stage must not have.
        Assert.Single(writer.ReconciledBatches);
        Assert.Equal(4, writer.ReconciledBatches[0].Count);
        Assert.Equal(1, report.WriteCalls);
    }

    /// <summary>
    /// The delta feed is never the source of the list, so the handler is asked and not assumed.
    /// </summary>
    /// <remarks>
    /// <b>The check that the stage exists at all.</b> A version's <c>currentRecord</c> flag as delivered in a
    /// dated summaries window is a property of the handler's <i>whole</i> lineage delivered as a property of one
    /// version — adding version 13 says nothing about version 12, and version 12 is not in the delta feed
    /// because it did not change. So the reconciliation must ask per handler, and this asserts the request it
    /// sends carries <c>handlerId</c> and no date range at all.
    /// </remarks>
    [Fact]
    public async Task TheRequestNamesTheHandlerAndCarriesNoDateWindow()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, _, _) = Build();

        await reconcile.ReconcileAsync(RunId, [Handler]);

        string uri = Assert.Single(client.RequestedUris);

        Assert.Contains("handlerId=" + Handler, uri, StringComparison.Ordinal);
        Assert.DoesNotContain("startDate", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("endDate", uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// A handler named twice by a multi-window walk costs one request and is submitted once.
    /// </summary>
    /// <remarks>
    /// A handler that changed in three of a decade's windows is one lineage, not three. Each duplicate would
    /// otherwise cost a request and submit the same list again — harmless to the data and not to the request
    /// budget an initial load has to live inside.
    /// </remarks>
    [Fact]
    public async Task AHandlerNamedTwiceIsReconciledOnce()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, RecordingRunWriter writer, _) = Build();

        ReconcileReport report = await reconcile.ReconcileAsync(
            RunId,
            [Handler, "  " + Handler + "  ", Handler.ToLowerInvariant()]);

        Assert.Single(client.RequestedUris);
        Assert.Single(writer.ReconciledBatches);
        Assert.Equal(1, report.ConsideredCount);
    }

    /// <summary>
    /// No lineage ever straddles a batch boundary, even when the limit falls in the middle of one.
    /// </summary>
    /// <remarks>
    /// <b>The single most important test in this file.</b> With an element limit of 3 and two handlers holding
    /// two versions each, a batcher that closed <i>at</i> the limit would send
    /// <c>[A/1, A/2, B/1]</c> then <c>[B/2]</c> — and script 521, told that <c>B</c>'s complete list is
    /// <c>[B/1]</c>, would demote <c>B/2</c>, then be told that <c>B</c>'s complete list is <c>[B/2]</c> and
    /// demote <c>B/1</c>. Both halves are individually well-formed, the procedure raises nothing, and the
    /// lineage ends with zero current versions. Closing the batch <i>before</i> the element that would overflow
    /// it is the whole fix, and this asserts the boundary rather than the total — a flattened record of what was
    /// sent contains all four versions and looks perfectly correct.
    /// </remarks>
    [Fact]
    public async Task ALineageIsNeverSplitAcrossTwoCalls()
    {
        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build(
            request => Ran.Enumerated(
                Ran.HandlerIdIn(request),
                Ran.SummariesBody(Ran.HandlerIdIn(request), "MD", (1, false), (2, true))));

        writer.MaxElementsPerCall = 3;

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler, Other]);

        Assert.Equal(2, writer.ReconciledBatches.Count);
        Assert.Equal(2, report.WriteCalls);

        Assert.All(
            writer.ReconciledBatches,
            batch => Assert.Single(batch.Select(element => element.HandlerId).Distinct(StringComparer.Ordinal)));

        Assert.All(writer.ReconciledBatches, batch => Assert.Equal(2, batch.Count));
        Assert.True(report.Complete);
    }

    /// <summary>
    /// A handler holding more versions than one call may carry is refused whole, not split.
    /// </summary>
    /// <remarks>
    /// Not reachable against EPA today — the deepest Maryland lineage observed holds 33 versions against a limit
    /// of 500 — and guarded because the failure it would cause is silent flag corruption rather than an error.
    /// Refused means the handler is reported unreconciled and nothing at all is submitted for it: half a lineage
    /// is worse than none.
    /// </remarks>
    [Fact]
    public async Task AHandlerDeeperThanOneCallIsRefusedWhole()
    {
        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build(
            request => Ran.Enumerated(
                Ran.HandlerIdIn(request),
                Ran.SummariesBody(Ran.HandlerIdIn(request), "MD", (1, false), (2, false), (3, true))));

        writer.MaxElementsPerCall = 2;

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler]);

        Assert.Empty(writer.ReconciledBatches);
        Assert.Equal(0, report.WriteCalls);
        Assert.False(report.Complete);

        HandlerReconcileReport handler = Assert.Single(report.UnreconciledHandlers);

        Assert.Equal(HandlerReconcileStatus.TooManyVersions, handler.Status);
        Assert.Equal(Handler, handler.HandlerId);
        Assert.Contains("refused whole", handler.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer naming a handler the request did not ask about is refused whole.
    /// </summary>
    /// <remarks>
    /// A different guard from the activity-location one and a different loss. Out-of-scope stops another state's
    /// regulated entities being mirrored; this stops a <i>third</i> handler's lineage being asserted from a list
    /// that was never its complete one, which would demote every version of it the answer happened to omit. Both
    /// refuse the answer whole for the same underlying reason: a response that did not honour the parameter it
    /// was given cannot be trusted to have honoured the rest.
    /// </remarks>
    [Fact]
    public async Task AnAnswerNamingAnotherHandlerIsRefusedWhole()
    {
        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build(
            _ => Ran.Enumerated(Handler, Ran.SummariesBody(Other, "MD", (1, true))));

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler]);

        Assert.Empty(writer.ReconciledBatches);

        HandlerReconcileReport handler = Assert.Single(report.UnreconciledHandlers);

        Assert.Equal(HandlerReconcileStatus.ForeignHandler, handler.Status);

        // Neither identifier is reproduced in the problem text: the actionable fact is that the answer named
        // more than one handler, and the report row already carries the one that was asked about.
        Assert.DoesNotContain(Other, handler.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An out-of-state answer is refused whole, by the same method the walk uses.
    /// </summary>
    /// <remarks>
    /// <b>Checked here even though the caller checked it, because a contract note is not a check.</b> This
    /// endpoint's <c>handlerId</c> form cannot carry an <c>activityLocation</c> filter at all, so verifying the
    /// answer is the only scope this stage has.
    /// </remarks>
    [Fact]
    public async Task AnOutOfStateAnswerIsRefusedWhole()
    {
        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build(
            _ => Ran.Enumerated(Handler, Ran.SummariesBody(Handler, "VA", (1, true))));

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler]);

        Assert.Empty(writer.ReconciledBatches);
        Assert.Equal(
            HandlerReconcileStatus.OutOfScope,
            Assert.Single(report.UnreconciledHandlers).Status);
    }

    /// <summary>
    /// EPA holding no summaries for the handler is a contradiction, not a deletion — and not a success.
    /// </summary>
    /// <remarks>
    /// <b>The third distinct meaning this project gives <c>ApiFetchOutcome.NotFound</c>, and the two it is not.</b>
    /// On <c>/hd/sources/{id}/{type}/{sequence}</c> it is AR7's soft-delete signal; on the dated summaries form
    /// it is an empty window. Here it means EPA holds no version list for a handler whose version this run has
    /// just merged. Nothing may be soft deleted on the strength of it — the AR7 signal is a 404 for a version
    /// this loader named, and this call names a handler — and it must not count as reconciled either: an empty
    /// <c>@Summaries</c> mentions no pair and script 521 documents it as a no-op, so calling it reconciled would
    /// report the one definitely-unexamined lineage as examined and agreed.
    /// </remarks>
    [Theory]
    [InlineData("404", true)]
    [InlineData("empty array", false)]
    public async Task AnEmptyAnswerIsNeverReportedAsReconciled(string label, bool notFound)
    {
        Assert.NotEmpty(label);

        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build(
            _ => notFound
                ? Ran.Enumerated(Handler, outcome: ApiFetchOutcome.NotFound, httpStatusCode: 404)
                : Ran.Enumerated(Handler, "[]"));

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler]);

        Assert.Empty(writer.ReconciledBatches);
        Assert.Equal(0, report.ReconciledCount);
        Assert.False(report.Complete);

        HandlerReconcileReport handler = Assert.Single(report.UnreconciledHandlers);

        // Both are NoVersions, and the two problem texts differ on the one point that could be misread. Only
        // the 404 could be mistaken for AR7's soft-delete signal, so only that one has to say it is not.
        Assert.Equal(HandlerReconcileStatus.NoVersions, handler.Status);

        if (notFound)
        {
            Assert.Contains("NOTHING WAS SOFT DELETED", handler.Problem!, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("no-op", handler.Problem!, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One handler's failure neither stops the stage nor discards the lineages that were asserted.
    /// </summary>
    /// <remarks>
    /// The reversal against <see cref="SummaryWalk"/>. A failed window must stop the bookmark because the gap it
    /// leaves is permanent and nothing downstream can notice it. A failed <i>handler</i> here leaves a flag that
    /// one query finds and a targeted run repairs, so continuing is right and so is keeping the work that
    /// landed: refusing to submit the lineages already read would trade a discoverable stale flag for two of
    /// them.
    /// </remarks>
    [Fact]
    public async Task AFailedHandlerDoesNotStopTheStageAndDoesNotDiscardTheOnesThatLanded()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, RecordingRunWriter writer, _) = Build(
            request => Ran.HandlerIdIn(request) == Other
                ? Ran.Enumerated(Other, outcome: ApiFetchOutcome.ServiceFailure, httpStatusCode: 503)
                : Ran.Enumerated(Ran.HandlerIdIn(request)));

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler, Other, "MDD000000003"]);

        // Every handler was still asked, including the ones after the failure.
        Assert.Equal(3, client.RequestedUris.Count);
        Assert.Equal(3, report.ConsideredCount);
        Assert.Equal(2, report.ReconciledCount);

        // And the two that answered were submitted rather than abandoned.
        Assert.Single(writer.ReconciledBatches);
        Assert.Equal(2, writer.ReconciledBatches[0].Count);

        HandlerReconcileReport failed = Assert.Single(report.UnreconciledHandlers);

        Assert.Equal(HandlerReconcileStatus.FetchFailed, failed.Status);
        Assert.False(report.Complete);
        Assert.Contains("1 of 3", report.FailureMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fatal outcome stops the stage, and the handlers never reached say so rather than going unmentioned.
    /// </summary>
    /// <remarks>
    /// <c>NotAttempted</c> and a reconciled verdict must never be confusable: the first is "we did not ask" and
    /// the second is "we asked and the lineage agreed". Omitting the unreached handlers would leave the report's
    /// counts adding up while quietly meaning the first.
    /// </remarks>
    [Fact]
    public async Task AFatalOutcomeStopsTheStageAndTheUnreachedHandlersAreStillReported()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, _, _) = Build(
            _ => Ran.Enumerated(Handler, outcome: ApiFetchOutcome.Unauthorized, httpStatusCode: 401),
            fetchBatchSize: 1);

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler, Other, "MDD000000003"]);

        Assert.Single(client.RequestedUris);
        Assert.Equal(ApiFetchOutcome.Unauthorized, report.FatalOutcome);
        Assert.Equal(3, report.ConsideredCount);

        Assert.Equal(
            2,
            report.Handlers.Count(handler => handler.Status == HandlerReconcileStatus.NotAttempted));

        Assert.False(report.Complete);
    }

    /// <summary>
    /// A cancelled call is reported as cancelled and not as one more failed handler.
    /// </summary>
    /// <remarks>
    /// The one condition this stage does propagate upward. Cancellation is a fact about the <i>run</i> rather
    /// than about a lineage, and every cancelled run holds its watermark — so unlike an unreconciled handler,
    /// this one must reach <see cref="ReconcileReport.WasCancelled"/> for the orchestrator to read.
    /// </remarks>
    [Fact]
    public async Task ACancelledCallIsReportedAsCancellation()
    {
        (CurrentRecordReconcile reconcile, _, _, _) = Build(
            _ => Ran.Enumerated(Handler, outcome: ApiFetchOutcome.Cancelled));

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler]);

        Assert.True(report.WasCancelled);
        Assert.False(report.Complete);
        Assert.Contains("cancel", report.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The list EPA sent is submitted verbatim, including the identifiers — not the requested ones.
    /// </summary>
    /// <remarks>
    /// Substituting the requested handler and a default source type would make
    /// <see cref="AnAnswerNamingAnotherHandlerIsRefusedWhole"/> unfalsifiable: the projection would repair the
    /// very disagreement the refusal is there to detect. The flags matter for the same reason — this stage
    /// mirrors EPA's answer, and <c>[R38]</c> measured that EPA does publish lineages with zero current
    /// versions, so the stage must be able to submit one.
    /// </remarks>
    [Fact]
    public async Task EpasOwnIdentifiersAndFlagsAreWhatIsSubmitted()
    {
        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build(
            _ => Ran.Enumerated(Handler, Ran.SummariesBody(Handler, "MD", (1, false), (3, false))));

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler]);

        IReadOnlyCollection<HandlerVersionElement> batch = Assert.Single(writer.ReconciledBatches);

        Assert.Equal([1, 3], batch.Select(element => element.Sequence));
        Assert.All(batch, element => Assert.Equal("N", element.SourceType));
        Assert.All(batch, element => Assert.Equal(Handler, element.HandlerId));
        Assert.All(batch, element => Assert.False(element.CurrentRecord));

        // Zero current versions is EPA's answer and not a defect, so it is reconciled.
        Assert.True(report.Complete);
        Assert.Equal(0, Assert.Single(report.Handlers).CurrentRecordCount);
    }

    /// <summary>
    /// The targeted overload spends no request, because its caller already holds the complete list.
    /// </summary>
    /// <remarks>
    /// <b><c>Calls</c> must be zero, and it is not bookkeeping.</b> <c>LoadRun</c> derives the retry count by
    /// subtracting logical calls from the pacer's reservations, so a call counted twice reads in
    /// <c>logs.LoadRun</c> as a retry that never happened.
    /// </remarks>
    [Fact]
    public async Task TheKnownOverloadSpendsNoRequestAndStillSubmitsTheWholeLineage()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, RecordingRunWriter writer, _) = Build();

        ReconcileReport report = await reconcile.ReconcileKnownAsync(
            RunId,
            Handler,
            [Summary(1, false), Summary(2, true)]);

        Assert.Empty(client.RequestedUris);
        Assert.Equal(0, report.Calls);

        Assert.Equal(2, Assert.Single(writer.ReconciledBatches).Count);
        Assert.True(report.Complete);
    }

    /// <summary>
    /// The targeted overload re-runs the guards its caller has already satisfied.
    /// </summary>
    /// <remarks>
    /// The caller holds a list it read from the same endpoint and could reasonably be trusted. It is not,
    /// because the cost of the guard is two comparisons and the cost of its absence is a demoted lineage — and
    /// because the one mistake script 521's contract punishes is passing the versions a targeted run
    /// <i>selected</i> rather than the complete list it read, which no guard here can detect.
    /// </remarks>
    [Fact]
    public async Task TheKnownOverloadStillRefusesAForeignOrOutOfStateList()
    {
        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build();

        ReconcileReport foreign = await reconcile.ReconcileKnownAsync(
            RunId, Handler, [Summary(1, true, handlerId: Other)]);

        ReconcileReport outOfState = await reconcile.ReconcileKnownAsync(
            RunId, Handler, [Summary(1, true, activityLocation: "VA")]);

        Assert.Empty(writer.ReconciledBatches);

        Assert.Equal(
            HandlerReconcileStatus.ForeignHandler,
            Assert.Single(foreign.UnreconciledHandlers).Status);

        Assert.Equal(
            HandlerReconcileStatus.OutOfScope,
            Assert.Single(outOfState.UnreconciledHandlers).Status);
    }

    /// <summary>
    /// An empty handler list is a no-op that reports one, rather than a call with an empty payload.
    /// </summary>
    [Fact]
    public async Task NoHandlersMeansNoRequestAndNoCall()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, RecordingRunWriter writer, _) = Build();

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, ["   ", string.Empty]);

        Assert.Empty(client.RequestedUris);
        Assert.Empty(writer.ReconciledBatches);
        Assert.True(report.Complete);
        Assert.Equal(0, report.ConsideredCount);
        Assert.Null(report.FailureMessage);
    }

    /// <summary>
    /// Unusable configuration throws rather than reporting an unreconciled handler.
    /// </summary>
    /// <remarks>
    /// A loader defect is not an answer from EPA, and here it is sharper than elsewhere: the activity location
    /// is the only scope this endpoint's <c>handlerId</c> form has, because the request cannot carry the filter.
    /// Reported as a failed handler it would look like EPA's fault, and the run would blame the API for a
    /// configuration file.
    /// </remarks>
    [Fact]
    public async Task UnusableOptionsThrow()
    {
        (CurrentRecordReconcile reconcile, StubDataClient client, _, _) = Build(activityLocation: " ");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reconcile.ReconcileAsync(RunId, [Handler]));

        Assert.Empty(client.RequestedUris);
    }

    /// <summary>
    /// The counts script 521 reported come back, and zero rows affected is the good answer.
    /// </summary>
    /// <remarks>
    /// <c>RowsAffected</c> counts flags this run had to <i>correct</i>. Zero means EPA's list and the mirror
    /// already agreed, which is what a healthy run looks like — so a non-zero value is the interesting one, and
    /// it is the disagreement §D4 exists to settle rather than a defect in the merge.
    /// </remarks>
    [Fact]
    public async Task TheProceduresCountsAreSummedAcrossEveryCall()
    {
        (CurrentRecordReconcile reconcile, _, RecordingRunWriter writer, _) = Build();

        writer.MaxElementsPerCall = 1;
        writer.Reconcile = _ => new ReconcileCounts(2, 3);

        ReconcileReport report = await reconcile.ReconcileAsync(RunId, [Handler, Other]);

        Assert.Equal(2, writer.ReconciledBatches.Count);
        Assert.Equal(4, report.Counts.RowsAffected);
        Assert.Equal(6, report.Counts.Observations);
    }

    /// <summary>Nothing this stage logs names a handler, a URI or a body (AR8).</summary>
    /// <remarks>
    /// Harder here than anywhere else in the folder, because the handler identifier is the stage's whole subject
    /// and the report carries it one field away. The identifier is not itself a secret — script 506 says so —
    /// but <c>LoadRunLog</c> carries none and this log follows it, so the split stays legible: reports name
    /// handlers, logs count them.
    /// </remarks>
    [Fact]
    public async Task TheLogNamesNoHandlerAndNoBody()
    {
        (CurrentRecordReconcile reconcile, _, _, RecordingLogger<CurrentRecordReconcile> logger) = Build(
            request => Ran.HandlerIdIn(request) == Other
                ? Ran.Enumerated(Other, Ran.SummariesBody(Other, "VA", (1, true)))
                : Ran.Enumerated(Ran.HandlerIdIn(request)));

        await reconcile.ReconcileAsync(RunId, [Handler, Other]);

        Assert.NotEmpty(logger.Entries);

        foreach (string message in logger.Entries.Select(entry => entry.Message))
        {
            Assert.DoesNotContain(Handler, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Other, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hd/sources", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("currentRecord\"", message, StringComparison.Ordinal);
        }
    }

    private static HandlerSourceSummary Summary(
        int sequence,
        bool currentRecord,
        string handlerId = Handler,
        string activityLocation = "MD") =>
        new()
        {
            HandlerId = handlerId,
            ActivityLocation = activityLocation,
            SourceType = "N",
            Sequence = sequence,
            CurrentRecord = currentRecord,
        };

    private static (
        CurrentRecordReconcile Reconcile,
        StubDataClient Client,
        RecordingRunWriter Writer,
        RecordingLogger<CurrentRecordReconcile> Logger) Build(
        Func<RcraInfoDataRequest, ApiFetchResult>? answer = null,
        string activityLocation = "MD",
        int fetchBatchSize = 50)
    {
        StubDataClient client = new(answer ?? (request => Ran.Enumerated(Ran.HandlerIdIn(request))));
        RecordingRunWriter writer = new(new CallLog());
        RecordingLogger<CurrentRecordReconcile> logger = new();

        CurrentRecordReconcile reconcile = new(
            client,
            writer,
            Options.Create(
                new LoadRunOptions
                {
                    ActivityLocation = activityLocation,
                    WindowDays = 7,
                    FetchBatchSize = fetchBatchSize,
                }),
            Options.Create(new RcraInfoThrottleOptions()),
            logger);

        return (reconcile, client, writer, logger);
    }
}
