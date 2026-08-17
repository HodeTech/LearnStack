---
name: local-dev-setup
description: >
  Bring up the LearnStack local stack — Postgres, Valkey, Vault, Kafka, Dapr
  sidecar, APISIX, Keycloak (two realms), SeaweedFS, LiveKit OSS, Meilisearch — via
  `docker-compose` plus the project's `make dev` orchestrator. USE FOR: first-time
  workstation setup, restoring a broken local environment, switching between
  `DeploymentMode` for testing. DO NOT USE FOR: production deployment (separate
  topic), CI environment configuration (CI uses Testcontainers directly), or
  cloud / managed equivalents (those are deployment-mode-specific composition).
---

# Local development setup

## Purpose

Stand up a full LearnStack stack on a developer workstation so backend + frontend
can run against real Postgres / Valkey / Kafka / Vault / Keycloak / SeaweedFS /
LiveKit / Meilisearch / APISIX — the same components production uses
([12-infrastructure.md § Local Infrastructure](../../../docs/standards/12-infrastructure.md),
[20-infrastructure-stack.md](../../../docs/standards/20-infrastructure-stack.md)).

## When to use

- New workstation; first checkout.
- The local stack is in a broken state (port collisions, stale containers, lost
  volumes).
- You need to test `DeploymentMode.Development` vs `DeploymentMode.SelfHosted`
  locally.
- You want to reproduce a Hub-backed (`SaaS` / `Dedicated`) scenario by pointing
  at a local Hub stack from the `learnstack-hub` repo.

## When not to use

- Production deployment. Topic for ops runbooks, not this skill.
- CI configuration. CI uses Testcontainers via `LearnStack.Tests.Integration`,
  not `make dev`.
- Cloud-managed services (AWS RDS, Confluent Cloud). Composition is similar but
  config differs; out of scope.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Docker Desktop | Yes | Required for every container. |
| .NET 10 SDK | Yes | `dotnet --version` returns `10.0.x`. |
| Node 20+ + pnpm | Yes | For the frontend. |
| Deployment mode | Yes | `Development` (default) / `SaaS` / `Dedicated` / `SelfHostedOnline` / `SelfHostedAirGapped` (per [Standards 12 § Deployment Modes](../../../docs/standards/12-infrastructure.md)). |
| `.env` (gitignored) | Optional | Local overrides; `.env.example` is the source of truth. |

## Workflow

### Step 1: Prereqs

```bash
dotnet --version       # 10.0.x
node --version         # v20+
pnpm --version
docker info >/dev/null && echo "docker OK"
```

If any of these is missing, install:

- .NET 10 SDK: <https://dotnet.microsoft.com/download>
- Node: use Volta or fnm; project's `.nvmrc` pins the version.
- pnpm: `corepack enable && corepack prepare pnpm@latest --activate`.
- Docker Desktop: <https://www.docker.com/products/docker-desktop>.

### Step 2: Clone + restore

```bash
git clone <repo> learnstack
cd learnstack

cp .env.example .env       # creates the local-only file (gitignored)
# Edit .env to override any defaults; for first-run, leave as-is.

dotnet restore --locked-mode
pnpm install --frozen-lockfile
```

### Step 3: Bring up the stack

```bash
make dev      # the canonical orchestrator; wraps docker-compose + dotnet + pnpm
```

Equivalent without the make wrapper:

```bash
docker compose -f infra/compose/docker-compose.yml up -d
docker compose -f infra/compose/dapr.yml up -d
docker compose -f infra/compose/apisix.yml up -d
```

The components and their default ports:

| Component | Port | Purpose |
|-----------|------|---------|
| Postgres | 5432 | Main DB. |
| Valkey | 6379 | Cache + Dapr state. |
| Kafka | 9092 | Dapr pub/sub backend. |
| Kafka UI | 9094 | Optional UI for topics. |
| Vault (dev mode) | 8200 | Secrets backend; `root` token, **not** for production. |
| Keycloak | 8080 | Two realms: `learnstack` (tenants) + `learnstack-hub` (operators). |
| SeaweedFS | 9000 (S3 API) / 9001 (filer UI) / 9333 (master) | Object storage (single dev binary: master + volume + filer + S3 gateway). |
| Meilisearch | 7700 | Search. |
| LiveKit | 7880 (API) / 7881 (TLS) / 7882 (RTC) | Live classroom. |
| coturn | 3478 / 5349 | TURN for LiveKit. |
| LiveKit Egress | — | Recording worker. |
| Mailhog | 8025 | Captures outbound email in dev. |
| OTel Collector | 4317 (gRPC) | Local observability. |
| Dapr sidecar (per service) | 3500 (HTTP) / 50001 (gRPC) | Building-block runtime. |
| APISIX | 9080 (HTTP gateway) / 9443 (HTTPS) / 9091 (Prometheus metrics) | File-driven standalone (`data_plane`) per ADR-0015 — no Admin API, no dashboard companion. `apisix.yaml` is the only source of truth. |
| LearnStack API | 5100 | Backend host. |
| LearnStack Web (`apps/web`) | 3000 | Frontend dev server. |

### Step 4: First-run bootstrap

The first `make dev` run additionally:

1. Applies every module's EF migrations.
2. Seeds Keycloak's `learnstack` realm with a platform admin and two demo tenants
   (each with two organizations + 4 users covering each role).
3. Seeds Keycloak's `learnstack-hub` realm with an operator (`hub-operator`) and a
   billing viewer.
4. Seeds the two demo tenants' customization data (`TenantContentType`,
   `TenantPageBlock`, `TenantLevelTaxonomy`, …) so the page renderer has
   something to render.
5. Creates the SeaweedFS buckets.
6. Creates the Meilisearch indexes.

For a clean re-seed:

```bash
make seed-reset
```

### Step 5: Verify

```bash
# API health
curl -fsS http://localhost:5100/healthz | jq

# APISIX (gateway pass-through)
curl -fsS http://localhost:9080/healthz | jq

# Keycloak realms
open http://localhost:8080/realms/learnstack/account
open http://localhost:8080/realms/learnstack-hub/account

# SeaweedFS filer UI (replaces the MinIO console of the prior stack)
open http://localhost:9001       # S3 access: learnstack / learnstack-dev-secret

# Web app
open http://localhost:3000       # one of the demo tenants

# A second demo tenant (use the Hosts file to alias)
# /etc/hosts: 127.0.0.1 demo-yoga.learnstack.local demo-english.learnstack.local
open http://demo-english.learnstack.local:3000
```

### Step 6: Switch deployment modes locally

Edit `.env` to flip `DEPLOYMENT_MODE`:

| Value | What happens |
|-------|--------------|
| `Development` (default) | `InProcessEventBus` + `InMemoryCacheService` + env vars for secrets. Dapr sidecar is still present but not exercised. |
| `SaaS` | `DaprEventBus` (Kafka) + `DaprCacheService` (Valkey) + `DaprSecretProvider` (Vault) + `HubEntitlementProvider` pointing at the local Hub. Requires the `learnstack-hub` repo's `make dev` to be running. |
| `Dedicated` | Same as `SaaS` for the composition; in practice the Hub is dedicated to one tenant. |
| `SelfHostedOnline` | `HubEntitlementProvider` against the LearnStack-hosted Hub (phone-home daily, 30-day cached-projection grace per ADR-0020). |
| `SelfHostedAirGapped` | `SignedLicenseKeyEntitlementProvider` reads `.lic` from `./secrets/license.lic`; no Hub interaction. |

After changing `.env`, restart the API:

```bash
make restart-api
```

### Step 7: Common troubleshooting

| Symptom | Fix |
|---------|-----|
| `Bind for 127.0.0.1:5432 failed: port is already allocated` | Stop your local Postgres (or change `POSTGRES_PORT` in `.env`). |
| `relation "tenants" does not exist` | Migrations didn't run; `make migrate`. |
| `unable to read app.tenant_id` | The `DbCommandInterceptor` tenant-context guard is unwired, or `TransactionBehavior` did not issue the `SET LOCAL` pair. It is deliberately **not** a connection-checkout interceptor — checkout precedes `BEGIN`. |
| Keycloak realm not found | First-run seed failed; `make seed-reset` rebuilds. |
| Web app shows raw i18n keys | i18n bundle build skipped; `pnpm build:i18n`. |
| Hub-backed mode hangs | The `learnstack-hub` repo's stack isn't up; start it or switch to `Development`. |
| LiveKit join fails with TURN error | coturn not reachable from the browser; check firewall + container network. |

### Step 8: Tear-down

```bash
make dev-down                 # stops containers, keeps volumes
make dev-clean                # stops containers AND removes volumes (lose data)
```

The `dev-clean` target is destructive; only use when you genuinely want a fresh
state.

## Validation

- `make dev` exits 0 and the API responds 200 to `/healthz`.
- The web app loads against a demo tenant's host (either default subdomain or
  Hosts-aliased custom domain).
- Keycloak login works for both realms.
- A test learner can complete a lesson against the seeded English tenant's data.
- `dotnet test backend/tests/LearnStack.Tests.Integration` passes against the
  same containers (the Testcontainers fixture is independent; this is just a
  consistency check).

## Common pitfalls

- **Mixing local Postgres + Testcontainers Postgres.** Both bind 5432 by default.
  Use distinct ports or shut down the dev Postgres before running integration
  tests.
- **Editing `.env.example`.** That file is the **template**; commit changes only
  if the project's default really should change. Your local overrides go in
  `.env` (gitignored).
- **Skipping Dapr.** Even in `Development` mode the sidecar runs (composition
  root falls back to `InProcessEventBus`, but the sidecar is harmless). Don't
  remove it from compose.
- **Hardcoded localhost in code.** Reads URLs from config (`IOptions<HubOptions>`,
  `IOptions<KeycloakOptions>`). Anything else is wrong.
- **Loading secrets from `.env` in production-mode code paths.** `.env` is
  development-only; `SaaS` / `Dedicated` / `SelfHosted` read via
  `ISecretProvider` (Vault).
- **`docker compose down -v` by accident.** That destroys volumes. Use
  `dev-down` (no `-v`) for routine restarts.
