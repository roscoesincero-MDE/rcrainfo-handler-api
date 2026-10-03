using System.Security.Cryptography;

namespace RCRAInfo.Core.Credentials;

/// <summary>
/// Turns a plaintext secret into a string that can sit in a file, and back again.
/// </summary>
/// <remarks>
/// An interface rather than a direct call to
/// <see cref="System.Security.Cryptography.ProtectedData"/> for one reason that matters and one that
/// does not.
///
/// The reason that matters: G5's third row — <c>Encrypted: true</c> and decryption fails — is the row
/// with the strictest requirement on this code ("never silently overwrite or fall back to plaintext"),
/// and it is the row that in production is caused by a machine rebuild. A test cannot rebuild the
/// machine. It can substitute a protector that throws, and it can also produce the real thing three
/// other ways: tamper with the ciphertext, seal with one application's entropy and open with the
/// other's, or set the flag to <c>true</c> over a value that was never sealed at all. All four are
/// exercised; three of them go through real DPAPI.
///
/// The reason that does not matter: portability. Nothing here is expected to run off Windows. G18 puts
/// both applications on Windows Server in all three environments.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>Seals a plaintext secret. The result is safe to write to a file.</summary>
    /// <param name="plaintext">The secret. Never logged, never echoed in an exception message.</param>
    /// <returns>The sealed form, as a string that survives a JSON round trip.</returns>
    string Protect(string plaintext);

    /// <summary>Recovers a secret sealed by <see cref="Protect"/>.</summary>
    /// <param name="ciphertext">The sealed form as read from the file.</param>
    /// <returns>The plaintext secret.</returns>
    /// <exception cref="CryptographicException">
    /// The value could not be recovered — for any reason, including a value that is not even in the
    /// right encoding. One exception type, because the caller's only correct response to every cause
    /// is the same: refuse to start and tell the operator to re-seed. An implementation that let a
    /// <see cref="FormatException"/> escape instead would reach a <c>catch</c> block written for
    /// cryptographic failure and crash with a stack trace rather than the actionable message G5
    /// requires.
    /// </exception>
    string Unprotect(string ciphertext);
}
