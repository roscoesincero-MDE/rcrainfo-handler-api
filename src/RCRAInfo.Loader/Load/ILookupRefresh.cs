namespace RCRAInfo.Loader.Load;

/// <summary>
/// The run's first stage: refresh the 23 mirrored EPA code lists before any handler data is fetched (G15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this runs first, and what "first" actually buys.</b> A handler version merged against a code list
/// that has not yet seen a code EPA published this morning produces a
/// <c>logs.DataQualityObservation</c> row rather than a failure — the handler tables carry no foreign keys
/// to the lookup tables, which was G15's decision. So the ordering does not make an unknown code
/// impossible; it makes one rare. That is the whole claim, and it is why a code list that could not be
/// fetched does <b>not</b> end the run.
/// </para>
/// <para>
/// <b>The asymmetry that governs everything in this stage: a failure changes nothing, a success can delete.</b>
/// Script 523 in <c>Full</c> mode retires every code absent from the payload, within the activity locations
/// the payload mentions. A call that fails never reaches the procedure, so the previous mirror stands
/// complete and one run stale. A call that succeeds with a narrower payload than intended retires the
/// difference and reports success. Every refusal in the implementation is aimed at the second case.
/// </para>
/// </remarks>
public interface ILookupRefresh
{
    /// <summary>Refreshes every mirrored code list.</summary>
    /// <param name="loadRunId">The run these refreshes belong to — <c>logs.LoadRun.LoadRunId</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// One report per list, always as many as there are lists, and whether the run may continue.
    /// </returns>
    /// <remarks>
    /// Does not throw for a failed list, a rejected payload or a refused write; all three are reported. It
    /// throws only for the failures that mean the stage could not run at all — an unusable
    /// <c>ActivityLocation</c>, or a null argument.
    /// </remarks>
    Task<LookupStageReport> RefreshAllAsync(int loadRunId, CancellationToken cancellationToken = default);
}
