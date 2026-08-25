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
| **Hub Operator** | A subset of Platform Admins who use the operator portal (`operator-portal`). Same Keycloak realm; finer-grained roles (`hub-platform-admin`, `hub-operator`, `hub-billing-viewer`). |
| **Tenant Admin** | A user with administrative rights inside a single tenant. Authenticates against the `learnstack` Keycloak realm. Cannot see other tenants. |
| **Org Admin** | A user with administrative rights inside a single organization within a tenant. |
| **Deployment Mode** | One of `Development` / `SaaS` / `Dedicated` / `SelfHostedOnline` / `SelfHostedAirGapped` per [ADR-0020](decisions/0020-triple-deployment-hybrid-license.md). Selected at composition root; module code never branches on it. The two `SelfHosted*` variants differ on phone-home availability (entitlement source). |
| **Core** | The reusable platform layer. Does **not** contain domain-specific business rules. |
| **Tenant Customization** | Per-tenant data (JSON Schemas + DSL expressions) that defines the tenant's domain shape: content types, page blocks, lesson item types, level taxonomies, scoring rules, completion rules, custom fields, notification templates. Authored by tenants, not by LearnStack. See [ADR-0018](decisions/0018-tenant-driven-customization-model.md). |

## Identity & Membership

| Term | Definition |
|------|------------|
| **User** | A person known to LearnStack at the global level. Identified by a stable user id. |
| **Membership** | The relationship between a user, a tenant, and (optionally) an organization. Triple-keyed `(user_id, tenant_id, organization_id)` per [ADR-0017](decisions/0017-tenant-organization-hierarchy.md). A user can have memberships in multiple tenants and multiple organizations within one tenant. |
| **Role** | A named bundle of permissions. Scope: `Platform` / `Tenant` / `Organization`. Examples: `tenant-admin`, `editor`, `instructor`, `learner`, `org-admin`. |
| **Permission** | A fine-grained capability `{module}.{resource}.{action}` with a scope (Platform / Tenant / Organization). Action set is closed: `read \| write \| delete \| admin`. |
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
| **Module (course aggregate)** | An ordered grouping of lessons inside a course version. Distinct from the backend module-loading concept *`IModule`* (see *Module-Loading Contracts* below). When the term "module" appears unqualified in code or docs, prefer this domain meaning unless the surrounding text is clearly about the backend loader. |
| **Lesson** | A unit of learning consumption inside a module. |
| **Lesson Item** | A single piece inside a lesson: rich text, video, file, quiz reference, live-session reference, embedded tool. |
| **Learning Path** | An ordered or conditional traversal across multiple courses or lessons. |
| **Completion Rule** | A rule that determines when a lesson, module, or course is considered complete. |

## Enrollment & Access

| Term | Definition |
|------|------------|
| **Enrollment** | A learner's grant of access to a specific course (and specific course version). |
| **Course Access** | A *learner's* right to open a specific course, derived from an `Enrollment` (or from a tenant-side purchase, cohort membership, or admin grant). Evaluated inside the Enrollment module against tenant data. **Not an Entitlement** — see *Feature Flags & Entitlements*. The two words were used interchangeably in earlier drafts; they are different subjects (a learner versus a tenant), different owners (LearnStack versus Hub), and different lifecycles. |
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
| **Live Recording** | Metadata for a recording produced by the provider's egress pipeline. The file lives in SeaweedFS / S3; LearnStack stores the metadata and consent state. |

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
| **Plan (tenant storefront)** | A package or subscription definition referencing one or more products. Lives in the LearnStack core `Billing` module — what a tenant sells to its own learners. Distinct from the Hub-side `Plan` (see *Hub & Licensing*) that governs the tenant's own LearnStack subscription. |
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
| **Genericity Boundary** | The stated edge of the claim "the domain is tenant data". **Inside** — content shape, presentation, and pure rule evaluation: all three are pure functions of tenant data and already-recorded state, so a JSON Schema plus an evaluator is a complete answer, and a tenant declares them without a LearnStack release. **Outside** — stateful entitlement (credit packs, session quotas: a schema declares a shape, it cannot declare a ledger) and external capability invocation (executing submitted programs, scoring audio: a rule DSL evaluates, it does not execute arbitrary programs). Outside-the-boundary needs are met by **generically named, plan-gated platform features** written by LearnStack — never by per-vertical code and never by a customization row. Per [ADR-0018 Amendment (2026-08-08)](decisions/0018-tenant-driven-customization-model.md); stated product-facing in [Platform Vision](architecture/01-platform-vision.md). |
| **Composite Renderer Key** | A reference from a `TenantPageBlock` / `TenantLessonItemType` to a **built-in composite renderer** that the runtime resolves to a React component. The registry is closed and lives in [32-tenant-customization-model.md § 2](architecture/32-tenant-customization-model.md): `default-card`, `content-list`, `media-gallery`, `rich-page`, `lesson-shell`, `quiz-shell`, `placement-shell`, `live-shell`, `submission-shell`. Every key names a capability, never a domain. Tenants compose; they do not bring code. **Distinct from a *page block key*** (`hero`, `rich-text`, `card-grid`, …), which is the page-builder's own built-in set — the two registries are described in different documents and their reconciliation is owned by [Phase 06](roadmap/phase-06-renderer-admin-studio.md). |
| **Provider Adapter** | A concrete implementation of an infrastructure-side interface (payment, live-class, search, storage, email, SMS, event bus, cache, secrets, entitlement source, host resolver). |
| **Vertical (deprecated)** | Pre-2026-05-18 term for a domain-specific code module. Verticals as code no longer exist — see [ADR-0018](decisions/0018-tenant-driven-customization-model.md). Domain shapes live as tenant customization data. |

## Feature Flags & Entitlements

| Term | Definition |
|------|------------|
| **Feature Flag (tenant-level)** | A typed capability toggle stored in `tenant_feature_flags` for experimental / rollout / opt-in features. Resolved through `IFeatureFlags`. Catalog is code-defined (`FeatureKeys`); per-tenant overrides are data. |
| **Entitlement** | **The single definition.** The set of capabilities a **tenant** holds by virtue of its plan: which features are enabled, which numeric limits apply, which compliance caps are forced. Its subject is always a tenant, never a learner. It is **owned by the Hub** and **mirrored** into LearnStack; LearnStack reads it and never authors it. Per [ADR-0021](decisions/0021-feature-based-entitlement.md) and [ADR-0034](decisions/0034-hub-contract-surface-invariant.md). Three things in the corpus are frequently called "entitlement" and are distinguished by name below. |
| **Entitlement Aggregate (Hub-side)** | The authoritative aggregate in the `learnstack-hub` repository, recomputed from `Plan` + `HubSubscription` + operator-set compliance caps. The **only** writer of an entitlement anywhere. |
| **Entitlement Projection** | The read-only copy LearnStack holds, delivered by `PUT /api/internal/tenants/{id}/entitlements` or embedded in a signed licence key, stored durably in `platform_entitlement_cache` and served through `IEntitlementProvider`. Carries `expires_at`, `grace_until` and a monotonic `generation` on the wire; these persist to the `valid_until`, `grace_until` and `generation` columns of `platform_entitlement_cache`. Wire shape pinned by `entitlement-v1.schema.json` in both repositories. |
| **Course Access** | *Not* an entitlement — a learner's right to open a course. See *Enrollment & Access*. |
| **Feature Key / Limit Key** | A typed value object (`FeatureKey`, `LimitKey`) from the `FeatureKeys` / `LimitKeys` static catalogs. Free-form strings are forbidden. Canonical spelling is `{area}.{name}` with no `.enabled` suffix on features and no `limits.` prefix on limits — `classroom.recording`, `tenancy.max_learners`. See [26-hybrid-license-model.md § 0](architecture/26-hybrid-license-model.md). |
| **Killswitch** | A `KillswitchKeys.*` flag whose default is "enabled" and gates an expensive or risky code path. Flipped off during an incident to disable the path platform-wide. Overlay wins over both plan projection and tenant flag. |
| **Soft vs Hard Limit** | A `LimitKey` declares `LimitEnforcement = Soft \| Hard`. Hard refuses the operation with `403 ProblemDetails`; Soft surfaces a banner and emits a Hub-side soft-limit alert. |

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
| **Sub-processor** | A third-party service LearnStack uses to process tenant data (Keycloak, LiveKit, S3 / SeaweedFS, email provider, ...). Changes require 30-day tenant notice. |

## Audit

| Term | Definition |
|------|------------|
| **AuditEntry** | The append-only aggregate owned by `LearnStack.Modules.Audit`. Inherits `Entity<TId>`, **not** `AuditableEntity<T>` — append-only by design (`AuditEntry_Inherits_Entity_Not_AuditableEntity`). Persisted to `audit_log`, whose primary key is the composite `(id, timestamp)`. See [ADR-0033](decisions/0033-audit-durability-model.md) and [31-audit-subsystem.md](architecture/31-audit-subsystem.md). |
| **Audit Operation Class** | One of `create`, `update`, `delete`, `read-sensitive`, `security-event`, `platform-admin`. Determines whether an action MUST, SHOULD, or MAY be audited. |
| **Audit Coverage Matrix** | A per-module table mapping resources × operations to MUST / SHOULD / MAY / – classifications. Required for every module spec. |
| **AuditConfig** | The per-tenant override of the catalog's MUST/SHOULD/MAY mapping. A tenant may opt into stricter coverage; it can never relax MUST — the classifier applies the override and then re-applies the catalogue's MUST floor. A failure to **read** the configuration **fails closed**: the operation is rejected rather than proceeding unaudited, so a config-store outage cannot silently switch off mandatory security auditing. Per [ADR-0033](decisions/0033-audit-durability-model.md). |
| **Audit Capture Pipeline** | `AuditChangeTrackerInterceptor` (EF) → `IAuditStateCapture` (before/after/changes JSON) → `AuditLogBehavior` (MediatR) → `IAuditStore`. Modules never write `audit_log` directly. |
| **Durable Audit Intent** | The MUST-class audit row, written by `IAuditStore.WritePendingAsync` on the **same transaction as the business write** — `AuditLogBehavior` classifies and parks the intent at pipeline step 3, `TransactionBehavior` writes it immediately before `COMMIT` — so it commits with that write or not at all, and so it executes while `app.tenant_id` is set and the Row Level Security `WITH CHECK` accepts it. "The same `SaveChanges`" was the earlier formulation and ADR-0033 withdraws it: the guarantee is the transaction, which needs no cross-`DbContext` machinery. If it cannot be written, the business operation **fails closed**. Enrichment, redaction, projection, and external fan-out happen after the commit, reading the durable row, and are best-effort. SHOULD/MAY-class audit is **not** a durable intent: it is written outside the transaction, and its accepted loss is written down rather than assumed. Per [ADR-0033](decisions/0033-audit-durability-model.md), which supersedes ADR-0016. |
| **Retention Class** | The retention floor for a category of audit entries (7y for security-event / platform-admin / financial; 2y for others). Enforced by a **daily** Hangfire purge job from [Phase 11](roadmap/phase-11-production-hardening.md). |

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
| **Idempotency Key** | A client-supplied `Idempotency-Key` header on unsafe operations with external side effects (payments, notification sending, recording start/stop). It is a **nonce inside a tenant's key space, not an identity**: the stored record is addressed by `(tenant, key)` and carries a fingerprint of the organization, principal, method, path, query and body, so the same key presented for a different request is refused rather than replayed. Inbound webhooks do **not** use it — they deduplicate on `(provider, event_id)`. See [ADR-0037](decisions/0037-idempotency-key-contract.md) and [04-api-design.md § Idempotency](standards/04-api-design.md). |
| **Problem Details** | RFC 7807 JSON error envelope used by every LearnStack error response: `type`, `title` (the `lockey_*` key), `status`, `code`, `messageKey`, `instance`, `correlationId`, and `errors` for field-level failures. There is no `detail` — an earlier version of this entry listed one and omitted `messageKey`. The canonical example lives in [09-error-handling.md § API Surface](standards/09-error-handling.md); this entry names the fields rather than restating it. |
| **Hub Contract Surface** | The enumerated set of endpoints between LearnStack core and the Hub, governed by **two invariants** rather than by a count ([ADR-0034](decisions/0034-hub-contract-surface-invariant.md)): (1) the Hub stores no tenant content — only tenant metadata: plan, subscription, licence, custom domain, compliance caps, aggregated usage; (2) every crossing goes through a named adapter — `IEntitlementProvider`, `IUsageReporter`, `IHubTenantSync` — and no other type may hold a Hub client. All crossings carry mTLS + RS256 JWT + HMAC body signature. Adding an endpoint still requires an ADR, because the surface is a cross-repository contract. The superseded "closed at four endpoints" phrasing was never true, and protecting the number is what caused TLS private keys to be tunnelled through the entitlement payload. ADR-0034 § The endpoint set is the authoritative enumeration; this entry deliberately does not restate a count, because a count here is a second thing to keep in step. |

## Cross-Cutting Concerns

| Term | Definition |
|------|------------|
| **`Result<T>`** | The sealed record in `LearnStack.SharedKernel.Results` implementing `IResultBase`: carries `IsSuccess`, `Value`, optional `Error`, and an optional `SuccessMessage` (a `LocalizedMessage`). Primary constructor is `internal`; callers go through `Ok` (throws on null value) / `Fail`. Application + Domain layer methods return `Result<T>` for **expected** outcomes (validation failed, not found, forbidden, business-rule violation). Exceptions are reserved for **unexpected** bugs / infrastructure faults. `Result<None>` is the payload-less success shape. See [09-error-handling.md § Two-Track Model](standards/09-error-handling.md) and [ADR-0032](decisions/0032-exception-handling-logging-and-observability.md). |
| **`Error`** | The sealed record `(LocalizedMessage Message, IReadOnlyDictionary<string, IReadOnlyList<LocalizedMessage>>? Details)` that travels inside `Result<T>.Fail(error)`. `Code` is the **unprefixed stable identifier** (e.g. `"validation_failed"`) derived by stripping the `lockey_` prefix from `Message.Key` — so routing logic (`Result.ToActionResult()`, Problem Details writers) and frontend localization read consistent values without manual sync. Field-level `Details` flow as `LocalizedMessage` lists, keeping the prefix invariant uniform across the entire error payload. See [09-error-handling.md § Result Type](standards/09-error-handling.md). |
| **`LocalizedMessage`** | The sealed record `(string Key, IReadOnlyDictionary<string,string>? Params)` in `LearnStack.SharedKernel.Localization`. `Key` MUST begin with `lockey_` — enforced at the constructor — so the frontend's translation catalogues (keyed under the same prefix) resolve every user-facing message without raw English on the wire. Equality is structural (Key + Params); ctor defensively copies Params; empty params normalised to null. `Params` values must be plain text — the frontend resolves messages via React text nodes, never `dangerouslySetInnerHTML`. Per [Phase 02a Packet 2](roadmap/phase-02a-kernel-tenancy.md). |
| **`None`** | The `readonly record struct` value used as `Result<None>` when a command/query succeeds without returning data — replaces the `Result<T>` with `IsSuccess = true` and `Value = null` shape Standards 09 § Forbidden bans. |
| **`IClock` / `IRandom` / `IGuidFactory`** | The three deterministic-test abstractions in `LearnStack.SharedKernel`. Production code never reads `DateTime.UtcNow`, instantiates `System.Random`, or calls `Guid.NewGuid()` directly — those calls go through the abstractions so tests pin the values via `FixedClock` / `FixedRandom` / `FixedGuidFactory`. Per Standards 02 § Time. |
| **`UserId`** | The cross-cutting strongly-typed actor identifier in `LearnStack.SharedKernel.Identifiers` (Vogen `[ValueObject<Guid>]`). Audit columns on `AuditableEntity<TId>` reference users by `UserId` so the "no raw `Guid` on the public surface" rule (Standards 02) holds even though the Identity module lands in Phase 02b. Identity consumes the same type when it ships. |
| **`Entity<TId>` / `AuditableEntity<TId>`** | The two aggregate bases in `LearnStack.SharedKernel.Domain`. `Entity<TId>` is the append-only / audit-row base — identity, in-process domain events, identity-based equality with **uninitialized-id + cross-runtime-type guards** so `HashSet`-backed collection navigations, `Distinct()` and `Contains` behave correctly before ids are minted. EF Core's change tracker is not among the reasons — it keys on the primary-key value and tracks by reference, never calling these members. `AuditableEntity<TId>` is the mutable base — adds `CreatedAt/By`, `UpdatedAt/By`, `DeletedAt/By`, `Version`, and the `IsDeleted` projection by implementing `ISoftDelete` + `IOptimisticConcurrency`. `MarkCreated` throws on second call; `SoftDelete` also bumps `UpdatedAt` so "last touched" stays monotonic. `AuditEntry` (audit subsystem) inherits `Entity<TId>` — never `AuditableEntity<TId>` — by architecture-test rule. |
| **`IDomainEvent`** | The marker interface (`: MediatR.INotification`) every in-process domain event implements. Raised from aggregate methods, collected by the unit of work, dispatched in-process by MediatR. The abstract `DomainEvent` base declares `EventId` and `OccurredAt` as `required init` so events are always stamped through `IGuidFactory` / `IClock` at the call site. Distinct from integration events, which cross module boundaries through the outbox + Dapr pub/sub per [ADR-0010](decisions/0010-cross-module-communication.md). |
| **`CursorPagination` / `Page<T>` / `PageInfo`** | The cursor-first pagination triple in `LearnStack.SharedKernel.Pagination` matching Standards 04 § Pagination. `CursorPagination(Cursor, Limit)` is the request (default `Limit = 20`, max 100; ctor throws on `Limit <= 0` — kernel-level guard); `Page<T>(Items, PageInfo)` is the response; `PageInfo(NextCursor, PreviousCursor, HasNext, HasPrevious)` carries the opaque cursors the client never parses. |
| **`LearnStackVogenDefaults.IdMask`** | The canonical `Conversions` mask every Vogen-emitted ID and value object opts into: `EfCoreValueConverter \| SystemTextJson \| TypeConverter`. Per [ADR-0023](decisions/0023-strongly-typed-id-source-generator.md) every aggregate-root ID writes `[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]`. |
| **Pipeline Behavior** | A MediatR pipeline behavior — one of the eight canonical steps wrapping every command / query: `Validation → Logging → AuditLog → TenantContext → Authorization → Transaction → OutboxFlush → Handler`. The order is binding per [ADR-0032 § Sub-decision 2](decisions/0032-exception-handling-logging-and-observability.md); the architecture test `MediatR_Pipeline_Order_Matches_Canonical_Sequence` enforces it. |
| **`IExceptionHandler` (LearnStack L1)** | The `.NET 8+` exception-handler interface; `LearnStackExceptionHandler : IExceptionHandler` is the final catch site for every unhandled exception. Maps to Problem Details, attaches `correlationId`, records the OTel span error, calls `IErrorTrackingProvider.CaptureAsync` when `ShouldCapture(ex)` is true. See [ADR-0032 § Sub-decision 1](decisions/0032-exception-handling-logging-and-observability.md). |
| **Correlation ID** | The W3C `traceparent` value (or a derived UUID for fallback) that threads through every signal — logs, traces, audit rows, outbox rows, Hangfire payloads, Problem Details bodies — bound to a single user request or background operation. Same as `Activity.Current.TraceId` for HTTP requests; reconstructed from the outbox row / Hangfire payload / event envelope at consumer side. See [10-observability.md § Correlation](standards/10-observability.md). |
| **Telemetry Signal** | One of the three OpenTelemetry signals — logs, traces, metrics. LearnStack emits all three through Microsoft.Extensions.Logging (Serilog implementation) + OTel SDK; errors additionally flow to `IErrorTrackingProvider`. |
| **`IErrorTrackingProvider`** | The composition-root abstraction over the error backend. Implementations: `NoOpErrorTracker` (Development / SelfHostedOnline-without-DSN), `SentryErrorTracker` (SaaS / Dedicated / SelfHostedOnline-with-DSN), `LocalFileErrorTracker` (SelfHostedAirGapped). Modules never import `Sentry.SentrySdk`; the architecture test `Modules_Do_Not_Reference_Sentry_SDK_Directly` enforces it. See [ADR-0032 § Sub-decision 9](decisions/0032-exception-handling-logging-and-observability.md). |
| **`IProviderResilience<TPort>`** | The Polly v8 `ResiliencePipeline` (retry + circuit breaker + timeout + bulkhead) that every provider adapter takes as a **collaborator** and routes outbound calls through. Not a decorator: C# forbids a type parameter as a base type, so no `ResilientProviderAdapter<TPort> : TPort` can exist ([ADR-0032 Amendment 2](decisions/0032-exception-handling-logging-and-observability.md)). The adapter does only SDK exception → `ProviderException` translation. Configured per port in `appsettings.Resilience:<portName>:`. See [ADR-0032 § Sub-decision 5](decisions/0032-exception-handling-logging-and-observability.md). |
| **`ITenantContextAccessor`** | The singleton, `AsyncLocal<ITenantContext?>`-backed accessor that cross-cutting infrastructure (`TenantContextSpanProcessor`, Serilog enricher, Sentry enricher) reads to enrich telemetry without inheriting the request-scoped DI lifetime. Populated at scope start by `TenantResolverMiddleware` (HTTP), `HubCorrelationMiddleware` (`/api/internal/*`), Hangfire `JobActivator` (background jobs), and the outbox / inbox handler scope. Modules never write to it. See [ADR-0032 § Sub-decision 10](decisions/0032-exception-handling-logging-and-observability.md). |
| **`TenantContextSpanProcessor`** | The `BaseProcessor<Activity>` registered once at the OTel tracing pipeline; its `OnStart` hook reads from `ITenantContextAccessor` and enriches every span with `tenant.id`, `organization.id`, `user.id`, `module`, `correlation.id` — including spans produced by auto-instrumentation libraries (EF Core, HttpClient, Valkey via Dapr, SeaweedFS S3 SDK, LiveKit). See [ADR-0032 § Sub-decision 10](decisions/0032-exception-handling-logging-and-observability.md). |
| **`ProviderException.IsClientError`** | Boolean flag set by adapters when translating upstream 4xx (`true`) or 5xx (`false`) responses. The L1 `IExceptionHandler` reads it to decide whether to Sentry-capture (only 5xx — provider's infra fault) or log-only (4xx — provider's user-error). |
| **L1 / L2 / L3 (cache)** | "L1 cache" is the per-pod in-process `IMemoryCache`; "L2 cache" is the cross-pod Valkey state via Dapr. Both layers are managed through `ICacheService`; do not confuse with error-handling layers. See [20-infrastructure-stack.md § Cache layer cheat sheet](standards/20-infrastructure-stack.md). |

## Foundation Infrastructure

| Term | Definition |
|------|------------|
| **Demand-Gated Building Block** | An infrastructure choice that ships as a **port plus a working default implementation** now, and as a **vendor adapter later**, in a named phase, when a written trigger fires. It qualifies only when all four exist: the port, the default implementation, the owning phase, and the trigger condition. A block missing any of the four is not demand-gated — it is simply missing. Distinguished from a one-way door by the test below. The gated set and each trigger are tabulated in [ADR-0035](decisions/0035-demand-gated-infrastructure.md), which also names its own two exceptions — `audit_log` partitioning, which is schema-internal and so has no port, and LiveKit, which has no default because its absence is a missing product feature rather than a missing implementation. |
| **One-Way Door** | A decision that gets more expensive with every week of delay, tested by the question: *if I add this six months from now, will I have to touch code that is already written?* **Yes → one-way door; ship it now** (tenant and organization isolation, the `outbox_messages` table and its ownership, strongly-typed identifiers, the localization schema — each touches every query, migration, or job payload ever written). **No → additive; ship the port now and the adapter on demand.** The test is mechanical, not a matter of taste: tenant isolation's cost grows with the codebase, a Dapr adapter's cost does not. A corollary rule: **a deployment mode or customer segment without a signed contract cannot be the deciding factor in a technical choice** — it may break a tie between otherwise-equal options, nothing more. Per [ADR-0035](decisions/0035-demand-gated-infrastructure.md) and [Engineering Principles](standards/00-principles.md). |
| **Dapr Building Blocks** | The three Dapr abstractions LearnStack uses **when it uses Dapr**: pub/sub (Kafka), state (Valkey), secrets (Vault) per [ADR-0014](decisions/0014-adopt-dapr.md). Service invocation, workflow, bindings, and actors are out of scope. Demand-gated to [Phase 11](roadmap/phase-11-production-hardening.md); ADR-0014 decides *what*, [ADR-0035](decisions/0035-demand-gated-infrastructure.md) decides *when*. |
| **`IEventBus`** | Interface for publishing integration events, taking an `IntegrationEventEnvelope`. `InProcessEventBus` is the only registered implementation until the Dapr adapter's trigger fires — and it is a **first-class transport, not a stub**: same `IIntegrationEventHandler<T>`, same `IInboxGuard`, same tenant-context restoration, same per-partition-key ordering as the durable path. The `OutboxProcessor` is the only sanctioned caller. |
| **`IntegrationEventEnvelope`** | One integration event plus the dispatch metadata the outbox row carries and the event does not: `Topic`, `CorrelationId`, `OrganizationId`, `CausationId`, `ActorUserId`. Its `PartitionKey` is the event's own, so the ordering domain has exactly one source ([ADR-0014 Amendment 3](decisions/0014-adopt-dapr.md)). Metadata describes the *delivery*; the event describes the *fact*. |
| **`IPartitionSerializer`** | Runs work sequentially within one partition key and concurrently across different ones — the in-process stand-in for what a broker gives you by assigning a partition to one consumer. It exists so the development transport carries the same ordering guarantee as the durable path rather than a weaker one. Queuing work for the key you are already inside is refused rather than deadlocked: the caller that does it is publishing from inside a handler, which [Standards 20](standards/20-infrastructure-stack.md) forbids. |
| **`EventTenantContext`** | The `ITenantContext` a consumer runs under, rebuilt from the envelope by the transport before the handler runs. A consumer executes outside the request that produced the fact, so there is no ambient context to inherit — which is why the tenant travels on the event and the rest on the envelope. Restoring it is what makes the query filters and the RLS policies evaluate against the right scope. |
| **`UserId.SystemActor`** | The fixed, non-empty `UserId` that integration-event consumers, background jobs and other non-request executions write state as — what [Audit Coverage](standards/18-audit-coverage.md) means by an actor of type `system`. Fixed rather than generated because it is a foreign key: the Tenancy migration seeds the matching `users` row so `created_by` resolves. `AuditableEntity.MarkCreated` refuses `default(UserId)` and `Guid.Empty` alike, so without it no consumer could create an aggregate at all. |
| **`ICacheService`** | Interface for cache reads / writes. `InMemoryCacheService` today; a Valkey-backed implementation when more than one instance runs concurrently. Cache keys lead with the tenant segment — `{tenant_id}:{module}:{logical-name}`, or `{tenant_id}:{organization_id}:{module}:{logical-name}` for a value scoped to one organization — composed by `CacheKey` and enforced by `CacheKey.EnsureValid`, because there is no query filter and no RLS policy in front of a dictionary. `RemoveByPrefixAsync` is **removed** ([ADR-0014 Amendment 2](decisions/0014-adopt-dapr.md)) — it iterated an instance-local key set, so keys written by another instance were never evicted. What replaces it is the **generation-key** pattern, which is a caller-side convention rather than a member of this interface: a durable counter bumped inside the business transaction and embedded in the key template. |
| **`ISecretProvider`** | Interface for secret reads. `ConfigurationSecretProvider` today; the Vault-backed implementation when a production secret must rotate without a redeploy, or more than one operator needs access to production secrets ([ADR-0035](decisions/0035-demand-gated-infrastructure.md) — *not* when a non-development deployment merely exists, which SaaS satisfies on day one). Secret namespace `learnstack/{deployment}/{module}/{key}`. |
| **`IEntitlementProvider`** | Interface for the Entitlement Projection source. Implementations: `NullEntitlementProvider` (Development only — all features enabled, no limits), `HubEntitlementProvider` (SaaS / Dedicated, from Phase 02c), `SignedLicenseKeyEntitlementProvider` (Self-Hosted; skeleton from Hub `P02c-6`, hardened in Phase 11). The Hub-backed provider resolves in the normative order `L1 → L2 → platform_entitlement_cache → Hub` and never throws out of a feature-flag check. |
| **`IHostToTenantResolver`** | Interface for host → `(tenant_id, organization_id?)` resolution. Reads `platform_host_to_tenant` and **nothing else** — never the Hub, because an anonymous page load must not depend on a control plane being reachable ([ADR-0034](decisions/0034-hub-contract-surface-invariant.md)). |
| **APISIX** | The gateway in standalone YAML-reload mode per [ADR-0015](decisions/0015-api-gateway-apisix.md). The intended tenant-facing ingress, demand-gated to [Phase 11](roadmap/phase-11-production-hardening.md); until then ASP.NET middleware carries the same responsibilities in-process. `/api/internal/*` is never proxied by it — that listener is mTLS-only inside the pod. |
| **`OutboxProcessor`** | The BackgroundService that **claims** batches of `outbox_messages` by writing a lease (`locked_by` / `locked_until`) under `FOR UPDATE SKIP LOCKED`, dispatches each through `IEventBus`, and handles retry / dead-letter. The lease is written to the row rather than held in the transaction, because `FOR UPDATE` locks end when the transaction ends. Delivery is **at-least-once**; consumer-side `IInboxGuard` is what makes duplicates safe. See [15-event-and-outbox.md](architecture/15-event-and-outbox.md). |
| **`IInboxGuard`** | The per-module inbox-deduplication helper. Every integration-event handler must call `IsAlreadyProcessedAsync` before business logic and `MarkAsProcessed` inside the same SaveChanges. |
| **`DeploymentMode`** | Enum (`Development \| SaaS \| Dedicated \| SelfHostedOnline \| SelfHostedAirGapped`) read at the composition root to select provider implementations. Modules never read this enum. |

## Hub & Licensing

| Term | Definition |
|------|------------|
| **Plan (Hub-side)** | The plan catalog entry owned by `learnstack-hub`. Carries default features, limits, compliance caps. |
| **HubSubscription** | A per-tenant binding to a `Plan`. Has lifecycle state (trial / active / cancelled / grace / suspended). |
| **License Key** | An RSA-2048 signed `.lic` file issued by Hub for Self-Hosted deployments. Carries claims (`tenant_id`, `plan_code`, `features`, `limits`, `valid_until`, …) and a signature. See [ADR-0020](decisions/0020-triple-deployment-hybrid-license.md). |
| **Phone-Home** | The 24h verify call a Self-Hosted instance makes against Hub's `POST /api/v1/internal/license/verify`. Optional; can be disabled for air-gapped operation. |
| **Grace Period** | The 30-day window during which an instance keeps operating after the last successful phone-home. Bounded by `grace_until` in the **durable** `platform_entitlement_cache` row — never by a cache TTL. Collapsing the two makes the advertised 30 days into 15 minutes; see [26-hybrid-license-model.md § 5](architecture/26-hybrid-license-model.md). |
| **Platform Entitlement Cache** | The `platform_entitlement_cache` table in LearnStack core — the **durable** read-only mirror of the Hub-side Entitlement Aggregate, carrying `valid_until` and `grace_until`. Despite the name it is a projection store, not a cache: the volatile layers are L1 and L2 in front of it. Third in the normative read path `L1 → L2 → platform_entitlement_cache → Hub` ([ADR-0034](decisions/0034-hub-contract-surface-invariant.md)). Eager-invalidated on the entitlement-updated event; modules never read the table directly (`Modules_Do_Not_Read_Entitlement_Cache_Directly`). |
| **Operator Portal** | `operator-portal` — the separate Next.js app for Hub operators. Authenticates against the `learnstack-hub` Keycloak realm. |

## Roadmap & Delivery

| Term | Definition |
|---|---|
| **Phase** | A roadmap-level milestone with its own `phase-NN-topic.md` file under `docs/roadmap/`. Phases are numbered, sometimes letter-suffixed (`02a` / `02b` / `02c`) when sub-streams ship in parallel or in sequence. Each phase doc carries its own `## Phase Exit Decision` section spelling out the gate that closes the phase. |
| **Packet** | A dependency-ordered shipping slice **within** a phase, sized to be independently reviewable in one pull request. Packet numbering restarts per phase (`P02a-0`, `P02a-1`, …); the canonical reference shape is `P<PhaseId>-<PacketIndex>` (so the kickoff packet of Phase 02a is `P02a-0`). Commit and PR titles use the prose form (`feat(phase-02a): packet 0 — kickoff`). A packet may ship documentation only (e.g. a kickoff packet that defines the breakdown), decisions only (an ADR move from Draft to Accepted), code only, or any combination — but always one phase's worth of progress, no cross-phase bundling. Per-phase packet history lives in the phase doc's Status block (see [phase-01-repository-tooling.md](roadmap/phase-01-repository-tooling.md) for the canonical shape). |
| **Kickoff Packet** | The first packet of a phase when that phase is large enough to need an explicit plan up front. A kickoff packet ships only the per-packet breakdown for its phase plus any glossary / cross-reference updates the breakdown depends on; no code. Phase 01 did not need one (packets fell out cleanly from the existing scaffold targets); Phase 02a does (the foundation surface is wide). |
| **Walking Skeleton** | A **thin vertical slice through every layer** that produces a working, browser-visible artefact as early as the foundation allows — deliberately shallow in features and complete in path. LearnStack's is [Phase 02d](roadmap/phase-02d-walking-skeleton.md): two hosts, two tenants in unrelated domains, `Course` + `Lesson`, two read endpoints, two public pages, one binary, one database, one schema. Its purpose is evidence, not features: it moves the platform's single most testable claim — that the same code paths serve unrelated education domains — from an assertion five phases away to something a non-engineer can check in a browser. Each capability it touches is delivered shallowly there and completely in its owning phase, and each owning phase records what the skeleton already shipped so no work is claimed twice. The exit gate is a browser, not a feature set: a change that does not move a pixel on one of the two pages belongs to its owning phase. |

## List Queries

| Term | Definition |
|------|------------|
| **Cursor Pagination** | The default paging shape for every list endpoint: `?cursor=&limit=`, where the cursor is an opaque token the server minted and the client never parses. `limit` defaults to 20 and is **clamped** at 100, not rejected. Contract in [Standards 04 § Pagination](standards/04-api-design.md); kernel type `CursorPagination`. |
| **Sort Specification** | The parsed form of `?sort=`, per [Standards 04 § Filtering and Sorting](standards/04-api-design.md): an ordered list of **sort terms**, most significant first. Kernel type `SortSpecification`; parsing (`TryParse`) and authorising (`Restrict`) are separate steps, because only the endpoint knows which fields it permits. |
| **Sort Term** | One key of a sort: a field name plus a direction, where a leading `-` in the wire form means descending. Named `SortTerm` rather than `SortKey` to avoid colliding with `System.Globalization.SortKey`. |
| **Sortable Field Allow-List** | The set of field names one endpoint permits in `?sort=`. A well-formed field outside it is a **400 naming the field**, never a silently ignored key — a page returned in an order the client did not request is the one failure the client cannot detect. |

## Conventions

- `PascalCase` for entities and aggregates.
- `kebab-case` for slugs, route segments, and config keys.
- `snake_case` for database tables and columns.
- `camelCase` for JSON payloads.
