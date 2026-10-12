---
name: add-integration-event
description: >
  Publish or consume an integration event across modules using the outbox pattern,
  `IEventBus` (`InProcessEventBus` today; Dapr pub/sub → Kafka on the Phase 11
  trigger), and per-module inbox idempotency.
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
The binding port and envelope contract is
[ADR-0038](../../../docs/decisions/0038-cross-cutting-port-and-event-contracts.md).
Accepted [ADR-0057](../../../docs/decisions/0057-transactional-event-production-and-completion.md)
owns immediate producer admission, completion and payload policy. P02b-1
Steps 2/3 implement immediate enqueue, payload admission, successful-save
collection and the completed-provisioning publisher. Step 4 implements the
production censuses and catalogue enforcement; the
[delivery record](../../../docs/roadmap/phase-02b-events-auth.md#p02b-1-step-4--producer-enforcement-and-closeout-2026-10-12)
owns final verification and review status.
P02b-2/P02b-3 retain dispatcher/inbox decisions.

## When to use

- Module A's state change must influence module B (Billing → Enrollment,
  Classroom → Analytics, Identity → Audit).
- A read-model projection elsewhere needs to refresh.

## When not to use

- A domain fact needs only in-module translation to outbox — use MediatR
  `INotification` inside the owner transaction. Domain reactions cannot mutate
  roots or coordinate additional writes; substantive reactions belong in an
  integration consumer under ADR-0057.
- The consumer can fetch the data on-demand from the producer's read API.
- Provider webhooks (Stripe, LiveKit) — they enter via a webhook receiver that
  writes to the outbox itself, not via this skill.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Event name | Yes | `<Aggregate><PastTenseFact>IntegrationEventV<N>`, PascalCase + version suffix. |
| Producing module | Yes | Owns the aggregate the event describes. |
| Consuming module(s) | Yes | At least one; can be many. |
| Topic | Declared | The event's `Topic` override, normally `learnstack.{module}.{aggregate}`. |
| Schema fields | Yes | `IntegrationEventBase` supplies `EventId`, `OccurredAt`, `TenantId` (all `required`) and demands `Topic` and `PartitionKey` overrides. Additional fields are GUIDs or approved strongly-typed identifiers, optionally nullable; no text, DTOs, collections, binary/raw JSON or serializer overrides. |

## Workflow

### Step 1: Define the event record

In `<Producer>.Application.Contracts/IntegrationEvents/<EventName>.cs`:

```csharp
[IntegrationEventPartition(nameof(EnrollmentId))]
public sealed record EnrollmentCreatedIntegrationEventV1 : IntegrationEventBase
{
    public required Guid EnrollmentId { get; init; }
    public required Guid LearnerId { get; init; }
    public required Guid CourseVersionId { get; init; }
    public Guid? CohortId { get; init; }

    // Both abstract on IntegrationEventBase, so this record does not compile
    // without them — deliberately, because each is a property of the event TYPE
    // and a value with two sources is a value that can disagree with itself.
    public override string Topic => "learnstack.enrollment.enrollment";

    // Declare EnrollmentId as the partition source. ADR-0057 accepts canonical
    // identifier derivation; G3 durable publish ordering remains P02b-2.
    public override string PartitionKey => EnrollmentId.ToString("D");
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

The concrete record declares `[IntegrationEventPartition(nameof(EnrollmentId))]`.
Use `[IntegrationEventPartition(nameof(TenantId), tenantWide: true)]` only for an
explicitly tenant-wide fact. The named non-nullable source must match the actual
canonical lowercase `D` GUID key; inheriting a declaration is insufficient.
Approved typed payload identifiers are currently `TenantId`, `OrganizationId`
and `UserId`; module-local identifiers cross the contract as `Guid`.

It supplies **no** `OrganizationId`, `CorrelationId`, `CausationId` or
`ActorUserId`: those describe the delivery and travel on
`IntegrationEventEnvelope`, copied from the outbox row. An organization-owned
event implements `IOrganizationScopedIntegrationEvent`; envelope construction
then rejects a missing or empty organization id. Do not duplicate delivery
metadata on the event payload.

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
    TenantId = tenantContext.TenantId.Value,   // the envelope carries a Guid
    EnrollmentId = enrollment.Id.Value,
    LearnerId = request.LearnerId.Value,
    // Three types, two of them the same. The contract carries a module-local id as a
    // `Guid` (add-mediatr-handler § Step 1) and this event declares one, so the request's
    // value assigns directly — `request.CourseVersionId.Value` does not compile, because a
    // `Guid` has no `Value`. The handler's own `courseVersionId` is the TYPED id it built
    // one layer in, so that spelling needs `courseVersionId.Value`.
    CourseVersionId = request.CourseVersionId,
    CohortId = request.CohortId,
}, cancellationToken);

await db.SaveChangesAsync(cancellationToken);   // flush; owner commits state + outbox + audit
```

Rules:

- `IOutbox.EnqueueAsync` validates and INSERTs immediately through the enrolled,
  announced, writable ambient `IUnitOfWork.Connection`/`Transaction`. It opens
  no connection, commits nothing and never calls `IEventBus`. The frame compares
  the live announcement generation and event tenant, including unresolved
  provisioning; caller snapshots are not authority. Raw transactions cannot
  bypass producer enrollment. `id = EventId`; duplicates fail.
- Serialize the concrete runtime event through the existing frozen
  `IntegrationEventBase.PayloadJsonOptions`. The complete canonical UTF-8 input
  fits `OutboxPayloadLimits.MaxBytes` (262,144 bytes inclusive) before INSERT;
  multibyte accounting is bytes, not characters. JSON `metadata` is SQL NULL;
  `IOutbox` accepts no arbitrary metadata. No DDL size CHECK is claimed.
- Tenant/organization come from the transaction announcement; correlation,
  causation and optional causal actor come from trusted execution. That boundary
  guarantees W3C Activity without a listener. Domain translation mints a new
  integration EventId and uses its domain EventId as causation. A non-marked
  event does not erase an announced organization; G1 delivery scope stays open.
- The owner drains successful-save events FIFO before invariants/MUST audit and
  commit, at most 256 per transaction. Domain handlers only translate to outbox:
  no mutation/write stores, nested sends, further raises, publication or providers.
  Successful enqueue/promotion leaves sticky receipts in every ancestor;
  `Result.Fail` with such work poisons the unit before frame resolution, even if
  absorbed. Event-free refusals retain ADR-0040 semantics. Terminal paths discard
  source state; joiners neither drain nor clear the owner.
- `SaveChangesAsync` **flushes** the context; it does not commit. `TransactionBehavior` owns
  the commit boundary, so the aggregate change and the outbox row land together because they
  share that transaction — not because one `SaveChanges` wrote both.

### Step 3: Topic declaration

The concrete event declares the topic once using:

```text
learnstack.{module}.{aggregate}
```

`{module}` is the producing module name (`enrollment`, `classroom`, …);
`{aggregate}` is the aggregate name (`enrollment`, `session`, …). Examples:

- `learnstack.identity.user`
- `learnstack.enrollment.enrollment`
- `learnstack.classroom.session`
- `learnstack.hub.entitlement` (Hub side)

The outbox copies that declared value to persistence and the envelope forwards it;
neither derives a second answer. The architecture test
`Integration_Event_TopicNames_FollowConvention` enforces the pattern; deviation
fails the build.

### Step 4: Consumer — handler + inbox guard

**Inherited target, pending P02b-2 G1 and P02b-3 delivery implementation.** This
example does not decide inbox ownership, deduplication identity or delivery scope.
The accepted first flow uses independent Customization `card@1`/`plain@1`
subscriptions for `TenantCreatedIntegrationEventV1`, not an Audit mirror. G1 must
prevent one subscription from suppressing its sibling before those handlers ship.
P02b-3 owns create-only preservation, per-effect MUST audit and the semantic
key/version Seeder handoff described in ADR-0057.

In `<Consumer>.Application/IntegrationEvents/<HandlerName>.cs`:

```csharp
public sealed class QueueWelcomeNotificationOnEnrollmentCreated(
    IInboxGuard inbox,
    NotificationsDbContext db,
    ILogger<QueueWelcomeNotificationOnEnrollmentCreated> logger)
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
        var dispatch = NotificationDispatch.QueueWelcome(
            @event.LearnerId, @event.EnrollmentId);
        db.NotificationDispatches.Add(dispatch);

        inbox.MarkAsProcessed(@event.EventId, @event.GetType().Name);
        await db.SaveChangesAsync(ct);   // atomic business write + inbox marker
    }
}
```

Rules:

- Under the inherited consumer target, **every** `IIntegrationEventHandler` invokes
  `IInboxGuard.IsAlreadyProcessedAsync`
  before any business logic. The architecture test
  `Integration_Event_Handlers_Use_InboxGuard` holds every handler to it from
  [Phase 02b](../../../docs/roadmap/phase-02b-events-auth.md), which ships the guard; it
  is registered, not running, until then.
- `MarkAsProcessed` writes on the same **transaction** as the business write — the ambient
  one the transport opens per delivery through `IUnitOfWork`
  ([ADR-0040](../../../docs/decisions/0040-ambient-unit-of-work.md)) — so the inbox marker
  and the business write commit atomically.
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
- **The effective actor is always `UserId.SystemActor`.** A human named by the
  envelope remains separate causal audit metadata (`CausalActorUserId`); an
  asynchronous consumer never impersonates that human.
- **Your handler's constructor must do nothing but assign fields.** Each
  subscription gets its own async DI scope and exactly one handler construction.
  Constructor, handler and disposal failures are contained to that subscription,
  so healthy siblings still run.

### Step 5: Subscription registration

Today, expose the consumer assembly to the composition root so the
construction-free registry discovers and registers its concrete handlers:

```csharp
builder.AddLearnStackCrossCuttingFoundation(
    deploymentMode,
    typeof(QueueWelcomeNotificationOnEnrollmentCreated).Assembly);
```

Today `InProcessEventBus` resolves the concrete
`IIntegrationEventHandler<T>` directly from DI in that subscription's async
scope. MediatR is not involved. There is no shipped
`AddDaprSubscription<T>` helper; do not invent one. Phase 11's Dapr adapter
invokes the same event-declared topic and handler contract, so the handler code
remains identical across transports.

### Step 6: Tests

Follow the canonical producer rules and remaining consumer obligations in
[Standards 21](../../../docs/standards/21-architecture-tests-catalogue.md#events-jobs-and-correlation):
announced transaction/admission, sticky failure receipts, owner completion,
outbox-only handlers and identifier-only payloads; extend existing partition and
row-context proofs. Use real production subjects and planted violations, including
no-listener trace context, failed-save attribution, absorbed descendant refusal,
byte boundaries, SQL NULL metadata and terminal reuse. For delivered flows:

1. **Producer test** — exercise the handler, assert an `outbox_messages` row exists
   with the right `type`, `topic`, `payload`.
2. **Consumer test** — feed the event into the handler twice; assert the business
   write happens exactly once (idempotency via `IInboxGuard`).

## Validation

- `dotnet build` and `dotnet test` pass.
- `LearnStack.Tests.Architecture` is green — `Integration_Event_TopicNames_FollowConvention`
  and `Integration_Events_Inherit_From_IntegrationEventBase` run today.
  `Integration_Event_Handlers_Use_InboxGuard` remains Registered for P02b-3's
  consumer delivery after G1. Check
  [the catalogue](../../../docs/standards/21-architecture-tests-catalogue.md) for their
  status rather than assuming a net is under you.
- An integration test confirms the round-trip: handler enqueues → outbox row
  created → outbox processor dispatches → consumer handles + writes business
  state + inbox row.
- Sending the same event twice writes the consumer's business state exactly once.

## Common pitfalls

- **Crossing modules with a domain event.** Domain events stay inside the module
  that owns the aggregate. The moment another module needs to react, it must be an
  integration event.
- **Forgetting `IInboxGuard`.** The consumer rule remains Registered until
  P02b-3; do not infer enforcement from its name. Consumer delivery must prove
  redelivery cannot duplicate its business effect.
- **Writing the outbox row in a separate transaction.** Use `IOutbox.EnqueueAsync`
  through the enrolled ambient `IUnitOfWork`; never `new TransactionScope`.
- **Supplying a second topic at enqueue or subscription time.** The concrete
  event's `Topic` override is the one source; persistence, envelope and transport
  forward it unchanged.
- **Bumping the schema without versioning.** A breaking change ships a `V2` record;
  `V1` stays supported during its compatibility window.
- **Tenant context missing on the consumer side.** The registered transport sets
  it from the event and envelope before handler lookup; a future adapter must
  preserve that timing or handlers can resolve the publisher/unresolved tenant.
