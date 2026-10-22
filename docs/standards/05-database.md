# 05 — Database Standards

**Status:** Active
**Derives from:** [ADR-0002 Initial Architecture](../decisions/0002-initial-architecture.md)
(Amendments 1 + 2),
[ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
(Amendment 1: Organization Scope; **Amendment 3: corrected RLS policy template and
database role model**; **Amendment 5: the UPDATE/DELETE guards exclude an
organization-scoped session from tenant-wide rows**; **Amendment 6: exact INSERT
scope and independently enforced parent mirrors**),
[ADR-0006 Events and Outbox](../decisions/0006-events-and-outbox.md)
(Amendment 1: Dapr pub/sub dispatch transport),
[ADR-0038 Cross-Cutting Port and Event Contracts](../decisions/0038-cross-cutting-port-and-event-contracts.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0031 PostgreSQL — Start on 18.x](../decisions/0031-postgresql-major-version.md)
(Amendment 1: the built-in is `uuidv7()`),
[ADR-0037 Idempotency Key Contract](../decisions/0037-idempotency-key-contract.md),
[ADR-0039 The Optimistic Concurrency Token](../decisions/0039-optimistic-concurrency-token.md),
[ADR-0040 The Ambient Unit of Work](../decisions/0040-ambient-unit-of-work.md),
[ADR-0044 The Audit Write Path](../decisions/0044-audit-write-path.md)
(§ 9 with Amendment 1: `audit_log` is tenant-owned and org-scoped, `audit_config`
tenant-owned and tenant-wide; **Amendment 3: the platform sentinel's guards sit on the
write path, not on the `tenants` CHECK**),
[ADR-0045 The Entitlement and Feature-Flag Socket](../decisions/0045-entitlement-and-feature-flag-socket.md)
(§ 5: `platform_killswitches` is the second platform-scoped table; **Amendment 1: it
ships unwritten**).
Public-read additions derive from
[ADR-0052](../decisions/0052-anonymous-public-read-boundary.md).
Tenant API authentication additions derive from
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md).
Producer additions derive from
[ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
Delivery/recovery additions derive from
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md).
Ordinary-job acquisition guards derive from
[ADR-0047](../decisions/0047-transactional-background-job-runtime.md).
Vendor-maintenance configuration and source deadlines derive from
[ADR-0058](../decisions/0058-bounded-stock-hangfire-maintenance.md).
Actual runtime/source ownership additionally derives from
[ADR-0059](../decisions/0059-owned-hangfire-server-generations.md), qualified by
[ADR-0060](../decisions/0060-fatal-hangfire-startup-and-unsafe-unwind.md).

PostgreSQL schema, EF Core, and migration conventions.

## Database

- **PostgreSQL 18+** in all environments.
- One database per environment; single schema (`public`).
- One `DbContext` per module (not one global).
- Schema migrations live with the owning module.

With [P02d-1 complete](../roadmap/phase-02d-delivery-log.md#delivery-record-p02d-1),
five migration chains exist: Tenancy, Platform, Customization, Audit and
Education. P02b-3 Step 1 brings their inventory to twenty-three data tables and
five migration-history tables.

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
| Shared inbox / delivery ledger | Tuple primary key; [canonical DDL](#inbox) | `inbox_messages` / `event_deliveries` |

`learnstack_` prefix reserved for system-wide objects (roles, extensions).

Two names on `audit_log` diverge, both because two Accepted records fix them, and
neither is a precedent:

- the trigger `audit_log_append_only_guard`, carried in that spelling by
  [ADR-0033](../decisions/0033-audit-durability-model.md),
  [ADR-0044 § 9](../decisions/0044-audit-write-path.md) and the DDL in
  [Audit Subsystem § 7](../architecture/31-audit-subsystem.md);
- the primary key `audit_log_pkey`, written that way in
  [ADR-0033](../decisions/0033-audit-durability-model.md) and
  [ADR-0016](../decisions/0016-audit-log-subsystem.md).

Everything else on that table takes the forms above — `ck_audit_log_outcome`,
`ix_audit_log_tenant_id_timestamp`, `fn_audit_log_append_only`,
`tg_audit_log_no_truncate` — and so does every trigger, function and constraint a
migration adds from here.

## Tenant-Owned and Organization-Scoped Tables

P02b-5's [tenant API boundary](../decisions/0064-tenant-api-authentication-boundary.md)
adds no table, role, grant or RLS exception. Its actor/scope projection is not a
persisted User/Membership model; Phase 03 owns that identity domain. AccountSite
uses the existing application-role physical ReadOnly owner and independently
checked host scope. The canonical isolation templates below remain unchanged.

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
> one permissive isolation policy and two restrictive write guards.

```sql
CREATE TABLE courses (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,                -- org-scoped; null = tenant-wide
    slug_key        varchar(160) NOT NULL,    -- stable authoring handle; NOT routable
    -- No title, no description, no slug: translatable columns live in
    -- course_translations per ADR-0008. See § Translation satellite tables.
    -- ... domain columns ...
    created_at      timestamptz NOT NULL,       -- supplied by MarkCreated from IClock
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
    -- Composite unique on (tenant_id, id) exists solely so child tables can
    -- carry a composite FK. See § Foreign keys between tenant-owned tables.
    -- It contains the primary key, so § Soft Delete's partial rule exempts it.
    CONSTRAINT ux_courses_tenant_id_id   UNIQUE (tenant_id, id),
    CONSTRAINT ck_courses_slug_key_format CHECK (
        slug_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
        AND slug_key !~ '^[0-9a-f]{32}$'
        AND slug_key !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    )
);

-- slug_key is unique per tenant among live rows. A table with deleted_at takes a
-- partial unique INDEX: PostgreSQL has no partial UNIQUE table constraint, and a
-- table-wide one lets a deleted row hold its key forever (§ Soft Delete).
CREATE UNIQUE INDEX ux_courses_tenant_id_slug_key
    ON courses (tenant_id, slug_key) WHERE deleted_at IS NULL;

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
            (organization_id IS NULL
             AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
        )
    );

-- The tenant-scope hatch above widens READS across organizations, which is what
-- cross-org reporting needs. It must not widen writes — but USING is also what
-- selects the rows an UPDATE may target, and for DELETE it is the ONLY gate
-- (PostgreSQL has no WITH CHECK for DELETE). Without the restrictive policy
-- below, a tenant-scope session could delete another organization's rows, or
-- reassign them to itself. Each guard's first arm is AND-ed with "the session has no
-- organization", because the bare `organization_id IS NULL` admitted the reverse: an
-- ORG-scoped session rewriting the tenant-wide fallback every other organization reads.
-- Measured on the shipped schema before the correction.
CREATE POLICY courses_org_write_guard ON courses
    AS RESTRICTIVE FOR UPDATE
    USING (
        -- A tenant-wide row (organization_id IS NULL) belongs to no organization, so a
        -- session that HAS one is writing outside its own when it touches one. The
        -- first arm therefore requires the session to be tenant-scope as well.
        (organization_id IS NULL
         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
    );

CREATE POLICY courses_org_delete_guard ON courses
    AS RESTRICTIVE FOR DELETE
    USING (
        -- A tenant-wide row (organization_id IS NULL) belongs to no organization, so a
        -- session that HAS one is writing outside its own when it touches one. The
        -- first arm therefore requires the session to be tenant-scope as well.
        (organization_id IS NULL
         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
    );

-- No standalone index on tenant_id: ux_courses_tenant_id_id is a b-tree with
-- tenant_id leading and serves a tenant-only lookup (the partial slug_key index
-- serves only queries that exclude deleted rows). One composite index carries the
-- organization dimension.
-- Deliberately NOT partial: the policy's `organization_id IS NULL` branch matches
-- every tenant-wide row, and a b-tree indexes NULLs, so the non-partial form serves
-- both branches of the predicate.
CREATE INDEX ix_courses_tenant_id_organization_id ON courses (tenant_id, organization_id);
```

### Foreign keys between tenant-owned tables

**Every foreign key between two tenant-owned tables is composite on `tenant_id`.**

PostgreSQL evaluates referential integrity at **runtime** as a security-restricted
operation on behalf of the table owner, and those RI triggers are **not subject to Row
Level Security** ([PostgreSQL row-security documentation](https://www.postgresql.org/docs/18/ddl-rowsecurity.html)).
Constraint validation also checks existing rows regardless of their RLS visibility:
`ALTER TABLE … ADD CONSTRAINT` checks all existing rows unless `NOT VALID` skips
that scan; `VALIDATE CONSTRAINT` performs the deferred check
([PostgreSQL ALTER TABLE documentation](https://www.postgresql.org/docs/18/sql-altertable.html)).
An ordinary `UPDATE` backfill remains subject to RLS; see [Data Migrations](#data-migrations).
A single-column `lessons.course_id → courses.id` therefore lets a row in tenant A
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
        ON DELETE RESTRICT
);
-- Plus an index led by (tenant_id, course_id) — § Indexes: index every foreign key.
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

The standing exception is a **child inside an aggregate boundary**, which
cascades from its own root: it is not an independent row, and outliving its root
would leave it referring to nothing. That is deletion *within* an aggregate, not
deletion *of* one, and it is why the two fences differ. Two shapes are in the
class — **translation satellites** (`course_translations` and `lesson_translations`,
implemented in P02d-1 Step 2), and `tenant_level_taxonomy_items`, whose composite key
names the taxonomy revision it belongs to and which is meaningless without it. A cascade from anything that is *not* a
root's own child is still a decision, not a convenience, and it needs a record.

**Implemented in P02d-1 Step 2:** `Course` and `Lesson` are separate aggregate roots;
`lessons → courses` is `ON DELETE RESTRICT`. Each root contains its own translations,
whose foreign key cascades from that root. The
[Education data model](../modules/education/README.md#data-model-and-invariants)
owns this interim hierarchy and the preservation obligations for Phase 05's versioned
model. This decision does not introduce a content-deletion command.

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
  every organization-scoped table, not an optional hardening step. `WITH CHECK`'s
  tenant-wide arm also requires an unset or empty `app.organization_id`: an
  organization-scoped session may read a tenant-wide row but may neither insert,
  update nor delete one. P02d-1 Step 1 applied this accepted
  [ADR-0003 Amendment 6](../decisions/0003-tenant-isolation-defense-in-depth.md#amendment-6--insert-scope-and-parent-mirrors-2026-09-14)
  correction to the existing `tenant_settings` and `audit_log` policies by forward
  migrations; Step 2 applies it to all four Education tables. Applied migrations
  are not rewritten.
- The session variable names `app.tenant_id`, `app.organization_id`, `app.scope` and
  `app.resolving_host` are canonical and the set is closed; do not invent alternatives
  (`app.current_tenant_id`, `learnstack.tenant_id`, …). `app.tenant_id` and
  `app.organization_id` are set inside the ambient transaction — by
  `TransactionBehavior` in the general case, and by each of the out-of-band setters on
  the transaction it opens ([Security Standards § The out-of-band setters](11-security.md)).
  `app.scope` has **no carrier**: it derives from the actor's role plus a declared
  tenant-wide operation, and roles land with `Membership` / `Role` in
  [Phase 03](../roadmap/phase-03-identity-admin.md) — after
  [Phase 02b](../roadmap/phase-02b-events-auth.md)'s authenticated principal, which is the
  prerequisite and not the carrier. Until then the tenant-scope read hatch is unreachable
  — the correct default. Its placement rule is unchanged and applies
  the moment a carrier exists. `app.resolving_host` is set by
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
              'organization_id is immutable after insert (table %)',
              TG_TABLE_NAME
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
  ([ADR-0044 § 9](../decisions/0044-audit-write-path.md)), its storage prefix
  `tenants/{tenant_id}/organizations/{organization_id}/…` and its cache-key prefix are
  all organization-qualified per
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md), so re-parenting would
  orphan three subsystems at once. Moving content between organizations is a copy, not
  an update.

  The shared function names no primary-key column: translation satellites have no
  `id`, and a violation must remain `23514` rather than a missing-field error.
  Tenancy owns the function; every dependent chain installs its own table triggers
  after Tenancy has applied (§ Migrations). This obligation follows the **org-scoped
  table class**, not the mere presence of `organization_id`: the platform host map
  and tenant-wide outbox metadata are not org-scoped rows. `audit_log` satisfies it
  through its append-only guard, whose refusal of an organization change must be
  proved independently of the role's absent UPDATE privilege.

### Parent organization mirrors

When a child must share its parent's organization, its factory derives the scope
from that parent. The database independently enforces the same equality with a
`SECURITY INVOKER` row trigger on **both INSERT and UPDATE**, in addition to the
tenant-composite foreign key and the immutability trigger. The following is the
canonical form; substitute the owning child, parent and parent-id column:

```sql
CREATE FUNCTION public.fn_lesson_translations_parent_scope() RETURNS trigger
    LANGUAGE plpgsql
    SECURITY INVOKER
    SET search_path = pg_catalog
AS $$
DECLARE
    parent_organization_id uuid;
BEGIN
    SELECT parent.organization_id INTO parent_organization_id
    FROM public.lessons AS parent
    WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.lesson_id
    FOR KEY SHARE;

    IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW.organization_id THEN
        RAISE EXCEPTION 'parent scope is unavailable or mismatched (table %)', TG_TABLE_NAME
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER tg_lesson_translations_parent_scope
    BEFORE INSERT OR UPDATE ON public.lesson_translations
    FOR EACH ROW EXECUTE FUNCTION public.fn_lesson_translations_parent_scope();
```

P02d-1 Step 2 implements this control on `lessons`, `course_translations` and
`lesson_translations`. The applied schema and its planted violations are covered by
[EducationStructureTests](../../backend/tests/LearnStack.Tests.Integration/Database/EducationStructureTests.cs);
[EducationIsolationTests](../../backend/tests/LearnStack.Tests.Integration/Database/EducationIsolationTests.cs)
exercises insertion through separately authenticated `learnstack_app` connections;
[EducationParentScopeTests](../../backend/tests/LearnStack.Tests.Integration/Database/EducationParentScopeTests.cs)
exercises reparenting and the parent lock before the FK executes.

The lookup carries **both** tenant and parent identity, runs under the caller's RLS,
and uses `IS DISTINCT FROM` so tenant-wide `NULL` is compared as a value. A missing,
hidden or mismatched parent produces the same `23514`, without exposing its identity
or scope. `FOR KEY SHARE` holds the parent until the transaction ends, closing the
delete/reinsert race in which the same parent id could otherwise acquire another
organization between the check and the child commit. Schema-qualified relations and
the restricted `search_path` prevent a temporary relation from shadowing the parent.

The composite foreign key remains independent: the trigger does not replace the
tenant-reference invariant. PostgreSQL runs BEFORE triggers ahead of policy and
constraint checks, so the complete stack can refuse an invalid child with `23514`
before RLS (`42501`) or the foreign key (`23503`) executes. Isolation tests exercise
the complete stack and each layer separately, with positive controls. A test-only
control removal or privilege grant must commit in a disposable database before a
separate authenticated `learnstack_app` connection tests the remaining layer; neither
an owner connection nor `SET ROLE` is an isolation proof.

### Translation satellite tables

A `<entity>_translations` table is independently protected as a tenant-owned table
and gets the full template. In the domain it is a contained entity of its parent:

```sql
CREATE TABLE course_translations (
    course_id       uuid NOT NULL,
    tenant_id       uuid NOT NULL,     -- a real column; RLS is per table, never inherited
    organization_id uuid NULL,         -- mirrors the parent; for RLS, never for uniqueness
    locale          varchar(35) NOT NULL,
    title           text NOT NULL,
    slug            varchar(160) NOT NULL,
    -- ... other translatable columns ...
    PRIMARY KEY (course_id, locale),
    CONSTRAINT ck_course_translations_slug_format CHECK (
        slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'
        AND slug !~ '^[0-9a-f]{32}$'
        AND slug !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
    ),
    CONSTRAINT ux_course_translations_tenant_id_locale_slug
        UNIQUE (tenant_id, locale, slug),
    CONSTRAINT fk_course_translations_course
        FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id)
        ON DELETE CASCADE
);

-- Index every foreign key (§ Indexes): neither the primary key, led by course_id,
-- nor the slug key, led by (tenant_id, locale), serves (tenant_id, course_id).
CREATE INDEX ix_course_translations_tenant_id_course_id
    ON course_translations (tenant_id, course_id);
```

It then declares `ENABLE` + `FORCE ROW LEVEL SECURITY` and the same
one-permissive-policy-plus-two-restrictive-guards set as its parent. A satellite with no
policy of its own is unprotected — a check constraint on the parent does not propagate
Row Level Security, and a table holding `title` and `slug` holds the content.

`organization_id` on a satellite carries isolation, never slug uniqueness. It mirrors
the parent through the factory and the independent
[parent-scope trigger](#parent-organization-mirrors); the immutability trigger then
refuses scope changes. Both markers and `IOrganizationScoped` make the ordinary EF
filter sweep reach the satellite even though it has no auditable base class.

For the Education shape implemented in P02d-1, `locale` is the shipped
`LocaleTag`'s canonical spelling in `varchar(35)` (`tr-TR`, `zh-Hans`).
[Localization Standards § Education slug grammar](08-localization.md#education-slug-grammar)
owns the slug grammar; the named checks above enforce its predicate independently of
application validation.

A satellite has its natural primary key and no surrogate `id`, independent audit
timestamps, `row_version` or `deleted_at`. Changes advance the owning root's token and
belong to that root's audit capture. Consequently a draft translation reserves its
slug immediately, and soft-deleting its parent **does not release the slug**. Public
read eligibility must also check the parent; the satellite has no second soft-delete
lifecycle. Phase 05 owns future deletion/release behavior with Phase 04's redirects
and slug registry. `organization_id` is deliberately **absent** from the slug unique
key — see § Constraints and [Localization Standards § Pattern A](08-localization.md).

The foreign key is composite on `tenant_id` for the reason in § Foreign keys between
tenant-owned tables. A composite key that also carried `organization_id` would not work:
the column is nullable, and under the default `MATCH SIMPLE` a foreign key with any null
column is not checked at all — which would silently re-open the cross-tenant reference
hole the composite key exists to close.

### Table classes

Not every table in the tenancy schema is tenant-owned, and applying the template above
to all of them produces a deadlock rather than isolation. Six classes exist, and every
migration states which one its table is.

| Class | Rule | Tables |
|---|---|---|
| **Tenant-owned, org-scoped** | The full template above: `ENABLE` + `FORCE`, one permissive policy `AND`-ing the tenant term with the organization term, explicit `WITH CHECK`, **and** the two `AS RESTRICTIVE` `UPDATE` / `DELETE` guards | any domain table carrying `organization_id`, plus `tenant_settings` — the only org-scoped table in the Packet 6 set — `event_deliveries` (P02b-3 Step 1), `background_jobs`, `background_job_attempts`, `background_job_recipients` (P02b-4 Step 2), and `audit_log`, which Packet 9 added ([ADR-0044 § 9](../decisions/0044-audit-write-path.md)); P02d-1 Step 2 adds `courses`, `lessons` and both translation tables |
| **Tenant-owned, tenant-wide** | The same shape with the organization half of the predicate omitted, and therefore **no** restrictive guards — there is no organization to guard | `organizations`, `tenant_domains`, `tenant_locales`, `tenant_feature_flags`, `platform_entitlement_cache`, `idempotency_keys`, `outbox_messages`, `inbox_messages` (P02b-3 Step 1), `background_job_capacity`, `background_job_reservations` (P02b-4 Step 2), `tenant_content_types`, `tenant_level_taxonomies`, `tenant_level_taxonomy_items`, `customization_generations`, and `audit_config`, which Packet 9 adds ([ADR-0044 Amendment 1](../decisions/0044-audit-write-path.md)) |
| **Tenant-owned, self-keyed** | Identical, except the tenant term is `id = …` because the row's primary key *is* the tenant id | `tenants` |
| **Platform-scoped** | `ENABLE` + `FORCE`, and role-qualified per-command policies: the read is widened by an explicitly declared non-tenant predicate, and writes are either tenant-keyed or reserved to `learnstack_platform` | `platform_host_to_tenant`, `platform_killswitches` |
| **Infrastructure control** | `ENABLE` + `FORCE`, no app policy or privilege; enumerated confined bypass control access under ADR-0047 | `background_job_recurrences`, `background_job_occurrences` |
| **Vendor control** | Pinned stock schema with exact confined role grants and metadata guards under ADR-0047; no tenant payload or app access | The twelve Hangfire schema-23 tables; canonical vendor contract below |

**The EF query filter follows the class.** It is the second layer, not a restatement of
the policy: the filter is what a handler's `IQueryable` meets, the policy is what still
drops the other tenants' rows when the filter is missing.

- **Tenant-owned, org-scoped** — `e.TenantId == currentTenantId` **and**
  `e.OrganizationId == null || e.OrganizationId == currentOrgId`, the null arm being the
  tenant-wide row ([ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)).
- **Tenant-owned, tenant-wide** — `e.TenantId == currentTenantId`, with no organization
  term, for the same reason the class carries no restrictive guards.
- **Tenant-owned, self-keyed** — `t.Id == currentTenantId`. `tenants` has no `TenantId`
  property, so the filter keys on the primary key exactly as the policy does.
- **Platform-scoped** — **no query filter.** `platform_host_to_tenant` is read in order
  to *determine* the tenant, so a tenant-keyed filter would return zero rows on every
  anonymous request and no host would ever resolve. `PlatformHostMapping` carries a
  `TenantId` property and still takes no filter — the property is not the test, the
  class is. `platform_killswitches` takes none either, and could not: it carries no
  tenant column for a filter to key on.

**A table is platform-scoped only when no tenant predicate can isolate it.** Two tables
qualify, in two shapes. `platform_host_to_tenant` is read **before the tenant is known**,
so a tenant-keyed policy returns nothing on the path that decides the tenant.
`platform_killswitches` is read inside a tenant request and still has no tenant of its
own — [ADR-0045 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md) is the
decision this rule asked for, and the class fits for the stated reason rather than by
analogy: the rows belong to no tenant, so there is nothing for a tenant predicate to
isolate. A third platform-scoped table is still a decision, not a convenience. ADR-0047
separately accepts the infrastructure-control and vendor-control classes above;
these carry neither tenant content nor general Platform-role access.

`platform_entitlement_cache` does **not** qualify despite its name: `IFeatureFlags`
resolves the tenant from `ITenantContext` and throws `TenantContextMissingException`
when there is none, and it reaches the projection through
`IEntitlementProvider.GetAsync(TenantId)` rather than by reading the table
([ADR-0045 § 2](../decisions/0045-entitlement-and-feature-flag-socket.md)); the
provider's `RefreshAsync` is driven by `PUT /api/internal/tenants/{id}/entitlements`,
which carries the tenant id in its path. Both directions have a tenant, so the table
keeps the tenant-owned template and the application role never holds a table-wide read
of every tenant's plan.

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
- **Enumerating tenants requires explicit privileged authority.** `SELECT … FROM
  tenants` with no `app.tenant_id` returns zero rows. Operator list screens use
  `EnterPlatformAdminScope(reason)`. ADR-0047's confined recurrence router uses
  only its [minimal lifecycle projection](#background-jobs), with no general
  platform job or tenant-selection API. The application role cannot enumerate
  the customer list.

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

#### `audit_log` and `audit_config` — one chain, two classes

`audit_log` is **tenant-owned, org-scoped** and takes the template above
**unmodified** — `ENABLE` + `FORCE`, one permissive policy `AND`-ing the tenant term
with the organization term, an explicit `WITH CHECK`, and both `AS RESTRICTIVE` guards.
It carries `organization_id`, and the class follows the column.

`audit_config` is **tenant-owned, tenant-wide**. It has no `organization_id` column —
its overrides are keyed `(tenant_id, module, operation)`, and nothing asks a tenant to
classify one organization's operations differently from another's — so it takes the
same shape with the organization half of the predicate omitted and, for the reason that
class gives, no restrictive write guards.
[ADR-0044 § 9](../decisions/0044-audit-write-path.md) put both tables in the org-scoped
class; its Amendment 1 corrects that, and this is the corrected reading.

Both take their policy from the class, so none is restated here: a template copied for
one more table is how the corpus last shipped a broken policy into four files at once.

`audit_log` takes the class's **policy** unmodified and deliberately not the template's
`ix_<table>_tenant_id_organization_id`: its readers are tenant-scope, so the arm that
fires is `app.scope = 'tenant'`, which no index serves, and a fifth index on a
high-volume append-only table is a write cost paid on every row for a predicate arm
nothing shipped takes. The four indexes it does carry, and the reasoning, are in
[Audit Subsystem § 7](../architecture/31-audit-subsystem.md).

They ship in a **fourth migration chain**, owned by
`LearnStack.Modules.Audit.Infrastructure`'s `AuditDbContext`, on the pattern Packet 8 set
for Customization.

`audit_log`'s class binds the audit store's own writes. A row whose `organization_id` is
non-null while `app.organization_id` is unset fails `WITH CHECK` — the unset GUC reads as
the empty string, `NULLIF` makes it `NULL`, and the comparison is `NULL`, which is false.
The two standalone writers therefore announce **both** session variables from the draft
as their first statements. The ambient transaction uses the bounded read-mode
sequence in [Security Standards § Tenant Context](11-security.md#tenant-context). Every
`denied` row for an org-scoped resource travels that path.

Two departures from what the rest of this document assumes, each for its own reason:

- **`audit_log` carries no foreign key to `tenants`.** The platform-scope row carries
  `TenantId.PlatformSentinel`, which has no `tenants` row by construction, and a foreign
  key admits no exception — not for `learnstack_platform` and not under `BYPASSRLS`,
  because it is a constraint rather than a policy. Independently: an audit log that
  cascades or restricts on tenant deletion is not an audit log, because the record of
  what happened to a tenant has to outlive the tenant. `audit_config` keeps its FK — it
  is live configuration rather than history, and its rows are meaningless without the
  tenant they configure.
- **`tenants` carries `CONSTRAINT ck_tenants_not_platform_sentinel CHECK (id <>
  '00000000-0000-7000-8000-000000000002')`** — the reserved platform tenant id, exposed
  as `TenantId.PlatformSentinel`
  ([ADR-0044 § 1](../decisions/0044-audit-write-path.md)), and the one tenant id that
  must never name a row in the table every other tenant id names. What the `CHECK` buys
  is that the sentinel never enters the registry: `platform_host_to_tenant` carries
  `fk_platform_host_to_tenant_tenant`, so no host can map to it, no request context
  resolves it, and no tenant-keyed policy matches the rows it owns.

  **The `CHECK` is the backstop, not the control.** A constraint on `tenants` cannot
  stop the sentinel from being *announced* on `app.tenant_id`, and the one announcement
  path that takes a caller-supplied id — `SetProvisioningTenantContextAsync` — passes on
  whatever it is handed. The guards therefore sit where the value enters:
  `SetProvisioningTenantContextAsync` refuses `TenantId.PlatformSentinel` exactly as it
  already refuses `Guid.Empty`, and `Tenant.Create` refuses it in the factory
  ([ADR-0044 Amendment 3](../decisions/0044-audit-write-path.md)). Neither guard stands
  in the way of the writer the sentinel exists for: `WritePlatformScopeAsync` issues
  parameterised SQL on the scope's own platform-role connection and touches neither the
  unit of work nor an aggregate factory.

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

#### `platform_killswitches` — platform-scoped

A killswitch is one platform-wide switch per key, and it is not tenant data:
`key varchar(150) PRIMARY KEY`, `is_enabled boolean NOT NULL`, `reason text NULL`,
`toggled_at timestamptz NOT NULL`, `toggled_by uuid NULL` — no `tenant_id` and no
foreign key. It ships in the **Tenancy** migration chain
([ADR-0045 § 5](../decisions/0045-entitlement-and-feature-flag-socket.md)).

The cap on `key` is the migration's, on the precedent `platform_entitlement_cache` set:
an unbounded primary key is an unbounded index, and 150 characters holds any
`{module}.{capability}` key the registry declares. Bare `text` is what this section first
declared and is the bound it did not carry.

The table exists because the alternative cannot be written at all. A killswitch held in
`tenant_feature_flags` "for the sentinel platform tenant" fails
`fk_tenant_feature_flags_tenant REFERENCES tenants (id)`, since the sentinel has no
`tenants` row by the `CHECK` above — and a foreign key is a constraint, so no role and
no `BYPASSRLS` attribute moves it.

Policies are role-qualified in the same shape as `platform_host_to_tenant`'s, with the
read widened rather than keyed:

```sql
ALTER TABLE platform_killswitches ENABLE ROW LEVEL SECURITY;
ALTER TABLE platform_killswitches FORCE  ROW LEVEL SECURITY;

-- READ is unconditional, and that is the point: the switch is global by construction,
-- so hiding it from the role that has to honour it would only fail open.
CREATE POLICY platform_killswitches_read ON platform_killswitches
    FOR SELECT TO learnstack_app
    USING (true);

-- No write policy for learnstack_app, and no write privilege either — see the matrix
-- below. Every toggle runs as learnstack_platform inside
-- EnterPlatformAdminScope(reason), which is what gives tenancy.killswitch.toggle a real
-- actor and a MUST-class audit row. No such caller ships in Packet 9 — see below.
```

`USING (true)` widens nothing: there is no tenant term to widen, and the `GRANT` is what
bounds the role instead — `learnstack_app` holds `SELECT` and nothing else, so the read
policy is the only policy it can exercise. The owner is denied by the same mechanism as
on `platform_host_to_tenant`: the policy names `learnstack_app`, so under `FORCE` none
applies to `learnstack_migration`.

**This is the first table for which "no tenant context ⇒ zero rows" is deliberately
false**, and the shipped whole-schema sweep has to be taught the difference.
`platform_host_to_tenant` satisfies that property without being asked to — both arms
of its read are `NULL` when nothing is announced — so
`Unsetting_tenant_context_returns_zero_rows_through_RLS` has swept every ordinary
table in `public` with no exception at all, which is what makes it worth having. The
first killswitch row turns it red for correct behaviour. It is taught the distinction
**without** a name-based inclusion list: it keeps enumerating the catalogue, and what
changes is the expectation — every table it finds answers zero except the one declared
exception, and a second assertion pins the set of `public` policies whose predicate is
`true` to exactly that exception. A sweep over a hand-written list of names fails open,
which is how a second permissive policy on `outbox_messages` once passed the whole
suite. The positive half is worth asserting too, because nothing asserts it today: with
no context, `learnstack_app` reads **all** of `platform_killswitches`, which is what
`USING (true)` exists to guarantee.

**Packet 9 shipped the table and the read path, and no writer.** The reason is
reachability rather than scheduling: every toggle runs inside
`EnterPlatformAdminScope(reason)`, the registered `IPlatformAdminGate` is
`DenyAllPlatformAdminGate`, and nothing can enter that scope until the Platform-scope
permission arrives with the registry in
[Phase 03](../roadmap/phase-03-identity-admin.md) — which owns the toggle command, its
permission and its runbook
([ADR-0045 Amendment 1](../decisions/0045-entitlement-and-feature-flag-socket.md)). The
`learnstack_platform` write grant in the matrix below is written ahead of its caller,
and no runtime path puts a row in the table until that phase lands. Every gated read
honours a flipped switch the day one exists; what is absent is the flipping.

The overlay is read through the L1 cache and invalidated on toggle, so the table itself
is touched on a cache miss rather than on every request
([Feature Flags § Killswitch Pattern](../architecture/21-feature-flags.md)).

### Database roles

Four roles, four **separate login credentials**, four connection strings. The model is
closed; a fifth role requires an ADR.

| Role | Connection string | Present in | Used by | RLS posture |
|------|---|---|---------|-------------|
| `learnstack_migration` | `ConnectionStrings:Migration` | the deploy job / `make migrate` only — **never** in API or worker runtime configuration | `dotnet ef database update`; **owns** every table | `NOBYPASSRLS`; `FORCE ROW LEVEL SECURITY` denies ownership any bypass |
| `learnstack_app` | `ConnectionStrings:Default` | API host, worker host | every request-path and job-path `DbContext` | `NOBYPASSRLS` |
| `learnstack_platform` | `ConnectionStrings:PlatformAdmin` | API host, worker host | `EnterPlatformAdminScope(reason)` and nothing else | `BYPASSRLS` |
| `learnstack_outbox_admin` | `ConnectionStrings:OutboxDispatcher` | API host only; never Seeder or web | confined dispatcher infrastructure only | `BYPASSRLS` |

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
the executable form.

**Idempotent is not the same as convergent, and the script is both.** The
`\gexec` creates fire only when a role is absent; the `ALTER ROLE` block that
follows runs unconditionally, re-asserting each password and each of
`NOBYPASSRLS` / `NOSUPERUSER` / `NOCREATEDB` / `NOCREATEROLE` / `NOREPLICATION`,
and then revoking every role membership the four roles hold. That is what makes
re-applying the file the documented recovery path for a drifted password, an
out-of-band `SUPERUSER`, or a `GRANT learnstack_platform TO learnstack_app` —
the last of which leaves `learnstack_app`'s own attributes reading correctly and
still puts every policy in the database one `SET ROLE` away from inert.
`TheRolesScriptIsIdempotent` proves a re-run does not error;
`TheRolesScriptTakesBackAnEscalatedAttribute` and
`TheRolesScriptRevokesAnEscalatedMembership` prove it converges. It also revokes `CONNECT, TEMPORARY` from `PUBLIC` before granting, which
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
-- `courses` before its owning migration; measured, it does exactly that.
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
| `tenants` | `SELECT, INSERT, UPDATE` | `SELECT, INSERT, UPDATE, DELETE` | Lifecycle-column `SELECT` only; exact [projection grant](#job-lifecycle-projection) |
| `organizations` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT, INSERT, UPDATE, DELETE` | Lifecycle-column `SELECT` only; exact [projection grant](#job-lifecycle-projection) |
| `tenant_domains` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_locales` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_settings` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_feature_flags` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT, INSERT, UPDATE, DELETE` | — |
| `platform_entitlement_cache` | `SELECT, INSERT, UPDATE` | `SELECT, DELETE` | — |
| `platform_host_to_tenant` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT, INSERT, UPDATE, DELETE` | — |
| `platform_killswitches` | `SELECT` | `SELECT, INSERT, UPDATE, DELETE` | — |
| `idempotency_keys` | `SELECT, INSERT, UPDATE` | `SELECT, DELETE` | — |
| `inbox_messages` | `SELECT`, `INSERT (tenant_id, subscription_id, event_id)` | — | — |
| `event_deliveries` | `SELECT`, acceptance-column `INSERT`, `UPDATE (processed_at)`; exact lists [below](#inbox) | — | routing/status-column `SELECT`, scheduling/status-column `UPDATE`; exact lists [below](#inbox) |
| `outbox_messages` | `SELECT, INSERT` | `SELECT, DELETE` | `SELECT`, `UPDATE (processed_at, attempts, last_error, available_after, locked_by, locked_until, terminal_reason, dead_lettered_at)` (P02b-2 Step 1) |
| `background_jobs` | RLS `SELECT`; envelope `INSERT`, admission/checkpoint `UPDATE` | — | Routing/status `SELECT, UPDATE` |
| `background_job_attempts` | RLS `SELECT`; receipt `INSERT`, evidence `UPDATE` | — | Enumerated metadata `SELECT`, excluding outcome DTO |
| `background_job_capacity` | RLS `SELECT, INSERT`; immutable-column `UPDATE` for row locking | — | `SELECT` |
| `background_job_reservations` | RLS `SELECT, INSERT`; positive-evidence release `UPDATE` | — | `SELECT` |
| `background_job_recurrences` | — | — | Manifest `INSERT`, control `SELECT, UPDATE` |
| `background_job_occurrences` | — | — | Slot `INSERT`, control `SELECT, UPDATE` |
| `background_job_recipients` | RLS `SELECT`; child receipt `UPDATE` | — | Route `INSERT`, routing `SELECT, UPDATE` |
| `job`, `jobqueue`, `server`, `lock`, `set`, `hash`, `list`, `aggregatedcounter` | — | — | `SELECT, DELETE`; exact stock-column `INSERT, UPDATE` [below](#p02b-4-stock-hangfire-control-storage) |
| `jobparameter` | — | — | `SELECT`; exact stock-column `INSERT, UPDATE` |
| `state` | — | — | `SELECT`; exact stock-column `INSERT` |
| `counter` | — | — | `SELECT, DELETE`; exact stock-column `INSERT` |
| `schema` | — | — | `SELECT` for schema-23 verification |
| `tenant_content_types` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_level_taxonomies` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `tenant_level_taxonomy_items` | `SELECT, INSERT, UPDATE, DELETE` | `SELECT` | — |
| `customization_generations` | `SELECT, INSERT, UPDATE` | `SELECT` | — |
| `audit_log` | `SELECT, INSERT` | `SELECT, INSERT, DELETE`, `UPDATE (actor_email, ip_address, user_agent, before_state, after_state, changes)` | `INSERT` only (P02b-2 Step 1) |
| `audit_config` | `SELECT` | `SELECT` | — |
| `courses` | `SELECT, INSERT, UPDATE` | `SELECT` | — |
| `lessons` | `SELECT, INSERT, UPDATE` | `SELECT` | — |
| `course_translations` | `SELECT, INSERT` | `SELECT` | — |
| `lesson_translations` | `SELECT, INSERT` | `SELECT` | — |

ADR-0046 accepts the two P02b-2 cells above. Step 1 implements their migrations
and exact-grant proofs; both independent review rounds are complete in the
[delivery record](../roadmap/phase-02b-delivery-log.md#p02b-2-step-1--decision-schema-and-confined-composition-2026-10-12).
Outbox status grants land in the Platform chain. The audit
INSERT grant lands in a new Audit-chain migration after `audit_log` exists, never
in initdb. No audit SELECT/UPDATE/DELETE, business access or role membership is
added. P02b-3 Step 1 implements the exact [Inbox/ledger grants](#inbox). The bypass
scheduler has no payload or causal-envelope SELECT and no business-completion
UPDATE; `learnstack_app` holds RLS-protected acceptance/completion permissions.
Neither store grants access to `learnstack_platform` or `PUBLIC`.

The four Education grants are **implemented in P02d-1 Step 2** and checked against
the applied schema. Root UPDATE supports publication and concurrency;
satellite UPDATE/DELETE and root DELETE have no Phase 02d command and are not
pre-granted. Their later introduction requires the owning command's decision and
coverage. No Education privilege is granted to `PUBLIC` or the outbox role.

**`audit_log`'s `UPDATE` is column-restricted, and the column list is the control.**
`learnstack_platform` may rewrite only the six columns a GDPR erasure touches —
`actor_email`, `ip_address`, `user_agent`, `before_state`, `after_state`, `changes`;
`outbox_messages`' dispatcher grant is the existing precedent for the form. Append-only
then holds in three layers, each stopping a different actor, measured on PostgreSQL
18.6: `learnstack_app` by the **absent privilege** (`42501`); a `learnstack_platform`
`UPDATE` touching any other column by the **column-level `GRANT`**, before the trigger
runs; and the table **owner** — which no grant bounds, because ownership carries every
privilege implicitly, and which `FORCE ROW LEVEL SECURITY` does subject to the table's
own policies, though a migration session satisfies them by announcing the row's tenant —
only by the `audit_log_append_only_guard` trigger. The trigger is not redundant with the
grant: it is the layer that still binds `learnstack_migration` once that session has a
tenant to announce ([ADR-0044 § 9](../decisions/0044-audit-write-path.md)).

Five things the matrix cannot express, and one it must not be asked to:

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
- **`audit_config` has no writer in the matrix**, and that is deliberate rather than an
  omission. Packet 9 creates the table and both runtime roles hold `SELECT`, so the only
  role that can insert a row is the owner and the table is empty until a tenant authors
  one. The editor that authors it lands with the Studio tenant-settings screens in
  [Phase 06](../roadmap/phase-06-renderer-admin-studio.md), gated by an audit permission
  from the registry [ADR-0044](../decisions/0044-audit-write-path.md) places in
  [Phase 03](../roadmap/phase-03-identity-admin.md). `INSERT, UPDATE, DELETE` for
  `learnstack_app` lands in that phase's migration, beside the command that needs it —
  not ahead of it. Packet 9's own test for the override branch seeds the row as the
  migration role.

#### How `EnterPlatformAdminScope(reason)` reaches `learnstack_platform`

**By a second connection, not by `SET ROLE`.** `learnstack_app` is **not** a member of
`learnstack_platform`; no `GRANT learnstack_platform TO learnstack_app` exists. The
composition root registers a keyed `PrivilegedDataSource` facade from
`ConnectionStrings:PlatformAdmin`; its only sanctioned consumer is
`LearnStack.Infrastructure.MultiTenancy.PlatformAdminScope`. The facade owns the
separately credentialed lazy Npgsql pool without exposing it. Every synchronous or
asynchronous acquisition checks the independent ordinary-job ceiling before pool
creation or reuse, under [ADR-0047](../decisions/0047-transactional-background-job-runtime.md).
`IPlatformAdminScope.EnterAsync(reason, …)` opens a **connection and a transaction** on
that guarded source and returns an `IAsyncDisposable` handle carrying both;
disposing without committing rolls back. Module code cannot resolve the data source;
`Platform_DataSource_Resolved_Only_By_PlatformAdminScope` enforces that.

An earlier wording here — and in the glossary — said the scope opens "a DI scope whose
`DbContext` is built on that data source". A connection is what shipped, for a reason
worth keeping: every module `DbContext` is bound to `IUnitOfWork.Connection`, which comes
from the application data source the composition root guards by name to be
`learnstack_app`, so a DI-resolved context on the platform source cannot be built without
reopening that guard. Nothing is foreclosed — EF can be constructed on the handle's
connection by whoever needs it — and ADR-0003, which is the Accepted authority, says only
"a second connection".

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
`EnterPlatformAdminScope` throws **on entry** — on the first call, naming the missing
`ConnectionStrings:PlatformAdmin`, so a host that never enters the scope still boots,
every test fixture included — rather than degrading to `learnstack_app`; and, **since
[Packet 9](../roadmap/phase-02a-kernel-tenancy.md)**, by an audit row written on the
scope's own connection before the operation runs, in a transaction of its own that
**commits before** the transaction the caller works in begins — so an operation that later
fails, throws or is abandoned is still recorded. (The row first rode the caller's
transaction, where rolling the work back erased the only record that the access had
happened.) An entry whose row cannot be written, or whose slug the catalogue does not
declare, is **refused**: no transaction is handed out for it. The `Warning` line the scope has carried
since Packet 7 stays beside the row, now carrying the row's id — the row is the durable
record, the line is the real-time signal. That row is written as
`learnstack_platform`, through `IAuditStore`'s fourth write method
`WritePlatformScopeAsync` ([ADR-0044 § 10](../decisions/0044-audit-write-path.md)), and
carries `TenantId.PlatformSentinel` — see § `audit_log` and `audit_config` above —
because a cross-tenant operation has no tenant of its own and `audit_log` is itself
tenant-owned.

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

Education's plain translation satellites have no independent token. `AddTranslation`
advances the owning `Course` or `Lesson` token in the same save as the new member.
[EducationPersistenceTests](../../backend/tests/LearnStack.Tests.Integration/Database/EducationPersistenceTests.cs)
loads competing copies of each root, adds different locales, and proves that the
losing concurrency write leaves no satellite row. Its capture assertions keep the
satellite change inside that root's audit subject. P02d-2 Step 3 supplies the six
Education commands; [EducationWriterTests](../../backend/tests/LearnStack.Tests.Integration/Database/EducationWriterTests.cs)
proves exact versions, concurrent winners and atomic audit on the composed path.

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
    `outbox_messages`. Its canonical fence carries the clause; a fence and this
    rule disagreeing is a defect in one of them.
    - `audit_log` is the written exception and takes **both** paths:
      `DEFAULT uuidv7()` stays on the column as a backstop, and every row
      `PostgresAuditStore` writes carries an id minted **app-side**
      ([ADR-0023 Amendment 9](../decisions/0023-strongly-typed-id-source-generator.md)).
      `AuditLogBehavior` mints the `AuditEntryId` at pipeline step 3 to declare
      the intent, and ADR-0033's `Indeterminate` pair — two rows, one id, on two
      connections — needs both inserts to carry the *same* id, which a
      server-side default cannot give them.
    - `inbox_messages` is **not** one of them despite being append-only: its key
      is `(tenant_id, subscription_id, event_id)`, and `event_id` reuses the
      producing envelope's app-minted `EventId`. `event_deliveries` uses the same
      tuple without a surrogate. Generating another event ID would defeat
      deduplication; [Inbox](#inbox) owns both canonical definitions.
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
- Uniqueness for tenant-scoped natural keys (`(tenant_id, slug_key)`): a `UNIQUE`
  constraint, or — on a table with a `deleted_at` column — a partial unique index, per
  § Soft Delete.
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
- **A unique index on a table with a `deleted_at` column carries
  `WHERE deleted_at IS NULL`.** A table-wide one lets a deleted row hold its key forever,
  and where soft delete is the only way to change a row — `audit_config` has no setter, so
  an override changes by deleting it and declaring a fresh one — the second write fails
  `23505` for good. Two shapes are exempt, and the rule
  [`Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows`](21-architecture-tests-catalogue.md#unique_indexes_on_soft_deletable_tables_exclude_deleted_rows)
  names both: an index containing the whole primary key, which is a foreign-key target
  rather than a natural key and can never collide with a successor; and
  `ux_tenants_slug`, held table-wide **by decision** — a tenant slug is a hostname, and
  whether a terminated tenant's slug may ever be reissued is
  [Phase 02c](../roadmap/phase-02c-hub-foundation.md)'s call.
- Scheduled purge job removes rows past retention.

## Migrations

- EF Core migrations per module, generated from code.
- Name format: `<UTC_yyyyMMddHHmmss>_<intent>`. Intent in snake-case: `add_course_publish_at_column`.
- Every PR that changes a `*Configuration` ships the migration.
- Forward-only by default; reversal only for non-destructive changes.
- Destructive migrations (drop column, change type) require:
  1. ADR or PR description explaining the data plan.
  2. Two-step deploy: tolerant code → migration → strict code.
- **Chain application order is load-bearing, and the Tenancy chain applies first.**
  `audit_config` references `tenants`
  ([ADR-0044 § 9](../decisions/0044-audit-write-path.md)), which is the schema's only
  foreign key crossing two migration chains. P02d-1's Education triggers also depend
  on Tenancy's shared `fn_organization_id_immutable`, including its no-`id` correction
  ([ADR-0003 Amendment 6](../decisions/0003-tenant-isolation-defense-in-depth.md#amendment-6--insert-scope-and-parent-mirrors-2026-09-14)).
  P02b-3's Platform-chain ledger trigger reuses the same function, without adding
  a cross-chain foreign key. A run reaching a dependent chain first
  fails on a clean database with a missing relation or function. Alphabetical order
  produces exactly that run — `Modules/Audit` sorts before `Modules/Tenancy` — so
  `make migrate` names the Tenancy chain ahead of the glob that finds the rest. The
  ordering is held by `Migrate_Target_Applies_The_Tenancy_Chain_First`, which replays
  the recipe's project list — expanding each glob against the chains that exist, and
  skipping one already visited, exactly as the recipe does — and fails when Tenancy is
  not reached before Audit. A coverage rule cannot stand in for it:
  `Migrate_Target_Covers_Every_Migration_Chain` stays green when the Tenancy prefix is
  deleted, because the glob still reaches Tenancy, while every fresh deployment breaks
  from that commit onward. The integration fixture preserves the same dependency
  order: Tenancy precedes every dependent chain. Independent chains may differ in
  relative order: the fixture applies Customization before Audit, while the recipe
  discovers Audit first. Neither depends on the other.
  Reversal removes the dependent Platform, Education and Audit schema before reversing
  Tenancy; restoring the old shared function while a ledger or Education trigger
  still uses it is not a valid
  rollback order. Fresh application, reversed application and reapplication belong
  to the migration-chain integration proof.

## Data Migrations

- Pure-SQL data migrations live in migration files when small.
- Larger deploy-time backfills use an idempotent migration-owned runner. The
  ordinary API job runtime never receives migration credentials; privileged
  scheduled maintenance remains Phase 11 under ADR-0028 and ADR-0047.
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

**ADR-0046 schema implemented in P02b-2 Step 1; both review rounds complete.**
The canonical
DDL below includes lease/terminal/identity/descriptor changes. Step 2 implements
fenced transitions and atomic terminal auditing; both review rounds passed.
Step 3 implements hosted execution and metrics; both independent review rounds
are complete.
Step 4 enforcement and local closeout are complete; all four steps completed both
independent review rounds. The [merge closeout](../roadmap/phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13)
owns CI/merge status. Existing
migrations remain immutable; a new Platform-chain migration replaces free-form
legacy `last_error` with `migration:legacy:redacted` before its byte CHECK.
This specific ADR-0046 Platform migration temporarily uses owner-only `NO FORCE`
for that platform-wide diagnostic backfill, then restores `FORCE` before commit.
The migration is atomic: any failure rolls back both the data change and the
security setting. It widens no runtime policy, role or grant and does not change
the per-tenant data-migration rule above. Invalid legacy identities fail the whole
migration for explicit repair; facts/state are not silently rewritten. Existing
attempts above six remain representable and route
to terminalization, never another normal publication.

```sql
CREATE TABLE outbox_messages (
    id              uuid PRIMARY KEY DEFAULT uuidv7(), -- producer supplies EventId
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    tenant_id       uuid NOT NULL,
    organization_id uuid NULL,             -- null = tenant-wide event; see note below
    correlation_id  text NOT NULL,         -- full W3C traceparent, per ADR-0032 § 12
    causation_id    uuid NULL,
    actor_user_id   uuid NULL,
    type            text NOT NULL,         -- assembly-qualified event type name
    topic           text NOT NULL,         -- "learnstack.{module}.{aggregate}"
    partition_key   text NOT NULL,         -- routing source: aggregate id or explicitly tenant-wide tenant_id
    payload         jsonb NOT NULL,
    metadata        jsonb NULL,            -- producer writes SQL NULL (ADR-0057)
    processed_at    timestamptz NULL,
    attempts        int NOT NULL DEFAULT 0,
    last_error      text NULL,
    available_after timestamptz NOT NULL DEFAULT now(),
    locked_by       text NULL,             -- fresh canonical UUID acquisition token
    locked_until    timestamptz NULL,
    terminal_reason text NULL,
    dead_lettered_at timestamptz NULL,
    CONSTRAINT ck_outbox_messages_identity CHECK (
        id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,
                             '00000000-0000-7000-8000-000000000002'::uuid)
        AND (organization_id IS NULL OR
             organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
    CONSTRAINT ck_outbox_messages_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_outbox_messages_last_error_size CHECK (
        last_error IS NULL OR octet_length(last_error) <= 256),
    CONSTRAINT ck_outbox_messages_lease CHECK (
        (locked_by IS NULL AND locked_until IS NULL) OR
        (locked_by IS NOT NULL AND length(locked_by) > 0 AND locked_until IS NOT NULL)),
    CONSTRAINT ck_outbox_messages_terminal_reason CHECK (
        terminal_reason IS NULL OR terminal_reason IN (
            'unknown_type', 'retired_type', 'invalid_envelope', 'retry_exhausted')),
    CONSTRAINT ck_outbox_messages_state CHECK (
        (processed_at IS NULL OR
            (dead_lettered_at IS NULL AND terminal_reason IS NULL AND locked_by IS NULL))
        AND (dead_lettered_at IS NULL OR
            (processed_at IS NULL AND terminal_reason IS NOT NULL AND locked_by IS NULL)))
);

CREATE INDEX ix_outbox_messages_pending
    ON outbox_messages (available_after, occurred_at, id)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND attempts < 6 AND locked_by IS NULL;
CREATE INDEX ix_outbox_messages_tenant_pending
    ON outbox_messages (tenant_id, available_after)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL;
CREATE INDEX ix_outbox_messages_recovery
    ON outbox_messages (available_after, occurred_at, id)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND (locked_by IS NOT NULL OR attempts >= 6);
CREATE INDEX ix_outbox_messages_terminal
    ON outbox_messages (available_after, occurred_at, id)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NOT NULL;

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
GRANT UPDATE (processed_at, attempts, last_error, available_after,
              locked_by, locked_until, terminal_reason, dead_lettered_at)
    ON outbox_messages TO learnstack_outbox_admin;

-- The OutboxProcessor connects as learnstack_outbox_admin, which holds BYPASSRLS so it
-- can read every tenant's pending rows for dispatch. That bypass is the reason the
-- table's own policy still matters: application code writing an outbox row does so as
-- learnstack_app, inside the business transaction, and WITH CHECK pins the row to the
-- caller's tenant. BYPASSRLS bypasses policies, not GRANTs — the column list above is
-- what actually bounds the dispatcher, and SELECT ... FOR UPDATE SKIP LOCKED works with
-- a column-level UPDATE grant, so no table-wide UPDATE is needed. The P02b-2 migration
-- adds the status columns and their grants together. INSERT-only on audit_log belongs
-- in the Audit chain, not this DDL. See ADR-0046 and 15-event-and-outbox.md.
```

[ADR-0057](../decisions/0057-transactional-event-production-and-completion.md)
accepts `id = EventId`, supplied by the producer; the existing DDL default is
not the producer identity source. There is no separate `event_id` column.
Duplicate enqueue fails. `type` retains the assembly-qualified concrete name.
`IOutbox` inserts immediately on the enrolled, announced, writable ambient
`IUnitOfWork` connection/transaction as `learnstack_app`; it opens/commits
nothing and calls no transport. The immutable announcement generation and tenant
must match the producer frame, including unresolved provisioning.

The producer writes concrete-runtime JSON with frozen `PayloadJsonOptions`.
Additional payload fields are identifiers only; the complete canonical UTF-8
payload is capped at 262,144 bytes inclusive before INSERT. `metadata` is SQL
NULL with no caller extension channel. This is application admission, not a new
DDL CHECK or a claim about stored `jsonb` byte length. P02b-1 implements this
producer boundary. ADR-0046 accepts producer recovery and the shared inbox
contract; P02b-2 Step 2 implements producer transitions and terminal audit,
Step 3 implements hosted dispatch with both independent review rounds complete, and
P02b-3 owns inbox/delivery runtime.

`organization_id` is `uuid NULL` and mirrors the organization context of the transaction
that enqueued the row —
[ADR-0032 § Sub-decision 12](../decisions/0032-exception-handling-logging-and-observability.md)
makes it contractual. It is nullable because a tenant-wide event has no organization, not
because it is optional on an org-scoped one. The producer persists organization
context for the transport; the [delivery ledger](#inbox) retains it in each accepted
envelope copy. The consumer runs in a fresh scope and restores `ITenantContext`
from that envelope, so an organization that was never persisted cannot be
restored. The outbox column is deliberately **not** indexed and **not** part of
its RLS predicate — `outbox_messages` is a tenant-wide table whose policy carries no
organization term, and the dispatcher reads the column rather than filtering on it.

`correlation_id` is `text NOT NULL` (not `uuid`): it stores the **full W3C `traceparent`
string** — `00-<32-hex trace-id>-<16-hex span-id>-<2-hex flags>` — so a consumer rehydrates
the trace with `ActivityContext.TryParse(row.CorrelationId, traceState: null, out var ctx)`
rather than re-deriving it
([ADR-0032 § Sub-decision 12](../decisions/0032-exception-handling-logging-and-observability.md),
[10-observability.md § Correlation](10-observability.md)). It is `text` rather than `uuid`
because a traceparent is not a UUID. The request middleware synthesises a root traceparent
when the inbound request carries none. The outbox producer requires it before INSERT;
background jobs preserve it in their authoritative envelope, while vendor wakeups
carry no trace metadata. No producer enqueue can omit the correlation id, which
lets the
registered `Outbox_Row_Carries_Correlation_Context` assertion rest on the schema rather than
on developer discipline. The earlier justification for nullability — that APISIX's
`request-id` plugin echoes any client-supplied id — did not survive contact with
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md), which demand-gates APISIX to
[Phase 11](../roadmap/phase-11-production-hardening.md): the column exists from Phase 02a,
so its contract cannot rest on a component that is not there yet. The architecture deep dive
lives in [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).


Normal publication eligibility requires no processed/dead-lettered/terminal-request
state, fewer than six attempts, due availability, no lease awaiting recovery and
manifest/local-partition admission. Required Pending contracts and aliases are
excluded before acquisition and payload validation. Every transition checks ID,
current token and database-time unexpired lease. Recovery first replaces an
expired token; attempts count committed normal claims only. Terminalization
claims without incrementing attempts or publishing, then writes terminal state
and its MUST audit atomically. See
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#producer-dispatch-and-recovery).

## Inbox

**P02b-3 Steps 1–4 deliver the shared schema, durable acceptance, inbox claims,
business completion and independent subscriber scheduling.**
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#subscriber-acceptance-and-inbox-ownership)
and the [accepted P02b-3 package](../roadmap/phase-02b-delivery-log.md#accepted-storage-and-authority-details)
define transport-owned deduplication and independent subscription delivery.
`event_deliveries` retains one immutable envelope copy per
`(tenant_id, subscription_id, event_id)`; `inbox_messages` retains that tuple's
immutable completion marker without a surrogate ID. Neither references the
producer or a business table. Producer deletion cannot remove pending delivery
payload or deduplication history. P02b-8 inventories these copies for erasure;
Phase 11 owns retention purge and its minimal grants. No P3 role receives purge
privileges on either table, including `learnstack_platform`.

The cross-tenant scheduler indexes intentionally begin with availability time,
then occurrence time and event ID, to support ADR-0046's global selection order.
They are the explicit exception to the ordinary tenant-first index convention;
application isolation remains enforced by the policies and grants below.

The ledger is **tenant-owned, organization-scoped** and follows the canonical
[organization policy and write guards](#tenant-owned-and-organization-scoped-tables),
including organization immutability. The inbox is **tenant-owned, tenant-wide**:
it carries identity only, without payload or organization authority. Both are
raw SQL infrastructure, without EF entities or query filters; application SQL
carries explicit tenant predicates and executes on the announced app-role
transaction. Tenancy's shared `fn_organization_id_immutable` must exist before
this Platform migration, as in the sanctioned migration sequence.

The inbox timestamp records the uncommitted claim, using database time. The
transport inserts it before handler construction; only a successful owner
transaction makes it durable alongside business effects, produced outbox, MUST
audit and fenced ledger completion. Rollback removes a new claim; targeted replay
never resets completed inbox rows. Database triggers refuse inbox UPDATE, DELETE
and TRUNCATE even as the owner; Phase 11 must explicitly decide any retention
exception. Only `learnstack_app` updates the ledger's `processed_at`.
Completion retains the paired lease token/expiry as inert provenance, because
app grants cannot clear them. Every scheduler index and selection excludes
completed rows. Terminal completion clears ownership. Replay count/timestamp
are paired, and subscription IDs follow the manifest's ASCII
`{module}.{subscription}.v{positive-version}` grammar.

Canonical DDL and exact column grants:

```sql
CREATE TABLE event_deliveries (
    tenant_id       uuid NOT NULL,
    subscription_id text NOT NULL,
    event_id        uuid NOT NULL,
    organization_id uuid NULL,
    occurred_at     timestamptz NOT NULL,
    correlation_id  text NOT NULL,
    causation_id    uuid NULL,
    actor_user_id   uuid NULL,
    type            text NOT NULL,
    topic           text NOT NULL,
    partition_key   text NOT NULL,
    payload         jsonb NOT NULL,
    processed_at    timestamptz NULL,
    attempts        int NOT NULL DEFAULT 0,
    last_error      text NULL,
    available_after timestamptz NOT NULL DEFAULT now(),
    locked_by       text NULL,
    locked_until    timestamptz NULL,
    terminal_reason text NULL,
    dead_lettered_at timestamptz NULL,
    replay_count    int NOT NULL DEFAULT 0,
    replayed_at     timestamptz NULL,
    CONSTRAINT pk_event_deliveries PRIMARY KEY (tenant_id, subscription_id, event_id),
    CONSTRAINT ck_event_deliveries_identity CHECK (
        tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,
                         '00000000-0000-7000-8000-000000000002'::uuid)
        AND event_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND (organization_id IS NULL OR
             organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (causation_id IS NULL OR causation_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (actor_user_id IS NULL OR actor_user_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
    CONSTRAINT ck_event_deliveries_subscription CHECK (
        subscription_id COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9-]*\.v[1-9][0-9]*$'),
    CONSTRAINT ck_event_deliveries_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_event_deliveries_replay CHECK (
        replay_count >= 0 AND ((replay_count = 0 AND replayed_at IS NULL)
                              OR (replay_count > 0 AND replayed_at IS NOT NULL))),
    CONSTRAINT ck_event_deliveries_last_error_size CHECK (
        last_error IS NULL OR octet_length(last_error) <= 256),
    CONSTRAINT ck_event_deliveries_lease CHECK (
        (locked_by IS NULL AND locked_until IS NULL) OR
        (locked_by IS NOT NULL AND locked_until IS NOT NULL
         AND locked_by COLLATE "C" ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
         AND locked_by <> '00000000-0000-0000-0000-000000000000')),
    CONSTRAINT ck_event_deliveries_terminal_reason CHECK (
        terminal_reason IS NULL OR terminal_reason IN (
            'unknown_type', 'retired_type', 'invalid_envelope',
            'invalid_subscription', 'retry_exhausted')),
    CONSTRAINT ck_event_deliveries_state CHECK (
        (processed_at IS NULL OR (dead_lettered_at IS NULL AND terminal_reason IS NULL))
        AND (dead_lettered_at IS NULL OR
             (processed_at IS NULL AND terminal_reason IS NOT NULL AND locked_by IS NULL)))
);

CREATE INDEX ix_event_deliveries_tenant_id_organization_id
    ON event_deliveries (tenant_id, organization_id);
CREATE INDEX ix_event_deliveries_pending
    ON event_deliveries (available_after, occurred_at, event_id, tenant_id, subscription_id)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND attempts < 6 AND locked_by IS NULL;
CREATE INDEX ix_event_deliveries_recovery
    ON event_deliveries (available_after, occurred_at, event_id, tenant_id, subscription_id)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND (locked_by IS NOT NULL OR attempts >= 6);
CREATE INDEX ix_event_deliveries_terminal
    ON event_deliveries (available_after, occurred_at, event_id, tenant_id, subscription_id)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NOT NULL;

ALTER TABLE event_deliveries ENABLE ROW LEVEL SECURITY;
ALTER TABLE event_deliveries FORCE ROW LEVEL SECURITY;
CREATE POLICY event_deliveries_isolation ON event_deliveries
    USING (
        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND (organization_id IS NULL
             OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
             OR current_setting('app.scope', true) = 'tenant'))
    WITH CHECK (
        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
        AND ((organization_id IS NULL
              AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
             OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
CREATE POLICY event_deliveries_org_write_guard ON event_deliveries
    AS RESTRICTIVE FOR UPDATE
    USING ((organization_id IS NULL
            AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
           OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
CREATE POLICY event_deliveries_org_delete_guard ON event_deliveries
    AS RESTRICTIVE FOR DELETE
    USING ((organization_id IS NULL
            AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
           OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
CREATE TRIGGER tg_event_deliveries_organization_id_immutable
    BEFORE UPDATE ON event_deliveries
    FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();

CREATE TABLE inbox_messages (
    tenant_id       uuid NOT NULL,
    subscription_id text NOT NULL,
    event_id        uuid NOT NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_inbox_messages PRIMARY KEY (tenant_id, subscription_id, event_id),
    CONSTRAINT ck_inbox_messages_identity CHECK (
        tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid,
                         '00000000-0000-7000-8000-000000000002'::uuid)
        AND event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_inbox_messages_subscription CHECK (
        subscription_id COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9-]*\.v[1-9][0-9]*$')
);
ALTER TABLE inbox_messages ENABLE ROW LEVEL SECURITY;
ALTER TABLE inbox_messages FORCE ROW LEVEL SECURITY;
CREATE POLICY inbox_messages_isolation ON inbox_messages
    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

CREATE FUNCTION fn_inbox_messages_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'inbox_messages is immutable (attempted % as %)', TG_OP, current_user
        USING ERRCODE = 'insufficient_privilege';
END;
$$;
CREATE TRIGGER tg_inbox_messages_immutable
    BEFORE UPDATE OR DELETE ON inbox_messages
    FOR EACH ROW EXECUTE FUNCTION fn_inbox_messages_immutable();
CREATE TRIGGER tg_inbox_messages_no_truncate
    BEFORE TRUNCATE ON inbox_messages
    FOR EACH STATEMENT EXECUTE FUNCTION fn_inbox_messages_immutable();

GRANT SELECT ON event_deliveries, inbox_messages TO learnstack_app;
GRANT INSERT (tenant_id, subscription_id, event_id, organization_id, occurred_at,
              correlation_id, causation_id, actor_user_id, type, topic, partition_key, payload)
    ON event_deliveries TO learnstack_app;
GRANT UPDATE (processed_at) ON event_deliveries TO learnstack_app;
GRANT INSERT (tenant_id, subscription_id, event_id) ON inbox_messages TO learnstack_app;
GRANT SELECT (tenant_id, subscription_id, event_id, organization_id, occurred_at,
              type, topic, partition_key, processed_at, attempts, last_error,
              available_after, locked_by, locked_until, terminal_reason,
              dead_lettered_at, replay_count, replayed_at)
    ON event_deliveries TO learnstack_outbox_admin;
GRANT UPDATE (attempts, last_error, available_after, locked_by, locked_until,
              terminal_reason, dead_lettered_at, replay_count, replayed_at)
    ON event_deliveries TO learnstack_outbox_admin;
```

The app acceptance INSERT excludes all scheduling/completion/terminal columns;
inbox INSERT excludes the database-owned timestamp. The scheduler cannot read
payload, correlation, causation or original actor; it cannot insert/delete a
ledger row, access the inbox, mutate routing/envelope fields or fake business
completion. Column UPDATE permits row locking without table-wide UPDATE.
The three scheduling indexes share total order
`(available_after, occurred_at, event_id, tenant_id, subscription_id)` and their
eligibility predicates. Recovery handles expired ownership or exhausted attempts;
terminal requests remain outside normal acquisition. Runtime adds database-time
lease/due predicates and manifest/local partition admission. The primary key
covers tenant-only reads; the full tenant/organization index covers RLS branches,
including tenant-wide rows.

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

**A hand-written write is outside the audit capture**, which sees only what the
`ChangeTracker` holds ([ADR-0044 § 7](../decisions/0044-audit-write-path.md)). An
`INSERT`, `UPDATE` or `DELETE` on an `NpgsqlCommand` is therefore used only where the
module's audit matrix says how that write is recorded or why it is not —
`customization_generations`, bumped by one `ON CONFLICT … DO UPDATE` and deliberately
unaudited, is the shipped case — and an audited entity is written through its aggregate
and `SaveChanges`. EF Core's set-based writes — `ExecuteUpdate`, `ExecuteDelete`,
`ExecuteSql*` — are not used at all: they read as ordinary EF, which makes them the
likely accident, and they bypass the capture the same way
([`No_Set_Based_Write_Bypasses_The_Audit_Capture`](21-architecture-tests-catalogue.md#no_set_based_write_bypasses_the_audit_capture)).

## Connection Management

**P02d-4 Step 1 delivers the Accepted read mode.**
[ADR-0052](../decisions/0052-anonymous-public-read-boundary.md) adds transaction-local
ReadOnly intent on the existing ambient connection; default writers remain
ReadWrite. Follow [Security's sequencing rule](11-security.md#the-rule): setup is
complete before Begin returns, and tenant announcement precedes data/dispatch.
Mode setup alone is not tenant announcement. Same-mode nesting joins; mixed modes
refuse and poison. Failed/cancelled setup cleans up immediately. Successful mode
reset never clears sticky rollback-only state. Prove app-role EF/SQL refusal,
writable controls, successful reuse and pooled-connection reset; a poisoned unit
requires a fresh scope. No fifth role or independent public-read connection is added.

- Npgsql connection multiplexing where appropriate.
- **PgBouncer in transaction-pooling mode** for production — this is a hard
  prerequisite for RLS: `SET LOCAL app.tenant_id = ...` is transaction-scoped, so
  statement-mode pooling would reset the value between statements and silently break
  isolation. The architecture test `Db_Connection_String_Is_TransactionPooled`,
  registered for [Phase 11](../roadmap/phase-11-production-hardening.md), holds the
  deployment configuration to it once a non-development one exists to inspect;
  deviation requires an ADR.
- `app.tenant_id` (and `app.organization_id` when relevant) set **within the same
  transaction** as the work (`SET LOCAL ...`).
- A `DbCommandInterceptor` — **not** a connection-checkout interceptor — guards
  the context. **Packet 7 step 8 ships it**, as `TenantContextGuardInterceptor`: Packet 6 ships the setter and the policies it
  backs up, and the first tenant-owned read on a request path is Packet 7's, which is
  where the guard belongs. Checkout happens before `TransactionBehavior` opens the transaction that
  carries the `SET LOCAL` values, so a checkout hook would read an unset
  `app.tenant_id` on every request and throw universally; under PgBouncer transaction
  pooling it would sometimes read a *previous* transaction's leftover value, which is
  worse than throwing. The command interceptor instead checks the in-process marker
  **a sanctioned setter** stamps on the transaction it opens, once the `SET LOCAL`
  pair is issued, and throws `TenantContextMissingException` when any command a module
  `DbContext` issues runs on an unmarked transaction — no extra round trip, and keyed on
  the transaction rather than on the table, because deciding per table would mean parsing
  command text and every command from a module context belongs to a request that had a
  tenant to announce. Both arms are asserted by
  [`Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction`](21-architecture-tests-catalogue.md). The setters are a closed
  set, named in [Security Standards § The out-of-band setters](11-security.md), which is
  the placement authority: a guard keyed on `TransactionBehavior` alone would reject the
  writes the idempotency store and the audit store make on their own short transactions.
- The database-side guard is fail-closed independently: with `app.tenant_id` unset or
  reset the policy predicate is `NULL`, so the query returns zero rows rather than
  leaking. The interceptor exists to turn that silent empty result into a loud failure,
  not to be the isolation boundary.

## Background jobs

**Derives from:** [ADR-0047](../decisions/0047-transactional-background-job-runtime.md)
and [ADR-0058](../decisions/0058-bounded-stock-hangfire-maintenance.md).

The Platform chain owns seven raw infrastructure tables. Job, attempt and recipient
rows use the org-scoped class; capacity and reservation rows use the tenant-wide
class. Recurrence and occurrence rows use the infrastructure-control class: FORCE
RLS, no application policy or privilege, and the confined dispatcher's enumerated
control access. None receives Platform or PUBLIC privileges. No runtime DELETE or
TRUNCATE is granted; ordinary owner writes also meet the immutable guards.

Parameters, plans and outcomes preserve canonical JSON text and inclusive UTF-8
byte bounds. Control SQL never inspects their contents. Strict typed identifier
validation remains the submission/read boundary's obligation. The immutable job
plan and each attempt's write-once evidence retain history. A final outcome pointer
selects one normal attempt for all separately counted finalize admissions.

App INSERT accepts only the envelope; defaults and the initial-state guard refuse
forged starts or completions. A routing claim precedes receipt INSERT, then the
matching count/current-attempt UPDATE and optional reservation INSERT commit in
one app transaction. Deferred constraints reread final transaction state and require
summary/ledger parity and reciprocal recipient/child receipts. Capacity locking uses
UPDATE privilege on the immutable capacity-group column solely because PostgreSQL
requires an UPDATE privilege for SELECT FOR UPDATE. Release requires the exact
attempt's announced organization and positive completion/no-start/quiescence.
Grouped transactional rollback uses write-once `transactional_cleanup_at` evidence
and its matching `transactional_rollback` release in one application transaction.
The exact original invocation must join business, source and private cancellation
cleanup before this checkpoint; lease expiry alone never releases capacity.
Deferred validation requires released history before retry or terminal eligibility.
A worker without positive local cleanup parks the job for paced recovery probes.

App checkpoint transitions derive the new wake generation, immediate eligibility
and handoff repair, clearing prior bridge hints. The app has no arbitrary routing
UPDATE. Dispatcher grants expose neither DTOs nor causal facts and cannot produce
successful business completion. Schema guards enforce storage invariants; exact
caller enrollment, live token predicates and same-transaction terminal MUST audit
are runtime obligations of the typed execution and control owners. The minimal
Tenancy lifecycle inventory SELECT grants derive from ADR-0047 and are confined
to the [named projection](#job-lifecycle-projection). Step 8 implements its bounded
reader; complete caller enrollment and runtime qualification remain its closure gates.

Eligibility indexes lead with their actual clock/order for the confined global
scans; tenant and FK indexes remain complete. Clocks are runtime predicates. The
canonical SQL below is copied verbatim into the additive Platform migration and
compared against a separately applied database by schema proofs.

The quarantine-presence index also serves a confined global status scan. One
bounded observation runs at the snapshot cadence on the existing serial control
lane. It reads no payload and publishes only after acknowledged read rollback and
actual resource cleanup. Health reads the cached observation; refresh failure
preserves its last good value while reporting unhealthy. A fresh process must
observe durable quarantine again before it can report healthy.

Candidate selection uses a stable database cutoff, such as `statement_timestamp()`
or a captured database timestamp parameter, so the clock column forms an index
range bound even for idle and partial pages. A volatile `clock_timestamp()` filter
can scan every future row despite selecting the ordered index. Authority and
expiry checks after row locking still use current database time; a selection
cutoff does not establish a live lease.

An expired external normal owner parks with a finite recovery due time and one
new wake generation. A separate seek normalizes a legacy unowned row with no
due time once; an already scheduled row keeps its cycle.
Neither operation spends admission, fabricates effect evidence or releases a
reservation. Bridge scheduling uses the recovery due time, including at normal
attempt six; acquisition requires the current generation and an actually due,
unowned row. Expired routing in either ordinary profile preserves its generation
when cleared for bridge repair. Downgrading the recovery-wake migration refuses
while any tenant retains an incomplete external Reconciling row.

```sql
CREATE TABLE background_jobs (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    organization_id uuid NULL,
    definition_key text NOT NULL,
    profile text NOT NULL,
    queue text NOT NULL,
    capacity_group text NULL,
    parameters text NOT NULL,
    original_due_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    traceparent text NOT NULL,
    causal_actor_user_id uuid NULL,
    causation_event_id uuid NULL,
    parent_job_id uuid NULL,
    recipient_id uuid NULL,
    plan text NULL,
    plan_fingerprint text NULL,
    prepared_at timestamptz NULL,
    operation_id uuid NULL,
    final_outcome_attempt_id uuid NULL,
    finalization_status text NULL,
    finalization_reason text NULL,
    business_finalization_incomplete boolean NOT NULL DEFAULT false,
    phase text NOT NULL DEFAULT 'pending',
    normal_attempts integer NOT NULL DEFAULT 0,
    finalize_attempts integer NOT NULL DEFAULT 0,
    current_attempt_id uuid NULL,
    ownership_token uuid NULL,
    ownership_kind text NULL,
    ownership_until timestamptz NULL,
    attempt_deadline timestamptz NULL,
    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
    wake_generation bigint NOT NULL DEFAULT 1,
    bridge_token uuid NULL,
    bridge_until timestamptz NULL,
    vendor_job_id text NULL,
    handoff_check_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    probe_after timestamptz NULL,
    unsuccessful_probes integer NOT NULL DEFAULT 0,
    last_error text NULL,
    quarantine_reason text NULL,
    terminal_reason text NULL,
    processed_at timestamptz NULL,
    dead_lettered_at timestamptz NULL,
    CONSTRAINT ux_background_jobs_tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT ux_background_jobs_recipient UNIQUE (recipient_id),
    CONSTRAINT ck_background_jobs_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
CONSTRAINT fk_background_jobs_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
CONSTRAINT ck_background_jobs_organization CHECK ((organization_id IS NULL OR organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
CONSTRAINT fk_background_jobs_organization FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_background_jobs_parent FOREIGN KEY (tenant_id, parent_job_id)
        REFERENCES background_jobs (tenant_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_background_jobs_ids CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7
        AND (causal_actor_user_id IS NULL OR causal_actor_user_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (causation_event_id IS NULL OR causation_event_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (ownership_token IS NULL OR ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid) AND (bridge_token IS NULL OR bridge_token <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (operation_id IS NULL OR operation_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND parent_job_id IS DISTINCT FROM id),
    CONSTRAINT ck_background_jobs_names CHECK ((length(definition_key) <= 150 AND definition_key COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*$')
        AND (length(queue) <= 50 AND queue COLLATE "C" ~ '^[a-z][a-z0-9_]*$')
        AND (capacity_group IS NULL OR (length(capacity_group) <= 150 AND capacity_group COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$'))),
    CONSTRAINT ck_background_jobs_profile CHECK (profile IN ('transactional', 'external')
        AND (profile <> 'external' OR capacity_group IS NOT NULL)),
    CONSTRAINT ck_background_jobs_parameters CHECK (
        octet_length(parameters) <= 262144 AND parameters IS JSON OBJECT WITH UNIQUE KEYS),
    CONSTRAINT ck_background_jobs_trace CHECK (
        traceparent COLLATE "C" ~ '^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$'
        AND substring(traceparent FROM 4 FOR 32) <> repeat('0', 32)
        AND substring(traceparent FROM 37 FOR 16) <> repeat('0', 16)),
    CONSTRAINT ck_background_jobs_plan CHECK (
        (plan IS NULL AND plan_fingerprint IS NULL AND prepared_at IS NULL AND operation_id IS NULL)
        OR (profile = 'external' AND plan IS NOT NULL AND plan_fingerprint IS NOT NULL
            AND prepared_at IS NOT NULL AND operation_id IS NOT NULL
            AND octet_length(plan) <= 65536 AND plan IS JSON OBJECT WITH UNIQUE KEYS
            AND plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$')),
    CONSTRAINT ck_background_jobs_finalization CHECK (
        (finalization_status IS NULL AND finalization_reason IS NULL AND final_outcome_attempt_id IS NULL)
        OR (profile = 'external' AND plan IS NOT NULL AND final_outcome_attempt_id IS NOT NULL
            AND finalization_status IS NOT NULL AND finalization_reason IS NOT NULL
            AND ((finalization_status = 'succeeded' AND finalization_reason = 'none')
                 OR (finalization_status = 'failed' AND finalization_reason IN
                     ('external_failed', 'external_cancelled', 'normal_attempts_exhausted', 'permanent_failure'))))),
    CONSTRAINT ck_background_jobs_phase CHECK (phase IN ('pending', 'preparing', 'prepared', 'executing',
        'reconciling', 'finalize_pending', 'finalizing', 'terminal_pending', 'completed', 'quarantined')),
    CONSTRAINT ck_background_jobs_counts CHECK (
        normal_attempts BETWEEN 0 AND 6 AND finalize_attempts BETWEEN 0 AND 6
        AND unsuccessful_probes BETWEEN 0 AND 6 AND wake_generation > 0),
    CONSTRAINT ck_background_jobs_ownership CHECK (
        (ownership_token IS NULL AND ownership_kind IS NULL AND ownership_until IS NULL)
        OR (ownership_token IS NOT NULL AND ownership_kind IS NOT NULL AND ownership_kind IN ('routing', 'execution', 'recovery')
            AND ownership_until IS NOT NULL)),
    CONSTRAINT ck_background_jobs_bridge CHECK (
        (bridge_token IS NULL AND bridge_until IS NULL) OR (bridge_token IS NOT NULL AND bridge_until IS NOT NULL)),
    CONSTRAINT ck_background_jobs_vendor CHECK (vendor_job_id IS NULL OR
        (length(vendor_job_id) <= 20 AND vendor_job_id COLLATE "C" ~ '^[1-9][0-9]*$')),
    CONSTRAINT ck_background_jobs_errors CHECK ((last_error IS NULL OR
        (octet_length(last_error) <= 256 AND last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')))
        AND (quarantine_reason IS NULL OR quarantine_reason IN ('unknown_outcome', 'probe_exhausted', 'invalid_control'))
        AND (terminal_reason IS NULL OR terminal_reason IN ('retired_definition', 'invalid_envelope', 'permanent_failure', 'retry_exhausted', 'external_retry_forbidden', 'finalize_refused', 'finalize_exhausted'))),
    CONSTRAINT ck_background_jobs_state CHECK (
        (phase <> 'completed' OR processed_at IS NOT NULL)
        AND (processed_at IS NULL OR (phase = 'completed' AND dead_lettered_at IS NULL AND terminal_reason IS NULL))
        AND (dead_lettered_at IS NULL OR (phase = 'terminal_pending' AND terminal_reason IS NOT NULL AND processed_at IS NULL))
        AND ((phase = 'terminal_pending') = (terminal_reason IS NOT NULL))
        AND ((phase = 'quarantined') = (quarantine_reason IS NOT NULL))
        AND (phase NOT IN ('finalize_pending', 'finalizing') OR finalization_status IS NOT NULL)
        AND (phase NOT IN ('preparing', 'prepared') OR profile = 'external')
        AND (phase <> 'preparing' OR plan IS NULL)
        AND (phase <> 'prepared' OR plan IS NOT NULL)
        AND (phase <> 'executing' OR profile = 'transactional' OR plan IS NOT NULL)
        AND (phase NOT IN ('preparing', 'prepared', 'executing', 'finalizing')
            OR (ownership_kind = 'execution' AND ownership_token IS NOT NULL AND current_attempt_id IS NOT NULL))
        AND (phase <> 'reconciling' OR current_attempt_id IS NOT NULL)
        AND (NOT business_finalization_incomplete OR (plan IS NOT NULL AND dead_lettered_at IS NOT NULL))
        AND (processed_at IS NULL AND dead_lettered_at IS NULL OR ownership_token IS NULL)),
    CONSTRAINT ck_background_jobs_time CHECK (isfinite(original_due_at) AND isfinite(created_at)
        AND isfinite(available_after) AND isfinite(handoff_check_at)
        AND (ownership_until IS NULL OR isfinite(ownership_until))
        AND (attempt_deadline IS NULL OR isfinite(attempt_deadline))
        AND (bridge_until IS NULL OR isfinite(bridge_until))
        AND (probe_after IS NULL OR isfinite(probe_after))
        AND (prepared_at IS NULL OR isfinite(prepared_at))
        AND (processed_at IS NULL OR isfinite(processed_at))
        AND (dead_lettered_at IS NULL OR isfinite(dead_lettered_at)))
);
CREATE TABLE background_job_attempts (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    organization_id uuid NULL,
    job_id uuid NOT NULL,
    stage text NOT NULL,
    ordinal integer NOT NULL,
    admission_token uuid NOT NULL,
    admitted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    deadline timestamptz NOT NULL,
    final_outcome_attempt_id uuid NULL,
    plan_fingerprint text NULL,
    operation_id uuid NULL,
    execute_intent_at timestamptz NULL,
    outcome text NULL,
    outcome_kind text NULL,
    outcome_at timestamptz NULL,
    quiescence_id uuid NULL,
    quiescence_kind text NULL,
    quiescent_at timestamptz NULL,
    no_start_at timestamptz NULL,
    transactional_cleanup_at timestamptz NULL,
    failure_policy text NULL,
    CONSTRAINT ux_background_job_attempts_tenant_job_id UNIQUE (tenant_id, job_id, id),
    CONSTRAINT ux_background_job_attempts_stage_ordinal UNIQUE (job_id, stage, ordinal),
    CONSTRAINT ck_background_job_attempts_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
CONSTRAINT fk_background_job_attempts_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
CONSTRAINT ck_background_job_attempts_organization CHECK ((organization_id IS NULL OR organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
CONSTRAINT fk_background_job_attempts_organization FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_background_job_attempts_job FOREIGN KEY (tenant_id, job_id)
        REFERENCES background_jobs (tenant_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_background_job_attempts_outcome FOREIGN KEY (tenant_id, job_id, final_outcome_attempt_id)
        REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_background_job_attempts_identity CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7
        AND admission_token <> '00000000-0000-0000-0000-000000000000'::uuid AND stage IN ('normal', 'finalize') AND ordinal BETWEEN 1 AND 6
        AND ((stage = 'normal' AND final_outcome_attempt_id IS NULL)
             OR (stage = 'finalize' AND final_outcome_attempt_id IS NOT NULL))),
    CONSTRAINT ck_background_job_attempts_plan CHECK (
        (plan_fingerprint IS NULL AND operation_id IS NULL)
        OR (plan_fingerprint IS NOT NULL AND operation_id IS NOT NULL AND operation_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$')),
    CONSTRAINT ck_background_job_attempts_execute CHECK (execute_intent_at IS NULL OR
        (stage = 'normal' AND plan_fingerprint IS NOT NULL AND no_start_at IS NULL)),
    CONSTRAINT ck_background_job_attempts_outcome CHECK (
        (outcome IS NULL AND outcome_kind IS NULL AND outcome_at IS NULL)
        OR (stage = 'normal' AND outcome IS NOT NULL AND outcome_kind IS NOT NULL AND outcome_kind IN ('succeeded', 'failed', 'cancelled')
            AND outcome_at IS NOT NULL AND execute_intent_at IS NOT NULL
            AND octet_length(outcome) <= 65536 AND outcome IS JSON OBJECT WITH UNIQUE KEYS
            AND quiescent_at IS NOT NULL)),
    CONSTRAINT ck_background_job_attempts_failure_policy CHECK (((failure_policy IS NULL
        AND (outcome_kind IS NULL OR outcome_kind='succeeded'))
        OR (stage='normal' AND outcome IS NOT NULL AND outcome_at IS NOT NULL AND execute_intent_at IS NOT NULL
            AND quiescent_at IS NOT NULL AND no_start_at IS NULL AND transactional_cleanup_at IS NULL
            AND failure_policy IS NOT NULL AND ((outcome_kind='failed'
                AND failure_policy IN ('permanent','transient_repeatable','transient_not_repeatable'))
                OR (outcome_kind='cancelled' AND failure_policy IN ('transient_repeatable','transient_not_repeatable'))))) IS TRUE),
    CONSTRAINT ck_background_job_attempts_quiescence CHECK (
        (quiescence_id IS NULL AND quiescence_kind IS NULL AND quiescent_at IS NULL)
        OR (stage = 'normal' AND quiescence_id IS NOT NULL AND quiescence_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND quiescence_kind IS NOT NULL AND quiescence_kind IN ('provider_fenced', 'execution_terminated')
            AND quiescent_at IS NOT NULL AND execute_intent_at IS NOT NULL)),
    CONSTRAINT ck_background_job_attempts_no_start CHECK (no_start_at IS NULL OR
        (stage = 'normal' AND execute_intent_at IS NULL AND outcome IS NULL AND quiescent_at IS NULL)),
    CONSTRAINT ck_background_job_attempts_transactional_cleanup CHECK (
        transactional_cleanup_at IS NULL OR (stage='normal' AND isfinite(transactional_cleanup_at)
            AND transactional_cleanup_at>=admitted_at AND plan_fingerprint IS NULL AND operation_id IS NULL
            AND execute_intent_at IS NULL AND outcome IS NULL AND outcome_kind IS NULL AND outcome_at IS NULL
            AND quiescence_id IS NULL AND quiescence_kind IS NULL AND quiescent_at IS NULL AND no_start_at IS NULL)),
    CONSTRAINT ck_background_job_attempts_time CHECK (isfinite(admitted_at) AND isfinite(deadline)
        AND deadline > admitted_at AND deadline <= admitted_at + interval '24 hours'
        AND (execute_intent_at IS NULL OR isfinite(execute_intent_at))
        AND (outcome_at IS NULL OR isfinite(outcome_at))
        AND (quiescent_at IS NULL OR isfinite(quiescent_at))
        AND (no_start_at IS NULL OR isfinite(no_start_at)))
);
ALTER TABLE background_jobs ADD CONSTRAINT fk_background_jobs_current_attempt
    FOREIGN KEY (tenant_id, id, current_attempt_id)
    REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT
    DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE background_jobs ADD CONSTRAINT fk_background_jobs_final_outcome
    FOREIGN KEY (tenant_id, id, final_outcome_attempt_id)
    REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT
    DEFERRABLE INITIALLY DEFERRED;
CREATE TABLE background_job_capacity (
    tenant_id uuid NOT NULL,
    capacity_group text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT pk_background_job_capacity PRIMARY KEY (tenant_id, capacity_group),
    CONSTRAINT ck_background_job_capacity_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
CONSTRAINT fk_background_job_capacity_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
    CONSTRAINT ck_background_job_capacity_group CHECK ((length(capacity_group) <= 150 AND capacity_group COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$') AND isfinite(created_at))
);
CREATE TABLE background_job_reservations (
    tenant_id uuid NOT NULL,
    capacity_group text NOT NULL,
    job_id uuid NOT NULL,
    attempt_id uuid NOT NULL,
    reserved_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    released_at timestamptz NULL,
    release_reason text NULL,
    CONSTRAINT pk_background_job_reservations PRIMARY KEY (tenant_id, job_id, attempt_id),
    CONSTRAINT fk_background_job_reservations_capacity FOREIGN KEY (tenant_id, capacity_group)
        REFERENCES background_job_capacity (tenant_id, capacity_group) ON DELETE RESTRICT,
    CONSTRAINT fk_background_job_reservations_attempt FOREIGN KEY (tenant_id, job_id, attempt_id)
        REFERENCES background_job_attempts (tenant_id, job_id, id) ON DELETE RESTRICT,
    CONSTRAINT ck_background_job_reservations_reserved CHECK (isfinite(reserved_at)),
    CONSTRAINT ck_background_job_reservations_release CHECK (
        (released_at IS NULL AND release_reason IS NULL)
        OR (released_at IS NOT NULL AND release_reason IS NOT NULL AND release_reason IN ('completed', 'never_started', 'quiescent', 'transactional_rollback')
            AND isfinite(released_at) AND released_at >= reserved_at))
);
CREATE TABLE background_job_recurrences (
    recurrence_key text NOT NULL,
    revision bigint NOT NULL,
    manifest_hash text NOT NULL,
    target_definition_key text NOT NULL,
    schedule text NOT NULL,
    activates_at timestamptz NOT NULL,
    anchor_at timestamptz NOT NULL,
    period_microseconds bigint NOT NULL,
    jitter_microseconds bigint NOT NULL,
    recipient_policy text NOT NULL,
    misfire_policy text NOT NULL DEFAULT 'coalesce_latest',
    superseded_at timestamptz NULL,
    next_scheduled_at timestamptz NOT NULL,
    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
    ownership_token uuid NULL,
    ownership_until timestamptz NULL,
    last_error text NULL,
    quarantined_at timestamptz NULL,
    CONSTRAINT pk_background_job_recurrences PRIMARY KEY (recurrence_key, revision),
    CONSTRAINT ck_background_job_recurrences_manifest CHECK (
        (length(recurrence_key) <= 150 AND recurrence_key COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*$') AND (length(target_definition_key) <= 150 AND target_definition_key COLLATE "C" ~ '^[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*$')
        AND revision > 0 AND manifest_hash COLLATE "C" ~ '^[0-9a-f]{64}$'
        AND schedule IN ('interval', 'daily') AND misfire_policy = 'coalesce_latest'
        AND period_microseconds BETWEEN 60000000 AND 31622400000000
        AND (schedule <> 'daily' OR period_microseconds = 86400000000)
        AND jitter_microseconds BETWEEN 0 AND LEAST(period_microseconds, 7200000000)
        AND recipient_policy IN ('provisioned_non_archived_tenants', 'serving_tenants', 'serving_organizations')),
    CONSTRAINT ck_background_job_recurrences_time CHECK (isfinite(activates_at) AND isfinite(anchor_at)
        AND isfinite(next_scheduled_at) AND next_scheduled_at >= activates_at AND isfinite(available_after)
        AND (superseded_at IS NULL OR (isfinite(superseded_at) AND superseded_at >= activates_at))
        AND (quarantined_at IS NULL OR isfinite(quarantined_at))),
    CONSTRAINT ck_background_job_recurrences_lease CHECK (
        (ownership_token IS NULL AND ownership_until IS NULL)
        OR (ownership_token IS NOT NULL AND ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid AND ownership_until IS NOT NULL AND isfinite(ownership_until))),
    CONSTRAINT ck_background_job_recurrences_error CHECK (last_error IS NULL OR last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted'))
);
CREATE TABLE background_job_occurrences (
    id uuid PRIMARY KEY,
    recurrence_key text NOT NULL,
    revision bigint NOT NULL,
    manifest_hash text NOT NULL,
    target_definition_key text NOT NULL,
    recipient_policy text NOT NULL,
    scheduled_at timestamptz NOT NULL,
    jitter_microseconds bigint NOT NULL,
    skipped_slots bigint NOT NULL,
    bounds_frozen_at timestamptz NULL,
    upper_tenant_id uuid NULL,
    upper_organization_id uuid NULL,
    after_tenant_id uuid NULL,
    after_organization_id uuid NULL,
    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
    ownership_token uuid NULL,
    ownership_until timestamptz NULL,
    completed_at timestamptz NULL,
    last_error text NULL,
    quarantined_at timestamptz NULL,
    CONSTRAINT ux_background_job_occurrences_slot UNIQUE (recurrence_key, scheduled_at),
    CONSTRAINT fk_background_job_occurrences_recurrence FOREIGN KEY (recurrence_key, revision)
        REFERENCES background_job_recurrences (recurrence_key, revision) ON DELETE RESTRICT,
    CONSTRAINT ck_background_job_occurrences_identity CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7 AND skipped_slots >= 0),
    CONSTRAINT ck_background_job_occurrences_bounds CHECK (
        (bounds_frozen_at IS NOT NULL OR (upper_tenant_id IS NULL AND upper_organization_id IS NULL
            AND after_tenant_id IS NULL AND after_organization_id IS NULL AND completed_at IS NULL))
        AND (upper_organization_id IS NULL OR upper_tenant_id IS NOT NULL)
        AND (after_organization_id IS NULL OR after_tenant_id IS NOT NULL)
        AND (after_tenant_id IS NULL OR (upper_tenant_id IS NOT NULL
            AND ROW(after_tenant_id, COALESCE(after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                <= ROW(upper_tenant_id, COALESCE(upper_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))))
        AND (recipient_policy = 'serving_organizations' OR (upper_organization_id IS NULL AND after_organization_id IS NULL))
        AND (upper_tenant_id IS NULL OR upper_tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (upper_organization_id IS NULL OR upper_organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)
        AND (after_tenant_id IS NULL OR after_tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid) AND (after_organization_id IS NULL OR after_organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
    CONSTRAINT ck_background_job_occurrences_lease CHECK (
        (ownership_token IS NULL AND ownership_until IS NULL)
        OR (ownership_token IS NOT NULL AND ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid AND ownership_until IS NOT NULL AND isfinite(ownership_until))),
    CONSTRAINT ck_background_job_occurrences_error CHECK (last_error IS NULL OR last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')),
    CONSTRAINT ck_background_job_occurrences_time CHECK (isfinite(scheduled_at) AND isfinite(available_after)
        AND (bounds_frozen_at IS NULL OR isfinite(bounds_frozen_at))
        AND (completed_at IS NULL OR isfinite(completed_at))
        AND (quarantined_at IS NULL OR isfinite(quarantined_at)))
);
CREATE TABLE background_job_recipients (
    id uuid PRIMARY KEY,
    occurrence_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    organization_id uuid NULL,
    recurrence_key text NOT NULL,
    revision bigint NOT NULL,
    manifest_hash text NOT NULL,
    target_definition_key text NOT NULL,
    recipient_policy text NOT NULL,
    scheduled_at timestamptz NOT NULL,
    due_at timestamptz NOT NULL,
    available_after timestamptz NOT NULL DEFAULT clock_timestamp(),
    ownership_token uuid NULL,
    ownership_until timestamptz NULL,
    accepted_job_id uuid NULL,
    accepted_at timestamptz NULL,
    skipped_at timestamptz NULL,
    skipped_reason text NULL,
    last_error text NULL,
    quarantined_at timestamptz NULL,
    CONSTRAINT ux_background_job_recipients_tenant_id_id UNIQUE (tenant_id, id),
    CONSTRAINT ux_background_job_recipients_route UNIQUE NULLS NOT DISTINCT (occurrence_id, tenant_id, organization_id),
    CONSTRAINT ck_background_job_recipients_tenant CHECK (tenant_id NOT IN ('00000000-0000-0000-0000-000000000000'::uuid, '00000000-0000-7000-8000-000000000002'::uuid)),
CONSTRAINT fk_background_job_recipients_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
CONSTRAINT ck_background_job_recipients_organization CHECK ((organization_id IS NULL OR organization_id <> '00000000-0000-0000-0000-000000000000'::uuid)),
CONSTRAINT fk_background_job_recipients_organization FOREIGN KEY (tenant_id, organization_id) REFERENCES organizations (tenant_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_background_job_recipients_occurrence FOREIGN KEY (occurrence_id)
        REFERENCES background_job_occurrences (id) ON DELETE RESTRICT,
    CONSTRAINT fk_background_job_recipients_job FOREIGN KEY (tenant_id, accepted_job_id)
        REFERENCES background_jobs (tenant_id, id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT ck_background_job_recipients_identity CHECK (uuid_extract_version(id) IS NOT DISTINCT FROM 7),
    CONSTRAINT ck_background_job_recipients_lease CHECK (
        (ownership_token IS NULL AND ownership_until IS NULL)
        OR (ownership_token IS NOT NULL AND ownership_token <> '00000000-0000-0000-0000-000000000000'::uuid AND ownership_until IS NOT NULL AND isfinite(ownership_until))),
    CONSTRAINT ck_background_job_recipients_receipt CHECK (
        (accepted_job_id IS NULL AND accepted_at IS NULL)
        OR (accepted_job_id IS NOT NULL AND accepted_at IS NOT NULL AND skipped_at IS NULL)),
    CONSTRAINT ck_background_job_recipients_skip CHECK (
        (skipped_at IS NULL AND skipped_reason IS NULL)
        OR (skipped_at IS NOT NULL AND skipped_reason IS NOT NULL AND skipped_reason IN ('ineligible', 'deleted') AND accepted_job_id IS NULL)),
    CONSTRAINT ck_background_job_recipients_error CHECK (last_error IS NULL OR last_error IN ('deployment_mismatch', 'capacity_unavailable', 'concurrency_conflict', 'rate_limited', 'dependency_unavailable', 'audit_unavailable', 'unexpected_fault', 'permanent_failure', 'lease_lost', 'deadline_exceeded', 'shutdown', 'unknown_outcome', 'invalid_control', 'finalize_refused', 'finalize_exhausted')),
    CONSTRAINT ck_background_job_recipients_time CHECK (isfinite(scheduled_at) AND isfinite(due_at)
        AND due_at >= scheduled_at AND isfinite(available_after)
        AND (accepted_at IS NULL OR isfinite(accepted_at))
        AND (skipped_at IS NULL OR isfinite(skipped_at))
        AND (quarantined_at IS NULL OR isfinite(quarantined_at)))
);
ALTER TABLE background_jobs ADD CONSTRAINT fk_background_jobs_recipient
    FOREIGN KEY (tenant_id, recipient_id) REFERENCES background_job_recipients (tenant_id, id)
    ON DELETE RESTRICT;

CREATE FUNCTION public.fn_background_job_no_delete() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
BEGIN
    RAISE EXCEPTION 'background job history cannot be removed (%)', TG_TABLE_NAME USING ERRCODE = '23514';
END;
$$;
CREATE FUNCTION public.fn_background_jobs_envelope() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE parent_org uuid; recipient record;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.definition_key, NEW.profile,
               NEW.queue, NEW.capacity_group, NEW.parameters, NEW.original_due_at, NEW.created_at,
               NEW.traceparent, NEW.causal_actor_user_id, NEW.causation_event_id, NEW.parent_job_id, NEW.recipient_id)
            IS DISTINCT FROM
           ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.definition_key, OLD.profile,
               OLD.queue, OLD.capacity_group, OLD.parameters, OLD.original_due_at, OLD.created_at,
               OLD.traceparent, OLD.causal_actor_user_id, OLD.causation_event_id, OLD.parent_job_id, OLD.recipient_id) THEN
            RAISE EXCEPTION 'job envelope is immutable' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF NEW.phase <> 'pending' OR NEW.normal_attempts <> 0 OR NEW.finalize_attempts <> 0
       OR NEW.wake_generation <> 1 OR NEW.current_attempt_id IS NOT NULL OR NEW.ownership_token IS NOT NULL
       OR NEW.bridge_token IS NOT NULL OR NEW.vendor_job_id IS NOT NULL OR NEW.plan IS NOT NULL
       OR NEW.finalization_status IS NOT NULL OR NEW.processed_at IS NOT NULL OR NEW.dead_lettered_at IS NOT NULL
       OR NEW.terminal_reason IS NOT NULL OR NEW.quarantine_reason IS NOT NULL OR NEW.attempt_deadline IS NOT NULL
       OR NEW.probe_after IS NOT NULL OR NEW.unsuccessful_probes <> 0 OR NEW.last_error IS NOT NULL
       OR NEW.business_finalization_incomplete THEN
        RAISE EXCEPTION 'job insert accepts only a new envelope' USING ERRCODE = '23514';
    END IF;
    NEW.created_at := clock_timestamp();
    NEW.available_after := NEW.original_due_at;
    NEW.handoff_check_at := NEW.created_at;
    IF NEW.parent_job_id IS NOT NULL THEN
        SELECT parent.organization_id INTO parent_org FROM public.background_jobs AS parent
        WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.parent_job_id FOR KEY SHARE;
        IF NOT FOUND OR parent_org IS DISTINCT FROM NEW.organization_id THEN
            RAISE EXCEPTION 'job parent scope is unavailable or mismatched' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.recipient_id IS NOT NULL THEN
        SELECT r.organization_id, r.target_definition_key, r.due_at, r.ownership_until,
               r.accepted_job_id, r.skipped_at, r.quarantined_at
          INTO recipient FROM public.background_job_recipients r
          WHERE r.tenant_id = NEW.tenant_id AND r.id = NEW.recipient_id FOR KEY SHARE;
        IF NOT FOUND OR recipient.organization_id IS DISTINCT FROM NEW.organization_id
           OR recipient.target_definition_key <> NEW.definition_key OR recipient.due_at <> NEW.original_due_at
           OR recipient.ownership_until IS NULL OR recipient.ownership_until <= clock_timestamp()
           OR recipient.accepted_job_id IS NOT NULL OR recipient.skipped_at IS NOT NULL
           OR recipient.quarantined_at IS NOT NULL THEN
            RAISE EXCEPTION 'job recipient is unavailable or mismatched' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_jobs_transition() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE attempt record; final_attempt record; admission boolean;
    expected_status text; expected_reason text; expected_terminal text; recovery_cycle boolean := false;
BEGIN
    SELECT NULL::text AS stage, NULL::timestamptz AS execute_intent_at, NULL::timestamptz AS quiescent_at,
           NULL::timestamptz AS no_start_at, NULL::text AS outcome_kind, NULL::text AS failure_policy, NULL::text AS plan_fingerprint,
           NULL::uuid AS operation_id, NULL::integer AS ordinal, NULL::timestamptz AS deadline,
           NULL::uuid AS organization_id, NULL::uuid AS admission_token INTO attempt;
    IF OLD.processed_at IS NOT NULL OR OLD.dead_lettered_at IS NOT NULL OR OLD.phase = 'quarantined' THEN
        IF NEW IS DISTINCT FROM OLD THEN
            RAISE EXCEPTION 'completed or quarantined job is frozen' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF OLD.terminal_reason IS NOT NULL AND NEW.terminal_reason IS DISTINCT FROM OLD.terminal_reason THEN
        RAISE EXCEPTION 'a terminal request retains its original reason' USING ERRCODE='23514';
    END IF;
    IF (OLD.plan IS NOT NULL AND ROW(NEW.plan, NEW.plan_fingerprint, NEW.prepared_at, NEW.operation_id)
            IS DISTINCT FROM ROW(OLD.plan, OLD.plan_fingerprint, OLD.prepared_at, OLD.operation_id))
       OR (OLD.finalization_status IS NOT NULL AND
           ROW(NEW.final_outcome_attempt_id, NEW.finalization_status, NEW.finalization_reason)
            IS DISTINCT FROM ROW(OLD.final_outcome_attempt_id, OLD.finalization_status, OLD.finalization_reason)) THEN
        RAISE EXCEPTION 'job checkpoints are write-once' USING ERRCODE = '23514';
    END IF;
    admission := NEW.normal_attempts <> OLD.normal_attempts OR NEW.finalize_attempts <> OLD.finalize_attempts;
    IF NOT admission AND (NEW.unsuccessful_probes < OLD.unsuccessful_probes
        OR NEW.unsuccessful_probes > OLD.unsuccessful_probes + 1) THEN
        RAISE EXCEPTION 'recovery probes advance one bounded receipt at a time' USING ERRCODE = '23514';
    END IF;
    IF admission THEN
        IF current_user = 'learnstack_outbox_admin' OR OLD.ownership_kind IS DISTINCT FROM 'routing'
           OR OLD.ownership_until <= clock_timestamp() OR OLD.available_after > clock_timestamp()
           OR NEW.current_attempt_id IS NULL OR NEW.current_attempt_id IS NOT DISTINCT FROM OLD.current_attempt_id
           OR NOT ((OLD.phase = 'pending' AND NEW.normal_attempts = OLD.normal_attempts + 1
                    AND NEW.finalize_attempts = OLD.finalize_attempts
                    AND NEW.phase = (CASE WHEN NEW.profile = 'transactional' THEN 'executing'
                        WHEN NEW.plan IS NULL THEN 'preparing' ELSE 'prepared' END))
                OR (OLD.phase = 'finalize_pending' AND NEW.finalize_attempts = OLD.finalize_attempts + 1
                    AND NEW.normal_attempts = OLD.normal_attempts AND NEW.phase = 'finalizing')) THEN
            RAISE EXCEPTION 'invalid counted job admission' USING ERRCODE = '23514';
        END IF;
        SELECT a.stage, a.ordinal, a.admission_token, a.deadline INTO attempt
          FROM public.background_job_attempts a
          WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
        IF NOT FOUND OR attempt.admission_token <> OLD.ownership_token
           OR attempt.stage <> (CASE WHEN NEW.phase = 'finalizing' THEN 'finalize' ELSE 'normal' END)
           OR attempt.ordinal <> (CASE WHEN NEW.phase = 'finalizing' THEN NEW.finalize_attempts ELSE NEW.normal_attempts END)
           OR NEW.attempt_deadline IS DISTINCT FROM attempt.deadline THEN
            RAISE EXCEPTION 'admission requires the matching attempt receipt' USING ERRCODE = '23514';
        END IF;
        NEW.ownership_kind := 'execution';
        NEW.unsuccessful_probes := 0;
        NEW.ownership_until := LEAST(OLD.ownership_until, attempt.deadline);
    ELSIF NEW.current_attempt_id IS DISTINCT FROM OLD.current_attempt_id
          OR NEW.attempt_deadline IS DISTINCT FROM OLD.attempt_deadline THEN
        RAISE EXCEPTION 'attempt identity changes only with admission' USING ERRCODE = '23514';
    END IF;
    IF NEW.current_attempt_id IS NOT NULL THEN
        SELECT a.stage, a.execute_intent_at, a.quiescent_at, a.no_start_at, a.outcome_kind, a.failure_policy,
               a.plan_fingerprint, a.operation_id, a.ordinal, a.deadline, a.organization_id, a.admission_token INTO attempt
          FROM public.background_job_attempts a
          WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.current_attempt_id;
        IF NOT FOUND THEN RAISE EXCEPTION 'current attempt unavailable' USING ERRCODE = '23514'; END IF;
    END IF;
    -- A parked external wake is scheduling only. Clearing an owner must
    -- independently reject a still-live lease; the non-NULL takeover guard
    -- below cannot protect a transition whose new token is NULL.
    IF NOT admission AND NEW.profile='external' AND NEW.phase='reconciling' AND NEW.ownership_token IS NULL THEN
        IF NEW.finalization_status IS NOT NULL OR NEW.final_outcome_attempt_id IS NOT NULL OR NEW.finalize_attempts<>0
           OR attempt.stage IS DISTINCT FROM 'normal' OR attempt.ordinal IS DISTINCT FROM NEW.normal_attempts
           OR attempt.deadline IS DISTINCT FROM NEW.attempt_deadline
           OR attempt.organization_id IS DISTINCT FROM NEW.organization_id
           OR NEW.normal_attempts NOT BETWEEN 1 AND 6 OR NEW.capacity_group IS NULL THEN
            RAISE EXCEPTION 'external recovery wake requires the current normal attempt' USING ERRCODE='23514';
        END IF;
        IF OLD.ownership_token IS NOT NULL AND
           (current_user<>'learnstack_outbox_admin' OR OLD.ownership_until>clock_timestamp()
            OR NEW.probe_after IS NULL OR isfinite(NEW.probe_after) IS NOT TRUE
            OR NOT ((OLD.phase IN ('preparing','prepared','executing') AND OLD.ownership_kind='execution'
                     AND OLD.ownership_token=attempt.admission_token)
                 OR (OLD.phase='reconciling' AND OLD.ownership_kind='recovery'
                     AND OLD.ownership_token<>attempt.admission_token))) THEN
            RAISE EXCEPTION 'external recovery wake cannot discard live or unrelated ownership' USING ERRCODE='23514';
        END IF;
        IF OLD.phase='reconciling' AND OLD.ownership_token IS NULL AND OLD.probe_after IS NOT NULL
           AND NEW.probe_after IS DISTINCT FROM OLD.probe_after THEN
            RAISE EXCEPTION 'an unowned recovery wake retains its scheduling cycle' USING ERRCODE='23514';
        END IF;
        recovery_cycle := current_user='learnstack_outbox_admin' AND NEW.probe_after IS NOT NULL
            AND isfinite(NEW.probe_after) AND (OLD.ownership_token IS NOT NULL
                OR (OLD.phase='reconciling' AND OLD.probe_after IS NULL));
    END IF;
    IF current_user = 'learnstack_outbox_admin' AND
       (NEW.processed_at IS DISTINCT FROM OLD.processed_at OR NEW.phase = 'completed'
        OR ROW(NEW.plan, NEW.plan_fingerprint, NEW.prepared_at, NEW.operation_id,
               NEW.final_outcome_attempt_id, NEW.finalization_status, NEW.finalization_reason)
           IS DISTINCT FROM ROW(OLD.plan, OLD.plan_fingerprint, OLD.prepared_at, OLD.operation_id,
               OLD.final_outcome_attempt_id, OLD.finalization_status, OLD.finalization_reason)) THEN
        RAISE EXCEPTION 'control role cannot complete business checkpoints' USING ERRCODE = '23514';
    END IF;
    IF current_user = 'learnstack_app' AND NOT admission THEN
        IF OLD.ownership_kind NOT IN ('execution', 'recovery') OR OLD.ownership_token IS NULL
           OR OLD.ownership_until <= clock_timestamp() THEN
            RAISE EXCEPTION 'checkpoint requires live execution or recovery ownership' USING ERRCODE = '23514';
        END IF;
        IF NEW.phase <> OLD.phase AND NOT
           ((OLD.phase = 'preparing' AND NEW.phase = 'prepared')
            OR (OLD.phase = 'prepared' AND NEW.phase = 'executing')
            OR (OLD.phase IN ('prepared', 'executing', 'reconciling') AND NEW.phase = 'finalize_pending')
            OR (OLD.phase = 'executing' AND OLD.profile = 'transactional' AND NEW.phase = 'completed')
            OR (OLD.phase = 'finalizing' AND NEW.phase IN ('completed', 'terminal_pending'))) THEN
            RAISE EXCEPTION 'invalid app checkpoint transition' USING ERRCODE = '23514';
        END IF;
    ELSIF current_user <> 'learnstack_app' AND NOT admission AND NEW.phase <> OLD.phase THEN
        IF NOT ((NEW.phase = 'reconciling' AND OLD.phase IN ('preparing', 'prepared', 'executing', 'finalizing')
                    AND OLD.ownership_until <= clock_timestamp())
             OR (OLD.phase = 'reconciling' AND NEW.phase IN ('pending', 'finalize_pending'))
             OR (OLD.phase IN ('pending', 'preparing', 'prepared', 'executing', 'reconciling', 'finalize_pending', 'finalizing')
                    AND NEW.phase IN ('terminal_pending', 'quarantined'))) THEN
            RAISE EXCEPTION 'invalid control transition' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NEW.plan IS NOT NULL AND OLD.plan IS NULL AND
       (OLD.phase <> 'preparing' OR NEW.phase <> 'prepared' OR NEW.prepared_at > clock_timestamp()) THEN
        RAISE EXCEPTION 'plan requires the prepare checkpoint' USING ERRCODE = '23514';
    END IF;
    IF NEW.phase = 'executing' AND OLD.phase = 'prepared' AND
       (OLD.ownership_kind <> 'execution' OR attempt.execute_intent_at IS NULL
        OR attempt.plan_fingerprint IS DISTINCT FROM NEW.plan_fingerprint
        OR attempt.operation_id IS DISTINCT FROM NEW.operation_id) THEN
        RAISE EXCEPTION 'external execute requires durable matching intent' USING ERRCODE = '23514';
    END IF;
    IF NEW.phase IN ('pending', 'finalize_pending', 'terminal_pending') AND OLD.phase <> NEW.phase AND NEW.profile = 'external'
       AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
           AND a.job_id = NEW.id AND a.execute_intent_at IS NOT NULL
           AND (a.quiescent_at IS NULL OR a.outcome_kind IS NULL)) THEN
        RAISE EXCEPTION 'unresolved external effects cannot retry or terminalize' USING ERRCODE = '23514';
    END IF;
    IF NEW.finalization_status IS NOT NULL AND OLD.finalization_status IS NULL THEN
        SELECT a.stage, a.ordinal, a.organization_id, a.outcome_kind, a.failure_policy,
               a.quiescent_at, a.no_start_at, a.plan_fingerprint, a.operation_id INTO final_attempt
          FROM public.background_job_attempts a
          WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.id AND a.id = NEW.final_outcome_attempt_id;
        IF NOT FOUND OR NEW.final_outcome_attempt_id IS DISTINCT FROM NEW.current_attempt_id
           OR final_attempt.stage <> 'normal' OR final_attempt.ordinal<>NEW.normal_attempts
           OR final_attempt.organization_id IS DISTINCT FROM NEW.organization_id
           OR final_attempt.plan_fingerprint IS DISTINCT FROM NEW.plan_fingerprint
           OR final_attempt.operation_id IS DISTINCT FROM NEW.operation_id
           OR (final_attempt.quiescent_at IS NULL AND final_attempt.no_start_at IS NULL)
           OR (NEW.finalization_status = 'succeeded' AND final_attempt.outcome_kind IS DISTINCT FROM 'succeeded')
           OR (NEW.finalization_reason = 'external_failed' AND final_attempt.outcome_kind IS DISTINCT FROM 'failed')
           OR (NEW.finalization_reason = 'external_cancelled' AND final_attempt.outcome_kind IS DISTINCT FROM 'cancelled')
           OR NEW.phase <> 'finalize_pending' THEN
            RAISE EXCEPTION 'finalization requires positive selected-attempt evidence' USING ERRCODE = '23514';
        END IF;
        IF final_attempt.outcome_kind='succeeded' THEN
            expected_status:='succeeded'; expected_reason:='none';
        ELSIF final_attempt.outcome_kind='failed' AND final_attempt.failure_policy='permanent' THEN
            expected_status:='failed'; expected_reason:='external_failed';
        ELSIF final_attempt.outcome_kind IN ('failed','cancelled') AND final_attempt.failure_policy='transient_not_repeatable' THEN
            expected_status:='failed';
            expected_reason:=CASE WHEN final_attempt.outcome_kind='failed' THEN 'external_failed' ELSE 'external_cancelled' END;
        ELSIF final_attempt.outcome_kind IN ('failed','cancelled') AND final_attempt.failure_policy='transient_repeatable'
              AND NEW.normal_attempts=6 THEN
            expected_status:='failed'; expected_reason:='normal_attempts_exhausted';
        ELSIF final_attempt.outcome_kind IS NULL AND final_attempt.no_start_at IS NOT NULL
              AND NEW.finalization_status='failed' AND
              (NEW.finalization_reason='permanent_failure' OR (NEW.finalization_reason='normal_attempts_exhausted'
                  AND NEW.normal_attempts=6)) THEN
            expected_status:='failed'; expected_reason:=NEW.finalization_reason;
        ELSE
            RAISE EXCEPTION 'finalization does not match deterministic attempt policy' USING ERRCODE='23514';
        END IF;
        IF ROW(NEW.finalization_status,NEW.finalization_reason) IS DISTINCT FROM ROW(expected_status,expected_reason) THEN
            RAISE EXCEPTION 'finalization status and reason contradict attempt policy' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NEW.profile = 'external'
       AND (NEW.phase = 'pending' OR (admission AND NEW.normal_attempts <> OLD.normal_attempts))
       AND (NEW.finalization_status IS NOT NULL OR EXISTS (
           SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
               AND a.job_id = NEW.id AND a.stage = 'normal' AND a.outcome_kind = 'succeeded')) THEN
        RAISE EXCEPTION 'known final outcome cannot reopen normal execution' USING ERRCODE = '23514';
    END IF;
    IF NEW.profile = 'external'
       AND (NEW.phase = 'pending' OR (admission AND NEW.normal_attempts <> OLD.normal_attempts))
       AND EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id = NEW.tenant_id
           AND a.job_id = NEW.id AND a.stage = 'normal'
           AND (NOT admission OR a.id IS DISTINCT FROM NEW.current_attempt_id)
           AND a.no_start_at IS NULL AND (a.quiescent_at IS NULL OR a.outcome_kind NOT IN ('failed','cancelled')
               OR a.failure_policy IS DISTINCT FROM 'transient_repeatable')) THEN
        RAISE EXCEPTION 'external retry requires positive prior-attempt evidence' USING ERRCODE = '23514';
    END IF;
    IF NEW.phase = 'completed' AND (NEW.processed_at IS NULL
       OR (NEW.profile = 'external' AND (OLD.phase <> 'finalizing' OR NEW.finalization_status <> 'succeeded'))) THEN
        RAISE EXCEPTION 'successful completion requires its business stage' USING ERRCODE = '23514';
    END IF;
    IF NEW.profile='external' AND NEW.phase='terminal_pending' THEN
        IF NEW.finalization_status IS NOT NULL THEN
            SELECT a.stage,a.ordinal,a.organization_id,a.outcome_kind,a.failure_policy,a.quiescent_at,a.no_start_at,
                   a.plan_fingerprint,a.operation_id INTO final_attempt
              FROM public.background_job_attempts a
              WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND a.id=NEW.final_outcome_attempt_id;
            IF NOT FOUND OR final_attempt.stage<>'normal' OR final_attempt.ordinal<>NEW.normal_attempts
               OR final_attempt.organization_id IS DISTINCT FROM NEW.organization_id
               OR final_attempt.plan_fingerprint IS DISTINCT FROM NEW.plan_fingerprint
               OR final_attempt.operation_id IS DISTINCT FROM NEW.operation_id THEN
                RAISE EXCEPTION 'terminal policy requires the exact selected normal attempt' USING ERRCODE='23514';
            END IF;
            expected_terminal:=NULL;
            IF final_attempt.outcome_kind='failed' AND final_attempt.failure_policy='permanent'
               AND NEW.finalization_status='failed' AND NEW.finalization_reason='external_failed' THEN
                expected_terminal:='permanent_failure';
            ELSIF final_attempt.outcome_kind IN ('failed','cancelled')
                  AND final_attempt.failure_policy='transient_not_repeatable' AND NEW.finalization_status='failed'
                  AND NEW.finalization_reason=(CASE WHEN final_attempt.outcome_kind='failed'
                      THEN 'external_failed' ELSE 'external_cancelled' END) THEN
                expected_terminal:='external_retry_forbidden';
            ELSIF final_attempt.outcome_kind IN ('failed','cancelled') AND final_attempt.failure_policy='transient_repeatable'
                  AND NEW.normal_attempts=6 AND NEW.finalization_status='failed'
                  AND NEW.finalization_reason='normal_attempts_exhausted' THEN
                expected_terminal:='retry_exhausted';
            ELSIF final_attempt.outcome_kind IS NULL AND final_attempt.no_start_at IS NOT NULL
                  AND NEW.finalization_status='failed' THEN
                IF NEW.finalization_reason='permanent_failure' THEN expected_terminal:='permanent_failure';
                ELSIF NEW.finalization_reason='normal_attempts_exhausted' AND NEW.normal_attempts=6 THEN
                    expected_terminal:='retry_exhausted';
                END IF;
            ELSIF final_attempt.outcome_kind='succeeded' AND NEW.finalization_status='succeeded'
                  AND NEW.finalization_reason='none' THEN
                expected_terminal:=NULL;
            ELSE
                RAISE EXCEPTION 'terminal selected facts contradict deterministic policy' USING ERRCODE='23514';
            END IF;
            IF NEW.terminal_reason IN ('finalize_refused','finalize_exhausted') THEN
                IF current_user<>'learnstack_outbox_admin' OR NEW.finalize_attempts<1
                   OR (NEW.terminal_reason='finalize_exhausted' AND NEW.finalize_attempts<>6)
                   OR attempt.stage IS DISTINCT FROM 'finalize' THEN
                    RAISE EXCEPTION 'finalizer terminal route requires its separate counted finalization budget' USING ERRCODE='23514';
                END IF;
            ELSIF NEW.terminal_reason IN ('retired_definition','invalid_envelope') THEN
                IF current_user<>'learnstack_outbox_admin' THEN
                    RAISE EXCEPTION 'uninvokable terminal route belongs only to control' USING ERRCODE='23514';
                END IF;
            ELSE
                IF expected_terminal IS NULL OR NEW.terminal_reason IS DISTINCT FROM expected_terminal
                   OR current_user<>'learnstack_app' OR OLD.phase<>'finalizing' OR NEW.dead_lettered_at IS NULL
                   OR NEW.business_finalization_incomplete OR OLD.ownership_kind IS DISTINCT FROM 'execution'
                   OR OLD.ownership_until<=clock_timestamp() OR OLD.attempt_deadline<=clock_timestamp()
                   OR NOT EXISTS (SELECT 1 FROM public.background_job_attempts a
                       WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND a.id=NEW.current_attempt_id
                         AND a.organization_id IS NOT DISTINCT FROM NEW.organization_id AND a.stage='finalize'
                         AND a.ordinal=NEW.finalize_attempts AND a.admission_token=OLD.ownership_token
                         AND a.final_outcome_attempt_id=NEW.final_outcome_attempt_id AND a.deadline=OLD.attempt_deadline) THEN
                    RAISE EXCEPTION 'ordinary terminal policy requires live app failure finalization' USING ERRCODE='23514';
                END IF;
            END IF;
        ELSIF NEW.plan IS NOT NULL THEN
            IF current_user<>'learnstack_outbox_admin' OR NEW.terminal_reason NOT IN ('retired_definition','invalid_envelope') THEN
                RAISE EXCEPTION 'prepared work cannot bypass its domain finalizer' USING ERRCODE='23514';
            END IF;
        ELSIF NEW.terminal_reason='external_retry_forbidden' OR NEW.terminal_reason IN ('finalize_refused','finalize_exhausted') THEN
            RAISE EXCEPTION 'terminal policy is missing its selected outcome' USING ERRCODE='23514';
        END IF;
    END IF;
    IF NEW.dead_lettered_at IS NOT NULL AND OLD.dead_lettered_at IS NULL THEN
        -- The request may precede NoStart. Only its first durable completion
        -- requires complete positive no-effect and released-capacity history.
        IF NEW.profile='external' AND NEW.plan IS NULL THEN
            IF (current_user='learnstack_outbox_admin' AND OLD.phase='terminal_pending'
                AND NEW.phase='terminal_pending' AND OLD.terminal_reason IS NOT NULL
                AND OLD.ownership_kind='recovery' AND OLD.ownership_token IS NOT NULL
                AND OLD.ownership_until>clock_timestamp()
                AND NEW.ownership_token=OLD.ownership_token AND NEW.ownership_kind='recovery'
                AND NEW.processed_at IS NULL AND NEW.plan_fingerprint IS NULL
                AND NEW.prepared_at IS NULL AND NEW.operation_id IS NULL
                AND NEW.finalization_status IS NULL AND NEW.finalization_reason IS NULL
                AND NEW.final_outcome_attempt_id IS NULL AND NEW.finalize_attempts=0
                AND NOT NEW.business_finalization_incomplete) IS NOT TRUE THEN
                RAISE EXCEPTION 'no-plan terminal completion requires its exact live control owner' USING ERRCODE='23514';
            END IF;
            IF NEW.normal_attempts=0 THEN
                IF NEW.terminal_reason NOT IN ('retired_definition','invalid_envelope')
                   OR NEW.current_attempt_id IS NOT NULL OR NEW.attempt_deadline IS NOT NULL
                   OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                       WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)
                   OR EXISTS (SELECT 1 FROM public.background_job_reservations r
                       WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id) THEN
                    RAISE EXCEPTION 'unadmitted terminal completion cannot invent attempt history' USING ERRCODE='23514';
                END IF;
            ELSE
                IF (NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.capacity_group IS NOT NULL
                    AND (NEW.terminal_reason IN ('permanent_failure','retired_definition','invalid_envelope')
                        OR NEW.terminal_reason='retry_exhausted' AND NEW.normal_attempts=6)
                    AND NEW.current_attempt_id IS NOT NULL AND NEW.attempt_deadline IS NOT NULL) IS NOT TRUE
                   OR (SELECT count(*) FROM public.background_job_attempts a
                       WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)<>NEW.normal_attempts
                   OR (SELECT count(*) FROM public.background_job_reservations r
                       WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id)<>NEW.normal_attempts
                   OR EXISTS (SELECT 1 FROM public.background_job_attempts a
                       LEFT JOIN public.background_job_reservations r ON r.tenant_id=a.tenant_id
                           AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=NEW.capacity_group
                       WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND (
                           a.organization_id IS DISTINCT FROM NEW.organization_id OR a.stage<>'normal'
                           OR a.ordinal NOT BETWEEN 1 AND NEW.normal_attempts
                           OR a.final_outcome_attempt_id IS NOT NULL OR a.plan_fingerprint IS NOT NULL
                           OR a.operation_id IS NOT NULL OR a.execute_intent_at IS NOT NULL
                           OR a.outcome_kind IS NOT NULL OR a.outcome_at IS NOT NULL
                           OR a.failure_policy IS NOT NULL OR a.quiescence_id IS NOT NULL
                           OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                           OR a.transactional_cleanup_at IS NOT NULL
                           OR a.no_start_at IS NULL OR isfinite(a.no_start_at) IS NOT TRUE
                           OR a.no_start_at<a.admitted_at OR a.no_start_at>clock_timestamp()
                           OR a.ordinal=NEW.normal_attempts AND (a.id<>NEW.current_attempt_id
                               OR a.deadline IS DISTINCT FROM NEW.attempt_deadline)
                           OR r.attempt_id IS NULL OR r.released_at IS NULL OR isfinite(r.released_at) IS NOT TRUE
                           OR r.released_at<a.no_start_at OR r.released_at>clock_timestamp()
                           OR r.release_reason IS DISTINCT FROM 'never_started')) THEN
                    RAISE EXCEPTION 'no-plan terminal completion requires complete positive no-start history' USING ERRCODE='23514';
                END IF;
            END IF;
        END IF;
        IF current_user = 'learnstack_app' AND
            (OLD.phase <> 'finalizing' OR NEW.finalization_status IS DISTINCT FROM 'failed' OR NEW.plan IS NULL) THEN
            RAISE EXCEPTION 'app terminal completion requires failure finalization' USING ERRCODE = '23514';
        END IF;
        IF current_user = 'learnstack_outbox_admin' AND NEW.plan IS NOT NULL THEN
            IF (OLD.phase='terminal_pending' AND NEW.phase='terminal_pending'
                AND OLD.terminal_reason IS NOT NULL AND NEW.terminal_reason=OLD.terminal_reason
                AND OLD.ownership_kind='recovery' AND OLD.ownership_token IS NOT NULL
                AND OLD.ownership_until>clock_timestamp()
                AND NEW.ownership_token=OLD.ownership_token AND NEW.ownership_kind='recovery'
                AND NOT OLD.business_finalization_incomplete AND NOT NEW.business_finalization_incomplete
                AND isfinite(NEW.dead_lettered_at) AND NEW.dead_lettered_at>=NEW.prepared_at
                AND NEW.dead_lettered_at<=clock_timestamp()
                AND NOT EXISTS (SELECT 1 FROM public.background_job_attempts a WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id
                    AND a.admitted_at>NEW.dead_lettered_at)
                AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id
                    AND r.released_at>NEW.dead_lettered_at)
                AND (
                    NEW.profile='external' AND NEW.phase='terminal_pending' AND NEW.processed_at IS NULL
                    AND NEW.capacity_group IS NOT NULL AND NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.finalize_attempts BETWEEN 0 AND 6
                    AND NEW.plan_fingerprint IS NOT NULL AND NEW.plan_fingerprint COLLATE "C" ~ '^[0-9a-f]{64}$'
                    AND NEW.operation_id IS NOT NULL AND NEW.operation_id<>'00000000-0000-0000-0000-000000000000'::uuid
                    AND NEW.prepared_at IS NOT NULL AND isfinite(NEW.prepared_at) AND NEW.prepared_at<=clock_timestamp()
                    AND NEW.current_attempt_id IS NOT NULL AND NEW.attempt_deadline IS NOT NULL
                    AND (NEW.terminal_reason IN ('retired_definition','invalid_envelope')
                        OR NEW.terminal_reason='finalize_refused' AND NEW.finalize_attempts BETWEEN 1 AND 6
                        OR NEW.terminal_reason='finalize_exhausted' AND NEW.finalize_attempts=6)
                    AND (SELECT count(*) FROM public.background_job_attempts a WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id)=NEW.normal_attempts+NEW.finalize_attempts
                    AND (SELECT count(*) FROM public.background_job_attempts a WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND a.stage='normal')=NEW.normal_attempts
                    AND (SELECT count(*) FROM public.background_job_reservations r WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id)=NEW.normal_attempts
                    AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                        WHERE r.tenant_id=NEW.tenant_id AND r.job_id=NEW.id AND r.released_at IS NULL)
                    AND NOT EXISTS (SELECT 1 FROM public.background_job_attempts a LEFT JOIN public.background_job_reservations r
                        ON r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id AND r.capacity_group=NEW.capacity_group
                        WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.id AND (
                            a.organization_id IS DISTINCT FROM NEW.organization_id
                            OR a.admission_token='00000000-0000-0000-0000-000000000000'::uuid
                            OR isfinite(a.admitted_at) IS NOT TRUE OR a.admitted_at>clock_timestamp()
                            OR isfinite(a.deadline) IS NOT TRUE OR a.deadline<=a.admitted_at OR a.deadline>a.admitted_at+interval '24 hours'
                            OR a.transactional_cleanup_at IS NOT NULL
                            OR (a.stage='normal' AND (
                                a.ordinal NOT BETWEEN 1 AND NEW.normal_attempts OR a.final_outcome_attempt_id IS NOT NULL
                                OR ((a.plan_fingerprint IS NULL AND a.operation_id IS NULL)
                                    OR (a.plan_fingerprint=NEW.plan_fingerprint AND a.operation_id=NEW.operation_id)) IS NOT TRUE
                                OR (a.ordinal<NEW.normal_attempts AND a.no_start_at IS NULL
                                    AND (a.outcome_kind IN ('failed','cancelled') AND a.failure_policy='transient_repeatable') IS NOT TRUE)
                                OR ((a.no_start_at IS NOT NULL AND isfinite(a.no_start_at) AND a.no_start_at>=a.admitted_at
                                        AND a.no_start_at<=clock_timestamp() AND a.execute_intent_at IS NULL
                                        AND a.outcome_kind IS NULL AND a.outcome_at IS NULL AND a.failure_policy IS NULL
                                        AND a.quiescence_id IS NULL AND a.quiescence_kind IS NULL AND a.quiescent_at IS NULL
                                        AND r.attempt_id IS NOT NULL AND r.released_at IS NOT NULL AND isfinite(r.released_at)
                                        AND r.released_at>=a.no_start_at AND r.released_at<=clock_timestamp() AND r.release_reason='never_started')
                                    OR (a.no_start_at IS NULL AND a.plan_fingerprint=NEW.plan_fingerprint AND a.operation_id=NEW.operation_id
                                        AND a.execute_intent_at IS NOT NULL AND isfinite(a.execute_intent_at)
                                        AND a.execute_intent_at>=a.admitted_at AND a.execute_intent_at<=a.deadline
                                        AND a.outcome_at IS NOT NULL AND isfinite(a.outcome_at)
                                        AND a.outcome_at>=a.execute_intent_at AND a.outcome_at<=clock_timestamp()
                                        AND ((a.outcome_kind='succeeded' AND a.failure_policy IS NULL)
                                            OR (a.outcome_kind='failed' AND a.failure_policy='permanent')
                                            OR (a.outcome_kind IN ('failed','cancelled') AND a.failure_policy IN ('transient_repeatable','transient_not_repeatable')))
                                        AND a.quiescence_id IS NOT NULL AND a.quiescence_id<>'00000000-0000-0000-0000-000000000000'::uuid
                                        AND a.quiescence_kind IN ('provider_fenced','execution_terminated')
                                        AND a.quiescent_at IS NOT NULL AND isfinite(a.quiescent_at)
                                        AND a.quiescent_at>=a.execute_intent_at AND a.quiescent_at<=clock_timestamp()
                                        AND r.attempt_id IS NOT NULL AND r.released_at IS NOT NULL AND isfinite(r.released_at)
                                        AND r.released_at>=a.quiescent_at AND r.released_at>=a.outcome_at
                                        AND r.released_at<=clock_timestamp() AND r.release_reason='quiescent')) IS NOT TRUE))
                            OR (a.stage='finalize' AND (
                                a.ordinal NOT BETWEEN 1 AND NEW.finalize_attempts OR NEW.final_outcome_attempt_id IS NULL
                                OR a.final_outcome_attempt_id IS DISTINCT FROM NEW.final_outcome_attempt_id
                                OR a.plan_fingerprint IS NOT NULL OR a.operation_id IS NOT NULL OR a.execute_intent_at IS NOT NULL
                                OR a.outcome_kind IS NOT NULL OR a.outcome_at IS NOT NULL OR a.failure_policy IS NOT NULL
                                OR a.quiescence_id IS NOT NULL OR a.quiescence_kind IS NOT NULL OR a.quiescent_at IS NOT NULL
                                OR a.no_start_at IS NOT NULL OR r.attempt_id IS NOT NULL))
                            OR a.stage NOT IN ('normal','finalize')))
                    AND EXISTS (SELECT 1 FROM public.background_job_attempts current_attempt
                        WHERE current_attempt.tenant_id=NEW.tenant_id AND current_attempt.job_id=NEW.id AND current_attempt.id=NEW.current_attempt_id
                            AND current_attempt.organization_id IS NOT DISTINCT FROM NEW.organization_id
                            AND current_attempt.deadline=NEW.attempt_deadline
                            AND ((NEW.finalize_attempts=0 AND current_attempt.stage='normal' AND current_attempt.ordinal=NEW.normal_attempts)
                                OR (NEW.finalize_attempts>0 AND current_attempt.stage='finalize' AND current_attempt.ordinal=NEW.finalize_attempts
                                    AND current_attempt.final_outcome_attempt_id=NEW.final_outcome_attempt_id)))
                    AND ((NEW.final_outcome_attempt_id IS NULL AND NEW.finalization_status IS NULL AND NEW.finalization_reason IS NULL
                            AND NEW.finalize_attempts=0 AND NEW.terminal_reason IN ('retired_definition','invalid_envelope'))
                        OR (NEW.final_outcome_attempt_id IS NOT NULL AND EXISTS (SELECT 1 FROM public.background_job_attempts selected
                            WHERE selected.tenant_id=NEW.tenant_id AND selected.job_id=NEW.id AND selected.id=NEW.final_outcome_attempt_id
                                AND selected.organization_id IS NOT DISTINCT FROM NEW.organization_id AND selected.stage='normal'
                                AND selected.ordinal=NEW.normal_attempts AND selected.plan_fingerprint=NEW.plan_fingerprint AND selected.operation_id=NEW.operation_id
                                AND ((selected.outcome_kind='succeeded' AND selected.failure_policy IS NULL
                                        AND NEW.finalization_status='succeeded' AND NEW.finalization_reason='none')
                                    OR (selected.outcome_kind='failed' AND selected.failure_policy='permanent'
                                        AND NEW.finalization_status='failed' AND NEW.finalization_reason='external_failed')
                                    OR (selected.outcome_kind IN ('failed','cancelled') AND selected.failure_policy='transient_not_repeatable'
                                        AND NEW.finalization_status='failed' AND NEW.finalization_reason=CASE selected.outcome_kind
                                            WHEN 'failed' THEN 'external_failed' ELSE 'external_cancelled' END)
                                    OR (selected.outcome_kind IN ('failed','cancelled') AND selected.failure_policy='transient_repeatable'
                                        AND NEW.normal_attempts=6 AND NEW.finalization_status='failed' AND NEW.finalization_reason='normal_attempts_exhausted')
                                    OR (selected.outcome_kind IS NULL AND selected.no_start_at IS NOT NULL AND NEW.finalization_status='failed'
                                        AND (NEW.finalization_reason='permanent_failure' OR NEW.finalization_reason='normal_attempts_exhausted' AND NEW.normal_attempts=6))))))
                )) IS NOT TRUE THEN
                RAISE EXCEPTION 'prepared terminal completion requires exact live control and positive history' USING ERRCODE='23514';
            END IF;
            -- Recheck after the bounded history reads, not only before their work.
            IF OLD.ownership_until<=clock_timestamp() THEN
                RAISE EXCEPTION 'prepared terminal completion lost its live control owner' USING ERRCODE='23514';
            END IF;
            NEW.business_finalization_incomplete := true;
        END IF;
    END IF;
    IF NEW.ownership_until > clock_timestamp() + interval '10 minutes' THEN
        RAISE EXCEPTION 'job ownership exceeds its lease bound' USING ERRCODE = '23514';
    END IF;
    IF NEW.bridge_token IS NOT NULL THEN
        IF (NEW.bridge_token IS DISTINCT FROM OLD.bridge_token AND
                ((NEW.phase NOT IN ('pending', 'finalize_pending') AND NOT (NEW.profile='external'
                    AND NEW.phase='reconciling' AND NEW.probe_after IS NOT NULL AND isfinite(NEW.probe_after)
                    AND NEW.normal_attempts BETWEEN 1 AND 6 AND NEW.finalize_attempts=0
                    AND NEW.finalization_status IS NULL AND NEW.final_outcome_attempt_id IS NULL))
                OR NEW.ownership_token IS NOT NULL))
           OR NEW.bridge_until > clock_timestamp() + interval '10 minutes'
           OR (NEW.bridge_token = OLD.bridge_token AND NEW.bridge_until > OLD.bridge_until
               AND OLD.bridge_until <= clock_timestamp())
           OR (NEW.bridge_token IS DISTINCT FROM OLD.bridge_token AND OLD.bridge_token IS NOT NULL
               AND OLD.bridge_until > clock_timestamp()) THEN
            RAISE EXCEPTION 'bridge requires bounded available ownership' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF NOT admission AND NEW.ownership_kind = 'execution' AND
       (OLD.ownership_kind IS DISTINCT FROM 'execution' OR NEW.ownership_token IS DISTINCT FROM OLD.ownership_token) THEN
        RAISE EXCEPTION 'execution authority is minted only by counted admission' USING ERRCODE = '23514';
    END IF;
    -- The checked app finalization selection below becomes a new
    -- eligibility cycle and clears all recovery ownership before RETURN.
    IF NOT admission AND NEW.ownership_kind = 'recovery' AND
       ((NEW.phase NOT IN ('reconciling', 'terminal_pending')
         AND NOT (current_user='learnstack_app' AND OLD.phase='reconciling'
             AND NEW.phase='finalize_pending' AND NEW.finalization_status IS NOT NULL))
        OR (OLD.ownership_kind IS DISTINCT FROM 'recovery' AND NEW.ownership_token IS NOT DISTINCT FROM OLD.ownership_token)) THEN
        RAISE EXCEPTION 'recovery requires a separate non-executing token' USING ERRCODE = '23514';
    END IF;
    IF NOT admission AND OLD.ownership_kind = 'execution' AND NEW.ownership_token IS NULL
       AND NEW.phase = OLD.phase THEN
        RAISE EXCEPTION 'execution authority cannot be silently discarded' USING ERRCODE = '23514';
    END IF;
    IF NEW.ownership_token IS NOT NULL AND NOT admission THEN
        IF NEW.ownership_kind = 'routing' AND (NEW.phase NOT IN ('pending', 'finalize_pending')
            OR NEW.available_after > clock_timestamp() OR NEW.original_due_at > clock_timestamp()
            OR OLD.ownership_kind IN ('execution', 'recovery')) THEN
            RAISE EXCEPTION 'routing cannot replace execution authority' USING ERRCODE = '23514';
        END IF;
        IF NEW.ownership_token = OLD.ownership_token AND NEW.ownership_until > OLD.ownership_until
           AND OLD.ownership_until <= clock_timestamp() THEN
            RAISE EXCEPTION 'expired ownership cannot be renewed' USING ERRCODE = '23514';
        END IF;
        IF NEW.ownership_kind = 'execution' AND NEW.ownership_until > NEW.attempt_deadline THEN
            RAISE EXCEPTION 'execution lease exceeds immutable deadline' USING ERRCODE = '23514';
        END IF;
        IF NEW.ownership_token IS DISTINCT FROM OLD.ownership_token AND OLD.ownership_token IS NOT NULL
           AND OLD.ownership_until > clock_timestamp() THEN
            RAISE EXCEPTION 'live ownership cannot be replaced' USING ERRCODE = '23514';
        END IF;
    END IF;
    IF (NEW.phase IN ('pending', 'finalize_pending') AND NEW.phase <> OLD.phase) OR recovery_cycle THEN
        NEW.wake_generation := OLD.wake_generation + 1;
        IF current_user = 'learnstack_app' THEN NEW.available_after := clock_timestamp(); END IF;
        NEW.handoff_check_at := clock_timestamp();
        NEW.bridge_token := NULL; NEW.bridge_until := NULL; NEW.vendor_job_id := NULL;
        NEW.ownership_token := NULL; NEW.ownership_kind := NULL; NEW.ownership_until := NULL;
    ELSIF NEW.wake_generation <> OLD.wake_generation THEN
        RAISE EXCEPTION 'wake generation changes only with a new eligibility cycle' USING ERRCODE = '23514';
    END IF;
    IF NEW.phase IN ('completed', 'quarantined') OR NEW.dead_lettered_at IS NOT NULL
       OR (NEW.phase = 'terminal_pending' AND OLD.phase <> 'terminal_pending') THEN
        NEW.bridge_token := NULL; NEW.bridge_until := NULL; NEW.vendor_job_id := NULL;
        NEW.ownership_token := NULL; NEW.ownership_kind := NULL; NEW.ownership_until := NULL;
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_job_attempts_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE job record;
BEGIN
    SELECT j.organization_id, j.phase, j.profile, j.normal_attempts, j.finalize_attempts,
           j.ownership_token, j.ownership_kind, j.ownership_until, j.current_attempt_id,
           j.final_outcome_attempt_id, j.finalization_status, j.plan_fingerprint, j.operation_id, j.capacity_group,
           j.attempt_deadline, j.processed_at, j.dead_lettered_at, j.terminal_reason
      INTO job FROM public.background_jobs j
      WHERE j.tenant_id = NEW.tenant_id AND j.id = NEW.job_id FOR KEY SHARE;
    IF NOT FOUND OR job.organization_id IS DISTINCT FROM NEW.organization_id THEN
        RAISE EXCEPTION 'attempt parent scope is unavailable or mismatched' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF job.ownership_kind IS DISTINCT FROM 'routing' OR job.ownership_until <= clock_timestamp()
           OR NEW.admission_token <> job.ownership_token
           OR (NEW.stage = 'normal' AND (job.phase <> 'pending' OR NEW.ordinal <> job.normal_attempts + 1))
           OR (NEW.stage = 'finalize' AND (job.phase <> 'finalize_pending' OR NEW.ordinal <> job.finalize_attempts + 1
               OR NEW.final_outcome_attempt_id IS DISTINCT FROM job.final_outcome_attempt_id))
           OR NEW.plan_fingerprint IS NOT NULL OR NEW.operation_id IS NOT NULL
           OR NEW.execute_intent_at IS NOT NULL OR NEW.outcome IS NOT NULL
           OR NEW.quiescent_at IS NOT NULL OR NEW.no_start_at IS NOT NULL
           OR NEW.transactional_cleanup_at IS NOT NULL OR NEW.failure_policy IS NOT NULL THEN
            RAISE EXCEPTION 'attempt insert requires a fresh routing receipt' USING ERRCODE = '23514';
        END IF;
        IF job.profile='external' AND NEW.stage='normal' AND
           (job.finalization_status IS NOT NULL OR EXISTS (
               SELECT 1 FROM public.background_job_attempts a
               WHERE a.tenant_id=NEW.tenant_id AND a.job_id=NEW.job_id AND a.stage='normal'
                 AND a.no_start_at IS NULL AND (a.quiescent_at IS NULL
                     OR a.outcome_kind NOT IN ('failed','cancelled')
                     OR a.failure_policy IS DISTINCT FROM 'transient_repeatable'))) THEN
            RAISE EXCEPTION 'external admission requires resolved repeatable history' USING ERRCODE='23514';
        END IF;
        NEW.admitted_at := clock_timestamp();
        IF (job.profile = 'transactional' OR NEW.stage = 'finalize')
           AND NEW.deadline > NEW.admitted_at + interval '5 minutes' THEN
            RAISE EXCEPTION 'short stage deadline exceeds its bound' USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
    IF ROW(NEW.id, NEW.tenant_id, NEW.organization_id, NEW.job_id, NEW.stage, NEW.ordinal,
           NEW.admission_token, NEW.admitted_at, NEW.deadline, NEW.final_outcome_attempt_id)
       IS DISTINCT FROM ROW(OLD.id, OLD.tenant_id, OLD.organization_id, OLD.job_id, OLD.stage, OLD.ordinal,
           OLD.admission_token, OLD.admitted_at, OLD.deadline, OLD.final_outcome_attempt_id)
       OR (OLD.plan_fingerprint IS NOT NULL AND ROW(NEW.plan_fingerprint, NEW.operation_id)
            IS DISTINCT FROM ROW(OLD.plan_fingerprint, OLD.operation_id))
       OR (OLD.execute_intent_at IS NOT NULL AND NEW.execute_intent_at IS DISTINCT FROM OLD.execute_intent_at)
       OR (OLD.outcome IS NOT NULL AND ROW(NEW.outcome, NEW.outcome_kind, NEW.outcome_at, NEW.failure_policy)
            IS DISTINCT FROM ROW(OLD.outcome, OLD.outcome_kind, OLD.outcome_at, OLD.failure_policy))
       OR (OLD.quiescent_at IS NOT NULL AND ROW(NEW.quiescence_id, NEW.quiescence_kind, NEW.quiescent_at)
            IS DISTINCT FROM ROW(OLD.quiescence_id, OLD.quiescence_kind, OLD.quiescent_at))
       OR (OLD.no_start_at IS NOT NULL AND NEW.no_start_at IS DISTINCT FROM OLD.no_start_at)
       OR (OLD.transactional_cleanup_at IS NOT NULL AND NEW.transactional_cleanup_at IS DISTINCT FROM OLD.transactional_cleanup_at) THEN
        RAISE EXCEPTION 'attempt identity and recorded evidence are immutable' USING ERRCODE = '23514';
    END IF;
    IF job.current_attempt_id IS DISTINCT FROM NEW.id OR job.ownership_kind NOT IN ('execution', 'recovery')
       OR job.ownership_token IS NULL OR job.ownership_until <= clock_timestamp() THEN
        RAISE EXCEPTION 'attempt checkpoint requires current live ownership' USING ERRCODE = '23514';
    END IF;
    IF NEW.plan_fingerprint IS NOT NULL AND
        (NEW.plan_fingerprint IS DISTINCT FROM job.plan_fingerprint OR NEW.operation_id IS DISTINCT FROM job.operation_id) THEN
        RAISE EXCEPTION 'attempt plan does not match the immutable job plan' USING ERRCODE = '23514';
    END IF;
    IF NEW.execute_intent_at IS NOT NULL AND OLD.execute_intent_at IS NULL AND
       (job.phase <> 'prepared' OR job.ownership_kind <> 'execution' OR job.ownership_token <> NEW.admission_token
        OR NEW.execute_intent_at > clock_timestamp() OR NEW.execute_intent_at >= NEW.deadline) THEN
        RAISE EXCEPTION 'execute intent requires original prepared execution authority' USING ERRCODE = '23514';
    END IF;
    IF NEW.no_start_at IS NOT NULL AND OLD.no_start_at IS NULL AND
       (job.ownership_kind <> 'recovery' OR job.ownership_token = NEW.admission_token) THEN
        RAISE EXCEPTION 'no-start proof requires revoked original authority' USING ERRCODE = '23514';
    END IF;
    IF NEW.transactional_cleanup_at IS NOT NULL AND OLD.transactional_cleanup_at IS NULL THEN
        IF job.profile <> 'transactional' OR job.capacity_group IS NULL OR NEW.stage <> 'normal'
           OR job.phase <> 'reconciling' OR job.ownership_kind <> 'recovery'
           OR job.ownership_token = NEW.admission_token OR job.normal_attempts <> NEW.ordinal
           OR job.attempt_deadline IS DISTINCT FROM NEW.deadline
           OR job.processed_at IS NOT NULL OR job.dead_lettered_at IS NOT NULL OR job.terminal_reason IS NOT NULL
           OR NOT isfinite(NEW.transactional_cleanup_at) OR NEW.transactional_cleanup_at > clock_timestamp() THEN
            RAISE EXCEPTION 'transactional cleanup requires exact live revoked recovery authority' USING ERRCODE = '23514';
        END IF;
        NEW.transactional_cleanup_at := clock_timestamp();
    END IF;
    IF NEW.outcome IS NOT NULL AND OLD.outcome IS NULL THEN
        IF (job.profile='external' AND NEW.stage='normal' AND job.phase IN ('executing','reconciling')
            AND OLD.execute_intent_at IS NOT NULL AND NEW.plan_fingerprint=job.plan_fingerprint
            AND NEW.operation_id=job.operation_id AND isfinite(NEW.outcome_at) AND isfinite(NEW.quiescent_at)
            AND NEW.outcome_at>=NEW.execute_intent_at AND NEW.quiescent_at>=NEW.execute_intent_at
            AND NEW.outcome_at<=clock_timestamp() AND NEW.quiescent_at<=clock_timestamp()) IS NOT TRUE THEN
            RAISE EXCEPTION 'outcome requires its armed external attempt and bounded evidence times' USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_job_attempt_counts() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE target_tenant uuid; target_job uuid; job record; normal_count bigint; finalize_count bigint;
BEGIN
    target_tenant := NEW.tenant_id;
    IF TG_TABLE_NAME = 'background_jobs' THEN target_job := NEW.id; ELSE target_job := NEW.job_id; END IF;
    SELECT j.normal_attempts, j.finalize_attempts, j.current_attempt_id, j.capacity_group, j.profile, j.phase, j.processed_at INTO job
      FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.id = target_job FOR NO KEY UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'attempt job unavailable' USING ERRCODE = '23514'; END IF;
    SELECT count(*) FILTER (WHERE a.stage = 'normal'), count(*) FILTER (WHERE a.stage = 'finalize')
      INTO normal_count, finalize_count FROM public.background_job_attempts a
      WHERE a.tenant_id = target_tenant AND a.job_id = target_job;
    IF job.normal_attempts <> normal_count OR job.finalize_attempts <> finalize_count
       OR ((normal_count + finalize_count = 0) <> (job.current_attempt_id IS NULL))
       OR EXISTS (SELECT 1 FROM public.background_job_attempts a
           WHERE a.tenant_id = target_tenant AND a.job_id = target_job
             AND a.ordinal > CASE WHEN a.stage = 'normal' THEN normal_count ELSE finalize_count END) THEN
        RAISE EXCEPTION 'job counts and durable attempt receipts must commit together' USING ERRCODE = '23514';
    END IF;
    IF job.capacity_group IS NOT NULL AND EXISTS (
        SELECT 1 FROM public.background_job_attempts a
        WHERE a.tenant_id = target_tenant AND a.job_id = target_job AND a.stage = 'normal'
          AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
              WHERE r.tenant_id = a.tenant_id AND r.job_id = a.job_id AND r.attempt_id = a.id
                AND r.capacity_group = job.capacity_group)) THEN
        RAISE EXCEPTION 'counted capacity admission requires its durable reservation' USING ERRCODE = '23514';
    END IF;
    IF job.capacity_group IS NOT NULL AND job.profile='external' AND EXISTS (
        SELECT 1 FROM public.background_job_attempts a
        WHERE a.tenant_id=target_tenant AND a.job_id=target_job AND a.stage='normal'
          AND (a.outcome_kind IS NOT NULL OR a.no_start_at IS NOT NULL)
          AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
              WHERE r.tenant_id=a.tenant_id AND r.capacity_group=job.capacity_group AND r.job_id=a.job_id
                AND r.attempt_id=a.id AND r.released_at IS NOT NULL
                AND ((a.outcome_kind IS NOT NULL AND a.quiescent_at IS NOT NULL AND r.release_reason='quiescent')
                    OR (a.no_start_at IS NOT NULL AND a.execute_intent_at IS NULL AND r.release_reason='never_started')))) THEN
        RAISE EXCEPTION 'external evidence and exact reservation release must commit together' USING ERRCODE='23514';
    END IF;
    IF job.capacity_group IS NOT NULL AND job.profile = 'transactional' THEN
        IF EXISTS (SELECT 1 FROM public.background_job_attempts a
            WHERE a.tenant_id=target_tenant AND a.job_id=target_job AND a.transactional_cleanup_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                  WHERE r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
                    AND r.capacity_group=job.capacity_group AND r.released_at IS NOT NULL
                    AND r.release_reason='transactional_rollback')) THEN
            RAISE EXCEPTION 'transactional cleanup and reservation release must commit together' USING ERRCODE='23514';
        END IF;
        IF EXISTS (SELECT 1 FROM public.background_job_attempts a
            WHERE a.tenant_id=target_tenant AND a.job_id=target_job AND a.stage='normal'
              AND (a.id IS DISTINCT FROM job.current_attempt_id OR job.phase IN ('pending','terminal_pending','completed'))
              AND NOT EXISTS (SELECT 1 FROM public.background_job_reservations r
                  WHERE r.tenant_id=a.tenant_id AND r.job_id=a.job_id AND r.attempt_id=a.id
                    AND r.capacity_group=job.capacity_group AND r.released_at IS NOT NULL
                    AND ((r.release_reason='transactional_rollback' AND a.transactional_cleanup_at IS NOT NULL
                            AND NOT (job.phase='completed' AND a.id=job.current_attempt_id))
                        OR (r.release_reason='never_started' AND a.no_start_at IS NOT NULL
                            AND NOT (job.phase='completed' AND a.id=job.current_attempt_id))
                        OR (r.release_reason='completed' AND job.processed_at IS NOT NULL AND a.id=job.current_attempt_id)))) THEN
            RAISE EXCEPTION 'grouped transactional eligibility requires positive released history' USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NULL;
END;
$$;
CREATE FUNCTION public.fn_background_job_capacity_immutable() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
BEGIN
    IF NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'capacity mutex facts are immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_job_reservations_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE attempt record;
BEGIN
    SELECT a.organization_id, a.stage, a.no_start_at, a.quiescent_at, a.outcome_kind, a.transactional_cleanup_at, j.profile, j.capacity_group,
           j.current_attempt_id, j.processed_at INTO attempt
      FROM public.background_job_attempts a JOIN public.background_jobs j
        ON j.tenant_id = a.tenant_id AND j.id = a.job_id
      WHERE a.tenant_id = NEW.tenant_id AND a.job_id = NEW.job_id AND a.id = NEW.attempt_id
      FOR KEY SHARE OF a, j;
    IF NOT FOUND OR attempt.organization_id IS DISTINCT FROM
           NULLIF(current_setting('app.organization_id', true), '')::uuid
       OR attempt.capacity_group IS DISTINCT FROM NEW.capacity_group OR attempt.stage <> 'normal' THEN
        RAISE EXCEPTION 'reservation attempt scope is unavailable or mismatched' USING ERRCODE = '23514';
    END IF;
    IF TG_OP = 'INSERT' THEN
        IF NEW.released_at IS NOT NULL OR NEW.release_reason IS NOT NULL
           OR attempt.current_attempt_id IS DISTINCT FROM NEW.attempt_id THEN
            RAISE EXCEPTION 'reservation requires a current unreleased admission' USING ERRCODE = '23514';
        END IF;
        PERFORM 1 FROM public.background_job_capacity c
          WHERE c.tenant_id = NEW.tenant_id AND c.capacity_group = NEW.capacity_group FOR UPDATE;
        IF NOT FOUND THEN RAISE EXCEPTION 'capacity mutex unavailable' USING ERRCODE = '23514'; END IF;
        NEW.reserved_at := clock_timestamp();
    ELSE
        IF ROW(NEW.tenant_id, NEW.capacity_group, NEW.job_id, NEW.attempt_id, NEW.reserved_at)
            IS DISTINCT FROM ROW(OLD.tenant_id, OLD.capacity_group, OLD.job_id, OLD.attempt_id, OLD.reserved_at)
           OR (OLD.released_at IS NOT NULL AND NEW IS DISTINCT FROM OLD) THEN
            RAISE EXCEPTION 'reservation identity and release are immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.released_at IS NOT NULL AND OLD.released_at IS NULL AND NOT
           ((NEW.release_reason = 'completed' AND attempt.processed_at IS NOT NULL)
            OR (NEW.release_reason = 'never_started' AND attempt.no_start_at IS NOT NULL)
            OR (NEW.release_reason = 'quiescent' AND attempt.quiescent_at IS NOT NULL AND attempt.outcome_kind IS NOT NULL)
            OR (NEW.release_reason = 'transactional_rollback' AND attempt.profile = 'transactional'
                AND attempt.transactional_cleanup_at IS NOT NULL)) THEN
            RAISE EXCEPTION 'reservation release requires positive completion or quiescence' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

CREATE FUNCTION public.fn_background_job_control_lease() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
BEGIN
    IF NEW.ownership_token IS NOT NULL THEN
        IF NEW.ownership_until > clock_timestamp() + interval '10 minutes' THEN
            RAISE EXCEPTION 'control lease exceeds its bound' USING ERRCODE = '23514';
        END IF;
        IF TG_OP = 'UPDATE' THEN
            IF NEW.ownership_token = OLD.ownership_token AND NEW.ownership_until > OLD.ownership_until
               AND OLD.ownership_until <= clock_timestamp() THEN
                RAISE EXCEPTION 'expired control lease cannot renew' USING ERRCODE = '23514';
            END IF;
            IF NEW.ownership_token IS DISTINCT FROM OLD.ownership_token AND OLD.ownership_token IS NOT NULL
               AND OLD.ownership_until > clock_timestamp() THEN
                RAISE EXCEPTION 'live control lease cannot be replaced' USING ERRCODE = '23514';
            END IF;
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_job_recurrences_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE previous record;
BEGIN
    IF TG_OP = 'INSERT' THEN
        PERFORM pg_advisory_xact_lock(hashtextextended(NEW.recurrence_key, 0));
        SELECT r.revision, r.activates_at, r.superseded_at INTO previous
          FROM public.background_job_recurrences r WHERE r.recurrence_key = NEW.recurrence_key
          ORDER BY r.revision DESC LIMIT 1 FOR UPDATE;
        IF FOUND THEN
            IF NEW.revision <= previous.revision OR NEW.activates_at < previous.activates_at THEN
                RAISE EXCEPTION 'recurrence revision and activation cannot move backward' USING ERRCODE = '23514';
            END IF;
            UPDATE public.background_job_recurrences SET superseded_at = NEW.activates_at
              WHERE recurrence_key = NEW.recurrence_key AND revision = previous.revision;
        END IF;
        IF NEW.superseded_at IS NOT NULL OR NEW.ownership_token IS NOT NULL
           OR NEW.quarantined_at IS NOT NULL OR NEW.last_error IS NOT NULL THEN
            RAISE EXCEPTION 'recurrence insert accepts only a new manifest' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF ROW(NEW.recurrence_key, NEW.revision, NEW.manifest_hash, NEW.target_definition_key,
               NEW.schedule, NEW.activates_at, NEW.anchor_at, NEW.period_microseconds,
               NEW.jitter_microseconds, NEW.recipient_policy, NEW.misfire_policy)
           IS DISTINCT FROM ROW(OLD.recurrence_key, OLD.revision, OLD.manifest_hash, OLD.target_definition_key,
               OLD.schedule, OLD.activates_at, OLD.anchor_at, OLD.period_microseconds,
               OLD.jitter_microseconds, OLD.recipient_policy, OLD.misfire_policy)
           OR (OLD.superseded_at IS NOT NULL AND NEW.superseded_at IS DISTINCT FROM OLD.superseded_at)
           OR NEW.next_scheduled_at < OLD.next_scheduled_at
           OR (OLD.quarantined_at IS NOT NULL AND NEW IS DISTINCT FROM OLD) THEN
            RAISE EXCEPTION 'recurrence manifest and progress are immutable or monotonic' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_job_occurrences_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE manifest record;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT r.manifest_hash, r.target_definition_key, r.recipient_policy, r.jitter_microseconds,
               r.activates_at, r.superseded_at, r.anchor_at, r.period_microseconds INTO manifest
          FROM public.background_job_recurrences r
          WHERE r.recurrence_key = NEW.recurrence_key AND r.revision = NEW.revision FOR KEY SHARE;
        IF NOT FOUND OR ROW(NEW.manifest_hash, NEW.target_definition_key, NEW.recipient_policy, NEW.jitter_microseconds)
           IS DISTINCT FROM ROW(manifest.manifest_hash, manifest.target_definition_key, manifest.recipient_policy, manifest.jitter_microseconds)
           OR NEW.scheduled_at < manifest.activates_at
           OR (manifest.superseded_at IS NOT NULL AND NEW.scheduled_at >= manifest.superseded_at)
           OR mod(extract(epoch FROM NEW.scheduled_at - manifest.anchor_at) * 1000000, manifest.period_microseconds) <> 0
           OR NEW.scheduled_at > clock_timestamp() OR NEW.bounds_frozen_at IS NOT NULL
           OR NEW.ownership_token IS NOT NULL OR NEW.completed_at IS NOT NULL
           OR NEW.quarantined_at IS NOT NULL OR NEW.last_error IS NOT NULL THEN
            RAISE EXCEPTION 'occurrence does not match its eligible frozen manifest' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF ROW(NEW.id, NEW.recurrence_key, NEW.revision, NEW.manifest_hash, NEW.target_definition_key,
               NEW.recipient_policy, NEW.scheduled_at, NEW.jitter_microseconds, NEW.skipped_slots)
           IS DISTINCT FROM ROW(OLD.id, OLD.recurrence_key, OLD.revision, OLD.manifest_hash, OLD.target_definition_key,
               OLD.recipient_policy, OLD.scheduled_at, OLD.jitter_microseconds, OLD.skipped_slots)
           OR (OLD.bounds_frozen_at IS NOT NULL AND ROW(NEW.bounds_frozen_at, NEW.upper_tenant_id, NEW.upper_organization_id)
               IS DISTINCT FROM ROW(OLD.bounds_frozen_at, OLD.upper_tenant_id, OLD.upper_organization_id))
           OR (OLD.after_tenant_id IS NOT NULL AND (NEW.after_tenant_id IS NULL
               OR ROW(NEW.after_tenant_id, COALESCE(NEW.after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                < ROW(OLD.after_tenant_id, COALESCE(OLD.after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))))
           OR ((OLD.completed_at IS NOT NULL OR OLD.quarantined_at IS NOT NULL) AND NEW IS DISTINCT FROM OLD) THEN
            RAISE EXCEPTION 'occurrence facts and progress are immutable or monotonic' USING ERRCODE = '23514';
        END IF;
        IF ROW(NEW.bounds_frozen_at, NEW.after_tenant_id, NEW.after_organization_id, NEW.completed_at)
             IS DISTINCT FROM ROW(OLD.bounds_frozen_at, OLD.after_tenant_id, OLD.after_organization_id, OLD.completed_at)
           AND (OLD.ownership_token IS NULL OR OLD.ownership_until <= clock_timestamp()) THEN
            RAISE EXCEPTION 'occurrence checkpoint requires live ownership' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_job_recipients_guard() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE occurrence record; child record;
BEGIN
    IF TG_OP = 'INSERT' THEN
        SELECT o.recurrence_key, o.revision, o.manifest_hash, o.target_definition_key, o.recipient_policy,
               o.scheduled_at, o.jitter_microseconds, o.bounds_frozen_at, o.ownership_until,
               o.completed_at, o.quarantined_at, o.upper_tenant_id, o.upper_organization_id,
               o.after_tenant_id, o.after_organization_id INTO occurrence
          FROM public.background_job_occurrences o WHERE o.id = NEW.occurrence_id FOR KEY SHARE;
        IF NOT FOUND OR ROW(NEW.recurrence_key, NEW.revision, NEW.manifest_hash, NEW.target_definition_key,
               NEW.recipient_policy, NEW.scheduled_at)
            IS DISTINCT FROM ROW(occurrence.recurrence_key, occurrence.revision, occurrence.manifest_hash,
               occurrence.target_definition_key, occurrence.recipient_policy, occurrence.scheduled_at)
           OR occurrence.bounds_frozen_at IS NULL OR occurrence.ownership_until IS NULL
           OR occurrence.ownership_until <= clock_timestamp() OR occurrence.completed_at IS NOT NULL
           OR occurrence.quarantined_at IS NOT NULL OR occurrence.upper_tenant_id IS NULL
           OR ROW(NEW.tenant_id, COALESCE(NEW.organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
               > ROW(occurrence.upper_tenant_id, COALESCE(occurrence.upper_organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
           OR (occurrence.after_tenant_id IS NOT NULL AND
               ROW(NEW.tenant_id, COALESCE(NEW.organization_id, '00000000-0000-0000-0000-000000000000'::uuid))
                   <= ROW(occurrence.after_tenant_id, COALESCE(occurrence.after_organization_id, '00000000-0000-0000-0000-000000000000'::uuid)))
           OR NEW.due_at > NEW.scheduled_at + occurrence.jitter_microseconds * interval '1 microsecond'
           OR ((NEW.organization_id IS NOT NULL) <> (NEW.recipient_policy = 'serving_organizations'))
           OR NEW.accepted_job_id IS NOT NULL OR NEW.skipped_at IS NOT NULL
           OR NEW.ownership_token IS NOT NULL OR NEW.quarantined_at IS NOT NULL OR NEW.last_error IS NOT NULL THEN
            RAISE EXCEPTION 'recipient does not match its claimed occurrence route' USING ERRCODE = '23514';
        END IF;
        NEW.available_after := NEW.due_at;
    ELSE
        IF ROW(NEW.id, NEW.occurrence_id, NEW.tenant_id, NEW.organization_id, NEW.recurrence_key,
               NEW.revision, NEW.manifest_hash, NEW.target_definition_key, NEW.recipient_policy, NEW.scheduled_at, NEW.due_at)
           IS DISTINCT FROM ROW(OLD.id, OLD.occurrence_id, OLD.tenant_id, OLD.organization_id, OLD.recurrence_key,
               OLD.revision, OLD.manifest_hash, OLD.target_definition_key, OLD.recipient_policy, OLD.scheduled_at, OLD.due_at)
           OR ((OLD.accepted_job_id IS NOT NULL OR OLD.skipped_at IS NOT NULL OR OLD.quarantined_at IS NOT NULL)
               AND NEW IS DISTINCT FROM OLD) THEN
            RAISE EXCEPTION 'recipient route and final receipt are immutable' USING ERRCODE = '23514';
        END IF;
        IF NEW.skipped_at IS NOT NULL AND OLD.skipped_at IS NULL AND
           (OLD.ownership_token IS NULL OR OLD.ownership_until <= clock_timestamp()) THEN
            RAISE EXCEPTION 'recipient skip requires live claim' USING ERRCODE = '23514';
        END IF;
        IF NEW.accepted_job_id IS NOT NULL AND OLD.accepted_job_id IS NULL THEN
            IF current_user = 'learnstack_outbox_admin' OR OLD.ownership_token IS NULL
               OR OLD.ownership_until <= clock_timestamp() THEN
                RAISE EXCEPTION 'recipient acceptance requires app role and live claim' USING ERRCODE = '23514';
            END IF;
            SELECT j.organization_id, j.recipient_id, j.definition_key, j.original_due_at INTO child
              FROM public.background_jobs j WHERE j.tenant_id = NEW.tenant_id AND j.id = NEW.accepted_job_id FOR KEY SHARE;
            IF NOT FOUND OR ROW(child.organization_id, child.recipient_id, child.definition_key, child.original_due_at)
                IS DISTINCT FROM ROW(NEW.organization_id, NEW.id, NEW.target_definition_key, NEW.due_at) THEN
                RAISE EXCEPTION 'recipient child receipt is mismatched' USING ERRCODE = '23514';
            END IF;
            NEW.accepted_at := clock_timestamp();
            NEW.ownership_token := NULL; NEW.ownership_until := NULL;
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE FUNCTION public.fn_background_job_recipient_receipt() RETURNS trigger
LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
DECLARE target_tenant uuid; target_recipient uuid; recipient record; child record;
BEGIN
    target_tenant := NEW.tenant_id;
    IF TG_TABLE_NAME = 'background_jobs' THEN target_recipient := NEW.recipient_id;
    ELSE target_recipient := NEW.id; END IF;
    IF target_recipient IS NULL THEN RETURN NULL; END IF;
    SELECT r.organization_id, r.accepted_job_id, r.target_definition_key, r.due_at INTO recipient
      FROM public.background_job_recipients r
      WHERE r.tenant_id = target_tenant AND r.id = target_recipient FOR KEY SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'recipient receipt unavailable' USING ERRCODE = '23514'; END IF;
    SELECT j.id, j.organization_id, j.definition_key, j.original_due_at INTO child
      FROM public.background_jobs j WHERE j.tenant_id = target_tenant AND j.recipient_id = target_recipient FOR KEY SHARE;
    IF (FOUND AND (recipient.accepted_job_id IS DISTINCT FROM child.id
                  OR ROW(recipient.organization_id, recipient.target_definition_key, recipient.due_at)
                     IS DISTINCT FROM ROW(child.organization_id, child.definition_key, child.original_due_at)))
       OR (NOT FOUND AND recipient.accepted_job_id IS NOT NULL) THEN
        RAISE EXCEPTION 'child insertion and reciprocal recipient receipt must commit together' USING ERRCODE = '23514';
    END IF;
    RETURN NULL;
END;
$$;

CREATE INDEX ix_background_jobs_tenant_id_organization_id ON background_jobs (tenant_id, organization_id);

CREATE INDEX ix_background_job_attempts_tenant_id_organization_id ON background_job_attempts (tenant_id, organization_id);

CREATE INDEX ix_background_job_recipients_tenant_id_organization_id ON background_job_recipients (tenant_id, organization_id);

CREATE INDEX ix_background_jobs_parent ON background_jobs (tenant_id, parent_job_id);
CREATE INDEX ix_background_jobs_recipient ON background_jobs (tenant_id, recipient_id);
CREATE INDEX ix_background_jobs_current_attempt ON background_jobs (tenant_id, id, current_attempt_id);
CREATE INDEX ix_background_jobs_final_outcome ON background_jobs (tenant_id, id, final_outcome_attempt_id);
CREATE INDEX ix_background_job_attempts_outcome ON background_job_attempts (tenant_id, job_id, final_outcome_attempt_id);
CREATE INDEX ix_background_job_reservations_capacity ON background_job_reservations (tenant_id, capacity_group);
CREATE INDEX ix_background_job_recipients_job ON background_job_recipients (tenant_id, accepted_job_id);
CREATE INDEX ix_background_job_occurrences_recurrence ON background_job_occurrences (recurrence_key, revision);
CREATE INDEX ix_background_jobs_pending ON background_jobs (available_after, created_at, id)
    WHERE phase = 'pending' AND processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND normal_attempts < 6 AND ownership_token IS NULL;
CREATE INDEX ix_background_jobs_retirement ON background_jobs (available_after, created_at, id)
    WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL;
CREATE INDEX ix_background_jobs_finalize ON background_jobs (available_after, created_at, id)
    WHERE phase = 'finalize_pending' AND processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND finalize_attempts < 6 AND ownership_token IS NULL;
CREATE INDEX ix_background_jobs_recovery ON background_jobs (ownership_until, created_at, id)
    WHERE ownership_token IS NOT NULL AND processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND phase <> 'quarantined';
CREATE INDEX ix_background_jobs_exhausted ON background_jobs (available_after, created_at, id)
    WHERE ownership_token IS NULL AND processed_at IS NULL AND dead_lettered_at IS NULL
      AND terminal_reason IS NULL AND ((phase = 'pending' AND normal_attempts = 6)
          OR (phase = 'finalize_pending' AND finalize_attempts = 6));
CREATE INDEX ix_background_jobs_terminal ON background_jobs (available_after, created_at, id)
    WHERE phase = 'terminal_pending' AND terminal_reason IS NOT NULL
      AND processed_at IS NULL AND dead_lettered_at IS NULL;
CREATE INDEX ix_background_jobs_quarantined ON background_jobs (id)
    WHERE phase = 'quarantined';
CREATE INDEX ix_background_jobs_bridge ON background_jobs (handoff_check_at, created_at, id)
    WHERE (phase IN ('pending', 'finalize_pending') OR (profile='external' AND phase='reconciling'
        AND probe_after IS NOT NULL AND normal_attempts BETWEEN 1 AND 6 AND finalize_attempts=0
        AND finalization_status IS NULL AND final_outcome_attempt_id IS NULL))
      AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL AND ownership_token IS NULL;
CREATE INDEX ix_background_jobs_external_unarmed ON background_jobs (created_at, id)
    WHERE profile='external' AND phase='reconciling' AND ownership_token IS NULL AND probe_after IS NULL
      AND current_attempt_id IS NOT NULL AND normal_attempts BETWEEN 1 AND 6 AND finalize_attempts=0
      AND finalization_status IS NULL AND final_outcome_attempt_id IS NULL
      AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL;
CREATE INDEX ix_background_jobs_probes ON background_jobs (probe_after, created_at, id)
    WHERE phase = 'reconciling' AND ownership_token IS NULL AND probe_after IS NOT NULL
      AND processed_at IS NULL AND dead_lettered_at IS NULL AND terminal_reason IS NULL;
CREATE INDEX ix_background_job_reservations_occupied ON background_job_reservations (tenant_id, capacity_group)
    WHERE released_at IS NULL;
CREATE INDEX ix_background_job_recurrences_pending ON background_job_recurrences (available_after, recurrence_key, revision)
    WHERE quarantined_at IS NULL AND (superseded_at IS NULL OR next_scheduled_at < superseded_at);
CREATE INDEX ix_background_job_occurrences_pending ON background_job_occurrences (available_after, scheduled_at, id)
    WHERE completed_at IS NULL AND quarantined_at IS NULL;
CREATE INDEX ix_background_job_recipients_pending ON background_job_recipients (available_after, id)
    WHERE accepted_job_id IS NULL AND skipped_at IS NULL AND quarantined_at IS NULL;
CREATE INDEX ix_background_job_recurrences_quarantined ON background_job_recurrences (recurrence_key, revision)
    WHERE quarantined_at IS NOT NULL;
CREATE INDEX ix_background_job_occurrences_quarantined ON background_job_occurrences (id)
    WHERE quarantined_at IS NOT NULL;
CREATE INDEX ix_background_job_recipients_quarantined ON background_job_recipients (id)
    WHERE quarantined_at IS NOT NULL;

ALTER TABLE background_jobs ENABLE ROW LEVEL SECURITY;
ALTER TABLE background_jobs FORCE ROW LEVEL SECURITY;

CREATE POLICY background_jobs_isolation ON background_jobs
            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND (organization_id IS NULL
                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    OR current_setting('app.scope', true) = 'tenant'))
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
        CREATE POLICY background_jobs_org_write_guard ON background_jobs AS RESTRICTIVE FOR UPDATE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
        CREATE POLICY background_jobs_org_delete_guard ON background_jobs AS RESTRICTIVE FOR DELETE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
        CREATE TRIGGER tg_background_jobs_organization_id_immutable BEFORE UPDATE ON background_jobs
            FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();

ALTER TABLE background_job_attempts ENABLE ROW LEVEL SECURITY;
ALTER TABLE background_job_attempts FORCE ROW LEVEL SECURITY;

CREATE POLICY background_job_attempts_isolation ON background_job_attempts
            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND (organization_id IS NULL
                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    OR current_setting('app.scope', true) = 'tenant'))
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
        CREATE POLICY background_job_attempts_org_write_guard ON background_job_attempts AS RESTRICTIVE FOR UPDATE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
        CREATE POLICY background_job_attempts_org_delete_guard ON background_job_attempts AS RESTRICTIVE FOR DELETE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
        CREATE TRIGGER tg_background_job_attempts_organization_id_immutable BEFORE UPDATE ON background_job_attempts
            FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();

ALTER TABLE background_job_capacity ENABLE ROW LEVEL SECURITY;
ALTER TABLE background_job_capacity FORCE ROW LEVEL SECURITY;

CREATE POLICY background_job_capacity_isolation ON background_job_capacity
            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE background_job_reservations ENABLE ROW LEVEL SECURITY;
ALTER TABLE background_job_reservations FORCE ROW LEVEL SECURITY;

CREATE POLICY background_job_reservations_isolation ON background_job_reservations
            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE background_job_recurrences ENABLE ROW LEVEL SECURITY;
ALTER TABLE background_job_recurrences FORCE ROW LEVEL SECURITY;

ALTER TABLE background_job_occurrences ENABLE ROW LEVEL SECURITY;
ALTER TABLE background_job_occurrences FORCE ROW LEVEL SECURITY;

ALTER TABLE background_job_recipients ENABLE ROW LEVEL SECURITY;
ALTER TABLE background_job_recipients FORCE ROW LEVEL SECURITY;

CREATE POLICY background_job_recipients_isolation ON background_job_recipients
            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND (organization_id IS NULL
                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    OR current_setting('app.scope', true) = 'tenant'))
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                    OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid));
        CREATE POLICY background_job_recipients_org_write_guard ON background_job_recipients AS RESTRICTIVE FOR UPDATE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
        CREATE POLICY background_job_recipients_org_delete_guard ON background_job_recipients AS RESTRICTIVE FOR DELETE
            USING ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid);
        CREATE TRIGGER tg_background_job_recipients_organization_id_immutable BEFORE UPDATE ON background_job_recipients
            FOR EACH ROW EXECUTE FUNCTION fn_organization_id_immutable();

CREATE TRIGGER tg_background_jobs_no_delete BEFORE DELETE ON background_jobs
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
    CREATE TRIGGER tg_background_jobs_no_truncate BEFORE TRUNCATE ON background_jobs
        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

CREATE TRIGGER tg_background_job_attempts_no_delete BEFORE DELETE ON background_job_attempts
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
    CREATE TRIGGER tg_background_job_attempts_no_truncate BEFORE TRUNCATE ON background_job_attempts
        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

CREATE TRIGGER tg_background_job_capacity_no_delete BEFORE DELETE ON background_job_capacity
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
    CREATE TRIGGER tg_background_job_capacity_no_truncate BEFORE TRUNCATE ON background_job_capacity
        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

CREATE TRIGGER tg_background_job_reservations_no_delete BEFORE DELETE ON background_job_reservations
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
    CREATE TRIGGER tg_background_job_reservations_no_truncate BEFORE TRUNCATE ON background_job_reservations
        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

CREATE TRIGGER tg_background_job_recurrences_no_delete BEFORE DELETE ON background_job_recurrences
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
    CREATE TRIGGER tg_background_job_recurrences_no_truncate BEFORE TRUNCATE ON background_job_recurrences
        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

CREATE TRIGGER tg_background_job_occurrences_no_delete BEFORE DELETE ON background_job_occurrences
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
    CREATE TRIGGER tg_background_job_occurrences_no_truncate BEFORE TRUNCATE ON background_job_occurrences
        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

CREATE TRIGGER tg_background_job_recipients_no_delete BEFORE DELETE ON background_job_recipients
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_no_delete();
    CREATE TRIGGER tg_background_job_recipients_no_truncate BEFORE TRUNCATE ON background_job_recipients
        FOR EACH STATEMENT EXECUTE FUNCTION public.fn_background_job_no_delete();

CREATE TRIGGER tg_background_jobs_envelope BEFORE INSERT OR UPDATE ON background_jobs
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_jobs_envelope();
CREATE TRIGGER tg_background_jobs_transition BEFORE UPDATE ON background_jobs
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_jobs_transition();
CREATE TRIGGER tg_background_job_attempts_guard BEFORE INSERT OR UPDATE ON background_job_attempts
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_attempts_guard();
CREATE TRIGGER tg_background_job_capacity_immutable BEFORE UPDATE ON background_job_capacity
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_capacity_immutable();
CREATE TRIGGER tg_background_job_reservations_guard BEFORE INSERT OR UPDATE ON background_job_reservations
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_reservations_guard();
CREATE TRIGGER tg_background_job_recurrences_guard BEFORE INSERT OR UPDATE ON background_job_recurrences
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recurrences_guard();
CREATE TRIGGER tg_background_job_occurrences_guard BEFORE INSERT OR UPDATE ON background_job_occurrences
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_occurrences_guard();
CREATE TRIGGER tg_background_job_recipients_guard BEFORE INSERT OR UPDATE ON background_job_recipients
    FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recipients_guard();
CREATE CONSTRAINT TRIGGER ct_background_jobs_attempt_counts AFTER INSERT OR UPDATE ON background_jobs
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_attempt_counts();
CREATE CONSTRAINT TRIGGER ct_background_job_attempts_counts AFTER INSERT OR UPDATE ON background_job_attempts
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_attempt_counts();
CREATE CONSTRAINT TRIGGER ct_background_jobs_recipient AFTER INSERT OR UPDATE ON background_jobs
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recipient_receipt();
CREATE CONSTRAINT TRIGGER ct_background_job_recipients_receipt AFTER INSERT OR UPDATE ON background_job_recipients
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_recipient_receipt();

CREATE TRIGGER tg_background_job_recurrences_lease BEFORE INSERT OR UPDATE ON background_job_recurrences
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_control_lease();

CREATE TRIGGER tg_background_job_occurrences_lease BEFORE INSERT OR UPDATE ON background_job_occurrences
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_control_lease();

CREATE TRIGGER tg_background_job_recipients_lease BEFORE INSERT OR UPDATE ON background_job_recipients
        FOR EACH ROW EXECUTE FUNCTION public.fn_background_job_control_lease();

GRANT SELECT ON background_jobs, background_job_attempts, background_job_capacity,
    background_job_reservations, background_job_recipients TO learnstack_app;
GRANT INSERT (id, tenant_id, organization_id, definition_key, profile, queue, capacity_group,
    parameters, original_due_at, traceparent, causal_actor_user_id, causation_event_id, parent_job_id, recipient_id)
    ON background_jobs TO learnstack_app;
GRANT UPDATE (phase, normal_attempts, finalize_attempts, current_attempt_id, attempt_deadline,
    plan, plan_fingerprint, prepared_at, operation_id, final_outcome_attempt_id, finalization_status,
    finalization_reason, processed_at, terminal_reason, dead_lettered_at)
    ON background_jobs TO learnstack_app;
GRANT INSERT (id, tenant_id, organization_id, job_id, stage, ordinal, admission_token, deadline, final_outcome_attempt_id)
    ON background_job_attempts TO learnstack_app;
GRANT UPDATE (plan_fingerprint, operation_id, execute_intent_at, outcome, outcome_kind, outcome_at,
    quiescence_id, quiescence_kind, quiescent_at, no_start_at, transactional_cleanup_at, failure_policy)
    ON background_job_attempts TO learnstack_app;
GRANT INSERT (tenant_id, capacity_group) ON background_job_capacity TO learnstack_app;
-- PostgreSQL row locking requires an UPDATE privilege. The column is immutable;
-- this narrow grant permits SELECT FOR UPDATE, never a mutex identity mutation.
GRANT UPDATE (capacity_group) ON background_job_capacity TO learnstack_app;
GRANT INSERT (tenant_id, capacity_group, job_id, attempt_id) ON background_job_reservations TO learnstack_app;
GRANT UPDATE (released_at, release_reason) ON background_job_reservations TO learnstack_app;
GRANT UPDATE (accepted_job_id, accepted_at) ON background_job_recipients TO learnstack_app;
GRANT SELECT (id, tenant_id, organization_id, definition_key, profile, queue, capacity_group,
    original_due_at, created_at, recipient_id, plan_fingerprint, prepared_at, operation_id,
    final_outcome_attempt_id, finalization_status, finalization_reason, business_finalization_incomplete,
    phase, normal_attempts, finalize_attempts, current_attempt_id, ownership_token, ownership_kind,
    ownership_until, attempt_deadline, available_after, wake_generation, bridge_token, bridge_until,
    vendor_job_id, handoff_check_at, probe_after, unsuccessful_probes, last_error, quarantine_reason,
    terminal_reason, processed_at, dead_lettered_at) ON background_jobs TO learnstack_outbox_admin;
GRANT UPDATE (phase, ownership_token, ownership_kind, ownership_until, available_after,
    wake_generation, bridge_token, bridge_until, vendor_job_id, handoff_check_at, probe_after,
    unsuccessful_probes, last_error, quarantine_reason, terminal_reason, dead_lettered_at,
    business_finalization_incomplete) ON background_jobs TO learnstack_outbox_admin;
GRANT SELECT (id, tenant_id, organization_id, job_id, stage, ordinal, admission_token, admitted_at,
    deadline, final_outcome_attempt_id, plan_fingerprint, operation_id, execute_intent_at, outcome_kind,
    outcome_at, quiescence_id, quiescence_kind, quiescent_at, no_start_at, transactional_cleanup_at, failure_policy)
    ON background_job_attempts TO learnstack_outbox_admin;
GRANT SELECT ON background_job_capacity, background_job_reservations,
    background_job_recurrences, background_job_occurrences, background_job_recipients TO learnstack_outbox_admin;
GRANT INSERT (recurrence_key, revision, manifest_hash, target_definition_key, schedule, activates_at,
    anchor_at, period_microseconds, jitter_microseconds, recipient_policy, misfire_policy, next_scheduled_at)
    ON background_job_recurrences TO learnstack_outbox_admin;
GRANT UPDATE (superseded_at, next_scheduled_at, available_after, ownership_token, ownership_until,
    last_error, quarantined_at) ON background_job_recurrences TO learnstack_outbox_admin;
GRANT INSERT (id, recurrence_key, revision, manifest_hash, target_definition_key, recipient_policy,
    scheduled_at, jitter_microseconds, skipped_slots) ON background_job_occurrences TO learnstack_outbox_admin;
GRANT UPDATE (bounds_frozen_at, upper_tenant_id, upper_organization_id, after_tenant_id,
    after_organization_id, available_after, ownership_token, ownership_until, completed_at,
    last_error, quarantined_at) ON background_job_occurrences TO learnstack_outbox_admin;
GRANT INSERT (id, occurrence_id, tenant_id, organization_id, recurrence_key, revision,
    manifest_hash, target_definition_key, recipient_policy, scheduled_at, due_at)
    ON background_job_recipients TO learnstack_outbox_admin;
GRANT UPDATE (available_after, ownership_token, ownership_until, skipped_at, skipped_reason,
    last_error, quarantined_at) ON background_job_recipients TO learnstack_outbox_admin;

REVOKE ALL ON FUNCTION public.fn_background_job_no_delete() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_jobs_envelope() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_jobs_transition() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_attempts_guard() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_attempt_counts() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_capacity_immutable() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_reservations_guard() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_control_lease() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_recurrences_guard() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_occurrences_guard() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_recipients_guard() FROM PUBLIC;

REVOKE ALL ON FUNCTION public.fn_background_job_recipient_receipt() FROM PUBLIC;
```

### Job lifecycle projection

Tenancy declares the status meanings as closed metadata; the job framework owns
the exact minimal reads. No module service, query factory, delegate or arbitrary
tenant selector is constructed by tenantless orchestration. Raw key pages contain
at most 100 routes before eligibility filtering; their frozen upper bounds and
last examined key belong to the same transaction as recipient INSERTs. This is
an eligibility sweep, not a historical MVCC cohort. Recipient submission rechecks
the exact admitted route under application-role scope.

The additive Tenancy migration owns the projection grant. No table-level SELECT,
name/content/entitlement access or tenancy mutation is granted to the dispatcher:

```sql
GRANT SELECT (id,status,deleted_at,default_organization_id) ON public.tenants TO learnstack_outbox_admin;
GRANT SELECT (tenant_id,id,status,deleted_at) ON public.organizations TO learnstack_outbox_admin;
```

### P02b-4 stock Hangfire control storage

The migration owner applies the unmodified Hangfire.PostgreSql 1.21.1
Install.v3–v23 resources from
`backend/src/LearnStack.Infrastructure/Jobs/HangfireSql/`, using only the pinned
installer's `public` schema substitution and version bookkeeping. The resulting
version is exactly 23, with the ADR-0047 twelve-table/nine-sequence inventory. No
runtime DDL or degraded preparation is permitted.

Tables: `aggregatedcounter`, `counter`, `hash`, `job`, `jobparameter`, `jobqueue`,
`list`, `lock`, `schema`, `server`, `set`, `state`.
Sequences: `aggregatedcounter_id_seq`, `counter_id_seq`, `hash_id_seq`, `job_id_seq`,
`jobparameter_id_seq`, `jobqueue_id_seq`, `list_id_seq`, `set_id_seq`, `state_id_seq`.

The grant list below is exhaustive. Runtime SELECT/DELETE are table grants;
INSERT/UPDATE name every accepted stock column. `state` and `jobparameter` deletion
is only through the job cascade. Vendor objects have no tenant RLS: isolation is
exact ACL denial to app, platform and PUBLIC, and the confined dispatcher path. No
schema-wide grant is added.

Actual recurring `Job` hash writes lock their exact unsuperseded manifest with
`FOR SHARE` inside the stock vendor transaction. The trigger is `VOLATILE`, so
its activation query takes a fresh snapshot after any earlier row-lock wait.
`FOR KEY SHARE` does not exclude a non-key supersession UPDATE. Startup verifies
the function's volatility together with its body, owner, language and ACL.
Retained older hints remain valid finite metadata until repair; unchanged stock
updates may omit `Job` and are not claimed to pass through this replacement gate.

```sql
REVOKE ALL ON TABLE public."schema" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT ON TABLE public."schema" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."counter" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."counter" TO learnstack_outbox_admin;
GRANT INSERT ("id","key","value","expireat") ON TABLE public."counter" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."aggregatedcounter" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."aggregatedcounter" TO learnstack_outbox_admin;
GRANT INSERT ("id","key","value","expireat") ON TABLE public."aggregatedcounter" TO learnstack_outbox_admin;
GRANT UPDATE ("id","key","value","expireat") ON TABLE public."aggregatedcounter" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."hash" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."hash" TO learnstack_outbox_admin;
GRANT INSERT ("id","key","field","value","expireat","updatecount") ON TABLE public."hash" TO learnstack_outbox_admin;
GRANT UPDATE ("id","key","field","value","expireat","updatecount") ON TABLE public."hash" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."job" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."job" TO learnstack_outbox_admin;
GRANT INSERT ("id","stateid","statename","invocationdata","arguments","createdat","expireat","updatecount") ON TABLE public."job" TO learnstack_outbox_admin;
GRANT UPDATE ("id","stateid","statename","invocationdata","arguments","createdat","expireat","updatecount") ON TABLE public."job" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."state" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT ON TABLE public."state" TO learnstack_outbox_admin;
GRANT INSERT ("id","jobid","name","reason","createdat","data","updatecount") ON TABLE public."state" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."jobqueue" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."jobqueue" TO learnstack_outbox_admin;
GRANT INSERT ("id","jobid","queue","fetchedat","updatecount") ON TABLE public."jobqueue" TO learnstack_outbox_admin;
GRANT UPDATE ("id","jobid","queue","fetchedat","updatecount") ON TABLE public."jobqueue" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."list" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."list" TO learnstack_outbox_admin;
GRANT INSERT ("id","key","value","expireat","updatecount") ON TABLE public."list" TO learnstack_outbox_admin;
GRANT UPDATE ("id","key","value","expireat","updatecount") ON TABLE public."list" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."server" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."server" TO learnstack_outbox_admin;
GRANT INSERT ("id","data","lastheartbeat","updatecount") ON TABLE public."server" TO learnstack_outbox_admin;
GRANT UPDATE ("id","data","lastheartbeat","updatecount") ON TABLE public."server" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."set" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."set" TO learnstack_outbox_admin;
GRANT INSERT ("id","key","score","value","expireat","updatecount") ON TABLE public."set" TO learnstack_outbox_admin;
GRANT UPDATE ("id","key","score","value","expireat","updatecount") ON TABLE public."set" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."jobparameter" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT ON TABLE public."jobparameter" TO learnstack_outbox_admin;
GRANT INSERT ("id","jobid","name","value","updatecount") ON TABLE public."jobparameter" TO learnstack_outbox_admin;
GRANT UPDATE ("id","jobid","name","value","updatecount") ON TABLE public."jobparameter" TO learnstack_outbox_admin;
REVOKE ALL ON TABLE public."lock" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT SELECT, DELETE ON TABLE public."lock" TO learnstack_outbox_admin;
GRANT INSERT ("resource","updatecount","acquired") ON TABLE public."lock" TO learnstack_outbox_admin;
GRANT UPDATE ("resource","updatecount","acquired") ON TABLE public."lock" TO learnstack_outbox_admin;
REVOKE ALL ON SEQUENCE public."counter_id_seq",public."hash_id_seq",public."job_id_seq",public."state_id_seq",public."jobqueue_id_seq",public."list_id_seq",public."set_id_seq",public."jobparameter_id_seq",public."aggregatedcounter_id_seq" FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT USAGE ON SEQUENCE public."counter_id_seq",public."hash_id_seq",public."job_id_seq",public."state_id_seq",public."jobqueue_id_seq",public."list_id_seq",public."set_id_seq",public."jobparameter_id_seq",public."aggregatedcounter_id_seq" TO learnstack_outbox_admin;
```

The following first-party invoker guards belong to this migration, separately
from the unchanged provider scripts. `fn_` functions use a fixed search path and
migration ownership; `tg_` triggers remain ALWAYS enabled. Bootstrap verifies
exact source bodies, owners, settings, trigger shape and ACLs before constructing
actual stock storage. Queue and server pool tokens use bounded grammar, while
this binary's client/performer selects only its frozen finite queues; valid newer
capability queues do not refuse an older binary's startup. Invocation targets,
signatures and operational keys are closed; UUID/generation references remain
opaque and are not business admission.
Stock delayed scheduling retains either a numeric vendor ID or its explicit
`queue:id` form under the `schedule` key only. Both parts are bounded; this is
transport routing metadata, never tenant authority. An additive Platform-chain
migration upgrades already-installed guards and can restore the prior predicate.

```sql
-- ADR-0047: below-filter transport containment. No business payload or authority.
-- Functions are invoker-rights, migration-owned and have a fixed search path.
CREATE FUNCTION public.fn_hangfire_queue(value text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path = pg_catalog
AS $$ SELECT value ~ '^[a-z][a-z0-9_]{0,49}$' $$;

CREATE FUNCTION public.fn_hangfire_server(value text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path = pg_catalog
AS $$ SELECT value ~ '^learnstack-[a-z][a-z0-9_]{0,49}:[1-9][0-9]{0,9}:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' $$;

CREATE FUNCTION public.fn_hangfire_recurrence(value text) RETURNS boolean
LANGUAGE sql STABLE SET search_path = pg_catalog, public
AS $$ SELECT EXISTS (SELECT FROM public.background_job_recurrences WHERE recurrence_key = value) $$;

CREATE FUNCTION public.fn_hangfire_invocation(data jsonb, args jsonb) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path = pg_catalog, public
AS $$
DECLARE parameter_types jsonb; argument0 jsonb; argument1 bigint; type_name text;
BEGIN
    IF jsonb_typeof(data) <> 'object' OR jsonb_typeof(args) <> 'array'
       OR octet_length(data::text) > 2048 OR octet_length(args::text) > 512
       OR jsonb_array_length(args) <> 2
       OR NOT data ?& ARRAY['Type','Method','ParameterTypes','Arguments']
       OR EXISTS (SELECT FROM jsonb_object_keys(data) key WHERE key NOT IN ('Type','Method','ParameterTypes','Arguments','Queue'))
       OR (data->>'Method') IS DISTINCT FROM 'RunAsync' OR (data ? 'Queue' AND data->>'Queue' IS NOT NULL AND NOT public.fn_hangfire_queue(data->>'Queue'))
       OR jsonb_typeof(data->'Type') <> 'string' OR jsonb_typeof(data->'ParameterTypes') <> 'string'
       OR jsonb_typeof(data->'Arguments') <> 'string'
       OR (data->>'Arguments')::jsonb <> args
       OR jsonb_typeof(args->0) <> 'string' OR jsonb_typeof(args->1) <> 'string'
    THEN RETURN false; END IF;
    parameter_types := (data->>'ParameterTypes')::jsonb;
    argument0 := (args->>0)::jsonb;
    IF (args->>1) !~ '^[1-9][0-9]{0,18}$' THEN RETURN false; END IF;
    argument1 := (args->>1)::bigint;
    type_name := data->>'Type';
    IF type_name = 'LearnStack.Infrastructure.Jobs.HangfireJobWakeup, LearnStack.Infrastructure' THEN
        RETURN parameter_types = '["LearnStack.SharedKernel.Jobs.BackgroundJobId, LearnStack.SharedKernel","System.Int64"]'::jsonb
          AND jsonb_typeof(argument0) = 'string'
          AND argument0 #>> '{}' ~ '^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$';
    ELSIF type_name = 'LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick, LearnStack.Infrastructure' THEN
        RETURN parameter_types = '["System.String","System.Int64"]'::jsonb
          AND jsonb_typeof(argument0) = 'string'
          AND EXISTS (SELECT FROM public.background_job_recurrences WHERE recurrence_key = argument0 #>> '{}' AND revision = argument1);
    END IF;
    RETURN false;
EXCEPTION WHEN OTHERS THEN RETURN false;
END $$;

CREATE FUNCTION public.fn_hangfire_job_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
BEGIN
    IF public.fn_hangfire_invocation(NEW.invocationdata,NEW.arguments) IS DISTINCT FROM true
       OR (NEW.statename IS NOT NULL AND NEW.statename NOT IN ('Enqueued','Scheduled','Processing','Succeeded','Failed','Deleted'))
       OR (TG_OP = 'UPDATE' AND (NEW.id,NEW.invocationdata,NEW.arguments,NEW.createdat) IS DISTINCT FROM (OLD.id,OLD.invocationdata,OLD.arguments,OLD.createdat))
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_job_guard BEFORE INSERT OR UPDATE ON public.job FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_job_guard();

CREATE FUNCTION public.fn_hangfire_state_valid(state_name text, state_reason text, state_data jsonb) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SET search_path = pg_catalog, public
AS $$
DECLARE pair record; allowed text[];
BEGIN
    allowed := CASE state_name
      WHEN 'Enqueued' THEN ARRAY['EnqueuedAt','Queue']
      WHEN 'Scheduled' THEN ARRAY['EnqueueAt','ScheduledAt']
      WHEN 'Processing' THEN ARRAY['StartedAt','ServerId','WorkerId']
      WHEN 'Succeeded' THEN ARRAY['SucceededAt','PerformanceDuration','Latency','Result']
      WHEN 'Failed' THEN ARRAY['FailedAt','ExceptionType','ExceptionMessage','ExceptionDetails','ServerId']
      WHEN 'Deleted' THEN ARRAY['DeletedAt','Exception'] ELSE NULL END;
    IF allowed IS NULL OR (state_data IS NOT NULL AND jsonb_typeof(state_data) <> 'object')
    THEN RETURN false; END IF;
    IF state_reason IS NOT NULL AND state_reason <> 'transport_redacted' THEN RETURN false; END IF;
    FOR pair IN SELECT * FROM jsonb_each(COALESCE(state_data,'{}'::jsonb)) LOOP
      IF NOT pair.key = ANY(allowed) THEN RETURN false; END IF;
      IF pair.key IN ('ExceptionType','ExceptionMessage','ExceptionDetails','Exception','Result') THEN
        IF pair.value IS DISTINCT FROM '"transport_redacted"'::jsonb THEN RETURN false; END IF;
      ELSIF jsonb_typeof(pair.value) <> 'string'
        OR (pair.key = 'Queue' AND NOT public.fn_hangfire_queue(pair.value #>> '{}'))
        OR (pair.key = 'ServerId' AND NOT public.fn_hangfire_server(pair.value #>> '{}'))
        OR (pair.key = 'WorkerId' AND pair.value #>> '{}' !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$')
        OR (pair.key IN ('PerformanceDuration','Latency') AND pair.value #>> '{}' !~ '^[0-9]{1,15}$')
        OR (pair.key IN ('EnqueuedAt','EnqueueAt','ScheduledAt','StartedAt','SucceededAt','FailedAt','DeletedAt') AND pair.value #>> '{}' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$')
      THEN RETURN false; END IF;
    END LOOP;
    IF octet_length(COALESCE(state_data::text,'')) > 1024 THEN RETURN false; END IF;
    RETURN true;
EXCEPTION WHEN OTHERS THEN RETURN false;
END $$;
CREATE FUNCTION public.fn_hangfire_state_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
DECLARE field text;
BEGIN
    -- Redact before validating shape/size, including the stock disabled-filter fallback.
    IF NEW.reason IS NOT NULL THEN NEW.reason := 'transport_redacted'; END IF;
    IF jsonb_typeof(NEW.data) = 'object' THEN
      FOREACH field IN ARRAY ARRAY['ExceptionType','ExceptionMessage','ExceptionDetails','Exception','Result'] LOOP
        IF NEW.data ? field THEN NEW.data := jsonb_set(NEW.data,ARRAY[field],'"transport_redacted"'::jsonb); END IF;
      END LOOP;
    END IF;
    IF public.fn_hangfire_state_valid(NEW.name,NEW.reason,NEW.data) IS DISTINCT FROM true
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_state_guard BEFORE INSERT OR UPDATE ON public.state FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_state_guard();

CREATE FUNCTION public.fn_hangfire_parameter_valid(parameter_name text, parameter_value text) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path = pg_catalog, public
AS $$
DECLARE value jsonb;
BEGIN
    value := parameter_value::jsonb;
    IF ((parameter_name IN ('CurrentCulture','CurrentUICulture') AND value = '""'::jsonb)
       OR (parameter_name = 'RetryCount' AND parameter_value = '0')
       OR (parameter_name = 'Time' AND parameter_value ~ '^[0-9]{1,15}$')
       OR (parameter_name = 'RecurringJobId' AND jsonb_typeof(value) = 'string' AND public.fn_hangfire_recurrence(value #>> '{}'))) IS DISTINCT FROM true
       OR octet_length(parameter_value) > 256
    THEN RETURN false; END IF;
    RETURN true;
EXCEPTION WHEN OTHERS THEN RETURN false;
END $$;
CREATE FUNCTION public.fn_hangfire_parameter_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public
AS $$
BEGIN
    IF public.fn_hangfire_parameter_valid(NEW.name,NEW.value) IS DISTINCT FROM true
       OR (TG_OP = 'UPDATE' AND (NEW.id,NEW.jobid,NEW.name) IS DISTINCT FROM (OLD.id,OLD.jobid,OLD.name))
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_parameter_guard BEFORE INSERT OR UPDATE ON public.jobparameter FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_parameter_guard();

CREATE FUNCTION public.fn_hangfire_metadata_valid(table_name text, row_data jsonb) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path = pg_catalog, public
AS $$
DECLARE valid boolean := false; payload jsonb;
BEGIN
    CASE table_name
    WHEN 'jobqueue' THEN valid := public.fn_hangfire_queue((row_data->>'queue')) AND (row_data->>'jobid')::bigint > 0;
    WHEN 'server' THEN
      valid := public.fn_hangfire_server((row_data->>'id')) AND jsonb_typeof((row_data->'data')) = 'object'
        AND (row_data->'data') ?& ARRAY['WorkerCount','Queues','StartedAt']
        AND (SELECT count(*) FROM jsonb_object_keys((row_data->'data'))) = 3
        AND (row_data->'data')->>'WorkerCount' ~ '^([1-9]|1[0-6])$'
        AND jsonb_typeof((row_data->'data')->'WorkerCount') = 'number'
        AND jsonb_typeof((row_data->'data')->'Queues') = 'array' AND jsonb_array_length((row_data->'data')->'Queues') BETWEEN 1 AND 64
        AND NOT EXISTS (SELECT FROM jsonb_array_elements((row_data->'data')->'Queues') queue WHERE jsonb_typeof(queue) <> 'string' OR public.fn_hangfire_queue(queue #>> '{}') IS DISTINCT FROM true)
        AND (row_data->'data')->>'StartedAt' ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
    WHEN 'counter','aggregatedcounter' THEN valid := (row_data->>'key') IN ('stats:succeeded','stats:deleted') OR (row_data->>'key') ~ '^stats:(succeeded|failed|deleted):[0-9]{4}-[0-9]{2}-[0-9]{2}(-[0-9]{2})?$';
    WHEN 'lock' THEN valid := (row_data->>'resource') IN ('locks:expirationmanager','public:locks:schedulepoller','public:recurring-jobs:lock')
        OR (row_data->>'resource') ~ '^public:job:[1-9][0-9]{0,18}:state-lock$'
        OR ((row_data->>'resource') LIKE 'public:lock:recurring-job:%' AND public.fn_hangfire_recurrence(substr((row_data->>'resource'),27)));
    WHEN 'set' THEN valid := (row_data->>'score')::float8 NOT IN ('Infinity'::float8,'-Infinity'::float8,'NaN'::float8)
        AND (((row_data->>'key') IN ('schedule','failed','succeeded','deleted','processing') AND (row_data->>'value') ~ '^[1-9][0-9]{0,18}$')
        OR ((row_data->>'key') = 'schedule' AND (row_data->>'value') ~ '^[a-z][a-z0-9_]{0,49}:[1-9][0-9]{0,18}$')
        OR ((row_data->>'key') = 'recurring-jobs' AND public.fn_hangfire_recurrence((row_data->>'value'))));
    WHEN 'list' THEN valid := (row_data->>'key') IN ('failed','succeeded','deleted') AND (row_data->>'value') ~ '^[1-9][0-9]{0,18}$';
    WHEN 'hash' THEN
      IF (row_data->>'key') LIKE 'recurring-job:%' AND public.fn_hangfire_recurrence(substr((row_data->>'key'),15)) THEN
        CASE (row_data->>'field')
        WHEN 'Job' THEN
          payload := (row_data->>'value')::jsonb;
          valid := public.fn_hangfire_invocation(payload,(payload->>'Arguments')::jsonb)
            AND payload->>'Type' = 'LearnStack.Infrastructure.Jobs.HangfireRecurrenceTick, LearnStack.Infrastructure'
            AND ((payload->>'Arguments')::jsonb->>0)::jsonb #>> '{}' = substr((row_data->>'key'),15);
        WHEN 'Queue' THEN valid := public.fn_hangfire_queue((row_data->>'value'));
        WHEN 'TimeZoneId' THEN valid := (row_data->>'value') = 'UTC';
        WHEN 'Cron' THEN valid := (row_data->>'value') = '* * * * *';
        WHEN 'V' THEN valid := (row_data->>'value') = '2';
        WHEN 'Misfire' THEN valid := (row_data->>'value') = '0';
        WHEN 'RetryAttempt' THEN valid := (row_data->>'value') ~ '^[0-6]$';
        WHEN 'LastJobId' THEN valid := (row_data->>'value') = '' OR (row_data->>'value') ~ '^[1-9][0-9]{0,18}$';
        WHEN 'CreatedAt','LastExecution','NextExecution' THEN valid := (row_data->>'value') = '' OR (row_data->>'value') ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$';
        WHEN 'Error' THEN valid := (row_data->>'value') = 'transport_redacted';
        ELSE valid := false; END CASE;
      END IF;
      valid := valid AND octet_length(COALESCE((row_data->>'value'),'')) <= 2048;
    ELSE valid := false; END CASE;
    RETURN valid IS TRUE;
EXCEPTION WHEN OTHERS THEN RETURN false;
END $$;
CREATE FUNCTION public.fn_hangfire_metadata_guard() RETURNS trigger
LANGUAGE plpgsql VOLATILE SET search_path = pg_catalog, public
AS $$
DECLARE payload jsonb;
BEGIN
    IF TG_TABLE_NAME = 'hash' THEN
      IF NEW.field = 'Error' THEN NEW.value := 'transport_redacted'; END IF;
    END IF;
    IF public.fn_hangfire_metadata_valid(TG_TABLE_NAME,to_jsonb(NEW)) IS DISTINCT FROM true
    THEN RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514'; END IF;
    IF TG_TABLE_NAME = 'hash' THEN
      IF NEW.field = 'Job' THEN
        payload := NEW.value::jsonb;
        -- A fresh trigger query and SHARE lock serialize the actual vendor write
        -- with supersession, including waits before this trigger is entered.
        PERFORM 1 FROM public.background_job_recurrences
          WHERE recurrence_key = substr(NEW.key,15)
            AND revision = ((payload->>'Arguments')::jsonb->>1)::bigint
            AND superseded_at IS NULL
          FOR SHARE;
        IF NOT FOUND THEN
          RAISE EXCEPTION 'hangfire_metadata_refused' USING ERRCODE = '23514';
        END IF;
      END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.jobqueue FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.server FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.counter FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.aggregatedcounter FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.lock FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.set FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.list FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();
CREATE TRIGGER tg_hangfire_metadata_guard BEFORE INSERT OR UPDATE ON public.hash FOR EACH ROW EXECUTE FUNCTION public.fn_hangfire_metadata_guard();

ALTER TABLE public."job" ENABLE ALWAYS TRIGGER tg_hangfire_job_guard;
ALTER TABLE public."state" ENABLE ALWAYS TRIGGER tg_hangfire_state_guard;
ALTER TABLE public."jobparameter" ENABLE ALWAYS TRIGGER tg_hangfire_parameter_guard;
ALTER TABLE public."jobqueue" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."server" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."counter" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."aggregatedcounter" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."lock" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."set" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."list" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
ALTER TABLE public."hash" ENABLE ALWAYS TRIGGER tg_hangfire_metadata_guard;
REVOKE ALL ON FUNCTION public.fn_hangfire_state_valid(text,text,jsonb), public.fn_hangfire_parameter_valid(text,text), public.fn_hangfire_metadata_valid(text,jsonb), public.fn_hangfire_queue(text), public.fn_hangfire_server(text), public.fn_hangfire_recurrence(text), public.fn_hangfire_invocation(jsonb,jsonb), public.fn_hangfire_job_guard(), public.fn_hangfire_state_guard(), public.fn_hangfire_parameter_guard(), public.fn_hangfire_metadata_guard() FROM PUBLIC, learnstack_app, learnstack_platform, learnstack_outbox_admin;
GRANT EXECUTE ON FUNCTION public.fn_hangfire_state_valid(text,text,jsonb), public.fn_hangfire_parameter_valid(text,text), public.fn_hangfire_metadata_valid(text,jsonb), public.fn_hangfire_queue(text), public.fn_hangfire_server(text), public.fn_hangfire_recurrence(text), public.fn_hangfire_invocation(jsonb,jsonb), public.fn_hangfire_job_guard(), public.fn_hangfire_state_guard(), public.fn_hangfire_parameter_guard(), public.fn_hangfire_metadata_guard() TO learnstack_outbox_admin;

```

The shared source requires reset-safe PostgreSQL startup defaults
`statement_timeout=5000`, `lock_timeout=5000` and
`idle_in_transaction_session_timeout=5000`, driver connect/command defaults of
5 seconds plus cancellation cleanup of 1 second, `Enlist=false`, pool reset enabled
and raw error-detail/parameter logging disabled. No shared `transaction_timeout`
is introduced. Statement/lock deadlines apply per statement/acquisition; idle
cleanup terminates idle/aborted backend sessions, not client sockets. Warm reset,
successful outbox/delivery transactions and real aborted-idle cleanup require
production-source controls.

Wire serialization pins `CompatibilityLevel.Version_110`, simple assembly type
names and `TypeNameHandling.None`; the two instance `Task RunAsync` targets accept
`BackgroundJobId,long` and `string,long`, with no serialized cancellation token.
Provider preparation/degraded startup/enlistment/long polling are false; native
queue transactions and sliding invisibility are true. Accepted
[ADR-0058](../decisions/0058-bounded-stock-hangfire-maintenance.md) suppresses only
the exact stock counter aggregator and statistics-history filter through public
hooks, retaining stock expiration/heartbeat and required admission/retry filters.
Every enabled command retains finite driver read/write I/O bounds; server SQL
tests alone do not prove them. Exact component/filter inventories and the positive
one-millisecond internal expiration interval must be verified before workers start.

Version/guard/ACL failure refuses the explicit storage bootstrap before worker
construction. Contract-only registration remains lazy and activates no worker.
The [Step 2 record](../roadmap/phase-02b-delivery-log.md#p02b-4-step-2--storage-and-guarded-vendor-bootstrap-2026-10-16)
owns delivery status: Step 2 implements idle-session settings and source/reset
qualification. The [Step 4 continuation](../roadmap/phase-02b-delivery-log.md#source-lifetime-and-facade-continuation-2026-10-16)
owns current runtime/source qualification and full-step review dispositions.
Step 4 is locally complete after both full review rounds and verified corrections.
Later SQL lanes require fresh capacity qualification; Step 9 owns full
observation/enforcement.

#### Shared dispatcher source lifetime and capacity

The shared `PrivilegedDataSource` treats container disposal as a request. Actual
source disposal waits for positive completion of `BackgroundJobRuntime` and both
actual messaging processors' startup/main-loop source use. A finite `StopAsync`
return, timeout, fault or cancellation supplies no positive completion witness.
Already-owned completion/removal SQL retains access during ordinary host stop.
Failed actual source disposal remains recorded; repeated disposal cannot turn the
driver's later no-op into cleanup evidence.

The dispatch and bridge stores retain concrete connections and disposal tasks on
connection-cleanup failure and seal later acquisitions. Each messaging store has
two fixed ownership slots for terminal-COMMIT reconciliation; the bridge has one
serialized SQL slot. These witnesses concern physical-source release, not all
application callbacks or private driver tasks. Failed opens rely on the pinned
driver's qualified ordinary unwind; an arbitrary driver-cleanup fault is not a
release guarantee.

`BackgroundJobPhysicalCapacity` calculates the current Step 8 floor as
`W + 4P + 2C + 18 + U = 2W + 8P + 2C + 18`, where `W` is total workers, `P` is
pools, `C` is control pools and `U = W + 4P` is retained invocation owners.
Each invocation issues at most one privileged routing, recovery acquisition,
confirmation or checkpoint-history SQL operation at a time, including actual
cleanup. A clean unavailable history retry follows cleanup in that same slot;
it does not add another simultaneous checkout.
The original eleven covers two shared components, four bridge calls, one bridge
SQL lane and two depth-two messaging lanes. Step 5 adds one batch-renewal slot,
one recovery slot plus its fresh-probe slot, and one terminal slot plus its
fresh-probe slot. Step 8 adds one serialized recurrence SQL lane and one separately
owned native recurring-manager call. The latter reuses its dedicated provider
connection for its stock lock and write transaction. A held native call cannot
consume another recurrence SQL slot. Failed cleanup retains the concrete owner
and seals that lane;
probes do not recursively acquire another probe. The default topology requires
**52**; the maximum valid topology requires **158**. Pooled startup refuses a
lower `MaxPoolSize` before bootstrap, using settings captured from the same
immutable configuration as the source. Nonpooling remains allowed with the same
owner bounds; its positive `MaxPoolSize` is not a physical-checkout cap.

The floor is a conservative supported envelope, not an observed simultaneous
peak. Its complete acquisition ledger is:

| Allowance | Actual owner / nesting boundary |
|---|---|
| `W` | One stock worker acquisition lane per declared worker; a storage wrapper alone is not a physical checkout. |
| `4P` | Per-generation metadata heartbeat, watchdog, cancellation watcher and lifecycle allowance; retained cleanup still belongs to that one generation. |
| `2C` | Delayed and recurring schedulers, each with parallelism one and sequential reuse of its outer distributed-lock connection. |
| Original eleven | Shared expiration and fetched-job heartbeat; four bridge native calls plus serialized SQL; two concrete slots in each messaging store. |
| Five control slots | One batch renewal, control/recovery plus fresh probe, terminal plus fresh probe; unconfirmed cleanup seals each lane. |
| Two recurrence slots | One serialized SQL flight and one native manager, including actual return and cleanup. |
| `U` | One privileged routing/recovery/history/status flight per admitted invocation, including retained cleanup; application work uses the separate source. |

Lifecycle activity need not overlap every normal child, and control-tick invocation
reserve is conservative. Ordinary stock worker state locks are released before
`PerformJob`; their wrapper lifetime does not prove a nested physical checkout
throughout business execution. Genuine scheduler, native-client lock and
original/fresh-probe nesting remain included. Long polling/reconnect or another
source borrower changes this ledger and requires requalification.

The API application source has one `ApplicationDataSourceLifetime` owner and a
stable `Lazy<NpgsqlDataSource>` registration, without a raw-source DI alias.
Container disposal requests release; actual release waits for the same job and
messaging completion witnesses. Each job invocation's `JobApplicationSourceOwner`
permits one main unit and one independently tracked auxiliary operation. Cache
waiter expiry cannot replace the actual loader, connection or failed cleanup.
The four closed auxiliary helpers are audit-config, standalone/best-effort audit,
tenant flags and killswitch reads; nested ordinary work shares the same main unit.
The job-only application floor is `2Ub + 2 + 8`, where `Ub` sums workers-plus-four for
both transactional and external pools: **32** by default and **128** at the
maximum business-pool topology. The existing terminal lane owns one sequential
NoStart application owner with one main and one tracked auxiliary slot per API
instance. Step 8 adds four recipient submission owners, each with the same
main/auxiliary pair. This is a bound on all locally retained recipient owners,
including live work and unresolved receipt history; it does not add stock worker
slots. Adding control pools adds neither terminal nor recipient application owners.
External Prepare, read, Arm, Outcome, recovery checkpoints and fresh Finalize
use the same sequential main/auxiliary inventory; their phase count is not a
connection multiplier. The facade fixture for privileged maximum 158
has eight pools of four workers, six of them control, and requires 42 application
connections. Other worker distributions can share the same privileged maximum
with a different application floor; neither maximum substitutes for calculating
both manifests from the actual topology.
Unrelated HTTP/delivery demand requires separately budgeted headroom. This floor
is not a bound on arbitrary arrivals or total application-source demand.

Step 6 reuses the recovery/probe lane for capacity acquisition and parked probes.
Its app checkpoint follows positive main/auxiliary/callback cleanup on the same
invocation source owner, with old bindings permanently closed. One prebound
checkpoint slot retains actual work and cancellation cleanup; a failed cleanup
cannot start another scope. This sequential phase adds no simultaneous main or
auxiliary checkout. The [Step 6 record](../roadmap/phase-02b-delivery-log.md#p02b-4-step-6--tenant-capacity-reservations-2026-10-16)
owns passing local qualification, both completed full review rounds and verified
corrections.

These are current acquisition inventories under Accepted ADR-0047, not
deployment-wide PostgreSQL capacity guarantees. Independent progress at each
supported boundary has source-specific qualification in the
[Step 9 record](../roadmap/phase-02b-delivery-log.md#combined-physical-capacity-qualification).
The combined real-owner proof at the default52 configuration complements the
complete ledger and existing topology/retention controls; it does not claim52
simultaneously busy connections or a tight empirical minimum.
Later SQL lanes require a new census and requalification; Phase 11 owns measured
production capacity and PgBouncer.

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
