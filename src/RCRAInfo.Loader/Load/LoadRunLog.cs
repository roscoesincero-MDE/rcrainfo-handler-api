using Microsoft.Extensions.Logging;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// <see cref="LoadRun"/>'s log messages, as source-generated <c>LoggerMessage</c> delegates.
/// </summary>
/// <remarks>
/// <para>
/// Generated rather than hand-written because <c>CA1848</c> is an error under this solution's
/// <c>TreatWarningsAsErrors</c>. Every one of these fires once per run or once per batch — never once per
/// handler, which is the orchestrator's own reason for buffering everything else.
/// </para>
/// <para>
/// <b>Every parameter is a count, a run identifier, a date, a stage name, a status or a two-letter activity
/// location.</b> Same rule as <see cref="LoadResumeLog"/>, <see cref="LoadJournalLog"/>,
/// <see cref="SummaryWalkLog"/> and <see cref="LookupRefreshLog"/>: no handler identifier and no part of
/// any payload, because the versions are in <c>logs.HandlerLoadStatus</c> and the run number carried here
/// is what finds them. <see cref="RunFailed"/> takes a message this project composed and never an
/// exception's <c>ToString</c> — the same value goes to <c>logs.LoadRun.FailureMessage</c>, which scripts
/// 500 and 502 return to a web page.
/// </para>
/// </remarks>
internal static partial class LoadRunLog
{
    [LoggerMessage(
        EventId = 5271,
        Level = LogLevel.Information,
        Message = "Load: starting a {RunMode} run for {ActivityLocation} over {FromDate:yyyy-MM-dd} to "
            + "{ToDate:yyyy-MM-dd} inclusive, in windows of {WindowDays} day(s). Overlap applied: "
            + "{OverlapDays} day(s).")]
    public static partial void RunPlanned(
        ILogger logger,
        string runMode,
        string activityLocation,
        DateOnly fromDate,
        DateOnly toDate,
        int windowDays,
        int overlapDays);

    [LoggerMessage(
        EventId = 5272,
        Level = LogLevel.Information,
        Message = "Load: run {LoadRunId} is open (resumed from {ResumedFromLoadRunId}).")]
    public static partial void RunOpened(ILogger logger, int loadRunId, int? resumedFromLoadRunId);

    [LoggerMessage(
        EventId = 5273,
        Level = LogLevel.Warning,
        Message = "Load: {FeedName}/{ActivityLocation} is disabled in config.LoadWatermark, so no run was "
            + "opened and nothing was fetched. This is an operator setting and not a fault -- but a "
            + "scheduled task that reports success while a feed is switched off is invisible for as long "
            + "as nobody checks, which is why it is a warning.")]
    public static partial void FeedDisabled(ILogger logger, string feedName, string activityLocation);

    [LoggerMessage(
        EventId = 5274,
        Level = LogLevel.Warning,
        Message = "Load: script 510 refused to open a run because one is already live for "
            + "{ActivityLocation}. Nothing was fetched. If no loader is running, the previous run's row is "
            + "stale and will be swept to 'Abandoned' once it is older than {AbandonAfterMinutes} "
            + "minute(s).")]
    public static partial void AlreadyRunning(
        ILogger logger,
        string activityLocation,
        int abandonAfterMinutes);

    [LoggerMessage(
        EventId = 5275,
        Level = LogLevel.Information,
        Message = "Load: lookups refreshed first (G15) -- {RefreshedCount} of {ListCount} list(s), "
            + "{RetiredCount} code(s) retired.")]
    public static partial void LookupsRefreshed(
        ILogger logger,
        int refreshedCount,
        int listCount,
        int retiredCount);

    [LoggerMessage(
        EventId = 5276,
        Level = LogLevel.Error,
        Message = "Load: the lookup refresh stage stopped on {FatalOutcome} and no handler data will be "
            + "fetched. A credential or connectivity failure on a code list is the same failure the fetch "
            + "loop would meet several hundred thousand times, and G15 puts the lookups first precisely so "
            + "it is met once.")]
    public static partial void LookupsFatal(ILogger logger, string fatalOutcome);

    [LoggerMessage(
        EventId = 5277,
        Level = LogLevel.Warning,
        Message = "Load: {UnrefreshedCount} of {ListCount} lookup list(s) did not refresh. Handler data "
            + "will still be fetched -- a stale code list renders a label wrongly, it does not corrupt a "
            + "handler record -- but this run cannot report Succeeded, because a code EPA has withdrawn is "
            + "still live in this mirror and nothing else will notice.")]
    public static partial void LookupsIncomplete(ILogger logger, int unrefreshedCount, int listCount);

    [LoggerMessage(
        EventId = 5278,
        Level = LogLevel.Information,
        Message = "Load: walk covered {WalkedCount} of {WindowCount} window(s) and named {VersionCount} "
            + "distinct version(s) from {SummaryCount} summary row(s).")]
    public static partial void WalkComplete(
        ILogger logger,
        int walkedCount,
        int windowCount,
        int versionCount,
        int summaryCount);

    [LoggerMessage(
        EventId = 5279,
        Level = LogLevel.Error,
        Message = "Load: the summaries walk stopped on {FatalOutcome} after covering {WalkedCount} of "
            + "{WindowCount} window(s), and no version will be fetched in this run. The versions it did "
            + "name are enumerated in logs.HandlerLoadStatus and the next run resumes from them.")]
    public static partial void WalkFatal(
        ILogger logger,
        string fatalOutcome,
        int walkedCount,
        int windowCount);

    [LoggerMessage(
        EventId = 5280,
        Level = LogLevel.Information,
        Message = "Load: batch {BatchNumber} of {BatchCount} -- {Fetched} fetched, {Inserted} inserted, "
            + "{Updated} updated, {Unchanged} unchanged, {SoftDeleted} soft deleted, {Failed} failed.")]
    public static partial void BatchMerged(
        ILogger logger,
        int batchNumber,
        int batchCount,
        int fetched,
        int inserted,
        int updated,
        int unchanged,
        int softDeleted,
        int failed);

    [LoggerMessage(
        EventId = 5281,
        Level = LogLevel.Error,
        Message = "Load: stopping the fetch loop after batch {BatchNumber} of {BatchCount} on "
            + "{FatalOutcome}. That outcome is fatal to the run rather than to one version -- a rejected "
            + "credential, a denied scope or a request EPA will not accept does not become correct on the "
            + "next handler -- so the remaining {RemainingCount} version(s) are left unfetched for the next "
            + "run to resume.")]
    public static partial void FetchFatal(
        ILogger logger,
        int batchNumber,
        int batchCount,
        string fatalOutcome,
        int remainingCount);

    [LoggerMessage(
        EventId = 5282,
        Level = LogLevel.Warning,
        Message = "Load: a payload EPA answered 200 for could not be read, so version {Ordinal} of batch "
            + "{BatchNumber} was not merged and is recorded as failed. {Problem}")]
    public static partial void PayloadUnreadable(
        ILogger logger,
        int ordinal,
        int batchNumber,
        string problem);

    [LoggerMessage(
        EventId = 5283,
        Level = LogLevel.Information,
        Message = "Load: watermark for {FeedName}/{ActivityLocation} advanced to {WatermarkDate:yyyy-MM-dd} "
            + "by run {LoadRunId}. Every window the run asked for was covered.")]
    public static partial void WatermarkAdvanced(
        ILogger logger,
        string feedName,
        string activityLocation,
        DateOnly watermarkDate,
        int loadRunId);

    // The hold rule has more than one condition -- every window covered, and every version accounted for --
    // so the message takes the reason as a composed string rather than naming one of them. It used to report
    // only the window counts, which made runs 2621 and 2622 say "0 of N window(s) went uncovered" when the
    // cause was code lists that did not refresh: arithmetically true, causally false, and it sends the
    // operator to inspect the walk. A hold is the one event whose whole value to the reader is WHY.
    //
    // [R41] Those two runs were the last that could say it, because the code lists were then removed from the
    // rule altogether -- EPA answers 200-with-empty-array for two state-scoped lists, so that condition could
    // never clear itself and pinned the bookmark permanently. LookupsIncomplete (5277) reports the shortfall
    // now, and it always did; see LoadRun's class remarks.
    [LoggerMessage(
        EventId = 5284,
        Level = LogLevel.Warning,
        Message = "Load: the watermark was NOT advanced, because {HoldReason}. The next run must ask for the "
            + "same range again. Advancing it here would make anything this run missed unreachable by any "
            + "later run, and /hd/sources/summaries returns no envelope -- nothing in a later response would "
            + "ever reveal the gap.")]
    public static partial void WatermarkHeld(ILogger logger, string holdReason);

    [LoggerMessage(
        EventId = 5292,
        Level = LogLevel.Warning,
        Message = "Load: watermark for {FeedName}/{ActivityLocation} advanced only to "
            + "{WatermarkDate:yyyy-MM-dd} by run {LoadRunId}, short of the "
            + "{RequestedToDate:yyyy-MM-dd} it asked for. {UnwalkedCount} of {WindowCount} window(s) went "
            + "uncovered, and the bookmark stops at the last window before the first gap -- so the days from "
            + "there on are asked for again, including the ones this run did cover. Banking the part that "
            + "landed is what lets a catch-up spanning years finish across several runs instead of needing "
            + "one flawless one.")]
    public static partial void WatermarkAdvancedPartially(
        ILogger logger,
        string feedName,
        string activityLocation,
        DateOnly watermarkDate,
        DateOnly requestedToDate,
        int loadRunId,
        int unwalkedCount,
        int windowCount);

    [LoggerMessage(
        EventId = 5285,
        Level = LogLevel.Warning,
        Message = "Load: the watermark move failed for run {LoadRunId}, and the run's own status is "
            + "unaffected. Every record this run fetched is committed; only the bookmark did not move, so "
            + "the next run re-asks for a range it already has and merges it as Unchanged.")]
    public static partial void WatermarkMoveFailed(ILogger logger, int loadRunId, Exception error);

    [LoggerMessage(
        EventId = 5286,
        Level = LogLevel.Information,
        Message = "Load: run {LoadRunId} closed as {Status}. {Summary}")]
    public static partial void RunClosed(ILogger logger, int loadRunId, string status, string summary);

    [LoggerMessage(
        EventId = 5287,
        Level = LogLevel.Error,
        Message = "Load: run {LoadRunId} failed. {FailureMessage}")]
    public static partial void RunFailed(ILogger logger, int loadRunId, string failureMessage);

    [LoggerMessage(
        EventId = 5288,
        Level = LogLevel.Critical,
        Message = "Load: run {LoadRunId} could not be closed, so its row stays at 'Running' until a later "
            + "run sweeps it to 'Abandoned' after {AbandonAfterMinutes} minute(s). The data this run wrote "
            + "is committed and is not affected; the run's own counters are lost.")]
    public static partial void RunCouldNotBeClosed(
        ILogger logger,
        int loadRunId,
        int abandonAfterMinutes,
        Exception error);

    [LoggerMessage(
        EventId = 5289,
        Level = LogLevel.Warning,
        Message = "Load: cancelled. Everything buffered was flushed and run {LoadRunId} was closed, so the "
            + "next run resumes from what this one finished rather than sweeping it as abandoned.")]
    public static partial void RunCancelled(ILogger logger, int loadRunId);

    [LoggerMessage(
        EventId = 5290,
        Level = LogLevel.Warning,
        Message = "Load: the journal reported {RowsOrphaned} orphaned row(s) and {ValuesWithheld} withheld "
            + "value(s) across {Calls} call(s). Both should be zero: an orphan means an attempt was "
            + "recorded for a version this run never enumerated, and a withheld value means a RequestPath "
            + "reached script 524 without a leading slash (AR8).")]
    public static partial void JournalDefects(
        ILogger logger,
        int rowsOrphaned,
        int valuesWithheld,
        int calls);

    [LoggerMessage(
        EventId = 5293,
        Level = LogLevel.Information,
        Message = "Load: starting a Targeted run for one handler in {ActivityLocation}, scope {Scope}. No "
            + "watermark is read and none will be moved -- this run covers no date range, so it can make no "
            + "claim about which days are loaded. The lookups are not refreshed either: nothing has a foreign "
            + "key to a lookup table, so a new code cannot fail the merge. The handler is not named here for "
            + "the reason this class's remarks give; the operator typed it, and run {LoadRunId}'s rows in "
            + "logs.HandlerLoadStatus carry it.")]
    public static partial void TargetedRunPlanned(
        ILogger logger,
        string activityLocation,
        string scope,
        int loadRunId);

    [LoggerMessage(
        EventId = 5294,
        Level = LogLevel.Information,
        Message = "Load: the targeted enumeration selected {VersionCount} of the {SummaryCount} version(s) "
            + "the summaries feed returned, {CurrentRecordCount} of which EPA marks as its current record.")]
    public static partial void TargetedVersionsSelected(
        ILogger logger,
        int versionCount,
        int summaryCount,
        int currentRecordCount);

    [LoggerMessage(
        EventId = 5295,
        Level = LogLevel.Warning,
        Message = "Load: the targeted run has nothing to fetch and is recorded as Failed rather than as a "
            + "quiet success. {Problem}")]
    public static partial void TargetedNothingToFetch(ILogger logger, string problem);

    [LoggerMessage(
        EventId = 5291,
        Level = LogLevel.Error,
        Message = "Load: {FeedName}/{ActivityLocation} has no usable watermark row, so no run was opened. "
            + "{Problem} Script 340 seeds exactly one row, ('HandlerSource', 'MD'); its absence means the "
            + "database was not deployed as documented, and choosing a start date here would be this "
            + "loader inventing one.")]
    public static partial void NotConfigured(
        ILogger logger,
        string feedName,
        string activityLocation,
        string problem);
}
