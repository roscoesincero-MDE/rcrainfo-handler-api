#!/usr/bin/env python
"""Acceptance test: every deployed module carries its header block IN THE DATABASE.

MDE's review finding, 2026-09-05: "The view objects have a header that includes description,
modification history, etc. The procedures, however, do not" -- and the procedures' own comments
referred to that header ("See the header note"), so a maintainer was pointed at documentation that
was not there.

The cause was not a missing header. Every procedure file had one. sys.sql_modules stores only the
batch that contains CREATE, and each procedure carried a script-level `SET XACT_ABORT ON` followed by
`GO` placed BELOW the header -- so the header ended up in its own batch, present in the file and
absent from the database. Measured before the fix: 11 procedures HeaderPresent = no, 2 views YES. The
views were never at risk because nothing sits between their header and their CREATE.

WHY THIS EXISTS ALONGSIDE THE FILE-LEVEL RULE. .claude/hooks/validate-sql.py rejects a GO between a
header and its CREATE, which prevents the specific cause. This checks the EFFECT, against the
deployed catalog, which is where MDE looked and is the only place the answer is authoritative. The
two are not redundant: the file rule cannot see an object deployed from an older copy of a script,
one applied by hand in a query window, or one whose header is stripped by some future step between
the file and the server. An object with no stored header is invisible to sp_helptext,
OBJECT_DEFINITION and SSMS "Script as CREATE", which is where a maintainer actually reads one --
nobody opens the deployment script to find out what a procedure does.

It also re-checks the AR8 CATCH from the deployed side, for the same reason and against the same
review finding: MDE's other application had a read-only procedure whose SELECT called a UDF, the UDF
errored, and nothing was recorded. A procedure in the catalog with no CATCH is that defect, whatever
the file says.

Requires: sqlcmd on PATH, Windows authentication, and the deployment already applied. Read-only --
it creates nothing and changes nothing, so unlike check_run_twice it is safe to run at any time.

Since 2026-09-05 it also checks that a procedure rolls back only what it opened. That rule came from a
probe rather than a review: both paged reads opened a transaction "for template fidelity" and carried
a writer's `IF XACT_STATE () <> 0 ROLLBACK TRANSACTION;`. XACT_STATE () <> 0 is also true when the
CALLER owns the transaction, and every validation refusal throws before the BEGIN TRANSACTION -- so on
the likeliest failure the read rolled back somebody else's work. Worse, ROLLBACK is illegal inside
INSERT ... EXEC: it raised error 8004, replaced the error being reported, and aborted the CATCH before
uspRecordExecutionError could run. A procedure could therefore pass the CATCH rule above and still
record nothing, which is why this is checked here and not left to the file rule alone.

Exit 0 when every module has its header, every non-exempt procedure records its errors, and no
procedure rolls back a transaction it did not open; 1 on a finding, 2 on a setup problem.
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys

# The five AR8 exemptions from the error-recording rule, and the only ones. The four logging
# procedures cannot call the logging procedures -- uspRecordExecutionError calling itself from its own
# CATCH is an unbounded recursion on the exact failure it exists to survive -- and
# util.uspSetObjectDescription runs in script 030, before logs.ExecutionLog exists. This list is
# duplicated in .claude/hooks/validate-sql.py on purpose: the two checks answer the same question from
# opposite sides, and a shared import would let one edit silently move both.
INSTRUMENTATION_EXEMPT = {
    "logs.uspStartExecutionLoggingInsert",
    "logs.uspStartExecutionLogging",
    "logs.uspRecordExecutionErrorUpdate",
    "logs.uspRecordExecutionError",
    "util.uspSetObjectDescription",
}

# Both header fields are required, not either: a definition can contain "ObjectName:" because some
# other comment mentions it. Requiring the first and last field of the block means the whole block
# survived into the batch, which is the thing being measured.
#
# The rollback-ownership rule cannot be asked as a LIKE and is not in this query. OBJECT_DEFINITION
# returns every comment, and the two procedures that HAD the defect now discuss both keywords at
# length in their headers -- so a pattern matching the bare words is satisfied by the paragraph
# explaining why the statement is absent, which is documentation disarming its own gate. Requiring a
# trailing semicolon looked like enough and was not: it survived one edit before a comment reading
# "there was no BEGIN TRANSACTION; a client that reads rows slowly ..." used the semicolon as
# punctuation and turned the check green again. So the definitions are fetched whole and the comments
# are removed properly, below.
#
# LIKE, not a regex: SQL Server 2022 has no regex functions (CLAUDE.md), and this query has to run on
# the target platform, not just on the 2025 workstation it was written against.
QUERY = """
SET NOCOUNT ON;
SELECT SCHEMA_NAME (o.schema_id) + N'.' + o.name COLLATE DATABASE_DEFAULT
     + N'|' + o.type_desc COLLATE DATABASE_DEFAULT
     + N'|' + CASE WHEN OBJECT_DEFINITION (o.object_id) LIKE N'%ObjectName:%'
                    AND OBJECT_DEFINITION (o.object_id) LIKE N'%Modification History:%'
                   THEN N'header' ELSE N'NO-HEADER' END
     + N'|' + CASE WHEN OBJECT_DEFINITION (o.object_id) LIKE N'%BEGIN TRY%'
                    AND OBJECT_DEFINITION (o.object_id) LIKE N'%uspRecordExecutionError%'
                   THEN N'records' ELSE N'NO-CATCH' END
  FROM sys.objects AS o
 WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF')
   AND o.is_ms_shipped = 0
 ORDER BY SCHEMA_NAME (o.schema_id), o.name;
"""


# One row per procedure would need a width flag sqlcmd will not combine with -h -1, so the whole
# catalog comes back as a single nvarchar(max) value split on a delimiter no T-SQL body contains.
DELIMITER = "~~~RCRAINFO-OBJECT~~~"

DEFINITIONS = f"""
SET NOCOUNT ON;
SELECT STRING_AGG (CAST (N'{DELIMITER}'
                       + SCHEMA_NAME (o.schema_id) + N'.' + o.name + CHAR (10)
                       + OBJECT_DEFINITION (o.object_id) AS NVARCHAR (MAX)), CHAR (10))
  FROM sys.objects AS o
 WHERE o.type = 'P'
   AND o.is_ms_shipped = 0;
"""


def strip_comments(sql: str) -> str:
    """Blank out block comments, line comments and string literals, preserving length.

    A near-copy of strip_strings_and_comments in .claude/hooks/validate-sql.py, and duplicated for
    the same reason INSTRUMENTATION_EXEMPT is: these two checks answer one question from opposite
    sides, and a shared import would let one edit move both at once. The nesting counter is the part
    that matters -- T-SQL block comments NEST, so the obvious regex is wrong on exactly the file that
    needs it.
    """
    out = list(sql)
    i, n = 0, len(sql)
    while i < n:
        two = sql[i:i + 2]
        if two == "/*":
            depth, j = 1, i + 2
            while j < n and depth:
                if sql[j:j + 2] == "/*":
                    depth, j = depth + 1, j + 2
                elif sql[j:j + 2] == "*/":
                    depth, j = depth - 1, j + 2
                else:
                    j += 1
            for k in range(i, min(j, n)):
                if out[k] != "\n":
                    out[k] = " "
            i = j
        elif two == "--":
            j = sql.find("\n", i)
            j = n if j == -1 else j
            for k in range(i, j):
                out[k] = " "
            i = j
        elif sql[i] == "'":
            j = i + 1
            while j < n:
                if sql[j] == "'" and sql[j:j + 2] != "''":
                    j += 1
                    break
                j += 2 if sql[j:j + 2] == "''" else 1
            for k in range(i, min(j, n)):
                if out[k] != "\n":
                    out[k] = " "
            i = j
        else:
            i += 1
    return "".join(out)


def unowned_rollbacks(text: str) -> tuple[list[str], int]:
    """Procedures whose EXECUTABLE body rolls back a transaction it never opened.

    Ownership, not "reads must not roll back": a procedure that opens its own transaction still has to
    roll it back, and all of the writers do. Only the pairing is checked, because whether a given
    ROLLBACK belongs to this procedure is not decidable from a keyword scan, and the pairing is what
    was actually wrong.
    """
    found: list[str] = []
    chunks = [c for c in text.split(DELIMITER) if c.strip()]
    for chunk in chunks:
        name, _, body = chunk.partition("\n")
        body = strip_comments(body)
        if re.search(r"\bROLLBACK\b", body, re.I) and not re.search(
                r"\bBEGIN\s+TRAN(SACTION)?\b", body, re.I):
            found.append(name.strip())
    return found, len(chunks)


def as_developer(server: str, query: str) -> list[str]:
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    # -C trusts the server certificate, which this workstation's default instance needs; without it
    # sqlcmd stops with "the certificate chain was issued by an authority that is not trusted".
    result = subprocess.run(
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", "RCRAInfo", "-h", "-1", "-W",
         "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(f"sqlcmd exited {result.returncode}:\n{result.stdout}{result.stderr}")

    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def raw_text(server: str, query: str) -> str:
    """The same connection, but for one nvarchar(max) value that must arrive intact.

    -y 0 rather than the -h -1 -W of as_developer, because module definitions run to hundreds of lines
    and sqlcmd's default display width of 256 would cut every one of them -- and a truncated body is
    what makes a keyword scan a confident wrong answer. -y 0 will not combine with -h -1; with no
    column name and NOCOUNT ON there is no header row to suppress, so nothing is lost by dropping it.
    """
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    result = subprocess.run(
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", "RCRAInfo", "-y", "0", "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(f"sqlcmd exited {result.returncode}:\n{result.stdout}{result.stderr}")

    return result.stdout


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server", default=".", help="target SQL Server instance (default: .)")
    args = parser.parse_args()

    try:
        rows = as_developer(args.server, QUERY)
        definitions = raw_text(args.server, DEFINITIONS)
    except RuntimeError as exc:
        print(f"SETUP  check_stored_headers could not query the catalog: {exc}")
        return 2

    orphan_rollback, procedures = unowned_rollbacks(definitions)
    if not procedures:
        print("SETUP  no procedure definitions came back, so the rollback-ownership rule examined "
              "nothing. A truncated or empty result here would otherwise report as a clean pass.")
        return 2

    if not rows:
        # A check that examined nothing must not report success -- that turns a wrong -Q, an empty
        # database, or a deployment that never ran into a green build.
        print("SETUP  no views, procedures or functions found in RCRAInfo. Has the deployment run?")
        return 2

    headerless: list[str] = []
    uninstrumented: list[str] = []
    modules = 0

    for row in rows:
        parts = row.split("|")
        if len(parts) != 4:
            print(f"SETUP  unparseable catalog row: {row!r}")
            return 2

        name, type_desc, header, records = parts
        modules += 1

        if header == "NO-HEADER":
            headerless.append(f"{name} ({type_desc})")

        if (type_desc == "SQL_STORED_PROCEDURE"
                and name not in INSTRUMENTATION_EXEMPT
                and records == "NO-CATCH"):
            uninstrumented.append(name)

    if headerless:
        print(f"FAIL  {len(headerless)} of {modules} deployed module(s) have NO header block in "
              f"sys.sql_modules, so sp_helptext and SSMS show them undocumented:")
        for item in headerless:
            print(f"      - {item}")
        print("      Almost always a GO between the header and the CREATE: sys.sql_modules stores "
              "only the batch that contains CREATE. Move SET XACT_ABORT ON / GO ABOVE the header, "
              "then redeploy -- fixing the file is not enough, the object in the database still "
              "holds the old definition.")

    if uninstrumented:
        print(f"FAIL  {len(uninstrumented)} deployed procedure(s) have no CATCH calling "
              f"logs.uspRecordExecutionError, so an error in them is recorded nowhere:")
        for item in uninstrumented:
            print(f"      - {item}")
        print("      A read-only procedure is not exempt. MDE's review found one whose body was a "
              "single SELECT calling a UDF; the UDF errored and nothing was written. Pass "
              "@ExecutionLogId = NULL and the MERGE writes an orphan row on purpose.")

    if orphan_rollback:
        print(f"FAIL  {len(orphan_rollback)} deployed procedure(s) roll back a transaction they never "
              f"opened -- a ROLLBACK TRANSACTION with no BEGIN TRANSACTION:")
        for item in orphan_rollback:
            print(f"      - {item}")
        print("      XACT_STATE () <> 0 is also true when the CALLER owns the transaction, so this "
              "discards work the procedure never did; and inside INSERT ... EXEC the ROLLBACK itself "
              "raises error 8004, replacing the error being reported and aborting the CATCH before "
              "logs.uspRecordExecutionError runs -- so the procedure satisfies the rule above and "
              "still records nothing. A read needs neither a transaction nor a rollback.")

    if headerless or uninstrumented or orphan_rollback:
        return 1

    print(f"PASS  stored headers ({modules} deployed module(s) all carry their header block in "
          f"sys.sql_modules, every non-exempt procedure records its own errors, and none of the "
          f"{procedures} procedure(s) rolls back a transaction it did not open)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
