---
name: add-integration-event
description: >
  Publish or consume an integration event across modules using the outbox pattern,
  `IEventBus` (`InProcessEventBus` today; Dapr pub/sub → Kafka on the Phase 11
  trigger), and shared transport-owned inbox idempotency.
  USE FOR: declaring a new versioned `<Something>IntegrationEventVN`, wiring a
  module to publish it via `IOutbox.EnqueueAsync`, and wiring another module to
  consume it via `IIntegrationEventHandler<T>` under the transport-owned inbox
  boundary. DO NOT USE FOR:
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
Accepted [ADR-0046](../../../docs/decisions/0046-durable-event-delivery-and-recovery.md)
owns dispatcher recovery, subscription readiness and the shared inbox contract.
P02b-2 implements producer dispatch; P02b-3 still owns durable acceptance,
independent execution, canonical inbox/ledger DDL and real subscriptions.

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
    // identifier derivation; ADR-0046 bounds eligible selection, not publish order.
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
  event does not erase an announced organization. ADR-0046 preserves that envelope
  organization during delivery; a non-marked type never widens its scope.
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

### Step 4: Consumer — transport-owned inbox boundary

**Accepted target, pending P02b-3 implementation.** The transport owns the shared
`inbox_messages` guard; business handlers do not inject or call `IInboxGuard`.
Deduplication is `(tenant_id, subscription_id, event_id)`, with a stable versioned
subscription ID independent of handler type names. The separate delivery ledger
owns attempts, leases, terminal state and targeted replay.

Before constructing a handler, the executor opens an app-role unit of work,
announces envelope tenant and organization, and acquires the uncommitted inbox
claim. Completed duplicates skip construction. The completed inbox marker,
business changes, produced outbox, MUST audit and fenced ledger completion commit
on that one transaction; rollback removes a new claim. A replay never resets a
completed inbox row or republishes to healthy siblings.

The effective actor is `UserId.SystemActor`; an envelope's human actor remains
separate trusted causal metadata. Envelope organization is preserved even for a
non-marked event type. Restore context before resolving any handler, store or
module `DbContext`, and restore the caller's context afterwards.

A handler constructor only assigns fields. Construction, execution and disposal
failures belong to that subscription's own retry budget. Timeouts poison the
transaction owner; late work cannot commit success. An uncertain physical COMMIT
is Indeterminate and recovers from atomic ledger/inbox state. Ordinary app-role
delivery refuses privileged erasure work; Phase 03 owns that separate audited
boundary. External effects need idempotency keyed by subscription/event, or an
outbox intent; database rollback alone cannot undo them.

The first real flow has two independent Customization subscriptions:
`customization.initialize-content-type-default.v1` installs `card@1` content type;
`customization.initialize-taxonomy-default.v1` installs `plain@1` taxonomy. Both
remain Pending until P02b-3 supplies durable acceptance, create-only preservation,
per-effect MUST audit and the semantic key/version Seeder handoff. Neither may
suppress its sibling or overwrite authored, retired or deleted definitions.

### Step 5: Subscription registration

The current baseline exposes the consumer assembly to the composition root so the
construction-free registry discovers and registers its concrete handlers. This
abridged assembly-registration example uses the shipped public module marker;
retain the other module assemblies in the actual composition root:

```csharp
builder.AddLearnStackCrossCuttingFoundation(
    deploymentMode,
    typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly);
```

Today `InProcessEventBus` resolves the concrete
`IIntegrationEventHandler<T>` directly from DI in that subscription's async
scope. MediatR is not involved. There is no shipped
`AddDaprSubscription<T>` helper; do not invent one. P02b-2's construction-free
manifest implements stable subscription IDs, compatible
type aliases and required Pending/Active readiness. Pending holds the producer row
before acquisition or payload validation. Only explicit intentional absence may
acknowledge a known type without publication. P02b-3 makes Active publication mean
durable acceptance of all matching deliveries, independently of business handling.
Do not invent a registration API before its implementation ships. Phase 11 must
preserve these contracts when it adds Dapr.

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
2. **Consumer test, P02b-3** — deliver duplicates through the real transport and
   ledger, including concurrent duplicates and sibling subscriptions. Assert one
   completed inbox/business effect per subscription/event, atomic rollback,
   independent retry budgets and recovery after an uncertain final-claim COMMIT.

## Validation

- `dotnet build` and `dotnet test` pass.
- `LearnStack.Tests.Architecture` is green — `Integration_Event_TopicNames_FollowConvention`
  and `Integration_Events_Inherit_From_IntegrationEventBase` run today.
  `Integration_Event_Handlers_Use_InboxGuard` remains Registered for P02b-3's
  transport-owned consumer delivery. Check
  [the catalogue](../../../docs/standards/21-architecture-tests-catalogue.md) for their
  status rather than assuming a net is under you.
- P02b-2 proves producer recovery and terminal audit independently of Pending
  subscriptions. P02b-3 proves the round-trip through durable acceptance and the
  shared inbox; do not claim that proof before those components ship.
- P02b-3 duplicate delivery yields one committed business effect per subscription.

## Common pitfalls

- **Crossing modules with a domain event.** Domain events stay inside the module
  that owns the aggregate. The moment another module needs to react, it must be an
  integration event.
- **Moving the inbox guard into a handler.** The transport must claim before
  construction. The rule remains Registered until P02b-3; its name alone proves
  no enforcement. Redelivery must preserve independent sibling effects.
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
