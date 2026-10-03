using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// The two database calls <see cref="LoadJournal"/> makes, and the element limit they impose.
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface exists because <c>RCRAInfoContext</c> is <c>sealed</c>.</b> The journal's own
/// behaviour — when it flushes, in what order, how it chunks, and that it flushes on both exit paths —
/// is testable only against a writer that can be observed, and a sealed class cannot be subclassed into
/// one. It is deliberately narrow: two methods out of the context's nine, so the seam cannot become a
/// second way to reach the database.
/// </para>
/// <para>
/// <b><see cref="MaxElementsPerCall"/> lives here rather than in <see cref="LoadJournalOptions"/>,</b>
/// because the limit is the writer's and not the caller's: <c>PayloadJson.Serialize</c> throws
/// <c>ArgumentOutOfRangeException</c> above <c>RCRAInfoDataOptions.MaxPayloadElements</c>. Copying it into
/// a second setting would create two numbers that have to agree, and the failure when they stop agreeing
/// is a thrown flush — which is to say buffered rows lost from the one table that exists to make loss
/// visible.
/// </para>
/// </remarks>
public interface ILoadJournalWriter
{
    /// <summary>
    /// The most elements one call may carry. The journal chunks at this size; it never sends more.
    /// </summary>
    int MaxElementsPerCall { get; }

    /// <summary>Writes one mode's worth of status rows. Script 520.</summary>
    /// <param name="loadRunId">The run these rows belong to.</param>
    /// <param name="mode">One of <c>Enumerate</c>, <c>Attempt</c>, <c>Fail</c>, <c>Skip</c>.</param>
    /// <param name="elements">The rows, no more than <see cref="MaxElementsPerCall"/> of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many status rows the procedure wrote.</returns>
    Task<int> UpsertStatusAsync(
        int loadRunId,
        string mode,
        IReadOnlyCollection<HandlerLoadStatusElement> elements,
        CancellationToken cancellationToken = default);

    /// <summary>Appends attempt rows. Script 524.</summary>
    /// <param name="loadRunId">The run these rows belong to.</param>
    /// <param name="elements">The rows, no more than <see cref="MaxElementsPerCall"/> of them.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Rows written, plus the two defect counts the procedure reports.</returns>
    /// <remarks>
    /// The two defect counts are returned rather than swallowed: script 524 writes what it can and reports
    /// what it could not, and a caller that discards the report turns a defect into silence.
    /// </remarks>
    Task<AttemptWriteCounts> RecordAttemptsAsync(
        int loadRunId,
        IReadOnlyCollection<HandlerLoadAttemptElement> elements,
        CancellationToken cancellationToken = default);
}

/// <summary>What script 524 reported for one call.</summary>
/// <param name="RowsWritten">Attempt rows appended.</param>
/// <param name="RowsOrphaned">
/// Elements naming a handler version this run never enumerated. Should be zero: a non-zero count means
/// either the flush order was wrong or the orchestrator attempted something it had not enumerated.
/// </param>
/// <param name="ValuesWithheld">
/// Values the procedure replaced rather than stored — in practice a <c>RequestPath</c> that did not begin
/// with a slash (AR8). Should be zero, and is <c>ApiFetchResult.ToAttemptElement</c>'s job to keep there.
/// </param>
public readonly record struct AttemptWriteCounts(int RowsWritten, int RowsOrphaned, int ValuesWithheld);
