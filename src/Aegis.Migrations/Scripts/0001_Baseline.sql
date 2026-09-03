-- Baseline: AEGIS core schema (task 1.2 will flesh this out).
-- This migration exists so the DbUp pipeline is exercised end-to-end.

IF OBJECT_ID(N'dbo.SchemaVersion', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaVersion
    (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ScriptName NVARCHAR(255) NOT NULL,
        AppliedAt DATETIME2 NOT NULL CONSTRAINT DF_SchemaVersion_AppliedAt DEFAULT SYSUTCDATETIME()
    );
END
