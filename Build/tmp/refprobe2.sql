SET NOCOUNT ON;
DECLARE @B INT, @A INT, @N INT, @M NVARCHAR(160), @Label NVARCHAR(80);
DECLARE @i INT = 1;
WHILE @i <= 6
BEGIN
    SELECT @B = COUNT(*) FROM logs.ExecutionLog;
    SET @N = 0; SET @M = N'(accepted)';
    BEGIN TRY
        IF @i=1 BEGIN SET @Label=N'statuspage: bad sortby'; EXEC logs.uspGetHandlerLoadStatusPage @SortBy=N'Nonsense', @Skip=0, @Take=1; END
        IF @i=2 BEGIN SET @Label=N'grid: bad sortby';       EXEC dbo.uspGetHandlerSourcePage @SortBy=N'Nonsense', @Skip=0, @Take=1; END
        IF @i=3 BEGIN SET @Label=N'runpage: bad sortby';    EXEC logs.uspGetLoadRunPage @SortBy=N'Nonsense', @Skip=0, @Take=1; END
        IF @i=4 BEGIN SET @Label=N'watermark get: unknown'; EXEC config.uspGetLoadWatermark @FeedName=N'ZZNoSuchFeed', @ActivityLocation=N'ZZ'; END
        IF @i=5 BEGIN SET @Label=N'history: bad sortby';    EXEC dbo.uspGetHandlerSourceHistoryPage @HandlerId=N'ZZTEST000001', @SortBy=N'Nonsense', @Skip=0, @Take=1; END
        IF @i=6 BEGIN SET @Label=N'statuspage: ok';         EXEC logs.uspGetHandlerLoadStatusPage @Skip=0, @Take=1; END
    END TRY
    BEGIN CATCH SET @N=ERROR_NUMBER(); SET @M=LEFT(ERROR_MESSAGE(),100); END CATCH
    SELECT @A = COUNT(*) FROM logs.ExecutionLog;
    PRINT CONCAT(@Label, N' -> err ', @N, N', +', @A-@B, N' : ', @M);
    SET @i += 1;
END
PRINT N'--- watermark seed ---';
IF NOT EXISTS (SELECT 1 FROM config.LoadWatermark WHERE FeedName = N'ZZTestFeed' AND ActivityLocation = N'ZZ')
    INSERT config.LoadWatermark (FeedName, ActivityLocation) VALUES (N'ZZTestFeed', N'ZZ');
SELECT @B = COUNT(*) FROM logs.ExecutionLog;
EXEC config.uspSetLoadWatermark @FeedName=N'ZZTestFeed', @ActivityLocation=N'ZZ', @WatermarkDate='2026-01-02', @LoadRunId=NULL;
SELECT @A = COUNT(*) FROM logs.ExecutionLog;
PRINT CONCAT(N'watermark set ok -> +', @A-@B);
SELECT @B = COUNT(*) FROM logs.ExecutionLog;
EXEC config.uspGetLoadWatermark @FeedName=N'ZZTestFeed', @ActivityLocation=N'ZZ';
SELECT @A = COUNT(*) FROM logs.ExecutionLog;
PRINT CONCAT(N'watermark get ok -> +', @A-@B);
