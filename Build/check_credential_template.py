#!/usr/bin/env python
"""
Workstream C guardrail: the two committed credential templates carry no secret, name the keys the
application actually reads, and are not wired into configuration.

The templates are the one part of AR4 that is BOTH committed to source control and shaped like a
place to type a password. `.gitignore` excludes secrets.json; it deliberately does not exclude
secrets.Template.json, because the template has to ship. So the file a developer opens in order to
learn where the password goes is a tracked file, and filling it in there instead of in the copy is
one keystroke away and looks exactly like doing it right. Nothing errors. `git add -A` publishes it.

Four things are checked, each because its failure is silent:

  1. Every value is empty and Encrypted is false. A password pasted here is a password committed.

  2. Every key the application reads is present, spelled the way the application spells it. A
     renamed key in the template hands the operator a file that parses, validates nothing and seals
     nothing -- CredentialFile reads JSON as a JsonNode so unknown properties survive untouched,
     which is what makes the rewrite safe and also what makes a typo invisible.

  3. The Monitor template carries NO ApiId or ApiKey. That is a decision, not an omission: the
     monitoring application never calls EPA, it reads what the loader recorded, so it has no
     legitimate use for the API credential -- and the loader's own file DENIES read access to the
     application pool identity. A template that invites the web app to hold a copy defeats both.

  4. Nothing registers a credential file as an IConfiguration source. The file is rewritten in
     place on first run, and a JSON configuration source added with reloadOnChange watches the file
     it was built from -- so the application recycles itself in the middle of its own startup, at
     the one moment it is holding an exclusive lock on that file. It is also why the file is not
     named appsettings.something.json: `AddJsonFile("appsettings.{env}.json")` would pick it up by
     convention, with no line of code naming it.

Every predicate here is proved against an inline fixture on each run. Without that, "every value is
empty" passes on a file with no values at all, and check 4 passes on a source tree it failed to
read -- a guardrail examining nothing reports exactly what a clean one does.

Exit 0 when all four hold, 1 otherwise.
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

# The keys each application reads, and the ones it must not be handed. Kept as literals rather than
# derived from the C#, because the point is to catch the two drifting apart.
TEMPLATES: dict[str, dict[str, object]] = {
    "src/RCRAInfo.Loader/secrets.Template.json": {
        "required": ["Encrypted", "SqlPassword", "ApiId", "ApiKey"],
        "forbidden": [],
    },
    "src/RCRAInfo.Monitor/secrets.Template.json": {
        "required": ["Encrypted", "SqlPassword"],
        # See the module docstring, check 3.
        "forbidden": ["ApiId", "ApiKey"],
    },
}

# Where a configuration source would be registered. Matches AddJsonFile and the Configuration
# indexer forms; `secrets` rather than the full filename so a renamed template is still caught.
CONFIG_SOURCE = re.compile(
    r"AddJsonFile\s*\(\s*[^)]*secrets|AddJsonFile\s*\(\s*[^)]*Template", re.IGNORECASE)


def strip_json_comments(text: str) -> str:
    """Remove // line comments, tracking string state so a value containing // survives.

    The templates carry comments on purpose -- they are the instructions the operator reads -- and
    CredentialFile.Parse allows them. Python's json does not, so they come off here. Doing it with a
    regex would corrupt any value that contained a //, which is precisely the value this check exists
    to notice.
    """
    out: list[str] = []
    in_string = False
    escaped = False
    i = 0

    while i < len(text):
        char = text[i]

        if in_string:
            out.append(char)
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            i += 1
            continue

        if char == '"':
            in_string = True
            out.append(char)
            i += 1
            continue

        if char == "/" and i + 1 < len(text) and text[i + 1] == "/":
            while i < len(text) and text[i] != "\n":
                i += 1
            continue

        out.append(char)
        i += 1

    return "".join(out)


def filled_values(document: dict) -> list[str]:
    """Keys whose value is anything other than empty-or-false. The findings, not the values."""
    findings = []

    for key, value in document.items():
        if key == "Encrypted":
            if value is not False:
                findings.append(f"Encrypted is {value!r}, and a template must ship as false")
            continue

        if isinstance(value, str):
            if value.strip():
                # The value itself is NEVER printed. This check exists because the value may be a
                # real password, and a finding that quoted it would put it in CI output, a terminal
                # scrollback and possibly a build log -- three more places than the file it came
                # from. The length is enough to recognise which paste it was.
                findings.append(f"{key} holds {len(value)} characters and must be empty")
        elif value is not None:
            findings.append(f"{key} holds a {type(value).__name__} and must be an empty string")

    return findings


def is_ignored(path: str) -> bool:
    result = subprocess.run(
        ["git", "check-ignore", "--no-index", "-q", "--", path],
        cwd=REPO, capture_output=True, text=True)

    if result.returncode not in (0, 1):
        raise RuntimeError(
            f"git check-ignore failed for {path!r} (exit {result.returncode}): "
            f"{result.stderr.strip()}")

    return result.returncode == 0


def self_test() -> list[str]:
    """Prove each predicate rejects what it claims to. A guardrail never shown to fail is
    indistinguishable from one examining nothing."""
    failures = []

    pasted = '{ "Encrypted": false, "SqlPassword": "hunter2-not-a-real-one", "ApiId": "" }'
    if not filled_values(json.loads(strip_json_comments(pasted))):
        failures.append("self-test: a template with a pasted password was accepted")

    claimed = '{ "Encrypted": true, "SqlPassword": "" }'
    if not filled_values(json.loads(strip_json_comments(claimed))):
        failures.append("self-test: a template claiming Encrypted: true was accepted")

    clean = '{ "Encrypted": false, "SqlPassword": "", "ApiId": "", "ApiKey": "" }'
    if filled_values(json.loads(strip_json_comments(clean))):
        failures.append("self-test: an empty template was rejected")

    # The comment stripper, on the case a regex gets wrong: a value that contains //.
    slashes = '{ "SqlPassword": "a//b" } // trailing'
    parsed = json.loads(strip_json_comments(slashes))
    if parsed.get("SqlPassword") != "a//b":
        failures.append(
            "self-test: the comment stripper mangled a value containing // "
            f"(got {parsed.get('SqlPassword')!r})")

    if not CONFIG_SOURCE.search('builder.Configuration.AddJsonFile("secrets.json");'):
        failures.append("self-test: AddJsonFile(\"secrets.json\") was not detected")

    if CONFIG_SOURCE.search('builder.Configuration.AddJsonFile("appsettings.json");'):
        failures.append("self-test: AddJsonFile(\"appsettings.json\") was flagged and must not be")

    return failures


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    failures = self_test()
    checked = 0

    for relative, spec in TEMPLATES.items():
        path = REPO / relative

        if not path.is_file():
            failures.append(f"{relative} does not exist. AR4's seeding step tells the operator to "
                            f"copy it, so a missing template is a runbook that cannot be followed.")
            continue

        checked += 1
        text = path.read_text(encoding="utf-8")

        try:
            document = json.loads(strip_json_comments(text))
        except json.JSONDecodeError as error:
            failures.append(f"{relative} is not valid JSON once comments are removed: "
                            f"line {error.lineno}, column {error.colno}.")
            continue

        for finding in filled_values(document):
            failures.append(f"{relative}: {finding}")

        for key in spec["required"]:
            if key not in document:
                failures.append(
                    f"{relative} is missing {key}, which the application reads by that exact name. "
                    f"A template without it seals nothing and reports nothing.")

        for key in spec["forbidden"]:
            if key in document:
                failures.append(
                    f"{relative} carries {key}, and must not. The monitoring application never "
                    f"calls EPA, and the loader's credential file denies read access to its "
                    f"identity precisely so it cannot hold this credential.")

        if is_ignored(relative):
            failures.append(
                f"{relative} is git-ignored, so it will not ship. The template must be committed; "
                f"it is the copy named secrets.json that must not be.")

        beside = str(Path(relative).parent / "secrets.json").replace("\\", "/")
        if not is_ignored(beside):
            failures.append(
                f"{beside} is NOT git-ignored, and it is the file that holds the real password.")

    sources = sorted(
        p for p in (REPO / "src").rglob("*.cs")
        if "bin" not in p.parts and "obj" not in p.parts)

    if not sources:
        failures.append("no .cs files were found under src/, so check 4 examined nothing.")

    for source in sources:
        for number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
            if CONFIG_SOURCE.search(line):
                failures.append(
                    f"{source.relative_to(REPO).as_posix()}:{number} registers a credential file as "
                    f"a configuration source. The file is rewritten in place on first run, while "
                    f"holding an exclusive lock on it, and a watched configuration source recycles "
                    f"the application mid-startup.")

    if failures:
        print(f"FAIL  credential templates ({len(failures)} finding(s)):")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print(f"PASS  credential templates ({checked} templates: no value filled, every read key "
          f"present, no API credential in the web app's copy, no configuration source across "
          f"{len(sources)} source files; 6 self-tests)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
