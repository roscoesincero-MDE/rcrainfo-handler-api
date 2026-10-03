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
ObjectName:   logs.uspUpsertHandlerLoadStatusSet
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Records per-source-record load status for one run, a set at a time. This is the procedure that puts the AR5 rows on the
board and moves them, and dbo.uspMergeHandlerSourceBatch is the procedure that closes them: script 400 UPDATEs status
rows and never INSERTs any, because "they exist as 'Pending' from the moment the run enumerated its work". This is what
makes that sentence true.

Four modes, one per moment in the loader's night:

    Enumerate   EPA's index has been read and the run now knows which source records it intends to fetch. Inserts a
                Pending row per element, revives one that retention soft-deleted, and leaves a row that has already
                progressed exactly as it is.
    Attempt     a fetch is about to be made. Sets AttemptCount from the element's attemptNumber, stamps
                LastAttemptStartedDateUtc, moves the row to InProgress.
    Fail        the fetch failed at the API, so no payload ever reached dbo.uspMergeHandlerSourceBatch. Records the HTTP
                and RCRAInfo error detail and moves the row to Failed.
    Skip        the run decided not to fetch this record at all. Moves the row to Skipped.

There is no Succeed mode, and that is not an omission. A row is only Succeeded if a version of the handler committed,
which only script 400 can know, and only inside the transaction that committed it.

========================================================================================================================
Requirements and Key Dependencies:

logs.HandlerLoadStatus (script 310), logs.LoadRun (script 300).

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, copied from
.claude/skills/sql-objects/templates/procedure.sql.

EXECUTE is granted to RCRAInfoLoaderRole only. The monitoring web app reads these rows and never writes them.

========================================================================================================================
Notes:

EVERY MODE IS IDEMPOTENT, INCLUDING Attempt, AND THAT COST A DESIGN DECISION. The obvious way to count attempts is
AttemptCount = AttemptCount + 1, and it is wrong here. The AR8 completion UPDATE runs after the COMMIT, so a failure in
it reports a committed call as failed and the caller may retry -- which the rest of this database survives only because
every write in it is a no-op the second time. An incrementing counter is not, so a single retry would leave the row
claiming an attempt that never happened, in the one column an operator uses to decide whether a handler is stuck. So
the element carries attemptNumber and this procedure SETS AttemptCount to it. The loader is the thing doing the
retrying; it already knows which attempt it is on, and moving that knowledge into the payload is what keeps the write
convergent. Enumerate, Fail and Skip are naturally idempotent for the same reason 400's merge is: nothing is written
that is not distinct from what is there.

RE-ENUMERATING A RUN MUST NOT UNDO ITS PROGRESS. Enumerate is the one mode that can be called twice with the same
elements for real reasons -- a resumed run, a second page of an index that overlaps the first. So its MATCHED arm
touches a live row only when ActivityLocation actually differs; it does not reset Status, AttemptCount, or any of the
outcome columns 400 owns. Reviving a soft-deleted row does reset Status to Pending, because a row retention removed and
this run has enumerated again is genuinely at the start of its life.

THE MERGE MATCH IS UNFILTERED, AND MUST BE, for the reason script 400's header gives about dbo.HandlerSource:
UX_logs_HandlerLoadStatus_Natural excludes soft-deleted rows, so matching through it would report a soft-deleted row as
NOT MATCHED and insert a second row with the same natural key -- two rows the filtered index cannot reject.
IX_logs_HandlerLoadStatus_LoadRunId exists unfiltered for exactly this kind of seek.

HOLDLOCK IS NOT OPTIONAL. Without it MERGE releases its read locks before writing, so two calls carrying the same
element can both evaluate NOT MATCHED and both insert, and the natural-key index then rejects one of them on an
unattended overnight load. The join is seekable, so the range locks are on the keys in the batch rather than the table.

A KEY WIDER THAN ITS COLUMN IS THE ONE MISTAKE HERE THAT CORRUPTS RATHER THAN LOSES. OPENJSON ... WITH truncates an
over-wide string silently (G36), and a truncated HandlerId is not a missing handler -- it is a DIFFERENT handler, whose
row would be marked Failed on this one's behalf. So the shred is deliberately wide, into NVARCHAR (4000), and the
widths are checked against the column definitions before anything is written. The same reasoning is why the CASTs in
the write statements are on the @Element side: comparing raw NVARCHAR (4000) against NVARCHAR (12) makes the engine
widen the COLUMN and turns every seek into a scan.

A Fail ELEMENT MUST SAY SOMETHING ABOUT WHY. At least one of httpStatusCode, apiErrorCode or apiErrorMessage is
required, because a Failed row carrying none of the three tells the operator only that something went wrong -- which is
already visible from Status -- and AR5 exists to answer the next question. httpStatusCode is bounded to 100-599 so that
a transport-level failure with no response does not arrive as 0 and read as a real status code; leave it out instead.

Succeeded ROWS ARE EXCLUDED FROM Attempt, Fail AND Skip, and the count of exclusions is reported rather than swallowed.
A Succeeded row names a committed version in HandlerSourceId, and overwriting it from a later call in the same run
would make the status lie about data that is present -- the same rule script 400's failure flush follows. The exclusion
is a symptom, though: it means the loader tried to act on work it had already finished, so the number lands in Comments
under skippedSucceeded= where the monitoring app can see it.

EMPTY ELEMENTS IS NOT AN ERROR. A run with nothing to enumerate is a real outcome -- EPA had no changes in the window
-- and turning a harmless no-op into a failed run would make an unattended load report a problem it does not have. The
count goes into Comments.

THE RUN MUST BE Running. Recording work against a run that has already reported Succeeded or Failed means the loader
lost track of its own lifecycle, and the row would then contradict the run summary the monitoring app displays. That is
worth stopping for rather than absorbing. A soft-deleted or non-existent run is refused for the same reason.

G35 DOES NOT APPLY TO THIS PROCEDURE, which is worth saying because every other writer in this database carries it. The
table-variable accumulator exists so that a doomed transaction still records WHICH handlers a batch failed on. Here the
status rows ARE the work; if this procedure rolls back, there is nothing to preserve, because nothing was ever written.
The failure is recorded in logs.ExecutionLog like any other and the loader retries.

WHAT @KeyParameters MAY CONTAIN. Identifiers and counts. @Elements is excluded BY NAME -- it names regulated entities
and can carry thousands of them, and logs.ExecutionLog has a different read audience and a different retention policy
from this table. So is apiErrorMessage, which is EPA's text and is already stored in the column built for it. From
MDE's own template: do NOT include parameters such as passwords and Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- 1. The run has read EPA's index and knows its work.
DECLARE @Work NVARCHAR (MAX) = N'[{"handlerId":"MD0000123456","activityLocation":"MD","sourceType":"N","sequence":1}
                                 ,{"handlerId":"MD0000987654","activityLocation":"MD","sourceType":"N","sequence":1}]';
DECLARE @Rows INT;
EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = 1, @Mode = N'Enumerate', @Elements = @Work
                                      , @RowsAffected = @Rows OUTPUT;

-- 2. About to fetch the first one, for the second time.
EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = 1, @Mode = N'Attempt'
   , @Elements = N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":2}]';

-- 3. EPA returned 503, so no payload ever reached the merge.
EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = 1, @Mode = N'Fail'
   , @Elements = N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"httpStatusCode":503
                    ,"apiErrorCode":"E_SERVICE_UNAVAILABLE","apiErrorMessage":"Service temporarily unavailable"}]';

-- 4. Out of scope for this run.
EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = 1, @Mode = N'Skip'
   , @Elements = N'[{"handlerId":"MD0000987654","sourceType":"N","sequence":1}]';

Enumerate seeks IX_logs_HandlerLoadStatus_LoadRunId under HOLDLOCK; the other three seek it and join the shredded set.
Cost scales with the element count, and the payload is parsed once. Instrumentation adds one singleton insert per call
and one singleton update on the successful path.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4. Named by MDE as the next procedure to
											build, because script 400 already depends on the rows it creates.
2026-09-05	rsincero						@Elements validation now requires ISJSON (@Elements, ARRAY), not just
											ISJSON. Found by the DA5 round-trip tests: a bare JSON object passed the
											old gate and surfaced as error 245 from an internal CAST rather than as
											this procedure's own refusal. Scripts 521, 522 and 523 carried the same
											omission and were corrected together; 400 was already correct.
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspUpsertHandlerLoadStatusSet
      @LoadRunId    INT
    , @Mode         NVARCHAR (20)
    , @Elements     NVARCHAR (MAX)
    , @RowsAffected INT            = NULL OUTPUT
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
                                                    , N'[logs].[uspUpsertHandlerLoadStatusSet]')
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

    DECLARE @NowUtc           DATETIME2       = SYSUTCDATETIME ()
          , @ElementCount     INT             = 0
          , @Written          INT             = 0
          , @SkippedSucceeded INT             = 0
          , @RunStatus        NVARCHAR (20)   = NULL
          , @Failure          NVARCHAR (2048) = NULL;

    -- Identifiers and counts only. @Elements is excluded BY NAME and so is apiErrorMessage; see the
    -- header. The element count is added after the shred, because that is when it is known.
    SET @KeyParameters = CONCAT (N'LoadRunId=', COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(null)')
                               , N', Mode=',    COALESCE (@Mode, N'(null)'));

    -- The shred target is WIDE on purpose. OPENJSON ... WITH truncates silently (G36), and a truncated
    -- HandlerId names a DIFFERENT handler rather than no handler -- see the header. Widths are checked
    -- against logs.HandlerLoadStatus below, before anything is written.
    DECLARE @Element TABLE
    (
        Ordinal          INT             NOT NULL PRIMARY KEY,
        HandlerId        NVARCHAR (4000)     NULL,
        ActivityLocation NVARCHAR (4000)     NULL,
        SourceType       NVARCHAR (4000)     NULL,
        Sequence         INT                 NULL,
        AttemptNumber    INT                 NULL,
        HttpStatusCode   INT                 NULL,
        ApiErrorCode     NVARCHAR (4000)     NULL,
        ApiErrorMessage  NVARCHAR (MAX)      NULL,
        ApiErrorId       NVARCHAR (4000)     NULL,
        ApiErrorDate     DATETIME2           NULL
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
        -- 1. Validation, all of it before BEGIN TRANSACTION so a rejected call has no transaction to
        --    unwind and is recorded as a failed execution like any other.
        -- -----------------------------------------------------------------------------------------
        -- Assigned first, then thrown. THROW's message argument accepts a literal or a variable and
        -- NOT an expression, so a concatenation written inline is a syntax error -- caught here on
        -- the first deployment of this script rather than at 02:00 on the branch that raises it.
        IF @LoadRunId IS NULL
        BEGIN
            SET @Failure = N'@LoadRunId is required. A status row that names no run cannot be shown '
                         + N'against one, and logs.HandlerLoadStatus.LoadRunId is NOT NULL.';
            THROW 50000, @Failure, 1;
        END;

        IF @Mode IS NULL OR @Mode NOT IN (N'Enumerate', N'Attempt', N'Fail', N'Skip')
        BEGIN
            SET @Failure = CONCAT (N'@Mode must be one of ''Enumerate'', ''Attempt'', ''Fail'' or '
                                 , N'''Skip''. It was '
                                 , COALESCE (N'''' + @Mode + N'''', N'NULL')
                                 , N'. There is deliberately no ''Succeed'': a row is only Succeeded '
                                 , N'if a version of the handler committed, which only '
                                 , N'dbo.uspMergeHandlerSourceBatch can know, and only inside the '
                                 , N'transaction that committed it.');
            THROW 50000, @Failure, 1;
        END;

        -- ARRAY, not just ISJSON. Measured 2026-09-05 by the DA5 round-trip tests: without the type
        -- constraint a caller who sends one element as a bare OBJECT instead of a one-element array
        -- passes this gate, and OPENJSON then enumerates the object's PROPERTIES instead of its
        -- elements. What comes back is error 245, "Conversion failed when converting the nvarchar
        -- value 'handlerId' to data type int", from the ordinal CAST below -- an engine error that
        -- names an internal column, is not 50000, and so is not classified as a refusal by anything
        -- branching on the number. Worse, it is luck: the 245 only happens because this procedure
        -- casts the OPENJSON key. A payload procedure whose columns were all strings would shred the
        -- object to zero rows and report success. Script 400 has had the ARRAY constraint since it
        -- was written and records the same reasoning; these four siblings copied the check without
        -- it. ISJSON's type argument is SQL Server 2022 and therefore in scope for the target.
        IF @Elements IS NULL OR ISJSON (@Elements, ARRAY) = 0
        BEGIN
            SET @Failure = N'@Elements is NULL, is not valid JSON, or is not a JSON array. It must be '
                         + N'an array of objects carrying at least handlerId, sourceType and sequence, '
                         + N'even for a single element. An empty array [] is accepted and does nothing.';
            THROW 50000, @Failure, 1;
        END;

        -- The run has to exist, be live, and still be Running. See the header: recording work
        -- against a closed run makes the status rows contradict the run summary the monitor shows.
        SELECT @RunStatus = r.Status
          FROM logs.LoadRun AS r
         WHERE r.LoadRunId = @LoadRunId
           AND r.IsDeleted = 0;

        IF @RunStatus IS NULL
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' does not exist, or has been '
                                 , N'soft-deleted. Open a run with logs.uspStartLoadRun and record '
                                 , N'status against the LoadRunId it returns.');
            THROW 50000, @Failure, 1;
        END;

        IF @RunStatus <> N'Running'
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' has Status ''', @RunStatus
                                 , N''', so it is closed and no further work can be recorded against '
                                 , N'it. The loader has lost track of its own lifecycle: either this '
                                 , N'call belongs to a newer run, or logs.uspCompleteLoadRun was '
                                 , N'called too early. Absorbing it would leave status rows that '
                                 , N'contradict the run summary the monitoring app displays.');
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 2. Shred. JSON_VALUE rather than OPENJSON ... WITH, so nothing is truncated on the way in.
        -- -----------------------------------------------------------------------------------------
        INSERT INTO @Element (Ordinal, HandlerId, ActivityLocation, SourceType, Sequence
                            , AttemptNumber, HttpStatusCode, ApiErrorCode, ApiErrorMessage
                            , ApiErrorId, ApiErrorDate)
        SELECT CAST (e.[key] AS INT) + 1
             , JSON_VALUE (e.value, '$.handlerId')
             , JSON_VALUE (e.value, '$.activityLocation')
             , JSON_VALUE (e.value, '$.sourceType')
             , TRY_CAST (JSON_VALUE (e.value, '$.sequence')       AS INT)
             , TRY_CAST (JSON_VALUE (e.value, '$.attemptNumber')  AS INT)
             , TRY_CAST (JSON_VALUE (e.value, '$.httpStatusCode') AS INT)
             , JSON_VALUE (e.value, '$.apiErrorCode')
             -- JSON_VALUE returns NULL, in lax mode, for a value longer than 4000 characters rather
             -- than clipping it. That is the right failure for this column: it holds NVARCHAR (4000),
             -- so anything longer could not be stored anyway, and losing the text is a loss where a
             -- clip would be a lie. If such a message were the element's ONLY detail the set is
             -- refused by the Fail check below, which tells the operator to send a code as well.
             , JSON_VALUE (e.value, '$.apiErrorMessage')
             , JSON_VALUE (e.value, '$.apiErrorId')
             , TRY_CAST (JSON_VALUE (e.value, '$.apiErrorDate')   AS DATETIME2)
          FROM OPENJSON (@Elements) AS e;

        SET @ElementCount = @@ROWCOUNT;
        SET @KeyParameters = CONCAT (@KeyParameters, N', Elements=', @ElementCount);

        -- -----------------------------------------------------------------------------------------
        -- 3. The natural key, complete and within its widths. Both checks name the offending
        --    element, because "the batch was rejected" is not actionable at 02:00.
        -- -----------------------------------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM @Element
                    WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' is missing part of the natural key (handlerId, sourceType, '
                                    , N'sequence). Those three columns plus LoadRunId identify the '
                                    , N'row and none of them can be defaulted, so the set is '
                                    , N'rejected whole rather than partly applied.')
              FROM @Element
             WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL;
            THROW 50000, @Failure, 1;
        END;

        -- Enumerate is the only mode that INSERTs, so it is the only one that needs a value for a
        -- NOT NULL column it would otherwise have to default. The others ignore activityLocation
        -- entirely -- the row already has one, and this is not the procedure that corrects it.
        IF @Mode = N'Enumerate'
           AND EXISTS (SELECT 1 FROM @Element WHERE ActivityLocation IS NULL)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has no activityLocation, and Enumerate is the mode that '
                                    , N'INSERTs, so there is nothing to default a NOT NULL column '
                                    , N'from. MDE''s scope decision is MD only for a handler''s own '
                                    , N'activityLocation (G2), but the value is required rather '
                                    , N'than assumed -- a silent ''MD'' would hide a loader reading '
                                    , N'the wrong feed.')
              FROM @Element
             WHERE ActivityLocation IS NULL;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                    WHERE LEN (HandlerId)                  > 12
                       OR LEN (SourceType)                 > 1
                       OR LEN (ActivityLocation)           > 2
                       OR LEN (ApiErrorCode)               > 100
                       OR LEN (ApiErrorMessage)            > 4000
                       OR LEN (ApiErrorId)                 > 50)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' carries a value wider than the column that stores it '
                                    , N'(handlerId 12, sourceType 1, activityLocation 2, '
                                    , N'apiErrorCode 100, apiErrorMessage 4000, apiErrorId 50). '
                                    , N'This is checked rather than truncated because a truncated '
                                    , N'handlerId is not a missing handler -- it is a different '
                                    , N'one, whose row would be marked on this element''s behalf.')
              FROM @Element
             WHERE LEN (HandlerId)        > 12   OR LEN (SourceType)      > 1
                OR LEN (ActivityLocation) > 2    OR LEN (ApiErrorCode)    > 100
                OR LEN (ApiErrorMessage)  > 4000 OR LEN (ApiErrorId)      > 50;
            THROW 50000, @Failure, 1;
        END;

        -- A duplicate key inside the set would raise error 8672 from inside the MERGE, whose message
        -- names neither the key nor the element. Checked here so the message is useful.
        IF EXISTS (SELECT 1 FROM @Element
                   GROUP BY HandlerId, SourceType, Sequence
                   HAVING COUNT (*) > 1)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'The set names handler ', HandlerId, N' / sourceType '
                                            , SourceType, N' / sequence ', Sequence, N' ', COUNT (*)
                                            , N' times. One element per source record: two rows '
                                            , N'targeting the same key would make the outcome depend '
                                            , N'on which one the engine applied last.')
              FROM @Element
             GROUP BY HandlerId, SourceType, Sequence
            HAVING COUNT (*) > 1
             ORDER BY HandlerId, SourceType, Sequence;
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 4. Mode-specific validation.
        -- -----------------------------------------------------------------------------------------
        IF @Mode = N'Attempt'
           AND EXISTS (SELECT 1 FROM @Element WHERE AttemptNumber IS NULL OR AttemptNumber < 1)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has no usable attemptNumber. Attempt mode SETS '
                                    , N'AttemptCount rather than incrementing it, so that a retry '
                                    , N'of this call cannot inflate the count an operator uses to '
                                    , N'decide whether a handler is stuck. The loader is the thing '
                                    , N'retrying and already knows the number; the first attempt '
                                    , N'is 1.')
              FROM @Element
             WHERE AttemptNumber IS NULL OR AttemptNumber < 1;
            THROW 50000, @Failure, 1;
        END;

        IF @Mode = N'Fail'
           AND EXISTS (SELECT 1 FROM @Element
                        WHERE HttpStatusCode IS NULL
                          AND ApiErrorCode    IS NULL
                          AND ApiErrorMessage IS NULL)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' is a Fail carrying none of httpStatusCode, apiErrorCode or '
                                    , N'apiErrorMessage. At least one is required: a Failed row with '
                                    , N'no detail tells the operator only that something went wrong, '
                                    , N'which Status already said, and answering the next question '
                                    , N'is what AR5 exists for.')
              FROM @Element
             WHERE HttpStatusCode IS NULL AND ApiErrorCode IS NULL AND ApiErrorMessage IS NULL;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                    WHERE HttpStatusCode IS NOT NULL
                      AND HttpStatusCode NOT BETWEEN 100 AND 599)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has httpStatusCode ', MIN (HttpStatusCode), N', which is '
                                    , N'not a status code (100-599). A transport failure with no '
                                    , N'response has no status code -- leave the property out '
                                    , N'rather than sending 0, which reads as a real code in the '
                                    , N'monitoring grid.')
              FROM @Element
             WHERE HttpStatusCode IS NOT NULL AND HttpStatusCode NOT BETWEEN 100 AND 599;
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 5. Write.
        -- -----------------------------------------------------------------------------------------
        BEGIN TRANSACTION;

        IF @Mode = N'Enumerate'
        BEGIN
            -- Unfiltered match under HOLDLOCK. Both are load-bearing; see the header.
            MERGE logs.HandlerLoadStatus WITH (HOLDLOCK) AS tgt
            USING (SELECT CAST (e.HandlerId        AS NVARCHAR (12)) AS HandlerId
                        , CAST (e.ActivityLocation AS NVARCHAR (2))  AS ActivityLocation
                        , CAST (e.SourceType       AS NVARCHAR (1))  AS SourceType
                        , e.Sequence
                     FROM @Element AS e) AS src
               ON tgt.LoadRunId  = @LoadRunId
              AND tgt.HandlerId  = src.HandlerId
              AND tgt.SourceType = src.SourceType
              AND tgt.Sequence   = src.Sequence

            -- ONE MATCHED ARM, NOT TWO, AND THE CASE EXPRESSIONS ARE WHY. MERGE permits only a
            -- single WHEN MATCHED ... THEN UPDATE clause -- a second one is error 10714, "an action
            -- of type 'WHEN MATCHED' cannot appear more than once in a 'UPDATE' clause" -- so the
            -- two situations this arm handles are separated by CASE on tgt.IsDeleted instead:
            --
            --   revived (IsDeleted = 1)  a row retention soft-deleted and this run has enumerated
            --                            again really is at the start of its life, so Status goes
            --                            back to Pending and the attempt history clears.
            --   live    (IsDeleted = 0)  the row keeps everything it has earned. Only
            --                            ActivityLocation is corrected. Every other column reads
            --                            tgt.<column> back into itself, which is a no-op.
            --
            -- The guard is what keeps re-enumeration convergent: a live row whose ActivityLocation
            -- already agrees is not matched at all, so auditModifiedDateUtc does not move for a row
            -- nothing changed about.
            WHEN MATCHED AND (tgt.IsDeleted = 1
                          OR  tgt.ActivityLocation IS DISTINCT FROM src.ActivityLocation)
            THEN UPDATE
                    SET IsDeleted                 = 0
                      , ActivityLocation          = src.ActivityLocation
                      , Status                    = CASE WHEN tgt.IsDeleted = 1 THEN N'Pending'
                                                         ELSE tgt.Status END
                      , Outcome                   = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.Outcome END
                      , AttemptCount              = CASE WHEN tgt.IsDeleted = 1 THEN 0
                                                         ELSE tgt.AttemptCount END
                      , HttpStatusCode            = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.HttpStatusCode END
                      , ApiErrorCode              = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.ApiErrorCode END
                      , ApiErrorMessage           = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.ApiErrorMessage END
                      , ApiErrorId                = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.ApiErrorId END
                      , ApiErrorDate              = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.ApiErrorDate END
                      , HandlerSourceId           = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.HandlerSourceId END
                      , PayloadSha256             = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.PayloadSha256 END
                      , FirstSeenDateUtc          = CASE WHEN tgt.IsDeleted = 1 THEN @NowUtc
                                                         ELSE tgt.FirstSeenDateUtc END
                      , LastAttemptStartedDateUtc = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.LastAttemptStartedDateUtc END
                      , CompletedDateUtc          = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.CompletedDateUtc END
                      , DurationMs                = CASE WHEN tgt.IsDeleted = 1 THEN NULL
                                                         ELSE tgt.DurationMs END
                      -- Not CASEd: whichever situation this was, THIS execution touched the row, and
                      -- ExecutionLogId is how a maintainer gets from the row to what did it.
                      , ExecutionLogId            = @ExecutionId
                      , auditModifiedBy           = ORIGINAL_LOGIN ()
                      , auditModifiedDateUtc      = @NowUtc
                      -- auditDeleted* are deliberately left alone on a revive. They record when the
                      -- row was last retired, which is history and stays true; there is no column
                      -- for "and then it came back", and ExecutionLogId already points at the call.

            WHEN NOT MATCHED BY TARGET
            THEN INSERT (LoadRunId, HandlerId, ActivityLocation, SourceType, Sequence
                       , Status, AttemptCount, FirstSeenDateUtc, ExecutionLogId)
                 VALUES (@LoadRunId, src.HandlerId, src.ActivityLocation, src.SourceType, src.Sequence
                       , N'Pending', 0, @NowUtc, @ExecutionId);
            --
            -- The audit* columns are left to their DEFAULTs on the INSERT arm, per MDE's decision
            -- that the create-audit columns are the table's business. auditModifiedDateUtc is set
            -- EXPLICITLY on both UPDATE arms, because its default fires on INSERT only.

            SET @Written = @@ROWCOUNT;
        -- No semicolon: a terminated IF cannot take an ELSE.
        END
        ELSE IF @Mode = N'Attempt'
        BEGIN
            UPDATE s
               SET Status                    = N'InProgress'
                 , AttemptCount              = e.AttemptNumber
                 , LastAttemptStartedDateUtc = @NowUtc
                 -- Cleared, because they describe the PREVIOUS attempt. Leaving them would show an
                 -- in-progress row carrying the error from the attempt before it.
                 , HttpStatusCode            = NULL
                 , ApiErrorCode              = NULL
                 , ApiErrorMessage           = NULL
                 , ApiErrorId                = NULL
                 , ApiErrorDate              = NULL
                 , CompletedDateUtc          = NULL
                 , ExecutionLogId            = @ExecutionId
                 , auditModifiedBy           = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc      = @NowUtc
              FROM logs.HandlerLoadStatus AS s
              -- The CAST is on the ELEMENT side deliberately: comparing NVARCHAR (4000) against the
              -- column would make the engine widen the COLUMN and scan. The width check has already
              -- passed, so it cannot lose anything.
              JOIN @Element AS e
                ON s.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
               AND s.SourceType = CAST (e.SourceType AS NVARCHAR (1))
               AND s.Sequence   = e.Sequence
             WHERE s.LoadRunId = @LoadRunId
               AND s.IsDeleted = 0
               AND s.Status   <> N'Succeeded';

            SET @Written = @@ROWCOUNT;
        END
        ELSE IF @Mode = N'Fail'
        BEGIN
            UPDATE s
               SET Status               = N'Failed'
                 -- Left NULL on purpose. Outcome describes what was APPLIED to dbo.HandlerSource,
                 -- and a fetch that failed at the API applied nothing. Script 400's failure flush
                 -- makes the same choice for the same reason.
                 , Outcome              = NULL
                 , HttpStatusCode       = e.HttpStatusCode
                 , ApiErrorCode         = CAST (e.ApiErrorCode    AS NVARCHAR (100))
                 , ApiErrorMessage      = CAST (e.ApiErrorMessage AS NVARCHAR (4000))
                 , ApiErrorId           = CAST (e.ApiErrorId      AS NVARCHAR (50))
                 , ApiErrorDate         = e.ApiErrorDate
                 , CompletedDateUtc     = @NowUtc
                 -- The CASE is not defensive padding. LEAST IGNORES NULL arguments rather than
                 -- propagating them, so LEAST (NULL, 2147483647) is 2147483647 -- and a Fail
                 -- recorded without a preceding Attempt would then claim a duration of 24 days.
                 -- DATEDIFF_BIG rather than DATEDIFF for the reason the ceiling exists at all:
                 -- DATEDIFF (MILLISECOND, ...) overflows outright at about 24 days.
                 , DurationMs           = CASE
                                              WHEN s.LastAttemptStartedDateUtc IS NULL THEN NULL
                                              ELSE CAST (LEAST (DATEDIFF_BIG (MILLISECOND
                                                                            , s.LastAttemptStartedDateUtc
                                                                            , @NowUtc)
                                                              , CAST (2147483647 AS BIGINT)) AS INT)
                                          END
                 , ExecutionLogId       = @ExecutionId
                 , auditModifiedBy      = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc = @NowUtc
              FROM logs.HandlerLoadStatus AS s
              JOIN @Element AS e
                ON s.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
               AND s.SourceType = CAST (e.SourceType AS NVARCHAR (1))
               AND s.Sequence   = e.Sequence
             WHERE s.LoadRunId = @LoadRunId
               AND s.IsDeleted = 0
               AND s.Status   <> N'Succeeded';

            SET @Written = @@ROWCOUNT;
        END
        ELSE
        BEGIN
            -- Skip. There is no reason column on this table; a skip's reason belongs to the run, so
            -- it goes in logs.LoadRun.Notes or the ExecutionLog row this call already writes.
            UPDATE s
               SET Status               = N'Skipped'
                 , Outcome              = NULL
                 , CompletedDateUtc     = @NowUtc
                 , ExecutionLogId       = @ExecutionId
                 , auditModifiedBy      = ORIGINAL_LOGIN ()
                 , auditModifiedDateUtc = @NowUtc
              FROM logs.HandlerLoadStatus AS s
              JOIN @Element AS e
                ON s.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
               AND s.SourceType = CAST (e.SourceType AS NVARCHAR (1))
               AND s.Sequence   = e.Sequence
             WHERE s.LoadRunId = @LoadRunId
               AND s.IsDeleted = 0
               AND s.Status   <> N'Succeeded';

            SET @Written = @@ROWCOUNT;
        END;

        -- How many elements this call declined to touch because the row was already Succeeded. A
        -- symptom rather than an error -- the loader acted on work it had finished -- so it is
        -- reported rather than raised. Enumerate is exempt: it never overwrites a live row anyway.
        IF @Mode <> N'Enumerate'
        BEGIN
            SELECT @SkippedSucceeded = COUNT (*)
              FROM logs.HandlerLoadStatus AS s
              JOIN @Element AS e
                ON s.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
               AND s.SourceType = CAST (e.SourceType AS NVARCHAR (1))
               AND s.Sequence   = e.Sequence
             WHERE s.LoadRunId = @LoadRunId
               AND s.IsDeleted = 0
               AND s.Status    = N'Succeeded';
        END;

        SET @RowsAffected = @Written;

        SET @Comments = CONCAT (N'mode=', @Mode, N', elements=', @ElementCount
                              , N', rowsWritten=', @Written
                              , N', skippedSucceeded=', @SkippedSucceeded
                              , N', notFound=', @ElementCount - @Written - @SkippedSucceeded);

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT; see the template header for what that costs,
        -- and the first note above for why every mode here survives the retry it can provoke.
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

        -- Nothing to flush out of @Element. G35's table-variable pattern exists so a doomed
        -- transaction still records WHICH handlers a batch failed on -- but here the status rows ARE
        -- the work, so a rollback leaves nothing to preserve. See the header.
        SET @ContextMessage = CONCAT (N'mode=', COALESCE (@Mode, N'(null)')
                                    , N', elements=', @ElementCount
                                    , N', nothing applied (rolled back).');

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

        -- Bare, so the ORIGINAL error number reaches the caller. RAISERROR would make it 50000 and
        -- the loader could no longer tell a deadlock (1205, retry) from a rejected set.
        ;THROW;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspUpsertHandlerLoadStatusSet'
    , @Description = N'Records per-source-record load status for one run, a set at a time, from a JSON @Elements array. Four modes: Enumerate inserts a Pending row per element (and revives one retention soft-deleted), Attempt sets AttemptCount from the element''s attemptNumber and moves the row to InProgress, Fail records the HTTP and RCRAInfo error detail for a fetch that never produced a payload, and Skip marks a record the run declined to fetch. There is deliberately no Succeed mode: a row is only Succeeded if a version of the handler committed, which only dbo.uspMergeHandlerSourceBatch can know and only inside the transaction that committed it -- script 400 therefore UPDATEs status rows and never INSERTs any, which is what makes this procedure a prerequisite for it. EVERY MODE IS IDEMPOTENT, including Attempt: AttemptCount is SET from the payload rather than incremented, because the AR8 completion UPDATE runs after the COMMIT and can make the caller retry a committed call, and an incrementing counter would then claim an attempt that never happened in the one column an operator uses to judge whether a handler is stuck. Re-enumerating a run does not undo its progress -- a live row keeps its Status, AttemptCount and outcome columns, and only ActivityLocation is corrected, only when it differs. The MERGE match is unfiltered and takes HOLDLOCK, both for the reasons script 400 gives: a filtered match would insert a duplicate of a soft-deleted key, and without the range lock two concurrent calls can both insert. Values are shredded wide with JSON_VALUE and width-checked before any write, because OPENJSON ... WITH truncates silently (G36) and a truncated handlerId names a DIFFERENT handler rather than none. A Fail element must carry at least one of httpStatusCode, apiErrorCode or apiErrorMessage, and httpStatusCode is bounded to 100-599. Succeeded rows are excluded from Attempt, Fail and Skip and the count is reported in Comments as skippedSucceeded. The run must exist, be live, and still be Running. An empty array is a no-op, not an error. @Elements and apiErrorMessage are excluded from @KeyParameters by name. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. The loader writes these rows; the monitoring web app reads them through its own paged
-- read and must never be able to change a status it is displaying.
--
-- Ownership chaining carries the writes to logs.HandlerLoadStatus, the read of logs.LoadRun, and the
-- chained inserts into logs.ExecutionLog through this single grant, so the loader login holds no
-- direct permission on any of the three tables (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspUpsertHandlerLoadStatusSet TO RCRAInfoLoaderRole;
END;
GO

PRINT N'520: logs.uspUpsertHandlerLoadStatusSet created or altered, EXECUTE granted to the loader role.';
GO
