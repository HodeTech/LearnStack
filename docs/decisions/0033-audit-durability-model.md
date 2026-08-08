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

LearnStack splits audit writes into two durability classes.

**MUST-class audit** — security, compliance, and privileged-access events, as
classified in [Audit Coverage Standards](../standards/18-audit-coverage.md) — is
written as a **durable intent** inside the business transaction. The audit row is
enrolled in the same `DbContext.SaveChanges` as the state change, so it commits with it
or not at all, and so it executes while the transaction-local `app.tenant_id` is set
and Row Level Security accepts it. If the audit row cannot be written, the business
transaction **fails closed**.

**SHOULD/MAY-class audit** — operational and diagnostic events — remains best-effort
and may be written outside the transaction. Its accepted loss is written down rather
than assumed.

Enrichment, redaction, projection and external fan-out happen **after** the commit,
reading the durable intent. `AuditLogBehavior` keeps its shipped position in the
pipeline and its shipped responsibility — catching handler exceptions, recording the
outcome, and rethrowing via `ExceptionDispatchInfo`. What changes is that the MUST-class
row it records is *already durable* by the time it runs.

A tenant `AuditConfig` override may narrow SHOULD/MAY coverage. It may **not** remove
baseline MUST coverage, and a failure to read the audit configuration **fails closed**
— the operation is rejected rather than proceeding unaudited.

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
table constraint on `(id, timestamp)`. PostgreSQL rejects that table. A partitioned
table must include every partition key column in its primary key, so the composite is
the correct one and the inline declaration is the error:

```sql
CREATE TABLE audit_log (
    id              uuid        NOT NULL,
    tenant_id       uuid        NOT NULL,
    organization_id uuid        NULL,
    -- ... remaining columns unchanged from ADR-0016 ...
    timestamp       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT audit_log_pkey PRIMARY KEY (id, timestamp)
) PARTITION BY RANGE (timestamp);
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

- MUST-class audit rows are enrolled through `IAuditStore` in the same
  `DbContext.SaveChanges` as the business write. Modules never write `audit_log`
  directly — unchanged from ADR-0016.
- `AuditConfig` lookups fail closed. A configuration read failure rejects the operation
  rather than skipping the audit.
- Lands in [Phase 02a Packet 9](../roadmap/phase-02a-kernel-tenancy.md), together with
  the `AuditChangeTrackerInterceptor` and `IAuditStateCapture`.
- Architecture tests: `AuditEntry_Inherits_Entity_Not_AuditableEntity`,
  `Every_TenantOwned_Command_HasAuditCoverage`,
  `MustClass_Audit_Writes_Share_The_Business_Transaction` (new — registered in
  [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) by
  Packet 9).
- The integration test that proves it: one command produces exactly one `audit_log`
  row, and a command whose audit write is forced to fail produces **zero** business
  rows.

## References

- [ADR-0016 Audit Log Subsystem](0016-audit-log-subsystem.md) (superseded by this ADR)
- [ADR-0003 Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md) (Amendment 3)
- [ADR-0028 Audit Log Partition Management](0028-audit-log-partition-management.md)
- [ADR-0032 Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0035 Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [Audit Coverage Standards](../standards/18-audit-coverage.md)
- [Audit Subsystem](../architecture/31-audit-subsystem.md)
