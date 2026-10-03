SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @Id BIGINT = (SELECT HandlerSourceId FROM dbo.HandlerSource
                       WHERE HandlerId = N'MDR000503581' AND IsDeleted = 0);

PRINT '--- child rows written (only non-empty shown) ---';
SELECT ChildTable, Rows FROM (
    SELECT 'AdditionalContact' ChildTable, COUNT(*) Rows FROM dbo.HandlerSourceAdditionalContact      WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'Certification',        COUNT(*) FROM dbo.HandlerSourceCertification             WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'EpisodicProject',      COUNT(*) FROM dbo.HandlerSourceEpisodicProject           WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'EpisodicWaste',        COUNT(*) FROM dbo.HandlerSourceEpisodicWaste             WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'HsmActivity',          COUNT(*) FROM dbo.HandlerSourceHsmActivity               WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'LqgConsolidationVsqg', COUNT(*) FROM dbo.HandlerSourceLqgConsolidationVsqg      WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'NaicsOther',           COUNT(*) FROM dbo.HandlerSourceNaicsOther                WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'Operator',             COUNT(*) FROM dbo.HandlerSourceOperator                  WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'Owner',                COUNT(*) FROM dbo.HandlerSourceOwner                     WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'PermitOtherPermit',    COUNT(*) FROM dbo.HandlerSourcePermitOtherPermit         WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'RawJson',              COUNT(*) FROM dbo.HandlerSourceRawJson                   WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'StateDistrictCounty',  COUNT(*) FROM dbo.HandlerSourceStateDistrictCounty       WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'WasteFederalWasteCode',COUNT(*) FROM dbo.HandlerSourceWasteFederalWasteCode     WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'WasteStateActivity',   COUNT(*) FROM dbo.HandlerSourceWasteStateActivity        WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'WasteStateWasteCode',  COUNT(*) FROM dbo.HandlerSourceWasteStateWasteCode       WHERE HandlerSourceId=@Id AND IsDeleted=0
    UNION ALL SELECT 'WasteUniversalWaste',  COUNT(*) FROM dbo.HandlerSourceWasteUniversalWaste       WHERE HandlerSourceId=@Id AND IsDeleted=0
) AS c WHERE Rows > 0 ORDER BY Rows DESC, ChildTable;

PRINT '--- a spot check of mapped scalar fields ---';
SELECT SiteLocationCity, SiteLocationStateCode, SiteLocationZip
     , GenCategory = WasteFederalGeneratorCategoryCode
     , GenCategoryDesc = WasteFederalGeneratorCategoryDescription
     , ActivityLocation
     , ReceivedDate = CONVERT(varchar(10),ReceivedDate,23)
  FROM dbo.HandlerSource WHERE HandlerSourceId = @Id;

PRINT '--- observations for run 2591 ---';
SELECT ObservationType, Severity, Rows = COUNT(*)
  FROM logs.DataQualityObservation WHERE LoadRunId = 2591 GROUP BY ObservationType, Severity;

PRINT '--- run 2591 counters as recorded ---';
SELECT Status, SourceRecordsEnumerated, SourceRecordsFetched, SourceRecordsInserted
     , HttpRequestCount, HttpRetryCount, WatermarkAfterDate
  FROM logs.LoadRun WHERE LoadRunId = 2591;
