---
name: local-dev-setup
description: >
  Bring up the LearnStack local stack — Postgres, Keycloak (two realms),
  SeaweedFS, LiveKit OSS, Meilisearch, Mailpit, Coturn by default, and Valkey,
  Kafka, kafka-ui, Vault, APISIX and the two Dapr services behind the `gated` profile — via
  `docker-compose` plus the project's `make dev` orchestrator. USE FOR: first-time
  workstation setup, restoring a broken local environment, switching between
  `DeploymentMode` for testing. DO NOT USE FOR: production deployment (separate
  topic), CI environment configuration (CI uses Testcontainers directly), or
  cloud / managed equivalents (those are deployment-mode-specific composition).
---

# Local development setup

## Purpose

Stand up a full LearnStack stack on a developer workstation so backend + frontend
can run against real Postgres / Keycloak / SeaweedFS / LiveKit / Meilisearch —
the same components production uses. Valkey, Kafka, kafka-ui, Vault, APISIX and Dapr sit
behind the `gated` profile per
[ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md): nothing
the backend runs today calls them, so `make dev` starts 7 services and
`make dev-gated` starts all 14
([12-infrastructure.md § Local Infrastructure](../../../docs/standards/12-infrastructure.md),
[20-infrastructure-stack.md](../../../docs/standards/20-infrastructure-stack.md)).

## When to use

- New workstation; first checkout.
- The local stack is in a broken state (port collisions, stale containers, lost
  volumes).
- You need to exercise one of the five real deployment-mode values locally:
  `Development`, `SaaS`, `Dedicated`, `SelfHostedOnline`, or
  `SelfHostedAirGapped`.
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
- Node: use Volta or fnm; `frontend/package.json` requires `>=20.11.0` and CI
  pins `20.11.0`.
- pnpm: `corepack enable`; `frontend/package.json` pins `pnpm@9.12.3`.
- Docker Desktop: <https://www.docker.com/products/docker-desktop>.

### Step 2: Clone + restore

```bash
git clone <repo> learnstack
cd learnstack

cp .env.example .env       # creates the local-only file (gitignored)
# Edit .env to override any defaults; for first-run, leave as-is.

(cd backend && dotnet restore LearnStack.slnx --locked-mode)
(cd frontend && pnpm install --frozen-lockfile)
```

### Step 3: Bring up the stack

```bash
make dev        # the daily loop: 7 services, the ones the backend can call
make dev-gated  # all 14, including Valkey, Kafka, kafka-ui, Vault, APISIX and Dapr
```

`make dev` is `docker compose up -d` plus a status line. It does **not** start
the API or the web app, and it does not run migrations or seeds; those are
separate commands you run yourself:

```bash
dotnet run --project backend/src/LearnStack.Api    # API on 5080
pnpm --filter @learnstack/web dev                  # web on 3000
make seed                                          # health gate + demo credentials
```

What `make dev` expands to:

```bash
# One file holds the whole stack. --env-file is not optional: Compose resolves
# its default env file from the project directory (infra/compose/), not the cwd,
# so without the flag the repo-root .env is ignored and every ${VAR:-default}
# silently falls back.
docker compose --env-file .env -f infra/compose/dev.yml up -d
```

The canonical service inventory — image, local endpoint and default credentials
for every service in the stack — lives in
[`infra/compose/README.md`](../../../infra/compose/README.md), grouped by the
Phase 01 packet that introduced each one. It is not repeated here; a second copy
is a second thing to keep true, and this file has no way to notice when
`dev.yml` changes.

Three properties of that inventory matter while you are setting up:

- **Every published port binds `127.0.0.1`, never `0.0.0.0`** — see
  [Infrastructure Standards § Published ports](../../../docs/standards/12-infrastructure.md).
- **Kafka and the Dapr placement service publish nothing.** They are reached
  over the compose network only; nothing on the host speaks to them directly.
- **Seven of the fourteen do not start by default.** Valkey, Kafka, kafka-ui,
  Vault, APISIX and the two Dapr services sit behind the `gated` compose
  profile: nothing the backend runs today calls any of them, and their adapters
  land in Phase 11 against written triggers
  ([ADR-0035](../../../docs/decisions/0035-demand-gated-infrastructure.md)).
  `make down` and `make clean` stop the gated ones too — a profile-less teardown
  silently leaves them running, which is why every teardown target carries
  `--profile '*'`.
- **Neither application host is a compose service.** `LearnStack.Api` runs on
  the workstation via `dotnet run` on the `ASPNETCORE_URLS` port in
  `.env.example` (5080), and `apps/web` runs via `pnpm dev` on 3000.

To read the resolved truth rather than any document, ask the stack:

```bash
docker compose --env-file .env -f infra/compose/dev.yml config --format json
```

### Step 4: First-run bootstrap

`make seed` verifies the stack is healthy and prints the demo credentials. The
steps below are its **Phase 02a** scope — `scripts/seed.sh` carries them as a
documented placeholder and does not run them yet:

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

There is no separate reset target today. If the placeholder seed must be rerun
against fresh local data, use the destructive `make clean`, then `make seed`.

### Step 5: Verify

```bash
# API health
# 5080 is ASPNETCORE_URLS in .env.example - the single source of truth for it.
curl -fsS http://localhost:5080/healthz | jq

# APISIX (gateway pass-through; only after `make dev-gated` and while the API runs)
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

Edit `.env` to flip `DEPLOYMENT_MODE`. This changes the composition paths that
already exist, such as error tracking and telemetry. It does not make the
demand-gated Dapr adapters exist early:

| Value | What happens |
|-------|--------------|
| `Development` (default) | Current fully wired local mode; no network telemetry or external error tracker. |
| `SaaS` | Current SaaS composition paths, including Sentry/OTLP when configured. Hub entitlement wiring arrives in its owning phase. |
| `Dedicated` | A prepared composition seam, not an end-to-end supported deployment until its Phase 11 integration suite exists. |
| `SelfHostedOnline` | A prepared composition seam; phone-home and signed-license entitlement wiring land in their owning phases. |
| `SelfHostedAirGapped` | Current composition suppresses network telemetry and uses local-file error tracking; full air-gapped entitlement wiring remains phase-owned. |

For **every** value today, the three demand-gated ports still resolve to
`InProcessEventBus`, `InMemoryCacheService`, and
`ConfigurationSecretProvider`. `DaprEventBus`, `DaprCacheService`, and
`DaprSecretProvider` land in Phase 11 only after their ADR-0035 triggers fire.

After changing `.env`, stop and rerun the API process:

```bash
# In the terminal running `dotnet run`, press Ctrl+C, then:
dotnet run --project backend/src/LearnStack.Api
```

### Step 7: Common troubleshooting

| Symptom | Fix |
|---------|-----|
| `Bind for 127.0.0.1:5432 failed: port is already allocated` | Stop your local Postgres, or stop the other compose project holding the port — host ports are fixed in `dev.yml`, so two projects cannot both bind them. |
| `relation "tenants" does not exist` | The owning Tenancy migrations have not landed or were not applied; check the active phase plan before adding an ad-hoc target. |
| `unable to read app.tenant_id` | The `DbCommandInterceptor` tenant-context guard is unwired, or `TransactionBehavior` did not issue the `SET LOCAL` pair. It is deliberately **not** a connection-checkout interceptor — checkout precedes `BEGIN`. |
| Keycloak realm not found | Recreate local data with destructive `make clean`, then `make seed`; `scripts/seed.sh` is still a Phase 02a placeholder today. |
| Web app shows raw i18n keys | i18n bundle build skipped; `pnpm build:i18n`. |
| Hub-backed mode hangs | The `learnstack-hub` repo's stack isn't up; start it or switch to `Development`. |
| LiveKit join fails with TURN error | coturn not reachable from the browser; check firewall + container network. |

### Step 8: Tear-down

```bash
make down                     # stops containers, keeps volumes
make clean                    # stops containers AND removes volumes (lose data)
```

The `clean` target is destructive; only use when you genuinely want a fresh
state.

## Validation

- `make dev` exits 0; after starting the API separately, `/healthz` responds 200.
- After `make dev-gated` and with that API running, APISIX forwards `/healthz`.
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
- **Expecting Dapr in the daily loop.** `make dev` deliberately omits the gated
  sidecar. Use `make dev-gated` only when inspecting the future adapter stack;
  the backend still resolves the in-process/default ports today.
- **Hardcoded localhost in code.** Reads URLs from config (`IOptions<HubOptions>`,
  `IOptions<KeycloakOptions>`). Anything else is wrong.
- **Assuming a non-development enum value selects Vault today.** All modes still
  resolve `ConfigurationSecretProvider`; the Vault-backed adapter is Phase 11
  work and must not be claimed before it is wired and tested.
- **`docker compose down -v` by accident.** That destroys volumes. Use
  `down` (no `-v`) for routine restarts.
