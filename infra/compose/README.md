# Local Infrastructure (Docker Compose)

Compose stacks for local development. Operational rules live in
[docs/standards/12-infrastructure.md](../../docs/standards/12-infrastructure.md).

## `dev.yml` (this packet)

Core data-plane services every backend dev needs from day one:

| Service | Image | Local endpoint | Default credentials |
|---------|-------|----------------|---------------------|
| PostgreSQL 16 | `postgres:16.6-alpine` | `localhost:5432` | `learnstack` / `learnstack` |
| Redis 7 | `redis:7.4-alpine` | `localhost:6379` | — |
| MinIO | `minio/minio:RELEASE.2025-01-20T14-49-07Z` | `localhost:9000` (S3), `localhost:9001` (console) | `learnstack` / `learnstack-dev-secret` |
| Mailpit | `axllent/mailpit:v1.21` | `localhost:1025` (SMTP), `localhost:8025` (UI) | accepts any auth |
| Meilisearch | `getmeili/meilisearch:v1.11` | `localhost:7700` | master key `learnstack-dev-master-key` |

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

## What this file deliberately does NOT bring up

Per the [Phase 01 plan](../../docs/roadmap/phase-01-repository-tooling.md),
later packets land:

- **Keycloak** (two realms: `learnstack` + `learnstack-hub`)
- **LiveKit OSS** + **Coturn**
- **Kafka** + **kafka-ui** (Dapr pub/sub backend)
- **Vault** (Dapr secret store, dev mode)
- **Dapr sidecar** + **placement service**
- **APISIX** (standalone YAML-reload) + **apisix-dashboard**

These add their own healthchecks and volumes when they ship; this file stays
small in the interim to keep `make dev` cold-start fast.

A companion `e2e.yml` (same stack, tuned for end-to-end test runs) is also a
later Phase 01 deliverable.
