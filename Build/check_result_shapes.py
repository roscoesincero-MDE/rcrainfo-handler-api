#!/usr/bin/env python
"""Acceptance test: every C# result shape matches the projection of the procedure that fills it.

The nine classes in src/RCRAInfo.Data/Results were EMITTED from
sys.dm_exec_describe_first_result_set, not typed. That is the only sane way to produce a 215-property
class, and it is also the reason they need a guardrail: an emitted file looks hand-written a week
later, and nothing about it says which query it came from.

The failure mode is silent in the direction that matters. EF Core binds a FromSqlRaw result by COLUMN
NAME, so a projection that gains a column loses it here without a word, and a projection that RENAMES
one leaves the C# property at its type default -- null for a string, 0 for an int, false for a bool.
A grid then draws a blank column and a caller reads a zero, and both look like data.

`dotnet ef dbcontext scaffold` cannot cover this. It does not generate types for stored-procedure
result sets at all, which is why scaffold-and-diff is kept for the two VIEWS and the procedures are
covered from outside instead.

WHAT IT CHECKS. Both sides are derived from source; there is no table in this file to go stale.

  1. READ SHAPES     For every `QueryAsync<T> ("schema.usp...")` call site in the context, the
                     procedure's first result set must match T property-for-property: same names, same
                     order, same CLR type.
  2. WRITE SHAPES    For every `ExecuteAsync ("schema.usp...")` call site, the procedure must return
                     NO result set. ExecuteSqlRawAsync discards rows, and for a procedure with both
                     rows and OUTPUT parameters the outputs are not populated until the rows have been
                     consumed -- so a write procedure that grew a projection would start reporting
                     zeroes from its output parameters, which this project's binder then raises on. A
                     count silently read as zero would report a batch that wrote thousands as having
                     done nothing.
  3. NO ORPHANS      Every class in Results/ is reached by some QueryAsync call site. A shape nothing
                     fills is either a projection that was removed or a method that was never written,
                     and both are worth knowing.
  4. DESCRIBABLE     A procedure the engine cannot describe is a finding, not a skip. It means the
                     projection depends on dynamic SQL, and neither this check nor a reader can then
                     say what the shape is.

WHAT IT DELIBERATELY DOES NOT CHECK: NULLABILITY. describe_first_result_set reports a projected column
as nullable whenever the optimizer cannot prove otherwise, which is most of them -- TotalRows comes
back nullable on three shapes and non-nullable on a fourth, from procedures that compute it
identically. Enforcing that here would force the C# to mirror an optimizer artifact. The rule the
classes follow instead is one that cannot throw: SQL nullable means C# nullable, and a C# nullable over
a non-null column is always safe. Field-level non-nullity is asserted where it can be asserted
honestly -- by the round-trip tests, against a fully populated payload.

Requires: sqlcmd on PATH, Windows authentication, and the deployment already applied. Read-only.

Exit 0 when every shape agrees, 1 on a finding, 2 on a setup problem.
"""

from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

DATA = Path(__file__).resolve().parents[1] / "src" / "RCRAInfo.Data"
RESULTS = DATA / "Results"

# SQL type -> the CLR type these classes use for it.
#
# datetime2 maps to DateTimeOffset rather than DateTime, and that is a decision rather than a
# convenience: DATETIME2 read into a DateTime arrives as DateTimeKind.Unspecified, so a caller doing
# ToLocalTime() on a column named StartedDateUtc shifts a value that was already UTC. The context
# applies a value converter over every DateTimeOffset property to make the mapping real; see
# RCRAInfoContext.ConfigureConventions. A bare DateTime in Results/ is therefore a finding here.
#
# date maps to DateOnly for the same family of reason -- a date with a time component invites a
# timezone question that the column does not have an answer to.
CLR_BY_SQL = {
    "bit": "bool",
    "tinyint": "byte",
    "smallint": "short",
    "int": "int",
    "bigint": "long",
    "real": "float",
    "float": "double",
    "date": "DateOnly",
    "datetime2": "DateTimeOffset",
    "uniqueidentifier": "Guid",
    "char": "string",
    "nchar": "string",
    "varchar": "string",
    "nvarchar": "string",
    "text": "string",
    "ntext": "string",
    "decimal": "decimal",
    "numeric": "decimal",
    "money": "decimal",
    "smallmoney": "decimal",
    "varbinary": "byte[]",
    "binary": "byte[]",
}

# One row per procedure per projected column. OUTER APPLY rather than CROSS APPLY on purpose: a
# procedure with no result set must still produce a row, or "returns nothing" and "was not asked
# about" would be the same answer -- and finding 2 exists precisely to tell those apart.
QUERY_TEMPLATE = """
SET NOCOUNT ON;
WITH Procedures (Name) AS (
    SELECT * FROM (VALUES {values}) AS v (Name)
)
SELECT p.Name
     + N'|' + CAST (COALESCE (d.column_ordinal, 0) AS NVARCHAR (10))
     + N'|' + COALESCE (d.name, N'')
     + N'|' + COALESCE (d.system_type_name, N'')
     + N'|' + COALESCE (CAST (d.error_number AS NVARCHAR (20)), N'')
     + N'|' + COALESCE (d.error_message, N'')
  FROM Procedures AS p
  OUTER APPLY sys.dm_exec_describe_first_result_set_for_object (OBJECT_ID (p.Name), 0) AS d
 ORDER BY p.Name, COALESCE (d.column_ordinal, 0);
"""


def as_developer(server: str, query: str) -> list[str]:
    exe = shutil.which("sqlcmd")
    if exe is None:
        raise RuntimeError("sqlcmd is not on PATH.")

    # -y 4000 rather than -W; sqlcmd rejects -W together with -y, and rejects -y 0 together with
    # -h -1. An error_message is nvarchar(max) and the default display width of 256 would truncate the
    # one row whose whole value is its text.
    result = subprocess.run(
        [exe, "-S", server, "-E", "-C", "-b", "-I", "-x", "-d", "RCRAInfo", "-h", "-1",
         "-y", "4000", "-Q", query],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )

    if result.returncode != 0:
        raise RuntimeError(f"sqlcmd exited {result.returncode}:\n{result.stdout}{result.stderr}")

    return [line.rstrip() for line in result.stdout.splitlines() if line.strip()]


def call_sites() -> tuple[list[tuple[str, str, str]], list[tuple[str, str]]]:
    """(reads, writes) from the context's own source.

    reads  -- (file, result type, procedure) for each QueryAsync<T> ("schema.usp...").
    writes -- (file, procedure) for each ExecuteAsync ("schema.usp...").

    Derived rather than listed, so the check cannot drift from the code it is checking. Both patterns
    require the procedure name to be a STRING LITERAL on the call, which it always is: the context
    composes its EXEC text from compile-time literals only, and a name that were not a literal would
    be the more serious finding.
    """
    reads: list[tuple[str, str, str]] = []
    writes: list[tuple[str, str]] = []

    for path in sorted(DATA.glob("RCRAInfoContext*.cs")):
        text = path.read_text(encoding="utf-8")

        for match in re.finditer(
                r"QueryAsync<(\w+)>\s*\(\s*\r?\n\s*\"([\w]+\.usp\w+)\"", text):
            reads.append((path.name, match.group(1), match.group(2)))

        for match in re.finditer(
                r"(?<!Query)ExecuteAsync\s*\(\s*\r?\n\s*\"([\w]+\.usp\w+)\"", text):
            writes.append((path.name, match.group(1)))

    return reads, writes


def column_of(property_name: str) -> str:
    """The column a property binds to.

    One rename exists and it is deliberate. This database's convention is PascalCase for every object
    and field EXCEPT the standard audit columns, which are auditCreatedBy, auditCreatedDateUtc,
    auditModifiedBy and auditModifiedDateUtc -- and a C# property spelled auditModifiedDateUtc would
    fail the analyzers. So the property is PascalCase and RCRAInfoContext.OnModelCreating maps it.

    Applying the same rule here is only honest if the mapping is really there, which is why
    `mapping_rule_present` asserts it separately. Without that assertion this function would be a way
    to make a genuine name mismatch disappear.
    """
    if property_name.startswith("Audit"):
        return "audit" + property_name[len("Audit"):]
    return property_name


def mapping_rule_present() -> bool:
    """Whether OnModelCreating still maps Audit* onto audit*."""
    text = (DATA / "RCRAInfoContext.cs").read_text(encoding="utf-8")
    return bool(re.search(r'StartsWith\s*\(\s*"Audit"', text)) and \
           bool(re.search(r'string\.Concat\s*\(\s*"audit"', text))


def properties(type_name: str) -> list[tuple[str, str]] | None:
    """[(column, CLR type without its ? suffix)] in file order, or None if there is no such file."""
    path = RESULTS / f"{type_name}.cs"
    if not path.is_file():
        return None

    out: list[tuple[str, str]] = []
    for match in re.finditer(
            r"^ {4}public ([A-Za-z0-9_\[\]]+)(\?)? (\w+) \{ get; init; \}",
            path.read_text(encoding="utf-8"), re.M):
        out.append((column_of(match.group(3)), match.group(1)))
    return out


def clr_of(system_type_name: str) -> str | None:
    """The CLR type for a system_type_name like `nvarchar(50)` or `datetime2(7)`."""
    base = system_type_name.split("(", 1)[0].strip().lower()
    return CLR_BY_SQL.get(base)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server", default=".", help="target SQL Server instance (default: .)")
    args = parser.parse_args()

    reads, writes = call_sites()

    if not reads or not writes:
        print(f"SETUP  check_result_shapes found {len(reads)} QueryAsync and {len(writes)} "
              f"ExecuteAsync call sites in {DATA.name}. It expects both. Either the context was "
              f"restructured or the call-site patterns no longer match it, and a check that matched "
              f"nothing would pass while examining nothing.")
        return 2

    if not mapping_rule_present():
        print("SETUP  RCRAInfoContext.OnModelCreating no longer maps Audit* properties onto the "
              "camelCase audit* columns. This check normalises those names on the assumption that it "
              "does, so without the mapping it would be hiding eleven real name mismatches instead "
              "of allowing one deliberate rename.")
        return 2

    procedures = sorted({p for _, _, p in reads} | {p for _, p in writes})
    values = ", ".join(f"(N'{p}')" for p in procedures)

    try:
        rows = as_developer(args.server, QUERY_TEMPLATE.format(values=values))
    except RuntimeError as exc:
        print(f"SETUP  check_result_shapes could not describe the procedures: {exc}")
        return 2

    # procedure -> [(ordinal, column, system_type_name)], and procedure -> describe error.
    shapes: dict[str, list[tuple[int, str, str]]] = {p: [] for p in procedures}
    errors: dict[str, str] = {}

    for row in rows:
        parts = row.split("|", 5)
        if len(parts) != 6:
            print(f"SETUP  unparseable describe row: {row!r}")
            return 2

        name, ordinal, column, sql_type, error_number, error_message = (p.strip() for p in parts)

        if name not in shapes:
            print(f"SETUP  describe returned a procedure that was not asked about: {name!r}")
            return 2

        if error_number:
            errors[name] = f"{error_number}: {error_message}"
            continue

        if ordinal == "0" or not column:
            continue

        shapes[name].append((int(ordinal), column, sql_type))

    findings: list[str] = []

    for name in procedures:
        if name in errors:
            findings.append(
                f"{name}: SQL Server cannot describe this procedure's first result set "
                f"({errors[name]}). A projection the engine cannot describe is one that depends on "
                f"dynamic SQL, so neither this check nor a maintainer can say what shape it returns. "
                f"@SortBy belongs in a CASE-based ORDER BY, not in a concatenated statement.")

    # 1. Read shapes.
    for file_name, type_name, procedure in reads:
        if procedure in errors:
            continue

        expected = shapes[procedure]
        actual = properties(type_name)

        if actual is None:
            findings.append(
                f"{file_name}: QueryAsync<{type_name}> names a result type with no file at "
                f"Results/{type_name}.cs, so its shape cannot be compared against {procedure}.")
            continue

        if not expected:
            findings.append(
                f"{procedure}: returns NO result set, but {file_name} reads it with "
                f"QueryAsync<{type_name}>, which materialises rows. This call returns an empty list "
                f"for every input.")
            continue

        expected_names = [column for _, column, _ in expected]
        actual_names = [prop for prop, _ in actual]

        if expected_names != actual_names:
            missing = [c for c in expected_names if c not in actual_names]
            extra = [p for p in actual_names if p not in expected_names]

            detail = []
            if missing:
                detail.append(
                    f"projected but absent from {type_name}: {', '.join(missing)} -- these columns "
                    f"are read from the wire and DISCARDED")
            if extra:
                detail.append(
                    f"declared on {type_name} but not projected: {', '.join(extra)} -- these stay at "
                    f"their type default on every row, which reads as data")
            if not detail:
                detail.append(
                    f"same names in a different order. Nothing breaks today, because EF Core binds by "
                    f"name -- but it means {type_name} was not regenerated from the current "
                    f"projection, so the next person who regenerates it gets a diff they cannot "
                    f"attribute. Projected order: {', '.join(expected_names)}")

            findings.append(f"{procedure} vs {type_name}: " + "; ".join(detail) + ".")
            continue

        actual_types = dict(actual)
        for _, column, sql_type in expected:
            wanted = clr_of(sql_type)

            if wanted is None:
                findings.append(
                    f"{procedure}.{column} is {sql_type}, which this check has no CLR mapping for. "
                    f"Add it to CLR_BY_SQL rather than leaving the column unchecked.")
                continue

            if actual_types[column] != wanted:
                findings.append(
                    f"{procedure}.{column} is {sql_type}, which maps to {wanted}, but "
                    f"{type_name}.{column} is declared {actual_types[column]}.")

    # 2. Write shapes.
    for file_name, procedure in writes:
        if procedure in errors:
            continue

        if shapes[procedure]:
            columns = ", ".join(column for _, column, _ in shapes[procedure])
            findings.append(
                f"{procedure}: returns a result set ({columns}), but {file_name} calls it through "
                f"ExecuteAsync, which discards rows. Worse, a procedure with both rows and OUTPUT "
                f"parameters does not populate the outputs until the rows are consumed, so every "
                f"count this call reads back would be unset. Read it with QueryAsync<T> instead.")

    # 3. Orphaned shapes.
    used = {type_name for _, type_name, _ in reads}
    for path in sorted(RESULTS.glob("*.cs")):
        if path.stem not in used:
            findings.append(
                f"Results/{path.name}: no QueryAsync call site names {path.stem}, so nothing fills "
                f"this shape. Either a projection was removed or a method was never written.")

    for finding in findings:
        print(f"FAIL   {finding}")

    if findings:
        print(f"\n{len(findings)} finding(s). {len(reads)} read shape(s) and {len(writes)} write "
              f"call(s) compared against {len(procedures)} procedures.")
        return 1

    columns = sum(len(shapes[p]) for p in procedures)
    print(f"PASS   {len(reads)} result shape(s), {columns} projected columns, matched their "
          f"procedures; {len(writes)} write call(s) return no result set.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
