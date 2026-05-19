# ADR-0029: Object Storage — SeaweedFS

## Status

Accepted

**Date:** 2026-05-19
**Deciders:** @platform
**Supersedes (partial):** ADR-0002 — Initial Architecture (the "MinIO" choice on
the storage row only; the rest of ADR-0002 stands)

## Decision Drivers

- **Original choice (MinIO) lost its free-tier path.** The MinIO Inc. policy
  shift around the AGPL community edition and the archival of the official
  `minio/minio` container repository in 2026-04 mean there is no supported
  upstream stream of new tagged images for the self-hosted, no-license-server
  posture LearnStack relies on. Continuing on the last free image
  (`RELEASE.2025-09-07T16-13-09Z`) is a frozen-image dead end, not a
  maintainable foundation.
- **Provider portability is non-negotiable.** Per
  [ADR-0002](0002-initial-architecture.md) + [Standards 20 § Composition Root](../standards/20-infrastructure-stack.md),
  every external dependency sits behind a `LearnStack`-defined interface.
  `IStorageProvider` already abstracts the storage adapter — the choice of
  backend behind the interface should be an operational call, not a code
  rewrite.
- **Self-Hosted + Air-Gapped must work.** The triple-deployment model
  ([ADR-0020](0020-triple-deployment-hybrid-license.md)) requires the
  storage backend to ship as a single self-contained binary or container
  with no phone-home, no license-key check, no SaaS dependency.
- **S3 API compatibility is the integration contract.** The `IStorageProvider`
  adapter that the rest of the stack talks to is written against the S3 API
  (signed URLs, multipart upload, key-prefix isolation per
  [Standards 12 § Object Storage Operations](../standards/12-infrastructure.md)).
  Any backend that does not expose a high-fidelity S3 API forces a second
  adapter layer.
- **Day-1 multi-region story is a Phase-11 concern, not Day-1.** The MVP
  scope is single-region; the chosen backend must not paint us into a
  corner if Phase 11 adds replication, but multi-region is not required
  today.
- **Operational footprint must stay one-process-small.** The dev compose
  is already 14 services; a storage backend that introduces three or four
  more processes (or a dedicated control plane) is disproportionate cost.

## Considered Options

1. **SeaweedFS (chosen).** Self-hosted single binary, native S3-compatible
   API, Apache 2.0 license, active upstream, optional volume + filer + S3
   gateway split-or-merged per deployment size.
2. **Stay on the last free MinIO image, indefinitely (rejected).**
   `minio/minio:RELEASE.2025-09-07T16-13-09Z` works today but freezes the
   stack to a no-longer-supported image — including security patches. This
   makes ADR-0002's storage row a long-term tech debt magnet.
3. **Garage S3 (rejected for now).** Rust-based, Apache 2.0, smaller
   feature set. Strong fit for edge / small deployments; weaker
   production-scale story and a smaller operational community. Worth
   revisiting if SeaweedFS develops a blocker.
4. **Ceph RGW (rejected).** Heavy-weight; designed for petabyte clusters
   with a separate operator team. Disproportionate for LearnStack's
   single-binary Self-Hosted posture; production multi-region story is
   solid but is solving a problem we do not yet have.
5. **Cloud-managed S3 only (rejected — incompatible with deployment
   model).** Amazon S3 / R2 / B2 are excellent for SaaS; they directly
   violate Self-Hosted Air-Gapped (ADR-0020). A SaaS-only deployment
   could swap behind `IStorageProvider`, but the *default* must be
   self-hostable.

## Decision

LearnStack adopts **SeaweedFS** as the object-storage backend behind the
`IStorageProvider` adapter for all four deployment modes (Development /
SaaS / Dedicated / SelfHosted). The S3 gateway exposes the same S3 API
surface the MinIO-based adapter relied on, so adapter code remains
unchanged in signature.

Image: `chrislusf/seaweedfs:latest` (pinned to a specific tag per
[Standards 12 § Image Conventions](../standards/12-infrastructure.md);
the dev compose pins the current stable tag).

This ADR **supersedes the storage choice in ADR-0002 only** — the rest of
ADR-0002 (Postgres, Redis, modular monolith) is unchanged.

## Context

The repository accreted ~27 docs that name MinIO directly (Standards 11,
Standards 12, Standards 20, ADR-0002, ADR-0003, ADR-0014, ADR-0017,
ADR-0018, architecture 02 / 03 / 04 / 05 / 07 / 08 / 09 / 16 / 18 / 23 /
25 / 29 / 32, decisions/README). The Day-1 commitment was: provider
portability behind `IStorageProvider`, single-binary self-hostable
backend, S3 API as the integration contract. MinIO satisfied all three at
the time; SeaweedFS satisfies all three today **and** is not on a
license-shift trajectory.

The bulk of the MinIO references describe **storage characteristics that
remain true** — tenant key-prefix isolation (`{tenant_id}/...`),
production swap-out to AWS S3, partition-friendly key layout. Those rules
do not change. The backend name does, and a tightly-scoped doc sweep
plus a runtime config swap close out the migration.

### Why MinIO got us here and why we're moving off

MinIO was the right choice in 2024–early 2026 because it shipped as a
self-hosted binary, exposed a high-fidelity S3 API, and stayed on a
permissive license. The 2026-04 archival of the `minio/minio` Docker Hub
repository plus MinIO Inc.'s pivot to a commercial-first posture removed
the *no-license-server, no phone-home* path the Self-Hosted Air-Gapped
deployment mode (ADR-0020) requires. The platform-level commitment is to
keep that mode first-class; the backend choice must follow.

### Why SeaweedFS

- **Single binary**, multi-process when scale needs it. The dev compose
  runs one container; production can split master / volume / filer / S3
  gateway across replicas.
- **Native S3 gateway** (`weed s3` subcommand) exposing a near-complete
  S3 API surface — signed URLs, multipart upload, bucket policies, key
  versioning. Sufficient for what `IStorageProvider` consumes today.
- **Apache 2.0 license**, no telemetry phone-home, no license key
  required.
- **Active upstream** (commits weekly, stable release cadence).
- **Operational primitives we already need**: tiered storage (hot →
  warm), erasure coding (Phase 11 cost-optimization), built-in metrics
  (Prometheus-compatible).

### Why not Garage

Garage's feature set is tighter (no native erasure coding; smaller
ecosystem). The trade-off is conscious and we re-consider Garage if
SeaweedFS produces a concrete blocker — at that point Garage is one
ADR + one adapter-config swap away.

### Adapter-level impact: none

`IStorageProvider` already abstracts the storage backend behind a S3-
shaped contract (signed URL issuance, multipart upload coordination,
bucket + key access, server-side encryption). The SeaweedFS S3 gateway
implements the subset `IStorageProvider` uses. The composition root in
each deployment mode picks the backend's endpoint + credentials; no
module-level code change is required.

## Consequences

### Positive

- Self-Hosted Air-Gapped stays first-class — no phone-home, no license
  key, no commercial-tier gating.
- The dev compose stays single-container for storage (no operator-
  pattern overhead at dev time).
- Apache 2.0 license matches the rest of LearnStack's OSS posture
  (Standards 20 § Self-Hosted Infrastructure Preferred).
- Production tiered storage + erasure coding give us a clear cost-
  reduction lever in Phase 11 without a backend swap.
- The decision **also** answers "what happens if MinIO Inc. tightens
  licensing further" — we're already off.

### Negative

- One-time doc + scaffold migration: ~27 doc references + the dev
  compose service + the `IStorageProvider` adapter implementation
  (Phase 02a). The adapter change is small because both backends speak
  S3, but the doc sweep is mechanical work.
- SeaweedFS S3 gateway is not 100% S3 surface — a few edge endpoints
  (CloudFront-specific extensions, S3 Object Lambda) do not exist.
  None are on the LearnStack-side dependency list today; flagging so
  Phase 02a's adapter-write does not assume them.
- Smaller operational community than MinIO at its peak. Mitigated by
  active upstream and the architecture-level provider-portability
  commitment — switching backends behind `IStorageProvider` remains a
  composition-root edit.

### Neutral

- The `MINIO_` env-var names in the dev compose become `SEAWEEDFS_` (or
  the equivalent SeaweedFS variables). Names move; semantics do not.
- Production AWS S3 / Cloudflare R2 / Backblaze B2 swap-in continues to
  work through `IStorageProvider`; the dev backend choice is independent
  of the production backend choice.

## Implementation Notes

- Phase 01 packet 6 cleanup (this ADR's commit): the dev compose `minio`
  service is replaced by a `seaweedfs` service exposing the S3 gateway
  on the same `localhost:9000` port (drop-in for any dev URL that
  hardcoded the old endpoint), with the master / volume processes on
  their own internal ports. The MinIO console (port 9001) becomes
  SeaweedFS's filer UI.
- Phase 02a (Storage adapter): `LearnStack.Infrastructure.Storage.SeaweedFS`
  ships the `IStorageProvider` implementation. The signed-URL,
  multipart-upload, and bucket-prefix conventions in
  [Standards 12 § Object Storage Operations](../standards/12-infrastructure.md)
  stay verbatim — the adapter speaks S3 underneath.
- Phase 11 (production): erasure coding policy + tiered storage tuning;
  multi-region replication evaluation; production credential rotation
  (today's dev credentials live in dev compose only).

## References

- [ADR-0002 Initial Architecture](0002-initial-architecture.md) — original storage row, now superseded for this slot.
- [ADR-0020 Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md) — Self-Hosted Air-Gapped requirement that motivated the move.
- [Standards 12 § Object Storage Operations](../standards/12-infrastructure.md)
- [Standards 20 § Self-Hosted Infrastructure](../standards/20-infrastructure-stack.md)
- [architecture/16-media-pipeline.md](../architecture/16-media-pipeline.md) — media + recording storage paths.
- SeaweedFS upstream: <https://github.com/seaweedfs/seaweedfs>.
