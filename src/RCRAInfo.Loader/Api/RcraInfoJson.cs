using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// The one set of options every RCRAInfo response in this solution is read with.
/// </summary>
/// <remarks>
/// <para>
/// <b>One object rather than one per client, because the two had already drifted.</b>
/// <see cref="RcraInfoAuthClient"/> and <see cref="RcraInfoDataClient"/> each built their own
/// <c>new JsonSerializerOptions (JsonSerializerDefaults.Web)</c>, and only the auth client's carried
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> — yet <see cref="ApiError"/> is deserialized by
/// <b>both</b>, so the same EPA error body was read by two different sets of rules depending on which
/// client received it. That is not a bug anybody would notice until an error body failed to parse on one
/// path and parsed on the other.
/// </para>
/// <para>
/// <b>Nothing here is a preference; each entry answers something EPA was observed to do.</b>
/// <see cref="JsonSerializerDefaults.Web"/> for camelCase — and for case-insensitive property names,
/// which is the tolerance <c>LookupPayload</c> argues for at length and gets here for free —
/// <see cref="JsonNumberHandling"/> for a number quoted as a string, and
/// <see cref="RcraInfoTimestampConverter"/> for the offset form EPA's auth response actually uses; see
/// that class, which exists because of a live call rather than a reading of the spec.
/// </para>
/// <para>
/// <see cref="RcraInfoDateConverter"/> and <see cref="RcraInfoBooleanConverter"/> are the two entries
/// that answer something EPA has <i>not</i> been observed to do, and each says why. The summaries feed's
/// three <c>format: date</c> fields are the next place the pinned spec can turn out to disagree with the
/// service — G25 is open on exactly that question — and the stock <c>DateOnly</c> reader accepts the
/// documented form only. The quoted boolean is not a guess either: script 521 already maps
/// <c>N'true'</c>/<c>N'1'</c> by name in T-SQL, so the hazard was accepted as real one layer down, and
/// leaving the loader intolerant of it means the payload is rejected before it ever reaches the T-SQL
/// that was written to cope.
/// </para>
/// <para>
/// <b>Deliberately not used by <c>LookupPayload</c>.</b> That reader binds case-insensitively rather than
/// camelCase-first, and <c>LookupElement</c> has no date property to convert — its eleven properties are
/// strings, flags and an integer, and <c>build/check_lookup_catalog.py</c> holds that against the spec. If
/// a date ever appears there, the guardrail reports the new property and this is the options object to
/// reach for.
/// </para>
/// </remarks>
public static class RcraInfoJson
{
    /// <summary>Options for reading a RCRAInfo response body.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new RcraInfoTimestampConverter(),
            new RcraInfoDateConverter(),
            new RcraInfoBooleanConverter(),
        },
    };
}
