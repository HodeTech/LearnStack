---
name: wire-cross-cutting-foundation
description: >
  Wire the LearnStack cross-cutting foundation in a backend host
  (`LearnStack.Api`, worker, background-service host) — `IExceptionHandler`,
  8-step MediatR pipeline, `Result<T>.ToActionResult()`, Serilog + OTel,
  `TenantContextSpanProcessor`, `IErrorTrackingProvider`,
  `IProviderResilience<TPort>`, Roslyn analyzer for `DomainException`. USE
  FOR: standing up the foundation in Phase 02a (one-time wiring per host
  process), or restoring it after a composition-root refactor. DO NOT USE
  FOR: adding a new provider adapter ([add-provider-adapter](../add-provider-adapter/SKILL.md)),
  adding a single MediatR handler ([add-mediatr-handler](../add-mediatr-handler/SKILL.md)),
  or touching observability backends in Phase 11 (different scope —
  dashboard / alert / Sentry SaaS config).
---

# Wiring the cross-cutting foundation

## Purpose

Bring up the LearnStack error-handling + logging + observability foundation
in a backend host. The contract is bound by
[ADR-0032](../../../docs/decisions/0032-exception-handling-logging-and-observability.md).
This skill walks the canonical wiring step by step so the eight binding
sub-decisions land in the right composition-root order and the architecture
tests pass.

## When to use

- Phase 02a — first time the `LearnStack.Api` (or worker host) is stood up;
  every piece below has to land in one consistent pass.
- After a composition-root refactor that touched DI registration order; this
  skill is the checklist that verifies the pipeline still matches ADR-0032.
- Standing up a new host process (a future dedicated worker, a future
  background-service binary) that needs the same cross-cutting plumbing.

## When not to use

- Adding a single MediatR handler — use
  [add-mediatr-handler](../add-mediatr-handler/SKILL.md). The pipeline is
  already wired; new handlers participate automatically.
- Adding a new provider adapter — use
  [add-provider-adapter](../add-provider-adapter/SKILL.md). That skill
  handles the resilience decorator and `ProviderException` translation for
  a single adapter.
- Deploying / configuring an OTel Collector or a Sentry SaaS project — that
  is Phase 11 ops work, not application-code wiring.
- Adjusting a Resilience policy for a single port — edit
  `appsettings.Resilience:<port>:` and review the test; no need to re-walk
  the foundation.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Host project | Yes | `LearnStack.Api` for the main API, or the worker host name. |
| `DeploymentMode` | Yes | Determines `IErrorTrackingProvider` + OTLP exporter target. |
| Sentry DSN source | Conditional | If `DeploymentMode ∈ { SaaS, Dedicated, SelfHostedOnline }` and Sentry is enabled, the DSN comes from `ISecretProvider`. |
| OTel Collector endpoint | Yes (non-Dev) | OTLP gRPC endpoint; falls back to a local file exporter in `SelfHostedAirGapped`. |
| Module list | Yes | The set of modules the host loads — each module's `IModule.RegisterServices` must be called after the foundation registers. |

## Workflow

### Step 1: Read the binding contract

Open
[ADR-0032](../../../docs/decisions/0032-exception-handling-logging-and-observability.md)
and
[33-cross-cutting-concerns.md](../../../docs/architecture/33-cross-cutting-concerns.md).
You should be able to recite, before you write a line of code:

- The eight pipeline behaviors and their order
  (`Validation → Logging → Audit → TenantContext → Authorization → Transaction → OutboxFlush → Handler`).
- The Sentry-vs-OTel boundary (`ShouldCapture(ex)` table).
- The Serilog + OTLP wiring rule (no `AddOpenTelemetry().WithLogging()` alongside).
- The composition-root branching for `IErrorTrackingProvider`.

### Step 2: Add the foundation NuGet packages

Add these to `Directory.Packages.props`:

```xml
<PackageVersion Include="Serilog.AspNetCore" Version="..." />
<PackageVersion Include="Serilog.Sinks.OpenTelemetry" Version="..." />
<PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="..." />
<PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="..." />
<PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="..." />
<PackageVersion Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="..." />
<PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="..." />
<PackageVersion Include="Microsoft.Extensions.Resilience" Version="..." />
<PackageVersion Include="Sentry.AspNetCore" Version="..." />        <!-- referenced only from Infrastructure.ErrorTracking -->
```

Architecture test `Modules_Do_Not_Reference_Sentry_SDK_Directly` enforces
the `Sentry.AspNetCore` reference being restricted to
`LearnStack.Infrastructure.ErrorTracking`.

### Step 3: Wire Serilog (logger primary)

```csharp
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.WithCorrelationContext(services)            // tenant / org / user / module / correlation_id
    .Enrich.With<RedactSensitiveFieldsEnricher>()       // strips tokens, passwords, PII
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteTo.OpenTelemetry(o =>
    {
        o.Endpoint = ctx.Configuration["Telemetry:OtlpEndpoint"];
        o.Protocol = OtlpProtocol.Grpc;
    }));
```

**Do not** also register `AddOpenTelemetry().WithLogging()` —
[ADR-0032 § Sub-decision 8](../../../docs/decisions/0032-exception-handling-logging-and-observability.md)
forbids it.

### Step 4: Wire OpenTelemetry tracing + metrics

```csharp
services
    .AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "learnstack-api",
        serviceVersion: GitSha.Current))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("LearnStack.*")                    // every module's manual ActivitySource
        .AddProcessor<TenantContextSpanProcessor>()   // ADR-0032 § Sub-decision 10
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(builder.Configuration["Telemetry:OtlpEndpoint"]!);
            o.Protocol = OtlpExportProtocol.Grpc;
        }))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("learnstack.*")
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(builder.Configuration["Telemetry:OtlpEndpoint"]!);
            o.Protocol = OtlpExportProtocol.Grpc;
        }));
```

For `DeploymentMode.SelfHostedAirGapped`, swap `AddOtlpExporter` for the
file exporter pointing at `/var/learnstack/otel/`.

### Step 5: Register `IErrorTrackingProvider`

In `LearnStack.Infrastructure.ErrorTracking` add the three implementations
and the composition-root extension:

```csharp
public static IServiceCollection AddErrorTracking(
    this IServiceCollection services, DeploymentMode mode, IConfiguration config)
{
    services.AddSingleton<IErrorTrackingProvider>(sp => mode switch
    {
        DeploymentMode.Development           => new NoOpErrorTracker(),
        DeploymentMode.SaaS                  => CreateSentry(sp, config),
        DeploymentMode.Dedicated             => CreateSentry(sp, config),
        DeploymentMode.SelfHostedOnline      => CreateSentryOrNoOp(sp, config),
        DeploymentMode.SelfHostedAirGapped   => new LocalFileErrorTracker(
            config["ErrorTracking:LocalFile:Directory"]!),
        _ => throw new UnreachableException($"DeploymentMode {mode}")
    });
    return services;
}
```

Modules never reference `Sentry.SentrySdk`. The architecture test
`Modules_Do_Not_Reference_Sentry_SDK_Directly` enforces it.

### Step 6: Register the L1 `IExceptionHandler`

```csharp
services.AddProblemDetails();
services.AddExceptionHandler<LearnStackExceptionHandler>();
// in pipeline:
app.UseExceptionHandler();
```

`LearnStackExceptionHandler` (in `LearnStack.Api`) builds the Problem
Details body, calls `Activity.Current.RecordException + SetStatus(Error)`,
and dispatches to `IErrorTrackingProvider.CaptureAsync` only when
`ShouldCapture(ex)` returns true (see
[09-error-handling.md § Sentry vs OpenTelemetry — Error Capture Boundary](../../../docs/standards/09-error-handling.md)).

### Step 7: Register the MediatR pipeline (8 behaviors in order)

```csharp
services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<AssemblyMarker>();

    // Order matters — outermost first, innermost last.
    // Architecture test Pipeline_Order_Matches_ADR_0032 enforces this.
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(AuditLogBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TenantContextBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
    cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(OutboxFlushBehavior<,>));
});
```

Do **not** add an `ExceptionHandlingBehavior`. The `AuditLogBehavior` catches
handler exceptions, writes the failure audit, and rethrows via
`ExceptionDispatchInfo`; the L1 `IExceptionHandler` is the final catch site.

### Step 8: Register the `IProviderResilience<TPort>` extension

In `LearnStack.Infrastructure.Resilience`:

```csharp
public static IServiceCollection AddProviderResilience<TPort, TImpl>(
    this IServiceCollection services, string portName)
    where TPort : class
    where TImpl : class, TPort
{
    services.AddSingleton<TPort, TImpl>();
    services.AddSingleton<IProviderResilience<TPort>>(sp =>
        new ProviderResilience<TPort>(
            portName,
            sp.GetRequiredService<IConfiguration>()
              .GetSection($"Resilience:{portName}")));
    services.Decorate<TPort, ResilientProviderAdapter<TPort>>();
    return services;
}
```

The composition root calls this extension once per provider port (see
[add-provider-adapter](../add-provider-adapter/SKILL.md) for the per-adapter
work).

### Step 9: Wire `Result<T>.ToActionResult()` extension

In `LearnStack.Api.Common`:

```csharp
public static class ResultExtensions
{
    public static IActionResult ToActionResult<T>(this Result<T> result)
        => result.IsSuccess
            ? new OkObjectResult(result.Value)
            : new ObjectResult(ProblemDetailsFactory.For(result.Error!))
            {
                StatusCode = HttpStatusMap.For(result.Error!.Code)
            };
}
```

Controllers stay thin:

```csharp
[HttpPost]
public async Task<IActionResult> Create(CreateCourseCommand cmd, CancellationToken ct)
    => (await _mediator.Send(cmd, ct)).ToActionResult();
```

No action filter, no `ResultUnwrapBehavior`. Explicit beats magic.

### Step 10: Add the Roslyn analyzer

The `LearnStackException-DomainExceptionThrow` analyzer lives in
`backend/analyzers/` and ships as a NuGet package referenced by
`Domain` + `Application` projects via
`<PackageReference Include="LearnStack.Analyzers" ... />`. Severity:
Warning in Phase 02a, escalates to Error after Phase 03 exit.

### Step 11: Register module services last

Each module's `IModule` registration runs **after** the foundation is in
place, so behaviors and instrumentation are already wired before
module-specific code lights up:

```csharp
services
    .AddCrossCuttingFoundation(builder.Configuration, deploymentMode)
    .AddModuleAudit()
    .AddModuleTenancy()
    .AddModuleCustomization()
    // ... more modules
    .AddModuleApi();    // controllers wire last
```

## Validation

- `dotnet build` succeeds for `LearnStack.Api`.
- Architecture tests pass:
  - `IExceptionHandler_Registered_AtStartup`
  - `Pipeline_Order_Matches_ADR_0032`
  - `ValidationBehavior_DoesNotThrow_ValidationException`
  - `OTel_Pipeline_Includes_TenantContextSpanProcessor`
  - `Logging_Goes_Through_Microsoft_Extensions_Logging`
  - `Modules_Do_Not_Reference_Sentry_SDK_Directly`
  - `TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing`
- Manual smoke: hit a known-bad endpoint, confirm Problem Details body
  carries the correct `code`, `correlationId`, and HTTP status; confirm the
  OTel span shows `SetStatus(Error)`; confirm Sentry receives the event in
  `Development` only as `NoOp` (no actual dispatch).
- Integration smoke: trigger a `Result.Fail(validation_failed)`, confirm
  the OTel span is `SetStatus(Ok)` and Sentry receives nothing.

## Common pitfalls

- **Adding `ExceptionHandlingBehavior` "just in case".** Forbidden by
  ADR-0032; `AuditLogBehavior` + L1 cover every path.
- **Registering both Serilog OTLP sink and OpenTelemetry `LoggerProvider`.**
  Duplicates every log line. The pipeline expects Serilog only.
- **Skipping `TenantContextSpanProcessor`.** Auto-instrumentation spans
  show up with no `tenant.id` and Tempo searches become useless.
- **Reading `DeploymentMode` from inside a module.** Forbidden — the
  composition root selects implementations once. Architecture test
  `Modules_Do_Not_Reference_DeploymentMode` catches it.
- **Capturing every exception to Sentry, including `OperationCanceled`
  and 4xx provider responses.** Sentry noise. The `ShouldCapture` switch is
  binding.
- **Hand-rolling retry / circuit breaker inside an adapter.** Resilience
  policies live in the `IProviderResilience<TPort>` decorator, not in
  adapter code.

## References

- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../../../docs/decisions/0032-exception-handling-logging-and-observability.md)
- [33-cross-cutting-concerns.md](../../../docs/architecture/33-cross-cutting-concerns.md)
- [09-error-handling.md](../../../docs/standards/09-error-handling.md)
- [10-observability.md](../../../docs/standards/10-observability.md)
- [02-backend-coding.md § Pipeline Behaviors](../../../docs/standards/02-backend-coding.md)
- [20-infrastructure-stack.md § Composition Root and Deployment Mode](../../../docs/standards/20-infrastructure-stack.md)
- [Phase 02a Roadmap](../../../docs/roadmap/phase-02a-kernel-tenancy.md)
- [add-provider-adapter](../add-provider-adapter/SKILL.md)
- [add-mediatr-handler](../add-mediatr-handler/SKILL.md)
