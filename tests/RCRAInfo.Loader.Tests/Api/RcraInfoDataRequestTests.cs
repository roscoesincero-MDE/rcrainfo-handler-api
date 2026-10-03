using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The request factories exist to make three specific mistakes impossible: a query string in a logged
/// path, an unscoped summaries call, and a <c>404</c> read as a deletion when it is a wrong URL.
/// </summary>
/// <remarks>
/// Every assertion here corresponds to a failure that would <b>not</b> announce itself. A blanked
/// <c>ActivityLocation</c> succeeds and returns the nation; a <c>404</c> from a mistyped path is
/// indistinguishable from an absent record unless the endpoint's documented status set says otherwise. The
/// tests are cheap; the failures they catch are diagnosed from data that is already wrong.
/// </remarks>
public class RcraInfoDataRequestTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly End = new(2026, 1, 31);

    // ---- Paths and logging (AR8) --------------------------------------------------------------------

    [Fact]
    public void NoFactoryEverPutsAQueryStringIntoThePath()
    {
        // logs.HandlerLoadAttempt.RequestPath is readable by the monitoring web application, and three of
        // the four call families carry an identifying argument in the query string. This is the one
        // invariant that holds across every factory, so it is asserted across every factory -- including
        // all 23 lookup lists, seven of which append a stateCode.
        RcraInfoDataRequest[] all =
        [
            RcraInfoDataRequest.Summaries("MD", Start, End),
            RcraInfoDataRequest.SummariesForHandler("MDD000000001"),
            RcraInfoDataRequest.Source("MDD000000001", "N", 1),
            RcraInfoDataRequest.OtherIds("MDD000000001"),
            .. RcraInfoLookups.All.Select(
                l => RcraInfoDataRequest.Lookup(l, l.TakesStateCode ? "MD" : null)),
        ];

        foreach (RcraInfoDataRequest request in all)
        {
            Assert.DoesNotContain('?', request.Path);
            Assert.DoesNotContain('&', request.Path);
            Assert.DoesNotContain('=', request.Path);
        }
    }

    [Fact]
    public void ToStringIsThePathAndNotTheUriThatWasSent()
    {
        RcraInfoDataRequest request = RcraInfoDataRequest.OtherIds("MDD000000001");

        // The day someone interpolates a request into a message, the safe half is what lands there.
        Assert.Equal(RcraInfoDataRequest.OtherIdsPath, request.ToString());
        Assert.DoesNotContain("MDD000000001", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHandlerIdIsInTheLoggedPathOnlyForTheDetailCall()
    {
        // Permitted, and asserted rather than assumed: a handler id is not a secret (script 506 says so
        // explicitly), and the detail endpoint's whole key is path segments. Nothing else may widen that.
        Assert.Contains(
            "MDD000000001",
            RcraInfoDataRequest.Source("MDD000000001", "N", 1).Path,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "MDD000000001",
            RcraInfoDataRequest.SummariesForHandler("MDD000000001").Path,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetailCallIsTheOneRequestWhosePathAndUriAreTheSame()
    {
        RcraInfoDataRequest request = RcraInfoDataRequest.Source("MDD000000001", "N", 3);

        Assert.Equal(request.Path, request.RelativeUri);
        Assert.Equal("api/v1/hd/sources/MDD000000001/N/3", request.Path);
    }

    // ---- The documented status sets ------------------------------------------------------------------

    [Fact]
    public void OtherIdsDocumentsNoNotFoundAndTheOtherTwoDo()
    {
        // The single most consequential assertion in this file. DocumentsNotFound is what decides whether a
        // 404 becomes ApiFetchOutcome.NotFound and feeds dbo.uspSoftDeleteHandlerSourceSet, or becomes
        // Unexpected and deletes nothing. The plan carried the wrong other-ids path form until 2026-09-06;
        // had it shipped, every handler would have answered 404 and every handler would have been soft
        // deleted. This line is why that would have been reported as a client defect instead.
        Assert.False(RcraInfoDataRequest.OtherIds("MDD000000001").DocumentsNotFound);

        Assert.True(RcraInfoDataRequest.Summaries("MD", Start, End).DocumentsNotFound);
        Assert.True(RcraInfoDataRequest.Source("MDD000000001", "N", 1).DocumentsNotFound);
    }

    [Fact]
    public void TheDetailEndpointDocumentsNoBadRequestAndSummariesDoes()
    {
        Assert.False(RcraInfoDataRequest.Source("MDD000000001", "N", 1).IsDocumented(400));
        Assert.True(RcraInfoDataRequest.Summaries("MD", Start, End).IsDocumented(400));
        Assert.True(RcraInfoDataRequest.OtherIds("MDD000000001").IsDocumented(400));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public void EveryEndpointDocumentsTheFourStatusesTheyShare(int status)
    {
        Assert.True(RcraInfoDataRequest.Summaries("MD", Start, End).IsDocumented(status));
        Assert.True(RcraInfoDataRequest.Source("MDD000000001", "N", 1).IsDocumented(status));
        Assert.True(RcraInfoDataRequest.OtherIds("MDD000000001").IsDocumented(status));
    }

    [Theory]
    [InlineData(204)]
    [InlineData(302)]
    [InlineData(429)]
    [InlineData(503)]
    public void StatusesTheSpecDoesNotListAreUndocumentedEverywhere(int status)
    {
        // 429 and 503 are included on purpose. Neither is in the pinned spec for any of the three data
        // endpoints, and both are entirely plausible from a real gateway -- so "undocumented" cannot mean
        // "impossible" to the classifier. It means the spec did not promise it, which is a fact worth
        // logging and not a reason to treat the answer as data.
        Assert.False(RcraInfoDataRequest.Summaries("MD", Start, End).IsDocumented(status));
        Assert.False(RcraInfoDataRequest.Source("MDD000000001", "N", 1).IsDocumented(status));
        Assert.False(RcraInfoDataRequest.OtherIds("MDD000000001").IsDocumented(status));
    }

    [Fact]
    public void BothSummariesFormsClassifyAsTheSameEndpoint()
    {
        // Two factories, one endpoint: the status set belongs to the operation, not to the argument list.
        Assert.Equal(
            RcraInfoDataEndpoint.Summaries,
            RcraInfoDataRequest.Summaries("MD", Start, End).Endpoint);

        Assert.Equal(
            RcraInfoDataEndpoint.Summaries,
            RcraInfoDataRequest.SummariesForHandler("MDD000000001").Endpoint);
    }

    // ---- Summaries ----------------------------------------------------------------------------------

    [Fact]
    public void SummariesSendsTheLocationAndBothDatesInIsoForm()
    {
        RcraInfoDataRequest request = RcraInfoDataRequest.Summaries("MD", Start, End);

        Assert.Equal(RcraInfoDataRequest.SummariesPath, request.Path);
        Assert.Equal(
            "api/v1/hd/sources/summaries?activityLocation=MD&startDate=2026-01-01&endDate=2026-01-31",
            request.RelativeUri);
    }

    [Fact]
    public void TheEndDateIsSentEvenWhenTheWindowIsASingleDay()
    {
        // EPA defaults endDate to today when it is omitted, which would make a window's end depend on when
        // the request was made -- a load starting at 23:55 and continuing past midnight would ask for two
        // different windows, and neither would be the one the watermark records.
        RcraInfoDataRequest request = RcraInfoDataRequest.Summaries("MD", Start, Start);

        Assert.Contains("startDate=2026-01-01", request.RelativeUri, StringComparison.Ordinal);
        Assert.Contains("endDate=2026-01-01", request.RelativeUri, StringComparison.Ordinal);
    }

    [Fact]
    public void DatesAreFormattedInvariantlyAndNotByTheMachinesCulture()
    {
        // A workstation set to a culture with a different short-date pattern would otherwise send
        // 01/31/2026 -- which EPA answers with a 400 in the best case, and this project's target servers do
        // not all share a locale with the developer's.
        RcraInfoDataRequest request =
            RcraInfoDataRequest.Summaries("MD", new DateOnly(2026, 12, 31), new DateOnly(2026, 12, 31));

        Assert.Contains("startDate=2026-12-31", request.RelativeUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankActivityLocationIsRefusedAndTheMessageSaysWhy(string activityLocation)
    {
        // Not a 400 from EPA: every parameter on this endpoint is required:false, so an empty query string
        // is a legal request for every handler in the country. It succeeds. The failure mode is a mirror
        // full of out-of-scope regulated entities and a run that reported success.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RcraInfoDataRequest.Summaries(activityLocation, Start, End));

        Assert.Equal("activityLocation", error.ParamName);
        Assert.Contains("country", error.Message, StringComparison.Ordinal);
        Assert.Contains("ActivityLocation", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("M")]
    [InlineData("MDX")]
    [InlineData("24")]
    [InlineData("M1")]
    public void AnActivityLocationThatIsNotTwoLettersIsRefused(string activityLocation)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RcraInfoDataRequest.Summaries(activityLocation, Start, End));

        Assert.Equal("activityLocation", error.ParamName);
    }

    [Theory]
    [InlineData("md")]
    [InlineData(" MD ")]
    [InlineData("Md")]
    public void AnActivityLocationIsTrimmedAndUpperCasedRatherThanRefused(string activityLocation)
    {
        // Case and stray whitespace in a config file are an editing accident, not a scope error, and EPA's
        // examples are upper case. Normalising is safe here in a way that guessing the value would not be.
        Assert.Contains(
            "activityLocation=MD",
            RcraInfoDataRequest.Summaries(activityLocation, Start, End).RelativeUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyMarylandIsNotEnforcedBecauseScopeIsConfiguration()
    {
        // Deliberate: G2 says MD, and G2 is a configuration decision. Hard-coding it here would put the
        // same value in two places and would make a lawful expansion of scope a code change. What the
        // factory rejects is a value that cannot be an activity location at all.
        Assert.Contains(
            "activityLocation=VA",
            RcraInfoDataRequest.Summaries("VA", Start, End).RelativeUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ABackwardsWindowIsRefusedBecauseItSucceedsEmpty()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RcraInfoDataRequest.Summaries("MD", End, Start));

        Assert.Equal("endDate", error.ParamName);

        // The message has to explain a refusal of something EPA would accept, so it names the consequence:
        // an empty 200 is indistinguishable from a quiet day and would advance the watermark over data
        // that was never fetched.
        Assert.Contains("watermark", error.Message, StringComparison.Ordinal);
        Assert.Contains("2026-01-31", error.Message, StringComparison.Ordinal);
        Assert.Contains("2026-01-01", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SummariesForHandlerSendsTheHandlerAndNoLocation()
    {
        // The spec describes the two forms as alternatives; a request satisfying both invites EPA to pick.
        RcraInfoDataRequest request = RcraInfoDataRequest.SummariesForHandler("MDD000000001");

        Assert.Equal("api/v1/hd/sources/summaries?handlerId=MDD000000001", request.RelativeUri);
        Assert.DoesNotContain("activityLocation", request.RelativeUri, StringComparison.Ordinal);
        Assert.DoesNotContain("startDate", request.RelativeUri, StringComparison.Ordinal);
    }

    // ---- Other ids ---------------------------------------------------------------------------------

    [Fact]
    public void OtherIdsUsesTheQueryParameterFormAndNotAPathSegment()
    {
        // The correction of 2026-09-06. handlerId is in:query, required:true; the only path-segment form in
        // the spec is the DELETE this application never calls. The wrong form matches no route at all.
        RcraInfoDataRequest request = RcraInfoDataRequest.OtherIds("MDD000000001");

        Assert.Equal("api/v1/hd/other-ids", request.Path);
        Assert.Equal("api/v1/hd/other-ids?handlerId=MDD000000001", request.RelativeUri);
        Assert.DoesNotContain("other-ids/MDD000000001", request.RelativeUri, StringComparison.Ordinal);
    }

    // ---- Handler ids and escaping ------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ABlankHandlerIdIsRefusedByEveryFactoryThatTakesOne(string handlerId)
    {
        Assert.Equal(
            "handlerId",
            Assert.Throws<ArgumentException>(
                () => RcraInfoDataRequest.SummariesForHandler(handlerId)).ParamName);

        Assert.Equal(
            "handlerId",
            Assert.Throws<ArgumentException>(
                () => RcraInfoDataRequest.OtherIds(handlerId)).ParamName);

        Assert.Equal(
            "handlerId",
            Assert.Throws<ArgumentException>(
                () => RcraInfoDataRequest.Source(handlerId, "N", 1)).ParamName);
    }

    [Fact]
    public void AHandlerIdIsTrimmedBecauseAFixedWidthFeedPadsIt()
    {
        Assert.Equal(
            "api/v1/hd/other-ids?handlerId=MDD000000001",
            RcraInfoDataRequest.OtherIds("  MDD000000001  ").RelativeUri);
    }

    [Fact]
    public void AHandlerIdIsEscapedInBothTheQueryAndThePath()
    {
        // Handler ids should be alphanumeric, and "should be" is not a guarantee about a value echoed from
        // a response. An unescaped separator would change which endpoint is called, not merely which
        // record -- and the answer to a request pointing somewhere else is a 404, which this client reads
        // as a deletion.
        Assert.Equal(
            "api/v1/hd/other-ids?handlerId=MD%2FD1",
            RcraInfoDataRequest.OtherIds("MD/D1").RelativeUri);

        Assert.Equal(
            "api/v1/hd/sources/MD%2FD1/N/1",
            RcraInfoDataRequest.Source("MD/D1", "N", 1).Path);
    }

    [Fact]
    public void TheSourceTypeIsEscapedBecauseItsAlphabetIsUnknown()
    {
        // G24: the spec documents sourceType only as "('N', 'I' etc)" and offers no lookup endpoint to
        // enumerate it, so nothing here may assume it is a single safe letter.
        Assert.Equal(
            "api/v1/hd/sources/MDD000000001/N%20X/1",
            RcraInfoDataRequest.Source("MDD000000001", "N X", 1).Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankSourceTypeIsRefusedBecauseItCollapsesThePath(string sourceType)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RcraInfoDataRequest.Source("MDD000000001", sourceType, 1));

        Assert.Equal("sourceType", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveSequenceIsRefusedBecauseTheAnswerWouldBeASoftDelete(int sequence)
    {
        // These three values are echoed from the summaries feed, so a 0 here means this client corrupted
        // them in between. A throw is the right answer to a key this client invented; a 404 is not.
        Assert.Equal(
            "sequence",
            Assert.Throws<ArgumentOutOfRangeException>(
                () => RcraInfoDataRequest.Source("MDD000000001", "N", sequence)).ParamName);
    }

    [Fact]
    public void TheSequenceIsRenderedInvariantlyAndWithoutSeparators()
    {
        Assert.Equal(
            "api/v1/hd/sources/MDD000000001/N/1234567",
            RcraInfoDataRequest.Source("MDD000000001", "N", 1_234_567).Path);
    }

    // ---- The constants -----------------------------------------------------------------------------

    // ---- LogPath, the third form -------------------------------------------------------------------

    [Fact]
    public void TheLoggedPathLeadsWithASlashAndTheSentPathDoesNot()
    {
        // Two correct rules pulling opposite ways. Script 524 accepts RequestPath only if it starts with
        // '/' and REPLACES THE WHOLE VALUE when it does not -- so handing it Path would not throw, it would
        // fill the attempt log with withheld notices and report a defect count for a load that worked.
        RcraInfoDataRequest request = RcraInfoDataRequest.OtherIds("MDD000000001");

        Assert.Equal("/api/v1/hd/other-ids", request.LogPath);
        Assert.Equal("api/v1/hd/other-ids", request.Path);
    }

    [Fact]
    public void TheLoggedPathPassesEveryOneOfScript524sChecks()
    {
        // The procedure's rule, transcribed: a leading '/', and none of '?', '#', '://', '@' or '/auth/'.
        // Asserted against every factory, because the check that matters is that no request can produce a
        // value 524 would withhold.
        RcraInfoDataRequest[] all =
        [
            RcraInfoDataRequest.Summaries("MD", Start, End),
            RcraInfoDataRequest.SummariesForHandler("MDD000000001"),
            RcraInfoDataRequest.Source("MDD000000001", "N", 1),
            RcraInfoDataRequest.OtherIds("MDD000000001"),
        ];

        foreach (RcraInfoDataRequest request in all)
        {
            string logPath = request.LogPath;

            Assert.StartsWith("/", logPath, StringComparison.Ordinal);
            Assert.DoesNotContain('?', logPath);
            Assert.DoesNotContain('#', logPath);
            Assert.DoesNotContain("://", logPath, StringComparison.Ordinal);
            Assert.DoesNotContain('@', logPath);
            Assert.DoesNotContain("/auth/", logPath, StringComparison.Ordinal);

            // NVARCHAR (400): 524 replaces anything longer with a notice stating the length.
            Assert.True(logPath.Length <= 400);
        }
    }

    [Fact]
    public void ThePathConstantsAreRelativeSoTheyResolveAgainstTheConfiguredBase()
    {
        // A leading slash would resolve against the host and drop /rcra-api/rest, sending every data call
        // to a path EPA does not serve -- the same failure RcraInfoApiOptions' trailing slash prevents from
        // the other end.
        foreach (string path in new[]
        {
            RcraInfoDataRequest.SummariesPath,
            RcraInfoDataRequest.OtherIdsPath,
            RcraInfoDataRequest.SourcesPath,
        })
        {
            Assert.False(path.StartsWith('/'));
            Assert.StartsWith("api/v1/hd/", path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ARequestResolvesAgainstThePreprodBaseToTheUrlEpaDocuments()
    {
        Uri baseUri = new("https://rcranodepreprod.epa.gov/rcra-api/rest/");

        Assert.Equal(
            "https://rcranodepreprod.epa.gov/rcra-api/rest/api/v1/hd/other-ids?handlerId=MDD000000001",
            new Uri(baseUri, RcraInfoDataRequest.OtherIds("MDD000000001").RelativeUri).AbsoluteUri);
    }
}
