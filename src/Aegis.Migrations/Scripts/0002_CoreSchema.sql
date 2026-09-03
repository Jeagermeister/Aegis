-- 0002_CoreSchema.sql
-- AEGIS core schema per DESIGN-v2 data model
-- Eight core tables + contract-layer tables + Alert table
-- Three locked decisions enforced:
-- 1. JobSourceBinding with BoundAt/UnboundAt (no auto-merge)
-- 2. Validation pins ContractVersionId (SOX defensibility)
-- 3. JobOwnership per CatalogSync snapshot (RawEvidence retained)

-- SourceSystem: tracks each scheduler instance
CREATE TABLE dbo.SourceSystem
(
    Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
    Type            NVARCHAR(50)    NOT NULL,  -- 'SQLAgent', 'Airflow', 'VisualCron'
    Name            NVARCHAR(200)   NOT NULL,  -- human-readable name
    Config          NVARCHAR(MAX)   NOT NULL,  -- JSON connection config
    LastHeartbeat   DATETIME2       NULL,      -- collector heartbeat
    CreatedAt       DATETIME2       NOT NULL CONSTRAINT DF_SourceSystem_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2       NOT NULL CONSTRAINT DF_SourceSystem_UpdatedAt DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_SourceSystem_Type ON dbo.SourceSystem (Type);
CREATE INDEX IX_SourceSystem_LastHeartbeat ON dbo.SourceSystem (LastHeartbeat);

-- Job: canonical job identity (ULID)
CREATE TABLE dbo.Job
(
    Id          CHAR(26)        NOT NULL PRIMARY KEY,  -- ULID as char(26)
    Name        NVARCHAR(500)   NOT NULL,
    IsActive    BIT             NOT NULL CONSTRAINT DF_Job_IsActive DEFAULT 1,
    FirstSeen   DATETIME2       NOT NULL CONSTRAINT DF_Job_FirstSeen DEFAULT SYSUTCDATETIME(),
    LastSeen    DATETIME2       NOT NULL CONSTRAINT DF_Job_LastSeen DEFAULT SYSUTCDATETIME(),
    CreatedAt   DATETIME2       NOT NULL CONSTRAINT DF_Job_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt   DATETIME2       NOT NULL CONSTRAINT DF_Job_UpdatedAt DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_Job_Name ON dbo.Job (Name);
CREATE INDEX IX_Job_IsActive ON dbo.Job (IsActive);
CREATE INDEX IX_Job_LastSeen ON dbo.Job (LastSeen);

-- JobSourceBinding: time-bounded binding of Job to SourceSystem native ID
-- Migration = deliberate rebind (new row), never auto-merged
CREATE TABLE dbo.JobSourceBinding
(
    Id               INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
    JobId            CHAR(26)        NOT NULL,
    SourceSystemId   INT             NOT NULL,
    NativeId         NVARCHAR(500)   NOT NULL,  -- scheduler's native job ID
    NativeName       NVARCHAR(500)   NULL,      -- scheduler's native job name
    BoundAt          DATETIME2       NOT NULL CONSTRAINT DF_JobSourceBinding_BoundAt DEFAULT SYSUTCDATETIME(),
    UnboundAt        DATETIME2       NULL,      -- NULL = currently bound
    CreatedAt        DATETIME2       NOT NULL CONSTRAINT DF_JobSourceBinding_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_JobSourceBinding_Job FOREIGN KEY (JobId) REFERENCES dbo.Job (Id),
    CONSTRAINT FK_JobSourceBinding_SourceSystem FOREIGN KEY (SourceSystemId) REFERENCES dbo.SourceSystem (Id)
);

CREATE INDEX IX_JobSourceBinding_JobId ON dbo.JobSourceBinding (JobId);
CREATE INDEX IX_JobSourceBinding_SourceSystemId ON dbo.JobSourceBinding (SourceSystemId);
CREATE INDEX IX_JobSourceBinding_NativeId ON dbo.JobSourceBinding (SourceSystemId, NativeId);
CREATE UNIQUE INDEX UQ_JobSourceBinding_ActiveBinding ON dbo.JobSourceBinding (JobId, SourceSystemId) WHERE UnboundAt IS NULL;
CREATE INDEX IX_JobSourceBinding_BoundAt ON dbo.JobSourceBinding (BoundAt);

-- JobRun: append-only run history (never updated after insert)
CREATE TABLE dbo.JobRun
(
    Id                  BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    JobId               CHAR(26)        NOT NULL,
    SourceSystemId      INT             NOT NULL,
    NativeRunId         NVARCHAR(500)   NOT NULL,  -- scheduler's run/execution ID
    StartedAt           DATETIME2       NOT NULL,
    EndedAt             DATETIME2       NULL,
    Status              NVARCHAR(50)    NOT NULL,  -- 'Running', 'Succeeded', 'Failed', 'Cancelled', etc.
    ErrorText           NVARCHAR(MAX)   NULL,
    FingerprintId       CHAR(32)        NULL,      -- error fingerprint (MD5 of normalized error)
    CreatedAt           DATETIME2       NOT NULL CONSTRAINT DF_JobRun_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_JobRun_Job FOREIGN KEY (JobId) REFERENCES dbo.Job (Id),
    CONSTRAINT FK_JobRun_SourceSystem FOREIGN KEY (SourceSystemId) REFERENCES dbo.SourceSystem (Id),
    CONSTRAINT UQ_JobRun_NativeRun UNIQUE (SourceSystemId, NativeRunId)
);

CREATE INDEX IX_JobRun_JobId ON dbo.JobRun (JobId);
CREATE INDEX IX_JobRun_SourceSystemId ON dbo.JobRun (SourceSystemId);
CREATE INDEX IX_JobRun_StartedAt ON dbo.JobRun (StartedAt);
CREATE INDEX IX_JobRun_Status ON dbo.JobRun (Status);
CREATE INDEX IX_JobRun_FingerprintId ON dbo.JobRun (FingerprintId);
-- Composite for watermark-gap detection and reconciliation
CREATE INDEX IX_JobRun_SourceSystem_NativeRun_Started ON dbo.JobRun (SourceSystemId, NativeRunId, StartedAt);

-- CatalogSync: records each collector sync with row counts for zero-row detection
CREATE TABLE dbo.CatalogSync
(
    Id              BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    SourceSystemId  INT             NOT NULL,
    StartedAt       DATETIME2       NOT NULL CONSTRAINT DF_CatalogSync_StartedAt DEFAULT SYSUTCDATETIME(),
    CompletedAt     DATETIME2       NULL,
    JobCount        INT             NOT NULL CONSTRAINT DF_CatalogSync_JobCount DEFAULT 0,
    UnownedCount    INT             NOT NULL CONSTRAINT DF_CatalogSync_UnownedCount DEFAULT 0,
    Status          NVARCHAR(50)    NOT NULL,  -- 'Running', 'Completed', 'Failed'
    ErrorText       NVARCHAR(MAX)   NULL,
    CreatedAt       DATETIME2       NOT NULL CONSTRAINT DF_CatalogSync_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_CatalogSync_SourceSystem FOREIGN KEY (SourceSystemId) REFERENCES dbo.SourceSystem (Id)
);

CREATE INDEX IX_CatalogSync_SourceSystemId ON dbo.CatalogSync (SourceSystemId);
CREATE INDEX IX_CatalogSync_StartedAt ON dbo.CatalogSync (StartedAt);
CREATE INDEX IX_CatalogSync_Status ON dbo.CatalogSync (Status);

-- JobOwnership: per-sync ownership snapshot (not mutated, append-only per sync)
CREATE TABLE dbo.JobOwnership
(
    Id              BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    JobId           CHAR(26)        NOT NULL,
    TeamId          NVARCHAR(200)   NULL,       -- parsed team identifier
    RawEvidence     NVARCHAR(MAX)   NOT NULL,   -- original description text parsed
    ParsedFrom      NVARCHAR(100)   NOT NULL,   -- which field: 'Description', 'Tags', 'Ticket', etc.
    SyncId          BIGINT          NOT NULL,   -- references CatalogSync
    CreatedAt       DATETIME2       NOT NULL CONSTRAINT DF_JobOwnership_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_JobOwnership_Job FOREIGN KEY (JobId) REFERENCES dbo.Job (Id),
    CONSTRAINT FK_JobOwnership_CatalogSync FOREIGN KEY (SyncId) REFERENCES dbo.CatalogSync (Id)
);

CREATE INDEX IX_JobOwnership_JobId ON dbo.JobOwnership (JobId);
CREATE INDEX IX_JobOwnership_TeamId ON dbo.JobOwnership (TeamId);
CREATE INDEX IX_JobOwnership_SyncId ON dbo.JobOwnership (SyncId);
CREATE INDEX IX_JobOwnership_Unowned ON dbo.JobOwnership (JobId);

-- DependencyEdge: manually declared upstream→downstream edges
CREATE TABLE dbo.DependencyEdge
(
    Id                  BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    UpstreamJobId       CHAR(26)        NOT NULL,
    DownstreamJobId     CHAR(26)        NOT NULL,
    DeclaredBy          NVARCHAR(200)   NOT NULL,  -- user/process that declared the edge
    DeclaredAt          DATETIME2       NOT NULL CONSTRAINT DF_DependencyEdge_DeclaredAt DEFAULT SYSUTCDATETIME(),
    CreatedAt           DATETIME2       NOT NULL CONSTRAINT DF_DependencyEdge_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_DependencyEdge_Upstream FOREIGN KEY (UpstreamJobId) REFERENCES dbo.Job (Id),
    CONSTRAINT FK_DependencyEdge_Downstream FOREIGN KEY (DownstreamJobId) REFERENCES dbo.Job (Id),
    CONSTRAINT UQ_DependencyEdge_Unique UNIQUE (UpstreamJobId, DownstreamJobId),
    CONSTRAINT CK_DependencyEdge_NoSelfRef CHECK (UpstreamJobId <> DownstreamJobId)
);

CREATE INDEX IX_DependencyEdge_UpstreamJobId ON dbo.DependencyEdge (UpstreamJobId);
CREATE INDEX IX_DependencyEdge_DownstreamJobId ON dbo.DependencyEdge (DownstreamJobId);

-- ============================================================
-- Contract layer tables (Track B)
-- ============================================================

-- Feed: landing zone feed definition
CREATE TABLE dbo.Feed
(
    Id              INT             NOT NULL IDENTITY(1,1) PRIMARY KEY,
    Name            NVARCHAR(200)   NOT NULL,
    TeamId          NVARCHAR(200)   NULL,
    LandingPrefix   NVARCHAR(500)   NOT NULL,  -- S3 prefix or file path pattern
    IsActive        BIT             NOT NULL CONSTRAINT DF_Feed_IsActive DEFAULT 1,
    CreatedAt       DATETIME2       NOT NULL CONSTRAINT DF_Feed_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt       DATETIME2       NOT NULL CONSTRAINT DF_Feed_UpdatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_Feed_Name UNIQUE (Name)
);

CREATE INDEX IX_Feed_TeamId ON dbo.Feed (TeamId);
CREATE INDEX IX_Feed_LandingPrefix ON dbo.Feed (LandingPrefix);

-- ContractVersion: versioned contract specs (YAML in git, synced to DB)
CREATE TABLE dbo.ContractVersion
(
    Id              BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    FeedId          INT             NOT NULL,
    Version         INT             NOT NULL,
    SpecHash        CHAR(64)        NOT NULL,  -- SHA256 of SpecJson
    SpecJson        NVARCHAR(MAX)   NOT NULL,  -- contract spec as JSON
    EffectiveFrom   DATETIME2       NOT NULL CONSTRAINT DF_ContractVersion_EffectiveFrom DEFAULT SYSUTCDATETIME(),
    CreatedAt       DATETIME2       NOT NULL CONSTRAINT DF_ContractVersion_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_ContractVersion_Feed FOREIGN KEY (FeedId) REFERENCES dbo.Feed (Id),
    CONSTRAINT UQ_ContractVersion_Feed_Version UNIQUE (FeedId, Version)
);

CREATE INDEX IX_ContractVersion_FeedId ON dbo.ContractVersion (FeedId);
CREATE INDEX IX_ContractVersion_EffectiveFrom ON dbo.ContractVersion (EffectiveFrom);
CREATE INDEX IX_ContractVersion_SpecHash ON dbo.ContractVersion (SpecHash);

-- Arrival: file arrival events in landing zone
CREATE TABLE dbo.Arrival
(
    Id              BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    FeedId          INT             NOT NULL,
    [Key]           NVARCHAR(1000)  NOT NULL,  -- S3 key or file path
    SizeBytes       BIGINT          NOT NULL,
    ArrivedAt       DATETIME2       NOT NULL,
    Disposition     NVARCHAR(50)    NOT NULL,  -- 'Pending', 'Validated', 'Quarantined', 'Missing', 'Late'
    CreatedAt       DATETIME2       NOT NULL CONSTRAINT DF_Arrival_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_Arrival_Feed FOREIGN KEY (FeedId) REFERENCES dbo.Feed (Id),
    CONSTRAINT UQ_Arrival_Feed_Key UNIQUE (FeedId, [Key])
);

CREATE INDEX IX_Arrival_FeedId ON dbo.Arrival (FeedId);
CREATE INDEX IX_Arrival_ArrivedAt ON dbo.Arrival (ArrivedAt);
CREATE INDEX IX_Arrival_Disposition ON dbo.Arrival (Disposition);

-- Validation: validation result pinned to ContractVersion (locked decision #2)
CREATE TABLE dbo.Validation
(
    Id                  BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    ContractVersionId   BIGINT          NOT NULL,
    ArrivalId           BIGINT          NOT NULL,
    Result              NVARCHAR(50)    NOT NULL,  -- 'Passed', 'Failed', 'Error'
    FindingsJson        NVARCHAR(MAX)   NOT NULL,  -- structured findings (schema diffs, null counts, etc.)
    RanAt               DATETIME2       NOT NULL CONSTRAINT DF_Validation_RanAt DEFAULT SYSUTCDATETIME(),
    CreatedAt           DATETIME2       NOT NULL CONSTRAINT DF_Validation_CreatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_Validation_ContractVersion FOREIGN KEY (ContractVersionId) REFERENCES dbo.ContractVersion (Id),
    CONSTRAINT FK_Validation_Arrival FOREIGN KEY (ArrivalId) REFERENCES dbo.Arrival (Id),
    CONSTRAINT UQ_Validation_Arrival_Version UNIQUE (ArrivalId, ContractVersionId)
);

CREATE INDEX IX_Validation_ContractVersionId ON dbo.Validation (ContractVersionId);
CREATE INDEX IX_Validation_ArrivalId ON dbo.Validation (ArrivalId);
CREATE INDEX IX_Validation_Result ON dbo.Validation (Result);
CREATE INDEX IX_Validation_RanAt ON dbo.Validation (RanAt);

-- ============================================================
-- Alert table (shared by both tracks)
-- ============================================================

-- Alert: deduplicated, routed, acknowledged alerts with audit trail
CREATE TABLE dbo.Alert
(
    Id                  BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    Type                NVARCHAR(100)   NOT NULL,  -- 'JobFailed', 'FeedLate', 'SchemaDrift', 'CollectorStale', etc.
    DedupKey            CHAR(64)        NOT NULL,  -- SHA256 of normalized alert signature
    Status              NVARCHAR(50)    NOT NULL,  -- 'Firing', 'Acknowledged', 'Resolved'
    RoutedTo            NVARCHAR(200)   NULL,      -- team/user routed to
    FirstSeen           DATETIME2       NOT NULL CONSTRAINT DF_Alert_FirstSeen DEFAULT SYSUTCDATETIME(),
    LastOccurrence      DATETIME2       NOT NULL CONSTRAINT DF_Alert_LastOccurrence DEFAULT SYSUTCDATETIME(),
    Occurrences         INT             NOT NULL CONSTRAINT DF_Alert_Occurrences DEFAULT 1,
    AckedBy             NVARCHAR(200)   NULL,
    AckedAt             DATETIME2       NULL,
    CreatedAt           DATETIME2       NOT NULL CONSTRAINT DF_Alert_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt           DATETIME2       NOT NULL CONSTRAINT DF_Alert_UpdatedAt DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_Alert_DedupKey UNIQUE (DedupKey)
);

CREATE INDEX IX_Alert_Type ON dbo.Alert (Type);
CREATE INDEX IX_Alert_Status ON dbo.Alert (Status);
CREATE INDEX IX_Alert_RoutedTo ON dbo.Alert (RoutedTo);
CREATE INDEX IX_Alert_FirstSeen ON dbo.Alert (FirstSeen);
CREATE INDEX IX_Alert_LastOccurrence ON dbo.Alert (LastOccurrence);