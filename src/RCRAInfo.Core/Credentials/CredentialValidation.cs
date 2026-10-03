namespace RCRAInfo.Core.Credentials;

/// <summary>
/// The answer to "are these credentials actually usable?" — AR4's "login using the password which
/// will verify that the password is valid", and for the API Key its analogue, a successful
/// <c>GET /api/v1/auth/{apiId}/{apiKey}</c> (Analysis §6.1, G6).
/// </summary>
/// <param name="IsValid">Whether the credentials were accepted by whatever they authenticate to.</param>
/// <param name="FailureMessage">
/// Why they were rejected, for the operator. Empty when <paramref name="IsValid"/> is
/// <see langword="true"/>.
/// <para>
/// <b>This string must never contain the credential it failed to validate.</b> It is logged, and in
/// this solution the log is readable by the monitoring web application (AR8). A SQL login failure
/// message names the login and not the password, and an EPA auth failure is an HTTP status — so the
/// natural implementations are already safe. The one that is not is a caller that helpfully appends
/// "(password: ...)" to make the message easier to act on.
/// </para>
/// </param>
public sealed record CredentialValidation(bool IsValid, string FailureMessage)
{
    /// <summary>The credentials were accepted.</summary>
    public static CredentialValidation Valid { get; } = new(true, string.Empty);

    /// <summary>The credentials were rejected, for the stated reason.</summary>
    /// <param name="failureMessage">
    /// Why, for an operator. Must not contain the credential — see the remarks on
    /// <see cref="FailureMessage"/>.
    /// </param>
    public static CredentialValidation Invalid(string failureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        return new CredentialValidation(false, failureMessage);
    }
}

/// <summary>
/// Proves that credentials work, by using them. Supplied by the application rather than implemented
/// here: RCRAInfo.Core knows nothing about SQL Server or HTTP, and both of the real validators are
/// one of those two things.
/// </summary>
/// <param name="credentials">The plaintext credentials to try.</param>
/// <param name="cancellationToken">Cancellation.</param>
/// <returns>Whether they were accepted.</returns>
/// <remarks>
/// Called <b>before</b> anything is encrypted, on every path — that is G5's first two rows, and the
/// whole of "never encrypt a password that failed validation". It is also called on the already-sealed
/// path, because a password rotated upstream is G5's fourth row and the only way to detect it is to
/// try it.
///
/// An implementation is expected to <b>return</b> a rejection rather than throw one. A thrown
/// exception is treated as a rejection too, so a validator that throws on a bad login is not a defect
/// — but its exception message goes to the operator, and an exception from an HTTP stack carries a URI
/// with a query string, which is why AR8 forbids logging one whole (see
/// <see cref="CredentialValidation.FailureMessage"/>).
/// </remarks>
public delegate Task<CredentialValidation> CredentialValidator(
    ApplicationCredentials credentials,
    CancellationToken cancellationToken);
