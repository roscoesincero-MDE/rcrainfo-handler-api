SET XACT_ABORT ON;
CREATE TYPE dbo.ZZProbeTableType AS TABLE (HandlerId NVARCHAR (12) NOT NULL);
GO
CREATE OR ALTER PROCEDURE dbo.uspZZSwallowProbe
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        SELECT 1 AS Probe;
    END TRY
    BEGIN CATCH
        -- Deliberately swallows. Temporary; dropped by build/tmp/unbreak_probe.sql.
    END CATCH
END;
GO
PRINT N'probe objects created';
