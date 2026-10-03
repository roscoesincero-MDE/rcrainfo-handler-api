using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Calls EPA's auth endpoint and turns every possible answer into one <see cref="ApiAuthOutcome"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The credential is in the request path, and that shapes this whole class.</b> The endpoint is
/// <c>GET /api/v1/auth/{apiId}/{apiKey}</c> — not a header, not a body — so the request URI of this one
/// call is as much a secret as the API Key itself. Three consequences, each of which is a rule rather
/// than a matter of care:
/// </para>
/// <para>
/// 1. <b>Nothing derived from the request URI is reported</b>, and this client is registered with
/// <c>RemoveAllLoggers ()</c>. Measured, because the default is the dangerous one: with
/// <c>IHttpClientFactory</c>'s stock logging, two of the eight log entries a single request produces are
/// <c>Start processing HTTP request GET {uri}</c> and <c>Sending HTTP request GET {uri}</c>, both at
/// <b>Information</b> — the full URI, API Key included, in any sink attached to the application. Polly's
/// resilience telemetry was measured too and names the pipeline and the status, never the URI, so the
/// resilience handler is safe to keep.
/// </para>
/// <para>
/// 2. <b>The response body is never quoted.</b> <see cref="ApiError.Message"/> is parsed and dropped;
/// only <c>code</c> and <c>errorId</c> are reported. A gateway in front of EPA that returns "no route
/// for /api/v1/auth/…" would otherwise put the Key in an operator-facing message, and gateways do that.
/// </para>
/// <para>
/// 3. <b>Every diagnostic is checked against both halves of the credential before it is returned</b> —
/// see <c>EnsureNoCredential</c>. Belt and braces, in the same spirit as
/// <c>ApplicationCredentials.ToString</c>: the checks above are the design, and this is what catches the
/// day someone adds a message that undoes them.
/// </para>
/// <para>
/// <b>What the exception messages do not contain, measured.</b> The assumption recorded before this was
/// built — that an <c>HttpRequestException</c> from this call would name the API Key, because the Key is
/// in the URI — is <b>wrong</b>. A refused connection reports <c>"No connection could be made … (host:port)"</c>;
/// an unresolvable host reports <c>"No such host is known. (host:port)"</c>; a timeout reports the
/// configured <c>HttpClient.Timeout</c>; <c>EnsureSuccessStatusCode</c> reports the status. Host and port,
/// never the path. The leak was never in the exception; it was in the logging, which is where the fix went.
/// </para>
/// </remarks>
public sealed class RcraInfoAuthClient : IRcraInfoAuthClient
{
    /// <summary>The auth endpoint, relative to the configured base address.</summary>
    /// <remarks>
    /// From the pinned spec: <c>basePath</c> <c>/rcra-api/rest</c> plus path
    /// <c>/api/v1/auth/{apiId}/{apiKey}</c>. The base address carries the <c>basePath</c>, so this is the
    /// remainder and is deliberately relative — an absolute URI here would silently override the
    /// configured environment, which is the one mistake <see cref="RcraInfoApiOptions.BaseAddress"/>
    /// exists to prevent.
    /// </remarks>
    private static readonly CompositeFormat AuthPathFormat =
        CompositeFormat.Parse("api/v1/auth/{0}/{1}");

    private readonly HttpClient client;
    private readonly TimeProvider clock;

    /// <summary>Creates the client.</summary>
    /// <param name="client">
    /// The HTTP client to use. Its <c>BaseAddress</c> must be set and must end in a slash — see
    /// <see cref="RcraInfoApiOptions.TryGetBaseUri"/> — and it must have no logging attached.
    /// </param>
    /// <param name="clock">The clock, so token lifetimes are testable without waiting.</param>
    public RcraInfoAuthClient(HttpClient client, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);

        if (client.BaseAddress is null)
        {
            throw new ArgumentException(
                "The HttpClient has no BaseAddress, so the auth path would resolve against nothing. Set "
                + $"it from {RcraInfoApiOptions.SectionName}:BaseAddress.",
                nameof(client));
        }

        this.client = client;
        this.clock = clock;
    }

    /// <inheritdoc/>
    public async Task<ApiAuthResult> AuthenticateAsync(
        string apiId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // Escaped because these are path segments and their alphabet is unpublished (G1 is still open, and
        // the shape validator refuses only what cannot be in a URI path at all). An unescaped '/' or '?'
        // in a Key would otherwise change which endpoint is called, and the answer would be a 404 that
        // looks like a service problem.
        string path = string.Format(
            CultureInfo.InvariantCulture,
            AuthPathFormat,
            Uri.EscapeDataString(apiId),
            Uri.EscapeDataString(apiKey));

        try
        {
            using HttpResponseMessage response = await client
                .GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return await ClassifyAsync(response, apiId, apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            // Message deliberately absent: measured to carry host and port only, but this call is the one
            // place in the solution where being wrong about that costs a leaked credential, so the type is
            // what gets reported. HttpRequestError names the cause without touching the URI.
            return Failure(
                ApiAuthOutcome.Unreachable,
                $"EPA's RCRAInfo service could not be reached ({error.HttpRequestError}). Check network "
                + "connectivity, DNS and any outbound proxy from the machine running the scheduled task, "
                + $"and confirm {RcraInfoApiOptions.SectionName}:BaseAddress names the right host.",
                apiId,
                apiKey);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request timed out; the caller did not cancel. .NET reports both as
            // OperationCanceledException, and only the token can tell them apart.
            return Failure(
                ApiAuthOutcome.Unreachable,
                $"EPA's RCRAInfo auth endpoint did not answer within {client.Timeout.ToString("c", CultureInfo.InvariantCulture)}. This is retryable.",
                apiId,
                apiKey);
        }
    }

    private async Task<ApiAuthResult> ClassifyAsync(
        HttpResponseMessage response,
        string apiId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        int status = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return await ReadTokenAsync(response, apiId, apiKey, cancellationToken).ConfigureAwait(false);
        }

        ApiError? error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        string detail = error?.Describe() ?? string.Empty;
        string suffix = detail.Length == 0 ? string.Empty : $" EPA reported {detail}.";

        // 401 and 403 are the two the plan requires be kept apart, and the ordering here is by status
        // rather than by ApiError.code: the code is advisory (the spec marks it required, and a gateway
        // that never reaches EPA's application will not send one at all), while the status always arrives.
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Failure(
                ApiAuthOutcome.InvalidCredentials,
                "EPA rejected the RCRAInfo API ID and Key (401)." + suffix
                + " This is not retried, because a rejected credential stays rejected. Generate a new API "
                + "ID and Key in RCRAInfo for THIS environment and re-seed the credential file (plan "
                + "§4.2). Neither value is shown here: both are secrets, and both travel in the request "
                + "path of this call.",
                apiId,
                apiKey,
                status,
                error?.Code),

            HttpStatusCode.Forbidden => Failure(
                ApiAuthOutcome.AccessDenied,
                "EPA accepted the credential and refused the request (403)." + suffix
                + " This is a permissions or scope problem, not a bad key -- RCRAInfo grants API access by "
                + "state and region, and Maryland's Handler data is what this application is scoped to "
                + "(Analysis G2). Re-seeding the credential will not change it; ask EPA to widen the "
                + "account's scope.",
                apiId,
                apiKey,
                status,
                error?.Code),

            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => Failure(
                ApiAuthOutcome.ServiceFailure,
                $"EPA's RCRAInfo auth endpoint answered {status}, which asks for a slower retry." + suffix,
                apiId,
                apiKey,
                status,
                error?.Code),

            _ when status >= 500 => Failure(
                ApiAuthOutcome.ServiceFailure,
                $"EPA's RCRAInfo service failed ({status})." + suffix + " This is retryable; nothing about "
                + "the credential is implied by it.",
                apiId,
                apiKey,
                status,
                error?.Code),

            _ => Failure(
                ApiAuthOutcome.Unexpected,
                $"EPA's RCRAInfo auth endpoint answered {status}, which the pinned spec does not document "
                + "for this call (it documents 200, 401 and 500)." + suffix + " Retrying will not change "
                + "it. Either the service changed or the request is not reaching it -- confirm "
                + $"{RcraInfoApiOptions.SectionName}:BaseAddress ends with the service's /rcra-api/rest "
                + "path.",
                apiId,
                apiKey,
                status,
                error?.Code),
        };
    }

    private async Task<ApiAuthResult> ReadTokenAsync(
        HttpResponseMessage response,
        string apiId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ApiAuthResponse? payload;

        try
        {
            payload = await response.Content
                .ReadFromJsonAsync<ApiAuthResponse>(RcraInfoJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            // The exception's Path and BytePositionInLine are reported and its Message is not, for the
            // reason AR4 already established for the credential file: the message quotes the offending
            // JSON, and this body is a token.
            return Failure(
                ApiAuthOutcome.Unexpected,
                "EPA answered 200 with a body that is not the documented JSON (at path "
                + $"{error.Path ?? "?"}, byte {error.BytePositionInLine?.ToString(CultureInfo.InvariantCulture) ?? "?"}). "
                + "The response is not quoted here because a successful auth body contains a bearer token. "
                + $"Confirm {RcraInfoApiOptions.SectionName}:BaseAddress names EPA's REST root and not a "
                + "portal or proxy page.",
                apiId,
                apiKey,
                (int)response.StatusCode);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Token))
        {
            return Failure(
                ApiAuthOutcome.Unexpected,
                "EPA answered 200 with no token. Nothing can be sent to the data endpoints without one, so "
                + "this is a failure and not an empty result.",
                apiId,
                apiKey,
                (int)response.StatusCode);
        }

        DateTimeOffset now = clock.GetUtcNow();

        // An absent expiration is treated as an immediate one rather than as "forever". The spec marks the
        // field optional in the sense that JSON allows it to be missing; a token this application believes
        // never expires is a token it will still be sending after EPA stops accepting it, and the failure
        // arrives as a wave of 401s in the middle of an overnight load.
        DateTimeOffset expiresAt = payload.Expiration ?? now;

        return new ApiAuthResult(
            ApiAuthOutcome.Succeeded,
            new ApiToken(payload.Token, now, expiresAt),
            "EPA issued a bearer token.",
            (int)response.StatusCode);
    }

    private static async Task<ApiError?> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<ApiError>(RcraInfoJson.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A non-JSON error body is ordinary -- a proxy, a gateway, an HTML page. There is nothing to
            // report from it and nothing to log: the status is already known and the body may echo the
            // request path, which holds the credential.
            return null;
        }
        catch (NotSupportedException)
        {
            // An unreadable or absent content type. Same reasoning.
            return null;
        }
    }

    private static ApiAuthResult Failure(
        ApiAuthOutcome outcome,
        string diagnostic,
        string apiId,
        string apiKey,
        int? status = null,
        string? errorCode = null) =>
        new(outcome,
            null,
            EnsureNoCredential(diagnostic, apiId, apiKey),
            status,
            EnsureNoCredentialOrNull(errorCode, apiId, apiKey));

    /// <summary>Last line of defence: a diagnostic that repeats the credential is replaced, not trimmed.</summary>
    /// <remarks>
    /// Nothing above puts a credential in a message on purpose. This catches the two ways it could happen
    /// anyway: EPA echoing the request into a field this client does report (<c>code</c> or
    /// <c>errorId</c>), and a future edit that adds the value "to make the message actionable". Replacing
    /// the whole string rather than redacting the substring is deliberate — a message that has already
    /// been mangled is not a message worth showing, and the replacement says exactly what happened.
    /// </remarks>
    private static string EnsureNoCredential(string diagnostic, string apiId, string apiKey)
    {
        bool leaks =
            (apiId.Length > 0 && diagnostic.Contains(apiId, StringComparison.OrdinalIgnoreCase))
            || (apiKey.Length > 0 && diagnostic.Contains(apiKey, StringComparison.OrdinalIgnoreCase));

        return leaks
            ? "The RCRAInfo auth call failed, and the description of the failure repeated the API ID or "
              + "Key, so it has been withheld in full. This means EPA echoed the request back in its "
              + "error payload. The HTTP status is reported separately and is safe."
            : diagnostic;
    }

    private static string? EnsureNoCredentialOrNull(string? value, string apiId, string apiKey) =>
        value is null ? null : EnsureNoCredential(value, apiId, apiKey);

    /// <summary>The success payload: the pinned spec's <c>ApiAuthResponse</c>.</summary>
    private sealed class ApiAuthResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("expiration")]
        public DateTimeOffset? Expiration { get; set; }
    }
}
