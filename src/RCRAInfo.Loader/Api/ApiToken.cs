using System.Globalization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// A bearer token from <c>GET /api/v1/auth/{apiId}/{apiKey}</c>, with the two instants needed to decide
/// when to replace it.
/// </summary>
/// <param name="Value">
/// The token itself — a 20-minute JWT, per EPA's documentation. A secret: it authorizes every request
/// this application makes, so it is treated exactly like the API Key and never logged.
/// </param>
/// <param name="IssuedAt">
/// When this process received it, by <see cref="TimeProvider"/>. Not EPA's issue time, which the payload
/// does not carry — and using ours is what makes <see cref="RcraInfoApiOptions.MinimumRefreshInterval"/>
/// measurable against our own clock rather than against the clock that is in question.
/// </param>
/// <param name="ExpiresAt">EPA's stated expiration, from the response's <c>expiration</c> field.</param>
/// <remarks>
/// <b><see cref="ToString"/> is overridden for the reason <c>ApplicationCredentials.ToString</c> is.</b>
/// A positional record prints every member, so one <c>LogDebug ("token: {Token}", token)</c> — an
/// entirely ordinary line to write while chasing a 401 — would put a live bearer token into
/// <c>logs.ExecutionLog</c>, which the monitoring web application can read (AR8).
/// </remarks>
public sealed record ApiToken(string Value, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    /// <summary>How long EPA said this token would live.</summary>
    public TimeSpan Lifetime => ExpiresAt - IssuedAt;

    /// <summary>A description naming the times and not the token.</summary>
    public override string ToString() =>
        $"ApiToken {{ Value = <redacted>, "
        + $"IssuedAt = {IssuedAt.ToString("O", CultureInfo.InvariantCulture)}, "
        + $"ExpiresAt = {ExpiresAt.ToString("O", CultureInfo.InvariantCulture)} }}";
}
