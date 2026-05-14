# Technical Architecture

## Recommended Stack

- Backend: .NET 10, ASP.NET Core Web API
- Language: C#
- ORM: Entity Framework Core
- Database: PostgreSQL
- Cache: Redis
- Object storage: MinIO locally, S3-compatible storage in production
- Background jobs: Hangfire or Quartz.NET
- Search: Meilisearch first, OpenSearch later if complexity demands it
- Frontend: Next.js
- Infrastructure: Docker Compose locally, container-based deployment in production
- Observability: OpenTelemetry, structured logs, Sentry or similar error tracking

## Architecture Style

Start with a modular monolith.

This gives the project:

- Faster local development.
- Simpler deployment.
- Stronger refactoring ability while the domain is still forming.
- Clear extraction paths for future services.

Potential future service candidates:

- Billing
- Notifications
- Analytics
- Search indexing
- Media processing

## Backend Layering

Recommended layering:

- Api: HTTP endpoints, auth middleware, request binding, OpenAPI.
- Application: use cases, commands, queries, validation, transactions.
- Domain: entities, value objects, domain services, domain events.
- Infrastructure: EF Core, Redis, MinIO, email/SMS providers, external adapters.
- Modules: bounded feature areas with their own application/domain/infrastructure internals.

## Database Strategy

Use PostgreSQL as the source of truth.

Initial multi-tenancy approach:

- Shared database.
- Shared schema.
- TenantId on tenant-owned tables.
- Strong query filters and application-level tenant enforcement.

Later options:

- PostgreSQL Row Level Security for stronger database-side isolation.
- Schema-per-tenant for selected enterprise tenants if needed.
- Read replicas and reporting projections as scale grows.

## API Strategy

Start with REST APIs.

Use:

- OpenAPI for documentation.
- Problem Details for error responses.
- Cursor pagination for list endpoints.
- Idempotency keys for payment and webhook-sensitive operations.
- Optimistic concurrency for versioned content.

GraphQL can be considered later for content rendering, but should not be the first dependency unless frontend requirements clearly demand it.

## Frontend Strategy

Use Next.js for:

- Public tenant websites.
- SEO-oriented landing pages.
- Tenant-aware page rendering.
- Admin/content studio.
- Learner and instructor portal, at least initially.

Possible structure:

- apps/web: public tenant renderer
- apps/studio: admin/content studio
- apps/portal: learner and instructor portal
- packages/ui: shared UI primitives
- packages/sdk: typed API client

## Local Infrastructure

Local development should run with Docker Compose:

- PostgreSQL
- Redis
- MinIO
- Optional Meilisearch
- Optional mail catcher

Application projects should run outside containers during active development unless containerized development proves more convenient.

