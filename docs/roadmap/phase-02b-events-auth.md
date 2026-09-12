# Phase 02b: Events, Background Jobs, Identity, and Session

## Goal

Give the platform a signed-in user, a durable cross-module event path, and a place for
work that runs outside an HTTP request.

[Phase 02d](phase-02d-walking-skeleton.md) puts two tenants' public sites in a browser
without any of the three, and it runs first: an anonymous read path needs a host
resolver, tenant context and Row Level Security, not an identity provider. Everything
after it needs to know who is asking, and needs a state change in one module to reach
another module without a shared table.

Six subsystems land here — the outbox producer, the dispatcher, the consumer side,
background jobs, the API's authentication, and the BFF session. That is wider than any
Phase 02a packet, and several of the choices it needs are one-way doors that later
phases already depend on. § Packets and decision gates sequences them and names what
each one waits on.

### What this phase inherits

Most of the contract this phase implements is already written. Reading it first is what
keeps the phase from re-deciding settled questions or repairing repaired defects.

- **The outbox table** — its canonical DDL, `partition_key`, `correlation_id` holding
  the full W3C `traceparent`, and its rows in the grant matrix — shipped in
  [Phase 02a Packet 6](phase-02a-kernel-tenancy.md#delivery-record-packet-6) and lives
  in exactly one place,
  [Database Standards § Outbox](../standards/05-database.md#outbox).
- **The event contract** — `IntegrationEventBase` with `Topic` and **`PartitionKey`
  abstract**, so no event inherits a default; `IntegrationEventEnvelope` validating the
  traceparent and reading the key off the event rather than carrying a second copy;
  `InProcessEventBus` serialising handler invocation per key — shipped in
  [Packet 5](phase-02a-kernel-tenancy.md#delivery-record-packet-5) under
  [ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md).
- **The claim protocol.**
  [Events and Outbox § The claim protocol](../architecture/15-event-and-outbox.md#the-claim-protocol)
  specifies the lease, and
  [§ The simpler alternative, and its cost](../architecture/15-event-and-outbox.md#the-simpler-alternative-and-its-cost)
  demotes the batch-held transaction to a fallback for a deployment that cannot add the
  two columns. [ADR-0006 Amendment 2](../decisions/0006-events-and-outbox.md) already
  assigns `locked_by` / `locked_until` and the matching `learnstack_outbox_admin`
  column-grant extension to this phase's migration. This phase **implements** that
  design; it does not choose between candidates.
- **The delivery contract.** At-least-once dispatch with consumer-side idempotency.
  [§ The claim problem](../architecture/15-event-and-outbox.md#the-claim-problem)
  already withdrew the "no double dispatch" claim, and
  [§ What the processor guarantees](../architecture/15-event-and-outbox.md#what-the-processor-guarantees)
  bounds duplicates to crash recovery and lease expiry.
- **The ambient unit of work**, and the delivery shape
  [ADR-0040 § Consumers do not have a request](../decisions/0040-ambient-unit-of-work.md#consumers-do-not-have-a-request-and-still-need-all-of-this)
  assigns to this phase by name: `BeginTransactionAsync` → `SetTenantContextAsync` →
  handler → commit, per delivery.
- **The audit write path** — `AuditLogBehavior` classifying at step 3,
  `TransactionBehavior` flushing MUST-class rows immediately before `COMMIT`,
  `IAuditStore`, the merged `IAuditCatalog` — shipped in
  [Packet 9](phase-02a-kernel-tenancy.md#delivery-record-packet-9) under
  [ADR-0033](../decisions/0033-audit-durability-model.md) and
  [ADR-0044](../decisions/0044-audit-write-path.md). This phase adds the entry points it
  does not yet cover.

What is genuinely absent: `IOutbox`, `IInboxGuard`, `inbox_messages`, the lease columns,
`OutboxProcessor`, any `BackgroundService` or `IHostedService`, Hangfire and its package
pins, the realm's protocol mappers, and a body for `OutboxFlushBehavior` — still
`return next()` behind a Phase 02b `TODO`. Packet 5 shipped the `IInboxGuard` **seam**,
which is the name in three comments — two XML doc comments in `SharedKernel.Messaging`
and the composition root's transport `TODO` — and in no declaration; the guard, its
table and its semantics are this phase's.

### What this phase is not

**It is not the phase where a transport is chosen.** `IEventBus` and `InProcessEventBus`
shipped in [Phase 02a Packet 5](phase-02a-kernel-tenancy.md) as a **first-class
transport** — the same `IIntegrationEventHandler<T>`, the same `IInboxGuard` seam, the
same tenant-context restoration as any durable path. Phase 02b adds *durability and
redelivery* on top of that transport; it does not add a second one.

The Dapr pub/sub and Kafka adapters are **not in this phase**. Both are demand-gated to
[Phase 11](phase-11-production-hardening.md) by
[ADR-0035 § The gated set](../decisions/0035-demand-gated-infrastructure.md#the-gated-set),
and they carry *different* triggers there: Dapr's is "a second process needs to consume
an integration event", Kafka's is "event volume, replay, or ordering across processes is
required". Until one of those is true, a cross-process broker moves an event from one
thread to another thread in the same process, through two network hops and a
serialization boundary, for a service the daily loop no longer starts — Packet 5 moved
Kafka, kafka-ui, Valkey, Vault, APISIX and the two Dapr containers behind the `gated`
compose profile, taking `make dev` from fourteen services to seven.
[ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md) and
[ADR-0006](../decisions/0006-events-and-outbox.md) as amended remain the decisions about
**which** transport LearnStack uses when it needs one; ADR-0035 decides **when**, and
the answer is not this phase.

Because the transport is swappable, everything this phase builds is written against
`IEventBus`, not against Dapr. Swapping the implementation later changes one
composition-root registration.

### Explicitly not in this phase

Each item below is named somewhere as adjacent to this phase's work. Each has an owner.

- **An operator-facing recovery API.** This phase ships replay as a dispatcher-level
  operation with its tests (§ The consumer side). The route space, the Platform-scope
  permission key, the cross-tenant read and the runbook land in
  [Phase 03](phase-03-identity-admin.md) beside the killswitch toggle, on the
  [ADR-0045 Amendment 1](../decisions/0045-entitlement-and-feature-flag-socket.md)
  precedent — `EnterPlatformAdminScope`'s registered gate is `DenyAllPlatformAdminGate`,
  which refuses everyone until then. Whether a platform URL space exists at all is a
  question
  [ADR-0036 § The platform-admin override is not a resolution source](../decisions/0036-tenant-resolution-trusted-inputs.md#the-platform-admin-override-is-not-a-resolution-source)
  says needs its own decision record.
- **The Hangfire dashboard.** No route is mapped in any deployment mode here.
  [Phase 03](phase-03-identity-admin.md) maps it behind the Platform-scope permission
  and carries the [ADR-0015](../decisions/0015-api-gateway-apisix.md) amendment its
  `platform-admin` role check owes;
  [Phase 11](phase-11-production-hardening.md#security) carries the APISIX half.
- **Job definitions.** This phase ships the runner, its tenant contract and its enqueue
  guard, and registers no job. The first jobs arrive in
  [Phase 04](phase-04-cms-media-pages.md) and
  [Phase 08a](phase-08a-assessment-notifications.md).
- **The Keycloak-mirrored identity-event feed.** `user.created` and
  `password.reset.requested` need a producer, an authenticated ingress and an Identity
  module, all of which [Phase 03](phase-03-identity-admin.md) owns; the Audit module
  spec records that it consumes nothing and has no inbox today.
- **`GET /readyz` and the deployment-level unhealthy backstop.** The `outbox` health
  check this phase registers follows `audit`'s precedent in
  [Observability Standards § Health checks](../standards/10-observability.md#health-checks):
  registered, not mapped. [Phase 11](phase-11-production-hardening.md#reliability) owns
  the readiness surface.
- **Purging processed outbox rows and aged inbox rows.**
  [Phase 11 § Data Operations](phase-11-production-hardening.md#data-operations) owns
  it. This phase is the one that starts filling both tables, so it records the
  constraint — the `DELETE` grant belongs to `learnstack_platform`, whose only entry
  takes a reason and no actor, so a recurring job has no principal to present — and G18
  registers the question.
- **Cross-instance L1 cache invalidation** (`learnstack.cache.invalidation`). Port
  `ICacheService`, default implementation `InMemoryCacheService`, owning phase
  [Phase 11](phase-11-production-hardening.md), trigger "more than one application
  instance runs concurrently"
  ([ADR-0035 § The gated set](../decisions/0035-demand-gated-infrastructure.md#the-gated-set)).
  This phase neither declares the topic nor consumes it; the adapter that gives it an
  effect brings its own subscription.
- **Tenant status as a gate on asynchronous work.** Both entry points this phase adds
  take their tenant from a payload written earlier and consult nothing about that tenant
  now, so a pending outbox row or a queued job for a suspended or archived tenant still
  executes. The HTTP path's switch is the host mapping, which has no asynchronous
  equivalent. [Phase 02c](phase-02c-hub-foundation.md) owns the status transition and
  decides what it does to work already in flight — hold, drop, or dead-letter — beside
  the termination question it already carries.
- **The documentation-lint CI promotion.** [Phase 02a](phase-02a-kernel-tenancy.md)'s
  Packet 3b record assigns the XML-documentation link check and the prose-width check to
  this phase, on the trigger that a second contributor gains write access. The trigger
  has not fired; `P02b-8` picks them up if it does, and otherwise they travel to the
  phase that inherits them.
- **Browser end-to-end tests.**
  [Testing Standards § End-to-End Tests](../standards/06-testing.md#end-to-end-tests)
  assigns Playwright over a running stack to
  [Phase 06](phase-06-renderer-admin-studio.md). This phase proves its browser-facing
  claims with route-level tests in the `frontend` job and container-backed token tests
  in `backend-integration`, and says so rather than writing "tested end to end".
- **Production realm provisioning.** Every realm change this phase makes is a
  requirement on the production identity definition
  [Phase 11](phase-11-production-hardening.md) owns; the changes here land in the dev
  import and its reconciler.

Decisions made or referenced in this phase:

- [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
  (the four-role model the dispatcher credential and any job storage sit inside)
- [ADR-0004 Authentication Strategy](../decisions/0004-authentication-strategy.md)
  (Accepted: self-hosted Keycloak. Amendment 1 adds the `learnstack-hub` realm and makes
  the operator / tenant population split a hard invariant. The dev export of both realms
  lives in this repository under `infra/keycloak/realms/`; the Hub repository owns that
  realm's *consumer* and its production provisioning.)
- [ADR-0006 Events and Outbox](../decisions/0006-events-and-outbox.md) (as amended —
  Amendment 2 presupposes the lease)
- [ADR-0010 Cross-Module Communication](../decisions/0010-cross-module-communication.md)
- [ADR-0015 API Gateway (APISIX)](../decisions/0015-api-gateway-apisix.md) (the Hangfire
  dashboard gate, referenced and deferred)
- [ADR-0017 Tenant / Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md)
- [ADR-0023 Strongly-Typed Id Source Generator](../decisions/0023-strongly-typed-id-source-generator.md)
  (whose DB-side minting list names `inbox_messages`, which Database Standards
  contradicts — G1 carries the erratum)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
- [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
  (supersedes ADR-0016)
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)
- [ADR-0036 Tenant Resolution and Trusted Inputs](../decisions/0036-tenant-resolution-trusted-inputs.md)
  (which stages authentication into this phase by name, and delegates the claim shape to
  it by name)
- [ADR-0038 Cross-Cutting Port and Event Contracts](../decisions/0038-cross-cutting-port-and-event-contracts.md)
- [ADR-0040 Ambient Unit of Work](../decisions/0040-ambient-unit-of-work.md)
- [ADR-0044 Audit Write Path](../decisions/0044-audit-write-path.md)
- [ADR-0045 Entitlement and Feature-Flag Socket](../decisions/0045-entitlement-and-feature-flag-socket.md)
  (the killswitch precedent for a mechanism shipped without its operator surface)
- Two reserved, undrafted records this phase must Accept before its dependent packets
  start: **ADR-0046** (event delivery, the inbox and the subscriber failure contract)
  and **ADR-0047** (the background-job runtime), both registered in
  [decisions/README § Open ADR Drafts](../decisions/README.md#open-adr-drafts).

## Scope

### Packets and decision gates

Phase 02a needed twelve packets for a narrower scope. This phase declares nine, in
dependency order. `P02b-0` writes no code.

| Packet | Contents | Cannot start until |
|---|---|---|
| **P02b-0** | Decisions only: the two reserved ADRs drafted and Accepted, the dated amendments the gates below name, Database Standards § Inbox, the catalogue rows for every rule this phase introduces, and the glossary headwords its mechanisms need — `inbox`, outbox lease, dead letter, audit frame, job activator, integration-event consumer, each written once the gate that fixes its meaning closes | — |
| **P02b-1** | Producer: `IOutbox`, the `OutboxFlushBehavior` body, the domain-event collector and its single dispatch site, and the first event with its enqueue path | G2, G6, G19 Accepted |
| **P02b-2** | Dispatcher: the lease migration and grant extension, claim / publish / mark, backoff, the producer terminal state, the credential and its boot guard, the metrics and the health check | G3, G4, G5, G8, G18 Accepted; P02b-1 |
| **P02b-3** | Consumer: the inbox DDL, `IInboxGuard`, the per-delivery transaction, the sample flow's consumer, per-subscription attempts, the dead-letter store and its audit row, replay as a dispatcher operation | G1, G6, G7 Accepted; P02b-2 |
| **P02b-4** | Background jobs: the runner, its storage, the tenant-context hook, the enqueue guard, no job definition | G9 Accepted |
| **P02b-5** | API authentication: JwtBearer wiring and its insertion order, claim population, the host/JWT cross-check through the existing recorder, token-keyed rate limits, the 401 rows, one authenticated endpoint | G10, G11, G14, G20 Accepted |
| **P02b-6** | Realm and seed: the shared client scope, the mappers, the user-profile declaration, UUID attributes agreeing with `SeedData`, the warm-realm reconciler, the Keycloak test fixture | G12 Accepted; P02b-5 |
| **P02b-7** | BFF session: Auth.js, the cookie session, single-flight refresh, CSRF, logout, the studio guard, the SDK's bearer attachment | G13 Accepted; P02b-6 |
| **P02b-8** | `LS0002`, the carrier reconciliations this phase owes, the branch-protection edit that makes the behavioural check gate, and the exit checks | G16, G17, G18 Accepted |

The events packets precede the identity packets because the Hub's `P02c-3` waits on this
phase's events half ([Phase 02c](phase-02c-hub-foundation.md)), not on its session half.

#### The decision register

Each row is a question this phase must answer before the packet it blocks starts. The
**Leaning** column records the reviewers' consolidated proposal and is **not** a
decision; the **Vehicle** column is what this repository's own rules require for the
answer to count. A gate whose vehicle is an amendment is Accepted **before** the code it
governs is written, not alongside it.

| # | Question | Leaning | Vehicle | Blocks |
|---|---|---|---|---|
| G1 | What is the subscriber's fate when its handler exhausts retries, and what is the inbox table — one shared or per module, keyed `event_id` or `(tenant_id, consumer, event_id)`, claimed by the handler or by the transport? Three arms travel with it: the class of consumer whose effect cannot share the inbox transaction (an external call, the platform-scope erasure handler the Audit spec already carries) and what "effectively once" means for it; whether an event without the organization marker is delivered tenant-wide regardless of the producing request's organization; and whether the inbox entity joins [ADR-0044 § 7](../decisions/0044-audit-write-path.md)'s named audit-capture exclusion list once a delivery declares intents | One shared tenant-owned `inbox_messages`, transport-claimed; the terminal state in its own store so "the inbox row is not marked processed" stays true | **ADR-0046** + a dated **ADR-0006 Amendment** (or an explicit supersession note in ADR-0046 naming Amendment 1's consumer-side paragraph, which fixes the per-module inbox) + Database Standards § Inbox + an ADR-0041 erratum beside ADR-0023's minting list | P02b-3 |
| G2 | Does `IOutbox.EnqueueAsync` write the row immediately on the ambient transaction, or buffer for `OutboxFlushBehavior` at step 7 — and what is the flush point for the two paths that have no step 7? | Immediate write, with the behavior asserting no buffered message survives the handler | Phase-doc statement + [Events and Outbox § Producer pattern](../architecture/15-event-and-outbox.md#producer-pattern); a dated ADR-0032 Amendment if step 7 stops being a flush point | P02b-1 |
| G3 | What replaces the unconditional same-key publish-order promise — a scoped bound, or strict per-key head-of-line blocking — and is the key's derivation checkable structurally, given that [§ Ordering](../architecture/15-event-and-outbox.md#ordering) admits an aggregate id or a deliberately declared tenant-wide key and nothing else? | Scope it: no column in `outbox_messages` carries an unconditional per-key guarantee, so the honest bound is one processor, no failure, enqueue order | Scoping is a phase-doc plus [§ Ordering](../architecture/15-event-and-outbox.md#ordering) edit; blocking is a dated ADR-0006 Amendment plus an index | P02b-2 |
| G4 | What counts as an attempt, what is `MaxAttempts` bound to, and what marker keeps a terminal row terminal as the clock advances? | Six attempts (one immediate plus the five documented delays) and an explicit terminal column, because a pushed-out `available_after` stops being terminal and never leaves the pending gauge | Dated ADR-0006 Amendment + the Database Standards § Outbox DDL, index and grant edits riding it | P02b-2 |
| G5 | Does the stored event identity stay the assembly-qualified type name with declared compatibility mappings, and what happens to an unresolvable type, a retired type and an event with no subscriber? And what is the **row's** identity — a surrogate `id` plus an `event_id` column, or `EventId` as the key — given that two documents list an `event_id` the canonical DDL does not have? | Keep the type identity, add a bounded resolver reading through `PayloadJsonOptions` and declared mappings; dead-letter a retired type at once rather than spending its attempt budget; reconcile the row's column list in one direction | Phase-doc + Database Standards § Outbox if kept; a dated ADR-0006 Amendment if the type identity is replaced, and a dated ADR-0032 Amendment if the `event_id` column is withdrawn from its § Sub-decision 12 list | P02b-2 |
| G6 | When the unit-of-work owner is not an `AuditLogBehavior`, who performs the four owner-side audit acts and enters the outermost audit frame — and does a state-mutating delivery declare its own intents, keyed by handler type? | The transport opens the outermost frame beside its transaction; a delivery declares its own intents and an unregistered handler is refused as an unregistered request is | One dated diff: **ADR-0044 Amendment 8**, carried into **ADR-0033 Amendment 8** and **ADR-0040 Amendment 8** | P02b-1, P02b-3 |
| G7 | For a dead-letter transition: what class, what `OperationType`, what slug, on which transaction, in what order against the state change, with what failure posture — and may a second non-MediatR caller reach `IAuditStore.WriteStandaloneAsync`? | class MUST, `OperationType` `security-event`, the slug open under the § The join grammar `{module}.{resource}.{verb}`; the row written before the transition, so a failed write leaves the transition unmade and the next poll retries | Dated ADR-0033 Amendment + `DeclareOffPath` registrations + the Baseline row in [Audit Coverage Standards](../standards/18-audit-coverage.md#baseline-coverage-learnstack-core-modules) | P02b-3 |
| G8 | Does the dispatcher's `BYPASSRLS` use carry a per-invocation `security-event` row, or is it exempt — and on what written terms? | Exempt, on three terms: the bypass is grant-bounded and code-path-confined, dispatch attempts are logged rather than audited, and the dead-letter transitions are the audited events | Dated ADR-0006 or ADR-0003 Amendment; an architecture document cannot grant it | P02b-2 |
| G9 | Which record decides the background-job runtime — its storage under the closed four-role model, the single tenant-context writer, the job frame's transaction owner, the enqueue-site rule *and who may enqueue whose job type*, the retry contract (whose class, slug and posture adopt **G7**'s), the tenantless platform-job class a purge needs, the ADR-0035 classification, the queue grammar, and the package pins with their licence verdict? | One record answering all of it; a job reaches the database only through `ISender`, which leaves ADR-0040's closed setter set at eight | **ADR-0047** + a dated ADR-0003 Amendment for the storage + a dated **ADR-0036 Amendment** (or an erratum) if the job-path writer is not the `JobActivator` its § Rules names in the closed four-caller set, then Standards 05 and Standards 20 | P02b-4 |
| G10 | Does the active tenant travel as a scalar `tenant_id` claim, a `memberships` array, or both — and what value does `UserId` hold? | The scalar claim, whose 02b source is an admin-set user attribute; a second UUID-valued `user_id` attribute for the actor, with `sub` staying Keycloak's subject | Phase-doc: [ADR-0036 § What is out of scope](../decisions/0036-tenant-resolution-trusted-inputs.md#what-is-out-of-scope-and-what-is-not) hands the shape to this phase by name. Reconcile `architecture/13`'s array sketch in the same change | P02b-5 |
| G11 | Which [ADR-0036 matrix](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix) rows resolve in this phase, given that row 10 consults a reader that denies everyone — and what row covers a signature-valid token carrying no `tenant_id` claim on a tenant host? | Either mint `organization_id` scoped to the host's organization, which makes row 10 need no membership read, or declare the organization host's authenticated 404 and test it as the expected outcome | Dated **ADR-0036 Amendment 8** plus errata beside § Staging across packets and the 2026-09-02 erratum, both of which assign row 10 to this phase without noting that its `M covers (T, O)` term is denied until Phase 03 — which groups it with rows 7 and 14 rather than with 6 and 9 | P02b-5 |
| G12 | Which Keycloak client does the BFF exchange the code with, where does sign-in happen across two tenant hosts, and how do the client scope, the mappers and the seed users reach a Keycloak database that has already consumed the import? | A confidential BFF client with one redirect URI per seed host, and an idempotent reconciler inside `make seed` — a mapper added only to the import reaches no existing workstation | Phase-doc + the realm JSON + `scripts/seed.sh` + `infra/keycloak/README.md`; a new ADR only if the reconciler becomes ADR-0004's `IIdentityProvider` | P02b-6 |
| G13 | What is the BFF session — its custody of the refresh token, its store, its idle and absolute lifetimes, its refresh serialisation under concurrent requests, its terminal `invalid_grant` behaviour, its CSRF control and its logout — and which document owns the access-token lifetime? | The `HttpOnly` cookie Security Standards already describes, encrypted by the session adapter, read only by server code, with single-flight refresh and one authority for the TTL | Phase-doc + one reconciled [Security Standards § Authentication](../standards/11-security.md#authentication); a **new ADR** only for a server-side session store | P02b-7 |
| G14 | What bounds the request types reachable over HTTP while `AuthorizationBehavior` is `return next()`, and what is the shape of the token-keyed rate-limit stage behind a shared BFF connection? | An enumerated read-only routable set held by a catalogue-registered rule, and a limiter stage after `UseAuthentication` keyed on the validated subject | Phase-doc + catalogue registration; a dated ADR-0032 Amendment for a deny-by-default step instead | P02b-5 |
| G15 | What initiates a replay in this phase? | A dispatcher-level operation on the dispatcher's own connection, with the operator surface deferred | Phase-doc; the URL space is its own decision record, per ADR-0036 | — (answered in § The consumer side; recorded so the question travels with the deferral) |
| G16 | What is `LS0002`'s rule name, which project trees does it run over, what does it inspect, and what escalates it to Error? | The name and scope recorded the way Amendment 1 recorded `LS0001`, with the scope reaching the assemblies that actually handle tokens | Dated **ADR-0032 Amendment 4** + a catalogue entry + an `AnalyzerReleases` row | P02b-8 |
| G17 | What are the label sets for the event and job metrics, is the tenant axis permitted, and is it spelled `tenant` or `tenant_id`? | One note in the document that owns metric names, no raw tenant label on a per-event series, and an oldest-eligible-row age metric separate from the pending count | [Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics) edit | P02b-8 (names may land with P02b-2; labels may not) |
| G18 | May an integration-event payload or a job argument carry personal data, what may `last_error` contain, and do the four new durable stores enter the erasure scope? | Identifiers only, a sanitized and length-bounded failure descriptor, and the stores named in the erasure inventory | Dated **ADR-0038 Amendment 2** for the payload rule; [Data Protection § Right to Erasure](../architecture/23-data-protection.md#right-to-erasure-right-to-be-forgotten) and Phase 11 for the rest | P02b-2 (the `last_error` rule and its DDL bound), P02b-8 (the payload rule and the erasure inventory) |
| G19 | Where do the sample flow's publisher and consumer live — a fixture pair in the integration assembly, or a real event a shipped module consumes? | A real event between two of the three modules that hold domain code, because a test-assembly-only consumer leaves every module-scoped sweep with zero subjects | Phase-doc + the Tenancy module spec, which books three `learnstack.tenancy.*` rows to this phase and names Audit as a consumer of one, and the Audit module spec, which says it consumes nothing and has no inbox — a disagreement whichever pair G19 picks has to resolve | P02b-1 |
| G20 | What does an anonymous request to a **routed non-public** request type on a live tenant host answer — and does the catalogued `Backend_RequiresJwt_OnAllAuthenticatedRoutes` narrow to a surface outside tenant resolution? A second half rides with it: the shipped assertion middleware says that from this phase the refusal code differs by caller, `tenant_mismatch` for an authenticated one and `not_found` for an anonymous one, written by the middleware itself | The matrix wins on a tenant host — [row 2](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix) answers 404, byte-identical to an unknown host's, and the 401 rule narrows to the surface where a 401 discloses nothing | Dated **ADR-0036 Amendment 8** (the same one G11 needs) + a catalogue edit to that rule's Asserts line | P02b-5 |

### Durable outbox dispatch

The producer side is **planned, not shipped**: `IOutbox` does not exist and Packet 3's
`OutboxFlushBehavior` is a pass-through shell. Both land here, and so does the consumer
side. The table shipped in Packet 6.

A handler calls `IOutbox.EnqueueAsync` and the row commits on the same **transaction**
as the aggregate change — the ambient one `IUnitOfWork` owns
([ADR-0040](../decisions/0040-ambient-unit-of-work.md)), not a shared `SaveChanges`, a
formulation [ADR-0033](../decisions/0033-audit-durability-model.md) withdrew. Whether
the row is written at the call or buffered for step 7 is **G2**, and the answer decides
whether an enqueue from a consumer, a domain-event handler or a nested command reaches
the table at all: two of the three paths never run `OutboxFlushBehavior`.

#### Process topology

`OutboxProcessor` is a `BackgroundService` — the shape
[Events and Outbox § OutboxProcessor](../architecture/15-event-and-outbox.md#outboxprocessor-backgroundservice),
ADR-0006 Amendment 1, ADR-0010 and the glossary all specify, with a 200 ms poll no job
schedule expresses. This phase hosts it in `LearnStack.Api`, the only host that exists:
the solution builds one runnable host, nothing in `backend/src` implements
`IHostedService` today, the corpus's gateway route table puts `/admin/hangfire*` on that
host ([API Gateway](../architecture/30-api-gateway.md), route id 3, upstream
`learnstack-api`), and the production image is
[Phase 11](phase-11-production-hardening.md)'s. A second worker host is not a
configuration change — a second process consuming an integration event is ADR-0035's
Dapr trigger verbatim — so it stays with Phase 11.

- The phase declares itself **single-instance**. The cache-invalidation deferral above
  depends on that premise, and so does the ordering bound in § Ordering.
- `ConnectionStrings:OutboxDispatcher` carries the `learnstack_outbox_admin` credential,
  registered as a keyed data source behind a `Lazy<>` with a boot-time role guard, on
  the pattern `Platform_DataSource_Resolved_Only_By_PlatformAdminScope` already holds.
  `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` is **Awaiting backfill** in
  the catalogue and is carried here or nowhere.
- The three carriers that say the credential lives in "the worker host" —
  [Database Standards § Database roles](../standards/05-database.md#database-roles),
  `architecture/15` and `.env.example` — are derived cells, not a topology decision;
  ADR-0003's own role table has no host column. They are corrected to name the API host
  in the same change.

#### The dispatcher

- **The lease.** The claim is a durable `UPDATE` stamping `locked_by` / `locked_until`
  inside a short transaction; dispatch runs outside it; the claim predicate itself
  reclaims an expired lease, so there is no sweeper. Every state transition carries the
  ownership fence, failure included — a stale processor must not overwrite a new
  claimant's success or clear its lease.
- **The migration** adds both columns *and* extends `learnstack_outbox_admin`'s
  column-scoped `UPDATE` grant in the same change. ADR-0006 Amendment 2 requires that
  pairing and Database Standards § Outbox already predicts the runtime failure of
  omitting it: `permission denied for table`. The integration case
  `PlatformSchemaTests.TheDispatcherHoldsExactlyTheFourColumnUpdateGrant` pins the
  current four columns by exact equality, and is re-pinned — and renamed — here.
- **Retry** is driven by `available_after` with `attempts` and `last_error` on the row,
  on the schedule
  [Infrastructure Stack Standards § Outbox and Inbox](../standards/20-infrastructure-stack.md#outbox-and-inbox)
  publishes. That document publishes the schedule and a maximum of five retries; **G4**
  fixes what counts as an attempt against that number and what marks a row terminal, and
  **G18** fixes what `last_error` may contain — both Accepted before this packet writes
  the canonical DDL, which is a canonical artifact.
- **The SQL form.** The claim, the lease, the mark and the terminal transition are
  parameterised commands on the dispatcher's own data source, never EF's set-based APIs
  — which CLAUDE.md and `No_Set_Based_Write_Bypasses_The_Audit_Capture` forbid, and
  whose `FromSqlInterpolated` cousin that rule cannot see is the other wrong answer. The
  platform tables already follow this shape: their `DbContext` maps no entity types.
- **A per-delivery deadline**, so one hanging handler cannot stall dispatch for every
  tenant, and so a timeout is not misread as shutdown.

#### Ordering

`partition_key` is on the row and `InProcessEventBus` serialises per key within a
process. What a key guarantees across commits, retries and processes is **G3**: no
column obtainable from `outbox_messages` carries an unconditional per-key publish-order
guarantee — `occurred_at` is the transaction's start time and is identical for every row
one transaction writes, the `uuidv7()` id is minted at `INSERT`, a failed row's
`available_after` moves past its own successor, and an unordered `RETURNING` has no
defined row order. The completion criterion follows the gate, and
[§ Ordering](../architecture/15-event-and-outbox.md#ordering) is edited to match it.

#### Domain events collected and dispatched

`Entity<TId>` has carried the raise-and-clear list since
[Phase 02a Packet 2](phase-02a-kernel-tenancy.md); no aggregate raises an event yet, and
nothing collects them. This phase adds the collector — an `ISaveChangesInterceptor`
registered beside `AuditChangeTrackerInterceptor`, which is what makes it reach the
seeder's composition as well as the API's — and one dispatch site inside the ambient
transaction, because [ADR-0010](../decisions/0010-cross-module-communication.md) puts a
domain event in the same transaction as the change that raised it.

Three things the site has to satisfy, and **G6** is the vehicle for the first two:

- **It runs before the MUST-class audit flush** that ADR-0033 puts immediately before
  `COMMIT`, or a handler's write is captured into a buffer that holds no intent naming
  its type — and the composer selects `changes` by the intent's entity type, so the
  write reaches no row and nothing fails.
- **It runs where an enqueued outbox row can still be enrolled.** `OutboxFlushBehavior`
  is step 7 and the owning frame's post-handler region runs after it returns, so a
  handler told to add an outbox row there adds it to a buffer whose only flush has
  already run.
- **Every frame that owns an ambient transaction owes the same step**, in the same
  order. There is more than one today, and a job frame would be a third.

What a handler may add to the transaction is an outbox row. Whether it may also write
state the raising aggregate owns is part of **G6**: dispatch runs after the handler
built its `Result`, so a post-response `Version++` commits a version the response never
reported. `Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning` does **not**
cover the shape — it counts aggregate roots per constructor, and two handlers holding
one port each are green — so the limit needs a rule of its own, registered with the
planted-offender companion
[the catalogue](../standards/21-architecture-tests-catalogue.md#how-to-add-an-entry)
requires of a rule that ships before its first subject.

`IDomainEvent` is a MediatR `INotification`, so a handler's only failure channel is a
throw, which
[ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)'s
two-track model treats as an *unexpected* failure — a bug, a transient infrastructure
fault or a contract violation — never as an expected outcome a `Result` could carry. The
rule this phase states is therefore narrower than that model: an in-module domain-event
handler is infallible by construction, and a reaction that can legitimately fail is an
integration event. Events are cleared as they are collected; a handler may raise
another, drained inside the same transaction to a bounded depth; and a failed `Result`
or a rollback discards what was neither collected nor dispatched.

#### The consumer side

- **`inbox_messages` and `IInboxGuard`**, built from the canonical DDL that lands in
  Database Standards § Inbox — beside § Outbox, on ADR-0006 Amendment 2's precedent,
  with its table class, its policy taken by link to
  [§ Table classes](../standards/05-database.md#table-classes) rather than copied, and
  its grants. Today the only inbox DDL in the corpus is four columns keyed
  `event_id uuid PRIMARY KEY` in an architecture document, Database Standards has no
  inbox section, and ADR-0023 lists the table as DB-side-minting while Database
  Standards says its key is the producing envelope's `EventId`. **G1** settles the shape
  and the erratum lands with it.
- **One unit of work per `(event, consumer)` delivery**, opened by the transport:
  `BeginTransactionAsync`, then `SetTenantContextAsync`, then the handler, then commit
  or roll back. Both statements run **before** the handler is resolved, because a module
  `DbContext` built outside the ambient transaction is refused — and the shipped
  transport resolves the handler first, so this is a change to it, not a description of
  it. A construction failure therefore happens inside a transaction that must roll back,
  and counts as one failed attempt for that pair.
- **Tenant and organization context restored from the envelope** in every handler scope.
  A consumer that runs without tenant context writes rows Row Level Security rejects, or
  worse, does not. `EventTenantContext` restores from the payload's `TenantId`; the
  row's `tenant_id` is the value Row Level Security checked on insert, and the two are
  required to agree before dispatch.
- **The subscriber failure contract**, under **G1**. Two committed designs currently
  contradict each other: this phase's per-`(event, consumer)` attempts with a terminal
  state inside `inbox_messages`, and
  [§ Dead-letter](../architecture/15-event-and-outbox.md#dead-letter-two-sides-two-failure-domains)'s
  dead-letter destination with the inbox row deliberately left unmarked. They are
  mutually exclusive, the four-column inbox cannot hold attempts or a consumer
  dimension, and the requirement both are trying to meet is the same: one poisoned
  consumer must not dead-letter an event that four other consumers handled cleanly. The
  losing text is deleted, not left beside the winner.
- **A named owner for every unhandled delivery's next attempt.** The shipped
  `InProcessEventBus` rethrows a handler failure out of `PublishAsync`, and
  [Error Handling Standards § Outbox](../standards/09-error-handling.md#outbox) charges
  any `PublishAsync` exception to the outbox row — so today one poisoned consumer
  increments the *producer's* attempts and redelivers to every subscription. ADR-0038's
  one-method port also cannot address a single subscription. Whatever G1 decides, the
  record names the component that redelivers and where the per-subscription attempt
  count survives the delivery transaction's rollback.
- **Dead-lettering emits its counter and writes a MUST-class audit row.** A silently
  dropped integration event is a data-integrity event, not a log line. No slug, class,
  catalogue source or matrix row exists for it today, and Audit Coverage Standards makes
  catalogue → matrix total with no exemption, so **G7** is what makes it buildable.
- **Replay is a dispatcher operation** here: reset the dead-lettered pair and redeliver
  to that one subscription through the guard, with integration tests. It runs on the
  dispatcher's own connection, because no API-reachable role may `UPDATE` an outbox row
  — `learnstack_platform` holds `SELECT, DELETE` — and the operator surface is Phase
  03's (§ Explicitly not in this phase).

#### Observability

[Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics)
owns metric names and
[Events and Outbox § Observability](../architecture/15-event-and-outbox.md#observability)
specifies the seven this subsystem needs. This phase emits all seven in those spellings
and registers the four Observability Standards does not yet carry — the producer-side
dead-letter counter, the lost-lease counter, the inbox dedup counter and the
subscriber-side dead-letter counter. It re-spells none of the three that document
already holds, and the only names it adds beyond the seven are the two liveness signals
below, registered in the same place under **G17**.

- The two dead-letter counters stay separate signals on separate dashboards, for the
  reason `architecture/15` gives: a producer-side spike means the transport is down, a
  subscriber-side spike means a handler or a payload is broken.
- A pending **count** is not a **lag**. A terminal row keeps `processed_at IS NULL`, so
  a naive count includes rows that will never be scheduled again, and a small stuck
  queue is worse than a large one draining. An oldest-eligible-row age metric lands with
  the dispatcher, and the alerting row that reads a raw count is corrected with it.
- Dispatcher liveness is an `outbox` health check plus a heartbeat gauge, following
  `audit`'s registered-not-mapped precedent — not the job scheduler's surface.
- Label sets wait on **G17**. Names may ship before labels; a series shipped with a
  guessed label cannot be renamed once a dashboard and an alert read it.

### Background jobs

Nothing in the corpus decides the background-job runtime. Two Accepted ADRs are credited
with it by the documents that cite them, and neither mentions Hangfire at all; many
other Accepted ADRs mention it, and none of them decides it. **G9** and its record,
**ADR-0047**, are therefore the first work item, and the packet does not start until it
is Accepted. The record has to close, at minimum:

- **Storage under the closed four-role model.** Infrastructure Stack Standards
  prescribes a `hangfire` schema; Database Standards says the database has one schema,
  `public`; all four roles are `NOCREATEDB` and none holds `CREATE` on the database, so
  no role can create that schema, and every structural Row Level Security sweep reads
  `public` only. Each default here breaks a written rule, which is why the storage
  decision owes a dated ADR-0003 Amendment rather than a configuration line.
- **One writer of `ITenantContextAccessor.Current` on the job path.** ADR-0036 § Rules
  names the Hangfire `JobActivator`; `architecture/09` names a `LearnStackJob<TParams>`
  base class; the catalogue carries both spellings. The writer allow-list in
  `TenantContextConstructionTests` is an exact match over two files, so the third file
  is named in the same packet, and whichever loses is retired rather than left in place.
- **Who owns a job frame's transaction and announces `app.tenant_id` on it.** Restoring
  the accessor announces no GUC, and a job touching a module `DbContext` therefore
  throws. ADR-0040 has exactly two entry points and eight setters, and the set is
  closed: the `ISender` shape keeps it at eight, a runner-owned transaction makes it
  nine and owes an amendment.
- **The enqueue-site rule.** Enqueuing a job from inside a handler is the dual write the
  outbox exists to prevent, and the corpus currently draws it as a sibling of
  `IOutbox.EnqueueAsync` off the same handler. `IUnitOfWork` has no post-commit seam.
- **The retry and dead-letter contract**, one taxonomy shared with dispatch and
  delivery, with its counter registered in Observability Standards and its audit row
  under **G7**.
- **The ADR-0035 classification.** The job *tenant contract* is a one-way door — it is
  in ADR-0035's own list of things that touch every job payload — and the runner is
  additive. Ship the contract and the port; the classification decides whether the
  vendor adapter is gated, and no scheduling port name exists in the corpus to inherit.
- **The queue grammar and job-id convention.** Infrastructure Stack Standards carries a
  dotted job-name rule; two ADR bodies spell the same thing with colons, and no document
  defines a queue grammar at all — while Phase 08a's dependency table inherits a "queue
  policy" from this phase and its own Notifications section says that phase defines "its
  queue and its retry policy", a tension the record resolves. Queue names and job ids
  are separate concepts and the record distinguishes them.
- **The package pins and the licence verdict** for Self-Hosted redistribution. Nothing
  is pinned today.

What this phase ships once that record is Accepted:

- The runner in its own `LearnStack.Infrastructure.*` assembly, registered at the
  composition root, with its storage created on the DDL path ADR-0047 names.
- The tenant-context restoration hook: one writer, its Origin, the system-actor and
  causal-actor split, the `Guid.Empty` and `TenantId.PlatformSentinel` refusals, an
  `Activity` started from the stamped `traceparent`, and a `finally` that restores the
  previous accessor value so the next job on the worker does not inherit it.
- An enqueue-time guard over `tenant_id`, `organization_id?`, a `traceparent`-shaped
  `correlation_id` and the causal actor. Job payloads missing any one of them **fail at
  enqueue time**, not at activation: the failure is loud and belongs to the caller who
  can fix it. The catalogued rule reads "missing `tenant_id` **or** `correlation_id`",
  so a payload missing exactly one of them fails too, and the guard checks provenance
  rather than mere presence — the platform stamps these values from the resolved
  context.
- A behavioural rule that a job runs under its restored tenant. The catalogued
  structural rule asserts statement order and is green whether restoration works or not.
- **No job definition and no dashboard route** (§ Explicitly not in this phase).

### Identity integration — the `learnstack` realm

The LearnStack identity **domain** (`User`, `Membership`, `Role`, `Permission`,
`Invitation`) lands in [Phase 03](phase-03-identity-admin.md). This phase delivers the
authentication plumbing for the tenant-facing `learnstack` realm, and adds nothing to
the rules its carriers own:
[Identity and Auth § Token Flow](../architecture/13-identity-and-auth.md#token-flow)
owns the OAuth role split,
[Security Standards § Authentication](../standards/11-security.md#authentication) owns
token custody and lifetimes,
[Frontend Architecture Standards § Auth](../standards/07-frontend-architecture.md#auth)
owns the session mechanism, and
[ADR-0036 § Staging across packets](../decisions/0036-tenant-resolution-trusted-inputs.md#staging-across-packets)
stages the wiring into this phase by name. Where this phase disagreed with one of them,
it was the phase that was wrong.

LearnStack does not implement password hashing, password-reset rendering, MFA enrolment,
or refresh-token rotation. Those live in Keycloak; the split is described in
[Identity and Auth](../architecture/13-identity-and-auth.md).

- **The authentication wiring ADR-0036 stages here**, and its insertion order:
  `UseAuthentication` above tenant resolution so a validated claim exists when the
  resolver builds its attempt, `UseAuthorization` below the tenant assertions. Two
  insertions, not one block, with an ordering rule and a planted-order companion
  registered in the catalogue. The claim signal is populated at the one site the
  resolver's comment reserves, from a validated `learnstack`-realm principal only.
- **One JWT authority for `/api/v1/*`**, the `learnstack` realm, with the issuer, the
  audience, the algorithm allow-list, the clock skew, `azp`, and the rejection of ID
  tokens all pinned. `Api_Registers_Only_The_Tenant_Realm_Authority` is catalogued to
  this phase and its own note says the structural half passes while issuer validation is
  disabled in configuration — the integration half is the load-bearing one. A
  `learnstack-hub` token on a tenant-facing endpoint is refused here, not in
  demand-gated Phase 02c.
- **The claim shape and the actor's identity**, under **G10**. The pipeline carries a
  `UserId` from this phase on, and the resolver already dereferences one when a tenant
  claim arrives with a subject, so the value is load-bearing rather than cosmetic. Phase
  03 owns the durable `sub` → `UserId` mapping; this phase owns where the claim comes
  from and must not mint a fresh identifier per login.
- **Which matrix rows go live**, under **G11**. `demo-english` is a tenant-wide host and
  `demo-yoga` maps to its default organization, so a tenant-only token on the yoga host
  is a row that consults `ITenantMembershipReader` — registered as
  `DenyAllTenantMembershipReader` until Phase 03. Whichever way G11 resolves, the
  outcome is asserted rather than discovered: a successful Keycloak callback on its own
  conceals it.
- **The host/JWT tenant cross-check.** A request whose host-derived tenant disagrees
  with its JWT tenant returns 404 — not 403, which would confirm the resource exists —
  through the **same** recorder and the **same** operation key Packet 9 shipped, with
  `metadata.assertionSource = jwt-claim`, per
  [ADR-0036 § Recording a rejected assertion](../decisions/0036-tenant-resolution-trusted-inputs.md#recording-a-rejected-assertion).
  Not a second detector. The shipped types cannot carry that metadata yet — the
  rejection record has no source, actor or correlation field, the recorder hard-codes a
  null actor, and the resolver's refusal path only logs — so those are this phase's
  edits. The organization dimension follows G11. The cross-check is a fault detector,
  not an authorization control, and not the first isolation layer: Row Level Security
  and the query filters precede it.
- **What an anonymous request to a routed endpoint answers**, under **G20**. The
  catalogued `Backend_RequiresJwt_OnAllAuthenticatedRoutes` is booked to this phase and
  says every endpoint outside the public allow-list answers 401 without a bearer token;
  [ADR-0036's matrix](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix)
  row 2 says a non-`[PublicSurface]` request on a tenant host with no credential answers
  404, byte-identical to an unknown host's. Both cannot hold for the same request, and
  the default `UseAuthorization` wiring answers the one the matrix forbids. The same
  gate carries the refusal **code** split the shipped assertion middleware assigns to
  this phase by name — `tenant_mismatch` for an authenticated caller, `not_found` for an
  anonymous one, written by the middleware rather than left to the status-code pages.
- **Absence is not disagreement.** A token with no `tenant_id` claim resolves under the
  host-only ceiling by design. What this phase must not do is let that be silent: with
  the realm as it stands every token is claimless, so a lost mapper looks exactly like
  an anonymous visitor. The claimless-token case on a tenant host has no matrix row at
  all, and G11 gives it one.
- **The authenticated tier's bound**, under **G14**. Authentication goes live while
  `AuthorizationBehavior` is still `return next()`, so without a bound any tenant user
  reaches every routed request type until Phase 03. The token-keyed budgets that
  [Security Standards § Rate Limiting](../standards/11-security.md#rate-limiting),
  Standards 04 and the shipped limiter's own remarks assign to this phase go live with
  authentication — they are also the bound ADR-0036 names for its per-occurrence
  mismatch row, so activating that path activates this dependency.
- **Seed users**, under **G12**: tenant users only. Operators are the `learnstack-hub`
  population and ADR-0004 Amendment 1 makes the separation a hard invariant — the
  `learnstack` realm declares no platform-admin role and should not gain one. The
  organization dimension is not representable in a token this phase issues, so "across
  both organizations" is not a promise this phase can keep.
- **Keycloak realm roles.** The realm ships three `tenant-*` roles with full scope on
  both clients, so they appear in every token, while roles live in LearnStack and not in
  Keycloak. Either they leave the realm or they are declared inert with a rule proving
  no LearnStack assembly reads a role claim. A documentation link is not a mitigation
  for a leak the realm actively causes.

#### Correction: the realm emits no `tenant_id` claim at all

This is the one defect in this phase's original four that the corpus has not since
repaired, and it is the reason the cross-check cannot run today.
`infra/keycloak/realms/learnstack.json` stores `tenant_id` as a **user attribute** and
declares **zero protocol mappers**. Keycloak does not put user attributes into tokens
unless a mapper says so, so every issued JWT reaches the API with no `tenant_id` claim,
every request resolves under the host-only ceiling, and the first layer of tenant
defence is off with nothing reporting it.

Three changes, and the third is the one that makes the first two take effect:

- An **`oidc-usermodel-attribute-mapper`** on a client scope shared by the API and the
  BFF's client, projecting the attribute into the access token and the ID token, typed
  as `String`, with an explicit audience mapper beside it. A mapper on the API's client
  alone shapes nothing in a token minted for another client, which is the client the BFF
  redeems the code with.
- The seeded value changed from the slug `"tenant-a"` to the seed tenant's **UUID**. The
  claim is consumed as a `TenantId` — a Vogen value object over `Guid` from
  [Phase 02a Packet 6](phase-02a-kernel-tenancy.md) — so a slug cannot be parsed. The
  attribute is declared **admin-only** in the realm's user-profile configuration: it
  alone raises a request to the host-and-claim authority ceiling, so a
  self-service-writable attribute is a privilege-escalation path.
- A **reconciliation mechanism**, under **G12**. Keycloak consumes the realm import
  once, at first start, and re-import overwrites nothing that already exists. A change
  made only in the import reaches no existing workstation, and a Keycloak database wipe
  is not the upgrade path — `make seed` today only polls each realm's discovery endpoint
  and never writes to Keycloak. Without this step the token-shape test below is green in
  CI on a fresh container while every warm workstation issues claimless tokens.

The test asserts the shape rather than the configuration file: a token issued by the dev
realm for a seed user carries a `tenant_id` claim that parses to that seed tenant's
`TenantId`, and the realm export's seeded value equals `SeedData`'s literal. A realm
export that loses the mapper fails it.

#### The BFF session

The session is the first thing in the repository that holds a user credential, and its
properties are required rather than inherited from an adapter's defaults. **G13**
settles it; what the phase ships either way:

- **OIDC Authorization Code flow with PKCE between the browser, the BFF and Keycloak.**
  The BFF is the code-exchange client; the API is a resource server that validates a
  bearer token and is not a party to the flow.
- **Custody.** Security Standards puts refresh tokens in `HttpOnly`, `Secure`,
  `SameSite=Lax` cookies and that document owns the rule; what this phase adds is that
  only server code reads them, and that no response body, RSC payload, session JSON or
  log line carries one.
- **Lifetimes and refresh.** One authority for the access-token TTL — four documents
  currently give four numbers, and this phase links the authority rather than adding a
  fifth. Concurrent requests arriving with a near-expiry token produce exactly one token
  request and one `Set-Cookie`; a terminal `invalid_grant` clears the session and the
  visitor gets the public page anonymously rather than an error.
- **Cookie scoping and CSRF.** Host-only cookies with no `Domain`, so two tenant hosts
  do not share a session, plus an `Origin` check and a session-bound token on every
  mutating route. `SameSite=Lax` does not separate hosts under one registrable domain,
  which is exactly the SaaS subdomain shape, and no CSRF rule exists in the catalogue
  today.
- **Login topology**, under **G12**: login is served on the tenant host the visitor is
  already on, which needs one registered redirect URI per seed host — the realm
  registers only `localhost` today, and the product is reached on two tenant hosts. The
  login transaction's `state`, `nonce` and `code_verifier` live in a host-only
  transaction cookie, and return URLs are validated.
- **Logout**, which
  [Identity and Auth § Logout](../architecture/13-identity-and-auth.md#logout) describes
  and Phase 03 expects: the session cleared, the end-session endpoint called, and
  back-navigation not restoring an authenticated view.
- **Something that consumes the session.** One authenticated `/api/v1/*` read and one
  guarded page, both in the OpenAPI document and the regenerated SDK. Without them "a
  login completes" cannot be demonstrated, and
  `Backend_RequiresJwt_OnAllAuthenticatedRoutes` sweeps an empty route set.
- **Auth.js and `auth()` placed in `apps/web`**, the `(studio)` / `(portal)` redirect,
  the server SDK's bearer attachment, and server-only environment keys. One skill
  imports `auth` from a package that does not exist and a second calls `auth()` with no
  import at all; both are corrected here.

### Audit coverage wiring

The audit pipeline lit up in
[Phase 02a Packet 9](phase-02a-kernel-tenancy.md#delivery-record-packet-9) under
[ADR-0033](../decisions/0033-audit-durability-model.md). This phase adds the entry
points it does not reach, and both of Packet 9's hand-offs land here.

- **The delivery path has no audit mechanism today**, and the gap is silent.
  [ADR-0040](../decisions/0040-ambient-unit-of-work.md#consumers-do-not-have-a-request-and-still-need-all-of-this)
  makes an event delivery the application's second entry point, and the transport runs
  `Begin → SetTenantContext → handler → Commit` with no MediatR behavior in the path.
  Only `AuditLogBehavior` declares intents, only `TransactionBehavior` flushes them, and
  the catalogue is keyed by request type — so a consumer that writes its own aggregate
  through its `DbContext`, which is the shape the corpus teaches as canonical, has its
  changes captured into a buffer that holds no intent naming its type. **No row is
  written and nothing fails.** That is the one window
  [ADR-0033](../decisions/0033-audit-durability-model.md) says must not exist. **G6**
  decides it, and the case that proves it is a MUST-classified direct-write consumer,
  not only a consumer that sends a command.
- **The audit frame's owner on a non-pipeline entry point.** What is *already* decided
  is not reopened here:
  [ADR-0033 Amendment 2 § 2](../decisions/0033-audit-durability-model.md) and
  [ADR-0044 § 4](../decisions/0044-audit-write-path.md) both say only the owning frame
  writes, signals the boundary, reconciles and clears, and a joiner does none of it.
  What is open is narrower and is **G6**: the shipped `AuditLogBehavior` takes its owner
  flag from an audit-frame counter rather than from unit-of-work ownership, and
  [Audit Subsystem](../architecture/31-audit-subsystem.md) records that split as
  deliberate for the HTTP path — at step 3 no transaction is open to answer the
  question. The two coincide today only because the pipeline is the only thing that
  opens a transaction with a command inside it. A transport-opened transaction breaks
  the coincidence: the inner command becomes a unit-of-work joiner while still being the
  outermost audit frame, so it reconciles before the delivery commits, writes its MUST
  row standalone as `failed` for a change that then commits, and clears the buffer the
  owner would have flushed. Either resolution edits an Accepted record, so the amendment
  is Accepted before the transport opens a transaction.
- **The discriminating case.** The pair this phase used to name — a consumer's
  MUST-class command recording `success` on the delivery's transaction, and a
  rolled-back delivery recording it standalone as `failed` — passes under **both**
  candidate designs. The cases that tell them apart are an inner refusal the consumer
  absorbs while the delivery commits, a faulted `COMMIT` producing exactly one
  `indeterminate` row, and `Clear()` running exactly once per delivery.
- **The dead-letter transitions**, under **G7**. "Through `IAuditStore`" names neither a
  method nor a permitted caller, and no single role can both make the terminal outbox
  transition and insert the row on one transaction: `learnstack_app` has no `UPDATE` on
  `outbox_messages` and `learnstack_outbox_admin` has nothing at all on `audit_log`. The
  record names the class, the slug, the transaction, the ordering against the state
  change and the failure posture; the slugs are registered off-path and carry their
  matrix rows in the module that writes them. Poisoned-job handling inherits the same
  shape.
- **The dispatcher's `BYPASSRLS` posture**, under **G8**. Audit Coverage Standards
  requires a `security-event` row on every platform-bypass invocation and lists RLS
  bypass as MUST; the only exemption is a sentence in an architecture document, which
  cannot grant it. A per-invocation row on a 200 ms polling loop is a different
  table-growth curve from a per-request one, and this is the first `BYPASSRLS`
  credential that actually runs.
- **The rejected-assertion row names its actor once there is one.** The recorder writes
  a null actor on the authenticated tier today, because no principal exists in the
  process and `IsAuthenticated` is a tier rather than an identity. ADR-0036 makes the
  actor the finding on that tier, so the realm integration above populates it from the
  validated token's user — the observable that also proves the assertion middleware runs
  after authentication.
- **Causal actor and causation.** The envelope carries both and the outbox row has
  columns for both, and neither reaches a consumer's audit row or a follow-up outbox
  row. ADR-0038 promises the causal actor "is retained separately as causal audit
  metadata" and nothing retains it. Which metadata keys a composed row may carry is part
  of **G6**.
- **Classification for the operations this phase introduces** — the sample flow's
  command, the first authenticated read, login, logout and refresh — registered in their
  owning module's catalogue source and matrix before their handlers ship, because an
  operation the catalogue does not classify is refused, and the join is total in both
  directions. The authenticated read carries no permission key until Phase 03.

The `AuditEntry` aggregate is owned by the **Audit** module and shipped in Phase 02a;
rows arrive through the store's parameterised inserts, and
[Phase 03](phase-03-identity-admin.md) registers the Identity module's catalogue source
and coverage matrix against it. See
[Audit Coverage Standards](../standards/18-audit-coverage.md).

### Compile-time secret leakage (carried out of Phase 02a Packet 3)

Packet 3 shipped runtime redaction — the Serilog enricher and the local error tracker
both scrub the same set through the single `SensitiveTokenCatalog`. It covers logs, OTLP
and error-tracker tags. It does not cover a value already interpolated into an exception
**message**, and this phase adds three durable sinks for one: an outbox row's
`last_error`, a subscriber-side failure record, and a job's persisted failure state.
Those sinks, not a Problem Details body, are the threat this phase's diagnostic answers
— the response factory builds no free text from an exception message.

`LS0002` is a build-breaking diagnostic, and a roadmap document is not its registry.
**G16** records it the way ADR-0032 Amendment 1 recorded `LS0001`: its
`LearnStackException-<Topic>` rule name, the project trees it runs over, what it
inspects, its `AnalyzerReleases` row, its catalogue entry, and the condition that
escalates it from Warning to Error. Two things the record has to settle rather than
inherit:

- **Its scope.** `LS0001`'s `Domain` + `Application` reference set excludes the
  assemblies where this phase handles tokens and provider errors, which is where the
  sinks are.
- **Its token source.** The analyzer targets a different framework from the assembly
  that holds `SensitiveTokenCatalog`, whose own remark says new tokens land there and
  not in consuming projects, and the catalogue's public accessor exposes only part of
  the set. A drift test is what makes "matches `SensitiveTokenCatalog`" true rather than
  aspirational.

Its own tests assert the positive case, a clean case staying clean, a sensitive name
outside a `throw` staying clean, no `AD0001` crash — the failure mode ADR-0032 Amendment
1 records for `LS0001` — and that the analyzer's token set equals the catalogue's.

The same change bounds what the durable sinks may hold, under **G18**: a sanitized
descriptor — the exception type, the error code and the attempt's `traceparent` — with a
length bound in the canonical DDL, applied in the same migration as the lease columns
while nothing writes the table yet.

### Architecture tests

[Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) is the
canonical reference for every rule identifier, and a hand-kept second copy of it here
goes stale the moment the catalogue moves. The obligation is therefore stated over the
catalogue's own **Phase** field: **every row whose Phase names 02b is Implemented and
green by this phase's exit, and none is still Registered or Awaiting backfill.** That
set includes the outbox, inbox, partition-key, claim-protocol, job-payload, JWT and
realm-authority rules, and it currently contains rows this document's previous list
omitted.

What this phase owes the catalogue beyond implementing those rows:

- **New rows**, registered in `P02b-0` before the code that satisfies them: the ADR-0036
  middleware-ordering rule, the same-transaction outbox write, the two domain-event
  rules, the routable-set bound (**G14**), the Keycloak-role-claim rule, a
  payload-content rule (**G18**), and `LS0002` under its rule name (**G16**).
- **Widened assertions** where a catalogued rule is satisfied by the defect it exists to
  catch. `Outbox_Row_Carries_Correlation_Context` asserts only non-nullness of two
  columns and extends to `partition_key` and to value correctness.
  `Outbox_Claim_IsHeld_Until_Dispatch_Completes` says "exactly once" unconditionally,
  which contradicts the at-least-once contract; it narrows to live-lease contention and
  gains a redelivery companion for crash and expiry.
  `Hangfire_Job_Payloads_Include_TenantId` gains the organization, actor and traceparent
  fields. `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` gains the hanging-handler
  case. `Integration_Event_Declares_PartitionKey` is typed as reflection over module
  assemblies and cannot observe a runtime key — an uninitialised instance reads a zeroed
  Guid as non-blank — while the compiler and the envelope's own guard already hold both
  halves; it becomes a behavioural assertion over persisted rows, or is re-typed and
  retired in `P02b-0`. Two rules the catalogue stamps **02a** also widen here, so the
  Phase-field sweep above does not reach them: the transport arm of
  `Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction`, whose second arm has no
  subject until the transport announces a tenant, and
  `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork`'s connection allow-list, which
  the dispatcher's keyed data source and the job storage both touch — an eighth entry
  there is a decision, recorded under **G9**.
- **A planted-offender companion for every structural rule this phase ships.** The
  catalogue's own rule is that a rule shipping before its first subject exists carries a
  companion that plants the violation it exists to catch; with one sample event and one
  consumer, most of this phase's structural rules are near-vacuous without one. That is
  Packet 9's repeated lesson — a rule that cannot tell *clean* from *blind* — and Packet
  10 established the convention this phase follows: a `*Probes.cs` type holding the
  planted violation, and a `The_<X>_Can_Actually_Fail` case beside the rule.
- **Rules that leave this document.** A read-model naming rule belongs to
  [Database Standards § Read Models](../standards/05-database.md#read-models), which
  already carries `public_<module>_<concept>`; it has no subject, no marker identifying
  a table as a read model and no catalogue row, and the first phase that ships a
  projection registers it with its first subject. The provider-SDK rule is the
  catalogue's and is wider than the paraphrase this document carried — and because its
  row is stamped 02b, the Phase-field obligation above would otherwise sweep a rule with
  no subject, so `P02b-0` moves its owning-phase cell to the phase that lands the first
  provider adapter, unless the Keycloak reconciler **G12** settles is that adapter and
  gives it its first subject here.

Two notes on what "green in CI" can mean. Several of this phase's rows are behavioural
rather than structural, and CI runs those in a separate job under a Docker trait filter,
so "the architecture suite is green" does not reach them. That job is **not** a required
check today — CONTRIBUTING names adding it to branch protection as the one remaining
edit, a repository setting no phase owns — and every behavioural criterion below sits in
it, so `P02b-8` takes that edit and records its date. And the executed-test guard counts
executed tests, so a per-test skip leaves its count non-zero — the criterion says *not
skipped*, per assembly.

## Deliverables

- **ADR-0046 Accepted** — event delivery, the inbox table's canonical shape, and the
  subscriber failure contract as one state machine: subscription identity, attempts,
  backoff, cancellation, terminal transitions and replay, with the losing design
  reconciled out of its carriers.
- **ADR-0047 Accepted** — the background-job runtime, with the dated ADR-0003 Amendment
  its storage decision owes.
- **The dated amendments the register names**, each Accepted before the code it governs:
  ADR-0044 / ADR-0033 / ADR-0040 for the delivery and domain-event frames (G6), ADR-0033
  for the dead-letter writer (G7), ADR-0006 or ADR-0003 for the dispatcher's bypass
  posture (G8), ADR-0006 for the retry contract (G4), ADR-0036 for the matrix staging
  (G11), ADR-0032 for `LS0002` (G16), ADR-0038 for the payload-content rule (G18).
- **Database Standards § Inbox** — the canonical inbox DDL beside § Outbox, with its
  table class, its policy taken by link, its grants and its retention floor; plus the
  erratum ADR-0023's DB-side minting list owes.
- `IOutbox` and the `OutboxFlushBehavior` body, with the producer write model G2
  settles.
- A durable `OutboxProcessor` implementing the specified lease: a fenced claim, a
  per-delivery deadline, bounded retry, a terminal producer state, and the migration
  that adds `locked_by` / `locked_until` **and** extends `learnstack_outbox_admin`'s
  column-scoped `UPDATE` grant in the same change.
- The dispatcher's credential registered as a keyed data source behind a boot-checked
  `Lazy<>`, with `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` moved from
  Awaiting backfill to Implemented and a companion proving the sweep can fail.
- `partition_key` persisted onto the row and rebuilt into the envelope on dispatch.
  `PartitionKey` abstract on `IntegrationEventBase` and per-key serialisation in
  `InProcessEventBus` shipped in Packet 5; the durable hop is this phase's.
- `inbox_messages` and `IInboxGuard` over the canonical DDL, the per-`(event, consumer)`
  delivery transaction opened before handler construction, and tenant and organization
  context restored in every handler scope from the envelope.
- A subscriber-side dead-letter path in the shape G1 settles: a terminal outcome per
  subscription, a sanitized failure descriptor, its counter, its MUST-class audit row,
  and a replay that re-enters the inbox-guarded path — as a dispatcher operation, with
  the operator surface deferred to Phase 03.
- A worked sample flow — enqueue → outbox → dispatch → idempotent consumption — placed
  where G19 settles, between modules that hold domain code, so the module-scoped sweeps
  have a subject.
- Domain-event collection on the shipped seam and one dispatch site inside the owning
  frame's transaction, ordered before the MUST-class audit flush and before whatever
  enrols outbox rows.
- **The delivery path's audit frame** in the shape **G6** settles — the component that
  performs the four owner-side acts on a transport-opened transaction, and the same
  owner duties performed in the same order by every frame that owns an ambient
  transaction (Packet 9 hand-off).
- The background-job runner, its storage on the path ADR-0047 names, its tenant-context
  hook, its enqueue-time guard over four fields, its package pins — and no job
  definition and no dashboard route.
- The `outbox` health check registered and not mapped, plus six rows in
  [Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics)
  — the four event counters in the spellings
  [Events and Outbox](../architecture/15-event-and-outbox.md#observability) already
  uses, and the dispatcher heartbeat gauge and the oldest-eligible-row age metric under
  **G17**.
- `UseAuthentication` and `UseAuthorization` at their two pinned positions, the claim
  signal populated at the resolver's reserved site, one JWT authority for `/api/v1/*`
  with its full validation contract written into
  [Security Standards § Authentication](../standards/11-security.md#authentication), and
  token-keyed rate-limit partitions going live with authentication.
- The rejection record carrying a source, an actor and a correlation id; the recorder
  writing `metadata.assertionSource` and populating `actor_user_id` on the authenticated
  tier; and the resolver's refusal path calling the recorder rather than only logging.
- The host-vs-JWT tenant cross-check returning 404 through Packet 9's existing recorder
  and operation key — the same key, no second detector.
- The Keycloak `learnstack` realm with a shared client scope carrying the `tenant_id`
  mapper, the `user_id` mapper G10 settles, an explicit audience mapper, UUID-valued
  seed attributes agreeing with `SeedData`, both attributes declared admin-only,
  tenant-only seed users, the realm-role question resolved, and per-host redirect URIs.
- An idempotent reconciler that applies all of that to a warm Keycloak database on every
  `make seed`, preserving existing accounts and the `learnstack-hub` realm.
- OIDC PKCE login through the BFF on the visitor's own tenant host, a cookie session
  with host-only scoping and single-flight refresh, an `Origin` plus session-bound-token
  CSRF control, RP-initiated logout, and Auth.js wired in `apps/web` with the two
  skills' broken `auth` references corrected.
- The first authenticated `/api/v1/*` read and the first guarded page, both in the
  OpenAPI document and the regenerated SDK.
- A Keycloak test fixture over the committed realm JSON and an in-process JWT issuer for
  the ADR-0036 token rows, both carrying the Docker trait where they need a container
  and both visible to the trait sweep.
- `LS0002` shipping as a Warning under its catalogued rule name, with its ADR-0032
  amendment, its catalogue entry, its `AnalyzerReleases` row, its linked token source
  and its four tests.
- **Carrier reconciliation**, in the change that lands each mechanism:
  [Events and Outbox](../architecture/15-event-and-outbox.md) (§ Inbox table, §
  Subscriber side, § Ordering, § Dead-letter — producer side, whose pushed-out
  `available_after` terminal reading and manual-replay recovery both change; the
  producer and consumer samples; and the dispatcher sketch's non-existent context,
  unbounded type resolution, unfenced `RecordFailure` write and raw `ex.Message`);
  [Infrastructure Stack Standards § Outbox and Inbox](../standards/20-infrastructure-stack.md#outbox-and-inbox)
  and § Background Jobs;
  [Error Handling Standards § Outbox](../standards/09-error-handling.md#outbox) and
  [§ Background Jobs](../standards/09-error-handling.md#background-jobs);
  [Database Standards](../standards/05-database.md);
  [Observability Standards](../standards/10-observability.md);
  [Identity and Auth](../architecture/13-identity-and-auth.md) and
  [Frontend Architecture](../architecture/14-frontend-architecture.md);
  [Security Standards](../standards/11-security.md) and
  [Frontend Architecture Standards](../standards/07-frontend-architecture.md);
  [Testing Standards](../standards/06-testing.md), which names Postgres as its only
  integration container; the `add-integration-event` skill, whose consumer sample still
  writes an audit row through a module `DbContext`; and the glossary, both for the
  headwords this phase's mechanisms need and for its existing `IInboxGuard` entry, which
  calls the table per-module. Four carriers will contradict this phase the day it lands
  and are corrected with it: [Phase 03](phase-03-identity-admin.md), which still owns
  the BFF session, the host-and-claim cross-check and logout, and acquires instead the
  Hangfire dashboard route behind the Platform-scope permission with its ADR-0015
  amendment, the operator recovery surface, and this phase's stale in-code messages;
  [Phase 11 § Data Operations](phase-11-production-hardening.md#data-operations), which
  acquires the processed-outbox and aged-inbox purge under the `learnstack_platform`
  `DELETE` grant with the no-principal constraint **G9** carries;
  [Phase 08a](phase-08a-assessment-notifications.md), which credits
  `OutboxFlushBehavior` to Phase 02a and books the queue two ways; and
  `infra/apisix/README.md`, which dates the dashboard's gateway gating to Phase 08a. Two
  more sentences go stale on landing: the post-commit-seam comment that names this
  phase's outbox dispatch as its obligation — a polling dispatcher needs no such seam —
  and
  [Security Standards § The out-of-band setters](../standards/11-security.md#the-out-of-band-setters),
  whose transport row and reproduced count both change when the transport opens a
  transaction.

## Completion Criteria

Each criterion below is observable: a named test can pass or fail on it. Criteria whose
shape depends on an open gate say so, and are written when that gate is Accepted.

**Producer and dispatcher**

- An aggregate change and its outbox row commit together and roll back together,
  asserted as `learnstack_app`. An enqueue whose event `TenantId` differs from the
  ambient context is refused.
- An event enqueued from an HTTP request **and** one enqueued from inside an event
  delivery both commit rows whose `correlation_id` rehydrates to the enqueuing
  activity's trace, whose `tenant_id` and `organization_id` equal the announced context,
  and whose `partition_key` equals the event's — and the assertion fails on a null
  `partition_key`.
- Two `OutboxProcessor` instances draining one pending batch, **both holding unexpired
  leases**, publish each message once; the claim `UPDATE` succeeds as
  `learnstack_outbox_admin` and fails with `permission denied for table` against the
  pre-extension grant. A companion of the same harness against a release-on-read claim
  sees duplicates.
- A lease that expires mid-dispatch is reclaimed, the first processor's success **and**
  failure writes are both no-ops under the ownership fence, the lost-lease counter
  increments, and the consumer sees one business effect.
- A failed dispatch retries on the published backoff with its lease cleared — so the
  first retry runs at its stated delay rather than at the lease duration — reaches the
  terminal state G4 names at the stated attempt, increments the producer dead-letter
  counter, and leaves the pending gauge.
- A blocked dispatcher loop turns the `outbox` health check unhealthy within a bounded
  number of poll intervals, while `/healthz` still answers 200.
- With one terminal row, one row whose `available_after` is in the future and one due
  row in the table, the age metric reports the **due** row's age — the case that
  separates it from `min(occurred_at) WHERE processed_at IS NULL`.
- A `ConnectionStrings:OutboxDispatcher` naming any role other than
  `learnstack_outbox_admin` refuses the boot at registration; an absent or malformed
  value names its key and the expected form; a valid one registers without connecting;
  and no message carries the password. The four cases already exist for the application
  data source and are copied rather than re-derived.
- A payload written by a shipped event's V1 is read by V2-deployment code without loss;
  a row naming a retired type dead-letters at once under its named reason; a dispatched
  event with no registered subscription marks its row processed and increments no
  failure counter.
- The same-key ordering criterion is written when **G3** is Accepted. Whatever it
  asserts, it runs interleaved producing transactions — the first to begin commits last
  — because a same-key pair written with a wide clock gap by one processor is green
  against a dispatcher that reorders whenever two producing transactions overlap.

**Consumer side**

- One envelope delivered twice, sequentially and then concurrently, yields one business
  row and one processed inbox record, and the dedup counter increments once; the same
  case with the guard check removed fails.
- A handler that marks the inbox and then throws commits neither, and the next
  redelivery writes exactly once.
- A fan-out where consumer 1 commits and consumer 2 throws leaves consumer 1's write
  committed, consumer 2's rolled back, consumer 2's failure recorded durably **after**
  that rollback, and exactly one of the two retry budgets advanced. The test names the
  component that owns consumer 2's next attempt.
- A handler that throws on every delivery reaches the terminal state for **that consumer
  only**, leaves the other consumers of the same event unaffected, accumulates no
  attempts on the producer's row, increments the subscriber dead-letter counter, and
  produces exactly one MUST-class audit row under the slug G7 names.
- A reset `(event, consumer)` pair is redelivered to that subscription and to no other,
  and a late success racing the reset is absorbed by the guard.
- The same event delivered to two handlers in one module runs both, and neither one's
  inbox record makes the other skip.
- An event of tenant B published during tenant A's request is handled with tenant B
  resolved **and** `app.tenant_id` set to B, before the handler is constructed; the
  handler's write is visible to B and invisible to A; and a transport variant that skips
  the context statement fails the same case. No shipped case asserts `app.tenant_id`;
  `InProcessEventBusTests` proves the accessor and the scoped context in-process only.
- A consumer whose constructor takes a module write store resolves — true only if the
  delivery transaction is open before the handler is resolved.
- Each module-assembly event sweep reports a non-zero subject count as its own leg, so a
  sample flow that lives only in a test assembly fails the phase rather than passing
  three rules over nothing.
- A tenant-wide event published from an organization-scoped request is consumed with the
  organization scope **G1** settles, and its consumer can write the rows that scope
  permits rather than failing obscurely.
- An inbox record written under tenant A is invisible to a `learnstack_app` session
  announcing tenant B, and all module migration chains still apply together on an empty
  database.
- A dispatch failure whose exception message embeds a value the sensitive-token
  catalogue matches leaves no trace of it in any persisted failure field, and the length
  bound refuses a longer value.

**Audit**

- A MUST-classified **direct-write** consumer commits exactly one `success` row for its
  delivery; a delivery whose handler throws leaves the aggregate unwritten and exactly
  one standalone `failed` row.
- A state-mutating integration-event handler the catalogue does not classify is refused,
  proved by a planted unregistered handler rather than by the absence of one.
- An inner refusal a consumer absorbs keeps its own outcome on its own intent while the
  delivery commits; a faulted delivery `COMMIT` produces exactly one `indeterminate`
  row; and the capture is cleared exactly once per delivery.
- A domain event raised by an aggregate method reaches its in-module handler inside the
  same transaction; the handler's write appears in the request's MUST-class row's
  `changes`, proving dispatch ran before the flush; and an outbox row that handler
  enqueues is dispatched — asserted on the dispatched message, not on the enqueue call.
- A frame whose captures name an aggregate root that no intent in the frame declares
  produces the behaviour G6 records.
- A consumer handling an event caused by user U writes a row whose metadata names U and
  the consumed event, and the follow-up outbox row carries that event as its causation.
- Every registered off-path dead-letter slug has a matrix row in the module that writes
  it; removing the row fails the build.

**Background jobs**

- A job enqueued without a tenant id, and one enqueued without a correlation id, each
  fail **at enqueue time**; an organization-scoped job missing its organization fails
  the same way; and the guard reports a payload whose tenant is merely
  present-but-default.
- A job enqueued under tenant A runs under A, writes a tenant-owned row as
  `learnstack_app` that Row Level Security accepts, reads none of tenant B's rows, and
  leaves the accessor clear for the next job on a one-worker server.
- A first boot of the local stack creates the job storage and its grants without
  granting any role `CREATE` beyond what ADR-0047 specifies; the four roles are still
  `NOCREATEDB` and the migration role is still the only creator in `public`. Every
  structural Row Level Security sweep either covers the schema that storage lives in, or
  names it explicitly with a companion proving the sweep reports a planted violation
  inside it.
- `GET /admin/hangfire` returns 404 in every deployment mode.
- No module assembly references a job-runtime type.

**Identity and session**

- A token obtained from the Keycloak container over the committed realm export carries a
  `tenant_id` claim that parses to that seed tenant's `TenantId`, asserted against
  `SeedData`'s literals — in `backend-integration`, which is where a container runs.
- Login completes on **each** seed host, each returning to the host it started on, and a
  callback to a host with no registered redirect URI is refused; the BFF's code
  exchange, its single-flight refresh and its logout are asserted at route level in the
  `frontend` job against a stubbed token endpoint. Composing all three processes in one
  browser run is [Phase 06](phase-06-renderer-admin-studio.md)'s.
- A token signed by a realm key activated after startup validates after one JWKS
  refetch, and a token whose key was withdrawn is rejected after the refresh interval —
  both intervals set in test configuration, since a test left on the defaults either
  waits or passes vacuously.
- A `learnstack-hub`-realm token is 401 on the phase's named `/api/v1/*` endpoint, and
  so is a token whose issuer is wrong under a key the API trusts — the negative the
  structural realm-authority rule cannot see.
- On `demo-english`, an authenticated read answers 200 under the host-and-claim ceiling;
  a token naming the other seed tenant returns 404 and produces exactly one rejection
  row under the host's tenant carrying a non-null actor, the request's correlation id,
  and `metadata.assertionSource = jwt-claim`. A catalogue sweep finds no second slug for
  the cross-check.
- On `demo-yoga`, an organization host, the same request produces the outcome **G11**
  records — a resolved context with no membership read, or a deliberate 404 — asserted
  as the expected outcome with its matrix row named, not discovered.
- A signature-valid token carrying no `tenant_id` claim on a tenant host produces the
  outcome G11 records, asserted rather than skipped, so a lost mapper fails loudly
  instead of falling through to the host-only ceiling.
- An anonymous request to a routed non-public request type on a live tenant host answers
  what **G20** records, over a non-empty route set, and the authenticated refusal
  carries `tenant_mismatch` while the anonymous one stays byte-identical to an unknown
  host's.
- The realm export declares `tenant_id` and `user_id` in its user-profile configuration
  with admin-only view and edit; a variant export marking either attribute user-editable
  fails the case; and a seed user's own token cannot write either through Keycloak's
  account REST surface.
- Two principals behind one BFF do not share a quota: one exhausts the authenticated
  budget and the other's next request is not 429. The 429 carries the one Problem
  Details shape and `Retry-After`, and repeated mismatches from one token reach 429 and
  stop producing audit rows.
- A second `make seed` against a Keycloak database created from the pre-mapper realm
  JSON leaves every existing account and the `learnstack-hub` realm intact, applies the
  client scope and the mappers, and yields a token carrying a UUID-valued `tenant_id`. A
  third run changes nothing.
- `exp - iat` on a dev-realm token equals the lifetime the realm export declares, so
  raising one without the other fails.
- The callback route rejects a response whose `state` does not match the transaction
  cookie and one with no `code_verifier`; the session cookie is `HttpOnly`, `Secure`,
  `SameSite=Lax`, `Path=/` with no `Domain`; and no response body or script-readable
  cookie carries the refresh token.
- Two concurrent requests with a near-expiry token produce exactly one token request and
  one `Set-Cookie`; a terminal `invalid_grant` clears the session and serves the public
  page anonymously with a 200; logout ends the session at Keycloak and back-navigation
  does not restore an authenticated view.
- A cross-origin POST between the two demo hosts is refused, and so is a POST with a
  correct `Origin` and no session-bound token.
- An unauthenticated request to `(studio)` is redirected to sign-in and `(public)` is
  not.
- **Both [Phase 02d](phase-02d-walking-skeleton.md) sites still render anonymously with
  Keycloak stopped** — the session is additive to the anonymous path, never in front of
  it.

**Cross-cutting**

- `LS0002` reports on a synthetic violation as a Warning, stays silent on a
  non-sensitive interpolation and on a sensitive name outside a `throw`, produces no
  `AD0001`, and its token set equals the union of the catalogue's single- and two-word
  sets — proved by a token added only to the two-word set failing the drift case — with
  its rule name, catalogue entry and `AnalyzerReleases` row in the same commit as the
  analyzer.
- The phase's authenticated endpoint appears in the generated OpenAPI document and in
  the regenerated SDK surface.
- Every metric this phase emits is spelled as its owner spells it — the seven names
  [Events and Outbox § Observability](../architecture/15-event-and-outbox.md#observability)
  already carries, and the two liveness names **G17** registers in
  [Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics)
  — asserted against literal strings taken from that table rather than against the
  emitting type's own constant, which is true by construction.
- **No row in the [catalogue](../standards/21-architecture-tests-catalogue.md) whose
  Phase names 02b is still Registered or Awaiting backfill.** Structural rows are green
  in the `backend` check and behavioural rows in the `backend integration` check, which
  this phase makes a required one; neither job reports a skipped case in the assemblies
  this phase touches. Every structural rule this phase implements has a planted offender
  and a companion that fails when the rule's predicate is removed.

## Risks

- **A gate is skipped because its code looks writable without it.** Every gate in the
  register blocks a packet, and most of them need an amendment to an Accepted record. An
  amendment written after the code it governs is a record nobody can disagree with — the
  lesson Packet 9 recorded for its own readings. `P02b-0` is the mitigation, and it
  ships no code.
- **The claim fix is treated as a detail.** The specified lease is a few dozen lines,
  which makes it easy to defer past a green build: single-instance development never
  reproduces the failure, and a single-processor run of the concurrency test passes
  against the broken protocol. The mitigation is two processors with a forced
  interleaving, plus the lease-expiry companion, landing **with** the fix.
- **The partition key is persisted but meaningless.**
  [§ Ordering](../architecture/15-event-and-outbox.md#ordering) admits two shapes: the
  aggregate id for an event about an instance, and `TenantId` chosen deliberately for a
  tenant-wide fact. The failure modes are the other two — a key derived from `EventId`,
  which is a partition of one, and a per-type default, which is one partition for every
  tenant. The catalogued rule asserts non-blankness and detects neither, so **G3** asks
  whether the derivation is checkable structurally, and the ordering criterion it
  settles is what observes order.
- **Dead-lettering becomes invisible.** A terminal state that appears only in a database
  column is silent data loss. The counter and the audit row are part of the deliverable,
  and the audit row has no sanctioned writer until G7 closes — which is why G7 blocks
  the packet rather than trailing it.
- **The two dead-letter designs are both implemented.** They contradict each other in
  the two carriers that describe them, so an implementer who reads one and a reviewer
  who reads the other both pass. G1 picks one and deletes the other.
- **The Keycloak mapper regresses, or never reaches a warm workstation.** Realm JSON is
  regenerated by hand and by the admin console, and a mapper is easy to lose; worse, the
  import is consumed once, so a fresh-container test is green while every existing
  workstation issues claimless tokens. The mitigation is the reconciler *and* asserting
  the issued token rather than the file — the fresh-container test alone is the shape
  that cannot tell clean from blind.
- **Tenant context is forgotten in a background job.** The catalogued structural rule
  asserts statement order and is green whether restoration works or not, and it is one
  of two rules — the enqueue guard is the other. The mitigation is the behavioural
  restoration test, run as `learnstack_app` against a real row.
- **The outbox is treated as optional** — "module X will adopt it later." No reflection
  scan can observe whether a row committed with the change that raised it, so the
  mitigation is an integration test that commits and rolls back both together,
  registered as a new catalogue row rather than assumed to exist.
- **Authentication goes live with authorization still a pass-through.** Between this
  phase and Phase 03 any tenant user reaches every routed request type. G14 bounds the
  routable set and holds the bound with a rule; the token-keyed limits land in the same
  packet because the per-occurrence audit row depends on them.
- **The access-token TTL is the revocation window.** Between this phase and Phase 03 no
  membership check runs and `AuthorizationBehavior` is a pass-through, so a token keeps
  tenant access for its full lifetime after the attribute behind it is removed. The
  mitigation is the single TTL authority this phase links plus Phase 03's membership
  read; until then the window is a stated property, not an accident.
- **Refresh-token rotation is off in the dev realm.** The realm this phase hardens
  enables neither revocation on use nor a reuse limit, so the phase ships its first
  credential-holding session without rotation. Keycloak owns the mechanism and
  [Phase 11](phase-11-production-hardening.md) owns enabling it; this phase states the
  property rather than implying the default.
- **Keycloak becomes a sign-in bottleneck.** JWKS caching bounds the API's validation
  cost, not Keycloak's sign-in throughput; the dev stack already runs Keycloak on
  PostgreSQL, so that is inherited rather than mitigation. What this phase controls is
  that an anonymous page load depends on no identity provider, which the Phase 02d
  regression criterion asserts.
- **Keycloak claims leak into authorization decisions.** Keycloak authenticates;
  LearnStack authorizes. The realm currently ships three tenant-shaped realm roles with
  full scope on both clients, so the mitigation is the realm-role resolution above plus
  a rule proving no LearnStack assembly reads a role claim — not a link to the document
  that states the split.

## Phase Exit Decision

[Phase 03](phase-03-identity-admin.md) begins when:

- **ADR-0046 and ADR-0047 are Accepted**, and every dated amendment the register names
  is Accepted and reflected in its carriers.
- A seed user signs in through the BFF against the dev realm and the issued token
  carries a `tenant_id` claim that resolves to a real `TenantId`, on a **warm** Keycloak
  database reconciled by `make seed`.
- An authenticated request answers on a named `/api/v1/*` endpoint on `demo-english`,
  the organization host's documented outcome is asserted, and a `learnstack-hub` token
  is 401.
- The host-vs-JWT cross-check returns 404 and writes one row under Packet 9's existing
  operation key with a non-null actor and `metadata.assertionSource = jwt-claim`.
- The sample integration event flows enqueue → outbox → dispatch → idempotent
  consumption; two dispatchers holding live leases publish each message once; and a
  **forced redelivery** produces one business effect — the check that exercises the
  guard, which "no duplicate handling" alone does not.
- A poisoned consumer dead-letters visibly instead of looping: the terminal state for
  that consumer only, its counter, and its audit row.
- A MUST-classified direct-write consumer produces exactly one row for its delivery, and
  a rolled-back delivery exactly one standalone `failed` row — the delivery-frame
  hand-off Packet 9 left open.
- The background-job enqueue guard rejects a payload missing any one of its required
  fields, and a job runs under its restored tenant against a real row.
- Both Phase 02d sites still render anonymously with Keycloak stopped.
- `LS0002` ships as a Warning with its record, its catalogue entry and its release row.
- No catalogue row whose Phase names 02b is still Registered or Awaiting backfill; both
  CI checks are green with no skipped case in the assemblies this phase touches; and the
  behavioural job is a required check, with the branch-protection date recorded.

[Phase 02c](phase-02c-hub-foundation.md) is unblocked by this phase but does not gate
it. Phase 02c hangs off the spine and starts only when its own trigger fires — a tenant
that must be billed or plan-gated
([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)).
