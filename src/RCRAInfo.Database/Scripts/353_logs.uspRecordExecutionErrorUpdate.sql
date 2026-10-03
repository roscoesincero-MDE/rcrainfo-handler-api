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
ObjectName:   logs.uspRecordExecutionErrorUpdate
Author:       rsincero
CreateDate:   2026-09-05
========================================================================================================================
Description:

Records the failure of one procedure call: closes out its logs.ExecutionLog row with the error, the end time and the
duration, or -- when that row is not there to close -- writes a new row that carries the error and says why the start
time is unknown.

Both outcomes are one MERGE, because "update the row if it exists, insert one if it does not" is exactly what MERGE is
for, and because the two cases have to be indivisible. This runs from a CATCH block, which is the one place in the
database where a second failure cannot be reported to anybody: logs.uspRecordExecutionError wraps this call and swallows
whatever it throws, deliberately, so that the original error still reaches the application. Anything this procedure gets
wrong is silent.

The row can be missing for two different reasons and the distinction is diagnostic, so the row this procedure writes says
which one happened:

  - @ExecutionLogId is NULL           -- logging never started. logs.uspStartExecutionLogging itself failed, or the
                                         instrumentation block was copied without its opening call.
  - @ExecutionLogId is set but gone   -- the start row was written inside the transaction that has just rolled back, and
                                         the CATCH block's re-creation call did not put it back either.

========================================================================================================================
Requirements and Key Dependencies:

logs.ExecutionLog.

Called only by logs.uspRecordExecutionError. Application logins are granted EXECUTE on that wrapper, not on this
procedure; ownership chaining supplies the UPDATE and INSERT, so neither login holds any direct permission on
logs.ExecutionLog (AR3).

========================================================================================================================
Notes:

WITH (HOLDLOCK) on the target is what makes the MERGE safe. Without it, MERGE's existence check and its insert are not
atomic, and two sessions can both find no row and both insert one. That is the documented MERGE race, and it applies here
even though each execution has its own key, because the orphan path inserts a row that no key protects.

The match key is COALESCE (@ExecutionLogId, -1), not @ExecutionLogId. Both values fail to match and both therefore fall
to WHEN NOT MATCHED BY TARGET, which is the branch this procedure wants -- but they get there differently. Comparing a
column to NULL is not seekable, so under HOLDLOCK the engine would hold locks across a scan of the whole log table, in
the error path, on the one table every session in the database inserts into. -1 is seekable and no IDENTITY (1, 1) column
ever produces it, so the lock is a range at the unused low end of the index rather than a scan across the hot end.

ElapsedMilliseconds is computed with DATEDIFF_BIG and clamped with LEAST, not with a bare DATEDIFF. DATEDIFF in
milliseconds overflows INT at about 24 days and raises error 535 -- and it would raise it here, inside the error handler,
where the wrapper would swallow it and the failure would be recorded nowhere. A call still open after 24 days is the
symptom of a hung session, which is precisely the case worth reporting rather than losing. Both functions are available
on SQL Server 2022.

The orphan row is identified by EndDateUtc IS NOT NULL AND ElapsedMilliseconds IS NULL, which no completed row and no
in-flight row can produce, and ContextMessage says which of the two ways it became one. It is NOT the same thing as a
re-created row, which carries ReCreatedAfterRollback = 1 and a duration; an orphan has no start time to measure from.
Neither is identifiable from auditCreatedDateUtc > StartDateUtc, which the DA0 probe measured to be clock-tick noise.

The orphan row gets a NEW ExecutionLogId. The original identity value went with the rolled-back row and cannot be
re-inserted into an IDENTITY column, so the old value is written into ContextMessage instead -- that is the only place the
two rows can be tied together.

Successful and the audit columns are omitted from the INSERT branch so their DEFAULTs fire; the UPDATE branch sets
auditModifiedBy and auditModifiedDateUtc explicitly, because those DEFAULTs fire on INSERT only.

@ErrorMessage, @DynamicSql and @ContextMessage are free text, and SQL Server composes @ErrorMessage itself and can quote
data inside it. Together with the AR8 rule that @KeyParameters carries identifiers and counts only, this is why no
application login is granted SELECT on logs.ExecutionLog.

========================================================================================================================
Example Usage and Performance:

EXEC logs.uspRecordExecutionErrorUpdate
      @ProcedureName   = N'[dbo].[uspMergeHandlerSourceBatch]'
    , @KeyParameters   = N'LoadRunId=418, HandlerCount=500'
    , @ExecutionLogId  = 91422
    , @ErrorMessage    = N'Transaction (Process ID 71) was deadlocked ... (line 214)'
    , @ErrorProcedure  = N'uspMergeHandlerSourceBatch'
    , @ErrorNumber     = 1205
    , @ErrorLine       = 214
    , @DynamicSql      = NULL
    , @ContextMessage  = N'Merging HandlerSourceContact.';

-- The orphan path: same call with @ExecutionLogId = NULL. Inserts rather than updates, and says so in ContextMessage.

One singleton seek and one singleton write. It runs once per FAILED call, so unlike the start procedure it is not on the
critical path of ordinary work.

========================================================================================================================
Modification History:
Date		Author          Ticket    		Description
---------- 	--------------- -----------		----------------------------------------------------------------------------
2026-09-05	rsincero						Initial version. Workstream DA0, requirement AR8 [R12]. Adapted from the
											template MDE supplied on 2026-09-05, keeping its MERGE and its orphan-insert
											branch, with four changes: the match key is COALESCE (@ExecutionLogId, -1)
											so the HOLDLOCK does not sit across a table scan, ElapsedMilliseconds is
											computed overflow-safe because a bare DATEDIFF would throw inside an error
											handler, the UPDATE branch stamps auditModifiedBy and auditModifiedDateUtc,
											and the orphan row explains in ContextMessage which of the two ways it
											became an orphan.
***********************************************************************************************************************/


CREATE OR ALTER PROCEDURE logs.uspRecordExecutionErrorUpdate
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

    DECLARE @NowUtc        DATETIME2      = SYSUTCDATETIME ()
          , @MatchId       BIGINT
          , @OrphanContext NVARCHAR (MAX);

    -- -------------------------------------------------------------------------------------------------
    -- See Notes: -1 rather than NULL so the HOLDLOCK below is a seek at the unused low end of the
    -- index instead of a scan across the end every other session is inserting into.
    -- -------------------------------------------------------------------------------------------------
    SET @MatchId = COALESCE (@ExecutionLogId, -1);

    -- -------------------------------------------------------------------------------------------------
    -- The orphan row has to explain itself, because nothing else will: the caller's CATCH block is
    -- about to re-raise the original error and this row is the only record that logging failed too.
    -- -------------------------------------------------------------------------------------------------
    SET @OrphanContext =
        N'Orphan error row. '
      + CASE
            WHEN @ExecutionLogId IS NULL
                THEN N'No ExecutionLogId was supplied, so execution logging never started for this call.'
            ELSE N'ExecutionLogId ' + CAST (@ExecutionLogId AS NVARCHAR (20))
               + N' was supplied but no such row exists: the start row was rolled back and was not re-created.'
        END
      + N' StartDateUtc on this row is the time the row was written, not the time the call began, so '
      + N'ElapsedMilliseconds is unknown.';

    IF @ContextMessage IS NOT NULL
    BEGIN
        SET @OrphanContext = @OrphanContext + NCHAR (13) + NCHAR (10) + @ContextMessage;
    END;

    -- -------------------------------------------------------------------------------------------------
    -- One statement, two outcomes. HOLDLOCK is required: it is what stops two sessions from both
    -- finding no row and both inserting one.
    -- -------------------------------------------------------------------------------------------------
    MERGE logs.ExecutionLog WITH (HOLDLOCK) AS tgt
    USING (SELECT @MatchId AS ExecutionLogId) AS src
       ON tgt.ExecutionLogId = src.ExecutionLogId

    WHEN MATCHED THEN
        UPDATE
           SET EndDateUtc           = @NowUtc
             , ElapsedMilliseconds  = CAST (LEAST (DATEDIFF_BIG (MILLISECOND, tgt.StartDateUtc, @NowUtc)
                                                 , CAST (2147483647 AS BIGINT)) AS INT)
             , Successful           = 0
             , ErrorMessage         = @ErrorMessage
             , ErrorProcedure       = @ErrorProcedure
             , ErrorNumber          = @ErrorNumber
             , ErrorLine            = @ErrorLine
             , DynamicSql           = @DynamicSql
             , ContextMessage       = @ContextMessage
               -- Keep what the start row recorded; fill it only if it recorded nothing.
             , KeyParameters        = COALESCE (tgt.KeyParameters, @KeyParameters)
               -- These two DEFAULTs fire on INSERT only, so an UPDATE that omits them makes the audit
               -- trail claim the row has never changed.
             , auditModifiedBy      = ORIGINAL_LOGIN ()
             , auditModifiedDateUtc = @NowUtc

    WHEN NOT MATCHED BY TARGET THEN
        INSERT
        (
              ProcedureName
            , KeyParameters
            , StartDateUtc
            , EndDateUtc
            , ElapsedMilliseconds
            , ErrorMessage
            , ErrorProcedure
            , ErrorNumber
            , ErrorLine
            , DynamicSql
            , ContextMessage
        )
        VALUES
        (
              COALESCE (@ProcedureName, N'(unknown procedure)')
            , @KeyParameters
            , @NowUtc
            , @NowUtc
            , NULL              -- unknown, and the signature that identifies an orphan row
            , @ErrorMessage
            , @ErrorProcedure
            , @ErrorNumber
            , @ErrorLine
            , @DynamicSql
            , @OrphanContext
        );

    RETURN 0;
END;
GO

EXEC util.uspSetObjectDescription
      @SchemaName  = N'logs'
    , @ObjectType  = N'PROCEDURE'
    , @ObjectName  = N'uspRecordExecutionErrorUpdate'
    , @Description = N'Records the failure of one procedure call: closes out its logs.ExecutionLog row with the error, end time and duration, or writes a new row carrying the error when that row is not there to close. One MERGE WITH (HOLDLOCK), because the two cases must be indivisible and because this runs from a CATCH block whose wrapper swallows everything it throws -- anything wrong here is silent. Matches on COALESCE (@ExecutionLogId, -1) rather than @ExecutionLogId so the HOLDLOCK is a seek at the unused low end of the index instead of a scan across the end every other session inserts into. Computes ElapsedMilliseconds with DATEDIFF_BIG clamped by LEAST, because a bare DATEDIFF in milliseconds overflows INT after 24 days and would throw inside the error handler. An orphan row is identified by EndDateUtc IS NOT NULL AND ElapsedMilliseconds IS NULL, and its ContextMessage says which of the two ways it became an orphan -- no ExecutionLogId supplied, or one supplied whose row was rolled back and not re-created.';
GO

PRINT N'353: logs.uspRecordExecutionErrorUpdate created or altered.';
GO
