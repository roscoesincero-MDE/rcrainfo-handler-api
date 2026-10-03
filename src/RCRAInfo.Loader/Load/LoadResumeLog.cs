using Microsoft.Extensions.Logging;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="LoadResume"/>'s four log messages, as source-generated <c>LoggerMessage</c> delegates.
/// </summary>
/// <remarks>
/// <para>
/// Generated rather than hand-written because <c>CA1848</c> is an error under this solution's
/// <c>TreatWarningsAsErrors</c>. These four fire once per run rather than once per handler, so the cost is
/// not the reason here — consistency with the other three log classes is.
/// </para>
/// <para>
/// <b>Every parameter is a count, a run identifier, a run status or a two-letter activity location.</b>
/// Deliberately no handler identifier, even in <see cref="VersionsVanished"/> where naming the first few
/// would be convenient: AR8's rule for <c>logs.ExecutionLog.KeyParameters</c> is applied to this
/// application's own log too, and the versions themselves are in <c>logs.HandlerLoadStatus</c> where a
/// query can find them with the run number this message carries.
/// </para>
/// </remarks>
internal static partial class LoadResumeLog
{
    [LoggerMessage(
        EventId = 5261,
        Level = LogLevel.Information,
        Message = "Resume: nothing to resume for {ActivityLocation}. Either no previous run exists, the "
            + "last one succeeded, one is still live, or the last one is older than the configured limit. "
            + "This run will fetch every version its walk names, which is safe -- an unchanged version "
            + "merges as Unchanged.")]
    public static partial void NoResumePoint(ILogger logger, string activityLocation);

    [LoggerMessage(
        EventId = 5262,
        Level = LogLevel.Information,
        Message = "Resume: found run {ResumedFromLoadRunId} for {ActivityLocation} ({RunMode}, currently "
            + "{RunStatus}, started {StartedUtc:yyyy-MM-dd HH:mm}Z, {AgeHours}h ago) with {CompletedCount} "
            + "completed and {UnfinishedCount} unfinished version(s).")]
    public static partial void ResumePointFound(
        ILogger logger,
        int resumedFromLoadRunId,
        string activityLocation,
        string runMode,
        string runStatus,
        DateTime startedUtc,
        int ageHours,
        int completedCount,
        int unfinishedCount);

    [LoggerMessage(
        EventId = 5263,
        Level = LogLevel.Warning,
        Message = "Resume: run {ResumedFromLoadRunId} is still marked 'Running' and was resumed from "
            + "anyway, because it started {AgeHours}h ago and the abandonment threshold is "
            + "{AbandonAfterMinutes} minute(s). logs.uspStartLoadRun will mark it 'Abandoned' when this "
            + "run opens. If that run is in fact alive, this run and it are now both writing -- and the "
            + "threshold, which both procedures must be given the same value for, is where to look.")]
    public static partial void ResumedFromRunningRun(
        ILogger logger,
        int resumedFromLoadRunId,
        int ageHours,
        int abandonAfterMinutes);

    [LoggerMessage(
        EventId = 5264,
        Level = LogLevel.Warning,
        Message = "Resume: {VanishedCount} of run {ResumedFromLoadRunId}'s {KnownCount} version(s) were "
            + "not named by this run's summaries walk. Nothing is wrong with this run, and nothing can be "
            + "done about it here -- but EPA has stopped reporting records it reported before, which is "
            + "the symptom G25 describes: /hd/sources/summaries may not be filtering on the date field "
            + "this loader assumes. Query logs.HandlerLoadStatus for run {ResumedFromLoadRunId} to see "
            + "which.")]
    public static partial void VersionsVanished(
        ILogger logger,
        int vanishedCount,
        int resumedFromLoadRunId,
        int knownCount);

    [LoggerMessage(
        EventId = 5265,
        Level = LogLevel.Information,
        Message = "Resume: this run's walk named {WalkedCount} version(s); {ToFetchCount} will be fetched "
            + "and {ToSkipCount} ({SkipRate:F1}%) skipped as already loaded. The skipped ones still get a "
            + "Skip status row, so the run's own grid accounts for the whole population.")]
    public static partial void PlanBuilt(
        ILogger logger,
        int walkedCount,
        int toFetchCount,
        int toSkipCount,
        double skipRate);
}
