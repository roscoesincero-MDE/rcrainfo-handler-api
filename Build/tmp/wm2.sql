SET NOCOUNT ON;
SELECT p.name, TYPE_NAME(p.user_type_id) AS T, p.max_length
  FROM sys.parameters p JOIN sys.procedures o ON o.object_id=p.object_id
  JOIN sys.schemas s ON s.schema_id=o.schema_id
 WHERE s.name=N'config' AND o.name=N'uspSetLoadWatermark' ORDER BY p.parameter_id;
