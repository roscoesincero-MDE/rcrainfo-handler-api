#!/usr/bin/env python
"""Acceptance test: a paged read that filters on a CLOSED set validates the value against that set.

The defect this exists for has no error message and no wrong row. logs.HandlerLoadStatus.Status is
closed by CK_logs_HandlerLoadStatus_Status to five values. A grid that sends @Status = N'Faild'
matches nothing, so the procedure returns an empty page and the monitoring screen whose entire job is
to show failures draws an empty table. Zero rows and no failures are the same picture and they are
not the same fact. The same argument applies to every filter over a set this database closes.

So each such filter has to reject an unrecognised value, which means the procedure repeats the
constraint's list -- and a repeated list is drift with a silent failure mode in BOTH directions:

  - add a sixth Status to the table and the procedure starts REFUSING a value the table accepts, so
    the new state is unreachable from the UI;
  - remove one and the procedure keeps accepting a value that can no longer occur, which is harmless
    but tells the operator the wrong thing about what the column holds.

The procedure cannot look the constraint up for itself. Script 050 denies metadata visibility to both
application logins, and a read that consulted sys.check_constraints at run time would work for the
developer and fail for the web app. So the list is hand-written and this check compares it against
the catalog from outside, which is the same arrangement as build/check_lookup_coverage.py: the
authority is in the database, the copy is in the file, and something external has to hold them level.

WHAT IT CHECKS, per procedure script:

  1. UNVALIDATED   Every filter of the form (@X IS NULL OR alias.Column = @X) where alias resolves to
                   a table whose Column carries a closed-set CHECK constraint must have a matching
                   `@X NOT IN (...)` rejection. This is the direction that finds a MISSING check --
                   the one nobody notices, because the procedure works.
  2. SET MISMATCH  The NOT IN list must be exactly the constraint's set of values.
  3. NAMED         The rejection message must name the constraint it copies, so a maintainer reading
                   the error knows where the authority lives -- and so this check can tell which
                   constraint the list was copied from rather than guessing.
  4. PROSE         The message must contain `<ConstraintName> allows: a, b, c.` and that prose list
                   must be the same set again. It is what the operator is told to type, so a list
                   that disagrees with the NOT IN above it hands them a value the procedure rejects.

WHAT IT DELIBERATELY DOES NOT CHECK. A filter on a column with NO constraint. That is not an
oversight in the procedure, it is the rule: validate a value only where THIS database closes the set.
HandlerId, ActivityLocation and SourceType are EPA's, they carry no CHECK on purpose -- a value EPA
invents next quarter must LOAD rather than fail -- and a filter that rejected an unrecognised value
would make a code EPA has started sending unsearchable on the very screen an operator would use to
notice it. Ranges (CK_config_LoadWatermark_OverlapDays) and predicates (ISJSON) are not closed sets
either and are ignored: only a constraint that is nothing but equality literals, optionally with IS
NULL, states a list a filter could copy.

Requires: sqlcmd on PATH, Windows authentication, and the deployment already applied. Read-only.

Exit 0 when every closed-set filter validates against the set it filters, 1 on a finding, 2 on a
setup problem.
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1] / "src" / "RCRAInfo.Database" / "Scripts"

# The catalog is the authority for both the table and the column, rather than the constraint NAME
# parsed apart. The naming convention would make CK_<schema>_<Table>_<Column> parseable, but a
# constraint is free to be misnamed and this check would then compare the file against a table nobody
# meant -- parent_object_id and parent_column_id cannot be misnamed.
#
# parent_column_id is 0 for a table-level constraint spanning more than one column; those cannot be a
# single filter's set, so they arrive with an empty column name and are dropped below.
QUERY = """
SET NOCOUNT ON;
SELECT c.name
     + N'|' + SCHEMA_NAME (t.schema_id) + N'.' + t.name
     + N'|' + COALESCE (col.name, N'')
     + N'|' + REPLACE (REPLACE (c.definition, NCHAR (13), N' '), NCHAR (10), N' ')
  FROM sys.check_constraints AS c
  JOIN sys.tables            AS t   ON t.object_id  = c.parent_object_id
  LEFT JOIN sys.columns      AS col ON col.object_id = c.parent_object_id
                                   AND col.column_id = c.parent_column_id
 WHERE c.is_ms_shipped = 0
 ORDER BY c.name;
"""


def as_developer(server: str, query: str) -> list[str]:
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    # -y 4000 rather than -W, and the two cannot be combined: sqlcmd rejects -W with -y, and rejects
    # -y 0 with -h -1. A CHECK definition is nvarchar(max) and the default display width of 256 would
    # TRUNCATE the longer ones -- which would silently drop values from the set this check compares
    # against, and a truncated set is a confident wrong answer. Trailing blanks are stripped here
    # instead, which is all -W was doing.
    result = subprocess.run(
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", "RCRAInfo", "-h", "-1",
         "-y", "4000", "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(f"sqlcmd exited {result.returncode}:\n{result.stdout}{result.stderr}")

    return [line.rstrip() for line in result.stdout.splitlines() if line.strip()]


def sql_literals(text: str) -> list[str]:
    """Every N'...' literal in order, with SQL's doubled quotes unescaped."""
    return [m.group(1).replace("''", "'")
            for m in re.finditer(r"N'((?:[^']|'')*)'", text)]


def closed_set(column: str, definition: str) -> set[str] | None:
    """The set of values a constraint allows, or None when it is not a closed set at all.

    A closed set is a definition made of nothing but `[Col]=N'value'` alternatives, optionally with
    `[Col] IS NULL`. Anything else left over -- a comparison, a function call, a second column -- and
    the constraint states a rule rather than a list, so no filter could copy it.
    """
    remainder = definition
    bracketed = f"[{column}]"

    if bracketed not in remainder:
        return None

    values: set[str] = set()

    def take(match: re.Match[str]) -> str:
        values.add(match.group(1).replace("''", "'"))
        return " "

    remainder = remainder.replace(f"{bracketed} IS NULL", " ")
    remainder = re.sub(re.escape(bracketed) + r"\s*=\s*N'((?:[^']|'')*)'", take, remainder)

    # Only the connective tissue may remain. Note this runs AFTER the literals are gone, so stripping
    # the word OR cannot eat an O or an R out of a value.
    remainder = re.sub(r"\s+|\bOR\b|[()]", "", remainder)

    if remainder or not values:
        return None

    return values


def procedure_body(text: str) -> str | None:
    """Everything from CREATE OR ALTER PROCEDURE onward.

    The header is excluded rather than comment-stripped, because T-SQL block comments NEST and a
    regex that removes them is wrong on exactly the file that needs it. Everything this check looks
    for is below the CREATE anyway.
    """
    match = re.search(r"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+(\w+)\.(\w+)", text, re.I)
    if match is None:
        return None
    return text[match.start():]


def aliases(body: str) -> dict[str, str]:
    """alias -> schema.Table, from every FROM/JOIN that names one."""
    found: dict[str, str] = {}
    for match in re.finditer(r"\b(?:FROM|JOIN)\s+(\w+)\.(\w+)\s+AS\s+(\w+)", body, re.I):
        found[match.group(3)] = f"{match.group(1)}.{match.group(2)}"
    return found


def filters(body: str) -> list[tuple[str, str, str]]:
    """(parameter, alias, Column) for every `@X IS NULL OR alias.Column = @X` predicate.

    The repeated backreference matters: `@From IS NULL OR s.Date >= @From` is a RANGE filter, not an
    equality one, and a range over a closed set is meaningless rather than dangerous. Only the
    equality form claims 'this exact value', which is the form that can silently match nothing.
    """
    out: list[tuple[str, str, str]] = []
    for match in re.finditer(r"@(\w+)\s+IS\s+NULL\s+OR\s+(\w+)\.(\w+)\s*=\s*@(\w+)\b",
                             body, re.I):
        if match.group(1).lower() == match.group(4).lower():
            out.append((match.group(1), match.group(2), match.group(3)))
    return out


def rejections(body: str) -> dict[str, tuple[set[str], str]]:
    """parameter -> (the NOT IN set, the message text that follows it, up to the THROW).

    The segment stops at the parameter's own THROW rather than at the next NOT IN, so a constraint
    name mentioned in a comment further down cannot be read as this rejection's authority.
    """
    out: dict[str, tuple[set[str], str]] = {}
    for match in re.finditer(r"@(\w+)\s+NOT\s+IN\s*\(([^)]*)\)", body, re.I):
        values = set(sql_literals(match.group(2)))
        if not values:
            continue
        tail = body[match.end():]
        stop = re.search(r";\s*THROW\b", tail, re.I)
        out[match.group(1)] = (values, tail[:stop.start()] if stop else tail[:4000])
    return out


def render(values: set[str]) -> str:
    return ", ".join(sorted(values))


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
    except RuntimeError as exc:
        print(f"SETUP  check_closed_set_filters could not query the catalog: {exc}")
        return 2

    if not rows:
        print("SETUP  no CHECK constraints found in RCRAInfo. Has the deployment run?")
        return 2

    # (schema.Table, Column) -> (constraint name, allowed values), closed sets only.
    catalog: dict[tuple[str, str], tuple[str, set[str]]] = {}
    known_constraints: set[str] = set()
    for row in rows:
        parts = row.split("|", 3)
        if len(parts) != 4:
            print(f"SETUP  unparseable catalog row: {row!r}")
            return 2

        name, table, column, definition = (p.strip() for p in parts)
        known_constraints.add(name)

        if not column:
            continue

        values = closed_set(column, definition)
        if values is not None:
            catalog[(table, column)] = (name, values)

    if not catalog:
        # Nothing to compare is not a pass. It means the catalog query changed shape, or the closed-set
        # parser stopped recognising the form these constraints are written in.
        print("SETUP  no closed-set CHECK constraints were recognised, so this check compared "
              "nothing. Either the constraints changed shape or closed_set() no longer parses them.")
        return 2

    findings: list[str] = []
    checked = 0
    files = 0

    for path in sorted(SCRIPTS.glob("*.sql")):
        text = path.read_text(encoding="utf-8")
        body = procedure_body(text)
        if body is None:
            continue

        files += 1
        alias_map = aliases(body)
        rejected = rejections(body)
        seen: set[str] = set()

        for parameter, alias, column in filters(body):
            table = alias_map.get(alias)
            if table is None:
                continue

            entry = catalog.get((table, column))
            if entry is None:
                continue

            constraint, allowed = entry
            if parameter in seen:
                continue
            seen.add(parameter)
            checked += 1

            if parameter not in rejected:
                findings.append(
                    f"{path.name}: @{parameter} filters {table}.{column}, which {constraint} closes "
                    f"to {{{render(allowed)}}}, but the procedure never rejects a value outside that "
                    f"set. An unrecognised value returns an EMPTY PAGE, which on a monitoring grid "
                    f"reads as all-clear. Add `IF @{parameter} IS NOT NULL AND @{parameter} NOT IN "
                    f"(...) ... ;THROW 50000`, naming {constraint} in the message.")
                continue

            values, message = rejected[parameter]

            if values != allowed:
                findings.append(
                    f"{path.name}: @{parameter} rejects everything outside {{{render(values)}}} but "
                    f"{constraint} on {table}.{column} allows {{{render(allowed)}}}. "
                    f"Only in the procedure: {{{render(values - allowed)}}}. "
                    f"Only in the constraint: {{{render(allowed - values)}}}.")

            named = set(re.findall(r"\bCK_\w+", message))

            if constraint not in named:
                other = ", ".join(sorted(named)) if named else "no constraint at all"
                findings.append(
                    f"{path.name}: the message rejecting @{parameter} names {other}, but the set it "
                    f"copies is {constraint} on {table}.{column}. The message has to name the "
                    f"constraint it duplicates -- it is how a maintainer finds the authority, and how "
                    f"this check knows which set the list was copied from.")

            for unknown in sorted(named - known_constraints):
                findings.append(
                    f"{path.name}: the message rejecting @{parameter} names {unknown}, which is not "
                    f"a constraint in this database. Renamed, or never created.")

            prose = re.search(re.escape(constraint) + r"\s+allows:\s*([^.]*)\.",
                              " ".join(sql_literals(message)))
            if prose is None:
                findings.append(
                    f"{path.name}: the message rejecting @{parameter} does not contain "
                    f"'{constraint} allows: <values>.' -- that phrasing is required, because the "
                    f"prose list is what the operator is told to type and it is the half of the "
                    f"message no NOT IN list can keep honest.")
            else:
                listed = {item.strip() for item in prose.group(1).split(",") if item.strip()}
                if listed != allowed:
                    findings.append(
                        f"{path.name}: the message rejecting @{parameter} tells the operator to use "
                        f"{{{render(listed)}}} but {constraint} allows {{{render(allowed)}}}, so the "
                        f"error hands them a value the procedure itself refuses.")

    if not checked:
        print("SETUP  no closed-set filter was found in any procedure script, so this check compared "
              "nothing. Either the filter spelling changed or filters() no longer matches it.")
        return 2

    if findings:
        print(f"FAIL  {len(findings)} closed-set filter finding(s):")
        for item in findings:
            print(f"      - {item}")
        return 1

    print(f"PASS  closed-set filters ({checked} filter(s) across {files} procedure script(s) each "
          f"reject every value outside the CHECK constraint they filter, name that constraint, and "
          f"list the same set in prose; {len(catalog)} closed-set constraint(s) in the catalog)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
