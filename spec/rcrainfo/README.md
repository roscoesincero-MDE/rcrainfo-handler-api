# The pinned RCRAInfo API specification

`swagger.json` is EPA's own description of the RCRAInfo REST API. Every field count in
`Phase1-Analysis.md` and `Phase1-Plan.md`, and every column in the Workstream B DDL, is derived from
**this file** — not from the live endpoint.

```powershell
python build\measure_spec.py            # the measurements
python build\measure_spec.py --assert   # fail if the file or the measurements moved
python build\measure_spec.py --leaves   # every one of the 444 leaf fields, with type and width
python build\measure_spec.py --lookups  # the 24 lookup endpoints and their response shapes
```

## Provenance

| | |
|---|---|
| Source | `https://rcranodepreprod.epa.gov/rcra-api/rest/swagger.json` |
| Retrieved | 2026-09-04 |
| `info.version` | `1.0 (built on 09/01/2026 06:43:56 PM)` |
| SHA-256 | `cd8fffdcc29963e2813953bc8560d81b8ec2e4d63c03d853549535e585938aba` |
| Size | 246,025 bytes |
| Swagger | 2.0 — 91 paths, 142 definitions |

**No API key was needed to retrieve it.** The specification is public on both preprod and
production; only the data endpoints require credentials. That is why Workstream B does not wait on
the API key.

## Why it is pinned rather than fetched

`info.version` carries a build timestamp, and the document really does move — the preprod copy above
was built five days after the production copy retrieved the same afternoon. A schema generated from
a moving upstream file cannot be reproduced, and a column that quietly appears or disappears between
two runs of the generator is exactly the kind of defect that surfaces in UAT instead of here.

`build/measure_spec.py --assert` hashes this file and re-derives every count, so adopting a newer
spec is a deliberate act with a visible diff. See that script's docstring for the procedure.

## Preprod is the modelling source, and it differs from production

Both environments return the same 91 paths and the same 142 definitions, and 139 of those
definitions are byte-identical. Three things differ, and one of them changes the DDL:

| What | Production | Preprod (pinned) |
|---|---|---|
| `HandlerSourceNaics.primary` | `required` | not required; described as *"Required when the source type is N or A (HD2-610)"* |
| `/lookup/hd/relationships` `operationId` | `retrieveContactTypeCodes_1` | `retrieveContactTypeCodes` |
| `/lookup/hd/contact-types` `operationId` | `retrieveContactTypeCodes` | `retrieveContactTypeCodes_1` |

The `operationId` pair is a code-generator artifact — two operations share a name, so one gets
suffixed, and which one is arbitrary. No schema impact.

The `HandlerSourceNaics.primary` difference does matter: **production's flat `required` would have
justified a `NOT NULL` column, and preprod explains why that is wrong.** The primary NAICS code is
required *conditionally*, for source types N and A only, so the column is nullable and the rule
belongs in the loader as a data-quality observation (AR5). Preprod is pinned because it is the newer
document and the more informative one.

## Scope exclusions applied when counting

Named constants in `build/measure_spec.py`, not a filter buried in the walk.

| Subtree | Decision |
|---|---|
| `addendum.*` | **Omitted.** Three state addenda — `alabama` (3 leaf fields), `wisconsin` (30), `washington` (34) — and **no `maryland`**. G16 named only Washington; the other two are excluded on G16's own reasoning, since `activityLocation` is MD only (G2). 67 of the 444 leaf fields, and one of the 38 top-level properties. |
| `episodic.*` | **In scope.** G14 put the `/episodic-events` *endpoint* out of scope, which is a statement about the load schedule. This is a subtree of a payload already being fetched, so it costs nothing to keep and would be expensive to retrofit. 4 of the 18 child tables. |

If EPA adds `addendum.maryland`, `--assert` fails on both the hash and the leaf counts. That is the
point of the baseline: the day an exclusion stops being correct, something says so.

## What the measurements say

```
HandlerSource top-level properties               38
  ... of which scalar (dbo.HandlerSource columns) 19
leaf fields (total)                              444
  ... carrying an EPA description                187  (42%)
leaf fields in scope (columns to model)          377
  ... carrying an EPA description                187  (50%)
repeating collections (total)                     24
  ... in scope (child tables)                     18
nested objects (flattened into parent)            85
  ... in scope                                    74
/lookup/hd/* endpoints                            24
  ... distinct response definitions                23
in-scope string leaves with no maxLength          73
```

Two of those deserve comment.

**Description coverage is 50%, not 42%.** All 67 addendum leaf fields are undescribed, so excluding
them raises the share of in-scope fields that arrive with EPA prose. 187 of 377 **`HandlerSource` leaf
fields** can have their `MS_Description` harvested from the spec; the other 190 need one written by
hand.

Do not read that as a column count. As built, the mirror harvested **203** descriptions and wrote
**278** placeholders across **481** spec-derived columns — a larger denominator, because it also covers
the 24 lookup tables, EPA's nested code objects flattened into prefixed columns, and the 13 columns of
`other-ids`. Both figures are correct about different things; `measure_spec.py --assert` pins the
field-level one. A provenance query against the database returns **490** rather than 481, because nine
hand-authored `logs` columns record an EPA field and cite it too — the four `ApiError*` columns on
`HandlerLoadStatus` and `HandlerLoadAttempt`, and `DataQualityObservation.JsonPath`.

**23 definitions serve 24 endpoints** — `/lookup/hd/naics-codes` and `/lookup/hd/naics-other` both
return `Naics`. The mirror is **24 tables**, not 23: `StateDistrict` carries a nested `counties` array
that becomes `dbo.LookupStateDistrictCounty` in its own right. Definitions and tables differ by exactly
that one child, and conflating the two is how "23 tables" got recorded in the first place.

## The 73 unbounded string columns

The spec declares `maxLength` on most strings but not all, and a width has to be chosen for the rest.
Three quarters of them are one field repeated:

| Count | Field | Width, and where it comes from |
|---|---|---|
| 53 | `description` | the description beside a lookup code. **`NVARCHAR (255)`** — EPA's own `episodic.projects[].otherDescription` is 255 |
| 7 | `name` | **`NVARCHAR (80)`** — matches EPA's `handlerName`, which is 80 |
| 6 | free-text (`notes`, `publicNotes`, `comments`, `publicComments`, `shortTermGeneratorNotes`, `natureOfBusiness`) | **`NVARCHAR (4000)`** — EPA bounds its own long text at 4000 in three places, so this is their number, not an invented one |
| 5 | bare-string arrays of waste codes | **`NVARCHAR (6)`** — the `WasteCode` lookup declares `code` as `maxLength: 6` |
| 2 | `SrcCreatedBy`, `SrcUpdatedBy` | **`NVARCHAR (100)`** — already decided in `Phase1-Plan.md` §B0. These mirror EPA user IDs, not SQL Server logins, so the `NVARCHAR (128)` floor that `ORIGINAL_LOGIN()` forces on the `audit*By` columns does not apply |
| 1 | `permit.otherPermits[].number` | **`NVARCHAR (50)`** — no precedent in the spec; other identifier fields run 12–50 |

`NVARCHAR (MAX)` is deliberately not used for the free-text columns. EPA demonstrably caps long text
at 4000, and `MAX` cannot be indexed and pushes rows off-page.

**The loader must never truncate.** A value longer than its column is recorded as an AR5
data-quality observation and the row is rejected, because silently shortening a handler's public
comment is data loss that no one will notice. Widening a column later is a guarded
`ALTER TABLE ... ALTER COLUMN`, which stays convergent; recovering a truncated value needs a refetch
from EPA.
