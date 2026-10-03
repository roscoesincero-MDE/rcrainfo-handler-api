SET NOCOUNT ON;
SELECT CONCAT(s.name, N'.', o.name, N' | try=',
       CASE WHEN m.definition LIKE N'%BEGIN TRY%' THEN 1 ELSE 0 END, N' record=',
       CASE WHEN m.definition LIKE N'%uspRecordExecutionError%' THEN 1 ELSE 0 END, N' throw=',
       CASE WHEN m.definition LIKE N'%THROW;%' THEN 1 ELSE 0 END)
  FROM sys.sql_modules m
  JOIN sys.procedures o ON o.object_id = m.object_id
  JOIN sys.schemas s ON s.schema_id = o.schema_id
 WHERE CONCAT(s.name,N'.',o.name) NOT IN (N'logs.uspStartExecutionLogging',N'logs.uspStartExecutionLoggingInsert',
       N'logs.uspRecordExecutionError',N'logs.uspRecordExecutionErrorUpdate',N'util.uspSetObjectDescription')
 ORDER BY s.name, o.name;
