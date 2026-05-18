# ADR 0014: Adopt Dapr for Cross-Cutting Infrastructure

## Status

Accepted

## Date

2026-05-18

## Decision

LearnStack adopts **Dapr** (Distributed Application Runtime) for three building blocks:

| Building block | Backend (production) | Component file |
|----------------|---------------------|----------------|
| Pub/Sub | Apache Kafka | `dapr/components/pubsub.yaml` |
| State store | Redis | `dapr/components/statestore.yaml` |
| Secret store | HashiCorp Vault | `dapr/components/secretstore-vault.yaml` |

Application code interacts with Dapr **exclusively through wrapped abstractions** —
`IEventBus`, `ICacheService`, `ISecretProvider` in `LearnStack.SharedKernel` — never via
`DaprClient` directly.

Other Dapr building blocks (service invocation, workflow, bindings, actors, configuration,
distributed lock) are **not adopted at this time**; they remain available for future
ADRs if a concrete need appears.

## Context

LearnStack is a modular monolith multi-tenant PaaS for education. It needs:

- Cross-module integration events with at-least-once delivery, durable replay, multi-tenant
  fan-out. The outbox pattern (ADR-0010, ADR-0006) requires a dispatch target.
- Distributed cache (L2) on top of in-process memory cache (L1) for tenant-scoped reads.
- Secret access for Keycloak admin credentials, MinIO access keys, LiveKit API secrets,
  Stripe/Iyzico API keys, exchange rate API keys, etc.

The platform plans triple deployment (SaaS / Dedicated / Self-Hosted; ADR-0020). All three
must use the same backend abstractions; backend providers may differ per deployment
(Vault in production, file-based secret store in dev; managed Redis in SaaS, self-hosted
Redis on-prem).

Nexora's experience (see `Nexora/docs/architecture/COMMUNICATION_FLOW.md`,
`Nexora/docs/decisions/0005-transactional-outbox.md`,
`Nexora/docs/decisions/0011-outbox-service-atomicity.md`,
`Nexora/docs/decisions/0013-cache-cross-instance-invalidation.md`) demonstrated:

- Dapr-backed `IEventBus` / `ICacheService` / `ISecretProvider` abstractions caused **zero
  module-level coupling to Dapr** — the interfaces live in SharedKernel, the
  `DaprXxxService` implementation lives in Infrastructure, and modules never import
  `Dapr.Client`.
- Component swap (Kafka → another broker, Redis → another KV) is a configuration-only
  change with the same interface signatures.
- Same sidecar pattern works in Docker Compose (dev), Kubernetes (production), and air-gapped
  installations (on-prem with bundled sidecar binary).

## Decision drivers

1. **Provider portability.** A LearnStack deployment may need to replace Kafka with RabbitMQ,
   Redis with KeyDB, Vault with AWS Secrets Manager. Dapr's component model treats this as a
   YAML change, not a code change.
2. **Same abstraction across deployments.** SaaS uses managed Kafka; Self-Hosted uses bundled
   Kafka; both call the same `IEventBus.PublishAsync`. The differentiating layer is
   `dapr/components/*.yaml`, not application code.
3. **Resiliency by default.** Dapr provides retry, circuit breaker, dead-letter, and outbox
   semantics on top of every component without extra application code. LearnStack's outbox
   pattern (ADR-0006) leans on Dapr's at-least-once delivery guarantees.
4. **Mature OSS, CNCF Graduated (2024).** Production-grade, multi-vendor support, large
   ecosystem.
5. **Operational uniformity.** One sidecar pattern, one observability story (Dapr emits OTel
   traces + metrics), one secret-rotation story (component metadata pulls from secret store
   automatically).
6. **Battle-tested in the same architecture style** by Nexora — a sister project run by the
   same team, modular monolith multi-tenant SaaS — without abandoned-attempts or rip-out
   pain.

## Considered options

### Option A — Adopt Dapr for pub/sub + state + secrets (chosen)

Three components in production; same components in dev (with secret-store local file fallback
when Vault unavailable). Application code uses `ICacheService`, `IEventBus`, `ISecretProvider`
exclusively.

**Pros:**
- Provider portability via component YAML.
- Same interfaces across SaaS / Dedicated / Self-Hosted.
- Resiliency built-in.
- Proven in Nexora.

**Cons:**
- One more runtime (sidecar) to operate.
- Additional learning curve for engineers new to Dapr.
- Sidecar startup ordering must be respected (Dapr must be ready before app starts publishing).

### Option B — Direct integration (rejected)

Application code uses `Confluent.Kafka` producer, `StackExchange.Redis` directly,
`VaultSharp` directly. No abstraction layer beyond simple wrappers.

**Pros:**
- One less moving part (no sidecar).
- Simpler local development for engineers who only know one backend.

**Cons:**
- Provider lock-in. Switching brokers / state stores / secret managers requires code
  changes across every consumer.
- Reimplements outbox / retry / DLQ semantics manually for each backend.
- Diverges from Nexora pattern — no cross-project pattern sharing.

### Option C — Custom abstraction layer (rejected)

Define `IEventBus`, `ICacheService`, `ISecretProvider` exactly as in Option A, but implement
them with direct backend SDKs (no Dapr). The interfaces match Option A; only the
implementation differs.

**Pros:**
- Interface-level portability is preserved.
- One less runtime than Option A.

**Cons:**
- Reimplements building blocks Dapr already provides (retry, DLQ, secret rotation
  notification, multi-namespace pub/sub).
- Diverges from Nexora pattern; ports become source-of-truth for the team's distributed-
  systems primitives, and any bug fix lives only in LearnStack.
- The custom layer becomes a non-trivial maintenance burden as new building blocks are
  needed (eventual addition of workflows, configuration, distributed lock would each
  require new abstractions in LearnStack).

## Decision outcome

Adopt **Option A**: Dapr for pub/sub + state + secrets.

### Sidecar deployment

- **Local dev**: Docker Compose service `learnstack-api-dapr` sharing the network namespace
  of `learnstack-api` (`network_mode: "service:learnstack-api"`). App talks to
  `http://localhost:3500` (HTTP) / `localhost:50001` (gRPC).
- **Kubernetes (production / staging)**: Dapr sidecar injected via annotation
  (`dapr.io/enabled: "true"`, `dapr.io/app-id: "learnstack-api"`, `dapr.io/app-port: "5000"`).
- **Self-Hosted on-prem (no Kubernetes)**: Dapr runs as a daemon (`daprd`) alongside the
  LearnStack process; sidecar packaged in Helm chart with optional `--mode standalone`.

### Application access pattern

Every module's domain and application code uses:

```csharp
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;
}

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory,
        CacheOptions? options = null, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, CacheOptions? options = null,
        CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
}

public interface ISecretProvider
{
    Task<string> GetSecretAsync(string key, CancellationToken ct = default);
    Task<T> GetSecretAsync<T>(string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetSecretsAsync(string prefix,
        CancellationToken ct = default);
}
```

Concrete implementations (`DaprEventBus`, `DaprCacheService`, `DaprSecretProvider`) live in
`LearnStack.Infrastructure` and are the **only** Dapr-aware code in the codebase.

### Non-goals (explicitly out of scope)

- **Service invocation** (`POST /v1.0/invoke/<app-id>/method/<method>`) — LearnStack is a
  modular monolith; cross-module calls go through MediatR or `Application.Contracts`,
  not Dapr service invocation.
- **Workflow** — Hangfire (ADR-0002, ADR-0006) is the background-job runtime. Dapr Workflow
  is reconsidered if and when Hangfire reaches a capability ceiling.
- **Bindings** — modules ship their own adapters for email (SendGrid/Postmark), SMS
  (Twilio), payment (Stripe/Iyzico), etc. Dapr Bindings is reconsidered when an adapter's
  cross-deployment portability becomes a problem.
- **Actors** — LearnStack's domain model has no need for actors.
- **Configuration** — `IConfiguration` + `IOptions<T>` + database-backed
  `ITenantConfiguration` cover the LearnStack use cases.
- **Distributed lock** — `pg_try_advisory_lock` is the LearnStack pattern (ADR-0017
  customisation; advisory locks in MigrationRunner per Nexora pattern).

## Architecture tests

Three architecture tests enforce the abstraction boundary (added in Phase 02):

1. `Dapr_SDK_Types_NotImportedOutsideInfrastructure` — `Dapr.Client.*` types appear only
   in `LearnStack.Infrastructure.{Caching,Messaging,Secrets}` namespaces.
2. `Modules_DoNotReference_DaprPackage` — module assemblies have no NuGet dependency on
   `Dapr.Client` or `Dapr.AspNetCore`.
3. `ICacheService_Is_OnlyCacheAbstraction` — no module imports `Microsoft.Extensions.Caching.Distributed`,
   `Microsoft.Extensions.Caching.Memory`, `StackExchange.Redis`, or `DaprClient` directly.

## Consequences

### Positive

- Provider portability: switching Kafka → RabbitMQ, Redis → KeyDB, Vault → AWS Secrets
  Manager is a Dapr component YAML change.
- Same code, same components across SaaS / Dedicated / Self-Hosted.
- Outbox + at-least-once delivery + retry + DLQ provided by Dapr pub/sub; LearnStack's
  outbox table (ADR-0010 Amendment 1) is the producer-side durable buffer; Dapr is the
  consumer-side dispatcher.
- Operational observability: Dapr emits OTel traces and metrics matching LearnStack's stack.

### Negative

- Adds Dapr sidecar to every deployment topology. Local dev needs the sidecar running;
  developers without Docker won't be able to test Dapr-backed paths.
- Cold-start ordering: app must wait for Dapr sidecar ready (mitigated by `dapr-app-id`
  health check and retry on the first publish).
- Operational learning curve. Engineers unfamiliar with Dapr need orientation
  (`docs/architecture/29-dapr-integration.md` is the entry point).

### Neutral

- Dapr's own configuration (component YAML files) becomes a new artifact tracked in git
  alongside infrastructure config. They are reviewed like any other deployment artifact.

## Implementation notes

- Phase 01 — Repository tooling: `dapr/components/` directory scaffolded with sample
  `pubsub.yaml`, `statestore.yaml`, `secretstore-vault.yaml`; docker-compose service
  `learnstack-api-dapr` added; Dapr sidecar logs verified at first run.
- Phase 02 — Platform kernel: `DaprEventBus`, `DaprCacheService`, `DaprSecretProvider`
  implementations land; modules consume only the interfaces. `EnvironmentSecretProvider`
  composite fallback for dev when Vault not reachable.
- Phase 11 — Production hardening: Kubernetes sidecar injection annotations finalised;
  per-environment Dapr resiliency policies tuned; production secret rotation procedure.

The full deployment topology, component examples, and operational runbook live in
[29-dapr-integration.md](../architecture/29-dapr-integration.md).

## References

- ADR-0006 — Events and Outbox (status: Accepted after this ADR; previously Proposed).
- ADR-0010 — Cross-Module Communication; Amendment 1 specifies Dapr pub/sub as the
  outbox dispatch target.
- ADR-0019 — LearnStack Hub (separate Dapr namespace for Hub if applicable).
- ADR-0020 — Triple Deployment Model.
- [29-dapr-integration.md](../architecture/29-dapr-integration.md) — architecture deep dive.
- [20-infrastructure-stack.md](../standards/20-infrastructure-stack.md) — Dapr usage rules.
