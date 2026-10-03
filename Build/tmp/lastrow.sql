SET NOCOUNT ON;
SELECT TOP 3 ExecutionLogId, ProcedureName, Successful, ErrorNumber, ErrorProcedure, ReCreatedAfterRollback,
       LEFT(ISNULL(KeyParameters, N'(null)'), 60) AS KeyParams
  FROM logs.ExecutionLog ORDER BY ExecutionLogId DESC;
SELECT c.name, t.name AS T, c.is_nullable
  FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
 WHERE c.object_id = OBJECT_ID(N'logs.ExecutionLog')
   AND c.name IN (N'Successful', N'ErrorNumber', N'ProcedureName', N'ReCreatedAfterRollback', N'KeyParameters');
SELECT COUNT(*) AS TableTypes FROM sys.table_types;
