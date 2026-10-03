SET NOCOUNT ON;
DECLARE @LoadRunId INT, @A INT, @B INT, @Before INT, @After INT, @N INT, @Msg NVARCHAR (400);
EXEC logs.uspStartLoadRun @RunMode = N'Full', @ActivityLocation = N'ZZ',
     @AllowConcurrent = 1, @LoadRunId = @LoadRunId OUTPUT;

DECLARE @Cases TABLE (Ordinal INT IDENTITY, Label NVARCHAR (60), Elements NVARCHAR (MAX));
INSERT INTO @Cases (Label, Elements) VALUES
  (N'sequence overflows int',   N'[{"handlerId":"ZZTEST000001","sourceType":"H","sequence":99999999999}]')
, (N'sequence is not a number', N'[{"handlerId":"ZZTEST000001","sourceType":"H","sequence":"banana"}]')
, (N'handlerId wider than 12',  N'[{"handlerId":"ZZTEST0000019999","sourceType":"H","sequence":1}]')
, (N'sourceType wider than 1',  N'[{"handlerId":"ZZTEST000001","sourceType":"HHHH","sequence":1}]')
, (N'httpStatusCode overflows', N'[{"handlerId":"ZZTEST000001","sourceType":"H","sequence":1,"httpStatusCode":99999999999}]');

DECLARE @i INT = 1, @Total INT = (SELECT COUNT (*) FROM @Cases), @Label NVARCHAR (60), @E NVARCHAR (MAX);
WHILE @i <= @Total
BEGIN
    SELECT @Label = Label, @E = Elements FROM @Cases WHERE Ordinal = @i;
    SELECT @Before = COUNT (*) FROM logs.ExecutionLog
     WHERE ProcedureName = N'[logs].[uspUpsertHandlerLoadStatusSet]';

    BEGIN TRY
        EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = @LoadRunId, @Mode = N'Enumerate',
             @Elements = @E, @RowsAffected = @A OUTPUT;
        SET @N = 0; SET @Msg = N'(accepted)';
    END TRY
    BEGIN CATCH
        SET @N = ERROR_NUMBER (); SET @Msg = LEFT (ERROR_MESSAGE (), 90);
    END CATCH

    SELECT @After = COUNT (*) FROM logs.ExecutionLog
     WHERE ProcedureName = N'[logs].[uspUpsertHandlerLoadStatusSet]';

    PRINT CONCAT (@Label, N' -> err ', @N, N', log rows +', @After - @Before, N' : ', @Msg);
    SET @i += 1;
END

EXEC logs.uspCompleteLoadRun @LoadRunId = @LoadRunId, @Status = N'Succeeded';
