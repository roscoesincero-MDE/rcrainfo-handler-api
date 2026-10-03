SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

PRINT '--- 1. run 2592 status rows, grouped, with NULL counts ---';
SELECT Status, Outcome, Rows = COUNT(*)
     , NullOutcome    = SUM(CASE WHEN Outcome            IS NULL THEN 1 ELSE 0 END)
     , NullCompleted  = SUM(CASE WHEN CompletedDateUtc   IS NULL THEN 1 ELSE 0 END)
     , NullSourceId   = SUM(CASE WHEN HandlerSourceId    IS NULL THEN 1 ELSE 0 END)
     , NullSha        = SUM(CASE WHEN PayloadSha256      IS NULL THEN 1 ELSE 0 END)
  FROM logs.HandlerLoadStatus WHERE LoadRunId = 2592
 GROUP BY Status, Outcome ORDER BY Status, Outcome;

PRINT '--- 2. the version list run 2592 touched ---';
SELECT SourceType, Sequence, Status, Outcome, AttemptCount
  FROM logs.HandlerLoadStatus WHERE LoadRunId = 2592
 ORDER BY SourceType, Sequence;

PRINT '--- 3. attempt rows: path only, no query string ---';
SELECT a.HttpStatusCode, a.RequestPath, a.ApiErrorCode, a.DurationMs
  FROM logs.HandlerLoadAttempt a
  JOIN logs.HandlerLoadStatus s ON s.HandlerLoadStatusId = a.HandlerLoadStatusId
 WHERE s.LoadRunId = 2592
 ORDER BY a.RequestPath;

PRINT '--- 4. every stored version of MDR000503581 ---';
SELECT SourceType, Sequence, CurrentRecord, HandlerName
     , SrcCreated = CONVERT(varchar(10), SrcCreatedDate, 23)
     , SrcUpdated = CONVERT(varchar(10), SrcUpdatedDate, 23)
     , Received   = CONVERT(varchar(10), ReceivedDate, 23)
     , CreatedRun = auditCreatedDateUtc
     , ModifiedRun = auditModifiedDateUtc
  FROM dbo.HandlerSource
 WHERE HandlerId = N'MDR000503581' AND IsDeleted = 0
 ORDER BY SourceType, Sequence;

PRINT '--- 5. default read vs history read for this handler ---';
SELECT RowsInDefaultRead = (SELECT COUNT(*) FROM dbo.vwHandlerSource        WHERE HandlerId = N'MDR000503581')
     , RowsInHistoryRead = (SELECT COUNT(*) FROM dbo.vwHandlerSourceHistory WHERE HandlerId = N'MDR000503581');

PRINT '--- 6. child rows per version (only non-empty shown) ---';
SELECT v.SourceType, v.Sequence, c.ChildTable, c.Rows
  FROM dbo.HandlerSource v
 CROSS APPLY (
    SELECT 'Certification' ChildTable, COUNT(*) Rows FROM dbo.HandlerSourceCertification        WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'Operator',            COUNT(*) FROM dbo.HandlerSourceOperator             WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'Owner',               COUNT(*) FROM dbo.HandlerSourceOwner                WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'RawJson',             COUNT(*) FROM dbo.HandlerSourceRawJson              WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'WasteStateWasteCode', COUNT(*) FROM dbo.HandlerSourceWasteStateWasteCode  WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'WasteFederalWasteCode',COUNT(*) FROM dbo.HandlerSourceWasteFederalWasteCode WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'WasteStateActivity',  COUNT(*) FROM dbo.HandlerSourceWasteStateActivity   WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'AdditionalContact',   COUNT(*) FROM dbo.HandlerSourceAdditionalContact    WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'StateDistrictCounty', COUNT(*) FROM dbo.HandlerSourceStateDistrictCounty  WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'NaicsOther',          COUNT(*) FROM dbo.HandlerSourceNaicsOther           WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'HsmActivity',         COUNT(*) FROM dbo.HandlerSourceHsmActivity          WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'PermitOtherPermit',   COUNT(*) FROM dbo.HandlerSourcePermitOtherPermit    WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'WasteUniversalWaste', COUNT(*) FROM dbo.HandlerSourceWasteUniversalWaste  WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
    UNION ALL SELECT 'LqgConsolidationVsqg',COUNT(*) FROM dbo.HandlerSourceLqgConsolidationVsqg WHERE HandlerSourceId=v.HandlerSourceId AND IsDeleted=0
 ) c
 WHERE v.HandlerId = N'MDR000503581' AND v.IsDeleted = 0 AND c.Rows > 0
 ORDER BY v.SourceType, v.Sequence, c.ChildTable;

PRINT '--- 7. observations for run 2592 ---';
SELECT ObservationType, Severity, Rows = COUNT(*)
  FROM logs.DataQualityObservation WHERE LoadRunId = 2592 GROUP BY ObservationType, Severity;

PRINT '--- 8. run 2592 counters as recorded ---';
SELECT Status, RunMode, SourceRecordsEnumerated, SourceRecordsFetched, SourceRecordsInserted
     , SourceRecordsUpdated, SourceRecordsUnchanged, SourceRecordsFailed
     , HttpRequestCount, HttpRetryCount, WatermarkAfterDate
  FROM logs.LoadRun WHERE LoadRunId = 2592;

PRINT '--- 9. did run 2592 move auditModifiedDateUtc on the version run 2591 loaded? ---';
SELECT SourceType, Sequence
     , SameStamp = CASE WHEN auditCreatedDateUtc = auditModifiedDateUtc THEN 'yes -- never updated'
                        ELSE 'no -- modified after insert' END
     , auditCreatedDateUtc, auditModifiedDateUtc
  FROM dbo.HandlerSource
 WHERE HandlerId = N'MDR000503581' AND IsDeleted = 0
 ORDER BY SourceType, Sequence;
