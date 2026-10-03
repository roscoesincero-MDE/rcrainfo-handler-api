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
ObjectName:   dbo.uspGetHandlerSourceHistoryPage
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Returns one page of EVERY version of ONE handler, newest first, to the monitoring web app. It is the screen an operator
reaches by clicking a row in dbo.uspGetHandlerSourcePage's grid and asking "what did this handler look like before?"

It reads dbo.vwHandlerSourceHistory, which is dbo.HandlerSource filtered to IsDeleted = 0 with the four deletion-stamp
columns masked, and NOT dbo.vwHandlerSource, which additionally fixes CurrentRecord = 1. Those two views are the only
structural enforcement of AR7 in this database; nothing in 50x reads the base table.

------------------------------------------------------------------------------------------------------------------------
@HandlerId IS REQUIRED, AND THAT IS THE DECISION EVERY OTHER DECISION HERE RESTS ON

503's filters are all optional -- a grid with nothing typed into it sends nothing and gets the first page of Maryland.
This procedure refuses that call. A version history is asked ABOUT A HANDLER; there is no screen anywhere in the
monitoring app that asks "show me every version of every handler," and if one existed it would be the most expensive
query in the database: four times the grid's row count with CurrentRecord unavailable to narrow it. Making the parameter
required is therefore not a restriction bolted onto a general procedure, it is what the procedure is.

Three things follow from it.

  * IT NEEDS NO NEW INDEX. UX_dbo_HandlerSource_Natural is UNIQUE NONCLUSTERED on (HandlerId, SourceType, Sequence)
    WHERE IsDeleted = 0, and that filter is EXACTLY dbo.vwHandlerSourceHistory's predicate -- so one handler's history
    is a seek on the leading key of an index whose filter the view already matches. 503 needed
    IX_dbo_HandlerSource_Grid (script 390) because its query has no required equality at all. This one gets its
    access path for free, and the reason is worth stating rather than enjoying quietly: if @HandlerId is ever made
    optional, that seek becomes a scan of the whole mirror and the index question reopens.
  * THE ROW COUNT IS BOUNDED, which changes the TotalRows decision -- see below.
  * AN EMPTY @HandlerId IS REFUSED, NOT MATCHED. `@HandlerId = N''` is a legal equality that costs a seek and finds
    nothing, so it would come back as an empty page -- and an empty version history reads as "this handler has never
    changed", which is a statement about the data. The truth is that the caller did not say which handler. A grid
    sending an untouched textbox must get a message, not a plausible answer. Whitespace is trimmed before the test for
    the same reason.
  * @HandlerId IS DECLARED NVARCHAR (20) AND THE COLUMN IS NVARCHAR (12). That is not a mistake and it is not
    generosity. A parameter declared at the column's own width TRUNCATES SILENTLY before the procedure ever runs, so
    ` MDPROBE05041 ` -- fifteen characters, a real twelve-character id with a space either side, which is what pasting
    from a spreadsheet produces -- arrives as `  MDPROBE054`, and the trim inside the procedure then cannot recover it.
    It would come back as an empty page for an id that exists, which is the worst of the outcomes available: the screen
    would be saying a handler has no history when the truth is that two characters were thrown away in the call. The
    probe caught this on its first run against an NVARCHAR (12) parameter, and the fix is the wider parameter rather
    than a wider column: the extra width exists ONLY so the trim has something to trim, and the trimmed value is then
    compared against a twelve-character column, so nothing longer than twelve real characters can ever match. That is
    the correct outcome for a genuinely over-long id -- an empty page, not a truncated match on some other handler.

------------------------------------------------------------------------------------------------------------------------
TotalRows IS COUNT (*) OVER () HERE, WHICH IS THE OPPOSITE OF WHAT 503 DOES, AND THE MEASUREMENT IS WHY

503 replaced COUNT (*) OVER () with its own scalar COUNT (*) on measured grounds: at an assumed 400,000 rows with
100,000 visible the window aggregate built a worktable spool costing 202,152 logical reads, larger than scanning the
218-column clustered index outright (66,776), and no index reduces it because a spool is not a table.

That measurement is a statement about the number of rows the aggregate spools, and here that number is the versions of
ONE handler. EPA keeps a notification per submission, so it is single digits for most handlers and low double digits for
an old one. A spool of forty rows costs nothing measurable.

So the honest application of the measurement is to reverse 503's deviation rather than to copy it, because the split
503 pays for has a real price and this procedure has no reason to pay it: THE FILTER PREDICATE WOULD BE WRITTEN TWICE.
That is the drift risk 503's own header names -- a filter added to the count and not the page makes TotalRows a count of
a different set than the rows beside it, which is a silent wrong number that survives review because both halves look
right in isolation. One copy of the predicate cannot drift from itself.

WHAT WOULD INVALIDATE THIS, STATED SO IT IS CHECKABLE: making @HandlerId optional. Nothing else. The bound is the
required equality, not the shape of the query, and a reader who relaxes that parameter must move TotalRows to a scalar
COUNT (*) in the same change.

------------------------------------------------------------------------------------------------------------------------
CurrentRecord IS PROJECTED, AND THIS IS THE ONLY SCREEN WHERE IT CAN BE

503 cannot project it: reading through dbo.vwHandlerSource it is a constant 1, and a column that is always 1 is
decoration. Here it is the point of the result set -- a list of versions in which one of them is live -- so it is
projected immediately after Sequence, which is the column that identifies the version it qualifies.

TWO OPERATIONAL CONSEQUENCES, AND NEITHER IS INCIDENTAL.

First, CurrentRecord is NULLABLE, and `CurrentRecord = 1` excludes NULL -- so a handler whose flag has never been
stamped by dbo.uspReconcileCurrentRecord is INVISIBLE to 503's grid and its rows are visible only here. That makes this
procedure the diagnostic screen for the gap 503's header names: a handler that the grid swears does not exist can be
looked up here, and its versions will show CurrentRecord NULL on every row, which names the remedy.

Second, AR4 records the defect this mirror is most exposed to: `CurrentRecord` flags go stale when a new version
arrives, leaving TWO rows both marked current for one handler, and downstream Phase 2 ETS then reads the wrong record.
This is the screen on which that becomes VISIBLE -- two rows showing 1. It is not asserted here, because a read must
not decide that data is wrong; dbo.uspReconcileCurrentRecord owns the correction and the invariant. But an operator
looking at a suspect handler can see it, which is the whole reason the column is in the projection rather than a
`CASE WHEN CurrentRecord = 1 THEN N'Current' ELSE N'Superseded' END` label. A label would have hidden the third state.

------------------------------------------------------------------------------------------------------------------------
NEWEST FIRST, WHICH IS A DIFFERENT DEFAULT THAN 503's

503 defaults to HandlerId ascending: a handler list is read as a list, and a list is alphabetical. A version history is
read as a log -- what is it now, and what was it before -- so this defaults to ReceivedDate DESCENDING, with
@SortDescending defaulting to 1 rather than 0. That difference between two sibling procedures is deliberate and is the
kind of thing a reader assumes is a copy-paste slip, so: it is not.

@SortBy IS VALIDATED AGAINST A WHITELIST OF FIVE and an unrecognised value is REJECTED rather than defaulted, exactly as
in 500, 501 and 503, so a mistyped sort column cannot come back as rows sorted by something else. The five are
ReceivedDate, SrcUpdatedDate, Sequence, SourceType and HandlerName.

HandlerName IS ON THAT LIST FOR A REASON THAT LOOKS WRONG AT FIRST. Within one handler most columns barely vary, and
offering a sort over a column that does not move would be a lie about the data. HandlerName is the exception: a name
change is one of the most common reasons a handler has history at all, so sorting by it groups the versions that share
a name and shows where it changed. SiteLocationCity is NOT offered -- a relocation is rare enough that the sort would be
a no-op on nearly every handler.

SORTING BY SourceType SORTS BY SourceTypeSortOrder FIRST, then by the code, as in 503, so a code the lookup does not
carry -- SourceTypeSortOrder is nullable -- still comes out in a deterministic place rather than wherever the plan
happens to put it.

THE TIEBREAKER IS HandlerSourceId ASCENDING IN BOTH DIRECTIONS. All five sortable columns are non-unique within one
handler -- Sequence is unique only per (HandlerId, SourceType), and two source types can both have Sequence 1 -- so
without it, paging repeats and skips rows. It stays ascending when @SortDescending = 1 because it only has to be
DETERMINISTIC; flipping it would be symmetry for its own sake.

------------------------------------------------------------------------------------------------------------------------
THIS ONE ACTUALLY SORTS, AND AT THIS CARDINALITY THAT IS THE RIGHT ANSWER

Measured for 503: under OPTION (RECOMPILE) the parameter values are embedded as constants at compile time, so
`CASE WHEN @SortBy = N'ReceivedDate' AND @SortDescending = 1 THEN v.ReceivedDate END` folds to `v.ReceivedDate` and the
other eleven expressions fold to NULL and are eliminated. Where an index provides the folded order, no Sort operator
appears at all -- 4 logical reads against 400,000 rows.

NO INDEX PROVIDES THIS ORDER. UX_dbo_HandlerSource_Natural is keyed (HandlerId, SourceType, Sequence), so it orders the
Sequence and SourceType sorts and not the three date-and-name ones. Those sort. That is correct and no index should be
added for it: the input to the sort is the versions of one handler, and a sort of forty rows in memory is not a cost
worth an index the loader would maintain on every write. The claim being made here is narrow and it is the one the
measurement supports -- a CASE-based ORDER BY sorts unless an index happens to provide the folded order, and here the
sort is over a set small enough that it does not matter.

OPTION (RECOMPILE) IS STILL WARRANTED, for the folding rather than for cardinality. @SourceType is optional, so the
`(@SourceType IS NULL OR ...)` predicate is either a filter or nothing depending on the call, and a plan cached for one
shape is wrong for the other. The recompile is per call and the query is one seek.

------------------------------------------------------------------------------------------------------------------------
NO DATE-RANGE FILTERS, AND THE OMISSION IS A DECISION

503 offers four date bounds because it pages Maryland. Here the whole result set is the versions of one handler. A
filter narrowing twelve rows to nine is UI noise that costs a duplicated predicate and a pair of validation refusals,
and the operator can read twelve rows. @SourceType is the one optional filter kept, because it partitions the history
into independent streams -- a handler's Part A versions and its notification versions are different documents, and
looking at one is a real question rather than a narrowing of a small list.

There is consequently NO INVERTED-RANGE REFUSAL here, which 503 has and this does not. It has no range to invert.

------------------------------------------------------------------------------------------------------------------------
THE PROJECTION IS 503's TWENTY-TWO PLUS CurrentRecord, AND THE PII EXCLUSIONS ARE IDENTICAL

dbo.HandlerSource has 218 columns. ContactFirstName, ContactLastName, ContactPhone, ContactEmail and the contact
address are EXCLUDED, and so is SrcUpdatedBy, which identifies an EPA user. The reasoning is 503's and it is about
volume rather than sensitivity in the abstract: a paged screen returns up to five hundred rows and a detail screen
returns one, so the same column is a different exposure in each. dbo.uspGetHandlerSourceDetail carries them.

THE EXCLUSION APPLIES HERE EVEN THOUGH THE PAGE IS SMALL, and that is worth one sentence because the volume argument
appears to weaken it. A history page for one handler might return twelve rows, not five hundred. But it returns twelve
DIFFERENT VERSIONS OF THE SAME PERSON'S CONTACT DETAILS -- every address and phone number that handler has ever filed
-- which is a worse disclosure than one current record, not a better one. The exclusion is stronger here, not weaker.

IsDeleted IS NOT PROJECTED. Through dbo.vwHandlerSourceHistory it is a constant 0, and the four deletion-stamp columns
the view masks are therefore NULL on every row it returns. There is likewise NO @IncludeDeleted parameter: honouring it
would mean restating the AR7 filter inside this procedure, which is the one thing the two views exist to own.

------------------------------------------------------------------------------------------------------------------------
Ordinal IS PROJECTED, AS IN 503

The grid's "showing 1-10 of 12" caption needs the absolute row number of each row. Without it the client adds @Skip to
a loop counter, which is right on every page but the last. 500 does not project it and should; 503 and this do.

------------------------------------------------------------------------------------------------------------------------
NO CLOSED-SET VALIDATION, BECAUSE dbo.HandlerSource CLOSES NO SET

sys.check_constraints reports that dbo.HandlerSource carries NO CHECK constraints at all, so @SourceType has no
permitted-value list to be checked against and an unrecognised code returns an empty page rather than a refusal.
[R17]'s rule -- a closed-set filter must REJECT an unrecognised value -- is scoped to sets THIS DATABASE closes, and a
source type EPA invents next quarter must stay lookupable on the screen that would reveal it. build/
check_closed_set_filters.py enforces that scoping mechanically by comparing every equality filter against
sys.check_constraints rather than against a list in a document.

@HandlerId is likewise not pattern-validated. An EPA handler id is twelve characters and MD ids begin MD, but the
column is NVARCHAR (12) with no CHECK, out-of-state ids appear in the mirror through other-ids, and a read that
rejected a shape the loader accepts would make a row unfindable on the screen that exists to find it.

------------------------------------------------------------------------------------------------------------------------
@Take IS CLAMPED HERE, NOT AT THE CALLER

1..500, the same range as 503, and @Skip is floored at 0 and ceilinged at 2,000,000,000 -- the paging predicate is
`Ordinal <= @Skip + @Take` and a @Skip near the INT maximum would overflow the sum and fail the call with an arithmetic
error the grid cannot act on. The ceiling of 500 is generous for a history page and stays anyway: G22, the initial-load
volume, is measured in F2, so EPA's maximum version depth for a single handler is not known here, and a ceiling chosen
to fit an assumption about that would be the assumption entering the code.

AN EMPTY RESULT SET MEANS ZERO, AND THE CALLER CANNOT READ IT. TotalRows arrives ON the rows, so a page past the end of
the history carries no row and therefore no total. That is inherent to the single-result-set shape DA5 requires and it
is the same in 500, 501 and 503; a client showing "of N" must keep N from the page it asked for.

------------------------------------------------------------------------------------------------------------------------
ERROR-ONLY INSTRUMENTATION

Per MDE's DA1 narrowing: every procedure that WRITES is instrumented; a read is instrumented only where the plan names
it. This is a read, so there is NO logs.uspStartExecutionLogging call and no successful-path row -- a monitoring grid
that refreshes is the highest-frequency caller in the system, and instrumenting it would make logs.ExecutionLog mostly
a record of people looking at things, which directly worsens the open G7 retention question.

THAT MAKES THE TRY/CATCH THE ONLY THING RECORDING ANYTHING, WHICH IS THE POINT. MDE's requirement is that ANY error is
recorded, and it comes from a real defect in another MDE application: a procedure whose body was a single SELECT called
a scalar UDF, the UDF errored, the procedure wrote and updated nothing, and the error was recorded NOWHERE. So the
CATCH here inserts an ORPHAN row through logs.uspRecordExecutionError with @ExecutionLogId = NULL, recognisable by
Successful = 0 with EndDateUtc set and ElapsedMilliseconds NULL. The duration is recovered into ContextMessage as
`ElapsedMs=` precisely because that NULL is the orphan's signature and must not be filled in.

NO TRANSACTION, AND THEREFORE NO ROLLBACK. Nothing here writes on the successful path, and at READ COMMITTED a
transaction over these statements would preserve no consistency a caller could use while blocking the tables the loader
is writing. The CATCH therefore contains no ROLLBACK -- A PROCEDURE ROLLS BACK ONLY WHAT IT OPENED. `XACT_STATE () <> 0`
is equally true when the CALLER owns the transaction, so the guard that reads as a safety net would discard a writer's
uncommitted work on a mistyped @SortBy. ROLLBACK is also illegal inside INSERT ... EXEC (error 8004), where it would
replace the error being reported and abort the CATCH before the failure was recorded. Gated by
build/check_stored_headers.py against the DEPLOYED module.

G37 APPLIES HERE UNCHANGED AND IS STILL OPEN. If a caller wraps this call in its own transaction and that transaction is
doomed, error 3930 makes it impossible to write the log row BY ANY MEANS until the transaction is rolled back -- which
this procedure must not do, because it does not own it. The nineteen writers escape only because they roll back their
own first. logs.uspRecordExecutionError names 3930 in its severity-10 warning so the one action that fixes it is
guessable from the message, and build/tmp/da4_gethandlersourcehistorypage.probesql pins the boundary with an assertion
written to report CHANGED rather than PASS if it ever stops being true.

WHAT @KeyParameters MAY CONTAIN: identifiers, dates and counts only, per MDE's own template comment -- "do NOT include
parameters such as passwords and Personally Identifiable Information (PII)". @HandlerId IS logged; it is a public
regulated-entity identifier, not PII. There is no free-text parameter here. THE STANDING RULE FOR WHAT REMAINS IN 50x:
when dbo.uspSearchHandlerSource adds a free-text search parameter, THE TERM MUST NOT BE LOGGED, because a search term is
whatever a user typed and this database holds regulated-entity records.

@ProcName FALLS BACK TO A LITERAL. QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID)) returns NULL for a caller without metadata
visibility, and the monitor login has none -- so the COALESCE literal is not a defensive nicety, it is what the monitor
actually logs. Keep it in step with the CREATE name above.

========================================================================================================================
Example Usage:

-- The call the monitoring app makes when an operator clicks a row in 503's grid.
EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId = N'MDD000000001';

-- Second page of ten, oldest first.
EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId = N'MDD000000001', @Skip = 10, @Take = 10
                                      , @SortBy = N'ReceivedDate', @SortDescending = 0;

-- Only the Part A versions, in submission order.
EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId = N'MDD000000001', @SourceType = N'B'
                                      , @SortBy = N'Sequence', @SortDescending = 0;

-- Refused: which handler? An untouched textbox must get a message, not an empty history.
EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId = N'   ';

-- Diagnosing a handler 503's grid says does not exist. If every row comes back with CurrentRecord NULL,
-- dbo.uspReconcileCurrentRecord has not run for it.
EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId = N'MDD000000001', @SortBy = N'Sequence';

Performance: one seek on UX_dbo_HandlerSource_Natural (HandlerId, SourceType, Sequence) WHERE IsDeleted = 0, whose
filter is dbo.vwHandlerSourceHistory's own predicate, followed by at most @Take clustered-index seeks through the view
for the projection. The date and name sorts sort; the input is one handler's versions, so the sort is trivial. No new
index was added -- see the header. F2 measures it against real volumes.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the version-history read. @HandlerId is
											REQUIRED, which is what makes UX_dbo_HandlerSource_Natural sufficient and no
											new index necessary. Reverses 503's TotalRows deviation back to
											COUNT (*) OVER () on the measured ground that the spool is bounded by one
											handler's version count -- see the header, and see 503 for the measurement.
											Projects CurrentRecord, which 503 cannot. @HandlerId is NVARCHAR (20)
											against an NVARCHAR (12) column: the probe caught a silent truncation of a
											padded id at the column's own width, which returned an empty page for a
											handler that exists.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE dbo.uspGetHandlerSourceHistoryPage
    -- @HandlerId is FIRST and has NO DEFAULT. Every other parameter in 50x defaults, and this one deliberately does
    -- not: a caller that omits it gets a compile-time "expects parameter" error rather than a page of Maryland. See
    -- the header -- this is the decision the rest of the procedure rests on.
    --
    -- NVARCHAR (20), NOT the column's NVARCHAR (12). A parameter as wide as the column truncates a padded id BEFORE
    -- the trim below can strip the padding, so a pasted ` MDPROBE05041 ` would arrive as `  MDPROBE054` and return an
    -- empty page for a handler that exists. The extra width exists only so the trim has something to trim; a value
    -- longer than twelve REAL characters still matches nothing, which is correct. See the header.
      @HandlerId       NVARCHAR (20)
    -- Paging and sorting. @SortDescending defaults to 1, unlike every sibling: a version history is read newest first.
    , @Skip            INT           = 0
    , @Take            INT           = 50
    , @SortBy          NVARCHAR (50) = N'ReceivedDate'
    , @SortDescending  BIT           = 1
    -- The one optional filter. A handler's Part A versions and its notification versions are different documents.
    , @SourceType      NVARCHAR (1)  = NULL
    -- No date ranges, no @IncludeDeleted, no @CurrentRecord. See the header: the first would be UI noise over a page
    -- this small, and dbo.vwHandlerSourceHistory has already decided the other two.
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
                                                     , N'[dbo].[uspGetHandlerSourceHistoryPage]')
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

    -- The page, as keys. Ordinal preserves the sort so the projecting SELECT needs one ORDER BY column instead of
    -- repeating all twelve CASE expressions. TotalRows IS a column here, unlike 503: it comes from COUNT (*) OVER ()
    -- in the same pass as ROW_NUMBER, which is the measured reversal the header explains.
    DECLARE @Page TABLE
    (
        Ordinal         INT NOT NULL PRIMARY KEY,
        HandlerSourceId INT NOT NULL,
        TotalRows       INT NOT NULL
    );

    -- COALESCE on every argument, including the integers: CONCAT renders NULL as an empty string, so an omitted @Take
    -- would log as `Take=,` and read as a truncated message rather than as a NULL. These are the values the CALLER
    -- sent, before trimming and clamping, which is what makes the row useful when the call was refused. HandlerId is
    -- logged deliberately -- it is a public regulated-entity identifier, not PII. There is no free-text parameter here.
    SET @KeyParameters = CONCAT (N'HandlerId=', COALESCE (N'''' + @HandlerId + N'''', N'(null)')
                               , N', Skip=', COALESCE (CAST (@Skip AS NVARCHAR (11)), N'(null)')
                               , N', Take=', COALESCE (CAST (@Take AS NVARCHAR (11)), N'(null)')
                               , N', SortBy=', COALESCE (@SortBy, N'(null)')
                               , N', SortDescending=', COALESCE (CAST (@SortDescending AS NVARCHAR (1)), N'(null)')
                               , N', SourceType=', COALESCE (@SourceType, N'(any)'));

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
        -- The required parameter, tested after trimming. `N'   '` is a grid sending an untouched
        -- textbox: it is a legal equality that would find nothing and come back as "this handler has
        -- never changed", which is a statement about the data rather than about the call. See the
        -- header. The trim is assigned back, so a stray space around a pasted id does not make a real
        -- handler unfindable.
        SET @HandlerId = NULLIF (LTRIM (RTRIM (@HandlerId)), N'');

        IF @HandlerId IS NULL
        BEGIN
            SET @Failure = N'@HandlerId is required and was empty or NULL. A version history is asked about one '
                         + N'handler: there is no screen that asks for every version of every handler, and returning '
                         + N'a page of Maryland instead would answer a question nobody asked. An empty page would be '
                         + N'worse still -- it reads as "this handler has never changed" rather than as "you did not '
                         + N'say which handler".';
            ;THROW 50000, @Failure, 1;
        END;

        -- The whitelist. Five columns, and an unrecognised value is refused rather than defaulted --
        -- @SortBy must never be concatenated into dynamic SQL, and it must never silently become
        -- something else either. SiteLocationCity is absent on purpose: see the header.
        IF @SortBy IS NULL
           OR @SortBy NOT IN (N'ReceivedDate', N'SrcUpdatedDate', N'Sequence', N'SourceType', N'HandlerName')
        BEGIN
            SET @Failure = CONCAT (N'@SortBy = ', COALESCE (N'''' + @SortBy + N'''', N'NULL')
                                 , N' is not a sortable column of this history. Use one of ReceivedDate, ')
                         + N'SrcUpdatedDate, Sequence, SourceType, HandlerName. The value is rejected rather than '
                         + N'defaulted so that a mistyped sort column cannot come back as rows sorted by something '
                         + N'else.';
            ;THROW 50000, @Failure, 1;
        END;

        -- There is no inverted-range refusal here, and no closed-set refusal either. This procedure
        -- offers no date range to invert, and dbo.HandlerSource carries no CHECK constraints, so
        -- @SourceType has no permitted-value list to check against. See the header.

        -- GREATEST/LEAST are SQL Server 2022 and in scope. COALESCE first, so NULL means "the default"
        -- and not "the floor": a grid that omits @Take wants 50 rows, not 1.
        SET @Skip = LEAST (GREATEST (COALESCE (@Skip, 0), 0), 2000000000);
        SET @Take = LEAST (GREATEST (COALESCE (@Take, 50), 1), 500);

        SET @ContextMessage = CONCAT (N'HandlerId=', @HandlerId, N', Skip=', @Skip, N', Take=', @Take
                                    , N', SortBy=', @SortBy, N', SortDescending=', @SortDescending);

        -- No BEGIN TRANSACTION, and the omission is deliberate -- see the header. Nothing here writes at
        -- all on the successful path, and READ COMMITTED gives the statements below no shared
        -- consistency that a transaction could preserve.

        -- ------------------------------------------------------------------------------------------
        -- 2. The page and the total, in ONE pass over ONE copy of the predicate. COUNT (*) OVER ()
        --    rather than a separate COUNT (*): the spool it builds is bounded by the versions of one
        --    handler because @HandlerId is required, and one copy of a WHERE clause cannot drift from
        --    itself. 503 splits them and must; see both headers.
        -- ------------------------------------------------------------------------------------------
        -- ROW_NUMBER rather than OFFSET/FETCH: with OFFSET/FETCH the outer query would have to trust
        -- that a derived table hands its rows on in the order it produced them, which is nowhere
        -- guaranteed, and Ordinal would not exist to be projected. Under OPTION (RECOMPILE) the CASE
        -- expressions below fold to constants and eleven of the twelve disappear.
        INSERT INTO @Page (Ordinal, HandlerSourceId, TotalRows)
        SELECT p.Ordinal, p.HandlerSourceId, p.TotalRows
          FROM (SELECT ROW_NUMBER () OVER (
                       ORDER BY CASE WHEN @SortBy = N'ReceivedDate'   AND @SortDescending = 0 THEN v.ReceivedDate       END ASC
                              , CASE WHEN @SortBy = N'ReceivedDate'   AND @SortDescending = 1 THEN v.ReceivedDate       END DESC
                              , CASE WHEN @SortBy = N'SrcUpdatedDate' AND @SortDescending = 0 THEN v.SrcUpdatedDate     END ASC
                              , CASE WHEN @SortBy = N'SrcUpdatedDate' AND @SortDescending = 1 THEN v.SrcUpdatedDate     END DESC
                              , CASE WHEN @SortBy = N'Sequence'       AND @SortDescending = 0 THEN v.Sequence           END ASC
                              , CASE WHEN @SortBy = N'Sequence'       AND @SortDescending = 1 THEN v.Sequence           END DESC
                              , CASE WHEN @SortBy = N'HandlerName'    AND @SortDescending = 0 THEN v.HandlerName        END ASC
                              , CASE WHEN @SortBy = N'HandlerName'    AND @SortDescending = 1 THEN v.HandlerName        END DESC
                              -- SourceType sorts by the lookup's own display order, then by the code, so
                              -- that a code the lookup does not carry -- SourceTypeSortOrder is nullable
                              -- -- still comes out deterministically. See the header.
                              , CASE WHEN @SortBy = N'SourceType'     AND @SortDescending = 0 THEN v.SourceTypeSortOrder END ASC
                              , CASE WHEN @SortBy = N'SourceType'     AND @SortDescending = 1 THEN v.SourceTypeSortOrder END DESC
                              , CASE WHEN @SortBy = N'SourceType'     AND @SortDescending = 0 THEN v.SourceType          END ASC
                              , CASE WHEN @SortBy = N'SourceType'     AND @SortDescending = 1 THEN v.SourceType          END DESC
                              -- The tiebreaker. Not decoration: all five sortable columns are non-unique
                              -- within one handler, and Sequence is unique only per (HandlerId,
                              -- SourceType), so two source types can both have Sequence 1. Ascending in
                              -- both directions on purpose -- it only has to be deterministic.
                              , v.HandlerSourceId ASC) AS Ordinal
                     , v.HandlerSourceId
                     -- The whole point of the single pass. Counted over every version this call matches,
                     -- before the Ordinal window below narrows it to a page.
                     , COUNT (*) OVER () AS TotalRows
                  FROM dbo.vwHandlerSourceHistory AS v
                 WHERE v.HandlerId = @HandlerId
                   AND (@SourceType IS NULL OR v.SourceType = @SourceType)) AS p
         WHERE p.Ordinal >  @Skip
           AND p.Ordinal <= @Skip + @Take
        OPTION (RECOMPILE);

        SET @RowsOnPage = @@ROWCOUNT;

        -- Over at most @Take rows of a table variable. Only for the context message -- the projection
        -- reads TotalRows from @Page directly, so this cannot disagree with what the caller sees.
        SELECT @TotalRows = COALESCE (MAX (p.TotalRows), 0) FROM @Page AS p;

        SET @ContextMessage = CONCAT (@ContextMessage, N', RowsOnPage=', @RowsOnPage
                                    , N', TotalRows=', @TotalRows);

        -- ------------------------------------------------------------------------------------------
        -- 3. The projection, and the last statement in the block. Joined back through the VIEW rather
        --    than the base table, so the AR7 filter is applied on this path too -- at most @Take
        --    clustered-index seeks. Curated: 218 columns are available and this draws twenty-two.
        --    Contact columns are excluded as PII, more firmly here than in 503; see the header.
        -- ------------------------------------------------------------------------------------------
        SELECT p.Ordinal
             , v.HandlerSourceId
             , v.HandlerId
             , v.ActivityLocation
             , v.SourceType
             , v.SourceTypeDescription
             , v.Sequence
             -- The column 503 cannot project, and the reason this screen exists. NULL means
             -- dbo.uspReconcileCurrentRecord has not stamped this handler, which makes it invisible to
             -- 503's grid and visible only here. Two rows showing 1 is the AR4 defect, made visible
             -- rather than corrected -- a read does not decide that data is wrong. See the header.
             , v.CurrentRecord
             , v.ReceivedDate
             , v.HandlerName
             , v.SiteLocationAddress1
             , v.SiteLocationCity
             , v.SiteLocationStateCode
             , v.SiteLocationZip
             , v.SiteLocationCountyDescription
             , v.WasteFederalGeneratorCategoryCode
             , v.WasteFederalGeneratorCategoryDescription
             -- The three activity flags MDE reads a handler by in practice. The other forty-odd Waste*
             -- flags are in the detail procedure.
             , v.WasteTsd
             , v.WasteTransporter
             , v.WasteRecyclerActivity
             , v.SrcUpdatedDate
             , v.auditModifiedDateUtc
             -- From @Page, not a variable: COUNT (*) OVER () computed it in the same pass as Ordinal, so
             -- it is the same number for every row on the page and cannot disagree with the rows it
             -- arrives beside. Unreadable when nothing matched, because there is then no row to read it
             -- from.
             , p.TotalRows
          FROM @Page                       AS p
          JOIN dbo.vwHandlerSourceHistory  AS v ON v.HandlerSourceId = p.HandlerSourceId
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
    , @ObjectName  = N'uspGetHandlerSourceHistoryPage'
    , @Description = N'Returns one page of EVERY version of ONE handler, newest first, to the monitoring web app -- the screen reached by clicking a row in dbo.uspGetHandlerSourcePage''s grid. Reads dbo.vwHandlerSourceHistory, never the base table, so AR7''s soft-delete filter is applied by the view and there is no @IncludeDeleted parameter; unlike dbo.vwHandlerSource the view does not fix CurrentRecord, which is why every version is returned. @HandlerId is REQUIRED and has no default, and an empty or whitespace-only value is refused rather than matched, because an empty history reads as "this handler has never changed" rather than as "you did not say which handler". The parameter is declared NVARCHAR (20) against an NVARCHAR (12) column deliberately: a parameter at the column''s own width truncates a padded id before the procedure can trim it, so a pasted id with a space either side would return an empty page for a handler that exists. That requirement is what makes UX_dbo_HandlerSource_Natural (HandlerId, SourceType, Sequence) WHERE IsDeleted = 0 sufficient -- its filter is exactly the view''s predicate -- so unlike 503 this procedure needed no new index. TotalRows is COUNT (*) OVER () rather than a separate COUNT (*), which reverses 503''s deviation on measured grounds: the spool the window aggregate builds is bounded by one handler''s version count, and a single copy of the WHERE clause cannot drift from a second copy the way a split count can. CurrentRecord IS projected, which 503 cannot do because through its view it is a constant 1; here it identifies the live version, a NULL on every row means dbo.uspReconcileCurrentRecord has not stamped the handler and 503''s grid therefore cannot see it at all, and two rows showing 1 is the AR4 stale-flag defect made visible -- surfaced rather than corrected, because a read does not decide that data is wrong. @SortBy is checked against a whitelist of five columns (ReceivedDate, SrcUpdatedDate, Sequence, SourceType, HandlerName) and an unrecognised value is REJECTED rather than defaulted; the default is ReceivedDate DESCENDING, unlike every sibling, because a version history is read newest first. Sorting by SourceType sorts by the lookup''s SourceTypeSortOrder first, then the code. ORDER BY uses one CASE per column per direction and always ends with HandlerSourceId, so paging over a non-unique key cannot repeat and skip rows -- Sequence is unique only per (HandlerId, SourceType). No date-range filters, because a filter narrowing twelve rows to nine is noise; @SourceType is the one optional filter and is deliberately NOT validated, dbo.HandlerSource carrying no CHECK constraints. @Take is clamped to 1..500 and @Skip floored at 0 inside the procedure, because the procedure is the boundary the permission model enforces. The projection is curated -- 218 columns are available and this draws twenty-two -- and contact name, phone, email and address are excluded as PII along with SrcUpdatedBy, more firmly here than in 503: a history page returns every address and phone number a handler has ever filed, which is a worse disclosure than one current record. dbo.uspGetHandlerSourceDetail carries them. An empty result set means TotalRows is unreadable: the caller cannot read the total from a row that is not there. Instrumented for FAILURES ONLY, with no successful-path row, and it opens no transaction and therefore never rolls one back. EXECUTE is granted to RCRAInfoMonitorRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- The monitor only. The console app has no reason to read a version history: it knows what it just
-- merged, and dbo.uspReconcileCurrentRecord -- which it does execute -- reads the flags it needs
-- directly. Both directions are asserted by build/check_permission_posture.py, so the asymmetry is
-- measured rather than intended.
--
-- Ownership chaining carries the SELECT on dbo.vwHandlerSourceHistory, and through it on
-- dbo.HandlerSource, and the INSERT on logs.ExecutionLog, through this grant -- so the monitor login
-- holds no direct permission on any of the three (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetHandlerSourceHistoryPage TO RCRAInfoMonitorRole;
END;
GO

PRINT N'504: dbo.uspGetHandlerSourceHistoryPage created or altered, EXECUTE granted to the monitor role.';
GO
