#!/usr/bin/env python
"""Scratch: splice the catalog-emitted property declarations into the result-type files.

Run once. The committed artifact is the .cs files plus build/check_result_shapes.py, which is what
keeps them honest -- this script needs a database and generate_schema.py's spec-driven generators do
not, so this is not one of them.
"""

from pathlib import Path

REPO = Path(__file__).resolve().parent.parent.parent
TMP = REPO / "build" / "tmp"
OUT = REPO / "src" / "RCRAInfo.Data" / "Results"

blocks: dict[str, list[str]] = {}
current = None
for line in (TMP / "da5_all_props.txt").read_text(encoding="utf-8").splitlines():
    if line.startswith("@@@ "):
        current = line[4:].strip()
        blocks[current] = []
    elif current is not None and line.strip():
        blocks[current].append(line)

blocks["dbo.uspGetHandlerSourceDetail"] = [
    line for line in (TMP / "da5_detail_props.txt").read_text(encoding="utf-8").splitlines()
    if line.strip()
]

HEADER = """using Microsoft.EntityFrameworkCore;

namespace RCRAInfo.Data.Results;

/// <summary>
{summary}
/// </summary>
/// <remarks>
/// <para>
/// A keyless result type: it exists to receive one row shape and has no identity, no navigation and
{remark}
/// </para>
/// <para>
/// The property list mirrors the procedure's projection exactly -- name, order and type. It was
/// emitted from <c>sys.dm_exec_describe_first_result_set</c> rather than typed, and
/// <c>build/check_result_shapes.py</c> re-derives it from the deployed procedure on every guardrail
/// run: the projection and this type drift in one direction and drift silently, because a column
/// this type does not name is simply not materialised and nothing fails.
/// </para>
/// <para>
/// Nullability follows the engine's answer, not intent. Where the projection reports a column
/// nullable it is nullable here, even where the procedure cannot in fact produce a null, because
/// declaring a nullable column non-nullable is the direction that throws at runtime. The reverse is
/// always safe.{paging}
/// </para>
/// </remarks>
[Keyless]
public sealed class {name}
{{
{props}
}}
"""

PAGING_NOTE = (
    " That is also why <c>TotalRows</c> is <c>int?</c> here although two of the three paged shapes "
    "report it non-nullable -- one uniform declaration that cannot throw beats three that mirror an "
    "artifact of the optimizer.")

FILES = [
    ("HandlerSourceGridRow", "dbo.uspGetHandlerSourcePage",
     "/// One row of the handler grid, as projected by <c>dbo.uspGetHandlerSourcePage</c> (script 503).",
     "Contact columns are absent because the procedure does not project them: they are PII, and the "
     "grid is a list. <c>dbo.uspGetHandlerSourceDetail</c> is the only way to them."),

    ("HandlerSourceHistoryRow", "dbo.uspGetHandlerSourceHistoryPage",
     "/// One version of one handler, as projected by <c>dbo.uspGetHandlerSourceHistoryPage</c>\n"
     "/// (script 504).",
     "<c>CurrentRecord</c> is the distinguishing column: the history page shows every version of a "
     "handler, of which at most one is current. Contact columns are excluded as PII, and "
     "<c>SrcUpdatedBy</c> with them, because it identifies an EPA user."),

    ("HandlerSourceSearchRow", "dbo.uspSearchHandlerSource",
     "/// One search hit, as projected by <c>dbo.uspSearchHandlerSource</c> (script 506).",
     "<c>MatchRank</c> and <c>MatchField</c> are the two columns that make this shape different from "
     "the grid: the procedure offers no sort parameter at all, because its seven rank levels ARE the "
     "order, and <c>MatchField</c> names which column earned the hit. Contact columns are excluded "
     "from the projection AND from the predicate -- searching by surname would turn a handler search "
     "into a people search."),

    ("HandlerSourceDetail", "dbo.uspGetHandlerSourceDetail",
     "/// The whole of one handler version -- all 215 projected columns -- as returned by\n"
     "/// <c>dbo.uspGetHandlerSourceDetail</c> (script 505).",
     "This is the only shape in the project that carries PII: contact names, telephone numbers, "
     "email addresses and contact mailing addresses. Nothing read through it may be copied into a "
     "log message. Script 505's header puts it plainly -- a CATCH that said which handler's contact "
     "record failed to load would be writing a name into a table the monitoring web app can read."),

    ("LoadRunRow", "logs.uspGetLoadRunPage",
     "/// One load run, as projected by <c>logs.uspGetLoadRunPage</c> (script 500).",
     "<c>FailureMessage</c> is returned to a web page, so it must never carry the API key or any "
     "credential; script 500's header records that it is displayed."),

    ("HandlerLoadStatusRow", "logs.uspGetHandlerLoadStatusPage",
     "/// The per-source-record load status of one handler in one run, as projected by\n"
     "/// <c>logs.uspGetHandlerLoadStatusPage</c> (script 501).",
     "<c>PayloadSha256</c> is the digest computed by <see cref=\"PayloadJson.Sha256\"/>; the two "
     "must agree in case and in encoding or every comparison misses, which reads as \"this handler "
     "changed\" on every run."),

    ("LoadRunSummary", "logs.uspGetLoadRunSummary",
     "/// The single-row rollup of one load run, as returned by <c>logs.uspGetLoadRunSummary</c>\n"
     "/// (script 502).",
     "<c>CounterDriftDetected</c> and <c>ElapsedIsProvisional</c> are the two columns worth knowing "
     "about: the first reports that the run's own counters disagree with the status rows counted "
     "underneath them, and the second that the run has not finished, so the elapsed time is a "
     "reading rather than a total."),

    ("LoadWatermark", "config.uspGetLoadWatermark",
     "/// One feed's load watermark and the run parameters it recommends, as returned by\n"
     "/// <c>config.uspGetLoadWatermark</c> (script 512).",
     "The four <c>Recommended*</c> columns are the procedure's answer to \"what should the next run "
     "ask for\", derived from the watermark and the overlap. <c>Notes</c> is operator free text and "
     "is excluded from that procedure's log parameters by name."),

    ("MergeOutcomeRow", "dbo.uspMergeHandlerSourceBatch",
     "/// What the merge did to one element of a batch, as returned by\n"
     "/// <c>dbo.uspMergeHandlerSourceBatch</c> (script 400).",
     "This is how the loader learns per-record outcomes without asking again: the procedure reports "
     "them from inside the transaction that committed them, which is the only place they are "
     "knowable. <c>Outcome</c> is what the caller then feeds to "
     "<c>logs.uspUpsertHandlerLoadStatusSet</c>."),
]

def doc_wrap(text: str, lead: str) -> str:
    """Wrap prose into /// lines at 100 columns, keeping the first line's lead-in."""
    words = (lead + " " + text).split()
    lines, current = [], "///"
    for word in words:
        if len(current) + 1 + len(word) > 100:
            lines.append(current)
            current = "///"
        current += " " + word
    lines.append(current)
    return "\n".join(lines)


OUT.mkdir(parents=True, exist_ok=True)
for name, proc, summary, remark in FILES:
    props = blocks[proc]
    paging = PAGING_NOTE if any("TotalRows" in line for line in props) else ""
    text = HEADER.format(
        summary=summary,
        remark=doc_wrap(remark, "no change tracking."),
        paging=doc_wrap(paging, "").replace("///", "", 1).rstrip() if paging else "",
        name=name,
        props="\n".join(props))
    (OUT / f"{name}.cs").write_text(text.replace("\r\n", "\n"), encoding="utf-8", newline="\r\n")
    print(f"{name}.cs  {len(props)} properties  <- {proc}")
