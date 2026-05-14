# ADR 0003: Tenant Isolation Defense in Depth

## Status

Accepted

## Decision

LearnStack will use defense-in-depth tenant isolation:

- Application tenant context.
- EF Core global query filters.
- PostgreSQL Row Level Security for tenant-owned tables.
- Architecture tests that detect unprotected tenant-owned entities.
- Explicit platform-admin paths for cross-tenant operations.
- Tenant context propagation for background jobs and outbox processing.

## Context

LearnStack uses a shared database and shared schema initially. Query filters alone are not enough because raw SQL, forgotten filters, `IgnoreQueryFilters()`, or background job mistakes can leak tenant data.

## Consequences

- Tenant-owned tables must include `tenant_id`.
- Tenant context must be set per request and per background job.
- Platform-admin operations must be explicit and audited.
- PostgreSQL RLS policies are required before production.
- Tests must fail when tenant-owned entities lack protection.

