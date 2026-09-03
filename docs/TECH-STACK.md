# TECH-STACK — AEGIS technology choices

> Decision record, 2026-09-02, companion to [DESIGN-v2.md](DESIGN-v2.md) and
> [ROADMAP.md](ROADMAP.md). Status: **settled — records the stack named in the
> v1 brainstorm, argues it, and closes the three contested choices.**

## Decision summary

| Layer | Choice | Status |
|---|---|---|
| Runtime | .NET 10 (LTS), C# | Locked — constrained |
| UI | Blazor Server + SignalR (API as separate layer) | Locked — v1 design |
| Database | SQL Server 2022+ | Locked — v1 design |
| Workers | `BackgroundService` + `PeriodicTimer` (no job-queue framework) | Locked — this session |
| Migrations | DbUp (plain SQL scripts) | Decided — this session |
| Data access | Hybrid: EF Core (app state) + Dapper (msdb polling, hot appends) | Decided — this session |
| Validator | Pure .NET (CsvHelper + hand-rolled checks), engine behind an interface | Decided — this session |
| Feed formats (first phase) | CSV + fixed-width first, then JSON | Decided — this session |
| Feed formats (later) | X12 EDI via pluggable parser interface | Deferred with interface |
| Contracts | YAML (YamlDotNet) | Locked — this session |
| Identity | ULIDs | Locked — v2 data model |
| Testing | xUnit + Testcontainers (real SQL/MinIO/Airflow containers) | Locked — this session |
| CI | GitHub Actions | Locked — this session |
| Local S3 | MinIO | Locked — v1 design |

## Constrained choices (forced — rationale recorded, not debated)

### Runtime: .NET 10, C#

The Windows/MSSQL shops this targets run C# throughout, so it is the language
with the most institutional experience. A compiled language with Microsoft's enterprise
support is the right posture for a tool that must sail through SOX change
management, and the same toolchain covers collectors, API, and UI — with
Blazor available when this grows into a full server/web platform.

**One consequence accepted:** VisualCron's client API is a .NET API that may
target .NET Framework. The adapter therefore lives behind an interface
(DESIGN-v2, risk #5) and the simulator doubles keep the core testable without
the commercial dependency.

### Workers: plain `BackgroundService` + `PeriodicTimer`

No Quartz, no Hangfire, no message broker for the pollers. The collectors are
stateless pollers on one box (DESIGN-v2 architecture section); embedding a job
scheduler inside a scheduler-monitoring tool adds failure modes and irony in
equal measure. The one event-driven path (S3 → SQS → validation worker) is a
plain queue consumer, which `BackgroundService` handles fine. If horizontal
scale ever becomes real (DESIGN-v2 says it won't soon), swapping the timer for
a broker consumer is contained behind the same collector interface.

### Testing: xUnit + Testcontainers

The parity argument from DESIGN-v2 applies to tests too: the msdb collector is
tested against a real SQL Server container, the Airflow collector against the
real docker-compose stack, the validator against real MinIO objects. No
in-memory doubles for the integration seams — that's where the spike risk
lives. Unit tests cover parsers, fingerprints, and the packed-datetime traps
(3.1's DoD).

## Decided choices (the three contested ones)

### Validator: pure .NET, engine behind an interface

**Decision:** CsvHelper for delimited files, hand-rolled schema/nullability
checks, all behind a `IValidatorEngine`-style interface.

**Reasoning:** .NET everywhere keeps one toolchain, one deploy, one failure
domain. The check taxonomy is small (filemask, arrival, schema diff,
nullability — plus JSON shape, below); a 500-expectation framework is overkill
for v0 and adds an ops burden the "monitor the monitor" principle can't afford
on a one-box tool.

**Hedge:** the interface keeps the Great Expectations/Python embed alive as a
*later option* if the taxonomy outgrows hand-rolled rules — noted in
DESIGN-v2 risk #7 (GE "could be embedded/borrowed from"), but not on the
current roadmap.

### Data access: hybrid EF Core + Dapper, DbUp migrations

**Decision:**
- **EF Core** for app/API state (jobs, runs, catalog, alerts) — the CRUD-shaped
  read-mostly surface ORMs are for.
- **Dapper/raw SQL** for `msdb` polling (EF-mapping system tables is a fight
  nobody wins) and the hot append path (`JobRun`, `Arrival`, `Validation` —
  where the 15M rows/year land).
- **DbUp** for migrations: plain SQL scripts, ordered, legible to DBAs, and a
  clean SOX story ("here are the scripts that changed the schema, with dates")
  — no code-first magic in the audit trail.

### Feed formats: CSV + fixed-width first, then JSON — both in the first phase

**Decision:** the v0 validator and M2 demo ship with CSV + fixed-width (the
overwhelming shape of carrier landing zones). JSON support follows as a
fast-follow task before the demos (M4), via System.Text.Json structural
schema checks — same contract spec, same metadata-only posture.

**Deferred with interface, not dropped:** X12 EDI (834/837 — real insurance
traffic) validates on segments/elements, not columns, which is a different
check model *and* a different contract-spec vocabulary. The parser interface
accommodates it; the real thing arrives later when actual carrier
formats are visible. Designing the contract spec for X12 now, against
synthetic data, would be speculation.

## Cross-cutting conventions

- **Contracts are YAML** (YamlDotNet): hand-editable by the teams who own
  feeds, diffable in git, and matches "versioned config in git, synced to DB."
- **ULIDs** for canonical identity (sortable, collision-free without
  coordination — `Job.Id` in the v2 data model).
- **CI is GitHub Actions**: build + `dotnet test` with Testcontainers on every
  push; roadmap task 1.1's DoD.
- **MinIO** for local S3 parity (already locked in the local test stack).

## Consequences for the roadmap

- **Epic 2 gains a task:** JSON feed support after the CSV/fixed-width
  validator lands (see ROADMAP 2.6). Estimate impact: +1–2 e.u.
- **Task 1.1 scaffold** now includes: solution layout with API/UI/collector/
  validator projects, DbUp migration project, Testcontainers wiring, and the
  `docker compose up` stack. The stack decisions here are its inputs.
- **No other roadmap changes** — everything else above was already implicit
  in DESIGN-v2's architecture section; this document just makes it explicit
  and argued.