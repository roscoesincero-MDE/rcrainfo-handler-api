/**********************************************************************************************************************
Script:       330_logs.DataQualityObservation.sql
Author:       hand-authored
CreateDate:   2026-09-04
========================================================================================================================
Description:

Where a data-quality problem goes when the answer is "record it and load the row anyway".

This table is what makes [R10] operable. The decision not to put a foreign key from any handler table to any mirrored
lookup list rests entirely on there being somewhere else for an unknown code to be reported: this database is a copy of
someone else's system of record, and refusing EPA's data because our copy of EPA's own code list is stale asserts an
authority we do not have. So the row loads, and the unknown code lands here. Without this table that decision would
quietly become "unknown codes are ignored", which is a different and much worse decision.

It is not limited to unknown codes. Anything the loader notices but must not fail on belongs here: a value too long for
the column it was measured for, an unexpected NULL in a field EPA marks required (their required markers are
demonstrably unreliable -- preprod and production disagree about HandlerSourceNaics.primary), a payload property the
pinned spec does not describe.

========================================================================================================================
Notes:

NO UNIQUE NATURAL KEY, and unlike logs.LoadRun the reason is mechanical as well as semantic. Repeated observations are
legitimately repeated -- one unknown waste code appears on four hundred handlers in the same run, and each of those is a
fact worth keeping. And a unique index over these columns could not work anyway: most of them are nullable, and SQL
Server treats NULL as equal to NULL for uniqueness, so two observations that were simply not about a specific handler
would collide.

ObservationType is deliberately NOT constrained by a CHECK, whereas Severity is. New kinds of observation will be
recognised as the loader matures, and a CHECK would make recording one require a DDL change -- which the loader login
has no rights to perform (AR3). Severity is a fixed three-value scale and will not grow.

There are no acknowledgement or resolution columns. The web app's notification design still has open questions
(Phase1-Plan.md), and inventing a workflow here before it is designed would be guessing in DDL. Adding columns to this
table later is a guarded ALTER TABLE ... ADD; that is cheap, and guessing is not.

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
IF OBJECT_ID (N'logs.DataQualityObservation', N'U') IS NULL
BEGIN
    CREATE TABLE logs.DataQualityObservation
    (
        DataQualityObservationId  INT             IDENTITY (1, 1) NOT NULL,
        LoadRunId                 INT                             NOT NULL,
        HandlerLoadStatusId       INT                                 NULL,
        ObservationType           NVARCHAR (50)                   NOT NULL,
        Severity                  NVARCHAR (20)                   NOT NULL CONSTRAINT DF_logs_DataQualityObservation_Severity DEFAULT (N'Warning'),
        HandlerId                 NVARCHAR (12)                       NULL,
        SourceType                NVARCHAR (1)                        NULL,
        Sequence                  INT                                 NULL,
        TableName                 NVARCHAR (128)                      NULL,
        ColumnName                NVARCHAR (128)                      NULL,
        JsonPath                  NVARCHAR (400)                      NULL,
        LookupName                NVARCHAR (100)                      NULL,
        ObservedValue             NVARCHAR (400)                      NULL,
        Detail                    NVARCHAR (4000)                     NULL,
        ObservedDateUtc           DATETIME2                       NOT NULL CONSTRAINT DF_logs_DataQualityObservation_ObservedDateUtc DEFAULT (SYSUTCDATETIME ()),
        IsDeleted                 BIT                             NOT NULL CONSTRAINT DF_logs_DataQualityObservation_IsDeleted DEFAULT (0),
        auditDeletedBy            NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_DataQualityObservation_auditDeletedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditDeletedDateUtc       DATETIME2                       NOT NULL CONSTRAINT DF_logs_DataQualityObservation_auditDeletedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditCreatedBy            NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_DataQualityObservation_auditCreatedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditCreatedDateUtc       DATETIME2                       NOT NULL CONSTRAINT DF_logs_DataQualityObservation_auditCreatedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditModifiedBy           NVARCHAR (128)                  NOT NULL CONSTRAINT DF_logs_DataQualityObservation_auditModifiedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditModifiedDateUtc      DATETIME2                       NOT NULL CONSTRAINT DF_logs_DataQualityObservation_auditModifiedDateUtc DEFAULT (SYSUTCDATETIME ()),

        CONSTRAINT PK_logs_DataQualityObservation PRIMARY KEY CLUSTERED (DataQualityObservationId),

        -- Severity is a fixed scale, so it is constrained. ObservationType is not: new kinds of
        -- observation will be recognised over time, and a CHECK would make recording one require
        -- DDL rights the loader does not have.
        CONSTRAINT CK_logs_DataQualityObservation_Severity CHECK (Severity IN (N'Info', N'Warning', N'Error'))
    )
    WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 2. "What did this run notice", which is the query the monitoring UI runs after every load,
--    and the parent foreign key index at the same time.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_DataQualityObservation_LoadRunId'
                  AND object_id = OBJECT_ID (N'logs.DataQualityObservation'))
BEGIN
    CREATE INDEX IX_logs_DataQualityObservation_LoadRunId
        ON logs.DataQualityObservation (LoadRunId, Severity, ObservationType)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_DataQualityObservation_LoadRunId'
              AND i.object_id = OBJECT_ID (N'logs.DataQualityObservation')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_DataQualityObservation_LoadRunId
        ON logs.DataQualityObservation REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 3. The optional link to the source record the observation is about.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_DataQualityObservation_HandlerLoadStatusId'
                  AND object_id = OBJECT_ID (N'logs.DataQualityObservation'))
BEGIN
    CREATE INDEX IX_logs_DataQualityObservation_HandlerLoadStatusId
        ON logs.DataQualityObservation (HandlerLoadStatusId)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_DataQualityObservation_HandlerLoadStatusId'
              AND i.object_id = OBJECT_ID (N'logs.DataQualityObservation')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_DataQualityObservation_HandlerLoadStatusId
        ON logs.DataQualityObservation REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 4. "Have we seen this unknown code before, and in which list?" -- the question that decides
--    whether an unknown code is a stale mirror on our side or something new on EPA's.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_logs_DataQualityObservation_ObservationType'
                  AND object_id = OBJECT_ID (N'logs.DataQualityObservation'))
BEGIN
    CREATE INDEX IX_logs_DataQualityObservation_ObservationType
        ON logs.DataQualityObservation (ObservationType, LookupName, ObservedValue)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_logs_DataQualityObservation_ObservationType'
              AND i.object_id = OBJECT_ID (N'logs.DataQualityObservation')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_logs_DataQualityObservation_ObservationType
        ON logs.DataQualityObservation REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 5. Foreign keys. NO ACTION on delete, not CASCADE: this database performs no hard deletes,
--    so a cascade could never fire and declaring one would advertise a behaviour that does
--    not exist.
--
--    HandlerLoadStatusId is nullable and stays nullable: an observation about a lookup list
--    refresh, or about the spec itself, is not about any one source record.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_logs_DataQualityObservation_LoadRun')
BEGIN
    ALTER TABLE logs.DataQualityObservation
        ADD CONSTRAINT FK_logs_DataQualityObservation_LoadRun FOREIGN KEY (LoadRunId)
            REFERENCES logs.LoadRun (LoadRunId);
END;
GO

IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_logs_DataQualityObservation_HandlerLoadStatus')
BEGIN
    ALTER TABLE logs.DataQualityObservation
        ADD CONSTRAINT FK_logs_DataQualityObservation_HandlerLoadStatus FOREIGN KEY (HandlerLoadStatusId)
            REFERENCES logs.HandlerLoadStatus (HandlerLoadStatusId);
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
    , @ObjectName  = N'DataQualityObservation'
    , @Description = N'Where a data-quality problem goes when the answer is "record it and load the row anyway". This table is what makes [R10] operable: the decision to put no foreign key from any handler table to any mirrored lookup list rests on an unknown code having somewhere else to be reported, because this database is a copy of someone else''s system of record and refusing their data over a stale copy of their own code list asserts an authority we do not have. Without this table that decision quietly becomes "unknown codes are ignored". Also carries anything else the loader must notice without failing: a value longer than the column measured for it, a NULL where EPA marks a field required, a payload property the pinned spec does not describe. There is no unique natural key -- repeated observations are legitimately repeated, and most of these columns are nullable, where SQL Server would treat NULL as equal to NULL and reject the second observation that simply was not about a specific handler.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'DataQualityObservationId'
    , @Description = N'Surrogate key for one observation.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'LoadRunId'
    , @Description = N'The run that made the observation. Always present: an observation with no run behind it has no context in which to be judged.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'HandlerLoadStatusId'
    , @Description = N'The source record the observation is about, or NULL when it is not about one -- an observation raised while refreshing a lookup list, or about the shape of the payload itself, has no single handler behind it.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'ObservationType'
    , @Description = N'What kind of problem this is. The loader''s vocabulary, for example UnknownLookupCode (the [R10] case), ValueTooLong, UnexpectedNull, UnknownProperty. Deliberately NOT constrained by a CHECK: new kinds will be recognised as the loader matures, and a CHECK would make recording one require DDL rights the loader login does not have (AR3).';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'Severity'
    , @Description = N'''Info'', ''Warning'' (the default) or ''Error''. Constrained by CK_logs_DataQualityObservation_Severity, because unlike ObservationType this is a fixed three-value scale that will not grow. ''Error'' here still does not fail the load -- if it should fail the load, it belongs in logs.HandlerLoadStatus as a Failed record, not in this table.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'HandlerId'
    , @Description = N'EPA handler identifier the observation concerns, when it concerns one. Duplicated from the status row on purpose: this is the column a person searches by, and it must be readable without a join on a table that may be reported on long after the run.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'SourceType'
    , @Description = N'RCRAInfo source type of the record the observation concerns, when it concerns one.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'Sequence'
    , @Description = N'RCRAInfo sequence number of the record the observation concerns, when it concerns one. With HandlerId and SourceType this identifies the version, matching the grain of dbo.HandlerSource.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'TableName'
    , @Description = N'The table in this database the observation is about, unqualified, for example HandlerSourceWasteFederalWasteCode. NULL when the observation is not about a particular table.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'ColumnName'
    , @Description = N'The column the observation is about. With TableName this is what turns "an unknown code arrived" into something a person can actually go and look at.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'JsonPath'
    , @Description = N'EPA''s own path to the value, for example waste.federalWasteCodes[3]. The same provenance convention the column descriptions use as their [RCRAInfo: ...] suffix, so an observation can be traced back to the payload rather than only forward to our schema.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'LookupName'
    , @Description = N'Which mirrored lookup list the value should have appeared in, for UnknownLookupCode observations. This is the column that answers the operational question: if the list was refreshed this run and the code is still absent, the code is new on EPA''s side rather than stale on ours.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'ObservedValue'
    , @Description = N'The value EPA actually sent, as sent. Truncated to 400 characters if it was longer -- which is itself only ever the case for a ValueTooLong observation, where the full value is in the payload preserved on dbo.HandlerSourceRawJson.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'Detail'
    , @Description = N'Free text from the loader explaining the observation, for a reader who has none of the surrounding context. Must never contain the API key or any credential.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'ObservedDateUtc'
    , @Description = N'UTC timestamp the loader made the observation. Set by DEFAULT on insert.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'IsDeleted'
    , @Description = N'Soft-delete flag. 1 = deleted, 0 = active. This database performs no hard deletes; every read path filters IsDeleted = 0. On this table the flag is the only mechanism a retention policy has (G7).';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'auditDeletedBy'
    , @Description = N'Login that soft-deleted the row. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'auditDeletedDateUtc'
    , @Description = N'UTC timestamp of the soft delete. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1; the deleting statement sets it explicitly.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'auditCreatedBy'
    , @Description = N'Login that inserted the row. Under the application logins this identifies which application wrote it, not an end user.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'auditCreatedDateUtc'
    , @Description = N'UTC timestamp of row insert. Set by DEFAULT; the loader omits this column from its INSERT column list so the default fires.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'auditModifiedBy'
    , @Description = N'Login that last modified the row.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'DataQualityObservation'
    , @ColumnName  = N'auditModifiedDateUtc'
    , @Description = N'UTC timestamp of last modification. The DEFAULT fires on INSERT only, so every UPDATE and MERGE must set this column explicitly or the audit trail will claim the row has never changed.';
GO

PRINT N'330: logs.DataQualityObservation created or altered, 22 column(s) described.';
GO
