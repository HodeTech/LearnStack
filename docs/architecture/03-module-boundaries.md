# Module Boundaries

LearnStack starts as a modular monolith. Modules should be independently understandable and have explicit contracts.

## Backend Modules

### Tenancy

Owns tenants, domains, branding, settings, feature flags, and tenant resolution.

### Identity

Owns users, memberships, roles, permissions, invitations, sessions, and security audit events.

### Content

Owns content types, content entries, pages, page versions, page blocks, navigation, redirects, and publication workflow.

### Media

Owns media assets, object storage metadata, file lifecycle, variants, and asset access policies.

### Education

Owns programs, courses, course versions, modules, lessons, learning paths, catalog metadata, and completion rules.

### Assessment

Owns assessments, question banks, questions, attempts, answers, scoring, and result publication.

### Enrollment

Owns learner access, entitlements, cohorts, classrooms, and progress tracking.

### Scheduling

Owns instructor availability, sessions, bookings, attendance, and external live-class meeting references.

### Billing

Owns products, plans, prices, orders, subscriptions, invoice references, and payment provider adapters.

### Notification

Owns notification templates, delivery channels, user preferences, and dispatch orchestration.

### Analytics

Owns event ingestion, learning events, content events, commerce events, and reporting read models.

### Integrations

Owns external provider credentials, webhooks, API keys, LTI/xAPI readiness, and integration lifecycle.

## Dependency Direction

Modules should depend on shared abstractions and public contracts, not each other's database tables.

Allowed patterns:

- Application service contract
- Domain event
- Integration event
- Read model projection
- Explicit module API

Avoid:

- Cross-module EF navigation properties
- Hidden database coupling
- Shared mutable domain entities
- Tenant-specific business rules inside generic core modules

## Suggested Initial Backend Projects

- LearnStack.Api
- LearnStack.Application
- LearnStack.Domain
- LearnStack.Infrastructure
- LearnStack.Modules.Tenancy
- LearnStack.Modules.Identity
- LearnStack.Modules.Content
- LearnStack.Modules.Media
- LearnStack.Modules.Education
- LearnStack.Modules.Assessment
- LearnStack.Modules.Enrollment
- LearnStack.Modules.Scheduling
- LearnStack.Modules.Billing
- LearnStack.Modules.Notifications
- LearnStack.Modules.Analytics
- LearnStack.Modules.Integrations

