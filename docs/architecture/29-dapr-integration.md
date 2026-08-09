# Dapr Integration

**Derives from:** [ADR-0014](../decisions/0014-adopt-dapr.md),
[ADR-0006](../decisions/0006-events-and-outbox.md),
[ADR-0010](../decisions/0010-cross-module-communication.md).

LearnStack uses **Dapr** (Distributed Application Runtime) for three building blocks:
**pub/sub** (Kafka), **state store** (Valkey), **secret store** (Vault). Application code
interacts with Dapr only through SharedKernel abstractions (`IEventBus`, `ICacheService`,
`ISecretProvider`) — never via `DaprClient` directly.

## 1. Topology

```mermaid
flowchart LR
    subgraph App[LearnStack.Host pod]
        API[ASP.NET API\n:5000]
        Daprd[Dapr sidecar\n:3500 HTTP / :50001 gRPC]
    end

    API -- localhost:3500 / localhost:50001 --> Daprd

    subgraph Backends[Backends]
        Kafka[(Kafka)]
        Valkey[(Valkey)]
        Vault[(HashiCorp Vault)]
    end

    Daprd -- pubsub.kafka --> Kafka
    Daprd -- state.redis --> Valkey
    Daprd -- secretstore.hashicorp.vault --> Vault

    subgraph Subscribers[Subscriber side]
        OtherApp[Another LearnStack pod\nor learnstack-hub pod]
        OtherDaprd[Dapr sidecar]
    end

    Kafka --> OtherDaprd --> OtherApp
```

The sidecar shares the network namespace of the app pod (Docker compose
`network_mode: "service:learnstack-api"`; Kubernetes via `dapr.io/enabled` annotation).
The app talks to the sidecar via `localhost`, never directly to Kafka / Valkey / Vault.

## 2. Components

Component YAML files live in `dapr/components/`. They are tracked in git as deployment
artifacts.

### `pubsub.yaml` — Kafka pub/sub

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: pubsub
  namespace: default
spec:
  type: pubsub.kafka
  version: v1
  metadata:
    - name: brokers
      value: kafka:29092
    - name: authType
      value: none                # production: SASL_SSL with TLS + SCRAM creds via Vault
    - name: consumeRetryInterval
      value: "200ms"
    - name: maxMessageBytes
      value: "1048576"           # 1MB
    - name: consumerID
      value: "learnstack-api"    # one per app id; isolates consumer groups
```

Topics follow the convention `learnstack.{module}.{aggregate}`. Examples:
- `learnstack.identity.user`
- `learnstack.tenancy.tenant`
- `learnstack.tenancy.organization`
- `learnstack.enrollment.enrollment`
- `learnstack.classroom.session`
- `learnstack.hub.entitlement`        (Hub-side)
- `learnstack.cache.invalidation`     (cross-instance L1 cache invalidation)

### `statestore.yaml` — Valkey state store

> The component below uses `spec.type: state.redis` and `redisHost` metadata —
> these are **Dapr provider-type / RESP-protocol identifiers**, NOT vendor
> brand markers. The actual backend is Valkey 8.x per
> [ADR-0030](../decisions/0030-redis-compatible-store-valkey.md); Valkey is
> drop-in compatible on the RESP wire protocol so the Dapr `state.redis`
> adapter consumes it unchanged. Operators read `redisHost` as "where to
> reach the RESP-compatible store"; in dev compose the value points at the
> `valkey` service (`infra/dapr/components/statestore-redis.yaml`).

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: statestore
  namespace: default
spec:
  type: state.redis
  version: v1
  metadata:
    - name: redisHost
      value: redis:6379
    - name: redisPassword
      secretKeyRef:
        name: redis-password
        key: redis-password
    - name: actorStateStore
      value: "false"             # we don't use actors
auth:
  secretStore: secretstore-vault
```

Used as L2 cache. Modules call `ICacheService.GetOrSetAsync(...)`; the implementation
wraps state-store calls plus an L1 in-memory cache plus tenant-aware key prefixing.

### `secretstore-vault.yaml` — Vault secret store

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: secretstore-vault
  namespace: default
spec:
  type: secretstores.hashicorp.vault
  version: v1
  metadata:
    - name: vaultAddr
      value: "https://vault:8200"
    - name: vaultToken
      value: "${VAULT_TOKEN}"   # dev only; production uses AppRole or Kubernetes auth
    - name: vaultKVPrefix
      value: "learnstack"
    - name: vaultKVUsePrefix
      value: "true"
    - name: enginePath
      value: "secret"
```

Secret path schema:

```
secret/learnstack/postgres            connection-string, ssl-cert
secret/learnstack/redis               password
secret/learnstack/keycloak            base-url, admin-username, admin-password
secret/learnstack/seaweedfs               endpoint, access-key, secret-key
secret/learnstack/meilisearch         master-key, public-key
secret/learnstack/livekit             api-key, api-secret, ws-url
secret/learnstack/coturn              shared-secret
secret/learnstack/hub                 internal-api-hmac-key, internal-api-mtls-cert,
                                      internal-api-mtls-key, internal-api-jwt-signing-key
```

In `Development` the **primary** `ISecretProvider` implementation is
`EnvironmentSecretProvider` (reads from process env vars; configured via
`.env`; matches the composition-root table in
[20-infrastructure-stack.md § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md)).
For dev workflows that prefer Dapr-shaped secrets (e.g. exercising the
`DaprSecretProvider` code path locally), an optional
`secretstore-local-file.yaml` reading from `dapr/components/secrets.json` is
**available** but not the default; the composition root picks one based on
`Deployment:Secrets:Provider` config (`env` | `dapr-file`). Both paths produce
the same observable behaviour through `ISecretProvider`.

### `secretstore-local-file.yaml` — optional dev variant

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: secretstore
  namespace: default
scopes:
  - environment: Development
spec:
  type: secretstores.local.file
  version: v1
  metadata:
    - name: secretsFile
      value: /components/secrets.json
    - name: nestedSeparator
      value: "/"
```

`secrets.json` is git-ignored; a `secrets.json.template` is committed.

## 3. SharedKernel abstractions

Application code interacts with Dapr exclusively through three interfaces in
`LearnStack.SharedKernel.Abstractions`:

```csharp
// LearnStack.SharedKernel.Abstractions.Messaging
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;
}

// LearnStack.SharedKernel.Abstractions.Caching
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        CacheOptions? options = null, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CacheOptions? options = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
}

public sealed record CacheOptions(TimeSpan? L1Ttl = null, TimeSpan? L2Ttl = null, string[]? Tags = null);

// LearnStack.SharedKernel.Abstractions.Secrets
public interface ISecretProvider
{
    Task<string> GetSecretAsync(string key, CancellationToken ct = default);
    Task<T> GetSecretAsync<T>(string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default);
}
```

Concrete implementations (`DaprEventBus`, `DaprCacheService`, `DaprSecretProvider`) live
in `LearnStack.Infrastructure.{Messaging, Caching, Secrets}`. They are the **only**
Dapr-aware code in the codebase.

### Development fallback: `InProcessEventBus`

When `DeploymentMode.Development` and the Dapr sidecar is not running, the composition
root registers `InProcessEventBus : IEventBus` instead. It routes
`PublishAsync<TEvent>(@event, ct)` to `MediatR.IPublisher.Publish(@event, ct)`. Module
subscribers (`INotificationHandler<TIntegrationEvent>`) handle the event in-process. No
durable buffer, no Kafka, no sidecar dependency for dev environments without Docker.

## 4. Cache implementation (DaprCacheService)

L1 in-memory + L2 Dapr State Store, with automatic tenant prefixing.

```csharp
internal sealed class DaprCacheService : ICacheService
{
    private readonly DaprClient _dapr;
    private readonly IMemoryCache _memoryCache;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _trackedKeys = new();
    private static long _lastCleanupTicks;
    private const string StateStoreName = "statestore";
    private static readonly Guid InstanceId = Guid.NewGuid();

    private string PrefixKey(string key)
    {
        var tenantId = _tenantContext.Current?.TenantId.ToString() ?? "platform";
        var orgId = _tenantContext.Current?.OrganizationId?.ToString();
        return orgId is null ? $"{tenantId}:{key}" : $"{tenantId}:{orgId}:{key}";
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        CacheOptions? options = null, CancellationToken ct = default)
    {
        var prefixed = PrefixKey(key);

        // L1
        if (_memoryCache.TryGetValue(prefixed, out T? cached) && cached is not null) return cached;

        // L2
        var (state, etag) = await _dapr.GetStateAndETagAsync<T?>(StateStoreName, prefixed, cancellationToken: ct);
        if (!string.IsNullOrEmpty(etag) && state is not null)
        {
            _memoryCache.Set(prefixed, state, options?.L1Ttl ?? TimeSpan.FromMinutes(2));
            return state;
        }

        // Factory
        var value = await factory(ct);
        await SetAsync(key, value, options, ct);
        return value;
    }

    public Task SetAsync<T>(string key, T value, CacheOptions? options = null, CancellationToken ct = default)
    {
        var prefixed = PrefixKey(key);
        _memoryCache.Set(prefixed, value, options?.L1Ttl ?? TimeSpan.FromMinutes(2));
        _trackedKeys[prefixed] = DateTimeOffset.UtcNow + (options?.L2Ttl ?? TimeSpan.FromMinutes(15));
        CleanupExpiredTrackedKeys();

        var metadata = new Dictionary<string, string>
        {
            ["ttlInSeconds"] = ((int)(options?.L2Ttl ?? TimeSpan.FromMinutes(15)).TotalSeconds).ToString()
        };
        return _dapr.SaveStateAsync(StateStoreName, prefixed, value, metadata: metadata, cancellationToken: ct);
    }

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var prefixed = PrefixKey(prefix);

        // Local removal
        foreach (var tracked in _trackedKeys.Keys.Where(k => k.StartsWith(prefixed, StringComparison.Ordinal)).ToList())
        {
            _memoryCache.Remove(tracked);
            _trackedKeys.TryRemove(tracked, out _);
            await _dapr.DeleteStateAsync(StateStoreName, tracked, cancellationToken: ct);
        }

        // Cross-instance L1 invalidation via Dapr pub/sub
        await _dapr.PublishEventAsync("pubsub", "learnstack.cache.invalidation",
            new CacheInvalidationEvent(prefixed, InstanceId), ct);
    }

    // ... (omitted: CleanupExpiredTrackedKeys throttled to once per 30s via Interlocked.CompareExchange)
}
```

Cross-instance L1 invalidation: when one pod calls `RemoveByPrefixAsync`, it publishes a
`learnstack.cache.invalidation` event. A `CacheInvalidationSubscriber` (background
service) on every pod consumes the event and clears matching L1 entries — except those
published by its own instance (`InstanceId` skip-self).

## 5. Sidecar deployment

### Docker Compose (development)

```yaml
services:
  learnstack-api:
    build: .
    ports: ["5100:5000"]
    depends_on:
      postgres:  { condition: service_healthy }
      redis:     { condition: service_healthy }
      kafka:     { condition: service_healthy }
      vault:     { condition: service_healthy }

  learnstack-api-dapr:
    image: daprio/daprd:1.14
    network_mode: "service:learnstack-api"
    command:
      - "./daprd"
      - "--app-id=learnstack-api"
      - "--app-port=5000"
      - "--dapr-http-port=3500"
      - "--dapr-grpc-port=50001"
      - "--resources-path=/components"
      - "--placement-host-address=dapr-placement:50006"
      - "--log-level=info"
    volumes:
      - ./dapr/components:/components:ro
    depends_on:
      - learnstack-api

  dapr-placement:
    image: daprio/placement:1.14
    command: ["./placement", "-port", "50006"]
    ports: ["50006:50006"]
```

### Kubernetes (production)

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: learnstack-api
  annotations:
    dapr.io/enabled: "true"
    dapr.io/app-id: "learnstack-api"
    dapr.io/app-port: "5000"
    dapr.io/config: "learnstack-config"
    dapr.io/log-level: "info"
```

Components are deployed as `Component` Custom Resources via Helm; the Dapr operator
hot-loads them.

## 6. Resilience policies

`config/resiliency.yaml` (Dapr Resiliency CR) defines retry / circuit-breaker / timeout
policies referenced by component metadata:

```yaml
apiVersion: dapr.io/v1alpha1
kind: Resiliency
metadata:
  name: learnstack-resiliency
spec:
  policies:
    timeouts:
      pubsubTimeout: 30s
      stateTimeout: 5s
      secretTimeout: 5s
    retries:
      pubsubRetry:
        policy: exponential
        duration: 200ms
        maxInterval: 30s
        maxRetries: 5
    circuitBreakers:
      pubsubCB:
        maxRequests: 10
        interval: 60s
        timeout: 30s
        trip: consecutiveFailures >= 5
  targets:
    components:
      pubsub:
        outbound:
          timeout: pubsubTimeout
          retry: pubsubRetry
          circuitBreaker: pubsubCB
      statestore:
        outbound:
          timeout: stateTimeout
      secretstore-vault:
        outbound:
          timeout: secretTimeout
```

## 7. Observability

Dapr emits OTLP traces and Prometheus metrics by default. Both are routed to the same
collector LearnStack uses (`otel-collector` service in compose; OTel Collector in K8s).

Traces:
- Every pub/sub publish gets a span (`<topic> publish`).
- Every state store call gets a span (`<store> get` / `<store> save`).
- Every secret store fetch gets a span (`<store> get`).
- W3C trace context propagated from app → sidecar → backend automatically.

Metrics (Prometheus):
- `dapr_component_pubsub_egress_count{component, topic}` — publish count per topic.
- `dapr_component_pubsub_ingress_count{component, topic, status}` — consume count + outcome.
- `dapr_state_<op>_count{component, status}` — state ops.
- `dapr_resiliency_count{name, policy, target_type}` — circuit-breaker / retry triggers.

## 8. Architecture tests

Three blocker-level tests (added in Phase 02):

1. `Dapr_SDK_Types_NotImportedOutsideInfrastructure` — `Dapr.Client.*` types appear only
   in `LearnStack.Infrastructure.{Caching, Messaging, Secrets}` namespaces. Roslyn-based
   source scan.
2. `Modules_DoNotReference_DaprPackage` — module csproj files have no `<PackageReference
   Include="Dapr.*" />` entries.
3. `ICacheService_Is_OnlyCacheAbstraction` — no module imports
   `Microsoft.Extensions.Caching.Distributed`, `Microsoft.Extensions.Caching.Memory`,
   `StackExchange.Redis`, or `Dapr.Client` directly. Same for `IEventBus` and
   `ISecretProvider`.

## 9. Migration / rollback

If Dapr proves unfit for a particular deployment, only the Infrastructure layer changes:

- Replace `DaprEventBus` with `KafkaEventBus` (`Confluent.Kafka` direct producer).
- Replace `DaprCacheService` with `RedisCacheService` (`StackExchange.Redis` direct).
- Replace `DaprSecretProvider` with `VaultSecretProvider` (`VaultSharp` direct).

Module code is unaffected because module code references only the interfaces. Migration
window: one release.

## 10. Non-goals (deliberately not adopted)

- **Service invocation** — modular monolith doesn't need cross-app HTTP via Dapr; modules
  call each other via `Application.Contracts` interfaces in-process.
- **Workflow** — Hangfire (ADR-0002) is the background-job runtime.
- **Bindings** — modules ship their own adapters for email, SMS, payment, etc. Bindings is
  reconsidered if adapter portability becomes problematic.
- **Actors** — domain model has no need for actors.
- **Configuration** — `IConfiguration` + `IOptions<T>` + database-backed
  `ITenantConfiguration` covers LearnStack.
- **Distributed lock** — `pg_try_advisory_lock` is the LearnStack pattern (Nexora-proven).

If any of these become needed, a new ADR scopes the change.

## References

- ADR-0014 — Adopt Dapr.
- ADR-0006 Amendment 1 — Dapr pub/sub dispatch transport.
- ADR-0010 Amendment 1 — Outbox dispatch via Dapr.
- [20-infrastructure-stack.md](../standards/20-infrastructure-stack.md) — usage rules.
- Nexora reference:
  `Nexora/docs/decisions/0013-cache-cross-instance-invalidation.md` (cross-instance
  caching),
  `Nexora/docs/decisions/0005-transactional-outbox.md` +
  `Nexora/docs/decisions/0010-notification-delivery-kafka.md` (messaging),
  `Nexora/docs/architecture/COMMUNICATION_FLOW.md`.
