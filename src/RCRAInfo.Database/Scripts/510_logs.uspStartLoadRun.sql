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
ObjectName:   logs.uspStartLoadRun
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Opens a load run. Inserts one logs.LoadRun row with Status = 'Running' and returns its key, which every other procedure
the run calls will carry. It is the loader's first database call of the night and the only place a LoadRunId is created.

It does two other things on the way, both of which exist because a scheduled process on a workstation or a server does
not always get to finish: it marks previously stranded runs 'Abandoned', and it refuses to start a second run while
another is genuinely in flight.

========================================================================================================================
Requirements and Key Dependencies:

logs.LoadRun -- inserted here, and the target of the abandonment sweep.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, which is copied
verbatim from .claude/skills/sql-objects/templates/procedure.sql.

config.uspGetLoadWatermark supplies @RunMode, @RequestedFromDate, @RequestedToDate, @WatermarkBeforeDate and
@OverlapDaysApplied. This procedure does not read the watermark itself -- see the note below on why.

EXECUTE is granted to RCRAInfoLoaderRole only. The monitoring web app is read-only in Phase 1 and has no business
opening a run.

========================================================================================================================
Notes:

THIS PROCEDURE WRITES, SO IT IS INSTRUMENTED. Every write procedure in this database carries the AR8 block; reads are
opt-in and only logs.uspGetLoadRunPage is named. Note what that means here: one logs.LoadRun row and one
logs.ExecutionLog row are written per run, for the same event, and that is not duplication. The LoadRun row is the
subject -- what the run did. The ExecutionLog row is the record that this call happened at all, including the calls that
threw before a LoadRun row existed. A run refused for concurrency has no LoadRun row and would otherwise leave no trace
whatever.

IT DOES NOT READ THE WATERMARK, AND THE SPLIT IS DELIBERATE. Reading config.LoadWatermark here would make one procedure
that decides what to fetch and records that it decided, and the loader would have no way to log a run whose parameters
it overrode by hand -- a re-load of one week, say. config.uspGetLoadWatermark recommends, the loader decides, and this
procedure records the decision it was given. The consequence is that the parameters recorded on the row are what the
loader ACTUALLY used, which is the only version worth auditing.

@RunMode AND THE DATES ARE VALIDATED HERE EVEN THOUGH CK_logs_LoadRun_RunMode WOULD CATCH THE FIRST ONE. The check
constraint raises error 547 naming a constraint, which tells the loader's exception handler that something is wrong with
a database object; the explicit test raises 50000 naming the parameter and listing the legal values. The constraint stays
as the backstop for anything that writes the table without going through here.

THE ABANDONMENT SWEEP. A run whose process was killed leaves Status = 'Running' and CompletedDateUtc NULL forever, and
that state is indistinguishable from a run still working. The monitoring grid then shows a load that has been running for
nine days. So a starting run closes out its predecessors: any row for the same activity location that is still 'Running'
and older than @AbandonAfterMinutes becomes 'Abandoned' with CompletedDateUtc set. The threshold has a FLOOR of 15
minutes, enforced, because a value low enough to catch a healthy in-flight run is far worse than leaving a dead one
lying: it would abandon the row of a run that is still writing, and then the concurrency check below would wave a second
loader through. Pass NULL to skip the sweep entirely.

The sweep covers ONE activity location -- this run's. A stranded run for a different location belongs to a different
schedule and is not this run's business to close. It also does not name the sweeping run in the abandoned row's
FailureMessage, because the sweep happens before the INSERT and there is no id yet; running the sweep afterwards would
mean the new row is itself a candidate, which is a worse trade than a message that names the age instead of the id.

FailureMessage IS WRITTEN ONLY WHERE IT IS EMPTY. A run that recorded a real failure and then died before completing
would otherwise have its own account of what went wrong overwritten by the sweep's boilerplate.

THE CONCURRENCY REFUSAL, AND THE LOCK THAT MAKES IT MEAN ANYTHING. Two loaders running at once double every HTTP
request, and both would advance the watermark -- so by default a start is refused while another run for the same
activity location is in flight. Under READ COMMITTED that check is worth nothing on its own: two starts a millisecond
apart both see no 'Running' row and both insert one. The existence check therefore takes (UPDLOCK, HOLDLOCK), which
holds a key-range lock on IX_logs_LoadRun_Status until the COMMIT, so the second caller waits and then sees the first
caller's row. This is the only range lock taken anywhere in this database, and it is taken over at most a handful of
rows.

The refusal is an error rather than a NULL @LoadRunId, and that is on purpose: a scheduled task that quietly returns
"nothing to do" when it collided with a still-running load reports success to Task Scheduler, and the collision is
invisible until someone asks why the mirror is a day behind. Pass @AllowConcurrent = 1 to override -- which is the right
thing for a Reconcile pass running alongside an Incremental, and the wrong thing for two Incrementals.

'Targeted' IS EXEMPT FROM THE REFUSAL IN BOTH DIRECTIONS, AND THE SECOND DIRECTION IS THE ONE THAT MATTERS. A Targeted
run is one handler asked for by hand (the loader's --handler-id switch): it walks no date window, so it cannot advance
the watermark, and it issues a handful of requests rather than doubling a population's worth -- so neither reason for
the refusal applies to it, and the loader passes @AllowConcurrent = 1. The predicate ALSO ignores Targeted rows when
deciding whether something is in flight, which is the half that would otherwise bite: an operator who interrupts a
--handler-id run leaves a 'Running' row, and counting it would refuse every scheduled load until the abandonment
threshold swept it twelve hours later. A diagnostic that can stop the nightly load is worse than no diagnostic.

WHAT @KeyParameters MAY CONTAIN. Identifiers, dates and counts. @MachineName, @ProcessId and @ApplicationVersion are
omitted from it deliberately -- not because they are sensitive, but because they land on the LoadRun row this call
returns, and logging them a second time adds a column to search rather than a fact to know. From MDE's own template: do
NOT include parameters such as passwords and Personally Identifiable Information.

@LoadRunId IS AN OUTPUT PARAMETER, NOT A RESULT SET. Matches logs.uspStartExecutionLogging, and EF Core reads it through
a SqlParameter with Direction = Output on an ExecuteSqlRawAsync call. A single-row SELECT would work too but would make
this the only write procedure in the database that returns rows.

SCOPE_IDENTITY () IS CHECKED. It returns NULL if an INSERT trigger is ever added here and changes scope. The caller would
then carry a NULL LoadRunId into every subsequent call of the run, and each one would fail on a different foreign key,
somewhere else. Fail here instead, where the cause is still visible.

========================================================================================================================
Example Usage and Performance:

-- The scheduled incremental, with the parameters config.uspGetLoadWatermark recommended.
DECLARE @LoadRunId INT;

EXEC logs.uspStartLoadRun
      @RunMode             = N'Incremental'
    , @ActivityLocation    = N'MD'
    , @RequestedFromDate   = '2026-09-03'
    , @RequestedToDate     = '2026-09-05'
    , @WatermarkBeforeDate = '2026-09-04'
    , @OverlapDaysApplied  = 1
    , @MachineName         = N'MDE-55TT2J4'
    , @ProcessId           = 18244
    , @ApplicationVersion  = N'1.0.0+build.214'
    , @LoadRunId           = @LoadRunId OUTPUT;

-- The first run of a fresh database: WatermarkDate was NULL, so everything.
EXEC logs.uspStartLoadRun @RunMode = N'Full', @LoadRunId = @LoadRunId OUTPUT;

-- A reconcile pass alongside a scheduled incremental.
EXEC logs.uspStartLoadRun @RunMode = N'Reconcile', @AllowConcurrent = 1, @LoadRunId = @LoadRunId OUTPUT;

-- One handler asked for by hand -- the loader's --handler-id switch. No date window, because a Targeted run does not
-- walk one and therefore never advances the watermark: the three date parameters stay NULL, and that is what tells a
-- later reader this row cannot have moved the bookmark. @AllowConcurrent = 1 because a diagnostic must not have to wait
-- for the nightly load, and the load must not have to wait for it.
EXEC logs.uspStartLoadRun @RunMode = N'Targeted', @AllowConcurrent = 1, @LoadRunId = @LoadRunId OUTPUT;

One range-locked seek on IX_logs_LoadRun_Status, one narrow UPDATE over the stranded rows (normally none), and one
singleton insert at the end of a clustered identity. It runs once per load, so its cost never matters; what matters is
that the range lock is held only for the length of that insert.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the first of the loader's two bookends.
2026-09-06	rsincero						@RunMode admits 'Targeted', for the single-handler run --handler-id starts, and
											the in-flight check ignores 'Targeted' rows so an interrupted diagnostic cannot
											refuse the scheduled load for the next @AbandonAfterMinutes.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspStartLoadRun
    -- What the run is, and what it was told to fetch. The loader decides these; this procedure records
    -- them. See the header on why the watermark is not read here.
      @RunMode              NVARCHAR (20)
    , @ActivityLocation     NVARCHAR (2)   = N'MD'
    , @RequestedFromDate    DATE           = NULL
    , @RequestedToDate      DATE           = NULL
    , @WatermarkBeforeDate  DATE           = NULL
    , @OverlapDaysApplied   INT            = NULL
    , @ResumedFromLoadRunId INT            = NULL
    -- Provenance. Recorded on the row, and deliberately not logged a second time in @KeyParameters.
    , @MachineName          NVARCHAR (128) = NULL
    , @ProcessId            INT            = NULL
    , @ApplicationVersion   NVARCHAR (50)  = NULL
    -- Housekeeping. NULL on the first skips the sweep; 1 on the second permits a parallel run.
    , @AbandonAfterMinutes  INT            = 720
    , @AllowConcurrent      BIT            = 0
    , @LoadRunId            INT            = NULL OUTPUT
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
                                                    , N'[logs].[uspStartLoadRun]')
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
    DECLARE @NowUtc      DATETIME2       = SYSUTCDATETIME ()
          , @Abandoned   INT             = 0
          , @InFlightId  INT             = NULL
          , @Failure     NVARCHAR (2048) = NULL;

    -- The caller's arguments, before anything is validated. Identifiers, dates and counts only.
    -- COALESCE on every one, including the integers and dates: CONCAT renders NULL as an empty string,
    -- so an omitted @OverlapDaysApplied would log as `OverlapDaysApplied=,` and read as a truncated
    -- message rather than as a NULL.
    SET @KeyParameters = CONCAT (N'RunMode=', COALESCE (@RunMode, N'(null)')
                               , N', ActivityLocation=', COALESCE (@ActivityLocation, N'(null)')
                               , N', RequestedFromDate=', COALESCE (CONVERT (NVARCHAR (10), @RequestedFromDate, 23), N'(null)')
                               , N', RequestedToDate=', COALESCE (CONVERT (NVARCHAR (10), @RequestedToDate, 23), N'(null)')
                               , N', WatermarkBeforeDate=', COALESCE (CONVERT (NVARCHAR (10), @WatermarkBeforeDate, 23), N'(null)')
                               , N', OverlapDaysApplied=', COALESCE (CAST (@OverlapDaysApplied AS NVARCHAR (11)), N'(null)')
                               , N', ResumedFromLoadRunId=', COALESCE (CAST (@ResumedFromLoadRunId AS NVARCHAR (11)), N'(null)')
                               , N', AbandonAfterMinutes=', COALESCE (CAST (@AbandonAfterMinutes AS NVARCHAR (11)), N'(none)')
                               , N', AllowConcurrent=', COALESCE (CAST (@AllowConcurrent AS NVARCHAR (1)), N'(null)'));

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

        SET @LoadRunId = NULL;

        -- ------------------------------------------------------------------------------------------
        -- 1. Validation. Before BEGIN TRANSACTION, as in every procedure here, so a bad argument
        --    never opens a transaction and never takes the range lock below.
        -- ------------------------------------------------------------------------------------------
        IF @RunMode IS NULL
           OR @RunMode NOT IN (N'Full', N'Incremental', N'Reconcile', N'Targeted')
        BEGIN
            SET @Failure = CONCAT (N'@RunMode = ', COALESCE (N'''' + @RunMode + N'''', N'NULL')
                                 , N' is not a load mode. Use ''Full'', ''Incremental'', ''Reconcile'' or ''Targeted''. ')
                         + N'A NULL here most often means config.uspGetLoadWatermark returned '
                         + N'RecommendedRunMode NULL, which is how it reports a feed with IsEnabled = 0 '
                         + N'-- a feed that is paused should not be started.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @ActivityLocation IS NULL
           OR LEN (LTRIM (RTRIM (@ActivityLocation))) = 0
        BEGIN
            SET @Failure = N'@ActivityLocation is required and defaults to ''MD'' (G2). It is not '
                         + N'constrained to MD, because a second location could be loaded later, but it '
                         + N'cannot be blank: the abandonment sweep and the concurrency check are both '
                         + N'scoped by it, and a blank one would scope them to nothing.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @RequestedFromDate IS NOT NULL
           AND @RequestedToDate IS NOT NULL
           AND @RequestedFromDate > @RequestedToDate
        BEGIN
            SET @Failure = CONCAT (N'@RequestedFromDate ', CONVERT (NVARCHAR (10), @RequestedFromDate, 23)
                                 , N' is after @RequestedToDate ', CONVERT (NVARCHAR (10), @RequestedToDate, 23)
                                 , N'. An inverted window is an empty window: EPA would return nothing, ')
                         + N'the run would succeed having fetched no records, and the watermark would '
                         + N'then advance over a range that was never asked for.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @OverlapDaysApplied IS NOT NULL
           AND @OverlapDaysApplied < 0
        BEGIN
            SET @Failure = CONCAT (N'@OverlapDaysApplied = ', @OverlapDaysApplied
                                 , N' is negative. The overlap reaches BACK from the watermark, so a ')
                         + N'negative value would describe a run that skipped days rather than one that '
                         + N're-fetched them.';
            ;THROW 50000, @Failure, 1;
        END;

        -- The floor is the whole reason this parameter is validated rather than clamped. Clamping a 2
        -- to 15 would silently do the right thing and teach the caller nothing; the value is almost
        -- certainly a units mistake, and the next one might be in the other direction.
        IF @AbandonAfterMinutes IS NOT NULL
           AND @AbandonAfterMinutes < 15
        BEGIN
            SET @Failure = CONCAT (N'@AbandonAfterMinutes = ', @AbandonAfterMinutes
                                 , N' is below the floor of 15. A threshold short enough to catch a ')
                         + N'HEALTHY in-flight run would mark its row ''Abandoned'' while it is still '
                         + N'writing, and the concurrency check would then wave a second loader through '
                         + N'-- two loaders, double the requests, and both advancing the watermark. '
                         + N'Pass NULL to skip the sweep instead of shortening it.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @ResumedFromLoadRunId IS NOT NULL
           AND NOT EXISTS (SELECT 1
                             FROM logs.LoadRun
                            WHERE LoadRunId = @ResumedFromLoadRunId
                              AND IsDeleted = 0)
        BEGIN
            -- FK_logs_LoadRun_ResumedFromLoadRun would also catch a missing row, as error 547 naming a
            -- constraint. It cannot catch a soft-deleted one, and it cannot say which parameter was
            -- wrong.
            SET @Failure = CONCAT (N'@ResumedFromLoadRunId = ', @ResumedFromLoadRunId
                                 , N' is not an active load run. Either it never existed or it has been ')
                         + N'soft-deleted by the retention pass (G7), and a run cannot declare itself the '
                         + N'continuation of history that is no longer there.';
            ;THROW 50000, @Failure, 1;
        END;

        SET @ContextMessage = CONCAT (N'RunMode=', @RunMode, N', ActivityLocation=', @ActivityLocation
                                    , N', AbandonAfterMinutes=', COALESCE (CAST (@AbandonAfterMinutes AS NVARCHAR (11)), N'(none)')
                                    , N', AllowConcurrent=', @AllowConcurrent);

        BEGIN TRANSACTION;

        -- ------------------------------------------------------------------------------------------
        -- 2. Close out runs that were stranded by a killed process.
        --    Same activity location only, older than the threshold, and FailureMessage written only
        --    where it is empty so a run's own account of its failure is never overwritten.
        -- ------------------------------------------------------------------------------------------
        IF @AbandonAfterMinutes IS NOT NULL
        BEGIN
            UPDATE logs.LoadRun
               SET Status               = N'Abandoned'
                 , CompletedDateUtc     = @NowUtc
                 , FailureMessage       = COALESCE (FailureMessage
                                                  , CONCAT (N'Marked ''Abandoned'' by a later run: still '
                                                          , N'''Running'' more than ', @AbandonAfterMinutes
                                                          , N' minute(s) after it started, so the process that '
                                                          , N'owned it did not complete it. No outcome was '
                                                          , N'recorded, and the counters on this row are '
                                                          , N'whatever the run had reached.'))
                 , auditModifiedBy      = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc = @NowUtc
             WHERE Status           = N'Running'
               AND CompletedDateUtc IS NULL
               AND IsDeleted        = 0
               AND ActivityLocation = @ActivityLocation
               AND StartedDateUtc   < DATEADD (MINUTE, -@AbandonAfterMinutes, @NowUtc);

            -- @@ROWCOUNT is reset by the next statement, so read it immediately.
            SET @Abandoned = @@ROWCOUNT;
        END;

        -- ------------------------------------------------------------------------------------------
        -- 3. Refuse a second concurrent run, unless asked not to.
        --    (UPDLOCK, HOLDLOCK) is what makes this a check rather than a coin toss: it holds a key-
        --    range lock until the COMMIT below, so two starts a millisecond apart serialise and the
        --    second one sees the first one's row. Without it both read "nothing running" and both
        --    insert. It is deliberately AFTER the sweep, so a run stranded last week does not block
        --    tonight's.
        -- ------------------------------------------------------------------------------------------
        IF @AllowConcurrent = 0
        BEGIN
            SELECT TOP (1) @InFlightId = LoadRunId
              FROM logs.LoadRun WITH (UPDLOCK, HOLDLOCK)
             WHERE Status           = N'Running'
               AND CompletedDateUtc IS NULL
               AND IsDeleted        = 0
               AND ActivityLocation = @ActivityLocation
               -- A 'Targeted' run never blocks anything. It is one handler asked for by hand, it walks
               -- no date window and it cannot advance the watermark, so neither reason for this refusal
               -- applies to it -- and the cost of counting it would be paid on the wrong side. An
               -- operator who interrupts a --handler-id run leaves a 'Running' row that would then
               -- refuse every scheduled load for the next @AbandonAfterMinutes, which is twelve hours
               -- by default. A diagnostic must not be able to stop the nightly load.
               AND RunMode         <> N'Targeted'
             ORDER BY LoadRunId;

            IF @InFlightId IS NOT NULL
            BEGIN
                SET @Failure = CONCAT (N'Load run ', @InFlightId, N' is still in flight for activity '
                                     , N'location ''', @ActivityLocation, N''', so this run is refused. '
                                     , N'Two loaders double every HTTP request and both advance the '
                                     , N'watermark. This is raised as an error rather than returned as a '
                                     , N'NULL LoadRunId on purpose: a scheduled task that quietly reports '
                                     , N'success after colliding with a running load hides the collision '
                                     , N'until someone asks why the mirror is a day behind. If run ', @InFlightId
                                     , N' is in fact dead, it will be swept once it is older than the '
                                     , N'abandonment threshold. Pass @AllowConcurrent = 1 for a genuinely '
                                     , N'parallel pass, such as a Reconcile beside an Incremental.');
                ;THROW 50000, @Failure, 1;
            END;
        END;

        -- ------------------------------------------------------------------------------------------
        -- 4. The run row. Status, StartedDateUtc, InvokedBy, the eleven counters and the audit
        --    columns are all left to their DEFAULTs -- the table owns those values, and naming them
        --    here would be a second authority to keep in step with it.
        -- ------------------------------------------------------------------------------------------
        INSERT logs.LoadRun
        (
              RunMode
            , ActivityLocation
            , RequestedFromDate
            , RequestedToDate
            , WatermarkBeforeDate
            , OverlapDaysApplied
            , ResumedFromLoadRunId
            , MachineName
            , ProcessId
            , ApplicationVersion
        )
        VALUES
        (
              @RunMode
            , @ActivityLocation
            , @RequestedFromDate
            , @RequestedToDate
            , @WatermarkBeforeDate
            , @OverlapDaysApplied
            , @ResumedFromLoadRunId
            , @MachineName
            , @ProcessId
            , @ApplicationVersion
        );

        SET @LoadRunId = SCOPE_IDENTITY ();

        -- SCOPE_IDENTITY () returns NULL if an INSERT trigger is ever added here and changes scope. The
        -- caller would then carry a NULL LoadRunId through the whole run and each subsequent call would
        -- fail on a different foreign key, far from the cause.
        IF @LoadRunId IS NULL
        BEGIN
            SET @Failure = N'Failed to retrieve SCOPE_IDENTITY () after inserting the logs.LoadRun row. '
                         + N'The run would have proceeded with no identity, and every call it made would '
                         + N'have failed somewhere else.';
            ;THROW 50000, @Failure, 1;
        END;

        SET @Comments = CONCAT (N'LoadRunId=', @LoadRunId, N', RunMode=', @RunMode
                              , N', ActivityLocation=', @ActivityLocation
                              , N', AbandonedStaleRuns=', @Abandoned);

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

        -- The rollback discards the LoadRunId this call had allocated, so the caller must not be left
        -- holding one. Cleared before the rollback would be equally correct; cleared here it is next to
        -- the reason.
        SET @LoadRunId = NULL;

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
        -- loader could no longer tell a deadlock from a concurrency refusal. The leading semicolon is
        -- required: a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspStartLoadRun'
    , @Description = N'Opens a load run: inserts one logs.LoadRun row with Status = ''Running'' and returns its key as an OUTPUT parameter. The only place a LoadRunId is created. It does not read config.LoadWatermark -- config.uspGetLoadWatermark recommends the parameters, the loader decides, and this procedure records the decision it was given, so the row shows what the run ACTUALLY used. Two pieces of housekeeping come with it. First, runs for the same activity location that are still ''Running'' more than @AbandonAfterMinutes after they started are marked ''Abandoned'' with CompletedDateUtc set, because a killed process otherwise leaves a row indistinguishable from a live run forever; the threshold has an enforced floor of 15 minutes, since a shorter one would abandon a healthy run mid-write, and NULL skips the sweep. Second, a start is REFUSED while another run for the same location is in flight, because two loaders double every request and both advance the watermark; the existence check takes (UPDLOCK, HOLDLOCK) so that two near-simultaneous starts serialise instead of both succeeding, and the refusal is an error rather than a NULL key so a collision cannot be reported to Task Scheduler as success. @AllowConcurrent = 1 overrides it, which is right for a Reconcile beside an Incremental. A ''Targeted'' run -- one handler asked for by hand through the loader''s --handler-id switch -- is exempt on both sides: it neither triggers the refusal nor counts as the run in flight, because it walks no date window, cannot advance the watermark and issues a handful of requests, and because an operator who interrupts one would otherwise leave a ''Running'' row that refused every scheduled load until the sweep cleared it. @RunMode and the date window are validated here as well as by CK_logs_LoadRun_RunMode, so the loader gets a message naming the parameter instead of error 547 naming a constraint. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The loader only. The monitoring web app is read-only in Phase 1, and the mirror image of this
-- grant -- the monitor being unable to open a run -- is asserted by
-- build/check_permission_posture.py, so the asymmetry is measured rather than intended.
--
-- Ownership chaining carries the INSERT and UPDATE on logs.LoadRun and the INSERT on
-- logs.ExecutionLog through this grant, so the loader login holds no direct permission on either
-- table (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspStartLoadRun TO RCRAInfoLoaderRole;
END;
GO

PRINT N'510: logs.uspStartLoadRun created or altered, EXECUTE granted to the loader role.';
GO
