# ADR-0033: Audit Durability Model

## Status

Accepted

**Date:** 2026-08-08
**Supersedes:** [ADR-0016](0016-audit-log-subsystem.md)

## Decision Drivers

- **ADR-0016 and Standards 18 contradict each other, and both are load-bearing.**
  [Audit Coverage Standards](../standards/18-audit-coverage.md) requires a MUST-class
  audit row to be written *in the same transaction* as the state change it records, and
  forbids writing after the controlling transaction commits.
  [ADR-0016](0016-audit-log-subsystem.md) requires that audit never blocks business
  logic. Under the shipped pipeline both cannot hold.
- **The shipped pipeline puts audit outside the transaction.** The canonical order
  fixed in [ADR-0032](0032-exception-handling-logging-and-observability.md) and shipped
  in Phase 02a Packet 3 is `Validation → Logging → AuditLog → TenantContext →
  Authorization → Transaction → OutboxFlush → Handler`. `AuditLogBehavior` wraps
  `TransactionBehavior` from the outside, so its write lands after the business
  transaction has committed or rolled back.
- **Row Level Security turns this from a durability gap into a silent failure.** Once
  [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) lands, the audit
  insert runs outside the transaction that sets `app.tenant_id`. The policy's
  `WITH CHECK` rejects the row, and the catch-and-log posture described in
  [Audit Subsystem](../architecture/31-audit-subsystem.md) swallows the rejection. The
  audit log would record nothing while reporting success.
- **Compliance classes have different tolerances.** Losing an operational "course
  updated" entry costs a support conversation. Losing "platform admin read tenant B's
  learner records" costs an audit finding.
- **Reordering the pipeline is more expensive than it looks.** The order is a global
  property asserted by `MediatR_Pipeline_Order_Matches_Canonical_Sequence`, referenced
  by ADR-0032's rationale chain, and already shipped. Changing it to fix audit would
  also move `TenantContext` and `Authorization` relative to `Transaction`, each with
  its own consequences.

## Considered Options

1. **Split the audit classes by durability, keep the pipeline order** (chosen).
   MUST-class audit is written as a durable intent inside the business transaction;
   enrichment and dispatch happen outside it. SHOULD/MAY-class stays best-effort.
2. **Move `TransactionBehavior` outward so it wraps `AuditLogBehavior`** (rejected).
   Fixes audit durability by making every audit write share the business transaction —
   but also drags `TenantContext` and `Authorization` inside the transaction, opens the
   transaction before validation has finished, and changes a shipped, test-asserted
   global ordering to solve a problem that belongs to one behavior.
3. **Accept best-effort auditing everywhere and weaken Standards 18** (rejected).
   Honest, cheap, and wrong: the compliance classes this platform claims to support
   (`actor.platformAdmin`, `actor.hubOperator`, permission changes, cross-tenant reads)
   are exactly the ones a best-effort log may lose.
4. **Write audit through the outbox** (rejected for MUST-class). The outbox is
   at-least-once and asynchronous by design; an audit row that may arrive twice, or
   late, is not what a compliance reviewer is asking for. The outbox remains correct
   for audit *fan-out* to external sinks, which is a different problem.

## Decision

LearnStack splits audit writes into two durability classes and fixes **one component per
step**, so no moment in the lifecycle is unowned. The shape is called
**decide → write → reconcile**.

| Step | Owner | Position | What it does |
|---|---|---|---|
| **Decide** | `AuditLogBehavior` | step 3 | Classifies `(module, operation)`, mints the `AuditEntryId`, parks a **pending intent** in the scoped `IAuditStateCapture`. Opens no transaction, touches no `DbContext`. |
| **Write** | `TransactionBehavior` | step 6, immediately before `COMMIT` | Calls `IAuditStore.WritePendingAsync`, which issues one parameterised `INSERT INTO audit_log` on the **ambient transaction**, with `SET LOCAL app.tenant_id` already in force. A failure here rolls the business transaction back. |
| **Reconcile** | `AuditLogBehavior` | step 3, on the way out | Reads the intent's final state. If it is anything other than `Committed`, writes the row **standalone**, in its own short transaction, carrying the real outcome. |

**MUST-class audit** — security, compliance, and privileged-access events, as classified
in [Audit Coverage Standards](../standards/18-audit-coverage.md) — commits **in the same
transaction** as the state change it records, or it is written standalone with a
non-success outcome. There is no third possibility and no window in which a committed
state change has no audit row.

**Durability is a property of the commit, not of the enrolment.** The write step marks
the intent `WrittenInTransaction`, which is *not* durable — the transaction has not
committed. `TransactionBehavior` therefore reports the commit boundary explicitly:
`Committed` once `CommitAsync` returns, `RolledBack` after a rollback, and
`Indeterminate` when `CommitAsync` throws in a way that leaves the server-side outcome
unknown. The reconcile step keys off that signal and off nothing else. A row that was
inserted and then rolled back is **not** consumed; it is re-written standalone with
outcome `failed`. The common case this protects is not exotic: a handler that calls
`SaveChanges` and then returns `Result.Fail(...)` rolls the audit row back on every
business-rule rejection.

**MUST-class audit with no business transaction** — a `denied` authorisation outcome at
step 5, a read-sensitive query, a non-mutating security event — never reaches step 6. Its
row is written by the reconcile step through `IAuditStore.WriteStandaloneAsync`, which
opens its own short transaction and issues `SET LOCAL app.tenant_id` as its first
statement so the `audit_log` `WITH CHECK` predicate is satisfied on its own terms. It
runs on a connection that is not inside the business transaction, so a rollback there
cannot take it.

**SHOULD/MAY-class audit** — operational and diagnostic events — remains best-effort,
written by the reconcile step outside any business transaction. Its accepted loss is
written down in the module's coverage matrix rather than assumed.

**The row is written once and never updated.** By the time `TransactionBehavior` is about
to commit, every field is known — actor, correlation, before/after snapshots, outcome —
so the write step composes the complete row. There is no second phase, no enrichment
`UPDATE`, and `IAuditStore` has no update method.

**Classification never reads the database on the request path.** The catalogue is
in-process, registered at startup by `IModule.RegisterAuditDefaults()`. The tenant's
`audit_config` overrides are read through `ICacheService`; on a miss the loader opens
**its own** short transaction and sets `app.tenant_id` itself. This is not a
refinement — at step 3 no transaction exists, `app.tenant_id` is unset, and `audit_config`
is RLS-protected, so a lookup there returns **zero rows silently**, which reads exactly
like "this tenant has no overrides" and never trips a fail-closed `catch`.

**Fail-closed, stated precisely.** Two failures reject the operation: an operation the
catalogue does not classify at all (`audit_unclassified_operation`), and a MUST-class row
that cannot be written durably (`audit_unavailable`, HTTP 503). A failure to read a
*tenant override* does **not**: the in-process catalogue still supplies the MUST floor, so
the operation never proceeds unaudited — which is the property ADR-0016's
`catch → continue` path lost — and rejecting every request platform-wide because a cache
is unavailable is a worse compliance outcome than losing one tenant's voluntary
SHOULD→MUST elevation. The failure is logged at `Error` and surfaced on the audit health
check. A tenant `AuditConfig` override may narrow SHOULD/MAY coverage; it may never remove
baseline MUST coverage.

## Context

### Why the durable-intent shape

The pattern is the outbox pattern applied to audit, with one deliberate difference: the
intent is written synchronously and its failure is fatal to the business operation,
because for MUST-class events the audit *is* part of the operation's contract. What the
intent buys is the separation of two concerns that ADR-0016 conflated:

| Concern | Where it happens | Failure posture |
|---|---|---|
| Recording that the event occurred | Inside the business transaction | Fail closed — the operation is rejected |
| Enriching, redacting, projecting, exporting | After commit, from the durable row | Best-effort, retried, never blocks |

ADR-0016's "audit never blocks business logic" was written about the second column and
applied to both. It is preserved for the second column and withdrawn for the first.

### What was rejected and why it might come back

Option 2 (reordering the pipeline) is not wrong in principle — a pipeline where the
transaction is the outermost data-touching behavior is a defensible design. It was
rejected because the cost of changing a shipped, test-asserted global ordering exceeds
the cost of fixing the one behavior that needs fixing. If a later phase finds a second
independent reason to move `TransactionBehavior`, this decision should be revisited
together with that reason rather than piecemeal.

### Corrected `audit_log` DDL

ADR-0016's example DDL declares a primary key twice — once inline on `id` and once as a
table constraint on `(id, timestamp)`. PostgreSQL rejects that table. The composite is
the one to keep and the inline declaration is the error.

The table below is what Phase 02a Packet 9 ships: a **plain, unpartitioned** table. The
composite key is still the right key for it, for a forward-looking reason rather than a
present one — a partitioned table must include every partition-key column in its primary
key, so declaring `(id, timestamp)` now is what lets the Phase 11 conversion happen
without a key migration, which is the expensive half of that operation.

```sql
CREATE TABLE audit_log (
    id              uuid        NOT NULL,
    tenant_id       uuid        NOT NULL,
    organization_id uuid        NULL,
    -- ... remaining columns unchanged from ADR-0016 ...
    timestamp       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT audit_log_pkey PRIMARY KEY (id, timestamp)
);
-- No PARTITION BY clause in Phase 02a. PostgreSQL has no in-place conversion —
-- there is no ALTER TABLE ... PARTITION BY — so Phase 11 creates a partitioned
-- parent, attaches this table to it, and recreates the indexes and the RLS policy
-- on the parent, under a lock. The composite key above is what keeps that a data
-- operation rather than a key migration.
```

Partitioning itself is **not** a Phase 02a concern. Phase 02a Packet 9 ships
`audit_log` as a single correct table; monthly partitioning, the retention job, and the
lifecycle policy from [ADR-0028](0028-audit-log-partition-management.md) ship in
[Phase 11](../roadmap/phase-11-production-hardening.md) per
[ADR-0035](0035-demand-gated-infrastructure.md). Audit correctness cannot be added
later; audit scale can.

### Retention schedule

Three documents currently disagree on the retention job's cadence (daily in
[Audit Coverage Standards](../standards/18-audit-coverage.md) and
[ADR-0028](0028-audit-log-partition-management.md), weekly in
[Audit Subsystem](../architecture/31-audit-subsystem.md)). The cadence is **daily**;
the architecture document is the outlier and is corrected.

## Consequences

### Positive

- MUST-class audit rows commit with the state change they describe, or the state change
  does not happen. The guarantee Standards 18 always claimed is now true.
- The audit insert executes inside the transaction that sets `app.tenant_id`, so Row
  Level Security accepts it. The silent-failure mode that ADR-0003 Amendment 3 would
  otherwise have introduced never exists.
- The shipped pipeline order, its architecture test, and ADR-0032's rationale chain are
  untouched.
- Operational audit keeps its cheap path; the platform does not pay compliance-grade
  cost for "a course was renamed".

### Negative

- A MUST-class command now has a failure mode it did not have: audit-store failure
  rejects the operation. This is the point of the decision, but it is a real
  availability trade-off and must be visible in the operational runbooks.
- Every module's audit-coverage matrix must classify its operations before its commands
  ship, rather than after — MUST/SHOULD/MAY is now a functional distinction, not a
  documentation one.

### Neutral

- `AuditLogBehavior` keeps its position and its exception-handling responsibility; only
  its durability contract changes.
- Audit fan-out to external sinks still rides the outbox, unchanged.

## Implementation Notes

- **`IAuditStore`** — port in `LearnStack.SharedKernel.Abstractions.Audit`,
  implementation `PostgresAuditStore` in `LearnStack.Infrastructure.Audit`. Exactly three
  write methods and **no update method**:
  - `WritePendingAsync(IUnitOfWork uow, CancellationToken ct)` — the in-transaction write;
    a no-op when no MUST-class intent is pending; **throws** on failure so the caller
    rolls back.
  - `WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken ct)` — its own short
    transaction: `BEGIN; SET LOCAL app.tenant_id; INSERT; COMMIT`, on a connection that is
    not inside the business transaction.
  - `WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken ct)` — same shape,
    SHOULD/MAY only; the caller logs and drops failures.
- **The row is written as parameterised SQL, not through an EF entity.** `AuditEntry`
  stays the Audit module's aggregate and is the **read** model for the audit admin API.
  The write path carries `AuditEntryDraft`, a `SharedKernel` record, and
  `PostgresAuditStore` turns it into one `INSERT`. This is deliberate. Mapping
  `AuditEntry` into every module's `DbContext` through a shared configuration in
  `LearnStack.SharedKernel` would require SharedKernel to reference
  `LearnStack.Modules.Audit.Domain` — a **circular project reference**, since that Domain
  project already references SharedKernel — and would put hand-written EF Core mapping
  code in SharedKernel, which
  [Architecture Standards § Build-time-only exceptions](../standards/01-architecture-standards.md)
  restricts to generated or marker shapes and gates behind an ADR. It would also make
  every module's Infrastructure assembly reference `AuditEntry`, which is exactly what
  [`Modules_Do_Not_Write_AuditLog_Directly`](../standards/21-architecture-tests-catalogue.md)
  exists to prevent.
- **Atomicity comes from the transaction, not from `SaveChanges`.** "The same
  `DbContext.SaveChanges` as the business write" was the wrong formulation and is
  withdrawn. The guarantee is "the same transaction", which is what a reader of
  `audit_log` actually observes and which needs no cross-context machinery.
- **`IUnitOfWork`** — the seam `TransactionBehavior` uses to open, commit and roll back
  the ambient transaction without naming a module's `DbContext`, and through which
  `IAuditStore` reaches the ambient connection. A
  [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) deliverable that
  `TransactionBehavior`'s shipped shell already presumes; named here because the durable
  audit write depends on it.
- **`AuditConfig` overrides are a cached projection**, refreshed out of band and
  invalidated by the tenant-configuration integration event — never a request-path query.
  The loader sets its own `app.tenant_id`.
- Modules never write `audit_log` directly — unchanged from ADR-0016.
- Lands in [Phase 02a Packet 9](../roadmap/phase-02a-kernel-tenancy.md), together with
  the `AuditChangeTrackerInterceptor` (snapshot capture only — it never constructs or
  inserts an audit row), `IAuditStateCapture`, and the `audit_log_append_only_guard`
  trigger.
- Architecture tests, all registered in
  [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) by
  Packet 9: `AuditEntry_Inherits_Entity_Not_AuditableEntity`,
  `Every_TenantOwned_Command_HasAuditCoverage`,
  `MustClass_Audit_Writes_Share_The_Business_Transaction`,
  `Audit_Survives_Transaction_Rollback`,
  `Audit_Classification_Does_Not_Read_The_Database_On_The_Request_Path`,
  `AuditLog_Update_Is_Column_Restricted`.
- The binding integration tests, all run as `learnstack_app` (`NOBYPASSRLS`): one
  MUST-class command produces exactly one `audit_log` row; a command whose transaction is
  forced to roll back at `COMMIT` produces **zero** business rows and **exactly one**
  `audit_log` row with outcome `failed`; a command whose durable audit write is forced to
  fail produces zero business rows and returns `503 audit_unavailable`.

## References

- [ADR-0016 Audit Log Subsystem](0016-audit-log-subsystem.md) (superseded by this ADR)
- [ADR-0003 Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md) (Amendment 3)
- [ADR-0028 Audit Log Partition Management](0028-audit-log-partition-management.md)
- [ADR-0032 Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0035 Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [Audit Coverage Standards](../standards/18-audit-coverage.md)
- [Audit Subsystem](../architecture/31-audit-subsystem.md)
