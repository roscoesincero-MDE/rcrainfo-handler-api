SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

SELECT HandlersInTable  = COUNT (DISTINCT HandlerId)
     , VersionsInTable  = COUNT (*)
     , NeverReconciled  = SUM (CASE WHEN CurrentRecord IS NULL THEN 1 ELSE 0 END)
     , FlaggedCurrent   = SUM (CASE WHEN CurrentRecord = 1 THEN 1 ELSE 0 END)
     , FlaggedNotCurrent= SUM (CASE WHEN CurrentRecord = 0 THEN 1 ELSE 0 END)
  FROM dbo.HandlerSource WHERE IsDeleted = 0;

SELECT HandlersInDefaultRead = COUNT (DISTINCT HandlerId), RowsInDefaultRead = COUNT (*)
  FROM dbo.vwHandlerSource;

SELECT ObservationRows = COUNT (*) FROM logs.DataQualityObservation WHERE IsDeleted = 0;
