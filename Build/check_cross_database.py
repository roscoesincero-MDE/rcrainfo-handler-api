#!/usr/bin/env python
"""
A3 guardrail: every object RCRAInfo names must resolve inside RCRAInfo.

The validator hook covers .sql files. This covers everywhere else SQL actually lives in a .NET
solution: string literals in .cs, connection strings in appsettings templates and launchSettings,
Razor pages, MSBuild files, deployment PowerShell.

The constraint being enforced (G17/G18): on the developer's workstation RCRAInfo and the ETS
database (epal_issi) sit on the SAME SQL Server, so a three-part name works. In UAT and Production
they are on DIFFERENT SERVERS, where the same name fails -- and fails at run time, on a scheduled
overnight load, not at deploy time where someone would see it. Phase 2 moves data between the two
through the application, never through a name.

Detection differs from the .sql case on purpose. A four-part-name pattern is safe in T-SQL and
useless in C#, where `System.Collections.Generic.List` is a four-part dotted name; so outside .sql
a dotted name is only reported when a SQL keyword immediately precedes it. That also catches the
first three parts of a four-part name, which is enough to fail the build.

The keyword rule has one exemption, and it is here because English shares a word with T-SQL: "from"
is also a preposition, so a comment reading "copied from secrets.Template.json" parses as
FROM <database>.<schema>.<object>. A dotted name whose last segment is a file extension is exempt --
see FILENAME_TAILS, which lists only tails that cannot be a table name, and whose limits are proved
in both directions by the self-test.

Exit 0 when nothing is found, 1 otherwise.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

# Application and deployment sources. build/ is deliberately absent: this file and the validator
# hook both contain the string 'epal_issi' as the thing they look for, and a checker that reports
# itself is a checker people learn to ignore.
SCAN_ROOTS = ["src", "tools", "tests"]

SCAN_SUFFIXES = {
    ".cs", ".cshtml", ".razor", ".csproj", ".props", ".targets",
    ".json", ".config", ".xml", ".ps1", ".psm1", ".psd1",
}

# Build output, vendored client libraries, and the fixtures, which are wrong on purpose.
SKIP_DIRECTORIES = {"bin", "obj", "lib", "node_modules", ".git", ".vs", "fixtures"}

# Databases a connection string may legitimately name. master appears only in the bootstrap that
# creates RCRAInfo, before there is anything to connect to.
ALLOWED_CATALOGS = {"rcrainfo", "master"}

# Last segments that make a dotted name a FILENAME rather than a SQL object. Outside .sql, English
# prose is one of the things being scanned, and English uses "from" as a preposition: two comments in
# CredentialFile.cs say "copied from secrets.Template.json", which the three-part-name rule read as
# FROM <database>.<schema>.<object> and reported twice. Exempting the shape is right; loosening the
# rule would not be, and neither would a per-file suppression -- the next comment naming a file would
# be reported again, and the fix that gets applied under time pressure is deleting the rule.
#
# The set is deliberately short and holds only tails that cannot plausibly be a table or procedure
# name. `config` and `log` were both considered and left OUT: `FROM ETSPROD.dbo.Config` is exactly
# what this rule exists to catch, and one exemption that swallows a genuine cross-database read costs
# more than every false positive this list removes. Both directions are in the self-test.
FILENAME_TAILS = {
    "json", "txt", "cs", "sql", "md", "xml", "csproj", "props", "targets",
    "ps1", "psm1", "psd1", "py", "exe", "dll", "pdb", "cshtml", "razor",
    "csv", "docx", "pdf", "png", "yml", "yaml",
}


def names_a_file(matched: str) -> bool:
    """True when a keyword-plus-dotted-name match is prose or code naming a file on disk."""
    tail = matched.rsplit(".", 1)[-1]
    return tail.strip("[]\"'`;,)").lower() in FILENAME_TAILS


# (pattern, why, exempt). `exempt` is given the matched text and returns True to discard the finding;
# None means every match is reported. It exists for exactly one rule -- see names_a_file.
FINDINGS = [
    (re.compile(r"\bepal_issi\b", re.IGNORECASE),
     "names the ETS database directly. In UAT and Production that is a different SQL Server, so "
     "this resolves on the workstation and fails everywhere else. Phase 2 moves data through the "
     "application",
     None),

    (re.compile(r"\bOPEN(?:QUERY|DATASOURCE|ROWSET)\s*\(", re.IGNORECASE),
     "ad-hoc distributed query embedded in application code; RCRAInfo reads no other server",
     None),

    (re.compile(r"\bsp_addlinkedserver\b", re.IGNORECASE),
     "creates a linked server; the deployment provisions no linked servers",
     None),

    (re.compile(r"\b(?:FROM|JOIN|INTO|UPDATE|MERGE|EXEC|EXECUTE)\s+"
                r"\[?\w+\]?\.\[?\w*\]?\.\[?\w+\]?", re.IGNORECASE),
     "embeds a three-part (or longer) name, which reaches outside RCRAInfo; the object must be "
     "named as schema.object",
     names_a_file),
]

CATALOG = re.compile(r"(?:Initial\s+Catalog|Database)\s*=\s*([A-Za-z_][A-Za-z0-9_]*)",
                     re.IGNORECASE)

# Proof that the patterns match what they claim to, and only that. The .sql rules get fixture files
# under build/fixtures; these cannot, because a fixture would have to sit inside the very tree this
# script scans. So the samples are inline and run on every invocation.
#
# The MUST_NOT_MATCH half matters more than the MUST_MATCH half. Outside .sql, dotted names are
# ordinary: a rule loose enough to flag `System.Collections.Generic.List` gets suppressed within a
# day, and a suppressed rule enforces nothing.
SELF_TEST_MUST_MATCH = [
    'var sql = "SELECT HandlerId FROM epal_issi.dbo.Handler";',
    '"Data Source=ETSPROD;Initial Catalog=epal_issi;"',
    'var sql = "SELECT * FROM OPENQUERY(ETSPROD, \'SELECT 1\')";',
    'EXEC master.dbo.sp_addlinkedserver @server = N\'ETSPROD\';',
    'var sql = "SELECT h.HandlerId FROM RCRAInfoUat.dbo.HandlerSource AS h";',
    '"Server=(local);Database=SomeOtherDatabase;Integrated Security=true"',
    # The other side of FILENAME_TAILS. A table can be called Config or Log, so the exemption must
    # not reach these -- an exemption that swallows a real cross-server read is the expensive
    # direction of this fix.
    'var sql = "SELECT Setting FROM ETSPROD.dbo.Config";',
    'var sql = "SELECT TOP 1 Id FROM ETSPROD.dbo.Log";',
]

SELF_TEST_MUST_NOT_MATCH = [
    'using System.Collections.Generic.List;',
    'var name = typeof(Microsoft.EntityFrameworkCore.DbContext).FullName;',
    'var q = from h in context.HandlerSource where h.IsDeleted == 0 select h;',
    'var sql = "SELECT HandlerId FROM dbo.HandlerSource WHERE IsDeleted = 0";',
    'EXEC util.uspSetObjectDescription @SchemaName = N\'dbo\';',
    '"Server=(local);Database=RCRAInfo;Integrated Security=true"',
    '"Server=(local);Initial Catalog=master;Integrated Security=true"',
    '"Server=(local);Database=$(TargetDatabase);"',
    '"Server=(local);Database={DatabaseName};"',
    # English prose, which this script scans because comments and XML docs live in the same .cs files
    # as the string literals. "from <filename>" is the shape that produced the only false positives
    # this rule has ever raised; see FILENAME_TAILS.
    '// the operator copies the value from secrets.Template.json into secrets.json',
    '/// <summary>Read from appsettings.Development.json beside the executable.</summary>',
    '// deployed by Deploy-RCRAInfo.ps1, which reads from src/RCRAInfo.Database/Scripts/010_Database.sql',
]


def findings_in(text: str) -> list[tuple[int, str, str]]:
    """Every finding in one blob of text as (offset, matched text, why).

    The single place the rules are applied, so the self-test and the file scan cannot drift. They did
    once: `matches` re-implemented the loop, and a rule gaining an exemption would have been exempt in
    the scan and still reported in the self-test -- or, worse, the reverse.
    """
    found: list[tuple[int, str, str]] = []

    for pattern, why, exempt in FINDINGS:
        for match in pattern.finditer(text):
            if exempt is not None and exempt(match.group(0)):
                continue

            found.append((match.start(), match.group(0).strip(), why))

    for match in CATALOG.finditer(text):
        catalog = match.group(1)
        if catalog.lower() in ALLOWED_CATALOGS:
            continue

        found.append((
            match.start(),
            match.group(0),
            f"connection string names catalog {catalog!r}; the applications connect to RCRAInfo only"))

    return found


def matches(text: str) -> bool:
    """True if any rule would report this text."""
    return bool(findings_in(text))


def self_test() -> list[str]:
    failures = []

    for sample in SELF_TEST_MUST_MATCH:
        if not matches(sample):
            failures.append(f"self-test: NOT detected but must be: {sample}")

    for sample in SELF_TEST_MUST_NOT_MATCH:
        if matches(sample):
            failures.append(f"self-test: false positive on legitimate code: {sample}")

    return failures


def files_to_scan() -> list[Path]:
    found: list[Path] = []

    for root in SCAN_ROOTS:
        base = REPO / root
        if not base.is_dir():
            continue

        for path in base.rglob("*"):
            if not path.is_file() or path.suffix.lower() not in SCAN_SUFFIXES:
                continue
            if SKIP_DIRECTORIES & {part.lower() for part in path.relative_to(REPO).parts[:-1]}:
                continue
            found.append(path)

    return sorted(found)


def scan(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8", errors="replace")
    relative = path.relative_to(REPO).as_posix()

    return [
        f"{relative}:{text.count(chr(10), 0, offset) + 1}: {matched!r} {why}"
        for offset, matched, why in findings_in(text)
    ]


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    targets = files_to_scan()

    if not targets:
        # Same reasoning as the SQL validator: examining nothing and reporting success is worse
        # than not running at all, because it looks like evidence.
        print(f"FAIL  cross-database references: found no files to scan under "
              f"{', '.join(SCAN_ROOTS)}. Either the roots moved or the suffix list is wrong.")
        return 1

    problems: list[str] = self_test()
    for path in targets:
        problems.extend(scan(path))

    if problems:
        print(f"FAIL  cross-database references ({len(problems)} in {len(targets)} files):")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    samples = len(SELF_TEST_MUST_MATCH) + len(SELF_TEST_MUST_NOT_MATCH)
    print(f"PASS  cross-database references ({len(targets)} files scanned, {samples} self-test "
          f"samples classified; everything resolves inside RCRAInfo)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
