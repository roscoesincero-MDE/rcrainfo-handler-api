SET NOCOUNT ON;
SELECT CONCAT(OBJECT_NAME(i.object_id),' | ',i.name,' | uq=',i.is_unique,' | filter=',ISNULL(i.filter_definition,'(none)'),
       ' | keys=', (SELECT STRING_AGG(c.name,',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                      FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                     WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.is_included_column=0))
FROM sys.indexes i
WHERE i.object_id IN (OBJECT_ID('dbo.HandlerSource'), OBJECT_ID('dbo.HandlerSourceOwner'), OBJECT_ID('dbo.HandlerSourceHsmActivityWasteCode'))
  AND i.type > 0
ORDER BY OBJECT_NAME(i.object_id), i.index_id;
SELECT '--- CHECK constraints on logs.DataQualityObservation ---';
SELECT CONCAT(name,' : ',definition) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('logs.DataQualityObservation');
