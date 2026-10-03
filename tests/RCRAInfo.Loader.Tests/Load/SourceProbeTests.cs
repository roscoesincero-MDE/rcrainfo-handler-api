using RCRAInfo.Loader.Api;
using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The record-detail probe: two requests, the second keyed by the first, and the raw date text of both.
/// </summary>
/// <remarks>
/// <para>
/// <b>This closes the half of G25 the summaries probe could not reach.</b> [R37] answered the question for
/// <c>/hd/sources/summaries</c>, and the answer does not transfer: the two feeds are known to differ in date
/// shape and are read by different code, so <c>HandlerSource.createdDate</c> and <c>updatedDate</c> have never
/// been seen as EPA writes them. <c>SrcCreatedDate</c> sitting in the mirror as <c>2003-07-03</c> proves the
/// converter accepted <i>something</i> — the exact claim that was true of the auth endpoint's
/// <c>expiration</c> until a <c>+0000</c> offset broke it ([R33]).
/// </para>
/// <para>
/// <b>Two requests, asserted, and the second one's key must come from the first.</b> An operator cannot supply a
/// valid <c>(sourceType, sequence)</c> triple — sequences are scoped per source type and EPA's own numbering has
/// gaps in 3.4% of lineages — so a probe that took the version from anywhere but the summaries answer would
/// produce a <c>404</c>, which is the one shape this codebase reads as a withdrawn record.
/// </para>
/// <para>
/// <b>The version choice reuses script 521's tie-break rather than a rule invented here</b>, and that is
/// asserted too: the day the two disagree is the day somebody compares this output against what the loader
/// stored and concludes the mirror is wrong.
/// </para>
/// </remarks>
public sealed class SourceProbeTests
{
    private const string Handler = "MDD000000001";

    /// <summary>Exactly two requests, and the second names the version the first did.</summary>
    /// <remarks>
    /// <b>The count is half the assertion and the key is the other half.</b> A third request would mean the
    /// probe had started walking the handler, which is a load wearing a diagnostic's name; and a detail path
    /// whose version did not come out of the summaries answer is a guessed triple, which answers <c>404</c>.
    /// </remarks>
    [Fact]
    public async Task ExactlyTwoRequestsAndTheSecondCarriesTheVersionTheFirstNamed()
    {
        (SourceProbe probe, StubDataClient client) = Build();

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal(2, client.RequestedUris.Count);
        Assert.Contains($"handlerId={Handler}", client.RequestedUris[0], StringComparison.Ordinal);
        Assert.Equal($"{RcraInfoDataRequest.SourcesPath}/{Handler}/N/1", client.RequestedUris[1]);
        Assert.Equal("N/1", report.Version);
    }

    /// <summary>The identifier is normalised before it is sent, in both requests.</summary>
    /// <remarks>
    /// The detail path is case-sensitive, and the argument layer has already normalised — this is the second
    /// place, because the stage is also reachable from a test and from any future caller.
    /// </remarks>
    [Fact]
    public async Task TheIdentifierIsNormalisedBeforeEitherRequestIsSent()
    {
        (SourceProbe probe, StubDataClient client) = Build();

        SourceProbeReport report = await probe.ProbeAsync("  mdd000000001 ");

        Assert.Equal(Handler, report.HandlerId);
        Assert.All(client.RequestedUris, uri => Assert.Contains(Handler, uri, StringComparison.Ordinal));
    }

    /// <summary>
    /// The version EPA marks as its current record is the one fetched, even when another is newer.
    /// </summary>
    /// <remarks>
    /// It is the row a regulator reads, the row <c>dbo.vwHandlerSource</c> exposes, and therefore the row an
    /// operator comparing this output against EPA's screen will be looking at. Newer-but-not-current is the
    /// ordinary shape of a lineage, so a probe that took the newest would usually fetch a row nobody is asking
    /// about.
    /// </remarks>
    [Fact]
    public async Task TheVersionEpaMarksCurrentIsTheOneFetchedEvenWhenAnotherIsNewer()
    {
        (SourceProbe probe, StubDataClient client) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":2,"currentRecord":true,
              "receivedDate":"2019-11-01"},
             {"handlerId":"MDD000000001","sourceType":"N","sequence":3,"currentRecord":false,
              "receivedDate":"2024-06-30"}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal("N/2", report.Version);
        Assert.Equal($"{RcraInfoDataRequest.SourcesPath}/{Handler}/N/2", client.RequestedUris[1]);
        Assert.Equal("the version EPA marks as its current record", report.ChosenBecause);
    }

    /// <summary>
    /// When several versions claim the marker, the tie-break is script 521's and the report says so.
    /// </summary>
    /// <remarks>
    /// <b>409 pairs do claim it, and that is EPA's own contradiction rather than ours</b> — the user reproduced
    /// it in EPA's UI, selected a sequence, saved, and the surplus asterisks vanished. Highest
    /// <c>receivedDate</c> then highest sequence is what <c>521</c> stores after [R43], so this cannot disagree
    /// with the mirror. Note that <c>receivedDate</c> beats sequence here: the chosen version has the lower
    /// sequence of the two.
    /// </remarks>
    [Fact]
    public async Task SeveralVersionsClaimingTheMarkerAreSettledByScript521sTieBreak()
    {
        (SourceProbe probe, _) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":3,"currentRecord":true,
              "receivedDate":"2019-01-01"},
             {"handlerId":"MDD000000001","sourceType":"B","sequence":1,"currentRecord":true,
              "receivedDate":"2020-05-05"}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal("B/1", report.Version);
        Assert.Contains("script 521", report.ChosenBecause!, StringComparison.Ordinal);
        Assert.Contains("2 versions claim", report.ChosenBecause!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A handler with no current version anywhere is probed anyway, and the report says that is ordinary.
    /// </summary>
    /// <remarks>
    /// <b>[R44]'s finding, encoded as behaviour.</b> <c>currentRecord</c> is EPA's answer to "which version is
    /// this handler's live record", answered once per <i>handler</i> and not once per source type: 6,860 handlers
    /// have no current in their <c>N</c> lineage and every one of them is current under another type. A probe
    /// that refused a handler whose named versions happen not to carry the marker would refuse most of the state,
    /// and — worse — the refusal would read as a defect finding.
    /// </remarks>
    [Fact]
    public async Task AHandlerWithNoCurrentVersionIsProbedAndTheReportCallsItOrdinary()
    {
        (SourceProbe probe, _) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":false,
              "receivedDate":"2019-01-01"},
             {"handlerId":"MDD000000001","sourceType":"N","sequence":2,"currentRecord":false,
              "receivedDate":"2024-06-30"}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.True(report.IsAnswered);
        Assert.Equal("N/2", report.Version);
        Assert.Contains("ordinary rather than a defect", report.ChosenBecause!, StringComparison.Ordinal);
        Assert.Null(report.Problem);
    }

    /// <summary>
    /// A version with no readable receive date loses to one that has one, rather than winning by accident.
    /// </summary>
    /// <remarks>
    /// The same ordering <c>521</c> uses: <c>NULL receivedDate</c> sorts last under descending order. Note the
    /// dateless version carries the <i>higher</i> sequence, so a comparison that let a missing date sort first
    /// would pick it and the test would catch nothing if the sequences agreed.
    /// </remarks>
    [Fact]
    public async Task AVersionWithNoReceiveDateLosesToOneThatHasOne()
    {
        (SourceProbe probe, _) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":9,"currentRecord":false},
             {"handlerId":"MDD000000001","sourceType":"N","sequence":2,"currentRecord":false,
              "receivedDate":"2001-01-01"}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal("N/2", report.Version);
    }

    /// <summary>Sequence breaks a tie on receive date, which is exactly what 521 does after [R43].</summary>
    [Fact]
    public async Task SequenceBreaksATieOnReceiveDate()
    {
        (SourceProbe probe, _) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":2,"currentRecord":false,
              "receivedDate":"2020-03-09"},
             {"handlerId":"MDD000000001","sourceType":"N","sequence":5,"currentRecord":false,
              "receivedDate":"2020-03-09"}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal("N/5", report.Version);
    }

    /// <summary>
    /// Every version EPA named is listed with its marker, so a reader can re-run against a different one.
    /// </summary>
    /// <remarks>
    /// Printed because sequence numbers are scoped per source type and EPA's UI lists all types in one table,
    /// so the ordinals repeat: <c>MDD000796250</c> has four records all numbered 1, received in 1980, 1990, 2008
    /// and 2020. Without the source type beside the sequence, a reader cannot tell which row is which.
    /// </remarks>
    [Fact]
    public async Task EveryVersionEpaNamedIsListedWithItsMarker()
    {
        (SourceProbe probe, _) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":2,"currentRecord":false},
             {"handlerId":"MDD000000001","sourceType":"B","sequence":1,"currentRecord":true},
             {"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":false}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal(["B/1*", "N/1", "N/2"], report.VersionsAvailable);
        Assert.Contains("versions EPA names (3)", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A row for a different handler in the answer is ignored, and never fetched.
    /// </summary>
    /// <remarks>
    /// <c>CurrentRecordReconcile.FindForeignHandler</c>'s reasoning: a response that did not honour the one
    /// parameter it was given cannot be trusted to have honoured the rest of it. Here the foreign row is the
    /// newest, so a probe that did not filter would fetch a record belonging to somebody else and print it under
    /// this handler's name.
    /// </remarks>
    [Fact]
    public async Task ARowForADifferentHandlerIsIgnoredAndNeverFetched()
    {
        (SourceProbe probe, StubDataClient client) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":false,
              "receivedDate":"2001-01-01"},
             {"handlerId":"MDD999999999","sourceType":"N","sequence":7,"currentRecord":true,
              "receivedDate":"2026-01-01"}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal(["N/1"], report.VersionsAvailable);
        Assert.Equal($"{RcraInfoDataRequest.SourcesPath}/{Handler}/N/1", client.RequestedUris[1]);
        Assert.DoesNotContain("MDD999999999", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A version EPA named with a sequence of zero is reported rather than fetched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This protects the soft-delete path rather than the request.</b>
    /// <c>RcraInfoDataRequest.Source</c> throws on a non-positive sequence on purpose — a <c>0</c> means a value
    /// echoed from the summaries feed was corrupted in between, and the answer to a malformed detail path is a
    /// <c>404</c>, which the loader reads as "EPA no longer has this record". Filtering it here turns that into a
    /// report instead of a throw out of a diagnostic.
    /// </para>
    /// <para>
    /// <c>SummaryPayload</c> deliberately does <i>not</i> refuse a zero — it refuses only a negative, because
    /// EPA's sequences are merely <i>observed</i> to start at 1 and refusing a whole window over a surprising
    /// value is the wrong trade. So the zero reaches this stage, and this stage is where it stops.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AVersionWithASequenceOfZeroIsReportedRatherThanFetched()
    {
        (SourceProbe probe, StubDataClient client) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":0,"currentRecord":true}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Single(client.RequestedUris);
        Assert.False(report.IsAnswered);
        Assert.Contains("1 row(s)", report.Problem!, StringComparison.Ordinal);
        Assert.Contains("Nothing was fetched", report.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A version list EPA sent with no source type is refused by the payload reader, by element index.
    /// </summary>
    /// <remarks>
    /// The refusal belongs to <c>SummaryPayload</c> and is passed through unchanged, which is the point: the
    /// probe must not paraphrase a diagnosed problem into a vaguer one of its own. An element index is enough to
    /// find the row in a body an operator can fetch again, and no value from the body is quoted (AR8).
    /// </remarks>
    [Fact]
    public async Task AVersionListWithNoSourceTypeIsRefusedByElementIndex()
    {
        (SourceProbe probe, StubDataClient client) = Build(
            summaries:
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":1},
             {"handlerId":"MDD000000001","sourceType":null,"sequence":2}]
            """);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Single(client.RequestedUris);
        Assert.False(report.IsAnswered);
        Assert.Contains("element 1 of 2", report.Problem!, StringComparison.Ordinal);
        Assert.Contains("no sourceType", report.Problem!, StringComparison.Ordinal);
    }

    /// <summary>An empty version list is reported, and nothing is fetched.</summary>
    /// <remarks>
    /// The likeliest live result of a mistyped identifier. It must read as "EPA named no versions", never as a
    /// statement that a record was withdrawn.
    /// </remarks>
    [Fact]
    public async Task AnEmptyVersionListIsReportedAndNothingIsFetched()
    {
        (SourceProbe probe, StubDataClient client) = Build(summaries: "[]");

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Single(client.RequestedUris);
        Assert.False(report.IsAnswered);
        Assert.Empty(report.VersionsAvailable);
        Assert.Null(report.Version);
        Assert.DoesNotContain("delete", report.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A refused version list is a report, not an exception, and nothing is fetched.</summary>
    [Fact]
    public async Task ARefusedVersionListIsAReportAndNotAnException()
    {
        (SourceProbe probe, StubDataClient client) = Build(
            answer: request => Journalled.LookupFailure(request, ApiFetchOutcome.AccessDenied, 403));

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Single(client.RequestedUris);
        Assert.False(report.IsAnswered);
        Assert.Equal(403, report.SummariesHttpStatusCode);
        Assert.Null(report.SourceHttpStatusCode);
        Assert.Contains("AccessDenied", report.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A documented 404 on the version list carries no deletion language, exactly as it does for the walk.
    /// </summary>
    /// <remarks>
    /// The probe writes nothing, so it could not soft-delete anything even if it read the outcome the other way.
    /// It is asserted anyway: this is another reader of that outcome on this endpoint, and the day they disagree
    /// is the day somebody reasons about the loader from the probe's output.
    /// </remarks>
    [Fact]
    public async Task ADocumentedNotFoundCarriesNoDeletionLanguage()
    {
        (SourceProbe probe, _) = Build(
            answer: request => Journalled.LookupFailure(request, ApiFetchOutcome.NotFound, 404));

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal(404, report.SummariesHttpStatusCode);
        Assert.DoesNotContain("delete", report.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("withdrawn", report.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refused record fetch keeps the version list, and says the failure is about the one record.
    /// </summary>
    /// <remarks>
    /// <b>The distinction is the whole value of the second half of this report.</b> A failure after a readable
    /// version list means the handler exists and one version could not be had — which is a different errand from
    /// a handler EPA does not know, and the operator's next step differs accordingly. The version list survives
    /// so they can re-run against another version without querying anything.
    /// </remarks>
    [Fact]
    public async Task ARefusedRecordFetchKeepsTheVersionListAndSaysItIsAboutTheOneRecord()
    {
        (SourceProbe probe, StubDataClient client) = Build(
            answer: request => request.Endpoint == RcraInfoDataEndpoint.Summaries
                ? Journalled.LookupResult(request, OneCurrentVersion)
                : Journalled.LookupFailure(request, ApiFetchOutcome.NotFound, 404));

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal(2, client.RequestedUris.Count);
        Assert.False(report.IsAnswered);
        Assert.Equal(200, report.SummariesHttpStatusCode);
        Assert.Equal(404, report.SourceHttpStatusCode);
        Assert.Equal(["N/1*"], report.VersionsAvailable);
        Assert.Equal("N/1", report.Version);
        Assert.Contains("the handler exists", report.Problem!, StringComparison.Ordinal);
        Assert.NotEmpty(report.SummariesRawDates);
        Assert.Empty(report.SourceRawDates);
        Assert.Empty(report.Disagreements);
    }

    /// <summary>An unreadable version list is reported with the payload's own problem, and not quoted.</summary>
    [Fact]
    public async Task AnUnreadableVersionListIsReportedWithoutQuotingIt()
    {
        (SourceProbe probe, StubDataClient client) = Build(
            summaries: """{"unexpected":"object root"}""");

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Single(client.RequestedUris);
        Assert.False(report.IsAnswered);
        Assert.NotNull(report.Problem);
        Assert.DoesNotContain("object root", report.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// EPA's date text from the record body is reported verbatim — the open half of G25.
    /// </summary>
    /// <remarks>
    /// <b>The assertion the whole mode exists for.</b> The canned record below carries a <c>+0000</c> offset on
    /// <c>createdDate</c> and a bare date on <c>updatedDate</c>, and both must survive to the report. Anything
    /// that re-rendered them through <c>DateOnly</c> would print two identical values, G25 would be answered
    /// wrongly, and there would be no way to tell afterwards: no column retains a raw body.
    /// </remarks>
    [Fact]
    public async Task EpasDateTextFromTheRecordBodyIsReportedVerbatim()
    {
        (SourceProbe probe, _) = Build(source: RecordThatDisagrees);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.True(report.IsAnswered);
        Assert.Equal(["\"2003-07-03T00:00:00.000+0000\""], report.SourceRawDates["$.createdDate"]);
        Assert.Equal(["\"2019-11-14\""], report.SourceRawDates["$.updatedDate"]);
        Assert.Equal(["\"2019-11-14T09:30:00.000+0000\""], report.SourceRawDates["$.contact[].updatedDate"]);
    }

    /// <summary>
    /// Every field the two feeds write differently is named, and the record-detail block is printed first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two findings out of one body, and the second is the interesting one. <c>createdDate</c> differs at the
    /// root — a <c>+0000</c> offset on one feed and a bare date on the other, which is [R33]'s exact shape.
    /// <c>updatedDate</c> is <i>identical</i> at the root and differs only on the contact collection, and it is
    /// named anyway: the comparison is on the leaf name and takes the union of every path it appears at, because
    /// the two feeds have no paths in common to compare. A field whose shape depends on where in the payload it
    /// sits is a finding worth surfacing, not one worth averaging away.
    /// </para>
    /// <para>
    /// The block order is deliberate: the record detail is the half G25 has open and the half that cannot be
    /// recovered later, so it must not sit below a block that re-states an answer already written down in [R37].
    /// </para>
    /// <para>
    /// <b>[R46] <c>receivedDate</c> is the control, and it is the reason this test now earns its keep.</b> It
    /// holds a different value in the same shape on the two sides, and it must not be named — which is exactly
    /// what the first live run got wrong, three times, on a handler where nothing differed at all. Each entry
    /// carries both shapes so the claim is checkable from the line that makes it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryFieldTheTwoFeedsWriteDifferentlyIsNamedAndTheOpenHalfIsPrintedFirst()
    {
        (SourceProbe probe, _) = Build(source: RecordThatDisagrees);

        SourceProbeReport report = await probe.ProbeAsync(Handler);
        string printed = report.ToString();

        Assert.Equal(
            [
                "createdDate: \"yyyy-MM-ddTHH:mm:ss.fff+ZZZZ\" vs \"yyyy-MM-dd\"",
                "updatedDate: \"yyyy-MM-dd\" or \"yyyy-MM-ddTHH:mm:ss.fff+ZZZZ\" vs \"yyyy-MM-dd\"",
            ],
            report.Disagreements);

        Assert.Contains("2 date field(s) DIFFER", printed, StringComparison.Ordinal);
        Assert.Contains("record detail vs summaries", printed, StringComparison.Ordinal);
        Assert.Contains("[R33]", printed, StringComparison.Ordinal);

        Assert.True(
            printed.IndexOf("THE OPEN HALF OF G25", StringComparison.Ordinal)
                < printed.IndexOf("answered in [R37]", StringComparison.Ordinal),
            "The record-detail block must be printed before the summaries block.");
    }

    /// <summary>
    /// Two feeds that agree are said to agree, rather than left as a silence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty list is the expected result, and it is a finding either way — it is what would let [R37]'s answer
    /// be carried over to the record detail. Printing nothing would leave a reader unable to tell agreement from
    /// a comparison that never ran.
    /// </para>
    /// <para>
    /// <b>[R46] This is the live case, and it is the regression test for the false positive.</b> Not one date
    /// value is shared between the two fixtures, because the two feeds are asked different questions — the
    /// summaries call returns every version of the handler and the record call returns one — so on real data the
    /// value sets are a superset and a subset and never equal. `MD0570024000` printed three fields as differing
    /// when all of them were bare <c>"yyyy-MM-dd"</c>. Agreement here means agreement of shape.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoFeedsThatAgreeAreSaidToAgreeRatherThanLeftAsASilence()
    {
        (SourceProbe probe, _) = Build(source: RecordThatAgrees);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        // Guards the premise: if a later edit makes the fixtures share their values again, this test stops
        // testing anything and the assertion below would pass for the wrong reason.
        Assert.NotEqual(
            report.SourceRawDates["$.createdDate"],
            report.SummariesRawDates["$[].createdDate"]);

        Assert.Empty(report.Disagreements);
        Assert.Contains("in the same shape", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A record body with no date-shaped property says so, rather than printing an absence.</summary>
    [Fact]
    public async Task ARecordBodyWithNoDateShapedPropertySaysSo()
    {
        (SourceProbe probe, _) = Build(source: """{"handlerId":"MDD000000001","handlerName":"A Site"}""");

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.True(report.IsAnswered);
        Assert.Empty(report.SourceRawDates);
        Assert.Contains("no date-shaped property", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The output carries no URI, no query string, no credential and no <c>FailureMessage</c>.
    /// </summary>
    /// <remarks>
    /// <b>AR8, and the <c>FailureMessage</c> half is the one that needs asserting.</b> An
    /// <c>HttpRequestException</c>'s message can carry the request URI, and the credential travels in the URI on
    /// the auth call. This report is printed to a console an operator may paste into a ticket, which is a wider
    /// audience than a log table rather than a narrower one — so the failure is reported as an outcome name and
    /// a status code, and the message it came with is discarded.
    /// </remarks>
    [Fact]
    public async Task TheOutputCarriesNoUriAndNoFailureMessage()
    {
        const string Leak =
            "GET https://rcrainfopreprod.epa.gov/rcrainfo/rest/api/v1/hd/sources/summaries"
            + "?handlerId=MDD000000001 failed (apiKey=SUPERSECRET)";

        (SourceProbe probe, _) = Build(
            answer: request => Journalled.LookupFailure(request, ApiFetchOutcome.Unreachable)
                with { FailureMessage = Leak });

        SourceProbeReport report = await probe.ProbeAsync(Handler);
        string printed = report.ToString();

        Assert.DoesNotContain("SUPERSECRET", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api/v1", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("handlerId=", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Leak, printed, StringComparison.Ordinal);

        // The handler identifier itself is not a secret and is expected -- it is what the report is about.
        Assert.Contains(Handler, printed, StringComparison.Ordinal);
    }

    /// <summary>
    /// No contact field can reach the output, because the selection rule is a name ending in <c>Date</c>.
    /// </summary>
    /// <remarks>
    /// Contact columns are PII: they are excluded from the grid, history and search projections and are reachable
    /// only through <c>dbo.uspGetHandlerSourceDetail</c>. This mode fetches the whole 377-field payload, so the
    /// only thing keeping a contact's name and email out of a console line is <c>RawDateScan</c>'s suffix rule.
    /// Asserted here as well as in <see cref="RawDateScanTests"/>, because this is the caller that hands it a
    /// body containing them.
    /// </remarks>
    [Fact]
    public async Task NoContactFieldCanReachTheOutput()
    {
        (SourceProbe probe, _) = Build(source: RecordThatDisagrees);

        string printed = (await probe.ProbeAsync(Handler)).ToString();

        Assert.DoesNotContain("Ada", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("Lovelace", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("410-555", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", printed, StringComparison.Ordinal);

        // SrcUpdatedBy identifies an EPA user and is excluded from every projection for that reason.
        Assert.DoesNotContain("EPAUSER", printed, StringComparison.Ordinal);
    }

    /// <summary>The measured sizes and durations are the ones the two calls came back with.</summary>
    /// <remarks>
    /// The record-detail size is worth having beside the summaries size: it is the per-version cost the initial
    /// load pays several hundred thousand times, and F2's sizing has never had a real number for it.
    /// </remarks>
    [Fact]
    public async Task TheMeasuredSizesAndDurationsAreTheOnesTheCallsReturned()
    {
        (SourceProbe probe, _) = Build(source: RecordThatAgrees);

        SourceProbeReport report = await probe.ProbeAsync(Handler);

        Assert.Equal(200, report.SummariesHttpStatusCode);
        Assert.Equal(200, report.SourceHttpStatusCode);
        Assert.Equal(OneCurrentVersion.Length, report.SummariesResponseBytes);
        Assert.Equal(RecordThatAgrees.Length, report.SourceResponseBytes);
        Assert.True(report.SummariesDurationMs > 0);
        Assert.True(report.SourceDurationMs > 0);
    }

    /// <summary>
    /// A blank handler identifier throws before anything is asked, because it would ask for the whole state.
    /// </summary>
    /// <remarks>
    /// A caller defect rather than an answer from EPA — the argument parser refuses it first, so reaching this
    /// means the parser was bypassed. A blank <c>handlerId</c> on the summaries feed does not fail: it asks for
    /// every handler EPA has.
    /// </remarks>
    /// <param name="handlerId">The identifier, such as it is.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ABlankIdentifierThrowsBeforeAnythingIsAsked(string? handlerId)
    {
        (SourceProbe probe, StubDataClient client) = Build();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => probe.ProbeAsync(handlerId!));

        Assert.Contains("no handler identifier", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.RequestedUris);
    }

    /// <summary>One current version, received once — the default version list.</summary>
    private const string OneCurrentVersion =
        """
        [{"handlerId":"MDD000000001","activityLocation":"MD","sourceType":"N","sequence":1,
          "currentRecord":true,"receivedDate":"2019-11-01","updatedDate":"2019-11-14",
          "createdDate":"2019-11-14"}]
        """;

    /// <summary>
    /// A record body whose <c>createdDate</c> is shaped differently from the summaries feed's, with contact
    /// fields present so AR8 has something real to keep out of the output.
    /// </summary>
    /// <remarks>
    /// <b>[R46] <c>receivedDate</c> holds a different value in the same shape</b>, and it is here to be a field
    /// the comparison must <i>not</i> name. Under the original value-based comparison it would have been named,
    /// which is the defect the first live run printed three times over.
    /// </remarks>
    private const string RecordThatDisagrees =
        """
        {"handlerId":"MDD000000001","sourceType":"N","sequence":1,"handlerName":"A Site",
         "createdDate":"2003-07-03T00:00:00.000+0000","updatedDate":"2019-11-14",
         "receivedDate":"2020-02-29",
         "createdBy":"EPAUSER","updatedBy":"EPAUSER",
         "contact":[{"firstName":"Ada","lastName":"Lovelace","phoneNumber":"410-555-0100",
                     "email":"ada@example.org","updatedDate":"2019-11-14T09:30:00.000+0000"}]}
        """;

    /// <summary>A record body that writes every shared date field in the same shape as the summaries feed.</summary>
    /// <remarks>
    /// <b>[R46] Every value here differs from the summaries fixture's, deliberately.</b> It used to repeat them
    /// exactly, which is why the whole file agreed that a value-based comparison worked: no fixture ever put a
    /// difference of value and a sameness of shape in the same place. These are `MD0570024000`/`B`/11's real
    /// dates, read off EPA on 2026-09-07 — the case that produced the false positive.
    /// </remarks>
    private const string RecordThatAgrees =
        """
        {"handlerId":"MDD000000001","sourceType":"N","sequence":1,
         "createdDate":"2026-06-12","updatedDate":"2026-06-12","receivedDate":"2026-05-28"}
        """;

    private static (SourceProbe Probe, StubDataClient Client) Build(
        string? summaries = null,
        string? source = null,
        Func<RcraInfoDataRequest, ApiFetchResult>? answer = null)
    {
        // Dispatched on the endpoint rather than on call order, because the whole point of this stage is that
        // the second request's key comes out of the first answer -- a stub that served bodies positionally
        // would pass even if the two were swapped.
        StubDataClient client = new(
            answer ?? (request => Journalled.LookupResult(
                request,
                request.Endpoint == RcraInfoDataEndpoint.Summaries
                    ? summaries ?? OneCurrentVersion
                    : source ?? RecordThatAgrees)));

        return (new SourceProbe(client), client);
    }
}
