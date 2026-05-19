# Phase 02a: Platform Kernel, Multi-Tenancy, Organization, and Foundation Sockets

## Goal

Build the runtime foundation everything else stands on: shared kernel conventions,
tenant + organization resolution, tenant + organization isolation defense-in-depth,
database conventions, API conventions, **Dapr building blocks**, **APISIX gateway**,
**audit infrastructure**, **Tenant Customization aggregates**, **entitlement projection
socket**, **host-to-tenant resolver socket**, and architecture tests. This is the half
of the foundation that is least sensitive to identity provider and outbox wiring.

Phase 02b (events, outbox, identity integration) follows; Phase 02c (LearnStack Hub
Foundation in the separate `learnstack-hub` repo) runs in **parallel** with 02b. The
phases were split to reduce single-point-of-failure risk and keep each half
independently mergeable.

The decisions made in this phase are the ones that are most painful to reverse later.
They are codified in:

- [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
  (Amendment 1: Organization Scope)
- [ADR-0014 Adopt Dapr](../decisions/0014-adopt-dapr.md)
- [ADR-0015 API Gateway: APISIX](../decisions/0015-api-gateway-apisix.md)
- [ADR-0016 Audit Log Subsystem](../decisions/0016-audit-log-subsystem.md)
- [ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md)
- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)

## Scope

### Shared Kernel

- Base entity and aggregate concepts.
- Strongly typed identifiers (UUIDv7-backed, with EF value converters).
- `Entity<TId>` (append-only / audit aggregate base) and `AuditableEntity<T>` (mutable
  aggregate base with `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `DeletedAt`,
  `DeletedBy`, `Version`).
- Soft delete strategy + EF global query filter.
- Optimistic concurrency strategy.
- Domain event model (in-process MediatR-style) — wired but consumed only inside
  modules; the cross-module outbox dispatcher lives in Phase 02b.
- Result and error model (`Result<T>` with `LocalizedMessage`'s `lockey_` prefix
  invariant).
- Pagination model (cursor-first).
- `IClock`, `IRandom`, `IGuidFactory` for deterministic tests.
- **`IEventBus`, `ICacheService`, `ISecretProvider`** interfaces declared in
  `LearnStack.SharedKernel`. Default implementations: `InProcessEventBus`,
  `InMemoryCacheService`, `EnvironmentSecretProvider`. The Dapr-backed implementations
  ship in this phase too but are selected by `DeploymentMode` at composition root.
- **`IEntitlementProvider`** interface declared; `NullEntitlementProvider` ships as
  the default (returns "all features enabled, no limits"). The Hub-backed and
  signed-license-key implementations land in Phase 02c.
- **`IHostToTenantResolver`** interface declared with a Postgres-backed default that
  reads from `platform_host_to_tenant`.

### Dapr Building Blocks (Day 1)

Per [ADR-0014](../decisions/0014-adopt-dapr.md):

- Dapr sidecar runs in dev `docker-compose.yml`. Pub/sub component → Kafka. State
  component → Valkey. Secrets component → Vault (dev mode).
- `DaprEventBus`, `DaprCacheService`, `DaprSecretProvider` implementations ship in
  `LearnStack.Infrastructure`.
- Topic naming convention enforced by `Dapr_PubSub_TopicNames_FollowConvention`
  architecture test.
- Service invocation, workflow, bindings, actors are explicitly **out of scope**
  (ADR-0014 non-goals).

### APISIX Gateway (Day 1)

Per [ADR-0015](../decisions/0015-api-gateway-apisix.md):

- APISIX runs in standalone YAML-reload mode in dev `docker-compose.yml`.
- `infra/apisix/config.yaml` ships with the plugin chain wired:
  `cors` → `jwt-auth` → `limit-req` → `proxy-rewrite` → `prometheus`.
- A second route set bound to a dedicated SSL object (mTLS in APISIX is SSL-object
  config — `client.ca` / `client.depth` — not a route plugin) plus an `ip-restriction`
  on the Hub egress range, reserved Day 1 for the future `/api/internal/*` endpoints
  (the endpoints themselves arrive in 02c). The commented stub at the bottom of
  `infra/apisix/apisix.yaml` documents the canonical shape.

### Tenancy Schema Foundations

The following Tenancy-owned tables are created in this phase so later modules don't
have to retrofit:

- `tenants` — tenant root.
- `organizations` — sub-unit within a tenant per
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md). Every tenant has at
  least one default organization seeded at creation.
- `tenant_domains` — host → tenant mapping (lifecycle/verification UI lands in
  Phase 04 / Phase 06; the **Hub-owned** custom-domain admin lands in 02c).
- `tenant_locales` — default + enabled locales (see
  [ADR-0008](../decisions/0008-localization-schema.md)). Required before any
  tenant-owned content table ships.
- `tenant_settings` — non-translated tenant configuration. Org-scoped settings ride
  on a nullable `organization_id` column.
- `tenant_feature_flags` — typed feature flag overrides (see
  [21-feature-flags.md](../architecture/21-feature-flags.md) — *tenant-flag-level
  only*; plan-level features live in the entitlement projection).
- `platform_entitlement_cache` — Hub-projected entitlement cache (Hub-side population
  lands in 02c; the **table and `IEntitlementProvider` read path ship now** so the
  rest of the system can be coded against the projection from Day 1).
- `platform_host_to_tenant` — host → `(tenant_id, organization_id?)` mapping
  (Hub-populated for SaaS / Dedicated, config-populated for SelfHosted; the table
  ships now).

### Audit Infrastructure (Day 1)

Per [ADR-0016](../decisions/0016-audit-log-subsystem.md):

- `LearnStack.Infrastructure.Audit` ships with `AuditChangeTrackerInterceptor` (EF
  SaveChanges interceptor), `IAuditStateCapture` (before/after/changes JSON capture),
  and `AuditLogBehavior` (MediatR pipeline behavior).
- `LearnStack.Modules.Audit` ships with `AuditEntry` aggregate (inheriting
  `Entity<TId>`, **not** `AuditableEntity<T>`) and `AuditConfig`.
- `audit_log` table partitioned by month from Day 1; retention job (Hangfire)
  ships now with the policy from [18-audit-coverage.md](../standards/18-audit-coverage.md).
- MUST-class coverage is enabled for every command and security event the modules
  declare; modules added in later phases extend the catalog, not the infrastructure.

### Cross-cutting Concerns (Day 1)

Per [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md):

- **L1 exception handler.** `LearnStackExceptionHandler : IExceptionHandler`
  ships in `LearnStack.Api`; registered with
  `services.AddExceptionHandler<LearnStackExceptionHandler>() +
  services.AddProblemDetails()`.
- **MediatR pipeline behaviors (eight-step canonical order).**
  `ValidationBehavior` (returns `Result.Fail(validation_failed)` — never
  throws), `LoggingBehavior` (opens the 8-field `ILogger` scope + manual
  `Activity` + latency histogram), `AuditLogBehavior` (per ADR-0016 — wraps
  inner pipeline with try/catch + audit-fail entry + ExceptionDispatchInfo
  rethrow), `TenantContextBehavior` (asserts resolved + sets RLS GUCs),
  `AuthorizationBehavior` (returns `Result.Fail(forbidden)` on deny),
  `TransactionBehavior` (UoW), `OutboxFlushBehavior` (enrols outbox writes
  in current tx).
- **`Result<T>.ToActionResult()` extension** lives in
  `LearnStack.Api.Common`; every controller endpoint uses it explicitly. No
  action filter, no `ResultUnwrapBehavior`.
- **Roslyn analyzer `LearnStackException-DomainExceptionThrow`** under
  `backend/analyzers/` flags every `throw new DomainException(...)` outside
  aggregate invariant guards. Warning in Phase 02a, escalates to Error after
  Phase 03 exit.
- **`IProviderResilience<TPort>` decorator** with Polly v8
  `ResiliencePipeline` (retry + circuit breaker + timeout + bulkhead) lives
  in `LearnStack.Infrastructure.Resilience`. Configuration shape:
  `appsettings.Resilience:<port-name>:`. Every adapter is wired through the
  `AddProviderResilience<TPort, TImpl>(string portName)` composition-root
  extension. The
  [add-provider-adapter](../../.claude/skills/add-provider-adapter/SKILL.md)
  skill walks the canonical wiring.
- **Serilog primary logger + OTLP sink.** Hosts wire
  `builder.Host.UseSerilog(...)` with `WriteTo.Console(...)` +
  `WriteTo.OpenTelemetry(...)`. The OTel `LoggerProvider`
  (`AddOpenTelemetry().WithLogging()`) is **not** registered alongside.
  Modules log through `ILogger<T>` only.
- **OpenTelemetry SDK** wired with `AddAspNetCoreInstrumentation` +
  `AddHttpClientInstrumentation` + `AddEntityFrameworkCoreInstrumentation` +
  `AddProcessor<TenantContextSpanProcessor>` + `AddOtlpExporter`. Manual
  `ActivitySource` named per module (`learnstack.<module>`) for use-case
  spans.
- **`ITenantContextAccessor`** (singleton, `AsyncLocal<ITenantContext?>`-backed)
  lives in `LearnStack.SharedKernel` alongside the request-scoped
  `ITenantContext`. The scoped interface is what handlers and services
  inject; the singleton accessor is what cross-cutting infrastructure
  (OTel processor, Serilog enricher, Sentry enricher) reads. The accessor
  is populated at scope start by `TenantResolverMiddleware` (HTTP),
  `HubCorrelationMiddleware` (`/api/internal/*`), Hangfire `JobActivator`
  (background jobs), and the outbox / inbox handler scope (integration
  events). Modules never write to the accessor.
- **`TenantContextSpanProcessor : BaseProcessor<Activity>`** lives in
  `LearnStack.Infrastructure.Observability`; reads from
  `ITenantContextAccessor.Current` in its `OnStart` hook and enriches every
  span with `tenant.id`, `organization.id`, `user.id`, `module`,
  `correlation.id`.
- **`IErrorTrackingProvider` socket.** Three implementations land:
  `NoOpErrorTracker`, `SentryErrorTracker`, `LocalFileErrorTracker`.
  Composition root branches on `DeploymentMode`. DSN comes from
  `ISecretProvider`. Modules never reference `Sentry.SentrySdk`.

### Tenant Customization Foundation (Day 1)

Per [ADR-0018](../decisions/0018-tenant-driven-customization-model.md):

- `LearnStack.Modules.Customization` ships with `TenantContentType`,
  `TenantPageBlock`, `TenantLessonItemType`, `TenantLevelTaxonomy`,
  `TenantScoringRule`, `TenantCompletionRule`, `TenantCustomFieldDef`,
  `TenantTemplateLibrary` aggregates and their schemas tables.
- The runtime read paths (JSON Schema validators, sandboxed DSL stub) ship now; the
  Admin Studio editors land in Phase 06.
- A small built-in seed (a `default-card` page-block composite, a stock `Plain` level
  taxonomy) lets early phases exercise the customization runtime without depending on
  a real tenant data set.

### Tenant + Organization Resolution

Context is resolvable from:

- Custom domain (via `platform_host_to_tenant`).
- Subdomain on the platform domain (still via `platform_host_to_tenant`).
- Org-scoped subdomain (`branch-istanbul.example.edu` → tenant + organization).
- Explicit tenant + organization selector headers for admin/studio usage.
- API request headers (`X-Tenant-Id`, `X-Organization-Id`).
- Background job parameter.
- Integration event envelope (envelope contract defined in Phase 02b; the resolver
  respects it from the start).

Implementation:

- `IHostToTenantResolver` (Postgres-backed default reading `platform_host_to_tenant`).
- `TenantResolverMiddleware`.
- `ITenantContext` (request-scoped) exposing `TenantId`, `OrganizationId?`, `UserId?`.
- Tenant- and org-aware query conventions.
- Tenant + org context propagation seams for Hangfire jobs and outbox dispatcher
  handlers (wired in 02b).

### Tenant + Organization Isolation — Defense in Depth

Implemented **from day one**, not deferred to hardening. Two enforcement layers, both
required, applied to both dimensions where the entity is org-scoped:

1. **EF Core global query filters** on every entity implementing `ITenantOwned` and
   `IOrganizationScoped`.
2. **PostgreSQL Row Level Security** policies on every tenant-owned table, with
   `app.tenant_id` and (where applicable) `app.organization_id` session variables set
   per connection lease via a `DbConnectionInterceptor`. Transaction-local
   `set_config(..., true)` is the primitive.

Platform-admin cross-tenant access is explicit, scoped, audited
(`EnterPlatformAdminScope(reason)`). See
[Tenant Isolation](../architecture/09-tenant-isolation.md).

### Database Conventions

- Naming, indexing, migration, JSONB, soft-delete, audit, concurrency rules per
  [Database Standards](../standards/05-database.md).
- Required columns and RLS policy on every tenant-owned table; verified by
  architecture tests.
- Required `OrganizationId` column + RLS policy on every `[OrganizationScoped]`
  entity.
- Migrations append-only after merge; destructive changes go through a deprecation
  window.

### API Conventions

Per [API Standards](../standards/04-api-design.md):

- REST + URL versioning (`/v1/...`).
- Problem Details (RFC 7807) for errors.
- Cursor pagination.
- Idempotency keys for write endpoints with external side effects.
- Optimistic concurrency via ETag / `version`.
- Correlation IDs in headers and logs.
- OpenAPI generated from code; SDK generated from spec.
- Tenant + organization headers (`X-Tenant-Id`, `X-Organization-Id`) bound on every
  request.

### Configuration

- Strongly typed options bound from `ISecretProvider` (Vault) + env vars +
  `appsettings.*.json`. Vault wins.
- Environment-based configuration (dev / staging / prod) and
  **`DeploymentMode`-based composition** (Development / SaaS / Dedicated /
  SelfHostedOnline / SelfHostedAirGapped) per
  [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md).
- Secret handling — never in source.
- Tenant-level + organization-level settings model with a typed accessor.

### Architecture Tests (Day 1)

The architecture test project starts going green during this phase. Phase 02a covers:

- Module dependency direction.
- No cross-module Domain/Infrastructure references.
- Every `[TenantOwned]` entity has filter and RLS policy.
- Every `[OrganizationScoped]` entity has org filter + RLS
  (`Every_OrgScoped_Entity_HasOrgIdAndFilter`).
- No `IgnoreQueryFilters()` outside platform-admin module.
- Audit-coverage matrix file exists per module.
- `Dapr_PubSub_TopicNames_FollowConvention`.
- `AuditEntry_Inherits_Entity_Not_AuditableEntity`.
- `LearnStack_Modules_DoNotReference_Hub`.
- `Modules_Do_Not_Inject_Valkey_Directly`, `Modules_Do_Not_Read_Entitlement_Cache_Directly`,
  `Modules_Do_Not_Write_AuditLog_Directly`.
- `Modules_Do_Not_Reference_DeploymentMode` — modules never read `DeploymentMode`
  directly; the composition root selects provider implementations once. See
  [20-infrastructure-stack.md § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md).
- `Core_Modules_HaveNo_DomainSpecific_Names`,
  `No_Source_Folder_Named_Verticals`.
- `IExceptionHandler_Registered_AtStartup` — every host registers
  `LearnStackExceptionHandler`.
- `MediatR_Pipeline_Order_Matches_Canonical_Sequence` — DI registration produces the eight-step
  pipeline in the canonical order.
- `ValidationBehavior_DoesNotThrow_ValidationException` — runtime assertion
  via an integration test that triggers a validation failure.
- `Domain_Methods_Do_Not_Throw_For_Expected_Cases` — uses the
  `LearnStackException-DomainExceptionThrow` Roslyn analyzer report.
- `Adapters_Wrap_Provider_Exceptions` — provider SDK exception types do not
  leave `LearnStack.Infrastructure.<Adapter>` namespaces.
- `Modules_Do_Not_Reference_Sentry_SDK_Directly`.
- `Logging_Goes_Through_Microsoft_Extensions_Logging` — modules import
  `Microsoft.Extensions.Logging.ILogger<T>`, not `Serilog.ILogger`.
- `OTel_Pipeline_Includes_TenantContextSpanProcessor`.
- `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing` — unit
  test guard.

The event/outbox-specific tests (serialisable records, job payloads with `TenantId`)
land in Phase 02b.

## Deliverables

- Shared kernel package with `IEventBus` / `ICacheService` / `ISecretProvider` /
  `IEntitlementProvider` / `IHostToTenantResolver` interfaces and dev-mode defaults.
- Dapr sidecar + Kafka + Vault wired in dev compose; pub/sub component, state
  component, secrets component all functional.
- APISIX standalone gateway running in dev compose with the documented plugin chain.
- Tenant-aware + organization-aware API foundation with both EF filters and
  PostgreSQL RLS active for both dimensions.
- `LearnStack.Modules.Customization` aggregates + runtime read paths.
- `LearnStack.Modules.Audit` aggregates + `LearnStack.Infrastructure.Audit` pipeline +
  partitioned `audit_log` table + retention job.
- `platform_entitlement_cache` and `platform_host_to_tenant` tables + read paths.
- Cross-cutting foundation (per
  [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)):
  `LearnStackExceptionHandler`, 8-step MediatR pipeline, `Result.ToActionResult`
  extension, `IProviderResilience<TPort>` decorator, Serilog + OTLP sink,
  `TenantContextSpanProcessor`, `IErrorTrackingProvider` with three
  implementations, Roslyn analyzer for `DomainException`. The
  [wire-cross-cutting-foundation](../../.claude/skills/wire-cross-cutting-foundation/SKILL.md)
  skill walks the canonical wiring.
- Database conventions implemented and enforced.
- API conventions wired (versioning, Problem Details, cursor pagination, idempotency,
  ETag).
- Architecture test project running with the Phase-02a rules.
- Tenant + organization context tests passing for at least two seed tenants, each
  with at least two organizations.

## Completion Criteria

- A request reliably resolves its tenant **and** organization.
- Unknown hosts return 404 (no platform disclosure).
- Tenant-owned queries cannot leak across tenants — verified by integration test pair
  (`Tenant_A_cannot_read_Tenant_B_data`, `Unsetting_tenant_context_returns_zero_rows_through_RLS`).
- Org-scoped queries cannot leak across organizations within the same tenant —
  verified by `Org_X_cannot_read_Org_Y_within_TenantA`.
- API errors use Problem Details consistently.
- Platform-admin scope writes an audit event via `AuditLogBehavior`; the
  `AuditChangeTrackerInterceptor` captures EF state changes; both flow into
  `audit_log` and survive a manual retention-job dry run.
- A MUST-audit operation written through any seed module produces an entry with
  `before` and `after` snapshots.
- `IFeatureFlags.IsEnabledAsync(FeatureKey)` reads the `NullEntitlementProvider`'s
  default of "all enabled"; flipping `DeploymentMode = SelfHosted` and pointing at a
  signed-license-key file produces the limited feature set without code change.
- `IHostToTenantResolver` resolves a seed custom-domain row.
- Architecture tests for tenant + org ownership, RLS, module-boundary direction,
  audit pipeline, and Dapr conventions are not skippable.

## Risks

- Leaving tenant or organization enforcement to developer discipline; mitigated by
  RLS + architecture tests.
- Treating RLS as optional "later" hardening — explicitly rejected by ADR-0003.
- Premature event/outbox work before tenant isolation is stable — moved to Phase 02b
  on purpose.
- Audit pipeline overhead on hot paths — mitigated by `AuditConfig` MAY-class skip and
  by partitioned-table inserts; budget reviewed in Phase 11.

## Phase Exit Decision

Phase 02b can begin when tenant + organization resolution, isolation tests, audit
pipeline, customization runtime read paths, API conventions, and the architecture
test gate are stable and green in CI. Phase 02c (Hub Foundation in the separate
`learnstack-hub` repo) may start **in parallel** with 02b — both consume the sockets
already in place from 02a.

### ADR commitments that must land in this phase

Per [decisions/README.md § Open ADR Drafts](../decisions/README.md), three drafts
target Phase 02a and **must be Accepted before this phase exits**:

| Reserved # | Topic | Why it blocks Phase 02a |
|---|---|---|
| ADR-0023 | Strongly-typed ID source generator (Vogen vs StronglyTypedId vs custom) | EF interceptors and value converters depend on the emitter at compile time |
| ADR-0024 | API versioning policy (deprecation cadence, sunset headers, `/v1/` convention) | OpenAPI spec + SDK generation start in this phase |
| ADR-0028 | `audit_log` monthly partition management (Hangfire job vs `pg_partman`) | Partition policy ships Day 1 with the audit infrastructure |

Each PR that lands one of these ADRs flips the corresponding "exit checklist"
item; the phase is not "done" until all three are Accepted and CI exercises the
chosen implementation.
