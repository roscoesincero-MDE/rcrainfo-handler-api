using System.Runtime.Versioning;
using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Core.Tests;

/// <summary>
/// The three secret values every credential test uses, and the file shapes they go into.
/// </summary>
/// <remarks>
/// The values are deliberately unmistakable rather than plausible. Half of what these tests assert is
/// that a secret did <b>not</b> reach somewhere — an operator message, a <c>ToString</c>, a log — and a
/// password of "Password1" cannot be searched for with any confidence. Each value carries a marker that
/// occurs nowhere else in the solution, so a substring search for it means what it says.
/// </remarks>
internal static class Secrets
{
    internal const string SqlPassword = "sql-password-QQ7MARKERf3";
    internal const string ApiId = "api-id-QQ7MARKERf3";
    internal const string ApiKey = "api-key-QQ7MARKERf3";

    /// <summary>Every secret value, for the assertions that check all three are absent.</summary>
    internal static string[] All => [SqlPassword, ApiId, ApiKey];

    /// <summary>The loader's file shape: a SQL password and the API pair.</summary>
    internal static string LoaderFile(bool encrypted = false) =>
        $$"""
        {
          "Encrypted": {{(encrypted ? "true" : "false")}},
          "SqlPassword": "{{SqlPassword}}",
          "ApiId": "{{ApiId}}",
          "ApiKey": "{{ApiKey}}"
        }
        """;

    /// <summary>
    /// The monitor's file shape: a SQL password and nothing else, because the monitoring application
    /// never calls EPA (Analysis §6.1). One secret means one <c>Protect</c> call, which is what makes the
    /// concurrency assertion a count rather than an inequality.
    /// </summary>
    internal static string MonitorFile(bool encrypted = false) =>
        $$"""
        {
          "Encrypted": {{(encrypted ? "true" : "false")}},
          "SqlPassword": "{{SqlPassword}}"
        }
        """;
}

/// <summary>
/// Alters a sealed value the way a truncating editor or a bad copy does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it alters is measured, not chosen.</b> The obvious arrangement — flip a character near the
/// front of the Base64 — does not fail. A byte-by-byte probe of a 246-byte
/// <c>LocalMachine</c> blob on this project found that <c>CryptUnprotectData</c> tolerates exactly
/// 16 of the 246 byte offsets: <b>4 through 19</b>, which is the provider GUID in the
/// <c>DPAPI_BLOB</c> header. Windows ignores that field and uses the default provider regardless. Every
/// other offset — version, master key GUID, description, salt, ciphertext, HMAC — fails.
/// </para>
/// <para>
/// Base64 character 10 covers bytes 6 to 8, squarely inside the tolerated window, so the first version
/// of the two tamper tests altered the blob and then watched it decrypt perfectly. Both tests reported
/// the mutation as undetected, which is exactly what a test asserting the opposite is for. This helper
/// alters the <b>last</b> non-padding character instead, which is always in the ciphertext and its MAC
/// whatever the plaintext length.
/// </para>
/// </remarks>
internal static class Tamper
{
    /// <summary>Changes one character of a Base64 payload, at an offset DPAPI actually authenticates.</summary>
    /// <param name="base64">The sealed value.</param>
    /// <returns>The same value with one character different.</returns>
    internal static string OneCharacterOf(string base64)
    {
        int at = base64.TrimEnd('=').Length - 1;

        return base64.Remove(at, 1).Insert(at, base64[at] == 'A' ? "B" : "A");
    }
}

/// <summary>
/// A real DPAPI protector that counts what it was asked to do.
/// </summary>
/// <remarks>
/// Wrapping the real thing rather than replacing it. Every G5 row that a substitute could fake is
/// reachable with genuine DPAPI — a tampered blob, the other application's entropy, a flag set over a
/// value that was never sealed — so the only thing this adds is the count, and the count is what
/// distinguishes "the lock worked" from "the two runs happened not to overlap".
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class SpyProtector(ApplicationIdentity application) : ISecretProtector
{
    private readonly DpapiSecretProtector inner = new(application);
    private int protects;
    private int unprotects;

    /// <summary>How many values have been sealed through this protector.</summary>
    internal int Protects => Volatile.Read(ref protects);

    /// <summary>How many values have been opened through this protector.</summary>
    internal int Unprotects => Volatile.Read(ref unprotects);

    public string Protect(string plaintext)
    {
        Interlocked.Increment(ref protects);
        return inner.Protect(plaintext);
    }

    public string Unprotect(string ciphertext)
    {
        Interlocked.Increment(ref unprotects);
        return inner.Unprotect(ciphertext);
    }
}

/// <summary>The three validator behaviours the G5 matrix needs, and a record of being called.</summary>
internal sealed class RecordingValidator
{
    private readonly Func<ApplicationCredentials, Task<CredentialValidation>> behaviour;
    private int calls;

    private RecordingValidator(Func<ApplicationCredentials, Task<CredentialValidation>> behaviour) =>
        this.behaviour = behaviour;

    /// <summary>How many times the validator was invoked.</summary>
    internal int Calls => Volatile.Read(ref calls);

    /// <summary>The credentials it was last given, for the tests that check what was handed over.</summary>
    internal ApplicationCredentials? Seen { get; private set; }

    /// <summary>Accepts whatever it is given.</summary>
    internal static RecordingValidator Accepts() =>
        new(_ => Task.FromResult(CredentialValidation.Valid));

    /// <summary>Rejects, the way a failed SQL login does — by returning, not by throwing.</summary>
    internal static RecordingValidator Rejects(string message) =>
        new(_ => Task.FromResult(CredentialValidation.Invalid(message)));

    /// <summary>Throws, the way an HTTP stack does.</summary>
    internal static RecordingValidator Throws(Exception error) =>
        new(_ => Task.FromException<CredentialValidation>(error));

    /// <summary>Accepts, but not until the returned gate is released.</summary>
    internal static RecordingValidator AcceptsAfter(TaskCompletionSource gate) =>
        new(async _ =>
        {
            await gate.Task;
            return CredentialValidation.Valid;
        });

    /// <summary>The delegate to hand to <see cref="CredentialBootstrapper.BootstrapAsync"/>.</summary>
    internal CredentialValidator Delegate =>
        (credentials, _) =>
        {
            Interlocked.Increment(ref calls);
            Seen = credentials;
            return behaviour(credentials);
        };
}
