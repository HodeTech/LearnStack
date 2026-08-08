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
| Indexes | `ix_<table>_<columns>` | `ix_courses_tenant_id_slug` |
| Unique indexes | `ux_<table>_<columns>` | `ux_courses_tenant_id_slug` |
| Check constraints | `ck_<table>_<rule>` | `ck_courses_slug_format` |
| RLS policies | `<table>_isolation` (one per table) | `courses_isolation` |
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
    slug            text NOT NULL,
    title           text NOT NULL,
    -- ... domain columns ...
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid NOT NULL,
    row_version     bigint NOT NULL DEFAULT 0,
    CONSTRAINT ux_courses_tenant_id_slug UNIQUE (tenant_id, slug),
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
- The session variable names `app.tenant_id`, `app.organization_id` and `app.scope`
  are canonical; do not invent alternatives (`app.current_tenant_id`,
  `learnstack.tenant_id`, …). Always call `current_setting` with the second argument
  `true` (missing-OK) so an unset context yields `NULL` and the predicate filters the
  row out, rather than raising inside a pooled connection.
- The application sets `app.tenant_id` and `app.organization_id` with `SET LOCAL`
  **inside the ambient transaction**, after it opens. These settings are
  transaction-local: set before the transaction begins — in a MediatR behavior that
  runs earlier, or in a connection interceptor that fires at connection open — they are
  discarded before the query they are meant to protect ever runs. See
  [Security Standards § Tenant Context](11-security.md).

### Database roles

| Role | Used by | RLS posture |
|------|---------|-------------|
| `learnstack_migration` | EF Core migrations; **owns** every tenant-owned table | `NOBYPASSRLS` — `FORCE ROW LEVEL SECURITY` denies ownership any bypass |
| `learnstack_app` | The application's runtime connection; not an owner | `NOBYPASSRLS`; granted only `SELECT, INSERT, UPDATE, DELETE` |
| `learnstack_platform` | Cross-tenant platform-admin work, entered only through the audited `EnterPlatformAdminScope(reason)` path | `BYPASSRLS` |
| `learnstack_outbox_admin` | The outbox dispatcher — see [Events and Outbox](../architecture/15-event-and-outbox.md) | `BYPASSRLS` |

```sql
CREATE ROLE learnstack_migration LOGIN NOBYPASSRLS;
CREATE ROLE learnstack_app       LOGIN NOBYPASSRLS;

ALTER TABLE courses OWNER TO learnstack_migration;
GRANT SELECT, INSERT, UPDATE, DELETE ON courses TO learnstack_app;
```

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
- `UNIQUE` constraints for tenant-scoped natural keys (`UNIQUE (tenant_id, slug)`).
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
    correlation_id  text NULL,             -- opaque correlation id from APISIX request-id
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
CREATE POLICY outbox_messages_isolation ON outbox_messages
    USING      (tenant_id = current_setting('app.tenant_id', true)::uuid)
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- The OutboxProcessor connects as learnstack_outbox_admin, which holds BYPASSRLS so it
-- can read every tenant's pending rows for dispatch. That bypass is the reason the
-- table's own policy still matters: application code writing an outbox row does so as
-- learnstack_app, inside the business transaction, and WITH CHECK pins the row to the
-- caller's tenant. See ADR-0006 Amendment 1 and 15-event-and-outbox.md.
```

`correlation_id` is `text NULL` (not `uuid NOT NULL`): APISIX's `request-id` plugin
echoes any client-supplied id and falls back to a UUID, so the field is opaque and
optional. The architecture deep dive lives in
[15-event-and-outbox.md](../architecture/15-event-and-outbox.md).

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
- A checkout interceptor verifies both values are set before queries run; a missing
  `app.tenant_id` throws `TenantContextMissingException` rather than risking an
  unscoped query.

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
