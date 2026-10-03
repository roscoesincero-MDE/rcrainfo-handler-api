#!/usr/bin/env python
"""Acceptance test: each query record's default sort column is the one its procedure declares.

WHY THIS DUPLICATION EXISTS AT ALL. The four paged read procedures each declare a visible default --
`@SortBy NVARCHAR (50) = N'HandlerId'` and its three counterparts -- and each REFUSES a value outside
its whitelist instead of falling through to a default, because a fall-through and a typo are
indistinguishable to the caller: a grid that sends 'StartedDate' by mistake would get rows back,
sorted by something else, and the defect would surface as a user saying the sort arrows do not work.

But RCRAInfo.Data binds every parameter explicitly, which means a procedure's declared default can
never fire. An unset SortBy arrived as NULL, the whitelist correctly refused NULL, and the read threw
for a caller who had simply not expressed a preference. That was a live defect, found by the DA5
round-trip tests when a grid read with no sort specified failed against a real server.

The fix repeats each procedure's default as a `const string DefaultSortBy` on the matching query
record. That is a second copy of a string in two languages, which is drift waiting to happen, and this
check is the reason the copy is acceptable rather than merely convenient. Both sides are read from
source; there is no table in this file to go stale.

WHAT IT CHECKS.

  1. PAIRED          Every query record that has a SortBy property has a DefaultSortBy constant, and
                     every one is reachable from a read method in RCRAInfoContext.Reads.cs that names
                     the procedure it calls. A record with a SortBy and no constant is back to sending
                     NULL.
  2. AGREES          The constant equals the procedure's declared default, compared case-sensitively.
                     Case-insensitively would pass on a value the whitelist accepts but which reads as
                     a different column name to anyone maintaining either side.
  3. WHITELISTED     The declared default is one of the values the procedure's own ORDER BY tests, so
                     a default nobody kept in step with the sort list cannot ship. This is the one
                     that catches a renamed sort column: rename the column in the CASE ladder and the
                     default keeps its old name, and every caller who did not specify a sort gets the
                     refusal this whole arrangement exists to prevent.

WHAT IT DOES NOT CHECK: that the sort actually orders by that column. Only a call against a server can
show that, and ReadProcedureTests does it.

File-only. No server, no deployment. Read-only.

Exit 0 when every default agrees, 1 on a finding, 2 on a setup problem.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
DATA = REPO / "src" / "RCRAInfo.Data"
QUERIES = DATA / "Queries.cs"
READS = DATA / "RCRAInfoContext.Reads.cs"
SCRIPTS = REPO / "src" / "RCRAInfo.Database" / "Scripts"

# A query record: its name, and the body up to the next record or end of file.
RECORD = re.compile(
    r"public\s+sealed\s+record\s+(?P<name>\w+)\s*\{(?P<body>.*?)\n\}", re.S)

# `public const string DefaultSortBy = "HandlerId";`
CONSTANT = re.compile(
    r"public\s+const\s+string\s+DefaultSortBy\s*=\s*\"(?P<value>[^\"]*)\"\s*;")

# A SortBy property, whatever its declared type. A nullable one is itself a finding: it means the
# property can still carry the NULL the procedure refuses.
SORT_PROPERTY = re.compile(r"public\s+(?P<type>string\??)\s+SortBy\s*\{")

# In Reads.cs: the parameter type of a read method, and the procedure name passed to QueryAsync a few
# lines later. Non-greedy across the body so the FIRST QueryAsync after the signature is the one.
READ_METHOD = re.compile(
    r"\(\s*(?P<query>\w+Query)\s+query\s*,.*?QueryAsync<\w+>\s*\(\s*\n?\s*\"(?P<proc>[\w.]+)\"",
    re.S)

# `, @SortBy           NVARCHAR (50) = N'StartedDateUtc'`
DECLARED = re.compile(
    r"@SortBy\s+NVARCHAR\s*\(\s*\d+\s*\)\s*=\s*N'(?P<value>[^']*)'")

# `CASE WHEN @SortBy = N'HandlerName' AND ...`
TESTED = re.compile(r"@SortBy\s*=\s*N'(?P<value>[^']*)'")


def read(path: Path) -> str:
    if not path.is_file():
        raise RuntimeError(f"{path} does not exist.")
    return path.read_text(encoding="utf-8-sig")


def script_for(procedure: str) -> Path:
    """The numbered script that creates a procedure, found by name rather than by a lookup table."""
    matches = [
        p for p in sorted(SCRIPTS.glob("*.sql"))
        if re.search(
            r"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+" + re.escape(procedure) + r"\b",
            read(p), re.I)
    ]

    if len(matches) != 1:
        raise RuntimeError(
            f"{procedure} is created by {len(matches)} scripts; expected exactly one.")

    return matches[0]


def body_of(script: str, procedure: str) -> str:
    """From CREATE OR ALTER onward, so an example in the header cannot be mistaken for the signature."""
    start = re.search(
        r"CREATE\s+OR\s+ALTER\s+PROCEDURE\s+" + re.escape(procedure) + r"\b", script, re.I)

    if start is None:
        raise RuntimeError(f"{procedure} has no CREATE OR ALTER in its own script.")

    return script[start.start():]


def main() -> int:
    try:
        queries = read(QUERIES)
        reads = read(READS)
    except RuntimeError as error:
        print(f"SETUP: {error}")
        return 2

    # query record name -> the procedure it calls.
    procedures = {m.group("query"): m.group("proc") for m in READ_METHOD.finditer(reads)}

    findings: list[str] = []
    checked = 0

    for record in RECORD.finditer(queries):
        name, body = record.group("name"), record.group("body")

        sort = SORT_PROPERTY.search(body)
        if sort is None:
            continue

        checked += 1

        if sort.group("type") == "string?":
            findings.append(
                f"{name}.SortBy is `string?`. A null reaches the procedure as NULL, which its "
                f"whitelist refuses, so a caller who specified no sort gets an error rather than the "
                f"default. Give it `string` and the DefaultSortBy initialiser.")

        constant = CONSTANT.search(body)
        if constant is None:
            findings.append(
                f"{name} has a SortBy property but no `public const string DefaultSortBy`. Without "
                f"it the property's default is null and the procedure refuses the read.")
            continue

        if name not in procedures:
            findings.append(
                f"{name} is not the parameter of any read method in {READS.name} that calls "
                f"QueryAsync, so this check cannot tell which procedure's default it should equal.")
            continue

        procedure = procedures[name]

        try:
            script = script_for(procedure)
            signature = body_of(read(script), procedure)
        except RuntimeError as error:
            findings.append(f"{name} -> {procedure}: {error}")
            continue

        declared = DECLARED.search(signature)
        if declared is None:
            findings.append(
                f"{procedure} ({script.name}) declares no default for @SortBy. "
                f"{name}.DefaultSortBy is '{constant.group('value')}', which now claims to mirror "
                f"something that is not there.")
            continue

        if declared.group("value") != constant.group("value"):
            findings.append(
                f"{name}.DefaultSortBy is '{constant.group('value')}' but {procedure} "
                f"({script.name}) declares '{declared.group('value')}'. The two have drifted, which "
                f"is exactly what this check exists for: the C# value is the one that ships, so "
                f"every caller who specifies no sort is silently sorting by the wrong column or "
                f"getting a refusal.")
            continue

        tested = {m.group("value") for m in TESTED.finditer(signature)}
        if declared.group("value") not in tested:
            findings.append(
                f"{procedure} ({script.name}) defaults @SortBy to '{declared.group('value')}', "
                f"which its own ORDER BY never tests. Permitted: "
                f"{', '.join(sorted(tested)) or '(none found)'}. Every caller who specifies no sort "
                f"gets the refusal the default was supposed to avoid.")

    if checked == 0:
        print(
            "SETUP: no query record with a SortBy property was found in "
            f"{QUERIES.name}. This check would pass by examining nothing.")
        return 2

    for finding in findings:
        print(f"FINDING: {finding}")

    if findings:
        print(f"\n{len(findings)} finding(s) across {checked} query record(s).")
        return 1

    print(f"OK: {checked} query record(s) default to the sort column their procedure declares.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
