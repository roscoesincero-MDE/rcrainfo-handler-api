#!/usr/bin/env python
"""
D1 guardrail: no HttpClient registration in this solution keeps the stock HTTP logging.

EPA's auth endpoint is GET /api/v1/auth/{apiId}/{apiKey} -- both halves of the credential are PATH
SEGMENTS. Measured on this solution's package versions: one request through a stock
`services.AddHttpClient(...)` produces eight log entries, two of them at INFORMATION, and both print the
full request URI:

    Start processing HTTP request GET https://.../api/v1/auth/<API ID>/<API KEY>
    Sending HTTP request GET       https://.../api/v1/auth/<API ID>/<API KEY>

So the default configuration writes the API Key into whatever sink the host has attached. A third hazard
sits at TRACE, where the same logging writes request headers -- `Authorization: Bearer <token>` for every
data call. `IHttpClientBuilder.RemoveAllLoggers ()` removes all three, and the loader loses nothing it
wanted: its HTTP record is logs.HandlerLoadAttempt, which stores the PATH ONLY -- never a query string,
never a header -- because the monitoring web application can read that table (AR8).

This check exists because the leak is a MISSING call rather than a wrong one. Nothing about
`AddHttpClient("x", ConfigureX)` looks unsafe in a diff, and the day someone adds a second data client the
tests will still pass: RCRAInfo.Loader.Tests proves the two registrations that exist are silent, and can
say nothing about a third.

Exit 0 when every registration removes its loggers, 1 otherwise.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent

SCAN_ROOTS = ["src", "tools"]

SKIP_DIRECTORIES = {"bin", "obj", "lib", "node_modules", ".git", ".vs"}

# The registration and everything chained onto it, up to the semicolon that ends the statement. Chains in
# this solution span several lines, so the pattern is deliberately multiline and non-greedy.
REGISTRATION = re.compile(r"\.?\bAddHttpClient\b.*?;", re.DOTALL)

REQUIRED = "RemoveAllLoggers"

# `AddHttpClient` with no chained configuration at all -- `services.AddHttpClient ();` -- registers the
# factory itself and no named client, so there is nothing for it to log.
BARE = re.compile(r"^\.?AddHttpClient\s*\(\s*\)\s*;$", re.DOTALL)

SELF_TEST_MUST_MATCH = [
    'services.AddHttpClient("auth", Configure);',
    'services.AddHttpClient<IAuthClient, AuthClient>("auth").AddStandardResilienceHandler();',
    """services.AddHttpClient(Names.Data, ConfigureData)
            .AddHttpMessageHandler<ApiTokenHandler>()
            .AddStandardResilienceHandler();""",
]

SELF_TEST_MUST_NOT_MATCH = [
    'services.AddHttpClient("auth", Configure).RemoveAllLoggers();',
    """services.AddHttpClient(Names.Data, ConfigureData)
            .RemoveAllLoggers()
            .AddHttpMessageHandler<ApiTokenHandler>()
            .AddStandardResilienceHandler();""",
    # Registers the factory and no client.
    'services.AddHttpClient();',
    # Prose about the rule, in the file that implements it. A checker that reports the comment explaining
    # itself is a checker people learn to ignore.
    '// Both clients are registered with RemoveAllLoggers because AddHttpClient would log the URI;',
]


def without_comment_lines(text: str) -> str:
    """The text with whole comment lines blanked, keeping every line and every offset intact.

    Only lines that are ENTIRELY comment are blanked, and each is replaced by a line of the same length,
    so reported line numbers still match the file. A general C# comment stripper would have to know where
    string literals end, and getting that wrong here means either swallowing the `;` that terminates a
    registration -- turning one statement into two and losing a `RemoveAllLoggers` -- or reporting nothing
    at all. This file's own XML docs are the reason it is needed: they name both `AddHttpClient` and the
    rule, and a checker that reports the paragraph explaining itself is a checker people switch off.
    """
    return "\n".join(
        " " * len(line) if line.lstrip().startswith(("//", "/*", "*")) else line
        for line in text.split("\n"))


def offenders_in(text: str) -> list[tuple[int, str]]:
    """Every registration in one blob of text that does not remove its loggers, as (offset, excerpt)."""
    found: list[tuple[int, str]] = []

    for match in REGISTRATION.finditer(without_comment_lines(text)):
        statement = match.group(0)

        if REQUIRED in statement or BARE.match(statement.strip().lstrip(".")):
            continue

        excerpt = " ".join(statement.split())

        found.append((match.start(), excerpt[:120]))

    return found


def self_test() -> list[str]:
    failures = []

    for sample in SELF_TEST_MUST_MATCH:
        if not offenders_in(sample):
            failures.append(f"self-test: NOT detected but must be: {' '.join(sample.split())}")

    for sample in SELF_TEST_MUST_NOT_MATCH:
        if offenders_in(sample):
            failures.append(f"self-test: false positive on a safe registration: {' '.join(sample.split())}")

    return failures


def files_to_scan() -> list[Path]:
    found: list[Path] = []

    for root in SCAN_ROOTS:
        base = REPO / root
        if not base.is_dir():
            continue

        for path in base.rglob("*.cs"):
            if SKIP_DIRECTORIES & {part.lower() for part in path.relative_to(REPO).parts[:-1]}:
                continue
            found.append(path)

    return sorted(found)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    targets = files_to_scan()

    if not targets:
        # Examining nothing and reporting success is worse than not running, because it looks like evidence.
        print(f"FAIL  HttpClient logging: found no .cs files under {', '.join(SCAN_ROOTS)}. Either the "
              "roots moved or this check has stopped looking at anything.")
        return 1

    problems: list[str] = self_test()
    registrations = 0

    for path in targets:
        text = path.read_text(encoding="utf-8", errors="replace")
        registrations += len(REGISTRATION.findall(text))
        relative = path.relative_to(REPO).as_posix()

        for offset, excerpt in offenders_in(text):
            line = text.count("\n", 0, offset) + 1
            problems.append(
                f"{relative}:{line}: this registration does not call {REQUIRED} (), so "
                f"IHttpClientFactory will log the full request URI at Information and the request headers "
                f"at Trace -- for the auth call that is the API ID and Key, and for a data call the bearer "
                f"token: {excerpt}")

    if problems:
        print(f"FAIL  HttpClient logging ({len(problems)}):")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    samples = len(SELF_TEST_MUST_MATCH) + len(SELF_TEST_MUST_NOT_MATCH)
    print(f"PASS  HttpClient logging ({registrations} registration(s) in {len(targets)} files, {samples} "
          f"self-test samples classified; none can log a credential-bearing URI)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
