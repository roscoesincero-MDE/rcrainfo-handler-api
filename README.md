# RCRAInfo Handler Mirror

Mirrors EPA's **RCRAInfo Handler** data set into a Maryland Department of the Environment SQL Server
database, using the RCRAInfo REST APIs. A console application does one initial load of everything and
then nightly update-only loads under Windows Task Scheduler; a small web application reports on what
those runs did.

It exists because MDE needs Handler data locally, on a schedule, with a record of every load — Phase 1
of the requirement in [`Requirements.txt`](Requirements.txt). Phase 2 (migrating this data into the
agency's ETS system) is out of scope here.

---

## Status

**Phase 1, in progress.** The database, the data-access layer, and the loader pipeline exist and are
tested; the monitoring application is not built yet.

| Piece | Where | State |
|---|---|---|
| Database (5 schemas, ~90 scripts) | `src/RCRAInfo.Database` | deployed by script, convergent on re-run |
| Credential bootstrap (DPAPI) | `src/RCRAInfo.Core` | done |
| Data access (EF Core, procedure-only) | `src/RCRAInfo.Data` | done |
| Loader console application | `src/RCRAInfo.Loader` | done, not yet run against a real EPA credential |
| Monitoring web application | `src/RCRAInfo.Monitor` | scaffold only — default Razor Pages |
| Downlevel SQL syntax gate | `tools/RCRAInfo.SqlCheck` | done |
| Unit + integration tests | `tests/` | 4 projects; the integration suite needs a database |

Two things are deliberately unmeasured until a real API credential is in hand: EPA's rate tolerance
(the throttle defaults are an assumption, not a published limit) and the load journal's flush
settings. No badges — this is an internal project with no public CI.

---

## Prerequisites

* **Windows.** Not a preference — the loader is marked `[SupportedOSPlatform("windows")]`. DPAPI
  encrypts the stored passwords, an NTFS deny-read ACE is the real control on the credential file,
  and Task Scheduler runs the load.
* **.NET 10 SDK.** `TargetFramework` is `net10.0` (this workstation has 10.0.203).
* **SQL Server.** Local instance for development; UAT and Production run **SQL Server 2022**, so
  everything is written to the 2022 grammar. Deploying needs a login with `CREATE DATABASE` and
  `CREATE LOGIN` (sysadmin locally); `Deploy-Database.ps1` warns if yours has neither.
  *A 2025 database cannot be moved down to 2022 — deploy by script or DACPAC, never by backup/restore.*
* **`sqlcmd`** on `PATH` (or pass `-SqlCmdPath`), and **PowerShell** for the deployment scripts.
* **An RCRAInfo API ID and Key**, issued *per environment* (preprod and production keys are not
  interchangeable). The Key is displayed once, at generation.
* **A password manager entry** for each SQL login before you create it — there is no way to read one
  back out of SQL Server.
* *Optional:* **Python 3** with `python-docx`, `openpyxl` and `Pillow`, only to regenerate the Word
  and Excel documents under `docs/`.

---

## Installation / setup

### 1. Build and run the offline tests

```bash
dotnet build RCRAInfo.sln
dotnet test  RCRAInfo.sln
```

Warnings are errors here, and banned-API analyzers enforce the data-access rules at compile time, so
a clean build is a meaningful signal. Without `RCRAINFO_INTEGRATION` set, the integration suite
skips itself and everything else runs with no database.

### 2. Deploy the database

Generate the two application passwords, **record them in the password manager first**, then:

```powershell
$env:LoaderPassword  = '<from the password manager>'
$env:MonitorPassword = '<from the password manager>'

cd src\RCRAInfo.Database\Deployment
.\Deploy-Database.ps1 -ServerInstance '.'
```

Scripts `010`–`050` build the database, schemas, logins, roles and the DENY posture; everything from
`100` up is discovered automatically in numeric order (tables before the things that reference them).
Every script converges — a second run changes nothing. `-WhatIf` prints the plan without touching the
server. Subsequent runs need no passwords: the script asks the server which logins already exist and
only demands the variables for the ones that do not.

Details, rotation procedure, and what `Reset-ApplicationPassword.ps1` /
`Move-DevCredentialsToPasswordManager.ps1` are for: [`src/RCRAInfo.Database/Deployment/README.md`](src/RCRAInfo.Database/Deployment/README.md).

### 3. Configure and seal the loader's credentials

Beside the built executable:

```powershell
copy appsettings.Template.json appsettings.json   # non-secret settings; fill in two values
copy secrets.Template.json     secrets.json       # SqlPassword, ApiId, ApiKey; leave Encrypted false
```

Lock the credential file down, then seal it:

```powershell
.\Set-CredentialFileAcl.ps1 -Path 'C:\Apps\RCRAInfo.Loader\secrets.json' `
    -LoaderIdentity 'MDE\svc-rcrainfo-loader' -AppPoolIdentity 'IIS AppPool\RCRAInfoMonitor'

RCRAInfo.Loader --seed-only
```

`--seed-only` validates the SQL password and the API pair, encrypts them in place with DPAPI, sets
`Encrypted: true`, and **loads nothing**. Run it under the service identity that will run the load —
DPAPI ciphertext is machine-bound. If validation fails the file is left exactly as you typed it so the
typo can be fixed. Exit code 2 means sealed-and-nothing-loaded; that is success for this step.

### 4. Schedule it

A Task Scheduler action running `RCRAInfo.Loader` with no arguments is the nightly load. Alert on the
exit code, not on the console — see the table below.

---

## Usage

### The loader

```
RCRAInfo.Loader                                     the scheduled load, scoped by config.LoadWatermark
RCRAInfo.Loader --seed-only                         prove and seal the credentials; load nothing
RCRAInfo.Loader --handler-id <id> --current-record   one handler, the version EPA marks current
RCRAInfo.Loader --handler-id <id> --every-version    one handler, its entire history
RCRAInfo.Loader --probe-summaries <from> <to>        report on one summaries window (yyyy-MM-dd); writes nothing
RCRAInfo.Loader --probe-source <id>                  report EPA's raw date text for one handler; writes nothing
```

An unrecognised switch stops the run rather than being ignored: every switch asks for *less* than the
default, so a typo would otherwise start the full scheduled load — which on a fresh database is the
initial load. `--handler-id` requires a scope, and the modes above cannot be combined.

The targeted runs are the diagnostic path: they read no watermark, move none, refresh no code list,
and resume from nothing, so they are safe to use while a scheduled load is not running. The two probe
modes write nothing anywhere and work on a database that has never loaded.

**Exit codes are the interface**, because Task Scheduler reads them and nobody reads a console at 2am.
Zero means a completed load and nothing else.

| Code | Meaning |
|---|---|
| 0 | load completed |
| 2 | credentials sealed (`--seed-only`); nothing loaded |
| 3 | credential failure |
| 4 | required configuration missing |
| 5 | load partially succeeded — some windows failed |
| 6 | load failed |
| 7 | cancelled |
| 8 | no run was opened (e.g. no `InitialLoadFromDate` on a first run) |
| 9 | bad command line |
| 10 | probe answered |
| 11 | probe failed |

Progress and outcomes land in `logs.LoadRun`, `logs.HandlerLoadStatus`, `logs.HandlerLoadAttempt` and
`logs.DataQualityObservation`; the bookmark lives in `config.LoadWatermark`. A killed run is resumed
from `logs.HandlerLoadStatus` rather than restarted — the summaries feed has no offset or limit, so
that table is the only record of how far a run got.

### The monitoring web application

```bash
dotnet run --project src/RCRAInfo.Monitor    # http://localhost:5073 / https://localhost:7110
```

Currently the default Razor Pages scaffold. Its `secrets.Template.json` holds only a `SqlPassword`
(for the `RCRAInfoMonitor` login) — it never calls EPA, so it has no API credential, and the loader's
credential file denies read access to the web application's identity.

### The SQL syntax gate

```bash
dotnet run --project tools/RCRAInfo.SqlCheck            # parses src\RCRAInfo.Database by default
dotnet run --project tools/RCRAInfo.SqlCheck -- <path>
```

Parses every deployment script with `TSql160Parser` (SQL Server 2022 grammar) so 2025-only syntax
fails here rather than at the UAT deployment. It is a *syntax* gate only — native `json`, `REGEXP_*`,
`VECTOR` and friends parse cleanly and are caught elsewhere. Finding no `.sql` files is a failure, not
a pass.

### Integration tests

```powershell
$env:RCRAINFO_INTEGRATION = '1'
$env:LoaderPassword       = '<RCRAInfoLoader password>'
dotnet test tests\RCRAInfo.Data.Integration.Tests
```

These call every stored procedure against a real database and write real rows.

---

## Configuration

### `appsettings.json` — non-secret, from `appsettings.Template.json`

Any key can also come from the environment, using a **double underscore** as the separator
(`RCRAInfoApi__BaseAddress`, `RCRAInfoLoad__ActivityLocation`, …); environment variables win over the
file. That is how a Task Scheduler action configures a machine with no configuration file.

| Setting | Required | Notes |
|---|---|---|
| `RCRAInfoData:ConnectionString` | yes | the loader's own SQL login, **no password**; Integrated Security is refused |
| `RCRAInfoData:MaxPayloadElements` | no (500) | ceiling on one JSON payload parameter. **2500 in the template on purpose** — EPA serves 1,701 NAICS codes and a lookup list too large for one call cannot be split |
| `RCRAInfoApi:BaseAddress` | yes | preprod `https://rcranodepreprod.epa.gov/rcra-api/rest`, production `https://rcranode.epa.gov/rcra-api/rest`. No default: a wrong one writes preprod data into Production and reports success |
| `RCRAInfoApi:RequestTimeout` / `TokenRefreshMargin` / `MinimumRefreshInterval` | no | EPA tokens live 20 minutes; an initial load outlives many of them |
| `RCRAInfoApi:Throttle:*` | no | `MaxRequestsPerSecond` 2, `MaxConcurrentRequests` 2, `MaxRetryAttempts` 3, backoff 2s→30s. EPA publishes no rate limit; a `Retry-After` header wins over all of it |
| `RCRAInfoLoad:ActivityLocation` | yes | `MD` for MDE. No default — the lookup refresh runs in Full mode and retires codes it does not see |
| `RCRAInfoLoad:InitialLoadFromDate` | yes, before the first run | recommended `1980-01-01`. A floor, not a start: a watermark recommendation always wins, so it cannot rewind an established mirror. Earlier than 1900-01-01 is refused |
| `RCRAInfoLoad:WindowDays` | no (7, max 366) | the only lever on summaries response size, and the unit the watermark advances in |
| `RCRAInfoLoad:AbandonAfterMinutes` | no (720, min 15) | how long a run may sit at `Running` before the next run treats it as killed |
| `RCRAInfoLoad:ResumeMaxAgeHours` | no (48) | how old a success may be and still be trusted enough to skip. `null` = no limit; a correctness setting, since EPA updates handler sources in place |
| `RCRAInfoLoad:FlushRowCount` / `FlushInterval` | no (100 / 30s) | how often buffered journal rows are written, and how stale the monitor's view can be |

### `secrets.json` — from `secrets.Template.json`, never in source control

`Encrypted` (leave `false`), `SqlPassword`, `ApiId`, `ApiKey`. The first run encrypts in place and
flips the flag; do not set it by hand. Once it reads `true` the plaintext is gone from the machine and
the password manager holds the only copy. `.gitignore` excludes `appsettings*.json` (except the
template), `secrets.json`, `dev-credentials.json`, and a broad set of `*apikey*` / `*api-key*`
patterns — that last group exists because a stray `apiKey.txt` once sat in the repository root.

### Environment variables used outside the applications

| Variable | Used by |
|---|---|
| `LoaderPassword`, `MonitorPassword` | `Deploy-Database.ps1` (login creation), integration tests |
| `NewPassword` | `Reset-ApplicationPassword.ps1` |
| `RCRAINFO_INTEGRATION` | enables the integration suite |
| `RCRAINFO_INTEGRATION_SERVER` / `_DATABASE` | target instance (default `.` / `RCRAInfo`) |
| `RCRAINFO_INTEGRATION_ALLOW_REMOTE` | required to point the suite at a non-local server |
| `CI` | turns on `ContinuousIntegrationBuild` |

Passwords go through the environment rather than the command line so nothing secret appears in the
process list.

### Server settings left out of the deployment scripts

Both need exclusive database access, so including them would make the deployment non-convergent.
Apply them by hand, in a maintenance window:

```sql
ALTER DATABASE RCRAInfo SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;  -- recommended
ALTER DATABASE RCRAInfo SET RECOVERY FULL;  -- or SIMPLE; a DBA decision, tied to the backup schedule
```

---

## Further reading

* [`Requirements.txt`](Requirements.txt) — the Phase 1 / Phase 2 requirement and EPA's documentation links
* [`spec/rcrainfo/README.md`](spec/rcrainfo/README.md) — the pinned EPA Swagger document, why it is
  pinned, and how preprod differs from production
* [`src/RCRAInfo.Database/Deployment/README.md`](src/RCRAInfo.Database/Deployment/README.md) —
  deployment, password rotation, credential-file ACLs
* [`Directory.Build.props`](Directory.Build.props) — how everything here is compiled, and why
* `docs/api-keys/`, `docs/data-quality/`, `docs/phase1-status/` — the explainer documents
