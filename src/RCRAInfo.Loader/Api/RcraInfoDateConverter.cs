using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Reads the <c>format: date</c> fields RCRAInfo publishes, and reads them whether the live service sends
/// a bare date or a full timestamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is <see cref="RcraInfoTimestampConverter"/>'s lesson applied before it costs anything.</b> That
/// converter exists because the pinned spec and the live service disagreed about a date format and the
/// suite could not see it: every test had been written from the spec, so the spec and the tests agreed
/// while the spec and EPA did not. The three date fields on <c>HandlerSourceSummary</c> —
/// <c>receivedDate</c>, <c>createdDate</c>, <c>updatedDate</c> — are the next place that can happen. All
/// three are declared <c>format: date</c> with the example <c>2020-01-01</c>, and <b>G25 is open</b>: the
/// day-granularity of the summaries window is a reading of the spec, not a measurement. So this converter
/// accepts the documented form <i>and</i> the timestamp forms, rather than making a load fail on the first
/// live call because EPA sends the time of day as well.
/// </para>
/// <para>
/// <b>A timestamp is reduced to the date as written, not to the date in UTC.</b> That distinction is the
/// only subtle line here and it is worth a sentence: <c>2020-01-01T20:00:00-0500</c> is 2020-01-02 in UTC.
/// Shifting it would move a summary out of the window that returned it, and — because the load's watermark
/// advances by window — a version whose date shifted past the window end is a version the next run does not
/// ask for either. A silent one-day hole in the mirror, from a timezone conversion applied to a field EPA
/// documents as having no time at all. So the offset is dropped rather than applied.
/// </para>
/// <para>
/// <b>An unreadable value throws</b>, for <see cref="RcraInfoTimestampConverter"/>'s reason: the caller
/// turns it into one reportable problem naming a JSON path, which is a fact somebody acts on, where a
/// silent <c>null</c> would be a summaries window that looks complete and is not.
/// </para>
/// <para>
/// <b>No value is ever put in a message here.</b> The rule is the solution's, not the field's — see
/// <c>LookupPayload</c> and <see cref="RcraInfoTimestampConverter"/>. A character count is enough to tell
/// a bare date from a timestamp from something else entirely, which is the whole diagnostic question.
/// </para>
/// </remarks>
public sealed class RcraInfoDateConverter : JsonConverter<DateOnly>
{
    /// <summary>The one form this converter writes, which is the form the spec documents.</summary>
    private const string IsoDate = "yyyy-MM-dd";

    /// <inheritdoc/>
    public override DateOnly Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                "A RCRAInfo date must be a JSON string. The value is not reported here; the caller "
                + "reports the JSON path, which is what identifies the field.");
        }

        string? text = reader.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException(
                "A RCRAInfo date was present but empty. An empty string is not an absent field: the "
                + "field is there, so something produced it, and a guessed date on a summary decides "
                + "which window the version belongs to.");
        }

        // The documented form first. DateOnly.TryParse refuses anything carrying a time, so the two
        // branches cannot both match and the order is not load-bearing -- it is just the common case first.
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            return date;
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            // DateTimeOffset.DateTime, not UtcDateTime. See the class remarks: converting first would move
            // a late-evening summary into the next day and out of the window that returned it.
            return DateOnly.FromDateTime(parsed.DateTime);
        }

        throw new JsonException(
            string.Format(
                CultureInfo.InvariantCulture,
                "A RCRAInfo date of {0} character(s) is not a date this client can read. The documented "
                + "form (yyyy-MM-dd), RFC 3339 timestamps and ISO 8601 basic-offset timestamps (+0000) "
                + "are all accepted, so a value reaching here is a fourth form. The value itself is not "
                + "reported -- fetch one summaries window and inspect the body directly.",
                text.Length));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString(IsoDate, CultureInfo.InvariantCulture));
    }
}
