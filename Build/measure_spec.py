#!/usr/bin/env python
r"""
Measures the pinned RCRAInfo swagger spec, and asserts it has not moved underneath the plan.

Every field count in Phase1-Analysis.md and Phase1-Plan.md -- 38 top-level properties, 444 leaf
fields, 24 repeating collections, the share of fields carrying an EPA description -- describes
`spec/rcrainfo/swagger.json`. Those numbers were originally typed by hand, and three of them were
wrong by the time anyone checked. So they are derived here instead, from the file, and compared
against a recorded baseline.

    python build/measure_spec.py             print the measurements
    python build/measure_spec.py --assert    fail if they differ from spec/rcrainfo/baseline.json
    python build/measure_spec.py --leaves    also list every leaf field and its type
    python build/measure_spec.py --lookups   also list the /lookup/hd/* response shapes

--assert is what runs in build/guardrails.py. It has two jobs, and the second is the important one:

  1. The spec file still hashes to the pinned value. EPA rebuilds this document -- info.version
     carries a build timestamp, and the preprod copy is routinely days ahead of production. A
     refetched spec is a legitimate thing to want; silently generating a schema from a different
     one is not.
  2. The derived counts still match. If a future edit changes the walk -- what counts as a leaf,
     which subtrees are in scope -- the counts move, and the documents quoting them go stale
     without anything failing. This makes that failure loud.

To adopt a newer spec: refetch it, run --write-baseline, read the diff the run prints, and update
the documents it names. The point is not that the numbers never change. It is that they never
change unnoticed.

SCOPE DECISIONS BAKED IN HERE (see Phase1-Plan.md, G14 / G16 / B-1):

  addendum.*             OMITTED. G16 named addendum.washington; the spec also has addendum.alabama
                         and addendum.wisconsin, and no addendum.maryland. See OMITTED_SUBTREES.
  episodic.*             IN SCOPE. G14 put the /episodic-events ENDPOINT out of scope, which is a
                         statement about the load schedule. HandlerSource.episodic is a subtree of
                         a payload we already fetch, so excluding it would discard data that costs
                         nothing to keep and would be expensive to retrofit.

Both are named constants below rather than a filter buried in the walk, because "why is this table
missing" is a question someone will ask in a year.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SPEC = REPO / "spec" / "rcrainfo" / "swagger.json"
BASELINE = REPO / "spec" / "rcrainfo" / "baseline.json"

ROOT_DEFINITION = "HandlerSource"

# Subtrees deliberately not modelled. Prefix match against the dotted path.
#
# G16 omitted addendum.washington by name. The spec has THREE state addenda -- alabama (3 leaf
# fields), wisconsin (30) and washington (34) -- and no maryland. So the whole `addendum` property is
# omitted on G16's own reasoning rather than only the branch G16 happened to name: activityLocation
# is MD only (G2), and there is no Maryland addendum for a Maryland handler to populate. That is 67
# of the 444 leaf fields, and one of the 38 top-level properties.
#
# If EPA ever adds addendum.maryland, --assert fails on the spec hash and the leaf counts, which is
# the whole point of the baseline: the day this exclusion stops being correct, something says so.
OMITTED_SUBTREES = ("addendum",)

LOOKUP_PREFIX = "/api/v1/lookup/hd/"


class Spec:
    """A Swagger 2.0 document with $ref resolution and a scalar/object/array walk."""

    def __init__(self, document: dict) -> None:
        self.document = document
        self.definitions = document["definitions"]

    def resolve(self, schema: dict) -> dict:
        """Follows $ref to the schema it names. Swagger 2.0 allows a chain, so this loops."""
        seen = set()
        while "$ref" in schema:
            name = schema["$ref"].rsplit("/", 1)[-1]
            if name in seen:
                raise ValueError(f"circular $ref through {name}")
            seen.add(name)
            schema = self.definitions[name]
        return schema

    def walk(self, root: str) -> dict:
        """
        Walks a definition and classifies every node.

        leaves       a property that resolves to a scalar -- one database column
        collections  an array -- one child table, keyed back to its parent
        objects      an object with properties -- flattened into its parent's table (B1)

        A leaf counts as described if EITHER the property site OR the definition it points at
        carries a `description`. Both forms appear in this document, and counting only one of them
        is how the 43% figure in the plan came to be wrong.
        """
        leaves: list[dict] = []
        collections: list[str] = []
        objects: list[str] = []

        def visit(schema: dict, path: str, site_description: str | None) -> None:
            resolved = self.resolve(schema)
            description = site_description or resolved.get("description")

            if resolved.get("type") == "array":
                collections.append(path)
                visit(resolved["items"], path + "[]", None)
                return

            if resolved.get("properties"):
                if path:
                    objects.append(path)
                for name, sub in resolved["properties"].items():
                    child = f"{path}.{name}" if path else name
                    visit(sub, child, sub.get("description"))
                return

            leaves.append({
                "path": path,
                "type": resolved.get("type"),
                "format": resolved.get("format"),
                "maxLength": resolved.get("maxLength"),
                "described": bool(description),
            })

        visit({"$ref": f"#/definitions/{root}"}, "", None)
        return {"leaves": leaves, "collections": collections, "objects": objects}

    def lookups(self) -> dict[str, dict]:
        """Maps each /lookup/hd/* path to the definition name its 200 response returns."""
        found = {}
        for path, methods in self.document["paths"].items():
            if not path.startswith(LOOKUP_PREFIX):
                continue
            get = methods.get("get")
            if not get:
                continue
            schema = get.get("responses", {}).get("200", {}).get("schema", {})
            if schema.get("type") == "array":
                schema = schema.get("items", {})
            name = schema.get("$ref", "").rsplit("/", 1)[-1] or None
            found[path] = {
                "definition": name,
                "parameters": [p.get("name") for p in get.get("parameters", [])],
            }
        return found


def in_scope(path: str) -> bool:
    return not path.startswith(OMITTED_SUBTREES)


def measure(spec: Spec) -> dict:
    walked = spec.walk(ROOT_DEFINITION)
    leaves = walked["leaves"]
    collections = walked["collections"]
    described = [leaf for leaf in leaves if leaf["described"]]
    root = spec.definitions[ROOT_DEFINITION]["properties"]

    scalars = [
        name for name, sub in root.items()
        if not spec.resolve(sub).get("properties") and spec.resolve(sub).get("type") != "array"
    ]

    scoped = [leaf for leaf in leaves if in_scope(leaf["path"])]
    scoped_described = [leaf for leaf in scoped if leaf["described"]]

    unbounded = [
        leaf["path"] for leaf in leaves
        if leaf["type"] == "string" and leaf["maxLength"] is None and leaf["format"] is None
        and in_scope(leaf["path"])
    ]

    return {
        "specSha256": hashlib.sha256(SPEC.read_bytes()).hexdigest(),
        "specVersion": spec.document["info"].get("version"),
        "paths": len(spec.document["paths"]),
        "definitions": len(spec.definitions),
        "topLevelProperties": len(root),
        "topLevelScalars": len(scalars),
        "leafFields": len(leaves),
        "describedLeafFields": len(described),
        "describedPercent": round(len(described) * 100 / len(leaves)),
        "leafFieldsInScope": len(scoped),
        "describedLeafFieldsInScope": len(scoped_described),
        "describedPercentInScope": round(len(scoped_described) * 100 / len(scoped)),
        "collections": len(collections),
        "collectionsInScope": len([c for c in collections if in_scope(c)]),
        "nestedObjects": len(walked["objects"]),
        "nestedObjectsInScope": len([o for o in walked["objects"] if in_scope(o)]),
        "lookupEndpoints": len(spec.lookups()),
        "lookupDefinitions": len({v["definition"] for v in spec.lookups().values()}),
        "unboundedStrings": len(unbounded),
    }


LABELS = {
    "specVersion": "spec info.version",
    "paths": "API paths",
    "definitions": "definitions",
    "topLevelProperties": "HandlerSource top-level properties",
    "topLevelScalars": "  ... of which scalar (dbo.HandlerSource columns)",
    "leafFields": "leaf fields (total)",
    "describedLeafFields": "  ... carrying an EPA description",
    "describedPercent": "  ... as a percentage",
    "leafFieldsInScope": "leaf fields in scope (columns to model)",
    "describedLeafFieldsInScope": "  ... carrying an EPA description",
    "describedPercentInScope": "  ... as a percentage (the B4 harvest)",
    "collections": "repeating collections (total)",
    "collectionsInScope": "  ... in scope (child tables)",
    "nestedObjects": "nested objects (flattened into parent)",
    "nestedObjectsInScope": "  ... in scope",
    "lookupEndpoints": "/lookup/hd/* endpoints",
    "lookupDefinitions": "  ... distinct response definitions",
    "unboundedStrings": "in-scope string leaves with no maxLength",
}


def main(argv: list[str]) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    parser = argparse.ArgumentParser(
        description="Measures the pinned RCRAInfo swagger spec.",
        epilog=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--assert", dest="assert_baseline", action="store_true",
                        help="fail if the spec or its measurements differ from the baseline")
    parser.add_argument("--write-baseline", action="store_true",
                        help="record the current measurements as the baseline")
    parser.add_argument("--leaves", action="store_true", help="list every leaf field")
    parser.add_argument("--collections", action="store_true", help="list every collection")
    parser.add_argument("--lookups", action="store_true", help="list the lookup response shapes")
    args = parser.parse_args(argv)

    if not SPEC.is_file():
        print(f"FAIL  measure_spec: {SPEC} does not exist. See spec/rcrainfo/README.md.")
        return 1

    spec = Spec(json.loads(SPEC.read_text(encoding="utf-8")))
    current = measure(spec)

    if args.write_baseline:
        BASELINE.write_text(json.dumps(current, indent=2) + "\n", encoding="utf-8")
        print(f"Baseline written to {BASELINE.relative_to(REPO)}.")

    for key, label in LABELS.items():
        print(f"  {label:<48} {current[key]}")

    if args.collections:
        walked = spec.walk(ROOT_DEFINITION)
        print("\nCollections:")
        for path in walked["collections"]:
            print(f"  {'omitted ' if not in_scope(path) else '        '}{path}")

    if args.leaves:
        print("\nLeaf fields:")
        for leaf in spec.walk(ROOT_DEFINITION)["leaves"]:
            width = f"({leaf['maxLength']})" if leaf["maxLength"] else ""
            kind = leaf["format"] or leaf["type"]
            flag = "" if leaf["described"] else "  [no description]"
            print(f"  {leaf['path']:<64} {kind}{width}{flag}")

    if args.lookups:
        print("\nLookup endpoints:")
        for path, info in sorted(spec.lookups().items()):
            params = f"  ?{','.join(p for p in info['parameters'] if p)}" if info["parameters"] else ""
            print(f"  {path.replace(LOOKUP_PREFIX, ''):<26} -> {info['definition']}{params}")

    if not args.assert_baseline:
        return 0

    if not BASELINE.is_file():
        print(f"\nFAIL  measure_spec: no baseline at {BASELINE.relative_to(REPO)}. "
              f"Run with --write-baseline.")
        return 1

    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
    drift = [(k, baseline.get(k), v) for k, v in current.items() if baseline.get(k) != v]

    if not drift:
        print(f"\nPASS  measure_spec: the pinned spec and all {len(current)} measurements match "
              f"the baseline.")
        return 0

    print(f"\nFAIL  measure_spec: {len(drift)} measurement(s) differ from the baseline.")
    for key, was, now in drift:
        print(f"       {key}: baseline {was!r} -> now {now!r}")
    if any(key == "specSha256" for key, _, _ in drift):
        print("       The spec file itself changed. This is allowed, but it is not automatic: read")
        print("       the differences above, update the counts in Phase1-Analysis.md and")
        print("       Phase1-Plan.md, re-derive any generated DDL, then --write-baseline.")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
