using Microsoft.Extensions.Logging;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="SummaryWalk"/>'s log messages, as source-generated <c>LoggerMessage</c> delegates.
/// </summary>
/// <remarks>
/// <para>
/// Generated for <see cref="LookupRefreshLog"/>'s reason: <c>CA1848</c> is an error under this solution's
/// <c>TreatWarningsAsErrors</c>, and a second logging style in the same folder would cost more than it saves.
/// </para>
/// <para>
/// <b>Every parameter is a count, a date, a two-letter state, a run identifier, a status or a property
/// name.</b> Two absences are deliberate and both are one edit away from happening. There is <b>no handler
/// identifier</b> here, although the walk holds several hundred thousand of them and naming the first one
/// would make a window report far easier to chase — an identifier of a regulated entity is data, and AR8's
/// rule for <c>logs.ExecutionLog.KeyParameters</c> applies to this application's own log because the same
/// people read it and paste it into the same tickets. And there is <b>no relative URI</b>, only the two
/// dates: the query string of a summaries call is not a secret, but a message template that takes one is
/// the template a future endpoint's credentialed URI gets logged through.
/// </para>
/// </remarks>
internal static partial class SummaryWalkLog
{
    [LoggerMessage(
        EventId = 5251,
        Level = LogLevel.Information,
        Message = "Load run {LoadRunId}: walking the {ActivityLocation} summaries feed from {FromDate} to "
            + "{ToDate} in {WindowCount} window(s) of at most {WindowDays} day(s). /hd/sources/summaries "
            + "has no paging, so the window is the only lever on response size (plan [R28]).")]
    public static partial void WalkStarting(
        ILogger logger,
        int loadRunId,
        string activityLocation,
        DateOnly fromDate,
        DateOnly toDate,
        int windowCount,
        int windowDays);

    [LoggerMessage(
        EventId = 5252,
        Level = LogLevel.Debug,
        Message = "Load run {LoadRunId}: window {Window} walked — {SummaryCount} summary(ies), "
            + "{NewVersionCount} new version(s), {CurrentRecordCount} marked current, {DurationMs} ms.")]
    public static partial void WindowWalked(
        ILogger logger,
        int loadRunId,
        string window,
        int summaryCount,
        int newVersionCount,
        int currentRecordCount,
        int durationMs);

    [LoggerMessage(
        EventId = 5253,
        Level = LogLevel.Information,
        Message = "Load run {LoadRunId}: window {Window} came back {HttpStatusCode} and is treated as an "
            + "EMPTY window, not as a deletion. /hd/sources/summaries documents 404, and a date range with "
            + "no versions in it is exactly what this loader asks for on a quiet week. Reading it as "
            + "NotFound-means-gone would soft-delete handlers on the strength of a date range (AR7).")]
    public static partial void WindowEmptyByNotFound(
        ILogger logger,
        int loadRunId,
        string window,
        int? httpStatusCode);

    [LoggerMessage(
        EventId = 5254,
        Level = LogLevel.Warning,
        Message = "Load run {LoadRunId}: window {Window} was not walked ({Status}) — {Problem} The "
            + "watermark will NOT advance past it, so the range stays in scope for the next run. Every "
            + "other window's versions are still fetched.")]
    public static partial void WindowNotWalked(
        ILogger logger,
        int loadRunId,
        string window,
        SummaryWindowStatus status,
        string problem);

    [LoggerMessage(
        EventId = 5255,
        Level = LogLevel.Warning,
        Message = "Load run {LoadRunId}: window {Window} carried {PropertyCount} property name(s) this "
            + "loader cannot bind: {PropertyNames}. Every version was still read; whatever those "
            + "properties say about them was discarded. Re-pin spec/rcrainfo/swagger.json.")]
    public static partial void UnexpectedProperties(
        ILogger logger,
        int loadRunId,
        string window,
        int propertyCount,
        string propertyNames);

    [LoggerMessage(
        EventId = 5256,
        Level = LogLevel.Error,
        Message = "Load run {LoadRunId}: the summaries walk stopped at window {Window} on {FatalOutcome}, "
            + "which is fatal to the run. {WalkedCount} window(s) had been walked; {NotAttemptedCount} were "
            + "not attempted. This outcome will fail identically on every remaining window, so the walk "
            + "does not continue.")]
    public static partial void WalkStoppedFatally(
        ILogger logger,
        int loadRunId,
        string window,
        Api.ApiFetchOutcome fatalOutcome,
        int walkedCount,
        int notAttemptedCount);

    [LoggerMessage(
        EventId = 5257,
        Level = LogLevel.Information,
        Message = "Load run {LoadRunId}: summaries walk finished — {WalkedCount} of {WindowCount} "
            + "window(s) walked, {VersionCount} distinct version(s) to consider, {DuplicateCount} "
            + "duplicate(s) across windows. Watermark may advance: {MayAdvanceWatermark}. A non-zero "
            + "duplicate count means EPA's date filter is not the field this loader assumes it is (G25).")]
    public static partial void WalkFinished(
        ILogger logger,
        int loadRunId,
        int walkedCount,
        int windowCount,
        int versionCount,
        int duplicateCount,
        bool mayAdvanceWatermark);
}
