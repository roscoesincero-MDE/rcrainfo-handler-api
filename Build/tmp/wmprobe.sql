SET NOCOUNT ON;
SELECT FeedName, ActivityLocation, WatermarkDate, IsEnabled, IsDeleted FROM config.LoadWatermark;
DECLARE @LoadRunId INT;
EXEC logs.uspStartLoadRun @RunMode=N'Full', @ActivityLocation=N'ZZ', @AllowConcurrent=1, @LoadRunId=@LoadRunId OUTPUT;
PRINT N'--- set watermark for an unknown feed under ZZ ---';
BEGIN TRY
    EXEC config.uspSetLoadWatermark @FeedName=N'ZZTestFeed', @ActivityLocation=N'ZZ',
         @WatermarkDate='2026-01-01', @LoadRunId=@LoadRunId;
    PRINT N'  ACCEPTED';
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 200));
END CATCH
PRINT N'--- set the SAME date again (idempotent?) ---';
BEGIN TRY
    EXEC config.uspSetLoadWatermark @FeedName=N'ZZTestFeed', @ActivityLocation=N'ZZ',
         @WatermarkDate='2026-01-01', @LoadRunId=@LoadRunId;
    PRINT N'  ACCEPTED';
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 200));
END CATCH
EXEC logs.uspCompleteLoadRun @LoadRunId=@LoadRunId, @Status=N'Succeeded';
SELECT FeedName, ActivityLocation, WatermarkDate FROM config.LoadWatermark WHERE FeedName = N'ZZTestFeed';
