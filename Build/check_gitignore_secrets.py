#!/usr/bin/env python
"""
A1 guardrail: prove .gitignore actually excludes the secret-bearing files.

AR4 puts a plaintext SQL password inside appsettings.json and has the application rewrite the
file in place to encrypt it. A secret committed once is committed forever, so the exclusion is
verified mechanically rather than trusted.

This asks git itself (`git check-ignore`) rather than reading .gitignore and reasoning about it,
because the question is what git does, not what the file appears to say. Nothing is written to
the working tree: paths are hypothetical.

Exit 0 when every path is classified correctly, 1 otherwise.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

# Paths that MUST be ignored. AR4's own file, plus the shapes it takes per project and per
# environment, plus certificate material and the Claude Code local override.
MUST_IGNORE = [
    "appsettings.json",
    "src/RCRAInfo.Loader/appsettings.json",
    "src/RCRAInfo.Monitor/appsettings.json",
    "src/RCRAInfo.Loader/appsettings.Development.json",
    "src/RCRAInfo.Loader/appsettings.Production.json",
    "src/RCRAInfo.Monitor/appsettings.Production.json",
    "secrets.json",
    "src/RCRAInfo.Core/secrets.json",
    "certs/client.pfx",
    "certs/client.p12",
    ".claude/settings.local.json",
    "src/RCRAInfo.Core/bin/Debug/net10.0/RCRAInfo.Core.dll",
    "src/RCRAInfo.Core/obj/project.assets.json",
    # The RCRAInfo API ID and Key. RCRAInfo shows the key ONCE, at generation, so it lands in
    # whatever text file is nearest -- and a 93-byte apiKey.txt was found sitting in the repo root,
    # untracked and unignored, one `git add -A` from being published. The .gitignore entry is broad
    # because the next one will be named slightly differently; these paths assert the breadth,
    # since an entry nothing tests is an entry a later tidy-up removes.
    "apiKey.txt",
    "apikey.json",
    "src/RCRAInfo.Loader/api-key.txt",
    "build/api_key.txt",
    # The two dev SQL logins. The move to a password manager is a human step that is still open, so
    # the file is still on disk and must stay out of the tree while it is.
    "dev-credentials.json",
]

# Paths that MUST NOT be ignored. A .gitignore rule broad enough to swallow these has stopped
# protecting secrets and started hiding source.
MUST_TRACK = [
    "CLAUDE.md",
    "Phase1-Plan.md",
    ".gitignore",
    # The templates AR4's seeding step tells the operator to copy. They must ship, so an exclusion
    # broad enough to catch them silently removes the instructions from the deployment. The name was
    # appsettings.Template.json until the credential file stopped being an appsettings file at all --
    # deliberately, so that AddJsonFile("appsettings.{env}.json") cannot pick it up by convention --
    # and this list went on asserting the old name, which is not ignored because nothing of that name
    # exists. An assertion about an absent file passes forever.
    "src/RCRAInfo.Loader/secrets.Template.json",
    "src/RCRAInfo.Monitor/secrets.Template.json",
    "src/RCRAInfo.Loader/RCRAInfo.Loader.csproj",
    "src/RCRAInfo.Database/Scripts/010_Database.sql",
    ".claude/settings.json",
    ".claude/hooks/validate-sql.py",
    "build/check_gitignore_secrets.py",
]


def is_ignored(path: str) -> bool:
    """Ask git whether it would ignore this path. --no-index so the answer does not depend on
    whether the file is already tracked, and check-ignore does not require the file to exist."""
    result = subprocess.run(
        ["git", "check-ignore", "--no-index", "-q", "--", path],
        cwd=REPO, capture_output=True, text=True,
    )
    # 0 = ignored, 1 = not ignored, 128 = error (not a repo, bad args)
    if result.returncode not in (0, 1):
        raise RuntimeError(
            f"git check-ignore failed for {path!r} "
            f"(exit {result.returncode}): {result.stderr.strip()}")
    return result.returncode == 0


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    failures: list[str] = []

    for path in MUST_IGNORE:
        try:
            if not is_ignored(path):
                failures.append(f"NOT IGNORED but must be: {path}")
        except RuntimeError as exc:
            failures.append(str(exc))

    for path in MUST_TRACK:
        # Existence matters on this list and not on the other. A MUST_IGNORE path is hypothetical on
        # purpose -- the point is that git WOULD exclude a password file, and creating one to find out
        # would be the mistake. But "this path is not ignored" is trivially true of a path that does
        # not exist, so a MUST_TRACK entry naming a renamed or deleted file keeps passing while
        # asserting nothing. That is not hypothetical either: the entry for
        # appsettings.Template.json outlived the file by the whole of Workstream C.
        if not (REPO / path).exists():
            failures.append(
                f"LISTED but absent: {path}. An entry naming a file that does not exist passes "
                f"forever without checking anything -- rename it or remove it.")
            continue

        try:
            if is_ignored(path):
                failures.append(f"IGNORED but must be tracked: {path}")
        except RuntimeError as exc:
            failures.append(str(exc))

    checked = len(MUST_IGNORE) + len(MUST_TRACK)
    if failures:
        print(f"FAIL  .gitignore secret exclusion ({len(failures)} of {checked} paths wrong):")
        for f in failures:
            print(f"  - {f}")
        return 1

    print(f"PASS  .gitignore secret exclusion "
          f"({len(MUST_IGNORE)} ignored, {len(MUST_TRACK)} tracked, {checked} paths checked)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
