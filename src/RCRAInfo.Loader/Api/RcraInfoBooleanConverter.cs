using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Reads a RCRAInfo boolean whether it arrives as a JSON <c>true</c>/<c>false</c> or as a quoted one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The quoted form is not a hypothetical this project invented for symmetry — the database already
/// handles it.</b> <c>HandlerVersionElement.CurrentRecord</c> records why: script 521 maps the strings
/// <c>true</c>, <c>false</c>, <c>1</c> and <c>0</c> by name, because <c>TRY_CAST (N'true' AS BIT)</c>
/// returns <c>NULL</c> and a value read as "not current" by accident <b>demotes a version EPA calls
/// current</b>. Half of that defence sitting in T-SQL and the other half absent from the loader is the
/// arrangement worth avoiding: the loader is upstream, so it fails first, and a <see cref="JsonException"/>
/// on <c>currentRecord</c> rejects a whole summaries window.
/// </para>
/// <para>
/// <b>It throws for anything outside that set, and that is the point rather than a limitation.</b> A
/// converter that fell back to <see langword="false"/> would turn "a form nobody anticipated" into "this
/// version is not the current record", silently, on every row — the exact failure the T-SQL refuses to
/// make. Tolerance means accepting more <i>spellings</i> of a known value, never guessing at an unknown
/// one.
/// </para>
/// <para>
/// No value reaches a message, for the reason <see cref="RcraInfoTimestampConverter"/> gives; the caller
/// reports the JSON path, which is what identifies the field.
/// </para>
/// </remarks>
public sealed class RcraInfoBooleanConverter : JsonConverter<bool>
{
    /// <inheritdoc/>
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;

            case JsonTokenType.False:
                return false;

            case JsonTokenType.String:
                return ReadQuoted(reader.GetString());

            default:
                throw new JsonException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "A RCRAInfo flag must be a JSON boolean or a quoted one; this is {0}. The value "
                        + "is not reported here -- the caller reports the JSON path.",
                        reader.TokenType));
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // One form out, as everywhere else in this solution: a real JSON boolean.
        writer.WriteBooleanValue(value);
    }

    private static bool ReadQuoted(string? text)
    {
        // The same four spellings script 521 maps by name, and no others. Ordinal-ignore-case rather than
        // culture-aware: these are protocol tokens, and a culture-sensitive comparison of "true" is a
        // Turkish-I defect waiting for a server with a different locale.
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            return true;
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            return false;
        }

        throw new JsonException(
            string.Format(
                CultureInfo.InvariantCulture,
                "A RCRAInfo flag arrived as a quoted string of {0} character(s) that is not true, false, "
                + "1 or 0. It is refused rather than read as false: on currentRecord, guessing false "
                + "demotes the version EPA calls the current record. The value is not reported here.",
                text?.Length ?? 0));
    }
}
