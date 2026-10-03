using System.Globalization;

using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// A shared, ordered log of the calls the orchestrator made across every stage.
/// </summary>
/// <remarks>
/// <b>One list rather than one per double, because the assertions that matter here are between stages.</b>
/// Three of <see cref="LoadRun"/>'s steps are load-bearing only in relation to another step — the resume read
/// must precede the run's own row, the lookups must precede the first handler fetch, and the enumerate must
/// precede the skip — and none of those is observable from any single double.
/// </remarks>
internal sealed class CallLog
{
    private readonly List<string> entries = [];

    /// <summary>A snapshot, taken under the lock because the fetch loop is parallel.</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (this.entries)
            {
                return [.. this.entries];
            }
        }
    }

    /// <summary>Records one call.</summary>
    /// <param name="entry">The call's name, and its count where a count is worth asserting.</param>
    public void Add(string entry)
    {
        lock (this.entries)
        {
            this.entries.Add(entry);
        }
    }

    /// <summary>The index of the first entry starting with <paramref name="prefix"/>, or -1.</summary>
    /// <param name="prefix">What to look for.</param>
    /// <returns>The index.</returns>
    public int IndexOf(string prefix)
    {
        IReadOnlyList<string> snapshot = Entries;

        for (int index = 0; index < snapshot.Count; index++)
        {
            if (snapshot[index].StartsWith(prefix, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The whole sequence, for an assertion message that says what actually happened.</summary>
    public override string ToString() => string.Join(" -> ", Entries);
}

/// <summary>
/// An <see cref="ILoadRunWriter"/> that records every call and every cancellation token it was given.
/// </summary>
/// <remarks>
/// <b>The recorded tokens are half the point of this double.</b> Whether the closing write is cancellable is
/// not observable from its result — a cancelled <c>CompleteRunAsync</c> and one that was never reached both
/// leave <c>logs.LoadRun</c> at <c>Running</c> — so the only place it can be asserted is in the argument.
/// </remarks>
/// <param name="log">The shared call log.</param>
internal sealed class RecordingRunWriter(CallLog log) : ILoadRunWriter
{
    /// <inheritdoc />
    public int MaxElementsPerCall { get; set; } = 500;

    /// <summary>What <c>config.uspGetLoadWatermark</c> answers with.</summary>
    public LoadWatermark? Watermark { get; set; } = Ran.Watermark();

    /// <summary>What <c>logs.uspStartLoadRun</c> returns.</summary>
    public int LoadRunId { get; set; } = 42;

    /// <summary>Thrown by <c>StartRunAsync</c>, if set.</summary>
    public Exception? ThrowOnStart { get; set; }

    /// <summary>Thrown by <c>AdvanceWatermarkAsync</c>, if set.</summary>
    public Exception? ThrowOnWatermark { get; set; }

    /// <summary>Thrown by <c>CompleteRunAsync</c>, if set.</summary>
    public Exception? ThrowOnComplete { get; set; }

    /// <summary>How each merged batch is classified. Default: everything is an insert.</summary>
    public Func<IReadOnlyCollection<HandlerEnvelope>, MergeCounts> Merge { get; set; } =
        envelopes => new MergeCounts(envelopes.Count, 0, 0);

    /// <summary>What the orchestrator asked script 510 to open.</summary>
    public LoadRunRequest? StartRequest { get; private set; }

    /// <summary>Each merge call's envelopes, kept per call so the batch boundaries survive.</summary>
    public List<IReadOnlyCollection<HandlerEnvelope>> MergedBatches { get; } = [];

    /// <summary>Each soft-delete call.</summary>
    public List<(IReadOnlyCollection<HandlerKeyElement> Keys, string Reason)> SoftDeletes { get; } = [];

    /// <summary>
    /// Each <c>dbo.uspReconcileCurrentRecord</c> call's elements, kept per call.
    /// </summary>
    /// <remarks>
    /// <b>Per call and not flattened, because the batch boundary is the thing worth asserting.</b> Script 521
    /// demotes every live version of a mentioned <c>(HandlerId, SourceType)</c> pair that the submitted list
    /// does not name, so a lineage split across two calls has each half demote the other — and a flattened
    /// record of what was sent would contain every version and look perfectly correct.
    /// </remarks>
    public List<IReadOnlyCollection<HandlerVersionElement>> ReconciledBatches { get; } = [];

    /// <summary>What each reconcile call answers with. Default: nothing changed and nothing observed.</summary>
    public Func<IReadOnlyCollection<HandlerVersionElement>, ReconcileCounts> Reconcile { get; set; } =
        _ => ReconcileCounts.Empty;

    /// <summary>What the run was closed as, if it was closed.</summary>
    public (int LoadRunId, string Status, string? FailureMessage, LoadRunCounters Counters)? Completed
    { get; private set; }

    /// <summary>Where the watermark was moved to, if it moved.</summary>
    public (string FeedName, string ActivityLocation, DateOnly WatermarkDate, int LoadRunId)? Advanced
    { get; private set; }

    /// <summary>Every call's name paired with whether its token could be cancelled.</summary>
    public List<(string Call, bool Cancellable)> Tokens { get; } = [];

    /// <inheritdoc />
    public Task<LoadWatermark?> ReadWatermarkAsync(
        string feedName,
        string activityLocation,
        CancellationToken cancellationToken = default)
    {
        Record("ReadWatermark", cancellationToken);

        return Task.FromResult(Watermark);
    }

    /// <inheritdoc />
    public Task<int> StartRunAsync(LoadRunRequest request, CancellationToken cancellationToken = default)
    {
        Record("StartRun", cancellationToken);

        if (ThrowOnStart is not null)
        {
            throw ThrowOnStart;
        }

        StartRequest = request;

        return Task.FromResult(LoadRunId);
    }

    /// <inheritdoc />
    public Task CompleteRunAsync(
        int loadRunId,
        string status,
        string? failureMessage,
        LoadRunCounters counters,
        CancellationToken cancellationToken = default)
    {
        Record("CompleteRun", cancellationToken);

        if (ThrowOnComplete is not null)
        {
            throw ThrowOnComplete;
        }

        Completed = (loadRunId, status, failureMessage, counters);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MergeCounts> MergeAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        Record($"Merge({envelopes.Count})", cancellationToken);
        MergedBatches.Add([.. envelopes]);

        return Task.FromResult(Merge(envelopes));
    }

    /// <inheritdoc />
    public Task<int> SoftDeleteAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerKeyElement> keys,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Record($"SoftDelete({keys.Count})", cancellationToken);
        SoftDeletes.Add(([.. keys], reason));

        return Task.FromResult(keys.Count);
    }

    /// <inheritdoc />
    public Task<ReconcileCounts> ReconcileAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerVersionElement> summaries,
        CancellationToken cancellationToken = default)
    {
        Record($"Reconcile({summaries.Count})", cancellationToken);
        ReconciledBatches.Add([.. summaries]);

        return Task.FromResult(Reconcile(summaries));
    }

    /// <inheritdoc />
    public Task AdvanceWatermarkAsync(
        string feedName,
        string activityLocation,
        DateOnly watermarkDate,
        int loadRunId,
        CancellationToken cancellationToken = default)
    {
        Record("AdvanceWatermark", cancellationToken);

        if (ThrowOnWatermark is not null)
        {
            throw ThrowOnWatermark;
        }

        Advanced = (feedName, activityLocation, watermarkDate, loadRunId);

        return Task.CompletedTask;
    }

    private void Record(string call, CancellationToken cancellationToken)
    {
        log.Add(call);

        lock (Tokens)
        {
            Tokens.Add((call, cancellationToken.CanBeCanceled));
        }
    }
}

/// <summary>A lookup stage that answers with a canned report.</summary>
/// <param name="log">The shared call log.</param>
/// <param name="report">What it answers with.</param>
internal sealed class StubLookupRefresh(CallLog log, LookupStageReport report) : ILookupRefresh
{
    /// <inheritdoc />
    public Task<LookupStageReport> RefreshAllAsync(
        int loadRunId,
        CancellationToken cancellationToken = default)
    {
        log.Add("RefreshLookups");

        return Task.FromResult(report);
    }
}

/// <summary>A summaries walk that answers with a canned report and records the range it was given.</summary>
/// <param name="log">The shared call log.</param>
/// <param name="report">What it answers with.</param>
internal sealed class StubSummaryWalk(CallLog log, SummaryWalkReport report) : ISummaryWalk
{
    /// <summary>The three arguments the orchestrator supplied.</summary>
    /// <remarks>
    /// Asserted because <c>config.uspGetLoadWatermark</c> already applied the overlap (G25), so an
    /// orchestrator that re-derived the range would produce a working run over the wrong days.
    /// </remarks>
    public (int LoadRunId, DateOnly FromDate, DateOnly ToDate)? Asked { get; private set; }

    /// <inheritdoc />
    public Task<SummaryWalkReport> WalkAsync(
        int loadRunId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        log.Add("Walk");
        Asked = (loadRunId, fromDate, toDate);

        return Task.FromResult(report);
    }
}

/// <summary>
/// A resume stage that answers with a canned point and plans with the real splitting logic.
/// </summary>
/// <remarks>
/// <c>Plan</c> delegates to <see cref="LoadResumePoint.Plan"/> rather than returning a canned plan, because
/// the split has its own tests and a canned one would let a test pass while the orchestrator planned against
/// the wrong version set.
/// </remarks>
/// <param name="log">The shared call log.</param>
/// <param name="point">What the read answers with.</param>
internal sealed class StubResume(CallLog log, LoadResumePoint point) : ILoadResume
{
    /// <summary>The run number the orchestrator asked for. Should always be null: it has none yet.</summary>
    public int? AskedForLoadRunId { get; private set; }

    /// <summary>Whether the read happened at all.</summary>
    public bool WasAsked { get; private set; }

    /// <inheritdoc />
    public Task<LoadResumePoint> ReadAsync(
        int? loadRunId = null,
        CancellationToken cancellationToken = default)
    {
        log.Add("ReadResume");
        AskedForLoadRunId = loadRunId;
        WasAsked = true;

        return Task.FromResult(point);
    }

    /// <inheritdoc />
    public LoadResumePlan Plan(LoadResumePoint resumePoint, IEnumerable<HandlerVersion> walked)
    {
        log.Add("PlanResume");

        return resumePoint.Plan(walked);
    }
}

/// <summary>A journal that records what it buffered and what token each call was given.</summary>
/// <remarks>
/// Also its own factory, so a test can hold the instance the orchestrator will be handed. The real
/// <see cref="LoadJournalFactory"/> creates one per run; here there is one run.
/// </remarks>
/// <param name="log">The shared call log.</param>
internal sealed class RecordingJournal(CallLog log) : ILoadJournal, ILoadJournalFactory
{
    /// <summary>The run identifier the factory was asked for, which proves it came from script 510.</summary>
    public int CreatedForLoadRunId { get; private set; }

    /// <inheritdoc />
    public int PendingRows { get; private set; }

    /// <inheritdoc />
    public LoadJournalFlush Total { get; private set; } = LoadJournalFlush.Empty;

    /// <summary>Reported by every flush.</summary>
    public LoadJournalFlush FlushResult { get; set; } = LoadJournalFlush.Empty;

    /// <summary>Thrown by the next flush, if set. Cleared once thrown.</summary>
    public Exception? ThrowOnFlushOnce { get; set; }

    /// <summary>Every status element buffered as <c>Enumerate</c>.</summary>
    public List<HandlerLoadStatusElement> Enumerated { get; } = [];

    /// <summary>Every version buffered as <c>Skip</c>.</summary>
    public List<HandlerVersion> Skipped { get; } = [];

    /// <summary>Every attempt recorded, with the version it belonged to.</summary>
    public List<(ApiFetchResult Result, HandlerVersion Version)> Attempts { get; } = [];

    /// <summary>Every conclusion recorded, with the version it belonged to.</summary>
    public List<(ApiFetchResult Result, HandlerVersion Version)> Concluded { get; } = [];

    /// <summary>How many times the journal was flushed.</summary>
    public int Flushes { get; private set; }

    /// <summary>Whether the orchestrator disposed it.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Every call's name paired with whether its token could be cancelled.</summary>
    public List<(string Call, bool Cancellable)> Tokens { get; } = [];

    /// <inheritdoc />
    public ILoadJournal Create(int loadRunId)
    {
        CreatedForLoadRunId = loadRunId;

        return this;
    }

    /// <inheritdoc />
    public ValueTask EnumerateAsync(
        IEnumerable<HandlerLoadStatusElement> versions,
        CancellationToken cancellationToken = default)
    {
        HandlerLoadStatusElement[] elements = [.. versions];

        Record($"Enumerate({elements.Length})", cancellationToken);
        Enumerated.AddRange(elements);
        PendingRows += elements.Length;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RecordAttemptAsync(
        ApiFetchResult result,
        HandlerVersion version,
        int attemptNumber,
        CancellationToken cancellationToken = default)
    {
        Record("RecordAttempt", cancellationToken);

        lock (Attempts)
        {
            Attempts.Add((result, version));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> ConcludeAsync(
        ApiFetchResult result,
        HandlerVersion version,
        CancellationToken cancellationToken = default)
    {
        Record("Conclude", cancellationToken);
        Concluded.Add((result, version));

        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask SkipAsync(
        IEnumerable<HandlerVersion> versions,
        CancellationToken cancellationToken = default)
    {
        HandlerVersion[] elements = [.. versions];

        Record($"Skip({elements.Length})", cancellationToken);
        Skipped.AddRange(elements);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LoadJournalFlush> FlushAsync(CancellationToken cancellationToken = default)
    {
        Record("Flush", cancellationToken);

        if (ThrowOnFlushOnce is not null)
        {
            Exception error = ThrowOnFlushOnce;
            ThrowOnFlushOnce = null;

            throw error;
        }

        Flushes++;
        PendingRows = 0;
        Total = Total.Add(FlushResult);

        return Task.FromResult(FlushResult);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        log.Add("DisposeJournal");

        return ValueTask.CompletedTask;
    }

    private void Record(string call, CancellationToken cancellationToken)
    {
        log.Add(call);

        lock (Tokens)
        {
            Tokens.Add((call, cancellationToken.CanBeCanceled));
        }
    }
}

/// <summary>Builders for the shapes a run is assembled from.</summary>
internal static class Ran
{
    /// <summary>The window every default run asks for.</summary>
    public static readonly DateOnly From = new(2026, 8, 30);

    /// <summary>The last day every default run asks for, and where the watermark lands.</summary>
    public static readonly DateOnly To = new(2026, 9, 5);

    /// <summary>A watermark row that recommends an Incremental run over the default window.</summary>
    /// <param name="runMode">The recommended mode.</param>
    /// <param name="fromDate">The recommended start, or null to say the row recommends none.</param>
    /// <param name="toDate">The recommended end, or null to say the row recommends none.</param>
    /// <param name="isEnabled">Whether the feed is switched on.</param>
    /// <param name="overlapDays">The overlap script 512 already applied (G25).</param>
    /// <returns>The row.</returns>
    public static LoadWatermark Watermark(
        string? runMode = "Incremental",
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        bool isEnabled = true,
        int? overlapDays = 2) =>
        new()
        {
            LoadWatermarkId = 1,
            FeedName = LoadRun.HandlerSourceFeed,
            ActivityLocation = "MD",
            WatermarkDate = new DateOnly(2026, 8, 31),
            OverlapDays = 2,
            IsEnabled = isEnabled,
            RecommendedRunMode = runMode,
            RecommendedFromDate = runMode is null ? null : fromDate ?? From,
            RecommendedToDate = runMode is null ? null : toDate ?? To,
            RecommendedOverlapDaysApplied = overlapDays,
        };

    /// <summary>
    /// The row script 512 answers with on a database whose watermark has never advanced — the initial load.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A separate builder because the shape it produces cannot be reached through
    /// <see cref="Watermark"/>, and that shape is the one every first run meets.</b> Script 340 seeds
    /// <c>WatermarkDate</c> as <c>NULL</c>, and script 512 reads that as "ask for everything" by recommending
    /// <c>Full</c> with <b>no from-date at all</b>. Until <c>InitialLoadFromDate</c> existed, the orchestrator
    /// read that absent date as a broken row and refused with <c>NotConfigured</c> — so the loader could not
    /// perform an initial load at all.
    /// </para>
    /// <para>
    /// The end date is still recommended: <c>/hd/sources/summaries</c> requires <c>startDate</c> and has no
    /// paging and no envelope, so "everything" has to be given both a first and a last day.
    /// </para>
    /// </remarks>
    /// <param name="toDate">The end of the range script 512 recommends.</param>
    /// <returns>The row.</returns>
    public static LoadWatermark NeverAdvanced(DateOnly? toDate = null) =>
        new()
        {
            LoadWatermarkId = 1,
            FeedName = LoadRun.HandlerSourceFeed,
            ActivityLocation = "MD",
            WatermarkDate = null,
            OverlapDays = 2,
            IsEnabled = true,
            RecommendedRunMode = "Full",
            RecommendedFromDate = null,
            RecommendedToDate = toDate ?? To,
            RecommendedOverlapDaysApplied = 0,
        };

    /// <summary>One summary, in the shape <c>SummaryPayload</c> would have produced.</summary>
    /// <param name="handlerId">EPA's identifier.</param>
    /// <param name="sequence">The version sequence.</param>
    /// <returns>The summary.</returns>
    public static HandlerSourceSummary Summary(string handlerId = "MDD000000001", int sequence = 1) =>
        new()
        {
            HandlerId = handlerId,
            ActivityLocation = "MD",
            SourceType = "N",
            Sequence = sequence,
            CurrentRecord = sequence == 1,
        };

    /// <summary>A lookup stage report of the given shape.</summary>
    /// <param name="refreshed">How many lists refreshed.</param>
    /// <param name="unrefreshed">How many did not, as <c>FetchFailed</c>.</param>
    /// <param name="fatal">The outcome that stopped the stage, if one did.</param>
    /// <param name="cancelled">Whether the stage was cancelled.</param>
    /// <returns>The report.</returns>
    public static LookupStageReport Lookups(
        int refreshed = 23,
        int unrefreshed = 0,
        ApiFetchOutcome? fatal = null,
        bool cancelled = false)
    {
        List<LookupRefreshReport> reports = [];

        for (int index = 0; index < refreshed + unrefreshed; index++)
        {
            reports.Add(new LookupRefreshReport(
                RcraInfoLookups.All[index % RcraInfoLookups.All.Count],
                index < refreshed ? LookupRefreshStatus.Refreshed : LookupRefreshStatus.FetchFailed,
                index < refreshed ? 3 : 0,
                default,
                index < refreshed ? ApiFetchOutcome.Succeeded : ApiFetchOutcome.ServiceFailure,
                [],
                null));
        }

        return new LookupStageReport(reports, fatal, cancelled);
    }

    /// <summary>A walk report naming the given versions, with every window either walked or not.</summary>
    /// <param name="versions">The versions the walk named.</param>
    /// <param name="walkedWindows">How many windows were covered.</param>
    /// <param name="unwalkedWindows">How many were not, as <c>FetchFailed</c>.</param>
    /// <param name="fatal">The outcome that stopped the walk, if one did.</param>
    /// <param name="cancelled">Whether the walk was cancelled.</param>
    /// <returns>The report.</returns>
    public static SummaryWalkReport Walk(
        IEnumerable<HandlerSourceSummary>? versions = null,
        int walkedWindows = 1,
        int unwalkedWindows = 0,
        ApiFetchOutcome? fatal = null,
        bool cancelled = false)
    {
        HandlerSourceSummary[] named = [.. versions ?? [Summary()]];
        List<SummaryWindowReport> windows = [];

        for (int index = 0; index < walkedWindows + unwalkedWindows; index++)
        {
            DateWindow window = new(From.AddDays(index * 7), From.AddDays((index * 7) + 6));
            bool walked = index < walkedWindows;

            windows.Add(new SummaryWindowReport(
                window,
                walked ? SummaryWindowStatus.Walked : SummaryWindowStatus.FetchFailed,
                walked ? named.Length : 0,
                walked ? named.Length : 0,
                walked ? named.Count(summary => summary.CurrentRecord) : 0,
                walked ? ApiFetchOutcome.Succeeded : ApiFetchOutcome.ServiceFailure,
                walked ? 200 : 503,
                40,
                [],
                null));
        }

        return new SummaryWalkReport(windows, named, fatal, cancelled);
    }

    /// <summary>A fetch result carrying a body, for the merge path.</summary>
    /// <param name="version">The version fetched.</param>
    /// <param name="payload">The body EPA answered with.</param>
    /// <returns>The result.</returns>
    public static ApiFetchResult Fetched(HandlerVersion version, string payload = """{"handlerId":"x"}""")
    {
        DateTimeOffset started = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

        return new ApiFetchResult
        {
            Outcome = ApiFetchOutcome.Succeeded,
            Request = RcraInfoDataRequest.Source(version.HandlerId, version.SourceType, version.Sequence),
            StartedDateUtc = started,
            CompletedDateUtc = started.AddMilliseconds(90),
            DurationMs = 90,
            Payload = payload,
            HttpStatusCode = 200,
            ResponseBytes = payload.Length,
        };
    }

    /// <summary>
    /// A summaries body in the shape <c>/hd/sources/summaries?handlerId=…</c> answers with — a bare JSON array.
    /// </summary>
    /// <remarks>
    /// <b>Built as text rather than as <see cref="HandlerSourceSummary"/> objects, deliberately.</b> The
    /// targeted run's enumeration goes through <c>SummaryPayload.Read</c>, and that reader is the only thing
    /// standing between a hand-typed handler identifier and a merge — its casing rules, its width check and its
    /// <c>currentRecord</c> parsing all apply here. A double that handed the orchestrator ready-made objects
    /// would test the orchestrator against a payload EPA cannot send.
    /// </remarks>
    /// <param name="handlerId">EPA's identifier, repeated on every element.</param>
    /// <param name="activityLocation">
    /// The state each element names, or an empty string to omit the property. Present so the out-of-scope
    /// refusal can be provoked: on this request form the loader cannot ask EPA to filter, so the answer is the
    /// only place the check can happen.
    /// </param>
    /// <param name="versions">Each element's sequence and whether EPA flags it as the current record.</param>
    /// <returns>The body.</returns>
    public static string SummariesBody(
        string handlerId = "MDD000000001",
        string activityLocation = "MD",
        params (int Sequence, bool CurrentRecord)[] versions)
    {
        (int Sequence, bool CurrentRecord)[] named = versions.Length > 0 ? versions : [(1, true)];

        string location = string.IsNullOrEmpty(activityLocation)
            ? string.Empty
            : "\"activityLocation\":\"" + activityLocation + "\",";

        IEnumerable<string> elements = named.Select(version => string.Concat(
            "{",
            location,
            "\"handlerId\":\"",
            handlerId,
            "\",\"sourceType\":\"N\",\"sequence\":",
            version.Sequence.ToString(CultureInfo.InvariantCulture),
            ",\"currentRecord\":",
            version.CurrentRecord ? "true" : "false",
            "}"));

        return "[" + string.Join(",", elements) + "]";
    }

    /// <summary>A fetch result for the targeted run's one enumeration call.</summary>
    /// <param name="handlerId">The identifier the request names.</param>
    /// <param name="body">The body, or null for a single current version.</param>
    /// <param name="outcome">What happened.</param>
    /// <param name="httpStatusCode">The status, where there was one.</param>
    /// <returns>The result.</returns>
    /// <remarks>
    /// The request is built by <see cref="RcraInfoDataRequest.SummariesForHandler"/> rather than by hand, so
    /// the harness's answer can tell this call apart from the per-version fetches by its
    /// <see cref="RcraInfoDataRequest.Endpoint"/> — which is also how the orchestrator's own code distinguishes
    /// them.
    /// </remarks>
    public static ApiFetchResult Enumerated(
        string handlerId = "MDD000000001",
        string? body = null,
        ApiFetchOutcome outcome = ApiFetchOutcome.Succeeded,
        int? httpStatusCode = 200)
    {
        DateTimeOffset started = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);
        string? payload = outcome == ApiFetchOutcome.Succeeded ? body ?? SummariesBody(handlerId) : null;

        return new ApiFetchResult
        {
            Outcome = outcome,
            Request = RcraInfoDataRequest.SummariesForHandler(handlerId),
            StartedDateUtc = started,
            CompletedDateUtc = started.AddMilliseconds(60),
            DurationMs = 60,
            Payload = payload,
            HttpStatusCode = httpStatusCode,
            ResponseBytes = payload?.Length ?? 0,
        };
    }

    /// <summary>The handler a <c>/hd/sources/summaries?handlerId=…</c> request names.</summary>
    /// <param name="request">The request the reconcile stage sent.</param>
    /// <returns>The identifier, or an empty string if the request names none.</returns>
    /// <remarks>
    /// Read out of the query rather than tracked alongside, for <see cref="VersionIn"/>'s reason: the whole
    /// point of the reconcile stage is <i>which</i> handler it asks about, so a double that was told the answer
    /// could agree with itself while the stage asked about something else.
    /// </remarks>
    public static string HandlerIdIn(RcraInfoDataRequest request)
    {
        const string Marker = "handlerId=";

        int at = request.RelativeUri.IndexOf(Marker, StringComparison.Ordinal);

        return at < 0
            ? string.Empty
            : Uri.UnescapeDataString(request.RelativeUri[(at + Marker.Length)..].Split('&')[0]);
    }

    /// <summary>
    /// The answer to the reconcile stage's per-handler summaries call, built from the versions a walk named.
    /// </summary>
    /// <param name="request">The request, which names the handler.</param>
    /// <param name="versions">Every version the run knows about, across every handler.</param>
    /// <returns>The result, naming only the requested handler's versions.</returns>
    /// <remarks>
    /// <b>Filtered to the requested handler, because the stage refuses an answer that names another one.</b>
    /// That refusal exists because script 521 asserts a <i>complete</i> lineage: a list carrying a third
    /// handler's versions would demote every version of it the list happened to omit. A double that answered
    /// with the whole version set would trip it on every multi-handler test.
    /// </remarks>
    public static ApiFetchResult SummariesFor(
        RcraInfoDataRequest request,
        IReadOnlyList<HandlerVersion> versions)
    {
        string handlerId = HandlerIdIn(request);

        (int Sequence, bool CurrentRecord)[] named =
        [
            .. versions
                .Where(version => string.Equals(version.HandlerId, handlerId, StringComparison.Ordinal))
                .Select(version => (version.Sequence, version.Sequence == 1)),
        ];

        return named.Length == 0

            // EPA holds no summaries for a handler this run just merged a version for -- a contradiction the
            // stage reports as NoVersions rather than as a deletion. Reachable only from a test whose walk
            // named a version the version list does not, which is worth answering honestly rather than
            // defaulting to one version EPA never mentioned.
            ? Enumerated(handlerId, outcome: ApiFetchOutcome.NotFound, httpStatusCode: 404)
            : Enumerated(handlerId, SummariesBody(handlerId, "MD", named));
    }

    /// <summary>The version a <c>/hd/sources/{id}/{type}/{sequence}</c> request names.</summary>
    /// <param name="request">The request the orchestrator sent.</param>
    /// <returns>The version.</returns>
    /// <remarks>
    /// Parsed out of the path rather than matched against a list the test supplied, because the targeted run's
    /// version set comes from the <i>answer</i> to its enumeration — so a test that also had to restate that
    /// set could agree with itself while disagreeing with the payload.
    /// </remarks>
    public static HandlerVersion VersionIn(RcraInfoDataRequest request)
    {
        string[] segments = request.Path.Split('/');

        return new HandlerVersion(
            Uri.UnescapeDataString(segments[^3]),
            Uri.UnescapeDataString(segments[^2]),
            int.Parse(segments[^1], CultureInfo.InvariantCulture));
    }

    /// <summary>A fetch result that did not come back with a body.</summary>
    /// <param name="version">The version fetched.</param>
    /// <param name="outcome">What happened.</param>
    /// <param name="httpStatusCode">The status, where there was one.</param>
    /// <returns>The result.</returns>
    public static ApiFetchResult NotFetched(
        HandlerVersion version,
        ApiFetchOutcome outcome,
        int? httpStatusCode = null)
    {
        DateTimeOffset started = new(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);

        return new ApiFetchResult
        {
            Outcome = outcome,
            Request = RcraInfoDataRequest.Source(version.HandlerId, version.SourceType, version.Sequence),
            StartedDateUtc = started,
            CompletedDateUtc = started.AddMilliseconds(90),
            DurationMs = 90,
            HttpStatusCode = httpStatusCode,
        };
    }
}
