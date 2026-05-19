# ADR 0002: Initial Architecture

## Status

Accepted with two amendments — see the bottom of this document for the dated
amendment blocks. Each amendment supersedes a single backend row of the
original Decision section without rewriting the rest of the ADR:

- **Amendment 1 (2026-05-19):** storage backend MinIO → SeaweedFS per
  [ADR-0029](0029-object-storage-seaweedfs.md).
- **Amendment 2 (2026-05-19):** cache + state backend Redis → Valkey per
  [ADR-0030](0030-redis-compatible-store-valkey.md); PostgreSQL major
  version pinned to 18.x per
  [ADR-0031](0031-postgresql-major-version.md).

Every other choice in the Decision section below — .NET 10, ASP.NET Core,
EF Core, modular monolith, Next.js — stands. The Decision + Consequences
text is the original, immutable form per CLAUDE.md's "never edit an
Accepted ADR's decision section" rule; read it together with the
Amendment blocks at the bottom for the current backend choices.

## Decision

LearnStack starts as a modular monolith using .NET 10, ASP.NET Core, Entity Framework Core, PostgreSQL, Redis, MinIO, and Next.js.

## Context

The domain is still being shaped. A modular monolith allows fast iteration while preserving clear module boundaries and future extraction paths.

The team has stronger familiarity with .NET, so .NET 10 is preferred over Go for backend implementation.

## Consequences

- The first backend implementation should use .NET 10.
- EF Core should be the default ORM.
- PostgreSQL should be the primary database.
- Redis should be used for caching and distributed coordination where needed.
- MinIO should be used locally for S3-compatible object storage.
- Next.js should be used for public rendering, admin studio, and portals initially.

---

## Amendment 1 — Storage backend: MinIO → SeaweedFS (2026-05-19)

The original storage row picked MinIO. Two things changed:

1. The `minio/minio` container repository was archived in 2026-04 with no
   ongoing free-tier image stream.
2. MinIO Inc.'s licensing trajectory removed the no-phone-home,
   no-license-key posture that the Self-Hosted Air-Gapped deployment mode
   ([ADR-0020](0020-triple-deployment-hybrid-license.md)) requires.

[ADR-0029](0029-object-storage-seaweedfs.md) records the replacement
decision: SeaweedFS sits behind the same `IStorageProvider` S3-shaped
contract (no code-level adapter contract change), Apache 2.0 licensed,
single-binary self-hostable. The rest of ADR-0002 is unchanged.

Every doc that previously said "MinIO" should be read as "SeaweedFS" for
operational guidance; conceptual rules ("tenant key-prefix isolation",
"swap to AWS S3 in SaaS through `IStorageProvider`") were never
backend-specific and stand verbatim.

## Amendment 2 — Cache: Redis → Valkey; PostgreSQL: pin major to 18.x (2026-05-19)

Two backend-row clarifications recorded together because they share the
trigger (do major-version + vendor calls while LearnStack is still
pre-implementation, so the migration drag is zero):

1. **Cache + state backend Redis → Valkey** per
   [ADR-0030](0030-redis-compatible-store-valkey.md). Redis 7.4 was the
   last BSD-3-Clause Redis release; the 8.x line ships under a
   triple-license (AGPLv3 / RSALv2 / SSPLv1). Valkey is the
   Linux-Foundation-governed BSD-3-Clause fork, drop-in compatible on
   the RESP protocol. The Dapr `state.redis` component name does not
   change — it is the RESP-provider identifier, not a vendor brand.
2. **PostgreSQL major pinned to 18.x** per
   [ADR-0031](0031-postgresql-major-version.md). 18 is the longest-
   runway LTS available (EOL 2030-11), brings native `gen_uuid_v7()`
   that the [ADR-0023 draft](README.md#open-adr-drafts) can adopt
   without an extension, and async I/O for sequential scans helps the
   partitioned `audit_log`
   ([ADR-0016](0016-audit-log-subsystem.md)) operator queries. RLS
   policy syntax + connection-string + role provisioning are unchanged
   from 16 / 17, so the tenant-isolation defense-in-depth pattern
   ([ADR-0003](0003-tenant-isolation-defense-in-depth.md)) transfers
   verbatim.

Every doc that previously said "Redis 7" or "PostgreSQL 16" should be
read as "Valkey 8" or "PostgreSQL 18". Library names + protocol
identifiers stay (`StackExchange.Redis`, `IConnectionMultiplexer`,
`state.redis` Dapr component, RESP) — those are protocol/library
identifiers, not vendor brands.
