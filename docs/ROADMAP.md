# ROADMAP — AEGIS: start-to-finish work breakdown

> Planning doc, 2026-09-02, companion to [DESIGN-v2.md](DESIGN-v2.md). Status:
> **draft — scopes and tasks defined, sizes estimated, critical path mapped.**
> Sizes are estimates, revised as work lands.

## Scope & assumptions

- **"Finish" = the phase ends with both demos working on the test stack.**
- **Effort unit = one evening-unit (e.u.)** ≈ one focused 2–3 hour evening.
  At a realistic 4–6 e.u./week, the estimates below are a ~2.5–3.5 month
  calendar runway. Sustaining that pace is itself a schedule risk (see bottom).

## Milestones

| ID | Milestone | Proof |
|---|---|---|
| **M1** | Substrate green | ✅ **DONE** — `docker compose up` + migrations + heartbeats + generator dropping files |
| **M2** | Track B demo | Late file fires proactive alert before window close; drift diff report |
| **M3** | Track A demo | Two real schedulers + VC simulator, green/red, stale-data cone |
| **M4** | Demos assembled | Both demos compose-up-able |

## Epic 1 — Substrate (v0; both tracks build on it)

| Task | Size | Depends | DoD |
|---|---|---|---|
| 1.1 Solution scaffold + CI + dev stack | 2–3 | — | ✅ **DONE (2026-09-02)** — solution scaffolded (Api/Web/Collectors/Validator/Migrations + unit/integration tests), DbUp pipeline with baseline migration, `docker-compose.yml` (SQL Server Dev w/ Agent, MinIO, Airflow), GitHub Actions CI. `dotnet build` + `dotnet test` green locally. Remaining: verify `docker compose up` + Testcontainers once Docker group membership is active |
| 1.2 Core DDL (promote DESIGN-v2 entity sketch) | 2–3 | 1.1 | ✅ **DONE (2026-09-03)** — migrations for all tables apply cleanly; round-trip tests; append-only JobRun enforced (closed runs never rewritten) |
| 1.3 Monitor-the-monitor substrate | 2 | 1.2 | ✅ **DONE (2026-09-03)** — heartbeats + `CatalogSync` stats; zero-row and sharp-drop alerts; `StaleCollectorSweepService` raises `CollectorStale` when a source's heartbeat goes quiet and resolves it when it returns |
| 1.4 Synthetic feed generator | 3–4 | 1.1 | ✅ **DONE (2026-09-03)** — `Aegis.Generator` CLI produces synthetic carrier feeds (CSV/fixed-width) with injected violations: late/missing files, mask drift, schema drift (renamed/added/removed columns), NULLs in NOT-NULL columns, data type mismatches, truncation, duplicates. Manifest JSON written. Triples as test suite + both demos. |

## Epic 2 — Contract layer (Track B v0→v1) → **M2**

| Task | Size | Depends | DoD |
|---|---|---|---|
| 2.1 Contract spec + versioning | 2 | 1.2 | ✅ **DONE (2026-09-03)** — YAML contract schema (mask, arrival window, schema, nullability, owner); `ContractParser` + `ContractStore` git→DB sync; `SpecHash` + `EffectiveFrom`; unchanged specs don't create new versions; sample contracts under `contracts/` |
| 2.2 Arrival capture | 1–2 | 2.1 | MinIO event → Arrival rows; disposition tracked |
| 2.3 Validator worker | 3–4 | 2.1, 2.2 | In-place inspection, metadata-only; mask/schema/NULL checks; quarantine disposition; every Validation row pins its ContractVersionId |
| 2.4 Proactive missing/late | 2 | 2.3 | Window-close sweep fires alert before pipeline would have run |
| 2.5 Alert core + routing + acknowledge | 2 | 1.3 | DedupKey, route to contract-declared owner, acknowledge with who/when audit |
| 2.6 JSON feed support | 1–2 | 2.3 | Same contract spec + validator via System.Text.Json structural checks; decided in TECH-STACK (CSV/fixed-width first, JSON fast-follow before the demos) |

## Epic 3 — Collectors (Track A v1, part 1)

| Task | Size | Depends | DoD |
|---|---|---|---|
| 3.1 SQL Agent collector | 3–4 | 1.2, 1.3 | Watermark-gap detection (alert on `instance_id` gap = purge); sysjobactivity/sysjobhistory reconciliation; packed-datetime parser unit-tested (durations >24h, midnight); auto-bind on first sight |
| 3.2 Airflow collector | 2–3 | 1.2, 1.3 | Incremental REST polling (`updated_after`); records Airflow's own state-transition timestamps, never collector arrival; auth is adapter config |
| 3.3 Identity/binding ops | 1–2 | 1.2 | Audited rebind operation (CLI or admin endpoint) — migration is deliberate, never auto-merged |
| 3.4 Derived catalog | 3 | 3.1, 3.2 | Per-scheduler parser profiles against **synthetic** sample descriptions; per-sync ownership snapshots (RawEvidence kept); unowned-gap list queryable |

## Epic 4 — Visibility surface (Track A v1, part 2) → **M3**

| Task | Size | Depends | DoD |
|---|---|---|---|
| 4.1 API surface | 2 | 3.1–3.4 | Inventory, timeline, run detail, staleness endpoints; API separate from UI from day one (WASM escape hatch) |
| 4.2 Blazor dashboard | 4–5 | 4.1 | One screen green/red, SignalR live, "as of" staleness, failure detail enriched from contract-layer arrival log. **Scope discipline risk — see below** |
| 4.3 Minimal dependency edges + cone | 2–3 | 4.1 | Declare edge (API/UI), cone query, failed upstream lights up downstream. *Amendment to DESIGN-v2 sequencing: the v2 table put the dependency graph in phase v2, but the winning demo needs the cone — minimal edge declaration moves up; full classifier + inference stay post-demo* |

## Epic 5 — VisualCron (parallel, timeboxed)

| Task | Size | Depends | DoD |
|---|---|---|---|
| 5.1 Trial VM spike | 3 (**hard timebox**) | — | Remote connection + auth model (VC-local vs AD) + client/server version coupling assessed; go/no-go recorded |
| 5.2 VC simulator (only if 5.1 no-go) | 1–2 | 5.1 | Interface double; demo shows "three" schedulers; real adapter deferred to later work |

## Epic 6 — Demos, publication → **M4**

| Task | Size | Depends | DoD |
|---|---|---|---|
| 6.1 Compose-up demo assembly | 1–2 | M2, M3 | `docker compose up` = the complete demo; zero manual steps |
| 6.3 Publication framing | 2 | (independent, any time) | ✅ **DONE (2026-09-03)** — DESIGN/README/ROADMAP rewritten to the generic framing; the docs ship as-is |

## Critical path & parallelism

```
1.1 ── 1.2 ──┬── 1.3 ── 2.1 ── 2.2 ── 2.3 ── 2.4/2.5 ───── M2 ─┐
              │                                                 ├─ 6.1 ─ M4
              └── 1.4 ── 3.1 ── 3.2/3.3 ── 3.4 ── 4.1 ── 4.2 ─ 4.3 ─ M3 ─┘
     5.1 (parallel, timeboxed)                                   6.3 (parallel)
```

- **M2 path is shorter** → Track B lands its demo first, matching the
  sequencing argument (highest ROI, lowest access friction). Track A continues
  while Track B's demo does early socializing.
- 1.4 (generator) sits deliberately before both tracks' test work.
- Epic 2 and Epic 3 interleave freely on separate evenings; 3.1 before 3.2
  (harder source first validates the normal form).

## Estimate summary

| Epic | e.u. |
|---|---|
| 1 Substrate | 9–12 |
| 2 Contract layer | 11–13 |
| 3 Collectors | 9–12 |
| 4 Visibility | 8–10 |
| 5 VisualCron | 3–5 |
| 6 Demos | 4–6 |
| **Total** | **43–56 e.u.** |

At 4–6 e.u./week ≈ **2.5–3.5 months calendar**.

## Later (listed, not scheduled)

real credentials against real schedulers · fleet collector topology ·
real VisualCron adapter (if 5.1 no-go) · production hardening · Great
Expectations evaluation · full classifier + graph inference.

## Schedule risks

1. **Pace decay** — evenings erode; the two-track structure is the hedge (each
   milestone demo is independently shippable to its audience).
2. **UI scope creep** (4.2) — the dashboard is the most elastic task on the
   board; DoD is deliberately "one screen." Everything beyond green/red, detail,
   and cone is post-demo.
3. **Generator underestimation** (1.4) — it's two artifacts in one (test
   suite, public demo). Better to discover overrun here than in
   2.3, which is why it's scheduled first.
4. **VC timebox discipline** (5.1) — the doc says a few evenings; the roadmap
   makes it a hard 3. A stalled third adapter must not stall the first two.