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
ObjectName:   logs.uspRecordExecutionError
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

The call every instrumented procedure makes from its CATCH block, just before it re-raises the original error with a bare
THROW. It records the failure in logs.ExecutionLog and, whatever happens while doing so, returns quietly.

The swallow is the whole reason this procedure exists in front of logs.uspRecordExecutionErrorUpdate. By the time it runs
there is already a real error on its way to the application -- a deadlock, a constraint violation, a timeout -- and that
error is what the operator needs. If logging the failure were itself allowed to fail, its error would replace the one
being reported: the caller's THROW would never run, and the application would be told the execution log could not be
written rather than that the handler batch was deadlocked. That is error-masking, and it hides the more important of the
two errors behind the less important one.

So this procedure is the only place in the database permitted to discard an error, and it is permitted precisely because
it has something better to protect.

========================================================================================================================
Requirements and Key Dependencies:

logs.uspRecordExecutionErrorUpdate, and through it logs.ExecutionLog.

Both application logins are granted EXECUTE on this procedure. Ownership chaining carries the write through, so neither
holds any direct permission on logs.ExecutionLog (AR3).

========================================================================================================================
Notes:

Swallowing is not the same as being silent. A discarded error means the execution log is now missing a row it should
have, and a monitoring table that quietly loses records is worse than one that admits it. So the CATCH raises the fact at
severity 10, which is informational: it does not throw, it does not abort the batch, it does not populate the caller's
error state, and SqlClient surfaces it on the InfoMessage event rather than as an exception. The calling CATCH block runs
on undisturbed and the developer reading the output still learns that the log is incomplete. The message carries no format
placeholders and interpolates nothing but a CAST of ERROR_NUMBER (), so the RAISERROR cannot fail on its own arguments.

THE ONE CASE IN WHICH AR8'S PROMISE DOES NOT HOLD, MEASURED 2026-09-05 AND NEEDING AN MDE DECISION (G37). A CATCH running
inside a transaction that SET XACT_ABORT ON has DOOMED cannot write a log row by any means. The MERGE behind this
procedure returns error 3930 -- "the current transaction cannot be committed and cannot support operations that write to
the log file. Roll back the transaction." -- and so would any other write, including a plain INSERT, a table variable
flush, or a call through a different procedure. It is not a defect in this procedure and cannot be fixed inside it.

Why the nineteen writing procedures never meet it: they ROLL BACK their own transaction in the CATCH before recording,
which un-dooms the session and lets the write through. That is the real work the rollback was doing, and it is available
only to a procedure that OWNS the transaction. The two paged reads own none -- a transaction open around a read belongs to
the caller -- so on 2026-09-05 they stopped rolling back at all, because rolling back a caller's transaction destroys work
they never did and, inside INSERT ... EXEC, raises error 8004 and loses the error anyway. See
logs.uspGetLoadRunPage's header.

So the boundary is: a read called WITHOUT an enclosing transaction records its errors, which is how the monitoring web app
and EF Core call it, and is what build/tmp/da4_getstatuspage.probesql asserts. A read called INSIDE a transaction that
aborts records nothing, and this warning -- now naming 3930 -- is the only trace. The two remedies are a caller-side rule
(do not wrap a grid read in a write transaction) and the autonomous-logging pattern MDE's own template already sketches:
the commented-out four-part [AutonomousLogging].[Brokenwood].logs.usp_* calls are a loopback linked server, whose call
runs in its own transaction and is therefore immune to the caller's doomed state. The second needs a linked server in
three environments, which is a DBA and infrastructure decision rather than a code one, so it is recorded as G37 rather
than assumed.

This is the fifth and last of the procedures exempt from AR8's own requirement. The four in this schema cannot be
instrumented, because instrumenting them would mean calling themselves, and util.uspSetObjectDescription is exempt for a
different reason: it is script 030, and logs.ExecutionLog does not exist until script 350, so at the moment it first runs
there is no table to log to.

Every argument is passed by name. Nine parameters of which six are nullable, in a procedure that runs only when
something has already gone wrong, is the worst possible place for a positional call to silently shift by one.

The caller must reassign @ExecutionId from the re-creation call BEFORE calling this. Passing the id of the row that was
rolled back produces a correct but less useful record: the MERGE finds nothing, writes an orphan row, and notes in
ContextMessage that the start row was rolled back and not re-created -- which is true, and is not the same as the row
having been re-created and completed.

========================================================================================================================
Example Usage and Performance:

-- In the CATCH block of an instrumented procedure, after the rollback and after re-creating the log row.
EXEC logs.uspRecordExecutionError
      @ProcedureName   = @ProcName
    , @KeyParameters   = @KeyParameters
    , @ExecutionLogId  = @ExecutionId
    , @ErrorMessage    = @ErrorMsg
    , @ErrorProcedure  = @ErrorProcedureName
    , @ErrorNumber     = @ErrorNumber
    , @ErrorLine       = @ErrorLine
    , @DynamicSql      = @DynamicSql
    , @ContextMessage  = @ContextMessage;

;THROW;   -- the original error, unchanged, with its own number

One nested call and one singleton write, on the failure path only.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA0, requirement AR8 [R12]. Adapted from the
											template MDE supplied on 2026-09-05, keeping its swallow-everything outer
											CATCH, with two changes: arguments are passed by name, and the CATCH raises
											an informational severity-10 message instead of doing nothing at all, so a
											lost log row is visible without interrupting the calling CATCH block.
2026-09-05	rsincero						The severity-10 warning now carries the error number and names 3930, the
											doomed-transaction case. Found by build/tmp/da4_getstatuspage.probesql: a
											CATCH running inside a transaction the caller doomed cannot write a log row
											at all, and the message gave the operator no way to know that was the reason.
											See the Notes section -- this is the one failure mode in which AR8's promise
											that every error is recorded does not hold, and it needs an MDE decision.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspRecordExecutionError
      @ProcedureName  NVARCHAR (300)
    , @KeyParameters  NVARCHAR (MAX) = NULL
    , @ExecutionLogId BIGINT         = NULL
    , @ErrorMessage   NVARCHAR (MAX) = NULL
    , @ErrorProcedure NVARCHAR (300) = NULL
    , @ErrorNumber    INT            = NULL
    , @ErrorLine      INT            = NULL
    , @DynamicSql     NVARCHAR (MAX) = NULL
    , @ContextMessage NVARCHAR (MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY

        EXEC logs.uspRecordExecutionErrorUpdate
              @ProcedureName   = @ProcedureName
            , @KeyParameters   = @KeyParameters
            , @ExecutionLogId  = @ExecutionLogId
            , @ErrorMessage    = @ErrorMessage
            , @ErrorProcedure  = @ErrorProcedure
            , @ErrorNumber     = @ErrorNumber
            , @ErrorLine       = @ErrorLine
            , @DynamicSql      = @DynamicSql
            , @ContextMessage  = @ContextMessage;

    END TRY
    BEGIN CATCH

        -- ---------------------------------------------------------------------------------------------
        -- Discard it. The caller is holding a real error and is about to re-raise it; letting this one
        -- escape would replace that error with this one and hide the failure that matters.
        --
        -- Severity 10 is informational: no throw, no abort, no change to the caller's error state, and
        -- SqlClient reports it on InfoMessage rather than as an exception. It is here so that a lost
        -- log row is not also an invisible one.
        --
        -- The number is appended, and 3930 is named, because of what a probe found on 2026-09-05. Any
        -- procedure whose CATCH runs inside a transaction that SET XACT_ABORT ON has DOOMED gets error
        -- 3930 here -- "the current transaction cannot be committed and cannot support operations that
        -- write to the log file" -- and no log row can be written at all, by any means, until that
        -- transaction is rolled back. The nineteen writing procedures never see it because they roll
        -- back their OWN transaction first, which is what makes their recording possible; a read cannot
        -- do that, because a transaction open around a read belongs to the caller. Without the number
        -- in this message the operator is told a row is missing and given nothing to act on, and the
        -- one action that fixes it -- stop wrapping the call in a transaction, or make the logging
        -- autonomous, per the four-part [AutonomousLogging] calls in MDE's own template -- is not
        -- guessable from "could not write".
        --
        -- Still no interpolated ARGUMENTS: CONCAT of a CAST INT cannot fail, so this statement cannot
        -- throw on its own inputs and become the second lost error.
        -- ---------------------------------------------------------------------------------------------
        DECLARE @warning NVARCHAR (2048) =
            N'logs.uspRecordExecutionError could not write the execution log row for this failure '
          + N'(error ' + CAST (ERROR_NUMBER () AS NVARCHAR (11)) + N'). '
          + CASE WHEN ERROR_NUMBER () = 3930
                 THEN N'The calling transaction is doomed, so NO log row can be written until it is '
                    + N'rolled back. The caller wrapped this call in a transaction that SET '
                    + N'XACT_ABORT ON then aborted; a procedure that owns its transaction rolls it '
                    + N'back before recording, and one that does not cannot. '
                 ELSE N'' END
          + N'The error it hit was discarded so that the original error still reaches the caller. '
          + N'logs.ExecutionLog is therefore missing a row for a call that failed.';

        RAISERROR (@warning, 10, 1) WITH NOWAIT;

    END CATCH;

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspRecordExecutionError'
    , @Description = N'The call every instrumented procedure makes from its CATCH block, immediately before re-raising the original error with a bare THROW. Records the failure in logs.ExecutionLog and returns quietly whatever happens. The only place in this database permitted to discard an error, and permitted because it has something better to protect: by the time it runs a real error -- a deadlock, a constraint violation -- is already on its way to the application, and letting a logging failure escape would replace that error with this one, telling the operator the log could not be written rather than that the batch was deadlocked. Not silent, though: the CATCH raises the loss at severity 10, which is informational and so does not throw, abort, or disturb the calling CATCH block, but does tell whoever is reading that logs.ExecutionLog is missing a row for a call that failed. One of the five procedures exempt from AR8''s own instrumentation requirement.';
GO

-- -------------------------------------------------------------------------------------------------
-- Grants. This procedure is granted; logs.uspRecordExecutionErrorUpdate behind it is NOT. Both
-- applications reach it from the CATCH block of every instrumented procedure, and ownership chaining
-- carries the write through to logs.ExecutionLog, which script 050 denies them directly (AR3).
--
-- Granting the wrapper rather than the inner procedure is also what enforces the swallow: an
-- application cannot reach the MERGE by a route that would let its errors escape.
-- -------------------------------------------------------------------------------------------------
IF DATABASE_PRINCIPAL_ID (N'RCRAInfoLoaderRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRecordExecutionError TO RCRAInfoLoaderRole;
END;
GO

IF DATABASE_PRINCIPAL_ID (N'RCRAInfoMonitorRole') IS NOT NULL
BEGIN
    GRANT EXECUTE ON logs.uspRecordExecutionError TO RCRAInfoMonitorRole;
END;
GO

PRINT N'354: logs.uspRecordExecutionError created or altered, EXECUTE granted to both roles.';
GO
