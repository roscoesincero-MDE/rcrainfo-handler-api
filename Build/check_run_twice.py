#!/usr/bin/env python
"""
A3/[R8] acceptance test: run the whole deployment twice and prove the second run changed nothing.

The developer runs the DDL scripts by hand (G3), which means they get run twice -- after an
interruption, after a merge, after someone is unsure whether they already ran them. Every script is
written to converge, and "written to converge" is a claim, not a fact. This measures it.

What "changed nothing" has to mean, and why each part is here:

  sys.objects            no object created, dropped, or replaced with a new object_id. A DROP/CREATE
                         instead of CREATE OR ALTER shows up here and nowhere else, and it silently
                         discards every grant and extended property on the old object.
  sys.columns            no column added, retyped, or renamed.
  sys.extended_properties  descriptions identical. sp_addextendedproperty errors on a second run;
                         the helper that replaces it must be genuinely idempotent, not merely quiet.
  sys.database_permissions  the DENY posture is byte-identical. A permission re-granted is invisible
                         in every other view here.
  audit* on seeded rows  and this is the one that is easy to miss: a re-run that UPDATEs a row to
                         the value it already holds moves auditModifiedDateUtc. Nothing is broken,
                         no error is raised, and the audit trail now says the data changed today.
                         That is a converging script telling a lie, so the timestamps are compared
                         exactly, not just the data.

The catalog snapshot is taken as SORTED, NORMALISED text so the diff is readable: the failure output
names the object that moved rather than reporting that two large result sets differ.

Requires: sqlcmd on PATH, Windows authentication with rights to run the deployment, and the
deployment already applied once (run Deploy-Database.ps1 first). This does NOT create the database.

Exit 0 when the second run is a no-op, 1 otherwise, 2 on a setup problem.
"""

from __future__ import annotations

import argparse
import difflib
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DEPLOY = REPO / "src" / "RCRAInfo.Database" / "Deployment" / "Deploy-Database.ps1"

# Matched exactly, not as "starts with a bracket": an extended-property description that happens to
# begin with a parenthesis would otherwise be dropped from the snapshot and never compared.
ROWS_AFFECTED = re.compile(r"^\(\d+ rows? affected\)$")

# Each probe is (label, query). Two things every query here has to do:
#
#   ORDER BY its own output, because without it the engine is free to return the same rows in a
#   different sequence, and the diff then reports a finding that is not one.
#
#   COLLATE DATABASE_DEFAULT on every sysname and *_desc column it concatenates. Catalog metadata
#   columns are Latin1_General_CI_AS_KS_WS regardless of the database collation, which is
#   SQL_Latin1_General_CP1_CI_AS here, and mixing the two in a + raises "cannot resolve collation
#   conflict" -- at run time, on a query that looks obviously correct.
PROBES = [
    ("objects", """
     SELECT CAST(o.object_id AS NVARCHAR(20)) + N'|'
          + s.name COLLATE DATABASE_DEFAULT + N'.' + o.name COLLATE DATABASE_DEFAULT + N'|'
          + o.type_desc COLLATE DATABASE_DEFAULT
       FROM sys.objects AS o
       JOIN sys.schemas AS s ON s.schema_id = o.schema_id
      WHERE o.is_ms_shipped = 0
      ORDER BY s.name, o.name, o.object_id;
     """),

    ("columns", """
     SELECT s.name COLLATE DATABASE_DEFAULT + N'.' + o.name COLLATE DATABASE_DEFAULT + N'.'
          + c.name COLLATE DATABASE_DEFAULT + N'|'
          + t.name COLLATE DATABASE_DEFAULT + N'(' + CAST(c.max_length AS NVARCHAR(10)) + N','
          + CAST(c.precision AS NVARCHAR(10)) + N',' + CAST(c.scale AS NVARCHAR(10)) + N')|'
          + CAST(c.is_nullable AS NVARCHAR(1)) + N'|'
          + ISNULL(dc.definition COLLATE DATABASE_DEFAULT, N'')
       FROM sys.columns AS c
       JOIN sys.objects AS o ON o.object_id = c.object_id
       JOIN sys.schemas AS s ON s.schema_id = o.schema_id
       JOIN sys.types   AS t ON t.user_type_id = c.user_type_id
       LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
      WHERE o.is_ms_shipped = 0
      ORDER BY s.name, o.name, c.name;
     """),

    # Indexes are NOT covered by the "objects" probe: sys.objects holds the PRIMARY KEY and FOREIGN
    # KEY constraints but not a nonclustered index, so before this probe existed a deployment could
    # add 19 indexes and the check would still report that nothing had changed. It was doing exactly
    # that when the Workstream B schema landed.
    #
    # data_compression_desc is included on purpose. A nonclustered index does not inherit the table's
    # compression, so the scripts fix it with a guarded ALTER INDEX ... REBUILD. If that guard were
    # ever wrong, every deployment would rebuild every index forever -- costly, invisible, and
    # precisely the non-convergence [R8] exists to catch.
    ("indexes", """
     SELECT s.name COLLATE DATABASE_DEFAULT + N'.' + t.name COLLATE DATABASE_DEFAULT + N'|'
          + i.name COLLATE DATABASE_DEFAULT + N'|'
          + i.type_desc COLLATE DATABASE_DEFAULT + N'|'
          + CAST(i.is_unique AS NVARCHAR(1)) + N'|'
          + ISNULL(i.filter_definition COLLATE DATABASE_DEFAULT, N'(unfiltered)') + N'|'
          + STUFF ((SELECT N',' + c.name COLLATE DATABASE_DEFAULT
                      FROM sys.index_columns AS ic
                      JOIN sys.columns AS c ON c.object_id = ic.object_id
                                           AND c.column_id = ic.column_id
                     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                     ORDER BY ic.key_ordinal, ic.index_column_id
                       FOR XML PATH (''), TYPE).value(N'.', N'NVARCHAR(MAX)'), 1, 1, N'') + N'|'
          + ISNULL (MAX (p.data_compression_desc) COLLATE DATABASE_DEFAULT, N'?')
       FROM sys.indexes AS i
       JOIN sys.tables  AS t ON t.object_id = i.object_id AND t.is_ms_shipped = 0
       JOIN sys.schemas AS s ON s.schema_id = t.schema_id
       LEFT JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id
      WHERE i.name IS NOT NULL
      GROUP BY s.name, t.name, i.name, i.type_desc, i.is_unique, i.filter_definition,
               i.object_id, i.index_id
      ORDER BY s.name, t.name, i.name;
     """),

    ("descriptions", """
     SELECT ISNULL(s.name COLLATE DATABASE_DEFAULT, N'(db)') + N'.'
          + ISNULL(o.name COLLATE DATABASE_DEFAULT, N'') + N'.'
          + ISNULL(c.name COLLATE DATABASE_DEFAULT, N'')
          + N'|' + ep.name COLLATE DATABASE_DEFAULT
          + N'|' + CAST(ep.value AS NVARCHAR(MAX)) COLLATE DATABASE_DEFAULT
       FROM sys.extended_properties AS ep
       LEFT JOIN sys.objects AS o ON o.object_id = ep.major_id AND ep.class = 1
       LEFT JOIN sys.schemas AS s ON s.schema_id = o.schema_id
       LEFT JOIN sys.columns AS c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
                                     AND ep.class = 1 AND ep.minor_id > 0
      ORDER BY 1;
     """),

    ("permissions", """
     SELECT dp.class_desc COLLATE DATABASE_DEFAULT + N'|'
          + ISNULL(s.name COLLATE DATABASE_DEFAULT, N'') + N'|'
          + ISNULL(o.name COLLATE DATABASE_DEFAULT, N'') + N'|'
          + pr.name COLLATE DATABASE_DEFAULT + N'|'
          + dp.permission_name COLLATE DATABASE_DEFAULT + N'|'
          + dp.state_desc COLLATE DATABASE_DEFAULT
       FROM sys.database_permissions AS dp
       JOIN sys.database_principals  AS pr ON pr.principal_id = dp.grantee_principal_id
       LEFT JOIN sys.schemas AS s ON s.schema_id = dp.major_id AND dp.class = 3
       LEFT JOIN sys.objects AS o ON o.object_id = dp.major_id AND dp.class = 1
      WHERE pr.name IN (N'RCRAInfoLoader', N'RCRAInfoMonitor',
                        N'RCRAInfoLoaderRole', N'RCRAInfoMonitorRole')
      ORDER BY pr.name, dp.class_desc, ISNULL(s.name, N''), ISNULL(o.name, N''),
               dp.permission_name, dp.state_desc;
     """),

    ("principals", """
     SELECT pr.name COLLATE DATABASE_DEFAULT + N'|'
          + pr.type_desc COLLATE DATABASE_DEFAULT + N'|'
          + ISNULL(r.name COLLATE DATABASE_DEFAULT, N'(no role)')
       FROM sys.database_principals AS pr
       LEFT JOIN sys.database_role_members AS drm ON drm.member_principal_id = pr.principal_id
       LEFT JOIN sys.database_principals   AS r   ON r.principal_id = drm.role_principal_id
      WHERE pr.is_fixed_role = 0
            AND pr.name NOT IN (N'public', N'guest', N'dbo',
                                N'INFORMATION_SCHEMA', N'sys')
      ORDER BY pr.name, ISNULL(r.name, N'');
     """),

]

# The tables that carry audit columns. Asked separately, then aggregated one table at a time, rather
# than assembled into a single dynamic UNION ALL: nesting a string that builds a string that builds a
# string is how you spend an afternoon counting quotes instead of testing a deployment.
AUDITED_TABLES = """
    SELECT s.name COLLATE DATABASE_DEFAULT + N'.' + t.name COLLATE DATABASE_DEFAULT
      FROM sys.tables  AS t
      JOIN sys.schemas AS s ON s.schema_id = t.schema_id
     WHERE EXISTS (SELECT 1 FROM sys.columns AS c
                    WHERE c.object_id = t.object_id AND c.name = N'auditModifiedDateUtc')
     ORDER BY s.name, t.name;
    """


def sqlcmd_query(server: str, database: str, query: str) -> str:
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    result = subprocess.run(
        # -h -1 suppresses headers; -y 8000 raises the display width for variable-length values.
        # That second flag is not cosmetic: the DEFAULT width silently truncates at 256 characters,
        # and the extended-property descriptions this test compares are exactly the values long
        # enough to be cut, which would leave the test blind to any change past that point.
        #
        # sqlcmd rejects -W alongside any -y, and rejects -h alongside -y 0 specifically, so the
        # combination below is the one that both suppresses headers and does not truncate. Trailing
        # whitespace is trimmed in Python in place of -W.
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", database,
         "-h", "-1", "-y", "8000", "-w", "8192", "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(
            f"sqlcmd exited {result.returncode} on the {database} probe:\n"
            f"{result.stdout}\n{result.stderr}")

    lines = [
        line.rstrip()
        for line in result.stdout.splitlines()
        if line.strip() and not ROWS_AFFECTED.match(line.strip())
    ]
    return "\n".join(lines)


def audit_stamps(server: str) -> str:
    """Row count and the latest audit stamps per table. Empty until Workstream B creates the first
    table, and that is fine: this probe becomes load-bearing the moment there is data to protect,
    and until then it records that there was nothing to compare."""
    tables = [t for t in sqlcmd_query(server, "RCRAInfo", AUDITED_TABLES).splitlines() if t.strip()]

    if not tables:
        return "(no tables with audit columns yet)"

    lines = []
    for qualified in tables:
        schema, _, table = qualified.strip().partition(".")
        rows = sqlcmd_query(server, "RCRAInfo", f"""
            SELECT CAST(COUNT(*) AS NVARCHAR(20))
                 + N'|' + ISNULL(CONVERT(NVARCHAR(30), MAX(auditCreatedDateUtc), 126), N'-')
                 + N'|' + ISNULL(CONVERT(NVARCHAR(30), MAX(auditModifiedDateUtc), 126), N'-')
              FROM {quote_name(schema)}.{quote_name(table)};
            """)
        lines.append(f"{schema}.{table}|{rows.strip()}")

    return "\n".join(lines)


def quote_name(identifier: str) -> str:
    """Bracket-quote an identifier read back from the catalog. The value comes from sys.tables, not
    from a user, but a name is still interpolated into a query, so it is escaped rather than
    trusted."""
    return "[" + identifier.replace("]", "]]") + "]"


def snapshot(server: str) -> dict[str, str]:
    result = {label: sqlcmd_query(server, "RCRAInfo", query) for label, query in PROBES}
    result["audit stamps"] = audit_stamps(server)
    return result


def deploy(server: str) -> None:
    """Run the deployment exactly as a developer would, through the same script."""
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
         "-File", str(DEPLOY), "-ServerInstance", server],
        cwd=REPO, capture_output=True, text=True, encoding="utf-8", errors="replace",
        env=os.environ.copy(),
    )

    if result.returncode != 0:
        raise RuntimeError(
            f"Deploy-Database.ps1 exited {result.returncode}:\n{result.stdout}\n{result.stderr}")


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default=".", help="target SQL Server instance (default: .)")
    parser.add_argument("--snapshot-only", action="store_true",
                        help="print the catalog snapshot and exit; does not deploy")
    args = parser.parse_args()

    if not DEPLOY.is_file():
        print(f"FAIL  run-twice: {DEPLOY} not found.")
        return 2

    try:
        before = snapshot(args.server)
    except RuntimeError as exc:
        print(f"FAIL  run-twice: could not read the catalog. Has the deployment been run once?\n"
              f"      {exc}")
        return 2

    if args.snapshot_only:
        for label, text in before.items():
            print(f"--- {label} ---")
            print(text)
        return 0

    try:
        deploy(args.server)
        after = snapshot(args.server)
    except RuntimeError as exc:
        print(f"FAIL  run-twice: {exc}")
        return 1

    differences: list[str] = []

    for label in before:
        if before[label] == after[label]:
            continue

        diff = difflib.unified_diff(
            before[label].splitlines(), after[label].splitlines(),
            fromfile=f"{label} (before second run)", tofile=f"{label} (after second run)",
            lineterm="", n=1)
        differences.append("\n".join(diff))

    if differences:
        print("FAIL  run-twice: the second deployment changed the database. Every script must "
              "converge, because a developer runs them by hand (G3).")
        for diff in differences:
            print()
            for line in diff.splitlines():
                print(f"  {line}")
        return 1

    counts = ", ".join(f"{label} {len(text.splitlines())}" for label, text in before.items())
    print(f"PASS  run-twice: a second full deployment changed nothing "
          f"({len(before)} catalog probes compared; rows per probe: {counts})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
