using Microsoft.Extensions.Logging;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="CurrentRecordReconcile"/>'s log messages, as source-generated <c>LoggerMessage</c> delegates.
/// </summary>
/// <remarks>
/// <para>
/// Generated for <see cref="LookupRefreshLog"/>'s reason: <c>CA1848</c> is an error under this solution's
/// <c>TreatWarningsAsErrors</c>.
/// </para>
/// <para>
/// <b>No handler identifier, and here the omission takes more discipline than anywhere else in the folder.</b>
/// This stage is organised entirely around individual handlers, every message below would read better with one
/// named, and a handler identifier is not a secret — plan §D3 and script 506 both say so, and
/// <see cref="HandlerReconcileReport.HandlerId"/> carries it. What stops it appearing here is where the
/// application log goes: the same operators, the same tickets, and the AR8 rule
/// <see cref="SummaryWalkLog"/> applies for the same reason. The <i>report</i> names the handler for whoever
/// is chasing one; the log names counts, statuses, run identifiers and durations.
/// </para>
/// </remarks>
internal static partial class CurrentRecordReconcileLog
{
    [LoggerMessage(
        EventId = 5296,
        Level = LogLevel.Information,
        Message = "Load run {LoadRunId}: reconciling CurrentRecord for {HandlerCount} handler lineage(s), "
            + "{RequestCount} of which need a summaries call. EPA's currentRecord is a property of a "
            + "handler's whole version list delivered on one version, so a version that did not change still "
            + "has a flag that did (Analysis §5.2 item 2).")]
    public static partial void ReconcileStarting(
        ILogger logger,
        int loadRunId,
        int handlerCount,
        int requestCount);

    [LoggerMessage(
        EventId = 5297,
        Level = LogLevel.Debug,
        Message = "Load run {LoadRunId}: a handler lineage read {VersionCount} version(s), "
            + "{CurrentRecordCount} of them flagged current, in {DurationMs} ms. Zero flagged current is a "
            + "legitimate answer from EPA and is not corrected here ([R38]).")]
    public static partial void HandlerRead(
        ILogger logger,
        int loadRunId,
        int versionCount,
        int currentRecordCount,
        int durationMs);

    [LoggerMessage(
        EventId = 5298,
        Level = LogLevel.Warning,
        Message = "Load run {LoadRunId}: a handler lineage was NOT asserted ({Status}) — {Problem} Its "
            + "versions are still merged; what may be wrong is the CurrentRecord flag, which is findable by "
            + "query and repairable by a targeted run on that handler. The watermark is NOT held for this.")]
    public static partial void HandlerNotReconciled(
        ILogger logger,
        int loadRunId,
        HandlerReconcileStatus status,
        string problem);

    [LoggerMessage(
        EventId = 5299,
        Level = LogLevel.Error,
        Message = "Load run {LoadRunId}: a handler holds {VersionCount} version(s), more than the "
            + "{MaxElementsPerCall} one dbo.uspReconcileCurrentRecord call may carry, so its lineage was "
            + "REFUSED WHOLE rather than split. Splitting is not a smaller assertion: script 521 sets every "
            + "unmentioned live version of a mentioned pair to CurrentRecord = 0, so half a list would demote "
            + "the other half. Raise RCRAInfoData:MaxPayloadElements.")]
    public static partial void HandlerTooManyVersions(
        ILogger logger,
        int loadRunId,
        int versionCount,
        int maxElementsPerCall);

    [LoggerMessage(
        EventId = 5300,
        Level = LogLevel.Debug,
        Message = "Load run {LoadRunId}: reconcile batch {BatchNumber} carried {HandlerCount} whole "
            + "lineage(s) and {VersionCount} version(s); the procedure changed {RowsAffected} row(s) and "
            + "recorded {Observations} observation(s). RowsAffected = 0 is the expected value.")]
    public static partial void BatchReconciled(
        ILogger logger,
        int loadRunId,
        int batchNumber,
        int handlerCount,
        int versionCount,
        int rowsAffected,
        int observations);

    [LoggerMessage(
        EventId = 5301,
        Level = LogLevel.Error,
        Message = "Load run {LoadRunId}: the CurrentRecord reconciliation stopped on {FatalOutcome}, which is "
            + "fatal to the run. {ReconciledCount} lineage(s) had been asserted; {NotAttemptedCount} were not "
            + "attempted. This outcome will fail identically on every remaining handler.")]
    public static partial void ReconcileStoppedFatally(
        ILogger logger,
        int loadRunId,
        Api.ApiFetchOutcome fatalOutcome,
        int reconciledCount,
        int notAttemptedCount);

    [LoggerMessage(
        EventId = 5302,
        Level = LogLevel.Information,
        Message = "Load run {LoadRunId}: CurrentRecord reconciliation finished — {ReconciledCount} of "
            + "{ConsideredCount} lineage(s) asserted in {WriteCalls} procedure call(s), {RowsAffected} flag(s) "
            + "corrected, {Observations} observation(s) recorded. Complete: {Complete}. A non-zero correction "
            + "count is this stage doing its job, not a defect in the merge.")]
    public static partial void ReconcileFinished(
        ILogger logger,
        int loadRunId,
        int reconciledCount,
        int consideredCount,
        int writeCalls,
        int rowsAffected,
        int observations,
        bool complete);
}
