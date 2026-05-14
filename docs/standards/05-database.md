# 05 — Database Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0003 — Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md), [ADR 0006 — Events and Outbox](../decisions/0006-events-and-outbox.md).

PostgreSQL schema, EF Core, and migration conventions.

## Database

- **PostgreSQL 16+** in all environments.
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
| RLS policies | `<table>_tenant_isolation` | `courses_tenant_isolation` |
| Triggers | `tg_<table>_<purpose>` | `tg_courses_set_updated_at` |
| Functions | `fn_<purpose>` | `fn_set_updated_at` |
| Read-model tables | `public_<module>_<concept>` | `public_education_course_summaries` |
| Outbox | `outbox_messages` (global) | |

`learnstack_` prefix reserved for system-wide objects (roles, extensions).

## Tenant-Owned Tables

Every tenant-owned table **must**:

```sql
CREATE TABLE courses (
    id              uuid PRIMARY KEY,
    tenant_id       uuid NOT NULL,
    slug            text NOT NULL,
    title           text NOT NULL,
    -- ... domain columns ...
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      uuid NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      uuid NOT NULL,
    row_version     bigint NOT NULL DEFAULT 0,
    CONSTRAINT ux_courses_tenant_id_slug UNIQUE (tenant_id, slug)
);

ALTER TABLE courses ENABLE ROW LEVEL SECURITY;

CREATE POLICY courses_tenant_isolation ON courses
    USING (tenant_id = current_setting('app.current_tenant_id')::uuid);

CREATE INDEX ix_courses_tenant_id ON courses (tenant_id);
```

Rules:
- `tenant_id` is the **first column** of every composite index unless profiling proves otherwise.
- Every tenant-owned table has an RLS policy.
- Application sets `app.current_tenant_id` on connection checkout via `SET LOCAL`.
- Platform-admin operations use a separate database role (`learnstack_platform`) that bypasses RLS.

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
- Tenant-aware: per-tenant data migrations use the tenant role and `SET app.current_tenant_id` per tenant.

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
    correlation_id  uuid NOT NULL,
    type            text NOT NULL,
    payload         jsonb NOT NULL,
    processed_at    timestamptz NULL,
    attempts        int NOT NULL DEFAULT 0,
    last_error      text NULL,
    available_after timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_outbox_messages_pending
    ON outbox_messages (available_after)
    WHERE processed_at IS NULL;
```

See [Events & Outbox](../architecture/15-event-and-outbox.md).

## Raw SQL

Allowed when:
- EF Core cannot express the query efficiently.
- Tenant predicate is **explicit**.
- Query is parameterized.
- Query is covered by an integration test.

Forbidden: string interpolation with non-constant values.

## Connection Management

- Npgsql connection multiplexing where appropriate.
- PgBouncer in transaction mode for production.
- `app.current_tenant_id` set **within the same transaction** as the work (`SET LOCAL ...`).
- A checkout interceptor verifies tenant is set before queries run.

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
