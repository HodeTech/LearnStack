---
name: add-tenant-owned-entity
description: >
  Add an aggregate (or entity) that is scoped to a tenant — and, where applicable,
  to an organization — with all four defense-in-depth layers: `[TenantOwned]` /
  `[OrganizationScoped]` markers, EF global query filter, PostgreSQL RLS policy,
  and an architecture test. USE FOR: any new domain entity that holds tenant data
  (Course, Enrollment, Cohort, LiveSession, content rows, audit-like rows). DO NOT
  USE FOR: the two tables with their own RLS class — `tenants` (tenant-owned,
  self-keyed) and `platform_host_to_tenant` (platform-scoped), both governed by
  Database Standards § Table classes — or pure value objects. Note that
  `platform_entitlement_cache` IS an ordinary tenant-owned table despite its name.
---

# Adding a tenant-owned / org-scoped entity

## Purpose

Wire every new tenant-scoped entity into the four-layer isolation model from
day one ([ADR-0003 Amendment 1](../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md),
[ADR-0017](../../../docs/decisions/0017-tenant-organization-hierarchy.md)):
context + EF filter + RLS + architecture test. Forgetting any layer creates a silent
cross-tenant leak; this skill is the prevention.

## When to use

- Adding any new EF entity that should be visible only to its owning tenant.
- Adding an entity whose visibility is further constrained to a single organization
  inside the tenant (`Course` with `OrganizationId`, etc.).
- Promoting an existing global table to tenant-owned (rare; treat carefully).

## When not to use

- `tenants` — **not** because its isolation is conceptual. ADR-0003 Amendment 3
  gives it a policy like every other table; it is the **tenant-owned, self-keyed**
  class, whose predicate keys on `id` because the primary key *is* the tenant id.
  It carries no marker-driven `TenantId` filter, because it has no `tenant_id`
  column to filter on. Use
  [Database Standards § Table classes](../../../docs/standards/05-database.md),
  not this skill.
- `platform_host_to_tenant` — the one **platform-scoped** table, with role-qualified
  per-command policies, because it is read *in order to determine* the tenant. It
  takes **no `[TenantOwned]` marker at all**. Standards 05 again.
- `users`, `plans`, `permissions` — owned by phases that have not written their
  schema yet. Nothing here says they are exempt from row security.
- The audit aggregate (`AuditEntry`) — it inherits `Entity<TId>`, not
  `AuditableEntity<T>`, and has its own RLS rule.

`platform_entitlement_cache` is **not** on this list. Its name suggests
platform-scoped and it is not: every read resolves the tenant from `ITenantContext`
first and every write arrives on `PUT /api/internal/tenants/{id}/entitlements`, so
both directions have a tenant and it keeps the ordinary tenant-owned template. An
earlier version of this file exempted it, which would have handed the application
role a table-wide read of every tenant's plan.
- Pure value objects with no own table.

The marker's scope is decided by **table class**, not by whether a `TenantId`
property happens to be there — the note under
`Every_TenantOwned_Entity_HasFilterAndRlsPolicy` in
[the catalogue](../../../docs/standards/21-architecture-tests-catalogue.md) is the
authority.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Entity name | Yes | PascalCase. The aggregate root or owned entity. |
| Owning module | Yes | Determines DbContext, namespace, migration project. |
| Org-scoped? | Yes | `false` = tenant-wide; `true` = needs `OrganizationId` + org RLS. |
| Soft-deletable? | Yes | Decides the **query filter**, not the columns: `AuditableEntity<TId>` implements `ISoftDelete` unconditionally, so `deleted_at` / `deleted_by` are on every derived table either way ([Database Standards § Audit Columns](../../../docs/standards/05-database.md)). |
| Strongly-typed id | Yes | Even simple entities use `<Name>Id : strongly-typed Guid` per [02-backend-coding.md](../../../docs/standards/02-backend-coding.md). |

## Workflow

### Step 1: Domain layer — strongly-typed id + entity

In `<Module>.Domain/<Name>/<Name>Id.cs`:

```csharp
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct <Name>Id : IStronglyTypedId<Guid>
{
}
```

Vogen generates the body — `From`, `TryFrom`, `Value`, `IsInitialized()`, the
EF Core value converter, the JSON converter and the `TypeConverter`. Do not
hand-roll the struct: a hand-written `record struct <Name>Id(Guid Value)` has
no `IsInitialized()`, so `Entity<TId>` cannot satisfy its constraint, and it
silently permits the `default(<Name>Id)` state that
[ADR-0023](../../../docs/decisions/0023-strongly-typed-id-source-generator.md)
exists to forbid. New values are minted through the injected `IGuidFactory`
(`<Name>Id.From(guidFactory.NewUuidV7())`) rather than a static `New()`, so
tests can pin them — see
[Backend Coding Standards § Time](../../../docs/standards/02-backend-coding.md).

In `<Module>.Domain/<Name>/<Name>.cs`:

```csharp
[TenantOwned]                  // mandatory
[OrganizationScoped]           // only if org-scoped (skip otherwise)
public sealed class <Name> : AuditableEntity<<Name>Id>
{
    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }   // nullable when row may be tenant-wide

    // ... domain fields ...

    private <Name>() { /* EF */ }

    public static <Name> Create(TenantId tenantId, OrganizationId? organizationId, ...)
    {
        // factory + invariants + raise domain event if needed
    }
}
```

Rules:

- `TenantId` is always set at construction; nullable is not allowed.
- `OrganizationId` is nullable only for entities that may be tenant-wide; if the
  entity is **always** org-scoped, make the property non-nullable.
- Use `AuditableEntity<<Name>Id>` for mutable aggregates. **Never** `Entity<TId>`
  unless the aggregate is append-only (e.g. `AuditEntry`).
- Domain events for state changes; don't write to other aggregates from this one.

### Step 2: EF configuration — filter + indexes

In `<Module>.Infrastructure/Persistence/Configurations/<Name>Configuration.cs`:

```csharp
public sealed class <Name>Configuration : IEntityTypeConfiguration<<Name>>
{
    public void Configure(EntityTypeBuilder<<Name>> builder)
    {
        builder.ToTable("<name_plural>");

        builder.HasKey(x => x.Id);
        // Vogen generates a private constructor: `new <Name>Id(v)` does not compile.
        // `From` is the factory, and it is what runs the validation.
        builder.Property(x => x.Id).HasConversion(id => id.Value, v => <Name>Id.From(v));

        builder.Property(x => x.TenantId).HasConversion(...).IsRequired();
        builder.Property(x => x.OrganizationId).HasConversion(...);   // omit if not org-scoped

        builder.HasIndex(x => x.TenantId);
        builder.HasIndex(x => x.OrganizationId)
               .HasFilter("organization_id IS NOT NULL");             // partial; omit if not org-scoped

        // ... other columns ...
    }
}
```

**The query filter is not written here, and it is not written by a convention
either.** There is no `TenantQueryFilterConvention` and no
`OrganizationQueryFilterConvention` — neither type has ever existed in this
repository, and an earlier version of this file described both as the only legal
source, which sent implementers looking for something to import. A later version
over-corrected and put a snippet in this step; that snippet was **wrong in a way
that cannot be seen locally**, and the two facts behind it are what you need:

- **A query filter's closure root must be a `DbContext` instance member.** EF
  builds the model once and caches it. Anything else the lambda closes over —
  an `ITenantContext` injected into the configuration, a local, a field on the
  configuration — is constant-folded into the cached model as a SQL literal, so
  every later request answers with whichever tenant happened to build the model
  first. Measured in this repository: two contexts, two tenants, and request B
  emitted `WHERE t."TenantId" = '1111…'` — tenant A's id, baked in. Under RLS
  that is a silent zero-rows outage rather than a leak, which is worse to
  diagnose, not better.
- **`ApplyConfigurationsFromAssembly` silently skips a configuration that has
  constructor arguments.** No exception, no log: the entity is simply mapped by
  convention, with no filter at all. `TenancyDbContext` uses that call, so a
  `ThingConfiguration(ITenantContext ctx)` disappears rather than failing.

Together those rule out the obvious shape. **Since Packet 7 step 3 you do not
write a filter at all** — you declare the entity's scope and the seam applies
one:

1. Implement `ITenantOwned` (or `IOrganizationScoped`, which extends it) from
   `LearnStack.SharedKernel.Persistence`.
2. Mark the class `[TenantOwned]`, adding `[OrganizationScoped]` when it carries
   a nullable `OrganizationId`.
3. Make sure the module's `DbContext` derives from `TenantScopedDbContext`. Its
   `OnModelCreating` sweeps the model and applies the filter to every entity
   implementing those interfaces, closing over the **context instance member**
   that is the whole point of the mechanism.

Two entities take neither treatment, and both are table classes rather than
oversights: the tenant-owned **self-keyed** class carries
`[TenantOwned(SelfKeyed = true)]` and is filtered on its own `Id`, and a
**platform-scoped** table carries no marker at all
([Database Standards § Table classes](../../../docs/standards/05-database.md)).

`Every_TenantOwned_Entity_HasFilterAndRlsPolicy` and
`Every_OrgScoped_Entity_HasOrgIdAndFilter` are **implemented** as of that step and
will fail the build for a marked entity with no filter, no tenant key, or a
migration missing `ENABLE` + `FORCE` + one permissive policy with both clauses.
They cannot catch a **missing marker** — a marker-gated rule iterates what it
finds — so that one is still on you and on review.

One thing that stays true: a second `HasQueryFilter` call **replaces** the first
rather than combining with it, so a soft-delete term has to go into the same
expression as the tenant term rather than beside it — and it gates on
`DeletedAt`, not the computed `IsDeleted` property, which EF cannot translate.

### Step 3: Migration — schema + RLS

Generate the migration:

> `dotnet ef migrations add` needs `ConnectionStrings__Migration` exported into
> the process environment first — the design-time factory reads it and nothing
> else, and `--connection` does not satisfy it. See
> [add-ef-migration Step 1](../add-ef-migration/SKILL.md) for the one-line export;
> `make migrate` does the same thing for applying them.

```bash
# EF prepends its own UTC timestamp, so pass the INTENT only. Passing a timestamp
# too produces 20260827120000_20260827120000_add_<name>.cs.
dotnet ef migrations add add_<name> \
  --project backend/src/Modules/<Module>/LearnStack.Modules.<Module>.Infrastructure \
  --startup-project backend/src/LearnStack.Api \
  --output-dir Persistence/Migrations
```

`--output-dir` is not optional, and this is the skill that needs it most: EF
defaults the output to `Migrations/` when the project has no sibling migration to
reuse, which is six of the seven module assemblies today. `make migrate`,
`backend/.editorconfig` and `Migrate_Target_Covers_Every_Migration_Chain` all key
on `Persistence/Migrations` — a chain landing one directory up is skipped by the
Makefile loop in silence and is invisible to the architecture test written for
exactly that hole, while the Testcontainers fixtures call `MigrateAsync()` directly
and keep the suite green.

Edit the generated migration to add the table and **one** RLS policy.

> **The canonical template lives in
> [05-database.md § Tenant-Owned and Organization-Scoped Tables](../../../docs/standards/05-database.md),
> and this skill does not mirror it.** Open that file and copy the block from there.
> Mirroring it here would be the same mistake that produced four divergent copies before
> 2026-08-08, one of which leaked every tenant-wide row across tenants
> ([ADR-0003 Amendment 3](../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md)) —
> and a disclaimer saying "the standard wins if they disagree" does not stop the drift,
> it only predicts it.

```csharp
migrationBuilder.Sql("""
    CREATE TABLE <name_plural> (
        id               uuid PRIMARY KEY,
        tenant_id        uuid NOT NULL,
        organization_id  uuid NULL,                  -- omit if not org-scoped
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
        -- Looks redundant next to the primary key; it is not. See the note below.
        CONSTRAINT ux_<name_plural>_tenant_id_id UNIQUE (tenant_id, id)
    );

    -- Every foreign key from this table to another tenant-owned table is
    -- COMPOSITE on tenant_id:
    --
    --     CONSTRAINT fk_<name_plural>_<parent>
    --         FOREIGN KEY (tenant_id, <parent>_id)
    --         REFERENCES <parents> (tenant_id, id)
    --
    -- PostgreSQL evaluates referential integrity as a security-restricted
    -- operation on behalf of the table owner, and RI checks are NOT subject to
    -- Row Level Security. A single-column FK therefore lets a row in tenant A
    -- reference a row in tenant B: the child's WITH CHECK passes because its
    -- tenant_id is A's, and the FK check passes because it can see B's row. The
    -- result is a permanent cross-tenant reference that no policy ever sees,
    -- because no policy ran.

    -- One composite index, deliberately NOT partial: the policy's
    -- `organization_id IS NULL` branch matches every tenant-wide row and a b-tree
    -- indexes NULLs, so the non-partial form serves both branches. No standalone
    -- index on tenant_id — the UNIQUE constraints above already lead with it.
    -- Drop organization_id from the index if the table is not org-scoped.
    CREATE INDEX ix_<name_plural>_tenant_id_organization_id
        ON <name_plural> (tenant_id, organization_id);

    -- ─────────────────────────────────────────────────────────────────────────
    -- RLS: DO NOT WRITE THE POLICY FROM MEMORY, AND DO NOT COPY IT HERE.
    --
    -- Open docs/standards/05-database.md § Tenant-Owned and Organization-Scoped
    -- Tables and copy the canonical block into this migration NOW, substituting
    -- <name_plural>. That file is the only place the template exists; this skill
    -- deliberately does not carry a second instance of it.
    --
    -- The template that preceded 2026-08-08 lived in four documents and was wrong
    -- in all four — two PERMISSIVE policies, which PostgreSQL combines with OR, so
    -- every tenant-wide row was visible across tenants. It was corrected once, in
    -- one file. A copy here is how that recurs (ADR-0003 Amendment 3).
    --
    -- What you are copying, so you can tell if you got it wrong:
    --   * ENABLE *and* FORCE ROW LEVEL SECURITY  (without FORCE the owner bypasses)
    --   * exactly ONE permissive policy, tenant AND organization in one predicate
    --   * an explicit WITH CHECK, without the app.scope='tenant' read hatch
    --   * two AS RESTRICTIVE guards, FOR UPDATE and FOR DELETE, when org-scoped
    --   * NULLIF(current_setting(...), '') on every GUC read
    -- Drop the organization terms entirely if the table is not org-scoped.
    -- ─────────────────────────────────────────────────────────────────────────
    """);
```

Five properties of the block you just copied are load-bearing. Check each one against
what you pasted — a reviewer will:

- **`FORCE ROW LEVEL SECURITY`** — without it the owner bypasses the policy and the
  whole layer is inert while every structural test stays green.
- **One policy with an `AND`-ed predicate** — splitting the tenant and organization
  terms into two policies inverts the meaning from AND to OR.
- **`WITH CHECK`** — `USING` governs reads; without `WITH CHECK` a write can place a
  row in another tenant. Note the `app.scope = 'tenant'` term is deliberately absent
  from `WITH CHECK`: tenant-scope reporting may *read* across organizations, but
  nothing may *write* outside its own. `WITH CHECK` is not sufficient on its own for
  that guarantee — PostgreSQL has no `WITH CHECK` for `DELETE`, and `USING` is also
  what selects the rows an `UPDATE` may target — which is why the two `AS RESTRICTIVE`
  guards above are part of the template and not an optional extra.
- **The composite `UNIQUE (tenant_id, id)` and the composite foreign keys** — referential
  integrity is checked on behalf of the table owner and bypasses RLS entirely, so a
  single-column FK is a cross-tenant reference waiting to happen, invisible to every
  policy. See
  [05-database.md § Foreign keys between tenant-owned tables](../../../docs/standards/05-database.md).

Always call `current_setting` with the second argument `true`. Without it an unset
context raises inside a pooled connection instead of simply filtering the row out.

The session-variable names are **canonical**: `app.tenant_id`, `app.organization_id`,
`app.scope` and `app.resolving_host`
([05-database.md](../../../docs/standards/05-database.md)). Other names break RLS
silently. Note that **nothing sets `app.scope`** — `ITenantContext` exposes no scope
member, the flag derives from the actor's role, and roles arrive in
[Phase 02b](../../../docs/roadmap/phase-02b-events-auth.md), which is the earliest
phase that can own the carrier. The cross-organization read hatch is therefore
unreachable at runtime, which is the correct default; write the term into the policy
anyway, because a test can set the variable and the two `AS RESTRICTIVE` guards need
it to mean anything.

The runtime connects as **`learnstack_app`** (`NOBYPASSRLS`, not the table owner);
migrations run as `learnstack_migration`, which owns the table. Integration tests for
this entity must connect as `learnstack_app` — a test that connects as the owner passes
against an inert policy and proves nothing.

### Step 4: Architecture test (implemented for Tenancy; Packet 10 closes it)

`Every_TenantOwned_Entity_HasFilterAndRlsPolicy` and
`Every_OrgScoped_Entity_HasOrgIdAndFilter` are **implemented** as of Phase 02a
Packet 7 step 3, in `TenantScopingTests`. For a **marked** entity they fail the
build on a missing tenant key, a missing or non-narrowing query filter, a mapped
`organization_id` that is not nullable, or a migration lacking `ENABLE` + `FORCE`
plus exactly one permissive policy with both a `USING` and a `WITH CHECK` clause —
and, for an organization-scoped table, either `AS RESTRICTIVE` write guard.

Two gaps remain, and both are yours to close by hand:

- **A marker-gated rule cannot catch a missing marker.** It iterates what it
  finds. An entity you forget to mark is invisible to both rules, and the
  isolation test in Step 5 is the net for it.
- **The reflection scope is the Tenancy domain assembly** until Packet 10 widens
  it across every module.

Also live against your migration: `Every_Foreign_Key_Has_A_Supporting_Index` and
the schema sweeps in `TenancySchemaTests` — row security enabled *and* forced,
no second permissive policy for one command, snake_case identifiers, and the exact
grant matrix. Those run against the applied schema.

If you're adding a *new* marker attribute or a *new* isolation pattern, write a
new architecture test (see
[add-architecture-test](../add-architecture-test/SKILL.md)).

### Step 5: Integration test — the isolation pair

Every new tenant-owned entity must ship with an isolation test pair in
`LearnStack.Tests.Integration`:

```csharp
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class <Name>IsolationTests(SchemaFixture schema)
{
    [Fact]
    public async Task <Name>_TenantA_cannot_read_TenantB_data()
    {
        // AppConnectionString — learnstack_app, NOBYPASSRLS, not the owner. A test
        // that connects as learnstack_migration, learnstack_platform or
        // learnstack_outbox_admin passes with every policy inert and proves nothing.
        await using var connection = await PostgresFixture.OpenAsync(
            schema.Postgres.AppConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();

        // The transaction's first statement. set_config(..., true) is
        // transaction-local, so outside one it is discarded before the read.
        await SchemaQueries.SetTenantAsync(connection, transaction, SchemaFixture.TenantA);

        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM <name_plural> WHERE tenant_id = @other",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        read.Parameters.AddWithValue("other", SchemaFixture.TenantB);

        (await read.ExecuteScalarAsync()).Should().Be(0L);
    }
}

// Add a matching org-isolation test if the entity is [OrganizationScoped].
```

There is no `TestFixture`, no `CreateTenantAsync` and no `AsTenant(...)` helper —
an earlier version of this file used all three. The shipped fixtures are
`PostgresFixture` (container + the four roles) and `SchemaFixture` (both migration
chains, every table seeded for two tenants), shared with
`[Collection(SharedSchema.Name)]`. The fixture must seed **both** tenants: a count
of zero against a table nothing populated passes whatever the policy says.

See [add-integration-test](../add-integration-test/SKILL.md).

## Validation

- `dotnet build` is green.
- `dotnet ef migrations script` includes the table, both indexes, RLS-enable, and
  the policies — the policies matching the canonical block in
  [05-database.md § Tenant-Owned and Organization-Scoped Tables](../../../docs/standards/05-database.md)
  verbatim, with only `<name_plural>` substituted.
- `LearnStack.Tests.Architecture` is green. Note that no rule covers this entity's
  filter until Packet 7 lands the two in Step 4; the schema sweeps in
  `TenancySchemaTests` are what run against your migration today.
- `LearnStack.Tests.Integration` includes the cross-tenant test (and cross-org if
  applicable).
- Glossary updated if the entity name is a new domain term.

## Common pitfalls

- **Wrong session var name.** `app.current_tenant_id` looks similar to
  `app.tenant_id` but breaks isolation silently — RLS returns zero rows, no test
  fails unless you test for non-empty.
- **Org-scoped without nullable column.** Forces every row to be org-bound, breaking
  tenant-wide rows. The default is `nullable + tenant-wide allowed`.
- **`Entity<TId>` instead of `AuditableEntity<<Name>Id>`.** You lose
  `created_at` / `updated_at` automation. Only the audit aggregate uses `Entity<TId>`.
- **Writing a query filter at all.** Since Packet 7 step 3 the module's
  `TenantScopedDbContext` base applies one for every entity implementing
  `ITenantOwned` / `IOrganizationScoped`; your job is the interface and the marker.
  A hand-written one closing over anything but a `DbContext` instance member is
  baked into EF's cached model as a literal, and every later request carries
  whichever tenant built the model first — under RLS a zero-rows outage rather
  than a leak, which is harder to diagnose, not safer. There is no convention
  adding a filter for you either, in either direction.
- **No isolation test.** The schema sweeps catch a missing or mis-shaped *policy*;
  they cannot catch a policy that is well-formed and wrong. An explicit
  `TenantA_cannot_read_TenantB` test — connecting as `learnstack_app`, against a
  fixture that seeds **both** tenants — is the only safety net for that. A count
  assertion against a table the fixture never populated passes whatever the policy
  says; that shipped once in Packet 6 and is the reason `SchemaFixture` fills
  every table it asserts on.
- **Forgetting `SchemaQueries.SetTenantAsync` in tests.** Without it the transaction
  has no `app.tenant_id` and every query sees nothing — which can mask a missing
  filter. Issuing it outside a transaction has the same effect, because
  `set_config(..., true)` is transaction-local.
