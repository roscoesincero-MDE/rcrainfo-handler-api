-- SET XACT_ABORT ON sits ABOVE the header block deliberately. The GO on the next line ends the batch, and
-- sys.sql_modules stores only the batch that contains CREATE -- so a header placed AFTER this GO is
-- invisible to anyone reading the procedure out of the database through sp_helptext, OBJECT_DEFINITION or
-- SSMS "Script as CREATE", which is where a maintainer actually reads it.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   logs.uspGetHandlerLoadResumeSet
Author:       rsincero
CreateDate:   2026-09-06
========================================================================================================================
Description:

Answers the question a load run asks before it fetches anything: WAS THERE A RUN BEFORE ME THAT DID NOT FINISH, AND WHICH
HANDLER VERSIONS HAD IT ALREADY DEALT WITH? It returns one row per logs.HandlerLoadStatus row of that unfinished run --
every status, not just the finished ones -- with the run's own identity and requested window repeated on each row.

This is D2.7. [R28] settled why it has to exist: /hd/sources/summaries has no offset and no limit, so a killed initial
load cannot restart at a page. There is no cursor to save and nothing to seek to. The only record of how far a run got is
logs.HandlerLoadStatus, and this procedure is the read of it.

The loader's use is two subtractions against the version set the summaries walk produced:

  * a returned row at Status = 'Succeeded' is a version this run must NOT fetch. Script 520's Skip mode is what the
    loader then writes for it -- "the run decided not to fetch this record at all" -- so the resumed run's own grid
    shows the whole population rather than only the part it re-fetched.
  * a returned row at any other status was enumerated and never finished. Those are re-fetched, which the walk would
    have produced anyway; they are returned because their ABSENCE from the walk is a finding. A version the previous
    run knew about and this walk did not produce is the G25 signal -- EPA filtering summaries on a date field this
    loader has assumed -- and it can only be seen by something holding both sets.

========================================================================================================================
Requirements and Key Dependencies:

logs.LoadRun, for the candidate and its window; CK_logs_LoadRun_Status is the authority for the status values named
below. logs.HandlerLoadStatus, seeked through UX_logs_HandlerLoadStatus_Natural (LoadRunId, HandlerId, SourceType,
Sequence) WHERE IsDeleted = 0, which is both the filter and the sort order this procedure needs.

logs.uspRecordExecutionError, for the error half of the AR8 instrumentation block.

EXECUTE is granted to RCRAInfoLoaderRole only. The monitoring web app pages the same table through
logs.uspGetHandlerLoadStatusPage; resuming is not a monitoring question. The asymmetry is asserted by
build/check_permission_posture.py.

========================================================================================================================
Notes:

THIS PROCEDURE RUNS BEFORE logs.uspStartLoadRun, AND THAT ORDERING IS THE WHOLE REASON @AbandonAfterMinutes IS A
PARAMETER HERE. It is the least obvious thing in the file and it was very nearly a silent defect, so it is first.

A killed process leaves its logs.LoadRun row at Status = 'Running' with CompletedDateUtc NULL forever. Nothing sweeps
that row until the NEXT run starts, because the sweep lives in logs.uspStartLoadRun. But the resume set has to be read
BEFORE the new run starts -- logs.uspStartLoadRun takes @ResumedFromLoadRunId as an input and records it on the row it
inserts, so the answer has to exist before the insert. Which means that at the moment this procedure looks, the run it
is being asked about is still 'Running':

    22:00  run 100 starts, Status = 'Running'
    01:30  the machine reboots. Run 100's row is untouched and still says 'Running'.
    22:00  run 101 starts. It reads THIS procedure first -- and run 100 is still 'Running'.
           logs.uspStartLoadRun marks run 100 'Abandoned' a moment later, which is too late.

Read only for the three terminal-but-unfinished statuses, this procedure would have returned nothing on precisely the
occasion it exists for, and run 101 would have re-fetched every one of run 100's several hundred thousand successes. So
a 'Running' run older than @AbandonAfterMinutes IS a candidate here -- the same rule logs.uspStartLoadRun is about to
apply, applied a moment earlier and without writing anything.

  THE THRESHOLD IS NOT DUPLICATED, IT IS PASSED. Both procedures take it as a parameter with the same default of 720
  minutes, and THE LOADER MUST SEND THE SAME VALUE TO BOTH. If it sends a shorter one here, this procedure treats a
  healthy in-flight run as resumable and the second run skips versions the first is still writing; if it sends a longer
  one here, the resume goes back to being the no-op described above. There is no way for either procedure to check the
  other, so the agreement is the loader's to keep -- which is the same arrangement as every other threshold this
  application configures, and the reason both defaults are the same number.

  A 'Running' RUN INSIDE THE THRESHOLD IS NOT A CANDIDATE, and this procedure returns zero rows rather than raising.
  That run may be alive. logs.uspStartLoadRun refuses to start a second run beside it anyway, so the refusal exists and
  belongs there; raising it here as well would mean two procedures reporting one collision, and the loader would see
  the wrong one first.

ZERO ROWS IS AN ANSWER, NOT A FAILURE, AND IT IS THE SAFE ONE. Every "no candidate" path returns an empty set: no run
before this one, the previous run succeeded, the previous run is alive, the previous run is too old to trust. The
loader's reading of an empty set is "fetch everything the walk names", which is correct in all four cases and merely
slower than necessary in none of them -- dbo.uspMergeHandlerSourceBatch reports an unchanged version as Unchanged, so
re-fetching costs requests and changes no data. That asymmetry is the reason this read does not throw where the other
DA4 reads do: here the empty answer is safe, and on a screen whose job is to show failures it is not.

WHY EVERY STATUS COMES BACK AND NOT ONLY 'Succeeded'. Returning the finished versions alone is the smaller read and it
is what the fetch decision needs, so it looked right. It throws away the only measurement of G25 anyone will ever get
for free. The walk re-asks EPA for the same date range the abandoned run asked for -- the watermark did not advance,
which is what logs.uspCompleteLoadRun and config.uspSetLoadWatermark between them guarantee -- so the two version sets
SHOULD be the same set plus whatever changed since. A version in this result that the walk did not name means the
summaries feed no longer reports a record it reported yesterday, and the loader can count that and say so. Nothing else
in the system is in a position to notice it.

  It also makes the returned set the whole of the previous run's enumeration, which is what lets the resumed run's own
  AR5 grid add up: enumerated = fetched + skipped, with the skipped ones explained by the run they came from.

@MaxAgeHours IS A CORRECTNESS GUARD AND NOT HOUSEKEEPING. Skipping a version because a previous run succeeded on it is
a claim that the version has not changed since -- and EPA does update a handler source in place: dbo.HandlerSource
mirrors SrcUpdatedDate for exactly that reason, and the natural key (HandlerId, SourceType, Sequence) does not move when
it happens. So the older the success, the weaker the claim. A resume from last night is sound; a resume from a run three
weeks ago would skip several hundred thousand versions on the strength of three-week-old fetches and report a clean
load. 48 hours is the default because the schedule is nightly and a run killed at 01:30 is resumed at 22:00 the same
day, which is well inside it, while two consecutive failures still resume from the first. NULL means no limit, which is
a deliberate escape hatch for an operator re-driving a long initial load by hand.

  @MaxAgeHours APPLIES TO AN AUTOMATIC CANDIDATE AND NOT TO A NAMED ONE. @LoadRunId is how a human says "resume that
  one", and a human naming a three-week-old run has made the decision the guard exists to make automatically. The age
  is recorded in @ContextMessage either way, so the choice is visible after the fact.

  SETTING @MaxAgeHours BELOW @AbandonAfterMinutes / 60 MAKES THE 'Running' BRANCH UNREACHABLE -- a run cannot be both
  older than twelve hours and newer than one. That is not refused, because both parameters are individually meaningful
  and the combination is only wasteful, but it is worth knowing before wondering why a resume stopped happening.

A NAMED @LoadRunId IS VALIDATED THREE WAYS AND TWO OF THEM THROW. It must exist and not be soft-deleted; it must belong
to @ActivityLocation; and it must not be 'Running' inside the abandonment threshold. The activity-location check is the
one that matters most and it is the one nobody would think to write: resuming a Delaware run into a Maryland run would
skip a set of versions that has nothing to do with the population being loaded, and every skipped version would look
like a success in the grid. A mismatch is a caller defect with no safe reading, so it raises.

  A NAMED RUN AT 'Succeeded' IS HONOURED RATHER THAN REFUSED. It is a strange thing to ask for and a coherent one: it
  is how an operator re-drives a window while skipping what a good run already did.

WHAT @KeyParameters MAY CONTAIN. An activity location, a run number and two thresholds. There is no free-text parameter
on this procedure and nothing here is PII. From MDE's own template: do NOT include parameters such as passwords and
Personally Identifiable Information.

INSTRUMENTATION IS ERROR-ONLY, which is the policy MDE settled at the DA1 review and the arrangement every DA4 read
carries. No logs.ExecutionLog row is opened on the successful path; the CATCH always records, because [R15] -- a
procedure whose body is one SELECT can still fail, and in another MDE application one did, through a UDF, and nothing
was written anywhere. The error row is an ORPHAN by design and @ContextMessage says so, or every read failure would read
as a second defect in the logging chain. @ExecutionId and @EndTimeUtc are absent from the declare block on purpose: with
no start row, nothing would assign them.

THIS READ OPENS NO TRANSACTION AND ITS CATCH DOES NOT ROLL BACK. The correction made to logs.uspGetHandlerLoadStatusPage
on 2026-09-05 applies here unchanged and is not re-argued: XACT_STATE () <> 0 is true when the CALLER has a transaction
open, every refusal below throws before any work, and ROLLBACK is illegal inside INSERT ... EXEC. A procedure rolls back
only what it opened. build/check_stored_headers.py holds the rule against the deployed module.

========================================================================================================================
Example Usage and Performance:

-- What the loader asks before it starts a run. Both thresholds at their defaults, which the loader must
-- also be sending to logs.uspStartLoadRun.
EXEC logs.uspGetHandlerLoadResumeSet @ActivityLocation = N'MD';

-- The same question with the loader's configured abandonment threshold, which is the real call shape.
EXEC logs.uspGetHandlerLoadResumeSet @ActivityLocation     = N'MD'
                                   , @AbandonAfterMinutes  = 720
                                   , @MaxAgeHours          = 48;

-- An operator re-driving one particular run's leftovers. The age guard does not apply to a named run.
EXEC logs.uspGetHandlerLoadResumeSet @ActivityLocation = N'MD', @LoadRunId = 100;

-- How much of run 100 this would skip, before deciding to resume from it.
SELECT Status, COUNT (*) AS Versions
  FROM logs.HandlerLoadStatus
 WHERE LoadRunId = 100
   AND IsDeleted = 0
 GROUP BY Status;

-- The error row a failure leaves, read as the developer:
SELECT ProcedureName, ErrorNumber, ErrorMessage, ContextMessage
  FROM logs.ExecutionLog
 WHERE ProcedureName = N'[logs].[uspGetHandlerLoadResumeSet]'
 ORDER BY ExecutionLogId DESC;

One seek on logs.LoadRun for the candidate -- PK or IX_logs_LoadRun_ActivityLocation, one row either way -- then one
range seek on UX_logs_HandlerLoadStatus_Natural, which is filtered to IsDeleted = 0 and leads with LoadRunId, so it
supplies both the predicate and the ORDER BY with no sort. Status and AttemptCount are not in that index, so the
projection is a key lookup per row, and the row count is the previous run's whole enumeration: for the initial load,
the entire Maryland population.

  THAT IS THE ONE COST WORTH MEASURING IN F2, AND THE REMEDY IS NAMED RATHER THAN APPLIED. If the lookups dominate, add
  INCLUDE (Status, AttemptCount) to UX_logs_HandlerLoadStatus_Natural as a guarded block in
  310_logs.HandlerLoadStatus.sql -- an index belongs to its table's script, not to the procedure that would benefit.
  Nothing is done now because the read happens once per run and the alternative is guessing at an index for a table
  whose real size nobody has seen. There is deliberately no @Take and no cap: the set is bounded by the natural key,
  one row per version per run, so a cap could only truncate a correct answer -- and a truncated resume set silently
  re-fetches, which is the failure that looks like everything working.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-06	rsincero						Initial version. Workstream D2.7, the resume read. Twenty-third procedure and
											the ninth read. Takes @AbandonAfterMinutes because it necessarily runs BEFORE
											logs.uspStartLoadRun's abandonment sweep -- see the header; without it the
											procedure returns nothing on exactly the occasion it exists for. Returns every
											status of the candidate run rather than only 'Succeeded', so that a version the
											previous run knew about and this run's walk does not name is visible to
											somebody (G25).
2026-09-06	rsincero						The automatic candidate search skips RunMode = 'Targeted'. A single-handler run
											is not a population and has no leftovers to resume; as the newest row it would
											have disqualified resume outright and re-fetched an abandoned population run's
											entire recorded progress. The explicit @LoadRunId path is untouched -- naming a
											run is the operator's decision.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspGetHandlerLoadResumeSet
    -- The population being loaded. Scoped, for the reason logs.uspStartLoadRun scopes its own sweep and
    -- concurrency check: two activity locations are two independent loads.
      @ActivityLocation    NVARCHAR (2) = N'MD'
    -- NULL means "find the candidate yourself", which is what the loader sends. A value is a human naming
    -- one run, and it changes two of the rules -- see the header.
    , @LoadRunId           INT          = NULL
    -- The same threshold logs.uspStartLoadRun applies a moment later, and THE LOADER MUST SEND THE SAME
    -- VALUE TO BOTH. Same default for that reason. See the header: this is the parameter that makes a
    -- reboot-killed run resumable at all.
    , @AbandonAfterMinutes INT          = 720
    -- How old a success may be and still be trusted enough to skip. NULL means no limit. A correctness
    -- guard rather than housekeeping -- EPA updates a handler source in place.
    , @MaxAgeHours         INT          = 48
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- -------------------------------------------------------------------------------------------------
    -- AR8 instrumentation, ERROR HALF ONLY -- the policy every DA4 read carries. @ExecutionId and
    -- @EndTimeUtc are absent on purpose: with no start row, nothing would ever assign them.
    -- -------------------------------------------------------------------------------------------------
    -- Not a fallback for odd cases: this literal is what the loader login actually logs, because script
    -- 050 denies it metadata visibility and OBJECT_NAME (@@PROCID) returns NULL for such a principal.
    -- Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetHandlerLoadResumeSet]')
          , @StartTimeUtc   DATETIME2      = SYSUTCDATETIME ()
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- -------------------------------------------------------------------------------------------------
    -- This procedure's own state. @ResumeRunId is the answer to "which run", and NULL means there is no
    -- candidate -- which is a legitimate answer on four separate paths and never an error.
    -- -------------------------------------------------------------------------------------------------
    DECLARE @NowUtc        DATETIME2       = SYSUTCDATETIME ()
          , @ResumeRunId   INT             = NULL
          , @CandidateId   INT             = NULL
          , @Status        NVARCHAR (20)   = NULL
          , @StartedUtc    DATETIME2       = NULL
          , @AgeHours      INT             = NULL
          , @RowsReturned  INT             = 0
          , @Failure       NVARCHAR (2048) = NULL;

    -- COALESCE on every argument, including the integers: CONCAT renders NULL as an empty string, so an
    -- omitted @MaxAgeHours would log as `MaxAgeHours=,` and read as a truncated message rather than as a
    -- NULL. Identifiers and thresholds only, per MDE's template.
    SET @KeyParameters = CONCAT (N'ActivityLocation=', COALESCE (@ActivityLocation, N'(null)')
                               , N', LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(auto)')
                               , N', AbandonAfterMinutes='
                               , COALESCE (CAST (@AbandonAfterMinutes AS NVARCHAR (11)), N'(none)')
                               , N', MaxAgeHours='
                               , COALESCE (CAST (@MaxAgeHours AS NVARCHAR (11)), N'(none)'));

    -- Explains the orphan row before anyone has to wonder about it. logs.uspRecordExecutionErrorUpdate
    -- appends this to the text it writes for a row with no ExecutionLogId, which otherwise reads as a
    -- defect in the logging chain rather than as this procedure working exactly as designed.
    SET @ContextMessage = N'Error-only instrumentation (AR8, DA1 review decision 2): this read does not '
                        + N'open a logs.ExecutionLog row on the successful path, so an orphan error row '
                        + N'is expected here and is not a sign that a start row was lost.';

    BEGIN TRY

        -- ==========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ====
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 1. Validation. First, and before any work -- the position a writing procedure puts it in to
        --    stay outside its own transaction, kept here because a refusal should cost nothing but
        --    the parse.
        -- ------------------------------------------------------------------------------------------
        IF @ActivityLocation IS NULL OR LEN (LTRIM (RTRIM (@ActivityLocation))) = 0
        BEGIN
            SET @Failure = N'@ActivityLocation cannot be NULL or blank. It is what makes the candidate '
                         + N'the previous run of THIS population: two activity locations are two '
                         + N'independent loads, and an unscoped search would offer a Delaware run as '
                         + N'the resume point for a Maryland one. G2 says MD, and the loader reads it '
                         + N'from RCRAInfoLoad:ActivityLocation, which has no default for the same '
                         + N'reason this parameter refuses a blank.';
            ;THROW 50000, @Failure, 1;
        END;

        -- A floor and no ceiling, and the floor is the same 15 minutes logs.uspStartLoadRun enforces on
        -- its sweep -- for the mirror-image reason. There, a shorter threshold abandons a healthy
        -- in-flight run; here it declares one dead and hands its unfinished versions to a second run as
        -- "already done by somebody else", while the first run is still writing them.
        IF @AbandonAfterMinutes IS NOT NULL AND @AbandonAfterMinutes < 15
        BEGIN
            SET @Failure = CONCAT (N'@AbandonAfterMinutes = ', @AbandonAfterMinutes
                                 , N' is below the floor of 15 minutes. A shorter threshold would treat ')
                         + N'a HEALTHY in-flight run as an abandoned one and return its status rows as a '
                         + N'resume set, so a second run would skip versions the first is still fetching. '
                         + N'This is the same floor logs.uspStartLoadRun enforces on the same threshold, '
                         + N'and the loader must send both procedures the same value. Pass NULL to '
                         + N'consider only runs that already reached a terminal status.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @MaxAgeHours IS NOT NULL AND @MaxAgeHours < 1
        BEGIN
            SET @Failure = CONCAT (N'@MaxAgeHours = ', @MaxAgeHours
                                 , N' is not a usable age limit; the minimum is 1. Zero or negative ')
                         + N'would reject every candidate, which returns an empty set and reads as '
                         + N'"nothing to resume" -- a configuration mistake that makes every resumed '
                         + N'run silently re-fetch the whole population. Pass NULL for no limit.';
            ;THROW 50000, @Failure, 1;
        END;

        -- No BEGIN TRANSACTION, and the omission is deliberate -- see the header. Nothing here writes,
        -- READ COMMITTED gives the statements below no shared consistency to lose, and a transaction
        -- this procedure opened would have to be rolled back by a CATCH that cannot safely do it.

        -- ------------------------------------------------------------------------------------------
        -- 2. Which run, if any. Two paths: a human named one, or this procedure finds the previous
        --    run of this population. The named path validates and can throw; the automatic path
        --    qualifies and can only decline.
        -- ------------------------------------------------------------------------------------------
        IF @LoadRunId IS NOT NULL
        BEGIN
            SELECT @CandidateId = r.LoadRunId
                 , @Status      = r.Status
                 , @StartedUtc  = r.StartedDateUtc
              FROM logs.LoadRun AS r
             WHERE r.LoadRunId = @LoadRunId
               AND r.IsDeleted = 0;

            IF @CandidateId IS NULL
            BEGIN
                SET @Failure = CONCAT (N'@LoadRunId = ', @LoadRunId, N' is not an active run. It either ')
                             + N'never existed or has been soft-deleted by retention (G7). This raises '
                             + N'rather than returning an empty set because an empty set means "resume '
                             + N'from nothing", and a caller who NAMED a run would read that as "the run '
                             + N'I asked about finished nothing" -- two answers that are not the same '
                             + N'fact.';
                ;THROW 50000, @Failure, 1;
            END;

            -- The check nobody would think to write, and the one with the worst failure mode. Resuming
            -- one population's run into another's would skip a set of versions that has nothing to do
            -- with the load in progress, and every skipped version would read as a success in the grid.
            IF NOT EXISTS (SELECT 1
                             FROM logs.LoadRun AS r
                            WHERE r.LoadRunId        = @LoadRunId
                              AND r.ActivityLocation = @ActivityLocation)
            BEGIN
                SET @Failure = CONCAT (N'@LoadRunId = ', @LoadRunId, N' does not belong to activity ')
                             + N'location ''' + @ActivityLocation + N'''. A resume set from another '
                             + N'population would tell this run to skip versions that are not in it, '
                             + N'and every one of them would show in the AR5 grid as a handled record. '
                             + N'Name a run of this activity location, or omit @LoadRunId and let the '
                             + N'previous run of this population be found.';
                ;THROW 50000, @Failure, 1;
            END;

            -- A live run, named explicitly. Refused, because the one thing that cannot be resumed is a
            -- run that has not stopped: its Pending rows are work in progress, not leftovers.
            IF @Status = N'Running'
               AND (@AbandonAfterMinutes IS NULL
                    OR @StartedUtc >= DATEADD (MINUTE, -@AbandonAfterMinutes, @NowUtc))
            BEGIN
                SET @Failure = CONCAT (N'@LoadRunId = ', @LoadRunId, N' is still ''Running'' and started ')
                             + N'less than ' + COALESCE (CAST (@AbandonAfterMinutes AS NVARCHAR (11))
                                                       , N'the abandonment threshold')
                             + N' minutes ago, so it may still be fetching. Resuming from a live run '
                             + N'would hand its in-flight versions to a second run as work already '
                             + N'done. Wait for it to finish, or let logs.uspStartLoadRun''s '
                             + N'abandonment sweep mark it ''Abandoned'' first.';
                ;THROW 50000, @Failure, 1;
            END;

            -- Honoured whatever the status now is, including 'Succeeded' -- see the header. And the age
            -- guard does not apply: naming a run is a decision, and this procedure is not the place to
            -- overrule it. The age is recorded below either way.
            SET @ResumeRunId = @CandidateId;
        END;
        ELSE
        BEGIN
            -- The previous run of this population, whatever became of it. TOP (1) on LoadRunId DESC and
            -- not "the newest unfinished run": if the newest run SUCCEEDED there is nothing to resume,
            -- and skipping over it to an older failure would resume from a run whose leftovers a later
            -- run has already dealt with.
            --
            -- 'Targeted' RUNS ARE NOT CANDIDATES, AND THE FILTER IS WHY THIS SELECT CAN USE TOP (1) AT ALL.
            -- A Targeted run is one handler asked for by hand; it is not a population, so it has no
            -- leftovers a later run would want. Left in, it would be the newest row -- and since it
            -- normally succeeds, the qualification below would then find nothing to resume and return an
            -- empty set. The loader reads an empty set as "fetch everything", so a single diagnostic
            -- lookup would silently discard a genuine abandoned population run's several hundred thousand
            -- recorded successes and re-fetch every one of them. That is the whole hazard: not a wrong
            -- answer, a correct-looking one arrived at by ignoring the run that mattered.
            SELECT TOP (1)
                   @CandidateId = r.LoadRunId
                 , @Status      = r.Status
                 , @StartedUtc  = r.StartedDateUtc
              FROM logs.LoadRun AS r
             WHERE r.ActivityLocation = @ActivityLocation
               AND r.RunMode         <> N'Targeted'
               AND r.IsDeleted        = 0
             ORDER BY r.LoadRunId DESC;

            -- Qualification, and every failure of it returns an empty set. See the header: the loader
            -- reads an empty set as "fetch everything the walk names", which is correct in all four
            -- cases and merely slower in none of them.
            IF @CandidateId IS NOT NULL
               AND (@Status IN (N'Abandoned', N'Failed', N'PartiallySucceeded')
                    -- The line the header is about. A 'Running' row older than the threshold is the
                    -- corpse of a killed process, and logs.uspStartLoadRun will mark it 'Abandoned' a
                    -- moment from now -- after this read. Same rule, applied a moment earlier, writing
                    -- nothing. The loader must send both procedures the same threshold.
                    OR (@Status = N'Running'
                        AND @AbandonAfterMinutes IS NOT NULL
                        AND @StartedUtc < DATEADD (MINUTE, -@AbandonAfterMinutes, @NowUtc)))
               -- The age guard, on the automatic path only. Skipping a version because an old run
               -- succeeded on it is a claim that EPA has not changed it since, and EPA updates a handler
               -- source in place.
               AND (@MaxAgeHours IS NULL
                    OR @StartedUtc >= DATEADD (HOUR, -@MaxAgeHours, @NowUtc))
            BEGIN
                SET @ResumeRunId = @CandidateId;
            END;
        END;

        -- Recorded whether or not a candidate qualified, and this is the only place the DECISION is
        -- visible after the fact: a resume that quietly did not happen looks exactly like a first run.
        -- DATEDIFF_BIG clamped by LEAST rather than a bare DATEDIFF, because an overflow here would
        -- raise from inside a diagnostic and replace whatever this procedure was doing.
        SET @AgeHours = CASE WHEN @StartedUtc IS NULL THEN NULL
                            ELSE CAST (LEAST (DATEDIFF_BIG (HOUR, @StartedUtc, @NowUtc)
                                            , CAST (2147483647 AS BIGINT)) AS INT)
                       END;

        SET @ContextMessage = CONCAT (@ContextMessage
                                    , N' CandidateLoadRunId='
                                    , COALESCE (CAST (@CandidateId AS NVARCHAR (11)), N'(none)')
                                    , N', CandidateStatus=', COALESCE (@Status, N'(none)')
                                    , N', CandidateAgeHours='
                                    , COALESCE (CAST (@AgeHours AS NVARCHAR (11)), N'(none)')
                                    , N', ResumedFromLoadRunId='
                                    , COALESCE (CAST (@ResumeRunId AS NVARCHAR (11)), N'(none)'));

        -- ==========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 3. The set, and the last statement in the block. Zero rows when there is no candidate, and
        --    the WHERE handles it without a branch: @ResumeRunId IS NULL matches nothing. No COMMIT,
        --    because there was no BEGIN TRANSACTION -- a client that reads rows slowly holds nothing
        --    but its own cursor.
        -- ------------------------------------------------------------------------------------------
        SELECT r.LoadRunId               AS ResumedFromLoadRunId
             , r.RunMode                 AS ResumedFromRunMode
             -- The status as it stands NOW, which for the reboot case is still 'Running' -- see the
             -- header. The loader logs it, because "resumed from a run that was still marked Running"
             -- is the sentence that explains an unexpected resume, and there is no other trace of it.
             , r.Status                  AS ResumedFromStatus
             , r.StartedDateUtc          AS ResumedFromStartedDateUtc
             -- The window the previous run was TOLD to fetch. The resuming run reads it to check that
             -- its own window covers it: a narrower one would leave the abandoned run's unfinished
             -- versions outside the range nobody will ask EPA about again.
             , r.RequestedFromDate       AS ResumedFromRequestedFromDate
             , r.RequestedToDate         AS ResumedFromRequestedToDate
             -- The version, by the natural key every table and procedure here agrees on.
             , s.HandlerId
             , s.ActivityLocation
             , s.SourceType
             , s.Sequence
             -- The subtraction the caller makes. 'Succeeded' means do not fetch this one; anything else
             -- means it was enumerated and never finished, and is returned so that its absence from
             -- this run's walk can be noticed (G25).
             , s.Status
             -- How many calls the previous run already spent on it. A version that failed three times
             -- last night is a candidate for a shorter leash tonight, and that decision needs the
             -- count.
             , s.AttemptCount
          FROM logs.HandlerLoadStatus AS s
          JOIN logs.LoadRun           AS r ON r.LoadRunId = s.LoadRunId
         WHERE s.LoadRunId = @ResumeRunId
           AND s.IsDeleted = 0
         -- The order UX_logs_HandlerLoadStatus_Natural already supplies, so this costs no sort. It is
         -- specified anyway rather than left to the plan: the caller builds a set from these rows and a
         -- deterministic order is what makes a test over them assertable.
         ORDER BY s.HandlerId
                , s.SourceType
                , s.Sequence;

        SET @RowsReturned = @@ROWCOUNT;

        SET @ContextMessage = CONCAT (@ContextMessage, N', RowsReturned=', @RowsReturned);

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

        -- No ROLLBACK. This procedure opens no transaction, so it has nothing of its own to roll back,
        -- and any transaction open at this point belongs to the caller. See the header: the same
        -- correction was made to logs.uspGetHandlerLoadStatusPage on 2026-09-05 and is not re-argued.

        -- The one column an orphan row cannot fill is ElapsedMilliseconds -- there is no start row to
        -- subtract from, and that NULL is the signature that identifies an orphan, so it must stay NULL.
        -- The duration is not lost: it goes into @ContextMessage, which an orphan row does carry.
        SET @ContextMessage = CONCAT (@ContextMessage, N', ElapsedMs='
                                    , CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc
                                                               , SYSUTCDATETIME ())
                                                 , CAST (2147483647 AS BIGINT)) AS INT));

        -- No re-creation block, and nothing is missing: the instrumented procedures have one because a
        -- rollback destroys the row logs.uspStartExecutionLogging wrote, and this read never wrote one.
        --
        -- @ExecutionLogId = NULL is therefore passed deliberately, and
        -- logs.uspRecordExecutionErrorUpdate takes its orphan-insert branch on purpose. [R15]: the error
        -- is recorded either way. This call swallows everything by design, so it cannot mask the error
        -- below it.
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
        -- loader could no longer tell a deadlock from a named run that does not exist. The leading
        -- semicolon is required: a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetHandlerLoadResumeSet'
    , @Description = N'Returns the resume set for a load run: one row per logs.HandlerLoadStatus row of the previous unfinished run of the same activity location, every status included, with that run''s identity and requested window repeated on each row. This is D2.7, and it exists because [R28] found that /hd/sources/summaries has no offset and no limit -- a killed initial load cannot restart at a page, so the only record of how far a run got is logs.HandlerLoadStatus. A returned row at Status = ''Succeeded'' is a version the resuming run must NOT fetch, and it writes script 520''s Skip mode for it; a row at any other status was enumerated and never finished, and is returned because its ABSENCE from the new run''s summaries walk is the G25 signal that EPA has stopped reporting a record it reported yesterday. @AbandonAfterMinutes is a parameter, and that is the least obvious thing about this procedure: it necessarily runs BEFORE logs.uspStartLoadRun, because that procedure takes @ResumedFromLoadRunId as an input -- so a reboot-killed run is still marked ''Running'' at the moment this read looks, and the abandonment sweep that would fix it has not happened yet. A ''Running'' run older than the threshold is therefore a candidate here, which is the same rule logs.uspStartLoadRun applies a moment later, applied without writing anything; THE LOADER MUST SEND THE SAME VALUE TO BOTH, and both defaults are 720 minutes for that reason. A ''Running'' run INSIDE the threshold is not a candidate, and the floor of 15 minutes is enforced because a shorter one would hand a healthy run''s in-flight versions to a second run as work already done. @MaxAgeHours (default 48, NULL for no limit) is a correctness guard rather than housekeeping: skipping a version because an old run succeeded on it claims EPA has not changed it since, and EPA updates a handler source in place -- it applies to an automatically-found candidate and NOT to one a human names, since naming a run is a decision. The automatic search ignores RunMode = ''Targeted'' rows: a single-handler run started by hand is not a population and has no leftovers to resume, and as the newest row it would have disqualified resume outright -- so one diagnostic lookup would have discarded an abandoned population run''s several hundred thousand recorded successes and re-fetched every one of them. Zero rows is an answer and never a failure: no previous run, a previous run that succeeded, one still alive, or one too old all return an empty set, and the loader reads that as "fetch everything the walk names", which is safe in every case because dbo.uspMergeHandlerSourceBatch reports an unchanged version as Unchanged. A NAMED @LoadRunId is validated three ways and two of them throw -- it must exist and not be soft-deleted, it must belong to @ActivityLocation (resuming one population''s run into another''s would skip versions that are not in the load and show every one as handled), and it must not be a live ''Running'' run. Instrumentation is error-only per the DA1 review, and the CATCH always records ([R15]); the error row is an orphan by design and @ContextMessage says so, and also carries the candidate, its status, its age and the resume decision, which is the only trace a resume that quietly did not happen leaves. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The loader only. The monitoring web app pages the same table through
-- logs.uspGetHandlerLoadStatusPage; how a run decides what to skip is not a monitoring question, and
-- granting a read "in case" is how a permission surface grows without anyone deciding to grow it.
-- Both directions are asserted by build/check_permission_posture.py.
--
-- Ownership chaining carries the SELECT on logs.LoadRun and logs.HandlerLoadStatus and the chained
-- INSERT into logs.ExecutionLog through this grant, so the loader login holds no direct permission on
-- any of the three (AR3). The loader also holds EXECUTE on logs.uspRecordExecutionError from script
-- 354, which is what lets the CATCH block above record at all.
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetHandlerLoadResumeSet TO RCRAInfoLoaderRole;
END;
GO

PRINT N'525: logs.uspGetHandlerLoadResumeSet created or altered, EXECUTE granted to the loader role.';
GO
