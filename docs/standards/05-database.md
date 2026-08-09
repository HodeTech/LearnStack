# 05 — Database Standards

**Status:** Active
**Derives from:** [ADR-0002 Initial Architecture](../decisions/0002-initial-architecture.md)
(Amendments 1 + 2),
[ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
(Amendment 1: Organization Scope),
[ADR-0006 Events and Outbox](../decisions/0006-events-and-outbox.md)
(Amendment 1: Dapr pub/sub dispatch transport),
[ADR-0014 Adopt Dapr](../decisions/0014-adopt-dapr.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0031 PostgreSQL — Start on 18.x](../decisions/0031-postgresql-major-version.md).

PostgreSQL schema, EF Core, and migration conventions.

## Database

- **PostgreSQL 18+** in all environments.
- One database per environment; single schema (`public`).
- One `DbContext` per module (not one global).
- Schema migrations live with the owning module.

## Naming

| Element | Convention | Example |
|---------|------------|---------|
| Tables | `snake_case`, plural | `course_versions` |
| Columns | `snake_case` | `published_at` |
| Primary keys | `id` (`uuid`) | `id` |
| Foreign keys | `<entity>_id` | `course_id` |
| Indexes | `ix_<table>_<columns>` | `ix_lessons_tenant_id_course_id` |
| Unique indexes | `ux_<table>_<columns>` | `ux_course_translations_tenant_id_locale_slug` |
| Check constraints | `ck_<table>_<rule>` | `ck_course_translations_slug_format` |
| RLS policies (tenant-owned) | `<table>_isolation` — exactly one permissive policy | `courses_isolation` |
| RLS policies (restrictive guards) | `<table>_org_write_guard` / `<table>_org_delete_guard` | `courses_org_write_guard` |
| RLS policies (platform-scoped) | `<table>_read` / `<table>_insert` / `<table>_update` / `<table>_delete` | `platform_host_to_tenant_read` |
| Triggers | `tg_<table>_<purpose>` | `tg_courses_set_updated_at` |
| Functions | `fn_<purpose>` | `fn_set_updated_at` |
| Read-model tables | `public_<module>_<concept>` | `public_education_course_summaries` |
| Outbox | `outbox_messages` (global) | |

`learnstack_` prefix reserved for system-wide objects (roles, extensions).

## Tenant-Owned and Organization-Scoped Tables

Every tenant-owned table **must** carry `tenant_id` + one RLS policy + an EF filter.
Org-scoped tables additionally carry an `organization_id` column (nullable when the row
may be tenant-wide), and the organization term is `AND`-ed into that same policy per
[ADR-0017](../decisions/0017-tenant-organization-hierarchy.md) and
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md).

> **One permissive policy per table.** The natural instinct is to write a tenant policy
> and an organization policy. Do not. `CREATE POLICY` is permissive by default and
> PostgreSQL combines permissive policies with `OR`, so a second permissive policy
> **widens** access instead of narrowing it. This is not theoretical — it is the defect
> ADR-0003 Amendment 3 corrects, and it made every tenant-wide row visible to every
> tenant. A second policy is allowed only when it is declared `AS RESTRICTIVE`, which
> combines with `AND` and therefore cannot widen anything. The template below uses
> exactly one of each.

```sql
CREATE TABLE courses (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,                -- org-scoped; null = tenant-wide
    slug_key        text NOT NULL,            -- stable authoring handle; NOT routable
    -- No title, no description, no slug: translatable columns live in
    -- course_translations per ADR-0008. See § Translation satellite tables.
    -- ... domain columns ...
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid NOT NULL,
    row_version     bigint NOT NULL DEFAULT 0,
    CONSTRAINT ux_courses_tenant_id_slug_key UNIQUE (tenant_id, slug_key),
    -- Composite unique on (tenant_id, id) exists solely so child tables can
    -- carry a composite FK. See § Foreign keys between tenant-owned tables.
    CONSTRAINT ux_courses_tenant_id_id   UNIQUE (tenant_id, id)
);

-- Enable *and* force: without FORCE, the table owner bypasses its own policies.
ALTER TABLE courses ENABLE ROW LEVEL SECURITY;
ALTER TABLE courses FORCE  ROW LEVEL SECURITY;

-- ONE permissive policy, ONE AND-ed predicate. Two permissive policies would be
-- OR-ed together and a tenant-wide row (organization_id IS NULL) would satisfy
-- the organization half on its own — visible to every tenant.
--
-- NULLIF(..., '') is not decoration. A customized (dotted) GUC becomes a session
-- placeholder the first time it is assigned, and its reset value is the empty
-- string, not "undefined". On a pooled connection whose previous transaction set
-- app.tenant_id and whose next one forgets to, current_setting(..., true) returns
-- '' and ''::uuid RAISES instead of filtering. NULLIF turns that into NULL, and a
-- NULL policy result is false for both USING and WITH CHECK — fail-closed for the
-- never-set path and the reset path alike.
CREATE POLICY courses_isolation ON courses
    USING (
        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            organization_id IS NULL                                                                -- tenant-wide row
            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid    -- caller's org
            OR current_setting('app.scope', true) = 'tenant'                                       -- tenant-scope READ
        )
    )
    WITH CHECK (
        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (
            organization_id IS NULL
            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
        )
    );

-- The tenant-scope hatch above widens READS across organizations, which is what
-- cross-org reporting needs. It must not widen writes — but USING is also what
-- selects the rows an UPDATE may target, and for DELETE it is the ONLY gate
-- (PostgreSQL has no WITH CHECK for DELETE). Without the restrictive policy
-- below, a tenant-scope session could delete another organization's rows, or
-- reassign them to itself.
CREATE POLICY courses_org_write_guard ON courses
    AS RESTRICTIVE FOR UPDATE
    USING (
        organization_id IS NULL
        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
    );

CREATE POLICY courses_org_delete_guard ON courses
    AS RESTRICTIVE FOR DELETE
    USING (
        organization_id IS NULL
        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
    );

CREATE INDEX ix_courses_tenant_id ON courses (tenant_id);
CREATE INDEX ix_courses_organization_id ON courses (organization_id)
    WHERE organization_id IS NOT NULL;
```

### Foreign keys between tenant-owned tables

**Every foreign key between two tenant-owned tables is composite on `tenant_id`.**

PostgreSQL evaluates referential integrity as a security-restricted operation on behalf
of the table owner, and RI checks are **not subject to Row Level Security**. A
single-column `lessons.course_id → courses.id` therefore lets a row in tenant A
reference a row in tenant B: the child's own `WITH CHECK` passes because its
`tenant_id` is A's, and the FK check passes because it can see B's row. The result is a
permanent cross-tenant reference in the one system whose top-line threat is
cross-tenant leakage — and it is invisible to every policy, because no policy ran.

```sql
CREATE TABLE lessons (
    id          uuid PRIMARY KEY,
    tenant_id   uuid NOT NULL,
    course_id   uuid NOT NULL,
    -- ... domain columns ...
    CONSTRAINT fk_lessons_course
        FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id)
);
```

The parent therefore carries `UNIQUE (tenant_id, id)` purely to be referenceable this
way. The cost is one redundant-looking unique index per parent table; the alternative is
a class of cross-tenant corruption that no RLS policy can catch.

This block is the **single canonical RLS template**.
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md),
[Tenant Isolation](../architecture/09-tenant-isolation.md) and
[ADR-0017](../decisions/0017-tenant-organization-hierarchy.md) link here rather than
repeating it — a template copied into four documents is a template that drifts in four
directions.

Rules:
- `tenant_id` is the **first column** of every composite index unless profiling proves
  otherwise.
- Every tenant-owned table carries **exactly one** policy whose predicate `AND`s the
  tenant term with the organization term. A tenant-wide table simply omits the
  organization half. If a second policy is ever genuinely needed it must be declared
  `AS RESTRICTIVE`; a second permissive policy widens access rather than narrowing it.
- Every tenant-owned table declares both `ENABLE` and `FORCE ROW LEVEL SECURITY`.
- Every policy carries an explicit `WITH CHECK`. `USING` governs which rows are
  visible; `WITH CHECK` governs which rows may be written. The `app.scope = 'tenant'`
  escape hatch is deliberately **absent** from `WITH CHECK`: a tenant-scope reporting
  query may read across organizations, but nothing may write outside its organization.
  `WITH CHECK` does not deliver that property on its own. PostgreSQL has no `WITH CHECK`
  for `DELETE`, and `USING` is also what selects the rows an `UPDATE` may target — so
  the two `AS RESTRICTIVE` `FOR UPDATE` / `FOR DELETE` guards in the template above are
  what actually close the `USING`-only write paths. They are part of the template for
  every organization-scoped table, not an optional hardening step.
- The session variable names `app.tenant_id`, `app.organization_id`, `app.scope` and
  `app.resolving_host` are canonical and the set is closed; do not invent alternatives
  (`app.current_tenant_id`, `learnstack.tenant_id`, …). The first three are set by
  `TransactionBehavior` inside the ambient transaction. `app.resolving_host` is set by
  `CachedHostToTenantResolver` alone, in its own short read-only transaction, and is
  read by exactly one policy — see § Table classes. Always call `current_setting` with
  the second argument `true` (missing-OK), and always wrap the result in `NULLIF(…, '')`,
  so an unset context yields `NULL` and the predicate filters the row out rather than
  raising inside a pooled connection.
- The application sets `app.tenant_id` and `app.organization_id` with `SET LOCAL`
  **inside the ambient transaction**, after it opens. These settings are
  transaction-local: set before the transaction begins — in a MediatR behavior that
  runs earlier, or in a connection interceptor that fires at connection open — they are
  discarded before the query they are meant to protect ever runs. See
  [Security Standards § Tenant Context](11-security.md).
- `organization_id` on a tenant-owned row is **immutable after insert**, enforced by a
  `BEFORE UPDATE` trigger (`tg_<table>_organization_id_immutable`). A row does not move
  between organizations: its audit rows
  ([ADR-0016](../decisions/0016-audit-log-subsystem.md)), its storage prefix
  `tenants/{tenant_id}/organizations/{organization_id}/…` and its cache-key prefix are
  all organization-qualified per
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md), so re-parenting would
  orphan three subsystems at once. Moving content between organizations is a copy, not
  an update.

### Translation satellite tables

A `<entity>_translations` table is a tenant-owned table in its own right, not an
extension of its parent, and it gets the full template:

```sql
CREATE TABLE course_translations (
    course_id       uuid NOT NULL,
    tenant_id       uuid NOT NULL,     -- a real column; RLS is per table, never inherited
    organization_id uuid NULL,         -- mirrors the parent; for RLS, never for uniqueness
    locale          text NOT NULL,
    title           text NOT NULL,
    slug            text NOT NULL,
    -- ... other translatable columns ...
    PRIMARY KEY (course_id, locale),
    CONSTRAINT ux_course_translations_tenant_id_locale_slug
        UNIQUE (tenant_id, locale, slug),
    CONSTRAINT fk_course_translations_course
        FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id)
        ON DELETE CASCADE
);
```

It then declares `ENABLE` + `FORCE ROW LEVEL SECURITY` and the same
one-permissive-policy-plus-two-restrictive-guards set as its parent. A satellite with no
policy of its own is unprotected — a check constraint on the parent does not propagate
Row Level Security, and a table holding `title` and `slug` holds the content.

`organization_id` on a satellite exists **only** to carry the isolation predicate.
Denormalizing it is safe because of the immutability rule above. It is deliberately
**absent** from the slug unique key — see § Constraints and
[Localization Standards § Pattern A](08-localization.md).

The foreign key is composite on `tenant_id` for the reason in § Foreign keys between
tenant-owned tables. A composite key that also carried `organization_id` would not work:
the column is nullable, and under the default `MATCH SIMPLE` a foreign key with any null
column is not checked at all — which would silently re-open the cross-tenant reference
hole the composite key exists to close.

### Table classes

Not every table in the tenancy schema is tenant-owned, and applying the template above
to all of them produces a deadlock rather than isolation. Three classes exist, and every
migration states which one its table is.

| Class | Rule | Tables |
|---|---|---|
| **Tenant-owned** | The full template above: `ENABLE` + `FORCE`, one permissive policy `AND`-ing the tenant term with the organization term, explicit `WITH CHECK`, restrictive `UPDATE` / `DELETE` guards when org-scoped | every domain table, plus `organizations`, `tenant_domains`, `tenant_locales`, `tenant_settings` (org-scoped), `tenant_feature_flags`, `platform_entitlement_cache`, `outbox_messages` |
| **Tenant-owned, self-keyed** | Identical, except the tenant term is `id = …` because the row's primary key *is* the tenant id | `tenants` |
| **Platform-scoped** | `ENABLE` + `FORCE`, and role-qualified per-command policies: the read is widened by an explicitly declared non-tenant predicate, writes stay tenant-keyed | `platform_host_to_tenant` |

**A table is platform-scoped only when it is read before the tenant is known.** That is
one table today, and adding a second is a decision, not a convenience.
`platform_entitlement_cache` does **not** qualify despite its name: `IFeatureFlags`
resolves the tenant from `ITenantContext` and throws `TenantContextMissingException`
when there is none, and `IEntitlementProvider.RefreshAsync` is driven by
`PUT /api/internal/tenants/{id}/entitlements`, which carries the tenant id in its path.
Both directions have a tenant, so the table keeps the tenant-owned template and the
application role never holds a table-wide read of every tenant's plan.

#### `tenants` — self-keyed

`tenants` has no `tenant_id` column; its `id` **is** the tenant id, so the canonical
predicate would reference a column that does not exist. The policy keys on `id`:

```sql
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE  ROW LEVEL SECURITY;

CREATE POLICY tenants_isolation ON tenants
    USING      (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

Two consequences are binding:

- **A tenant id is never minted inside a handler.** The registry that owns the `Tenant`
  aggregate assigns it — the Hub in SaaS / Dedicated (`POST /api/internal/tenants`
  carries it), configuration in Self-Hosted, the fixture in a seed — and the
  provisioning transaction sets `app.tenant_id` to that id before the `INSERT`, so
  `WITH CHECK` passes. A handler that generated the id itself could not satisfy its own
  policy. A provisioning path that genuinely cannot supply an id ahead of the write runs
  under `EnterPlatformAdminScope("tenant-provisioning")`; that is the sanctioned escape,
  not a reason to weaken the policy.
- **Enumerating tenants requires the platform role.** `SELECT … FROM tenants` with no
  `app.tenant_id` returns zero rows, so `PlatformJob<TParams>`'s active-tenant sweep and
  every operator list screen go through `EnterPlatformAdminScope(reason)`. This is the
  intended cost: the application role cannot enumerate the customer list.

`tenants.slug` is globally unique, and PostgreSQL enforces unique indexes with row
security bypassed, so a duplicate-slug insert reveals that *some* tenant already holds
the slug. That is accepted here because slugs appear in hostnames and are public by
construction. It is not accepted anywhere else, which is why tenant-owned natural keys
are `UNIQUE (tenant_id, …)`.

#### `platform_host_to_tenant` — platform-scoped

`IHostToTenantResolver` reads this table **in order to determine the tenant**. At that
moment `app.tenant_id` is unset, so the tenant-owned template returns zero rows and no
tenant can ever resolve. The answer is not to drop row security — a table without
`ENABLE ROW LEVEL SECURITY` is indistinguishable from one nobody thought about — but to
give the read an explicitly declared key of its own. The resolver announces the host it
is about to resolve, and the policy admits exactly that row.

```sql
ALTER TABLE platform_host_to_tenant ENABLE ROW LEVEL SECURITY;
ALTER TABLE platform_host_to_tenant FORCE  ROW LEVEL SECURITY;

-- READ. Two deliberate branches, neither satisfiable by accident: an unset dotted GUC
-- is the empty string on a pooled connection, NULLIF turns it into NULL, and NULL is
-- false for USING.
--   app.resolving_host — the pre-context single-row lookup by IHostToTenantResolver
--   app.tenant_id      — a tenant listing its own hosts (Studio, canonical URLs)
CREATE POLICY platform_host_to_tenant_read ON platform_host_to_tenant
    FOR SELECT TO learnstack_app
    USING (
        host = NULLIF(current_setting('app.resolving_host', true), '')
        OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
    );

-- WRITE is never pre-context: every writer knows its tenant. Hub sync carries it in
-- PUT /api/internal/tenants/{id}/host-mappings; provisioning and Self-Hosted
-- configuration both declare it. Writes therefore stay tenant-keyed.
CREATE POLICY platform_host_to_tenant_insert ON platform_host_to_tenant
    FOR INSERT TO learnstack_app
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

CREATE POLICY platform_host_to_tenant_update ON platform_host_to_tenant
    FOR UPDATE TO learnstack_app
    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

CREATE POLICY platform_host_to_tenant_delete ON platform_host_to_tenant
    FOR DELETE TO learnstack_app
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

A wide `SELECT` policy does **not** widen `UPDATE` or `DELETE`. PostgreSQL applies the
command's own policies in addition to the `SELECT` policy, and a row must satisfy both.
A session that can see another tenant's host through `app.resolving_host` still cannot
repoint it: the `UPDATE` reports `UPDATE 0`, and an `INSERT` naming a foreign tenant
raises `new row violates row-level security policy`.

**`app.resolving_host` must be set inside an explicit transaction.** `SET LOCAL` outside
a transaction block emits `WARNING: SET LOCAL can only be used in transaction blocks`
and has no effect, and a session-level `set_config(…, false)` would survive on a pooled
connection into the next request. `CachedHostToTenantResolver` therefore opens a short
read-only transaction on a cache miss, issues `SET LOCAL app.resolving_host = @host`,
runs the single-row `SELECT`, and commits. The failure mode of forgetting it is an empty
result and a 404 — never a wider read.

Because these policies are role-qualified `TO learnstack_app`, **no policy applies to
the owner**, so under `FORCE` every access by `learnstack_migration` to this table is
denied. Rows arrive through `learnstack_app` under tenant context (Hub sync, tenant
provisioning, Self-Hosted configuration, dev seed) or through `learnstack_platform`.
This does not generalise: the canonical tenant-owned policy carries no `TO` clause, so
it applies to the owner as well, and per-tenant data migrations under
`SET LOCAL app.tenant_id` work as § Data Migrations describes.

Rejected alternative, recorded so it is not re-derived: a `SECURITY DEFINER` function
fronting the lookup would be tighter still — `learnstack_app` would hold `EXECUTE` and
no table privilege at all — but the function needs an owner that bypasses row security,
and that is a fifth database role. The four-role model is closed; a fifth requires an
ADR ([ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md)).

`tenant_domains` is a different table and stays tenant-owned: it holds the tenant's own
domain lifecycle and verification state, read and written under tenant context.
`platform_host_to_tenant` holds the resolution index, read before any context exists.
The two differ in *when* they are read, which is exactly why they cannot share one rule.

### Database roles

Four roles, four **separate login credentials**, four connection strings. The model is
closed; a fifth role requires an ADR.

| Role | Connection string | Present in | Used by | RLS posture |
|------|---|---|---------|-------------|
| `learnstack_migration` | `ConnectionStrings:Migration` | the deploy job / `make migrate` only — **never** in API or worker runtime configuration | `dotnet ef database update`; **owns** every table | `NOBYPASSRLS`; `FORCE ROW LEVEL SECURITY` denies ownership any bypass |
| `learnstack_app` | `ConnectionStrings:Default` | API host, worker host | every request-path and job-path `DbContext` | `NOBYPASSRLS` |
| `learnstack_platform` | `ConnectionStrings:PlatformAdmin` | API host, worker host | `EnterPlatformAdminScope(reason)` and nothing else | `BYPASSRLS` |
| `learnstack_outbox_admin` | `ConnectionStrings:OutboxDispatcher` | the worker host that runs `OutboxProcessor` | `OutboxProcessor` and nothing else | `BYPASSRLS` |

```sql
-- One-time provisioning, run once per database before the first migration. Ships as
-- infra/compose/postgres-init/02-create-roles.sql in Phase 02a Packet 6.
CREATE ROLE learnstack_migration    LOGIN PASSWORD :'migration_pw' NOBYPASSRLS;
CREATE ROLE learnstack_app          LOGIN PASSWORD :'app_pw'       NOBYPASSRLS;
CREATE ROLE learnstack_platform     LOGIN PASSWORD :'platform_pw'  BYPASSRLS;
CREATE ROLE learnstack_outbox_admin LOGIN PASSWORD :'outbox_pw'    BYPASSRLS;

GRANT CONNECT ON DATABASE learnstack
    TO learnstack_migration, learnstack_app, learnstack_platform, learnstack_outbox_admin;

-- Since PostgreSQL 15 the public schema no longer grants CREATE to PUBLIC, and the
-- schema is owned by pg_database_owner. Without the CREATE grant below the first
-- migration fails with "permission denied for schema public" — and the tempting fix
-- (make the migration role a superuser or the database owner) reinstates exactly the
-- ownership arrangement FORCE ROW LEVEL SECURITY exists to defeat.
REVOKE ALL   ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO learnstack_migration;
GRANT USAGE         ON SCHEMA public
    TO learnstack_app, learnstack_platform, learnstack_outbox_admin;

-- Per-table grants are written in the migration that creates the table; see the matrix
-- below. There is deliberately no ALTER DEFAULT PRIVILEGES.
GRANT SELECT, INSERT, UPDATE, DELETE ON courses TO learnstack_app;
```

Migrations connect **as** `learnstack_migration`, so every table it creates is already
owned by it — no `ALTER TABLE … OWNER TO` is needed, and seeing one in a migration is a
sign the migration is running as the wrong role. The connection string `dotnet ef` uses
and the one the application uses at runtime are **different roles**, and must not be
unified in any environment, local development included: one shared role makes the runtime
role the table owner, which is the arrangement `FORCE ROW LEVEL SECURITY` exists to
defeat, and every isolation test would then pass against inert policies.

`BYPASSRLS` bypasses **policies, not `GRANT`s**: a role holding the attribute with no
table privilege gets `permission denied for table`. Grant scope is therefore the only
thing that actually bounds the two bypass roles, and the matrix below is the whole of
that bound.

#### GRANT matrix

`—` means no privilege. `learnstack_migration` owns every table, which carries all
privileges implicitly.

| Table | `learnstack_app` | `learnstack_platform` | `learnstack_outbox_admin` |
|---|---|---|---|
| `tenants` | `SELECT, INSERT, UPDATE` | `SELECT, INSERT, UPDATE, DELETE` | — |
| `organizations` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT, INSERT, UPDATE, DELETE` | — |
| `tenant_domains` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_locales` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_settings` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_feature_flags` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT, INSERT, UPDATE, DELETE` | — |
| `platform_entitlement_cache` | `SELECT, INSERT, UPDATE` | `SELECT, DELETE` | — |
| `platform_host_to_tenant` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT, INSERT, UPDATE, DELETE` | — |
| `outbox_messages` | `SELECT, INSERT` | `SELECT, DELETE` | `SELECT`, `UPDATE (processed_at, attempts, last_error, available_after)` |

Four things the matrix cannot express, and one it must not be asked to:

- **A `GRANT` names a role, not a code path.** "Reserved to the provisioning path" is
  not something PostgreSQL can enforce — every handler in the API process runs as the
  same role, so `learnstack_app` holding `INSERT` on `tenants` means any handler may
  insert a `tenants` row *that satisfies the policy*. Code-path confinement is a
  separate layer, carried by `Modules_Do_Not_Read_Entitlement_Cache_Directly`,
  `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` and
  `Platform_DataSource_Resolved_Only_By_PlatformAdminScope`. State the two separately;
  conflating them produces a sentence that reads like a control and is not one.
- **No `ALTER DEFAULT PRIVILEGES` grants exist.** Every table's grants are written in
  the migration that creates it. A new table nobody granted fails loudly with
  `permission denied` instead of silently inheriting DML — and can never silently widen
  a `BYPASSRLS` role.
- **No privilege is ever granted to `PUBLIC`.** A single `GRANT … TO PUBLIC` would
  un-bound both bypass roles at once, because grant scope is the only thing bounding
  them.
- **Sequences.** A table with a generated integer key uses
  `bigint GENERATED BY DEFAULT AS IDENTITY`, not `bigserial`. An identity column's
  sequence needs no privilege of its own; a `bigserial` column's does, and forgetting
  `GRANT USAGE ON SEQUENCE` surfaces as `permission denied for sequence …` on the first
  insert.
- **Tenant hard-deprovisioning is not in the matrix** because no phase owns it yet. When
  it lands it will need `DELETE` for `learnstack_platform` across every tenant-owned
  table; that is a deliberate widening of a `BYPASSRLS` role and is recorded in the ADR
  that introduces it, not slipped into a migration.

#### How `EnterPlatformAdminScope(reason)` reaches `learnstack_platform`

**By a second connection, not by `SET ROLE`.** `learnstack_app` is **not** a member of
`learnstack_platform`; no `GRANT learnstack_platform TO learnstack_app` exists. The
composition root registers a second, separately-credentialed `NpgsqlDataSource` from
`ConnectionStrings:PlatformAdmin` as a keyed singleton whose only sanctioned consumer is
`LearnStack.Infrastructure.MultiTenancy.PlatformAdminScope`.
`EnterPlatformAdminScope(reason)` opens a DI scope whose `DbContext` is built on that
data source and returns an `IAsyncDisposable` handle. Module code cannot resolve it;
`Platform_DataSource_Resolved_Only_By_PlatformAdminScope` enforces that.

`SET ROLE` is rejected on three grounds:

1. **Membership is a standing capability.** Once `learnstack_app` is a member of
   `learnstack_platform`, any code path that reaches raw SQL can execute
   `SET ROLE learnstack_platform` and acquire `BYPASSRLS` — the same session reads zero
   rows as `learnstack_app` and every tenant's rows after the switch. The four-role
   separation would then be a naming convention, not a boundary.
2. **A plain `SET ROLE` survives `COMMIT`.** PgBouncer runs `server_reset_query`
   (`DISCARD ALL`) only in session pooling unless `server_reset_query_always = 1`, and
   LearnStack runs **transaction** pooling, so a leaked `SET ROLE` persists on the pooled
   server connection and the next tenant's request runs with `BYPASSRLS`.
   `SET LOCAL ROLE` does reset at commit, but it makes correctness depend on remembering
   `LOCAL` on the one statement whose omission is an unbounded cross-tenant read.
3. **Per-role settings do not follow `SET ROLE`.** `ALTER ROLE … SET statement_timeout`
   is applied at login from the authenticated role: a session that logs in as
   `learnstack_app` keeps that role's value after `SET ROLE learnstack_platform`, while a
   fresh login as `learnstack_platform` gets the platform value. The per-role
   `statement_timeout` split and the per-role pool separation that
   [Phase 11](../roadmap/phase-11-production-hardening.md) builds are only implementable
   when each role logs in for itself.

The residual risk is that the platform credential exists in the API process's
configuration. It is mitigated by resolving it only through `PlatformAdminScope`; by a
separate secret path (`learnstack/{deployment}/platform/db-password`) that a deployment
needing no platform admin simply does not provision, in which case
`EnterPlatformAdminScope` throws at startup rather than degrading to `learnstack_app`;
and by an audit row written **inside** the scope before the operation runs and committed
on its own, so an operation that later fails is still recorded. That row is written as
`learnstack_platform` and carries the sentinel platform tenant id, because a cross-tenant
operation has no tenant of its own and `audit_log` is itself tenant-owned.

**Isolation tests connect as `learnstack_app`.** A test that connects as the owner or
as a `BYPASSRLS` role passes even when every policy is inert, so it proves nothing.

## Audit Columns

Mutable tenant-owned aggregates include:

- `created_at timestamptz NOT NULL`
- `created_by uuid NOT NULL`
- `updated_at timestamptz NOT NULL`
- `updated_by uuid NOT NULL`

Soft-deletable aggregates also include:

- `deleted_at timestamptz NULL`
- `deleted_by uuid NULL`

A shared EF interceptor populates these on `SaveChanges`.

## Concurrency

`row_version bigint` (incremented by an EF interceptor) for optimistic concurrency. `xmin`-based tokens are an alternative; pick one project-wide.

## Identifiers

- `uuid` PKs (`gen_random_uuid()`).
- Strongly-typed ids in code via value converters.
- No surrogate `int` keys for domain entities.
- Sequence-based ids only for high-write append-only logs (outbox, audit log).

## Indexes

- Index every foreign key.
- Index `tenant_id` as the first column of composite indexes for tenant-owned tables.
- Avoid over-indexing; each index costs on every write.
- Partial indexes for highly-skewed predicates: `WHERE deleted_at IS NULL`.
- Covering indexes when a hot query reads only a few columns.

## Constraints

- `NOT NULL` aggressively; defaults only when business sense dictates.
- `CHECK` constraints for invariants the database can enforce (`status IN (...)`, `length(slug) BETWEEN 1 AND 120`).
- `UNIQUE` constraints for tenant-scoped natural keys (`UNIQUE (tenant_id, slug_key)`).
- **A nullable column in a `UNIQUE` constraint does not constrain the rows where it is
  null.** PostgreSQL follows the SQL standard and treats nulls as distinct for uniqueness
  purposes, so `UNIQUE (tenant_id, organization_id, key)` permits unlimited duplicates
  among tenant-wide rows (`organization_id IS NULL`) — the rows a tenant usually creates
  first, and the ones a single-organization tenant creates exclusively. Two ways out, and
  the choice is not stylistic. Ask: **does the nullable column select between rows that a
  consumer resolves by an explicit authored precedence, or does it partition a namespace
  that must resolve to exactly one row?**
  - **Authored precedence** — an organization override of a tenant-wide notification
    template, resolved by the documented org → tenant fallback chain. The column belongs
    in the key, and the constraint is declared `UNIQUE NULLS NOT DISTINCT (…)`. Available
    from PostgreSQL 15; LearnStack pins 18.x across all deployment modes
    ([ADR-0031](../decisions/0031-postgresql-major-version.md)), so partial-index
    workarounds are never needed for availability.
  - **One-row namespace** — a routable slug, where a host resolving to
    `(tenant_id, organization_id)` serves both tiers and something has to pick a winner
    at render time. The column does **not** belong in the key. Drop it and constrain the
    flat namespace. See [Localization Standards § Pattern A](08-localization.md).

  Paired partial unique indexes (`WHERE organization_id IS NULL` / `WHERE organization_id
  IS NOT NULL`) are not the house pattern: they need two names and two predicates that
  must exhaustively partition the space, PostgreSQL has no `UNIQUE (…) WHERE …` table
  constraint so they cannot be declared alongside the columns they guard, and they solve
  the same half of the problem `NULLS NOT DISTINCT` solves while leaving the cross-tier
  collision open.
- Foreign keys with `ON DELETE` set explicitly.

## Soft Delete

- Opt-in per aggregate; not a global default.
- Soft-deleted rows excluded via global EF query filter where applicable.
- Scheduled purge job removes rows past retention.

## Migrations

- EF Core migrations per module, generated from code.
- Name format: `<UTC_yyyyMMddHHmmss>_<intent>`. Intent in snake-case: `add_course_publish_at_column`.
- Every PR that changes a `*Configuration` ships the migration.
- Forward-only by default; reversal only for non-destructive changes.
- Destructive migrations (drop column, change type) require:
  1. ADR or PR description explaining the data plan.
  2. Two-step deploy: tolerant code → migration → strict code.

## Data Migrations

- Pure-SQL data migrations live in migration files when small.
- Larger data migrations are idempotent Hangfire jobs.
- Always re-runnable.
- Tenant-aware: per-tenant data migrations use the tenant role and `SET LOCAL app.tenant_id = ...` per tenant (and `app.organization_id` where applicable).

## Read Models

Public read models consumed by other modules:

- Live in the producing module's migrations.
- Naming: `public_<module>_<concept>`.
- Populated by event handlers or projection jobs.
- Consumers read via the producer's repository contract, not raw SQL across modules.

## Outbox

```sql
CREATE TABLE outbox_messages (
    id              uuid PRIMARY KEY,
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,             -- null = tenant-wide event; see note below
    correlation_id  text NOT NULL,         -- full W3C traceparent, per ADR-0032 § 12
    causation_id    uuid NULL,
    actor_user_id   uuid NULL,
    type            text NOT NULL,         -- assembly-qualified event type name
    topic           text NOT NULL,         -- "learnstack.{module}.{aggregate}"
    payload         jsonb NOT NULL,
    metadata        jsonb NULL,
    processed_at    timestamptz NULL,
    attempts        int NOT NULL DEFAULT 0,
    last_error      text NULL,
    available_after timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_outbox_messages_pending
    ON outbox_messages (available_after)
    WHERE processed_at IS NULL;
CREATE INDEX ix_outbox_messages_tenant_pending
    ON outbox_messages (tenant_id, available_after)
    WHERE processed_at IS NULL;

ALTER TABLE outbox_messages ENABLE ROW LEVEL SECURITY;
ALTER TABLE outbox_messages FORCE  ROW LEVEL SECURITY;

-- Tenant-wide table: no organization term, but the same one-policy,
-- USING + WITH CHECK, FORCE-enabled shape as every other tenant-owned table.
-- NULLIF is mandatory here for the same reason as everywhere else: on a pooled
-- connection whose previous transaction set app.tenant_id and whose next one does not,
-- current_setting returns '' and ''::uuid RAISES instead of filtering.
CREATE POLICY outbox_messages_isolation ON outbox_messages
    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

-- Grants. Application code only ever enqueues, so learnstack_app gets no UPDATE and no
-- DELETE; status transitions belong to the dispatcher and purging belongs to the
-- audited platform scope.
GRANT SELECT, INSERT ON outbox_messages TO learnstack_app;
GRANT SELECT, DELETE ON outbox_messages TO learnstack_platform;
GRANT SELECT ON outbox_messages TO learnstack_outbox_admin;
GRANT UPDATE (processed_at, attempts, last_error, available_after)
    ON outbox_messages TO learnstack_outbox_admin;

-- The OutboxProcessor connects as learnstack_outbox_admin, which holds BYPASSRLS so it
-- can read every tenant's pending rows for dispatch. That bypass is the reason the
-- table's own policy still matters: application code writing an outbox row does so as
-- learnstack_app, inside the business transaction, and WITH CHECK pins the row to the
-- caller's tenant. BYPASSRLS bypasses policies, not GRANTs — the column list above is
-- what actually bounds the dispatcher, and SELECT ... FOR UPDATE SKIP LOCKED works with
-- a column-level UPDATE grant, so no table-wide UPDATE is needed. When locked_by and
-- locked_until land in Phase 02b, that migration extends this grant; a column added
-- without extending it fails at runtime with `permission denied for table`.
-- See ADR-0006 Amendment 1 and 15-event-and-outbox.md.
```

`organization_id` is `uuid NULL` and mirrors the organization context of the transaction
that enqueued the row —
[ADR-0032 § Sub-decision 12](../decisions/0032-exception-handling-logging-and-observability.md)
makes it contractual. It is nullable because a tenant-wide event has no organization, not
because it is optional on an org-scoped one. The outbox row is the **only** carrier of
organization context across a transport boundary: the consumer runs in a fresh scope and
restores `ITenantContext` from the envelope, so an organization that was never persisted
here cannot be restored there. It is deliberately **not** indexed and **not** part of the
table's RLS predicate — `outbox_messages` is a tenant-wide table whose policy carries no
organization term, and the dispatcher reads the column rather than filtering on it.

`correlation_id` is `text NOT NULL` (not `uuid`): it stores the **full W3C `traceparent`
string** — `00-<32-hex trace-id>-<16-hex span-id>-<2-hex flags>` — so a consumer rehydrates
the trace with `ActivityContext.TryParse(row.CorrelationId, traceState: null, out var ctx)`
rather than re-deriving it
([ADR-0032 § Sub-decision 12](../decisions/0032-exception-handling-logging-and-observability.md),
[10-observability.md § Correlation](10-observability.md)). It is `text` rather than `uuid`
because a traceparent is not a UUID. The request middleware synthesises a root traceparent
when the inbound request carries none, and Hangfire enqueue rejects payloads without one, so
no enqueue path can produce a row without a correlation id — which is what lets the
registered `Outbox_Row_Carries_Correlation_Context` assertion rest on the schema rather than
on developer discipline. The earlier justification for nullability — that APISIX's
`request-id` plugin echoes any client-supplied id — did not survive contact with
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md), which demand-gates APISIX to
[Phase 11](../roadmap/phase-11-production-hardening.md): the column exists from Phase 02a,
so its contract cannot rest on a component that is not there yet. The architecture deep dive
lives in [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).

## Raw SQL

Allowed when:
- EF Core cannot express the query efficiently.
- Tenant predicate is **explicit**.
- Query is parameterized.
- Query is covered by an integration test.

Forbidden: string interpolation with non-constant values.

## Connection Management

- Npgsql connection multiplexing where appropriate.
- **PgBouncer in transaction-pooling mode** for production — this is a hard
  prerequisite for RLS: `SET LOCAL app.tenant_id = ...` is transaction-scoped, so
  statement-mode pooling would reset the value between statements and silently break
  isolation. The architecture test `Db_Connection_String_Is_TransactionPooled`
  enforces this in the deployment config; deviation requires an ADR.
- `app.tenant_id` (and `app.organization_id` when relevant) set **within the same
  transaction** as the work (`SET LOCAL ...`).
- A `DbCommandInterceptor` — **not** a connection-checkout interceptor — guards the
  context. Checkout happens before `TransactionBehavior` opens the transaction that
  carries the `SET LOCAL` values, so a checkout hook would read an unset
  `app.tenant_id` on every request and throw universally; under PgBouncer transaction
  pooling it would sometimes read a *previous* transaction's leftover value, which is
  worse than throwing. The command interceptor instead checks the in-process marker
  `TransactionBehavior` stamps on the ambient transaction once it has issued the
  `SET LOCAL` pair, and throws `TenantContextMissingException` when a command against a
  `[TenantOwned]` table runs without it — no extra round trip.
- The database-side guard is fail-closed independently: with `app.tenant_id` unset or
  reset the policy predicate is `NULL`, so the query returns zero rows rather than
  leaking. The interceptor exists to turn that silent empty result into a loud failure,
  not to be the isolation boundary.

## Backups

- Daily logical backups (`pg_dump`) for dev-grade restore.
- Continuous WAL archiving in production for PITR.
- Restore drills run at least quarterly — a drill counts only if a backup is restored to a fresh instance and integration tests pass.

## Forbidden

- Raw SQL with interpolated user input.
- Cross-module joins inside SQL.
- Cross-tenant queries outside platform-admin scope.
- `IgnoreQueryFilters()` in non-platform code.
- Lazy loading.
- Multiple `DbContext` instances within one logical transaction.
- Tenant tables without RLS in production migrations (CI rejects).
