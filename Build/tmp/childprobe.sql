SET NOCOUNT ON;
DECLARE @Present  NVARCHAR (MAX) = N'{"handler":{"hsmActivity":[{"code":"A"},{"code":"B"}]}}';
DECLARE @Empty    NVARCHAR (MAX) = N'{"handler":{"hsmActivity":[]}}';
DECLARE @Absent   NVARCHAR (MAX) = N'{"handler":{}}';

PRINT '--- P1: JSON_PATH_EXISTS distinguishes absent from empty ---';
DECLARE @p int = JSON_PATH_EXISTS (@Present, '$.handler.hsmActivity');
DECLARE @e int = JSON_PATH_EXISTS (@Empty,   '$.handler.hsmActivity');
DECLARE @a int = JSON_PATH_EXISTS (@Absent,  '$.handler.hsmActivity');
PRINT CONCAT ('present=', @p, ' empty=', @e, ' absent=', @a);

PRINT '--- P2: OPENJSON over a NULL AS JSON column ---';
BEGIN TRY
    DECLARE @rows int;
    SELECT @rows = COUNT (*)
      FROM OPENJSON (@Absent) WITH (hsmActivity NVARCHAR (MAX) '$.handler.hsmActivity' AS JSON) AS e
     CROSS APPLY OPENJSON (e.hsmActivity) AS arr;
    PRINT CONCAT ('CROSS APPLY OPENJSON over an absent property: ', @rows, ' row(s), no error');
END TRY
BEGIN CATCH
    PRINT CONCAT ('ERROR ', ERROR_NUMBER (), ': ', ERROR_MESSAGE ());
END CATCH;

PRINT '--- P3: arr.[key] as the zero-based ordinal ---';
DECLARE @ords NVARCHAR (100);
SELECT @ords = STRING_AGG (CONCAT (arr.[key], '=>', JSON_VALUE (arr.value, '$.code')), ',')
  FROM OPENJSON (@Present) WITH (hsmActivity NVARCHAR (MAX) '$.handler.hsmActivity' AS JSON) AS e
 CROSS APPLY OPENJSON (e.hsmActivity) AS arr;
PRINT CONCAT ('ordinals: ', @ords);

PRINT '--- P4: is a CTE a legal MERGE target? ---';
BEGIN TRY
    EXEC (N'
      CREATE TABLE #T (Id int IDENTITY, ParentId int, Ord int, Val nvarchar (10), IsDeleted bit DEFAULT (0));
      INSERT INTO #T (ParentId, Ord, Val) VALUES (1, 0, N''keep''), (1, 1, N''drop''), (2, 0, N''other'');
      WITH t AS (SELECT * FROM #T WHERE ParentId = 1)
      MERGE t AS tgt
      USING (SELECT 1 AS ParentId, 0 AS Ord, N''keep'' AS Val) AS src
         ON tgt.ParentId = src.ParentId AND tgt.Ord = src.Ord
      WHEN NOT MATCHED BY SOURCE THEN UPDATE SET IsDeleted = 1;
      SELECT CONCAT (Id, '':'', Val, '':deleted='', IsDeleted) FROM #T ORDER BY Id;');
END TRY
BEGIN CATCH
    PRINT CONCAT ('ERROR ', ERROR_NUMBER (), ': ', ERROR_MESSAGE ());
END CATCH;
