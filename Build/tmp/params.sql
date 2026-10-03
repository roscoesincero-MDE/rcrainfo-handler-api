SET NOCOUNT ON;
SELECT CONCAT(s.name, N'.', o.name, N' | ', p.name, N' ', TYPE_NAME(p.user_type_id),
   CASE WHEN TYPE_NAME(p.user_type_id) LIKE N'%char%' THEN CONCAT(N'(', p.max_length, N')') ELSE N'' END,
   CASE WHEN p.is_output = 1 THEN N' OUT' ELSE N'' END)
  FROM sys.parameters p JOIN sys.procedures o ON o.object_id=p.object_id
  JOIN sys.schemas s ON s.schema_id=o.schema_id
 WHERE CONCAT(s.name,N'.',o.name) NOT IN (N'logs.uspStartExecutionLogging',N'logs.uspStartExecutionLoggingInsert',
       N'logs.uspRecordExecutionError',N'logs.uspRecordExecutionErrorUpdate',N'util.uspSetObjectDescription')
 ORDER BY s.name, o.name, p.parameter_id;
