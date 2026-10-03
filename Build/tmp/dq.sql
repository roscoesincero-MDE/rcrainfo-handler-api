SET NOCOUNT ON;
SELECT CONCAT(c.name,' ',TYPE_NAME(c.user_type_id),
  CASE WHEN TYPE_NAME(c.user_type_id) LIKE '%char%' THEN CONCAT('(',c.max_length/2,')') ELSE '' END,
  CASE WHEN c.is_nullable=1 THEN ' NULL' ELSE ' NOT NULL' END,
  CASE WHEN c.is_identity=1 THEN ' IDENTITY' ELSE '' END)
FROM sys.columns c WHERE c.object_id = OBJECT_ID('logs.DataQualityObservation') ORDER BY c.column_id;
