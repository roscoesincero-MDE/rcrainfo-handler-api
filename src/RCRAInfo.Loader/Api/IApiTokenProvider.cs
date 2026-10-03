namespace RCRAInfo.Loader.Api;

/// <summary>
/// Supplies the bearer token for RCRAInfo requests, renewing it when it is due.
/// </summary>
public interface IApiTokenProvider
{
    /// <summary>The current token, obtaining or renewing one if needed.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A token believed usable.</returns>
    /// <exception cref="RcraInfoAuthException">No token could be obtained.</exception>
    Task<ApiToken> GetTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces a token the caller has found unusable.</summary>
    /// <param name="rejected">
    /// The token that was rejected. Used to tell "renew this" from "someone already renewed it": if the
    /// cached token is no longer the one that failed, the caller simply lost a race and is handed the
    /// newer one instead of triggering a second, pointless auth call.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A token believed usable.</returns>
    /// <exception cref="RcraInfoAuthException">No token could be obtained.</exception>
    Task<ApiToken> RefreshAsync(ApiToken? rejected, CancellationToken cancellationToken = default);
}
