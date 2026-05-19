# 00 — Engineering Principles

**Status:** Active

These are the beliefs that every other standard descends from. When a standard and a principle disagree, the principle wins and the standard gets fixed.

## 1. The Core Stays Generic

LearnStack is a platform for many education products. The core never holds
domain-specific business rules. CEFR levels, exam curricula, certification flows, kyu
ranks, asana catalogs, kata progressions, beat-counting exercises — all of these live as
**tenant customization data** ([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)),
not as code in any module. If a feature request would put product-specific logic into a
core module, the answer is to express it as a `TenantContentType`, `TenantLevelTaxonomy`,
`TenantScoringRule`, `TenantCompletionRule`, `TenantLessonItemType`, or
`TenantCustomFieldDef` — not to add a module or branch on a domain identifier.

A class, file, table, column, permission, or audit event name in the core that contains
a domain term (`CEFR`, `English`, `Asana`, `Kyu`, …) is a bug. The architecture test
`Core_Modules_HaveNo_DomainSpecific_Names` guards against this.

## 2. Tenant Isolation Is Not a Feature, It Is a Boundary Condition

Every read, every write, every job, every event, every log line, every metric is
tenant-scoped (and, where applicable, organization-scoped per
[ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)) unless it is an
explicit platform-admin operation. There is no "we'll harden tenant isolation later."
It is in from day one and defended in depth: tenant context + EF query filter +
PostgreSQL RLS + architecture tests, with the same four-layer treatment for the
organization dimension where the entity is org-scoped. The architecture tests run on
Day 1 of Phase 02, not as a Phase-11 cleanup pass.

## 3. Modules Talk Through Contracts, Not Tables

The modular monolith works only because modules pretend to be services. Cross-module navigation properties, cross-module SQL joins, and cross-module entity imports are all forbidden. The four allowed patterns — application contract, intra-module domain event, integration event, read-model projection — are enough.

## 4. Providers Are Adapters

Anything that crosses the LearnStack boundary — payments, email, SMS, search, storage,
identity, live-class media, the **Hub**, **entitlement source**, **host→tenant
resolution**, **event bus**, **cache**, **secret store** — lives behind an interface.
The domain code knows nothing about Stripe, Postmark, SeaweedFS, Keycloak, LiveKit, Dapr,
Kafka, Redis, Vault, or the Hub. Provider-specific code lives in
`Infrastructure.<Provider>` packages. Swapping a provider is a composition-root edit,
not a code change.

In practice this means: `IEventBus` not `KafkaProducer`; `ICacheService` not
`IConnectionMultiplexer`; `ISecretProvider` not `VaultClient`; `IEntitlementProvider`
not `HubHttpClient`; `IHostToTenantResolver` not `IConfiguration["hosts:..."]`.

## 5. Explicit Over Implicit

We prefer code that says what it does. Strongly-typed ids over `Guid`. Result types over thrown exceptions for expected outcomes. Tenant context as a parameter or ambient with audited resolution, never invisible static state. JSON shape declared by record types, not inferred from runtime.

## 6. Foundation First, Polish Second

We invest in correct foundations before chasing surface polish. Tenant resolution, module boundaries, event infrastructure, observability — these are paid up front. Pixel-perfect UI, animation timings, and editor ergonomics come after the engine works.

## 7. Trust the Type System

If the type system can prove an invariant, the type system should prove it. Magic strings get replaced by enums or value objects. Nullability is checked, not assumed. C# nullable reference types and TypeScript strict mode are non-negotiable.

## 8. Tests Are Documentation

A failing test should tell a reader what behavior we promised. Test names read like specifications. Architecture tests guard module boundaries that human discipline cannot. Integration tests prove tenant isolation in CI, not in production.

## 9. Default to Boring

We use mature, well-known technologies until measured pain forces an exception. PostgreSQL, Redis, SeaweedFS, ASP.NET Core, Next.js, Hangfire — boring on purpose. The interesting parts of LearnStack are the education domain and the platform composition, not the infrastructure choices.

## 10. Cost-Aware From Day One

Live classroom bandwidth, recording compute, video delivery egress, search index storage — each has a per-tenant cost trail. We model it, monitor it, and surface it. Cost-blindness is a planning bug.

## 11. Reversibility Beats Cleverness

A clever solution that locks us in is worse than a boring solution behind an adapter. Default toward changes we can undo: feature flags, adapter swaps, additive migrations, schema versioning.

## 12. Write Code That a Tired Reviewer Can Understand

Future you, on a Friday afternoon, has to understand this code. Clear naming, short functions, deliberate abstraction. A clever trick is a tax that compounds.

## 13. No Dead Code, No Dead Tests, No Dead Branches

Commented-out code is deleted. Skipped tests are either fixed within one sprint or removed. Long-lived feature branches accumulate drift and merge pain — keep them short. If a code path is intentionally dead, an ADR or comment explains why with a date.

## 14. Single Source of Truth

Each piece of knowledge lives in exactly one place:
- Glossary defines terms.
- ADRs hold decisions.
- Standards hold ongoing rules.
- OpenAPI describes the API.
- Migrations describe the schema.

Duplication invites drift.

## 15. Disagree Through Pull Requests

If a principle, standard, or pattern feels wrong, the answer is a PR that changes it — not silent deviation. Standards are versioned documents, not folklore. Drift kills modular monoliths.

## Tone Across the Codebase

- Be precise. Avoid words that hedge unnecessarily.
- Be kind in review comments. Critique code, not people.
- Be specific in error messages. "Something went wrong" is not an error message.
- Be honest in commit messages. Describe what changed and why; not what you wish was true.
