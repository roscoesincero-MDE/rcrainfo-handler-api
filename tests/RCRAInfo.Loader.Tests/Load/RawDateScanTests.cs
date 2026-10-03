using System.Text;

using RCRAInfo.Loader.Load;

namespace RCRAInfo.Loader.Tests.Load;

/// <summary>
/// The raw-date walk, and the reason every assertion in it is about text rather than about a value.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole class exists because a parsed date cannot answer G25.</b> A <see cref="DateOnly"/> that came out
/// of <c>RcraInfoDateConverter</c> proves the converter accepted <i>something</i>; it cannot say what. That is
/// exactly the claim that was true of the auth endpoint's <c>expiration</c> right up until a <c>+0000</c> offset
/// the pinned specification does not describe broke it ([R33]). So the tests below compare strings, quotes
/// included, and a test that compared anything else would pass while the scan rounded the answer off.
/// </para>
/// <para>
/// <b>And the answer cannot be recovered afterwards.</b> No column retains a raw body — <c>dbo.HandlerSource</c>
/// keeps mapped columns and <c>logs.HandlerLoadStatus</c> keeps a hash — so a shape this scan flattens is a
/// shape nobody can go back for.
/// </para>
/// <para>
/// <b>The second theme is AR8.</b> The selection rule is a property name ending in <c>Date</c>, and that is
/// what keeps a contact name, a phone number or an email address out of a console line an operator may paste
/// into a ticket. It is asserted here rather than left to review, because the rule is one
/// <c>StringComparison</c> away from matching everything.
/// </para>
/// </remarks>
public sealed class RawDateScanTests
{
    /// <summary>EPA's text survives verbatim, at the root and one collection down, quotes and all.</summary>
    /// <remarks>
    /// <b>The single most consequential assertion in the file.</b> The two shapes are the ones [R33] taught this
    /// project to expect — a bare date and a date carrying a <c>+0000</c> offset — and anything that
    /// re-rendered them through a date type would report two identical values and answer G25 wrongly.
    /// </remarks>
    [Fact]
    public void EpasTextSurvivesVerbatimAtEveryDepth()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan = RawDateScan.Scan(
            """
            {"handlerId":"MDD000000001",
             "createdDate":"2003-07-03T00:00:00.000+0000",
             "updatedDate":"2019-11-14",
             "contact":[{"updatedDate":"2019-11-14T09:30:00.000+0000"}]}
            """);

        Assert.Equal(["\"2003-07-03T00:00:00.000+0000\""], scan["$.createdDate"]);
        Assert.Equal(["\"2019-11-14\""], scan["$.updatedDate"]);
        Assert.Equal(["\"2019-11-14T09:30:00.000+0000\""], scan["$.contact[].updatedDate"]);
    }

    /// <summary>
    /// A property EPA sent with a JSON <c>null</c> is reported as <c>null</c>, not as absent and not as empty.
    /// </summary>
    /// <remarks>
    /// Three findings a <c>GetString</c> would flatten into one: present with a value, present and null, absent
    /// altogether. Which of the three a field is decides whether the column mapped from it may be
    /// <c>NOT NULL</c>, so collapsing them answers a schema question wrongly — and quietly.
    /// </remarks>
    [Fact]
    public void APropertyPresentWithAJsonNullIsDistinguishableFromAnAbsentOne()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan =
            RawDateScan.Scan("""{"createdDate":null,"updatedDate":""}""");

        Assert.Equal(["null"], scan["$.createdDate"]);
        Assert.Equal(["\"\""], scan["$.updatedDate"]);
        Assert.False(scan.ContainsKey("$.receivedDate"));
    }

    /// <summary>A date sent as a number rather than as a string is reported as the number it is.</summary>
    /// <remarks>
    /// An epoch millisecond value is a real possibility on a feed nobody has read raw, and it is the one shape
    /// that would reach a date converter and fail there rather than here. Reported as <c>1057190400000</c> with
    /// no quotes, which is how a reader tells the two cases apart at a glance.
    /// </remarks>
    [Fact]
    public void ADateSentAsANumberIsReportedAsANumber()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan =
            RawDateScan.Scan("""{"createdDate":1057190400000}""");

        Assert.Equal(["1057190400000"], scan["$.createdDate"]);
    }

    /// <summary>
    /// Every element of a collection reports under one collapsed path, so the output holds shapes and not rows.
    /// </summary>
    /// <remarks>
    /// A handler payload has 18 child collections and hundreds of rows across them. Reporting a path per index
    /// would produce output nobody reads, which for a diagnostic is the same as producing none.
    /// </remarks>
    [Fact]
    public void EveryElementOfACollectionReportsUnderOneCollapsedPath()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan = RawDateScan.Scan(
            """
            {"wasteCode":[{"updatedDate":"2019-11-14"},
                          {"updatedDate":"2020-01-02"},
                          {"updatedDate":"2019-11-14"}]}
            """);

        Assert.Single(scan);
        Assert.Equal(["\"2019-11-14\"", "\"2020-01-02\""], scan["$.wasteCode[].updatedDate"]);
    }

    /// <summary>One shape repeated across a thousand rows is reported once, in first-seen order.</summary>
    [Fact]
    public void RepeatedShapesAreReportedOnce()
    {
        StringBuilder body = new("[");

        for (int index = 0; index < 500; index++)
        {
            body.Append(index == 0 ? string.Empty : ",").Append("""{"updatedDate":"2019-11-14"}""");
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> scan =
            RawDateScan.Scan(body.Append(']').ToString());

        Assert.Equal(["\"2019-11-14\""], scan["$[].updatedDate"]);
    }

    /// <summary>
    /// The sample cap holds for repeats of a shape already seen, and it holds at its stated value rather than
    /// near it.
    /// </summary>
    /// <remarks>
    /// Asserted at the boundary because a cap tested only well inside or well outside it is a cap whose actual
    /// value nothing checks. The ninth distinct <i>value</i> is dropped; the first eight are the ones kept, so
    /// the output is what EPA sent first rather than an arbitrary subset. <b>[R46] Every value here carries the
    /// same shape</b>, which is what makes the cap apply — see
    /// <see cref="AValueCarryingANewShapeIsKeptPastTheSampleCap"/> for the case that outranks it.
    /// </remarks>
    [Fact]
    public void AtMostTheStatedNumberOfDistinctValuesIsKeptPerPathWhenTheShapeRepeats()
    {
        StringBuilder body = new("[");

        for (int index = 0; index < RawDateScan.MaxSamplesPerPath + 4; index++)
        {
            body.Append(index == 0 ? string.Empty : ",")
                .Append("{\"updatedDate\":\"2019-11-")
                .Append((index + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\"}");
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> scan =
            RawDateScan.Scan(body.Append(']').ToString());

        Assert.Equal(RawDateScan.MaxSamplesPerPath, scan["$[].updatedDate"].Count);
        Assert.Equal("\"2019-11-01\"", scan["$[].updatedDate"][0]);
        Assert.DoesNotContain("\"2019-11-09\"", scan["$[].updatedDate"]);
    }

    /// <summary>The path cap holds, so a drifted body cannot produce output nobody will read.</summary>
    /// <remarks>
    /// Reaching this cap is itself a finding: a handler payload has 18 child collections, so 64 distinct
    /// date-shaped paths would mean the payload is not the payload the mirror was modelled from.
    /// </remarks>
    [Fact]
    public void AtMostTheStatedNumberOfPathsIsReported()
    {
        StringBuilder body = new("{");

        for (int index = 0; index < RawDateScan.MaxPaths + 10; index++)
        {
            body.Append(index == 0 ? string.Empty : ",")
                .Append('"')
                .Append("field")
                .Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("Date\":\"2019-11-14\"");
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> scan =
            RawDateScan.Scan(body.Append('}').ToString());

        Assert.Equal(RawDateScan.MaxPaths, scan.Count);
    }

    /// <summary>
    /// The depth cap holds at its stated value, so a pathologically nested body cannot turn a diagnostic into a
    /// stack overflow.
    /// </summary>
    /// <remarks>
    /// The deepest date in a handler payload sits one collection below the root, so this cap exists only as a
    /// floor under a body EPA has not sent yet. Asserted as a pair at the boundary for the sample cap's reason.
    /// </remarks>
    [Fact]
    public void TheDepthCapIsReachedOnlyOnceItIsExceeded()
    {
        Assert.Single(RawDateScan.Scan(Nested(RawDateScan.MaxDepth)));
        Assert.Empty(RawDateScan.Scan(Nested(RawDateScan.MaxDepth + 1)));
    }

    /// <summary>
    /// A property whose name does not end in <c>Date</c> is never reported, whatever it holds.
    /// </summary>
    /// <remarks>
    /// <b>AR8, asserted rather than reviewed.</b> This output is printed to a console an operator may paste into
    /// a ticket, which is a wider audience than a log table rather than a narrower one. A contact's name, phone
    /// number and email address are in the body this scan walks, and the only thing keeping them out of the
    /// report is the suffix rule.
    /// </remarks>
    [Fact]
    public void NothingButADateNamedPropertyIsEverReported()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan = RawDateScan.Scan(
            """
            {"contact":[{"firstName":"Ada","lastName":"Lovelace","phoneNumber":"410-555-0100",
                         "email":"ada@example.org","streetNumber":"1","updatedDate":"2019-11-14"}],
             "createdBy":"EPAUSER","updatedBy":"EPAUSER","handlerName":"A Site"}
            """);

        Assert.Equal(["$.contact[].updatedDate"], scan.Keys);

        string printed = string.Join("|", scan.SelectMany(pair => pair.Value));

        Assert.DoesNotContain("Ada", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("410-555", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("example.org", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("EPAUSER", printed, StringComparison.Ordinal);
    }

    /// <summary>The suffix matches whatever casing EPA used for it.</summary>
    /// <remarks>
    /// The feeds are camel case today. A field arriving as <c>updateddate</c> or <c>UpdatedDATE</c> is exactly
    /// the drift a probe exists to notice, and an ordinal suffix test would silently skip it.
    /// </remarks>
    /// <param name="property">The property name as EPA might send it.</param>
    [Theory]
    [InlineData("updatedDate")]
    [InlineData("updateddate")]
    [InlineData("UpdatedDATE")]
    [InlineData("Date")]
    public void TheSuffixMatchesWhateverCasingEpaUsed(string property)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan =
            RawDateScan.Scan($"{{\"{property}\":\"2019-11-14\"}}");

        Assert.Single(scan);
    }

    /// <summary>
    /// An unparseable, empty or absent body yields an empty scan rather than an exception.
    /// </summary>
    /// <remarks>
    /// <b>Never throwing is the requirement, not a convenience.</b> The caller has already read the same body
    /// through a payload reader that reports its own diagnosed problem, so a throw out of this second parse
    /// would replace a diagnosed refusal with an undiagnosed one — and the operator would be told the probe
    /// crashed instead of being told what was wrong with EPA's answer.
    /// </remarks>
    /// <param name="payload">The body, such as it is.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("""{"updatedDate":"2019-11-14",""")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    public void AnUnusableBodyIsAnEmptyScanAndNeverAThrow(string? payload)
    {
        Assert.Empty(RawDateScan.Scan(payload));
    }

    /// <summary>
    /// A truncated body reports the dates it read before the break, rather than discarding them.
    /// </summary>
    /// <remarks>
    /// This is not merely tolerance. <c>JsonDocument.Parse</c> refuses the whole document, so the scan returns
    /// what it had — which for a truncated response is nothing. The assertion pins that the swallow does not
    /// turn into a silent partial answer somebody might read as complete.
    /// </remarks>
    [Fact]
    public void ATruncatedBodyDoesNotYieldAPartialAnswerThatLooksComplete()
    {
        Assert.Empty(RawDateScan.Scan("""[{"updatedDate":"2019-11-14"},{"updatedDate":"""));
    }

    /// <summary>
    /// A scalar or an empty structure at the root is an empty scan, which is an answer and not a failure.
    /// </summary>
    /// <param name="payload">A valid JSON body with no date-shaped property in it.</param>
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"2019-11-14\"")]
    public void AValidBodyWithNoDateNamedPropertyIsAnEmptyScan(string payload)
    {
        Assert.Empty(RawDateScan.Scan(payload));
    }

    /// <summary>
    /// Two bodies that write a shared date field in a different shape are reported as disagreeing, by leaf name,
    /// and the entry carries both shapes.
    /// </summary>
    /// <remarks>
    /// <b>This is the comparison G25 actually needs.</b> The summaries feed is an array of rows and the
    /// record-detail feed is an object, so their paths cannot match — comparing on the leaf name is what makes
    /// the two answers comparable at all. And the finding is the point: two lists of strings leave a reader to
    /// diff them by eye, which is how a <c>+0000</c> on one field of one feed goes unnoticed.
    /// </remarks>
    [Fact]
    public void TwoBodiesWritingASharedFieldDifferentlyAreReportedAsDisagreeing()
    {
        IReadOnlyList<string> differ = RawDateScan.Disagreements(
            RawDateScan.Scan("""[{"updatedDate":"2019-11-14","receivedDate":"2019-11-01"}]"""),
            RawDateScan.Scan("""{"updatedDate":"2019-11-14T00:00:00.000+0000","receivedDate":"2019-11-01"}"""));

        // updatedDate differs in shape; receivedDate is identical and must not be named. Both shapes are on the
        // line, so the claim can be checked without reading the two scans.
        Assert.Equal(
            ["updatedDate: \"yyyy-MM-dd\" vs \"yyyy-MM-ddTHH:mm:ss.fff+ZZZZ\""],
            differ);
    }

    /// <summary>
    /// <b>[R46]</b> The same shape carrying different values is not a disagreement, however many values there
    /// are on either side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test that was missing, and its absence produced a false positive on the very first live
    /// run.</b> The probe asks two feeds different questions: <c>?handlerId=</c> answers with every version of the
    /// handler and the record-detail call answers with one. So the summaries side carries 23 handlers' worth of
    /// dates and the record side carries one version's — a superset and a subset, never equal as sets of values.
    /// Comparing raw values therefore reported <c>createdDate</c>, <c>receivedDate</c> and <c>updatedDate</c> as
    /// differing "in shape" on <c>MD0570024000</c> when all nine values on both sides were bare
    /// <c>"yyyy-MM-dd"</c>.
    /// </para>
    /// <para>
    /// <b>Every existing case compared bodies whose shared field held the same value</b>, so not one of them
    /// distinguished a difference of value from a difference of shape — [R43]'s lesson for the third time. The
    /// body below is the live shape of the two feeds, deliberately: many values one side, one value the other,
    /// no overlap at all between them.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSameShapeCarryingDifferentValuesIsNotADisagreement()
    {
        IReadOnlyList<string> differ = RawDateScan.Disagreements(
            RawDateScan.Scan("""{"createdDate":"2026-06-12","receivedDate":"2026-05-28"}"""),
            RawDateScan.Scan(
                """
                [{"createdDate":"2024-07-08","receivedDate":"2024-03-01"},
                 {"createdDate":"2022-08-29","receivedDate":"2022-08-16"},
                 {"createdDate":"2018-10-03","receivedDate":"2012-11-02"}]
                """));

        Assert.Empty(differ);
    }

    /// <summary>Two bodies that agree report no disagreement, which is the expected result.</summary>
    [Fact]
    public void TwoBodiesThatAgreeReportNoDisagreement()
    {
        IReadOnlyList<string> differ = RawDateScan.Disagreements(
            RawDateScan.Scan("""[{"updatedDate":"2019-11-14"}]"""),
            RawDateScan.Scan("""{"updatedDate":"2019-11-14","contact":[{"updatedDate":"2019-11-14"}]}"""));

        Assert.Empty(differ);
    }

    /// <summary>
    /// A field only one body carries is not a disagreement, because there is nothing to compare it against.
    /// </summary>
    /// <remarks>
    /// The record-detail body has date fields the summaries feed does not, and calling every one of them a
    /// disagreement would bury the fields that genuinely differ under the fields that are simply richer.
    /// </remarks>
    [Fact]
    public void AFieldOnlyOneBodyCarriesIsNotADisagreement()
    {
        IReadOnlyList<string> differ = RawDateScan.Disagreements(
            RawDateScan.Scan("""[{"updatedDate":"2019-11-14"}]"""),
            RawDateScan.Scan("""{"updatedDate":"2019-11-14","certifiedDate":"2019-11-14T00:00:00.000+0000"}"""));

        Assert.Empty(differ);
    }

    /// <summary>
    /// One leaf name appearing at several paths in one body is compared as the union of what it held.
    /// </summary>
    /// <remarks>
    /// <c>updatedDate</c> occurs at the root and inside most of the 18 child collections. Comparing path by
    /// path would be comparing nothing, since the other feed has neither path; comparing the union answers the
    /// question actually being asked, which is whether the field's <i>shape</i> differs between the feeds.
    /// </remarks>
    [Fact]
    public void OneLeafNameAtSeveralPathsIsComparedAsTheUnionOfItsShapes()
    {
        IReadOnlyList<string> differ = RawDateScan.Disagreements(
            RawDateScan.Scan("""[{"updatedDate":"2019-11-14"}]"""),
            RawDateScan.Scan(
                """
                {"updatedDate":"2019-11-14",
                 "contact":[{"updatedDate":"2019-11-14T00:00:00.000+0000"}]}
                """));

        // Both of the second body's shapes are on the line, in shape order, so a reader can see that the root
        // agrees and the nested one does not -- which is the whole finding.
        Assert.Equal(
            ["updatedDate: \"yyyy-MM-dd\" vs \"yyyy-MM-dd\" or \"yyyy-MM-ddTHH:mm:ss.fff+ZZZZ\""],
            differ);
    }

    /// <summary>Two empty scans agree, and a null argument is refused rather than treated as empty.</summary>
    [Fact]
    public void TwoEmptyScansAgreeAndANullScanIsRefused()
    {
        Assert.Empty(RawDateScan.Disagreements(RawDateScan.Scan("{}"), RawDateScan.Scan("[]")));

        Assert.Throws<ArgumentNullException>(
            () => RawDateScan.Disagreements(null!, RawDateScan.Scan("{}")));

        Assert.Throws<ArgumentNullException>(
            () => RawDateScan.Disagreements(RawDateScan.Scan("{}"), null!));
    }

    /// <summary>The leaf name is the text after the last separator, and a path without one is its own leaf.</summary>
    /// <param name="path">A path the scan produces.</param>
    /// <param name="expected">Its leaf name.</param>
    [Theory]
    [InlineData("$.updatedDate", "updatedDate")]
    [InlineData("$.contact[].updatedDate", "updatedDate")]
    [InlineData("$[].updatedDate", "updatedDate")]
    [InlineData("updatedDate", "updatedDate")]
    [InlineData("$", "$")]
    // The collapsed array marker stays on the leaf, so a date-named collection is never confused with a
    // date-named property of one.
    [InlineData("$.wasteCode[]", "wasteCode[]")]
    public void TheLeafNameIsTheTextAfterTheLastSeparator(string path, string expected)
    {
        Assert.Equal(expected, RawDateScan.LeafName(path));
    }

    /// <summary>A null path is refused rather than answered.</summary>
    [Fact]
    public void ANullPathIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => RawDateScan.LeafName(null!));
    }

    /// <summary>
    /// <b>[R46]</b> A raw token is classified by its shape, and the two shapes G25 turns on are named as the
    /// formats they are.
    /// </summary>
    /// <remarks>
    /// The first two rows are the whole of G25: what the summaries feed sends and what [R33]'s auth
    /// <c>expiration</c> sent. Every value below is a different date from every other, which is the point —
    /// the shape must not vary with the value.
    /// </remarks>
    /// <param name="raw">The raw JSON token, as <c>GetRawText</c> returns it.</param>
    /// <param name="expected">Its shape.</param>
    [Theory]
    [InlineData("\"2026-06-12\"", "\"yyyy-MM-dd\"")]
    [InlineData("\"2003-07-03T00:00:00.000+0000\"", "\"yyyy-MM-ddTHH:mm:ss.fff+ZZZZ\"")]
    [InlineData("\"1999-12-31\"", "\"yyyy-MM-dd\"")]
    [InlineData("\"2019-11-14T09:30:00\"", "\"yyyy-MM-ddTHH:mm:ss\"")]
    [InlineData("\"2019-11-14T09:30:00.123Z\"", "\"yyyy-MM-ddTHH:mm:ss.fffZ\"")]
    [InlineData("\"2019-11-14T09:30:00.123+05:30\"", "\"yyyy-MM-ddTHH:mm:ss.fff+ZZ:ZZ\"")]
    [InlineData("\"2019-11-14 09:30:00\"", "\"yyyy-MM-dd HH:mm:ss\"")]
    [InlineData("null", "null")]
    [InlineData("1057190400000", "number")]
    [InlineData("-1", "number")]
    [InlineData("true", "boolean")]
    [InlineData("\"\"", "empty text")]
    [InlineData("", "empty text")]
    // An unrecognised but still date-shaped mask is reported as the mask. Legible enough, and this is the case
    // that wants looking at rather than reading fluently.
    [InlineData("\"2019-11\"", "\"dddd-dd\"")]
    [InlineData("\"20191114\"", "\"dddddddd\"")]
    public void ARawTokenIsClassifiedByItsShapeAndNotByItsValue(string raw, string expected)
    {
        Assert.Equal(expected, RawDateScan.ShapeOf(raw));
    }

    /// <summary>
    /// <b>[R46] AR8 again, one level lower: a shape can never reproduce text.</b>
    /// </summary>
    /// <remarks>
    /// The suffix rule already makes it impossible for a contact field to be walked at all, so this is the
    /// second independent guard rather than the first. Only digits collapsed to <c>d</c> and eight structural
    /// characters can survive into a shape; anything else is a character count, which is script <c>523</c>'s
    /// rule for a width refusal applied to a console line.
    /// </remarks>
    /// <param name="raw">A raw token that is not date-shaped.</param>
    [Theory]
    [InlineData("\"Ada Lovelace\"")]
    [InlineData("\"ada@example.org\"")]
    [InlineData("\"410-555-0100x\"")]
    [InlineData("\"SACRED HEART HOSPITAL\"")]
    public void AShapeIsACharacterCountWhenTheTextIsNotDateShaped(string raw)
    {
        string shape = RawDateScan.ShapeOf(raw);

        Assert.StartsWith("text(len=", shape, StringComparison.Ordinal);
        Assert.EndsWith(")", shape, StringComparison.Ordinal);

        foreach (char character in raw.Trim('"'))
        {
            if (char.IsAsciiLetter(character))
            {
                Assert.DoesNotContain(character.ToString(), shape[9..], StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// <b>[R46]</b> A date-named property holding an object or an array is reported as that, and never by its
    /// contents.
    /// </summary>
    /// <remarks>
    /// The one branch where the token could hold something that is not a date at all. A structural surprise on a
    /// date field is a finding worth printing; its contents are not printable, because nothing constrains what an
    /// object under <c>updatedDate</c> would contain.
    /// </remarks>
    [Fact]
    public void AStructuredValueUnderADateNamedPropertyIsReportedWithoutItsContents()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan = RawDateScan.Scan(
            """{"updatedDate":{"value":"2019-11-14","enteredBy":"Ada Lovelace"},"createdDate":["2019-11-14"]}""");

        Assert.Equal("object", RawDateScan.ShapeOf(scan["$.updatedDate"][0]));
        Assert.Equal("array", RawDateScan.ShapeOf(scan["$.createdDate"][0]));

        IReadOnlyList<string> differ = RawDateScan.Disagreements(
            scan,
            RawDateScan.Scan("""{"updatedDate":"2019-11-14","createdDate":"2019-11-14"}"""));

        Assert.Equal(
            [
                "createdDate: array vs \"yyyy-MM-dd\"",
                "updatedDate: object vs \"yyyy-MM-dd\"",
            ],
            differ);

        Assert.DoesNotContain("Ada", string.Join("|", differ), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>[R46] A value whose shape is new is kept even after the sample cap is reached.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cap exists so a body carrying one shape across five hundred rows does not print five hundred values.
    /// Applied to values alone it can hide the answer: a handler whose first eight dates are ordinary and whose
    /// ninth carries an offset would report as entirely ordinary — the [R33] defect, reintroduced by the
    /// instrument built to find it.
    /// </para>
    /// <para>
    /// <c>MD0570024000</c> came within one value of showing this on the first live run: its summaries body named
    /// **23** versions of one handler and the walk kept 8.
    /// </para>
    /// </remarks>
    [Fact]
    public void AValueCarryingANewShapeIsKeptPastTheSampleCap()
    {
        StringBuilder body = new("[");

        for (int index = 0; index < RawDateScan.MaxSamplesPerPath + 4; index++)
        {
            body.Append(index == 0 ? string.Empty : ",")
                .Append("{\"updatedDate\":\"2019-11-")
                .Append((index + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\"}");
        }

        // The odd one out, and it arrives last -- well past the cap.
        body.Append(""",{"updatedDate":"2019-11-30T00:00:00.000+0000"}]""");

        IReadOnlyList<string> samples = RawDateScan.Scan(body.ToString())["$[].updatedDate"];

        Assert.Equal(RawDateScan.MaxSamplesPerPath + 1, samples.Count);
        Assert.Contains("\"2019-11-30T00:00:00.000+0000\"", samples);

        // And it is not merely retained: it reaches the comparison, which is the only place it does any good.
        Assert.Single(RawDateScan.Disagreements(
            RawDateScan.Scan(body.ToString()),
            RawDateScan.Scan("""{"updatedDate":"2019-11-14"}""")));
    }

    /// <summary>
    /// <b>[R46]</b> The extra room for new shapes is itself bounded, so a body of nothing but novel shapes
    /// cannot grow the output without limit.
    /// </summary>
    [Fact]
    public void TheRoomForNewShapesIsAlsoCapped()
    {
        StringBuilder body = new("[");

        // Every element carries a different mask: one digit longer each time.
        for (int index = 0; index < RawDateScan.MaxSamplesPerPath + RawDateScan.MaxExtraShapesPerPath + 6; index++)
        {
            body.Append(index == 0 ? string.Empty : ",").Append("{\"updatedDate\":\"").Append('1', index + 1)
                .Append("\"}");
        }

        IReadOnlyList<string> samples = RawDateScan.Scan(body.Append(']').ToString())["$[].updatedDate"];

        Assert.Equal(RawDateScan.MaxSamplesPerPath + RawDateScan.MaxExtraShapesPerPath, samples.Count);
    }

    /// <summary>A null token is refused rather than classified.</summary>
    [Fact]
    public void ANullTokenIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => RawDateScan.ShapeOf(null!));
    }

    /// <summary>A body with one date-shaped property at the given nesting depth, and nothing else.</summary>
    /// <param name="depth">How many objects to wrap it in. Zero puts it at the root.</param>
    /// <returns>The JSON body.</returns>
    private static string Nested(int depth)
    {
        StringBuilder body = new();

        for (int level = 0; level < depth; level++)
        {
            body.Append("{\"child\":");
        }

        body.Append("""{"deepDate":"2019-11-14"}""");

        for (int level = 0; level < depth; level++)
        {
            body.Append('}');
        }

        return body.ToString();
    }
}
