using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCRAInfo.Data;

/// <summary>
/// The one and only JSON configuration used to build a set parameter. Every payload-taking
/// procedure in this database is fed through here.
/// </summary>
/// <remarks>
/// <para>
/// G32 made every set parameter a single <c>NVARCHAR (MAX)</c> JSON string shredded with
/// <c>OPENJSON</c>. That decision moves one specific risk out of the database and into this file:
/// <b>SQL Server matches JSON property names case-sensitively, regardless of database
/// collation, and a path that matches nothing is not an error.</b> It shreds to <c>NULL</c>. So a
/// serializer configured with PascalCase somewhere in the solution would not fail, would not warn,
/// and would merge a batch of empty columns while reporting success.
/// </para>
/// <para>
/// That is why there is exactly one <see cref="JsonSerializerOptions"/> here, why it is
/// <c>private</c>, and why nothing in this project accepts one from a caller. The procedures read
/// <c>'$.handlerId'</c>, <c>'$.apiErrorDate'</c>, <c>'$.sortOrder'</c> — camelCase, matching EPA's
/// own payload — so the policy is <see cref="JsonNamingPolicy.CamelCase"/> and a test asserts it
/// against the property names actually named in scripts 400 and 520 through 523.
/// </para>
/// <para>
/// <b>Dates are normalised to UTC here, not at the call site.</b> <c>DATETIME2</c> accepts an ISO
/// 8601 string with an offset and then <i>discards</i> the offset rather than applying it —
/// measured, not assumed — so a <see cref="DateTimeOffset"/> written as <c>-05:00</c> would land in
/// a column named <c>…DateUtc</c> as a local wall-clock reading five hours wrong. The converter
/// below converts before it writes. Payload types deliberately use
/// <see cref="DateTimeOffset"/> and <see cref="DateOnly"/> and never <see cref="DateTime"/>,
/// because a <see cref="DateTimeKind.Unspecified"/> value has no answer to this question at all.
/// </para>
/// </remarks>
public static class PayloadJson
{
    private static readonly JsonSerializerOptions Options = new ()
    {
        // The whole point of this file. See the class remarks.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Omitting a null property is equivalent to sending JSON null: both JSON_VALUE and
        // OPENJSON ... WITH yield NULL for a path that is absent and for one whose value is null.
        // Since they are equivalent, the smaller payload wins -- a 500-element batch of 210-field
        // handlers is a large NVARCHAR (MAX) and most fields are empty for most handlers.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Indentation would be several megabytes of whitespace crossing the wire per batch.
        WriteIndented = false,

        Converters = { new UtcDateTimeOffsetConverter () },
    };

    /// <summary>
    /// Serialises a set of payload elements into the JSON array that a procedure's
    /// <c>NVARCHAR (MAX)</c> parameter expects, together with the facts about it that get logged.
    /// </summary>
    /// <typeparam name="T">A payload element type from the <c>Payloads</c> folder.</typeparam>
    /// <param name="elements">The elements. May be empty; an empty array is a documented no-op in
    /// every procedure that takes one, and is not an error.</param>
    /// <param name="maxElements">Refuse a batch larger than this. See
    /// <see cref="RCRAInfoDataOptions.MaxPayloadElements"/>.</param>
    /// <returns>The JSON, its SHA-256, and the element count.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="elements"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The batch exceeds
    /// <paramref name="maxElements"/>.</exception>
    /// <exception cref="InvalidOperationException">The result is not a JSON array. This cannot
    /// happen for a well-formed element type and is checked anyway, because it is the one failure
    /// whose database-side symptom is silence: <c>OPENJSON</c> over a bare object enumerates its
    /// properties instead of its elements, so every path misses and the batch merges nulls.
    /// See [R7] — the loader owns well-formedness and the procedure's <c>ISJSON</c> guard is the
    /// backstop, not the check.</exception>
    public static PayloadBatch Serialize<T> (IReadOnlyCollection<T> elements, int maxElements)
    {
        ArgumentNullException.ThrowIfNull (elements);
        ArgumentOutOfRangeException.ThrowIfLessThan (maxElements, 1);

        if (elements.Count > maxElements)
        {
            throw new ArgumentOutOfRangeException (
                nameof (elements), elements.Count,
                string.Format (
                    CultureInfo.InvariantCulture,
                    "A payload of {0} element(s) exceeds the configured maximum of {1}. Split the " +
                    "batch: every procedure that takes a payload is set-based and idempotent, so " +
                    "several smaller calls are equivalent to one large one.",
                    elements.Count, maxElements));
        }

        string json = JsonSerializer.Serialize (elements, Options);
        AssertJsonArray (json);

        return new PayloadBatch (json, Sha256 (json), elements.Count);
    }

    /// <summary>
    /// The lowercase hex SHA-256 of a string, as stored in
    /// <c>logs.HandlerLoadStatus.PayloadSha256</c>.
    /// </summary>
    /// <remarks>
    /// One implementation, because the column is 64 characters of <c>NVARCHAR</c> and a second
    /// implementation differing only in case or in encoding would make every comparison miss —
    /// which reads as "this handler changed" on every single run.
    /// </remarks>
    /// <param name="value">The text to hash. UTF-8 is the encoding; it is stated here rather than
    /// left to a default so the hash of a given handler payload is the same everywhere.</param>
    /// <returns>64 lowercase hex characters.</returns>
    public static string Sha256 (string value)
    {
        ArgumentNullException.ThrowIfNull (value);
        return Convert.ToHexStringLower (SHA256.HashData (Encoding.UTF8.GetBytes (value)));
    }

    /// <summary>
    /// Confirms the text is a well-formed JSON <i>array</i> — the .NET-side equivalent of the
    /// procedures' <c>ISJSON (@Payload, ARRAY) = 1</c> guard.
    /// </summary>
    /// <param name="json">Candidate JSON.</param>
    /// <exception cref="InvalidOperationException">Not valid JSON, or not an array.</exception>
    public static void AssertJsonArray (string json)
    {
        ArgumentNullException.ThrowIfNull (json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse (json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException (
                "A payload parameter was not valid JSON. Nothing was sent to the database: a " +
                "malformed payload shreds to zero rows, and a procedure that merged zero rows " +
                "would report success.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException (
                    string.Format (
                        CultureInfo.InvariantCulture,
                        "A payload parameter was valid JSON but its root is {0}, not an array. " +
                        "Every payload-taking procedure enumerates with OPENJSON, which over a " +
                        "bare object enumerates its PROPERTIES -- so every path would miss and " +
                        "the batch would merge nulls without erroring. Send a one-element array " +
                        "even for a single record.",
                        document.RootElement.ValueKind));
            }
        }
    }

    /// <summary>
    /// Writes a <see cref="DateTimeOffset"/> as UTC with a <c>Z</c> suffix and seven fractional
    /// digits, matching <c>DATETIME2</c>'s precision exactly.
    /// </summary>
    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        // Seven digits because DATETIME2's default scale is 7. Writing more would be silently
        // rounded by the engine; writing fewer would lose precision that the column can hold, and
        // a hash comparison over a round-tripped value would then never match.
        private const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        public override DateTimeOffset Read (
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset ();

        public override void Write (
            Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull (writer);
            writer.WriteStringValue (
                value.ToUniversalTime ().ToString (Format, CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>One serialised payload, and the two facts about it that are safe to log.</summary>
/// <param name="Json">The JSON array, bound to an <c>NVARCHAR (MAX)</c> parameter.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of <paramref name="Json"/>.</param>
/// <param name="ElementCount">Number of elements in the array.</param>
/// <remarks>
/// AR8 forbids a payload from reaching <c>logs.ExecutionLog</c>, and every payload-taking procedure
/// excludes its payload parameter from <c>@KeyParameters</c> by name. A count and a digest are the
/// permitted substitutes: neither can be turned back into a contact name.
/// </remarks>
public readonly record struct PayloadBatch (string Json, string Sha256, int ElementCount);
