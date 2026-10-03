using System.Globalization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Where EPA's RCRAInfo REST service is and how long a token is trusted. Bound from the
/// <c>RCRAInfoApi</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="BaseAddress"/> has no default, deliberately.</b> The two published base addresses
/// differ only in a hostname — <c>https://rcranodepreprod.epa.gov/rcra-api/rest</c> and
/// <c>https://rcranode.epa.gov/rcra-api/rest</c> — and the credentials are environment-specific
/// (Analysis §6.1, G1). A default would make one of the two the silent fallback, and the failure it
/// produces is the worst kind available here: a scheduled load that authenticates successfully against
/// the wrong environment and writes preprod data into Production, reporting success the whole time.
/// Absent configuration is a startup failure instead.
/// </para>
/// <para>
/// <b>The refresh margin is a bound, not a schedule.</b> See
/// <see cref="RefreshDueAt(DateTimeOffset, DateTimeOffset)"/>: the effective margin is capped at half
/// the token's own lifetime, so a token whose life is shorter than the margin cannot put this
/// application into a re-authentication loop.
/// </para>
/// </remarks>
public sealed class RcraInfoApiOptions
{
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "RCRAInfoApi";

    /// <summary>
    /// Root of the REST service, including the <c>/rcra-api/rest</c> path from the pinned spec's
    /// <c>basePath</c>. A trailing slash is added if it is missing — see <see cref="TryGetBaseUri"/>.
    /// </summary>
    public string BaseAddress { get; set; } = string.Empty;

    /// <summary>How long a single HTTP request may take before it is abandoned.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far ahead of a token's stated expiration to renew it. Two minutes against a 20-minute token:
    /// long enough to cover a request that starts just before expiry and modest clock skew, short enough
    /// that it costs one extra auth call per token rather than one per request.
    /// </summary>
    public TimeSpan TokenRefreshMargin { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The shortest interval between two auth calls, however unusable the current token looks.
    /// </summary>
    /// <remarks>
    /// This exists for one measured failure mode: a token that arrives already expired, which happens
    /// when EPA's clock and this machine's clock disagree by more than the token's lifetime. Without a
    /// floor, every single request would first fetch a token, find it expired, and fetch another —
    /// turning a clock problem into a denial of service aimed at EPA and authored by us. With it, the
    /// load keeps running on the "expired" token, the <c>401</c> retry path covers the case where EPA
    /// agrees it is expired, and the auth call rate stays bounded.
    /// </remarks>
    public TimeSpan MinimumRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Parses <see cref="BaseAddress"/> into the base URI an <c>HttpClient</c> needs.</summary>
    /// <param name="baseUri">The parsed, slash-terminated base URI, when this returns true.</param>
    /// <param name="problem">Why it could not be used, when this returns false.</param>
    /// <returns>Whether <see cref="BaseAddress"/> is usable.</returns>
    /// <remarks>
    /// The trailing slash is not cosmetic. <c>new Uri (new Uri ("https://h/rcra-api/rest"),
    /// "api/v1/auth/...")</c> resolves to <c>https://h/rcra-api/api/v1/auth/...</c>, because relative
    /// resolution discards the last segment of a path that does not end in a slash. That produces a 404
    /// from a base address that looks correct in configuration and correct in the error message, which is
    /// a bad afternoon; adding the slash here costs nothing.
    /// </remarks>
    public bool TryGetBaseUri(out Uri? baseUri, out string? problem)
    {
        baseUri = null;

        if (string.IsNullOrWhiteSpace(BaseAddress))
        {
            problem =
                $"{SectionName}:BaseAddress is not configured. Set it to the RCRAInfo REST root for THIS "
                + "environment -- https://rcranodepreprod.epa.gov/rcra-api/rest for preprod, "
                + "https://rcranode.epa.gov/rcra-api/rest for production. There is deliberately no "
                + "default: the API credentials are issued per environment, and a default would let a "
                + "run authenticate against the wrong one and report success.";

            return false;
        }

        if (!Uri.TryCreate(BaseAddress.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            problem = $"{SectionName}:BaseAddress is not an absolute URI.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps
            && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback))
        {
            // Both halves of the credential travel in the request path of the auth call, so plaintext
            // HTTP puts the API Key on the wire in the clear and into every proxy log on the way. Loopback
            // is exempt because that is how this client is tested against a local stub, and a request that
            // never leaves the machine has no wire to be read from.
            problem =
                $"{SectionName}:BaseAddress uses {parsed.Scheme}, which would send the RCRAInfo API ID and "
                + "Key across the network in clear text -- EPA's auth endpoint carries both in the request "
                + "path. Use https (http is accepted only for loopback, which exists for testing).";

            return false;
        }

        baseUri = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
        problem = null;

        return true;
    }

    /// <summary>Validates everything, for a fail-at-startup check.</summary>
    /// <returns>The problems found, empty when the options are usable.</returns>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];

        if (!TryGetBaseUri(out _, out string? baseAddressProblem))
        {
            problems.Add(baseAddressProblem!);
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:RequestTimeout must be greater than zero.");
        }

        if (TokenRefreshMargin < TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:TokenRefreshMargin cannot be negative.");
        }

        if (MinimumRefreshInterval < TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:MinimumRefreshInterval cannot be negative.");
        }

        return problems;
    }

    /// <summary>When a token issued at <paramref name="issuedAt"/> should be renewed.</summary>
    /// <param name="issuedAt">When the token was received.</param>
    /// <param name="expiresAt">When EPA says it expires.</param>
    /// <returns>The instant at or after which the token should be replaced.</returns>
    /// <remarks>
    /// The effective margin is <c>min (TokenRefreshMargin, lifetime / 2)</c>. Halving is what makes this
    /// self-limiting: whatever lifetime EPA hands out, this asks for at most two tokens per lifetime.
    /// Applying the configured margin unconditionally would mean a five-minute token with a two-minute
    /// margin refreshes at 60% of its life (fine) while a one-minute token refreshes before it is ever
    /// used (not fine), and nothing in the published documentation promises the 20 minutes stays 20.
    /// </remarks>
    public DateTimeOffset RefreshDueAt(DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        TimeSpan lifetime = expiresAt - issuedAt;

        if (lifetime <= TimeSpan.Zero)
        {
            // Already expired on arrival: due now. MinimumRefreshInterval is what keeps that from
            // becoming a request-per-token loop.
            return expiresAt;
        }

        TimeSpan margin = TimeSpan.FromTicks(Math.Min(TokenRefreshMargin.Ticks, lifetime.Ticks / 2));

        return expiresAt - margin;
    }

    /// <summary>The options as a single line for a startup log. Contains no secret; there is none here.</summary>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} {{ BaseAddress = {1}, RequestTimeout = {2}, TokenRefreshMargin = {3}, "
            + "MinimumRefreshInterval = {4} }}",
            SectionName,
            string.IsNullOrWhiteSpace(BaseAddress) ? "<not configured>" : BaseAddress,
            RequestTimeout,
            TokenRefreshMargin,
            MinimumRefreshInterval);
}
