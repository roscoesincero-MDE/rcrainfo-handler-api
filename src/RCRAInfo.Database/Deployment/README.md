# Deploying the RCRAInfo database

A developer runs the DDL by hand (G3). Every script is written to converge — a second run changes
nothing and reports nothing — and that claim is measured, not assumed, by
`build/check_run_twice.py`.

Nothing in `Scripts/` or in this folder contains a password.

---

## Run order

```powershell
cd src\RCRAInfo.Database\Deployment
.\Deploy-Database.ps1 -ServerInstance '.'
```

| # | Script | Database | What it does |
|---|---|---|---|
| 010 | `010_Database.sql` | `master` | the database, compatibility level 160, database options |
| 020 | `020_Schemas.sql` | `RCRAInfo` | `dbo`, `auth`, `logs`, `config`, `util` |
| 030 | `030_util.uspSetObjectDescription.sql` | `RCRAInfo` | the description helper, before any table needs it |
| 040 | `040_Logins.sql` | `master` | `RCRAInfoLoader`, `RCRAInfoMonitor` |
| 050 | `050_Roles_and_Users.sql` | `RCRAInfo` | roles, users, and the DENY posture |

`Deploy-Database.ps1` knows which database each script targets, and passes `-b` so that a failed
batch produces a non-zero exit code. Without `-b`, sqlcmd prints the error and exits 0 — a failed
deployment then looks exactly like a successful one. Running the scripts by hand is fine, but do it
with `-b -I`, and against the database in the table above.

`-WhatIf` prints the plan without touching the server.

---

## The first run needs two passwords

040 creates two SQL logins. Their passwords are supplied through the **environment**, never on the
command line — sqlcmd resolves an unset `$(Var)` from its own environment, so nothing secret appears
in the process list, where any other user on the machine can read it.

```powershell
# Generate them, and record them in the password manager BEFORE running anything.
[System.Web.Security.Membership]::GeneratePassword(32, 8)

$env:LoaderPassword  = '<from the password manager>'
$env:MonitorPassword = '<from the password manager>'

.\Deploy-Database.ps1 -ServerInstance '.'
```

The password manager entry is created **at generation time**, not afterwards. There is no way to
read one of these back out of SQL Server.

**On this workstation that rule was broken once and then repaired.** The first pair of development
passwords went to a temporary directory that was cleaned up, which left two logins nobody could
authenticate as; the replacements were applied with `Reset-ApplicationPassword.ps1` and parked in
`%LOCALAPPDATA%\RCRAInfo\dev-credentials.json` so that `check_permission_posture.py` had something to
authenticate with. That file is a staging post and not a store — plaintext JSON in a roaming profile,
readable by anything running as the developer. Move it into the password manager with:

```powershell
.\Move-DevCredentialsToPasswordManager.ps1 -RemoveFile
```

It verifies each password authenticates before filing it (a value that does not authenticate is a
rotation problem, not a filing problem), prints the entry to create, and passes the secret through the
**clipboard only** — never the console, which PowerShell transcription would capture.

**What licenses the deletion is the paste-back.** Everything else in that script tests a value it
already has; only retrieving the entry from the password manager and pasting it back tests what the
*manager* holds, which is the thing about to become the only copy. Press Enter to skip it and
`-RemoveFile` refuses — deliberately, because the reason this staging file exists is that the previous
pair of passwords was deleted while it was the only copy.

The paste-back compares strings rather than asking SQL Server, and that is not squeamishness. Both
logins have `CHECK_POLICY = ON`, so they inherit the Windows account lockout policy — on this
workstation `net accounts` reports a **threshold of 3 and a 15-minute lockout**. "Try it and see" has
three lives; string comparison has unlimited ones.

**Run it in a PowerShell window.** `Read-Host -AsSecureString` reads the console and ignores standard
input, so under a pipe it would block forever; the script detects a redirected stdin and refuses up
front instead. For a non-interactive shell there is `-Login <name>`, which copies one password to the
clipboard and exits.

Afterwards the guardrails read `LoaderPassword` and `MonitorPassword` from the environment, which is
the path UAT and Production take anyway; `--dev-credentials` is the workstation shortcut and losing it
is the point. The end-to-end proof, run **from the password manager's values**, is:

```powershell
$env:LoaderPassword  = '<pasted from the password manager>'
$env:MonitorPassword = '<pasted from the password manager>'
python build\guardrails.py --with-database
```

244 permission assertions attempted as the logins themselves. That is the strongest statement
available that the manager holds something that works, and it costs one login attempt per login.

**Subsequent runs need no password at all.** `Deploy-Database.ps1` asks the server which logins
already exist and only demands the variables for the ones that do not. It substitutes a placeholder
for the rest, because sqlcmd expands `$( )` while *reading* the file — before the server ever
evaluates the `IF` that guards `CREATE LOGIN` — so the token has to resolve even on a run that will
create nothing. The placeholder is never applied to a login.

---

## Rotating a password

040 never re-applies a password on a re-run. `ALTER LOGIN ... WITH PASSWORD` on an existing login
would rotate a working credential without being asked and break whatever DPAPI-encrypted copy the
applications already hold. Rotation is therefore a separate, deliberate act:

```powershell
$env:NewPassword = '<recorded in the password manager first>'
.\Reset-ApplicationPassword.ps1 -Login RCRAInfoLoader
```

**A rotation is not finished when `ALTER LOGIN` succeeds.** The application holds its own encrypted
copy of the password, so:

1. record the new password in the password manager
2. run the script
3. re-run the credential-seeding step on **every machine** running that application
4. confirm the next scheduled run succeeded

Skip 3 and you have an application that cannot log in and will not report it until its next
scheduled run — overnight, with no operator present.

---

## Locking down the credential file

The console application keeps its SQL password DPAPI-encrypted with the `LocalMachine` scope, which
means the ciphertext can be decrypted by **any process on that machine** that can read the file. The
`optionalEntropy` value is obfuscation, not a boundary: it lives in the same binary an attacker
already has. The file's ACL is the real control, and G18 puts the web application on the same
machine, so:

```powershell
.\Set-CredentialFileAcl.ps1 `
    -Path 'C:\Apps\RCRAInfo.Loader\appsettings.json' `
    -LoaderIdentity 'MDE\svc-rcrainfo-loader' `
    -AppPoolIdentity 'IIS AppPool\RCRAInfoMonitor'
```

Run it elevated. It turns inheritance off (so a group added to the parent folder six months from now
cannot leak in), writes an explicit ACL, and then **runs a real access check** through the Authz API
to confirm the application pool identity gets nothing and the loader still gets read. Both halves
are asserted: an ACL that locks out the application it protects gets reset by whoever is on call at
2am, and the reset removes the deny along with everything else.

`.\Set-CredentialFileAcl.ps1 -SelfTest` proves the mechanism in both directions against a temp file
and a well-known account, and needs no arguments, no server, and no elevation. It runs as part of
`build/guardrails.py`.

**What is still outstanding:** an end-to-end read attempt *as* the application pool identity. A
virtual account has no password, and `LogonUser` for one needs `SeTcbPrivilege` — i.e. LocalSystem —
so it cannot be done from a deployment script. The access check above is the same computation the
kernel performs on that open, against the same descriptor, but it is not the same evidence. Confirm
it in **Workstream C** from the running web application, once the application pool exists.

---

## Verifying a deployment

```powershell
python build\check_run_twice.py --server .
python build\check_permission_posture.py --server . --dev-credentials
```

Or both, together with every file-based guardrail:

```powershell
python build\guardrails.py --with-database --dev-credentials
```

`--with-database` is not the default because these tests **deploy**, twice.

- **`check_run_twice.py`** snapshots `sys.objects` (including `object_id`, so a `DROP`/`CREATE` in
  place of `CREATE OR ALTER` is caught), `sys.columns`, extended properties, the permission posture,
  the principals, and the audit stamps on every audited table; deploys again; and diffs. The audit
  stamps are the part that is easy to miss: a re-run that `UPDATE`s a row to the value it already
  holds moves `auditModifiedDateUtc`, raises no error, and leaves the audit trail claiming the data
  changed today.
- **`check_permission_posture.py`** asserts the DENY posture by **connecting as each application
  login and attempting the operation**. It does not ask the catalog what should happen. It also
  checks fixed database role and server role membership, because a role picked up later outranks
  every schema-level DENY in `050`.

---

## Deliberately not in the scripts

Two settings that belong to whoever operates the instance, both recommended, both left out because
each needs exclusive access to the database and would kill other sessions on a re-run — which would
make the deployment non-convergent:

```sql
ALTER DATABASE RCRAInfo SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
ALTER DATABASE RCRAInfo SET RECOVERY FULL;  -- or SIMPLE; a DBA decision, tied to the backup schedule
```

`READ_COMMITTED_SNAPSHOT ON` is recommended rather than optional in spirit: the monitoring web
application reads run status while the loader is mid-merge, which is the textbook reader/writer
blocking case. Apply it during a maintenance window.

The recovery model is tied to the backup schedule and to where these databases end up (G17, still
undecided).

---

## A SQL Server 2025 database cannot be moved to 2022

UAT and Production run **SQL Server 2022**; this workstation runs **2025**. Deploy by script or
DACPAC only — **never** by backup/restore or attach, which is a one-way trip up a version and will
simply be refused on the way back down. The local database is a working copy, never an artifact.

Compatibility level 160 is not a feature gate: it does not stop 2025-only syntax from compiling
here and failing in UAT. That gate is `tools/RCRAInfo.SqlCheck`, which parses every script with
`TSql160Parser`, plus the `validate-sql.py` rules. Both run in `build/guardrails.py`.
