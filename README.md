# LearnStack

LearnStack is a core platform for building education products.

The goal is not to build a single LMS. LearnStack is intended to be a multi-tenant, education-aware CMS and platform engine that can power different learning brands, landing pages, catalogs, portals, and domain-specific products such as an online English learning platform.

## Current Direction

- Backend: .NET 10, ASP.NET Core, Entity Framework Core
- Database: PostgreSQL
- Cache: Redis
- Object storage: MinIO / S3-compatible storage
- Frontend: Next.js
- Architecture: Modular monolith with clear module boundaries
- Product model: Core platform + vertical education products

## Documentation

- [Platform Vision](docs/architecture/01-platform-vision.md)
- [Domain Model](docs/architecture/02-domain-model.md)
- [Module Boundaries](docs/architecture/03-module-boundaries.md)
- [Technical Architecture](docs/architecture/04-technical-architecture.md)
- [MVP Scope](docs/architecture/05-mvp-scope.md)
- [Extension Model](docs/architecture/06-extension-model.md)

