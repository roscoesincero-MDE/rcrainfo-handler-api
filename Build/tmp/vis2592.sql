SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
PRINT '--- visibility, split by real MD handler vs EPA ZZTEST synthetic ---';
SELECT Kind = CASE WHEN HandlerId LIKE N'ZZTEST%' THEN 'ZZTEST synthetic' ELSE 'real MD handler' END
     , HandlersStored = COUNT(DISTINCT HandlerId)
     , VersionsStored = COUNT(*)
  FROM dbo.HandlerSource WHERE IsDeleted = 0
 GROUP BY CASE WHEN HandlerId LIKE N'ZZTEST%' THEN 'ZZTEST synthetic' ELSE 'real MD handler' END;
PRINT '--- and in the default read ---';
SELECT Kind = CASE WHEN HandlerId LIKE N'ZZTEST%' THEN 'ZZTEST synthetic' ELSE 'real MD handler' END
     , HandlersVisible = COUNT(DISTINCT HandlerId), RowsVisible = COUNT(*)
  FROM dbo.vwHandlerSource
 GROUP BY CASE WHEN HandlerId LIKE N'ZZTEST%' THEN 'ZZTEST synthetic' ELSE 'real MD handler' END;
PRINT '--- every real MD handler: stored versions vs rows in the default read ---';
SELECT h.HandlerId, Versions = COUNT(*)
     , Lineages = COUNT(DISTINCT h.SourceType)
     , InDefaultRead = (SELECT COUNT(*) FROM dbo.vwHandlerSource v WHERE v.HandlerId = h.HandlerId)
  FROM dbo.HandlerSource h
 WHERE h.IsDeleted = 0 AND h.HandlerId NOT LIKE N'ZZTEST%'
 GROUP BY h.HandlerId ORDER BY h.HandlerId;
