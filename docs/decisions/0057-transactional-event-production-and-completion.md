# ADR-0057: Transactional Event Production and Completion

## Status

**Proposed — approval pending.** No producer gate is Accepted and no
implementation is authorized by this draft. Approval includes the bounded
replacements below and the
[P02b-1 decision package](../roadmap/phase-02b-events-auth.md#p02b-1-proposed-decision-package-2026-10-12).

**Date:** 2026-10-12
**Deciders:** @cemil (approval pending)

## Decision Drivers

- Commit an integration event with its source state and MUST audit, including
  provisioning through the Seeder's unresolved ambient tenant context.
- Give requests and future deliveries one completion protocol; step 7 alone
  cannot flush work added after the request handler returns.
- Prevent a refused nested producer from leaving a publishable fact when its
  outer caller absorbs the refusal. Joined frames are not savepoints.
- Establish a useful production subject without inventing cache invalidation
  for readers that are already fresh or uncached.

## Considered Options

1. **Immediate transactional enqueue and owner-side completion** (recommended).
   One commit boundary; all sanctioned producer paths share it.
2. **Buffer until MediatR step 7** (rejected). Domain reactions and direct
   delivery handlers can run after that slot or without it.
3. **Dispatch domain events during each SaveChanges** (rejected). A later save
   or command refusal can invalidate an intermediate operation; joiners must
   not dispatch or claim commit ownership.
4. **Allow domain reactions to mutate aggregates** (rejected). Post-handler
   mutation can invalidate the returned version and evade per-handler root
   counting. Such work belongs to an integration-event consumer.
5. **Retain explicit-only poisoning for all nested Result failures** (rejected
   for produced work). It relies on every caller remembering a consistency
   obligation that the shared producer boundary can enforce.

## Decision

The following contracts apply **if this proposal is accepted**. They authorize
P02b-1 producer implementation, not dispatcher or consumer delivery claims.

### Immediate enqueue and stored identity

`IOutbox.EnqueueAsync` validates and inserts immediately through the active
`IUnitOfWork.Connection` and `Transaction`. It opens no connection, commits
nothing and never calls `IEventBus`. It requires an enrolled producer frame,
a writable, non-poisoned transaction and its successful tenant announcement.
Raw transactions without that frame cannot enqueue.

- `outbox_messages.id` is exactly `EventId`, supplied by the producer. There
  is no additional surrogate or `event_id` column. Retry and replay preserve
  that identity; duplicate enqueue is an error, not an ignored conflict.
- `type` retains the assembly-qualified concrete event name. P02b-2 owns the
  bounded compatibility resolver and unknown/retired/unsubscribed outcomes.
- Payload serialization uses the concrete runtime type and the existing frozen
  `IntegrationEventBase.PayloadJsonOptions`. Maximum UTF-8 payload size is
  262,144 bytes; refusal happens before INSERT.
- Each concrete event declares its partition source: its aggregate identifier
  or an explicitly tenant-wide `TenantId`. The key is that identifier's
  canonical GUID representation. Structural controls check the declaration
  and derivation with planted violations; behavioral controls compare actual
  values. This decides no publish-order guarantee; G3's ordering half is P02b-2.

### Transaction metadata and payload boundary

The unit exposes a read-only snapshot of the context it successfully announced
to PostgreSQL. It is bound to a transaction generation, cleared at every terminal
boundary and cannot be supplied by a module. Enqueue compares the event's tenant
to that snapshot, refusing empty/sentinel tenants, mismatches and stale scopes.
Provisioning uses its already-sanctioned announcement; it does not populate
`ITenantContextAccessor` or add a context writer or session-variable setter.

The envelope keeps the announced organization. Correlation, causation and the
optional causal actor come from the trusted execution boundary, not SQL session
variables or arbitrary producer-supplied metadata. An organization-scoped event
requires that organization; a non-marked event does not silently erase an
announced organization. G1's eventual delivery scope decision remains
P02b-2/P02b-3. Effective asynchronous execution uses `UserId.SystemActor`,
without impersonating the causal human.

The producer execution boundary guarantees a W3C Activity even when telemetry
has no listener, as in the Seeder. Enqueue stores its full traceparent. Domain
translation allocates a new integration `EventId` and uses the originating domain
`EventId` as causation; future delivery-originated production uses the trusted
incoming event identity. No caller-supplied header establishes causation.

Beyond the existing event identity, tenant, timestamp and computed routing
members, payload fields are identifiers only: GUIDs or approved strongly-typed
identifier representations, optionally nullable. Arbitrary text, names, email,
DTOs, collections, binary data, raw JSON and custom serializer overrides are
excluded. A module consumer resolves needed state through owner contracts.
Identifiers can still reference people; this is data minimization, not a claim
that payloads contain no personal data. G18's error descriptors and durable-store
erasure inventory remain P02b-2 and P02b-8 respectively.

### Collection, completion and nested refusals

A shared SaveChanges interceptor stages domain events and promotes them only
after successful persistence. A failed/canceled save promotes nothing. Both
API and Seeder compositions install it. The collector preserves source/frame
attribution and the transaction generation; detach/delete and repeated saves
must neither lose nor duplicate an event.

Only the transaction owner completes this sequence:

1. Refuse a failed, read-only-with-produced-work or rollback-only unit.
2. Drain successfully collected domain events sequentially, at one dispatch
   site, before audit composition. At most 256 domain events belong to one
   transaction; exceeding the bound fails the whole unit.
3. Check producer invariants, then write pending MUST audit and commit.
4. Report committed, rolled-back or indeterminate audit durability accurately,
   reconcile at the outer audit frame, and clear execution state exactly once.

Domain-event handlers may translate facts into outbox messages only. They may
not mutate roots, use write stores, send nested requests, raise further domain
events, publish notifications themselves or invoke external providers. Unexpected
enqueue/contract faults throw and roll back; expected business decisions stay
in commands or later integration consumers. The 256 bound is a total count,
not recursive cascade support.

Each producer invocation has a frame with a sticky produced-work receipt. A
successful outbox INSERT or promotion after a successful save records that
receipt in the frame **and all its ancestors**. A `Result.Fail` from any such
frame marks the shared transaction rollback-only **before** its UoW frame is
resolved, even if an ancestor later absorbs the result. Queue drain or cleanup
cannot erase the receipt. Joining never creates a savepoint.

Reads, ordinary event-free refusals, uncollected raises, unsuccessful enqueue
attempts and failed saves do not themselves trigger this additional rule.
Exceptions and unknown database outcomes retain their existing failure posture.
This is a producer consistency guarantee; event-free nested mutations retain
ADR-0040's existing absorbable-failure semantics.

Success, refusal, rollback, cancellation, fault and disposal discard collector
state and pending source lists, preventing replay in another transaction using
the same DI scope. Joiners neither drain nor clear the owner's whole queue.
`OutboxFlushBehavior` keeps step 7's position as an enrollment/invariant guard;
it is not a buffered flush or the domain-event dispatch site.

### Audit ownership outside MediatR

A reusable execution coordinator exposes the audit-frame lifecycle currently
owned by `AuditLogBehavior`; requests preserve their existing classification and
outcome rules. A future delivery owner opens it before handler construction,
classifies the handler type, and refuses an unregistered mutating handler.
Direct one-root writes declare that handler's intents; nested commands retain
their own intents without an extra duplicate business row.

The transaction owner drains events, writes MUST rows, and reports the commit
boundary. Only the outermost audit frame reconciles and clears the capture.
Producer invocation frames remain distinct from audit ownership: each nested
request needs a receipt, while only the owner reconciles. P02b-1 proves the
non-MediatR owner through a controlled fixture; production transport wiring and
inbox atomicity belong to P02b-3. There is no second audit writer in a module.

### First useful production flow

Tenancy publishes `TenantCreatedIntegrationEventV1` on
`learnstack.tenancy.tenant` when `ProvisionTenantCommand` completes the tenant,
default organization and back-reference. This is completed provisioning, not
the first intermediate tenant INSERT. Its partition source is `TenantId`, its
payload needs no extra fields, and its envelope organization is null.

P02b-3 implements two independent Customization subscriptions: one initializes
`card@1`, the other `plain@1`, the defaults already declared by
`BuiltInCustomizations`. Each owns one root, generation change, inbox outcome
and MUST audit on its delivery transaction. One may finish before the other;
retry/recovery resolves an incomplete pair. ADR-0042's provisioning exception
is unchanged; no cross-module write enters the producer transaction.

Initialization is create-only for an untouched built-in concept. Existing
Draft, Active, Deprecated, successor or deliberately deleted definitions are
preserved, never overwritten, republished or resurrected. Customization owns
new UUIDv7 IDs and semantic key/version identity; Tenancy carries neither schema
nor Customization IDs.

P02b-3 also owns the Seeder handoff: discover/verify those built-ins through
module contracts rather than requiring demo-fixed IDs, preserve existing IDs,
retain exact declared IDs for tenant-authored curriculum, and prove concurrent
fresh seeding/delivery convergence. Until then P02b-1 keeps today's seed writes
and emits pending outbox rows; it claims no automatic initialization delivery.
Settings/cache invalidation and an asynchronous Audit mirror are not the sample:
typed settings are uncached, definition readers probe fresh generations, and
source MUST audit already commits with the producer.

## Context

P02b-0 re-verified an existing outbox table and transport seam, but no enqueue
port, collector, domain publisher or useful production consumer. Step 7 cannot
own completion for direct handlers or events dispatched after a command returns.
Provisioning announces a tenant only on the transaction; the Seeder has no
guaranteed Activity. Reading ambient HTTP context alone would reject its event.

The immediate-row choice avoids a second durable buffer. Reusing EventId avoids
two identities and a migration without losing a replay key. Restricting domain
reactions protects returned versions and the one-root write boundary. Automatic
poisoning is deliberately limited to produced work; a general redesign of
nested command failures is outside this decision.

The sample changes default-installation orchestration, not the customization
model. If defaults cease to be a product requirement, G19 needs a replacement
useful subscriber before P02b-3 closes; a test-only or no-op consumer is not one.

## Consequences

- Source state, integration fact and MUST audit share one commit boundary.
- Refused producer work cannot escape through an absorbing outer handler.
- Domain reactions have a deliberately narrow capability set; substantive
  reactions run asynchronously and must tolerate retries and partial readiness.
- Producer memory and payload size are bounded. Exceeding a bound fails the
  operation rather than silently dropping a fact.
- P02b-3 gains real default initialization and a substantive Seeder handoff;
  P02b-1 does not claim consumer or dispatcher completeness.

## Implementation Notes

On approval, accept this record before runtime code and register the canonical
proofs below as Registered, not Implemented. Reconcile the P02b-1 gate vehicles
and their split ownership in the phase package. Add dated navigation/disclosure
notes, preserving Accepted bodies, naming these bounded replacements:

- **ADR-0032:** step 7 becomes an invariant guard; owner completion dispatches
  domain events before MUST audit. Its separate `event_id` column requirement
  is replaced by `id = EventId`.
- **ADR-0040 § Nesting:** only an absorbed `Result.Fail` with frame/descendant
  produced work gains automatic rollback-only. Existing ordinary refusal,
  exception, read-only, setter and transaction-ownership rules survive.
- **ADR-0038:** the existing envelope/serializer/transport contracts survive;
  the identifier-only producer payload policy is added.
- **ADR-0044 and ADR-0033:** common audit entry/owner duties are clarified for
  non-MediatR frames; durability and refusal reconciliation remain unchanged.

Update architecture, Backend/Database/Observability/Audit/Infrastructure
standards, the phase's domain-event target sketch, glossary,
Tenancy/Customization/Audit specs and relevant workflow skills with those
approved contracts. Do not add lease/inbox tables, dispatcher policies, job
runtime, auth, Hub endpoints or provider adapters in P02b-1.

## Architecture Tests

These are **proposed obligations**, not registered or passing tests. On approval
the catalogue owns their names, status, kind and enforcing tests. Reuse existing
entries for event naming/serialization, partition derivation and row correlation;
add these names only for obligations those entries do not already cover:

- `Outbox_Enqueue_Uses_The_Announced_Transaction` — behavioral: resolved and
  unresolved provisioning, mismatch/read-only/stale frame refusals, same role,
  connection and transaction, no-listener trace context and causal metadata.
- `Producer_Failures_Poison_Produced_Work` — behavioral: direct and descendant
  receipts survive drain; absorbed refusal cannot commit; event-free control
  still commits; failed saves do not promote and stale source lists do not replay.
- `Domain_Event_Completion_Is_Owner_Only` — behavioral: FIFO, repeated saves,
  rollback/cancellation, count bound, response version stability and MUST audit
  failure all preserve atomicity; controlled direct owner and nested joiners.
- `Domain_Event_Handlers_Are_Outbox_Only` — structural: complete module census,
  helper/interface traversal, planted forbidden write/send/publish/provider
  paths and clean translation control.
- `Integration_Event_Payloads_Contain_Only_Identifiers` — structural plus
  behavioral: production subjects, disallowed members/serializer overrides
  and runtime-type JSON round-trip. Extend
  `Integration_Event_Declares_PartitionKey` for the declared source/derivation
  and `Outbox_Enqueue_Uses_The_Announced_Transaction` for rollback/atomicity.

## References

- [ADR-0006: Events and Outbox](0006-events-and-outbox.md)
- [ADR-0010: Cross-Module Communication](0010-cross-module-communication.md)
- [ADR-0032: Exception Handling, Logging and Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0033: Audit Durability](0033-audit-durability-model.md)
- [ADR-0038: Cross-Cutting Ports and Events](0038-cross-cutting-port-and-event-contracts.md)
- [ADR-0040: Ambient Unit of Work](0040-ambient-unit-of-work.md)
- [ADR-0042: Bounded Tenant Provisioning](0042-tenant-provisioning-cross-aggregate-transaction.md)
- [ADR-0044: Audit Write Path](0044-audit-write-path.md)
- [Events and Outbox](../architecture/15-event-and-outbox.md)
