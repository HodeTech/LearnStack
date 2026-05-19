# ADR 0003: Tenant Isolation Defense in Depth

## Status

Accepted (Amendment 1: 2026-05-18 — adds Organization scope; see bottom of document)

## Decision

LearnStack uses defense-in-depth tenant isolation:

- Application tenant context.
- EF Core global query filters.
- PostgreSQL Row Level Security for tenant-owned tables.
- Architecture tests that detect unprotected tenant-owned entities.
- Explicit platform-admin paths for cross-tenant operations.
- Tenant context propagation for background jobs and outbox processing.

## Context

LearnStack uses a shared database and shared schema initially. Query filters alone are not enough because raw SQL, forgotten filters, `IgnoreQueryFilters()`, or background job mistakes can leak tenant data.

## Consequences

- Tenant-owned tables must include `tenant_id`.
- Tenant context must be set per request and per background job.
- Platform-admin operations must be explicit and audited.
- PostgreSQL RLS policies are required before production.
- Tests must fail when tenant-owned entities lack protection.

---

## Amendment 1 — Organization scope (2026-05-18)

Per [ADR-0017](0017-tenant-organization-hierarchy.md), LearnStack adopts a two-level
hierarchy (Tenant + Organization). This amendment extends the original defense-in-depth
table without altering the tenant-level guarantees.

**Extended defense-in-depth layers:**

| Layer | Mechanism |
|-------|-----------|
| Tenant isolation | `tenant_id` column + EF query filter + RLS policy + architecture tests (unchanged from original decision) |
| **Organization filter (new)** | `organization_id` column on org-scoped entities + EF query filter + RLS policy + architecture test |
| Identity | Keycloak realm-per-tenant + `organization_id` JWT claim populated from active org |
| Cache | Cache keys auto-prefixed `{tenant_id}:{organization_id}:{key}` when org context set; `{tenant_id}:{key}` otherwise |
| Files (SeaweedFS) | Object key prefix `tenants/{tenant_id}/organizations/{org_id}/...` when org-scoped; `tenants/{tenant_id}/...` when tenant-wide |
| Search (Meilisearch) | Query filter `tenant_id = X AND (organization_id = Y OR organization_id IS NULL)` for org-context queries |
| Jobs (Hangfire) | Job payload carries `TenantId` (mandatory) + `OrganizationId?` (when applicable) |
| Audit | Every audit row carries `tenant_id` (mandatory) + `organization_id?` (when applicable) — ADR-0016 |

**RLS policy template for org-scoped tables:**

```sql
CREATE POLICY <table>_organization_isolation ON <table>
    USING (
        organization_id IS NULL
        OR organization_id = current_setting('app.organization_id', true)::uuid
        OR current_setting('app.scope', true) = 'tenant'
    );
```

**Org-scope opt-in.** A tenant-owned entity may be **tenant-wide** (no `organization_id`)
or **org-scoped** (`organization_id` populated). Entities mark themselves via
`[OrganizationScoped]` attribute; architecture test enforces the column + filter + policy.

**Default org.** A tenant without explicit orgs has one default org auto-created at tenant
provisioning. Single-org tenants experience no UX difference (org switcher hidden).

The original tenant-level guarantees, RLS-from-day-one rule, and architecture tests for
tenant isolation remain unchanged.

---

## Amendment 2 — Identity row terminology (2026-05-19)

The "Identity" row in Amendment 1's defense-in-depth table reads "Keycloak
realm-per-tenant + `organization_id` JWT claim". Read this as a reference to the
**realm-per-tenant opt-in** described in
[ADR-0004 Amendment 1](0004-authentication-strategy.md) — it is **not** the default.

The default Keycloak strategy (per ADR-0004) is **single-realm `learnstack` with a
`tenant_id` JWT claim**; realm-per-tenant is an enterprise opt-in for compliance-
driven isolation. Both strategies satisfy the defense-in-depth requirement of this
ADR — the row was written assuming the realm-per-tenant variant; the live
architecture guide [09-tenant-isolation.md](../architecture/09-tenant-isolation.md)
reflects the corrected wording.

This is a documentation clarification; the Decision is unchanged.

