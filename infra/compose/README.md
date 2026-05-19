# Local Infrastructure (Docker Compose)

Compose stacks for local development. Operational rules live in
[docs/standards/12-infrastructure.md](../../docs/standards/12-infrastructure.md).

## `dev.yml`

Services in order they appear in `dev.yml` (data plane → identity → media →
eventing → secrets → Dapr sidecar → gateway). Packets 1-6 shipped; packets
7-8 (DX orchestrator + CI) remain.

### Data plane (Phase 01 packet 3)

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| PostgreSQL 16 | `postgres:16.14-alpine` | `localhost:5432` | `learnstack` / `learnstack` |
| Redis 7 | `redis:7.4-alpine` | `localhost:6379` | — |
| MinIO | `minio/minio:RELEASE.2025-01-20T14-49-07Z` | `localhost:9000` (S3), `localhost:9001` (console) | `learnstack` / `learnstack-dev-secret` |
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
| kafka-ui | `ghcr.io/kafbat/kafka-ui:latest` | `localhost:8081` | open UI (dev only) |
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
docker compose -f infra/compose/dev.yml up -d
docker compose -f infra/compose/dev.yml ps          # confirm healthchecks pass
docker compose -f infra/compose/dev.yml down        # stop, keep volumes
docker compose -f infra/compose/dev.yml down -v     # stop, wipe local data
```

### Dev credentials are dev credentials

The shared credentials above are checked into the repo intentionally — they are
**only** acceptable in `Development` deployment mode. Production secrets come
from Vault via `ISecretProvider` (Standards 12 § Secrets Management; Standards
20). Do not reuse these strings anywhere except local Docker.

### Tenant isolation in MinIO

Tenant isolation in object storage is enforced by **key prefix**
(`{tenant_id}/...`), never bucket-per-tenant — see Standards 12 § Object
Storage Operations and [docs/architecture/16-media-pipeline.md](../../docs/architecture/16-media-pipeline.md).
A single bucket per environment is created at first use.

## What this file deliberately does NOT bring up yet

Per the [Phase 01 plan](../../docs/roadmap/phase-01-repository-tooling.md),
later packets land:

- `Makefile` (`make dev` / `test` / `lint` / `seed`), `.env.example` per app,
  pre-commit hook (dotnet-format + prettier), and `infra/compose/e2e.yml`
  companion stack — Phase 01 packet 7.
- GitHub Actions CI workflow + `make seed` populating two demo tenants and a
  platform admin — Phase 01 packet 8.
