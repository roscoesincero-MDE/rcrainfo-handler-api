SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @Run INT = (SELECT MAX (LoadRunId) FROM logs.LoadRun);
DECLARE @Full NVARCHAR (MAX) = N'[{"retrievedDateUtc":"2026-09-05T10:00:00","handler":{
  "handlerId":"MDTEST00001","activityLocation":"MD","type":{"code":"N"},"sequence":1,
  "owners":[{"name":"OWNER A","type":{"code":"P"}},{"name":"OWNER B","type":{"code":"O"}}],
  "waste":{"federalWasteCodes":["D001","D002"]},
  "hsm":{"activities":[{"estimatedShortTons":10,"wasteCodes":["K001","K002"]}]}
}}]';

DECLARE @Shrunk NVARCHAR (MAX) = N'[{"retrievedDateUtc":"2026-09-05T11:00:00","handler":{
  "handlerId":"MDTEST00001","activityLocation":"MD","type":{"code":"N"},"sequence":1,
  "owners":[{"name":"OWNER A","type":{"code":"P"}}],
  "waste":{"federalWasteCodes":["D001"]},
  "hsm":{"activities":[{"estimatedShortTons":10,"wasteCodes":["K001"]}]}
}}]';

DECLARE @NoOwners NVARCHAR (MAX) = N'[{"retrievedDateUtc":"2026-09-05T12:00:00","handler":{
  "handlerId":"MDTEST00001","activityLocation":"MD","type":{"code":"N"},"sequence":1,
  "waste":{"federalWasteCodes":["D001"]},
  "hsm":{"activities":[{"estimatedShortTons":10,"wasteCodes":["K001"]}]}
}}]';

DECLARE @Snap TABLE (Phase NVARCHAR (20), Line NVARCHAR (200));

DECLARE @Phase NVARCHAR (20);

DECLARE @i INT = 1;
WHILE @i <= 4
BEGIN
    SET @Phase = CASE @i WHEN 1 THEN N'1 first load'
                         WHEN 2 THEN N'2 identical'
                         WHEN 3 THEN N'3 shrunk'
                         ELSE          N'4 owners omitted' END;

    IF @i IN (1, 2) EXEC dbo.uspMergeHandlerSourceBatch @Payload = @Full,     @LoadRunId = @Run;
    IF @i = 3       EXEC dbo.uspMergeHandlerSourceBatch @Payload = @Shrunk,   @LoadRunId = @Run;
    IF @i = 4       EXEC dbo.uspMergeHandlerSourceBatch @Payload = @NoOwners, @LoadRunId = @Run;

    INSERT INTO @Snap (Phase, Line)
    SELECT @Phase, CONCAT ('owner ', o.OrdinalPosition, '=', o.Name, ' type=', o.SourceType, ' del=', o.IsDeleted)
      FROM dbo.HandlerSourceOwner AS o
      JOIN dbo.HandlerSource AS hs ON hs.HandlerSourceId = o.HandlerSourceId
     WHERE hs.HandlerId = N'MDTEST00001'
    UNION ALL
    SELECT @Phase, CONCAT ('fedwaste ', f.OrdinalPosition, '=', f.FederalWasteCode, ' del=', f.IsDeleted)
      FROM dbo.HandlerSourceWasteFederalWasteCode AS f
      JOIN dbo.HandlerSource AS hs ON hs.HandlerSourceId = f.HandlerSourceId
     WHERE hs.HandlerId = N'MDTEST00001'
    UNION ALL
    SELECT @Phase, CONCAT ('hsm ', a.OrdinalPosition, ' tons=', a.EstimatedShortTons, ' del=', a.IsDeleted)
      FROM dbo.HandlerSourceHsmActivity AS a
      JOIN dbo.HandlerSource AS hs ON hs.HandlerSourceId = a.HandlerSourceId
     WHERE hs.HandlerId = N'MDTEST00001'
    UNION ALL
    SELECT @Phase, CONCAT ('hsmcode ', wc.OrdinalPosition, '=', wc.WasteCode, ' del=', wc.IsDeleted)
      FROM dbo.HandlerSourceHsmActivityWasteCode AS wc
      JOIN dbo.HandlerSourceHsmActivity AS a ON a.HandlerSourceHsmActivityId = wc.HandlerSourceHsmActivityId
      JOIN dbo.HandlerSource AS hs ON hs.HandlerSourceId = a.HandlerSourceId
     WHERE hs.HandlerId = N'MDTEST00001';

    SET @i += 1;
END;

SELECT CONCAT (Phase, ' | ', Line) FROM @Snap ORDER BY Phase, Line;

SELECT CONCAT ('COMMENTS  ', ROW_NUMBER () OVER (ORDER BY ExecutionLogId), ': ', Comments)
  FROM logs.ExecutionLog
 WHERE ProcedureName = N'[dbo].[uspMergeHandlerSourceBatch]'
   AND StartDateUtc >= DATEADD (MINUTE, -2, SYSUTCDATETIME ())
 ORDER BY ExecutionLogId;

SELECT CONCAT ('OBSERVATION: ', ObservationType, ' ', Severity, ' ', TableName, ' ', JsonPath, ' [', ObservedValue, ']')
  FROM logs.DataQualityObservation
 WHERE ObservedDateUtc >= DATEADD (MINUTE, -2, SYSUTCDATETIME ());

ROLLBACK TRANSACTION;
