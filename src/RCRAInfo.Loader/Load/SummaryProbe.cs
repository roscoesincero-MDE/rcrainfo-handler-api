using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Options;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Asks EPA for one summaries window and reports what came back, writing nothing anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because three questions in the plan could not be answered by any load, and one of them
/// cannot be answered after the fact at all.</b> [R28] needs the row count and the response size for a
/// single day of one state, because <c>summaries</c> takes no <c>offset</c> and no <c>limit</c> and the
/// window is therefore the only lever on response size. G25 needs EPA's date fields as literally sent, and
/// <b>no column retains a raw body</b> — <c>dbo.HandlerSource</c> keeps mapped columns and
/// <c>logs.HandlerLoadStatus</c> keeps a hash — so a format not written down at the moment of the call is a
/// format unobserved. And F1's second pass needs a handler with more than one version, because on a
/// single-version handler <c>--every-version</c> and <c>--current-record</c> do the same thing and prove the
/// same nothing.
/// </para>
/// <para>
/// <b>It writes nothing, and that is a design constraint rather than an economy.</b> No <c>logs.LoadRun</c>
/// row, no <c>logs.HandlerLoadStatus</c> row, no watermark. Two consequences follow and both are wanted.
/// First, <c>CK_logs_LoadRun_RunMode</c> needs no new value, so this needed no hand-run script (G3) — the
/// probe was usable the day it was written. Second, and more important: a probe that opened a run row would
/// put a row in the table the monitoring web application reads, and an operator scanning that table would
/// see a load that loaded nothing. <b>A diagnostic must not be able to look like a load.</b>
/// </para>
/// <para>
/// <b>One request, deliberately, and no windowing.</b> <see cref="SummaryWalk"/> splits a range into
/// <c>WindowDays</c> windows and de-duplicates across them; this asks exactly what it was asked to ask. The
/// difference is the point: the walk answers "which versions exist in this range", and the probe answers
/// "what does one window cost", which is a question a walk destroys by splitting.
/// </para>
/// <para>
/// <b>No retry loop and no pacing of its own.</b> Both live below this class in the client's pipeline, for
/// <see cref="SummaryWalk"/>'s reason.
/// </para>
/// </remarks>
/// <param name="client">The data client. Classifies the answer; does not throw for a failed call.</param>
/// <param name="options">The run's scope. Only the activity location is used — never <c>WindowDays</c>.</param>
public sealed class SummaryProbe(
    IRcraInfoDataClient client,
    IOptions<LoadRunOptions> options)
{
    /// <summary>How many <c>--every-version</c> candidates to name.</summary>
    /// <remarks>
    /// Five, because the operator is going to pick one and run it. A full ranking of two thousand handlers by
    /// sequence would bury the answer in the output it is printed to, which is the same reason
    /// <see cref="MaxRawSamplesPerProperty"/> exists.
    /// </remarks>
    private const int MaxHistoryCandidates = 5;

    /// <summary>How many distinct raw values to keep per date property.</summary>
    /// <remarks>
    /// Distinct values, capped. The question is what <i>shapes</i> EPA sends, and a window of five thousand
    /// rows carrying one shape answers it as completely as a window of five — while an uncapped list would
    /// bury the answer in the output it is printed to.
    /// </remarks>
    private const int MaxRawSamplesPerProperty = 8;

    /// <summary>The date properties G25 is about, as they are spelled in the payload.</summary>
    /// <remarks>
    /// <c>receivedDate</c> is included although G25 does not name it: it is read by the same converter, it is
    /// on the same object, and a probe that had to be re-run to answer a question it could have answered on
    /// the first call is a probe that cost more than it needed to.
    /// </remarks>
    private static readonly string[] DateProperties = ["updatedDate", "createdDate", "receivedDate"];

    private readonly LoadRunOptions runOptions = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Probes one window.</summary>
    /// <param name="fromDate">First day, inclusive.</param>
    /// <param name="toDate">Last day, inclusive.</param>
    /// <param name="cancellationToken">Cancels the single call.</param>
    /// <returns>The report. Never null; a refusal by EPA is a report and not an exception.</returns>
    /// <exception cref="InvalidOperationException">
    /// The <c>RCRAInfoLoad</c> options are unusable, or the range is inverted. Both are caller or
    /// configuration defects rather than answers from EPA, and <see cref="SummaryWalk"/> throws for the same
    /// two for the same reason.
    /// </exception>
    public async Task<SummaryProbeReport> ProbeAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> problems = runOptions.Validate();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The RCRAInfoLoad configuration is not usable, so no summaries window can be asked for: "
                + string.Join(" ", problems));
        }

        if (toDate < fromDate)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The probe was asked for {0:yyyy-MM-dd} to {1:yyyy-MM-dd}, which ends before it starts. "
                    + "EPA answers an inverted range with 200 and an empty array, so the probe would report "
                    + "a quiet window rather than a bad question.",
                    fromDate,
                    toDate));
        }

        string activityLocation = runOptions.NormalizedActivityLocation();

        ApiFetchResult result = await client.FetchAsync(
            RcraInfoDataRequest.Summaries(activityLocation, fromDate, toDate),
            cancellationToken).ConfigureAwait(false);

        if (!result.HasPayload)
        {
            // The outcome name and the status code, and nothing else. result.FailureMessage is not
            // reproduced: an HttpRequestException's message can carry the request URI, and the credential
            // travels in the URI on the auth call (AR8).
            return Empty(
                activityLocation,
                fromDate,
                toDate,
                result,
                $"EPA did not return a usable body. Outcome {result.Outcome}, status "
                + $"{result.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}.");
        }

        SummaryPayloadRead read = SummaryPayload.Read(result.Payload);

        if (!read.IsReadable)
        {
            return Empty(activityLocation, fromDate, toDate, result, read.Problem);
        }

        IReadOnlyList<ProbedHandlerVersions> handlers = Group(read.Summaries);

        return new SummaryProbeReport(
            activityLocation,
            fromDate,
            toDate,
            result.HttpStatusCode,
            result.DurationMs,
            result.ResponseBytes,
            read.Summaries.Count,
            handlers.Count,
            read.CurrentRecordCount,
            [.. handlers
                .Where(handler => handler.Versions.Count > 1)
                .OrderByDescending(handler => handler.Versions.Count)
                .ThenBy(handler => handler.HandlerId, StringComparer.Ordinal)],
            [.. handlers
                .Where(handler => handler.MaxSequence > 1)
                .OrderByDescending(handler => handler.MaxSequence)
                .ThenBy(handler => handler.HandlerId, StringComparer.Ordinal)
                .Take(MaxHistoryCandidates)],
            RawDateSamples(result.Payload!),
            read.UnexpectedProperties,
            Problem: null);
    }

    private static SummaryProbeReport Empty(
        string activityLocation,
        DateOnly fromDate,
        DateOnly toDate,
        ApiFetchResult result,
        string? problem) =>
        new(activityLocation,
            fromDate,
            toDate,
            result.HttpStatusCode,
            result.DurationMs,
            result.ResponseBytes,
            SummaryCount: 0,
            DistinctHandlerCount: 0,
            CurrentRecordCount: 0,
            MultiVersionHandlers: [],
            DeepestHistoryCandidates: [],
            RawDateSamples: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            UnexpectedProperties: [],
            Problem: problem);

    /// <summary>Groups the window's rows by handler, once, for both of the report's two lists.</summary>
    /// <remarks>
    /// <para>
    /// <b>The two lists it feeds answer different questions and the difference was measured rather than
    /// reasoned about.</b> A 91-day window of <c>MD</c> named nine handlers more than once, and eight of those
    /// nine were one handler with two <i>source types</i> — <c>B/7</c> and <c>N/3</c> — rather than two
    /// versions of one lineage. Meanwhile the same window showed handlers sitting at <c>B/10</c> in a single
    /// row, which is ten versions of history that a "named more than once" filter throws away entirely.
    /// </para>
    /// <para>
    /// So <see cref="ProbedHandlerVersions.MaxSequence"/> is carried and is what the
    /// <c>--every-version</c> ranking uses. The rows-in-window count is still reported, because two source
    /// types on one handler is a real fact about the data and the one that decides whether
    /// <c>currentRecord</c> can be true twice for the same identifier.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ProbedHandlerVersions> Group(
        IReadOnlyList<HandlerSourceSummary> summaries) =>
        [.. summaries
            .Where(summary => summary.HandlerId is not null)
            .GroupBy(summary => summary.HandlerId!, StringComparer.Ordinal)
            .Select(group => new ProbedHandlerVersions(
                group.Key,
                [.. group
                    .OrderBy(summary => summary.SourceType, StringComparer.Ordinal)
                    .ThenBy(summary => summary.Sequence)
                    .Select(summary => string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}/{1}",
                        summary.SourceType ?? "?",
                        summary.Sequence))],
                group.Count(summary => summary.CurrentRecord),
                group.Max(summary => summary.Sequence)))];

    /// <summary>
    /// The distinct raw JSON text of each date property, as EPA sent it — the G25 answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from the body rather than from the bound objects, and that is the whole value of it.</b> A
    /// <see cref="DateOnly"/> that came out of <c>RcraInfoDateConverter</c> tells you the converter accepted
    /// something; it cannot tell you what. That distinction is not academic — the auth endpoint's
    /// <c>expiration</c> field parsed cleanly in every offline test and carried a <c>+0000</c> offset the
    /// pinned specification does not describe ([R33]).
    /// </para>
    /// <para>
    /// <c>GetRawText</c> rather than <c>GetString</c>, so a numeric or <c>null</c> value is reported as what
    /// it is instead of as an empty string. A property present with a JSON <c>null</c> and a property absent
    /// are different findings.
    /// </para>
    /// </remarks>
    private static Dictionary<string, IReadOnlyList<string>> RawDateSamples(string payload)
    {
        Dictionary<string, List<string>> samples = new(StringComparer.Ordinal);

        foreach (string property in DateProperties)
        {
            samples[property] = [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                // Not reported as a problem: SummaryPayload.Read has already accepted the body, so reaching
                // this would mean the two disagree about its shape, and the probe's job is not to arbitrate.
                return Freeze(samples);
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (string property in DateProperties)
                {
                    if (!element.TryGetProperty(property, out JsonElement value))
                    {
                        continue;
                    }

                    string raw = value.GetRawText();
                    List<string> seen = samples[property];

                    if (seen.Count < MaxRawSamplesPerProperty
                        && !seen.Contains(raw, StringComparer.Ordinal))
                    {
                        seen.Add(raw);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Swallowed, and only here. SummaryPayload.Read parses the same body and reports its own
            // problem, so a throw from this second parse would replace a diagnosed refusal with an
            // undiagnosed one. The samples stay empty, which is honest.
            return Freeze(samples);
        }

        return Freeze(samples);
    }

    // Returns the concrete Dictionary rather than the interface because CA1859 is an error here and the caller
    // widens it anyway. The name still says what it is for: every exit from RawDateSamples goes through this,
    // so the lists cannot escape as the mutable List<string> they are built as.
    private static Dictionary<string, IReadOnlyList<string>> Freeze(
        Dictionary<string, List<string>> samples) =>
        samples.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
}
