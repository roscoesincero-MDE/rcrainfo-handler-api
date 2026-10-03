-- SET XACT_ABORT ON sits ABOVE the header block deliberately. The GO on the next line ends the batch, and
-- sys.sql_modules stores only the batch that contains CREATE -- so a header placed AFTER this GO is invisible to
-- anyone reading the procedure out of the database through sp_helptext, OBJECT_DEFINITION, or SSMS "Script as
-- CREATE", which is where a maintainer actually reads it.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspGetHandlerSourcePage
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Returns one page of the CURRENT version of Maryland's handler records to the monitoring web app, filtered and sorted,
with the filtered total carried on every row as TotalRows. This is the handler grid: which handlers this database
mirrors, where they are, what generator category they are in, and when EPA last changed them.

It is the first of the four DA4 reads over the handler mirror itself rather than over the logs schema, and it is the
one whose sort cost 500 and 501 both deferred to it. That measurement was carried out before this procedure was
written, and it changed two things about how it is written -- see the two headings marked MEASURED below.

========================================================================================================================
Requirements and Key Dependencies:

dbo.vwHandlerSource, which is dbo.vwHandlerSourceHistory (IsDeleted = 0) with CurrentRecord = 1 added. Never
dbo.HandlerSource directly: those two views are the ONLY structural enforcement of AR7 now that EF Core is reduced to
calling stored procedures, and a procedure that reads the base table is a procedure that can forget the filter.

IX_dbo_HandlerSource_Grid, from script 390, for SPEED ONLY. This procedure returns the same rows without it; see
MEASURED, THE INDEX below for what it costs to be missing.

logs.uspRecordExecutionError, for the AR8 instrumentation block.

EXECUTE is granted to RCRAInfoMonitorRole only. The console app has no reason to page the grid -- it merges what the
API hands it and finds its own starting point through config.uspGetLoadWatermark -- and the asymmetry is asserted by
build/check_permission_posture.py.

========================================================================================================================
Notes:

ERROR-ONLY INSTRUMENTATION, WHICH IS THE POLICY 500 PUT TO MDE AND THIS PROCEDURE FOLLOWS. Write procedures are
instrumented always; read procedures opt in. logs.uspGetLoadRunPage keeps a successful-path row as the witness that a
read shape survives the template at all; the other paged reads, this one included, record only failures. A row per
page request would be a row per click of a grid a human is holding a mouse over.

THAT MAKES THE TRY/CATCH THE ONLY THING RECORDING ANYTHING, WHICH IS THE POINT. The defect this project is guarding
against is a procedure that only SELECTs, calls something that fails inside the SELECT, and records nothing because it
wrote no row on the way past. Every path out of the TRY block below either returns rows or reaches
logs.uspRecordExecutionError.

NO TRANSACTION, AND THEREFORE NO ROLLBACK. A procedure rolls back only what it opened; this one opens nothing, so any
transaction live in the CATCH belongs to the caller and rolling it back would discard work this procedure never did.
ROLLBACK is also illegal inside INSERT ... EXEC (error 8004), where it would replace the error being reported and abort
the CATCH before the failure could be recorded. build/check_stored_headers.py enforces the rule against the DEPLOYED
module: a module containing ROLLBACK must also contain BEGIN TRANSACTION.

THERE IS NO @IncludeDeleted PARAMETER, AND ITS ABSENCE IS A DEVIATION FROM THE BLOCK 500 SETTLED. It cannot be
honoured through dbo.vwHandlerSource: the view has already excluded soft-deleted rows before this procedure sees them,
and honouring the parameter would mean reading dbo.HandlerSource directly and restating the AR7 filter here -- a second
copy of the rule the views exist to hold. IsDeleted is not in the projection either, for the same reason: through this
view it is a provably constant 0, and a column that can only ever say one thing invites a grid to draw a "deleted"
marker that will never light. A soft-deleted handler is EPA no longer reporting the record, which is a question about
history; dbo.uspGetHandlerSourceHistoryPage is where history is answered.

THERE IS NO @CurrentRecord FILTER EITHER, FOR THE SAME REASON. The view fixes it at 1, so this grid is by definition
the current picture. The operational consequence, which belongs to the loader rather than to this procedure: because
CurrentRecord is NULLABLE and `= 1` excludes NULL, a row the loader never stamped appears in the history grid and not
in this one. dbo.uspReconcileCurrentRecord exists to make sure that does not happen, and a handler visible in 504 and
missing here is the symptom that it did not run.

--- MEASURED, THE WINDOW FUNCTION ---------------------------------------------------------------------------------
TotalRows IS ITS OWN COUNT (*) STATEMENT, NOT COUNT (*) OVER (), AND THAT IS A DEVIATION FROM 500 WITH A MEASURED
REASON. 500 argued for the window function because EF Core cannot materialize a second result set and an OUTPUT
parameter cannot be read until the reader is closed -- both still true, and both still rule out the obvious
alternatives. What 500 could not know is the price. Measured at 400,000 rows with 100,000 of them current
(build/tmp/measure503_sort.probesql, build/tmp/io503alt2.probesql):

    COUNT (*) OVER ()      66,776 reads on the table + a 202,152-read WORKTABLE
    separate COUNT (*)      1,642 reads for the count + 4 for the page

The window aggregate's spool was the largest single cost in the query -- larger than scanning the 218-column clustered
index it sits on -- and it is unaffected by any index, because a spool is not a table. Splitting it out keeps the ONE
result set that was the actual constraint: @TotalRows is carried into the projection as a scalar, so every row still
has a TotalRows column and EF Core sees exactly the shape 500 established.

WHAT THE SPLIT COSTS, STATED PLAINLY. The count and the page are two statements, so a row inserted or soft-deleted
between them is counted and not shown, or shown and not counted. That is not a regression: at READ COMMITTED no shared
lock survives the statement that took it, so the window-function version was never one consistent read either -- 500's
own header says so. What is new is only that the inconsistency is now visible in the shape of the code instead of
hidden in a single statement.

THE FILTER PREDICATE IS WRITTEN TWICE AND THE TWO COPIES MUST AGREE. That is the real cost of the split, and it is a
drift risk rather than a performance one: a filter added to one and not the other makes TotalRows a count of a
different set than the page. build/tmp/da4_gethandlersourcepage.probesql asserts TotalRows against an independently
computed count for every filter this procedure offers, which is the check that catches it.

--- MEASURED, THE SORT --------------------------------------------------------------------------------------------
A CASE-BASED ORDER BY DOES NOT ALWAYS SORT, AND 500's HEADER SAYS IT DOES. That claim -- "A CASE-BASED ORDER BY CANNOT
USE AN INDEX FOR ORDERING. It always sorts" -- is what deferred this measurement here in the first place, and the
measurement disproved it. Under OPTION (RECOMPILE) the parameter values are embedded as constants at compile time, so
`CASE WHEN @SortBy = N'HandlerId' AND @SortDescending = 0 THEN v.HandlerId END` folds to `v.HandlerId` and the other
thirteen expressions fold to NULL and are eliminated. What reaches the optimizer is a plain ORDER BY on one column, and
when an index provides that order it is used: measured at 400,000 rows, sorting by HandlerId cost FOUR logical reads
and produced no sort operator at all, because the page stops as soon as ROW_NUMBER reaches @Skip + @Take.

WHICH MAKES @Skip THE THING THAT SCALES, NOT THE TABLE -- but only on the indexed sort. Measured: @Skip = 0 cost 4
reads and @Skip = 10,000 cost 167, both independent of the 400,000 rows behind them. On a sort column no index orders,
the whole filtered set is sorted and the deep page costs the same as the first -- 1,642 reads either way, in memory.
Both are acceptable for a human-initiated grid refresh, which is the only caller.

500 AND 501 ARE NOT CHANGED TO MATCH, AND THAT IS DELIBERATE. Their claim is corrected in their headers, because
leaving a disproved statement in a deployed module misleads the next maintainer. Their MECHANISM is left alone:
logs.LoadRun holds one row per load run under G7 retention and logs.HandlerLoadStatus one row per handler per run, so
the window function's spool costs them a fraction of what it costs here, and rewriting two working procedures to save
milliseconds on a table that is orders of magnitude smaller is churn. If G7 retention is ever relaxed, this note is the
argument for revisiting them.

--- THE REST ------------------------------------------------------------------------------------------------------
OPTION (RECOMPILE) NOW EARNS ITS KEEP TWICE. 500 justified it on plan shapes -- six sort orders times two directions
times seven optional filters is more than one cached plan can serve, and the parameter values are what decide between a
seek and a scan. The measurement adds the second reason, which is larger: without the recompile the CASE expressions
cannot fold, so the index cannot supply the ordering and every sort is a real sort. Removing OPTION (RECOMPILE) here
would not merely risk a bad cached plan, it would give up the 4-read page.

MEASURED, THE INDEX. IX_dbo_HandlerSource_Grid (script 390) is keyed on HandlerId -- this procedure's default sort --
filtered to exactly dbo.vwHandlerSource's own predicate, and carries the other sort keys and every filter column as
INCLUDE columns. Without it the paging query scans the 218-column clustered index: 66,776 logical reads instead of
1,642, and no possibility of the 4-read page, because the ordering has nowhere to come from. This procedure does not
depend on it for correctness and will run on a database where script 390 has not been applied.

THE PROJECTION IS CURATED, AND dbo.HandlerSource HAS 218 COLUMNS. Returning all of them to a grid would ship roughly
ten times the bytes for columns no grid draws, and the wide row belongs to dbo.uspGetHandlerSourceDetail, which
returns one handler. What is here is what identifies a handler and what MDE sorts and filters by.

CONTACT COLUMNS ARE EXCLUDED FROM THE GRID ON PURPOSE. ContactFirstName, ContactLastName, ContactPhone, ContactEmail
and the contact address are a named individual at a regulated business -- Personally Identifiable Information under
MDE's own template rule -- and a grid does not need them to list handlers. They are available through the detail
procedure, which returns one record to a user who has navigated to it, so the exposure is one handler rather than five
hundred per click. SrcUpdatedBy is excluded on the same reasoning: it identifies an EPA user, and SrcUpdatedDate is
what the grid actually sorts by.

Ordinal IS PROJECTED HERE AND IS NOT IN 500. 500's header argues that Ordinal is what a "showing 51-75 of 340" caption
needs and then does not return it, leaving the client to add @Skip to a loop counter. On a grid of a dozen load runs
that is harmless; on this one, where TotalRows runs to tens of thousands and the page can be deep, an off-by-one in the
caption is a support call. It costs four bytes a row.

@SortBy IS VALIDATED AGAINST A WHITELIST AND AN UNRECOGNISED VALUE IS AN ERROR, NOT A DEFAULT. There is no dynamic SQL
in this procedure, so the whitelist is not defending a string about to be executed -- it is defending against a silent
fall-through, where a grid sending 'HandlerID ' or 'handlerName' by mistake gets rows back sorted by something else and
the defect surfaces as a user saying the sort arrows do not work. The comparison is collation-based and therefore
case-insensitive, so 'handlerId' from a JavaScript grid works.

SORTING BY SourceType SORTS BY SourceTypeSortOrder FIRST. The lookup carries its own display order precisely because
alphabetical order on a one-character code is not meaningful to anyone reading the grid. SourceType itself is the
second key so that rows sharing a sort order -- or carrying none, because SourceTypeSortOrder is nullable and a code
absent from the lookup has no order at all -- still come out deterministically. That is one sortable column costing
four CASE expressions instead of two, and it is the only column in the whitelist where the value sorted is not the
value shown.

NO CLOSED-SET VALIDATION, BECAUSE dbo.HandlerSource CLOSES NO SET. Validate a value only where this database closes it
with a CHECK constraint; that is the line 500 and 501 draw, and on this table there are no CHECK constraints at all --
verified against sys.check_constraints. @SourceType, @ActivityLocation and @FederalGeneratorCategory are therefore
passed through unvalidated, and an unmatched value is a legitimate answer of "nothing" rather than a typo worth
refusing. @ActivityLocation additionally must not be constrained to MD anywhere in this database, by MDE's decision.

AN INVERTED DATE RANGE IS REFUSED, AND 500 DOES NOT DO THIS. @ReceivedFromDate later than @ReceivedToDate cannot match
a row -- not "matches nothing today", but cannot match one ever, for any data -- so it is a caller defect rather than a
query. Returning an empty page for it is the @SortBy fall-through again: the grid shows no handlers and the user
concludes there are none. A closed-set value is a judgement call about whose set it is; an inverted range is
arithmetic, which is why this one is refused where @SourceType is not.

THE TIEBREAKER IS NOT OPTIONAL, AND IT STAYS ASCENDING IN BOTH DIRECTIONS. Every ORDER BY ends with HandlerSourceId.
HandlerName, ReceivedDate and SourceType are all non-unique -- and ReceivedDate is very non-unique, since EPA loads
arrive in batches -- so paging over them without a unique final key lets the engine return a row on page 1 and again on
page 2 while another appears on neither, silently, only under load, because the order of tied rows is undefined and the
plan may change between two calls. The tiebreaker only has to be DETERMINISTIC, not aligned with @SortDescending, so it
is not flipped; flipping it would double the plan shapes for no benefit a user could see.

@Take IS CLAMPED HERE, NOT AT THE CALLER. A grid asking for 100000 rows gets 500. The clamp is inside the procedure
because the procedure is the boundary the permission model actually enforces; a check in the web app is a check the
caller can skip. @Skip is floored at 0 and ceilinged well below the INT maximum, because the paging predicate is
`Ordinal <= @Skip + @Take` and a @Skip near that maximum makes the sum overflow and fails the call with an arithmetic
error the grid cannot act on.

AN EMPTY RESULT SET MEANS ZERO, AND THE CALLER CANNOT READ TotalRows FROM A ROW THAT IS NOT THERE. The count is known
to this procedure even when the page is empty, but there is nowhere to put it without inventing a row of NULLs and
corrupting the shape EF Core materializes. The web app should read an empty result set as "nothing matches the current
filter" and, if it had paged deep, re-request page 1 -- which is the case where TotalRows is non-zero and the page is
still empty.

@ProcName FALLS BACK TO A LITERAL, AND THE LITERAL IS THE BRANCH THE MONITOR ACTUALLY TAKES. OBJECT_NAME (@@PROCID)
returns NULL for a principal denied metadata visibility, and script 050 denies exactly that to both application logins.
Permission to run an object is not permission to see its name, so without the COALESCE every row the monitor wrote
would carry no procedure name -- the one column the monitoring grid groups by. Change both when renaming.

WHAT @KeyParameters MAY CONTAIN. Paging arguments, filter codes, dates, and HandlerId. HandlerId is a public
regulated-entity identifier rather than PII, which is the rule 501 settled, so it is logged. There is no free-text
parameter on this procedure; when dbo.uspSearchHandlerSource adds one, the term must NOT be logged, because a search
term is whatever a user typed and this database holds regulated-entity records. From MDE's own template: do NOT include
parameters such as passwords and Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- The grid's default: first page of 50, by handler ID.
EXEC dbo.uspGetHandlerSourcePage;

-- Second page of 25, largest generators first by name.
EXEC dbo.uspGetHandlerSourcePage @Skip = 25, @Take = 25, @SortBy = N'HandlerName', @SortDescending = 0;

-- Large Quantity Generators EPA changed since July, most recently changed first.
EXEC dbo.uspGetHandlerSourcePage @FederalGeneratorCategory = N'1', @SrcUpdatedFromDate = '2026-07-01'
                               , @SortBy = N'SrcUpdatedDate', @SortDescending = 1;

-- One handler, all its current-record rows across source types.
EXEC dbo.uspGetHandlerSourcePage @HandlerId = N'MD0000123456', @SortBy = N'SourceType';

Measured at a stated assumed volume of 400,000 rows with 100,000 current, on IX_dbo_HandlerSource_Grid: the count is
1,642 logical reads, the default page is 4, a deep page at @Skip = 10,000 is 167, and a sort on a column the index does
not order is 1,642 plus an in-memory sort -- around 15 to 30 ms end to end. Without the index the paging query scans the
218-column clustered index at 66,776 reads, about 215 ms. dbo.HandlerSource holds 50 rows on the development
workstation, all soft-deleted, so those figures come from a temp table built with SELECT TOP (0) * INTO ... FROM
dbo.HandlerSource -- real column types, therefore real widths -- at assumed volumes, because G22 is credential-gated
and the true row count is measured in F2. The harnesses are build/tmp/measure503_sort.probesql and
build/tmp/io503alt2.probesql.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the first paged read over the handler mirror.
											Carries out the sort-cost measurement 500 and 501 deferred here, and deviates
											from the block 500 settled in three places as a result: TotalRows is its own
											COUNT (*) rather than a window function, there is no @IncludeDeleted, and
											Ordinal is projected. Added IX_dbo_HandlerSource_Grid (script 390) as the
											outcome of the same measurement.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE dbo.uspGetHandlerSourcePage
    -- Paging and sorting first, in the order 500 settled. @SortBy defaults to HandlerId ascending rather than to a
    -- newest-first date, because a handler list is read as a list and a load-run list is read as a log.
      @Skip                     INT           = 0
    , @Take                     INT           = 50
    , @SortBy                   NVARCHAR (50) = N'HandlerId'
    , @SortDescending           BIT           = 0
    -- Filters. NULL means no filter, always, so a grid with nothing typed into it sends nothing.
    , @HandlerId                NVARCHAR (12) = NULL
    , @ActivityLocation         NVARCHAR (2)  = NULL
    , @SourceType               NVARCHAR (1)  = NULL
    , @FederalGeneratorCategory NVARCHAR (1)  = NULL
    , @ReceivedFromDate         DATE          = NULL
    , @ReceivedToDate           DATE          = NULL
    , @SrcUpdatedFromDate       DATE          = NULL
    , @SrcUpdatedToDate         DATE          = NULL
    -- No @IncludeDeleted and no @CurrentRecord: dbo.vwHandlerSource has already decided both. See the header.
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- -------------------------------------------------------------------------------------------------
    -- AR8 instrumentation. Boilerplate: copy verbatim. The error-only variant -- no @ExecutionId and no
    -- @Comments, because nothing is written on the successful path.
    -- -------------------------------------------------------------------------------------------------
    -- Not a fallback for odd cases: this literal is what the monitor login actually logs, because
    -- metadata visibility is denied to it. See the header note. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                      + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                     , N'[dbo].[uspGetHandlerSourcePage]')
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

    -- The page, as keys. Ordinal preserves the sort so the projecting SELECT needs one ORDER BY column
    -- instead of repeating all fourteen CASE expressions. TotalRows is NOT a column here: it is a scalar
    -- now rather than a window function, which is the measured deviation the header explains.
    DECLARE @Page TABLE
    (
        Ordinal         INT NOT NULL PRIMARY KEY,
        HandlerSourceId INT NOT NULL
    );

    -- COALESCE on every argument, including the integers: CONCAT renders NULL as an empty string, so an
    -- omitted @Take would log as `Take=,` and read as a truncated message rather than as a NULL. These
    -- are the values the CALLER sent, before clamping. HandlerId is logged deliberately -- it is a
    -- public regulated-entity identifier, not PII. There is no free-text parameter here to exclude.
    SET @KeyParameters = CONCAT (N'Skip=', COALESCE (CAST (@Skip AS NVARCHAR (11)), N'(null)')
                               , N', Take=', COALESCE (CAST (@Take AS NVARCHAR (11)), N'(null)')
                               , N', SortBy=', COALESCE (@SortBy, N'(null)')
                               , N', SortDescending=', @SortDescending
                               , N', HandlerId=', COALESCE (@HandlerId, N'(any)')
                               , N', ActivityLocation=', COALESCE (@ActivityLocation, N'(any)')
                               , N', SourceType=', COALESCE (@SourceType, N'(any)')
                               , N', FederalGeneratorCategory=', COALESCE (@FederalGeneratorCategory, N'(any)')
                               , N', ReceivedFromDate=', COALESCE (CONVERT (NVARCHAR (10), @ReceivedFromDate, 23), N'(any)')
                               , N', ReceivedToDate=', COALESCE (CONVERT (NVARCHAR (10), @ReceivedToDate, 23), N'(any)')
                               , N', SrcUpdatedFromDate=', COALESCE (CONVERT (NVARCHAR (10), @SrcUpdatedFromDate, 23), N'(any)')
                               , N', SrcUpdatedToDate=', COALESCE (CONVERT (NVARCHAR (10), @SrcUpdatedToDate, 23), N'(any)'));

    BEGIN TRY

        -- No logs.uspStartExecutionLogging call. See the header: this read records failures only, and the
        -- CATCH below inserts an orphan row with @ExecutionLogId = NULL rather than updating one.

        -- ==========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ====
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 1. Validation and clamping. First, and before any work -- a refusal should cost nothing but
        --    the parse.
        -- ------------------------------------------------------------------------------------------
        IF @SortBy IS NULL
           OR @SortBy NOT IN (N'HandlerId', N'HandlerName', N'ReceivedDate', N'SrcUpdatedDate'
                            , N'SourceType', N'SiteLocationCity')
        BEGIN
            SET @Failure = CONCAT (N'@SortBy = ', COALESCE (N'''' + @SortBy + N'''', N'NULL')
                                 , N' is not a sortable column of this grid. Use one of HandlerId, ')
                         + N'HandlerName, ReceivedDate, SrcUpdatedDate, SourceType, SiteLocationCity. '
                         + N'The value is rejected rather than defaulted so that a mistyped sort column '
                         + N'cannot come back as rows sorted by something else.';
            ;THROW 50000, @Failure, 1;
        END;

        -- The two date ranges. An inverted range cannot match a row for ANY data, so it is arithmetic
        -- rather than a judgement about whose set a value belongs to -- which is why these are refused
        -- and @SourceType is not. See the header. dbo.HandlerSource carries no CHECK constraints, so
        -- there is no closed set here to validate against.
        IF @ReceivedFromDate IS NOT NULL
           AND @ReceivedToDate IS NOT NULL
           AND @ReceivedFromDate > @ReceivedToDate
        BEGIN
            SET @Failure = CONCAT (N'@ReceivedFromDate = ', CONVERT (NVARCHAR (10), @ReceivedFromDate, 23)
                                 , N' is later than @ReceivedToDate = '
                                 , CONVERT (NVARCHAR (10), @ReceivedToDate, 23)
                                 , N', which cannot match a row for any data. Rejected rather than ')
                         + N'returned as an empty page, because an empty grid reads as "there are no '
                         + N'handlers" rather than as "the dates are the wrong way round".';
            ;THROW 50000, @Failure, 1;
        END;

        IF @SrcUpdatedFromDate IS NOT NULL
           AND @SrcUpdatedToDate IS NOT NULL
           AND @SrcUpdatedFromDate > @SrcUpdatedToDate
        BEGIN
            SET @Failure = CONCAT (N'@SrcUpdatedFromDate = ', CONVERT (NVARCHAR (10), @SrcUpdatedFromDate, 23)
                                 , N' is later than @SrcUpdatedToDate = '
                                 , CONVERT (NVARCHAR (10), @SrcUpdatedToDate, 23)
                                 , N', which cannot match a row for any data. Rejected for the same ')
                         + N'reason as the received-date range.';
            ;THROW 50000, @Failure, 1;
        END;

        -- GREATEST/LEAST are SQL Server 2022 and in scope. COALESCE first, so NULL means "the default"
        -- and not "the floor": a grid that omits @Take wants 50 rows, not 1.
        SET @Skip = LEAST (GREATEST (COALESCE (@Skip, 0), 0), 2000000000);
        SET @Take = LEAST (GREATEST (COALESCE (@Take, 50), 1), 500);

        SET @ContextMessage = CONCAT (N'Skip=', @Skip, N', Take=', @Take
                                    , N', SortBy=', @SortBy, N', SortDescending=', @SortDescending);

        -- No BEGIN TRANSACTION, and the omission is deliberate -- see the header. Nothing here writes at
        -- all on the successful path, and READ COMMITTED gives the statements below no shared
        -- consistency that a transaction could preserve.

        -- ------------------------------------------------------------------------------------------
        -- 2. The filtered total, as its own statement. Measured cheaper than COUNT (*) OVER () by two
        --    orders of magnitude -- see the header. THE WHERE CLAUSE HERE AND THE ONE IN SECTION 3
        --    MUST STAY IDENTICAL: a filter added to one and not the other makes TotalRows a count of a
        --    different set than the page, and build/tmp/da4_gethandlersourcepage.probesql is what
        --    catches that.
        -- ------------------------------------------------------------------------------------------
        SELECT @TotalRows = COUNT (*)
          FROM dbo.vwHandlerSource AS v
         WHERE (@HandlerId                IS NULL OR v.HandlerId                         =  @HandlerId)
           AND (@ActivityLocation         IS NULL OR v.ActivityLocation                  =  @ActivityLocation)
           AND (@SourceType               IS NULL OR v.SourceType                        =  @SourceType)
           AND (@FederalGeneratorCategory IS NULL OR v.WasteFederalGeneratorCategoryCode  =  @FederalGeneratorCategory)
           AND (@ReceivedFromDate         IS NULL OR v.ReceivedDate                      >= @ReceivedFromDate)
           AND (@ReceivedToDate           IS NULL OR v.ReceivedDate                      <= @ReceivedToDate)
           AND (@SrcUpdatedFromDate       IS NULL OR v.SrcUpdatedDate                    >= @SrcUpdatedFromDate)
           AND (@SrcUpdatedToDate         IS NULL OR v.SrcUpdatedDate                    <= @SrcUpdatedToDate)
        OPTION (RECOMPILE);

        -- ------------------------------------------------------------------------------------------
        -- 3. The page, as keys. One CASE per sortable column per direction -- a single CASE listing
        --    several columns would apply data-type precedence to its branches and sort by a converted
        --    value, so ReceivedDate and HandlerName in one CASE would sort the grid as text or fail
        --    outright. HandlerSourceId last, always.
        -- ------------------------------------------------------------------------------------------
        -- ROW_NUMBER rather than OFFSET/FETCH: with OFFSET/FETCH the outer query would have to trust
        -- that a derived table hands its rows on in the order it produced them, which is nowhere
        -- guaranteed, and Ordinal would not exist to be projected. Under OPTION (RECOMPILE) the CASE
        -- expressions below fold to constants and thirteen of the fourteen disappear, which is how the
        -- index comes to supply the ordering -- see MEASURED, THE SORT in the header.
        INSERT INTO @Page (Ordinal, HandlerSourceId)
        SELECT p.Ordinal, p.HandlerSourceId
          FROM (SELECT ROW_NUMBER () OVER (
                       ORDER BY CASE WHEN @SortBy = N'HandlerId'        AND @SortDescending = 0 THEN v.HandlerId           END ASC
                              , CASE WHEN @SortBy = N'HandlerId'        AND @SortDescending = 1 THEN v.HandlerId           END DESC
                              , CASE WHEN @SortBy = N'HandlerName'      AND @SortDescending = 0 THEN v.HandlerName         END ASC
                              , CASE WHEN @SortBy = N'HandlerName'      AND @SortDescending = 1 THEN v.HandlerName         END DESC
                              , CASE WHEN @SortBy = N'ReceivedDate'     AND @SortDescending = 0 THEN v.ReceivedDate        END ASC
                              , CASE WHEN @SortBy = N'ReceivedDate'     AND @SortDescending = 1 THEN v.ReceivedDate        END DESC
                              , CASE WHEN @SortBy = N'SrcUpdatedDate'   AND @SortDescending = 0 THEN v.SrcUpdatedDate      END ASC
                              , CASE WHEN @SortBy = N'SrcUpdatedDate'   AND @SortDescending = 1 THEN v.SrcUpdatedDate      END DESC
                              , CASE WHEN @SortBy = N'SiteLocationCity' AND @SortDescending = 0 THEN v.SiteLocationCity    END ASC
                              , CASE WHEN @SortBy = N'SiteLocationCity' AND @SortDescending = 1 THEN v.SiteLocationCity    END DESC
                              -- SourceType sorts by the lookup's own display order, then by the code, so
                              -- that a code the lookup does not carry -- SourceTypeSortOrder is nullable
                              -- -- still comes out deterministically. See the header.
                              , CASE WHEN @SortBy = N'SourceType'       AND @SortDescending = 0 THEN v.SourceTypeSortOrder END ASC
                              , CASE WHEN @SortBy = N'SourceType'       AND @SortDescending = 1 THEN v.SourceTypeSortOrder END DESC
                              , CASE WHEN @SortBy = N'SourceType'       AND @SortDescending = 0 THEN v.SourceType          END ASC
                              , CASE WHEN @SortBy = N'SourceType'       AND @SortDescending = 1 THEN v.SourceType          END DESC
                              -- The tiebreaker. Not decoration: HandlerName, ReceivedDate and SourceType
                              -- are all non-unique, and without it paging over them repeats and skips
                              -- rows. Ascending in both directions on purpose -- it only has to be
                              -- deterministic.
                              , v.HandlerSourceId ASC) AS Ordinal
                     , v.HandlerSourceId
                  FROM dbo.vwHandlerSource AS v
                 WHERE (@HandlerId                IS NULL OR v.HandlerId                         =  @HandlerId)
                   AND (@ActivityLocation         IS NULL OR v.ActivityLocation                  =  @ActivityLocation)
                   AND (@SourceType               IS NULL OR v.SourceType                        =  @SourceType)
                   AND (@FederalGeneratorCategory IS NULL OR v.WasteFederalGeneratorCategoryCode  =  @FederalGeneratorCategory)
                   AND (@ReceivedFromDate         IS NULL OR v.ReceivedDate                      >= @ReceivedFromDate)
                   AND (@ReceivedToDate           IS NULL OR v.ReceivedDate                      <= @ReceivedToDate)
                   AND (@SrcUpdatedFromDate       IS NULL OR v.SrcUpdatedDate                    >= @SrcUpdatedFromDate)
                   AND (@SrcUpdatedToDate         IS NULL OR v.SrcUpdatedDate                    <= @SrcUpdatedToDate)) AS p
         WHERE p.Ordinal >  @Skip
           AND p.Ordinal <= @Skip + @Take
        OPTION (RECOMPILE);

        SET @RowsOnPage = @@ROWCOUNT;

        SET @ContextMessage = CONCAT (@ContextMessage, N', RowsOnPage=', @RowsOnPage
                                    , N', TotalRows=', @TotalRows);

        -- ------------------------------------------------------------------------------------------
        -- 4. The projection, and the last statement in the block. Joined back through the VIEW rather
        --    than the base table, so the AR7 filter is applied on this path too -- at most @Take
        --    clustered-index seeks. Curated: 218 columns are available and the grid draws twenty.
        --    Contact columns are excluded as PII; see the header.
        -- ------------------------------------------------------------------------------------------
        SELECT p.Ordinal
             , v.HandlerSourceId
             , v.HandlerId
             , v.ActivityLocation
             , v.SourceType
             , v.SourceTypeDescription
             , v.Sequence
             , v.ReceivedDate
             , v.HandlerName
             , v.SiteLocationAddress1
             , v.SiteLocationCity
             , v.SiteLocationStateCode
             , v.SiteLocationZip
             , v.SiteLocationCountyDescription
             , v.WasteFederalGeneratorCategoryCode
             , v.WasteFederalGeneratorCategoryDescription
             -- The three activity flags MDE filters a handler list by in practice. The other forty-odd
             -- Waste* flags are in the detail procedure.
             , v.WasteTsd
             , v.WasteTransporter
             , v.WasteRecyclerActivity
             , v.SrcUpdatedDate
             , v.auditModifiedDateUtc
             -- A scalar, not a window function, and the same number for every row on the page. Zero
             -- when nothing matches -- which the caller cannot see, because there is then no row to
             -- read it from.
             , @TotalRows AS TotalRows
          FROM @Page                AS p
          JOIN dbo.vwHandlerSource  AS v ON v.HandlerSourceId = p.HandlerSourceId
         ORDER BY p.Ordinal;

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

        -- NO ROLLBACK. This procedure opens no transaction, so it has nothing of its own to roll back
        -- and any transaction live here belongs to the caller -- rolling it back would discard work this
        -- procedure never did. ROLLBACK is also illegal inside INSERT ... EXEC (error 8004), where it
        -- would replace the error being reported and abort this CATCH before the failure was recorded.
        -- A read that cannot record its own failure is precisely the defect this project is guarding
        -- against.

        -- Recovered into the context so a failure still says how far it got, since there is no start row
        -- carrying it. @TotalRows and @RowsOnPage are variables, so they survive whatever the caller
        -- does to its transaction.
        SET @ContextMessage = CONCAT (COALESCE (@ContextMessage, N'(before clamping)')
                                    , N', ElapsedMs='
                                    , LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, SYSUTCDATETIME ())
                                           , CAST (2147483647 AS BIGINT)));

        -- @ExecutionLogId = NULL on purpose: there is no start row to update, so this takes the
        -- orphan-insert branch of logs.uspRecordExecutionErrorUpdate. The row is recognisable by
        -- Successful = 0 with EndDateUtc set and ElapsedMilliseconds NULL, which is why the duration is
        -- carried in the context message instead. Swallows everything by design, so this call cannot
        -- mask the error below it.
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
        -- client could no longer tell a deadlock from a bad @SortBy. The leading semicolon is required:
        -- a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetHandlerSourcePage'
    , @Description = N'Returns one page of the CURRENT version of Maryland handler records to the monitoring web app, filtered and sorted, with the filtered total carried on every row as TotalRows. Reads dbo.vwHandlerSource, never the base table, so the AR7 soft-delete filter and CurrentRecord = 1 are applied by the view; consequently there is no @IncludeDeleted parameter and IsDeleted is not projected, because through this view it is a constant 0. The projection is curated -- dbo.HandlerSource has 218 columns and the grid draws twenty -- and contact name, phone, email and address are excluded as PII, available instead through dbo.uspGetHandlerSourceDetail, which returns one handler rather than five hundred. @Take is clamped to 1..500 and @Skip floored at 0 inside the procedure, because the procedure is the boundary the permission model enforces. @SortBy is checked against a whitelist of six columns and an unrecognised value is REJECTED rather than defaulted, so a mistyped sort column cannot come back as rows sorted by something else; sorting by SourceType sorts by the lookup''s SourceTypeSortOrder first, then by the code. An inverted date range is refused because it cannot match a row for any data; @SourceType, @ActivityLocation and @FederalGeneratorCategory are deliberately NOT validated, because dbo.HandlerSource carries no CHECK constraints and no state column in this database is constrained to MD. ORDER BY uses one CASE per column per direction and always ends with HandlerSourceId, so paging over a non-unique key cannot repeat and skip rows. TotalRows is its own COUNT (*) rather than COUNT (*) OVER (): measured at an assumed 400,000 rows the window aggregate cost a 202,152-read worktable spool, larger than scanning the 218-column clustered index, and the split preserves the single result set EF Core requires. Under OPTION (RECOMPILE) the CASE expressions fold to constants at compile time, so IX_dbo_HandlerSource_Grid supplies the ordering on the default sort and the page stops early -- four logical reads. An empty result set means TotalRows is zero: the caller cannot read the total from a row that is not there. Instrumented for FAILURES ONLY, with no successful-path row, and it opens no transaction and therefore never rolls one back. EXECUTE is granted to RCRAInfoMonitorRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The monitor only. The console app never pages this grid -- it merges what the API hands it through
-- dbo.uspMergeHandlerSourceBatch, which the monitor in turn cannot execute. Both directions are
-- asserted by build/check_permission_posture.py, so the asymmetry is measured rather than intended.
--
-- Ownership chaining carries the SELECT on dbo.vwHandlerSource, and through it on dbo.HandlerSource,
-- and the INSERT on logs.ExecutionLog, through this grant -- so the monitor login holds no direct
-- permission on any of the three (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetHandlerSourcePage TO RCRAInfoMonitorRole;
END;
GO

PRINT N'503: dbo.uspGetHandlerSourcePage created or altered, EXECUTE granted to the monitor role.';
GO
