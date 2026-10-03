using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>What happened to one handler's <c>CurrentRecord</c> reconciliation.</summary>
/// <remarks>
/// <para>
/// <b>Not a projection of <see cref="ApiFetchOutcome"/>, for <c>SummaryWindowStatus</c>'s reason.</b> This
/// enum answers "was this handler's lineage asserted against EPA's own list", and two of its values have no
/// HTTP status behind them at all.
/// </para>
/// <para>
/// <b><see cref="NoVersions"/> is not <see cref="Reconciled"/>, and the distinction is the sharp edge in this
/// file.</b> Script 521 treats an empty list as a documented no-op: it mentions no
/// <c>(HandlerId, SourceType)</c> pair, so it asserts nothing and changes nothing. Counting that as a
/// reconciled handler would report the one case where the lineage is definitely unexamined as the case where
/// it was examined and agreed.
/// </para>
/// </remarks>
public enum HandlerReconcileStatus
{
    /// <summary>
    /// EPA's complete list for the handler was read and submitted to <c>dbo.uspReconcileCurrentRecord</c>.
    /// Includes the ordinary case where the procedure changed nothing because the mirror already agreed.
    /// </summary>
    Reconciled,

    /// <summary>The call did not come back with a body. The lineage is unknown, not consistent.</summary>
    FetchFailed,

    /// <summary>A body arrived and could not be used. See <c>SummaryPayloadRead.Problem</c>.</summary>
    PayloadRejected,

    /// <summary>
    /// The body named a handler outside the activity location this installation loads. Refused whole, for
    /// <see cref="SummaryWalk.FindOutOfScope"/>'s reason.
    /// </summary>
    OutOfScope,

    /// <summary>
    /// The body named a version of some <i>other</i> handler. Refused whole, and for a reason specific to
    /// script 521 rather than to scope: submitting a foreign handler's versions would assert <i>that</i>
    /// handler's lineage from a list that was never the complete one, and the procedure's answer to an
    /// incomplete list is to demote every version it does not name.
    /// </summary>
    ForeignHandler,

    /// <summary>
    /// EPA named no versions for a handler this run had just merged one for — including its documented
    /// <c>404</c> on the <c>handlerId</c> form. <b>Nothing is soft deleted:</b> AR7's soft delete is a
    /// <c>404</c> for a version this loader NAMED, and this call names a handler rather than a version.
    /// </summary>
    NoVersions,

    /// <summary>
    /// The handler holds more versions than one <c>dbo.uspReconcileCurrentRecord</c> call may carry, so it was
    /// not submitted at all. <b>Refused rather than split</b>, because a partial list is not a smaller
    /// assertion — script 521 sets every unmentioned live version of a mentioned pair to
    /// <c>CurrentRecord = 0</c>, so half a lineage would demote the other half.
    /// </summary>
    TooManyVersions,

    /// <summary>
    /// The stage stopped before reaching this handler — a fatal outcome, or cancellation. Reported rather
    /// than omitted, for <c>SummaryWindowStatus.NotAttempted</c>'s reason: "we never asked" and "we asked and
    /// it agreed" must never be confused.
    /// </summary>
    NotAttempted,
}

/// <summary>One handler's lineage, and what asserting it did.</summary>
/// <param name="HandlerId">
/// EPA's identifier. Carried here and deliberately <b>not</b> in
/// <see cref="CurrentRecordReconcileLog"/> — a handler identifier is not a secret (plan §D3, script 506) and
/// this report is read by an operator chasing one handler, but the application log follows
/// <see cref="SummaryWalkLog"/>'s rule and names none.
/// </param>
/// <param name="Status">Whether the lineage was asserted.</param>
/// <param name="VersionCount">
/// How many versions EPA's list named. Zero for every non-<see cref="HandlerReconcileStatus.Reconciled"/>
/// status except <see cref="HandlerReconcileStatus.TooManyVersions"/>, where it is the reason.
/// </param>
/// <param name="CurrentRecordCount">
/// How many of them EPA flagged current. <b>Zero is a legitimate answer</b> — measured on live Maryland data
/// ([R38]) — so nothing here asserts "exactly one". Above one means EPA's own list is ambiguous, which script
/// 521 records as <c>CurrentRecordAmbiguousInSource</c> and resolves by taking the highest sequence.
/// </param>
/// <param name="FetchOutcome">The HTTP-level classification, kept even when the handler reconciled.</param>
/// <param name="HttpStatusCode">The status EPA answered with, when there was a response.</param>
/// <param name="DurationMs">How long the call took. Zero when no call was made.</param>
/// <param name="Problem">
/// Why the lineage was not asserted, or <see langword="null"/>. Safe to log: a JSON path, a value kind, an
/// element index, a count, a status code or a two-letter state — never any part of the body.
/// </param>
public sealed record HandlerReconcileReport(
    string HandlerId,
    HandlerReconcileStatus Status,
    int VersionCount,
    int CurrentRecordCount,
    ApiFetchOutcome? FetchOutcome,
    int? HttpStatusCode,
    int DurationMs,
    string? Problem)
{
    /// <summary>Whether this handler's lineage was asserted against EPA's list.</summary>
    public bool IsReconciled => Status == HandlerReconcileStatus.Reconciled;

    /// <summary>A handler the stage never reached.</summary>
    /// <param name="handlerId">The handler that was not asked about.</param>
    /// <returns>The report.</returns>
    public static HandlerReconcileReport NotReached(string handlerId) =>
        new(handlerId, HandlerReconcileStatus.NotAttempted, 0, 0, null, null, 0, null);
}

/// <summary>
/// Everything the <c>CurrentRecord</c> reconciliation stage did, and whether the run may call itself complete.
/// </summary>
/// <param name="Handlers">One report per handler considered, in the order they were considered.</param>
/// <param name="Counts">What <c>dbo.uspReconcileCurrentRecord</c> reported, summed across every call.</param>
/// <param name="Calls">
/// HTTP requests this stage spent — one per handler asked about, and zero for a handler whose list the caller
/// already had. Counted so <c>LoadRun.Counters</c> does not read them as retries.
/// </param>
/// <param name="WriteCalls">
/// How many <c>dbo.uspReconcileCurrentRecord</c> calls the batching produced. Interesting only against
/// <see cref="ReconciledCount"/>: it is the number that would rise if the batcher ever started splitting a
/// handler, which it must not.
/// </param>
/// <param name="FatalOutcome">
/// The outcome that stopped the stage, when one did — <c>BadRequest</c>, <c>AccessDenied</c> or
/// <c>Unauthorized</c>. Non-null means the remaining handlers were not asked about.
/// </param>
/// <param name="WasCancelled">Whether the stage stopped because the run was cancelled.</param>
/// <remarks>
/// <para>
/// <b>An incomplete reconcile downgrades the run and deliberately does NOT hold the watermark, which is the
/// opposite of every other stage's rule and the one decision in this file worth arguing with.</b> The
/// watermark is withheld elsewhere because an advanced bookmark makes a gap <i>permanent and
/// undiscoverable</i> — nothing in any later response reveals a date range that was never asked for. A stale
/// <c>CurrentRecord</c> flag is neither. It is discoverable by one query
/// (<c>GROUP BY HandlerId, SourceType HAVING SUM(CASE WHEN CurrentRecord = 1 THEN 1 ELSE 0 END) &gt; 1</c>,
/// which is how <c>MDR000501742</c> was found) and repairable by a targeted run on that handler alone, with
/// no date range involved at all. Holding the bookmark would trade a visible, repairable flag for an
/// unloadable date range, and a handler whose summaries call fails persistently would stall the whole feed
/// indefinitely.
/// </para>
/// <para>
/// So this stage reports <see cref="Complete"/>, the run turns that into <c>PartiallySucceeded</c>, and the
/// bookmark still moves on the walk's and the fetch's conditions.
/// </para>
/// <para>
/// <b><see cref="WasCancelled"/> is the one exception, and it is not an exception to the argument.</b> A
/// cancelled stage means the process is shutting down, which is a fact about the <i>run</i> rather than about
/// any lineage — every stage propagates it and every cancelled run holds the watermark, because a run that
/// stopped early cannot vouch for the range it was given.
/// </para>
/// </remarks>
public sealed record ReconcileReport(
    IReadOnlyList<HandlerReconcileReport> Handlers,
    ReconcileCounts Counts,
    int Calls,
    int WriteCalls,
    ApiFetchOutcome? FatalOutcome,
    bool WasCancelled)
{
    /// <summary>The stage did not run, or had nothing to consider.</summary>
    public static ReconcileReport Nothing { get; } =
        new([], ReconcileCounts.Empty, 0, 0, null, false);

    /// <summary>How many handlers were considered.</summary>
    public int ConsideredCount => Handlers.Count;

    /// <summary>How many lineages were asserted against EPA's list.</summary>
    public int ReconciledCount => Handlers.Count(handler => handler.IsReconciled);

    /// <summary>How many were not.</summary>
    public int UnreconciledCount => Handlers.Count - ReconciledCount;

    /// <summary>The handlers whose lineage this run did not assert, for the run summary to name.</summary>
    public IReadOnlyList<HandlerReconcileReport> UnreconciledHandlers =>
        [.. Handlers.Where(handler => !handler.IsReconciled)];

    /// <summary>
    /// Whether every handler this run touched had its lineage asserted. Required for
    /// <c>LoadRunOutcome.Succeeded</c>; see the class remarks for why it is <i>not</i> required for the
    /// watermark.
    /// </summary>
    public bool Complete => FatalOutcome is null && !WasCancelled && UnreconciledCount == 0;

    /// <summary>
    /// Why the run is not complete, in a form safe for <c>logs.LoadRun.FailureMessage</c> — which scripts 500
    /// and 502 return to a web page. <see langword="null"/> when <see cref="Complete"/>.
    /// </summary>
    /// <remarks>
    /// Names counts and the first unreconciled handler's status, never a URI and never an exception's
    /// <c>ToString</c>. The handler identifier is omitted even though it is permitted, because this string
    /// lands in a column an unauthenticated-in-Phase-1 monitoring page renders and the count is what makes it
    /// actionable; <c>logs.HandlerLoadStatus</c> and this run's log already name the handlers.
    /// </remarks>
    public string? FailureMessage
    {
        get
        {
            if (Complete)
            {
                return null;
            }

            if (WasCancelled)
            {
                return $"The CurrentRecord reconciliation was cancelled after asserting {ReconciledCount} of "
                    + $"{ConsideredCount} handler lineage(s). Every version this run fetched is merged; what "
                    + "is unasserted is a derived flag, and the watermark was NOT held for it.";
            }

            string stopped = FatalOutcome is { } fatal
                ? $"The CurrentRecord reconciliation stopped on {fatal}. "
                : string.Empty;

            return stopped
                + $"{UnreconciledCount} of {ConsideredCount} handler lineage(s) were not asserted against "
                + "EPA's own version list, so dbo.HandlerSource.CurrentRecord may still name more than one "
                + "current version for them. Every version this run fetched is merged and the watermark was "
                + "NOT held: the flag is discoverable by query and repairable by a targeted run on the "
                + "handler alone (plan §D4).";
        }
    }
}
