namespace RCRAInfo.Core.Credentials;

/// <summary>
/// The plaintext secrets from one credential file, in memory only. Never written anywhere by this
/// type, and never rendered by it either.
/// </summary>
/// <param name="SqlPassword">
/// The password for this application's own SQL login (AR3). Required: every environment authenticates
/// with a password, so an absent one is a malformed file rather than a default.
/// </param>
/// <param name="ApiId">
/// The RCRAInfo API ID, or <see langword="null"/> for the monitoring application, which never calls
/// EPA (AR2) and has no legitimate use for it — Analysis §6.1.
/// </param>
/// <param name="ApiKey">The RCRAInfo API Key, or <see langword="null"/> for the same reason.</param>
/// <remarks>
/// <b><see cref="ToString"/> is overridden, and that override is load-bearing.</b> A positional record
/// generates a <c>ToString</c> that prints every member, so
/// <c>logger.LogDebug("credentials: {Credentials}", credentials)</c> — a line that looks entirely
/// ordinary in a diff, and that a developer writes while chasing a login failure — would put the SQL
/// password and the API Key into a log. In this solution the log is
/// <c>logs.ExecutionLog</c>, which the monitoring web application can <b>read</b> (AR8). The generated
/// <c>ToString</c> is the only member of this record that can leak, and it is the one nobody calls on
/// purpose.
///
/// <c>Equals</c> and <c>Deconstruct</c> still expose the values, deliberately: both are explicit reads
/// by a caller that has already decided to handle the plaintext.
/// </remarks>
public sealed record ApplicationCredentials(string SqlPassword, string? ApiId, string? ApiKey)
{
    /// <summary>A description that names the fields present and reveals none of their values.</summary>
    public override string ToString() =>
        $"ApplicationCredentials {{ SqlPassword = <redacted>, "
        + $"ApiId = {(ApiId is null ? "<absent>" : "<redacted>")}, "
        + $"ApiKey = {(ApiKey is null ? "<absent>" : "<redacted>")} }}";
}
