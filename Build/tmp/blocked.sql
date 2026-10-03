SET NOCOUNT ON;
SET LOCK_TIMEOUT 2000;
DECLARE @B INT, @A INT, @N INT, @M NVARCHAR(200), @Label NVARCHAR(60);
DECLARE @i INT = 1;
WHILE @i <= 2
BEGIN
    SELECT @B = COUNT(*) FROM logs.ExecutionLog;
    SET @N = 0; SET @M = N'(no error)';
    BEGIN TRY
        IF @i = 1
        BEGIN
            SET @Label = N'softdelete under TABLOCKX';
            DECLARE @R INT, @C INT;
            EXEC dbo.uspSoftDeleteHandlerSourceSet
                 @LoadRunId = NULL,
                 @Elements  = N'[{"handlerId":"ZZTEST000006","sourceType":"Z","sequence":1}]',
                 @Reason    = N'lock probe',
                 @RowsAffected = @R OUTPUT, @ChildRows = @C OUTPUT;
        END
        IF @i = 2
        BEGIN
            SET @Label = N'grid read under TABLOCKX';
            EXEC dbo.uspGetHandlerSourcePage @Skip = 0, @Take = 1;
        END
    END TRY
    BEGIN CATCH SET @N = ERROR_NUMBER(); SET @M = LEFT(ERROR_MESSAGE(), 130); END CATCH
    SELECT @A = COUNT(*) FROM logs.ExecutionLog;
    PRINT CONCAT(@Label, N' -> err ', @N, N', +', @A - @B, N' : ', @M);
    SET @i += 1;
END
