SET NOCOUNT ON;
CREATE TABLE #Child
(
    ChildId              INT IDENTITY (1,1) NOT NULL PRIMARY KEY,
    HandlerSourceId      INT             NOT NULL,
    OrdinalPosition      INT             NOT NULL,
    OwnerName            NVARCHAR (80)       NULL,
    OwnerType            NVARCHAR (1)        NULL,
    IsDeleted            BIT             NOT NULL DEFAULT (0),
    auditDeletedBy       NVARCHAR (128)  NOT NULL DEFAULT (ORIGINAL_LOGIN ()),
    auditDeletedDateUtc  DATETIME2       NOT NULL DEFAULT (SYSUTCDATETIME ()),
    auditCreatedBy       NVARCHAR (128)  NOT NULL DEFAULT (ORIGINAL_LOGIN ()),
    auditCreatedDateUtc  DATETIME2       NOT NULL DEFAULT (SYSUTCDATETIME ()),
    auditModifiedBy      NVARCHAR (128)  NOT NULL DEFAULT (ORIGINAL_LOGIN ()),
    auditModifiedDateUtc DATETIME2       NOT NULL DEFAULT ('2000-01-01')
);
CREATE UNIQUE INDEX UX_Natural ON #Child (HandlerSourceId, OrdinalPosition) WHERE IsDeleted = 0;
GO
SET NOCOUNT ON;
DECLARE @NowUtc DATETIME2 = '2026-09-05T12:00:00';

-- 101 stored 2 owners; payload now sends 1 changed + drops ordinal 1, and adds ordinal 2 (new).
-- 102 stored 1 owner; payload OMITS owners entirely -> must survive.
-- 103 stored 1 owner, soft-deleted at ordinal 0; payload sends it again -> must revive.
INSERT INTO #Child (HandlerSourceId, OrdinalPosition, OwnerName, OwnerType, IsDeleted)
VALUES (101, 0, N'A',   N'P', 0)
     , (101, 1, N'B',   N'P', 0)
     , (102, 0, N'Z',   N'P', 0)
     , (104, 0, N'EMPTY',N'P', 0)
     , (103, 0, N'GONE',N'P', 1);

DECLARE @Version TABLE (Ordinal INT NOT NULL PRIMARY KEY, HandlerSourceId INT NOT NULL UNIQUE);
INSERT INTO @Version VALUES (1, 101), (2, 102), (3, 103), (4, 104);

DECLARE @Sent TABLE (TableName NVARCHAR (128) NOT NULL, ParentId INT NOT NULL, PRIMARY KEY (TableName, ParentId));
DECLARE @ChildAction TABLE (TableName NVARCHAR (128) NOT NULL, Action NVARCHAR (10) NOT NULL, IsDeleted BIT NOT NULL);

DECLARE @Payload NVARCHAR (MAX) = N'[
 {"handler":{"handlerId":"MD1","owners":[{"name":"A","type":"P"}]}},
 {"handler":{"handlerId":"MD2"}},
 {"handler":{"handlerId":"MD3","owners":[{"name":"GONE","type":"P"}]}},
 {"handler":{"handlerId":"MD4","owners":[]}}
]';

INSERT INTO @Sent (TableName, ParentId)
SELECT g.TableName, v.HandlerSourceId
  FROM OPENJSON (@Payload) AS e
  JOIN @Version AS v ON v.Ordinal = CAST (e.[key] AS INT) + 1
 CROSS APPLY (VALUES (N'HandlerSourceOwner', JSON_PATH_EXISTS (e.[value], '$.handler.owners'))) AS g (TableName, Sent)
 WHERE g.Sent = 1;

SELECT CONCAT('@Sent: ', STRING_AGG (CONCAT (TableName, '/', ParentId), ' ')) FROM @Sent;

WITH Batch AS
(
    SELECT c.HandlerSourceId, c.OrdinalPosition, c.OwnerName, c.OwnerType
         , c.IsDeleted, c.auditDeletedBy, c.auditDeletedDateUtc
         , c.auditModifiedBy, c.auditModifiedDateUtc
      FROM #Child AS c WITH (HOLDLOCK)
     WHERE c.HandlerSourceId IN (SELECT v.HandlerSourceId FROM @Version AS v)
)
MERGE Batch AS tgt
USING (SELECT v.HandlerSourceId
            , CAST (arr.[key] AS INT) AS OrdinalPosition
            , j.OwnerName
            , j.OwnerType
         FROM OPENJSON (@Payload) AS e
         JOIN @Version AS v ON v.Ordinal = CAST (e.[key] AS INT) + 1
        CROSS APPLY OPENJSON (e.[value], '$.handler.owners') AS arr
        CROSS APPLY OPENJSON (arr.[value])
             WITH ( OwnerName NVARCHAR (80) '$.name'
                  , OwnerType NVARCHAR (1)  '$.type' ) AS j) AS src
   ON tgt.HandlerSourceId = src.HandlerSourceId
  AND tgt.OrdinalPosition = src.OrdinalPosition
WHEN MATCHED AND (tgt.IsDeleted = 1
               OR tgt.OwnerName IS DISTINCT FROM src.OwnerName
               OR tgt.OwnerType IS DISTINCT FROM src.OwnerType) THEN
    UPDATE SET OwnerName            = src.OwnerName
             , OwnerType            = src.OwnerType
             , IsDeleted            = 0
             , auditModifiedBy      = ORIGINAL_LOGIN ()
             , auditModifiedDateUtc = @NowUtc
WHEN NOT MATCHED BY TARGET THEN
    INSERT (HandlerSourceId, OrdinalPosition, OwnerName, OwnerType)
    VALUES (src.HandlerSourceId, src.OrdinalPosition, src.OwnerName, src.OwnerType)
WHEN NOT MATCHED BY SOURCE
 AND EXISTS (SELECT 1 FROM @Sent AS s
              WHERE s.TableName = N'HandlerSourceOwner'
                AND s.ParentId  = tgt.HandlerSourceId) THEN
    UPDATE SET IsDeleted            = 1
             , auditDeletedBy       = ORIGINAL_LOGIN ()
             , auditDeletedDateUtc  = @NowUtc
             , auditModifiedBy      = ORIGINAL_LOGIN ()
             , auditModifiedDateUtc = @NowUtc
OUTPUT N'HandlerSourceOwner', $action, inserted.IsDeleted
  INTO @ChildAction (TableName, Action, IsDeleted);

SELECT CONCAT ('rows affected = ', @@ROWCOUNT);
SELECT CONCAT ('OUTPUT: inserted=', SUM (CASE WHEN Action = N'INSERT' THEN 1 ELSE 0 END),
               ' updated=',         SUM (CASE WHEN Action = N'UPDATE' AND IsDeleted = 0 THEN 1 ELSE 0 END),
               ' retired=',         SUM (CASE WHEN Action = N'UPDATE' AND IsDeleted = 1 THEN 1 ELSE 0 END))
  FROM @ChildAction;

SELECT CONCAT (HandlerSourceId, '/', OrdinalPosition, ' name=', ISNULL(OwnerName,'(null)'),
               ' type=', ISNULL(OwnerType,'-'), ' del=', IsDeleted,
               ' touched=', CASE WHEN auditModifiedDateUtc = @NowUtc THEN 'yes' ELSE 'no' END)
  FROM #Child ORDER BY HandlerSourceId, OrdinalPosition, ChildId;
GO
DROP TABLE #Child;
