<#
.SYNOPSIS
    Rotates the password of one application login.

.DESCRIPTION
    Deliberately a separate script from Deploy-Database.ps1, and deliberately one login at a time.

    040_Logins.sql never re-applies a password on a re-run, because ALTER LOGIN ... WITH PASSWORD on
    an existing login would rotate a working credential without being asked, breaking whatever
    DPAPI-encrypted copy the applications already hold on that machine. Rotation therefore has to be
    a separate, deliberate act, which is this script.

    Rotating a password is not finished when ALTER LOGIN succeeds. The application holds its own
    encrypted copy (Workstream C), so the full sequence is:

        1. record the new password in the password manager
        2. run this script
        3. re-run the credential-seeding step on every machine that runs either application
        4. confirm the next scheduled run succeeded

    Steps 3 and 4 are outside this script because they are outside this database. Skipping them
    leaves an application that cannot log in, and it will not find out until its next scheduled run.

.PARAMETER Login
    RCRAInfoLoader or RCRAInfoMonitor. No other login is a subject for this script: the developer's
    own account is a Windows login and has no password here, and rotating anything else from a
    project script is how an unrelated service goes down.

.PARAMETER ServerInstance
    The target instance. Defaults to the local default instance.

.PARAMETER PasswordEnvironmentVariable
    Name of the environment variable holding the new password. The password is passed to sqlcmd as a
    scripting variable resolved FROM THE ENVIRONMENT, so it never appears on a command line, where
    any other user on the machine can read it out of the process list.

.PARAMETER SqlCmdPath
    Full path to SQLCMD.EXE. Discovered on PATH when omitted.

.EXAMPLE
    $env:NewPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host 'New password' -AsSecureString)))

    .\Reset-ApplicationPassword.ps1 -Login RCRAInfoLoader

.NOTES
    The login must already exist. This script does not create one: a rotation that silently creates
    a missing login would paper over the more interesting problem of why it is missing.

    Windows PowerShell 5.1 is the floor.
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param
(
    [Parameter(Mandatory = $true)]
    [ValidateSet('RCRAInfoLoader', 'RCRAInfoMonitor')]
    [string] $Login,

    [string] $ServerInstance = '.',

    [string] $PasswordEnvironmentVariable = 'NewPassword',

    [string] $SqlCmdPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($SqlCmdPath)
{
    if (-not (Test-Path -LiteralPath $SqlCmdPath))
    {
        throw "SqlCmdPath '$SqlCmdPath' does not exist."
    }
    $sqlCmd = (Resolve-Path -LiteralPath $SqlCmdPath).Path
}
else
{
    $found = Get-Command -Name 'sqlcmd.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found)
    {
        throw 'SQLCMD.EXE is not on PATH. Pass -SqlCmdPath.'
    }
    $sqlCmd = $found.Source
}

$newPassword = [Environment]::GetEnvironmentVariable($PasswordEnvironmentVariable)

if (-not $newPassword)
{
    throw ("Environment variable '$PasswordEnvironmentVariable' is not set. Record the new " +
           "password in the password manager FIRST, then set the variable for this session only. " +
           "It is read from the environment so that it does not appear on a command line.")
}

# A short or trivial password would be refused by CHECK_POLICY anyway, but the refusal arrives as a
# generic ALTER LOGIN failure. Say the useful thing here instead.
if ($newPassword.Length -lt 16)
{
    throw ("The new password is $($newPassword.Length) characters. These are unattended service " +
           "accounts that no one types, so there is no reason for them to be short; use at least " +
           "16, and 32 or more by preference.")
}

# Confirm the login exists before changing anything, so a typo produces a clear message rather than
# a successful-looking no-op.
$exists = & $sqlCmd -S $ServerInstance -E -C -b -I -x -d master -h -1 -W `
    -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.server_principals WHERE name = N'$Login' AND type = 'S';"

if ($LASTEXITCODE -ne 0)
{
    throw "sqlcmd exited $LASTEXITCODE checking for login [$Login]:`n$exists"
}

# sqlcmd returns an ARRAY of lines, including blank ones. Interpolating it into a string joins the
# elements with spaces, so "1" becomes "1   " and the comparison below fails on a login that plainly
# exists. Filter the array, then take the first line that has content.
$firstLine = @($exists) | Where-Object { "$_".Trim() } | Select-Object -First 1

if ("$firstLine".Trim() -ne '1')
{
    throw ("Login [$Login] does not exist on $ServerInstance. Run 040_Logins.sql to create it; " +
           "this script only rotates an existing password.")
}

if (-not $PSCmdlet.ShouldProcess("$ServerInstance / [$Login]", 'ALTER LOGIN WITH PASSWORD'))
{
    return
}

# $(NewPasswordValue) is resolved by sqlcmd from the environment of the child process. The value is
# never an argument, so it is never in the process list.
$env:NewPasswordValue = $newPassword

try
{
    $output = & $sqlCmd -S $ServerInstance -E -C -b -I -d master `
        -Q "ALTER LOGIN [$Login] WITH PASSWORD = '`$(NewPasswordValue)';"

    if ($LASTEXITCODE -ne 0)
    {
        throw "sqlcmd exited $LASTEXITCODE rotating [$Login]:`n$output"
    }
}
finally
{
    Remove-Item Env:\NewPasswordValue -ErrorAction SilentlyContinue
}

Write-Host "Rotated the password for [$Login] on $ServerInstance."
Write-Host ''
Write-Host 'Not finished. Still to do:'
Write-Host '  1. Confirm the new password is in the password manager.'
Write-Host '  2. Re-run the credential-seeding step on every machine running either application.'
Write-Host '  3. Confirm the next scheduled run succeeds. Until then, that application cannot log in.'
