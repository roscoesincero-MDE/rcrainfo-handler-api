/**********************************************************************************************************************
Script:       340_config.LoadWatermark.sql
Author:       hand-authored
CreateDate:   2026-09-04
========================================================================================================================
Description:

The incremental watermark (G25) and the overlap window that goes with it. One row per feed per activity location.

EPA's incremental parameters are DAY-GRANULAR, which is the whole reason this table has an overlap setting rather than
just a timestamp. If a run finishes at 14:00 and records "loaded through today", a record EPA updates at 16:00 the same
day is invisible to every subsequent run: the next one asks for changes since today and gets a set that no longer
includes it. OverlapDays is the deliberate re-fetch that closes that hole. Re-fetching costs a request and finds the
payload unchanged; missing an update costs a silent gap in a regulatory mirror.

WHY FeedName EXISTS WHEN ONLY ONE FEED USES IT. In Phase 1 the single row is ('HandlerSource', 'MD'); the mirrored
lookup lists are refreshed wholesale every run and need no watermark. The column is here because a second watermarked
feed -- most plausibly the periodic reconcile pass, which tracks when the last full comparison happened rather than when
the last delta was taken -- would otherwise require a change to this table's GRAIN. Adding a column later is cheap;
changing a grain is the one thing this schema goes out of its way to avoid, and one narrow column now buys that.

========================================================================================================================
Notes:

WatermarkDate IS NULL MEANS "NEVER SUCCESSFULLY LOADED", and that is a load-bearing value, not a missing one: it is what
tells the loader its next run must be a full load. It is also what the seeded row starts as, so a fresh database asks
for everything the first time it runs, without anyone having to remember to say so.

A watermark is only ever advanced by a run that SUCCEEDED. Advancing it past records that were never fetched is the
single cheapest way to lose data permanently in this design -- the gap closes behind the watermark and no later run ever
looks there again. logs.LoadRun.WatermarkAfterDate records each advance, so the history is auditable.

THE SEED AT THE BOTTOM IS A CONVERGING INSERT, not a MERGE that rewrites. It inserts the row when absent and touches
nothing when present. An UPDATE to the values it already holds would move auditModifiedDateUtc and make the audit trail
claim the configuration changed today -- and worse here than elsewhere, since a re-run would also silently overwrite an
OverlapDays an operator had deliberately changed.

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
IF OBJECT_ID (N'config.LoadWatermark', N'U') IS NULL
BEGIN
    CREATE TABLE config.LoadWatermark
    (
        LoadWatermarkId          INT             IDENTITY (1, 1) NOT NULL,
        FeedName                 NVARCHAR (50)                   NOT NULL,
        ActivityLocation         NVARCHAR (2)                    NOT NULL,
        WatermarkDate            DATE                                NULL,
        OverlapDays              INT                             NOT NULL CONSTRAINT DF_config_LoadWatermark_OverlapDays DEFAULT (1),
        IsEnabled                BIT                             NOT NULL CONSTRAINT DF_config_LoadWatermark_IsEnabled DEFAULT (1),
        LastAdvancedByLoadRunId  INT                                 NULL,
        LastAdvancedDateUtc      DATETIME2                           NULL,
        Notes                    NVARCHAR (4000)                     NULL,
        IsDeleted                BIT                             NOT NULL CONSTRAINT DF_config_LoadWatermark_IsDeleted DEFAULT (0),
        auditDeletedBy           NVARCHAR (128)                  NOT NULL CONSTRAINT DF_config_LoadWatermark_auditDeletedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditDeletedDateUtc      DATETIME2                       NOT NULL CONSTRAINT DF_config_LoadWatermark_auditDeletedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditCreatedBy           NVARCHAR (128)                  NOT NULL CONSTRAINT DF_config_LoadWatermark_auditCreatedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditCreatedDateUtc      DATETIME2                       NOT NULL CONSTRAINT DF_config_LoadWatermark_auditCreatedDateUtc DEFAULT (SYSUTCDATETIME ()),
        auditModifiedBy          NVARCHAR (128)                  NOT NULL CONSTRAINT DF_config_LoadWatermark_auditModifiedBy DEFAULT (ORIGINAL_LOGIN ()),
        auditModifiedDateUtc     DATETIME2                       NOT NULL CONSTRAINT DF_config_LoadWatermark_auditModifiedDateUtc DEFAULT (SYSUTCDATETIME ()),

        CONSTRAINT PK_config_LoadWatermark PRIMARY KEY CLUSTERED (LoadWatermarkId),

        -- A sanity bound, not a policy. 0 disables the overlap; a year is far more than any real
        -- catch-up needs. The bound exists because a fat-fingered 3650 would turn every scheduled
        -- incremental into a near-full load, and would do it quietly.
        CONSTRAINT CK_config_LoadWatermark_OverlapDays CHECK (OverlapDays BETWEEN 0 AND 365)
    )
    WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 2. Natural key.
--    A filtered unique index, not a unique constraint: a soft-deleted row keeps its key, and
--    an unfiltered constraint would mean a retired feed could never be re-created under the
--    same name.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'UX_config_LoadWatermark_Natural'
                  AND object_id = OBJECT_ID (N'config.LoadWatermark'))
BEGIN
    CREATE UNIQUE INDEX UX_config_LoadWatermark_Natural
        ON config.LoadWatermark (FeedName, ActivityLocation)
        WHERE IsDeleted = 0
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'UX_config_LoadWatermark_Natural'
              AND i.object_id = OBJECT_ID (N'config.LoadWatermark')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX UX_config_LoadWatermark_Natural
        ON config.LoadWatermark REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 3. The foreign key column, unfiltered, for the same reason as everywhere else: the index
--    above is filtered to IsDeleted = 0 and cannot serve a read that must see retired rows.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.indexes
                WHERE name      = N'IX_config_LoadWatermark_LastAdvancedByLoadRunId'
                  AND object_id = OBJECT_ID (N'config.LoadWatermark'))
BEGIN
    CREATE INDEX IX_config_LoadWatermark_LastAdvancedByLoadRunId
        ON config.LoadWatermark (LastAdvancedByLoadRunId)
        WITH (DATA_COMPRESSION = PAGE);
END;
GO

IF EXISTS (SELECT 1
             FROM sys.indexes AS i
             JOIN sys.partitions AS p
               ON p.object_id = i.object_id
              AND p.index_id  = i.index_id
            WHERE i.name      = N'IX_config_LoadWatermark_LastAdvancedByLoadRunId'
              AND i.object_id = OBJECT_ID (N'config.LoadWatermark')
              AND p.data_compression_desc <> N'PAGE')
BEGIN
    ALTER INDEX IX_config_LoadWatermark_LastAdvancedByLoadRunId
        ON config.LoadWatermark REBUILD WITH (DATA_COMPRESSION = PAGE);
END;
GO

----------------------------------------------------------------------------------------------------
-- 4. Foreign key. NO ACTION on delete, not CASCADE: this database performs no hard deletes, so
--    a cascade could never fire and declaring one would advertise a behaviour that does not
--    exist. Here a cascade would also be actively wrong -- retiring a run must not take the
--    watermark with it.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.foreign_keys
                WHERE name = N'FK_config_LoadWatermark_LoadRun')
BEGIN
    ALTER TABLE config.LoadWatermark
        ADD CONSTRAINT FK_config_LoadWatermark_LoadRun FOREIGN KEY (LastAdvancedByLoadRunId)
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
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @Description = N'The incremental watermark (G25) and its overlap window, one row per feed per activity location. EPA''s incremental parameters are day-granular, which is why an overlap setting exists at all: a run finishing at 14:00 that records "loaded through today" would never see a record EPA updates at 16:00 the same day, because every later run asks for changes since a date that already includes it. Re-fetching a day costs a request and finds the payload unchanged; missing an update leaves a silent gap in a regulatory mirror. In Phase 1 there is exactly one row, FeedName ''HandlerSource'' and ActivityLocation ''MD'', seeded by this script.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'LoadWatermarkId'
    , @Description = N'Surrogate key for one watermark.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'FeedName'
    , @Description = N'Which feed this watermark belongs to. ''HandlerSource'' is the only value in Phase 1; the mirrored lookup lists are refreshed wholesale every run and need no watermark. The column exists anyway because a second watermarked feed -- most plausibly the periodic reconcile pass, which tracks the last full comparison rather than the last delta -- would otherwise require changing this table''s grain, and a grain change is the one retrofit this schema is designed to avoid.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'ActivityLocation'
    , @Description = N'RCRAInfo activity location this watermark covers, ''MD'' in Phase 1 (G2). Part of the natural key rather than assumed, so a second location could be loaded on its own schedule without either one disturbing the other''s progress.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'WatermarkDate'
    , @Description = N'The last date successfully loaded through, inclusive. NULL means NEVER SUCCESSFULLY LOADED, which is a load-bearing value and not a missing one: it is what tells the loader the next run must be a full load, and it is what the seeded row starts as so that a fresh database asks for everything without anyone remembering to say so. Only ever advanced by a run that succeeded -- advancing it past records that were never fetched closes the gap behind it, and no later run ever looks there again.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'OverlapDays'
    , @Description = N'How many days before WatermarkDate each incremental run reaches back. 1 by default. This is deliberate re-fetching, and it is the mechanism that makes a day-granular watermark safe. Bounded 0 to 365 by CK_config_LoadWatermark_OverlapDays -- a sanity bound rather than a policy, because a fat-fingered 3650 would quietly turn every scheduled incremental into a near-full load.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'IsEnabled'
    , @Description = N'Whether the scheduled loader should process this feed. Distinct from IsDeleted on purpose: pausing a feed during an EPA outage is an operational act, and doing it by soft-deleting the configuration row would conflate "temporarily stopped" with "retired" -- and would then have to be undone by reviving a deleted row.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'LastAdvancedByLoadRunId'
    , @Description = N'The run that last moved WatermarkDate forward. With logs.LoadRun.WatermarkBeforeDate and WatermarkAfterDate this makes every advance auditable, which matters because an incorrect advance is invisible in the data it skipped.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'LastAdvancedDateUtc'
    , @Description = N'UTC timestamp of the last advance. Distinct from WatermarkDate, which is a date in EPA''s data; this is a timestamp in ours, and the difference between them is how far behind the mirror currently is.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'Notes'
    , @Description = N'Why this row holds the values it holds -- in particular why an operator changed OverlapDays or set WatermarkDate by hand. A watermark moved by hand with no explanation is indistinguishable from one moved by mistake.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'IsDeleted'
    , @Description = N'Soft-delete flag. 1 = deleted, 0 = active. This database performs no hard deletes; every read path filters IsDeleted = 0. Use IsEnabled to pause a feed -- this column is for retiring one permanently.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'auditDeletedBy'
    , @Description = N'Login that soft-deleted the row. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'auditDeletedDateUtc'
    , @Description = N'UTC timestamp of the soft delete. Populated by DEFAULT on insert, so it is only meaningful when IsDeleted = 1; the deleting statement sets it explicitly.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'auditCreatedBy'
    , @Description = N'Login that inserted the row. On the seeded row this is the developer who deployed, because the seed runs as part of the deployment rather than as part of a load.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'auditCreatedDateUtc'
    , @Description = N'UTC timestamp of row insert. Set by DEFAULT; the seed below omits this column from its INSERT column list so the default fires.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'auditModifiedBy'
    , @Description = N'Login that last modified the row.';
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'config'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'LoadWatermark'
    , @ColumnName  = N'auditModifiedDateUtc'
    , @Description = N'UTC timestamp of last modification. The DEFAULT fires on INSERT only, so every UPDATE must set this column explicitly. On this table it is the record of when the watermark last moved as far as the audit trail is concerned, and it should always agree with LastAdvancedDateUtc.';
GO

----------------------------------------------------------------------------------------------------
-- 5. Seed: the one feed Phase 1 loads.
--
--    A CONVERGING INSERT, not a MERGE that rewrites. It inserts when the row is absent and
--    touches nothing when it is present. Re-applying the values would move auditModifiedDateUtc
--    and make the audit trail claim the configuration changed today -- and here it would also
--    silently overwrite an OverlapDays or a hand-set WatermarkDate an operator had deliberately
--    chosen, which is worse than a misleading timestamp.
--
--    The audit columns are left out of the column list on purpose, so their DEFAULTs fire.
----------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM config.LoadWatermark
                WHERE FeedName         = N'HandlerSource'
                  AND ActivityLocation = N'MD')
BEGIN
    INSERT config.LoadWatermark (FeedName, ActivityLocation, WatermarkDate, OverlapDays, IsEnabled, Notes)
    VALUES (N'HandlerSource', N'MD', NULL, 1, 1,
            N'Seeded by 340_config.LoadWatermark.sql. WatermarkDate starts NULL, which is what tells the loader its '
          + N'first run must be a full load. OverlapDays 1 covers the day-granular incremental window (G25).');
END;
GO

PRINT N'340: config.LoadWatermark created or altered, 16 column(s) described, seed row present.';
GO
