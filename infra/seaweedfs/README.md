# SeaweedFS (Dev)

Self-hosted S3-compatible object storage per
[ADR-0029](../../docs/decisions/0029-object-storage-seaweedfs.md). The .NET
app talks to SeaweedFS through `IStorageProvider`; the SeaweedFS SDK is
never imported by any module — only by
`LearnStack.Infrastructure.Storage.SeaweedFS` (ships Phase 02a). The dev
container packs master + volume + filer + S3 gateway in one binary; the
production topology splits them per ADR-0029 § Implementation Notes.

## Access

| Endpoint | Address | Purpose |
|----------|---------|---------|
| S3 API gateway | `http://localhost:9000` | The endpoint `IStorageProvider` talks to (drop-in port-map for the previous MinIO S3 endpoint) |
| Filer / volume UI | `http://localhost:9001` | Bucket browser, replaces the MinIO console |
| Master HTTP API | `http://localhost:9333` | Cluster topology + health (`/cluster/healthz`) |
| Volume HTTP API | `http://localhost:8080` | Internal — read / write blob ops |

## Dev credentials

| Surface | Credential |
|---------|------------|
| S3 access key | `learnstack` |
| S3 secret key | `learnstack-dev-secret` |

Both come from `infra/seaweedfs/s3-identities.json`, mounted read-only at
`/etc/s3.json`. Production loads identities from Vault via
`ISecretProvider` per Standards 12 § Secrets Management; the literals
above are dev-only.

The credential pair intentionally matches the prior MinIO defaults so any
test fixture, env var, or local script that already hard-coded
`learnstack` / `learnstack-dev-secret` keeps working through the swap.

## Tenant isolation pattern (unchanged)

Tenant isolation in object storage is enforced by **key prefix**
(`{tenant_id}/...`), never bucket-per-tenant — see
[Standards 12 § Object Storage Operations](../../docs/standards/12-infrastructure.md)
and [docs/architecture/16-media-pipeline.md](../../docs/architecture/16-media-pipeline.md).
A single bucket per environment is created at first use by the
`IStorageProvider` adapter at startup.

## Re-seed / wipe

```bash
docker compose -f infra/compose/dev.yml down seaweedfs
docker volume rm learnstack-dev_seaweedfs-data
docker compose -f infra/compose/dev.yml up -d seaweedfs
```

## What does NOT live here

- The `IStorageProvider` adapter implementation —
  `LearnStack.Infrastructure.Storage.SeaweedFS` (Phase 02a).
- Production-mode topology (split master / volume / filer / S3 gateway
  containers, erasure-coding policy, tiered storage) — Phase 11.
- Multi-region replication evaluation — Phase 11+.
- The MinIO-era image (`minio/minio:RELEASE.2025-01-20T...`) — removed
  in this packet per ADR-0029.
