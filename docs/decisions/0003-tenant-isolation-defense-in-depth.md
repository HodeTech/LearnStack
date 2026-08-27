# ADR 0003: Tenant Isolation Defense in Depth

## Status

Accepted (Amendment 1: 2026-05-18 — adds Organization scope; Amendment 2: 2026-05-19 —
identity row terminology; **Amendment 3: 2026-08-08 — corrects the RLS policy template
and adds the database role model**; see bottom of document)

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

**RLS policy template for org-scoped tables:** superseded by
[Amendment 3](#amendment-3--rls-policy-template-correction-and-database-role-model-2026-08-08).
The template originally published here created a **second permissive policy**, which
PostgreSQL combines with `OR`. The canonical template now lives in exactly one place:
[Database Standards § Tenant-Owned and Organization-Scoped Tables](../standards/05-database.md).

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

---

## Amendment 3 — RLS policy template correction and database role model (2026-08-08)

Amendment 1 published an RLS template that **does not deliver the isolation this ADR
decides**. This amendment corrects the mechanism. The Decision — defense in depth with
PostgreSQL RLS as one of its layers — is unchanged; what changes is the SQL that
implements it, plus two layers that were never specified at all.

### What was wrong

`CREATE POLICY` produces a **permissive** policy by default, and PostgreSQL combines
multiple permissive policies for the same command with `OR`. Only `AS RESTRICTIVE`
policies are combined with `AND`.

Amendment 1's template created a separate `<table>_organization_isolation` policy
alongside `<table>_tenant_isolation`. The intended predicate was:

```text
tenant matches AND (organization matches OR row is tenant-wide)
```

The predicate PostgreSQL actually evaluated was:

```text
tenant matches OR organization matches OR row is tenant-wide OR scope = 'tenant'
```

`organization_id IS NULL` satisfies the organization policy on its own — and per
[ADR-0017](0017-tenant-organization-hierarchy.md), a null `organization_id` is the
**defined representation of a tenant-wide row**. Every tenant-wide row in an
org-scoped table was therefore readable by **every tenant**.

Three further gaps compounded it:

- **No `FORCE ROW LEVEL SECURITY`.** A table's owner bypasses that table's policies
  unless the table forces them. The default Entity Framework Core setup — migrations
  and the application connecting as the same role — puts the application in exactly
  that position, so the RLS layer could have shipped inert.
- **No `WITH CHECK`.** A `USING` clause constrains reads. Without an explicit
  `WITH CHECK`, writes are unconstrained for the commands where the two differ.
- **No database role model.** No document named the role the application connects as,
  so nothing prevented it from being the owner or from holding `BYPASSRLS`.

### The correction

The canonical, corrected template lives in exactly one place —
[Database Standards § Tenant-Owned and Organization-Scoped Tables](../standards/05-database.md).
[Tenant Isolation](../architecture/09-tenant-isolation.md) and
[ADR-0017](0017-tenant-organization-hierarchy.md) link to it instead of repeating it.
Its binding properties:

1. **One permissive policy per table, one `AND`-ed predicate.** Tenant and organization
   scope are evaluated in a single policy. Splitting them across two permissive policies
   is what caused the defect; a second policy may only be added `AS RESTRICTIVE`, which
   combines with `AND` and therefore cannot widen.
2. **Two `AS RESTRICTIVE` write guards on org-scoped tables**, one `FOR UPDATE` and one
   `FOR DELETE`. The `app.scope = 'tenant'` term is a *read* hatch, but `USING` also
   selects which rows an `UPDATE` may target, and for `DELETE` it is the only gate —
   PostgreSQL has no `WITH CHECK` for `DELETE`. Without the guards a tenant-scope
   reporting session can delete another organization's rows or reassign them to itself.
3. **`ENABLE` *and* `FORCE` row level security** on every tenant-owned table.
4. **An explicit `WITH CHECK`** that constrains writes to the caller's tenant, and to
   either the caller's organization or a tenant-wide row.
5. **Every `current_setting` read wrapped in `NULLIF(..., '')`.** A dotted GUC becomes a
   session placeholder the first time it is assigned and its reset value is the empty
   string, not "undefined" — so on a pooled connection whose previous transaction set
   the value and whose next one does not, `''::uuid` *raises* instead of filtering.
   `NULLIF` turns that into `NULL`, and a `NULL` policy result is false for both
   `USING` and `WITH CHECK`. This is what makes
   `Unsetting_tenant_context_returns_zero_rows_through_RLS` able to pass at all.
6. **Composite foreign keys on `tenant_id` between tenant-owned tables.** PostgreSQL
   evaluates referential integrity with Row Level Security bypassed, so a single-column
   foreign key lets a row in one tenant reference a row in another — invisibly, because
   no policy runs. See
   [Database Standards § Foreign keys between tenant-owned tables](../standards/05-database.md). The `app.scope = 'tenant'`
   escape hatch applies to reads only — a tenant-scope reporting query may read across
   organizations, but no query may write outside its organization.

### Which tables the template applies to

> **Amendment note (2026-08-27).** The enumeration below was written before
> `idempotency_keys` existed and before the tenant-owned class was split into
> org-scoped and tenant-wide. **[Database Standards § Table classes](../standards/05-database.md)
> is the single authority for the assignment**; this section keeps the *rule* —
> which class a table belongs to and why — and no longer keeps the list, for the
> same reason the template itself lives in one file: a list copied into three
> documents drifts in three directions, and this copy already had.

The corrected template governs **tenant-owned** tables. Two classes sit outside it, and
both are enumerated rather than left to judgement:

| Class | Rule | Tables |
|---|---|---|
| Tenant-owned | The corrected template, with the organization term present only on tables that carry an `organization_id` | every domain table, plus the Phase 02a tenancy set — enumerated and kept current in [Database Standards § Table classes](../standards/05-database.md), which splits it into org-scoped and tenant-wide |
| Tenant-owned, self-keyed | The corrected template with the tenant term keyed on `id`, because the primary key *is* the tenant id | `tenants` |
| Platform-scoped | `ENABLE` + `FORCE`, role-qualified per-command policies; the read is widened by an explicitly declared non-tenant predicate, writes stay tenant-keyed | `platform_host_to_tenant` |

**A table is platform-scoped only when it is read before the tenant is known.** Exactly
one table meets that test: `IHostToTenantResolver` reads `platform_host_to_tenant` in
order to *determine* the tenant, so under a tenant-keyed predicate it would return zero
rows and no tenant could ever resolve. Its read policy is keyed on a GUC the resolver
declares (`app.resolving_host`) or on `app.tenant_id` for a tenant listing its own hosts;
its writes stay tenant-keyed, because every writer knows its tenant. Row security is
never *disabled* on any of these tables — a table without `ENABLE ROW LEVEL SECURITY` is
indistinguishable from one nobody thought about.

`platform_entitlement_cache` is **not** platform-scoped despite its name. Every read
goes through `IFeatureFlags`, which resolves the tenant from `ITenantContext` and throws
when there is none; every write goes through `IEntitlementProvider.RefreshAsync`, driven
by `PUT /api/internal/tenants/{id}/entitlements`, which carries the tenant id in its
path. It keeps the tenant-owned template.

No statement of the form "writes are reserved to the migration and provisioning path" is
an implementable control: PostgreSQL grants privileges to **roles**, and every handler in
the API process runs as the same role. Grants are stated as a role × table × privilege
matrix; code-path confinement is a separate layer carried by architecture tests. The
matrix and the tests both live in
[Database Standards § Database roles](../standards/05-database.md).

### Database role model

Four roles, four **separate login credentials**, four connection strings. The model is
closed; a fifth role requires a new ADR.

| Role | Connection string | Used by | RLS posture |
|-------|---|---------|-------------|
| `learnstack_migration` | `ConnectionStrings:Migration`, present only in the deploy job | EF Core migrations. **Owns** every table. | `NOBYPASSRLS`; `FORCE ROW LEVEL SECURITY` means ownership grants no bypass |
| `learnstack_app` | `ConnectionStrings:Default` | The application's runtime connection. Not an owner. | `NOBYPASSRLS` |
| `learnstack_platform` | `ConnectionStrings:PlatformAdmin` | Cross-tenant platform-admin operations, entered only through the audited `EnterPlatformAdminScope(reason)` path | `BYPASSRLS` |
| `learnstack_outbox_admin` | `ConnectionStrings:OutboxDispatcher` | The outbox dispatcher, per [Events and Outbox](../architecture/15-event-and-outbox.md) | `BYPASSRLS` |

`BYPASSRLS` bypasses **policies, not `GRANT`s**. Grant scope is therefore the only thing
bounding the two bypass roles, which is why no privilege is granted to `PUBLIC` and no
`ALTER DEFAULT PRIVILEGES` grant exists.

**`EnterPlatformAdminScope(reason)` reaches `learnstack_platform` by a second connection,
not by `SET ROLE`.** `learnstack_app` is not a member of `learnstack_platform`. A
membership grant would make the bypass a standing capability of the application role,
reachable from any code path that emits raw SQL; a plain `SET ROLE` survives `COMMIT` and
would persist on a PgBouncer transaction-pooled server connection into the next tenant's
request; and per-role settings such as `statement_timeout` are applied at login and do
not follow a `SET ROLE`, which would defeat the per-role timeout split
[Phase 11](../roadmap/phase-11-production-hardening.md) builds. The composition root owns
a second, separately-credentialed data source that only
`LearnStack.Infrastructure.MultiTenancy.PlatformAdminScope` may resolve.

### Session-variable placement

The RLS predicates read `app.tenant_id` and `app.organization_id`, set with
`set_config(..., true)` / `SET LOCAL`. Both are **transaction-local**: they are
discarded when the transaction ends. They must therefore be set **inside the ambient
transaction**, after it opens — not in a MediatR behavior that runs before the
transaction begins, and not in a connection interceptor, which fires when the
connection opens rather than when the transaction starts.
[Security Standards § Tenant Context](../standards/11-security.md) is the authority
for this placement.

### Test requirement

Isolation tests connect as **`learnstack_app`**. A test that connects as the owner or
as a `BYPASSRLS` role passes even when every policy is inert, and therefore proves
nothing. The suite must include, at minimum:

- a tenant-wide (`organization_id IS NULL`) row of tenant B is invisible to tenant A —
  the exact case Amendment 1's template leaked;
- an organization-scoped row of organization Y is invisible inside organization X of
  the same tenant;
- clearing the tenant context returns zero rows rather than all rows;
- a write carrying a foreign `tenant_id` is rejected by `WITH CHECK`.

### Where this lands

The corrected template **and the four-role model** are applied by the **first
migration**, in [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md). They are
one deliverable, not two: the runtime grants to `learnstack_app` are DDL in the same
migration as the policies, and `FORCE ROW LEVEL SECURITY` has no effect worth having
while the connecting role is still the owner. No `ALTER TABLE … OWNER TO` appears in a
migration — migrations connect **as** `learnstack_migration`, so every table they create
is already owned by it, and an explicit `OWNER TO` is a sign the migration is running as
the wrong role ([Database Standards § Database roles](../standards/05-database.md)).

The transaction-local session variables (`SET LOCAL` inside the ambient transaction)
and the isolation suite land in **Packet 7**, with `TenantResolverMiddleware`. Between
the two packets no tenant-owned table is read on a request path: with the policies
live and `app.tenant_id` unset, every predicate evaluates to `NULL` and every query
correctly returns zero rows. Any seeding Packet 6 performs runs inside a transaction
that sets `app.tenant_id` for the tenant being created.

No migration may be written against the superseded template.

