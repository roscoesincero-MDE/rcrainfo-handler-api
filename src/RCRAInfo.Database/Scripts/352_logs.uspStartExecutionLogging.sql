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
ObjectName:   logs.uspStartExecutionLogging
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Opens the execution log for one procedure call and hands back the ExecutionLogId the caller will use to complete or fail
that row. Every instrumented procedure in this database calls this as the first statement inside its TRY block, and calls
it a second time from its CATCH block when a rollback has destroyed the first row.

It is the stable public name in front of logs.uspStartExecutionLoggingInsert, which does the work. Sixteen procedure
bodies name this one, so the mechanism underneath can be replaced -- by a loopback linked server, CLR, or a queue -- with
no edit to any of them. See that procedure's header for why the seam is worth having.

========================================================================================================================
Requirements and Key Dependencies:

logs.uspStartExecutionLoggingInsert, and through it logs.ExecutionLog.

Both application logins are granted EXECUTE on this procedure. Ownership chaining carries the INSERT through, so neither
holds any direct permission on logs.ExecutionLog (AR3).

========================================================================================================================
Notes:

@StartDateUtc is REQUIRED here, though the procedure underneath will accept NULL. The caller captures the start time
before doing any work and passes the same value again if the row has to be re-created after a rollback, which is what
makes StartDateUtc the time the PROCEDURE began rather than the time the row was written. Defaulting it here would
quietly turn a re-created row's start time into the time of the failure, which would understate the duration of every
failed call by exactly the time it spent failing. A missing @StartDateUtc means the instrumentation block was copied
without its DECLARE, so it is an error, not a default.

Like the procedure it wraps, this one does NOT swallow errors. It runs before BEGIN TRANSACTION, so there is no partial
work to protect and no original error to preserve; a failure means the call is about to run unlogged. The opposite
choice is correct in logs.uspRecordExecutionError, and the asymmetry is the design.

The re-creation call from a CATCH block passes the same arguments as the opening call except for
@ReCreatedAfterRollback = 1. It does not reuse the old ExecutionLogId -- that identity value is gone with the rolled-back
row and cannot be re-inserted into an IDENTITY column -- so the second row gets a new id and the caller's @ExecutionId is
reassigned before the error is recorded against it. The gap left in the identity sequence is itself visible evidence that
a row was rolled back, but it is not something a query can rely on, which is why the flag is a column.

The flag is a parameter rather than a derived value because the two calls are indistinguishable from in here. The
original design inferred re-creation from auditCreatedDateUtc > StartDateUtc; the DA0 probe measured that gap at 0 to
1998 microseconds across four rows -- one Windows clock tick either way -- with the largest value on an ordinary
successful call. It measured nothing.

========================================================================================================================
Example Usage and Performance:

-- Opening call, first statement inside the TRY block.
EXEC logs.uspStartExecutionLogging
      @ProcedureName          = @ProcName
    , @KeyParameters          = @KeyParameters
    , @StartDateUtc           = @StartTimeUtc
    , @ReCreatedAfterRollback = 0
    , @ExecutionLogId         = @ExecutionId OUTPUT;

-- Re-creation call, in the CATCH block, after the rollback. Same arguments but for the flag.
IF @ExecutionId IS NULL
   OR NOT EXISTS (SELECT 1 FROM logs.ExecutionLog WHERE ExecutionLogId = @ExecutionId)
BEGIN
    EXEC logs.uspStartExecutionLogging
          @ProcedureName          = @ProcName
        , @KeyParameters          = @KeyParameters
        , @StartDateUtc           = @StartTimeUtc
        , @ReCreatedAfterRollback = 1
        , @ExecutionLogId         = @ExecutionId OUTPUT;
END;

One nested procedure call and one singleton insert, on the critical path of every instrumented call in the database.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA0, requirement AR8 [R12]. Adapted from the
											template MDE supplied on 2026-09-05: renamed to the project's usp prefix and
											PascalCase, arguments passed by name rather than by position, the
											commented-out four-part call removed in favour of the explanation of the
											seam in logs.uspStartExecutionLoggingInsert, and @StartDateUtc made
											mandatory here rather than optional.
2026-09-05	rsincero						Added @ReCreatedAfterRollback, passed straight through, after the DA0 probe
											showed re-creation could not be inferred from the timestamps.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspStartExecutionLogging
      @ProcedureName          NVARCHAR (300)
    , @KeyParameters          NVARCHAR (MAX) = NULL
    , @StartDateUtc           DATETIME2      = NULL
    , @ReCreatedAfterRollback BIT            = 0
    , @ExecutionLogId         BIGINT         = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @msg NVARCHAR (2048);

    SET @ExecutionLogId = NULL;

    IF @StartDateUtc IS NULL
    BEGIN
        SET @msg = N'@StartDateUtc is required. The instrumentation block captures it before doing any '
                 + N'work and passes the same value again when the log row is re-created after a '
                 + N'rollback; defaulting it here would report the time of the failure as the start of '
                 + N'the call. Received NULL from '
                 + ISNULL (@ProcedureName, N'an unnamed caller') + N'.';
        ;THROW 50000, @msg, 1;
    END;

    -- -------------------------------------------------------------------------------------------------
    -- The seam. Everything about making this insert survive the caller's rollback lives on the other
    -- side of this call, so replacing it changes nothing here and nothing in the sixteen callers.
    -- -------------------------------------------------------------------------------------------------
    EXEC logs.uspStartExecutionLoggingInsert
          @ProcedureName          = @ProcedureName
        , @KeyParameters          = @KeyParameters
        , @StartDateUtc           = @StartDateUtc
        , @ReCreatedAfterRollback = @ReCreatedAfterRollback
        , @ExecutionLogId         = @ExecutionLogId OUTPUT;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspStartExecutionLogging'
    , @Description = N'Opens the execution log for one procedure call and returns the ExecutionLogId the caller uses to complete or fail that row. Called as the first statement inside every instrumented procedure''s TRY block, and again from its CATCH block -- with @ReCreatedAfterRollback = 1 -- when a rollback destroyed the first row. The stable public name in front of logs.uspStartExecutionLoggingInsert, so the mechanism underneath can be replaced without editing any caller. Requires @StartDateUtc even though the procedure it wraps does not: the caller captures it before doing any work and passes the same value again on re-creation, which is what keeps StartDateUtc the time the procedure began rather than the time the row was written and stops a failed call''s duration being understated by the time it spent failing. Does not swallow errors -- it runs before BEGIN TRANSACTION, so a failure here means the call is about to run unlogged.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. Each procedure script carries its own, which is what makes the query at the end of
-- script 050 the authoritative answer to what the two applications can do.
--
-- This procedure is granted; logs.uspStartExecutionLoggingInsert behind it is NOT. Both
-- applications call this one from every instrumented procedure, and ownership chaining carries the
-- INSERT through to logs.ExecutionLog, which script 050 denies them directly (AR3).
--
-- GRANT is idempotent, so this is safe on a re-run. The guard is for the role, not the grant: it
-- lets this script run against a database where script 050 has not been applied yet.
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspStartExecutionLogging TO RCRAInfoLoaderRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspStartExecutionLogging TO RCRAInfoMonitorRole;
END;
GO

PRINT N'352: logs.uspStartExecutionLogging created or altered, EXECUTE granted to both roles.';
GO
