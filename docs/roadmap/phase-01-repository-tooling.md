# Phase 01: Repository, Tooling, and Local Infrastructure

> **In-progress status (2026-05-19).** The phase is implemented incrementally in
> packets. Packets 1-3 are shipped; packets 4-8 remain.
>
> **Packet 1 — Backend skeleton ✅**
> .NET 10 solution scaffold, central package management, `LearnStack.slnx`, 7 core
> projects + 7 modules × 4 packages + 4 test projects (Unit / Integration /
> Architecture / Contract) including `No_Source_Folder_Named_Verticals`
> (ADR-0018), module-dependency-direction tests, and the
> `Meta_NetArchTest_DetectsAPlantedViolation` positive-control meta-test.
> Minimal `/healthz` + OpenAPI host. Contract test fixture pins
> `Environments.Development` so `MapOpenApi()` lights up under `dotnet test`.
>
> **Packet 2 — Frontend monorepo ✅**
> pnpm workspaces (Node 20.11+, pnpm 9.12.3 via corepack), `apps/web`
> Next.js 15 App Router with `(public)` / `(studio)/studio` / `(portal)/portal`
> route groups + `api/healthz` BFF + `middleware.ts` (production-guarded host
> trust placeholder), `lib/customization/` runtime resolver against the closed
> ADR-0018 primitive + composite sets, `packages/{config,ui,sdk}`.
> `pnpm-lock.yaml` committed; `postinstall` hook stubs `.next/types/routes.d.ts`.
>
> **Packet 3 — Core dev compose ✅**
> `infra/compose/dev.yml` with PostgreSQL 16, Redis 7, MinIO + console, Mailpit
> (binary `readyz` healthcheck), Meilisearch — pinned tags, healthchecks,
> named volumes, dev-only credential banners.
>
> **Packet 4 — Identity stack ✅**
> Keycloak 26 in dev compose with two realms imported on first boot
> (`learnstack` tenant-facing + `learnstack-hub` operator), each hard-isolated
> per ADR-0004 Amendment 1. Realm seeds at `infra/keycloak/realms/`; Postgres
> init script provisions the Keycloak DB on the first start of the
> `postgres-data` volume.
>
> **Packet 5 — Live media ✅**
> LiveKit OSS v1.8.0 + Coturn 4.6 in dev compose with the dev key/secret pair
> the eventual `ILiveClassProvider` adapter (Phase 08c) will sign tokens with.
> Configs at `infra/livekit/livekit.yaml` and `infra/coturn/turnserver.conf`.
>
> **Packet 6 — Eventing + secrets + gateway ✅**
> Kafka 7.8 in KRaft mode (no ZooKeeper) + kafka-ui, Vault 1.18 in `-dev` mode,
> Dapr 1.14.4 sidecar (`learnstack-api` app id, `-app-channel-address
> host.docker.internal` so subscriptions reach the workstation) + placement
> with three components — `pubsub-kafka.yaml`, `statestore-redis.yaml`
> (`actorStateStore: false` per ADR-0014 non-goals), `secretstore-vault.yaml`
> — and APISIX 3.10 in file-driven standalone mode (`deployment.role:
> data_plane`, no Admin API, no companion dashboard) per ADR-0015. The
> `/api/internal/*` Phase-02c surface is documented as an SSL-object +
> ip-restriction stub (mTLS in APISIX is not a route-level plugin).
>
> **Packet 7 — Developer experience (pending)**
> `Makefile` (`make dev` / `test` / `lint` / `seed`), `.env.example` per app,
> pre-commit hook (dotnet-format + prettier), `infra/compose/e2e.yml`
> companion stack, optional `learnstack-hub` compose overlay.
>
> **Packet 8 — CI baseline + seed (pending)**
> GitHub Actions workflow (backend build + unit + arch + contract +
> Testcontainers integration; frontend install + typecheck + build + lint +
> component; OpenAPI breaking-change check; Lighthouse budget), `make seed`
> with two demo tenants + platform admin, required status checks on `main`.

## Goal

Create a development environment that is repeatable, maintainable, and ready to grow. This phase establishes the project structure, local infrastructure, CI, and engineering workflow. It does not focus on product features.

## Scope

### Repository Structure

```text
learnstack/
  backend/
    src/                      # core projects + Modules/<Name>/{Application,Application.Contracts,Domain,Infrastructure}
    tests/
  frontend/
    apps/
      web/                    # single tenant-facing Next.js app (ADR-0009)
        src/{app, components, lib/customization}
    packages/                 # shared workspace packages (extracted only when duplication is real)
      config/                 # eslint + tsconfig + tailwind presets
      sdk/                    # generated typed API client
      ui/                     # design-system primitives
  infra/
    compose/
    docker/
  docs/
    architecture/
    decisions/
    roadmap/
    standards/
```

There is **no `frontend/extensions/` folder** — tenant-specific renderers
resolve through composite renderer keys against the closed primitive +
composite sets per [ADR-0018](../decisions/0018-tenant-driven-customization-model.md).

### Backend Scaffold

- Create the .NET 10 solution.
- Initial backend projects:
  - LearnStack.Api
  - LearnStack.Application
  - LearnStack.Application.Contracts
  - LearnStack.Domain
  - LearnStack.Infrastructure
  - LearnStack.Infrastructure.Audit
  - LearnStack.SharedKernel
  - LearnStack.Modules.Tenancy.{Application, Application.Contracts, Domain, Infrastructure}
  - LearnStack.Modules.Identity.{...}
  - LearnStack.Modules.Customization.{...}
  - LearnStack.Modules.Audit.{...}
  - LearnStack.Modules.Content.{...}
  - LearnStack.Modules.Media.{...}
  - LearnStack.Modules.Education.{...}
- Test projects (one project per suite per [Standards 06 § Backend Test Types](../standards/06-testing.md);
  module-level classes are namespaced under the suite, e.g. `LearnStack.Tests.Unit.Education`):
  - LearnStack.Tests.Unit
  - LearnStack.Tests.Integration
  - LearnStack.Tests.Architecture
  - LearnStack.Tests.Contract

There is **no `Verticals/` folder**. Tenant-specific shapes are data, not code
([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)). Architecture
test `No_Source_Folder_Named_Verticals` enforces this from this phase onward.

### Frontend Scaffold

**Single Next.js application** at `frontend/apps/web` with route segments separating
concerns ([ADR-0009](../decisions/0009-frontend-single-app-first.md) and
[Frontend Architecture](../architecture/14-frontend-architecture.md)):

- `app/(public)/` — tenant-facing public site.
- `app/(studio)/` — admin and content studio.
- `app/(portal)/` — learner and instructor portal.
- `app/api/` — thin BFF proxies only.
- `components/blocks/`, `components/ui/`.
- `lib/customization/` — runtime resolvers for tenant-defined content types, page
  blocks, lesson item types (the data-not-code surface from
  [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)).

There is **no `extensions/` folder for vertical-provided components**. Per ADR-0018,
tenant-specific renderers are composite renderer keys referenced by
`TenantPageBlock` / `TenantLessonItemType` data; the renderer key resolves to a
built-in composite at runtime.

Shared packages (extracted only when duplication is real):

- `packages/ui` — design-system primitives.
- `packages/sdk` — generated typed API client.
- `packages/config` — shared tsconfig, eslint, tailwind.

Multi-app split inside this repo is deferred; see
[ADR-0009](../decisions/0009-frontend-single-app-first.md) for the extraction
triggers. The **operator portal** (`learnstack-hub-web`) is a *separate Next.js app
in the `learnstack-hub` repository* per
[ADR-0019](../decisions/0019-learnstack-hub.md) — not in this scaffold.

### Local Infrastructure

Docker Compose under `infra/compose/`:

- PostgreSQL 16.
- Redis 7.
- MinIO + MinIO console.
- Mailpit (outbound email).
- Meilisearch.
- LiveKit OSS + Coturn (for in-app classroom development).
- Keycloak (identity development; see
  [Identity and Authentication](../architecture/13-identity-and-auth.md) and
  [ADR-0004](../decisions/0004-authentication-strategy.md)). Two realms seeded:
  `learnstack` + `learnstack-hub`.
- **Kafka** (Dapr pub/sub backend) + kafka-ui.
- **Vault** (Dapr secret store, dev mode).
- **Dapr sidecar** + placement service.
- **APISIX** (standalone YAML-reload mode) + apisix-dashboard (dev only).
- Optional Jaeger or Tempo (for trace inspection).
- Optional `learnstack-hub` compose overlay for local Hub development (depends on
  the same Keycloak / Postgres / Kafka / Vault / APISIX stack).

Two compose files:

- `infra/compose/dev.yml` — services for active development.
- `infra/compose/e2e.yml` — the same stack tuned for end-to-end test runs.

### Developer Experience

- `.env.example` per app.
- `make dev`, `make test`, `make lint`, `make seed`.
- Local setup documentation in `docs/standards/12-infrastructure.md`.
- Health check endpoint (`GET /healthz`) and OpenAPI endpoint (`GET /openapi/v1.json`).
- Pre-commit hook for formatters (dotnet-format, prettier).

### CI Baseline

- GitHub Actions workflow.
- Backend build and unit + architecture + contract tests.
- Integration tests with Testcontainers PostgreSQL.
- Frontend install, typecheck, build, lint, component tests.
- OpenAPI breaking-change check.
- Lighthouse budget check on representative public pages.
- Required status checks on `main`.

## Deliverables

- Working backend solution scaffolded with modular layout.
- Working frontend workspace with the single Next.js app.
- Local Docker Compose infrastructure with PostgreSQL, Redis, MinIO, Mailpit, Meilisearch, LiveKit, Coturn, Keycloak.
- Initial CI pipeline.
- Local development documentation.
- `make seed` populating two demo tenants + one platform admin user.

## Completion Criteria

- A new developer can clone the repository and start the local environment by following one document.
- Backend API responds on `GET /healthz`.
- PostgreSQL, Redis, MinIO, LiveKit, Coturn, Keycloak all run locally via compose.
- Frontend builds and serves the three route segments.
- CI passes on `main`.
- The architecture-test project is set up and green even before domain features exist.

## Technical Notes

- Backend remains in the monorepo initially.
- Backend and frontend deployments can be independent.
- EF Core migrations are part of the workflow from day one.
- Testcontainers used for integration test PostgreSQL.
- LiveKit OSS in compose is the same server used in production (self-hosted by default per [ADR 0005](../decisions/0005-live-classroom-media-stack.md)).
- Keycloak in compose is the same identity provider used in production.

## Risks

- Adding too many tools before the product shape is clear.
- Splitting frontend into multiple apps too aggressively (deferred per [ADR 0009](../decisions/0009-frontend-single-app-first.md)).
- Treating local infrastructure as production infrastructure.
- Delaying CI until implementation grows complex.
- Drifting `infra/compose/dev.yml` from the production deployment shape.
