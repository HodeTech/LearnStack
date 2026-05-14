# Phase 02a: Platform Kernel and Multi-Tenancy

## Goal

Build the runtime foundation everything else stands on: shared kernel conventions, tenant resolution, tenant isolation defense-in-depth, database conventions, API conventions, and architecture tests. This is the half of the foundation that is least sensitive to identity and event-infrastructure choices.

Phase 02b (events, outbox, identity integration) follows; the two phases were originally a single Phase 02 and were split to reduce single-point-of-failure risk and keep each half independently mergeable.

The decisions made in this phase are the ones that are most painful to reverse later. They are codified in:

- [ADR 0003 — Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
- [ADR 0007 — Documentation Language and Conventions](../decisions/0007-documentation-language-and-conventions.md)

## Scope

### Shared Kernel

- Base entity and aggregate concepts.
- Strongly typed identifiers (UUIDv7-backed, with EF value converters).
- Auditable fields: `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `DeletedAt`, `DeletedBy`, `Version`.
- Soft delete strategy + EF global query filter.
- Optimistic concurrency strategy.
- Domain event model (in-process MediatR-style) — wired but consumed only inside modules; the cross-module outbox lives in Phase 02b.
- Result and error model (`Result<T>`).
- Pagination model (cursor-first).
- `IClock`, `IRandom`, `IGuidFactory` for deterministic tests.

### Tenant Resolution

Tenant context is resolvable from:

- Custom domain.
- Subdomain on the platform domain.
- Explicit tenant selector header for admin/studio usage.
- API request header (`X-Tenant-Id`).
- Background job parameter.
- Integration event envelope (envelope contract defined in Phase 02b; the resolver respects it from the start).

Implementation:

- Tenant registry.
- Hostname → tenant mapping.
- `TenantResolverMiddleware`.
- `ITenantContext` (request-scoped).
- Tenant-aware query conventions.
- Tenant context propagation seams for Hangfire jobs and outbox dispatcher handlers (wired in 02b).

### Tenant Isolation — Defense in Depth

Implemented **from day one**, not deferred to hardening. Two enforcement layers, both required:

1. **EF Core global query filters** on every entity implementing `ITenantOwned`.
2. **PostgreSQL Row Level Security** policies on every tenant-owned table, with `app.tenant_id` session variable set per connection lease via a `DbConnectionInterceptor`. Transaction-local `set_config(..., true)` is the primitive.

Platform-admin cross-tenant access is explicit, scoped, audited (`EnterPlatformAdminScope(reason)`). See [Tenant Isolation](../architecture/09-tenant-isolation.md).

### Database Conventions

- Naming, indexing, migration, JSONB, soft-delete, audit, concurrency rules per [Database Standards](../standards/05-database.md).
- Required columns and RLS policy on every tenant-owned table; verified by architecture tests.
- Migrations append-only after merge; destructive changes go through a deprecation window.

### API Conventions

Per [API Standards](../standards/04-api-design.md):

- REST + URL versioning (`/v1/...`).
- Problem Details (RFC 7807) for errors.
- Cursor pagination.
- Idempotency keys for write endpoints with external side effects.
- Optimistic concurrency via ETag / `version`.
- Correlation IDs in headers and logs.
- OpenAPI generated from code; SDK generated from spec.

### Configuration

- Strongly typed options bound from configuration providers.
- Environment-based configuration (dev / staging / prod).
- Secret handling — never in source; environment variables or a secret store.
- Tenant-level settings model with a typed accessor.

### Architecture Tests (initial set)

The architecture test project starts going green during this phase. Phase 02a covers:

- Module dependency direction.
- No cross-module Domain/Infrastructure references.
- Every `[TenantOwned]` entity has filter and RLS policy.
- No `IgnoreQueryFilters()` outside platform-admin module.
- Audit-coverage matrix file exists per module.

The event/outbox-specific tests (serialisable records, job payloads with `TenantId`) land in Phase 02b.

## Deliverables

- Shared kernel package.
- Tenant-aware API foundation with both EF filters and PostgreSQL RLS active.
- Database conventions implemented and enforced.
- API conventions wired (versioning, Problem Details, cursor pagination, idempotency, ETag).
- Architecture test project running with the Phase-02a rules.
- Tenant context tests passing for at least two seed tenants.

## Completion Criteria

- A request reliably resolves its tenant.
- Unknown tenants return 404 (no platform disclosure).
- Tenant-owned queries cannot leak across tenants — verified by integration test pair (`Tenant_A_cannot_read_Tenant_B_data`, `Unsetting_tenant_context_returns_zero_rows_through_RLS`).
- API errors use Problem Details consistently.
- Platform-admin scope writes an audit event (audit aggregate is wired here; cross-system audit lands in 02b).
- Architecture tests for tenant ownership, RLS, and module-boundary direction are not skippable.

## Risks

- Leaving tenant enforcement to developer discipline; mitigated by RLS + architecture tests.
- Treating RLS as optional "later" hardening — explicitly rejected by ADR 0003.
- Premature event/outbox work before tenant isolation is stable — moved to Phase 02b on purpose.

## Phase Exit Decision

Phase 02b can begin when tenant resolution, isolation tests, API conventions, and the architecture test gate are stable and green in CI.
