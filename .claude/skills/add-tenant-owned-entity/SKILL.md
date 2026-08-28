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
  Use [Database Standards § Table classes](../../../docs/standards/05-database.md),
  not this skill.
- `platform_host_to_tenant` — the one **platform-scoped** table, with role-qualified
  per-command policies, because it is read *in order to determine* the tenant.
  Standards 05 again.
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

Together those rule out the obvious shape, which is why **Phase 02a Packet 7
owns the filters** — it lands `TenantResolverMiddleware`, the value the filter
reads, and the two rules below, together. Do not invent a filter here ahead of
it; if your entity ships before Packet 7, say so in the PR and rely on RLS,
which is live from the migration that creates the table.

`Every_TenantOwned_Entity_HasFilterAndRlsPolicy` is what will make a forgotten
filter fail, and `Every_OrgScoped_Entity_HasOrgIdAndFilter` covers the
organization term. Both are **registered and not yet implemented** — Packet 7
introduces them, so until then a forgotten filter is caught by review only. Check
the [catalogue](../../../docs/standards/21-architecture-tests-catalogue.md)
rather than assuming a net is under you.

One thing that is true whenever the filter does land: a second `HasQueryFilter`
call **replaces** the first rather than combining with it, so the tenant term,
the organization term and the soft-delete term go into one expression — and the
soft-delete term gates on `DeletedAt`, not the computed `IsDeleted` property,
which EF cannot translate.

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
  --startup-project backend/src/LearnStack.Api
```

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
`app.scope` ([05-database.md](../../../docs/standards/05-database.md)). Other names
break RLS silently.

The runtime connects as **`learnstack_app`** (`NOBYPASSRLS`, not the table owner);
migrations run as `learnstack_migration`, which owns the table. Integration tests for
this entity must connect as `learnstack_app` — a test that connects as the owner passes
against an inert policy and proves nothing.

### Step 4: Architecture test (registered, not yet implemented)

**Nothing catches a missing filter automatically today.** An earlier version of
this step said the opposite and named three rules —
`Every_TenantOwned_Entity_Has_TenantId`,
`Every_TenantOwned_Table_HasRls_With_AppTenantId` and
`Every_OrgScoped_Entity_HasOrgIdAndFilter` — of which the first two exist nowhere
in the catalogue or the test tree. A developer who reached this step was told the
work was done.

The two rules the catalogue actually registers for this surface are
`Every_TenantOwned_Entity_HasFilterAndRlsPolicy` and
`Every_OrgScoped_Entity_HasOrgIdAndFilter`, both **Registered** and owned by
Phase 02a Packet 7. What *is* implemented and will catch part of this today:
`Every_Foreign_Key_Has_A_Supporting_Index` and the schema sweeps in
`TenancySchemaTests` — row security enabled *and* forced on every table in the
catalogue, no second permissive policy for one command, snake_case identifiers,
and the exact grant matrix. Those run against the applied schema, so they cover
your migration the moment it lands.

Until Packet 7, the isolation test in Step 5 is the net, and it is the only one.
If you're adding a *new* marker attribute or a *new* isolation pattern, write a
new architecture test (see
[add-architecture-test](../add-architecture-test/SKILL.md)).

### Step 5: Integration test — the isolation pair

Every new tenant-owned entity must ship with an isolation test pair in
`LearnStack.Tests.Integration`:

```csharp
[Fact]
public async Task <Name>_TenantA_cannot_read_TenantB_data()
{
    await using var fixture = await TestFixture.CreateAsync();
    var aId = await fixture.CreateTenantAsync("A");
    var bId = await fixture.CreateTenantAsync("B");

    using (fixture.AsTenant(aId)) {
        await fixture.Create<Name>Async();
    }

    using (fixture.AsTenant(bId)) {
        var rows = await fixture.Db.<NamePlural>.ToListAsync();
        Assert.Empty(rows);
    }
}

// Add a matching org-isolation test if the entity is [OrganizationScoped].
```

See [add-integration-test](../add-integration-test/SKILL.md).

## Validation

- `dotnet build` is green.
- `dotnet ef migrations script` includes the table, both indexes, RLS-enable, and
  the policies — matching the templates above verbatim except for column names.
- `LearnStack.Tests.Architecture` passes the auto-conventions on this entity.
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
- **A query filter closing over anything but a `DbContext` instance member.**
  EF caches the model, so the value is baked in as a literal and every later
  request answers with the first tenant that built it. Under RLS that is a silent
  zero-rows outage. See Step 2 — and note there is no convention adding a filter
  for you, in either direction.
- **No isolation test.** The schema sweeps catch a missing or mis-shaped *policy*;
  they cannot catch a policy that is well-formed and wrong. An explicit
  `TenantA_cannot_read_TenantB` test — connecting as `learnstack_app`, against a
  fixture that seeds **both** tenants — is the only safety net for that. A count
  assertion against a table the fixture never populated passes whatever the policy
  says; that shipped once in Packet 6 and is the reason `SchemaFixture` fills
  every table it asserts on.
- **Forgetting `using x.AsTenant(...)` in tests.** Without it, the connection has no
  `app.tenant_id` set and queries see nothing — which can mask a missing filter.
