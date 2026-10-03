<#
.SYNOPSIS
    Runs the RCRAInfo database scripts, in order, against one instance.

.DESCRIPTION
    A developer runs the DDL by hand (G3), and every script is written to converge: a second run
    changes nothing and reports nothing. This script exists so that "in order, against the right
    database, with the right flags" is not something anyone has to remember at 4pm on a Friday.

    Three of the flags matter more than they look:

      -b   sqlcmd returns a non-zero exit code when a batch fails. WITHOUT IT, sqlcmd prints the
           error and exits 0, so a failed deployment looks exactly like a successful one. This is
           the single most important flag here.
      -I   QUOTED_IDENTIFIER ON, which filtered indexes require. Every unique constraint in this
           database is a filtered index, because soft delete (AR7) means the row for a retired key
           never leaves the table.
      -x   scripting-variable substitution OFF. Applied to every script that does not need it, so a
           literal $(something) in a future script cannot be silently replaced by an environment
           variable that happens to share the name.

.PARAMETER ServerInstance
    The target instance. Defaults to the local default instance.

.PARAMETER ScriptDirectory
    Where the numbered scripts live. Defaults to ..\Scripts relative to this file.

.PARAMETER SqlCmdPath
    Full path to SQLCMD.EXE. Discovered on PATH when omitted.

.PARAMETER SkipConnectionTest
    Skip the pre-flight connection and permission probe. Only useful when diagnosing that probe.

.EXAMPLE
    # Set the passwords for this session only, from the password manager entry. Read-Host without
    # -AsSecureString would put them in the console; this keeps them out of it and out of history.
    $env:LoaderPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host 'Loader password' -AsSecureString)))
    $env:MonitorPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR((Read-Host 'Monitor password' -AsSecureString)))

    .\Deploy-Database.ps1 -ServerInstance '.'

.EXAMPLE
    .\Deploy-Database.ps1 -ServerInstance 'MDESQLUAT01' -WhatIf

.NOTES
    Passwords are passed through the ENVIRONMENT, never on the command line. sqlcmd resolves an
    unset $(Var) from the environment on its own, so nothing secret appears in the process listing,
    where any user on the machine can read it, or in this repository. See README.md.

    Windows PowerShell 5.1 is the floor: that is what is on the workstation, and a deployment script
    that only runs on PowerShell 7 is a deployment script that does not run in UAT.
#>

#Requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true)]
param
(
    [string] $ServerInstance = '.',

    # Resolved in the body, not here: a param() default is evaluated before $PSScriptRoot is
    # populated when the script is invoked with -File, so a default built from it arrives empty.
    [string] $ScriptDirectory,

    [string] $SqlCmdPath,

    [switch] $SkipConnectionTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
# The deployment order, and the two things about each script that cannot be inferred from its name.
#
# TargetDatabase is a property of the script, not a choice: 010 and 040 create server-level objects
# and must run against master, while the rest assert they are in RCRAInfo and stop if they are not.
#
# NeedsVariables is true only for 040, which is the only script that references a $(Var).
# ---------------------------------------------------------------------------------------------------
$deploymentPlan = @(
    [pscustomobject] @{ File = '010_Database.sql';                     TargetDatabase = 'master';   NeedsVariables = $false; What = 'database, compatibility level 160, options' }
    [pscustomobject] @{ File = '020_Schemas.sql';                      TargetDatabase = 'RCRAInfo'; NeedsVariables = $false; What = 'the five schemas' }
    [pscustomobject] @{ File = '030_util.uspSetObjectDescription.sql'; TargetDatabase = 'RCRAInfo'; NeedsVariables = $false; What = 'the description helper' }
    [pscustomobject] @{ File = '040_Logins.sql';                       TargetDatabase = 'master';   NeedsVariables = $true;  What = 'the two application logins' }
    [pscustomobject] @{ File = '050_Roles_and_Users.sql';              TargetDatabase = 'RCRAInfo'; NeedsVariables = $false; What = 'roles, users, and the DENY posture' }
)

function Resolve-SqlCmd
{
    param ([string] $Explicit)

    if ($Explicit)
    {
        if (-not (Test-Path -LiteralPath $Explicit))
        {
            throw "SqlCmdPath '$Explicit' does not exist."
        }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $found = Get-Command -Name 'sqlcmd.exe' -ErrorAction SilentlyContinue |
             Select-Object -First 1

    if (-not $found)
    {
        throw ('SQLCMD.EXE is not on PATH. Install the Microsoft command line utilities for SQL ' +
               'Server, or pass -SqlCmdPath.')
    }

    return $found.Source
}

function Invoke-SqlCmdFile
{
    <#
        One script, one sqlcmd invocation. Returns nothing; throws on failure, because a deployment
        that carries on after a failed script produces a half-built database whose later errors all
        point somewhere other than the cause.
    #>
    param
    (
        [string] $SqlCmd,
        [string] $Server,
        [string] $Database,
        [string] $Path,
        [bool]   $AllowVariables
    )

    $arguments = @('-S', $Server, '-E', '-C', '-b', '-I', '-d', $Database, '-i', $Path)

    if (-not $AllowVariables)
    {
        $arguments += '-x'
    }

    & $SqlCmd @arguments
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0)
    {
        throw "sqlcmd exited $exitCode running $(Split-Path -Leaf $Path) against [$Database]."
    }
}

function Invoke-SqlCmdQuery
{
    param
    (
        [string] $SqlCmd,
        [string] $Server,
        [string] $Database,
        [string] $Query
    )

    $output = & $SqlCmd -S $Server -E -C -b -I -x -d $Database -h -1 -W -Q $Query 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "sqlcmd exited $LASTEXITCODE on the probe query against [$Database]:`n$output"
    }

    return ($output | Where-Object { $_ -and $_ -notmatch '^\(\d+ rows? affected\)$' })
}

# ---------------------------------------------------------------------------------------------------
# Pre-flight. Everything that can be checked before touching the server is checked before touching
# the server, so a deployment either does not start or runs to completion.
# ---------------------------------------------------------------------------------------------------
$sqlCmd = Resolve-SqlCmd -Explicit $SqlCmdPath

if (-not $ScriptDirectory)
{
    $ScriptDirectory = Join-Path $PSScriptRoot '..\Scripts'
}

$scriptRoot = (Resolve-Path -LiteralPath $ScriptDirectory).Path
Write-Host "sqlcmd : $sqlCmd"
Write-Host "scripts: $scriptRoot"
Write-Host "server : $ServerInstance"
Write-Host ''

# ---------------------------------------------------------------------------------------------------
# The schema scripts, 100 and up, are DISCOVERED rather than listed.
#
# The five foundation scripts above are named explicitly because each one carries a fact that its
# filename does not: which database it runs against, and whether it needs $(Var) substitution. From
# 100 onward there is no such fact -- every script runs against RCRAInfo with variables off -- and
# there are 46 of them, generated from the pinned EPA spec by build/generate_schema.py.
#
# Listing 46 generated names here would be a second copy of the generator's output, kept in step by
# hand. The failure mode is not a broken deployment but a silent one: a new script that nobody
# deploys, discovered in UAT when a table is missing. Sorting by name is the deployment order,
# because the numbering is assigned so that a table precedes anything that references it -- the two
# views after dbo.HandlerSource, each grandchild after its parent.
#
# Anything that must run at a particular point relative to the foundation scripts belongs BELOW 100
# and in the explicit list above.
# ---------------------------------------------------------------------------------------------------
$schemaScripts = @(
    Get-ChildItem -LiteralPath $scriptRoot -Filter '*.sql' -File |
        Where-Object { $_.Name -match '^[1-9]\d\d_' } |
        Sort-Object -Property Name |
        ForEach-Object {
            [pscustomobject] @{
                File           = $_.Name
                TargetDatabase = 'RCRAInfo'
                NeedsVariables = $false
                What           = ($_.BaseName -replace '^\d+_', '')
            }
        }
)

if ($schemaScripts.Count -eq 0)
{
    # Not a warning. A deployment that silently applies only the foundation would leave a database
    # with five schemas, no tables, and an exit code of 0.
    throw ("No schema scripts (100 and up) were found in '$scriptRoot'. Run " +
           '`python build/generate_schema.py` to write them.')
}

$deploymentPlan += $schemaScripts

$missing = $deploymentPlan |
           Where-Object { -not (Test-Path -LiteralPath (Join-Path $scriptRoot $_.File)) } |
           ForEach-Object { $_.File }

if ($missing)
{
    throw "Missing script(s) in ${scriptRoot}: $($missing -join ', ')"
}

# The scripts are ASCII by design, so no sqlcmd code-page flag is needed. Verify rather than
# assume: a stray non-ASCII character read under the console code page becomes a different
# character in a PRINT, an object description, or -- worst case -- a password literal.
foreach ($step in $deploymentPlan)
{
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $scriptRoot $step.File))
    $high  = $bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1

    if ($null -ne $high)
    {
        throw ("$($step.File) contains a non-ASCII byte. Either keep the scripts ASCII or add " +
               "-f 65001 to the sqlcmd arguments and re-test; sqlcmd otherwise reads the file " +
               "under the console code page.")
    }
}

if (-not $SkipConnectionTest)
{
    $identity = Invoke-SqlCmdQuery -SqlCmd $sqlCmd -Server $ServerInstance -Database 'master' `
                                   -Query 'SELECT SUSER_SNAME() + N''|'' + CAST(IS_SRVROLEMEMBER(N''sysadmin'') AS NVARCHAR(1));'

    $parts = ("$identity".Trim() -split '\|')
    Write-Host "connected as: $($parts[0])"

    if ($parts.Count -lt 2 -or $parts[1] -ne '1')
    {
        # CREATE DATABASE and CREATE LOGIN are server-level. Failing here names the real problem,
        # instead of letting 010 fail with a permission error that reads like a bad connection.
        Write-Warning ('This login is not sysadmin. 010_Database.sql needs CREATE DATABASE and ' +
                       '040_Logins.sql needs CREATE LOGIN; the deployment will fail without them.')
    }
}

# 040 references $(LoaderPassword) and $(MonitorPassword). sqlcmd substitutes those while READING
# the file, before the server ever sees the IF that guards the CREATE LOGIN, so the variables must
# resolve even on a re-run where no login will be created. Rather than have the operator supply a
# password that is guaranteed to be ignored -- which is how a placeholder eventually gets applied to
# a login that was quietly dropped -- ask the server which logins already exist.
$loginsPresent = @()

if (-not $SkipConnectionTest)
{
    $loginsPresent = Invoke-SqlCmdQuery -SqlCmd $sqlCmd -Server $ServerInstance -Database 'master' `
                        -Query ("SELECT name FROM sys.server_principals " +
                                "WHERE name IN (N'RCRAInfoLoader', N'RCRAInfoMonitor') AND type = 'S';") |
                     ForEach-Object { "$_".Trim() } |
                     Where-Object { $_ }
}

$needLoader  = $loginsPresent -notcontains 'RCRAInfoLoader'
$needMonitor = $loginsPresent -notcontains 'RCRAInfoMonitor'

$absent = @()
if ($needLoader  -and -not $env:LoaderPassword)  { $absent += 'LoaderPassword' }
if ($needMonitor -and -not $env:MonitorPassword) { $absent += 'MonitorPassword' }

if ($absent)
{
    throw (("Environment variable(s) not set: $($absent -join ', '). " +
            "The login(s) they create do not exist yet. Set them for this session only, from the " +
            "password manager entry, and never on the command line:`n`n") +
           "    `$env:LoaderPassword  = '<from the password manager>'`n" +
           "    `$env:MonitorPassword = '<from the password manager>'`n`n" +
           "To generate a new one, record it in the password manager FIRST, then set it here. See " +
           "README.md.")
}

if (-not $needLoader -and -not $needMonitor)
{
    Write-Host ('logins  : RCRAInfoLoader and RCRAInfoMonitor already exist; no password needed. ' +
                '040 will not change either one.')

    # A placeholder, only so sqlcmd can resolve the token in a branch the server will not take. It
    # is not a secret and it is never applied: 040 guards CREATE LOGIN on sys.server_principals.
    if (-not $env:LoaderPassword)  { $env:LoaderPassword  = 'not-used-on-a-re-run' }
    if (-not $env:MonitorPassword) { $env:MonitorPassword = 'not-used-on-a-re-run' }
}

Write-Host ''

# ---------------------------------------------------------------------------------------------------
# Run.
# ---------------------------------------------------------------------------------------------------
$step = 0

foreach ($item in $deploymentPlan)
{
    $step++
    $path  = Join-Path $scriptRoot $item.File
    $label = "[$step/$($deploymentPlan.Count)] $($item.File) -> [$($item.TargetDatabase)]  $($item.What)"

    if (-not $PSCmdlet.ShouldProcess("$ServerInstance / $($item.TargetDatabase)", $item.File))
    {
        Write-Host "WHATIF $label"
        continue
    }

    Write-Host ''
    Write-Host $label
    Write-Host ('-' * 100)

    Invoke-SqlCmdFile -SqlCmd $sqlCmd -Server $ServerInstance -Database $item.TargetDatabase `
                      -Path $path -AllowVariables ([bool] $item.NeedsVariables)
}

Write-Host ''
Write-Host "Deployment complete against $ServerInstance."
Write-Host ('Verify with:  python build\check_run_twice.py --server ' + $ServerInstance)
Write-Host ('              python build\check_permission_posture.py --server ' + $ServerInstance)
