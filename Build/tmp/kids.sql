SET NOCOUNT ON;
SELECT CONCAT(SCHEMA_NAME(t.schema_id), '.', t.name, '  cols=',
       (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = t.object_id))
  FROM sys.tables AS t
  JOIN sys.foreign_keys AS f ON f.parent_object_id = t.object_id
 WHERE f.referenced_object_id = OBJECT_ID('dbo.HandlerSource')
 ORDER BY t.name;
