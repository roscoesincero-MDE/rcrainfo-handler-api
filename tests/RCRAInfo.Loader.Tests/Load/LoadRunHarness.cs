using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

// Aliased for the reason LoadRunTests aliases it: the harness has a property called Options, which shadows
// the static class of the same name. Renaming the property would be the other fix and the worse one.
using OptionsWrapper = Microsoft.Extensions.Options.Options;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>Assembles a <see cref="LoadRun"/> over recording doubles.</summary>
/// <remarks>
/// <para>
/// Mutable so a test changes the one thing it is about and inherits the rest. <see cref="ExecuteAsync"/>
/// builds the orchestrator at the last moment, after the test's edits.
/// </para>
/// <para>
/// <b>One harness for both entry points, and sharing the construction is the point.</b>
/// <see cref="LoadRun.RunTargetedAsync"/> deliberately skips three stages the scheduled run performs, and the
/// assertions that matter about it are that those stages did <i>not</i> happen. A second harness built for the
/// targeted path could satisfy those assertions by simply not wiring the stages up — so the doubles must be
/// the same instances, wired the same way, and the run must be the only difference.
/// </para>
/// </remarks>
internal sealed class Harness
{
    public CallLog Log { get; } = new();

    public RecordingRunWriter Writer { get; private set; } = null!;

    public RecordingJournal Journal { get; } = new(new CallLog());

    public StubSummaryWalk Walk { get; set; } = null!;

    public StubResume Resume { get; private set; } = null!;

    public StubDataClient Client { get; private set; } = null!;

    /// <summary>The reconcile stage the run used, available after <see cref="RunAsync"/>.</summary>
    public CurrentRecordReconcile Reconcile { get; private set; } = null!;

    /// <summary>
    /// Everything <see cref="LoadRun"/> logged, so a test can assert what a message SAYS and not only that
    /// the run reached the branch that emits it.
    /// </summary>
    /// <remarks>
    /// Added for the watermark-hold message, which was arithmetically correct and causally wrong for the
    /// whole of run 2621 precisely because no test read it. A branch whose only product is a sentence is
    /// untested until the sentence is asserted.
    /// </remarks>
    public RecordingLogger<LoadRun> RunLog { get; } = new();

    public LookupStageReport Lookups { get; set; } = Ran.Lookups();

    public LoadResumePoint Point { get; set; } = LoadResumePoint.None;

    /// <summary>What each per-version <c>/hd/sources/{id}/{type}/{sequence}</c> fetch answers with.</summary>
    /// <remarks>
    /// <b>Per-version fetches only — the summaries endpoint is <see cref="Summaries"/>.</b> The split is not
    /// cosmetic. A scheduled run now sends two shapes of request (§D4 asks EPA for a handler's whole version
    /// list), and the two want two shapes of body. When this property answered both, every test that
    /// overrode it to fail one version also failed that handler's reconcile, so a test about a soft delete
    /// reported <see cref="LoadRunOutcome.PartiallySucceeded"/> for a reason it never mentioned.
    /// </remarks>
    public Func<RcraInfoDataRequest, ApiFetchResult> Answer { get; set; } = null!;

    /// <summary>What <c>/hd/sources/summaries?handlerId=…</c> answers with.</summary>
    /// <remarks>
    /// Serves the reconcile stage on a scheduled run and the enumeration on a targeted one — the same
    /// endpoint asked the same way by the two paths, which is why one seam covers both.
    /// </remarks>
    public Func<RcraInfoDataRequest, ApiFetchResult> Summaries { get; set; } = null!;

    public LoadRunOptions Options { get; } = new()
    {
        ActivityLocation = "MD",
        WindowDays = 7,
        AbandonAfterMinutes = 720,
    };

    /// <summary>A harness whose walk names the given versions and whose fetches all succeed.</summary>
    /// <param name="versions">What the walk found.</param>
    /// <returns>The harness.</returns>
    /// <remarks>
    /// <b>The reconcile stage is answered with the lineage the walk itself named</b>, which is the honest
    /// default: a run whose merge just agreed with EPA on every version should find EPA's list agreeing too,
    /// so the reconcile is a no-op and no test that is about something else has to say so. The tests that are
    /// about §D4 replace <see cref="Summaries"/>.
    /// </remarks>
    public static Harness For(IReadOnlyList<HandlerVersion> versions)
    {
        Harness harness = Bare();

        harness.Walk = new StubSummaryWalk(
            harness.Log,
            Ran.Walk([.. versions.Select(version => Ran.Summary(version.HandlerId, version.Sequence))]));

        // Answers as whichever version the path names, so a test that asserts on a specific handler
        // is not relying on the fetch order the parallel loop happened to produce.
        harness.Answer = request => Ran.Fetched(
            versions.FirstOrDefault(
                version => request.RelativeUri.Contains(version.HandlerId, StringComparison.Ordinal),
                versions[0]));

        harness.Summaries = request => Ran.SummariesFor(request, versions);

        return harness;
    }

    /// <summary>
    /// A harness for the targeted path: one canned answer to the enumeration, and a body for every version
    /// the run goes on to ask for.
    /// </summary>
    /// <remarks>
    /// <b>The walk is still wired to a real report</b>, even though a targeted run must never call it. An
    /// unwired walk would make "the walk did not run" true by construction, which is the one thing these
    /// tests are for; a wired one that recorded nothing is evidence.
    /// </remarks>
    /// <param name="enumeration">What <c>/hd/sources/summaries?handlerId=…</c> answers with.</param>
    /// <param name="source">
    /// What each per-version fetch answers with, or null for a body that merges. Taken as a function of the
    /// version rather than as a list, so a test that fails one version of several does not have to restate the
    /// version set the enumeration already named.
    /// </param>
    /// <returns>The harness.</returns>
    public static Harness Targeted(
        ApiFetchResult enumeration,
        Func<HandlerVersion, ApiFetchResult>? source = null)
    {
        Harness harness = Bare();

        harness.Walk = new StubSummaryWalk(harness.Log, Ran.Walk());

        harness.Summaries = _ => enumeration;
        harness.Answer = request => (source ?? (version => Ran.Fetched(version)))(Ran.VersionIn(request));

        return harness;
    }

    /// <summary>Runs one scheduled load.</summary>
    /// <param name="cancellationToken">The token the fetch is given.</param>
    /// <returns>The result.</returns>
    public Task<LoadRunResult> RunAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(run => run.RunAsync(cancellationToken));

    /// <summary>Runs one single-handler load.</summary>
    /// <param name="request">The handler and the scope.</param>
    /// <param name="cancellationToken">The token the fetch is given.</param>
    /// <returns>The result.</returns>
    public Task<LoadRunResult> RunTargetedAsync(
        TargetedLoadRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(run => run.RunTargetedAsync(request, cancellationToken));

    private static Harness Bare()
    {
        Harness harness = new();

        harness.Writer = new RecordingRunWriter(harness.Log);

        return harness;
    }

    private async Task<LoadRunResult> ExecuteAsync(Func<LoadRun, Task<LoadRunResult>> run)
    {
        Resume = new StubResume(Log, Point);

        // Routed here rather than inside each Answer, so a test that replaces one shape of answer cannot
        // silently take over the other -- see the remarks on Answer for what that cost.
        Client = new StubDataClient(
            request => request.Endpoint == RcraInfoDataEndpoint.Summaries
                ? Summaries(request)
                : Answer(request));

        RcraInfoThrottleOptions throttle = new();

        using RequestPacer pacer = new(OptionsWrapper.Create(throttle), TimeProvider.System);

        // The REAL reconcile stage, over the same client and the same writer -- not a stub. Deliberate, and
        // the opposite choice from the walk: a stubbed reconcile would let a test assert that a lineage was
        // reconciled without anything ever having read EPA's list or batched it, and both of those are the
        // parts of §D4 that can be got wrong. Wired here means Writer.ReconciledBatches records what script
        // 521 would actually have been handed, batch boundaries included.
        Reconcile = new CurrentRecordReconcile(
            Client,
            Writer,
            OptionsWrapper.Create(Options),
            OptionsWrapper.Create(throttle),
            new RecordingLogger<CurrentRecordReconcile>());

        LoadRun loadRun = new(
            Writer,
            new StubLookupRefresh(Log, Lookups),
            Walk,
            Resume,
            Reconcile,
            new JournalFactory(Journal, Log),
            Client,
            pacer,
            OptionsWrapper.Create(Options),
            OptionsWrapper.Create(throttle),
            RunLog);

        return await run(loadRun).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands out the test's journal and logs the creation against the shared log.
    /// </summary>
    /// <remarks>
    /// A wrapper rather than making <see cref="RecordingJournal"/> its own factory, so the journal's
    /// dispose entry lands in the same ordered log as everything else.
    /// </remarks>
    private sealed class JournalFactory(RecordingJournal journal, CallLog log) : ILoadJournalFactory
    {
        public ILoadJournal Create(int loadRunId) => new Proxy(journal.Create(loadRunId), journal, log);

        private sealed class Proxy(ILoadJournal inner, RecordingJournal journal, CallLog log)
            : ILoadJournal
        {
            public int PendingRows => inner.PendingRows;

            public LoadJournalFlush Total => inner.Total;

            public ValueTask EnumerateAsync(
                IEnumerable<HandlerLoadStatusElement> versions,
                CancellationToken cancellationToken = default)
            {
                HandlerLoadStatusElement[] elements = [.. versions];

                log.Add($"Enumerate({elements.Length})");

                return inner.EnumerateAsync(elements, cancellationToken);
            }

            public ValueTask RecordAttemptAsync(
                ApiFetchResult result,
                HandlerVersion version,
                int attemptNumber,
                CancellationToken cancellationToken = default)
            {
                log.Add("RecordAttempt");

                return inner.RecordAttemptAsync(result, version, attemptNumber, cancellationToken);
            }

            public ValueTask<bool> ConcludeAsync(
                ApiFetchResult result,
                HandlerVersion version,
                CancellationToken cancellationToken = default)
            {
                log.Add("Conclude");

                return inner.ConcludeAsync(result, version, cancellationToken);
            }

            public ValueTask SkipAsync(
                IEnumerable<HandlerVersion> versions,
                CancellationToken cancellationToken = default)
            {
                HandlerVersion[] elements = [.. versions];

                log.Add($"Skip({elements.Length})");

                return inner.SkipAsync(elements, cancellationToken);
            }

            public Task<LoadJournalFlush> FlushAsync(CancellationToken cancellationToken = default)
            {
                log.Add("Flush");

                return inner.FlushAsync(cancellationToken);
            }

            public ValueTask DisposeAsync()
            {
                log.Add("DisposeJournal");

                return journal.DisposeAsync();
            }
        }
    }
}
