namespace RCRAInfo.Core.Credentials;

/// <summary>
/// How <see cref="CredentialBootstrapper.BootstrapAsync"/> ended. One member per operator action:
/// if two outcomes would send the operator to the same place, they are the same outcome.
/// </summary>
public enum CredentialBootstrapOutcome
{
    /// <summary>
    /// The file was already sealed, opened, and its credentials were accepted. The steady state, and
    /// the only outcome that should ever appear in a log after the first run in an environment.
    /// </summary>
    Ready = 1,

    /// <summary>
    /// The file held plaintext, the credentials were accepted, and the file has been rewritten with
    /// <c>Encrypted: true</c>. G5 row 1. Expected exactly once per application per machine.
    /// </summary>
    Sealed = 2,

    /// <summary>
    /// The credentials were rejected. G5 rows 2 and 4. Plaintext, if that is what the file held, has
    /// been <b>left exactly as it was</b>, so an operator typo is still correctable by editing the
    /// file.
    /// </summary>
    ValidationFailed = 3,

    /// <summary>
    /// The file says <c>Encrypted: true</c> and the value could not be recovered. G5 row 3 — usually a
    /// machine rebuild or a change of service account. Nothing has been written, and nothing will be:
    /// there is no fallback to plaintext and no silent re-seal.
    /// </summary>
    DecryptionFailed = 4,

    /// <summary>
    /// The credentials were accepted, but the sealed file could not be written. The secret is still
    /// valid and still in plaintext on disk, which is why this is a failure and not a warning — see
    /// the remarks on <see cref="CredentialBootstrapResult"/>.
    /// </summary>
    SealFailed = 5,

    /// <summary>The credential file does not exist. The environment has not been seeded (plan §4.2).</summary>
    NotSeeded = 6,

    /// <summary>
    /// The file exists but cannot be read as a credential file. Reported separately from
    /// <see cref="NotSeeded"/> because the remedy differs: a missing file is created from the template,
    /// a malformed one is repaired without losing whatever else it holds.
    /// </summary>
    Malformed = 7,

    /// <summary>
    /// Another process held the file for longer than the configured lock timeout. Under G18 the two
    /// applications are co-resident, so contention on a first run is normal and is waited out; this
    /// outcome means the wait was not enough.
    /// </summary>
    Locked = 8,

    /// <summary>
    /// The file could not be opened for read and write by this identity. Almost always the ACL applied
    /// by <c>Set-CredentialFileAcl.ps1</c> naming a different account than the one the application is
    /// actually running as.
    /// </summary>
    AccessDenied = 9,
}

/// <summary>
/// What happened, what to tell the operator, and — only on success — the credentials.
/// </summary>
/// <param name="Outcome">Which of the G5 rows this run took.</param>
/// <param name="Credentials">
/// The plaintext credentials on <see cref="CredentialBootstrapOutcome.Ready"/> and
/// <see cref="CredentialBootstrapOutcome.Sealed"/>; <see langword="null"/> on every failure.
/// </param>
/// <param name="Message">
/// One paragraph for the operator, naming the file and the next action. Contains no credential on any
/// path — that is asserted by test, for every outcome, including the paths where the value that failed
/// <i>is</i> the password.
/// </param>
/// <param name="LockWaits">
/// How many times the file was found locked by another process before it opened. Zero in the ordinary
/// case. Reported rather than swallowed so that a test of the concurrent first run can assert the lock
/// was actually <i>contended</i> — otherwise two runs that happened to serialise would pass the test
/// while proving nothing about the lock.
/// </param>
/// <remarks>
/// <b>Why <see cref="CredentialBootstrapOutcome.SealFailed"/> is a failure.</b> At that point the
/// credentials are known good, so the application could carry on and do its night's work. It does not,
/// because the alternative is worse in a way nobody would notice: the load succeeds every night, the
/// file stays in plaintext, and the one visit the runbook allocates to seeding this machine has already
/// happened. A non-zero exit brings the operator back while they still remember why they were there.
/// </remarks>
public sealed record CredentialBootstrapResult(
    CredentialBootstrapOutcome Outcome,
    ApplicationCredentials? Credentials,
    string Message,
    int LockWaits)
{
    /// <summary>Whether the application may continue.</summary>
    public bool Succeeded =>
        Outcome is CredentialBootstrapOutcome.Ready or CredentialBootstrapOutcome.Sealed;

    /// <summary>
    /// The credentials, or an exception if this result is a failure. For the call site that has already
    /// checked <see cref="Succeeded"/> and would otherwise write <c>result.Credentials!</c>, which is
    /// the same assertion made invisibly.
    /// </summary>
    /// <exception cref="InvalidOperationException">This result is a failure.</exception>
    public ApplicationCredentials Require() =>
        Credentials
        ?? throw new InvalidOperationException(
               $"Credential bootstrap ended in {Outcome} and produced no credentials. {Message}");
}
