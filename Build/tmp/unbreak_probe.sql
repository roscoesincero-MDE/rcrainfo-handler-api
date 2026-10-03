SET XACT_ABORT ON;
IF OBJECT_ID (N'dbo.uspZZSwallowProbe', N'P') IS NOT NULL DROP PROCEDURE dbo.uspZZSwallowProbe;
IF TYPE_ID (N'dbo.ZZProbeTableType') IS NOT NULL DROP TYPE dbo.ZZProbeTableType;
PRINT N'probe objects removed';
