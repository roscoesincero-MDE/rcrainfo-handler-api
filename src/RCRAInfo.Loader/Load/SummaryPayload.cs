using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using RCRAInfo.Loader.Api;

namespace RCRAInfo.Loader.Load;

/// <summary>
/// One element of the <c>GET /hd/sources/summaries</c> response: a handler version, and enough about it
/// to decide whether to fetch it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the change feed.</b> There is no other endpoint that says which handler versions exist, so
/// every fetch this loader makes against <c>/hd/sources/{handlerId}/{sourceType}/{sequence}</c> and against
/// <c>/hd/other-ids</c> traces back to a row of this shape. Nine of the eleven properties are
/// <c>required</c> in the pinned spec; all eleven are declared nullable or defaulted here anyway, because
/// a reader that throws on a missing field turns "EPA changed one field" into "the window returned
/// nothing", and <see cref="SummaryPayload"/> is the layer that decides which absences actually matter.
/// </para>
/// <para>
/// <b>The three dates are read by <see cref="RcraInfoDateConverter"/> and not by the stock reader</b>, and
/// <see cref="CurrentRecord"/> by <see cref="RcraInfoBooleanConverter"/>. Both of those exist for reasons
/// written out in their own files; the short version is that this project has already been caught once by
/// a date format the pinned spec did not predict.
/// </para>
/// </remarks>
public sealed class HandlerSourceSummary
{
    /// <summary>EPA's handler identifier. Part of the natural key; <c>required</c>.</summary>
    public string? HandlerId { get; init; }

    /// <summary>
    /// The state whose activity this is. <c>required</c>, and checked against the configured value by
    /// <see cref="SummaryWalk"/> rather than here — see that class on why the <i>answer</i> is verified
    /// and not just the request.
    /// </summary>
    public string? ActivityLocation { get; init; }

    /// <summary>The source-type code — <c>N</c>, <c>I</c> and so on. Part of the natural key.</summary>
    public string? SourceType { get; init; }

    /// <summary>EPA's version sequence within the source type. Part of the natural key.</summary>
    public int Sequence { get; init; }

    /// <summary>The date EPA received the handler's submission.</summary>
    public DateOnly? ReceivedDate { get; init; }

    /// <summary>The report cycle, for <c>B</c> source-type records. Optional in the spec.</summary>
    public int? ReportCycle { get; init; }

    /// <summary>The federal waste generator status.</summary>
    public string? FederalGeneratorStatus { get; init; }

    /// <summary>
    /// Whether EPA considers this version the current record. Feeds
    /// <c>dbo.uspReconcileCurrentRecord</c> (script 521).
    /// </summary>
    [JsonConverter(typeof(RcraInfoBooleanConverter))]
    public bool CurrentRecord { get; init; }

    /// <summary>Where the source record came from. Optional in the spec.</summary>
    public string? DataOrigin { get; init; }

    /// <summary>The date the version was last updated.</summary>
    public DateOnly? UpdatedDate { get; init; }

    /// <summary>The date the version was created.</summary>
    public DateOnly? CreatedDate { get; init; }

    /// <summary>The natural key this summary names.</summary>
    /// <returns>The version.</returns>
    /// <remarks>
    /// Only ever called after <see cref="SummaryPayload"/> has accepted the payload, which is what
    /// guarantees the two strings are non-blank — that check is the reader's, so this does not repeat it.
    /// </remarks>
    public HandlerVersion ToVersion() => new(HandlerId!, SourceType!, Sequence);
}

/// <summary>
/// Reads one <c>/hd/sources/summaries</c> response body into the handler versions it names.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the second place in the solution that deserializes a RCRAInfo response, and the exception
/// <c>LookupPayload</c> claimed to be the only one.</b> Its argument still holds for handler <i>detail</i>
/// payloads — 377 fields stored as <c>NVARCHAR (MAX)</c> and shredded in T-SQL, so parsing them here would
/// mean deserializing them to re-serialize them. It does not hold for a summary: nothing is <i>stored</i>
/// from this body at all. Its whole purpose is to decide what to fetch next, and a decision cannot be made
/// by passing bytes through.
/// </para>
/// <para>
/// <b>The root is a bare array with no envelope, and that is the finding the windowing design rests
/// on.</b> There is no <c>totalCount</c>, no <c>offset</c>, no <c>hasMore</c> — plan [R28]. So a response
/// carries no way to tell "these are all the versions in the window" from "this is as many as the service
/// felt like sending", and the only defence against the second is a window narrow enough that the first is
/// obviously true. That is why <see cref="SummaryWalk"/> reports a per-window count an operator can look
/// at, and why the count is worth looking at.
/// </para>
/// <para>
/// <b>Never throws for a bad payload.</b> Same contract as <c>LookupPayload</c> and for a sharper reason:
/// a rejected window must be reportable, because a window that failed is the one thing that must stop the
/// watermark from advancing. An exception here would be caught somewhere generic and the run would end as
/// "failed" without recording <i>which</i> window is missing.
/// </para>
/// <para>
/// <b>What counts as a problem is deliberately narrow: it is what makes an element unusable as a
/// key.</b> A blank <c>handlerId</c> or <c>sourceType</c>, or either one wider than the column that has to
/// hold it, means the run cannot name the version to fetch it, cannot journal it, and cannot merge it —
/// there is nothing to do but refuse. A missing <c>updatedDate</c>, by contrast, is <i>reported and
/// used</i>: the watermark advances by the window that was asked for, never by a date read out of a row,
/// so an absent date costs this loader nothing it relies on.
/// </para>
/// </remarks>
public static class SummaryPayload
{
    /// <summary>
    /// The widest <c>handlerId</c> the database can hold, from <c>logs.HandlerLoadStatus.HandlerId</c>.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to script 520, which shreds wide with <c>JSON_VALUE</c> and
    /// width-checks before writing for the reason G36 records: <c>OPENJSON ... WITH</c> truncates silently,
    /// and <b>a truncated handlerId names a different handler rather than none</b>. The procedure refuses
    /// it; this reader refuses it earlier and can say which element, without failing a flush that carries
    /// several hundred other versions.
    /// </remarks>
    public const int MaxHandlerIdLength = 12;

    /// <summary>The widest <c>sourceType</c> the database can hold. One character.</summary>
    public const int MaxSourceTypeLength = 1;

    /// <summary>Every property name <see cref="HandlerSourceSummary"/> can bind, in EPA's casing.</summary>
    /// <remarks>
    /// Derived from the type for <c>LookupPayload</c>'s reason: a hand-written list would eventually report
    /// a property as unexpected while binding it, or bind one while reporting nothing, and the second is
    /// the one that loses data silently.
    /// </remarks>
    private static readonly HashSet<string> KnownProperties =
        new(
            typeof(HandlerSourceSummary)
                .GetProperties()
                .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a summaries response body.</summary>
    /// <param name="payload">
    /// The body, verbatim — <c>ApiFetchResult.Payload</c>. Non-null exactly when the call succeeded.
    /// </param>
    /// <returns>The summaries, any unexpected property names, and the problem when there was one.</returns>
    public static SummaryPayloadRead Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return SummaryPayloadRead.Rejected(
                "the response body was empty. A summaries call classified as Succeeded is guaranteed to "
                + "carry one, so reaching this means the classification and the body disagree.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            // The path and the position, never exception.Message and never any part of the body. A
            // summaries body names regulated entities, so unlike a code list this one genuinely is data.
            return SummaryPayloadRead.Rejected(
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
                return SummaryPayloadRead.Rejected(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the response body is valid JSON but its root is {0}, not an array. "
                        + "/hd/sources/summaries returns a bare array in the pinned spec -- no envelope, "
                        + "no total, no paging (plan [R28]), and the windowing loop is built on that. An "
                        + "object here means the service now wraps the results, and the wrapper may say "
                        + "the response was truncated.",
                        document.RootElement.ValueKind));
            }

            SortedSet<string> unexpected = new(StringComparer.Ordinal);
            CollectUnexpectedProperties(document.RootElement, unexpected);

            HandlerSourceSummary[]? summaries;
            try
            {
                summaries = JsonSerializer.Deserialize<HandlerSourceSummary[]>(
                    payload, RcraInfoJson.Options);
            }
            catch (JsonException exception)
            {
                // exception.Message is dropped: System.Text.Json quotes the offending value in it, and the
                // two converters that can raise one here were written to report a character count instead.
                return SummaryPayloadRead.Rejected(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "the response body is a JSON array but an element does not fit a handler source "
                        + "summary (at {0}).",
                        exception.Path ?? "$"));
            }

            if (summaries is null)
            {
                return SummaryPayloadRead.Rejected("the response body deserialized to nothing.");
            }

            string? keyProblem = FindUnusableKey(summaries);

            return keyProblem is null
                ? new SummaryPayloadRead(summaries, [.. unexpected], null)
                : SummaryPayloadRead.Rejected(keyProblem);
        }
    }

    /// <summary>Walks the payload for property names <see cref="HandlerSourceSummary"/> cannot bind.</summary>
    /// <remarks>
    /// One level shallower than <c>LookupPayload</c>'s equivalent, because a summary is flat: the spec
    /// declares no nested object on it. The recursion is kept anyway, so that a nested shape appearing in a
    /// future revision is <i>reported</i> rather than walked past — the whole purpose of this sweep is to
    /// notice a change nobody predicted.
    /// </remarks>
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
                        // A property NAME, which is schema and not data. No value is ever collected.
                        unexpected.Add(property.Name);
                    }

                    CollectUnexpectedProperties(property.Value, unexpected);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The checks that decide whether an element can be used as a handler version at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported as an element index and a length, never a value. An index is enough to find the row in a
    /// body an operator can fetch again; the identifier of a regulated entity is not this loader's to put
    /// in a log the monitoring web application reads (AR8).
    /// </para>
    /// <para>
    /// <see cref="HandlerSourceSummary.Sequence"/> is checked for being negative and not for being zero.
    /// EPA's sequences are observed to start at 1, but nothing in the spec says so, and refusing a whole
    /// window over a value that is merely surprising is the wrong trade — a negative sequence, by contrast,
    /// cannot be a version number under any reading.
    /// </para>
    /// </remarks>
    private static string? FindUnusableKey(HandlerSourceSummary[] summaries)
    {
        for (int index = 0; index < summaries.Length; index++)
        {
            HandlerSourceSummary summary = summaries[index];

            if (string.IsNullOrWhiteSpace(summary.HandlerId))
            {
                return Problem(index, summaries.Length, "has no handlerId, so there is nothing to fetch");
            }

            if (summary.HandlerId.Length > MaxHandlerIdLength)
            {
                return Problem(
                    index,
                    summaries.Length,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "has a handlerId of {0} characters, wider than the {1} the database holds. It is "
                        + "refused rather than clipped: a truncated handlerId names a DIFFERENT handler "
                        + "rather than none (G36)",
                        summary.HandlerId.Length,
                        MaxHandlerIdLength));
            }

            if (string.IsNullOrWhiteSpace(summary.SourceType))
            {
                return Problem(index, summaries.Length, "has no sourceType, so its version cannot be named");
            }

            if (summary.SourceType.Length > MaxSourceTypeLength)
            {
                return Problem(
                    index,
                    summaries.Length,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "has a sourceType of {0} characters where the column holds {1}",
                        summary.SourceType.Length,
                        MaxSourceTypeLength));
            }

            if (summary.Sequence < 0)
            {
                return Problem(index, summaries.Length, "has a negative sequence, which is not a version");
            }
        }

        return null;
    }

    private static string Problem(int index, int total, string what) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "element {0} of {1} {2}.",
            index,
            total,
            what);
}

/// <summary>The result of reading one summaries response body.</summary>
/// <param name="Summaries">
/// The versions the window named, in the order EPA sent them. Empty when <paramref name="Problem"/> is
/// set — and also, legitimately, when the window was quiet.
/// </param>
/// <param name="UnexpectedProperties">
/// Property names EPA sent that <see cref="HandlerSourceSummary"/> has no home for, sorted and
/// de-duplicated. Empty against the pinned spec. Non-empty means every version was still read and
/// something about them is being discarded.
/// </param>
/// <param name="Problem">
/// Why the body could not be used, or <see langword="null"/>. Safe to log: it names a JSON path, a value
/// kind, an element index or a length — never a value from the body, and never a handler identifier.
/// </param>
public sealed record SummaryPayloadRead(
    IReadOnlyList<HandlerSourceSummary> Summaries,
    IReadOnlyList<string> UnexpectedProperties,
    string? Problem)
{
    /// <summary>Whether the body yielded usable summaries.</summary>
    public bool IsReadable => Problem is null;

    /// <summary>How many of the summaries EPA marked as the current record.</summary>
    /// <remarks>
    /// Worth counting rather than merely carrying, because it is the one number in a summaries response
    /// that has an expected shape: at most one current record per <c>(handlerId, sourceType)</c>. A window
    /// reporting more is either a genuine EPA state or a reading defect, and script 521 is the thing that
    /// has to resolve it either way.
    /// </remarks>
    public int CurrentRecordCount => Summaries.Count(summary => summary.CurrentRecord);

    /// <summary>A rejection carrying no summaries.</summary>
    /// <param name="problem">Why, in a form safe to log.</param>
    /// <returns>The rejection.</returns>
    internal static SummaryPayloadRead Rejected(string problem) => new([], [], problem);
}
