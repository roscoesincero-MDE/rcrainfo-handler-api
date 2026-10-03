using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCRAInfo.Data.Payloads;

/// <summary>
/// One element of <c>dbo.uspMergeHandlerSourceBatch</c>'s <c>@Payload</c> array: when the handler
/// was retrieved, and the handler itself.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Handler"/> is EPA's own JSON, forwarded unchanged.</b> This is the single most
/// important design decision in this project's write path, and it is worth being explicit about why
/// there is no 210-property C# type here.
/// </para>
/// <para>
/// Script 400 shreds 210 columns by explicit path — <c>'$.handler.handlerId'</c>,
/// <c>'$.handler.contact.address.foreignState.countryCode'</c>, and so on — and those paths were
/// generated from the same pinned EPA swagger spec that generated <c>dbo.HandlerSource</c>. So the
/// nested shape the procedure expects is, by construction, exactly the shape EPA sends. If the
/// loader deserialised that into C# classes and re-serialised it, every one of those 210 paths would
/// become a casing and spelling assertion maintained by hand in a second place — and
/// <c>OPENJSON</c> matches property names case-sensitively, so each one that drifted would shred to
/// <c>NULL</c> without erroring. Script 400's own header names this as the reason it is generated
/// rather than typed: "a hand-written mapping is a second source of truth for the one mapping this
/// project cannot afford to have two of".
/// </para>
/// <para>
/// Passing the <see cref="JsonElement"/> straight through removes that class of defect entirely
/// rather than testing for it. The bytes that reach <c>OPENJSON</c> are the bytes EPA sent, so a
/// field this project has never heard of still lands in its column, and a field EPA renames fails
/// visibly at the round-trip test rather than invisibly at 02:00. It is also markedly cheaper: a
/// 500-element batch is copied once, not parsed into 105,000 properties and written back out.
/// </para>
/// </remarks>
public sealed class HandlerEnvelope
{
    /// <summary>
    /// When the loader retrieved this handler from RCRAInfo. Serialised as UTC with a <c>Z</c>
    /// suffix; see <see cref="PayloadJson"/> for why the offset is normalised here rather than
    /// trusted to the column.
    /// </summary>
    public DateTimeOffset RetrievedDateUtc { get; init; }

    /// <summary>
    /// EPA's <c>handler</c> object, exactly as received. Never rebuilt from typed properties — see
    /// the type remarks.
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonElement"/> is written verbatim by <see cref="JsonSerializer"/>: a naming
    /// policy applies to <i>this</i> property's name and not to the contents of the element, which
    /// is precisely the behaviour wanted. The property name itself must be <c>handler</c>, which
    /// camelCase makes it.
    /// </remarks>
    [JsonPropertyName ("handler")]
    public JsonElement Handler { get; init; }
}
