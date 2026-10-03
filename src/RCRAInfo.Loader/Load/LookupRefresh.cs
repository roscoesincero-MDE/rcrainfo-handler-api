using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Refreshes the 23 mirrored EPA code lists, one call each, in <see cref="RcraInfoLookups.All"/> order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequential, and there is no retry.</b> Both are decisions rather than omissions.
/// </para>
/// <para>
/// Sequential because 23 calls are nothing beside the several hundred thousand the handler stage will make,
/// and this stage runs immediately before it — spending a concurrency budget nobody has measured (G21 is
/// still open, so EPA's rate limit is unknown) on the cheap part of the run would buy seconds and risk
/// arriving at the expensive part already throttled. A stable, boring order also makes two runs comparable,
/// which is what F2 measures against.
/// </para>
/// <para>
/// No retry because there is no retry policy in this solution yet and inventing one here would be the
/// second copy of a decision that belongs to the orchestrator and needs G21 to make well. The cost of not
/// retrying is bounded and known: a transient blip leaves one code list one run stale, because a failed
/// lookup never reaches script 523 and so retires nothing. When the shared policy exists, wrapping
/// <see cref="IRcraInfoDataClient.FetchAsync"/> is the whole change.
/// </para>
/// <para>
/// <b><c>Full</c> is the only mode this stage sends.</b> Script 523's <c>Upsert</c> mode exists for a list
/// fetched in pages, and no <c>/lookup/hd</c> endpoint pages — none takes a <c>skip</c> or a <c>take</c>, and
/// none returns a page envelope. Sending <c>Upsert</c> would refresh every list and retire nothing, so a code
/// EPA withdrew would stay live and current forever with no error anywhere. That is why
/// <see cref="ILookupRefreshWriter.MaxElementsPerCall"/> being exceeded is a <i>refusal</i> here rather than
/// a split into two calls: the second <c>Full</c> call would retire everything the first one wrote.
/// </para>
/// </remarks>
/// <param name="client">The data client. Never throws for a failed call; it classifies.</param>
/// <param name="writer">The script 523 seam.</param>
/// <param name="options">The run's scope, for the <c>stateCode</c> the seven scoped endpoints take.</param>
/// <param name="logger">The log.</param>
public sealed class LookupRefresh(
    IRcraInfoDataClient client,
    ILookupRefreshWriter writer,
    IOptions<LoadRunOptions> options,
    ILogger<LookupRefresh> logger) : ILookupRefresh
{
    /// <summary>The only mode this stage sends. See the class remarks.</summary>
    private const string FullMode = "Full";

    /// <inheritdoc />
    public async Task<LookupStageReport> RefreshAllAsync(
        int loadRunId,
        CancellationToken cancellationToken = default)
    {
        LoadRunOptions runOptions = options.Value;

        IReadOnlyList<string> problems = runOptions.Validate();
        if (problems.Count > 0)
        {
            // Not reported as a per-list failure: without an activity location the five lists that require
            // a stateCode cannot be requested at all, and the two that accept one would be fetched
            // nationally -- which is a successful call whose payload retires nothing in Maryland and
            // acquires fifty states of codes.
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        string activityLocation = runOptions.NormalizedActivityLocation();

        LookupRefreshLog.StageStarting(
            logger, loadRunId, RcraInfoLookups.All.Count, activityLocation);

        List<LookupRefreshReport> reports = new(RcraInfoLookups.All.Count);
        ApiFetchOutcome? fatalOutcome = null;
        bool cancelled = false;

        foreach (RcraInfoLookup lookup in RcraInfoLookups.All)
        {
            if (fatalOutcome is not null)
            {
                // Every remaining list still gets a report, so the count is always 23 and "not attempted" is
                // distinguishable from "forgotten". A stage that returned a short list would read as though
                // the catalog itself had shrunk.
                reports.Add(LookupRefreshReport.NotReached(
                    lookup,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "an earlier list ended in {0}, which is fatal to the run.",
                        fatalOutcome)));

                continue;
            }

            if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;

                reports.Add(LookupRefreshReport.NotReached(
                    lookup, "the run was cancelled before this list was reached."));

                continue;
            }

            LookupRefreshReport report = await RefreshOneAsync(
                loadRunId, lookup, activityLocation, cancellationToken).ConfigureAwait(false);

            reports.Add(report);
            Report(loadRunId, report);

            if (report.FetchOutcome is ApiFetchOutcome outcome && outcome.IsFatalToTheRun())
            {
                fatalOutcome = outcome;

                LookupRefreshLog.StageStoppedFatally(
                    logger,
                    loadRunId,
                    lookup.Name,
                    outcome,
                    reports.Count(r => r.Status == LookupRefreshStatus.Refreshed),
                    RcraInfoLookups.All.Count - reports.Count);
            }
            else if (report.FetchOutcome == ApiFetchOutcome.Cancelled)
            {
                cancelled = true;
            }
        }

        LookupStageReport stage = new(reports, fatalOutcome, cancelled);

        LookupRefreshLog.StageFinished(
            logger, loadRunId, stage.RefreshedCount, stage.Reports.Count, stage.TotalRetired);

        return stage;
    }

    /// <summary>Fetches one list, reads it, and refreshes it.</summary>
    private async Task<LookupRefreshReport> RefreshOneAsync(
        int loadRunId,
        RcraInfoLookup lookup,
        string activityLocation,
        CancellationToken cancellationToken)
    {
        // The endpoint decides the scope, not configuration: the stateCode goes only where the spec says the
        // parameter exists, and RcraInfoDataRequest.Lookup refuses it anywhere else rather than dropping it.
        RcraInfoDataRequest request = RcraInfoDataRequest.Lookup(
            lookup,
            lookup.TakesStateCode ? activityLocation : null);

        ApiFetchResult result = await client
            .FetchAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!result.HasPayload)
        {
            return new LookupRefreshReport(
                lookup,
                result.Outcome == ApiFetchOutcome.Cancelled
                    ? LookupRefreshStatus.NotAttempted
                    : LookupRefreshStatus.FetchFailed,
                0,
                default,
                result.Outcome,
                [],
                Describe(result));
        }

        LookupPayloadRead read = LookupPayload.Read(result.Payload);

        if (!read.IsReadable)
        {
            return new LookupRefreshReport(
                lookup, LookupRefreshStatus.PayloadRejected, 0, default, result.Outcome, [], read.Problem);
        }

        if (read.Elements.Count == 0)
        {
            // EPA answered 200 with []. Refused rather than sent: in Full mode this would retire the whole
            // list, and a code list that has ever had a code in it does not legitimately become empty.
            return new LookupRefreshReport(
                lookup,
                LookupRefreshStatus.EmptyPayload,
                0,
                default,
                result.Outcome,
                read.UnexpectedProperties,
                "EPA answered 200 with an empty array. Sending that in Full mode would retire every code "
                + "in the list, so it was refused; script 523 refuses it as well.");
        }

        if (read.Elements.Count > writer.MaxElementsPerCall)
        {
            return new LookupRefreshReport(
                lookup,
                LookupRefreshStatus.TooLarge,
                read.Elements.Count,
                default,
                result.Outcome,
                read.UnexpectedProperties,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the list has {0} code(s) and one call may carry {1}. It cannot be split: no "
                    + "/lookup/hd endpoint pages, and two Full-mode calls would have the second retire "
                    + "everything the first wrote. Raise RCRAInfoData:MaxPayloadElements instead.",
                    read.Elements.Count,
                    writer.MaxElementsPerCall));
        }

        try
        {
            LookupWriteCounts counts = await writer
                .RefreshAsync(loadRunId, lookup.Name, FullMode, read.Elements, cancellationToken)
                .ConfigureAwait(false);

            return new LookupRefreshReport(
                lookup,
                LookupRefreshStatus.Refreshed,
                read.Elements.Count,
                counts,
                result.Outcome,
                read.UnexpectedProperties,
                null);
        }
        catch (OperationCanceledException)
        {
            return new LookupRefreshReport(
                lookup,
                LookupRefreshStatus.NotAttempted,
                read.Elements.Count,
                default,
                ApiFetchOutcome.Cancelled,
                read.UnexpectedProperties,
                "the run was cancelled while script 523 was running. Whether the refresh committed is "
                + "the procedure's transaction to say; nothing is half-applied either way.");
        }
#pragma warning disable CA1031 // Deliberate: one list's write must not lose the other 22. See below.
        catch (Exception error)
        {
            // Broad on purpose, and narrower than it looks in effect: everything reachable here is a
            // database-side refusal or fault, and all of them mean the same thing to this stage -- this one
            // list did not refresh. Letting it out would abandon the remaining lists, and the whole reason
            // the lookups are refreshed first is to have as many of them current as possible before the
            // handler data is merged against them.
            LookupRefreshLog.ListWriteFailed(logger, error, loadRunId, lookup.Name, error.GetType().Name);

            return new LookupRefreshReport(
                lookup,
                LookupRefreshStatus.WriteFailed,
                read.Elements.Count,
                default,
                result.Outcome,
                read.UnexpectedProperties,

                // The exception TYPE and not its message. A SqlException's message carries the text script
                // 523 raised, which is safe, but the same catch also sees connection failures whose messages
                // name the server and the login -- and this string reaches logs.LoadRun.FailureMessage,
                // which the monitoring web application reads. The exception itself is logged above, where
                // the log's own destination is the operator's to control.
                string.Format(
                    CultureInfo.InvariantCulture,
                    "script 523 refused or could not complete the refresh ({0}). See logs.ExecutionLog "
                    + "for the procedure's own account of it.",
                    error.GetType().Name));
        }
#pragma warning restore CA1031
    }

    /// <summary>Logs one list's report at the level its status deserves.</summary>
    private void Report(int loadRunId, LookupRefreshReport report)
    {
        if (report.UnexpectedProperties.Count > 0)
        {
            LookupRefreshLog.UnexpectedProperties(
                logger,
                loadRunId,
                report.Lookup.Name,
                report.UnexpectedProperties.Count,
                string.Join(", ", report.UnexpectedProperties));
        }

        switch (report.Status)
        {
            case LookupRefreshStatus.Refreshed:
                LookupRefreshLog.ListRefreshed(
                    logger,
                    loadRunId,
                    report.Lookup.Name,
                    report.ElementCount,
                    report.Counts.RowsAffected,
                    report.Counts.RetiredRows,
                    report.Counts.ChildRows);
                break;

            // WriteFailed has already been logged with its exception, where the exception could be attached.
            case LookupRefreshStatus.WriteFailed:
                break;

            default:
                LookupRefreshLog.ListNotRefreshed(
                    logger,
                    loadRunId,
                    report.Lookup.Name,
                    report.Status,
                    report.Problem ?? "no reason was recorded, which is itself a defect.");
                break;
        }
    }

    /// <summary>
    /// One line about a failed call, composed from the classification rather than from EPA's prose.
    /// </summary>
    /// <remarks>
    /// <b><c>ApiFetchResult.ApiErrorMessage</c> is deliberately not used.</b> The data client carries it into
    /// <c>logs.HandlerLoadAttempt.ApiErrorMessage</c>, a column whose description says "stored as received" —
    /// but a lookup has no attempt row to put it in, so the only place it could go from here is
    /// <c>logs.LoadRun.FailureMessage</c> and this stage's own log. The documented leak is a gateway echoing
    /// back a request path it could not route, and for a lookup that path is a fixed public segment, so the
    /// risk is small; it is left out anyway because the outcome and the status code are what an operator
    /// acts on, and <c>ApiErrorId</c> is what EPA's own support asks for.
    /// </remarks>
    private static string Describe(ApiFetchResult result) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "the call ended in {0}{1}{2}.",
            result.Outcome,
            result.HttpStatusCode is int status
                ? string.Format(CultureInfo.InvariantCulture, " (HTTP {0})", status)
                : " (no response)",
            result.ApiErrorId is null
                ? string.Empty
                : string.Format(
                    CultureInfo.InvariantCulture, ", EPA error id {0}", result.ApiErrorId));
}
