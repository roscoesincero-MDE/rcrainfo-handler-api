namespace RCRAInfo.Loader.Load;

/// <summary>
/// Re-asserts <c>dbo.HandlerSource.CurrentRecord</c> for whole lineages, from EPA's own per-handler version
/// list.
/// </summary>
/// <remarks>
/// <para>
/// <b>This stage exists because <c>currentRecord</c> is stale by construction, and no amount of care in the
/// merge can fix that.</b> EPA's flag is a property of a handler's <i>whole version list</i> delivered as a
/// property of <i>one version</i>: adding version 13 says nothing about version 12, and version 12 is not in
/// the delta feed because version 12 did not change. Script 400 mirrors each version faithfully — measured,
/// <c>CurrentRecord IS NULL</c> is zero across every live version — and faithful per-version mirroring is
/// exactly what produces the wrong lineage-level answer. Analysis §5.2 item 2 states it; live Maryland data
/// demonstrates it, at <c>MDR000501742/N</c>, where sequences 12 and 13 both claim to be current.
/// </para>
/// <para>
/// <b>Two entry points, one write path, and the difference is only whether the caller already holds the
/// list.</b> The scheduled run does not: its walk names versions changed <i>in a date window</i>, never a
/// lineage, so <see cref="ReconcileAsync"/> asks <c>/hd/sources/summaries?handlerId=…</c> once per handler.
/// The targeted run does — that call is how it enumerated in the first place — so it uses
/// <see cref="ReconcileKnownAsync"/> and spends no request at all. Both project, batch and write through the
/// same private path, so there is one place that knows script 521's contract.
/// </para>
/// <para>
/// <b>That contract is the whole reason this interface is shaped around a handler rather than a version:
/// <c>@Summaries</c> must be the COMPLETE list for every <c>(HandlerId, SourceType)</c> pair it
/// mentions.</b> A live version of a mentioned pair that the list does not name is set to
/// <c>CurrentRecord = 0</c> and reported as <c>VersionNotInSourceSummary</c>. So a handler's versions are
/// never split across two calls, and a handler holding more versions than one call may carry is refused
/// whole rather than halved.
/// </para>
/// <para>
/// <b>Nothing here writes to <c>logs.HandlerLoadStatus</c>,</b> for <see cref="ISummaryWalk"/>'s reason: a
/// status row names a handler version, and a summaries call is about a handler. Script 521 writes its own
/// <c>logs.DataQualityObservation</c> and <c>logs.ExecutionLog</c> rows.
/// </para>
/// </remarks>
public interface ICurrentRecordReconcile
{
    /// <summary>
    /// Asks EPA for each handler's complete version list and asserts it. One request per handler.
    /// </summary>
    /// <param name="loadRunId">The run doing the reconciling.</param>
    /// <param name="handlerIds">
    /// The handlers to assert. De-duplicated by the caller or not; this method de-duplicates ordinally either
    /// way, because two windows naming the same handler must not cost two requests.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the stage; a cancelled stage is a report, not an exception.
    /// </param>
    /// <returns>One report per handler, the procedure's counts, and whether the run may call itself complete.</returns>
    /// <exception cref="InvalidOperationException">
    /// The <c>RCRAInfoLoad</c> options are unusable. Thrown rather than reported, for
    /// <see cref="ISummaryWalk.WalkAsync"/>'s reason — the activity location governs the scope check on the
    /// answer, and this endpoint's <c>handlerId</c> form cannot carry the filter in the request at all.
    /// </exception>
    Task<ReconcileReport> ReconcileAsync(
        int loadRunId,
        IReadOnlyCollection<string> handlerIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asserts one handler's lineage from a list the caller already has. Spends no HTTP request.
    /// </summary>
    /// <param name="loadRunId">The run doing the reconciling.</param>
    /// <param name="handlerId">The handler the list belongs to.</param>
    /// <param name="summaries">
    /// EPA's answer to <c>/hd/sources/summaries?handlerId=…</c> for that handler — the <b>complete</b> list,
    /// unfiltered. Passing the versions a targeted run <i>selected</i> would be the one mistake script 521's
    /// contract punishes: <c>--current-record</c> selects one version, and submitting only that one would
    /// demote every other version of the same lineage.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, holding exactly one handler.</returns>
    /// <remarks>
    /// The scope check runs here too, on the same <see cref="SummaryWalk.FindOutOfScope"/> the caller used. A
    /// second call on an already-checked list costs one string comparison per version, and the alternative is
    /// a contract note that some future caller reads as optional.
    /// </remarks>
    Task<ReconcileReport> ReconcileKnownAsync(
        int loadRunId,
        string handlerId,
        IReadOnlyList<HandlerSourceSummary> summaries,
        CancellationToken cancellationToken = default);
}
