using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Reads the date-and-time strings RCRAInfo actually sends, which <see cref="JsonSerializer"/> on its own
/// will not read.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because of a measurement, not a precaution.</b> The first live call ever made to EPA's
/// auth endpoint from this solution returned <c>200</c> with a token — the credential worked — and the
/// client rejected the answer, because the <c>expiration</c> field arrived as
/// <c>2026-09-06T13:40:44.361+0000</c>. That is ISO 8601, in the <i>basic</i> offset form. It is not
/// RFC 3339, and <see cref="Utf8JsonReader.GetDateTimeOffset"/> implements RFC 3339 strictly: it requires
/// <c>+00:00</c> or <c>Z</c> and refuses <c>+0000</c>. So the outcome was
/// <see cref="ApiAuthOutcome.Unexpected"/> — "the pinned spec and the live service have diverged" — which
/// was exactly the right classification of exactly the right problem, arriving on the first request rather
/// than at 2am.
/// </para>
/// <para>
/// <b>The remedy is tolerance on the way in, and it is one call.</b>
/// <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/> with
/// <see cref="CultureInfo.InvariantCulture"/> accepts <c>+0000</c> and <c>-0400</c> as well as the RFC 3339
/// forms — measured, both of them. That is the same asymmetry <c>LookupPayload</c> argues for and for the
/// same reason: what EPA sends is not ours to pin, while what this solution <i>writes</i> stays exact.
/// <see cref="Write"/> therefore emits one form only, UTC with a <c>Z</c>, matching
/// <c>PayloadJson</c>'s own converter.
/// </para>
/// <para>
/// <b>Registered for <see cref="DateTimeOffset"/> and relied on for <c>DateTimeOffset?</c>.</b>
/// <see cref="JsonSerializer"/> unwraps <see cref="Nullable{T}"/> to the converter for <c>T</c> and handles
/// the <c>null</c> token itself, so both of the properties this converter exists for —
/// <c>RcraInfoAuthClient.ApiAuthResponse.Expiration</c> and <see cref="ApiError.ErrorDate"/>, both nullable
/// — are covered without a second converter that could drift from this one.
/// </para>
/// <para>
/// <b>An unreadable value throws rather than becoming <c>null</c>.</b> Silently nulling
/// <c>expiration</c> would not fail: <c>RcraInfoAuthClient</c> treats an absent expiration as an immediate
/// one, so a load would keep running and simply re-authenticate against EPA at the floor rate
/// (<c>MinimumRefreshInterval</c>) for its whole duration. That is a sustained, unexplained load aimed at
/// the one endpoint that carries the credential in its URI. Throwing turns the same condition into one
/// <see cref="ApiAuthOutcome.Unexpected"/> with a JSON path attached, which is a fact somebody acts on.
/// </para>
/// </remarks>
public sealed class RcraInfoTimestampConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>The one form this converter writes: UTC, seven fractional digits, <c>Z</c>.</summary>
    private const string RoundTripUtc = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <inheritdoc/>
    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            // No value in the message, on any path here. This converter reads the auth response, and the
            // property beside the one it is reading is a bearer token (AR8) -- so the rule is the class's,
            // not the value's, and it holds even though a timestamp is not itself a secret.
            throw new JsonException(
                "A RCRAInfo date and time must be a JSON string. The value is not reported here because "
                + "this converter reads the auth response, whose other property is a bearer token.");
        }

        string? text = reader.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException(
                "A RCRAInfo date and time was present but empty. An empty string is not an absent field: "
                + "the field is there, so something produced it, and guessing a value for it would put a "
                + "made-up expiry on a real token.");
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw new JsonException(
            string.Format(
                CultureInfo.InvariantCulture,
                "A RCRAInfo date and time of {0} character(s) is not a date and time this client can "
                + "read. Both RFC 3339 (+00:00, Z) and ISO 8601 basic offsets (+0000) are accepted, so a "
                + "value that reaches here is a third form. The value itself is not reported -- run the "
                + "auth call and inspect the body directly.",
                text.Length));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToUniversalTime().ToString(RoundTripUtc, CultureInfo.InvariantCulture));
    }
}
