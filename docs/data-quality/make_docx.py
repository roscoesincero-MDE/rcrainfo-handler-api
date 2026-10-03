"""
Builds docs/data-quality/RCRAInfo-Data-Quality-Observations-Explained.docx.

Run:  python docs/data-quality/make_docx.py
Requires: python-docx, and the PNGs produced by export-diagram-png.mjs.

Audience is MDE staff who monitor the nightly loads, not developers. The main
body assumes no technical background; the column-by-column reference lives in
the appendices. Regenerate rather than hand-edit, so the document and the
diagrams stay in step.

The page setup, palette and box/table helpers are shared with
docs/api-keys/make_docx.py so the two documents read as one set. They are
imported from that file rather than copied, so a change to the house style
lands in both.
"""

import importlib.util
import os

from docx import Document
from docx.enum.section import WD_ORIENT
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml.ns import qn
from docx.shared import Inches, Pt

HERE = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(HERE, "img")
OUTFILE = os.path.join(HERE, "RCRAInfo-Data-Quality-Observations-Explained.docx")

_spec = importlib.util.spec_from_file_location(
    "rcrainfo_docx_style",
    os.path.join(os.path.dirname(HERE), "api-keys", "make_docx.py"),
)
style = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(style)
style.IMG = IMG                      # figure() reads this at call time

INK, INK_SOFT = style.INK, style.INK_SOFT
BLUE, GREEN, AMBER, RED = style.BLUE, style.GREEN, style.AMBER, style.RED
FONT, TEXT_INDENT = style.FONT, style.TEXT_INDENT
body, rich, bullet, heading, subhead = (style.body, style.rich, style.bullet,
                                        style.heading, style.subhead)
callout, figure, table, spacer, run = (style.callout, style.figure, style.table,
                                       style.spacer, style.run)


def steps(doc, items, *, lead_color=AMBER, size=11.5, space_after=7):
    """A hanging-indent numbered list, matching the API-keys document."""
    for n, (head, rest) in enumerate(items, 1):
        p = doc.add_paragraph()
        pf = p.paragraph_format
        pf.left_indent = TEXT_INDENT + Inches(0.35)
        pf.first_line_indent = Inches(-0.35)
        pf.space_after = Pt(space_after)
        run(p, "%d.  " % n, size=size, bold=True, color=lead_color)
        run(p, head + ("  " if rest else ""), size=size, bold=True)
        if rest:
            run(p, rest, size=size, color=INK_SOFT)


def finding(doc, title, name, severity, explanation, todo):
    """One of Section 6's blocks, glued so Word never separates a finding's
    explanation from its "What to do" line."""
    subhead(doc, title, color=RED)                       # already keeps with next
    style.keep_with_next(
        body(doc, "Name on screen: %s   ·   Severity: %s" % (name, severity)))
    style.keep_with_next(body(doc, explanation))
    rich(doc, [("What to do:  ", True), (todo, False, INK_SOFT)])


def build():
    doc = Document()

    s = doc.sections[0]
    s.orientation = WD_ORIENT.LANDSCAPE
    s.page_width, s.page_height = Inches(11), Inches(8.5)
    s.left_margin = s.right_margin = Inches(0.8)
    s.top_margin = Inches(0.7)
    s.bottom_margin = Inches(0.6)

    normal = doc.styles["Normal"]
    normal.font.name = FONT
    normal.font.size = Pt(11)
    normal.element.rPr.rFonts.set(qn("w:eastAsia"), FONT)

    footer = s.footer.paragraphs[0]
    footer.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run(footer, "Data Quality Observations — Explained Simply   ·   "
                "MDE Phase 1   ·   September 8, 2026",
        size=9, color=INK_SOFT)

    # ---------------------------------------------------------- title page
    for _ in range(3):
        doc.add_paragraph().paragraph_format.space_after = Pt(0)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(6)
    run(p, "Data Quality Observations", size=38, bold=True, color=BLUE)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(18)
    run(p, "Explained Simply", size=26, color=AMBER)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(6)
    run(p, "What the nightly load writes down when EPA's data surprises it, "
           "how to read those notes, and which ones need a person.",
        size=13, color=INK_SOFT)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(30)
    run(p, "Prepared for MDE staff who monitor the loads   ·   Phase 1   ·   "
           "September 8, 2026   ·   Revision 1",
        size=11, color=INK_SOFT)

    callout(doc, "green", "The one sentence to take away.", [
        "An observation is the load telling you something it noticed. It is not "
        "an error report, and it does not mean anything was rejected — the data "
        "was stored anyway, and the note was written beside it.",
        "Sections 1 to 3 explain why the load works that way. Section 5 lists "
        "every kind of note it can write, and Section 6 covers the three that "
        "actually ask something of you.",
    ], width=Inches(8.0), indent=0.7)

    # -------------------------------------------------- the whole idea
    heading(doc, None, "The whole thing, in seven sentences", page_break=True)

    for i, (lead, rest) in enumerate([
        ("Every night the load asks EPA for handler records and stores them in our database. ",
         "One record is one version of one handler, exactly as EPA sent it."),
        ("Sometimes a record is odd, but not so odd that it cannot be stored. ",
         "EPA might name two versions of the same handler as current, or stop "
         "publishing a code we still hold."),
        ("When that happens the load stores the record and writes a note about what it noticed. ",
         "That note is an observation. It is one row in a table called "
         "logs.DataQualityObservation."),
        ("It writes the note instead of refusing the record, because refusing would lose real data. ",
         "A note can be read next week. A record we declined to store is simply gone."),
        ("Each note carries a severity — Info, Warning, or Error — which tells you how hard to look. ",
         "Even Error means the load carried on. It does not mean the load failed."),
        ("A note is never deleted, corrected, or tidied away. ",
         "It stays exactly as written, because it is evidence of what the data "
         "looked like on the night it arrived."),
        ("The monitoring website shows how many notes a run produced, by severity. ",
         "Reading those three numbers, in that order, is the whole of the "
         "routine job — Section 8 walks through it."),
    ], 1):
        p = doc.add_paragraph()
        pf = p.paragraph_format
        pf.left_indent = TEXT_INDENT + Inches(0.35)
        pf.first_line_indent = Inches(-0.35)
        pf.space_after = Pt(6)
        run(p, "%d.  " % i, size=11.5, bold=True, color=AMBER)
        run(p, lead, size=11.5, bold=True)
        run(p, rest, size=11.5, color=INK_SOFT)

    spacer(doc, 2)

    callout(doc, "blue", "The question this document exists to answer: "
                        "“Something says Error. Is the data broken?”", [
        "Almost certainly not, and the load almost certainly finished. Error, on "
        "an observation, means the load found something it cannot resolve on its "
        "own and a person should look at it. The record was still stored.",
        "A load that genuinely failed does not say so here at all. It says so on "
        "the run's own status, which is the subject of a different screen.",
    ])

    # ---------------------------------------------------------- Section 1
    heading(doc, "1", "Three things can happen to a record from EPA",
            page_break=True)
    body(doc, "Only three. Observations are about data we have, not data we lost.")
    figure(doc, "01-three-outcomes.png",
           "Figure 1 — The middle path is what this document is about: the "
           "record was stored, and a note was written beside it.",
           width=Inches(6.8))

    bullet(doc, "the ordinary night. Nothing to see, and nothing to do.",
           bold_lead="Stored, nothing noted — ")
    bullet(doc, "the record is there, and so is a note about it, counted on the "
                "run summary.",
           bold_lead="Stored, and noted — ")
    bullet(doc, "the only case where data is missing. It shows up as a failed row "
                "on the run's own status, not as an observation, and the load will "
                "try again.",
           bold_lead="Not stored at all — ")

    # ---------------------------------------------------------- Section 2
    heading(doc, "2", "Why the load writes it down instead of refusing the record",
            page_break=True)
    body(doc, "This is a deliberate design decision, not an oversight, and it is "
              "worth understanding because it explains why a healthy run can "
              "still produce hundreds of notes.")

    subhead(doc, "The reasoning, in three moves")
    steps(doc, [
        ("We are copying EPA's data, not judging it.",
         "If EPA publishes something strange, the strange thing is the fact. Our "
         "copy is supposed to look like theirs, including the parts we did not "
         "expect."),
        ("A record refused is a record lost.",
         "EPA's nightly feed sends updates, not the whole history again. A record "
         "we decline today may not come round again tomorrow."),
        ("A note costs almost nothing and can be read later.",
         "It carries the handler, the field, the value that surprised us, and a "
         "plain-English sentence about what to make of it — so somebody can judge "
         "it next week with the evidence still intact."),
    ])

    spacer(doc, 4)

    callout(doc, "amber", "The practical consequence", [
        "Our database is allowed to hold values EPA no longer publishes, and "
        "values that do not appear in any of EPA's code lists.",
        "That is not a defect. A handler form submitted in 2019 is still "
        "correctly described by the codes that were current in 2019. Throwing "
        "those away would make the older records unreadable.",
        "Observations are how we keep track of that without pretending it is "
        "tidy.",
    ])

    # ---------------------------------------------------------- Section 3
    heading(doc, "3", "The three severities, and what each one asks of you",
            page_break=True)
    body(doc, "Severity is the load's own estimate of how hard you should look. "
              "There are exactly three values and they never change.")

    table(doc,
          ["Severity", "What it means", "What it asks of you"],
          [["Info",
             "Something changed that somebody may want to find later.",
             "Nothing. Read it if you are curious."],
           ["Warning",
             "Something the load expected to be possible, and handled.",
             "Read the count. Look closer only if it is unusually large."],
           ["Error",
             "Something the load cannot resolve on its own.",
             "Look at it. Section 6 says what to do for each kind."]],
          widths=[1.1, 3.1, 2.7], accent="amber")

    callout(doc, "red", "Error here does not mean the load failed.", [
        "This is the one thing people get wrong. An observation with severity "
        "Error still describes a record that was stored successfully, in a run "
        "that carried on to the end.",
        "Whether a load failed is a separate question with a separate answer: "
        "the run's status, and the per-handler statuses beneath it. A run can "
        "finish perfectly while producing Errors, and a run can fail while "
        "producing none at all.",
    ])

    body(doc, "The wording is chosen so that severity means the same thing in "
              "every kind of note. A Warning about a missing code list and a "
              "Warning about a handler's current version are asking for the same "
              "amount of attention, even though they come from completely "
              "different parts of the load.")

    # ---------------------------------------------------------- Section 4
    heading(doc, "4", "Reading one observation", page_break=True)
    body(doc, "A single note answers six questions. You do not need to know the "
              "database to read one — every field is either a plain name or a "
              "plain sentence.")

    table(doc,
          ["The question", "The field that answers it", "Example"],
          [["Which night?", "LoadRunId", "the run you are already looking at"],
           ["What kind of thing was noticed?", "ObservationType",
            "CurrentRecordAmbiguousInSource"],
           ["How hard should I look?", "Severity", "Info, Warning, or Error"],
           ["Which handler?", "HandlerId", "MDD000000000 — blank if it is not "
            "about one handler"],
           ["What exactly surprised us?", "ObservedValue",
            "the value or count, kept short"],
           ["What am I supposed to make of it?", "Detail",
            "several sentences of plain English, written when the note was made"]],
          widths=[2.15, 1.95, 2.8], accent="blue")

    callout(doc, "green", "Detail is the part worth reading.", [
        "It is not a code and not a stack trace. It is a short piece of writing "
        "that says what was noticed, why the load decided to continue, what it "
        "did about it, and what would usually fix it.",
        "It was composed at the moment the note was written, by the part of the "
        "load that noticed — so it knows things that cannot be worked out "
        "afterwards.",
        "A few notes also name the table, column or code list involved. Those are "
        "for a developer, and are safe to skip.",
    ])

    # ---------------------------------------------------------- Section 5
    heading(doc, "5", "The eight things the load notices today", page_break=True)
    body(doc, "The complete list as the load stands today. The names are the exact "
              "text on screen; the plain meaning is beside them.")

    callout(doc, "amber", "A name you have not seen before is possible.", [
        "The list of names is not fixed in the database, deliberately: the load can "
        "add a new kind of note without a database change. So an unfamiliar name is "
        "not a fault — read its Detail, and tell the project team.",
    ])

    table(doc,
          ["Name you will see", "Severity", "What it means", "What to do"],
          [["CollectionAbsentButStored", "Warning",
            "EPA sent no value at all for a whole group of details.",
            "Nothing. Many at once may mean EPA renamed something."],
           ["CurrentRecordAmbiguousInSource", "Error",
            "EPA's own list named two versions of a handler as current.",
            "Section 6. The load picked one, and said which."],
           ["VersionNotInSourceSummary", "Warning",
            "A version we hold as current is no longer in EPA's list.",
            "Nothing, unless many handlers at once."],
           ["CurrentRecordAmbiguousInMirror", "Error",
            "After tidying, a handler still has two current versions.",
            "Section 6. This one always needs a person."],
           ["CurrentRecordMissingInMirror", "Warning",
            "A handler we hold has no current version at all.",
            "Nothing for one or two. Many is worth reporting."],
           ["LookupRefreshRetiredTooMany", "Error",
            "A code-list refresh would have retired most of the list.",
            "Section 6. Nothing changed — it is a safety catch."],
           ["LookupCodesRetired", "Info",
            "EPA stopped publishing codes we hold; marked retired.",
            "Nothing. A record that it happened."],
           ["StatusRowsStrandedByClosedRun", "Warning",
            "A run reported success leaving some handlers unfinished.",
            "Nothing now; the next run re-fetches them."]],
          widths=[2.3, 0.85, 1.9, 1.85], accent="gray", size=8.5)

    # ---------------------------------------------------------- Section 6
    heading(doc, "6", "The three that actually ask something of you",
            page_break=True)
    body(doc, "Five of the eight kinds are for the record. Three are worth acting "
              "on, and all three have a known first move.")

    finding(doc, "EPA named two current versions of the same handler",
            "CurrentRecordAmbiguousInSource", "Error",
            "At most one version of a handler can be the current one, and EPA's "
            "list named several. The load did not stop: it chose the version EPA "
            "received most recently, and the note says which one it chose and "
            "which ones competed. Our copy is therefore usable right now.",
            "ask for that one handler to be fetched and reconciled again. The "
            "contradiction is in EPA's data, so it usually clears on the next "
            "fetch. If it survives a re-fetch, it is a question for EPA.")

    finding(doc, "Our copy still has two current versions after tidying",
            "CurrentRecordAmbiguousInMirror", "Error",
            "This is the rarest note and the only one that always needs a person. "
            "The load's own tidying step cannot produce this outcome, so something "
            "else did — most likely two parts of the load working on the same "
            "handler at the same time. While it lasts, that handler appears twice "
            "on screens that should show it once.",
            "report it. Note the handler, and say that it is "
            "CurrentRecordAmbiguousInMirror rather than the other ambiguous one — "
            "the two look alike and need different investigation.")

    finding(doc, "A code-list refresh was refused for taking too much away",
            "LookupRefreshRetiredTooMany", "Error",
            "EPA publishes about two dozen code lists that we mirror. If a refresh "
            "would retire most of a list at once, the load treats that as a "
            "truncated answer from EPA rather than a real change, refuses the "
            "refresh, and leaves the list exactly as it was. Nothing was lost and "
            "nothing was changed.",
            "ask for that one list to be refreshed again later. A genuinely "
            "shrunken list needs a developer to allow it deliberately, which is "
            "the point of the catch.")

    spacer(doc, 4)

    callout(doc, "blue", "And one more that is worth watching rather than acting on", [
        "StatusRowsStrandedByClosedRun says a run claimed success while leaving "
        "some handlers unfinished. The unfinished ones were marked failed, so the "
        "next run collects them — nothing is lost.",
        "It is only a Warning because it repairs itself. But it means a run's "
        "report did not match what the run actually did, so if it appears on "
        "several nights in a row, that is worth raising.",
    ])

    # ---------------------------------------------------------- Section 7
    heading(doc, "7", "Where the notes come from, and who reads them",
            page_break=True)
    body(doc, "All of them land in one table, whichever part of the load wrote "
              "them. That is why one screen can show you everything a run "
              "noticed, without knowing which step noticed it.")
    figure(doc, "02-writers-and-readers.png",
           "Figure 2 — Several parts of the load write notes; they all go to one "
           "table; the monitoring website reads the counts from it.",
           width=Inches(6.2))

    body(doc, "Three groups of work write notes: storing the batch of records EPA "
              "sent, deciding which version of a handler is current, and the "
              "maintenance steps that refresh EPA's code lists and tidy up after a "
              "run. Between them they produce the eight kinds in Section 5.")

    callout(doc, "green", "Notes survive even when the work is undone.", [
        "If a step has to abandon what it was doing, its notes are still kept — "
        "with an extra sentence added to say that the change never happened.",
        "That may look odd, and it is on purpose. A note about what the load was "
        "about to do is evidence, and throwing it away would leave nobody able "
        "to explain the night afterwards.",
    ])

    # ---------------------------------------------------------- Section 8
    heading(doc, "8", "How to read a run, step by step", page_break=True)
    body(doc, "This is the routine. On most nights it takes under a minute and "
              "ends at step 4.")
    figure(doc, "03-triage.png",
           "Figure 3 — Errors first, then Warnings, then Info. The lower row is "
           "only entered when something needs a person.",
           width=Inches(6.0))

    steps(doc, [
        ("Open the run on the monitoring website.",
         "One run is one night's work."),
        ("Read the three counts: Errors, Warnings, Info.",
         "Reading them in that order stops you spending time on notes that ask "
         "nothing of you."),
        ("If there are Errors, look at those first.",
         "Section 6 covers all three kinds, and each has a known first move."),
        ("If there are only Warnings and Info, the run is accepted.",
         "Warnings are expected. A run with several hundred can be perfectly "
         "healthy."),
        ("Before escalating, ask how many different kinds there are.",
         "The run summary reports that too. One kind repeated ten thousand times "
         "is one problem; ten thousand kinds is a different conversation."),
        ("For a handler-specific note, ask for that handler to be reconciled.",
         "This is the usual fix, and it is safe to ask for at any time."),
        ("If it survives a reconcile, raise it.",
         "It has stopped being routine, and is now either a developer's question "
         "or EPA's."),
    ], space_after=5)

    # ---------------------------------------------------------- Section 9
    heading(doc, "9", "Things that look alarming and are not", page_break=True)

    table(doc,
          ["What you see", "Why it is fine"],
          [["Hundreds of Warnings on a successful run",
            "Warnings are the expected middle case. The load was designed to "
            "produce them rather than to hide them."],
           ["An Error on a run that finished successfully",
            "Both are true at once. Errors describe data the load could not "
            "resolve; they do not stop it."],
           ["The same note repeated many times",
            "Nothing removes duplicates, on purpose. Two identical notes mean it "
            "was noticed twice, which is itself worth knowing."],
           ["A note about a code EPA no longer publishes",
            "Old records still need old codes to describe them. The code is "
            "marked retired and still works."],
           ["A note that says its own change was undone",
            "Deliberate. The note is kept as evidence of what was about to "
            "happen, with a sentence added saying it did not."],
           ["A count of deleted notes that is not zero",
            "Notes are never truly removed. If one is marked deleted, the "
            "summary still counts it and says so separately."],
           ["A note naming a handler outside Maryland",
            "Contacts and mailing addresses are legitimately out of state. Only "
            "the handler's own location is restricted to Maryland."]],
          widths=[2.6, 4.3], accent="green")

    callout(doc, "amber", "Count the kinds, not the rows.", [
        "The most common mistake in reading a run is reacting to a large number. A "
        "single handler fetched repeatedly can produce a great many identical "
        "notes, and that is one small problem, not a large one.",
        "The number of distinct kinds — which the run summary also reports — is "
        "the figure that tells you whether something broad went wrong.",
    ])

    # --------------------------------------------------------- Section 10
    heading(doc, "10", "What is not built yet", page_break=True)
    body(doc, "This section is here so that nobody spends time looking for "
              "something that does not exist. None of these are faults; they are "
              "work not yet done.")

    for t, d in [
        ("There is no screen for individual notes.",
         "The monitoring website shows the counts for a run. Reading the notes "
         "themselves means asking someone to query the table — which is quick, "
         "but it is not self-service yet."),
        ("There is no way to acknowledge or close a note.",
         "The table has no notion of a note being dealt with. Tracking what has "
         "been looked at is, for now, a matter for whatever notes you keep "
         "yourself."),
        ("Four kinds of note are described but never written.",
         "The database's own documentation lists names for an unknown code, a "
         "value too long, an unexpected blank, and an unexpected property. "
         "Nothing in the load writes any of them today."),
        ("In particular, unknown codes are not yet reported.",
         "The load deliberately accepts a code that is in no EPA list — that is "
         "the decision described in Section 2 — but nothing yet writes a note "
         "when it happens. So the absence of such notes is not evidence that "
         "there are no unknown codes."),
    ]:
        bullet(doc, d, bold_lead=t + "  ")

    spacer(doc, 6)

    callout(doc, "gray", "What this means when you are asked “is the data clean?”", [
        "The honest answer is: the data is complete, and everything the load "
        "noticed about it is written down where it can be read.",
        "It is not an answer about every possible problem, because the load only "
        "reports the eight things in Section 5. Anything it does not look for, "
        "it does not report.",
    ])

    # -------------------------------------------------------- Appendix A
    heading(doc, "A", "Appendix — every field on a note", page_break=True,
            color=INK_SOFT)
    body(doc, "For a technical reader. This is the whole of "
              "logs.DataQualityObservation, in the order the fields appear.")

    table(doc,
          ["Field", "Holds"],
          [["DataQualityObservationId", "The note's own number. Assigned in order."],
           ["LoadRunId", "The run that produced it. Always present."],
           ["HandlerLoadStatusId", "The specific handler-version attempt, when "
            "there was one."],
           ["ObservationType", "The kind of note. Free text by design — no "
            "database constraint limits the values."],
           ["Severity", "Info, Warning, or Error. Constrained to those three; "
            "defaults to Warning."],
           ["HandlerId", "EPA's handler identifier, when the note is about one."],
           ["SourceType", "EPA's single-character source type, when relevant."],
           ["Sequence", "EPA's version number within that handler and source type."],
           ["TableName", "The table the note concerns, when it concerns one."],
           ["ColumnName", "The column, likewise."],
           ["JsonPath", "The position in EPA's payload the value came from."],
           ["LookupName", "The code list involved, for the code-list notes."],
           ["ObservedValue", "The value or count that was noticed. Kept short "
            "deliberately."],
           ["Detail", "The prose explanation. Up to 4,000 characters."],
           ["ObservedDateUtc", "When the load noticed it, in UTC."],
           ["IsDeleted", "Soft-delete flag. There is no hard delete anywhere in "
            "this database."],
           ["auditDeletedBy, auditDeletedDateUtc", "Who marked it deleted, and "
            "when."],
           ["auditCreatedBy, auditCreatedDateUtc", "Which login wrote the note, "
            "and when."],
           ["auditModifiedBy, auditModifiedDateUtc", "Who last changed the row, "
            "and when."]],
          widths=[2.5, 4.4], accent="gray", size=9)

    # -------------------------------------------------------- Appendix B
    heading(doc, "B", "Appendix — who writes the notes, and who reads them",
            page_break=True, color=INK_SOFT)

    subhead(doc, "The five procedures that write observations")
    table(doc,
          ["Procedure", "Kinds it writes"],
          [["dbo.uspMergeHandlerSourceBatch", "CollectionAbsentButStored"],
           ["dbo.uspReconcileCurrentRecord",
            "CurrentRecordAmbiguousInSource, VersionNotInSourceSummary, "
            "CurrentRecordAmbiguousInMirror, CurrentRecordMissingInMirror"],
           ["dbo.uspSoftDeleteHandlerSourceSet", "CurrentRecordMissingInMirror"],
           ["dbo.uspRefreshLookupSet",
            "LookupRefreshRetiredTooMany, LookupCodesRetired"],
           ["logs.uspSweepStrandedHandlerLoadStatus",
            "StatusRowsStrandedByClosedRun"]],
          widths=[3.0, 3.9], accent="gray", size=9)

    body(doc, "CurrentRecordMissingInMirror is written by two of them, for the same "
              "condition reached two ways — so a change to its meaning has to be "
              "made in both places at once.")

    subhead(doc, "How the monitoring website reads them")
    body(doc, "logs.uspGetLoadRunSummary returns one row per run, carrying six "
              "figures about observations: the total, how many are marked deleted, "
              "the counts of Error, Warning and Info, and the number of distinct "
              "kinds — the last being what separates one problem repeated from "
              "many. It deliberately does not filter out soft-deleted notes, the "
              "one read path in the database that does not, because a note marked "
              "deleted is still evidence and hiding it would make the totals lie.")

    bullet(doc, "Notes accumulate in memory during a step and are written even if "
                "that step has to abandon its work, with a sentence appended "
                "recording that the change did not happen.",
           bold_lead="They survive a rollback.  ")
    bullet(doc, "Detail is prose the load wrote about the data. It never contains "
                "the EPA API key or any other credential.",
           bold_lead="They carry no secrets.  ")
    bullet(doc, "the three figures in this document are also interactive, in "
                "docs/data-quality/diagrams. Open one in a browser to zoom, search "
                "it, or trace a single path.",
           bold_lead="The diagrams are live.  ")

    # -------------------------------------------------------- Appendix C
    heading(doc, "C", "Appendix — words you may hear", page_break=True,
            color=INK_SOFT)
    table(doc,
          ["Word", "What it actually means"],
          [["Observation", "One note the load wrote about something it noticed. "
            "The subject of this document."],
           ["Severity", "How hard to look: Info, Warning, or Error."],
           ["Run", "One night's work. Everything the load did between starting "
            "and stopping."],
           ["Handler", "EPA's word for a regulated business or site."],
           ["Version", "One copy of a handler's record as EPA received it. A "
            "handler has many, over the years."],
           ["Current version", "The one version EPA says describes the handler "
            "today. There should be exactly one."],
           ["Reconcile", "Fetch a handler's list of versions from EPA again and "
            "settle which one is current. The usual fix."],
           ["Code list (lookup)", "One of EPA's published lists of allowed "
            "codes. We mirror about two dozen of them."],
           ["Retired", "A code EPA no longer publishes. Kept, because older "
            "records still need it. Not the same as inactive."],
           ["Soft delete", "Marking a row deleted while keeping it. The only "
            "kind of delete this database has."],
           ["Stranded row", "A handler a run listed and never finished. Marked "
            "failed so the next run collects it."],
           ["Mirror", "Our copy of EPA's data. When a note says the mirror, it "
            "means our database, not EPA's."]],
          widths=[1.8, 5.1], accent="gray", size=9.5)

    callout(doc, "gray", "Where the exact detail lives", [
        "This document is deliberately simplified, and nothing in it is untrue. For "
        "the precise definitions, the reasoning behind each decision, and the full "
        "text the load writes, the source is the database scripts themselves and "
        "Phase1-Analysis.md.",
    ])

    style.tighten(doc)
    doc.save(OUTFILE)
    print("wrote", OUTFILE)


if __name__ == "__main__":
    build()
