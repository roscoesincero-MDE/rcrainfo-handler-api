using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// One request to a RCRAInfo data endpoint, carrying <b>both</b> the URI to send and the bare path to log.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two are separate properties because <c>logs.HandlerLoadAttempt.RequestPath</c> stores the path
/// only — never the query string, never a header (AR8).</b> The monitoring web application can read that
/// table, RCRAInfo credentials travel in headers and in the auth call's path, and two of the three data
/// calls here carry their identifying argument in the <i>query string</i>. So "log the request" and "send
/// the request" need different values, and the reliable way to get that right is to hand the caller both
/// at once rather than to hand it a <see cref="Uri"/> and a rule. Script
/// <c>logs.uspRecordHandlerLoadAttemptSet</c> enforces the rule as well, replacing any value that is not a
/// bare path and counting it — so getting this wrong is loud rather than silent, and this type is what
/// keeps the count at zero.
/// </para>
/// <para>
/// <see cref="ToString"/> returns <see cref="Path"/> alone, for the same reason
/// <c>ApplicationCredentials.ToString</c> returns no secret: the day someone interpolates a request into a
/// message, the safe half is what lands there.
/// </para>
/// <para>
/// <b>Every factory below validates, and the reason is specific rather than general tidiness.</b>
/// <see cref="ApiFetchOutcome.NotFound"/> drives the AR7 soft delete, so any defect that makes a
/// well-formed-looking request point at nothing does not surface as an error — it surfaces as
/// <b>deleted data</b>. A blank <c>activityLocation</c>, a sequence of <c>0</c>, an unescaped handler id:
/// each produces a <c>404</c> or a wrong-scope <c>200</c> that the pipeline downstream is built to trust.
/// Validating here is cheap; the alternative is diagnosing a soft delete.
/// </para>
/// </remarks>
/// <param name="Endpoint">Which endpoint this addresses, for status classification.</param>
/// <param name="Path">
/// The path relative to the configured base address, with no query string and <b>no leading slash</b>.
/// See <see cref="LogPath"/> for the form the attempt log stores.
/// </param>
/// <param name="RelativeUri">
/// The path plus query string, relative to the base address — what is actually sent. Never logged.
/// </param>
public sealed record RcraInfoDataRequest(
    RcraInfoDataEndpoint Endpoint,
    string Path,
    string RelativeUri)
{
    /// <summary>
    /// The absolute-path form of <see cref="Path"/> — a leading slash, still no query string. <b>This is
    /// the value to store in <c>logs.HandlerLoadAttempt.RequestPath</c>, not <see cref="Path"/>.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A third form exists because two correct rules pull in opposite directions at exactly this
    /// point.</b> <see cref="Path"/> must <i>not</i> begin with a slash: it is resolved against
    /// <c>HttpClient.BaseAddress</c>, and <c>new Uri (base, "/api/v1/hd/...")</c> resolves against the
    /// <i>host</i> and silently discards the <c>/rcra-api/rest</c> the base address carries — the same
    /// failure <see cref="RcraInfoApiOptions.TryGetBaseUri"/>'s trailing slash prevents from the other end.
    /// Script 524, meanwhile, accepts a <c>RequestPath</c> only if it <i>does</i> begin with a slash, and
    /// <b>replaces the whole value with a notice when it does not</b>, counting it into
    /// <c>@ValuesWithheld</c>.
    /// </para>
    /// <para>
    /// So handing <see cref="Path"/> to the attempt log would not fail — it would write
    /// <c>(withheld: …)</c> into every row and report a non-zero withheld count for the whole load, which
    /// the plan describes as a loader defect to fix. Two properties, each satisfying its own rule, and
    /// <c>ApiFetchResult.ToAttemptElement</c> is the single place that picks the right one.
    /// </para>
    /// </remarks>
    public string LogPath => "/" + Path;

    /// <summary>The summaries path, from the pinned spec, relative to the <c>/rcra-api/rest</c> base.</summary>
    public const string SummariesPath = "api/v1/hd/sources/summaries";

    /// <summary>The other-ids path. A <b>collection</b> path — the handler id goes in the query string.</summary>
    /// <remarks>
    /// This plan said <c>api/v1/hd/other-ids/{handlerId}</c> until 2026-09-06, and the spec has never had
    /// that form for a <c>GET</c>: <c>handlerId</c> is <c>in: query, required: true</c>, and the only
    /// path-segment form is the <c>DELETE</c> this application never calls. The wrong form matches no route
    /// at all, so it would have failed as a <c>404</c> on the one endpoint whose whole cost argument (G14)
    /// rests on being called for every handler — and, worse, a <c>404</c> is the soft-delete signal
    /// elsewhere in this client. That is why <see cref="RcraInfoDataEndpoint.OtherIds"/> declares no
    /// documented <c>404</c>: it makes this specific mistake classify as
    /// <see cref="ApiFetchOutcome.Unexpected"/>, which means "the spec and the service disagree", rather
    /// than as an absent handler.
    /// </remarks>
    public const string OtherIdsPath = "api/v1/hd/other-ids";

    /// <summary>The prefix of the per-version detail path.</summary>
    public const string SourcesPath = "api/v1/hd/sources";

    /// <summary>EPA's date format for <c>startDate</c> and <c>endDate</c>: <c>format: date</c>.</summary>
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// The statuses the pinned spec documents, per endpoint. An answer outside its endpoint's set is
    /// <see cref="ApiFetchOutcome.Unexpected"/>.
    /// </summary>
    private static readonly FrozenDictionary<RcraInfoDataEndpoint, FrozenSet<int>> DocumentedStatuses =
        new Dictionary<RcraInfoDataEndpoint, FrozenSet<int>>
        {
            [RcraInfoDataEndpoint.Summaries] = new[] { 200, 400, 401, 403, 404, 500 }.ToFrozenSet(),
            [RcraInfoDataEndpoint.Source] = new[] { 200, 401, 403, 404, 500 }.ToFrozenSet(),
            [RcraInfoDataEndpoint.OtherIds] = new[] { 200, 400, 401, 403, 500 }.ToFrozenSet(),
            [RcraInfoDataEndpoint.Lookup] = new[] { 200, 401, 403, 500 }.ToFrozenSet(),
        }.ToFrozenDictionary();

    /// <summary>
    /// Whether a <c>404</c> from this endpoint is a documented answer about the data rather than a defect
    /// in the request.
    /// </summary>
    /// <remarks>
    /// <b>The single most consequential line in this file.</b> When this is true a <c>404</c> becomes
    /// <see cref="ApiFetchOutcome.NotFound"/> and feeds <c>dbo.uspSoftDeleteHandlerSourceSet</c>; when it
    /// is false the same status becomes <see cref="ApiFetchOutcome.Unexpected"/> and stops nothing being
    /// deleted. Deriving it from the spec's own response sets, rather than from "404 means gone", is what
    /// keeps a wrong URL from reading as EPA having removed a record.
    /// </remarks>
    public bool DocumentsNotFound => DocumentedStatuses[Endpoint].Contains(404);

    /// <summary>Whether the spec documents <paramref name="status"/> for this endpoint.</summary>
    /// <param name="status">The HTTP status received.</param>
    /// <returns>Whether the pinned spec lists it for this endpoint.</returns>
    public bool IsDocumented(int status) => DocumentedStatuses[Endpoint].Contains(status);

    /// <summary>
    /// The summaries call that drives a load: one activity location, one closed date window.
    /// </summary>
    /// <param name="activityLocation">The two-letter activity location. <c>MD</c> in this project (G2).</param>
    /// <param name="startDate">First day of the window, inclusive.</param>
    /// <param name="endDate">Last day of the window, inclusive.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="activityLocation"/> is blank or is not two letters, or the window runs backwards.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>A blank <paramref name="activityLocation"/> throws, and that guard is the whole reason this
    /// method exists instead of a string concatenation at the call site.</b> Every one of this endpoint's
    /// four parameters is <c>required: false</c> in the spec, so
    /// <c>GET api/v1/hd/sources/summaries</c> with no query string is a <b>legal</b> call asking EPA for
    /// every handler in the country. It does not fail: it succeeds, returns far more, and fills this mirror
    /// with other states' regulated entities while reporting success. An unset or blanked
    /// <c>ActivityLocation</c> in <c>appsettings.json</c> is the entire distance to that outcome, which is
    /// why the refusal lives in code rather than in a comment beside the setting.
    /// </para>
    /// <para>
    /// <b><paramref name="startDate"/> is required here because the spec's prose requires it and its
    /// <c>required</c> flags do not.</b> The operation description reads *"Specify either handlerId or
    /// activityLocation and startDate"* while marking all four optional — the machine-readable contract is
    /// looser than the written one, and the looser one is the one a generator would follow. This method
    /// implements the written one.
    /// </para>
    /// <para>
    /// <b><paramref name="endDate"/> is always sent, even though EPA defaults it to today.</b> Letting it
    /// default makes the window's end depend on when the request is made, so a load that starts at 23:55
    /// and continues past midnight asks for two different windows and neither is the one the watermark
    /// records. An explicit end date also makes a slice reproducible, which is what F2's measurement needs.
    /// </para>
    /// <para>
    /// The two-letter check is a <b>shape</b> check and deliberately not a check for <c>MD</c>. Scope is a
    /// configuration decision (G2) and hard-coding it here would put the same value in two places; what
    /// this rejects is a value that cannot be an activity location at all, which is the failure a
    /// mis-edited config file produces.
    /// </para>
    /// </remarks>
    public static RcraInfoDataRequest Summaries(
        string activityLocation,
        DateOnly startDate,
        DateOnly endDate)
    {
        string location = RequireActivityLocation(activityLocation);

        if (endDate < startDate)
        {
            throw new ArgumentException(
                $"The summaries window ends before it starts ({Format(startDate)} to {Format(endDate)}). "
                + "EPA would answer 200 with an empty array, which is indistinguishable from a quiet day "
                + "and would advance the watermark over data that was never fetched.",
                nameof(endDate));
        }

        string query = Query(
            ("activityLocation", location),
            ("startDate", Format(startDate)),
            ("endDate", Format(endDate)));

        return new RcraInfoDataRequest(
            RcraInfoDataEndpoint.Summaries,
            SummariesPath,
            SummariesPath + query);
    }

    /// <summary>
    /// The summaries call for one handler: every version it has, with each version's current flag.
    /// </summary>
    /// <param name="handlerId">The handler's EPA identifier.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentException"><paramref name="handlerId"/> is blank.</exception>
    /// <remarks>
    /// This is the second of the endpoint's two documented forms, and it is not an alternative way to do
    /// the same job — it is what makes <c>CurrentRecord</c> reconciliation possible (Analysis §5.2 item 2,
    /// plan §D4). A new version flips its <i>siblings</i>' flags, and the siblings are not in the delta
    /// feed, so after any change to a handler this call returns the authoritative list for it. Sending
    /// <c>activityLocation</c> as well would be harmless and is omitted anyway: the spec describes the two
    /// forms as alternatives, and a request that satisfies both invites EPA to pick one.
    /// </remarks>
    public static RcraInfoDataRequest SummariesForHandler(string handlerId)
    {
        string id = RequireHandlerId(handlerId);

        return new RcraInfoDataRequest(
            RcraInfoDataEndpoint.Summaries,
            SummariesPath,
            SummariesPath + Query(("handlerId", id)));
    }

    /// <summary>One full version — the 377-field payload the mirror is built from.</summary>
    /// <param name="handlerId">The handler's EPA identifier.</param>
    /// <param name="sourceType">EPA's source-type code.</param>
    /// <param name="sequence">EPA's version sequence.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentException">Any part of the key is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequence"/> is not positive.</exception>
    /// <remarks>
    /// <para>
    /// The whole key is in the path, so <b>this is the one data request whose <see cref="Path"/> and
    /// <see cref="RelativeUri"/> are identical</b> — and the one where the logged path names a handler.
    /// That is permitted: a handler id is not a secret and both the plan and script <c>506</c> say so
    /// explicitly. What may never appear is a credential, a token or a query string.
    /// </para>
    /// <para>
    /// <b>The <paramref name="sequence"/> guard protects the soft-delete path, not the request.</b> These
    /// three values are echoed from the summaries feed, so a <c>0</c> or a negative here means this client
    /// corrupted them in between — and the answer to a malformed detail path is a <c>404</c>, which this
    /// client is built to read as "EPA no longer has this record" and to turn into a soft delete. A throw is
    /// the correct response to a key this client invented; a <c>404</c> is not.
    /// </para>
    /// <para>
    /// All three are escaped as path segments. <c>sourceType</c> is documented only as <c>"('N', 'I'
    /// etc)"</c> with no lookup endpoint to enumerate it (G24), so its alphabet is genuinely unknown — and
    /// an unescaped <c>/</c> or <c>?</c> in a value taken from a response would silently change which
    /// endpoint is called.
    /// </para>
    /// </remarks>
    public static RcraInfoDataRequest Source(string handlerId, string sourceType, int sequence)
    {
        string id = RequireHandlerId(handlerId);
        string type = RequireSourceType(sourceType);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);

        string path = string.Concat(
            SourcesPath,
            "/",
            Uri.EscapeDataString(id),
            "/",
            Uri.EscapeDataString(type),
            "/",
            sequence.ToString(CultureInfo.InvariantCulture));

        return new RcraInfoDataRequest(RcraInfoDataEndpoint.Source, path, path);
    }

    /// <summary>The alternate identifiers for one handler (G14, [R8] — one extra call per handler).</summary>
    /// <param name="handlerId">The handler's EPA identifier.</param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentException"><paramref name="handlerId"/> is blank.</exception>
    /// <remarks>
    /// <b><c>200</c> with an empty array is the ordinary answer and must not be treated as an absence.</b>
    /// Nothing in the summaries feed says which handlers have other identifiers, so this call is made for
    /// every one of them — which is where G14's cost lands — and most will have none. The endpoint documents
    /// no <c>404</c> at all, so "this handler has no other ids" arrives as an empty successful array.
    /// </remarks>
    public static RcraInfoDataRequest OtherIds(string handlerId)
    {
        string id = RequireHandlerId(handlerId);

        return new RcraInfoDataRequest(
            RcraInfoDataEndpoint.OtherIds,
            OtherIdsPath,
            OtherIdsPath + Query(("handlerId", id)));
    }

    /// <summary>One of the 23 mirrored EPA code lists (G15), fetched before the handler data.</summary>
    /// <param name="lookup">The list, from <see cref="RcraInfoLookups.All"/>.</param>
    /// <param name="activityLocation">
    /// The activity location, sent as EPA's <c>stateCode</c> — required when
    /// <see cref="RcraInfoLookup.StateCode"/> is <see cref="LookupStateCode.Required"/> or
    /// <see cref="LookupStateCode.Optional"/>, and refused when the endpoint has no such parameter.
    /// </param>
    /// <returns>The request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lookup"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The endpoint needs a <c>stateCode</c> and none was supplied or it is not two letters, or the endpoint
    /// has no <c>stateCode</c> parameter and one was supplied anyway.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>Supplying an activity location to one of the sixteen endpoints that has no <c>stateCode</c>
    /// parameter throws, rather than being ignored.</b> That asymmetry is the whole reason this factory takes
    /// a nullable argument instead of always appending the parameter. Nine of those sixteen lists <i>are</i>
    /// jurisdiction-scoped in their payload — every element carries a <c>required</c>
    /// <c>activityLocation</c> — so it is entirely reasonable to assume the request can be scoped too, and
    /// it cannot. An ignored parameter would leave a caller believing a national response was a Maryland
    /// one, and script 523 retires by what the payload names, so the belief would never be contradicted by
    /// an error. Throwing is what turns that into a fixable mistake.
    /// </para>
    /// <para>
    /// <b>Nothing is filtered on this side afterwards.</b> See <see cref="RcraInfoLookup"/>: trimming a
    /// national payload down to one state would leave every other jurisdiction's stored codes live and
    /// permanently stale, because 523 retires only within the activity locations the payload mentions.
    /// </para>
    /// </remarks>
    public static RcraInfoDataRequest Lookup(RcraInfoLookup lookup, string? activityLocation)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        if (!lookup.TakesStateCode)
        {
            if (!string.IsNullOrWhiteSpace(activityLocation))
            {
                throw new ArgumentException(
                    $"GET {lookup.Path} has no stateCode parameter, so '{activityLocation.Trim()}' cannot "
                    + "scope it. Nine of the unscoped endpoints still return an activityLocation on every "
                    + "element, which is what makes this look possible; EPA would ignore the parameter and "
                    + "answer with every jurisdiction, and script 523 retires only within the activity "
                    + "locations the payload names -- so the response would be accepted as scoped and "
                    + "nothing would ever say otherwise.",
                    nameof(activityLocation));
            }

            return new RcraInfoDataRequest(RcraInfoDataEndpoint.Lookup, lookup.Path, lookup.Path);
        }

        if (string.IsNullOrWhiteSpace(activityLocation))
        {
            throw new ArgumentException(
                $"GET {lookup.Path} takes a stateCode ({lookup.StateCode}) and none was supplied. Omitting a "
                + "required one cannot come back as a 400, because no /lookup/hd endpoint documents one: it "
                + "arrives as an undocumented status, or as a 200 carrying every jurisdiction's codes. Set "
                + "RCRAInfoLoad:ActivityLocation (MD for this project, G2).",
                nameof(activityLocation));
        }

        string location = RequireActivityLocation(activityLocation);

        return new RcraInfoDataRequest(
            RcraInfoDataEndpoint.Lookup,
            lookup.Path,
            lookup.Path + Query(("stateCode", location)));
    }

    /// <summary>The bare path, never the query string. See the remarks on this type.</summary>
    /// <returns><see cref="Path"/>.</returns>
    public override string ToString() => Path;

    private static string Format(DateOnly date) =>
        date.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>Builds an escaped query string from pairs that are all known non-empty.</summary>
    private static string Query(params (string Name, string Value)[] parameters)
    {
        StringBuilder query = new();

        foreach ((string name, string value) in parameters)
        {
            query.Append(query.Length == 0 ? '?' : '&')
                 .Append(name)
                 .Append('=')
                 .Append(Uri.EscapeDataString(value));
        }

        return query.ToString();
    }

    private static string RequireActivityLocation(string activityLocation)
    {
        if (string.IsNullOrWhiteSpace(activityLocation))
        {
            throw new ArgumentException(
                "No activityLocation was supplied for a summaries request. Every parameter on that endpoint "
                + "is optional in EPA's spec, so the call would succeed and return every handler in the "
                + "country -- filling this mirror with out-of-scope regulated entities and reporting "
                + "success. Set RCRAInfoLoad:ActivityLocation (MD for this project, G2).",
                nameof(activityLocation));
        }

        string trimmed = activityLocation.Trim();

        if (trimmed.Length != 2 || !char.IsAsciiLetter(trimmed[0]) || !char.IsAsciiLetter(trimmed[1]))
        {
            throw new ArgumentException(
                $"activityLocation must be two letters; '{trimmed}' is not. This checks the shape and "
                + "deliberately not the value -- which state is in scope is a configuration decision (G2), "
                + "and hard-coding MD here would put it in two places.",
                nameof(activityLocation));
        }

        return trimmed.ToUpperInvariant();
    }

    private static string RequireHandlerId(string handlerId)
    {
        if (string.IsNullOrWhiteSpace(handlerId))
        {
            throw new ArgumentException(
                "No handlerId was supplied. On the summaries endpoint this would silently widen the request "
                + "to every handler EPA has; on the other endpoints it produces a 404, which this client "
                + "reads as a deletion.",
                nameof(handlerId));
        }

        return handlerId.Trim();
    }

    private static string RequireSourceType(string sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            throw new ArgumentException(
                "No sourceType was supplied. The detail path has a segment for it, so an empty value "
                + "collapses two slashes into one and addresses a different route entirely.",
                nameof(sourceType));
        }

        return sourceType.Trim();
    }
}
