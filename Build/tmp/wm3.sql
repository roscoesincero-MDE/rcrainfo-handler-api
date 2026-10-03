SET NOCOUNT ON;
DECLARE @B INT, @A INT;
IF NOT EXISTS (SELECT 1 FROM config.LoadWatermark WHERE FeedName = N'ZZTestFeed' AND ActivityLocation = N'ZZ')
    INSERT config.LoadWatermark (FeedName, ActivityLocation) VALUES (N'ZZTestFeed', N'ZZ');
PRINT N'seeded';
SELECT @B = COUNT(*) FROM logs.ExecutionLog;
BEGIN TRY
  EXEC config.uspSetLoadWatermark @FeedName=N'ZZTestFeed', @ActivityLocation=N'ZZ', @WatermarkDate='2026-01-02';
  PRINT N'set succeeded';
END TRY
BEGIN CATCH PRINT CONCAT(N'set failed ', ERROR_NUMBER(), N': ', LEFT(ERROR_MESSAGE(),300)); END CATCH
SELECT @A = COUNT(*) FROM logs.ExecutionLog;
PRINT CONCAT(N'set delta +', @A-@B);
SELECT @B = COUNT(*) FROM logs.ExecutionLog;
BEGIN TRY
  EXEC config.uspGetLoadWatermark @FeedName=N'ZZTestFeed', @ActivityLocation=N'ZZ';
  PRINT N'get succeeded';
END TRY
BEGIN CATCH PRINT CONCAT(N'get failed ', ERROR_NUMBER(), N': ', LEFT(ERROR_MESSAGE(),300)); END CATCH
SELECT @A = COUNT(*) FROM logs.ExecutionLog;
PRINT CONCAT(N'get delta +', @A-@B);
