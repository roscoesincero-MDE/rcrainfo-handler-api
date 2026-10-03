/**********************************************************************************************************************
Script:       350_logs.ExecutionLog.sql
Author:       hand-authored
CreateDate:   2026-09-05
========================================================================================================================
Description:

One row per stored procedure call: what ran, with which key parameters, when it started, how long it took, whether it
succeeded, and if not, the error. This is the substrate for the AR8 requirement that every procedure an application
calls be instrumented and wrapped in TRY/CATCH.

It is deliberately NOT a duplicate of the AR5 tables next to it. logs.LoadRun and logs.HandlerLoadStatus record what
happened to a RUN and to a HANDLER -- domain facts. This table records what happened to a CALL. A load run that fails
produces one row here and five hundred there, and neither substitutes for the other: this table says "the merge
procedure threw error 1205 at 02:14 after nine seconds", and that is the fact nothing in the schema could previously
state.

Nor is it a substitute for application logging. The reason it has to live in the database rather than in a .NET logging
sink is the rollback: a procedure that fails rolls back everything it wrote, so the record of the failure has to be
re-created after the rollback by the procedure itself. A catch block in C# never sees a statement that was rolled back,
and a log line written from C# cannot be joined to the run it belongs to.

========================================================================================================================
Notes:

OPTIMIZE_FOR_SEQUENTIAL_KEY = ON, which is the opposite of the choice made everywhere else in this database. This is the
one table every session inserts into, always at the end of a clustered identity, which is the last-page insert
contention that option exists to relieve. On the other 50 tables it would be overhead for a workload that does not have
the problem.

Successful defaults to 0 and the start procedure omits it from the INSERT, so a call is unsuccessful until something
proves otherwise. That is what makes the failure index self-maintaining: a row enters IX_logs_ExecutionLog_Failures when
the call begins and leaves it when the call succeeds, so the index holds failures plus calls currently in flight, which
is exactly the monitoring question.

StartDateUtc and auditCreatedDateUtc are not redundant: StartDateUtc is when the PROCEDURE began, passed in by the caller
so that it survives the row being re-created, and auditCreatedDateUtc is when the ROW was written.

Do NOT use auditCreatedDateUtc > StartDateUtc to identify a row that was re-created after a rollback. That comparison was
the original design and it does not work, which the DA0 probe measured rather than assumed: across four rows the deltas
were 0, 0, 1000 and 1998 microseconds, because SYSUTCDATETIME () advances with the Windows clock tick of roughly a
millisecond, so two calls a few statements apart return either the same value or one tick apart regardless of whether
anything was re-created. It is noise in both directions -- an ordinary successful row showed the largest delta of the four,
and a genuinely re-created row showed 1000 microseconds. ReCreatedAfterRollback records the fact explicitly instead.

KeyParameters, Comments, ErrorMessage, DynamicSql and ContextMessage are free text written by us, in a schema the
monitoring web application can be granted access to. They carry the same restriction as
logs.HandlerLoadAttempt.RequestPath: NEVER a credential, NEVER a URL query string, NEVER a request header, and -- the
RCRAInfo-specific trap -- NEVER the @Payload parameter. The reflexive debugging move is to log the parameter, and here
the parameter is the entire Handler batch: thousands of regulated-entity records copied into a second table with a
different read audience, and a log row the size of the data it describes. Identifiers and counts only.

Two nonclustered indexes, not three. Every index on this table is paid for on the insert path, which is every call in
the database, so "history for one procedure" (ProcedureName, StartDateUtc) is deliberately left until F2 measures
whether the scan actually hurts. Guessing wrong here is a cost on every procedure call.

RE-RUNNABLE. Every CREATE is guarded, descriptions go through util.uspSetObjectDescription (which adds or updates), and
nothing is dropped or truncated. A second run changes nothing.

Later changes are ADDITIVE: append a guarded ALTER TABLE ... ADD block rather than editing the CREATE TABLE below, or
the script stops converging on a database that already has the table.

Modification History:
    2026-09-05  Initial version (Workstream DA0). Adapted from the template MDE supplied on 2026-09-05, with the
                project's conventions applied: PascalCase name, the seven audit columns, DF_ constraint names carrying
                the schema, MS_Description on the table and all 21 columns, guarded CREATEs, PAGE compression,
                ElapsedMilliseconds rather than an unlabelled ElapsedTime, ErrorMessage/ContextMessage rather than
                ErrorMsg/ContextMsg, and OPTIMIZE_FOR_SEQUENTIAL_KEY ON rather than OFF.
    2026-09-05  Added ReCreatedAfterRollback (section 1a) after the DA0 acceptance probe showed that the intended
                marker for a re-created row -- auditCreatedDateUtc > StartDateUtc -- is clock-tick noise and fires on
                successful rows. Corrected the StartDateUtc, auditCreatedDateUtc and ErrorProcedure descriptions
                accordingly; ERROR_PROCEDURE () was measured to return a schema-qualified, unbracketed name.
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
IF OBJECT_ID (N'logs.ExecutionLog', N'U') IS NULL
BEGIN
    CREATE TABLE logs.ExecutionLog
    (
        ExecutionLogId        BIGINT          IDENTITY (1, 1) NOT NULL,

        ProcedureName         NVARCHAR (300)                  NOT NULL,
        KeyParameters         NVARCHAR (MAX)                      NULL,
        StartDateUtc          DATETIME2                       NOT NULL CONSTRAINT DF_logs_ExecutionLog_StartDateUtc DEFAULT (SYSUTCDATETIME ()),
        EndDateUtc            DATETIME2                           NULL,
        ElapsedMilliseconds   INT                                 NULL,
        Successful            BIT                             NOT NULL CONSTRAINT DF_logs_ExecutionLog_Successful DEFAULT (0),
        Comments              NVARCHAR (MAX)                      NULL,

        ErrorMessage          NVARCHAR (MAX)                      NULL,
        ErrorProcedure        NVARCHAR (300)                      NULL,
        ErrorNumber           INT                                 NULL,
        ErrorLine             INT                                 NULL,
        DynamicSql            NVARCHAR (MAX)                      NULL,
        ContextMessage        NVARCHAR (MAX)                      NULL,

        IsDeleted             BIT                             NOT NULL CONSTRAINT DF_logs_ExecutionLog_IsDeleted DEFAULT (0),
        auditDeletedBy        NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_ExecutionLog_auditDeletedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditDeletedDateUtc   DATETIME2                       NOT NULL CONSTRAINT DF_logs_ExecutionLog_auditDeletedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditCreatedBy        NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_ExecutionLog_auditCreatedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditCreatedDateUtc   DATETIME2                       NOT NULL CONSTRAINT DF_logs_ExecutionLog_auditCreatedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditModifiedBy       NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_ExecutionLog_auditModifiedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditModifiedDateUtc  DATETIME2                       NOT NULL CONSTRAINT DF_logs_ExecutionLog_auditModifiedDateUtc DEFAULT (SYSUTCDATETIME ()),

        -- OPTIMIZE_FOR_SEQUENTIAL_KEY is ON here and nowhere else in this database. See Notes.
        CONSTRAINT PK_logs_ExecutionLog PRIMARY KEY CLUSTERED (ExecutionLogId ASC)
            WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON)
    )
    WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 1a. ReCreatedAfterRollback, added after the table had already been applied.
--
--     It is an ALTER rather than a line in the CREATE TABLE above on purpose, and this is the first
--     occasion in this project for the additive rule: the table exists on the developer's database,
--     so editing the CREATE would leave a script that converges on an empty server and silently
--     does nothing on a populated one. Dropping and re-creating is not an option here or anywhere
--     else in this database.
--
--     Why it exists at all: the original design identified a re-created row by
--     auditCreatedDateUtc > StartDateUtc, and the DA0 probe showed that comparison to be noise --
--     see Notes. The fact has to be recorded, not inferred.
----------------------------------------------------------------------------------------------------
IF COL_LENGTH (N'logs.ExecutionLog', N'ReCreatedAfterRollback') IS NULL
BEGIN
    ALTER TABLE logs.ExecutionLog
        ADD ReCreatedAfterRollback BIT NOT NULL
            CONSTRAINT DF_logs_ExecutionLog_ReCreatedAfterRollback DEFAULT (0);
END;
GO

----------------------------------------------------------------------------------------------------
-- 2. "What ran recently, and did it work" -- the monitoring app's default view, and the only
--    index that has to cover the whole table.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_ExecutionLog_StartDate'
                  AND object_id = OBJECT_ID (N'logs.ExecutionLog'))
BEGIN
    CREATE INDEX IX_logs_ExecutionLog_StartDate
        ON logs.ExecutionLog (StartDateUtc DESC)
        INCLUDE (ProcedureName, Successful, ElapsedMilliseconds)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_ExecutionLog_StartDate'
              AND i.object_id = OBJECT_ID (N'logs.ExecutionLog')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_ExecutionLog_StartDate
        ON logs.ExecutionLog REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 3. "What broke recently" -- AR2's first screen. Filtered, so it is a fraction of the table, and
--    self-maintaining: a row joins when the call starts and leaves when the call succeeds, which
--    leaves failures plus calls still in flight. IsDeleted = 0 is in the filter so the index
--    matches the read predicate every read path uses (AR7).
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_ExecutionLog_Failures'
                  AND object_id = OBJECT_ID (N'logs.ExecutionLog'))
BEGIN
    CREATE INDEX IX_logs_ExecutionLog_Failures
        ON logs.ExecutionLog (StartDateUtc DESC)
        INCLUDE (ProcedureName, ErrorNumber, ErrorProcedure)
        WHERE Successful = 0 AND IsDeleted = 0
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_ExecutionLog_Failures'
              AND i.object_id = OBJECT_ID (N'logs.ExecutionLog')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_ExecutionLog_Failures
        ON logs.ExecutionLog REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

/*
    No foreign key to logs.LoadRun, and this is a decision rather than an omission.

    Most calls have no run behind them -- every monitoring read, every watermark read -- so the
    column would be NULL more often than not, and the ones that do have a run already record it in
    KeyParameters as text. More importantly, a foreign key here would make the log row depend on the
    run row surviving, and the whole purpose of this table is to record calls that failed, including
    the call that was trying to create the run.
*/

/*
    Extended properties. Required on the table and on EVERY column (AR6).

    Through util.uspSetObjectDescription only. It adds or updates, so this script re-runs and an
    improved wording replaces the old one; a bare sp_addextendedproperty succeeds once and then
    fails on every subsequent run.
*/

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @Description = N'One row per stored procedure call: what ran, with which key parameters, when it started, how long it took, whether it succeeded, and if not, the error. The substrate for AR8, which requires every procedure an application calls to be instrumented and wrapped in TRY/CATCH. Deliberately not a duplicate of the AR5 tables beside it: logs.LoadRun and logs.HandlerLoadStatus record what happened to a run and to a handler, where this records what happened to a call -- a failed load produces one row here and five hundred there, and neither substitutes for the other. It lives in the database rather than in a .NET logging sink because of the rollback: a procedure that fails discards everything it wrote, so the record of the failure has to be re-created after the rollback by the procedure itself, and a catch block in C# never sees a statement that was rolled back. Written only by logs.uspStartExecutionLogging and logs.uspRecordExecutionError; no application login holds any direct permission on it.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ExecutionLogId'
    , @Description = N'Surrogate key for one procedure call. BIGINT rather than INT because this table takes a row per call rather than per handler, so its growth follows call frequency and an INT ceiling is a real horizon rather than a theoretical one.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ProcedureName'
    , @Description = N'The procedure that ran, schema-qualified and bracket-quoted, for example [dbo].[uspMergeHandlerSourceBatch]. Derived from @@PROCID when the caller can resolve it, falling back to the procedure''s own name as a literal when it cannot -- which is the usual case, because OBJECT_NAME (@@PROCID) returns NULL for a principal denied metadata visibility and both application logins are denied it. Never NULL and never a placeholder: the monitoring web app groups by this column.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'KeyParameters'
    , @Description = N'The identifiers and counts that make this call findable, for example ''LoadRunId=418, HandlerCount=500''. IDENTIFIERS AND COUNTS ONLY. Never a credential, never a URL query string, never a request header, and never the @Payload parameter -- logging the payload would copy thousands of regulated-entity records into a table with a different read audience and produce a log row the size of the data it describes. Same restriction as logs.HandlerLoadAttempt.RequestPath, and for the same reason.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'StartDateUtc'
    , @Description = N'UTC timestamp the PROCEDURE began, captured by the caller before any work and passed in, so that it survives the row being re-created after a rollback -- which is what makes the elapsed time of a failed call the time it actually ran rather than the time since its log row was rebuilt. Not the same as auditCreatedDateUtc, which is when the ROW was written. Do not infer re-creation from the difference between the two: SYSUTCDATETIME () advances with the Windows clock tick, so the gap is 0 or one tick whether or not anything was re-created. ReCreatedAfterRollback records that fact.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'EndDateUtc'
    , @Description = N'UTC timestamp the call finished, successfully or not. NULL means the call has not reported back: either it is still running, or the process died in a way that reached neither the success path nor the CATCH block. That second case is exactly what this column is for -- a row with no EndDateUtc and a StartDateUtc hours ago is a crashed call, and nothing else in the database would record it.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ElapsedMilliseconds'
    , @Description = N'Duration of the call in milliseconds, computed as DATEDIFF (MILLISECOND, StartDateUtc, SYSUTCDATETIME ()) when the call reports back. The unit is in the name on purpose: a duration column called ''ElapsedTime'' gets populated in whatever unit the next caller assumes. INT holds 24 days of milliseconds, which is ample for one call. NULL whenever EndDateUtc is NULL, and on an orphan row, where the start time is unknown.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'Successful'
    , @Description = N'1 when the call completed its work and committed, 0 otherwise. Defaults to 0 and the start procedure omits it from the INSERT, so a call is unsuccessful until something proves otherwise -- which is what makes IX_logs_ExecutionLog_Failures self-maintaining: a row joins that index when the call begins and leaves it when the call succeeds, so the index holds failures plus calls still in flight.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'Comments'
    , @Description = N'Optional note from the successful path, for what the call actually did when the count is not obvious from the parameters -- for example ''412 inserted, 88 updated, 0 rejected''. Set by the calling procedure through its @Comments variable. Subject to the same restriction as KeyParameters: no credentials, no payloads.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ErrorMessage'
    , @Description = N'ERROR_MESSAGE () from the failing statement, with the line number appended by the caller. Note that SQL Server composes this text itself and can quote data in it -- a duplicate key value, an over-long string -- which cannot be prevented from here and is the main reason no application login is granted SELECT on this table.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ErrorProcedure'
    , @Description = N'ERROR_PROCEDURE () -- the procedure the error was RAISED in, which is not always the procedure ProcedureName names. When a procedure calls another and the inner one throws, the outer CATCH records itself in ProcedureName and the inner one here, and the pair is what locates the failure. Measured format: schema-qualified but NOT bracket-quoted, for example ''dbo.uspMergeHandlerSourceBatch'', so it never string-matches ProcedureName, which is bracket-quoted. Join on OBJECT_ID rather than on text.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ErrorNumber'
    , @Description = N'ERROR_NUMBER (). Preserved because it is the only part of the error that is safe to branch on: 1205 is a deadlock and the call should be retried, 2627 and 547 are constraint violations and it should not. The calling procedure re-raises the original error with a bare THROW so the same number also reaches the .NET client.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ErrorLine'
    , @Description = N'ERROR_LINE () -- the line within the failing module, not within the deployment script.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'DynamicSql'
    , @Description = N'The dynamic statement being executed when the call failed, where one was. Only the read procedures that sort by a whitelisted @SortBy through sp_executesql build any, so this is NULL for every write procedure. Never the parameter VALUES, only the statement text: the values are what would leak.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ContextMessage'
    , @Description = N'Whatever the calling procedure knew that the error text does not say -- which step of a multi-step procedure was running, or, on an orphan row, that the start row could not be found and the start time is therefore unknown.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'ReCreatedAfterRollback'
    , @Description = N'1 when this row was written a second time by the caller''s CATCH block because a rollback destroyed the original, 0 otherwise. Only reachable when the procedure was called inside a transaction that was already open -- the start row is written before BEGIN TRANSACTION, so a procedure called with no ambient transaction writes it in autocommit and the rollback cannot touch it. A row with this set therefore carries two facts: the execution failed, and it failed under a caller-managed transaction, which AR8 tells the .NET side not to open. Recorded explicitly because the original design inferred it from auditCreatedDateUtc > StartDateUtc, and the DA0 probe measured that comparison to be clock-tick noise that fires on successful rows too.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'IsDeleted'
    , @Description = N'Soft-delete flag. 1 = deleted, 0 = active. This database performs no hard deletes; every read path filters IsDeleted = 0. On this table the flag is the only mechanism a retention policy has (G7), and retention here is a compliance question rather than a storage one, because this is the record of who ran what.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'auditDeletedBy'
    , @Description = N'Login that soft-deleted the row. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'auditDeletedDateUtc'
    , @Description = N'UTC timestamp of the soft delete. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1; the deleting statement sets it explicitly.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'auditCreatedBy'
    , @Description = N'Login that inserted the row -- which, because ORIGINAL_LOGIN () survives ownership chaining, is the application login whose call is being logged rather than the owner of the logging procedure.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'auditCreatedDateUtc'
    , @Description = N'UTC timestamp the ROW was written, set by DEFAULT because the start procedure omits this column from its INSERT. Distinct from StartDateUtc, which is when the PROCEDURE began, but NOT usefully comparable to it: SYSUTCDATETIME () advances with the Windows clock tick, so the two are equal or one tick apart on every row regardless of history. Use ReCreatedAfterRollback to tell a rolled-back execution from an ordinary one.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'auditModifiedBy'
    , @Description = N'Login that last modified the row. Set explicitly by both the success UPDATE and the error MERGE, because the DEFAULT fires on INSERT only.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'ExecutionLog'
    , @ColumnName  = N'auditModifiedDateUtc'
    , @Description = N'UTC timestamp of last modification. The DEFAULT fires on INSERT only, so every UPDATE and MERGE must set this column explicitly or the audit trail will claim the row has never changed -- which on this table, being itself an audit trail, would be the most misleading place in the database to get it wrong.';
GO

PRINT N'350: logs.ExecutionLog created or altered, 22 column(s) described.';
GO
