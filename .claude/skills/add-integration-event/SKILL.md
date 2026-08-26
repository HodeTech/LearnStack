---
name: add-integration-event
description: >
  Publish or consume an integration event across modules using the outbox pattern,
  `IEventBus` (Dapr pub/sub → Kafka in non-dev), and per-module inbox idempotency.
  USE FOR: declaring a new versioned `<Something>IntegrationEventVN`, wiring a
  module to publish it via `IOutbox.EnqueueAsync`, and wiring another module to
  consume it via `IIntegrationEventHandler<T>` + `IInboxGuard`. DO NOT USE FOR:
  intra-module domain events (those use plain MediatR `INotification`,
  in-process), provider webhook receivers (they have their own pattern), or
  Hub-side events (those live in the `learnstack-hub` repo).
---

# Adding an integration event

## Purpose

Move state changes between modules with at-least-once delivery and idempotent
consumers, per
[ADR-0006 Events and Outbox](../../../docs/decisions/0006-events-and-outbox.md) +
Amendment 1 (Dapr pub/sub dispatch),
[ADR-0010 Cross-Module Communication](../../../docs/decisions/0010-cross-module-communication.md),
and [15-event-and-outbox.md](../../../docs/architecture/15-event-and-outbox.md).

## When to use

- Module A's state change must influence module B (Billing → Enrollment,
  Classroom → Analytics, Identity → Audit).
- A read-model projection elsewhere needs to refresh.
- The Hub publishes a `learnstack.hub.entitlement` (or similar) event that
  LearnStack core needs to react to.

## When not to use

- Two aggregates in the **same** module need to coordinate — use MediatR
  `INotification` (a domain event) and handle it in-process inside that module.
- The consumer can fetch the data on-demand from the producer's read API.
- Provider webhooks (Stripe, LiveKit) — they enter via a webhook receiver that
  writes to the outbox itself, not via this skill.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Event name | Yes | `<Verb><Aggregate>IntegrationEventV<N>`, PascalCase + version suffix. |
| Producing module | Yes | Owns the aggregate the event describes. |
| Consuming module(s) | Yes | At least one; can be many. |
| Topic | Derived | `learnstack.{module}.{aggregate}` (e.g. `learnstack.enrollment.enrollment`). |
| Schema fields | Yes | `IntegrationEventBase` supplies `EventId`, `OccurredAt`, `TenantId` (all `required`) and demands a `PartitionKey` override. Everything else is yours to declare. |

## Workflow

### Step 1: Define the event record

In `<Producer>.Application.Contracts/IntegrationEvents/<EventName>.cs`:

```csharp
public sealed record EnrollmentCreatedIntegrationEventV1 : IntegrationEventBase
{
    public required Guid EnrollmentId { get; init; }
    public required Guid LearnerId { get; init; }
    public required Guid CourseVersionId { get; init; }
    public Guid? CohortId { get; init; }
    public required string Source { get; init; }  // "manual" | "billing" | "invitation"

    // Both abstract on IntegrationEventBase, so this record does not compile
    // without them — deliberately, because each is a property of the event TYPE
    // and a value with two sources is a value that can disagree with itself.
    public override string Topic => "learnstack.enrollment.enrollment";

    // Ordering is guaranteed per partition key and nowhere else, and the
    // aggregate this event is about is the ordering domain. Keying on TenantId
    // instead would serialise the tenant's whole stream onto one partition — a
    // real throughput cost, and one worth taking deliberately rather than by
    // inheriting a default.
    public override string PartitionKey => EnrollmentId.ToString();
}
```

`IntegrationEventBase` (`LearnStack.SharedKernel.Messaging`) supplies exactly
five members, and every one of them is mandatory:
- `EventId` — `required`; identity for consumer-side deduplication
- `OccurredAt` — `required`; from `IClock`, never `DateTime.UtcNow`
- `TenantId` — `required`; what the transport restores before your handler runs
- `Topic` — `abstract`; the channel, `learnstack.{module}.{aggregate}`, checked by
  `Integration_Event_TopicNames_FollowConvention`
- `PartitionKey` — `abstract`; the ordering domain, declared by each event

It supplies **no** `OrganizationId`, `CorrelationId`, `CausationId` or
`ActorUserId`. Correlation travels with the ambient context rather than on the
payload, and is asserted on the outbox row by
`Outbox_Row_Carries_Correlation_Context`. If your consumer genuinely needs the
organization or the acting user, declare them on your own record — but read the
note under Step 4 first, because the consumer's restored context will not carry
them.

Versioning: a breaking change ships a **new** record (`V2`). The `V1` stays
supported during the migration window.

### Step 2: Producer — enqueue inside the handler transaction

In the producer's command handler (see
[add-mediatr-handler](../add-mediatr-handler/SKILL.md)):

```csharp
await outbox.EnqueueAsync(new EnrollmentCreatedIntegrationEventV1
{
    EventId = guidFactory.NewUuidV7(),        // IGuidFactory, not Guid.NewGuid
    OccurredAt = clock.UtcNow,                // IClock per Standards 02 § Time
    TenantId = tenantContext.TenantId,
    EnrollmentId = enrollment.Id.Value,
    LearnerId = request.LearnerId.Value,
    CourseVersionId = request.CourseVersionId.Value,
    CohortId = request.CohortId?.Value,
    Source = request.Source.ToString().ToLowerInvariant(),
}, cancellationToken);

await db.SaveChangesAsync(cancellationToken);   // atomic
```

Rules:

- `IOutbox.EnqueueAsync` enrolls in the **ambient** `DbContext`. Do not open a new
  transaction.
- `SaveChangesAsync` commits the aggregate change and the outbox row together.

### Step 3: Topic mapping

The outbox processor derives the topic from the event type using:

```text
learnstack.{module}.{aggregate}
```

`{module}` is the producing module name (`enrollment`, `classroom`, …);
`{aggregate}` is the aggregate name (`enrollment`, `session`, …). Examples:

- `learnstack.identity.user`
- `learnstack.enrollment.enrollment`
- `learnstack.classroom.session`
- `learnstack.hub.entitlement` (Hub side)

The architecture test `Integration_Event_TopicNames_FollowConvention` enforces the
pattern; deviation fails the build.

### Step 4: Consumer — handler + inbox guard

In `<Consumer>.Application/IntegrationEvents/<HandlerName>.cs`:

```csharp
public sealed class CreateAuditEntryOnEnrollmentCreated(
    IInboxGuard inbox,
    AuditDbContext db,
    ILogger<CreateAuditEntryOnEnrollmentCreated> logger)
    : IIntegrationEventHandler<EnrollmentCreatedIntegrationEventV1>
{
    public async Task HandleAsync(
        EnrollmentCreatedIntegrationEventV1 @event, CancellationToken ct)
    {
        if (await inbox.IsAlreadyProcessedAsync(@event.EventId, ct))
        {
            logger.LogDebug("Duplicate event {EventId} skipped", @event.EventId);
            return;
        }

        // ... business logic ...
        var entry = AuditEntry.FromIntegrationEvent(@event);
        db.AuditEntries.Add(entry);

        inbox.MarkAsProcessed(@event.EventId, @event.GetType().Name);
        await db.SaveChangesAsync(ct);   // atomic business write + inbox marker
    }
}
```

Rules:

- **Every** `IIntegrationEventHandler` invokes `IInboxGuard.IsAlreadyProcessedAsync`
  before any business logic. Architecture test
  `Integration_Event_Handlers_Use_InboxGuard` enforces this.
- `MarkAsProcessed` enrolls in the same `DbContext`; the inbox marker and the
  business write commit atomically.
- The **transport** — not middleware — restores tenant context from
  `@event.TenantId` before your handler runs, and puts the publisher's own back
  afterwards. Read it through `ITenantContext` as usual; don't read it from
  anywhere else.
- **Organization comes from the envelope, and it has to.** An earlier version of
  this skill said it was deliberately not restored, reasoning that inventing an
  organization scope would narrow queries the producer never narrowed. Under the
  canonical Row Level Security policy the reasoning inverts: with
  `app.organization_id` unset, an organization-scoped row evaluates
  `false OR NULL OR NULL`, and a NULL policy result is false — so an absent
  organization *hides* every organization-scoped row and `WITH CHECK` rejects
  writing one. Widening is the `app.scope = 'tenant'` hatch, not an absent value.
- **The actor is `UserId.SystemActor`** unless the envelope names one.
  `AuditableEntity.MarkCreated` refuses `default(UserId)`, so without it your
  consumer cannot create an aggregate at all.
- **Your handler's constructor must do nothing but assign fields.** Each handler
  gets its own DI scope and the container materialises the whole handler array
  per scope, so a constructor runs several times per delivery. A constructor that
  opens a connection, emits a metric or writes a log line will do it more than
  once. A constructor that *throws* denies the event to every other module's
  handler — construction happens before per-handler isolation can start.

### Step 5: Subscription registration

The Dapr subscription is declared in the consumer module's startup:

```csharp
services.AddDaprSubscription<EnrollmentCreatedIntegrationEventV1>(
    topic: "learnstack.enrollment.enrollment",
    pubsubName: "pubsub");
```

In Development mode (`DeploymentMode = Development`) the `InProcessEventBus`
replaces Dapr; the same `IIntegrationEventHandler<T>` is invoked by MediatR
in-process. The handler code is **the same** across modes.

### Step 6: Tests

Two tests minimum:

1. **Producer test** — exercise the handler, assert an `outbox_messages` row exists
   with the right `type`, `topic`, `payload`.
2. **Consumer test** — feed the event into the handler twice; assert the business
   write happens exactly once (idempotency via `IInboxGuard`).

## Validation

- `dotnet build` and `dotnet test` pass.
- `LearnStack.Tests.Architecture` is green; specifically
  `Integration_Events_Inherit_From_IntegrationEventBase`,
  `Integration_Event_Handlers_Use_InboxGuard`,
  `Integration_Event_TopicNames_FollowConvention`.
- An integration test confirms the round-trip: handler publishes → outbox row
  created → outbox processor dispatches → consumer handles + writes business
  state + inbox row.
- Sending the same event twice writes the consumer's business state exactly once.

## Common pitfalls

- **Crossing modules with a domain event.** Domain events stay inside the module
  that owns the aggregate. The moment another module needs to react, it must be an
  integration event.
- **Forgetting `IInboxGuard`.** The architecture test catches it, but the symptom
  in production (Kafka redelivery → duplicate work) is silent until then.
- **Writing the outbox row in a separate transaction.** Use `IOutbox.EnqueueAsync`
  inside the ambient `DbContext`; never `new TransactionScope`.
- **Hand-rolling the topic name.** The convention is mechanical; never invent.
- **Bumping the schema without versioning.** A breaking change ships a `V2` record;
  `V1` stays supported. Architecture test `Integration_Events_Are_Versioned`
  rejects inline edits to a published event shape.
- **Tenant context missing on the consumer side.** The Dapr-side middleware sets
  it from `@event.TenantId`; if you build a custom subscriber, you must replicate
  that or you'll write rows with no tenant.
