# Glossary

This glossary defines LearnStack-specific terms. When a term is ambiguous across the industry, this document is the source of truth for how LearnStack uses it.

## Platform & Tenancy

| Term | Definition |
|------|------------|
| **LearnStack** | The core platform engine. Not a single product, not a single LMS. The reusable foundation that hosts education products of any domain. |
| **LearnStack Hub** | The companion control-plane application in the separate `learnstack-hub` repository. Owns plans, custom-domain admin, license-key issuance, and the entitlement projection that LearnStack core mirrors. See [ADR-0019](decisions/0019-learnstack-hub.md) and [24-learnstack-hub.md](architecture/24-learnstack-hub.md). |
| **Tenant** | A logical education platform or brand running on LearnStack. One LearnStack deployment can host many tenants. Each tenant has its own domain, branding, content, courses, and members. |
| **Organization** | A sub-unit within a tenant — a branch, campus, studio, department, or cohort. Two-level hierarchy strict (`Tenant → Organization`, no nesting) per [ADR-0017](decisions/0017-tenant-organization-hierarchy.md). Every tenant has at least one default organization. |
| **Brand** | Synonym for Tenant in product-facing language. In code, prefer `Tenant`. |
| **Platform Admin** | A LearnStack operator who manages tenants, plans, infrastructure-level settings. Authenticates against the `learnstack-hub` Keycloak realm. Operates above tenants. |
| **Hub Operator** | A subset of Platform Admins who use the operator portal (`learnstack-hub-web`). Same Keycloak realm; finer-grained roles (`hub-platform-admin`, `hub-operator`, `hub-billing-viewer`). |
| **Tenant Admin** | A user with administrative rights inside a single tenant. Authenticates against the `learnstack` Keycloak realm. Cannot see other tenants. |
| **Org Admin** | A user with administrative rights inside a single organization within a tenant. |
| **Deployment Mode** | One of `Development` / `SaaS` / `Dedicated` / `SelfHosted` per [ADR-0020](decisions/0020-triple-deployment-hybrid-license.md). Selected at composition root; module code never branches on it. |
| **Core** | The reusable platform layer. Does **not** contain domain-specific business rules. |
| **Tenant Customization** | Per-tenant data (JSON Schemas + DSL expressions) that defines the tenant's domain shape: content types, page blocks, lesson item types, level taxonomies, scoring rules, completion rules, custom fields, notification templates. Authored by tenants, not by LearnStack. See [ADR-0018](decisions/0018-tenant-driven-customization-model.md). |

## Identity & Membership

| Term | Definition |
|------|------------|
| **User** | A person known to LearnStack at the global level. Identified by a stable user id. |
| **Membership** | The relationship between a user, a tenant, and (optionally) an organization. Triple-keyed `(user_id, tenant_id, organization_id)` per [ADR-0017](decisions/0017-tenant-organization-hierarchy.md). A user can have memberships in multiple tenants and multiple organizations within one tenant. |
| **Role** | A named bundle of permissions. Scope: `Platform` / `Tenant` / `Organization`. Examples: `tenant-admin`, `editor`, `instructor`, `learner`, `org-admin`. |
| **Permission** | A fine-grained capability `{module}.{resource}.{action}` with a scope (Platform / Tenant / Organization). Action set is closed: `read | write | delete | admin`. |
| **Invitation** | A pending offer for a user to accept a membership in a tenant + organization. |

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
| **Tenant Customization Aggregate** | One of `TenantContentType`, `TenantPageBlock`, `TenantLessonItemType`, `TenantLevelTaxonomy`, `TenantScoringRule`, `TenantCompletionRule`, `TenantCustomFieldDef`, `TenantTemplateLibrary`. Per [ADR-0018](decisions/0018-tenant-driven-customization-model.md), per-tenant domain shapes live here as data, not code. |
| **Composite Renderer Key** | A reference from a `TenantPageBlock` / `TenantLessonItemType` to a built-in primitive renderer (`default-card`, `content-list`, `card-grid`, …) that the runtime resolves to a React component. Tenants compose; they do not bring code. |
| **Provider Adapter** | A concrete implementation of an infrastructure-side interface (payment, live-class, search, storage, email, SMS, event bus, cache, secrets, entitlement source, host resolver). |
| **Vertical (deprecated)** | Pre-2026-05-18 term for a domain-specific code module. Verticals as code no longer exist — see [ADR-0018](decisions/0018-tenant-driven-customization-model.md). Domain shapes live as tenant customization data. |

## Feature Flags & Entitlements

| Term | Definition |
|------|------------|
| **Feature Flag (tenant-level)** | A typed capability toggle stored in `tenant_feature_flags` for experimental / rollout / opt-in features. Resolved through `IFeatureFlags`. Catalog is code-defined (`FeatureKeys`); per-tenant overrides are data. |
| **Entitlement** | A Hub-projected fact about a tenant's plan: which features are enabled, which limits apply, which compliance caps. Mirrored from the Hub into `platform_entitlement_cache` per [ADR-0021](decisions/0021-feature-based-entitlement.md). |
| **Feature Key / Limit Key** | A typed value object (`FeatureKey`, `LimitKey`) from the `FeatureKeys` / `LimitKeys` static catalogs. Free-form strings are forbidden. |
| **Killswitch** | A `KillswitchKeys.*` flag whose default is "enabled" and gates an expensive or risky code path. Flipped off during an incident to disable the path platform-wide. Overlay wins over both plan projection and tenant flag. |
| **Soft vs Hard Limit** | A `LimitKey` declares `LimitEnforcement = Soft | Hard`. Hard refuses the operation with `403 ProblemDetails`; Soft surfaces a banner and emits a Hub-side soft-limit alert. |

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
| **Custom Domain** | A tenant-chosen hostname (`learn.acme.com`) that resolves to a tenant via `platform_host_to_tenant`. Hub owns the issuance + TLS lifecycle; LearnStack mirrors the host → tenant mapping. See [ADR-0022](decisions/0022-custom-domain-tls.md) and [27-custom-domain-tls.md](architecture/27-custom-domain-tls.md). |
| **Domain Verification** | The DNS-01 / HTTP-01 challenge that proves the tenant controls the host before TLS issuance. Hub-side flow. |
| **Reserved Host** | A hostname the platform refuses to assign to a tenant (e.g. `api.*`, `admin.*`, `hub.*`, any platform domain). |
| **`IHostToTenantResolver`** | The interface every host-lookup goes through. Backed by `platform_host_to_tenant`. The frontend edge calls a thin API endpoint that delegates to it. |

## Branding

| Term | Definition |
|------|------------|
| **TenantBranding** | The aggregate inside Tenancy that carries the tenant's design tokens — logo, primary / secondary colour set, typography tokens, header / footer settings. Resolved once per request at the layout level and injected as CSS variables on the SSR'd HTML root. |
| **OrganizationBranding** | An optional override row attached to an `Organization` that supplies a partial design-token set. When the resolved request carries an organization id, the runtime merges `OrganizationBranding` on top of `TenantBranding` before injecting tokens; missing fields fall through to the tenant default. |

## Module-Loading Contracts

| Term | Definition |
|------|------------|
| **`IModule`** | The module-loading contract every backend module exposes: a single `AddXxxModule(IServiceCollection)` extension that registers the module's MediatR handlers, EF DbContext, validators, permission catalogue, audit-coverage matrix, and any provider adapters. The composition root calls each module's contract exactly once at startup; modules never register cross-module dependencies. |
| **`IPermissionRegistry`** | The interface modules use to declare their permission keys + scope (Platform / Tenant / Organization) + default role grants. Registry is composed at startup from every module's contributions; CI fails on duplicates. See [19-permissions.md](standards/19-permissions.md). |
| **`IAuthorizationHandler<TOperation, TResource>`** | The application-layer policy class that evaluates whether an actor with a given permission can perform an operation on a specific resource (e.g. `instructor` with `education.course.write` against `Course.OwnerId == actor.UserId`). Sits behind `IAuthorizationService.AuthorizeAsync` calls. |

## Marker Attributes

| Term | Definition |
|------|------------|
| **`[TenantOwned]`** | Marks a domain entity whose rows are scoped to a tenant. The build inspects the marker to assert: the entity carries a `TenantId`, an EF global query filter for tenant scope is configured, and a PostgreSQL RLS policy on the backing table reads `current_setting('app.tenant_id')`. See [05-database.md](standards/05-database.md). |
| **`[OrganizationScoped]`** | Marks a `[TenantOwned]` entity that additionally carries `OrganizationId` (nullable; null means tenant-wide). The build asserts a matching org-aware EF filter + RLS policy reading `current_setting('app.organization_id', true)`. Per [ADR-0017](decisions/0017-tenant-organization-hierarchy.md). |
| **`[PiiSensitive]`** | Marks a field whose value the audit pipeline must redact before persisting to `audit_log`. The redaction filter strips matching property names from `before` / `after` snapshots and replaces with `"<redacted>"`. |
| **`[ConsistencyTier(...)]`** | Optional marker on a command handler that explicitly states the distributed-consistency tier (1 / 2A / 2B / 3) per [01-architecture-standards.md § Distributed-Consistency Tiers](standards/01-architecture-standards.md). Reviewers use it to reason about failure modes. |

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
| **AuditEntry** | The append-only aggregate owned by `LearnStack.Modules.Audit`. Inherits `Entity<TId>`, **not** `AuditableEntity<T>` — append-only by design. See [ADR-0016](decisions/0016-audit-log-subsystem.md) and [31-audit-subsystem.md](architecture/31-audit-subsystem.md). |
| **Audit Operation Class** | One of `create`, `update`, `delete`, `read-sensitive`, `security-event`, `platform-admin`. Determines whether an action MUST, SHOULD, or MAY be audited. |
| **Audit Coverage Matrix** | A per-module table mapping resources × operations to MUST / SHOULD / MAY / – classifications. Required for every module spec. |
| **AuditConfig** | The per-tenant override of the catalog's MUST/SHOULD/MAY mapping (tenants can opt into stricter coverage but cannot relax MUST). |
| **Audit Capture Pipeline** | `AuditChangeTrackerInterceptor` (EF) → `IAuditStateCapture` (before/after/changes JSON) → `AuditLogBehavior` (MediatR) → `IAuditStore`. Modules never write `audit_log` directly. |
| **Retention Class** | The retention floor for a category of audit entries (7y for security-event / platform-admin / financial; 2y for others). |

## Permissions

| Term | Definition |
|------|------------|
| **Permission Key** | A dotted string `{module}.{resource}.{action}` with `action` drawn from the closed set `read \| write \| delete \| admin`. Domain terms (CEFR, English, yoga, …) are forbidden in keys. |
| **Permission Scope** | One of `platform` (Hub operators), `tenant` (within a tenant), or `organization` (within one organization). Registries are disjoint. |
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
| **BFF (Backend-for-Frontend)** | The Next.js server-side proxy layer in `app/api/` that holds session cookies, refreshes Keycloak tokens silently, and forwards calls to the .NET API with `Authorization` and tenant + organization headers. The browser never sees refresh tokens. |
| **Idempotency Key** | A client-supplied `Idempotency-Key` header on `POST` operations with external side effects (payments, webhook processing, notification sending, recording start/stop). The server stores `(idempotency_key, response)` for 24 hours and replays the stored response for duplicates. See [04-api-design.md § Idempotency](standards/04-api-design.md). |
| **Problem Details** | RFC 7807 JSON error envelope (`type`, `title`, `status`, `code`, `detail`, `instance`, `correlationId`) used by every LearnStack error response. |
| **Hub Contract Surface** | The closed set of four endpoints between LearnStack core and the Hub: `POST /api/internal/tenants`, `PUT /api/internal/tenants/{id}/entitlements`, `POST /api/v1/internal/license/verify`, `POST /api/v1/usage/report`. mTLS + signed JWT + HMAC. Adding a fifth requires a new ADR. |

## Foundation Infrastructure

| Term | Definition |
|------|------------|
| **Dapr Building Blocks** | The three Dapr abstractions LearnStack uses: pub/sub (Kafka), state (Redis), secrets (Vault) per [ADR-0014](decisions/0014-adopt-dapr.md). Service invocation, workflow, bindings, and actors are out of scope. |
| **`IEventBus`** | Interface for publishing integration events. Backed by `DaprEventBus` (production) or `InProcessEventBus` (development). The `OutboxProcessor` is the only sanctioned caller. |
| **`ICacheService`** | Interface for cache reads / writes. Backed by `DaprCacheService` (production, Redis-backed) or `InMemoryCacheService` (development). Cache keys carry `{tenant_id}` prefix. |
| **`ISecretProvider`** | Interface for secret reads. Backed by `DaprSecretProvider` (production, Vault) or `EnvironmentSecretProvider` (development). Secret namespace `learnstack/{deployment}/{module}/{key}`. |
| **`IEntitlementProvider`** | Interface for the entitlement source. Implementations: `NullEntitlementProvider` (dev), `HubEntitlementProvider` (SaaS / Dedicated), `SignedLicenseKeyEntitlementProvider` (Self-Hosted). |
| **`IHostToTenantResolver`** | Interface for host → `(tenant_id, organization_id?)` resolution. Backed by `platform_host_to_tenant`. |
| **APISIX** | The gateway in standalone YAML-reload mode per [ADR-0015](decisions/0015-api-gateway-apisix.md). The only tenant-facing ingress. A separate route set guards `/api/internal/*` with mTLS. |
| **`OutboxProcessor`** | The BackgroundService that polls `outbox_messages` with `FOR UPDATE SKIP LOCKED`, dispatches through `IEventBus`, and handles retry / dead-letter. |
| **`IInboxGuard`** | The per-module inbox-deduplication helper. Every integration-event handler must call `IsAlreadyProcessedAsync` before business logic and `MarkAsProcessed` inside the same SaveChanges. |
| **`DeploymentMode`** | Enum (`Development | SaaS | Dedicated | SelfHosted`) read at the composition root to select provider implementations. Modules never read this enum. |

## Hub & Licensing

| Term | Definition |
|------|------------|
| **Plan (Hub-side)** | The plan catalog entry owned by `learnstack-hub`. Carries default features, limits, compliance caps. |
| **HubSubscription** | A per-tenant binding to a `Plan`. Has lifecycle state (trial / active / cancelled / grace / suspended). |
| **Entitlement (Hub-side)** | The effective feature + limit + compliance set for a tenant — the snapshot Hub pushes to LearnStack core via `PUT /api/internal/tenants/{id}/entitlements`. |
| **License Key** | An RSA-2048 signed `.lic` file issued by Hub for Self-Hosted deployments. Carries claims (`tenant_id`, `plan_code`, `features`, `limits`, `valid_until`, …) and a signature. See [ADR-0020](decisions/0020-triple-deployment-hybrid-license.md). |
| **Phone-Home** | The 24h verify call a Self-Hosted instance makes against Hub's `POST /api/v1/internal/license/verify`. Optional; can be disabled for air-gapped operation. |
| **Grace Period** | The 30-day window during which a Self-Hosted instance keeps operating after the last successful phone-home. |
| **Platform Entitlement Cache** | The `platform_entitlement_cache` table in LearnStack core — the read-only mirror of Hub's `Entitlement`. 15-min TTL upper bound, eager-invalidated on `learnstack.hub.entitlement` Dapr event. |
| **Operator Portal** | `learnstack-hub-web` — the separate Next.js app for Hub operators. Authenticates against the `learnstack-hub` Keycloak realm. |

## Conventions

- `PascalCase` for entities and aggregates.
- `kebab-case` for slugs, route segments, and config keys.
- `snake_case` for database tables and columns.
- `camelCase` for JSON payloads.
