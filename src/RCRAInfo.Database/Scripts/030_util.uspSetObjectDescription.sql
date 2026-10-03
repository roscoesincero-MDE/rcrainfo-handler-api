-- SET XACT_ABORT ON sits ABOVE the header block deliberately, and moving it back below would restore a
-- real defect. The GO on the next line ends the batch, and sys.sql_modules stores only the batch that
-- contains CREATE -- so a header placed AFTER this GO is invisible to anyone reading the procedure out
-- of the database through sp_helptext, OBJECT_DEFINITION, or SSMS "Script as CREATE", which is where a
-- maintainer actually reads it. Every view in this database already carried its header inside the
-- definition because nothing separates the two; no procedure did until this was corrected.
SET XACT_ABORT ON;
-- SET QUOTED_IDENTIFIER ON is not decoration, and it is not the invoking client's business: sqlcmd
-- defaults it OFF where every other client defaults it ON, the setting is BAKED IN at CREATE time and
-- stored in sys.sql_modules, and a module compiled with it OFF cannot UPDATE a table carrying a
-- filtered index -- error 1934. EVERY unique constraint in this database is a filtered index
-- (WHERE IsDeleted = 0), so that is every table. Setting it here means a hand run cannot get it wrong.
SET QUOTED_IDENTIFIER ON;
GO

/***********************************************************************************************************************
ObjectName:   util.uspSetObjectDescription
Author:       rsincero
CreateDate:   2026-09-04
========================================================================================================================
Description:

Adds or updates an MS_Description extended property on a table, view, procedure, function, or on one column of a table
or view. Idempotent: it checks sys.extended_properties and picks sp_addextendedproperty or sp_updateextendedproperty
accordingly, so a deployment script can be re-run and an improved wording replaces the old one instead of erroring.

This is the ONLY sanctioned way to set a description in this database. It exists because neither raw procedure is
re-runnable on its own -- sp_addextendedproperty fails when the property already exists, sp_updateextendedproperty fails
when it does not -- and a developer runs these scripts by hand (G3). The requirement is a description on every table AND
every column, so without this helper the re-run exposure is the whole schema rather than one object.

It is therefore the first object created in the database, before any table script.

========================================================================================================================
Requirements and Key Dependencies:

sys.extended_properties, sys.objects, sys.schemas, sys.columns, sys.sp_addextendedproperty, sys.sp_updateextendedproperty

Callers need ALTER on the object being described. Neither application login has it, and neither should: descriptions are
DDL-adjacent metadata set at deployment time by the developer (G3), not by the running applications.

========================================================================================================================
Notes:

@Description is NVARCHAR (3750) because an extended property value is a sql_variant capped at 7500 bytes, which is 3750
NVARCHAR characters. A longer description is truncated by the engine with no warning, so the parameter is the narrower
of the two limits on purpose.

The object and column are verified to exist before the property is set. sp_addextendedproperty on a missing object fails
with a message that names the property rather than the object, which is a poor error to hand a developer at 2am.

@ObjectType is the extended-property level1type, not a SQL type name. It must be one of TABLE, VIEW, PROCEDURE or
FUNCTION. TABLE covers table-valued objects for property purposes; a table-valued function is described with FUNCTION.

========================================================================================================================
Example Usage and Performance:

-- Table
exec util.uspSetObjectDescription
      @SchemaName  = N'dbo'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerSource'
    , @Description = N'Handler source records mirrored from EPA RCRAInfo.';

-- Column
exec util.uspSetObjectDescription
      @SchemaName  = N'dbo'
    , @ObjectType  = N'TABLE'
    , @ObjectName  = N'HandlerSource'
    , @ColumnName  = N'HandlerId'
    , @Description = N'EPA-assigned RCRA handler identifier, e.g. ''MD0000123456''.';

Cost is two catalog lookups and one system procedure call. It is called once per column at deployment time and never by
the applications, so its performance is not on any critical path.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-04	rsincero						Initial version. Workstream A6. Adapted from the sql-objects skill template,
											with three corrections: THROW is preceded by a semicolon (bare THROW after
											BEGIN is a syntax error), the message is passed as a variable, and the target
											object and column are verified to exist before the property is set.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE util.uspSetObjectDescription
      @SchemaName  SYSNAME
    , @ObjectType  SYSNAME              -- TABLE | VIEW | PROCEDURE | FUNCTION
    , @ObjectName  SYSNAME
    , @Description NVARCHAR (3750)
    , @ColumnName  SYSNAME = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @msg NVARCHAR (2048);

    -- ---------------------------------------------------------------------------------------------
    -- Argument validation. Every failure here names what was wrong with the call, because the
    -- underlying system procedures do not.
    -- ---------------------------------------------------------------------------------------------
    IF @ObjectType NOT IN (N'TABLE', N'VIEW', N'PROCEDURE', N'FUNCTION')
    BEGIN
        SET @msg = N'@ObjectType must be TABLE, VIEW, PROCEDURE or FUNCTION; received '
                 + ISNULL (QUOTENAME (@ObjectType), N'NULL') + N'.';
        ;THROW 50000, @msg, 1;
    END;

    IF @Description IS NULL OR LEN (LTRIM (RTRIM (@Description))) = 0
    BEGIN
        SET @msg = N'@Description is required. An object with no authoritative description should '
                 + N'say so explicitly (for example ''TODO: awaiting EPA data dictionary'') rather '
                 + N'than carry an empty or invented one.';
        ;THROW 50000, @msg, 1;
    END;

    DECLARE @objectId INT =
        OBJECT_ID (QUOTENAME (@SchemaName) + N'.' + QUOTENAME (@ObjectName));

    IF @objectId IS NULL
    BEGIN
        SET @msg = N'Object ' + QUOTENAME (@SchemaName) + N'.' + QUOTENAME (@ObjectName)
                 + N' does not exist, so it cannot be described. Run the script that creates it first.';
        ;THROW 50000, @msg, 1;
    END;

    IF @ColumnName IS NOT NULL
       AND NOT EXISTS (SELECT 1
                         FROM sys.columns AS c
                        WHERE c.object_id = @objectId
                          AND c.name      = @ColumnName)
    BEGIN
        SET @msg = N'Column ' + QUOTENAME (@ColumnName) + N' does not exist on '
                 + QUOTENAME (@SchemaName) + N'.' + QUOTENAME (@ObjectName) + N'.';
        ;THROW 50000, @msg, 1;
    END;

    -- ---------------------------------------------------------------------------------------------
    -- Add or update. class = 1 is an object-or-column property; minor_id = 0 is the object itself.
    -- ---------------------------------------------------------------------------------------------
    DECLARE @alreadyExists BIT =
    (
        SELECT CASE WHEN EXISTS
        (
            SELECT 1
              FROM sys.extended_properties AS ep
              LEFT JOIN sys.columns        AS c ON c.object_id = ep.major_id
                                               AND c.column_id = ep.minor_id
             WHERE ep.class    = 1
               AND ep.name     = N'MS_Description'
               AND ep.major_id = @objectId
               AND (
                        (@ColumnName IS NULL     AND ep.minor_id = 0)
                     OR (@ColumnName IS NOT NULL AND c.name      = @ColumnName)
                   )
        ) THEN 1 ELSE 0 END
    );

    IF @alreadyExists = 1
    BEGIN
        IF @ColumnName IS NULL
            EXEC sys.sp_updateextendedproperty
                  @name       = N'MS_Description', @value      = @Description
                , @level0type = N'SCHEMA',         @level0name = @SchemaName
                , @level1type = @ObjectType,       @level1name = @ObjectName;
        ELSE
            EXEC sys.sp_updateextendedproperty
                  @name       = N'MS_Description', @value      = @Description
                , @level0type = N'SCHEMA',         @level0name = @SchemaName
                , @level1type = @ObjectType,       @level1name = @ObjectName
                , @level2type = N'COLUMN',         @level2name = @ColumnName;
    END;
    ELSE
    BEGIN
        IF @ColumnName IS NULL
            EXEC sys.sp_addextendedproperty
                  @name       = N'MS_Description', @value      = @Description
                , @level0type = N'SCHEMA',         @level0name = @SchemaName
                , @level1type = @ObjectType,       @level1name = @ObjectName;
        ELSE
            EXEC sys.sp_addextendedproperty
                  @name       = N'MS_Description', @value      = @Description
                , @level0type = N'SCHEMA',         @level0name = @SchemaName
                , @level1type = @ObjectType,       @level1name = @ObjectName
                , @level2type = N'COLUMN',         @level2name = @ColumnName;
    END;

    RETURN 0;
END;
GO

-- -------------------------------------------------------------------------------------------------
-- Describe the helper with itself. This is the first exercise of the add-or-update path: on a fresh
-- database it takes the add branch, and on every re-run it takes the update branch. If this line
-- succeeds twice, the mechanism the whole schema depends on is working.
-- -------------------------------------------------------------------------------------------------
EXEC util.uspSetObjectDescription
      @SchemaName  = N'util'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspSetObjectDescription'
    , @Description = N'Adds or updates an MS_Description extended property on an object or one of its columns. The only sanctioned way to set a description in this database: sp_addextendedproperty fails if the property exists and sp_updateextendedproperty fails if it does not, so neither is safe in a script a developer re-runs by hand.';
GO

PRINT N'030: util.uspSetObjectDescription created or altered, and described.';
GO
