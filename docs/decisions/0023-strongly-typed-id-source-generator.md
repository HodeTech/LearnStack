# ADR-0023: Strongly-Typed ID Source Generator — Vogen

## Status

Accepted

**Date:** 2026-05-20
**Deciders:** @platform

## Decision Drivers

- **Strongly-typed IDs are a hard requirement.**
  [Standards 02 § Strongly-Typed Identifiers](../standards/02-backend-coding.md)
  forbids raw `Guid` on the public surface of any entity. Every aggregate root and
  cross-module reference uses `record struct CourseId(Guid Value) : IStronglyTypedId<Guid>`
  shape; the shape is fixed, the *emitter* is what this ADR picks.
- **The emitter has to produce four artefacts per ID type**, not just the struct:
  EF Core value converter, `JsonConverter`, ASP.NET Core minimal-API model binder, and
  OpenAPI schema mapping. Hand-rolling four boilerplate files per ID across ~15 modules
  with multiple aggregates each is a maintenance crime.
- **Value objects share the same emitter shape.** Standards 02 calls out `Email`, `Slug`,
  `LocaleCode` as value objects with invariants. An emitter that handles *both* IDs and
  value objects with the same pattern is a leverage point; one that handles only IDs
  leaves a second hand-rolled track for value objects.
- **Pre-implementation phase is the cheapest commit window.** Phase 02a's Packet 2
  introduces the first `Entity<TId>` and `AuditableEntity<T>` bases, which depend on
  the emitter at compile time. Choosing now means every subsequent packet wires the
  same generator from the first commit; choosing later means a forced re-emit pass
  across every module that already landed an ID type.
- **Provider lock-in budget is zero on the core paths.** ADR-0014 puts every external
  port behind an interface (`IEventBus`, `ICacheService`, `IStorageProvider`, …). The
  ID emitter is a compile-time build dependency, not a runtime port — it cannot sit
  behind an interface — so the chosen library has to be one we are comfortable depending
  on for the platform's lifetime, or removable with a one-shot find-and-replace if it
  goes unmaintained.
- **PostgreSQL 18 native `uuidv7()` is available** ([ADR-0031](0031-postgresql-major-version.md)).
  The emitter has to play well with DB-side `DEFAULT uuidv7()` as well as
  app-side `Guid.CreateVersion7()` (.NET 9+) — both code paths exist in the codebase
  (DB-side for high-volume audit / outbox tables; app-side for aggregates that need
  the ID before flush).

## Considered Options

1. **Vogen** (chosen). MIT, source generator authored by Steven Giesel; emits a `record
   struct` value object (works for both IDs and richer value objects), plus
   pre-baked `EFCoreValueConverter`, `SystemTextJsonConverter`, `TypeConverter`,
   ASP.NET Core minimal-API model binder, Dapper handler, `INumber<T>`/`IParsable<T>`
   conformance, OpenAPI schema customizer. Annotation-based opt-in:
   `[ValueObject<Guid>(...)]` on a partial record struct.
2. **StronglyTypedId** (rejected). MIT, Andrew Lock's library; the original entrant
   in this space. Focused exclusively on IDs (no broader value-object support),
   smaller surface, older codebase. Active maintenance but the project's velocity
   has slowed since 2024.
3. **Custom in-house emitter** (rejected). A Roslyn source generator in
   `backend/analyzers/` emitting the four-artefact set per `[Id]`-marked struct.
   Maximum control, zero third-party trust, full alignment with LearnStack's
   own conventions.

## Decision

LearnStack uses **Vogen** as the source generator for strongly-typed IDs **and**
value objects.

- Every aggregate's ID type is declared as a partial `record struct`
  annotated with `[ValueObject<Guid>(conversions: Conversions.EfCoreValueConverter |
  Conversions.SystemTextJson | Conversions.TypeConverter)]`. The
  `TypeConverter` flag is what makes ASP.NET Core route-parameter binding
  work — Vogen does not ship a separate `AspNetCoreRouteParameter` flag.
  OpenAPI schema customisation is wired separately (see Implementation Notes
  § OpenAPI).
- The Vogen **build-time** generator (`Vogen` package) is referenced via
  `PrivateAssets="all"` on **each project that hosts `[ValueObject<...>]`
  declarations** — `LearnStack.SharedKernel` for cross-cutting value objects
  (`Email`, `Slug`, `LocaleCode`, `Money`), and **each
  `LearnStack.Modules.<X>.Domain`** for the module's aggregate-root IDs. The
  reference is centralised via `Directory.Build.props` so adding a new module
  picks the generator up automatically. The **runtime** assembly
  `Vogen.SharedTypes` (which carries the `Conversions` enum and a handful of
  helper types the generated code calls into) flows transitively to
  consumers — this is a small (~10 KB) MIT dependency, not a heavyweight
  runtime.
- Value objects with invariants (`Email`, `Slug`, `LocaleCode`, `Money`, …)
  follow the same annotation pattern, with a `Validate` static method
  enforcing the invariant.
- The `IStronglyTypedId<TKey>` marker interface ([Standards 02
  § Strongly-Typed Identifiers](../standards/02-backend-coding.md)) is
  implemented by every Vogen-emitted ID struct. The interface stays; Vogen
  is just the body.

The choice covers the four-artefact emission requirement, the value-object case, the
PostgreSQL 18 DB-side UUIDv7 path (Vogen can wrap any `Guid`, including those minted
by `uuidv7()`), and a `[Description]`/`[ReadOnly]` annotation surface Roslyn
analyzers can read for additional compile-time rules.

## Context

### Why Vogen over StronglyTypedId

StronglyTypedId was the field's first mover and would have worked for the ID case.
Three things pushed the choice:

- **Value-object coverage.** Standards 02 already lists `Email`, `Slug`, `LocaleCode`
  as value objects with invariants. Vogen's `[ValueObject]` annotation generates the
  same emitter set for these as for IDs — one generator covers both surfaces. With
  StronglyTypedId, IDs use the library and value objects use a hand-rolled path, with
  divergent EF/JSON conversion patterns.
- **OpenAPI schema customizer.** Vogen ships a Swashbuckle / Microsoft.OpenApi schema
  filter that registers the underlying primitive (`format: uuid`) on every emitted
  type. StronglyTypedId requires a manual `MapType<CourseId>(() => new
  OpenApiSchema(...))` per ID type in the OpenAPI setup — across 60+ ID types
  projected for Phase 02a–08, that is a real cost.
- **Maintenance velocity.** Vogen released 12 versions in 2024 and 3 in early 2025;
  StronglyTypedId's release cadence has slowed. Both are MIT, both are forkable in
  a worst case, but the active-maintenance signal favours Vogen.

### Why not custom

A custom Roslyn source generator would have produced the same artefacts. We
considered it because:

- The IL it emits is small and well-understood; we could match Vogen's output by hand.
- We have no external pressure to ship Vogen's "Bogus customization", "Dapper
  handler", or other peripheral surfaces.
- LearnStack already has a Roslyn analyzer project (`backend/analyzers/` per
  ADR-0032's `LearnStackException-DomainExceptionThrow`); adding one more isn't
  conceptually new.

Three things outweighed the appeal:

- **Vogen has 5+ years of community-found bugs already fixed.** Equality semantics,
  EF Core conversion edge cases (nullable navigation), JSON deserialization of `null`
  vs `0`, OpenAPI schema for nested generic types — Vogen's issue tracker is the
  receipts. Rebuilding that from scratch costs months we should spend on the domain.
- **The maintenance interface is `git pull`, not "find the file we wrote in
  2026".** A custom generator is a forever-owned artefact; an MIT package is owned
  externally with a clean exit (fork) if it stalls.
- **Roslyn source generator design has a steep learning curve.** Generator-author
  experience (incremental generators, attribute discovery, cancellation tokens,
  `IIncrementalGenerator` vs the older `ISourceGenerator`) is non-trivial; the
  library represents real expertise we'd otherwise rediscover.

### What would change our minds

- Vogen license shift away from MIT.
- Vogen archived / abandoned for > 12 months on .NET 11+ without a community fork
  picking it up.
- A LearnStack-specific emission requirement Vogen cannot model via a custom
  `Conversions` enum entry or a `[Description]`-style annotation pair — for instance,
  a regulatory ID format we needed to enforce at compile time that Vogen's
  validation hook could not express.

### What we explicitly punted on

- **UUIDv7 source.** Both DB-side (`uuidv7()`) and app-side
  (`Guid.CreateVersion7()`) are valid; the choice between them is per-aggregate (high-
  volume insert paths like `audit_log` / `outbox_messages` prefer DB-side, aggregates
  that need the ID before flush prefer app-side). This stays a Standards 05 (database)
  micro-rule, not an ADR.
- **Vogen-emitted type comparison semantics.** Default record-struct equality is
  by-value, which is correct for IDs; we accept Vogen's defaults rather than tuning
  them.

## Consequences

### Positive

- Compile-time strongly-typed IDs across the codebase from the first aggregate
  onwards; no raw-`Guid` slippage at the boundary.
- EF Core, JSON, OpenAPI, ASP.NET route binding all "just work" per ID type — no
  per-ID boilerplate.
- Value objects (`Email`, `Slug`, …) reuse the same annotation pattern; one shape
  to teach the team.
- OpenAPI spec generation knows the underlying primitive; the SDK generator (per
  ADR-0024) emits clean wrapper types for SDK consumers without manual schema
  hints.
- Roslyn-analyzer-friendly: future architecture tests can read Vogen's
  `[ValueObject]` attribute to enforce "every aggregate root ID uses Vogen, not
  raw `Guid`".

### Negative

- One more compile-time dependency on a third-party generator. Bumping Vogen major
  versions can break emission; we pin the version in `Directory.Packages.props` and
  treat upgrades as deliberate ADR-adjacent changes.
- Source generators slow incremental build slightly. Measured impact in Phase 02a's
  scaffold is < 200ms on a clean build; reassess if the codebase grows to a point
  where the generator dominates compile time.
- Diagnostics on emitted code reference Vogen-generated source files — IDE
  "go to definition" lands on generated `obj/` files; team has to know to
  navigate to the partial declaration instead.

### Neutral

- The `IStronglyTypedId<TKey>` interface in `LearnStack.SharedKernel` stays as a
  type-system contract; Vogen-generated structs implement it.
- Consumer projects gain a small (~10 KB) **runtime** dependency on
  `Vogen.SharedTypes` (which carries the `Conversions` enum and a handful of
  helper types referenced by generated code). The Vogen generator package
  itself (`Vogen`) stays build-time-only via `PrivateAssets="all"`.

## Implementation Notes

- **Package references:** `Directory.Packages.props` pins `<PackageVersion Include="Vogen" Version="7.0.0" />` (see Amendment 1 below for the rationale of the 6.x → 7.0.0 drift). **Every project that hosts `[ValueObject<>]` declarations** — `LearnStack.SharedKernel` (for cross-cutting value objects: `UserId`, `Email`, `Slug`, `LocaleCode`, `Money`) and **each `LearnStack.Modules.<X>.Domain`** (for its aggregate-root IDs) — adds `<PackageReference Include="Vogen" PrivateAssets="all" />` plus a transitive `Microsoft.EntityFrameworkCore` reference (the Vogen-emitted EF converter requires it at compile time; the build-time exception is recorded in [Standards 01 § Dependency Direction](../standards/01-architecture-standards.md)). A `Directory.Build.props` rule under `backend/src/Modules/` keeps the per-module addition automatic when a new module is scaffolded. Source generators only run on projects that reference the generator package; transitive references do **not** carry the generator (this is a `PrivateAssets="all"` semantics constraint, not a Vogen quirk).
- **Naming convention (per Standards 02):** ID type names end in `Id`
  (`TenantId`, `OrganizationId`, `CourseId`, …); value object types are named
  for the concept (`Email`, not `EmailValueObject`).
- **Default conversions enum:** every ID + value object opts into the same
  `Conversions` mask: `EfCoreValueConverter | SystemTextJson | TypeConverter`. The
  `TypeConverter` member carries ASP.NET Core minimal-API + MVC route-parameter
  binding (Vogen does not expose a separate `AspNetCoreRouteParameter` flag).
  A `LearnStack.SharedKernel.VogenDefaults` const captures the mask so the
  annotation reads `[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]`.
- **OpenAPI schema mapping:** Vogen does **not** ship a `Conversions.SwaggerSchemaFilter`-style
  flag. Schema customisation is wired one of two ways: (a) an assembly-level
  `[VogenDefaults(openApiSchemaCustomizations: ...)]` attribute in
  `LearnStack.SharedKernel` so every emitted type advertises its primitive
  shape, or (b) a custom `IOpenApiSchemaTransformer` in `LearnStack.Api`
  (Microsoft.AspNetCore.OpenApi) that detects Vogen-generated wrappers via the
  generated `IVogenValueObject<T>`-marker interface and emits the underlying
  primitive (`format: uuid`, `format: int64`, …). Packet 4 picks one when API
  conventions wiring lands; both paths are documented in Vogen's upstream docs.
- **EF Core registration:** the `EfCoreValueConverter` Vogen flag **generates**
  the converter type per ID; it does not auto-register it. Each module's
  `DbContext.OnConfiguring` (or a shared `IModelCustomizer`) calls
  `configurationBuilder.Properties<TId>().HaveConversion<TId.EfCoreValueConverter>()`
  for each Vogen-emitted ID. A `LearnStack.SharedKernel.Infrastructure`
  helper `ModelConfigurationBuilder.RegisterVogenIds(Assembly[])` reflects
  over the Domain assemblies and applies the registration in one call.
- **Architecture test (lands in Phase 02a Packet 2):**
  `Aggregate_Roots_Use_StronglyTypedId` — every type implementing
  `IAggregateRoot<TId>` has `TId : IStronglyTypedId<Guid>`, and every such
  `TId` carries `[ValueObject<Guid>]`. Catalogued under
  [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md)
  when the first ID lands.
- **UUIDv7 minting:**
  - DB-side (`uuidv7()` per ADR-0031) for `audit_log`, `outbox_messages`,
    `inbox_messages`, `idempotency_keys` — high-volume append-only tables.
  - App-side (`Guid.CreateVersion7()`) for aggregates that need the ID before
    `SaveChangesAsync()` to emit domain events / outbox writes referencing the
    new aggregate ID.
- **PostgreSQL 18 alignment:** Vogen-emitted `Guid` wrapper types are wire-compatible
  with `uuid` columns; the `uuidv7()` `DEFAULT` is a server-side concern,
  invisible to Vogen.

## Amendments

### Amendment 1 — Vogen 7.0.0 pin + architecture-test placement (2026-05-21)

Two clarifications surfaced when the ADR met implementation in
[Phase 02a Packet 2](../roadmap/phase-02a-kernel-tenancy.md):

- **Pinned Vogen version is 7.0.0.** The ADR was written when 6.x was the
  newest line; by the time `Directory.Packages.props` was wired the 6.0.x
  series had been superseded on NuGet and the lowest available major was
  `7.0.0`. The decision is unchanged — Vogen is still the chosen emitter
  per the original "Decision" section; this amendment records the
  concrete version pin for traceability. The previous-line placeholder
  in Implementation Notes (`Version="..."`) is now read as `Version="7.0.0"`.

- **`Aggregate_Roots_Use_StronglyTypedId` lands with the first aggregate,
  not in Packet 2.** Implementation Notes originally said "lands in
  Phase 02a Packet 2"; that placement is wrong because no module ships an
  aggregate in Packet 2 (the first concrete aggregate IDs arrive in
  Packet 6 — Tenancy schema foundations — and after). The test would have
  been vacuously green for the entire Packet 2 → Packet 5 window. The
  correct placement is alongside the first `IAggregateRoot<TId>` type,
  catalogued under
  [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md)
  at that point.

### Amendment 2 — cross-cutting identifiers (2026-08-10)

`UserId`, `TenantId` and `OrganizationId` live in `LearnStack.SharedKernel`.

The Decision splits `[ValueObject<>]` declarations two ways: `LearnStack.SharedKernel`
for cross-cutting value objects, and each `LearnStack.Modules.<X>.Domain` for **that
module's aggregate-root IDs**. Three identifiers satisfy both arms, and the Decision's
illustrative list — `Email`, `Slug`, `LocaleCode`, `Money` — contains no identifier, so
it settles nothing for them.

**`UserId`, `TenantId` and `OrganizationId` are cross-cutting value objects and live in
`LearnStack.SharedKernel`.** They are the only three; every other aggregate-root ID
follows the Decision's second arm unchanged.

The reason is structural, not stylistic. `AuditableEntity<TId>` is a **SharedKernel**
type and carries `CreatedBy` / `UpdatedBy` / `DeletedBy` as `UserId`, so `UserId` cannot
live in `LearnStack.Modules.Identity.Domain` — SharedKernel may not reference a module.
`TenantId` appears on every tenant-owned entity in every module and `OrganizationId` on
every org-scoped one, so placing either in its owning module's `Domain` would make every
other module's `Domain` reference `Tenancy.Domain`, which
`ModuleDomain_DoesNotDependOn_OtherModuleDomain` rejects.

This records what shipped and what the corpus already assumed: `UserId` is listed under
SharedKernel in § Implementation Notes and exists at
`backend/src/LearnStack.SharedKernel/Identifiers/UserId.cs`, and [Phase 02a Packet
6](../roadmap/phase-02a-kernel-tenancy.md) introduces `TenantId` / `OrganizationId` in
the same assembly. What was missing was the rule that licenses it, so each of the three
read as an unexplained exception.

Aggregate-root **ownership** is unaffected: the `Organization` aggregate is declared in
`LearnStack.Modules.Tenancy.Domain` per
[ADR-0017 Amendment 2 (2026-08-10)](0017-tenant-organization-hierarchy.md#2026-08-10--module-ownership-of-the-organization-aggregate).
An ID in SharedKernel is a shared vocabulary, not a claim on the aggregate.

This is a clarification; the Decision is unchanged.

### Amendment 3 — `IStronglyTypedId<TKey>` is no longer a pure marker (2026-08-10)

The Decision calls `IStronglyTypedId<TKey>` a **marker** interface and closes with
"the interface stays; Vogen is just the body". Both readings need narrowing after
[Phase 02a Packet 3b](../roadmap/phase-02a-kernel-tenancy.md).

**The interface carries one behavioural member: `bool IsInitialized()`.** It is no
longer a pure marker. The member exists because the obvious spelling of "is this id
unset?" — `id.Equals(default(TId))` — silently answers `false` for an unset id: a
Vogen `[ValueObject]` returns `false` from `Equals` whenever either side is
uninitialized. A guard written that way never runs, which is what shipped in Packet 2
and what Packet 3b found by measuring: `Entity<TId>.GetHashCode()` threw
`ValueObjectValidationException` for any aggregate that had not been given an id, so
a `HashSet` of two new aggregates was an exception rather than a set.

**"Vogen is just the body" now has a direction.** Vogen emits `IsInitialized()`, so
every existing id satisfies the member without a line of code — but the dependency
runs the other way too: the interface now requires something only Vogen happens to
generate. A hand-written id must implement it explicitly, and replacing Vogen means
replacing that member as well. That is a real narrowing of the Decision's "the
interface stays" independence, and it is recorded here rather than discovered later.

**Aggregate id type parameters also constrain to `IEquatable<TId>`.** Without it
`id.Equals(other.Id)` binds to `ValueType.Equals(object)` and boxes. Measured per
call on the shipped kernel: 40 bytes for that single call, and 0 with the constraint.
Every Vogen `record struct` already implements `IEquatable<TSelf>`, so this is a
constraint on the *use* of ids, not a new requirement on ids themselves. It is stated
here because it is a consequence of choosing a struct-based id generator, which is
this ADR's decision.

Both rules are written in
[Standards 02 § Domain Modeling](../standards/02-backend-coding.md).

This is a clarification; the Decision is unchanged.

## References

- [Standards 02 § Strongly-Typed Identifiers](../standards/02-backend-coding.md)
- [ADR-0031 PostgreSQL — Start on 18.x](0031-postgresql-major-version.md) — native
  UUIDv7 widens the design space; this ADR commits to Vogen-emitted wrappers on
  that primitive.
- [ADR-0032 Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md)
  — establishes the `backend/analyzers/` Roslyn analyzer location; future ID-shape
  analyzers live alongside.
- [Vogen on GitHub](https://github.com/SteveDunn/Vogen) — upstream project (MIT).
