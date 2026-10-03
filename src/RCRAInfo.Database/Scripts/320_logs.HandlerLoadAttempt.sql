/**********************************************************************************************************************
Script:       320_logs.HandlerLoadAttempt.sql
Author:       hand-authored
CreateDate:   2026-09-04
========================================================================================================================
Description:

The append-only attempt log AR5 asks for alongside the status table: one row per HTTP attempt at one source record, at
the grain (HandlerLoadStatusId, AttemptNumber).

logs.HandlerLoadStatus holds the CURRENT state of a source record within a run and is updated in place; this table holds
the individual attempts and is only ever inserted into. Both are needed, and for different questions. "Did this handler
load?" is answered by the status row. "Why did it take four tries, and was it EPA throttling us or a timeout on our
side?" can only be answered by rows that were never overwritten -- and that question is the one asked when the API
starts misbehaving at 3am, which is the situation the whole load is designed around.

APPEND-ONLY IS A DESIGN PROPERTY, NOT AN ENFORCED ONE. Nothing in the DDL prevents an UPDATE here; the intent is
recorded in the extended properties and honoured by the procedures that write it. Enforcing it would take a trigger
that raises on UPDATE, which is deliberately not built in Phase 1 -- it would be the only such trigger in the database
and would surprise the next developer more than it would protect the data.

========================================================================================================================
Notes:

The identity is BIGINT here and INT everywhere else in this database. This is the highest-volume table by an order of
magnitude -- one row per attempt, not per record per run -- and it is the one place where the retention question (G7)
and the key width question could actually collide. BIGINT costs four bytes a row under PAGE compression and removes the
collision entirely.

RequestPath stores the PATH ONLY. Never the query string, never a header. RCRAInfo authentication travels in headers and
the token endpoint takes credentials directly, so a log that captured whole requests would eventually capture an API key
into a table the monitoring web app can read.

RE-RUNNABLE. Every CREATE is guarded, descriptions go through util.uspSetObjectDescription (which adds or updates), and
nothing is dropped or truncated. A second run changes nothing.

Later changes are ADDITIVE: append a guarded ALTER TABLE ... ADD block rather than editing the CREATE TABLE below, or
the script stops converging on a database that already has the table.

Modification History:
    2026-09-04  Initial version (Workstream B3).
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
IF OBJECT_ID (N'logs.HandlerLoadAttempt', N'U') IS NULL
BEGIN
    CREATE TABLE logs.HandlerLoadAttempt
    (
        HandlerLoadAttemptId  BIGINT          IDENTITY (1, 1) NOT NULL,
        HandlerLoadStatusId   INT                             NOT NULL,
        LoadRunId             INT                             NOT NULL,
        AttemptNumber         INT                             NOT NULL,
        StartedDateUtc        DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_StartedDateUtc DEFAULT (SYSUTCDATETIME ()),
        CompletedDateUtc      DATETIME2                           NULL,
        DurationMs            INT                                 NULL,
        Outcome               NVARCHAR (20)                   NOT NULL,
        HttpStatusCode        INT                                 NULL,
        RequestPath           NVARCHAR (400)                      NULL,
        ResponseBytes         INT                                 NULL,
        RetryAfterSeconds     INT                                 NULL,
        ApiErrorCode          NVARCHAR (100)                      NULL,
        ApiErrorMessage       NVARCHAR (4000)                     NULL,
        ApiErrorId            NVARCHAR (50)                       NULL,
        ApiErrorDate          DATETIME2                           NULL,
        FailureMessage        NVARCHAR (4000)                     NULL,
        IsDeleted             BIT                             NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_IsDeleted DEFAULT (0),
        auditDeletedBy        NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_auditDeletedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditDeletedDateUtc   DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_auditDeletedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditCreatedBy        NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_auditCreatedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditCreatedDateUtc   DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_auditCreatedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditModifiedBy       NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_auditModifiedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditModifiedDateUtc  DATETIME2                       NOT NULL CONSTRAINT DF_logs_HandlerLoadAttempt_auditModifiedDateUtc DEFAULT (SYSUTCDATETIME ()),

        CONSTRAINT PK_logs_HandlerLoadAttempt PRIMARY KEY CLUSTERED (HandlerLoadAttemptId),

        -- Throttled and TimedOut are kept out of the general 'Failed' bucket on purpose: both are
        -- expected, both are retried, and both mean something different about the API's health than
        -- an error EPA chose to return.
        CONSTRAINT CK_logs_HandlerLoadAttempt_Outcome CHECK (Outcome IN (N'Succeeded', N'Failed', N'Throttled',
                                                                        N'TimedOut', N'Cancelled'))
    )
    WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 2. Natural key.
--    A filtered unique index, not a unique constraint: a soft-deleted row keeps its key, and
--    an unfiltered constraint would block re-creation of that key forever.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'UX_logs_HandlerLoadAttempt_Natural'
                  AND object_id = OBJECT_ID (N'logs.HandlerLoadAttempt'))
BEGIN
    CREATE UNIQUE INDEX UX_logs_HandlerLoadAttempt_Natural
        ON logs.HandlerLoadAttempt (HandlerLoadStatusId, AttemptNumber)
        WHERE IsDeleted = 0
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'UX_logs_HandlerLoadAttempt_Natural'
              AND i.object_id = OBJECT_ID (N'logs.HandlerLoadAttempt')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX UX_logs_HandlerLoadAttempt_Natural
        ON logs.HandlerLoadAttempt REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 3. The parent foreign key column, unfiltered.
--    Deliberately not redundant with the index above, which is filtered to IsDeleted = 0 and so
--    cannot serve a read that has to see soft-deleted rows. Retention (G7) works through
--    IsDeleted, and this is the table retention will touch first.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_HandlerLoadAttempt_HandlerLoadStatusId'
                  AND object_id = OBJECT_ID (N'logs.HandlerLoadAttempt'))
BEGIN
    CREATE INDEX IX_logs_HandlerLoadAttempt_HandlerLoadStatusId
        ON logs.HandlerLoadAttempt (HandlerLoadStatusId)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_HandlerLoadAttempt_HandlerLoadStatusId'
              AND i.object_id = OBJECT_ID (N'logs.HandlerLoadAttempt')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_HandlerLoadAttempt_HandlerLoadStatusId
        ON logs.HandlerLoadAttempt REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 4. "Every attempt in this run", which is how a bad night is diagnosed and how the retry
--    ratio on logs.LoadRun is reconciled against the detail behind it.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_HandlerLoadAttempt_LoadRunId'
                  AND object_id = OBJECT_ID (N'logs.HandlerLoadAttempt'))
BEGIN
    CREATE INDEX IX_logs_HandlerLoadAttempt_LoadRunId
        ON logs.HandlerLoadAttempt (LoadRunId, Outcome)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_HandlerLoadAttempt_LoadRunId'
              AND i.object_id = OBJECT_ID (N'logs.HandlerLoadAttempt')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_HandlerLoadAttempt_LoadRunId
        ON logs.HandlerLoadAttempt REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 5. Foreign keys. NO ACTION on delete, not CASCADE: this database performs no hard deletes,
--    so a cascade could never fire and declaring one would advertise a behaviour that does
--    not exist.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_logs_HandlerLoadAttempt_HandlerLoadStatus')
BEGIN
    ALTER TABLE logs.HandlerLoadAttempt
        ADD CONSTRAINT FK_logs_HandlerLoadAttempt_HandlerLoadStatus FOREIGN KEY (HandlerLoadStatusId)
            REFERENCES logs.HandlerLoadStatus (HandlerLoadStatusId);
END;
GO

IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_logs_HandlerLoadAttempt_LoadRun')
BEGIN
    ALTER TABLE logs.HandlerLoadAttempt
        ADD CONSTRAINT FK_logs_HandlerLoadAttempt_LoadRun FOREIGN KEY (LoadRunId)
            REFERENCES logs.LoadRun (LoadRunId);
END;
GO

/*
    Extended properties. Required on the table and on EVERY column (AR6).

    Through util.uspSetObjectDescription only. It adds or updates, so this script re-runs and an
    improved wording replaces the old one; a bare sp_addextendedproperty succeeds once and then
    fails on every subsequent run.
*/

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @Description = N'The append-only attempt log AR5 asks for alongside the status table: one row per HTTP attempt at one source record, at the grain (HandlerLoadStatusId, AttemptNumber). logs.HandlerLoadStatus is updated in place and answers "did this handler load"; this table is only inserted into and answers "why did it take four tries, and was EPA throttling us or did we time out" -- a question that can only be answered by rows nothing overwrote. Append-only is honoured by the procedures that write it rather than enforced by a trigger, which would be the only such trigger in this database. The identity is BIGINT rather than INT because this is the highest-volume table here by an order of magnitude.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'HandlerLoadAttemptId'
    , @Description = N'Surrogate key for one attempt. BIGINT, unlike every other identity in this database: one row per attempt rather than per record per run makes this the one table where key width and the open retention question (G7) could actually collide.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'HandlerLoadStatusId'
    , @Description = N'The logs.HandlerLoadStatus row this attempt belongs to -- one source record within one run.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'LoadRunId'
    , @Description = N'The run this attempt was made during. Reachable through HandlerLoadStatusId and stored anyway: "every attempt in this run" is the query asked when a night goes badly, and on the largest table here it should not need a join to answer.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'AttemptNumber'
    , @Description = N'1 for the first attempt at this source record in this run, incrementing per retry. Part of the natural key.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'StartedDateUtc'
    , @Description = N'UTC timestamp the attempt was issued. Set by DEFAULT on insert. The spacing between consecutive attempts is the observable record of whether backoff is actually working.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'CompletedDateUtc'
    , @Description = N'UTC timestamp the attempt finished, however it finished. NULL on a row written before the response arrived, and permanently NULL if the process died mid-request -- which is itself the evidence that it did.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'DurationMs'
    , @Description = N'Elapsed milliseconds for this single attempt. Aggregated across a run, this is the measurement G6 needs to answer how long a full load actually takes.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'Outcome'
    , @Description = N'How this attempt ended: ''Succeeded'', ''Failed'', ''Throttled'', ''TimedOut'', or ''Cancelled''. Throttled and TimedOut are deliberately not folded into Failed -- both are expected, both are retried, and both say something different about the API''s health than an error EPA chose to return. Constrained by CK_logs_HandlerLoadAttempt_Outcome.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'HttpStatusCode'
    , @Description = N'HTTP status returned by this attempt, or NULL if no response was received at all.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'RequestPath'
    , @Description = N'The request PATH only -- never the query string and never a header. RCRAInfo authentication travels in headers and the token endpoint takes the API ID and key directly, so a log that captured whole requests would eventually capture a credential into a table the monitoring web app can read.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'ResponseBytes'
    , @Description = N'Size of the response body. Kept because a successful fetch that returned far less than usual is a data problem that no status code reports.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'RetryAfterSeconds'
    , @Description = N'The Retry-After value EPA returned, when it returned one. This is the API telling us its own rate limit, which G5 otherwise leaves as an open question to be answered by asking EPA.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'ApiErrorCode'
    , @Description = N'EPA''s ApiError.code for this attempt, for example E_AccessDenied. [RCRAInfo: ApiError.code]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'ApiErrorMessage'
    , @Description = N'EPA''s ApiError.message for this attempt, stored as received. [RCRAInfo: ApiError.message]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'ApiErrorId'
    , @Description = N'EPA''s ApiError.errorId -- the UUID identifying this error in EPA''s systems, and the value to quote verbatim in a support request. [RCRAInfo: ApiError.errorId]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'ApiErrorDate'
    , @Description = N'When EPA says the error occurred. No Utc suffix on purpose: EPA declares this as format date-time but states no time zone. [RCRAInfo: ApiError.errorDate]';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'FailureMessage'
    , @Description = N'Our own failure detail -- a socket error, a TLS failure, a deserialisation problem -- as distinct from an error EPA returned, which goes in the ApiError columns. The distinction matters: one is a problem to raise with EPA, the other is ours. Must never contain the API key or any credential.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'IsDeleted'
    , @Description = N'Soft-delete flag. 1 = deleted, 0 = active. This database performs no hard deletes; every read path filters IsDeleted = 0. On this table the flag is the only mechanism a retention policy has (G7), and this is the table that will need one first.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'auditDeletedBy'
    , @Description = N'Login that soft-deleted the row. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'auditDeletedDateUtc'
    , @Description = N'UTC timestamp of the soft delete. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1; the deleting statement sets it explicitly.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'auditCreatedBy'
    , @Description = N'Login that inserted the row. Under the application logins this identifies which application wrote it, not an end user.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'auditCreatedDateUtc'
    , @Description = N'UTC timestamp of row insert. Set by DEFAULT; the loader omits this column from its INSERT column list so the default fires. On an append-only table this is the row''s own timestamp, and StartedDateUtc is the attempt''s -- they differ by however long the write was buffered.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'auditModifiedBy'
    , @Description = N'Login that last modified the row. On an append-only table this should always equal auditCreatedBy; if it does not, something updated a row that was never meant to be updated.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerLoadAttempt'
    , @ColumnName  = N'auditModifiedDateUtc'
    , @Description = N'UTC timestamp of last modification. The DEFAULT fires on INSERT only. On an append-only table this should always equal auditCreatedDateUtc, and a difference is the cheapest available evidence that the append-only intent was not honoured.';
GO

PRINT N'320: logs.HandlerLoadAttempt created or altered, 24 column(s) described.';
GO
