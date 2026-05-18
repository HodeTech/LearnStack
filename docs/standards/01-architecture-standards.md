# 01 — Architecture Standards

**Status:** Active
**Derives from:** [ADR-0002 Initial Architecture](../decisions/0002-initial-architecture.md),
[ADR-0010 Cross-Module Communication](../decisions/0010-cross-module-communication.md)
(Amendment 1: outbox dispatch via Dapr pub/sub),
[ADR-0014 Adopt Dapr](../decisions/0014-adopt-dapr.md),
[ADR-0016 Audit Log Subsystem](../decisions/0016-audit-log-subsystem.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
(supersedes ADR-0011 Vertical Extension Points).

These rules turn the architecture docs into enforceable code conventions. They are checked by architecture tests where possible.

## Module Layout

Every backend module has the same internal shape:

```
LearnStack.Modules.<Name>.Application.Contracts/   public contracts (referenced by other modules)
LearnStack.Modules.<Name>.Application/             use cases, MediatR handlers, validators
LearnStack.Modules.<Name>.Domain/                  entities, aggregates, value objects, domain events
LearnStack.Modules.<Name>.Infrastructure/          EF configurations, adapters, external clients
```

Rules:
- A module's `Domain` references only the shared kernel.
- A module's `Application` references its own `Domain` plus other modules'
  `Application.Contracts`.
- A module's `Infrastructure` references its own `Application` and `Domain`, plus
  provider SDKs.
- Other modules reference only `<Name>.Application.Contracts`. Never `<Name>.Domain`
  or `<Name>.Infrastructure`.
- **There is no `LearnStack.Verticals.*` namespace.** Domain-specific shapes
  (CEFR levels, asana catalogs, kyu/dan ranks, code-challenge runners, …) live as
  **tenant customization data** ([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)),
  not as compiled code. The architecture test `No_Source_Folder_Named_Verticals`
  enforces this.

## Dependency Direction

```mermaid
flowchart LR
  contracts[Module.Application.Contracts]
  app[Module.Application]
  domain[Module.Domain]
  infra[Module.Infrastructure]
  kernel[SharedKernel]
  otherContracts[Other Module.Application.Contracts]
  providers[Provider SDKs]

  domain --> kernel
  app --> domain
  app --> contracts
  app --> otherContracts
  infra --> app
  infra --> providers
  contracts --> kernel
```

Forbidden edges:
- Domain → Application
- Domain → Infrastructure
- Application → Infrastructure (composition root wires the implementation in)
- Module A → Module B.Domain
- Module A → Module B.Infrastructure

## Aggregate Ownership

- Every entity belongs to exactly one aggregate; aggregate is owned by exactly one module.
- The aggregate root is the only entry point for state changes inside the aggregate.
- Repositories return aggregates, not raw entities.
- Cross-aggregate writes inside a single transaction are forbidden. Use an integration event.

## Cross-Module Communication

Allowed patterns (see [Cross-Module Contracts](../architecture/10-cross-module-contracts.md)):

1. Application service contract — interface in `<Module>.Application.Contracts`.
2. Domain event — internal only; never crosses module boundaries.
3. Integration event — written to the outbox in the same transaction; consumed asynchronously.
4. Read-model projection — public read-only table owned by one module.

Forbidden patterns:
- Cross-module EF navigation properties.
- Cross-module raw SQL joining tables owned by different modules.
- Static service locators that hide cross-module dependencies.
- Implicit ambient state used for cross-module coordination.

## Provider Adapters

Every external dependency that crosses the LearnStack boundary lives behind an interface:

| Concern | Interface |
|---------|-----------|
| Payment | `IPaymentProvider` |
| Email | `IEmailProvider` |
| SMS | `ISmsProvider` |
| Storage | `IStorageProvider` |
| Search | `ISearchProvider` |
| Live classroom | `ILiveClassProvider` |
| Recording egress | `IRecordingEgressProvider` |
| Identity | `IIdentityProvider` |

Rules:
- Interfaces live in `LearnStack.Application.Contracts` (or the appropriate module's contracts package).
- Implementations live in `LearnStack.Infrastructure.<Concern>.<Provider>` projects.
- Domain and application code never references a provider's SDK types.
- Composition root wires the chosen adapter via DI registration.

## Tenant-Scoped Code

- Every entity that has a `TenantId` property must be annotated `[TenantOwned]`.
- `[TenantOwned]` entities must have a configured EF global query filter and a PostgreSQL RLS policy (see [Database Standards](05-database.md)).
- Application services must never expose `IgnoreQueryFilters()` directly to callers.
- Background jobs and integration event handlers must accept `TenantId` as part of their payload and set it as the ambient context before doing work.

## Composition Root

A single composition project wires modules together:

- `LearnStack.Application` registers MediatR pipelines, validators, outbox dispatcher, and module-level service bindings.
- Each module exposes a single extension method (`services.AddXxxModule()`).
- Provider adapters are registered explicitly with configuration-bound options.

## Architecture Tests

Architecture tests live in `LearnStack.Tests.Architecture`. They enforce:

| Rule | Check |
|------|-------|
| Module dependency direction | NetArchTest / ArchUnitNET ruleset. |
| No cross-module Domain references | Project graph inspection. |
| Every `[TenantOwned]` has filter and policy | Reflection + migration scan. |
| No `IgnoreQueryFilters()` in non-platform code | Roslyn analyzer. |
| Public read models follow `public_<module>_<concept>` naming | Migration scan. |
| Provider SDK types not imported in Domain/Application | Reflection. |
| Hangfire job payloads include `TenantId` | Reflection. |

Tests run on every CI build and are not skippable.

## Tenant Customization (no Vertical Modules)

Per [ADR-0018](../decisions/0018-tenant-driven-customization-model.md), LearnStack
does **not** ship per-domain vertical modules. Tenant-specific shapes are expressed as
**data** in the customization aggregates owned by `LearnStack.Modules.Customization`:

- `TenantContentType` — JSON Schema definitions for tenant-defined content types.
- `TenantPageBlock` — schema + composite-renderer key for tenant-defined page blocks.
- `TenantLessonItemType` — schema + player key for tenant-defined lesson items.
- `TenantLevelTaxonomy` — items declared by a tenant's taxonomy (CEFR, yoga
  difficulty, kyu/dan, …).
- `TenantScoringRule` — sandboxed DSL expression for assessment scoring.
- `TenantCompletionRule` — boolean DSL expression for lesson/module/course completion.
- `TenantCustomFieldDef` — custom fields on built-in entities (`User`, `Course`,
  `Enrollment`, …), stored in the entity's `custom_fields jsonb` column.
- `TenantTemplateLibrary` — notification templates (email/SMS/WhatsApp/in-app) per
  locale, optionally per organization.

The runtime composes per-tenant content shapes by reading these records — schemas are
JSON, expressions are DSL strings (engine choice pending its own ADR). No tenant ships
a C# project; no `Verticals/` folder exists; an architecture test
(`No_Source_Folder_Named_Verticals`) keeps it that way.

Full data model and worked tenant examples:
[32-tenant-customization-model.md](../architecture/32-tenant-customization-model.md).

## Distributed-Consistency Tiers

When a use case writes to PostgreSQL and also calls an external system (LiveKit, Keycloak, Stripe/iyzico, an outbound webhook), the order of the two writes determines the failure model. Pick a tier per command instead of inventing a pattern.

| Tier | Pattern | When to use | Failure model |
|------|---------|-------------|---------------|
| **1** | DB-only. No external call inside the transaction. | The change does not require a side effect at commit time. | Standard EF Core transaction; nothing more. |
| **2A** | DB-first, then external. External is a *mirror* of DB state. | Search indexing, analytics fan-out, "send a notification after success." | If the external call fails, retry via outbox; the DB state is still correct. Never roll back the DB because the side effect failed. |
| **2B** | External-first, then DB. The external system returns an id the DB must store. | Provisioning a LiveKit room, creating a Keycloak user, opening a Stripe customer. | If the DB write fails, run a **compensating delete** against the external system. Idempotency keys on the external call are mandatory. |
| **3** | Idempotency-key + pending state + webhook confirmation. | Payments, recordings, anything where the external system needs minutes to confirm and may call back. | Insert a `pending` row protected by the idempotency key. Confirm via webhook. Never assume sync success means the side effect completed. |

Rules:

- Every cross-boundary command states its tier in code (`// consistency-tier: 2B`) or in the handler XML doc when the tier is non-obvious.
- Outbox dispatch handles tier 2A reliably; tier 2B requires the compensating delete to be testable end to end.
- Tier 3 commands must have a webhook-side handler that closes the pending state idempotently.
- Provider SDK calls inside an EF transaction are forbidden (already a rule under [Database Standards](05-database.md) § Forbidden) — the tier framing makes the reason explicit.

This is the decision rule every PR author runs before writing a handler that talks to an external system.

## Service Extraction Readiness

The modular monolith must remain extractable. When a module is later promoted to a
separate service, only the **adapter** and **transport** layer should change, not the
domain model.

To stay extractable:
- Public contracts in `Application.Contracts` must use only primitives and ids.
- Integration events must be serializable to JSON without loss.
- Cross-module reads happen through contracts, never through shared DbContexts.
- The outbox boundary is identical between **`InProcessEventBus`** (dev) and
  **`DaprEventBus` → Kafka** (production) — both implement the same `IEventBus`
  interface, so a module promoted to a separate service inherits at-least-once
  delivery with no code change. See
  [20-infrastructure-stack.md](20-infrastructure-stack.md) and
  [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).
