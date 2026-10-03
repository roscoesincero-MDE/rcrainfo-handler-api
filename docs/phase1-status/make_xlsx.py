"""
Builds docs/phase1-status/Phase1-Status.xlsx.

Run:  python docs/phase1-status/make_xlsx.py
Requires: openpyxl.

Audience is MDE management. It is the row-by-row companion to Phase1-Plan.md:
the same work, one line per step, with a status you can filter on instead of a
document you have to scroll. It deliberately says LESS than the plan -- every
row is a pointer back to a plan section that carries the reasoning.

This file is the source of truth and the .xlsx is the artifact. Regenerate
rather than hand-editing the workbook, or the next regeneration silently
discards the edit. When a step's status changes, change it HERE, in the same
commit as the work and the [Rn] revision block in Phase1-Plan.md.
"""

import os
from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.table import Table, TableStyleInfo

HERE = os.path.dirname(os.path.abspath(__file__))
OUTFILE = os.path.join(HERE, "Phase1-Status.xlsx")

AS_OF = "2026-09-07"

# Same palette as docs/api-keys, so the two management documents match.
INK = "1C2533"
INK_SOFT = "58657A"
BLUE, BLUE_L = "1F4E79", "E9F0F8"
GREEN, GREEN_L = "1A6A42", "E5F3EB"
AMBER, AMBER_L = "A96304", "FDF3E0"
RED, RED_L = "A32820", "FCEAE8"
GRAY_L = "F4F6F9"
RULE = "C9D2DE"

FONT = "Segoe UI"

DONE = "Done"
GOING = "In progress"
NOT_STARTED = "Not started"
BLOCKED = "Blocked"

# The four statuses management asked for, and what each one is allowed to mean.
# "Blocked" is reserved for work that cannot start until someone OUTSIDE the
# development team acts -- it is not a synonym for "not started", and keeping
# them apart is the whole point of the sheet.
STATUS_STYLE = {
    DONE: (GREEN, GREEN_L),
    GOING: (BLUE, BLUE_L),
    NOT_STARTED: (INK_SOFT, GRAY_L),
    BLOCKED: (RED, RED_L),
}

LEGEND = [
    (DONE, "Built, tested, and written up in the plan's own “as built” section. "
           "Nothing is owed on it."),
    (GOING, "Started and partly delivered. The row says how much."),
    (NOT_STARTED, "Not begun, and nothing outside the team is stopping it. "
                  "It is waiting its turn."),
    (BLOCKED, "Cannot start until someone outside the development team decides "
              "something or supplies something. The “Waiting on” column names who."),
]

# --------------------------------------------------------------- the status rows
#
# (id, workstream, what it delivers, status, waiting on, where it stands, plan §)
#
# roll_up rows are totals of the rows beneath them and are excluded from the
# counts on the Overview sheet, so nothing is counted twice.

ROLL_UP = {"D2"}

STEPS = [
    # ---------------------------------------------------------------- section 1
    ("1.1", "Getting started",
     "Get an API ID and API Key from EPA, so the application can sign in to RCRAInfo",
     GOING, "EPA · MDE RCRAInfo Administrator (G1)",
     "The pre-production key was issued 2026-09-04 and PROVED against EPA on 2026-09-06 — the "
     "first live call this project has ever made. EPA issued a token. Production is a separate "
     "registration and has not been requested yet; it is not needed until F4.",
     "§1.1"),
    ("1.2", "Getting started",
     "Ask EPA the two questions that change the design: how fast we may call them, and how they "
     "signal a deleted record",
     NOT_STARTED, "EPA (G21, G23)",
     "Both questions are written out and the EPA contact is named in the plan; the email has not "
     "gone out. Worth adding a third, lower-stakes question while asking: the list of valid "
     "sourceType values (G24). F2 can now measure the rate limit ourselves, which makes the answer "
     "a confirmation rather than a dependency.",
     "§1.2"),
    ("1.3", "Getting started",
     "Settle the scope questions only MDE can answer",
     DONE, "—",
     "All answered 2026-09-04: Maryland only, other-ids in scope, episodic events out, mirror all "
     "24 EPA code lists, omit the Washington addendum, and the developer runs the database scripts.",
     "§1.3"),
    ("1.4", "Getting started",
     "Four technical decisions needed before a single table could be created",
     DONE, "—",
     "Answered 2026-09-04: the database is named RCRAInfo, audit columns are at least 128 "
     "characters wide, sets are passed to procedures as JSON, and there is one local SQL Server "
     "instance.",
     "§1.4"),
    ("1.5", "Getting started",
     "Make every database script safe to run twice, because a person runs them by hand",
     DONE, "—",
     "Now a property of every script rather than a task: a build check runs the whole set twice and "
     "fails if the second run changes anything, and the SQL validator refuses the shortcuts that "
     "would break it.",
     "§1.5"),

    # ---------------------------------------------------------------- workstream A
    ("A1", "A — Foundation",
     "Put the work under source control, with the rules that keep credentials out of it",
     DONE, "—", "Git repository; the ignore rules for secrets are themselves checked by a "
     "build guardrail.", "§A1"),
    ("A2", "A — Foundation",
     "Solution scaffolding — projects, shared build settings, warnings treated as errors",
     DONE, "—", "One place sets the compiler settings for every project, so a warning cannot "
     "be ignored in one corner of the solution.", "§A2"),
    ("A3", "A — Foundation",
     "Lock the project to SQL Server 2022, so nothing that only works on 2025 can be written by "
     "accident",
     DONE, "—",
     "Development happens on SQL Server 2025 and the target is 2022, so this is a real hazard "
     "rather than a formality. A parser gate plus an automatic check on every script; nine "
     "deliberately-wrong fixtures prove the gate actually catches violations.",
     "§A3"),
    ("A4", "A — Foundation",
     "A local development database set to the 2022 compatibility level",
     DONE, "—", "Created by a guarded script, so re-running it is harmless.", "§A4"),
    ("A5", "A — Foundation",
     "The two application logins, their separate rights, and the file permissions that keep the web "
     "app away from the loader's credentials",
     DONE, "—",
     "Neither application can change the database structure. The web app is denied read access to "
     "the loader's credential file, which is what makes the encryption meaningful.",
     "§A5"),
    ("A6", "A — Foundation",
     "The helper that writes the plain-English description onto every table and column",
     DONE, "—", "Deliberately the first object created in the database, because every table "
     "script that follows depends on it.", "§A6"),
    ("A7", "A — Foundation",
     "Written record of what Workstream A actually built",
     DONE, "—", "Completed 2026-09-04.", "§A7"),

    # ---------------------------------------------------------------- workstream B
    ("B1", "B — Schema",
     "The main Handler tables — one row per version of a record, so nothing is ever overwritten",
     DONE, "—",
     "Includes the 18 child collections and the alternate identifiers table. No state column on any "
     "contact or address is restricted to Maryland, because a Maryland handler may legitimately "
     "have an out-of-state contact.",
     "§B1"),
    ("B2", "B — Schema", "EPA's 24 code lists, mirrored as tables", DONE, "—",
     "No foreign keys point at them, deliberately: an unrecognised code is recorded as a data-quality "
     "observation and the handler still loads, rather than the whole batch failing.", "§B2"),
    ("B3", "B — Schema",
     "The tracking tables that record what each run did, handler by handler",
     DONE, "—", "Five tables rather than the three originally planned — the two additions are "
     "the append-only retry log and the data-quality observations.", "§B3"),
    ("B4", "B — Schema", "A plain-English description on every table and every column", DONE,
     "EPA data dictionary for 278 of them (G12)",
     "All 1,011 columns carry a description. 278 of them are honest placeholders that say so, and "
     "they stay that way until EPA supplies its data dictionary. They are visible in the database, "
     "so nobody has to take this on trust.",
     "§B4"),
    ("B5", "B — Schema", "Written record of what Workstream B built", DONE, "—",
     "Completed 2026-09-04: 50 tables, 2 views, 1,011 described columns, 130 indexes, 28 foreign "
     "keys, none of them cascading.", "§B5"),

    # ---------------------------------------------------------------- workstream DA
    ("DA0", "DA — Data access",
     "The error-logging table and procedures that every other procedure uses",
     DONE, "—",
     "Built before anything that depends on it, on purpose. It exists because of a specific past "
     "failure in another MDE application: a procedure that only read data called a function that "
     "errored, and nothing recorded it.",
     "§DA0"),
    ("DA1", "DA — Data access",
     "Build one procedure of each shape first, then have MDE review them before the other twenty",
     DONE, "—", "Reviewed by MDE 2026-09-05. The review settled the paging and sorting contract "
     "for every read procedure and closed one open design question outright.", "§DA1"),
    ("DA2", "DA — Data access", "The standard parameter block every read procedure uses", DONE,
     "—", "Settled by the DA1 review rather than built as a separate step.", "§DA2"),
    ("DA3", "DA — Data access", "Audit stamping written into every procedure", DONE, "—",
     "The procedures own the audit columns — who changed a row and when — rather than the "
     "application, so the record cannot be bypassed.", "§DA3"),
    ("DA4", "DA — Data access", "The remaining stored procedures", DONE, "—",
     "14 of 14 built. 24 procedures in the database in total. ONE OF THEM WAS ADDED ON 2026-09-06 "
     "BECAUSE A DIFFERENT ONE HAD BEEN ASSUMED TO DO THE JOB AND DOES NOT. When a download is "
     "interrupted, the record of each individual site it had already listed but not yet fetched is "
     "left saying “still working on it”, and nothing in the database was able to close those "
     "records off afterwards. There are 202 of them, spread over about ten past downloads. That "
     "matters because those records are exactly what the monitoring web application is going to put "
     "on the screen — as it stands, a member of staff would see 202 downloads apparently still "
     "in progress for jobs that finished weeks ago. Worth noting what the count also revealed: every "
     "one of those 202 belongs to a download that reported SUCCESS. The new procedure closes the "
     "records off and, for each such download, writes a note recording the contradiction rather than "
     "quietly tidying it away — the note is the only evidence that a download can report success "
     "while leaving work unfinished. It is written and checked but NOT yet applied to the database; "
     "the developer applies database changes by hand, so those 202 records stay as they are until "
     "that happens.", "§DA4"),
    ("DA5", "DA — Data access",
     "The thin Entity Framework layer, plus a round-trip test for every procedure",
     DONE, "—",
     "The round-trip tests found five real defects the first time they ran, which is the argument for "
     "having written them.",
     "§DA5"),

    # ---------------------------------------------------------------- workstream C
    ("4.1", "C — Credential bootstrap",
     "The code that encrypts the stored passwords on the machine, the first time it runs",
     DONE, "—",
     "Windows' own machine-bound encryption. It never encrypts a password that failed validation "
     "and never falls back to storing one in plain text.",
     "§4.1"),
    ("4.2", "C — Credential bootstrap",
     "The written procedure for seeding a new machine with its credentials",
     DONE, "—", "Written down before it was needed, because the API Key is displayed once by "
     "RCRAInfo and cannot be read back.", "§4.2"),
    ("4.3", "C — Credential bootstrap",
     "Validate the RCRAInfo API pair the same way — by actually calling EPA",
     DONE, "—",
     "Was a shape check standing in for the real call; became the real call when D1 was built; ran "
     "for real on 2026-09-06 and EPA issued a token. This is what closed G1.",
     "§4.3"),
    ("4.4", "C — Credential bootstrap", "Written record of what Workstream C built", DONE,
     "—", "Completed 2026-09-06. All five specified failure behaviours are tested and also "
     "driven end to end through the compiled executable.", "§4.4"),

    # ---------------------------------------------------------------- workstream D
    ("D1", "D — API client",
     "Sign in to EPA and keep the 20-minute token fresh by itself, for the length of a load",
     DONE, "—",
     "PROVED against EPA pre-production on 2026-09-06, and proving it earned its keep: EPA sends its "
     "expiry timestamp in a format the standard reader refuses, so the token was being rejected even "
     "though the credential was perfectly good. Found and fixed the same day. A key tested only "
     "with a plain web request would have looked fine and shipped the defect.",
     "§D1"),
    ("D2", "D — API client",
     "Fetch orchestration — the download loop itself",
     DONE, "—", "COMPLETE as of 2026-09-06. All ten of its pieces are built, including the loop "
     "that runs them in order; the ten rows below are the detail. The console application can now "
     "perform a download from end to end, which is what F1 was waiting on.", "§D2"),
    ("D2.1", "D — API client",
     "Record every request attempt, so a retry history exists after the fact",
     DONE, "—", "Also the first place the rule “never store the query string or a header” "
     "is enforced by the database rather than merely documented — RCRAInfo credentials travel in "
     "those, and the monitoring web app can read this table.", "§D2"),
    ("D2.2", "D — API client",
     "The client that makes the read calls and classifies every answer EPA can give",
     DONE, "—",
     "A “not found” answer is what tells us EPA withdrew a record, so it is classified per "
     "endpoint against what EPA documents for that endpoint. Getting this wrong would have deleted "
     "data instead of raising an error.",
     "§D2"),
    ("D2.3", "D — API client",
     "Write the per-handler progress rows in batches instead of one at a time",
     DONE, "—",
     "Building it exposed a fault nothing in the database could have shown: a status value had been "
     "legal to store since the schema was created with nothing able to write it, so every withdrawn "
     "record would have been re-fetched nightly, forever.",
     "§D2"),
    ("D2.4", "D — API client", "Teach the client EPA's 24 code-list endpoints", DONE, "—",
     "Reading EPA's specification rather than our own notes produced four corrections, every one of "
     "them in the direction of deleting reference data we should have kept.", "§D2"),
    ("D2.5", "D — API client", "The code-list refresh that runs before the handler data", DONE,
     "—",
     "Organised around one asymmetry: a failed code-list call changes nothing, while a successful "
     "one can retire reference data. So it refuses to succeed wrongly — it will not split an "
     "oversized payload, because the second call would retire everything the first wrote. THAT "
     "REFUSAL DID ITS JOB ON 2026-09-06, twice, and both refusals were correct: EPA's industry-code "
     "list is 1,701 codes, which is more than the safety ceiling allowed, and EPA's waste-code list "
     "carried a description longer than the column we had made for it. Neither could ever have "
     "loaded, and the refusal is the only reason we know rather than having half a list. Both are "
     "fixed — the ceiling was raised to a measured number rather than a guessed one, and the two "
     "columns were widened to what EPA actually publishes — and both lists have now loaded in full. "
     "The industry-code list had never held a single row before that day.",
     "§D2"),
    ("D2.6", "D — API client",
     "Walk EPA's change feed by date window to decide which records to fetch",
     DONE, "—",
     "EPA's summaries call has no paging and no “here is how much there was” marker, so a date "
     "window that fails is invisible afterwards. The load therefore refuses to move its progress "
     "marker unless every window succeeded — otherwise a failed window becomes a permanent hole "
     "nothing would ever ask about again.",
     "§D2"),
    ("D2.7", "D — API client", "Resume an interrupted run from where it stopped", DONE,
     "—",
     "Building it found a sequencing fault: a run killed by a reboot is still marked “running”, "
     "and the housekeeping that marks it abandoned happens after the resume decision has to be "
     "made. So the resume read applies the same staleness rule itself, from a single setting shared "
     "by both — the only way the two can be made to agree.",
     "§D2"),
    ("D2.8", "D — API client", "Limit how many requests run at once", DONE, "—",
     "Set to a deliberately cautious 2 requests per second and 2 at a time, and enforced at the "
     "network layer rather than in the download loop, so any code added later is held to the same "
     "ceiling. Being slowed down by EPA is recoverable; being blocked is a phone call.",
     "§D2"),
    ("D2.9", "D — API client", "Back off politely when EPA says “slow down”",
     DONE, "EPA, for the documented limit (G21) — no longer holds anything up",
     "When EPA asks us to wait, we wait exactly as long as EPA asked rather than guessing. The "
     "retry timings had been running on library defaults meant for a website, not for an overnight "
     "batch; they are now stated settings that F2 can tune from one place.", "§D2"),
    ("D2.10", "D — API client",
     "The run that ties every step together, start to finish",
     DONE, "—",
     "The piece that decides what a run is allowed to CONCLUDE. Three of its steps only work in "
     "one order, and the database cannot enforce any of them — so the tests check the order itself, "
     "not just the outcome. The strict rule it enforces: the progress marker moves only when every "
     "date window succeeded and every record was accounted for. A "
     "single failed record holds the marker, because once it moves past that record nothing would "
     "ever ask for it again. The run also reports a distinct exit code for each way it can end, so "
     "Task Scheduler can tell “nothing to do” from “it failed”. "
     "THAT RULE WAS CORRECTED ON 2026-09-06, AND THE VERSION BEFORE THE CORRECTION WOULD HAVE COST "
     "REAL MONEY EVERY NIGHT. It had a third condition — the progress marker also would not move "
     "unless all 24 of EPA's code lists had refreshed. Two of those lists come back from EPA empty, "
     "the application is right to refuse an empty list (accepting one would wipe out our copy), and "
     "asking again gives the same empty answer. So that condition could never be satisfied, and the "
     "progress marker could never move at all. The whole-of-Maryland download on 2026-09-06 did "
     "nearly two hours of correct work and was unable to record that it had done it, which means the "
     "next night would have started over from the same date — and the date range grows by a day every "
     "day. The code lists still decide whether a run is allowed to report full success, which is where "
     "that check belongs; they no longer decide whether the download is allowed to remember where it "
     "got to. The lesson is the general one: two checks that are each individually sensible can "
     "combine into a rule that nothing can ever pass.",
     "§D2"),
    ("D2.11", "D — API client",
     "Download one named handler on demand, and cope with a missed schedule",
     DONE, "—",
     "Two things MDE asked for on 2026-09-06. First, a way to download a single handler by hand — "
     "either just its latest record or its whole history; both are supported, and the operator has "
     "to say which, because guessing either way is wrong in a way nobody would notice. Second, the "
     "scheduled job cannot be assumed to have run: the gap since the last download might be a week, "
     "a month, or — on the very first run — decades. Acting on that found a real defect. THE "
     "APPLICATION COULD NOT HAVE PERFORMED ITS FIRST DOWNLOAD AT ALL: the progress marker tells the "
     "run where to start, and on a brand-new database there is no marker yet, so the run had nothing "
     "to ask for and stopped. A first date is now a setting, with no default, because how far back "
     "the download reaches is a decision and a wrong one would be invisible. Also fixed: a long "
     "catch-up used to lose ALL its progress if any single date window failed — decades is roughly "
     "2,400 windows, so one hiccup discarded the whole night, every night, and it would never have "
     "finished. It now keeps the progress it has actually earned.",
     "§D2"),
    ("D3", "D — API client", "Map all 377 EPA fields into the tables", DONE, "—",
     "Generated once from EPA's specification rather than hand-written, and the tables it targets "
     "are already built. WORTH READING AS A CORRECTION, not just a tick: this step was recorded as "
     "not started and the mapping had in fact been built weeks earlier. What was genuinely missing "
     "was proof that it worked. 167 of the 377 fields live in the 18 repeating lists (a site's "
     "owners, its contacts, its waste codes), and for those, a field whose name EPA changed would "
     "silently arrive as “empty” rather than as an error — the download would report success and the "
     "column would simply never fill in. The existing tests would all have passed with 165 of those "
     "167 fields permanently empty. All 377 are now checked value-by-value on every build, and the "
     "check was deliberately broken first to confirm it actually catches the problem.", "§D3"),
    ("D4", "D — API client", "The update-only run, and the reconciliation that follows it",
     DONE, "—",
     "BUILT AND PROVED AGAINST EPA, and it fixed the duplicate reported last week. EPA keeps every "
     "past version of a site's record and marks one of them as the current one, but EPA only tells us "
     "about the version that changed — it never mentions that the previous version has stopped being "
     "current. So after a download the stored records could disagree about which version is current, "
     "and one site did: MDR000501742 had two versions both claiming to be current and would have "
     "appeared TWICE in the monitoring web app's list. IT NOW APPEARS ONCE. The fix asks EPA for a "
     "site's whole version list after anything about it changes, and sets the current marker from that "
     "list. Four sites turned out to need correcting, not one — the other three were only discovered "
     "because 25 sites were then downloaded with their full history at MDE's request (170 versions in "
     "total), which is worth noting because none of the three could have been found by looking at one "
     "version at a time. Across everything stored, no site's record now claims two current versions "
     "except one of EPA's own practice records, and that one cannot be fixed because EPA has since "
     "removed the practice site altogether and no longer answers questions about it. Nothing was "
     "deleted in the attempt. Also part of this step and also done: EPA's change feed is day-granular, "
     "so each run re-requests the last day it processed and relies on repeat-safe merges. The deletion "
     "question (G23) turned out not to gate this step at all.",
     "§D4-reconcile"),

    # ---------------------------------------------------------------- workstream E
    ("E", "E — Web monitoring app",
     "The web application that shows run status and errors, and notifies the people affected",
     BLOCKED, "MDE and MDE security (G8, G9, G10, G11)",
     "NOW THE NEXT STEP, and nothing technical is in the way — the procedures it reads are built and "
     "tested, and the one data fault that would have shown up on its screens (a site listed twice) "
     "was fixed in D4. Four decisions are in the way: how notifications are sent, who receives them, "
     "when they are suppressed, and how the site authenticates. A fifth question (how a web-app user "
     "is attributed to a change) follows from the fourth.",
     "§6"),

    # ---------------------------------------------------------------- workstream F
    ("F1", "F — Prove, measure, deploy",
     "Load one handler end to end against EPA pre-production",
     GOING, "—",
     "STARTED AND THE MAIN RESULT IS IN: on 2026-09-06 the application downloaded a real Maryland "
     "handler from EPA and stored it, start to finish, without a person touching anything in "
     "between — sign in, ask EPA which versions exist, download the version, store it, record what "
     "happened. Four runs, all successful, two EPA requests each and no retries. THIS IS WHY THE "
     "STEP EXISTS: it found two faults that no amount of testing on this end had shown, and both "
     "are now fixed. The first stopped the download outright. The second was quieter and worse — "
     "the handler's data was stored correctly, but the record of HOW it was stored was left saying "
     "“still working on it”, which the monitoring web app would have shown to staff as an unfinished "
     "download, and the next scheduled run would have downloaded the same thing again. Both were "
     "invisible to the automated tests, which had been asserting the behaviour the code produced "
     "rather than the behaviour the database needs. SINCE THEN the two sizing measurements have "
     "been taken and the download was also proved on a handler with a long history — 16 versions of "
     "one Maryland site, going back through 13 revisions, all downloaded and stored correctly in a "
     "single run. Both were done with a new read-only mode that asks EPA a question and writes "
     "nothing at all, so it can be run safely at any time without touching the database. Two useful "
     "facts came out of it: three months of Maryland changes fit comfortably in a single request to "
     "EPA, which is far more generous than assumed, and the dates EPA sends were finally read "
     "exactly as EPA writes them rather than trusted to have been understood. ONE MORE HISTORY "
     "DOWNLOAD was then run on a second site, chosen because it had history rather than for "
     "convenience, and it earned its keep: all three of that site's versions came down, the one "
     "version already downloaded the day before was correctly recognised as unchanged and left "
     "untouched (its “last modified” stamp did not move, which is exactly right — the audit trail "
     "must not claim a record changed when it did not), and three things turned up that no test "
     "could have found. EPA does not always number versions consecutively — this site has a version 1 "
     "and a version 3 and no version 2. The same EPA site number has carried TWO DIFFERENT SITE "
     "NAMES over the years, twenty-four years apart, which is precisely what keeping the history is "
     "for. And the count reported last week turned out to be measuring EPA's practice records rather "
     "than real ones — see step D4 for the corrected figure. TWENTY-FIVE MORE SITES were then "
     "downloaded with their complete histories at MDE's request — 170 versions across the 25, every "
     "one stored successfully — which was not a test but is now the best evidence the download works "
     "at depth. THE ONE THING LEFT "
     "needs a person: comparing the stored record against what EPA's own website shows for the same "
     "handler. That is the only remaining check that could reveal a field being read into the wrong "
     "place, as opposed to the machinery around it misbehaving. "
     "THAT CHECK IS NOW A WRITTEN CHECKLIST rather than a vague instruction, and it names the exact "
     "sites to look at — see §F1-ui-comparison in the plan. It is entirely read-only: log in to "
     "EPA's pre-production site, search the site number, and read what EPA displays. Six checks, and "
     "each one is on a site where OUR DATABASE HAD TO MAKE A JUDGEMENT that only EPA's own screen can "
     "settle — a site where everything already agrees would prove nothing. The most important single "
     "check is site MDD985416569: EPA told us TWO different versions of it were the current one, we "
     "chose the higher-numbered version, and the other version is dated sixteen months later. "
     "MDE DID THAT CHECK ON 2026-09-07, AND IT PAID FOR THE WHOLE STEP. EPA's own website shows "
     "BOTH versions as current — so EPA's website and EPA's data feed agree with each other, and the "
     "contradiction our software reported is genuinely in EPA's records rather than a mistake in how "
     "we read them. That is reassuring. The second half is not: OF THE TWO, WE HAD PICKED THE WRONG "
     "ONE. The version we chose is one of four identical copies of a form submitted in January 2025; "
     "the version we ignored is a genuine update EPA received in May 2026. MDE also supplied the "
     "explanation, which no amount of examining the data could have produced: THE DUPLICATES ARE "
     "MDE'S OWN — the same information was sent to EPA electronically several times. Our software had "
     "been choosing the highest version number, on the assumption that EPA numbers versions in the "
     "order they happen. It does not; it numbers them in the order it receives them, so repeated "
     "submissions push the number up without anything actually being newer. THE RULE IS FIXED: the "
     "software now chooses by the date EPA received the form, which is the date EPA's own screen "
     "displays, and falls back to the version number only to break a tie. ONE site out of the 410 was "
     "affected and it has been corrected; the other 409 were already right. The report our software "
     "writes when it finds this problem now also prints each version's receive date beside it, so the "
     "next case can be sorted at a glance into “MDE sent this twice, MDE can ask EPA to remove the "
     "copies” or “EPA's records genuinely disagree, send them the evidence”. Measured across all 410: "
     "7 are duplicate submissions and 403 are EPA's own. Separately, and harmlessly, the duplicate-"
     "submission habit shows up 586 times across the whole database — about 1.4% of the records, "
     "costing storage and nothing else, but worth MDE knowing. THE REAL LESSON IS ABOUT THE CHECKLIST "
     "ITSELF: this check had been written with two possible outcomes, and what actually happened was "
     "neither of them. A person looking at the screen saw something nobody had thought to predict. "
     "That is the argument for doing the remaining five. "
     "MDE THEN DID THE REMAINING FIVE, ON THE SAME DAY, AND THE CHECKLIST IS NOW COMPLETE. Three "
     "things came out of it. FIRST AND BIGGEST: the 10,458 warnings are harmless, and the check that "
     "raises them is asking the wrong question. MDE supplied the missing piece of meaning — when a "
     "record is marked as coming from the Implementer, it means MDE itself supplied the information "
     "rather than the site, and EPA quite properly treats that as the site's live record instead of "
     "the site's own form. So a form type with no current version is NORMAL, not a gap. Checked across "
     "the whole database: EVERY SINGLE SITE HAS A CURRENT RECORD — not one is missing — and all 6,860 "
     "of the cases the software was complaining about turn out to be current under a different form "
     "type. One form type in particular, the Biennial Report, is current on only 10 of its 1,605 "
     "records, and it is not broken the other 1,595 times. The software is counting per form type "
     "when the question only makes sense per site, where the answer is zero problems. That will be "
     "corrected. It matters more than it sounds: 10,458 false alarms in a single download would teach "
     "staff to ignore the very list that also carries the real problems. SECOND, and this one is "
     "genuinely good news about the design: MDE fixed one of the disputed sites directly on EPA's "
     "website — picked the right version, saved — and the software then re-downloaded it to see what "
     "EPA had done. EPA updated the timestamp on the version it PROMOTED and left the two versions it "
     "DEMOTED completely untouched. That sounds like a detail and is actually the difference between a "
     "correct database and a permanently wrong one. Had EPA changed nothing, no future scheduled "
     "download would ever have noticed this site. And had our software downloaded only the version "
     "that changed, the two demoted versions would have gone on claiming to be current forever. It is "
     "correct only because the design re-downloads a site's ENTIRE version list whenever any one of "
     "its versions changes — a precaution written months ago, now proved against EPA's real behaviour. "
     "The disputed-site count also drops from 410 to 409, because MDE's fix removed one permanently. "
     "THIRD, and this one is a correction to our own work rather than a discovery: two of the six "
     "checks had been written around numbers I got wrong. I had reported versions as MISSING for three "
     "sites. They are not missing — the database holds exactly what EPA shows, 21, 7 and 23 records "
     "respectively. The mistake was comparing a count of one form type's records against the highest "
     "version number across all of them. EPA numbers versions separately WITHIN each form type and "
     "lists them all on one screen, so the same number appears more than once: one site has four "
     "different records all numbered 1, received in 1980, 1990, 2008 and 2020, which is the "
     "“thirty years apart” MDE noticed. EPA also simply skips numbers sometimes — MDE could see the "
     "gaps on EPA's own screen — and that turns out to be ordinary, affecting about 3.4% of records. "
     "The useful outcome is a rule for the future: “we hold fewer records than the highest version "
     "number” is NOT evidence of anything and must never be turned into an automated check. "
     "WHAT IS LEFT OF THIS STEP IS TWO THINGS. The first is one sitting with two screens open, "
     "comparing the individual fields of one deliberately messy site — a military base with 23 "
     "records and several name changes — against EPA's page. Every structural question the checklist "
     "existed to answer is now answered; what remains tests whether each field landed in the right "
     "place, which no automated test can do because a test can only check the answer its author "
     "already believed. The second is smaller but time-sensitive: reading EPA's dates ONCE with our "
     "own eyes, exactly as EPA writes them, on the record-detail feed. This was already done for the "
     "other feed, and the two are known to write dates differently, so the first answer does not "
     "carry over. It matters because of a fault found earlier in the project: a date EPA sent in an "
     "unexpected form was accepted by our software without complaint and only revealed itself by "
     "breaking later. Our software currently reports that these dates were read successfully, which "
     "is a weaker statement than it sounds — it means the software accepted SOMETHING. THE REASON "
     "NOT TO POSTPONE IT: nothing anywhere keeps a copy of EPA's original response, so a format not "
     "written down at the moment of the call cannot be recovered afterwards. Every further day of "
     "downloading adds records without adding evidence. "
     "THE TOOL FOR THAT SECOND THING IS NOW BUILT AND TESTED, so this step no longer waits on any "
     "development work — it waits on one sitting. It is a read-only mode that stores nothing and can "
     "be run at any time; it asks EPA a question and prints EPA's dates back exactly as EPA wrote "
     "them, punctuation included, so an unexpected form is visible rather than quietly accepted. It "
     "also names any date field the two feeds write differently, which is the actual question. One "
     "correction to what was written here before: it is TWO requests to EPA, not one, and the second "
     "cannot be made without the first. Nobody outside EPA can know which version numbers a site has "
     "— EPA numbers them separately within each form type and skips numbers — and asking for a "
     "version that does not exist gets the same answer EPA gives for a record it has WITHDRAWN, which "
     "our software would be right to treat as a deletion. So the tool asks EPA which versions exist, "
     "picks one using the software's own selection rule, and reads that one. Using the same rule the "
     "scheduled download uses is deliberate: the tool cannot then show a different version from the "
     "one that was actually stored. Both remaining pieces are on the same site, so one sitting "
     "settles the step. "
     "THAT SECOND THING IS NOW DONE, AND THE ANSWER IS THE GOOD ONE. The tool was run against EPA on "
     "2026-09-07 and EPA's dates on the record-detail feed came back in the plain, unambiguous form — "
     "just a date, no time, no time-zone marker, nothing unexpected — which is exactly what the other "
     "feed does. So the two feeds agree after all, and the assumption the whole scheduled-download "
     "design rests on (that EPA tells us WHICH DAY something changed but not what time, so each run "
     "must re-ask about the previous day) is now something we have SEEN on both feeds rather than seen "
     "on one and assumed for the other. This was the last remaining question that would have become "
     "unanswerable with time, and it was answered before it did. The run also confirmed, on a second "
     "site chosen for other reasons entirely, that last week's corrected version-picking rule agrees "
     "with EPA's own website. AND IT FOUND A FAULT IN ITSELF ON ITS FIRST RUN, which is the outcome "
     "this kind of tool exists to make possible. It announced that three date fields were written "
     "differently by the two feeds — and they were not; its own printout, three lines further down, "
     "showed them identical. The cause is worth stating plainly because it was certain rather than "
     "unlucky: the tool was comparing the actual dates instead of their FORMAT. One feed returns every "
     "version of a site (23, for this one) and the other returns a single version, so the two lists of "
     "dates cannot possibly match, and the comparison would have cried wolf on every site with more "
     "than one version. It now compares the shape of the dates and prints both sides of any difference, "
     "so a reader can check the claim from the line that makes it. A second, quieter fault was found in "
     "the same pass and is the more dangerous of the two: the tool kept only the first eight different "
     "dates it saw per field, which means a site whose ninth date was the odd one out would have been "
     "reported as entirely normal — the tool built to catch unexpected date formats could have hidden "
     "one. This site came within a single date of demonstrating it. Fixed: an unfamiliar format is now "
     "always kept and reported, however late it appears. As with the earlier faults in this step, no "
     "automated test could have caught either, for the same reason as before — every test compared "
     "dates that were deliberately IDENTICAL, so not one of them could tell the difference between "
     "\"different date\" and \"different format\", which is the only distinction that matters here. "
     "Reading the real output is what found it. ONE LIMIT WORTH STATING: this site's record did not "
     "contain 7 of the 11 date fields the database can store, because they belong to parts of the form "
     "this site does not have (permits, closure dates, and so on). They were absent, not wrong. Those "
     "seven are also converted by a different part of the system, so one more run on a site that HAS "
     "those sections would close the question completely. It is a small, well-understood follow-up "
     "rather than an open risk. THE CORRECTED TOOL WAS THEN RUN AGAINST EPA AGAIN, because a fix "
     "proved only against our own tests is the situation that produced the fault in the first place. "
     "It now correctly reports that the two feeds agree, on the same site and the same record, and it "
     "no longer hides an unfamiliar date format however late it appears. One useful thing came free: "
     "EPA returned a response of exactly the same size both times, which is early evidence that EPA "
     "sends the same answer the same way each time. That matters for a planned saving — skipping "
     "records that have not changed by comparing a fingerprint of them — which only works if EPA is "
     "consistent about how it writes its answers. It is not proof, and it is now on the list to test "
     "properly rather than assume. WHAT IS LEFT OF THIS STEP IS THE ONE SITTING WITH TWO SCREENS.",
     "§F1"),
    ("F2", "F — Prove, measure, deploy",
     "Load a scoped batch and measure it — throughput, batch size, and requests per handler",
     GOING, "—",
     "This is where the two unknown numbers get answered: how big the initial load is (G22), and "
     "what request rate EPA actually tolerates (G21). STARTED AHEAD OF SCHEDULE, because MDE asked "
     "for two catch-up downloads and those ARE scoped batches — so the measurements came out of real "
     "work rather than out of a rehearsal. What is now measured: a whole-of-Maryland download back to "
     "March 2025 names 7,576 record versions and gets through them at roughly 100 a minute, so "
     "eighteen months of catch-up is a little over an hour; EPA never once asked us to slow down "
     "across thousands of requests; and the biggest single cost in a run is not the record downloads "
     "but the extra call made per site to confirm which of its versions EPA calls current — half of "
     "one run's requests went on that. What is still to do: the two remaining measurements need a "
     "deliberate run rather than an opportunistic one — how far back the very first download must "
     "reach (G38), and how fast EPA will actually let us go (G21), which means carefully going faster "
     "than our own self-imposed limit and watching for a refusal. "
     "ONE OF THOSE TWO IS NOW DONE. On 2026-09-07 the very first full download was run deliberately, "
     "reaching all the way back to 1970, and it settled G38 outright — see that row. It also gave the "
     "size answer this step was created to find: a complete download of Maryland's entire RCRAInfo "
     "history is 43,048 record versions, 56,699 requests to EPA, and about 8 hours, with nothing lost "
     "and not one request refused. That is the number to plan the first production run around. "
     "ONLY G21 IS LEFT HERE, and it is now the smaller question: we know our own self-imposed limit "
     "of 2 requests a second is comfortably safe over 56,699 consecutive requests, which is a far "
     "stronger result than a rehearsal would have given. What we still do not know is how much "
     "FASTER EPA would allow, and that needs a run that deliberately exceeds our limit.",
     "§F2"),
    ("F3", "F — Prove, measure, deploy", "First deployment to UAT", BLOCKED,
     "MDE management (G17)",
     "Waiting on a decision, not on information: which SQL Servers hold the RCRAInfo and ETS "
     "databases in UAT and Production, and their edition and collation. Four server names in all. "
     "It cannot be chased, only waited on. Deployment is by script or package — a 2025 database "
     "cannot be restored onto 2022.",
     "§F3"),
    ("F4", "F — Prove, measure, deploy", "Production", NOT_STARTED,
     "F3, plus a production API key (G1)",
     "The production API ID and Key are a separate registration from pre-production and have not "
     "been requested — see 1.1. Worth starting that wait early.",
     "§F4"),
]

# ------------------------------------------------------- workstream-level summary

WORKSTREAMS = [
    ("Getting started", GOING, "Decisions and requests that had to come first. The three MDE and "
     "design items are closed. Two are not: the production API key is a separate registration and "
     "has not been requested (1.1), and the email putting two questions to EPA has not been sent "
     "(1.2). Neither holds up work today."),
    ("A — Foundation", DONE, "Repository, solution, the SQL Server 2022 guard, the development "
     "database, the two application logins, the description helper."),
    ("B — Schema", DONE, "The largest workstream. 50 tables, 1,011 described columns, and the "
     "24 mirrored EPA code lists."),
    ("DA — Data access", DONE, "22 stored procedures, all logged and all wrapped in error "
     "handling, plus the thin Entity Framework layer over them."),
    ("C — Credential bootstrap", DONE, "Passwords and the API pair encrypt themselves on the "
     "machine on first run, and are validated before they are trusted."),
    ("D — API client", DONE, "FINISHED. Sign-in is built and proved against EPA; the download loop "
     "is finished, including on-demand single-handler downloads and the fixes a missed schedule "
     "needed; the 377-field mapping is proved field by field; and the update-only run with its "
     "current-version reconciliation (D4) is now built and has been run against EPA, which corrected "
     "four sites' records and fixed the duplicate row the monitoring app would have shown. See D4."),
    ("E — Web monitoring app", BLOCKED, "Waiting on four MDE decisions about notifications and "
     "site authentication. Nothing technical is in the way."),
    ("F — Prove, measure, deploy", GOING, "F1 has earned its keep several times over — a real "
     "Maryland handler was downloaded from EPA and stored on 2026-09-06, then a second one with 16 "
     "versions of history, then 25 more at MDE's request with their complete histories (170 versions "
     "in all), and doing it for real found things no test on this end could: two faults, both fixed, "
     "and one missing step in the download that has since been built and run (D4). The sizing "
     "measurements the next step needs are taken. THE COMPARISON AGAINST EPA'S OWN WEBSITE HAS NOW "
     "BEEN DONE — MDE worked through all six checks on 2026-09-07 and it was the single most valuable "
     "hour spent on this project. It found a wrong rule in our software and fixed it; it proved 10,458 "
     "warnings harmless and showed the check that raises them is asking the wrong question; it proved "
     "one of the design's key precautions correct against EPA's real behaviour; and it corrected two "
     "numbers I had got wrong. None of the four was reachable from this end. THE READ-ONLY DATE CHECK "
     "HAS SINCE BEEN RUN TOO, on 2026-09-07, and it closed the last question that would have become "
     "unanswerable with time: EPA's dates on the detailed site record come back plain, with no time and "
     "no time-zone marker, matching the other feed. It also found a fault in itself on its first run — "
     "it claimed three date fields differed when its own printout showed them identical — which is "
     "precisely what a tool that prints EPA's raw answer is for. Both faults it exposed are fixed. "
     "WHAT IS LEFT OF F1 IS ONE SITTING WITH TWO SCREENS and no development work: comparing the "
     "individual fields of one messy site against EPA's page. F2 HAS NOW ALSO "
     "STARTED, not by plan but because MDE asked for two whole-of-"
     "Maryland catch-up downloads and those are exactly the scoped batch F2 was going to stage: "
     "eighteen months of Maryland changes is 7,576 record versions and takes a little over an hour, "
     "and EPA did not once ask us to slow down. Those two runs also refused to load two of EPA's code "
     "lists, correctly — neither could ever have fitted the space we had made for it, and one of them "
     "had never loaded a single row — which is a deliberate safety refusal working as designed and is "
     "now fixed. F3 is still waiting on where the UAT and Production databases live."),
]

# ---------------------------------------------------------------------- the key
#
# What management asked for: "so they can easily see what G1, G21, DA4, D2 mean."

KEY_ROWS = [
    ("Numbering", "G…", "A numbered GAP — an open question or an outside dependency. The "
     "full list, with who owns each one, is on the Gaps sheet."),
    ("Numbering", "AR…", "An ADDITIONAL REQUIREMENT that management added on top of the three "
     "original ones. AR1 to AR8; listed further down this sheet."),
    ("Numbering", "[R…]", "A REVISION marker in the planning documents — [R13] means "
     "“changed in revision 13”. It exists so a reader can see what a document said before, "
     "instead of finding it quietly rewritten. This workbook reflects revision 46."),
    ("Numbering", "§…", "A section of Phase1-Plan.md. The “Plan section” column on "
     "the Status sheet points at the reasoning behind each row."),

    ("Workstream", "Getting started", "Plan section 1 — the decisions and requests that had to "
     "happen before, or alongside, everything else."),
    ("Workstream", "A", "FOUNDATION. Repository, solution, the guard that keeps the code compatible "
     "with SQL Server 2022, the development database, the application logins."),
    ("Workstream", "B", "SCHEMA. Every table: the Handler data, EPA's code lists, and the run-tracking "
     "tables. The largest workstream."),
    ("Workstream", "DA", "DATA ACCESS. The stored procedures, and the thin Entity Framework layer "
     "that calls them. Management's direction: procedures work on whole sets, never one row at a "
     "time."),
    ("Workstream", "C", "CREDENTIAL BOOTSTRAP. How passwords and the API key get onto a machine and "
     "encrypt themselves there."),
    ("Workstream", "D", "API CLIENT. Talking to EPA — signing in, downloading, and mapping what "
     "comes back."),
    ("Workstream", "E", "WEB MONITORING APPLICATION. The site that shows run status and errors and "
     "notifies affected users."),
    ("Workstream", "F", "PROVE, MEASURE, DEPLOY. Running the thing against real data, measuring it, "
     "and deploying to UAT and Production."),

    ("Step", "1.1 – 1.5", "The five start-up items: request the API key, ask EPA the two design "
     "questions, settle MDE's scope questions, make four technical decisions, and make every "
     "database script safe to re-run."),
    ("Step", "A1 – A7", "The seven foundation steps, A7 being the written record of what the "
     "other six built."),
    ("Step", "B1 – B5", "Handler tables, code lists, run-tracking tables, column descriptions, "
     "and the written record."),
    ("Step", "DA0 – DA5", "DA0 is the logging substrate and comes first. DA1 builds two "
     "procedures for MDE to review. DA2 and DA3 are the contracts that review settled. DA4 is the "
     "other fourteen procedures. DA5 is the Entity Framework layer over all of them."),
    ("Step", "4.1 – 4.4", "Workstream C's four steps. They are numbered 4.x because Workstream C "
     "is section 4 of the plan."),
    ("Step", "D1", "Sign in to EPA and keep the short-lived token fresh."),
    ("Step", "D2", "FETCH ORCHESTRATION — the download loop. The big one, and now complete. Eleven "
     "parts, shown as D2.1 to D2.11."),
    ("Step", "D3", "Map EPA's 377 fields into the tables, and prove every one of them."),
    ("Step", "D4", "The update-only run, and reconciling which version of each record is current."),
    ("Step", "F1 – F4", "One handler end to end, then a measured scoped load, then UAT, then "
     "Production."),

    ("Requirement (AR)", "AR1", "A console application, run by Windows Task Scheduler, does the "
     "downloading."),
    ("Requirement (AR)", "AR2", "A web application monitors status and errors and notifies impacted "
     "users. Phase 1 is monitoring only."),
    ("Requirement (AR)", "AR3", "Each application gets its own SQL Server login with its own rights. "
     "Neither may change the database structure."),
    ("Requirement (AR)", "AR4", "The stored password is encrypted, and encrypts itself the first time "
     "the application runs."),
    ("Requirement (AR)", "AR5", "Status is tracked per handler ID, so a failure can be traced to the "
     "records it affected."),
    ("Requirement (AR)", "AR6", "Every table and column carries a description inside the database."),
    ("Requirement (AR)", "AR7", "Soft delete only. Records are marked deleted, never removed — "
     "there is no hard delete anywhere in this database."),
    ("Requirement (AR)", "AR8", "Every stored procedure logs what it did and wraps its work in error "
     "handling, so no failure goes unrecorded."),
]

# ------------------------------------------------------------------- the gaps
#
# (number, the question, status, who answers it, what it holds up)

CLOSED = "Closed"
OPEN = "Open"

GAPS = [
    ("G1", "The RCRAInfo API ID and API Key — which only a person can generate, in RCRAInfo, "
     "once per environment",
     "CLOSED 2026-09-06 — pre-production key issued and proved against EPA",
     "MDE RCRAInfo Administrator · EPA",
     "Was the one hard blocker. F1 is now unblocked. Production still needs its own key, for F4."),
    ("G2", "Which activity location do we load?", "Closed — MD only", "MDE",
     "Settled the schema. Contacts and mailing addresses may still be out of state, so no address "
     "field is restricted to Maryland."),
    ("G3", "Who runs the database scripts?", "Closed — the developer, by hand", "MDE",
     "Became a standing rule instead of a task: every script must survive being run twice."),
    ("G4", "How is the stored password encrypted?", "Closed — Windows machine-bound encryption",
     "Design", "Workstream C."),
    ("G5", "What happens when a credential fails validation?",
     "Closed — five behaviours specified, built, and tested", "Design", "Workstream C."),
    ("G6", "Does the same encryption cover the RCRAInfo API ID and Key?", "Closed — yes",
     "Design", "Workstream C. Both are equally secret."),
    ("G7", "How long do we keep the per-handler status rows?", OPEN, "MDE, with design",
     "Needed before the first production schedule. The execution log is the fastest-growing of the "
     "three, because it grows with how often procedures are called — including on days when "
     "nothing loads."),
    ("G8", "How are notifications sent — which mail relay, from which address?", OPEN, "MDE",
     "Workstream E."),
    ("G9", "Who are the “impacted users” who get notified?", OPEN, "MDE",
     "Workstream E."),
    ("G10", "What triggers a notification, and what stops thousands of them?", OPEN, "MDE",
     "Workstream E. A failed run can touch every handler in the state."),
    ("G11", "Where is the web app hosted, and how do users sign in to it?", OPEN, "MDE security",
     "Workstream E, and G31 follows from it."),
    ("G12", "Where do the 278 missing field descriptions come from?", OPEN, "EPA, via MDE",
     "Nothing. The placeholders are in place and say what they are; they are replaced when the "
     "data dictionary arrives."),
    ("G13", "Keep full history, or only the current version of each record?",
     "Closed — keep full version history", "MDE", "Shaped every table in Workstream B."),
    ("G14", "What exactly is in the “Handler data set”?",
     "Closed — alternate identifiers in, the episodic-events endpoint out", "MDE",
     "Costs one extra API call per handler across the whole population — which is why the download "
     "loop does not yet make it. The table and the call are both built; the procedure that writes "
     "the rows is not, so the loop deliberately skips that endpoint rather than fetch data it would "
     "throw away. One more procedure, scheduled with D3."),
    ("G15", "Do we mirror EPA's code lists?", "Closed — mirror all 24", "MDE",
     "Built as 24 tables, and the refresh that keeps them current is built too."),
    ("G16", "Do we need the Washington addendum?", "Closed — omitted", "MDE",
     "Reversible: the raw payload is stored, so it could be added later without re-downloading."),
    ("G17", "Which SQL Servers hold the RCRAInfo and ETS databases in UAT and Production — plus "
     "edition and collation? Four server names.",
     OPEN + " — undecided, not merely unasked",
     "MDE management",
     "F3, the first UAT deployment, and nothing else. Cannot be chased, only waited on."),
    ("G18", "Which machines do the console app and the web app run on?",
     "Closed — the same machine, in all three environments", "MDE",
     "Is why the file permission that keeps the web app away from the loader's credentials is "
     "necessary rather than theoretical."),
    ("G19", "Can the eventual servers reach EPA on port 443?",
     OPEN + " — proved from this workstation only", "MDE network",
     "F1 on the real host. The workstation's access was proved on 2026-09-06; a server's firewall "
     "is a different question."),
    ("G20", "Source control", "Closed — done in A1", "—", "Nothing."),
    ("G21", "Does EPA publish a request rate limit or a concurrency cap?",
     OPEN + " — undocumented anywhere", "EPA",
     "No longer holds anything up. The download loop now runs at a stated, deliberately cautious 2 "
     "requests per second and 2 at a time, and honours EPA's own “wait this long” instruction "
     "exactly. F2 measures what EPA tolerates, so EPA's answer becomes a confirmation. Keep the "
     "question open regardless: a published limit and an observed tolerance are different facts. "
     "FIRST REAL MEASUREMENT, 2026-09-06: two state-wide downloads ran back to back — thousands of "
     "requests each — and EPA never once asked us to slow down. The loop held about 1.8 requests per "
     "second, which is the ceiling we set doing its job rather than EPA pushing back, so what has "
     "been shown is that our cautious rate is SAFE, not what the real limit is. Finding the real "
     "limit means deliberately going faster, which F2 can do in a controlled way."),
    ("G22", "How big is the initial load?",
     OPEN + " — first real figures in hand; the whole-history figure still unmeasured",
     "Measured in F2",
     "Decides batch sizes and how long the first run takes. FIRST FIGURES, 2026-09-06. Asking EPA "
     "for every Maryland change since 2025-03-18 — about eighteen months — named 7,576 record "
     "versions, and downloading them runs at roughly 100 versions a minute, so eighteen months of "
     "catch-up takes a little over an hour. That is a usable planning number for the NIGHTLY job and "
     "for any catch-up after an outage. What it is NOT is the initial load: that reaches back to "
     "1980, and nobody has yet asked EPA how much is there. The honest state of this question is "
     "therefore that the recurring cost is now known and the one-off cost is not."),
    ("G23", "How does EPA signal that a record was deleted?", OPEN, "EPA",
     "Nothing any more — D4 was built without it. The download treats a “not found” answer as a "
     "deletion signal only when it asks for one specific version of one specific record, and never "
     "when it asks for a site's version list; a version list coming back empty is treated as a "
     "contradiction to investigate, not as permission to delete. That distinction was exercised for "
     "real: EPA has withdrawn one of its own practice sites and now answers “not found” for it, and "
     "the download correctly deleted nothing. EPA's answer would let us stop being cautious; it is no "
     "longer needed to be correct."),
    ("G24", "What are the valid sourceType values?", OPEN + " — four seen, two undocumented", "EPA",
     "Nothing. The column deliberately has no restriction, so a value EPA adds later cannot fail a "
     "load. The answer would feed a data-quality rule, not a constraint. AND THAT DECISION HAS NOW "
     "PAID FOR ITSELF: EPA's documentation gives two example values, and the real data downloaded so "
     "far contains four — the commonest one, accounting for about 94% of the records, is not "
     "documented at all. Had we restricted the column to what EPA documents, the first Maryland "
     "download would have rejected almost everything."),
    ("G25", "EPA's change feed carries a date, not a time — what does that cost?",
     "Closed — confirmed on both EPA feeds with EPA's own data", "Us — answered, nothing owed by EPA",
     "Nothing was ever blocked: each run re-requests the last day it processed and relies on repeat-safe "
     "merges, or it would miss same-day changes. On 2026-09-06 this was confirmed by looking at what "
     "EPA actually sends rather than at what our code accepted — the change feed's dates carry no "
     "time of day, so the day-at-a-time approach above is the right one. Worth doing because an "
     "earlier assumption about a different EPA date had already turned out wrong, and only reading "
     "the raw text caught it. CLOSED ON 2026-09-07: the same check was run on the detailed site record "
     "— the half that could not be done after the fact, since EPA's original text is not kept anywhere "
     "— and it came back the same way. Plain dates, no time, no time-zone marker, on all four date "
     "fields that site's record carried. So both EPA feeds agree, and the day-at-a-time design is now "
     "based on something seen twice rather than seen once and assumed. One small follow-up remains, on "
     "a site whose record contains the other seven date fields (permits, closure dates and the like) — "
     "they were simply absent from this site, not written oddly. See step F1."),
    ("G26", "How is the schema deployed?", "Closed — by script or package only", "Design",
     "A SQL Server 2025 database cannot be restored onto 2022, so backup-and-restore is not an "
     "option at all."),
    ("G27", "What does “use stored procedures” mean, exactly?",
     "Closed — whole sets at a time, never row by row", "MDE management", "Workstream DA."),
    ("G28", "How do we detect when a procedure's result shape drifts from the code?",
     "Closed — round-trip tests, built", "Design",
     "Nothing. They found five real defects on their first run."),
    ("G29", "Who stamps the audit columns — the application or the procedure?",
     "Closed — the procedures", "MDE management", "Workstream DA."),
    ("G30", "What is the database called?", "Closed — RCRAInfo", "MDE", "Nothing."),
    ("G31", "When the web app writes a row, whose name goes in the audit column?", OPEN,
     "MDE, with design", "Workstream E. Waits on G11, because it depends on how users sign in."),
    ("G32", "How are sets passed to a procedure — JSON or table parameters?",
     "Closed — JSON, every set", "MDE", "Nothing. The known hazard of that choice is guarded "
     "in five places, and the guards have already caught it twice."),
    ("G33", "How wide are the audit columns that hold a login name?",
     "Closed — at least 128 characters", "MDE",
     "Nothing. Narrower and the default value would itself fail the insert."),
    ("G34", "Does the developer get a second local SQL Server instance?", "Closed — no", "MDE",
     "Nothing, but it means an automatic check is the only thing preventing one database from "
     "reaching into the other."),
    ("G35", "Which run-tracking rows must survive a failed transaction, and how?",
     "Closed — built, and proved by deliberately failing a batch", "Design, with MDE",
     "Nothing. It was the only gap that ever blocked a workstream already under way."),
    ("G36", "A too-long value from EPA could be silently shortened on the way in",
     "Closed — detection built, and proved against two deliberate faults", "Design",
     "Nothing. The full payload is stored as well, so a shortened value is recoverable."),
    ("G37", "A read called inside a failed transaction cannot record its own error",
     OPEN + " — measured, documented, and out of reach of current callers",
     "MDE, with a DBA",
     "Nothing built today. It is the one documented exception to “every error is recorded”, "
     "and it only arises if a future caller wraps a screen read inside a write transaction."),
    ("G38", "How far back should the very first download reach?",
     CLOSED + " — the first download HAS NOW RUN, all the way back, and the answer is measured",
     "Nobody. Settled by the download itself on 2026-09-07",
     "Nothing. The setting has a value (1980, when hazardous-waste notification began) and the "
     "application works. What is open is whether that date is EARLY ENOUGH, and the risk only runs "
     "one way: too early costs some extra requests, while too late leaves records permanently "
     "missing AND LOOKING COMPLETE, because the download faithfully reports every date range it "
     "asked for and says nothing about years it was never asked about. Old records sometimes carry a "
     "placeholder date such as 1900, which would sit before the cutoff. F2 settles it for the price "
     "of one deliberately wide request: ask for everything before 1980 once and count what comes "
     "back. Nothing is waiting on this — F1 downloads a named handler and uses no date range. "
     "THIS STOPPED BEING THEORETICAL ON 2026-09-06. A download that asks EPA “what changed since "
     "date X” can only ever see records EPA touched after date X, and the state-wide runs turned up "
     "sites where we now hold some versions but not the one EPA calls current — because that version "
     "was last touched before the window we asked for. Those sites are in the database and would not "
     "appear on a screen that shows current records, which is the “missing AND looking complete” "
     "failure above, seen for real rather than argued about. It is self-correcting in the sense that "
     "one download reaching far enough back fixes it permanently, and that download has still never "
     "been run. The exact count is being remeasured once the catch-up now in progress finishes, "
     "since it moves while a run is in flight. "
     "SETTLED ON 2026-09-07, AND THIS IS THE GOOD NEWS ITEM OF THE REVISION. The download was set to "
     "reach back to 1970 — nine years before hazardous-waste notification even began — and then run. "
     "It took about 8 hours, made 56,699 requests to EPA without a single retry or rate-limit refusal, "
     "and lost nothing: 43,048 versions were accounted for and NONE failed. The result that matters to "
     "management is the visibility one. Before this download, 49 real Maryland sites were in our "
     "database but could not appear on any screen that shows current information, because the version "
     "EPA calls current had never been downloaded. That number is now ZERO, out of 16,793 sites. The "
     "database went from about 7,700 records covering 7,029 sites to 43,048 records covering 16,793 "
     "sites, and the screen the web application reads went from 7,228 rows to 17,159. "
     "AND THE WORRY BEHIND THIS QUESTION TURNED OUT NOT TO EXIST. The fear was that old records might "
     "carry a placeholder date such as 1900 and sit forever before any cutoff. Having now asked EPA "
     "for everything from 1970 onward, the oldest information Maryland has in RCRAInfo is dated "
     "2000-09-15, and there is nothing before it. So the cutoff setting can be any date on or before "
     "September 2000 and no record can hide behind it. For the record, the data is concentrated recently: "
     "about 3,900 records from the 2000s, 24,200 from the 2010s and 15,000 from the 2020s. "
     "ONE CAUTION FOR ANYONE READING SIMILAR MEASUREMENTS LATER. A cheap sampling check run before the "
     "download estimated the oldest data at 2002 or 2003. It was wrong by two years, because it only "
     "sampled the first quarter of each year and the real oldest record is from a September. Sampling "
     "is good enough to estimate how big a job is; it is not good enough to state where data begins."),
    ("G39", "Does EPA mark one current version per site, or one per record type within a site?",
     OPEN + " — nine times more evidence than last week, and still not an answer",
     "EPA, with our measurements in hand",
     "Nothing built wrong. A site can hold more than one KIND of record in EPA's system, and the step "
     "that fixes the current-version marker (D4) assumes each kind gets its own current version. Last "
     "week only three real Maryland sites had been downloaded; there are now about 28, and THREE OF "
     "THEM DO have a current version on two different kinds at once — which is exactly what our "
     "assumption allows and what the other reading would forbid. That makes the assumption look right. "
     "It is deliberately still recorded as open, because the one observation that would actually settle "
     "it has not appeared: a site with two current versions of the SAME kind that EPA says is correct. "
     "More data has made the answer feel safer without making it known, so nothing is being changed on "
     "the strength of it and EPA can simply tell us. Related: many sites have record types with NO "
     "version marked current at all, and EPA's own data says so, which means “no current version” is a "
     "legitimate state and not a fault to be repaired."),
]

GLOSSARY = [
    ("RCRAInfo", "EPA's national system for hazardous-waste handler data. The source of everything "
     "this project downloads."),
    ("Handler", "A regulated site or business in RCRAInfo. The main thing being downloaded."),
    ("Pre-production (preprod)", "EPA's practice copy of RCRAInfo. It has its own accounts and its "
     "own API keys, entirely separate from Production."),
    ("API ID and API Key", "The permanent credential a person generates in RCRAInfo. The Key is "
     "displayed once and cannot be read back — losing it means generating a new one."),
    ("Token", "The short-lived pass (about 20 minutes) the application gets by presenting its API "
     "Key. The application renews tokens by itself, forever; it can never renew its own API Key."),
    ("Console app / the loader", "The unattended program that does the downloading, started by "
     "Windows Task Scheduler."),
    ("Soft delete", "Marking a row as deleted instead of removing it. There is no hard delete "
     "anywhere in this database."),
    ("Stored procedure", "A named operation that lives inside the database. Management's direction "
     "is that the applications do everything through these."),
    ("Guardrail", "An automatic check that runs on every build and fails it. 15 of them, covering "
     "things a code review would have to remember — for example that no log message can carry a "
     "credential."),
    ("Offline test", "A test that needs nothing but the code — no database, no network. 972 of "
     "them, and they run in seconds."),
    ("Integration test", "A test that runs against the real development database. 506 of them; they "
     "are skipped when no database connection is configured."),
    ("Extended property", "The plain-English description stored inside the database against a table "
     "or column, so the meaning travels with the data."),
    ("Built vs. proved", "“Built” means written and tested here. “Proved” means it "
     "has actually spoken to EPA. The two diverged for the first time in this project while waiting "
     "for the API key — and on 2026-09-06 proving found a real defect that every offline test had "
     "agreed with."),
]


# ------------------------------------------------------------------ spreadsheet

def style_header(ws, row, ncols, fill=BLUE, colour="FFFFFF"):
    for c in range(1, ncols + 1):
        cell = ws.cell(row=row, column=c)
        cell.font = Font(name=FONT, size=10, bold=True, color=colour)
        cell.fill = PatternFill("solid", fgColor=fill)
        cell.alignment = Alignment(vertical="center", wrap_text=True)
        cell.border = Border(bottom=Side("thin", color=RULE))
    ws.row_dimensions[row].height = 30


def widths(ws, values):
    for i, w in enumerate(values, start=1):
        ws.column_dimensions[get_column_letter(i)].width = w


def title_block(ws, title, subtitle, span):
    ws["A1"] = title
    ws["A1"].font = Font(name=FONT, size=16, bold=True, color=INK)
    ws["A2"] = subtitle
    ws["A2"].font = Font(name=FONT, size=10, color=INK_SOFT)
    ws.merge_cells(start_row=1, start_column=1, end_row=1, end_column=span)
    ws.merge_cells(start_row=2, start_column=1, end_row=2, end_column=span)
    ws.row_dimensions[1].height = 24
    ws.row_dimensions[2].height = 16


def body(cell, bold=False, colour=INK, size=10, wrap=True, center=False):
    cell.font = Font(name=FONT, size=size, bold=bold, color=colour)
    cell.alignment = Alignment(
        vertical="top", wrap_text=wrap, horizontal="center" if center else "general")
    cell.border = Border(bottom=Side("hair", color=RULE))


def status_cell(cell, status):
    colour, fill = STATUS_STYLE[status]
    cell.value = status
    cell.font = Font(name=FONT, size=10, bold=True, color=colour)
    cell.fill = PatternFill("solid", fgColor=fill)
    cell.alignment = Alignment(vertical="top", horizontal="center", wrap_text=True)
    cell.border = Border(bottom=Side("hair", color=RULE))


def as_table(ws, name, first_row, last_row, ncols):
    """A real Excel table, so the filter arrows and banding come for free."""
    ref = f"A{first_row}:{get_column_letter(ncols)}{last_row}"
    table = Table(displayName=name, ref=ref)
    table.tableStyleInfo = TableStyleInfo(
        name="TableStyleLight1", showRowStripes=True, showColumnStripes=False)
    ws.add_table(table)


# ------------------------------------------------------------------- the sheets

def overview(wb):
    ws = wb.create_sheet("Overview")
    ws.sheet_view.showGridLines = False
    widths(ws, [26, 15, 96])

    title_block(ws, "RCRAInfo Handler Data — Phase 1 status at a glance",
                f"Status as of {AS_OF}. Generated from docs/phase1-status/make_xlsx.py — "
                "regenerate it, do not edit the workbook by hand.", 3)

    row = 4
    ws.cell(row=row, column=1, value="Where the project stands")
    ws.cell(row=row, column=1).font = Font(name=FONT, size=12, bold=True, color=BLUE)
    row += 1

    headline = [
        "THE FIRST COMPLETE DOWNLOAD OF MARYLAND'S RCRAInfo HISTORY RAN ON 2026-09-07, AND IT IS THE "
        "MOST IMPORTANT RESULT SO FAR. It was set to reach back to 1970 — nine years before "
        "hazardous-waste notification even began — and it finished in about 8 hours: 43,048 record "
        "versions, 56,699 requests to EPA, NOTHING lost, not one record failed, and not once did EPA "
        "ask us to slow down. The database went from roughly 7,700 records covering 7,029 sites to "
        "43,048 records covering 16,793 sites.",
        "WHAT THAT DOWNLOAD FIXED, IN PLAIN TERMS. Before it, 49 real Maryland sites were sitting in "
        "our database unable to appear on any screen showing current information, because the version "
        "EPA calls current had never been downloaded — the “missing but looking complete” fault this "
        "project has been warning about for weeks. That count is now ZERO out of 16,793 sites. It "
        "also retired the last open worry about how far back to reach: the oldest information Maryland "
        "has in RCRAInfo is dated September 2000, and there is nothing before it, so no record can "
        "hide behind the cutoff setting.",
        "A HUMAN LOOKING AT EPA'S WEBSITE FOUND A DEFECT NO TEST COULD HAVE. On 2026-09-07 MDE opened "
        "EPA's own page for one Maryland site and compared it against what our database had decided. Two "
        "things came out of it, and they point in opposite directions. THE GOOD NEWS: EPA's website shows "
        "TWO versions of that site as current, exactly as EPA's data feed told us — so when our software "
        "reports “EPA says two records are current and only one can be”, it is reporting EPA's records "
        "faithfully, not misreading them. That question had been open and is now closed. THE BAD NEWS: "
        "when our software had to choose between the two, it chose wrongly. It had been picking the "
        "highest version number, assuming EPA numbers versions in the order events happen. EPA numbers "
        "them in the order it RECEIVES them — so when the same form is submitted repeatedly, the number "
        "climbs without anything being newer. MDE identified the cause, which was the piece no analysis "
        "of the data could have supplied: THE DUPLICATES WERE MDE'S OWN, the same information sent to EPA "
        "electronically several times. Our software had picked the last of four identical copies of a "
        "January 2025 form over a genuine May 2026 update. THE RULE IS FIXED — the software now chooses "
        "by the date EPA received the form, the same date EPA's screen displays. One site out of 410 was "
        "affected and has been corrected; the other 409 were already right. THE POINT WORTH TAKING TO "
        "MANAGEMENT is not the one site. It is that this check had been written down with two possible "
        "answers and the real answer was neither — which is why the five remaining checks on that list "
        "are worth a person's time, and why the largest of them (10,458 sites with no current version) "
        "should be next.",
        "MDE THEN DID THE OTHER FIVE CHECKS THE SAME DAY, AND THE LARGEST WORRY ON THE PROJECT "
        "EVAPORATED. The 10,458 warnings in the overnight download were the biggest unexplained number "
        "we had. They are harmless. MDE supplied the piece of meaning our software was missing: when a "
        "record is marked as coming from the Implementer, it means MDE itself supplied the information "
        "rather than the site, and EPA properly treats that as the site's live record. So a form type "
        "with no current version is normal, not a gap. Checked across the whole database: EVERY SINGLE "
        "SITE HAS A CURRENT RECORD — not one is missing — and all 6,860 complaints turn out to be "
        "current under a different form type. Our software is asking the question per form type when it "
        "only makes sense per site, and that will be corrected. This matters more than “false alarm” "
        "suggests: 10,458 needless warnings in one download would train staff to ignore the same list "
        "that carries the real problems. TWO MORE RESULTS. A design precaution written months ago was "
        "proved correct against EPA's live behaviour: MDE fixed a disputed site directly on EPA's "
        "website, and EPA updated the timestamp on the version it promoted while leaving the demoted "
        "versions untouched — meaning our software only gets this right because it re-downloads a "
        "site's entire version list whenever any one version changes. Downloading just the changed "
        "version would have left the wrong records marked current forever. And a correction to our own "
        "work: I had reported records as MISSING for three sites and they were not missing — the "
        "database holds exactly what EPA shows, 21, 7 and 23. EPA numbers versions separately within "
        "each form type, so the same number legitimately appears several times (one site has four "
        "records all numbered 1, from 1980, 1990, 2008 and 2020), and EPA also skips numbers, which MDE "
        "could see on EPA's own screen. THE PATTERN ACROSS BOTH DAYS IS THE POINT: four findings, none "
        "of them reachable from our side of the connection. Two needed EPA's own meaning for its data, "
        "one needed EPA's live behaviour, and one needed a person to count rows on a page.",
        "WHAT WAS LEFT OF THE START-UP GROUP IS NOW ONE SITTING RATHER THAN A PIECE OF DEVELOPMENT. "
        "Two things remained after MDE's checks: comparing one deliberately messy site field by field "
        "against EPA's website, and reading EPA's dates once with our own eyes on the second of EPA's "
        "two feeds. THE TOOL FOR THE SECOND IS NOW BUILT AND TESTED. It is read-only, stores nothing, "
        "and prints EPA's dates back exactly as EPA wrote them — because our software currently "
        "reports only that it ACCEPTED them, which is a weaker statement than it sounds, and a date "
        "sent in an unexpected form once got through unnoticed earlier in this project and broke "
        "later. It cannot be postponed: no copy of EPA's original response is kept anywhere, so every "
        "further day of downloading adds records without adding evidence. Both remaining pieces are on "
        "the same site, so one sitting with the pre-production key finishes the group.",
        "AND BUILDING THAT TURNED UP A FAULT IN THE MONITORING SIDE, exactly where management will "
        "care about it: the list that shows what the downloads did could not show the single-site "
        "downloads AT ALL. It was not that they appeared empty — asking for them produced an error. "
        "The two downloads it could not show are the very ones that produced the findings above. The "
        "cause is mundane: the same list of permitted values is written down in two places, one was "
        "widened when the single-site download was added, and the other was not. What is worth taking "
        "from it is that the procedure's own notes, written two days earlier, PREDICTED this exact "
        "drift in this exact direction — and it happened anyway, which is why the automatic check that "
        "compares the two copies is worth its keep. Fixed, and waiting with the other database "
        "changes for the developer to apply by hand.",
        "A DESIGN DECISION MADE LAST REVISION PAID FOR ITSELF IMMEDIATELY. Until it was changed, the "
        "application refused to record how far it had got if any of EPA's 24 code lists was stale — "
        "and two of them come back empty from EPA every single time, for reasons only EPA can "
        "explain. Under the old rule this 8-hour download would have recorded nothing and repeated "
        "itself from scratch every night, indefinitely. Under the new rule it recorded its progress "
        "correctly while still reporting the stale code lists as a problem. The change was made "
        "before this run, not because of it, which is the only reason the 8 hours were not wasted.",
        "The one hard blocker is gone. On 2026-09-06 the pre-production API key was used to sign in "
        "to EPA for the first time, and EPA issued a token. Everything that was waiting on “we "
        "have never actually spoken to EPA” is now waiting only on the remaining code.",
        "The download loop was finished the same day — all eleven of its pieces, including the loop "
        "that runs them in order. The console application can now sign in, download, and record a "
        "run from end to end, download a single named handler on demand, and cope with a schedule "
        "that has not run for a week, a month, or ever. The 377-field mapping is done and, more "
        "importantly, now proved field by field. THE UPDATE-ONLY RUN IS NOW DONE TOO, so the whole "
        "download group is finished — and it has been run against EPA, not merely built.",
        "TWO DEFECTS WERE FOUND BY BUILDING THOSE, and both were the silent kind. The application "
        "could not have performed its very first download at all — the piece that says where to "
        "start had nothing to say on a brand-new database. And 167 of the 377 fields had never "
        "actually been checked by any test; a name EPA changed in any of them would have produced "
        "empty columns and a download reporting success. Both are fixed, and the second is now "
        "checked on every build. Neither was findable by reading anything; only by building it.",
        "Five of the eight groups of work are now finished outright: the foundation, the database "
        "schema, the data-access procedures, the credential handling, and — new this revision — the "
        "download itself. The start-up group is all "
        "but closed — what is left in it is two requests to EPA, not development work, and neither "
        "holds anything up today.",
        "THE FAULT REPORTED LAST WEEK IS FIXED. EPA marks one version of each site as the current "
        "one, but only ever tells us about the version that changed — so nothing was updating the "
        "previous version to say it had stopped being current, and one site (MDR000501742) had two "
        "versions both claiming to be current and would have been listed TWICE on the monitoring "
        "screen. The missing step has now been built and run against EPA: that site is down to one "
        "listing, and no site's record claims two current versions any more, apart from one of EPA's "
        "own practice sites that EPA has since withdrawn and will no longer answer questions about. "
        "Nothing was deleted in the process. Along the way FOUR sites needed correcting rather than "
        "one — the other three came to light only because 25 sites were downloaded with their full "
        "histories at MDE's request, 170 versions in all, which is worth stating because none of the "
        "three could have been found by looking at one version at a time.",
        "THE FIRST WHOLE-OF-MARYLAND DOWNLOADS HAVE NOW RUN, at MDE's request, and they turned the "
        "measuring step (F2) from something to be staged later into something already half done: "
        "eighteen months of Maryland changes is 7,576 record versions and takes a little over an "
        "hour, and EPA never once asked us to slow down. They also produced two refusals, both "
        "correct and both worth understanding, because a refusal is what this design does instead of "
        "loading half a list. EPA's list of industry codes has 1,701 entries — more than the safety "
        "ceiling allowed — and EPA's list of waste codes carries descriptions longer than the space "
        "the database had for them. Neither could ever have loaded; the industry-code list had never "
        "held a single row. Both are now fixed and both have loaded in full. The point is not the two "
        "fixes: it is that the alternative to refusing was a code list that looked complete and was "
        "not, and nothing downstream would have said so.",
        "ONE GAP GOT MORE URGENT, and it is worth management seeing it plainly. The download asks EPA "
        "“what changed since a date”, so it can only ever see records EPA touched after that date. "
        "The runs turned up Maryland sites where we hold some versions but not the one EPA calls "
        "current, because that version was last changed before the range we asked for — so the site "
        "is in the database and would not appear on a screen listing current records. This is the "
        "failure mode described under G38: missing data that looks complete. One download reaching "
        "far enough back (1980) fixes it permanently, and that download has still never been run.",
        "TWO DEFECTS WERE FOUND BY READING WHAT THOSE DOWNLOADS REPORTED, RATHER THAN BY ANY TEST, "
        "AND BOTH ARE NOW FIXED. The first is the more expensive. The download keeps a marker "
        "recording the date it has reached, so the next night starts from there; that marker was not "
        "allowed to move unless all 24 of EPA's code lists had refreshed. Two of those lists come "
        "back from EPA empty, refusing an empty list is correct, and asking again gives the same "
        "answer — so the marker could never move. Nearly two hours of correct work could not be "
        "recorded as done, and every night afterwards would have repeated it over a date range that "
        "grows by a day every day. The code lists still decide whether a run may report full success; "
        "they no longer decide whether it may remember where it got to. The transferable lesson is "
        "that two individually sensible checks combined into a rule nothing could ever pass. The "
        "second defect is a record-keeping one with the same shape as the fault found last week in the "
        "single-site download: 202 records left saying “still working on it” by downloads that ended "
        "early — and nothing in the database was able to close them off, because the procedure "
        "believed to do that job turned out only to handle whole downloads and not the individual "
        "sites inside them. Those 202 records are what the monitoring web application would show as "
        "work in progress. A new procedure closes them; it is written and checked but not yet applied, "
        "because the developer applies database changes by hand. Every one of the 202 belongs to a "
        "download that reported success, so the procedure also records that contradiction rather than "
        "quietly tidying it up.",
        "THE LAST QUESTION THAT HAD A DEADLINE IS ANSWERED, AND THE ANSWER IS THE GOOD ONE. Every "
        "other open question can be answered whenever we get to it; this one could not, because "
        "nothing keeps a copy of EPA's original response, so a date arriving in an unexpected form and "
        "not written down at that moment is unrecoverable afterwards — and every further day of "
        "downloading added records without adding evidence. On 2026-09-07 a read-only check was run "
        "against EPA and printed EPA's dates back exactly as EPA wrote them: plain dates, no time of "
        "day, no time-zone marker, on every date field the record carried. That is the same as the "
        "other EPA feed, so the two agree, and the design's central assumption — EPA tells us WHICH "
        "DAY a record changed but not what time, so each run must re-ask about the previous day — is "
        "now something seen on both feeds rather than seen on one and assumed for the other. Nothing "
        "needed changing as a result, which is the outcome worth having.",
        "THAT CHECK FOUND A FAULT IN ITSELF ON ITS FIRST RUN, and that is the third time in three "
        "weeks the same lesson has arrived, so it is worth management seeing it rather than only the "
        "fixes. The check announced that three date fields were written differently by EPA's two "
        "feeds. They were not — its own printout, three lines below, showed them identical. The cause "
        "was certain rather than unlucky: it was comparing the dates themselves instead of their "
        "FORMAT, and since one feed returns every version of a site and the other returns one version, "
        "the two lists can never match. It would have cried wolf on every site with more than one "
        "version. A second, quieter fault in the same code was the more dangerous: it kept only the "
        "first eight different dates per field, so a site whose ninth date was the odd one out would "
        "have been reported as entirely normal — the tool built to catch unexpected date formats could "
        "have hidden one, and this site came within a single date of demonstrating it. Both are fixed. "
        "NEITHER WAS FINDABLE BY TESTING, for the same reason as the earlier cases: every existing "
        "test compared dates that were deliberately identical, so not one could tell “different date” "
        "from “different format”, which is the only distinction that matters. The author wrote the "
        "tests, the code and the sentence it printed from the same wrong idea. Reading the real output "
        "is what broke it, and that is the argument for continuing to run real things against EPA "
        "rather than trusting a green test suite. THE FIX WAS THEN PROVED THE SAME WAY IT WAS FOUND — "
        "by running it against EPA again rather than only against our own tests, since trusting the "
        "tests is what produced the fault. It now reports correctly that the two feeds agree.",
        "THE NEXT STEP IS THE MONITORING WEB APPLICATION, which is waiting on four MDE decisions "
        "rather than on development work (see below). The one remaining part of F1 needs a person "
        "rather than more code: comparing a stored record field by field against EPA's own website. "
        "Standing correction, first made last week and repeated because a number was given to "
        "management before it was checked: the early report of “20 sites stored, 8 visible” was "
        "counting EPA's practice records. The lesson generalises — a count taken over EPA's practice "
        "system describes the practice data unless the practice records are separated out first.",
        "Two things are blocked on decisions outside the development team, and neither is technical. "
        "The monitoring web application needs four answers about notifications and sign-in (G8–G11). "
        "The first UAT deployment needs to know which servers hold the databases (G17).",
        "One request has not gone out yet: the production API key is a separate registration from "
        "pre-production, and that wait is worth starting early (see step 1.1).",
    ]
    for text in headline:
        ws.cell(row=row, column=1, value="•")
        ws.cell(row=row, column=1).alignment = Alignment(horizontal="right", vertical="top")
        ws.cell(row=row, column=1).font = Font(name=FONT, size=10, color=BLUE)
        c = ws.cell(row=row, column=2, value=text)
        body(c)
        c.border = Border()
        ws.merge_cells(start_row=row, start_column=2, end_row=row, end_column=3)
        ws.row_dimensions[row].height = 42
        row += 1

    row += 1
    ws.cell(row=row, column=1, value="What each status means")
    ws.cell(row=row, column=1).font = Font(name=FONT, size=12, bold=True, color=BLUE)
    row += 1
    for status, meaning in LEGEND:
        status_cell(ws.cell(row=row, column=2), status)
        c = ws.cell(row=row, column=3, value=meaning)
        body(c)
        ws.row_dimensions[row].height = 30
        row += 1

    row += 1
    ws.cell(row=row, column=1, value="The eight groups of work")
    ws.cell(row=row, column=1).font = Font(name=FONT, size=12, bold=True, color=BLUE)
    row += 1

    header = row
    for i, label in enumerate(["Group of work", "Status", "What it covers"], start=1):
        ws.cell(row=header, column=i, value=label)
    style_header(ws, header, 3)
    row += 1
    for name, status, covers in WORKSTREAMS:
        body(ws.cell(row=row, column=1, value=name), bold=True)
        status_cell(ws.cell(row=row, column=2), status)
        body(ws.cell(row=row, column=3, value=covers))
        ws.row_dimensions[row].height = 30
        row += 1

    row += 1
    ws.cell(row=row, column=1, value="Counting the steps")
    ws.cell(row=row, column=1).font = Font(name=FONT, size=12, bold=True, color=BLUE)
    row += 1

    leaves = [s for s in STEPS if s[0] not in ROLL_UP]
    counts = {status: sum(1 for s in leaves if s[3] == status) for status, _ in LEGEND}
    total = len(leaves)

    header = row
    for i, label in enumerate(["Status", "Steps", "Share of the plan"], start=1):
        ws.cell(row=header, column=i, value=label)
    style_header(ws, header, 3)
    row += 1
    for status, _ in LEGEND:
        status_cell(ws.cell(row=row, column=1), status)
        body(ws.cell(row=row, column=2, value=counts[status]), center=True)
        share = counts[status] / total
        c = ws.cell(row=row, column=3, value=share)
        body(c)
        c.number_format = "0%"
        row += 1
    body(ws.cell(row=row, column=1, value="All steps"), bold=True)
    body(ws.cell(row=row, column=2, value=total), bold=True, center=True)
    body(ws.cell(row=row, column=3,
                 value="Counted on the Status sheet. The D2 summary row is excluded, because its "
                       "eleven parts are counted individually."))
    row += 2

    ws.cell(row=row, column=1, value="How we know")
    ws.cell(row=row, column=1).font = Font(name=FONT, size=12, bold=True, color=BLUE)
    row += 1
    evidence = [
        "1,084 automated tests pass with no database and no network, in seconds.",
        "506 further tests run against the real development database, and are skipped when no "
        "connection is configured.",
        "15 automatic build checks enforce the rules a code review would otherwise have to "
        "remember — among them that no log message, anywhere, can carry a credential.",
        "The build produces zero warnings, because warnings are treated as errors.",
        "Every database script is run twice by an automatic test, and the second run must change "
        "nothing.",
        "And — new on 2026-09-06 — real Maryland handlers have been downloaded from EPA and stored "
        "with no person involved in between: 28 sites, 170 versions of history among 25 of them, "
        "across more than thirty live runs. Tests are not the same as doing it. Those runs found two "
        "faults and one missing step that all 1,478 tests had passed straight over, and then proved "
        "the repair by correcting four sites' records.",
    ]
    for text in evidence:
        ws.cell(row=row, column=1, value="•")
        ws.cell(row=row, column=1).alignment = Alignment(horizontal="right", vertical="top")
        ws.cell(row=row, column=1).font = Font(name=FONT, size=10, color=BLUE)
        c = ws.cell(row=row, column=2, value=text)
        body(c)
        c.border = Border()
        ws.merge_cells(start_row=row, start_column=2, end_row=row, end_column=3)
        ws.row_dimensions[row].height = 28
        row += 1

    row += 1
    c = ws.cell(row=row, column=1,
                value="Where to look next — Status for the row-by-row plan, Key for what an "
                      "identifier means, Gaps for every open question and who owns it, Glossary for "
                      "the vocabulary. The full reasoning behind any row is in Phase1-Plan.md at the "
                      "section named in the last column.")
    c.font = Font(name=FONT, size=10, italic=True, color=INK_SOFT)
    c.alignment = Alignment(vertical="top", wrap_text=True)
    ws.merge_cells(start_row=row, start_column=1, end_row=row, end_column=3)
    ws.row_dimensions[row].height = 32

    ws.freeze_panes = "A4"


def status_sheet(wb):
    ws = wb.create_sheet("Status")
    ws.sheet_view.showGridLines = False
    widths(ws, [9, 27, 54, 14, 26, 78, 13])

    title_block(ws, "Phase 1 — every step, one row each",
                f"Status as of {AS_OF}. Use the filter arrows on the Status or Group column. "
                "“Waiting on” names who or what a row needs; “—” means nothing "
                "outside the team.", 7)

    header = 4
    labels = ["Step", "Group of work", "What this step delivers", "Status", "Waiting on",
              "Where it stands", "Plan section"]
    for i, label in enumerate(labels, start=1):
        ws.cell(row=header, column=i, value=label)
    style_header(ws, header, len(labels))

    row = header + 1
    for step_id, group, delivers, status, waiting, notes, section in STEPS:
        roll_up = step_id in ROLL_UP
        body(ws.cell(row=row, column=1, value=step_id), bold=True)
        body(ws.cell(row=row, column=2, value=group))
        body(ws.cell(row=row, column=3, value=delivers), bold=roll_up)
        status_cell(ws.cell(row=row, column=4), status)
        body(ws.cell(row=row, column=5, value=waiting),
             colour=RED if status == BLOCKED else INK_SOFT)
        body(ws.cell(row=row, column=6, value=notes))
        body(ws.cell(row=row, column=7, value=section), colour=INK_SOFT, center=True)
        row += 1

    as_table(ws, "Phase1Steps", header, row - 1, len(labels))
    ws.freeze_panes = f"A{header + 1}"


def key_sheet(wb):
    ws = wb.create_sheet("Key")
    ws.sheet_view.showGridLines = False
    widths(ws, [20, 18, 100])

    title_block(ws, "Key — what the codes mean",
                "Filter the Kind column to see one family at a time. The numbered gaps (G1, G17, "
                "G21 …) have their own sheet, because each one has an owner and a status.", 3)

    header = 4
    labels = ["Kind", "Code", "What it means"]
    for i, label in enumerate(labels, start=1):
        ws.cell(row=header, column=i, value=label)
    style_header(ws, header, len(labels))

    row = header + 1
    for kind, code, meaning in KEY_ROWS:
        body(ws.cell(row=row, column=1, value=kind), colour=INK_SOFT)
        body(ws.cell(row=row, column=2, value=code), bold=True)
        body(ws.cell(row=row, column=3, value=meaning))
        row += 1

    # Kind first so the filter is useful, Code second so it reads left to right.
    as_table(ws, "Phase1Key", header, row - 1, len(labels))
    ws.freeze_panes = f"A{header + 1}"


def gaps_sheet(wb):
    ws = wb.create_sheet("Gaps")
    ws.sheet_view.showGridLines = False
    widths(ws, [8, 56, 40, 26, 62])

    # Counted rather than typed: a subtitle that says "22 of 37" and disagrees with the rows
    # below it is worse than no subtitle at all.
    still_open = sum(1 for g in GAPS if g[2].startswith(OPEN))

    title_block(ws, "Gaps — the numbered open questions, and who answers them",
                f"Status as of {AS_OF}. A “gap” is a question or an outside dependency, "
                "numbered so the plan can refer to it in a couple of characters. "
                f"{len(GAPS) - still_open} of the {len(GAPS)} are closed; the {still_open} that are "
                "open are what the plan is waiting on. Only two pieces of work are actually held up "
                "by them: the monitoring web application (G8–G11) and the first UAT deployment "
                "(G17).", 5)

    header = 4
    labels = ["Gap", "The question", "Status", "Who answers it", "What it holds up"]
    for i, label in enumerate(labels, start=1):
        ws.cell(row=header, column=i, value=label)
    style_header(ws, header, len(labels))

    row = header + 1
    for number, question, status, owner, holds in GAPS:
        is_open = status.startswith(OPEN)
        body(ws.cell(row=row, column=1, value=number), bold=True)
        body(ws.cell(row=row, column=2, value=question))

        cell = ws.cell(row=row, column=3, value=status)
        colour, fill = (AMBER, AMBER_L) if is_open else (GREEN, GREEN_L)
        cell.font = Font(name=FONT, size=10, bold=is_open, color=colour)
        cell.fill = PatternFill("solid", fgColor=fill)
        cell.alignment = Alignment(vertical="top", wrap_text=True)
        cell.border = Border(bottom=Side("hair", color=RULE))

        body(ws.cell(row=row, column=4, value=owner), colour=INK_SOFT)
        body(ws.cell(row=row, column=5, value=holds))
        row += 1

    as_table(ws, "Phase1Gaps", header, row - 1, len(labels))
    ws.freeze_panes = f"A{header + 1}"


def glossary_sheet(wb):
    ws = wb.create_sheet("Glossary")
    ws.sheet_view.showGridLines = False
    widths(ws, [28, 110])

    title_block(ws, "Glossary — the words the other sheets use",
                "Kept deliberately short. Anything not here is explained in "
                "docs/api-keys/RCRAInfo-API-Keys-Explained.docx.", 2)

    header = 4
    for i, label in enumerate(["Term", "What it means"], start=1):
        ws.cell(row=header, column=i, value=label)
    style_header(ws, header, 2)

    row = header + 1
    for term, meaning in GLOSSARY:
        body(ws.cell(row=row, column=1, value=term), bold=True)
        body(ws.cell(row=row, column=2, value=meaning))
        row += 1

    as_table(ws, "Phase1Glossary", header, row - 1, 2)
    ws.freeze_panes = f"A{header + 1}"


def build():
    wb = Workbook()
    wb.remove(wb.active)

    overview(wb)
    status_sheet(wb)
    key_sheet(wb)
    gaps_sheet(wb)
    glossary_sheet(wb)

    wb.active = 0
    for ws in wb.worksheets:
        ws.print_options.horizontalCentered = True
        ws.page_setup.orientation = "landscape"
        ws.page_setup.fitToWidth = 1
        ws.sheet_properties.pageSetUpPr.fitToPage = True

    wb.save(OUTFILE)
    print("wrote", OUTFILE)

    leaves = [s for s in STEPS if s[0] not in ROLL_UP]
    for status, _ in LEGEND:
        print(f"  {status:<12} {sum(1 for s in leaves if s[3] == status):>3}")
    print(f"  {'all steps':<12} {len(leaves):>3}")
    print(f"  {'gaps open':<12} {sum(1 for g in GAPS if g[2].startswith(OPEN)):>3}"
          f" of {len(GAPS)}")


if __name__ == "__main__":
    build()
