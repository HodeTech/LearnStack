# ADR-0057: Transactional Event Production and Completion

## Status

**Proposed — approval pending.** No producer gate is Accepted and no
implementation is authorized by this draft. Approval includes the bounded
supersessions below and the
[P02b-1 decision package](../roadmap/phase-02b-events-auth.md#p02b-1-proposed-decision-package-2026-10-12).

**Bounded supersession on acceptance:** ADR-0032 Sub-decision 2's step-7
completion role and Sub-decision 12 / Architecture Tests' separate `event_id`
column; ADR-0040 § Nesting and Amendment 2's absorbable-`Result.Fail` rule only
for successfully produced work. This new ADR supplies the changed authority.
Those records remain Accepted outside these clauses; this is not an ADR-0041
factual correction.

**Date:** 2026-10-12
**Deciders:** @cemil (approval pending)

## Decision Drivers

- Commit an integration event with its source state and MUST audit under
  [ADR-0006](0006-events-and-outbox.md) and
  [ADR-0033](0033-audit-durability-model.md), including provisioning through the
  Seeder's unresolved ambient context under
  [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md).
- Give requests and future direct deliveries one completion protocol:
  [ADR-0040](0040-ambient-unit-of-work.md#consumers-do-not-have-a-request-and-still-need-all-of-this)
  requires a transaction owner outside MediatR; step 7 cannot flush later work.
- Prevent a refused nested producer from leaving a publishable fact when its
  outer caller absorbs the refusal. Joined frames are not savepoints under
  [ADR-0040 § Nesting](0040-ambient-unit-of-work.md#nesting).
- Establish a useful production subject using the declared tenant defaults;
  [P02b-0's audit](../roadmap/phase-02b-events-auth.md#p02b-0-entry-verification-2026-10-12)
  finds no useful invalidation effect for already fresh or uncached readers.

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
  `IntegrationEventBase.PayloadJsonOptions`. The proposed
  `OutboxPayloadLimits.MaxBytes = 256 * 1024` caps the complete serialized UTF-8
  payload at 262,144 bytes, inclusive; refusal happens before INSERT. This ADR
  selects a conservative application-layer bound, not a measured throughput
  limit. It matches the numerical customization-row ceiling but does not reuse
  `JsonValue.MaxRowBytes`, whose contract belongs to customization. Existing
  outbox DDL has no payload-size CHECK; this proposal adds no migration or claim
  that raw SQL is size-constrained. The stored `jsonb` representation need not
  have the same byte length as the serialized input.
- Each concrete event declares its partition source: its aggregate identifier
  or an explicitly tenant-wide `TenantId`. The key is that identifier's
  canonical GUID representation. Structural controls check the declaration
  and derivation with planted violations; behavioral controls compare actual
  values. This decides no publish-order guarantee; G3's ordering half is P02b-2.

### Transaction metadata and payload boundary

`IUnitOfWork` gains one read-only member in `SharedKernel.Persistence`:

```csharp
TenantContextAnnouncement? GetTenantContextAnnouncement(DbTransaction? transaction);
```

`TenantContextAnnouncement` is an immutable snapshot with `long Generation`,
`TenantId TenantId` and `OrganizationId? OrganizationId`. The generation belongs
to the physical transaction, increases for each new transaction on that unit,
and is not a tenant-context setter. The getter returns a snapshot only after
all announcement SQL succeeds, for the unit's current non-null transaction,
with `IsTenantContextIssuedOn(transaction)` true. It returns null for an absent
or foreign transaction, an unresolved announcement without a usable tenant,
and after setup failure, commit, rollback or disposal. Joiners reuse the same
snapshot and cannot retarget it. Both existing sanctioned announcement methods
capture the normalized values actually sent to PostgreSQL, not a later ambient
context read. Clear the snapshot when a new physical transaction begins and
before a new announcement attempt; a failed/canceled attempt cannot retain
earlier authority. No new session-variable setter is added.

Producer frames capture that generation. Enqueue reads the live snapshot from
its own unit and compares both generation and event tenant, refusing stale
frames even if Npgsql reuses a transaction object, empty/sentinel tenants and
mismatches. It accepts no caller-supplied snapshot as authority. Provisioning
uses its already-sanctioned announcement without populating
`ITenantContextAccessor`; ADR-0036's context-writer set is unchanged.

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

The optional `outbox_messages.metadata` JSONB column is not a producer extension
point: `IOutbox` exposes no arbitrary metadata argument and writes SQL NULL.
This does not suppress the named envelope columns above. Existing stored rows
are not rewritten. Any future use of JSON metadata needs its own accepted
contract for allowed fields, minimization and size; it cannot bypass the payload
policy through a second JSON object.

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
The `IntegrationEventV<N>` suffix follows the existing
[event-authoring convention](../../.claude/skills/add-integration-event/SKILL.md);
unversioned illustrative names in older documents do not introduce a second
convention for new event types.

P02b-3 implements two independent Customization subscriptions: one initializes
`card@1`, the other `plain@1`, the defaults already declared by
`BuiltInCustomizations`. Each business effect owns one root and its generation
change; its inbox outcome and MUST audit must share the delivery transaction.
One may finish before the other; retry/recovery resolves an incomplete pair.
This fixes the two business effects and independent subscriptions, not G1's
inbox table, key, claim owner or organization-delivery policy. P02b-2 must accept
G1, including distinct subscription deduplication identities so one consumer
cannot suppress its sibling, and P02b-3 must reconcile its delivery/audit
contracts before these consumers are implemented. An incompatible G1 choice
reopens this flow's contract before consumer code. ADR-0042's provisioning
exception is unchanged; no cross-module write enters the producer transaction.

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

[P02b-0](../roadmap/phase-02b-events-auth.md#p02b-0-entry-verification-2026-10-12)
re-verified an existing outbox table and transport seam, but no enqueue port,
collector, domain publisher or useful production consumer. ADR-0032's step-7
target predates an implemented producer. It cannot own completion for direct
handlers or events dispatched after a command returns.
[The existing provisioning seam](../../backend/src/LearnStack.SharedKernel/Persistence/IUnitOfWork.cs)
announces a tenant only on the transaction; the Seeder has no guaranteed Activity.
Reading ambient HTTP context alone would reject its event. The new read seam
makes that announcement observable without another authority writer.

The immediate-row choice avoids a second durable buffer. Reusing EventId avoids
two identities and a migration without losing a replay key. Restricting domain
reactions protects returned versions and the one-root write boundary. Automatic
poisoning is deliberately limited to produced work; a general redesign of
nested command failures is outside this decision.

The 256 KiB payload ceiling is an explicit producer budget selected here. The
existing customization-row cap supplies a comparable conservative size, not a
shared schema rule. Identifier-only payloads should remain far smaller; evidence
that a useful event cannot fit requires reconsidering its contract before
raising the budget or copying state into it.

### What would reopen this decision

The sample changes default-installation orchestration, not the customization
model. If defaults cease to be a product requirement, G19 needs a replacement
useful subscriber before P02b-3 closes; a test-only or no-op consumer is not one.
An incompatible G1 delivery choice likewise reopens the consumer contract.
If the shared lifecycle cannot prove successful-save attribution, ancestor
receipts or terminal cleanup, producer implementation must revisit the design
rather than weaken the rollback guarantee. A future need for domain reactions
to write roots requires a new decision about versions and aggregate boundaries.

### Explicitly deferred ownership

P02b-2 owns leases, ordering, type-resolution failures and G1's inbox decision;
P02b-3 owns its concrete consumer/audit implementation and the Seeder handoff.
P02b-8 owns the durable-store erasure inventory. This record adds no broker,
worker, job runtime, cache invalidation or new Hub crossing.

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
and their split ownership in the phase package. The instruments are distinct:

| Affected contract | Instrument on acceptance | Boundary |
|---|---|---|
| ADR-0032 Sub-decision 2, step 7 | Bounded supersession by this new ADR | Owner-side completion replaces the step-7 flush/publication role; the pipeline position survives as an invariant guard |
| ADR-0032 Sub-decision 12 and Architecture Tests table, `event_id` | Bounded supersession by this new ADR | `id = EventId` replaces the separate column requirement in both locations; it is not asserted to have been false at acceptance |
| ADR-0040 § Nesting and Amendment 2 | Bounded supersession by this new ADR | Absorbed failure with frame/descendant produced work poisons the unit; ordinary event-free refusals retain their prior meaning |
| ADR-0040's enumerated seam | Additive, dated seam amendment citing this ADR | Name `GetTenantContextAnnouncement` and its immutable result, lifecycle and generation checks, as Amendment 5 named its boolean reader; no setter or ownership change |
| ADR-0038 producer payload/metadata policy | Additional policy decided by this ADR, with a dated cross-reference | Identifier-only payloads, application byte cap and NULL JSON metadata are new obligations; the envelope, frozen serializer and transport contracts survive |
| ADR-0044 / ADR-0033 common audit entry | Dated clarifying amendments citing this ADR | Non-MediatR owners perform existing classification, MUST-write and outer reconciliation duties; no durability/classification change |

Append dated supersession disclosures in ADR-0032 and ADR-0040 linking the exact
replaced clauses to this ADR, following the bounded-supersession precedent in
[ADR-0054](0054-bounded-public-renderer-admission.md). The disclosures provide
navigation; they do not supply the changed decision. Keep all original bodies,
acceptance metadata and earlier amendments intact. ADR-0040's seam addition is
recorded separately from its nested-failure supersession. None of these changes
uses ADR-0041's false-at-entry erratum or in-place correction instruments.

Update architecture, Backend/Database/Observability/Audit/Infrastructure
standards, the phase's domain-event target sketch, glossary,
Tenancy/Customization/Audit specs and relevant workflow skills with those
approved contracts. Align the event skill's name input with its existing
`EnrollmentCreatedIntegrationEventV1` example (aggregate then past-tense fact),
as well as its producer payload guidance. Do not add lease/inbox tables,
dispatcher policies, job runtime, auth, Hub endpoints or provider adapters in
P02b-1.
P02b-3 also updates `BuiltInCustomizations.cs`'s creator/Seeder/Hub remarks with
the accepted subscription owner and seed handoff when that delivery exists;
P02b-1 must not describe those consumers as already implemented.

## Architecture Tests

These are **proposed obligations**, not registered or passing tests. On approval
the catalogue owns their names, status, kind and enforcing tests. Reuse existing
entries for event naming/serialization, partition derivation and row correlation;
add these names only for obligations those entries do not already cover:

- `Outbox_Enqueue_Uses_The_Announced_Transaction` — behavioral: resolved and
  unresolved provisioning, mismatch/read-only/stale frame refusals, same role,
  connection and transaction, no-listener trace context and causal metadata;
  complete canonical UTF-8 payload at/over the inclusive byte bound, multibyte
  accounting and SQL NULL JSON metadata. Snapshot proofs cover failed setup,
  foreign/reused transaction objects, joins and every terminal boundary.
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
- [ADR-0023: Strongly-Typed IDs](0023-strongly-typed-id-source-generator.md)
- [ADR-0032: Exception Handling, Logging and Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0033: Audit Durability](0033-audit-durability-model.md)
- [ADR-0036: Trusted Tenant Resolution](0036-tenant-resolution-trusted-inputs.md)
- [ADR-0038: Cross-Cutting Ports and Events](0038-cross-cutting-port-and-event-contracts.md)
- [ADR-0040: Ambient Unit of Work](0040-ambient-unit-of-work.md)
- [ADR-0041: Correcting Accepted ADRs](0041-correcting-false-statements-in-accepted-adrs.md)
- [ADR-0042: Bounded Tenant Provisioning](0042-tenant-provisioning-cross-aggregate-transaction.md)
- [ADR-0043: Customization Validation and Generations](0043-customization-payload-validation.md)
- [ADR-0044: Audit Write Path](0044-audit-write-path.md)
- [Events and Outbox](../architecture/15-event-and-outbox.md)
- [Documentation Standards: Correcting and Amending ADRs](../standards/13-documentation.md#correcting-and-amending-adrs)
- [Phase 02b](../roadmap/phase-02b-events-auth.md)
