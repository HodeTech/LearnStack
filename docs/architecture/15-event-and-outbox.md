# Events and Outbox

Events allow modules to collaborate without cross-module database coupling. This document
defines the event types, the outbox pattern, and how dispatch is delivered via Dapr
pub/sub to Kafka (ADR-0010 Amendment 1 + ADR-0014).

## Decision

Use **domain events** within a module (in-process MediatR notifications, same transaction
as the aggregate change) and **integration events** across modules (written to an outbox
in the same transaction; dispatched at-least-once to subscribers via Dapr pub/sub to
Kafka with retry / backoff / dead-lettering).

The outbox table is **LearnStack-owned** (per-module in PostgreSQL); Dapr is the
**dispatch transport**, not the durable buffer.

## Event types

| Type | Scope | Transport | Example |
|------|-------|-----------|---------|
| **Domain event** | Inside one module | MediatR `INotification` in-process | `CourseVersionPublished` |
| **Integration event** | Cross-module | Outbox → `IEventBus.PublishAsync` → Dapr pub/sub → Kafka | `OrderPaidV1`, `EnrollmentCreated`, `TenantSuspended` |
| **Analytics event** | Reporting stream | Same channel as integration events | `LessonCompleted` |
| **Provider event** | External callback (webhook) | Provider → API endpoint → outbox → ... | `LiveKitParticipantJoined`, `StripeInvoicePaid` |

## Outbox flow

```mermaid
sequenceDiagram
    participant Module as Owning Module
    participant DB as PostgreSQL (module schema)
    participant Outbox as outbox_messages
    participant Processor as OutboxProcessor (BackgroundService)
    participant Dapr as Dapr sidecar
    participant Kafka
    participant ConsumerDapr as Consumer Dapr sidecar
    participant Consumer as Consuming Module
    participant Inbox as inbox_messages

    Module->>DB: Aggregate state change
    Module->>Outbox: INSERT outbox row (same transaction)
    DB->>DB: COMMIT (atomic: aggregate + outbox row)

    loop Polling (every 200ms; configurable)
        Processor->>Outbox: SELECT WHERE processed_at IS NULL<br/>FOR UPDATE SKIP LOCKED LIMIT @batch
        Processor->>Dapr: IEventBus.PublishAsync → DaprClient.PublishEventAsync<br/>(topic: learnstack.{module}.{aggregate})
        Dapr->>Kafka: produce
        Processor->>Outbox: UPDATE processed_at = now()
    end

    Kafka->>ConsumerDapr: deliver to subscribed sidecar
    ConsumerDapr->>Consumer: HTTP POST /events/{topic}
    Consumer->>Inbox: IsAlreadyProcessedAsync(@event.EventId)
    alt Already processed
        Inbox-->>Consumer: skip
    else New event
        Consumer->>Consumer: Handle business logic
        Consumer->>Inbox: MarkAsProcessed(@event.EventId, @event.GetType().Name)
        Consumer->>DB: SaveChangesAsync (business write + inbox marker, atomic)
    end
```

## Outbox table

Each module owns its own outbox table inside the module's schema (or in a shared
namespace with module-prefixed key). LearnStack uses a single shared `outbox_messages`
table with `tenant_id` for RLS isolation:

```sql
CREATE TABLE outbox_messages (
    id              uuid PRIMARY KEY,
    occurred_at     timestamptz NOT NULL DEFAULT now(),
    tenant_id       uuid NOT NULL,
    correlation_id  text NULL,
    causation_id    uuid NULL,
    actor_user_id   uuid NULL,
    type            text NOT NULL,           -- assembly-qualified event type name
    topic           text NOT NULL,           -- "learnstack.identity.user"
    payload         jsonb NOT NULL,
    metadata        jsonb NULL,
    processed_at    timestamptz NULL,
    attempts        int NOT NULL DEFAULT 0,
    last_error      text NULL,
    available_after timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_outbox_pending
    ON outbox_messages (available_after)
    WHERE processed_at IS NULL;
CREATE INDEX ix_outbox_tenant_pending
    ON outbox_messages (tenant_id, available_after)
    WHERE processed_at IS NULL;

ALTER TABLE outbox_messages ENABLE ROW LEVEL SECURITY;
CREATE POLICY outbox_messages_tenant_isolation ON outbox_messages
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);
-- OutboxProcessor uses learnstack_outbox_admin role that bypasses RLS to read all tenants.
```

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
        OccurredAt = DateTime.UtcNow
    }, ct);

    await _dbContext.SaveChangesAsync(ct);    // aggregate + outbox row, atomic

    return Result.Success(MapToDto(enrollment), LocalizedMessage.Of("enrollment.created"));
}
```

`IOutbox.EnqueueAsync` writes to the same `DbContext` (no separate transaction); commit
is atomic with the aggregate write.

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

Continuously polls `outbox_messages` and dispatches:

```csharp
public sealed class OutboxProcessor : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

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
        // Connection set to learnstack_outbox_admin role; RLS bypassed.

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.Set<OutboxMessage>()
            .FromSqlInterpolated($@"
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL AND available_after <= now()
                ORDER BY occurred_at
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);

        if (batch.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        // Commit the SELECT FOR UPDATE transaction immediately to release row locks.
        await tx.CommitAsync(ct);

        foreach (var msg in batch)
        {
            await using var perMessageTx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var eventInstance = JsonSerializer.Deserialize(msg.Payload, Type.GetType(msg.Type)!);
                await eventBus.PublishAsync((IIntegrationEvent)eventInstance!, ct);
                msg.MarkProcessed(DateTimeOffset.UtcNow);
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

Key invariants:

- `FOR UPDATE SKIP LOCKED` allows horizontal scaling (multiple OutboxProcessor instances
  across pods can run concurrently without double-dispatch).
- The `SELECT FOR UPDATE` transaction commits immediately to release row locks.
- Each message gets its own transaction; one failure doesn't roll back the batch.
- Failed messages are retried with exponential backoff (1s, 5s, 30s, 5min, 1h);
  permanent failures (max retries reached) move to dead-letter (manual intervention).

## `IEventBus` → Dapr pub/sub → Kafka

```csharp
public sealed class DaprEventBus(DaprClient daprClient) : IEventBus
{
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
    {
        var topic = ConventionTopicName(@event);   // "learnstack.{module}.{aggregate}"
        return daprClient.PublishEventAsync("pubsub", topic, @event, ct);
    }

    private static string ConventionTopicName(IIntegrationEvent @event)
        => $"learnstack.{ExtractModule(@event.GetType())}.{ExtractAggregate(@event.GetType())}";
}
```

Topic naming convention: `learnstack.{module}.{aggregate}`. Examples:

- `learnstack.identity.user`
- `learnstack.tenancy.tenant`
- `learnstack.tenancy.organization`
- `learnstack.enrollment.enrollment`
- `learnstack.classroom.session`
- `learnstack.hub.entitlement` (Hub-side)
- `learnstack.cache.invalidation` (cross-instance L1 cache)

## Development mode

When `DeploymentMode.Development` and Dapr sidecar isn't running, `InProcessEventBus`
replaces `DaprEventBus`:

```csharp
public sealed class InProcessEventBus(IPublisher publisher) : IEventBus
{
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IIntegrationEvent
        => publisher.Publish(@event, ct);
}
```

Module subscribers (`INotificationHandler<TIntegrationEvent>` for dev / `IIntegrationEventHandler<TEvent>`
for prod) handle the event. The composition root chooses based on `DeploymentMode`.

## Rules

- Integration events are **versioned**. Breaking changes ship a new versioned record
  (`UserCreatedIntegrationEventV2`); old version remains supported during a migration
  window.
- Consumers are **idempotent** via `IInboxGuard` (per-module inbox table).
- Mandatory metadata on every integration event: `EventId`, `TenantId`, `OccurredAt`,
  `CorrelationId`. Optional but recommended: `CausationId`, `ActorUserId`.
- **Tenant context** restored on the consumer side before any business logic runs
  (Dapr passes the event payload; consumer middleware sets `accessor.SetTenant(...)`
  from `@event.TenantId` before invoking the handler).
- Ordering is **not** guaranteed across topics; **per-partition** ordering within a Kafka
  topic is preserved by partition-keying on the aggregate id when ordering is required
  (rare).
- Failed dispatches **retry with exponential backoff**; permanent failures move to a
  dead-letter state (manual review via the `OutboxStatusEndpoints` admin API).

## Service extraction readiness

The outbox + `IEventBus` is the future service boundary. If a module is promoted to a
separate service later:

- The module's integration events already cross a real broker (Kafka).
- The consumer-side subscription pattern (Dapr `[Topic]` attribute or programmatic
  subscription) works identically across in-process and cross-service.
- The producer-side outbox is durable; service extraction doesn't require dual-write
  semantics.

## Architecture tests

- `Integration_Events_Inherit_From_IntegrationEventBase` — every type implementing
  `IIntegrationEvent` extends `IntegrationEventBase` (which carries `EventId`,
  `OccurredAt`, `TenantId`).
- `Integration_Event_Handlers_Use_InboxGuard` — every `IIntegrationEventHandler<T>`
  implementation invokes `IInboxGuard.IsAlreadyProcessedAsync` before processing.
- `Dapr_PubSub_TopicNames_FollowConvention` — string scan ensures every `[Topic]`
  attribute argument matches `^learnstack\.[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*$`.
- `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` — integration test asserts one
  poisoned message doesn't prevent others in the batch from processing.

## Observability

- Outbox lag metric: `learnstack_outbox_pending_count{tenant_id}` (gauge).
- Dispatch duration: `learnstack_outbox_dispatch_duration_seconds{event_type}` (histogram).
- Dispatch failures: `learnstack_outbox_dispatch_failed_total{event_type}` (counter).
- Inbox dedup count: `learnstack_inbox_dedup_total{module, event_type}` (counter).

Grafana dashboard surfaces these alongside Dapr's own pub/sub metrics
(`dapr_component_pubsub_*`).

## References

- ADR-0006 (Amendment 1) — Events and Outbox; Dapr pub/sub dispatch transport.
- ADR-0010 (Amendment 1) — Cross-Module Communication; outbox dispatch target.
- ADR-0014 — Adopt Dapr.
- ADR-0016 — Audit Log Subsystem.
- [29-dapr-integration.md](29-dapr-integration.md) — Dapr deep dive.
- [10-cross-module-contracts.md](10-cross-module-contracts.md) — the four sanctioned
  cross-module mechanisms.
- Nexora reference: `Nexora/docs/decisions/0005-transactional-outbox.md`,
  `Nexora/docs/decisions/0011-outbox-service-atomicity.md`,
  `Nexora/docs/decisions/0010-notification-delivery-kafka.md`.
