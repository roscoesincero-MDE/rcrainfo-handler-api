/***********************************************************************************************************************
Script:       020_Schemas.sql
Author:       rsincero
CreateDate:   2026-09-04
========================================================================================================================
Description:

Creates the five schemas this database uses. Run against RCRAInfo, after 010_Database.sql.

    dbo     user data          -- HandlerSource and its 25 collections, other-ids, the lookups
    auth    authn / authz      -- the web application's own users and roles (AR2)
    logs    logging            -- LoadRun, HandlerLoadStatus (AR5)
    config  app configuration  -- LoadWatermark, credential rows (AR4)
    util    utility objects    -- uspSetObjectDescription and friends

========================================================================================================================
Notes:

RE-RUNNABLE. Each CREATE SCHEMA is guarded by SCHEMA_ID and wrapped in EXEC, because CREATE SCHEMA
must be the only statement in its batch and so cannot sit directly inside an IF block.

dbo already exists in every database, so it is asserted rather than created. There is deliberately
no DROP path: dropping a schema is a hard delete, and this database has none (AR7).

Schemas are the unit that permissions are granted on in 040_Roles.sql. Adding a schema later means
revisiting that script, so the five are created up front even though auth and config hold nothing
until Workstreams C and E.

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

-- Fail fast and legibly if this was run against the wrong database. Every later script in this
-- folder assumes RCRAInfo, and the failure mode without this check is objects quietly created
-- somewhere else.
IF DB_NAME () <> N'RCRAInfo'
BEGIN
    DECLARE @wrongDb NVARCHAR (2048) =
        N'020_Schemas.sql must be run against the RCRAInfo database; current database is '
        + QUOTENAME (DB_NAME ()) + N'.';
    ;THROW 50000, @wrongDb, 1;
END;
GO

IF SCHEMA_ID (N'auth') IS NULL
BEGIN
    EXEC (N'CREATE SCHEMA auth AUTHORIZATION dbo');
    PRINT N'020: schema auth created.';
END;
GO

IF SCHEMA_ID (N'logs') IS NULL
BEGIN
    EXEC (N'CREATE SCHEMA logs AUTHORIZATION dbo');
    PRINT N'020: schema logs created.';
END;
GO

IF SCHEMA_ID (N'config') IS NULL
BEGIN
    EXEC (N'CREATE SCHEMA config AUTHORIZATION dbo');
    PRINT N'020: schema config created.';
END;
GO

IF SCHEMA_ID (N'util') IS NULL
BEGIN
    EXEC (N'CREATE SCHEMA util AUTHORIZATION dbo');
    PRINT N'020: schema util created.';
END;
GO

-- dbo is created with the database. If it is missing, something is very wrong.
IF SCHEMA_ID (N'dbo') IS NULL
BEGIN
    DECLARE @noDbo NVARCHAR (2048) = N'The dbo schema does not exist. This is not a usable database.';
    ;THROW 50000, @noDbo, 1;
END;
GO

-- -------------------------------------------------------------------------------------------------
-- Report the resulting state.
-- -------------------------------------------------------------------------------------------------
SELECT s.name                        AS SchemaName
     , USER_NAME (s.principal_id)    AS Owner
  FROM sys.schemas AS s
 WHERE s.name IN (N'dbo', N'auth', N'logs', N'config', N'util')
 ORDER BY CASE s.name
              WHEN N'dbo'    THEN 1
              WHEN N'auth'   THEN 2
              WHEN N'logs'   THEN 3
              WHEN N'config' THEN 4
              WHEN N'util'   THEN 5
          END;
GO
