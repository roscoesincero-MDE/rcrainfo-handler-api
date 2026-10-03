using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RCRAInfo.Core.Credentials;

/// <summary>
/// The AR4 mechanism: Windows DPAPI at <see cref="DataProtectionScope.LocalMachine"/> scope with a
/// per-application <c>optionalEntropy</c> value, Base64 for the file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="DataProtectionScope.LocalMachine"/> and not <c>CurrentUser</c></b> (Analysis
/// §6.1): the two applications run under different identities — a Task Scheduler service account and
/// an IIS application pool. <c>CurrentUser</c> binds the ciphertext to one Windows profile and needs
/// that profile loaded, which is the classic "works interactively, fails when scheduled" failure.
/// <c>LocalMachine</c> is robust under both hosts, and its weakness — any process on the box can
/// decrypt — is answered by the file ACL, not by the scope.
/// </para>
/// <para>
/// <b>The entropy is derived from a constant, and that is deliberate.</b> It is a fixed string per
/// application, hashed to 32 bytes so the length is uniform. It is not a key, it is not secret, and
/// hardening it would be theatre: it ships inside the binary of the application it belongs to, on the
/// same machine as the ciphertext. See <see cref="ApplicationIdentity"/> for what it does buy.
/// </para>
/// <para>
/// The version suffix in each literal exists so that a future decision to change the entropy is
/// forced to be a deliberate, named change. Changing these strings invalidates every sealed file in
/// every environment — the same consequence as a machine rebuild, and it needs the same re-seed visit.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly byte[] entropy;

    /// <summary>Creates a protector for one application's credential file.</summary>
    /// <param name="application">Whose file this protector seals and opens.</param>
    public DpapiSecretProtector(ApplicationIdentity application)
    {
        string purpose = application switch
        {
            ApplicationIdentity.Loader => "RCRAInfo.Loader/AR4/entropy/v1",
            ApplicationIdentity.Monitor => "RCRAInfo.Monitor/AR4/entropy/v1",

            // Not unreachable: an enum parameter accepts any int. A cast value would otherwise seal
            // with all-zero entropy, which would still round-trip in the same process and fail
            // nowhere until another build opened the file.
            _ => throw new ArgumentOutOfRangeException(
                     nameof(application),
                     application,
                     "No DPAPI entropy is defined for this application."),
        };

        entropy = SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
    }

    /// <inheritdoc/>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        // The intermediate byte array is the plaintext in a second place in memory, so it is zeroed
        // rather than left for the garbage collector. This is hygiene, not a boundary -- the string
        // itself is immutable and cannot be zeroed -- but it is one copy fewer in a process dump.
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);

        try
        {
            byte[] sealedBytes = ProtectedData.Protect(bytes, entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(sealedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc/>
    public string Unprotect(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        byte[] sealedBytes;

        try
        {
            sealedBytes = Convert.FromBase64String(ciphertext);
        }
        catch (FormatException error)
        {
            // The length is safe to report and is the one detail that separates "the flag is set over
            // a value that was never sealed" from "the ciphertext was truncated by an editor". The
            // VALUE is not reported: this message reaches an operator, and on the flag-set-over-
            // plaintext path the value is the password.
            throw new CryptographicException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The stored value is not Base64, so it was not produced by this application "
                    + "({0} character(s)). Either the file records Encrypted: true over a value that "
                    + "was never sealed, or the value was edited by hand.",
                    ciphertext.Length),
                error);
        }

        byte[] plaintextBytes = ProtectedData.Unprotect(
            sealedBytes, entropy, DataProtectionScope.LocalMachine);

        try
        {
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}
