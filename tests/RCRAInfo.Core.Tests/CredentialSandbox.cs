using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RCRAInfo.Core.Credentials;

namespace RCRAInfo.Core.Tests;

/// <summary>
/// A throwaway directory holding one credential file, plus the few things every credential test needs
/// to do to it.
/// </summary>
/// <remarks>
/// A directory rather than a bare temp file, because two of the assertions are about the directory: that
/// sealing never leaves a second file behind, and that the file's ACL survives being rewritten. Both are
/// meaningless without a container to inspect.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class CredentialSandbox : IDisposable
{
    internal CredentialSandbox()
    {
        Directory = Path.Combine(
            Path.GetTempPath(), "rcrainfo-credential-" + Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(Directory);
        File = Path.Combine(Directory, CredentialFile.DefaultFileName);
    }

    /// <summary>The temporary directory.</summary>
    internal string Directory { get; }

    /// <summary>The credential file, whether or not it exists yet.</summary>
    internal string File { get; }

    /// <summary>Writes the credential file's text, replacing whatever was there.</summary>
    internal void Write(string json) => System.IO.File.WriteAllText(File, json);

    /// <summary>The credential file's text.</summary>
    internal string Read() => System.IO.File.ReadAllText(File);

    /// <summary>Every file in the sandbox, by name, sorted.</summary>
    internal string[] Files =>
        [.. System.IO.Directory.GetFiles(Directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    /// <summary>
    /// Holds the credential file open exclusively, the way the other application would while it seals.
    /// </summary>
    internal FileStream HoldExclusively() =>
        System.IO.File.Open(File, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    /// <summary>
    /// Locks the credential file down the way <c>Set-CredentialFileAcl.ps1</c> does: inheritance off, the
    /// current account full control, and an explicit deny for a stand-in application pool identity.
    /// </summary>
    /// <remarks>
    /// A <b>fresh</b> <see cref="FileSecurity"/> every call, and not by accident. Measured on this
    /// project: <see cref="ObjectSecurity"/> clears its modified-sections flag once persisted, so a
    /// reused instance writes <i>nothing</i> on a second <c>SetAccessControl</c> — silently. An earlier
    /// version of the probe that produced the table in
    /// <see cref="CredentialBootstrapper"/>'s remarks reused one, and reported that writing a file's
    /// contents had removed its ACL. It had not; the ACL had never been applied.
    /// </remarks>
    internal void ApplyDenyReadAcl()
    {
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().Name,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        security.AddAccessRule(new FileSystemAccessRule(
            DeniedIdentity,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));

        new FileInfo(File).SetAccessControl(security);
    }

    /// <summary>
    /// The stand-in for the IIS application pool identity: a well-known local account that resolves on
    /// every Windows machine, so the test does not need a pool to exist.
    /// </summary>
    internal const string DeniedIdentity = @"NT AUTHORITY\NETWORK SERVICE";

    /// <summary>Whether inheritance is still off and the deny ACE is still present.</summary>
    internal (bool Protected, bool Denied) DescribeAcl()
    {
        FileSecurity security = new FileInfo(File).GetAccessControl(AccessControlSections.Access);

        bool denied = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(NTAccount))
            .Cast<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Deny
                         && string.Equals(
                                rule.IdentityReference.Value,
                                DeniedIdentity,
                                StringComparison.OrdinalIgnoreCase));

        return (security.AreAccessRulesProtected, denied);
    }

    public void Dispose()
    {
        try
        {
            // The read-only attribute is one of the arrangements here, and Directory.Delete refuses a
            // read-only file. Cleared first so that test leaves no temp litter behind.
            foreach (string file in System.IO.Directory.GetFiles(Directory))
            {
                System.IO.File.SetAttributes(file, FileAttributes.Normal);
            }

            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // The corpus of temp directories is not the subject of any assertion, and a failed cleanup
            // must not turn a passing test red or mask a failing one's message.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
