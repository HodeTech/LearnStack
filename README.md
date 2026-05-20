# LearnStack

LearnStack is a **multi-tenant core platform for building education products** — not a
single LMS. It is an education-aware CMS and platform engine that powers different
learning brands, landing pages, catalogs, portals, and **domain-agnostic** education
products. The same code paths serve an online English-learning brand, a yoga studio,
a coding bootcamp, a music school, or a driving school — the difference between them
is **tenant customization data** loaded at provisioning, not compiled code.

The first showcase tenant happens to be an online English-learning platform (Phase 10);
the substrate-genericity proof is a second, non-English tenant running the same code
paths against its own customization data.

LearnStack ships in three production deployment modes — SaaS, Dedicated, Self-Hosted —
backed by the companion **`learnstack-hub`** repository which provides the SaaS /
Dedicated control plane, plan editor, custom-domain admin, and license-key issuance.

## Status

Phase 01 complete. The repository now holds the .NET 10 solution scaffold (7
modules × 4 projects + 4 test projects with `No_Source_Folder_Named_Verticals`
architecture test), the `pnpm` frontend monorepo (`apps/web` +
`packages/{config,ui,sdk}`), the full local-dev `docker-compose` stack —
PostgreSQL 18, Valkey, SeaweedFS, Mailpit, Meilisearch, Keycloak (two realms),
LiveKit OSS + Coturn, Kafka + kafka-ui, Vault, Dapr sidecar + placement, APISIX
(file-driven standalone) — and the DX + CI surround: repo-root `Makefile`,
`.env.example` single source of truth, `.githooks/pre-commit` formatter,
`infra/compose/e2e.yml` ephemeral overlay, `.github/workflows/ci.yml`, and
`scripts/seed.sh`. See [docs/roadmap/phase-01-repository-tooling.md](docs/roadmap/phase-01-repository-tooling.md)
for the per-packet history. Phase 02a (Platform Kernel + Multi-Tenancy) has
kicked off; see [Phase 02a Status & Packets](docs/roadmap/phase-02a-kernel-tenancy.md)
for the 11-packet breakdown. Packet 0 — Kickoff has shipped; Packet 1 —
Foundation decisions (ADR-0023 / ADR-0024 / ADR-0028 to Accepted) is next.
Phase 02c (Hub Foundation, parallel, separate repo) starts once the 02a
sockets it depends on are in place.

```bash
make install   # one-time: deps + git hooks
make dev       # bring local stack up
make seed      # verify health + print demo credentials
```

## Direction At A Glance

- **Backend:** .NET 10, ASP.NET Core, Entity Framework Core, MediatR.
- **Database:** PostgreSQL 18, with Row-Level Security from day one. Tenant + **Organization**
  defense in depth ([ADR-0003 Amendment 1](docs/decisions/0003-tenant-isolation-defense-in-depth.md),
  [ADR-0017](docs/decisions/0017-tenant-organization-hierarchy.md)).
- **Cache / Pub-Sub / Secrets:** Valkey 8 (RESP-protocol Linux-Foundation BSD fork
  per [ADR-0030](docs/decisions/0030-redis-compatible-store-valkey.md)), Kafka,
  HashiCorp Vault — all accessed via **Dapr** building blocks (`IEventBus`,
  `ICacheService`, `ISecretProvider`) per
  [ADR-0014](docs/decisions/0014-adopt-dapr.md).
- **API Gateway:** **APISIX** in file-driven standalone (`data_plane`) mode per
  [ADR-0015](docs/decisions/0015-api-gateway-apisix.md).
- **Object storage:** SeaweedFS locally, S3-compatible storage in production.
- **Search:** Meilisearch initially.
- **Frontend:** Next.js 15 (App Router), TypeScript, React. **One** application
  (`apps/web`) with route segments for public, studio, and portal — multi-app split
  inside this repo deferred. The operator portal (`learnstack-hub-web`) lives in the
  separate `learnstack-hub` repository.
- **Identity:** Self-hosted Keycloak with **two realms** — `learnstack` for tenant
  users, `learnstack-hub` for operators.
- **Architecture:** Modular monolith with explicit module contracts.
- **Tenant customization:** Per [ADR-0018](docs/decisions/0018-tenant-driven-customization-model.md),
  content types, page blocks, lesson item types, level taxonomies, scoring rules,
  completion rules, custom fields, and notification templates are **data** authored
  by tenants, not code. The core stays generic.
- **Audit:** Append-only `LearnStack.Modules.Audit` with EF interceptor + MediatR
  behavior + partitioned `audit_log` table per
  [ADR-0016](docs/decisions/0016-audit-log-subsystem.md).
- **Entitlements:** Feature-based projection mirrored from the Hub per
  [ADR-0021](docs/decisions/0021-feature-based-entitlement.md); typed
  `FeatureKeys` / `LimitKeys` registries.
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

Decision context:
- [18 — WebRTC Build vs Adopt](docs/architecture/18-webrtc-build-vs-adopt.md)
- [19 — MVP Vertical Slice](docs/architecture/19-mvp-vertical-slice.md)

### Decisions (`docs/decisions/`)
- [ADR index](docs/decisions/README.md) — accepted decisions with their reasoning and
  consequences. The 2026-05-18 redesign added ADRs 0014–0022.

### Engineering Standards (`docs/standards/`)
- [Standards index](docs/standards/README.md) — rules that apply to every PR: coding,
  testing, security, observability, accessibility, performance,
  [infrastructure-stack rules](docs/standards/20-infrastructure-stack.md), and more.

### Roadmap (`docs/roadmap/`)
- [Phased roadmap](docs/roadmap/README.md) — phases 00 through 12, including the
  Phase 02c (Hub Foundation) and Phase 09b (Hub Billing) parallel tracks.

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
4. [ADR index](docs/decisions/README.md)

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
