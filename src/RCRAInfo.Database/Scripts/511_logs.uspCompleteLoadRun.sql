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
ObjectName:   logs.uspCompleteLoadRun
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Closes a load run. Sets Status and CompletedDateUtc on the logs.LoadRun row that logs.uspStartLoadRun opened, and records
the eleven counters the run accumulated. It is the loader's last database call of the night, and after it the row is
final.

========================================================================================================================
Requirements and Key Dependencies:

logs.LoadRun -- the row opened by logs.uspStartLoadRun, which must still be 'Running'.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, which is copied
verbatim from .claude/skills/sql-objects/templates/procedure.sql.

config.uspSetLoadWatermark, which owns logs.LoadRun.WatermarkAfterDate. This procedure deliberately cannot write it --
see the note below.

logs.HandlerLoadStatus is where 'PartiallySucceeded' is explained, per handler. This procedure records the count, not
the detail.

EXECUTE is granted to RCRAInfoLoaderRole only.

========================================================================================================================
Notes:

IT DOES NOT WRITE WatermarkAfterDate, AND THAT IS THE MOST IMPORTANT THING ON THIS PAGE. Advancing the watermark and
recording the advance are one fact, and config.uspSetLoadWatermark applies both inside one transaction. If this procedure
also accepted a @WatermarkAfterDate there would be two writers for one column and they could disagree -- a watermark
advanced but not audited, or audited but never advanced. The first of those is how a regulatory mirror loses a day
silently. So the loader calls config.uspSetLoadWatermark to advance, and then this procedure to close the run.

IT IS IDEMPOTENT, AND IT HAS TO BE. The instrumentation block's completion UPDATE runs after the COMMIT, so a call whose
work committed can still be reported to the caller as failed, and the loader may retry it. The UPDATE below is therefore
filtered on Status = 'Running', and a zero row count is resolved rather than assumed:

    no such row, or soft-deleted          -> error, naming which
    row already carries the same @Status  -> no-op, reported in the log Comments
    row carries a DIFFERENT final status  -> error, naming both

The last of those is the case worth having. A run closed as 'Succeeded' and then closed again as 'Failed' is not a retry;
it is two different beliefs about the same night, and overwriting the first with the second would make whichever call
happened to be last the truth.

THE COUNTERS TAKE NULL TO MEAN "LEAVE IT". Every counter column is NOT NULL DEFAULT (0), so a run that never got as far
as counting anything already reads zero. A loader that only knows some of them -- a run that died during the lookup
refresh, say -- passes those and leaves the rest, rather than having to send eleven zeroes and overwrite whatever the
run had already recorded through other procedures.

'Succeeded' WITH FAILED RECORDS IS REFUSED. That combination is what 'PartiallySucceeded' exists for, and the difference
matters downstream: the monitoring grid colours them differently and the watermark rule below turns on it. Reporting a
run as fully successful while its own counter says records failed is the single most misleading row this table could
hold, so it is rejected rather than accepted and explained.

'Failed' REQUIRES A MESSAGE. A failed run with a NULL FailureMessage is exactly the row the monitoring web app exists to
explain, and it explains nothing. 'PartiallySucceeded' does not require one, because logs.HandlerLoadStatus carries the
per-handler detail and a summary sentence would only duplicate it.

FailureMessage IS DISPLAYED ON A WEB PAGE. Whatever the loader puts in it reaches the monitor's users, so it must never
carry the API ID or Key, a bearer token, a request header, or a URL query string -- the same rule that keeps
logs.HandlerLoadAttempt.RequestPath to the path alone. This procedure cannot enforce that. It is named here because this
is the parameter through which a credential would travel if it ever did.

'Abandoned' IS ACCEPTED HERE TOO, EVEN THOUGH logs.uspStartLoadRun IS WHAT NORMALLY SETS IT. A process that catches its
own shutdown -- a Ctrl-C, a service stop, a Task Scheduler timeout -- knows more than a later sweep ever will, and can
say so at the time. The sweep remains for the case where nothing got the chance.

CompletedDateUtc IS SET FROM ONE SYSUTCDATETIME () CAPTURE, shared with auditModifiedDateUtc, so the two agree exactly.
Two calls a microsecond apart would leave an audit trail claiming the row was modified after the run finished.

WHAT @KeyParameters MAY CONTAIN. The run id, the status and the counters -- identifiers and counts. @FailureMessage is
NOT logged there, and that is not an oversight: it is free text the loader composed, it may be four thousand characters
of stack trace, and it is already stored on the row this call names. From MDE's own template: do NOT include parameters
such as passwords and Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- The ordinary close of a clean incremental.
EXEC logs.uspCompleteLoadRun
      @LoadRunId               = 418
    , @Status                  = N'Succeeded'
    , @SourceRecordsEnumerated = 8412
    , @SourceRecordsFetched    = 8412
    , @SourceRecordsInserted   = 17
    , @SourceRecordsUpdated    = 203
    , @SourceRecordsUnchanged  = 8192
    , @HttpRequestCount        = 92
    , @HttpRetryCount          = 3;

-- Some handlers failed. The detail is in logs.HandlerLoadStatus; this is the count.
EXEC logs.uspCompleteLoadRun @LoadRunId = 419, @Status = N'PartiallySucceeded', @SourceRecordsFailed = 4;

-- A hard failure. The message is required, and it must not carry a credential or a query string.
EXEC logs.uspCompleteLoadRun
      @LoadRunId      = 420
    , @Status         = N'Failed'
    , @FailureMessage = N'RCRAInfo returned HTTP 503 on 5 consecutive attempts for the handler page at offset 1200.';

One primary-key seek and one singleton update, plus a second seek only on the paths that raise an error. It runs once per
load.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the second of the loader's two bookends.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspCompleteLoadRun
      @LoadRunId                INT
    , @Status                   NVARCHAR (20)
    -- Required when @Status is 'Failed'. Reaches a web page, so no credentials, headers or query
    -- strings -- see the header note.
    , @FailureMessage           NVARCHAR (4000) = NULL
    -- The counters. NULL means "leave whatever the row already holds", which is 0 by DEFAULT on a run
    -- that never got as far as counting.
    , @LookupListsRefreshed     INT             = NULL
    , @SourceRecordsEnumerated  INT             = NULL
    , @SourceRecordsFetched     INT             = NULL
    , @SourceRecordsInserted    INT             = NULL
    , @SourceRecordsUpdated     INT             = NULL
    , @SourceRecordsUnchanged   INT             = NULL
    , @SourceRecordsSoftDeleted INT             = NULL
    , @SourceRecordsSkipped     INT             = NULL
    , @SourceRecordsFailed      INT             = NULL
    , @HttpRequestCount         INT             = NULL
    , @HttpRetryCount           INT             = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- -------------------------------------------------------------------------------------------------
    -- AR8 instrumentation. Boilerplate: copy verbatim.
    -- -------------------------------------------------------------------------------------------------
    -- Not a fallback for odd cases: this literal is what the loader login actually logs, because
    -- metadata visibility is denied to it. See the header note. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspCompleteLoadRun]')
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

    -- -------------------------------------------------------------------------------------------------
    -- This procedure's own state.
    -- -------------------------------------------------------------------------------------------------
    DECLARE @NowUtc         DATETIME2       = SYSUTCDATETIME ()
          , @RowsClosed     INT             = 0
          , @CurrentStatus  NVARCHAR (20)   = NULL
          , @CurrentDeleted BIT             = NULL
          , @Failure        NVARCHAR (2048) = NULL;

    -- Identifiers and counts only. @FailureMessage is excluded by name -- see the header.
    SET @KeyParameters = CONCAT (N'LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(null)')
                               , N', Status=', COALESCE (@Status, N'(null)')
                               , N', LookupListsRefreshed=', COALESCE (CAST (@LookupListsRefreshed AS NVARCHAR (11)), N'(keep)')
                               , N', Enumerated=', COALESCE (CAST (@SourceRecordsEnumerated AS NVARCHAR (11)), N'(keep)')
                               , N', Fetched=', COALESCE (CAST (@SourceRecordsFetched AS NVARCHAR (11)), N'(keep)')
                               , N', Inserted=', COALESCE (CAST (@SourceRecordsInserted AS NVARCHAR (11)), N'(keep)')
                               , N', Updated=', COALESCE (CAST (@SourceRecordsUpdated AS NVARCHAR (11)), N'(keep)')
                               , N', Unchanged=', COALESCE (CAST (@SourceRecordsUnchanged AS NVARCHAR (11)), N'(keep)')
                               , N', SoftDeleted=', COALESCE (CAST (@SourceRecordsSoftDeleted AS NVARCHAR (11)), N'(keep)')
                               , N', Skipped=', COALESCE (CAST (@SourceRecordsSkipped AS NVARCHAR (11)), N'(keep)')
                               , N', Failed=', COALESCE (CAST (@SourceRecordsFailed AS NVARCHAR (11)), N'(keep)')
                               , N', HttpRequests=', COALESCE (CAST (@HttpRequestCount AS NVARCHAR (11)), N'(keep)')
                               , N', HttpRetries=', COALESCE (CAST (@HttpRetryCount AS NVARCHAR (11)), N'(keep)'));

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        -- ==========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ====
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 1. Validation. Before BEGIN TRANSACTION, as in every procedure here.
        -- ------------------------------------------------------------------------------------------
        IF @LoadRunId IS NULL
        BEGIN
            SET @Failure = N'@LoadRunId is required. A NULL here usually means the loader lost the OUTPUT '
                         + N'value from logs.uspStartLoadRun, in which case the run it opened is still '
                         + N'''Running'' and will be swept as ''Abandoned'' by the next start.';
            ;THROW 50000, @Failure, 1;
        END;

        -- 'Running' is rejected separately from the other unknown values, because it is the one wrong
        -- answer a caller can give in good faith: closing a run to 'Running' is not closing it.
        IF @Status IS NULL
           OR @Status = N'Running'
           OR @Status NOT IN (N'Succeeded', N'PartiallySucceeded', N'Failed', N'Abandoned')
        BEGIN
            SET @Failure = CONCAT (N'@Status = ', COALESCE (N'''' + @Status + N'''', N'NULL')
                                 , N' is not a final outcome. Use ''Succeeded'', ''PartiallySucceeded'', ')
                         + N'''Failed'' or ''Abandoned''. ''Running'' is the status this procedure closes, '
                         + N'not one it can set: a run left ''Running'' has no CompletedDateUtc, which is '
                         + N'exactly how a later start recognises it as stranded.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @Status = N'Failed'
           AND (@FailureMessage IS NULL OR LEN (LTRIM (RTRIM (@FailureMessage))) = 0)
        BEGIN
            SET @Failure = N'@FailureMessage is required when @Status = ''Failed''. A failed run with no '
                         + N'message is precisely the row the monitoring web app exists to explain, and it '
                         + N'explains nothing. Say what failed and where -- but never include the API ID '
                         + N'or Key, a request header, or a URL query string: this column is displayed to '
                         + N'the monitor''s users.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @Status = N'Succeeded'
           AND COALESCE (@SourceRecordsFailed, 0) > 0
        BEGIN
            SET @Failure = CONCAT (N'@Status = ''Succeeded'' with @SourceRecordsFailed = ', @SourceRecordsFailed
                                 , N' is a contradiction, and ''PartiallySucceeded'' is the status that ')
                         + N'describes it. The difference is not cosmetic: the monitoring grid colours the '
                         + N'two differently, and only a run reported ''Succeeded'' may advance the '
                         + N'watermark -- so accepting this would let a run skip past records it failed to '
                         + N'load and close the gap behind itself.';
            ;THROW 50000, @Failure, 1;
        END;

        -- One loop's worth of near-identical tests, written out. A negative counter is a loader defect
        -- rather than an operator mistake, so the message names the parameter and stops.
        IF COALESCE (@LookupListsRefreshed,     0) < 0
           OR COALESCE (@SourceRecordsEnumerated,  0) < 0
           OR COALESCE (@SourceRecordsFetched,     0) < 0
           OR COALESCE (@SourceRecordsInserted,    0) < 0
           OR COALESCE (@SourceRecordsUpdated,     0) < 0
           OR COALESCE (@SourceRecordsUnchanged,   0) < 0
           OR COALESCE (@SourceRecordsSoftDeleted, 0) < 0
           OR COALESCE (@SourceRecordsSkipped,     0) < 0
           OR COALESCE (@SourceRecordsFailed,      0) < 0
           OR COALESCE (@HttpRequestCount,         0) < 0
           OR COALESCE (@HttpRetryCount,           0) < 0
        BEGIN
            SET @Failure = N'One or more counters is negative. Every counter on logs.LoadRun is a tally of '
                         + N'things that happened, so a negative value is a loader defect -- most often a '
                         + N'subtraction where an accumulation was meant. The counters are: '
                         + N'LookupListsRefreshed, SourceRecordsEnumerated, Fetched, Inserted, Updated, '
                         + N'Unchanged, SoftDeleted, Skipped, Failed, HttpRequestCount, HttpRetryCount.';
            ;THROW 50000, @Failure, 1;
        END;

        SET @ContextMessage = CONCAT (N'LoadRunId=', @LoadRunId, N', Status=', @Status
                                    , N', FailureMessageSupplied='
                                    , CASE WHEN @FailureMessage IS NULL THEN N'0' ELSE N'1' END);

        BEGIN TRANSACTION;

        -- ------------------------------------------------------------------------------------------
        -- 2. Close the run. Filtered on Status = 'Running', which is what makes a retry safe: a run
        --    already closed matches nothing and is resolved below rather than overwritten.
        --
        --    @NowUtc is one capture, shared by CompletedDateUtc and auditModifiedDateUtc, so the two
        --    agree exactly instead of the audit trail claiming the row changed after the run ended.
        -- ------------------------------------------------------------------------------------------
        UPDATE logs.LoadRun
           SET Status                   = @Status
             , CompletedDateUtc         = @NowUtc
             , FailureMessage           = COALESCE (@FailureMessage, FailureMessage)
             , LookupListsRefreshed     = COALESCE (@LookupListsRefreshed,     LookupListsRefreshed)
             , SourceRecordsEnumerated  = COALESCE (@SourceRecordsEnumerated,  SourceRecordsEnumerated)
             , SourceRecordsFetched     = COALESCE (@SourceRecordsFetched,     SourceRecordsFetched)
             , SourceRecordsInserted    = COALESCE (@SourceRecordsInserted,    SourceRecordsInserted)
             , SourceRecordsUpdated     = COALESCE (@SourceRecordsUpdated,     SourceRecordsUpdated)
             , SourceRecordsUnchanged   = COALESCE (@SourceRecordsUnchanged,   SourceRecordsUnchanged)
             , SourceRecordsSoftDeleted = COALESCE (@SourceRecordsSoftDeleted, SourceRecordsSoftDeleted)
             , SourceRecordsSkipped     = COALESCE (@SourceRecordsSkipped,     SourceRecordsSkipped)
             , SourceRecordsFailed      = COALESCE (@SourceRecordsFailed,      SourceRecordsFailed)
             , HttpRequestCount         = COALESCE (@HttpRequestCount,         HttpRequestCount)
             , HttpRetryCount           = COALESCE (@HttpRetryCount,           HttpRetryCount)
             , auditModifiedBy          = ORIGINAL_LOGIN ()
             , auditModifiedDateUtc     = @NowUtc
         WHERE LoadRunId = @LoadRunId
           AND Status    = N'Running'
           AND IsDeleted = 0;

        -- @@ROWCOUNT is reset by the next statement, so read it immediately.
        SET @RowsClosed = @@ROWCOUNT;

        -- ------------------------------------------------------------------------------------------
        -- 3. Nothing matched. Find out why, and say which of the three cases it is. This read is on
        --    the exceptional path only, so it costs nothing on a normal close.
        -- ------------------------------------------------------------------------------------------
        IF @RowsClosed = 0
        BEGIN
            SELECT @CurrentStatus  = Status
                 , @CurrentDeleted = IsDeleted
              FROM logs.LoadRun
             WHERE LoadRunId = @LoadRunId;

            IF @CurrentStatus IS NULL
            BEGIN
                SET @Failure = CONCAT (N'Load run ', @LoadRunId, N' does not exist. Either the id never '
                                     , N'came from logs.uspStartLoadRun, or the transaction that opened '
                                     , N'it rolled back -- in which case the loader is holding an id that '
                                     , N'was never committed.');
                ;THROW 50000, @Failure, 1;
            END;

            IF @CurrentDeleted = 1
            BEGIN
                SET @Failure = CONCAT (N'Load run ', @LoadRunId, N' has been soft-deleted, so it cannot be '
                                     , N'closed. A run retired by the retention pass (G7) is history that '
                                     , N'has been withdrawn; writing an outcome onto it would revive part '
                                     , N'of a row the rest of the database treats as gone.');
                ;THROW 50000, @Failure, 1;
            END;

            IF @CurrentStatus = @Status
            BEGIN
                -- The retry case, and the reason this procedure is filtered rather than unconditional.
                -- The counters are NOT applied: the run is closed, and a second opinion about its
                -- totals arriving after the fact is not more accurate than the first.
                SET @Comments = CONCAT (N'LoadRunId=', @LoadRunId, N', Status=', @Status
                                      , N', RowsClosed=0, NoOp=1 -- already closed with this status, so '
                                      , N'this call is a retry and the row is left exactly as it was.');
            END
            -- No semicolon on the END above: `END; ELSE` is a syntax error in T-SQL, because the
            -- semicolon terminates the IF and leaves ELSE with no statement to attach to.
            ELSE
            BEGIN
                SET @Failure = CONCAT (N'Load run ', @LoadRunId, N' is already closed as ''', @CurrentStatus
                                     , N''' and this call asked for ''', @Status, N'''. That is not a retry, '
                                     , N'it is two different accounts of the same run, and applying the '
                                     , N'second would make whichever call happened to arrive last the '
                                     , N'truth. Nothing has been changed.');
                ;THROW 50000, @Failure, 1;
            END;
        END
        -- Again no semicolon: `END; ELSE` does not parse.
        ELSE
        BEGIN
            SET @Comments = CONCAT (N'LoadRunId=', @LoadRunId, N', Status=', @Status
                                  , N', RowsClosed=', @RowsClosed, N', NoOp=0');
        END;

        -- ==========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- ==========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT; see the template header for what that costs.
        -- auditModifiedDateUtc is set explicitly because its DEFAULT fires on INSERT only.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc           = @EndTimeUtc
                 , ElapsedMilliseconds  = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                     , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful           = 1
                 , Comments             = @Comments
                 , auditModifiedBy      = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc = @EndTimeUtc
             WHERE ExecutionLogId = @ExecutionId;
        END;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them, so
        -- capture them before doing anything else -- including before the rollback.
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

        -- The rollback destroyed the row logs.uspStartExecutionLogging wrote. Put it back, with the
        -- ORIGINAL @StartTimeUtc, or the only unrecorded executions in the database would be the
        -- failures. The nested TRY is required because the start procedure does not swallow: an error
        -- escaping here would replace the error being reported.
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
        -- loader could no longer tell a deadlock from a double close. The leading semicolon is required:
        -- a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspCompleteLoadRun'
    , @Description = N'Closes a load run: sets Status and CompletedDateUtc on the logs.LoadRun row logs.uspStartLoadRun opened, and records the eleven counters. It deliberately CANNOT write WatermarkAfterDate -- advancing the watermark and recording the advance are one fact, and config.uspSetLoadWatermark applies both in one transaction, because two writers for that column could leave a watermark advanced but not audited, which is how a day goes missing silently. Idempotent by construction: the UPDATE is filtered on Status = ''Running'', and a zero row count is resolved rather than assumed -- a missing or soft-deleted run is an error naming which, a run already closed with the SAME status is a no-op recorded in the log, and a run already closed with a DIFFERENT status is an error naming both, because that is two accounts of one night rather than a retry. Counters take NULL to mean "leave whatever the row holds", so a run that died partway does not have to send eleven zeroes. ''Succeeded'' together with SourceRecordsFailed > 0 is refused, since ''PartiallySucceeded'' describes it and only a ''Succeeded'' run may advance the watermark. ''Failed'' requires a message; that message reaches the monitoring web page, so it must never carry the API ID or Key, a header, or a query string. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The loader only, and the monitor's inability to close a run is asserted by
-- build/check_permission_posture.py rather than assumed.
--
-- Ownership chaining carries the UPDATE on logs.LoadRun and the INSERT on logs.ExecutionLog through
-- this grant, so the loader login holds no direct permission on either table (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspCompleteLoadRun TO RCRAInfoLoaderRole;
END;
GO

PRINT N'511: logs.uspCompleteLoadRun created or altered, EXECUTE granted to the loader role.';
GO
