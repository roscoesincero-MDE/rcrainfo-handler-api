#!/usr/bin/env python
"""
A3 guardrail: prove the SQL validator actually rejects what it claims to reject.

`.claude/hooks/validate-sql.py` is the only mechanical enforcement of the DDL conventions, and it
has the failure mode every linter has: a rule whose pattern stops matching goes on reporting
success. Run against correct scripts it is indistinguishable from a rule that has been deleted.

So this runs three things:

  1. A positive control. The real scripts in src/RCRAInfo.Database/Scripts must pass. A validator
     that fails everything is no more useful than one that passes everything.
  2. Every fixture in build/fixtures/*.badsql must FAIL, and must fail with the specific messages
     the fixture names in its own `-- EXPECT:` lines. Asserting only the exit code would let a
     fixture pass because an unrelated rule tripped, which is exactly how a broken rule hides
     behind a working one.
  3. The empty case. Pointed at a directory with no .sql files, the validator must exit 2 rather
     than report success. Green and blind is the worst state a check can be in, and it is the state
     a mistyped path in CI produces.

Exit 0 when all three hold, 1 otherwise.
"""

from __future__ import annotations

import re
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
VALIDATOR = REPO / ".claude" / "hooks" / "validate-sql.py"
FIXTURES = REPO / "build" / "fixtures"
GOOD_SCRIPTS = REPO / "src" / "RCRAInfo.Database" / "Scripts"

EXPECT = re.compile(r"^\s*--\s*EXPECT:\s*(.+?)\s*$", re.MULTILINE)


def run_validator(*args: str) -> tuple[int, str]:
    """Run the validator in CLI mode. Both streams are returned as one blob because the violation
    list goes to stderr and the PASS line goes to stdout, and callers care about both."""
    result = subprocess.run(
        [sys.executable, str(VALIDATOR), *args],
        cwd=REPO, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    return result.returncode, (result.stdout or "") + (result.stderr or "")


def check_positive_control(failures: list[str]) -> int:
    code, output = run_validator(str(GOOD_SCRIPTS))
    if code != 0:
        failures.append(
            f"positive control: the real scripts under {GOOD_SCRIPTS.relative_to(REPO)} did not "
            f"pass (exit {code}). Either a script regressed or a new rule has a false positive:\n"
            + "\n".join(f"      {line}" for line in output.splitlines()))
    return 1


def check_fixture(fixture: Path, failures: list[str]) -> None:
    text = fixture.read_text(encoding="utf-8")
    expected = EXPECT.findall(text)

    if not expected:
        failures.append(
            f"{fixture.name}: has no '-- EXPECT:' line, so it asserts only that something went "
            "wrong. Name the message the fixture is meant to provoke.")
        return

    code, output = run_validator(str(fixture))

    if code == 0:
        failures.append(
            f"{fixture.name}: the validator PASSED a file that is deliberately wrong. "
            f"The rule behind these expectations is not firing: {'; '.join(expected)}")
        return

    for want in expected:
        if want not in output:
            failures.append(
                f"{fixture.name}: expected the report to contain {want!r}, and it did not. "
                "The rule was either removed or its message was reworded without updating the "
                "fixture.")


def check_empty_case(failures: list[str]) -> None:
    with tempfile.TemporaryDirectory() as empty:
        code, output = run_validator(empty)

    if code != 2:
        failures.append(
            f"empty directory: expected exit 2 ('found no .sql files'), got {code}. A validator "
            f"that reports success when it examined nothing turns a mistyped CI path into a green "
            f"build:\n" + "\n".join(f"      {line}" for line in output.splitlines()))


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    if not VALIDATOR.is_file():
        print(f"FAIL  SQL guardrails: validator not found at {VALIDATOR}")
        return 1

    fixtures = sorted(FIXTURES.glob("*.badsql"))
    if not fixtures:
        print(f"FAIL  SQL guardrails: no fixtures found in {FIXTURES}. "
              "The fixtures ARE the test; without them nothing here is verified.")
        return 1

    failures: list[str] = []
    checks = check_positive_control(failures)

    for fixture in fixtures:
        check_fixture(fixture, failures)
        checks += 1

    check_empty_case(failures)
    checks += 1

    expectations = sum(len(EXPECT.findall(f.read_text(encoding="utf-8"))) for f in fixtures)

    if failures:
        print(f"FAIL  SQL guardrails ({len(failures)} problem(s) across {checks} checks):")
        for f in failures:
            print(f"  - {f}")
        return 1

    print(f"PASS  SQL guardrails ({len(fixtures)} negative fixtures asserting {expectations} "
          f"distinct rules, plus the positive control and the empty-input case)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
