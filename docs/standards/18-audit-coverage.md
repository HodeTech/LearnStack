# 18 — Audit Coverage Standards

**Status:** Active
**Derives from:** [ADR-0016 Audit Log Subsystem](../decisions/0016-audit-log-subsystem.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[11-security.md](11-security.md) § Audit Log,
[01-architecture-standards.md](01-architecture-standards.md).

This standard defines **which operations must be audited**, **what an audit entry
contains**, **how long entries are retained**, and how each module signs up for its
own coverage. The `AuditEntry` aggregate itself is defined in
[02-domain-model.md](../architecture/02-domain-model.md) § Audit and the subsystem deep
dive is in [31-audit-subsystem.md](../architecture/31-audit-subsystem.md); this
document is the rule that prevents the audit story from drifting.

## Quick Rules

- A breach investigator or regulator should be able to answer "**who did what, to which
  resource, when, from where, with what outcome**" for every meaningful state change.
- If omitting an audit entry would embarrass a compliance officer, it **MUST** be
  audited.
- Audit entries are append-only, immutable, tenant-scoped (and org-scoped when the
  resource is org-scoped per [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)),
  and queryable by tenant admins for their own tenant (org admins for their org).
- Every module owns a Permission × Operation classification table for its resources.
  The matrix is reviewed when the module is built and again when it is extended.
- Coverage is configured in the **catalog** (`AuditConfig` per-tenant override of the
  module/operation MUST/SHOULD/MAY mapping) and **enforced by the MediatR
  `AuditLogBehavior`**. Modules never call `IAuditStore` directly — the pipeline does
  it for them based on the catalog.

## Operation Types

This standard uses **OperationType** to mean *what kind of operation produced the
audit row* — distinct from `OperationClass` in [ADR-0016](../decisions/0016-audit-log-subsystem.md),
which carries the MUST/SHOULD/MAY audit-coverage tier. Both fields live on
`AuditEntry` (see [31-audit-subsystem.md](../architecture/31-audit-subsystem.md)).

| OperationType | Meaning | Default audit classification (OperationClass) |
|---------------|---------|------------------------------------------------|
| `create` | New aggregate row written | SHOULD |
| `update` | Existing aggregate mutated | MUST when status, permission, money, content-publication, or consent fields change |
| `delete` | Aggregate removed or soft-deleted | MUST |
| `read-sensitive` | Read of another user's PII, financial data, learner progress, recording, or consent state | MUST |
| `security-event` | Login, MFA challenge, role grant/revoke, permission change, tenant impersonation, token revocation, RLS bypass | MUST |
| `platform-admin` | Any operation performed by a platform admin against a tenant they are not a member of (subsumes the generic `action` type from ADR-0016's initial enum; see ADR-0016 Amendment 1) | MUST |

`read-sensitive` is not "any GET request" — it's the read paths a regulator would ask about. Examples: guardian viewing a student's grades; instructor exporting a class roster with PII; admin viewing learner email list; admin downloading a recording.

## Classification Matrix Template

Every module ships this table in its module spec under `docs/modules/<module>/audit.md` (or equivalent). The matrix is part of the module's PR; reviewers refuse merges without it. The `docs/modules/` directory is created with the first module spec and does not exist yet during pre-implementation.

| Resource | create | update | delete | read-sensitive | security-event |
|----------|:------:|:------:|:------:|:--------------:|:--------------:|
| `ResourceA` | MUST | MUST | MUST | – | – |
| `ResourceB` | SHOULD | MUST | MUST | MUST | – |

Legend: **MUST** = audit entry required for every occurrence. **SHOULD** = audit by default; module may justify an opt-out per-operation. **MAY** = audit is allowed but not required. **–** = operation does not apply.

## Baseline Coverage (LearnStack Core Modules)

The following operations are MUST-audit across LearnStack regardless of which module owns them. Module matrices may add more rules but **cannot remove anything in this list**.

| Domain | MUST-audit operation |
|--------|----------------------|
| Identity | Membership created / removed (per `(user_id, tenant_id, organization_id)`); role assigned / revoked; permission set changed; invitation created / accepted / revoked; platform-admin tenant access; Hub operator access to a tenant resource. |
| Tenancy | Tenant created / suspended / deleted; organization created / archived; tenant setting changed; custom domain added / verified / removed; feature flag toggled; entitlement projection refresh (`tenancy.entitlement.refresh`); killswitch toggled. |
| Customization | `TenantContentType` / `TenantPageBlock` / `TenantLessonItemType` / `TenantLevelTaxonomy` / `TenantScoringRule` / `TenantCompletionRule` / `TenantCustomFieldDef` / `TenantTemplateLibrary` created / updated / deleted (schema changes are MUST; both `before` and `after` snapshots required). |
| Content / Pages | Page published / unpublished; content type schema changed; redirect created / changed. |
| Catalog / Learning | Course published / unpublished; CourseVersion published; lesson item replaced post-publish. |
| Enrollment | Enrollment created / suspended / cancelled / completed; entitlement granted / revoked. |
| Assessment | Attempt graded; score published; question added/removed from a published assessment. |
| Scheduling | LiveSession scheduled / rescheduled / cancelled; booking created / cancelled. |
| Classroom | Room opened / ended; participant joined / left (security-event); join token issued; consent state changed. |
| Recording | Recording started / stopped; recording downloaded; retention policy changed; legal hold applied / removed. |
| Billing | Order paid / refunded; subscription created / cancelled; payment provider account changed. |
| Hub contract | Inbound Hub command received (`tenancy.tenant.create-from-hub`, `tenancy.entitlement.push`); outbound usage report (`platform.usage.report`); license verification result. |
| Notifications | Template changed; outbound delivery to a recipient (SHOULD for non-PII channels; MUST for password reset / invitation / billing). |
| Integrations | External provider credential created / rotated / revoked; webhook secret rotated. |
| Security | Login failure burst beyond rate limit; MFA challenge failed; admin override of any guard; mTLS / signed-JWT / HMAC verification failure on `/api/internal/*`. |

There are **no vertical modules**. Tenant-specific extensions land as data via the
Customization aggregates and inherit the MUST-audit rules above for schema changes.

## Audit Entry Payload Contract

Every audit entry conforms to this shape; deviations require a one-line note in the module spec and a code-review approval.

```json
{
  "id": "01H...",
  "occurredAt": "2026-05-18T12:34:56.789Z",
  "tenantId": "ten_01H...",
  "organizationId": "org_01H...",
  "actor": {
    "userId": "usr_01J...",
    "membershipId": "mbr_01J...",
    "roles": ["instructor"],
    "platformAdmin": false,
    "hubOperator": false
  },
  "operation": {
    "module": "enrollment",
    "resource": "Enrollment",
    "resourceId": "enr_01K...",
    "action": "create",
    "class": "create",
    "operationType": "command"
  },
  "request": {
    "correlationId": "01H...",
    "sourceIp": "203.0.113.4",
    "userAgent": "Mozilla/5.0 ...",
    "route": "POST /v1/enrollments"
  },
  "outcome": "success",
  "before": null,
  "after": { "courseVersionId": "...", "userId": "...", "status": "active" },
  "changes": [
    { "path": "/status", "before": null, "after": "active" }
  ],
  "reason": null,
  "metadata": { "source": "tenant-admin-studio" }
}
```

Rules:

- `before` and `after` are **mandatory** for `update` on permission, money, content-publication, recording-policy, and consent fields. Snapshots are JSON, redacted for PII fields the module marks as `[PiiSensitive]`.
- `outcome` is one of `success`, `denied`, `failed`. `denied` is used when an authorisation check rejects the operation — these MUST be audited so we can detect probing.
- `reason` is required when the operator is a platform admin acting on a tenant they are not a member of. Free-text, surfaced in the tenant admin's audit view.
- `correlationId` matches the trace id in logs and the value in the Problem Details response for failures.
- PII fields are listed in the module's audit spec; the audit pipeline strips them before persistence.

## Storage

- One global `audit_log` table partitioned by `occurred_at` **monthly from day one**
  ([ADR-0016](../decisions/0016-audit-log-subsystem.md)). RLS isolates rows by
  `tenant_id`; the partition strategy serves retention pruning.
- Append-only; the `AuditEntry` aggregate inherits `Entity<TId>` **not**
  `AuditableEntity<T>`. No `UPDATE` or `DELETE` in application code; CI rejects
  `UpdateAsync` / `DeleteAsync` calls against `AuditEntry` and a Postgres trigger
  rejects them at the database layer too.
- Tenant admins query their own tenant's entries through a paginated, indexed view.
  Org admins additionally filter by `organization_id`. Platform admins query across
  tenants through a separate read role. Hub operators are platform admins from this
  surface's perspective and their reads are themselves audited.
- The audit log is **never** the source of truth for application state; downstream
  consumers project from integration events, not from audit entries.
- Capture pipeline: `AuditChangeTrackerInterceptor` (EF Core SaveChanges interceptor)
  → `IAuditStateCapture` (before/after/changes JSON capture) →
  `AuditLogBehavior` (MediatR pipeline behavior that enriches with actor + correlation
  + operation metadata and writes through `IAuditStore`). Modules see none of this
  plumbing.

## Retention

| Class | Default retention | Notes |
|-------|-------------------|-------|
| `security-event` | **7 years** | KVKK / GDPR / sector compliance baseline |
| `platform-admin` | **7 years** | Cross-tenant operations |
| Financial (`Billing.*`) | **7 years** | Tax and invoice retention |
| `read-sensitive` | **2 years** | Sufficient for typical investigation cycles |
| `create` / `update` / `delete` (other domains) | **2 years** | Tenant-configurable down to 6 months or up to 7 years |
| Recording-policy and consent changes | **7 years** | Lives with the recording itself if longer. **Note:** this is the retention of the *audit-log entry* about a recording policy or consent change — not the retention of the recording **file** itself. Recording files follow the per-tenant retention policy declared in [16-media-pipeline.md § Recordings](../architecture/16-media-pipeline.md) (default 30 days; tenant-configurable up to the platform cap). The two retentions are independent: the audit entry persists for compliance reconstruction even after the recording file is purged. |

A tenant cannot reduce retention below the platform-defined floor for `security-event`, `platform-admin`, or financial entries. Retention is enforced by a daily Hangfire job; deletions are batched, logged, and themselves audited (`security-event`).

## Required Behaviours

- Every command handler that mutates a MUST-audit resource writes the audit entry in the **same transaction** as the state change (via the outbox if dispatching to consumers, but the audit row itself is local).
- Failed `denied` outcomes are audited even though no state changed.
- Background jobs that mutate MUST-audit resources receive the `actor` via job payload (operator id, or the seed of a system actor with a stable id) and write the entry under that identity.
- Integration event handlers that mutate state are treated as actors of type `system` and audited accordingly.
- Platform-bypass code paths (`IgnoreQueryFilters()` etc.) write a `security-event` entry on every invocation.

## Architecture Tests

The following tests live in `LearnStack.Tests.Architecture` and run on every PR:

- Every handler that calls `SaveChanges` against a MUST-audit aggregate **also** writes
  an audit row in the same transaction (instrumented via the EF interceptor).
- The `AuditEntry` aggregate is never updated or deleted by application code (Roslyn
  analyzer + Postgres trigger).
- `IAuditStore` is the only sanctioned write path: `Modules_Do_Not_Write_AuditLog_Directly`.
- Every module ships an `audit.md` matrix at the path expected by the doc-coverage
  check.
- Outbox dispatcher attaches `actor` and `correlation_id` to every event.
- `AuditEntry_Inherits_Entity_Not_AuditableEntity` ensures the audit aggregate is
  append-only by inheritance.

## Tenant-Admin Visibility

A tenant admin's audit view supports:

- Filter by actor user, module, resource, action, outcome, date range.
- Detail view shows `before`, `after`, `request`, `reason`.
- CSV export limited by retention class (security-event entries do not export to non-admin roles).
- Search across `resource`, `route`, `correlationId`, `userId`.

Platform admins additionally see cross-tenant entries through a separate route guarded by platform-admin scope and rate-limited.

## Forbidden

- Writing an audit entry **after** the controlling transaction commits (the entry can be lost on failure).
- Truncating `before`/`after` snapshots silently — if the diff is too large, store an external pointer (`audit_blob_id`) but never an empty object.
- Mutating an audit row in place.
- Storing audit entries in the same module's read schema (the audit aggregate is global / cross-module).
- Reducing retention without an ADR.
- Skipping the matrix in a module spec.

## References

- [ADR-0016 Audit Log Subsystem](../decisions/0016-audit-log-subsystem.md) — capture
  pipeline, retention policy, partitioning strategy.
- [ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md) —
  `organization_id` in audit entries.
- [31-audit-subsystem.md](../architecture/31-audit-subsystem.md) — subsystem deep dive
  (interceptor, state capture, MediatR behavior, retention job).
- [11-security.md](11-security.md) — security headers, authorization, tenant isolation,
  secrets.
- [02-domain-model.md](../architecture/02-domain-model.md) § Audit — `AuditEntry`
  aggregate definition.
- [13-identity-and-auth.md](../architecture/13-identity-and-auth.md) — Keycloak vs
  LearnStack audit split.
- [10-observability.md](10-observability.md) — correlation id propagation, redaction.
- [20-infrastructure-stack.md](20-infrastructure-stack.md) — `IAuditStore` rules.
