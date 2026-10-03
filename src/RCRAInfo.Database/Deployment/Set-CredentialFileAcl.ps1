<#
.SYNOPSIS
    Locks the loader's credential file down, and then proves the lock works.

.DESCRIPTION
    G18 puts the console application and the web application on the SAME MACHINE in all three
    environments. The console application stores its SQL password DPAPI-encrypted with the
    LocalMachine scope, which means the ciphertext can be decrypted by ANY PROCESS ON THAT MACHINE
    that can read the file. Not by any user: by any process. The optionalEntropy value is
    obfuscation, not a boundary, because it lives in the same binary the attacker already has.

    So the file's ACL is the actual security boundary for that credential, and the one identity that
    must not get past it is the IIS application pool running the web application. The web app is
    monitoring only; it has its own SQL login with its own rights; it has no business holding the
    loader's.

    An ACL that was set but never tested is a hope. This script therefore does three things, and the
    third is the point:

      1. Removes inheritance, so a permission granted higher up the tree cannot leak in later. This
         is the failure a one-time review will not catch: the ACL is correct on the day it is set,
         and a group is added to the parent folder six months later.
      2. Writes an explicit ACL: SYSTEM and Administrators full, the loader identity read/write, and
         an explicit DENY for the application pool identity.
      3. ASKS WINDOWS what access that identity would actually get, by running a real access check
         against the file's security descriptor through the Authz API. Reading the ACE back out and
         recognising it is not a test -- that only confirms the script can read its own writing. An
         access check is the answer that accounts for ACE ORDER and GROUP MEMBERSHIP, which is where
         a hand-built ACL goes wrong.

    Two things established by measurement on this project, recorded because both cost an hour:

      - GetEffectiveRightsFromAcl, the obvious API for step 3, fails with ERROR_NO_SUCH_DOMAIN
        (1355) on a domain-joined workstation for both the name and the SID trustee form. Microsoft
        recommends the Authz API instead, and Authz answers correctly for the same input.
      - The ACL is written from a FRESH FileSecurity object through File.SetAccessControl, not
        through Get-Acl / Set-Acl. Only the sections a FileSecurity has modified get written, so
        this touches the DACL and nothing else. Set-Acl round-trips the whole descriptor and fails
        with "does not possess the 'SeSecurityPrivilege' privilege" on an already-protected file
        when the caller is not an administrator.

    WHAT THIS SCRIPT DOES NOT DO: it does not log on as the application pool identity and open the
    file. That is the strongest possible evidence and it cannot be produced here -- a virtual
    account has no password, and LogonUser for one needs SeTcbPrivilege, which means LocalSystem.
    The access check below is the same computation the kernel performs on that open, run against the
    same descriptor. The genuine end-to-end read attempt belongs to Workstream C, once the web
    application and its application pool exist: browse to a page that deliberately tries to read the
    loader's file and confirm it fails.

.PARAMETER Path
    The credential file: the loader's secrets.json, which is CredentialFile.DefaultFileName and sits
    beside the executable. AR4 keeps the encrypted SQL password and the RCRAInfo API pair in it and
    rewrites the file in place on first run. NOT appsettings.json -- that file holds the server, the
    database and the API base address, and by design holds no secret at all.

.PARAMETER LoaderIdentity
    The account the console application runs as under Task Scheduler. Gets read and write: AR4 has
    the application rewrite the file to replace the plaintext password with ciphertext on first run.

.PARAMETER AppPoolIdentity
    The IIS application pool identity to deny. For a standard application pool this is
    'IIS AppPool\<pool name>', and the pool must already exist for the name to resolve.

.PARAMETER SelfTest
    Ignores every other parameter and exercises the write-and-verify mechanism end to end against a
    temporary file and a well-known local account, in BOTH directions: an allowed identity must be
    reported as having read access, and a denied one must not. Run this after any change to this
    script, and on a new machine before trusting it. A verifier that always answers "no access"
    passes the check that matters and is worthless.

.EXAMPLE
    .\Set-CredentialFileAcl.ps1 -SelfTest

.EXAMPLE
    .\Set-CredentialFileAcl.ps1 `
        -Path 'C:\Apps\RCRAInfo.Loader\secrets.json' `
        -LoaderIdentity 'MDE\svc-rcrainfo-loader' `
        -AppPoolIdentity 'IIS AppPool\RCRAInfoMonitor'

.NOTES
    Run this elevated. The ACL it writes grants Administrators full control and does NOT grant the
    person running it anything, so a non-administrator who applies it loses the ability to revise it
    afterwards except by virtue of owning the file. The script warns before doing that.

    Windows PowerShell 5.1 is the floor.
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'Apply')]
param
(
    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [string] $Path,

    [Parameter(Mandatory = $true, ParameterSetName = 'Apply')]
    [string] $LoaderIdentity,

    [Parameter(ParameterSetName = 'Apply')]
    [string] $AppPoolIdentity = 'IIS AppPool\RCRAInfoMonitor',

    [Parameter(Mandatory = $true, ParameterSetName = 'SelfTest')]
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
# The access check, performed by Windows rather than reasoned about by this script.
#
# AuthzAccessCheck is the user-mode form of the kernel's access check. Given a security descriptor and
# a client context built from a SID, it returns the access mask that identity would actually be
# granted, having applied ACE order and group membership.
# ---------------------------------------------------------------------------------------------------
if (-not ('RCRAInfo.AccessCheck' -as [type]))
{
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RCRAInfo
{
    /// <summary>The access mask an identity would be granted on a security descriptor.</summary>
    public static class AccessCheck
    {
        // FILE_READ_DATA: the single bit that decides whether the DPAPI ciphertext can be obtained.
        public const uint FileReadData = 0x0001;

        private const uint NoAudit          = 0x0001;  // AUTHZ_RM_FLAG_NO_AUDIT
        private const uint SkipTokenGroups  = 0x0002;  // AUTHZ_SKIP_TOKEN_GROUPS
        private const uint MaximumAllowed   = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccessRequest
        {
            public uint   DesiredAccess;
            public IntPtr PrincipalSelfSid;
            public IntPtr ObjectTypeList;
            public uint   ObjectTypeListLength;
            public IntPtr OptionalArguments;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccessReply
        {
            public uint   ResultListLength;
            public IntPtr GrantedAccessMask;
            public IntPtr SaclEvaluationResults;
            public IntPtr Error;
        }

        [DllImport("authz.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AuthzInitializeResourceManager(
            uint flags, IntPtr accessCheck, IntPtr computeDynamicGroups, IntPtr freeDynamicGroups,
            string name, out IntPtr resourceManager);

        [DllImport("authz.dll", SetLastError = true)]
        private static extern bool AuthzInitializeContextFromSid(
            uint flags, byte[] userSid, IntPtr resourceManager, IntPtr expiration, Luid identifier,
            IntPtr dynamicGroupArgs, out IntPtr clientContext);

        [DllImport("authz.dll", SetLastError = true)]
        private static extern bool AuthzAccessCheck(
            uint flags, IntPtr clientContext, ref AccessRequest request, IntPtr auditEvent,
            byte[] securityDescriptor, IntPtr optionalSecurityDescriptorArray,
            uint optionalSecurityDescriptorArrayLength, ref AccessReply reply, IntPtr results);

        [DllImport("authz.dll", SetLastError = true)]
        private static extern bool AuthzFreeContext(IntPtr clientContext);

        [DllImport("authz.dll", SetLastError = true)]
        private static extern bool AuthzFreeResourceManager(IntPtr resourceManager);

        /// <param name="securityDescriptor">Self-relative security descriptor, owner included.</param>
        /// <param name="sid">Binary form of the identity's SID.</param>
        /// <param name="groupsExpanded">
        /// False when the client context had to be built without expanding group membership, which
        /// happens when the machine cannot resolve the identity's groups. The result is then computed
        /// from ACEs naming the identity itself, so a grant arriving through a group would be missed.
        /// Reported rather than hidden: it is the difference between a complete answer and a partial
        /// one.
        /// </param>
        public static uint GrantedAccess(byte[] securityDescriptor, byte[] sid, out bool groupsExpanded)
        {
            groupsExpanded = true;

            IntPtr resourceManager;
            if (!AuthzInitializeResourceManager(
                    NoAudit, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, "RCRAInfo", out resourceManager))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "AuthzInitializeResourceManager failed.");
            }

            IntPtr clientContext = IntPtr.Zero;

            try
            {
                Luid identifier = new Luid();

                if (!AuthzInitializeContextFromSid(
                        0, sid, resourceManager, IntPtr.Zero, identifier, IntPtr.Zero,
                        out clientContext))
                {
                    int firstError = Marshal.GetLastWin32Error();
                    groupsExpanded = false;

                    if (!AuthzInitializeContextFromSid(
                            SkipTokenGroups, sid, resourceManager, IntPtr.Zero, identifier,
                            IntPtr.Zero, out clientContext))
                    {
                        throw new Win32Exception(firstError,
                            "AuthzInitializeContextFromSid failed both with and without group " +
                            "expansion (first error " + firstError + ", second " +
                            Marshal.GetLastWin32Error() + ").");
                    }
                }

                IntPtr grantedMask = Marshal.AllocHGlobal(sizeof(uint));
                IntPtr errorCode   = Marshal.AllocHGlobal(sizeof(uint));

                try
                {
                    Marshal.WriteInt32(grantedMask, 0);
                    Marshal.WriteInt32(errorCode, 0);

                    AccessRequest request = new AccessRequest { DesiredAccess = MaximumAllowed };
                    AccessReply   reply   = new AccessReply
                    {
                        ResultListLength      = 1,
                        GrantedAccessMask     = grantedMask,
                        SaclEvaluationResults = IntPtr.Zero,
                        Error                 = errorCode
                    };

                    if (!AuthzAccessCheck(0, clientContext, ref request, IntPtr.Zero,
                                          securityDescriptor, IntPtr.Zero, 0, ref reply, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "AuthzAccessCheck failed.");
                    }

                    return (uint) Marshal.ReadInt32(grantedMask);
                }
                finally
                {
                    Marshal.FreeHGlobal(grantedMask);
                    Marshal.FreeHGlobal(errorCode);
                }
            }
            finally
            {
                if (clientContext != IntPtr.Zero) { AuthzFreeContext(clientContext); }
                AuthzFreeResourceManager(resourceManager);
            }
        }
    }
}
'@
}

function Resolve-Sid
{
    param ([string] $Identity)

    $sid = (New-Object System.Security.Principal.NTAccount($Identity)).Translate(
               [System.Security.Principal.SecurityIdentifier])

    $bytes = New-Object byte[] $sid.BinaryLength
    $sid.GetBinaryForm($bytes, 0)

    return $bytes
}

function Get-EffectiveAccess
{
    <#
        The access an identity would be granted on the file. Asks Windows; does not interpret the ACL
        itself. Returns the mask, whether it includes read, and whether groups were expanded.
    #>
    param
    (
        [string] $FilePath,
        [string] $Identity
    )

    $descriptor = (Get-Acl -LiteralPath $FilePath).GetSecurityDescriptorBinaryForm()

    $groupsExpanded = $true
    $mask = [RCRAInfo.AccessCheck]::GrantedAccess(
                $descriptor, (Resolve-Sid $Identity), [ref] $groupsExpanded)

    return [pscustomobject] @{
        Identity       = $Identity
        Mask           = $mask
        CanRead        = [bool] ($mask -band [RCRAInfo.AccessCheck]::FileReadData)
        GroupsExpanded = $groupsExpanded
    }
}

function New-Rule
{
    param
    (
        [string] $Identity,
        [string] $Rights,
        [string] $Type
    )

    # None/None: this is a file, so there is nothing to inherit it. Spelled out rather than defaulted,
    # because the same call on a directory with the same arguments would be a different mistake.
    return New-Object System.Security.AccessControl.FileSystemAccessRule(
               $Identity, $Rights, 'None', 'None', $Type)
}

function Set-FileDacl
{
    <#
        Replaces the file's DACL with exactly the rules given, and nothing else.

        A FRESH FileSecurity, deliberately: only the sections such an object has modified get written,
        so this writes the DACL and leaves the owner, group, and SACL alone. Get-Acl piped to Set-Acl
        round-trips the whole descriptor instead and fails with "does not possess the
        'SeSecurityPrivilege' privilege" on an already-protected file when the caller is not an
        administrator -- which is exactly the second run of this script.

        SetAccessRuleProtection($true, $false) turns inheritance off WITHOUT copying the inherited
        ACEs down. Copying them is the friendly default and the wrong one here: it preserves whatever
        the parent folder grants today and hides that this file's access was ever meant to be narrow.
    #>
    param
    (
        [string]   $FilePath,
        [object[]] $Rules
    )

    $security = New-Object System.Security.AccessControl.FileSecurity
    $security.SetAccessRuleProtection($true, $false)

    foreach ($rule in $Rules)
    {
        $security.AddAccessRule($rule)
    }

    [System.IO.File]::SetAccessControl($FilePath, $security)
}

function Write-AccessLine
{
    param ([string] $Label, [object] $Access)

    $note = ''
    if (-not $Access.GroupsExpanded)
    {
        $note = '  (group membership could not be expanded; ACEs naming the identity only)'
    }

    Write-Host ("{0,-8}: {1} -> read access: {2} (granted mask 0x{3:X}){4}" -f
                $Label, $Access.Identity, $Access.CanRead, $Access.Mask, $note)
}

# ---------------------------------------------------------------------------------------------------
# Self-test.
# ---------------------------------------------------------------------------------------------------
if ($SelfTest)
{
    $probeIdentity = 'NT AUTHORITY\NETWORK SERVICE'
    $me            = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $probeFile     = Join-Path ([System.IO.Path]::GetTempPath()) ('rcrainfo-acl-selftest-' +
                                [System.Guid]::NewGuid().ToString('N') + '.tmp')

    'ciphertext-stand-in' | Set-Content -LiteralPath $probeFile -Encoding UTF8

    try
    {
        Write-Host "self-test file    : $probeFile"
        Write-Host "self-test identity: $probeIdentity"
        Write-Host ''

        # The current user is granted full control on the probe file only. Without it, a
        # non-administrator cannot rewrite the ACL for the second direction or delete the file
        # afterwards -- the self-test would leave litter and report a failure that is about this
        # script's own privileges rather than about the mechanism under test.
        $mine = New-Rule -Identity $me -Rights 'FullControl' -Type 'Allow'

        # Direction 1: allowed. If this reports no read access, the verifier answers "no access"
        # regardless of the ACL, which would make every deny below appear to succeed.
        Set-FileDacl -FilePath $probeFile -Rules @(
            $mine
            (New-Rule -Identity $probeIdentity -Rights 'Read' -Type 'Allow')
        )

        $allowed = Get-EffectiveAccess -FilePath $probeFile -Identity $probeIdentity
        Write-AccessLine -Label 'allowed' -Access $allowed

        # Direction 2: denied, with the Allow still present, so the deny has to actually win rather
        # than merely be the only rule.
        Set-FileDacl -FilePath $probeFile -Rules @(
            $mine
            (New-Rule -Identity $probeIdentity -Rights 'Read' -Type 'Allow')
            (New-Rule -Identity $probeIdentity -Rights 'FullControl' -Type 'Deny')
        )

        $denied = Get-EffectiveAccess -FilePath $probeFile -Identity $probeIdentity
        Write-AccessLine -Label 'denied' -Access $denied
        Write-Host ''

        if ($allowed.CanRead -and -not $denied.CanRead)
        {
            Write-Host ('PASS  credential-file ACL: the deny is written, it outranks a competing ' +
                        'Allow, and the access check detects both directions.')
            exit 0
        }

        if (-not $allowed.CanRead)
        {
            Write-Host ('FAIL  credential-file ACL: an explicit Allow Read was not reported as read ' +
                        'access. The access check is not measuring what it claims to, so a passing ' +
                        'deny would mean nothing.')
        }
        else
        {
            Write-Host ('FAIL  credential-file ACL: an explicit Deny FullControl did not remove read ' +
                        'access. Check ACE ordering in the written DACL.')
        }

        exit 1
    }
    finally
    {
        Remove-Item -LiteralPath $probeFile -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------------------------------------------
# Apply.
# ---------------------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
{
    throw ("'$Path' does not exist. The ACL is set on the credential file itself, so the file has " +
           "to exist first: deploy the application, or copy appsettings.json from the template.")
}

$resolved = (Resolve-Path -LiteralPath $Path).Path

# Resolve both identities before changing anything. A typo otherwise produces a half-written ACL, and
# the DENY is the half that goes missing.
foreach ($identity in @($LoaderIdentity, $AppPoolIdentity))
{
    try
    {
        [void] (Resolve-Sid $identity)
    }
    catch
    {
        throw ("Identity '$identity' does not resolve to a SID on this machine. For an IIS " +
               "application pool the name is 'IIS AppPool\<pool name>' and the pool must already " +
               "exist. Nothing has been changed.")
    }
}

$isAdministrator = (New-Object System.Security.Principal.WindowsPrincipal(
                       [System.Security.Principal.WindowsIdentity]::GetCurrent())
                   ).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator)
{
    # Worth saying before the fact rather than discovering it on the re-run. The ACL below grants
    # Administrators and not the caller, so a non-administrator keeps the ability to revise it only
    # by owning the file.
    Write-Warning ('Not running elevated. The ACL about to be written grants Administrators full ' +
                   'control and grants the current account nothing, so revising it later will need ' +
                   'elevation or ownership of the file.')
}

if (-not $PSCmdlet.ShouldProcess($resolved, "replace DACL and deny $AppPoolIdentity"))
{
    return
}

Set-FileDacl -FilePath $resolved -Rules @(
    (New-Rule -Identity 'NT AUTHORITY\SYSTEM'    -Rights 'FullControl' -Type 'Allow')
    (New-Rule -Identity 'BUILTIN\Administrators' -Rights 'FullControl' -Type 'Allow')

    # Read AND write: AR4 has the application rewrite this file in place to replace the plaintext
    # password with ciphertext on first run.
    (New-Rule -Identity $LoaderIdentity -Rights 'Read, Write' -Type 'Allow')

    # Deny everything, not only read. Read is the disclosure risk and the one G18 names, but the web
    # application has no reason to write or delete this file either, and a Deny covering the whole
    # right is one fewer thing to reason about later.
    (New-Rule -Identity $AppPoolIdentity -Rights 'FullControl' -Type 'Deny')
)

$loaderAccess  = Get-EffectiveAccess -FilePath $resolved -Identity $LoaderIdentity
$appPoolAccess = Get-EffectiveAccess -FilePath $resolved -Identity $AppPoolIdentity

Write-Host "file    : $resolved"
Write-AccessLine -Label 'loader'  -Access $loaderAccess
Write-AccessLine -Label 'apppool' -Access $appPoolAccess
Write-Host ''

$problems = @()

if ($appPoolAccess.CanRead)
{
    $problems += ("$AppPoolIdentity can still read the file. DPAPI LocalMachine ciphertext is " +
                  "decryptable by any process on this machine, so this ACL is the only thing " +
                  "separating the web application from the loader's SQL password.")
}

if (-not $loaderAccess.CanRead)
{
    # Equally a failure. An ACL that locks out the application it protects gets loosened by whoever
    # is on call at 2am, and it will not be loosened carefully.
    $problem = ("$LoaderIdentity cannot read the file, so the console application cannot start. " +
                "Fix this rather than leaving it: the remedy applied under pressure is to reset " +
                "the ACL, which removes the deny along with everything else.")

    if (-not $loaderAccess.GroupsExpanded)
    {
        # The one place the group-expansion fallback can produce a WRONG answer, so it is named at
        # the point it would mislead. Access granted through a group is invisible to a SID-only
        # check, and this script grants the loader directly -- so a missing grant here is more
        # likely a bad account name than a real lockout. The DENY result above is not affected: a
        # deny ACE naming the SID is evaluated whether or not groups were expanded.
        $problem += (' Note that group membership could not be expanded on this machine, so a read ' +
                     'right arriving through a group would not be counted. Verify the account name ' +
                     'before widening the ACL.')
    }

    $problems += $problem
}

if ($problems.Count -gt 0)
{
    foreach ($problem in $problems) { Write-Host "  - $problem" }
    throw "Credential-file ACL verification failed ($($problems.Count) problem(s))."
}

Write-Host 'PASS  credential-file ACL: the loader can read it, the application pool identity cannot.'
Write-Host ''
Write-Host ('Still outstanding, and not something this script can do: an end-to-end read attempt ' +
            'AS the application pool identity. A virtual account has no password to log on with. ' +
            'Confirm it in Workstream C from the running web application.')
