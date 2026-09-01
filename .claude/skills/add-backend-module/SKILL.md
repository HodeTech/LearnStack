---
name: add-backend-module
description: >
  Scaffold a new LearnStack backend module under `backend/src/Modules/<Name>/` with
  the four-package layout (`Application.Contracts`, `Application`, `Domain`,
  `Infrastructure`) plus the module's `IModule` registration, audit-coverage matrix,
  and permission catalogue. USE FOR: introducing a brand-new module (rare;
  pre-implementation we have ~15 modules already named). DO NOT USE FOR: adding an
  aggregate inside an existing module (use `add-tenant-owned-entity` /
  `add-mediatr-handler`), adding domain-specific code to any module (forbidden by
  ADR-0018 — use tenant customization data), or naming the module after a domain
  term (CEFR, asana, kyu/dan — all forbidden).
---

# Scaffolding a new backend module

## Purpose

Stand up a new modular-monolith module that complies with
[03-module-boundaries.md](../../../docs/architecture/03-module-boundaries.md) and
[01-architecture-standards.md](../../../docs/standards/01-architecture-standards.md)
out of the gate: four packages, the right project references, an `IModule`
registration, a permission catalogue, an audit matrix, and a place for the module's
EF DbContext.

## When to use

- A new platform capability genuinely warrants its own module (rare).
- The capability fits one of the named modules in
  [03-module-boundaries.md § Module Map](../../../docs/architecture/03-module-boundaries.md);
  use that name, not a new one.

## When not to use

- The capability is an aggregate inside an existing module. Add the aggregate, not a
  new module.
- The capability is domain-specific (English-learning vocabulary, yoga asanas, …).
  Express it as tenant customization data per
  [ADR-0018](../../../docs/decisions/0018-tenant-driven-customization-model.md).
- You're tempted to name the module `Verticals.*`. Forbidden. Architecture test
  `No_Source_Folder_Named_Verticals` will fail.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Module name | Yes | One of the named modules in 03-module-boundaries. PascalCase. |
| Owns | Yes | Aggregates this module owns. |
| Cross-module dependencies | Yes | Other modules' `Application.Contracts` it will reference. |
| Provider adapters | No | External-boundary interfaces it wraps (if any). |

## Workflow

### Step 1: Confirm the name

Open
[03-module-boundaries.md § Backend Modules](../../../docs/architecture/03-module-boundaries.md).
Your module must appear in that list. If not, **stop**; either fit the work into an
existing module or open an ADR to add a new module to the boundary map.

### Step 2: Create the four projects

```
backend/src/Modules/<Name>/
  LearnStack.Modules.<Name>.Application.Contracts/
    LearnStack.Modules.<Name>.Application.Contracts.csproj
  LearnStack.Modules.<Name>.Application/
    LearnStack.Modules.<Name>.Application.csproj
  LearnStack.Modules.<Name>.Domain/
    LearnStack.Modules.<Name>.Domain.csproj
  LearnStack.Modules.<Name>.Infrastructure/
    LearnStack.Modules.<Name>.Infrastructure.csproj
```

Project references follow the strict graph in
[01-architecture-standards.md § Dependency Direction](../../../docs/standards/01-architecture-standards.md):

```mermaid
flowchart LR
  Domain --> SharedKernel
  Application --> Domain
  Application --> Application.Contracts
  Application -. depends on .-> OtherModule.Application.Contracts
  Infrastructure --> Application
  Infrastructure --> ProviderSDKs
  Application.Contracts --> SharedKernel
```

Forbidden references (architecture test will catch them):

- Domain → Application / Infrastructure
- Application → Infrastructure
- Module A → Module B.Domain
- Module A → Module B.Infrastructure

### Step 3: Author the `IModule` registration

> **The shape, not today's API.** `IModule`, `AddMediatRFromModule`,
> `IPermissionRegistry`, `IAuditCatalog` and the `modules.Add(...)` call site do
> not exist yet — the registration seam lands with **Phase 02a Packet 9**, which
> is what ships `IAuditStore` and the audit catalogue, and the permission
> registry with it. `AddModuleDbContext<T>` **does** exist and the warning below
> it is live today. Until Packet 9, a module registers its handlers and
> validators from the composition root directly.

In `LearnStack.Modules.<Name>.Application/<Name>Module.cs`:

```csharp
public sealed class <Name>Module : IModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // The DbContext is NOT registered here — see below. Application may not
        // reference Infrastructure, and the registration helper lives there.
        services.AddMediatRFromModule(typeof(<Name>Module).Assembly);
        services.AddValidatorsFromAssembly(typeof(<Name>Module).Assembly);

        // Provider adapters (composition root chooses concrete adapter):
        // services.AddScoped<I<Name>Provider, <Name>Provider>();
    }

    public void RegisterPermissions(IPermissionRegistry registry)
    {
        // see add-permission skill
    }

    public void RegisterAuditCoverage(IAuditCatalog catalog)
    {
        // see add-audit-coverage skill
    }
}
```

Call this from the composition root (`LearnStack.Api/Program.cs`):

```csharp
modules.Add(new <Name>Module());
```

**The `DbContext` registration is a composition-root concern, not a module one.**
`AddModuleDbContext<T>` lives in `LearnStack.Infrastructure.Persistence`, and
`Application` may not reference `Infrastructure` — so the call belongs beside the
others in `AddLearnStackPersistence`
(`LearnStack.Api/Composition/PersistenceCompositionExtensions.cs`):

```csharp
services.AddModuleDbContext<<Name>DbContext>();
```

Not `AddDbContext(o => o.UseNpgsql(connectionString))`. A context that opens its
own connection never saw the `SET LOCAL` the ambient transaction carries, so every
read through it returns **zero rows** under the corrected RLS policy — silently.
Per [ADR-0040](../../../docs/decisions/0040-ambient-unit-of-work.md) every module
context is built on the connection `IUnitOfWork` owns, and
`Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork` fails the build if you reach
for the EF default instead — from both sides: the registration, and the fact that
only three files under `backend/src` may mention `UseNpgsql` at all.

### Step 4: Module DbContext

In `LearnStack.Modules.<Name>.Infrastructure/Persistence/<Name>DbContext.cs`:

```csharp
// ONE constructor parameter. `ModuleDbContextRegistration` builds every module
// context with `Activator.CreateInstance(typeof(TContext), options)`, so a second
// parameter throws `MissingMethodException` on first resolution — and
// `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork` forbids registering the
// context any other way. This is the shape the one shipped context carries.
public sealed class <Name>DbContext(
    DbContextOptions<<Name>DbContext> options, ITenantContextAccessor accessor)
    : TenantScopedDbContext(options, accessor)
{
    // The base owns the two members the filters close over and applies one to
    // every entity implementing ITenantOwned / IOrganizationScoped. Do not write
    // a filter here, and never from an IEntityTypeConfiguration: a configuration
    // reached by ApplyConfigurationsFromAssembly cannot close over the context
    // instance, and a filter whose closure root is anything else is constant-
    // folded into EF's cached model as a SQL literal. There is no
    // TenantQueryFilterConvention either; that type has never existed.
    //
    // The accessor rather than an injected ITenantContext: that contract is
    // registered transient and resolved from this same accessor, so a context
    // holding one freezes whatever the accessor held at construction.

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(<Name>DbContext).Assembly);

        // AFTER the configurations, so every entity type is in the model when the
        // base sweeps it. Forgetting this call loses every filter, silently.
        base.OnModelCreating(modelBuilder);
    }
}
```

**How the instance member gets populated is Packet 7's choice**, and it is one of
two: switch `ModuleDbContextRegistration` to
`ActivatorUtilities.CreateInstance(provider, typeof(TContext), options)`, or read
the accessor off the application service provider the registrar already passes to
`UseApplicationServiceProvider`. Prefer the singleton `ITenantContextAccessor`
over an `ITenantContext` snapshot: EF parameterises `_accessor.Current` per query,
and it avoids baking in an `UnresolvedTenantContext` whose `TenantId` throws.

Per [05-database.md](../../../docs/standards/05-database.md), one `DbContext` per
module — not one global.

`ApplyConfigurationsFromAssembly` also **silently skips** a configuration class
that has constructor arguments — no exception, no log, the entity mapped by
convention with no filter at all — so `<Name>Configuration(ITenantContext ctx)`
disappears rather than failing. That is the second reason the filter is not a
configuration's job. See
[add-tenant-owned-entity Step 2](../add-tenant-owned-entity/SKILL.md).

### Step 5: Architecture test fixture

The dependency-direction and cross-module rules live in
`backend/tests/LearnStack.Tests.Architecture/ModuleDependencyTests.cs`. Both are
`[Theory]`-driven from the literal `ModuleNames` array in that file, not scanned —
**add `<Name>` to that array**. Until you do, the new module's `Domain` assembly is
never inspected and both rules pass vacuously. What is still owed is
`Every_Module_Has_An_AuditCoverage_Matrix`, registered in
[21-architecture-tests-catalogue.md](../../../docs/standards/21-architecture-tests-catalogue.md)
and **awaiting backfill in Packet 9** with the audit catalogue it reads. Until it
exists, the two matrix files below are a review check rather than a test.

### Step 6: Module spec files

Per [13-documentation.md § Per-Module Specifications](../../../docs/standards/13-documentation.md),
create the spec files under `docs/modules/<name>/`:

- `README.md` with an `## Overview` section — what the module owns and does not
  own. (The standard names the *section*, not a filename; the one shipped spec,
  `docs/modules/tenancy/README.md`, is the model.)
- `audit.md` — audit-coverage matrix (use the
  [add-audit-coverage](../add-audit-coverage/SKILL.md) skill).
- `permissions.md` — permission matrix (use the
  [add-permission](../add-permission/SKILL.md) skill).
- ER diagram, state diagrams, integration-event catalogue per the standard.

### Step 7: Wire migrations

Module migrations live with the module:

> `dotnet ef migrations add` needs `ConnectionStrings__Migration` exported into
> the process environment first — the design-time factory reads it and nothing
> else, and `--connection` does not satisfy it. See
> [add-ef-migration Step 1](../add-ef-migration/SKILL.md) for the one-line export;
> `make migrate` does the same thing for applying them.

```bash
# INTENT ONLY, snake_case: EF prepends the UTC timestamp, producing the
# <UTC_yyyyMMddHHmmss>_<intent> filename Standards 05 specifies.
dotnet ef migrations add create_<name>_schema \
  --project backend/src/Modules/<Name>/LearnStack.Modules.<Name>.Infrastructure \
  --startup-project backend/src/LearnStack.Api \
  --output-dir Persistence/Migrations
```

See [add-ef-migration](../add-ef-migration/SKILL.md) for migration conventions
(RLS, partitioning, naming).

## Validation

- `dotnet build` succeeds for all four projects.
- `LearnStack.Tests.Architecture` is green; specifically the two rules that
  actually run, `ModuleDomain_DoesNotDependOn_OtherModuleDomain` and
  `ModuleDomain_DoesNotDependOn_AnyApplicationOrInfrastructure`, for `<Name>`.
- `dotnet ef migrations script` for the module shows the expected baseline schema.
- The module appears in [03-module-boundaries.md](../../../docs/architecture/03-module-boundaries.md)
  module map and in [docs/glossary.md](../../../docs/glossary.md) if it owns any
  glossary-worthy terms.

## Common pitfalls

- **Naming the module after a domain.** Forbidden by ADR-0018. Use the generic name
  from the boundary map.
- **Putting EF entities in `Application`.** They live in `Domain`. EF configurations
  live in `Infrastructure`.
- **Skipping the contracts package.** Other modules must reference the contracts,
  never the full application — the contracts package is what makes service
  extraction reversible.
- **Cross-module EF navigation properties.** Use id references only; cross-module
  reads go through repository contracts or read-model projections.
- **Forgetting the `IModule` registration in the composition root.** The module
  builds but no handlers run; takes hours to diagnose.
- **Missing `docs/modules/<name>/` spec files.** Nothing fails.
  `Every_Module_Has_An_AuditCoverage_Matrix` is Registered against Packet 9 and
  there is no permission-matrix rule at all, so review is the only gate until then.
