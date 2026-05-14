# Glossary

This glossary defines LearnStack-specific terms. When a term is ambiguous across the industry, this document is the source of truth for how LearnStack uses it.

## Platform & Tenancy

| Term | Definition |
|------|------------|
| **LearnStack** | The core platform engine. Not a single product, not a single LMS. The reusable foundation that hosts education products. |
| **Tenant** | A logical education platform or brand running on LearnStack. One LearnStack deployment can host many tenants. Each tenant has its own domain, branding, content, courses, and members. |
| **Brand** | Synonym for Tenant in product-facing language. In code, prefer `Tenant`. |
| **Platform Admin** | A LearnStack operator who manages tenants, plans, infrastructure-level settings. Operates above tenants. |
| **Tenant Admin** | A user with administrative rights inside a single tenant. Cannot see other tenants. |
| **Vertical Product** | A domain-specific product built on the LearnStack core. Examples: English-learning, exam preparation, corporate academy. |
| **Core** | The reusable platform layer. Does not contain vertical-specific business rules. |

## Identity & Membership

| Term | Definition |
|------|------------|
| **User** | A person known to LearnStack at the global level. Identified by a stable user id. |
| **Membership** | The relationship between a user and a tenant. A user can have memberships in multiple tenants with different roles. |
| **Role** | A named bundle of permissions inside a tenant. Examples: `tenant-admin`, `editor`, `instructor`, `learner`. |
| **Permission** | A fine-grained capability such as `course.publish` or `media.upload`. |
| **Invitation** | A pending offer for a user to accept a membership in a tenant. |

## Content & Pages

| Term | Definition |
|------|------------|
| **Content Type** | A schema definition for structured content (e.g. `BlogPost`, `Testimonial`). Defined by a tenant. |
| **Content Entry** | An instance of a content type. |
| **Page** | A public URL surface owned by a tenant. Has a slug, SEO metadata, and an ordered set of blocks. |
| **Page Version** | A draft or published snapshot of a page. |
| **Page Block** | A typed, composable unit inside a page (Hero, RichText, CourseList, etc.). |
| **Block Schema** | The JSON shape that a block expects. Versioned. |
| **Navigation Menu** | A named tree of links rendered by the public site (header, footer, sidebar). |

## Education & Learning

| Term | Definition |
|------|------------|
| **Program** | A higher-level grouping of related courses or learning paths. |
| **Course** | A learning product listed in a tenant catalog. Identified by a stable id. |
| **Course Version** | A versioned, publishable structure of modules and lessons attached to a Course. Enrollments target a specific version. |
| **Module** | An ordered grouping of lessons inside a course version. |
| **Lesson** | A unit of learning consumption inside a module. |
| **Lesson Item** | A single piece inside a lesson: rich text, video, file, quiz reference, live-session reference, embedded tool. |
| **Learning Path** | An ordered or conditional traversal across multiple courses or lessons. |
| **Completion Rule** | A rule that determines when a lesson, module, or course is considered complete. |

## Enrollment & Access

| Term | Definition |
|------|------------|
| **Enrollment** | A learner's grant of access to a specific course (and specific course version). |
| **Entitlement** | A user's right to access a paid or assigned capability. Enrollment is one source of entitlements; billing is another. |
| **Cohort** | A group of learners progressing through the same course version on a shared timeline. Cohorts may have scheduled live sessions. |
| **Progress** | The learner's recorded advancement against the structure of a course version. |

## Live Classroom

| Term | Definition |
|------|------------|
| **Live Session** | A scheduled live event in which one or more participants meet inside the LearnStack classroom. Owns time, participants, materials, attendance. |
| **Live Booking** | A reservation tying a learner (or cohort) to a Live Session. |
| **Live Room** | The runtime media room provisioned by a live-class provider. Lives for the duration of a Live Session. |
| **Live Room Provider** | The backing implementation of `ILiveClassProvider` that creates rooms and tokens (e.g. self-hosted LiveKit, LiveKit Cloud, Daily). |
| **Live Room Token** | A short-lived join token issued by the provider, scoped to a user, a room, and a role. |
| **Live Attendance** | A computed or recorded record of who joined a Live Session, for how long, and in what role. |
| **Live Session Material** | A file, link, or content entry attached to a Live Session and visible inside the classroom. |
| **Live Session Event** | An append-only event emitted during a Live Session (join, leave, screen-share start, recording start, etc.). |
| **Live Recording** | Metadata for a recording produced by the provider's egress pipeline. The file lives in MinIO / S3; LearnStack stores the metadata and consent state. |

> **Cohort vs. Classroom vs. Live Session.** Cohort is a *group of people*. Live Session is a *scheduled event*. Live Room is the *runtime artifact* of a Live Session. Earlier drafts used `Classroom` for both group and runtime; the term `Classroom` is deprecated in favor of the explicit `Cohort` / `Live Session` / `Live Room` split.

## Assessment

| Term | Definition |
|------|------------|
| **Assessment** | A quiz, exam, placement test, or survey definition. |
| **Question Bank** | A reusable collection of questions. |
| **Question** | A single prompt with an answer definition. |
| **Attempt** | A learner's session against an assessment. |
| **Attempt Answer** | A submitted answer inside an attempt. |
| **Score** | The computed result of an attempt. |

## Billing

| Term | Definition |
|------|------------|
| **Product** | A sellable platform item. |
| **Plan** | A package or subscription definition referencing one or more products. |
| **Price** | A currency / interval / amount combination attached to a plan. |
| **Order** | A purchase intent and lifecycle record. |
| **Subscription** | A recurring access grant. |
| **Invoice Reference** | A pointer to an external invoice or payment record. |
| **Payment Provider Account** | Per-tenant configuration of an upstream payment provider. |

## Events & Analytics

| Term | Definition |
|------|------------|
| **Domain Event** | An event raised inside one module to express that a meaningful change happened in its aggregate. Stays inside the module. |
| **Integration Event** | An event published outward via the outbox so that other modules or external consumers can react. |
| **Outbox** | The transactional buffer that turns domain changes into integration events without dual-write inconsistency. |
| **Learning Event** | An analytics event describing learner behavior (lesson viewed, assessment completed). |
| **Commerce Event** | An analytics event describing the commerce funnel. |
| **Classroom Event** | An analytics event derived from `LiveSessionEvent` streams. |

## Multi-tenancy

| Term | Definition |
|------|------------|
| **Tenant-owned table** | A database table that holds rows scoped to a single tenant. Has a `tenant_id` column and is protected by a global query filter and (later) RLS policy. |
| **Global table** | A database table that lives above tenants (e.g. `tenants`, `users`, `plans`). |
| **Tenant context** | The ambient resolved tenant for a request, job, or background task. |
| **Query filter** | EF Core global query filter that injects `WHERE tenant_id = @current_tenant_id` automatically. |

## Extension Model

| Term | Definition |
|------|------------|
| **Extension Point** | A documented hook in the core (event subscription, block registration, content-type registration, provider adapter slot) where a vertical product can attach. |
| **Provider Adapter** | A concrete implementation of an infrastructure-side interface (payment, live-class, search, storage, email, SMS). |
| **Domain Extension** | A vertical-specific entity or workflow (e.g. CEFR level, placement-test scoring). |
| **Content Extension** | A vertical-specific content type or page block. |
| **UI Extension** | A vertical-specific frontend component, block renderer, or portal widget. |

## Feature Flags

| Term | Definition |
|------|------------|
| **Feature Flag** | A typed, tenant-scoped capability toggle stored in `tenant_feature_flags` and resolved through `IFeatureFlags`. Catalog is code-defined; per-tenant overrides are data. |
| **Flag Catalog** | The code-defined enumeration of every flag the platform knows about. Free-form string keys are forbidden; `FlagKey<T>` is the only entry point. |
| **Killswitch** | A flag whose default is "enabled" and that gates an expensive or risky code path. Flipped off during an incident to disable the path. |
| **Vertical Enablement Flag** | A flag of the form `vertical.<key>.enabled` that gates whether a vertical's handlers, blocks, and content types are active for a tenant. |

## Search

| Term | Definition |
|------|------------|
| **Search Index** | A Meilisearch index named `<env>-<kind>-<locale>` (e.g. `prod-course-en`). |
| **Search Kind** | A document type (`course`, `content-entry`, `media`, ...). Verticals can register their own kinds. |
| **Tenant Filter (search)** | The mandatory `tenant_id = ?` predicate every tenant-scoped search query carries. Enforcement lives in `ITenantSearch`; direct Meilisearch client calls are forbidden. |
| **Reindex** | A platform-admin operation that streams documents from the source-of-truth table into a fresh index, then atomically swaps the alias. |

## Custom Domains

| Term | Definition |
|------|------------|
| **Custom Domain** | A tenant-chosen hostname (`learn.acme.com`) that resolves to a tenant via the host → tenant registry. |
| **Domain Verification** | The DNS check (TXT or CNAME) that proves the tenant controls the host before traffic is routed. |
| **Reserved Host** | A hostname the platform refuses to assign to a tenant (e.g. `api.*`, `admin.*`, any platform domain). |

## Data Protection

| Term | Definition |
|------|------------|
| **PII Category** | One of `PII-Identity`, `PII-Behaviour`, `PII-Sensitive`, `Payment`, `Audit`. Each category has its own redaction, retention, and erasure rules. |
| **Data Controller** | The tenant. Decides what personal data is collected and why. |
| **Data Processor** | LearnStack. Processes personal data on the controller's instructions. |
| **Right of Access** | The user's KVKK/GDPR right to receive an export of their personal data. |
| **Right to Erasure** | The user's right to have their personal data deleted, subject to retention exceptions (legal hold, financial records). |
| **Anonymisation** | Replacement of PII fields with pseudonymous values; row stays for analytics / audit integrity. Distinct from soft delete and hard delete. |
| **Consent Record** | An append-only per-purpose record (terms of service, recording, marketing). "Changing one's mind" creates a new record, never edits an old one. |
| **Sub-processor** | A third-party service LearnStack uses to process tenant data (Keycloak, LiveKit, S3 / MinIO, email provider, ...). Changes require 30-day tenant notice. |

## Audit

| Term | Definition |
|------|------------|
| **Audit Operation Class** | One of `create`, `update`, `delete`, `read-sensitive`, `security-event`, `platform-admin`. Determines whether an action MUST, SHOULD, or MAY be audited. |
| **Audit Coverage Matrix** | A per-module table mapping resources × operations to MUST / SHOULD / MAY / – classifications. Required for every module spec. |
| **Retention Class** | The retention floor for a category of audit entries (7y for security-event / platform-admin / financial; 2y for others). |

## Permissions

| Term | Definition |
|------|------------|
| **Permission Key** | A dotted string `{module}.{resource}.{action}` with `action` drawn from the closed set `read \| write \| delete \| admin`. |
| **Permission Scope** | Either `platform` (cross-tenant operator capabilities) or `tenant` (within a tenant). Registries are disjoint. |
| **Permission Matrix** | A per-module table mapping `Resource × Action` to ✓ / – plus default role grants. Required for every module spec. |

## Page Builder

| Term | Definition |
|------|------------|
| **Block Schema Version** | The `(key, schemaVersion)` tuple that identifies a page block's payload shape. Schemas are immutable after publish; breaking changes bump the version. |
| **Lazy Migration (blocks)** | The studio-side upgrade of an existing block instance from an older `schemaVersion` to a newer one on save. The published version is untouched until the editor publishes. |
| **Bulk Migration (blocks)** | A platform-admin operation that walks every tenant's stored block instances of a given `(key, schemaVersion)` and migrates them to a new version. |

## Distributed Consistency

| Term | Definition |
|------|------------|
| **Tier 1** | A command with no external calls; DB transaction is the boundary. |
| **Tier 2A** | A command where the external system is a mirror of DB state; DB-first, external call after, failure non-fatal (retry via outbox). |
| **Tier 2B** | A command where the external system returns an ID we must store; external call first, then DB write, compensating action on DB failure. |
| **Tier 3** | A cross-system commit with provider-confirmed completion (payment, recording); idempotency key + pending row + provider webhook. |

## API & Integration

| Term | Definition |
|------|------------|
| **BFF (Backend-for-Frontend)** | The Next.js server-side proxy layer in `app/api/` that holds session cookies, refreshes Keycloak tokens silently, and forwards calls to the .NET API with `Authorization` and tenant headers. The browser never sees refresh tokens. |
| **Idempotency Key** | A client-supplied `Idempotency-Key` header on `POST` operations with external side effects (payments, webhook processing, notification sending, recording start/stop). The server stores `(idempotency_key, response)` for 24 hours and replays the stored response for duplicates. See [04-api-design.md § Idempotency](standards/04-api-design.md). |
| **Problem Details** | RFC 7807 JSON error envelope (`type`, `title`, `status`, `code`, `detail`, `instance`, `correlationId`) used by every LearnStack error response. |

## Conventions

- `PascalCase` for entities and aggregates.
- `kebab-case` for slugs, route segments, and config keys.
- `snake_case` for database tables and columns.
- `camelCase` for JSON payloads.
