using System.Globalization;

using RCRAInfo.Data;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// How a run ended, at the granularity an exit code needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three of these seven are not failures, and conflating them is the failure mode this enum exists to
/// prevent.</b> <see cref="FeedDisabled"/> is an operator's own decision, <see cref="AlreadyRunning"/> is
/// the previous run still working, and <see cref="Cancelled"/> is a shutdown. None of them means the load
/// is broken, and all three would look like one if the only distinction were zero versus non-zero.
/// </para>
/// <para>
/// <b>They are also not the same as <c>logs.LoadRun.Status</c>, which has five values and is the record
/// this project keeps.</b> The overlap is deliberate but partial: <see cref="FeedDisabled"/>,
/// <see cref="AlreadyRunning"/> and <see cref="NotConfigured"/> never reach that table at all, because no
/// run was opened — and a <c>Running</c> row for a run that did nothing is exactly the row that blocks the
/// next one.
/// </para>
/// </remarks>
public enum LoadRunOutcome
{
    /// <summary>Every stage did what it was asked. <c>logs.LoadRun.Status</c> is <c>Succeeded</c>.</summary>
    Succeeded,

    /// <summary>
    /// Real work landed and something did not. <c>logs.LoadRun.Status</c> is <c>PartiallySucceeded</c>, and
    /// the watermark has moved only if the walk covered every window.
    /// </summary>
    PartiallySucceeded,

    /// <summary>
    /// The run stopped early. <c>logs.LoadRun.Status</c> is <c>Failed</c>; the watermark has not moved.
    /// </summary>
    Failed,

    /// <summary>
    /// Shutdown or Ctrl-C. Everything buffered was still flushed and the run was still closed — an
    /// unclosed run is indistinguishable from one still going.
    /// </summary>
    Cancelled,

    /// <summary>
    /// <c>config.LoadWatermark.IsEnabled</c> is 0 for this feed. No run was opened and nothing was fetched.
    /// </summary>
    FeedDisabled,

    /// <summary>
    /// Script 510 refused because a run is already live. No second run was opened, which is the point.
    /// </summary>
    AlreadyRunning,

    /// <summary>
    /// The feed has no watermark row, or the row recommends no date range. No run was opened.
    /// </summary>
    NotConfigured,
}

/// <summary>
/// What one orchestrated load did, and enough of why to write an exit code and a log line.
/// </summary>
/// <param name="Outcome">The verdict. See <see cref="LoadRunOutcome"/>.</param>
/// <param name="LoadRunId">
/// The run in <c>logs.LoadRun</c>, or <see langword="null"/> when no run was opened.
/// </param>
/// <param name="RunMode">The mode script 512 recommended, or <see langword="null"/> if it got that far.</param>
/// <param name="Counters">The counters written to <c>logs.LoadRun</c>.</param>
/// <param name="Lookups">The lookup refresh stage's report, or <see langword="null"/> if it did not run.</param>
/// <param name="Walk">The summaries walk's report, or <see langword="null"/> if it did not run.</param>
/// <param name="Reconcile">
/// The <c>CurrentRecord</c> reconciliation's report, or <see langword="null"/> if it did not run — which on a
/// scheduled run means the walk named no version, and on a targeted run means nothing was enumerated.
/// <b><see cref="ReconcileReport.Counts"/>' <c>RowsAffected</c> is the interesting number and zero is the good
/// one</b>: it counts flags this run had to correct, so a non-zero value is EPA's list disagreeing with the
/// mirror, which is the disagreement §D4 exists to settle rather than a defect in the merge.
/// </param>
/// <param name="Journal">Everything the journal wrote across every flush.</param>
/// <param name="WatermarkAdvancedTo">
/// The day the watermark now sits on, or <see langword="null"/> if it did not move. <b>Null is the
/// interesting value</b>: it means at least one window is unaccounted for and a later run will ask again.
/// </param>
/// <param name="FailureMessage">
/// Why, composed by this project and safe for <c>logs.LoadRun.FailureMessage</c> — which scripts 500 and
/// 502 return to a web page. Never an exception's <c>ToString</c>, never a request URI.
/// </param>
/// <remarks>
/// <b>Every report the stages produced is carried rather than summarised into counters.</b> The counters go
/// to <c>logs.LoadRun</c> and answer "how much"; the reports answer "which", and the two questions have
/// different audiences. An operator reading a failed overnight run wants
/// <see cref="SummaryWalkReport.UnwalkedWindows"/>, and it is not derivable from a count.
/// </remarks>
public sealed record LoadRunResult(
    LoadRunOutcome Outcome,
    int? LoadRunId,
    string? RunMode,
    LoadRunCounters Counters,
    LookupStageReport? Lookups,
    SummaryWalkReport? Walk,
    ReconcileReport? Reconcile,
    LoadJournalFlush Journal,
    DateOnly? WatermarkAdvancedTo,
    string? FailureMessage)
{
    /// <summary>A verdict reached before any run was opened.</summary>
    /// <param name="outcome">Which one.</param>
    /// <param name="failureMessage">Why, in a form safe to print.</param>
    /// <returns>The result.</returns>
    public static LoadRunResult NotStarted(LoadRunOutcome outcome, string failureMessage) =>
        new(
            outcome,
            null,
            null,
            new LoadRunCounters(),
            null,
            null,
            null,
            LoadJournalFlush.Empty,
            null,
            failureMessage);

    /// <summary>Whether real data reached the database.</summary>
    /// <remarks>
    /// Soft deletes count. A run whose only effect was to withdraw records EPA no longer serves did change
    /// this mirror, and reporting it as having done nothing would make the one AR7 path that is hardest to
    /// observe also the one that looks idle.
    /// </remarks>
    public bool WroteData =>
        Counters.SourceRecordsInserted > 0
        || Counters.SourceRecordsUpdated > 0
        || Counters.SourceRecordsSoftDeleted > 0;

    /// <summary>The result as one line for a console or a log. Contains no credential and no URI.</summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} run={1} mode={2} enumerated={3} fetched={4} inserted={5} updated={6} unchanged={7} "
            + "deleted={8} skipped={9} failed={10} requests={11} retries={12} watermark={13} "
            + "reconciled={14} flagsFixed={15}",
            Outcome,
            LoadRunId?.ToString(CultureInfo.InvariantCulture) ?? "(none)",
            RunMode ?? "(none)",
            Counters.SourceRecordsEnumerated,
            Counters.SourceRecordsFetched,
            Counters.SourceRecordsInserted,
            Counters.SourceRecordsUpdated,
            Counters.SourceRecordsUnchanged,
            Counters.SourceRecordsSoftDeleted,
            Counters.SourceRecordsSkipped,
            Counters.SourceRecordsFailed,
            Counters.HttpRequestCount,
            Counters.HttpRetryCount,
            WatermarkAdvancedTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(not moved)",

            // Two numbers rather than one, because they answer different questions and only the second is
            // ever surprising: how many lineages were asserted against EPA's own list, and how many
            // CurrentRecord flags that assertion had to correct. Both are counts (AR8) -- the console line is
            // pasted into tickets, so no handler is named here even though naming one is permitted.
            Reconcile is null
                ? "(none)"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/{1}",
                    Reconcile.ReconciledCount,
                    Reconcile.ConsideredCount),
            Reconcile?.Counts.RowsAffected ?? 0);
}
