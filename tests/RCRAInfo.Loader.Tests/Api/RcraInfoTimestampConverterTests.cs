using System.Text.Json;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Tests.Api;

/// <summary>
/// The one converter in the solution that exists because of a live call rather than a reading of the spec.
/// </summary>
/// <remarks>
/// Every case here is about the same asymmetry: what EPA sends is read loosely, and what this solution
/// writes is exact. The forms accepted are the ones a real service produces; the forms refused are refused
/// out loud, because the alternative for an expiry is a token believed to be already expired and an auth
/// call per request for the length of an overnight load.
/// </remarks>
public class RcraInfoTimestampConverterTests
{
    [Theory]

    // What EPA actually sent, measured against preprod on 2026-09-06. ISO 8601 basic offset -- the form
    // Utf8JsonReader.GetDateTimeOffset refuses, and the whole reason this converter exists.
    [InlineData("2026-09-06T13:40:44.361+0000", "2026-09-06T13:40:44.3610000+00:00")]
    [InlineData("2026-09-06T09:40:44.361-0400", "2026-09-06T09:40:44.3610000-04:00")]

    // RFC 3339, which the spec documents and which must keep working -- the tolerance is additive.
    [InlineData("2026-09-06T13:40:44.361+00:00", "2026-09-06T13:40:44.3610000+00:00")]
    [InlineData("2026-09-06T13:40:44.361Z", "2026-09-06T13:40:44.3610000+00:00")]
    [InlineData("2026-09-06T13:40:44Z", "2026-09-06T13:40:44.0000000+00:00")]

    // Hour-only offset. Accepted, and asserted rather than assumed, because "which forms does TryParse
    // take" is exactly the question a future reader will have -- and the answer is not the one guessed
    // here: RFC 1123 was expected to be accepted too and is REFUSED, which is why it now sits in the
    // theory below instead of this one.
    [InlineData("2026-09-06T13:40:44.361+00", "2026-09-06T13:40:44.3610000+00:00")]
    public void TheFormsAServiceActuallyProducesAreRead(string sent, string expected)
    {
        DateTimeOffset read = Read(sent);

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), read);
        Assert.Equal(TimeSpan.Zero, read.ToUniversalTime().Offset);
    }

    [Fact]
    public void AnOffsetIsPreservedRatherThanNormalisedOnTheWayIn()
    {
        // The instant is what matters and the offset is kept alongside it. Normalising to UTC here would
        // work, and would also mean the value read back could not be compared to what EPA sent -- which is
        // the comparison a support conversation with EPA is made of.
        DateTimeOffset read = Read("2026-09-06T09:40:44.361-0400");

        Assert.Equal(TimeSpan.FromHours(-4), read.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 13, 40, 44, 361, TimeSpan.Zero), read.ToUniversalTime());
    }

    [Theory]
    [InlineData("1789040444361")]
    [InlineData("2026-09-06 13:40:44 EDT")]
    [InlineData("yesterday")]
    [InlineData("2026-13-45T99:99:99Z")]

    // Measured, and the opposite of what was expected when this converter was written: RFC 1123 is
    // REFUSED by DateTimeOffset.TryParse under the invariant culture with DateTimeStyles.RoundtripKind.
    // Recorded here because the guess ("the invariant culture takes RFC 1123, so a service that switched
    // to it would keep working") is a plausible one to make again, and it is wrong.
    [InlineData("Sat, 06 Sep 2026 13:40:44 GMT")]
    public void AFormNobodyCanReadThrowsRatherThanBecomingNull(string sent)
    {
        JsonException error = Assert.Throws<JsonException>(() => Read(sent));

        // The length, never the value: this converter reads the auth response, and the property beside the
        // one it is reading is a bearer token.
        Assert.DoesNotContain(sent, error.Message, StringComparison.Ordinal);
        Assert.Contains($"{sent.Length} character(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberIsRefusedRatherThanInterpreted()
    {
        // Epoch milliseconds as a JSON number is the shape of the next divergence, and guessing the unit --
        // seconds or milliseconds -- would put a token's expiry off by a factor of a thousand in whichever
        // direction is worse.
        JsonException error = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<Holder>("""{"when":1789040444361}""", RcraInfoJson.Options));

        Assert.Contains("must be a JSON string", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyStringIsRefusedRatherThanTreatedAsAbsent()
    {
        JsonException error = Assert.Throws<JsonException>(() => Read(string.Empty));

        Assert.Contains("present but empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentIsNullAndNullIsNullAndNeitherReachesTheConverter()
    {
        // The nullable handling is JsonSerializer's, not this converter's -- the converter is registered for
        // DateTimeOffset and the framework unwraps Nullable<T> to it. Asserted because the alternative
        // design (a second converter for DateTimeOffset?) is the one that drifts.
        Assert.Null(JsonSerializer.Deserialize<Holder>("{}", RcraInfoJson.Options)!.When);
        Assert.Null(JsonSerializer.Deserialize<Holder>("""{"when":null}""", RcraInfoJson.Options)!.When);
    }

    [Fact]
    public void WritingProducesOneFormOnly()
    {
        // Loose in, exact out. A value read from EPA at -04:00 is written as UTC with a Z, so a round-trip
        // through this solution never re-emits the form that could not be read back.
        Holder holder = new() { When = new DateTimeOffset(2026, 9, 6, 9, 40, 44, 361, TimeSpan.FromHours(-4)) };

        Assert.Equal(
            """{"when":"2026-09-06T13:40:44.3610000Z"}""",
            JsonSerializer.Serialize(holder, RcraInfoJson.Options));
    }

    [Fact]
    public void TheSharedOptionsAlsoReadANumberQuotedAsAString()
    {
        // Not this converter's doing -- JsonNumberHandling.AllowReadingFromString, which lived on the auth
        // client's own options object and NOT on the data client's before RcraInfoJson unified them. ApiError
        // is deserialized by both, so this is the drift that unification removed.
        Assert.Equal(7, JsonSerializer.Deserialize<Counter>("""{"count":"7"}""", RcraInfoJson.Options)!.Count);
    }

    private static DateTimeOffset Read(string sent) =>
        JsonSerializer.Deserialize<Holder>(
            $$"""{"when":{{JsonSerializer.Serialize(sent)}}}""",
            RcraInfoJson.Options)!.When!.Value;

    private sealed class Holder
    {
        public DateTimeOffset? When { get; set; }
    }

    private sealed class Counter
    {
        public int Count { get; set; }
    }
}
