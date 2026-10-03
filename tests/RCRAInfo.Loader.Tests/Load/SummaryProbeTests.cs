using Microsoft.Extensions.Options;

using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The read-only measurement, and the three questions it exists to answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertions here are unusually literal, and that is the point of the class under test.</b> Everything
/// else in this suite checks a decision — whether to advance a watermark, whether a 404 is a deletion. The
/// probe makes no decisions; it reports. So what has to be tested is that the reported numbers are the ones
/// that came back, and above all that <see cref="SummaryProbeReport.RawDateSamples"/> carries EPA's text
/// <i>verbatim</i>.
/// </para>
/// <para>
/// <b>Verbatim is the whole value.</b> A date read through <c>RcraInfoDateConverter</c> proves only that the
/// converter accepted something; it cannot show what. That distinction has already cost this project once —
/// [R33]'s <c>+0000</c> offset parsed cleanly in every offline test and was not in the pinned specification —
/// and unlike that case there is no second chance here: no column anywhere retains a raw body, so a format the
/// probe rounds off is a format nobody can recover.
/// </para>
/// <para>
/// <b>Exactly one request, asserted.</b> A probe that quietly windowed its range would report the size of the
/// last window and call it the size of the range, and the number it exists to produce would be wrong in a way
/// that looks entirely plausible.
/// </para>
/// </remarks>
public sealed class SummaryProbeTests
{
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 1);

    /// <summary>One window is one request, whatever <c>WindowDays</c> says.</summary>
    /// <remarks>
    /// <c>WindowDays</c> is deliberately set to 1 against a 28-day range here: the walk would send 28
    /// requests, and the probe must send one. Splitting would make the reported response size the size of a
    /// fragment while the row count stayed the total, so <c>BytesPerSummary</c> would come out roughly
    /// twenty-eight times too small — a plausible-looking number that would under-size F2 by that factor.
    /// </remarks>
    [Fact]
    public async Task OneWindowIsOneRequestNoMatterWhatTheWalkWidthIs()
    {
        (SummaryProbe probe, StubDataClient client) = Build(windowDays: 1);

        await probe.ProbeAsync(From, new DateOnly(2026, 9, 28));

        Assert.Single(client.RequestedUris);
        Assert.Contains("startDate=2026-09-01", client.RequestedUris[0], StringComparison.Ordinal);
        Assert.Contains("endDate=2026-09-28", client.RequestedUris[0], StringComparison.Ordinal);
        Assert.Contains("activityLocation=MD", client.RequestedUris[0], StringComparison.Ordinal);
    }

    /// <summary>The counts are the ones the body carried, including the distinct-handler count.</summary>
    /// <remarks>
    /// Four rows naming three handlers, so <c>SummaryCount</c> and <c>DistinctHandlerCount</c> cannot both be
    /// satisfied by the same number — a body where they coincide would let a copy-paste between them pass.
    /// </remarks>
    [Fact]
    public async Task TheCountsAreTheOnesTheBodyCarried()
    {
        (SummaryProbe probe, _) = Build(answer: FourRowsThreeHandlers);

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.True(report.IsAnswered);
        Assert.Equal(4, report.SummaryCount);
        Assert.Equal(3, report.DistinctHandlerCount);
        Assert.Equal(3, report.CurrentRecordCount);
        Assert.Equal(200, report.HttpStatusCode);
    }

    /// <summary>
    /// EPA's date text is reported exactly as sent, quotes and all, and not as a parsed value.
    /// </summary>
    /// <remarks>
    /// <b>The single most consequential assertion in the file.</b> The canned body below carries a trailing
    /// <c>+0000</c> on one row and a bare date on another — the two shapes [R33] taught this project to expect
    /// — and both must survive to the report. Anything that re-rendered them through <c>DateOnly</c> would
    /// report two identical values and G25 would be answered wrongly, with no way to tell afterwards because
    /// no column retains the body.
    /// </remarks>
    [Fact]
    public async Task TheRawDateTextIsReportedExactlyAsEpaSentIt()
    {
        (SummaryProbe probe, _) = Build(answer: FourRowsThreeHandlers);

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.Equal(
            ["\"2003-07-03T00:00:00.000+0000\"", "\"2019-11-14\""],
            report.RawDateSamples["updatedDate"]);
    }

    /// <summary>A property EPA sent with a JSON null is reported as null, not as absent and not as empty.</summary>
    /// <remarks>
    /// Three different findings that a <c>GetString</c> would flatten into one: present with a value, present
    /// and null, absent altogether. Which of the three a field is decides whether the column mapped from it
    /// may be <c>NOT NULL</c>, so collapsing them would answer a schema question wrongly.
    /// </remarks>
    [Fact]
    public async Task APropertyPresentWithAJsonNullIsDistinguishableFromAnAbsentOne()
    {
        (SummaryProbe probe, _) = Build(answer: FourRowsThreeHandlers);

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.Contains("null", report.RawDateSamples["createdDate"]);
        Assert.Empty(report.RawDateSamples["receivedDate"]);
    }

    /// <summary>Distinct values only, so one shape repeated a thousand times reports once.</summary>
    [Fact]
    public async Task RepeatedDateShapesAreReportedOnce()
    {
        (SummaryProbe probe, _) = Build(
            answer: request => Journalled.LookupResult(
                request,
                """
                [{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"updatedDate":"2019-11-14"},
                 {"handlerId":"MDD000000002","sourceType":"N","sequence":1,"updatedDate":"2019-11-14"},
                 {"handlerId":"MDD000000003","sourceType":"N","sequence":1,"updatedDate":"2019-11-14"}]
                """));

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.Equal(["\"2019-11-14\""], report.RawDateSamples["updatedDate"]);
    }

    /// <summary>
    /// The handlers the window named more than once are listed, most rows first, and single-row handlers
    /// are not.
    /// </summary>
    [Fact]
    public async Task MultiRowHandlersAreListedMostRowsFirst()
    {
        (SummaryProbe probe, _) = Build(answer: FourRowsThreeHandlers);

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        // MDD000000002 has two rows; the other two handlers have one each and must not appear.
        Assert.Single(report.MultiVersionHandlers);
        Assert.Equal("MDD000000002", report.MultiVersionHandlers[0].HandlerId);
        Assert.Equal(["N/1", "N/2"], report.MultiVersionHandlers[0].Versions);
        Assert.Equal(1, report.MultiVersionHandlers[0].CurrentRecordCount);
    }

    /// <summary>
    /// The <c>--every-version</c> candidates are ranked by how deep their history is, not by how many rows the
    /// window happened to show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sharpest test in the file, and it was written because the first live 91-day window disagreed
    /// with the code.</b> Nine handlers were named more than once and eight of those nine turned out to be one
    /// handler with two <i>source types</i> — <c>B/7</c> and <c>N/3</c> — which is two lineages, not a history.
    /// Meanwhile handlers sitting at <c>B/10</c> in a single row were being discarded, and ten versions of
    /// history is exactly what <c>--every-version</c> was waiting for.
    /// </para>
    /// <para>
    /// The body below encodes that shape deliberately: <c>MDD000000009</c> appears once at sequence 9 and
    /// <c>MDD000000002</c> appears twice at sequences 1 and 2. Ranking by rows would recommend the wrong one,
    /// and the recommendation is the whole output.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheEveryVersionCandidatesAreRankedByDepthOfHistoryNotByRowsInTheWindow()
    {
        (SummaryProbe probe, _) = Build(
            answer: request => Journalled.LookupResult(
                request,
                """
                [{"handlerId":"MDD000000009","sourceType":"B","sequence":9,"currentRecord":true},
                 {"handlerId":"MDD000000002","sourceType":"N","sequence":1,"currentRecord":false},
                 {"handlerId":"MDD000000002","sourceType":"N","sequence":2,"currentRecord":true},
                 {"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":true}]
                """));

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        // Deepest first: sequence 9 beats sequence 2, even though the sequence-2 handler has twice the rows.
        Assert.Equal(
            ["MDD000000009", "MDD000000002"],
            report.DeepestHistoryCandidates.Select(handler => handler.HandlerId));

        Assert.Equal(9, report.DeepestHistoryCandidates[0].MaxSequence);

        // And the rows-in-window list still says what it says, which is a different fact.
        Assert.Equal("MDD000000002", Assert.Single(report.MultiVersionHandlers).HandlerId);
    }

    /// <summary>
    /// A handler at sequence 1 is not a candidate however many source types it has, because there is no
    /// history to walk.
    /// </summary>
    /// <remarks>
    /// The eight-of-nine case from the live window, isolated. Two source types at sequence 1 each is two rows
    /// and no history: <c>--every-version</c> on it fetches two versions where <c>--current-record</c> fetches
    /// two, and the history path is still unexercised.
    /// </remarks>
    [Fact]
    public async Task TwoSourceTypesAtSequenceOneIsNotAHistoryCandidate()
    {
        (SummaryProbe probe, _) = Build(
            answer: request => Journalled.LookupResult(
                request,
                """
                [{"handlerId":"MDD000000001","sourceType":"B","sequence":1,"currentRecord":true},
                 {"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":true}]
                """));

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.Single(report.MultiVersionHandlers);
        Assert.Empty(report.DeepestHistoryCandidates);
        Assert.Contains("sequence 1", report.ToString(), StringComparison.Ordinal);

        // Two current records for one handler is correct rather than a defect: EPA marks one per
        // (handlerId, sourceType), and this handler has two source types.
        Assert.Equal(2, report.MultiVersionHandlers[0].CurrentRecordCount);
    }

    /// <summary>A window with no history at all says so rather than reporting an empty list quietly.</summary>
    [Fact]
    public async Task AWindowWithNoHistoryCandidateSaysSo()
    {
        (SummaryProbe probe, _) = Build();

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.Empty(report.DeepestHistoryCandidates);
        Assert.Contains("--every-version", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Bytes per summary is reported, and an empty window does not divide by zero.</summary>
    /// <remarks>
    /// An empty window is the ordinary case for a single quiet day, so the guard is not defensive
    /// programming — it is the first thing an operator probing yesterday is likely to hit.
    /// </remarks>
    [Fact]
    public async Task BytesPerSummaryIsReportedAndAnEmptyWindowDoesNotDivideByZero()
    {
        (SummaryProbe withRows, _) = Build(answer: FourRowsThreeHandlers);
        (SummaryProbe empty, _) = Build(answer: request => Journalled.LookupResult(request, "[]"));

        SummaryProbeReport rows = await withRows.ProbeAsync(From, To);
        SummaryProbeReport quiet = await empty.ProbeAsync(From, To);

        Assert.NotNull(rows.BytesPerSummary);
        Assert.True(rows.BytesPerSummary > 0);

        Assert.True(quiet.IsAnswered);
        Assert.Equal(0, quiet.SummaryCount);
        Assert.Null(quiet.BytesPerSummary);
    }

    /// <summary>
    /// A documented 404 is a quiet window here, exactly as it is for the walk — never a deletion signal.
    /// </summary>
    /// <remarks>
    /// The probe writes nothing, so it could not soft-delete anything even if it read the outcome the other
    /// way. It is asserted anyway: this is the second reader of that outcome on this endpoint, and the two
    /// must not drift apart, because the day they disagree is the day somebody reasons about the loader from
    /// the probe's output.
    /// </remarks>
    [Fact]
    public async Task ADocumentedNotFoundIsReportedWithoutAnyDeletionLanguage()
    {
        (SummaryProbe probe, _) = Build(
            answer: request => Journalled.LookupFailure(request, ApiFetchOutcome.NotFound, 404));

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        // No payload, so there is nothing to read -- but the problem names the outcome and the status and
        // nothing more. It is not, and must not read as, a statement about any handler.
        Assert.False(report.IsAnswered);
        Assert.Equal(404, report.HttpStatusCode);
        Assert.DoesNotContain("delete", report.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A failed call is a report, not an exception, and the report names no URI.</summary>
    /// <remarks>
    /// <b>The URI is the assertion that matters.</b> <c>ApiFetchResult.FailureMessage</c> can carry an
    /// <c>HttpRequestException</c>'s message, and a request URI is where the credential travels on the auth
    /// call (AR8). This output is printed to a console an operator may paste into a ticket, which is a wider
    /// audience than a log table.
    /// </remarks>
    [Fact]
    public async Task AFailedCallIsReportedAndCarriesNoUriAndNoCredential()
    {
        (SummaryProbe probe, _) = Build(
            answer: request => Journalled.LookupFailure(request, ApiFetchOutcome.AccessDenied, 403));

        SummaryProbeReport report = await probe.ProbeAsync(From, To);
        string printed = report.ToString();

        Assert.False(report.IsAnswered);
        Assert.Equal(403, report.HttpStatusCode);
        Assert.DoesNotContain("http://", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api/v1", printed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unreadable body is reported with the payload's own problem and no part of the body.</summary>
    [Fact]
    public async Task AnUnreadableBodyIsReportedWithoutQuotingIt()
    {
        (SummaryProbe probe, _) = Build(
            answer: request => Journalled.LookupResult(request, """{"unexpected":"object root"}"""));

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.False(report.IsAnswered);
        Assert.NotNull(report.Problem);
        Assert.DoesNotContain("object root", report.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A property EPA sent that this loader has nowhere to put is reported, because drift is what a probe
    /// notices first.
    /// </summary>
    [Fact]
    public async Task APropertyThisLoaderCannotBindIsReported()
    {
        (SummaryProbe probe, _) = Build(
            answer: request => Journalled.LookupResult(
                request,
                """[{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"somethingNew":"x"}]"""));

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.Contains("somethingNew", report.UnexpectedProperties);
        Assert.Contains("somethingNew", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An inverted range throws before anything is asked, and the throw says why.</summary>
    /// <remarks>
    /// The dangerous alternative is not an error — it is a plausible answer. EPA replies to an inverted range
    /// with 200 and an empty array, so the probe would report a quiet window and somebody would conclude the
    /// state had no activity in a fortnight it was busy.
    /// </remarks>
    [Fact]
    public async Task AnInvertedRangeThrowsBeforeAnythingIsAsked()
    {
        (SummaryProbe probe, StubDataClient client) = Build();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => probe.ProbeAsync(To, From.AddDays(-14)));

        Assert.Contains("empty array", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.RequestedUris);
    }

    /// <summary>Unusable configuration throws before anything is asked, as it does for the walk.</summary>
    /// <param name="activityLocation">The unusable value.</param>
    [Theory]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("MDX")]
    public async Task UnusableOptionsThrowBeforeAnythingIsAsked(string activityLocation)
    {
        (SummaryProbe probe, StubDataClient client) = Build(activityLocation: activityLocation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.ProbeAsync(From, To));

        Assert.Empty(client.RequestedUris);
    }

    /// <summary>The configured activity location is normalised the same way the walk normalises it.</summary>
    [Fact]
    public async Task TheActivityLocationIsNormalisedBeforeItIsSent()
    {
        (SummaryProbe probe, StubDataClient client) = Build(activityLocation: " md ");

        SummaryProbeReport report = await probe.ProbeAsync(From, To);

        Assert.Contains("activityLocation=MD", client.RequestedUris[0], StringComparison.Ordinal);
        Assert.Equal("MD", report.ActivityLocation);
    }

    /// <summary>
    /// Four rows naming three handlers, with the two date shapes [R33] taught this project to expect.
    /// </summary>
    /// <remarks>
    /// <c>createdDate</c> appears once as a JSON <c>null</c> and <c>receivedDate</c> never appears at all, so
    /// the three states a property can be in are all represented in one body.
    /// </remarks>
    private static ApiFetchResult FourRowsThreeHandlers(RcraInfoDataRequest request) =>
        Journalled.LookupResult(
            request,
            """
            [{"handlerId":"MDD000000001","activityLocation":"MD","sourceType":"N","sequence":1,
              "currentRecord":true,"updatedDate":"2003-07-03T00:00:00.000+0000","createdDate":null},
             {"handlerId":"MDD000000002","activityLocation":"MD","sourceType":"N","sequence":1,
              "currentRecord":false,"updatedDate":"2019-11-14","createdDate":"2019-11-14"},
             {"handlerId":"MDD000000002","activityLocation":"MD","sourceType":"N","sequence":2,
              "currentRecord":true,"updatedDate":"2019-11-14","createdDate":"2019-11-14"},
             {"handlerId":"MDD000000003","activityLocation":"MD","sourceType":"N","sequence":1,
              "currentRecord":true,"updatedDate":"2019-11-14","createdDate":"2019-11-14"}]
            """);

    private static (SummaryProbe Probe, StubDataClient Client) Build(
        Func<RcraInfoDataRequest, ApiFetchResult>? answer = null,
        string activityLocation = "MD",
        int windowDays = 7)
    {
        StubDataClient client = new(
            answer ?? (request => Journalled.LookupResult(
                request,
                """[{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":true}]""")));

        SummaryProbe probe = new(
            client,
            Options.Create(
                new LoadRunOptions { ActivityLocation = activityLocation, WindowDays = windowDays }));

        return (probe, client);
    }
}
