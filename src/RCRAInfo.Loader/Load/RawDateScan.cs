using System.Globalization;
using System.Text.Json;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// Every date-shaped property in a JSON body, with the raw text EPA sent for it — the G25 answer, for a
/// payload of any shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because no column retains a raw body.</b> <c>dbo.HandlerSource</c> keeps mapped columns and
/// <c>logs.HandlerLoadStatus</c> keeps a hash, so a format not written down at the moment of the call is a
/// format unobserved permanently. That is the whole reason G25 cannot be answered retrospectively, and the
/// reason a probe is the only instrument that can answer it.
/// </para>
/// <para>
/// <b>Read from the body rather than from the bound objects, which is the entire point.</b> A
/// <see cref="DateOnly"/> that came out of <c>RcraInfoDateConverter</c> proves the converter accepted
/// <i>something</i>; it cannot say what. The standing counter-example is the auth endpoint's
/// <c>expiration</c>: it parsed cleanly in every offline test and carried a <c>+0000</c> offset the pinned
/// specification does not describe, and it was found by failing ([R33]).
/// </para>
/// <para>
/// <b>Deliberately not shared with <see cref="SummaryProbe"/>, which has its own narrower version.</b> That
/// one assumes an array root, reports bare property names, and is covered by tests asserting exactly that
/// output. Making it general would change the shape of a report that has already answered its question for the
/// summaries feed, to no benefit — G25 for that feed is closed. The duplication is bounded and the alternative
/// was a refactor of a working answer.
/// </para>
/// <para>
/// <b>Property names, JSON paths and date values only — never an arbitrary value.</b> The selection rule is a
/// name ending in <c>Date</c>, which is what keeps AR8 satisfied by construction rather than by review: a
/// contact name, a phone number and an email address cannot match it, so no walk of a handler payload can
/// carry one into a console line an operator may paste into a ticket. A date of record is not personal
/// information.
/// </para>
/// </remarks>
public static class RawDateScan
{
    /// <summary>The suffix that marks a property as one this scan reports.</summary>
    /// <remarks>
    /// A suffix rather than a fixed list, because the point of scanning a payload nobody has read raw is to
    /// find the date fields <i>nobody named</i> — including any EPA adds after the specification was pinned.
    /// A fixed list can only confirm what is already known.
    /// </remarks>
    public const string DateSuffix = "Date";

    /// <summary>How many distinct raw values to keep per path.</summary>
    /// <remarks>
    /// The question is what <i>shapes</i> EPA sends, and a body carrying one shape across four hundred rows
    /// answers it as completely as one carrying it across four — while an uncapped list would bury the answer
    /// in the output it is printed to.
    /// </remarks>
    public const int MaxSamplesPerPath = 8;

    /// <summary>
    /// How many further values to keep past <see cref="MaxSamplesPerPath"/> when each carries a shape not
    /// already represented.
    /// </summary>
    /// <remarks>
    /// <b>[R46] The cap has to yield to a new shape, or it can hide the one thing the probe is for.</b> A cap on
    /// distinct <i>values</i> means a handler whose first eight dates are ordinary and whose ninth carries a
    /// <c>+0000</c> offset reports as entirely ordinary — the exact failure [R33] describes, reintroduced by the
    /// instrument built to detect it. `MD0570024000` came within one value of demonstrating it: its summaries
    /// body named 23 versions and the walk kept 8. So a value whose shape is new is always kept, and only a
    /// repeat of a shape already seen is dropped.
    /// </remarks>
    public const int MaxExtraShapesPerPath = 8;

    /// <summary>How many distinct paths to report before the scan stops adding new ones.</summary>
    /// <remarks>
    /// A handler payload has 18 child collections, so an uncapped scan of a drifted payload could name more
    /// paths than an operator will read. The cap is generous enough that reaching it is itself a finding.
    /// </remarks>
    public const int MaxPaths = 64;

    /// <summary>How deep to recurse.</summary>
    /// <remarks>
    /// The deepest date in a handler payload sits one collection below the root. Eight is far past that and
    /// exists only so a cyclic or pathologically nested body cannot turn a diagnostic into a stack overflow.
    /// </remarks>
    public const int MaxDepth = 8;

    /// <summary>Scans one body.</summary>
    /// <param name="payload">The raw response body. A null or unparseable body yields an empty scan.</param>
    /// <returns>
    /// Distinct raw JSON text per path, in path order. Empty when the body has no date-shaped property, which
    /// is itself an answer and not a failure.
    /// </returns>
    /// <remarks>
    /// <b>Never throws.</b> A <see cref="JsonException"/> is swallowed and the scan returns what it had,
    /// because the caller has already read the same body through a payload reader that reports its own
    /// diagnosed problem — and a throw from this second parse would replace a diagnosed refusal with an
    /// undiagnosed one.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Scan(string? payload)
    {
        Dictionary<string, List<string>> samples = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return Freeze(samples);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            Walk(document.RootElement, "$", samples, depth: 0);
        }
        catch (JsonException)
        {
            // Swallowed, and only here. See the remarks above: the caller diagnoses the body.
            return Freeze(samples);
        }

        return Freeze(samples);
    }

    /// <summary>
    /// Whether two scans agree on the <b>shapes</b> they saw for the paths that share a leaf property name.
    /// </summary>
    /// <param name="first">One scan. Its shapes are printed first in every entry.</param>
    /// <param name="second">The other.</param>
    /// <returns>
    /// One entry per shared leaf property name whose <b>shapes</b> differ, in name order, each naming the field
    /// and both sides' shapes. Empty means the two bodies wrote every shared date field in the same shape.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>This is the comparison G25 actually needs, and it is why the source probe reads both feeds in one
    /// errand.</b> The summaries feed and the record-detail feed are already known to differ in date shape, so
    /// an answer for one does not transfer to the other. Comparing them directly turns "we have two lists of
    /// strings" into "these fields disagree, and here is the pair" — which is the finding, if there is one.
    /// Compared on the leaf name because the paths cannot match: one body is an array of summaries and the
    /// other an object.
    /// </para>
    /// <para>
    /// <b>[R46] It compares shapes, and the first version compared raw values — which made a false positive
    /// unavoidable on any handler with more than one version.</b> The two feeds are asked different questions:
    /// <c>?handlerId=</c> returns every version of the handler, and the record-detail call returns one. So the
    /// value sets are a superset and a subset by construction, never equal, and the very first live run reported
    /// <c>createdDate</c>, <c>receivedDate</c> and <c>updatedDate</c> as differing "in shape" when all six values
    /// on both sides were bare <c>"yyyy-MM-dd"</c>. Nothing in the test suite could have caught it: every case
    /// compared bodies whose shared field held the <i>same</i> value, so no test distinguished a difference of
    /// value from a difference of shape. That is [R43]'s lesson arriving a third time — a test asserts what its
    /// author already believed — and it is why the entries now carry both shapes, so the claim can be checked
    /// from the line that makes it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Disagreements(
        IReadOnlyDictionary<string, IReadOnlyList<string>> first,
        IReadOnlyDictionary<string, IReadOnlyList<string>> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        Dictionary<string, SortedSet<string>> left = ShapesByLeafName(first);
        Dictionary<string, SortedSet<string>> right = ShapesByLeafName(second);

        return [.. left.Keys
            .Where(right.ContainsKey)
            .Where(name => !left[name].SetEquals(right[name]))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => string.Concat(
                name,
                ": ",
                string.Join(" or ", left[name]),
                " vs ",
                string.Join(" or ", right[name])))];
    }

    /// <summary>The shape of one raw JSON token, as a format rather than as a value.</summary>
    /// <param name="rawJsonText">The raw text <c>GetRawText</c> returned, quotes included.</param>
    /// <returns>
    /// A format description such as <c>"yyyy-MM-dd"</c>, or <c>null</c>, <c>number</c>, <c>empty text</c>,
    /// <c>object</c>, <c>array</c>, or <c>text(len=N)</c> for anything that is not date-shaped.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Every digit becomes <c>d</c> and only eight structural characters survive</b> — <c>- : . T Z t z +</c>
    /// and a space. Anything else and the whole token is reported as <c>text(len=N)</c>, a character count and
    /// not the text. So AR8 holds here for a second independent reason: even if a date-named property somehow
    /// carried free text, this cannot print it. That is script <c>523</c>'s rule for a width refusal — name the
    /// property and its length, never reproduce the value — applied to a console line.
    /// </para>
    /// <para>
    /// A format string rather than the raw mask for the shapes anyone will actually see, because
    /// <c>"yyyy-MM-dd"</c> is legible in a pasted ticket and <c>dddd-dd-dd</c> needs decoding. Unrecognised
    /// masks are printed as the mask, which is still a shape and still safe.
    /// </para>
    /// </remarks>
    public static string ShapeOf(string rawJsonText)
    {
        ArgumentNullException.ThrowIfNull(rawJsonText);

        if (rawJsonText.Length == 0)
        {
            return "empty text";
        }

        switch (rawJsonText[0])
        {
            case 'n' when string.Equals(rawJsonText, "null", StringComparison.Ordinal):
                return "null";

            case 't' or 'f' when rawJsonText is "true" or "false":
                return "boolean";

            // An object or an array under a date-named property is itself the finding. Its contents are never
            // reported: this is the one branch where the token could hold something that is not a date.
            case '{':
                return "object";

            case '[':
                return "array";

            case '"':
                break;

            default:
                return "number";
        }

        string inner = rawJsonText.Length >= 2 ? rawJsonText[1..^1] : string.Empty;

        if (inner.Length == 0)
        {
            return "empty text";
        }

        Span<char> mask = inner.Length <= 64 ? stackalloc char[inner.Length] : new char[inner.Length];

        for (int index = 0; index < inner.Length; index++)
        {
            char character = inner[index];

            if (character is >= '0' and <= '9')
            {
                mask[index] = 'd';

                continue;
            }

            if (character is '-' or ':' or '.' or 'T' or 'Z' or 't' or 'z' or '+' or ' ')
            {
                mask[index] = character;

                continue;
            }

            // Not date-shaped at all. A length, never the text.
            return string.Create(
                CultureInfo.InvariantCulture,
                $"text(len={inner.Length})");
        }

        string shape = new(mask);

        return KnownShapes.TryGetValue(shape, out string? format)
            ? string.Concat("\"", format, "\"")
            : string.Concat("\"", shape, "\"");
    }

    /// <summary>The leaf property name of a path, for a comparison across two differently-shaped bodies.</summary>
    /// <param name="path">A path this scan produced.</param>
    /// <returns>The text after the last dot, or the whole path when it has none.</returns>
    public static string LeafName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int dot = path.LastIndexOf('.');

        return dot >= 0 && dot < path.Length - 1 ? path[(dot + 1)..] : path;
    }

    // The masks worth naming as the format they are. Anything absent is printed as its mask, which is legible
    // enough for an unexpected shape and is the case that wants looking at rather than reading fluently.
    private static readonly Dictionary<string, string> KnownShapes = new(StringComparer.Ordinal)
    {
        ["dddd-dd-dd"] = "yyyy-MM-dd",
        ["dddd-dd-ddTdd:dd:dd"] = "yyyy-MM-ddTHH:mm:ss",
        ["dddd-dd-ddTdd:dd:dd.ddd"] = "yyyy-MM-ddTHH:mm:ss.fff",
        ["dddd-dd-ddTdd:dd:ddZ"] = "yyyy-MM-ddTHH:mm:ssZ",
        ["dddd-dd-ddTdd:dd:dd.dddZ"] = "yyyy-MM-ddTHH:mm:ss.fffZ",
        ["dddd-dd-ddTdd:dd:dd.ddd+dddd"] = "yyyy-MM-ddTHH:mm:ss.fff+ZZZZ",
        ["dddd-dd-ddTdd:dd:dd.ddd+dd:dd"] = "yyyy-MM-ddTHH:mm:ss.fff+ZZ:ZZ",
        ["dddd-dd-dd dd:dd:dd"] = "yyyy-MM-dd HH:mm:ss",
        ["dd-dd-dddd"] = "MM-dd-yyyy",
    };

    private static Dictionary<string, SortedSet<string>> ShapesByLeafName(
        IReadOnlyDictionary<string, IReadOnlyList<string>> scan)
    {
        Dictionary<string, SortedSet<string>> byName = new(StringComparer.Ordinal);

        foreach ((string path, IReadOnlyList<string> values) in scan)
        {
            string name = LeafName(path);

            if (!byName.TryGetValue(name, out SortedSet<string>? seen))
            {
                seen = new SortedSet<string>(StringComparer.Ordinal);
                byName[name] = seen;
            }

            foreach (string value in values)
            {
                seen.Add(ShapeOf(value));
            }
        }

        return byName;
    }

    private static void Walk(
        JsonElement element,
        string path,
        Dictionary<string, List<string>> samples,
        int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    // The collapsed array notation keeps 18 child collections from producing 18 near-identical
                    // paths: every element of one array reports under the same path, and the sample cap then
                    // holds the distinct shapes rather than the distinct rows.
                    string child = string.Concat(path, ".", property.Name);

                    if (property.Name.EndsWith(DateSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        Record(child, property.Value, samples);
                    }

                    Walk(property.Value, child, samples, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, string.Concat(path, "[]"), samples, depth + 1);
                }

                break;

            default:
                // A scalar reached by recursion rather than through a date-named property. Nothing to record:
                // the selection rule is the property name, and this element has none of its own.
                break;
        }
    }

    private static void Record(
        string path,
        JsonElement value,
        Dictionary<string, List<string>> samples)
    {
        if (!samples.TryGetValue(path, out List<string>? seen))
        {
            if (samples.Count >= MaxPaths)
            {
                return;
            }

            seen = [];
            samples[path] = seen;
        }

        // GetRawText rather than GetString, so a numeric or null value is reported as what it is instead of as
        // an empty string. A property present with a JSON null and a property absent are different findings,
        // and the first is invisible to a converter that maps both to default.
        string raw = value.GetRawText();

        if (seen.Contains(raw, StringComparer.Ordinal))
        {
            return;
        }

        // [R46] A value carrying a shape not yet seen is kept even past the sample cap, because the shapes are
        // the answer and the values are only the evidence for it. Dropping the ninth distinct value of a handler
        // with 23 versions is free; dropping it when it is the only one with an offset would hide precisely the
        // [R33] defect this probe exists to find.
        string shape = ShapeOf(raw);
        bool shapeIsNew = !seen.Any(kept => string.Equals(ShapeOf(kept), shape, StringComparison.Ordinal));

        int ceiling = shapeIsNew ? MaxSamplesPerPath + MaxExtraShapesPerPath : MaxSamplesPerPath;

        if (seen.Count < ceiling)
        {
            seen.Add(raw);
        }
    }

    // Returns the concrete Dictionary rather than the interface because CA1859 is an error here and the caller
    // widens it anyway. Every exit from Scan goes through this, so the lists cannot escape as the mutable
    // List<string> they are built as.
    private static Dictionary<string, IReadOnlyList<string>> Freeze(
        Dictionary<string, List<string>> samples) =>
        samples
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.Ordinal);
}
