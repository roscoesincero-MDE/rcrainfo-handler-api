#!/usr/bin/env python
"""Negative test for build/check_closed_set_filters.py.

A gate that has only ever passed has not been shown to be able to fail. Each mutation below breaks
one of the four things the gate claims to check, in the FILE only, runs the gate, and puts the file
back. Every mutation must be reported, and each must be reported for the reason it was made.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TARGET = REPO / "src" / "RCRAInfo.Database" / "Scripts" / "501_logs.uspGetHandlerLoadStatusPage.sql"
GATE = REPO / "build" / "check_closed_set_filters.py"

STATUS_BLOCK = re.compile(
    r"        IF \@Status IS NOT NULL\n.*?\n        END;\n\n", re.DOTALL)


def drop_status_block(text: str) -> str:
    return STATUS_BLOCK.sub("", text, count=1)


def wrong_value(text: str) -> str:
    return text.replace("N'Succeeded', N'Failed', N'Skipped')",
                        "N'Succeeded', N'Failed', N'Skiped')", 1)


def wrong_constraint(text: str) -> str:
    return text.replace("+ N'CK_logs_HandlerLoadStatus_Status allows:",
                        "+ N'CK_logs_LoadRun_Status allows:", 1)


def unknown_constraint(text: str) -> str:
    return text.replace("+ N'CK_logs_HandlerLoadStatus_Status allows:",
                        "+ N'CK_logs_HandlerLoadStatus_StatusCode allows:", 1)


def no_prose(text: str) -> str:
    return text.replace("+ N'CK_logs_HandlerLoadStatus_Status allows: Pending, InProgress, '",
                        "+ N'CK_logs_HandlerLoadStatus_Status. Use Pending, InProgress, '", 1)


def prose_drifts(text: str) -> str:
    return text.replace("+ N'Succeeded, Failed, Skipped. Rejected rather than returned as an empty '",
                        "+ N'Succeeded, Failed, Ignored. Rejected rather than returned as an empty '",
                        1)


MUTATIONS = [
    ("delete the @Status validation block entirely", drop_status_block,
     "never rejects a value outside that set"),
    ("misspell one value in the @Status NOT IN list", wrong_value,
     "Only in the procedure: {Skiped}"),
    ("name the wrong existing constraint in the message", wrong_constraint,
     "names CK_logs_LoadRun_Status, but the set it copies is CK_logs_HandlerLoadStatus_Status"),
    ("name a constraint that does not exist", unknown_constraint,
     "not a constraint in this database"),
    ("drop the required 'allows:' phrasing", no_prose,
     "does not contain 'CK_logs_HandlerLoadStatus_Status allows: <values>.'"),
    ("let the prose list drift from the NOT IN list", prose_drifts,
     "tells the operator to use"),
]


def run_gate() -> tuple[int, str]:
    result = subprocess.run([sys.executable, str(GATE), "--server", "."],
                            cwd=REPO, capture_output=True, text=True,
                            encoding="utf-8", errors="replace")
    return result.returncode, (result.stdout or "") + (result.stderr or "")


def main() -> int:
    original = TARGET.read_text(encoding="utf-8")

    code, output = run_gate()
    if code != 0:
        print("SETUP  the gate does not pass on the unmutated file, so a mutation proves nothing:")
        print(output)
        return 2

    failures = 0
    try:
        for label, mutate, expected in MUTATIONS:
            mutated = mutate(original)
            if mutated == original:
                print(f"UNTESTED  mutation did not apply: {label}")
                failures += 1
                continue

            TARGET.write_text(mutated, encoding="utf-8")
            code, output = run_gate()

            if code == 0:
                print(f"MISSED    {label}: the gate still passed.")
                failures += 1
            elif expected not in output:
                print(f"WRONG     {label}: the gate failed, but not for this reason.")
                print("          expected to see: " + expected)
                for line in output.splitlines():
                    print(f"          {line}")
                failures += 1
            else:
                print(f"caught    {label}")
    finally:
        TARGET.write_text(original, encoding="utf-8")

    code, output = run_gate()
    if code != 0:
        print("RESTORE   the file was not restored cleanly:")
        print(output)
        return 2

    print()
    print(f"{len(MUTATIONS) - failures} of {len(MUTATIONS)} mutation(s) caught for the right reason.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
