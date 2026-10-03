SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

SELECT Status, Outcome, Attempts = SUM(AttemptCount), Rows = COUNT(*),
       NullOutcome = SUM(CASE WHEN Outcome IS NULL THEN 1 ELSE 0 END),
       NullCompleted = SUM(CASE WHEN CompletedDateUtc IS NULL THEN 1 ELSE 0 END),
       NullSourceId = SUM(CASE WHEN HandlerSourceId IS NULL THEN 1 ELSE 0 END),
       NullSha = SUM(CASE WHEN PayloadSha256 IS NULL THEN 1 ELSE 0 END)
  FROM logs.HandlerLoadStatus
 WHERE LoadRunId = 2590
 GROUP BY Status, Outcome;

SELECT Versions = COUNT(*), MinSeq = MIN(Sequence), MaxSeq = MAX(Sequence),
       SourceTypes = COUNT(DISTINCT SourceType),
       CurrentRecords = SUM(CASE WHEN CurrentRecord = 1 THEN 1 ELSE 0 END)
  FROM dbo.HandlerSource
 WHERE HandlerId = N'MDR000501742' AND IsDeleted = 0;

SELECT SourceType, Sequence, CurrentRecord, SrcCreatedDate, SrcUpdatedDate
  FROM dbo.HandlerSource
 WHERE HandlerId = N'MDR000501742' AND IsDeleted = 0
 ORDER BY SourceType, Sequence;
