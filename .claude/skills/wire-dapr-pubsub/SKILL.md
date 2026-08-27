---
name: wire-dapr-pubsub
description: >
  Wire transport-independent subscription metadata for a cross-module
  integration event and, only after Phase 11's trigger fires, its Dapr pub/sub
  adapter, with the
  `learnstack.{module}.{aggregate}` topic-name convention, Kafka as the broker, and
  `InProcessEventBus` default. USE FOR: registering a consumer of a declared topic
  or implementing/verifying the Phase 11 Dapr adapter after its trigger fires.
  DO NOT USE FOR: declaring a new integration event shape
  (use `add-integration-event`), Dapr service invocation / workflow / bindings /
  actors (out of scope per ADR-0038), or direct `KafkaProducer` usage (forbidden).
---

# Wiring a Dapr pub/sub topic

## Purpose

Register a new consumer against the current transport-independent contract. If
ADR-0035's Phase 11 trigger has fired, also wire the Dapr adapter without changing
that contract. The wiring contract is in
[ADR-0038](../../../docs/decisions/0038-cross-cutting-port-and-event-contracts.md) and
[29-dapr-integration.md](../../../docs/architecture/29-dapr-integration.md);
this skill is the **mechanical** check-list.

## When to use

- A new integration event needs a new topic (the topic-name comes from the event).
- A second consumer module wants to subscribe to an existing topic.
- The `InProcessEventBus` test fixture needs a registration for a new event.
- Phase 11's Dapr pub/sub trigger has fired and its adapter is being implemented
  or extended.

## When not to use

- Declaring the event shape itself — use
  [add-integration-event](../add-integration-event/SKILL.md).
- Dapr service invocation, workflow, bindings, actors — out of scope per ADR-0038.
- Direct `KafkaProducer` / `ConsumerBuilder` usage — forbidden by
  [20-infrastructure-stack.md](../../../docs/standards/20-infrastructure-stack.md).
- Hub-side pub/sub (lives in `learnstack-hub` repo).

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Event type | Yes | The fully-qualified C# event type (`Module.IntegrationEvents.<Name>V1`). |
| Producing module | Yes | Owns the aggregate. |
| Consuming module(s) | Yes | At least one. |
| Topic | Declared | The event's `Topic` override; normally `learnstack.{module}.{aggregate}`. |
| Partition key | Declared | The event's `PartitionKey` override; normally the aggregate id. |

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

Architecture test `Integration_Event_TopicNames_FollowConvention`
(`CrossCuttingFoundationTests`) is the source of truth for the pattern, and this skill
deliberately does **not** restate it. A copy of the regex lived here and had already
drifted: it collapsed the two shapes into one optional trailing group, so it accepted
`learnstack.identity.user.created` — a four-segment core topic the test rejects. Two
things to know, and the test for the rest:

- LearnStack-core topics are **three** segments: `learnstack.{module}.{aggregate}`.
- A **fourth** segment is accepted only when the second is `hub`.

The fourth segment exists for **Hub-side event-name suffixes**
(`learnstack.hub.custom-domain.activated`, `learnstack.hub.custom-domain.deactivated`,
`learnstack.hub.custom-domain.revoked`). LearnStack-core topics stay 3-segment
(`learnstack.{module}.{aggregate}`); the 4-segment shape is reserved for the Hub-side
naming exception. Treat the architecture test as the source of truth; keep this
skill's regex aligned with it.

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

The `OutboxProcessor` (BackgroundService) polls `outbox_messages`, constructs an
`IntegrationEventEnvelope`, and calls `IEventBus.PublishAsync(envelope)`.
`DaprEventBus.PublishAsync` invokes `DaprClient.PublishEventAsync` with
`envelope.Topic`, `envelope.Event`, and `envelope.PartitionKey` metadata.

The topic is declared by the event's `Topic` override and checked by the
architecture test; the transport never re-derives or renames it.

### Step 4: Consumer side — current subscription metadata

`InProcessEventBus` discovers handler types from assemblies passed at the
composition root. Ensure the consumer assembly is included and let the registry
register the concrete handler type:

```csharp
builder.AddLearnStackCrossCuttingFoundation(
    deploymentMode,
    typeof(CreateAuditEntryOnUserDeleted).Assembly);
```

There is no shipped `AddDaprSubscription<T>` helper or `[Topic]` attribute. Do
not invent one. The event declares its topic and the construction-free handler
registry supplies the current subscription metadata.

The future Phase 11 subscription pipeline must preserve this behavior:

1. The sidecar **discovers** subscriptions with `GET /dapr/subscribe`, which returns
   the topic-to-route table; it then **delivers** each event with an HTTP `POST` to the
   route that table named. They are two different calls, and there is no endpoint
   called `/dapr/subscribe-endpoint`.
2. LearnStack's Phase 11 Dapr adapter deserialises the envelope, restores
   `TenantContext` from the event and envelope, and resolves the matching
   `IIntegrationEventHandler<T>` directly from DI.
3. The `IIntegrationEventHandler<T>` runs with `IInboxGuard` protection.

### Step 5: Current transport (`InProcessEventBus`)

Every deployment-mode value currently resolves `InProcessEventBus`. Do not add a
mode switch to a nonexistent adapter. After ADR-0035's trigger fires, Phase 11
changes this single composition-root selection site:

```csharp
// Current: SelectEventBus(...) returns InProcessEventBus for every mode.
// Phase 11: select DaprEventBus only for the deployment(s) whose trigger fired.
```

`InProcessEventBus` reads construction-free subscription metadata, creates one
async DI scope per subscription, and resolves exactly that subscription's
concrete `IIntegrationEventHandler<T>`. MediatR is not involved: there is no
`IPublisher` or `INotificationHandler`, and registering a second interface is
precisely the mistake the single consumer contract exists to prevent.

The handler code is the **same** on both transports; the bus is the only
difference. Implement the handler once as
`IIntegrationEventHandler<TIntegrationEvent>` and expose its assembly to the
registry as shown in Step 4.

### Step 6: Cross-instance L1 cache invalidation

Cross-instance invalidation is Phase 11 work, because it requires more than one
process. Do not add a current `AddDaprSubscription` call. When the trigger fires,
the adapter-owned `learnstack.cache.invalidation` consumer may evict one exact
tenant-qualified key; generation keys remain the mechanism for set invalidation.

### Step 7: Ordering (rare)

Every event declares its ordering domain by overriding `PartitionKey` — normally
the aggregate id. Never pass a second key while enqueuing and never call
`DaprClient` directly. `IOutbox.EnqueueAsync` copies the declared key to the row;
the processor reconstructs the envelope and the Dapr adapter forwards the same
value as publish metadata:

```csharp
public override string PartitionKey => EnrollmentId.ToString();
```

In Phase 11, `DaprEventBus.PublishAsync` translates this into the equivalent Dapr
metadata (`partitionKey`) on the `PublishEventAsync` call:

```csharp
// LearnStack.Infrastructure.Messaging.DaprEventBus (Infrastructure only — never
// call DaprClient from a module).
await daprClient.PublishEventAsync(
    "pubsub", envelope.Topic, envelope.Event,
    metadata: new Dictionary<string, string>
    {
        ["partitionKey"] = envelope.PartitionKey,
    });
```

Kafka guarantees ordering **within a partition**. Cross-partition ordering does
not exist; design around it.

### Step 8: Observability

Phase 11 must add and verify the transport metrics governed by the observability
standard; they are not automatic in the current in-process implementation:

- `learnstack_outbox_dispatch_duration_seconds{event_type}`
- `learnstack_outbox_dispatch_failed_total{event_type}`
- `learnstack_inbox_dedup_total{module, event_type}`

Keep tags low-cardinality and confirm the names against the implementation and
dashboard when the adapter lands.

## Validation

- `dotnet build` and `dotnet test` pass.
- Current architecture tests
  `Integration_Event_TopicNames_FollowConvention` and
  `Modules_Do_Not_Inject_IEventBus_Directly` pass.
- When doing Phase 02b consumer work, add/keep its registered inheritance and
  inbox-guard rules. When doing Phase 11 adapter work, add/keep the Dapr binding
  and direct-provider boundary rules owned by that phase.
- Today, an in-process integration test confirms publish → dispatch → consume →
  inbox marker and the current transport's consumer span.
- When Phase 11's adapter exists, its Dapr/Testcontainers binding test and
  transport metrics pass as additional validation.

## Common pitfalls

- **Inventing a topic name.** The convention is `learnstack.{module}.{aggregate}`
  for LearnStack-core topics (3 segments). Hub-side events may add a 4th
  event-name segment (`learnstack.hub.custom-domain.activated`). Architecture
  test `Integration_Event_TopicNames_FollowConvention` rejects anything else today;
  Phase 11's Dapr binding test checks the component copy too.
- **Direct `DaprClient` / `KafkaProducer` injection.** Both forbidden. Use
  `IEventBus`.
- **Subscribing in the wrong module.** A subscription declared in the producer
  module's startup runs *on the producer side*, which is almost always wrong.
- **Switching on deployment mode before the trigger.** All modes use
  `InProcessEventBus` today. Add the Dapr branch only with the Phase 11 adapter
  and its integration suite.
- **Ordering assumption across topics.** Kafka does not order across topics or
  cross-partition within a topic. If your design needs strict ordering, use
  partition keys and document the assumption.
- **Component YAML drift between dev and prod.** Keep one canonical YAML in
  `infra/dapr/components/`; environment overrides only via Vault-injected
  metadata fields (broker URL, auth).
