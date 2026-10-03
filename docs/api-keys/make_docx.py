"""
Builds docs/api-keys/RCRAInfo-API-Keys-Explained.docx.

Run:  python docs/api-keys/make_docx.py
Requires: python-docx, and the PNGs produced by make_diagrams.py.

Audience is MDE management, not developers. Every sentence is meant to be
readable without any technical background. Regenerate rather than hand-edit,
so the document and the diagrams stay in step.
"""

import os
from docx import Document
from docx.enum.section import WD_ORIENT
from docx.enum.table import WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Emu, Inches, Pt, RGBColor

HERE = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(HERE, "img")
OUTFILE = os.path.join(HERE, "RCRAInfo-API-Keys-Explained.docx")

# Same palette as the diagrams, as hex, so the document matches the pictures.
INK = RGBColor(0x1C, 0x25, 0x33)
INK_SOFT = RGBColor(0x58, 0x65, 0x7A)
BLUE = RGBColor(0x1F, 0x4E, 0x79)
GREEN = RGBColor(0x1A, 0x6A, 0x42)
AMBER = RGBColor(0xA9, 0x63, 0x04)
RED = RGBColor(0xA3, 0x28, 0x20)

BLUE_L, GREEN_L, AMBER_L, RED_L, GRAY_L = "E9F0F8", "E5F3EB", "FDF3E0", "FCEAE8", "F4F6F9"
HEX = {"blue": ("1F4E79", BLUE_L), "green": ("1A6A42", GREEN_L),
       "amber": ("A96304", AMBER_L), "red": ("A32820", RED_L),
       "gray": ("96A1B2", GRAY_L)}

FONT = "Segoe UI"
MONO = "Consolas"

TEXT_INDENT = Inches(1.2)      # body copy sits inside the figures
TEXT_WIDTH = Inches(6.9)
FIG_WIDTH = Inches(8.8)        # ~200 dpi; small enough to leave room for a table


# --------------------------------------------------------------- low level

def _el(tag, **attrs):
    e = OxmlElement(tag)
    for k, v in attrs.items():
        e.set(qn("w:" + k), v)
    return e


def shade(cell, fill):
    cell._tc.get_or_add_tcPr().append(_el("w:shd", val="clear", color="auto", fill=fill))


def cell_borders(cell, **sides):
    """sides: top/left/bottom/right = (size_eighths_pt, hex) or None to hide."""
    tcPr = cell._tc.get_or_add_tcPr()
    borders = _el("w:tcBorders")
    for side in ("top", "left", "bottom", "right"):
        if side not in sides:
            continue
        spec = sides[side]
        if spec is None:
            borders.append(_el("w:" + side, val="nil"))
        else:
            size, color = spec
            borders.append(_el("w:" + side, val="single", sz=str(size),
                               space="0", color=color))
    tcPr.append(borders)


def cell_margins(cell, top=100, start=180, bottom=100, end=180):
    mar = _el("w:tcMar")
    for tag, v in (("top", top), ("start", start), ("bottom", bottom), ("end", end)):
        mar.append(_el("w:" + tag, w=str(v), type="dxa"))
    cell._tc.get_or_add_tcPr().append(mar)


def indent_table(table, inches):
    table._tbl.tblPr.append(_el("w:tblInd", w=str(int(inches * 1440)), type="dxa"))


def keep_with_next(par):
    par.paragraph_format.keep_with_next = True


def run(par, text, *, size=11, bold=False, italic=False, color=INK, font=FONT):
    r = par.add_run(text)
    r.font.name = font
    r.font.size = Pt(size)
    r.font.bold = bold
    r.font.italic = italic
    r.font.color.rgb = color
    # East-Asian font name, or Word may substitute for some glyphs
    r._element.rPr.rFonts.set(qn("w:eastAsia"), font)
    return r


# -------------------------------------------------------------- structure

def spacer(doc, pts=6):
    """A thin gap. A normal empty paragraph is 11pt tall and pushes boxes
    onto the next page for no visible reason."""
    p = doc.add_paragraph()
    p.paragraph_format.space_before = Pt(0)
    p.paragraph_format.space_after = Pt(pts)
    run(p, "", size=1)
    return p


def tighten(doc):
    """A box leaves a blank gap paragraph behind it. If the next thing is a
    page break, that gap can land alone on a page of its own — so remove it."""
    ps = doc.paragraphs
    for i, p in enumerate(ps[:-1]):
        if p.text.strip():
            continue
        nxt = ps[i + 1]._p
        if any(br.get(qn("w:type")) == "page"
               for br in nxt.findall(".//" + qn("w:br"))):
            p._p.getparent().remove(p._p)


def body(doc, text, *, size=11, bold=False, italic=False, color=INK,
         space_after=8, indent=True):
    p = doc.add_paragraph()
    pf = p.paragraph_format
    pf.space_after = Pt(space_after)
    pf.space_before = Pt(0)
    if indent:
        pf.left_indent = TEXT_INDENT
        pf.right_indent = Inches(11 - 0.8 - 0.8) - TEXT_INDENT - TEXT_WIDTH
    run(p, text, size=size, bold=bold, italic=italic, color=color)
    return p


def rich(doc, parts, *, size=11, space_after=8, indent=True):
    """parts: list of (text, bold) or (text, bold, color)."""
    p = doc.add_paragraph()
    pf = p.paragraph_format
    pf.space_after = Pt(space_after)
    pf.space_before = Pt(0)
    if indent:
        pf.left_indent = TEXT_INDENT
        pf.right_indent = Inches(11 - 1.6) - TEXT_INDENT - TEXT_WIDTH
    for part in parts:
        text, bold = part[0], part[1]
        color = part[2] if len(part) > 2 else INK
        run(p, text, size=size, bold=bold, color=color)
    return p


def bullet(doc, text, *, bold_lead=None, size=11, color=INK):
    p = doc.add_paragraph(style="List Bullet")
    pf = p.paragraph_format
    pf.left_indent = TEXT_INDENT + Inches(0.3)
    pf.space_after = Pt(5)
    pf.space_before = Pt(0)
    if bold_lead:
        run(p, bold_lead, size=size, bold=True, color=color)
    run(p, text, size=size, color=color)
    return p


def numbered(doc, text, *, bold_lead=None, size=11):
    p = doc.add_paragraph(style="List Number")
    pf = p.paragraph_format
    pf.left_indent = TEXT_INDENT + Inches(0.3)
    pf.space_after = Pt(5)
    pf.space_before = Pt(0)
    if bold_lead:
        run(p, bold_lead, size=size, bold=True)
    run(p, text, size=size)
    return p


def heading(doc, number, text, *, color=BLUE, size=20, page_break=False):
    p = doc.add_paragraph()
    pf = p.paragraph_format
    pf.space_before = Pt(0 if page_break else 22)
    pf.space_after = Pt(10)
    pf.keep_with_next = True
    if page_break:
        p.add_run().add_break(WD_BREAK.PAGE)
    if number:
        run(p, number + "   ", size=size, bold=True, color=RGBColor(0x96, 0xA1, 0xB2))
    run(p, text, size=size, bold=True, color=color)
    return p


def subhead(doc, text, *, color=INK):
    p = doc.add_paragraph()
    p.paragraph_format.space_before = Pt(12)
    p.paragraph_format.space_after = Pt(6)
    p.paragraph_format.left_indent = TEXT_INDENT
    p.paragraph_format.keep_with_next = True
    run(p, text, size=13, bold=True, color=color)
    return p


def callout(doc, kind, head, lines, *, width=TEXT_WIDTH, indent=1.2):
    """A shaded box with a thick accent bar on the left."""
    accent, wash = HEX[kind]
    t = doc.add_table(rows=1, cols=1)
    t.alignment = WD_TABLE_ALIGNMENT.LEFT
    t.autofit = False
    indent_table(t, indent)
    cell = t.cell(0, 0)
    cell.width = width
    t.columns[0].width = width
    shade(cell, wash)
    cell_borders(cell, left=(36, accent), top=(6, accent),
                 bottom=(6, accent), right=(6, accent))
    cell_margins(cell, top=140, bottom=140, start=200, end=200)
    t.rows[0]._tr.get_or_add_trPr().append(_el("w:cantSplit"))

    p = cell.paragraphs[0]
    p.paragraph_format.space_after = Pt(4 if lines else 0)
    color = {"blue": BLUE, "green": GREEN, "amber": AMBER,
             "red": RED, "gray": INK_SOFT}[kind]
    run(p, head, size=12, bold=True, color=color)
    for i, line in enumerate(lines):
        q = cell.add_paragraph()
        q.paragraph_format.space_after = Pt(4 if i < len(lines) - 1 else 0)
        run(q, line, size=11, color=INK)
    spacer(doc, 6)
    return t


def figure(doc, filename, caption, width=FIG_WIDTH):
    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_before = Pt(8)
    p.paragraph_format.space_after = Pt(4)
    p.paragraph_format.keep_with_next = True
    p.add_run().add_picture(os.path.join(IMG, filename), width=width)
    c = doc.add_paragraph()
    c.alignment = WD_ALIGN_PARAGRAPH.CENTER
    c.paragraph_format.space_after = Pt(14)
    run(c, caption, size=9.5, italic=True, color=INK_SOFT)


def table(doc, headers, rows, *, widths=None, accent="blue", width=TEXT_WIDTH,
          indent=1.2, size=10.5):
    accent_hex, wash = HEX[accent]
    t = doc.add_table(rows=1, cols=len(headers))
    t.alignment = WD_TABLE_ALIGNMENT.LEFT
    t.autofit = False
    indent_table(t, indent)

    if widths is None:
        widths = [1] * len(headers)
    total = sum(widths)
    widths = [Emu(int(width * w / total)) for w in widths]

    hdr = t.rows[0]
    for i, h in enumerate(headers):
        cell = hdr.cells[i]
        cell.width = widths[i]
        shade(cell, wash)
        cell_borders(cell, top=(8, accent_hex), bottom=(12, accent_hex),
                     left=None, right=None)
        cell_margins(cell, top=70, bottom=70)
        p = cell.paragraphs[0]
        p.paragraph_format.space_after = Pt(0)
        run(p, h, size=size, bold=True, color=INK)

    for r in rows:
        cells = t.add_row().cells
        for i, val in enumerate(r):
            cell = cells[i]
            cell.width = widths[i]
            cell_borders(cell, bottom=(4, "D6DCE5"), top=None,
                         left=None, right=None)
            cell_margins(cell, top=60, bottom=60)
            p = cell.paragraphs[0]
            p.paragraph_format.space_after = Pt(0)
            run(p, val, size=size, bold=(i == 0 and len(headers) > 1))
    for col, w in zip(t.columns, widths):
        col.width = w

    # Never let a table straddle a page: rows do not split, and every row but
    # the last is glued to the one after it, so Word moves the whole table.
    for i, row in enumerate(t.rows):
        row._tr.get_or_add_trPr().append(_el("w:cantSplit"))
        if i < len(t.rows) - 1:
            for cell in row.cells:
                for p in cell.paragraphs:
                    p.paragraph_format.keep_with_next = True

    spacer(doc, 8)
    return t


# ------------------------------------------------------------------ build

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
    run(footer, "RCRAInfo API Keys — Explained Simply   ·   "
                "MDE Phase 1   ·   September 4, 2026",
        size=9, color=INK_SOFT)

    # ---------------------------------------------------------- title page
    for _ in range(3):
        doc.add_paragraph().paragraph_format.space_after = Pt(0)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(6)
    run(p, "RCRAInfo API Keys", size=40, bold=True, color=BLUE)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(18)
    run(p, "Explained Simply", size=26, color=AMBER)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(6)
    run(p, "What they are, who makes them, and what happens once the "
           "download program runs on its own at night.",
        size=13, color=INK_SOFT)

    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(30)
    run(p, "Prepared for MDE management   ·   Phase 1   ·   "
           "September 4, 2026   ·   Revision 1",
        size=11, color=INK_SOFT)

    callout(doc, "amber", "Please read this first.", [
        "This document says what we know today, and it is honest about what we "
        "do not know yet.",
        "Anything we have not confirmed is marked clearly in Section 9. Please do "
        "not assume those items are settled — several of them need a decision "
        "from management, or an answer from EPA, before we can finish building.",
    ], width=Inches(8.0), indent=0.7)

    # ------------------------------------------------------ the whole idea
    heading(doc, None, "The whole thing, in six sentences", page_break=True)

    for i, (lead, rest) in enumerate([
        ("To download data from EPA, our program has to prove who it is. ",
         "EPA will not send data to a stranger."),
        ("It proves who it is with two secrets that EPA gives us: an API ID and an API Key. ",
         "Think of them as a username and a password, made for a program instead of a person."),
        ("A human being had to log in to EPA's website and click a button to create those two secrets. ",
         "No software can do that step. There is no button EPA offers to a program."),
        ("Every time our program runs, it shows EPA the ID and the Key, and EPA hands back a token. ",
         "The token is a temporary pass. It stops working after 20 minutes."),
        ("The program gets fresh tokens by itself, all night, forever. ",
         "Nobody is called. Nobody is woken up. This is normal and expected."),
        ("The program can never make itself a new API Key. ",
         "Only a person, on EPA's website, can do that."),
    ], 1):
        p = doc.add_paragraph()
        pf = p.paragraph_format
        pf.left_indent = TEXT_INDENT + Inches(0.35)
        pf.first_line_indent = Inches(-0.35)
        pf.space_after = Pt(9)
        run(p, "%d.  " % i, size=12, bold=True, color=AMBER)
        run(p, lead, size=12, bold=True)
        run(p, rest, size=12, color=INK_SOFT)

    spacer(doc, 4)

    callout(doc, "green", "The question everyone is asking: "
                          "“How does the app get a new API Key?”", [
        "It does not — and it does not need to.",
        "What the app renews, over and over and entirely by itself, is the "
        "20-minute token. That is the thing that keeps expiring, and the app "
        "handles it without help.",
        "The API Key is a different thing. It is long-lived, and replacing it is "
        "always a human task on EPA's website. Sections 4 and 9 explain exactly "
        "when that becomes necessary.",
    ])

    # ------------------------------------------------------------ Section 1
    heading(doc, "1", "There are two different things. Do not mix them up.",
            page_break=True)
    body(doc, "Almost every misunderstanding about this topic comes from one "
              "confusion: treating the API Key and the token as the same thing. "
              "They are not. One is made by a person and lasts a long time. The "
              "other is made by the program and lasts 20 minutes.")
    figure(doc, "01-two-things.png",
           "Figure 1 — The API Key is like a house key. The token is like a "
           "wristband that expires.")

    subhead(doc, "The two, side by side")
    table(doc,
          ["", "The API Key (and API ID)", "The token"],
          [["Who makes it", "A person, on EPA's website", "The program, automatically"],
           ["How long it lasts", "A long time — see Section 9", "20 minutes"],
           ["How often it changes", "Almost never", "Constantly, all night"],
           ["Who sees it", "The person who generated it", "Nobody; it is never written down"],
           ["Where it lives", "Locked up on our server", "In memory only, then discarded"],
           ["If it is lost", "A person must generate a new one", "The program simply asks for another"]],
          widths=[1.5, 2.9, 2.5], accent="blue")

    # ------------------------------------------------------------ Section 2
    heading(doc, "2", "Where an API Key comes from", page_break=True)
    body(doc, "Getting an API Key is entirely a people process. Five steps, and "
              "every one of them needs a human being. The important part for "
              "planning is that two of the steps involve waiting on someone else.")
    figure(doc, "02-key-is-born.png",
           "Figure 2 — Every step needs a person. No software can do any of it.",
           width=Inches(7.6))   # tallest figure; narrowed so it shares its page

    callout(doc, "red", "The Key is shown on screen exactly once.", [
        "When the Generate button is clicked, EPA displays the API ID and the API "
        "Key together, one time. Close that page without copying the Key and it is "
        "gone for good — the only remedy is to generate a brand new one.",
        "This is why the person who generates the Key must be ready, at that "
        "moment, to put it straight onto the server. It must not travel through "
        "email, chat, or a note on a desk.",
    ])

    # ------------------------------------------------------------ Section 3
    heading(doc, "3", "What happens every night, all by itself", page_break=True)
    body(doc, "This section is the direct answer to the question that started this "
              "document. Below is everything the program does on a normal night. "
              "Notice that no person appears anywhere in it.")
    figure(doc, "03-every-night.png",
           "Figure 3 — The app trades its Key for a 20-minute token, and keeps "
           "trading for a fresh one until the work is done.")

    body(doc, "The loop between steps 3 and 4 may run hundreds of times during a "
              "long download. Each turn of that loop is a routine, expected, "
              "invisible event. It is not an error, it is not a warning, and it "
              "does not need to be reported to anyone.")

    callout(doc, "blue", "Two words that sound alike but mean very different things", [
        "Renewing the TOKEN happens every 20 minutes, automatically, forever. "
        "Nobody is involved.",
        "Replacing the KEY happens rarely, by hand, on EPA's website. A named "
        "person is always involved.",
    ])

    # ------------------------------------------------------------ Section 4
    heading(doc, "4", "When does a person actually have to step in?",
            page_break=True)
    body(doc, "Only four situations require a human being. Everything else the "
              "program handles quietly on its own.")
    figure(doc, "04-human-steps-in.png",
           "Figure 4 — Four situations need a person. Token expiry is not one "
           "of them.")

    subhead(doc, "The same four situations, in a table")
    table(doc,
          ["Situation", "What has to happen", "How often we expect it"],
          [["A new or rebuilt server",
            "A person pastes the Key in again on that machine",
            "Once per server, plus any rebuild"],
           ["Moving from practice to the real system",
            "A completely different Key is generated and installed",
            "Once, at go-live"],
           ["The Key stops working",
            "A person generates a new Key and installs it",
            "Unknown — see Section 9"],
           ["MDE chooses to replace the Key",
            "A planned, scheduled human task",
            "However often MDE policy says"]],
          widths=[2.1, 2.6, 2.2], accent="amber")

    # ------------------------------------------------------------ Section 5
    heading(doc, "5", "Practice and Real are two completely separate worlds",
            page_break=True)
    body(doc, "EPA runs two RCRAInfo systems: a practice one for testing and the "
              "real one holding actual regulated-business data. They share nothing. "
              "Two accounts, two approvals, two Keys.")
    figure(doc, "05-two-worlds.png",
           "Figure 5 — Nothing crosses over between the practice system and the "
           "real one.")

    callout(doc, "amber", "This has a schedule consequence worth acting on now.", [
        "Each account needs its own approval, and each approval has its own "
        "waiting period. Requesting one, waiting, then requesting the other means "
        "waiting twice.",
        "Recommendation: request the real-system account at the same time as the "
        "practice one, even though we will not use it for months.",
    ])

    # ------------------------------------------------------------ Section 6
    heading(doc, "6", "Once locked up, the Key only works on one server",
            page_break=True)
    body(doc, "The Key is not left sitting in a readable file. The first time the "
              "program runs, it locks the Key up using a feature built into Windows. "
              "That lock is tied to the specific machine that performed it.")
    figure(doc, "06-locked-to-server.png",
           "Figure 6 — Copying the settings file to another machine does not "
           "carry the Key with it.")

    body(doc, "In plain terms: the locked-up Key cannot be copied from one server "
              "to another. That is a security benefit — stealing the file gains "
              "an attacker nothing. It is also an operational cost: every server "
              "must be set up by hand, once, by someone holding the Key.")

    # ------------------------------------------------------------ Section 7
    heading(doc, "7", "Whose Key is it? This is the real risk.", page_break=True)
    body(doc, "This is the part of the picture we would most like management to "
              "look at, because it is a people problem rather than a software one.")
    figure(doc, "07-whose-key.png",
           "Figure 7 — Today the Key belongs to one named individual's EPA "
           "account.")

    body(doc, "Today, an API Key is generated from one person's RCRAInfo account. "
              "The nightly download therefore depends on that individual's account "
              "remaining active and keeping its permission. If that person leaves, "
              "changes role, or has their access reviewed, we believe the Key stops "
              "working and the nightly load fails — and the only fix is for "
              "someone with the right permission to generate a new Key.")

    callout(doc, "red", "What this means in practice", [
        "A staffing change can become a data outage, with no code change and no "
        "warning.",
        "We would like to ask EPA whether a shared MDE account, not tied to one "
        "individual, may hold the Key instead. We do not yet know whether EPA "
        "permits this.",
        "Whatever the answer, MDE should name at least two people who are "
        "permitted and trained to generate a replacement Key.",
    ])

    # ------------------------------------------------------------ Section 8
    heading(doc, "8", "If the Key stops working, what will you see?",
            page_break=True)
    body(doc, "It is worth knowing in advance, because the failure is abrupt and "
              "total rather than gradual.")

    for n, (h, t) in enumerate([
        ("The program cannot get a token.",
         "EPA answers with a refusal that means, roughly, “these credentials "
         "are not valid.”"),
        ("The run stops immediately.",
         "It does not try to carry on with partial data. Nothing is half-loaded, "
         "so there is no cleanup to do."),
        ("The failure is recorded.",
         "The reason, the time, and EPA's own reference number for the refusal are "
         "all written down."),
        ("The monitoring website shows it.",
         "The status for that night's run turns to failed, with the reason "
         "visible — this is what the web application in Phase 1 is for."),
        ("A person generates a new Key and installs it.",
         "This is the manual step from Section 2, done again."),
        ("The run is started again.",
         "Nothing is lost. The next run picks up from where the data left off."),
    ], 1):
        p = doc.add_paragraph()
        pf = p.paragraph_format
        pf.left_indent = TEXT_INDENT + Inches(0.35)
        pf.first_line_indent = Inches(-0.35)
        pf.space_after = Pt(7)
        run(p, "%d.  " % n, size=11.5, bold=True, color=AMBER)
        run(p, h + "  ", size=11.5, bold=True)
        run(p, t, size=11.5, color=INK_SOFT)

    spacer(doc, 4)

    callout(doc, "green", "The good news", [
        "Because a bad Key stops the run at the very first step, there is no risk "
        "of loading half of a night's data or of silently loading nothing while "
        "appearing to succeed. It fails loudly, immediately, and visibly.",
    ])

    # ------------------------------------------------------------ Section 9
    heading(doc, "9", "What we know for certain, and what we do not",
            page_break=True)
    subhead(doc, "Confirmed — we have done these ourselves or read them in "
                 "EPA's documentation", color=GREEN)
    for t in [
        "There are two values, an API ID and an API Key, and both are needed.",
        "A developer can generate them on EPA's practice site today. This has been done.",
        "The Key is displayed one time only, at the moment it is generated.",
        "Generating a Key requires an extra permission, granted by MDE's own "
        "RCRAInfo Administrator, not by EPA.",
        "Trading the ID and Key for a token is a single request, and the token "
        "lasts 20 minutes.",
        "The practice system and the real system need separate accounts and "
        "separate Keys.",
        "There is no EPA request a program can make to create or replace an API Key.",
    ]:
        bullet(doc, t)

    subhead(doc, "Not known — these are open questions for EPA", color=RED)
    table(doc,
          ["Question for EPA", "Why it matters to MDE"],
          [["Does an API Key expire on its own, and if so after how long?",
            "Determines whether we need a scheduled human task on the calendar."],
           ["Can a Key be replaced without downtime — can two Keys be valid at once?",
            "Decides whether a Key change needs a maintenance window."],
           ["May a shared MDE account hold the Key instead of one named person?",
            "This is the single biggest continuity risk. See Section 7."],
           ["What happens to the Key if the account holder's role or access changes?",
            "Tells us whether HR and access reviews can silently break the nightly load."],
           ["Does EPA warn anyone before a Key stops working?",
            "Decides whether we can act in advance or only react after a failure."],
           ["How long does MDE Administrator approval usually take?",
            "Needed to schedule Phase 1 realistically."],
           ["Does EPA limit how often we may request tokens or download records?",
            "Affects how fast the nightly download can safely run."]],
          widths=[3.4, 3.5], accent="red")

    # ----------------------------------------------------------- Section 10
    heading(doc, "10", "What we are asking management to decide",
            page_break=True)
    table(doc,
          ["#", "Decision or action", "Who we think should own it"],
          [["1", "Name the role that owns the API Key — and name at least two "
                "people trained to replace it.",
            "MDE management"],
           ["2", "Decide who contacts EPA with the seven questions in Section 9, "
                "and by when.",
            "MDE management"],
           ["3", "Decide whether to formally request a shared MDE account to hold "
                "the Key.",
            "MDE management, with EPA"],
           ["4", "Request the real-system RCRAInfo account now, in parallel with "
                "the practice one.",
            "Project team, needs approval"],
           ["5", "Approve that each server — test, acceptance, production, and "
                "any rebuild — gets one manual Key installation.",
            "MDE management and IT"],
           ["6", "Approve a short written procedure for replacing the Key outside "
                "business hours.",
            "MDE management and IT"]],
          widths=[0.4, 4.1, 2.4], accent="amber")

    callout(doc, "blue", "Nothing here is blocked on software.", [
        "The program can be built and tested with the practice Key we already "
        "have. Every item above is about people, accounts, and permissions — "
        "which is precisely why they take longer than the software does, and why "
        "we are raising them now rather than later.",
    ])

    # ----------------------------------------------------------- Section 11
    heading(doc, "11", "Words you may hear", page_break=True)
    table(doc,
          ["Word", "What it actually means"],
          [["API", "A way for one program to ask another program for data, with no "
                   "person clicking anything."],
           ["API ID", "The program's user name at EPA. Not secret in the same way "
                      "the Key is, but still not published."],
           ["API Key", "The program's password at EPA. Long-lived. Shown once. "
                       "Made by a person."],
           ["Token (or JWT)", "A temporary pass, good for 20 minutes, that the "
                              "program gets by showing its ID and Key."],
           ["Credentials", "A collective word for the ID and the Key together."],
           ["Environment", "One separate copy of a system. EPA has a practice one "
                           "and a real one; MDE has several of its own."],
           ["Pre-production", "EPA's name for the practice system."],
           ["Production", "EPA's name for the real system, with real data."],
           ["Encrypted", "Scrambled so it cannot be read. Here it also means "
                         "locked to one specific server."],
           ["Rotation", "Deliberately replacing a Key with a new one, on a "
                        "schedule, as a security practice."],
           ["Handler", "EPA's word for a regulated business or site. Handler "
                       "records are the data we are downloading."]],
          widths=[1.6, 5.3], accent="gray", size=10)

    # ----------------------------------------------------------- Section 12
    heading(doc, "12", "Where to go for more detail", page_break=True)
    body(doc, "This document is deliberately simplified. Nothing in it is untrue, "
              "but a great deal has been left out on purpose.")
    body(doc, "If a technical reviewer needs the exact requests we send to EPA, the "
              "exact refusals EPA can send back, how the Key is stored, or the "
              "numbered list of every open question, all of that lives in the two "
              "project documents below. They are kept up to date as answers arrive.")

    table(doc,
          ["Document", "What it contains"],
          [["Phase1-Analysis.md",
            "The verified facts about EPA's interface, the error codes, and the "
            "numbered register of every open gap — including the API-key questions "
            "in Section 9 of this document."],
           ["Phase1-Plan.md",
            "The work plan: what gets built, in what order, who owns each piece, "
            "and which items are waiting on a decision or on EPA."]],
          widths=[1.9, 5.0], accent="gray")

    callout(doc, "gray", "One last thing worth remembering", [
        "The single most useful sentence in this whole document is this: the app "
        "renews its 20-minute token by itself, forever, and it can never renew its "
        "API Key. Everything else follows from that.",
    ])

    tighten(doc)
    doc.save(OUTFILE)
    print("wrote", OUTFILE)


if __name__ == "__main__":
    build()
