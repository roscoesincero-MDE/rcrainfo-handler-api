SET NOCOUNT ON;
DECLARE @Before INT, @After INT;
SELECT @Before = COUNT (*) FROM logs.ExecutionLog;
DECLARE @x INT;
EXEC dbo.uspGetHandlerSourcePage @Skip = 0, @Take = 1;
SELECT @After = COUNT (*) FROM logs.ExecutionLog;
PRINT CONCAT (N'read on success: +', @After - @Before);

SELECT @Before = COUNT (*) FROM logs.ExecutionLog;
BEGIN TRY
    EXEC dbo.uspGetHandlerSourcePage @Skip = 0, @Take = 1, @SortBy = N'Nonsense';
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  refused ', ERROR_NUMBER ());
END CATCH
SELECT @After = COUNT (*) FROM logs.ExecutionLog;
PRINT CONCAT (N'read on failure: +', @After - @Before);
