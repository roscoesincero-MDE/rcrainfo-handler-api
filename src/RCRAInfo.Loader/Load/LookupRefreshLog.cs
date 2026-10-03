using Microsoft.Extensions.Logging;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="LookupRefresh"/>'s log messages, as source-generated <c>LoggerMessage</c> delegates.
/// </summary>
/// <remarks>
/// <para>
/// Generated rather than written as extension calls because <c>CA1848</c> is an error under this solution's
/// <c>TreatWarningsAsErrors</c>. The performance argument that justifies it for <see cref="LoadJournalLog"/>
/// does not apply here — this is 23 calls, not several hundred thousand — but a second logging style in the
/// same folder would be the more expensive thing.
/// </para>
/// <para>
/// <b>Every parameter is a count, a list name, a status, or a property name.</b> A list name is one of 23
/// fixed strings and a property name is schema; neither is data. Nothing here takes a payload, a URI, a
/// query string, a header or EPA's error prose — AR8's rule for <c>logs.ExecutionLog.KeyParameters</c>
/// applies to this application's own log for the reason <see cref="LoadJournalLog"/> gives.
/// </para>
/// </remarks>
internal static partial class LookupRefreshLog
{
    [LoggerMessage(
        EventId = 5231,
        Level = LogLevel.Information,
        Message = "Load run {LoadRunId}: refreshing {ListCount} mirrored EPA code list(s) for "
            + "{ActivityLocation} before any handler data (G15).")]
    public static partial void StageStarting(
        ILogger logger,
        int loadRunId,
        int listCount,
        string activityLocation);

    [LoggerMessage(
        EventId = 5232,
        Level = LogLevel.Debug,
        Message = "Load run {LoadRunId}: {LookupName} refreshed — {ElementCount} code(s) published, "
            + "{RowsAffected} written, {RetiredRows} retired, {ChildRows} child row(s).")]
    public static partial void ListRefreshed(
        ILogger logger,
        int loadRunId,
        string lookupName,
        int elementCount,
        int rowsAffected,
        int retiredRows,
        int childRows);

    [LoggerMessage(
        EventId = 5233,
        Level = LogLevel.Warning,
        Message = "Load run {LoadRunId}: {LookupName} was not refreshed ({Status}) — {Problem} The "
            + "previous mirror is intact and one run stale; script 523 was not called, so nothing was "
            + "retired.")]
    public static partial void ListNotRefreshed(
        ILogger logger,
        int loadRunId,
        string lookupName,
        LookupRefreshStatus status,
        string problem);

    [LoggerMessage(
        EventId = 5234,
        Level = LogLevel.Error,
        Message = "Load run {LoadRunId}: {LookupName} failed to write ({Problem}). Script 523 runs in one "
            + "transaction, so the list is either fully refreshed or untouched — but the run cannot tell "
            + "which from here.")]
    public static partial void ListWriteFailed(
        ILogger logger,
        Exception error,
        int loadRunId,
        string lookupName,
        string problem);

    [LoggerMessage(
        EventId = 5235,
        Level = LogLevel.Warning,
        Message = "Load run {LoadRunId}: {LookupName} carried {PropertyCount} property name(s) this loader "
            + "cannot bind: {PropertyNames}. Every code was stored; whatever those properties say about the "
            + "codes was discarded. The pinned spec in spec/rcrainfo/swagger.json no longer matches the "
            + "live service — re-pin it and re-run build/check_lookup_catalog.py.")]
    public static partial void UnexpectedProperties(
        ILogger logger,
        int loadRunId,
        string lookupName,
        int propertyCount,
        string propertyNames);

    [LoggerMessage(
        EventId = 5236,
        Level = LogLevel.Error,
        Message = "Load run {LoadRunId}: the lookup stage stopped at {LookupName} on {FatalOutcome}, which "
            + "is fatal to the run. {RefreshedCount} list(s) had refreshed; {NotAttemptedCount} were not "
            + "attempted. This outcome will fail identically on every remaining list and on every handler, "
            + "so the run does not continue.")]
    public static partial void StageStoppedFatally(
        ILogger logger,
        int loadRunId,
        string lookupName,
        Api.ApiFetchOutcome fatalOutcome,
        int refreshedCount,
        int notAttemptedCount);

    [LoggerMessage(
        EventId = 5237,
        Level = LogLevel.Information,
        Message = "Load run {LoadRunId}: lookup stage finished — {RefreshedCount} of {ListCount} list(s) "
            + "refreshed, {RetiredCount} code(s) retired. A retirement count near a list's own size is a "
            + "narrower payload than intended rather than EPA withdrawing codes, and the run reports "
            + "success either way.")]
    public static partial void StageFinished(
        ILogger logger,
        int loadRunId,
        int refreshedCount,
        int listCount,
        int retiredCount);
}
