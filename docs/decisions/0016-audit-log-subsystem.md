# ADR 0016: Audit Log Subsystem

## Status

Superseded by [ADR-0033](0033-audit-durability-model.md) (2026-08-08)

> **What changed.** ADR-0033 keeps this ADR's subsystem design — a central Audit module
> owning `AuditEntry`, capture through the MediatR pipeline, modules never touching
> `audit_log` directly — and replaces its **durability contract**. This ADR's "audit
> never blocks business logic" applied uniformly; under ADR-0033 it applies to
> SHOULD/MAY-class audit only. MUST-class audit is written as a durable intent inside
> the business transaction and **fails closed**, which is what
> [Audit Coverage Standards](../standards/18-audit-coverage.md) always required and
> what PostgreSQL Row Level Security requires once
> [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) lands.
>
> ADR-0033 also corrects the `audit_log` DDL below, which declares a primary key twice
> — once inline on `id` and once as a table constraint on `(id, timestamp)` — and is
> therefore rejected by PostgreSQL.
>
> Read this ADR for the subsystem's context and rationale; read ADR-0033 for the
> binding rules.

## Date

2026-05-18

## Decision

LearnStack ships a first-class **audit log subsystem** comprising:

1. **`LearnStack.Modules.Audit`** — a core module owned by the platform, with its own
   aggregate (`AuditEntry`), repository, retention job, and (later) admin API + UI.
2. **`AuditChangeTrackerInterceptor`** — an EF Core `SaveChangesInterceptor` that snapshots
   `Added` / `Modified` / `Deleted` state for every entity inheriting `AuditableEntity<T>`,
   producing JSON before/after/delta diffs.
3. **`IAuditStore`** + `PostgresAuditStore` — the persistence port; append-only writes to
   `audit_log` table.
4. **`AuditLogBehavior<TRequest, TResponse>`** — a MediatR pipeline behavior that wraps
   command/query handlers, captures the diff buffer (`IAuditStateCapture`), and writes one
   `AuditEntry` row per request with full context (actor, tenant, organization, correlation
   id, before/after JSON, outcome).
5. **`audit_log` table** — append-only, partitioned by month for retention, with mandatory
   `tenant_id`, `correlation_id`, `actor_user_id`, `module`, `operation`, `operation_type`,
   `outcome`, `before_state` (jsonb), `after_state` (jsonb), `changes` (jsonb), `timestamp`.
6. **Retention policy** — per-tenant configurable; default `7 years` for security events
   and financial operations, `2 years` for everything else. Purge job runs in the
   `maintenance` queue.

The subsystem is mandatory: every command handler is auto-audited by default; queries are
opt-in (`[Auditable]` attribute or `IAuditable` interface). Audit failure **never blocks
business logic** — try/catch around the audit save, with `ExceptionDispatchInfo.Capture(...)`
to re-throw the original handler exception preserving the original stack trace.

## Context

Standard 18 ([`docs/standards/18-audit-coverage.md`](../standards/18-audit-coverage.md))
declares a **MUST / SHOULD / MAY** audit-coverage matrix per module. The standard exists but
the implementation does not. Without a concrete subsystem, the standard remains aspirational.

Nexora's audit implementation (see
`Nexora/docs/modules/tier-1-core/audit/SPEC.md` and
`Nexora/docs/decisions/0009-audit-repository-pattern.md`) demonstrated the
three-piece split — **interceptor captures**, **scoped buffer holds**,
**MediatR behavior writes** — survives multi-aggregate commands and lets audit be feature-
toggled per (module, operation) without DbContext awareness. The same shape applies
unchanged to LearnStack's RLS-shared-schema model; only the persistence target changes from
"audit table per tenant schema" to "single `audit_log` table with `tenant_id` column +
month partitions".

Multi-tenant SaaS for education brings specific audit demands:

- **Learner enrollment / unenrollment** — paid courses, GDPR right-to-erasure, dispute
  resolution. Required by ADR-0020 (compliance caps).
- **Course publish / unpublish** — content-version transitions affect every enrolled
  learner; auditor must reconstruct who published what when.
- **Recording start/stop, consent change** — privacy regulator inquiry (KVKK + GDPR).
- **Platform-admin cross-tenant queries** — every cross-tenant operation MUST audit (see
  ADR-0003 defense-in-depth).
- **Hub-side actions** — tenant create/suspend/terminate, plan change, license issue,
  custom-domain approval — recorded on the Hub side (separate audit stream) AND on the
  LearnStack side when they trigger downstream effects.

## Decision drivers

1. **Standard 18 must become enforceable.** Architecture tests should fail when a Tier-1+
   module commands lack audit coverage.
2. **Audit must never block business writes.** A failed audit insert (DB hiccup, full disk)
   must log and continue; the user-facing operation succeeds.
3. **Auditable state must reflect *what the operation changed*** — before/after snapshots
   for `Update` operations; full row snapshot for `Create` and `Delete`.
4. **Append-only by inheritance.** `AuditEntry` extends `Entity<TId>` (not
   `AuditableEntity<T>`) — soft delete on an audit row is a contradiction.
5. **Partitioning for retention.** 7-year retention on security events generates large
   tables; monthly partitions enable cheap drop-table retention.
6. **MUST/SHOULD/MAY is per-module.** Different modules have different audit volume tolerance
   (e.g. Notifications template-edit MUST audit; per-recipient delivery MAY audit).
7. **Same proven pattern** as Nexora — battle-tested across 6 modules, no observed audit
   data loss across phases of development.

## Considered options

### Option A — EF Interceptor + Scoped Capture + MediatR Behavior (chosen)

Three pieces, separated concerns:

1. **`AuditChangeTrackerInterceptor : ISaveChangesInterceptor`** — runs inside `SaveChanges`,
   walks ChangeTracker, snapshots state for `AuditableEntity<T>` descendants, appends to
   scoped `IAuditStateCapture.Changes` buffer.
2. **`IAuditStateCapture`** — scoped (per-request) buffer that holds entity snapshots until
   the MediatR behavior reads them after the handler returns.
3. **`AuditLogBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>`** —
   wraps the handler, awaits it (catching exceptions to still audit failed operations),
   reads `IAuditStateCapture.Changes`, builds one `AuditEntry` carrying the multi-entity
   diff, and writes via `IAuditStore.SaveAsync`. On audit-store failure, logs and continues.

**Pros:**
- Survives multi-aggregate commands (one request → many entities → one audit row with
  multi-entity JSON).
- Audit is configurable per (module, operation) without changing handler code.
- Battle-tested in Nexora.

**Cons:**
- Three moving parts to understand.
- Scoped buffer must be cleared on request end to prevent cross-request bleed.

### Option B — Domain-event-driven audit (rejected)

Every aggregate emits an `EntityAudited` domain event on mutation; a single handler writes
the audit row.

**Pros:**
- Pure domain-event model, no infrastructure interceptor.

**Cons:**
- Every aggregate must explicitly emit the event — high boilerplate, easy to forget.
- Bulk updates (e.g. EF `ExecuteUpdate`) bypass the domain layer; no event raised; audit lost.
- Multi-aggregate commands generate N events for one logical audit entry — must be
  re-correlated.

### Option C — Database trigger-based audit (rejected)

PostgreSQL triggers write to `audit_log` on every INSERT / UPDATE / DELETE.

**Pros:**
- Cannot be bypassed by code.

**Cons:**
- Triggers can't access application context (`actor_user_id`, `correlation_id`, `tenant_id`
  beyond what's in the row).
- Trigger-based audits are notoriously difficult to evolve (schema migrations are scarier).
- Per-operation enablement (MUST/SHOULD/MAY) is hard to express in trigger logic.
- Locks the audit format to one DB engine.

## Decision outcome

Adopt **Option A**: EF Interceptor + Scoped Capture + MediatR Behavior, with audit data
persisted in a single shared `audit_log` table protected by RLS.

### Domain model

```csharp
namespace LearnStack.Modules.Audit.Domain;

public sealed class AuditEntry : Entity<AuditEntryId>  // NOT AuditableEntity
{
    public Guid TenantId { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string? ActorEmail { get; private set; }

    public string Module { get; private set; }                  // "courses", "enrollment"
    public string Operation { get; private set; }               // "PublishCourse", "EnrollLearner"
    public OperationType OperationType { get; private set; }    // Create | Update | Delete | ReadSensitive | SecurityEvent
    public OperationClass OperationClass { get; private set; }  // MUST | SHOULD | MAY (audit-coverage tier)

    public string? EntityType { get; private set; }
    public string? EntityId { get; private set; }

    public bool IsSuccess { get; private set; }
    public string? ErrorKey { get; private set; }               // lockey-style key on failure

    public string? BeforeState { get; private set; }            // JSON
    public string? AfterState { get; private set; }             // JSON
    public string? Changes { get; private set; }                // JSON diff

    public string? CorrelationId { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }
    public Dictionary<string, string>? Metadata { get; private set; }

    public static AuditEntry Create(
        Guid tenantId, string module, string operation, OperationType type, /* ... */)
    {
        // Validate, then construct. No public setters — audit is immutable after creation.
    }
}

public enum OperationType { Create, Update, Delete, ReadSensitive, SecurityEvent, Action }
public enum OperationClass { Must, Should, May }
```

### Persistence

```sql
CREATE TABLE audit_log (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,
    actor_user_id   uuid NULL,
    actor_email     text NULL,
    module          text NOT NULL,
    operation       text NOT NULL,
    operation_type  text NOT NULL,        -- Create | Update | Delete | ReadSensitive | SecurityEvent | Action
    operation_class text NOT NULL,        -- Must | Should | May
    entity_type     text NULL,
    entity_id       text NULL,
    is_success      boolean NOT NULL,
    error_key       text NULL,            -- lockey-style key
    before_state    jsonb NULL,
    after_state     jsonb NULL,
    changes         jsonb NULL,
    correlation_id  text NULL,
    ip_address      inet NULL,
    user_agent      text NULL,
    timestamp       timestamptz NOT NULL DEFAULT now(),
    metadata        jsonb NULL,
    CONSTRAINT audit_log_pkey PRIMARY KEY (id, timestamp)
) PARTITION BY RANGE (timestamp);

-- Monthly partitions managed by Hangfire job
CREATE TABLE audit_log_2026_05 PARTITION OF audit_log
    FOR VALUES FROM ('2026-05-01') TO ('2026-06-01');

CREATE INDEX ix_audit_log_tenant_id_timestamp
    ON audit_log (tenant_id, timestamp DESC);
CREATE INDEX ix_audit_log_actor_timestamp
    ON audit_log (actor_user_id, timestamp DESC);
CREATE INDEX ix_audit_log_correlation
    ON audit_log (correlation_id) WHERE correlation_id IS NOT NULL;

-- RLS
ALTER TABLE audit_log ENABLE ROW LEVEL SECURITY;
CREATE POLICY audit_log_tenant_isolation ON audit_log
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
-- Platform admin role bypasses RLS via SET role audit_admin
```

### Pipeline behavior order

In MediatR:

```
Request → Validation → Logging → AuditLogBehavior → TenantContextCheck
        → AuthorizationBehavior → TransactionBehavior → OutboxFlushBehavior → Handler
```

`AuditLogBehavior` wraps the rest. Handler exception → catch → audit failure entry →
re-throw with original stack via `ExceptionDispatchInfo`.

### Audit-coverage configuration

Per (module, operation) opt-in via `IAuditConfigService`:

```csharp
public interface IAuditConfigService
{
    Task<bool> IsEnabledAsync(string module, string operation,
        CancellationToken ct, bool defaultEnabled = true);
}
```

Backed by `audit_config` table (per-tenant override) + module-declared defaults in
`IModule.RegisterAuditDefaults()`. Commands default to `enabled: true`; queries default to
`enabled: false` (only `ReadSensitive` queries are auto-enabled).

### Phasing

| Phase | Deliverable |
|-------|-------------|
| 02 | Infrastructure: `AuditChangeTrackerInterceptor`, `IAuditStateCapture`, `AuditLogBehavior`, `IAuditStore` + `PostgresAuditStore`, `audit_log` table + first monthly partition, retention `NexoraJob` analog (`LearnStackJob`). |
| 03 | `LearnStack.Modules.Audit` domain module: `AuditEntry` aggregate, repository, integration event consumer (`ContactGdprDeleted` → audit redaction). Audit admin API endpoints (`GET /api/v1/audit/events`, filtering by tenant/user/module/operation/timestamp). |
| 06+ | Admin Studio audit UI: timeline view, filter, diff viewer, export to CSV/JSON, GDPR redaction flows. |
| 09 | Hub-side audit stream + cross-Hub-to-LearnStack audit correlation (one trace, two streams). |

## Architecture tests

Three blocker-level architecture tests are added in Phase 02:

1. `Every_TenantOwned_Command_HasAuditCoverage` — auto-discovers commands; each module's
   commands must appear in `IAuditConfigService.GetCoverageMatrix()` as MUST or SHOULD.
2. `AuditEntry_Is_AppendOnly` — `AuditEntry` does not implement `ISoftDeletable`; no
   `Update*` method on the aggregate.
3. `AuditLogBehavior_NeverBlocks_BusinessWrites` — integration test asserts that when
   `IAuditStore.SaveAsync` throws, the command result is still returned.

## Consequences

### Positive

- Standard 18 (audit-coverage) becomes mechanically enforceable.
- One row per logical operation; multi-aggregate diff in a single JSON.
- Audit data lives in the same Postgres cluster (no separate audit DB to operate); RLS
  + monthly partitions handle isolation + retention.
- Failed audit writes degrade gracefully; business operation succeeds.
- Nexora pattern proven, code transferable.

### Negative

- One more table partition to manage per month (managed by a Hangfire recurring job
  per [ADR-0028](0028-audit-log-partition-management.md)).
- Multi-entity command JSON can be large (>100 KB for bulk updates); truncation policy
  needed for very large operations.
- The PII-redaction obligation (ADR-0026 in Nexora, equivalent rule in LearnStack) applies
  to before/after JSON blobs containing learner/parent/guardian fields. A
  `ContactGdprDeleted`-equivalent (`UserGdprDeleted`) handler scans audit blobs and redacts
  matching rows.

### Neutral

- The Hub publishes a separate audit stream (Hub operator actions are not LearnStack
  tenant-scoped). The two streams correlate via `correlation_id` for cross-stream traces.

## Implementation notes

- The `Operation` field uses a stable `PascalCase` command name (e.g. `PublishCourse`,
  `EnrollLearner`). Architecture test asserts the command class name matches.
- The `before_state` / `after_state` JSON is produced by the EF interceptor using a custom
  `IEntityTypeConfiguration` reflection helper; strongly-typed IDs are flattened to their
  `Value` field so JSON is readable.
- The `Changes` JSON shape: array of `{ entityType, entityId, field, old, new }` for
  multi-entity commands; flat object `{ field, old, new }` for single-entity commands.
- Retention follows the split defined in
  [ADR-0028](0028-audit-log-partition-management.md):
  partition lifecycle (creating monthly partitions, dropping them only at the
  platform-max horizon) is owned by the `learnstack:audit:partition-management`
  Hangfire job; per-tenant retention enforcement (default 7y / 2y by operation
  class) is a separate row-level delete job
  (`learnstack:audit:retention-purge`) operating *inside* the still-attached
  partitions — never by partition drop.
- PII redaction handler: subscribes to `UserGdprDeletedIntegrationEvent`, runs a
  parameterised UPDATE on audit rows containing the matching user reference, replaces PII
  fields with `[REDACTED]` placeholder constant.

The full subsystem architecture, table schema, and operational runbook live in
[31-audit-subsystem.md](../architecture/31-audit-subsystem.md).

## References

- ADR-0003 — Tenant Isolation (audit rows are tenant-owned with RLS).
- ADR-0014 — Adopt Dapr (audit `UserGdprDeleted` event arrives via Dapr pub/sub).
- ADR-0017 — Tenant + Organization (organization_id column on audit_log).
- [18-audit-coverage.md](../standards/18-audit-coverage.md) — MUST/SHOULD/MAY matrix
  (existing) — gains its concrete implementation contract from this ADR.
- [31-audit-subsystem.md](../architecture/31-audit-subsystem.md) — architecture deep dive.
- Nexora reference implementation: `Nexora/docs/modules/tier-1-core/audit/SPEC.md`
  and `Nexora/docs/decisions/0009-audit-repository-pattern.md`.

## Amendments

### 2026-05-19 — `OperationType` extended with `PlatformAdmin`

The original Decision section defined `OperationType` as
`{ Create, Update, Delete, ReadSensitive, SecurityEvent, Action }`. In practice
[18-audit-coverage.md § Operation Types](../standards/18-audit-coverage.md) and the
"Baseline Coverage" matrix call out a distinct `platform-admin` type — any operation
performed by a platform admin (or Hub operator) against a tenant they are not a
member of. The audit consumer (compliance / regulator) needs to filter on this
distinction directly; mapping it to the generic `Action` value loses signal.

**Amended enum (binding):**

```csharp
public enum OperationType
{
    Create,
    Update,
    Delete,
    ReadSensitive,
    SecurityEvent,
    PlatformAdmin,   // added — cross-tenant operator action
    Action,          // retained — generic non-CRUD action that doesn't fit above
}
```

[31-audit-subsystem.md § 6](../architecture/31-audit-subsystem.md) and any module
that emits cross-tenant operator actions must classify them as `PlatformAdmin`.
The `Action` value remains for genuine in-tenant non-CRUD actions (e.g. recording
start when no consent change is captured).

The audit-coverage standard already treats `platform-admin` as MUST; no MUST/SHOULD/MAY
tier change. Architecture test `OperationType_Enum_Matches_Catalog` is added to assert
the enum members match the OperationType list in
[18-audit-coverage.md § Operation Types](../standards/18-audit-coverage.md).
