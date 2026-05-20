# Local Infrastructure (Docker Compose)

Compose stacks for local development. Operational rules live in
[docs/standards/12-infrastructure.md](../../docs/standards/12-infrastructure.md).

## `dev.yml`

Services in order they appear in `dev.yml` (data plane → identity → media →
eventing → secrets → Dapr sidecar → gateway).

Bring it up with `make dev` from the repo root (the orchestrator copies
`.env.example` → `.env` on first run, so every `${VAR:-default}` reference
in `dev.yml` resolves against the developer's copy). The end-to-end overlay
adds tmpfs volumes for ephemeral test runs — see `e2e.yml` below.

### Data plane (Phase 01 packet 3)

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| PostgreSQL 18 | `postgres:18.4-alpine` | `localhost:5432` | `learnstack` / `learnstack` |
| Valkey 8 | `valkey/valkey:8.1-alpine` | `localhost:6379` | — |
| SeaweedFS | `chrislusf/seaweedfs:3.94` | `localhost:9000` (S3), `localhost:9001` (filer UI), `localhost:9333` (master) | S3 access `learnstack` / secret `learnstack-dev-secret` |
| Mailpit | `axllent/mailpit:v1.29.7` | `localhost:1025` (SMTP), `localhost:8025` (UI) | accepts any auth |
| Meilisearch | `getmeili/meilisearch:v1.44.0` | `localhost:7700` | master key `learnstack-dev-master-key` |

### Identity (Phase 01 packet 4)

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| Keycloak | `quay.io/keycloak/keycloak:26.6.2` | `localhost:8080` | master admin `admin` / `admin-dev-secret` |

Two realms imported on first boot from `infra/keycloak/realms/`:

- `learnstack` (tenant users) — clients `learnstack-api` (confidential) + `learnstack-web` (public PKCE); demo users `demo-admin@tenant-a.test` and `demo-learner@tenant-a.test` (both `demo-dev-secret`).
- `learnstack-hub` (operators) — client `learnstack-hub-web` (public PKCE); demo user `demo-operator@learnstack.test` (`demo-dev-secret`). `CONFIGURE_TOTP` required-action present so the MFA flow surfaces in dev.

See [../keycloak/README.md](../keycloak/README.md) for the realm-isolation
invariant, re-seed procedure, and the Phase 02b/03 wiring notes.

The Postgres init script at `postgres-init/01-create-keycloak-db.sql` creates
the `keycloak` database on the first start of the `postgres-data` volume.
Re-seeding the realm structure requires either `down -v` (wipes all volumes)
or a manual `DROP DATABASE keycloak; CREATE DATABASE keycloak OWNER learnstack;`.

### Live media (Phase 01 packet 5)

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| LiveKit OSS | `livekit/livekit-server:v1.12.0` | `ws://localhost:7880` (signaling), `tcp/7881` (TCP fallback), `tcp/7882` (TURN/TLS), `udp/50000-50100` (media) | API key `devkey` / secret `devsecret-32-byte-min-length-padding-xyz` |
| Coturn | `coturn/coturn:4.11.0` | `udp+tcp/3478` (STUN/TURN), `tcp/5349` (TURN/TLS), `udp/49152-49200` (relay range) | TURN user `devuser` / password `devsecret` |

LiveKit config at `infra/livekit/livekit.yaml`; Coturn config at
`infra/coturn/turnserver.conf`. See [../livekit/README.md](../livekit/README.md)
for the `ILiveClassProvider` integration plan (Phase 08c) + the
recording / consent / cost-tracking story.

### Eventing + secrets + Dapr sidecar + gateway (Phase 01 packet 6)

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| Kafka (KRaft) | `confluentinc/cp-kafka:8.2.1` | `localhost:9092` (in-cluster only — see note below) | none (`PLAINTEXT`, `authType: none`) |
| kafka-ui | `ghcr.io/kafbat/kafka-ui:v1.5.0` | `localhost:8081` | open UI (dev only) |
| Vault | `hashicorp/vault:1.21.4` | `localhost:8200` | root token `learnstack-dev-root-token` |
| Dapr placement | `daprio/placement:1.17.7` | `localhost:50005` | — |
| Dapr sidecar (api) | `daprio/daprd:1.17.7` | `localhost:3500` (HTTP), `localhost:50001` (gRPC) | — |
| APISIX | `apache/apisix:3.16.0-debian` | `localhost:9080` (HTTP), `localhost:9443` (HTTPS), `localhost:9091` (metrics) | none (file-driven standalone — no Admin API) |

Configs:

- **Kafka** runs in KRaft mode (no ZooKeeper); cluster id is pinned so the
  log dir survives restarts without re-format. Only the in-cluster
  `PLAINTEXT://kafka:9092` listener is advertised; host-side tools (kcat,
  kafka-topics from the workstation) will resolve the bootstrap address as
  `kafka:9092` and fail unless `127.0.0.1 kafka` is added to `/etc/hosts`.
  Use `kafka-ui` (`localhost:8081`) for workstation-side browsing; the
  Phase 07 DX packet ships either an EXTERNAL listener or documents the
  `kafka-ui`-only workflow as canonical.
- **Vault** runs in `-dev` mode with the root token baked in — production
  runs HA + auto-unseal + AppRole.
- **Dapr** components live under `infra/dapr/components/`
  (`pubsub-kafka.yaml`, `statestore-redis.yaml`, `secretstore-vault.yaml`);
  runtime config at `infra/dapr/config/dapr-config.yaml`. The sidecar is
  wired to call back to `host.docker.internal:5080` via the
  `-app-channel-address` flag (daprd's default `127.0.0.1` would resolve
  inside the sidecar container and break subscription deliveries). See
  [../dapr/README.md](../dapr/README.md) for the
  `IEventBus` / `ICacheService` / `ISecretProvider` consumption pattern.
- **APISIX** runs in file-driven standalone mode (`deployment.role:
  data_plane`) per ADR-0015 — no etcd, no Admin API, no companion
  dashboard. Main config at `infra/apisix/config.yaml`; route table at
  `infra/apisix/apisix.yaml`. See [../apisix/README.md](../apisix/README.md)
  for the plugin chain, route table walk-through, and the
  `/api/internal/*` mTLS-via-SSL-object placeholder reserved for Phase 02c.

The .NET API host runs OUTSIDE the compose network during active dev; both
the Dapr sidecar and APISIX target `host.docker.internal:5080` so they can
reach the workstation-local `dotnet run` process. The
`host.docker.internal:host-gateway` alias is wired via a YAML anchor in
`dev.yml` so Linux developers don't need a manual override.

```bash
# Repo-root orchestrator (preferred):
make dev                                            # bring stack up
make ps                                             # confirm healthchecks pass
make down                                           # stop, keep volumes
make clean                                          # stop, wipe local data

# Raw compose (equivalent — useful when `make` is unavailable):
docker compose -f infra/compose/dev.yml up -d
docker compose -f infra/compose/dev.yml ps
docker compose -f infra/compose/dev.yml down
docker compose -f infra/compose/dev.yml down -v
```

## `e2e.yml` — end-to-end overlay

Layered on top of `dev.yml` to swap durable named volumes for tmpfs, so
every run starts from a clean Postgres / SeaweedFS / Meilisearch / Kafka.
Images, ports, and credentials are identical to dev — only the
*operational posture* (data persistence + Mailpit retention) changes.

```bash
make e2e-up                                         # tmpfs-backed stack up
make e2e-down                                       # stop; tmpfs evaporates

# Raw equivalent:
docker compose -f infra/compose/dev.yml -f infra/compose/e2e.yml up -d
```

Phase 06 Playwright + Phase 07 SDK contract tests run against this overlay
in CI; the Playwright project itself lives in `frontend/apps/web/e2e/` and
arrives in its owning phase.

### Dev credentials are dev credentials

The shared credentials above are checked into the repo intentionally — they are
**only** acceptable in `Development` deployment mode. Production secrets come
from Vault via `ISecretProvider` (Standards 12 § Secrets Management; Standards
20). Do not reuse these strings anywhere except local Docker.

### Tenant isolation in SeaweedFS S3

Tenant isolation in object storage is enforced by **key prefix**
(`{tenant_id}/...`), never bucket-per-tenant — see Standards 12 § Object
Storage Operations and [docs/architecture/16-media-pipeline.md](../../docs/architecture/16-media-pipeline.md).
A single bucket per environment is created at first use by the
`IStorageProvider` adapter. The rule is backend-independent — it
applied to MinIO, it applies to SeaweedFS, it will apply to any future
S3-compatible swap-in.

See [../seaweedfs/README.md](../seaweedfs/README.md) for the SeaweedFS-
specific dev access surface (filer UI, S3 identity config, re-seed).

## What this file deliberately does NOT bring up yet

Phase 01 is complete; the remaining deferrals belong to later phases and
NOT to this compose stack:

- The .NET API host (`LearnStack.Api`) runs **outside** the compose network
  via `dotnet run` on the developer's workstation. Moving it inside compose
  is a Phase 11 (production hardening) decision — the dapr-sidecar-api
  service is already pointed at `host.docker.internal:5080` so the swap
  is a one-line `upstream` change.
- `livekit-egress` (recording / consent) — Phase 08c.
- OpenTelemetry Collector — Phase 11.
- Application-level tenant seeding via `LearnStack.Tools.Seeder` —
  Phase 02a (the `scripts/seed.sh` orchestrator stubs the activation point).
- Production-grade Vault (HA + auto-unseal + AppRole) — Phase 11.
