# ADR 0038: Cross-Cutting Port and Event Contracts

## Status

Accepted

Supersedes [ADR-0014](0014-adopt-dapr.md). The Dapr technology choice survives;
the port and delivery contracts are restated here because changing those contracts by
amendment was not permitted by
[Documentation Standards § ADR Amendments](../standards/13-documentation.md#adr-amendments).

## Date

2026-08-26

## Context

ADR-0014 selected Dapr pub/sub, state and secret-store building blocks. During Packet 5,
proposed amendments attempted to change its published `IEventBus` and `ICacheService`
decisions. Those edits were contract changes rather than clarifications and are not
retained in the Accepted ADR. Implementation also exposed several ambiguities the
earlier signatures could not express safely:

- a partition key and topic each had more than one source;
- outbox correlation, organization, causation and actor metadata had no single carrier;
- an organization-scoped event could silently become tenant-wide;
- causal human identity could become the consumer's effective audit principal;
- a mutable serializer policy could produce payloads the canonical reader could not read;
- eager handler enumeration let one broken constructor deny every subscription;
- a generic platform cache-key helper could collapse tenant-owned data into one bucket.

The technology decision, contract decision and demand gate must be traceable without
depending on a chain of corrections to an Accepted ADR.

## Decision Drivers

- One source for every routing and ordering value.
- Fail-closed tenant and organization boundaries.
- Durable outbox metadata that maps directly to dispatch.
- Transport parity without coupling modules to Dapr.
- Per-subscription failure, scope and trace isolation.
- Cache semantics that remain safe when the adapter changes.
- Contracts that are valid before the first producer and consumer ship.

## Decision

### Infrastructure choice and demand gate

LearnStack retains Dapr as the cross-process adapter boundary for:

| Building block | Production backend | Application port |
|---|---|---|
| Pub/sub | Apache Kafka | `IEventBus` |
| State/cache | Valkey | `ICacheService` |
| Secrets | HashiCorp Vault | `ISecretProvider` |

Modules never import `DaprClient` or provider SDKs. The default adapters are
`InProcessEventBus`, `InMemoryCacheService` and `ConfigurationSecretProvider` until the
specific triggers in [ADR-0035](0035-demand-gated-infrastructure.md) require the Dapr
adapters in Phase 11.

### Event contract

`IEventBus` is non-generic and accepts one validated envelope:

```csharp
Task PublishAsync(
    IntegrationEventEnvelope envelope,
    CancellationToken cancellationToken = default);
```

The envelope carries the event plus W3C `CorrelationId`, optional `OrganizationId`,
`CausationId` and causal `ActorUserId`. `Topic` and `PartitionKey` are declared once by
the concrete event and forwarded by the envelope. Empty identifiers, default timestamps,
blank routing values and malformed traceparents are rejected at envelope construction.

An organization-owned event implements `IOrganizationScopedIntegrationEvent`; its
envelope requires a non-empty organization identifier. A tenant-wide event deliberately
omits the marker. A consumer always executes as `UserId.SystemActor`; a human
`ActorUserId` is retained separately as causal audit metadata and never becomes the
consumer's effective principal.

`IntegrationEventBase.ToPayloadJson()` serializes by runtime type with one named,
read-only `JsonSerializerOptions` instance. Callers cannot substitute a serializer
policy. The same options govern deserialization.

The in-process transport discovers lightweight subscription metadata at composition.
Each subscription gets one consumer activity, one async DI scope and exactly one handler
construction. Tenant context is established before subscription lookup or resolution.
Constructor, handler and disposal failures are isolated per subscription and collected;
publish-token cancellation stops dispatch before a later subscription starts. A future
Dapr adapter must preserve these observable semantics.

Only the outbox processor publishes through `IEventBus`. Modules write the outbox inside
their business transaction and may consume through
`IIntegrationEventHandler<TEvent>`; they do not inject `IEventBus` or resolve it through
`IServiceProvider`.

Topic names use `learnstack.{module}.{aggregate}`. Hub may use the documented
four-segment form `learnstack.hub.{domain}.{event}`. Every segment starts with a lower-case
letter and may then contain lower-case letters, digits or internal hyphens.

### Cache contract

`ICacheService` exposes `GetAsync`, `GetOrSetAsync`, `SetAsync` and `RemoveAsync`.
Prefix/tag invalidation is not part of the port; callers that invalidate a set use a
durable generation key.

Every tenant-owned key begins with its canonical tenant identifier. The `platform`
sentinel is reserved for the normalized Hub host-map family:
`platform:hub:host-map:{normalized-host}`. Callers compose that family through
`CacheKey.ForHostMapping`; there is no generic platform-key factory.

The in-memory adapter is a process singleton with bounded storage. Concurrent misses for
the same key and requested type are single-flight: the first caller owns the factory and
TTL, waiter cancellation does not cancel other waiters, and an abandoned factory must
actually terminate before a replacement begins. Positive, representable TTLs are
validated before lookup or factory execution. Cache metrics use stable family names and
never full keys, tenant IDs or entity IDs.

## Considered Options

### Keep the proposed ADR-0014 amendments as the authority

Rejected. They would change Decision-section contracts and therefore violate the repository's
ADR immutability rule. Readers also have to reconcile superseding signatures across
multiple amendments.

### Expose provider SDKs directly

Rejected. It couples modules to transport/cache/secret providers and breaks the shared
deployment-mode abstraction.

### Keep metadata as separate publish parameters

Rejected. Topic and partition key can disagree with the event, and adding another
required outbox field repeatedly breaks every publisher.

### Treat every event as organization-scoped

Rejected. Tenant-wide facts are legitimate. An explicit marker makes the narrower scope
required only where the event type declares it.

## Consequences

- ADR-0014 is historical; this ADR is the authority for Dapr-facing ports and event
  delivery.
- Event producers declare valid event metadata; the outbox processor constructs the
  validated envelope. Organization-owned event types implement the scope marker.
- Consumers get per-subscription scope, trace and failure isolation, with system identity
  separated from causal identity.
- The Tenancy migration must seed `UserId.SystemActor`
  (`00000000-0000-7000-8000-000000000001`) before a persisted consumer can write audit
  foreign keys.
- Cache callers cannot create arbitrary platform-wide families.
- Dapr/Valkey adapters in Phase 11 must match the same envelope, single-flight and
  observability behavior rather than introducing a second contract.

## Amendments

None.

## References

- [ADR-0006 — Events and Outbox](0006-events-and-outbox.md)
- [ADR-0010 — Cross-Module Communication](0010-cross-module-communication.md)
- [ADR-0035 — Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [Event and Outbox Architecture](../architecture/15-event-and-outbox.md)
- [Dapr Integration](../architecture/29-dapr-integration.md)
- [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md)
- [Audit Coverage Standards](../standards/18-audit-coverage.md)
