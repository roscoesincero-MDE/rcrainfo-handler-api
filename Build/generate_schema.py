#!/usr/bin/env python
r"""
Generates the mirrored-schema DDL scripts from the pinned EPA swagger spec.

    python build/generate_schema.py            write the scripts
    python build/generate_schema.py --check    fail if the scripts on disk differ (runs in guardrails)
    python build/generate_schema.py --list     what would be written, one line per table

WHY THIS IS GENERATED AND NOT TYPED

Workstream B mirrors 377 in-scope `HandlerSource` leaf fields -- plus EPA's separate `other-ids`
response -- into 21 transactional tables and 24 reference tables (23 lookup response definitions, one
of which carries a nested array that becomes its own table). Every one of those tables carries the
7-column audit block, `DF_<schema>_<Table>_<Field>` default constraint names that must interpolate the
real table name, a filtered unique index, and an `MS_Description` extended property on the table AND
on every column -- roughly 950 description calls.

Hand-writing that is not craftsmanship, it is 45 opportunities to paste a constraint name from the
previous table and have the deployment fail on a duplicate. Generating it makes the conventions
structurally true instead of repeatedly remembered, and `--check` in `build/guardrails.py` means the
checked-in scripts and the spec cannot drift apart.

The scripts are still committed, still readable, and still run by hand by the developer (G3). The
generator produces them; it does not replace them. Nothing at deployment time reads this file.

WHAT IS NOT GENERATED

The AR5 status tables (`logs.LoadRun`, `logs.HandlerLoadStatus`, ...) are this project's own design
rather than a mirror of someone else's, so they are hand-authored. `OWNED_PREFIXES` below is the
generator's territory; anything else in Scripts/ is left alone, and --check only compares files the
generator claims.

THE DECISIONS ENCODED HERE

Each of these is a judgement that had to be made once and applied 377 times. They are constants at
the top of the file rather than logic buried in the emitter, because "why is this column NVARCHAR
(255)" is a question someone will ask in a year.

  Everything is NVARCHAR.  This mirrors an external system. One unexpected non-ASCII character in a
    company name would fail an unattended overnight load, and auditing 377 EPA fields for that risk
    is not possible from a spec that does not say. PAGE compression absorbs most of the cost.

  Nullability comes from identity, not from EPA's `required`.  Only the four columns that make a row
    identifiable are NOT NULL. EPA's own `required` markers are demonstrably unreliable -- preprod
    and production disagree about whether `HandlerSourceNaics.primary` is required (see
    spec/rcrainfo/README.md) -- and a mirror that rejects a handler because EPA omitted a field it
    claims is mandatory turns their data-entry gap into our failed load. The loader records the
    violation as an AR5 observation instead.

  Widths for the 73 unbounded strings come from EPA's own precedents elsewhere in the spec, never
    from invention, and an unrecognised unbounded field raises rather than defaulting. See
    UNBOUNDED_STRING_WIDTHS.

  Every child table carries OrdinalPosition.  A JSON array is ordered, and a mirror that cannot
    reproduce the order EPA sent is not a faithful copy. It also gives child rows a stable natural
    key -- (HandlerSourceId, OrdinalPosition) -- which nothing else in the payload provides.

  Every column description ends with [RCRAInfo: <json.path>].  Flattening turns
    `siteLocation.state.code` into `SiteLocationStateCode`, and the reverse mapping is what a reader
    of this database actually needs. It also keeps the 278 'TODO' placeholders useful: the field is
    undocumented, but at least it is identified.

  No foreign key to any lookup table (B2). Foreign keys to the parent HandlerSource row are correct
    and present; foreign keys to a mirrored code list are not. See Phase1-Plan.md B2.
"""

from __future__ import annotations

import argparse
import difflib
import importlib.util
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SCRIPTS = REPO / "src" / "RCRAInfo.Database" / "Scripts"

# Files this generator owns. --check compares exactly these and nothing else, and reports any file
# with one of these prefixes that the generator no longer produces as stale.
#
# A whole leading digit is claimed at a time, which is why the script numbering reserves ranges rather
# than assigning numbers as objects are written: 0xx setup, 1xx-2xx generated tables and views, 3xx
# hand-authored DDL -- the logs/config tables, the logging procedures, and additive changes to the
# GENERATED tables such as an index added for one query -- 4xx generated procedures, 5xx hand-authored
# application procedures. A hand-written 4xx script would be reported stale on the next --check, so the
# range boundary is not cosmetic; 390_dbo.HandlerSource_GridIndex.sql is in 3xx rather than beside the
# table it indexes for exactly that reason, and 3xx still deploys after every table and before every
# procedure, which is the order an index needs.
OWNED_PREFIXES = ("1", "2", "4")

GENERATED_BANNER = "GENERATED FILE -- do not edit by hand."

ROOT = "HandlerSource"
PARENT_TABLE = "HandlerSource"
SCHEMA = "dbo"

# --------------------------------------------------------------------------------------------------
# Decisions: naming
# --------------------------------------------------------------------------------------------------

# Full-path column-name overrides. Applied before the generic PascalCase flattening.
#
# `type.code` becomes SourceType because that is the name the natural key has carried since the
# analysis document, and (HandlerId, TypeCode, Sequence) would leave the plan and the schema
# describing the same key by different names.
#
# EPA's own provenance fields take the Src prefix (CLAUDE.md) so they are never confused with the
# local audit* columns. They are named SrcCreatedDate rather than SrcCreatedDateUtc: EPA sends a
# `date` with no time and no zone, and appending Utc would assert a precision the value does not
# have. The Src prefix is the requirement; the Utc suffix was illustrative.
COLUMN_NAME_OVERRIDES = {
    "type.code": "SourceType",
    "type.description": "SourceTypeDescription",
    "type.sortOrder": "SourceTypeSortOrder",
    "createdBy": "SrcCreatedBy",
    "createdDate": "SrcCreatedDate",
    "updatedBy": "SrcUpdatedBy",
    "updatedDate": "SrcUpdatedDate",
}

# Collection path -> child table name (singular, PascalCase, HandlerSource-prefixed).
CHILD_TABLE_NAMES = {
    "additionalContacts":                    "HandlerSourceAdditionalContact",
    "owners":                                "HandlerSourceOwner",
    "operators":                              "HandlerSourceOperator",
    "certifications":                        "HandlerSourceCertification",
    "lqgConsolidationsVsqgs":                "HandlerSourceLqgConsolidationVsqg",
    "naics.other":                           "HandlerSourceNaicsOther",
    "siteLocation.stateDistrict.counties":   "HandlerSourceStateDistrictCounty",
    "waste.federalWasteCodes":               "HandlerSourceWasteFederalWasteCode",
    "waste.stateWasteCodes":                 "HandlerSourceWasteStateWasteCode",
    "waste.universalWastes":                 "HandlerSourceWasteUniversalWaste",
    "waste.stateActivities":                 "HandlerSourceWasteStateActivity",
    "permit.otherPermits":                   "HandlerSourcePermitOtherPermit",
    "hsm.activities":                        "HandlerSourceHsmActivity",
    "hsm.activities[].wasteCodes":           "HandlerSourceHsmActivityWasteCode",
    "episodic.wastes":                       "HandlerSourceEpisodicWaste",
    "episodic.wastes[].federalWasteCodes":   "HandlerSourceEpisodicWasteFederalWasteCode",
    "episodic.wastes[].stateWasteCodes":     "HandlerSourceEpisodicWasteStateWasteCode",
    "episodic.projects":                     "HandlerSourceEpisodicProject",
}

# Script order for the 18 collections: by subject area, not alphabetically and not in spec order.
# The numbers are what a reviewer navigates by, and a reviewer reads all four waste tables together.
# Deployment only needs a parent before its grandchild, which this order also satisfies.
CHILD_ORDER = [
    # People
    "additionalContacts", "owners", "operators",
    # Location and classification
    "siteLocation.stateDistrict.counties", "naics.other",
    # Waste
    "waste.federalWasteCodes", "waste.stateWasteCodes", "waste.universalWastes",
    "waste.stateActivities",
    # Permits
    "permit.otherPermits",
    # Hazardous secondary material
    "hsm.activities", "hsm.activities[].wasteCodes",
    # Episodic generation
    "episodic.wastes", "episodic.wastes[].federalWasteCodes",
    "episodic.wastes[].stateWasteCodes", "episodic.projects",
    # Certification and LQG consolidation
    "certifications", "lqgConsolidationsVsqgs",
]

# The json_path sentinel for the single column of a bare-string array: the element itself is the
# value, so there is no property to path to. Chosen as '$' rather than None or '' because Column's
# payload test is `if c.json_path`, and a falsy sentinel would drop the only column the table has.
BARE_ARRAY_JSON_PATH = "$"

# The column a bare-string array shreds into. An array of plain strings has no property name to
# flatten, so the value column has to be named deliberately.
BARE_ARRAY_COLUMN = {
    "waste.federalWasteCodes":             "FederalWasteCode",
    "waste.stateWasteCodes":               "StateWasteCode",
    "hsm.activities[].wasteCodes":         "WasteCode",
    "episodic.wastes[].federalWasteCodes": "FederalWasteCode",
    "episodic.wastes[].stateWasteCodes":   "StateWasteCode",
}

# --------------------------------------------------------------------------------------------------
# Decisions: types and widths
# --------------------------------------------------------------------------------------------------

# Widths for string leaves the spec leaves unbounded, keyed on the last path segment. Every value is
# a number EPA uses somewhere in its own spec; none is invented. An unlisted unbounded field raises,
# so a new one in a future spec is a decision someone makes rather than a default someone inherits.
UNBOUNDED_STRING_WIDTHS = {
    # EPA bounds episodic.projects[].otherDescription at 255, and every code list's `description`
    # is the same kind of value.
    "description": 255,
    # EPA bounds handlerName at 80.
    "name": 80,
    # EPA bounds its own long text -- episodic.wastes[].description, episodic.rescindComment -- at
    # 4000. NVARCHAR(MAX) is deliberately not used: it cannot be indexed and it pushes rows off-page,
    # and EPA demonstrably does not need more than 4000.
    "notes": 4000,
    "publicNotes": 4000,
    "comments": 4000,
    "publicComments": 4000,
    "shortTermGeneratorNotes": 4000,
    "natureOfBusiness": 4000,
    # No precedent in the spec for a permit number. Other identifier fields run 12-50.
    "number": 50,
    # EPA user IDs, not SQL Server logins, so the NVARCHAR(128) floor that ORIGINAL_LOGIN() forces
    # on the audit*By columns does not apply. Phase1-Plan.md B0.
    "createdBy": 100,
    "updatedBy": 100,
    # Bare-string waste-code arrays. The WasteCode lookup declares `code` as maxLength 6.
    "federalWasteCodes[]": 6,
    "stateWasteCodes[]": 6,
    "wasteCodes[]": 6,
    # WasteCode.codeType classifies a code. It was 10 on the reasoning that a classifier cannot be
    # wider than the widest code EPA declares anywhere in the 23 lookup lists -- which does not
    # follow, and EPA disproved it: run 2621 was refused by script 523's width check on a real
    # WasteCode element. EPA declares NO maxLength for this property, so 10 was never its bound,
    # only ours. 50 is EPA's own width for the other unbounded short-text identifier (`number`).
    "codeType": 50,
}

# Widths that override UNBOUNDED_STRING_WIDTHS for ONE property, keyed on the full path. Consulted
# before the last-path-segment table above, so a single list can be widened without moving every
# column that happens to share the leaf name.
UNBOUNDED_STRING_WIDTH_BY_PATH = {
    # `description` is 255 everywhere by analogy with episodic.projects[].otherDescription, and for
    # 22 of the 23 lists that has held. It did not hold for WasteCode: EPA sent a longer one and
    # script 523 refused the whole list rather than clip a code table (run 2621). EPA declares no
    # maxLength here either, so nothing is being violated by widening it.
    #
    # 4000 AND NOT MORE, because 4000 is the real ceiling either way: script 523 shreds this value
    # with JSON_VALUE, which returns NULL in lax mode for anything longer than 4000 characters
    # instead of clipping it. A wider column could therefore never be filled, and NVARCHAR (MAX) is
    # refused for the reason the `notes` entry above gives.
    "WasteCode.description": 4000,
}

SQL_TYPE_BY_FORMAT = {"int32": "INT", "int64": "BIGINT", "double": "FLOAT", "date": "DATE"}

# Columns that make a row identifiable. NOT NULL here is a real assertion: a row whose handler or
# version cannot be determined is not usable data, cannot be keyed, and cannot have children
# attached. The loader rejects such a payload as an AR5 observation rather than storing it.
PARENT_NOT_NULL = ("HandlerId", "ActivityLocation", "SourceType", "Sequence")

AUDIT_BLOCK = [
    ("IsDeleted", "BIT", "(0)"),
    ("auditDeletedBy", "NVARCHAR (128)", "(ORIGINAL_LOGIN ())"),
    ("auditDeletedDateUtc", "DATETIME2", "(SYSUTCDATETIME ())"),
    ("auditCreatedBy", "NVARCHAR (128)", "(ORIGINAL_LOGIN ())"),
    ("auditCreatedDateUtc", "DATETIME2", "(SYSUTCDATETIME ())"),
    ("auditModifiedBy", "NVARCHAR (128)", "(ORIGINAL_LOGIN ())"),
    ("auditModifiedDateUtc", "DATETIME2", "(SYSUTCDATETIME ())"),
]

TODO_DESCRIPTION = "TODO: awaiting EPA data dictionary."

# An extended-property value is a sql_variant capped at 7500 bytes = 3750 NVARCHAR characters, and
# util.uspSetObjectDescription's parameter is NVARCHAR (3750) for that reason. Longer descriptions
# are truncated with a visible marker rather than silently by the engine.
MAX_DESCRIPTION = 3750


# --------------------------------------------------------------------------------------------------
# Model
# --------------------------------------------------------------------------------------------------

@dataclass
class Column:
    name: str
    sql_type: str
    nullable: bool
    description: str
    default: str | None = None
    # The EPA JSON path this column mirrors, e.g. 'siteLocation.state.code'. None for columns this
    # project invents: the surrogate key, OrdinalPosition, and the seven audit columns.
    #
    # It is carried explicitly rather than parsed back out of the '[RCRAInfo: <path>]' suffix on the
    # description, because emit_merge_procedure builds an OPENJSON path from it and a description is
    # prose -- one reworded sentence and the shredding mapping would silently change shape.
    json_path: str | None = None


@dataclass
class Table:
    name: str
    description: str
    columns: list[Column]
    identity_column: str
    unique_index: tuple[str, ...]
    parent_table: str | None = None
    parent_column: str | None = None
    extra_indexes: list[tuple[str, tuple[str, ...], str]] = field(default_factory=list)
    checks: list[tuple[str, str]] = field(default_factory=list)

    @property
    def qualified(self) -> str:
        return f"{SCHEMA}.{self.name}"


def load_spec():
    module_path = REPO / "build" / "measure_spec.py"
    loader = importlib.util.spec_from_file_location("measure_spec", module_path)
    module = importlib.util.module_from_spec(loader)
    loader.loader.exec_module(module)
    document = json.loads(module.SPEC.read_text(encoding="utf-8"))
    return module, module.Spec(document)


# --------------------------------------------------------------------------------------------------
# Naming and typing
# --------------------------------------------------------------------------------------------------

def pascal(segment: str) -> str:
    segment = segment.replace("[]", "")
    return segment[:1].upper() + segment[1:] if segment else segment


def column_name(path: str, *, relative_to: str = "") -> str:
    """Flattens a dotted JSON path into a PascalCase column name."""
    if path in COLUMN_NAME_OVERRIDES:
        return COLUMN_NAME_OVERRIDES[path]

    remainder = path
    if relative_to:
        prefix = relative_to + "[]."
        if not path.startswith(prefix):
            raise ValueError(f"{path!r} is not inside {relative_to!r}")
        remainder = path[len(prefix):]

    if remainder in COLUMN_NAME_OVERRIDES:
        return COLUMN_NAME_OVERRIDES[remainder]

    return "".join(pascal(part) for part in remainder.split("."))


def sql_type(leaf: dict) -> str:
    fmt, kind = leaf.get("format"), leaf.get("type")

    if fmt in SQL_TYPE_BY_FORMAT:
        return SQL_TYPE_BY_FORMAT[fmt]
    if kind == "boolean":
        return "BIT"
    if kind == "integer":
        return "INT"
    if kind == "number":
        return "FLOAT"
    if kind == "string":
        if leaf.get("maxLength"):
            return f"NVARCHAR ({leaf['maxLength']})"
        if leaf["path"] in UNBOUNDED_STRING_WIDTH_BY_PATH:
            return f"NVARCHAR ({UNBOUNDED_STRING_WIDTH_BY_PATH[leaf['path']]})"
        last = leaf["path"].rsplit(".", 1)[-1]
        if last not in UNBOUNDED_STRING_WIDTHS:
            raise SystemExit(
                f"FAIL  generate_schema: '{leaf['path']}' is a string with no maxLength and no width "
                f"decision. Add it to UNBOUNDED_STRING_WIDTHS with a width taken from EPA's own "
                f"spec, or explain the choice there. Refusing to guess a width for a column that "
                f"will silently reject data at load time.")
        return f"NVARCHAR ({UNBOUNDED_STRING_WIDTHS[last]})"

    raise SystemExit(f"FAIL  generate_schema: no SQL type for {leaf['path']} "
                     f"(type={kind!r}, format={fmt!r}).")


def clean(text: str) -> str:
    """Collapses EPA's prose to a single line and caps it at what the helper accepts.

    38 of EPA's descriptions contain newlines, which would break a single-line T-SQL literal.
    Quoting is NOT done here -- see literal(). Doing both in one function is how a description that
    this generator writes itself, rather than harvesting, ends up unescaped: `handler's` closes the
    literal early and the script fails to parse.
    """
    text = re.sub(r"\s+", " ", text).strip()
    if len(text) > MAX_DESCRIPTION:
        text = text[:MAX_DESCRIPTION - 20].rstrip() + " ... [truncated]"
    return text


def literal(text: str) -> str:
    """Escapes a string for a T-SQL NVARCHAR literal. Applied at every emission point, so it cannot
    matter whether the text came from EPA or from this file."""
    return clean(text).replace("'", "''")


def describe(leaf: dict, spec_description: str | None) -> str:
    body = clean(spec_description) if spec_description else TODO_DESCRIPTION
    return f"{body} [RCRAInfo: {leaf['path']}]"


# --------------------------------------------------------------------------------------------------
# Building the tables
# --------------------------------------------------------------------------------------------------

def leaf_descriptions(spec) -> dict[str, str | None]:
    """Maps every leaf path to its description, preferring the property site over the definition."""
    found: dict[str, str | None] = {}

    def visit(schema: dict, path: str, site: str | None) -> None:
        resolved = spec.resolve(schema)
        description = site or resolved.get("description")

        if resolved.get("type") == "array":
            visit(resolved["items"], path + "[]", None)
            return
        if resolved.get("properties"):
            for name, sub in resolved["properties"].items():
                child = f"{path}.{name}" if path else name
                visit(sub, child, sub.get("description"))
            return
        found[path] = description

    visit({"$ref": f"#/definitions/{ROOT}"}, "", None)
    return found


def audit_columns(table_name: str) -> list[Column]:
    return [
        Column(name, typ, nullable=False,
               default=f"CONSTRAINT DF_{SCHEMA}_{table_name}_{name} DEFAULT {default}",
               description=AUDIT_DESCRIPTIONS[name])
        for name, typ, default in AUDIT_BLOCK
    ]


AUDIT_DESCRIPTIONS = {
    "IsDeleted": "Soft-delete flag. 1 = deleted, 0 = active. This database performs no hard "
                 "deletes; every read path filters IsDeleted = 0.",
    "auditDeletedBy": "Login that soft-deleted the row. Populated by DEFAULT on insert, so it is "
                      "only meaningful when IsDeleted = 1.",
    "auditDeletedDateUtc": "UTC timestamp of the soft delete. Populated by DEFAULT on insert, so it "
                           "is only meaningful when IsDeleted = 1; the deleting statement sets it "
                           "explicitly.",
    "auditCreatedBy": "Login that inserted the row. Under the application logins this identifies "
                      "which application wrote it, not an end user.",
    "auditCreatedDateUtc": "UTC timestamp of row insert. Set by DEFAULT; the loader omits this "
                           "column from its INSERT column list so the default fires.",
    "auditModifiedBy": "Login that last modified the row.",
    "auditModifiedDateUtc": "UTC timestamp of last modification. The DEFAULT fires on INSERT only, "
                            "so every UPDATE and MERGE must set this column explicitly or the audit "
                            "trail will claim the row has never changed.",
}


def build_parent(spec, module, descriptions: dict[str, str | None]) -> Table:
    walked = spec.walk(ROOT)
    leaves = [leaf for leaf in walked["leaves"]
              if module.in_scope(leaf["path"]) and "[]" not in leaf["path"]]

    columns = [Column(
        "HandlerSourceId", "INT", nullable=False,
        description="Surrogate key. No business meaning: RCRAInfo's natural key is (HandlerId, "
                    "SourceType, Sequence), and child tables reference this column so that a new "
                    "version of a handler cannot silently absorb the previous version's children.")]

    payload = [Column(column_name(leaf["path"]), sql_type(leaf),
                      nullable=column_name(leaf["path"]) not in PARENT_NOT_NULL,
                      description=describe(leaf, descriptions.get(leaf["path"])),
                      json_path=leaf["path"])
               for leaf in leaves]

    # Identity first, then the natural key, then everything else in spec order. EPA's own property
    # order scatters the key -- `type.code` lands first and `sequence` third -- and in a 210-column
    # CREATE TABLE the four columns that identify the row are the ones a reader looks for.
    key = [c for c in payload if c.name in PARENT_NOT_NULL]
    key.sort(key=lambda c: PARENT_NOT_NULL.index(c.name))
    columns.extend(key)
    columns.extend(c for c in payload if c.name not in PARENT_NOT_NULL)

    missing = set(PARENT_NOT_NULL) - {c.name for c in key}
    if missing:
        raise SystemExit(
            f"FAIL  generate_schema: {sorted(missing)} declared NOT NULL in PARENT_NOT_NULL but not "
            f"present in the spec. The natural key would silently become nullable.")

    columns.extend(audit_columns(PARENT_TABLE))

    return Table(
        name=PARENT_TABLE,
        description="Handler source records mirrored from EPA RCRAInfo. One row per (HandlerId, "
                    "SourceType, Sequence) version of a handler's submitted source record; "
                    "Sequence IS the version, so no history table and no temporal table is needed. "
                    "Read through dbo.vwHandlerSource or dbo.vwHandlerSourceHistory, never directly.",
        columns=columns,
        identity_column="HandlerSourceId",
        unique_index=("HandlerId", "SourceType", "Sequence"),
        # Unfiltered, and deliberately not redundant with UX_..._Natural: the loader has to find an
        # existing row INCLUDING a soft-deleted one, to revive it rather than insert a duplicate, and
        # the filtered unique index cannot serve that seek.
        extra_indexes=[("IX_dbo_HandlerSource_Natural_All",
                        ("HandlerId", "SourceType", "Sequence"),
                        "The natural key again, unfiltered. UX_dbo_HandlerSource_Natural excludes "
                        "soft-deleted rows, so it cannot serve the loader's lookup of a row it may "
                        "need to revive -- without this index that seek is a scan of every handler.")],
    )


def build_children(spec, module, descriptions: dict[str, str | None]) -> list[Table]:
    walked = spec.walk(ROOT)
    collections = [c for c in walked["collections"] if module.in_scope(c)]
    leaves = [leaf for leaf in walked["leaves"] if module.in_scope(leaf["path"])]

    # If EPA adds or removes a collection, say so rather than emitting a differently-numbered set of
    # scripts. CHILD_ORDER and CHILD_TABLE_NAMES are decisions; the spec is not allowed to edit them
    # silently.
    if sorted(collections) != sorted(CHILD_ORDER) or sorted(collections) != sorted(CHILD_TABLE_NAMES):
        added = sorted(set(collections) - set(CHILD_ORDER))
        gone = sorted(set(CHILD_ORDER) - set(collections))
        raise SystemExit(
            f"FAIL  generate_schema: the in-scope collections no longer match CHILD_ORDER / "
            f"CHILD_TABLE_NAMES.\n"
            f"       new in the spec: {added or 'none'}\n"
            f"       gone from the spec: {gone or 'none'}\n"
            f"       Give each new collection a table name and a place in the subject-area order. "
            f"Renumbering scripts is a deliberate act.")

    tables: list[Table] = []

    for path in CHILD_ORDER:
        name = CHILD_TABLE_NAMES[path]
        prefix = path + "[]"

        # Leaves belonging to THIS collection: inside it, but not inside a nested collection.
        nested = [c for c in collections if c != path and c.startswith(prefix)]
        own = [leaf for leaf in leaves
               if leaf["path"].startswith(prefix)
               and not any(leaf["path"].startswith(n + "[]") for n in nested)]

        # A grandchild collection keys to its own parent child table, not to HandlerSource. Keying it
        # to HandlerSource would merge the waste codes of every activity into one undifferentiated
        # list -- the same defect as keying a child to HandlerId, one level down.
        owner = max((c for c in collections if c != path and path.startswith(c + "[]")),
                    key=len, default=None)
        parent_table = CHILD_TABLE_NAMES[owner] if owner else PARENT_TABLE
        parent_column = f"{parent_table}Id" if owner else "HandlerSourceId"

        identity = f"{name}Id"
        columns = [
            Column(identity, "INT", nullable=False,
                   description=f"Surrogate key for one element of the RCRAInfo {path} array."),
            Column(parent_column, "INT", nullable=False,
                   description=f"The {SCHEMA}.{parent_table} version this row belongs to. Keyed to "
                               f"the surrogate key, never to HandlerId: keying a collection to "
                               f"HandlerId collapses every version of the handler into one list and "
                               f"corrupts the history."),
            Column("OrdinalPosition", "INT", nullable=False,
                   description=f"Zero-based position of this element within the RCRAInfo {path} "
                               f"array. A JSON array is ordered, and EPA supplies no key for these "
                               f"elements, so the position is both the fidelity of the mirror and "
                               f"the only natural key this row has."),
        ]

        # json_path on a child column is RELATIVE to the array element, not to '$.handler' -- the
        # merge shreds these with a CROSS APPLY over the element, so the element is the root. The
        # sentinel '$' means "the element IS the value", which is what a bare-string array shreds to
        # and the one case that has no property name to name.
        is_bare = len(own) == 1 and own[0]["path"] == prefix
        if is_bare:
            leaf = own[0]
            columns.append(Column(
                BARE_ARRAY_COLUMN[path], sql_type(leaf), nullable=True,
                description=describe(leaf, descriptions.get(leaf["path"])),
                json_path=BARE_ARRAY_JSON_PATH))
        else:
            for leaf in own:
                columns.append(Column(
                    column_name(leaf["path"], relative_to=path), sql_type(leaf), nullable=True,
                    description=describe(leaf, descriptions.get(leaf["path"])),
                    json_path=leaf["path"][len(prefix) + 1:]))

        columns.extend(audit_columns(name))

        shape = ("an array of bare strings" if is_bare else
                 f"an array of objects with {len(own)} leaf field(s)")
        tables.append(Table(
            name=name,
            description=f"Mirrors the RCRAInfo {path} collection -- {shape} -- one row per element. "
                        f"Keyed to {SCHEMA}.{parent_table} by surrogate key and ordered by "
                        f"OrdinalPosition.",
            columns=columns,
            identity_column=identity,
            unique_index=(parent_column, "OrdinalPosition"),
            parent_table=parent_table,
            parent_column=parent_column,
        ))

    # Scripts run in numeric order, so a grandchild's foreign key target must already be created.
    # CHILD_ORDER satisfies this by construction -- checked here rather than trusted, because the
    # failure mode is a deployment that dies halfway through on script 141.
    seen: set[str] = {PARENT_TABLE}
    for table in tables:
        if table.parent_table not in seen:
            raise SystemExit(
                f"FAIL  generate_schema: {table.name} references {table.parent_table}, which "
                f"CHILD_ORDER places later. Move it after its parent.")
        seen.add(table.name)

    return tables


def build_raw_json() -> Table:
    name = "HandlerSourceRawJson"
    columns = [
        Column("HandlerSourceId", "INT", nullable=False,
               description="The dbo.HandlerSource version this payload produced. Also the primary "
                           "key: one payload per version."),
        Column("RawJson", "NVARCHAR (MAX)", nullable=True,
               description="The HandlerSource JSON exactly as EPA returned it. NVARCHAR(MAX) with "
                           "an ISJSON check, never the native json type, which is SQL Server 2025 "
                           "only. Held in its own table so the 210-column relational row stays "
                           "narrow in the buffer pool. This is what makes an omitted subtree -- the "
                           "three state addenda -- recoverable by re-projection instead of by "
                           "re-fetching from EPA."),
        Column("PayloadSha256", "NVARCHAR (64)", nullable=True,
               description="SHA-256 of RawJson, hex, computed by the loader before the write. Lets "
                           "an unchanged payload be recognised without comparing NVARCHAR(MAX) "
                           "values, and gives the AR5 report something to point at when EPA "
                           "re-issues a version with no visible change."),
        Column("RetrievedDateUtc", "DATETIME2", nullable=True,
               description="UTC timestamp at which the loader received this payload from EPA. "
                           "Distinct from auditCreatedDateUtc, which is when the row was written, "
                           "and from EPA's own SrcUpdatedDate."),
    ]
    columns.extend(audit_columns(name))

    return Table(
        name=name,
        description="The raw EPA payload behind each dbo.HandlerSource row, one to one. Separate "
                    "from the relational table so that the wide row stays narrow, and so that a "
                    "scope decision reversed later can be satisfied by re-projection with no EPA "
                    "re-fetch.",
        columns=columns,
        identity_column="",
        unique_index=("HandlerSourceId",),
        parent_table=PARENT_TABLE,
        parent_column="HandlerSourceId",
        checks=[(f"CK_{SCHEMA}_{name}_RawJson", "RawJson IS NULL OR ISJSON (RawJson) = 1")],
    )


# --------------------------------------------------------------------------------------------------
# other-ids: the one mirrored table that is not version-grained
# --------------------------------------------------------------------------------------------------

OTHER_ID_DEFINITION = "HandlerOtherId"
OTHER_ID_TABLE = "HandlerOtherIdentifier"
OTHER_ID_PATH = "other-ids"
OTHER_ID_NOT_NULL = ("HandlerId", "ActivityLocation", "OtherId")


def other_id_leaves(spec, definition: str, path: str) -> list[dict]:
    """Flattens HandlerOtherId into leaves carrying the JSON path of the endpoint's own response.

    lookup_scalars() is deliberately not reused: it names a nested path after the DEFINITION it
    recursed into, so `relationship.code` would be recorded as [RCRAInfo: Relationship.code]. That is
    tolerable on a mirrored code list, where the definition name IS what the endpoint returns; here
    the provenance suffix has to be the path a reader will actually find in the payload.
    """
    leaves: list[dict] = []

    for prop, schema in spec.definitions[definition]["properties"].items():
        resolved = spec.resolve(schema)
        child = f"{path}.{prop}"

        if resolved.get("type") == "array":
            raise SystemExit(
                f"FAIL  generate_schema: '{child}' is an array. HandlerOtherId is modelled as one "
                f"flat table; a repeating collection inside it needs a child table and a decision "
                f"about its grain, not a silent flattening.")

        if resolved.get("properties"):
            nested = schema.get("$ref", "").rsplit("/", 1)[-1]
            leaves.extend(other_id_leaves(spec, nested, child))
            continue

        leaves.append({"path": child,
                       "type": resolved.get("type"),
                       "format": resolved.get("format"),
                       "maxLength": resolved.get("maxLength"),
                       "description": schema.get("description") or resolved.get("description")})

    return leaves


def build_other_ids(spec) -> Table:
    """The /api/v1/hd/other-ids mirror. In scope per G14; one extra API call per handler (D2)."""
    leaves = other_id_leaves(spec, OTHER_ID_DEFINITION, OTHER_ID_PATH + "[]")

    payload = [Column(column_name(leaf["path"], relative_to=OTHER_ID_PATH), sql_type(leaf),
                      nullable=column_name(leaf["path"], relative_to=OTHER_ID_PATH)
                      not in OTHER_ID_NOT_NULL,
                      description=describe(leaf, leaf["description"]))
               for leaf in leaves]

    key = [c for c in payload if c.name in OTHER_ID_NOT_NULL]
    key.sort(key=lambda c: OTHER_ID_NOT_NULL.index(c.name))

    missing = set(OTHER_ID_NOT_NULL) - {c.name for c in key}
    if missing:
        raise SystemExit(
            f"FAIL  generate_schema: {sorted(missing)} declared NOT NULL in OTHER_ID_NOT_NULL but "
            f"not present in EPA's {OTHER_ID_DEFINITION}. The natural key of the other-ids mirror "
            f"would silently become nullable.")

    identity = f"{OTHER_ID_TABLE}Id"
    columns = [Column(
        identity, "INT", nullable=False,
        description=f"Surrogate key. EPA's own identity for one of these rows is (HandlerId, "
                    f"ActivityLocation, OtherId) -- the three values their DELETE path takes -- "
                    f"enforced here by a filtered unique index.")]
    columns.extend(key)
    columns.extend(c for c in payload if c.name not in OTHER_ID_NOT_NULL)
    columns.extend(audit_columns(OTHER_ID_TABLE))

    return Table(
        name=OTHER_ID_TABLE,
        description=(
            "Identifiers assigned to a handler outside RCRAInfo, mirrored from EPA's HandlerOtherId "
            "shape as returned by /api/v1/hd/other-ids. In scope per G14, at the cost of one extra "
            "API call per handler. THIS IS THE ONE MIRRORED TABLE IN THIS DATABASE THAT IS NOT "
            "VERSION-GRAINED, and the exception is EPA's, not a convenience: their endpoint is keyed "
            "by handlerId alone, neither the request nor the response carries a sequence, so an "
            "other-id belongs to the handler rather than to one submitted version of it. It "
            "therefore keys to (HandlerId, ActivityLocation, OtherId) and takes NO foreign key to "
            "dbo.HandlerSource -- HandlerId is deliberately not unique there, and a foreign key to "
            "the surrogate key would assert a grain the data does not have. Read it by HandlerId and "
            "join it to whichever version you are reporting on; the association is to the handler. "
            "The table is named HandlerOtherIdentifier rather than HandlerOtherId so that its "
            "surrogate key reads HandlerOtherIdentifierId and not HandlerOtherIdId."),
        columns=columns,
        identity_column=identity,
        unique_index=tuple(OTHER_ID_NOT_NULL),
        # Unfiltered, for the same reason every other table here carries one: the filtered unique
        # index cannot serve the loader's lookup of a row it may need to revive, and nothing in this
        # database is ever hard-deleted. Leading with HandlerId also serves the read the monitoring
        # app makes -- every other-id for one handler.
        extra_indexes=[(f"IX_{SCHEMA}_{OTHER_ID_TABLE}_HandlerAll",
                        tuple(OTHER_ID_NOT_NULL),
                        f"EPA's natural key again, unfiltered. UX_{SCHEMA}_{OTHER_ID_TABLE}_Natural "
                        f"excludes soft-deleted rows, so it cannot serve the loader's lookup of a row "
                        f"it may need to revive rather than duplicate. Leading with HandlerId, it "
                        f"also serves the obvious read: every other-id recorded for one handler.")],
    )


def lookup_scalars(spec, definition: str, prefix: str = "") -> tuple[list[Column], list[str]]:
    """The scalar columns of a lookup definition, with single-cardinality nested objects flattened
    in exactly as they are on dbo.HandlerSource. Returns (columns, nested array property names).

    EpisodicProject references EpisodicType, which is itself a mirrored list with its own table. The
    reference is flattened rather than turned into a foreign key, for the same reason no handler
    column has one (B2): a code EPA retires still has to resolve for the historical rows that used
    it, and a foreign key would make loading the two lists order-dependent.
    """
    columns: list[Column] = []
    arrays: list[str] = []

    for prop, schema in spec.definitions[definition]["properties"].items():
        resolved = spec.resolve(schema)
        path = f"{definition}.{prefix}{prop}"

        if resolved.get("type") == "array":
            arrays.append(prop)
            continue

        if resolved.get("properties"):
            nested_ref = schema.get("$ref", "").rsplit("/", 1)[-1]
            inner, inner_arrays = lookup_scalars(spec, nested_ref, "")
            for col in inner:
                columns.append(Column(pascal(prop) + col.name, col.sql_type, col.nullable,
                                      col.description))
            arrays.extend(f"{prop}.{a}" for a in inner_arrays)
            continue

        leaf = {"path": path, "type": resolved.get("type"),
                "format": resolved.get("format"), "maxLength": resolved.get("maxLength")}
        body = schema.get("description") or resolved.get("description")
        columns.append(Column(
            pascal(prop), sql_type(leaf), nullable=True,
            description=(clean(body) if body else TODO_DESCRIPTION) + f" [RCRAInfo: {path}]"))

    return columns, arrays


LOOKUP_PREAMBLE = (
    "Retired codes are SOFT-DELETED and must still resolve: a code EPA retires this year is still "
    "the correct code for a handler version submitted while it was current, so a historical read "
    "must NOT filter IsDeleted = 0 on this table. That is the one documented exception to this "
    "database's blanket read rule. No handler table takes a foreign key to this one (see "
    "Phase1-Plan.md B2): the lists load independently, and a code that arrives on a handler before "
    "it appears in the mirrored list is a data-quality observation, not a failed load.")


def build_lookups(spec) -> list[Table]:
    """One table per distinct lookup/hd response definition. 24 endpoints, 23 definitions."""
    endpoints: dict[str, list[str]] = {}
    for path, info in sorted(spec.lookups().items()):
        endpoints.setdefault(info["definition"], []).append(
            path.replace("/api/v1/lookup/hd/", ""))

    tables: list[Table] = []
    for definition, paths in sorted(endpoints.items()):
        name = f"Lookup{definition}"
        scalars, arrays = lookup_scalars(spec, definition)

        # The natural key is EPA's own: `required` on these lists is uniform across all 23 and
        # agrees between preprod and production, unlike HandlerSourceNaics.primary.
        required = spec.definitions[definition].get("required", [])
        natural = tuple(pascal(p) for p in ("activityLocation", "code") if p in required)
        if not natural:
            raise SystemExit(f"FAIL  generate_schema: {definition} declares no code/activityLocation "
                             f"natural key; decide one explicitly rather than defaulting.")
        for col in scalars:
            if col.name in natural:
                col.nullable = False

        columns = [Column(
            f"{name}Id", "INT", nullable=False,
            description=f"Surrogate key. EPA's natural key for this list is "
                        f"({', '.join(natural)}), enforced by a filtered unique index.")]
        columns.extend(scalars)
        columns.extend(audit_columns(name))

        served = ", ".join(f"/lookup/hd/{p}" for p in paths)
        note = ("" if not arrays else
                f" EPA's {definition} also carries the {', '.join(arrays)} array, which becomes its "
                f"own table.")
        tables.append(Table(
            name=name,
            description=f"Mirror of the EPA RCRAInfo {definition} code list, served by {served}. "
                        f"{LOOKUP_PREAMBLE}{note}",
            columns=columns,
            identity_column=f"{name}Id",
            unique_index=natural,
        ))

        # The one nested array across all 23 lists: StateDistrict.counties, a district-to-county
        # mapping. It carries the county's own columns rather than a foreign key to LookupCounty,
        # for the reason in LOOKUP_PREAMBLE.
        for array_prop in arrays:
            item_ref = spec.resolve(
                spec.definitions[definition]["properties"][array_prop])["items"]
            item = item_ref.get("$ref", "").rsplit("/", 1)[-1]
            # Named from the item DEFINITION, not from the pluralised property: naive de-pluralising
            # produces LookupStateDistrictCountie.
            child_name = f"{name}{item}"
            inner, _ = lookup_scalars(spec, item)

            child_columns = [
                Column(f"{child_name}Id", "INT", nullable=False,
                       description=f"Surrogate key for one element of EPA's "
                                   f"{definition}.{array_prop} array."),
                Column(f"{name}Id", "INT", nullable=False,
                       description=f"The {SCHEMA}.{name} row this element belongs to."),
                Column("OrdinalPosition", "INT", nullable=False,
                       description=f"Zero-based position within EPA's {definition}.{array_prop} "
                                   f"array. EPA supplies no key for these elements, so the position "
                                   f"is both the fidelity of the mirror and the only natural key."),
            ]
            child_columns.extend(inner)
            child_columns.extend(audit_columns(child_name))

            tables.append(Table(
                name=child_name,
                description=f"Mirrors EPA's {definition}.{array_prop} array -- the {item} entries "
                            f"belonging to each {definition} row -- one row per element. {item} also "
                            f"has its own mirrored table; this one records the association, and "
                            f"carries the same columns rather than a foreign key. "
                            f"{LOOKUP_PREAMBLE}",
                columns=child_columns,
                identity_column=f"{child_name}Id",
                unique_index=(f"{name}Id", "OrdinalPosition"),
                parent_table=name,
                parent_column=f"{name}Id",
            ))

    return tables


# --------------------------------------------------------------------------------------------------
# Emitting
# --------------------------------------------------------------------------------------------------

def emit_table(table: Table, *, ordinal: int, source_note: str) -> str:
    out: list[str] = []
    w = out.append
    pk_columns = table.identity_column or table.unique_index[0]

    w("/" + "*" * 118)
    w(f"Script:       {ordinal:03d}_{table.qualified}.sql")
    w(f"Author:       generated by build/generate_schema.py")
    w("=" * 120)
    w("Description:")
    w("")
    for line in wrap(table.description, 118):
        w(line)
    w("")
    w(f"{GENERATED_BANNER} Regenerate with:")
    w("")
    w("    python build/generate_schema.py")
    w("")
    for line in wrap(source_note, 118):
        w(line)
    w("")
    w("build/guardrails.py runs `generate_schema.py --check`, which fails if this file and the spec")
    w("have drifted apart, so editing it here is not merely discouraged -- it breaks the build.")
    w("")
    w("=" * 120)
    w("Notes:")
    w("")
    w("RE-RUNNABLE. Every CREATE is guarded, descriptions go through util.uspSetObjectDescription")
    w("(which adds or updates), and nothing is dropped or truncated. A second run changes nothing.")
    w("")
    w("Later changes are ADDITIVE: append a guarded ALTER TABLE ... ADD block rather than editing")
    w("the CREATE TABLE above, or the script stops converging on a database that already has the")
    w("table. In this file that means changing the generator, not the output.")
    w("*" * 118 + "/")
    w("")
    w("SET XACT_ABORT ON;")
    for line in QUOTED_IDENTIFIER_PREAMBLE:
        w(line)
    w("GO")
    w("")

    # ---- the table -------------------------------------------------------------------------------
    w("-" * 100)
    w("-- 1. The table. Guarded, so a second run is a no-op.")
    w("-" * 100)
    w(f"IF OBJECT_ID (N'{table.qualified}', N'U') IS NULL")
    w("BEGIN")
    w(f"    CREATE TABLE {table.qualified}")
    w("    (")

    width_name = max(len(c.name) for c in table.columns)
    width_type = max(len(c.sql_type) for c in table.columns)
    width_identity = 16 if table.identity_column else 0

    lines: list[str] = []
    for col in table.columns:
        identity = "IDENTITY (1, 1)" if col.name == table.identity_column else ""
        null = "NOT NULL" if not col.nullable else "    NULL"
        piece = (f"        {col.name:<{width_name}}  {col.sql_type:<{width_type}}  "
                 f"{identity:<{width_identity}}{null}")
        if col.default:
            piece += f" {col.default}"
        lines.append(piece + ",")

    out.extend(lines)
    w("")
    w(f"        CONSTRAINT PK_{SCHEMA}_{table.name} PRIMARY KEY CLUSTERED ({pk_columns}),")
    for cname, expression in table.checks:
        w(f"        CONSTRAINT {cname} CHECK ({expression}),")
    out[-1] = out[-1].rstrip(",")
    w("    )")
    w("    WITH (DATA_COMPRESSION = PAGE);")
    w("END;")
    w("GO")
    w("")

    # ---- indexes ---------------------------------------------------------------------------------
    def emit_index(section: str, idx_name: str, cols: tuple[str, ...], why: list[str],
                   *, unique: bool) -> None:
        """A guarded CREATE INDEX, then a guarded compression repair for the same index.

        The repair is here because the CREATE guard alone does not converge. A nonclustered index
        does NOT inherit the table's compression -- WITH (DATA_COMPRESSION = PAGE) on CREATE TABLE
        reaches the clustered index only -- so every nonclustered index on all 44 tables was sitting
        at NONE. Adding the clause to the CREATE fixes new databases and does nothing at all to the
        ones already deployed, because IF NOT EXISTS finds the index and skips. The ALTER is the
        additive, separately guarded change CLAUDE.md asks for, and it converges: it fires only while
        the compression is actually wrong, so a second run rebuilds nothing.
        """
        w("-" * 100)
        w(f"-- {section}")
        for piece in why:
            w(f"--    {piece}")
        w("-" * 100)
        w("IF NOT EXISTS (SELECT 1")
        w("                 FROM sys.indexes")
        w(f"                WHERE name      = N'{idx_name}'")
        w(f"                  AND object_id = OBJECT_ID (N'{table.qualified}'))")
        w("BEGIN")
        w(f"    CREATE {'UNIQUE ' if unique else ''}INDEX {idx_name}")
        w(f"        ON {table.qualified} ({', '.join(cols)})")
        if unique:
            w("        WHERE IsDeleted = 0")
        w("        WITH (DATA_COMPRESSION = PAGE);")
        w("END;")
        w("GO")
        w("")
        w("IF EXISTS (SELECT 1")
        w("             FROM sys.indexes AS i")
        w("             JOIN sys.partitions AS p")
        w("               ON p.object_id = i.object_id")
        w("              AND p.index_id  = i.index_id")
        w(f"            WHERE i.name      = N'{idx_name}'")
        w(f"              AND i.object_id = OBJECT_ID (N'{table.qualified}')")
        w("              AND p.data_compression_desc <> N'PAGE')")
        w("BEGIN")
        w(f"    ALTER INDEX {idx_name}")
        w(f"        ON {table.qualified} REBUILD WITH (DATA_COMPRESSION = PAGE);")
        w("END;")
        w("GO")
        w("")

    unique_name = f"UX_{SCHEMA}_{table.name}_Natural"

    # No separate natural-key index when the primary key already IS the natural key -- a second
    # unique index on the same single column costs writes and buys nothing.
    if table.unique_index != (pk_columns,):
        emit_index(
            "2. Natural key.", unique_name, table.unique_index,
            ["A filtered unique index, not a unique constraint: a soft-deleted row keeps its key,",
             "and an unfiltered constraint would block re-creation of that key forever -- the load",
             "would fail on a handler EPA had corrected and resubmitted."],
            unique=True)

    # The foreign key column, unfiltered. SQL Server does not index a foreign key automatically.
    # Skipped only when the primary key already leads with that column, which is the case for the
    # one-to-one dbo.HandlerSourceRawJson.
    if table.parent_column and table.parent_column != pk_columns:
        emit_index(
            "3. The foreign key column.",
            f"IX_{SCHEMA}_{table.name}_{table.parent_column}", (table.parent_column,),
            [f"Deliberately NOT redundant with {unique_name} above. That index is filtered to",
             "IsDeleted = 0, so it cannot serve a lookup that has to see soft-deleted rows -- and",
             "for this loader that is a normal path, not an exceptional one. Nothing is ever hard-",
             "deleted, so re-loading a handler means finding the existing child rows, including any",
             "previously soft-deleted, and reviving them. Without this index that seek is a scan of",
             "every child row in the table, for every handler in the batch."],
            unique=False)

    for idx_name, cols, why in table.extra_indexes:
        emit_index("Supporting index.", idx_name, cols, wrap(why, 94), unique=False)

    # ---- foreign key -----------------------------------------------------------------------------
    if table.parent_table:
        fk = f"FK_{SCHEMA}_{table.name}_{table.parent_table}"
        w("-" * 100)
        w("-- Foreign key to the parent version. NO ACTION on delete, not CASCADE: this database")
        w("--    performs no hard deletes, so a cascade could never fire and declaring one would")
        w("--    advertise a behaviour that does not exist.")
        w("-" * 100)
        w("IF NOT EXISTS (SELECT 1")
        w("                 FROM sys.foreign_keys")
        w(f"                WHERE name = N'{fk}')")
        w("BEGIN")
        w(f"    ALTER TABLE {table.qualified}")
        w(f"        ADD CONSTRAINT {fk} FOREIGN KEY ({table.parent_column})")
        w(f"            REFERENCES {SCHEMA}.{table.parent_table} "
          f"({table.parent_column if table.parent_table != PARENT_TABLE else 'HandlerSourceId'});")
        w("END;")
        w("GO")
        w("")

    # ---- extended properties ---------------------------------------------------------------------
    w("/*")
    w("    Extended properties. Required on the table and on EVERY column.")
    w("")
    w("    Through util.uspSetObjectDescription only. It adds or updates, so this script re-runs and")
    w("    an improved wording replaces the old one; a bare sp_addextendedproperty succeeds once and")
    w("    then fails on every subsequent run.")
    w("")
    w("    Each column description ends with the RCRAInfo JSON path it mirrors. Flattening turned")
    w("    siteLocation.state.code into SiteLocationStateCode, and the way back is what a reader of")
    w("    this database needs. Where EPA's spec carries no description the placeholder says so")
    w("    rather than inventing one -- a wrong description in database metadata is worse than a")
    w("    missing one.")
    w("*/")
    w("")
    w("EXEC util.uspSetObjectDescription")
    w(f"      @SchemaName  = N'{SCHEMA}'")
    w("    , @ObjectType  = N'TABLE'")
    w(f"    , @ObjectName  = N'{table.name}'")
    w(f"    , @Description = N'{literal(table.description)}';")
    w("GO")
    w("")

    for col in table.columns:
        w("EXEC util.uspSetObjectDescription")
        w(f"      @SchemaName  = N'{SCHEMA}'")
        w("    , @ObjectType  = N'TABLE'")
        w(f"    , @ObjectName  = N'{table.name}'")
        w(f"    , @ColumnName  = N'{col.name}'")
        w(f"    , @Description = N'{literal(col.description)}';")
        w("GO")
        w("")

    w(f"PRINT N'{ordinal:03d}: {table.qualified} created or altered, "
      f"{len(table.columns)} column(s) described.';")
    w("GO")
    return "\n".join(out) + "\n"


def wrap(text: str, width: int) -> list[str]:
    words, lines, current = text.split(), [], ""
    for word in words:
        if current and len(current) + 1 + len(word) > width:
            lines.append(current)
            current = word
        else:
            current = f"{current} {word}".strip()
    if current:
        lines.append(current)
    return lines


# SET QUOTED_IDENTIFIER ON is emitted by every script this generator writes, and it is not the invoking
# client's business. sqlcmd defaults it OFF where every other client defaults it ON; the setting is BAKED IN
# at CREATE time and stored in sys.sql_modules; and a module -- or a session -- carrying it OFF cannot run
# DML against a table with a filtered index, which raises error 1934. EVERY unique constraint in this
# database is a filtered index (WHERE IsDeleted = 0), so that is every table. Found by a hand run of
# 510_logs.uspStartLoadRun.sql: the deployment harness passes sqlcmd -I and never saw it.
QUOTED_IDENTIFIER_PREAMBLE = [
    "-- Not decoration: sqlcmd defaults QUOTED_IDENTIFIER OFF where every other client defaults it ON, the",
    "-- setting is BAKED IN at CREATE time, and a module or session carrying it OFF cannot run DML against a",
    "-- table with a filtered index (error 1934). Every unique constraint here is one. Set it so that a hand",
    "-- run cannot get it wrong.",
    "SET QUOTED_IDENTIFIER ON;",
]


def emit_views(parent: Table) -> tuple[str, str]:
    """The two AR7 views. vwHandlerSource layers on vwHandlerSourceHistory so the deletion-stamp
    masking is written once and cannot disagree between them."""
    payload = [c.name for c in parent.columns
               if c.name not in [a for a, _, _ in AUDIT_BLOCK]]

    def header(name: str, description: str, usage: str) -> list[str]:
        # Above the header rather than below it: validate-sql.py forbids a batch separator BETWEEN a
        # header and its CREATE, and this needs its own batch to take effect before the CREATE runs.
        out = [*QUOTED_IDENTIFIER_PREAMBLE, "GO", "",
               "/" + "*" * 118,
               f"ObjectName:   {SCHEMA}.{name}",
               "Author:       generated by build/generate_schema.py",
               "CreateDate:   2026-09-04",
               "=" * 120,
               "Description:",
               ""]
        out += wrap(description, 118)
        out += ["",
                f"{GENERATED_BANNER} Regenerate with `python build/generate_schema.py`; the column",
                "list follows dbo.HandlerSource and would drift the moment a column is added.",
                "",
                "=" * 120,
                "Requirements and Key Dependencies:",
                "",
                f"{SCHEMA}.{PARENT_TABLE}. Column-level descriptions live on that table: SQL Server",
                "does not inherit an extended property into a view, and a second copy of 210",
                "descriptions would drift from the first.",
                "",
                "=" * 120,
                "Example Usage and Performance:",
                ""]
        out += wrap(usage, 118)
        out += ["",
                "=" * 120,
                "Modification History:",
                "Date\t\tAuthor          Ticket    \t\tDescription",
                "---------- \t--------------- -----------\t\t" + "-" * 76,
                "2026-09-04\tgenerated\t\t\t\t\t\tInitial version. Workstream B1.",
                "*" * 118 + "/",
                ""]
        return out

    history = header(
        "vwHandlerSourceHistory",
        "Every handler source version this database holds, soft-deleted rows excluded. With EF Core "
        "reduced to calling stored procedures, an EF Core global query filter protects nothing, so "
        "this view and dbo.vwHandlerSource are the ONLY structural enforcement of AR7. Every "
        "consumer reads a view; a procedure that must hit the base table filters IsDeleted = 0 "
        "itself. The four deletion-stamp columns are masked to NULL unless the row is actually "
        "deleted: auditDeletedBy and auditDeletedDateUtc are NOT NULL with defaults, so they are "
        "populated on INSERT and would otherwise show a meaningless deletion date beside every "
        "healthy handler in the monitoring UI.",
        "SELECT HandlerId, Sequence, HandlerName FROM dbo.vwHandlerSourceHistory "
        "WHERE HandlerId = N'MD0000123456' ORDER BY Sequence DESC;")
    history += [
        f"CREATE OR ALTER VIEW {SCHEMA}.vwHandlerSourceHistory",
        "AS",
        "SELECT" + "".join(f"\n       {'' if i == 0 else ', '}hs.{c}" for i, c in enumerate(payload)),
        "     , hs.IsDeleted",
        "       -- Masked: both columns are NOT NULL with a DEFAULT, so they carry an insert-time",
        "       -- value on every live row. NULL is the honest answer for a row that is not deleted.",
        "     , CASE WHEN hs.IsDeleted = 1 THEN hs.auditDeletedBy      END AS auditDeletedBy",
        "     , CASE WHEN hs.IsDeleted = 1 THEN hs.auditDeletedDateUtc END AS auditDeletedDateUtc",
        "     , hs.auditCreatedBy",
        "     , hs.auditCreatedDateUtc",
        "     , hs.auditModifiedBy",
        "     , hs.auditModifiedDateUtc",
        f"  FROM {SCHEMA}.{PARENT_TABLE} AS hs",
        " WHERE hs.IsDeleted = 0;",
        "GO",
        "",
        "EXEC util.uspSetObjectDescription",
        f"      @SchemaName  = N'{SCHEMA}'",
        "    , @ObjectType  = N'VIEW'",
        "    , @ObjectName  = N'vwHandlerSourceHistory'",
        "    , @Description = N'Every handler source version held, soft-deleted rows excluded. One of "
        "the two AR7 enforcement points; with EF Core calling only procedures, these views are the "
        "only structural guarantee that IsDeleted = 0 is applied. Deletion-stamp columns read NULL "
        "unless the row is deleted.';",
        "GO",
        "",
        "PRINT N'120: dbo.vwHandlerSourceHistory created or altered.';",
        "GO",
    ]

    current = header(
        "vwHandlerSource",
        "The current version of each handler source record: soft-deleted rows excluded and "
        "CurrentRecord = 1. This is the default read path for the application. It selects from "
        "dbo.vwHandlerSourceHistory rather than from the base table, so the IsDeleted filter and the "
        "deletion-stamp masking are defined once and the two views cannot disagree.",
        "SELECT HandlerId, HandlerName, ActivityLocation FROM dbo.vwHandlerSource "
        "WHERE ActivityLocation = N'MD';")
    current += [
        f"CREATE OR ALTER VIEW {SCHEMA}.vwHandlerSource",
        "AS",
        "SELECT *",
        f"  FROM {SCHEMA}.vwHandlerSourceHistory",
        " WHERE CurrentRecord = 1;",
        "GO",
        "",
        "EXEC util.uspSetObjectDescription",
        f"      @SchemaName  = N'{SCHEMA}'",
        "    , @ObjectType  = N'VIEW'",
        "    , @ObjectName  = N'vwHandlerSource'",
        "    , @Description = N'The current version of each handler source record: IsDeleted = 0 and "
        "CurrentRecord = 1. The default read path for the applications. Layered on "
        "dbo.vwHandlerSourceHistory so the AR7 filter is defined in exactly one place.';",
        "GO",
        "",
        "PRINT N'121: dbo.vwHandlerSource created or altered.';",
        "GO",
    ]

    return "\n".join(history) + "\n", "\n".join(current) + "\n"


# --------------------------------------------------------------------------------------------------
# Driver
# --------------------------------------------------------------------------------------------------

# --------------------------------------------------------------------------------------------------
# The set-based write procedure (DA1)
# --------------------------------------------------------------------------------------------------

# The batch parameter is an ENVELOPE, not EPA's object bare. Each array element is
#
#     { "retrievedDateUtc": "2026-09-05T11:02:31.417", "handler": { ...EPA's object, verbatim... } }
#
# Two properties this project owns, wrapping one it does not touch. The alternative -- adding
# retrievedDateUtc alongside EPA's own properties at the top level -- was rejected for one reason:
# dbo.HandlerSourceRawJson.RawJson is populated by OPENJSON's `'$.handler' AS JSON`, which hands back
# the element's own JSON text. With a flat shape that text would contain our two additions, and the
# 'recover a mapping defect by re-projecting RawJson' story in D3 requires RawJson to be EPA's
# payload and nothing else. The envelope keeps the boundary between their data and ours visible in
# the payload itself.
PAYLOAD_HANDLER = "handler"
PAYLOAD_RETRIEVED = "retrievedDateUtc"

MERGE_PROCEDURE = "uspMergeHandlerSourceBatch"
RAW_JSON_TABLE = "HandlerSourceRawJson"

# OPENJSON path segments are emitted unquoted, so a property name that is not a bare identifier would
# produce a path that parses as something else. EPA's are all camelCase identifiers; this refuses
# rather than emits a path it cannot vouch for.
SAFE_JSON_SEGMENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*\Z")


def json_path(leaf_path: str) -> str:
    """'siteLocation.state.code' -> "'$.handler.siteLocation.state.code'"."""
    for segment in leaf_path.split("."):
        if not SAFE_JSON_SEGMENT.match(segment):
            raise SystemExit(
                f"FAIL  generate_schema: JSON property {segment!r} in path {leaf_path!r} is not a "
                f"bare identifier, so an OPENJSON path cannot be emitted for it unquoted. Decide the "
                f"quoting explicitly rather than letting this generator guess.")
    return f"'$.{PAYLOAD_HANDLER}.{leaf_path}'"


def emit_child_merges(parent: Table, children: list[Table]) -> list[str]:
    """Section 4 of dbo.uspMergeHandlerSourceBatch: one MERGE per repeating collection, plus the
    machinery the 18 of them share.

    Three shapes, decided by the payload rather than by preference:

      * an array of objects keyed to dbo.HandlerSource         (10 of the 18)
      * an array of bare strings keyed to dbo.HandlerSource     (2)
      * either shape keyed to another CHILD row -- a grandchild (3, and 3 more that are objects)

    All three share one retire rule, which is the decision MDE took on 2026-09-05: a stored child
    row is retired only when EPA actually SENT the property. See the emitted comments and the
    RETIRING A CHILD note in the procedure header for why the gate is not optional."""
    payload = [c for c in parent.columns if c.json_path]
    key = list(parent.unique_index)
    key_width = max(len(k) for k in key)
    path_by_table = {name: path for path, name in CHILD_TABLE_NAMES.items()}

    # EPA's payload nests exactly two levels deep, and the grandchild thread below relies on it: a
    # grandchild finds its parent's surrogate key by joining the already-merged child table on
    # (HandlerSourceId, OrdinalPosition). A third level would need that join threaded through two
    # ordinals, which is a decision rather than a mechanical extension -- so it raises here instead
    # of emitting a join that silently keys to the wrong row.
    grandchildren: dict[str, list[Table]] = {}
    for table in children:
        if table.parent_table == PARENT_TABLE:
            continue
        owner = next((c for c in children if c.name == table.parent_table), None)
        if owner is None or owner.parent_table != PARENT_TABLE:
            raise SystemExit(
                f"FAIL  generate_schema: {table.name} nests more than two levels below "
                f"{SCHEMA}.{PARENT_TABLE}. The merge threads a grandchild by joining its "
                f"already-merged parent on (HandlerSourceId, OrdinalPosition), which assumes that "
                f"parent is a direct child. Extend the thread deliberately.")
        grandchildren.setdefault(owner.name, []).append(table)

    def element_path(table: Table) -> str:
        """The OPENJSON / JSON_PATH_EXISTS literal, relative to whichever row shreds it."""
        path = path_by_table[table.name]
        if table.parent_table == PARENT_TABLE:
            return f"'$.{PAYLOAD_HANDLER}.{path}'"
        return f"'$.{path[len(path_by_table[table.parent_table]) + 3:]}'"

    def display_path(table: Table) -> str:
        """The full path as a human reads it in EPA's spec, [] markers and all. This is what lands in
        logs.DataQualityObservation.JsonPath, where its whole job is to be searched for by hand in
        the current API spec."""
        return f"$.{PAYLOAD_HANDLER}.{path_by_table[table.name]}"

    def bare_column(table: Table, columns: list[Column]) -> Column | None:
        if len(columns) == 1 and columns[0].json_path == BARE_ARRAY_JSON_PATH:
            return columns[0]
        return None

    out: list[str] = []
    w = out.append

    # ---- the shared machinery ---------------------------------------------------------------------
    w("        " + "-" * 89)
    w(f"        -- 4. The {len(children)} repeating collections. Keyed to the surrogate key of the version")
    w("        --    above, never to HandlerId: a collection keyed to the handler would collapse every")
    w("        --    version into one list and destroy the history this mirror exists to keep.")
    w("        --")
    w("        --    Each one is a MERGE through a CTE that restricts the target to THIS batch's")
    w("        --    parents. The CTE is not cosmetic: WHEN NOT MATCHED BY SOURCE over the bare table")
    w("        --    would make the engine consider every row of a table that grows without bound, and")
    w("        --    the predicate that saves correctness would not save the plan.")
    w("        " + "-" * 89)
    w("        -- The surrogate key of every element, resolved once and reused by all")
    w(f"        -- {len(children)} statements below. Statement 2 inserted or revived every element in this batch, so")
    w("        -- a short row count here means the parent merge did not do what the rest of this")
    w("        -- procedure assumes -- which would silently merge no children at all for the missing")
    w("        -- handler, and, worse, retire the children it already had.")
    w("        INSERT INTO @Version (Ordinal, HandlerSourceId)")
    w("        SELECT k.Ordinal, hs.HandlerSourceId")
    w("          FROM @Key AS k")
    w(f"          JOIN {SCHEMA}.{PARENT_TABLE} AS hs")
    for i, k in enumerate(key):
        col = next(c for c in payload if c.name == k)
        src = f"CAST (k.{k} AS {col.sql_type})" if col.sql_type.startswith("NVARCHAR") else f"k.{k}"
        w(f"{'ON' if i == 0 else 'AND':>13} hs.{k:<{key_width}} = {src}")
    w(f"{'AND':>13} hs.{'IsDeleted':<{key_width}} = 0;")
    w("")
    w("        IF @@ROWCOUNT <> @ElementCount")
    w("        BEGIN")
    w("            SET @Failure = CONCAT (N'Resolved ', (SELECT COUNT (*) FROM @Version), N' of '")
    w("                                 , @ElementCount, N' element(s) to a live '")
    w(f"                                 , N'{SCHEMA}.{PARENT_TABLE} row after the parent merge. The child ')")
    w("                                 + N'collections cannot be merged against a version that is not '")
    w("                                 + N'there, and merging the rest would retire the missing '")
    w("                                 + N'handler''s children. The batch is rolled back whole.';")
    w("            THROW 50000, @Failure, 1;")
    w("        END;")
    w("")
    w("        -- The retire gate, and the reason this section is longer than the parent's. EPA sending")
    w("        -- NO property is not the same fact as EPA sending an EMPTY array, but a CROSS APPLY")
    w("        -- OPENJSON cannot tell them apart: both shred to zero rows, without error -- and so does")
    w("        -- a property EPA has RENAMED. An ungated WHEN NOT MATCHED BY SOURCE would therefore")
    w("        -- soft-delete every child row of every handler in the batch the day a property is")
    w("        -- renamed, with no error and no row-count anomaly to notice it by.")
    w("        --")
    w("        -- JSON_PATH_EXISTS does tell them apart -- 1 for both [] and a populated array, 0 only")
    w("        -- when the property is absent -- so it is called once per collection per element and the")
    w("        -- answer is recorded here. An empty or shorter array is a real removal and retires. An")
    w("        -- ABSENT property retires nothing and is recorded as a data-quality observation")
    w("        -- instead, because absent is not evidence of none. MDE's decision, 2026-09-05.")
    top = [t for t in children if t.parent_table == PARENT_TABLE]
    name_lit_width = max(len(t.name) for t in children) + 3
    w("        INSERT INTO @Collection (TableName, JsonPath, ParentId, Sent)")
    w("        SELECT g.TableName, g.JsonPath, v.HandlerSourceId, g.Sent")
    w("          FROM OPENJSON (@Payload) AS e")
    w("          JOIN @Version AS v ON v.Ordinal = CAST (e.[key] AS INT) + 1")
    w("         CROSS APPLY (VALUES")
    for i, table in enumerate(top):
        comma = " " if i == 0 else ","
        w(f"                       {comma} (N'{table.name}'{'':<{name_lit_width - len(table.name) - 3}}, N'{display_path(table)}'")
        w(f"                         , JSON_PATH_EXISTS (e.[value], {element_path(table)}))")
    w("                     ) AS g (TableName, JsonPath, Sent);")
    w("")

    # ---- one MERGE per collection -----------------------------------------------------------------
    def emit_gate(owner: Table) -> None:
        """The same JSON_PATH_EXISTS gate, one level down: a grandchild's property is present or absent
        per PARENT ELEMENT, not per handler, so its gate keys to the child row rather than the
        version. Emitted after the owner's merge, because it joins the rows that merge just wrote."""
        kids = grandchildren[owner.name]
        owner_id = f"{owner.name}Id"
        w(f"        -- The retire gate for {owner.name}'s own collection(s), keyed to the")
        w("        -- child row rather than to the version. Same rule, one level down.")
        w("        INSERT INTO @Collection (TableName, JsonPath, ParentId, Sent)")
        w(f"        SELECT g.TableName, g.JsonPath, p.{owner_id}, g.Sent")
        w("          FROM OPENJSON (@Payload) AS e")
        w("          JOIN @Version AS v ON v.Ordinal = CAST (e.[key] AS INT) + 1")
        w(f"         CROSS APPLY OPENJSON (e.[value], {element_path(owner)}) AS pe")
        w(f"          JOIN {owner.qualified} AS p")
        w("            ON p.HandlerSourceId = v.HandlerSourceId")
        w("           AND p.OrdinalPosition = CAST (pe.[key] AS INT)")
        w("           AND p.IsDeleted       = 0")
        w("         CROSS APPLY (VALUES")
        for i, kid in enumerate(kids):
            comma = " " if i == 0 else ","
            w(f"                       {comma} (N'{kid.name}', N'{display_path(kid)}'")
            w(f"                         , JSON_PATH_EXISTS (pe.[value], {element_path(kid)}))")
        w("                     ) AS g (TableName, JsonPath, Sent);")
        w("")

    def emit_one(table: Table) -> None:
        columns = [c for c in table.columns if c.json_path]
        pcol = table.parent_column
        width = max(len(c.name) for c in columns)
        bare = bare_column(table, columns)
        nested = table.parent_table != PARENT_TABLE
        owner_id = f"{table.parent_table}Id" if nested else None
        owner = next((c for c in children if c.name == table.parent_table), None) if nested else None

        w("        " + "-" * 89)
        w(f"        --    {SCHEMA}.{table.name}")
        w(f"        --    <- {display_path(table)}")
        w("        " + "-" * 89)
        w("        WITH Batch AS")
        w("        (")
        w(f"            SELECT c.{pcol}")
        w("                 , c.OrdinalPosition")
        for col in columns:
            w(f"                 , c.{col.name}")
        for col in ("IsDeleted", "auditDeletedBy", "auditDeletedDateUtc",
                    "auditModifiedBy", "auditModifiedDateUtc"):
            w(f"                 , c.{col}")
        w(f"              FROM {table.qualified} AS c WITH (HOLDLOCK)")
        if nested:
            w(f"             WHERE c.{pcol} IN")
            w(f"                   (SELECT p.{owner_id}")
            w(f"                      FROM {SCHEMA}.{table.parent_table} AS p")
            w("                     WHERE p.HandlerSourceId IN (SELECT v.HandlerSourceId FROM @Version AS v))")
        else:
            w(f"             WHERE c.{pcol} IN (SELECT v.HandlerSourceId FROM @Version AS v)")
        w("        )")
        w("        MERGE Batch AS tgt")
        w(f"        USING (SELECT {'p.' + owner_id if nested else 'v.HandlerSourceId'}")
        w("                    , CAST (arr.[key] AS INT) AS OrdinalPosition")
        if bare is not None:
            # A bare-string array has no property to path to, so the element IS the value. The CAST is
            # explicit because arr.[value] is NVARCHAR (4000) and the column is not.
            w(f"                    , CAST (arr.[value] AS {bare.sql_type}) AS {bare.name}")
        else:
            for col in columns:
                w(f"                    , j.{col.name}")
        w("                 FROM OPENJSON (@Payload) AS e")
        w("                 JOIN @Version AS v ON v.Ordinal = CAST (e.[key] AS INT) + 1")
        if nested:
            w(f"                CROSS APPLY OPENJSON (e.[value], {element_path(owner)}) AS pe")
            w(f"                 JOIN {SCHEMA}.{table.parent_table} AS p")
            w("                   ON p.HandlerSourceId = v.HandlerSourceId")
            w("                  AND p.OrdinalPosition = CAST (pe.[key] AS INT)")
            w("                  AND p.IsDeleted       = 0")
            w(f"                CROSS APPLY OPENJSON (pe.[value], {element_path(table)}) AS arr")
        else:
            w(f"                CROSS APPLY OPENJSON (e.[value], {element_path(table)}) AS arr")
        if bare is None:
            type_width = max(len(c.sql_type) for c in columns)
            w("                CROSS APPLY OPENJSON (arr.[value])")
            w("                     WITH (")
            for i, col in enumerate(columns):
                comma = " " if i == 0 else ","
                w(f"                           {comma} {col.name:<{width}}  {col.sql_type:<{type_width}}  "
                  f"'$.{col.json_path}'")
            w("                           ) AS j) AS src")
        else:
            out[-1] = out[-1] + ") AS src"
        w(f"           ON tgt.{pcol} = src.{pcol}")
        w("          AND tgt.OrdinalPosition = src.OrdinalPosition")
        w("        -- Unfiltered on IsDeleted, exactly like the parent, and for the same reason: a")
        w("        -- soft-deleted row whose ordinal EPA is sending again must be REVIVED, not")
        w("        -- duplicated. This is the descendant half of the revival obligation script 522")
        w("        -- records and cannot discharge from its own side.")
        w("        WHEN MATCHED AND (tgt.IsDeleted = 1")
        for col in columns:
            w(f"                       OR tgt.{col.name:<{width}} IS DISTINCT FROM src.{col.name}")
        w("                         ) THEN")
        w("            UPDATE SET")
        for i, col in enumerate(columns):
            comma = " " if i == 0 else ","
            w(f"                  {comma} {col.name:<{width}} = src.{col.name}")
        w(f"                  , {'IsDeleted':<{width}} = 0")
        w(f"                  , {'auditModifiedBy':<{width}} = ORIGINAL_LOGIN ()")
        w(f"                  , {'auditModifiedDateUtc':<{width}} = @NowUtc")
        w("        WHEN NOT MATCHED BY TARGET THEN")
        w("            -- All seven audit columns omitted, so every DEFAULT fires.")
        w(f"            INSERT ({pcol}, OrdinalPosition")
        for col in columns:
            w(f"                  , {col.name}")
        w("                   )")
        w(f"            VALUES (src.{pcol}, src.OrdinalPosition")
        for col in columns:
            w(f"                  , src.{col.name}")
        w("                   )")
        w("        -- The gate. Without the EXISTS this branch would retire on a renamed property.")
        w("        --")
        w("        -- IsDeleted = 0 is not redundant with it. An already-retired row is NOT MATCHED BY")
        w("        -- SOURCE on every later run too, so without this test every run would re-stamp")
        w("        -- auditDeletedDateUtc on rows retired weeks ago -- the audit trail would claim they")
        w("        -- were deleted tonight, and childRetired would never fall back to zero, which is")
        w("        -- exactly the signal a renamed property is supposed to raise.")
        w("        WHEN NOT MATCHED BY SOURCE")
        w("         AND tgt.IsDeleted = 0")
        w("         AND EXISTS (SELECT 1 FROM @Collection AS g")
        w(f"                      WHERE g.TableName = N'{table.name}'")
        w(f"                        AND g.ParentId  = tgt.{pcol}")
        w("                        AND g.Sent      = 1) THEN")
        w("            UPDATE SET IsDeleted            = 1")
        w("                     , auditDeletedBy       = ORIGINAL_LOGIN ()")
        w("                     , auditDeletedDateUtc  = @NowUtc")
        w("                     , auditModifiedBy      = ORIGINAL_LOGIN ()")
        w("                     , auditModifiedDateUtc = @NowUtc")
        w(f"        OUTPUT N'{table.name}', $action, inserted.IsDeleted")
        w("          INTO @ChildAction (TableName, Action, IsDeleted);")
        w("")

    for table in children:
        emit_one(table)
        if table.name in grandchildren:
            emit_gate(table)

    # ---- totals, and the observation an absent collection earns ------------------------------------
    w("        -- $action is 'UPDATE' for both a revive and a retire, so the retire count comes from")
    w("        -- inserted.IsDeleted rather than from $action. It is the number that matters most in")
    w("        -- this section: a renamed property's signature is a retire count that jumps.")
    w("        SELECT @ChildRows    = COUNT (*)")
    w("             , @ChildRetired = SUM (CASE WHEN Action = N'UPDATE' AND IsDeleted = 1")
    w("                                        THEN 1 ELSE 0 END)")
    w("          FROM @ChildAction;")
    w("")
    w("        SET @ChildRetired = COALESCE (@ChildRetired, 0);")
    w("")
    w("        SELECT @AbsentCollections = COUNT (*)")
    w("          FROM (SELECT g.TableName")
    w("                  FROM @Collection AS g")
    w("                 GROUP BY g.TableName")
    w("                HAVING SUM (CASE WHEN g.Sent = 1 THEN 1 ELSE 0 END) = 0) AS d;")
    w("")
    w("        -- One observation per collection per batch, and ONLY for the combination that is")
    w("        -- actually diagnostic: EPA sent the property for no element in the batch, AND this")
    w("        -- database holds live rows for those same parents. That pair is the rename signature.")
    w("        -- Absent with nothing stored is unremarkable -- most handlers have no episodic wastes")
    w("        -- and never will -- and writing a row for it would put roughly fifteen rows per handler")
    w("        -- per night into a table whose whole value is that a row in it means something.")
    w("        --")
    w("        -- LoadRunId is NOT NULL on logs.DataQualityObservation, so a call with no run to")
    w("        -- attribute has nowhere to record this. The count still reaches Comments below.")
    w("        IF @LoadRunId IS NOT NULL AND @AbsentCollections > 0")
    w("        BEGIN")
    w("            WITH Absent (TableName, JsonPath, Parents) AS")
    w("            (")
    w("                SELECT g.TableName, g.JsonPath, COUNT (*)")
    w("                  FROM @Collection AS g")
    w("                 GROUP BY g.TableName, g.JsonPath")
    w("                HAVING SUM (CASE WHEN g.Sent = 1 THEN 1 ELSE 0 END) = 0")
    w("            )")
    w("            , Stored (TableName) AS")
    w("            (")
    for i, table in enumerate(children):
        if i:
            w("                UNION ALL")
        w(f"                SELECT N'{table.name}'")
        w("                 WHERE EXISTS (SELECT 1")
        w(f"                                 FROM {table.qualified} AS x")
        w("                                 JOIN @Collection AS g")
        w(f"                                   ON g.TableName = N'{table.name}'")
        w(f"                                  AND g.ParentId  = x.{table.parent_column}")
        w("                                WHERE x.IsDeleted = 0)")
    w("            )")
    w("            INSERT INTO logs.DataQualityObservation")
    w("                  (LoadRunId, ObservationType, Severity, TableName, JsonPath, ObservedValue")
    w("                 , Detail, ObservedDateUtc)")
    w("            SELECT @LoadRunId")
    w("                 , N'CollectionAbsentButStored'")
    w("                 , N'Warning'")
    w("                 , a.TableName")
    w("                 , a.JsonPath")
    w("                 , CONCAT (N'absent from all ', a.Parents, N' parent element(s)')")
    w("                 , CONCAT (N'EPA sent no ', a.JsonPath, N' property for any of the ', a.Parents")
    w("                          , N' parent element(s) in this batch, yet this database holds live ')")
    w("                          + CONCAT (N'rows in ', a.TableName, N' for them. Nothing was retired: ')")
    w("                          + N'an absent property is not evidence of none, and is equally "
      "consistent '")
    w("                          + N'with EPA having renamed it. Compare the JsonPath above against the '")
    w("                          + N'current API spec before treating the stored rows as stale.'")
    w("                 , @NowUtc")
    w("              FROM Absent AS a")
    w("              JOIN Stored AS s ON s.TableName = a.TableName;")
    w("")
    w("            SET @Observations = @@ROWCOUNT;")
    w("        END;")
    w("")
    return out


def emit_merge_procedure(parent: Table, children: list[Table]) -> str:
    """dbo.uspMergeHandlerSourceBatch -- the set-based write, generated from the same spec and the
    same column_name() that produced dbo.HandlerSource (Phase1-Plan.md D3, [R11])."""
    payload = [c for c in parent.columns if c.json_path]
    key = list(parent.unique_index)                       # HandlerId, SourceType, Sequence
    changing = [c for c in payload if c.name not in key]   # the join settles the key columns

    missing = [k for k in key if k not in {c.name for c in payload}]
    if missing:
        raise SystemExit(
            f"FAIL  generate_schema: the natural key column(s) {missing} carry no JSON path, so the "
            f"merge procedure would join on a column it cannot shred out of the payload.")

    name_width = max(len(c.name) for c in payload)
    type_width = max(len(c.sql_type) for c in payload)

    out: list[str] = []
    w = out.append

    # SET XACT_ABORT ON is emitted ABOVE the header block, not below it. The GO that follows ends the
    # batch, and sys.sql_modules stores only the batch containing CREATE -- so a header after that GO
    # never reaches the database, and sp_helptext, OBJECT_DEFINITION and SSMS "Script as CREATE" all
    # show a procedure with no header at all. The generated VIEWS never had this problem because
    # nothing separates their header from their CREATE; every procedure did, until it was corrected.
    # validate-sql.py now rejects a batch separator between the header and its CREATE.
    for line in wrap(
            "SET XACT_ABORT ON sits ABOVE the header block deliberately, and moving it below would "
            "restore a real defect: the GO on the next line ends the batch, sys.sql_modules stores "
            "only the batch that contains CREATE, and a header after the GO is invisible to anyone "
            "reading this procedure out of the database. The header must be the last thing before "
            "CREATE with no batch separator between them.", 116):
        w(f"-- {line}" if line else "--")
    w("SET XACT_ABORT ON;")
    for line in QUOTED_IDENTIFIER_PREAMBLE:
        w(line)
    w("GO")
    w("")

    w("/" + "*" * 118)
    w(f"ObjectName:   {SCHEMA}.{MERGE_PROCEDURE}")
    w("Author:       generated by build/generate_schema.py")
    w("CreateDate:   2026-09-05")
    w("=" * 120)
    w("Description:")
    w("")
    for line in wrap(
            f"Merges a batch of handler source records into {SCHEMA}.{PARENT_TABLE}, "
            f"{SCHEMA}.{RAW_JSON_TABLE} and all {len(children)} child collection tables from one JSON "
            f"payload, in one transaction. Set-based: a "
            f"single record is a one-element array, and there is deliberately no singleton variant "
            f"beside this one. Returns one row per record that was inserted or updated; a record the "
            f"caller sent that is absent from the result set was already current and was not "
            f"touched.", 118):
        w(line)
    w("")
    w(f"{GENERATED_BANNER} Regenerate with:")
    w("")
    w("    python build/generate_schema.py")
    w("")
    for line in wrap(
            f"The OPENJSON ... WITH mapping below is produced by the same column_name() function that "
            f"produced the {len(payload)} payload columns of {SCHEMA}.{PARENT_TABLE}, from the same "
            f"pinned spec. That is the whole reason this procedure is generated rather than typed: a "
            f"hand-written mapping is a second source of truth for the one mapping this project "
            f"cannot afford to have two of, and a single mis-cased property name shreds to NULL "
            f"without erroring. See Phase1-Plan.md D3.", 118):
        w(line)
    w("")
    w("build/guardrails.py runs `generate_schema.py --check`, which fails if this file and the spec")
    w("have drifted apart, so editing it here is not merely discouraged -- it breaks the build.")
    w("")
    w("=" * 120)
    w("Requirements and Key Dependencies:")
    w("")
    w(f"{SCHEMA}.{PARENT_TABLE}, {SCHEMA}.{RAW_JSON_TABLE}, and the {len(children)} child collection tables:")
    w("")
    for table in children:
        w(f"    {SCHEMA}.{table.name:<44} <- $.{PAYLOAD_HANDLER}."
          f"{ {name: path for path, name in CHILD_TABLE_NAMES.items()}[table.name] }")
    w("")
    w("logs.DataQualityObservation, for a collection EPA sent for no element in the batch while this")
    w("database holds live rows for it. See the RETIRING note below.")
    w("")
    w("logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation")
    w("block. It is copied verbatim from .claude/skills/sql-objects/templates/procedure.sql.")
    w("")
    w("=" * 120)
    w("Notes:")
    w("")
    for block in MERGE_PROCEDURE_NOTES:
        for line in wrap(block, 118):
            w(line)
        w("")
    w("=" * 120)
    w("Example Usage and Performance:")
    w("")
    w("DECLARE @Payload NVARCHAR (MAX) = N'[{\"retrievedDateUtc\":\"2026-09-05T11:02:31\",")
    w("                                      \"handler\":{\"handlerId\":\"MD0000123456\",")
    w("                                                 \"activityLocation\":\"MD\",")
    w("                                                 \"type\":{\"code\":\"N\"},\"sequence\":1}}]';")
    w("")
    w(f"EXEC {SCHEMA}.{MERGE_PROCEDURE} @Payload = @Payload, @LoadRunId = 1;")
    w("")
    for line in wrap(
            f"The two parent statements seek IX_dbo_HandlerSource_Natural_All under HOLDLOCK; each "
            f"child MERGE seeks its own foreign-key index, restricted to this batch by the CTE that "
            f"is its target. Cost scales with the batch, and the payload is parsed once per "
            f"statement: twice for the parent and its RawJson row, once more for the retire gate, and "
            f"once per collection below that. MDE accepted that at the DA1 design review rather than "
            f"pre-optimising it -- F2 measures throughput at several batch sizes, and if the parse "
            f"dominates, the first remedy is shredding once into a temporary table, not a change of "
            f"set mechanism (G32 is decided). The AR5 status writes add one seek per handler on an "
            f"index that is already hot from the merge above them.", 118):
        w(line)
    w("")
    w("=" * 120)
    w("Modification History:")
    w("Date\t\tAuthor          Ticket    \t\tDescription")
    w("---------- \t--------------- -----------\t\t" + "-" * 76)
    w("2026-09-05\tgenerated\t\t\t\t\t\tInitial version. Workstream DA1.")
    w("*" * 118 + "/")
    # Nothing between the header block and CREATE -- not a GO, not a SET. Both are emitted above the
    # header instead. See the note at the top of this function.
    w("")
    w(f"CREATE OR ALTER PROCEDURE {SCHEMA}.{MERGE_PROCEDURE}")
    w("      @Payload   NVARCHAR (MAX)")
    w("    , @LoadRunId INT = NULL")
    w("AS")
    w("BEGIN")
    w("    SET NOCOUNT ON;")
    w("    SET XACT_ABORT ON;")
    w("")
    w("    " + "-" * 93)
    w("    -- AR8 instrumentation. Boilerplate: copy verbatim.")
    w("    " + "-" * 93)
    # The literal is generated from the same MERGE_PROCEDURE constant as the CREATE OR ALTER above, so
    # the two cannot drift -- which is the whole reason the fallback can safely be a literal at all.
    # See "@ProcName FALLS BACK TO A LITERAL" in the emitted header for why it must not be a placeholder.
    w("    -- Not a fallback for odd cases: this literal is what the loader login actually logs,")
    w("    -- because metadata visibility is denied to it. See the header note.")
    w("    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))")
    w("                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))")
    w(f"                                                    , N'[{SCHEMA}].[{MERGE_PROCEDURE}]')")
    w("          , @StartTimeUtc   DATETIME2      = SYSUTCDATETIME ()")
    w("          , @EndTimeUtc     DATETIME2      = NULL")
    w("          , @ExecutionId    BIGINT         = NULL")
    w("          , @KeyParameters  NVARCHAR (MAX) = NULL")
    w("          , @Comments       NVARCHAR (MAX) = NULL")
    w("          , @ContextMessage NVARCHAR (MAX) = NULL")
    w("          , @DynamicSql     NVARCHAR (MAX) = NULL")
    w("          , @ErrorMsg       NVARCHAR (MAX) = NULL")
    w("          , @ErrorProc      NVARCHAR (300) = NULL")
    w("          , @ErrorNumber    INT            = NULL")
    w("          , @ErrorLine      INT            = NULL;")
    w("")
    w("    " + "-" * 93)
    w("    -- This procedure's own state.")
    w("    " + "-" * 93)
    w("    DECLARE @ElementCount INT            = 0")
    w("          , @Inserted     INT            = 0")
    w("          , @Updated      INT            = 0")
    w("          , @RawJsonRows  INT            = 0")
    w("          , @StatusRows   INT            = 0")
    w("          , @StatusMissing INT           = 0")
    w("          , @FailedStatusRows INT        = 0")
    w("          , @ChildRows    INT            = 0")
    w("          , @ChildRetired INT            = 0")
    w("          , @AbsentCollections INT       = 0")
    w("          , @Observations INT            = 0")
    w("          , @NowUtc       DATETIME2      = NULL")
    w("          , @Failure      NVARCHAR (2048) = NULL;")
    w("")
    w("    -- The natural key of every element, shredded once for validation. Deliberately WIDER than")
    w("    -- the target columns: narrowing here would truncate a bad HandlerId into a valid-looking")
    w("    -- one and merge it into the wrong handler's row, which is the one failure in this")
    w("    -- procedure that corrupts data instead of raising.")
    w("    DECLARE @Key TABLE")
    w("    (")
    w("        Ordinal          INT             NOT NULL PRIMARY KEY,")
    w("        HandlerId        NVARCHAR (4000)     NULL,")
    w("        ActivityLocation NVARCHAR (4000)     NULL,")
    w("        SourceType       NVARCHAR (4000)     NULL,")
    w("        Sequence         INT                 NULL")
    w("    );")
    w("")
    w("    -- MERGE's OUTPUT, and the procedure's result set. A table variable is not rolled back,")
    w("    -- which is why the rows are still here in the CATCH block -- see G35 in the header.")
    w("    DECLARE @Merged TABLE")
    w("    (")
    w("        Action          NVARCHAR (10) NOT NULL,")
    w("        HandlerSourceId INT           NOT NULL PRIMARY KEY,")
    w("        HandlerId       NVARCHAR (12) NOT NULL,")
    w("        SourceType      NVARCHAR (1)  NOT NULL,")
    w("        Sequence        INT           NOT NULL")
    w("    );")
    w("")
    w("    -- The batch's surrogate keys, resolved once after the parent merge. Every child statement")
    w("    -- below joins this instead of re-resolving the natural key, and it is what restricts each")
    w("    -- child MERGE's target to this batch rather than to the whole table.")
    w("    DECLARE @Version TABLE")
    w("    (")
    w("        Ordinal         INT NOT NULL PRIMARY KEY,")
    w("        HandlerSourceId INT NOT NULL UNIQUE")
    w("    );")
    w("")
    w("    -- The retire gate: for each collection and each parent row in the batch, did EPA actually")
    w("    -- SEND the property? Sent = 0 is not the same fact as an empty array, and this table exists")
    w("    -- so that the difference survives into the MERGE statements, which cannot see it.")
    w("    DECLARE @Collection TABLE")
    w("    (")
    w("        TableName NVARCHAR (128) NOT NULL,")
    w("        JsonPath  NVARCHAR (400) NOT NULL,")
    w("        ParentId  INT            NOT NULL,")
    w("        Sent      BIT            NOT NULL,")
    w("        PRIMARY KEY (TableName, ParentId)")
    w("    );")
    w("")
    w("    -- One row per child row actually written. Matched-but-unchanged rows produce nothing, so in")
    w("    -- steady state this stays near empty; a first load fills it, which is the cost of being able")
    w("    -- to report a retire count at all. IsDeleted is captured because $action says 'UPDATE' for a")
    w("    -- revive and a retire alike.")
    w("    DECLARE @ChildAction TABLE")
    w("    (")
    w("        TableName NVARCHAR (128) NOT NULL,")
    w("        Action    NVARCHAR (10)  NOT NULL,")
    w("        IsDeleted BIT            NOT NULL")
    w("    );")
    w("")
    w("    -- Identifiers and counts ONLY. @Payload is excluded BY NAME: it carries thousands of")
    w("    -- regulated-entity records, and logs.ExecutionLog has a different read audience and a")
    w("    -- different retention policy from the tables the payload lands in. A byte count is not the")
    w("    -- payload, and it is the figure a capacity question actually asks for.")
    w("    SET @KeyParameters = CONCAT (N'LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(null)')")
    w("                               , N', PayloadBytes=', COALESCE (DATALENGTH (@Payload), 0));")
    w("")
    w("    BEGIN TRY")
    w("")
    w("        EXEC logs.uspStartExecutionLogging")
    w("              @ProcedureName          = @ProcName")
    w("            , @KeyParameters          = @KeyParameters")
    w("            , @StartDateUtc           = @StartTimeUtc")
    w("            , @ReCreatedAfterRollback = 0")
    w("            , @ExecutionLogId         = @ExecutionId OUTPUT;")
    w("")
    w("        -- " + "=" * 86)
    w("        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ==")
    w("        -- " + "=" * 86)
    w("")
    w("        " + "-" * 89)
    w("        -- 1. Validation. BEFORE BEGIN TRANSACTION, so a rejected payload is logged as a failed")
    w("        --    execution like any other and there is no transaction to unwind.")
    w("        " + "-" * 89)
    w("        IF @Payload IS NULL OR ISJSON (@Payload, ARRAY) = 0")
    w("        BEGIN")
    w("            -- Without this, malformed JSON shreds to ZERO ROWS and the merge reports success.")
    w("            -- The ARRAY constraint is not decoration. A caller that sends one handler as a bare")
    w("            -- object instead of a one-element array gets an OPENJSON that enumerates its")
    w("            -- PROPERTIES, so every path below misses and the batch merges NULLs -- and the")
    w("            -- ordinal CAST below assumes an array key too. ISJSON's type argument is SQL Server")
    w("            -- 2022 and therefore in scope for the target platform.")
    w("            SET @Failure = N'@Payload is NULL, is not valid JSON, or is not a JSON array. It "
      "must be an array of '")
    w("                         + N'{\"retrievedDateUtc\":...,\"handler\":{...}} envelopes, even for a "
      "single record. Nothing was merged.';")
    w("            THROW 50000, @Failure, 1;")
    w("        END;")
    w("")
    w("        INSERT INTO @Key (Ordinal, HandlerId, ActivityLocation, SourceType, Sequence)")
    w("        SELECT CAST (e.[key] AS INT) + 1")
    w(f"             , JSON_VALUE (e.value, '$.{PAYLOAD_HANDLER}.{column_json_source(payload, 'HandlerId')}')")
    w(f"             , JSON_VALUE (e.value, '$.{PAYLOAD_HANDLER}.{column_json_source(payload, 'ActivityLocation')}')")
    w(f"             , JSON_VALUE (e.value, '$.{PAYLOAD_HANDLER}.{column_json_source(payload, 'SourceType')}')")
    w(f"             , TRY_CAST (JSON_VALUE (e.value, '$.{PAYLOAD_HANDLER}.{column_json_source(payload, 'Sequence')}') AS INT)")
    w("          FROM OPENJSON (@Payload) AS e;")
    w("")
    w("        SET @ElementCount = @@ROWCOUNT;")
    w("")
    w("        -- An empty array is NOT an error. The loader should not call with nothing to do, but")
    w("        -- turning a harmless no-op into a failed run would make an unattended load report a")
    w("        -- problem it does not have. It is recorded in Comments instead.")
    w("")
    w("        IF EXISTS (SELECT 1 FROM @Key")
    w("                    WHERE HandlerId IS NULL OR ActivityLocation IS NULL")
    w("                       OR SourceType IS NULL OR Sequence IS NULL)")
    w("        BEGIN")
    w("            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount")
    w("                                    , N' is missing part of the natural key (handlerId, ')")
    w("                                    + N'activityLocation, type.code, sequence). The four NOT NULL '")
    w("                                    + N'columns cannot be defaulted, so the batch is rejected whole.'")
    w("              FROM @Key")
    w("             WHERE HandlerId IS NULL OR ActivityLocation IS NULL")
    w("                OR SourceType IS NULL OR Sequence IS NULL;")
    w("            THROW 50000, @Failure, 1;")
    w("        END;")
    w("")
    w("        -- A key wider than its column would be TRUNCATED by the merge below, silently, into a")
    w("        -- different handler's key. Length is checked here, against the widths the generator put")
    w("        -- on dbo.HandlerSource, rather than trusted to the loader.")
    w("        IF EXISTS (SELECT 1 FROM @Key")
    w(f"                    WHERE LEN (HandlerId)        > {column_length(payload, 'HandlerId')}")
    w(f"                       OR LEN (ActivityLocation) > {column_length(payload, 'ActivityLocation')}")
    w(f"                       OR LEN (SourceType)       > {column_length(payload, 'SourceType')})")
    w("        BEGIN")
    w("            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal)")
    w("                                    , N' has a natural-key value wider than the column that ')")
    w("                                    + N'stores it, which would truncate into a different handler. '")
    w("                                    + N'The batch is rejected whole.'")
    w("              FROM @Key")
    w(f"             WHERE LEN (HandlerId)        > {column_length(payload, 'HandlerId')}")
    w(f"                OR LEN (ActivityLocation) > {column_length(payload, 'ActivityLocation')}")
    w(f"                OR LEN (SourceType)       > {column_length(payload, 'SourceType')};")
    w("            THROW 50000, @Failure, 1;")
    w("        END;")
    w("")
    w("        -- Two elements with the same natural key make MERGE raise 8672, 'attempted to UPDATE or")
    w("        -- DELETE the same row more than once', from inside the transaction. Caught here so the")
    w("        -- message names the duplicate instead of the statement. Deduplicating with ROW_NUMBER")
    w("        -- would be the wrong repair: this is a mirror, and quietly discarding one of two")
    w("        -- conflicting versions of the same record loses data EPA sent.")
    w("        IF EXISTS (SELECT 1 FROM @Key")
    w("                   GROUP BY HandlerId, SourceType, Sequence")
    w("                   HAVING COUNT (*) > 1)")
    w("        BEGIN")
    w("            -- The ORDER BY is inside the derived table on purpose: a SELECT that assigns a")
    w("            -- variable is not a data-retrieval statement, and ordering one is at best")
    w("            -- undefined. TOP (1) over an ordered derived table is defined.")
    w("            SELECT @Failure = CONCAT (N'The batch contains ', d.Copies")
    w("                                    , N' elements with the natural key ('")
    w("                                    , d.HandlerId, N', ', d.SourceType, N', ', d.Sequence")
    w("                                    , N'). MERGE cannot apply two versions of one row in one ')")
    w("                                    + N'statement, so the batch is rejected whole.'")
    w("              FROM (SELECT TOP (1) HandlerId, SourceType, Sequence, COUNT (*) AS Copies")
    w("                      FROM @Key")
    w("                     GROUP BY HandlerId, SourceType, Sequence")
    w("                    HAVING COUNT (*) > 1")
    w("                     ORDER BY COUNT (*) DESC, HandlerId, SourceType, Sequence) AS d;")
    w("            THROW 50000, @Failure, 1;")
    w("        END;")
    w("")
    w("        -- Set once the payload is known to be shreddable, so a failure in either MERGE below")
    w("        -- reaches logs.ExecutionLog.ContextMessage with the batch size attached.")
    w("        SET @ContextMessage = CONCAT (N'Elements=', @ElementCount);")
    w("")
    w("        -- One timestamp for the whole batch. SYSUTCDATETIME () inline would be evaluated per")
    w("        -- statement, so the two MERGE statements would stamp the same logical change with two")
    w("        -- different auditModifiedDateUtc values.")
    w("        SET @NowUtc = SYSUTCDATETIME ();")
    w("")
    w("        BEGIN TRANSACTION;")
    w("")
    w("        " + "-" * 89)
    w(f"        -- 2. The parent. {len(payload)} columns, shredded by explicit path and explicit type.")
    w("        " + "-" * 89)
    w(f"        MERGE {SCHEMA}.{PARENT_TABLE} WITH (HOLDLOCK) AS tgt")
    w("        USING (SELECT *")
    w("                 FROM OPENJSON (@Payload)")
    w("                      WITH (")

    for i, col in enumerate(payload):
        comma = " " if i == 0 else ","
        w(f"                           {comma} {col.name:<{name_width}}  {col.sql_type:<{type_width}}  "
          f"{json_path(col.json_path)}")

    w("                           ) AS j) AS src")
    key_width = max(len(k) for k in key)
    for i, k in enumerate(key):
        w(f"{'ON' if i == 0 else 'AND':>13} tgt.{k:<{key_width}} = src.{k}")
    w("        -- IsDeleted = 1 is the FIRST test on purpose. A soft-deleted row whose columns all")
    w("        -- still match the payload has nothing to change, and without this test the row would")
    w("        -- stay deleted even though EPA just sent it again -- a handler silently missing from")
    w("        -- every view in the database.")
    w("        WHEN MATCHED AND (tgt.IsDeleted = 1")
    for col in changing:
        w(f"                       OR tgt.{col.name:<{name_width}} IS DISTINCT FROM src.{col.name}")
    w("                         ) THEN")
    w("            -- No hash, no rowversion: the change test above compares the columns THIS database")
    w("            -- stores. A payload-hash test would report every row as changed the day EPA")
    w("            -- reorders a property or respaces its JSON, moving auditModifiedDateUtc on the whole")
    w("            -- table and making the audit trail claim a change that did not happen.")
    w("            UPDATE SET")
    for i, col in enumerate(changing):
        comma = " " if i == 0 else ","
        w(f"                  {comma} {col.name:<{name_width}} = src.{col.name}")
    w(f"                  , {'IsDeleted':<{name_width}} = 0")
    w(f"                  , {'auditModifiedBy':<{name_width}} = ORIGINAL_LOGIN ()")
    w(f"                  , {'auditModifiedDateUtc':<{name_width}} = @NowUtc")
    w("        WHEN NOT MATCHED BY TARGET THEN")
    w("            -- All seven audit columns are omitted, so every DEFAULT fires. Listing")
    w("            -- auditCreatedBy here would hand the stamping to whatever this statement passes.")
    w("            INSERT (")
    for i, col in enumerate(payload):
        comma = " " if i == 0 else ","
        w(f"                     {comma} {col.name}")
    w("                     )")
    w("            VALUES (")
    for i, col in enumerate(payload):
        comma = " " if i == 0 else ","
        w(f"                     {comma} src.{col.name}")
    w("                     )")
    w("        OUTPUT $action, inserted.HandlerSourceId, inserted.HandlerId")
    w("             , inserted.SourceType, inserted.Sequence")
    w("          INTO @Merged (Action, HandlerSourceId, HandlerId, SourceType, Sequence);")
    w("")
    w("        -- SUM ... ELSE 0, not COUNT (CASE ...): COUNT over a CASE with no ELSE counts by")
    w("        -- discarding NULLs, which raises warning 8153 ('Null value is eliminated by an")
    w("        -- aggregate') on every successful batch. The loader would collect that as an info")
    w("        -- message on every call and the one that matters would be lost in it.")
    w("        SELECT @Inserted = SUM (CASE WHEN Action = N'INSERT' THEN 1 ELSE 0 END)")
    w("             , @Updated  = SUM (CASE WHEN Action = N'UPDATE' THEN 1 ELSE 0 END)")
    w("          FROM @Merged;")
    w("")
    w("        -- SUM of no rows is NULL, and CONCAT would then render the counts as empty strings.")
    w("        SELECT @Inserted = COALESCE (@Inserted, 0), @Updated = COALESCE (@Updated, 0);")
    w("")
    w("        " + "-" * 89)
    w(f"        -- 3. {SCHEMA}.{RAW_JSON_TABLE}. Merged for EVERY element, not only the ones the parent")
    w("        --    merge touched: the omitted addendum subtree and any field this mirror does not")
    w("        --    model can change while all mirrored columns stay identical, and RawJson is what")
    w("        --    makes that recoverable by re-projection instead of by re-fetching from EPA.")
    w("        " + "-" * 89)
    w(f"        MERGE {SCHEMA}.{RAW_JSON_TABLE} WITH (HOLDLOCK) AS tgt")
    w("        USING (SELECT hs.HandlerSourceId")
    w("                    , j.RawJson")
    w("                    , j.RetrievedDateUtc")
    w("                    -- Hashed from the text being STORED, by the engine storing it, so the")
    w("                    -- hash and the payload cannot disagree. The loader's own hash stays on")
    w("                    -- the .NET side for its skip decisions and never crosses the boundary:")
    w("                    -- two independently computed hashes of 'the same' JSON is a promise about")
    w("                    -- serialization that no serializer makes.")
    w("                    , CONVERT (NVARCHAR (64), HASHBYTES ('SHA2_256', j.RawJson), 2) AS PayloadSha256")
    w("                 FROM OPENJSON (@Payload)")
    w("                      WITH (")
    for i, k in enumerate(key):
        col = next(c for c in payload if c.name == k)
        comma = " " if i == 0 else ","
        w(f"                           {comma} {col.name:<16}  {col.sql_type:<15}  {json_path(col.json_path)}")
    w(f"                           , {'RawJson':<16}  {'NVARCHAR (MAX)':<15}  '$.{PAYLOAD_HANDLER}' AS JSON")
    w(f"                           , {'RetrievedDateUtc':<16}  {'DATETIME2':<15}  '$.{PAYLOAD_RETRIEVED}'")
    w("                           ) AS j")
    w(f"                 JOIN {SCHEMA}.{PARENT_TABLE} AS hs")
    for i, k in enumerate(key):
        w(f"{'ON' if i == 0 else 'AND':>21} hs.{k:<{key_width}} = j.{k}")
    w("                  -- Safe here and only here: statement 2 has already revived every row in this")
    w("                  -- batch, so IsDeleted = 0 is the whole batch and the seek uses the filtered")
    w("                  -- unique index rather than scanning history.")
    w("                  AND hs.IsDeleted = 0) AS src")
    w("           ON tgt.HandlerSourceId = src.HandlerSourceId")
    w("        WHEN MATCHED AND (tgt.IsDeleted = 1")
    w("                       OR tgt.PayloadSha256 IS DISTINCT FROM src.PayloadSha256) THEN")
    w("            UPDATE SET RawJson              = src.RawJson")
    w("                     , PayloadSha256        = src.PayloadSha256")
    w("                     , RetrievedDateUtc     = src.RetrievedDateUtc")
    w("                     , IsDeleted            = 0")
    w("                     , auditModifiedBy      = ORIGINAL_LOGIN ()")
    w("                     , auditModifiedDateUtc = @NowUtc")
    w("        WHEN NOT MATCHED BY TARGET THEN")
    w("            INSERT (HandlerSourceId, RawJson, PayloadSha256, RetrievedDateUtc)")
    w("            VALUES (src.HandlerSourceId, src.RawJson, src.PayloadSha256, src.RetrievedDateUtc);")
    w("")
    w("        SET @RawJsonRows = @@ROWCOUNT;")
    w("")
    out.extend(emit_child_merges(parent, children))
    w("        " + "-" * 89)
    w("        -- 5. AR5 per-handler status (G35). INSIDE the transaction, so \"handler X was inserted\"")
    w("        --    is true only if the version it names actually committed. The failure half of this")
    w("        --    is in the CATCH block below, where it has to be.")
    w("        --")
    w("        --    UPDATE, never INSERT. These rows are written 'Pending' when the run ENUMERATES its")
    w("        --    work -- that is the resumability mechanism rather than an after-the-fact log -- so a")
    w("        --    handler with no row here was never enumerated, and inserting one would hide a loader")
    w("        --    defect by manufacturing the evidence for it. The shortfall is counted into Comments.")
    w("        " + "-" * 89)
    w("        IF @LoadRunId IS NOT NULL")
    w("        BEGIN")
    w("            UPDATE s")
    w("               SET Status               = N'Succeeded'")
    w("                 -- Absent from @Merged means the MERGE found nothing to change, which is")
    w("                 -- 'Unchanged' -- the count the caller derives by subtraction, recorded here per")
    w("                 -- handler so it does not have to.")
    w("                 , Outcome              = COALESCE (CASE m.Action")
    w("                                                        WHEN N'INSERT' THEN N'Inserted'")
    w("                                                        WHEN N'UPDATE' THEN N'Updated'")
    w("                                                    END, N'Unchanged')")
    w("                 , HandlerSourceId      = hs.HandlerSourceId")
    w("                 , PayloadSha256        = rj.PayloadSha256")
    w("                 , CompletedDateUtc     = @NowUtc")
    w("                 , ExecutionLogId       = @ExecutionId")
    w("                 , auditModifiedBy      = ORIGINAL_LOGIN ()")
    w("                 , auditModifiedDateUtc = @NowUtc")
    w("              FROM logs.HandlerLoadStatus AS s")
    w("              -- The CAST is on the VARIABLE side deliberately. @Key is NVARCHAR (4000) so that")
    w("              -- nothing can truncate before the width check runs; comparing it raw would make")
    w("              -- the engine widen s.HandlerId instead and turn the seek into a scan. By this")
    w("              -- point the width check has passed, so the CAST cannot lose anything.")
    w("              JOIN @Key AS k")
    for i, k in enumerate(key):
        col = next(c for c in payload if c.name == k)
        src = f"CAST (k.{k} AS {col.sql_type})" if col.sql_type.startswith("NVARCHAR") else f"k.{k}"
        w(f"{'ON' if i == 0 else 'AND':>17} s.{k:<{key_width}} = {src}")
    w("              -- Inner, and that is an assertion: statement 2 inserted or revived every element")
    w("              -- in this batch, so a missing live row here would mean the MERGE did not do what")
    w("              -- the rest of this procedure assumes.")
    w(f"              JOIN {SCHEMA}.{PARENT_TABLE} AS hs")
    for i, k in enumerate(key):
        w(f"{'ON' if i == 0 else 'AND':>17} hs.{k:<{key_width}} = s.{k}")
    w(f"{'AND':>17} hs.{'IsDeleted':<{key_width}} = 0")
    w("              LEFT JOIN @Merged AS m")
    w("                ON m.HandlerSourceId = hs.HandlerSourceId")
    w(f"              LEFT JOIN {SCHEMA}.{RAW_JSON_TABLE} AS rj")
    w("                ON rj.HandlerSourceId = hs.HandlerSourceId")
    w("               AND rj.IsDeleted       = 0")
    w("             WHERE s.LoadRunId = @LoadRunId")
    w("               AND s.IsDeleted = 0;")
    w("")
    w("            SET @StatusRows = @@ROWCOUNT;")
    w("")
    w("            -- @Key is unique on the natural key by now (the duplicate check above rejected the")
    w("            -- batch otherwise) and so is the index behind these rows, so the join is 1:1 at most")
    w("            -- and this subtraction cannot go negative.")
    w("            SET @StatusMissing = @ElementCount - @StatusRows;")
    w("        END;")
    w("")
    w("        SET @Comments = CONCAT (N'Elements=',   @ElementCount")
    w("                              , N', inserted=', @Inserted")
    w("                              , N', updated=',  @Updated")
    w("                              , N', unchanged=', @ElementCount - @Inserted - @Updated")
    w("                              , N', rawJson=',  @RawJsonRows")
    w("                              , N', childRows=', @ChildRows")
    w("                              , N', childRetired=', @ChildRetired")
    w("                              -- Named even when zero: 'absentCollections=0' is the line an")
    w("                              -- operator checks after an EPA release, and a figure that appears")
    w("                              -- only when it is non-zero cannot be checked for.")
    w("                              , N', absentCollections=', @AbsentCollections")
    w("                              , CASE WHEN @Observations > 0")
    w("                                     THEN CONCAT (N' (', @Observations, N' observed)')")
    w("                                     ELSE N'' END")
    w("                              -- Spelt out rather than left as 'status=0', which would read as a")
    w("                              -- fault when it only means the caller passed no run to attribute.")
    w("                              , CASE WHEN @LoadRunId IS NULL")
    w("                                     THEN N', status=n/a (no LoadRunId)'")
    w("                                     ELSE CONCAT (N', status=', @StatusRows")
    w("                                                , CASE WHEN @StatusMissing > 0")
    w("                                                       THEN CONCAT (N' (', @StatusMissing")
    w("                                                                  , N' not enumerated)')")
    w("                                                       ELSE N'' END)")
    w("                                END);")
    w("")
    w("        -- " + "=" * 86)
    w("        -- ===== End of the procedure's own work. ===================================================")
    w("        -- " + "=" * 86)
    w("")
    w("        IF @@TRANCOUNT > 0")
    w("        BEGIN")
    w("            COMMIT TRANSACTION;")
    w("        END;")
    w("")
    w("        -- Completion. Deliberately after the COMMIT; see the header for what that costs.")
    w("        -- auditModifiedDateUtc is set explicitly because its DEFAULT fires on INSERT only.")
    w("        SET @EndTimeUtc = SYSUTCDATETIME ();")
    w("")
    w("        IF @ExecutionId IS NOT NULL")
    w("        BEGIN")
    w("            UPDATE logs.ExecutionLog")
    w("               SET EndDateUtc           = @EndTimeUtc")
    w("                 , ElapsedMilliseconds  = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)")
    w("                                                     , CAST (2147483647 AS BIGINT)) AS INT)")
    w("                 , Successful           = 1")
    w("                 , Comments             = @Comments")
    w("                 , auditModifiedBy      = ORIGINAL_LOGIN ()")
    w("                 , auditModifiedDateUtc = @EndTimeUtc")
    w("             WHERE ExecutionLogId = @ExecutionId;")
    w("        END;")
    w("")
    w("        -- The result set, AFTER the commit: a client that reads rows slowly must not be able to")
    w("        -- hold the write transaction open. One row per record inserted or updated; a record the")
    w("        -- caller sent that is not here was already current. That subtraction is how the loader")
    w("        -- gets its unchanged count without this procedure parsing the payload a third time.")
    w("        SELECT m.HandlerSourceId")
    w("             , m.HandlerId")
    w("             , m.SourceType")
    w("             , m.Sequence")
    w("             , CASE m.Action WHEN N'INSERT' THEN N'Inserted' ELSE N'Updated' END AS Outcome")
    w("          FROM @Merged AS m")
    w("         ORDER BY m.HandlerId, m.SourceType, m.Sequence;")
    w("")
    w("    END TRY")
    w("    BEGIN CATCH")
    w("")
    w("        -- The ERROR_* functions are valid only in this scope and any statement can reset them, so")
    w("        -- capture them before doing anything else -- including before the rollback.")
    w("        SELECT @ErrorNumber = ERROR_NUMBER ()")
    w("             , @ErrorProc   = ERROR_PROCEDURE ()")
    w("             , @ErrorLine   = ERROR_LINE ()")
    w("             , @ErrorMsg    = ERROR_MESSAGE ()")
    w("                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))")
    w("                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';")
    w("")
    w("        -- One test, not two: XACT_ABORT ON makes XACT_STATE () = -1 the common case, and -1 and 1")
    w("        -- both need the same unqualified rollback.")
    w("        IF XACT_STATE () <> 0")
    w("        BEGIN")
    w("            ROLLBACK TRANSACTION;")
    w("        END;")
    w("")
    w("        -- The rollback destroyed the row logs.uspStartExecutionLogging wrote. Put it back, with the")
    w("        -- ORIGINAL @StartTimeUtc, or the only unrecorded executions in the database would be the")
    w("        -- failures. The nested TRY is required because the start procedure does not swallow: an")
    w("        -- error escaping here would replace the error being reported.")
    w("        BEGIN TRY")
    w("            IF @ExecutionId IS NULL")
    w("               OR NOT EXISTS (SELECT 1")
    w("                                FROM logs.ExecutionLog")
    w("                               WHERE ExecutionLogId = @ExecutionId)")
    w("            BEGIN")
    w("                EXEC logs.uspStartExecutionLogging")
    w("                      @ProcedureName          = @ProcName")
    w("                    , @KeyParameters          = @KeyParameters")
    w("                    , @StartDateUtc           = @StartTimeUtc")
    w("                    , @ReCreatedAfterRollback = 1")
    w("                    , @ExecutionLogId         = @ExecutionId OUTPUT;")
    w("            END;")
    w("        END TRY")
    w("        BEGIN CATCH")
    w("            SET @ExecutionId = NULL;")
    w("        END CATCH;")
    w("")
    w("        " + "-" * 89)
    w("        -- G35, ANSWERED HERE. The AR5 per-handler rows the rollback took away are written back")
    w("        -- from @Key, which still holds every element's natural key because a table variable is")
    w("        -- not transactional. That property is normally a footgun and is exactly the one required.")
    w("        --")
    w("        -- No new accumulator was needed: @Key already is one, and it is populated BEFORE")
    w("        -- BEGIN TRANSACTION. That is what makes this cover a VALIDATION failure too -- a batch")
    w("        -- rejected for a duplicate key never opened a transaction, and its handlers are still")
    w("        -- told they failed rather than left at 'Pending' for a monitoring grid to misread as")
    w("        -- work still in flight.")
    w("        --")
    w("        -- What is written, and what is deliberately NOT:")
    w("        --   Status          -> 'Failed'. Terminal here: the next attempt is the loader's next")
    w("        --                      call, which will set this row itself.")
    w("        --   Outcome         -> NULL. The rollback means NOTHING was applied, so stamping the")
    w("        --                      outcome the MERGE was going to produce would record work that")
    w("        --                      does not exist. @Merged is the tempting source and it is the")
    w("        --                      wrong one: those rows describe a transaction that un-happened.")
    w("        --   HandlerSourceId -> left alone. The rollback removed nothing that predated this call,")
    w("        --                      so whatever is in that column is still true.")
    w("        --   ExecutionLogId  -> @ExecutionId, which is the ONLY route from a Failed row to why it")
    w("        --                      failed: this table has no column for our own error text and")
    w("        --                      ApiErrorMessage must not be borrowed for one, being EPA's and")
    w("        --                      stored as received. The resurrection block above runs first for")
    w("        --                      this reason -- the row it puts back is the row named here.")
    w("        --")
    w("        -- Rows already 'Succeeded' are excluded. A Succeeded row names a committed version in")
    w("        -- HandlerSourceId, and overwriting it from a later failing call in the same run would")
    w("        -- make the status lie about data that is present.")
    w("        --")
    w("        -- Nested TRY, swallowing, for the same reason as the resurrection block: an error")
    w("        -- escaping here would replace the error being reported and the caller would be told the")
    w("        -- wrong thing entirely. The cost is that a failed flush loses these rows silently, which")
    w("        -- is why logs.HandlerLoadStatus.ExecutionLogId carries no foreign key -- see section 7 of")
    w("        -- script 310. It is recorded in ContextMessage instead of raised.")
    w("        " + "-" * 89)
    w("        BEGIN TRY")
    w("            IF @LoadRunId IS NOT NULL")
    w("            BEGIN")
    w("                UPDATE s")
    w("                   SET Status               = N'Failed'")
    w("                     , Outcome              = NULL")
    w("                     , CompletedDateUtc     = SYSUTCDATETIME ()")
    w("                     , ExecutionLogId       = @ExecutionId")
    w("                     , auditModifiedBy      = ORIGINAL_LOGIN ()")
    w("                     , auditModifiedDateUtc = SYSUTCDATETIME ()")
    w("                  FROM logs.HandlerLoadStatus AS s")
    w("                  JOIN @Key AS k")
    for i, k in enumerate(key):
        col = next(c for c in payload if c.name == k)
        src = f"CAST (k.{k} AS {col.sql_type})" if col.sql_type.startswith("NVARCHAR") else f"k.{k}"
        w(f"{'ON' if i == 0 else 'AND':>21} s.{k:<{key_width}} = {src}")
    w("                 WHERE s.LoadRunId = @LoadRunId")
    w("                   AND s.IsDeleted = 0")
    w("                   AND s.Status   <> N'Succeeded'")
    w("                   -- Unlike the success path, the width check may be WHAT THREW, so @Key can")
    w("                   -- hold a value wider than the column. Without these two tests the CAST above")
    w("                   -- would truncate it into some OTHER handler's key and mark that handler")
    w("                   -- failed -- the one mistake in this procedure that corrupts rather than")
    w("                   -- loses. An over-wide key simply matches nothing, which is correct: it never")
    w("                   -- named a real handler.")
    guards = [f"                   AND LEN (k.{k}) <= {column_length(payload, k)}"
              for k in key
              if next(c for c in payload if c.name == k).sql_type.startswith("NVARCHAR")]
    for i, guard in enumerate(guards):
        w(guard + (";" if i == len(guards) - 1 else ""))
    w("")
    w("                SET @FailedStatusRows = @@ROWCOUNT;")
    w("")
    w("                -- '+' rather than CONCAT on the left, on purpose: '+' propagates NULL, so a")
    w("                -- failure that happened before @ContextMessage was set yields no stray comma.")
    w("                SET @ContextMessage = CONCAT (COALESCE (@ContextMessage + N', ', N'')")
    w("                                            , N'statusFailed=', @FailedStatusRows);")
    w("            -- No semicolon: a terminated IF cannot take an ELSE.")
    w("            END")
    w("            ELSE")
    w("            BEGIN")
    w("                SET @ContextMessage = CONCAT (COALESCE (@ContextMessage + N', ', N'')")
    w("                                            , N'statusFailed=n/a (no LoadRunId)');")
    w("            END;")
    w("        END TRY")
    w("        BEGIN CATCH")
    w("            -- Swallowed by design; see above. Recorded, though, because a lost flush is the one")
    w("            -- way AR5 can under-report a failure without anything saying so.")
    w("            SET @ContextMessage = CONCAT (COALESCE (@ContextMessage + N', ', N'')")
    w("                                        , N'statusFlushFailed=1');")
    w("        END CATCH;")
    w("")
    w("        -- Swallows everything by design, so this call cannot mask the error below it. Runs AFTER")
    w("        -- the status flush so that the flush's own result reaches ContextMessage.")
    w("        EXEC logs.uspRecordExecutionError")
    w("              @ProcedureName   = @ProcName")
    w("            , @KeyParameters   = @KeyParameters")
    w("            , @ExecutionLogId  = @ExecutionId")
    w("            , @ErrorMessage    = @ErrorMsg")
    w("            , @ErrorProcedure  = @ErrorProc")
    w("            , @ErrorNumber     = @ErrorNumber")
    w("            , @ErrorLine       = @ErrorLine")
    w("            , @DynamicSql      = @DynamicSql")
    w("            , @ContextMessage  = @ContextMessage;")
    w("")
    w("        -- Bare, so the ORIGINAL error number reaches the caller. RAISERROR would make it 50000 and")
    w("        -- the client could no longer tell a deadlock from a constraint violation. The leading")
    w("        -- semicolon is required: a bare THROW immediately after BEGIN is a syntax error.")
    w("        ;THROW;")
    w("")
    w("    END CATCH;")
    w("")
    w("    RETURN 0;")
    w("END;")
    w("GO")
    w("")
    w("EXEC util.uspSetObjectDescription")
    w(f"      @SchemaName  = N'{SCHEMA}'")
    w("    , @ObjectType  = N'PROCEDURE'")
    w(f"    , @ObjectName  = N'{MERGE_PROCEDURE}'")
    w(f"    , @Description = N'{literal(f'Merges a batch of handler source records into {SCHEMA}.{PARENT_TABLE} and {SCHEMA}.{RAW_JSON_TABLE} from one JSON payload, in one transaction. Set-based; there is no singleton variant. Returns one row per record inserted or updated -- a record the caller sent that is absent from the result set was already current and was not touched.')}';")
    w("GO")
    w("")
    w("-- Each procedure script carries its own grant, which is what makes the report at the end of")
    w("-- script 050 the authoritative answer to what the applications can do. The monitoring app never")
    w("-- writes handler data, so it is not granted here.")
    w("IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL")
    w("BEGIN")
    w(f"    GRANT EXECUTE ON {SCHEMA}.{MERGE_PROCEDURE} TO RCRAInfoLoaderRole;")
    w("END;")
    w("GO")
    w("")
    w(f"PRINT N'400: {SCHEMA}.{MERGE_PROCEDURE} created or altered, {len(payload)} column(s) mapped, "
      f"EXECUTE granted to the loader role.';")
    w("GO")
    w("")

    return "\n".join(out)


def column_json_source(payload: list[Column], name: str) -> str:
    return next(c for c in payload if c.name == name).json_path


def column_length(payload: list[Column], name: str) -> int:
    sql = next(c for c in payload if c.name == name).sql_type
    match = re.match(r"NVARCHAR \((\d+)\)\Z", sql)
    if not match:
        raise SystemExit(
            f"FAIL  generate_schema: natural-key column {name} is {sql}, not a bounded NVARCHAR, so "
            f"the merge procedure cannot emit a truncation guard for it.")
    return int(match.group(1))


MERGE_PROCEDURE_NOTES = [
    "IDEMPOTENT, AND THAT IS LOAD-BEARING. Running this twice with the same payload inserts on the "
    "first call and changes nothing on the second: every column is compared with IS DISTINCT FROM "
    "and a row that matches is not updated at all. The AR8 completion UPDATE sits after the COMMIT, "
    "so a failure there reports a committed batch as failed and the caller may retry -- which is only "
    "survivable because the retry is a no-op.",

    "SCOPE. The parent table, its RawJson row, and all 18 repeating child collections -- three of "
    "which are grandchildren, nested inside another collection's elements. DA1 deliberately shipped "
    "the parent alone first, to settle the set mechanism and the audit stamping on one statement "
    "before the same mapping was replicated nineteen times; the children were added once the retire "
    "rule below had been decided. dbo.HandlerSourceOtherIdentifier is NOT here and never will be: it "
    "comes from a different endpoint and is not keyed to a handler VERSION.",

    "RETIRING A CHILD ROW IS GATED, AND THE GATE IS THE WHOLE DESIGN. A child element has no key of "
    "EPA's own, so its only natural key is (parent surrogate key, OrdinalPosition) -- which means a "
    "payload that sends three owners where four are stored is asserting that the fourth is gone, and "
    "WHEN NOT MATCHED BY SOURCE is how that is applied. The hazard is that a CROSS APPLY OPENJSON "
    "cannot distinguish three cases that must not be treated alike: an ABSENT property, an EMPTY "
    "array, and a property EPA has RENAMED all shred to zero rows, with no error and no row-count "
    "anomaly. Ungated, the first EPA release that renames a property would soft-delete every child "
    "row of every handler in every batch, silently.",

    "SO THE GATE IS JSON_PATH_EXISTS, ONCE PER COLLECTION PER ELEMENT, RECORDED IN @Collection BEFORE "
    "ANY CHILD MERGE RUNS. It returns 1 for both [] and a populated array and 0 only when the "
    "property is absent, which is exactly the distinction the shred loses. An empty or shorter array "
    "is a real removal and retires; an ABSENT property retires nothing, because absent is not "
    "evidence of none. MDE's decision, 2026-09-05. The cost is one JSON_PATH_EXISTS per collection "
    "per element and it is not negotiable down: the alternative is a silent mass delete.",

    "AN ABSENT COLLECTION IS RECORDED, BUT ONLY WHEN IT IS DIAGNOSTIC. logs.DataQualityObservation "
    "gets one row per collection per batch, and only when EPA sent the property for NO element in the "
    "batch AND this database holds live rows for those same parents. That pair is the rename "
    "signature. Absent with nothing stored is unremarkable -- most Maryland handlers have no episodic "
    "wastes and never will -- and recording it would put roughly fifteen rows per handler per night "
    "into a table whose entire value is that a row in it means something. The count reaches "
    "logs.ExecutionLog.Comments either way, as absentCollections=N, spelt out even when it is zero.",

    "childRetired IN Comments IS AN ALARM, NOT A STATISTIC. $action reports 'UPDATE' for a revive and "
    "a retire alike, so the retire count is taken from inserted.IsDeleted instead. A retire count "
    "that jumps after an EPA release is the signature of a genuine mass removal; combined with an "
    "absentCollections observation it is the signature of a rename. For that number to be readable it "
    "has to return to zero when nothing is happening, which is why WHEN NOT MATCHED BY SOURCE also "
    "tests tgt.IsDeleted = 0 -- an already-retired row is unmatched on every later run too, and "
    "without that test every run would re-stamp auditDeletedDateUtc on rows retired weeks ago.",

    "A CHILD MERGE'S TARGET IS A CTE, AND THAT IS NOT COSMETIC. WHEN NOT MATCHED BY SOURCE over the "
    "bare table would make the engine consider every row of a table that grows without bound; the "
    "EXISTS predicate would keep it correct and would not save the plan. Each MERGE therefore targets "
    "a CTE restricted to this batch's parents, which seeks the child's foreign-key index. A CTE over "
    "a single base table with a WHERE clause is insertable, updatable, and accepts OUTPUT -- all three "
    "were measured against this engine before nineteen statements were generated against them.",

    "A GRANDCHILD FINDS ITS PARENT BY JOINING THE TABLE THAT WAS JUST MERGED, NOT BY MERGE ... OUTPUT. "
    "OUTPUT cannot serve as the id map: a matched-but-unchanged row produces no OUTPUT row at all, so "
    "on the second run of an unchanged payload the map would be empty and every grandchild row would "
    "be orphaned or retired. Joining the already-merged child on its unique (ParentId, "
    "OrdinalPosition) index always finds it, and costs a seek. This is why CHILD_ORDER must place a "
    "parent before its grandchild, and why generate_schema.py refuses to emit if it does not.",

    "REVIVAL IS DONE HERE, AND SCRIPT 522 IS RIGHT TO INSIST ON IT. Every child and grandchild MERGE "
    "matches on (parent, OrdinalPosition) WITHOUT filtering IsDeleted, exactly as the parent does, so "
    "a soft-deleted row whose ordinal EPA is sending again is REVIVED rather than duplicated. That "
    "discharges the obligation dbo.uspSoftDeleteHandlerSourceSet records in its own header and cannot "
    "meet from the other side. The unfiltered match is also what makes a duplicate impossible: "
    "because a deleted row is matched and revived rather than re-inserted, two rows can never come to "
    "share one (parent, ordinal).",

    "G35 IS ANSWERED, AND THE ANSWER IS A TABLE VARIABLE. AR5's logs.HandlerLoadStatus rows are "
    "written by a procedure whose transaction may be doomed, and a rollback would take them with it -- "
    "so the failed batch would report nothing about WHICH handlers it failed, which is the whole reason "
    "AR5 exists. MDE's decision was to accumulate the per-handler set in a table variable, which a "
    "rollback does not unwind, and flush it from the CATCH block. No new accumulator was needed: @Key "
    "already holds every element's natural key and is populated before BEGIN TRANSACTION, so the flush "
    "also covers a batch rejected in validation, which never opened a transaction at all. The success "
    "half is written INSIDE the transaction instead, because 'handler X was inserted' is only true if "
    "it committed. Rows are UPDATEd, never INSERTed -- they exist as 'Pending' from the moment the run "
    "enumerated its work -- and the failure flush deliberately does not carry an Outcome, because "
    "nothing was applied. logs.HandlerLoadStatus.ExecutionLogId, added additively for this, is how a "
    "Failed row reaches the error text.",

    "WHY THE CHANGE TEST IS COLUMN-BY-COLUMN. IS DISTINCT FROM compares NULLs as equal, which a "
    "plain <> does not: with 200-odd nullable columns, a <> test reports 'no change' for every row "
    "where any compared value is NULL. The alternative -- comparing a hash of the payload -- was "
    "rejected because it tests the SERIALIZATION rather than the data. The day EPA reorders a "
    "property or changes its whitespace, every row in the batch would be reported as changed and "
    "auditModifiedDateUtc would move across the whole table for a change that did not happen. "
    "CLAUDE.md's 'converge, do not re-apply' rule is precisely this.",

    "THE MATCH IS UNFILTERED, AND MUST BE. The ON clause does not test IsDeleted. "
    "UX_dbo_HandlerSource_Natural excludes soft-deleted rows, so matching on it would report a "
    "soft-deleted handler as NOT MATCHED and insert a second row with the same natural key -- two "
    "rows, one live and one deleted, that the filtered unique index cannot reject. Matching "
    "unfiltered and setting IsDeleted = 0 on the matched arm is what keeps the natural key unique "
    "across all of history, and IX_dbo_HandlerSource_Natural_All exists to serve exactly this seek.",

    "HOLDLOCK IS NOT OPTIONAL ON EITHER MERGE. Without it, MERGE takes its read locks and releases "
    "them before the write, so two batches carrying the same handler can both evaluate NOT MATCHED "
    "and both insert. The natural-key index would then reject one of them with a unique-violation on "
    "an unattended overnight load. Both joins are seekable, so the range locks HOLDLOCK takes are on "
    "the keys in the batch and not on the table.",

    "WHAT @KeyParameters MAY CONTAIN. Identifiers and counts. @Payload is excluded BY NAME -- logging "
    "it would copy thousands of regulated-entity records into logs.ExecutionLog, which has a "
    "different read audience and a different retention policy from the tables the payload lands in. "
    "The same restriction applies to @Comments, @ContextMessage and @DynamicSql. From MDE's own "
    "template: do NOT include parameters such as passwords and Personally Identifiable Information.",

    "VALIDATION IS BEFORE BEGIN TRANSACTION. All four checks -- valid JSON, a complete natural key, a "
    "key that fits its column, and no duplicate key within the batch -- run inside the TRY block and "
    "before the transaction opens, so a rejected payload is recorded as a failed execution like any "
    "other with no transaction to unwind. The duplicate check in particular replaces error 8672 "
    "raised from inside the MERGE with a message that names the offending key.",

    "A RENAMED OR MIS-CASED PROPERTY SHREDS TO NULL SILENTLY. OPENJSON matches property names "
    "case-sensitively regardless of database collation, and a path that matches nothing is not an "
    "error. Generating this mapping and the CREATE TABLE from the same spec removes the drift between "
    "them; it does not remove the drift between this mapping and EPA. G28's round-trip tests are what "
    "compensate, and the four natural-key columns are checked for NULL above because those are the "
    "ones whose silence would corrupt rather than merely lose.",

    "@ProcName FALLS BACK TO A LITERAL, AND THE LITERAL IS THE ONE THE LOADER ACTUALLY USES. "
    "OBJECT_NAME (@@PROCID) and OBJECT_SCHEMA_NAME (@@PROCID) return NULL for a principal denied "
    "metadata visibility, and script 050 denies exactly that to both application logins. Measured: "
    "RCRAInfoMonitor holds EXECUTE = 1 on a procedure and still reads NULL from OBJECT_ID on it, "
    "because permission to run an object is not permission to see its name. So the COALESCE is not a "
    "defensive nicety for ad-hoc batches -- it is the branch every single loader call takes, and with "
    "a placeholder there every row the console app wrote would carry no procedure name, which is the "
    "one column the monitoring web app groups by. The literal is emitted from the same constant as "
    "the CREATE OR ALTER PROCEDURE name, so a rename moves both. The dynamic half is kept because it "
    "still resolves for the developer.",

    "THE COMPLETION UPDATE IS AFTER THE COMMIT, AND THAT HAS A CONSEQUENCE. If the COMMIT succeeds "
    "and the completion UPDATE then fails, control reaches the CATCH block with XACT_STATE () = 0, "
    "the error is recorded against the existing row, and a committed batch is reported as failed. "
    "Survivable only because of the first note.",
]


def blank_comments(sql: str) -> str:
    """Replaces block and line comment bodies with spaces, preserving length and line breaks.

    An apostrophe in a comment is legal, so the quote-balance scan below has to ignore them --
    otherwise it fires on every 'loader's' in the prose and reports nothing useful.
    """
    out, i, n = list(sql), 0, len(sql)
    while i < n:
        # A string literal is skipped WHOLE and before either comment form is considered. Several
        # descriptions contain a literal '--' as prose punctuation, and treating that as the start of
        # a line comment blanks the closing quote and manufactures the error this function exists to
        # detect. The project's own validate-sql.py scans in this same order for the same reason.
        if sql[i] == "'":
            j = i + 1
            while j < n:
                if sql[j] == "'" and sql[j:j + 2] != "''":
                    break
                j += 2 if sql[j:j + 2] == "''" else 1
            i = min(j + 1, n)
            continue

        if sql[i:i + 2] == "/*":
            end = sql.find("*/", i + 2)
            end = n if end == -1 else end + 2
        elif sql[i:i + 2] == "--":
            end = sql.find("\n", i)
            end = n if end == -1 else end
        else:
            i += 1
            continue

        for k in range(i, end):
            if out[k] != "\n":
                out[k] = " "
        i = end
    return "".join(out)


def assert_emittable(name: str, text: str) -> None:
    """Refuses to write a script that would not parse or would not deploy.

    The first check exists because it already caught a real defect: descriptions this generator
    writes itself contain apostrophes -- "handler's submitted source record" -- and only EPA's
    harvested prose was being escaped. Every literal is checked here rather than at each of the
    dozen places one is built, so adding a new hand-written description cannot reintroduce it.

    The second reflects Deploy-Database.ps1, which throws on any byte above 127. Finding out at
    deployment time which of 46 scripts carries a stray en dash is a bad afternoon.
    """
    problems: list[str] = []

    for number, line in enumerate(blank_comments(text).splitlines(), 1):
        # Quotes must balance on every emitted line: nothing here spans a literal across lines.
        if line.replace("''", "").count("'") % 2:
            problems.append(f"line {number}: odd number of single quotes, so a literal is left open "
                            f"(double the apostrophe): {line.strip()[:110]}")

    for number, line in enumerate(text.splitlines(), 1):
        # @Description is always the LAST argument, so its literal must close at end of line. Checked
        # against the RAW line, not the comment-blanked one: this is the precise test, and an
        # apostrophe inside the prose ends the literal early and turns the remainder into syntax.
        marker = "@Description = N'"
        if marker in line:
            body = line.split(marker, 1)[1]
            if not body.endswith("';"):
                problems.append(f"line {number}: @Description literal does not close the statement; "
                                f"an apostrophe in the prose is probably undoubled")
            elif "'" in body[:-2].replace("''", ""):
                problems.append(f"line {number}: unescaped single quote inside @Description: "
                                f"{line.strip()[:110]}")

    # A T-SQL block comment NESTS. A '/*' anywhere inside one -- and '/lookup/hd/*' in a header note
    # is exactly that -- opens a second comment, so the header's own '*/' closes only the inner one
    # and the whole script becomes an unterminated comment. This caught it in all 24 lookup scripts.
    depth, i, n = 0, 0, len(text)
    while i < n:
        # A quote only opens a literal OUTSIDE a comment. Inside one it is just an apostrophe, and
        # treating "handler's" in the header as a literal skips straight past the header's own
        # terminator -- which is how this check first reported every file as unterminated.
        if depth == 0 and text[i] == "'":
            j = i + 1
            while j < n:
                if text[j] == "'" and text[j:j + 2] != "''":
                    break
                j += 2 if text[j:j + 2] == "''" else 1
            i = min(j + 1, n)
        elif text[i:i + 2] == "/*":
            depth += 1
            if depth > 1:
                problems.append(
                    f"line {text.count(chr(10), 0, i) + 1}: a nested block-comment open. T-SQL "
                    f"comments nest, so this swallows the header's terminator. Rewrite the prose "
                    f"without the two characters: {text[max(0, i - 60):i + 20].splitlines()[-1]}")
            i += 2
        elif text[i:i + 2] == "*/":
            depth = max(0, depth - 1)
            i += 2
        elif depth == 0 and text[i:i + 2] == "--":
            end = text.find("\n", i)
            i = n if end == -1 else end
        else:
            i += 1

    if depth:
        problems.append(f"an unterminated block comment: {depth} unclosed open(s)")

    for number, line in enumerate(text.splitlines(), 1):
        bad = [c for c in line if ord(c) > 127]
        if bad:
            problems.append(f"line {number}: non-ASCII character(s) {bad!r}; "
                            f"Deploy-Database.ps1 rejects the file")

    if problems:
        print(f"FAIL  generate_schema: {name} would not deploy.")
        for problem in problems[:20]:
            print(f"       {problem}")
        if len(problems) > 20:
            print(f"       ... and {len(problems) - 20} more")
        raise SystemExit(1)


def generate() -> dict[str, str]:
    module, spec = load_spec()
    descriptions = leaf_descriptions(spec)

    parent = build_parent(spec, module, descriptions)
    raw = build_raw_json()
    children = build_children(spec, module, descriptions)
    other_ids = build_other_ids(spec)
    lookups = build_lookups(spec)

    files: dict[str, str] = {}

    files[f"100_{SCHEMA}.{parent.name}.sql"] = emit_table(
        parent, ordinal=100,
        source_note="Generated from the 19 scalar top-level properties of EPA's HandlerSource "
                    "definition plus every leaf of its single-cardinality nested objects, flattened "
                    "into prefixed column names. The three state addenda are omitted; the 18 "
                    "repeating collections become their own tables (scripts 130 and up).")
    files[f"110_{SCHEMA}.{raw.name}.sql"] = emit_table(
        raw, ordinal=110,
        source_note="Not derived from the spec's field list: this table holds the payload itself.")

    history_sql, current_sql = emit_views(parent)
    files[f"120_{SCHEMA}.vwHandlerSourceHistory.sql"] = history_sql
    files[f"121_{SCHEMA}.vwHandlerSource.sql"] = current_sql

    for i, table in enumerate(children):
        ordinal = 130 + i
        files[f"{ordinal}_{SCHEMA}.{table.name}.sql"] = emit_table(
            table, ordinal=ordinal,
            source_note="Generated from one repeating collection in EPA's HandlerSource payload.")

    files[f"150_{SCHEMA}.{other_ids.name}.sql"] = emit_table(
        other_ids, ordinal=150,
        source_note="Generated from EPA's HandlerOtherId definition -- the response shape of "
                    "/api/v1/hd/other-ids, a separate endpoint rather than a subtree of "
                    "HandlerSource, and the only table here not keyed to a HandlerSource version. "
                    "SameFacility carries no CHECK constraint even though EPA declares the pattern "
                    "Y|N|U: nothing in this mirror constrains a value EPA may extend, because a "
                    "rejected value fails an unattended overnight load instead of being reported.")

    files[f"400_{SCHEMA}.{MERGE_PROCEDURE}.sql"] = emit_merge_procedure(parent, children)

    for i, table in enumerate(lookups):
        ordinal = 200 + i
        files[f"{ordinal}_{SCHEMA}.{table.name}.sql"] = emit_table(
            table, ordinal=ordinal,
            source_note="Generated from one lookup/hd response definition. Mirrored per G15. (Written without the glob character on purpose: a T-SQL block comment nests, so a slash-star inside this header would swallow its own terminator.)")

    for name, body in files.items():
        assert_emittable(name, body)

    return files


def main(argv: list[str]) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

    parser = argparse.ArgumentParser(description="Generates the mirrored-schema DDL.",
                                     epilog=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true",
                        help="fail if the scripts on disk differ from what would be generated")
    parser.add_argument("--list", action="store_true", help="list what would be written")
    args = parser.parse_args(argv)

    files = generate()

    if args.list:
        for name, body in files.items():
            print(f"  {name:<58} {len(body.splitlines()):>5} lines")
        print(f"\n{len(files)} file(s).")
        return 0

    owned = {p.name for p in SCRIPTS.glob("*.sql") if p.name[0] in OWNED_PREFIXES}

    if args.check:
        problems: list[str] = []
        for name, body in files.items():
            path = SCRIPTS / name
            if not path.is_file():
                problems.append(f"{name}: missing; run python build/generate_schema.py")
                continue
            on_disk = path.read_text(encoding="utf-8")
            if on_disk != body:
                diff = list(difflib.unified_diff(
                    on_disk.splitlines(), body.splitlines(),
                    "on disk", "generated", lineterm="", n=1))
                problems.append(f"{name}: differs from the generator ({len(diff)} diff line(s)); "
                                f"first change: {next((d for d in diff[3:] if d[:1] in '+-'), '?')}")
        for extra in sorted(owned - set(files)):
            problems.append(f"{extra}: in Scripts/ but the generator does not produce it. Either it "
                            f"is stale, or it should not be numbered {extra[0]}xx.")

        if problems:
            print(f"FAIL  generate_schema: {len(problems)} problem(s).")
            for p in problems:
                print(f"       {p}")
            print("       The scripts are generated from spec/rcrainfo/swagger.json. Change the")
            print("       generator, not the output, then re-run it and commit both.")
            return 1

        print(f"PASS  generate_schema: {len(files)} script(s) match the generator.")
        return 0

    written = 0
    for name, body in files.items():
        path = SCRIPTS / name
        # Converge, do not re-apply: rewriting an identical file churns the working tree and the
        # file timestamps for no reason.
        if path.is_file() and path.read_text(encoding="utf-8") == body:
            continue
        path.write_text(body, encoding="utf-8", newline="\n")
        written += 1

    print(f"generate_schema: {len(files)} script(s), {written} written, "
          f"{len(files) - written} already current.")
    stale = sorted(owned - set(files))
    if stale:
        print(f"  NOTE {len(stale)} file(s) in Scripts/ are no longer generated and were NOT "
              f"removed -- nothing in this project deletes: {', '.join(stale)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
