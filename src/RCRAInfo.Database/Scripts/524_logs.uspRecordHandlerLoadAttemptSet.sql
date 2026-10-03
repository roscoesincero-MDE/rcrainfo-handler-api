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
ObjectName:   logs.uspRecordHandlerLoadAttemptSet
Author:       rsincero
CreateDate:   2026-09-06
========================================================================================================================
Description:

Appends rows to logs.HandlerLoadAttempt, a set at a time. This is the ONLY writer of that table, and until this script
existed the table had none: scripts 300-340 built the five AR5 tables in Workstream B3, and the sixteen DA procedures
wrote four of them. The attempt log was left for the workstream that produces attempts, which is D2.

logs.HandlerLoadStatus holds the CURRENT state of one source record within one run and is updated in place;
logs.HandlerLoadAttempt holds the INDIVIDUAL attempts and is only ever inserted into. "Did this handler load?" is
answered by the status row. "Why did it take four tries, and was EPA throttling us or did we time out?" can only be
answered by rows nobody overwrote -- and that is the question asked at 03:00 when the API starts misbehaving, which is
the situation the whole load is designed around.

The loader buffers attempt rows and flushes them as a set, on a row count or a time interval, because writing each
outcome as it happens is row-at-a-time by definition and every write in this database is set-based. The flush interval
IS the resumability granularity of the attempt history: a killed run loses the attempts it had buffered, which is why
the loader flushes unconditionally on both the success and the failure path before the process exits.

========================================================================================================================
Requirements and Key Dependencies:

logs.HandlerLoadAttempt (script 320), logs.HandlerLoadStatus (script 310), logs.LoadRun (script 300).

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, copied from
.claude/skills/sql-objects/templates/procedure.sql.

logs.uspUpsertHandlerLoadStatusSet (script 520) must have run its Enumerate mode for these source records FIRST. This
procedure resolves HandlerLoadStatusId from the natural key and cannot invent one; an element naming a record the run
never enumerated is reported as orphaned rather than written.

EXECUTE is granted to RCRAInfoLoaderRole only. The monitoring web app reads these rows through
logs.uspGetLoadRunSummary and must never be able to write one.

========================================================================================================================
Notes:

THE CALLER DOES NOT KNOW HandlerLoadStatusId, AND SHOULD NOT HAVE TO. The table's grain is
(HandlerLoadStatusId, AttemptNumber), but HandlerLoadStatusId is a surrogate that script 520 assigns and does not
return. The loader knows what EPA told it -- handlerId, sourceType, sequence -- and which attempt it is on. So the
element carries the natural key and this procedure resolves the surrogate by joining logs.HandlerLoadStatus. Pushing
the surrogate out to the loader would mean either a second round trip per flush or a cache of identity values whose
staleness nothing detects.

APPEND-ONLY AND IDEMPOTENT AT THE SAME TIME, WHICH IS WHY THIS IS AN INSERT ... WHERE NOT EXISTS. The AR8 completion
UPDATE runs after the COMMIT, so a failure in it reports a committed call as failed and the loader may retry -- which
the rest of this database survives because every write in it is a no-op the second time. A plain INSERT is not: the
second call would hit UX_logs_HandlerLoadAttempt_Natural and raise 2601 on a flush that had already succeeded. So an
element whose (status row, attemptNumber) is already present is NOT written and NOT updated, and the count is reported
as alreadyRecorded=. Keeping the first row rather than refreshing it is the append-only rule applied to itself: the
first record of an attempt is the record of that attempt, and an UPDATE here would make the table's central property
untrue for the sake of a retry that carries the same values anyway.

THE NOT EXISTS IS UNFILTERED BY IsDeleted, DELIBERATELY, AND IT IS THE OPPOSITE OF SCRIPT 520's MERGE MATCH. 520
matches unfiltered so a soft-deleted row is found and REVIVED; here a soft-deleted attempt row is found and the insert
is DECLINED. The difference is what the two rows mean. A status row is a current state that a re-enumerated run
legitimately restarts. An attempt is a historical event: if retention retired it, the event still happened, and
inserting a second row for the same key would leave the table asserting the attempt twice -- which the filtered unique
index permits, because it excludes the retired row from its own uniqueness. Reviving it is not this procedure's job
either; retention retired it on purpose and a live run has no business undoing that.

UPDLOCK, HOLDLOCK ON THE ANTI-SEMI-JOIN IS NOT OPTIONAL. Without it two calls carrying the same element can both
evaluate NOT EXISTS as true and both insert, and the filtered unique index then rejects one of them on an unattended
overnight load. The subquery is a seek on UX_logs_HandlerLoadAttempt_Natural, so the range locks are on the keys in the
batch rather than on the largest table in the database.

REQUESTPATH IS THE PATH ONLY, AND THIS PROCEDURE IS THE FIRST PLACE THAT RULE IS ENFORCED RATHER THAN WRITTEN DOWN.
Script 320's extended property says it, scripts 500 and 511 name it and both add "this procedure cannot enforce that."
This one can, because it is the writer. A value is accepted only if it starts with '/' and contains none of '?', '#',
'://', '@' or the auth path. The reason is specific and measured: EPA's auth endpoint is
GET /api/v1/auth/{apiId}/{apiKey}, so BOTH HALVES OF THE CREDENTIAL ARE PATH SEGMENTS, and RCRAInfo's data calls carry
a bearer token in a header. A log that captured whole requests would eventually copy an API key into a table the
monitoring web application can read. '@' is on the list for userinfo -- https://id:key@host/... is a URL shape that
puts a credential before the host. '?' and '#' are on it because a query string is out of contract even when it is
harmless today.

A NON-CONFORMING VALUE IS REPLACED, NOT REFUSED, AND THE ROW IS STILL WRITTEN. Refusing would discard the log to
protect the log: one bad path in a flush of a hundred would throw away ninety-nine good attempt rows, on the one table
whose entire purpose is diagnosing a failure at 03:00. So the offending value is replaced IN FULL by a fixed notice --
not masked character by character, because a partial redaction of a path still shows the path's shape and a partial
redaction of a key still shows its length -- and the count comes back in @ValuesWithheld so the loader can report a
defect it must then fix. The same treatment is applied to apiErrorMessage when it contains the auth path, which is the
leak EPA's error envelope actually presents: a message is free text from a server and from whatever gateway sits in
front of it, and gateways routinely echo the request path they could not route.

WHAT THIS PROCEDURE CANNOT CHECK. It does not know the API ID or the API Key, so it cannot recognise them in free text
that does not also name the auth path. That backstop lives in the loader, which does know them and replaces any
diagnostic containing either half. Two layers, neither sufficient alone: the loader knows the secret and this procedure
knows the contract.

AN ORPHANED ELEMENT IS REPORTED, NOT REFUSED, AND THAT IS A DIFFERENT DECISION FROM THE VALIDATION REFUSALS BELOW. An
element whose status row does not exist in this run means the loader attempted work nobody enumerated, which is a real
contract violation -- but refusing the set would lose every good row in the flush and the loader, which does not retry
a 50000, would lose them permanently. So the resolvable rows are written, the count comes back in @RowsOrphaned, and
the loader raises it. The refusals below are for defects a retry cannot fix and that make a row incoherent rather than
absent: a malformed set is worth stopping for, a missing neighbour is not.

TIMINGS ARE REQUIRED, NOT DEFAULTED. startedDateUtc must be present even though the column has a DEFAULT, because a
buffered flush lands seconds or minutes after the attempt it describes and a defaulted stamp would record the flush
instead. That would corrupt the one measurement G21 needs -- how long EPA takes to answer -- in a way no reader could
detect. durationMs is derived from the two stamps only when the element omits it: the loader measures with a Stopwatch
and the difference of two DATETIME2 values is the worse number, so the supplied value wins whenever there is one.

G35 DOES NOT APPLY, and it is worth saying because every other set-based writer here carries it. The table-variable
accumulator exists so a doomed transaction still records WHICH handlers a batch failed on. Here the attempt rows ARE
that record; if this procedure rolls back there is nothing left to preserve, and the failure is recorded in
logs.ExecutionLog like any other.

THE RUN MUST BE Running, for script 520's reason: recording work against a run that has already reported Succeeded or
Failed means the loader lost track of its own lifecycle, and the rows would contradict the summary the monitoring app
displays. A soft-deleted or non-existent run is refused the same way.

EMPTY ELEMENTS IS NOT AN ERROR. A flush with nothing buffered is a real outcome -- the interval expired on a quiet
stretch -- and turning a harmless no-op into a failed run would make an unattended load report a problem it does not
have.

A KEY WIDER THAN ITS COLUMN IS THE ONE MISTAKE HERE THAT CORRUPTS RATHER THAN LOSES, so the shred is deliberately wide
and the widths are checked before anything is written. OPENJSON ... WITH truncates an over-wide string silently (G36),
and a truncated handlerId is not a missing handler -- it is a DIFFERENT one, whose attempt history would gain a row it
never earned. The CASTs in the write statement are on the element side for the same reason script 520 gives: comparing
NVARCHAR (4000) against NVARCHAR (12) makes the engine widen the COLUMN and turns every seek into a scan.

WHAT @KeyParameters MAY CONTAIN. Identifiers and counts. @Elements is excluded BY NAME -- it names regulated entities
and can carry hundreds of them -- and so are requestPath, apiErrorMessage and failureMessage, which are the three
free-text values this procedure handles and the three that logs.ExecutionLog must never receive from it. From MDE's own
template: do NOT include parameters such as passwords and Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- 1. Two attempts on the same record: throttled, then succeeded. Both are kept, which is the point of the table.
DECLARE @Attempts NVARCHAR (MAX) = N'
[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":1
 ,"startedDateUtc":"2026-09-06T02:00:01.120","completedDateUtc":"2026-09-06T02:00:01.480","durationMs":360
 ,"outcome":"Throttled","httpStatusCode":429,"requestPath":"/api/v1/hd/sources/MD0000123456/N/1"
 ,"retryAfterSeconds":30,"responseBytes":214,"apiErrorCode":"E_RateLimitExceeded"
 ,"apiErrorId":"7f1c9d2e-0b44-4a1e-9c8d-2a5b6c7d8e90"}
,{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":2
 ,"startedDateUtc":"2026-09-06T02:00:31.500","completedDateUtc":"2026-09-06T02:00:33.900","durationMs":2400
 ,"outcome":"Succeeded","httpStatusCode":200,"requestPath":"/api/v1/hd/sources/MD0000123456/N/1"
 ,"responseBytes":48213}]';
DECLARE @Written INT, @Orphaned INT, @Withheld INT;
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = 1, @Elements = @Attempts
                                       , @RowsAffected = @Written OUTPUT
                                       , @RowsOrphaned = @Orphaned OUTPUT
                                       , @ValuesWithheld = @Withheld OUTPUT;

-- 2. A transport failure. No status code, because there was no response; the reason goes in failureMessage.
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = 1
   , @Elements = N'[{"handlerId":"MD0000987654","sourceType":"N","sequence":1,"attemptNumber":1
                    ,"startedDateUtc":"2026-09-06T02:04:00","outcome":"TimedOut"
                    ,"requestPath":"/api/v1/hd/sources/MD0000987654/N/1"
                    ,"failureMessage":"TaskCanceledException after the per-attempt timeout elapsed."}]';

-- 3. What NOT to send. The requestPath carries a query string, so the row is written with the path withheld and
--    @ValuesWithheld comes back 1. Nothing is refused, and nothing is logged that should not be.
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = 1
   , @Elements = N'[{"handlerId":"MD0000987654","sourceType":"N","sequence":1,"attemptNumber":2
                    ,"startedDateUtc":"2026-09-06T02:05:00","outcome":"Failed","httpStatusCode":400
                    ,"requestPath":"/api/v1/hd/other-ids?handlerId=MD0000987654"}]';

The insert seeks IX_logs_HandlerLoadStatus_LoadRunId to resolve the surrogate and
UX_logs_HandlerLoadAttempt_Natural under UPDLOCK, HOLDLOCK for the anti-semi-join. Cost scales with the element count
and the payload is parsed once. Instrumentation adds one singleton insert per call and one singleton update on the
successful path.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-06	rsincero						Initial version. Workstream D2. The first and only writer of
											logs.HandlerLoadAttempt, which script 320 created in B3 and no procedure
											had written since.
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE logs.uspRecordHandlerLoadAttemptSet
      @LoadRunId      INT
    , @Elements       NVARCHAR (MAX)
    , @RowsAffected   INT            = NULL OUTPUT
    , @RowsOrphaned   INT            = NULL OUTPUT
    , @ValuesWithheld INT            = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- =============================================================================================
    -- AR8 instrumentation. Boilerplate: copied verbatim from the template.
    -- =============================================================================================
    -- The literal is not a fallback for odd cases; it is what the loader login actually logs, because
    -- script 050 denies it metadata visibility. Keep it in step with the name above.
    DECLARE @ProcName       NVARCHAR (300) = COALESCE (QUOTENAME (OBJECT_SCHEMA_NAME (@@PROCID))
                                                     + N'.' + QUOTENAME (OBJECT_NAME (@@PROCID))
                                                    , N'[logs].[uspRecordHandlerLoadAttemptSet]')
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

    DECLARE @NowUtc          DATETIME2       = SYSUTCDATETIME ()
          , @ElementCount    INT             = 0
          , @Written         INT             = 0
          , @Orphaned        INT             = 0
          , @AlreadyRecorded INT             = 0
          , @PathsWithheld   INT             = 0
          , @MessagesWithheld INT            = 0
          , @RunStatus       NVARCHAR (20)   = NULL
          , @Failure         NVARCHAR (2048) = NULL;

    -- The replacement text, not a mask. A partial redaction of a path still shows its shape and a
    -- partial redaction of a key still shows its length; see the header.
    DECLARE @WithheldPath    NVARCHAR (400)  = N'(withheld: not a bare path -- see '
                                             + N'logs.uspRecordHandlerLoadAttemptSet)'
          , @WithheldMessage NVARCHAR (4000) = N'(withheld: EPA''s message named the auth path, whose '
                                             + N'segments are the API ID and Key. The code and error '
                                             + N'id are the fields EPA support asks for and are kept.)'
          , @WithheldWidth   NVARCHAR (4000) = N'(withheld: longer than the 4000 characters this '
                                             + N'column stores. Replaced rather than clipped, because '
                                             + N'a message cut at its limit reads as a message that '
                                             + N'ended there.)';

    -- Identifiers and counts only. @Elements is excluded BY NAME, and so are requestPath,
    -- apiErrorMessage and failureMessage. See the header. The counts are appended once known.
    SET @KeyParameters = CONCAT (N'LoadRunId='
                               , COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(null)'));

    -- The shred target is WIDE on purpose. OPENJSON ... WITH truncates silently (G36), and a truncated
    -- handlerId names a DIFFERENT handler rather than no handler. Widths are checked below, before
    -- anything is written.
    DECLARE @Element TABLE
    (
        Ordinal           INT             NOT NULL PRIMARY KEY,
        HandlerId         NVARCHAR (4000)     NULL,
        SourceType        NVARCHAR (4000)     NULL,
        Sequence          INT                 NULL,
        AttemptNumber     INT                 NULL,
        StartedDateUtc    DATETIME2           NULL,
        CompletedDateUtc  DATETIME2           NULL,
        DurationMs        INT                 NULL,
        Outcome           NVARCHAR (4000)     NULL,
        HttpStatusCode    INT                 NULL,
        RequestPath       NVARCHAR (MAX)      NULL,
        ResponseBytes     INT                 NULL,
        RetryAfterSeconds INT                 NULL,
        ApiErrorCode      NVARCHAR (4000)     NULL,
        ApiErrorMessage   NVARCHAR (MAX)      NULL,
        ApiErrorId        NVARCHAR (4000)     NULL,
        ApiErrorDate      DATETIME2           NULL,
        FailureMessage    NVARCHAR (MAX)      NULL
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
        -- NOT an expression, so a concatenation written inline is a syntax error.
        IF @LoadRunId IS NULL
        BEGIN
            SET @Failure = N'@LoadRunId is required. logs.HandlerLoadAttempt.LoadRunId is NOT NULL, and '
                         + N'an attempt that names no run cannot be shown against one.';
            THROW 50000, @Failure, 1;
        END;

        -- ARRAY, not just ISJSON, for the reason script 520 records: without the type constraint a
        -- caller who sends one element as a bare OBJECT passes the gate, OPENJSON then enumerates the
        -- object's PROPERTIES, and what comes back is engine error 245 from the ordinal CAST rather
        -- than this procedure's own refusal. ISJSON's type argument is SQL Server 2022 and in scope.
        IF @Elements IS NULL OR ISJSON (@Elements, ARRAY) = 0
        BEGIN
            SET @Failure = N'@Elements is NULL, is not valid JSON, or is not a JSON array. It must be '
                         + N'an array of objects carrying at least handlerId, sourceType, sequence, '
                         + N'attemptNumber, startedDateUtc and outcome, even for a single element. An '
                         + N'empty array [] is accepted and does nothing.';
            THROW 50000, @Failure, 1;
        END;

        SELECT @RunStatus = r.Status
          FROM logs.LoadRun AS r
         WHERE r.LoadRunId = @LoadRunId
           AND r.IsDeleted = 0;

        IF @RunStatus IS NULL
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' does not exist, or has been '
                                 , N'soft-deleted. Open a run with logs.uspStartLoadRun and record '
                                 , N'attempts against the LoadRunId it returns.');
            THROW 50000, @Failure, 1;
        END;

        IF @RunStatus <> N'Running'
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' has Status ''', @RunStatus
                                 , N''', so it is closed and no further attempts can be recorded '
                                 , N'against it. The loader has lost track of its own lifecycle: '
                                 , N'either this flush belongs to a newer run, or '
                                 , N'logs.uspCompleteLoadRun was called before the final flush. '
                                 , N'Absorbing it would leave attempt rows that contradict the run '
                                 , N'summary the monitoring app displays.');
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 2. Shred. JSON_VALUE rather than OPENJSON ... WITH, so nothing is truncated on the way in.
        -- -----------------------------------------------------------------------------------------
        INSERT INTO @Element (Ordinal, HandlerId, SourceType, Sequence, AttemptNumber, StartedDateUtc
                            , CompletedDateUtc, DurationMs, Outcome, HttpStatusCode, RequestPath
                            , ResponseBytes, RetryAfterSeconds, ApiErrorCode, ApiErrorMessage
                            , ApiErrorId, ApiErrorDate, FailureMessage)
        SELECT CAST (e.[key] AS INT) + 1
             , JSON_VALUE (e.value, '$.handlerId')
             , JSON_VALUE (e.value, '$.sourceType')
             , TRY_CAST (JSON_VALUE (e.value, '$.sequence')          AS INT)
             , TRY_CAST (JSON_VALUE (e.value, '$.attemptNumber')     AS INT)
             , TRY_CAST (JSON_VALUE (e.value, '$.startedDateUtc')    AS DATETIME2)
             , TRY_CAST (JSON_VALUE (e.value, '$.completedDateUtc')  AS DATETIME2)
             , TRY_CAST (JSON_VALUE (e.value, '$.durationMs')        AS INT)
             , JSON_VALUE (e.value, '$.outcome')
             , TRY_CAST (JSON_VALUE (e.value, '$.httpStatusCode')    AS INT)
             -- JSON_VALUE returns NULL, in lax mode, for a value longer than 4000 characters rather
             -- than clipping it. That is the right failure for every free-text column here: losing
             -- the text is a loss where a clip would be a lie, and the width check below reports the
             -- ones that are merely too wide for their column.
             , JSON_VALUE (e.value, '$.requestPath')
             , TRY_CAST (JSON_VALUE (e.value, '$.responseBytes')     AS INT)
             , TRY_CAST (JSON_VALUE (e.value, '$.retryAfterSeconds') AS INT)
             , JSON_VALUE (e.value, '$.apiErrorCode')
             , JSON_VALUE (e.value, '$.apiErrorMessage')
             , JSON_VALUE (e.value, '$.apiErrorId')
             , TRY_CAST (JSON_VALUE (e.value, '$.apiErrorDate')      AS DATETIME2)
             , JSON_VALUE (e.value, '$.failureMessage')
          FROM OPENJSON (@Elements) AS e;

        SET @ElementCount = @@ROWCOUNT;
        SET @KeyParameters = CONCAT (@KeyParameters, N', Elements=', @ElementCount);

        -- -----------------------------------------------------------------------------------------
        -- 3. The key, complete and within its widths. Every message names the offending element,
        --    because "the batch was rejected" is not actionable at 03:00.
        -- -----------------------------------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM @Element
                    WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL
                       OR AttemptNumber IS NULL OR AttemptNumber < 1)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' is missing part of its key (handlerId, sourceType, sequence, '
                                    , N'attemptNumber, the first attempt being 1). The first three '
                                    , N'plus LoadRunId locate the status row and attemptNumber '
                                    , N'completes the attempt''s own grain, so none of them can be '
                                    , N'defaulted and the set is rejected whole rather than partly '
                                    , N'applied.')
              FROM @Element
             WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL
                OR AttemptNumber IS NULL OR AttemptNumber < 1;
            THROW 50000, @Failure, 1;
        END;

        -- Required even though the column has a DEFAULT. A buffered flush lands after the attempt it
        -- describes, so a defaulted stamp would record the flush -- see the header. A value that
        -- failed TRY_CAST above arrives here as NULL and is reported the same way, which is why the
        -- message names the format.
        IF EXISTS (SELECT 1 FROM @Element WHERE StartedDateUtc IS NULL)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has no usable startedDateUtc (ISO 8601, UTC, for example '
                                    , N'2026-09-06T02:00:01.120). It is required even though the '
                                    , N'column defaults, because this set is flushed from a buffer '
                                    , N'and the default would stamp the flush rather than the '
                                    , N'attempt -- corrupting the one measurement the throughput '
                                    , N'question needs, in a way no reader could detect.')
              FROM @Element
             WHERE StartedDateUtc IS NULL;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                    WHERE Outcome IS NULL
                       OR Outcome NOT IN (N'Succeeded', N'Failed', N'Throttled', N'TimedOut'
                                         , N'Cancelled'))
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has outcome '
                                    , COALESCE (N'''' + MIN (Outcome) + N'''', N'NULL')
                                    , N'. It must be one of ''Succeeded'', ''Failed'', ''Throttled'', '
                                    , N'''TimedOut'' or ''Cancelled''. Throttled and TimedOut are kept '
                                    , N'out of the general Failed bucket on purpose: both are '
                                    , N'expected, both are retried, and both say something different '
                                    , N'about the API''s health from an error EPA chose to return. '
                                    , N'Checked here so the refusal names the element rather than '
                                    , N'arriving as CK_logs_HandlerLoadAttempt_Outcome.')
              FROM @Element
             WHERE Outcome IS NULL
                OR Outcome NOT IN (N'Succeeded', N'Failed', N'Throttled', N'TimedOut', N'Cancelled');
            THROW 50000, @Failure, 1;
        END;

        -- ONLY THE IDENTIFYING VALUES ARE REFUSED FOR WIDTH, and the split is deliberate. handlerId,
        -- sourceType and outcome decide which row this is and what it says, so an over-wide one is a
        -- defect that would corrupt rather than lose. The DESCRIPTIVE values -- requestPath,
        -- apiErrorCode, apiErrorId, apiErrorMessage, failureMessage -- are handled in section 5
        -- instead, because four of the five are EPA's text and refusing the flush over their width
        -- would make this log hostage to another system's field sizes: one long error code would
        -- discard every good attempt row buffered behind it.
        IF EXISTS (SELECT 1 FROM @Element
                    WHERE LEN (HandlerId)  > 12
                       OR LEN (SourceType) > 1
                       OR LEN (Outcome)    > 20)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' carries an identifying value wider than the column that '
                                    , N'stores it (handlerId 12, sourceType 1, outcome 20). Checked '
                                    , N'rather than truncated because a truncated handlerId is not a '
                                    , N'missing handler -- it is a different one, whose attempt '
                                    , N'history would gain a row it never earned. The descriptive '
                                    , N'columns are withheld rather than refused; see the header.')
              FROM @Element
             WHERE LEN (HandlerId) > 12 OR LEN (SourceType) > 1 OR LEN (Outcome) > 20;
            THROW 50000, @Failure, 1;
        END;

        -- A duplicate grain inside the set would make the outcome depend on which element the engine
        -- inserted first, and the NOT EXISTS below cannot see rows the same statement is inserting.
        IF EXISTS (SELECT 1 FROM @Element
                   GROUP BY HandlerId, SourceType, Sequence, AttemptNumber
                   HAVING COUNT (*) > 1)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'The set names handler ', HandlerId, N' / sourceType '
                                            , SourceType, N' / sequence ', Sequence, N' / attempt '
                                            , AttemptNumber, N' ', COUNT (*), N' times. One element '
                                            , N'per attempt: two rows targeting the same grain cannot '
                                            , N'both be inserted, and the anti-semi-join below cannot '
                                            , N'see a row the same statement is writing, so the '
                                            , N'filtered unique index would reject one of them.')
              FROM @Element
             GROUP BY HandlerId, SourceType, Sequence, AttemptNumber
            HAVING COUNT (*) > 1
             ORDER BY HandlerId, SourceType, Sequence, AttemptNumber;
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 4. Coherence. Each of these makes a row that is present but says two things at once, which
        --    is worse in a diagnostic table than a row that is absent.
        -- -----------------------------------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM @Element
                    WHERE HttpStatusCode IS NOT NULL
                      AND HttpStatusCode NOT BETWEEN 100 AND 599)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has httpStatusCode ', MIN (HttpStatusCode), N', which is not '
                                    , N'a status code (100-599). A transport failure has no status '
                                    , N'code at all -- leave the property out rather than sending 0, '
                                    , N'which reads as a real code in the monitoring grid.')
              FROM @Element
             WHERE HttpStatusCode IS NOT NULL AND HttpStatusCode NOT BETWEEN 100 AND 599;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                    WHERE DurationMs        < 0
                       OR ResponseBytes     < 0
                       OR RetryAfterSeconds < 0)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has a negative durationMs, responseBytes or '
                                    , N'retryAfterSeconds. None of the three can be negative, and a '
                                    , N'negative duration is the signature of a clock that moved '
                                    , N'backwards mid-attempt rather than of a fast response.')
              FROM @Element
             WHERE DurationMs < 0 OR ResponseBytes < 0 OR RetryAfterSeconds < 0;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                    WHERE CompletedDateUtc IS NOT NULL
                      AND CompletedDateUtc < StartedDateUtc)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' completed before it started. Two stamps in that order make '
                                    , N'every duration derived from them negative, and the row would '
                                    , N'go on being averaged into the throughput figures long after '
                                    , N'anyone remembered why they looked wrong.')
              FROM @Element
             WHERE CompletedDateUtc IS NOT NULL AND CompletedDateUtc < StartedDateUtc;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                    WHERE Outcome = N'Succeeded'
                      AND (ApiErrorCode IS NOT NULL OR ApiErrorId IS NOT NULL
                        OR FailureMessage IS NOT NULL))
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' is a Succeeded attempt carrying an apiErrorCode, apiErrorId '
                                    , N'or failureMessage. An attempt that succeeded has no error to '
                                    , N'report, and a row asserting both would make the monitoring '
                                    , N'grid''s outcome breakdown disagree with its own error '
                                    , N'columns.')
              FROM @Element
             WHERE Outcome = N'Succeeded'
               AND (ApiErrorCode IS NOT NULL OR ApiErrorId IS NOT NULL OR FailureMessage IS NOT NULL);
            THROW 50000, @Failure, 1;
        END;

        -- Cancelled is exempt on purpose: the process was told to stop, so there is genuinely nothing
        -- to say about why the attempt did not finish, and demanding a reason would push the loader
        -- into inventing one.
        IF EXISTS (SELECT 1 FROM @Element
                    WHERE Outcome IN (N'Failed', N'Throttled', N'TimedOut')
                      AND HttpStatusCode IS NULL
                      AND ApiErrorCode   IS NULL
                      AND FailureMessage IS NULL)
        BEGIN
            SELECT @Failure = CONCAT (N'Element ', MIN (Ordinal), N' of ', @ElementCount
                                    , N' has outcome ', MIN (Outcome), N' and carries none of '
                                    , N'httpStatusCode, apiErrorCode or failureMessage. At least one '
                                    , N'is required: an attempt row that says only that something '
                                    , N'went wrong repeats what Outcome already said, and answering '
                                    , N'the next question is what this table exists for. Cancelled '
                                    , N'is exempt, because a process told to stop has nothing to '
                                    , N'add.')
              FROM @Element
             WHERE Outcome IN (N'Failed', N'Throttled', N'TimedOut')
               AND HttpStatusCode IS NULL AND ApiErrorCode IS NULL AND FailureMessage IS NULL;
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 5. Withhold what must not be stored. Replaced in full, never masked, and the row is still
        --    written -- see the header for why refusing here would discard the log to protect it.
        --
        --    LIKE rather than a regex: SQL Server 2022 has no regex functions, and this has to run on
        --    the target platform rather than only on the 2025 workstation. None of '?', '#', ':',
        --    '/' or '@' is a LIKE metacharacter, so no ESCAPE clause is needed; '[', '%' and '_' are
        --    the ones that would need one and none of them is being searched for.
        -- -----------------------------------------------------------------------------------------
        UPDATE @Element
           SET RequestPath = @WithheldPath
         WHERE RequestPath IS NOT NULL
           AND (LEFT (RequestPath, 1) <> N'/'
             OR RequestPath LIKE N'%?%'
             OR RequestPath LIKE N'%#%'
             OR RequestPath LIKE N'%://%'
             OR RequestPath LIKE N'%@%'
             OR RequestPath LIKE N'%/auth/%');

        SET @PathsWithheld = @@ROWCOUNT;

        -- The auth path only. A message is free text from EPA and from whatever gateway sits in front
        -- of it, and the documented hazard is a gateway echoing the request path it could not route --
        -- for the auth call, that path IS the credential. A broader pattern here would withhold
        -- ordinary prose and teach an operator that the notice means nothing.
        UPDATE @Element
           SET ApiErrorMessage = @WithheldMessage
         WHERE ApiErrorMessage IS NOT NULL
           AND (ApiErrorMessage LIKE N'%/auth/%' OR ApiErrorMessage LIKE N'%/api/v1/auth%');

        SET @MessagesWithheld = @@ROWCOUNT;

        UPDATE @Element
           SET FailureMessage = @WithheldMessage
         WHERE FailureMessage IS NOT NULL
           AND (FailureMessage LIKE N'%/auth/%' OR FailureMessage LIKE N'%/api/v1/auth%');

        SET @MessagesWithheld = @MessagesWithheld + @@ROWCOUNT;

        -- Over-wide descriptive values, replaced by a notice that states the length rather than
        -- clipped to fit. A clip is a lie -- a path cut at 400 characters reads as a path that ended
        -- there -- and these five columns are not the row's identity, so losing the text costs a
        -- detail while refusing the flush would cost every attempt buffered behind it. Only
        -- requestPath and apiErrorCode/apiErrorId can actually reach here: JSON_VALUE in lax mode
        -- returns NULL rather than clipping anything past 4000 characters, so the two 4000-wide
        -- columns are unreachable by width and are listed for the reader who checks.
        UPDATE @Element
           SET RequestPath = CONCAT (N'(withheld: ', LEN (RequestPath)
                                   , N' characters, longer than the 400 this column stores)')
         WHERE LEN (RequestPath) > 400;

        SET @PathsWithheld = @PathsWithheld + @@ROWCOUNT;

        UPDATE @Element
           SET ApiErrorCode = NULL
         WHERE LEN (ApiErrorCode) > 100;

        SET @MessagesWithheld = @MessagesWithheld + @@ROWCOUNT;

        -- NULL rather than a notice for these two, because the notice would not fit and a code or an
        -- id is only useful verbatim: EPA support matches them exactly, so a substitute is noise. The
        -- count still reports that something was dropped.
        UPDATE @Element
           SET ApiErrorId = NULL
         WHERE LEN (ApiErrorId) > 50;

        SET @MessagesWithheld = @MessagesWithheld + @@ROWCOUNT;

        UPDATE @Element
           SET ApiErrorMessage = @WithheldWidth
         WHERE LEN (ApiErrorMessage) > 4000;

        SET @MessagesWithheld = @MessagesWithheld + @@ROWCOUNT;

        UPDATE @Element
           SET FailureMessage = @WithheldWidth
         WHERE LEN (FailureMessage) > 4000;

        SET @MessagesWithheld = @MessagesWithheld + @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 6. Write. One INSERT, resolving the surrogate by natural key and declining any grain that
        --    is already recorded.
        -- -----------------------------------------------------------------------------------------
        BEGIN TRANSACTION;

        INSERT INTO logs.HandlerLoadAttempt
            (HandlerLoadStatusId, LoadRunId, AttemptNumber, StartedDateUtc, CompletedDateUtc
           , DurationMs, Outcome, HttpStatusCode, RequestPath, ResponseBytes, RetryAfterSeconds
           , ApiErrorCode, ApiErrorMessage, ApiErrorId, ApiErrorDate, FailureMessage)
        SELECT s.HandlerLoadStatusId
             , @LoadRunId
             , e.AttemptNumber
             , e.StartedDateUtc
             , e.CompletedDateUtc
             -- Derived only when the element omits it. The loader measures with a Stopwatch and the
             -- difference of two DATETIME2 values is the worse number, so a supplied value wins.
             -- DATEDIFF_BIG rather than DATEDIFF, and the ceiling, for the reason script 520 records:
             -- DATEDIFF (MILLISECOND, ...) overflows outright at about 24 days.
             , COALESCE (e.DurationMs
                       , CASE WHEN e.CompletedDateUtc IS NULL THEN NULL
                              ELSE CAST (LEAST (DATEDIFF_BIG (MILLISECOND, e.StartedDateUtc
                                                            , e.CompletedDateUtc)
                                              , CAST (2147483647 AS BIGINT)) AS INT)
                         END)
             , CAST (e.Outcome         AS NVARCHAR (20))
             , e.HttpStatusCode
             , CAST (e.RequestPath     AS NVARCHAR (400))
             , e.ResponseBytes
             , e.RetryAfterSeconds
             , CAST (e.ApiErrorCode    AS NVARCHAR (100))
             , CAST (e.ApiErrorMessage AS NVARCHAR (4000))
             , CAST (e.ApiErrorId      AS NVARCHAR (50))
             , e.ApiErrorDate
             , CAST (e.FailureMessage  AS NVARCHAR (4000))
          FROM @Element AS e
          -- The CAST is on the ELEMENT side deliberately: comparing NVARCHAR (4000) against the column
          -- would make the engine widen the COLUMN and scan. The width check has already passed, so it
          -- cannot lose anything.
          JOIN logs.HandlerLoadStatus AS s
            ON s.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
           AND s.SourceType = CAST (e.SourceType AS NVARCHAR (1))
           AND s.Sequence   = e.Sequence
         WHERE s.LoadRunId = @LoadRunId
           AND s.IsDeleted = 0
           -- Unfiltered by IsDeleted, and UPDLOCK, HOLDLOCK. Both are load-bearing; see the header.
           AND NOT EXISTS (SELECT 1
                             FROM logs.HandlerLoadAttempt AS a WITH (UPDLOCK, HOLDLOCK)
                            WHERE a.HandlerLoadStatusId = s.HandlerLoadStatusId
                              AND a.AttemptNumber       = e.AttemptNumber);
        --
        -- The audit* columns are left to their DEFAULTs, per MDE's decision that the create-audit
        -- columns are the table's business. There is no UPDATE anywhere in this procedure, so
        -- auditModifiedDateUtc never needs setting explicitly -- this table is append-only.

        SET @Written = @@ROWCOUNT;

        -- How many elements named a grain that was already on the table. Expected on a retry of a
        -- committed flush, so it is reported rather than raised.
        SELECT @AlreadyRecorded = COUNT (*)
          FROM @Element AS e
          JOIN logs.HandlerLoadStatus AS s
            ON s.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
           AND s.SourceType = CAST (e.SourceType AS NVARCHAR (1))
           AND s.Sequence   = e.Sequence
         WHERE s.LoadRunId = @LoadRunId
           AND s.IsDeleted = 0
           AND EXISTS (SELECT 1
                         FROM logs.HandlerLoadAttempt AS a
                        WHERE a.HandlerLoadStatusId = s.HandlerLoadStatusId
                          AND a.AttemptNumber       = e.AttemptNumber);

        -- How many named a source record this run never enumerated. A contract violation, reported
        -- rather than refused; see the header.
        SELECT @Orphaned = COUNT (*)
          FROM @Element AS e
         WHERE NOT EXISTS (SELECT 1
                             FROM logs.HandlerLoadStatus AS s
                            WHERE s.LoadRunId  = @LoadRunId
                              AND s.IsDeleted  = 0
                              AND s.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
                              AND s.SourceType = CAST (e.SourceType AS NVARCHAR (1))
                              AND s.Sequence   = e.Sequence);

        SET @RowsAffected   = @Written;
        SET @RowsOrphaned   = @Orphaned;
        SET @ValuesWithheld = @PathsWithheld + @MessagesWithheld;

        SET @Comments = CONCAT (N'elements=', @ElementCount
                              , N', rowsWritten=', @Written
                              , N', alreadyRecorded=', @AlreadyRecorded
                              , N', orphaned=', @Orphaned
                              , N', pathsWithheld=', @PathsWithheld
                              , N', messagesWithheld=', @MessagesWithheld);

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        -- Completion. Deliberately after the COMMIT; the insert is a no-op the second time, which is
        -- what makes the retry this can provoke harmless. See the header.
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
        -- transaction still records WHICH handlers a batch failed on -- but here the attempt rows ARE
        -- that record, so a rollback leaves nothing to preserve. Counts only: the free-text columns
        -- this procedure handles are exactly the three that must not reach logs.ExecutionLog.
        SET @ContextMessage = CONCAT (N'elements=', @ElementCount
                                    , N', pathsWithheld=', @PathsWithheld
                                    , N', messagesWithheld=', @MessagesWithheld
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
    , @ObjectName  = N'uspRecordHandlerLoadAttemptSet'
    , @Description = N'Appends rows to logs.HandlerLoadAttempt from a JSON @Elements array, a flush at a time. The ONLY writer of that table: script 320 created it in Workstream B3 and no procedure wrote it until Workstream D2, because the attempt log belongs to the workstream that produces attempts. The caller supplies the NATURAL key (handlerId, sourceType, sequence) plus attemptNumber and this procedure resolves HandlerLoadStatusId itself, because that surrogate is assigned by logs.uspUpsertHandlerLoadStatusSet and never returned -- so Enumerate must have run for these records first, and an element naming a record the run never enumerated is counted in @RowsOrphaned rather than written. APPEND-ONLY AND IDEMPOTENT AT ONCE: it is an INSERT ... WHERE NOT EXISTS, so a retry of a committed flush -- which the AR8 completion UPDATE running after the COMMIT can provoke -- writes nothing and raises nothing, and an element whose grain is already present is neither written nor updated. The NOT EXISTS is UNFILTERED by IsDeleted, the opposite of script 520''s MERGE match: a soft-deleted status row is revived because it is a current state, but a soft-deleted attempt row DECLINES the insert because an attempt is a historical event that happened once. UPDLOCK, HOLDLOCK on the anti-semi-join, or two concurrent flushes carrying the same grain both insert and the filtered unique index rejects one. THIS IS THE FIRST PLACE THE RequestPath CONTRACT IS ENFORCED RATHER THAN DOCUMENTED: a path is accepted only if it starts with ''/'' and contains none of ''?'', ''#'', ''://'', ''@'' or the auth path, because EPA''s auth endpoint carries BOTH HALVES OF THE CREDENTIAL AS PATH SEGMENTS and the monitoring web app can read this table. A non-conforming value is REPLACED IN FULL by a fixed notice and the row is still written -- refusing would discard the log to protect the log, losing a whole flush of good diagnostic rows over one bad path -- and the count comes back in @ValuesWithheld so the loader reports the defect. The same treatment applies to apiErrorMessage and failureMessage when either names the auth path, which is the leak EPA''s error envelope actually presents through a gateway echoing a path it could not route. What this procedure cannot check is the credential itself, which it was never told; that backstop is the loader''s. startedDateUtc is REQUIRED despite the column''s DEFAULT, because a buffered flush lands after the attempt and the default would stamp the flush; durationMs is derived from the two stamps only when omitted, since a Stopwatch beats a DATETIME2 subtraction. Refused: a malformed set, an outcome outside the five-value domain, an over-wide value, a duplicate grain, a status code outside 100-599, a negative duration, a completion before its start, a Succeeded attempt carrying an error, and a Failed, Throttled or TimedOut attempt carrying no detail at all -- Cancelled is exempt, because a process told to stop has nothing to add. The run must exist, be live, and still be Running. An empty array is a no-op. @Elements, requestPath, apiErrorMessage and failureMessage are all excluded from @KeyParameters by name. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. The loader writes these rows; the monitoring web app reads them through
-- logs.uspGetLoadRunSummary and must never be able to write an attempt it is displaying.
--
-- Ownership chaining carries the INSERT on logs.HandlerLoadAttempt, the read of
-- logs.HandlerLoadStatus, the read of logs.LoadRun and the chained inserts into logs.ExecutionLog
-- through this single grant, so the loader login holds no direct permission on any of the four
-- tables (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRecordHandlerLoadAttemptSet TO RCRAInfoLoaderRole;
END;
GO

PRINT N'524: logs.uspRecordHandlerLoadAttemptSet created or altered, EXECUTE granted to the loader role.';
GO
