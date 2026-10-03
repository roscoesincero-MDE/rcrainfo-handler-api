namespace RCRAInfo.Loader.Api;

/// <summary>
/// One call to <c>GET /api/v1/auth/{apiId}/{apiKey}</c>, classified.
/// </summary>
/// <remarks>
/// An interface for one method, which needs a reason. It is here so
/// <see cref="RcraInfoTokenProvider"/>'s behaviour — proactive refresh, one refresh at a time,
/// refresh-and-retry-once — can be tested against a counted, scripted sequence of answers rather than
/// against a stubbed HTTP handler. Those are the parts of D1 that hold state and race; testing them
/// through a fake socket would mean every test also exercises JSON parsing and status classification,
/// and a failure would not say which of the three broke.
/// </remarks>
public interface IRcraInfoAuthClient
{
    /// <summary>Exchanges the credential pair for a bearer token.</summary>
    /// <param name="apiId">The RCRAInfo API ID.</param>
    /// <param name="apiKey">The RCRAInfo API Key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// The outcome. Implementations <b>return</b> failures rather than throwing them, so that the
    /// six-way classification is the only way a caller learns what happened.
    /// </returns>
    Task<ApiAuthResult> AuthenticateAsync(
        string apiId,
        string apiKey,
        CancellationToken cancellationToken = default);
}
