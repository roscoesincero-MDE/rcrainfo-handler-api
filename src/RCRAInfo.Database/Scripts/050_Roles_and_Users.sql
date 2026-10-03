/***********************************************************************************************************************
Script:       050_Roles_and_Users.sql
Author:       rsincero
CreateDate:   2026-09-04
========================================================================================================================
Description:

Creates the two database roles, maps the two application logins to users in them, and denies both roles every direct
data permission. Run against RCRAInfo, after 040_Logins.sql.

    RCRAInfoLoaderRole   <- user RCRAInfoLoader   <- login RCRAInfoLoader    (console app, AR1)
    RCRAInfoMonitorRole  <- user RCRAInfoMonitor  <- login RCRAInfoMonitor   (web app, AR2)

Rights are granted to the ROLES, never to the users, so UAT and Production can be scripted identically even if the
login names differ. EXECUTE on individual procedures is granted by the script that creates each procedure, not here --
a procedure ships with its own grant, so the permission cannot be forgotten when the procedure is added.

========================================================================================================================
Requirements and Key Dependencies:

040_Logins.sql must have run. Needs no passwords: a user is created FROM LOGIN and carries none.

========================================================================================================================
Notes:

RE-RUNNABLE. Roles, users and role membership are each guarded; DENY is idempotent by nature, so it is simply
re-applied.

WHY DENY AND NOT MERELY "NO GRANT". Absence of a grant already blocks access. The explicit DENY exists to survive the
next person: adding a role to db_datareader "just to test something" is a two-second act that would otherwise hand both
applications read access to every table in the database. DENY beats GRANT, including a grant inherited through a fixed
database role, so this posture holds even then.

THIS CONSTRAINS WORKSTREAM DA, AND THE CONSTRAINT IS EASY TO TRIP OVER. A procedure reads its tables through ownership
chaining: the procedure and the table have the same owner (dbo), so permissions on the table are not checked at all and
the DENY above is not consulted. That chain BREAKS the moment a procedure builds and runs dynamic SQL, because the
dynamic batch is a separate context whose permissions are checked against the CALLER. So:

    - A read procedure that sorts with a CASE-based ORDER BY works under this posture. Prefer it.
    - A read procedure that builds its ORDER BY through sp_executesql will fail for both application logins with a
      permission error, not a wrong answer. If dynamic SQL is genuinely needed, the procedure must be declared
      WITH EXECUTE AS OWNER. It must NOT be fixed by granting SELECT on the table.

That failure is loud and arrives at the DA1 design review rather than in production, which is the point.

========================================================================================================================
Example Usage and Performance:

sqlcmd -S <server> -E -C -b -d RCRAInfo -i 050_Roles_and_Users.sql

To verify the posture from the other side, connect AS RCRAInfoLoader and confirm this is refused:
    select top 1 * from dbo.HandlerSource;

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-04	rsincero						Initial version. Workstream A5.
***********************************************************************************************************************/

SET XACT_ABORT ON;
-- Not decoration: sqlcmd defaults QUOTED_IDENTIFIER OFF where every other client defaults it ON, the
-- setting is BAKED IN at CREATE time, and a module or session carrying it OFF cannot run DML against a
-- table with a filtered index (error 1934). Every unique constraint here is one. Set it so that a hand
-- run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

IF DB_NAME () <> N'RCRAInfo'
BEGIN
    DECLARE @wrongDb NVARCHAR (2048) =
        N'050_Roles_and_Users.sql must be run against the RCRAInfo database; current database is '
        + QUOTENAME (DB_NAME ()) + N'.';
    ;THROW 50000, @wrongDb, 1;
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 1. The roles.
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NULL
BEGIN
    CREATE ROLE RCRAInfoLoaderRole AUTHORIZATION dbo;
    PRINT N'050: role RCRAInfoLoaderRole created.';
END;
GO

IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NULL
BEGIN
    CREATE ROLE RCRAInfoMonitorRole AUTHORIZATION dbo;
    PRINT N'050: role RCRAInfoMonitorRole created.';
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 2. The users, each mapped from its login. Created only when the login exists, so this script
--    reports a clear reason rather than a raw engine error if 040 has not been run.
-- -------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'RCRAInfoLoader')
    OR NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'RCRAInfoMonitor')
BEGIN
    DECLARE @noLogin NVARCHAR (2048) =
        N'One or both application logins are missing. Run 040_Logins.sql against master first.';
    ;THROW 50000, @noLogin, 1;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoader') IS NULL
BEGIN
    CREATE USER RCRAInfoLoader FOR LOGIN RCRAInfoLoader WITH DEFAULT_SCHEMA = dbo;
    PRINT N'050: user RCRAInfoLoader created.';
END;
GO

IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitor') IS NULL
BEGIN
    CREATE USER RCRAInfoMonitor FOR LOGIN RCRAInfoMonitor WITH DEFAULT_SCHEMA = dbo;
    PRINT N'050: user RCRAInfoMonitor created.';
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 3. Role membership.
-- -------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.database_role_members AS drm
                 JOIN sys.database_principals   AS r ON r.principal_id = drm.role_principal_id
                 JOIN sys.database_principals   AS m ON m.principal_id = drm.member_principal_id
                WHERE r.name = N'RCRAInfoLoaderRole'
                  AND m.name = N'RCRAInfoLoader')
BEGIN
    ALTER ROLE RCRAInfoLoaderRole ADD MEMBER RCRAInfoLoader;
    PRINT N'050: RCRAInfoLoader added to RCRAInfoLoaderRole.';
END;
GO

IF NOT EXISTS (SELECT 1
                 FROM sys.database_role_members AS drm
                 JOIN sys.database_principals   AS r ON r.principal_id = drm.role_principal_id
                 JOIN sys.database_principals   AS m ON m.principal_id = drm.member_principal_id
                WHERE r.name = N'RCRAInfoMonitorRole'
                  AND m.name = N'RCRAInfoMonitor')
BEGIN
    ALTER ROLE RCRAInfoMonitorRole ADD MEMBER RCRAInfoMonitor;
    PRINT N'050: RCRAInfoMonitor added to RCRAInfoMonitorRole.';
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 4. Deny every direct data permission on every schema, to both roles.
--
--    Read the Notes block above before changing anything here. In particular: this does NOT stop a
--    stored procedure from reading these tables, because ownership chaining skips the check
--    entirely. It stops the application logins from reading them directly, which is the whole
--    point of routing every operation through a procedure.
--
--    REFERENCES is included because it leaks column names and lets a principal build a foreign key
--    against a table it cannot read.
-- -------------------------------------------------------------------------------------------------
DENY SELECT, INSERT, UPDATE, DELETE, REFERENCES ON SCHEMA::dbo    TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
DENY SELECT, INSERT, UPDATE, DELETE, REFERENCES ON SCHEMA::auth   TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
DENY SELECT, INSERT, UPDATE, DELETE, REFERENCES ON SCHEMA::logs   TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
DENY SELECT, INSERT, UPDATE, DELETE, REFERENCES ON SCHEMA::config TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
DENY SELECT, INSERT, UPDATE, DELETE, REFERENCES ON SCHEMA::util   TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
GO

-- The util schema holds deployment-time metadata helpers. Neither application calls them, and a
-- procedure that needs one reaches it through ownership chaining.
DENY EXECUTE ON SCHEMA::util TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
GO

-- Deny the catalog-reconnaissance permissions too. Neither application enumerates the schema:
-- EF Core is a procedure caller here (AR: Revision 6), not a model-discovering ORM.
DENY VIEW DEFINITION      TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
DENY VIEW DATABASE STATE  TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
DENY ALTER ANY SCHEMA     TO RCRAInfoLoaderRole, RCRAInfoMonitorRole;
GO

PRINT N'050: direct data permissions denied on all five schemas for both roles.';
GO

-- -------------------------------------------------------------------------------------------------
-- 5. Assert neither user has been placed in a fixed database role. db_datareader, db_owner and
--    friends are the shortcut that undoes everything above, and DENY only covers what DENY names.
-- -------------------------------------------------------------------------------------------------
IF EXISTS (SELECT 1
             FROM sys.database_role_members AS drm
             JOIN sys.database_principals   AS r ON r.principal_id = drm.role_principal_id
             JOIN sys.database_principals   AS m ON m.principal_id = drm.member_principal_id
            WHERE m.name IN (N'RCRAInfoLoader', N'RCRAInfoMonitor')
              AND r.is_fixed_role = 1)
BEGIN
    DECLARE @fixedRole NVARCHAR (2048) =
        N'An application user is a member of a fixed database role (db_datareader, db_owner or '
      + N'similar). Remove it: AR3 gives each application only EXECUTE on the procedures it needs.';
    ;THROW 50000, @fixedRole, 1;
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 6. Report the grant surface. This is the drift check: as Workstream DA adds procedures, each
--    with its own GRANT EXECUTE, this query is the authoritative answer to "what can these two
--    applications actually do?". Run it after every deployment and read it.
-- -------------------------------------------------------------------------------------------------
SELECT dp.name                                        AS Grantee
     , p.state_desc                                   AS PermissionState
     , p.permission_name                              AS Permission
     , p.class_desc                                   AS OnClass
     , COALESCE (QUOTENAME (s.name) + N'.' + QUOTENAME (o.name)
               , QUOTENAME (sch.name)
               , N'(database)')                       AS OnObject
  FROM sys.database_permissions AS p
  JOIN sys.database_principals  AS dp  ON dp.principal_id = p.grantee_principal_id
  LEFT JOIN sys.objects         AS o   ON o.object_id  = p.major_id
                                      AND p.class      = 1
  LEFT JOIN sys.schemas         AS s   ON s.schema_id  = o.schema_id
  LEFT JOIN sys.schemas         AS sch ON sch.schema_id = p.major_id
                                      AND p.class       = 3
 WHERE dp.name IN (N'RCRAInfoLoaderRole', N'RCRAInfoMonitorRole'
                 , N'RCRAInfoLoader',     N'RCRAInfoMonitor')
 ORDER BY dp.name, p.state_desc, p.permission_name, OnObject;
GO
