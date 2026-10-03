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
ObjectName:   logs.uspGetLoadRunSummary
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

One load run, rolled up: the run's own header and counters, plus what its three child tables actually contain. This is
the screen an operator reaches by clicking a row in logs.uspGetLoadRunPage's grid, and the question it answers is "what
happened in run 412" without making them page through five hundred handler rows to work it out.

It returns EXACTLY ONE ROW, or none. That is the whole of its difference from the other paged reads, and it is why it has
no @Skip, @Take, @SortBy or @SortDescending: there is nothing to page and nothing to sort. The plan groups it with the
paged reads because it is a monitoring read granted to the same login, not because it pages.

========================================================================================================================
Requirements and Key Dependencies:

logs.LoadRun for the header, and its three children for everything derived: logs.HandlerLoadStatus (AR5 per-handler
status), logs.HandlerLoadAttempt (per-attempt HTTP detail), logs.DataQualityObservation (G35 observations). Each is
reached through its LoadRunId-leading index -- IX_logs_HandlerLoadStatus_LoadRunId,
IX_logs_HandlerLoadAttempt_LoadRunId, IX_logs_DataQualityObservation_LoadRunId.

logs.uspRecordExecutionError, for the error half of the AR8 instrumentation block.

EXECUTE is granted to RCRAInfoMonitorRole only, for the same reason as logs.uspGetHandlerLoadStatusPage: the console app
WRITES these counters and does not need its own arithmetic read back to it. The asymmetry is asserted by
build/check_permission_posture.py.

========================================================================================================================
Notes:

ONE WIDE ROW, NOT SEVERAL RESULT SETS, AND THAT IS A DECISION ABOUT THE CALLER. The natural shape for a roll-up is four
result sets -- the run, then a status breakdown, then attempts, then observations -- and it is the wrong one here. DA5's
DbContext is deliberately thin: one method per procedure, materializing one type. Four result sets would need
ExecuteReader and NextResult by hand, in the one place the plan says no hand-written SQL lives, and EF Core cannot
materialize the second and later sets at all through FromSqlRaw. So the breakdown arrives as conditional aggregates in
named columns of a single row, which is wider to read and trivial to bind.

THE COUNTERS AND THE OBSERVED COUNTS ARE BOTH RETURNED AND ARE NEVER RECONCILED INTO ONE NUMBER. logs.LoadRun carries
SourceRecordsInserted, SourceRecordsFailed and the rest: they are what the LOADER SAID it did, written by
logs.uspCompleteLoadRun. The Status* and Outcome* columns here are what the status rows ACTUALLY CONTAIN, counted now. A
summary that averaged, preferred or silently replaced one with the other would destroy the only signal that says the two
disagree -- and they disagree in exactly the cases worth seeing: a run killed mid-flight updates rows and never reaches
its counters, and a run whose completion call failed has counters frozen at zero over a table full of successes.

So CounterDriftDetected is a flag, not a correction. It is 1 when any of the six pairs that should correspond one-to-one
disagree, 0 when all six agree, and NULL while the run is still Running -- where disagreement is not drift but simply
work in progress. Three of the run's counters are deliberately NOT in the comparison: SourceRecordsEnumerated and
SourceRecordsFetched count enumeration and HTTP fetches rather than status rows, and HttpRequestCount counts requests, so
comparing any of them against a row count would raise a permanent false alarm. For an Abandoned run the flag is expected
to be 1; that is the point of it.

THE DERIVED COUNTS IGNORE IsDeleted, AND @IncludeDeleted GOVERNS ONLY THE RUN ITSELF. This is the one place in the
database where a read does not filter IsDeleted = 0, and the reason is specific rather than convenient: a summary of a
run is a statement about what that run DID, and retiring one of its status rows under G7 retention months later does not
change what the run did. If the counts honoured @IncludeDeleted = 0 they would fall below the run's own counters as soon
as retention touched the run, and CounterDriftDetected -- the column that exists to report a real disagreement -- would
start reporting retention instead. Retention is still visible: StatusRowsSoftDeleted, AttemptRowsSoftDeleted and
ObservationRowsSoftDeleted count it explicitly, so "these numbers include rows since retired" is answerable rather than
hidden. @IncludeDeleted therefore has exactly one job here: whether a retired RUN can be summarized at all.

A MISSING RUN IS REFUSED AND AN EMPTY DATABASE IS NOT, WHICH IS AN ASYMMETRY ON PURPOSE. @LoadRunId = 412 is the caller
asserting that run 412 exists; if it does not, returning zero rows would let a monitoring page render an empty summary
for a run id somebody mistyped, and an empty summary reads as a run that did nothing. So it throws, and it distinguishes
the two ways of being absent: no such id at all, or a retired run that @IncludeDeleted = 1 would show. @LoadRunId = NULL
asks a different question -- "summarize the most recent run" -- and on a database where the loader has never run, the
honest answer is that there is no such run. That returns ZERO ROWS rather than throwing, because a new installation is
not a fault and the monitoring page must be able to render "no runs yet" without an error banner.

The empty answer is a real empty RESULT SET rather than an early RETURN, so the column metadata is identical whether or
not a row comes back. A procedure that returns no result set at all on one path and one on another is a procedure EF Core
binds successfully in development and fails on in production.

THIS PROCEDURE RAISES INFORMATIONAL WARNING 8153 -- "Null value is eliminated by an aggregate or other SET operation" --
AND THAT IS EXPECTED RATHER THAN A DEFECT LEFT IN PLACE. Three of the aggregates read genuinely nullable columns:
DurationMs and CompletedDateUtc are NULL until a source record finishes, and RetryAfterSeconds is NULL unless EPA sent
one. Any MIN, MAX or SUM over a column holding a NULL raises 8153 under SET ANSI_WARNINGS ON, which is the setting every
client here connects with.

It is not suppressed, and the tempting fix is the reason to say so out loud. SET ANSI_WARNINGS OFF would silence it in one
line, and it would also stop an arithmetic overflow in the SUMs from raising -- returning NULL instead. The CASTs to
BIGINT above exist precisely so an overflow is impossible rather than silent, and turning the setting off to tidy the
message log would trade a loud failure for a quiet wrong number. Rewriting each aggregate to exclude its own NULLs would
cost three more reads of the same rows to change nothing a caller can observe. The messages are class 0, so ADO.NET
delivers them as InfoMessage events and EF Core ignores them; DA5 needs no handling for them.

WHAT @KeyParameters MAY CONTAIN. Both arguments, in full: a run id and a bit. There is no free-text parameter here and
nothing this procedure receives could be PII. From MDE's own template: do NOT include parameters such as passwords and
Personally Identifiable Information.

FailureMessage IS RETURNED AND IS DISPLAYED, so the rule on logs.LoadRun.FailureMessage is load-bearing rather than
decorative: it must never contain the API key or any credential. This procedure is one of the two places that value
reaches a web page.

THIS READ OPENS NO TRANSACTION AND ITS CATCH ROLLS BACK NOTHING, per the correction of 2026-09-05 -- see
logs.uspGetHandlerLoadStatusPage's header for what that fixed. A procedure rolls back only what it opened, and this one
opens nothing. Note what the absence of a transaction means HERE specifically, because this procedure reads four tables
where the others read one: the four reads are NOT a consistent snapshot of each other. A run that is still Running can
gain a status row between the aggregate below and the projection, so StatusRowCount and the run's own counters can
disagree by a row or two on a live run. That is why CounterDriftDetected is NULL while Running rather than 0 or 1, and it
is the correct trade: the alternative is a snapshot isolation transaction held across four aggregates over the tables the
loader is writing, which is precisely the blocking a monitoring screen must not cause.

========================================================================================================================
Example Usage and Performance:

-- The dashboard's default panel: summarize the most recent run.
EXEC logs.uspGetLoadRunSummary;

-- One run, reached by clicking a row in the grid.
EXEC logs.uspGetLoadRunSummary @LoadRunId = 412;

-- A run that G7 retention has retired, which the default hides.
EXEC logs.uspGetLoadRunSummary @LoadRunId = 12, @IncludeDeleted = 1;

Four statements: a singleton seek on PK_logs_LoadRun, then one range seek per child table on its LoadRunId-leading index.

When @LoadRunId is omitted there is one more, and it is a TOP (1) BACKWARD SCAN of
IX_logs_LoadRun_StartedDateUtc rather than a seek -- "the latest" has no seek predicate. It costs one row anyway, and it
needs no sort: the index keys StartedDateUtc alone, but a nonclustered index carries the clustering key as its row
locator, so a descending scan already yields StartedDateUtc DESC then LoadRunId DESC -- which is exactly the ORDER BY
below. The IsDeleted = 0 predicate is a residual on that scan, and at TOP (1) that is a lookup or two, not a table's
worth.

The three child aggregates each read every row that run wrote, which is inherent to a roll-up and is the cost worth
naming rather than hiding: for a full-Maryland run that is one row per source record in logs.HandlerLoadStatus and at
least one per attempt in logs.HandlerLoadAttempt. Only IX_logs_DataQualityObservation_LoadRunId comes close to covering
what is asked of it (LoadRunId, Severity, ObservationType); the other two aggregate columns that are not in the index, so
each seek carries key lookups. That is measured in F2 rather than pre-optimised, and if it needs fixing the remedy is
INCLUDE columns on the existing indexes, NOT a change of shape and NOT a materialized summary table -- a stored roll-up
would be a fourth writer of numbers that already exist twice.

There is no OPTION (RECOMPILE) here, and its absence is deliberate. It earns its keep on the paged reads because a
dozen optional filters make one plan wrong for the next call; this procedure has one parameter that reaches a WHERE
clause and it is always an equality on a leading index key, so the cached plan is the right plan every time.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the third of the seven monitoring reads and
											the first that is not paged. Error-only instrumentation per [R15]; no
											transaction and no ROLLBACK per the 2026-09-05 correction to 500 and 501.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspGetLoadRunSummary
    -- NULL means "the most recent run", which is what a dashboard panel wants and is the only default
    -- that is useful before anyone has clicked anything.
      @LoadRunId      INT = NULL
    -- Soft-delete visibility, and here it decides only whether a RETIRED RUN may be summarized. It does
    -- not reach the derived counts -- see the header, because that is the surprising half.
    , @IncludeDeleted BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- -------------------------------------------------------------------------------------------------
    -- AR8 instrumentation, ERROR HALF ONLY. This read writes no successful-path row -- a summary panel
    -- refreshed on a timer would bury the load history in the same table it exists to summarize.
    -- @ExecutionId and @EndTimeUtc are absent from this block on purpose: with no start row, nothing
    -- would ever assign them.
    -- -------------------------------------------------------------------------------------------------
    -- Not a fallback for odd cases: this literal is what the monitor login actually logs, because
    -- metadata visibility is denied to it. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetLoadRunSummary]')
          , @StartTimeUtc   DATETIME2      = SYSUTCDATETIME ()
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- -------------------------------------------------------------------------------------------------
    -- This procedure's own state. @ResolvedLoadRunId is the run actually summarized, which differs from
    -- @LoadRunId whenever the caller asked for "the latest" -- and it is what the projection and the
    -- error context both report, so a support question about the wrong run is answerable.
    -- -------------------------------------------------------------------------------------------------
    DECLARE @ResolvedLoadRunId INT             = @LoadRunId
          , @Failure           NVARCHAR (2048) = NULL;

    -- Derived from logs.HandlerLoadStatus. Counts start at 0 rather than NULL: every one of them is an
    -- answer to "how many", and zero is that answer when there are none.
    DECLARE @StatusRowCount         INT    = 0
          , @StatusRowsSoftDeleted  INT    = 0
          , @StatusPending          INT    = 0
          , @StatusInProgress       INT    = 0
          , @StatusSucceeded        INT    = 0
          , @StatusFailed           INT    = 0
          , @StatusSkipped          INT    = 0
          , @OutcomeInserted        INT    = 0
          , @OutcomeUpdated         INT    = 0
          , @OutcomeUnchanged       INT    = 0
          , @OutcomeSoftDeleted     INT    = 0
          , @OutcomeNotSet          INT    = 0
          , @DistinctHandlerCount   INT    = 0
          , @AttemptCountTotal      BIGINT = 0
          , @OurFailureCount        INT    = 0
          , @DurationMeasuredCount  INT    = 0
          , @TotalDurationMs        BIGINT = 0
          , @MaxDurationMs          INT    = NULL
          , @FirstSeenMinDateUtc    DATETIME2 = NULL
          , @LastCompletedDateUtc   DATETIME2 = NULL;

    -- Derived from logs.HandlerLoadAttempt.
    DECLARE @AttemptRowCount        INT    = 0
          , @AttemptRowsSoftDeleted INT    = 0
          , @AttemptSucceeded       INT    = 0
          , @AttemptFailed          INT    = 0
          , @AttemptThrottled       INT    = 0
          , @AttemptTimedOut        INT    = 0
          , @AttemptCancelled       INT    = 0
          , @MaxAttemptNumber       INT    = NULL
          , @ResponseBytesTotal     BIGINT = 0
          , @Http4xxCount           INT    = 0
          , @Http5xxCount           INT    = 0
          , @MaxRetryAfterSeconds   INT    = NULL;

    -- Derived from logs.DataQualityObservation.
    DECLARE @ObservationRowCount        INT = 0
          , @ObservationRowsSoftDeleted INT = 0
          , @ObservationErrors          INT = 0
          , @ObservationWarnings        INT = 0
          , @ObservationInfos           INT = 0
          , @ObservationTypeCount       INT = 0;

    -- COALESCE on both, including the integer: CONCAT renders NULL as an empty string, so an omitted
    -- @LoadRunId would log as `LoadRunId=,` and read as a truncated message rather than as a NULL.
    -- This is what the CALLER sent; the run actually summarized goes into @ContextMessage below.
    SET @KeyParameters = CONCAT (N'LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(latest)')
                               , N', IncludeDeleted=', @IncludeDeleted);

    BEGIN TRY

        -- ==========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ====
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 1. Validation, then resolution of the run to summarize. First, and before any reading, so a
        --    refusal costs nothing but the parse.
        -- ------------------------------------------------------------------------------------------
        -- LoadRunId is an IDENTITY starting at 1, so a zero or a negative is not a run that was
        -- retired or has not happened yet -- it is a caller sending a sentinel, most often the 0 an
        -- uninitialised integer field arrives as. Refused rather than looked up and reported missing,
        -- because "run 0 does not exist" sends the reader looking for a run instead of for the bug.
        IF @LoadRunId IS NOT NULL AND @LoadRunId <= 0
        BEGIN
            SET @Failure = CONCAT (N'@LoadRunId = ', @LoadRunId, N' is not a possible run id. ')
                         + N'logs.LoadRun.LoadRunId is an IDENTITY starting at 1, so a value of zero '
                         + N'or below is an uninitialised or sentinel argument rather than a run that '
                         + N'is missing. Omit @LoadRunId to summarize the most recent run.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @LoadRunId IS NULL
        BEGIN
            -- The most recent run BY START TIME, with the id as the tiebreaker. Not MAX (LoadRunId):
            -- the id is the order rows were created, and logs.uspStartLoadRun's resume path can create
            -- a run that stands for an earlier window. StartedDateUtc is what "most recent" means to
            -- the operator reading the panel, and the two agree in the ordinary case anyway.
            SELECT TOP (1) @ResolvedLoadRunId = r.LoadRunId
              FROM logs.LoadRun AS r
             WHERE (@IncludeDeleted = 1 OR r.IsDeleted = 0)
             ORDER BY r.StartedDateUtc DESC, r.LoadRunId DESC;

            -- No run at all. Deliberately NOT an error -- see the header: a database where the loader
            -- has never run is a new installation, not a fault, and the panel has to render it.
            -- @ResolvedLoadRunId stays NULL, the projection matches nothing, and the caller receives an
            -- empty result set with the same columns it would have received a row in.
            IF @ResolvedLoadRunId IS NULL
            BEGIN
                SET @ContextMessage = N'No load run exists to summarize; returned an empty result set.';
            END;
        END
        ELSE
        BEGIN
            -- The caller asserted this run exists. Two ways for that to be wrong, and they are told
            -- apart because the remedies are different: one is a mistyped id, the other is one
            -- argument away from an answer.
            IF NOT EXISTS (SELECT 1
                             FROM logs.LoadRun AS r
                            WHERE r.LoadRunId = @LoadRunId
                              AND (@IncludeDeleted = 1 OR r.IsDeleted = 0))
            BEGIN
                IF EXISTS (SELECT 1 FROM logs.LoadRun AS r WHERE r.LoadRunId = @LoadRunId)
                BEGIN
                    SET @Failure = CONCAT (N'Load run ', @LoadRunId, N' exists but is soft-deleted, ')
                                 + N'and @IncludeDeleted = 0 hides it. Pass @IncludeDeleted = 1 to '
                                 + N'summarize a retired run. Nothing in this database is ever hard '
                                 + N'deleted, so the run and its history are still there.';
                END
                ELSE
                BEGIN
                    SET @Failure = CONCAT (N'Load run ', @LoadRunId, N' does not exist. Refused ')
                                 + N'rather than returned as an empty summary, because an empty '
                                 + N'summary reads as a run that did nothing rather than as a run id '
                                 + N'that was never issued.';
                END;
                ;THROW 50000, @Failure, 1;
            END;
        END;

        -- No BEGIN TRANSACTION, and the omission is deliberate -- see the header. Nothing here writes,
        -- the four reads below gain no consistency from READ COMMITTED that they could lose, and a
        -- transaction this procedure opened would have to be rolled back by a CATCH that cannot
        -- safely do it.

        -- ------------------------------------------------------------------------------------------
        -- 2. The three child aggregates. Skipped entirely when there is no run to summarize -- not for
        --    speed, but because running an aggregate over `LoadRunId = NULL` reads as a mistake to
        --    anyone maintaining this and would have to be explained every time.
        --
        --    None of the three filters IsDeleted. That is the one deliberate exception to the blanket
        --    read rule in this database and the header argues it: these are counts of what the RUN
        --    did, and retention retiring a row later does not change that. The *SoftDeleted columns
        --    report the retirement instead of hiding it.
        -- ------------------------------------------------------------------------------------------
        IF @ResolvedLoadRunId IS NOT NULL
        BEGIN
            -- logs.HandlerLoadStatus. The Status and Outcome breakdowns duplicate
            -- CK_logs_HandlerLoadStatus_Status and CK_logs_HandlerLoadStatus_Outcome, and OutcomeNotSet
            -- is the NULL that CK_logs_HandlerLoadStatus_Outcome also permits: without a column for it
            -- the four Outcome counts would silently fail to sum to StatusRowCount, which is the kind
            -- of arithmetic a reader blames on the query rather than on the data.
            SELECT @StatusRowCount        = COUNT (*)
                 , @StatusRowsSoftDeleted = COALESCE (SUM (CASE WHEN s.IsDeleted = 1                THEN 1 ELSE 0 END), 0)
                 , @StatusPending         = COALESCE (SUM (CASE WHEN s.Status  = N'Pending'         THEN 1 ELSE 0 END), 0)
                 , @StatusInProgress      = COALESCE (SUM (CASE WHEN s.Status  = N'InProgress'      THEN 1 ELSE 0 END), 0)
                 , @StatusSucceeded       = COALESCE (SUM (CASE WHEN s.Status  = N'Succeeded'       THEN 1 ELSE 0 END), 0)
                 , @StatusFailed          = COALESCE (SUM (CASE WHEN s.Status  = N'Failed'          THEN 1 ELSE 0 END), 0)
                 , @StatusSkipped         = COALESCE (SUM (CASE WHEN s.Status  = N'Skipped'         THEN 1 ELSE 0 END), 0)
                 , @OutcomeInserted       = COALESCE (SUM (CASE WHEN s.Outcome = N'Inserted'        THEN 1 ELSE 0 END), 0)
                 , @OutcomeUpdated        = COALESCE (SUM (CASE WHEN s.Outcome = N'Updated'         THEN 1 ELSE 0 END), 0)
                 , @OutcomeUnchanged      = COALESCE (SUM (CASE WHEN s.Outcome = N'Unchanged'       THEN 1 ELSE 0 END), 0)
                 , @OutcomeSoftDeleted    = COALESCE (SUM (CASE WHEN s.Outcome = N'SoftDeleted'     THEN 1 ELSE 0 END), 0)
                 , @OutcomeNotSet         = COALESCE (SUM (CASE WHEN s.Outcome IS NULL              THEN 1 ELSE 0 END), 0)
                 -- Our own failures, counted by the presence of the pointer rather than by status: a row
                 -- can be Failed because EPA refused the fetch, which is not a fault on this side and
                 -- has no logs.ExecutionLog row. The two numbers side by side separate the two.
                 , @OurFailureCount       = COALESCE (SUM (CASE WHEN s.ExecutionLogId IS NOT NULL   THEN 1 ELSE 0 END), 0)
                 -- CAST to BIGINT before summing, not after: AttemptCount is an INT per row and the sum
                 -- over a full-Maryland run can exceed 2147483647, which would abort the whole summary
                 -- with an arithmetic overflow rather than return a large number.
                 , @AttemptCountTotal     = COALESCE (SUM (CAST (s.AttemptCount AS BIGINT)), 0)
                 -- The denominator is returned with the total instead of an AVG, because AVG of an INT
                 -- truncates and because DurationMs is NULL for every row that has not finished: an
                 -- average over an unstated denominator is a number nobody can check.
                 , @DurationMeasuredCount = COALESCE (SUM (CASE WHEN s.DurationMs IS NOT NULL THEN 1 ELSE 0 END), 0)
                 , @TotalDurationMs       = COALESCE (SUM (CAST (s.DurationMs AS BIGINT)), 0)
                 , @MaxDurationMs         = MAX (s.DurationMs)
                 , @FirstSeenMinDateUtc   = MIN (s.FirstSeenDateUtc)
                 , @LastCompletedDateUtc  = MAX (s.CompletedDateUtc)
              FROM logs.HandlerLoadStatus AS s
             WHERE s.LoadRunId = @ResolvedLoadRunId;

            -- Distinct handlers is its own statement rather than a COUNT (DISTINCT) beside the
            -- conditional aggregates above: a run holds one row per source record and a handler can own
            -- several, so "how many handlers" and "how many rows" are different questions and the
            -- second is the one StatusRowCount answers.
            SELECT @DistinctHandlerCount = COUNT (DISTINCT s.HandlerId)
              FROM logs.HandlerLoadStatus AS s
             WHERE s.LoadRunId = @ResolvedLoadRunId;

            -- logs.HandlerLoadAttempt. The Outcome breakdown duplicates
            -- CK_logs_HandlerLoadAttempt_Outcome, which is a five-value set of its own and not the same
            -- set as the status table's.
            SELECT @AttemptRowCount        = COUNT (*)
                 , @AttemptRowsSoftDeleted = COALESCE (SUM (CASE WHEN a.IsDeleted = 1            THEN 1 ELSE 0 END), 0)
                 , @AttemptSucceeded       = COALESCE (SUM (CASE WHEN a.Outcome = N'Succeeded'   THEN 1 ELSE 0 END), 0)
                 , @AttemptFailed          = COALESCE (SUM (CASE WHEN a.Outcome = N'Failed'      THEN 1 ELSE 0 END), 0)
                 , @AttemptThrottled       = COALESCE (SUM (CASE WHEN a.Outcome = N'Throttled'   THEN 1 ELSE 0 END), 0)
                 , @AttemptTimedOut        = COALESCE (SUM (CASE WHEN a.Outcome = N'TimedOut'    THEN 1 ELSE 0 END), 0)
                 , @AttemptCancelled       = COALESCE (SUM (CASE WHEN a.Outcome = N'Cancelled'   THEN 1 ELSE 0 END), 0)
                 , @MaxAttemptNumber       = MAX (a.AttemptNumber)
                 , @ResponseBytesTotal     = COALESCE (SUM (CAST (a.ResponseBytes AS BIGINT)), 0)
                 -- Two HTTP bands rather than every code: 4xx is "EPA refused us" and 5xx is "EPA
                 -- broke", and those are the two an operator acts on differently. The individual codes
                 -- are one click away in logs.uspGetHandlerLoadStatusPage.
                 , @Http4xxCount           = COALESCE (SUM (CASE WHEN a.HttpStatusCode BETWEEN 400 AND 499 THEN 1 ELSE 0 END), 0)
                 , @Http5xxCount           = COALESCE (SUM (CASE WHEN a.HttpStatusCode BETWEEN 500 AND 599 THEN 1 ELSE 0 END), 0)
                 -- The largest Retry-After EPA asked for, which is the G21 rate-limit question stated in
                 -- seconds. F2 needs it and nothing else in the schema surfaces it per run.
                 , @MaxRetryAfterSeconds   = MAX (a.RetryAfterSeconds)
              FROM logs.HandlerLoadAttempt AS a
             WHERE a.LoadRunId = @ResolvedLoadRunId;

            -- logs.DataQualityObservation. The severity breakdown duplicates
            -- CK_logs_DataQualityObservation_Severity. ObservationTypeCount is how many DISTINCT kinds
            -- of problem the run saw, which is the number that tells an operator whether one thing went
            -- wrong ten thousand times or ten thousand things went wrong once.
            SELECT @ObservationRowCount        = COUNT (*)
                 , @ObservationRowsSoftDeleted = COALESCE (SUM (CASE WHEN o.IsDeleted = 1          THEN 1 ELSE 0 END), 0)
                 , @ObservationErrors          = COALESCE (SUM (CASE WHEN o.Severity = N'Error'    THEN 1 ELSE 0 END), 0)
                 , @ObservationWarnings        = COALESCE (SUM (CASE WHEN o.Severity = N'Warning'  THEN 1 ELSE 0 END), 0)
                 , @ObservationInfos           = COALESCE (SUM (CASE WHEN o.Severity = N'Info'     THEN 1 ELSE 0 END), 0)
                 , @ObservationTypeCount       = COUNT (DISTINCT o.ObservationType)
              FROM logs.DataQualityObservation AS o
             WHERE o.LoadRunId = @ResolvedLoadRunId;

            -- What a later failure report needs: which run was actually summarized, and the two numbers
            -- that say how much work reading it was. @ContextMessage rather than @Comments because
            -- there is no successful-path row for @Comments to reach.
            SET @ContextMessage = CONCAT (N'ResolvedLoadRunId=', @ResolvedLoadRunId
                                        , N', StatusRows=', @StatusRowCount
                                        , N', AttemptRows=', @AttemptRowCount
                                        , N', ObservationRows=', @ObservationRowCount);
        END;

        -- ==========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 3. The projection: one row, or none. Last statement in the block, and there is no COMMIT
        --    here because there was no BEGIN TRANSACTION.
        -- ------------------------------------------------------------------------------------------
        SELECT r.LoadRunId
             , r.RunMode
             , r.ActivityLocation
             , r.RequestedFromDate
             , r.RequestedToDate
             , r.WatermarkBeforeDate
             , r.WatermarkAfterDate
             , r.OverlapDaysApplied
             , r.Status
             , r.StartedDateUtc
             , r.CompletedDateUtc
             -- Measured to CompletedDateUtc when there is one and to now when there is not, and the
             -- companion bit says which. A single NULL would be truthful and useless -- "how long has
             -- this run been going" is the question asked most often about a run that is still going.
             -- DATEDIFF_BIG, not DATEDIFF: a run left Running over a weekend overflows INT
             -- milliseconds, and an arithmetic overflow inside the projection would fail the whole
             -- summary rather than report a large number.
             , DATEDIFF_BIG (MILLISECOND, r.StartedDateUtc
                           , COALESCE (r.CompletedDateUtc, SYSUTCDATETIME ()))     AS ElapsedMs
             , CASE WHEN r.CompletedDateUtc IS NULL THEN 1 ELSE 0 END              AS ElapsedIsProvisional
             , r.ResumedFromLoadRunId
             , r.LookupListsRefreshed
             -- The loader's own counters, as written. See the header: never reconciled with the
             -- observed counts below, only compared.
             , r.SourceRecordsEnumerated
             , r.SourceRecordsFetched
             , r.SourceRecordsInserted
             , r.SourceRecordsUpdated
             , r.SourceRecordsUnchanged
             , r.SourceRecordsSoftDeleted
             , r.SourceRecordsSkipped
             , r.SourceRecordsFailed
             , r.HttpRequestCount
             , r.HttpRetryCount
             -- Displayed on a web page, so logs.LoadRun.FailureMessage's rule is load-bearing: never
             -- the API key, never any credential.
             , r.FailureMessage
             , r.InvokedBy
             , r.MachineName
             , r.ProcessId
             , r.ApplicationVersion
             -- Returned so a panel showing a retired run can mark it, not so the caller can filter on
             -- it: @IncludeDeleted has already decided that.
             , r.IsDeleted
             , r.auditModifiedDateUtc
             -- Observed in logs.HandlerLoadStatus.
             , @StatusRowCount                                                     AS StatusRowCount
             , @StatusRowsSoftDeleted                                              AS StatusRowsSoftDeleted
             , @StatusPending                                                      AS StatusPending
             , @StatusInProgress                                                   AS StatusInProgress
             , @StatusSucceeded                                                    AS StatusSucceeded
             , @StatusFailed                                                       AS StatusFailed
             , @StatusSkipped                                                      AS StatusSkipped
             , @OutcomeInserted                                                    AS OutcomeInserted
             , @OutcomeUpdated                                                     AS OutcomeUpdated
             , @OutcomeUnchanged                                                   AS OutcomeUnchanged
             , @OutcomeSoftDeleted                                                 AS OutcomeSoftDeleted
             , @OutcomeNotSet                                                      AS OutcomeNotSet
             , @DistinctHandlerCount                                               AS DistinctHandlerCount
             , @AttemptCountTotal                                                  AS AttemptCountTotal
             , @OurFailureCount                                                    AS OurFailureCount
             , @DurationMeasuredCount                                              AS DurationMeasuredCount
             , @TotalDurationMs                                                    AS TotalDurationMs
             , @MaxDurationMs                                                      AS MaxDurationMs
             , @FirstSeenMinDateUtc                                                AS FirstSeenMinDateUtc
             , @LastCompletedDateUtc                                               AS LastCompletedDateUtc
             -- Observed in logs.HandlerLoadAttempt.
             , @AttemptRowCount                                                    AS AttemptRowCount
             , @AttemptRowsSoftDeleted                                             AS AttemptRowsSoftDeleted
             , @AttemptSucceeded                                                   AS AttemptSucceeded
             , @AttemptFailed                                                      AS AttemptFailed
             , @AttemptThrottled                                                   AS AttemptThrottled
             , @AttemptTimedOut                                                    AS AttemptTimedOut
             , @AttemptCancelled                                                   AS AttemptCancelled
             , @MaxAttemptNumber                                                   AS MaxAttemptNumber
             , @ResponseBytesTotal                                                 AS ResponseBytesTotal
             , @Http4xxCount                                                       AS Http4xxCount
             , @Http5xxCount                                                       AS Http5xxCount
             , @MaxRetryAfterSeconds                                               AS MaxRetryAfterSeconds
             -- Observed in logs.DataQualityObservation.
             , @ObservationRowCount                                                AS ObservationRowCount
             , @ObservationRowsSoftDeleted                                         AS ObservationRowsSoftDeleted
             , @ObservationErrors                                                  AS ObservationErrorCount
             , @ObservationWarnings                                                AS ObservationWarningCount
             , @ObservationInfos                                                   AS ObservationInfoCount
             , @ObservationTypeCount                                               AS ObservationTypeCount
             -- The flag, not a correction. NULL while the run is Running, where a disagreement is work
             -- in progress rather than drift; otherwise 1 if any of the six pairs that should
             -- correspond one-to-one disagree. Enumerated, Fetched and HttpRequestCount are absent from
             -- the comparison on purpose -- they count enumeration and requests, not status rows.
             , CASE WHEN r.Status = N'Running' THEN NULL
                    WHEN r.SourceRecordsFailed      <> @StatusFailed
                      OR r.SourceRecordsSkipped     <> @StatusSkipped
                      OR r.SourceRecordsInserted    <> @OutcomeInserted
                      OR r.SourceRecordsUpdated     <> @OutcomeUpdated
                      OR r.SourceRecordsUnchanged   <> @OutcomeUnchanged
                      OR r.SourceRecordsSoftDeleted <> @OutcomeSoftDeleted THEN 1
                    ELSE 0 END                                                     AS CounterDriftDetected
          FROM logs.LoadRun AS r
         WHERE r.LoadRunId = @ResolvedLoadRunId;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them, so
        -- capture them before doing anything else -- the CONCAT and the EXEC below both do.
        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        -- NO ROLLBACK. This procedure opens no transaction, so it has nothing of its own to roll back,
        -- and any transaction open at this point belongs to the caller -- see
        -- logs.uspGetHandlerLoadStatusPage's header for the two defects that taught this. A procedure
        -- rolls back only what it opened, and build/check_stored_headers.py enforces it against the
        -- deployed module.

        -- ElapsedMilliseconds must stay NULL on an orphan row -- it is the signature that identifies
        -- one, and a value there would make an orphan indistinguishable from a closed-out row. The
        -- duration is not lost: it goes into @ContextMessage, which an orphan row does carry.
        -- DATEDIFF_BIG clamped by LEAST rather than a bare DATEDIFF, because an overflow HERE would
        -- raise inside the error handler and replace the error being reported with an arithmetic one.
        SET @ContextMessage = CONCAT (@ContextMessage, N', ElapsedMs='
                                    , CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc
                                                               , SYSUTCDATETIME ())
                                                 , CAST (2147483647 AS BIGINT)) AS INT));

        -- @ExecutionLogId = NULL deliberately, so logs.uspRecordExecutionErrorUpdate takes its
        -- orphan-insert branch: there is no start row to update, and [R15] says the error is recorded
        -- either way. This call swallows everything by design, so it cannot mask the error below it.
        EXEC logs.uspRecordExecutionError
              @ProcedureName   = @ProcName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = NULL
            , @ErrorMessage    = @ErrorMsg
            , @ErrorProcedure  = @ErrorProc
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

        -- Bare, so the ORIGINAL error number reaches the caller. RAISERROR would make it 50000 and the
        -- client could no longer tell a deadlock from a bad @LoadRunId. The leading semicolon is
        -- required: a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetLoadRunSummary'
    , @Description = N'One load run rolled up into exactly one row, or none: the run''s own header and counters from logs.LoadRun, plus conditional-aggregate breakdowns of its three child tables -- Status and Outcome counts from logs.HandlerLoadStatus, Outcome and HTTP-band counts from logs.HandlerLoadAttempt, and severity counts from logs.DataQualityObservation. The screen an operator reaches by clicking a row in logs.uspGetLoadRunPage''s grid. It has no paging or sorting parameters because there is nothing to page, and it returns ONE WIDE ROW rather than four result sets because DA5''s DbContext materializes one type per procedure and EF Core cannot materialize a second result set through FromSqlRaw at all. The loader''s counters and the observed counts are BOTH returned and are never reconciled: CounterDriftDetected reports that six pairs which should correspond one-to-one disagree -- which is exactly what a run killed mid-flight or one whose completion call failed looks like -- and is NULL while the run is still Running. The derived counts deliberately do NOT filter IsDeleted, the one such exception in this database, because a summary states what the run DID and retention retiring a row later does not change that; StatusRowsSoftDeleted, AttemptRowsSoftDeleted and ObservationRowsSoftDeleted report the retirement instead of hiding it, and @IncludeDeleted governs only whether a retired RUN may be summarized. A @LoadRunId that does not exist is REFUSED, and a retired one is refused with a different message naming @IncludeDeleted, because an empty summary reads as a run that did nothing; but @LoadRunId omitted on a database with no runs at all returns an EMPTY RESULT SET, because a new installation is not a fault. Error-only AR8 instrumentation: no successful-path log row, and no transaction and no ROLLBACK, per the 2026-09-05 correction. EXECUTE is granted to RCRAInfoMonitorRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The monitor only, and the exclusion of the loader is the argued half: the console app WROTE every
-- counter this procedure reads, through logs.uspCompleteLoadRun and logs.uspUpsertHandlerLoadStatusSet.
-- Reading its own arithmetic back would tell it nothing it does not already know, and it would hand the
-- loader a read over three logs tables it otherwise only writes. Both directions are asserted by
-- build/check_permission_posture.py, so the asymmetry is measured rather than intended.
--
-- Ownership chaining carries the SELECT on logs.LoadRun, logs.HandlerLoadStatus,
-- logs.HandlerLoadAttempt and logs.DataQualityObservation, and the INSERT on logs.ExecutionLog,
-- through this one grant -- so the monitor login holds no direct permission on any of them (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetLoadRunSummary TO RCRAInfoMonitorRole;
END;
GO

PRINT N'502: logs.uspGetLoadRunSummary created or altered, EXECUTE granted to the monitor role.';
GO
