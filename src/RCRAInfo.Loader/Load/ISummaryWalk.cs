namespace RCRAInfo.Loader.Load;

/// <summary>
/// Walks EPA's handler-source change feed by date window and reports which versions exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>One method, and it decides nothing about fetching.</b> This stage answers "which handler versions
/// does EPA say exist in this date range", and nothing else — not which of them are already loaded (that is
/// the resume plan), not which to fetch first, and not what to do when one fails. Keeping it to the one
/// question is what lets the initial load and the incremental load share it: they differ only in the range
/// they pass, which is the favourable consequence plan [R28] identified.
/// </para>
/// <para>
/// <b>Nothing here writes to <c>logs.HandlerLoadStatus</c>, and that is a constraint of AR5 rather than a
/// choice.</b> Script 524 resolves every attempt element against a status row <i>of the same run</i>, and a
/// status row names a handler version — but a summaries call is about a date range and no version at all.
/// The same wall the lookup refresh ran into (§D2-lookup-refresh). So the walk's own fetch outcomes live in
/// its report and in the run's log, and it is the caller that turns the returned versions into
/// <c>Enumerate</c> rows.
/// </para>
/// </remarks>
public interface ISummaryWalk
{
    /// <summary>Walks the feed across an inclusive date range.</summary>
    /// <param name="loadRunId">The run doing the walking, from <c>logs.uspStartLoadRun</c>.</param>
    /// <param name="fromDate">The first day to cover, inclusive.</param>
    /// <param name="toDate">The last day to cover, inclusive.</param>
    /// <param name="cancellationToken">Cancels the walk; a cancelled walk is a report, not an exception.</param>
    /// <returns>
    /// The per-window reports, the distinct versions found, and whether the watermark may move.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The <c>RCRAInfoLoad</c> options are unusable, or <paramref name="toDate"/> is before
    /// <paramref name="fromDate"/>. Both are configuration or caller defects rather than answers from EPA,
    /// and both are the kind that otherwise produce a successful-looking empty run.
    /// </exception>
    Task<SummaryWalkReport> WalkAsync(
        int loadRunId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default);
}
