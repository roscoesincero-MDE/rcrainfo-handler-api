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
ObjectName:   logs.uspGetHandlerLoadStatusPage
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Returns one page of per-handler load status to the monitoring web app, filtered and sorted, with the total row count of
the filtered set carried on every row. This is the AR5 screen: of the five hundred handlers a run enumerated, which ones
succeeded, which are still Pending, which failed, and -- for the ones that failed -- what EPA said and where our own
error was written.

It is the first of the six paged reads that copy the parameter block logs.uspGetLoadRunPage settled, and it copies it
verbatim: same names, same order, same defaults, the same clamping, the same whitelist-that-throws, the same
CASE-per-column-per-direction ORDER BY, the same surrogate-key tiebreaker, the same key-then-project split, and TotalRows
as a window function. Read that procedure's header for why each of those is the way it is; only what DIFFERS is argued
here.

========================================================================================================================
Requirements and Key Dependencies:

logs.HandlerLoadStatus, and the two indexes it declares for exactly these reads: IX_logs_HandlerLoadStatus_LoadRunId for
the "this run's handlers" grid, and IX_logs_HandlerLoadStatus_Handler (HandlerId, SourceType, Sequence, LoadRunId DESC)
for @LatestOnly.

logs.uspRecordExecutionError, for the error half of the AR8 instrumentation block.

EXECUTE is granted to RCRAInfoMonitorRole only. The console app writes this table through
logs.uspUpsertHandlerLoadStatusSet and reads its resume point from the same procedure's output; it has no reason to page
a grid. The asymmetry is asserted by build/check_permission_posture.py.

========================================================================================================================
Notes:

THIS READ IS NOT INSTRUMENTED ON THE SUCCESSFUL PATH, AND THAT IS THE DECISION logs.uspGetLoadRunPage PUT TO MDE. Every
write procedure logs always; read procedures opt in. logs.uspGetLoadRunPage keeps its instrumentation as the witness that
a result-set shape survives the template at all, and the six paged reads drop the successful-path row -- a row per page
request would bury the load history in the same table the grid exists to show, and this grid is refreshed by a human
holding a mouse.

DROPPING THE SUCCESSFUL-PATH ROW IS NOT DROPPING THE ERROR RECORDING. [R15] settled that every procedure records its own
errors, because MDE's other application lost an error exactly this way: a procedure whose body was one SELECT, a UDF
called inside it, the UDF errored, and nothing was written anywhere. So the AR8 block is present in its error half. What
that costs, precisely:

  - @ExecutionId and @EndTimeUtc are absent from the declare block, because with no start row nothing would assign them.
    Their absence is the only structural difference from logs.uspGetLoadRunPage's block, and it is deliberate rather than
    an omission -- a declared variable no statement ever writes is the kind of thing a maintainer removes on the
    assumption it was forgotten.
  - The CATCH passes @ExecutionLogId = NULL, which makes logs.uspRecordExecutionErrorUpdate take its orphan-insert branch
    ON PURPOSE. The failure is recorded with its message, error number, line, procedure name and @KeyParameters.
  - The one column an orphan row cannot fill is ElapsedMilliseconds, because there is no start row to subtract from, and
    it must stay NULL: that NULL is the signature that identifies an orphan and a value there would make one
    indistinguishable from a closed-out row. So the CATCH appends the elapsed milliseconds to @ContextMessage instead,
    which an orphan row does carry. That is the whole reason @StartTimeUtc is still declared in a procedure that never
    writes a start row.
  - The CATCH has no re-creation block, because there is no row a rollback could have destroyed.

THE EFFECTIVE Skip AND Take GO INTO @ContextMessage, NOT @Comments. @Comments is only ever read by the completion UPDATE,
which this procedure does not perform, so a value written there would go nowhere. @ContextMessage is recorded on the
failure path, so the clamp stays visible where it matters: 'the grid asked for page 4000 and got nothing' is answered by
comparing @KeyParameters, which holds what the caller sent, against @ContextMessage, which holds what the procedure used.

@Status AND @Outcome ARE VALIDATED AGAINST CLOSED SETS AND A BAD VALUE THROWS. This is the @SortBy argument applied to a
filter, and here it is a safety argument rather than a usability one. A mistyped @Status = N'Faild' matches nothing and
returns an empty page, and on the one screen whose job is to show failures an empty grid reads as all-clear. Zero rows
and no failures are the same picture and they are not the same fact.

  The sets are closed because THIS database closes them: CK_logs_HandlerLoadStatus_Status and
  CK_logs_HandlerLoadStatus_Outcome. Those constraints are the authority and the lists below duplicate them, which is
  drift with a silent failure mode -- add a sixth Status and this procedure starts rejecting a value the table accepts.
  build/check_closed_set_filters.py compares the two, so the duplication is measured rather than trusted.

@SourceType IS DELIBERATELY NOT VALIDATED, AND THE LINE IS NOT ARBITRARY. Validate a value only where this database
closes the set with a CHECK constraint. SourceType is EPA's, it carries no constraint on purpose -- the same reason the
dbo.HandlerSource tree carries none, that a value EPA invents next quarter must LOAD rather than fail -- and a filter
that rejects an unrecognised value would make a code EPA has started sending unsearchable on the very screen an operator
would use to notice it. Same for @HandlerId and @ActivityLocation: they are EPA's identifiers, so a value that matches
nothing is a legitimate answer of "nothing".

@LatestOnly IS AN ANTI-SEMI-JOIN, NOT A PARTITIONED WINDOW FUNCTION, AND THE DIFFERENCE IS THE COMMON CASE. "The current
state of each source record" is the question logs.HandlerLoadStatus's own description says the monitoring UI asks, and it
is why IX_logs_HandlerLoadStatus_Handler ends in LoadRunId DESC. The obvious spelling -- ROW_NUMBER () OVER (PARTITION BY
HandlerId, SourceType, Sequence ORDER BY LoadRunId DESC) = 1 in a derived table -- is wrong here, because the window
function is computed whether or not the caller asked for it and no filter can be pushed underneath it. Every ordinary
"show me run 412" page would then pay a partitioned sort over the whole table. Written as NOT EXISTS (a newer row for the
same source record), the predicate sits beside the other filters, OPTION (RECOMPILE) folds it away entirely when
@LatestOnly = 0, and when @LatestOnly = 1 it is a seek per row on the index the table declares for it.

  The NOT EXISTS repeats the @IncludeDeleted predicate, which is not redundant. Without it a soft-deleted newer row would
  count as "newer" and suppress the visible older one, and the grid would show nothing at all for a handler whose history
  a retention pass had touched.

@LatestOnly = 1 TOGETHER WITH @LoadRunId IS REFUSED. The two are different questions and the combination answers neither.
"Rows in run 412 that are also the newest for their source record" is a coherent predicate and a useless report: on any
run that is not the most recent it is nearly empty, because anything touched since has been excluded -- which looks like
data loss rather than like a filter. The date filters are not refused alongside it, because narrowing the reduced set to
a window is a coherent question: 'handlers whose current state was first seen this week'.

THE TIEBREAKER MATTERS MORE HERE THAN IN logs.uspGetLoadRunPage, NOT LESS. There the sort column StartedDateUtc is very
nearly unique, one value per run, so paging over a non-unique ORDER BY was a rare and load-dependent defect. Here
logs.uspUpsertHandlerLoadStatusSet writes a whole batch in one set-based statement, so FirstSeenDateUtc -- the DEFAULT
column and the default sort -- takes the SAME instant for every row in the batch, and LoadRunId and Status are shared by
thousands of rows by design. Paging without HandlerLoadStatusId at the end of every ORDER BY would repeat and skip rows
on the first screen and every screen, not occasionally.

SEVEN SORTABLE COLUMNS, FOURTEEN EXPRESSIONS. LoadRunId, HandlerId, Status, Outcome, FirstSeenDateUtc, CompletedDateUtc,
AttemptCount. AttemptCount is in because "which handlers are being retried" is a health question the operator asks;
DurationMs and HttpStatusCode are out because a per-row grid is the wrong place for a performance distribution, and
because nobody asked. Adding one is two more expressions and one more whitelist entry, and one CASE listing several
columns is not the shortcut it looks like -- it applies data-type precedence to its branches and sorts by a converted
value.

ApiErrorMessage IS RETURNED AND OUR OWN ERROR TEXT IS NOT. The Api* columns are EPA's, stored as received, and they
answer "why did this handler fail" for the common case, which is a failed fetch. A failure on OUR side has its message in
logs.ExecutionLog, and this grid returns ExecutionLogId rather than joining to fetch the text: our error messages name
objects, parameters and line numbers, and a list screen showing five hundred of them is both a wide read and the wrong
place for them. One row, one click, one lookup. logs.HandlerLoadStatus has no column for our own failure text on purpose
-- borrowing ApiErrorMessage for one would make a database error read as something EPA said.

THE Ets* COLUMNS ARE RETURNED THOUGH PHASE 1 NEVER SETS THEM. They come back at their defaults, NotMigrated and 0. They
are in the projection now because Phase 2's migration monitor is this same grid with a different filter, and a column
added to a result set later is a change to every caller.

THIS READ OPENS NO TRANSACTION, AND THAT IS A CORRECTION MADE ON 2026-09-05. It used to open one "for template
fidelity", and its CATCH used to roll back whenever XACT_STATE () <> 0. Both were wrong, and
build/tmp/da4_getstatuspage.probesql found it on its first run:

  * XACT_STATE () <> 0 is true when the CALLER has a transaction open. Every refusal in section 1 throws BEFORE the
    BEGIN TRANSACTION, so on the most likely failure this procedure owned no transaction of its own and rolled back
    somebody else's -- a read discarding a writer's work because it was handed a misspelled @SortBy.
  * ROLLBACK is illegal inside INSERT ... EXEC (error 8004). It raised, replaced the error being reported, and aborted
    the CATCH before logs.uspRecordExecutionError could run. The error was then recorded NOWHERE. That is precisely the
    omission [R15] exists to prevent, arriving through a door nobody was watching.

The transaction was not buying anything that could be lost. At READ COMMITTED no shared lock is held past the statement
that took it, so wrapping the key query and the projection together never made them one consistent read: a row
soft-deleted between the two drops out of the page while TotalRows still counts it, transaction or no transaction. That
staleness is inherent to the key-then-project split and is acceptable on a grid. What the transaction did cost was real
-- it held both statements open as one on the loader's own tables, which is the blocking a monitoring screen must not
cause.

A PROCEDURE ROLLS BACK ONLY WHAT IT OPENED. That is now the house rule, and build/check_stored_headers.py enforces it
against the DEPLOYED module: a module containing ROLLBACK must also contain BEGIN TRANSACTION. One consequence of SET
XACT_ABORT ON remains and belongs to the caller -- a refusal raised inside an enclosing transaction dooms that
transaction, and the caller's CATCH must roll it back. This procedure will not do it for them.

WHAT @KeyParameters MAY CONTAIN. Paging arguments, filter values and identifiers: counts, codes, dates, and an EPA
handler id, which is a public regulated-entity identifier rather than PII. There is no free-text parameter on this
procedure. From MDE's own template: do NOT include parameters such as passwords and Personally Identifiable Information.

@IncludeDeleted DEFAULTS TO 0 AND EXISTS ANYWAY, for the same reason as on the load-run grid: G7 retention works through
IsDeleted, and the screen whose purpose is history must be able to ask for the history that was retired. IsDeleted comes
back on every row so the grid can mark it.

========================================================================================================================
Example Usage and Performance:

-- The grid's default: newest first, first page.
EXEC logs.uspGetHandlerLoadStatusPage;

-- Everything that failed in run 412, so the operator can see what to re-run.
EXEC logs.uspGetHandlerLoadStatusPage @LoadRunId = 412, @Status = N'Failed', @SortBy = N'HandlerId'
                                    , @SortDescending = 0;

-- One handler's whole history, newest run first. This is the "what happened to MDD000000000" question.
EXEC logs.uspGetHandlerLoadStatusPage @HandlerId = N'MDD000000000', @SortBy = N'LoadRunId';

-- Source records whose CURRENT state is Failed, across every run. @LoadRunId must be omitted.
EXEC logs.uspGetHandlerLoadStatusPage @LatestOnly = 1, @Status = N'Failed';

One Sort over the filtered set, then at most @Take primary-key seeks. With @LoadRunId supplied the filter is a seek on
IX_logs_HandlerLoadStatus_LoadRunId; with @LatestOnly = 1 there is an additional index seek per candidate row on
IX_logs_HandlerLoadStatus_Handler. Unlike logs.LoadRun this table is not small -- it grows by one row per source record
per run, which is the G7 retention question -- so the CASE-based ORDER BY sorts the filtered set every time and the
filter is what keeps that set small. A page request with no filters at all sorts the whole table.

THAT MEASUREMENT HAS NOW BEEN DONE, AND THIS PARAGRAPH USED TO END BY DEFERRING IT. It said the no-filter shape "has to
be MEASURED for rather than reasoned about" by dbo.uspGetHandlerSourcePage, and it has been:
build/tmp/measure503_sort.probesql, with the results and their consequences under the MEASURED headings in
503_dbo.uspGetHandlerSourcePage.sql. Two of its findings correct things asserted here and in 500. A CASE-based ORDER BY
does not always sort -- OPTION (RECOMPILE) folds the @SortBy comparisons to constants at compile time, and where an
index provides the folded order it is used, measured at four logical reads over an assumed 400,000 rows. And
COUNT (*) OVER () costs a worktable spool that was the single largest component of the query, 202,152 logical reads over
100,000 rows, which no index can reduce. Neither this procedure nor 500 is restructured in response: both read tables
bounded by G7 retention rather than by handler count, and the sentence above is still true OF THEM because their
indexes cover neither the projection nor the optional filters. The claims are corrected because a maintainer designing
around a constraint that does not exist is a cost this header would be imposing.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the first of the six paged reads. Copies the
											parameter block and mechanism settled by logs.uspGetLoadRunPage, drops the
											successful-path log row per that procedure's recommendation, keeps the error
											recording per [R15], and adds two things it did not need: closed-set
											validation on @Status and @Outcome, and @LatestOnly.
2026-09-05	rsincero						Removed the transaction and the CATCH's ROLLBACK. See the header note: the
											rollback fired on a transaction the CALLER owned, and inside INSERT ... EXEC it
											raised error 8004 and stopped the error from being recorded at all. Found by
											build/tmp/da4_getstatuspage.probesql on its first run; the rule that a
											procedure rolls back only what it opened is now checked against the deployed
											module by build/check_stored_headers.py.
2026-09-05	rsincero						The sort-cost measurement this header deferred to dbo.uspGetHandlerSourcePage
											has been carried out (build/tmp/measure503_sort.probesql). Corrected the two
											claims it disproved -- that a CASE-based ORDER BY always sorts, and the
											implicit assumption that COUNT (*) OVER () is free. Mechanism unchanged: this
											table is bounded by G7 retention, not by handler count.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspGetHandlerLoadStatusPage
    -- Paging and sorting first. Copied verbatim from logs.uspGetLoadRunPage: same names, same order,
    -- same defaults, same clamping. Only @SortBy's default names a column of this table instead.
      @Skip             INT           = 0
    , @Take             INT           = 50
    , @SortBy           NVARCHAR (50) = N'FirstSeenDateUtc'
    , @SortDescending   BIT           = 1
    -- Filters. NULL means no filter, always, so a grid with nothing typed into it sends nothing.
    , @LoadRunId        INT           = NULL
    , @HandlerId        NVARCHAR (12) = NULL
    , @ActivityLocation NVARCHAR (2)  = NULL
    , @SourceType       NVARCHAR (1)  = NULL
    , @Status           NVARCHAR (20) = NULL
    , @Outcome          NVARCHAR (20) = NULL
    , @FirstSeenFromUtc DATETIME2     = NULL
    , @FirstSeenToUtc   DATETIME2     = NULL
    -- "The current state of each source record", which is the question this table's description says
    -- the monitoring UI asks. See the header: an anti-semi-join, and refused together with @LoadRunId.
    , @LatestOnly       BIT           = 0
    -- Soft-delete visibility last, because it is the one filter that is about this database rather
    -- than about handler status.
    , @IncludeDeleted   BIT           = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- -------------------------------------------------------------------------------------------------
    -- AR8 instrumentation, ERROR HALF ONLY. This read writes no successful-path row -- see the header
    -- for what that costs and what it does not cost. @ExecutionId and @EndTimeUtc are absent from this
    -- block on purpose: with no start row, nothing would ever assign them.
    -- -------------------------------------------------------------------------------------------------
    -- Not a fallback for odd cases: this literal is what the monitor login actually logs, because
    -- metadata visibility is denied to it. See the header note. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetHandlerLoadStatusPage]')
          , @StartTimeUtc   DATETIME2      = SYSUTCDATETIME ()
          , @KeyParameters  NVARCHAR (MAX) = NULL
          , @ContextMessage NVARCHAR (MAX) = NULL
          , @DynamicSql     NVARCHAR (MAX) = NULL
          , @ErrorMsg       NVARCHAR (MAX) = NULL
          , @ErrorProc      NVARCHAR (300) = NULL
          , @ErrorNumber    INT            = NULL
          , @ErrorLine      INT            = NULL;

    -- -------------------------------------------------------------------------------------------------
    -- This procedure's own state.
    -- -------------------------------------------------------------------------------------------------
    DECLARE @RowsOnPage INT             = 0
          , @TotalRows  INT             = 0
          , @Failure    NVARCHAR (2048) = NULL;

    -- The page, as keys. Ordinal preserves the sort so the projecting SELECT below needs one ORDER BY
    -- column instead of repeating all fourteen CASE expressions.
    DECLARE @Page TABLE
    (
        Ordinal             INT NOT NULL PRIMARY KEY,
        HandlerLoadStatusId INT NOT NULL,
        TotalRows           INT NOT NULL
    );

    -- COALESCE on every argument, including the integers: CONCAT renders NULL as an empty string, so
    -- an omitted @Take would log as `Take=,` and read as a truncated message rather than as a NULL.
    -- These are the values the CALLER sent, before clamping; the effective ones go into @ContextMessage.
    SET @KeyParameters = CONCAT (N'Skip=', COALESCE (CAST (@Skip AS NVARCHAR (11)), N'(null)')
                               , N', Take=', COALESCE (CAST (@Take AS NVARCHAR (11)), N'(null)')
                               , N', SortBy=', COALESCE (@SortBy, N'(null)')
                               , N', SortDescending=', @SortDescending
                               , N', LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(any)')
                               , N', HandlerId=', COALESCE (@HandlerId, N'(any)')
                               , N', ActivityLocation=', COALESCE (@ActivityLocation, N'(any)')
                               , N', SourceType=', COALESCE (@SourceType, N'(any)')
                               , N', Status=', COALESCE (@Status, N'(any)')
                               , N', Outcome=', COALESCE (@Outcome, N'(any)')
                               , N', FirstSeenFromUtc=', COALESCE (CONVERT (NVARCHAR (27), @FirstSeenFromUtc, 126), N'(any)')
                               , N', FirstSeenToUtc=', COALESCE (CONVERT (NVARCHAR (27), @FirstSeenToUtc, 126), N'(any)')
                               , N', LatestOnly=', @LatestOnly
                               , N', IncludeDeleted=', @IncludeDeleted);

    BEGIN TRY

        -- ==========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ====
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 1. Validation and clamping. First, and before any work -- the position a writing procedure
        --    puts it in to stay outside its own transaction, kept here because a refusal should cost
        --    nothing but the parse.
        -- ------------------------------------------------------------------------------------------
        IF @SortBy IS NULL
           OR @SortBy NOT IN (N'LoadRunId', N'HandlerId', N'Status', N'Outcome'
                            , N'FirstSeenDateUtc', N'CompletedDateUtc', N'AttemptCount')
        BEGIN
            SET @Failure = CONCAT (N'@SortBy = ', COALESCE (N'''' + @SortBy + N'''', N'NULL')
                                 , N' is not a sortable column of this grid. Use one of LoadRunId, ')
                         + N'HandlerId, Status, Outcome, FirstSeenDateUtc, CompletedDateUtc, '
                         + N'AttemptCount. The value is rejected rather than defaulted so that a '
                         + N'mistyped sort column cannot come back as rows sorted by something else.';
            ;THROW 50000, @Failure, 1;
        END;

        -- The two closed sets, and the reason they are checked rather than left to match nothing: on a
        -- screen whose job is to show failures, an empty grid reads as all-clear. Duplicated from
        -- CK_logs_HandlerLoadStatus_Status and CK_logs_HandlerLoadStatus_Outcome, which are the
        -- authority; build/check_closed_set_filters.py compares the two so the drift is measured.
        IF @Status IS NOT NULL
           AND @Status NOT IN (N'Pending', N'InProgress', N'Succeeded', N'Failed', N'Skipped')
        BEGIN
            SET @Failure = CONCAT (N'@Status = ''', @Status, N''' is not one of the values ')
                         + N'CK_logs_HandlerLoadStatus_Status allows: Pending, InProgress, '
                         + N'Succeeded, Failed, Skipped. Rejected rather than returned as an empty '
                         + N'page, because on a screen whose job is to show failures no rows and no '
                         + N'failures are the same picture and are not the same fact.';
            ;THROW 50000, @Failure, 1;
        END;

        IF @Outcome IS NOT NULL
           AND @Outcome NOT IN (N'Inserted', N'Updated', N'Unchanged', N'SoftDeleted')
        BEGIN
            SET @Failure = CONCAT (N'@Outcome = ''', @Outcome, N''' is not one of the values ')
                         + N'CK_logs_HandlerLoadStatus_Outcome allows: Inserted, Updated, '
                         + N'Unchanged, SoftDeleted. Rejected rather than returned as an empty page, '
                         + N'for the same reason as @Status.';
            ;THROW 50000, @Failure, 1;
        END;

        -- The one refused COMBINATION. Both are valid alone; together they answer neither question.
        IF @LatestOnly = 1 AND @LoadRunId IS NOT NULL
        BEGIN
            SET @Failure = CONCAT (N'@LatestOnly = 1 cannot be combined with @LoadRunId = '
                                 , @LoadRunId, N'. They are different questions: @LoadRunId asks ')
                         + N'what happened in one run, @LatestOnly asks what the current state of '
                         + N'each source record is across all runs. Together they return only the '
                         + N'rows of that run which nothing has superseded, which on any run but the '
                         + N'most recent is nearly empty and reads as missing data rather than as a '
                         + N'filter. Drop one of the two.';
            ;THROW 50000, @Failure, 1;
        END;

        -- GREATEST/LEAST are SQL Server 2022 and in scope. COALESCE first, so NULL means "the default"
        -- and not "the floor": a grid that omits @Take wants 50 rows, not 1.
        -- @Skip has a ceiling as well as a floor, and the ceiling is not cosmetic: the paging predicate
        -- below is `Ordinal <= @Skip + @Take`, and @Skip near the INT maximum makes that sum overflow
        -- and fail the call with an arithmetic error the grid cannot act on.
        SET @Skip = LEAST (GREATEST (COALESCE (@Skip, 0), 0), 2000000000);
        SET @Take = LEAST (GREATEST (COALESCE (@Take, 50), 1), 500);

        -- The EFFECTIVE values, which @KeyParameters cannot carry because it is built before the clamp.
        -- This goes into @ContextMessage rather than @Comments because there is no successful-path row
        -- for @Comments to reach; on a failure the clamp is still recorded.
        SET @ContextMessage = CONCAT (N'Skip=', @Skip, N', Take=', @Take
                                    , N', SortBy=', @SortBy, N', SortDescending=', @SortDescending
                                    , N', LatestOnly=', @LatestOnly);

        -- No BEGIN TRANSACTION, and the omission is deliberate -- see the header. Nothing here writes,
        -- READ COMMITTED gives the two statements below no shared consistency to lose, and a
        -- transaction this procedure opened would have to be rolled back by a CATCH that cannot
        -- safely do it.

        -- ------------------------------------------------------------------------------------------
        -- 2. The page, as keys. One CASE per sortable column per direction; HandlerLoadStatusId last,
        --    always. See the header: on this table the sort columns are massively non-unique, so the
        --    tiebreaker is what stops paging from repeating and skipping rows on every screen.
        -- ------------------------------------------------------------------------------------------
        -- ROW_NUMBER rather than OFFSET/FETCH, and the ordinal is what the projection orders by. With
        -- OFFSET/FETCH the outer query would have to trust that a derived table hands its rows on in
        -- the order it produced them, which is nowhere guaranteed; a window function's numbering is
        -- defined by its own OVER clause and cannot be reordered out from under it.
        INSERT INTO @Page (Ordinal, HandlerLoadStatusId, TotalRows)
        SELECT p.Ordinal, p.HandlerLoadStatusId, p.TotalRows
          FROM (SELECT ROW_NUMBER () OVER (
                       ORDER BY CASE WHEN @SortBy = N'LoadRunId'        AND @SortDescending = 0 THEN s.LoadRunId        END ASC
                              , CASE WHEN @SortBy = N'LoadRunId'        AND @SortDescending = 1 THEN s.LoadRunId        END DESC
                              , CASE WHEN @SortBy = N'HandlerId'        AND @SortDescending = 0 THEN s.HandlerId        END ASC
                              , CASE WHEN @SortBy = N'HandlerId'        AND @SortDescending = 1 THEN s.HandlerId        END DESC
                              , CASE WHEN @SortBy = N'Status'           AND @SortDescending = 0 THEN s.Status           END ASC
                              , CASE WHEN @SortBy = N'Status'           AND @SortDescending = 1 THEN s.Status           END DESC
                              , CASE WHEN @SortBy = N'Outcome'          AND @SortDescending = 0 THEN s.Outcome          END ASC
                              , CASE WHEN @SortBy = N'Outcome'          AND @SortDescending = 1 THEN s.Outcome          END DESC
                              , CASE WHEN @SortBy = N'FirstSeenDateUtc' AND @SortDescending = 0 THEN s.FirstSeenDateUtc END ASC
                              , CASE WHEN @SortBy = N'FirstSeenDateUtc' AND @SortDescending = 1 THEN s.FirstSeenDateUtc END DESC
                              , CASE WHEN @SortBy = N'CompletedDateUtc' AND @SortDescending = 0 THEN s.CompletedDateUtc END ASC
                              , CASE WHEN @SortBy = N'CompletedDateUtc' AND @SortDescending = 1 THEN s.CompletedDateUtc END DESC
                              , CASE WHEN @SortBy = N'AttemptCount'     AND @SortDescending = 0 THEN s.AttemptCount     END ASC
                              , CASE WHEN @SortBy = N'AttemptCount'     AND @SortDescending = 1 THEN s.AttemptCount     END DESC
                              -- The tiebreaker. Not decoration: FirstSeenDateUtc is one instant for a
                              -- whole batch and LoadRunId is shared by thousands of rows, so without
                              -- this the first page already repeats and skips.
                              , s.HandlerLoadStatusId DESC) AS Ordinal
                     , s.HandlerLoadStatusId
                     , COUNT (*) OVER () AS TotalRows
                  FROM logs.HandlerLoadStatus AS s
                 WHERE (@IncludeDeleted    = 1     OR s.IsDeleted        =  0)
                   AND (@LoadRunId        IS NULL OR s.LoadRunId        =  @LoadRunId)
                   AND (@HandlerId        IS NULL OR s.HandlerId        =  @HandlerId)
                   AND (@ActivityLocation IS NULL OR s.ActivityLocation =  @ActivityLocation)
                   AND (@SourceType       IS NULL OR s.SourceType       =  @SourceType)
                   AND (@Status           IS NULL OR s.Status           =  @Status)
                   AND (@Outcome          IS NULL OR s.Outcome          =  @Outcome)
                   AND (@FirstSeenFromUtc IS NULL OR s.FirstSeenDateUtc >= @FirstSeenFromUtc)
                   AND (@FirstSeenToUtc   IS NULL OR s.FirstSeenDateUtc <= @FirstSeenToUtc)
                   -- "The newest row for this source record." An anti-semi-join rather than a
                   -- partitioned ROW_NUMBER, so OPTION (RECOMPILE) folds the whole branch away when
                   -- the caller did not ask for it -- see the header. The visibility predicate is
                   -- repeated inside on purpose: a soft-deleted newer row must not count as newer, or
                   -- a handler whose history retention touched would vanish from the grid entirely.
                   AND (@LatestOnly = 0
                        OR NOT EXISTS (SELECT 1
                                         FROM logs.HandlerLoadStatus AS n
                                        WHERE n.HandlerId  = s.HandlerId
                                          AND n.SourceType = s.SourceType
                                          AND n.Sequence   = s.Sequence
                                          AND n.LoadRunId  > s.LoadRunId
                                          AND (@IncludeDeleted = 1 OR n.IsDeleted = 0)))) AS p
         WHERE p.Ordinal >  @Skip
           AND p.Ordinal <= @Skip + @Take
        OPTION (RECOMPILE);

        SET @RowsOnPage = @@ROWCOUNT;

        -- Read from the page rather than counted again, so the total the caller receives and the total
        -- recorded on a later failure are the same number by construction. NULL when the page is empty.
        SELECT @TotalRows = COALESCE (MAX (TotalRows), 0) FROM @Page;

        -- Appended, not replaced: the clamped paging arguments above are what a "wrong page" report
        -- needs, and these two are what a "the grid hung on page 3" report needs.
        SET @ContextMessage = CONCAT (@ContextMessage, N', RowsOnPage=', @RowsOnPage
                                    , N', TotalRows=', @TotalRows);

        -- ==========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 3. The projection, and the last statement in the block. There is no COMMIT here because
        --    there was no BEGIN TRANSACTION; a client that reads rows slowly holds nothing but its
        --    own cursor. That was the one property the transaction had to have, and removing the
        --    transaction is the simplest way to guarantee it.
        -- ------------------------------------------------------------------------------------------
        SELECT s.HandlerLoadStatusId
             , s.LoadRunId
             , s.HandlerId
             , s.ActivityLocation
             , s.SourceType
             , s.Sequence
             , s.HandlerSourceId
             , s.Status
             , s.Outcome
             , s.AttemptCount
             , s.HttpStatusCode
             -- EPA's, stored as received. This is "why did this handler fail" for the common case, a
             -- failed fetch. Our own failure text is deliberately not here; ExecutionLogId below is.
             , s.ApiErrorCode
             , s.ApiErrorMessage
             , s.ApiErrorId
             , s.ApiErrorDate
             , s.PayloadSha256
             , s.FirstSeenDateUtc
             , s.LastAttemptStartedDateUtc
             , s.CompletedDateUtc
             , s.DurationMs
             -- Phase 2's, at their defaults throughout Phase 1. In the projection now because adding a
             -- column to a result set later is a change to every caller.
             , s.EtsStatus
             , s.EtsAttemptCount
             , s.EtsErrorMessage
             , s.EtsLastAttemptDateUtc
             , s.EtsMigratedDateUtc
             -- The pointer to OUR error, not the text of it. One row, one click, one lookup.
             , s.ExecutionLogId
             -- Returned so a grid showing soft-deleted history can mark it, not so the caller can
             -- filter on it: @IncludeDeleted has already decided that.
             , s.IsDeleted
             , s.auditModifiedDateUtc
             , p.TotalRows
          FROM @Page                     AS p
          JOIN logs.HandlerLoadStatus    AS s ON s.HandlerLoadStatusId = p.HandlerLoadStatusId
         ORDER BY p.Ordinal;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them, so
        -- capture them before doing anything else. This mattered more when a ROLLBACK stood below it,
        -- but it is not merely historical: the CONCAT and the EXEC below both reset ERROR_MESSAGE ().
        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        -- NO ROLLBACK, and this is the whole of the 2026-09-05 correction -- see the header. This
        -- procedure opens no transaction, so it has nothing of its own to roll back, and any
        -- transaction that IS open at this point belongs to the caller. Rolling it back would discard
        -- work this procedure never did and was never asked to abandon; worse, ROLLBACK is illegal
        -- inside INSERT ... EXEC, so attempting it raised error 8004 and aborted this CATCH before the
        -- error below could be recorded at all. What remains after the capture above is recording and
        -- re-raising, which is all a read has ever needed to do.

        -- The one thing an orphan row cannot carry is ElapsedMilliseconds -- there is no start row to
        -- subtract from, so that column stays NULL and is the signature that identifies an orphan. It
        -- must stay NULL, because a value there would make an orphan indistinguishable from a closed-out
        -- row. The duration is not lost, though: @StartTimeUtc was captured before any work, so the
        -- elapsed time goes into @ContextMessage, which an orphan row does carry. DATEDIFF_BIG clamped
        -- by LEAST rather than a bare DATEDIFF, because an overflow HERE would raise inside the error
        -- handler and replace the error being reported with an arithmetic one.
        SET @ContextMessage = CONCAT (@ContextMessage, N', ElapsedMs='
                                    , CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc
                                                               , SYSUTCDATETIME ())
                                                 , CAST (2147483647 AS BIGINT)) AS INT));

        -- No re-creation block, and nothing is missing. The instrumented procedures have one because a
        -- rollback destroys the row logs.uspStartExecutionLogging wrote; this read never wrote one.
        --
        -- @ExecutionLogId = NULL is therefore passed deliberately, and logs.uspRecordExecutionErrorUpdate
        -- takes its orphan-insert branch on purpose: the message, error number, line, procedure name and
        -- @KeyParameters are all recorded. [R15]: the error is recorded either way.
        --
        -- This call swallows everything by design, so it cannot mask the error below it.
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
        -- client could no longer tell a deadlock from a bad @SortBy. The leading semicolon is
        -- required: a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetHandlerLoadStatusPage'
    , @Description = N'Returns one page of AR5 per-handler load status to the monitoring web app, filtered and sorted, with the filtered total carried on every row as TotalRows. The first of the six DA4 paged reads, and it copies the parameter block, clamping, whitelist-that-throws, CASE-per-column-per-direction ORDER BY, surrogate-key tiebreaker, key-then-project split and TotalRows window function settled by logs.uspGetLoadRunPage. It DROPS the successful-path execution-log row, per that procedure''s recommendation -- a row per page request would bury the load history in the same table this grid exists to show -- and keeps the error recording, passing @ExecutionLogId = NULL so logs.uspRecordExecutionErrorUpdate writes an orphan row on purpose ([R15]: every procedure records its own errors, whether or not it writes anything). @Status and @Outcome are validated against the closed sets CK_logs_HandlerLoadStatus_Status and CK_logs_HandlerLoadStatus_Outcome and a bad value is REJECTED, because on a screen whose job is to show failures an empty grid reads as all-clear; @SourceType, @HandlerId and @ActivityLocation are deliberately NOT validated, because they are EPA''s and a value EPA invents next quarter must stay searchable. @LatestOnly = 1 reduces to the newest row per source record with a NOT EXISTS anti-semi-join rather than a partitioned ROW_NUMBER, so OPTION (RECOMPILE) folds the branch away when it is not asked for instead of making every ordinary page pay a partitioned sort; it is REFUSED together with @LoadRunId, which is a coherent predicate answering neither question. The projection returns EPA''s Api* columns as the reason a fetch failed and ExecutionLogId as the pointer to the reason OUR side failed, never our error text -- this table has no column for that on purpose. EXECUTE is granted to RCRAInfoMonitorRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The monitor only. The console app WRITES this table, through logs.uspUpsertHandlerLoadStatusSet,
-- and reads its resume point from logs.uspGetHandlerLoadResumeSet (script 525); it has no reason to
-- page a grid. Corrected 2026-09-06: this comment previously said the resume point came from
-- uspUpsertHandlerLoadStatusSet's own output, which it does not and must not -- a resume set has to be
-- readable before the run that resumes exists, and that write procedure is called by a run already
-- under way.
-- Both directions are asserted by build/check_permission_posture.py, so the asymmetry is measured
-- rather than intended.
--
-- Ownership chaining carries the SELECT on logs.HandlerLoadStatus and the INSERT on
-- logs.ExecutionLog through this grant, so the monitor login holds no direct permission on either
-- table (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetHandlerLoadStatusPage TO RCRAInfoMonitorRole;
END;
GO

PRINT N'501: logs.uspGetHandlerLoadStatusPage created or altered, EXECUTE granted to the monitor role.';
GO
