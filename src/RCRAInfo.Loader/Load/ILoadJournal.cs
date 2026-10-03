using RCRAInfo.Data.Payloads;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// The run's write-behind record of what it did: <c>logs.HandlerLoadStatus</c> and
/// <c>logs.HandlerLoadAttempt</c>, buffered and flushed as sets.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two obligations on every caller, both of which the orchestrator has to honour and neither of which
/// this interface can enforce.</b>
/// </para>
/// <para>
/// First, <see cref="FlushAsync"/> must be called before <c>dbo.uspMergeHandlerSourceBatch</c> runs for
/// any version in the buffer. Script 520's <c>Attempt</c>, <c>Fail</c> and <c>Skip</c> modes all exclude
/// rows already at <c>Succeeded</c> and report the count as <c>skippedSucceeded</c> — so a buffered
/// <c>Attempt</c> row flushed <i>after</i> the merge that marked its handler <c>Succeeded</c> is silently
/// dropped, and the attempt count an operator uses to judge whether EPA is struggling is the thing that
/// goes missing.
/// </para>
/// <para>
/// Second, <see cref="IAsyncDisposable.DisposeAsync"/> is a safety net and not the mechanism: it flushes and <b>swallows</b>
/// what the flush throws, because throwing out of a disposal that is unwinding from another exception
/// replaces the failure that ended the run with the failure to write about it. The orchestrator should
/// call <see cref="FlushAsync"/> on both its exit paths, where it can see the error.
/// </para>
/// </remarks>
public interface ILoadJournal : IAsyncDisposable
{
    /// <summary>How many rows are buffered and not yet written, across both tables.</summary>
    int PendingRows { get; }

    /// <summary>Everything flushed so far this run, for the run summary.</summary>
    LoadJournalFlush Total { get; }

    /// <summary>
    /// Buffers the versions this run intends to fetch, as <c>Pending</c> rows. Script 520,
    /// <c>Enumerate</c> mode.
    /// </summary>
    /// <param name="versions">The versions, one element each.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the rows are buffered, and any triggered flush has run.</returns>
    /// <remarks>
    /// This is what makes the run resumable and what makes an attempt row resolvable: script 524 matches
    /// each attempt to a status row of the same run and counts the ones it cannot as <c>@RowsOrphaned</c>.
    /// A version fetched but never enumerated therefore loses its attempt log.
    /// </remarks>
    ValueTask EnumerateAsync(
        IEnumerable<HandlerLoadStatusElement> versions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Buffers one HTTP call: its attempt row, and the <c>Attempt</c>-mode status row that moves the
    /// version to <c>InProgress</c> with this attempt number.
    /// </summary>
    /// <param name="result">The classified call.</param>
    /// <param name="version">The version the call was about.</param>
    /// <param name="attemptNumber">Which attempt this was, counted by the caller from 1.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the rows are buffered, and any triggered flush has run.</returns>
    /// <remarks>
    /// Called for <b>every</b> call, including the ones a retry supersedes — that is the difference
    /// between the two tables. The status row keeps the current state of the version; the attempt rows
    /// keep every call that led there, so a handler that eventually succeeded after two <c>429</c>s still
    /// says so.
    /// </remarks>
    ValueTask RecordAttemptAsync(
        ApiFetchResult result,
        HandlerVersion version,
        int attemptNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Buffers the final state of a version, once the caller has stopped retrying it.
    /// </summary>
    /// <param name="result">The last classified call for this version.</param>
    /// <param name="version">The version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> when this journal buffered the conclusion, <see langword="false"/> when
    /// another procedure owns it — see the remarks, which are the whole reason this returns anything.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Routes by <c>ApiFetchOutcomeExtensions.ToStatus</c>: <c>Failed</c> becomes script 520's
    /// <c>Fail</c> mode, <c>Skipped</c> its <c>Skip</c> mode, and <c>Succeeded</c> becomes
    /// <b>nothing at all</b>. That third case is not an omission. A status row reaches <c>Succeeded</c>
    /// only from the transaction that made it true: <c>dbo.uspMergeHandlerSourceBatch</c> when a version
    /// committed, or <c>dbo.uspSoftDeleteHandlerSourceSet</c> when EPA answered <c>404</c> and the version
    /// was withdrawn. Script 520 has no success mode, deliberately, and its own description says so.
    /// </para>
    /// <para>
    /// The caller therefore still owes the version a write: a merge for a payload, a soft delete for a
    /// <c>404</c>. Returning <see langword="false"/> is how that obligation is visible at the call site
    /// rather than implied by the outcome. A run that skipped it would leave the version reading
    /// <c>InProgress</c> forever, and the next run would re-fetch a record EPA has already withdrawn.
    /// </para>
    /// </remarks>
    ValueTask<bool> ConcludeAsync(
        ApiFetchResult result,
        HandlerVersion version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Buffers versions this run has decided not to fetch, as <c>Skipped</c> rows. Script 520,
    /// <c>Skip</c> mode.
    /// </summary>
    /// <param name="versions">The versions, by natural key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the rows are buffered, and any triggered flush has run.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is what a resumed run does with the previous run's successes</b> — the set
    /// <c>logs.uspGetHandlerLoadResumeSet</c> returns at <c>Status = 'Succeeded'</c>. Without it the
    /// resumed run's own grid shows only the fraction it re-fetched, and <c>enumerated</c> exceeds
    /// <c>fetched + failed</c> by several hundred thousand rows with nothing accounting for the
    /// difference. With it, the AR5 picture adds up and says <i>why</i>: 412,000 skipped, 188,000
    /// fetched.
    /// </para>
    /// <para>
    /// <b>Separate from <see cref="ConcludeAsync"/> rather than a case of it, and the reason is not
    /// tidiness.</b> <see cref="ConcludeAsync"/> routes an <see cref="ApiFetchResult"/>, so reaching
    /// <c>Skip</c> mode through it needs a result to route — and the only outcome that maps there is
    /// <c>Cancelled</c>, which <c>ToFailureElement</c> stamps as
    /// <c>ApiErrorCode = "LOADER-Cancelled"</c>. That is a lie about a version nothing went wrong with,
    /// written into the column an operator reads to find out what went wrong. Script 520's <c>Skip</c>
    /// mode takes the natural key and nothing else, deliberately: <i>"there is no reason column on this
    /// table; a skip's reason belongs to the run"</i>.
    /// </para>
    /// <para>
    /// <b>The versions must have been enumerated first</b>, in this run — <c>Skip</c> mode updates and
    /// never inserts. The resuming run gets that for free, because its summaries walk names them and
    /// <see cref="EnumerateAsync"/> takes the whole walk; the flush order puts <c>Enumerate</c> ahead of
    /// <c>Skip</c> even within one flush.
    /// </para>
    /// </remarks>
    ValueTask SkipAsync(
        IEnumerable<HandlerVersion> versions,
        CancellationToken cancellationToken = default);

    /// <summary>Writes everything buffered, now.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was written, and what the two procedures reported.</returns>
    /// <remarks>
    /// <para>
    /// Idempotent in the sense that matters: both procedures are safe to re-send (520 sets rather than
    /// increments, 524 inserts only what is missing), and a flush with an empty buffer makes no call and
    /// returns <see cref="LoadJournalFlush.Empty"/>.
    /// </para>
    /// <para>
    /// <b>A flush that throws has lost the rows it had taken, and the caller should treat that as fatal to
    /// the run.</b> Stated rather than fixed: putting the rows back would mean a deterministic failure — a
    /// revoked <c>GRANT</c>, an element the procedure refuses — retrying forever against a buffer that only
    /// grows, and then failing the shutdown flush as well, so that nothing is written at all. What is lost
    /// this way is one batch of diagnostic rows in a run that is ending; the versions themselves are
    /// re-enumerated by the next run either way, because that is what
    /// <c>logs.HandlerLoadStatus</c> is for.
    /// </para>
    /// </remarks>
    Task<LoadJournalFlush> FlushAsync(CancellationToken cancellationToken = default);
}
