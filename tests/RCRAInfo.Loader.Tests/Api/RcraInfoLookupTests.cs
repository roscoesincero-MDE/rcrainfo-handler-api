using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The 23 mirrored code lists (G15): the catalog, and the request factory that is the only thing standing
/// between "this endpoint has no <c>stateCode</c>" and a national response accepted as a Maryland one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure guarded here is silent.</b> A lookup endpoint documents no <c>400</c> and no
/// <c>404</c>, so a malformed lookup request cannot come back as a well-behaved refusal — it arrives as an
/// undocumented status or as a <c>200</c> carrying more than was asked for. And script 523 retires codes by
/// their absence from a successful payload, so a wrongly-scoped response is not merely stored: it
/// <i>retires</i> whatever it did not contain, within the activity locations it did.
/// </para>
/// <para>
/// The catalog's agreement with the pinned spec is asserted by <c>build/check_lookup_catalog.py</c> rather
/// than here. It is a hand-written list over a spec-derived truth, which is the shape the project has
/// already been bitten by (plan [R21]), and the spec is not a test fixture.
/// </para>
/// </remarks>
public class RcraInfoLookupTests
{
    [Fact]
    public void TheCatalogHolds23ListsAndNamesTheOneItLeavesOut()
    {
        // 24 endpoints, 23 mirrored. The 24th is /lookup/hd/naics-codes, which requires a `term`: it is a
        // SEARCH over the same Naics definition, not the list. Mirroring it would store whatever subset the
        // invented terms matched -- and then 523 would retire every code they missed.
        Assert.Equal(23, RcraInfoLookups.All.Count);
        Assert.Equal("naics-codes", RcraInfoLookups.ExcludedPathSegment);
        Assert.DoesNotContain(
            RcraInfoLookups.ExcludedPathSegment,
            RcraInfoLookups.All.Select(l => l.PathSegment));

        // The mirrorable NAICS list is the parameterless one.
        Assert.Equal("naics", RcraInfoLookups.ByLookupName("Naics").PathSegment);
    }

    [Fact]
    public void EveryNameAndEverySegmentIsDistinct()
    {
        // A duplicated name would refresh one table twice and leave another never refreshed -- and "never
        // refreshed" is indistinguishable from "EPA published nothing new", so it reports as success.
        Assert.Equal(23, RcraInfoLookups.All.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            23,
            RcraInfoLookups.All.Select(l => l.PathSegment).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SevenEndpointsTakeAStateCodeAndFiveOfThemDemandOne()
    {
        Assert.Equal(7, RcraInfoLookups.All.Count(l => l.TakesStateCode));
        Assert.Equal(5, RcraInfoLookups.All.Count(l => l.StateCode == LookupStateCode.Required));
        Assert.Equal(2, RcraInfoLookups.All.Count(l => l.StateCode == LookupStateCode.Optional));
        Assert.Equal(16, RcraInfoLookups.All.Count(l => l.StateCode == LookupStateCode.Unsupported));
    }

    [Theory]
    [InlineData("ContactType", "contact-types")]
    [InlineData("County", "counties")]
    [InlineData("StateActivity", "state-activities")]
    [InlineData("StateDistrict", "state-districts")]
    [InlineData("UniversalWaste", "universal-waste-codes")]
    public void TheFiveThatRequireAStateCodeAreTheseFive(string name, string segment)
    {
        RcraInfoLookup lookup = RcraInfoLookups.ByLookupName(name);

        Assert.Equal(segment, lookup.PathSegment);
        Assert.Equal(LookupStateCode.Required, lookup.StateCode);
    }

    [Fact]
    public void HandlerSourceTypeIsServedByAnEndpointNamedNothingLikeIt()
    {
        // /lookup/hd/submittal-reasons returns the HandlerSourceType definition. Nothing in either name
        // suggests the other; dbo.LookupHandlerSourceType's own MS_Description records the same pairing,
        // so the two sides agree by construction rather than by memory.
        Assert.Equal("submittal-reasons", RcraInfoLookups.ByLookupName("HandlerSourceType").PathSegment);
        Assert.Equal(
            LookupStateCode.Unsupported,
            RcraInfoLookups.ByLookupName("HandlerSourceType").StateCode);
    }

    [Fact]
    public void AnUnknownLookupNameIsRefusedAndTheMessageListsTheClosedSet()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RcraInfoLookups.ByLookupName("ContactTypes"));

        // 523 refuses an unknown @LookupName the same way and lists the same 23. The message is the fix.
        Assert.Contains("ContactType", error.Message, StringComparison.Ordinal);
        Assert.Contains("523", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNameIsMatchedCaseSensitivelyBecause523sRefusalMessageSpellsItOneWay()
    {
        Assert.Throws<ArgumentException>(() => RcraInfoLookups.ByLookupName("contacttype"));
    }

    // ---- The request factory ------------------------------------------------------------------------

    [Fact]
    public void ALookupPathIsTheSharedPrefixPlusTheSegmentAndNothingElse()
    {
        RcraInfoDataRequest request = RcraInfoDataRequest.Lookup(
            RcraInfoLookups.ByLookupName("Naics"),
            null);

        Assert.Equal("api/v1/lookup/hd/naics", request.Path);
        Assert.Equal("api/v1/lookup/hd/naics", request.RelativeUri);
        Assert.Equal("/api/v1/lookup/hd/naics", request.LogPath);
        Assert.Equal(RcraInfoDataEndpoint.Lookup, request.Endpoint);
    }

    [Fact]
    public void TheStateCodeGoesInTheQueryStringUnderEpasNameAndNeverIntoTheLoggedPath()
    {
        // The query parameter is `stateCode`; the payload property is `activityLocation`. Same value, two
        // names, and this factory is the only place the request form is composed.
        RcraInfoDataRequest request = RcraInfoDataRequest.Lookup(
            RcraInfoLookups.ByLookupName("County"),
            "MD");

        Assert.Equal("api/v1/lookup/hd/counties?stateCode=MD", request.RelativeUri);
        Assert.Equal("api/v1/lookup/hd/counties", request.Path);
        Assert.DoesNotContain("stateCode", request.LogPath, StringComparison.Ordinal);
        Assert.DoesNotContain("activityLocation", request.RelativeUri, StringComparison.Ordinal);
    }

    [Fact]
    public void AStateCodeOfferedToAnEndpointThatHasNoSuchParameterIsRefusedRatherThanDropped()
    {
        // The reason this throws instead of being ignored: nine of the sixteen unscoped endpoints return a
        // REQUIRED activityLocation on every element, so it is entirely reasonable to assume the request
        // can be scoped too. If the parameter were quietly dropped, the caller would treat a national
        // response as a Maryland one -- and script 523 retires by what the payload names, so nothing would
        // ever contradict that.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RcraInfoDataRequest.Lookup(RcraInfoLookups.ByLookupName("Country"), "MD"));

        Assert.Contains("no stateCode parameter", error.Message, StringComparison.Ordinal);
        Assert.Contains("523", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEndpointThatNeedsAStateCodeRefusesToBeCalledWithoutOne(string? activityLocation)
    {
        // No /lookup/hd endpoint documents a 400, so omitting a required parameter cannot come back as a
        // clean refusal: it is an undocumented status, or a 200 carrying every jurisdiction.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => RcraInfoDataRequest.Lookup(
                RcraInfoLookups.ByLookupName("StateDistrict"),
                activityLocation));

        Assert.Contains("stateCode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionalStateCodeIsSentAnyway()
    {
        // Two lists accept it optionally. Sending it makes all seven stateCode-capable endpoints behave the
        // same way -- and the failure mode of an inconsistent choice is worse than a smaller response: a
        // jurisdiction's codes fetched once nationally and then never named again by a scoped payload are
        // stored, never refreshed and never retired, permanently.
        Assert.Equal(
            "api/v1/lookup/hd/waste-codes?stateCode=MD",
            RcraInfoDataRequest.Lookup(RcraInfoLookups.ByLookupName("WasteCode"), "MD").RelativeUri);
        Assert.Equal(
            "api/v1/lookup/hd/generator-categories?stateCode=MD",
            RcraInfoDataRequest.Lookup(RcraInfoLookups.ByLookupName("GeneratorCategory"), "MD").RelativeUri);
    }

    [Fact]
    public void TheFederalFlagIsNeverSentOnTheTwoEndpointsThatAcceptIt()
    {
        // generator-categories and waste-codes take an optional `federal` boolean -- "return only the
        // federal codes". Sending it would answer with a strict subset, and 523 retires every code a
        // successful payload did not contain: every state-specific code in those two lists, soft deleted,
        // by a run that reported success. There is no overload that can send it; this asserts the request
        // it does build carries nothing but the stateCode.
        Assert.Equal(
            "api/v1/lookup/hd/generator-categories?stateCode=MD",
            RcraInfoDataRequest.Lookup(RcraInfoLookups.ByLookupName("GeneratorCategory"), "MD").RelativeUri);

        foreach (RcraInfoLookup lookup in RcraInfoLookups.All)
        {
            string uri = RcraInfoDataRequest
                .Lookup(lookup, lookup.TakesStateCode ? "MD" : null)
                .RelativeUri;

            Assert.DoesNotContain("federal", uri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('&', uri);
        }
    }

    [Theory]
    [InlineData("M")]
    [InlineData("MDX")]
    [InlineData("M1")]
    public void AStateCodeThatCannotBeAnActivityLocationIsRefused(string activityLocation)
    {
        Assert.Throws<ArgumentException>(
            () => RcraInfoDataRequest.Lookup(
                RcraInfoLookups.ByLookupName("ContactType"),
                activityLocation));
    }

    [Fact]
    public void TheStateCodeIsTrimmedAndUpperCasedLikeEveryOtherActivityLocation()
    {
        Assert.Equal(
            "api/v1/lookup/hd/counties?stateCode=MD",
            RcraInfoDataRequest.Lookup(RcraInfoLookups.ByLookupName("County"), " md ").RelativeUri);
    }

    [Fact]
    public void ANullLookupIsRefusedRatherThanProducingThePrefixAlone()
    {
        // api/v1/lookup/hd/ with no segment is not a 404 waiting to happen -- it is a request whose
        // classification would then depend on an endpoint set it does not belong to.
        Assert.Throws<ArgumentNullException>(() => RcraInfoDataRequest.Lookup(null!, "MD"));
    }

    // ---- Classification ----------------------------------------------------------------------------

    [Fact]
    public void ALookupDocumentsNeither404Nor400SoNeitherCanDeleteAnything()
    {
        RcraInfoDataRequest request = RcraInfoDataRequest.Lookup(
            RcraInfoLookups.ByLookupName("State"),
            null);

        // The load-bearing assertion of this file. ApiFetchOutcome.NotFound feeds
        // dbo.uspSoftDeleteHandlerSourceSet; a lookup must never reach it.
        Assert.False(request.DocumentsNotFound);
        Assert.False(request.IsDocumented(404));
        Assert.False(request.IsDocumented(400));

        Assert.True(request.IsDocumented(200));
        Assert.True(request.IsDocumented(401));
        Assert.True(request.IsDocumented(403));
        Assert.True(request.IsDocumented(500));
    }

    [Fact]
    public void EveryLookupSharesOneDocumentedStatusSet()
    {
        // All 24 /lookup/hd paths document 200, 401, 403, 500 -- identically. That is what lets one enum
        // member serve 23 lists, and it is asserted rather than assumed because the enum member is what
        // decides whether a 404 deletes data.
        foreach (RcraInfoLookup lookup in RcraInfoLookups.All)
        {
            RcraInfoDataRequest request = RcraInfoDataRequest.Lookup(
                lookup,
                lookup.TakesStateCode ? "MD" : null);

            Assert.False(request.DocumentsNotFound);
            Assert.False(request.IsDocumented(400));
            Assert.True(request.IsDocumented(200));
        }
    }
}
