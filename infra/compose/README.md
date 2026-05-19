# Local Infrastructure (Docker Compose)

Compose stacks for local development. Operational rules live in
[docs/standards/12-infrastructure.md](../../docs/standards/12-infrastructure.md).

## `dev.yml`

Services in order they appear in `dev.yml` (data plane → identity → media →
eventing+secrets → gateway). Packets 1-4 shipped; 5-6 land in subsequent
Phase-01 packets.

### Data plane (Phase 01 packet 3)

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| PostgreSQL 16 | `postgres:16.6-alpine` | `localhost:5432` | `learnstack` / `learnstack` |
| Redis 7 | `redis:7.4-alpine` | `localhost:6379` | — |
| MinIO | `minio/minio:RELEASE.2025-01-20T14-49-07Z` | `localhost:9000` (S3), `localhost:9001` (console) | `learnstack` / `learnstack-dev-secret` |
| Mailpit | `axllent/mailpit:v1.21` | `localhost:1025` (SMTP), `localhost:8025` (UI) | accepts any auth |
| Meilisearch | `getmeili/meilisearch:v1.11` | `localhost:7700` | master key `learnstack-dev-master-key` |

### Identity (Phase 01 packet 4)

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| Keycloak | `quay.io/keycloak/keycloak:26.0` | `localhost:8080` | master admin `admin` / `admin-dev-secret` |

Two realms imported on first boot from `infra/keycloak/realms/`:

- `learnstack` (tenant users) — clients `learnstack-api` (confidential) + `learnstack-web` (public PKCE); demo users `demo-admin@tenant-a.test` and `demo-learner@tenant-a.test` (both `demo-dev-secret`).
- `learnstack-hub` (operators) — client `learnstack-hub-web` (public PKCE); demo user `demo-operator@learnstack.test` (`demo-dev-secret`). `CONFIGURE_TOTP` required-action present so the MFA flow surfaces in dev.

See [../keycloak/README.md](../keycloak/README.md) for the realm-isolation
invariant, re-seed procedure, and the Phase 02b/03 wiring notes.

The Postgres init script at `postgres-init/01-create-keycloak-db.sql` creates
the `keycloak` database on the first start of the `postgres-data` volume.
Re-seeding the realm structure requires either `down -v` (wipes all volumes)
or a manual `DROP DATABASE keycloak; CREATE DATABASE keycloak OWNER learnstack;`.

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

- **LiveKit OSS** + **Coturn** (Phase 01 packet 5)
- **Kafka** + **kafka-ui** (Dapr pub/sub backend; Phase 01 packet 6)
- **Vault** (Dapr secret store, dev mode; Phase 01 packet 6)
- **Dapr sidecar** + **placement service** (Phase 01 packet 6)
- **APISIX** (standalone YAML-reload) + **apisix-dashboard** (Phase 01 packet 6)

A companion `e2e.yml` (same stack, tuned for end-to-end test runs) is also a
later Phase 01 deliverable (packet 7).
