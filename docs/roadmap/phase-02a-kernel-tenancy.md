# Phase 02a: Platform Kernel, Multi-Tenancy, Organization, and Foundation Sockets

> **Status (2026-05-20).** Phase 02a in progress. Packet 0 (kickoff) ships the
> breakdown plan; subsequent packets ship the foundation incrementally. Each
> packet is independently reviewable in its own commit, matching the
> [Phase 01 cadence](phase-01-repository-tooling.md).
>
> The packet order is dependency-driven: a later packet may consume any earlier
> packet's deliverables, but never the reverse. Packets land sequentially on
> `main` via their own pull request.
>
> **Packet 0 — Kickoff ✅ (this commit)**
> Phase 02a packet breakdown captured in this Status block. Glossary
> entries for "Phase", "Packet", and "Kickoff Packet" added under a new
> *Roadmap & Delivery* group so the terms are defined in exactly one
> place. No code, no ADR state changes — Packet 0 is a planning slice
> that unblocks the rest of the phase by fixing the order.
>
> **Packet 1 — Foundation decisions ✅**
> The three Phase-02a-blocking ADRs are now Accepted:
> [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md)
> picks **Vogen** as the source generator for both IDs and value objects;
> [ADR-0024](../decisions/0024-api-versioning-policy.md) codifies
> URL-based versioning with a **6-month deprecation window** + RFC 8594
> `Sunset` / `Deprecation` headers + OpenAPI `x-sunset` extensions;
> [ADR-0028](../decisions/0028-audit-log-partition-management.md) picks
> a **Hangfire recurring job** (`learnstack:audit:partition-management`)
> over `pg_partman` to keep the SelfHostedAirGapped story extension-free.
> Decision-only; no code. Standards 02 § Strongly-Typed Identifiers and
> Standards 04 § Versioning cross-link to the new ADRs.
>
> **Packet 2 — Shared Kernel core ✅**
> `IClock` + `SystemClock` / `FixedClock`, `IRandom` + `SystemRandom` /
> `FixedRandom`, `IGuidFactory` + `SystemGuidFactory` / `FixedGuidFactory`
> (deterministic-test abstractions per Standards 02 § Time);
> `LocalizedMessage` carrying the `lockey_` prefix invariant at the
> constructor (used by every `Result.Fail` and success-message payload);
> `Error` refactored to wrap `LocalizedMessage` with a `Code` projection
> over `Message.Key`; `Result<T>` extended with `IResultBase`, success-message
> overload, and the static `Result.FailFor<T>(error)` factory ADR-0032's
> `ValidationBehavior` consumes; `Entity<TId>` (append-only / audit
> aggregate base) + `AuditableEntity<TId>` (mutable aggregate base with
> `CreatedAt` / `CreatedBy` / `UpdatedAt` / `UpdatedBy` / `DeletedAt` /
> `DeletedBy` / `Version` plus the `IsDeleted` projection);
> `ISoftDelete` + `IOptimisticConcurrency` marker interfaces;
> `IDomainEvent : INotification` + `DomainEvent` base + `IHasDomainEvents`
> aggregate-side collector; cursor-first pagination
> (`CursorPagination` / `Page<T>` / `PageInfo` matching Standards 04
> § Pagination). Vogen 7.0.0 wired per ADR-0023 with
> `LearnStackVogenDefaults.IdMask` carrying the canonical
> `EfCoreValueConverter | SystemTextJson | TypeConverter` mask;
> `IAggregateRoot<TId>` / `IHasId<TId>` interfaces require `TId :
> IStronglyTypedId<Guid>` so future module aggregates inherit the
> constraint. Unit / architecture / contract suites all green in CI; the
> `VogenIdEmissionTests` smoke test asserts the emitter pipeline
> (Vogen `[ValueObject<Guid>]` → `IStronglyTypedId.Value` → JSON
> round-trip → `TypeConverter` round-trip) end-to-end via a synthetic
> `TestId` in the test project.
> Review pass folded in (commit `7c9133a`): `Entity<TId>` equality carries
> transient + cross-runtime-type guards; `Result.FailFor<TResponse>` returns
> the concrete `TResponse` via reflection (not `Result<TResponse>`);
> `Error.Code` is the unprefixed stable identifier projected from
> `Message.Key`; `Error.Details` flows `LocalizedMessage` lists so the prefix
> invariant covers field-level errors; `Result<T>.Ok` rejects null;
> `UserId` is a SharedKernel-level Vogen value object used by
> `AuditableEntity<TId>` instead of raw `Guid`; `DomainEvent.EventId` /
> `OccurredAt` are `required init`; `MarkCreated` throws on second call;
> `SoftDelete` bumps `UpdatedAt` for monotonic last-touched;
> `CursorPagination` validates `Limit > 0` at the ctor. Standards 01
> § Dependency Direction grows a "Build-time-only exceptions" sub-section
> for the EF Core + MediatR references SharedKernel requires. Unit /
> architecture / contract suites all green in CI.
>
> **Packet 3 — Cross-cutting foundation ✅**
> The [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)
> surface is wired end to end via the
> [wire-cross-cutting-foundation](../../.claude/skills/wire-cross-cutting-foundation/SKILL.md)
> skill. Shipped:
>
> - L1 `LearnStackExceptionHandler : IExceptionHandler` registered through
>   `services.AddExceptionHandler<T>()` + `app.UseExceptionHandler()`;
>   `ShouldCapture(ex)` switch drives the Sentry-vs-OTel boundary per
>   Standards 09 (`OperationCanceledException` + client-error
>   `ProviderException` skip capture).
> - Eight-step MediatR pipeline in `LearnStack.Application/Pipeline/`:
>   `Validation` + `Logging` are full implementations; `AuditLog` ships the
>   try / `ExceptionDispatchInfo` rethrow shell (audit-write lights up in
>   Packet 9 when `IAuditStore` lands); `TenantContext` shell short-circuits
>   with `Result.Fail(tenant_mismatch)` until the resolver middleware arrives
>   in Packet 7; `Authorization` / `Transaction` / `OutboxFlush` pass-through
>   shells whose registration order is the binding part. Order encoded in
>   `MediatRPipelineRegistration.CanonicalBehaviorOrder` and asserted by the
>   `MediatR_Pipeline_Order_Matches_Canonical_Sequence` architecture test.
> - `Result<T>.ToActionResult()` extension in `LearnStack.Api.Common`
>   alongside `ProblemDetailsFactory` + `HttpStatusMap`; explicit at every
>   future controller endpoint per Standards 09.
> - `LearnStackException` hierarchy in `LearnStack.SharedKernel/Errors/`:
>   `DomainException`, `InfrastructureException`, `ProviderException` (with
>   `IsClientError` flag), `TenantContextMissingException`.
>   `LearnStackException-DomainExceptionThrow` Roslyn analyzer under
>   `backend/analyzers/LearnStack.Analyzers/` flags `throw new DomainException`
>   in `Domain` + `Application` (warning class in Phase 02a, escalates to
>   error after Phase 03 exit per ADR-0032 § Sub-decision 4).
> - `IProviderResilience<TPort>` socket in
>   `LearnStack.SharedKernel/Resilience/` + Polly v8
>   `ResiliencePipeline` builder in
>   `LearnStack.Infrastructure.Resilience/`. Pipeline = retry (only
>   `InfrastructureException` + non-client `ProviderException`) → circuit
>   breaker → timeout → bulkhead. Configuration shape:
>   `appsettings.Resilience:<portName>:` (sample lit up under
>   `liveclass`, `payment`, `storage`, `search`). Per-adapter decoration is
>   the adapter's responsibility — the socket is what adapters consume in
>   Phase 02b+ via the [add-provider-adapter](../../.claude/skills/add-provider-adapter/SKILL.md)
>   skill. Hub HTTP clients excluded per Sub-decision 5.
> - Serilog primary logger wired in `LearnStack.Api/Composition/CrossCuttingFoundationExtensions.cs`
>   with `WriteTo.Console(RenderedCompactJsonFormatter)` +
>   `WriteTo.OpenTelemetry(OTLP gRPC)`. The OTel `LoggerProvider` is
>   intentionally **not** registered alongside per ADR-0032 § Sub-decision 8.
> - OpenTelemetry SDK with `AspNetCore` + `HttpClient` +
>   `EntityFrameworkCore` instrumentations + the OTLP exporter, plus the
>   singleton `TenantContextSpanProcessor` from
>   `LearnStack.Infrastructure.Observability/` that enriches every span
>   with `tenant.id` / `organization.id` / `user.id` / `correlation.id` /
>   `module` from the singleton
>   `ITenantContextAccessor` (`AsyncLocal<ITenantContext?>`-backed).
> - `IErrorTrackingProvider` socket in `LearnStack.SharedKernel/Observability/`
>   with three implementations in `LearnStack.Infrastructure.ErrorTracking/`:
>   `NoOpErrorTracker` (Development), `SentryErrorTracker` (SaaS / Dedicated /
>   SelfHostedOnline-with-DSN), `LocalFileErrorTracker`
>   (SelfHostedAirGapped, writes JSON envelopes to a configured directory).
>   Composition-root branching by `DeploymentMode` per Standards 20 table.
>   Sentry SDK is referenced only by the ErrorTracking project — enforced
>   by the `Modules_Do_Not_Reference_Sentry_SDK_Directly` architecture test.
> - Architecture tests green (`backend/tests/LearnStack.Tests.Architecture/CrossCuttingFoundationTests.cs`):
>   `MediatR_Pipeline_Order_Matches_Canonical_Sequence`,
>   `IExceptionHandler_Registered_AtStartup`,
>   `OTel_Pipeline_Includes_TenantContextSpanProcessor`,
>   `Logging_Goes_Through_Microsoft_Extensions_Logging`,
>   `Modules_Do_Not_Reference_Sentry_SDK_Directly`,
>   `Adapters_Wrap_Provider_Exceptions`,
>   `IErrorTrackingProvider_Is_Singleton`. Unit tests green for
>   `ValidationBehavior` (returns `Result.Fail(validation_failed)`,
>   never throws `ValidationException`), `AuditLogBehavior` (preserves
>   stack via `ExceptionDispatchInfo`), `TenantContextBehavior`
>   (short-circuits unresolved context), `TenantContextSpanProcessor`
>   (`OnStart` is null-safe and enriches resolved spans),
>   `ProviderResilience<TPort>` (retries non-client failures, skips
>   client-error retries), `HttpStatusMap` (mirrors Standards 09 table),
>   `ResultExtensions.ToActionResult()` (Problem Details shape),
>   `LocalFileErrorTracker` (writes the JSON envelope).
> - Initial validation (pre-review): `LearnStack.Tests.Unit` 111/111,
>   `LearnStack.Tests.Architecture` 25/25, `LearnStack.Tests.Contract` 1/1,
>   `LearnStack.Tests.Integration` 5/5 green under `CI=true`. Superseded by
>   the post-review-3/4 counts below.
>
> Review fixes (commit `<follow-up>`):
>
> - **B1** — Sentry DSN now reads via the new `ISecretProvider` socket
>   (`LearnStack.SharedKernel/Secrets/`) with `ConfigurationSecretProvider`
>   as the Phase 02a default; Packet 5 swaps in `DaprSecretProvider` for
>   Vault. ADR-0032 § Sub-decision 9 contract honoured.
> - **B2** — Serilog pipeline gains `CorrelationContextEnricher`
>   (copies tenant / org / user / correlation / module from
>   `ITenantContextAccessor` onto every log event) +
>   `RedactSensitiveFieldsEnricher` (scrubs password / token / secret /
>   DSN / JWT / authorization / SSN / TCKN / card-number tokens before
>   the formatter touches them). `LocalFileErrorTracker` redacts the
>   same token set on `CapturedContext.AdditionalTags`.
> - **M1** — `ProblemDetailsFactory.For(Exception)` routes status through
>   `HttpStatusMap.For(Exception)` so `ProviderException(IsClientError:true)`
>   returns 400 instead of falling through to 503 via the carried Error's
>   default code.
> - **M2** — L1 handler now skips `Activity.AddException` for
>   `ProviderException(IsClientError:true)` per Standards 09 § Sentry vs
>   OpenTelemetry table — `SetStatus(Error)` only, no exception event.
> - **M3** — All 14 module Domain + Application csproj files now reference
>   `LearnStack.Analyzers` via `OutputItemType="Analyzer"`. Future
>   `throw new DomainException(...)` in any module fails the analyzer.
> - **M4** — Polly `IProviderResilience<TPort>` pipeline now consumes
>   `BulkheadOptions` via `Polly.RateLimiting`'s
>   `AddRateLimiter(ConcurrencyLimiterOptions)`; the silent-dead config
>   gap is closed.
> - **N1** — `ProblemDetailsFactory` projects nested
>   FluentValidation property paths (`Address.Street`) and acronyms
>   (`URLValue`) to the right camelCase shape via
>   `JsonNamingPolicy.CamelCase`.
> - **N2** — `ToActionResult()` returns `ProblemDetailsActionResult`
>   which builds the body inside `ExecuteResultAsync(ActionContext)`, so
>   the sanctioned `(await Send(...)).ToActionResult()` shape populates
>   `Instance` + `correlationId` without the caller threading
>   `HttpContext`.
> - **N3 / A6** — `LocalFileErrorTracker` file names suffix a Guid for
>   guaranteed uniqueness in same-millisecond bursts; `stackalloc` is
>   capped at 128 chars so a multi-KB inbound `traceparent` cannot blow
>   the stack.
> - **A5** — `AuditLogBehavior` catch filter excludes
>   `OperationCanceledException` so client disconnects no longer churn
>   warning logs / future audit rows.
> - **A7** — `MediatRPipelineRegistration.CanonicalBehaviorOrder`
>   documentation explicitly notes "7 behaviors + the handler at the
>   innermost position = the 8 canonical steps".
> - **A8** — `ProblemDetailsFactory` strips the `_failed` suffix from
>   the Problem `type` URL (matches the Standards 09 § API Surface
>   example: `/validation`, not `/validation_failed`).
> - **A11** — New HTTP-level integration tests in
>   `LearnStack.Tests.Integration/CrossCuttingFoundationHttpTests` exercise
>   the L1 handler + ValidationBehavior end-to-end via
>   `WebApplicationFactory<Program>` and a synthetic test controller. The
>   Standards 21 catalogue row for
>   `ValidationBehavior_DoesNotThrow_ValidationException` is updated to
>   "unit + integration".
> - **A12** — `LearnStackExceptionHandler` is `internal sealed` — only
>   the framework's `AddExceptionHandler<T>()` instantiates it; tests
>   reach the type via `InternalsVisibleTo`.
> - **A13** — `TenantContextSpanProcessor` stringifies Guid tags
>   (`tenant.id` / `organization.id` / `user.id`) so the wire format is
>   stable across exporters.
> - **S1** — `MediatR_Pipeline_Order_Matches_Canonical_Sequence` test
>   asserts a hardcoded behavior-type sequence, not the production
>   `CanonicalBehaviorOrder` list, so an accidental list reorder cannot
>   sneak past.
> - **SU1** — L1 handler skips the body write on
>   `OperationCanceledException` / cancelled `CancellationToken`; the
>   client has already disconnected.
> - **SU4** — `TenantContextBehavior.AllowsUnresolvedContext` predicate
>   carries a TODO documenting the Packet 7 marker-attribute seam
>   (`[AllowsUnresolvedTenantContext]`).
> - New architecture test `Modules_Do_Not_Reference_DeploymentMode` lit
>   up — catalogue entry existed since ADR-0020 but had no implementation
>   until now.
>
> Review-2 fixes:
>
> - **N4** — `CrossCuttingFoundationExtensions.SelectSecretProvider`
>   becomes the single composition-root site that picks the
>   `ISecretProvider` implementation per `DeploymentMode`. Both the DI
>   registration and the local `AddLearnStackErrorTracking` argument
>   read the same instance. Packet 5's `DaprSecretProvider` swap now
>   touches one line, not two.
> - **SU5** — `SensitiveTokenCatalog` in
>   `LearnStack.SharedKernel/Secrets/` is the single source of truth for
>   the sensitive-property-name token list. Both
>   `RedactSensitiveFieldsEnricher` and
>   `LocalFileErrorTracker.RedactSensitiveTags` consume
>   `SensitiveTokenCatalog.IsSensitive(...)` so the two redaction
>   surfaces cannot drift. The catalogue now includes `vkn` (Vergi
>   Kimlik Numarası — Turkish corporate tax number, common for
>   instructor-owned sole proprietorships) alongside `tckn`.
> - **SU6** — `RedactSensitiveFieldsEnricher` remarks carry a dated
>   TODO naming the Packet 7+ Roslyn analyzer that should extend
>   `LearnStack.Analyzers` to flag string-interpolated
>   `throw new ...Exception($"...{token}...")` patterns in `Domain` +
>   `Application` projects. Runtime redaction covers logs / OTLP / Sentry
>   tags; the analyzer closes the last gap (secrets in exception
>   messages) at compile time.
> - **SU7** — `HttpStatusMap.For(Exception)` carries a rationale comment
>   for the non-IETF `499` "client closed request" status: matches
>   Nginx / IIS / Envoy / APISIX behaviour, keeps client disconnects
>   off the error-budget axis, and points at the L1 handler's skip-body
>   contract. If a future ADR pins a different code, the one comment
>   block is the seam to change.
>
> Review-3/4 fixes:
>
> - **H1 (blocker)** — the `DomainExceptionThrow` analyzer used a hyphenated
>   Roslyn diagnostic id, which Roslyn rejects (`AD0001` crash at report
>   time → CI build break under `TreatWarningsAsErrors` the first time a
>   `DomainException` is thrown). Fixed: diagnostic id is now `LS0001`
>   (valid identifier); `LearnStackException-DomainExceptionThrow` is
>   retained as the human-readable rule name. `LS0001` is listed in
>   `WarningsNotAsErrors` until the Phase 03 escalation so a legitimate
>   aggregate-invariant throw does not break CI. New
>   `DomainExceptionThrowAnalyzerTests` (`LearnStack.Tests.Unit`) run the
>   analyzer over synthetic compilations and assert `LS0001` is emitted
>   (no `AD0001`). Recorded as [ADR-0032 Amendment 1](../decisions/0032-exception-handling-logging-and-observability.md);
>   Standards 21 naming convention + analyzer entry updated.
> - **Provider error body/status consistency** — `HttpStatusMap.For(Exception)`
>   now derives the HTTP status from the carried `Error.Code` for every
>   `LearnStackException`, instead of special-casing
>   `ProviderException.IsClientError → 400`. `IsClientError` is purely an
>   observability concern (it gates Sentry capture), so a bare provider
>   failure is `dependency_unavailable` → 503 and an adapter surfacing a
>   provider 4xx passes an explicit `Error` (e.g. `validation_failed` → 400);
>   either way body code and status agree. Tests updated to assert both.
> - **Redaction over-match + nesting** — `SensitiveTokenCatalog.IsSensitive`
>   now matches on word-segment boundaries (camelCase / `_ . -`) instead of
>   raw substrings, so `ClassName` / `BusinessName` are no longer redacted
>   by the `ssn` token while `Password` / `ApiKey` / `SSNToken` still are.
>   `RedactSensitiveFieldsEnricher` recurses into destructured objects,
>   dictionaries, and sequences so a sensitive field nested in a
>   non-sensitive top-level property (`User.Password`) is scrubbed; lazy
>   reconstruction keeps clean events allocation-free. New
>   `SensitiveTokenCatalogTests` + `RedactSensitiveFieldsEnricherTests`.
> - **OTel naming + air-gapped** — the manual `AddSource` / `AddMeter`
>   filters use the documented lowercase `learnstack.*` convention (matching
>   the `learnstack.mediatr` ActivitySource without relying on
>   case-insensitive wildcard matching). `WireSerilog` / `WireOpenTelemetry`
>   now take `DeploymentMode`; `SelfHostedAirGapped` never wires the network
>   OTLP exporters (no-egress contract), with a dated TODO for the
>   `/var/learnstack/otel/` file target deferred to Phase 11 ops.
> - **M1** — new `Handlers_Return_Result` architecture test asserts every
>   `IRequestHandler<,TResponse>` has `TResponse : IResultBase`, so a
>   raw-DTO handler can't silently bypass the pipeline (validation / audit /
>   tenant-context + RLS). Vacuous today, active when handlers land.
> - **L4** — the Serilog enrichers are resolved from DI (the singletons
>   registered in `AddLearnStackObservabilityServices`) instead of being
>   `new()`'d in the pipeline, so there are no dead registrations.
> - **Docs** — `Domain_Methods_Do_Not_Throw_For_Expected_Cases` marked
>   **deferred** in Standards 21 (the `LS0001` analyzer already enforces the
>   rule at build time; the report-walking architecture test lands with
>   module domain code in Packet 6+). LoggingBehavior activity-name doc,
>   `correlationId`-as-full-traceparent in Standards 09/10, and the
>   `IMeterFactory.Create("learnstack.<module>")` example in architecture 33
>   reconciled with the code.
> - **Skipped (with reason)** — the resilience pipeline order
>   (retry → breaker → timeout → bulkhead) is left as-is: it is faithful to
>   ADR-0032 § Sub-decision 5's stated order. Whether the concurrency
>   limiter should sit outermost (to cap total in-flight including retries,
>   per the `Microsoft.Extensions.Resilience` standard handler) is an
>   ADR-level question for a future amendment, not a code defect in this
>   packet.
>
> Validation after review-3/4: `dotnet build LearnStack.slnx` (CI=true)
> clean; `LearnStack.Tests.Unit` 154/154, `LearnStack.Tests.Architecture`
> 26/26, `LearnStack.Tests.Integration` 5/5, `LearnStack.Tests.Contract`
> 1/1 green. (CI's `dotnet format --verify-no-changes` step also gates the
> backend job — run `dotnet format LearnStack.slnx --verify-no-changes`
> locally before pushing.)
>
> **Deferred follow-ups carried out of Packet 3** (each is recorded in its
> owning packet/phase below so it does not slip; no separate issue tracker
> needed — the roadmap is the backlog):
>
> - **Strongly-typed IDs for `ITenantContext` + `CapturedContext`** →
>   **Packet 7** (after the `TenantId` / `OrganizationId` Vogen value
>   objects land in Packet 6). Both contracts use raw `Guid` in Packet 3
>   because those VOs don't exist yet (only `UserId` does); they convert
>   together in one pass to avoid a half-typed intermediate.
> - **`[AllowsUnresolvedTenantContext]` marker attribute** for the
>   `TenantContextBehavior` opt-out (tenant-provisioning / platform-admin
>   commands) → **Packet 7**.
> - **`TransactionBehavior` / `OutboxFlushBehavior` shells light up** →
>   **Packet 6** (UoW transaction, per-module `DbContext`) and **Phase 02b**
>   (outbox enrolment) respectively.
> - **`AuthorizationBehavior` shell lights up** + **`LS0001` analyzer
>   severity escalates Warning → Error** (and is removed from
>   `WarningsNotAsErrors`) → **Phase 03 exit**.
> - **`Domain_Methods_Do_Not_Throw_For_Expected_Cases`** report-walking
>   architecture test → **Packet 10** (needs module domain code to walk;
>   until then the `LS0001` analyzer only does partial detection — it
>   reports every direct `throw new DomainException(...)` in `Domain` /
>   `Application` as a build-time Warning, non-blocking under
>   `WarningsNotAsErrors` until the Phase 03 exit escalation above, and does
>   not replace the broader report-walking test).
> - **Air-gapped OTLP file target** (`/var/learnstack/otel/`) → **Phase 11**
>   ops (the no-egress branch already prevents network export in
>   `SelfHostedAirGapped`; the file sink needs an exporter-package decision,
>   the operational controls in
>   [phase-11-production-hardening.md](phase-11-production-hardening.md#observability),
>   and a test asserting no network telemetry exporter is ever wired under
>   `SelfHostedAirGapped`).
> - **"No secrets in exception messages" Roslyn analyzer** (compile-time
>   complement to the runtime redactor) → **Phase 02b or later** (code TODO
>   in `RedactSensitiveFieldsEnricher`).
> - **Resilience pipeline order** (should the concurrency limiter sit
>   outermost to cap total in-flight incl. retries?) → **future ADR-0032
>   amendment**; the current order is faithful to ADR-0032 § Sub-decision 5.
>
> **Packet 4 — API conventions ⏳**
> REST + URL versioning (`/v1/...` per ADR-0024), Problem Details (RFC 7807)
> shape on every error, cursor pagination, idempotency keys for write
> endpoints with external side effects, ETag concurrency, correlation IDs
> in headers and logs, OpenAPI generation from code + SDK generation
> scaffolding, tenant + organization header binding (`X-Tenant-Id`,
> `X-Organization-Id`).
>
> **Packet 5 — Dapr building blocks + APISIX config ⏳**
> `IEventBus` / `ICacheService` / `ISecretProvider` interfaces in
> `LearnStack.SharedKernel` + `InProcessEventBus` / `InMemoryCacheService` /
> `EnvironmentSecretProvider` defaults + `DaprEventBus` / `DaprCacheService` /
> `DaprSecretProvider` adapters in `LearnStack.Infrastructure`. Composition-root
> branching by `DeploymentMode` (Development / SaaS / Dedicated /
> SelfHostedOnline / SelfHostedAirGapped). APISIX `config.yaml` finalised
> with the documented plugin chain
> (`cors` → `jwt-auth` → `limit-req` → `proxy-rewrite` → `prometheus`) and
> the `/api/internal/*` SSL-object + `ip-restriction` stub reserved for the
> Phase 02c Hub egress. `Dapr_PubSub_TopicNames_FollowConvention`
> architecture test goes green.
>
> **Packet 6 — Tenancy schema foundations ⏳**
> Migrations + EF configurations for `tenants`, `organizations` (per
> [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)),
> `tenant_domains`, `tenant_locales` (per
> [ADR-0008](../decisions/0008-localization-schema.md)), `tenant_settings`
> (with nullable `organization_id` for org-scoped settings),
> `tenant_feature_flags` (tenant-flag-level only — plan-level features
> arrive via the entitlement projection), `platform_entitlement_cache`,
> `platform_host_to_tenant`. Default-organization seeding at tenant
> creation. No business logic yet; later packets light up CRUD.
>
> Carries two Packet 3 follow-ups: (1) introduces the `TenantId` /
> `OrganizationId` Vogen value objects in `LearnStack.SharedKernel`
> alongside the schema (the kernel-level IDs that Packet 7 then threads
> through `ITenantContext` / `CapturedContext`); (2) the Packet 3
> `TransactionBehavior` shell lights up here once the per-module
> `DbContext` exists — UoW begin / commit-on-success-`Result` /
> rollback-on-failure, preserving the `ExceptionDispatchInfo` rethrow
> `AuditLogBehavior` owns one frame out.
>
> **Packet 7 — Tenant + Organization resolution + isolation
> (defense-in-depth) ⏳**
> `IHostToTenantResolver` (Postgres-backed default reading
> `platform_host_to_tenant`), `TenantResolverMiddleware`, request-scoped
> `ITenantContext` (`TenantId`, `OrganizationId?`, `UserId?`), singleton
> `ITenantContextAccessor` (`AsyncLocal<ITenantContext?>`-backed) populated
> at scope start by `TenantResolverMiddleware` (HTTP),
> `HubCorrelationMiddleware` (`/api/internal/*` — stub for 02c), Hangfire
> `JobActivator` (background jobs), and the outbox / inbox handler scope
> (integration events). `[TenantOwned]` and `[OrganizationScoped]` marker
> attributes. EF global query filters on every entity implementing
> `ITenantOwned` / `IOrganizationScoped`. PostgreSQL RLS policies on every
> tenant-owned table, with `app.tenant_id` and `app.organization_id`
> session variables set per connection lease via a
> `DbConnectionInterceptor` (transaction-local `set_config(..., true)`).
> Explicit, scoped, audited `EnterPlatformAdminScope(reason)` for the
> narrow cross-tenant access path. Cross-tenant + cross-org isolation
> integration tests for at least two seed tenants × two organizations each
> (`Tenant_A_cannot_read_Tenant_B_data`,
> `Org_X_cannot_read_Org_Y_within_TenantA`,
> `Unsetting_tenant_context_returns_zero_rows_through_RLS`). Picks up the
> application-level seed drop-in deferred from
> [Phase 01 Packet 8](phase-01-repository-tooling.md) — two demo tenants
> + platform admin user, wired through the new Tenancy module
> `DbContext` instead of the placeholder `scripts/seed.sh`.
>
> Carries two Packet 3 follow-ups: (1) converts `ITenantContext` **and**
> `CapturedContext` (`LearnStack.SharedKernel.Observability`) from raw
> `Guid` / `Guid?` to the strongly-typed `TenantId` / `OrganizationId`
> value objects (created in Packet 6) in a single pass — Packet 3 used raw
> `Guid` only because those VOs did not exist yet, and the two contracts
> convert together to avoid a half-typed intermediate; (2) replaces the
> `TenantContextBehavior.AllowsUnresolvedContext` stub with a real
> `[AllowsUnresolvedTenantContext]` marker-attribute discriminator (for
> tenant-provisioning / `EnterPlatformAdminScope` commands that legitimately
> run before a tenant is resolved), backed by an architecture test that the
> attribute lives only on that narrow command set.
>
> **Packet 8 — Tenant Customization foundation ⏳**
> `LearnStack.Modules.Customization` with `TenantContentType`,
> `TenantPageBlock`, `TenantLessonItemType`, `TenantLevelTaxonomy`,
> `TenantScoringRule`, `TenantCompletionRule`, `TenantCustomFieldDef`,
> `TenantTemplateLibrary` aggregates and their schema tables (per
> [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)).
> Runtime read paths — JSON Schema validators and sandboxed DSL stub —
> ship now; the Admin Studio editors land in Phase 06. A small built-in
> seed (a `default-card` page-block composite, a stock `Plain` level
> taxonomy) lets early phases exercise the customization runtime without
> a real tenant data set. The full DSL engine is gated on ADR-0025
> (Phase 05); Packet 8 ships only the stub.
>
> **Packet 9 — Audit infrastructure + Entitlement socket ⏳**
> `LearnStack.Infrastructure.Audit` with `AuditChangeTrackerInterceptor`
> (EF SaveChanges interceptor), `IAuditStateCapture` (before / after /
> changes JSON capture), `AuditLogBehavior` (MediatR behavior — already
> scaffolded as a shell in Packet 3; now lights up).
> `LearnStack.Modules.Audit` with `AuditEntry` aggregate (inheriting
> `Entity<TId>`, **not** `AuditableEntity<T>` — guarded by the
> `AuditEntry_Inherits_Entity_Not_AuditableEntity` architecture test) and
> `AuditConfig`. `audit_log` table partitioned by month from Day 1 per
> ADR-0028's chosen implementation; retention job (Hangfire) ships now
> with the policy from
> [18-audit-coverage.md](../standards/18-audit-coverage.md). MUST-class
> coverage is enabled for every command and security event the seed
> modules declare; modules added in later phases extend the catalog, not
> the infrastructure. `IEntitlementProvider` socket declared with
> `NullEntitlementProvider` as the default (returns "all features
> enabled, no limits"); Hub-backed and signed-license-key implementations
> land in Phase 02c.
>
> **Packet 10 — Architecture tests catalogue green + phase exit ⏳**
> Every Phase 02a architecture test in
> [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md)
> goes green in CI. The catalogue is the canonical name registry; the
> identifiers below either already exist there (the ADR-0032 batch) or
> land in their owning packet and are registered then. Tests grouped by
> introducing packet:
>
> - From the cross-cutting ADR-0032 batch already in the catalogue
>   (introduced by Packet 3):
>   - `IExceptionHandler_Registered_AtStartup`
>   - `MediatR_Pipeline_Order_Matches_Canonical_Sequence`
>   - `ValidationBehavior_DoesNotThrow_ValidationException`
>   - `Domain_Methods_Do_Not_Throw_For_Expected_Cases` (Roslyn analyzer
>     report) — **deferred to this packet from Packet 3**: the report-walking
>     test needs module domain code to walk, which does not exist until
>     Packet 6+. The underlying rule is already enforced from Packet 3 by the
>     `LS0001` analyzer running in every module's `Domain` + `Application`
>     build (+ `DomainExceptionThrowAnalyzerTests`); this packet adds the
>     report-walking architecture test once there is code to assert against.
>   - `Adapters_Wrap_Provider_Exceptions`
>   - `Modules_Do_Not_Reference_Sentry_SDK_Directly`
>   - `Logging_Goes_Through_Microsoft_Extensions_Logging`
>   - `OTel_Pipeline_Includes_TenantContextSpanProcessor`
>   - `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing`
> - From the module-dependency arm (introduced by Packet 2 / closed by
>   this packet):
>   - The Application + Infrastructure full matrix extending the
>     existing Phase-01 Packet 1 `ModuleDependencyTests` TODO at
>     [backend/tests/LearnStack.Tests.Architecture/ModuleDependencyTests.cs:17-21](../../backend/tests/LearnStack.Tests.Architecture/ModuleDependencyTests.cs#L17-L21)
>   - `LearnStack_Modules_DoNotReference_Hub`
>   - `Modules_Do_Not_Inject_Valkey_Directly`
>   - `Modules_Do_Not_Read_Entitlement_Cache_Directly`
>   - `Modules_Do_Not_Write_AuditLog_Directly`
>   - `Modules_Do_Not_Reference_DeploymentMode`
>   - `Core_Modules_HaveNo_DomainSpecific_Names`
>   - `No_Source_Folder_Named_Verticals` (already green from Phase 01)
> - From the tenancy + isolation arm (introduced by Packet 7 — canonical
>   identifiers land in the catalogue when the tests do):
>   - `Every_OrgScoped_Entity_HasOrgIdAndFilter`
>   - An `Every_TenantOwned_Entity_HasFilterAndRlsPolicy`-shaped pair
>     for the tenant dimension (final name TBD in Packet 7's catalogue
>     entry)
>   - "No `IgnoreQueryFilters()` outside the platform-admin scope" rule
>     (final identifier TBD in Packet 7's catalogue entry)
> - From the audit arm (introduced by Packet 9):
>   - `AuditEntry_Inherits_Entity_Not_AuditableEntity`
>   - `Every_TenantOwned_Command_HasAuditCoverage`
>   - "Audit-coverage matrix file exists per module" rule (final
>     identifier TBD in Packet 9's catalogue entry)
> - From the Dapr arm (introduced by Packet 5):
>   - `Dapr_PubSub_TopicNames_FollowConvention`
>
> The `if: false` CI placeholder for integration tests
> ([phase-01-repository-tooling.md § Packet 8](phase-01-repository-tooling.md))
> is removed once the first integration test from Packet 7 is green.
> Closes the architecture-test arm of the
> [Phase Exit Decision](#phase-exit-decision) checklist; the remaining
> exit gates (tenant + organization resolution, isolation tests, audit
> pipeline, customization runtime read paths, API conventions, the three
> blocking ADRs) close as their owning packets ship.

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
  `TenantTemplateLibrary` aggregates and their schema tables.
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

Every identifier in the cross-cutting list above is described — assertion,
type, source ADR / standard — in
[21-architecture-tests-catalogue.md § Cross-cutting: error handling, logging, observability](../standards/21-architecture-tests-catalogue.md).
The catalogue is the canonical reference; rename or relocation lands there
first.

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

Three ADRs targeted Phase 02a as exit blockers; all three are now Accepted
(Packet 1):

| # | Topic | Status | Decision |
|---|---|---|---|
| [ADR-0023](../decisions/0023-strongly-typed-id-source-generator.md) | Strongly-typed ID source generator | **Accepted** (2026-05-20) | Vogen as the emitter for both IDs and value objects |
| [ADR-0024](../decisions/0024-api-versioning-policy.md) | API versioning policy | **Accepted** (2026-05-20) | URL `/v{N}/`, 6-month deprecation window, RFC 8594 `Sunset` + `Deprecation` headers, OpenAPI `x-sunset` extensions |
| [ADR-0028](../decisions/0028-audit-log-partition-management.md) | `audit_log` monthly partition management | **Accepted** (2026-05-20) | Daily Hangfire recurring job (`learnstack:audit:partition-management`); no `pg_partman` runtime dependency |

The remaining exit gates (tenant + organization resolution, isolation tests,
audit pipeline, customization runtime read paths, API conventions, architecture-
test catalogue green) close as Packets 2–10 ship.
