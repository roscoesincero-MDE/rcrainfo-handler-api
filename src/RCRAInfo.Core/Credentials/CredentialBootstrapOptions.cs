namespace RCRAInfo.Core.Credentials;

/// <summary>
/// The two timings <see cref="CredentialBootstrapper"/> needs, separated from it so a test can make the
/// lock contend in milliseconds rather than in minutes.
/// </summary>
public sealed record CredentialBootstrapOptions
{
    /// <summary>The defaults, which are what both applications use in every environment.</summary>
    public static CredentialBootstrapOptions Default { get; } = new();

    /// <summary>
    /// How long to wait for another process to finish with the credential file before giving up.
    /// </summary>
    /// <remarks>
    /// <b>This must exceed however long validation takes</b>, and validation is a network round trip:
    /// a SQL login, or an EPA auth call. The process that wins the lock holds it across that call —
    /// it has to, because G5 requires the <c>Encrypted</c> flag to be re-read <i>inside</i> the lock —
    /// so the process that loses waits out the winner's entire login, including the winner's connect
    /// timeout if the server is slow to answer. Sixty seconds is four times the SQL Server default
    /// connect timeout of fifteen, which leaves room for a retry inside the validator.
    /// <para>
    /// Set too low, the symptom is not a hang. It is one of the two co-resident applications reporting
    /// <see cref="CredentialBootstrapOutcome.Locked"/> on the one run per environment where both start
    /// at once — the seeding run, under G18, which the plan calls the normal case rather than an edge
    /// case.
    /// </para>
    /// </remarks>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait between attempts to open the file.</summary>
    /// <remarks>
    /// There is no file-lock wait primitive on Windows for this: a sharing violation is an error, not a
    /// blocking call, so the wait is a poll. Fifty milliseconds is short enough that the loser starts
    /// promptly after the winner finishes and long enough that a minute of waiting is about a thousand
    /// attempts rather than a spin.
    /// </remarks>
    public TimeSpan LockPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);
}
