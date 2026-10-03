using Microsoft.Extensions.Logging;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="LoadJournal"/>'s two log messages, as source-generated <c>LoggerMessage</c> delegates.
/// </summary>
/// <remarks>
/// <para>
/// Generated rather than hand-written because <c>CA1848</c> is an error under this solution's
/// <c>TreatWarningsAsErrors</c>, and the reason it is worth having as an error here rather than suppressed
/// is the shape of this application: the journal's logging sits inside the per-handler loop of a run that
/// covers several hundred thousand versions, which is the one place where the boxing and the template
/// re-parse of the extension-method form actually costs something.
/// </para>
/// <para>
/// <b>Every parameter below is a count or a run identifier.</b> Nothing here takes a payload, a request
/// path, a query string, a header or an EPA error message — AR8's rule for
/// <c>logs.ExecutionLog.KeyParameters</c> applies just as much to a message this application writes to its
/// own log, because the log is read by the same people and copied into the same tickets.
/// </para>
/// </remarks>
internal static partial class LoadJournalLog
{
    [LoggerMessage(
        EventId = 5241,
        Level = LogLevel.Error,
        Message = "Load run {LoadRunId}: the attempt log reported {RowsOrphaned} orphaned row(s) and "
            + "{ValuesWithheld} withheld value(s). Orphaned means an attempt named a version this run "
            + "never enumerated; withheld means a request path that did not begin with a slash (AR8). "
            + "Both should be zero in a correct run.")]
    public static partial void AttemptLogDefects(
        ILogger logger,
        int loadRunId,
        int rowsOrphaned,
        int valuesWithheld);

    [LoggerMessage(
        EventId = 5242,
        Level = LogLevel.Warning,
        Message = "Load run {LoadRunId}: {PendingRows} journal row(s) were still buffered at shutdown and "
            + "have been written ({Flush}). The orchestrator is expected to flush on both of its exit "
            + "paths; reaching this path means it did not.")]
    public static partial void FlushedAtShutdown(
        ILogger logger,
        int loadRunId,
        int pendingRows,
        string flush);

    [LoggerMessage(
        EventId = 5243,
        Level = LogLevel.Error,
        Message = "Load run {LoadRunId}: the final journal flush failed with {PendingRows} row(s) "
            + "buffered. Those rows are lost, so logs.HandlerLoadStatus understates what this run did.")]
    public static partial void FinalFlushFailed(
        ILogger logger,
        Exception error,
        int loadRunId,
        int pendingRows);
}
