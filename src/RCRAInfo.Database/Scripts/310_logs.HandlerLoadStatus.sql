/**********************************************************************************************************************
Script:       310_logs.HandlerLoadStatus.sql
Author:       hand-authored
CreateDate:   2026-09-04
========================================================================================================================
Description:

AR5's per-handler status tracking. One row per source record per run, at the grain
(LoadRunId, HandlerId, SourceType, Sequence) -- version-grained, matching dbo.HandlerSource.

This table is the RESUMABILITY MECHANISM, not an after-the-fact log, and the distinction decides its shape. Rows are
written as Status = 'Pending' when the run ENUMERATES its work, before any of it is attempted, so a run that dies
partway restarts by reading what it already recorded rather than by asking EPA again. EPA offers no bulk export and the
Handler fetch is N+1, which makes a full load long enough that dying partway is an expected event, not a disaster.

WHY THE GRAIN INCLUDES LoadRunId. Status is kept per run historically (Phase1-Plan.md B3), so the whole attempt history
of a source record survives. The Analysis floated a second, current-state table for the monitoring UI to query cheaply;
it is deliberately NOT built here. IX_logs_HandlerLoadStatus_Handler makes "the latest state of this source record" a
seek plus TOP 1, and a second table holding the same state would be a copy that drifts. If measurement later shows the
UI needs it, adding it is additive -- whereas changing this grain would not be.

WHY THE ETS COLUMNS ARE HERE NOW, UNUSED. Phase 2 migrates this data to MDE's ETS system, and by then this table will
hold significant history. Adding a column to it later is easy; changing its grain is not. The Ets* columns are defaulted
and untouched in Phase 1 (AR5). The unit Phase 2 migrates is a dbo.HandlerSource VERSION, which is why HandlerSourceId
is on this row: Phase 2 joins on it rather than re-deriving the version from the natural key.

========================================================================================================================
Notes:

G7 IS STILL OPEN and this is the table it is about. This grows by roughly one row per source record per run, and AR7
forbids hard-deleting any of it. A retention or archive policy is needed before the first long-running production
schedule, not after. IsDeleted is the only mechanism available for it.

ApiErrorDate carries no Utc suffix on purpose. EPA declares ApiError.errorDate as format: date-time but states no time
zone, so naming it *Utc would assert something we have not been told. Every column this loader stamps itself does carry
the suffix, and does mean UTC.

RE-RUNNABLE. Every CREATE is guarded, descriptions go through util.uspSetObjectDescription (which adds or updates), and
nothing is dropped or truncated. A second run changes nothing.

Later changes are ADDITIVE: append a guarded ALTER TABLE ... ADD block rather than editing the CREATE TABLE below, or
the script stops converging on a database that already has the table.

Modification History:
    2026-09-04  Initial version (Workstream B3).
    2026-09-05  DA1/G35: added ExecutionLogId (section 7, additive) so a Failed status row reaches the
                reason it failed. See that section for why it carries no foreign key.
**********************************************************************************************************************/

SET XACT_ABORT ON;
-- Not decoration: sqlcmd defaults QUOTED_IDENTIFIER OFF where every other client defaults it ON, the
-- setting is BAKED IN at CREATE time, and a module or session carrying it OFF cannot run DML against a
-- table with a filtered index (error 1934). Every unique constraint here is one. Set it so that a hand
-- run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

----------------------------------------------------------------------------------------------------
-- 1. The table. Guarded, so a second run is a no-op.
----------------------------------------------------------------------------------------------------
IF OBJECT_ID (N'logs.HandlerLoadStatus', N'U') IS NULL
BEGIN
    CREATE TABLE logs.HandlerLoadStatus
    (
        HandlerLoadStatusId        INT             IDENTITY (1, 1) NOT NULL,
        LoadRunId                  INT                             NOT NULL,
        HandlerId                  NVARCHAR (12)                   NOT NULL,
        ActivityLocation           NVARCHAR (2)                    NOT NULL,
        SourceType                 NVARCHAR (1)                    NOT NULL,
        Sequence                   INT                             NOT NULL,
        HandlerSourceId            INT                                 NULL,
        Status                     NVARCHAR (20)                   NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_Status DEFAULT (N'Pending'),
        Outcome                    NVARCHAR (20)                       NULL,
        AttemptCount               INT                             NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_AttemptCount DEFAULT (0),
        HttpStatusCode             INT                                 NULL,
        ApiErrorCode               NVARCHAR (100)                      NULL,
        ApiErrorMessage            NVARCHAR (4000)                     NULL,
        ApiErrorId                 NVARCHAR (50)                       NULL,
        ApiErrorDate               DATETIME2                           NULL,
        PayloadSha256              NVARCHAR (64)                       NULL,
        FirstSeenDateUtc           DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_FirstSeenDateUtc DEFAULT (SYSUTCDATETIME ()),
        LastAttemptStartedDateUtc  DATETIME2                           NULL,
        CompletedDateUtc           DATETIME2                           NULL,
        DurationMs                 INT                                 NULL,
        EtsStatus                  NVARCHAR (20)                   NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_EtsStatus DEFAULT (N'NotMigrated'),
        EtsAttemptCount            INT                             NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_EtsAttemptCount DEFAULT (0),
        EtsErrorMessage            NVARCHAR (4000)                     NULL,
        EtsLastAttemptDateUtc      DATETIME2                           NULL,
        EtsMigratedDateUtc         DATETIME2                           NULL,
        IsDeleted                  BIT                             NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_IsDeleted DEFAULT (0),
        auditDeletedBy             NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_auditDeletedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditDeletedDateUtc        DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_auditDeletedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditCreatedBy             NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_auditCreatedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditCreatedDateUtc        DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_auditCreatedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditModifiedBy            NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_auditModifiedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditModifiedDateUtc       DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadStatus_auditModifiedDateUtc DEFAULT (SYSUTCDATETIME ()),

        CONSTRAINT PK_logs_HandlerLoadStatus PRIMARY KEY CLUSTERED (HandlerLoadStatusId),

        -- Our own values, so the sets are closed. The same constraints would be wrong on the
        -- dbo.HandlerSource tree, where a value EPA invents next quarter must load rather than fail.
        CONSTRAINT CK_logs_HandlerLoadStatus_Status CHECK (Status IN (N'Pending', N'InProgress', N'Succeeded',
                                                                     N'Failed', N'Skipped')),
        CONSTRAINT CK_logs_HandlerLoadStatus_Outcome CHECK (Outcome IS NULL
                                                            OR Outcome IN (N'Inserted', N'Updated', N'Unchanged',
                                                                           N'SoftDeleted')),
        CONSTRAINT CK_logs_HandlerLoadStatus_EtsStatus CHECK (EtsStatus IN (N'NotMigrated', N'Pending', N'InProgress',
                                                                           N'Migrated', N'Failed', N'NotApplicable'))
    )
    WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 2. Natural key.
--    A filtered unique index, not a unique constraint: a soft-deleted row keeps its key, and
--    an unfiltered constraint would block re-creation of that key forever -- here that would
--    mean a retention pass making a run number unusable.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'UX_logs_HandlerLoadStatus_Natural'
                  AND object_id = OBJECT_ID (N'logs.HandlerLoadStatus'))
BEGIN
    CREATE UNIQUE INDEX UX_logs_HandlerLoadStatus_Natural
        ON logs.HandlerLoadStatus (LoadRunId, HandlerId, SourceType, Sequence)
        WHERE IsDeleted = 0
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'UX_logs_HandlerLoadStatus_Natural'
              AND i.object_id = OBJECT_ID (N'logs.HandlerLoadStatus')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX UX_logs_HandlerLoadStatus_Natural
        ON logs.HandlerLoadStatus REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 3. The foreign key column, unfiltered.
--    Deliberately not redundant with the index above, which is filtered to IsDeleted = 0 and
--    so cannot serve a read that has to see soft-deleted rows. Retention (G7) works through
--    IsDeleted, which means every row this table ever retires is reachable only this way.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_HandlerLoadStatus_LoadRunId'
                  AND object_id = OBJECT_ID (N'logs.HandlerLoadStatus'))
BEGIN
    CREATE INDEX IX_logs_HandlerLoadStatus_LoadRunId
        ON logs.HandlerLoadStatus (LoadRunId)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_HandlerLoadStatus_LoadRunId'
              AND i.object_id = OBJECT_ID (N'logs.HandlerLoadStatus')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_HandlerLoadStatus_LoadRunId
        ON logs.HandlerLoadStatus REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 4. "The latest state of this source record", which is the query the monitoring UI actually
--    asks and the reason no separate current-state table exists. Leading with the natural key
--    and trailing with LoadRunId DESC turns it into a seek plus TOP 1.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_HandlerLoadStatus_Handler'
                  AND object_id = OBJECT_ID (N'logs.HandlerLoadStatus'))
BEGIN
    CREATE INDEX IX_logs_HandlerLoadStatus_Handler
        ON logs.HandlerLoadStatus (HandlerId, SourceType, Sequence, LoadRunId DESC)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_HandlerLoadStatus_Handler'
              AND i.object_id = OBJECT_ID (N'logs.HandlerLoadStatus')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_HandlerLoadStatus_Handler
        ON logs.HandlerLoadStatus REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 5. The version this outcome landed as. Phase 2 migrates by joining on this column, so it
--    is indexed now rather than discovered to be missing under a migration.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_HandlerLoadStatus_HandlerSourceId'
                  AND object_id = OBJECT_ID (N'logs.HandlerLoadStatus'))
BEGIN
    CREATE INDEX IX_logs_HandlerLoadStatus_HandlerSourceId
        ON logs.HandlerLoadStatus (HandlerSourceId)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_HandlerLoadStatus_HandlerSourceId'
              AND i.object_id = OBJECT_ID (N'logs.HandlerLoadStatus')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_HandlerLoadStatus_HandlerSourceId
        ON logs.HandlerLoadStatus REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 6. Foreign keys. NO ACTION on delete, not CASCADE: this database performs no hard deletes,
--    so a cascade could never fire and declaring one would advertise a behaviour that does
--    not exist.
--
--    The reference to dbo.HandlerSource is nullable and stays nullable: a Pending row exists
--    before anything has been fetched, and a Failed row never produces a version at all.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_logs_HandlerLoadStatus_LoadRun')
BEGIN
    ALTER TABLE logs.HandlerLoadStatus
        ADD CONSTRAINT FK_logs_HandlerLoadStatus_LoadRun FOREIGN KEY (LoadRunId)
            REFERENCES logs.LoadRun (LoadRunId);
END;
GO

IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_logs_HandlerLoadStatus_HandlerSource')
BEGIN
    ALTER TABLE logs.HandlerLoadStatus
        ADD CONSTRAINT FK_logs_HandlerLoadStatus_HandlerSource FOREIGN KEY (HandlerSourceId)
            REFERENCES dbo.HandlerSource (HandlerSourceId);
END;
GO

----------------------------------------------------------------------------------------------------
-- 7. ADDITIVE (DA1, G35): the link from a status row to the call that produced it.
--
--    Guarded and appended rather than folded into the CREATE TABLE above, per the convention at the
--    top of this script -- editing section 1 would stop this script converging on a database that
--    already has the table.
--
--    Why the column is needed at all. G35's answer is that dbo.uspMergeHandlerSourceBatch stamps
--    these rows 'Failed' from its CATCH block, after the rollback, out of a table variable the
--    rollback did not unwind. That gives AR5 the "which of five hundred handlers failed" half. The
--    "and why" half was missing: this table has no column for OUR OWN failure text, and reusing
--    ApiErrorMessage for it is forbidden -- that column is EPA's, stored as received, and a
--    database-side error written into it would read as something EPA said. The error already lives
--    in logs.ExecutionLog, in the row the resurrection block guarantees survives the rollback, so
--    what was actually missing was the pointer to it.
--
--    Deliberately NO foreign key to logs.ExecutionLog. This is the one column in the database where
--    a constraint would defeat its own purpose: the flush that writes it runs inside a nested
--    TRY/CATCH that swallows, because an error escaping there would replace the error being
--    reported. So an FK violation would not raise -- it would silently discard the per-handler
--    status rows, and it would do so in exactly the failure mode G35 exists to cover, the one where
--    the log row could not be written or re-created either. The flush has to be the most robust
--    write in that procedure, not the most constrained one. An orphaned value here reads as
--    'the call is not in the log', which is true and worth seeing.
----------------------------------------------------------------------------------------------------
IF COL_LENGTH (N'logs.HandlerLoadStatus', N'ExecutionLogId') IS NULL
BEGIN
    ALTER TABLE logs.HandlerLoadStatus
        ADD ExecutionLogId BIGINT NULL;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'ExecutionLogId'
    , @Description = N'The logs.ExecutionLog row for the call that last set this status -- the successful merge, or the failed one. This is how a Status = ''Failed'' row reaches the reason it failed: the message is in that log row''s ErrorMessage, because this table has no column for our own failure text and ApiErrorMessage must not be borrowed for one (it is EPA''s, stored as received). Not a foreign key, on purpose: the write that sets it happens in a swallowing CATCH block, so a constraint violation would discard the status row silently instead of raising, in precisely the failure mode this column exists for. NULL when no log row could be established, which is itself worth seeing.';
GO

/*
    Extended properties. Required on the table and on EVERY column (AR6).

    Through util.uspSetObjectDescription only. It adds or updates, so this script re-runs and an
    improved wording replaces the old one; a bare sp_addextendedproperty succeeds once and then
    fails on every subsequent run.

    These columns are ours, not EPA's, so there is no [RCRAInfo: ...] provenance suffix and no
    TODO placeholder -- except where a column mirrors EPA's ApiError, which is noted in place.
*/

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @Description = N'AR5 per-handler status tracking: one row per source record per run, at the grain (LoadRunId, HandlerId, SourceType, Sequence). This is the RESUMABILITY mechanism rather than an after-the-fact log -- rows are written as Status = ''Pending'' when the run enumerates its work, so a run that dies partway restarts from what it recorded instead of asking EPA again. Status is kept per run historically; the monitoring UI gets "latest state per source record" from IX_logs_HandlerLoadStatus_Handler rather than from a second current-state table that would drift. The Ets* columns are Phase 2''s, defaulted and unused in Phase 1, reserved now because adding a column to this table later is easy and changing its grain is not. G7 is open: this table grows by one row per source record per run and nothing may be hard-deleted, so it needs a retention policy before the first long production schedule.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'HandlerLoadStatusId'
    , @Description = N'Surrogate key for one source record''s status within one run. Parent of logs.HandlerLoadAttempt.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'LoadRunId'
    , @Description = N'The logs.LoadRun execution this status belongs to. Part of the natural key: status is kept per run, so the same source record has one row per run that touched it.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'HandlerId'
    , @Description = N'EPA handler identifier this status is about. Recorded as its own column rather than only through HandlerSourceId, because a Pending or Failed row has no dbo.HandlerSource row to point at -- and those are precisely the rows the monitoring UI needs to name.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'ActivityLocation'
    , @Description = N'RCRAInfo activity location of the source record, normally ''MD'' (G2). Not constrained to ''MD'': the scope is a property of the request, not of this table.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'SourceType'
    , @Description = N'RCRAInfo source type of the record. Part of the version grain: one handler has one source record per source type per sequence, so status tracked by HandlerId alone would collapse several distinct fetches into one row.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'Sequence'
    , @Description = N'RCRAInfo sequence number of the source record. The rest of the version grain, alongside HandlerId and SourceType.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'HandlerSourceId'
    , @Description = N'The dbo.HandlerSource version this run''s outcome produced or confirmed, or NULL if nothing landed -- which is the normal state of a Pending row and the permanent state of a Failed one. Phase 2 migrates by joining on this column rather than re-deriving the version from the natural key.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'Status'
    , @Description = N'Where this source record got to in this run: ''Pending'' (enumerated, not yet attempted -- the default, and what makes resumption possible), ''InProgress'', ''Succeeded'', ''Failed'' (every retry exhausted), or ''Skipped'' (deliberately not attempted, normally because a resumed run had already completed it). Constrained by CK_logs_HandlerLoadStatus_Status.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'Outcome'
    , @Description = N'What happened in the database once the record was fetched: ''Inserted'', ''Updated'', ''Unchanged'' (the payload hash matched, so nothing was written and auditModifiedDateUtc was left alone), or ''SoftDeleted''. NULL until the record succeeds. Status says whether the fetch worked; Outcome says what it changed.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'AttemptCount'
    , @Description = N'How many attempts this record took in this run. The individual attempts are rows in logs.HandlerLoadAttempt; this is the running total, kept here so the common query does not have to aggregate the attempt log.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'HttpStatusCode'
    , @Description = N'HTTP status of the last attempt. Kept even on success, because a 200 that took three attempts to reach is worth seeing.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'ApiErrorCode'
    , @Description = N'EPA''s ApiError.code from the last failed attempt, for example E_AccessDenied. [RCRAInfo: ApiError.code]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'ApiErrorMessage'
    , @Description = N'EPA''s ApiError.message from the last failed attempt. Stored as received, and must never be rewritten to include a credential. [RCRAInfo: ApiError.message]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'ApiErrorId'
    , @Description = N'EPA''s ApiError.errorId -- the UUID that identifies this error in EPA''s own systems. The one value worth quoting verbatim in a support request. [RCRAInfo: ApiError.errorId]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'ApiErrorDate'
    , @Description = N'When EPA says the error occurred. No Utc suffix on purpose: EPA declares this as format date-time but states no time zone, so claiming UTC would assert something we have not been told. [RCRAInfo: ApiError.errorDate]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'PayloadSha256'
    , @Description = N'SHA-256 of the payload as this run received it, the same value the run writes to dbo.HandlerSourceRawJson. Recorded here so that Outcome = ''Unchanged'' is auditable: without it, "we decided nothing had changed" is a claim with no evidence behind it.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'FirstSeenDateUtc'
    , @Description = N'UTC timestamp this row was enumerated, which is when the run first knew the record was work to do. Set by DEFAULT on insert, and NOT the time of the first fetch attempt -- the gap between the two is queue depth.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'LastAttemptStartedDateUtc'
    , @Description = N'UTC timestamp the most recent attempt began. On a row stuck at ''InProgress'' this is how long it has been stuck, and therefore how a crashed run is recognised.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'CompletedDateUtc'
    , @Description = N'UTC timestamp this record reached a terminal status, whether Succeeded, Failed or Skipped. NULL while it is still Pending or InProgress.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'DurationMs'
    , @Description = N'Elapsed milliseconds across every attempt for this record in this run. The number that turns "the initial load might take days" into a measurement, which G6 needs answered.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'EtsStatus'
    , @Description = N'PHASE 2. Migration state of this version into MDE''s ETS system: ''NotMigrated'' (the default and the only value Phase 1 ever writes), ''Pending'', ''InProgress'', ''Migrated'', ''Failed'', or ''NotApplicable''. Reserved now because Phase 2 must not require a schema change to a table that will by then hold significant history. Constrained by CK_logs_HandlerLoadStatus_EtsStatus.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'EtsAttemptCount'
    , @Description = N'PHASE 2. How many times migration to ETS has been attempted for this version. Unused in Phase 1. Phase 2 crosses two SQL Servers in UAT and Production, where MSDTC is commonly disabled, so migration is designed as idempotent and at-least-once -- and a retry counter is what makes at-least-once safe to operate rather than merely intended.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'EtsErrorMessage'
    , @Description = N'PHASE 2. Why the last ETS migration attempt failed. Unused in Phase 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'EtsLastAttemptDateUtc'
    , @Description = N'PHASE 2. UTC timestamp of the last ETS migration attempt, successful or not. Unused in Phase 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'EtsMigratedDateUtc'
    , @Description = N'PHASE 2. UTC timestamp this version was confirmed landed in ETS. Unused in Phase 1. This column, not EtsStatus, is the record of what actually arrived: a status can be set optimistically, a timestamp is written after the fact.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'IsDeleted'
    , @Description = N'Soft-delete flag. 1 = deleted, 0 = active. This database performs no hard deletes; every read path filters IsDeleted = 0. On this table the flag is the only mechanism a retention policy has (G7).';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'auditDeletedBy'
    , @Description = N'Login that soft-deleted the row. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'auditDeletedDateUtc'
    , @Description = N'UTC timestamp of the soft delete. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1; the deleting statement sets it explicitly.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'auditCreatedBy'
    , @Description = N'Login that inserted the row. Under the application logins this identifies which application wrote it, not an end user.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'auditCreatedDateUtc'
    , @Description = N'UTC timestamp of row insert. Set by DEFAULT; the loader omits this column from its INSERT column list so the default fires.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'auditModifiedBy'
    , @Description = N'Login that last modified the row.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadStatus'
    , @ColumnName  = N'auditModifiedDateUtc'
    , @Description = N'UTC timestamp of last modification. The DEFAULT fires on INSERT only, so every UPDATE and MERGE must set this column explicitly or the audit trail will claim the row has never changed. On this table a row is updated on every attempt, so getting this wrong would hide the retry history it exists to record.';
GO

PRINT N'310: logs.HandlerLoadStatus created or altered, 33 column(s) described.';
GO
