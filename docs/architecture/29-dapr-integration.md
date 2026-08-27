# Dapr Integration

**Derives from:** [ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md),
[ADR-0006](../decisions/0006-events-and-outbox.md),
[ADR-0010](../decisions/0010-cross-module-communication.md).

> **Read this first.** This document describes Dapr in the present tense as the **target
> design**. Per [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) no Dapr
> component is wired into application code today. All three ports have shipped:
> `IEventBus` and `ICacheService` use the Packet 5 defaults `InProcessEventBus` and
> `InMemoryCacheService`; `ISecretProvider` uses `ConfigurationSecretProvider`.
> Those are the only registrations in every deployment mode. The three Dapr adapters
> land in
> [Phase 11](../roadmap/phase-11-production-hardening.md) against written triggers — a
> second process consuming an integration event, a second application instance, and
> secrets needing rotation without a redeploy. Nothing below is wrong; none of it is
> running.

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

**Production pod topology, in text:** the API process and its Dapr sidecar share one
pod. The API talks to the sidecar over localhost; the sidecar talks to Kafka, Valkey and
Vault; and it delivers subscribed events back to the API over HTTP. Nothing in a module
speaks to a broker directly — the ports do.

The diagram is the production pod target: the sidecar shares the app pod's network
namespace via the Kubernetes Dapr annotation. Local development deliberately runs the
.NET host on the workstation and the sidecar in Compose; the exact topology and service
inventory live in [`infra/dapr/README.md`](../../infra/dapr/README.md) and
[`infra/compose/README.md`](../../infra/compose/README.md). Do not duplicate the Compose
service graph here.

## 2. Components

Component YAML files live in `infra/dapr/components/` and are tracked deployment
artifacts. The files themselves are the operational source of truth; the summaries
below deliberately do not duplicate their complete metadata.

### `pubsub-kafka.yaml` — Kafka pub/sub

The committed [`pubsub-kafka.yaml`](../../infra/dapr/components/pubsub-kafka.yaml)
uses component name `pubsub`, the Compose-network broker `kafka:9092`, and consumer
group `learnstack-api`. Production authentication and broker endpoints are deployment
overrides, not a second checked-in copy here.

Topics follow the convention `learnstack.{module}.{aggregate}`. Examples:
- `learnstack.identity.user`
- `learnstack.tenancy.tenant`
- `learnstack.tenancy.organization`
- `learnstack.enrollment.enrollment`
- `learnstack.classroom.session`
- `learnstack.hub.entitlement`        (Hub-side)
- `learnstack.cache.invalidation`     (cross-instance L1 cache invalidation)

### `statestore-redis.yaml` — Valkey state store

> The component below uses `spec.type: state.redis` and `redisHost` metadata —
> these are **Dapr provider-type / RESP-protocol identifiers**, NOT vendor
> brand markers. The actual backend is Valkey 8.x per
> [ADR-0030](../decisions/0030-redis-compatible-store-valkey.md); Valkey is
> drop-in compatible on the RESP wire protocol so the Dapr `state.redis`
> adapter consumes it unchanged. Operators read `redisHost` as "where to
> reach the RESP-compatible store"; in dev compose the value points at the
> `valkey` service (`infra/dapr/components/statestore-redis.yaml`).

The committed
[`statestore-redis.yaml`](../../infra/dapr/components/statestore-redis.yaml) uses
component name `statestore`, points `redisHost` at `valkey:6379`, and explicitly
sets `actorStateStore` to `false`. Its empty development password is replaced by
deployment configuration when the Phase 11 adapter lands.

Used as L2 cache. Modules call `ICacheService.GetOrSetAsync(...)`; the implementation
wraps state-store calls plus an L1 in-memory cache plus tenant-aware key prefixing.

### `secretstore-vault.yaml` — Vault secret store

The committed
[`secretstore-vault.yaml`](../../infra/dapr/components/secretstore-vault.yaml) uses
component name `secretstore`, the development endpoint `http://vault:8200`, and a
`secretKeyRef` resolved through `envvar-secrets`. It contains no literal token;
production replaces the development token flow with AppRole or Kubernetes auth.

Secret path schema:

```
secret/learnstack/postgres            connection-string, ssl-cert
secret/learnstack/valkey              password
secret/learnstack/keycloak            base-url, admin-username, admin-password
secret/learnstack/seaweedfs           endpoint, access-key, secret-key
secret/learnstack/meilisearch         master-key, public-key
secret/learnstack/livekit             api-key, api-secret, ws-url
secret/learnstack/coturn              shared-secret
secret/learnstack/hub                 internal-api-hmac-key, internal-api-mtls-cert,
                                      internal-api-mtls-key, internal-api-jwt-signing-key
```

In `Development` the **primary** `ISecretProvider` implementation is
`ConfigurationSecretProvider` (reads `IConfiguration`, which already merges environment
variables, user secrets and `appsettings.{env}.json`; matches the composition-root table in
[20-infrastructure-stack.md § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md)).
The committed `secretstore-envvar.yaml` is bootstrap support for the Dapr Vault
component; it does not replace `ISecretProvider` and does not select an application
adapter.

### `secretstore-envvar.yaml` — Vault bootstrap support

```yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: envvar-secrets
spec:
  type: secretstores.local.env
  version: v1
```

It supplies the development Vault token to `secretstore-vault.yaml` through
`secretKeyRef`. `ConfigurationSecretProvider` remains the only application registration
until ADR-0035's Vault trigger fires.

## 3. SharedKernel abstractions

Application code interacts with Dapr exclusively through three interfaces in
`LearnStack.SharedKernel`:

```csharp
// LearnStack.SharedKernel.Messaging
public interface IEventBus
{
    Task PublishAsync(IntegrationEventEnvelope envelope, CancellationToken ct = default);
}

// LearnStack.SharedKernel.Caching
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        CacheOptions? options = null, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CacheOptions? options = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

public sealed record CacheOptions(TimeSpan? L1Ttl = null, TimeSpan? L2Ttl = null);

// LearnStack.SharedKernel.Secrets
public interface ISecretProvider
{
    Task<string> GetSecretAsync(string key, CancellationToken ct = default);
    Task<T> GetSecretAsync<T>(string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(string prefix, CancellationToken ct = default);
}
```

`IEventBus.PublishAsync` is **not generic** and `ICacheService` has **no
`RemoveByPrefixAsync`**; `CacheOptions` carries **no `Tags`**. All three are governed by
[ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md) — see
[15-event-and-outbox.md](15-event-and-outbox.md) for why a generic publish reaches zero
handlers at the only call site that matters, and § 4 below for why a prefix removal
cannot be honoured across instances.

**Keys are composed by the caller, not by the adapter.** `CacheKey.ForTenant(tenantId,
module, name)` — or `ForOrganization(...)` — produces the key and `CacheKey.EnsureValid` guards
its shape, so an adapter that also prefixed would emit `{tenant}:{tenant}:{module}:{name}`.
[Standards 20 § `ICacheService`](../standards/20-infrastructure-stack.md) fixes the
shape; every implementation validates, none rewrites.

The target concrete implementations (`DaprEventBus`, `DaprCacheService`,
`DaprSecretProvider`) will live in
`LearnStack.Infrastructure.{Messaging, Caching, Secrets}`. Once added, they are the
only application code permitted to know Dapr types.

### Current default: `InProcessEventBus`

Every deployment mode currently registers `InProcessEventBus : IEventBus`, even when a
developer starts the gated sidecar for inspection. It is a **transport, not a stub**: it reads
construction-free subscription metadata, gives each subscription one consumer activity
and one async DI scope, restores tenant and module context before resolving exactly one
concrete `IIntegrationEventHandler<T>`, and restores the publisher's context after the
full dispatch. It leaves `IInboxGuard` deduplication to the handler exactly as the
durable path does and preserves per-partition-key ordering. A default path that skipped
those is a path where the isolation code is never exercised — see
[15-event-and-outbox.md](15-event-and-outbox.md), which owns the implementation, and
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md), which makes the four
obligations a condition of the gating.

## 4. Cache implementation (DaprCacheService)

L1 in-memory + L2 Dapr state store. The default implementation shipped in Packet 5 is
`InMemoryCacheService` (L1 only); this adapter lands on
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger — more than one
application instance running concurrently.

```csharp
internal sealed class DaprCacheService : ICacheService
{
    private readonly DaprClient _dapr;
    private readonly IMemoryCache _memoryCache;
    private const string StateStoreName = "statestore";

    public async Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        CacheOptions? options = null, CancellationToken ct = default)
    {
        // The key already carries its tenant — CacheKey composed it. Validate,
        // never re-prefix.
        CacheKey.EnsureValid(key);

        if (_memoryCache.TryGetValue(key, out T? cached) && cached is not null) return cached;

        var (state, etag) = await _dapr.GetStateAndETagAsync<T?>(StateStoreName, key, cancellationToken: ct);
        if (!string.IsNullOrEmpty(etag) && state is not null)
        {
            _memoryCache.Set(key, state, options?.L1Ttl ?? TimeSpan.FromMinutes(2));
            return state;
        }

        // Abridged: the shipped `InMemoryCacheService` coalesces concurrent
        // misses per (key, requested type) so one factory runs however many
        // callers arrive, the first caller owns the TTL, and a replacement waits
        // for an abandoned factory to terminate. This adapter owes the same
        // contract — the factory is the expensive side, and a cache that lets N
        // simultaneous misses each run it turns a cold key into a stampede
        // against the dependency it exists to spare.
        var value = await factory(ct);
        await SetAsync(key, value, options, ct);
        return value;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        CacheKey.EnsureValid(key);

        if (_memoryCache.TryGetValue(key, out T? cached) && cached is not null) return cached;

        var (state, etag) = await _dapr.GetStateAndETagAsync<T?>(StateStoreName, key, cancellationToken: ct);
        return string.IsNullOrEmpty(etag) ? default : state;
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        CacheKey.EnsureValid(key);
        _memoryCache.Remove(key);
        await _dapr.DeleteStateAsync(StateStoreName, key, cancellationToken: ct);
    }

    public Task SetAsync<T>(string key, T value, CacheOptions? options = null, CancellationToken ct = default)
    {
        CacheKey.EnsureValid(key);
        _memoryCache.Set(key, value, options?.L1Ttl ?? TimeSpan.FromMinutes(2));

        var metadata = new Dictionary<string, string>
        {
            ["ttlInSeconds"] = ((int)(options?.L2Ttl ?? TimeSpan.FromMinutes(15)).TotalSeconds).ToString()
        };
        return _dapr.SaveStateAsync(StateStoreName, key, value, metadata: metadata, cancellationToken: ct);
    }
}
```

**Required parity:** concurrent misses for the same key and requested type are
single-flight. The factory executes once, the first caller owns the TTL, and an
abandoned factory must terminate before a replacement starts. The Dapr adapter must
coalesce misses across its L1 path just as `InMemoryCacheService` does; adding L2 must
not reintroduce a stampede.

### Why there is no prefix removal, and no invalidation topic

An earlier version of this document carried a `RemoveByPrefixAsync` backed by an
instance-local `_trackedKeys` dictionary, whose subscriber cleared *prefix-matching* L1
entries on every other pod. That is superseded by
[ADR-0038 § Cache contract](../decisions/0038-cross-cutting-port-and-event-contracts.md#cache-contract).

The `learnstack.cache.invalidation` topic itself survives, and it is worth being precise
about what changed: the topic carries a `(tenant_id, cache_key)` payload and evicts **one
named key** across instances, which is enumerable by construction. What died is
invalidating a *set* the caller cannot enumerate. The topic is owned by
[Phase 11](../roadmap/phase-11-production-hardening.md), which lands the Dapr and Valkey
adapters and tests it under a broker partition — before that there is one instance and
one cache, so there is nothing to invalidate across.

The tracked-key set is the defect: it holds only what *this* instance wrote, so keys
written by another pod were never evicted — a method whose name promised a global effect
while delivering a local one, and which under-invalidated the moment a second instance
ran. That is precisely the condition under which the Dapr adapter exists at all.

What replaces it is a tenant-scoped **generation counter** embedded in the key template:
a durable value bumped inside the business transaction, so a write makes every stale key
unreachable at once without enumerating or deleting any of them, and without a
topic on the write path. It is a caller-side convention rather than a member of
`ICacheService`. See
[32-tenant-customization-model.md § 8.2](32-tenant-customization-model.md).

## 5. Sidecar deployment

### Docker Compose (development)

The authoritative local topology is the `gated` profile in
[`infra/compose/dev.yml`](../../infra/compose/dev.yml), explained once in
[`infra/compose/README.md`](../../infra/compose/README.md) and
[`infra/dapr/README.md`](../../infra/dapr/README.md). The workstation-hosted API,
`dapr-sidecar-api`, `host.docker.internal:5080`, placement port and pinned images must
not be duplicated here; those operational values change independently of this target
architecture.

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

Phase 11 will add `infra/dapr/config/resiliency.yaml` as a Dapr Resiliency CR defining
retry / circuit-breaker / timeout policies referenced by component metadata. No such
file is wired today; the following is the target shape:

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

Three blocker-level tests land with the Dapr adapters in Phase 11:

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

- ADR-0038 — Cross-Cutting Port and Event Contracts.
- ADR-0006 Amendment 1 — Dapr pub/sub dispatch transport.
- ADR-0010 Amendment 1 — Outbox dispatch via Dapr.
- [20-infrastructure-stack.md](../standards/20-infrastructure-stack.md) — usage rules.
- Nexora reference:
  `Nexora/docs/decisions/0013-cache-cross-instance-invalidation.md` (cross-instance
  caching),
  `Nexora/docs/decisions/0005-transactional-outbox.md` +
  `Nexora/docs/decisions/0010-notification-delivery-kafka.md` (messaging),
  `Nexora/docs/architecture/COMMUNICATION_FLOW.md`.
