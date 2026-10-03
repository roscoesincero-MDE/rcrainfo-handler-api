using System.Globalization;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// One handler that the probed window named more than once, and the versions it named.
/// </summary>
/// <param name="HandlerId">The handler's EPA identifier, which is not a secret (script 506).</param>
/// <param name="Versions">
/// Its <c>sourceType</c>/<c>sequence</c> pairs as the window reported them, in ascending order.
/// </param>
/// <param name="CurrentRecordCount">
/// How many of those versions EPA marked as its current record. Expected to be at most one per
/// <c>(handlerId, sourceType)</c>; more than that is a finding about EPA rather than about this loader.
/// </param>
/// <param name="MaxSequence">
/// The highest <c>sequence</c> the window showed for this handler, across every <c>sourceType</c>.
/// <b>This, and not <c>Versions.Count</c>, is what says how much history the handler has</b> — see
/// <see cref="SummaryProbeReport.DeepestHistoryCandidates"/>.
/// </param>
public sealed record ProbedHandlerVersions(
    string HandlerId,
    IReadOnlyList<string> Versions,
    int CurrentRecordCount,
    int MaxSequence)
{
    /// <summary>One line, for the probe's console output.</summary>
    /// <returns>The identifier, its versions, how many are current, and its highest sequence.</returns>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}  {1} row(s) in window: {2}  current={3}  highestSequence={4}",
            HandlerId,
            Versions.Count,
            string.Join(", ", Versions),
            CurrentRecordCount,
            MaxSequence);
}

/// <summary>
/// What one summaries window actually cost and actually contained.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is a count, a size, a duration, a date or a handler identifier</b> — the same rule
/// AR8 applies to <c>logs.ExecutionLog</c>, and it holds even though this report never reaches the database.
/// It is printed to a console an operator may paste into a ticket, which is a wider audience than a log
/// table, not a narrower one.
/// </para>
/// <para>
/// <b><see cref="RawDateSamples"/> is the exception, and it is the point of the probe rather than a lapse.</b>
/// G25 asks what EPA's date fields look like <i>as sent</i>, and [R33] is the reason it is asked at all: the
/// auth endpoint's <c>expiration</c> used an offset form the pinned specification does not describe, and it
/// was found by failing. A date of record is not personal information; a contact name is, and no contact
/// field appears anywhere in the summaries payload, so there is nothing here to withhold.
/// </para>
/// </remarks>
/// <param name="ActivityLocation">The two-letter state asked about.</param>
/// <param name="FromDate">First day of the window, inclusive.</param>
/// <param name="ToDate">Last day of the window, inclusive.</param>
/// <param name="HttpStatusCode">What EPA answered, when it answered.</param>
/// <param name="DurationMs">How long the single call took.</param>
/// <param name="ResponseBytes">
/// The response size. The number F2 cannot size a slice without: <c>summaries</c> takes no <c>offset</c> and
/// no <c>limit</c>, so the window is the only lever on it.
/// </param>
/// <param name="SummaryCount">How many summary rows the window returned.</param>
/// <param name="DistinctHandlerCount">How many distinct handlers those rows named.</param>
/// <param name="CurrentRecordCount">How many rows EPA marked as its current record.</param>
/// <param name="MultiVersionHandlers">
/// The handlers the window named more than once, most rows first. Mostly these turn out to be one handler with
/// two <i>source types</i> rather than two versions of one lineage — which is worth seeing, but is not the
/// thing <c>--every-version</c> needs. For that, read <see cref="DeepestHistoryCandidates"/>.
/// </param>
/// <param name="DeepestHistoryCandidates">
/// The handlers with the highest <c>sequence</c> the window showed, deepest first.
/// <b>These are the <c>--every-version</c> candidates, and the distinction from
/// <paramref name="MultiVersionHandlers"/> is not a nicety.</b> A window reports the versions <i>updated</i>
/// within it, so a handler appearing once as <c>N/9</c> has nine versions in EPA's history and a handler
/// appearing twice as <c>B/1, N/1</c> has two lineages of one version each. The first exercises the history
/// path nine deep; the second does not exercise it at all.
/// </param>
/// <param name="RawDateSamples">
/// Distinct raw JSON text, per date property, exactly as EPA sent it — the G25 answer.
/// </param>
/// <param name="UnexpectedProperties">
/// Property names EPA sent that <see cref="HandlerSourceSummary"/> has no home for. Drift between the pinned
/// specification and the running service, which is the one thing a probe is better placed to notice than a
/// load is.
/// </param>
/// <param name="Problem">
/// Why the probe has no answer, or <see langword="null"/>. Safe to print: a status code, a JSON path or a
/// value kind, never any part of the body.
/// </param>
public sealed record SummaryProbeReport(
    string ActivityLocation,
    DateOnly FromDate,
    DateOnly ToDate,
    int? HttpStatusCode,
    int DurationMs,
    int? ResponseBytes,
    int SummaryCount,
    int DistinctHandlerCount,
    int CurrentRecordCount,
    IReadOnlyList<ProbedHandlerVersions> MultiVersionHandlers,
    IReadOnlyList<ProbedHandlerVersions> DeepestHistoryCandidates,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RawDateSamples,
    IReadOnlyList<string> UnexpectedProperties,
    string? Problem)
{
    /// <summary>Whether the window was answered and read.</summary>
    public bool IsAnswered => Problem is null;

    /// <summary>Bytes per summary row, for sizing a wider window.</summary>
    /// <remarks>
    /// Reported rather than left for the reader to divide, because the whole purpose of the number is to be
    /// multiplied by an estimate of how many rows a wider range holds — and the division is the step where a
    /// zero-row window turns into a division by zero.
    /// </remarks>
    public int? BytesPerSummary =>
        ResponseBytes is { } bytes && SummaryCount > 0 ? bytes / SummaryCount : null;

    /// <summary>The whole report, as an operator should see it.</summary>
    /// <returns>Several lines. Contains no credential, no URI and no query string.</returns>
    public override string ToString()
    {
        System.Text.StringBuilder text = new();

        text.Append(CultureInfo.InvariantCulture, $"Summaries probe: {ActivityLocation} ")
            .Append(CultureInfo.InvariantCulture, $"{FromDate:yyyy-MM-dd} to {ToDate:yyyy-MM-dd}")
            .AppendLine()
            .Append(CultureInfo.InvariantCulture, $"  http={HttpStatusCode?.ToString(
                CultureInfo.InvariantCulture) ?? "(none)"} duration={DurationMs}ms ")
            .Append(CultureInfo.InvariantCulture, $"bytes={ResponseBytes?.ToString(
                CultureInfo.InvariantCulture) ?? "(none)"}")
            .AppendLine();

        if (!IsAnswered)
        {
            text.Append("  no answer: ").AppendLine(Problem);

            return text.ToString();
        }

        text.Append(CultureInfo.InvariantCulture, $"  summaries={SummaryCount} handlers=")
            .Append(CultureInfo.InvariantCulture, $"{DistinctHandlerCount} current={CurrentRecordCount} ")
            .Append(CultureInfo.InvariantCulture, $"bytesPerSummary={BytesPerSummary?.ToString(
                CultureInfo.InvariantCulture) ?? "(no rows)"}")
            .AppendLine();

        // G25 first among the details, because it is the one thing here that a later run cannot recover: the
        // raw body is not retained anywhere, so a format seen and not written down is a format unobserved.
        foreach ((string property, IReadOnlyList<string> samples) in RawDateSamples)
        {
            text.Append(CultureInfo.InvariantCulture, $"  raw {property}: ")
                .AppendLine(samples.Count == 0 ? "(never present)" : string.Join(", ", samples));
        }

        if (UnexpectedProperties.Count > 0)
        {
            text.Append("  properties this loader cannot bind: ")
                .AppendLine(string.Join(", ", UnexpectedProperties));
        }

        // The --every-version candidates first, and by depth of history rather than by rows in the window,
        // because that is the question being asked. A window shows the versions updated within it, so one row
        // reading N/9 is nine versions of history and two rows reading B/1, N/1 are none.
        if (DeepestHistoryCandidates.Count == 0)
        {
            text.AppendLine(
                "  every handler in this window is at sequence 1, so this window cannot supply a candidate "
                + "for --every-version: on a single-version handler that switch and --current-record fetch "
                + "the same one version and prove the same nothing.");
        }
        else
        {
            text.AppendLine("  deepest history in this window -- the --every-version candidates:");

            foreach (ProbedHandlerVersions handler in DeepestHistoryCandidates)
            {
                text.Append("    ").AppendLine(handler.ToString());
            }
        }

        if (MultiVersionHandlers.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                    $"  {MultiVersionHandlers.Count} handler(s) the window named more than once (usually two ")
                .AppendLine("source types, not two versions of one):");

            foreach (ProbedHandlerVersions handler in MultiVersionHandlers)
            {
                text.Append("    ").AppendLine(handler.ToString());
            }
        }

        return text.ToString();
    }
}
