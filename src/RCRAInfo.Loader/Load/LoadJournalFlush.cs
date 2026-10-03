using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>What one flush wrote, and what the two procedures reported about it.</summary>
/// <param name="Enumerated">Status rows written in <c>Enumerate</c> mode.</param>
/// <param name="Attempted">Status rows written in <c>Attempt</c> mode.</param>
/// <param name="Failed">Status rows written in <c>Fail</c> mode.</param>
/// <param name="Skipped">Status rows written in <c>Skip</c> mode.</param>
/// <param name="AttemptRows">Rows appended to <c>logs.HandlerLoadAttempt</c> by script 524.</param>
/// <param name="RowsOrphaned">
/// Attempt elements naming a version this run never enumerated. A defect count, not a statistic.
/// </param>
/// <param name="ValuesWithheld">
/// Values script 524 replaced rather than stored. A defect count, not a statistic.
/// </param>
/// <param name="Calls">Database round-trips this flush made, across both procedures and all chunks.</param>
/// <remarks>
/// <para>
/// <b>There is no <c>Succeeded</c> member, and its absence is the design.</b> Script 520 has four modes
/// and none of them is a success: a row reaches <c>Succeeded</c> only from
/// <c>dbo.uspMergeHandlerSourceBatch</c> (a version committed) or from
/// <c>dbo.uspSoftDeleteHandlerSourceSet</c> (EPA withdrew it, so it was soft-deleted). Both write it
/// inside the transaction that made it true, which is precisely what the journal cannot do — it flushes
/// after the fact, in a batch, on a different connection turn. See <see cref="LoadJournal.ConcludeAsync"/>.
/// </para>
/// <para>
/// <see cref="RowsOrphaned"/> and <see cref="ValuesWithheld"/> should both be zero in a correct run.
/// They are carried up rather than discarded because script 524 deliberately writes what it can and
/// reports what it could not: refusing the flush would lose the diagnostic rows for every other handler
/// in the same buffer, so the report is the only signal there is.
/// </para>
/// </remarks>
public readonly record struct LoadJournalFlush(
    int Enumerated,
    int Attempted,
    int Failed,
    int Skipped,
    int AttemptRows,
    int RowsOrphaned,
    int ValuesWithheld,
    int Calls)
{
    /// <summary>A flush that wrote nothing, because there was nothing buffered.</summary>
    public static LoadJournalFlush Empty { get; }

    /// <summary>Status rows written across all four modes.</summary>
    public int StatusRows => Enumerated + Attempted + Failed + Skipped;

    /// <summary>Every row this flush wrote, in either table.</summary>
    public int TotalRows => StatusRows + AttemptRows;

    /// <summary>Whether either procedure reported something a correct run would not produce.</summary>
    public bool HasDefects => RowsOrphaned > 0 || ValuesWithheld > 0;

    /// <summary>Adds another flush's counts to this one, for a running total across a run.</summary>
    /// <param name="other">The flush to add.</param>
    /// <returns>The combined counts.</returns>
    public LoadJournalFlush Add(LoadJournalFlush other) =>
        new(
            Enumerated + other.Enumerated,
            Attempted + other.Attempted,
            Failed + other.Failed,
            Skipped + other.Skipped,
            AttemptRows + other.AttemptRows,
            RowsOrphaned + other.RowsOrphaned,
            ValuesWithheld + other.ValuesWithheld,
            Calls + other.Calls);

    /// <summary>Counts only. Holds no identifier, no path and no payload, so it is safe in any log.</summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "enumerated={0} attempted={1} failed={2} skipped={3} attempts={4} calls={5}"
            + " orphaned={6} withheld={7}",
            Enumerated,
            Attempted,
            Failed,
            Skipped,
            AttemptRows,
            Calls,
            RowsOrphaned,
            ValuesWithheld);
}
