#!/usr/bin/env python
r"""
Writes the fully-populated handler payload the round-trip tests send, from the pinned EPA spec.

DA5's acceptance list says the round-trip test for each procedure must assert FIELD-LEVEL
NON-NULLITY on a fully-populated payload, and G32 is why: with a table-valued parameter a shape
mismatch fails loudly, but with JSON a renamed or mis-cased property shreds to NULL and the row
count is unchanged. So the test has to send every field and read every field back.

"Every field" is all 377 in-scope leaves: 210 columns of dbo.HandlerSource and 167 more across the
18 child tables. A hand-written fixture of 377 values would be a second copy of the mapping that
build/generate_schema.py already owns -- kept in step by hand, against a spec that EPA revises. The
failure would be quiet in exactly the way the test exists to catch: a field EPA adds appears in the
table and in script 400's shredding, and the fixture never mentions it, so the round-trip test
passes while that column has never once been populated.

This generator therefore imports build/generate_schema.py and walks the SAME Column lists that
produced both the tables and the merge procedure. There is one source of truth for the mapping and
this is downstream of it.

The child half is not a smaller version of the parent half, and the reason is worth stating. A
child column shreds through a nested CROSS APPLY OPENJSON over the array element, so its path is
relative to that element and a mis-cased segment there fails in a way NOTHING ELSE IN THE SUITE CAN
SEE: ChildCollectionTests asserts multiplicity and retirement -- order, shorter, empty, absent,
revive, grandchild parentage -- and it does so through hand-written elements carrying one or two
properties apiece. Those tests would pass unchanged with 165 of the 167 child columns permanently
null. Field-level coverage of the children is exactly the gap this file closes.

What it writes: one JSON file with three parts.

  handler       EPA's payload shape, nested, with a value at every one of the 377 leaf paths --
                the 210 scalars and one element in each of the 18 collections, grandchild arrays
                nested inside their own parent element. This is what goes into
                HandlerEnvelope.Handler and reaches OPENJSON verbatim.
  columns       the parent manifest -- for each column, its name, its JSON path, its SQL type and
                the value the round-trip is expected to read back. The test needs the expected
                value, not just "not null", because a truncated NVARCHAR is not null either (G36:
                OPENJSON ... WITH truncates SILENTLY, with no warning and no error).
  collections   the child manifest -- one entry per collection, carrying the table it lands in, the
                table and column it keys to, and the same per-column detail. The parentage is
                emitted rather than inferred because three of the 18 hang off another child, and a
                test that guessed the join could read a grandchild row belonging to the wrong
                element and call it a pass.

ONE element per collection, not two. Multiplicity is ChildCollectionTests' subject and is covered
there against payloads written for it; here a second element would double 167 assertions to prove
nothing new, because a mis-cased path shreds to NULL in element 0 exactly as in element 1. The
single element also means every ordinal in the manifest is 0, so the round-trip's query needs no
ordinal bookkeeping and cannot silently read the wrong row.

Three properties of the generated values, each chosen against a specific way the test could pass
while proving nothing:

  Every NVARCHAR value is EXACTLY the column's declared width, and ends in 'Z'. A value shorter
  than the column can never detect truncation, and it is the widest values that truncate. The
  terminal sentinel means a loss of even one character shows up in the value as well as in its
  length, so the failure message names the defect rather than a length mismatch.

  Values are DISTINCT per column wherever the type has room for it -- the NVARCHAR values carry
  their column's ordinal, the dates are consecutive, the numbers are the ordinal itself. Two
  columns holding the same value cannot detect a swapped pair of paths, and a swap between two
  adjacent generated paths is precisely what a generator bug looks like. The ordinal runs
  CONTINUOUSLY across the parent and then the children, so a child column mis-shredded from the
  parent's copy of the same property name -- `type.code' exists at both levels -- comes back with
  the other level's value rather than with something indistinguishable.

  BIT alternates by ordinal instead of being uniformly true, for the same reason: 63 columns all
  set to true would agree just as happily with a shredding that hardcoded one.

Usage:

  python build/generate_payload_fixture.py           write the fixture
  python build/generate_payload_fixture.py --check    fail if the file on disk is not current

--check is a guardrail (build/guardrails.py). It needs no server: the spec is pinned in the
repository and the fixture is derived from it alone.
"""

from __future__ import annotations

import argparse
import difflib
import importlib.util
import json
import re
import sys
from datetime import date, timedelta
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
FIXTURE = (REPO / "tests" / "RCRAInfo.Data.Integration.Tests" / "Fixtures"
           / "FullHandlerSource.json")

# --------------------------------------------------------------------------------------------------
# The reserved test identity.
#
# ZZ is not a state, so EPA can never issue a handler in it and no real row can ever collide with
# these. That matters more here than it would elsewhere: THERE IS NO HARD DELETE ANYWHERE IN THIS
# DATABASE, so every row the round-trip writes is permanent. A test corpus that cannot be removed
# has to be one a human can recognise on sight and a query can separate exactly.
#
# HandlerId is NVARCHAR (12) and 'ZZTEST000001' is exactly 12, so truncation is still detectable on
# the key itself.
# --------------------------------------------------------------------------------------------------
TEST_HANDLER_ID = "ZZTEST000001"
TEST_ACTIVITY_LOCATION = "ZZ"
TEST_SOURCE_TYPE = "Z"
TEST_SEQUENCE = 1

# Values that are not free to be generated, and why. Keyed by JSON path.
OVERRIDES = {
    "handlerId": TEST_HANDLER_ID,
    "activityLocation": TEST_ACTIVITY_LOCATION,
    "type.code": TEST_SOURCE_TYPE,
    "sequence": TEST_SEQUENCE,

    # Forced true rather than left to the BIT alternation. This flag decides whether the version is
    # the current one, and dbo.vwHandlerSource -- which the grid and the search read through --
    # returns current versions only. A fixture that happened to land on false would make the row
    # invisible to most of the reads being round-tripped, and the tests would read as a mapping
    # failure rather than as a fixture that asked for it.
    "currentRecord": True,
}

# The first date. Each DATE column gets this plus its ordinal in days, so all ten differ.
FIRST_DATE = date(2026, 1, 1)

NVARCHAR_WIDTH = re.compile(r"NVARCHAR \((\d+)\)\Z")


def load_generator():
    """Imports build/generate_schema.py as a module."""
    path = REPO / "build" / "generate_schema.py"
    loader = importlib.util.spec_from_file_location("generate_schema", path)
    module = importlib.util.module_from_spec(loader)

    # Registered before exec on purpose: @dataclass resolves its own annotations through
    # sys.modules[cls.__module__], so a module that is not registered raises inside dataclasses
    # rather than anywhere that names the cause.
    sys.modules["generate_schema"] = module
    loader.loader.exec_module(module)
    return module


def payload_tables(generator):
    """The parent table and the 18 child tables, from the one build the schema itself comes from."""
    spec_module, spec = generator.load_spec()
    descriptions = generator.leaf_descriptions(spec)

    return (generator.build_parent(spec, spec_module, descriptions),
            generator.build_children(spec, spec_module, descriptions))


def payload_columns(table):
    """The columns of one table that come from EPA's payload, in table order."""
    return [c for c in table.columns if c.json_path]


def text_value(width: int, ordinal: int) -> str:
    """A string of exactly `width` characters, carrying its ordinal and ending in a sentinel."""
    if width < 1:
        raise SystemExit(f"FAIL  generate_payload_fixture: NVARCHAR ({width}) is not a width.")
    if width == 1:
        return "Z"

    # 'A0042------Z': the leading ordinal makes the value distinct per column, the trailing 'Z' makes
    # truncation visible in the value. Narrow columns lose the ordinal before they lose the sentinel,
    # because a sentinel that a 2-character column cannot carry would leave that column unchecked.
    head = f"A{ordinal:04d}"[: width - 1]
    return head + "-" * (width - 1 - len(head)) + "Z"


def generated_value(column, ordinal: int):
    """The value for one column, by SQL type."""
    sql = column.sql_type

    match = NVARCHAR_WIDTH.match(sql)
    if match:
        return text_value(int(match.group(1)), ordinal)

    if sql == "BIT":
        # Alternating, not uniform -- see the module docstring.
        return ordinal % 2 == 0

    if sql in ("INT", "BIGINT"):
        return ordinal

    if sql == "FLOAT":
        # A fraction, so a column mapped to INT by mistake loses something a test can see.
        return ordinal + 0.5

    if sql == "DATE":
        return (FIRST_DATE + timedelta(days=ordinal)).isoformat()

    raise SystemExit(
        f"FAIL  generate_payload_fixture: no fixture value for {column.name} of type {sql!r}. "
        f"Add the type here deliberately; refusing to emit a value whose round-trip semantics "
        f"this generator has not been told about.")


def nest(values: dict[str, object]) -> dict:
    """Turns {'type.code': 'Z'} into {'type': {'code': 'Z'}}."""
    root: dict = {}

    for path, value in values.items():
        segments = path.split(".")
        node = root

        for segment in segments[:-1]:
            existing = node.get(segment)
            if existing is None:
                existing = {}
                node[segment] = existing
            elif not isinstance(existing, dict):
                # A leaf and an object at the same path. The spec does not do this today; if it
                # starts, the merge procedure has the same problem and both should be looked at.
                raise SystemExit(
                    f"FAIL  generate_payload_fixture: path {path!r} needs {segment!r} to be an "
                    f"object, but another column already claims it as a leaf.")
            node = existing

        leaf = segments[-1]
        if leaf in node:
            raise SystemExit(
                f"FAIL  generate_payload_fixture: two columns map to the JSON path {path!r}.")
        node[leaf] = value

    return root


def attach(handler: dict, path: str, element: object) -> None:
    """Puts a collection's single element, as a one-element array, at its spec path in `handler`.

    `path` is the collection's path as the spec spells it, so a grandchild carries its owner's
    subscript: 'hsm.activities[].wasteCodes'. A '[]' segment is therefore an instruction to descend
    into an ALREADY-ATTACHED element rather than to create anything -- which is sound only because
    CHILD_ORDER places every owner ahead of what it owns, and generate_schema.py asserts that.
    """
    node: object = handler
    segments = path.split(".")

    for segment in segments[:-1]:
        if segment.endswith("[]"):
            owner = node.get(segment[:-2]) if isinstance(node, dict) else None

            if not isinstance(owner, list) or not owner:
                raise SystemExit(
                    f"FAIL  generate_payload_fixture: {path!r} needs the collection "
                    f"{segment[:-2]!r} to have been attached first. CHILD_ORDER places an owner "
                    f"before what it owns; if that has changed, the merge procedure's grandchild "
                    f"threading has the same problem.")

            node = owner[0]
        else:
            if not isinstance(node, dict):
                raise SystemExit(
                    f"FAIL  generate_payload_fixture: {path!r} needs {segment!r} to be an object.")

            node = node.setdefault(segment, {})

    if not isinstance(node, dict):
        raise SystemExit(f"FAIL  generate_payload_fixture: {path!r} does not land inside an object.")

    key = segments[-1]

    # A collection name that is also a scalar leaf would mean the parent table and a child table are
    # both claiming one property. generate_schema.py could not have emitted that, so finding it here
    # means this function walked somewhere it should not have.
    if key in node:
        raise SystemExit(
            f"FAIL  generate_payload_fixture: {path!r} is already occupied by "
            f"{type(node[key]).__name__}.")

    node[key] = [element]


def column_entry(column, ordinal: int, value: object) -> dict:
    """One manifest row: what to look for, where it came from, and what it must equal."""
    return {
        "name": column.name,
        "path": column.json_path,
        "sqlType": column.sql_type,
        "nullable": column.nullable,
        "ordinal": ordinal,
        "expected": value,
    }


def build_collection(generator, path: str, child, next_ordinal: int) -> tuple[dict, object, int]:
    """One collection's manifest entry, its single JSON element, and the ordinal to carry on from."""
    columns = payload_columns(child)

    if not columns:
        raise SystemExit(
            f"FAIL  generate_payload_fixture: {child.name} has no payload columns, so nothing in "
            f"the fixture would populate it.")

    bare = len(columns) == 1 and columns[0].json_path == generator.BARE_ARRAY_JSON_PATH
    entries: list[dict] = []
    values: dict[str, object] = {}

    for offset, column in enumerate(columns):
        ordinal = next_ordinal + offset
        value = generated_value(column, ordinal)
        entries.append(column_entry(column, ordinal, value))

        if not bare:
            values[column.json_path] = value

    # A bare-string array's element IS the value -- there is no property to nest it under, which is
    # what BARE_ARRAY_JSON_PATH ('$') means and why 5 of the 18 shred through arr.[value] alone.
    element: object = entries[0]["expected"] if bare else nest(values)

    return ({
        "path": path,
        "table": child.name,
        "identityColumn": child.identity_column,
        "parentTable": child.parent_table,
        "parentColumn": child.parent_column,
        "bare": bare,
        "columns": entries,
    }, element, next_ordinal + len(columns))


def build() -> str:
    generator = load_generator()
    parent, children = payload_tables(generator)
    columns = payload_columns(parent)

    if not columns:
        raise SystemExit("FAIL  generate_payload_fixture: the parent table has no payload columns.")

    if not children:
        raise SystemExit(
            "FAIL  generate_payload_fixture: no child tables. The 18 collections carry 167 of the "
            "377 in-scope leaves, and a fixture that covered none of them would leave the child "
            "shredding entirely unasserted.")

    values: dict[str, object] = {}
    manifest: list[dict] = []

    for ordinal, column in enumerate(columns, start=1):
        path = column.json_path

        if path in OVERRIDES:
            value = OVERRIDES[path]
        else:
            value = generated_value(column, ordinal)

        values[path] = value
        manifest.append(column_entry(column, ordinal, value))

    handler = nest(values)
    collections: list[dict] = []
    next_ordinal = len(columns) + 1

    # Table has no spec path on it -- build_children takes the path from CHILD_ORDER and keeps only
    # the table name -- so the pairing is recovered here from the same two decisions it was made
    # from, and asserted rather than assumed.
    by_name = {child.name: child for child in children}

    # In CHILD_ORDER, which is also the order the tables are created in and the order the merge
    # shreds them: owners before what they own, so attach() can descend into an existing element.
    for path in generator.CHILD_ORDER:
        name = generator.CHILD_TABLE_NAMES[path]
        child = by_name.get(name)

        if child is None:
            raise SystemExit(
                f"FAIL  generate_payload_fixture: CHILD_ORDER names {path!r} -> {name}, which "
                f"build_children did not produce.")

        entry, element, next_ordinal = build_collection(generator, path, child, next_ordinal)
        attach(handler, path, element)
        collections.append(entry)

    if len(collections) != len(children):
        raise SystemExit(
            f"FAIL  generate_payload_fixture: {len(children)} child table(s) but "
            f"{len(collections)} collection(s) in the fixture.")

    document = {
        "$comment":
            "GENERATED FILE -- do not edit by hand. Written by "
            "build/generate_payload_fixture.py from spec/rcrainfo/swagger.json, the same pinned "
            "spec that generated dbo.HandlerSource, its 18 child tables and "
            "dbo.uspMergeHandlerSourceBatch. build/generate_payload_fixture.py --check fails when "
            "this file is not current.",
        "handlerId": TEST_HANDLER_ID,
        "activityLocation": TEST_ACTIVITY_LOCATION,
        "sourceType": TEST_SOURCE_TYPE,
        "sequence": TEST_SEQUENCE,
        "columnCount": len(manifest),
        "collectionCount": len(collections),
        "collectionColumnCount": sum(len(c["columns"]) for c in collections),
        "payloadColumnCount": next_ordinal - 1,
        "handler": handler,
        "columns": manifest,
        "collections": collections,
    }

    return json.dumps(document, indent=2, ensure_ascii=True) + "\n"


def main(argv: list[str]) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    parser = argparse.ArgumentParser(
        description="Generates the fully-populated handler payload fixture.",
        epilog=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true",
                        help="fail if the fixture on disk differs from what would be generated")
    args = parser.parse_args(argv)

    body = build()
    document = json.loads(body)

    described = (f'{document["payloadColumnCount"]} payload column(s) -- '
                 f'{document["columnCount"]} on dbo.HandlerSource and '
                 f'{document["collectionColumnCount"]} across '
                 f'{document["collectionCount"]} child table(s)')

    if args.check:
        if not FIXTURE.is_file():
            print(f"FAIL  generate_payload_fixture: {FIXTURE.relative_to(REPO)} is missing. Run "
                  f"python build/generate_payload_fixture.py.")
            return 1

        on_disk = FIXTURE.read_text(encoding="utf-8")

        if on_disk != body:
            diff = [d for d in difflib.unified_diff(
                on_disk.splitlines(), body.splitlines(),
                "on disk", "generated", lineterm="", n=0) if d[:1] in "+-" and d[:3] not in ("+++", "---")]
            print(f"FAIL  generate_payload_fixture: {FIXTURE.relative_to(REPO)} differs from the "
                  f"generator ({len(diff)} changed line(s)); first change: "
                  f"{diff[0] if diff else '?'}")
            print("       The fixture is generated from spec/rcrainfo/swagger.json. Change the")
            print("       generator, not the output, then re-run it and commit both.")
            return 1

        print(f"PASS  generate_payload_fixture: the fixture covers all {described}, and matches "
              f"the generator.")
        return 0

    if FIXTURE.is_file() and FIXTURE.read_text(encoding="utf-8") == body:
        print(f"generate_payload_fixture: already current ({described}).")
        return 0

    FIXTURE.parent.mkdir(parents=True, exist_ok=True)
    FIXTURE.write_text(body, encoding="utf-8", newline="\n")
    print(f"generate_payload_fixture: wrote {FIXTURE.relative_to(REPO)} ({described}).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
