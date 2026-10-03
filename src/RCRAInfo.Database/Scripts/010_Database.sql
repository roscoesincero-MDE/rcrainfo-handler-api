/***********************************************************************************************************************
Script:       010_Database.sql
Author:       rsincero
CreateDate:   2026-09-04
========================================================================================================================
Description:

Creates the RCRAInfo database and pins it to the SQL Server 2022 behaviour set.

Run this against the master database of the target instance. It is the first script in the
deployment order; 020_Schemas.sql follows.

========================================================================================================================
Notes:

RE-RUNNABLE. A developer runs this by hand (G3), so a second run must change nothing and report
nothing. CREATE DATABASE is guarded by DB_ID and wrapped in EXEC, because CREATE DATABASE must be
the only statement in its batch and therefore cannot sit directly inside an IF block.

The compatibility level and the database options below are re-applied on every run by design: each
ALTER DATABASE ... SET is a no-op when the setting already holds the requested value, so it
converges rather than churning.

Deliberately NOT here, because both need exclusive access to the database and would kill other
sessions on a second run -- see Deployment/README.md:
    - READ_COMMITTED_SNAPSHOT ON  (recommended: the monitoring web app reads run status while the
      loader is mid-merge, which is the textbook reader/writer blocking case)
    - the recovery model, which is a DBA decision tied to the backup schedule (G17)

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-04	rsincero						Initial version. Workstream A4.
***********************************************************************************************************************/

SET XACT_ABORT ON;
-- Not decoration: sqlcmd defaults QUOTED_IDENTIFIER OFF where every other client defaults it ON, the
-- setting is BAKED IN at CREATE time, and a module or session carrying it OFF cannot run DML against a
-- table with a filtered index (error 1934). Every unique constraint here is one. Set it so that a hand
-- run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- -------------------------------------------------------------------------------------------------
-- 1. The database.
--
--    Collation is stated explicitly rather than inherited from the server default. The ETS database
--    epal_issi is SQL_Latin1_General_CP1_CI_AS, Phase 2 compares and moves data between the two,
--    and a collation conflict surfaces as a query-time error on a join -- not at deploy time. In
--    UAT and Production the two databases are on different servers (Analysis 5.3), so the server
--    default cannot be relied on to match. Confirm this value against the UAT and Production ETS
--    instances when G17 is answered.
-- -------------------------------------------------------------------------------------------------
IF DB_ID (N'RCRAInfo') IS NULL
BEGIN
    EXEC (N'CREATE DATABASE RCRAInfo COLLATE SQL_Latin1_General_CP1_CI_AS');
    PRINT N'010: database RCRAInfo created.';
END;
ELSE
BEGIN
    PRINT N'010: database RCRAInfo already exists; nothing to create.';
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 2. Target the SQL Server 2022 behaviour set.
--
--    The workstation runs SQL Server 2025 (compatibility level 170); UAT and Production run 2022.
--    Level 160 makes the optimizer behave like the target. It is NOT a feature gate and does not
--    block 2025-only syntax -- that is the job of the validator hook and the ScriptDom 160 parse
--    check in build/. Both controls are needed; neither is sufficient alone.
-- -------------------------------------------------------------------------------------------------
IF EXISTS (SELECT 1
             FROM sys.databases
            WHERE name                = N'RCRAInfo'
              AND compatibility_level <> 160)
BEGIN
    ALTER DATABASE RCRAInfo SET COMPATIBILITY_LEVEL = 160;
    PRINT N'010: compatibility level set to 160 (SQL Server 2022).';
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 3. Database options that are safe to re-apply and worth being explicit about.
--
--    AUTO_CLOSE OFF     -- an overnight batch load must not pay first-connection startup cost
--    AUTO_SHRINK OFF    -- shrink fragments the very indexes the incremental load depends on
--    AUTO_CREATE/UPDATE_STATISTICS ON -- OPENJSON produces no statistics of its own (G32), so the
--                          statistics that do exist matter more here than usual
--    QUOTED_IDENTIFIER / ANSI_NULLS ON -- required for filtered indexes, which every unique
--                          constraint in this database is (soft delete, AR7)
-- -------------------------------------------------------------------------------------------------
ALTER DATABASE RCRAInfo SET AUTO_CLOSE OFF;
ALTER DATABASE RCRAInfo SET AUTO_SHRINK OFF;
ALTER DATABASE RCRAInfo SET AUTO_CREATE_STATISTICS ON;
ALTER DATABASE RCRAInfo SET AUTO_UPDATE_STATISTICS ON;
ALTER DATABASE RCRAInfo SET QUOTED_IDENTIFIER ON;
ALTER DATABASE RCRAInfo SET ANSI_NULLS ON;
GO

-- -------------------------------------------------------------------------------------------------
-- 4. Report the resulting state, so a hand-run script says what it did.
-- -------------------------------------------------------------------------------------------------
SELECT d.name                          AS DatabaseName
     , d.compatibility_level           AS CompatibilityLevel
     , d.collation_name                AS Collation
     , d.recovery_model_desc           AS RecoveryModel
     , d.is_read_committed_snapshot_on AS RcsiEnabled
  FROM sys.databases AS d
 WHERE d.name = N'RCRAInfo';
GO
