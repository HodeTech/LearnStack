---
name: add-provider-adapter
description: >
  Add a provider adapter (LiveKit / Stripe / Iyzico / Meilisearch / SeaweedFS
  / Keycloak / external HTTP service) in `LearnStack.Infrastructure.<X>` with
  the canonical pattern: port interface in `SharedKernel`, adapter in
  `Infrastructure` doing only SDK-exception → `ProviderException` translation,
  and the `IProviderResilience<TPort>` decorator carrying retry + circuit
  breaker + timeout + bulkhead from `appsettings.Resilience:<portName>:`.
  USE FOR: every new external integration that LearnStack reaches over the
  network. DO NOT USE FOR: the Dapr building-block ports (`IEventBus`,
  `ICacheService`, `ISecretProvider`) — those are wired by
  [wire-cross-cutting-foundation](../wire-cross-cutting-foundation/SKILL.md)
  and their resilience is handled by Dapr's runtime, not Polly; the four
  Hub HTTPS endpoints (`IEntitlementProvider`, `IUsageReporter`,
  `IHubTenantSync`) which have their own contractual mTLS + HMAC wrapper
  per [ADR-0019](../../../docs/decisions/0019-learnstack-hub.md); or adapters
  for in-process pure libraries (e.g. a JSON serializer) — no resilience
  needed.
---

# Adding a provider adapter

## Purpose

Every external integration (LiveKit room creation, Stripe charge,
Meilisearch search, SeaweedFS object PUT, Keycloak admin call, …) goes
through the same shape:

```text
Application code
  ↓ (port interface in SharedKernel)
ResilientProviderAdapter<TPort>           ← Polly v8 ResiliencePipeline
  ↓
LiveKitClient (adapter in Infrastructure) ← SDK exception → ProviderException
  ↓
LiveKit .NET SDK
  ↓
upstream
```

The point: the application sees one port; resilience is centralised in the
decorator; exception translation is the adapter's only job. This skill walks
the canonical wiring per
[ADR-0032 § Sub-decision 5](../../../docs/decisions/0032-exception-handling-logging-and-observability.md).

## When to use

- New external integration (a new payment processor, a new email provider, a
  new SMS provider, a new search index, a new media SDK).
- Replacing an existing adapter with a different upstream while keeping the
  port interface unchanged (e.g. Stripe → Iyzico for a tenant).
- Splitting one adapter into two with different resilience policies (e.g.
  one critical-path Stripe call vs. one fire-and-forget Stripe webhook reply).

## When not to use

- Wiring `IEventBus` / `ICacheService` / `ISecretProvider` — those are Dapr
  building blocks, handled by
  [wire-cross-cutting-foundation](../wire-cross-cutting-foundation/SKILL.md).
  Dapr's runtime provides retry + DLQ + circuit-breaker semantics already.
- The four Hub HTTPS endpoints (`IEntitlementProvider`, `IUsageReporter`,
  `IHubTenantSync`) — those use the dedicated mTLS + signed JWT + HMAC
  wrapper per [ADR-0019](../../../docs/decisions/0019-learnstack-hub.md).
- Pure in-process integrations (a JSON converter, a hash function) — no
  network call, no resilience needed.
- Trivial single-method clients that wrap a static method — write a static
  helper instead.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Port name | Yes | `liveclass`, `payment`, `storage`, `search`, `notifications`, … . Used as the `appsettings.Resilience:<portName>:` section key. |
| Port interface | Yes | `ILiveClassProvider`, `IPaymentProvider`, etc. Lives in `LearnStack.SharedKernel.Abstractions.<Area>`. |
| Adapter class | Yes | Concrete implementation in `LearnStack.Infrastructure.<X>`. |
| SDK package | Yes | The upstream client SDK. |
| `ProviderException` subclass | Yes | `LiveClassProviderException`, `PaymentProviderException`, `StorageProviderException`, etc. Subclass of `LearnStackException`. |

## Workflow

### Step 1: Declare the port interface in `SharedKernel`

In `LearnStack.SharedKernel.Abstractions.<Area>/I<Port>Provider.cs`:

```csharp
public interface ILiveClassProvider
{
    Task<LiveRoom> CreateRoomAsync(
        CreateRoomCommand command, CancellationToken ct = default);

    Task<LiveRoomToken> IssueTokenAsync(
        LiveRoomTokenCommand command, CancellationToken ct = default);

    Task EndRoomAsync(LiveRoomId roomId, CancellationToken ct = default);
}
```

Rules:

- Strongly-typed parameters and return values; no `string roomId` in the
  contract.
- Async + `CancellationToken` everywhere.
- No SDK types in the contract — the adapter translates SDK shapes to
  LearnStack domain types.
- Return `Result<T>` only at the application-layer boundary; provider ports
  return raw values and throw `ProviderException` on failure. The application
  handler catches at the boundary and converts to `Result.Fail` when the
  failure is expected (e.g. "room name already taken" → 409).

### Step 2: Add the `ProviderException` subclass

In `LearnStack.SharedKernel.Exceptions/`:

```csharp
public sealed class LiveClassProviderException : ProviderException
{
    public LiveClassProviderException(
        string code,
        string message,
        Exception? innerException = null,
        bool isClientError = false)
        : base(code, message, innerException, isClientError)
    {
    }
}
```

Rules:

- `isClientError` is `true` for 4xx upstream (the provider rejected the
  request because of bad input) and `false` for 5xx (the provider's infra
  failed). The L1 `IExceptionHandler` uses this flag to decide whether to
  Sentry-capture. See
  [09-error-handling.md § Sentry vs OpenTelemetry — Error Capture Boundary](../../../docs/standards/09-error-handling.md).
- Codes follow the pattern `provider.<short-reason>` (e.g.
  `provider.room_full`, `provider.unauthenticated`, `provider.unavailable`).

### Step 3: Write the adapter — translation only

In `LearnStack.Infrastructure.LiveClassroom.LiveKit/LiveKitClient.cs`:

```csharp
internal sealed class LiveKitClient(
    LiveKitClientOptions options,
    ILogger<LiveKitClient> logger) : ILiveClassProvider
{
    private readonly LiveKit.RoomServiceClient _sdk = new(
        options.WsUrl, options.ApiKey, options.ApiSecret);

    public async Task<LiveRoom> CreateRoomAsync(
        CreateRoomCommand cmd, CancellationToken ct)
    {
        try
        {
            var room = await _sdk.CreateRoom(/* SDK call */, ct);
            return MapToDomain(room);
        }
        catch (LiveKit.RoomAlreadyExistsException ex)
        {
            throw new LiveClassProviderException(
                "provider.room_already_exists", ex.Message, ex, isClientError: true);
        }
        catch (LiveKit.QuotaExceededException ex)
        {
            throw new LiveClassProviderException(
                "provider.quota_exceeded", ex.Message, ex, isClientError: true);
        }
        // .NET 5+ exposes `HttpRequestException.StatusCode` as `HttpStatusCode?`.
        // `null` = transport failure (DNS, connection refused, timeout) which is
        // an infra fault → isClientError: false. 5xx upstream → isClientError: false.
        // 4xx upstream is handled by the SDK-specific catches above; if a raw 4xx
        // reaches this clause it falls through to the catch-all below.
        catch (HttpRequestException ex)
            when (ex.StatusCode is null || (int)ex.StatusCode >= 500)
        {
            throw new LiveClassProviderException(
                "provider.unavailable", "Live-class provider unavailable.",
                ex, isClientError: false);
        }
        catch (Exception ex)
        {
            throw new LiveClassProviderException(
                "provider.unknown", "Unexpected live-class provider failure.",
                ex, isClientError: false);
        }
    }

    // ... other methods follow the same pattern
}
```

Rules:

- **No** retry, **no** circuit breaker, **no** timeout in the adapter — that
  is the decorator's job.
- The adapter is `internal sealed` — the composition root sees only the port
  interface.
- Every public method is wrapped in a `try / catch` whose only purpose is
  exception translation.
- Provider SDK exception types (`LiveKit.RoomAlreadyExistsException`,
  `Stripe.StripeException`, `Meilisearch.MeilisearchApiError`, …) **never**
  leave the adapter's namespace. The architecture test
  `Adapters_Wrap_Provider_Exceptions` enforces it.

### Step 4: Wire resilience in the composition root

In `LearnStack.Infrastructure/<Area>/ServiceCollectionExtensions.cs`:

```csharp
public static IServiceCollection AddLiveClassroomProvider(
    this IServiceCollection services, IConfiguration config)
{
    services.Configure<LiveKitClientOptions>(config.GetSection("LiveKit"));

    services.AddProviderResilience<ILiveClassProvider, LiveKitClient>("liveclass");

    return services;
}
```

The `AddProviderResilience<TPort, TImpl>` extension (registered by
[wire-cross-cutting-foundation](../wire-cross-cutting-foundation/SKILL.md))
does three things:

1. Registers `TImpl` as the base implementation.
2. Builds an `IProviderResilience<TPort>` carrying the Polly v8
   `ResiliencePipeline` from `appsettings.Resilience:<portName>:`.
3. Decorates `TPort` with `ResilientProviderAdapter<TPort>` so every call
   goes through the pipeline.

### Step 5: Author the resilience configuration

Add to `appsettings.json`:

```jsonc
{
  "Resilience": {
    "liveclass": {
      "retry": {
        "maxAttempts": 3,
        "delaySeconds": 1,
        "useJitter": true
      },
      "circuitBreaker": {
        "failureRatio": 0.5,
        "samplingDurationSeconds": 30,
        "minimumThroughput": 10,
        "breakDurationSeconds": 30
      },
      "timeout": { "totalSeconds": 10 },
      "bulkhead": { "maxConcurrency": 50 }
    }
  }
}
```

Tune per port based on the upstream's known characteristics. Document the
chosen values in the adapter's README (under
`backend/src/LearnStack.Infrastructure.<X>/README.md`) so reviewers know
*why* `maxAttempts: 3` and not 5.

### Step 6: Map provider 4xx vs 5xx correctly

The decorator only retries on `IsClientError == false` provider exceptions
and on `InfrastructureException`. A `LiveClassProviderException` with
`isClientError: true` skips retry — retrying "room already exists" is wrong.
Verify the mapping table for every translated SDK exception:

| Upstream signal | `ProviderException.IsClientError` | Sentry capture | Decorator retries |
|---|---|---|---|
| 4xx response | `true` | No | No |
| 5xx response | `false` | Yes | Yes |
| Timeout | `false` | Yes | Yes |
| DNS / connection refused | `false` | Yes | Yes |
| Auth failure (401 / 403 from upstream) | `true` (provider thinks our creds are bad — that's our config bug) | Yes (config bug) | No |
| Rate-limit (429) | `true` | No | Yes, but with a longer backoff — special-case if needed |

### Step 7: Adapter tests

In `LearnStack.Tests.Integration/Providers/`:

```csharp
[Fact]
public async Task CreateRoomAsync_translates_RoomAlreadyExists_to_4xx_provider_exception()
{
    // Arrange — SDK throws RoomAlreadyExistsException
    // Act
    var act = async () => await sut.CreateRoomAsync(cmd, ct);
    // Assert
    var ex = await act.Should().ThrowAsync<LiveClassProviderException>();
    ex.Which.Code.Should().Be("provider.room_already_exists");
    ex.Which.IsClientError.Should().BeTrue();
}

[Fact]
public async Task ResilientProviderAdapter_retries_on_5xx_until_circuit_opens()
{
    // Arrange — wrap a flaky in-memory adapter
    // Act — fire enough requests to open the breaker
    // Assert — subsequent calls return BrokenCircuitException wrapped in ProviderException
}
```

### Step 8: Module spec entry

In `docs/modules/<module>/providers.md`, add the adapter:

```markdown
## LiveKit (liveclass)

- Port: `ILiveClassProvider`
- Adapter: `LearnStack.Infrastructure.LiveClassroom.LiveKit.LiveKitClient`
- Resilience section: `Resilience:liveclass:`
- Exception subclass: `LiveClassProviderException`
- ADR: [ADR-0005](../../../docs/decisions/0005-live-classroom-media-stack.md)
```

## Validation

- `dotnet build` succeeds.
- Architecture test `Adapters_Wrap_Provider_Exceptions` passes — no SDK
  exception types leak from the adapter's namespace.
- Integration test confirms SDK 4xx → `ProviderException(isClientError:
  true)`, SDK 5xx → `ProviderException(isClientError: false)`.
- A deliberately-flaky test adapter triggers the circuit breaker after the
  configured threshold; subsequent calls fail fast with
  `BrokenCircuitException`.
- An `appsettings.Resilience:<portName>:` block exists with all four policy
  sections (retry, circuit breaker, timeout, bulkhead).

## Common pitfalls

- **Adding retry / timeout in the adapter.** The decorator handles those.
  Adapter-level retry double-counts attempts and breaks the circuit-breaker
  accounting.
- **Letting the SDK exception escape.** A `LiveKit.LiveKitException`
  reaching the application layer means the architecture test fires and the
  rest of the system can't decide whether to Sentry-capture (no
  `IsClientError` flag).
- **Setting `isClientError: false` on 4xx.** Forces a retry on
  invalid-input failures (the upstream will reject again and again until
  the circuit opens) and floods Sentry with "client mistake" events.
- **Forgetting the `Resilience:<portName>:` configuration block.** The
  decorator falls back to no-policy mode and silently masks failures during
  development — they only surface under load.
- **Reusing a single adapter for two ports with different resilience
  needs.** Split them. Each port has its own decorator instance and its
  own configuration section.
- **Calling the adapter directly from a module** (bypassing the port
  interface). The decorator is registered on the port; calling the
  concrete adapter skips resilience entirely.

## References

- [ADR-0032 § Sub-decision 5](../../../docs/decisions/0032-exception-handling-logging-and-observability.md)
- [09-error-handling.md § Provider Failures](../../../docs/standards/09-error-handling.md)
- [09-error-handling.md § Sentry vs OpenTelemetry — Error Capture Boundary](../../../docs/standards/09-error-handling.md)
- [20-infrastructure-stack.md](../../../docs/standards/20-infrastructure-stack.md)
- [33-cross-cutting-concerns.md § 10. Provider Resilience Pattern](../../../docs/architecture/33-cross-cutting-concerns.md)
- [wire-cross-cutting-foundation](../wire-cross-cutting-foundation/SKILL.md)
- Polly v8 — <https://www.pollydocs.org/>
