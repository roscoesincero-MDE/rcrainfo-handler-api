#!/usr/bin/env python
"""
AR8 guardrail: a free-text parameter never reaches a log column.

MDE's own procedure template carries the rule as a comment -- "do NOT include parameters such as
passwords and Personally Identifiable Information (PII)" in @keyParameters -- and this project
extended it to every free-text column of logs.ExecutionLog: KeyParameters, Comments, ErrorMessage,
DynamicSql and ContextMessage. For identifiers, codes, dates and counts the rule is easy, because
those are safe to log and everything gets logged. It is the FREE-TEXT parameters that need a gate:
whatever a person typed, over a database that holds contact names, telephone numbers, email
addresses and home-shaped mailing addresses.

Two procedures take one. dbo.uspSearchHandlerSource takes @SearchTerm, and someone searching a
surname to see which handlers a person is attached to would write that surname into
logs.ExecutionLog -- a table the monitoring web app can READ, so it would come back out on a
screen. config.uspSetLoadWatermark takes @Notes, which is an operator's explanation of why they
rewound a watermark, in their own words.

WHY THIS IS A FILE CHECK AND NOT A DATABASE CHECK. It runs in CI, where there is no SQL Server, and
the rule is a property of the source rather than of a deployment. build/check_stored_headers.py is
what compares deployed modules against the files.

--- THE THREE THINGS THIS CHECK HAD TO GET RIGHT ----------------------------------------------------

1. STRING LITERALS ARE STRIPPED, NOT JUST COMMENTS. This is the trap, and it has already been hit on
   three surfaces in this project: the prose that documents a rule contains the very tokens the rule
   forbids. 506's refusal message is the literal N'@SearchTerm must contain at least 2 characters ...
   error messages are written to logs.ExecutionLog ...' -- a naive scan sees @SearchTerm and
   logs.ExecutionLog in one statement and reports the sentence explaining why the leak is prevented.
   A gate defeated by its own documentation gets suppressed within a day, and a suppressed gate
   enforces nothing.

2. AN ABSENT VARIABLE IS A FAILURE, NOT A PASS. If somebody renames @SearchTerm to @Query, a check
   that only looks for co-occurrence finds none and reports success -- it is now guarding a variable
   that does not exist. So every registered name must be PRESENT in the module, and the failure
   message says to update the registry rather than to fix the code.

3. SCALARS ONLY. 520, 521, 522 and 523 each take a table-valued parameter carrying free text --
   @Elements, @Summaries, apiErrorMessage -- and those are excluded by name from @KeyParameters for
   the same reason. They are NOT registered here, because a TVP cannot be concatenated into an
   NVARCHAR at all: `CONCAT (N'x', @Elements)` does not compile. The rule for them is enforced by the
   type system, which is stronger than this check, and registering them would make requirement 2
   fail on a variable that can never appear in an assignment.

522's @Reason is deliberately NOT registered. It IS logged, on purpose -- it is the operator's
justification for a soft delete and the log row is the only place it can live -- and 522's header
says so. The boundary between @Reason and @Notes is a judgement MDE made, not one this file makes,
so a change to it belongs in the registry with a reason attached.

Exit 0 when every registered procedure is clean, 1 otherwise.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCRIPTS = REPO / "src" / "RCRAInfo.Database" / "Scripts"

# Where a value ends up if it is written to logs.ExecutionLog, plus the two message-raising
# statements, which are the other route: ERROR_MESSAGE () is captured into
# logs.ExecutionLog.ErrorMessage by every CATCH block in this database, so a RAISERROR that
# interpolates a term is a leak with an extra step. PRINT is included because a probe or a
# deployment transcript is a file somebody keeps.
SINKS = ("@KeyParameters", "@ContextMessage", "@DynamicSql", "@Comments", "@ErrorMsg", "@Failure")
RAISING = re.compile(r"\b(?:RAISERROR|THROW|PRINT)\b", re.IGNORECASE)

REGISTRY = [
    {
        "script": "506_dbo.uspSearchHandlerSource.sql",
        "procedure": "dbo.uspSearchHandlerSource",
        # @SearchTerm and every variable derived from it. The derived ones matter as much as the
        # parameter: @ContainsPattern is the term with two wildcards around it, which is the same
        # secret in a shape that looks technical enough to log.
        "secrets": ("@SearchTerm", "@Term", "@Escaped", "@ExactTerm",
                    "@PrefixPattern", "@ContainsPattern"),
        # Facts ABOUT the term that ARE logged, listed so a reader can see the line being drawn
        # rather than infer it. A length, a branch indicator and a present/absent BIT; the term
        # cannot be recovered from any of them. These are deliberately not secrets. @TermPresent
        # exists FOR this check -- it is what lets the statement building @KeyParameters distinguish
        # a NULL term from an empty one without naming the term, so the rule below needs no
        # exception clause.
        "logged": ("@TermLength", "@IdShaped", "@TermPresent"),
    },
    {
        "script": "513_config.uspSetLoadWatermark.sql",
        "procedure": "config.uspSetLoadWatermark",
        # @Notes is an operator's own words. @NewNotes is the value written to the config TABLE,
        # which is where the note belongs, so it is a secret here too -- it must not cross into a
        # log column either.
        "secrets": ("@Notes", "@NewNotes"),
        "logged": ("@NotesSupplied",),
    },
]


def strip_sql(text: str) -> str:
    """Blank out comments and string literals, preserving length and line structure.

    Characters are replaced with spaces rather than deleted so that reported line numbers still
    point at the real line. Newlines inside a block comment or a multi-line literal are kept for
    the same reason.
    """
    out = list(text)
    i, n = 0, len(text)

    while i < n:
        ch = text[i]

        # Line comment.
        if ch == "-" and text.startswith("--", i):
            while i < n and text[i] != "\n":
                out[i] = " "
                i += 1
            continue

        # Block comment. Nesting is legal in T-SQL, so it is counted.
        if ch == "/" and text.startswith("/*", i):
            depth = 0
            while i < n:
                if text.startswith("/*", i):
                    depth += 1
                    out[i] = out[i + 1] = " "
                    i += 2
                    continue
                if text.startswith("*/", i):
                    depth -= 1
                    out[i] = out[i + 1] = " "
                    i += 2
                    if depth == 0:
                        break
                    continue
                if text[i] != "\n":
                    out[i] = " "
                i += 1
            continue

        # String literal, with or without the N prefix. A doubled quote is an escaped quote and
        # does not end the literal -- which is what N'...@KeyParameters''s...' relies on.
        if ch == "'":
            out[i] = " "
            i += 1
            while i < n:
                if text[i] == "'":
                    out[i] = " "
                    i += 1
                    if i < n and text[i] == "'":
                        out[i] = " "
                        i += 1
                        continue
                    break
                if text[i] != "\n":
                    out[i] = " "
                i += 1
            continue

        i += 1

    return "".join(out)


# Where one statement ends and the next begins. The semicolon is the obvious boundary and it is not
# sufficient on its own: T-SQL lets a control-flow header share a semicolon-delimited chunk with the
# statement it governs, so `IF @Term IS NULL BEGIN SET @Failure = N'...';` arrives as ONE chunk
# naming a secret and a sink -- a false positive on code doing exactly the right thing, testing the
# term without logging it. BEGIN and END are added for that reason.
#
# CASE, WHEN, THEN and ELSE are deliberately NOT boundaries, even though adding them would look
# consistent. CASE is an EXPRESSION in T-SQL, so splitting on its keywords would put the sink and the
# secret in different pieces of one assignment:
#
#     SET @KeyParameters = CASE WHEN @TermPresent = 1 THEN N'' ELSE @Term END;   -- a real leak
#
# and the check would report nothing. Every keyword added here widens a hole as well as closing one,
# so the set stays at the two that only ever delimit STATEMENTS.
#
# Safe only because literals and comments are already blanked, so none of these words can be inside
# one.
BOUNDARY = re.compile(r";|\b(?:BEGIN|END)\b", re.IGNORECASE)

# The other half of the control-flow problem: `IF <condition> SET @Failure = ...` with no BEGIN,
# where the condition legitimately tests a secret and the body legitimately writes a sink. When a
# chunk OPENS with a control-flow keyword -- and only then -- it is cut at the first keyword that can
# begin a governed statement, which separates the condition from the body.
#
# The "only then" is what keeps this safe. Applied unconditionally it would cut `SET @ContextMessage
# = (SELECT ...)` at the inner SELECT and lose the sink, which is the same hole as CASE above.
CONTROL_HEADER = re.compile(r"^\s*(?:IF|ELSE|WHILE)\b", re.IGNORECASE)
GOVERNED = re.compile(
    r"\b(?:SET|SELECT|INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|PRINT|RAISERROR|THROW|RETURN)\b",
    re.IGNORECASE)

# A declaration item inside a DECLARE: a comma, a variable, and a TYPE. The type is what makes this
# precise -- a comma inside CONCAT (@a, @b) is not followed by one, so an expression is never split.
# Without this, one DECLARE that lists a sink and a secret among twenty variables reads as an
# assignment of one to the other, which is how 513's AR8 block first tripped this check.
#
# It is a LOOKAHEAD, and that is not cosmetic. A consuming match would eat the comma, the variable
# name and the type, so `DECLARE @x INT = 1, @Failure NVARCHAR (100) = @Term;` would split into a
# piece holding only `(100) = @Term` -- the sink's own name gone, and a genuine leak in an
# initializer reported as clean. Zero width keeps every declared name inside its own item.
DECLARE_ITEM = re.compile(
    r"(?=,\s*@\w+\s+(?:BIT|TINYINT|SMALLINT|INT|BIGINT|DECIMAL|NUMERIC|FLOAT|REAL|MONEY|"
    r"N?VARCHAR|N?CHAR|SYSNAME|DATE|TIME|DATETIME|DATETIME2|DATETIMEOFFSET|"
    r"UNIQUEIDENTIFIER|XML|TABLE|VARBINARY|BINARY)\b)",
    re.IGNORECASE)

STARTS_DECLARE = re.compile(r"^\s*DECLARE\b", re.IGNORECASE)


def split_on(pattern: re.Pattern[str], code: str, offset: int) -> list[tuple[int, str]]:
    """Split code at every match of pattern, keeping each piece with its absolute start offset."""
    pieces: list[tuple[int, str]] = []
    start = 0

    for match in pattern.finditer(code):
        if code[start:match.start()].strip():
            pieces.append((offset + start, code[start:match.start()]))
        start = match.end()

    if code[start:].strip():
        pieces.append((offset + start, code[start:]))

    return pieces


def refine(chunk: str, offset: int) -> list[tuple[int, str]]:
    """Cut one semicolon-delimited chunk into the pieces that are separate statements.

    Two cases, and neither applies to an ordinary assignment, which is returned untouched:
    a DECLARE list becomes one piece per declared variable, and a control-flow header is separated
    from the statement it governs.
    """
    if STARTS_DECLARE.search(chunk):
        return split_on(DECLARE_ITEM, chunk, offset)

    if CONTROL_HEADER.search(chunk):
        body = GOVERNED.search(chunk)
        if body:
            return [(offset, chunk[:body.start()]),
                    (offset + body.start(), chunk[body.start():])]

    return [(offset, chunk)]


def statements(code: str) -> list[tuple[int, str]]:
    """Split stripped SQL into (line number, statement) pairs."""
    found: list[tuple[int, str]] = []

    for offset, chunk in split_on(BOUNDARY, code, 0):
        for start, piece in refine(chunk, offset):
            if piece.strip():
                found.append((code.count("\n", 0, start) + 1, piece))

    return found


def variables(chunk: str) -> set[str]:
    """Every @variable named in a chunk of stripped SQL, case-folded."""
    return {name.lower() for name in re.findall(r"@\w+", chunk)}


def inspect(code: str, entry: dict) -> list[str]:
    """Co-occurrence rule: no statement may name a secret and a sink, or raise with a secret."""
    secrets = {name.lower() for name in entry["secrets"]}
    sinks = {name.lower() for name in SINKS}
    problems: list[str] = []

    for line, chunk in statements(code):
        named = variables(chunk)
        leaked = sorted(named & secrets)
        if not leaked:
            continue

        touched = sorted(named & sinks)
        if touched:
            problems.append(
                f"{entry['script']}:{line}: one statement names {', '.join(leaked)} and "
                f"{', '.join(touched)}. A free-text parameter must never reach a log column; log a "
                f"fact ABOUT it instead -- {entry['procedure']} logs "
                f"{', '.join(entry['logged'])}")
            continue

        if RAISING.search(chunk):
            problems.append(
                f"{entry['script']}:{line}: a RAISERROR, THROW or PRINT statement interpolates "
                f"{', '.join(leaked)}. ERROR_MESSAGE () is captured into "
                f"logs.ExecutionLog.ErrorMessage by every CATCH in this database, so a helpful "
                f"message is a leak with one extra step")

    return problems


def presence(code: str, entry: dict) -> list[str]:
    """Requirement 2: a registered name that is absent means the check is guarding nothing."""
    named = variables(code)
    problems: list[str] = []

    for group in ("secrets", "logged"):
        for name in entry[group]:
            if name.lower() not in named:
                problems.append(
                    f"{entry['script']}: {name} is registered as {group[:-1]} but appears nowhere "
                    f"in the script. Either it was renamed -- in which case update REGISTRY in "
                    f"build/check_search_term_privacy.py, because this check is currently guarding "
                    f"a variable that does not exist -- or it was removed and the entry is stale")

    return problems


# Proof that the rule matches what it claims to, and only that. The MUST_NOT_MATCH half is the more
# important one: samples 2 and 3 are the prose-defeats-the-gate trap, and 5 and 6 are the derivation
# chain, which has to be allowed or the procedure cannot build its own patterns.
SELF_TEST_MUST_MATCH = [
    "SET @KeyParameters = CONCAT (N'x', @SearchTerm);",
    "SET @ContextMessage = CONCAT (N'x', @ContainsPattern);",
    "SET @Failure = N'x' + @Term;",
    "RAISERROR (N'no matches for %s', 16, 1, @Term);",
    "PRINT @SearchTerm;",
    "SELECT @KeyParameters = @Escaped;",
    # A leak reached through a CASE expression, which is why CASE is not a statement boundary.
    "SET @KeyParameters = CASE WHEN @TermPresent = 1 THEN N'' ELSE @Term END;",
    # A leak in the BODY of a control-flow statement, which the condition/body split must not hide.
    "IF @TermLength > 0 SET @Failure = N'no match for ' + @Term;",
    # A leak in a declaration initialiser, which the DECLARE-item split must not hide.
    "DECLARE @Count INT = 0, @Failure NVARCHAR (2048) = @Term;",
]

SELF_TEST_MUST_NOT_MATCH = [
    "SET @KeyParameters = CONCAT (N'TermLength=', @TermLength, N', IdShaped=', @IdShaped);",
    "-- the term must NOT be logged: @SearchTerm never reaches @KeyParameters or @ContextMessage",
    "SET @Failure = N'@SearchTerm is withheld; error messages reach @KeyParameters''s table.';",
    "SET @Term = TRIM (N' ' FROM @SearchTerm);",
    "SET @ContainsPattern = N'%' + @Escaped + N'%';",
    "EXEC logs.uspRecordExecutionError @KeyParameters = @KeyParameters, @DynamicSql = @DynamicSql;",
    "/* WHAT @KeyParameters MAY CONTAIN. Not @SearchTerm, not @Term, not @ContainsPattern. */",
    # 506's refusal: the condition tests the term, the body writes a message that does not quote it.
    "IF @Term IS NULL OR @TermLength < 2 BEGIN SET @Failure = N'at least 2 characters'; END;",
    # The same without BEGIN, which only the condition/body split separates.
    "IF @Term IS NULL SET @Failure = N'the term is withheld from this message';",
    # One DECLARE naming a secret and a sink among its items -- 513's AR8 block.
    "DECLARE @Term NVARCHAR (200) = NULL, @TermPresent BIT = 0, @Failure NVARCHAR (2048) = NULL;",
    # The permitted NULL test, once it goes through the derived BIT rather than the term itself.
    "SET @TermPresent = CASE WHEN @Term IS NULL THEN 0 ELSE 1 END;",
]

SELF_TEST_ENTRY = {
    "script": "self-test",
    "procedure": "self-test",
    "secrets": ("@SearchTerm", "@Term", "@Escaped", "@ExactTerm", "@PrefixPattern",
                "@ContainsPattern"),
    "logged": ("@TermLength", "@IdShaped", "@TermPresent"),
}


def self_test() -> list[str]:
    failures = []

    for sample in SELF_TEST_MUST_MATCH:
        if not inspect(strip_sql(sample), SELF_TEST_ENTRY):
            failures.append(f"self-test: NOT detected but must be: {sample}")

    for sample in SELF_TEST_MUST_NOT_MATCH:
        if inspect(strip_sql(sample), SELF_TEST_ENTRY):
            failures.append(f"self-test: false positive on correct code: {sample}")

    return failures


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    problems: list[str] = self_test()

    for entry in REGISTRY:
        path = SCRIPTS / entry["script"]
        if not path.is_file():
            problems.append(
                f"{entry['script']}: registered in build/check_search_term_privacy.py but not "
                f"found under {SCRIPTS.relative_to(REPO).as_posix()}. Examining nothing and "
                f"reporting success is worse than not running")
            continue

        code = strip_sql(path.read_text(encoding="utf-8", errors="replace"))
        problems.extend(presence(code, entry))
        problems.extend(inspect(code, entry))

    if problems:
        print(f"FAIL  free-text parameter privacy ({len(problems)} problem(s)):")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    guarded = sum(len(entry["secrets"]) for entry in REGISTRY)
    samples = len(SELF_TEST_MUST_MATCH) + len(SELF_TEST_MUST_NOT_MATCH)
    print(f"PASS  free-text parameter privacy ({len(REGISTRY)} procedure(s), {guarded} guarded "
          f"variable(s), {len(SINKS)} log sink(s), {samples} self-test samples classified)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
