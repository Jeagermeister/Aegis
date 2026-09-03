-- 0003_CollectorState.sql
-- Collector state and identity constraints that 0002 was missing. Found in the 2026-09-03 review.

-- 1. Source watermark. Each collector remembers where its incremental read stopped
--    (SQL Agent: the last sysjobhistory outcome instance_id; Airflow: the instant of the last poll).
--    It was living in memory (lost on restart) or overwriting SourceSystem.Config (which then
--    failed to parse). Text, because the two sources watermark on different types. Written in the
--    same transaction as the batch it describes.
ALTER TABLE dbo.SourceSystem ADD Watermark NVARCHAR(100) NULL;

-- 2. A native id maps to at most one canonical job at a time: locked decision 1 read from the
--    other direction. 0002 only guaranteed one active binding per (job, source).
--    NativeId inherits the database collation (case-insensitive by default), so two Airflow DAG
--    ids differing only in case would collide here. Airflow allows that; nobody should.
CREATE UNIQUE INDEX UQ_JobSourceBinding_ActiveNativeId
    ON dbo.JobSourceBinding (SourceSystemId, NativeId)
    WHERE UnboundAt IS NULL;

-- 3. IX_JobOwnership_Unowned duplicated IX_JobOwnership_JobId column for column. Replace it with
--    the index the unowned-gap query actually wants: the null-owner rows of one sync.
DROP INDEX IX_JobOwnership_Unowned ON dbo.JobOwnership;
CREATE INDEX IX_JobOwnership_Unowned
    ON dbo.JobOwnership (SyncId, JobId)
    WHERE TeamId IS NULL;

-- 4. The zero-row baseline reads "the last N completed syncs of this source, newest first".
CREATE INDEX IX_CatalogSync_Source_Started
    ON dbo.CatalogSync (SourceSystemId, StartedAt DESC)
    INCLUDE (Status, JobCount);
