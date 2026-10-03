namespace RCRAInfo.Loader.Api;

/// <summary>
/// The result of one call to the auth endpoint: what happened, the token if one was issued, and a
/// description safe to show an operator.
/// </summary>
/// <param name="Outcome">Which of the six things happened.</param>
/// <param name="Token">The token, when <paramref name="Outcome"/> is
/// <see cref="ApiAuthOutcome.Succeeded"/>; otherwise <see langword="null"/>.</param>
/// <param name="Diagnostic">
/// Why, for a human, in one sentence. <b>Guaranteed to contain neither half of the credential</b> —
/// <see cref="RcraInfoAuthClient"/> checks that before returning, rather than relying on every message
/// having been written carefully.
/// </param>
/// <param name="StatusCode">EPA's HTTP status, or <see langword="null"/> when no response arrived.</param>
/// <param name="ErrorCode">EPA's <c>ApiError.code</c> when one could be parsed.</param>
public sealed record ApiAuthResult(
    ApiAuthOutcome Outcome,
    ApiToken? Token,
    string Diagnostic,
    int? StatusCode = null,
    string? ErrorCode = null)
{
    /// <summary>Whether a token was issued.</summary>
    public bool Succeeded => Outcome == ApiAuthOutcome.Succeeded;

    /// <summary>
    /// Whether trying again could plausibly produce a different answer.
    /// </summary>
    /// <remarks>
    /// Only the two "EPA or the network is having a moment" outcomes are retryable.
    /// <see cref="ApiAuthOutcome.InvalidCredentials"/> is not, and that is the important one: a rejected
    /// credential retried in a loop is how an account gets locked out, which is the lesson AR4 already
    /// recorded for the SQL login and applies unchanged to an EPA API account nobody here administers.
    /// </remarks>
    public bool IsRetryable =>
        Outcome is ApiAuthOutcome.ServiceFailure or ApiAuthOutcome.Unreachable;

    /// <summary>The token, or an exception naming the outcome.</summary>
    /// <returns>The issued token.</returns>
    /// <exception cref="RcraInfoAuthException">The call did not produce a token.</exception>
    public ApiToken Require() =>
        Token ?? throw new RcraInfoAuthException(this);
}
