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

Edit the generated migration to add **both** RLS policies:

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
        row_version      bigint NOT NULL DEFAULT 0
    );

    CREATE INDEX ix_<name_plural>_tenant_id ON <name_plural> (tenant_id);
    CREATE INDEX ix_<name_plural>_organization_id ON <name_plural> (organization_id)
        WHERE organization_id IS NOT NULL;   -- omit if not org-scoped

    ALTER TABLE <name_plural> ENABLE ROW LEVEL SECURITY;

    CREATE POLICY <name_plural>_tenant_isolation ON <name_plural>
        USING (tenant_id = current_setting('app.tenant_id')::uuid);

    -- org policy: omit if not org-scoped
    CREATE POLICY <name_plural>_organization_isolation ON <name_plural>
        USING (
            organization_id IS NULL
            OR organization_id = current_setting('app.organization_id', true)::uuid
        );
    """);
```

The session-variable names are **canonical**: `app.tenant_id`, `app.organization_id`
([05-database.md](../../../docs/standards/05-database.md)). Other names break RLS.

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
