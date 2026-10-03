using System.Net;
using System.Net.Http.Headers;

namespace RCRAInfo.Loader.Api;

/// <summary>
/// Attaches <c>Authorization: Bearer &lt;token&gt;</c> to every RCRAInfo data request, and on a
/// <c>401</c> renews the token and tries once more.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the scheme comes from.</b> The pinned spec (<c>spec/rcrainfo/swagger.json</c>) declares
/// <c>security: [{ api_token: [] }]</c> on every data endpoint and then leaves
/// <c>securityDefinitions</c> as <c>null</c> — so the spec asserts that a token is required and never
/// says how to present it. <c>Authorization: Bearer</c> comes from EPA's RCRAInfo wiki, recorded in
/// Analysis §6.1, and is the one part of this client that rests on documentation outside the pinned spec.
/// If a live call ever returns <c>401</c> with a token known good, this is the first thing to doubt.
/// </para>
/// <para>
/// <b>Why retry a 401 at all</b>, when <see cref="ApiAuthOutcome.InvalidCredentials"/> is explicitly not
/// retryable. The two are different events. A <c>401</c> from the <i>auth</i> endpoint means the API ID
/// and Key are wrong and retrying is pointless. A <c>401</c> from a <i>data</i> endpoint means the token
/// presented was not accepted — which happens for reasons that have nothing to do with the credential:
/// the token expired between the margin check and the server's clock, or EPA invalidated it early. Plan
/// §D1 requires the retry for exactly that case. It happens <b>once</b>: if a freshly issued token is
/// also refused, the response is returned as it stands, because a loop of renew-and-retry against an
/// endpoint that will keep refusing is a way to get an API account throttled.
/// </para>
/// <para>
/// <b>Only requests with no body are retried.</b> Every RCRAInfo call this application makes is a
/// <c>GET</c>, so the restriction costs nothing today; without it, the first <c>POST</c> added later would
/// retry with a consumed content stream and fail in a way that looks like a service problem.
/// </para>
/// </remarks>
public sealed class ApiTokenHandler : DelegatingHandler
{
    private readonly IApiTokenProvider tokens;

    /// <summary>Creates the handler.</summary>
    /// <param name="tokens">The token provider.</param>
    public ApiTokenHandler(IApiTokenProvider tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        this.tokens = tokens;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApiToken token = await tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        HttpResponseMessage response = await base
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized || request.Content is not null)
        {
            return response;
        }

        ApiToken renewed = await tokens.RefreshAsync(token, cancellationToken).ConfigureAwait(false);

        if (ReferenceEquals(renewed, token))
        {
            // The provider handed back the same token -- it was issued moments ago and the refresh floor
            // declined to replace it. Sending the identical token again would produce the identical 401.
            return response;
        }

        // The first response is disposed only now that a second attempt is certain: returning it above and
        // disposing it here would hand the caller a response whose content stream is already gone.
        response.Dispose();

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", renewed.Value);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
