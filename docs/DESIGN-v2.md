# DESIGN v2 — AEGIS: Automated ETL Governance & Inspection System

> Design doc v2, 2026-09-02, distilled from the v1 brainstorm (2026-07-13) and a
> design-review session. Status: **v2 draft — sequencing revised, data model
> added, collector spike specs added. Open questions at the bottom gate v3.**
>
> The v1 record (2026-07-13) was folded into this document.

## What changed in v2 (review session outcomes)

1. **Sequencing revised — the pillars stand, the build order doesn't.** v1 built
   catalog → signal → contracts and simultaneously called contracts "biggest
   ROI." v2 resolves the contradiction: contracts are nearly self-contained
   (a feed's owner is declared in its contract), need no cross-team credentials,
   and power the classifier's best enrichment. Build is now **two parallel
   tracks over a shared substrate** (below).
2. **Data model added.** v1 was a strategy doc; principle 4 (stable job
   identity) is a schema decision and now has one. Eight core tables, three
   locked decisions (below).
3. **Collector spike specs added** — the known traps per source, including the
   `sysjobhistory` retention blind spot and the alive-but-zero-rows failure
   mode that heartbeats alone won't catch.
4. **Open questions formalized** — the answers gate v3.

## Problem statement (unchanged from v1)

A shop of this kind runs on the order of a thousand or more scheduled jobs
across three schedulers — VisualCron, SQL Server Agent, and AWS MWAA (Airflow) —
owned by several teams. Consequences, as typically observed:

- **Failure emails are a firehose nobody can drink from.** Alerts are sent, buried,
  and unread; the effective detection mechanism is a business user, customer, or
  developer *noticing*. Mean-time-to-detect is measured in days and is
  customer-visible.
- **Failures don't propagate.** When an upstream job fails, downstream jobs run
  business-as-usual on stale/partial data and deliver it with a green checkmark.
  The silent bad-data propagation is worse than the failure itself.
- **Developers spend 20–30% of their time finding and diagnosing failures** — most
  of which are data, file, or schedule issues (missing/late customer files, filemask
  changes, schema drift, NULLs in declared-NOT-NULL columns), not code bugs.
- **Ownership/documentation is scattered** across Confluence, Jira, and the job
  descriptions of three schedulers (ticket #s, team names, tags — three formats),
  and rots because it's manual.

Feeds arrive from dozens of carriers (BCBS, Aetna, Humana, Cigna, …); a handful
account for most of the volume. File landing zones: legacy SMB shares + SFTPs,
**actively migrating to S3** (Prod / SOX / DS-Prod + DEV + UAT environments) for
centralization and SOX.

## The three capability pillars (unchanged; see sequencing for build order)

### 1. Control plane — read-only inventory + run states + dependency graph
Collectors poll each scheduler and normalize into one model:
- **SQL Agent:** direct queries against `msdb` (`sysjobs`, `sysjobhistory`, …).
- **MWAA:** standard Airflow REST API (DAGs, DAG runs, task instances).
- **VisualCron:** its native **.NET client API** — home-stack advantage.

**Derived catalog (harvest, don't ask):** ownership, ticket #s, teams, and tags are
parsed out of the job descriptions the teams already maintain in each scheduler.
The catalog regenerates on every sync, so it cannot rot; jobs with no parseable
owner surface as a visible, delegable gap list ("N of M jobs unowned").
No new documentation habit is asked of anyone — fixes happen by editing job
descriptions in the tools people already use.

**Dependency graph:** manually declared for critical chains at first (upstream →
downstream edges); inference from table/file reads-writes is a later enhancement.
One failure lights up every downstream job now running on stale inputs.

### 2. Signal layer — turn the firehose into triage
- **Dedup + grouping:** one root failure ≠ 40 emails; group by error fingerprint
  (strip timestamps/IDs/values, hash the shape — Sentry-style).
- **Ownership routing:** the right failure to the right owner via the derived
  catalog, not everything to everyone.
- **Failure classification** — deterministic rules over a small, known taxonomy:
  file missing / file late / filemask drift / schema drift / data violation /
  upstream failed / schedule collision / connection-credential / code bug.
  Rules auto-enrich using contract-layer data: "file not found" → check the
  arrival log: did the vendor drop anything? when? (This is why the classifier
  ships *after* contracts — see sequencing.)
- Trend/rollup views: "87% of this month's failures were missing/late vendor
  files — top 5 offending feeds" = root-cause ammunition, not just triage.

### 3. Contract layer — validate feeds BEFORE pipelines run (biggest ROI)
Per-feed **data contracts** (small config files): expected filemask, arrival
window/SLA, schema (columns, types, nullability), and **owner**. A
**landing-zone validator** checks every arriving file against its contract
*before* any pipeline touches it:
- S3-native and event-driven (S3 event notifications → validation worker); legacy
  SMB/SFTP zones get second-class polling watchers.
- Missing/late detection fires **proactively** when the window closes empty.
- Schema-drift reports are precise diffs ("column `PolicyDate` renamed; 340 NULLs
  in `CarrierId` declared NOT NULL — file quarantined, pipeline never ran") —
  precise enough to send to the carrier automatically.
- Pareto scope: contracts for the major carriers' feeds first covers most of
  the failure volume. The unit is the *feed* (each carrier sends several).
- **Synergy:** every feed that migrates to S3 gains validation for free → the tool
  accelerates the already-blessed S3/SOX centralization instead of competing.

### Phase 4 (later, data-driven): consolidation
With a real inventory, flakiness data, and a dependency graph in hand, the
migrate-and-standardize conversation (e.g. VisualCron → Airflow) argues itself.
Consolidation is a conclusion the data hands you, not a starting premise.
Also cheap and parallel: a pipeline scaffold (`dotnet new` / cookiecutter template
+ one blessed CI/CD path) attacks "simplify development/deployment" directly.

## Build sequencing — REVISED in v2: two tracks over a shared substrate

v1 built the pillars strictly in order and called the last one "biggest ROI."
The review surfaced that the assumed dependency — catalog before contracts — is
mostly false: a feed's owner is naturally *declared* in its contract, so the
contract layer is nearly self-contained. What actually depends on what:

- The classifier (pillar 2) is **downstream of contracts** — its best
  auto-enrichment ("file not found → what did the landing zone see?") is powered
  by the contract layer's arrival log. Contracts before classifier means the
  classifier gets its data source for free.
- The catalog is what needs **cross-team credentials** (msdb read, MWAA API,
  VisualCron) — the top political risk in v1's own ranking. Contracts need only
  landing-zone read access, which the home team plausibly already has.
- Highest ROI + lowest access friction argues contracts forward.

The revised plan — two parallel tracks over a shared substrate:

| Phase | Track A (visibility) | Track B (enforcement) |
|---|---|---|
| **v0** | — substrate: store, canonical identity, heartbeat/staleness monitoring | — contract validator on S3 events + synthetic feed generator |
| **v1** | msdb + Airflow collectors, catalog, ownership routing | — proactive missing/late alerts, schema-drift diffs |
| **v2** | VisualCron collector, dependency graph, dedup/classifier | — carrier-facing violation reports |

**Reading the table:**
- Track A produces the visibility demo (cross-scheduler green/red,
  stale-data cone). Track B produces the demo that pays for itself
  (customer-visible risk, proactive missing/late).
- The two demos serve different audiences — lead with whichever fits the room.
  That flexibility is the point of two tracks; it is not a compromise.
- The synthetic carrier-feed generator (v0, Track B) is the test suite for both
  tracks and both demos.

## Design principles (unchanged from v1; all six remain locked)

1. **Read-only first.** An observer that never touches the controlled data path is
   risk-free to pilot, threatens no team's territory, and sails through SOX
   change-management scrutiny.
2. **Metadata-only, always (PHI/HIPAA posture).** Files are inspected *in place*;
   only metadata leaves — filenames, timestamps, schema diffs, column names, NULL
   *counts*, row counts. Never row contents: not in the DB, not in reports, not in
   alerts. Stated architectural principle from day one; a retrofit is a rewrite.
3. **Harvest, don't ask.** No manually-curated catalog; derive everything from
   sources that already exist and regenerate continuously.
4. **Stable job identity across migrations.** A canonical internal job ID with
   *source bindings* (scheduler + native ID). When a job migrates VisualCron →
   Airflow, re-bind the source; history, ownership, and dependencies survive under
   the canonical ID. (The schema for this is below — it was always a data-model
   decision pretending to be a principle.)
5. **Monitor the monitor.** At this scale the real risk isn't performance, it's a
   collector silently dying = blind spot. Collectors emit heartbeats; every
   dashboard shows data staleness ("as of 09:42:13"); a stale collector is itself
   a routed alert. v2 extends this: a collector that is *alive* but getting
   *zero rows* is also a blind spot (see collector specs).
6. **Not an orchestrator. Ever.** No job authoring, no scheduling, no run
   triggering (a later "re-run" button would be the sole, carefully-gated
   exception). Scope discipline is what keeps this from becoming the fourth
   system to consolidate.

## Data model — NEW in v2

Eight core tables. Entity sketch, not final DDL — names are settled, column
details are provisional.

```
SourceSystem        (Id, Type, Name, Config, LastHeartbeat)
Job                 (Id ULID, Name, IsActive, FirstSeen, LastSeen)
JobSourceBinding    (JobId, SourceSystemId, NativeId, NativeName,
                     BoundAt, UnboundAt)          ← time-bounded; migration = rebind
JobRun              (JobId, SourceSystemId, NativeRunId, StartedAt, EndedAt,
                     Status, ErrorText, FingerprintId)   ← append-only
CatalogSync         (SourceSystemId, StartedAt, JobCount, UnownedCount, Status)
JobOwnership        (JobId, TeamId, RawEvidence, ParsedFrom, SyncId)  ← per-sync snapshot
DependencyEdge      (UpstreamJobId, DownstreamJobId, DeclaredBy, DeclaredAt)  ← audited
Feed                (Id, Name, TeamId, LandingPrefix)
ContractVersion     (FeedId, Version, SpecHash, Spec JSON, EffectiveFrom)
Arrival             (FeedId, Key, SizeBytes, ArrivedAt, Disposition)
Validation          (ContractVersionId, ArrivalId, Result, Findings JSON, RanAt)
Alert               (Type, DedupKey, Status, RoutedTo, FirstSeen, Occurrences,
                     AckedBy, AckedAt)             ← mutations audited for SOX
```

### Three locked decisions

1. **Identity is explicit, never auto-merged.** When a job migrates VisualCron →
   Airflow, someone inserts a new `JobSourceBinding` row. No fuzzy-matching of
   native IDs across systems — an auto-merge that is wrong silently corrupts
   history, and migrations are rare enough to afford deliberateness. Migration is
   a deliberate, audited operation (`BoundAt`/`UnboundAt` make the timeline
   reconstructable).
2. **Every `Validation` row pins the `ContractVersion` that judged the file.**
   Contracts live in git, get edited, and sync to the DB — meaning contracts
   *change over time*. Pinning the version is what makes "your file failed on
   Tuesday" defensible when the contract changed Wednesday. This is the SOX
   story in one foreign key.
3. **Ownership is sync-snapshotted, not mutated.** `JobOwnership` rows are
   per-`CatalogSync`, so ownership history and the unowned-gap list both fall out
   of queries rather than SCD2 machinery. At tens of millions of small metadata
   rows a year, don't build what indexes do for free. `RawEvidence` keeps the
   parsed source text so parsing changes can be replayed without re-polling
   the source.

(The sketch above is twelve tables, not eight, if you count the contract-layer
four — Feed, ContractVersion, Arrival, Validation — which belong to Track B.
The "eight core" framing from the review session referred to the control-plane
half. Both halves share only `Job`/`Feed` identity and `Alert` routing.)

## Architecture & scale (unchanged from v1, one open question added)

A couple of thousand jobs × tens of runs a day ≈ **tens of millions of small
metadata rows a year** — trivial for SQL Server with sane indexes and
date partitioning. Design for correctness and reliability; the horsepower is free.

- **Collectors:** stateless .NET background workers (one per source system),
  polling on 30–60s intervals; S3 validation is event-driven (S3 → SQS → worker).
  Horizontal scale = run more workers; at this volume one box handles everything,
  but statelessness keeps the option open.
- **Store:** SQL Server (home turf for the Windows/MSSQL shops this targets).
  Append-only run-history + current-state tables; contracts as versioned
  config (in git, synced to DB).
- **API:** stateless ASP.NET Core minimal API → scales out behind a load balancer
  if ever needed (it won't be soon).
- **UI:** Blazor Server first (fastest dev, home stack) with **SignalR push** so
  dashboards update live without refresh — multi-user comes free; dozens of
  concurrent internal users is well within one instance. If concurrency ever
  grows real, the escape hatch is Blazor WASM against the same API — which is why
  the API stays a separate layer from day one.
- **Multi-user semantics:** the tool is read-mostly; the few writes (acknowledge
  alert, declare dependency, edit contract) are low-contention and auditable
  (who/when on every mutation — SOX-friendly by construction).
- **Open question (collector topology):** how many SQL Server instances are in
  scope? One collector process polling N instances vs one worker per instance
  changes the collector-hosting story and the credential ask. Answer gates v3.

## Collector integration specs — NEW in v2

Per-source spike notes, in build order. These are the feasibility questions a
week of evenings on the local test stack should answer.

### SQL Agent (spike first — most transparent, validates the normal form)

- **`sysjobhistory` retention is the trap.** Default retention is tiny (~1,000
  rows total, ~100 per job); at 30–50 runs/day history purges within days —
  a silent blind spot. Mitigation is *not* asking every instance to re-tune
  retention (config changes on production boxes = access politics). Mitigation
  is **watermark + gap detection**: `instance_id` is monotonic, poll above the
  watermark, and alert when `min(instance_id) > watermark + 1` — a purge gap
  means rows were missed. This is principle 5 applied to the *source*, not just
  to AEGIS.
- **In-flight runs are not in `sysjobhistory`.** Poll `sysjobactivity` for live
  state, `sysjobhistory` for outcomes, reconcile the two.
- **Integer-packed datetimes** (`run_date`, `run_time`, `run_duration` as
  HHMMSS) are the classic normalization trap. Annoying, not hard; unit-test the
  parser, especially durations > 24h and midnight rollovers.

### Airflow / MWAA (second — easiest, two gotchas)

- Use the REST API's `updated_after` filters to poll incrementally, never full
  listings — MWAA rate-limits, and full DAG listings at thousand-DAG scale will
  trip the throttle.
- **Record Airflow's own state-transition timestamps, not collector arrival
  time.** Airflow scheduler lag makes runs "appear" late; if MTTD measures
  collector arrival, it silently includes Airflow's lag. The metric must be
  attributable to be actionable.

### VisualCron (last, in the trial VM — risk #4 from v1)

- **The known hazard:** the .NET client API is version-coupled to the server —
  client DLLs must match the server version. Confirm whether remote connections
  and the auth model (VC-local users vs AD) work in the 45-day trial **before
  writing adapter code**.
- **Timebox:** if the trial VM doesn't yield a working remote connection in a
  few evenings, fall back to interface + simulator. Do not let the third
  adapter stall the first two.

### Cross-cutting: the failure mode nobody designs for

A collector that is **alive but getting zero rows** — wrong `msdb` permissions
return empty result sets, not errors; a filtered MWAA view returns an empty DAG
list with HTTP 200. Heartbeats won't catch it. Therefore:

- Every sync records its row counts (`CatalogSync.JobCount`).
- A count that drops to zero (or drops sharply vs. the trailing baseline) is an
  alert, not a success. "Collector healthy, 0 jobs" is a contradiction the
  system must treat as such.

## Business case (unchanged from v1)

1. **Reclaimed engineering time:** 20–30% of developer time goes to
   finding/diagnosing failures. At N developers that's N×0.25 FTEs of salary burned
   on triage annually; target cutting it in half. Zero license cost.
2. **SOX evidence, automated:** complete job inventory with owners, immutable run
   history, failure detection + routing records, contract validation logs — the
   audit trail generates itself instead of quarterly screenshot archaeology.
   Compliance cost is a line item leadership already resents.
3. **Customer-facing risk:** today's effective failure detection is "a customer
   notices." Proactive feed validation + real MTTD is a reputation argument.
4. **Rides funded strategy:** accelerates the in-flight S3/SOX centralization.

## Risks, ranked (v1, updated)

1. **Collector reliability / silent blind spots** — mitigated by principle 5,
   extended in v2 with watermark-gap detection and zero-row detection.
2. **Access politics** (credentials for schedulers other teams own) — mitigated by
   read-only posture + pilot-then-pull rollout + Track B needing no cross-team
   access.
3. **`sysjobhistory` retention config on production instances** (new) — cannot be
   assumed or changed; the watermark-gap design makes the system correct under
   hostile retention, and a detected purge gap surfaces as data, which itself
   argues for adopting AEGIS.
4. **Description-parsing brittleness** (three ad-hoc metadata formats) — tolerant
   regex + a "unparseable" bucket that doubles as the cleanup list;
   `RawEvidence` retention (data model decision 3) makes parser changes
   replayable.
5. **VisualCron API quirks/versioning** — spike early with the timebox and
   simulator fallback defined above; it's the least-documented of the three
   integrations but no longer on the critical path for v1.
6. **Scope creep toward orchestrator** — principle 6; say no early and often.
7. **"Why not buy/adopt X?"** — DataHub/OpenMetadata are k8s-scale platforms that
   don't speak VisualCron or SQL Agent; Great Expectations overlaps the validator
   internals only (and could be embedded/borrowed from). Nothing off the shelf
   does cross-scheduler visibility for a Windows/MSSQL/AWS hybrid shop.

## Licensing and data

The core of AEGIS is open source (OSS licence to be chosen). All sample data,
job descriptions, feeds and carrier files in this repo are synthetic; no real
schema, credential or job description from any organisation is included.

## Name — DECIDED 2026-07-13: AEGIS (unchanged)

**A**utomated **E**TL **G**overnance & **I**nspection **S**ystem. The aegis is
the protective shield of Zeus/Athena — a tool that *protects* pipelines, at an
insurance company; "Governance" nods at SOX.

## Open questions — answers gate v3

1. **How many SQL Server instances are in scope?** One collector polling N
   instances vs one worker per instance changes the hosting story.

## Next steps (revised)

1. Stand up the local test stack: Airflow docker-compose + SQL Server Dev
   (Agent on, hostile retention) in Docker + MinIO; spike the msdb and
   Airflow-REST collectors with watermark-gap and zero-row detection built in
   from the first commit. A week of evenings answers most feasibility
   questions.
2. Build the synthetic carrier-feed generator early (test suite AND both demos,
   both tracks).
3. Revise this doc to v3 once the open questions are answered; then the data
   model gets promoted from entity sketch to real DDL.