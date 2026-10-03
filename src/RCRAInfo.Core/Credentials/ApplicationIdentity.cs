namespace RCRAInfo.Core.Credentials;

/// <summary>
/// Which of the two applications a credential file belongs to (AR3: each has its own SQL login, so
/// each has its own credential file).
/// </summary>
/// <remarks>
/// This selects the DPAPI <c>optionalEntropy</c> value, and that is the whole of its job. Analysis
/// §6.1 is explicit that the entropy is <b>not</b> a security boundary: G18 puts both applications on
/// one machine, <see cref="System.Security.Cryptography.DataProtectionScope.LocalMachine"/> ciphertext
/// is decryptable by any process there, and the entropy is a byte array sitting in the other
/// application's own binary. The NTFS ACL is the control.
///
/// What the entropy does buy is worth having anyway: a blob sealed for one application fails loudly
/// in the other rather than decrypting into the wrong credential. Copying the loader's
/// <c>secrets.json</c> into the monitor's directory is a plausible operator mistake, and the failure
/// mode without entropy is the monitor connecting to SQL Server as the loader.
/// </remarks>
public enum ApplicationIdentity
{
    /// <summary>The console application (AR1), run by Windows Task Scheduler.</summary>
    Loader = 1,

    /// <summary>The web application (AR2), run under an IIS application pool.</summary>
    Monitor = 2,
}
