SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @RunId INT, @W INT, @O INT, @V INT;

EXEC logs.uspStartLoadRun @RunMode = N'Incremental', @ActivityLocation = N'MD'
                        , @AllowConcurrent = 1, @LoadRunId = @RunId OUTPUT;
PRINT CONCAT (N'run=', @RunId);

EXEC logs.uspUpsertHandlerLoadStatusSet @LoadRunId = @RunId, @Mode = N'Enumerate'
   , @Elements = N'[{"handlerId":"MD0000123456","activityLocation":"MD","sourceType":"N","sequence":1}
                   ,{"handlerId":"MD0000987654","activityLocation":"MD","sourceType":"N","sequence":1}]';

PRINT N'--- 1. two attempts, throttled then succeeded';
DECLARE @A NVARCHAR (MAX) = N'
[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":1
 ,"startedDateUtc":"2026-09-06T02:00:01.120","completedDateUtc":"2026-09-06T02:00:01.480","durationMs":360
 ,"outcome":"Throttled","httpStatusCode":429,"requestPath":"/api/v1/hd/sources/MD0000123456/N/1"
 ,"retryAfterSeconds":30,"responseBytes":214,"apiErrorCode":"E_RateLimitExceeded"
 ,"apiErrorId":"7f1c9d2e-0b44-4a1e-9c8d-2a5b6c7d8e90"}
,{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":2
 ,"startedDateUtc":"2026-09-06T02:00:31.500","completedDateUtc":"2026-09-06T02:00:33.900"
 ,"outcome":"Succeeded","httpStatusCode":200,"requestPath":"/api/v1/hd/sources/MD0000123456/N/1"
 ,"responseBytes":48213}]';
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId, @Elements = @A
   , @RowsAffected = @W OUTPUT, @RowsOrphaned = @O OUTPUT, @ValuesWithheld = @V OUTPUT;
PRINT CONCAT (N'written=', @W, N' orphaned=', @O, N' withheld=', @V, N'  (expect 2/0/0)');

PRINT N'--- 2. the same flush again: idempotent';
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId, @Elements = @A
   , @RowsAffected = @W OUTPUT, @RowsOrphaned = @O OUTPUT, @ValuesWithheld = @V OUTPUT;
PRINT CONCAT (N'written=', @W, N' orphaned=', @O, N' withheld=', @V, N'  (expect 0/0/0)');

PRINT N'--- 3. derived durationMs on attempt 2 (2400 from the two stamps)';
SELECT AttemptNumber, Outcome, HttpStatusCode, DurationMs, RetryAfterSeconds, ResponseBytes
     , RequestPath, ApiErrorCode
  FROM logs.HandlerLoadAttempt
 WHERE LoadRunId = @RunId
 ORDER BY AttemptNumber;

PRINT N'--- 4. a query string in requestPath: written, path withheld';
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId
   , @Elements = N'[{"handlerId":"MD0000987654","sourceType":"N","sequence":1,"attemptNumber":1
                    ,"startedDateUtc":"2026-09-06T02:05:00","outcome":"Failed","httpStatusCode":400
                    ,"requestPath":"/api/v1/hd/other-ids?handlerId=MD0000987654"}]'
   , @RowsAffected = @W OUTPUT, @RowsOrphaned = @O OUTPUT, @ValuesWithheld = @V OUTPUT;
PRINT CONCAT (N'written=', @W, N' orphaned=', @O, N' withheld=', @V, N'  (expect 1/0/1)');

PRINT N'--- 5. the auth path echoed in apiErrorMessage, and a full URL with userinfo';
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId
   , @Elements = N'[{"handlerId":"MD0000987654","sourceType":"N","sequence":1,"attemptNumber":2
                    ,"startedDateUtc":"2026-09-06T02:06:00","outcome":"Failed","httpStatusCode":502
                    ,"requestPath":"https://MDEDEV01:SECRETKEY@rcrainfopreprod.epa.gov/api/v1/auth/x/y"
                    ,"apiErrorCode":"E_GatewayError"
                    ,"apiErrorMessage":"No route for GET /api/v1/auth/MDEDEV01/SECRETKEYVALUE"}]'
   , @RowsAffected = @W OUTPUT, @RowsOrphaned = @O OUTPUT, @ValuesWithheld = @V OUTPUT;
PRINT CONCAT (N'written=', @W, N' orphaned=', @O, N' withheld=', @V, N'  (expect 1/0/2)');

SELECT AttemptNumber, RequestPath, ApiErrorMessage
  FROM logs.HandlerLoadAttempt
 WHERE LoadRunId = @RunId AND AttemptNumber IN (1, 2)
   AND HandlerLoadStatusId IN (SELECT HandlerLoadStatusId FROM logs.HandlerLoadStatus
                                WHERE LoadRunId = @RunId AND HandlerId = N'MD0000987654')
 ORDER BY AttemptNumber;

PRINT N'--- 6. an orphan: a record this run never enumerated';
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId
   , @Elements = N'[{"handlerId":"MD0000000001","sourceType":"N","sequence":1,"attemptNumber":1
                    ,"startedDateUtc":"2026-09-06T02:07:00","outcome":"Succeeded","httpStatusCode":200
                    ,"requestPath":"/api/v1/hd/sources/MD0000000001/N/1"}]'
   , @RowsAffected = @W OUTPUT, @RowsOrphaned = @O OUTPUT, @ValuesWithheld = @V OUTPUT;
PRINT CONCAT (N'written=', @W, N' orphaned=', @O, N' withheld=', @V, N'  (expect 0/1/0)');

PRINT N'--- 7. an empty array is a no-op, not an error';
EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId, @Elements = N'[]'
   , @RowsAffected = @W OUTPUT, @RowsOrphaned = @O OUTPUT, @ValuesWithheld = @V OUTPUT;
PRINT CONCAT (N'written=', @W, N' orphaned=', @O, N' withheld=', @V, N'  (expect 0/0/0)');

PRINT N'--- 8. refusals, each caught so the probe continues';
DECLARE @Bad TABLE (Ordinal INT IDENTITY (1, 1), Label NVARCHAR (60), Payload NVARCHAR (MAX));
INSERT INTO @Bad (Label, Payload) VALUES
  (N'outcome outside the domain',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Retrying","httpStatusCode":500}]')
, (N'no startedDateUtc',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"outcome":"Succeeded","httpStatusCode":200}]')
, (N'Succeeded carrying an error',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Succeeded","httpStatusCode":200
      ,"apiErrorCode":"E_Whatever"}]')
, (N'Failed with no detail at all',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Failed"}]')
, (N'completed before it started',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","completedDateUtc":"2026-09-06T02:07:00"
      ,"outcome":"Succeeded","httpStatusCode":200}]')
, (N'negative durationMs',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","durationMs":-5,"outcome":"Succeeded"
      ,"httpStatusCode":200}]')
, (N'httpStatusCode 0',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Failed","httpStatusCode":0}]')
, (N'duplicate grain inside the set',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Succeeded","httpStatusCode":200}
    ,{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:01","outcome":"Succeeded","httpStatusCode":200}]')
, (N'attemptNumber 0',
   N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":0
      ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Succeeded","httpStatusCode":200}]')
, (N'handlerId wider than 12',
   N'[{"handlerId":"MD0000123456789","sourceType":"N","sequence":1,"attemptNumber":9
      ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Succeeded","httpStatusCode":200}]')
, (N'a bare object, not an array',
   N'{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
     ,"startedDateUtc":"2026-09-06T02:08:00","outcome":"Succeeded","httpStatusCode":200}');

DECLARE @i INT = 1, @n INT = (SELECT COUNT (*) FROM @Bad), @Label NVARCHAR (60), @P NVARCHAR (MAX);

WHILE @i <= @n
BEGIN
    SELECT @Label = Label, @P = Payload FROM @Bad WHERE Ordinal = @i;

    BEGIN TRY
        EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId, @Elements = @P;
        PRINT CONCAT (N'  NOT REFUSED (defect): ', @Label);
    END TRY
    BEGIN CATCH
        PRINT CONCAT (N'  refused [', ERROR_NUMBER (), N'] ', @Label, N': '
                    , LEFT (ERROR_MESSAGE (), 90));
    END CATCH;

    SET @i = @i + 1;
END;

PRINT N'--- 9. a closed run refuses further attempts';
EXEC logs.uspCompleteLoadRun @LoadRunId = @RunId, @Status = N'Succeeded';
BEGIN TRY
    EXEC logs.uspRecordHandlerLoadAttemptSet @LoadRunId = @RunId
       , @Elements = N'[{"handlerId":"MD0000123456","sourceType":"N","sequence":1,"attemptNumber":9
                        ,"startedDateUtc":"2026-09-06T02:09:00","outcome":"Succeeded"
                        ,"httpStatusCode":200}]';
    PRINT N'  NOT REFUSED (defect): a closed run accepted an attempt';
END TRY
BEGIN CATCH
    PRINT CONCAT (N'  refused [', ERROR_NUMBER (), N'] closed run: ', LEFT (ERROR_MESSAGE (), 80));
END CATCH;

PRINT N'--- 10. what the ExecutionLog recorded for this procedure';
SELECT TOP (6) Successful, KeyParameters, LEFT (COALESCE (Comments, ErrorMessage), 110) AS Detail
  FROM logs.ExecutionLog
 WHERE ProcedureName = N'[logs].[uspRecordHandlerLoadAttemptSet]'
 ORDER BY ExecutionLogId DESC;

PRINT N'--- 11. total rows this run left on the attempt table';
SELECT COUNT (*) AS AttemptRows FROM logs.HandlerLoadAttempt WHERE LoadRunId = @RunId;
