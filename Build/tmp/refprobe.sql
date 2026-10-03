SET NOCOUNT ON;
DECLARE @B INT, @A INT, @N INT, @M NVARCHAR(200);
DECLARE @Label NVARCHAR(80);

DECLARE @i INT = 1;
WHILE @i <= 8
BEGIN
    SELECT @B = COUNT(*) FROM logs.ExecutionLog;
    SET @N = 0; SET @M = N'(accepted)';
    BEGIN TRY
        IF @i = 1 BEGIN SET @Label=N'detail: negative id';        EXEC dbo.uspGetHandlerSourceDetail @HandlerSourceId = -1; END
        IF @i = 2 BEGIN SET @Label=N'detail: null id';            EXEC dbo.uspGetHandlerSourceDetail @HandlerSourceId = NULL; END
        IF @i = 3 BEGIN SET @Label=N'summary: negative id';       EXEC logs.uspGetLoadRunSummary @LoadRunId = -1; END
        IF @i = 4 BEGIN SET @Label=N'summary: null id';           EXEC logs.uspGetLoadRunSummary @LoadRunId = NULL; END
        IF @i = 5 BEGIN SET @Label=N'search: empty term';         EXEC dbo.uspSearchHandlerSource @SearchTerm = N'', @Skip=0, @Take=1; END
        IF @i = 6 BEGIN SET @Label=N'search: negative take';      EXEC dbo.uspSearchHandlerSource @SearchTerm = N'ZZTEST', @Skip=0, @Take=-1; END
        IF @i = 7 BEGIN SET @Label=N'grid: negative take';        EXEC dbo.uspGetHandlerSourcePage @Skip=0, @Take=-1; END
        IF @i = 8 BEGIN SET @Label=N'history: null handler';      EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId = NULL, @Skip=0, @Take=1; END
    END TRY
    BEGIN CATCH
        SET @N = ERROR_NUMBER(); SET @M = LEFT(ERROR_MESSAGE(), 110);
    END CATCH
    SELECT @A = COUNT(*) FROM logs.ExecutionLog;
    PRINT CONCAT(@Label, N' -> err ', @N, N', +', @A - @B, N' : ', @M);
    SET @i += 1;
END
