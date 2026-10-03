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
ObjectName:   config.uspSetLoadWatermark
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Advances the incremental watermark, and records the advance on the run that made it -- both in one transaction, because
they are one fact. Also the way an operator changes OverlapDays, pauses a feed, or resets a watermark to force a full
re-load.

This is the only procedure in the database that may move config.LoadWatermark.WatermarkDate, and the only one that may
write logs.LoadRun.WatermarkAfterDate.

========================================================================================================================
Requirements and Key Dependencies:

config.LoadWatermark -- the row must already exist; this procedure never creates one.

logs.LoadRun -- WatermarkAfterDate is stamped here when the caller names a run.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, which is copied
verbatim from .claude/skills/sql-objects/templates/procedure.sql.

EXECUTE is granted to RCRAInfoLoaderRole only. Operator changes -- OverlapDays, a pause, a reset -- are made by the
developer or DBA in a query window; the monitoring web app is read-only in Phase 1.

========================================================================================================================
Notes:

IT WRITES TWO TABLES IN ONE TRANSACTION, AND THAT IS THE WHOLE REASON IT EXISTS IN THIS SHAPE. Advancing the watermark
and recording the advance are one fact. Split across two calls they can disagree: a watermark advanced but not audited,
or audited but never actually advanced. The first is how a regulatory mirror loses a day with nothing in the record to
say so. Hence logs.uspCompleteLoadRun deliberately has no @WatermarkAfterDate parameter -- one writer per column.

ADVANCING PAST RECORDS THAT WERE NEVER FETCHED IS THE CHEAPEST WAY TO LOSE DATA PERMANENTLY IN THIS DESIGN. The gap
closes behind the watermark and no later run looks there again. Every guard below is a version of that one sentence.

A FUTURE WATERMARK IS REFUSED. A date later than today freezes the mirror: every subsequent incremental asks for changes
since a date that has not happened, gets nothing, and reports success. It is the failure that looks most like working.
The ceiling is the UTC date, which is what config.uspGetLoadWatermark also uses for RecommendedToDate, so the two agree;
on part of each day that is one day ahead of EPA's Eastern date, which costs nothing because a day that has not started
holds no records.

EVERY CHANGE TO WatermarkDate MUST BE ATTRIBUTABLE -- to a run through @LoadRunId, or to a person through @Notes. That is
enforced, not advised. From the table's own column description: a watermark moved by hand with no explanation is
indistinguishable from one moved by mistake.

A RUN MAY ONLY STAMP THE WATERMARK WHILE IT IS 'Running' OR AFTER IT 'Succeeded'. A 'Failed', 'Abandoned' or
'PartiallySucceeded' run advancing the watermark is exactly the data-loss case above. Note the ordinary sequence: the
loader advances the watermark while its run is still 'Running', then calls logs.uspCompleteLoadRun -- so 'Running' has to
be allowed, and 'Succeeded' is allowed too for the case where the advance is applied after the close.

A BACKWARDS MOVE NEEDS @AllowRewind = 1. Moving a watermark back is safe for the DATA -- it re-fetches -- but it is not
free and it is not usually intended. An accidental rewind, a loader passing a stale variable, would re-fetch every
intervening day on every run: thousands of requests, silently, while looking like normal operation. So the safe direction
still has to be asked for.

@WatermarkDate = NULL DOES NOT MEAN "CLEAR IT", BECAUSE NULL ALREADY MEANS SOMETHING ON THAT COLUMN. NULL in
config.LoadWatermark.WatermarkDate means "never successfully loaded", which is what tells the loader its next run must be
a full load. So the parameter cannot use NULL for "leave it alone" and for "set it to NULL" at once. It uses NULL for
"leave it alone", and @ClearWatermark = 1 for the reset -- which also requires @Notes, since forcing a full re-load of
every handler in the state is not something to do by accident. Passing both is a contradiction and is refused.

@Notes = NULL LEAVES THE EXISTING NOTE; AN EMPTY STRING CLEARS IT. Same problem, same shape of answer. The loader's
ordinary advance sends no note, so an operator's explanation survives every subsequent run rather than being erased by
the next one.

IT CONVERGES: A CALL THAT WOULD CHANGE NOTHING CHANGES NOTHING. The UPDATE is guarded by a comparison of every column it
would set, using IS DISTINCT FROM where the column is nullable. Re-applying identical values would move
auditModifiedDateUtc and make the audit trail claim the configuration changed today -- the same rule the seed in
340_config.LoadWatermark.sql follows, applied to a procedure. This matters more here than in most places, because a
retried call is normal: the instrumentation block's completion UPDATE runs after the COMMIT, so a call whose work
committed can still be reported as failed.

LastAdvanced* ARE STAMPED ONLY WHEN THE DATE ACTUALLY MOVED. A call that changes OverlapDays alone leaves them, because
they mean "when the watermark last advanced" and not "when this row was last touched" -- auditModifiedDateUtc is what
means the latter. When an operator moves the date by hand, LastAdvancedByLoadRunId is set to NULL rather than left
pointing at the previous run: a NULL there honestly says "not a run", and @Notes says who.

IT DOES NOT CREATE A MISSING ROW. 340_config.LoadWatermark.sql seeds ('HandlerSource', 'MD'); anything else is inserted
deliberately. An upsert here would let a typo'd feed name quietly start its own watermark, advance it, and look healthy
while the feed it was meant to be tracking never moved.

(UPDLOCK) BUT NOT HOLDLOCK. The row is required to exist, so an ordinary update lock on it serialises two concurrent
advances. The range lock logs.uspStartLoadRun takes is needed there for the opposite reason: that check is for the
ABSENCE of a row, and an absence cannot be locked without locking the range where it would be.

WHAT @KeyParameters MAY CONTAIN. Identifiers, dates and counts. @Notes is operator free text and is excluded by name --
only whether one was supplied is logged. It is stored on the row this call names. From MDE's own template: do NOT include
parameters such as passwords and Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- The ordinary advance, called by the loader before it closes the run.
EXEC config.uspSetLoadWatermark @WatermarkDate = '2026-09-05', @LoadRunId = 418;

-- An operator widening the overlap window. The date is untouched, so LastAdvanced* are untouched too.
EXEC config.uspSetLoadWatermark @OverlapDays = 3, @Notes = N'Widened to 3 days after the 2026-09-02 EPA late-update incident.';

-- Pausing the feed during an EPA outage. IsEnabled = 0, not IsDeleted: a pause is not a retirement.
EXEC config.uspSetLoadWatermark @IsEnabled = 0, @Notes = N'Paused: RCRAInfo preprod returning 503 since 2026-09-05 08:00.';

-- Forcing a full re-load. Requires the explicit flag and a reason.
EXEC config.uspSetLoadWatermark @ClearWatermark = 1, @Notes = N'Reset to force a full re-load after the 130-139 child-collection backfill.';

-- A deliberate rewind, to re-fetch a week.
EXEC config.uspSetLoadWatermark @WatermarkDate = '2026-08-29', @AllowRewind = 1, @Notes = N'Rewound one week: the 2026-08-30 run advanced on a partial page.';

One update-locked seek on UX_config_LoadWatermark_Natural, one singleton update, and one primary-key update on
logs.LoadRun when a run is named. It runs once per load.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4. Owns both config.LoadWatermark.WatermarkDate
											and logs.LoadRun.WatermarkAfterDate, so the advance and its audit cannot
											disagree.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE config.uspSetLoadWatermark
      @FeedName         NVARCHAR (50)   = N'HandlerSource'
    , @ActivityLocation NVARCHAR (2)    = N'MD'
    -- NULL leaves the date alone. It cannot mean "clear it" -- NULL already means "never successfully
    -- loaded" on that column -- so the reset has its own flag.
    , @WatermarkDate    DATE            = NULL
    , @ClearWatermark   BIT             = 0
    -- The run that made the advance, if a run did. Its LoadRun row gets WatermarkAfterDate stamped.
    , @LoadRunId        INT             = NULL
    -- Configuration. NULL leaves each one as it is.
    , @OverlapDays      INT             = NULL
    , @IsEnabled        BIT             = NULL
    -- NULL leaves the existing note; an empty string clears it.
    , @Notes            NVARCHAR (4000) = NULL
    , @AllowRewind      BIT             = 0
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
                                                    , N'[config].[uspSetLoadWatermark]')
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
    -- This procedure's own state. One SYSUTCDATETIME () capture, so the config row and the run row
    -- carry the same instant rather than two a microsecond apart.
    -- -------------------------------------------------------------------------------------------------
    DECLARE @NowUtc               DATETIME2       = SYSUTCDATETIME ()
          , @TodayUtc             DATE            = CAST (SYSUTCDATETIME () AS DATE)
          , @LoadWatermarkId      INT             = NULL
          , @RunStatus            NVARCHAR (20)   = NULL
          , @CurrentWatermarkDate DATE            = NULL
          , @CurrentOverlapDays   INT             = NULL
          , @CurrentIsEnabled     BIT             = NULL
          , @CurrentNotes         NVARCHAR (4000) = NULL
          , @NewWatermarkDate     DATE            = NULL
          , @NewOverlapDays       INT             = NULL
          , @NewIsEnabled         BIT             = NULL
          , @NewNotes             NVARCHAR (4000) = NULL
          , @NotesSupplied        BIT             = 0
          , @DateChanged          BIT             = 0
          , @RowsChanged          INT             = 0
          , @RunsStamped          INT             = 0
          , @Failure              NVARCHAR (2048) = NULL;

    -- A note is "supplied" when it has non-whitespace content. An empty string is a request to CLEAR
    -- the stored note, which is the opposite of supplying one, so it does not satisfy the attribution
    -- requirement below.
    SET @NotesSupplied = CASE WHEN @Notes IS NULL                   THEN 0
                              WHEN LEN (LTRIM (RTRIM (@Notes))) = 0 THEN 0
                              ELSE 1
                         END;

    -- Identifiers, dates and counts only. The note TEXT is excluded by name -- see the header.
    SET @KeyParameters = CONCAT (N'FeedName=', COALESCE (@FeedName, N'(null)')
                               , N', ActivityLocation=', COALESCE (@ActivityLocation, N'(null)')
                               , N', WatermarkDate=', COALESCE (CONVERT (NVARCHAR (10), @WatermarkDate, 23), N'(keep)')
                               , N', ClearWatermark=', COALESCE (CAST (@ClearWatermark AS NVARCHAR (1)), N'(null)')
                               , N', LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(none)')
                               , N', OverlapDays=', COALESCE (CAST (@OverlapDays AS NVARCHAR (11)), N'(keep)')
                               , N', IsEnabled=', COALESCE (CAST (@IsEnabled AS NVARCHAR (1)), N'(keep)')
                               , N', AllowRewind=', COALESCE (CAST (@AllowRewind AS NVARCHAR (1)), N'(null)')
                               , N', NotesSupplied=', @NotesSupplied);

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
        -- 1. Validation that needs no row. Before BEGIN TRANSACTION, as in every procedure here.
        -- ------------------------------------------------------------------------------------------
        IF @FeedName IS NULL
           OR LEN (LTRIM (RTRIM (@FeedName))) = 0
           OR @ActivityLocation IS NULL
           OR LEN (LTRIM (RTRIM (@ActivityLocation))) = 0
        BEGIN
            SET @Failure = N'@FeedName and @ActivityLocation are both required and default to '
                         + N'''HandlerSource'' and ''MD''. They are the natural key of the row being '
                         + N'changed, so a blank one does not identify a watermark at all.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @ClearWatermark = 1
           AND @WatermarkDate IS NOT NULL
        BEGIN
            SET @Failure = CONCAT (N'@ClearWatermark = 1 was passed together with @WatermarkDate = '
                                 , CONVERT (NVARCHAR (10), @WatermarkDate, 23)
                                 , N', which asks for two different things at once: reset the watermark to '
                                 , N'"never loaded", and set it to a date. Pass @ClearWatermark = 1 alone '
                                 , N'to force a full re-load, or @WatermarkDate alone to move it.');
            ;THROW 50000, @Failure, 1;
        END;

        IF @ClearWatermark = 1
           AND @NotesSupplied = 0
        BEGIN
            SET @Failure = N'@Notes is required when @ClearWatermark = 1. Clearing the watermark makes the '
                         + N'next run a FULL load of every handler in the activity location, and a reset '
                         + N'with no explanation is indistinguishable from a mistake six months from now. '
                         + N'Say why.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @AllowRewind = 1
           AND @NotesSupplied = 0
        BEGIN
            SET @Failure = N'@Notes is required when @AllowRewind = 1. A rewind re-fetches every day '
                         + N'between the new date and the old one, and the reason it was needed is the '
                         + N'only part of that a later reader cannot reconstruct.';
            ;THROW 50000, @Failure, 1;
        END;

        -- The freeze-forever case, and the one that looks most like working: every later incremental
        -- asks for changes since a date that has not happened, gets nothing, and reports success.
        IF @WatermarkDate IS NOT NULL
           AND @WatermarkDate > @TodayUtc
        BEGIN
            SET @Failure = CONCAT (N'@WatermarkDate = ', CONVERT (NVARCHAR (10), @WatermarkDate, 23)
                                 , N' is later than today (', CONVERT (NVARCHAR (10), @TodayUtc, 23)
                                 , N' UTC). A future watermark freezes the mirror: every subsequent '
                                 , N'incremental asks for changes since a date that has not happened, '
                                 , N'receives nothing, and reports success. The ceiling is the UTC date, '
                                 , N'which is what config.uspGetLoadWatermark uses for RecommendedToDate.');
            ;THROW 50000, @Failure, 1;
        END;

        -- CK_config_LoadWatermark_OverlapDays would also catch this, as error 547 naming a constraint.
        -- The explicit test names the parameter and says what the bound is for.
        IF @OverlapDays IS NOT NULL
           AND @OverlapDays NOT BETWEEN 0 AND 365
        BEGIN
            SET @Failure = CONCAT (N'@OverlapDays = ', @OverlapDays, N' is outside 0 to 365. The bound is a '
                                 , N'sanity check rather than a policy: 0 disables the overlap, and a '
                                 , N'fat-fingered 3650 would quietly turn every scheduled incremental into '
                                 , N'a near-full load.');
            ;THROW 50000, @Failure, 1;
        END;

        -- ------------------------------------------------------------------------------------------
        -- 2. If a run is named, it must be one that is entitled to move the watermark.
        -- ------------------------------------------------------------------------------------------
        IF @LoadRunId IS NOT NULL
        BEGIN
            SELECT @RunStatus = Status
              FROM logs.LoadRun
             WHERE LoadRunId = @LoadRunId
               AND IsDeleted = 0;

            IF @RunStatus IS NULL
            BEGIN
                SET @Failure = CONCAT (N'@LoadRunId = ', @LoadRunId, N' is not an active load run. Either '
                                     , N'it never existed, or it has been soft-deleted -- and a watermark '
                                     , N'cannot be attributed to history that has been withdrawn.');
                ;THROW 50000, @Failure, 1;
            END;

            IF @RunStatus NOT IN (N'Running', N'Succeeded')
            BEGIN
                SET @Failure = CONCAT (N'Load run ', @LoadRunId, N' has status ''', @RunStatus, N''', so it '
                                     , N'cannot advance the watermark. Only a run that is still in flight '
                                     , N'or that succeeded may: advancing on behalf of a ''Failed'', '
                                     , N'''Abandoned'' or ''PartiallySucceeded'' run moves the watermark '
                                     , N'past records that were never loaded, the gap closes behind it, '
                                     , N'and no later run looks there again. ''Running'' is allowed '
                                     , N'because the ordinary sequence is to advance first and call '
                                     , N'logs.uspCompleteLoadRun second.');
                ;THROW 50000, @Failure, 1;
            END;
        END;

        SET @ContextMessage = CONCAT (N'FeedName=', @FeedName, N', ActivityLocation=', @ActivityLocation
                                    , N', ClearWatermark=', @ClearWatermark
                                    , N', AllowRewind=', @AllowRewind
                                    , N', LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(none)'));

        BEGIN TRANSACTION;

        -- ------------------------------------------------------------------------------------------
        -- 3. Read the row under an update lock, so two concurrent advances serialise. UPDLOCK alone
        --    is enough: the row is required to exist, so there is no absence to lock a range around
        --    -- unlike logs.uspStartLoadRun, which needs HOLDLOCK for exactly that reason.
        -- ------------------------------------------------------------------------------------------
        SELECT @LoadWatermarkId      = w.LoadWatermarkId
             , @CurrentWatermarkDate = w.WatermarkDate
             , @CurrentOverlapDays   = w.OverlapDays
             , @CurrentIsEnabled     = w.IsEnabled
             , @CurrentNotes         = w.Notes
          FROM config.LoadWatermark AS w WITH (UPDLOCK)
         WHERE w.FeedName         = @FeedName
           AND w.ActivityLocation = @ActivityLocation
           AND w.IsDeleted        = 0;

        IF @LoadWatermarkId IS NULL
        BEGIN
            SET @Failure = CONCAT (N'There is no active watermark for feed ''', @FeedName
                                 , N''' and activity location ''', @ActivityLocation
                                 , N'''. This procedure does not create one: 340_config.LoadWatermark.sql '
                                 , N'seeds ''HandlerSource'' / ''MD'' and anything else is inserted '
                                 , N'deliberately. An upsert here would let a typo''d feed name start its '
                                 , N'own watermark, advance it, and look healthy while the feed it was '
                                 , N'meant to track never moved.');
            ;THROW 50000, @Failure, 1;
        END;

        -- ------------------------------------------------------------------------------------------
        -- 4. What the row should become. NULL means "leave it" on every parameter; the two columns
        --    where NULL already carries meaning have their own flag or convention instead.
        -- ------------------------------------------------------------------------------------------
        SET @NewWatermarkDate = CASE WHEN @ClearWatermark  = 1       THEN NULL
                                     WHEN @WatermarkDate IS NOT NULL THEN @WatermarkDate
                                     ELSE @CurrentWatermarkDate
                                END;

        SET @NewOverlapDays = COALESCE (@OverlapDays, @CurrentOverlapDays);
        SET @NewIsEnabled   = COALESCE (@IsEnabled,   @CurrentIsEnabled);

        -- NULL leaves the note, an empty string clears it. The loader's ordinary advance sends nothing,
        -- so an operator's explanation is not erased by the next scheduled run.
        SET @NewNotes = CASE WHEN @Notes IS NULL                   THEN @CurrentNotes
                             WHEN LEN (LTRIM (RTRIM (@Notes))) = 0 THEN NULL
                             ELSE @Notes
                        END;

        -- IS DISTINCT FROM rather than <>, because both sides are nullable and <> is UNKNOWN when
        -- either is NULL -- which would read as "unchanged" on exactly the transition that matters.
        SET @DateChanged = CASE WHEN @NewWatermarkDate IS DISTINCT FROM @CurrentWatermarkDate
                                THEN 1 ELSE 0
                           END;

        -- ------------------------------------------------------------------------------------------
        -- 5. The two guards that need both the old and the new value.
        -- ------------------------------------------------------------------------------------------
        IF @AllowRewind = 0
           AND @CurrentWatermarkDate IS NOT NULL
           AND @NewWatermarkDate     IS NOT NULL
           AND @NewWatermarkDate < @CurrentWatermarkDate
        BEGIN
            SET @Failure = CONCAT (N'Moving the watermark back from '
                                 , CONVERT (NVARCHAR (10), @CurrentWatermarkDate, 23), N' to '
                                 , CONVERT (NVARCHAR (10), @NewWatermarkDate, 23)
                                 , N' needs @AllowRewind = 1 and a reason in @Notes. A rewind is safe for '
                                 , N'the data -- it re-fetches -- but an ACCIDENTAL one, a loader passing '
                                 , N'a stale variable, re-fetches every intervening day on every run: '
                                 , N'thousands of requests, silently, while looking like normal '
                                 , N'operation.');
            ;THROW 50000, @Failure, 1;
        END;

        IF @DateChanged  = 1
           AND @LoadRunId IS NULL
           AND @NotesSupplied = 0
        BEGIN
            SET @Failure = N'A change to WatermarkDate must be attributable: pass @LoadRunId for a change '
                         + N'made by a load run, or @Notes for one made by a person. From the column''s own '
                         + N'description -- a watermark moved by hand with no explanation is '
                         + N'indistinguishable from one moved by mistake.';
            ;THROW 50000, @Failure, 1;
        END;

        -- ------------------------------------------------------------------------------------------
        -- 6. Apply, but only if something would actually change. Re-applying identical values would
        --    move auditModifiedDateUtc and make the audit trail claim the configuration changed
        --    today -- the same rule the seed in 340 follows, applied to a procedure. It matters here
        --    because a retried call is normal: the completion UPDATE below runs after the COMMIT, so
        --    a call whose work committed can still be reported to the caller as failed.
        --
        --    LastAdvanced* are stamped only when the DATE moved. They mean "when the watermark last
        --    advanced", not "when this row was last touched" -- auditModifiedDateUtc means that. And
        --    on an operator's change LastAdvancedByLoadRunId becomes NULL rather than keeping the
        --    previous run's id, because a NULL there honestly says "not a run" and @Notes says who.
        -- ------------------------------------------------------------------------------------------
        IF @NewWatermarkDate IS DISTINCT FROM @CurrentWatermarkDate
           OR @NewOverlapDays <> @CurrentOverlapDays
           OR @NewIsEnabled   <> @CurrentIsEnabled
           OR @NewNotes IS DISTINCT FROM @CurrentNotes
        BEGIN
            UPDATE config.LoadWatermark
               SET WatermarkDate           = @NewWatermarkDate
                 , OverlapDays             = @NewOverlapDays
                 , IsEnabled               = @NewIsEnabled
                 , Notes                   = @NewNotes
                 , LastAdvancedByLoadRunId = CASE WHEN @DateChanged = 1
                                                  THEN @LoadRunId
                                                  ELSE LastAdvancedByLoadRunId
                                             END
                 , LastAdvancedDateUtc     = CASE WHEN @DateChanged = 1
                                                  THEN @NowUtc
                                                  ELSE LastAdvancedDateUtc
                                             END
                 , auditModifiedBy         = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc    = @NowUtc
             WHERE LoadWatermarkId = @LoadWatermarkId;

            -- @@ROWCOUNT is reset by the next statement, so read it immediately.
            SET @RowsChanged = @@ROWCOUNT;
        END;

        -- ------------------------------------------------------------------------------------------
        -- 7. Record the advance on the run that made it. Same transaction as step 6, which is the
        --    entire reason logs.uspCompleteLoadRun has no @WatermarkAfterDate parameter: one fact,
        --    one writer, and no way for the advance and its audit to disagree.
        -- ------------------------------------------------------------------------------------------
        IF @DateChanged = 1
           AND @LoadRunId IS NOT NULL
        BEGIN
            UPDATE logs.LoadRun
               SET WatermarkAfterDate    = @NewWatermarkDate
                 , auditModifiedBy      = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc = @NowUtc
             WHERE LoadRunId = @LoadRunId
               AND IsDeleted = 0;

            SET @RunsStamped = @@ROWCOUNT;
        END;

        SET @Comments = CONCAT (N'LoadWatermarkId=', @LoadWatermarkId
                              , N', WatermarkBefore=', COALESCE (CONVERT (NVARCHAR (10), @CurrentWatermarkDate, 23), N'(null)')
                              , N', WatermarkAfter=', COALESCE (CONVERT (NVARCHAR (10), @NewWatermarkDate, 23), N'(null)')
                              , N', DateChanged=', @DateChanged
                              , N', OverlapDays=', @NewOverlapDays
                              , N', IsEnabled=', @NewIsEnabled
                              , N', RowsChanged=', @RowsChanged
                              , N', RunsStamped=', @RunsStamped
                              , CASE WHEN @RowsChanged = 0 THEN N', NoOp=1' ELSE N', NoOp=0' END);

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
        -- both need the same unqualified rollback. The rollback is what makes steps 6 and 7 one fact:
        -- neither the advance nor its audit survives without the other.
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
        -- loader could no longer tell a deadlock from a refused advance. The leading semicolon is
        -- required: a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSetLoadWatermark'
    , @Description = N'Advances the incremental watermark and records the advance on the run that made it, both in ONE transaction -- the only procedure that may write config.LoadWatermark.WatermarkDate or logs.LoadRun.WatermarkAfterDate. They are one fact; split across two calls they could disagree, and a watermark advanced but not audited is how a regulatory mirror loses a day with nothing in the record to say so. Every guard here restates one sentence: advancing past records that were never fetched closes the gap behind the watermark and no later run looks there again. So a FUTURE date is refused (it freezes the mirror while reporting success); a run may only stamp the watermark while ''Running'' or after it ''Succeeded'', never on behalf of a ''Failed'', ''Abandoned'' or ''PartiallySucceeded'' one; a BACKWARDS move needs @AllowRewind = 1, because an accidental rewind re-fetches every intervening day on every run while looking normal; and every change to the date must be attributable, to a run through @LoadRunId or to a person through @Notes. @WatermarkDate = NULL means "leave it alone", never "clear it", because NULL on that column already means "never successfully loaded" -- the reset is @ClearWatermark = 1, which also requires @Notes. @Notes = NULL leaves the existing note and an empty string clears it, so the loader''s ordinary advance does not erase an operator''s explanation. The procedure CONVERGES: a call that would change nothing changes nothing, so a retry does not move auditModifiedDateUtc. LastAdvancedByLoadRunId and LastAdvancedDateUtc are stamped only when the date actually moved, and an operator''s change sets the former to NULL rather than keeping the previous run''s id. It never creates a missing row -- an upsert would let a typo''d feed name start its own watermark and look healthy. Also the way OverlapDays is changed and a feed is paused with IsEnabled = 0, which is distinct from retiring it with IsDeleted. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The loader only. Operator changes -- OverlapDays, a pause, a reset -- are made by the developer or
-- DBA in a query window, not through the web app, which is read-only in Phase 1. That the monitor
-- cannot move the watermark is asserted by build/check_permission_posture.py rather than assumed.
--
-- Ownership chaining carries the UPDATE on config.LoadWatermark, the UPDATE on logs.LoadRun and the
-- INSERT on logs.ExecutionLog through this grant, so the loader login holds no direct permission on
-- any of the three tables (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON config.uspSetLoadWatermark TO RCRAInfoLoaderRole;
END;
GO

PRINT N'513: config.uspSetLoadWatermark created or altered, EXECUTE granted to the loader role.';
GO
