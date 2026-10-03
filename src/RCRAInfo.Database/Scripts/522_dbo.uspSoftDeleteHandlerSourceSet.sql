-- SET XACT_ABORT ON sits ABOVE the header block deliberately. sys.sql_modules stores only the batch
-- that contains CREATE, so a header placed after this GO would be present in the file and absent from
-- the database. build/check_stored_headers.py measures that against the deployed catalog.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   dbo.uspSoftDeleteHandlerSourceSet
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

The only way anything leaves the mirror. Soft-deletes a set of handler source versions and every descendant row that
hangs off them, a set at a time. There is no hard delete anywhere in this database (AR7), and there is no other
procedure that sets IsDeleted on dbo.HandlerSource.

DELETED IS NOT SUPERSEDED, and keeping the two apart is this procedure's reason for existing. Per Phase1-Analysis.md
[R4]: superseded is CurrentRecord = 0, deleted is IsDeleted = 1, and conflating them loses real information -- a version
EPA replaced is history worth keeping, a version EPA withdrew is a record that should never have been shown. So
dbo.uspReconcileCurrentRecord demotes and never deletes, this procedure deletes and never demotes, and the signal that
moves a version from the first to the second is a deliberate decision by the loader, not a side effect of a reconcile.

THE CASCADE IS THE POINT. Nineteen descendant tables hang off dbo.HandlerSource -- sixteen children keyed by
HandlerSourceId, and three grandchildren keyed through dbo.HandlerSourceEpisodicWaste and
dbo.HandlerSourceHsmActivity. Only dbo.vwHandlerSource and dbo.vwHandlerSourceHistory exist as views, so a read of any
child table filters that child's own IsDeleted; a parent deleted without its children leaves nineteen tables of rows
that read as live and belong to a version nobody is allowed to see. Every one of them is soft-deleted here, in the same
transaction, and build/check_cascade_coverage.py derives the descendant list from sys.foreign_keys and fails if this
file does not cover every table in it.

========================================================================================================================
Requirements and Key Dependencies:

dbo.HandlerSource (script 100) and its nineteen descendant tables (scripts 1xx, generated).
dbo.HandlerOtherIdentifier (script 1xx) -- handler-grained, not version-grained; see the notes.

logs.LoadRun (script 300), for the run this delete belongs to. logs.DataQualityObservation (script 330), for the one
consequence worth reporting.

logs.uspStartExecutionLogging and logs.uspRecordExecutionError, for the AR8 instrumentation block, copied from
.claude/skills/sql-objects/templates/procedure.sql.

EXECUTE is granted to RCRAInfoLoaderRole only.

========================================================================================================================
Notes:

WHERE THE SIGNAL COMES FROM (G23). It is not known whether EPA's summaries delta feed reports deletions at all, which is
why Phase1-Analysis.md makes a periodic full reconciliation pass mandatory rather than optional. The detection is
dbo.uspReconcileCurrentRecord's VersionNotInSourceSummary observation: a version live here and absent from EPA's
complete list for the handler. That observation deliberately does NOT delete anything, because absence has two causes --
EPA withdrew the version, or the caller passed a filtered list -- and only the loader knows which. This procedure is
where the loader acts once it knows.

@Reason IS REQUIRED, AND WHAT IT CAN AND CANNOT DO. It is recorded in logs.ExecutionLog, in KeyParameters and Comments,
so the reason a set of versions disappeared is recoverable from the log for that call. It is deliberately NOT stored on
the row: dbo.HandlerSource is a mirror of EPA's schema plus the audit columns, and adding a DeletionReason column would
put our workflow inside their table. The per-row trail is auditDeletedBy and auditDeletedDateUtc, which place the delete
in time; logs.ExecutionLog for that moment says why. That is a real limitation and it is stated rather than papered
over: if MDE later wants per-row reasons, the answer is a guarded ALTER TABLE ... ADD, not a re-reading of this comment.

THIS IS THE ONLY STATEMENT IN THE DATABASE THAT WRITES logs.HandlerLoadStatus.Outcome = N'SoftDeleted'. Step 10 marks
the status row of every version this call deleted Status = N'Succeeded', Outcome = N'SoftDeleted', inside the same
transaction as the delete -- so the row says 'withdrawn' only if the withdrawal actually committed, which is the same
rule and the same reason as script 400 step 5. Before that step existed nothing could mark a withdrawn version at all:
script 520 has four modes (Enumerate, Attempt, Fail, Skip) and none of them is a success, script 400 marks only what it
merged, and a version that answered 404 was therefore deleted correctly and left reading InProgress forever.

Status is Succeeded, not Failed, because the column records whether the run DEALT WITH the version and a 404 was dealt
with; Failed would make every subsequent run re-fetch a record that will still be absent tomorrow. The difference
between 'fetched and merged' and 'withdrawn' lives in Outcome, which is what Outcome is for. It is an UPDATE and never
an INSERT, for script 400's reason: the Pending rows are written when the run ENUMERATES, so a deleted version with no
row here was never enumerated by this run -- ordinary for a reconciliation-driven delete, a loader defect for a
404-driven one -- and inserting one would manufacture the evidence for the second. The shortfall is reported as
'status=N (M not enumerated)' in Comments instead. Unlike script 520's Fail and Skip modes, an already-Succeeded row is
not excluded: a version merged earlier in the same run and withdrawn by a later reconciliation really was deleted, and
the last thing the run did to it is what the row should say.

THE MIRROR IS CASCADED; THE AUDIT TRAIL IS NOT. logs.HandlerLoadStatus carries a real foreign key to
dbo.HandlerSource (HandlerSourceId), and logs.HandlerLoadAttempt and logs.DataQualityObservation hang
off it -- so a purely mechanical walk of sys.foreign_keys reaches twenty-two tables, not nineteen.
Those three are left alone deliberately. A row in dbo.* asserts something about a regulated entity, and
when EPA withdraws the version that assertion must stop being readable. A row in logs.* asserts
something about what our loader did: that on a given night it fetched this handler, got this HTTP
status, wrote this version, and found these data-quality problems. Withdrawing the version does not
make any of that false -- it makes it essential, because "when did we have this data and when did it
go?" is the question a withdrawal provokes. Deleting the observations with the version would erase the
reason for the deletion at the moment of the deletion.

A soft-deleted parent does not break referential integrity, so logs.HandlerLoadStatus.HandlerSourceId
keeps pointing at the withdrawn version and stays joinable, which is what an investigation needs. The
consequence for readers is that a log read joining dbo.HandlerSource must not assume the parent is
live. build/check_cascade_coverage.py holds these three in a named CASCADE_EXEMPT list rather than
filtering the logs schema, so the next log table somebody hangs off dbo.HandlerSource fails the check
and gets a decision instead of being absorbed by a wildcard.

dbo.HandlerOtherIdentifier IS HANDLER-GRAINED AND IS NOT PART OF THE VERSION CASCADE. It is keyed
(HandlerId, ActivityLocation, OtherId) and holds no HandlerSourceId, because an other-id belongs to the regulated entity
rather than to one version of its record -- which is also why it has no foreign key to dbo.HandlerSource. Deleting one
version of a handler must not remove its other identifiers. So it is soft-deleted only when the delete leaves the
handler with NO live version at all, which is the only moment the handler itself is gone from the mirror.

THE ONE CONSEQUENCE WORTH REPORTING. Deleting the version that was CurrentRecord = 1 while other versions survive
leaves the handler in dbo.HandlerSource and absent from dbo.vwHandlerSource -- indistinguishable, to every default read,
from a handler that was never loaded. The delete is still performed: if EPA withdrew the current version, the current
version is withdrawn, and refusing would leave the mirror knowingly wrong. But a CurrentRecordMissingInMirror
observation is recorded, using the same ObservationType dbo.uspReconcileCurrentRecord uses, so the monitoring app has
one thing to look for rather than two. The fix is a reconcile against the handler's fresh summary list, which is the
same fix in both cases.

[R47] AND IT IS ASKED PER HANDLER, NOT PER (HandlerId, SourceType). Re-grained 2026-09-08 alongside script 521's section
6b, and the shared ObservationType is exactly why it had to move at the same time: EPA flags currency once per HANDLER
rather than once per source type, so a handler whose current version now sits under some other source type has not left
dbo.vwHandlerSource and reporting it as missing would be false. Two writers putting two different meanings into one
ObservationType would be worse than either grain on its own -- the monitoring app cannot tell which procedure wrote a
row, which is the whole reason the type is shared. See the [R47] paragraph in script 521's header for the measurement
that settled the grain.

G35 APPLIES. Observations are logging, they live outside logs.ExecutionLog, and a rollback takes them -- so they are
accumulated in a table variable, which ROLLBACK does not affect, and written once. On the successful path they go in
inside the transaction. In the CATCH they are flushed after the rollback with a sentence appended saying the delete that
provoked them did not happen, because an observation describing a consequence of a rolled-back write is worse than no
observation.

REVIVAL IS dbo.uspMergeHandlerSourceBatch'S JOB, NOT THIS PROCEDURE'S. If EPA reports a withdrawn version again, the
merge must clear IsDeleted on the parent AND on every descendant row it writes -- the same unfiltered-match-plus-revive
shape script 400 already uses on the parent. The child collections in script 400 are still outstanding (they are
generated by build/generate_schema.py, not hand-written), and reviving descendants is part of that work rather than
something this procedure can do from the other side.

EVERY UPDATE IS GUARDED BY IsDeleted = 0, so a second identical call writes nothing, reports 0, and moves no
auditDeletedDateUtc. That matters more than usual here: re-stamping auditDeletedDateUtc on an already-deleted row would
make the audit trail claim the row was deleted at a time when it had already been deleted for a week.

WHAT @KeyParameters MAY CONTAIN. Identifiers, counts, and @Reason -- which is operator or loader text about why a set
was withdrawn and must not be used to carry anything else. @Elements is excluded BY NAME: it names regulated entities
and a reconciliation pass can carry thousands. From MDE's own template: do NOT include parameters such as passwords and
Personally Identifiable Information.

========================================================================================================================
Example Usage and Performance:

-- 1. The reconciliation pass found three versions that EPA no longer lists, and confirmed against a
--    re-fetch that they were withdrawn rather than missing from a filtered list.
DECLARE @Rows INT, @Children INT;
EXEC dbo.uspSoftDeleteHandlerSourceSet
      @LoadRunId    = 1
    , @Elements     = N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":2}
                        ,{"handlerId":"MD0000123456","sourceType":"N","sequence":3}
                        ,{"handlerId":"MD0000987654","sourceType":"N","sequence":1}]'
    , @Reason       = N'Absent from /hd/sources/summaries on two consecutive full reconciles (G23).'
    , @RowsAffected = @Rows     OUTPUT
    , @ChildRows    = @Children OUTPUT;

-- 2. A whole handler leaves the mirror: pass every one of its versions. There is no handler-level
--    granularity, on purpose -- see the notes on dbo.HandlerOtherIdentifier.

-- 3. Called twice with the same set: the second call reports 0 and 0 and writes nothing.

One index seek per element to resolve the target versions, then twenty-two guarded UPDATEs -- nineteen descendants, the
parent, dbo.HandlerOtherIdentifier for handlers left with nothing live, and logs.HandlerLoadStatus for the run's own
audit trail. Each seeks the target table's foreign-key index; the last seeks
UX_logs_HandlerLoadStatus_Natural. Cost scales with the number of descendant rows, not with the size of
dbo.HandlerSource, and a re-run touches nothing.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA4.
2026-09-05	rsincero						@Elements validation now requires ISJSON (@Elements, ARRAY). See script
											520's history entry of the same date for the finding.
2026-09-06	rsincero						Step 10 added: the AR5 status half, inside the transaction. Found while
											building the D2 data client, which classifies a 404 from the source-detail
											endpoint as the soft-delete signal and then had nowhere to record the
											result -- script 520 has no success mode and script 400 marks only what it
											merged, so Outcome = N'SoftDeleted' had been in
											CK_logs_HandlerLoadStatus_Outcome since script 310 with nothing able to
											write it. No signature change: the shortfall is reported in Comments as
											'status=N (M not enumerated)', following script 400.
2026-09-08	rsincero						[R47] Step 9's CurrentRecordMissingInMirror is now asked PER HANDLER
											rather than per (HandlerId, SourceType), matching the re-grain in script
											521 section 6b. EPA flags currency once per handler, so a handler still
											current under another source type has not left dbo.vwHandlerSource and
											reporting it was false. The grain MUST match 521's: both procedures
											write this same ObservationType into logs.DataQualityObservation, and
											one meaning per writer would be worse than either grain alone.
											SourceType is now NULL on these rows and ObservedValue names the source
											type count. HandlerLoadStatusId is left NULL and now says why in a
											comment -- every observation here is handler-grained, so no single
											status row determines it.
***********************************************************************************************************************/
CREATE OR ALTER PROCEDURE dbo.uspSoftDeleteHandlerSourceSet
      @LoadRunId    INT
    , @Elements     NVARCHAR (MAX)
    , @Reason       NVARCHAR (200)
    , @RowsAffected INT            = NULL OUTPUT
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
                                                    , N'[dbo].[uspSoftDeleteHandlerSourceSet]')
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

    DECLARE @NowUtc        DATETIME2       = SYSUTCDATETIME ()
          , @ActorLogin    NVARCHAR (128)  = ORIGINAL_LOGIN ()
          , @ElementCount  INT             = 0
          , @Targeted      INT             = 0
          , @Parents       INT             = 0
          , @Children      INT             = 0
          , @OtherIds      INT             = 0
          -- AR5. How many logs.HandlerLoadStatus rows this call marked SoftDeleted, and how many
          -- deleted versions had no row for this run to mark. See step 10.
          , @StatusRows    INT             = 0
          , @StatusMissing INT             = 0
          -- NULL until a flush has actually landed, and back to NULL if the flush fails. It is
          -- reported in Comments on the way out and in ContextMessage on the way down, so the log
          -- says whether the findings survived rather than leaving it to be inferred.
          , @ObservationsWritten INT        = NULL
          , @RunStatus     NVARCHAR (20)   = NULL
          , @Failure       NVARCHAR (2048) = NULL
          -- Guards the CATCH flush: the completion UPDATE runs after the COMMIT, so a failure there
          -- arrives with the observations already committed and nothing rolled back.
          , @Committed     BIT             = 0;

    -- Identifiers, counts and @Reason. @Elements is excluded BY NAME; see the header.
    SET @KeyParameters = CONCAT (N'LoadRunId='
                               , COALESCE (CAST (@LoadRunId AS NVARCHAR (11)), N'(null)')
                               , N', Reason=', COALESCE (@Reason, N'(null)'));

    -- The shred target is WIDE on purpose. OPENJSON ... WITH truncates silently (G36), and a truncated
    -- HandlerId names a DIFFERENT handler -- which in this procedure would delete somebody else's
    -- version and every descendant row it owns. Widths are checked below, before anything is written.
    DECLARE @Element TABLE
    (
        Ordinal    INT             NOT NULL PRIMARY KEY,
        HandlerId  NVARCHAR (4000)     NULL,
        SourceType NVARCHAR (4000)     NULL,
        Sequence   INT                 NULL
    );

    -- Resolved BEFORE anything is written, and the order is deliberate: the descendant deletes join
    -- these key sets rather than re-reading dbo.HandlerSource, so no statement below depends on a
    -- parent row still looking live after an earlier statement has deleted it.
    DECLARE @Target TABLE
    (
        HandlerSourceId INT           NOT NULL PRIMARY KEY,
        HandlerId       NVARCHAR (12) NOT NULL,
        SourceType      NVARCHAR (1)  NOT NULL,
        Sequence        INT           NOT NULL,
        WasCurrent      BIT               NULL
    );

    DECLARE @TargetEpisodicWaste TABLE (HandlerSourceEpisodicWasteId INT NOT NULL PRIMARY KEY);
    DECLARE @TargetHsmActivity   TABLE (HandlerSourceHsmActivityId   INT NOT NULL PRIMARY KEY);

    -- G35. A table variable is not affected by ROLLBACK, so what the delete noticed survives a delete
    -- that then fails. See the header.
    DECLARE @Observation TABLE
    (
        Ordinal         INT IDENTITY (1, 1) NOT NULL PRIMARY KEY,
        ObservationType NVARCHAR (50)       NOT NULL,
        Severity        NVARCHAR (20)       NOT NULL,
        HandlerId       NVARCHAR (12)           NULL,
        SourceType      NVARCHAR (1)            NULL,
        ObservedValue   NVARCHAR (400)          NULL,
        Detail          NVARCHAR (4000)         NULL
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
            SET @Failure = N'@LoadRunId is required. This is the only procedure that removes anything '
                         + N'from the mirror, so the run that did it is not optional -- and '
                         + N'logs.DataQualityObservation.LoadRunId is NOT NULL.';
            THROW 50000, @Failure, 1;
        END;

        -- ARRAY, not just ISJSON, for the reason script 520 records in full: a bare object passes a
        -- plain ISJSON, OPENJSON then enumerates its properties rather than its elements, and the
        -- caller gets error 245 from an internal CAST instead of this refusal.
        IF @Elements IS NULL OR ISJSON (@Elements, ARRAY) = 0
        BEGIN
            SET @Failure = N'@Elements is NULL, is not valid JSON, or is not a JSON array. It must be '
                         + N'an array of objects carrying handlerId, sourceType and sequence -- the '
                         + N'version-grained natural key -- even for a single element. An empty array '
                         + N'[] is accepted and does nothing.';
            THROW 50000, @Failure, 1;
        END;

        -- @Reason is required and is required to say something. A delete with no recoverable reason is
        -- the failure mode this whole procedure exists to prevent, and 'x' satisfies NOT NULL.
        IF @Reason IS NULL OR LEN (TRIM (@Reason)) < 10
        BEGIN
            SET @Failure = CONCAT (N'@Reason is required and must be at least 10 characters. It was '
                                 , COALESCE (N'''' + @Reason + N'''', N'NULL')
                                 , N'. It is the only record of WHY a set of versions left the mirror '
                                 , N'-- it is written to logs.ExecutionLog and nowhere else, because '
                                 , N'dbo.HandlerSource mirrors EPA''s schema and holds no column for '
                                 , N'our workflow. Say what happened: which reconcile, which feed, '
                                 , N'which decision.');
            THROW 50000, @Failure, 1;
        END;

        -- The run has to exist, be live, and still be Running, for the reason script 520 gives.
        SELECT @RunStatus = r.Status
          FROM logs.LoadRun AS r
         WHERE r.LoadRunId = @LoadRunId
           AND r.IsDeleted = 0;

        IF @RunStatus IS NULL
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' does not exist, or has been '
                                 , N'soft-deleted. Open a run with logs.uspStartLoadRun and delete '
                                 , N'against the LoadRunId it returns.');
            THROW 50000, @Failure, 1;
        END;

        IF @RunStatus <> N'Running'
        BEGIN
            SET @Failure = CONCAT (N'LoadRunId ', @LoadRunId, N' has Status ''', @RunStatus
                                 , N''', so it is closed and no further work can be recorded against '
                                 , N'it. A delete attributed to a finished run is a delete nobody can '
                                 , N'trace, which is the one thing this procedure must not produce.');
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 2. Shred. JSON_VALUE rather than OPENJSON ... WITH, so nothing is truncated on the way in.
        -- -----------------------------------------------------------------------------------------
        INSERT INTO @Element (Ordinal, HandlerId, SourceType, Sequence)
        SELECT CAST (e.[key] AS INT) + 1
             , JSON_VALUE (e.value, '$.handlerId')
             , JSON_VALUE (e.value, '$.sourceType')
             , TRY_CAST (JSON_VALUE (e.value, '$.sequence') AS INT)
          FROM OPENJSON (@Elements) AS e;

        SET @ElementCount = @@ROWCOUNT;
        SET @KeyParameters = CONCAT (@KeyParameters, N', Elements=', @ElementCount);

        -- -----------------------------------------------------------------------------------------
        -- 3. The natural key, complete, within its widths, and named once.
        -- -----------------------------------------------------------------------------------------
        IF EXISTS (SELECT 1 FROM @Element
                    WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'Element ', Ordinal, N' of @Elements is missing part of '
                                            , N'the natural key. handlerId, sourceType and sequence '
                                            , N'are all required and identify the version being '
                                            , N'deleted: handlerId='
                                            , COALESCE (N'''' + HandlerId + N'''', N'NULL')
                                            , N', sourceType='
                                            , COALESCE (N'''' + SourceType + N'''', N'NULL')
                                            , N', sequence='
                                            , COALESCE (CAST (Sequence AS NVARCHAR (11)), N'NULL')
                                            , N'. There is no handler-level delete: pass every version '
                                            , N'of a handler that is leaving the mirror.')
              FROM @Element
             WHERE HandlerId IS NULL OR SourceType IS NULL OR Sequence IS NULL
             ORDER BY Ordinal;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element WHERE LEN (HandlerId) > 12 OR LEN (SourceType) > 1)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'Element ', Ordinal, N' of @Elements does not fit '
                                            , N'dbo.HandlerSource: handlerId is ', LEN (HandlerId)
                                            , N' characters (max 12) and sourceType is '
                                            , LEN (SourceType), N' (max 1). The set is refused rather '
                                            , N'than truncated, because a shortened handlerId is a '
                                            , N'DIFFERENT handler and this procedure would delete '
                                            , N'their version and everything hanging off it.')
              FROM @Element
             WHERE LEN (HandlerId) > 12 OR LEN (SourceType) > 1
             ORDER BY Ordinal;
            THROW 50000, @Failure, 1;
        END;

        IF EXISTS (SELECT 1 FROM @Element
                   GROUP BY HandlerId, SourceType, Sequence
                   HAVING COUNT (*) > 1)
        BEGIN
            SELECT TOP (1) @Failure = CONCAT (N'@Elements names the same version ', COUNT (*)
                                            , N' times: handlerId=''', HandlerId, N''', sourceType='''
                                            , SourceType, N''', sequence=', Sequence
                                            , N'. A duplicate is harmless to the write -- the second '
                                            , N'copy finds the row already deleted -- and it is '
                                            , N'refused anyway, because a caller that named a version '
                                            , N'twice does not know what it is deleting.')
              FROM @Element
             GROUP BY HandlerId, SourceType, Sequence
             HAVING COUNT (*) > 1
             ORDER BY HandlerId, SourceType, Sequence;
            THROW 50000, @Failure, 1;
        END;

        -- -----------------------------------------------------------------------------------------
        -- 4. Resolve the target keys. Live rows only: an element naming an already-deleted version is
        --    not an error, it is a re-run, and it is reported as notFound in Comments.
        -- -----------------------------------------------------------------------------------------
        BEGIN TRANSACTION;

        INSERT INTO @Target (HandlerSourceId, HandlerId, SourceType, Sequence, WasCurrent)
        SELECT h.HandlerSourceId, h.HandlerId, h.SourceType, h.Sequence, h.CurrentRecord
          FROM dbo.HandlerSource AS h
          JOIN @Element          AS e
            ON h.HandlerId  = CAST (e.HandlerId  AS NVARCHAR (12))
           AND h.SourceType = CAST (e.SourceType AS NVARCHAR (1))
           AND h.Sequence   = e.Sequence
         WHERE h.IsDeleted = 0;

        SET @Targeted = @@ROWCOUNT;

        INSERT INTO @TargetEpisodicWaste (HandlerSourceEpisodicWasteId)
        SELECT w.HandlerSourceEpisodicWasteId
          FROM dbo.HandlerSourceEpisodicWaste AS w
          JOIN @Target                        AS t ON t.HandlerSourceId = w.HandlerSourceId;

        INSERT INTO @TargetHsmActivity (HandlerSourceHsmActivityId)
        SELECT a.HandlerSourceHsmActivityId
          FROM dbo.HandlerSourceHsmActivity AS a
          JOIN @Target                      AS t ON t.HandlerSourceId = a.HandlerSourceId;

        -- -----------------------------------------------------------------------------------------
        -- 5. The grandchildren, first. Their key sets are already resolved, so the order below is
        --    about readability rather than correctness -- but deepest-first is the order that stays
        --    correct if anyone ever changes these joins to re-read a parent.
        --
        --    Every statement in sections 5 and 6 has the same shape, and the shape is the contract:
        --    guarded by IsDeleted = 0 so a re-run is a no-op, and stamping the DELETED pair AND the
        --    MODIFIED pair, because a soft delete is also a modification (Phase1-Analysis.md).
        --    build/check_cascade_coverage.py reads the descendant list out of sys.foreign_keys and
        --    fails if any table in it is missing from this file.
        -- -----------------------------------------------------------------------------------------
        SET @Children = 0;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceEpisodicWasteFederalWasteCode AS c
          JOIN @TargetEpisodicWaste AS t
            ON t.HandlerSourceEpisodicWasteId = c.HandlerSourceEpisodicWasteId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceEpisodicWasteStateWasteCode AS c
          JOIN @TargetEpisodicWaste AS t
            ON t.HandlerSourceEpisodicWasteId = c.HandlerSourceEpisodicWasteId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceHsmActivityWasteCode AS c
          JOIN @TargetHsmActivity AS t
            ON t.HandlerSourceHsmActivityId = c.HandlerSourceHsmActivityId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 6. The sixteen children, keyed by HandlerSourceId. Alphabetical, so that a missing table is
        --    visible by reading rather than by trusting the count.
        -- -----------------------------------------------------------------------------------------
        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceAdditionalContact AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceCertification AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceEpisodicProject AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceEpisodicWaste AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceHsmActivity AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceLqgConsolidationVsqg AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceNaicsOther AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceOperator AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceOwner AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourcePermitOtherPermit AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        -- dbo.HandlerSourceRawJson holds the payload the version was built from. It goes with the
        -- version: keeping it live would leave the mirror able to hand back the full JSON of a record
        -- nobody is allowed to see, which is the same disclosure the parent delete just prevented.
        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceRawJson AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceStateDistrictCounty AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceWasteFederalWasteCode AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceWasteStateActivity AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceWasteStateWasteCode AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        UPDATE c
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSourceWasteUniversalWaste AS c
          JOIN @Target AS t ON t.HandlerSourceId = c.HandlerSourceId
         WHERE c.IsDeleted = 0;
        SET @Children += @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 7. The parent versions themselves, last -- so that a failure anywhere in the cascade rolls
        --    back a transaction in which no parent was ever marked deleted.
        --
        --    CurrentRecord is deliberately NOT touched. Superseded and deleted are different states
        --    (Phase1-Analysis.md [R4]), and clearing the flag on the way out would destroy the record
        --    of which version was current at the moment it was withdrawn.
        -- -----------------------------------------------------------------------------------------
        UPDATE h
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerSource AS h
          JOIN @Target           AS t ON t.HandlerSourceId = h.HandlerSourceId
         WHERE h.IsDeleted = 0;

        SET @Parents = @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 8. dbo.HandlerOtherIdentifier is handler-grained and has no HandlerSourceId, so it is not
        --    part of the cascade above. It goes only when the handler has no live version left at
        --    all, which is the moment the handler itself is gone from the mirror. Scoped to the
        --    handlers this call touched, so a handler nobody mentioned is never examined.
        -- -----------------------------------------------------------------------------------------
        UPDATE o
           SET IsDeleted            = 1
             , auditDeletedBy       = @ActorLogin
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM dbo.HandlerOtherIdentifier AS o
          JOIN (SELECT DISTINCT HandlerId FROM @Target) AS d ON d.HandlerId = o.HandlerId
         WHERE o.IsDeleted = 0
           AND NOT EXISTS (SELECT 1
                             FROM dbo.HandlerSource AS h
                            WHERE h.HandlerId = o.HandlerId
                              AND h.IsDeleted = 0);

        SET @OtherIds = @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 9. The one consequence worth reporting: a handler that still has live versions but no
        --    current one, because the version this call deleted was the current one. Measured after
        --    the delete, against the table, and scoped to the handlers this call touched.
        --
        --    [R47] ASKED PER HANDLER, NOT PER (HandlerId, SourceType), and re-grained here on
        --    2026-09-08 for the same reason script 521's section 6b was: EPA flags currency once per
        --    HANDLER rather than once per source type, so a handler still current under some other
        --    source type has NOT vanished from dbo.vwHandlerSource and reporting it would be false.
        --    The grain has to match 521's exactly, because this procedure and 521 write the SAME
        --    ObservationType into the same table -- and the same type meaning one thing from one
        --    writer and another from the other is worse than either grain alone. The scope is still
        --    only the handlers whose deleted version WAS the current one; the aggregate reads all of
        --    their source types, which is the question rather than scope creep.
        -- -----------------------------------------------------------------------------------------
        INSERT INTO @Observation (ObservationType, Severity, HandlerId, SourceType, ObservedValue
                                , Detail)
        SELECT N'CurrentRecordMissingInMirror'
             , N'Warning'
             , g.HandlerId
             -- SourceType NULL: the finding is about the handler. Naming the source type whose
             -- current version was deleted would re-assert the grain this check just abandoned.
             , NULL
             , LEFT (CONCAT (g.LiveVersions, N' live version(s) remain across '
                           , g.LiveSourceTypes, N' source type(s), none current'), 400)
             , N'This call soft-deleted the version that was flagged CurrentRecord = 1, no live '
             + N'version of this handler is now flagged current under ANY source type, and other '
             + N'versions of the handler are still live -- so the handler is in dbo.HandlerSource and '
             + N'absent from dbo.vwHandlerSource, which every default read and the Phase 2 ETS '
             + N'migration cannot tell from a handler that was never loaded. The delete was still '
             + N'correct: if EPA withdrew the current version then the current version is withdrawn, '
             + N'and refusing would leave the mirror knowingly wrong. The fix is a reconcile against '
             + N'the handler''s fresh summary list -- dbo.uspReconcileCurrentRecord -- which is the '
             + N'same fix this observation calls for when a reconcile raises it.'
          FROM (SELECT h.HandlerId
                     , LiveVersions    = COUNT (*)
                     , LiveSourceTypes = COUNT (DISTINCT h.SourceType)
                  FROM dbo.HandlerSource AS h
                  JOIN (SELECT DISTINCT HandlerId FROM @Target
                         WHERE WasCurrent = 1) AS p
                    ON p.HandlerId = h.HandlerId
                 WHERE h.IsDeleted = 0
                 GROUP BY h.HandlerId
                HAVING SUM (CASE WHEN h.CurrentRecord = 1 THEN 1 ELSE 0 END) = 0) AS g;

        -- The successful-path flush, inside the transaction: on this path the delete these rows
        -- describe is about to commit, so the observations and the delete stand or fall together.
        --
        -- HandlerLoadStatusId IS OMITTED AND THAT IS DELIBERATE, unlike the omission [R47] found in
        -- script 521. Every observation this procedure raises is handler-grained -- SourceType and
        -- Sequence are both NULL after the [R47] re-grain above -- so there is no single
        -- logs.HandlerLoadStatus row to point at. The column stays NULL because nothing determines
        -- it, not because nobody thought to set it.
        INSERT INTO logs.DataQualityObservation
              (LoadRunId, ObservationType, Severity, HandlerId, SourceType
             , TableName, ColumnName, ObservedValue, Detail, ObservedDateUtc)
        SELECT @LoadRunId
             , o.ObservationType
             , o.Severity
             , o.HandlerId
             , o.SourceType
             , N'dbo.HandlerSource'
             , N'CurrentRecord'
             , o.ObservedValue
             , o.Detail
             , @NowUtc
          FROM @Observation AS o
         ORDER BY o.Ordinal;

        SET @ObservationsWritten = @@ROWCOUNT;

        -- -----------------------------------------------------------------------------------------
        -- 10. AR5 per-handler status, INSIDE the transaction, for the same reason script 400 puts its
        --     status half inside its own: 'this version was soft-deleted' is true only if the delete
        --     it names actually commits. Script 400 marks a merged version Succeeded/Inserted;
        --     nothing marked a WITHDRAWN one until this step existed, so a version that answered 404
        --     was deleted correctly and left its status row reading InProgress forever. Outcome
        --     N'SoftDeleted' has been in CK_logs_HandlerLoadStatus_Outcome since script 310 and this
        --     is the only statement in the database that writes it.
        --
        --     Status is N'Succeeded' rather than N'Failed' deliberately: the column records whether
        --     the run DEALT WITH the version, and a 404 was dealt with. Marking it Failed would make
        --     the next run re-fetch a record that will still be absent tomorrow, forever. The
        --     distinction between 'fetched and merged' and 'withdrawn' survives in Outcome, which is
        --     what Outcome is for.
        --
        --     UPDATE, never INSERT -- the same rule as script 400 step 5. These rows are written
        --     Pending when the run ENUMERATES its work, so a deleted version with no row here was
        --     never enumerated by this run, which is the ordinary case for a reconciliation-driven
        --     delete (G23) and a loader defect for a 404-driven one. Inserting a row would
        --     manufacture the evidence for the second, so the shortfall is reported in Comments.
        --
        --     Unlike script 520's Fail and Skip modes, an already-Succeeded row is NOT excluded. A
        --     version merged earlier in this same run and withdrawn by a later reconciliation really
        --     was deleted, and the last thing the run did to it is what the row should say. The
        --     Outcome guard is what keeps the statement convergent for its own sake; @Target being
        --     resolved from live rows only is what makes a second identical call touch nothing at all.
        -- -----------------------------------------------------------------------------------------
        UPDATE s
           SET Status               = N'Succeeded'
             , Outcome              = N'SoftDeleted'
             , CompletedDateUtc     = @NowUtc
             , ExecutionLogId       = @ExecutionId
             , auditModifiedBy      = @ActorLogin
             , auditModifiedDateUtc = @NowUtc
          FROM logs.HandlerLoadStatus AS s
          JOIN @Target               AS t
            ON s.HandlerId  = t.HandlerId
           AND s.SourceType = t.SourceType
           AND s.Sequence   = t.Sequence
         WHERE s.LoadRunId = @LoadRunId
           AND s.IsDeleted = 0
           AND (s.Status IS DISTINCT FROM N'Succeeded'
             OR s.Outcome IS DISTINCT FROM N'SoftDeleted');

        SET @StatusRows = @@ROWCOUNT;

        -- @Target is unique on HandlerSourceId and logs.HandlerLoadStatus is unique on
        -- (LoadRunId, HandlerId, SourceType, Sequence), so the join is 1:1 at most and this
        -- subtraction cannot go negative.
        SET @StatusMissing = @Parents - @StatusRows;

        SET @RowsAffected = @Parents;
        SET @ChildRows    = @Children + @OtherIds;

        SET @Comments = CONCAT (N'elements=', @ElementCount
                              , N', versionsDeleted=', @Parents
                              , N', descendantRows=', @Children
                              , N', otherIdentifiers=', @OtherIds
                              , N', notFound=', @ElementCount - @Targeted
                              , N', observations=', @ObservationsWritten
                              -- Named even when zero, like script 400's absentCollections: 'status='
                              -- is the figure an operator checks to see that the audit trail kept up
                              -- with the delete, and a number that appears only when it is non-zero
                              -- cannot be checked for.
                              , N', status=', @StatusRows
                              , CASE WHEN @StatusMissing > 0
                                     THEN CONCAT (N' (', @StatusMissing, N' not enumerated)')
                                     ELSE N'' END
                              , N', reason=', @Reason);

        -- =========================================================================================
        -- ===== End of the procedure's own work. ===================================================
        -- =========================================================================================

        IF @@TRANCOUNT > 0
        BEGIN
            COMMIT TRANSACTION;
        END;

        SET @Committed = 1;

        -- Completion. Deliberately after the COMMIT; see the template header for what that costs. A
        -- retry provoked by a failure here is safe: every UPDATE above is guarded by IsDeleted = 0 and
        -- finds nothing the second time.
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

        -- G35's flush, guarded by @Committed so a failure in the completion UPDATE -- which runs after
        -- the COMMIT, with the observations already written -- does not double them.
        BEGIN TRY
            IF @Committed = 0
            BEGIN
                INSERT INTO logs.DataQualityObservation
                      (LoadRunId, ObservationType, Severity, HandlerId, SourceType
                     , TableName, ColumnName, ObservedValue, Detail, ObservedDateUtc)
                SELECT @LoadRunId
                     , o.ObservationType
                     , o.Severity
                     , o.HandlerId
                     , o.SourceType
                     , N'dbo.HandlerSource'
                     , N'CurrentRecord'
                     , o.ObservedValue
                     , LEFT (CONCAT (o.Detail
                                   , N' NOTE: the delete that would have caused this then FAILED and '
                                   , N'rolled back, so no version was deleted and the handler is '
                                   , N'unchanged. The row is kept because the finding is evidence '
                                   , N'about what the delete was ABOUT to do. See logs.ExecutionLog '
                                   , N'for the error.'), 4000)
                     , @NowUtc
                  FROM @Observation AS o
                 ORDER BY o.Ordinal;

                SET @ObservationsWritten = @@ROWCOUNT;
            END;
        END TRY
        BEGIN CATCH
            -- Swallowed on purpose: these rows are secondary to the error below, and a failure
            -- writing them must not replace the error the caller needs to see. Back to NULL, so
            -- ContextMessage cannot report a count of rows that were not written -- most likely
            -- because the caller's own transaction is doomed and nothing can be inserted from here.
            SET @ObservationsWritten = NULL;
        END CATCH;

        -- On the rollback path the counts describe writes that no longer exist. Zero them, so a
        -- caller that logs @RowsAffected without checking for the exception cannot report a delete
        -- that was undone. Left alone when @Committed = 1, where they are still true.
        IF @Committed = 0
        BEGIN
            SET @RowsAffected = 0;
            SET @ChildRows    = 0;
        END;

        SET @ContextMessage = CONCAT (N'elements=', @ElementCount
                                    , N', targeted=', @Targeted
                                    , N', observationsPreserved='
                                    , COALESCE (CAST (@ObservationsWritten AS NVARCHAR (11)), N'none')
                                    , CASE WHEN @Committed = 1
                                           THEN N', the delete COMMITTED and the failure is in the '
                                              + N'completion update; the rows ARE deleted.'
                                           ELSE N', nothing was deleted (rolled back).' END);

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
      @SchemaName  = N'dbo'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSoftDeleteHandlerSourceSet'
    , @Description = N'The only procedure that sets IsDeleted on dbo.HandlerSource, and therefore the only way anything leaves the mirror -- there is no hard delete anywhere in this database (AR7). Takes a JSON @Elements array of handlerId/sourceType/sequence, resolves the live versions it names, and soft-deletes each one together with every descendant row that hangs off it: three grandchildren keyed through dbo.HandlerSourceEpisodicWaste and dbo.HandlerSourceHsmActivity, then sixteen children keyed by HandlerSourceId, then the parent versions last, so a failure mid-cascade rolls back a transaction in which no parent was ever marked deleted. The cascade is mandatory rather than tidy: only the two dbo.vwHandlerSource* views exist, so a read of any child table filters that child''s own IsDeleted, and a parent deleted without its children leaves nineteen tables of rows that read as live and belong to a version nobody may see. build/check_cascade_coverage.py derives the descendant list from sys.foreign_keys and fails if this procedure does not cover every table in it. DELETED IS NOT SUPERSEDED (Phase1-Analysis.md [R4]): superseded is CurrentRecord = 0 and deleted is IsDeleted = 1, so this procedure never touches CurrentRecord and dbo.uspReconcileCurrentRecord never deletes -- and CurrentRecord is left as it stood, because clearing it on the way out would destroy the record of which version was current when it was withdrawn. dbo.HandlerOtherIdentifier is handler-grained, holds no HandlerSourceId, and is soft-deleted only when the delete leaves the handler with no live version at all. @Reason is required and at least 10 characters; it is written to logs.ExecutionLog and deliberately not to the row, since dbo.HandlerSource mirrors EPA''s schema and holds no column for our workflow. Deleting the current version while others survive makes the handler vanish from dbo.vwHandlerSource, so a CurrentRecordMissingInMirror observation is recorded -- the same type dbo.uspReconcileCurrentRecord uses, because the fix is the same. [R47] That check is asked PER HANDLER and not per (HandlerId, SourceType), re-grained 2026-09-08 in step with script 521 section 6b: EPA flags currency once per handler, so a handler still current under another source type has not left the view, and because the ObservationType is SHARED between the two procedures the grain has to be shared too -- the monitoring app cannot tell which procedure wrote a row. HandlerLoadStatusId is left NULL on these rows because every observation here is handler-grained and no single status row determines it. G35 applies: observations accumulate in a table variable and are flushed in the CATCH with a note that the delete never happened. Every UPDATE is guarded by IsDeleted = 0, so a second identical call writes nothing and does not re-stamp auditDeletedDateUtc. An empty array is a no-op. The run must exist, be live, and still be Running. @Elements is excluded from @KeyParameters by name. EXECUTE is granted to RCRAInfoLoaderRole only.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. The loader deletes; the monitoring web app must not be able to, in Phase 1 or after it --
-- this is the one procedure whose leak would remove data rather than expose it.
--
-- Ownership chaining carries every UPDATE in the cascade, the read of logs.LoadRun, the inserts into
-- logs.DataQualityObservation, and the chained inserts into logs.ExecutionLog through this single
-- grant, so the loader login holds no direct permission on any of the twenty-two tables (AR3).
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON dbo.uspSoftDeleteHandlerSourceSet TO RCRAInfoLoaderRole;
END;
GO

PRINT N'522: dbo.uspSoftDeleteHandlerSourceSet created or altered, EXECUTE granted to the loader role.';
GO
