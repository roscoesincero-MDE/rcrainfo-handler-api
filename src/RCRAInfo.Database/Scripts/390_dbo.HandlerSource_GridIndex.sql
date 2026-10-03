-- SET XACT_ABORT ON before anything else, so a partial failure cannot leave half-applied DDL behind.
SET XACT_ABORT ON;
-- Not decoration: sqlcmd defaults QUOTED_IDENTIFIER OFF where every other client defaults it ON, the
-- setting is BAKED IN at CREATE time, and a module or session carrying it OFF cannot run DML against a
-- table with a filtered index (error 1934). Every unique constraint here is one. Set it so that a hand
-- run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.IX_dbo_HandlerSource_Grid
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

A filtered covering index for the handler grid: keyed on HandlerId, carrying the columns
dbo.uspGetHandlerSourcePage sorts and filters by, and filtered to exactly the rows dbo.vwHandlerSource exposes
(IsDeleted = 0 AND CurrentRecord = 1).

This is an ADDITIVE change to dbo.HandlerSource and lives in its own script for two reasons. Editing the original
CREATE TABLE would stop 100_dbo.HandlerSource.sql converging on a database that already has the table; and that script
is GENERATED from spec/rcrainfo/swagger.json, so a hand-written line in it would be overwritten by the next
`python build/generate_schema.py`. The number is 390 rather than 101 for the same reason -- build/generate_schema.py
claims the whole 1xx and 2xx ranges and reports anything else in them as stale. 3xx is the hand-authored DDL range, and
it still deploys after every table and view and before every procedure, which is the order an index needs.

========================================================================================================================
Requirements and Key Dependencies:

dbo.HandlerSource, created by 100_dbo.HandlerSource.sql.

Consumed by dbo.uspGetHandlerSourcePage (script 503). Nothing depends on this index for CORRECTNESS -- every query
served by it returns the same rows without it, only slower -- so 503 deploys and runs whether or not this script has
been applied.

========================================================================================================================
Notes:

THIS INDEX EXISTS BECAUSE THE COST WAS MEASURED, NOT ASSUMED. 500_logs.uspGetLoadRunPage.sql and
501_logs.uspGetHandlerLoadStatusPage.sql both defer a measurement to dbo.uspGetHandlerSourcePage: a page request with
no filters sorts the whole table, which is acceptable on logs.LoadRun and was not to be assumed acceptable here.
build/tmp/measure503_sort.probesql and build/tmp/io503alt2.probesql are that measurement.

WHAT WAS MEASURED. dbo.HandlerSource holds 50 rows on the development workstation, all soft-deleted, so
dbo.vwHandlerSource returns none; G22 -- the real initial-load volume -- is credential-gated and is "measured in F2".
The measurement therefore ran against a temp table built with SELECT TOP (0) * INTO ... FROM dbo.HandlerSource, so all
218 columns kept their real types and widths, populated at STATED ASSUMED volumes of 25,000 / 100,000 / 400,000 rows
with one row in four current -- because dbo.HandlerSource stores one row per handler per VERSION, so the table is
several times the size of the set the grid pages over. The largest case assumes 20 versions for each of 20,000 Maryland
handlers and is deliberately pessimistic.

THE RESULT, at 400,000 rows and 100,000 of them current, logical reads for the paging query and its row count:

    COUNT (*) OVER (), no covering index      66,776 + 202,152 worktable        ~240 ms
    separate COUNT, no covering index         66,776 + 66,776                   ~215 ms
    separate COUNT, THIS INDEX, sort HandlerId     1,642 + 4                      ~15 ms
    separate COUNT, THIS INDEX, sort HandlerName   1,642 + 1,642                  ~30 ms
    separate COUNT, THIS INDEX, Skip = 10,000      1,642 + 167                    ~15 ms

Two things came out of that which were not expected, and both are recorded in 503's header because they changed how it
is written. The 202,152-read worktable is the window aggregate's spool, and it was the largest single cost in the
query -- larger than scanning the 218-column clustered index. And a CASE-based ORDER BY does NOT always sort: under
OPTION (RECOMPILE) the @SortBy comparisons fold to constants at compile time, so when the folded ORDER BY matches this
index the ordering comes from the index and the page stops early -- four logical reads, no sort, and a cost that scales
with @Skip rather than with the table.

WHY IT IS KEYED ON HandlerId ALONE. That is dbo.uspGetHandlerSourcePage's default sort, so it is the ordering that
benefits from being free. The other five sortable columns are INCLUDE columns: they still avoid the clustered index
scan, and they still sort -- 1,642 reads and an in-memory sort rather than 66,776. An index per sortable column would
be six indexes on the loader's hot path to save 15 ms on five of them.

WHY Sequence AND SourceTypeDescription ARE NOT HERE, even though the grid returns both. The INCLUDE list covers what
the PAGING query touches -- the sort keys and the filter columns -- and nothing else. The wide column list is a
separate join on HandlerSourceId over at most @Take rows, which is what the key-then-project split in 503 is for.
Adding projection columns here would widen the index for every write to save 500 clustered-index seeks.

THE FILTER PREDICATE COSTS THE LOADER SOMETHING, AND IT IS NAMED HERE SO IT IS NOT A SURPRISE. WHERE IsDeleted = 0 AND
CurrentRecord = 1 means a row ENTERS this index when dbo.uspReconcileCurrentRecord sets CurrentRecord = 1 and LEAVES it
when the same procedure clears it on the previous version -- so every reconcile pass writes here twice per handler it
advances, and every soft delete removes a row. That is the price of the index being one twentieth the size of the
alternative, and it is paid on a scheduled batch rather than on a user's click. If F2 measures the reconcile pass as too
slow because of it, the filter is the first thing to reconsider -- dropping CurrentRecord from the predicate keeps most
of the benefit at four times the size.

WHY THE PREDICATE IS NOT A SUPERSET OF THE VIEW'S. It has to match dbo.vwHandlerSource exactly, or the optimizer cannot
use the index for a query the view generates. dbo.vwHandlerSource is dbo.vwHandlerSourceHistory (IsDeleted = 0) with
CurrentRecord = 1 added, which is why both predicates appear here and why a change to either view has to be reflected
in this script.

CurrentRecord IS NULLABLE, AND `= 1` EXCLUDES NULL. A row the loader never stamped is in neither this index nor the
view. That is a pre-existing property of dbo.vwHandlerSource rather than something this index introduces, and it is
noted in 503's header as an operational consequence.

========================================================================================================================
Example Usage and Performance:

-- What the index is for. Compare with and without:
SET STATISTICS IO ON;
EXEC dbo.uspGetHandlerSourcePage @Take = 50;
SET STATISTICS IO OFF;

-- Confirm it is present and filtered as intended:
SELECT i.name, i.filter_definition
  FROM sys.indexes AS i
 WHERE i.object_id = OBJECT_ID (N'dbo.HandlerSource')
   AND i.name      = N'IX_dbo_HandlerSource_Grid';

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4. Added as the outcome of the sort-cost
											measurement that 500 and 501 both deferred to dbo.uspGetHandlerSourcePage.
***********************************************************************************************************************/

-- Guarded, because the developer runs these scripts by hand and a second run must change nothing and report nothing.
-- Not DROP-and-CREATE: that is the usual way to make an index script re-runnable and it is forbidden here.
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE object_id = OBJECT_ID (N'dbo.HandlerSource')
                  AND name      = N'IX_dbo_HandlerSource_Grid')
BEGIN
    CREATE NONCLUSTERED INDEX IX_dbo_HandlerSource_Grid
        ON dbo.HandlerSource (HandlerId)
        -- The sort keys dbo.uspGetHandlerSourcePage offers, and the columns it filters on. Nothing from the
        -- projection: that is a separate join over at most @Take rows.
        INCLUDE (ActivityLocation
               , SourceType
               , SourceTypeSortOrder
               , HandlerName
               , ReceivedDate
               , SrcUpdatedDate
               , SiteLocationCity
               , WasteFederalGeneratorCategoryCode)
        -- Exactly dbo.vwHandlerSource's own predicate. See the header: a superset would not be usable.
        WHERE IsDeleted = 0 AND CurrentRecord = 1;

    PRINT N'390: dbo.IX_dbo_HandlerSource_Grid created.';
END;
ELSE
BEGIN
    -- Deliberately silent on the re-run. A script that reports "already exists" on every pass trains the developer
    -- to skim its output, which is where a real message goes unread.
    SET NOCOUNT ON;
END;
GO
