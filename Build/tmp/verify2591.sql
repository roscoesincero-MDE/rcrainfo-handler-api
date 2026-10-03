SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

PRINT '--- 1. logs.HandlerLoadStatus for run 2591 ---';
SELECT Status, Outcome, AttemptCount, Rows = COUNT(*)
     , NullOutcome   = SUM(CASE WHEN Outcome         IS NULL THEN 1 ELSE 0 END)
     , NullCompleted = SUM(CASE WHEN CompletedDateUtc IS NULL THEN 1 ELSE 0 END)
     , NullSourceId  = SUM(CASE WHEN HandlerSourceId IS NULL THEN 1 ELSE 0 END)
     , NullSha       = SUM(CASE WHEN PayloadSha256   IS NULL THEN 1 ELSE 0 END)
  FROM logs.HandlerLoadStatus
 WHERE LoadRunId = 2591 AND IsDeleted = 0
 GROUP BY Status, Outcome, AttemptCount;

PRINT '--- 2. the attempt row: path only, no query string ---';
SELECT a.HttpStatusCode, a.RequestPath, a.ApiErrorCode, a.DurationMs
  FROM logs.HandlerLoadAttempt AS a
  JOIN logs.HandlerLoadStatus  AS s ON s.HandlerLoadStatusId = a.HandlerLoadStatusId
 WHERE s.LoadRunId = 2591;

PRINT '--- 3. what landed in dbo.HandlerSource ---';
SELECT HandlerId, SourceType, Sequence, CurrentRecord, HandlerName
     , SrcCreatedDate = CONVERT(varchar(10), SrcCreatedDate, 23)
     , SrcUpdatedDate = CONVERT(varchar(10), SrcUpdatedDate, 23)
     , auditCreatedDateUtc
  FROM dbo.HandlerSource
 WHERE HandlerId = N'MDR000503581' AND IsDeleted = 0
 ORDER BY SourceType, Sequence;

PRINT '--- 4. visible in the default read? ---';
SELECT RowsInDefaultRead = COUNT(*) FROM dbo.vwHandlerSource WHERE HandlerId = N'MDR000503581';
SELECT RowsInHistoryRead = COUNT(*) FROM dbo.vwHandlerSourceHistory WHERE HandlerId = N'MDR000503581';

PRINT '--- 5. child rows written, by table ---';
SELECT 'HandlerOwnerOperator' AS ChildTable, Rows = COUNT(*) FROM dbo.HandlerOwnerOperator c JOIN dbo.HandlerSource h ON h.HandlerSourceId = c.HandlerSourceId WHERE h.HandlerId = N'MDR000503581'
UNION ALL SELECT 'HandlerWasteCode',    COUNT(*) FROM dbo.HandlerWasteCode    c JOIN dbo.HandlerSource h ON h.HandlerSourceId = c.HandlerSourceId WHERE h.HandlerId = N'MDR000503581'
UNION ALL SELECT 'HandlerNaicsCode',    COUNT(*) FROM dbo.HandlerNaicsCode    c JOIN dbo.HandlerSource h ON h.HandlerSourceId = c.HandlerSourceId WHERE h.HandlerId = N'MDR000503581';

PRINT '--- 6. data quality observations for this run ---';
SELECT ObservationType, Severity, Rows = COUNT(*)
  FROM logs.DataQualityObservation WHERE LoadRunId = 2591 GROUP BY ObservationType, Severity;
