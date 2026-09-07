# ADR-0033: Audit Durability Model

## Status

Accepted (**Amendment 1: 2026-08-18**: a standalone MUST-class write
failure changes the response only when the operation would otherwise have succeeded.
**Amendment 2: 2026-09-07**: intents are plural, only the owning unit-of-work frame
writes them and reports the commit boundary, `outcome` is four values, the writer
supplies `timestamp`, both standalone writers announce two session variables, a fourth
write method serves `EnterPlatformAdminScope`, and a read-sensitive query **does** reach
step 6. Both at the bottom of the document.)

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

## Amendment 1 — Standalone MUST-class write failure and the response (2026-08-18)

**Status: Accepted.** Raised by [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
whose rejection path is the first caller of `WriteStandaloneAsync` that runs on an
anonymous, unauthenticated request.

### What was ambiguous

§ Decision states the fail-closed rule without distinguishing the two shapes of
MUST-class write: "Two failures reject the operation: an operation the catalogue does
not classify at all (`audit_unclassified_operation`), and a MUST-class row that cannot
be written durably (`audit_unavailable`, HTTP 503)." Read literally, that applies the
503 to a **standalone** row recording an operation that is *already being rejected* — a
`denied` authorisation outcome, or a rejected tenant assertion. There is nothing to fail
closed on: no state change is uncommitted-but-unaudited, and no disclosure is
granted-but-unaudited. Worse, on a rejection path an anonymous client can drive, it
converts audit-store pressure into a per-request availability signal that the same
client controls.

### How it should be read

A MUST-class **standalone** write failure changes the response only when the operation
would otherwise have **succeeded**.

- A standalone row recording an access that is being **granted** — a `read-sensitive`
  query is the case that matters — still rejects with **`503 audit_unavailable`**.
  Without it, data leaves the system unaudited, which is the whole point of the class.
- A standalone row recording an operation that is **already being refused** — a `denied`
  authorisation outcome, a rejected tenant assertion — keeps its own status (403, 404).
  The refusal is the security outcome; downgrading it to a 503 tells the caller more,
  not less.

In both cases the failure is not silent: it logs at `Critical`, increments the
standalone-write-failure counter, and marks the audit health check unhealthy. Beyond a
configured maximum unhealthy window the deployment fails closed at the **deployment**
level — it stops serving — rather than serving indefinitely with an audit trail nobody
is writing. Availability that is bought by losing the record is bought on credit, and
this is where the loan comes due.

**The in-transaction MUST class is untouched.** A command whose durable audit write
fails still produces zero business rows and returns `503 audit_unavailable`, exactly as
§ Decision and the binding integration test in § Implementation Notes state.

### Why this is a clarification and not a new decision

The guarantee § Decision protects is that nothing proceeds unaudited. An operation that
is being rejected does not proceed. The rule's subject was always the operation that
would otherwise have happened; this amendment says so in the one case where the original
wording could be read against its own purpose.

## Amendment 2 — The write path against the code that shipped after this ADR (2026-09-07)

**Status: Accepted.** Raised by [ADR-0044](0044-audit-write-path.md), which decides the
questions below and is the record to read for the reasoning; this amendment states what
changes in *this* ADR's contract. **§ Decision is unchanged**: MUST-class audit still
commits on the same transaction as the state change it records or is written standalone
with a non-success outcome, and there is still no third possibility.

### What this ADR could not have known

This record was accepted 2026-08-08. [ADR-0040](0040-ambient-unit-of-work.md) (2026-08-27)
then defined the ambient unit of work, its frames and its joiners; Packet 6 shipped
`TransactionBehavior`'s body; Packets 7 and 8 shipped the seven commands this ADR governs
and the two module matrices that classify them. Five clauses written before all of that
are incomplete against it. None was false when it entered the record, so each is amended
here rather than corrected in place ([ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)).

### 1. Intents are plural

§ Decision's *Decide* row parks "a pending intent", singular, and § Implementation Notes
describes `WritePendingAsync` as "a no-op when no MUST-class intent is pending". Read as
**one intent per audited `(resource, operation)`**, not one per request.
`IAuditStateCapture` holds an ordered list; `WritePendingAsync` composes and inserts all
of them on the ambient transaction.

*How it was shown incomplete:* `ProvisionTenantCommand` writes `Tenant` and
`Organization` on one transaction — the single exception
[ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md) sanctions — and
[the Tenancy matrix](../modules/tenancy/audit.md) classifies **both** MUST. Under the
singular reading the `Organization | create` row it requires is never written, and the
matrix is wrong the day it is implemented.

### 2. Only the owning frame writes, and only it reports the boundary

ADR-0040 § Nesting makes a joiner frame reachable, and `IUnitOfWorkScope.CompleteAsync`
is a documented no-op on one. `IAuditStore.WritePendingAsync` and the
`Committed` / `RolledBack` / `Indeterminate` signal are therefore called **only on the
owning frame** (`IUnitOfWorkScope.IsOwner`), and the owner flushes every intent in the
scope rather than only its own. A joiner writes nothing, signals nothing, does not run
the reconcile step, and does not call `IAuditStateCapture.Clear()`.

*How it was shown incomplete:* a joiner calling `MarkCommitted` claims durability for a
row nothing has committed; if the outer transaction then rolls back, the reconcile step
sees `Committed`, does nothing, and the MUST row is lost — which is the failure this ADR
exists to prevent. A joiner calling `Clear()` in its `finally` erases the outer request's
intents and snapshots before the owner has committed.

### 3. `outcome` is four values, and it is not a boolean

The `audit_log` column is `outcome text` under
`CHECK (outcome IN ('success','denied','failed','indeterminate'))`. ADR-0016's
`is_success boolean`, which § Corrected `audit_log` DDL inherits by writing "remaining
columns unchanged from ADR-0016", is superseded: a boolean cannot carry `denied`, which
[Audit Coverage Standards](../standards/18-audit-coverage.md) requires in order to detect
probing, and `Indeterminate` — which this ADR introduces as an intent state — needs a
value a reader can filter on.

### 4. The writer supplies `timestamp`

`PostgresAuditStore` always supplies `timestamp` from `IClock`: the intent's `DeclaredAt`
for the in-transaction row, a **fresh** reading for a standalone re-write. `DEFAULT now()`
remains on the column as a backstop for a row inserted by something other than the store.

*How it was shown incomplete:* § Decision requires the `Indeterminate` case to re-write
standalone carrying the same `AuditEntryId`, and the primary key is `(id, timestamp)`. Two
rows with one id coexist only if their timestamps differ. Supplying `DeclaredAt` for both
raises `23505` in exactly the case the design exists to serve, and the caller then receives
`audit_unavailable` for an operation that in fact committed. A `23505` on the standalone
re-write is instead **positive evidence that the `COMMIT` landed**: it is logged at
`Warning`, counted, and swallowed.

### 5. The row's tenant, and the second GUC

The row's `tenant_id` is the tenant the ambient transaction **announced** — for a request
implementing `IProvisionsTenant` under an unresolved context that is
`ProvisioningTenantId`, and for a platform-scope operation with no resolvable tenant it is
the reserved sentinel ADR-0044 § 1 fixes. It never comes from a request payload.

§ Implementation Notes gives both standalone writers as `BEGIN; SET LOCAL app.tenant_id;
INSERT; COMMIT`. Read as announcing **both** session variables — `app.tenant_id` and
`app.organization_id` — from the draft.

*How it was shown incomplete:* `audit_log` carries `organization_id` and therefore takes
the org-scoped policy of [Database Standards § Table classes](../standards/05-database.md).
Measured on that policy shape: an insert whose `organization_id` is non-null while
`app.organization_id` is unset is refused with *new row violates row-level security
policy*; with both variables set the same insert returns `INSERT 0 1`. Every `denied` row
for an org-scoped resource and every rollback re-write of one travels that path.

### 6. A fourth write method

`IAuditStore` gains `WritePlatformScopeAsync`, used by `EnterPlatformAdminScope(reason)`
to write its `security-event` row on its own platform-role connection before the operation
runs. § Implementation Notes' "exactly three write methods" is superseded; **"no update
method" is not** — this is a fourth write, and `IAuditStore` still cannot update a row.

*Why the three could not serve it:* the row runs on a connection the request path does not
own, as a role the other three never use, and before the operation it describes.

### 7. Correction — a read-sensitive query does reach step 6

§ Decision states that a MUST-class event with no business transaction "never reaches step
6" and lists a read-sensitive query among its examples. That is no longer true, and the
distinction matters because Amendment 1 builds a 503-versus-403 rule on top of it.

*How it was shown wrong:* `TransactionBehavior` as shipped in Packet 6 has no request-kind
gate — its class remarks say so explicitly and its body opens the ambient transaction
unconditionally for everything that reaches step 6, reads included, because a read needs
the `SET LOCAL` as much as a write does. A granted MUST-class `read-sensitive` query
therefore rides the **in-transaction** path.

`WriteStandaloneAsync` is reached by three shapes, and only these: a short-circuit at step
1, 4 or 5; a non-MediatR caller (`TenantAssertionMiddleware`, `EnterPlatformAdminScope`);
and the reconcile step after a `RolledBack` or `Indeterminate` outcome. The `denied` and
non-mutating-security-event examples in § Decision remain correct.

### Carriers changed by this amendment

[ADR-0044](0044-audit-write-path.md) (the deciding record),
[Audit Subsystem](../architecture/31-audit-subsystem.md) §§ 1, 3, 4, 5, 6, 7, 13, 14,
[Audit Coverage Standards](../standards/18-audit-coverage.md),
[Database Standards](../standards/05-database.md),
[the architecture-test catalogue](../standards/21-architecture-tests-catalogue.md),
[the glossary](../glossary.md), [CLAUDE.md](../../CLAUDE.md) and
[Phase 02a](../roadmap/phase-02a-kernel-tenancy.md). No other Accepted ADR's body changes;
[ADR-0023](0023-strongly-typed-id-source-generator.md),
[ADR-0028](0028-audit-log-partition-management.md),
[ADR-0021](0021-feature-based-entitlement.md) and
[ADR-0020](0020-triple-deployment-hybrid-license.md) each carry their own dated amendment
for the part of ADR-0044 and ADR-0045 that touches them.

## References

- [ADR-0016 Audit Log Subsystem](0016-audit-log-subsystem.md) (superseded by this ADR)
- [ADR-0003 Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md) (Amendment 3)
- [ADR-0028 Audit Log Partition Management](0028-audit-log-partition-management.md)
- [ADR-0032 Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0035 Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [Audit Coverage Standards](../standards/18-audit-coverage.md)
- [Audit Subsystem](../architecture/31-audit-subsystem.md)
