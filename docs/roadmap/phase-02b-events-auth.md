# Phase 02b: Identity Integration, Session, and Events

## Goal

Give the platform a signed-in user and a durable cross-module event path.

[Phase 02d](phase-02d-walking-skeleton.md) already put two tenants' public sites in a
browser without either. That is why it runs first: an anonymous read path needs a host
resolver, tenant context and Row Level Security — not an identity provider. Everything
after it needs to know who is asking, and needs a state change in one module to reach
another module without a shared table.

Phase 02b delivers three things and corrects four defects that would otherwise be built
on top of:

- **Identity integration** — Keycloak OIDC for the `learnstack` realm, JWKS validation,
  and a BFF cookie session, so that the request pipeline can carry a `UserId` and the
  `AuthorizationBehavior` shell has something to authorize in
  [Phase 03](phase-03-identity-admin.md).
- **The durable outbox dispatcher** over `outbox_messages` — the table itself shipped in
  [Phase 02a Packet 6](phase-02a-kernel-tenancy.md), where its schema and its ownership
  by LearnStack were the one-way door. Dispatch is the additive half, and it lands here.
- **Hangfire background jobs** with a tenant-aware `JobActivator`, so that work outside
  an HTTP request still runs inside a tenant context.

### What this phase is not

**It is not the phase where a transport is chosen.** `IEventBus` and `InProcessEventBus`
shipped in [Phase 02a Packet 5](phase-02a-kernel-tenancy.md) as a **first-class
transport** — the same `IIntegrationEventHandler<T>`, the same `IInboxGuard`, the same
tenant-context restoration as any durable path. Phase 02b adds *durability and
redelivery* on top of that transport; it does not add a second one.

The Dapr pub/sub and Kafka adapters are **not in this phase**. They are demand-gated to
[Phase 11](phase-11-production-hardening.md) by
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md), whose trigger is "a second
process needs to consume an integration event". Until that is true, a cross-process
broker moves an event from one thread to another thread in the same process, through two
network hops and a serialization boundary, for a service the daily loop no longer
starts — Packet 5 moved Kafka, kafka-ui, Valkey, Vault, APISIX and the two Dapr containers
behind the `gated` compose profile, taking `make dev` from fourteen services to
seven. [ADR-0006 Amendment 1](../decisions/0006-events-and-outbox.md) and
[ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md) remain the decisions about **which** transport
LearnStack uses when it needs one; ADR-0035 decides **when**, and the answer is not this
phase.

Because the transport is swappable, everything this phase builds — the outbox row shape,
the claim protocol, the partition key, the inbox guard, the dead-letter contract — is
written against `IEventBus`, not against Dapr. Swapping the implementation later changes
one composition-root registration.

Decisions made or referenced in this phase:

- [ADR-0004 Authentication Strategy](../decisions/0004-authentication-strategy.md)
  (Accepted: self-hosted Keycloak. Amendment 1 adds the `learnstack-hub` realm; that
  realm belongs to the Hub repository and is not built here.)
- [ADR-0006 Events and Outbox](../decisions/0006-events-and-outbox.md)
- [ADR-0010 Cross-Module Communication](../decisions/0010-cross-module-communication.md)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
- [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
  (supersedes ADR-0016)
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)

## Scope

### Durable outbox dispatch

The producer side already exists: a handler calls `IOutbox.EnqueueAsync`, the row is
written in the same `SaveChanges` as the aggregate change, and the Phase 02a Packet 3
`OutboxFlushBehavior` shell lights up here to enrol those messages on a success-`Result`.
What lands in this phase is the consumer side.

- **`OutboxProcessor`** as a `BackgroundService` polling `outbox_messages` with
  `FOR UPDATE SKIP LOCKED`, dispatching each message through `IEventBus`, and marking it
  processed.
- **Retry with exponential backoff** — 1s, 5s, 30s, 5min, 1h — driven by
  `available_after`, with `attempts` and `last_error` recorded on the row.
- **Per-module `inbox_messages` table and `IInboxGuard`.** Every
  `IIntegrationEventHandler<T>` calls `IsAlreadyProcessedAsync` before business logic and
  `MarkAsProcessed` inside the same `SaveChanges` as the business write. Dispatch is
  at-least-once; the inbox is what makes consumption effectively once.
- **Tenant and organization context restored from the envelope** in every handler scope,
  before the inner pipeline runs. A consumer that runs without tenant context writes
  rows that Row Level Security rejects, or worse, does not.
- **Versioned integration event types** in `<Module>.Application.Contracts`, inheriting
  `IntegrationEventBase`, carrying `EventId`, `TenantId`, `OccurredAt` and
  declaring `Topic` and `PartitionKey`. `CorrelationId`, organization, causation and
  causal actor are delivery metadata on `IntegrationEventEnvelope`, copied from the
  outbox row. The `correlation_id` column holds the **full W3C `traceparent` string**,
  not a bare UUID, so a consumer rehydrates the trace with
  `ActivityContext.TryParse(row.CorrelationId, traceState: null, out var parentCtx)` and
  starts its activity from `parentCtx`
  ([ADR-0032 § Sub-decision 12](../decisions/0032-exception-handling-logging-and-observability.md)).
- **A worked sample flow** — a synthetic `PlatformPingedV1` published by one module and
  consumed idempotently by another — so the path is exercised before a real consumer
  depends on it.

#### Correction: the dispatcher's claim is released before the work is done

The dispatcher published in
[Events and Outbox](../architecture/15-event-and-outbox.md) opens a transaction, selects
a batch with `FOR UPDATE SKIP LOCKED`, and then **commits that transaction immediately to
release the row locks** before publishing anything. `SKIP LOCKED` only skips rows that are
locked *right now*. The instant the claiming transaction commits, those rows are unlocked
and still have `processed_at IS NULL`, so a second `OutboxProcessor` — the horizontal
scaling the same document advertises — selects the same batch and publishes every message
a second time.

The document states the opposite as an invariant: *"multiple OutboxProcessor instances
across pods can run concurrently without double-dispatch."* That claim is false as
written, and it is the kind of false claim that is expensive later: consumers are built
against it, and the duplicates only appear under the load that makes them hardest to
diagnose.

This phase fixes the mechanism and deletes the claim. Two implementations are acceptable
and the packet picks one:

- **Hold the claim across the batch.** The selecting transaction stays open until every
  message in the batch is dispatched and marked. Simple and correct; the cost is that a
  long batch holds locks and a crashed processor's rows wait for the transaction to be
  reaped.
- **Add a lease column.** The claim is a durable `UPDATE` that stamps
  `locked_by` / `locked_until` inside a short transaction; dispatch runs outside it, and
  a sweeper reclaims expired leases. More moving parts, but a crash releases work in
  bounded time rather than at connection teardown.

Either way, the guarantee LearnStack states is **at-least-once dispatch with
consumer-side idempotency**, not "exactly once". `IInboxGuard` is not an optimisation
around a rare duplicate — it is the correctness mechanism, and the dispatcher must not
claim to make it redundant.

#### Correction: ordering is promised through partition keys that nothing sets

[Events and Outbox](../architecture/15-event-and-outbox.md) states that per-partition
ordering is preserved "by partition-keying on the aggregate id when ordering is
required". No integration event carries a partition key, `IntegrationEventBase` declares
none, and no publish path sets one. The promise is unenforceable, and a consumer that
depends on seeing `Created` before `Updated` gets whichever order the transport happens
to produce.

Packet 5 has already fixed the port seam; this phase persists and dispatches it:

- `IntegrationEventBase` declares **`PartitionKey` abstract**. Each event chooses the
  aggregate identifier, or explicitly chooses `TenantId` for a tenant-wide fact; there
  is no inherited default that serializes a tenant's whole stream by accident.
- `IOutbox.EnqueueAsync` copies that key to the row. `IEventBus.PublishAsync` accepts an
  `IntegrationEventEnvelope`, whose key forwards `Event.PartitionKey` rather than
  carrying a second value. `InProcessEventBus` serializes handler invocation per key;
  the Phase 11 Kafka adapter maps it to the message key.
- An architecture test asserts every `IIntegrationEvent` resolves a non-null partition
  key, so a new event cannot silently opt out of ordering.

#### Correction: nothing says what happens to a subscriber that exhausts its retries

The corpus describes the **producer** dead-letter path — max attempts, terminal state,
manual review through the `OutboxStatusEndpoints` admin API. It says nothing about the
**consumer** side: a handler that throws on every delivery. Without a written fate, the
default is an infinite redelivery loop that consumes the dispatcher and hides the
failure in log noise.

The subscriber-side contract lands here:

- A handler failure is retried on the same backoff schedule as a dispatch failure, with
  the attempt count tracked **per (event, consumer)** in `inbox_messages` — not per
  event, because one poisoned consumer must not dead-letter an event that four other
  consumers handled cleanly.
- After the maximum attempts, the `(event, consumer)` pair moves to a terminal
  `dead_lettered` state with the last exception recorded. Redelivery stops.
- Dead-lettering emits a metric and writes a MUST-class audit entry through
  [ADR-0033](../decisions/0033-audit-durability-model.md)'s durable path — a silently
  dropped integration event is a data-integrity event, not a log line.
- Replay is an explicit operator action through the admin API, and it re-enters the
  normal inbox-guarded path.

#### Observability

- `learnstack_outbox_pending_count{tenant_id}` (gauge) and dispatch-latency histogram.
- `learnstack_outbox_dispatch_failed_total{event_type}` and
  `learnstack_inbox_dead_lettered_total{module, event_type}` (counters).
- The dispatcher runs as a recurring job with its pending count and lag surfaced as
  metrics, so a stalled dispatcher is visible without reading the table.

Cross-instance L1 cache invalidation (`learnstack.cache.invalidation`) lands with the
distributed adapter in Phase 11. Declaring or consuming it in this single-instance phase
would provide no cross-instance effect — which is precisely
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger for the
distributed `ICacheService` adapter in [Phase 11](phase-11-production-hardening.md).
This phase therefore neither declares the topic nor consumes it; the adapter that gives
it an effect brings its own subscription.

### Background jobs

- Hangfire wired against PostgreSQL storage, with a queue-naming policy.
- A tenant-aware `JobActivator` that restores `ITenantContext` from the payload before
  any work runs, populating the same singleton `ITenantContextAccessor` the HTTP path
  uses.
- Job payloads without `tenant_id` and `correlation_id` **fail at enqueue time**, not at
  activation. The failure is loud and belongs to the caller who can fix it.
- Poisoned-job handling matches the outbox dead-letter contract above: bounded retries,
  a terminal state, a metric, and an operator-initiated replay.
- The `OutboxProcessor` runs under this scheduler rather than as a bare loop, so its
  liveness is observable through the same surface as every other recurring job.

### Identity integration — the `learnstack` realm

The LearnStack identity **domain** (`User`, `Membership`, `Role`, `Permission`,
`Invitation`) lands in [Phase 03](phase-03-identity-admin.md). This phase delivers only
the authentication plumbing, for the tenant-facing `learnstack` realm. The
`learnstack-hub` operator realm belongs to the Hub repository and is referenced from
[Phase 02c](phase-02c-hub-foundation.md).

- Keycloak realm configuration for `learnstack`, with the seed users a development
  environment needs: a platform admin plus two tenant admins per seed tenant, across both
  organizations of each tenant, idempotent under `make seed`.
- OIDC Authorization Code flow with PKCE against the .NET API: JWT validation against the
  Keycloak JWKS endpoint, with key caching and rotation handling.
- A BFF callback handler in `frontend/apps/web` establishing the cookie session
  (`HttpOnly`, `Secure`, `SameSite=Lax`). Silent refresh runs through the BFF; the
  browser never holds a refresh token.
- Access-token TTL capped at one hour, with the refresh path tested end to end.
- **Tenant and organization claim cross-check.** A request whose host-derived
  `(tenant_id, organization_id)` disagrees with the JWT claims returns 404 — not 403,
  which would confirm the resource exists — and writes an audit entry.

LearnStack does not implement password hashing, password-reset rendering, MFA enrolment,
or refresh-token rotation. Those live in Keycloak; the split is described in
[Identity and Auth](../architecture/13-identity-and-auth.md).

#### Correction: the realm emits no `tenant_id` claim at all

The cross-check above is the **first** layer of tenant defense-in-depth
([ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md)), and today it cannot
run. `infra/keycloak/realms/learnstack.json` stores `tenant_id` as a **user attribute**
and declares **zero protocol mappers**. Keycloak does not put user attributes into tokens
unless a mapper says so, so every issued JWT reaches the API with no `tenant_id` claim.
Any code that reads one reads `null`, and a cross-check against `null` either fails every
request or — the likelier outcome under delivery pressure — is written to skip when the
claim is absent, which turns the whole layer off.

Two changes, both in the realm import:

- Add an **`oidc-usermodel-attribute-mapper`** on the `learnstack-api` client (or on a
  dedicated client scope shared by `learnstack-api` and `learnstack-web`) that projects
  the `tenant_id` user attribute into the access token and the ID token, with
  `access.token.claim` and `id.token.claim` both enabled, typed as `String`. The same
  treatment applies to `organization_id` once memberships exist in
  [Phase 03](phase-03-identity-admin.md).
- Change the seeded value from the slug `"tenant-a"` to the seed tenant's **UUID**. The
  claim is consumed as a `TenantId` — a Vogen value object over `Guid` introduced in
  [Phase 02a Packet 6](phase-02a-kernel-tenancy.md) — so a slug cannot be parsed and the
  cross-check throws instead of comparing. The seed script and the realm import read the
  same identifiers, so the two cannot drift.

An integration test asserts the shape rather than the configuration file: a token issued
by the dev realm for a seed user carries a `tenant_id` claim that parses to the seed
tenant's `TenantId`. A realm export that loses the mapper fails the test.

### Audit coverage wiring

The audit pipeline itself lit up in [Phase 02a Packet 9](phase-02a-kernel-tenancy.md)
under [ADR-0033](../decisions/0033-audit-durability-model.md). This phase adds its
event-stream feeds:

- Outbox and inbox dead-letter transitions write through `IAuditStore`.
- Keycloak-mirrored identity events (`user.created`, `password.reset.requested`) arrive
  as `learnstack.identity.user` integration events and are consumed by the Audit module's
  handlers.
- Platform-admin scope entries opened by authentication-related operations get the same
  treatment.

The `AuditEntry` aggregate is owned by the **Audit** module and shipped in Phase 02a;
[Phase 03](phase-03-identity-admin.md) plugs the Identity domain into it, not the reverse.
See [Audit Coverage Standards](../standards/18-audit-coverage.md).

### Compile-time secret leakage (carried out of Phase 02a Packet 3)

Packet 3 shipped runtime redaction — `RedactSensitiveFieldsEnricher` scrubs sensitive
tokens from log events, and `LocalFileErrorTracker` scrubs the same set from error
envelopes, both through the single `SensitiveTokenCatalog`. Runtime redaction covers
logs, OTLP and error-tracker tags. It does not cover an exception **message**, because
by the time the string exists the secret is already inside it, and a Problem Details
response can carry it to a client.

This phase adds the compile-time complement: a second diagnostic in
`LearnStack.Analyzers`, id **`LS0002`**, that flags interpolated or concatenated
`throw new …Exception($"…{token}…")` patterns in `Domain` and `Application` where the
interpolated symbol's name matches `SensitiveTokenCatalog`. It ships as a Warning listed
in `WarningsNotAsErrors`, escalating to Error alongside `LS0001` at the
[Phase 03](phase-03-identity-admin.md) exit — the same cadence, for the same reason:
a rule that breaks the build before its violations are cleaned up gets suppressed rather
than obeyed.

The analyzer's own tests run it over synthetic compilations and assert that `LS0002` is
reported and no `AD0001` crash occurs — the failure mode
[ADR-0032 Amendment 1](../decisions/0032-exception-handling-logging-and-observability.md)
records for `LS0001`.

### Architecture tests

New rules this phase introduces, in addition to the Phase 02a set:

- `Integration_Events_Inherit_From_IntegrationEventBase` — every `IIntegrationEvent` is a
  JSON-serialisable record extending the base.
- `Integration_Event_Handlers_Use_InboxGuard` — every consumer calls
  `IInboxGuard.IsAlreadyProcessedAsync` before business logic.
- `Integration_Event_Handler_Restores_Tenant_Context` — the handler scope has
  `ITenantContext.IsResolved == true` before the inner pipeline runs.
- `Outbox_Row_Carries_Correlation_Context` — every persisted row has non-null `tenant_id`
  and `correlation_id`.
- `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` — one poisoned message does not
  prevent the rest of its batch from processing.
- `Outbox_Claim_IsHeld_Until_Dispatch_Completes` — **new.** Two concurrent processors over
  one pending batch dispatch each message exactly once. This is the test that would have
  caught the defect above, and its absence is why the false invariant survived review.
- `Integration_Event_Declares_PartitionKey` — **new.** Every `IIntegrationEvent` resolves
  a non-null partition key.
- `Hangfire_Job_Payloads_Include_TenantId` — enqueue rejects payloads missing `tenant_id`
  or `correlation_id`.
- Read-model tables follow the `public_<module>_<concept>` naming.
- Provider SDK types (Keycloak, Kafka, Vault, LiveKit, Stripe) are not imported in
  `Domain` or `Application`.
- Outbox writes happen inside the same transaction as the originating domain change.

`Integration_Event_TopicNames_FollowConvention` already enforces each event's declared
topic independently of transport, including the Hub-only four-segment form.
`Dapr_PubSub_TopicNames_FollowConvention` narrows to component bindings and activates
with the Dapr adapter in [Phase 11](phase-11-production-hardening.md).

The catalogue in
[Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) is the
canonical reference for every identifier above, including the two new rows; this list is
a Phase 02b shipping checklist.

## Deliverables

- A durable `OutboxProcessor` with a claim protocol that survives two concurrent
  instances, exponential backoff, and a producer-side dead-letter state.
- A subscriber-side dead-letter contract: per-`(event, consumer)` attempt tracking, a
  terminal state, an audit entry, a metric, and operator-initiated replay.
- `PartitionKey` on `IntegrationEventBase`, threaded through `IEventBus` and honoured by
  `InProcessEventBus`.
- Per-module `inbox_messages` tables and `IInboxGuard`, with tenant-context restoration
  in every handler scope.
- A worked end-to-end sample flow: publish → outbox → dispatch → idempotent consumption.
- Hangfire on PostgreSQL storage with a tenant-aware `JobActivator` and an enqueue-time
  guard.
- Keycloak `learnstack` realm with a working `oidc-usermodel-attribute-mapper` for
  `tenant_id`, UUID-valued seed attributes, and idempotent seed users.
- OIDC PKCE login through the BFF, JWKS validation with rotation handling, cookie session,
  and silent refresh.
- Host-vs-JWT tenant cross-check returning 404 and writing an audit entry.
- `LS0002` analyzer shipping as a Warning, with its own analyzer tests.
- Corrections landed in [Events and Outbox](../architecture/15-event-and-outbox.md): the
  false "no double dispatch" invariant deleted, the claim protocol described as
  implemented, the partition-key requirement made concrete, and the subscriber dead-letter
  fate written down.

## Completion Criteria

- A sample integration event travels publish → outbox → dispatch → consumer and is
  handled exactly once, with the consumer's business write and its inbox marker committing
  together.
- Two `OutboxProcessor` instances running against one pending batch dispatch every message
  exactly once; the test that proves it is in CI.
- Two events for the same aggregate arrive at their consumer in publish order.
- A handler that throws on every delivery reaches a terminal `dead_lettered` state for
  that consumer only, leaves other consumers of the same event unaffected, emits a metric,
  and produces an audit entry.
- A failed dispatch retries on the documented backoff and dead-letters cleanly at the
  maximum attempt.
- A background job enqueued without a tenant id fails at enqueue time.
- A login against the dev Keycloak realm with a seed user completes through the BFF; the
  JWT validates against JWKS; refresh works; and the token **carries a `tenant_id` claim
  that parses to the seed tenant's `TenantId`**.
- A request whose host-derived tenant disagrees with its JWT tenant returns 404 and writes
  an audit entry.
- `LS0002` reports on a synthetic violation and produces no `AD0001`.
- The full architecture test suite is green and not skippable.

## Risks

- **The claim fix is treated as a detail.** Both candidate implementations are a few
  dozen lines, which makes them easy to defer past a green build — single-instance
  development never reproduces the failure. The concurrency test is the mitigation, and
  it lands with the fix, not after it.
- **The partition key is added but never meaningful.** A key derived from the event's own
  `EventId`, or one defaulted per event type, satisfies the architecture test and orders
  nothing. Reviewers check that the key is the aggregate identifier.
- **Dead-lettering becomes invisible.** A terminal state that only appears in a database
  column is a silent data loss. The metric and the MUST-class audit entry are part of the
  deliverable, not follow-up work.
- **The Keycloak mapper regresses on the next realm export.** Realm JSON is regenerated by
  hand and by the Keycloak admin console, and a mapper is easy to lose. The integration
  test asserts the issued token, not the file.
- **Tenant context is forgotten in background jobs.** Mitigated by the `JobActivator` plus
  the enqueue-time guard, and by the architecture test that covers both.
- **The outbox is treated as optional** — "module X will adopt it later". Mitigated by the
  architecture test requiring same-transaction outbox writes, which fails the moment a
  module publishes outside its unit of work.
- **Keycloak becomes a sign-in bottleneck.** Mitigated by PostgreSQL-backed Keycloak
  storage and JWKS caching from the start.
- **Keycloak claims leak into authorization decisions.** Keycloak authenticates;
  LearnStack authorizes. The split is in
  [Identity and Auth](../architecture/13-identity-and-auth.md), and
  [Phase 03](phase-03-identity-admin.md) is where the LearnStack side is built.

## Phase Exit Decision

[Phase 03](phase-03-identity-admin.md) begins when a seed user can sign in through the
BFF against the dev realm with a `tenant_id` claim that resolves to a real `TenantId`;
when the sample integration event flows publish → outbox → dispatch → idempotent
consumption under two concurrent dispatchers with no duplicate handling; when a poisoned
consumer dead-letters visibly instead of looping; and when the background-job tenant
guard and the full architecture suite are green in CI.

[Phase 02c](phase-02c-hub-foundation.md) is unblocked by this phase but does not gate it.
Phase 02c hangs off the spine and starts only when its own trigger fires — a tenant that
must be billed or plan-gated
([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)).
