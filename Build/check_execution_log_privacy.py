#!/usr/bin/env python
"""Acceptance test: no credential and no payload ever reached a log column the web app can read.

AR8 is the rule this measures. MDE's own procedure template states it first -- "do NOT include
parameters such as passwords and Personally Identifiable Information (PII)" -- and this project extends
it to every free-text column in the logs schema: KeyParameters, Comments, ErrorMessage, DynamicSql,
ContextMessage, FailureMessage, ApiErrorMessage, RequestPath, Detail and ObservedValue. Identifiers,
dates and counts are logged. Nothing else is.

WHY THIS IS A DATABASE CHECK AND NOT A CODE REVIEW. Every one of these columns is populated from a
string that some caller composed, and the two ways a credential gets in are both one stack frame away
from correct code:

  * `catch (Exception ex) { ...FailureMessage: ex.ToString () }`. A SqlException message is safe here. An
    HttpRequestException from the RCRAInfo client is not always, because a request URI carries a query
    string and the RCRAInfo credentials travel in headers -- so a handler that logs the whole exception
    from anywhere in the HTTP stack can write an API ID into a table the monitoring web app displays.
  * A @KeyParameters string built by concatenating "everything this procedure received". Correct for
    twelve procedures and wrong for the five that take @Payload, @Elements, @Summaries or @Notes.

Neither shows up as an error. The row is written, the run succeeds, and the value sits in a table with a
web page in front of it until somebody reads it.

WHAT IT CHECKS

  1. CREDENTIALS   No scanned value contains a credential-shaped token: apikey, api_key, api-key,
                   x-api-id, x-api-key, bearer, authorization, password, pwd=, client_secret, secret=.
  2. VOLUME        No scanned value exceeds MAX_LENGTH characters. Length is the honest proxy for "this
                   is not an identifier": a KeyParameters string is a handful of names and integers, and
                   nothing legitimate in these columns runs to four thousand characters. What does run
                   that long is a serialised payload, a stack trace, or a response body.
  2b. TRUNCATION   And no scanned value sits at exactly its column's declared width. Only five of the
                   fourteen columns are NVARCHAR (MAX); on the other nine `LEN (col) > 4000` can never
                   be true no matter what was assigned, because the assignment succeeds and clips. So on
                   those columns a value at exactly the declared width is what an over-long one looks
                   like after the fact, and it is reported as truncation rather than as length -- the
                   finding is not "this is too long" but "something longer was written and the rest of
                   it is gone". Declared widths come from sys.columns, not from the DDL, so an ALTER
                   that widens a column does not leave this reporting the old limit.
  3. PAYLOAD SHAPE No scanned value looks like a JSON payload -- '[{"' or '"handlerId":"' inside a log
                   column means an @Elements or @Payload argument was logged whole, which for 400's
                   payload means contact names. Every payload token requires the ':"' of a real value;
                   see PAYLOAD_TOKENS for the false positive that taught this check the difference.
  4. COVERAGE      Every free-text column in the logs schema is either scanned or excluded BY NAME with
                   a reason. SCANNED is hand-written and the tables are generated, so it drifts in one
                   direction, and an unscanned column is an unmeasured place for a credential to land.

WHY IT STILL EXAMINES SOMETHING ON AN EMPTY DATABASE. A scan of zero rows passes, and a check that
passes while examining nothing is the failure this project keeps finding. So the same predicates are run
first against a fixture of known-bad AND known-good strings, every time, and a predicate that stops
catching its fixture -- or that starts flagging a compliant value -- is a finding rather than a silent
pass. The live scan then reports the value count it covered, so a green run says how much it actually
saw. This mirrors Set-CredentialFileAcl.ps1 -SelfTest, which proves the ACL check in both directions for
the same reason.

Requires: sqlcmd on PATH, Windows authentication, and the deployment already applied. Read-only -- it
issues SELECTs and nothing else, which also means it is safe to run against UAT.

Exit 0 when nothing was found, 1 on a finding, 2 on a setup problem.
"""

from __future__ import annotations

import argparse
import shutil
import subprocess
import sys

# Nothing legitimate in a log column is longer than this. See finding 2.
MAX_LENGTH = 4000

# Every free-text column in the logs schema, with the table it lives on and the identifier to report a
# hit by. Derived from the DDL rather than guessed: a column added to one of these tables and not added
# here is what MISSING_COLUMNS below turns into a finding.
SCANNED = [
    ("logs.ExecutionLog", "ExecutionLogId",
     ["KeyParameters", "Comments", "ErrorMessage", "DynamicSql", "ContextMessage"]),
    ("logs.LoadRun", "LoadRunId", ["FailureMessage"]),
    ("logs.HandlerLoadStatus", "HandlerLoadStatusId", ["ApiErrorMessage", "EtsErrorMessage"]),
    ("logs.HandlerLoadAttempt", "HandlerLoadAttemptId",
     ["RequestPath", "ApiErrorMessage", "FailureMessage"]),
    ("logs.DataQualityObservation", "DataQualityObservationId",
     ["JsonPath", "ObservedValue", "Detail"]),
]

# Free-text-shaped columns in the logs schema that are NOT scanned, each with the reason. Excluded by
# name rather than by a pattern, so adding a column is a decision somebody writes down.
EXCLUDED = {
    "logs.ExecutionLog.ProcedureName":
        "set from QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID)) + '.' + QUOTENAME (OBJECT_NAME (@@PROCID)) "
        "by MDE's house template. The engine composes it from the catalog; no caller supplies it.",
    "logs.ExecutionLog.ErrorProcedure":
        "set from ERROR_PROCEDURE () inside a CATCH. Engine-generated, same as ProcedureName.",
}

# Credential-shaped tokens. Matched case-insensitively, because a header name arrives in whatever case
# the server sent it and "APIKEY" is the same disclosure as "apiKey".
#
# LIKE patterns rather than a regex: SQL Server 2025's REGEXP_LIKE is not available on the 2022 target,
# and PATINDEX cannot express alternation. One predicate per token is longer to read and runs on the
# engine this project actually deploys to.
CREDENTIAL_TOKENS = [
    "apikey",
    "api_key",
    "api-key",
    "x-api-id",
    "x-api-key",
    "bearer",
    "authorization",
    "password",
    "pwd=",
    "client_secret",
    "secret=",
]

# A serialised payload in a log column. Every token requires a property name IMMEDIATELY FOLLOWED BY A
# QUOTED VALUE, and that is not fussiness -- it is a correction. The first version of this check matched
# the bare property name '"retrievedDateUtc"' and flagged two real rows that were entirely compliant:
# script 400's own refusal message names the shape it wanted, `an array of {"retrievedDateUtc":...,
# "handler":{...}} envelopes`. That message contains no data at all. A predicate that fires on a
# procedure explaining its own contract is a predicate that gets switched off, so the tokens now insist
# on the ':"' that only a real value produces. See the FIXTURE entry pinning that message as compliant.
PAYLOAD_TOKENS = [
    '[{"',
    '"handlerid":"',
    '"firstname":"',
    '"lastname":"',
    '"email":"',
]

# The fixture the predicates are proven against on every run. (value, must_be_flagged).
FIXTURE = [
    # Credentials, in the forms they actually arrive in.
    ("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9", True),
    ("GET /api/v1/hd/handler?apiKey=abc123", True),
    ("x-api-id=MDEDEV01;x-api-key=00000000", True),
    ("Login failed. Password=hunter2", True),
    ("client_secret=0123456789abcdef", True),
    # A payload logged whole.
    ('[{"handlerId":"MDD000000001","contact":{"firstName":"Ada"}}]', True),
    ('{"handlerId":"MDD000000001","sourceType":"P","sequence":3}', True),
    ('{"firstName":"Ada","lastName":"Lovelace","email":"ada@example.gov"}', True),
    # Too long to be an identifier list.
    ("HandlerId=MDD000000001; " * 400, True),
    # What a compliant row looks like. Every one of these must pass, or the check would report a
    # finding on correct data and be turned off within a week.
    ("LoadRunId=41; Mode=Upsert; ElementCount=500; PayloadSha256=3b1f...", False),
    ("HandlerId=MDD000000001; SourceType=P; Sequence=3", False),
    ("Skip=0; Take=50; TermLength=4; IdShaped=0", False),
    ("Violation of UNIQUE KEY constraint 'UX_dbo_HandlerSource_Natural'.", False),
    ("/api/v1/hd/handler/MDD000000001/P/3", False),
    ("FeedName=HandlerSource; ActivityLocation=MD; NotesSupplied=1", False),
    ("", False),

    # Script 400's refusal message, verbatim. It NAMES the payload shape without containing any of it,
    # and the first version of these predicates flagged it -- twice, from a real database. Pinned here
    # so that sharpening a token later cannot re-introduce the false positive: a procedure that
    # explains its own contract must stay clean.
    ('@Payload is NULL, is not valid JSON, or is not a JSON array. It must be an array of '
     '{"retrievedDateUtc":...,"handler":{...}} envelopes, even for a single record. Nothing was '
     'merged.', False),

    # 523's equivalent, for the same reason.
    ('@Elements must be a JSON array of {"code":...,"description":...} objects.', False),
]


def flagged(value: str) -> list[str]:
    """Why a value is a finding, in the same terms the SQL predicates use. Empty means it is clean."""
    reasons: list[str] = []
    lowered = value.lower()

    for token in CREDENTIAL_TOKENS:
        if token in lowered:
            reasons.append(f"credential-shaped token {token!r}")

    for token in PAYLOAD_TOKENS:
        if token in lowered:
            reasons.append(f"payload-shaped token {token!r}")

    if len(value) > MAX_LENGTH:
        reasons.append(f"{len(value)} characters, over the {MAX_LENGTH}-character limit")

    return reasons


def self_test() -> list[str]:
    """Findings from running the predicates against the fixture. Empty means they still work."""
    findings: list[str] = []

    for value, must_flag in FIXTURE:
        reasons = flagged(value)

        if must_flag and not reasons:
            findings.append(
                f"the predicates no longer flag a value they are written to catch: "
                f"{summarise(value)}. Whatever was removed from CREDENTIAL_TOKENS, PAYLOAD_TOKENS or "
                f"MAX_LENGTH took the rule with it, and the live scan below would pass on the same "
                f"value sitting in a log column.")

        if not must_flag and reasons:
            findings.append(
                f"the predicates flag a COMPLIANT value: {summarise(value)} -- {'; '.join(reasons)}. "
                f"A check that fails on correct data gets switched off, so this is as serious as a "
                f"missed credential.")

    return findings


def summarise(value: str) -> str:
    """A value, short enough to print and never long enough to reproduce a secret.

    A finding must not put the thing it found into a build log. The first sixteen characters are enough
    to locate the row and are not enough to use.
    """
    if not value:
        return "(empty)"

    head = value[:16].replace("\r", " ").replace("\n", " ")
    return f"{head!r} ... ({len(value)} characters)" if len(value) > 16 else repr(head)


def as_developer(server: str, query: str) -> list[str]:
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    # -y 4000 rather than -W, which sqlcmd rejects together with -y. The reported value is already
    # truncated to its first characters by the query itself, so nothing secret is displayed at any width.
    result = subprocess.run(
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", "RCRAInfo", "-h", "-1",
         "-y", "4000", "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(f"sqlcmd exited {result.returncode}:\n{result.stdout}{result.stderr}")

    return [line.rstrip() for line in result.stdout.splitlines() if line.strip()]


def escaped(token: str) -> str:
    """A token as a LIKE pattern expression, with LIKE's own wildcards neutralised.

    Every token here is literal text. '_' is a single-character wildcard in LIKE, so 'api_key' would
    otherwise also match 'apiXkey' -- harmless in this direction, but a pattern that matches more than
    it says is how a check acquires a false positive nobody can explain.

    A double quote is emitted as NCHAR (34) rather than as itself. Not a SQL requirement: the query
    travels to sqlcmd as a `-Q` argument, and a literal '"' inside it ends the argument as far as the
    Windows command line is concerned, so sqlcmd reports the remainder of the pattern as an unexpected
    flag. Concatenating the character keeps the whole query in one argument.
    """
    body = token.replace("'", "''").replace("[", "[[]").replace("_", "[_]").replace("%", "[%]")

    if '"' not in body:
        return f"N'%{body}%'"

    pieces = [f"N'{p}'" for p in body.split('"')]
    return "N'%' + " + " + NCHAR (34) + ".join(pieces) + " + N'%'"


def label(token: str) -> str:
    """A token, safe to embed in a SQL string literal that travels as a command-line argument.

    A double quote becomes '~' and a single quote is doubled. The label is only ever printed, so the
    substitution costs nothing and keeps the whole query in one argv slot.
    """
    return token.replace("'", "''").replace('"', "~")


def column_scan(table: str, key: str, column: str, declared: int | None) -> str:
    """A query returning one row per offending value in one column.

    Collation is forced to Latin1_General_100_CI_AS on the comparison so the match is case-insensitive
    regardless of what the database or the column was created with. A case-sensitive collation would
    make every token here match only its exact spelling, and a header arrives in whatever case the
    server sent it.

    `declared` is the column's declared character length, or None for NVARCHAR (MAX). It closes a hole
    the length test would otherwise have: only five of these fourteen columns are NVARCHAR (MAX), so on
    the other nine `LEN (col) > 4000` can never be true no matter what was assigned to them. What
    happens there instead is that the assignment SUCCEEDS and the value arrives clipped to the declared
    width, because these are all SET or INSERT ... SELECT assignments rather than a parameter bind. So a
    value at exactly the declared width is the observable evidence of an over-long one, and it is
    reported as truncation rather than as length -- the finding is not "this is too long" but "something
    longer than this was written and the rest is gone".
    """
    all_tokens = CREDENTIAL_TOKENS + PAYLOAD_TOKENS

    tokens = " OR ".join(
        f"{column} COLLATE Latin1_General_100_CI_AS LIKE {escaped(t)}" for t in all_tokens)

    # The reason is composed by the engine from the SAME predicates the WHERE clause uses. The first
    # version derived it in Python from the first sixteen characters of the value, which meant a row that
    # matched a token 300 characters in was reported as merely over the length limit -- a finding that
    # named the wrong problem, on a row that was not over the limit at all.
    # The label spells a double quote as '~' for the same command-line reason `escaped` documents: the
    # query reaches sqlcmd as one -Q argument, and a literal '"' anywhere in it ends that argument.
    reasons = " + ".join(
        f"CASE WHEN {column} COLLATE Latin1_General_100_CI_AS LIKE {escaped(t)} "
        f"THEN N'{label(t)} ' ELSE N'' END"
        for t in all_tokens)

    length_test = f"LEN ({column}) > {MAX_LENGTH}"
    length_label = f"CASE WHEN {length_test} THEN N'over-length ' ELSE N'' END"

    if declared is not None:
        length_test = f"({length_test} OR LEN ({column}) = {declared})"
        length_label = (
            f"CASE WHEN LEN ({column}) = {declared} "
            f"THEN N'exactly-the-declared-{declared}-characters(truncated) ' "
            f"ELSE {length_label} END")

    # LEFT (..., 16) is deliberate: the finding names the row and shows enough to locate it, and never
    # enough to reproduce the secret into a build log.
    return f"""
SET NOCOUNT ON;
SELECT N'{table}|{column}|'
     + CAST ({key} AS NVARCHAR (20)) + N'|'
     + CAST (LEN ({column}) AS NVARCHAR (20)) + N'|'
     + {length_label}
     + {reasons} + N'|'
     + REPLACE (REPLACE (LEFT ({column}, 16), NCHAR (13), N' '), NCHAR (10), N' ')
  FROM {table}
 WHERE {column} IS NOT NULL
   AND ( {length_test} OR {tokens} );
"""


def counted(table: str, columns: list[str]) -> str:
    """How many non-null values this table contributes to the scan."""
    total = " + ".join(f"COUNT ({c})" for c in columns)
    return f"SET NOCOUNT ON; SELECT CAST ({total} AS NVARCHAR (20)) FROM {table};"


def declared_widths(server: str) -> dict[str, int | None]:
    """`schema.table.column` -> declared character length, or None for NVARCHAR (MAX).

    Read from the catalog rather than from the DDL, because the DDL is what the file says and the catalog
    is what the server has -- and a column widened by a later ALTER would make a hand-written width
    report truncation on every row that reached the OLD limit.
    """
    rows = as_developer(server, """
SET NOCOUNT ON;
-- CAST to the character length here rather than in Python: sys.columns.max_length is in BYTES, so the
-- conversion depends on the type, and doing it in the query keeps that dependency in one place.
SELECT SCHEMA_NAME (t.schema_id) + N'.' + t.name + N'.' + c.name + N'|'
     + CAST (CASE WHEN c.max_length = -1 THEN -1
                  WHEN y.name = N'nvarchar' THEN c.max_length / 2
                  ELSE c.max_length
             END AS NVARCHAR (20))
  FROM sys.columns AS c
       INNER JOIN sys.tables AS t ON t.object_id = c.object_id
       INNER JOIN sys.types  AS y ON y.user_type_id = c.user_type_id
 WHERE SCHEMA_NAME (t.schema_id) = N'logs'
   AND y.name IN (N'nvarchar', N'varchar')
 ORDER BY 1;
""")

    widths: dict[str, int | None] = {}

    for row in rows:
        name, _, raw = row.partition("|")
        length = int(raw.strip())
        widths[name.strip()] = None if length == -1 else length

    return widths


def missing_columns(server: str) -> list[str]:
    """Free-text columns in the logs schema that this check does not scan.

    The list above is hand-written and the tables are not, so it drifts in one direction: a column added
    to logs.LoadRun next month is unscanned and nothing says so. This asks the catalog instead. NVARCHAR
    (MAX) and every NVARCHAR wide enough to hold a sentence count as free text; the audit*By columns and
    the narrow code columns do not.
    """
    known = ", ".join(
        [f"N'{table}.{column}'" for table, _, columns in SCANNED for column in columns]
        + [f"N'{name}'" for name in EXCLUDED])

    rows = as_developer(server, f"""
SET NOCOUNT ON;
SELECT SCHEMA_NAME (t.schema_id) + N'.' + t.name + N'.' + c.name
  FROM sys.columns AS c
       INNER JOIN sys.tables AS t ON t.object_id = c.object_id
       INNER JOIN sys.types  AS y ON y.user_type_id = c.user_type_id
 WHERE SCHEMA_NAME (t.schema_id) = N'logs'
   AND y.name IN (N'nvarchar', N'varchar')
   AND (c.max_length = -1 OR c.max_length >= 400)
   AND c.name NOT IN (N'auditCreatedBy', N'auditModifiedBy', N'auditDeletedBy')
   AND SCHEMA_NAME (t.schema_id) + N'.' + t.name + N'.' + c.name NOT IN ({known})
 ORDER BY 1;
""")

    return rows


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server", default=".", help="target SQL Server instance (default: .)")
    parser.add_argument("--self-test-only", action="store_true",
                        help="run the predicate fixture and stop; needs no server")
    args = parser.parse_args()

    findings = self_test()

    if args.self_test_only:
        for finding in findings:
            print(f"FAIL   {finding}")

        if findings:
            return 1

        print(f"PASS   {len(FIXTURE)} fixture value(s); the credential, payload and length predicates "
              f"all still catch what they are written to catch.")
        return 0

    if findings:
        # Stop here rather than scanning. A scan whose predicates are known broken would report a clean
        # database and be believed.
        for finding in findings:
            print(f"FAIL   {finding}")
        print(f"\n{len(findings)} finding(s) in the predicates themselves. The live scan was not run: "
              f"its result would be meaningless.")
        return 1

    try:
        unscanned = missing_columns(args.server)
        widths = declared_widths(args.server)
    except RuntimeError as exc:
        print(f"SETUP  check_execution_log_privacy could not read the catalog: {exc}")
        return 2

    unknown = [f"{table}.{column}"
               for table, _, columns in SCANNED
               for column in columns
               if f"{table}.{column}" not in widths]

    if unknown:
        # A column named here that the catalog does not have is a rename, and scanning the rest while
        # reporting a pass would say the renamed one was clean.
        print(f"SETUP  {', '.join(unknown)} is/are named in SCANNED but not present in the deployed "
              f"logs schema as a character column. The column was renamed, retyped, or never deployed.")
        return 2

    for column in unscanned:
        findings.append(
            f"{column} is a free-text column in the logs schema that this check does not scan. Every "
            f"table in this schema is readable by the monitoring web app, so an unscanned free-text "
            f"column is an unmeasured place for a credential to land. Add it to SCANNED, or if it "
            f"genuinely cannot hold caller-supplied text, exclude it by name with the reason.")

    scanned_values = 0

    for table, key, columns in SCANNED:
        try:
            count_rows = as_developer(args.server, counted(table, columns))
        except RuntimeError as exc:
            print(f"SETUP  check_execution_log_privacy could not read {table}: {exc}")
            return 2

        scanned_values += int(count_rows[0]) if count_rows and count_rows[0].isdigit() else 0

        for column in columns:
            try:
                hits = as_developer(
                    args.server,
                    column_scan(table, key, column, widths[f"{table}.{column}"]))
            except RuntimeError as exc:
                print(f"SETUP  check_execution_log_privacy could not scan {table}.{column}: {exc}")
                return 2

            for hit in hits:
                parts = hit.split("|", 5)
                if len(parts) != 6:
                    print(f"SETUP  unparseable scan row: {hit!r}")
                    return 2

                _, _, row_key, length, reason, head = (p.strip() for p in parts)

                findings.append(
                    f"{table}.{column} row {key}={row_key} holds {length} characters beginning "
                    f"{head!r} and matched: {reason or '(no token; over the length limit)'}. "
                    f"AR8: identifiers, dates and counts only. This table is readable by the "
                    f"monitoring web app. ('~' in a token stands for a double quote.)")

    for finding in findings:
        print(f"FAIL   {finding}")

    if findings:
        print(f"\n{len(findings)} finding(s) across {scanned_values} logged value(s).")
        return 1

    fixed = sum(1 for table, _, columns in SCANNED for c in columns
                if widths[f"{table}.{c}"] is not None)

    print(f"PASS   {len(FIXTURE)} fixture value(s) still classified correctly; {scanned_values} logged "
          f"value(s) across {sum(len(c) for _, _, c in SCANNED)} free-text column(s) "
          f"({fixed} of them fixed-width, checked for truncation as well as length) hold no credential, "
          f"no payload, and nothing over {MAX_LENGTH} characters.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
