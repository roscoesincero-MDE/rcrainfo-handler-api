using RCRAInfo.Data.Payloads;

using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The only deserializer of a RCRAInfo response in the solution, and the only thing between EPA's JSON and a
/// <c>Full</c>-mode refresh that retires what it did not see.
/// </summary>
/// <remarks>
/// Every rejection here has the same alternative: pass the payload on and let script 523 decide. The reason
/// not to is that 523's decision is a <c>MERGE</c> — an element it cannot key on is an element absent from
/// the payload, which in <c>Full</c> mode is a retirement.
/// </remarks>
public class LookupPayloadTests
{
    [Fact]
    public void EpasOwnCamelCasePropertyNamesBindToEveryPropertyLookupElementHas()
    {
        // Every property name below is copied from a /lookup/hd definition in the pinned spec: the union of
        // all 23 is exactly these eleven, and build/check_lookup_catalog.py holds that against the spec.
        const string payload = """
            [{
              "activityLocation": "MD",
              "code": "D001",
              "description": "Ignitable waste",
              "active": true,
              "sortOrder": 3,
              "codeType": "F",
              "acute": false,
              "industryApp": true,
              "brLoadActive": true,
              "episodicType": { "activityLocation": "MD", "code": "E1", "description": "Spill" },
              "counties": [{ "activityLocation": "MD", "code": "003", "description": "Anne Arundel" }]
            }]
            """;

        LookupPayloadRead read = LookupPayload.Read(payload);

        Assert.True(read.IsReadable);
        Assert.Empty(read.UnexpectedProperties);

        LookupElement element = Assert.Single(read.Elements);
        Assert.Equal("MD", element.ActivityLocation);
        Assert.Equal("D001", element.Code);
        Assert.Equal("Ignitable waste", element.Description);
        Assert.True(element.Active);
        Assert.Equal(3, element.SortOrder);
        Assert.Equal("F", element.CodeType);
        Assert.False(element.Acute);
        Assert.True(element.IndustryApp);
        Assert.True(element.BrLoadActive);
        Assert.Equal("E1", element.EpisodicType?.Code);
        Assert.Equal("003", Assert.Single(element.Counties!).Code);
    }

    [Fact]
    public void APropertyEpaAddsIsReportedRatherThanDroppedAndTheCodesStillArrive()
    {
        // System.Text.Json ignores an unmapped member silently. For a code list that is the quiet kind of
        // wrong: every code still arrives, the refresh still succeeds, and whatever the new field says about
        // the codes is discarded on every run from now on.
        const string payload = """
            [{ "activityLocation": "MD", "code": "01", "federalOnly": true }]
            """;

        LookupPayloadRead read = LookupPayload.Read(payload);

        Assert.True(read.IsReadable);
        Assert.Equal("01", Assert.Single(read.Elements).Code);
        Assert.Equal(["federalOnly"], read.UnexpectedProperties);
    }

    [Fact]
    public void APropertyAddedInsideTheNestedShapesIsJustAsInvisibleAndIsAlsoReported()
    {
        const string payload = """
            [{
              "activityLocation": "MD",
              "code": "01",
              "counties": [{ "code": "003", "fipsCode": "24003" }],
              "episodicType": { "code": "E1", "durationDays": 90 }
            }]
            """;

        LookupPayloadRead read = LookupPayload.Read(payload);

        Assert.True(read.IsReadable);
        Assert.Equal(["durationDays", "fipsCode"], read.UnexpectedProperties);
    }

    [Fact]
    public void AnUnexpectedPropertyIsReportedOnceHoweverManyElementsCarryIt()
    {
        // A national list is ~1200 codes. One name repeated 1200 times in a log message is a message nobody
        // reads, so the report is a set.
        string payload = "["
            + string.Join(
                ",",
                Enumerable.Range(1, 50).Select(i => $$"""{"code":"{{i:000}}","novel":1}"""))
            + "]";

        LookupPayloadRead read = LookupPayload.Read(payload);

        Assert.Equal(50, read.Elements.Count);
        Assert.Equal(["novel"], read.UnexpectedProperties);
    }

    [Fact]
    public void PascalCaseBindsToo()
    {
        // The pinned spec says camelCase and this reader does not depend on it. Reading loosely is the
        // asymmetry PayloadJson's own remarks argue for: writing must be exact because OPENJSON matches
        // case-sensitively and a missed path shreds to NULL, but a national code list arriving as a column
        // of nulls because EPA capitalised a letter is not a failure worth having.
        LookupPayloadRead read = LookupPayload.Read("""[{ "Code": "01", "ActivityLocation": "MD" }]""");

        Assert.True(read.IsReadable);
        Assert.Equal("01", Assert.Single(read.Elements).Code);
        Assert.Empty(read.UnexpectedProperties);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyBodyIsRejectedAndSaysTheClassificationDisagreesWithIt(string? payload)
    {
        // ApiFetchResult guarantees a payload for Succeeded. Reaching here means that guarantee broke, which
        // is worth saying rather than treating as an empty list -- an empty list in Full mode retires
        // everything.
        LookupPayloadRead read = LookupPayload.Read(payload);

        Assert.False(read.IsReadable);
        Assert.Contains("empty", read.Problem, StringComparison.Ordinal);
        Assert.Empty(read.Elements);
    }

    [Fact]
    public void AMalformedBodyIsRejectedWithoutQuotingAnyOfIt()
    {
        LookupPayloadRead read = LookupPayload.Read("""[{ "code": "01", }""");

        Assert.False(read.IsReadable);
        Assert.Contains("not valid JSON", read.Problem, StringComparison.Ordinal);

        // The problem reaches logs.LoadRun.FailureMessage, which the monitoring web application reads.
        // A code list holds no secret; the rule there has no exception for payloads that happen to be
        // harmless, so the message names a path and a position and never a value.
        Assert.DoesNotContain("01", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyThatIsAnObjectRatherThanAnArrayIsRejected()
    {
        // Not a hypothetical: a paged envelope would look exactly like this, and the reflexive fix would be
        // to reach inside for the array -- which is how a 200-per-page list gets refreshed one page at a
        // time in Full mode, each page retiring the last.
        LookupPayloadRead read = LookupPayload.Read("""{ "items": [{ "code": "01" }] }""");

        Assert.False(read.IsReadable);
        Assert.Contains("not an array", read.Problem, StringComparison.Ordinal);
        Assert.Contains("Object", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElementWhoseTypesDoNotFitIsRejectedRatherThanBindingNulls()
    {
        LookupPayloadRead read = LookupPayload.Read("""[{ "code": "01", "active": "maybe" }]""");

        Assert.False(read.IsReadable);
        Assert.Contains("does not fit a lookup code", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElementWithNoCodeIsRejectedAndTheMessageSaysWhichOne()
    {
        // code is required: true in all 23 definitions and is script 523's MERGE key.
        LookupPayloadRead read = LookupPayload.Read(
            """[{ "code": "01" }, { "description": "no code here" }]""");

        Assert.False(read.IsReadable);
        Assert.Contains("element 1 of 2", read.Problem, StringComparison.Ordinal);
        Assert.Contains("523", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountyWithNoCodeIsRejectedToo()
    {
        // dbo.LookupStateDistrictCounty is merged in the same call and the same transaction as its parent,
        // so an unkeyable child would be a retirement inside a successful refresh of the district.
        LookupPayloadRead read = LookupPayload.Read(
            """[{ "code": "01", "counties": [{ "code": "003" }, { "description": "nameless" }] }]""");

        Assert.False(read.IsReadable);
        Assert.Contains("county 1 nested in element 0 of 1", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyArrayIsReadableAndTheDecisionAboutItBelongsToTheCaller()
    {
        // Readable, and refused one layer up. Parsing and policy are kept apart because 200-with-[] is a
        // successful, well-formed answer -- what makes it dangerous is the mode it would be sent in.
        LookupPayloadRead read = LookupPayload.Read("[]");

        Assert.True(read.IsReadable);
        Assert.Empty(read.Elements);
        Assert.Null(read.Problem);
    }
}
