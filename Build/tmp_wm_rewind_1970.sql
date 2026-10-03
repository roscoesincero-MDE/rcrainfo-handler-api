/**********************************************************************************************************************
Script:       build/tmp_wm_rewind_1970.sql   (throwaway operator script -- not part of the deployment set)
Author:       rsincero
CreateDate:   2026-09-06
========================================================================================================================
Purpose:

REWIND THE HandlerSource BOOKMARK TO 1970-06-20 SO THE NEXT RUN IS THE INITIAL LOAD FROM THE PRE-RCRA FLOOR. This is
G38: the load from the floor has never run, and 148 lineages still hold no current version because the version carrying
EPA's current flag was last changed before any range ever asked for. An incremental window can never reach them.

WHY A SCRIPT AND NOT A SWITCH. The loader has NO --since and NO --from, deliberately. A scheduled run's window comes
only from config.LoadWatermark, and config.uspSetLoadWatermark is the only object permitted to write WatermarkDate. So
"retrieve handlers with last download date 1970-06-20" is expressed by moving the bookmark and then running the
ordinary scheduled load. There is no second path, and that is the point -- a --from switch would let an operator claim
coverage the bookmark never recorded.

@AllowRewind = 1 AND @Notes ARE BOTH MANDATORY HERE, and 513 enforces both. A backwards move is safe for the DATA (it
re-fetches) but not safe to do by accident, because an accidental rewind re-walks every intervening day on every run
while looking perfectly normal. @Notes is the attribution: every change to WatermarkDate must be traceable to a run
through @LoadRunId or to a person through @Notes.

@LoadRunId IS DELIBERATELY NOT PASSED, AND MUST NOT BE. 513 accepts @LoadRunId only for a run whose status is 'Running'
or 'Succeeded'. An operator move made after the fact has no such run to point at, and passing a closed run's id is
refused outright -- that is exactly how the previous version of this file (build/tmp_wm_set.sql, now deleted) threw and
wrote nothing: it named run 2622, which closed 'PartiallySucceeded'. Omitting it sets LastAdvancedByLoadRunId to NULL,
which is the honest answer: not a run. @Notes says who.

WHAT THE NEXT RUN WILL COST, MEASURED RATHER THAN GUESSED (--probe-summaries, read-only, 25 windows sampled):

    1970 / 1975 / 1980 / 1985 / 1995 / 1998 / 2000 / 2002 Q1  ..  0 summaries each (3-byte empty array)
    2003 Q1 .. 93     2004 Q1 .. 152    2005 Q1 .. 105    2008 Q1 .. 106    2012 Q1 .. 46    2015 Q1 .. 2
    2017 Q4 .. 405    2018 Q1 .. 9,936  2018 Q2 .. 304    2018 Q3 .. 549    2018 Q4 .. 580
    2019 Q1 .. 393    2020 Q1 .. 243    2021 Q1 .. 95     2022 Q1 .. 329    2023 Q1 .. 329    2024 Q1 .. 245

Three things follow, and they are the reason this rewind is affordable at all:

  1. THE DATA FLOOR IS BETWEEN 2002-04 AND 2003-01, not 1970. Every sampled quarter from 1970 through 2002 returns
     200-with-an-empty-array. So ~1,700 of the ~2,930 seven-day windows in this range are guaranteed empty and cost one
     fast request each -- about 15 minutes of the walk, and zero fetches. 1970-06-20 is therefore a SAFE floor rather
     than an expensive one: it buys "provably nothing earlier" for the price of empty requests.
  2. 2018 Q1 IS A ONE-OFF BULK EVENT, not a trend. Its own neighbours are 304-580; it is 9,936. Something re-wrote most
     of the state's handlers at once. The 91-day probe returned all 9,936 in a single 2.4 MB unpaged response without
     complaint, so the 7-day window the walk actually uses is safe by construction.
  3. THE FETCH IS THE COST, NOT THE WALK. Order 30,000-40,000 versions will be enumerated. Everything already in the
     mirror is SKIPPED, not re-fetched -- a skip means an earlier run already succeeded on that exact version -- so the
     real work is the difference. At run 2622's measured ~107 versions/minute this is several hours, not minutes.

SO PLAN FOR A LONG RUN, AND KNOW WHAT AN INTERRUPTION COSTS. Version-level progress is durable: logs.HandlerLoadStatus
rows that say 'Succeeded' make a resuming run skip them, so a killed run loses no fetched data. It banks NO watermark
movement, though -- one unaccounted-for version makes everyVersionAccountedFor false and the bookmark stays at
1970-06-20 until a run finishes clean. That is the intended conservatism, not a bug: the bookmark must never move past
a version nobody fetched. If a run ends with a handful of 'Failed' rows, re-run it -- it re-fetches only those.

SAFE TO RUN TWICE. 513 CONVERGES: a call that would change nothing changes nothing, so a second run finds the bookmark
already at 1970-06-20, writes no row, and does not move auditModifiedDateUtc. The AFTER section reads identically.

Read-back sections bracket the write so the before and after are both visible in the output.
**********************************************************************************************************************/

SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

PRINT N'--- BEFORE -------------------------------------------------------------';
GO

SELECT FeedName
     , ActivityLocation
     , WatermarkDate
     , OverlapDays
     , IsEnabled
     , LastAdvancedByLoadRunId
     , LastAdvancedDateUtc
     , Notes
  FROM config.LoadWatermark
 WHERE IsDeleted = 0
   AND FeedName  = N'HandlerSource';
GO

PRINT N'--- REWIND -------------------------------------------------------------';
GO

----------------------------------------------------------------------------------------------------
-- The note is built in a variable rather than passed inline: an EXEC argument in T-SQL must be a
-- constant or a variable, NEVER an expression, so a concatenation in the argument list is a syntax
-- error ("Incorrect syntax near '+'"). DECLARE and EXEC therefore share this one batch.
--
-- No guard precedes this call, unlike the script it replaces. That one was banking coverage and had
-- to prove the coverage was real first. A rewind proves nothing and claims nothing -- it only asks
-- for MORE work -- so the only thing worth refusing is a rewind done by accident, and @AllowRewind
-- plus @Notes is precisely 513's guard for that.
----------------------------------------------------------------------------------------------------
DECLARE @Notes NVARCHAR (4000) =
      N'Operator rewind 2026-09-06 to 1970-06-20: setting up the initial load from the pre-RCRA floor (G38). '
    + N'The bookmark had sat at 2025-03-18, so no run had ever asked for anything earlier, and 148 lineages hold '
    + N'no current version because the version carrying EPA''s current flag was last changed before any range ever '
    + N'covered it. --probe-summaries measured every quarter sampled from 1970 through 2002 Q1 as empty, so the real '
    + N'data floor is between 2002-04 and 2003-01 and this floor costs empty requests rather than fetches. Expect '
    + N'order 30,000-40,000 versions enumerated and several hours; already-loaded versions are skipped, not re-fetched. '
    + N'The bookmark stays here until one run accounts for every version it enumerated.';

EXEC config.uspSetLoadWatermark
      @FeedName         = N'HandlerSource'
    , @ActivityLocation = N'MD'
    , @WatermarkDate    = '1970-06-20'
    , @AllowRewind      = 1
    , @Notes            = @Notes;
GO

PRINT N'--- AFTER --------------------------------------------------------------';
GO

----------------------------------------------------------------------------------------------------
-- LastAdvancedByLoadRunId is expected to read NULL here, and that is correct rather than missing:
-- an operator's change sets it to NULL instead of keeping the previous run's id, because a NULL
-- honestly says "not a run" and Notes says who. LastAdvancedDateUtc is stamped only when the date
-- actually moved, so on a second run of this script it keeps the first run's timestamp.
----------------------------------------------------------------------------------------------------
SELECT FeedName
     , ActivityLocation
     , WatermarkDate
     , OverlapDays
     , IsEnabled
     , LastAdvancedByLoadRunId
     , LastAdvancedDateUtc
     , auditModifiedBy
     , auditModifiedDateUtc
     , Notes
  FROM config.LoadWatermark
 WHERE IsDeleted = 0
   AND FeedName  = N'HandlerSource';
GO

----------------------------------------------------------------------------------------------------
-- What the loader will resolve from that bookmark, straight from the procedure the loader itself
-- calls. RecommendedRunMode / RecommendedFromDate / RecommendedToDate are the run's actual window.
-- RecommendedFromDate should read 1970-06-20 minus OverlapDays -- the overlap is applied to a rewind
-- exactly as it is to an ordinary advance, so a few extra empty days is all that costs.
----------------------------------------------------------------------------------------------------
PRINT N'--- WHAT THE NEXT RUN WILL ASK FOR -------------------------------------';
GO

EXEC config.uspGetLoadWatermark
      @FeedName         = N'HandlerSource'
    , @ActivityLocation = N'MD';
GO
