# 18 — Audit Coverage Standards

**Status:** Active
**Derives from:** [11-security.md](11-security.md) § Audit Log, [01-architecture-standards.md](01-architecture-standards.md).

This standard defines **which operations must be audited**, **what an audit entry contains**, **how long entries are retained**, and how each module signs up for its own coverage. The `AuditLog` aggregate itself is defined in [02-domain-model.md](../architecture/02-domain-model.md); this document is the rule that prevents the audit story from drifting.

## Quick Rules

- A breach investigator or regulator should be able to answer "**who did what, to which resource, when, from where, with what outcome**" for every meaningful state change.
- If omitting an audit entry would embarrass a compliance officer, it **MUST** be audited.
- Audit entries are append-only, immutable, tenant-scoped, and queryable by tenant admins for their own tenant.
- Every module owns a Permission × Operation classification table for its resources. The matrix is reviewed when the module is built and again when it is extended.

## Operation Classes

| Class | Meaning | Default classification |
|-------|---------|------------------------|
| `create` | New aggregate row written | SHOULD audit |
| `update` | Existing aggregate mutated | MUST audit when status, permission, money, content-publication, or consent fields change |
| `delete` | Aggregate removed or soft-deleted | MUST audit |
| `read-sensitive` | Read of another user's PII, financial data, learner progress, recording, or consent state | MUST audit |
| `security-event` | Login, MFA challenge, role grant/revoke, permission change, tenant impersonation, token revocation, RLS bypass | MUST audit |
| `platform-admin` | Any operation performed by a platform admin against a tenant they are not a member of | MUST audit |

`read-sensitive` is not "any GET request" — it's the read paths a regulator would ask about. Examples: guardian viewing a student's grades; instructor exporting a class roster with PII; admin viewing learner email list; admin downloading a recording.

## Classification Matrix Template

Every module ships this table in its module spec under `docs/modules/<module>/audit.md` (or equivalent). The matrix is part of the module's PR; reviewers refuse merges without it.

| Resource | create | update | delete | read-sensitive | security-event |
|----------|:------:|:------:|:------:|:--------------:|:--------------:|
| `ResourceA` | MUST | MUST | MUST | – | – |
| `ResourceB` | SHOULD | MUST | MUST | MUST | – |

Legend: **MUST** = audit entry required for every occurrence. **SHOULD** = audit by default; module may justify an opt-out per-operation. **MAY** = audit is allowed but not required. **–** = operation does not apply.

## Baseline Coverage (LearnStack Core Modules)

The following operations are MUST-audit across LearnStack regardless of which module owns them. Module matrices may add more rules but **cannot remove anything in this list**.

| Domain | MUST-audit operation |
|--------|----------------------|
| Identity | Membership created / removed; role assigned / revoked; permission set changed; invitation created / accepted / revoked; platform-admin tenant access. |
| Tenancy | Tenant created / suspended / deleted; tenant setting changed; custom domain added / verified / removed; feature flag toggled. |
| Content / Pages | Page published / unpublished; content type schema changed; redirect created / changed. |
| Catalog / Learning | Course published / unpublished; CourseVersion published; lesson item replaced post-publish. |
| Enrollment | Enrollment created / suspended / cancelled / completed; entitlement granted / revoked. |
| Assessment | Attempt graded; score published; question added/removed from a published assessment. |
| Scheduling | LiveSession scheduled / rescheduled / cancelled; booking created / cancelled. |
| Classroom | Room opened / ended; participant joined / left (security-event); join token issued; consent state changed. |
| Recording | Recording started / stopped; recording downloaded; retention policy changed; legal hold applied / removed. |
| Billing | Order paid / refunded; subscription created / cancelled; payment provider account changed. |
| Notifications | Template changed; outbound delivery to a recipient (SHOULD for non-PII channels; MUST for password reset / invitation / billing). |
| Integrations | External provider credential created / rotated / revoked; webhook secret rotated. |
| Security | Login failure burst beyond rate limit; MFA challenge failed; admin override of any guard. |

Vertical modules (English Learning, etc.) extend this table with their own MUST/SHOULD entries; they never relax a core rule.

## Audit Entry Payload Contract

Every audit entry conforms to this shape; deviations require a one-line note in the module spec and a code-review approval.

```json
{
  "id": "01H...",
  "occurredAt": "2026-05-14T12:34:56.789Z",
  "tenantId": "ten_01H...",
  "actor": {
    "userId": "usr_01J...",
    "membershipId": "mbr_01J...",
    "roles": ["instructor"],
    "platformAdmin": false
  },
  "operation": {
    "module": "enrollment",
    "resource": "Enrollment",
    "resourceId": "enr_01K...",
    "action": "create",
    "class": "create"
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
  "reason": null
}
```

Rules:

- `before` and `after` are **mandatory** for `update` on permission, money, content-publication, recording-policy, and consent fields. Snapshots are JSON, redacted for PII fields the module marks as `[PiiSensitive]`.
- `outcome` is one of `success`, `denied`, `failed`. `denied` is used when an authorisation check rejects the operation — these MUST be audited so we can detect probing.
- `reason` is required when the operator is a platform admin acting on a tenant they are not a member of. Free-text, surfaced in the tenant admin's audit view.
- `correlationId` matches the trace id in logs and the value in the Problem Details response for failures.
- PII fields are listed in the module's audit spec; the audit pipeline strips them before persistence.

## Storage

- One global `audit_log` table; partitioned by `tenant_id` then `occurred_at` (monthly) once volume warrants it.
- Append-only; no `UPDATE` or `DELETE` in application code; CI rejects `UpdateAsync` / `DeleteAsync` calls against the audit aggregate.
- Tenant admins query their own tenant's entries through a paginated, indexed view. Platform admins query across tenants through a separate read role.
- The audit log is **never** the source of truth for application state; downstream consumers project from integration events, not from audit entries.

## Retention

| Class | Default retention | Notes |
|-------|-------------------|-------|
| `security-event` | **7 years** | KVKK / GDPR / sector compliance baseline |
| `platform-admin` | **7 years** | Cross-tenant operations |
| Financial (`Billing.*`) | **7 years** | Tax and invoice retention |
| `read-sensitive` | **2 years** | Sufficient for typical investigation cycles |
| `create` / `update` / `delete` (other domains) | **2 years** | Tenant-configurable down to 6 months or up to 7 years |
| Recording-policy and consent changes | **7 years** | Lives with the recording itself if longer |

A tenant cannot reduce retention below the platform-defined floor for `security-event`, `platform-admin`, or financial entries. Retention is enforced by a daily Hangfire job; deletions are batched, logged, and themselves audited (`security-event`).

## Required Behaviours

- Every command handler that mutates a MUST-audit resource writes the audit entry in the **same transaction** as the state change (via the outbox if dispatching to consumers, but the audit row itself is local).
- Failed `denied` outcomes are audited even though no state changed.
- Background jobs that mutate MUST-audit resources receive the `actor` via job payload (operator id, or the seed of a system actor with a stable id) and write the entry under that identity.
- Integration event handlers that mutate state are treated as actors of type `system` and audited accordingly.
- Platform-bypass code paths (`IgnoreQueryFilters()` etc.) write a `security-event` entry on every invocation.

## Architecture Tests

The following tests live in `LearnStack.Tests.Architecture` and run on every PR:

- Every handler that calls `SaveChanges` against a MUST-audit aggregate **also** writes an audit row in the same transaction (instrumented via the EF interceptor).
- The audit aggregate is never updated or deleted by application code (Roslyn analyzer).
- Every module ships an `audit.md` matrix at the path expected by the doc-coverage check.
- Outbox dispatcher attaches `actor` and `correlation_id` to every event.

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

- [11-security.md](11-security.md) — security headers, authorization, tenant isolation, secrets.
- [02-domain-model.md](../architecture/02-domain-model.md) — `AuditLog` aggregate definition.
- [13-identity-and-auth.md](../architecture/13-identity-and-auth.md) — Keycloak vs LearnStack audit split.
- [10-observability.md](10-observability.md) — correlation id propagation, redaction.
