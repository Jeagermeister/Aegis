# QUICKSTART — install & run AEGIS locally

> Companion to [README](../README.md), [DESIGN-v2.md](DESIGN-v2.md),
> [ROADMAP.md](ROADMAP.md), and [TECH-STACK.md](TECH-STACK.md). This page gets
> you from a clean machine to a running stack with live collection in under ten
> minutes. Every credential below is dev-stack only.

## Prerequisites

| Tool | Version | Why |
|---|---|---|
| Docker (with Compose v2) | any recent | runs SQL Server, MinIO, Airflow |
| .NET SDK | 10.0.x | builds and runs the .NET projects |
| git | any | clone the repo |

Check your tooling:

```sh
docker --version && docker compose version
dotnet --version
```

> **Linux note:** the SQL Server container needs the Docker group. If you get
> permission errors, add your user to the `docker` group and re-login:
> `sudo usermod -aG docker $USER`.

## 1. Clone

```sh
git clone <your-fork-or-origin> aegis
cd aegis
```

## 2. Start the dev stack

```sh
docker compose up -d
```

This stands up:

- **SQL Server 2022** (Developer, Agent enabled) with four sample Agent jobs —
  one succeeds every minute, one fails every minute on a missing vendor file,
  one two-step job fails on its second step, and one purges history older than
  20 minutes to simulate hostile retention.
- **MinIO** (S3-compatible object store) for the landing-zone side.
- **Airflow** with three sample DAGs (succeeding, failing, paused).

The `sqlserver-init` one-shot container creates the sample jobs and is
idempotent — safe to re-run.

Verify everything is healthy:

```sh
docker compose ps
```

Wait for the `sqlserver` and `airflow` healthchecks to report `healthy`
(30–90s on first pull).

## 3. Create the Aegis database

```sh
dotnet run --project src/Aegis.Migrations
```

This creates the `Aegis` database and applies the DbUp migration scripts.
Re-running is safe — DbUp tracks applied scripts.

## 4. Start the API (hosts the collectors)

```sh
dotnet run --project src/Aegis.Api
```

The API process hosts the SQL Agent and Airflow collectors (one box, per
DESIGN-v2). Development settings point at the local stack. You should see
collector log lines within ~30 seconds.

## 5. Generate synthetic carrier feeds (optional)

```sh
dotnet run --project src/Aegis.Generator
```

Drops a day of synthetic carrier feeds (CSV/fixed-width) with injected
violations under `generated_feeds/{yyyyMMdd}/`, plus a `MANIFEST.json` that
says what was injected where. Useful for exercising the contract layer.

## 6. Run the tests

```sh
dotnet test
```

Unit tests run anywhere; the integration tests use Testcontainers and need
Docker running.

## Verify it's working

Collection is visible in the store as soon as the API is up:

```sql
SELECT Id, SourceSystemId, Status, JobCount, UnownedCount, ErrorText
FROM dbo.CatalogSync ORDER BY Id DESC;

SELECT j.Name, r.Status, r.StartedAt, r.EndedAt, r.FingerprintId
FROM dbo.JobRun r JOIN dbo.Job j ON j.Id = r.JobId
ORDER BY r.StartedAt DESC;
```

You should see `CatalogSync` rows with `JobCount > 0` for both
`local-sqlserver` and `local-airflow`, and `JobRun` rows appearing as the
sample jobs fire.

## Web UIs

| Service | URL | Credentials |
|---|---|---|
| Airflow | <http://localhost:8080> | `admin` / `admin` |
| MinIO console | <http://localhost:9001> | `aegis` / `aegis-dev-secret` |

## Tearing down

```sh
docker compose down          # stop containers
docker compose down -v      # also delete the SQL/MinIO data volumes
```

## Troubleshooting

**`docker compose up` fails on SQL Server memory.** The SQL Server container
needs ~2 GB RAM. On Docker Desktop, raise the memory limit in Settings →
Resources. On Linux, ensure the container has enough memory available.

**Airflow is slow to come up.** The container runs `airflow db migrate`,
creates the admin user, and starts the scheduler before the webserver. Give it
up to 90s; `docker compose ps` will show `healthy` when ready.

**Migrations fail with a connection error.** SQL Server may still be starting.
Wait for `docker compose ps` to show `sqlserver` healthy, then re-run step 3.

**Collectors log errors against `local-sqlserver`.** The SQL Agent collector
reads `msdb`; if the sample jobs were not created (e.g. the init container
failed), check `docker compose logs sqlserver-init`.

**Ports already in use.** The stack binds 1433, 8080, 9000, 9001. Stop
whatever is on those ports, or edit the `ports:` mappings in
`docker-compose.yml` (and the connection strings in
`src/Aegis.Api/appsettings.Development.json`).
