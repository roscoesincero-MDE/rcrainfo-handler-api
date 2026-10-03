"""One-off code generator for 505's projection list.

505_dbo.uspGetHandlerSourceDetail.sql projects 215 of dbo.vwHandlerSourceHistory's 218 columns, and typing
215 names by hand invites exactly the transcription error that no test would catch. So the list is generated
ONCE from sys.columns, in column_id order, and pasted into the script -- after which the script is
hand-maintained like any other 5xx file.

WHAT KEEPS THE PASTED LIST HONEST IS NOT THIS FILE. build/check_detail_projection.py compares 505's
projection against the catalog on every guardrail run and FAILS when the generator adds a column 505 does
not return. This generator is a convenience; that check is the guarantee. Do not wire this into
build/guardrails.py -- 505 is hand-authored, and a 5xx script the generator rewrites would be a 4xx script
in the wrong range.

Usage:  python build/tmp/gen505_projection.py
Writes: build/tmp/gen505_projection.txt   -- the `     , v.Name` lines for the procedure
        build/tmp/gen505_probeshape.txt   -- the `     , v.Name` lines for the probe's SELECT TOP (0) INTO
"""
from __future__ import annotations

import pathlib
import subprocess
import sys

# Excluded, and each for a reason that is a property of the VIEW rather than a preference:
#   IsDeleted            -- the view's own WHERE is IsDeleted = 0, so it is a provable constant 0
#   auditDeletedBy       -- masked to NULL by the view unless the row is deleted, which it cannot be here
#   auditDeletedDateUtc  -- same
# Returning a column that is provably constant on every row this procedure can reach is noise, and noise in
# a 215-column payload is worse than noise in a 22-column one.
EXCLUDED = ("IsDeleted", "auditDeletedBy", "auditDeletedDateUtc")

QUERY = """
SET NOCOUNT ON;
SELECT c.name
  FROM sys.columns AS c
 WHERE c.object_id = OBJECT_ID (N'dbo.vwHandlerSourceHistory')
   AND c.name NOT IN (N'IsDeleted', N'auditDeletedBy', N'auditDeletedDateUtc')
 ORDER BY c.column_id;
"""


def main() -> int:
    result = subprocess.run(
        ["sqlcmd", "-S", "localhost", "-E", "-C", "-d", "RCRAInfo", "-h", "-1", "-W", "-Q", QUERY],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if result.returncode != 0:
        sys.stderr.write(result.stdout + result.stderr)
        return 1

    names = [line.strip() for line in result.stdout.splitlines() if line.strip()]
    names = [n for n in names if n not in EXCLUDED and not n.startswith("(")]
    if len(names) != 215:
        sys.stderr.write(f"expected 215 columns, got {len(names)}:\n" + "\n".join(names[:10]) + "\n")
        return 1

    out = pathlib.Path(__file__).parent
    # The procedure's projection: a complete SELECT list, no interleaved comments. Commentary goes in the
    # block above the SELECT in 505 rather than between the columns, so that check_detail_projection.py has
    # 215 uniform lines to parse and cannot be defeated by a comma inside a comment.
    lines = ["        SELECT v." + names[0]] + [f"             , v.{n}" for n in names[1:]]
    (out / "gen505_projection.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")

    # The probe's shape table. SELECT TOP (0) ... INTO copies the exact types out of the view, which is the
    # only way to get 215 declarations right without typing them.
    #
    # ISNULL on the first column is NOT decoration. SELECT ... INTO propagates the IDENTITY property
    # through the view -- measured: tempdb.sys.columns reports is_identity = 1 on the copy -- and
    # INSERT ... EXEC supplies no column list, so the insert would fail with error 8101 instead of
    # asserting anything. Wrapping the column in an expression breaks the propagation while keeping the
    # type INT; HandlerSourceId is never NULL, so the 0 is unreachable.
    assert names[0] == "HandlerSourceId", "the ISNULL below assumes the first column is HandlerSourceId"
    probe = ["    SELECT TOP (0) ISNULL (v.HandlerSourceId, 0) AS HandlerSourceId"] \
        + [f"                 , v.{n}" for n in names[1:]]
    (out / "gen505_probeshape.txt").write_text("\n".join(probe) + "\n", encoding="utf-8")

    print(f"wrote {len(names)} column(s) to gen505_projection.txt and gen505_probeshape.txt")
    print(f"first: {names[0]}   last: {names[-1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
