SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

SELECT Lineages = COUNT(*),
       WithNoCurrent = SUM(CASE WHEN Currents = 0 THEN 1 ELSE 0 END),
       WithOneCurrent = SUM(CASE WHEN Currents = 1 THEN 1 ELSE 0 END),
       WithManyCurrent = SUM(CASE WHEN Currents > 1 THEN 1 ELSE 0 END)
  FROM (
        SELECT HandlerId, SourceType, Currents = SUM(CASE WHEN CurrentRecord = 1 THEN 1 ELSE 0 END)
          FROM dbo.HandlerSource WHERE IsDeleted = 0
         GROUP BY HandlerId, SourceType
       ) AS lineage;

SELECT GridRowsFor501742 = COUNT(*) FROM dbo.vwHandlerSource WHERE HandlerId = N'MDR000501742';

SELECT NullCurrent = SUM(CASE WHEN CurrentRecord IS NULL THEN 1 ELSE 0 END), Total = COUNT(*)
  FROM dbo.HandlerSource WHERE IsDeleted = 0;
