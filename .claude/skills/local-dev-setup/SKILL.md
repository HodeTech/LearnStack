---
name: local-dev-setup
description: >
  Bring up the LearnStack local stack — Postgres, Keycloak (two realms),
  SeaweedFS, LiveKit OSS, Meilisearch, Mailpit, Coturn by default, and Valkey,
  Kafka, kafka-ui, Vault, APISIX and the two Dapr services behind the `gated` profile — via
  `docker-compose` plus the project's `make dev` orchestrator. USE FOR: first-time
  workstation setup, restoring a broken local environment, switching between
  `DeploymentMode` for testing. DO NOT USE FOR: production deployment (separate
  topic), CI environment configuration (integration uses Testcontainers; P02d-7
  owns the compose-backed demo), or
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
- You want to inspect the current deployment-mode composition paths. The Hub
  entitlement adapter is Phase 02c work, not a current local mode dependency.

## When not to use

- Production deployment. Topic for ops runbooks, not this skill.
- CI configuration. Integration uses Testcontainers; P02d-7 separately owns
  the isolated compose-backed demo and browser/audit job.
- Cloud-managed services (AWS RDS, Confluent Cloud). Composition is similar but
  config differs; out of scope.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Docker Desktop | Yes | Required for every container. |
| .NET 10 SDK | Yes | `dotnet --version` returns `10.0.x`. |
| OpenSSL CLI | Yes for frontend tests | Isolated TLS fixtures generate temporary certificates without installing trust. |
| Node >=22.23.1 + pnpm | Yes | For the frontend; `frontend/package.json` sets the floor and CI pins `22.23.1`. |
| Deployment mode | Yes | `Development` (default) / `SaaS` / `Dedicated` / `SelfHostedOnline` / `SelfHostedAirGapped` (per [Standards 12 § Deployment Modes](../../../docs/standards/12-infrastructure.md)). |
| Selected private `.env` (gitignored) | Yes before stack startup | `.env.example` is the template; preserve existing secrets and private file permissions. |

## Workflow

### Step 1: Prereqs

```bash
dotnet --version       # 10.0.x
node --version         # >=22.23.1
pnpm --version
docker info >/dev/null && echo "docker OK"
```

If any of these is missing, install:

- .NET 10 SDK: <https://dotnet.microsoft.com/download>
- Node: use Volta or fnm; `frontend/package.json` requires `>=22.23.1` and CI
  pins `22.23.1`.
- pnpm: `corepack enable`; `frontend/package.json` pins `pnpm@9.12.3`.
- Docker Desktop: <https://www.docker.com/products/docker-desktop>.

### Step 2: Clone + restore

```bash
git clone <repo> learnstack
cd learnstack

make .env                 # creates a missing local-only file with private permissions
# Before make dev, prepare KEYCLOAK_WEB_CLIENT_SECRET in that private file.

(cd backend && dotnet restore LearnStack.slnx --locked-mode)
(cd frontend && pnpm install --frozen-lockfile)
```

Never overwrite an existing private source or regenerate its BFF secret on a
routine rerun. For a legacy source, first verify it is your regular, nonsymlink,
single-link file, then explicitly set `chmod 600 .env` and add a missing key.
The tool refuses shared permissions; Makefile creation does not repair them.
Follow [secret continuity and recovery](../../../infra/keycloak/README.md#secret-continuity-and-recovery)
if the selected source disagrees with an existing confidential client. Keep its
contents out of tool output, command arguments and committed files.

### Step 3: Bring up the stack

```bash
make dev        # the daily loop: 7 services, the ones the backend can call
make dev-gated  # all 14, including Valkey, Kafka, kafka-ui, Vault, APISIX and Dapr
```

`make dev` starts infrastructure only. Apply migrations and seed first:

```bash
make seed
```

P02d-5 Step 1 supplies the paired loopback launcher. Follow
[README Quickstart](../../../README.md#3-prepare-local-https-and-start-the-applications)
for `make public-env`, explicit mkcert trust/leaf creation and manual hosts aliases.
Then run `make public-api` and `make public-web` in separate terminals. The API
helper loads application/dispatcher database credentials and the paired hop
network/secret;
the web launcher captures native socket provenance before Next. Neither script
shell-evaluates `.env`, edits hosts or installs trust. The optional web projection
must match the root private source. README Quickstart owns the
[closed local demo database grammar](../../../README.md#3-prepare-local-https-and-start-the-applications)
and its supported field aliases; it does not promise general Npgsql parsing.

**Ordinary API startup.** `dotnet run` does not load `.env`. For an API-only
session, supply `ConnectionStrings:Default` (`learnstack_app`) and
`ConnectionStrings:OutboxDispatcher` (`learnstack_outbox_admin`) through environment
or the project's `learnstack-api-dev` user-secrets store. The mandatory API worker
resolves its guarded source and verifies dispatcher connectivity at startup.
The dispatcher credential belongs only to the API; every Seeder invocation uses
an explicit application-only environment, and the web launcher receives neither
database credential. Both empty hop lists remain
valid in every mode; a network-only or secret-only configuration is refused.
The application connection must name `learnstack_app`; the data-source guard also
refuses direct or transitive BYPASSRLS/superuser access. Migration credentials
never enter either application launch recipe.

**`make migrate` runs as `learnstack_migration`, not as the API's role.** From
Phase 02a Packet 6 the stack provisions four database roles on the first boot of
a fresh `postgres-data` volume
([ADR-0003 Amendment 3](../../../docs/decisions/0003-tenant-isolation-defense-in-depth.md)):
`learnstack_migration` owns every table, `learnstack_app` is what the API
connects as, and `learnstack_platform` / `learnstack_outbox_admin` hold audited
bypasses. Four roles, four passwords, four connection strings — all in
`.env.example`, and none of them interchangeable. Running migrations as the
runtime role would make it the table owner, which is the arrangement
`FORCE ROW LEVEL SECURITY` exists to defeat.

**A volume created before that packet has no roles**, and nothing says so: init
scripts do not re-run, `make dev` reports healthy, and `make migrate` fails with
`password authentication failed`. Recovery is in
[`infra/compose/README.md`](../../../infra/compose/README.md) — `make clean` then
`make dev`, or apply `02-create-roles.sql` by hand, which is idempotent.

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
  loopback HTTP through `make public-api` (5080), and the native web listener runs
  on loopback HTTPS through `make public-web` (3000).

To inspect selected services without printing resolved credentials, ask the stack:

```bash
docker compose --env-file .env -f infra/compose/dev.yml config --services
```

Do not publish complete resolved Compose configuration: it contains private
environment values.

### Step 4: First-run bootstrap

`make seed` starts the selected stack, waits for PostgreSQL before migrations,
checks all seven default services and both Keycloak realm issuers, and — since
Phase 02a Packet 7 — writes the two
demo tenants. One command from a clean checkout.

[SeedData](../../../backend/src/LearnStack.Tools.Seeder/SeedData.cs) owns the exact
inventory. P02d-2 supplies the two tenants, organizations/host mappings, locales,
built-in and tenant-authored definitions, branding and Course/Lesson content.
[seed-tenant](../seed-tenant/SKILL.md) owns convergence, mismatch handling and future
data scope. A completed rerun is write/audit-neutral; a divergent run fails without
overwriting or resetting authored state. Public reads are delivered by P02d-4;
P02d-6 delivers pages, and P02d-7 starts them with `make demo` after the manual
TLS/hosts prerequisite. Seed alone does not start a renderer.

P02d-7 also supplies isolated CI mode over the same production lifecycle. In a
fresh checkout without root `.env`, web `.env.local` or `artifacts/public-demo`:

```bash
(cd frontend && pnpm install --frozen-lockfile)
(cd frontend && pnpm exec playwright install --with-deps chromium)
make demo DEMO_MODE=ci
```

This creates disposable private TLS/configuration and a unique tmpfs-backed
Compose project, then runs nine browser pages and 27 Lighthouse audits with its
Keycloak stopped. It removes only owned resources and requires successful cleanup.
Follow [run-tests-locally](../run-tests-locally/SKILL.md#public-demo-managed-browser-and-lighthouse)
for fresh artifacts, receipts and prerequisites. Manual hosts/CA trust remains the
human local-mode prerequisite; CI mode does not modify system hosts or trust.

### Step 5: Verify

```bash
# API health
# 5080 is the `http` launch profile's applicationUrl (Properties/launchSettings.json).
curl -fsS http://localhost:5080/healthz | jq

# APISIX (gateway pass-through; only after `make dev-gated` and while the API runs)
curl -fsS http://localhost:9080/healthz | jq

# Keycloak realms
open http://localhost:8080/realms/learnstack/account
open http://localhost:8080/realms/learnstack-hub/account

# SeaweedFS filer UI (replaces the MinIO console of the prior stack)
open http://localhost:9001       # S3 access: learnstack / learnstack-dev-secret

# Web HTTPS readiness after the paired launcher and explicit CA trust.
curl -fsS https://localhost:3000/api/healthz
```

P02d-5 delivers native ingress/TLS, live-host bootstrap and enabled-locale entry.
P02d-6 supplies public page composition and P02d-7 supplies the narrow two-host
browser/Lighthouse proof. TLS socket tests with isolated trust roots remain
separate from browser evidence and P6's recorded manual accessibility checks.

### Step 6: Switch deployment modes locally

For an API-only run, set `Deployment__Mode` in the shell that runs `dotnet run`,
or `Deployment:Mode` in its user-secrets store, to flip the mode. The paired
`make public-api` recipe explicitly selects Development; it is not a deployment
mode switch. Editing `.env` does nothing: it has no
mode key, and `dotnet run` reads no `.env` (Step 3). The committed value is
`Development`, under `Deployment:Mode` in `appsettings.Development.json`, and the
composition root refuses to start without the key rather than defaulting it. The mode
changes the composition paths that already exist, such as error tracking and telemetry.
It does not make the demand-gated Dapr adapters exist early:

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

After changing the mode for an API-only run, stop and rerun that process. Keep its
separately configured runtime credential. For the paired renderer workflow, restart
with `make public-api` so its child-only database and hop configuration is retained:

```bash
# In the paired API terminal, press Ctrl+C, then:
make public-api
```

### Step 7: Common troubleshooting

| Symptom | Fix |
|---------|-----|
| `Bind for 127.0.0.1:5432 failed: port is already allocated` | Stop your local Postgres, or stop the other compose project holding the port — host ports are fixed in `dev.yml`, so two projects cannot both bind them. |
| `relation "tenants" does not exist` | The owning Tenancy migrations have not landed or were not applied; check the active phase plan before adding an ad-hoc target. |
| `unable to read app.tenant_id` | The `DbCommandInterceptor` tenant-context guard is unwired, or `TransactionBehavior` did not issue the `SET LOCAL` pair. It is deliberately **not** a connection-checkout interceptor — checkout precedes `BEGIN`. |
| Keycloak realm not found | Verify the selected project, private source and Keycloak health/import. Fresh realms import on provider startup; a populated realm needs the shared bounded reconciliation from `make seed`. Follow the [realm guide](../../../infra/keycloak/README.md#fresh-import-and-warm-upgrade); do not wipe data as upgrade recovery. |
| `missing_web_secret` / `secret_conflict` | Follow [secret continuity and recovery](../../../infra/keycloak/README.md#secret-continuity-and-recovery); preserve the confidential client and existing accounts. |
| `lock_busy` | Follow [lock recovery](../../../infra/keycloak/README.md#lock-recovery); never delete a lock solely because it is old. |
| A non-Development mode fails startup | Check that mode's required Sentry/telemetry configuration and [current readiness](../../../docs/architecture/25-deployment-models.md#supported-today-versus-prepared-seam). Every mode currently uses `NullEntitlementProvider`; starting Hub does not supply the Phase 02c adapter. |
| LiveKit join fails with TURN error | coturn not reachable from the browser; check firewall + container network. |

### Step 8: Tear-down

```bash
make down                     # stops containers, keeps volumes
make clean                    # stops containers AND removes volumes (lose data)
```

The `clean` target is destructive; only use when you genuinely want a fresh
state.

## Validation

[ADR-0053](../../../docs/decisions/0053-trusted-public-server-rendering.md)
and the [P5 package](../../../docs/roadmap/phase-02d-walking-skeleton.md#p02d-5-decision-package-2026-10-08)
own the accepted host/TLS/transport contract. P02d-7 supplies foreground `make demo`
and CI browser/audit mode. Follow README Quickstart for production-build launch,
owned cleanup, retained local data and stale-lock handling. The packet's Step 4
record and CONTRIBUTING own real CI receipts and required-check activation.

- `make dev` exits 0; after starting the API separately, `/healthz` responds 200.
- After `make dev-gated` and with that API running, APISIX forwards `/healthz`.
- After explicit local trust/leaf setup, the native HTTPS `/api/healthz` returns 200.
- Direct stock Next cannot authenticate forged ingress carriers; ordinary
  `pnpm dev/start` use the mandatory launcher.
- Keycloak login works for both realms.
- `dotnet test backend/tests/LearnStack.Tests.Integration` passes against its
  independently provisioned Testcontainers databases; it does not use the dev
  Compose database. P7's demo proof separately exercises the Compose topology.

## Common pitfalls

- **Confusing compose and test ports.** The dev compose stack publishes loopback
  5432; Testcontainers chooses a random host port. They can coexist. P5's native
  renderer fixtures instead require free 3000/3011 and fail on collision without
  stopping an existing developer process.
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
