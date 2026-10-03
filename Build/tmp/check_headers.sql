SET NOCOUNT ON;
SELECT SCHEMA_NAME (o.schema_id) + N'.' + o.name AS ObjectName
     , o.type_desc
     , CASE WHEN OBJECT_DEFINITION (o.object_id) LIKE N'%ObjectName:%'
             AND OBJECT_DEFINITION (o.object_id) LIKE N'%Modification History:%'
            THEN N'YES' ELSE N'no' END AS HeaderPresent
  FROM sys.objects AS o
 WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF')
   AND o.is_ms_shipped = 0
 ORDER BY HeaderPresent, ObjectName;

SELECT SUM (CASE WHEN OBJECT_DEFINITION (o.object_id) LIKE N'%ObjectName:%'
                  AND OBJECT_DEFINITION (o.object_id) LIKE N'%Modification History:%'
                 THEN 1 ELSE 0 END) AS WithHeader
     , COUNT (*) AS TotalObjects
  FROM sys.objects AS o
 WHERE o.type IN ('P', 'V', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0;
