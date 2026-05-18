---
name: wire-dapr-pubsub
description: >
  Wire a Dapr pub/sub topic for cross-module integration events, with the
  `learnstack.{module}.{aggregate}` topic-name convention, Kafka as the broker, and
  the `InProcessEventBus` dev fallback. USE FOR: adding a new topic, configuring the
  Dapr component YAML for a new module, switching a dev environment between
  Dapr-on and Dapr-off. DO NOT USE FOR: declaring a new integration event shape
  (use `add-integration-event`), Dapr service invocation / workflow / bindings /
  actors (out of scope per ADR-0014), or direct `KafkaProducer` usage (forbidden).
---

# Wiring a Dapr pub/sub topic

## Purpose

Stand up a new pub/sub topic correctly: producer side, consumer side, dev fallback,
and component YAML. The wiring contract is in
[ADR-0014](../../../docs/decisions/0014-adopt-dapr.md) and
[29-dapr-integration.md](../../../docs/architecture/29-dapr-integration.md);
this skill is the **mechanical** check-list.

## When to use

- A new integration event needs a new topic (the topic-name comes from the event).
- A second consumer module wants to subscribe to an existing topic.
- The local dev compose stack needs a new component / route.
- The `InProcessEventBus` test fixture needs a registration for a new event.

## When not to use

- Declaring the event shape itself — use
  [add-integration-event](../add-integration-event/SKILL.md).
- Dapr service invocation, workflow, bindings, actors — out of scope per ADR-0014.
- Direct `KafkaProducer` / `ConsumerBuilder` usage — forbidden by
  [20-infrastructure-stack.md](../../../docs/standards/20-infrastructure-stack.md).
- Hub-side pub/sub (lives in `learnstack-hub` repo).

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Event type | Yes | The fully-qualified C# event type (`Module.IntegrationEvents.<Name>V1`). |
| Producing module | Yes | Owns the aggregate. |
| Consuming module(s) | Yes | At least one. |
| Topic | Derived | `learnstack.{module}.{aggregate}`. Architecture test enforces. |
| Partition key | No | Aggregate id when ordering is required (rare). |

## Workflow

### Step 1: Topic naming

Format: `learnstack.{module}.{aggregate}`. Examples:

| Topic | Use |
|-------|-----|
| `learnstack.identity.user` | User created / updated / GDPR-deleted |
| `learnstack.identity.membership` | Membership grants / revocations |
| `learnstack.tenancy.tenant` | Tenant lifecycle |
| `learnstack.tenancy.organization` | Organization lifecycle |
| `learnstack.enrollment.enrollment` | Enrollment created / completed / cancelled |
| `learnstack.classroom.session` | Session opened / ended / participant joined |
| `learnstack.hub.entitlement` | Hub → core entitlement projection refresh |
| `learnstack.hub.custom-domain.activated` | Hub → core host mapping update |
| `learnstack.cache.invalidation` | Cross-instance L1 cache invalidation |

Architecture test `Dapr_PubSub_TopicNames_FollowConvention` rejects anything that
doesn't match `^learnstack\.[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*$`.

### Step 2: Dapr component YAML

The pub/sub component lives once per deployment in `infra/dapr/components/`:

```yaml
# infra/dapr/components/pubsub-kafka.yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: pubsub
spec:
  type: pubsub.kafka
  version: v1
  metadata:
    - name: brokers
      value: "kafka:9092"
    - name: consumerGroup
      value: "learnstack-api"
    - name: authType
      value: "none"
```

You **do not** declare topics in the component YAML; topics are first-class on
Kafka and created on demand by the broker config (or by an ops-time script). Adding
a new topic requires no change to this file.

If you're adding a Hub-only topic that the Hub repo subscribes to, that
subscription lives in `learnstack-hub`'s components.

### Step 3: Producer side

In the producer module's command handler (already covered by
[add-mediatr-handler](../add-mediatr-handler/SKILL.md) +
[add-integration-event](../add-integration-event/SKILL.md)):

```csharp
await outbox.EnqueueAsync(new EnrollmentCreatedIntegrationEventV1 { ... }, ct);
await db.SaveChangesAsync(ct);
```

The `OutboxProcessor` (BackgroundService) polls `outbox_messages`, calls
`IEventBus.PublishAsync(event)`, and `DaprEventBus.PublishAsync` invokes
`DaprClient.PublishEventAsync("pubsub", topic, event)`.

The topic name is derived **mechanically** from the event type's namespace +
aggregate name; you do not name it manually.

### Step 4: Consumer side — subscription

In the consumer module's startup:

```csharp
public sealed class EnrollmentModule : ILearnStackModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // ... DbContext, MediatR, etc. ...

        services.AddDaprSubscription<UserGdprDeletedIntegrationEventV1>(
            topic: "learnstack.identity.user",
            pubsubName: "pubsub");
    }
}
```

The subscription pipeline:

1. Dapr sidecar delivers HTTP POST to `/dapr/subscribe-endpoint`.
2. LearnStack's `DaprSubscriptionMiddleware` deserialises the envelope, restores
   `TenantContext` from `event.TenantId` / `event.OrganizationId`, and dispatches
   via MediatR.
3. The `IIntegrationEventHandler<T>` runs with `IInboxGuard` protection.

### Step 5: Dev fallback (`InProcessEventBus`)

When `DeploymentMode = Development`, Dapr is bypassed:

```csharp
// composition root
if (deploymentMode == DeploymentMode.Development)
{
    services.AddSingleton<IEventBus, InProcessEventBus>();
}
else
{
    services.AddSingleton<IEventBus, DaprEventBus>();
}
```

`InProcessEventBus` publishes via `IPublisher` (MediatR); subscribers register as
`INotificationHandler<TIntegrationEvent>` in addition to `IIntegrationEventHandler<T>`.
The handler code is the **same** — the bus is the only difference. New events
require no extra registration for the dev path because the handler is discovered
by assembly scan.

### Step 6: Cross-instance L1 cache invalidation

If your module has its own L1 in-memory cache (rare; prefer `ICacheService`), you
must subscribe to `learnstack.cache.invalidation`:

```csharp
services.AddDaprSubscription<CacheInvalidationEvent>(
    topic: "learnstack.cache.invalidation",
    pubsubName: "pubsub");
```

The event carries `(tenant_id, cache_key)` so the local cache evicts the right
entry. Most modules use `ICacheService` directly and skip this.

### Step 7: Ordering (rare)

If consumers require per-aggregate ordering (e.g. learner progress events on the
same enrollment must arrive in order), set the partition key on the producer side:

```csharp
await daprClient.PublishEventAsync(
    "pubsub", topic, @event,
    metadata: new Dictionary<string, string>
    {
        ["partitionKey"] = enrollment.Id.ToString(),
    });
```

Kafka guarantees ordering **within a partition**. Cross-partition ordering does
not exist; design around it.

### Step 8: Observability

Each topic gets three automatic metrics:

- `learnstack_outbox_dispatch_duration_seconds{event_type}`
- `learnstack_outbox_dispatch_failed_total{event_type}`
- `learnstack_inbox_dedup_total{module, event_type}`

No extra wiring needed. Confirm the Grafana dashboard sees the new event type
within ~5 minutes of the first published message.

## Validation

- `dotnet build` and `dotnet test` pass.
- Architecture tests:
  - `Dapr_PubSub_TopicNames_FollowConvention`.
  - `Integration_Events_Inherit_From_IntegrationEventBase`.
  - `Integration_Event_Handlers_Use_InboxGuard`.
  - `Modules_Do_Not_Inject_Kafka_Directly`.
- An integration test (Testcontainers + Dapr in dev mode) confirms the round-trip
  publish → dispatch → consume → inbox marker.
- The metric `learnstack_outbox_dispatch_duration_seconds{event_type="..."}` appears
  in Prometheus.

## Common pitfalls

- **Inventing a topic name.** The convention is `learnstack.{module}.{aggregate}`.
  Architecture test fails the build for deviations.
- **Direct `DaprClient` / `KafkaProducer` injection.** Both forbidden. Use
  `IEventBus`.
- **Subscribing in the wrong module.** A subscription declared in the producer
  module's startup runs *on the producer side*, which is almost always wrong.
- **Forgetting the dev fallback.** Tests that run with `DeploymentMode.Development`
  see no events if `IEventBus` isn't wired to `InProcessEventBus`. Confirm
  composition-root branching.
- **Ordering assumption across topics.** Kafka does not order across topics or
  cross-partition within a topic. If your design needs strict ordering, use
  partition keys and document the assumption.
- **Component YAML drift between dev and prod.** Keep one canonical YAML in
  `infra/dapr/components/`; environment overrides only via Vault-injected
  metadata fields (broker URL, auth).
