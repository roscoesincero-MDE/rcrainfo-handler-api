using System.Collections.Frozen;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// How the <c>stateCode</c> query parameter behaves on one <c>/lookup/hd/*</c> endpoint, read from the
/// pinned spec rather than inferred from the table's shape.
/// </summary>
/// <remarks>
/// <b>This does not line up with which tables are jurisdiction-scoped, and that mismatch is the point of
/// the type.</b> Sixteen of the 23 mirrored lists carry an <c>activityLocation</c> on every element — it is
/// <c>required: true</c> in the spec's own definitions — but only <b>seven</b> of the endpoints accept a
/// <c>stateCode</c> at all. So for nine jurisdiction-scoped lists the loader cannot ask for one state and
/// must take whatever EPA publishes. See <see cref="RcraInfoLookup"/> for why that must not be "fixed" by
/// filtering the response.
/// </remarks>
public enum LookupStateCode
{
    /// <summary>
    /// The endpoint has no <c>stateCode</c> parameter. Sixteen of the 23 — the seven national lists and
    /// nine that are jurisdiction-scoped in their payload but not in their request.
    /// </summary>
    Unsupported,

    /// <summary>
    /// <c>stateCode</c> is <c>required: true</c>. Five lists: contact types, counties, state activities,
    /// state districts, universal waste codes.
    /// </summary>
    /// <remarks>
    /// Omitting it cannot produce a documented answer, because these endpoints document no <c>400</c> —
    /// see <see cref="RcraInfoDataEndpoint.Lookup"/>.
    /// </remarks>
    Required,

    /// <summary>
    /// <c>stateCode</c> is accepted and optional. Two lists: generator categories and waste codes.
    /// </summary>
    /// <remarks>
    /// This loader <b>always sends it</b> where it is accepted. See <see cref="RcraInfoLookup"/>.
    /// <para>
    /// These same two endpoints are also the only ones carrying a second optional parameter,
    /// <c>federal</c> — "flag to return only federal codes". It is <b>never sent</b>, and that is a
    /// correctness decision rather than an omission: <c>federal=true</c> would answer with a strict
    /// subset, and script 523 retires every code a successful payload did not contain, so it would soft
    /// delete every state-specific generator category and waste code in the jurisdictions the payload
    /// names. Omitted, EPA applies no filter. <c>build/check_lookup_catalog.py</c> holds that decision
    /// against the spec and reports any <i>new</i> optional parameter as a finding, because an unsent
    /// filter is a filter whose default EPA chooses.
    /// </para>
    /// </remarks>
    Optional,
}

/// <summary>
/// One of the 23 mirrored EPA code lists (G15): the name script 523 dispatches on, the
/// <c>/lookup/hd/</c> path segment that serves it, and whether that endpoint takes a <c>stateCode</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Name"/> is not a label — it is script 523's <c>@LookupName</c>, and the procedure
/// dispatches its <c>MERGE</c> on it.</b> A name outside the 23 is refused by the procedure with a message
/// listing all of them, so a misspelling fails loudly; a name that is spelled correctly but paired with the
/// wrong path would refresh the wrong table from the right payload, which fails quietly and is why the two
/// live in one record here rather than being matched up at a call site.
/// </para>
/// <para>
/// <b>The query parameter is called <c>stateCode</c> and the payload property is called
/// <c>activityLocation</c>.</b> Same value, two names, and only this type sends the request form. Using the
/// payload's name in the query string would not fail cleanly on the five lists that require the parameter,
/// because those endpoints document no <c>400</c>: EPA would answer something undocumented, or answer
/// <c>200</c> with every jurisdiction's codes.
/// </para>
/// <para>
/// <b>The nine lists that are jurisdiction-scoped in their payload but unscoped in their request must not
/// be filtered on this side, and that is a correctness rule rather than a preference.</b> Script 523
/// retires exactly the codes it did not see <i>under the activity locations the payload mentions</i>
/// (plan [R16]). Filtering a national response down to <c>MD</c> before sending it would therefore leave
/// every other jurisdiction's stored codes untouched but permanently stale — never refreshed, never
/// retired. Worse, the reverse switch retires nothing and silently acquires 50 states of codes. So the
/// scope of a lookup refresh is fixed by <b>the endpoint</b>, not by configuration, and the loader passes
/// the payload through as received.
/// </para>
/// </remarks>
/// <param name="Name">Script 523's <c>@LookupName</c>.</param>
/// <param name="PathSegment">The segment after <c>api/v1/lookup/hd/</c>.</param>
/// <param name="StateCode">Whether that endpoint accepts a <c>stateCode</c>, and whether it demands one.</param>
public sealed record RcraInfoLookup(
    string Name,
    string PathSegment,
    LookupStateCode StateCode)
{
    /// <summary>Whether this endpoint accepts a <c>stateCode</c> at all.</summary>
    /// <remarks>
    /// True for seven of the 23. This loader sends the parameter whenever it is true — including on the two
    /// where it is optional — so that all seven behave the same way. Consistency is worth more here than
    /// the extra coverage a national fetch would give: the failure mode of an inconsistent choice is a
    /// jurisdiction's codes that are stored, never refreshed and never retired.
    /// </remarks>
    public bool TakesStateCode => StateCode != LookupStateCode.Unsupported;

    /// <summary>The path relative to the configured base address. No leading slash — see
    /// <see cref="RcraInfoDataRequest.LogPath"/>.</summary>
    public string Path => PathPrefix + PathSegment;

    /// <summary>The prefix every mirrored code list shares.</summary>
    public const string PathPrefix = "api/v1/lookup/hd/";

    /// <summary>Script 523's <c>@LookupName</c>, which is also how this reads in a log message.</summary>
    /// <returns><see cref="Name"/>.</returns>
    public override string ToString() => Name;
}

/// <summary>
/// The 23 mirrored code lists, in the order a load fetches them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Twenty-three, from twenty-four endpoints — and the one left out is the finding this catalog exists to
/// record.</b> <c>GET /lookup/hd/naics-codes</c> returns the same <c>Naics</c> definition as
/// <c>GET /lookup/hd/naics</c>, but it takes <c>term</c> as a <b>required</b> query parameter: it is a
/// search over the list, not the list. It cannot be mirrored by enumeration, because there is no set of
/// terms that provably covers every code, and mirroring it would produce a subset that looks complete —
/// after which script 523 would retire every code the invented terms happened to miss. So <c>Naics</c> is
/// mirrored from <c>/lookup/hd/naics</c>, which takes no parameters, and <c>naics-codes</c> is never
/// called. A catalog built by walking the 24 endpoint paths would have got this wrong in the direction of
/// deleting data.
/// </para>
/// <para>
/// The other pairing worth stating: <c>HandlerSourceType</c> is served by
/// <c>/lookup/hd/submittal-reasons</c>. Nothing in either name suggests the other, and the definition the
/// endpoint returns is what settles it — <c>dbo.LookupHandlerSourceType</c>'s own description records the
/// same pairing, so the two agree by construction rather than by memory.
/// </para>
/// <para>
/// <b>Order is by name, and it is deliberately not "smallest first".</b> A refresh is 23 independent calls
/// with no dependency between them except one that is internal to a single payload —
/// <c>StateDistrict</c> carries its counties nested, so both tables are merged in one call. Fetching in a
/// stable, boring order makes two runs comparable in the attempt log, which is what F2 measures against.
/// </para>
/// </remarks>
public static class RcraInfoLookups
{
    /// <summary>All 23, in fetch order.</summary>
    public static readonly IReadOnlyList<RcraInfoLookup> All =
    [
        new("Accessibility", "accessibility-codes", LookupStateCode.Unsupported),
        new("ContactType", "contact-types", LookupStateCode.Required),
        new("Country", "countries", LookupStateCode.Unsupported),
        new("County", "counties", LookupStateCode.Required),
        new("EpisodicProject", "episodic-projects", LookupStateCode.Unsupported),
        new("EpisodicType", "episodic-events", LookupStateCode.Unsupported),
        new("GeneratorCategory", "generator-categories", LookupStateCode.Optional),
        new("HandlerSourceType", "submittal-reasons", LookupStateCode.Unsupported),
        new("HsmFacilityCode", "hsm-facility-codes", LookupStateCode.Unsupported),
        new("HsmLandBasedUnit", "hsm-land-based-units", LookupStateCode.Unsupported),
        new("HsmReason", "hsm-reasons", LookupStateCode.Unsupported),
        new("LandType", "land-types", LookupStateCode.Unsupported),
        new("Language", "languages", LookupStateCode.Unsupported),
        new("LqgClosureType", "lqg-closure-types", LookupStateCode.Unsupported),
        new("Naics", "naics", LookupStateCode.Unsupported),
        new("NonNotifier", "non-notifiers", LookupStateCode.Unsupported),
        new("OtherPermit", "other-permit-types", LookupStateCode.Unsupported),
        new("Relationship", "relationships", LookupStateCode.Unsupported),
        new("State", "states", LookupStateCode.Unsupported),
        new("StateActivity", "state-activities", LookupStateCode.Required),
        new("StateDistrict", "state-districts", LookupStateCode.Required),
        new("UniversalWaste", "universal-waste-codes", LookupStateCode.Required),
        new("WasteCode", "waste-codes", LookupStateCode.Optional),
    ];

    /// <summary>
    /// The endpoint path segment this application deliberately never calls, and why, so the omission is
    /// discoverable from code rather than only from a document.
    /// </summary>
    public const string ExcludedPathSegment = "naics-codes";

    private static readonly FrozenDictionary<string, RcraInfoLookup> ByName =
        All.ToFrozenDictionary(l => l.Name, StringComparer.Ordinal);

    /// <summary>Looks up one list by script 523's <c>@LookupName</c>.</summary>
    /// <param name="name">The name, matched case-sensitively — 523 compares it the same way.</param>
    /// <returns>The list.</returns>
    /// <exception cref="ArgumentException">The name is not one of the 23.</exception>
    /// <remarks>
    /// Case-sensitive on purpose. Script 523 dispatches with <c>=</c> under the database's collation and
    /// would accept <c>contacttype</c>, but the refusal message and every guardrail spell the names one
    /// way; accepting a second spelling here would make the two sides disagree about what the closed set
    /// is.
    /// </remarks>
    public static RcraInfoLookup ByLookupName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ByName.TryGetValue(name, out RcraInfoLookup? lookup)
            ? lookup
            : throw new ArgumentException(
                $"'{name}' is not one of the {All.Count} mirrored EPA code lists. Script 523 dispatches its "
                + $"MERGE on this exact string: {string.Join(", ", All.Select(l => l.Name))}.",
                nameof(name));
    }
}
