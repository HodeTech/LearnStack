# LearnStack

LearnStack is a **multi-tenant core platform for building education products** — not a single LMS. It is an education-aware CMS and platform engine that can power different learning brands, landing pages, catalogs, portals, and vertical education products (online English learning is planned as the first vertical, with more to follow).

## Status

Pre-implementation. This repository currently holds the architecture, decisions, roadmap, and engineering standards that will guide the build. No application code exists yet.

## Direction At A Glance

- **Backend:** .NET 10, ASP.NET Core, Entity Framework Core.
- **Database:** PostgreSQL 16, with Row-Level Security from day one (defense in depth alongside EF query filters).
- **Cache:** Redis 7.
- **Object storage:** MinIO locally, S3-compatible storage in production.
- **Search:** Meilisearch initially.
- **Frontend:** Next.js (App Router), TypeScript, React. **One** application with route segments for public, studio, and portal — multi-app split is deferred.
- **Identity:** Self-hosted Keycloak (Authentik as documented alternative).
- **Architecture:** Modular monolith with explicit module contracts.
- **Live classroom:** In-app WebRTC; **self-hosted LiveKit OSS** is the default; LiveKit Cloud available behind the same `ILiveClassProvider` interface. A custom WebRTC SFU is explicitly out of scope.
- **Recording:** Supported via LiveKit Egress to S3/MinIO; tenant-configurable; consent-aware; off by default.

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
- [11 — Extension Points](docs/architecture/11-extension-points.md)
- [12 — Localization](docs/architecture/12-localization.md)
- [13 — Identity and Authentication](docs/architecture/13-identity-and-auth.md)
- [14 — Frontend Architecture](docs/architecture/14-frontend-architecture.md)
- [15 — Events and Outbox](docs/architecture/15-event-and-outbox.md)
- [16 — Media Pipeline](docs/architecture/16-media-pipeline.md)
- [17 — Page Builder](docs/architecture/17-page-builder.md)
- [20 — Search](docs/architecture/20-search.md)
- [21 — Feature Flags](docs/architecture/21-feature-flags.md)
- [22 — Custom Domains](docs/architecture/22-custom-domains.md)
- [23 — Data Protection (KVKK / GDPR)](docs/architecture/23-data-protection.md)

Decision context:
- [18 — WebRTC Build vs Adopt](docs/architecture/18-webrtc-build-vs-adopt.md)
- [19 — MVP Vertical Slice](docs/architecture/19-mvp-vertical-slice.md)

### Decisions (`docs/decisions/`)
- [ADR index](docs/decisions/README.md) — accepted decisions with their reasoning and consequences.

### Engineering Standards (`docs/standards/`)
- [Standards index](docs/standards/README.md) — rules that apply to every PR: coding, testing, security, observability, accessibility, performance, and more.

### Roadmap (`docs/roadmap/`)
- [Phased roadmap](docs/roadmap/README.md) — phases 00 through 11 covering strategy, foundation, identity, CMS, education, renderer, enrollment, assessment, classroom, billing, English vertical, and production hardening.

### Reference
- [Glossary](docs/glossary.md) — canonical definitions.

## Conventions

- All documentation is written in **English** (see [ADR 0007](docs/decisions/0007-documentation-language-and-conventions.md)).
- Diagrams use **Mermaid** in fenced code blocks.
- Architectural decisions are recorded as ADRs under `docs/decisions/`.
- Engineering rules live under `docs/standards/`.
- Each piece of knowledge lives in exactly one place; the [glossary](docs/glossary.md) is the single source of truth for terminology.

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
5. [Engineering principles](docs/standards/00-principles.md)

For the live classroom direction:
1. [In-App Live Classroom](docs/architecture/07-in-app-live-classroom.md)
2. [Live Classroom Cost Model](docs/architecture/08-livekit-cost-model.md)
3. [WebRTC Build vs Adopt](docs/architecture/18-webrtc-build-vs-adopt.md)
4. [ADR 0005: Live Classroom Media Stack](docs/decisions/0005-live-classroom-media-stack.md)
