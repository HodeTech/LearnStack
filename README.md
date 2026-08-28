# LearnStack

LearnStack is a **white-label platform for multi-branch education businesses that teach
live** — not a single LMS, and not an education product of its own. It is an
education-aware CMS, learning engine, and platform foundation that powers different
brands, landing pages, catalogs, and learner portals. The same code paths serve a
language school, a yoga studio, a music school, or a coding bootcamp — the difference
between them is **tenant customization data** loaded at provisioning, not compiled code
([ADR-0018](docs/decisions/0018-tenant-driven-customization-model.md)).

That claim has a stated edge: content shape, presentation and pure rule evaluation are
tenant data; stateful entitlement and external capability invocation are platform
features gated by plan. See
[Platform Vision § Genericity boundary](docs/architecture/01-platform-vision.md).

Two tenants in unrelated domains exist from
[Phase 02a Packet 7](docs/roadmap/phase-02a-kernel-tenancy.md) onward and are both
rendered in a browser in [Phase 02d](docs/roadmap/phase-02d-walking-skeleton.md), so
genericity is tested continuously rather than asserted and checked once at the end.

LearnStack ships in three production deployment modes — SaaS, Dedicated, Self-Hosted —
backed by the companion **LearnStack Hub** repository, which provides the SaaS /
Dedicated control plane, plan editor, custom-domain admin, and license-key issuance.

Today only `Development` and `SaaS` are wired and tested end to end. `Dedicated` and
the two Self-Hosted `DeploymentMode` values (`SelfHostedOnline`,
`SelfHostedAirGapped`) are **prepared seams, not supported deployments** — the
composition root branches on them, but their adapters and integration suites land in
[Phase 11](docs/roadmap/phase-11-production-hardening.md) per
[ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md). See
[25-deployment-models.md](docs/architecture/25-deployment-models.md) for what a
prepared seam means concretely.

The Hub repo is expected to live at `../LearnStack-Hub` (sibling to this repo on the
developer's workstation) so the cross-repo doc links resolve. The Hub repository owns
its own roadmap at `../LearnStack-Hub/docs/roadmap/`. See
[LearnStack-Hub on GitHub](https://github.com/HodeTech/LearnStack-Hub) and
[docs/roadmap/phase-02c-hub-foundation.md](docs/roadmap/phase-02c-hub-foundation.md)
for LearnStack's side of the boundary.

## Status

**Phase 01 complete. [Phase 02a](docs/roadmap/phase-02a-kernel-tenancy.md) in progress —
packets 0–3, 3b, 4, 5 and 6 shipped; packets 4–10 re-scoped on 2026-08-08.
[Packet 7](docs/roadmap/phase-02a-kernel-tenancy.md#packet-sequence) — host and tenant
resolution, the query filters, and the two seed tenants — is next.**

Phase 01 shipped the .NET 10 solution scaffold, the `pnpm` frontend monorepo
(`apps/web` + `packages/{config,ui,sdk}`), the local-dev `docker-compose` stack, and the
DX + CI surround. See
[phase-01-repository-tooling.md](docs/roadmap/phase-01-repository-tooling.md) for the
per-packet history.

Phase 02a packets 0–3 shipped the foundation decisions
([ADR-0023](docs/decisions/0023-strongly-typed-id-source-generator.md) Vogen,
[ADR-0024](docs/decisions/0024-api-versioning-policy.md) API versioning,
[ADR-0028](docs/decisions/0028-audit-log-partition-management.md) audit partition
management — whose *timing* later moved to Phase 11), the shared kernel core, and the
[ADR-0032](docs/decisions/0032-exception-handling-logging-and-observability.md)
cross-cutting foundation. [Packet 3b](docs/roadmap/phase-02a-kernel-tenancy.md#delivery-record-packet-3b)
then repaired what a corpus audit found — before any consumer existed.

[Packet 4](docs/roadmap/phase-02a-kernel-tenancy.md#delivery-record-packet-4) shipped the
API conventions: `/api/v{N}` routing, one RFC 7807 shape on every error, cursor
pagination and the sort grammar, the
[ADR-0036](docs/decisions/0036-tenant-resolution-trusted-inputs.md) tenancy edge,
idempotency keys and ETag concurrency, and the first working SDK generation.
[Packet 5](docs/roadmap/phase-02a-kernel-tenancy.md#delivery-record-packet-5) shipped the
foundation ports with the implementations that actually run today — `ICacheService` /
`InMemoryCacheService`, `IEventBus` / `InProcessEventBus`, `ISecretProvider` /
`ConfigurationSecretProvider` — each selected at one composition-root site, and moved
the seven demand-gated services out of the daily `make dev` loop.
[Packet 6](docs/roadmap/phase-02a-kernel-tenancy.md#delivery-record-packet-6) shipped the
tenancy schema — the four database roles, ten tables in two migration chains, every one
under `ENABLE` **and** `FORCE ROW LEVEL SECURITY` with the corrected
[ADR-0003 Amendment 3](docs/decisions/0003-tenant-isolation-defense-in-depth.md)
template — together with the `Tenant` and `Organization` aggregates, the first module
spec, and [ADR-0040](docs/decisions/0040-ambient-unit-of-work.md)'s ambient unit of work.
Each record lists the defects the packet introduced and caught in its own review rounds
alongside what it built.

The 2026-08-08 restructure moved correctness earlier (the corrected RLS template in
[ADR-0003 Amendment 3](docs/decisions/0003-tenant-isolation-defense-in-depth.md), durable
MUST-class audit in [ADR-0033](docs/decisions/0033-audit-durability-model.md)), moved
additive infrastructure later behind its ports
([ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md)), and moved the
genericity proof earlier — two seed tenants in Packet 7, rendered in a browser in
[Phase 02d](docs/roadmap/phase-02d-walking-skeleton.md), the next user-visible
milestone. Tenancy is the only module holding domain code; the other six assemblies are
still empty.

```bash
make install   # one-time: deps + git hooks
make dev       # bring local stack up
make seed      # verify health + print demo credentials
```

> `make seed` exits 0 on a clean stack. The health gate used to time out on every run:
> it read every service's `healthcheck:` block in the Compose file and flagged any
> service that declared none, but `dapr-placement` and `dapr-sidecar-api` cannot
> declare one — their images ship a single binary on an empty base, with no shell
> and no `wget`/`curl`/`nc` to probe with.
> [Phase 02a Packet 3b](docs/roadmap/phase-02a-kernel-tenancy.md) repaired that along
> with the rest of the Phase 01 development-loop debt.

## Direction At A Glance

- **Backend:** .NET 10, ASP.NET Core, Entity Framework Core, MediatR.
- **Database:** PostgreSQL 18, with Row-Level Security from day one. Tenant +
  **Organization** defense in depth
  ([ADR-0003](docs/decisions/0003-tenant-isolation-defense-in-depth.md) Amendment 1 for
  organization scope, **Amendment 3** for the corrected policy template and the
  four-role database model,
  [ADR-0017](docs/decisions/0017-tenant-organization-hierarchy.md)). The canonical SQL
  lives in exactly one file:
  [Database Standards](docs/standards/05-database.md).
- **Foundation ports:** `IEventBus`, `ICacheService`, `ISecretProvider`,
  in `LearnStack.SharedKernel`, each with a working default implementation.
  `IEntitlementProvider` and `IHostToTenantResolver` are **not** among them — both need
  tenancy schema and land with Packets 9 and 7. Vendor adapters — Dapr
  ([ADR-0038](docs/decisions/0038-cross-cutting-port-and-event-contracts.md)), Kafka, Valkey
  ([ADR-0030](docs/decisions/0030-redis-compatible-store-valkey.md)), Vault, APISIX
  ([ADR-0015](docs/decisions/0015-api-gateway-apisix.md)) — are **demand-gated**: each
  has an owning phase and a written trigger condition in
  [ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md).
- **Object storage:** SeaweedFS locally, S3-compatible storage in production.
- **Search:** PostgreSQL full-text search first; Meilisearch behind `ITenantSearch` when
  quality or scale requires it.
- **Frontend:** Next.js 15 (App Router), TypeScript, React. **One** application
  (`apps/web`) with route segments for public, studio, and portal; a multi-app split
  inside this repo is not planned before
  [Phase 11](docs/roadmap/phase-11-production-hardening.md). The operator portal
  (`frontend/apps/operator-portal`) lives in the separate `LearnStack-Hub` repository.
- **Identity:** Self-hosted Keycloak with **two realms** — `learnstack` for tenant
  users, `learnstack-hub` for operators.
- **Architecture:** Modular monolith with explicit module contracts.
- **Tenant customization:** Per [ADR-0018](docs/decisions/0018-tenant-driven-customization-model.md),
  content types, page blocks, lesson item types, level taxonomies, scoring rules,
  completion rules, custom fields, and notification templates are **data** authored
  by tenants, not code. The core stays generic, within the boundary the ADR's 2026-08-08
  amendment draws.
- **Audit:** Append-only `LearnStack.Modules.Audit` with EF interceptor + MediatR
  behavior. MUST-class audit is a durable intent written inside the business
  transaction per [ADR-0033](docs/decisions/0033-audit-durability-model.md), which
  supersedes ADR-0016; `audit_log` partitioning and retention land in
  [Phase 11](docs/roadmap/phase-11-production-hardening.md).
- **Entitlements:** Feature-based projection mirrored from the Hub per
  [ADR-0021](docs/decisions/0021-feature-based-entitlement.md); typed
  `FeatureKeys` / `LimitKeys` registries. The Hub contract is governed by two
  invariants — the Hub stores no tenant content, and every crossing goes through a
  named adapter — per
  [ADR-0034](docs/decisions/0034-hub-contract-surface-invariant.md).
- **Live classroom:** In-app WebRTC; **self-hosted LiveKit OSS** is the default;
  LiveKit Cloud available behind the same `ILiveClassProvider` interface. A custom
  WebRTC SFU is explicitly out of scope.
- **Recording:** Supported via LiveKit Egress to S3/SeaweedFS; tenant-configurable;
  consent-aware; off by default.
- **Deployment:** Triple deployment model per
  [ADR-0020](docs/decisions/0020-triple-deployment-hybrid-license.md) —
  SaaS / Dedicated (Hub-backed) and Self-Hosted (RSA-signed `.lic` file +
  optional phone-home + 30-day grace).

## Documentation Map

### Architecture (`docs/architecture/`)

Strategy & shape:
- [01 — Platform Vision](docs/architecture/01-platform-vision.md)
- [02 — Domain Model](docs/architecture/02-domain-model.md)
- [03 — Module Boundaries](docs/architecture/03-module-boundaries.md)
- [04 — Technical Architecture](docs/architecture/04-technical-architecture.md)
- [05 — MVP Scope](docs/architecture/05-mvp-scope.md)
- [06 — Extension Model](docs/architecture/06-extension-model.md)

Live classroom:
- [07 — In-App Live Classroom](docs/architecture/07-in-app-live-classroom.md)
- [08 — Live Classroom Cost Model](docs/architecture/08-livekit-cost-model.md)

Platform concerns:
- [09 — Tenant Isolation](docs/architecture/09-tenant-isolation.md)
- [10 — Cross-Module Contracts](docs/architecture/10-cross-module-contracts.md)
- [12 — Localization](docs/architecture/12-localization.md)
- [13 — Identity and Authentication](docs/architecture/13-identity-and-auth.md)
- [14 — Frontend Architecture](docs/architecture/14-frontend-architecture.md)
- [15 — Events and Outbox](docs/architecture/15-event-and-outbox.md)
- [16 — Media Pipeline](docs/architecture/16-media-pipeline.md)
- [17 — Page Builder](docs/architecture/17-page-builder.md)
- [20 — Search](docs/architecture/20-search.md)
- [21 — Feature Flags & Entitlements](docs/architecture/21-feature-flags.md)
- [23 — Data Protection (KVKK / GDPR)](docs/architecture/23-data-protection.md)

LearnStack Hub + deployment:
- [24 — LearnStack Hub](docs/architecture/24-learnstack-hub.md)
- [25 — Deployment Models](docs/architecture/25-deployment-models.md)
- [26 — Hybrid License Model](docs/architecture/26-hybrid-license-model.md)
- [27 — Custom Domain + TLS](docs/architecture/27-custom-domain-tls.md)

Platform substrate deep dives:
- [28 — Platform Tenant + Organization](docs/architecture/28-platform-tenant-organization.md)
- [29 — Dapr Integration](docs/architecture/29-dapr-integration.md)
- [30 — API Gateway (APISIX)](docs/architecture/30-api-gateway.md)
- [31 — Audit Subsystem](docs/architecture/31-audit-subsystem.md)
- [32 — Tenant Customization Model](docs/architecture/32-tenant-customization-model.md)
- [33 — Cross-Cutting Concerns](docs/architecture/33-cross-cutting-concerns.md)

Decision context:
- [18 — WebRTC Build vs Adopt](docs/architecture/18-webrtc-build-vs-adopt.md)
- [19 — MVP Vertical Slice](docs/architecture/19-mvp-vertical-slice.md)

### Decisions (`docs/decisions/`)
- [ADR index](docs/decisions/README.md) — accepted decisions with their reasoning and
  consequences. The 2026-05-18 redesign added ADRs 0014–0022; the 2026-08-08
  restructure added [ADR-0033](docs/decisions/0033-audit-durability-model.md) (audit
  durability, supersedes ADR-0016),
  [ADR-0034](docs/decisions/0034-hub-contract-surface-invariant.md) (Hub contract
  invariants) and
  [ADR-0035](docs/decisions/0035-demand-gated-infrastructure.md) (demand-gated
  infrastructure).

### Engineering Standards (`docs/standards/`)
- [Standards index](docs/standards/README.md) — rules that apply to every PR: coding,
  testing, security, observability, accessibility, performance,
  [infrastructure-stack rules](docs/standards/20-infrastructure-stack.md), and the
  [architecture-test catalogue](docs/standards/21-architecture-tests-catalogue.md).

### Roadmap (`docs/roadmap/`)
- [Phased roadmap](docs/roadmap/README.md) — phases 00 through 12, including
  [Phase 02d](docs/roadmap/phase-02d-walking-skeleton.md) (Two-Tenant Walking Skeleton)
  and the Phase 02c (Hub Integration) and Phase 09b (Hub Billing) parallel tracks. The
  dependency map there is authoritative for order; filename order is not.

### Reference
- [Glossary](docs/glossary.md) — canonical definitions.

## Conventions

- All documentation is written in **English** (see
  [ADR-0007](docs/decisions/0007-documentation-language-and-conventions.md)).
- Diagrams use **Mermaid** in fenced code blocks.
- Architectural decisions are recorded as ADRs under `docs/decisions/`.
- Engineering rules live under `docs/standards/`.
- Each piece of knowledge lives in exactly one place; the
  [glossary](docs/glossary.md) is the single source of truth for terminology.

## How To Read This Repository

For the strategy and the headline decisions:
1. [Platform Vision](docs/architecture/01-platform-vision.md)
2. [MVP Scope](docs/architecture/05-mvp-scope.md)
3. [Roadmap overview](docs/roadmap/README.md)
4. [Phase 02a: Kernel + Tenancy](docs/roadmap/phase-02a-kernel-tenancy.md) — where the
   work is now
5. [Phase 02d: Two-Tenant Walking Skeleton](docs/roadmap/phase-02d-walking-skeleton.md)
   — where it is going next
6. [ADR index](docs/decisions/README.md)

For the technical foundations:
1. [Technical Architecture](docs/architecture/04-technical-architecture.md)
2. [Module Boundaries](docs/architecture/03-module-boundaries.md)
3. [Tenant Isolation](docs/architecture/09-tenant-isolation.md)
4. [Cross-Module Contracts](docs/architecture/10-cross-module-contracts.md)
5. [Tenant Customization Model](docs/architecture/32-tenant-customization-model.md)
6. [Infrastructure Stack Standards](docs/standards/20-infrastructure-stack.md)
7. [Engineering principles](docs/standards/00-principles.md)

For the Hub + deployment story:
1. [LearnStack Hub](docs/architecture/24-learnstack-hub.md)
2. [Deployment Models](docs/architecture/25-deployment-models.md)
3. [Hybrid License Model](docs/architecture/26-hybrid-license-model.md)
4. [Phase 02c: Hub Foundation](docs/roadmap/phase-02c-hub-foundation.md)

For the live classroom direction:
1. [In-App Live Classroom](docs/architecture/07-in-app-live-classroom.md)
2. [Live Classroom Cost Model](docs/architecture/08-livekit-cost-model.md)
3. [WebRTC Build vs Adopt](docs/architecture/18-webrtc-build-vs-adopt.md)
4. [ADR-0005: Live Classroom Media Stack](docs/decisions/0005-live-classroom-media-stack.md)
