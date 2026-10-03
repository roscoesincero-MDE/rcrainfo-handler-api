SET NOCOUNT ON;
PRINT '--- vwHandlerSource columns (in order)';
SELECT CAST(c.column_id AS varchar(9)) + ' ' + c.name + ' ' + TYPE_NAME(c.user_type_id)
     + CASE WHEN TYPE_NAME(c.user_type_id) LIKE 'n%char' THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length/2 AS varchar(9)) END + ')' ELSE '' END
     + CASE WHEN c.is_nullable = 0 THEN ' NOT NULL' ELSE '' END
  FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.vwHandlerSource') ORDER BY c.column_id;
PRINT '--- HandlerSource indexes';
SELECT i.name + CASE WHEN i.is_unique = 1 THEN ' UNIQUE' ELSE '' END + ' :: '
     + STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
               ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '')
     + COALESCE(' INCLUDE (' + STUFF((SELECT ', ' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1 ORDER BY ic.index_column_id FOR XML PATH('')), 1, 2, '') + ')', '')
     + COALESCE(' WHERE ' + i.filter_definition, '')
  FROM sys.indexes i WHERE i.object_id = OBJECT_ID('dbo.HandlerSource') AND i.type > 0;
PRINT '--- HandlerSource CHECK constraints';
SELECT k.name + ' :: ' + k.definition FROM sys.check_constraints k WHERE k.parent_object_id = OBJECT_ID('dbo.HandlerSource');
PRINT '--- HandlerSource NOT NULL columns';
SELECT c.name + ' ' + TYPE_NAME(c.user_type_id) + CASE WHEN d.definition IS NOT NULL THEN ' DF' ELSE '' END + CASE WHEN c.is_identity = 1 THEN ' IDENTITY' ELSE '' END
  FROM sys.columns c LEFT JOIN sys.default_constraints d ON d.parent_object_id = c.object_id AND d.parent_column_id = c.column_id
 WHERE c.object_id = OBJECT_ID('dbo.HandlerSource') AND c.is_nullable = 0 ORDER BY c.column_id;
PRINT '--- row counts';
SELECT 'HandlerSource total ' + CAST(COUNT(*) AS varchar(19)) FROM dbo.HandlerSource;
SELECT 'HandlerSource visible ' + CAST(COUNT(*) AS varchar(19)) FROM dbo.HandlerSource WHERE IsDeleted = 0;
SELECT 'columns in HandlerSource ' + CAST(COUNT(*) AS varchar(9)) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.HandlerSource');
