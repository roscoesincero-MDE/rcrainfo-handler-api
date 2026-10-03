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
ObjectName:   dbo.uspGetHandlerSourceDetail
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Returns ONE handler version in full -- 215 of dbo.vwHandlerSourceHistory's 218 columns -- to the monitoring web app. It
is the screen an operator reaches by clicking a row in dbo.uspGetHandlerSourcePage's grid (503) or in
dbo.uspGetHandlerSourceHistoryPage's version list (504), both of which project HandlerSourceId for exactly this purpose.

It is the last of the four 50x reads, and it is the only one where the PII decision inverts: the contact name, phone,
email and mailing address that 503 and 504 withhold by name are RETURNED here. Everything unusual about this procedure
follows from that, so the conditions under which it is safe are stated before the code rather than after it.

------------------------------------------------------------------------------------------------------------------------
@HandlerSourceId IS REQUIRED, AND IT IS A SURROGATE KEY RATHER THAN ANYTHING SEARCHABLE

There is no default, for the same reason as in 504: a detail screen with no identifier is not a request for a default
row, it is a call that forgot to say which row. But the choice of WHICH identifier is the load-bearing one here.

HandlerSourceId is an IDENTITY surrogate. It cannot be guessed from anything an outsider knows, it carries no meaning, it
is not a name, and it is not a substring of anything -- so this procedure offers NO way to ask "which handlers have a
contact called X". That is not an oversight to be fixed by a later parameter. A detail read that returns contact details
must be reachable ONLY by a caller that already holds a specific row identifier obtained from a screen it was allowed to
see, because that is what limits it to one record per click instead of a harvest. dbo.uspSearchHandlerSource (506) is the
procedure that takes free text, and it returns the narrow grid projection, not this one. The two must not be merged.

It is also why @KeyParameters is safe here while the RESULT SET is not: the parameter this procedure logs is an integer.
The payload contains PII; the log row contains a row number. Those are different disclosures and only one of them is
written to logs.ExecutionLog, which the monitoring web app can read.

@HandlerSourceId IS AN INT, SO 504's TRUNCATION DEFECT CANNOT RECUR HERE. 504's @HandlerId had to be declared WIDER than
its NVARCHAR (12) column because a padded string arrives already truncated at the column's own width. An INT has no
width to overflow silently: a value outside its range is a conversion ERROR at the call, which the caller sees. The
standing rule -- a parameter that is trimmed, normalised or pattern-checked must be declared wider than its target
column -- has nothing to bind to when the parameter is neither trimmed nor a string.

------------------------------------------------------------------------------------------------------------------------
THIS IS THE ONE READ THAT RETURNS CONTACT DETAILS, AND THE FOUR CONDITIONS ARE NOT DECORATION

503's and 504's headers both say contact columns are excluded as PII and "dbo.uspGetHandlerSourceDetail carries them".
This is that procedure, so the promise those headers made has to be honoured by something. It is honoured by four
properties, and losing any one of them is a reason to revisit the projection rather than a detail:

  1. ONE ROW PER CALL, BY CONSTRUCTION. The filter is an equality on the clustered primary key. There is no @Take to
     raise, no page to widen, no filter that can match a second row. 504's header makes the volume argument that
     applies: twelve rows of a history are twelve versions of the same person's contact details, every address and phone
     number that handler has ever filed. Here it is one version, and the caller had to name it.
  2. THE IDENTIFIER MUST COME FROM A SCREEN THE CALLER WAS ALREADY ALLOWED TO SEE. See above -- a surrogate key is not
     derivable from a name, so this cannot be the FIRST call in a session.
  3. THE GRANT IS MONITOR-ONLY. The console app does not execute it. It has no reason to read a handler back at all,
     never mind its contacts: it wrote the row, and dbo.uspReconcileCurrentRecord -- which it does execute -- reads the
     flags it needs directly. build/check_permission_posture.py asserts BOTH directions, so the asymmetry is measured
     rather than intended, and a future grant to the loader would turn a check red.
  4. THE LOG CARRIES AN INTEGER. Nothing in @KeyParameters or @ContextMessage names a person.

WHAT IS *NOT* A CONDITION: column-level encryption, masking, or a second "redacted" variant of this procedure. Those
were considered and rejected as scope MDE has not asked for; the honest statement is that this procedure returns the
regulated-entity contact details EPA already publishes to the states, to a role only MDE staff hold, one record at a
time. If MDE later decides the monitor should not see contacts at all, the change is to this projection and to nothing
else -- which is itself an argument for the projection being explicit. See below.

SrcUpdatedBy IS RETURNED HERE, AND IT IS EXCLUDED FROM 503 AND 504. It identifies an EPA user rather than a Maryland
one, which is why the grid and the history withhold it, but on a single-record detail screen "who at EPA last touched
this" is the question an operator is actually asking when a value looks wrong. Same reasoning, opposite answer, because
the volume is different -- which is the whole shape of this procedure.

------------------------------------------------------------------------------------------------------------------------
IT READS THE HISTORY VIEW, AND THAT IS WHAT MAKES 504's LIST CLICKABLE

dbo.vwHandlerSourceHistory, not dbo.vwHandlerSource. Both filter IsDeleted = 0 and mask the deletion stamps; the
difference is that dbo.vwHandlerSource additionally fixes CurrentRecord = 1.

If this procedure read dbo.vwHandlerSource, then every row in 504's version list EXCEPT the current one would come back
empty when clicked -- 504 exists precisely to show superseded versions, so the detail screen behind it must be able to
fetch one. The two procedures would each be correct alone and useless together. Reading the history view costs nothing:
HandlerSourceId is unique across all versions, so an equality on it returns one row whether that row is current or not.

CurrentRecord IS THEREFORE PROJECTED, and it means the same thing it means in 504: 1 is the live version, 0 is
superseded, and NULL means dbo.uspReconcileCurrentRecord has not stamped this handler at all. A detail screen for a
version with CurrentRecord NULL is reachable from 504 and from nowhere else, because 503's grid cannot see that handler.

------------------------------------------------------------------------------------------------------------------------
215 OF 218 COLUMNS, AND THE THREE THAT ARE OUT ARE PROVABLE CONSTANTS RATHER THAN CHOICES

The exclusions are properties of the VIEW, not preferences, which is what makes the number defensible:

  * IsDeleted           -- the view's own WHERE clause is IsDeleted = 0, so through this view it is a constant 0.
  * auditDeletedBy      -- the view masks it to NULL with CASE WHEN hs.IsDeleted = 1, and IsDeleted is 0 here.
  * auditDeletedDateUtc -- the same mask, the same reason.

All three are NOT NULL with defaults on dbo.HandlerSource, so they are populated on every INSERT and would otherwise
show a deletion stamp for a row that was never deleted -- which is why the view masks them, and returning the mask is
returning a column that is NULL on every row this procedure can reach. Noise in a 22-column grid is a nuisance; noise in
a 215-column payload is where a real column goes to hide.

Nothing else is excluded. In particular the ~57 contact, mailing-address, phone, email and SrcUpdatedBy columns ARE
included -- see above -- and so are the forty-odd Waste* activity flags that 503 and 504 reduce to three.

------------------------------------------------------------------------------------------------------------------------
THE COLUMN NAMES ARE WRITTEN OUT, AND SELECT * WOULD BE THE ONE PROJECTION NOBODY COULD REVIEW

215 explicit names is the longest statement in this database and it is deliberate. `SELECT *` would be shorter, would
never need maintaining, and would be wrong for three separate reasons, each sufficient:

  1. IT COULD NOT BE REVIEWED FOR PII. The paragraph above claims this projection returns contacts and withholds
     nothing else. Against `SELECT *` that claim is unverifiable by reading the procedure -- you would have to read the
     view, and then the table behind it, and then trust that neither changed.
  2. IT WOULD SILENTLY START RETURNING WHATEVER THE GENERATOR ADDS. build/generate_schema.py owns dbo.HandlerSource and
     dbo.vwHandlerSourceHistory. When the RCRAInfo spec grows a field, the generator adds a column and `SELECT *` would
     ship it to a web page in the same deployment, with no review and no decision -- including a field that should have
     been withheld.
  3. ORDINAL POSITION WOULD BE THE GENERATOR'S TO CHANGE. Any client binding by position -- and DA5's mapping does --
     would break or, worse, silently mis-map two columns of the same type.

SO THE LIST IS EXPLICIT, WHICH MOVES THE RISK RATHER THAN REMOVING IT: an explicit list goes STALE. When the generator
adds a column, this procedure quietly stops returning it and the detail screen shows a field that does not exist.
build/check_detail_projection.py exists for exactly that failure: on every guardrail run it reads this file's projection,
reads sys.columns on dbo.vwHandlerSourceHistory, subtracts the three documented exclusions, and FAILS when the two
disagree in either direction -- a column the generator added and this procedure misses, a column this procedure names
that the view no longer has, or a name out of column_id order. The failure message names the columns. The intended
response is to add the column here after deciding whether it should be returned, not to add it to the exclusion list.

The list was generated once from sys.columns in column_id order rather than typed, because 215 hand-typed names is a
transcription error waiting to be found by a user. From now on it is hand-maintained like any other 5xx script, and the
drift check is what makes that safe.

------------------------------------------------------------------------------------------------------------------------
A ROW THAT IS NOT THERE IS AN EMPTY RESULT SET. A ROW THAT COULD NEVER BE THERE IS A REFUSAL.

Two outcomes that look similar and are not:

  * @HandlerSourceId names a row that does not exist, or one that has been soft-deleted -> EMPTY RESULT SET, no error.
    This is a RACE, and a legitimate one: the loader soft-deletes superseded versions, so a row can vanish between 504
    rendering its list and an operator clicking it. Throwing would turn an ordinary timing window into an error the
    monitoring app has to explain, and the honest answer to "show me this row" when the row is gone is nothing.
    A soft-deleted row and a row that never existed are INDISTINGUISHABLE here, deliberately: both mean "not available",
    and telling a caller which one it was is telling it about a row it is not allowed to see.
  * @HandlerSourceId is NULL, or is zero or negative -> REFUSED with a message. Neither is a race. HandlerSource's
    IDENTITY starts at 1, so a non-positive id could never have matched anything at any point in the database's life --
    it is a caller bug, usually an unbound control sending 0. Returning an empty set for it would spend the one signal
    this procedure has: if "empty" also means "you sent nonsense", then "empty" no longer means "the row is gone", and
    the race above becomes indistinguishable from a defect in the web app.

------------------------------------------------------------------------------------------------------------------------
NOT PAGED, NOT SORTED, AND NO OPTION (RECOMPILE)

No @Skip, @Take, @SortBy, @SortDescending, Ordinal or TotalRows -- one row has nothing to page and nothing to sort, and
there is no ORDER BY because a single-row result has no order to specify. This is the only 50x read with no whitelist,
because it has no @SortBy to whitelist; the rule that @SortBy must never be concatenated into dynamic SQL is satisfied
here by there being no dynamic SQL and no @SortBy.

NO OPTION (RECOMPILE) EITHER, WHICH IS THE OPPOSITE OF 501, 503 AND 504. All three carry it because their plans depend
on parameter VALUES: the CASE-based ORDER BY folds to one column per sort choice, and optional filters fold out. Here
there is one plan for every input -- a single-row seek on PK_dbo_HandlerSource -- so a recompile per call would buy a
plan identical to the cached one and pay a compile for it. The construct is not a house style to be applied uniformly;
it is a response to plan-shape variance, and this procedure has none.

------------------------------------------------------------------------------------------------------------------------
THE CHILD COLLECTIONS ARE NOT HERE, AND THIS RETURNS ONE RESULT SET

A handler version has 19 mirror descendants -- the child collections dbo.uspMergeHandlerSourceBatch still has to learn
to write. None of them are read here. This procedure returns exactly ONE result set, because DA5's thin DbContext maps
one procedure to one shape and a multi-result-set procedure would need hand-written reader code that no other 50x
procedure needs. The child collections get their own reads when the generator emits them, keyed by the same
HandlerSourceId this procedure takes -- so the detail screen makes several calls, which is also what lets it show the
top of the record before the collections arrive.

------------------------------------------------------------------------------------------------------------------------
ERROR-ONLY INSTRUMENTATION

Per MDE's DA1 narrowing: every procedure that WRITES is instrumented; a read is instrumented only where the plan names
it. This is a read, so there is NO logs.uspStartExecutionLogging call and no successful-path row.

THAT MAKES THE TRY/CATCH THE ONLY THING RECORDING ANYTHING, WHICH IS THE POINT. MDE's requirement is that ANY error is
recorded, and it comes from a real defect in another MDE application: a procedure whose body was a single SELECT called
a scalar UDF, the UDF errored, the procedure wrote and updated nothing, and the error was recorded NOWHERE. That failure
mode is at its most likely HERE of anywhere in 50x -- this body is one SELECT of 215 columns and nothing else, so
without the CATCH there would be no code path in the procedure capable of recording anything at all. The CATCH inserts
an ORPHAN row through logs.uspRecordExecutionError with @ExecutionLogId = NULL, recognisable by Successful = 0 with
EndDateUtc set and ElapsedMilliseconds NULL. The duration is recovered into ContextMessage as `ElapsedMs=` precisely
because that NULL is the orphan's signature and must not be filled in.

NO TRANSACTION, AND THEREFORE NO ROLLBACK. Nothing here writes on the successful path. The CATCH contains no ROLLBACK --
A PROCEDURE ROLLS BACK ONLY WHAT IT OPENED. `XACT_STATE () <> 0` is equally true when the CALLER owns the transaction,
so the guard that reads as a safety net would discard a writer's uncommitted work because a web page asked for a row.
ROLLBACK is also illegal inside INSERT ... EXEC (error 8004), where it would replace the error being reported and abort
this CATCH before the failure was recorded. Gated by build/check_stored_headers.py against the DEPLOYED module.

G37 APPLIES HERE UNCHANGED AND IS STILL OPEN. If a caller wraps this call in its own transaction and that transaction is
doomed, error 3930 makes it impossible to write the log row BY ANY MEANS until the transaction is rolled back -- which
this procedure must not do, because it does not own it. logs.uspRecordExecutionError names 3930 in its severity-10
warning so the one action that fixes it is guessable from the message, and build/tmp/da4_gethandlersourcedetail.probesql
pins the boundary with an assertion written to report CHANGED rather than PASS if it ever stops being true.

ERROR 201 IS THE ONE ERROR THIS PROCEDURE CANNOT LOG, exactly as in 504. Omitting a parameter that has no default fails
with error 201 raised by the SERVER BEFORE the body executes, so the TRY/CATCH never runs and nothing reaches
logs.ExecutionLog. It is catchable by an outer TRY/CATCH and unrecordable in here. That is the price of giving
@HandlerSourceId no default, and it is the right trade: the alternative buys a log row by accepting NULL as "the
default row", and there is no default row.

WHAT @KeyParameters MAY CONTAIN: identifiers and counts only, per MDE's own template comment -- "do NOT include
parameters such as passwords and Personally Identifiable Information (PII)". @HandlerSourceId is an integer surrogate
key and IS logged. THE PROCEDURE'S RESULT SET IS FULL OF PII AND NONE OF IT IS LOGGED: nothing read from the view is
ever copied into @KeyParameters or @ContextMessage, not even to make an error message more helpful. A CATCH that said
which handler's contact record failed to load would be writing a name into a table the web app can read. THE STANDING
RULE FOR WHAT REMAINS IN 50x: when dbo.uspSearchHandlerSource adds a free-text search parameter, THE TERM MUST NOT BE
LOGGED, because a search term is whatever a user typed and this database holds regulated-entity records.

@ProcName FALLS BACK TO A LITERAL. QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID)) returns NULL for a caller without metadata
visibility, and the monitor login has none -- so the COALESCE literal is not a defensive nicety, it is what the monitor
actually logs. Keep it in step with the CREATE name above.

========================================================================================================================
Example Usage:

-- The call the monitoring app makes when an operator clicks a row in 503's grid or 504's version list. Both project
-- HandlerSourceId for this purpose.
EXEC dbo.uspGetHandlerSourceDetail @HandlerSourceId = 1;

-- A SUPERSEDED version, reached from 504. Returns the row: this reads the history view, not dbo.vwHandlerSource.
EXEC dbo.uspGetHandlerSourceDetail @HandlerSourceId = 42;

-- Empty result set, no error. The row was soft-deleted between the list rendering and the click, or never existed --
-- the two are deliberately indistinguishable.
EXEC dbo.uspGetHandlerSourceDetail @HandlerSourceId = 999999999;

-- Refused: an unbound control sending 0. IDENTITY starts at 1, so this could never have matched anything, and an empty
-- result set is reserved for a row that is genuinely gone.
EXEC dbo.uspGetHandlerSourceDetail @HandlerSourceId = 0;

-- Error 201 from the server, before the body runs, and therefore NOT logged. See the header.
EXEC dbo.uspGetHandlerSourceDetail;

Performance: one seek on PK_dbo_HandlerSource (HandlerSourceId), CLUSTERED, returning one wide row. It is the cheapest
read in 50x by logical reads -- the clustered index depth, three or four pages at any realistic volume -- and the most
expensive by BYTES per row, because 215 columns of one row is most of a data page and the row's variable-length columns
may spill. No index was added and none is possible to add usefully: the filter is the clustered key itself. F2 measures
it against real volumes.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4, the wide single-record read and the last
											of the four 50x reads. Projects 215 of dbo.vwHandlerSourceHistory's 218
											columns INCLUDING the ~57 contact, mailing-address and SrcUpdatedBy columns
											503 and 504 withhold as PII -- the four conditions that make that safe are
											in the header, and one of them is that @HandlerSourceId is a surrogate key
											rather than anything searchable. Reads the HISTORY view so a superseded
											version clicked in 504 is fetchable. Not found returns an empty result set,
											a non-positive id is refused, and the distinction is load-bearing. The
											explicit 215-name list is kept in step with the catalog by
											build/check_detail_projection.py, which is new in this change.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE dbo.uspGetHandlerSourceDetail
    -- The only parameter, and it has NO DEFAULT. A detail screen with no identifier is not asking for a default row.
    --
    -- An INT surrogate key rather than a handler id, a name, or anything else a person could type: that is what
    -- restricts this procedure -- the one read that returns contact details -- to a caller already holding a row
    -- identifier from a screen it was allowed to see. See the header; this is not a convenience.
    --
    -- No width to truncate, unlike 504's @HandlerId. An out-of-range value is a conversion error at the call.
      @HandlerSourceId INT
    -- No @Skip, @Take, @SortBy, @SortDescending, @IncludeDeleted or @CurrentRecord. One row has nothing to page or
    -- sort, and dbo.vwHandlerSourceHistory has already decided the other two. See the header.
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
                                                     , N'[dbo].[uspGetHandlerSourceDetail]')
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
    DECLARE @RowsReturned INT             = 0
          , @Failure      NVARCHAR (2048) = NULL;

    -- One integer. COALESCE because CONCAT renders NULL as an empty string, which would log as
    -- `HandlerSourceId=` and read as a truncated message rather than as the NULL that got the call refused.
    -- NOTHING READ FROM THE VIEW IS EVER ADDED TO THIS -- the result set is full of PII and the log row is an
    -- integer, which is the distinction the header rests on.
    SET @KeyParameters = CONCAT (N'HandlerSourceId='
                               , COALESCE (CAST (@HandlerSourceId AS NVARCHAR (11)), N'(null)'));

    BEGIN TRY

        -- No logs.uspStartExecutionLogging call. See the header: this read records failures only, and the
        -- CATCH below inserts an orphan row with @ExecutionLogId = NULL rather than updating one.

        -- ==========================================================================================
        -- ===== The procedure's own work starts here. Everything above and below is boilerplate. ====
        -- ==========================================================================================

        -- ------------------------------------------------------------------------------------------
        -- 1. Validation. First, and before any work -- a refusal should cost nothing but the parse.
        --    There is nothing to clamp here and nothing to whitelist: one parameter, no paging, no
        --    sort column, no dynamic SQL.
        -- ------------------------------------------------------------------------------------------
        -- NULL and non-positive are refused TOGETHER because they are the same mistake reaching the
        -- server two ways -- an unbound control sends NULL from one framework and 0 from another.
        -- Neither is a race: dbo.HandlerSource's IDENTITY starts at 1, so no value at or below zero
        -- has ever identified a row. A row that is merely GONE returns an empty result set instead,
        -- and that difference is the whole reason this refusal exists -- see the header.
        IF @HandlerSourceId IS NULL OR @HandlerSourceId <= 0
        BEGIN
            SET @Failure = CONCAT (N'@HandlerSourceId = '
                                 , COALESCE (CAST (@HandlerSourceId AS NVARCHAR (11)), N'NULL')
                                 , N' is not a possible row identifier. dbo.HandlerSource.HandlerSourceId is an '
                                 , N'IDENTITY starting at 1, so a NULL or non-positive value has never identified a '
                                 , N'row and never will -- it is an unset control rather than a missing record. A '
                                 , N'record that genuinely does not exist, or that has been soft-deleted since the '
                                 , N'grid was rendered, comes back as an EMPTY RESULT SET and no error; that is a '
                                 , N'legitimate race and it must stay distinguishable from this.');
            ;THROW 50000, @Failure, 1;
        END;

        -- No BEGIN TRANSACTION, and the omission is deliberate -- see the header. Nothing here writes at
        -- all on the successful path, and one statement reading one row by its clustered key has no
        -- multi-statement consistency for a transaction to preserve.

        -- ------------------------------------------------------------------------------------------
        -- 2. The row. One statement, one seek on PK_dbo_HandlerSource, and the last statement in the
        --    block. Read through the VIEW rather than the base table, which is what applies AR7's
        --    IsDeleted = 0 filter on this path without this procedure naming IsDeleted at all -- so a
        --    soft-deleted version returns nothing here by the same mechanism that hides it from 503
        --    and 504.
        --
        --    THE HISTORY VIEW, NOT dbo.vwHandlerSource: the latter fixes CurrentRecord = 1, which would
        --    make every superseded version in 504's list come back empty when clicked. See the header.
        --
        --    215 columns, written out. NOT `SELECT *` -- three reasons in the header, the first being
        --    that a projection returning contact details has to be reviewable by reading the procedure.
        --    IsDeleted, auditDeletedBy and auditDeletedDateUtc are the only omissions and all three are
        --    provable constants through this view. THE LIST IS CHECKED AGAINST sys.columns ON EVERY
        --    GUARDRAIL RUN by build/check_detail_projection.py, which fails when the generator adds a
        --    column this statement does not return; that check is what keeps an explicit list from
        --    going stale, and it parses these lines, so keep them one column per line with no
        --    interleaved comments -- the commentary belongs in this block.
        --
        --    No OPTION (RECOMPILE), unlike 501, 503 and 504: one plan serves every input here. No
        --    ORDER BY, because one row has no order.
        -- ------------------------------------------------------------------------------------------
        SELECT v.HandlerSourceId
             , v.HandlerId
             , v.ActivityLocation
             , v.SourceType
             , v.Sequence
             , v.SourceTypeDescription
             , v.SourceTypeSortOrder
             , v.CurrentRecord
             , v.ExtractFlag
             , v.ReceivedDate
             , v.HandlerName
             , v.NonNotifierCode
             , v.NonNotifierDescription
             , v.Acknowledgement
             , v.AccessibilityCode
             , v.AccessibilityDescription
             , v.SiteLocationStreetNumber
             , v.SiteLocationAddress1
             , v.SiteLocationAddress2
             , v.SiteLocationCity
             , v.SiteLocationStateActivityLocation
             , v.SiteLocationStateCode
             , v.SiteLocationStateDescription
             , v.SiteLocationStateActive
             , v.SiteLocationForeignStateActivityLocation
             , v.SiteLocationForeignStateCode
             , v.SiteLocationForeignStateDescription
             , v.SiteLocationForeignStateActive
             , v.SiteLocationForeignStateName
             , v.SiteLocationForeignStateCountryCode
             , v.SiteLocationCountryActivityLocation
             , v.SiteLocationCountryCode
             , v.SiteLocationCountryDescription
             , v.SiteLocationCountryActive
             , v.SiteLocationZip
             , v.SiteLocationLatitude
             , v.SiteLocationLongitude
             , v.SiteLocationLatLongPrimary
             , v.SiteLocationGisOriginActivityLocation
             , v.SiteLocationGisOriginCode
             , v.SiteLocationGisOriginDescription
             , v.SiteLocationGisOriginActive
             , v.SiteLocationCountyActivityLocation
             , v.SiteLocationCountyCode
             , v.SiteLocationCountyDescription
             , v.SiteLocationCountyActive
             , v.SiteLocationStateDistrictActivityLocation
             , v.SiteLocationStateDistrictCode
             , v.SiteLocationStateDistrictDescription
             , v.SiteLocationStateDistrictActive
             , v.SiteLocationStandardized
             , v.LandTypeCode
             , v.LandTypeDescription
             , v.SiteMailingAddressStreetNumber
             , v.SiteMailingAddressAddress1
             , v.SiteMailingAddressAddress2
             , v.SiteMailingAddressCity
             , v.SiteMailingAddressStateActivityLocation
             , v.SiteMailingAddressStateCode
             , v.SiteMailingAddressStateDescription
             , v.SiteMailingAddressStateActive
             , v.SiteMailingAddressForeignStateActivityLocation
             , v.SiteMailingAddressForeignStateCode
             , v.SiteMailingAddressForeignStateDescription
             , v.SiteMailingAddressForeignStateActive
             , v.SiteMailingAddressForeignStateName
             , v.SiteMailingAddressForeignStateCountryCode
             , v.SiteMailingAddressCountryActivityLocation
             , v.SiteMailingAddressCountryCode
             , v.SiteMailingAddressCountryDescription
             , v.SiteMailingAddressCountryActive
             , v.SiteMailingAddressZip
             , v.NaicsPrimaryActivityLocation
             , v.NaicsPrimaryCode
             , v.NaicsPrimaryDescription
             , v.NaicsPrimaryActive
             , v.ContactFirstName
             , v.ContactMiddleInitial
             , v.ContactLastName
             , v.ContactTitle
             , v.ContactPhone
             , v.ContactPhoneExtension
             , v.ContactFax
             , v.ContactEmail
             , v.ContactLanguageActivityLocation
             , v.ContactLanguageCode
             , v.ContactLanguageDescription
             , v.ContactLanguageActive
             , v.ContactAddressStreetNumber
             , v.ContactAddressAddress1
             , v.ContactAddressAddress2
             , v.ContactAddressCity
             , v.ContactAddressStateActivityLocation
             , v.ContactAddressStateCode
             , v.ContactAddressStateDescription
             , v.ContactAddressStateActive
             , v.ContactAddressForeignStateActivityLocation
             , v.ContactAddressForeignStateCode
             , v.ContactAddressForeignStateDescription
             , v.ContactAddressForeignStateActive
             , v.ContactAddressForeignStateName
             , v.ContactAddressForeignStateCountryCode
             , v.ContactAddressCountryActivityLocation
             , v.ContactAddressCountryCode
             , v.ContactAddressCountryDescription
             , v.ContactAddressCountryActive
             , v.ContactAddressZip
             , v.WasteFederalGeneratorCategoryActivityLocation
             , v.WasteFederalGeneratorCategoryCode
             , v.WasteFederalGeneratorCategoryDescription
             , v.WasteFederalGeneratorCategoryActive
             , v.WasteStateGeneratorCategoryActivityLocation
             , v.WasteStateGeneratorCategoryCode
             , v.WasteStateGeneratorCategoryDescription
             , v.WasteStateGeneratorCategoryActive
             , v.WasteFurnaceExemption
             , v.WasteMixedWasteGenerator
             , v.WasteOnsiteBurnerExemption
             , v.WasteReceivesOffSite
             , v.WasteRecognizedTraderExporter
             , v.WasteRecognizedTraderImporter
             , v.WasteRecyclerActivity
             , v.WasteRecyclerActivityNonStorage
             , v.WasteShortTermGenerator
             , v.WasteShortTermGeneratorNotes
             , v.WasteSlabExporter
             , v.WasteSlabImporter
             , v.WasteSubPartKCollege
             , v.WasteSubPartKHospital
             , v.WasteSubPartKNonprofit
             , v.WasteSubPartKWithdrawal
             , v.WasteSubPartPHealthCare
             , v.WasteSubPartPReverseDistributor
             , v.WasteSubPartPWithdrawal
             , v.WasteTransferFacility
             , v.WasteTransporter
             , v.WasteTsd
             , v.WasteUndergroundInjectionControl
             , v.WasteUniversalWasteDestinationFacility
             , v.WasteUsImporter
             , v.WasteUsedOilBurner
             , v.WasteUsedOilMarketBurner
             , v.WasteUsedOilProcessor
             , v.WasteUsedOilRefiner
             , v.WasteUsedOilSpecMarketer
             , v.WasteUsedOilTransferFacility
             , v.WasteUsedOilTransporter
             , v.PermitContactFirstName
             , v.PermitContactMiddleInitial
             , v.PermitContactLastName
             , v.PermitContactTitle
             , v.PermitContactPhone
             , v.PermitContactPhoneExtension
             , v.PermitContactEmail
             , v.PermitContactAddressStreetNumber
             , v.PermitContactAddressAddress1
             , v.PermitContactAddressAddress2
             , v.PermitContactAddressCity
             , v.PermitContactAddressStateActivityLocation
             , v.PermitContactAddressStateCode
             , v.PermitContactAddressStateDescription
             , v.PermitContactAddressStateActive
             , v.PermitContactAddressForeignStateActivityLocation
             , v.PermitContactAddressForeignStateCode
             , v.PermitContactAddressForeignStateDescription
             , v.PermitContactAddressForeignStateActive
             , v.PermitContactAddressForeignStateName
             , v.PermitContactAddressForeignStateCountryCode
             , v.PermitContactAddressCountryActivityLocation
             , v.PermitContactAddressCountryCode
             , v.PermitContactAddressCountryDescription
             , v.PermitContactAddressCountryActive
             , v.PermitContactAddressZip
             , v.PermitFacilityExistenceDate
             , v.PermitNatureOfBusiness
             , v.HsmManaged
             , v.HsmFinancialAssurance
             , v.HsmReasonCode
             , v.HsmReasonDescription
             , v.HsmEffectiveDate
             , v.LqgSiteClosureCompliance
             , v.LqgSiteClosureExpectedClosureDate
             , v.LqgSiteClosureRequestedClosureDate
             , v.LqgSiteClosureDateClosed
             , v.LqgSiteClosureClosureTypeCode
             , v.LqgSiteClosureClosureTypeDescription
             , v.EpisodicEventTypeActivityLocation
             , v.EpisodicEventTypeCode
             , v.EpisodicEventTypeDescription
             , v.EpisodicEventTypeActive
             , v.EpisodicContactFirstName
             , v.EpisodicContactMiddleInitial
             , v.EpisodicContactLastName
             , v.EpisodicContactPhone
             , v.EpisodicContactPhoneExt
             , v.EpisodicContactEmail
             , v.EpisodicBeginDate
             , v.EpisodicEndDate
             , v.EpisodicRescind
             , v.EpisodicRescindComment
             , v.Comments
             , v.PublicComments
             , v.SrcUpdatedDate
             , v.SrcUpdatedBy
             , v.SrcCreatedDate
             , v.SrcCreatedBy
             , v.LastRecord
             , v.ElectronicManifestBroker
             , v.BrExempt
             , v.IncludeInNationalReport
             , v.ReportCycle
             , v.auditCreatedBy
             , v.auditCreatedDateUtc
             , v.auditModifiedBy
             , v.auditModifiedDateUtc
          FROM dbo.vwHandlerSourceHistory AS v
         WHERE v.HandlerSourceId = @HandlerSourceId;

        -- Not returned to the caller and not used to decide anything -- it exists so that a failure in
        -- the CATCH can say whether the row had been found, which is the difference between "the seek
        -- came back empty" and "something broke while reading a row that is there".
        SET @RowsReturned = @@ROWCOUNT;

        SET @ContextMessage = CONCAT (N'HandlerSourceId=', @HandlerSourceId
                                    , N', RowsReturned=', @RowsReturned);

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
        -- procedure never did, because a web page asked for a row. ROLLBACK is also illegal inside
        -- INSERT ... EXEC (error 8004), where it would replace the error being reported and abort this
        -- CATCH before the failure was recorded. A read that cannot record its own failure is precisely
        -- the defect this project is guarding against, and this body is a single SELECT -- the exact
        -- shape MDE's original defect took.

        -- Recovered into the context so a failure still says how far it got, since there is no start row
        -- carrying it. @RowsReturned is a variable, so it survives whatever the caller does to its
        -- transaction. Still an integer and a count: no column of the row reaches the log.
        SET @ContextMessage = CONCAT (COALESCE (@ContextMessage, N'(before the read)')
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
        -- client could no longer tell a deadlock from a non-positive id. The leading semicolon is
        -- required: a bare THROW immediately after BEGIN is a syntax error.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspGetHandlerSourceDetail'
    , @Description = N'Returns ONE handler version in full -- 215 of dbo.vwHandlerSourceHistory''s 218 columns -- to the monitoring web app, and it is the screen reached by clicking a row in dbo.uspGetHandlerSourcePage''s grid or dbo.uspGetHandlerSourceHistoryPage''s version list, both of which project HandlerSourceId for exactly this purpose. IT IS THE ONE READ WHERE THE PII DECISION INVERTS: the ~57 contact name, phone, email, mailing-address and SrcUpdatedBy columns that 503 and 504 withhold by name are RETURNED here, and four properties are what make that safe -- one row per call by construction, because the filter is an equality on the clustered primary key with no @Take to raise; the identifier is an IDENTITY surrogate key that cannot be derived from a name, so this can never be the first call in a session and offers no way to ask which handlers have a contact called X; EXECUTE is granted to RCRAInfoMonitorRole only, with build/check_permission_posture.py asserting both directions; and @KeyParameters carries an integer, so the payload contains PII while the log row contains a row number. Nothing read from the view is ever copied into @KeyParameters or @ContextMessage, not even to make an error message more helpful. dbo.uspSearchHandlerSource is the procedure that takes free text and it returns the narrow grid projection, not this one; the two must not be merged. It reads dbo.vwHandlerSourceHistory rather than dbo.vwHandlerSource, which is what makes 504''s list clickable: dbo.vwHandlerSource fixes CurrentRecord = 1, so every superseded version would come back empty when clicked, and the two procedures would each be correct alone and useless together. CurrentRecord is therefore projected and NULL on every row still means dbo.uspReconcileCurrentRecord has not stamped the handler. The three excluded columns are properties of the view rather than preferences -- IsDeleted is a constant 0 because the view''s own WHERE says so, and auditDeletedBy and auditDeletedDateUtc are masked to NULL unless the row is deleted, which through this view it cannot be. The 215 names are written out rather than SELECT *, because a projection returning contact details must be reviewable by reading the procedure, because SELECT * would ship whatever build/generate_schema.py adds next to a web page with no decision, and because ordinal position would become the generator''s to change; the cost is that an explicit list goes stale, so build/check_detail_projection.py compares it against sys.columns on every guardrail run and fails in either direction. @HandlerSourceId is REQUIRED with no default, and NULL or a non-positive value is REFUSED with a message while a row that does not exist or has been soft-deleted returns an EMPTY RESULT SET and no error -- the second is a legitimate race between the grid rendering and the click, the first is an unset control, and keeping them distinguishable is what lets an empty result mean one specific thing. A soft-deleted row and a row that never existed are deliberately indistinguishable from each other. Not paged and not sorted, so there is no @SortBy to whitelist, and NO OPTION (RECOMPILE) unlike 501, 503 and 504, because one plan -- a single-row clustered seek -- serves every input. Returns exactly ONE result set; the 19 child collections get their own reads keyed by the same HandlerSourceId. Instrumented for FAILURES ONLY, with no successful-path row, and it opens no transaction and therefore never rolls one back -- the body being a single SELECT makes it the exact shape of the MDE defect that motivated the requirement, so the CATCH is the only thing in it capable of recording anything. Omitting @HandlerSourceId raises error 201 before the body runs and is therefore the one error it cannot log. EXECUTE is granted to RCRAInfoMonitorRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of script
-- 050 the authoritative answer to what the two applications can do.
--
-- THE MONITOR ONLY, AND HERE THAT IS A DISCLOSURE BOUNDARY RATHER THAN A TIDINESS RULE -- this is the
-- procedure that returns contact names, phone numbers and email addresses. The console app has no
-- reason to read a handler back at all: it wrote the row, and dbo.uspReconcileCurrentRecord -- which
-- it does execute -- reads the flags it needs directly. Both directions are asserted by
-- build/check_permission_posture.py, so a future grant to the loader turns a check red rather than
-- passing unnoticed.
--
-- Ownership chaining carries the SELECT on dbo.vwHandlerSourceHistory, and through it on
-- dbo.HandlerSource, and the INSERT on logs.ExecutionLog, through this grant -- so the monitor login
-- holds no direct permission on any of the three (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspGetHandlerSourceDetail TO RCRAInfoMonitorRole;
END;
GO

PRINT N'505: dbo.uspGetHandlerSourceDetail created or altered, EXECUTE granted to the monitor role.';
GO
