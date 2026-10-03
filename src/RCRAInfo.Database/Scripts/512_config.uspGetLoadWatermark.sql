-- SET XACT_ABORT ON sits ABOVE the header block deliberately, and moving it back below would restore a
-- real defect. The GO on the next line ends the batch, and sys.sql_modules stores only the batch that
-- contains CREATE -- so a header placed AFTER this GO is invisible to anyone reading the procedure out
-- of the database through sp_helptext, OBJECT_DEFINITION, or SSMS "Script as CREATE", which is where a
-- maintainer actually reads it. Every view in this database already carried its header inside the
-- definition because nothing separates the two; no procedure did until this was corrected.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   config.uspGetLoadWatermark
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Returns the watermark row for one feed and activity location, together with the run parameters it implies: which mode the
next run should be, and which date window it should ask EPA for. It is the loader's first read of the night, before it
opens a run.

The stored columns are here because the loader needs them; the computed ones are here because the day-granular overlap
arithmetic (G25) should exist in exactly one place, and this is it. A loader that did the subtraction itself would be a
second authority on a rule whose failure mode is a silent gap in a regulatory mirror.

========================================================================================================================
Requirements and Key Dependencies:

config.LoadWatermark, seeded by 340_config.LoadWatermark.sql with ('HandlerSource', 'MD').

logs.uspRecordExecutionError -- called from the CATCH block only. This procedure does NOT call
logs.uspStartExecutionLogging; see ERROR-ONLY INSTRUMENTATION below.

EXECUTE is granted to RCRAInfoLoaderRole only.

========================================================================================================================
Notes:

ERROR-ONLY INSTRUMENTATION: NO LOG ROW ON THE HAPPY PATH, AND AN ERROR ROW ALWAYS. This is the shape MDE settled for a
read, and it is two decisions rather than one.

The first is DA1 review decision 2 (2026-09-05): a procedure that WRITES opens and closes a logs.ExecutionLog row, and a
read does not, because a monitoring page that refreshes is the highest-frequency caller in the system and instrumenting
every read turns logs.ExecutionLog into a record of people looking at things. A DA1 probe defect demonstrated the volume
by accident -- about 70,000 rows in one afternoon.

The second overrides an earlier reading of the first. MDE's requirement, 2026-09-05: **any error must be recorded**,
including in a procedure that only reads. The case is not hypothetical -- in another MDE application a procedure whose
body was a single SELECT called a scalar user-defined function inside that SELECT; the function errored; nothing wrote a
row anywhere, because "it only reads" had been taken to mean "it cannot fail in a way worth recording". A read has no
INSERT of its own, but everything it calls can fail: a UDF, a view over a view, a computed column, a conversion, a
deadlock, a permission it turns out not to hold. So the CATCH block below is not optional and neither is what it calls.

An earlier revision of this file omitted the TRY/CATCH on the argument that a CATCH holding only ;THROW; is
behaviourally identical to no CATCH. That argument was correct about a CATCH holding only ;THROW; and wrong about what
belongs in the CATCH. **This one records before it rethrows.**

WHAT THE ERROR ROW LOOKS LIKE, AND WHY IT IS AN ORPHAN. @ExecutionLogId is NULL here, always, because no start row was
ever opened. logs.uspRecordExecutionErrorUpdate's MERGE handles that in its NOT MATCHED branch: it INSERTs a row and
stamps a ContextMessage saying that execution logging never started for the call, so StartDateUtc is the time the row was
written and ElapsedMilliseconds is unknown. For an instrumented procedure that state means something went wrong -- a
start row was rolled back and not re-created. **Here it is correct and expected**, which is why this procedure passes its
own @ContextMessage saying so; the MERGE appends it. Without that line, every error from a read would look like a
second, separate defect in the logging chain.

THE COST OF NOT OPENING A START ROW IS ACCEPTED, NOT OVERLOOKED. A successful call leaves no trace, so
logs.ExecutionLog cannot answer "how often is this read called". That is recoverable for this particular procedure --
every call is followed by a logs.uspStartLoadRun that IS logged, and a run that never started is a loader that never got
this far -- and it is the whole point for the paged reads, where the alternative is one row per grid refresh.

ONE CASE WHERE THE RECORDING CAN STILL BE LOST, STATED RATHER THAN GLOSSED. This procedure opens no transaction. If a
CALLER has one open and an error here dooms it (XACT_STATE () = -1), the INSERT inside logs.uspRecordExecutionError
cannot write, and that procedure's outer CATCH swallows the failure by design so that a logging failure never replaces
the error being reported. The error still reaches the caller; the row does not get written. Rolling back the caller's
transaction from here to make the write possible would be worse -- it is not this procedure's transaction to end. In
practice EF Core calls these reads without an ambient transaction, so the common path records.

NO TRANSACTION OF ITS OWN, WHICH IS STILL DELIBERATE. Nothing here writes, and a single SELECT under READ COMMITTED gains
nothing from being wrapped. The AR8 template's transaction is explicitly "for template fidelity, not for correctness".

A MISSING CONFIGURATION ROW IS AN ERROR, NOT AN EMPTY RESULT SET. Zero rows would leave the loader inventing a decision,
and both available inventions are wrong: treating "no configuration" as a full load means an unconfigured typo in a feed
name triggers a complete re-fetch of everything, and treating it as "nothing to do" means a scheduled load silently stops
happening and reports success. So it throws, naming the feed and the location it looked for.

WatermarkDate NULL MEANS "NEVER SUCCESSFULLY LOADED", which is why RecommendedRunMode comes back 'Full' in that case.
That is the seeded state, so a fresh database asks for everything on its first run without anyone having to remember to
say so.

RecommendedRunMode IS NULL WHEN THE FEED IS PAUSED. IsEnabled = 0 is an operational pause -- an EPA outage, a
deliberate hold -- and it is distinct from IsDeleted, which retires the feed. A NULL mode is the answer a loader cannot
accidentally act on: logs.uspStartLoadRun rejects a NULL @RunMode by name. IsEnabled is returned as well, so a caller
that wants to say why it is not running has the reason to hand.

RecommendedFromDate REACHES BACK OverlapDays, AND THE RE-FETCH IS THE POINT. EPA's incremental parameters are
day-granular, so a run that finishes at 14:00 and records "loaded through today" would never see a record EPA updates at
16:00 the same day. Re-fetching a day costs a request and finds the payload unchanged; missing an update leaves a gap
that no later run looks at again.

RecommendedToDate IS THE UTC DATE. EPA's data is stamped in a US time zone, so on part of each day the UTC date is one
day ahead of the Eastern one. Asking through a date that has not started in Eastern time is harmless -- there are no
records in it yet -- and it keeps this procedure agreeing with config.uspSetLoadWatermark, which uses the same UTC
ceiling when it refuses a future watermark.

IT RETURNS AT MOST ONE ROW, and the filtered unique index UX_config_LoadWatermark_Natural on (FeedName,
ActivityLocation) WHERE IsDeleted = 0 is what guarantees it. No TOP (1) is needed and none is used: a TOP would hide a
duplicate rather than let it fail.

========================================================================================================================
Example Usage and Performance:

-- What the loader asks before every run.
EXEC config.uspGetLoadWatermark;

-- Explicitly, which is what the loader actually sends.
EXEC config.uspGetLoadWatermark @FeedName = N'HandlerSource', @ActivityLocation = N'MD';

-- The error row a failure leaves, read as the developer:
SELECT ProcedureName, ErrorNumber, ErrorMessage, ContextMessage
  FROM logs.ExecutionLog
 WHERE ProcedureName = N'[config].[uspGetLoadWatermark]'
 ORDER BY ExecutionLogId DESC;

Two seeks on UX_config_LoadWatermark_Natural against a table with one row: the existence check, then the projection. The
existence check is separate so that a missing configuration row raises an error BEFORE any result set is opened -- a
procedure that throws after emitting rows leaves EF Core holding a partial reader and an exception. Note that the CATCH
cannot undo rows already sent; it can only make sure the failure is recorded, which is the point of it.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, and the first read to carry error-only
											instrumentation under the policy MDE settled at the DA1 review.
2026-09-05	rsincero						Added the TRY/CATCH and the logs.uspRecordExecutionError call, on MDE's
											review finding: a read-only procedure whose SELECT calls a UDF can fail
											without recording anything, and had, in another MDE application. The start
											row is still deliberately not opened. Also moved SET XACT_ABORT ON above
											the header so the header reaches sys.sql_modules.
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE config.uspGetLoadWatermark
      @FeedName         NVARCHAR (50) = N'HandlerSource'
    , @ActivityLocation NVARCHAR (2)  = N'MD'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- -------------------------------------------------------------------------------------------------
    -- AR8 error-only instrumentation. Half the block on purpose, and it is the CATCH half: no start
    -- row and no completion UPDATE, because this only reads; a CATCH that RECORDS, because only
    -- reading is not the same as not being able to fail. See the header.
    -- -------------------------------------------------------------------------------------------------
    -- Not a fallback for odd cases: this literal is what the loader login actually logs, because
    -- metadata visibility is denied to it by script 050 -- OBJECT_NAME (@@PROCID) returns NULL for a
    -- principal denied it. Keep the literal in step with the procedure name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[config].[uspGetLoadWatermark]')
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    DECLARE @LoadWatermarkId INT             = NULL
          , @TodayUtc        DATE            = CAST (SYSUTCDATETIME () AS DATE)
          , @Failure         NVARCHAR (2048) = NULL;

    -- Identifiers only. From MDE's own template: do NOT include parameters such as passwords and
    -- Personally Identifiable Information.
    SET @KeyParameters = CONCAT (N'FeedName=', COALESCE (@FeedName, N'(null)')
                               , N', ActivityLocation=', COALESCE (@ActivityLocation, N'(null)'));

    -- Explains the orphan row before anyone has to wonder about it. logs.uspRecordExecutionErrorUpdate
    -- appends this to the text it writes for a row with no ExecutionLogId, which otherwise reads as a
    -- defect in the logging chain rather than as this procedure working exactly as designed.
    SET @ContextMessage = N'Error-only instrumentation (AR8, DA1 review decision 2): this read does not '
                        + N'open a logs.ExecutionLog row on the successful path, so an orphan error row '
                        + N'is expected here and is not a sign that a start row was lost.';

    BEGIN TRY

        -- ------------------------------------------------------------------------------------------
        -- 1. Is there a configuration row at all? Answered before the projection opens a result set,
        --    so a missing feed raises an error the caller can read rather than an exception arriving
        --    mid-stream.
        -- ------------------------------------------------------------------------------------------
        SELECT @LoadWatermarkId = w.LoadWatermarkId
          FROM config.LoadWatermark AS w
         WHERE w.FeedName         = @FeedName
           AND w.ActivityLocation = @ActivityLocation
           AND w.IsDeleted        = 0;

        IF @LoadWatermarkId IS NULL
        BEGIN
            SET @Failure = CONCAT (N'There is no active watermark for feed '
                                 , COALESCE (N'''' + @FeedName + N'''', N'NULL')
                                 , N' and activity location '
                                 , COALESCE (N'''' + @ActivityLocation + N'''', N'NULL')
                                 , N'. 340_config.LoadWatermark.sql seeds ''HandlerSource'' / ''MD''; anything '
                                 , N'else has to be inserted deliberately. This is an error rather than an '
                                 , N'empty result set because both of the loader''s available guesses are '
                                 , N'wrong: reading "no configuration" as a full load makes a typo''d feed '
                                 , N'name re-fetch everything, and reading it as "nothing to do" makes a '
                                 , N'scheduled load stop happening and report success. If the row exists but '
                                 , N'is soft-deleted, the feed has been retired -- use IsEnabled = 0 to pause '
                                 , N'one instead.');
            ;THROW 50000, @Failure, 1;
        END;

        -- ------------------------------------------------------------------------------------------
        -- 2. The row, and the run parameters it implies. One row by construction: the natural key is a
        --    filtered unique index, so no TOP (1) is needed -- and a TOP would hide a duplicate rather
        --    than let it fail.
        -- ------------------------------------------------------------------------------------------
        SELECT w.LoadWatermarkId
             , w.FeedName
             , w.ActivityLocation
             -- The date the last successful run loaded through, inclusive. NULL means never loaded. This
             -- is the value logs.uspStartLoadRun takes as @WatermarkBeforeDate.
             , w.WatermarkDate
             , w.OverlapDays
             , w.IsEnabled
             , w.LastAdvancedByLoadRunId
             , w.LastAdvancedDateUtc
             , w.Notes

             -- NULL when the feed is paused, and that is the safe answer: logs.uspStartLoadRun rejects a
             -- NULL @RunMode by name, so a loader that ignores IsEnabled still cannot start a paused feed.
             , CASE WHEN w.IsEnabled     = 0    THEN NULL
                    WHEN w.WatermarkDate IS NULL THEN N'Full'
                    ELSE N'Incremental'
               END AS RecommendedRunMode

             -- NULL on a full load, which is how the loader knows to ask for everything rather than for a
             -- window. On an incremental it reaches OverlapDays back from the watermark: EPA's incremental
             -- parameters are day-granular, so without that overlap a record updated later on the day a
             -- run finished would never be seen again.
             , CASE WHEN w.IsEnabled     = 0    THEN NULL
                    WHEN w.WatermarkDate IS NULL THEN NULL
                    ELSE DATEADD (DAY, -w.OverlapDays, w.WatermarkDate)
               END AS RecommendedFromDate

             -- The UTC date. On part of each day that is one day ahead of EPA's Eastern date, which is
             -- harmless -- there are no records in a day that has not started -- and it matches the
             -- ceiling config.uspSetLoadWatermark enforces when it refuses a future watermark.
             , CASE WHEN w.IsEnabled = 0 THEN NULL ELSE @TodayUtc END AS RecommendedToDate

             -- What logs.uspStartLoadRun records as OverlapDaysApplied, so the row says how far back this
             -- particular run actually reached rather than what the configuration happens to say today.
             , CASE WHEN w.IsEnabled     = 0    THEN NULL
                    WHEN w.WatermarkDate IS NULL THEN NULL
                    ELSE w.OverlapDays
               END AS RecommendedOverlapDaysApplied

             , w.auditModifiedDateUtc
          FROM config.LoadWatermark AS w
         WHERE w.LoadWatermarkId = @LoadWatermarkId;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them, so
        -- capture them before doing anything else.
        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        -- No ROLLBACK. This procedure opens no transaction, and a caller's transaction is not this
        -- procedure's to end -- even when this error has doomed it. See the header for what that costs.
        --
        -- @ExecutionLogId is NULL because no start row was opened, which sends the MERGE inside
        -- logs.uspRecordExecutionErrorUpdate down its orphan-INSERT branch. That is the intended path
        -- here, not a fallback. This procedure swallows everything by design, so it cannot mask the
        -- error re-raised below it.
        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = NULL
            , @ContextMessage  = @ContextMessage;

        -- Bare, so the ORIGINAL error number reaches the caller. RAISERROR would make it 50000 and the
        -- loader could no longer tell a deadlock from a missing configuration row. The leading semicolon
        -- is required: a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetLoadWatermark'
    , @Description = N'Returns the watermark row for one feed and activity location plus the run parameters it implies: RecommendedRunMode, RecommendedFromDate, RecommendedToDate and RecommendedOverlapDaysApplied. The loader''s first read of the night. The computed columns are here so that the day-granular overlap arithmetic (G25) exists in one place -- a loader doing the subtraction itself would be a second authority on a rule whose failure mode is a silent gap in a regulatory mirror. WatermarkDate NULL means never successfully loaded, so RecommendedRunMode is ''Full''; that is the seeded state, which is how a fresh database asks for everything without being told to. RecommendedRunMode is NULL when IsEnabled = 0, and logs.uspStartLoadRun rejects a NULL @RunMode by name, so a loader that ignores the pause still cannot start a paused feed. RecommendedToDate is the UTC date, matching the ceiling config.uspSetLoadWatermark enforces. A missing configuration row raises an error rather than returning zero rows, because both of the loader''s available guesses are wrong: "no configuration" read as a full load makes a typo''d feed name re-fetch everything, and read as "nothing to do" makes a scheduled load stop happening and report success. INSTRUMENTATION IS ERROR-ONLY, which is two decisions: no logs.ExecutionLog row is opened on the successful path (DA1 review decision 2 -- a refreshing monitoring grid is the highest-frequency caller in the system), but the CATCH block always records, because MDE''s review found the opposite failure in another application -- a procedure whose body was one SELECT called a scalar UDF inside it, the UDF errored, and nothing was recorded anywhere. A read has no INSERT of its own, but a UDF, a view, a conversion, a deadlock or a permission can all fail. The error row is an ORPHAN by design (no start row existed), and the procedure passes a @ContextMessage saying so, or every read failure would look like a second defect in the logging chain. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The loader only. The monitoring web app has no screen for the watermark in Phase 1, and granting a
-- read "in case" is how a permission surface grows without anyone deciding to grow it. If a monitor
-- screen for it appears later, the grant is one guarded statement and one posture assertion.
--
-- Ownership chaining carries the SELECT on config.LoadWatermark and the chained INSERT into
-- logs.ExecutionLog through this grant, so the loader login holds no direct permission on either
-- table (AR3). The loader also holds EXECUTE on logs.uspRecordExecutionError from script 354, which
-- is what lets the CATCH block above record at all.
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspGetLoadWatermark TO RCRAInfoLoaderRole;
END;
GO

PRINT N'512: config.uspGetLoadWatermark created or altered, EXECUTE granted to the loader role.';
GO
