using RCRAInfo.Data;
using RCRAInfo.Data.Payloads;
using RCRAInfo.Data.Results;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// The database calls <see cref="LoadRun"/> makes that are not the journal's two.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second narrow seam over the same sealed context, for the reason
/// <see cref="ILoadJournalWriter"/> gives</b> — <c>RCRAInfoContext</c> is <c>sealed</c>, and the
/// orchestrator's decisions are the ones most worth testing: the order the stages run in, which failure
/// stops the run and which only downgrades its status, and that the watermark moves on exactly one
/// condition. None of that is observable against a real database without running a real load.
/// </para>
/// <para>
/// <b>It is deliberately a different interface rather than an extension of the journal's.</b> The journal
/// is handed to callers that write status rows and nothing else, and widening it would put
/// <see cref="AdvanceWatermarkAsync"/> — the one call in this project that can make a whole date range
/// permanently unreachable — within reach of every one of them. Two seams cost one more class; one wide
/// seam costs the argument that the journal cannot advance a watermark.
/// </para>
/// <para>
/// <b>There is no method here for <c>other-ids</c>, and that is a gap rather than a decision.</b>
/// <c>dbo.HandlerOtherIdentifier</c> exists (script 150) and no procedure writes it: script 400 says so in
/// its own header — that table "is NOT here and never will be: it comes from a different endpoint and is
/// not keyed to a handler VERSION". So the endpoint that roughly doubles the initial-load request count
/// (G14, [R8]) has a table, a client and no write path, and until it has one the orchestrator does not
/// call it. Fetching a payload with nowhere to put it would spend the requests and report success.
/// </para>
/// </remarks>
public interface ILoadRunWriter
{
    /// <summary>
    /// The most elements one merge or soft-delete call may carry, from <c>RCRAInfoDataOptions</c>.
    /// </summary>
    /// <remarks>
    /// Here rather than in <see cref="LoadRunOptions"/> for the reason
    /// <see cref="ILoadJournalWriter.MaxElementsPerCall"/> records: the limit belongs to the writer, and a
    /// copy of it in configuration is a second number that has to agree with the first.
    /// </remarks>
    int MaxElementsPerCall { get; }

    /// <summary>Reads a feed's watermark and the run it recommends. Script 512.</summary>
    /// <param name="feedName">The feed, <see cref="LoadRun.HandlerSourceFeed"/> in Phase 1.</param>
    /// <param name="activityLocation">The state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The watermark row, or <see langword="null"/> if the feed has none.</returns>
    /// <remarks>
    /// The recommendation is computed by the procedure and not by the loader, which is why this returns the
    /// whole row: <c>RecommendedRunMode</c>, <c>RecommendedFromDate</c> and <c>RecommendedToDate</c> already
    /// apply the overlap (G25), and deriving them a second time here would be two implementations of the
    /// one rule that decides whether a day is ever asked for again.
    /// </remarks>
    Task<LoadWatermark?> ReadWatermarkAsync(
        string feedName,
        string activityLocation,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the run. Script 510.</summary>
    /// <param name="request">What the run is and what it resumes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new run's identifier.</returns>
    Task<int> StartRunAsync(LoadRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Closes the run and records its counters. Script 511.</summary>
    /// <param name="loadRunId">The run.</param>
    /// <param name="status">One of <c>Succeeded</c>, <c>PartiallySucceeded</c>, <c>Failed</c>.</param>
    /// <param name="failureMessage">Why, composed by this project. Never a credential; never a URI.</param>
    /// <param name="counters">What the run did.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CompleteRunAsync(
        int loadRunId,
        string status,
        string? failureMessage,
        LoadRunCounters counters,
        CancellationToken cancellationToken = default);

    /// <summary>Merges one batch of fetched handler sources. Script 400.</summary>
    /// <param name="loadRunId">The run.</param>
    /// <param name="envelopes">The payloads, no more than <see cref="MaxElementsPerCall"/> of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The three counts, derived as script 400 intends.</returns>
    Task<MergeCounts> MergeAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerEnvelope> envelopes,
        CancellationToken cancellationToken = default);

    /// <summary>Soft deletes versions EPA answered <c>404</c> for. Script 522.</summary>
    /// <param name="loadRunId">The run.</param>
    /// <param name="keys">The natural keys, no more than <see cref="MaxElementsPerCall"/> of them.</param>
    /// <param name="reason">Why, recorded on every row and logged.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parent rows soft deleted.</returns>
    Task<int> SoftDeleteAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerKeyElement> keys,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-asserts <c>dbo.HandlerSource.CurrentRecord</c> for whole lineages from EPA's own list. Script 521.
    /// </summary>
    /// <param name="loadRunId">The run.</param>
    /// <param name="summaries">
    /// <b>The COMPLETE version list for every <c>(HandlerId, SourceType)</c> pair mentioned</b>, no more than
    /// <see cref="MaxElementsPerCall"/> of them. This is script 521's one hard contract and the reason this
    /// parameter is not the walk's own summaries: a live version of a mentioned pair that the list does not
    /// name is set to <c>CurrentRecord = 0</c> and reported as <c>VersionNotInSourceSummary</c>. A windowed
    /// walk names versions changed <i>in the window</i>, never a lineage, so feeding it here would demote
    /// most of the mirror.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows the procedure changed, and observations it recorded.</returns>
    /// <remarks>
    /// <b>A separate call from the merge, because the fact it writes is not a property of any one version.</b>
    /// EPA's <c>currentRecord</c> is a property of a handler's whole version list delivered as a property of
    /// one version, so adding version 13 says nothing about version 12 — and script 400 mirrors each version
    /// faithfully, which is exactly what leaves the lineage-level answer wrong. See Analysis §5.2 item 2.
    /// </remarks>
    Task<ReconcileCounts> ReconcileAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerVersionElement> summaries,
        CancellationToken cancellationToken = default);

    /// <summary>Moves the feed's watermark forward. Script 513.</summary>
    /// <param name="feedName">The feed.</param>
    /// <param name="activityLocation">The state.</param>
    /// <param name="watermarkDate">The last day this run actually covered.</param>
    /// <param name="loadRunId">The run that earned the move.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <b>Never rewinds</b> — <c>allowRewind</c> is left at its default of false, and the refusal is the
    /// protection script 513's own remarks describe: a watermark moved back by accident and then forward
    /// again skips the window between. The orchestrator therefore never has to reason about whether the
    /// date it computed is behind the stored one.
    /// </remarks>
    Task AdvanceWatermarkAsync(
        string feedName,
        string activityLocation,
        DateOnly watermarkDate,
        int loadRunId,
        CancellationToken cancellationToken = default);
}

/// <summary>What one <c>dbo.uspMergeHandlerSourceBatch</c> call did.</summary>
/// <param name="Inserted">Versions this database had not seen.</param>
/// <param name="Updated">Versions whose payload had changed.</param>
/// <param name="Unchanged">
/// Versions already current, <b>derived by subtraction</b> and not reported by the procedure.
/// </param>
/// <remarks>
/// The subtraction is script 400's own design, stated in the comment above its result set: "one row per
/// record inserted or updated; a record the caller sent that is not here was already current". Doing it in
/// the writer rather than in the orchestrator keeps the one place that knows the procedure's shape next to
/// the call, so a future result set that starts reporting <c>Unchanged</c> rows changes one class.
/// </remarks>
public readonly record struct MergeCounts(int Inserted, int Updated, int Unchanged)
{
    /// <summary>Nothing merged.</summary>
    public static MergeCounts Empty { get; }

    /// <summary>Versions the call accounted for.</summary>
    public int Total => Inserted + Updated + Unchanged;

    /// <summary>Adds another call's counts to these.</summary>
    /// <param name="other">The other call.</param>
    /// <returns>The sum.</returns>
    public MergeCounts Add(MergeCounts other) =>
        new(Inserted + other.Inserted, Updated + other.Updated, Unchanged + other.Unchanged);
}

/// <summary>What one <c>dbo.uspReconcileCurrentRecord</c> call did.</summary>
/// <param name="RowsAffected">
/// Versions whose <c>CurrentRecord</c> the procedure changed. <b>Zero is the expected value</b> — it means
/// the mirror already agreed with EPA, which is the normal case and the one this stage exists to keep normal.
/// </param>
/// <param name="Observations">
/// Rows the procedure wrote to <c>logs.DataQualityObservation</c> — <c>CurrentRecordAmbiguousInSource</c>,
/// <c>VersionNotInSourceSummary</c>, <c>CurrentRecordAmbiguousInMirror</c> or
/// <c>CurrentRecordMissingInMirror</c>. Non-zero is worth reading; it is not by itself a loader defect.
/// </param>
/// <remarks>
/// A loader-side struct rather than <c>RCRAInfo.Data.Results.ReconcileResult</c>, which additionally carries
/// the serialised <c>PayloadBatch</c>. That batch is the JSON body sent to the procedure, and the orchestrator
/// has no use for it — mirroring <see cref="MergeCounts"/> keeps the payload from travelling any further up
/// than the writer, which is AR8's rule about what may be held where it might be logged.
/// </remarks>
public readonly record struct ReconcileCounts(int RowsAffected, int Observations)
{
    /// <summary>Nothing reconciled.</summary>
    public static ReconcileCounts Empty { get; }

    /// <summary>Adds another call's counts to these.</summary>
    /// <param name="other">The other call.</param>
    /// <returns>The sum.</returns>
    public ReconcileCounts Add(ReconcileCounts other) =>
        new(RowsAffected + other.RowsAffected, Observations + other.Observations);
}
