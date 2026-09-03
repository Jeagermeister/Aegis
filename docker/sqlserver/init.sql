-- Sample SQL Agent jobs for the dev stack. Run by the sqlserver-init container on every
-- `docker compose up`; every step is idempotent.
--
--   AEGIS Sample - BCBS Eligibility Load    succeeds every minute, owner declared, one step
--   AEGIS Sample - Aetna Claims Load        fails every minute, no owner (lands on the gap list)
--   AEGIS Sample - Cigna Claims Reconcile   two steps, second fails, every 5 minutes, team + tag declared
--
--   AEGIS Sample - Purge History           deletes history older than 20 minutes, every 5 minutes
--
-- The purge job is the hostile retention DESIGN-v2 asks for ("configure hostile retention in the
-- dev container"), done the way production DBAs do it. sp_set_sqlagent_properties is silently
-- ignored by SQL Server on Linux, so the Agent's own max-rows settings cannot be used here.
-- Stop the collector for longer than 20 minutes and its next sync should raise
-- SqlAgentHistoryPurgeGap; keep it running and it should not.

USE msdb;
GO

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'AEGIS Sample - BCBS Eligibility Load')
BEGIN
    EXEC msdb.dbo.sp_add_job
        @job_name = N'AEGIS Sample - BCBS Eligibility Load',
        @description = N'Owner: LPDE; Ticket: DE-101. Loads the BCBS eligibility feed into staging.',
        @enabled = 1;

    EXEC msdb.dbo.sp_add_jobstep
        @job_name = N'AEGIS Sample - BCBS Eligibility Load',
        @step_name = N'Load staging',
        @subsystem = N'TSQL',
        @database_name = N'master',
        @command = N'WAITFOR DELAY ''00:00:20''; SELECT 1 AS loaded;';

    EXEC msdb.dbo.sp_add_schedule
        @schedule_name = N'AEGIS Sample - every minute',
        @freq_type = 4,
        @freq_interval = 1,
        @freq_subday_type = 4,
        @freq_subday_interval = 1,
        @active_start_time = 0;

    EXEC msdb.dbo.sp_attach_schedule
        @job_name = N'AEGIS Sample - BCBS Eligibility Load',
        @schedule_name = N'AEGIS Sample - every minute';

    EXEC msdb.dbo.sp_add_jobserver
        @job_name = N'AEGIS Sample - BCBS Eligibility Load';
END
GO

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'AEGIS Sample - Aetna Claims Load')
BEGIN
    EXEC msdb.dbo.sp_add_job
        @job_name = N'AEGIS Sample - Aetna Claims Load',
        @description = N'Loads the Aetna claims feed. See wiki.',
        @enabled = 1;

    EXEC msdb.dbo.sp_add_jobstep
        @job_name = N'AEGIS Sample - Aetna Claims Load',
        @step_name = N'Load claims',
        @subsystem = N'TSQL',
        @database_name = N'master',
        @command = N'DECLARE @file NVARCHAR(200) = N''/landing/AETNA_CLAIMS_'' + CONVERT(NVARCHAR(8), GETDATE(), 112) + N''.txt'';
                     RAISERROR (N''Could not find file ''''%s''''. The vendor drop did not arrive.'', 16, 1, @file);';

    EXEC msdb.dbo.sp_add_schedule
        @schedule_name = N'AEGIS Sample - every minute (claims)',
        @freq_type = 4,
        @freq_interval = 1,
        @freq_subday_type = 4,
        @freq_subday_interval = 1,
        @active_start_time = 30;

    EXEC msdb.dbo.sp_attach_schedule
        @job_name = N'AEGIS Sample - Aetna Claims Load',
        @schedule_name = N'AEGIS Sample - every minute (claims)';

    EXEC msdb.dbo.sp_add_jobserver
        @job_name = N'AEGIS Sample - Aetna Claims Load';
END
GO

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'AEGIS Sample - Cigna Claims Reconcile')
BEGIN
    EXEC msdb.dbo.sp_add_job
        @job_name = N'AEGIS Sample - Cigna Claims Reconcile',
        @description = N'Team: Carrier Integration #carrier. Reconciles Cigna claims against remittance.',
        @enabled = 1;

    EXEC msdb.dbo.sp_add_jobstep
        @job_name = N'AEGIS Sample - Cigna Claims Reconcile',
        @step_name = N'Stage remittance',
        @subsystem = N'TSQL',
        @database_name = N'master',
        @command = N'SELECT 1 AS staged;',
        @on_success_action = 3;  -- go to the next step

    EXEC msdb.dbo.sp_add_jobstep
        @job_name = N'AEGIS Sample - Cigna Claims Reconcile',
        @step_name = N'Reconcile',
        @subsystem = N'TSQL',
        @database_name = N'master',
        @command = N'RAISERROR (N''Schema drift: column ''''PolicyDate'''' not found in remittance file (expected 14 columns, found 15).'', 16, 1);';

    EXEC msdb.dbo.sp_add_schedule
        @schedule_name = N'AEGIS Sample - every 5 minutes',
        @freq_type = 4,
        @freq_interval = 1,
        @freq_subday_type = 4,
        @freq_subday_interval = 5,
        @active_start_time = 0;

    EXEC msdb.dbo.sp_attach_schedule
        @job_name = N'AEGIS Sample - Cigna Claims Reconcile',
        @schedule_name = N'AEGIS Sample - every 5 minutes';

    EXEC msdb.dbo.sp_add_jobserver
        @job_name = N'AEGIS Sample - Cigna Claims Reconcile';
END
GO

IF NOT EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'AEGIS Sample - Purge History')
BEGIN
    EXEC msdb.dbo.sp_add_job
        @job_name = N'AEGIS Sample - Purge History',
        @description = N'Owner: DBA. Hostile retention for the dev stack: keeps 20 minutes of job history.',
        @enabled = 1;

    EXEC msdb.dbo.sp_add_jobstep
        @job_name = N'AEGIS Sample - Purge History',
        @step_name = N'Purge',
        @subsystem = N'TSQL',
        @database_name = N'msdb',
        @command = N'DECLARE @oldest DATETIME = DATEADD(MINUTE, -20, GETDATE()); EXEC msdb.dbo.sp_purge_jobhistory @oldest_date = @oldest;';

    EXEC msdb.dbo.sp_add_schedule
        @schedule_name = N'AEGIS Sample - every 5 minutes (purge)',
        @freq_type = 4,
        @freq_interval = 1,
        @freq_subday_type = 4,
        @freq_subday_interval = 5,
        @active_start_time = 200;

    EXEC msdb.dbo.sp_attach_schedule
        @job_name = N'AEGIS Sample - Purge History',
        @schedule_name = N'AEGIS Sample - every 5 minutes (purge)';

    EXEC msdb.dbo.sp_add_jobserver
        @job_name = N'AEGIS Sample - Purge History';
END
GO

PRINT 'AEGIS sample Agent jobs are in place.';
GO
