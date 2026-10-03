SET NOCOUNT ON;
SELECT i.name + ' :: ' + STUFF((SELECT ', ' + c.name FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.is_included_column=0 ORDER BY ic.key_ordinal FOR XML PATH('')),1,2,'') FROM sys.indexes i WHERE i.object_id=OBJECT_ID('logs.LoadRun') AND i.type>0;
