CREATE OR ALTER PROCEDURE dbo.uspGetHandlerSourceDetail
    @HandlerSourceId INT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        SELECT @HandlerSourceId AS HandlerSourceId;
    END TRY
    BEGIN CATCH
        -- Deliberately swallows, for one test run. Restored by re-running script 505.
    END CATCH
END;
GO
PRINT N'detail stubbed';
