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
ObjectName:   logs.uspGetLoadRunPage
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Returns one page of load-run history to the monitoring web app, filtered and sorted, with the total row count of the
filtered set carried on every row. This is the grid on the front page of the monitor: what ran, when, how it ended, and
how much it moved.

It is the second of the two DA1 procedures, and its job is to settle the standard parameter block that the other six
paged reads in DA4 will copy. Everything below the parameter list is mechanism the other six inherit unchanged: the
clamping, the whitelist, the CASE-per-column-per-direction ORDER BY, the surrogate-key tiebreaker, the key-then-project
split, and TotalRows as a window function.

========================================================================================================================
Requirements and Key Dependencies:

logs.LoadRun.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, which is copied
verbatim from .claude/skills/sql-objects/templates/procedure.sql.

EXECUTE is granted to RCRAInfoMonitorRole only. The console app has no reason to read the grid -- it finds its own
starting point through config.uspGetLoadWatermark -- and the asymmetry is asserted by build/check_permission_posture.py.

========================================================================================================================
Notes:

THIS READ IS INSTRUMENTED, AND THAT IS A DECISION RATHER THAN THE DEFAULT. The policy DA1 puts to MDE is: every write
procedure always, read procedures opt-in. A row per page request would bury the load history the table exists to show,
in that same table, and the monitoring grid is refreshed by a human holding a mouse. This one procedure is instrumented
because DA1 has to prove that a read shape survives the template at all -- a SELECT returning a result set to the
caller, from inside TRY/CATCH, without the wrapping breaking the shape EF Core materializes. If MDE takes the
recommendation, this procedure keeps its instrumentation as the witness and the other six paged reads in DA4 drop it.

THIS READ OPENS NO TRANSACTION, AND THAT IS A CORRECTION MADE ON 2026-09-05. It used to open one "for template fidelity"
so that the instrumentation block would be byte-identical in all twenty procedures, and its CATCH used to roll back
whenever XACT_STATE () <> 0. Byte-identical was the wrong goal: a read and a write do not have the same obligations, and
copying a writer's CATCH into a reader put two defects here.
build/tmp/da4_getstatuspage.probesql found them against logs.uspGetHandlerLoadStatusPage, and they were true here for the
same reason:

  * XACT_STATE () <> 0 is true when the CALLER has a transaction open. Every refusal in section 1 throws BEFORE the
    BEGIN TRANSACTION, so on the most likely failure this procedure owned no transaction of its own and rolled back
    somebody else's -- a read discarding a writer's work because it was handed a misspelled @SortBy.
  * ROLLBACK is illegal inside INSERT ... EXEC (error 8004). It raised, replaced the error being reported, and aborted
    the CATCH before the error could be recorded. Note what that costs HERE specifically: this is the one paged read
    that still writes a successful-path row, so the rollback also destroyed the logs.ExecutionLog row
    logs.uspStartExecutionLogging had written, and then never reached the re-creation block that exists to bring it
    back. A failure erased its own start row and recorded nothing in its place.

The transaction was not buying anything that could be lost. At READ COMMITTED no shared lock is held past the statement
that took it, so wrapping the key query and the projection together never made them one consistent read: a row
soft-deleted between the two drops out of the page while TotalRows still counts it, transaction or no transaction. What
the transaction did cost was real -- it held both statements open as one, and this grid reads the table the loader is
writing.

A PROCEDURE ROLLS BACK ONLY WHAT IT OPENED. That is now the house rule, and build/check_stored_headers.py enforces it
against the DEPLOYED module: a module containing ROLLBACK must also contain BEGIN TRANSACTION. The nineteen procedures
that DO open their own transactions are unaffected -- for them the rollback is both owned and necessary.

@SortBy IS VALIDATED AGAINST A WHITELIST AND NEVER CONCATENATED. There is no dynamic SQL in this procedure at all, so
the whitelist is not defending a string that is about to be executed -- it is defending against a silent fall-through.
The comparison is collation-based and therefore case-insensitive, so 'startedDateUtc' from a JavaScript grid works.

AN UNRECOGNISED @SortBy IS AN ERROR, NOT A DEFAULT, AND THAT IS A DEVIATION FROM THE PLAN. Phase1-Plan.md DA2 sketched
@SortBy NVARCHAR (50) = NULL with the NULL case falling through to a default ORDER BY. This procedure instead defaults
the parameter visibly -- N'StartedDateUtc' -- and throws on a value it does not recognise. The reason is that the
fall-through and the typo are indistinguishable to the caller: a grid that sends 'StartedDate' by mistake gets rows
back, sorted by something else, and the defect surfaces as a user saying the sort arrows do not work. Settling this
block is DA1's assigned job, so the deviation is deliberate and is recorded in the plan.

@Status AND @RunMode ARE VALIDATED THE SAME WAY, AND WERE NOT UNTIL 2026-09-05. Both filter columns that THIS database
closes -- CK_logs_LoadRun_Status to five values, CK_logs_LoadRun_RunMode to four -- and until that date an unrecognised
value simply matched nothing. That is the @SortBy fall-through wearing a different hat, and here it is a safety argument
rather than a usability one: @Status = N'Faild' returns an empty page, and on the grid whose job is to show failures an
empty table reads as all-clear. Zero rows and no failures are the same picture and they are not the same fact. The lists
duplicate the constraints, which is drift in both directions -- add a sixth Status and this procedure starts refusing a
value the table accepts -- so build/check_closed_set_filters.py compares them against sys.check_constraints. That check
was written for logs.uspGetHandlerLoadStatusPage and found this omission here on its first run, which is the argument for
writing the check rather than the procedure carefully.

AND THE DRIFT THIS PARAGRAPH PREDICTED HAPPENED, IN THE DIRECTION IT NAMED. RunMode gained a fourth value, Targeted, when
the single-handler loader mode was built -- CK_logs_LoadRun_RunMode was widened and this list was not. So the procedure
refused a value the table accepts, and the effect was precisely the one the closed-set argument is about, one level up:
an operator could not page the targeted runs AT ALL. Runs 2624 and 2625 -- both Targeted, and both the evidence behind
[R43] and [R44] -- were unreachable from the grid whose job is to show what the loader did. The check caught it; the
procedure did not, and could not have.

@ActivityLocation IS DELIBERATELY NOT VALIDATED, AND THE LINE IS NOT ARBITRARY. Validate a value only where this database
closes the set with a CHECK constraint. That column carries none, on purpose and by MDE's decision -- no state column
anywhere in this database is constrained to MD -- so an unmatched value here is a legitimate answer of "nothing", not a
typo worth refusing.

ONE CASE PER SORTABLE COLUMN PER DIRECTION. A single CASE listing several columns would apply data-type precedence to
its branches and sort by a converted value -- Status and LoadRunId in one CASE sorts the whole grid numerically, or
fails outright. Twelve expressions for six columns is the cost of not having that bug. It is also why the whitelist is
short: ActivityLocation is MD-only for this project, and SourceRecordsFetched/Inserted/Updated are omitted because
sorting a grid by them was not asked for. Adding one is two more expressions and one more whitelist entry.

THE TIEBREAKER IS NOT OPTIONAL. Every ORDER BY ends with LoadRunId, the surrogate key. Status and RunMode are not
unique, and paging over a non-unique ORDER BY lets the engine return a row on page 1 and again on page 2 while some
other row appears on neither -- silently, and only under load, because the ordering of tied rows is undefined and the
plan is free to change between the two calls.

TotalRows IS A WINDOW FUNCTION, NOT AN OUTPUT PARAMETER. An OUTPUT parameter can only be read after the reader is
closed, and EF Core does not support multiple result sets at all, so a second SELECT is not available either. COUNT (*)
OVER () is evaluated over the whole filtered set, before the page predicate is applied, so it is the total and not the
page size. The consequence the web app must handle: when nothing matches, there are no rows and therefore no TotalRows
-- an empty result set means zero, and the client cannot read the total from a row that is not there.

THE PAGING QUERY SELECTS KEYS AND THE PROJECTION IS A SEPARATE JOIN. Paging over the narrow key set, then joining back
for the wide column list, keeps the CASE expressions and OPTION (RECOMPILE) in one small query and lets the completion
UPDATE record both the page size and the filtered total before any row is streamed. The cost is a second pass over at
most @Take rows by primary key. The alternative -- one wide SELECT with OFFSET/FETCH -- cannot report its own row count
to the log without either running twice or materialising all thirty columns into a table variable.

THE PAGE IS CUT WITH ROW_NUMBER, NOT OFFSET/FETCH. Both produce the same rows, but OFFSET/FETCH inside a derived table
would leave the outer query trusting that the derived table hands its rows on in the order it produced them, which is
nowhere guaranteed and would surface as an out-of-order grid under a plan change rather than as an error. A window
function's numbering is fixed by its own OVER clause. It is also the cheaper of the two here: the same single Sort feeds
both, and Ordinal comes out as the row's absolute position in the filtered set, which is what a "showing 51-75 of 340"
caption needs and OFFSET/FETCH does not provide.

A CASE-BASED ORDER BY SORTS UNLESS AN INDEX HAPPENS TO PROVIDE THE FOLDED ORDER, AND THIS PARAGRAPH SAID SOMETHING
STRONGER UNTIL 2026-09-05. It used to read "A CASE-BASED ORDER BY CANNOT USE AN INDEX FOR ORDERING. It always sorts",
and it was that claim which deferred a measurement to dbo.uspGetHandlerSourcePage. The measurement disproved it. Under
OPTION (RECOMPILE) the parameter values are embedded as constants at compile time, so
`CASE WHEN @SortBy = N'StartedDateUtc' AND @SortDescending = 1 THEN r.StartedDateUtc END DESC` folds to
`r.StartedDateUtc DESC` and the other eleven expressions fold to NULL and are eliminated. What reaches the optimizer is
a plain single-column ORDER BY, and where an index provides that order it is used and no Sort operator appears at all.
Measured on the shape this procedure uses, at an assumed 400,000 rows: four logical reads and no sort. See
build/tmp/measure503_sort.probesql and the MEASURED headings in 503_dbo.uspGetHandlerSourcePage.sql.

WHAT THAT MEANS HERE, WHICH IS LESS THAN IT SOUNDS. This procedure's own sorts still sort, because
IX_logs_LoadRun_StartedDateUtc and IX_logs_LoadRun_Status do not cover the projection or the optional filters, so the
optimizer prefers a scan and a sort to a scan and 500 lookups -- and on a table holding one row per load run under G7
retention that is the right trade and the cost is nothing. The mechanism here is deliberately NOT changed. What did
change is the claim, because a maintainer reading a disproved statement in a deployed module will design around a
constraint that does not exist.

THE MEASUREMENT ALSO FOUND SOMETHING THIS PROCEDURE'S TotalRows PAYS FOR. COUNT (*) OVER () builds a worktable spool --
202,152 logical reads over 100,000 rows, larger than scanning a 218-column clustered index, and no index can help
because a spool is not a table. 503 therefore takes its total from its own COUNT (*) statement and carries it into the
projection as a scalar, which keeps the single result set EF Core requires. This procedure keeps the window function:
the spool is proportional to the filtered set, logs.LoadRun's filtered set is bounded by runs per day, and rewriting a
working procedure to save microseconds is churn. If G7 retention is ever relaxed, this note is the argument for
revisiting it.

@Take IS CLAMPED HERE, NOT AT THE CALLER. A grid that asks for 100000 rows gets 500. The clamp is inside the procedure
because the procedure is the boundary the permission model actually enforces; a check in the web app is a check the
caller can skip. @Skip is floored at 0 because OFFSET rejects a negative value with an error the grid cannot act on.

OPTION (RECOMPILE) ON THE PAGING QUERY. Six sort orders times two directions times seven optional filters is more plan
shapes than one cached plan can serve, and the parameter values are what decide whether a seek or a scan is right. The
recompile is paid on a human-initiated page request, which is the cheapest place in this system to pay it.

@ProcName FALLS BACK TO A LITERAL, AND THE LITERAL IS THE ONE THE MONITOR ACTUALLY USES. OBJECT_NAME (@@PROCID) and
OBJECT_SCHEMA_NAME (@@PROCID) return NULL for a principal denied metadata visibility, and script 050 denies exactly that
to both application logins. Measured on this procedure: RCRAInfoMonitor holds EXECUTE = 1 on it and still reads NULL from
OBJECT_ID (N'logs.uspGetLoadRunPage'), because permission to run an object is not permission to see its name. So the
COALESCE is not a defensive nicety for ad-hoc batches -- it is the branch every web-app call takes, and with a
placeholder there every row the monitor wrote carried no procedure name, which is the one column the monitoring grid
groups by. This was found by the DA1 chained-call posture assertion, which reads the row back as the developer rather
than trusting that the EXEC succeeded; that is why the assertion reads the row at all. The dynamic half is kept because
it still resolves for the developer and catches a rename the literal would not -- so change both when renaming.

WHAT @KeyParameters MAY CONTAIN. Paging arguments, filter values and identifiers -- all of which are counts, codes and
dates here. There is no free-text search parameter on this procedure; when DA4 adds one to dbo.uspSearchHandlerSource,
the term must not be logged, because a search term is whatever a user typed and this database holds regulated-entity
records. From MDE's own template: do NOT include parameters such as passwords and Personally Identifiable Information.

FailureMessage IS RETURNED TO A WEB PAGE. Whatever the loader writes into it is displayed to the monitor's users, so it
must never carry a query string, a request header, or the API ID or Key -- the same rule that keeps
logs.HandlerLoadAttempt.RequestPath to the path alone. This procedure cannot enforce that; it is named here because this
is where the consequence becomes visible.

@IncludeDeleted DEFAULTS TO 0 AND EXISTS ANYWAY. Every read path filters IsDeleted = 0. A retention pass that soft-
deletes old runs (G7) would otherwise make history invisible to the one screen whose purpose is history, so the monitor
can ask for it explicitly and IsDeleted comes back on every row so the grid can mark it.

========================================================================================================================
Example Usage and Performance:

-- The grid's default: newest first, first page.
EXEC logs.uspGetLoadRunPage;

-- Second page of 25, oldest first.
EXEC logs.uspGetLoadRunPage @Skip = 25, @Take = 25, @SortBy = N'StartedDateUtc', @SortDescending = 0;

-- Everything that failed in the last week, worst first.
EXEC logs.uspGetLoadRunPage @Status = N'Failed', @StartedFromUtc = '2026-08-29', @SortBy = N'SourceRecordsFailed';

One Sort over the filtered set, then at most @Take primary-key seeks. Measured on the dev database against 12 rows the
whole call is under 5 ms after the first compile; the figure that matters is not this one but the row count logs.LoadRun
reaches under G7 retention, which is bounded by runs per day rather than by handlers.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA1, the paged read that settles the standard
											parameter block. Deviates from the plan's DA2 sketch by defaulting @SortBy
											visibly and throwing on an unrecognised value instead of falling through.
2026-09-05	rsincero						DA4: @Status and @RunMode now reject a value outside the set their CHECK
											constraint allows, instead of returning an empty page. Found by
											build/check_closed_set_filters.py, which was written for
											logs.uspGetHandlerLoadStatusPage and reported this procedure on its first run.
2026-09-05	rsincero						Removed the transaction and the CATCH's ROLLBACK. See the header note: the
											rollback fired on a transaction the CALLER owned, and inside INSERT ... EXEC it
											raised error 8004, stopped the failure from being recorded, and destroyed the
											start row on the way past. The re-creation block is kept -- a caller's own
											rollback can still take that row. Found by
											build/tmp/da4_getstatuspage.probesql; the rule that a procedure rolls back only
											what it opened is now checked against the deployed module by
											build/check_stored_headers.py.
2026-09-05	rsincero						Corrected the claim that a CASE-based ORDER BY can never use an index for
											ordering. It was that claim which deferred a measurement to
											dbo.uspGetHandlerSourcePage, and build/tmp/measure503_sort.probesql disproved
											it: OPTION (RECOMPILE) folds the @SortBy comparisons to constants and an index
											that provides the folded order is used, four logical reads and no sort at an
											assumed 400,000 rows. The same measurement priced COUNT (*) OVER () at a
											202,152-read worktable spool. The MECHANISM here is unchanged and the header
											says why; only the disproved statements are corrected.
2026-09-07	rsincero						@RunMode now accepts Targeted, the fourth value CK_logs_LoadRun_RunMode allows.
											The constraint was widened when the single-handler loader mode was built and this
											list was not, so the procedure refused a value the table accepts and the
											targeted runs -- including 2624 and 2625, the evidence behind [R43] and [R44] --
											could not be paged at all. Found by build/check_closed_set_filters.py, which is
											the second time that check has reported this procedure and the second time the
											drift it was written to catch was real.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspGetLoadRunPage
    -- Paging and sorting first. This block is the one DA1 settles, and the other six paged reads copy
    -- it verbatim: same names, same order, same defaults, same clamping.
      @Skip             INT           = 0
    , @Take             INT           = 50
    , @SortBy           NVARCHAR (50) = N'StartedDateUtc'
    , @SortDescending   BIT           = 1
    -- Filters. NULL means no filter, always, so a grid with nothing typed into it sends nothing.
    , @LoadRunId        INT           = NULL
    , @RunMode          NVARCHAR (20) = NULL
    , @ActivityLocation NVARCHAR (2)  = NULL
    , @Status           NVARCHAR (20) = NULL
    , @StartedFromUtc   DATETIME2     = NULL
    , @StartedToUtc     DATETIME2     = NULL
    -- Soft-delete visibility last, because it is the one filter that is about this database rather
    -- than about load runs.
    , @IncludeDeleted   BIT           = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- -------------------------------------------------------------------------------------------------
    -- AR8 instrumentation. Boilerplate: copy verbatim.
    -- -------------------------------------------------------------------------------------------------
    -- Not a fallback for odd cases: this literal is what the monitor login actually logs, because
    -- metadata visibility is denied to it. See the header note. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspGetLoadRunPage]')
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
    DECLARE @RowsOnPage INT             = 0
          , @TotalRows  INT             = 0
          , @Failure    NVARCHAR (2048) = NULL;

    -- The page, as keys. Ordinal preserves the sort so the projecting SELECT below needs one ORDER BY
    -- column instead of repeating all twelve CASE expressions.
    DECLARE @Page TABLE
    (
        Ordinal   INT NOT NULL PRIMARY KEY,
        LoadRunId INT NOT NULL,
        TotalRows INT NOT NULL
    );

    -- COALESCE on every argument, including the integers: CONCAT renders NULL as an empty string, so
    -- an omitted @Take would log as `Take=,` and read as a truncated message rather than as a NULL.
    -- These are the values the CALLER sent, before clamping; the effective ones go into @Comments.
    SET @KeyParameters = CONCAT (N'Skip=', COALESCE (CAST (@Skip AS NVARCHAR (11)), N'(null)')
                               , N', Take=', COALESCE (CAST (@Take AS NVARCHAR (11)), N'(null)')
                               , N', SortBy=', COALESCE (@SortBy, N'(null)')
                               , N', SortDescending=', @SortDescending
                               , N', LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(any)')
                               , N', RunMode=', COALESCE (@RunMode, N'(any)')
                               , N', ActivityLocation=', COALESCE (@ActivityLocation, N'(any)')
                               , N', Status=', COALESCE (@Status, N'(any)')
                               , N', StartedFromUtc=', COALESCE (CONVERT (NVARCHAR (27), @StartedFromUtc, 126), N'(any)')
                               , N', StartedToUtc=', COALESCE (CONVERT (NVARCHAR (27), @StartedToUtc, 126), N'(any)')
                               , N', IncludeDeleted=', @IncludeDeleted);

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
        -- 1. Validation and clamping. First, and before any work -- the position a writing procedure
        --    puts it in to stay outside its own transaction, kept here because a refusal should cost
        --    nothing but the parse.
        -- ------------------------------------------------------------------------------------------
        IF @SortBy IS NULL
           OR @SortBy NOT IN (N'LoadRunId', N'StartedDateUtc', N'CompletedDateUtc'
                            , N'Status', N'RunMode', N'SourceRecordsFailed')
        BEGIN
            SET @Failure = CONCAT (N'@SortBy = ', COALESCE (N'''' + @SortBy + N'''', N'NULL')
                                 , N' is not a sortable column of this grid. Use one of LoadRunId, ')
                         + N'StartedDateUtc, CompletedDateUtc, Status, RunMode, SourceRecordsFailed. '
                         + N'The value is rejected rather than defaulted so that a mistyped sort '
                         + N'column cannot come back as rows sorted by something else.';
            ;THROW 50000, @Failure, 1;
        END;

        -- The two CLOSED sets, added 2026-09-05 -- see the header. The lists duplicate
        -- CK_logs_LoadRun_Status and CK_logs_LoadRun_RunMode, which are the authority;
        -- build/check_closed_set_filters.py compares the two, and it is what found their absence here.
        IF @Status IS NOT NULL
           AND @Status NOT IN (N'Running', N'Succeeded', N'PartiallySucceeded', N'Failed'
                             , N'Abandoned')
        BEGIN
            SET @Failure = CONCAT (N'@Status = ''', @Status, N''' is not one of the values ')
                         + N'CK_logs_LoadRun_Status allows: Running, Succeeded, '
                         + N'PartiallySucceeded, Failed, Abandoned. Rejected rather than returned as '
                         + N'an empty page, because on a screen whose job is to show failures no rows '
                         + N'and no failures are the same picture and are not the same fact.';
            ;THROW 50000, @Failure, 1;
        END;

        -- Targeted added 2026-09-07. It is the fourth value CK_logs_LoadRun_RunMode allows, and its
        -- absence here refused it outright rather than matching nothing -- so the single-handler runs
        -- were unreachable from the grid. See the header.
        IF @RunMode IS NOT NULL
           AND @RunMode NOT IN (N'Full', N'Incremental', N'Reconcile', N'Targeted')
        BEGIN
            SET @Failure = CONCAT (N'@RunMode = ''', @RunMode, N''' is not one of the values ')
                         + N'CK_logs_LoadRun_RunMode allows: Full, Incremental, Reconcile, Targeted. '
                         + N'Rejected rather than returned as an empty page, for the same reason as '
                         + N'@Status.';
            ;THROW 50000, @Failure, 1;
        END;

        -- GREATEST/LEAST are SQL Server 2022 and in scope. COALESCE first, so NULL means "the default"
        -- and not "the floor": a grid that omits @Take wants 50 rows, not 1.
        -- @Skip has a ceiling as well as a floor, and the ceiling is not cosmetic: the paging predicate
        -- below is `Ordinal <= @Skip + @Take`, and @Skip near the INT maximum makes that sum overflow
        -- and fail the call with an arithmetic error the grid cannot act on.
        SET @Skip = LEAST (GREATEST (COALESCE (@Skip, 0), 0), 2000000000);
        SET @Take = LEAST (GREATEST (COALESCE (@Take, 50), 1), 500);

        SET @ContextMessage = CONCAT (N'Skip=', @Skip, N', Take=', @Take
                                    , N', SortBy=', @SortBy, N', SortDescending=', @SortDescending);

        -- No BEGIN TRANSACTION, and the omission is deliberate -- see the header. Nothing here writes
        -- except the instrumentation, which must survive a failure rather than be rolled back with it,
        -- and READ COMMITTED gives the two statements below no shared consistency to lose.

        -- ------------------------------------------------------------------------------------------
        -- 2. The page, as keys. One CASE per sortable column per direction; LoadRunId last, always.
        -- ------------------------------------------------------------------------------------------
        -- ROW_NUMBER rather than OFFSET/FETCH, and the ordinal is what the projection orders by. With
        -- OFFSET/FETCH the outer query would have to trust that a derived table hands its rows on in
        -- the order it produced them, which is nowhere guaranteed; a window function's numbering is
        -- defined by its own OVER clause and cannot be reordered out from under it. It also costs
        -- nothing here: the same single Sort feeds both, and Ordinal is the row's absolute position in
        -- the filtered set, which is what the grid's "showing 51-75 of 340" needs anyway.
        INSERT INTO @Page (Ordinal, LoadRunId, TotalRows)
        SELECT p.Ordinal, p.LoadRunId, p.TotalRows
          FROM (SELECT ROW_NUMBER () OVER (
                       ORDER BY CASE WHEN @SortBy = N'LoadRunId'           AND @SortDescending = 0 THEN r.LoadRunId           END ASC
                              , CASE WHEN @SortBy = N'LoadRunId'           AND @SortDescending = 1 THEN r.LoadRunId           END DESC
                              , CASE WHEN @SortBy = N'StartedDateUtc'      AND @SortDescending = 0 THEN r.StartedDateUtc      END ASC
                              , CASE WHEN @SortBy = N'StartedDateUtc'      AND @SortDescending = 1 THEN r.StartedDateUtc      END DESC
                              , CASE WHEN @SortBy = N'CompletedDateUtc'    AND @SortDescending = 0 THEN r.CompletedDateUtc    END ASC
                              , CASE WHEN @SortBy = N'CompletedDateUtc'    AND @SortDescending = 1 THEN r.CompletedDateUtc    END DESC
                              , CASE WHEN @SortBy = N'Status'              AND @SortDescending = 0 THEN r.Status              END ASC
                              , CASE WHEN @SortBy = N'Status'              AND @SortDescending = 1 THEN r.Status              END DESC
                              , CASE WHEN @SortBy = N'RunMode'             AND @SortDescending = 0 THEN r.RunMode             END ASC
                              , CASE WHEN @SortBy = N'RunMode'             AND @SortDescending = 1 THEN r.RunMode             END DESC
                              , CASE WHEN @SortBy = N'SourceRecordsFailed' AND @SortDescending = 0 THEN r.SourceRecordsFailed END ASC
                              , CASE WHEN @SortBy = N'SourceRecordsFailed' AND @SortDescending = 1 THEN r.SourceRecordsFailed END DESC
                              -- The tiebreaker. Not decoration: without it, paging over Status or
                              -- RunMode repeats and skips rows.
                              , r.LoadRunId DESC) AS Ordinal
                     , r.LoadRunId
                     , COUNT (*) OVER () AS TotalRows
                  FROM logs.LoadRun AS r
                 WHERE (@IncludeDeleted    = 1     OR r.IsDeleted        =  0)
                   AND (@LoadRunId        IS NULL OR r.LoadRunId        =  @LoadRunId)
                   AND (@RunMode          IS NULL OR r.RunMode          =  @RunMode)
                   AND (@ActivityLocation IS NULL OR r.ActivityLocation =  @ActivityLocation)
                   AND (@Status           IS NULL OR r.Status           =  @Status)
                   AND (@StartedFromUtc   IS NULL OR r.StartedDateUtc   >= @StartedFromUtc)
                   AND (@StartedToUtc     IS NULL OR r.StartedDateUtc   <= @StartedToUtc)) AS p
         WHERE p.Ordinal >  @Skip
           AND p.Ordinal <= @Skip + @Take
        OPTION (RECOMPILE);

        SET @RowsOnPage = @@ROWCOUNT;

        -- Read from the page rather than counted again, so the logged total and the total the caller
        -- receives are the same number by construction. NULL when the page is empty.
        SELECT @TotalRows = COALESCE (MAX (TotalRows), 0) FROM @Page;

        -- The EFFECTIVE Skip and Take, after clamping, which @KeyParameters cannot carry because it is
        -- built before the clamp and records what the caller asked for. Both are needed: 'the grid
        -- showed the wrong page' is answered by comparing the two, and without the pair here the clamp
        -- is invisible on a successful call.
        SET @Comments = CONCAT (N'Skip=', @Skip, N', Take=', @Take
                              , N', RowsOnPage=', @RowsOnPage, N', TotalRows=', @TotalRows);

        -- ==========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- ==========================================================================================

        -- No COMMIT, because there was no BEGIN TRANSACTION. The completion update below used to sit
        -- after it for the reason the merge procedure's header gives -- an instrumentation write must
        -- not be inside the transaction whose failure it is describing. With no transaction at all
        -- that property is unconditional rather than positional.
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

        -- ------------------------------------------------------------------------------------------
        -- 3. The projection, and the last statement in the block. After the COMMIT so a client that
        --    reads rows slowly cannot hold a transaction open, and after the completion UPDATE so the
        --    logged duration is the work rather than the network.
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
             -- Computed rather than sortable: for a run still in flight this grows between one page
             -- request and the next, and paging over a value that changes underneath the sort is the
             -- repeat-and-skip bug the tiebreaker exists to prevent.
             , CAST (LEAST (DATEDIFF_BIG (SECOND, r.StartedDateUtc
                                        , COALESCE (r.CompletedDateUtc, SYSUTCDATETIME ()))
                          , CAST (2147483647 AS BIGINT)) AS INT) AS ElapsedSeconds
             , r.ResumedFromLoadRunId
             , r.LookupListsRefreshed
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
             , r.FailureMessage
             , r.InvokedBy
             , r.MachineName
             , r.ProcessId
             , r.ApplicationVersion
             -- Returned so a grid showing soft-deleted history can mark it, not so the caller can
             -- filter on it: @IncludeDeleted has already decided that.
             , r.IsDeleted
             , r.auditModifiedDateUtc
             , p.TotalRows
          FROM @Page            AS p
          JOIN logs.LoadRun     AS r ON r.LoadRunId = p.LoadRunId
         ORDER BY p.Ordinal;

    END TRY
    BEGIN CATCH

        -- The ERROR_* functions are valid only in this scope and any statement can reset them, so
        -- capture them before doing anything else. This mattered more when a ROLLBACK stood below it,
        -- but it is not merely historical: the CONCAT, the re-creation block and the EXEC below all
        -- reset ERROR_MESSAGE ().
        SELECT @ErrorNumber = ERROR_NUMBER ()
             , @ErrorProc   = ERROR_PROCEDURE ()
             , @ErrorLine   = ERROR_LINE ()
             , @ErrorMsg    = ERROR_MESSAGE ()
                            + N' (error '  + CAST (ERROR_NUMBER () AS NVARCHAR (11))
                            + N', line '   + CAST (ERROR_LINE ()   AS NVARCHAR (11)) + N')';

        -- NO ROLLBACK, and this is the 2026-09-05 correction -- see the header. This procedure opens no
        -- transaction, so it has nothing of its own to roll back, and any transaction open at this
        -- point belongs to the caller. Rolling it back would discard work this procedure never did;
        -- worse, ROLLBACK is illegal inside INSERT ... EXEC, so attempting it raised error 8004,
        -- aborted this CATCH, and left the failure recorded nowhere -- after having destroyed the start
        -- row on the way past.

        -- The re-creation block STAYS, and not out of caution. This procedure no longer rolls anything
        -- back, but a CALLER that wraps the read in its own transaction and rolls it back still takes
        -- the start row with it, because logs.ExecutionLog is written on this connection and inside
        -- that transaction. Put it back, with the ORIGINAL @StartTimeUtc, or the only unrecorded
        -- executions in the database would be the failures. The nested TRY is required because the
        -- start procedure does not swallow: an error escaping here would replace the error being
        -- reported.
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
    , @ObjectName  = N'uspGetLoadRunPage'
    , @Description = N'Returns one page of load-run history to the monitoring web app, filtered and sorted, with the filtered total carried on every row as TotalRows. The DA1 procedure that settles the standard paged-read parameter block the other six paged reads copy: @Skip/@Take/@SortBy/@SortDescending first, NULL-means-no-filter arguments next, @IncludeDeleted last. @Take is clamped to 1..500 and @Skip floored at 0 inside the procedure, because the procedure is the boundary the permission model enforces. @SortBy is checked against a whitelist of six columns and an unrecognised value is REJECTED rather than defaulted, so a mistyped sort column cannot come back as rows sorted by something else; there is no dynamic SQL. @Status and @RunMode are checked the same way against the sets CK_logs_LoadRun_Status and CK_logs_LoadRun_RunMode close, because an unrecognised value would otherwise return an empty page and on the grid whose job is to show failures an empty page reads as all-clear; @ActivityLocation is deliberately NOT checked, because no state column in this database is constrained to MD and an unmatched value there is a legitimate answer of nothing. ORDER BY uses one CASE per column per direction -- a single CASE over several columns would apply data-type precedence and sort by a converted value -- and always ends with LoadRunId so that paging over a non-unique key cannot repeat and skip rows. The paging query selects keys with OPTION (RECOMPILE) and the wide column list is a separate join, which keeps the sort small and lets the completion log record both the page size and the total. An empty result set means TotalRows is zero: the caller cannot read the total from a row that is not there. EXECUTE is granted to RCRAInfoMonitorRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The monitor only. The console app never reads this grid -- it finds its starting point through
-- config.uspGetLoadWatermark -- and the mirror image of this grant is on
-- dbo.uspMergeHandlerSourceBatch, which the monitor cannot execute. Both directions are asserted by
-- build/check_permission_posture.py, so the asymmetry is measured rather than intended.
--
-- Ownership chaining carries the SELECT on logs.LoadRun and the INSERT on logs.ExecutionLog through
-- this grant, so the monitor login holds no direct permission on either table (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspGetLoadRunPage TO RCRAInfoMonitorRole;
END;
GO

PRINT N'500: logs.uspGetLoadRunPage created or altered, EXECUTE granted to the monitor role.';
GO
