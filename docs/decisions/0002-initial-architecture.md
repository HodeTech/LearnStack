# ADR 0002: Initial Architecture

## Status

Accepted (Amendment 1: 2026-05-19 — storage backend changed from MinIO to
SeaweedFS per [ADR-0029](0029-object-storage-seaweedfs.md); see Amendment at
the bottom of this document. Every other choice in this ADR — .NET 10,
ASP.NET Core, EF Core, PostgreSQL, Redis, Next.js, modular monolith — stands.)

## Decision

LearnStack starts as a modular monolith using .NET 10, ASP.NET Core, Entity Framework Core, PostgreSQL, Redis, SeaweedFS (see Amendment 1), and Next.js.

## Context

The domain is still being shaped. A modular monolith allows fast iteration while preserving clear module boundaries and future extraction paths.

The team has stronger familiarity with .NET, so .NET 10 is preferred over Go for backend implementation.

## Consequences

- The first backend implementation should use .NET 10.
- EF Core should be the default ORM.
- PostgreSQL should be the primary database.
- Redis should be used for caching and distributed coordination where needed.
- SeaweedFS should be used locally for S3-compatible object storage (Amendment 1).
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
