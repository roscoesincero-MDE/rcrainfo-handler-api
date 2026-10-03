SET NOCOUNT ON;
SELECT t.name + '.' + c.name FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='logs' AND t.name IN ('LoadRun','HandlerLoadStatus','HandlerLoadAttempt','DataQualityObservation') AND c.name NOT LIKE 'audit%' AND c.name <> 'IsDeleted' ORDER BY t.name, c.column_id;
