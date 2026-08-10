---
name: add-tenant-owned-entity
description: >
  Add an aggregate (or entity) that is scoped to a tenant — and, where applicable,
  to an organization — with all four defense-in-depth layers: `[TenantOwned]` /
  `[OrganizationScoped]` markers, EF global query filter, PostgreSQL RLS policy,
  and an architecture test. USE FOR: any new domain entity that holds tenant data
  (Course, Enrollment, Cohort, LiveSession, content rows, audit-like rows). DO NOT
  USE FOR: global tables (`Tenant`, `User`, `Plan`), Hub-mirrored projection rows,
  or pure value objects.
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

- Global tables: `Tenant`, `User`, `Plan`, `Permission` (their isolation is
  conceptual, not row-based).
- The audit aggregate (`AuditEntry`) — it inherits `Entity<TId>`, not
  `AuditableEntity<T>`, and has its own RLS rule.
- Hub-mirrored read-only projections (`platform_entitlement_cache`,
  `platform_host_to_tenant`) — they're tenant-id-keyed but written only by
  `IEntitlementProvider.RefreshAsync` and `IHostToTenantResolver`.
- Pure value objects with no own table.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Entity name | Yes | PascalCase. The aggregate root or owned entity. |
| Owning module | Yes | Determines DbContext, namespace, migration project. |
| Org-scoped? | Yes | `false` = tenant-wide; `true` = needs `OrganizationId` + org RLS. |
| Soft-deletable? | Yes | Adds `deleted_at` / `deleted_by` and an EF filter. |
| Strongly-typed id | Yes | Even simple entities use `<Name>Id : strongly-typed Guid` per [02-backend-coding.md](../../../docs/standards/02-backend-coding.md). |

## Workflow

### Step 1: Domain layer — strongly-typed id + entity

In `<Module>.Domain/<Name>/<Name>Id.cs`:

```csharp
public readonly record struct <Name>Id(Guid Value)
{
    public static <Name>Id New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}
```

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
        builder.Property(x => x.Id).HasConversion(id => id.Value, v => new <Name>Id(v));

        builder.Property(x => x.TenantId).HasConversion(...).IsRequired();
        builder.Property(x => x.OrganizationId).HasConversion(...);   // omit if not org-scoped

        builder.HasIndex(x => x.TenantId);
        builder.HasIndex(x => x.OrganizationId)
               .HasFilter("organization_id IS NOT NULL");             // partial; omit if not org-scoped

        // ... other columns ...
    }
}
```

The EF global query filter is applied **by convention**, not in this configuration:
`TenantQueryFilterConvention` (in SharedKernel) scans for `[TenantOwned]` and adds
`x => x.TenantId == _tenantContext.TenantId`. `OrganizationQueryFilterConvention`
adds `(x.OrganizationId == null || x.OrganizationId == _tenantContext.OrganizationId)`
when `[OrganizationScoped]` is present. **Do not write filters manually** — the
convention is the only legal source.

### Step 3: Migration — schema + RLS

Generate the migration:

```bash
dotnet ef migrations add Add_<Name> \
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
        created_at       timestamptz NOT NULL DEFAULT now(),
        created_by       uuid NOT NULL,
        updated_at       timestamptz NOT NULL DEFAULT now(),
        updated_by       uuid NOT NULL,
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

### Step 4: Architecture test (already covered by convention)

The conventions tests catch missing pieces automatically:

- `Every_TenantOwned_Entity_Has_TenantId` — the marker + property combo.
- `Every_TenantOwned_Table_HasRls_With_AppTenantId` — the migration's RLS policy.
- `Every_OrgScoped_Entity_HasOrgIdAndFilter` — the marker + nullable property + RLS.

If you're adding a *new* marker attribute or a *new* isolation pattern, write a new
architecture test (see [add-architecture-test](../add-architecture-test/SKILL.md)).

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
- **Manual EF query filter in `Configure(...)`.** The convention adds the filter;
  doing it twice creates an `AND` of two filters and breaks platform-admin reads.
- **No isolation test.** The architecture tests catch the RLS *policy* but not the
  semantic correctness. An explicit `TenantA_cannot_read_TenantB` test is the only
  safety net for a buggy migration.
- **Forgetting `using x.AsTenant(...)` in tests.** Without it, the connection has no
  `app.tenant_id` set and queries see nothing — which can mask a missing filter.
