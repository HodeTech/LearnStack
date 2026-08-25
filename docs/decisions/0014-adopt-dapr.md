# ADR 0014: Adopt Dapr for Cross-Cutting Infrastructure

## Status

Accepted (Amendment 1: 2026-08-08 — schedule moved to Phase 11; **Amendment 2:
2026-08-24 — corrects the published `IEventBus` and `ICacheService` signatures**;
see bottom of document)

## Date

2026-05-18

## Decision

LearnStack adopts **Dapr** (Distributed Application Runtime) for three building blocks:

| Building block | Backend (production) | Component file |
|----------------|---------------------|----------------|
| Pub/Sub | Apache Kafka | `infra/dapr/components/pubsub-kafka.yaml` |
| State store | Valkey (RESP-protocol fork; see ADR-0030) | `infra/dapr/components/statestore-redis.yaml` (file name keeps the `-redis` suffix because `state.redis` is the Dapr provider-type identifier, not the vendor brand) |
| Secret store | HashiCorp Vault | `infra/dapr/components/secretstore-vault.yaml` |

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
- Secret access for Keycloak admin credentials, SeaweedFS access keys, LiveKit API secrets,
  Stripe/Iyzico API keys, exchange rate API keys, etc.

The platform plans triple deployment (SaaS / Dedicated / Self-Hosted; ADR-0020). All three
must use the same backend abstractions; backend providers may differ per deployment
(Vault in production, file-based secret store in dev; managed Valkey in SaaS, self-hosted
Valkey on-prem).

Nexora's experience (see `Nexora/docs/architecture/COMMUNICATION_FLOW.md`,
`Nexora/docs/decisions/0005-transactional-outbox.md`,
`Nexora/docs/decisions/0011-outbox-service-atomicity.md`,
`Nexora/docs/decisions/0013-cache-cross-instance-invalidation.md`) demonstrated:

- Dapr-backed `IEventBus` / `ICacheService` / `ISecretProvider` abstractions caused **zero
  module-level coupling to Dapr** — the interfaces live in SharedKernel, the
  `DaprXxxService` implementation lives in Infrastructure, and modules never import
  `Dapr.Client`.
- Component swap (Kafka → another broker, Valkey → another KV) is a configuration-only
  change with the same interface signatures.
- Same sidecar pattern works in Docker Compose (dev), Kubernetes (production), and air-gapped
  installations (on-prem with bundled sidecar binary).

## Decision drivers

1. **Provider portability.** A LearnStack deployment may need to replace Kafka with RabbitMQ,
   Valkey with KeyDB, Vault with AWS Secrets Manager. Dapr's component model treats this as a
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

**The `IEventBus` and `ICacheService` signatures below are superseded by**
[Amendment 2](#2026-08-24--amendment-2-two-port-signatures-corrected-before-first-use).
They are left as written because an Accepted ADR's Decision section is not rewritten;
what Packet 5 ships is the amended shape. `ISecretProvider` is unchanged and shipped in
Packet 3.

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

- Provider portability: switching Kafka → RabbitMQ, Valkey → KeyDB, Vault → AWS Secrets
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

## Amendments

### 2026-08-08 — Schedule moved to Phase 11; the Decision is unchanged

The Decision stands: when LearnStack needs a cross-process event bus, a distributed cache
or a secret store, it uses Dapr for all three, and application code reaches them only
through `IEventBus` / `ICacheService` / `ISecretProvider`. This amendment does not
withdraw that choice.

What moved is **when**. Per [ADR-0035](0035-demand-gated-infrastructure.md) Dapr is
additive under the one-way-door test, so the three adapters are demand-gated:

| Adapter | Registered until then | Lands in | Trigger |
|---|---|---|---|
| `DaprEventBus` | `InProcessEventBus` | [Phase 11](../roadmap/phase-11-production-hardening.md) | A second process must consume an integration event |
| `DaprCacheService` | `InMemoryCacheService` | [Phase 11](../roadmap/phase-11-production-hardening.md) | More than one application instance runs concurrently |
| `DaprSecretProvider` | `ConfigurationSecretProvider` | [Phase 11](../roadmap/phase-11-production-hardening.md) | A production secret must rotate without a redeploy, or more than one operator needs access to production secrets |

The Implementation-notes bullet reading "Phase 02 — Platform kernel" and the
Architecture-tests preamble reading "added in Phase 02" both mean Phase 11 under
ADR-0035. The three architecture tests are already scheduled to Phase 11 in
[21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md);
they cannot run before the SDK is referenced.

The default secret provider that shipped in Phase 02a Packet 3 is named
`ConfigurationSecretProvider`, not `EnvironmentSecretProvider`, and reads
`IConfiguration` — which already merges environment variables, user secrets and
`appsettings.{env}.json` — rather than process environment variables alone.

### 2026-08-24 — Amendment 2: two port signatures, corrected before first use

The Decision stands. Dapr remains the cross-process choice for pub/sub, state and
secrets, and application code still reaches all three only through `IEventBus` /
`ICacheService` / `ISecretProvider`. What this amendment corrects is the **published
shape** of two of those interfaces, which Phase 02a Packet 5 is about to ship as code
and can only ship one way.

**1. `ICacheService.RemoveByPrefixAsync` is removed.**

The port becomes `GetAsync` / `GetOrSetAsync` / `SetAsync` / `RemoveAsync` and nothing
else. The reference implementation iterates a process-local key set, so keys written by
another instance are never evicted — a contract no candidate backend can honour, and one
whose name promises a global effect while delivering a local one.

The roadmap offered "removed **or** redesigned to a generation-key pattern". That is not
a fork at the port: the corpus's own definition of the pattern puts the counter in
**durable domain state** — a `customization_generation` column bumped inside the
business transaction and embedded in the key template ([architecture/32 §
8.2](../architecture/32-tenant-customization-model.md)) — which adds no member to this
interface. It also cannot live in the cache: an evicted counter would make previously
abandoned keys addressable again and resurrect stale values. Both branches therefore
remove the method, and the generation-key pattern is recorded as a **caller-side
convention** owned by the consumers that specify it.

Nothing is lost by the removal: the corpus contains no call site for the method.

**2. `IEventBus.PublishAsync` takes a partition key, and is not generic.**

Published here as:

```csharp
Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
    where TEvent : IIntegrationEvent;
```

It becomes:

```csharp
Task PublishAsync(IIntegrationEvent @event, string partitionKey, CancellationToken ct = default);
```

Two corrections in one signature.

*The partition key* is what
[architecture/15 § The bus](../architecture/15-event-and-outbox.md) and [Phase
02b](../roadmap/phase-02b-events-auth.md) already publish, and it is what lets the
durable transport map onto a Kafka message key and preserve per-aggregate ordering.
Adding a required parameter after the first consumer exists breaks every call site, so
the two shapes cannot be left to be reconciled later.

*The generic parameter* is removed because the outbox dispatcher deserializes to
`object` and calls through the base interface —
`eventBus.PublishAsync((IIntegrationEvent)eventInstance!, msg.PartitionKey, ct)` at
[architecture/15](../architecture/15-event-and-outbox.md). With a generic port, `TEvent`
binds to `IIntegrationEvent` at that call, so a transport resolving
`IIntegrationEventHandler<TEvent>` looks for
`IIntegrationEventHandler<IIntegrationEvent>` — which no concrete handler implements.
The result is a publish that dispatches to **zero handlers** and reports success. A
non-generic port makes the runtime-type resolution the transport has to do anyway
explicit, rather than hiding it behind a type parameter that is always erased to the
base interface at the only call site that matters.

Every other document publishing either signature is corrected in the same change:
`architecture/15`'s three sketches (the interface, `DaprEventBus` and `InProcessEventBus`,
the last of which must resolve handlers by runtime type rather than through a type
parameter), `architecture/32 § 8.2` and the Packet 5 scope paragraph, both of which stop
saying "removed **or** redesigned" now that it is removed.

### 2026-08-25 — Amendment 3: the publish envelope, decided before the first call site

The Decision stands, and so does Amendment 2's central correction — `PublishAsync` is
not generic, and it never becomes generic. What Amendment 3 changes is the **shape of
its argument**, which Amendment 2 published as `(IIntegrationEvent @event, string
partitionKey, CancellationToken ct)` and which Packet 5 has now built against.

**`IEventBus.PublishAsync` takes an envelope.**

```csharp
Task PublishAsync(IntegrationEventEnvelope envelope, CancellationToken ct = default);

public sealed record IntegrationEventEnvelope(
    IIntegrationEvent Event,
    string CorrelationId,
    Guid? OrganizationId = null,
    Guid? CausationId = null,
    UserId? ActorUserId = null)
{
    public string PartitionKey => Event.PartitionKey;
    public string Topic => Event.Topic;
}
```

> **Refined the same day.** `Topic` was first a parameter on this record, and it should
> not have been. It is a property of the event *type* — two events of one type always go
> to the same channel, and the name is derivable from the type — so a per-delivery
> parameter is the same second-source hazard `PartitionKey` had. It also made the
> catalogued `Integration_Event_TopicNames_FollowConvention` unwritable: that rule reads
> the event declarations, and nothing declared a topic. `Topic` is abstract on
> `IntegrationEventBase`; the envelope reads it.

Three things forced it, and all three were measured rather than argued.

**The dispatch metadata had nowhere to travel.** The canonical `outbox_messages` row
([Database Standards](../standards/05-database.md)) requires `topic` and
`correlation_id` as `NOT NULL` and carries `organization_id`, `causation_id` and
`actor_user_id`. None of them belong on the event — they describe the delivery, not the
fact — and the two-parameter signature had no room for them. The transport therefore
read correlation from whatever context happened to be ambient at dispatch, which is
`null` inside the background service the outbox processor is, so the trace chain broke
at exactly the boundary [Observability Standards](../standards/10-observability.md)
requires it to cross.

**The partition key had two sources and the transport read the wrong one.** Amendment 2
put it in the signature; `IntegrationEventBase` also declares it. Measured: the shipped
bus never read the event's copy, and every test published an event declaring one key
with a different one passed alongside — green. Ordering is guaranteed per partition key,
so a key that can differ from itself is a guarantee that cannot be stated. The envelope
reads it off the event and cannot disagree with it.

**A consumer could not write state at all.** `AuditableEntity.MarkCreated` refuses
`default(UserId)` and `Guid.Empty`, and the consumer context supplied neither an actor
nor an organization — so every state-writing handler threw from inside the kernel, and
every organization-scoped read came back empty under the canonical Row Level Security
policy, which fails closed when `app.organization_id` is unset. The envelope carries
both; an absent actor resolves to `UserId.SystemActor`, which is what
[Audit Coverage](../standards/18-audit-coverage.md) means by auditing such work as an
actor of type `system`.

**Why now.** Amendment 2 wrote the rule this amendment obeys: *adding a required
parameter after the first consumer exists breaks every call site, so the two shapes
cannot be left to be reconciled later.* There is still not one consumer. The envelope is
one type, it maps onto the outbox row Packet 6 creates, and it is the last moment it
costs nothing.

> The signature published under Amendment 2 above is superseded by this one. It is left
> as written because an Accepted ADR is not rewritten; the non-generic decision it makes
> is unchanged and is the reason the envelope carries the event as `IIntegrationEvent`.

**One consequence worth stating, because it is a trap the non-generic port creates.**
With `IIntegrationEvent` as the declared type at every dispatch boundary,
`JsonSerializer.Serialize(@event)` emits only the four interface members and silently
drops everything the concrete event added — valid JSON, no exception, and the loss
commits inside the business transaction that reported success. `IntegrationEventBase`
therefore ships `ToPayloadJson()`, which serialises by runtime type, and a named
`PayloadJsonOptions` — because a writer and a reader that disagree on casing
dead-letter every message.

## References

- ADR-0006 — Events and Outbox (status: Accepted after this ADR; previously Proposed).
- ADR-0010 — Cross-Module Communication; Amendment 1 specifies Dapr pub/sub as the
  outbox dispatch target.
- ADR-0019 — LearnStack Hub (separate Dapr namespace for Hub if applicable).
- ADR-0020 — Triple Deployment Model.
- [29-dapr-integration.md](../architecture/29-dapr-integration.md) — architecture deep dive.
- [20-infrastructure-stack.md](../standards/20-infrastructure-stack.md) — Dapr usage rules.
