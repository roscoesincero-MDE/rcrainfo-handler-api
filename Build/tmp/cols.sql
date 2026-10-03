SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.HandlerSource')
  AND (name LIKE '%Current%' OR name LIKE 'Src%' OR name IN (N'HandlerId',N'SourceType',N'Sequence'))
ORDER BY column_id;
