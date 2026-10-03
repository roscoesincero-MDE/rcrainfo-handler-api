#!/usr/bin/env python
"""Acceptance test: dbo.uspRefreshLookupSet handles EVERY mirrored lookup table, with its real shape.

Script 523 is a hand-written dispatch: 23 branches, one per EPA response definition, each with a static
MERGE naming its own table and its own columns. It has to be hand-written twice over. A static MERGE
cannot parameterise a table name, and script 050 denies the loader login metadata visibility, so the
procedure cannot read sys.columns at runtime to discover a shape on the caller's behalf either.

The 24 dbo.Lookup* tables, by contrast, are GENERATED from the pinned EPA swagger by
build/generate_schema.py. So the two halves drift in exactly one direction and entirely silently: EPA
adds a code list or a column, the generator mirrors it, and script 523 has no branch for it. Nothing
at runtime notices -- the new table sits at zero rows and every read of it returns nothing, which looks
like "EPA publishes no codes for that list" rather than "this mirror is never refreshed". The one thing
that would notice is the drift check below.

Four things are derived from the catalog and compared against the file:

  BRANCH     every dbo.Lookup* table is merged by 523, and 523 merges nothing that is not one.
  COLUMNS    every writable column of every lookup table is named in that table's MERGE. A generator
             that widens a list by one column would otherwise leave the column NULL forever, and a
             NULL description reads as "EPA has no name for this code".
  RETIREMENT every lookup table has a retirement UPDATE. A table that is merged but never retired
             accumulates codes EPA has withdrawn and keeps handing them back as current.
  DISPATCH   the @CodeWidth table matches the real Code column widths, and the @HasActivityLocation
             list matches which tables really have that column. These are the two facts 523's single
             set of generic validations reads, so a stale entry there is a width check that passes on
             a value the column cannot hold, or a natural-key check that does not require half the key.

Matching is on the shapes the procedure actually uses -- `MERGE dbo.X WITH (HOLDLOCK) AS tgt` and
`FROM dbo.X AS r` -- rather than on the bare table name, so a name appearing only in a comment or in
the MS_Description cannot satisfy any of it.

Requires: sqlcmd on PATH, Windows authentication, and the deployment already applied. Read-only.

Exit 0 when the procedure covers the mirrored lists exactly, 1 on a finding, 2 on a setup problem.
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "src" / "RCRAInfo.Database" / "Scripts" \
    / "523_dbo.uspRefreshLookupSet.sql"

# The one lookup table that is not a branch of its own. EPA serves it as StateDistrict.counties, a
# nested array, so it is merged inside the StateDistrict branch and has no @LookupName of its own --
# there is no /lookup/hd endpoint that returns it alone. Mapped rather than skipped, so that "handled"
# still means something for it: the StateDistrict branch must merge and retire it.
CHILD_OF = {"dbo.LookupStateDistrictCounty": "StateDistrict"}

# Columns every table has and 523 handles uniformly rather than per-branch, so requiring them in each
# MERGE would be noise. IsDeleted IS set by every branch (to 0 on a revive, to 1 on a retirement), but
# it is set the same way in all of them and is checked by the validator hook, not here.
UNIFORM = {"IsDeleted"}

# Table, column, and how many characters the column holds (NULL for non-string types). The identity
# column is excluded here rather than in Python: it is never named in an INSERT, by design.
QUERY = """
SET NOCOUNT ON;
SELECT SCHEMA_NAME (t.schema_id) + N'.' + t.name
     + N'|' + c.name
     + N'|' + CASE WHEN ty.name LIKE N'%char%' THEN CAST (c.max_length / 2 AS NVARCHAR (11))
                   ELSE N'' END
  FROM sys.tables  AS t
  JOIN sys.columns AS c  ON c.object_id = t.object_id
  JOIN sys.types   AS ty ON ty.user_type_id = c.user_type_id
 WHERE t.name LIKE N'Lookup%'
   AND SCHEMA_NAME (t.schema_id) = N'dbo'
   AND c.is_identity = 0
   AND c.name NOT LIKE N'audit%'
 ORDER BY t.name, c.column_id;
"""


def as_developer(server: str, query: str) -> list[str]:
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    # -C trusts the server certificate, which this workstation's default instance needs.
    result = subprocess.run(
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", "RCRAInfo", "-h", "-1", "-W",
         "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(f"sqlcmd exited {result.returncode}:\n{result.stdout}{result.stderr}")

    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def merge_blocks(body: str) -> dict[str, str]:
    """The text of each MERGE statement in 523, keyed by the table it writes.

    Sliced to the @@ROWCOUNT capture that follows it rather than to the next MERGE, so the retirement
    UPDATE below it is NOT included -- otherwise a key column named only by the retirement would
    satisfy the column check for a MERGE that had dropped it.
    """
    blocks: dict[str, str] = {}
    pattern = re.compile(r"MERGE\s+(dbo\.\w+)\s+WITH\s+\(HOLDLOCK\)\s+AS\s+tgt")
    end = re.compile(r"SET\s+@(?:Child)?Written\s*=\s*@@ROWCOUNT;")

    for match in pattern.finditer(body):
        stop = end.search(body, match.end())
        blocks[match.group(1)] = body[match.end():stop.start() if stop else len(body)]

    return blocks


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server", default=".", help="target SQL Server instance (default: .)")
    args = parser.parse_args()

    if not SCRIPT.is_file():
        print(f"SETUP  {SCRIPT} not found.")
        return 2

    body = SCRIPT.read_text(encoding="utf-8")

    try:
        rows = as_developer(args.server, QUERY)
    except RuntimeError as exc:
        print(f"SETUP  check_lookup_coverage could not read the lookup columns: {exc}")
        return 2

    if not rows:
        # There are 24 mirrored lookup tables. Zero means the deployment has not run, or the query hit
        # the wrong database -- either way a check that examined nothing must not pass.
        print(f"SETUP  no dbo.Lookup* tables found. Has the deployment run against RCRAInfo on "
              f"server {args.server!r}?")
        return 2

    columns: dict[str, list[str]] = {}
    code_width: dict[str, int] = {}
    for row in rows:
        parts = row.split("|")
        if len(parts) != 3:
            print(f"SETUP  unparseable catalog row: {row!r}")
            return 2
        table, column, width = parts
        columns.setdefault(table, []).append(column)
        if column == "Code" and width:
            code_width[table] = int(width)

    tables = sorted(columns)
    blocks = merge_blocks(body)
    retired = set(re.findall(r"FROM\s+(dbo\.\w+)\s+AS\s+r\b", body))

    # The branch list, the dispatch widths, and the seven names declared to have no ActivityLocation.
    branches = set(re.findall(r"@LookupName\s*=\s*N'(\w+)'", body))

    # The widths are read only from inside the @CodeWidth assignment, not from the whole file. The
    # obvious file-wide `WHEN N'x' THEN <n>` sweep also matches the JSON-boolean mapping's
    # `WHEN N'true' THEN 1`, which the procedure uses six times -- and 'true' would then be reported
    # as a code list with no branch, which is a confident, specific, wrong finding.
    width_clause = re.search(r"@CodeWidth\s*=\s*CASE\s+@LookupName(.*?)\bEND\b", body, re.DOTALL)
    if width_clause is None:
        print("SETUP  could not find the @CodeWidth dispatch in 523. It is the only place the "
              "procedure learns how wide a list's Code column is, and the width check every branch "
              "relies on reads it, so this check cannot pass without seeing it.")
        return 2
    dispatch = {name: int(width)
                for name, width in re.findall(r"WHEN\s+N'(\w+)'\s+THEN\s+(\d+)",
                                              width_clause.group(1))}

    no_location: set[str] = set()
    location_clause = re.search(r"@HasActivityLocation\s*=\s*CASE\s+WHEN\s+@LookupName\s+IN\s*\((.*?)\)",
                                body, re.DOTALL)
    if location_clause is None:
        print("SETUP  could not find the @HasActivityLocation dispatch in 523. It is what every "
              "generic validation in the procedure reads to decide whether a list has an "
              "activityLocation, so this check cannot pass without seeing it.")
        return 2
    no_location = set(re.findall(r"N'(\w+)'", location_clause.group(1)))

    failures = 0

    # ---- BRANCH ---------------------------------------------------------------------------------
    unmerged = [t for t in tables if t not in blocks]
    if unmerged:
        failures += len(unmerged)
        print(f"FAIL  {len(unmerged)} mirrored lookup table(s) are never merged by {SCRIPT.name}, so "
              f"nothing ever refreshes them and they read as empty code lists:")
        for table in unmerged:
            print(f"      - {table} ({', '.join(columns[table])})")
        print("      Add a branch in alphabetical order, in the same three parts as the others: an "
              "unfiltered MERGE under HOLDLOCK whose MATCHED arm is guarded by IS DISTINCT FROM on "
              "every column it writes, a retirement UPDATE guarded by @Mode = 'Full' and scoped to "
              "@Location, and the @@ROWCOUNT capture. Add the name and its Code width to the "
              "@CodeWidth dispatch too, or the branch is unreachable.")

    not_a_lookup = sorted(t for t in blocks if t not in columns)
    if not_a_lookup:
        failures += len(not_a_lookup)
        print(f"FAIL  {len(not_a_lookup)} table(s) are merged by {SCRIPT.name} but are not mirrored "
              f"lookup tables:")
        for table in not_a_lookup:
            print(f"      - {table}")
        print("      Either the table was renamed or this procedure has grown a responsibility that "
              "is not refreshing an EPA code list. A statement that still parses is not evidence "
              "that it still means anything.")

    # ---- COLUMNS --------------------------------------------------------------------------------
    for table in tables:
        block = blocks.get(table)
        if block is None:
            continue  # already reported as unmerged
        absent = [c for c in columns[table] if c not in UNIFORM and c not in block]
        if absent:
            failures += 1
            print(f"FAIL  {table} is merged by {SCRIPT.name} but {len(absent)} of its column(s) are "
                  f"never written: {', '.join(absent)}")
            print("      The generator added them to the table; the branch was not extended to "
                  "match, so they stay NULL for every code in the list forever -- and a NULL "
                  "description reads as EPA having no name for the code rather than as a gap in the "
                  "mirror. Add each one to the USING projection, the MATCHED guard, the UPDATE SET "
                  "and the INSERT.")

    # ---- RETIREMENT -----------------------------------------------------------------------------
    never_retired = [t for t in tables if t in blocks and t not in retired]
    if never_retired:
        failures += len(never_retired)
        print(f"FAIL  {len(never_retired)} lookup table(s) are merged by {SCRIPT.name} but have no "
              f"retirement UPDATE:")
        for table in never_retired:
            print(f"      - {table}")
        print("      A list that is merged but never retired accumulates codes EPA has withdrawn and "
              "keeps handing them back as current. Retirement is a soft delete -- a retired code "
              "still has to resolve for the handler versions submitted while it was current.")

    # ---- DISPATCH -------------------------------------------------------------------------------
    # Every branch must be reachable, and every dispatch entry must lead to a branch.
    unreachable = sorted(b for b in branches if b not in dispatch)
    if unreachable:
        failures += len(unreachable)
        print(f"FAIL  {len(unreachable)} branch name(s) in {SCRIPT.name} are not in the @CodeWidth "
              f"dispatch, so validation rejects them before the branch can run: "
              f"{', '.join(unreachable)}")

    orphan_dispatch = sorted(d for d in dispatch if d not in branches)
    if orphan_dispatch:
        failures += len(orphan_dispatch)
        print(f"FAIL  {len(orphan_dispatch)} name(s) in the @CodeWidth dispatch have no branch, so a "
              f"call naming one passes validation, writes nothing, and would report success were it "
              f"not for the final ELSE: {', '.join(orphan_dispatch)}")

    # The widths, against the real columns. This is the check that catches a generated column growing.
    for name, width in sorted(dispatch.items()):
        table = f"dbo.Lookup{name}"
        actual = code_width.get(table)
        if actual is None:
            failures += 1
            print(f"FAIL  the @CodeWidth dispatch names {name}, but {table} has no NVARCHAR Code "
                  f"column. The dispatch is keyed on EPA's response definition names and each one "
                  f"must correspond to a mirrored table.")
        elif actual != width:
            failures += 1
            print(f"FAIL  the @CodeWidth dispatch says {name} is {width} character(s), but "
                  f"{table}.Code holds {actual}.")
            print(f"      Too small rejects codes the column can store; too large lets an over-wide "
                  f"code through the width check and then truncates it on the CAST in the MERGE -- "
                  f"and a truncated code is a DIFFERENT code, in the table whose only job is to "
                  f"resolve codes.")

    # And which lists really have an ActivityLocation. 523 reads this to decide whether the natural
    # key needs one and whether the retirement is scoped -- both silently wrong if it is stale.
    truly_absent = {name for name in dispatch
                    if "ActivityLocation" not in columns.get(f"dbo.Lookup{name}", [])}
    if no_location != truly_absent:
        for name in sorted(no_location - truly_absent):
            failures += 1
            print(f"FAIL  523 declares {name} as having no ActivityLocation, but "
                  f"dbo.Lookup{name}.ActivityLocation exists. The natural-key check would not "
                  f"require it, so a payload omitting it would be accepted and then fail on the NOT "
                  f"NULL column -- or worse, the retirement would not be scoped and a refresh of one "
                  f"jurisdiction would retire another's codes.")
        for name in sorted(truly_absent - no_location):
            failures += 1
            print(f"FAIL  523 treats {name} as having an ActivityLocation, but "
                  f"dbo.Lookup{name} has no such column. The @Location join in its retirement cannot "
                  f"compile against a column that is not there.")

    if failures:
        return 1

    child_note = ", ".join(f"{t.split('.')[1]} inside the {parent} branch"
                          for t, parent in sorted(CHILD_OF.items()))
    total_columns = sum(len(c) for c in columns.values())
    print(f"PASS  lookup coverage ({len(tables)} mirrored lookup table(s), all merged and retired by "
          f"{SCRIPT.name}; {len(dispatch)} EPA response definition(s) dispatched, every Code width "
          f"and every ActivityLocation agreeing with sys.columns; {total_columns} writable column(s) "
          f"all written; {child_note})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
