---
name: add-ef-migration
description: >
  Generate and edit an EF Core migration for a LearnStack module with the project's
  naming convention, RLS-aware patterns (session vars `app.tenant_id` /
  `app.organization_id`), partition-aware patterns for append-only tables, and
  forward-only / two-step destructive-change rules. USE FOR: adding / altering /
  dropping a tenant-owned table or column, adding indexes, partitioning an
  append-only table by month. DO NOT USE FOR: hand-editing applied migrations,
  destructive changes without a two-step plan, schema changes that cross module
  schemas.
---

# Adding an EF Core migration

## Purpose

Land schema changes that compile, ship the right RLS / partition / index plumbing,
and survive forward-only deploy rules
([05-database.md](../../../docs/standards/05-database.md)).

## When to use

- Adding a new table, column, index, constraint.
- Adding RLS policies to an existing tenant-owned table that lacks them.
- Adding monthly partition for an append-only table (`audit_log`, future event
  tables).
- Backfilling data alongside a schema change (use an idempotent SQL block or a
  Hangfire job).

## When not to use

- Dropping a column that production data still uses without a two-step deploy plan.
- Renaming a column without a deprecation cycle.
- Editing a migration that already shipped to staging or production.
- Cross-module schema changes (each module owns its own migrations; cross-module
  reads use read-model projections).

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Migration intent | Yes | Snake-case description: `add_enrollment_status_column`. |
| Module | Yes | The module's `Infrastructure` project hosts the migration. |
| Touches RLS? | Yes | New table → yes. Column add on existing tenant-owned table → no. |
| Destructive? | Yes | Drop, rename, type-change — requires two-step plan. |
| Backfill? | No | If data must be populated, prefer an idempotent Hangfire job. |

## Workflow

### Step 1: Generate the migration

```bash
# Pass the INTENT only, in snake_case. EF prepends the UTC timestamp itself, so
# the file lands as <UTC_yyyyMMddHHmmss>_<intent>.cs — the format Standards 05
# specifies. Typing the timestamp as well produces it twice.
dotnet ef migrations add <intent> \
  --project backend/src/Modules/<Module>/LearnStack.Modules.<Module>.Infrastructure \
  --startup-project backend/src/LearnStack.Api \
  --output-dir Persistence/Migrations
```

Name format: timestamp + snake-case intent. Examples:

- `20260601120000_add_enrollment_status_column`
- `20260605091500_partition_audit_log_2026_07`

### Step 2: Verify what EF generated

Open the new migration file. EF usually gets the C# side right (CreateTable,
AddColumn) but does not know about:

- RLS enable + policies (you add them manually).
- Partition declarations (you add them manually).
- Postgres-specific defaults (`uuidv7()`, `now()`). `gen_random_uuid()` in a generated migration is a **defect**: it produces a v4 UUID with none of the index locality UUIDv7 was adopted for ([Database Standards § Identifiers](../../../docs/standards/05-database.md)).
- Strongly-typed id column types — confirm they map to `uuid`.

### Step 3: New table — add the four mandatory pieces

For a `[TenantOwned]` table, the migration must include:

```csharp
migrationBuilder.Sql("""
    CREATE TABLE <name_plural> (
        id               uuid PRIMARY KEY,
        tenant_id        uuid NOT NULL,
        organization_id  uuid NULL,                  -- only for [OrganizationScoped]
        -- ... domain columns ...
        -- The six-column set from Database Standards § Audit Columns, verbatim.
        -- updated_* are NULLABLE: MarkCreated stamps created_* only, so NOT NULL
        -- here rejects every INSERT. deleted_* are UNCONDITIONAL: AuditableEntity<TId>
        -- implements ISoftDelete for every aggregate, so EF maps them either way and
        -- a table without them cannot materialize its own entity.
        created_at       timestamptz NOT NULL DEFAULT now(),
        created_by       uuid NOT NULL,
        updated_at       timestamptz NULL,
        updated_by       uuid NULL,
        deleted_at       timestamptz NULL,
        deleted_by       uuid NULL,
        row_version      bigint NOT NULL DEFAULT 0,
        -- Exists solely so child tables can carry a composite FK into this one.
        CONSTRAINT ux_<name_plural>_tenant_id_id UNIQUE (tenant_id, id)
    );

    -- Every foreign key from this table to another tenant-owned table is
    -- COMPOSITE on tenant_id:
    --
    --     CONSTRAINT fk_<name_plural>_<parent>
    --         FOREIGN KEY (tenant_id, <parent>_id)
    --         REFERENCES <parents> (tenant_id, id)
    --
    -- Referential-integrity checks run on behalf of the table owner and are NOT
    -- subject to Row Level Security, so a single-column FK lets one tenant
    -- reference another tenant's rows and no policy ever observes it.

    -- One composite index, deliberately NOT partial: the policy's
    -- `organization_id IS NULL` branch matches every tenant-wide row and a b-tree
    -- indexes NULLs, so the non-partial form serves both branches. No standalone
    -- index on tenant_id — the UNIQUE constraints above already lead with it.
    -- Drop organization_id from the index when the table is not org-scoped.
    CREATE INDEX ix_<name_plural>_tenant_id_organization_id
        ON <name_plural> (tenant_id, organization_id);

    -- ─────────────────────────────────────────────────────────────────────────
    -- RLS: DO NOT WRITE THE POLICY FROM MEMORY, AND DO NOT COPY IT HERE.
    --
    -- Open docs/standards/05-database.md § Tenant-Owned and Organization-Scoped
    -- Tables and copy the canonical block into this migration now, substituting
    -- <name_plural>. That file is the only place the template exists.
    --
    -- The pre-2026-08-08 template lived in four documents and was wrong in all
    -- four — two PERMISSIVE policies, which PostgreSQL combines with OR, so every
    -- tenant-wide row was visible across tenants (ADR-0003 Amendment 3). It was
    -- corrected once, in one file. A second copy is how that recurs.
    --
    -- Checklist for what you paste:
    --   * ENABLE *and* FORCE ROW LEVEL SECURITY            (both table kinds)
    --   * explicit WITH CHECK                               (both table kinds)
    --   * NULLIF(current_setting(...), '') on every GUC read (both table kinds)
    --
    --   TENANT-ONLY table: exactly ONE permissive policy carrying the tenant
    --   predicate alone. No organization term, no restrictive guards — there is
    --   no second scope to widen or narrow.
    --
    --   [OrganizationScoped] table: still exactly ONE permissive policy, but its
    --   predicate ANDs the tenant term with the organization term; PLUS the two
    --   AS RESTRICTIVE guards (FOR UPDATE, FOR DELETE), because the
    --   app.scope='tenant' read hatch must not widen writes and DELETE has no
    --   WITH CHECK.
    -- ─────────────────────────────────────────────────────────────────────────
    """);
```

> The canonical template is
> [05-database.md § Tenant-Owned and Organization-Scoped Tables](../../../docs/standards/05-database.md),
> and this skill deliberately does **not** mirror it — the block above tells you to open
> that file and copy from it. See
> [ADR-0003 Amendment 3](../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md)
> for why the two-policy shape was withdrawn.

**Session variable names** are canonical: `app.tenant_id` / `app.organization_id` /
`app.scope`. Always pass the second `true` argument so an unset context filters the row
out instead of raising on a pooled connection.

**Roles.** Migrations run as `learnstack_migration` (the table owner);
the application connects as `learnstack_app` (`NOBYPASSRLS`, not the owner). Grant the
new table to `learnstack_app` in the same migration, or the application cannot read it.

### Step 4: Append-only table

An append-only table ships **unpartitioned**, with a composite primary key that a
future partition conversion can reuse. Do **not** write `PARTITION BY` in the first
migration: partitioning is demand-gated to
[Phase 11](../../../docs/roadmap/phase-11-production-hardening.md) on measured growth
([ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md)), and shipping
it early buys partition maintenance before there is anything to maintain.

```csharp
migrationBuilder.Sql("""
    CREATE TABLE audit_log (
        id            uuid NOT NULL,
        timestamp     timestamptz NOT NULL DEFAULT now(),
        tenant_id     uuid NOT NULL,
        -- ... rest ...
        -- The partition key must be IN the primary key for the Phase 11 conversion,
        -- so declare the composite now even though nothing is partitioned yet.
        -- Column name is `timestamp`, matching the canonical DDL in ADR-0033 and
        -- 31-audit-subsystem — not `occurred_at`, which is outbox_messages' column.
        CONSTRAINT audit_log_pkey PRIMARY KEY (id, timestamp)
    );
    """);
```

PostgreSQL has no `ALTER TABLE … PARTITION BY`, so Phase 11 does not convert this table
in place: it creates a partitioned parent, attaches this table to it, and recreates the
indexes and the policy on the parent. The composite key above is what keeps that a data
operation rather than a key migration
([ADR-0033 § Corrected `audit_log` DDL](../../../docs/decisions/0033-audit-durability-model.md)).

The Postgres trigger that rejects `UPDATE` / `DELETE` on `audit_log` lives in the
audit module's setup migration; reproduce it for any new append-only table.

### Step 5: Column add (existing table)

```csharp
migrationBuilder.AddColumn<string>(
    name: "status",
    table: "enrollments",
    type: "text",
    nullable: false,
    defaultValue: "active");
```

If the column has a CHECK constraint:

```csharp
migrationBuilder.Sql("""
    ALTER TABLE enrollments
        ADD CONSTRAINT ck_enrollments_status
        CHECK (status IN ('active', 'suspended', 'completed', 'cancelled'));
    """);
```

### Step 6: Destructive change (drop / rename / type)

A destructive change requires a **two-step deploy** plan:

1. **Tolerant code** — the application reads both old and new shape, writes the
   new shape only.
2. **Migration** — adds the new shape, backfills, drops the old shape.
3. **Strict code** — removes the tolerant read path.

Each step is its own release. Document the plan in the PR description and in the
migration's leading comment:

```csharp
// Destructive: drops enrollments.source_legacy after backfill.
// Prereq: tolerant-read released in v2026.05.20.
// Follow-up: strict-read release scheduled for v2026.06.10.
migrationBuilder.Sql("UPDATE enrollments SET source = source_legacy WHERE source IS NULL;");
migrationBuilder.DropColumn(name: "source_legacy", table: "enrollments");
```

### Step 7: Data migration

Small data migrations live inline as SQL. Larger migrations live as **idempotent
Hangfire jobs** triggered by the migration. The job sets `app.tenant_id` per tenant
before mutating data:

```csharp
foreach (var tenantId in tenantIds)
{
    await using var tx = await connection.BeginTransactionAsync(ct);

    // set_config(key, value, is_local: true) is the parameterised equivalent of
    // SET LOCAL, and it MUST run inside an explicit transaction: PostgreSQL
    // discards a SET LOCAL issued outside one (with a warning), so the UPDATE
    // below would otherwise run with no tenant context at all — which, under a
    // NULLIF-wrapped policy, means it silently updates zero rows.
    //
    // Parameterised, not interpolated. String-interpolated SQL is banned by
    // 05-database.md § Forbidden.
    await connection.ExecuteAsync(
        "SELECT set_config('app.tenant_id', @tenantId, true)",
        new { tenantId = tenantId.ToString() },
        transaction: tx);

    // No `WHERE tenant_id = current_setting(...)` clause. The connection runs as
    // learnstack_migration, which is NOBYPASSRLS against a table that is FORCE ROW
    // LEVEL SECURITY, so the policy scopes the statement to this tenant on its own;
    // restating the predicate in application SQL is a second copy of the policy that
    // can drift from the first. Note the consequence: skip the set_config above and
    // the UPDATE matches ZERO rows and still reports success.
    await connection.ExecuteAsync(
        "UPDATE enrollments SET source = 'legacy' WHERE source IS NULL",
        transaction: tx);

    await tx.CommitAsync(ct);
}
```

### Step 8: Down migration

For non-destructive changes, write the `Down(...)` method. For destructive
changes, leave the `Down(...)` empty with a comment explaining why
("Forward-only — reverting requires restoring backup").

### Step 9: Test the migration

```bash
# Build the migration's SQL without applying
dotnet ef migrations script \
  --project backend/src/Modules/<Module>/LearnStack.Modules.<Module>.Infrastructure \
  --startup-project backend/src/LearnStack.Api

# Apply against a local test DB. NOTE THE CONNECTION STRING: migrations connect as
# learnstack_migration, which OWNS every table. Running this through the API's
# runtime configuration would connect as learnstack_app and either fail with
# "permission denied for schema public" or — worse, if someone "fixes" that with a
# grant — make the runtime role the table owner, which is the arrangement
# FORCE ROW LEVEL SECURITY exists to defeat. ConnectionStrings:Migration must never
# appear in API or worker runtime configuration
# (docs/standards/05-database.md § Database roles).
dotnet ef database update \
  --project backend/src/Modules/<Module>/LearnStack.Modules.<Module>.Infrastructure \
  --startup-project backend/src/LearnStack.Api \
  --connection "$ConnectionStrings__Migration"
```

`ConnectionStrings__Migration` is the environment spelling of
`ConnectionStrings:Migration`. Packet 6 adds it to `.env.example` and exports it
from the repo-root `make migrate` target, which Standards 05 names as its only
sanctioned carrier.

Then run the architecture + integration test suite. The Testcontainers integration
tests automatically apply migrations on a fresh Postgres; a green run means the
migration is consistent.

## Validation

- `dotnet ef migrations script` output matches the expected SQL (table, indexes,
  RLS, partitions).
- `dotnet build` is green.
- `LearnStack.Tests.Architecture` is green; specifically
  `Every_TenantOwned_Table_HasRls_With_AppTenantId` and
  `Every_OrgScoped_Entity_HasOrgIdAndFilter` if applicable.
- An integration test exercises the new table / column under a tenant + org pair.
- For destructive changes: the two-step plan is documented in the PR + migration
  comment, and the prerequisite tolerant-read release exists.

## Common pitfalls

- **Forgetting RLS on a new tenant-owned table.** The architecture test catches it;
  fixing late is painful because production may already have leakable rows.
- **Wrong session variable name.** `current_setting('app.current_tenant_id')` is
  silently wrong — RLS returns zero rows because the variable is never set.
- **Editing an applied migration.** `__EFMigrationsHistory` holds only
  `MigrationId` and `ProductVersion` — there is no checksum and **nothing detects
  the edit**. On a database that already applied the migration your change is a
  silent no-op; on a fresh one it runs. The two diverge permanently. Add a new
  migration instead.
- **Mixing destructive change with non-destructive in one migration.** Split into
  separate migrations so rollback is granular.
- **One migration spanning multiple modules.** Each module owns its own migrations;
  cross-module schemas go through read-model projections, not shared tables.
- **Skipping `--locked-mode` in CI.** `dotnet restore --locked-mode` and
  `dotnet ef --no-build` keep CI deterministic.
