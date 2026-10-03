SET NOCOUNT ON;
SELECT CONCAT ('HandlerSource rows for MDTEST00001: ', COUNT (*)) FROM dbo.HandlerSource WHERE HandlerId LIKE N'MDTEST%';
SELECT CONCAT ('  id=', HandlerSourceId, ' seq=', Sequence, ' del=', IsDeleted, ' created=', auditCreatedDateUtc)
  FROM dbo.HandlerSource WHERE HandlerId LIKE N'MDTEST%';
SELECT CONCAT ('owners: ', COUNT (*)) FROM dbo.HandlerSourceOwner o JOIN dbo.HandlerSource hs ON hs.HandlerSourceId=o.HandlerSourceId WHERE hs.HandlerId LIKE N'MDTEST%';
