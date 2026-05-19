# ADR 0032: Exception Handling, Logging, and Observability Architecture

## Status

Accepted

**Date:** 2026-05-20
**Deciders:** @platform

## Decision Drivers

- **Standards 09 ↔ Standards 02 ↔ ADR-0016 are out of step.** Standards 02 §
  Pipeline Behaviors lists Logging → Validation → … without `AuditLogBehavior`;
  ADR-0016 § Pipeline behavior order has Validation → Logging → AuditLog → … as
  a binding order with `try/catch + ExceptionDispatchInfo` rethrow. Standards 09
  refers to a "global exception middleware" without saying which .NET pattern.
  Pre-implementation is the window to close the gap; after Phase 02a lands code,
  the cost of changing it goes up sharply.
- **Foundation Day-1 commitment.** Per CLAUDE.md, observability + audit
  infrastructure ship in Phase 02a, not in Phase 11. Without a binding contract
  here, every module added in Phase 03-10 freelances its own error / log /
  trace pattern, and the audit-coverage matrix becomes aspirational.
- **Two-track failure model is already a project rule** ([Standards 09](../standards/09-error-handling.md)):
  exceptions for unexpected (bug / infra), `Result<T>` for expected outcomes.
  The remaining decisions are about *how the rails are laid down* so that the
  rule is mechanically enforced.
- **Triple deployment.** SaaS / Dedicated / Self-Hosted online / Self-Hosted
  air-gapped (per [ADR-0020](0020-triple-deployment-hybrid-license.md)) all run
  the same binary. The same instrumentation must produce useful signals where
  network egress to Sentry / SaaS observability backends is available **and**
  silently degrade where it is not.
- **Provider-adapter parity.** Every external integration (LiveKit, Stripe,
  Iyzico, Meilisearch, SeaweedFS, Keycloak, Hub) is reached through a
  `LearnStack.Infrastructure.<X>` adapter
  ([20-infrastructure-stack.md](../standards/20-infrastructure-stack.md)). The
  resilience + exception-wrap pattern must be the same across all of them so
  reviewers don't need to re-learn it per adapter.
- **Hub HTTPS contract is closed at four endpoints.** Inbound `/api/internal/*`
  calls do not carry a tenant JWT; their correlation must come from
  `traceparent` + the request envelope, not from `ITenantContext`.
- **Pre-implementation status.** Only `Result<T>` and `Error` records exist as
  code (`backend/src/LearnStack.SharedKernel/Results/`). Everything below is a
  contract, not a code change.

## Considered Options

### 1. **Option A — Single binding ADR codifying the entire cross-cutting contract (chosen)**

One ADR pins: pipeline order, `IExceptionHandler` as L1, validation returning
`Result.Fail` (no throw), `DomainException` reserved for bugs, Polly v8
`ResiliencePipeline` as the provider-resilience primitive, an
`IErrorTrackingProvider` abstraction over Sentry, the Serilog + OpenTelemetry
bridge, and `traceparent` as the correlation primitive across HTTP / outbox /
Hangfire / Hub.

**Pros:**

- Closes Standards 02 ↔ ADR-0016 gap in a single place; standards then *cite*
  this ADR instead of redefining the contract.
- Mechanical enforcement via architecture tests becomes possible because every
  rule has one canonical reference.
- Future architectural tweaks (e.g. switching error tracker, adding a 5th
  pipeline behavior) land as Amendments here, not scattered edits.

**Cons:**

- Larger than a typical ADR (10+ binding sub-decisions). Mitigated by treating
  the Implementation Notes section as the source of truth for each sub-decision
  and keeping the Decision section short.
- Combines decisions of different blast radius (pipeline order is project-wide;
  Sentry-vs-OTel split is observability-only). Mitigated by per-section
  structure so an Amendment can address one slice without rewriting the rest.

### 2. **Option B — Multiple smaller ADRs (one per gap) (rejected)**

Split into: ADR-X "Pipeline order canonicalisation", ADR-Y "Error tracking
provider abstraction", ADR-Z "Provider resilience pattern", etc.

**Pros:**

- Each ADR is small and focused.
- Easier to amend a single concern.

**Cons:**

- These decisions are coupled: the pipeline order assumes a specific exception
  flow; the Sentry-vs-OTel boundary assumes the pipeline order; the provider
  resilience pattern feeds the Sentry-vs-OTel split. Splitting them produces 3-5
  ADRs that must always be read together.
- Reviewers must traverse the chain to confirm consistency; gaps reopen as the
  set grows.
- Standards 02 / 09 / 10 each cite five ADRs instead of one — citation noise
  with no decoupling benefit.

### 3. **Option C — Amend ADR-0016 in place (rejected)**

Extend ADR-0016 ("Audit Log Subsystem") with the rest of the pipeline-behavior
decisions: exception handling, logging, observability glue.

**Pros:**

- One Accepted ADR carries all pipeline rules.

**Cons:**

- Violates the project rule "an Accepted ADR's Decision section is immutable;
  write a new ADR that supersedes it" (CLAUDE.md). ADR-0016 is Accepted and its
  Decision section already covers audit; pipeline-order canonicalisation is
  related but distinct scope.
- The Amendment block on ADR-0016 would balloon to cover concerns far from
  "audit subsystem", muddying the ADR's topic.

### 4. **Option D — Defer to Phase 02a "to be decided in code review" (rejected)**

Make none of these decisions binding; leave Standards 09 / 10 as-is and resolve
ambiguity as it comes up during Phase 02a implementation.

**Pros:**

- Zero up-front cost.

**Cons:**

- Pre-implementation is precisely the cheapest moment to make these calls.
- Phase 02a deliverables include `AuditLogBehavior` plus architecture tests
  asserting the pipeline order; without a binding contract those tests can't be
  written.
- Phase 03-10 modules pick their own conventions; pipeline order rot starts
  before the architecture tests catch it.

## Decision

LearnStack adopts **Option A**: a single binding ADR fixing the cross-cutting
contract for error handling, logging, and observability across the backend
runtime and the per-module Application layer. The contract has thirteen
binding sub-decisions; standards documents (02, 09, 10) are updated to cite
this ADR instead of redefining the rules.

### Sub-decisions (each binding)

1. **L1 exception handler is `IExceptionHandler` (.NET 8+).** Every host
   (`LearnStack.Api`, worker, background-service) registers
   `LearnStackExceptionHandler : IExceptionHandler` via
   `services.AddExceptionHandler<LearnStackExceptionHandler>()` +
   `app.UseExceptionHandler()`. The handler maps unhandled exceptions to RFC
   7807 Problem Details, attaches `correlation_id`, records the OTel span
   error, and dispatches to `IErrorTrackingProvider` (sub-decision 9). The
   older `app.UseExceptionHandler(lambda)` and `app.Use(ctx, next)` patterns
   are not used in new code.

2. **MediatR pipeline order is the canonical eight-step list below.** Standards
   02 § Pipeline Behaviors and ADR-0016 § Pipeline behavior order are aligned
   to it:

   ```
   Request
     → ValidationBehavior        (FluentValidation; returns Result.Fail on invalid)
       → LoggingBehavior         (ILogger.BeginScope + Activity + correlation tags)
         → AuditLogBehavior      (handler wrap; try/catch + audit + ExceptionDispatchInfo)
           → TenantContextBehavior     (assert resolved; set RLS GUC)
             → AuthorizationBehavior   (permission check; Result.Fail(forbidden))
               → TransactionBehavior   (UnitOfWork begin / commit / rollback)
                 → OutboxFlushBehavior (publish enrolled events on commit)
                   → Handler
   ```

   The order is bottom-most = innermost. Validation runs first because invalid
   input must never reach DB / audit / business code. Audit wraps everything
   from `TenantContextBehavior` inward so it sees both `Result.Fail` outcomes
   and exception failures with the same try/catch pattern (per ADR-0016).
   `ExceptionHandlingBehavior` is **not** introduced — `AuditLogBehavior`
   already catches handler exceptions, audits the failure entry, and rethrows
   via `ExceptionDispatchInfo`; the L1 `IExceptionHandler` is the final catch
   site. Adding a separate behavior would duplicate that responsibility.

3. **`ValidationBehavior` returns `Result.Fail(validation_failed)`; it does
   not throw.** FluentValidation results are aggregated and lifted into the
   `Error.Details` dictionary. The pipeline never raises a
   `FluentValidation.ValidationException`. This keeps "exception ≠ control
   flow" as a single rule and removes the need for catch logic in any
   downstream behavior. The behavior is implemented with a generic constraint
   `where TResponse : IResultBase` so it can construct the correct `Result<T>`
   shape via the static `Result.FailFor<T>(error)` factory.

4. **`DomainException` is reserved for programmer errors (bugs).** Expected
   business-rule violations return `Result.Fail(business_rule_violation, …)`
   from the domain method. The Roslyn analyzer
   `LearnStackException-DomainExceptionThrow` flags every `throw new
   DomainException` in `Domain` / `Application` projects as a Warning by
   default and as an Error after the codebase reaches the green-bar threshold
   (Phase 03 exit). Architecture test
   `Domain_Methods_Do_Not_Throw_For_Expected_Cases` complements the analyzer
   by walking the `Result<T>`-returning methods and asserting that the
   corresponding analyzer report is empty for the module.

5. **Provider-adapter resilience uses Polly v8 `ResiliencePipeline` via
   `IProviderResilience<TPort>`.** The composition root wires every
   tenant-facing third-party adapter (`LiveKitClient`,
   `StripePaymentClient`, `IyzicoPaymentClient`, `MeilisearchClient`,
   `SeaweedFSStorageClient`, …) with a pipeline carrying retry (exp backoff
   + jitter), circuit breaker, timeout, and bulkhead policies declared in
   `appsettings.{env}.json` under the `Resilience:<port-name>:` section.
   Adapters' only exception-related job is translating provider SDK
   exceptions into the appropriate `ProviderException` subclass
   (`LiveClassProviderException`, `PaymentProviderException`,
   `StorageProviderException`, …). The
   `[add-provider-adapter](../../.claude/skills/add-provider-adapter/SKILL.md)`
   skill walks the canonical wiring. Architecture test
   `Adapters_Wrap_Provider_Exceptions` asserts the SDK exception types never
   leave `LearnStack.Infrastructure.<Adapter>` namespaces. **Hub HTTP
   clients (`IEntitlementProvider`, `IUsageReporter`, `IHubTenantSync`) are
   excluded from this rule** — they have an additional mTLS + signed JWT +
   HMAC wrapper per [ADR-0019](0019-learnstack-hub.md) and their resilience
   policy lives inside that wrapper, defined by Phase 02c when the Hub
   adapter itself lands. Re-introducing them into the standard
   `IProviderResilience<TPort>` table would split their resilience
   configuration across two files; the Hub-specific wrapper owns the policy
   end-to-end.

6. **Controller-to-Result mapping uses an explicit extension method.** The
   sanctioned shape:

   ```csharp
   [HttpPost("courses")]
   public async Task<IActionResult> Create(CreateCourseCommand command, CancellationToken ct)
       => (await _mediator.Send(command, ct)).ToActionResult();
   ```

   `ResultExtensions.ToActionResult()` lives in `LearnStack.Api.Common`; it
   matches on `Error.Code` and produces the Problem Details body. No action
   filter, no MediatR `ResultUnwrapBehavior`, no implicit conversion. The
   explicit pattern keeps the diff under review honest and the debug
   experience straightforward.

7. **Sentry-versus-OpenTelemetry error capture is partitioned, not
   duplicated.** Every failure tags its OTel span; **Sentry capture is
   reserved for "something is wrong with our code or our infrastructure"**.
   The pattern in short:

   - **Capture to `IErrorTrackingProvider`**: unhandled `Exception`,
     `LearnStackException` subclasses at L1, `ProviderException` with
     `IsClientError == false` (5xx upstream).
   - **OTel span only (no Sentry)**: `ProviderException` with
     `IsClientError == true` (4xx upstream), every `Result.Fail(...)`
     outcome, `OperationCanceledException`.

   The full eight-row partition table — including the per-row `Activity`
   status mapping and rationale — is authoritative in
   [09-error-handling.md § Sentry vs OpenTelemetry — Error Capture Boundary](../standards/09-error-handling.md);
   keeping a second copy here would drift. The L1 handler's
   `ShouldCapture(Exception ex)` switch implements the rule.

8. **Serilog is the primary logger; logs reach OTel via the OTLP sink.** The
   composition root wires:

   ```csharp
   builder.Host.UseSerilog((ctx, services, cfg) => cfg
       .ReadFrom.Configuration(ctx.Configuration)
       .Enrich.WithCorrelationContext(services)            // tenant / org / user / module / correlation_id
       .Enrich.With<RedactSensitiveFieldsEnricher>()       // strips tokens, passwords, PII
       .WriteTo.Console(new RenderedCompactJsonFormatter())
       .WriteTo.OpenTelemetry(o =>                         // Serilog.Sinks.OpenTelemetry → OTLP
       {
           o.Endpoint = otelEndpoint;
           o.Protocol = OtlpProtocol.Grpc;
       }));
   ```

   `Microsoft.Extensions.Logging` is the seam every module logs through;
   Serilog is the implementation. The OTel logger provider is **not** also
   registered (`AddOpenTelemetry().WithLogging()` is skipped); double-export
   would duplicate every log line. Phase 02a's architecture test
   `Logging_Goes_Through_Microsoft_Extensions_Logging` asserts no module
   references `Serilog.ILogger` directly — modules only see `ILogger<T>`.

9. **`IErrorTrackingProvider` socket abstracts Sentry.** Composition root
   branches on `DeploymentMode`:

   | `DeploymentMode` | Implementation | Notes |
   |---|---|---|
   | `Development` | `NoOpErrorTracker` | No external egress |
   | `SaaS` | `SentryErrorTracker` | DSN from `ISecretProvider` |
   | `Dedicated` | `SentryErrorTracker` | Per-tenant DSN allowed via Hub config |
   | `SelfHostedOnline` | `SentryErrorTracker` (optional; `NoOpErrorTracker` if DSN absent) | Customer chooses |
   | `SelfHostedAirGapped` | `LocalFileErrorTracker` (writes JSON to `/var/learnstack/errors/`) | No outbound network |

   `IErrorTrackingProvider.CaptureAsync(LearnStackException, CapturedContext)`
   is the only sanctioned entry point. The architecture test
   `Modules_Do_Not_Reference_Sentry_SDK_Directly` enforces it.

10. **Telemetry signals carry `tenant.id`, `organization.id`, `user.id`,
    `module`, `correlation_id` automatically.** A `TenantContextSpanProcessor :
    BaseProcessor<Activity>` is registered once at the composition root:

    ```csharp
    services
        .AddOpenTelemetry()
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddProcessor<TenantContextSpanProcessor>()    // enrich every span
            .AddOtlpExporter());
    ```

    Because OTel processors are singletons by SDK design, the processor reads
    tenant context from **`ITenantContextAccessor`** — a singleton,
    `AsyncLocal<T>`-backed accessor (analogous to `IHttpContextAccessor`) —
    **not** from the request-scoped `ITenantContext` directly. The scoped
    `ITenantContext` (handler-facing) and the singleton
    `ITenantContextAccessor` (cross-cutting-infrastructure-facing) are two
    contracts populated together: the `TenantResolverMiddleware` (HTTP), the
    Hangfire `JobActivator` (background jobs), and the
    integration-event-handler scope (outbox consumers) each set the accessor's
    `AsyncLocal` value at scope start so any singleton (OTel processor,
    Serilog enricher) can read the current tenant without a scope-validation
    failure. Phase 02a ships both contracts together. Auto-instrumentation
    libraries (EF Core, HttpClient, Valkey via Dapr, …) need no per-call
    enrichment.

11. **Hub HTTPS contract surface propagates correlation via `traceparent` +
    request envelope.** The four `/api/internal/*` endpoints
    ([Standards 20 § Hub HTTPS Contract Surface](../standards/20-infrastructure-stack.md))
    accept inbound `traceparent` headers; `HubCorrelationMiddleware`
    instantiates the corresponding `Activity` so the inbound call continues
    the upstream trace. Tenant context is **not** inferred from a JWT (there
    is none) — it is read from the JSON envelope's `tenantId` field and
    asserted against the HMAC-signed body. Outbound calls
    LearnStack → Hub (`POST /api/v1/internal/license/verify`, `POST
    /api/v1/usage/report`) inject the current `traceparent` so the Hub-side
    trace continues seamlessly.

12. **Outbox + Hangfire correlation propagation is contractual.** Every
    `outbox_messages` row carries `tenant_id`, `organization_id?`,
    `correlation_id`, `event_id`, `occurred_at`, `type` (already specified in
    Phase 02b deliverables). Every integration-event handler restores
    `ITenantContext` from the envelope and starts a new
    `Activity` with `traceparent` set to the row's
    `correlation_id`. Every Hangfire job payload includes `tenant_id` and
    `correlation_id`; the job activator restores the ambient context before
    handler invocation. The architecture tests
    `Hangfire_Job_Payloads_Include_TenantId` and
    `Outbox_Row_Carries_Correlation_Context` are added in Phase 02b.

13. **Frontend Sentry attaches `correlation_id` from the last server
    response.** The Next.js app surfaces a recovery page when an unhandled
    error reaches a root or segment error boundary; the page displays the
    `correlation_id` returned in the most recent Problem Details body so a
    support handoff is one-step. Frontend Sentry runs in `Production` and
    staging modes; in `Development` it is off.

## Context

The codebase is pre-implementation (Phase 01 packets 1-6 shipped; only
`Result<T>` and `Error` exist as live code). Standards 09 and Standards 10
already lay down the *what* (two-track model, three signals, RFC 7807,
13 error codes, 15+ required metrics). Three implementation specifics were
left open or contradictory between documents:

- **Pipeline order** disagreed between Standards 02 § Pipeline Behaviors and
  ADR-0016 § Pipeline behavior order. ADR-0016 is Accepted; Standards 02 was
  the one that drifted.
- **Where exceptions are caught** was unclear: Standards 09 referenced a
  "global exception middleware" without naming a .NET pattern. ADR-0016
  expected `AuditLogBehavior` to be the catch + audit + rethrow point;
  Standards 02 omitted that behavior entirely.
- **Sentry-versus-OTel split** for error capture, **Serilog-or-OTel** for
  log emission, **how `tenant.id` reaches every span**, **how
  `IErrorTrackingProvider` reacts to `DeploymentMode`**, and **how
  correlation propagates through Hub HTTPS / outbox / Hangfire** were not
  anywhere.

Pre-implementation is the cheap moment to close those gaps. Once Phase 02a
ships an opinionated pipeline, retrofitting changes pulls the audit
infrastructure, the architecture tests, and every module's handler signature
in tow.

ADR-0016 stays authoritative for the audit subsystem; this ADR cites it and
adopts its pipeline order verbatim. ADR-0014 stays authoritative for Dapr;
this ADR plugs Dapr's emitted traces / metrics into the OTel pipeline without
introducing new building blocks. ADR-0020 stays authoritative for
`DeploymentMode`; this ADR adds `IErrorTrackingProvider` to the composition
root's adapter table in [Standards 20 § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md).

## Consequences

### Positive

- **Standards 02 ↔ ADR-0016 ↔ Standards 09 ↔ Standards 10 collapse to one
  source of truth.** Each standard now cites this ADR for the cross-cutting
  rules instead of restating them.
- **Architecture tests become writable.** `MediatR_Pipeline_Order_Matches_Canonical_Sequence`,
  `Domain_Methods_Do_Not_Throw_For_Expected_Cases`,
  `Adapters_Wrap_Provider_Exceptions`,
  `Modules_Do_Not_Reference_Sentry_SDK_Directly`,
  `Logging_Goes_Through_Microsoft_Extensions_Logging` all enforce specific
  binding rules; without this ADR they would be opinion.
- **Air-gapped Self-Hosted works without Sentry egress.**
  `LocalFileErrorTracker` keeps the contract intact while obeying ADR-0020's
  no-network constraint.
- **Provider adapters become uniform.** Each adapter does exception
  translation only; resilience policy is one declarative `appsettings`
  section per port.
- **Phase 02a `AuditLogBehavior` is the only failure-catch site below L1.**
  Reviewers don't have to ask "where else might the exception be caught?".
- **One mental model for new contributors.** "Exceptions = bugs / infra =
  Sentry. `Result.Fail` = refused = no Sentry. Provider failure = wrapped at
  the adapter boundary. Audit sees both via `AuditLogBehavior`."

### Negative

- **Polly v8 dependency** lands now as part of the foundation. Net new
  package but the canonical choice — `Microsoft.Extensions.Resilience`
  builds on top of it.
- **`IErrorTrackingProvider` is one more adapter** in the composition-root
  branching table. Mitigated by the same pattern used for `IEventBus` /
  `ICacheService` / `ISecretProvider` / `IEntitlementProvider`.
- **`TenantContextSpanProcessor` couples OTel pipeline to
  `ITenantContext`.** If `ITenantContext` is uninitialised (very early
  middleware path, malformed request), the processor must no-op rather than
  throw. Implementation detail enforced by Phase 02a unit test
  `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing`.
- **Roslyn analyzer for `DomainException` throws** is real work and lives in
  `backend/analyzers/`. Without it, the "DomainException = bug" discipline
  relies on reviewer vigilance.

### Neutral

- The choice of Sentry as the error backend is unchanged; this ADR only adds
  the abstraction. Switching to another error backend (Honeybadger, Rollbar,
  self-hosted GlitchTip) is now a composition-root swap.
- `traceparent` is a W3C standard already mandated by Standards 10 § Tracing;
  this ADR makes it the binding correlation primitive across HTTP, outbox,
  Hangfire, and Hub.

## Implementation Notes

### Phase 02a deliverables that flow from this ADR

| Item | Owner | Architecture test |
|---|---|---|
| `LearnStackExceptionHandler : IExceptionHandler` | `LearnStack.Api` | `IExceptionHandler_Registered_AtStartup` |
| MediatR pipeline order (8 behaviors) | `LearnStack.Application` | `MediatR_Pipeline_Order_Matches_Canonical_Sequence` |
| `ValidationBehavior` returns `Result.Fail` | `LearnStack.Application` | `ValidationBehavior_DoesNotThrow_ValidationException` |
| `AuditLogBehavior` catches + audits + rethrows via `ExceptionDispatchInfo` | `LearnStack.Infrastructure.Audit` | (covered by ADR-0016's `AuditLogBehavior_NeverBlocks_BusinessWrites`) |
| `TenantContextSpanProcessor` registered on OTel tracing pipeline | `LearnStack.Infrastructure.Observability` | `OTel_Pipeline_Includes_TenantContextSpanProcessor` |
| Serilog + OTLP sink wired; no `AddOpenTelemetry().WithLogging()` | `LearnStack.Api`, worker hosts | `Logging_Goes_Through_Microsoft_Extensions_Logging` |
| `IErrorTrackingProvider` interface + 3 implementations | `LearnStack.SharedKernel`, `LearnStack.Infrastructure.ErrorTracking` | `Modules_Do_Not_Reference_Sentry_SDK_Directly` |
| `Result<T>.ToActionResult()` extension | `LearnStack.Api.Common` | n/a (lint rule) |
| Roslyn analyzer flagging `throw new DomainException` outside `Domain` invariants | `backend/analyzers/` | `Domain_Methods_Do_Not_Throw_For_Expected_Cases` (uses the analyzer) |

### Phase 02b deliverables that flow from this ADR

| Item | Owner | Architecture test |
|---|---|---|
| Outbox row schema columns: `tenant_id`, `organization_id?`, `correlation_id`, `event_id`, `occurred_at`, `type` | `LearnStack.Infrastructure.Outbox` | `Outbox_Row_Carries_Correlation_Context` |
| Hangfire job payloads include `tenant_id` + `correlation_id` | `LearnStack.Infrastructure.BackgroundJobs` | `Hangfire_Job_Payloads_Include_TenantId` |
| Integration-event handler scope restores `ITenantContext` from envelope | `LearnStack.Infrastructure.Outbox` | `Integration_Event_Handler_Restores_Tenant_Context` |
| `HubCorrelationMiddleware` for `/api/internal/*` | `LearnStack.Api` | (covered by `Standards 20 § Hub HTTPS Contract Surface` audit) |

### `IProviderResilience<TPort>` shape

```csharp
public interface IProviderResilience<TPort> where TPort : class
{
    ResiliencePipeline Pipeline { get; }
    string PortName { get; }                            // "liveclass", "payment", "storage", ...
}

// Composition-root extension (lives in LearnStack.Infrastructure)
public static IServiceCollection AddProviderResilience<TPort, TImpl>(
    this IServiceCollection services,
    string portName)
    where TPort : class
    where TImpl : class, TPort
{
    services.AddSingleton<TPort, TImpl>();              // base adapter
    services.AddSingleton<IProviderResilience<TPort>>(sp =>
        new ProviderResilience<TPort>(
            portName,
            sp.GetRequiredService<IConfiguration>().GetSection($"Resilience:{portName}")));
    services.Decorate<TPort, ResilientProviderAdapter<TPort>>();
    return services;
}
```

The decorator reads `Resilience:<portName>:` from configuration and builds a
`ResiliencePipeline` with retry + circuit breaker + timeout + bulkhead. The
configuration shape is fixed in [Standards 09 § Provider Failures](../standards/09-error-handling.md).

### `LearnStackExceptionHandler` shape

```csharp
internal sealed class LearnStackExceptionHandler(
    IErrorTrackingProvider errorTracker,
    ILogger<LearnStackExceptionHandler> logger,
    IProblemDetailsFactory problemDetailsFactory) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception ex, CancellationToken ct)
    {
        var problem = problemDetailsFactory.For(ex, context);
        var captured = ShouldCapture(ex);
        if (captured)
            await errorTracker.CaptureAsync(ex, CapturedContext.From(context), ct);

        Activity.Current?.RecordException(ex);
        Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);

        context.Response.StatusCode = problem.Status ?? 500;
        await context.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }

    private static bool ShouldCapture(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        ProviderException pex when pex.IsClientError => false,    // 4xx upstream
        _ => true,                                                 // 5xx upstream, bug, infra
    };
}
```

### `TenantContextSpanProcessor` shape

```csharp
internal sealed class TenantContextSpanProcessor(ITenantContextAccessor accessor)
    : BaseProcessor<Activity>
{
    public override void OnStart(Activity activity)
    {
        var context = accessor.Current;
        if (context is null) return;        // outside any resolved scope; do not throw

        if (context.IsResolved)
        {
            activity.SetTag("tenant.id", context.TenantId);
            if (context.OrganizationId is { } orgId)
                activity.SetTag("organization.id", orgId);
            if (context.UserId is { } userId)
                activity.SetTag("user.id", userId);
        }

        if (context.CorrelationId is { } correlationId)
            activity.SetTag("correlation.id", correlationId);
        if (context.ModuleName is { } moduleName)
            activity.SetTag("module", moduleName);
    }
}
```

`BaseProcessor<Activity>` is a singleton; injecting the **request-scoped**
`ITenantContext` directly would fail at startup with "Cannot consume scoped
service `ITenantContext` from singleton". The singleton accessor
`ITenantContextAccessor` solves the lifetime mismatch:

```csharp
public interface ITenantContextAccessor
{
    ITenantContext? Current { get; set; }   // AsyncLocal<ITenantContext>-backed
}

internal sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<ITenantContext?> _current = new();
    public ITenantContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
```

Population pattern, set at scope start:

| Host | Where `accessor.Current` is set |
|------|---------------------------------|
| `LearnStack.Api` HTTP request | `TenantResolverMiddleware` reads JWT + host, builds `ITenantContext`, assigns to accessor |
| Hangfire job | `JobActivator` reads `tenant_id` + `correlation_id` from payload, builds `ITenantContext`, assigns to accessor |
| Integration-event handler | Outbox / inbox handler scope reads envelope, builds `ITenantContext`, assigns to accessor |
| `/api/internal/*` | `HubCorrelationMiddleware` reads HMAC-verified envelope, builds `ITenantContext`, assigns to accessor |

Phase 02a unit test
`TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing` asserts
`OnStart` is safe to call before any scope has populated the accessor (the
SDK creates and disposes warm-up `Activity` instances during startup).

### Configuration shape (`appsettings.json`)

```jsonc
{
  "Resilience": {
    "liveclass": {
      "retry": { "maxAttempts": 3, "delaySeconds": 1, "useJitter": true },
      "circuitBreaker": { "failureRatio": 0.5, "samplingDurationSeconds": 30, "minimumThroughput": 10, "breakDurationSeconds": 30 },
      "timeout": { "totalSeconds": 10 }
    },
    "payment": { "...": "..." },
    "storage": { "...": "..." },
    "search":  { "...": "..." }
    // Hub HTTP clients have their own resilience inside the mTLS + signed-JWT
    // + HMAC wrapper per ADR-0019; not configured here. See Sub-decision 5.
  },

  "ErrorTracking": {
    "Provider": "Sentry",                                // matched against DeploymentMode in composition root
    "Sentry": { "Dsn": "from-vault", "Environment": "saas-prod" },
    "LocalFile": { "Directory": "/var/learnstack/errors/" }
  },

  "Telemetry": {
    "OtlpEndpoint": "http://otel-collector:4317",
    "Service":      { "Name": "learnstack-api", "Version": "git-sha" }
  }
}
```

## References

- [ADR-0002 Initial Architecture](0002-initial-architecture.md) — the
  observability stack column.
- [ADR-0006 Events and Outbox](0006-events-and-outbox.md) — outbox row
  schema; this ADR adds `correlation_id` propagation to integration-event
  handlers.
- [ADR-0010 Cross-Module Communication](0010-cross-module-communication.md)
  — the four sanctioned mechanisms; this ADR is observability-side and adds
  none.
- [ADR-0014 Adopt Dapr](0014-adopt-dapr.md) — Dapr emits OTel traces +
  metrics; this ADR plugs them into the Collector pipeline.
- [ADR-0016 Audit Log Subsystem](0016-audit-log-subsystem.md) — pipeline
  order originates here; this ADR adopts it unchanged.
- [ADR-0017 Tenant + Organization Hierarchy](0017-tenant-organization-hierarchy.md)
  — `tenant.id` / `organization.id` span attributes match this hierarchy.
- [ADR-0019 LearnStack Hub](0019-learnstack-hub.md) — Hub HTTPS contract
  surface; this ADR specifies the correlation propagation across it.
- [ADR-0020 Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md)
  — `DeploymentMode` table; this ADR adds `IErrorTrackingProvider` row.
- [02-backend-coding.md § Pipeline Behaviors](../standards/02-backend-coding.md)
  — order list cites this ADR.
- [09-error-handling.md](../standards/09-error-handling.md) — implementation
  patterns for L1 / `Result<T>` / Validation / provider resilience cite this
  ADR.
- [10-observability.md](../standards/10-observability.md) — Sentry / OTel
  split, Serilog bridge, `tenant.id` span propagation cite this ADR.
- [20-infrastructure-stack.md § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md)
  — `IErrorTrackingProvider` row.
- [33-cross-cutting-concerns.md](../architecture/33-cross-cutting-concerns.md)
  — conceptual deep dive and diagrams.
- [Phase 02a Roadmap](../roadmap/phase-02a-kernel-tenancy.md) — deliverables.
- [Phase 02b Roadmap](../roadmap/phase-02b-events-auth.md) — outbox /
  Hangfire correlation deliverables.
- W3C Trace Context — <https://www.w3.org/TR/trace-context/>
- Polly v8 documentation — <https://www.pollydocs.org/>
- OpenTelemetry .NET — <https://opentelemetry.io/docs/instrumentation/net/>
