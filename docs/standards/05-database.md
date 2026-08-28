# 05 — Database Standards

**Status:** Active
**Derives from:** [ADR-0002 Initial Architecture](../decisions/0002-initial-architecture.md)
(Amendments 1 + 2),
[ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
(Amendment 1: Organization Scope; **Amendment 3: corrected RLS policy template and
database role model**),
[ADR-0006 Events and Outbox](../decisions/0006-events-and-outbox.md)
(Amendment 1: Dapr pub/sub dispatch transport),
[ADR-0038 Cross-Cutting Port and Event Contracts](../decisions/0038-cross-cutting-port-and-event-contracts.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0031 PostgreSQL — Start on 18.x](../decisions/0031-postgresql-major-version.md)
(Amendment 1: the built-in is `uuidv7()`),
[ADR-0037 Idempotency Key Contract](../decisions/0037-idempotency-key-contract.md),
[ADR-0039 The Optimistic Concurrency Token](../decisions/0039-optimistic-concurrency-token.md),
[ADR-0040 The Ambient Unit of Work](../decisions/0040-ambient-unit-of-work.md).

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
    -- NULL until the first update. MarkCreated stamps only created_*; a row that
    -- has never been changed has no updater, and NOT NULL here would fail every
    -- INSERT. Order by `coalesce(updated_at, created_at)` when you want
    -- last-touched.
    updated_at      timestamptz NULL,
    updated_by      uuid NULL,
    -- Unconditional, not opt-in: AuditableEntity<TId> implements ISoftDelete for
    -- every aggregate, so EF maps these on every table that derives from it. A
    -- table that omitted them would fail to materialize its own entity.
    deleted_at      timestamptz NULL,
    deleted_by      uuid NULL,
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

-- No standalone index on tenant_id: ux_courses_tenant_id_slug_key and
-- ux_courses_tenant_id_id are both b-trees with tenant_id leading, and either serves
-- a tenant-only lookup. One composite index carries the organization dimension.
-- Deliberately NOT partial: the policy's `organization_id IS NULL` branch matches
-- every tenant-wide row, and a b-tree indexes NULLs, so the non-partial form serves
-- both branches of the predicate.
CREATE INDEX ix_courses_tenant_id_organization_id ON courses (tenant_id, organization_id);
```

### Foreign keys between tenant-owned tables

**Every foreign key between two tenant-owned tables is composite on `tenant_id`.**

PostgreSQL evaluates referential integrity at **runtime** as a security-restricted
operation on behalf of the table owner, and those RI triggers are **not subject to Row
Level Security**. (The DDL path is the opposite and matters in migrations: the scan
`ALTER TABLE … ADD CONSTRAINT` / `VALIDATE CONSTRAINT` performs runs as the issuing role
under its policies, so a constraint added to a populated tenant-owned table validates
against the rows that role can see. § Data Migrations carries the consequence.) A
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

**One written exception: the self-keyed parent.** `tenants` has no `tenant_id`
column — its `id` *is* the tenant id — so it can carry no `UNIQUE (tenant_id, id)`
and the composite form is not expressible for the one foreign key every other
tenancy table needs. `organizations.tenant_id` and its peers therefore reference
`tenants (id)` with a **single column**, and that is safe on this rule's own
reasoning: the composite form exists because a referential-integrity check runs
with row security bypassed and a single-column child FK could point at another
tenant's row. Here the referencing column *is* `tenant_id`, so pointing
elsewhere would be pointing at a different tenant by definition, which the
child's own `WITH CHECK` already refuses. The exception is exactly one table
wide; a second one is a decision, not a convenience.

**`ON DELETE RESTRICT` on every foreign key whose parent is an aggregate root,
until a phase owns deprovisioning.** A cascade from `tenants` or `organizations`
downward would be a tenant-deletion path nobody has designed — see the note in
§ GRANT matrix that tenant hard-deprovisioning has no owning phase. `RESTRICT`
makes the absence loud.

The one standing exception is a **translation satellite**, which cascades from
its own parent (`ON DELETE CASCADE` in the `course_translations` fence above): a
translation is not an independent row and outliving its parent would leave a
title with nothing to title. That is deletion *within* an aggregate, not deletion
*of* one, and it is why the two fences differ.

**The circular reference, and why it is still composite.**
`tenants.default_organization_id` points at `organizations`, which points back at
`tenants`. This direction is **not** covered by the self-keyed exception above —
that exception exists only because `tenants` has no `tenant_id` column to pair
with, and here the *child* side does: `tenants.id` **is** the tenant id, and
`organizations` already carries `UNIQUE (tenant_id, id)`. So the composite form is
expressible and is required:

```sql
CONSTRAINT fk_tenants_default_organization
    FOREIGN KEY (id, default_organization_id) REFERENCES organizations (tenant_id, id)
    ON DELETE RESTRICT
```

Single-column here would be the exact hole the rule closes: referential-integrity
checks run with row security bypassed, so tenant A could commit a permanent
pointer at tenant B's organization — a row A cannot even see. Under `MATCH SIMPLE`
the check is skipped entirely while `default_organization_id` is null, which is
what makes the nullable column safe before the `UPDATE` lands.

The column is **nullable**, and the provisioning transaction inserts the tenant,
inserts its default organization, then `UPDATE`s the tenant — three statements in
one transaction rather than a `DEFERRABLE INITIALLY DEFERRED` constraint, because
a deferred constraint moves the failure to `COMMIT`, where the error names the
constraint and not the statement that broke it.

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
  `BEFORE UPDATE` trigger. The function is declared once and the trigger once per
  org-scoped table, in the migration that creates it:

  ```sql
  CREATE FUNCTION fn_organization_id_immutable() RETURNS trigger AS $$
  BEGIN
      IF NEW.organization_id IS DISTINCT FROM OLD.organization_id THEN
          RAISE EXCEPTION
              'organization_id is immutable after insert (table %, row %)',
              TG_TABLE_NAME, OLD.id
              USING ERRCODE = '23514';
      END IF;
      RETURN NEW;
  END;
  $$ LANGUAGE plpgsql;

  CREATE TRIGGER tg_courses_organization_id_immutable
      BEFORE UPDATE ON courses
      FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();
  ```

  `IS DISTINCT FROM` rather than `<>`, so a move to or from `NULL` — tenant-wide to
  org-scoped, or back — is caught too; `<>` is `NULL` when either side is null and the
  trigger would pass. The restrictive `UPDATE` guard does not cover this: it admits the
  row when the *new* `organization_id` is the caller's own, which is exactly the
  re-parenting move. A row does not move
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
to all of them produces a deadlock rather than isolation. Four classes exist, and every
migration states which one its table is.

| Class | Rule | Tables |
|---|---|---|
| **Tenant-owned, org-scoped** | The full template above: `ENABLE` + `FORCE`, one permissive policy `AND`-ing the tenant term with the organization term, explicit `WITH CHECK`, **and** the two `AS RESTRICTIVE` `UPDATE` / `DELETE` guards | any domain table carrying `organization_id`, plus `tenant_settings` — the only org-scoped table in the Packet 6 set |
| **Tenant-owned, tenant-wide** | The same shape with the organization half of the predicate omitted, and therefore **no** restrictive guards — there is no organization to guard | `organizations`, `tenant_domains`, `tenant_locales`, `tenant_feature_flags`, `platform_entitlement_cache`, `idempotency_keys`, `outbox_messages` |
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
construction.

**Exactly two columns carry that cost.** The second is `tenant_domains.host`, and for
the same reason stated the other way round: a host resolving to two tenants is
unresolvable no matter who owns it, so global uniqueness is not a convenience but the
constraint the resolver depends on. Its index is **partial** —
`UNIQUE (host) WHERE deleted_at IS NULL` — because a table-wide unique would let a
soft-deleted claim hold a hostname forever, and
[ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) contemplates a
released-then-re-registered domain.

Adding a third is a decision, not a convenience. Every other tenant-owned natural key
is `UNIQUE (tenant_id, …)`.

#### `platform_host_to_tenant` — platform-scoped

`IHostToTenantResolver` reads this table **in order to determine the tenant**. At that
moment `app.tenant_id` is unset, so the tenant-owned template returns zero rows and no
tenant can ever resolve. The answer is not to drop row security — a table without
`ENABLE ROW LEVEL SECURITY` is indistinguishable from one nobody thought about — but to
give the read an explicitly declared key of its own. The resolver announces the host it
is about to resolve, and the policy admits exactly that row.

`host` also carries the **normalization backstop**
[ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) assigns to this
packet. It constrains the *output* of `EffectiveHost.Normalize`, not the
algorithm: the seven-step normalization — IDN mapping, port stripping, trailing-dot
handling, IP-literal rejection — is imperative and PostgreSQL cannot evaluate it in
a `CHECK`. What the database can guarantee is that nothing un-normalized was
inserted by a path that skipped the normalizer:

```sql
CONSTRAINT ck_platform_host_to_tenant_host_normalized CHECK (
    -- The LDH rule, stated positively: every label starts and ends alphanumeric
    -- and may carry hyphens between, labels joined by single dots. Written this
    -- way rather than as a list of prohibitions, because the prohibitions kept
    -- missing cases: measured, a `!~ '[^a-z0-9.-]'` form accepted
    -- `.example.com`, `a..b.com` and `-example.com`, none of which
    -- EffectiveHost.Normalize's own IsLdh gate can produce. Lowercase, no
    -- trailing dot and no embedded port all fall out of the pattern.
    -- `[a-z0-9]+(` rather than `[a-z0-9](`: the second spells `](`, which the CI
    -- link audit greps for as a Markdown link — it does not skip fenced code —
    -- and then fails the meta job on a target named `[a-z0-9-]*[a-z0-9]`.
    host ~ '^[a-z0-9]+([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]+([a-z0-9-]*[a-z0-9])?)*$'
    AND length(host) <= 253
)
```

`EffectiveHost.Normalize` remains the **sole** normalizer; this constraint never
normalizes anything, it only refuses. A row that violates it is a bug in a writer,
not a host to be fixed up.

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
read-only transaction on a cache miss, issues
`SELECT set_config('app.resolving_host', @host, true)`, runs the single-row `SELECT`,
and commits. It must be the **function** form: `SET` takes no bind parameter —
`SET LOCAL app.resolving_host = $1` is a syntax error, measured — and interpolating
the host into a `SET` on the anonymous page-load path would be an injection site.
`set_config`'s third argument `true` is what makes it transaction-local, exactly as
`SET LOCAL` is. The failure mode of forgetting it is an empty
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

**Passwords arrive from the environment, through `\getenv`.** The four
`:'name'` placeholders below are **psql client variables**, and the
`docker-entrypoint-initdb.d` runner binds none of them: measured, the script as
it stood failed on its first statement with `syntax error at or near ":"`.
`\getenv` reads each one from the container environment.
[Infrastructure Standards § Local Infrastructure](12-infrastructure.md) requires
every credential to be `${VAR:-fallback}` in compose with a matching row in
`.env.example`, and Packet 6 shipped both: the four `LEARNSTACK_*_PW` rows in
`.env.example` and the matching entries on the `postgres` service in `dev.yml`.
`e2e.yml` needs none — it overlays `dev.yml` and overrides only `volumes:`, so
the `environment:` block is inherited.

**The shipped script is `infra/compose/postgres-init/02-create-roles.sql` and it
is idempotent**, which the fence below is not: `CREATE ROLE` has no
`IF NOT EXISTS`, so each statement is generated by
`SELECT format(…) WHERE NOT EXISTS (SELECT FROM pg_roles …) \gexec`. The fence
states the *model* — which roles, which attributes, which grants; the script is
the executable form and `TheRolesScriptIsIdempotent` proves the re-run is a
no-op. It also revokes `CONNECT, TEMPORARY` from `PUBLIC` before granting, which
the fence omits and without which the grants add nothing: measured,
`learnstack_app` could otherwise connect to every database in the cluster. Measured, it works under the entrypoint's `ON_ERROR_STOP=1`, and
an **unset** variable leaves the placeholder unbound and aborts init rather than
creating a passwordless superuser-adjacent role — failing loud is the point.

```sql
-- One-time provisioning, run once per database before the first migration. Ships as
-- infra/compose/postgres-init/02-create-roles.sql in Phase 02a Packet 6.
\getenv migration_pw LEARNSTACK_MIGRATION_PW
\getenv app_pw       LEARNSTACK_APP_PW
\getenv platform_pw  LEARNSTACK_PLATFORM_PW
\getenv outbox_pw    LEARNSTACK_OUTBOX_PW

CREATE ROLE learnstack_migration    LOGIN PASSWORD :'migration_pw' NOBYPASSRLS;
CREATE ROLE learnstack_app          LOGIN PASSWORD :'app_pw'       NOBYPASSRLS;
CREATE ROLE learnstack_platform     LOGIN PASSWORD :'platform_pw'  BYPASSRLS;
CREATE ROLE learnstack_outbox_admin LOGIN PASSWORD :'outbox_pw'    BYPASSRLS;

-- :"db" quotes as an identifier, not a literal: the database name is POSTGRES_DB,
-- which .env.example may override, so hardcoding `learnstack` grants CONNECT on a
-- database that need not exist.
\getenv db POSTGRES_DB
GRANT CONNECT ON DATABASE :"db"
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
-- below. There is deliberately no ALTER DEFAULT PRIVILEGES. No grant on a specific
-- table belongs in THIS script: it runs at initdb time, before any table exists, and
-- under ON_ERROR_STOP=1 one `relation "…" does not exist` aborts the whole init and
-- the container never becomes healthy. This fence previously ended with a grant on
-- `courses`, a Phase 05 table; measured, it does exactly that.
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
| `idempotency_keys` | `SELECT, INSERT, UPDATE` | `SELECT, DELETE` | — |
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
- `updated_at timestamptz NULL` — null until the first update
- `updated_by uuid NULL`
- `deleted_at timestamptz NULL`
- `deleted_by uuid NULL`

All six, on every table whose entity derives from `AuditableEntity<TId>`. The
`deleted_*` pair used to be listed as a soft-delete opt-in and is not one:
`AuditableEntity<TId>` implements `ISoftDelete` unconditionally, so EF maps both
columns on every such table whether the aggregate is ever soft-deleted or not.
What is opt-in is the **query filter** — see § Soft Delete.

The `updated_*` pair used to be `NOT NULL`, which no insert could satisfy:
`MarkCreated` stamps `created_*` only, so a freshly created row has no updater and
the constraint would reject it. A row that has never been changed genuinely has
none; order by `coalesce(updated_at, created_at)` where "last touched" is
wanted.

These are populated by `AuditableEntity.MarkCreated` / `MarkUpdated` /
`SoftDelete`, which aggregate methods call with the `IClock` they already
inject. **Not by an EF interceptor.** The only sanctioned `SaveChanges`
interceptor is `AuditChangeTrackerInterceptor`, and per
[ADR-0033](../decisions/0033-audit-durability-model.md) it captures a snapshot
and writes nothing — an interceptor that also stamped audit columns would be
writing on a path that ADR deliberately keeps read-only.

## Concurrency

`row_version bigint`, CLR `long`, on every entity implementing
`IOptimisticConcurrency`. Configure it with exactly these three calls
([ADR-0039 Amendment 2](../decisions/0039-optimistic-concurrency-token.md)):

```csharp
builder.Property(x => x.Version)
    .HasColumnName("row_version")
    .HasDefaultValue(0L)        // the DEFAULT 0 this template declares
    .IsConcurrencyToken()       // the token
    .ValueGeneratedNever();     // undo HasDefaultValue's OnAdd side effect
```

`HasDefaultValue` sets `ValueGenerated = OnAdd` as a side effect, so
`IsConcurrencyToken()` on its own is only correct on a column with no default —
which is not the column this standard declares.
`Aggregates_With_Optimistic_Concurrency_Map_RowVersion` asserts
`ValueGenerated == Never`, so the two-call form fails it.

Neither `.ValueGeneratedOnAddOrUpdate()` nor `IsRowVersion()` may be added, and
they are the same mistake: on a `long` property they produce byte-identical
property metadata — `ValueGenerated.OnAddOrUpdate` with
`BeforeSave`/`AfterSave` = `Ignore` — and EF then **omits the column from the
`UPDATE` statement entirely**. Measured on EF Core 10 + Npgsql 10 against
PostgreSQL 18.4, on a table declared exactly as the template declares it:

| Configuration | Emitted SQL | Persisted `row_version` |
|---|---|---|
| `IsConcurrencyToken().ValueGeneratedOnAddOrUpdate()` | `UPDATE widgets SET name = @p0` | `0` |
| `IsRowVersion()` | `UPDATE widgets SET name = @p0` | `0` |
| `IsConcurrencyToken()` | `UPDATE widgets SET name = @p0, row_version = @p1` | `1` |

Those two forms tell EF the **database** generates the value. Nothing here does:
the column's only `DEFAULT` is `0`, there is no trigger and no
`GENERATED ALWAYS`. So the token never leaves `0`, every `If-Match` compares
equal, and optimistic concurrency silently never fires — a lost update succeeds
and reports success. `IsConcurrencyToken()` alone leaves the write behaviours at
`Save`, which is what puts the incremented value in the `SET` list.

The value is incremented inside `AuditableEntity`, by the same primitive that
stamps `UpdatedAt` / `UpdatedBy`, so an audited mutation is a versioned
mutation. `SoftDelete` routes through that primitive too; stamping the fields
itself would leave a soft delete un-versioned, and a client holding the
pre-delete ETag would still satisfy `If-Match` on the row it deleted.

**`xmin` is not used as a concurrency token anywhere**, and is no longer an
alternative. [ADR-0039](../decisions/0039-optimistic-concurrency-token.md)
closed the fork this line used to leave open: the token is client-visible
through ETag / `If-Match`, and a dump-restore or logical-replication cutover
changes `xmin` while leaving `row_version` intact.

## Identifiers

- `uuid` PKs, **UUIDv7**, by one of exactly two paths per
  [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md):
  - **App-side** for aggregates — `IGuidFactory.NewUuidV7()`, so a test can pin
    the value and the id exists before the row does.
  - **DB-side** `DEFAULT uuidv7()` for the high-volume append-only tables whose
    surrogate key is written by infrastructure rather than by an aggregate:
    `audit_log` and `outbox_messages`. Both canonical fences carry the clause;
    a fence and this rule disagreeing is a defect in one of them.
    - `inbox_messages` is **not** one of them despite being append-only: its key
      is the producing envelope's `EventId`, which the producer minted app-side.
      Generating a second id there would defeat the deduplication the table is.
    - `idempotency_keys` is not one either: it is addressed by `(tenant_id, key)`
      and has no surrogate id at all.
- **Never `gen_random_uuid()`.** It is a real function and produces a **v4**
  UUID — random, with none of the index locality UUIDv7 was adopted for. The
  built-in is `uuidv7()`; `gen_uuid_v7()` does not exist
  ([ADR-0031 Amendment 1](../decisions/0031-postgresql-major-version.md)).
- Strongly-typed ids in code via value converters.
- No surrogate `int` keys for domain entities.
- `bigint GENERATED BY DEFAULT AS IDENTITY` where a generated integer key is
  genuinely wanted — never `bigserial`, whose sequence needs a `GRANT USAGE` of
  its own that an identity column does not.

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
- **A closed-set status column is `text NOT NULL` with a `CHECK (col IN (…))`** — not
  a PostgreSQL `enum` type and not an `int`. An `enum` type's values can only be added,
  never removed or reordered, and every change is a migration on the type rather than
  on the table; an `int` makes a dump unreadable and a mistyped value
  indistinguishable from a valid one. `idempotency_keys.state` is the worked example.
  The CLR side stays a C# `enum` and maps through a value converter.
- Foreign keys with `ON DELETE` set explicitly.

## Soft Delete

- The **columns** are not opt-in — `AuditableEntity<TId>` carries `DeletedAt` / `DeletedBy` for every aggregate, so every such table has them (§ Audit Columns). What is opt-in is whether an aggregate is ever soft-deleted and whether its query filter excludes deleted rows.
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
- Tenant-aware: per-tenant data migrations run as `learnstack_migration` and issue
  `SET LOCAL app.tenant_id = '<id>'` inside a transaction **per tenant** — and, on
  org-scoped tables, `SET LOCAL app.organization_id = '<id>'` inside a transaction
  **per organization**. Both are mandatory, not hygiene. `learnstack_migration` is
  `NOBYPASSRLS` and every tenant-owned table is `FORCE ROW LEVEL SECURITY`, so a
  backfill that skips the tenant variable matches **zero rows** and still reports
  success; and because the two `AS RESTRICTIVE` write guards deliberately ignore
  `app.scope = 'tenant'`, a tenant-scope session cannot span organizations for
  `UPDATE` or `DELETE` — it would silently touch only the tenant-wide
  (`organization_id IS NULL`) rows. Granting `BYPASSRLS` to the migration role is
  **not** the fix: it removes the third defense layer for every table in the database.

## Read Models

Public read models consumed by other modules:

- Live in the producing module's migrations.
- Naming: `public_<module>_<concept>`.
- Populated by event handlers or projection jobs.
- Consumers read via the producer's repository contract, not raw SQL across modules.

## Outbox

```sql
CREATE TABLE outbox_messages (
    id              uuid PRIMARY KEY DEFAULT uuidv7(),
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,             -- null = tenant-wide event; see note below
    correlation_id  text NOT NULL,         -- full W3C traceparent, per ADR-0032 § 12
    causation_id    uuid NULL,
    actor_user_id   uuid NULL,
    type            text NOT NULL,         -- assembly-qualified event type name
    topic           text NOT NULL,         -- "learnstack.{module}.{aggregate}"
    partition_key   text NOT NULL,         -- ordering domain: aggregate id, else tenant_id
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

## Idempotency

`idempotency_keys` is the schema the durable `IIdempotencyStore` will read and
write ([ADR-0037](../decisions/0037-idempotency-key-contract.md)). The table and
the store ship apart, per Amendment 1: the schema is a one-way door and shipped
in Packet 6; the store is additive and ships on its ADR-0035 trigger, with
`InMemoryIdempotencyStore` registered until then. Every column below
is forced by the shipped port
(`LearnStack.SharedKernel/Idempotency/IIdempotencyStore.cs`) rather than chosen:
`fingerprint` by `TryClaimAsync`'s third parameter and the `Mismatched`
outcome, `claim_token` by the fence on `Complete` and `Abandon`, `state` by the
tombstone ADR-0037 says is explicitly **not** a release, and the four response
columns by `IdempotentResponse`.

```sql
-- Tenant-owned, tenant-wide. Addressed by its natural key, so it generates no id.
CREATE TABLE idempotency_keys (
    tenant_id    uuid        NOT NULL,
    key          text        NOT NULL,      -- client-chosen nonce inside the tenant's key space
    fingerprint  text        NOT NULL,      -- opaque digest; compared ordinally, never interpreted
    claim_token  uuid        NOT NULL,      -- the fence Complete/Abandon must present
    state        text        NOT NULL,      -- in_flight | completed | unreplayable
    status_code  int         NULL,          -- IdempotentResponse, set only when state = 'completed'
    content_type text        NULL,
    headers      jsonb       NULL,
    body         bytea       NULL,
    claimed_at   timestamptz NOT NULL DEFAULT now(),
    -- ONE expiry column, deliberately. It is the 5-minute claim lease while in_flight
    -- and the 24-hour retention window once the outcome is recorded, so the claim
    -- statement's "the existing row has expired" predicate is one comparison at every
    -- stage. AbandonAsync sets it to now(), which makes the released row satisfy that
    -- same predicate — a release needs no second code path, and learnstack_app never
    -- needs DELETE.
    expires_at   timestamptz NOT NULL,
    CONSTRAINT pk_idempotency_keys PRIMARY KEY (tenant_id, key),
    CONSTRAINT ck_idempotency_keys_state
        CHECK (state IN ('in_flight', 'completed', 'unreplayable')),
    -- ADR-0037's replay cap is 256 KiB "headers included", so this is a floor
    -- under it rather than the cap itself — the database can bound the body
    -- cheaply and the store enforces the headers-inclusive total, which is where
    -- the serialized size actually lives.
    CONSTRAINT ck_idempotency_keys_body_size
        CHECK (body IS NULL OR octet_length(body) <= 262144),
    -- Matches [Idempotent]'s header bounds, so a key the API accepted always fits.
    CONSTRAINT ck_idempotency_keys_key_length
        CHECK (length(key) BETWEEN 8 AND 128),
    -- The state and the response columns are one fact, not two. ADR-0037
    -- Amendment 2's claim statement reports a `completed` row as replayable, so a
    -- `completed` row with no status code and no body makes the caller replay a
    -- response that does not exist; and the reclaim branch NULLs all four
    -- alongside `state = 'in_flight'`, so the reverse is equally a lie about what
    -- the row is. content_type stays free in the completed arm — the port defines
    -- it as null for an empty body.
    CONSTRAINT ck_idempotency_keys_outcome CHECK (
        (state =  'completed' AND status_code IS NOT NULL AND body IS NOT NULL)
     OR (state <> 'completed' AND status_code IS NULL AND content_type IS NULL
                              AND headers IS NULL AND body IS NULL))
);

-- Serves both the retention sweep and the per-tenant admission count, tenant first
-- per the composite-index rule.
CREATE INDEX ix_idempotency_keys_tenant_id_expires_at
    ON idempotency_keys (tenant_id, expires_at);

ALTER TABLE idempotency_keys ENABLE ROW LEVEL SECURITY;
ALTER TABLE idempotency_keys FORCE  ROW LEVEL SECURITY;

CREATE POLICY idempotency_keys_isolation ON idempotency_keys
    USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

-- No DELETE for learnstack_app: a release is an UPDATE that backdates expires_at, and
-- withholding DELETE from the request-path role mirrors outbox_messages. Purging is the
-- audited platform scope's job.
GRANT SELECT, INSERT, UPDATE ON idempotency_keys TO learnstack_app;
GRANT SELECT, DELETE         ON idempotency_keys TO learnstack_platform;
```

Two properties are contract rather than implementation:

- **The store runs as `learnstack_app` and sets its own `app.tenant_id`.** A
  claim is taken *before* the MediatR pipeline reaches `TransactionBehavior`, so
  there is no ambient transaction yet. Each of `TryClaim`, `Complete` and
  `Abandon` opens a short transaction whose first statement is the `SET LOCAL`
  — one of the sanctioned out-of-band setters in
  [ADR-0040](../decisions/0040-ambient-unit-of-work.md). A store reaching for
  `learnstack_platform` would be invisible to the isolation suite.
- **Capacity is admission, not eviction.** When a tenant's unexpired-record
  count is at its ceiling the store answers `CapacityExhausted`; it never drops
  a live record to make room.

**Packet 6 ships the table; `PostgresIdempotencyStore` ships on its trigger** —
the first endpoint carrying `[Idempotent]`, or the first deployment running more
than one instance, whichever comes first
([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)). The table is
one-way-door schema; the implementation is not, and `InMemoryIdempotencyStore`
remains the registered default until then.

The retention sweep that deletes rows past `expires_at` is owned by
[Phase 11](../roadmap/phase-11-production-hardening.md), alongside the other
recurring maintenance jobs. Until it runs, rows accumulate and the admission
ceiling is what bounds the table — which is the correct failure, not a silent
one.

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
- A `DbCommandInterceptor` — **not** a connection-checkout interceptor — is to guard
  the context. **It does not exist yet**; Packet 6 ships the setter and the policies
  it backs up, and the first tenant-owned read on a request path is Packet 7's, which
  is where it belongs. Checkout happens before `TransactionBehavior` opens the transaction that
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
- More than one **connection**, or more than one **transaction**, within one
  logical transaction. Several module `DbContext`s enlisted on the *one*
  connection `IUnitOfWork` owns are fine and are the house pattern
  ([ADR-0040](../decisions/0040-ambient-unit-of-work.md)) — the failure this
  rule exists to prevent is two independent contexts each opening their own
  transaction, leaving a window in which one has committed and the other has
  not. This line previously read "Multiple `DbContext` instances within one
  logical transaction", which also forbade the safe shape.
- Reading a tenant-owned table from a transaction that has not issued its own
  `SET LOCAL` — the ambient one, or one of the closed set of out-of-band setters
  in [ADR-0040](../decisions/0040-ambient-unit-of-work.md). `SET LOCAL` is
  connection- and transaction-local, so such a read returns **zero rows** under
  the corrected policy — silently, because a policy that filters everything looks
  exactly like a table with no matching data.
- Tenant tables without RLS in production migrations (CI rejects).
