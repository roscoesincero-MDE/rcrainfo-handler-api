#!/usr/bin/env python
"""Acceptance test: dbo.uspSoftDeleteHandlerSourceSet soft-deletes EVERY descendant of dbo.HandlerSource.

The soft-delete cascade in script 522 is a hand-written list of UPDATE statements, one per descendant
table. It has to be hand-written: script 050 denies the loader login metadata visibility, so the
procedure cannot walk sys.foreign_keys at runtime on the caller's behalf -- a catalog-driven cascade
would find nothing when the loader is the one calling.

A hand-written list rots. dbo.HandlerSource's descendants are GENERATED from the RCRAInfo Handler
schema by build/generate_schema.py, so the next collection EPA adds -- or the next one MDE decides to
model -- arrives as a new table with a new foreign key and no corresponding UPDATE. Nothing at runtime
would notice: the parent version would be soft-deleted, the new child's rows would stay IsDeleted = 0,
and because only dbo.vwHandlerSource and dbo.vwHandlerSourceHistory exist as views, any read of that
child filters its own IsDeleted and hands back rows belonging to a version nobody is allowed to see.
That is a disclosure of withdrawn data, and it would look exactly like working software.

So the list is derived here, from sys.foreign_keys, transitively, and compared against the file. Both
directions are checked:

  MISSING  a descendant table with no UPDATE in 522 -- rows of a deleted version stay readable.
  EXTRA    an UPDATE in 522 naming a table that is no longer a descendant -- a rename or a schema
           change left a statement behind, and a statement that still parses is not evidence that it
           still means anything.

It also asserts that dbo.HandlerOtherIdentifier is NOT a descendant. Script 522 deliberately excludes
it from the version cascade because it is keyed (HandlerId, ActivityLocation, OtherId) and holds no
HandlerSourceId -- an other-id belongs to the regulated entity, not to one version of its record, so
deleting one version must not remove it. If somebody later gives it a foreign key to dbo.HandlerSource
then that reasoning no longer holds and the exclusion has to be re-decided rather than inherited.

Matching is on `FROM <table> AS c`, the shape every cascade statement in 522 uses, not on the bare
table name: a name that appears only in a comment or in the MS_Description would otherwise satisfy
this check while deleting nothing.

Requires: sqlcmd on PATH, Windows authentication, and the deployment already applied (the check reads
the deployed foreign keys, which is where the truth is -- not the generator's input). Read-only.

Exit 0 when the cascade covers the descendant closure exactly, 1 on a finding, 2 on a setup problem.
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "src" / "RCRAInfo.Database" / "Scripts" \
    / "522_dbo.uspSoftDeleteHandlerSourceSet.sql"

ROOT_TABLE = "dbo.HandlerSource"

# Handler-grained and deliberately outside the version cascade. See the module docstring.
EXPECTED_NON_DESCENDANT = "dbo.HandlerOtherIdentifier"

# Descendants that must NOT be cascaded, and why. These reach dbo.HandlerSource through a real foreign
# key -- logs.HandlerLoadStatus.HandlerSourceId -- so the closure below finds them, and cascading them
# would be a defect rather than an omission.
#
# THE MIRROR IS CASCADED; THE AUDIT TRAIL IS NOT. A row in dbo.* asserts something about a regulated
# entity, and when EPA withdraws the version that assertion has to stop being readable. A row in logs.*
# asserts something about what our loader did: that on a given night it fetched this handler, got this
# HTTP status, wrote this version. Withdrawing the version does not make that false. It makes it
# ESSENTIAL -- the audit trail is how anyone answers "when did we have this data, and when did it go?",
# which is the question a withdrawal provokes. Soft-deleting the log alongside the data would destroy
# the evidence for the deletion at the moment of the deletion, and would also erase the
# DataQualityObservation rows that recorded why the version was suspect in the first place.
#
# A soft-deleted parent does not break referential integrity, so logs.HandlerLoadStatus.HandlerSourceId
# keeps pointing at the withdrawn version and stays joinable -- which is exactly what an investigation
# needs. It does mean a log read that joins dbo.HandlerSource must not assume the parent is live; the
# monitoring reads in 50x join logs.HandlerLoadStatus and do not require it.
#
# The list is explicit rather than a `schema != 'logs'` filter on purpose. A filter would silently
# absorb the NEXT log table somebody hangs off dbo.HandlerSource, and that one might genuinely need
# cascading. Adding a table here is a decision; inheriting one is not.
CASCADE_EXEMPT = {
    "logs.HandlerLoadStatus":
        "per-run record of what the loader did with this version -- the audit trail, which a "
        "withdrawal makes more important rather than less",
    "logs.HandlerLoadAttempt":
        "per-attempt HTTP outcome beneath HandlerLoadStatus; evidence about our own requests, not "
        "about the regulated entity",
    "logs.DataQualityObservation":
        "the findings that explain why a version was suspect or withdrawn -- deleting them with the "
        "version would erase the reason for the deletion",
}

# The transitive closure of "has a foreign key pointing at", starting from dbo.HandlerSource. Depth is
# carried so the report reads in the same order the procedure has to delete in -- grandchildren before
# children -- and MAX(Depth) rather than MIN, so a table reachable by two paths is reported at the
# deepest one, which is the one that constrains the ordering.
#
# Self-referencing keys are excluded or the recursion never terminates. There are none today; a future
# generated hierarchy would introduce one, and this check failing to return is a worse failure than
# this check reporting something.
QUERY = f"""
SET NOCOUNT ON;
WITH Edge AS
(
    SELECT Child      = SCHEMA_NAME (pt.schema_id) + N'.' + pt.name COLLATE DATABASE_DEFAULT
         , Referenced = SCHEMA_NAME (rt.schema_id) + N'.' + rt.name COLLATE DATABASE_DEFAULT
      FROM sys.foreign_keys AS fk
      JOIN sys.tables       AS pt ON pt.object_id = fk.parent_object_id
      JOIN sys.tables       AS rt ON rt.object_id = fk.referenced_object_id
     WHERE fk.parent_object_id <> fk.referenced_object_id
),
Descendant AS
(
    SELECT e.Child, Depth = 1
      FROM Edge AS e
     WHERE e.Referenced = N'{ROOT_TABLE}'
    UNION ALL
    SELECT e.Child, Depth = d.Depth + 1
      FROM Edge       AS e
      JOIN Descendant AS d ON e.Referenced = d.Child
     WHERE d.Depth < 10
)
SELECT CAST (MAX (Depth) AS NVARCHAR (11)) + N'|' + Child
  FROM Descendant
 GROUP BY Child
 ORDER BY MAX (Depth) DESC, Child;
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
        print(f"SETUP  check_cascade_coverage could not read the foreign keys: {exc}")
        return 2

    if not rows:
        # dbo.HandlerSource has nineteen descendants. Zero means the deployment has not run, or the
        # query hit the wrong database -- either way a check that examined nothing must not pass.
        print(f"SETUP  no foreign keys point at {ROOT_TABLE}. Has the deployment run against "
              f"RCRAInfo on server {args.server!r}?")
        return 2

    descendants: dict[str, int] = {}
    for row in rows:
        depth, _, table = row.partition("|")
        if not table or not depth.isdigit():
            print(f"SETUP  unparseable catalog row: {row!r}")
            return 2
        descendants[table] = int(depth)

    # What the file actually deletes from, as opposed to what it mentions.
    cascaded = set(re.findall(r"FROM\s+(\w+\.\w+)\s+AS\s+c\b", body))

    missing = sorted(t for t in descendants
                     if t not in cascaded and t not in CASCADE_EXEMPT)
    extra = sorted(t for t in cascaded if t not in descendants)
    wrongly_cascaded = sorted(t for t in cascaded if t in CASCADE_EXEMPT)

    failures = 0

    if wrongly_cascaded:
        failures += len(wrongly_cascaded)
        print(f"FAIL  {len(wrongly_cascaded)} table(s) are soft-deleted by {SCRIPT.name} that must "
              f"NOT be -- the cascade covers the mirror, never the audit trail:")
        for table in wrongly_cascaded:
            print(f"      - {table}: {CASCADE_EXEMPT[table]}")
        print("      Withdrawing a version does not make the record of our having fetched it false. "
              "Remove the UPDATE. If MDE has decided otherwise, remove the table from "
              "CASCADE_EXEMPT here and say why in 522's header first.")

    if missing:
        failures += len(missing)
        print(f"FAIL  {len(missing)} descendant table(s) of {ROOT_TABLE} are NOT soft-deleted by "
              f"{SCRIPT.name}, so rows belonging to a deleted version stay readable:")
        for table in missing:
            print(f"      - {table} (depth {descendants[table]})")
        print("      Add an UPDATE for each, in the same shape as the others: guarded by "
              "IsDeleted = 0, stamping the deleted pair AND the modified pair, joined to @Target "
              "for a child or to the resolved parent-key table variable for a grandchild. A "
              "grandchild needs its own key set resolved before the cascade starts.")

    if extra:
        failures += len(extra)
        print(f"FAIL  {len(extra)} table(s) are soft-deleted by {SCRIPT.name} but no longer reach "
              f"{ROOT_TABLE} through a foreign key:")
        for table in extra:
            print(f"      - {table}")
        print("      Either the table was renamed or its foreign key was removed. A statement that "
              "still parses is not evidence that it still means anything -- confirm what the table "
              "is now for, then remove the UPDATE or fix the key.")

    if EXPECTED_NON_DESCENDANT in descendants:
        failures += 1
        print(f"FAIL  {EXPECTED_NON_DESCENDANT} now has a foreign key reaching {ROOT_TABLE}, and "
              f"{SCRIPT.name} deliberately keeps it OUT of the version cascade.")
        print("      The exclusion rests on it being handler-grained -- keyed (HandlerId, "
              "ActivityLocation, OtherId), holding no HandlerSourceId, because an other-id belongs "
              "to the regulated entity rather than to one version of its record. A new key means "
              "that is no longer true, so the exclusion has to be re-decided with MDE rather than "
              "inherited: today the table is soft-deleted only when the handler has no live version "
              "left at all.")

    if failures:
        return 1

    exempt_found = sorted(t for t in descendants if t in CASCADE_EXEMPT)
    print(f"PASS  cascade coverage ({len(descendants) - len(exempt_found)} mirror descendant(s) of "
          f"{ROOT_TABLE}, all soft-deleted by {SCRIPT.name}; {len(exempt_found)} audit-trail "
          f"descendant(s) correctly left alone: {', '.join(exempt_found)}; "
          f"{EXPECTED_NON_DESCENDANT} correctly has no foreign key and stays out of the version "
          f"cascade)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
