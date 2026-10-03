using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using RCRAInfo.Data.Payloads;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Reads one <c>/lookup/hd/*</c> response body into the elements <c>dbo.uspRefreshLookupSet</c> takes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only place in the solution that deserializes a RCRAInfo response.</b>
/// <c>ApiFetchResult.Payload</c> is the body verbatim as text and says why: handler payloads are stored as
/// <c>NVARCHAR (MAX)</c> and shredded in T-SQL, so parsing them here would mean deserializing 377 fields to
/// re-serialize them. A lookup is the exception, and not by preference — script 523's <c>@Elements</c> is
/// <see cref="LookupElement"/>-shaped, one flat array for all 23 lists, so the nesting EPA publishes
/// (<c>counties</c> inside a state district, <c>episodicType</c> inside a project) has to be understood on
/// this side before it can be sent. Passing the body through untouched is not available.
/// </para>
/// <para>
/// <b>Reading is case-insensitive and writing is not, and the asymmetry is the point.</b>
/// <c>PayloadJson</c> writes with <see cref="JsonNamingPolicy.CamelCase"/> because <c>OPENJSON</c> matches
/// property names case-sensitively whatever the collation, and a path that matches nothing shreds to
/// <c>NULL</c> without erroring — that is a hazard this project controls and pins. What EPA sends is not
/// ours to pin: the spec says camelCase, and this reader binds either casing rather than mirroring a
/// national code list as a column of nulls because a service changed a letter.
/// </para>
/// <para>
/// <b>A property EPA sends that <see cref="LookupElement"/> has no home for is reported rather than
/// dropped.</b> <see cref="JsonSerializer"/> ignores unmapped members silently, which for a code list is
/// the quiet kind of wrong: the codes still arrive, the refresh still succeeds, and a new field that
/// changes what a code <i>means</i> is discarded on every run forever.
/// <c>build/check_lookup_catalog.py</c> holds the same agreement against the pinned spec at build time;
/// this reader catches the case the guardrail cannot see, which is the live service having moved on from
/// the spec that was pinned.
/// </para>
/// </remarks>
public static class LookupPayload
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // See the class remarks: loose on the way in, strict on the way out.
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Every property name <see cref="LookupElement"/> can bind, in the casing EPA publishes.
    /// </summary>
    /// <remarks>
    /// Derived from the type rather than written out, so it cannot drift from what the deserializer will
    /// actually accept — a hand-written list would report a property as unexpected while binding it, or the
    /// reverse, and the reverse is the one that loses data.
    /// </remarks>
    private static readonly HashSet<string> KnownProperties =
        new(
            typeof(LookupElement)
                .GetProperties()
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a lookup response body.</summary>
    /// <param name="payload">
    /// The body, verbatim — <c>ApiFetchResult.Payload</c>. Non-null exactly when the call succeeded.
    /// </param>
    /// <returns>The elements, any unexpected property names, and the problem when there was one.</returns>
    /// <remarks>
    /// Never throws for a bad payload. A lookup endpoint documents no <c>400</c> and no <c>404</c>, so an
    /// unreadable body is one of the few answers this stage can get and it has to be reportable rather than
    /// exceptional — the caller records it against the one list it belongs to and carries on with the
    /// other 22.
    /// </remarks>
    public static LookupPayloadRead Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return LookupPayloadRead.Rejected(
                "the response body was empty. A lookup call classified as Succeeded is guaranteed to "
                + "carry one, so reaching this means the classification and the body disagree.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            // The path and position, never exception.Message and never any part of the body. A code list
            // holds no secret, but this message reaches logs.LoadRun.FailureMessage, and the rule there
            // does not have an exception for payloads that happen to be harmless.
            return LookupPayloadRead.Rejected(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "the response body is not valid JSON (at {0}, byte {1}).",
                    exception.Path ?? "$",
                    exception.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "unknown"));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return LookupPayloadRead.Rejected(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the response body is valid JSON but its root is {0}, not an array. Every "
                        + "/lookup/hd endpoint returns an array in the pinned spec.",
                        document.RootElement.ValueKind));
            }

            SortedSet<string> unexpected = new(StringComparer.Ordinal);
            CollectUnexpectedProperties(document.RootElement, unexpected);

            LookupElement[]? elements;
            try
            {
                elements = JsonSerializer.Deserialize<LookupElement[]>(payload, ReadOptions);
            }
            catch (JsonException exception)
            {
                return LookupPayloadRead.Rejected(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the response body is a JSON array but an element does not fit a lookup code "
                        + "(at {0}).",
                        exception.Path ?? "$"));
            }

            if (elements is null)
            {
                return LookupPayloadRead.Rejected("the response body deserialized to nothing.");
            }

            string? codeProblem = FindMissingCode(elements);

            return codeProblem is null
                ? new LookupPayloadRead(elements, [.. unexpected], null)
                : LookupPayloadRead.Rejected(codeProblem);
        }
    }

    /// <summary>
    /// Walks the payload for property names <see cref="LookupElement"/> cannot bind, including inside the
    /// two nested shapes.
    /// </summary>
    private static void CollectUnexpectedProperties(JsonElement element, SortedSet<string> unexpected)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectUnexpectedProperties(item, unexpected);
                }

                break;

            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!KnownProperties.Contains(property.Name))
                    {
                        // A property NAME, which is schema, not data -- safe to log and the only thing
                        // that makes the report actionable. No value is ever collected.
                        unexpected.Add(property.Name);
                    }

                    // Recurse regardless: episodicType and counties are LookupElement-shaped, so a new
                    // property one level down is exactly as invisible as one at the top.
                    CollectUnexpectedProperties(property.Value, unexpected);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The one field-level check worth making here rather than leaving to the procedure.
    /// </summary>
    /// <remarks>
    /// <c>code</c> is <c>required: true</c> in all 23 definitions and is the <c>MERGE</c> key in script 523.
    /// The procedure refuses an element without one, so this is not the guard — it is the guard that can
    /// say <i>which</i> element and can say it without a database round-trip that fails a whole list.
    /// </remarks>
    private static string? FindMissingCode(LookupElement[] elements)
    {
        for (int index = 0; index < elements.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(elements[index].Code))
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "element {0} of {1} has no code. Script 523 merges on the code, so an element "
                    + "without one cannot be matched, retired or stored.",
                    index,
                    elements.Length);
            }

            IReadOnlyList<LookupElement>? counties = elements[index].Counties;
            if (counties is null)
            {
                continue;
            }

            for (int child = 0; child < counties.Count; child++)
            {
                if (string.IsNullOrWhiteSpace(counties[child].Code))
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "county {0} nested in element {1} of {2} has no code.",
                        child,
                        index,
                        elements.Length);
                }
            }
        }

        return null;
    }
}

/// <summary>The result of reading one lookup response body.</summary>
/// <param name="Elements">The codes, empty when <paramref name="Problem"/> is set.</param>
/// <param name="UnexpectedProperties">
/// Property names EPA sent that <see cref="LookupElement"/> has no home for, sorted and de-duplicated.
/// Empty in a run against the pinned spec. Non-empty means the elements were still read — every code is
/// present — and something about them is being discarded.
/// </param>
/// <param name="Problem">
/// Why the body could not be used, or <see langword="null"/>. Safe to log: it names a JSON path, a value
/// kind or an element index, never a value from the body.
/// </param>
public sealed record LookupPayloadRead(
    IReadOnlyList<LookupElement> Elements,
    IReadOnlyList<string> UnexpectedProperties,
    string? Problem)
{
    /// <summary>Whether the body yielded usable elements.</summary>
    public bool IsReadable => Problem is null;

    /// <summary>A rejection carrying no elements.</summary>
    /// <param name="problem">Why, in a form safe to log.</param>
    /// <returns>The rejection.</returns>
    internal static LookupPayloadRead Rejected(string problem) => new([], [], problem);
}
