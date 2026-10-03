namespace RCRAInfo.Loader.Load;

/// <summary>
/// Finds the previous unfinished run of this activity location and reports what it already dealt with.
/// </summary>
/// <remarks>
/// <para>
/// <b>One method, and it must be called before the run starts.</b> <c>logs.uspStartLoadRun</c> takes
/// <c>@ResumedFromLoadRunId</c> as an input and records it on the row it inserts, so the answer has to
/// exist before that row does. The ordering has a consequence that is the single least obvious thing
/// about the resume: the abandonment sweep also lives in the start procedure, so at the moment this read
/// looks, a run killed by a reboot is <i>still</i> marked <c>Running</c>. See
/// <see cref="LoadRunOptions.AbandonAfterMinutes"/>, which exists as one setting for exactly that
/// reason.
/// </para>
/// <para>
/// <b>It decides nothing about fetching, in the same way <see cref="ISummaryWalk"/> decides nothing
/// about loading.</b> This stage answers "what did the last run get through"; the walk answers "what does
/// EPA say exists now"; the subtraction of the two is <see cref="LoadResumePoint.Plan"/>, which is pure
/// and belongs to neither. Keeping the three apart is what makes the interesting part — the arithmetic —
/// testable without a database or an HTTP call.
/// </para>
/// <para>
/// <b>Nothing here writes.</b> The <c>Skip</c> rows that record the decision are the caller's, through
/// <see cref="ILoadJournal.SkipAsync"/>, and they can only be written after the new run exists and has
/// enumerated the versions.
/// </para>
/// </remarks>
public interface ILoadResume
{
    /// <summary>Reads the resume point for the configured activity location.</summary>
    /// <param name="loadRunId">
    /// A specific run to resume from, or <see langword="null"/> — the ordinary case, and what the
    /// scheduled task passes — to resume from the previous run of this activity location if there is one.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resume point, or <see cref="LoadResumePoint.None"/> when there is nothing to resume from —
    /// which is a normal answer on four separate paths and not an error. See
    /// <see cref="LoadResumePoint"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The <c>RCRAInfoLoad</c> options are unusable. Thrown rather than reported, for
    /// <see cref="ISummaryWalk"/>'s reason: an empty report here is indistinguishable from "there was
    /// nothing to resume", and that reading makes a resumed run re-fetch the whole population and still
    /// report success.
    /// </exception>
    /// <exception cref="Microsoft.Data.SqlClient.SqlException">
    /// <paramref name="loadRunId"/> names a run that does not exist, belongs to another activity
    /// location, or is still live. Script 525 raises for those three and returns an empty set for
    /// everything else; a caller-supplied run number that is wrong has no safe reading.
    /// </exception>
    Task<LoadResumePoint> ReadAsync(
        int? loadRunId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Splits this run's walk into what to fetch and what to skip, and reports what the walk did not
    /// name.
    /// </summary>
    /// <param name="point">The resume point, from <see cref="ReadAsync"/>.</param>
    /// <param name="walked">
    /// The versions this run's summaries walk found —
    /// <c>SummaryWalkReport.Versions</c> mapped through <c>HandlerSourceSummary.ToVersion</c>.
    /// </param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// Thin over <see cref="LoadResumePoint.Plan"/>, which does the arithmetic and is pure. This exists
    /// so the run has one place that logs the outcome, including the G25 warning for versions the
    /// previous run knew about and this walk did not name — the whole reason the resume set carries
    /// unfinished versions as well as completed ones.
    /// </remarks>
    LoadResumePlan Plan(LoadResumePoint point, IEnumerable<HandlerVersion> walked);
}
