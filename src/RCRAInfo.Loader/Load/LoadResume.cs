using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Data.Results;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Reads <c>logs.uspGetHandlerLoadResumeSet</c> and turns its rows into two sets of handler versions.
/// </summary>
/// <remarks>
/// <para>
/// <b>The class is small because the design decisions are in the procedure and in
/// <see cref="LoadResumePoint.Plan"/>.</b> What is left here is the split of one result set into the two
/// sets the plan subtracts against, and the logging of a decision that is otherwise invisible: a resume
/// that quietly did not happen looks exactly like a first run, so <see cref="LoadResumeLog.NoResumePoint"/>
/// fires on the empty path too.
/// </para>
/// <para>
/// <b>Call order: this, then <c>logs.uspStartLoadRun</c>, then the walk.</b> Forced, and the reason is set
/// out on <see cref="ILoadResume"/> and <see cref="LoadRunOptions.AbandonAfterMinutes"/> — the start
/// procedure takes the resumed-from run as an <i>input</i>, and its abandonment sweep therefore has not
/// run when this read looks, so a reboot-killed run is still marked <c>Running</c> here. That case gets
/// its own warning, because it is the one where a wrong threshold produces two runs writing at once.
/// </para>
/// <para>
/// <b>Every status other than <c>Succeeded</c> lands in <see cref="LoadResumePoint.Unfinished"/>, and the
/// closed set is not re-checked.</b> <c>CK_logs_HandlerLoadStatus_Status</c> is the authority for what the
/// column can hold, and a value it admits that this code has not heard of should be re-fetched rather than
/// refused: the safe reading of an unrecognised status is "not known to be done".
/// </para>
/// </remarks>
/// <param name="reader">The one database call.</param>
/// <param name="options">The run's scope, and the two thresholds the procedure is given.</param>
/// <param name="clock">The clock, for the candidate's age in the log line. Injected so a test can fix it.</param>
/// <param name="logger">
/// Counts, run identifiers, statuses and a two-letter activity location. No handler identifier (AR8).
/// </param>
public sealed class LoadResume(
    ILoadResumeReader reader,
    IOptions<LoadRunOptions> options,
    TimeProvider clock,
    ILogger<LoadResume> logger) : ILoadResume
{
    private readonly LoadRunOptions runOptions = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<LoadResumePoint> ReadAsync(
        int? loadRunId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> problems = runOptions.Validate();

        if (problems.Count > 0)
        {
            // Thrown rather than reported, for SummaryWalk's reason: an empty resume point is a legitimate
            // answer that means "fetch everything", so a configuration failure returning one would make a
            // resumed run re-fetch the whole population and report success.
            throw new InvalidOperationException(
                "The RCRAInfoLoad configuration is not usable, so no resume point can be read: "
                + string.Join(" ", problems));
        }

        string activityLocation = runOptions.NormalizedActivityLocation();

        IReadOnlyList<HandlerLoadResumeRow> rows = await reader
            .ReadAsync(
                activityLocation,
                loadRunId,
                runOptions.AbandonAfterMinutes,
                runOptions.ResumeMaxAgeHours,
                cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            // Logged rather than passed over. This is the path where a misconfigured threshold or age
            // limit hides, and it produces no error, no exception and a successful run.
            LoadResumeLog.NoResumePoint(logger, activityLocation);

            return LoadResumePoint.None;
        }

        HashSet<HandlerVersion> completed = [];
        HashSet<HandlerVersion> unfinished = [];

        foreach (HandlerLoadResumeRow row in rows)
        {
            HandlerVersion version = new(row.HandlerId, row.SourceType, row.Sequence);

            // The only value that means "do not fetch this again". Everything else -- including a status
            // this code does not recognise -- is treated as unfinished, because the safe reading of an
            // unknown status is "not known to be done".
            if (string.Equals(row.Status, "Succeeded", StringComparison.Ordinal))
            {
                completed.Add(version);
            }
            else
            {
                unfinished.Add(version);
            }
        }

        // The procedure repeats the run's facts on every row, so any row carries them. Row zero rather
        // than a scan: they cannot differ, because the projection joins one run.
        HandlerLoadResumeRow first = rows[0];

        LoadResumePoint point = new(
            first.ResumedFromLoadRunId,
            first.ResumedFromRunMode,
            first.ResumedFromStatus,
            first.ResumedFromStartedDateUtc,
            first.ResumedFromRequestedFromDate,
            first.ResumedFromRequestedToDate,
            completed,
            unfinished);

        int ageHours = (int)Math.Min(
            (clock.GetUtcNow() - first.ResumedFromStartedDateUtc).TotalHours,
            int.MaxValue);

        LoadResumeLog.ResumePointFound(
            logger,
            first.ResumedFromLoadRunId,
            activityLocation,
            first.ResumedFromRunMode,
            first.ResumedFromStatus,
            first.ResumedFromStartedDateUtc.UtcDateTime,
            ageHours,
            completed.Count,
            unfinished.Count);

        if (string.Equals(first.ResumedFromStatus, "Running", StringComparison.Ordinal))
        {
            // The case the whole @AbandonAfterMinutes arrangement exists for, and the one where getting
            // the threshold wrong means two runs writing at once. It is normal after a reboot and it is
            // never routine, so it is a warning rather than an Information line.
            LoadResumeLog.ResumedFromRunningRun(
                logger, first.ResumedFromLoadRunId, ageHours, runOptions.AbandonAfterMinutes);
        }

        return point;
    }

    /// <inheritdoc />
    public LoadResumePlan Plan(LoadResumePoint point, IEnumerable<HandlerVersion> walked)
    {
        ArgumentNullException.ThrowIfNull(point);

        LoadResumePlan plan = point.Plan(walked);

        LoadResumeLog.PlanBuilt(
            logger, plan.WalkedCount, plan.ToFetch.Count, plan.ToSkip.Count, plan.SkipRate);

        if (plan.Vanished.Count > 0 && point.ResumedFromLoadRunId is int resumedFrom)
        {
            // The finding the resume set carries unfinished versions in order to make possible. Warning
            // and not Error: this run is correct and there is nothing it can do about it -- but nothing
            // else in the application holds both version sets, so if it is not said here it is not said.
            LoadResumeLog.VersionsVanished(
                logger, plan.Vanished.Count, resumedFrom, point.KnownCount);
        }

        return plan;
    }
}
