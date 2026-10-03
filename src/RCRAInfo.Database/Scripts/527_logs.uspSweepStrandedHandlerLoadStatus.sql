-- SET XACT_ABORT ON sits ABOVE the header block deliberately. sys.sql_modules stores only the batch
-- that contains CREATE, so a header placed after this GO would be present in the file and absent from
-- the database. build/check_stored_headers.py measures that against the deployed catalog.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspSweepStrandedHandlerLoadStatus
Author:       rsincero
CreateDate:   2026-09-06
========================================================================================================================
Description:

Resolves logs.HandlerLoadStatus rows that a run enumerated and then never finished, once that run itself has ended.
A row left at 'Pending' or 'InProgress' under a run that is no longer 'Running' describes a download that is neither
happening nor finished, and nothing else in this database will ever revisit it -- 510 sweeps stale logs.LoadRun rows to
'Abandoned' and 520 only ever writes forward from a live run. This procedure is the missing half of that housekeeping,
at the version grain.

IT EXISTS BECAUSE THE ROWS ARE WHAT THE MONITORING WEB APP SHOWS (AR2). logs.uspGetHandlerLoadStatusPage is the read
behind E's grid and logs.uspGetLoadRunSummary counts these statuses by name, so 202 rows stranded across old runs read
to a member of staff as 202 downloads still in flight -- for runs that closed hours or weeks ago. That is the same class
of defect F1 found on 2026-09-06 in the single-handler path: the handler data was stored correctly and the RECORD of how
it was stored said 'still working on it'. There it was one row and a missing call; here it is the accumulated residue
of every run that ended early.

WHAT THE FIRST SWEEP FOUND, AND WHY IT WRITES AN OBSERVATION RATHER THAN QUIETLY TIDYING UP. All 202 stranded rows on
the development database belong to runs whose own status is 'Succeeded'. A run that reports success while leaving
versions it enumerated unresolved is making a claim it did not earn, and a sweep that silently corrected the symptom
would remove the only evidence of it. So the rows are resolved AND one logs.DataQualityObservation is written per
offending run -- see the notes for which runs qualify.

========================================================================================================================
Requirements and Key Dependencies:

logs.HandlerLoadStatus (script 310), the rows swept. logs.LoadRun (script 300), which decides whether a row is stranded
or merely still being worked on. logs.DataQualityObservation (script 330), for the contradiction described above.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, copied from
.claude/skills/sql-objects/templates/procedure.sql.

EXECUTE is granted to RCRAInfoLoaderRole only. The monitoring web app READS these rows and must not be able to change
them: a screen that can resolve its own backlog is a screen that can make a defect disappear (AR3).

========================================================================================================================
Notes:

'Failed' AND NOT 'Abandoned', AND THE ARGUMENT IS NOT LAZINESS. 'Abandoned' is the more precise word and
logs.LoadRun.Status already uses it, so the temptation is to add a sixth value to
CK_logs_HandlerLoadStatus_Status. It is not added, for two reasons. The cost is four places, not one -- the constraint
in script 310, the @Status whitelist in script 501, the per-status counters in script 502, and E's grid -- and every one
of them is a place a new value can be forgotten. And nothing is lost by omitting it, because AttemptCount already
carries the distinction: a swept row with AttemptCount = 0 was never attempted, and one with AttemptCount > 0 was
attempted and never concluded. Of the 202 rows on the development database, 200 have AttemptCount = 0 and 2 have 1.

'Failed' IS ALSO THE SAFE DIRECTION, WHICH SETTLES IT. Script 525 treats anything that is not 'Succeeded' as
enumerated-and-unfinished, so a swept row is re-fetched by a resuming run rather than skipped. The alternative reading
-- marking a row 'Skipped', which is the only other terminal value that would clear the grid -- asserts that a previous
run succeeded on that version. That is a claim this procedure cannot make, and acting on it would silently drop a
version from a later run's work.

THE RUN'S OWN STATUS IS THE GATE, NOT A CLOCK. A row is swept only when its run is not 'Running', so there is no
threshold to tune and no way to touch a load in flight -- including one that has been going for hours. A run that
crashed and still reads 'Running' is deliberately NOT swept here: logs.uspStartLoadRun marks it 'Abandoned' at the start
of the next run, using @AbandonAfterMinutes, and this procedure catches its rows on the sweep after that. Two procedures
applying one staleness rule at two grains, in that order, rather than each carrying its own copy of the threshold --
which is the correction script 525's header records for the resume read.

THE OBSERVATION IS WRITTEN ONLY FOR A RUN THAT CLAIMED IT WAS FINE. One row per run whose status is 'Succeeded' or
'PartiallySucceeded', because those are the runs whose report contradicts the rows they left. A run that closed 'Failed'
or 'Abandoned' has already said so, and an observation repeating it would be noise -- the rows still get resolved.
Attribution is to the run that stranded them and not to the sweep, so an operator filtering
logs.DataQualityObservation by LoadRunId lands on the run that has the problem.

RE-RUNNABLE AND CONVERGENT. Every UPDATE is guarded by Status IN ('Pending', 'InProgress'), so a second call reports 0,
writes nothing, and moves no auditModifiedDateUtc -- re-stamping it would make the audit trail claim the row changed
today when it was resolved last week. auditModifiedDateUtc and auditModifiedBy are set EXPLICITLY, because their
DEFAULTs fire only on INSERT.

WHAT @KeyParameters MAY CONTAIN. @ActivityLocation, @LoadRunId and counts. There is no payload, no handler identifier
and no free text in this signature at all, which is the easiest AR8 posture there is: nothing to exclude by name. The
observation Detail carries counts and a run number only.

========================================================================================================================
Example Usage and Performance:

-- 1. The ordinary housekeeping call: everything eligible, all locations.
DECLARE @Rows INT, @Runs INT;
EXEC logs.uspSweepStrandedHandlerLoadStatus
      @RowsAffected = @Rows OUTPUT
    , @RunsAffected = @Runs OUTPUT;

-- 2. Maryland only.
EXEC logs.uspSweepStrandedHandlerLoadStatus @ActivityLocation = N'MD';

-- 3. One named run, for an operator who knows which run stranded them.
EXEC logs.uspSweepStrandedHandlerLoadStatus @LoadRunId = 2140;

-- 4. Called twice: the second call reports 0 and 0 and writes nothing.

One UPDATE joining logs.HandlerLoadStatus to logs.LoadRun on the primary key, then one grouped INSERT. The UPDATE is
driven by the Status predicate rather than by a seek, so it scans the status table -- roughly 9,500 rows today and
growing with every run. That is acceptable for housekeeping run once per load and deliberately not indexed for: an index
on Status would be maintained by every one of the millions of writes script 520 makes, to serve a statement that runs
once. If the table reaches a size where the scan matters, the fix is to bound the sweep by LoadRunId, not to index
Status.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-06	rsincero						Initial version. Found while verifying run 2622: 202 rows stranded across
											runs that all closed 'Succeeded', and no object in the database able to
											resolve them. Script 525 had been assumed to do it and does not -- it is a
											read that opens no transaction, and it sweeps logs.LoadRun rows only.
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspSweepStrandedHandlerLoadStatus
      @ActivityLocation NVARCHAR (2)  = NULL
    , @LoadRunId        INT           = NULL
    , @RowsAffected     INT           = NULL OUTPUT
    , @RunsAffected     INT           = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- AR8 instrumentation. Boilerplate: copied verbatim from the template.
    -- =============================================================================================
    -- The literal is not a fallback for odd cases; it is what the loader login actually logs, because
    -- script 050 denies it metadata visibility. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspSweepStrandedHandlerLoadStatus]')
          , @StartTimeUtc   DATETIME2      = SYSUTCDATETIME ()
          , @EndTimeUtc     DATETIME2      = NULL
          , @ExecutionId    BIGINT         = NULL
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @Comments       NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    DECLARE @NowUtc      DATETIME2      = SYSUTCDATETIME ()
          , @ActorLogin   NVARCHAR (128) = ORIGINAL_LOGIN ()
          , @SweptCount   INT            = 0
          , @Observations INT            = 0
          , @Failure      NVARCHAR (2048) = NULL
          -- Guards the CATCH: the observation INSERT is inside the same transaction as the UPDATE, so
          -- a failure anywhere before the COMMIT means neither happened and the counts must read 0.
          , @Committed    BIT            = 0;

    -- Nothing here needs excluding by name; the signature carries no payload and no identifier.
    SET @KeyParameters = CONCAT (N'ActivityLocation=', COALESCE (@ActivityLocation, N'(all)')
                               , N', LoadRunId='
                               , COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(all)'));

    -- What was swept, kept so the observation INSERT can group it without re-reading the table the
    -- UPDATE has just changed -- after the UPDATE, no row still matches the predicate that found it.
    DECLARE @Swept TABLE
    (
        HandlerLoadStatusId INT           NOT NULL PRIMARY KEY,
        LoadRunId           INT           NOT NULL,
        PreviousStatus      NVARCHAR (20) NOT NULL,
        RunStatus           NVARCHAR (20) NOT NULL,
        AttemptCount        INT           NOT NULL
    );

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. ==============================================
        -- =========================================================================================

        -- -----------------------------------------------------------------------------------------
        -- 1. Validation, all of it before BEGIN TRANSACTION so a rejected call has no transaction to
        --    unwind and is recorded as a failed execution like any other.
        -- -----------------------------------------------------------------------------------------
        -- Assigned first, then thrown. THROW's message argument accepts a literal or a variable and
        -- NOT an expression, so a concatenation written inline is a syntax error.
        IF @ActivityLocation IS NOT NULL AND LEN (@ActivityLocation) <> 2
        BEGIN
            SET @Failure = N'@ActivityLocation must be a two-character state code, or NULL for every '
                         + N'location. A one-character value would match nothing and report a '
                         + N'successful sweep of zero rows, which is indistinguishable from there '
                         + N'being nothing to sweep.';
            THROW 50000, @Failure, 1;
        END;

        -- A named run must exist and be live. Refused rather than treated as 'nothing matched', for
        -- the reason above: an operator who mistypes a run number is owed an error, not a quiet zero.
        IF @LoadRunId IS NOT NULL
           AND NOT EXISTS (SELECT 1
                             FROM logs.LoadRun
                            WHERE LoadRunId = @LoadRunId
                              AND IsDeleted = 0)
        BEGIN
            SET @Failure = CONCAT (N'@LoadRunId ', @LoadRunId, N' does not exist in logs.LoadRun, or '
                                 , N'is soft-deleted. Pass NULL to sweep every eligible run.');
            THROW 50000, @Failure, 1;
        END;

        BEGIN TRANSACTION;

        -- -----------------------------------------------------------------------------------------
        -- 2. The sweep. The gate is the RUN's status and not a clock -- see the header.
        -- -----------------------------------------------------------------------------------------
        UPDATE s
           SET Status               = N'Failed'
             -- COALESCE, not an overwrite: an 'InProgress' row may already carry the moment its one
             -- attempt ended, and that is a truer completion time than the moment of the sweep.
             , CompletedDateUtc     = COALESCE (s.CompletedDateUtc, @NowUtc)
             -- Set EXPLICITLY. The DEFAULTs on these two fire on INSERT only.
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
        OUTPUT inserted.HandlerLoadStatusId
             , inserted.LoadRunId
             , deleted.Status
             , N'(resolved below)'
             , inserted.AttemptCount
          INTO @Swept (HandlerLoadStatusId, LoadRunId, PreviousStatus, RunStatus, AttemptCount)
          FROM logs.HandlerLoadStatus AS s
          JOIN logs.LoadRun           AS r ON r.LoadRunId = s.LoadRunId
         WHERE s.IsDeleted = 0
           AND r.IsDeleted = 0
           -- The guard that makes a second call a no-op.
           AND s.Status IN (N'Pending', N'InProgress')
           -- A run still going owns its rows. Nothing here can touch a load in flight.
           AND r.Status <> N'Running'
           AND (@ActivityLocation IS NULL OR s.ActivityLocation = @ActivityLocation)
           AND (@LoadRunId        IS NULL OR s.LoadRunId        = @LoadRunId);

        SET @SweptCount = @@ROWCOUNT;

        -- The OUTPUT clause cannot reach the joined logs.LoadRun row, so the run's status is filled in
        -- afterwards rather than captured above. Doing it here keeps step 3's predicate readable.
        UPDATE w
           SET RunStatus = r.Status
          FROM @Swept        AS w
          JOIN logs.LoadRun  AS r ON r.LoadRunId = w.LoadRunId;

        -- -----------------------------------------------------------------------------------------
        -- 3. The contradiction, one row per run that claimed it was fine. See the header for why a
        --    run that closed 'Failed' or 'Abandoned' gets no observation.
        -- -----------------------------------------------------------------------------------------
        INSERT INTO logs.DataQualityObservation
              (LoadRunId, ObservationType, Severity, TableName, ColumnName
             , ObservedValue, Detail, ObservedDateUtc)
        SELECT w.LoadRunId
             , N'StatusRowsStrandedByClosedRun'
             , N'Warning'
             , N'logs.HandlerLoadStatus'
             , N'Status'
             , CONCAT (N'run closed ', MIN (w.RunStatus), N' with ', COUNT (*), N' unresolved row(s)')
             , CONCAT (N'This run closed as ', MIN (w.RunStatus), N' while ', COUNT (*)
                     , N' version(s) it enumerated were still at Pending or InProgress: '
                     , SUM (CASE WHEN w.PreviousStatus = N'Pending' THEN 1 ELSE 0 END)
                     , N' never attempted (AttemptCount = 0) and '
                     , SUM (CASE WHEN w.PreviousStatus = N'InProgress' THEN 1 ELSE 0 END)
                     , N' attempted and never concluded. A run reporting success has claimed every '
                     , N'version it enumerated was accounted for, so this is a contradiction and not '
                     , N'merely untidy. The rows have been resolved to Failed by '
                     , N'logs.uspSweepStrandedHandlerLoadStatus, which makes a resuming run re-fetch '
                     , N'them rather than skip them; what is NOT repaired is whatever let the run '
                     , N'close without concluding them. See logs.HandlerLoadAttempt for this run.')
             , @NowUtc
          FROM @Swept AS w
         WHERE w.RunStatus IN (N'Succeeded', N'PartiallySucceeded')
         GROUP BY w.LoadRunId;

        SET @Observations = @@ROWCOUNT;

        SET @RowsAffected = @SweptCount;
        SET @RunsAffected = @Observations;

        -- Every figure is named even when it is zero. 'swept=0, contradictingRuns=0' is the answer an
        -- operator wants from a housekeeping call and a figure that appears only when it is non-zero
        -- cannot be checked for -- the same reasoning script 522 records for its status count.
        SET @Comments = CONCAT (N'swept=', @SweptCount
                              , N', neverAttempted='
                              , (SELECT COUNT (*) FROM @Swept WHERE PreviousStatus = N'Pending')
                              , N', attemptedNotConcluded='
                              , (SELECT COUNT (*) FROM @Swept WHERE PreviousStatus = N'InProgress')
                              , N', contradictingRuns=', @Observations);

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        SET @Committed = 1;

        -- Completion. Deliberately after the COMMIT; see the template header for what that costs. A
        -- retry provoked by a failure here is safe: the UPDATE above is guarded by
        -- Status IN ('Pending', 'InProgress') and finds nothing the second time.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc           = @EndTimeUtc
                 , ElapsedMilliseconds  = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                     , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful           = 1
                 , KeyParameters        = @KeyParameters
                 , Comments             = @Comments
                 , auditModifiedBy      = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them -- the
        -- CONCAT and the EXEC below both do -- so capture them before doing anything else.
        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        -- One test, not two: XACT_ABORT ON makes XACT_STATE () = -1 the common case, and -1 and 1
        -- both need the same unqualified rollback.
        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        -- On the rollback path the counts describe writes that no longer exist. Zero them, so a caller
        -- that logs @RowsAffected without checking for the exception cannot report a sweep that was
        -- undone.
        IF @Committed = 0
        BEGIN
            SET @RowsAffected = 0;
            SET @RunsAffected = 0;
        END;

        SET @ContextMessage = CONCAT (N'swept=', @SweptCount
                                    , N', contradictingRuns=', @Observations
                                    , CASE WHEN @Committed = 1
                                           THEN N', the sweep COMMITTED and the failure is after it; '
                                              + N'the rows ARE resolved.'
                                           ELSE N', nothing was swept (rolled back).' END);

        -- The rollback may have destroyed the row logs.uspStartExecutionLogging wrote, and only when
        -- this procedure was called inside a transaction that was ALREADY open. Put it back with the
        -- ORIGINAL @StartTimeUtc, or the only executions never recorded would be the failures.
        BEGIN TRY
            IF @ExecutionId IS NULL
               OR NOT EXISTS (SELECT 1
                                FROM logs.ExecutionLog
                               WHERE ExecutionLogId = @ExecutionId)
            BEGIN
                EXEC logs.uspStartExecutionLogging
                      @ProcedureName          = @ProcName
                    , @KeyParameters          = @KeyParameters
                    , @StartDateUtc           = @StartTimeUtc
                    , @ReCreatedAfterRollback = 1
                    , @ExecutionLogId         = @ExecutionId OUTPUT;
            END;
        END TRY
        BEGIN CATCH
            SET @ExecutionId = NULL;
        END CATCH;

        -- Swallows everything by design, so this call cannot mask the error below it.
        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionId
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        -- Bare, so the ORIGINAL error number reaches the caller. RAISERROR would make it 50000 and the
        -- loader could no longer tell a deadlock (1205, retry) from a rejected argument.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSweepStrandedHandlerLoadStatus'
    , @Description = N'Resolves logs.HandlerLoadStatus rows that a run enumerated and never finished, once that run itself has ended -- the version-grained half of the housekeeping logs.uspStartLoadRun performs at the run grain. A row at ''Pending'' or ''InProgress'' whose logs.LoadRun row is no longer ''Running'' describes a download that is neither happening nor finished, and nothing else in this database revisits it: 520 only writes forward from a live run, and 525 is a read that sweeps logs.LoadRun rows only. It matters because these rows are what E''s grid shows (logs.uspGetHandlerLoadStatusPage) and what logs.uspGetLoadRunSummary counts by name, so stranded rows read to a member of staff as downloads still in flight for runs that closed weeks ago -- the same class of defect F1 found in the single-handler path on 2026-09-06. The row is resolved to ''Failed'' and NOT to a new ''Abandoned'' value: ''Abandoned'' would cost a constraint change in 310, the whitelist in 501, the counters in 502 and E''s grid, and nothing is lost because AttemptCount already separates never-attempted (0) from attempted-and-never-concluded (>0). ''Failed'' is also the safe direction, because script 525 treats anything but ''Succeeded'' as unfinished, so a swept row is re-fetched by a resuming run rather than skipped -- whereas ''Skipped'' would assert that some earlier run succeeded on it, a claim this procedure cannot make. The gate is the run''s own status and not a clock, so there is no threshold to tune and no way to touch a load in flight; a crashed run still reading ''Running'' is deliberately left for logs.uspStartLoadRun to mark ''Abandoned'' first, so one staleness rule is applied at two grains in order rather than copied. One logs.DataQualityObservation of type StatusRowsStrandedByClosedRun is written per run whose status is ''Succeeded'' or ''PartiallySucceeded'', because those are the runs whose report contradicts the rows they left; a run that closed ''Failed'' or ''Abandoned'' has already said so and gets none, though its rows are still resolved. Attribution is to the run that stranded the rows, not to the sweep. Every UPDATE is guarded by Status IN (''Pending'', ''InProgress''), so a second call reports 0, writes nothing and does not re-stamp auditModifiedDateUtc; auditModifiedBy and auditModifiedDateUtc are set explicitly because their DEFAULTs fire only on INSERT. Optional @ActivityLocation and @LoadRunId narrow the sweep; a @LoadRunId that does not exist is refused rather than reported as zero rows. The signature carries no payload, no handler identifier and no free text, so nothing is excluded from @KeyParameters by name. EXECUTE is granted to RCRAInfoLoaderRole only -- the web app reads these rows and must not be able to resolve its own backlog (AR3).';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. The loader sweeps; the monitoring web app must not be able to. A screen that can resolve
-- the rows it displays is a screen that can make a defect disappear, and these particular rows are
-- the evidence that a run closed without finishing (AR3).
--
-- Ownership chaining carries the UPDATE on logs.HandlerLoadStatus, the read of logs.LoadRun, the
-- INSERT into logs.DataQualityObservation and the chained inserts into logs.ExecutionLog through this
-- single grant, so the loader login holds no direct permission on any of the four tables.
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspSweepStrandedHandlerLoadStatus TO RCRAInfoLoaderRole;
END;
GO

PRINT N'527: logs.uspSweepStrandedHandlerLoadStatus created or altered, EXECUTE granted to the loader role.';
GO
