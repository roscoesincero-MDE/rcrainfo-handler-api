/**********************************************************************************************************************
Script:       300_logs.LoadRun.sql
Author:       hand-authored
CreateDate:   2026-09-04
========================================================================================================================
Description:

One row per execution of the console application (AR5). Records what the run was asked to do, what it did, and how it
ended: mode, requested date range, the watermark before and after, the outcome counts, and the account that invoked it.

This is the parent of logs.HandlerLoadStatus, logs.HandlerLoadAttempt and logs.DataQualityObservation, and the row that
config.LoadWatermark points at to record which run last advanced the watermark. A run row is INSERTED AT THE START of
the run, with Status = 'Running', not written at the end -- a run that dies without completing has to leave a trace, and
a row written only on success cannot record a failure.

There is deliberately NO natural key and no unique index beyond the primary key. Two runs may legitimately be started
with identical mode, location and date range: after an operator cancels one, or when a run is retried by hand. Asserting
uniqueness over those columns would reject the second, honest attempt.

NOT hand-authored data: every column here is written by our own loader, not mirrored from EPA, which is why Status and
RunMode carry CHECK constraints. The same constraints would be wrong on the dbo.HandlerSource tree, where a value EPA
invents next quarter must load rather than fail.

========================================================================================================================
Notes:

ActivityLocation is NOT constrained to 'MD' even though G2 scopes the load to Maryland. The scope is a property of the
request the loader makes, not of this table, and a CHECK here would have to be found and altered before anyone could
run a single out-of-state batch for comparison.

RE-RUNNABLE. Every CREATE is guarded, descriptions go through util.uspSetObjectDescription (which adds or updates), and
nothing is dropped or truncated. A second run changes nothing.

Later changes are ADDITIVE: append a guarded ALTER TABLE ... ADD block rather than editing the CREATE TABLE below, or
the script stops converging on a database that already has the table.

Modification History:
    2026-09-04  Initial version (Workstream B3).
    2026-09-06  CK_logs_LoadRun_RunMode widened to admit 'Targeted', for the single-handler run the
                loader's --handler-id switch starts. Section 5 converges a database that already has
                the narrow constraint; the CREATE TABLE carries the wide form for a fresh one.
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
IF OBJECT_ID (N'logs.LoadRun', N'U') IS NULL
BEGIN
    CREATE TABLE logs.LoadRun
    (
        LoadRunId                INT             IDENTITY (1, 1) NOT NULL,
        RunMode                  NVARCHAR (20)                   NOT NULL,
        ActivityLocation         NVARCHAR (2)                    NOT NULL,
        RequestedFromDate        DATE                                NULL,
        RequestedToDate          DATE                                NULL,
        WatermarkBeforeDate      DATE                                NULL,
        WatermarkAfterDate       DATE                                NULL,
        OverlapDaysApplied       INT                                 NULL,
        Status                   NVARCHAR (20)                   NOT NULL CONSTRAINT DF_logs_LoadRun_Status DEFAULT (N'Running'),
        StartedDateUtc           DATETIME2                       NOT NULL CONSTRAINT DF_logs_LoadRun_StartedDateUtc DEFAULT (SYSUTCDATETIME ()),
        CompletedDateUtc         DATETIME2                           NULL,
        ResumedFromLoadRunId     INT                                 NULL,
        LookupListsRefreshed     INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_LookupListsRefreshed DEFAULT (0),
        SourceRecordsEnumerated  INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsEnumerated DEFAULT (0),
        SourceRecordsFetched     INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsFetched DEFAULT (0),
        SourceRecordsInserted    INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsInserted DEFAULT (0),
        SourceRecordsUpdated     INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsUpdated DEFAULT (0),
        SourceRecordsUnchanged   INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsUnchanged DEFAULT (0),
        SourceRecordsSoftDeleted INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsSoftDeleted DEFAULT (0),
        SourceRecordsSkipped     INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsSkipped DEFAULT (0),
        SourceRecordsFailed      INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_SourceRecordsFailed DEFAULT (0),
        HttpRequestCount         INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_HttpRequestCount DEFAULT (0),
        HttpRetryCount           INT                             NOT NULL CONSTRAINT DF_logs_LoadRun_HttpRetryCount DEFAULT (0),
        FailureMessage           NVARCHAR (4000)                     NULL,
        InvokedBy                NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_LoadRun_InvokedBy DEFAULT (ORIGINAL_LOGIN ()),
        MachineName              NVARCHAR (128)                      NULL,
        ProcessId                INT                                 NULL,
        ApplicationVersion       NVARCHAR (50)                       NULL,
        IsDeleted                BIT                             NOT NULL CONSTRAINT DF_logs_LoadRun_IsDeleted DEFAULT (0),
        auditDeletedBy           NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_LoadRun_auditDeletedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditDeletedDateUtc      DATETIME2                       NOT NULL CONSTRAINT DF_logs_LoadRun_auditDeletedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditCreatedBy           NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_LoadRun_auditCreatedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditCreatedDateUtc      DATETIME2                       NOT NULL CONSTRAINT DF_logs_LoadRun_auditCreatedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditModifiedBy          NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_LoadRun_auditModifiedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditModifiedDateUtc     DATETIME2                       NOT NULL CONSTRAINT DF_logs_LoadRun_auditModifiedDateUtc DEFAULT (SYSUTCDATETIME ()),

        CONSTRAINT PK_logs_LoadRun PRIMARY KEY CLUSTERED (LoadRunId),

        -- Our own values, so the set is closed and a typo should fail loudly rather than reach the
        -- monitoring UI as a status it cannot render.
        CONSTRAINT CK_logs_LoadRun_RunMode CHECK (RunMode IN (N'Full', N'Incremental', N'Reconcile',
                                                              N'Targeted')),
        CONSTRAINT CK_logs_LoadRun_Status  CHECK (Status  IN (N'Running', N'Succeeded', N'PartiallySucceeded',
                                                             N'Failed', N'Abandoned'))
    )
    WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 2. The monitoring UI's two queries: "the most recent runs" and "runs that ended badly".
--    Not unique, and not filtered on IsDeleted: a retention pass that soft-deletes old runs
--    (G7) must not make them invisible to a query that is explicitly looking at history.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_LoadRun_StartedDateUtc'
                  AND object_id = OBJECT_ID (N'logs.LoadRun'))
BEGIN
    CREATE INDEX IX_logs_LoadRun_StartedDateUtc
        ON logs.LoadRun (StartedDateUtc DESC)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_LoadRun_StartedDateUtc'
              AND i.object_id = OBJECT_ID (N'logs.LoadRun')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_LoadRun_StartedDateUtc
        ON logs.LoadRun REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_LoadRun_Status'
                  AND object_id = OBJECT_ID (N'logs.LoadRun'))
BEGIN
    CREATE INDEX IX_logs_LoadRun_Status
        ON logs.LoadRun (Status, StartedDateUtc DESC)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_LoadRun_Status'
              AND i.object_id = OBJECT_ID (N'logs.LoadRun')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_LoadRun_Status
        ON logs.LoadRun REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 3. The self-referencing foreign key column, indexed like any other.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_LoadRun_ResumedFromLoadRunId'
                  AND object_id = OBJECT_ID (N'logs.LoadRun'))
BEGIN
    CREATE INDEX IX_logs_LoadRun_ResumedFromLoadRunId
        ON logs.LoadRun (ResumedFromLoadRunId)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_LoadRun_ResumedFromLoadRunId'
              AND i.object_id = OBJECT_ID (N'logs.LoadRun')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_LoadRun_ResumedFromLoadRunId
        ON logs.LoadRun REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 4. A run that resumes an abandoned one points at it. Self-referencing, and NO ACTION on
--    delete rather than CASCADE: this database performs no hard deletes, so a cascade could
--    never fire and declaring one would advertise a behaviour that does not exist. On a
--    self-reference SQL Server rejects CASCADE outright, which is a second reason.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_logs_LoadRun_ResumedFromLoadRun')
BEGIN
    ALTER TABLE logs.LoadRun
        ADD CONSTRAINT FK_logs_LoadRun_ResumedFromLoadRun FOREIGN KEY (ResumedFromLoadRunId)
            REFERENCES logs.LoadRun (LoadRunId);
END;
GO

----------------------------------------------------------------------------------------------------
-- 5. Widening CK_logs_LoadRun_RunMode to admit 'Targeted', on a database that already has the
--    narrow one. The CREATE TABLE above carries the wide form so a fresh database is right the
--    first time; this block is what converges an existing one, and the guard reads the constraint's
--    own definition so a second run does nothing.
--
--    A DROP and re-ADD of a CHECK, which is not the forbidden kind of drop: no table, no column and
--    no row is touched, and a CHECK cannot be widened in place -- ALTER TABLE has no syntax for it.
--    WITH CHECK on the way back in, deliberately: an untrusted constraint is one the optimiser stops
--    using and nobody notices, and every existing row already satisfies the wider set.
--
--    'Targeted' is a single-handler run started by hand -- see logs.uspStartLoadRun. It is a value
--    rather than a flag column because two other procedures have to be able to TELL, and a mode is
--    what they read: logs.uspGetHandlerLoadResumeSet must not resume from one, and
--    logs.uspStartLoadRun must not let one block the scheduled load.
----------------------------------------------------------------------------------------------------
IF EXISTS (SELECT 1
             FROM sys.check_constraints
            WHERE name        = N'CK_logs_LoadRun_RunMode'
              AND parent_object_id = OBJECT_ID (N'logs.LoadRun')
              AND definition NOT LIKE N'%Targeted%')
BEGIN
    ALTER TABLE logs.LoadRun DROP CONSTRAINT CK_logs_LoadRun_RunMode;
END;
GO

IF OBJECT_ID (N'logs.LoadRun', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1
                     FROM sys.check_constraints
                    WHERE name             = N'CK_logs_LoadRun_RunMode'
                      AND parent_object_id = OBJECT_ID (N'logs.LoadRun'))
BEGIN
    ALTER TABLE logs.LoadRun WITH CHECK
        ADD CONSTRAINT CK_logs_LoadRun_RunMode CHECK (RunMode IN (N'Full', N'Incremental', N'Reconcile',
                                                                 N'Targeted'));
END;
GO

/*
    Extended properties. Required on the table and on EVERY column (AR6).

    Through util.uspSetObjectDescription only. It adds or updates, so this script re-runs and an
    improved wording replaces the old one; a bare sp_addextendedproperty succeeds once and then
    fails on every subsequent run.

    These columns are ours, not EPA's, so there is no [RCRAInfo: ...] provenance suffix and no
    TODO placeholder: a description we cannot write here is a column we do not understand.
*/

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @Description = N'One row per execution of the console application (AR5): what the run was asked to do, what it did, and how it ended. The row is inserted at the START of the run with Status = ''Running'', so a run that dies without completing still leaves a trace. Parent of logs.HandlerLoadStatus, logs.HandlerLoadAttempt and logs.DataQualityObservation. There is no natural key beyond LoadRunId on purpose: two runs may legitimately have identical mode, location and date range, and asserting uniqueness would reject an honest retry.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'LoadRunId'
    , @Description = N'Surrogate key for one execution of the console application. Referenced by every status, attempt and observation row the run writes.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'RunMode'
    , @Description = N'What kind of run this was: ''Full'' (the initial load, everything from RCRAInfoLoad:InitialLoadFromDate forward), ''Incremental'' (updates since the watermark, the scheduled case), ''Reconcile'' (the periodic full comparison that finds records EPA deleted, which the delta feed may not report -- see G23), or ''Targeted'' (one handler, asked for by hand with --handler-id, either its current record or its whole history). Constrained by CK_logs_LoadRun_RunMode. ''Targeted'' is the only mode that walks no date window, and therefore the only one that can never advance the watermark -- which is why it is a mode and not a flag: logs.uspGetHandlerLoadResumeSet refuses to resume from one, and logs.uspStartLoadRun does not let one block the scheduled load.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'ActivityLocation'
    , @Description = N'The RCRAInfo activity location the run requested, normally ''MD'' (G2). Deliberately NOT constrained to ''MD'': the scope belongs to the request the loader makes, not to this table, and a CHECK here would have to be altered before anyone could run a single out-of-state batch for comparison.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'RequestedFromDate'
    , @Description = N'Start of the date range the run asked EPA for, inclusive. NULL on a full load, which asks for no range. Day-granular because EPA''s incremental parameters are (G25).';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'RequestedToDate'
    , @Description = N'End of the date range the run asked EPA for, inclusive. NULL on a full load.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'WatermarkBeforeDate'
    , @Description = N'The value of config.LoadWatermark.WatermarkDate as the run found it. Recorded so that a run can be replayed against the same starting point after the watermark has moved on.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'WatermarkAfterDate'
    , @Description = N'The value the run advanced the watermark to, or NULL if it did not advance it. A failed or partially succeeded run must leave this NULL: advancing the watermark past records that were never fetched is how data goes missing silently.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'OverlapDaysApplied'
    , @Description = N'How many days the run reached back BEFORE the watermark, from config.LoadWatermark.OverlapDays. The overlap is deliberate re-fetching: EPA''s dates are day-granular, so a record updated later on the same day as the last run would otherwise be missed.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'Status'
    , @Description = N'Outcome: ''Running'' (the default, set on insert), ''Succeeded'', ''PartiallySucceeded'' (some source records failed; see logs.HandlerLoadStatus for which), ''Failed'', or ''Abandoned'' (the process died and a later run marked this one dead -- the state that distinguishes a crash from a run still in flight). Constrained by CK_logs_LoadRun_Status.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'StartedDateUtc'
    , @Description = N'UTC timestamp the run began. Set by DEFAULT on insert.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'CompletedDateUtc'
    , @Description = N'UTC timestamp the run finished, however it finished. NULL while Status = ''Running'', and still NULL on a run that was killed -- which is exactly how the next run recognises one to mark ''Abandoned''.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'ResumedFromLoadRunId'
    , @Description = N'The earlier run this one picked up from, or NULL if it started fresh. Resumability is the reason logs.HandlerLoadStatus exists (the API''s N+1 fetch pattern makes a full load long enough that dying partway is expected), and this column is what makes a resumed run auditable rather than merely quick.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'LookupListsRefreshed'
    , @Description = N'How many of the mirrored /lookup/hd lists the run refreshed. The lookups are refreshed FIRST in every run: a handler arriving with a code the mirror has never seen is otherwise reported as a data-quality problem when the real problem is our stale reference data.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsEnumerated'
    , @Description = N'How many source records the run identified as work to do, before fetching any of them. The denominator for progress, and the count that logs.HandlerLoadStatus should hold one row per.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsFetched'
    , @Description = N'How many source records were successfully retrieved from EPA. Distinct from the counts below, which describe what happened to them in the database.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsInserted'
    , @Description = N'How many dbo.HandlerSource rows the run created.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsUpdated'
    , @Description = N'How many existing dbo.HandlerSource rows the run changed.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsUnchanged'
    , @Description = N'How many source records arrived identical to what we already held and were therefore NOT written. Kept separately from Updated because the difference is the whole point of comparing a content hash first: an UPDATE to the same values would move auditModifiedDateUtc and make the audit trail claim the data changed today.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsSoftDeleted'
    , @Description = N'How many rows the run marked IsDeleted = 1 because EPA no longer reports them. Normally non-zero only on a ''Reconcile'' run: the delta feed may not report deletions at all (G23), so a comparison pass is the only way to find them.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsSkipped'
    , @Description = N'How many source records the run deliberately did not attempt -- already completed by the run it resumed, or excluded by scope.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'SourceRecordsFailed'
    , @Description = N'How many source records could not be loaded after every retry. Non-zero here is what makes Status ''PartiallySucceeded'' rather than ''Succeeded'', and what the web app notifies impacted users about.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'HttpRequestCount'
    , @Description = N'Total HTTP requests the run issued, including retries. The number to quote to EPA when asking about rate limits, and the number that shows how expensive the N+1 fetch pattern really is.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'HttpRetryCount'
    , @Description = N'How many of those requests were retries. A rising ratio against HttpRequestCount is the early warning that the API is throttling or degrading, well before any run actually fails.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'FailureMessage'
    , @Description = N'Why the run ended badly, when the failure was the run''s own rather than one source record''s. Per-record failures belong in logs.HandlerLoadStatus. Must never contain the API key or any credential.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'InvokedBy'
    , @Description = N'Login the run connected as. Set by DEFAULT (ORIGINAL_LOGIN ()), so it identifies the loader application rather than a person -- which is the honest answer for a scheduled task.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'MachineName'
    , @Description = N'The machine the console application ran on. Recorded because two hosts running the scheduled task at once is a real failure mode and is otherwise invisible.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'ProcessId'
    , @Description = N'Operating-system process ID of the run, for correlating this row with the application log and with Task Scheduler history.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'ApplicationVersion'
    , @Description = N'Version of the console application that produced this run, so a change in behaviour can be tied to a deployment.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'IsDeleted'
    , @Description = N'Soft-delete flag. 1 = deleted, 0 = active. This database performs no hard deletes; every read path filters IsDeleted = 0. On this table the flag is how a retention policy retires old run history (G7) -- there is nothing else it could be, since a run that happened cannot be undone.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'auditDeletedBy'
    , @Description = N'Login that soft-deleted the row. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'auditDeletedDateUtc'
    , @Description = N'UTC timestamp of the soft delete. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1; the deleting statement sets it explicitly.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'auditCreatedBy'
    , @Description = N'Login that inserted the row. Under the application logins this identifies which application wrote it, not an end user.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'auditCreatedDateUtc'
    , @Description = N'UTC timestamp of row insert. Set by DEFAULT; the loader omits this column from its INSERT column list so the default fires.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'auditModifiedBy'
    , @Description = N'Login that last modified the row.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadRun'
    , @ColumnName  = N'auditModifiedDateUtc'
    , @Description = N'UTC timestamp of last modification. The DEFAULT fires on INSERT only, so every UPDATE and MERGE must set this column explicitly or the audit trail will claim the row has never changed. On this table that matters more than most: the run row is updated at least twice, at start and at completion.';
GO

PRINT N'300: logs.LoadRun created or altered, 35 column(s) described.';
GO
