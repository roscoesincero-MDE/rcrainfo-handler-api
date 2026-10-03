using System.Globalization;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Asks EPA for one handler's version list and then one of its records, and reports the raw date text both
/// answers carried. Writes nothing anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>This closes the half of G25 that <see cref="SummaryProbe"/> could not reach.</b> [R37] answered the
/// question for the summaries feed — <c>updatedDate</c>, <c>createdDate</c> and <c>receivedDate</c> come back
/// as bare quoted <c>"yyyy-MM-dd"</c> — and the answer does not transfer, because the two feeds are known to
/// differ in date shape and are read by different code. <c>HandlerSource.createdDate</c> and
/// <c>updatedDate</c> have never been seen as EPA writes them.
/// </para>
/// <para>
/// <b>"Parsed successfully" is a weaker claim than "read with your own eyes", and the difference has already
/// cost this project once.</b> <c>SrcCreatedDate</c> arrives from a real payload as <c>2003-07-03</c>, which
/// proves the converter accepted <i>something</i>. That is the exact claim that was true of the auth
/// endpoint's <c>expiration</c> field right up until a <c>+0000</c> offset the pinned specification does not
/// describe broke it, and it was found by failing ([R33]).
/// </para>
/// <para>
/// <b>It is urgent in a way no other outstanding item is, because the evidence decays.</b> No column retains a
/// raw body — <c>dbo.HandlerSource</c> keeps mapped columns and <c>logs.HandlerLoadStatus</c> keeps a hash — so
/// a format not captured at the moment of the call is unobservable afterwards. Every further day of loading
/// adds rows without adding evidence.
/// </para>
/// <para>
/// <b>Two requests, and the second one's key comes from the first.</b> An operator cannot supply a valid
/// <c>(sourceType, sequence)</c> pair: sequences are scoped per source type, EPA's numbering has gaps in 3.4%
/// of lineages, and a wrong pair answers <c>404</c> — the one shape this codebase reads as a withdrawn record.
/// Asking the summaries feed first also buys the comparison the question actually needs, both feeds' date text
/// for the same handler in one errand. See <see cref="SourceProbeRequest"/>.
/// </para>
/// <para>
/// <b>It writes nothing, and that is a design constraint rather than an economy</b> —
/// <see cref="SummaryProbe"/>'s reasoning applies unchanged. No <c>logs.LoadRun</c> row, so
/// <c>CK_logs_LoadRun_RunMode</c> needs no new value and this needed no hand-run script (G3); and no row in the
/// table the monitoring web application reads, because <b>a diagnostic must not be able to look like a
/// load.</b>
/// </para>
/// <para>
/// <b>No retry loop and no pacing of its own.</b> Both live below this class in the client's pipeline, for
/// <see cref="SummaryWalk"/>'s reason.
/// </para>
/// </remarks>
/// <param name="client">The data client. Classifies the answer; does not throw for a failed call.</param>
public sealed class SourceProbe(IRcraInfoDataClient client)
{
    /// <summary>Probes one handler.</summary>
    /// <param name="handlerId">The handler, already normalised by <see cref="SourceProbeRequest"/>.</param>
    /// <param name="cancellationToken">Cancels either call.</param>
    /// <returns>The report. Never null; a refusal by EPA is a report and not an exception.</returns>
    /// <exception cref="InvalidOperationException">
    /// The handler identifier is empty. A caller defect rather than an answer from EPA — the argument parser
    /// refuses this first, so reaching it means the parser was bypassed.
    /// </exception>
    public async Task<SourceProbeReport> ProbeAsync(
        string handlerId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handlerId))
        {
            throw new InvalidOperationException(
                "The record-detail probe was given no handler identifier, so no call was made.");
        }

        string id = handlerId.Trim().ToUpperInvariant();

        ApiFetchResult summaries = await client.FetchAsync(
            RcraInfoDataRequest.SummariesForHandler(id),
            cancellationToken).ConfigureAwait(false);

        if (!summaries.HasPayload)
        {
            // The outcome name and the status code, and nothing else. summaries.FailureMessage is not
            // reproduced: an HttpRequestException's message can carry the request URI, and the credential
            // travels in the URI on the auth call (AR8).
            return Empty(
                id,
                summaries,
                $"EPA did not return a usable version list. Outcome {summaries.Outcome}, status "
                + $"{Status(summaries.HttpStatusCode)}. Nothing was fetched and nothing was written.");
        }

        SummaryPayloadRead read = SummaryPayload.Read(summaries.Payload);

        if (!read.IsReadable)
        {
            return Empty(id, summaries, read.Problem);
        }

        // Every version in the answer to ?handlerId=X must belong to X, on CurrentRecordReconcile's
        // FindForeignHandler reasoning: a response that did not honour the parameter it was given cannot be
        // trusted to have honoured the rest of it.
        HandlerSourceSummary[] mine = [.. read.Summaries
            .Where(summary => summary.HandlerId is not null
                && string.Equals(summary.HandlerId, id, StringComparison.OrdinalIgnoreCase)
                && summary.SourceType is not null
                && summary.Sequence > 0)];

        if (mine.Length == 0)
        {
            return Empty(
                id,
                summaries,
                $"EPA answered the version list with {read.Summaries.Count} row(s) and none of them is a "
                + "usable version of this handler. Nothing was fetched.");
        }

        (HandlerSourceSummary chosen, string because) = Choose(mine);

        ApiFetchResult source = await client.FetchAsync(
            RcraInfoDataRequest.Source(id, chosen.SourceType!, chosen.Sequence),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> versions = Versions(mine);
        IReadOnlyDictionary<string, IReadOnlyList<string>> summaryDates = RawDateScan.Scan(summaries.Payload);

        if (!source.HasPayload)
        {
            return new SourceProbeReport(
                id,
                chosen.SourceType,
                chosen.Sequence,
                because,
                versions,
                summaries.HttpStatusCode,
                summaries.DurationMs,
                summaries.ResponseBytes,
                source.HttpStatusCode,
                source.DurationMs,
                source.ResponseBytes,
                summaryDates,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
                [],
                $"EPA did not return a usable record body. Outcome {source.Outcome}, status "
                + $"{Status(source.HttpStatusCode)}. The version list was read, so the handler exists and this "
                + "is about the one record.");
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> sourceDates = RawDateScan.Scan(source.Payload);

        return new SourceProbeReport(
            id,
            chosen.SourceType,
            chosen.Sequence,
            because,
            versions,
            summaries.HttpStatusCode,
            summaries.DurationMs,
            summaries.ResponseBytes,
            source.HttpStatusCode,
            source.DurationMs,
            source.ResponseBytes,
            summaryDates,
            sourceDates,
            // [R46] Record detail first, matching the order the report prints the two blocks in, so "A vs B"
            // in an entry reads in the same direction as the evidence above it.
            RawDateScan.Disagreements(sourceDates, summaryDates),
            Problem: null);
    }

    /// <summary>Which version to fetch, and why.</summary>
    /// <remarks>
    /// <para>
    /// <b>The version EPA marks as its current record, when there is one.</b> It is the row a regulator reads,
    /// the row <c>dbo.vwHandlerSource</c> exposes, and therefore the row whose date fields matter most — and it
    /// is the version an operator comparing this output against EPA's screen will be looking at.
    /// </para>
    /// <para>
    /// <b>Highest <c>receivedDate</c> and then highest sequence when EPA marks none</b>, which is script
    /// <c>521</c>'s tie-break after [R43] rather than a second rule invented here. A source type having no
    /// current version is the ordinary case and not a defect ([R44]): <c>currentRecord</c> is answered once per
    /// handler, and a probe that refused a handler whose named version happens not to carry the marker would
    /// refuse most of the state.
    /// </para>
    /// <para>
    /// When more than one version claims the marker — 409 pairs do, and that is EPA's own contradiction rather
    /// than ours — the same tie-break settles it, so this cannot disagree with what the loader would have
    /// stored.
    /// </para>
    /// </remarks>
    private static (HandlerSourceSummary Chosen, string Because) Choose(HandlerSourceSummary[] versions)
    {
        HandlerSourceSummary[] current = [.. versions.Where(version => version.CurrentRecord)];

        if (current.Length > 0)
        {
            HandlerSourceSummary chosen = Newest(current);

            return (chosen,
                current.Length == 1
                    ? "the version EPA marks as its current record"
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} versions claim to be EPA's current record, so this is the one script 521 would "
                        + "have stored: highest receivedDate, then highest sequence",
                        current.Length));
        }

        return (Newest(versions),
            "EPA marks no version of this handler current under any source type named here, which is ordinary "
            + "rather than a defect, so this is the newest by receivedDate and then by sequence");
    }

    // NULL receivedDate sorts last under descending order here as it does in 521, so a version with no readable
    // receive date loses to any version that has one rather than winning by accident.
    private static HandlerSourceSummary Newest(HandlerSourceSummary[] versions) =>
        versions
            .OrderByDescending(version => version.ReceivedDate.HasValue)
            .ThenByDescending(version => version.ReceivedDate ?? DateOnly.MinValue)
            .ThenByDescending(version => version.Sequence)
            .ThenBy(version => version.SourceType, StringComparer.Ordinal)
            .First();

    private static IReadOnlyList<string> Versions(HandlerSourceSummary[] versions) =>
        [.. versions
            .OrderBy(version => version.SourceType, StringComparer.Ordinal)
            .ThenBy(version => version.Sequence)
            .Select(version => string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}{2}",
                version.SourceType,
                version.Sequence,
                version.CurrentRecord ? "*" : string.Empty))];

    private static SourceProbeReport Empty(string handlerId, ApiFetchResult summaries, string? problem) =>
        new(handlerId,
            SourceType: null,
            Sequence: null,
            ChosenBecause: null,
            VersionsAvailable: [],
            summaries.HttpStatusCode,
            summaries.DurationMs,
            summaries.ResponseBytes,
            SourceHttpStatusCode: null,
            SourceDurationMs: 0,
            SourceResponseBytes: null,
            SummariesRawDates: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            SourceRawDates: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            Disagreements: [],
            Problem: problem);

    private static string Status(int? code) =>
        code?.ToString(CultureInfo.InvariantCulture) ?? "(none)";
}
