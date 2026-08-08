# Events and Outbox

Events allow modules to collaborate without cross-module database coupling. This document
defines the event types, the outbox pattern, the claim protocol that makes concurrent
dispatch safe, and how dispatch reaches subscribers — in process today, through Dapr
pub/sub to Kafka when the trigger for that adapter fires (ADR-0010 Amendment 1 +
ADR-0014 + ADR-0035).

## Decision

Use **domain events** within a module (in-process MediatR notifications, same transaction
as the aggregate change) and **integration events** across modules (written to an outbox
in the same transaction; dispatched at-least-once to subscribers with retry / backoff /
dead-lettering on both the producer and the subscriber side).

The outbox table is **LearnStack-owned** (a single shared table in PostgreSQL); the event
bus is the **dispatch transport**, not the durable buffer. That ownership split is what
makes the transport swappable without touching a single producer or consumer.

## What ships when

Three separable things are often collapsed into one; they are not the same and they do
not land together.

| Piece | Owning phase | State |
|---|---|---|
| `outbox_messages` table, its schema, and its LearnStack ownership | [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) | Ships even though nothing dispatches from it yet — the schema and its ownership are a one-way door |
| `IEventBus` port + `InProcessEventBus` | [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md) | The only registered implementation |
| `OutboxProcessor`, `IInboxGuard`, the claim protocol, the first real integration event | [Phase 02b](../roadmap/phase-02b-events-auth.md) | The durable dispatcher |
| `DaprEventBus` → Dapr pub/sub → Kafka | [Phase 11](../roadmap/phase-11-production-hardening.md) | Demand-gated; trigger: a second process needs to consume an integration event, or event volume / replay / cross-process ordering is required ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)) |

**`InProcessEventBus` is a first-class transport, not a stub.** It uses the same
`IIntegrationEventHandler<T>` interface, the same `IInboxGuard` deduplication, and the
same tenant-context restoration as the durable path. A development transport that skips
those is a development transport that never exercises the isolation code — and it forces
every consumer to carry two implementations, one of which is never tested against the
other. Everything in this document about consumer obligations applies identically to both
transports; the only difference is what carries the bytes between publish and handle.

[ADR-0014](../decisions/0014-adopt-dapr.md) stands as the decision that Dapr is the
cross-process transport LearnStack uses. [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)
decides when it arrives, and the answer is "when a second process exists".

## Event types

| Type | Scope | Transport | Example |
|------|-------|-----------|---------|
| **Domain event** | Inside one module | MediatR `INotification` in-process | `CourseVersionPublished` |
| **Integration event** | Cross-module | Outbox → `IEventBus.PublishAsync` → transport (in-process dispatcher today; Dapr pub/sub → Kafka from Phase 11) | `OrderPaidV1`, `EnrollmentCreated`, `TenantSuspended` |
| **Analytics event** | Reporting stream | Same channel as integration events | `LessonCompleted` |
| **Provider event** | External callback (webhook) | Provider → API endpoint → outbox → ... | `LiveKitParticipantJoined`, `StripeInvoicePaid` |

## Outbox flow

```mermaid
sequenceDiagram
    participant Module as Owning Module
    participant DB as PostgreSQL
    participant Outbox as outbox_messages
    participant Processor as OutboxProcessor (BackgroundService)
    participant Bus as IEventBus
    participant Transport as Transport (in-process dispatcher, or Dapr to Kafka)
    participant Consumer as Consuming Module
    participant Inbox as inbox_messages

    Module->>DB: Aggregate state change
    Module->>Outbox: INSERT outbox row (same transaction)
    DB->>DB: COMMIT (atomic: aggregate + outbox row)

    loop Polling (every 200ms; configurable)
        Processor->>Outbox: CLAIM — UPDATE SET locked_by, locked_until<br/>WHERE id IN (SELECT ... FOR UPDATE SKIP LOCKED)<br/>RETURNING *; the claim survives the COMMIT
        Processor->>Bus: PublishAsync(event, partitionKey)<br/>topic learnstack.{module}.{aggregate}
        Bus->>Transport: deliver
        Processor->>Outbox: UPDATE processed_at = now()<br/>WHERE id = @id AND locked_by = @me
    end

    Transport->>Consumer: deliver to subscribed handler
    Consumer->>Consumer: Restore tenant context from event.TenantId
    Consumer->>Inbox: IsAlreadyProcessedAsync(event.EventId)
    alt Already processed
        Inbox-->>Consumer: skip
    else New event
        Consumer->>Consumer: Handle business logic
        Consumer->>Inbox: MarkAsProcessed(event.EventId, event type name)
        Consumer->>DB: SaveChangesAsync (business write + inbox marker, atomic)
    end
```

Read as text: the producer writes the aggregate change and the outbox row in one
transaction. A processor **claims** a batch of unprocessed rows by stamping a lease on
them, and the claim outlives the claiming transaction. It publishes each claimed row and
marks it processed only if it still holds the lease. The transport delivers to the
consumer, which restores tenant context, deduplicates through its inbox, and commits the
business write and the inbox marker together.

## Outbox table

LearnStack uses a **single shared `outbox_messages` table** — not one per module — with
`tenant_id` for Row Level Security isolation. The canonical DDL, its indexes, and its RLS
policy live in exactly one place:
[Database Standards § Outbox](../standards/05-database.md). Do not copy the DDL into a
third document; the last time this table's policy was duplicated, the copy carried the
superseded two-permissive-policy shape that
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) corrects.

Three columns exist for reasons this document owns rather than the schema, and the two
guarantees below (ordering, single-claimant dispatch) cannot be honoured without them:

| Column | Why it exists | Lands with |
|---|---|---|
| `partition_key text NOT NULL` | The ordering guarantee below is expressed entirely through this value. Defaults to the aggregate id; falls back to `tenant_id` when the event names no aggregate. Set at enqueue time by `IOutbox.EnqueueAsync`, never by the transport. | The table itself, in [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) — a producer-side column is cheaper to ship with the table than to backfill |
| `locked_by text NULL` + `locked_until timestamptz NULL` | The dispatch **lease**. A processor stamps them to claim a row, and the claim survives the claiming transaction's commit. See the claim protocol below. | The dispatcher, in [Phase 02b](../roadmap/phase-02b-events-auth.md) |
| `available_after timestamptz NOT NULL` | Retry backoff. A failed dispatch pushes this forward instead of blocking the batch. | Already in the canonical DDL |

Both additions go into the canonical DDL in Standards 05 when their phase ships; this
document does not carry a second copy of the table.

The pending index therefore covers `(available_after)` filtered on
`processed_at IS NULL`, and the claim predicate additionally reads
`locked_until IS NULL OR locked_until < now()`.

### Why the `learnstack_outbox_admin` role bypasses RLS — and what bounds the bypass

The OutboxProcessor must dispatch events for **every** tenant from a single
BackgroundService instance; setting `app.tenant_id` per row would defeat the
`FOR UPDATE SKIP LOCKED` batching pattern. The role bypasses RLS by design, and
the bypass is bounded by:

- **Scope by grant**. The role has `SELECT`, `UPDATE` only on `outbox_messages`
  (status column transitions: `processed_at`, `attempts`, `last_error`,
  `available_after`, `locked_by`, `locked_until`). It has **no** access to any
  other tenant-owned table; the bypass cannot be used as a generic backdoor.
- **No connection sharing**. The role is used only by the `OutboxProcessor`
  BackgroundService DI scope; no MediatR handler or API endpoint ever runs
  under this role (architecture test
  `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` enforces this).
- **Audited on use**. Every dispatch attempt produces a structured log entry
  with `event_id`, `tenant_id`, `event_type`, and outcome. Bypass-as-such is
  not separately audited (the dispatch outcome is); a misuse pattern would
  surface as out-of-band tenants in the dispatch log.
- **Permission rationale anchored in ADR-0006 Amendment 1**. The outbox-as-
  durable-buffer + Dapr-as-transport pattern requires a single batch-fetcher;
  the RLS bypass is the minimum privilege escalation that makes that pattern
  work. It is not an exception to ADR-0003 — it is the only legitimate path.

`learnstack_outbox_admin` is one of the **four** roles in the database model fixed by
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) —
`learnstack_migration` (owner, `NOBYPASSRLS`), `learnstack_app` (runtime,
`NOBYPASSRLS`), `learnstack_platform` (audited cross-tenant admin, `BYPASSRLS`), and
this one. Cross-tenant audit reads use `learnstack_platform` through the audited
`EnterPlatformAdminScope(reason)` path; there is no separate audit role. Adding a fifth
role requires an ADR — every `BYPASSRLS` role is a hole in the isolation model that has
to earn its existence by grant scope, DI-scope isolation, and log discoverability.

## Inbox table (per consumer module)

```sql
CREATE TABLE inbox_messages (
    event_id        uuid PRIMARY KEY,
    event_type      text NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now(),
    processed_at    timestamptz NULL
);
```

Each module has its own inbox table; deduplication is per-module (a single event can be
processed by N consumers independently).

## Producer pattern

In a module's command handler:

```csharp
public async Task<Result<EnrollmentDto>> Handle(CreateEnrollmentCommand cmd, CancellationToken ct)
{
    var enrollment = Enrollment.Create(cmd.LearnerId, cmd.CourseId, ...);
    _dbContext.Enrollments.Add(enrollment);

    await _outbox.EnqueueAsync(new EnrollmentCreatedIntegrationEvent
    {
        TenantId = _tenantContext.Current.TenantId,
        EnrollmentId = enrollment.Id.Value,
        LearnerId = cmd.LearnerId,
        CourseId = cmd.CourseId,
        OccurredAt = _clock.UtcNow,   // IClock per Standards 02 § Time
    }, ct);

    await _dbContext.SaveChangesAsync(ct);    // aggregate + outbox row, atomic

    return Result<EnrollmentDto>.Ok(MapToDto(enrollment), LocalizedMessage.Of("lockey_enrollment_created"));
}
```

`IOutbox.EnqueueAsync` writes to the same `DbContext` (no separate transaction); commit
is atomic with the aggregate write. It also resolves and stores the row's
`partition_key` — here `EnrollmentId`, the aggregate this event is about. See
[Ordering](#ordering).

## Consumer pattern

```csharp
public sealed class CreateAuditTrailOnEnrollmentCreated(
    IInboxGuard inboxGuard,
    EnrollmentAuditDbContext db,
    ILogger<CreateAuditTrailOnEnrollmentCreated> logger)
    : IIntegrationEventHandler<EnrollmentCreatedIntegrationEvent>
{
    public async Task HandleAsync(EnrollmentCreatedIntegrationEvent @event, CancellationToken ct)
    {
        if (await inboxGuard.IsAlreadyProcessedAsync(@event.EventId, ct))
        {
            logger.LogDebug("Duplicate event {EventId} skipped", @event.EventId);
            return;
        }

        var entry = AuditEntry.CreateFor(@event);   // business logic
        await db.AuditEntries.AddAsync(entry, ct);

        inboxGuard.MarkAsProcessed(@event.EventId, @event.GetType().Name);
        await db.SaveChangesAsync(ct);
    }
}
```

## OutboxProcessor (BackgroundService)

### The claim problem

`SELECT ... FOR UPDATE SKIP LOCKED` holds its row locks **only until the transaction that
took them ends**. A processor that selects a batch, commits to release the locks, and
*then* starts publishing has released every row it is about to work on. A second
processor polling one millisecond later sees those rows with `processed_at IS NULL`, is
not blocked by any lock, and dispatches the same events. The first processor then marks
them processed, and nothing in the system records that they went out twice.

That is why this document no longer claims "no double dispatch". The delivery contract is
**at-least-once**, and consumer-side idempotency through `IInboxGuard` is what makes it
safe — not the row lock. What the claim protocol buys is that duplicates come from crash
recovery and lease expiry, which are rare and bounded, rather than from every concurrent
poll, which is neither.

### The claim protocol

The row lock has to survive the transaction that takes it, which means the claim must be
**written to the row**, not held in the lock table:

```csharp
public sealed class OutboxProcessor : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    private readonly string _processorId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = _serviceProvider.CreateAsyncScope();
            var processed = await ProcessBatchAsync(scope.ServiceProvider, ct);

            if (processed == 0)
                await Task.Delay(PollInterval, ct);
        }
    }

    private async Task<int> ProcessBatchAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<OutboxDbContext>();
        var eventBus = services.GetRequiredService<IEventBus>();
        // Connection runs as learnstack_outbox_admin; RLS bypassed by grant.

        // 1. CLAIM. One statement, its own short transaction. The lease is written to the
        //    row, so it outlives the commit that releases the physical locks.
        var batch = await db.Set<OutboxMessage>()
            .FromSqlInterpolated($@"
                UPDATE outbox_messages
                   SET locked_by    = {_processorId},
                       locked_until = now() + {LeaseDuration},
                       attempts     = attempts + 1
                 WHERE id IN (
                       SELECT id FROM outbox_messages
                        WHERE processed_at IS NULL
                          AND available_after <= now()
                          AND (locked_until IS NULL OR locked_until < now())
                        ORDER BY occurred_at
                        LIMIT {BatchSize}
                          FOR UPDATE SKIP LOCKED)
             RETURNING *")
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        // 2. PUBLISH. One short transaction per message. A single poisoned message does
        //    not roll back the batch.
        foreach (var msg in batch)
        {
            await using var perMessageTx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var eventInstance = JsonSerializer.Deserialize(msg.Payload, Type.GetType(msg.Type)!);
                await eventBus.PublishAsync((IIntegrationEvent)eventInstance!, msg.PartitionKey, ct);
                msg.MarkProcessed(_clock.UtcNow, _processorId);   // no-op if the lease was lost
            }
            catch (Exception ex)
            {
                msg.RecordFailure(ex.Message, GetBackoffDuration(msg.Attempts));
                _logger.LogWarning(ex, "Outbox dispatch failed for {EventId} (attempt {Attempt})",
                    msg.Id, msg.Attempts);
            }
            await db.SaveChangesAsync(ct);
            await perMessageTx.CommitAsync(ct);
        }
        return batch.Count;
    }
}
```

`MarkProcessed` writes `processed_at` **and** clears the lease under the predicate
`locked_by = @me`, so a processor whose lease expired mid-batch — because the transport
hung past `LeaseDuration` — cannot overwrite the state of whichever processor took the
row next. A lost lease is logged at warning level and surfaces on the
`learnstack_outbox_lease_lost_total` counter; a non-zero rate means `LeaseDuration` is
shorter than the transport's tail latency and must be raised.

### The simpler alternative, and its cost

Holding one transaction open across the whole batch — `BEGIN`, `SELECT ... FOR UPDATE
SKIP LOCKED`, publish each, `UPDATE processed_at`, `COMMIT` — is also correct, and is an
acceptable implementation of the same contract. Its costs are real and must be accepted
knowingly:

- The transaction stays open for the duration of every network publish in the batch,
  pinning the `VACUUM` horizon and the oldest transaction id for that long.
- One poisoned message rolls back the batch unless every publish is wrapped in a
  savepoint, which reintroduces most of the complexity the approach was meant to avoid.
- A crash mid-batch republishes everything already published in it.

The lease is the specified design. The batch-held transaction is the acceptable fallback
if a deployment cannot add the two columns. Whichever ships, the choice is made once, in
[Phase 02b](../roadmap/phase-02b-events-auth.md), and written into the dispatcher — not
left to each reader of this document.

### What the processor guarantees

- **At-least-once delivery.** Duplicates are expected; consumers deduplicate through
  `IInboxGuard`. This is the contract, not a defect.
- **At most one active claimant per row.** Two processors cannot both hold a live lease
  on the same row, so concurrent polling does not multiply dispatches. Duplicates arise
  only from lease expiry and crash recovery.
- **Batch independence.** Each message gets its own transaction; one failure does not
  roll back the batch.
- **Bounded retry.** Failed messages retry with exponential backoff (1s, 5s, 30s, 5min,
  1h). On reaching `MaxAttempts` the row moves to the producer-side dead-letter state
  described below.
- **Horizontal scalability.** `SKIP LOCKED` plus the lease predicate lets N processors
  across N pods drain the same table without coordination.

## `IEventBus` and the partition key

The port takes the partition key explicitly. It is not derived inside the transport,
because the transport is the one component that does not know what the event's ordering
domain is:

```csharp
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, string partitionKey, CancellationToken ct = default)
        where TEvent : IIntegrationEvent;
}
```

The durable implementation forwards it as Dapr's `partitionKey` metadata, which the Kafka
pub/sub component maps onto the Kafka message key:

```csharp
public sealed class DaprEventBus(DaprClient daprClient) : IEventBus
{
    public Task PublishAsync<TEvent>(TEvent @event, string partitionKey, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
        => daprClient.PublishEventAsync(
               "pubsub",
               ConventionTopicName(@event),          // "learnstack.{module}.{aggregate}"
               @event,
               new Dictionary<string, string> { ["partitionKey"] = partitionKey },
               ct);

    private static string ConventionTopicName(IIntegrationEvent @event)
        => $"learnstack.{ExtractModule(@event.GetType())}.{ExtractAggregate(@event.GetType())}";
}
```

`InProcessEventBus` honours the same key by serialising dispatch per partition key —
concurrent across keys, sequential within one — so a consumer cannot observe an ordering
in development that the durable transport would not also produce.

Topic naming convention: `learnstack.{module}.{aggregate}`. Examples:

- `learnstack.identity.user`
- `learnstack.tenancy.tenant`
- `learnstack.tenancy.organization`
- `learnstack.enrollment.enrollment`
- `learnstack.classroom.session`
- `learnstack.hub.entitlement` (Hub-side)
- `learnstack.cache.invalidation` (cross-instance L1 cache)

## `InProcessEventBus`

Until the Dapr adapter's trigger fires, `InProcessEventBus` is the only registered
`IEventBus`. It is a **transport**, not a shortcut, and it carries the same four
obligations as the durable path:

| Obligation | Why it cannot be skipped in development |
|---|---|
| Same handler interface — `IIntegrationEventHandler<T>`, never a bare `INotificationHandler<T>` | Two interfaces means two implementations per consumer, and the one that runs in CI is not the one that runs in production |
| Same `IInboxGuard` deduplication | Deduplication bugs are the most common integration-event defect; a transport that never delivers a duplicate never surfaces them |
| Same tenant-context restoration from `@event.TenantId` before the handler runs | A dev path that skips it is a dev path where Row Level Security and the query filters are never exercised on the consumer side |
| Same per-partition-key ordering | Ordering assumptions that hold only in process are discovered in production |

```csharp
public sealed class InProcessEventBus(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantAccessor,
    IPartitionSerializer partitions) : IEventBus
{
    public Task PublishAsync<TEvent>(TEvent @event, string partitionKey, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
        => partitions.RunSequentiallyFor(partitionKey, async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            tenantAccessor.Set(TenantContext.FromEvent(@event));    // same restore as the durable path
            foreach (var handler in scope.ServiceProvider.GetServices<IIntegrationEventHandler<TEvent>>())
                await handler.HandleAsync(@event, ct);              // handler calls IInboxGuard itself
        });
}
```

What the in-process transport genuinely does **not** provide, and what therefore
constitutes the trigger for the Dapr adapter: delivery to a second process, broker-side
replay, and durability of the in-flight message if the process dies between publish and
handle. The outbox row covers the third — an unhandled event has not been marked
processed and is redelivered — which is precisely why the outbox is LearnStack-owned and
ships before any transport.

The composition root selects the implementation by `DeploymentMode`; modules never see
the choice.

## Rules

- Integration events are **versioned**. Breaking changes ship a new versioned record
  (`UserCreatedIntegrationEventV2`); old version remains supported during a migration
  window.
- Consumers are **idempotent** via `IInboxGuard` (per-module inbox table).
- Mandatory metadata on every integration event: `EventId`, `TenantId`, `OccurredAt`,
  `CorrelationId`. Optional but recommended: `CausationId`, `ActorUserId`.
- **Tenant context** restored on the consumer side before any business logic runs. The
  transport delivers the event payload; the consumer scope sets the ambient context from
  `@event.TenantId` before invoking the handler, so the handler's queries carry the same
  filters and the same `app.tenant_id` as an HTTP-originated command.

## Ordering

Ordering is **not** guaranteed across topics, and never has been. Within one topic,
ordering is guaranteed **per partition key** — and a partition key that nobody sets is a
guarantee nobody has.

The rule, therefore, is that **every outbox row carries a non-null `partition_key`**:

| Event shape | Partition key |
|---|---|
| Event about one aggregate instance (the normal case) | The aggregate id — `EnrollmentCreatedIntegrationEvent` keys on `EnrollmentId` |
| Event about the tenant as a whole (`TenantSuspended`, `EntitlementUpdated`) | `TenantId` |
| Event with an explicit ordering domain that is neither (rare) | Declared by the event type through `IPartitionedIntegrationEvent.PartitionKey` |

`IOutbox.EnqueueAsync` resolves the key at enqueue time and writes it to the row; the
processor reads it from the row and passes it to `IEventBus.PublishAsync`. Nothing
downstream re-derives it, so the ordering domain is decided once, by the producer that
knows it.

Consequences worth stating plainly:

- Two events about the **same** aggregate arrive in publish order at every consumer.
- Two events about **different** aggregates may arrive in any relative order, even inside
  one topic. A consumer that needs a cross-aggregate order needs a different design —
  usually a read model, not an ordering assumption.
- Keying on `TenantId` for high-volume events serialises that tenant's whole stream onto
  one partition. That is a deliberate throughput cost, paid only where the ordering is
  genuinely tenant-wide.
- The column is `NOT NULL`, so the database asserts it. The catalogued
  `Outbox_Row_Carries_Correlation_Context` integration test extends its assertion to
  `partition_key` when the column lands, so a new enqueue path that forgets it fails in
  CI rather than in a consumer.

## Dead-letter: two sides, two failure domains

The distinction the earlier draft of this document missed: a producer-side dispatch
success means "the transport accepted the bytes". It says nothing about whether any
consumer processed them. The two sides fail independently and need separate handling.

### Producer side — dispatch never succeeded

The outbox row reaches `MaxAttempts` without a successful `PublishAsync`.

- The row stays in the table with `processed_at IS NULL`, `attempts >= MaxAttempts`, and
  the final `last_error`.
- `available_after` is pushed beyond the retry horizon so the poller stops picking it up.
- It surfaces on the `OutboxStatusEndpoints` admin API and on the
  `learnstack_outbox_deadletter_total{event_type}` counter; the alert is on the counter's
  rate, not on the table's size.
- Recovery is a manual replay: an operator clears the lease, resets `attempts`, and
  resets `available_after`.

### Subscriber side — dispatch succeeded, handling did not

A consumer's handler throws on every attempt: a malformed payload, a schema version it
cannot read, a permanently failing downstream dependency. The producer's row is long
since `processed_at`-stamped, and no amount of producer-side retry will help.

- The transport retries the handler with backoff. On exhausting its retry policy it
  routes the message to a **dead-letter topic**, `learnstack.dlq.{module}` — one per
  subscribing module, three segments, satisfying
  `Dapr_PubSub_TopicNames_FollowConvention`. Under Dapr this is the subscription's
  `deadLetterTopic`; under `InProcessEventBus` the equivalent is a row in
  `dead_letter_messages` carrying the same envelope.
- **The inbox row is not marked processed.** A dead-lettered message has not been
  handled, and recording it as handled would make a later replay a silent no-op.
- Every module subscribes to its own dead-letter topic with a handler that does nothing
  but persist the envelope, emit `learnstack_inbox_deadletter_total{module, event_type}`,
  and raise an operational alert. A dead-letter topic nobody consumes is a queue that
  grows until the broker's retention silently deletes the evidence.
- Recovery is an operator-initiated replay from the dead-letter store back onto the
  original topic. Because the inbox never marked the event processed, the replayed
  message is handled normally; because `IInboxGuard` is still in the path, a replay that
  races a late success is still idempotent.
- Poison-message containment is per subscription. One module's dead-lettered event does
  not block another module's consumption of the same topic — each subscription has its
  own offset and its own dead-letter path.

## Service extraction readiness

The outbox + `IEventBus` is the future service boundary. If a module is promoted to a
separate service later:

- The producer-side outbox is durable and LearnStack-owned; service extraction does not
  require dual-write semantics.
- The consumer-side contract — `IIntegrationEventHandler<T>`, `IInboxGuard`, tenant-context
  restoration, partition key — is identical under both transports, so extraction changes
  which process runs the handler, not how the handler is written.
- Swapping `InProcessEventBus` for `DaprEventBus` is a composition-root change. That is
  the whole reason the transport is demand-gated: extraction gets no cheaper by having
  built the adapter early, and the platform pays the operational cost of a broker every
  day until then.

## Architecture tests

- `Integration_Events_Inherit_From_IntegrationEventBase` — every type implementing
  `IIntegrationEvent` extends `IntegrationEventBase` (which carries `EventId`,
  `OccurredAt`, `TenantId`).
- `Integration_Event_Handlers_Use_InboxGuard` — every `IIntegrationEventHandler<T>`
  implementation invokes `IInboxGuard.IsAlreadyProcessedAsync` before processing.
- `Dapr_PubSub_TopicNames_FollowConvention` — string scan ensures every `[Topic]`
  attribute argument matches
  `^learnstack\.[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)?$`. The
  optional fourth segment is reserved for **Hub-side event-name suffixes**
  (`learnstack.hub.custom-domain.activated`, `.deactivated`, `.revoked`).
  LearnStack-core topics remain 3-segment (`learnstack.{module}.{aggregate}`).
- `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` — integration test asserts one
  poisoned message doesn't prevent others in the batch from processing.
- `Integration_Event_Handler_Restores_Tenant_Context` — catalogued in
  [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md); runs
  against whichever transport is registered, so `InProcessEventBus` is held to the same
  restoration contract as the durable path.
- `Outbox_Row_Carries_Correlation_Context` — extended to assert a non-null
  `partition_key`.
- Concurrent-claim integration test: two `OutboxProcessor` instances draining one table
  produce **one** claim per row while both leases are live. This is the test that would
  have caught the released-lock defect, and it needs two processors — a single-processor
  test passes against the broken protocol.

## Observability

- Outbox lag: `learnstack_outbox_pending_count{tenant_id}` (gauge).
- Dispatch duration: `learnstack_outbox_dispatch_duration_seconds{event_type}` (histogram).
- Dispatch failures: `learnstack_outbox_dispatch_failed_total{event_type}` (counter).
- Producer-side dead letter: `learnstack_outbox_deadletter_total{event_type}` (counter).
- Lost leases: `learnstack_outbox_lease_lost_total` (counter) — non-zero means the lease
  is shorter than the transport's tail latency.
- Inbox dedup: `learnstack_inbox_dedup_total{module, event_type}` (counter).
- Subscriber-side dead letter: `learnstack_inbox_deadletter_total{module, event_type}`
  (counter).

The two dead-letter counters are **separate signals on separate dashboards**. A
producer-side spike means the transport is down; a subscriber-side spike means a handler
or a payload is broken. Collapsing them into one "events failed" number destroys the only
information that distinguishes the two incidents. Once the Dapr adapter lands, both sit
alongside Dapr's own `dapr_component_pubsub_*` metrics.

## References

- ADR-0006 (Amendment 1) — Events and Outbox; dispatch transport.
- ADR-0010 (Amendment 1) — Cross-Module Communication; outbox dispatch target.
- ADR-0014 — Adopt Dapr (what LearnStack uses for cross-process pub/sub).
- [ADR-0033](../decisions/0033-audit-durability-model.md) — Audit durability model;
  audit fan-out to external sinks rides this outbox, MUST-class audit does not.
- [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) — Demand-gated
  infrastructure; when the Dapr and Kafka adapters arrive.
- [Database Standards § Outbox](../standards/05-database.md) — the canonical
  `outbox_messages` DDL and RLS policy.
- [29-dapr-integration.md](29-dapr-integration.md) — Dapr deep dive.
- [10-cross-module-contracts.md](10-cross-module-contracts.md) — the four sanctioned
  cross-module mechanisms.
- Nexora reference: `Nexora/docs/decisions/0005-transactional-outbox.md`,
  `Nexora/docs/decisions/0011-outbox-service-atomicity.md`,
  `Nexora/docs/decisions/0010-notification-delivery-kafka.md`.
