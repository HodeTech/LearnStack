# ADR 0006: Events and Outbox

## Status

Accepted (Amendment 1: 2026-05-18 — clarifies Dapr pub/sub as the dispatch transport; see
bottom of document)

## Decision

LearnStack uses domain events inside module boundaries and integration events across module boundaries.

Integration events are written to an outbox in the same transaction as the state change that produced them. A worker dispatches outbox events to internal handlers, projections, or external integrations.

## Context

The modular monolith must avoid direct cross-module database coupling and should remain extractable into services later.

## Consequences

- Domain events are internal to a module.
- Integration events are versioned contracts.
- Outbox dispatch is idempotent.
- Background workers preserve tenant context.
- Event schema changes require compatibility review.

---

## Amendment 1 — Dapr pub/sub as dispatch transport (2026-05-18)

Per [ADR-0014](0014-adopt-dapr.md) (Adopt Dapr) and [ADR-0010 Amendment 1](0010-cross-module-communication.md),
the outbox dispatch worker publishes integration events via the Dapr pub/sub building
block, with Apache Kafka as the production backend.

**Producer side (unchanged guarantee, clarified transport):**

```
Module transaction commits ─┐
                            ├── 1. Aggregate state row(s) updated
                            ├── 2. OutboxMessage row inserted (atomic with #1)
                            └── COMMIT
                                  │
                                  ▼
        OutboxProcessor (BackgroundService) polls outbox table
                  SELECT ... WHERE processed_at IS NULL
                  FOR UPDATE SKIP LOCKED LIMIT @batch_size
                                  │
                                  ▼
        Per message:
          IEventBus.PublishAsync<TIntegrationEvent>(event)
            → DaprEventBus → DaprClient.PublishEventAsync(
                pubsubName: "pubsub",
                topicName: $"learnstack.{module}.{aggregate}",
                data: event)
            → Dapr sidecar → Kafka topic
          On success: outbox row marked processed_at
          On failure: retry_count++, exponential backoff, eventually DLQ
```

**Consumer side:**

- Subscribed via Dapr `[Topic("pubsub", "learnstack.{module}.{aggregate}")]` attribute on
  the consuming handler, OR programmatic subscription in `IModule.ConfigureEventHandlers`.
- Consumer wraps work in `IInboxGuard.IsAlreadyProcessedAsync(eventId)` → process →
  `MarkAsProcessed(eventId, eventType)` → single `SaveChangesAsync()`. The inbox guard table
  lives in the consuming module's own schema; deduplication is per-module.

**Development fallback:**

When `DeploymentMode.Development` and Dapr sidecar not running, `InProcessEventBus`
implements `IEventBus` and routes via MediatR `IPublisher.Publish`. Module handlers
don't change.

**Schema versioning:**

Integration event types are `sealed record` inheriting `IntegrationEventBase` (in
`<Module>.Application.Contracts`); breaking changes ship a new versioned record
(`UserCreatedIntegrationEventV2`) and the producer migrates within one deployment window.
Outbox stores `EventType` as the assembly-qualified name for version disambiguation.


---

## Amendment 2 — The canonical outbox DDL lives in Standards 05 (2026-08-27)

This ADR's sketches predate the outbox table's canonical DDL and use two spellings
that table does not have: **`retry_count`** (the column is `attempts`) and
**`EventType`** (the column is `type`). The Decision — an outbox row written in the
same transaction as the aggregate change, dispatched at-least-once, deduplicated by
a consumer-side inbox — is unchanged; only these two names were stale.

**The canonical DDL is
[Database Standards § Outbox](../standards/05-database.md)** — every column, both
partial indexes, the isolation policy and the three role grants. It is written down
in exactly one place for the reason
[ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) records: the
previous template lived in four documents and was wrong in all four. A document
that needs the shape links there rather than restating it, and this ADR is now one
of them.

[Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) creates the table;
nothing dispatches from it until [Phase 02b](../roadmap/phase-02b-events-auth.md).
`locked_by` / `locked_until` arrive with the dispatcher in that phase and extend
`learnstack_outbox_admin`'s column-scoped `UPDATE` grant in the same migration — a
column added without extending it fails at runtime with `permission denied for
table`.

## Amendment 3 — Bounded delivery supersession by ADR-0046 (2026-10-12)

**Status: Accepted.** [ADR-0046](0046-durable-event-delivery-and-recovery.md#bounded-supersession-and-additive-seams)
supersedes Amendment 1's consumer-side handler-called guard and own-module inbox.
P02b-3 implements a shared transport-owned inbox keyed by tenant, stable
subscription ID and event ID; its separate delivery ledger owns independent
retries, terminal state and targeted replay. Publish success then means durable
acceptance of every required delivery, not completed business effects.

P02b-2 implements fresh-token leases, claim-counted attempts, explicit terminal
requests and atomic terminal audit under ADR-0046. Required Pending subscriptions
preserve rows before acquisition and validation; only declared intentional
absence acknowledges without publication. The canonical DDL/grants remain in
[Database Standards](../standards/05-database.md#outbox).

These are bounded new decisions, not ADR-0041 corrections. Original bodies,
acceptance blocks and prior amendments remain intact. Envelope contracts and
at-least-once recovery survive; demand-gated Dapr/Kafka remain Phase 11.
Acceptance claims no dispatcher or inbox delivery.
