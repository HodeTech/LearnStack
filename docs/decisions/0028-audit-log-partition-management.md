# ADR-0028: `audit_log` Partition Management — Hangfire Recurring Job

## Status

Accepted — **amended 2026-08-08** (see [Amendments](#amendments))

**Date:** 2026-05-20
**Deciders:** @platform

## Decision Drivers

- **`audit_log` is partitioned by month from Day 1.**
  [ADR-0016 § audit_log table](0016-audit-log-subsystem.md) commits the table
  to monthly partitions for cheap drop-based retention. Partition lifecycle
  (creating next month's partition, dropping expired ones) is a *recurring*
  operation, not a one-time migration, so it has to be owned by code that runs
  on a schedule.
- **Three deployment modes, one binary.** [ADR-0020](0020-triple-deployment-hybrid-license.md)
  commits LearnStack to SaaS / Dedicated / SelfHosted from a single codebase.
  SelfHostedAirGapped specifically prohibits any runtime dependency the
  customer cannot ship inside their air gap; an extra PostgreSQL extension
  pushes the customer's DBA workload onto the platform's deployment story.
- **Partition lifecycle is application-domain logic, not DBA tooling.** The
  rules ("create next month's partition by the 25th of this month", "drop
  partitions older than the longest retention window across all tenants",
  "honour the per-tenant `AuditConfig` retention overrides") live in the
  audit subsystem's design ([architecture/31-audit-subsystem.md
  § Retention](../architecture/31-audit-subsystem.md)). Putting them in a
  database extension means moving rules out of the codebase into per-environment
  config, which conflicts with the standards-corpus single-source-of-truth
  posture.
- **A Hangfire job runner already exists.** The audit retention purge is already
  committed to Hangfire ([Standards 18](../standards/18-audit-coverage.md)
  § Retention; [architecture/31-audit-subsystem.md
  § 8 Retention](../architecture/31-audit-subsystem.md)). Partition
  management can ride the same `LearnStackJob`-shaped surface instead of
  introducing a second mechanism.
- **PostgreSQL 18** ([ADR-0031](0031-postgresql-major-version.md)) ships with
  native declarative partitioning improvements; `CREATE TABLE ... PARTITION OF`
  + `DETACH PARTITION CONCURRENTLY` cover the operations a partition manager
  needs, without requiring an extension.
- **`pg_partman` is genuinely good at this — but at the cost of an extension
  binary** that has to be installed in every environment, version-matched to
  PostgreSQL major, and validated for the air-gapped story.

## Considered Options

1. **Hangfire recurring job (`learnstack:audit:partition-management`)**
   (chosen). A C# job in `LearnStack.Infrastructure.Audit`, scheduled via
   Hangfire's `RecurringJob.AddOrUpdate(...)`. Runs daily; creates next month's
   partition if missing; drops partitions whose end timestamp + the platform's
   max retention window < `now`. Per-tenant retention is enforced by a separate
   row-level purge job (`learnstack:audit:retention-purge`) operating *inside*
   the still-attached partitions.
2. **`pg_partman` PostgreSQL extension** (rejected). The de facto standard for
   declarative time-partitioned tables; battle-tested at scale. Configured via
   `partman.create_parent(...)` and a background `partman.run_maintenance()`
   call (either from pg_cron or a Hangfire-triggered SQL call). Drops the
   custom logic to a config-table lookup.
3. **Manual SQL via EF Core migrations** (rejected outright). Partition lifecycle
   is recurring — every month a new migration would have to land. This conflicts
   with the "migrations append-only after merge" rule
   ([Standards 05](../standards/05-database.md)) and makes the Self-Hosted
   upgrade story brittle.

## Decision

LearnStack manages `audit_log` partitions via a **Hangfire recurring job**
named `learnstack:audit:partition-management`, living in
`LearnStack.Infrastructure.Audit`.

- **Schedule:** daily at 02:15 UTC (after the daily retention purge slot but
  before the bulk of the next day's audit traffic ramps up).
- **Create-ahead:** the job ensures the **current** month's partition exists
  (idempotent), the **next** month's partition exists, and the
  **month-after-next** partition exists. The two-month-ahead horizon protects
  against a failed run on the 25th of a month not blocking the 1st of the
  next month's writes.
- **Drop policy:** the job drops a partition only when its end timestamp +
  the **platform's maximum retention window** (10 years for safety —
  longer than every per-class retention in
  [Standards 18 § Retention](../standards/18-audit-coverage.md)) is in the
  past. Per-tenant retention enforcement is **not** done by partition drop;
  it is done by row-level delete in the
  `learnstack:audit:retention-purge` job operating inside the still-attached
  partitions.
- **Partition shape:** `PARTITION BY RANGE (timestamp)` with monthly
  partitions `audit_log_YYYY_MM` covering `[YYYY-MM-01, (YYYY-MM+1)-01)`.
  This matches the shape defined in
  [ADR-0016 § audit_log table](0016-audit-log-subsystem.md) and
  [architecture/31](../architecture/31-audit-subsystem.md); this ADR owns
  *lifecycle*, not the table layout.
- **Failure mode:** the job is idempotent — re-running creates no duplicate
  partitions and drops no live data. If a job run fails, Hangfire retries
  the next day; if four consecutive runs fail the job emits a
  `learnstack.audit.partition.management.failed` event for operator alerting.
- **No `pg_partman` runtime dependency.** No environment ever needs to
  install the extension.

## Context

### Why Hangfire over `pg_partman`

The choice is genuinely close on technical merit; `pg_partman` is more polished
at the SQL surface. Three things tipped the balance toward Hangfire:

- **Air-gapped deployment story.** SelfHostedAirGapped is a real first-class
  mode in [ADR-0020](0020-triple-deployment-hybrid-license.md). Adding a
  PostgreSQL extension means the air-gapped customer's release bundle has to
  ship the extension binaries matched to their PG major, with their
  validation work, with their patching cadence on top. Hangfire is C# code
  that ships in the LearnStack binary itself.
- **Single source of truth for retention rules.** Standards 18 already owns
  the retention class table (7y / 2y / per-tenant override). Putting partition
  rules in `pg_partman` config means two sources for "how long does this row
  live": one in code (Standards 18), one in `partman.part_config` (the
  extension's config table). Drift is inevitable; choosing one source keeps
  the corpus clean.
- **The retention purge job already exists in Hangfire.** Two related jobs in
  two different mechanisms (one Hangfire, one `pg_partman` + pg_cron) is more
  cognitive load than one mechanism handling both.

### What `pg_partman` would have bought us

- **DBA familiarity.** Operators with a `pg_partman` background would have a
  shorter on-call ramp.
- **Tested-at-scale operations.** `BEFORE/AFTER` create-partition hooks,
  cross-database replication-friendly partition naming, support for
  retention by partition rather than row — all robust and battle-tested.
- **Less code to write and own.** A single SQL call (`partman.create_parent`)
  per environment vs. a custom Hangfire job + its tests + its monitoring.

We accept losing those in exchange for the air-gapped + single-source-of-truth
benefits.

### Why row-level retention purge inside still-attached partitions

The platform's max retention is 10 years; the longest per-tenant retention
(security events) is 7. Most rows have 2-year retention. If we relied solely on
partition drops, a 2-year-old row would still sit in a partition that won't
drop for 8 more years (because the 7y-retention rows in the same partition pin
it).

The architecture splits responsibilities:

- **Partition manager** ensures partitions exist for the writing window and
  drops only on the platform-max horizon.
- **Retention purge** issues per-tenant per-class deletes against still-attached
  partitions on its own schedule. Bulk deletes inside partitioned tables hit
  only the partitions relevant to the `timestamp` range, so the pruning
  remains cheap.

### What would change our minds

- A measured production scale where Hangfire-driven partition management can't
  keep up with audit write volume (i.e. partition creation lagging behind
  inbound writes). The threshold is "next month's partition does not exist on
  the 1st of that month for any tenant" — if we observe even one such failure
  in production, this ADR gets revisited (with `pg_partman` as the front-runner).
- An air-gapped deployment that happens to ship its own DBA-managed Postgres
  extension catalogue. If `pg_partman` becomes free in the air-gap world, the
  air-gapped argument weakens.
- A separate domain need for `pg_partman` (e.g. partitioning `outbox_messages`
  by hour) that's painful to write a Hangfire job for. If the extension lands
  for one other reason, the marginal cost of using it here drops.

### What we explicitly punted on

- **Sub-monthly partitioning** (daily / hourly). Phase 11 production hardening
  may revisit if audit volume exceeds projections; for Phase 02a–10 monthly is
  the design.
- **Cold-storage tiering** (moving partitions older than 1 year to slower
  storage). Mentioned in [architecture/31 § Phasing](../architecture/31-audit-subsystem.md);
  Phase 11 concern.
- **Hub-side audit aggregation.** Hub may run its own audit collation across
  tenants for SaaS support; that's a Hub-repo ADR and out of scope here.

## Consequences

### Positive

- One mechanism (Hangfire) owns all audit-related lifecycle: partition create,
  partition drop, row-level retention purge. Operator training curve is one
  job runner, not two.
- Air-gapped deployment ships the same binary as SaaS. No "make sure
  `pg_partman` is at version 5.x" install-time check.
- Retention rules live in one place (Standards 18 + per-tenant `AuditConfig`).
- Failure surface is `IErrorTrackingProvider` ([ADR-0032](0032-exception-handling-logging-and-observability.md))
  by default — same observability rails as everything else in the platform.

### Negative

- We write and maintain partition-management code that an industry-standard
  extension already provides. Roughly 200-300 lines of C# + tests; non-trivial
  but bounded.
- A Hangfire job that fails silently is a worse failure mode than a `pg_partman`
  + pg_cron pair, where the database itself complains. Mitigation: the
  consecutive-failure event + the architecture test
  `Partition_Manager_Job_Is_Registered_AtStartup` listed below.
- Adding sub-monthly partitioning later means rewriting our own scheduler logic;
  with `pg_partman` it would have been a config change.

### Neutral

- The `audit_log` table layout and the partition-naming convention
  (`audit_log_YYYY_MM`) are independent of the partition manager and would
  not change if we ever revisit this ADR.
- PostgreSQL 18's native `DETACH PARTITION CONCURRENTLY` is used by either
  approach; not a differentiator.

## Implementation Notes

- **Job class:** `AuditPartitionManagementJob` in
  `LearnStack.Infrastructure.Audit/Jobs/`. Implements a single
  `RunAsync(CancellationToken)` method. Registered at startup via
  `RecurringJob.AddOrUpdate("learnstack:audit:partition-management", ... ,
  "15 2 * * *")` (daily 02:15 UTC; cron in 6-field Hangfire format).
- **Schema-aware SQL:** the job uses `Microsoft.EntityFrameworkCore`'s
  `ExecuteSqlInterpolatedAsync` with a parameterized partition name; no string
  concatenation. The SQL templates live next to the job class.
- **Idempotency:** every CREATE is wrapped in `IF NOT EXISTS`; every DROP is
  preceded by a `SELECT pg_class.relname` existence check + a sanity-check
  that the partition's bounds match what the job expects.
- **Failure observability:** failed runs hit `IErrorTrackingProvider`
  (per [ADR-0032 § IErrorTrackingProvider](0032-exception-handling-logging-and-observability.md)).
  After four consecutive failures the job emits a
  `learnstack.audit.partition.management.failed` integration event via the
  outbox; an operator dashboard subscribes.
- **Architecture test (lands in Phase 02a Packet 9):**
  `Partition_Manager_Job_Is_Registered_AtStartup` — asserts the host's
  `IServiceCollection` registers `AuditPartitionManagementJob` and that
  Hangfire's recurring-job catalogue contains the canonical job id at startup.
  Catalogued under
  [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md).
- **Tenant-context shape:** the job runs without a tenant context (it's a
  platform-admin operation). `ITenantContextAccessor.Current` is null while
  the job runs; `TenantContextSpanProcessor` (ADR-0032) tolerates that and
  emits the span with `tenant.id = "platform"`.
- **Migration shape:** the first migration creates `audit_log` as a partitioned
  parent table with two seed partitions (current and next month). Subsequent
  partitions are created by the recurring job — *not* by additional migrations.

## Amendments

### 2026-08-08 — Schedule moved to Phase 11; the `pg_partman` rejection is re-opened for review

Two clarifications. Neither changes the Decision.

**Schedule.** Partition management is no longer a Phase 02a deliverable. Per
[ADR-0035](0035-demand-gated-infrastructure.md), Phase 02a Packet 9 ships `audit_log`
as a single correct table and the partitioning plus retention job land in
[Phase 11](../roadmap/phase-11-production-hardening.md), triggered by measured
`audit_log` growth. Audit *correctness* cannot be retrofitted; audit *scale* can, and
fixing a partition strategy before any volume exists fixes it against a guess. The
choice made here — a Hangfire recurring job rather than a PostgreSQL extension — is
unchanged.

**The `pg_partman` rejection warrants re-review.** This ADR's Context concedes that
`pg_partman` is technically the stronger option and rejects it primarily to keep the
`SelfHostedAirGapped` story extension-free. `SelfHostedAirGapped` has no signed
contract and ships no earlier than Phase 11, and
[Engineering Principles](../standards/00-principles.md) now states that a deployment
mode or customer segment without a signed contract cannot be the deciding factor in a
technical choice — it may only break a tie between otherwise-equal alternatives. Here
the alternatives were **not** equal by this ADR's own assessment.

The decision stands until it is re-examined. When Phase 11 implements partitioning, the
implementer re-runs the comparison against the deployment modes that actually exist by
then. If `pg_partman` wins, that is a new ADR superseding this one — not an edit here.

**Corrected DDL.** The `audit_log` DDL this ADR's partition strategy assumes is the one
in [ADR-0033](0033-audit-durability-model.md), not the one in ADR-0016, which declares
a primary key twice and is rejected by PostgreSQL.

## References

- [ADR-0016 Audit Log Subsystem](0016-audit-log-subsystem.md) — establishes
  the `audit_log` table shape and monthly-partition policy.
- [ADR-0020 Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md)
  — the SelfHostedAirGapped mode that pushes against PostgreSQL extensions.
- [ADR-0031 PostgreSQL — Start on 18.x](0031-postgresql-major-version.md) —
  native declarative partitioning surface this ADR builds on.
- [ADR-0032 Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md)
  — the `IErrorTrackingProvider` failure path; `TenantContextSpanProcessor`
  null-context tolerance.
- [Standards 18 § Retention](../standards/18-audit-coverage.md) — the per-class
  retention table that scopes the row-level purge job; partition drops use the
  platform max only.
- [architecture/31 § 8 Retention](../architecture/31-audit-subsystem.md) —
  the canonical job-name catalogue the recurring-job id slots into.
- [pg_partman](https://github.com/pgpartman/pg_partman) — rejected alternative;
  link kept for future revisit.
