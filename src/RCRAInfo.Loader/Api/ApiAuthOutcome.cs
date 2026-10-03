namespace RCRAInfo.Loader.Api;

/// <summary>
/// What happened when the RCRAInfo auth endpoint was called. One value per <b>operator action</b>,
/// which is the same rule <c>CredentialBootstrapOutcome</c> and the console application's exit codes
/// follow: two outcomes that lead to the same remedy do not need two names, and two that lead to
/// different remedies must never share one.
/// </summary>
public enum ApiAuthOutcome
{
    /// <summary>A token was issued.</summary>
    Succeeded,

    /// <summary>
    /// <c>401</c>. The API ID or Key is wrong, revoked, or belongs to another environment. Remedy:
    /// generate a new pair in RCRAInfo and re-seed the credential file (plan §4.2). Retrying does not
    /// help, so this is never retried — the same reasoning that stops a rejected SQL password from
    /// being retried into an account lockout.
    /// </summary>
    InvalidCredentials,

    /// <summary>
    /// <c>403</c>. The credential is good and the account is not permitted what was asked for. Remedy:
    /// a permissions or scope change at EPA (Analysis G2) — RCRAInfo grants API scope by state and
    /// region. Re-seeding fixes nothing here, and reporting this as a credential failure sends an
    /// operator to the password manager to solve an authorization problem.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// <c>5xx</c>, <c>408</c> or <c>429</c>. EPA's problem, or EPA asking us to slow down. Retryable,
    /// and the only outcome here that is.
    /// </summary>
    ServiceFailure,

    /// <summary>
    /// The request never got an answer: DNS, TLS, a refused connection, a proxy, a timeout. Retryable
    /// in the same way as <see cref="ServiceFailure"/>, but named separately because the remedy an
    /// operator would try first is a network one and not a wait.
    /// </summary>
    Unreachable,

    /// <summary>
    /// EPA answered with something this client cannot use: a success with no token, a body that is not
    /// the documented JSON, an undocumented status. Distinct from <see cref="ServiceFailure"/> because
    /// retrying an unparseable success produces another unparseable success, and because it is the one
    /// outcome that means the pinned spec and the live service have diverged.
    /// </summary>
    Unexpected,
}
