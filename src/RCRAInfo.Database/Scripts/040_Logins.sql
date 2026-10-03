/***********************************************************************************************************************
Script:       040_Logins.sql
Author:       rsincero
CreateDate:   2026-09-04
========================================================================================================================
Description:

Creates the two application logins. Run against master, after 030.

    RCRAInfoLoader   -- the console application (AR1). Retrieves from EPA, writes to RCRAInfo.
    RCRAInfoMonitor  -- the web application (AR2). Reads status, notifies users. Never writes handler data.

Neither login holds DDL. DDL is the developer's own account (G3), which is not created here and never appears in a
deployment script.

========================================================================================================================
Requirements and Key Dependencies:

This script references $(LoaderPassword) and $(MonitorPassword). The passwords are NOT in this file and must never be
added to it: this script is in source control, and a secret committed once is committed forever.

Supply them through the ENVIRONMENT, not with -v. sqlcmd resolves an unset $(Var) from its own environment, so nothing
secret reaches the command line, where any other user on the machine can read it out of the process list:

    $env:LoaderPassword  = '<generated>'
    $env:MonitorPassword = '<generated>'
    sqlcmd -S <server> -E -C -b -I -d master -i 040_Logins.sql

Or, preferably, run Deployment\Deploy-Database.ps1, which does exactly that and additionally skips the prompt entirely
when both logins already exist.

Note that sqlcmd substitutes $( ) while READING the file, before the server sees the IF that guards CREATE LOGIN, so
both variables must resolve even on a re-run that will create nothing.

Generate them with a password manager or:  [System.Web.Security.Membership]::GeneratePassword(32, 8)
Record them in the password manager at generation time. 050 does not need them; Workstream C does, once per machine.

========================================================================================================================
Notes:

RE-RUNNABLE, with one deliberate asymmetry worth understanding.

CREATE LOGIN is guarded by sys.server_principals, so a second run creates nothing. The password is NOT re-applied on a
re-run, because ALTER LOGIN ... WITH PASSWORD on an existing login would silently rotate a working credential and break
whatever DPAPI-encrypted copy the applications already hold on that machine. Re-running this script therefore never
changes a password. Rotating one is a separate, deliberate act -- see Deployment/README.md.

Two logins, not one, because AR3 gives the applications different rights and a shared login makes that unenforceable.
The two applications also share a machine in every environment (G18), so the login is the only thing that distinguishes
them at the database boundary.

CHECK_POLICY is left at its default ON so Windows password policy applies. CHECK_EXPIRATION is left OFF: these are
service accounts run unattended by Task Scheduler, and an expiring password on an overnight batch fails at 2am with no
operator present. That is a deliberate trade, not an oversight -- rotation is scheduled instead.

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

-- -------------------------------------------------------------------------------------------------
-- 1. The console application's login (AR1).
-- -------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.server_principals
                WHERE name = N'RCRAInfoLoader'
                  AND type = 'S')
BEGIN
    CREATE LOGIN RCRAInfoLoader
        WITH PASSWORD          = '$(LoaderPassword)'
           , DEFAULT_DATABASE  = RCRAInfo
           , CHECK_POLICY      = ON
           , CHECK_EXPIRATION  = OFF;

    PRINT N'040: login RCRAInfoLoader created.';
END;
ELSE
BEGIN
    PRINT N'040: login RCRAInfoLoader already exists; password left untouched.';
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 2. The web application's login (AR2).
-- -------------------------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1
                 FROM sys.server_principals
                WHERE name = N'RCRAInfoMonitor'
                  AND type = 'S')
BEGIN
    CREATE LOGIN RCRAInfoMonitor
        WITH PASSWORD          = '$(MonitorPassword)'
           , DEFAULT_DATABASE  = RCRAInfo
           , CHECK_POLICY      = ON
           , CHECK_EXPIRATION  = OFF;

    PRINT N'040: login RCRAInfoMonitor created.';
END;
ELSE
BEGIN
    PRINT N'040: login RCRAInfoMonitor already exists; password left untouched.';
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 3. Assert neither login has picked up a server role. A server role is a way around every
--    database-level restriction 050 puts in place, so this is checked rather than assumed.
-- -------------------------------------------------------------------------------------------------
IF EXISTS (SELECT 1
             FROM sys.server_role_members AS srm
             JOIN sys.server_principals   AS m ON m.principal_id = srm.member_principal_id
             JOIN sys.server_principals   AS r ON r.principal_id = srm.role_principal_id
            WHERE m.name IN (N'RCRAInfoLoader', N'RCRAInfoMonitor')
              AND r.name <> N'public')
BEGIN
    DECLARE @roleMsg NVARCHAR (2048) =
        N'An application login is a member of a server role. Neither application login may hold '
      + N'any server role: it would bypass the database-level restrictions in 050_Roles_and_Users.sql. '
      + N'Investigate before continuing.';
    ;THROW 50000, @roleMsg, 1;
END;
GO

-- -------------------------------------------------------------------------------------------------
-- 4. Report the resulting state.
-- -------------------------------------------------------------------------------------------------
SELECT sp.name              AS LoginName
     , sp.type_desc         AS LoginType
     , sp.is_disabled       AS IsDisabled
     , sp.default_database_name AS DefaultDatabase
     , l.is_policy_checked  AS PolicyChecked
     , l.is_expiration_checked AS ExpirationChecked
  FROM sys.server_principals AS sp
  LEFT JOIN sys.sql_logins   AS l ON l.principal_id = sp.principal_id
 WHERE sp.name IN (N'RCRAInfoLoader', N'RCRAInfoMonitor')
 ORDER BY sp.name;
GO
