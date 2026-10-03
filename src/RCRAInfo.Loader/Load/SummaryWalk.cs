using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Asks <c>/hd/sources/summaries</c> for one date window at a time and collects the handler versions it
/// names.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole class is organised around one asymmetry, and it is the mirror image of the lookup
/// refresh's.</b> There, a failed call changed nothing and a successful one could retire reference data. Here
/// a failed <i>window</i> is the dangerous case: the versions it would have named are simply absent from the
/// run, no error attaches to any handler, and if the watermark advances anyway then <b>no later run ever
/// asks for that range again</b>. There is no paging and no envelope to notice the gap with afterwards.
/// So the three behaviours below all exist to keep an unwalked window visible.
/// </para>
/// <list type="number">
/// <item>
/// <b>A failed window does not stop the walk.</b> The other windows' versions are real progress, and one
/// transient failure should not cost the other fifty. What it stops is
/// <see cref="SummaryWalkReport.MayAdvanceWatermark"/>, which is checked by the caller and is the only
/// thing that makes a gap permanent.
/// </item>
/// <item>
/// <b>A window the walk never reached is reported, not omitted.</b> <c>NotAttempted</c> rows are emitted
/// for every remaining window when a fatal outcome or a cancellation ends the walk — because "we did not
/// ask" and "we asked and it was quiet" are the two answers that must never be confused, and an omitted
/// window looks like neither.
/// </item>
/// <item>
/// <b>A <c>404</c> is an empty window and never a deletion.</b> See <see cref="ReadWindowAsync"/>. This is
/// the sharpest edge in the file.
/// </item>
/// </list>
/// <para>
/// <b>The answer's <c>activityLocation</c> is verified and not just the request's.</b> Plan §D2 puts it
/// plainly: a summaries call with no <c>activityLocation</c> asks EPA for every handler in the country and
/// the failure mode is a successful, larger response. <c>RcraInfoDataRequest</c> refuses to build such a
/// request, which closes the case where the filter is <i>absent</i>. It cannot close the case where the
/// filter is sent and does not apply — a service-side change, a gateway rewriting a query string, a
/// parameter renamed in a new API version. That case looks like a completely normal run and quietly mirrors
/// other states' regulated entities, so the window is refused as <c>OutOfScope</c> rather than read.
/// </para>
/// <para>
/// <b>There is no retry here, and unlike the lookup refresh that is now a decision rather than a gap.</b>
/// Transient retries belong to the resilience pipeline on the <c>rcrainfo-data</c> client, which is
/// configured from <see cref="RcraInfoThrottleOptions"/> — <c>429</c>, <c>Retry-After</c> and exponential
/// backoff with jitter (§D2-throttle). By the time a result reaches this class, retrying has already been
/// tried and given up on, and a second retry loop here would multiply the attempt count by EPA's own
/// tolerance without anything in the configuration saying so.
/// </para>
/// </remarks>
/// <param name="client">The data client. Classifies; does not throw for a failed call.</param>
/// <param name="options">The run's scope — the activity location and the window width.</param>
/// <param name="logger">Counts, dates and statuses only. No handler identifier and no URI (AR8).</param>
public sealed class SummaryWalk(
    IRcraInfoDataClient client,
    IOptions<LoadRunOptions> options,
    ILogger<SummaryWalk> logger) : ISummaryWalk
{
    private readonly LoadRunOptions runOptions = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<SummaryWalkReport> WalkAsync(
        int loadRunId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> problems = runOptions.Validate();

        if (problems.Count > 0)
        {
            // Thrown rather than reported, for LookupRefresh's reason: unusable configuration is not an
            // answer from EPA, and a walk that returned an empty report here would look exactly like a
            // range in which nothing changed -- which is the report that advances a watermark.
            throw new InvalidOperationException(
                "The RCRAInfoLoad configuration is not usable, so no summaries window can be asked for: "
                + string.Join(" ", problems));
        }

        if (toDate < fromDate)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The summaries walk was asked for {0:yyyy-MM-dd} to {1:yyyy-MM-dd}, which ends before "
                    + "it starts. EPA answers an inverted range with 200 and an empty array, so a walk that "
                    + "accepted this would report every window as quiet and let the watermark advance over "
                    + "the whole range.",
                    fromDate,
                    toDate));
        }

        string activityLocation = runOptions.NormalizedActivityLocation();
        IReadOnlyList<DateWindow> windows = DateWindow.Split(fromDate, toDate, runOptions.WindowDays);

        SummaryWalkLog.WalkStarting(
            logger, loadRunId, activityLocation, fromDate, toDate, windows.Count, runOptions.WindowDays);

        List<SummaryWindowReport> reports = new(windows.Count);

        // Insertion-ordered de-duplication. Windows do not overlap, so a repeat should be impossible -- but
        // which date field EPA filters on is not stated in the spec (G25), and if it is not the one this
        // loader assumes then repeats are exactly the symptom. Counting them makes that visible; a HashSet
        // alone would silently absorb the evidence.
        Dictionary<HandlerVersion, HandlerSourceSummary> seen = [];
        List<HandlerSourceSummary> versions = [];
        int duplicates = 0;

        ApiFetchOutcome? fatal = null;
        bool cancelled = false;

        for (int index = 0; index < windows.Count; index++)
        {
            DateWindow window = windows[index];

            if (fatal is not null || cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = cancelled || (fatal is null && cancellationToken.IsCancellationRequested);
                reports.Add(SummaryWindowReport.NotReached(window));

                continue;
            }

            SummaryWindowReport report = await ReadWindowAsync(
                    loadRunId, activityLocation, window, seen, versions, cancellationToken)
                .ConfigureAwait(false);

            duplicates += report.SummaryCount - report.NewVersionCount;
            reports.Add(report);

            if (report.FetchOutcome is ApiFetchOutcome outcome)
            {
                if (outcome == ApiFetchOutcome.Cancelled)
                {
                    cancelled = true;
                }
                else if (outcome.IsFatalToTheRun())
                {
                    fatal = outcome;

                    SummaryWalkLog.WalkStoppedFatally(
                        logger,
                        loadRunId,
                        window.ToString(),
                        outcome,
                        reports.Count(r => r.IsWalked),
                        windows.Count - reports.Count);
                }
            }
        }

        SummaryWalkReport walk = new(reports, versions, fatal, cancelled);

        SummaryWalkLog.WalkFinished(
            logger,
            loadRunId,
            walk.WalkedCount,
            walk.Windows.Count,
            versions.Count,
            duplicates,
            walk.MayAdvanceWatermark);

        return walk;
    }

    /// <summary>Asks for one window and classifies the answer.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>NotFound</c> is the line to read twice.</b> <c>ApiFetchOutcome.NotFound</c> is the AR7
    /// soft-delete signal: on <c>/hd/sources/{handlerId}/{sourceType}/{sequence}</c> it feeds
    /// <c>dbo.uspSoftDeleteHandlerSourceSet</c>, because a version EPA no longer serves is a version EPA
    /// has withdrawn. On <b>this</b> endpoint the same outcome means something completely different. The
    /// request names a date range rather than a record, <c>404</c> is in the endpoint's documented status
    /// set, and the only honest reading of "no handler sources found for these dates" is an empty window.
    /// Passing it through as a deletion signal would soft-delete on the strength of a quiet week.
    /// </para>
    /// <para>
    /// This is why the classification lives in <c>RcraInfoDataClient</c> per <i>endpoint</i> and why the
    /// consumer still has to interpret it: the outcome faithfully reports what EPA said, and what it means
    /// depends on what was asked.
    /// </para>
    /// </remarks>
    private async Task<SummaryWindowReport> ReadWindowAsync(
        int loadRunId,
        string activityLocation,
        DateWindow window,
        Dictionary<HandlerVersion, HandlerSourceSummary> seen,
        List<HandlerSourceSummary> versions,
        CancellationToken cancellationToken)
    {
        RcraInfoDataRequest request = RcraInfoDataRequest.Summaries(
            activityLocation, window.StartDate, window.EndDate);

        // Formatted once, outside the logging calls, because CA1873 is an error here and it is right to be:
        // a method call inside a Debug-level log runs whether or not anyone is listening. One string per
        // WINDOW rather than per row, so doing it unconditionally costs nothing worth measuring.
        string label = window.ToString();

        ApiFetchResult result = await client.FetchAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.Outcome == ApiFetchOutcome.NotFound)
        {
            SummaryWalkLog.WindowEmptyByNotFound(logger, loadRunId, label, result.HttpStatusCode);

            return Walked(window, result, 0, 0, 0, []);
        }

        if (result.Outcome != ApiFetchOutcome.Succeeded)
        {
            return Failed(
                loadRunId,
                window,
                SummaryWindowStatus.FetchFailed,
                result,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the call came back {0} with no usable body (HTTP {1}).",
                    result.Outcome,
                    result.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none"));
        }

        SummaryPayloadRead read = SummaryPayload.Read(result.Payload);

        if (read.UnexpectedProperties.Count > 0)
        {
            SummaryWalkLog.UnexpectedProperties(
                logger,
                loadRunId,
                label,
                read.UnexpectedProperties.Count,
                string.Join(", ", read.UnexpectedProperties));
        }

        if (!read.IsReadable)
        {
            return Failed(
                loadRunId, window, SummaryWindowStatus.PayloadRejected, result, read.Problem!,
                read.UnexpectedProperties);
        }

        if (FindOutOfScope(read.Summaries, activityLocation) is string outOfScope)
        {
            return Failed(
                loadRunId, window, SummaryWindowStatus.OutOfScope, result, outOfScope,
                read.UnexpectedProperties);
        }

        int newVersions = 0;

        foreach (HandlerSourceSummary summary in read.Summaries)
        {
            if (seen.TryAdd(summary.ToVersion(), summary))
            {
                versions.Add(summary);
                newVersions++;
            }
        }

        SummaryWalkLog.WindowWalked(
            logger,
            loadRunId,
            label,
            read.Summaries.Count,
            newVersions,
            read.CurrentRecordCount,
            result.DurationMs);

        return Walked(
            window,
            result,
            read.Summaries.Count,
            newVersions,
            read.CurrentRecordCount,
            read.UnexpectedProperties);
    }

    /// <summary>
    /// The scope check on the <i>answer</i>: every summary must name the activity location this
    /// installation loads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refuses the window whole rather than filtering the offending rows out, and the reason is what the
    /// finding would mean. One out-of-state handler in a response to a request that named
    /// <c>activityLocation=MD</c> does not indicate one stray row; it indicates the filter did not apply,
    /// and the rows that <i>look</i> like Maryland's in the same body are then no more trustworthy than the
    /// one that does not. Filtering would turn a "the request I sent is not the request that was answered"
    /// finding into a slightly shorter, entirely plausible window.
    /// </para>
    /// <para>
    /// A comparison and not a <c>CHECK</c> constraint: nothing in this database constrains a
    /// <i>contact's</i> state to <c>MD</c> and nothing here should either. This is
    /// <c>HandlerSource.activityLocation</c>, which G2 settles as <c>MD</c> only, and it is the one column
    /// where the restriction is correct.
    /// </para>
    /// <para>
    /// <b><see langword="internal"/> rather than private so the targeted single-handler run uses this exact
    /// method</b> — see <c>LoadRun.RunTargetedAsync</c>. It asks the same endpoint by <c>handlerId</c> instead
    /// of by date, which is a request this loader cannot scope to a state at all, so the answer is the only
    /// place the check can happen. A second copy of a security-relevant comparison is a second copy that can
    /// be amended on one side.
    /// </para>
    /// </remarks>
    internal static string? FindOutOfScope(
        IReadOnlyList<HandlerSourceSummary> summaries,
        string activityLocation)
    {
        for (int index = 0; index < summaries.Count; index++)
        {
            string? location = summaries[index].ActivityLocation?.Trim();

            if (string.IsNullOrEmpty(location))
            {
                continue;
            }

            if (!string.Equals(location, activityLocation, StringComparison.OrdinalIgnoreCase))
            {
                // The two-letter code is reportable -- it is a jurisdiction, not a regulated entity -- and
                // it is the one value that makes this actionable. The handler it belongs to is not named.
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "element {0} of {1} names activityLocation '{2}' where this installation loads '{3}'. "
                    + "The request carried the filter, so the window is refused whole rather than filtered: "
                    + "a response that ignored one parameter cannot be trusted to have applied the rest, "
                    + "and mirroring another state's regulated entities is the failure mode plan §D2 names.",
                    index,
                    summaries.Count,
                    location,
                    activityLocation);
            }
        }

        return null;
    }

    private static SummaryWindowReport Walked(
        DateWindow window,
        ApiFetchResult result,
        int summaryCount,
        int newVersionCount,
        int currentRecordCount,
        IReadOnlyList<string> unexpectedProperties) =>
        new(
            window,
            SummaryWindowStatus.Walked,
            summaryCount,
            newVersionCount,
            currentRecordCount,
            result.Outcome,
            result.HttpStatusCode,
            result.DurationMs,
            unexpectedProperties,
            null);

    private SummaryWindowReport Failed(
        int loadRunId,
        DateWindow window,
        SummaryWindowStatus status,
        ApiFetchResult result,
        string problem,
        IReadOnlyList<string>? unexpectedProperties = null)
    {
        SummaryWalkLog.WindowNotWalked(logger, loadRunId, window.ToString(), status, problem);

        return new SummaryWindowReport(
            window,
            status,
            0,
            0,
            0,
            result.Outcome,
            result.HttpStatusCode,
            result.DurationMs,
            unexpectedProperties ?? [],
            problem);
    }
}
