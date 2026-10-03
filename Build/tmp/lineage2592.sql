SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

PRINT '--- lineages by how many versions they flag current, broken out by source type ---';
WITH L AS (
    SELECT HandlerId, SourceType
         , Versions = COUNT(*)
         , Current1 = SUM(CASE WHEN CurrentRecord = 1 THEN 1 ELSE 0 END)
         , MaxSeq   = MAX(Sequence)
         , DistinctSeq = COUNT(DISTINCT Sequence)
      FROM dbo.HandlerSource WHERE IsDeleted = 0
     GROUP BY HandlerId, SourceType)
SELECT SourceType
     , Lineages = COUNT(*)
     , NoCurrent  = SUM(CASE WHEN Current1 = 0 THEN 1 ELSE 0 END)
     , OneCurrent = SUM(CASE WHEN Current1 = 1 THEN 1 ELSE 0 END)
     , ManyCurrent= SUM(CASE WHEN Current1 > 1 THEN 1 ELSE 0 END)
  FROM L GROUP BY SourceType ORDER BY SourceType;

PRINT '--- is the mirror dense in Sequence? gaps mean EPA does not publish every sequence ---';
WITH L AS (
    SELECT HandlerId, SourceType, Versions = COUNT(*), MaxSeq = MAX(Sequence), MinSeq = MIN(Sequence)
      FROM dbo.HandlerSource WHERE IsDeleted = 0 GROUP BY HandlerId, SourceType)
SELECT Shape = CASE WHEN MinSeq = 1 AND Versions = MaxSeq THEN 'dense from 1'
                    WHEN Versions = MaxSeq - MinSeq + 1   THEN 'dense, not from 1'
                    ELSE 'has gaps' END
     , Lineages = COUNT(*)
     , VersionsHeld = SUM(Versions)
  FROM L GROUP BY CASE WHEN MinSeq = 1 AND Versions = MaxSeq THEN 'dense from 1'
                       WHEN Versions = MaxSeq - MinSeq + 1   THEN 'dense, not from 1'
                       ELSE 'has gaps' END;

PRINT '--- the lineages with no current version, in full ---';
WITH L AS (
    SELECT HandlerId, SourceType, Versions = COUNT(*), MaxSeq = MAX(Sequence)
         , Current1 = SUM(CASE WHEN CurrentRecord = 1 THEN 1 ELSE 0 END)
      FROM dbo.HandlerSource WHERE IsDeleted = 0 GROUP BY HandlerId, SourceType)
SELECT HandlerId, SourceType, Versions, MaxSeq
     , AlsoHasCurrentElsewhere = CASE WHEN EXISTS (
           SELECT 1 FROM dbo.HandlerSource x
            WHERE x.HandlerId = L.HandlerId AND x.IsDeleted = 0 AND x.CurrentRecord = 1)
         THEN 'yes' ELSE 'no' END
  FROM L WHERE Current1 = 0 ORDER BY HandlerId, SourceType;

PRINT '--- and the lineages with more than one ---';
WITH L AS (
    SELECT HandlerId, SourceType
         , Current1 = SUM(CASE WHEN CurrentRecord = 1 THEN 1 ELSE 0 END)
      FROM dbo.HandlerSource WHERE IsDeleted = 0 GROUP BY HandlerId, SourceType)
SELECT h.HandlerId, h.SourceType, h.Sequence, h.CurrentRecord
     , SrcUpdated = CONVERT(varchar(10), h.SrcUpdatedDate, 23)
  FROM dbo.HandlerSource h JOIN L ON L.HandlerId = h.HandlerId AND L.SourceType = h.SourceType
 WHERE L.Current1 > 1 AND h.IsDeleted = 0 AND h.CurrentRecord = 1
 ORDER BY h.HandlerId, h.SourceType, h.Sequence;
