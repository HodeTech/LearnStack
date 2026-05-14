# ADR 0002: Initial Architecture

## Status

Accepted

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

