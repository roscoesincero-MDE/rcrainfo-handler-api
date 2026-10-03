<#
.SYNOPSIS
    Hands the local development passwords to the password manager, then removes the file they were
    parked in.

.DESCRIPTION
    %LOCALAPPDATA%\RCRAInfo\dev-credentials.json is a staging post, not a store. It exists because
    040_Logins.sql generates a password once and never shows it again, and because
    build\check_permission_posture.py needs both passwords to attempt 244 assertions AS the logins
    themselves. The file says so in its own 'note' field: copy into the password manager, then it may
    be deleted.

    Nothing about that file is a security control. It is plaintext JSON in a roaming profile,
    readable by the developer's own token and by anything running as the developer, which on this
    workstation includes every build script and every editor extension. The password manager is the
    control; this script is the walk between the two.

    What it does, in order:

        1. verifies each password still authenticates, so what gets filed is the working value and
           not a stale one left behind by a rotation
        2. prints the password-manager entry to create -- every field except the secret
        3. puts the secret on the clipboard, waits for confirmation that it is saved, and clears the
           clipboard again
        4. asks you to retrieve the entry from the password manager and PASTE IT BACK, and compares
           it to the file
        5. with -RemoveFile, overwrites and deletes dev-credentials.json -- but only if step 4
           matched for every login

    Step 1 matters more than it looks. A password that does not authenticate is not a password to
    archive: it means the login was rotated without the file being updated, and the answer is
    Reset-ApplicationPassword.ps1, not a password-manager entry that will mislead someone in six
    months.

    Step 4 is the one that earns the deletion. Steps 1 to 3 all test values this script already has;
    only the paste-back tests what the password MANAGER holds, which is the thing being relied on. It
    catches a truncated paste, a trailing space or newline the manager kept, the other login's entry,
    and an entry left over from an earlier rotation -- and it catches them by comparing strings rather
    than by asking SQL Server, which matters because both logins have CHECK_POLICY = ON and therefore
    inherit the Windows account lockout policy. On this workstation that is 3 attempts and a 15-minute
    lockout, so 'try it and see' has three lives; string comparison has unlimited ones.

    The paste-back can be skipped by pressing Enter alone, and then -RemoveFile refuses. That is
    deliberate: this file is the only copy of these two passwords outside the password manager, and the
    reason it exists at all is that the previous pair was deleted while it was the only copy.

    After the file is gone, the guardrails still run -- they just read the environment instead:

        $env:LoaderPassword  = '<from the password manager>'
        $env:MonitorPassword = '<from the password manager>'
        python build\guardrails.py --with-database

    which is the same path UAT and Production take. --dev-credentials is the workstation shortcut
    and stops being available, which is the point.

.PARAMETER CredentialFile
    Path to the staging file. Defaults to %LOCALAPPDATA%\RCRAInfo\dev-credentials.json.

.PARAMETER Login
    Handle one login only, and skip the wait-for-confirmation prompt. Use this when running the
    script somewhere without an interactive console: the secret lands on the clipboard and the script
    exits, so the clipboard is NOT cleared for you and -RemoveFile is refused.

.PARAMETER ServerInstance
    The instance to verify against. Defaults to the local default instance.

.PARAMETER Database
    The database to connect to when verifying. Defaults to the 'database' field in the file.

.PARAMETER SkipVerify
    Skip step 1. Only reasonable when the instance is not running; a filed password that was never
    tested is a filed guess.

.PARAMETER RemoveFile
    Overwrite and delete the file once every login has been confirmed saved. High-impact, so it
    honours -WhatIf and -Confirm.

.EXAMPLE
    .\Move-DevCredentialsToPasswordManager.ps1

    Walks both logins interactively and leaves the file in place.

.EXAMPLE
    .\Move-DevCredentialsToPasswordManager.ps1 -RemoveFile

    The same, then deletes the file after both are confirmed.

.EXAMPLE
    .\Move-DevCredentialsToPasswordManager.ps1 -Login RCRAInfoLoader

    One login, no prompt: metadata to the console, secret to the clipboard, exit.

.NOTES
    The overwrite before deletion is a courtesy, not a secure erase. On an SSD the original bytes may
    survive in a block the filesystem no longer points at. Treat these two passwords as having been
    exposed to this machine for as long as the file existed, and rotate them with
    Reset-ApplicationPassword.ps1 if that machine is ever in doubt.

    This script never writes a secret to the console, to a log, or to its own transcript -- the
    clipboard is the only channel. That is deliberate: PowerShell transcription may be on, and
    Write-Host output is the one thing guaranteed to be captured by whatever is watching the session.

    Windows PowerShell 5.1 is the floor.
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param
(
    [string] $CredentialFile = (Join-Path $env:LOCALAPPDATA 'RCRAInfo\dev-credentials.json'),

    [ValidateSet('RCRAInfoLoader', 'RCRAInfoMonitor')]
    [string] $Login,

    [string] $ServerInstance = '.',

    [string] $Database,

    [switch] $SkipVerify,

    [switch] $RemoveFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# What each login is for. The password manager entry is read by a human months from now, so the
# entry has to say why the credential exists, not just what it is.
$Purpose = @{
    'RCRAInfoLoader'  = 'Console application. Executes the RCRAInfo retrieval procedures under Task Scheduler. EXECUTE on its own procedures only -- no DDL, no direct table access.'
    'RCRAInfoMonitor' = 'Monitoring web application. Reads load status and errors. EXECUTE on the reporting procedures only -- no DDL, no direct table access, no write path.'
}

if (-not (Test-Path -LiteralPath $CredentialFile))
{
    throw ("'$CredentialFile' does not exist. If it was already moved, there is nothing to do: set " +
           "`$env:LoaderPassword and `$env:MonitorPassword from the password manager and the " +
           "guardrails will run.")
}

$data = Get-Content -LiteralPath $CredentialFile -Raw -Encoding UTF8 | ConvertFrom-Json

if (-not $data.logins)
{
    throw "'$CredentialFile' has no 'logins' object. It is not the file this script expects."
}

if (-not $Database)
{
    # Written as an if/else statement rather than as an assignment from an if expression, because
    # build\check_cross_database.py matches /(Initial Catalog|Database)\s*=\s*(\w+)/ to catch a
    # connection string naming a catalog other than RCRAInfo, and the expression form makes the
    # keyword that follows the assignment read as a catalog name. That check does not strip comments
    # either, so this note deliberately avoids writing the offending form out. It is right to be
    # blunt on a rule the whole two-database design depends on; this is the cheaper side to bend.
    if ($data.database)
    {
        $Database = $data.database
    }
    else
    {
        $Database = 'RCRAInfo'
    }
}

$names = @($data.logins.PSObject.Properties.Name)

if ($Login)
{
    if ($names -notcontains $Login)
    {
        throw "'$CredentialFile' does not carry a password for [$Login]. It has: $($names -join ', ')."
    }
    $names = @($Login)

    if ($RemoveFile)
    {
        throw ("-RemoveFile with -Login would delete the passwords for the logins this run did not " +
               "touch. Run without -Login to move all of them, then delete.")
    }
}
else
{
    # Refuse a redirected console rather than hang in it. Read-Host -AsSecureString reads the console
    # directly and ignores standard input, so under a pipe or a redirect it blocks forever with no
    # prompt and no output -- measured, not guessed. That is the worst failure mode a script can have,
    # and it is one line to rule out.
    if ([Console]::IsInputRedirected)
    {
        throw ("Standard input is redirected, and the paste-back prompt uses " +
               "Read-Host -AsSecureString, which reads the console and would block forever here. Run " +
               "this in a PowerShell window, or use -Login <name> for the non-interactive path that " +
               "copies one password to the clipboard and exits.")
    }
}

# ------------------------------------------------------------------------------------------------
# 1. Verify. A stale password is a rotation problem, not a filing problem.
# ------------------------------------------------------------------------------------------------

if (-not $SkipVerify)
{
    $found = Get-Command -Name 'sqlcmd.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found)
    {
        throw 'SQLCMD.EXE is not on PATH, so the passwords cannot be verified. Pass -SkipVerify to file them untested.'
    }
    $sqlCmd = $found.Source

    foreach ($name in $names)
    {
        # SQLCMDPASSWORD is read from the child process environment. -P would put the password in the
        # process list, where any other user on the machine can read it.
        $env:SQLCMDPASSWORD = $data.logins.$name

        try
        {
            $output = & $sqlCmd -S $ServerInstance -U $name -C -b -I -d $Database -h -1 -W `
                -Q 'SET NOCOUNT ON; SELECT 1;' 2>&1
            $code = $LASTEXITCODE
        }
        finally
        {
            Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
        }

        if ($code -ne 0)
        {
            throw ("[$name] cannot authenticate to $ServerInstance / $Database with the password in " +
                   "'$CredentialFile' (sqlcmd exited $code). Do NOT file this value. Either the login " +
                   "was rotated without updating the file, or the instance is unreachable. Rotate " +
                   "deliberately with Reset-ApplicationPassword.ps1.`n$output")
        }

        Write-Host "verified: [$name] authenticates to $ServerInstance / $Database."
    }

    Write-Host ''
}

# ------------------------------------------------------------------------------------------------
# 2 and 3. The entry, and the clipboard.
# ------------------------------------------------------------------------------------------------

$machine   = $env:COMPUTERNAME
$generated = if ($data.generated) { $data.generated } else { '(not recorded)' }
$confirmed = @()   # you said it is saved
$verified  = @()   # the password manager proved it, by paste-back

foreach ($name in $names)
{
    $secret = $data.logins.$name

    Write-Host '--------------------------------------------------------------------------------'
    Write-Host "Password manager entry $([array]::IndexOf($names, $name) + 1) of $($names.Count)"
    Write-Host '--------------------------------------------------------------------------------'
    Write-Host "  Title     : RCRAInfo dev -- SQL login $name"
    Write-Host "  Username  : $name"
    Write-Host "  Password  : (on your clipboard -- paste it, do not type it)"
    Write-Host "  Server    : $ServerInstance   (local development instance on $machine)"
    Write-Host "  Database  : $Database"
    Write-Host "  Generated : $generated"
    Write-Host "  Length    : $($secret.Length) characters"
    Write-Host "  Notes     : $($Purpose[$name])"
    Write-Host "              LOCAL DEVELOPMENT ONLY. UAT and Production have their own passwords,"
    Write-Host "              generated on those instances and never shared with this one."
    Write-Host "              Rotate with src\RCRAInfo.Database\Deployment\Reset-ApplicationPassword.ps1."
    Write-Host ''

    Set-Clipboard -Value $secret

    if ($Login)
    {
        Write-Host "The password for [$name] is on the clipboard. Paste it, then clear the clipboard yourself."
        return
    }

    $answer = Read-Host "Saved in the password manager? Type 'saved' to continue, anything else to stop"

    # Clear the clipboard whichever way that went. Leaving a service-account password on the
    # clipboard is how it ends up pasted into the next window that takes focus.
    Set-Clipboard -Value ' '

    if ($answer -ne 'saved')
    {
        Write-Host ''
        Write-Host "Stopped at [$name]. '$CredentialFile' is untouched, so nothing is lost -- run again."
        return
    }

    # 'saved' is a claim, and the file is about to be deleted on the strength of it. So make the
    # password manager prove it: retrieve the entry through the manager's own copy path and paste it
    # back. This is the only check available that tests what the MANAGER holds rather than what this
    # script already knows, and it is deliberately done offline, before anything is sent to SQL Server.
    #
    # Offline matters here. sys.sql_logins reports is_policy_checked = 1 for both logins, so they
    # inherit the Windows account lockout policy -- on this workstation, 3 attempts and 15 minutes.
    # 'try it and see' is a strategy with three lives. Comparing strings has unlimited ones.
    $matched = $false

    while (-not $matched)
    {
        $pastedSecure = Read-Host "Paste it back from the password manager to confirm (Enter alone to skip)" -AsSecureString
        $pointer      = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pastedSecure)

        try
        {
            $pasted = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        }
        finally
        {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }

        if (-not $pasted)
        {
            Write-Host "  skipped: [$name] is being filed on your word alone, not on a match."
            break
        }

        # Ordinal, not the default culture-aware comparison: two strings that a culture calls equal are
        # still two different passwords to SQL Server.
        if ([string]::Equals($pasted, $secret, [StringComparison]::Ordinal))
        {
            $matched = $true
            Write-Host "  match: the password manager holds the same $($secret.Length) characters."
        }
        elseif ($pasted.Length -ne $secret.Length)
        {
            # Say how it differs, never what it is. A length gap of one, with the manager longer, is
            # almost always a trailing space or newline the manager kept from an earlier paste.
            Write-Host ("  MISMATCH: the manager gave $($pasted.Length) characters, the file has " +
                        "$($secret.Length). A truncated paste, a trailing space, or the wrong entry.")
        }
        else
        {
            Write-Host ("  MISMATCH: same length, different characters. Most likely the other login's " +
                        "entry, or an entry from an earlier rotation.")
        }
    }

    $confirmed += $name

    if ($matched)
    {
        $verified += $name
    }

    Write-Host ''
}

Write-Host "Clipboard cleared. Confirmed saved: $($confirmed -join ', ')."

if ($verified.Count -lt $confirmed.Count)
{
    Write-Host ("Paste-back confirmed for: " +
                $(if ($verified) { $verified -join ', ' } else { 'none' }) +
                ". The rest are unverified.")
}

Write-Host ''

# ------------------------------------------------------------------------------------------------
# 4. Remove the staging file.
# ------------------------------------------------------------------------------------------------

if (-not $RemoveFile)
{
    Write-Host "'$CredentialFile' is left in place. Re-run with -RemoveFile to delete it once you are"
    Write-Host 'satisfied both entries are correct.'
    return
}

$total = @($data.logins.PSObject.Properties.Name).Count

if ($confirmed.Count -ne $total)
{
    throw 'Not every login was confirmed saved, so the file is kept. Run again.'
}

# The paste-back is the gate, not the 'saved' answer. This file is the only copy of these two
# passwords outside the password manager, and the reason it exists at all is that the previous pair
# was deleted while it was the only copy. Refusing here costs a re-run; agreeing costs a rotation.
if ($verified.Count -ne $total)
{
    throw ("The file is kept: $($verified.Count) of $total password(s) were confirmed by paste-back " +
           "from the password manager. Skipping that check means nothing has tested what the manager " +
           "actually holds -- and this file is the only other copy. Run again and paste each one back.")
}

if ($PSCmdlet.ShouldProcess($CredentialFile, 'Overwrite and delete'))
{
    $length = (Get-Item -LiteralPath $CredentialFile).Length
    [System.IO.File]::WriteAllBytes($CredentialFile, (New-Object byte[] $length))
    Remove-Item -LiteralPath $CredentialFile -Force

    Write-Host "Deleted '$CredentialFile'."
    Write-Host ''
    Write-Host 'From here, the guardrails read the environment, the same way UAT and Production will:'
    Write-Host ''
    Write-Host "    `$env:LoaderPassword  = '<from the password manager>'"
    Write-Host "    `$env:MonitorPassword = '<from the password manager>'"
    Write-Host '    python build\guardrails.py --with-database'
    Write-Host ''
    Write-Host '--dev-credentials no longer has a file to read. That is the intended end state.'
}
