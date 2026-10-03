-- SET XACT_ABORT ON sits ABOVE the header block deliberately, and moving it back below would restore a
-- real defect. The GO on the next line ends the batch, and sys.sql_modules stores only the batch that
-- contains CREATE -- so a header placed AFTER this GO is invisible to anyone reading the procedure out
-- of the database through sp_helptext, OBJECT_DEFINITION, or SSMS "Script as CREATE", which is where a
-- maintainer actually reads it.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspRefreshLookupSet
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Refreshes one mirrored EPA code list from a JSON array, a whole list at a time. G15 mirrors all 24 lookup/hd endpoints,
which are 23 response definitions and 24 tables -- StateDistrict carries a nested counties array that became
dbo.LookupStateDistrictCounty. @LookupName names the definition, and one branch below handles each. (The endpoint
prefix is written without its trailing glob character on purpose: a T-SQL block comment nests, so a slash-star inside
this header would open a comment that swallows its own terminator. The generated table scripts carry the same note.)

Two modes, and the difference between them is the whole safety story of this procedure:

    Full        @Elements is EPA's COMPLETE list for this lookup. Codes present in the mirror and absent from the
                payload are RETIRED -- soft-deleted, never removed.
    Upsert      @Elements is a PART of the list. Nothing is retired; codes are inserted, corrected, or revived only.

@Mode has no default on purpose. The natural default to write would be 'Full', and a caller that had fetched one page
of a paged list and relied on the default would then retire the entire remainder of the list in one call. Making the
caller say which of the two it means costs one argument and removes that failure entirely.

========================================================================================================================
Requirements and Key Dependencies:

The 24 dbo.Lookup* tables (scripts 200-223), logs.LoadRun (script 300), logs.DataQualityObservation (script 330).

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, copied from
.claude/skills/sql-objects/templates/procedure.sql.

build/check_lookup_coverage.py derives the lookup tables and their columns from sys.columns and fails if this procedure
does not handle every one of them, or handles a table that is not a lookup. The branch list below is hand-written
because script 050 denies the loader metadata visibility, so nothing here can walk the catalogue at run time; the gate
walks it externally instead. The tables are GENERATED, so without that gate the next code list EPA adds would arrive
mirrored and unrefreshable, and nothing would say so.

EXECUTE is granted to RCRAInfoLoaderRole only.

========================================================================================================================
Notes:

RETIRED IS NOT GONE, AND RETIRED IS NOT INACTIVE. Three states are distinct here and conflating any two of them loses
real information:

    Active = 0      EPA still publishes the code and says it should not be offered for new submissions.
    IsDeleted = 1   EPA no longer publishes the code at all. This procedure set it.
    (absent)        never mirrored.

Retirement is a soft delete because a retired code still has to RESOLVE. Every dbo.Lookup* table header says so: a code
EPA retires this year is still the correct code for a handler version submitted while it was current, so a historical
read must NOT filter IsDeleted = 0 on these tables. That is the one documented exception to this database's blanket read
rule, and this is the procedure that creates the rows it applies to.

A COMPLETE LIST CANNOT BE EMPTY. Full mode with an empty array is refused. Taken literally it means "EPA publishes no
accessibility codes at all", which has never been true of any of the 24 lists; taken as what it actually is, it means
the fetch returned nothing and the loader did not notice. Retiring an entire code list on the strength of an empty HTTP
response is the single worst thing this procedure could be talked into doing. Upsert mode accepts an empty array and
does nothing, because there it means only "no additions", which is an ordinary outcome.

A LIST THAT RETIRES MORE CODES THAN IT CONTAINS IS REFUSED, AND THE CHECK IS DELIBERATELY AFTER THE WRITE. Every other
validation in this procedure runs before BEGIN TRANSACTION, per the house pattern, so a rejected call has no
transaction to unwind. This one cannot: the retirement count is not knowable until the retirement has been attempted.
So it runs inside the transaction and refuses by throwing, and the transaction is what makes that safe. A real refresh
retires a handful of codes out of a list of hundreds; a truncated or partially-paged response retires most of the list.
@Retired > @ElementCount separates the two without needing a tuning knob, and the observation recorded alongside it
survives the rollback (G35) so the refusal explains itself in logs.DataQualityObservation rather than only in the
error.

THE MERGE MATCH IS UNFILTERED, AND MUST BE. UX_dbo_Lookup*_Natural excludes soft-deleted rows, so matching through it
would report a RETIRED code as NOT MATCHED and insert a second row with the same natural key -- and the filtered index
cannot reject that pair. Matching unfiltered means a code EPA brings back is REVIVED in place, which is both correct and
what keeps the filtered index satisfiable. The same reasoning is in script 400's and 520's headers.

HOLDLOCK IS NOT OPTIONAL, for the reason 520 gives: without it MERGE releases its read locks before writing, so two
calls carrying the same code can both evaluate NOT MATCHED and both insert, and the natural-key index then rejects one
of them on an unattended overnight load.

JSON true IS NOT CAST TO BIT, AND THIS IS THE TRAP THE FILE WOULD HAVE FALLEN INTO. JSON_VALUE returns the TEXT of a
JSON boolean, so it yields the string 'true' -- and CAST (N'true' AS BIT) fails while TRY_CAST (N'true' AS BIT) returns
NULL. Written the obvious way with TRY_CAST, every Active, IndustryApp, BrLoadActive and Acute value across all 24
lists would arrive NULL, silently, and the mirror would look loaded. The CASE in the shred below maps 'true'/'false'
and '1'/'0' explicitly and leaves anything else NULL.

WIDTHS ARE CHECKED, NOT TRUNCATED, and the shred is deliberately wide into NVARCHAR (4000) rather than using
OPENJSON ... WITH, which truncates silently (G36). A truncated code is not a missing code -- it is a DIFFERENT code,
and in a table whose whole purpose is to resolve codes that is the one failure mode that corrupts rather than loses.
The Code width varies by list (1 to 10), so it comes from the same @LookupName dispatch that decides whether the list
has an ActivityLocation at all; one check serves all 23 branches. The CASTs in the write statements are on the @Element
side because comparing raw NVARCHAR (4000) against the column would make the engine widen the COLUMN and scan.

SEVEN OF THE 23 LISTS HAVE NO ActivityLocation: Accessibility, HandlerSourceType, HsmLandBasedUnit, HsmReason,
LandType, LqgClosureType and NonNotifier. EPA keys those on code alone, and the tables were generated from EPA's own
`required`, so the natural-key check, the duplicate check and the retirement scope all read @HasActivityLocation rather
than assuming a uniform shape. A payload that supplies an activityLocation for one of those seven is not refused; the
value is ignored, because there is no column to put it in and refusing would break a caller for sending more than was
asked.

RETIREMENT IS SCOPED TO THE ActivityLocations THE PAYLOAD MENTIONS. A refresh that carries only MD must not retire a
code recorded under another activityLocation merely by not mentioning it. G2 says the handler scope is MD, but that is
a decision about which handlers are fetched, not a licence for one list's refresh to silently empty another
jurisdiction's codes out of the mirror.

RETIRING A DISTRICT RETIRES ITS COUNTIES. dbo.LookupStateDistrictCounty is the one lookup child table, and there is no
view over it, so a read of it filters ITS OWN IsDeleted. A district retired with its counties left live would leave
county rows reading as current under a district EPA no longer publishes -- the same disclosure shape that makes the
cascade in 522 mandatory rather than tidy. The cascade here is one statement, applied after the parent retirement and
convergent on its own.

CONVERGENCE IS THE POINT OF THE MATCHED GUARD. Every branch's WHEN MATCHED arm tests IS DISTINCT FROM on every column
it would write, so a second run with the same payload matches nothing, writes nothing, and does not move
auditModifiedDateUtc for a row nothing changed about. A refresh that touched every row every night would make
auditModifiedDateUtc mean "the loader ran" instead of "this code changed", and the audit trail would stop being able to
answer when EPA last altered a code list.

THE RUN MUST BE Running, and this procedure requires a @LoadRunId, for the reason 520 gives: a write the monitoring app
cannot attribute to a run is a write no operator can find.

WHAT @KeyParameters MAY CONTAIN. Identifiers and counts. @Elements is excluded BY NAME -- these are public code lists
rather than PII, but a single one runs to thousands of elements and logs.ExecutionLog has a different retention policy
from the mirror. The element count goes in instead. From MDE's own template: do NOT include parameters such as
passwords and Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- 1. The nightly refresh of a complete list.
DECLARE @Codes NVARCHAR (MAX) = N'[{"activityLocation":"MD","code":"OP","description":"Operator","active":true}
                                  ,{"activityLocation":"MD","code":"OW","description":"Owner","active":true}]';
DECLARE @Rows INT, @Retired INT;
EXEC dbo.uspRefreshLookupSet @LoadRunId = 1, @LookupName = N'ContactType', @Mode = N'Full', @Elements = @Codes
                           , @RowsAffected = @Rows OUTPUT, @RetiredRows = @Retired OUTPUT;

-- 2. A paged list: every page is an Upsert, so no page can retire what another page carried.
EXEC dbo.uspRefreshLookupSet @LoadRunId = 1, @LookupName = N'Naics', @Mode = N'Upsert', @Elements = @PageOne;
EXEC dbo.uspRefreshLookupSet @LoadRunId = 1, @LookupName = N'Naics', @Mode = N'Upsert', @Elements = @PageTwo;

-- 3. The one list with a nested child. counties is merged in the same call and the same transaction.
EXEC dbo.uspRefreshLookupSet @LoadRunId = 1, @LookupName = N'StateDistrict', @Mode = N'Full'
   , @Elements = N'[{"activityLocation":"MD","code":"BALT","description":"Baltimore","active":true
                    ,"counties":[{"activityLocation":"MD","code":"24005","description":"Baltimore County"
                                 ,"active":true}]}]';

Every branch seeks UX_dbo_Lookup*_Natural under HOLDLOCK for the merge; the retirement scans the target, which is
deliberate -- these tables run from a handful of rows to a few thousand, and scoping the retirement correctly matters
more here than saving a scan of a small table. The payload is parsed once. Instrumentation adds one singleton insert
per call and one singleton update on the successful path.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4. Structure chosen by MDE: one hand-written
											procedure with a branch per list, gated by build/check_lookup_coverage.py,
											rather than 24 generated per-table procedures.
2026-09-05	rsincero						@Elements validation now requires ISJSON (@Elements, ARRAY). See script
											520's history entry of the same date for the finding. It matters most on
											this procedure: in Full mode a payload that shreds to nothing retires
											every live code in the list.
2026-09-06	rsincero						The Description and CodeType widths are now dispatched per list beside
											the Code width, instead of being the constants 255 and 10 for all 23.
											Load run 2621 -- the first run to reach this procedure with a real EPA
											payload -- was refused on element 3 of 15 of the WasteCode list, and EPA
											declares no maxLength for either property, so 255 and 10 were never its
											bounds, only ours. WasteCode now dispatches 4000 and 50 and its table was
											widened to match by script 391. The Code width is UNCHANGED and stays
											strict, including WasteCode's published 6: a code that does not fit is
											not a long code, it is a different code.
2026-09-06	rsincero						The width refusal now names WHICH property overflowed and by how much,
											rather than listing all four limits and leaving the reader to guess. The
											old message cost a diagnosis cycle on run 2621. The offending VALUE is
											still never quoted -- only its property name and character count.
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspRefreshLookupSet
      @LoadRunId    INT
    , @LookupName   NVARCHAR (50)
    , @Mode         NVARCHAR (20)
    , @Elements     NVARCHAR (MAX)
    , @RowsAffected INT            = NULL OUTPUT
    , @RetiredRows  INT            = NULL OUTPUT
    , @ChildRows    INT            = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- AR8 instrumentation. Boilerplate: copied verbatim from the template.
    -- =============================================================================================
    -- The literal is not a fallback for odd cases; it is what the loader login actually logs, because
    -- script 050 denies it metadata visibility. See the header. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[dbo].[uspRefreshLookupSet]')
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

    DECLARE @ActorLogin          NVARCHAR (128)  = ORIGINAL_LOGIN ()
          , @NowUtc              DATETIME2       = SYSUTCDATETIME ()
          , @ElementCount        INT             = 0
          , @CountyCount         INT             = 0
          , @CodeWidth           INT             = NULL
          , @DescriptionWidth    INT             = NULL
          , @CodeTypeWidth       INT             = NULL
          , @HasActivityLocation BIT             = NULL
          , @OverWideColumn      NVARCHAR (20)   = NULL
          , @OverWideLength      INT             = NULL
          , @OverWideLimit       INT             = NULL
          , @OverWideOrdinal     INT             = NULL
          , @Written             INT             = 0
          , @Retired             INT             = 0
          , @ChildWritten        INT             = 0
          , @ChildRetired        INT             = 0
          , @ObservationsWritten INT             = NULL
          , @RunStatus           NVARCHAR (20)   = NULL
          , @Failure             NVARCHAR (2048) = NULL
          , @Committed           BIT             = 0;

    -- Identifiers and counts only. @Elements is excluded BY NAME; see the header. The element count is
    -- added after the shred, because that is when it is known.
    SET @KeyParameters = CONCAT (N'LoadRunId=',  COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(null)')
                               , N', LookupName=', COALESCE (@LookupName, N'(null)')
                               , N', Mode=',       COALESCE (@Mode, N'(null)'));

    -- The shred target is WIDE on purpose. OPENJSON ... WITH truncates silently (G36) and a truncated
    -- code is a DIFFERENT code -- see the header. Widths are checked below, before anything is written.
    --
    -- One table for all 23 lists. The columns that only one or two lists carry are NULL for the rest,
    -- which is the cost of a single shred and a single set of validations; the alternative is 23
    -- table variables that differ in four columns between them.
    DECLARE @Element TABLE
    (
        Ordinal                      INT             NOT NULL PRIMARY KEY,
        ActivityLocation             NVARCHAR (4000)     NULL,
        Code                         NVARCHAR (4000)     NULL,
        Description                  NVARCHAR (4000)     NULL,
        Active                       BIT                 NULL,
        SortOrder                    BIGINT              NULL,   -- HandlerSourceType only
        IndustryApp                  BIT                 NULL,   -- StateActivity only
        BrLoadActive                 BIT                 NULL,   -- WasteCode only
        CodeType                     NVARCHAR (4000)     NULL,   -- WasteCode only
        Acute                        BIT                 NULL,   -- WasteCode only
        EpisodicTypeActivityLocation NVARCHAR (4000)     NULL,   -- EpisodicProject only
        EpisodicTypeCode             NVARCHAR (4000)     NULL,   -- EpisodicProject only
        EpisodicTypeDescription      NVARCHAR (4000)     NULL,   -- EpisodicProject only
        EpisodicTypeActive           BIT                 NULL    -- EpisodicProject only
    );

    -- StateDistrict.counties, the one nested array across all 23 definitions. ParentOrdinal ties an
    -- element back to @Element.Ordinal; OrdinalPosition is EPA's own array index and is the child's
    -- only natural key, so it is mirrored zero-based exactly as the table's description says.
    DECLARE @County TABLE
    (
        ParentOrdinal    INT             NOT NULL,
        OrdinalPosition  INT             NOT NULL,
        ActivityLocation NVARCHAR (4000)     NULL,
        Code             NVARCHAR (4000)     NULL,
        Description      NVARCHAR (4000)     NULL,
        Active           BIT                 NULL,

        PRIMARY KEY (ParentOrdinal, OrdinalPosition)
    );

    -- The ActivityLocations this payload mentions, typed to the column so the retirement's join is a
    -- seek rather than a widened scan. Retirement is scoped to these; see the header.
    DECLARE @Location TABLE (ActivityLocation NVARCHAR (2) NOT NULL PRIMARY KEY);

    -- G35. Observations accumulate here rather than going straight to the table, so that a ROLLBACK
    -- cannot destroy the finding that explains the rollback. Flushed inside the transaction on
    -- success, and after the rollback in the CATCH.
    DECLARE @Observation TABLE
    (
        Ordinal         INT             IDENTITY (1, 1) NOT NULL PRIMARY KEY,
        ObservationType NVARCHAR (50)   NOT NULL,
        Severity        NVARCHAR (20)   NOT NULL,
        ObservedValue   NVARCHAR (400)      NULL,
        Detail          NVARCHAR (4000)     NULL
    );

    BEGIN TRY

        EXEC logs.uspStartExecutionLogging
              @ProcedureName          = @ProcName
            , @KeyParameters          = @KeyParameters
            , @StartDateUtc           = @StartTimeUtc
            , @ReCreatedAfterRollback = 0
            , @ExecutionLogId         = @ExecutionId OUTPUT;

        -- =========================================================================================
        -- ===== The procedure's own work starts here. ==============================================
        -- =========================================================================================

        -- -----------------------------------------------------------------------------------------
        -- 1. Validation, all of it before BEGIN TRANSACTION -- with the one documented exception in
        --    section 6, which cannot be known until after the write.
        -- -----------------------------------------------------------------------------------------
        -- Assigned first, then thrown. THROW's message argument accepts a literal or a variable and
        -- NOT an expression, so a concatenation written inline is a syntax error.
        IF @LoadRunId IS NULL
        BEGIN
            SET @Failure = N'@LoadRunId is required. A refresh the monitoring app cannot attribute to '
                         + N'a run is a change to the mirror that no operator can find, and a retired '
                         + N'code with no run behind it cannot be explained after the fact.';
            THROW 50000, @Failure, 1;
        END;

        IF @Mode IS NULL OR @Mode NOT IN (N'Full', N'Upsert')
        BEGIN
            SET @Failure = CONCAT (N'@Mode must be ''Full'' or ''Upsert''. It was '
                                 , COALESCE (N'''' + @Mode + N'''', N'NULL')
                                 , N'. Full means @Elements is EPA''s COMPLETE list and codes absent '
                                 , N'from it are retired; Upsert means @Elements is a PART of the '
                                 , N'list and nothing is retired. There is deliberately no default: '
                                 , N'a caller passing one page of a paged list under a default of '
                                 , N'''Full'' would retire the whole remainder of the list.');
            THROW 50000, @Failure, 1;
        END;

        -- ARRAY, not just ISJSON, for the reason script 520 records in full: a bare object passes a
        -- plain ISJSON, OPENJSON then enumerates its properties rather than its elements, and the
        -- caller gets error 245 from an internal CAST instead of this refusal. It matters most here,
        -- because in Full mode a payload that shreds to nothing is a payload that retires every live
        -- code in the list.
        IF @Elements IS NULL OR ISJSON (@Elements, ARRAY) = 0
        BEGIN
            SET @Failure = N'@Elements is NULL, is not valid JSON, or is not a JSON array. It must be '
                         + N'an array of objects carrying at least code -- and activityLocation for the '
                         + N'16 lists that have one -- even for a single element.';
            THROW 50000, @Failure, 1;
        END;

        -- The @LookupName dispatch. Two facts vary between the 23 lists and everything generic below
        -- reads them from here: how wide Code is, and whether the list has an ActivityLocation at all.
        -- Keeping them in one place is what lets a single set of validations serve all 23 branches.
        -- build/check_lookup_coverage.py checks this list against sys.columns, in both directions.
        SELECT @CodeWidth = CASE @LookupName
                                WHEN N'Accessibility'     THEN 1
                                WHEN N'ContactType'       THEN 2
                                WHEN N'Country'           THEN 2
                                WHEN N'County'            THEN 5
                                WHEN N'EpisodicProject'   THEN 3
                                WHEN N'EpisodicType'      THEN 1
                                WHEN N'GeneratorCategory' THEN 1
                                WHEN N'HandlerSourceType' THEN 1
                                WHEN N'HsmFacilityCode'   THEN 2
                                WHEN N'HsmLandBasedUnit'  THEN 2
                                WHEN N'HsmReason'         THEN 1
                                WHEN N'LandType'          THEN 1
                                WHEN N'Language'          THEN 2
                                WHEN N'LqgClosureType'    THEN 1
                                WHEN N'Naics'             THEN 6
                                WHEN N'NonNotifier'       THEN 1
                                WHEN N'OtherPermit'       THEN 1
                                WHEN N'Relationship'      THEN 1
                                WHEN N'State'             THEN 2
                                WHEN N'StateActivity'     THEN 5
                                WHEN N'StateDistrict'     THEN 10
                                WHEN N'UniversalWaste'    THEN 10
                                WHEN N'WasteCode'         THEN 6
                            END
             -- The seven without one. EPA keys those on code alone and the tables were generated from
             -- EPA's own `required`, so this is not a simplification -- it is the shape of the data.
             , @HasActivityLocation = CASE WHEN @LookupName IN (N'Accessibility', N'HandlerSourceType'
                                                             , N'HsmLandBasedUnit', N'HsmReason'
                                                             , N'LandType', N'LqgClosureType'
                                                             , N'NonNotifier')
                                          THEN 0 ELSE 1 END;

        -- Description and CodeType widths, dispatched separately from @CodeWidth above so that
        -- build/check_lookup_coverage.py keeps reading the @CodeWidth arms and only those -- it
        -- cross-checks them against the real Code columns and must not see these numbers.
        --
        -- 255 and 10 are the defaults because 22 of the 23 lists still fit them. WasteCode does not:
        -- EPA declares no maxLength for either property, so those figures were this project's
        -- inference and run 2621 disproved them. dbo.LookupWasteCode was widened to 4000 and 50 by
        -- script 391, and these two numbers are the same decision expressed where it is enforced.
        --
        -- 4000 IS ALSO THE CEILING, not merely a generous choice: the shred above reads Description
        -- with JSON_VALUE, which returns NULL in lax mode for a value longer than 4000 characters
        -- instead of clipping it. A wider column could never be filled through this procedure.
        SELECT @DescriptionWidth = CASE @LookupName WHEN N'WasteCode' THEN 4000 ELSE  255 END
             , @CodeTypeWidth    = CASE @LookupName WHEN N'WasteCode' THEN   50 ELSE   10 END;

        IF @CodeWidth IS NULL
        BEGIN
            SET @Failure = CONCAT (N'@LookupName '
                                 , COALESCE (N'''' + @LookupName + N'''', N'is NULL and')
                                 , N' names no mirrored EPA code list. The 23 names are: '
                                 , N'Accessibility, ContactType, Country, County, EpisodicProject, '
                                 , N'EpisodicType, GeneratorCategory, HandlerSourceType, '
                                 , N'HsmFacilityCode, HsmLandBasedUnit, HsmReason, LandType, '
                                 , N'Language, LqgClosureType, Naics, NonNotifier, OtherPermit, '
                                 , N'Relationship, State, StateActivity, StateDistrict, '
                                 , N'UniversalWaste, WasteCode. They are EPA''s response definition '
                                 , N'names, not the endpoint paths: /lookup/hd/submittal-reasons is '
                                 , N'''HandlerSourceType'', and naics and naics-codes are both '
                                 , N'''Naics''.');
            THROW 50000, @Failure, 1;
        END;

        -- The run has to exist, be live, and still be Running -- 520's reasoning, unchanged.
        SELECT @RunStatus = r.Status
          FROM logs.LoadRun AS r
         WHERE r.LoadRunId = @LoadRunId
           AND r.IsDeleted = 0;

        IF @RunStatus IS NULL
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' does not exist, or has been '
                                 , N'soft-deleted. Open a run with logs.uspStartLoadRun and refresh '
                                 , N'the lookups against the LoadRunId it returns.');
            THROW 50000, @Failure, 1;
        END;

        IF @RunStatus <> N'Running'
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' has Status ''', @RunStatus
                                 , N''', so it is closed and no further work can be recorded against '
                                 , N'it. Refreshing a code list under a closed run would change the '
                                 , N'mirror with nothing in the run summary to account for it.');
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 2. Shred. JSON_VALUE rather than OPENJSON ... WITH, so nothing is truncated on the way in.
        -- -----------------------------------------------------------------------------------------
        -- THE BOOLEANS ARE MAPPED, NOT CAST. JSON_VALUE returns the TEXT of a JSON boolean, so a
        -- payload's `true` arrives as the string 'true' -- CAST (N'true' AS BIT) raises, and
        -- TRY_CAST (N'true' AS BIT) returns NULL. Written with TRY_CAST this procedure would blank
        -- every Active, IndustryApp, BrLoadActive and Acute value across all 24 tables and report
        -- success. '1'/'0' are accepted too, for a client that serialises booleans numerically.
        INSERT INTO @Element (Ordinal, ActivityLocation, Code, Description, Active, SortOrder
                            , IndustryApp, BrLoadActive, CodeType, Acute
                            , EpisodicTypeActivityLocation, EpisodicTypeCode
                            , EpisodicTypeDescription, EpisodicTypeActive)
        SELECT CAST (e.[key] AS INT) + 1
             , JSON_VALUE (e.value, '$.activityLocation')
             , JSON_VALUE (e.value, '$.code')
             -- JSON_VALUE returns NULL in lax mode for a value longer than 4000 characters rather
             -- than clipping it. Accepted here, and it is also why 4000 is the widest any of these
             -- Description columns is ever set to: a wider column could not be filled through this
             -- statement. A clip would be a lie about a code's meaning; a NULL is visibly absent.
             --
             -- The columns themselves hold 255 for 22 of the 23 lists and 4000 for WasteCode, which
             -- is where EPA's descriptions turned out not to be short text after all (run 2621).
             , JSON_VALUE (e.value, '$.description')
             , CASE LOWER (JSON_VALUE (e.value, '$.active'))
                    WHEN N'true' THEN 1 WHEN N'false' THEN 0
                    WHEN N'1'    THEN 1 WHEN N'0'     THEN 0 END
             , TRY_CAST (JSON_VALUE (e.value, '$.sortOrder') AS BIGINT)
             , CASE LOWER (JSON_VALUE (e.value, '$.industryApp'))
                    WHEN N'true' THEN 1 WHEN N'false' THEN 0
                    WHEN N'1'    THEN 1 WHEN N'0'     THEN 0 END
             , CASE LOWER (JSON_VALUE (e.value, '$.brLoadActive'))
                    WHEN N'true' THEN 1 WHEN N'false' THEN 0
                    WHEN N'1'    THEN 1 WHEN N'0'     THEN 0 END
             , JSON_VALUE (e.value, '$.codeType')
             , CASE LOWER (JSON_VALUE (e.value, '$.acute'))
                    WHEN N'true' THEN 1 WHEN N'false' THEN 0
                    WHEN N'1'    THEN 1 WHEN N'0'     THEN 0 END
             -- EpisodicProject references EpisodicType, which is itself a mirrored list. The
             -- reference is FLATTENED into four columns rather than turned into a foreign key, per
             -- B2 -- see the generated table's header for why. So it is shredded from the nested
             -- object here rather than resolved against dbo.LookupEpisodicType.
             , JSON_VALUE (e.value, '$.episodicType.activityLocation')
             , JSON_VALUE (e.value, '$.episodicType.code')
             , JSON_VALUE (e.value, '$.episodicType.description')
             , CASE LOWER (JSON_VALUE (e.value, '$.episodicType.active'))
                    WHEN N'true' THEN 1 WHEN N'false' THEN 0
                    WHEN N'1'    THEN 1 WHEN N'0'     THEN 0 END
          FROM OPENJSON (@Elements) AS e;

        SET @ElementCount = @@ROWCOUNT;
        SET @KeyParameters = CONCAT (@KeyParameters, N', Elements=', @ElementCount);

        -- The nested array, for the one list that has one. Guarded rather than shredded
        -- unconditionally so that a stray `counties` property on any other list is ignored rather
        -- than half-processed into a table that is not this list's child.
        IF @LookupName = N'StateDistrict'
        BEGIN
            INSERT INTO @County (ParentOrdinal, OrdinalPosition, ActivityLocation, Code, Description
                               , Active)
            SELECT CAST (e.[key] AS INT) + 1
                 -- ZERO-based, and not adjusted. OrdinalPosition mirrors EPA's array index, which is
                 -- both the fidelity of the mirror and the child's only natural key.
                 , CAST (c.[key] AS INT)
                 , JSON_VALUE (c.value, '$.activityLocation')
                 , JSON_VALUE (c.value, '$.code')
                 , JSON_VALUE (c.value, '$.description')
                 , CASE LOWER (JSON_VALUE (c.value, '$.active'))
                        WHEN N'true' THEN 1 WHEN N'false' THEN 0
                        WHEN N'1'    THEN 1 WHEN N'0'     THEN 0 END
              FROM OPENJSON (@Elements) AS e
             CROSS APPLY OPENJSON (e.value, '$.counties') AS c;

            SET @CountyCount = @@ROWCOUNT;
            SET @KeyParameters = CONCAT (@KeyParameters, N', Counties=', @CountyCount);
        END;

        -- -----------------------------------------------------------------------------------------
        -- 3. The natural key, complete and within its widths. Both checks name the offending
        --    element, because "the list was rejected" is not actionable at 02:00.
        -- -----------------------------------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM @Element
                    WHERE Code IS NULL
                       OR (@HasActivityLocation = 1 AND ActivityLocation IS NULL))
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount, N' in the '
                                    , @LookupName, N' list is missing part of the natural key ('
                                    , CASE WHEN @HasActivityLocation = 1
                                           THEN N'activityLocation, code' ELSE N'code' END
                                    , N'). Neither can be defaulted, so the list is rejected whole '
                                    , N'rather than partly applied -- a half-applied Full refresh '
                                    , N'would retire the codes its missing half was carrying.')
              FROM @Element
             WHERE Code IS NULL
                OR (@HasActivityLocation = 1 AND ActivityLocation IS NULL);
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                    WHERE LEN (Code)             > @CodeWidth
                       OR LEN (ActivityLocation) > 2
                       OR LEN (Description)      > @DescriptionWidth
                       OR LEN (CodeType)         > @CodeTypeWidth)
        BEGIN
            -- WHICH property, and by how much. The previous wording listed all four limits and left
            -- the reader to work out which one had been hit, which on run 2621 cost a diagnosis
            -- cycle against a list that only refreshes as part of a whole load.
            --
            -- The offending VALUE is deliberately not quoted -- only its property name and its
            -- character count. A length is a diagnostic; the value is data, and this procedure's
            -- messages travel into logs.ExecutionLog and the application log.
            SELECT TOP (1)
                   @OverWideColumn = CASE WHEN LEN (Code)             > @CodeWidth        THEN N'code'
                                          WHEN LEN (ActivityLocation) > 2                 THEN N'activityLocation'
                                          WHEN LEN (Description)      > @DescriptionWidth THEN N'description'
                                          ELSE N'codeType' END
                 , @OverWideLength = CASE WHEN LEN (Code)             > @CodeWidth        THEN LEN (Code)
                                          WHEN LEN (ActivityLocation) > 2                 THEN LEN (ActivityLocation)
                                          WHEN LEN (Description)      > @DescriptionWidth THEN LEN (Description)
                                          ELSE LEN (CodeType) END
                 , @OverWideLimit  = CASE WHEN LEN (Code)             > @CodeWidth        THEN @CodeWidth
                                          WHEN LEN (ActivityLocation) > 2                 THEN 2
                                          WHEN LEN (Description)      > @DescriptionWidth THEN @DescriptionWidth
                                          ELSE @CodeTypeWidth END
                 , @OverWideOrdinal = Ordinal
              FROM @Element
             WHERE LEN (Code)             > @CodeWidth        OR LEN (ActivityLocation) > 2
                OR LEN (Description)      > @DescriptionWidth OR LEN (CodeType)         > @CodeTypeWidth
             ORDER BY Ordinal;

            SET @Failure = CONCAT (N'Element ', @OverWideOrdinal, N' of ', @ElementCount, N' in the '
                                 , @LookupName, N' list carries a ', @OverWideColumn, N' of '
                                 , @OverWideLength, N' character(s), and the column that stores it '
                                 , N'holds ', @OverWideLimit, N'. This is checked rather than '
                                 , N'truncated because a truncated code is not a missing code -- it '
                                 , N'is a DIFFERENT code, and these are the tables whose only job is '
                                 , N'to resolve codes. The value itself is not reproduced here.'
                                 , CASE WHEN @OverWideColumn IN (N'code', N'activityLocation')
                                        THEN N' A natural-key property is over width, which EPA '
                                           + N'DOES bound in its own response definition, so this is '
                                           + N'EPA exceeding its published maxLength (G36) and '
                                           + N'widening the column would be the wrong fix.'
                                        ELSE N' EPA declares no maxLength for this property, so the '
                                           + N'limit is this project''s own: widen the column and '
                                           + N'the matching width in this procedure''s dispatch, as '
                                           + N'script 391 did for WasteCode.' END);
            THROW 50000, @Failure, 1;
        END;

        IF @LookupName = N'StateDistrict'
           AND EXISTS (SELECT 1 FROM @County
                        WHERE Code IS NULL OR LEN (Code) > 5
                           OR LEN (ActivityLocation) > 2 OR LEN (Description) > 255)
        BEGIN
            SELECT @Failure = CONCAT (N'County element ', MIN (OrdinalPosition), N' of district '
                                    , N'element ', MIN (ParentOrdinal), N' has no code, or carries a '
                                    , N'value wider than its column (code 5, activityLocation 2, '
                                    , N'description 255). The counties array is mirrored as '
                                    , N'dbo.LookupStateDistrictCounty and is held to the same '
                                    , N'widths as the parent list.')
              FROM @County
             WHERE Code IS NULL OR LEN (Code) > 5
                OR LEN (ActivityLocation) > 2 OR LEN (Description) > 255;
            THROW 50000, @Failure, 1;
        END;

        -- A duplicate key inside the set would raise error 8672 from inside the MERGE, whose message
        -- names neither the key nor the element. Checked here so the message is useful. The GROUP BY
        -- collapses ActivityLocation for the seven lists that do not have one.
        IF EXISTS (SELECT 1 FROM @Element
                   GROUP BY CASE WHEN @HasActivityLocation = 1 THEN ActivityLocation END, Code
                   HAVING COUNT (*) > 1)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'The ', @LookupName, N' list names code ''', Code
                                            , N''' ', COUNT (*), N' times'
                                            , CASE WHEN @HasActivityLocation = 1
                                                   THEN N' for activityLocation '''
                                                      + MIN (ActivityLocation) + N'''' ELSE N'' END
                                            , N'. One element per code: two elements targeting the '
                                            , N'same key would make the stored description depend on '
                                            , N'which one the engine applied last.')
              FROM @Element
             GROUP BY CASE WHEN @HasActivityLocation = 1 THEN ActivityLocation END, Code
            HAVING COUNT (*) > 1
             ORDER BY MIN (Ordinal);
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 4. The refusal that matters most. See the header: Full mode with an empty array means
        --    "EPA publishes no codes at all for this list", which has never been true of any of the
        --    24 -- what it actually means is that the fetch returned nothing and the loader did not
        --    notice. Upsert accepts it, because there it only means "no additions".
        -- -----------------------------------------------------------------------------------------
        IF @Mode = N'Full' AND @ElementCount = 0
        BEGIN
            SET @Failure = CONCAT (N'@Mode is ''Full'' and @Elements is empty, which would retire '
                                 , N'every live code in the ', @LookupName, N' list. A complete code '
                                 , N'list is never empty, so this is a fetch that returned nothing '
                                 , N'rather than a list EPA has emptied. Check the API response. If '
                                 , N'the intent really is to record additions only, pass '
                                 , N'@Mode = ''Upsert'', where an empty array is a no-op.');
            THROW 50000, @Failure, 1;
        END;

        -- Retirement is scoped to the ActivityLocations the payload mentions; see the header. Built
        -- once, used by whichever branch runs.
        IF @HasActivityLocation = 1
        BEGIN
            INSERT INTO @Location (ActivityLocation)
            SELECT DISTINCT CAST (ActivityLocation AS NVARCHAR (2)) FROM @Element;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 5. Write. One branch per EPA code list, in alphabetical order.
        --
        --    THE FIRST TWO BRANCHES CARRY THE COMMENTS FOR ALL 23. Every branch has the same three
        --    parts and they mean the same thing in each: an unfiltered MERGE under HOLDLOCK whose
        --    MATCHED arm is guarded by IS DISTINCT FROM on every column it writes, so a re-run is a
        --    no-op; a retirement guarded by @Mode = 'Full' and scoped to @Location; and a @@ROWCOUNT
        --    capture. Repeating the reasoning 23 times would make a change to it 23 edits, of which
        --    22 would eventually be missed.
        -- -----------------------------------------------------------------------------------------
        BEGIN TRANSACTION;

        IF @LookupName = N'Accessibility'
        BEGIN
            -- Code and description only: no ActivityLocation to scope by, and no Active, so EPA's
            -- only statement about one of these codes is its text.
            --
            -- THE MATCH IS UNFILTERED, and that is the load-bearing detail of every branch below. A
            -- retired row MUST be matched, or it is reported NOT MATCHED and a second row with the
            -- same natural key is inserted -- which the filtered unique index cannot reject. Matching
            -- unfiltered means a code EPA brings back is revived in place. HOLDLOCK is not optional
            -- either: without it two concurrent calls can both evaluate NOT MATCHED and both insert.
            MERGE dbo.LookupAccessibility WITH (HOLDLOCK) AS tgt
            USING (SELECT Code        = CAST (e.Code        AS NVARCHAR (1))
                        , Description = CAST (e.Description AS NVARCHAR (255))
                     FROM @Element AS e) AS src
               ON tgt.Code = src.Code

            -- The guard is what makes a re-run write nothing at all: IS DISTINCT FROM on every column
            -- the arm sets, plus IsDeleted = 1 so a revival is not mistaken for a no-op.
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
                      -- auditDeleted* are deliberately left alone on a revive. They record when the
                      -- code was last retired, which is history and stays true; there is no column
                      -- for "and then EPA published it again", and the ExecutionLog row has the call.

            WHEN NOT MATCHED BY TARGET
            THEN INSERT (Code, Description)
                 VALUES (src.Code, src.Description);
                 -- The create-audit columns are left to the table's DEFAULTs, per MDE's decision that
                 -- they are the table's business. auditModifiedDateUtc is set EXPLICITLY on the
                 -- UPDATE arm above, because its default fires on INSERT only.

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupAccessibility AS r
                 WHERE r.IsDeleted = 0
                   -- The CAST is on the ELEMENT side: comparing the column against NVARCHAR (4000)
                   -- would make the engine widen the COLUMN. The width check has already passed, so
                   -- it cannot lose anything.
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.Code AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        -- No semicolon: a terminated IF cannot take an ELSE.
        END
        ELSE IF @LookupName = N'ContactType'
        BEGIN
            -- The shape 13 of the 23 lists share: activityLocation, code, description, active.
            MERGE dbo.LookupContactType WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (2))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupContactType AS r
                  -- THE JOIN TO @Location IS THE SCOPE, and leaving it out would be a real defect: a
                  -- refresh carrying only MD would retire every code recorded under every other
                  -- activityLocation merely by not mentioning them. See the header.
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (2)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'Country'
        BEGIN
            MERGE dbo.LookupCountry WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (2))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupCountry AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (2)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'County'
        BEGIN
            MERGE dbo.LookupCounty WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (5))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupCounty AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (5)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'EpisodicProject'
        BEGIN
            -- The four EpisodicType* columns are EPA's nested episodicType object, FLATTENED rather
            -- than resolved against dbo.LookupEpisodicType -- per B2, no handler or lookup column
            -- takes a foreign key to a lookup table, because a code EPA retires still has to resolve
            -- and a foreign key would make loading the two lists order-dependent.
            MERGE dbo.LookupEpisodicProject WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation             = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code                         = CAST (e.Code             AS NVARCHAR (3))
                        , Description                  = CAST (e.Description      AS NVARCHAR (255))
                        , Active                       = e.Active
                        , EpisodicTypeActivityLocation  = CAST (e.EpisodicTypeActivityLocation AS NVARCHAR (2))
                        , EpisodicTypeCode              = CAST (e.EpisodicTypeCode             AS NVARCHAR (1))
                        , EpisodicTypeDescription       = CAST (e.EpisodicTypeDescription      AS NVARCHAR (255))
                        , EpisodicTypeActive            = e.EpisodicTypeActive
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description                  IS DISTINCT FROM src.Description
                          OR  tgt.Active                       IS DISTINCT FROM src.Active
                          OR  tgt.EpisodicTypeActivityLocation IS DISTINCT FROM src.EpisodicTypeActivityLocation
                          OR  tgt.EpisodicTypeCode             IS DISTINCT FROM src.EpisodicTypeCode
                          OR  tgt.EpisodicTypeDescription      IS DISTINCT FROM src.EpisodicTypeDescription
                          OR  tgt.EpisodicTypeActive           IS DISTINCT FROM src.EpisodicTypeActive)
            THEN UPDATE
                    SET IsDeleted                    = 0
                      , Description                  = src.Description
                      , Active                       = src.Active
                      , EpisodicTypeActivityLocation = src.EpisodicTypeActivityLocation
                      , EpisodicTypeCode             = src.EpisodicTypeCode
                      , EpisodicTypeDescription      = src.EpisodicTypeDescription
                      , EpisodicTypeActive           = src.EpisodicTypeActive
                      , auditModifiedBy              = @ActorLogin
                      , auditModifiedDateUtc         = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active, EpisodicTypeActivityLocation
                       , EpisodicTypeCode, EpisodicTypeDescription, EpisodicTypeActive)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active
                       , src.EpisodicTypeActivityLocation, src.EpisodicTypeCode
                       , src.EpisodicTypeDescription, src.EpisodicTypeActive);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupEpisodicProject AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (3)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'EpisodicType'
        BEGIN
            MERGE dbo.LookupEpisodicType WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (1))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupEpisodicType AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'GeneratorCategory'
        BEGIN
            MERGE dbo.LookupGeneratorCategory WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (1))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupGeneratorCategory AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'HandlerSourceType'
        BEGIN
            -- EPA serves this one from /lookup/hd/submittal-reasons, and it is the only list with a
            -- SortOrder. The name here is the RESPONSE DEFINITION name, which is what every other
            -- branch uses too; see the @LookupName refusal message.
            MERGE dbo.LookupHandlerSourceType WITH (HOLDLOCK) AS tgt
            USING (SELECT Code        = CAST (e.Code        AS NVARCHAR (1))
                        , Description = CAST (e.Description AS NVARCHAR (255))
                        , SortOrder   = e.SortOrder
                     FROM @Element AS e) AS src
               ON tgt.Code = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.SortOrder   IS DISTINCT FROM src.SortOrder)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , SortOrder            = src.SortOrder
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (Code, Description, SortOrder)
                 VALUES (src.Code, src.Description, src.SortOrder);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupHandlerSourceType AS r
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.Code AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'HsmFacilityCode'
        BEGIN
            MERGE dbo.LookupHsmFacilityCode WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (2))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupHsmFacilityCode AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (2)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'HsmLandBasedUnit'
        BEGIN
            MERGE dbo.LookupHsmLandBasedUnit WITH (HOLDLOCK) AS tgt
            USING (SELECT Code        = CAST (e.Code        AS NVARCHAR (2))
                        , Description = CAST (e.Description AS NVARCHAR (255))
                     FROM @Element AS e) AS src
               ON tgt.Code = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (Code, Description)
                 VALUES (src.Code, src.Description);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupHsmLandBasedUnit AS r
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.Code AS NVARCHAR (2)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'HsmReason'
        BEGIN
            MERGE dbo.LookupHsmReason WITH (HOLDLOCK) AS tgt
            USING (SELECT Code        = CAST (e.Code        AS NVARCHAR (1))
                        , Description = CAST (e.Description AS NVARCHAR (255))
                     FROM @Element AS e) AS src
               ON tgt.Code = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (Code, Description)
                 VALUES (src.Code, src.Description);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupHsmReason AS r
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.Code AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'LandType'
        BEGIN
            MERGE dbo.LookupLandType WITH (HOLDLOCK) AS tgt
            USING (SELECT Code        = CAST (e.Code        AS NVARCHAR (1))
                        , Description = CAST (e.Description AS NVARCHAR (255))
                     FROM @Element AS e) AS src
               ON tgt.Code = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (Code, Description)
                 VALUES (src.Code, src.Description);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupLandType AS r
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.Code AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'Language'
        BEGIN
            MERGE dbo.LookupLanguage WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (2))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupLanguage AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (2)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'LqgClosureType'
        BEGIN
            MERGE dbo.LookupLqgClosureType WITH (HOLDLOCK) AS tgt
            USING (SELECT Code        = CAST (e.Code        AS NVARCHAR (1))
                        , Description = CAST (e.Description AS NVARCHAR (255))
                     FROM @Element AS e) AS src
               ON tgt.Code = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (Code, Description)
                 VALUES (src.Code, src.Description);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupLqgClosureType AS r
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.Code AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'Naics'
        BEGIN
            -- Two endpoints, /lookup/hd/naics and /lookup/hd/naics-codes, return this one definition
            -- and therefore mirror into this one table. It is also the longest of the 23 lists, so it
            -- is the one most likely to be fetched in pages -- which is what @Mode = 'Upsert' is for.
            MERGE dbo.LookupNaics WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (6))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupNaics AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (6)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'NonNotifier'
        BEGIN
            MERGE dbo.LookupNonNotifier WITH (HOLDLOCK) AS tgt
            USING (SELECT Code        = CAST (e.Code        AS NVARCHAR (1))
                        , Description = CAST (e.Description AS NVARCHAR (255))
                     FROM @Element AS e) AS src
               ON tgt.Code = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (Code, Description)
                 VALUES (src.Code, src.Description);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupNonNotifier AS r
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.Code AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'OtherPermit'
        BEGIN
            MERGE dbo.LookupOtherPermit WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (1))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupOtherPermit AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'Relationship'
        BEGIN
            MERGE dbo.LookupRelationship WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (1))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupRelationship AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (1)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'State'
        BEGIN
            -- The state list is mirrored WHOLE, and must be. G2 scopes the HANDLERS fetched to MD; it
            -- says nothing about the values a contact or a mailing address may carry, which are
            -- expected to be out of state. No state column anywhere in this database is constrained
            -- to MD, and this list is why that is workable.
            MERGE dbo.LookupState WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (2))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupState AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (2)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'StateActivity'
        BEGIN
            MERGE dbo.LookupStateActivity WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (5))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                        , IndustryApp      = e.IndustryApp
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active
                          OR  tgt.IndustryApp IS DISTINCT FROM src.IndustryApp)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , IndustryApp          = src.IndustryApp
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active, IndustryApp)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active, src.IndustryApp);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupStateActivity AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (5)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'StateDistrict'
        BEGIN
            -- THE ONLY LIST WITH A CHILD. Five statements, in an order that matters: the parent merge
            -- first, because the child rows need LookupStateDistrictId and a district new in this
            -- payload has no surrogate key until its INSERT has run.
            MERGE dbo.LookupStateDistrict WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (10))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupStateDistrict AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2))  = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (10)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;

            -- The counties. The parent is resolved by NATURAL key rather than by an OUTPUT clause off
            -- the merge above, because the merge's MATCHED arm is guarded -- a district that had not
            -- changed is not matched at all and so would not appear in an OUTPUT, and its counties
            -- would then be silently skipped. IsDeleted = 0 on the parent: a district this call just
            -- retired takes its counties with it in the cascade below rather than merging new ones.
            MERGE dbo.LookupStateDistrictCounty WITH (HOLDLOCK) AS tgt
            USING (SELECT p.LookupStateDistrictId
                        , c.OrdinalPosition
                        , ActivityLocation = CAST (c.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (c.Code             AS NVARCHAR (5))
                        , Description      = CAST (c.Description      AS NVARCHAR (255))
                        , Active           = c.Active
                     FROM @County AS c
                     JOIN @Element AS e
                       ON e.Ordinal = c.ParentOrdinal
                     JOIN dbo.LookupStateDistrict AS p
                       ON p.ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                      AND p.Code             = CAST (e.Code             AS NVARCHAR (10))
                      AND p.IsDeleted        = 0) AS src
               ON tgt.LookupStateDistrictId = src.LookupStateDistrictId
              AND tgt.OrdinalPosition       = src.OrdinalPosition
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.ActivityLocation IS DISTINCT FROM src.ActivityLocation
                          OR  tgt.Code             IS DISTINCT FROM src.Code
                          OR  tgt.Description      IS DISTINCT FROM src.Description
                          OR  tgt.Active           IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , ActivityLocation     = src.ActivityLocation
                      , Code                 = src.Code
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (LookupStateDistrictId, OrdinalPosition, ActivityLocation, Code, Description
                       , Active)
                 VALUES (src.LookupStateDistrictId, src.OrdinalPosition, src.ActivityLocation
                       , src.Code, src.Description, src.Active);

            SET @ChildWritten = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                -- Counties EPA dropped from a district it still publishes. Scoped through @Element to
                -- the districts THIS payload carried, so a partial refresh cannot empty another
                -- district's county list.
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupStateDistrictCounty AS r
                  JOIN dbo.LookupStateDistrict AS p
                    ON p.LookupStateDistrictId = r.LookupStateDistrictId
                  JOIN @Element AS e
                    ON CAST (e.ActivityLocation AS NVARCHAR (2))  = p.ActivityLocation
                   AND CAST (e.Code             AS NVARCHAR (10)) = p.Code
                 WHERE r.IsDeleted = 0
                   AND p.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @County AS c
                                    WHERE c.ParentOrdinal   = e.Ordinal
                                      AND c.OrdinalPosition = r.OrdinalPosition);

                SET @ChildRetired = @@ROWCOUNT;
            END;

            -- THE CASCADE. A district retired with its counties left live leaves county rows reading
            -- as current under a district EPA no longer publishes -- and there is no view over this
            -- child, so a read of it filters ITS OWN IsDeleted and would show them. Same reasoning as
            -- the cascade in 522, one statement instead of 21.
            --
            -- Deliberately NOT scoped to this call's retirements: it states the invariant -- no live
            -- county under a retired district -- rather than the delta, so it also repairs a pair left
            -- inconsistent by an earlier failure, and it converges to zero rows once it has run. It
            -- runs outside the @Mode guard for the same reason: Upsert never retires a district, but
            -- if one is already retired its counties should not be live regardless of this call's mode.
            UPDATE r
               SET IsDeleted            = 1
                 , auditDeletedBy       = @ActorLogin
                 , auditDeletedDateUtc  = @NowUtc
                 , auditModifiedBy      = @ActorLogin
                 , auditModifiedDateUtc = @NowUtc
              FROM dbo.LookupStateDistrictCounty AS r
              JOIN dbo.LookupStateDistrict AS p
                ON p.LookupStateDistrictId = r.LookupStateDistrictId
             WHERE r.IsDeleted = 0
               AND p.IsDeleted = 1;

            SET @ChildRetired += @@ROWCOUNT;
        END
        ELSE IF @LookupName = N'UniversalWaste'
        BEGIN
            MERGE dbo.LookupUniversalWaste WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (10))
                        , Description      = CAST (e.Description      AS NVARCHAR (255))
                        , Active           = e.Active
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description IS DISTINCT FROM src.Description
                          OR  tgt.Active      IS DISTINCT FROM src.Active)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupUniversalWaste AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2))  = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (10)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE IF @LookupName = N'WasteCode'
        BEGIN
            -- The widest of the 23 shapes: EPA adds brLoadActive, codeType and acute to the usual
            -- four. Acute in particular is regulatory rather than cosmetic -- it distinguishes acute
            -- hazardous waste, whose generator thresholds are two orders of magnitude lower -- so it
            -- is in the MATCHED guard like every other column, and a change to it moves
            -- auditModifiedDateUtc exactly as a description change does.
            -- 4000 and 50, not 255 and 10, and the table matches: EPA declares no maxLength for
            -- either property and sent a longer one on run 2621. Script 224 widened the columns and
            -- the @DescriptionWidth / @CodeTypeWidth dispatch above refuses anything past these.
            -- Code stays at EPA's published 6.
            MERGE dbo.LookupWasteCode WITH (HOLDLOCK) AS tgt
            USING (SELECT ActivityLocation = CAST (e.ActivityLocation AS NVARCHAR (2))
                        , Code             = CAST (e.Code             AS NVARCHAR (6))
                        , Description      = CAST (e.Description      AS NVARCHAR (4000))
                        , Active           = e.Active
                        , BrLoadActive     = e.BrLoadActive
                        , CodeType         = CAST (e.CodeType         AS NVARCHAR (50))
                        , Acute            = e.Acute
                     FROM @Element AS e) AS src
               ON tgt.ActivityLocation = src.ActivityLocation
              AND tgt.Code             = src.Code
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.Description  IS DISTINCT FROM src.Description
                          OR  tgt.Active       IS DISTINCT FROM src.Active
                          OR  tgt.BrLoadActive IS DISTINCT FROM src.BrLoadActive
                          OR  tgt.CodeType     IS DISTINCT FROM src.CodeType
                          OR  tgt.Acute        IS DISTINCT FROM src.Acute)
            THEN UPDATE
                    SET IsDeleted            = 0
                      , Description          = src.Description
                      , Active               = src.Active
                      , BrLoadActive         = src.BrLoadActive
                      , CodeType             = src.CodeType
                      , Acute                = src.Acute
                      , auditModifiedBy      = @ActorLogin
                      , auditModifiedDateUtc = @NowUtc
            WHEN NOT MATCHED BY TARGET
            THEN INSERT (ActivityLocation, Code, Description, Active, BrLoadActive, CodeType, Acute)
                 VALUES (src.ActivityLocation, src.Code, src.Description, src.Active
                       , src.BrLoadActive, src.CodeType, src.Acute);

            SET @Written = @@ROWCOUNT;

            IF @Mode = N'Full'
            BEGIN
                UPDATE r
                   SET IsDeleted            = 1
                     , auditDeletedBy       = @ActorLogin
                     , auditDeletedDateUtc  = @NowUtc
                     , auditModifiedBy      = @ActorLogin
                     , auditModifiedDateUtc = @NowUtc
                  FROM dbo.LookupWasteCode AS r
                  JOIN @Location AS l ON l.ActivityLocation = r.ActivityLocation
                 WHERE r.IsDeleted = 0
                   AND NOT EXISTS (SELECT 1 FROM @Element AS e
                                    WHERE CAST (e.ActivityLocation AS NVARCHAR (2)) = r.ActivityLocation
                                      AND CAST (e.Code             AS NVARCHAR (6)) = r.Code);

                SET @Retired = @@ROWCOUNT;
            END;
        END
        ELSE
        BEGIN
            -- UNREACHABLE, AND WORTH THE SIX LINES ANYWAY. @LookupName has already been validated
            -- against the dispatch in section 1, so getting here means the dispatch and the branch
            -- list have drifted apart -- a name added to one and not the other. Without this arm the
            -- call would COMMIT having written nothing, report success, and leave the operator
            -- believing a code list had been refreshed. build/check_lookup_coverage.py exists to catch
            -- that drift before deployment; this catches it at run time if the gate is ever skipped.
            SET @Failure = CONCAT (N'@LookupName ''', @LookupName, N''' passed validation but no '
                                 , N'branch handled it, so nothing was written. The @CodeWidth '
                                 , N'dispatch and the branch list in this procedure have drifted '
                                 , N'apart. Run build/check_lookup_coverage.py.');
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 6. The one check that CANNOT run before the transaction, because its input is the outcome.
        --    A real Full refresh retires a handful of codes out of a list of hundreds; a truncated or
        --    partially-paged response retires most of the list. @Retired > @ElementCount separates
        --    those without a tuning knob. The observation is recorded FIRST, into the table variable,
        --    so that it survives the rollback the THROW causes and the refusal explains itself in
        --    logs.DataQualityObservation rather than only in the error text.
        -- -----------------------------------------------------------------------------------------
        IF @Mode = N'Full' AND @Retired > @ElementCount
        BEGIN
            INSERT INTO @Observation (ObservationType, Severity, ObservedValue, Detail)
            SELECT N'LookupRefreshRetiredTooMany'
                 , N'Error'
                 , LEFT (CONCAT (@Retired, N' retired vs ', @ElementCount, N' supplied'), 400)
                 , LEFT (CONCAT (N'A Full refresh of the ', @LookupName, N' list would have retired '
                               , @Retired, N' codes while carrying only ', @ElementCount
                               , N'. A complete list does not shrink by more than its own size, so '
                               , N'this is a truncated or partially-paged API response rather than a '
                               , N'retirement EPA made. The call was refused and rolled back, so the '
                               , N'mirror is unchanged. Verify the response against the API; if the '
                               , N'additions are real and the retirements are not, apply them with '
                               , N'@Mode = ''Upsert'', which retires nothing.'), 4000);

            SET @Failure = CONCAT (N'Refused: a Full refresh of the ', @LookupName, N' list would '
                                 , N'retire ', @Retired, N' codes while carrying only ', @ElementCount
                                 , N'. That is a truncated response, not a retirement. Nothing was '
                                 , N'changed. See logs.DataQualityObservation for LoadRunId '
                                 , @LoadRunId, N', and use @Mode = ''Upsert'' to apply additions '
                                 , N'without retirements.');
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 7. Retirement is worth noticing even when it is legitimate. A code leaving one of EPA's
        --    lists is a regulatory change, and the handler rows that already reference it keep
        --    referencing it -- which is why these rows are soft-deleted and why a historical read of
        --    a lookup table must not filter IsDeleted = 0. Info rather than Warning: nothing is
        --    wrong, but somebody should be able to find out later that it happened.
        -- -----------------------------------------------------------------------------------------
        IF @Retired > 0 OR @ChildRetired > 0
        BEGIN
            INSERT INTO @Observation (ObservationType, Severity, ObservedValue, Detail)
            SELECT N'LookupCodesRetired'
                 , N'Info'
                 , LEFT (CONCAT (@Retired, N' code(s)'
                               , CASE WHEN @ChildRetired > 0
                                      THEN CONCAT (N', ', @ChildRetired, N' county row(s)')
                                      ELSE N'' END), 400)
                 , LEFT (CONCAT (N'EPA no longer publishes ', @Retired, N' code(s) that this mirror '
                               , N'held for the ', @LookupName, N' list, so they were retired '
                               , N'(IsDeleted = 1) rather than removed. They still resolve, and must: '
                               , N'a handler version submitted while one of them was current is still '
                               , N'correctly described by it, so a historical read of this table does '
                               , N'NOT filter IsDeleted = 0. Retired is not the same as Active = 0 -- '
                               , N'a code with Active = 0 is still published by EPA and is still in '
                               , N'this list.'
                               , CASE WHEN @ChildRetired > 0
                                      THEN CONCAT (N' ', @ChildRetired, N' dbo.'
                                                 , N'LookupStateDistrictCounty row(s) were retired '
                                                 , N'with them, because a live county under a retired '
                                                 , N'district would read as current.')
                                      ELSE N'' END), 4000);
        END;

        IF EXISTS (SELECT 1 FROM @Observation)
        BEGIN
            INSERT INTO logs.DataQualityObservation
                  (LoadRunId, ObservationType, Severity, TableName, LookupName, ObservedValue, Detail
                 , ObservedDateUtc)
            SELECT @LoadRunId
                 , o.ObservationType
                 , o.Severity
                 , CONCAT (N'dbo.Lookup', @LookupName)
                 , @LookupName
                 , o.ObservedValue
                 , o.Detail
                 , @NowUtc
              FROM @Observation AS o
             ORDER BY o.Ordinal;

            SET @ObservationsWritten = @@ROWCOUNT;
        END;

        SET @RowsAffected = @Written;
        SET @RetiredRows  = @Retired;
        SET @ChildRows    = @ChildWritten + @ChildRetired;

        SET @Comments = CONCAT (N'lookup=', @LookupName, N', mode=', @Mode
                              , N', elements=', @ElementCount
                              , N', rowsWritten=', @Written
                              , N', retired=', @Retired
                              , N', counties=', @CountyCount
                              , N', childWritten=', @ChildWritten
                              , N', childRetired=', @ChildRetired
                              , N', observations='
                              , COALESCE (CAST (@ObservationsWritten AS NVARCHAR (11)), N'0'));

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
            SET @Committed = 1;
        END;

        -- Completion. Deliberately after the COMMIT; see the template header for what that costs. Every
        -- write above is a no-op the second time -- the MATCHED guards and the IsDeleted = 0 predicates
        -- are what make that true -- so the retry this can provoke is safe.
        SET @EndTimeUtc = SYSUTCDATETIME ();

        IF @ExecutionId IS NOT NULL
        BEGIN
            UPDATE logs.ExecutionLog
               SET EndDateUtc           = @EndTimeUtc
                 , ElapsedMilliseconds  = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, @StartTimeUtc, @EndTimeUtc)
                                                     , CAST (2147483647 AS BIGINT)) AS INT)
                 , Successful           = 1
                 , KeyParameters        = @KeyParameters
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
        -- both need the same unqualified rollback.
        IF XACT_STATE () <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        -- G35. The rollback destroyed the observations, and one of them may BE the reason for the
        -- failure -- the section 6 refusal throws on purpose and its explanation is in here. Put them
        -- back outside the transaction. @Committed guards against a double flush when the failure was
        -- in the post-COMMIT completion UPDATE and the rows are already in the table.
        IF @Committed = 0 AND EXISTS (SELECT 1 FROM @Observation)
        BEGIN
            -- Swallowing, and only here: a failure to record the finding must not replace the error
            -- the caller is about to receive.
            BEGIN TRY
                INSERT INTO logs.DataQualityObservation
                      (LoadRunId, ObservationType, Severity, TableName, LookupName, ObservedValue
                     , Detail, ObservedDateUtc)
                SELECT @LoadRunId
                     , o.ObservationType
                     , o.Severity
                     , CONCAT (N'dbo.Lookup', @LookupName)
                     , @LookupName
                     , o.ObservedValue
                     , LEFT (CONCAT (o.Detail
                                   , N' NOTE: this refresh FAILED and rolled back, so no code was '
                                   , N'inserted, corrected or retired and the list is exactly as it '
                                   , N'was. The finding itself stands; the change did not happen. '
                                   , N'See logs.ExecutionLog for the error.'), 4000)
                     , @NowUtc
                  FROM @Observation AS o
                 ORDER BY o.Ordinal;

                SET @ObservationsWritten = @@ROWCOUNT;
            END TRY
            BEGIN CATCH
                SET @ObservationsWritten = NULL;
            END CATCH;
        END;

        -- A caller must not be able to log a rolled-back refresh as work done. Zeroed rather than left
        -- at whatever the failed branch had reached.
        IF @Committed = 0
        BEGIN
            SET @RowsAffected = 0;
            SET @RetiredRows  = 0;
            SET @ChildRows    = 0;
        END;

        SET @ContextMessage = CONCAT (N'lookup=', COALESCE (@LookupName, N'(null)')
                                    , N', mode=', COALESCE (@Mode, N'(null)')
                                    , N', elements=', @ElementCount
                                    , N', observationsPreserved='
                                    , COALESCE (CAST (@ObservationsWritten AS NVARCHAR (11)), N'none')
                                    , CASE WHEN @Committed = 1
                                           THEN N', the refresh COMMITTED and the failure is in the '
                                              + N'completion update; the changes ARE applied.'
                                           ELSE N', nothing was applied (rolled back).' END);

        -- The rollback may have destroyed the row logs.uspStartExecutionLogging wrote, and only when
        -- this procedure was called inside a transaction that was ALREADY open. Put it back with the
        -- ORIGINAL @StartTimeUtc, or the only executions never recorded would be the failures.
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
        -- loader could no longer tell a deadlock (1205, retry) from a refused list.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspRefreshLookupSet'
    , @Description = N'Refreshes one mirrored EPA code list from a JSON @Elements array, a whole list at a time. G15 mirrors all 24 /lookup/hd/* endpoints, which are 23 response definitions and 24 tables -- StateDistrict carries a nested counties array that became dbo.LookupStateDistrictCounty -- and @LookupName selects one of 23 branches. Two modes: Full means @Elements is EPA''s COMPLETE list and codes absent from it are RETIRED; Upsert means @Elements is a PART of the list and nothing is retired. @Mode has no default, because the natural default would be Full and a caller passing one page of a paged list under it would retire the whole remainder of the list. Three states are kept distinct and conflating any two loses real information: Active = 0 means EPA still publishes the code and says not to offer it for new submissions, IsDeleted = 1 means EPA no longer publishes it at all, and absent means never mirrored. Retirement is a soft delete because a retired code still has to RESOLVE -- a code EPA retires this year is still correct for a handler version submitted while it was current, so a historical read of a lookup table does NOT filter IsDeleted = 0, the one documented exception to this database''s blanket read rule, and this is the procedure that creates the rows it applies to. Full mode with an empty array is REFUSED: taken literally it means EPA publishes no codes at all for the list, which has never been true of any of the 24, and what it actually means is a fetch that returned nothing. A Full refresh that would retire more codes than the payload contains is also refused, and that check necessarily runs after the write because its input is the outcome; it throws, the transaction rolls it back, and the observation recorded alongside it survives the rollback (G35) so the refusal explains itself in logs.DataQualityObservation. Retirement is scoped to the ActivityLocations the payload mentions, so a refresh carrying only MD cannot empty another jurisdiction''s codes. Retiring a district retires its counties, because there is no view over that child and a live county under a retired district would read as current. Every MERGE matches UNFILTERED and takes HOLDLOCK, for the reasons scripts 400 and 520 give: a filtered match would insert a duplicate of a retired key that the filtered unique index cannot reject, and without the range lock two concurrent calls can both insert. Every MATCHED arm is guarded by IS DISTINCT FROM on every column it writes, so a second run with the same payload writes nothing and does not move auditModifiedDateUtc -- a refresh that touched every row nightly would make that column mean "the loader ran" rather than "this code changed". JSON booleans are MAPPED rather than cast: JSON_VALUE returns the text ''true'', and TRY_CAST of that to BIT is NULL, so the obvious spelling would silently blank every Active, IndustryApp, BrLoadActive and Acute value across all 24 tables. Values are shredded wide with JSON_VALUE and width-checked before any write, because OPENJSON ... WITH truncates silently (G36) and a truncated code is a DIFFERENT code. Seven of the 23 lists have no ActivityLocation and the natural-key, duplicate and retirement logic reads that from the same @LookupName dispatch that supplies the Code width. The run must exist, be live, and still be Running. @Elements is excluded from @KeyParameters by name. build/check_lookup_coverage.py derives the lookup tables and their columns from sys.columns and fails if this procedure does not handle every one; the branch list is hand-written because script 050 denies the loader metadata visibility, so nothing here can walk the catalogue at run time. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. The loader refreshes the code lists; the monitoring web app reads them and must never be
-- able to retire a code it is displaying.
--
-- Ownership chaining carries the writes to all 24 dbo.Lookup* tables, the read of logs.LoadRun, the
-- inserts into logs.DataQualityObservation, and the chained inserts into logs.ExecutionLog through
-- this single grant, so the loader login holds no direct permission on any of them (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspRefreshLookupSet TO RCRAInfoLoaderRole;
END;
GO

PRINT N'523: dbo.uspRefreshLookupSet created or altered, EXECUTE granted to the loader role.';
GO
