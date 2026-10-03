using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The reader between EPA's change feed and every fetch this loader makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>These tests carry the [R33] lesson forward, and that is most of why several of them exist.</b> The
/// pinned spec said EPA's token expiry was RFC 3339; the service sent <c>+0000</c>; 650 tests written from
/// the spec all agreed with the spec. <b>A test written from a specification cannot detect that the
/// specification is wrong.</b> So the cases below deliberately include forms the spec does <i>not</i>
/// describe — a timestamp in a <c>format: date</c> field, a quoted boolean — and assert that the reader
/// copes. If EPA never sends them, these tests cost nothing. If it does, the alternative was a window
/// rejected whole.
/// </para>
/// <para>
/// The other half asserts the narrowness of what counts as a problem. A summaries body is the input to
/// several hundred thousand fetches, and rejecting a window costs the watermark — so the reader refuses only
/// what makes an element unusable <i>as a key</i>, and everything else is read and passed on.
/// </para>
/// </remarks>
public class SummaryPayloadTests
{
    /// <summary>Every property the pinned spec declares, in EPA's own casing.</summary>
    private const string FullElement = """
        [{
          "handlerId": "MDD000000001",
          "activityLocation": "MD",
          "sourceType": "N",
          "sequence": 3,
          "receivedDate": "2020-01-01",
          "reportCycle": 2019,
          "federalGeneratorStatus": "LQG",
          "currentRecord": true,
          "dataOrigin": "eManifest",
          "updatedDate": "2020-02-15",
          "createdDate": "2019-12-31"
        }]
        """;

    [Fact]
    public void EverySpecDeclaredPropertyBinds()
    {
        SummaryPayloadRead read = SummaryPayload.Read(FullElement);

        Assert.True(read.IsReadable);
        Assert.Empty(read.UnexpectedProperties);

        HandlerSourceSummary summary = Assert.Single(read.Summaries);
        Assert.Equal("MDD000000001", summary.HandlerId);
        Assert.Equal("MD", summary.ActivityLocation);
        Assert.Equal("N", summary.SourceType);
        Assert.Equal(3, summary.Sequence);
        Assert.Equal(new DateOnly(2020, 1, 1), summary.ReceivedDate);
        Assert.Equal(2019, summary.ReportCycle);
        Assert.Equal("LQG", summary.FederalGeneratorStatus);
        Assert.True(summary.CurrentRecord);
        Assert.Equal("eManifest", summary.DataOrigin);
        Assert.Equal(new DateOnly(2020, 2, 15), summary.UpdatedDate);
        Assert.Equal(new DateOnly(2019, 12, 31), summary.CreatedDate);
        Assert.Equal(1, read.CurrentRecordCount);

        // The natural key the whole run is built on.
        HandlerVersion version = summary.ToVersion();
        Assert.Equal("MDD000000001", version.HandlerId);
        Assert.Equal("N", version.SourceType);
        Assert.Equal(3, version.Sequence);
    }

    [Fact]
    public void AnEmptyArrayIsAQuietWindowAndNotAProblem()
    {
        // The single most important non-rejection in the file. A quiet week must read as readable-and-empty,
        // because SummaryWalk turns "not readable" into a window that blocks the watermark.
        SummaryPayloadRead read = SummaryPayload.Read("[]");

        Assert.True(read.IsReadable);
        Assert.Empty(read.Summaries);
        Assert.Equal(0, read.CurrentRecordCount);
    }

    [Fact]
    public void AWrappedRootIsRefusedBecauseTheWrapperMayBeSayingTheResponseWasTruncated()
    {
        // Not pedantry about shape. The windowing design rests on the response being a bare array with no
        // total and no paging ([R28]); an envelope means that finding has expired, and the envelope is
        // exactly where a "truncated" flag would live.
        SummaryPayloadRead read = SummaryPayload.Read(
            """{"results":[{"handlerId":"MDD000000001","sourceType":"N","sequence":1}],"hasMore":true}""");

        Assert.False(read.IsReadable);
        Assert.Contains("bare array", read.Problem, StringComparison.Ordinal);
        Assert.Empty(read.Summaries);
    }

    [Fact]
    public void APropertyEpaAddsIsReportedRatherThanDroppedAndTheVersionsStillArrive()
    {
        SummaryPayloadRead read = SummaryPayload.Read(
            """
            [{
              "handlerId": "MDD000000001",
              "sourceType": "N",
              "sequence": 1,
              "nonNotifier": true,
              "lastMetalsDate": "2021-06-30"
            }]
            """);

        // Readable: an unbindable property is a notice, not a rejection. The alternative refuses a window --
        // and therefore blocks the watermark -- over a field this loader has no use for.
        Assert.True(read.IsReadable);
        Assert.Equal(["lastMetalsDate", "nonNotifier"], read.UnexpectedProperties);
        Assert.Single(read.Summaries);
    }

    [Fact]
    public void UnexpectedPropertyNamesAreReportedWithoutTheirValues()
    {
        SummaryPayloadRead read = SummaryPayload.Read(
            """
            [{
              "handlerId": "MDD000000001",
              "sourceType": "N",
              "sequence": 1,
              "contactEmail": "someone@example.com"
            }]
            """);

        Assert.Equal(["contactEmail"], read.UnexpectedProperties);

        // The name is schema; the value is a regulated entity's contact detail, which AR8 keeps out of this
        // application's own log. A property sweep that carried values would be the leak.
        Assert.DoesNotContain(
            "someone@example.com",
            string.Join("|", read.UnexpectedProperties),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ATimestampInADateFieldIsReadRatherThanRejected()
    {
        // The [R33] case, one field over. The spec says format: date with example "2020-01-01"; the same spec
        // said the token expiry was RFC 3339 and it was not. G25 is still open on which date field EPA even
        // filters by, so a timestamp arriving here is well within what "the spec is incomplete" covers.
        SummaryPayloadRead read = SummaryPayload.Read(
            """
            [{
              "handlerId": "MDD000000001",
              "sourceType": "N",
              "sequence": 1,
              "updatedDate": "2020-02-15T20:30:00-0500"
            }]
            """);

        Assert.True(read.IsReadable);

        // 2020-02-15 and NOT 2020-02-16: a late-evening summary converted to UTC lands in the next day, and
        // because the watermark advances by window, a date pushed past the window end is a permanent hole.
        Assert.Equal(new DateOnly(2020, 2, 15), Assert.Single(read.Summaries).UpdatedDate);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("\"true\"", true)]
    [InlineData("\"True\"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("false", false)]
    [InlineData("\"false\"", false)]
    [InlineData("\"0\"", false)]
    public void AQuotedCurrentRecordIsReadRatherThanRejected(string literal, bool expected)
    {
        // Script 521 already maps N'true'/N'1' by name in T-SQL, because TRY_CAST (N'true' AS BIT) returns
        // NULL. The hazard was therefore accepted as real one layer down, and a loader intolerant of it
        // rejects the payload before it reaches the T-SQL written to cope with it.
        SummaryPayloadRead read = SummaryPayload.Read(
            $$"""[{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":{{literal}}}]""");

        Assert.True(read.IsReadable);
        Assert.Equal(expected, Assert.Single(read.Summaries).CurrentRecord);
    }

    [Fact]
    public void AnUnknownCurrentRecordSpellingIsRefusedRatherThanGuessedAt()
    {
        // Tolerance means accepting more spellings of a KNOWN value, never guessing at an unknown one. A
        // value read as "not current" by accident demotes a version EPA calls current, which is a wrong
        // answer in dbo.HandlerSource rather than a missing one.
        SummaryPayloadRead read = SummaryPayload.Read(
            """[{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":"Y"}]""");

        Assert.False(read.IsReadable);
        Assert.Contains("does not fit", read.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectionNamesAPathAndNeverAnyPartOfTheBody()
    {
        const string handlerId = "MDD000000001";

        SummaryPayloadRead read = SummaryPayload.Read(
            $$"""[{"handlerId":"{{handlerId}}","sourceType":"N","sequence":"not-a-number"}]""");

        Assert.False(read.IsReadable);
        Assert.DoesNotContain(handlerId, read.Problem!, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-number", read.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnElementWithNoHandlerIdRefusesTheWindow()
    {
        SummaryPayloadRead read = SummaryPayload.Read(
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":1},
             {"sourceType":"N","sequence":1}]
            """);

        Assert.False(read.IsReadable);

        // The index locates the element in a body an operator can fetch again. The identifier of the element
        // BEFORE it -- which is perfectly good -- is still not named.
        Assert.Contains("element 1 of 2", read.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("MDD000000001", read.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandlerIdWiderThanTheColumnIsRefusedRatherThanClipped()
    {
        SummaryPayloadRead read = SummaryPayload.Read(
            """[{"handlerId":"MDD0000000012345","sourceType":"N","sequence":1}]""");

        Assert.False(read.IsReadable);

        // G36's reason, and it is why this is not left to OPENJSON: a truncated handlerId names a DIFFERENT
        // handler rather than none, so the wrong handler's version would be fetched and merged.
        Assert.Contains("16 characters", read.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("MDD0000000012345", read.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"handlerId":"MDD000000001","sourceType":"","sequence":1}]""")]
    [InlineData("""[{"handlerId":"MDD000000001","sourceType":"NX","sequence":1}]""")]
    [InlineData("""[{"handlerId":"MDD000000001","sourceType":"N","sequence":-1}]""")]
    [InlineData("""[{"handlerId":"   ","sourceType":"N","sequence":1}]""")]
    public void AnElementThatCannotBeNamedAsAVersionRefusesTheWindow(string payload)
    {
        SummaryPayloadRead read = SummaryPayload.Read(payload);

        Assert.False(read.IsReadable);
        Assert.Empty(read.Summaries);
    }

    [Fact]
    public void ASequenceOfZeroIsAcceptedBecauseSurprisingIsNotUnusable()
    {
        // EPA's sequences are observed to start at 1 and the spec does not say so. Refusing a whole window --
        // and blocking the watermark -- over a value that is merely unexpected is the wrong trade; a NEGATIVE
        // sequence, which cannot be a version under any reading, is the line.
        SummaryPayloadRead read = SummaryPayload.Read(
            """[{"handlerId":"MDD000000001","sourceType":"N","sequence":0}]""");

        Assert.True(read.IsReadable);
        Assert.Equal(0, Assert.Single(read.Summaries).Sequence);
    }

    [Fact]
    public void EveryDateAbsentIsStillReadableBecauseTheWatermarkNeverComesFromARow()
    {
        // Nine of the eleven properties are `required` in the spec and this element has three of them. It is
        // still accepted, because the watermark advances by the window that was ASKED for -- never by a date
        // read out of a row -- so an absent date costs this loader nothing it relies on.
        SummaryPayloadRead read = SummaryPayload.Read(
            """[{"handlerId":"MDD000000001","sourceType":"N","sequence":1}]""");

        Assert.True(read.IsReadable);

        HandlerSourceSummary summary = Assert.Single(read.Summaries);
        Assert.Null(summary.UpdatedDate);
        Assert.Null(summary.ReceivedDate);
        Assert.Null(summary.CreatedDate);
        Assert.False(summary.CurrentRecord);
    }

    [Fact]
    public void EpasCasingIsNotAssumedToStayTheSame()
    {
        // JsonSerializerDefaults.Web gives case-insensitive binding, which is the tolerance LookupPayload
        // argues for at length. Asserted rather than assumed: it arrives from a default, and a default that
        // changed would silently bind nothing while reporting every property as unexpected.
        SummaryPayloadRead read = SummaryPayload.Read(
            """[{"HandlerId":"MDD000000001","SOURCETYPE":"N","Sequence":4,"CurrentRecord":true}]""");

        Assert.True(read.IsReadable);
        Assert.Empty(read.UnexpectedProperties);

        HandlerSourceSummary summary = Assert.Single(read.Summaries);
        Assert.Equal("MDD000000001", summary.HandlerId);
        Assert.Equal(4, summary.Sequence);
        Assert.True(summary.CurrentRecord);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[{\"handlerId\":\"MDD000000001\"")]
    public void ABodyThatCannotBeParsedIsReportedRatherThanThrown(string? payload)
    {
        // Never throws, for a sharper reason than LookupPayload's: a rejected window must be REPORTABLE,
        // because a window that failed is the one thing that must stop the watermark advancing. An exception
        // would be caught somewhere generic and the run would end as "failed" without naming which window.
        SummaryPayloadRead read = SummaryPayload.Read(payload);

        Assert.False(read.IsReadable);
        Assert.NotNull(read.Problem);
        Assert.Empty(read.Summaries);
    }

    [Fact]
    public void CurrentRecordCountIsPerWindowAndCountsEveryOne()
    {
        SummaryPayloadRead read = SummaryPayload.Read(
            """
            [{"handlerId":"MDD000000001","sourceType":"N","sequence":1,"currentRecord":false},
             {"handlerId":"MDD000000001","sourceType":"N","sequence":2,"currentRecord":true},
             {"handlerId":"MDD000000002","sourceType":"N","sequence":1,"currentRecord":true}]
            """);

        Assert.True(read.IsReadable);
        Assert.Equal(3, read.Summaries.Count);

        // Two current records across two different handlers is normal; two for the SAME (handlerId,
        // sourceType) is what script 521 has to resolve. This count is what makes either visible at all.
        Assert.Equal(2, read.CurrentRecordCount);
    }
}
