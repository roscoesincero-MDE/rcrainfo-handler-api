SET NOCOUNT ON;
DECLARE @LoadRunId INT, @Rows INT;
EXEC logs.uspStartLoadRun @RunMode = N'Full', @ActivityLocation = N'ZZ',
     @AllowConcurrent = 1, @LoadRunId = @LoadRunId OUTPUT;
PRINT CONCAT (N'LoadRunId = ', @LoadRunId);

PRINT N'--- 520 with a JSON OBJECT rather than an array ---';
BEGIN TRY
    EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = @LoadRunId, @Mode = N'Enumerate',
         @Elements = N'{"handlerId":"ZZTEST000001","sourceType":"H","sequence":1}',
         @RowsAffected = @Rows OUTPUT;
    PRINT CONCAT (N'  ACCEPTED. RowsAffected = ', ISNULL (CAST (@Rows AS NVARCHAR (20)), N'NULL'));
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 160));
END CATCH

PRINT N'--- 520 with text that is not JSON at all ---';
BEGIN TRY
    EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = @LoadRunId, @Mode = N'Enumerate',
         @Elements = N'not json', @RowsAffected = @Rows OUTPUT;
    PRINT CONCAT (N'  ACCEPTED. RowsAffected = ', ISNULL (CAST (@Rows AS NVARCHAR (20)), N'NULL'));
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 160));
END CATCH

PRINT N'--- 520 with an empty array ---';
BEGIN TRY
    EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = @LoadRunId, @Mode = N'Enumerate',
         @Elements = N'[]', @RowsAffected = @Rows OUTPUT;
    PRINT CONCAT (N'  ACCEPTED. RowsAffected = ', ISNULL (CAST (@Rows AS NVARCHAR (20)), N'NULL'));
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 160));
END CATCH

PRINT N'--- 400 with a JSON OBJECT rather than an array ---';
BEGIN TRY
    EXEC dbo.uspMergeHandlerSourceBatch @Payload = N'{"retrievedDateUtc":"2026-01-01T00:00:00Z"}',
         @LoadRunId = @LoadRunId;
    PRINT N'  ACCEPTED.';
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 160));
END CATCH

EXEC logs.uspCompleteLoadRun @LoadRunId = @LoadRunId, @Status = N'Succeeded';
