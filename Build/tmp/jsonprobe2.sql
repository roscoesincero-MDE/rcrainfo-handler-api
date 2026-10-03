SET NOCOUNT ON;
DECLARE @LoadRunId INT, @A INT, @B INT, @C INT;
EXEC logs.uspStartLoadRun @RunMode = N'Full', @ActivityLocation = N'ZZ',
     @AllowConcurrent = 1, @LoadRunId = @LoadRunId OUTPUT;

DECLARE @Object NVARCHAR (MAX) = N'{"handlerId":"ZZTEST000001","sourceType":"H","sequence":1,"currentRecord":true,"code":"X","description":"x"}';

PRINT N'--- 521 with a JSON OBJECT ---';
BEGIN TRY
    EXEC dbo.uspReconcileCurrentRecord @LoadRunId = @LoadRunId, @Summaries = @Object,
         @RowsAffected = @A OUTPUT, @Observations = @B OUTPUT;
    PRINT CONCAT (N'  ACCEPTED. RowsAffected = ', ISNULL (CAST (@A AS NVARCHAR (20)), N'NULL'),
                  N', Observations = ', ISNULL (CAST (@B AS NVARCHAR (20)), N'NULL'));
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 200));
END CATCH

PRINT N'--- 522 with a JSON OBJECT ---';
BEGIN TRY
    EXEC dbo.uspSoftDeleteHandlerSourceSet @LoadRunId = @LoadRunId, @Elements = @Object,
         @Reason = N'DA5 malformed-JSON probe', @RowsAffected = @A OUTPUT, @ChildRows = @B OUTPUT;
    PRINT CONCAT (N'  ACCEPTED. RowsAffected = ', ISNULL (CAST (@A AS NVARCHAR (20)), N'NULL'),
                  N', ChildRows = ', ISNULL (CAST (@B AS NVARCHAR (20)), N'NULL'));
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 200));
END CATCH

PRINT N'--- 523 with a JSON OBJECT (ContactType, Upsert) ---';
BEGIN TRY
    EXEC dbo.uspRefreshLookupSet @LoadRunId = @LoadRunId, @LookupName = N'ContactType',
         @Mode = N'Upsert', @Elements = @Object,
         @RowsAffected = @A OUTPUT, @RetiredRows = @B OUTPUT, @ChildRows = @C OUTPUT;
    PRINT CONCAT (N'  ACCEPTED. RowsAffected = ', ISNULL (CAST (@A AS NVARCHAR (20)), N'NULL'));
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  REFUSED ', ERROR_NUMBER (), N': ', LEFT (ERROR_MESSAGE (), 200));
END CATCH

EXEC logs.uspCompleteLoadRun @LoadRunId = @LoadRunId, @Status = N'Succeeded';
