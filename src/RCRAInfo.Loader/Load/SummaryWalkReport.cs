using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>What happened to one date window of the summaries walk.</summary>
/// <remarks>
/// <para>
/// <b>These are not a projection of <see cref="ApiFetchOutcome"/> and must not become one.</b> The same
/// point <c>LookupRefreshStatus</c> makes: this enum answers "may the watermark move past this window",
/// and the fetch outcome answers "what did the HTTP call do". Those coincide today and would stop
/// coinciding the first time a window is skipped for a reason that has no HTTP status.
/// </para>
/// <para>
/// <b><see cref="Walked"/> covers an empty window, and that is the load-bearing line in this file.</b> A
/// quiet week is a normal answer, and so is EPA's documented <c>404</c> for this endpoint — see
/// <see cref="SummaryWalk"/> on why the second one must not be read as a deletion.
/// </para>
/// </remarks>
public enum SummaryWindowStatus
{
    /// <summary>
    /// The window was asked for and its answer read. Includes an empty answer, and includes a
    /// documented <c>404</c>.
    /// </summary>
    Walked,

    /// <summary>The call did not come back with a body. The window is unknown, not empty.</summary>
    FetchFailed,

    /// <summary>A body arrived and could not be used. See <c>SummaryPayloadRead.Problem</c>.</summary>
    PayloadRejected,

    /// <summary>
    /// The body named a handler outside the activity location this installation loads. Refused whole.
    /// </summary>
    OutOfScope,

    /// <summary>
    /// The walk stopped before reaching this window — a fatal outcome, or cancellation. Reported rather
    /// than omitted, because "we never asked" and "we asked and got nothing" are the two answers that must
    /// never be confused, and an omitted window looks like neither.
    /// </summary>
    NotAttempted,
}

/// <summary>One window of the walk, and what came back.</summary>
/// <param name="Window">The dates asked for.</param>
/// <param name="Status">Whether the window can be counted as covered.</param>
/// <param name="SummaryCount">How many summaries the body carried. Zero for every non-<c>Walked</c> status.</param>
/// <param name="NewVersionCount">
/// How many of them named a version the walk had not already seen in an earlier window. Windows do not
/// overlap, so a duplicate means EPA's date filter is not the field this loader assumes it is — worth
/// counting for that reason alone (G25).
/// </param>
/// <param name="CurrentRecordCount">How many the body marked as EPA's current record.</param>
/// <param name="FetchOutcome">
/// The HTTP-level classification, kept even when the window succeeded. A lookup call keeps its outcome for
/// the same reason: a <c>Walked</c> window whose outcome was <c>NotFound</c> is a fact worth being able to
/// find later.
/// </param>
/// <param name="HttpStatusCode">The status EPA answered with, when there was a response.</param>
/// <param name="DurationMs">How long the call took, from <c>ApiFetchResult</c>.</param>
/// <param name="UnexpectedProperties">Property names EPA sent that this loader cannot bind.</param>
/// <param name="Problem">
/// Why the window is not <c>Walked</c>, or <see langword="null"/>. Safe to log: a JSON path, a value kind,
/// an element index, a length, a status code or a two-letter state — never a handler identifier and never
/// any part of the body.
/// </param>
public sealed record SummaryWindowReport(
    DateWindow Window,
    SummaryWindowStatus Status,
    int SummaryCount,
    int NewVersionCount,
    int CurrentRecordCount,
    ApiFetchOutcome? FetchOutcome,
    int? HttpStatusCode,
    int DurationMs,
    IReadOnlyList<string> UnexpectedProperties,
    string? Problem)
{
    /// <summary>Whether this window counts as covered.</summary>
    public bool IsWalked => Status == SummaryWindowStatus.Walked;

    /// <summary>A window the walk never reached.</summary>
    /// <param name="window">The dates that were not asked for.</param>
    /// <returns>The report.</returns>
    public static SummaryWindowReport NotReached(DateWindow window) =>
        new(window, SummaryWindowStatus.NotAttempted, 0, 0, 0, null, null, 0, [], null);
}

/// <summary>Everything the summaries walk found, and whether the run may act on it.</summary>
/// <param name="Windows">One report per window, in date order. Always covers the whole requested range.</param>
/// <param name="Versions">
/// Every distinct handler version the walk saw, de-duplicated across windows, in first-seen order.
/// </param>
/// <param name="FatalOutcome">
/// The outcome that stopped the walk, when one did — <c>BadRequest</c>, <c>AccessDenied</c> or
/// <c>Unauthorized</c>. Non-null means the remaining windows were not attempted.
/// </param>
/// <param name="WasCancelled">Whether the walk stopped because the run was cancelled.</param>
public sealed record SummaryWalkReport(
    IReadOnlyList<SummaryWindowReport> Windows,
    IReadOnlyList<HandlerSourceSummary> Versions,
    ApiFetchOutcome? FatalOutcome,
    bool WasCancelled)
{
    /// <summary>How many windows were covered.</summary>
    public int WalkedCount => Windows.Count(window => window.IsWalked);

    /// <summary>How many were not.</summary>
    public int UnwalkedCount => Windows.Count - WalkedCount;

    /// <summary>How many summaries were read in total, counting duplicates across windows.</summary>
    public int SummaryCount => Windows.Sum(window => window.SummaryCount);

    /// <summary>Every window this walk did not cover, for the run summary to name.</summary>
    public IReadOnlyList<SummaryWindowReport> UnwalkedWindows =>
        [.. Windows.Where(window => !window.IsWalked)];

    /// <summary>Property names EPA sent that this loader cannot bind, across every window.</summary>
    public IReadOnlyList<string> UnexpectedProperties =>
        [.. Windows.SelectMany(window => window.UnexpectedProperties).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Whether the run may keep going after this stage.</summary>
    /// <remarks>
    /// A failed window does <i>not</i> stop the run. The versions the other windows named are real and
    /// fetching them is real progress; refusing to continue would throw that away to no purpose. What a
    /// failed window stops is <see cref="MayAdvanceWatermark"/>.
    /// </remarks>
    public bool MayContinue => FatalOutcome is null && !WasCancelled;

    /// <summary>
    /// Whether <c>config.uspSetLoadWatermark</c> may be moved to the end of the requested range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every window must be covered, and this is the single most consequential property in the
    /// file.</b> The watermark is what makes the next run skip a date range, and there is no paging and no
    /// envelope to notice a gap with afterwards — so a watermark advanced over a window that failed
    /// produces a <b>permanent hole</b> in the mirror. Not a stale row; an absent one, in a table nothing
    /// will ever ask about again.
    /// </para>
    /// <para>
    /// Note what this deliberately does <i>not</i> require: that the handler versions were successfully
    /// fetched. Those have <c>logs.HandlerLoadStatus</c> rows and a resumed run picks them up from there
    /// (§D2-resume). It is only the <i>enumeration</i> that has nowhere else to be recorded.
    /// </para>
    /// <para>
    /// <b>This is the whole-range answer, and it is not the only safe one</b> — see
    /// <see cref="ContiguouslyWalkedThrough"/>, which is what the orchestrator actually moves the watermark
    /// to. This property remains the definition of a <i>complete</i> walk, which is what
    /// <c>LoadRunOutcome.Succeeded</c> requires.
    /// </para>
    /// </remarks>
    public bool MayAdvanceWatermark => MayContinue && UnwalkedCount == 0;

    /// <summary>
    /// The last day the watermark may safely be moved to: the end of the last window in an unbroken run of
    /// walked windows from the start of the range. <see langword="null"/> when the first window did not land.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An all-or-nothing watermark cannot survive a long catch-up, and a long catch-up is the normal
    /// case rather than the exceptional one.</b> Nothing guarantees the scheduled task ran — a missed week,
    /// a month of a disabled action, or a first load covering the whole notification era all arrive here as
    /// one wide range, and forty-six years at the default width is roughly 2,400 windows. Requiring all
    /// 2,400 to succeed before <i>any</i> progress is durable means one transient <c>500</c> in hour six
    /// costs the whole night, every night, and the load never converges.
    /// </para>
    /// <para>
    /// <b>The prefix is safe because <see cref="Windows"/> is in date order and covers the whole range.</b>
    /// Every day up to this date was asked for and answered, so moving the bookmark there skips nothing —
    /// which is the one property the watermark has to have. The first window that did not land stops the
    /// prefix, and everything from its start date onward is asked for again by the next run, including the
    /// windows <i>after</i> it that this run did walk. Re-walking them costs one request each and their
    /// versions are merged as <c>Unchanged</c>; the alternative is a permanent hole, because
    /// <c>/hd/sources/summaries</c> has no envelope and nothing in any later response would reveal it.
    /// </para>
    /// <para>
    /// <b>A non-walked status is never a quiet window.</b> <c>FetchFailed</c> means the range is unknown,
    /// <c>PayloadRejected</c> means a body arrived that could not be read, <c>OutOfScope</c> means the
    /// filter did not apply, and <c>NotAttempted</c> means nobody asked. An empty answer — and EPA's
    /// documented <c>404</c> — is <c>Walked</c>, so a genuinely quiet fortnight does not stop the prefix.
    /// </para>
    /// <para>
    /// <b>This is a floor on the caller, not permission.</b> The orchestrator additionally requires that the
    /// lookups refreshed and that no version it named was left failed or unfetched, for the reasons
    /// <see cref="MayAdvanceWatermark"/> and <c>LoadRun</c> give.
    /// </para>
    /// </remarks>
    public DateOnly? ContiguouslyWalkedThrough
    {
        get
        {
            DateOnly? through = null;

            foreach (SummaryWindowReport window in Windows)
            {
                if (!window.IsWalked)
                {
                    break;
                }

                through = window.Window.EndDate;
            }

            return through;
        }
    }
}
