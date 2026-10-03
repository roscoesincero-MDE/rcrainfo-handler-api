using System.Globalization;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// How one of the 23 code lists ended up. One value per <b>caller branch</b>, the rule
/// <see cref="ApiFetchOutcome"/> follows.
/// </summary>
/// <remarks>
/// <b>These are not a projection of <see cref="ApiFetchOutcome"/> and must not become one.</b> A lookup has
/// answers a data fetch does not: a body that will not read as a code list, and a successful body that is
/// empty. Both arrive as <c>200</c>, so an outcome-shaped report would call them <c>Succeeded</c> — and the
/// second one is the dangerous answer in this whole stage, because in <c>Full</c> mode an empty payload
/// means "retire every code in this list".
/// </remarks>
public enum LookupRefreshStatus
{
    /// <summary>EPA answered, the payload read, and script 523 wrote it.</summary>
    Refreshed,

    /// <summary>
    /// The call did not come back with a payload. The mirror is untouched — 523 was never reached.
    /// </summary>
    /// <remarks>
    /// <b>The safe failure, and the reason no retry policy is invented here.</b> A lookup that fails
    /// changes nothing: the previous mirror stands, complete and one run stale. Only a <i>successful</i>
    /// payload can retire a code.
    /// </remarks>
    FetchFailed,

    /// <summary>
    /// EPA answered <c>200</c> with a body this loader could not read as a code list. 523 was never reached.
    /// </summary>
    /// <remarks>
    /// The pinned spec and the live service have diverged — the lookup equivalent of
    /// <see cref="ApiFetchOutcome.Unexpected"/>, and the finding <c>build/check_lookup_catalog.py</c> exists
    /// to make rare.
    /// </remarks>
    PayloadRejected,

    /// <summary>
    /// EPA answered <c>200</c> with an empty array. <b>Refused rather than sent.</b>
    /// </summary>
    /// <remarks>
    /// Script 523 refuses this too, and says so: <c>Full</c> mode with an empty payload would retire the
    /// whole list. It is refused here as well because the refusal that names the endpoint is worth more than
    /// the one that names the parameter — 523 cannot know which call returned nothing, and a database error
    /// row reads like a defect in the loader rather than a change at EPA.
    /// </remarks>
    EmptyPayload,

    /// <summary>The payload was more than one call may carry, and this list cannot be paged. Refused.</summary>
    /// <remarks>
    /// See <see cref="ILookupRefreshWriter.MaxElementsPerCall"/>: splitting a <c>Full</c>-mode refresh into
    /// two calls would have the second retire everything the first wrote.
    /// </remarks>
    TooLarge,

    /// <summary>523 was called and threw. Whether anything was written is the procedure's transaction to say.</summary>
    WriteFailed,

    /// <summary>The stage stopped before reaching this list.</summary>
    /// <remarks>
    /// Either the run was cancelled or an earlier list failed fatally. Reported rather than omitted, so the
    /// count of lists in the report is always 23 and "not attempted" is distinguishable from "forgotten".
    /// </remarks>
    NotAttempted,
}

/// <summary>What happened to one mirrored code list.</summary>
/// <param name="Lookup">Which list.</param>
/// <param name="Status">How it ended.</param>
/// <param name="ElementCount">Codes in the payload, zero when there was none.</param>
/// <param name="Counts">What script 523 reported, zeroes when it was not reached.</param>
/// <param name="FetchOutcome">
/// How the HTTP call was classified, or <see langword="null"/> when no call was made. Kept even on success,
/// because <see cref="ApiFetchOutcome.Succeeded"/> after a <see cref="ApiFetchOutcome.Throttled"/> is a
/// different fact about EPA than a clean first call — and unlike the handler fetch, a lookup has no
/// <c>logs.HandlerLoadAttempt</c> row to say so.
/// </param>
/// <param name="UnexpectedProperties">
/// Property names EPA sent that <c>LookupElement</c> could not bind. Empty in a correct run; non-empty means
/// the codes were stored and something about them was discarded.
/// </param>
/// <param name="Problem">
/// Why it did not refresh, or <see langword="null"/>. Safe to log — composed, never an exception's own
/// message, and never any part of a payload or a URI.
/// </param>
public sealed record LookupRefreshReport(
    RcraInfoLookup Lookup,
    LookupRefreshStatus Status,
    int ElementCount,
    LookupWriteCounts Counts,
    ApiFetchOutcome? FetchOutcome,
    IReadOnlyList<string> UnexpectedProperties,
    string? Problem)
{
    /// <summary>A list the stage never got to.</summary>
    /// <param name="lookup">The list.</param>
    /// <param name="reason">Why, in a form safe to log.</param>
    /// <returns>The report.</returns>
    public static LookupRefreshReport NotReached(RcraInfoLookup lookup, string reason) =>
        new(lookup, LookupRefreshStatus.NotAttempted, 0, default, null, [], reason);

    /// <summary>The list, its status and its counts. Never a payload and never a URI.</summary>
    /// <returns>One line for a log or a run summary.</returns>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} ({2} element(s), {3} written, {4} retired, {5} child)",
            Lookup.Name,
            Status,
            ElementCount,
            Counts.RowsAffected,
            Counts.RetiredRows,
            Counts.ChildRows);
}

/// <summary>What the whole lookup stage did, one report per list, always 23 of them.</summary>
/// <param name="Reports">One per list, in fetch order.</param>
/// <param name="FatalOutcome">
/// The outcome that ended the stage early, or <see langword="null"/>. Set only for an
/// <c>ApiFetchOutcomeExtensions.IsFatalToTheRun</c> outcome — a credential or scope problem that will fail
/// identically on the next list and on every handler after it.
/// </param>
/// <param name="WasCancelled">
/// Whether the stage stopped because the run was cancelled.
/// </param>
/// <remarks>
/// <b><paramref name="WasCancelled"/> is separate from <paramref name="FatalOutcome"/> because they are
/// different answers to different questions</b> — one is EPA refusing us, the other is this process being
/// asked to stop — and folding them together would put an orderly shutdown in front of an operator as a
/// credential problem. Both stop the stage, so <see cref="MayContinue"/> reads both; keeping them apart is
/// what lets the run summary say which happened.
/// </remarks>
public sealed record LookupStageReport(
    IReadOnlyList<LookupRefreshReport> Reports,
    ApiFetchOutcome? FatalOutcome,
    bool WasCancelled)
{
    /// <summary>Lists that refreshed.</summary>
    public int RefreshedCount =>
        Reports.Count(report => report.Status == LookupRefreshStatus.Refreshed);

    /// <summary>Lists that did not.</summary>
    public int UnrefreshedCount => Reports.Count - RefreshedCount;

    /// <summary>Codes soft-deleted across every list this stage refreshed.</summary>
    /// <remarks>
    /// <b>The number to read first.</b> Retirement is correct behaviour and is also what a wrongly-scoped
    /// payload produces, and neither EPA nor script 523 can tell those apart — the second one is a
    /// successful run that quietly soft-deletes reference data. Analysis §9 step 9 turns this into a
    /// behavioural check: count the 24 <c>dbo.Lookup*</c> tables before and after two consecutive runs and
    /// expect the second pair to be identical.
    /// </remarks>
    public int TotalRetired => Reports.Sum(report => report.Counts.RetiredRows);

    /// <summary>Whether the run may continue to the handler data.</summary>
    /// <remarks>
    /// <b>True even when lists failed, and that is G15's reasoning rather than a concession.</b> Refreshing
    /// the lookups first does not make an unknown code impossible — it makes one rare, and
    /// <c>logs.DataQualityObservation</c> is what handles the rest. Stopping a whole load because one code
    /// list was unreachable would trade a handful of observations for a night's worth of handler data.
    /// </remarks>
    public bool MayContinue => FatalOutcome is null && !WasCancelled;

    /// <summary>Lists whose payload carried a property this loader has no home for.</summary>
    public IReadOnlyList<LookupRefreshReport> WithUnexpectedProperties =>
        [.. Reports.Where(report => report.UnexpectedProperties.Count > 0)];

    /// <summary>The stage in one line. Counts only.</summary>
    /// <returns>A summary safe to log.</returns>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} of {1} code list(s) refreshed, {2} code(s) retired{3}",
            RefreshedCount,
            Reports.Count,
            TotalRetired,
            FatalOutcome is not null
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "; stopped on {0}, which is fatal to the run",
                    FatalOutcome)
                : WasCancelled
                    ? "; stopped because the run was cancelled"
                    : string.Empty);
}
