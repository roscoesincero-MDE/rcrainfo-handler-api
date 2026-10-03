#!/usr/bin/env python
"""
Runs every RCRAInfo guardrail. One command for the developer, one step for CI.

The guardrails exist because the project's hardest constraints are invisible at the point of
violation. Nothing about writing `DELETE FROM dbo.HandlerSource` looks wrong; nothing about
`FROM epal_issi.dbo.Handler` fails on the workstation; nothing about `sp_addextendedproperty`
fails the first time it runs. Each check below turns one of those into an immediate error.

  check_gitignore_secrets     .gitignore really does exclude the files that hold a password (A1)
  validate-sql (conventions)  the DDL conventions, across every script rather than only edited ones
  check_sql_guardrails        the validator itself still rejects what it claims to (its fixtures)
  check_cross_database        no cross-database or cross-server reference outside .sql either
  check_search_term_privacy   the two free-text parameters -- 506's @SearchTerm and 513's @Notes --
                              and every variable derived from them never reach a column of
                              logs.ExecutionLog, which the monitoring web app can READ. Identifiers
                              and counts are logged; whatever a person typed is not
  check_credential_template   the two COMMITTED credential templates carry no filled-in value, name
                              the keys the applications read, keep the API credential out of the web
                              app's copy, and are registered as a configuration source nowhere. The
                              template is the one part of AR4 that is both tracked by git and shaped
                              like a place to type a password, and filling it in there rather than in
                              the copy errors on nothing
  check_http_client_logging   every AddHttpClient registration calls RemoveAllLoggers (). EPA's auth
                              endpoint carries both halves of the credential as PATH SEGMENTS, and stock
                              IHttpClientFactory logging prints the full request URI at Information --
                              measured, eight entries per request, two of them the whole URI. The leak is
                              a MISSING call, which is invisible in a diff, and the tests can only prove
                              the registrations that exist today are silent (D1)
  check_sort_defaults         each query record's default sort column is the one its procedure
                              declares. The four reads REFUSE an unrecognised @SortBy rather than
                              falling through to a default, and RCRAInfo.Data binds every parameter
                              explicitly -- so an unset SortBy reached the whitelist as NULL and the
                              read threw for a caller with no preference. The C# repeats the default to
                              fix that, and this holds the copy against the declaration
  measure_spec --assert       the pinned EPA swagger spec, and every count derived from it (B1)
  check_lookup_catalog        the loader's 23 hand-written lookup rows -- name, path segment, whether the
                              endpoint takes a stateCode -- still agree with that same pinned spec, and
                              DocumentedStatuses still matches it for all four endpoint families. Every
                              way this drifts is silent, because script 523 retires the codes a payload
                              did NOT contain: a stale path answers 404 and no /lookup/hd endpoint
                              documents one, so the list is simply never refreshed again; a spurious
                              stateCode is IGNORED by EPA, so a national answer is accepted as a Maryland
                              one. It also holds the 23 names against 523's own dispatch, which is the
                              same closed set written twice in two files that never mention each other
  generate_schema --check     the 46 committed schema scripts still match the spec they came from
  generate_payload_fixture    the committed 210-column payload fixture still matches the same pinned
      --check                 spec. It is the input to the round-trip tests below, and its values are
                              designed rather than plausible: every NVARCHAR is exactly its declared
                              width and ends in a Z, because OPENJSON ... WITH truncates SILENTLY (G36)
  Set-CredentialFileAcl       the credential-file deny is written and detected, in both directions
  RCRAInfo.SqlCheck           every script parses as SQL Server 2022 (TSql160Parser), on a 2025 box
  dotnet build                the banned-API list is enforced; RS0030 is an error

Every check above runs against files alone, so it runs anywhere. The acceptance tests need a live
server and the deployment already applied, so they are behind --with-database:

  check_run_twice             a second full deployment changes nothing in the catalog ([R8])
  check_permission_posture    the DENY posture holds when attempted AS each application login (A5)
  check_stored_headers        every DEPLOYED module carries its header in sys.sql_modules, and every
                              non-exempt procedure records its own errors. Both were review findings
                              from MDE on 2026-09-05, and both were true of the deployed database
                              while every file on disk looked correct
  check_cascade_coverage      the hand-written soft-delete cascade in 522 still covers every descendant
                              of dbo.HandlerSource, derived from sys.foreign_keys -- and still leaves
                              the logs.* audit trail alone
  check_lookup_coverage       the hand-written dispatch in 523 still merges and retires every mirrored
                              dbo.Lookup* table, writes every one of their columns, and agrees with
                              sys.columns about each list's Code width and its activityLocation. The
                              tables are generated and the procedure is not, so this pair drifts in one
                              direction and drifts silently
  check_closed_set_filters    every paged read that filters on a column this database closes with a CHECK
                              constraint REJECTS a value outside that set, names the constraint it copies,
                              and lists the same set in prose. A missing check here has no error message
                              and no wrong row -- it returns an empty page, and on the grid whose job is
                              to show failures an empty page reads as all-clear
  check_detail_projection     505's 215 hand-written column names still match sys.columns on
                              dbo.vwHandlerSourceHistory, in the file and in the deployed module. The view
                              is generated and the projection is not, so the next field EPA adds appears
                              in the database and never on the detail screen -- with nothing failing
  check_result_shapes         the nine C# result classes in src/RCRAInfo.Data/Results still match the
                              projections that fill them, and the seven procedures called through
                              ExecuteAsync still return no result set. The classes were EMITTED from
                              describe_first_result_set, and EF Core binds by column NAME: a renamed
                              column leaves the property at its type default on every row, which reads
                              as data. `dotnet ef dbcontext scaffold` cannot cover this -- it generates
                              nothing for a procedure result set
  check_execution_log_privacy no credential, no payload and nothing over 4000 characters ever reached a
                              free-text column in the logs schema, every one of which the monitoring web
                              app can READ (AR8). The two ways one gets in are both one stack frame from
                              correct code: logging a whole exception from the HTTP stack, whose URI
                              carries a query string when RCRAInfo's credentials travel in headers, and
                              building @KeyParameters by concatenating everything a procedure received.
                              Neither errors. It also proves its own predicates against a fixture on
                              every run, because a scan of an empty table would otherwise pass while
                              examining nothing
  DA5 round-trip tests        every procedure is CALLED and its result materialised, which is the one
                              thing none of the checks above can do: `dotnet ef dbcontext scaffold`
                              generates nothing for a stored-procedure result set, so scaffold-and-diff
                              covers the two views and nothing else. The theory asserts FIELD-LEVEL
                              non-nullity over all 210 payload columns, because with JSON rather than a
                              table type a renamed or mis-cased path shreds to NULL and the row count
                              does not change -- a test that counted rows would pass while a column had
                              quietly stopped loading. The suite WRITES, into a database with no hard
                              delete, so it is opt-in; this is the run that opts in

Every check runs even after an earlier one fails, so a single run reports everything that is wrong
rather than the first thing. Exit 0 only when all of them pass.

  python build/guardrails.py                       everything that needs no server
  python build/guardrails.py --no-dotnet           Python checks only, for a box without the SDK
  python build/guardrails.py --with-tests          also run dotnet test
  python build/guardrails.py --with-database       also run the ten acceptance tests
  python build/guardrails.py --with-database --dev-credentials
                                                   ... reading the local SQL passwords from
                                                   %LOCALAPPDATA%\\RCRAInfo\\dev-credentials.json

--with-database is deliberately not the default. It DEPLOYS, twice, and a check that alters a server
as a side effect has no business running because someone typed the short form of the command.

Note on scope: `dotnet build` is what makes the BannedSymbols.txt bans real, because RS0030 is
escalated to an error. What it does NOT check is whether the entries in that file still resolve to
symbols that exist -- an entry naming a renamed API is ignored in silence. That is what
BannedSymbolsTests covers, so on any change to EF Core versions or to BannedSymbols.txt, run with
--with-tests.
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEPLOYMENT = REPO / "src" / "RCRAInfo.Database" / "Deployment"


class Check:
    """One guardrail: a label, a command, and whether it needs the .NET SDK."""

    def __init__(self, name: str, command: list[str], *, needs_dotnet: bool = False,
                 requires: Path | None = None, env: dict[str, str] | None = None) -> None:
        self.name = name
        self.command = command
        self.needs_dotnet = needs_dotnet
        self.requires = requires

        # Added to this child's environment only. The DA5 round-trip suite is opt-in through
        # RCRAINFO_INTEGRATION precisely so that it cannot run by accident, and setting it in this
        # process would opt every later `dotnet test` in as well.
        self.env = env


def python_check(name: str, script: str, *args: str) -> Check:
    path = REPO / "build" / script
    return Check(name, [sys.executable, str(path), *args], requires=path)


def powershell_check(name: str, script: Path, *args: str) -> Check:
    # powershell.exe, not pwsh: Windows PowerShell 5.1 is the floor for every script in this project,
    # and running the check on 7 would stop proving that.
    return Check(
        name,
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script), *args],
        requires=script)


def build_checks(args: argparse.Namespace) -> list[Check]:
    validator = REPO / ".claude" / "hooks" / "validate-sql.py"

    checks = [
        python_check("gitignore secrets", "check_gitignore_secrets.py"),
        Check("SQL conventions",
              [sys.executable, str(validator), str(REPO / "src" / "RCRAInfo.Database")],
              requires=validator),
        python_check("SQL validator fixtures", "check_sql_guardrails.py"),
        python_check("cross-database references", "check_cross_database.py"),
        python_check("free-text parameter privacy", "check_search_term_privacy.py"),
        python_check("sort defaults agree", "check_sort_defaults.py"),
        python_check("credential templates", "check_credential_template.py"),
        python_check("HttpClient logging", "check_http_client_logging.py"),
        python_check("API spec baseline", "measure_spec.py", "--assert"),
        python_check("lookup catalog", "check_lookup_catalog.py"),
        python_check("generated schema is current", "generate_schema.py", "--check"),
        python_check("payload fixture is current", "generate_payload_fixture.py", "--check"),
        powershell_check("credential-file ACL self-test",
                         DEPLOYMENT / "Set-CredentialFileAcl.ps1", "-SelfTest"),
        Check("SQL Server 2022 parse",
              ["dotnet", "run", "--project", str(REPO / "tools" / "RCRAInfo.SqlCheck"),
               "--verbosity", "quiet"],
              needs_dotnet=True),
        Check("build and banned APIs",
              ["dotnet", "build", str(REPO / "RCRAInfo.sln"), "--verbosity", "quiet",
               "--nologo"],
              needs_dotnet=True),
    ]

    if args.with_tests:
        checks.append(Check(
            "tests",
            ["dotnet", "test", str(REPO / "RCRAInfo.sln"), "--verbosity", "quiet", "--nologo"],
            needs_dotnet=True))

    if args.with_database:
        posture_args = ["--server", args.server]
        if args.dev_credentials:
            posture_args.append("--dev-credentials")

        # Posture, stored headers, the six drift checks, the round-trip tests, then run-twice. Not
        # alphabetical, and not arbitrary: run_twice DEPLOYS, so putting it last means the other nine
        # are reported against the database as the developer left it, rather than against one this
        # script has just redeployed underneath them. The round-trip tests are second-last because
        # they WRITE: a finding of "the deployed object is not what the file says" is worth having
        # before anything adds rows, and worth having before run_twice quietly repairs the object.
        # The eight read-only catalog checks have free positions and sit
        # here because their finding is "the deployed object is not what the file says", which is
        # worth knowing BEFORE a redeployment quietly repairs it and hides how long it had been true.
        # check_detail_projection makes that explicit -- it reports file-versus-deployed drift as its
        # own finding, and run_twice would erase the evidence for it.
        checks.append(python_check("permission posture", "check_permission_posture.py",
                                   *posture_args))
        checks.append(python_check("stored headers", "check_stored_headers.py",
                                   "--server", args.server))
        checks.append(python_check("cascade coverage", "check_cascade_coverage.py",
                                   "--server", args.server))
        checks.append(python_check("lookup coverage", "check_lookup_coverage.py",
                                   "--server", args.server))
        checks.append(python_check("closed-set filters", "check_closed_set_filters.py",
                                   "--server", args.server))
        checks.append(python_check("detail projection", "check_detail_projection.py",
                                   "--server", args.server))
        checks.append(python_check("result shapes", "check_result_shapes.py",
                                   "--server", args.server))
        checks.append(python_check("execution log privacy", "check_execution_log_privacy.py",
                                   "--server", args.server))

        # The only check here that CALLS the procedures rather than reading the catalog, and the only
        # one that writes. It goes after the eight read-only checks for the reason they are ordered at
        # all -- a finding of "the deployed object is not what the file says" is worth having before
        # anything touches the database -- and before run_twice for the same reason run_twice is last.
        #
        # RCRAINFO_INTEGRATION is set here rather than left to the developer's shell because that is
        # the whole opt-in: --with-database already means "this run may alter the server", so the
        # decision has been made by the time this line is reached. Without it the suite skips, and a
        # green guardrails run would report an acceptance test that examined nothing.
        checks.append(Check(
            "DA5 round-trip tests",
            ["dotnet", "test",
             str(REPO / "tests" / "RCRAInfo.Data.Integration.Tests"
                 / "RCRAInfo.Data.Integration.Tests.csproj"),
             "--verbosity", "quiet", "--nologo"],
            needs_dotnet=True,
            env={
                "RCRAINFO_INTEGRATION": "1",
                "RCRAINFO_INTEGRATION_SERVER": args.server,
            }))

        checks.append(python_check("run deployment twice", "check_run_twice.py",
                                   "--server", args.server))

    return checks


def run(check: Check) -> tuple[bool, str, float]:
    started = time.monotonic()
    environment = None if check.env is None else {**os.environ, **check.env}
    result = subprocess.run(
        check.command, cwd=REPO, capture_output=True, text=True,
        encoding="utf-8", errors="replace", env=environment,
    )
    elapsed = time.monotonic() - started
    output = ((result.stdout or "") + (result.stderr or "")).rstrip()
    return result.returncode == 0, output, elapsed


def main(argv: list[str]) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    parser = argparse.ArgumentParser(
        description="Runs every RCRAInfo guardrail.",
        epilog=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--no-dotnet", action="store_true",
                        help="skip the checks that need the .NET SDK")
    parser.add_argument("--with-tests", action="store_true", help="also run dotnet test")
    parser.add_argument("--with-database", action="store_true",
                        help="also run the ten acceptance tests; these DEPLOY to --server")
    parser.add_argument("--server", default=".",
                        help="target SQL Server instance for --with-database (default: .)")
    parser.add_argument("--dev-credentials", action="store_true",
                        help="read the application logins' passwords from the developer "
                             "credentials file rather than the environment")
    args = parser.parse_args(argv)

    if args.dev_credentials and not args.with_database:
        # Accepting it silently would suggest it did something.
        print("--dev-credentials only applies with --with-database.")
        return 2

    skip_dotnet = args.no_dotnet
    have_dotnet = shutil.which("dotnet") is not None

    if not skip_dotnet and not have_dotnet:
        # Not a skip: a missing SDK on a machine that is supposed to build is a broken machine, and
        # silently dropping half the guardrails would report a green run that proved much less.
        print("FAIL  guardrails: the .NET SDK is not on PATH, so the downlevel-parse and "
              "banned-API checks cannot run. Install it, or pass --no-dotnet to accept a partial "
              "run.")
        return 1

    checks = build_checks(args)
    results: list[tuple[str, bool | None, str, float]] = []

    for check in checks:
        if check.requires is not None and not check.requires.is_file():
            results.append((check.name, False, f"{check.requires} does not exist.", 0.0))
            continue

        if check.needs_dotnet and skip_dotnet:
            results.append((check.name, None, "skipped: --no-dotnet", 0.0))
            continue

        passed, output, elapsed = run(check)
        results.append((check.name, passed, output, elapsed))

        label = "PASS" if passed else "FAIL"
        print(f"[{label}] {check.name} ({elapsed:.1f}s)")
        if output:
            for line in output.splitlines():
                print(f"       {line}")

    for name, passed, output, _ in results:
        if passed is None:
            print(f"[SKIP] {name}: {output}")

    failed = [name for name, passed, _, _ in results if passed is False]
    skipped = [name for name, passed, _, _ in results if passed is None]
    ran = len(results) - len(skipped)

    print()
    if failed:
        print(f"FAIL  guardrails: {len(failed)} of {ran} check(s) failed - "
              f"{', '.join(failed)}")
        return 1

    tail = f" ({len(skipped)} skipped: {', '.join(skipped)})" if skipped else ""
    print(f"PASS  guardrails: {ran} check(s) passed{tail}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
