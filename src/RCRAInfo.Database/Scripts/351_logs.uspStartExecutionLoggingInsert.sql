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
ObjectName:   logs.uspStartExecutionLoggingInsert
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Writes the opening row of logs.ExecutionLog for one procedure call and returns its ExecutionLogId. This is the only
INSERT into that table on the normal path.

It is separate from logs.uspStartExecutionLogging, which is the procedure every instrumented body actually calls, and the
split is deliberate rather than layering for its own sake. This is the SEAM. The log row is written inside the caller's
transaction, so when the caller fails and rolls back, this row goes with it -- which is why the CATCH block in the AR8
template has to re-create it. The durable fix is to make the insert run outside the caller's transaction, and T-SQL has
no autonomous transaction; the only mechanisms that do are a loopback linked server, a CLR connection, or a queue read
by a separate session, and every one of them replaces exactly this procedure and nothing else. MDE's supplied template
anticipated the first of those, carrying a commented-out four-part call to a logging database on another server.

Keeping the seam here means the resurrection logic in the template stays correct either way: if the insert becomes
autonomous, the row is never rolled back, the resurrection check finds it, and nothing else changes.

========================================================================================================================
Requirements and Key Dependencies:

logs.ExecutionLog.

Called by logs.uspStartExecutionLogging, and by logs.uspRecordExecutionError's orphan path only indirectly -- that path
uses its own MERGE, because by then there is an error to record and a bare insert would lose it.

Callers need EXECUTE on this procedure and nothing else. It and logs.ExecutionLog share a schema and an owner, so
ownership chaining supplies the INSERT: no application login holds, or needs, any direct permission on the table (AR3).

========================================================================================================================
Notes:

@ProcedureName is NVARCHAR (300) to match the column rather than the 261 characters QUOTENAME of two sysname parts can
actually produce. A parameter narrower than its column truncates the value before the column ever sees it, and silent
truncation inside an error handler is the failure mode this substrate exists to prevent (G33).

@ReCreatedAfterRollback is 0 on the opening call and 1 on the call the caller's CATCH block makes after a rollback
destroyed the first row. It is a parameter rather than something this procedure could work out for itself, because from
here the two calls are indistinguishable. The DA0 probe measured why it has to exist: the original design read
auditCreatedDateUtc > StartDateUtc as the marker, and that gap is one Windows clock tick or zero on every row, whatever
its history.

Successful and the seven audit columns are OMITTED from the INSERT so their DEFAULTs fire. That is the project rule for
create-audit stamping, and here it also carries meaning: Successful defaults to 0, so a call is unsuccessful until
something proves otherwise, and a process that dies without reaching either the success path or the CATCH block leaves a
row that correctly reads as not-succeeded.

StartDateUtc is passed in, not defaulted, because the caller captures it before doing any work and passes the same value
again if the row has to be re-created after a rollback. Passing NULL falls back to the column DEFAULT of SYSUTCDATETIME (),
which is right for an ad-hoc call and wrong for an instrumented procedure -- so the wrapper requires it.

This procedure does NOT swallow errors, and that asymmetry with logs.uspRecordExecutionError is the point. Nothing has
happened yet when this runs, so there is no error worth protecting and no partial work at risk; a failure here means the
call is about to run unlogged, which is the compliance hole the requirement exists to close. Let it throw.

@ExecutionLogId is OUTPUT rather than a return value because RETURN is INT and this key is BIGINT.

========================================================================================================================
Example Usage and Performance:

DECLARE @ExecutionLogId BIGINT;

EXEC logs.uspStartExecutionLoggingInsert
      @ProcedureName          = N'[dbo].[uspMergeHandlerSourceBatch]'
    , @KeyParameters          = N'LoadRunId=418, HandlerCount=500'
    , @StartDateUtc           = N'2026-09-05T14:02:11.1234567'
    , @ReCreatedAfterRollback = 0
    , @ExecutionLogId         = @ExecutionLogId OUTPUT;

One singleton insert at the end of a clustered identity. It runs once per instrumented call, so it is on the critical
path of every procedure in the database -- which is why logs.ExecutionLog carries two indexes rather than the four that
would answer every monitoring question, and why OPTIMIZE_FOR_SEQUENTIAL_KEY is ON there and nowhere else.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA0, requirement AR8 [R12]. Adapted from the
											template MDE supplied on 2026-09-05: renamed to the project's usp prefix and
											PascalCase, parameters widened to match their columns, the commented-out
											four-part call replaced by the explanation of the seam above, and the audit
											columns omitted from the INSERT so the table defaults stamp them.
2026-09-05	rsincero						Added @ReCreatedAfterRollback, after the DA0 probe showed the intended
											inference from auditCreatedDateUtc > StartDateUtc to be clock-tick noise.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspStartExecutionLoggingInsert
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

    IF @ProcedureName IS NULL OR LEN (LTRIM (RTRIM (@ProcedureName))) = 0
    BEGIN
        SET @msg = N'@ProcedureName is required. The instrumentation block derives it from @@PROCID, '
                 + N'so a NULL here means the block was copied without its DECLARE.';
        ;THROW 50000, @msg, 1;
    END;

    -- -------------------------------------------------------------------------------------------------
    -- Successful and the audit columns are absent on purpose; their DEFAULTs stamp them. StartDateUtc
    -- falls back to its own DEFAULT when the caller has none, which is the ad-hoc case only.
    -- -------------------------------------------------------------------------------------------------
    INSERT INTO logs.ExecutionLog
    (
          ProcedureName
        , KeyParameters
        , StartDateUtc
        , ReCreatedAfterRollback
    )
    VALUES
    (
          @ProcedureName
        , @KeyParameters
        , COALESCE (@StartDateUtc, SYSUTCDATETIME ())
        , COALESCE (@ReCreatedAfterRollback, 0)
    );

    SET @ExecutionLogId = SCOPE_IDENTITY ();

    -- -------------------------------------------------------------------------------------------------
    -- SCOPE_IDENTITY () returns NULL if an INSERT trigger were ever added here and changed scope, or if
    -- the insert somehow affected no rows. Either way the caller would carry a NULL id, its CATCH block
    -- would insert an orphan instead of completing this row, and the cause would be invisible. Fail
    -- loudly instead: nothing is at stake yet.
    -- -------------------------------------------------------------------------------------------------
    IF @ExecutionLogId IS NULL
    BEGIN
        SET @msg = N'Failed to retrieve SCOPE_IDENTITY () after inserting the execution log row for '
                 + @ProcedureName + N'. The call would have run unlogged.';
        ;THROW 50000, @msg, 1;
    END;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspStartExecutionLoggingInsert'
    , @Description = N'Writes the opening row of logs.ExecutionLog for one procedure call and returns its ExecutionLogId; the only INSERT into that table on the normal path. Separate from logs.uspStartExecutionLogging because it is the seam: the row is written inside the caller''s transaction and is therefore rolled back when the caller fails, and the only mechanisms that would make it durable -- a loopback linked server, CLR, or a queue read by another session -- replace exactly this procedure and nothing else. Omits Successful and the audit columns from its INSERT so the table defaults fire, which is what makes a call unsuccessful until proven otherwise. Deliberately does not swallow errors, unlike logs.uspRecordExecutionError: nothing has happened yet when this runs, so a failure here means the call is about to run unlogged, which is the hole AR8 exists to close.';
GO

PRINT N'351: logs.uspStartExecutionLoggingInsert created or altered.';
GO
