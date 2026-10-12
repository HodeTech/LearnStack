# Phase 02b: Events, Background Jobs, Identity, and Session

**P02b-0 complete — 2026-10-12.** Phase 02d is complete and merged through PR #28;
its [merge closeout](phase-02d-walking-skeleton.md#p02d-7-merge-and-closeout-2026-10-11)
records the verified exit. The
[entry verification](#p02b-0-entry-verification-2026-10-12) reconciles the code,
corpus and Hub premises. P02b-1's
[decision package](#p02b-1-decision-package-2026-10-12) and ADR-0057 are
Accepted — 2026-10-12. G2, G3 derivation, G5 stored identities, G6 producer/common
ownership, G18 payload and G19 flow are Accepted; remaining gate parts stay open.
The four producer implementation steps are authorized. Step 1 lifecycle work is
implemented; both independent review rounds passed. The
[Step 1 record](#p02b-1-step-1--shared-execution-lifecycle-2026-10-12) owns
verification. Step 2 implements immediate enqueue; its
[record](#p02b-1-step-2--immediate-outbox-production-2026-10-12) owns verification
and both completed review rounds. Step 3 implements collection and the publisher;
its [record](#p02b-1-step-3--domain-completion-and-provisioning-publisher-2026-10-12)
records passing verification and both completed review rounds. Step 4 adds
production-census guards and catalogue enforcement; its
[record](#p02b-1-step-4--producer-enforcement-and-closeout-2026-10-12) records full
backend verification and both completed review rounds. **P02b-1 is complete;
PR review and merge remain pending.** The
[packet closeout](#p02b-1-packet-closeout-2026-10-12) owns the next P02b-2 handoff.

**Phase 02d handoff — 2026-10-11.** P02b-0 re-verifies the delivered `/api/v1`
public contracts, native HTTPS on retained `*.learnstack.local` seed hosts,
manual hosts/trust setup and the shared private hop-secret source. ADR-0053/0054
supply visitor and peer admission; ADR-0055 supplies one middleware-owned site
bootstrap per native request: two API calls per normal product document, one per
fixed status document and three for a followed missing detail. HEAD, RSC and
prefetch retain their separately proved accounting. Seed authoring distinguishes
tenant-wide rows from default- and sibling-organization rows; typed settings remain
uncached. Anonymous public reads retain no cookies or forwarded credentials.
The [Phase 02d delivery record](phase-02d-walking-skeleton.md)
owns verification and the CI/phase-exit state. G12/G14 re-verify these premises
before adding BFF/session behavior; auth routes may not shadow enabled
`/{locale}/courses...` paths. This handoff accepts no Phase 02b auth or BFF gate.

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

At the [P02b-0 entry audit](#p02b-0-entry-verification-2026-10-12), `IOutbox`
and step 7's implementation were absent. P02b-1 Step 2 now supplies immediate
enqueue and the enrollment/invariant guard. Still absent are `IInboxGuard`,
`inbox_messages`, the lease columns, `OutboxProcessor`, any `BackgroundService`
or `IHostedService`, Hangfire and its package pins, and the realm's protocol
mappers. Packet 5 shipped the `IInboxGuard` **seam**,
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
  question that
  [ADR-0036 § The platform-admin override is not a resolution source](../decisions/0036-tenant-resolution-trusted-inputs.md#the-platform-admin-override-is-not-a-resolution-source)
  says needs its own decision record.
- **The Hangfire dashboard.** No route is mapped in any deployment mode here.
  [Phase 03](phase-03-identity-admin.md) maps it behind the Platform-scope permission
  and carries the [ADR-0015](../decisions/0015-api-gateway-apisix.md) amendment its
  `platform-admin` role check owes;
  [Phase 11](phase-11-production-hardening.md#security) carries the APISIX half.
- **Job definitions.** This phase ships the runner, its tenant contract and its enqueue
  guard, and registers no job of its own. The first tenant-scoped jobs arrive in
  [Phase 04](phase-04-cms-media-pages.md) and
  [Phase 08a](phase-08a-assessment-notifications.md). The first **tenantless** ones are
  [ADR-0028](../decisions/0028-audit-log-partition-management.md)'s two audit jobs,
  whose implementation [Phase 11](phase-11-production-hardening.md) owns — and they are
  why **G9** has to settle a platform-job class: they run with no tenant to announce,
  against an enqueue guard that refuses a payload without one.
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
- **The full authenticated browser end-to-end suite.**
  [Testing Standards § End-to-End Tests](../standards/06-testing.md#end-to-end-tests)
  assigns the full Playwright Test/axe suite to
  [Phase 06](phase-06-renderer-admin-studio.md). P02d-7 already delivers a narrow
  real-stack public browser/Lighthouse proof, which remains enforced. This phase
  adds route-level session tests, container-backed token tests and the live cookie
  isolation checks its completion criteria name; those do not constitute a full
  browser-driven OIDC journey.
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
dependency order. `P02b-0` writes no code. Every packet after it opens with the decision
pass [Roadmap § Decision Timing](README.md#decision-timing) describes: its gates are
Accepted, and its catalogue rows registered, before its first line of code.

| Packet | Contents | Cannot start until |
|---|---|---|
| **P02b-0** | Phase entry: every premise the register below cites re-verified against `HEAD` — the code, the corpus, the Hub repository — and the register corrected where Phase 02d moved it. It drafts no record: each gate closes in the decision pass of the first packet it shapes, and each glossary headword this phase needs — `inbox`, outbox lease, dead letter, audit frame, job activator, integration-event consumer — is written in the pass whose gate fixes its meaning | [Phase 02d](phase-02d-walking-skeleton.md) exits |
| **P02b-1** | Producer: `IOutbox`, the `OutboxFlushBehavior` body, the domain-event collector and its single dispatch site, and the first event with its enqueue path | G2, G3 (the key's derivation), G5 (the stored identities), G6, G18 (the payload rule), G19 Accepted |
| **P02b-2** | Dispatcher: the lease migration and grant extension, claim / publish / mark, backoff, the producer terminal state, the credential and its boot guard, the metrics and the health check | G1, G3, G4, G5, G7, G8, G17, G18 (`last_error`) Accepted; P02b-1 |
| **P02b-3** | Consumer: the inbox DDL, `IInboxGuard`, the per-delivery transaction, the sample flow's consumer, per-subscription attempts, the dead-letter store and its audit row, replay as a dispatcher operation | G1, G6, G7 Accepted; P02b-2 |
| **P02b-4** | Background jobs: the runner, its storage, the tenant-context hook, the enqueue guard, no job definition | G9 Accepted; P02b-3 |
| **P02b-5** | API authentication: JwtBearer wiring and its insertion order, claim population, the host/JWT cross-check through the existing recorder, token-keyed rate limits, the 401 rows, one authenticated endpoint | G10, G11, G12 (the BFF's client), G14, G20 Accepted |
| **P02b-6** | Realm and seed: the shared client scope, the mappers, the user-profile declaration, UUID attributes agreeing with `SeedData`, the warm-realm reconciler, the Keycloak test fixture | G12, G13 (the access-token lifetime's authority) Accepted; P02b-5 |
| **P02b-7** | BFF session: Auth.js, the cookie session, single-flight refresh, CSRF, logout, the studio guard, the SDK's bearer attachment | G13 Accepted; P02b-6 |
| **P02b-8** | `LS0002`, the carrier reconciliations this phase owes, the re-verification that the behavioural check is still required under the name it reports, and the exit checks | G16, G18 (the erasure inventory) Accepted |

The events packets precede the identity packets because the Hub's `P02c-3` waits on this
phase's events half ([Phase 02c](phase-02c-hub-foundation.md)), not on its session half.

`P02b-4` follows `P02b-3` rather than running beside it. ADR-0047's retry contract
adopts G7's class, slug and posture, and a job frame owes the same owner duties as the
delivery frame `P02b-3` ships first, so the record is written against that frame rather
than its plan.

#### The decision register

Each row is a question this phase must answer before the packet it blocks starts. The
**Leaning** column records the reviewers' consolidated proposal and is **not** a
decision; the **Vehicle** column is what this repository's own rules require for the
answer to count. A gate whose vehicle is an amendment is Accepted **before** the code it
governs is written, not alongside it. It closes in the decision pass of the first packet
whose code it shapes, together with the gates coupled to it; where its parts shape
different packets, the **Blocks** cell names the part each packet waits on. Every
premise a row cites is re-verified at that pass rather than trusted.

| # | Question | Leaning | Vehicle | Blocks | Status |
|---|---|---|---|---|---|
| G1 | What is the subscriber's fate when its handler exhausts retries, and what is the inbox table — one shared or per module, keyed `event_id` or `(tenant_id, consumer, event_id)`, claimed by the handler or by the transport? Three arms travel with it: the class of consumer whose effect cannot share the inbox transaction (an external call, the platform-scope erasure handler the Audit spec already carries) and what "effectively once" means for it; whether an event without the organization marker is delivered tenant-wide regardless of the producing request's organization; and whether the inbox entity joins [ADR-0044 § 7](../decisions/0044-audit-write-path.md)'s named audit-capture exclusion list once a delivery declares intents | One shared tenant-owned `inbox_messages`, transport-claimed; the terminal state in its own store so "the inbox row is not marked processed" stays true | **ADR-0046** + a dated **ADR-0006 Amendment** (or an explicit supersession note in ADR-0046 naming Amendment 1's consumer-side paragraph, which fixes the per-module inbox) + a dated **ADR-0010 Amendment**, because its § Idempotency makes two things mandatory that the shared-table leaning changes — the inbox is "the module's **own** inbox table", and the guard is called by the consumer — + the `Integration_Event_Handlers_Use_InboxGuard` assertion re-written in the same record to name whoever ends up owning the call, since a transport-claimed delivery fails a structural rule that looks for the call inside the handler + Database Standards § Inbox + an ADR-0041 erratum beside ADR-0023's minting list | P02b-2 (the record and its amendments: the dispatcher's retry state and a failed delivery's next attempt are one state machine), P02b-3 (Database Standards § Inbox and the ADR-0023 erratum) | Open |
| G2 | Does `IOutbox.EnqueueAsync` write the row immediately on the ambient transaction, or buffer for `OutboxFlushBehavior` at step 7 — and what is the flush point for the two paths that have no step 7? | Immediate write, with the behavior asserting no buffered message survives the handler | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 2's step-7 completion role, with a dated supersession disclosure in ADR-0032 at acceptance; phase-doc + [Events and Outbox § Producer pattern](../architecture/15-event-and-outbox.md#producer-pattern) | P02b-1 | Accepted — immediate enqueue/owner completion |
| G3 | What replaces the unconditional same-key publish-order promise — a scoped bound, or strict per-key head-of-line blocking — and is the key's derivation checkable structurally, given that [§ Ordering](../architecture/15-event-and-outbox.md#ordering) admits an aggregate id or a deliberately declared tenant-wide key and nothing else? | Scope it: no column in `outbox_messages` carries an unconditional per-key guarantee, so the honest bound is one processor, no failure, enqueue order | Derivation: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md). Ordering: scoping is a phase-doc plus [§ Ordering](../architecture/15-event-and-outbox.md#ordering) edit; blocking is a dated ADR-0006 Amendment plus an index | P02b-1 (whether the key's derivation is checked structurally: the first event declares a key), P02b-2 (the ordering bound) | Derivation Accepted; ordering open (P02b-2) |
| G4 | What counts as an attempt, what is `MaxAttempts` bound to, and what marker keeps a terminal row terminal as the clock advances? | Six attempts (one immediate plus the five documented delays) and an explicit terminal column, because a pushed-out `available_after` stops being terminal and never leaves the pending gauge | Dated ADR-0006 Amendment + the Database Standards § Outbox DDL, index and grant edits riding it | P02b-2 | Open |
| G5 | Does the stored event identity stay the assembly-qualified type name with declared compatibility mappings, and what happens to an unresolvable type, a retired type and an event with no subscriber? And what is the **row's** identity — a surrogate `id` plus an `event_id` column, or `EventId` as the key — given that two documents list an `event_id` the canonical DDL does not have? | Keep the type identity, add a bounded resolver reading through `PayloadJsonOptions` and declared mappings; dead-letter a retired type at once rather than spending its attempt budget; reconcile the row's column list in one direction | Stored identities: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 12 and the Phase 02b deliverables table in Implementation Notes, with a dated supersession disclosure at acceptance; Database Standards alignment. Resolver outcomes remain a P02b-2 decision; replacing the type identity requires its own ADR-0006 vehicle | P02b-1 (the stored type identity and the row's identity, both written at enqueue), P02b-2 (unresolvable, retired and unsubscribed types) | Stored identities Accepted; resolution outcomes open (P02b-2) |
| G6 | When the unit-of-work owner is not an `AuditLogBehavior`, who performs the four owner-side audit acts and enters the outermost audit frame — and does a state-mutating delivery declare its own intents, keyed by handler type? | The transport opens the outermost frame beside its transaction; a delivery declares its own intents and an unregistered handler is refused as an unregistered request is | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md): bounded ADR-0040 nested-failure supersession; separately enumerated additive UoW read-seam amendment; clarifying ADR-0044/0033 common-owner amendments. G1-dependent production delivery/inbox implementation remains P02b-3 | P02b-1, P02b-3 | Producer/common owner Accepted; production delivery/inbox open (P02b-3 after G1) |
| G7 | For a dead-letter transition: what class, what `OperationType`, what slug, on which transaction, in what order against the state change, with what failure posture — and may a second non-MediatR caller reach `IAuditStore.WriteStandaloneAsync`? | class MUST, `OperationType` `security-event`, the slug open under the § The join grammar `{module}.{resource}.{verb}`; the row written before the transition, so a failed write leaves the transition unmade and the next poll retries | Dated ADR-0033 Amendment + `DeclareOffPath` registrations + the Baseline row in [Audit Coverage Standards](../standards/18-audit-coverage.md#baseline-coverage-learnstack-core-modules). If the chosen ordering persists the terminal transition and its MUST row on **separate** transactions, that changes what ADR-0033's Decision guarantees rather than clarifying it, and the vehicle is a superseding record, not an amendment | P02b-2 (the producer dead-letter row its criteria assert), P02b-3 | Open |
| G8 | Does the dispatcher's `BYPASSRLS` use carry a per-invocation `security-event` row, or is it exempt — and on what written terms? | Exempt, on three terms: the bypass is grant-bounded and code-path-confined, dispatch attempts are logged rather than audited, and the dead-letter transitions are the audited events | Dated ADR-0006 or ADR-0003 Amendment; an architecture document cannot grant it | P02b-2 | Open |
| G9 | Which record decides the background-job runtime — its storage under the closed four-role model, the single tenant-context writer, the job frame's transaction owner, the enqueue-site rule *and who may enqueue whose job type*, the retry contract (whose class, slug and posture adopt **G7**'s), the tenantless platform-job class a purge needs, the ADR-0035 classification, the queue grammar, and the package pins with their licence verdict? | One record answering all of it; a job reaches the database only through `ISender`, which leaves ADR-0040's closed setter set at eight | **ADR-0047** + a dated ADR-0003 Amendment for the storage + a dated **ADR-0036 Amendment** (or an erratum) if the job-path writer is not the `JobActivator` its § Rules names in the closed four-caller set, then Standards 05 and Standards 20 | P02b-4 | Open |
| G10 | Does the active tenant travel as a scalar `tenant_id` claim, a `memberships` array, or both — and what value does `UserId` hold? | The scalar claim, whose 02b source is an admin-set user attribute; a second UUID-valued `user_id` attribute for the actor, with `sub` staying Keycloak's subject | Phase-doc: [ADR-0036 § What is out of scope](../decisions/0036-tenant-resolution-trusted-inputs.md#what-is-out-of-scope-and-what-is-not) hands the shape to this phase by name. Reconcile `architecture/13`'s array sketch in the same change | P02b-5 | Open |
| G11 | Which [ADR-0036 matrix](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix) rows resolve in this phase, given that row 10 consults a reader that denies everyone — and what row covers a signature-valid token carrying no `tenant_id` claim on a tenant host? | Either mint `organization_id` scoped to the host's organization, which makes row 10 need no membership read, or declare the organization host's authenticated 404 and test it as the expected outcome | A dated **ADR-0036** amendment plus errata beside § Staging across packets and the 2026-09-02 erratum, both of which assign row 10 to this phase without noting that its `M covers (T, O)` term is denied until Phase 03 — which groups it with rows 7 and 14 rather than with 6 and 9 | P02b-5 | Open |
| G12 | Which Keycloak client does the BFF exchange the code with, where does sign-in happen across two tenant hosts, and how do the client scope, the mappers and the seed users reach a Keycloak database that has already consumed the import? Native HTTPS on retained `*.learnstack.local` hosts, with manual hosts/trust setup, is delivered by Phase 02d and re-verified by P02b-0 | A confidential BFF client with one redirect URI per seed host, retaining native HTTPS and `Secure` cookies; and an idempotent reconciler inside `make seed` — a mapper added only to the import reaches no existing workstation | Phase-doc + the realm JSON + `scripts/seed.sh` + `infra/keycloak/README.md`; a new ADR only if the reconciler becomes ADR-0004's `IIdentityProvider` | P02b-0 re-verifies [Phase 02d G32](phase-02d-walking-skeleton.md#the-decision-register), ADR-0053/0054 and ADR-0055; P02b-5 (which client the BFF redeems the code with: the API pins `azp` against it), P02b-6 | Open |
| G13 | What is the BFF session — its custody of the refresh token, its store, its idle and absolute lifetimes, its refresh serialisation under concurrent requests, its terminal `invalid_grant` behaviour, its CSRF control and its logout — and which document owns the access-token lifetime? | The `HttpOnly` cookie Security Standards already describes, encrypted by the session adapter, read only by server code, with single-flight refresh and one authority for the TTL | Phase-doc + one reconciled [Security Standards § Authentication](../standards/11-security.md#authentication); a **new ADR** only for a server-side session store | P02b-6 (which document owns the access-token lifetime: the realm export and its reconciler write it), P02b-7 | Open |
| G14 | What bounds the request types reachable over HTTP while `AuthorizationBehavior` is `return next()`, and what is the shape of the token-keyed rate-limit stage behind a shared BFF connection? | An enumerated read-only routable set held by a catalogue-registered rule, and a limiter stage after `UseAuthentication` keyed on the validated subject. The early anonymous stage already runs before authentication: P02d-5 delivers 60/min per canonical direct or authenticated-hop visitor IP plus 600/min peer acquisitions/allocations under ADR-0053 and ADR-0054. Exhausted known visitors refuse before peer debit; unknown visitor allocation remains peer-gated. These remain IP/peer quotas, not token quotas; a request they reject never reaches the token stage. Re-verify the [P5 remediation decision](phase-02d-walking-skeleton.md#p02d-5-external-review-remediation-2026-10-09) before choosing authenticated accounting, preserving pre-classification protection against unknown-host abuse | Phase-doc + catalogue registration; a dated ADR-0032 Amendment for a deny-by-default step instead | P02b-5 (the token-keyed stage and the early stage's treatment of validated callers); G34's anonymous-hop policy and ADR-0054's bounded accounting replacement are Accepted and delivered in P02d-5 | Open |
| G15 | What initiates a replay in this phase? | A dispatcher-level operation on the dispatcher's own connection, with the operator surface deferred | Phase-doc; the URL space is its own decision record, per ADR-0036 | — (answered in § The consumer side; recorded so the question travels with the deferral) | Answered in consumer scope; implementation pending |
| G16 | What is `LS0002`'s rule name, which project trees does it run over, what does it inspect, and what escalates it to Error? | The name and scope recorded the way Amendment 1 recorded `LS0001`, with the scope reaching the assemblies that actually handle tokens | A dated **ADR-0032** amendment + a catalogue entry + an `AnalyzerReleases` row | P02b-8 | Open |
| G17 | What are the label sets for the event and job metrics, is the tenant axis permitted, and is it spelled `tenant` or `tenant_id`? | One note in the document that owns metric names, no raw tenant label on a per-event series, and an oldest-eligible-row age metric separate from the pending count | [Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics) edit | P02b-2 (the first series ships there, and a label added afterwards touches every emitter already written) | Open |
| G18 | May an integration-event payload or a job argument carry personal data, what may `last_error` contain, and do the four new durable stores enter the erasure scope? | Identifiers only, a sanitized and length-bounded failure descriptor, and the stores named in the erasure inventory | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) decides the new producer payload/NULL JSON metadata policy and application byte cap; a dated ADR-0038 cross-reference at acceptance. [Data Protection § Right to Erasure](../architecture/23-data-protection.md#right-to-erasure-right-to-be-forgotten), P02b-8 and Phase 11 retain erasure ownership | P02b-1 (the payload rule: the first payload ships there), P02b-2 (the `last_error` rule and its DDL bound), P02b-8 (the erasure inventory) | Payload Accepted; failure descriptors and erasure open (P02b-2/P02b-8) |
| G19 | Where do the sample flow's publisher and consumer live — a fixture pair in the integration assembly, or a real event a shipped module consumes? | A real event between two shipped domain modules, because a test-assembly-only consumer leaves every module-scoped sweep with zero subjects | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) and the P02b-1 package; reconcile Tenancy/Customization/Audit specs at acceptance. G1 must settle distinct subscription deduplication and delivery scope before P02b-3 implements the two effects and Seeder handoff. The provisioning publisher must prove enqueue from its transaction announcement while ambient context remains unresolved | P02b-1 | Flow Accepted; subscriptions/Seeder handoff pending P02b-3 after G1 |
| G20 | What does an anonymous request to a **routed non-public** request type on a live tenant host answer — and does the catalogued `Backend_RequiresJwt_OnAllAuthenticatedRoutes` narrow to a surface outside tenant resolution? A second half rides with it: the shipped assertion middleware says that from this phase the refusal code differs by caller, `tenant_mismatch` for an authenticated one and `not_found` for an anonymous one, written by the middleware itself | The matrix wins on a tenant host — [row 2](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix) answers 404, byte-identical to an unknown host's, and the 401 rule narrows to the surface where a 401 discloses nothing | The dated **ADR-0036** amendment G11 needs + a catalogue edit to that rule's Asserts line | P02b-5 | Open |

### P02b-0 entry verification (2026-10-12)

**Complete.** This packet verifies entry premises and corrects current-state
claims. It writes no runtime code, drafts no ADR, accepts no later packet's gate
and introduces no glossary definition ahead of its owning decision pass.

#### Baseline and inherited enforcement

The audit used LearnStack `development` at
`3e86041dc32009ae6120c8cbb1533fd5d1325465`, tree
`7aac9df1ed19f0826b042fbd1a303dc92c9ab11a`. That commit reconciles the merged
P02d-7 source with PR #28's squash on `main`; the
[P7 merge closeout](phase-02d-walking-skeleton.md#p02d-7-merge-and-closeout-2026-10-11)
owns the merge/tree comparison and the completed phase exit.

The post-merge [CI run 38129121225](https://github.com/HodeTech/LearnStack/actions/runs/38129121225)
completed successfully on `main` at
`d37f46ccf0ebbf909f02c81a59d9019af3e9a1ca`. A fresh branch-protection API read
on 2026-10-12 confirmed strict up-to-date protection and all seven required
contexts: `backend (build + unit + arch + contract)`, `frontend (typecheck + lint
+ build + test)`, `secret scan (leakwatch)`, `meta (compose + commit hygiene +
link audit)`, `backend integration (Testcontainers)`, `openapi diff`, and
`public demo + lighthouse`. This receipt does not claim a new local full-stack
run or change those settings.

The Hub checkout was inspected read-only at
`6adf93638e76114eda9c18a5095d0fe3c0fd43b1`; fetched `origin/main` was
`310f718e5f2bad0dfb51e2c307c1ec361dab5952`. The checkout is an ancestor of that
main and their trees are identical:
`c5c380cfee42c6a32923d26d604ada7da2d5dc45`. Its roadmap still freezes P02c-2
onward pending the documented billing/plan-gate demand and ADR-0034 invariants.
Its P02c-3 waits on LearnStack's durable event path, not the BFF session.
This audit neither opens that track nor changes its contract surface.

The retained public baseline is native HTTPS, signed ingress, one live
middleware bootstrap and request-local immutable admission. Anonymous public
reads use the host boundary, physical read-only transactions, query filters and
RLS. They do not forward cookies or bearer credentials. The early limiter still
coordinates 60/min canonical visitors and 600/min physical-peer acquisitions/
allocations; known visitor refusals do not debit that peer budget. P02b-5 must
decide authenticated accounting without weakening this pre-classification bound.

#### Gate-premise audit

All twenty register rows were checked against their cited records and the
current implementation. **Verified** below means the premise is confirmed;
it does not mean the proposed answer is Accepted. G15 remains the register's
already-answered replay boundary. Every other row retains its decision vehicle
and first dependent packet above.

| Gate | Verified current premise and disposition | First decision consumer |
|---|---|---|
| G1 | No `IInboxGuard` declaration, inbox table or durable subscription failure state exists. ADR-0006/0010 still require a per-module, consumer-called inbox. Shared transport ownership remains a proposal requiring the registered record/amendments. | P02b-2 record; P02b-3 DDL |
| G2 | `OutboxFlushBehavior` only calls `next()`; no `IOutbox` exists. Immediate enqueue versus a buffer/flush policy remains undecided, including paths outside MediatR. | P02b-1 |
| G3 | The envelope requires a partition key and the in-process serializer orders invocation per key. The table has no durable sequence or retry ordering guarantee; key derivation and the dispatcher bound remain separate decisions. | P02b-1 derivation; P02b-2 bound |
| G4 | The table has `attempts`, `available_after` and `processed_at`, but no explicit terminal marker or lease columns. Five published retry delays do not themselves settle attempt counting or terminal state. | P02b-2 |
| G5 | Canonical DDL has a surrogate `id` but no `event_id`; ADR-0032 lists both. No durable type resolver or enqueue implementation resolves that mismatch yet. | P02b-1 identities; P02b-2 resolution |
| G6 | The bus restores the accessor and creates handler scopes without opening a transaction or audit frame. Pipeline audit ownership cannot simply be assumed for delivery/domain-event owners. No aggregate currently raises a domain event. | P02b-1; P02b-3 |
| G7 | No producer/subscriber terminal writer exists. The MUST durability contract, off-path caller restriction and transaction atomicity still apply; a multi-transaction alternative needs the vehicle the register names. | P02b-2; P02b-3 |
| G8 | The outbox-admin role and its bounded grants exist; a running dispatcher does not. No Accepted dispatcher-specific audit exemption has been added by this audit. | P02b-2 |
| G9 | No job runtime, package pin, storage, activator or enqueue guard is shipped. The closed four-role and tenant-context-writer contracts remain constraints, not implicit permission for a new runtime. | P02b-4, after P02b-3 |
| G10 | The API has no JwtBearer wiring. Dev users carry slug-valued tenant attributes without explicit protocol mappers; the scalar/array and actor-UUID choices remain open. | P02b-5 |
| G11 | The resolver's claim insertion point is reserved; membership reads still deny everyone. Tenant-only authentication on an organization host and a claimless valid token need explicit outcomes before activation. | P02b-5 |
| G12 | Delivered HTTPS/host/hop/bootstrap behavior is retained. Dev redirects still target localhost HTTP, and `make seed` polls realm readiness without reconciling mappers or warm users. | P02b-5 client; P02b-6 realm |
| G13 | No Auth.js session/helper exists. Access-token lifetimes in the realm and target documents differ; storage, refresh, CSRF and logout remain decisions before their first writes. | P02b-6 TTL; P02b-7 session |
| G14 | `AuthorizationBehavior` remains pass-through. Current HTTP product operations are public reads; anonymous coordinated admission already runs before classification. A protected read census and validated-subject quota do not yet exist. | P02b-5 |
| G15 | Replay remains an internal dispatcher operation for this phase; the operator URL/permission belongs to Phase 03. No recovery endpoint is shipped or authorized here. | Answered; P02b-3 implements |
| G16 | Only `LS0001` is shipped. `LS0002` needs scope, token-source parity, severity/escalation and its release/catalogue entries; it is not a current build-breaking guard. | P02b-8 |
| G17 | Metric carriers disagree on tenant-label spelling and cardinality. Dispatcher heartbeat and oldest-eligible-row age have no emitter yet. Resolve the label set before the first series ships. | P02b-2 |
| G18 | No shipped integration payload/job argument commits this phase to PII. `last_error` is unbounded text today; the future outbox, inbox, subscriber-failure and job stores need the registered erasure inventory. | P02b-1 payload; P02b-2 failure; P02b-8 erasure |
| G19 | No production integration event is published/consumed. Tenancy books three future topics while Audit consumes none. Typed settings are uncached, so a settings-invalidation consumer needs a real effect rather than an artificial cache. Provisioning also has an unresolved ambient context despite its transaction announcement. | P02b-1 |
| G20 | Only public product reads are routed. Existing non-public 404/401 and authenticated assertion-code questions remain unresolved; authentication must not silently choose framework defaults over the matrix. | P02b-5 |

Implementation evidence was read in the pipeline's `OutboxFlushBehavior`,
`TransactionBehavior` and `AuditLogBehavior`; the platform infrastructure
migration; `InProcessEventBus`, `IntegrationEventEnvelope` and
`EventTenantContext`; API startup, resolver and rate limiter; the Keycloak realm
exports and `scripts/seed.sh`; the uncached `TenantSettingsAccessor`; and the
web middleware, admission/resource loaders and configured caller. Existing
architecture and integration tests were inspected to distinguish accessor
restoration from database announcement and durable delivery.

#### Reconciliation and next packet

Current-state corrections in this packet cover the separate seeder executable,
the already-delivered public browser proof, absent API authentication, the
organization-claim choice still owned by G11, logical session-version wording,
future auth examples, absent delivery transactions/audit frames, planned LS0002
severity and the existing required-check/zero-skip enforcement. Architecture 13
now labels its identity/session diagrams as targets; Architecture 15 separates
the shipped transport from its future inbox/transaction obligations. Accepted
ADR bodies and historical Phase 02d delivery records remain unchanged.

Other target sketches that depend on an open gate remain target sketches.
Their packet-owned reconciliation is explicit in the register and deliverables:
P02b-1 updates producer/domain-event/payload carriers and the sample module
specs; P02b-2/3 update delivery, inbox, roles/grants, failure and metric carriers;
P02b-4 owns job storage/runtime carriers; P02b-5–7 own authentication, realm,
session and auth-skill carriers; P02b-8 owns analyzer and erasure closeout.
The Hub's per-module inbox reference remains consistent with the current
Accepted baseline; a different G1 decision must reconcile the cross-repo
reference without thawing the Hub roadmap.

**Next: P02b-1 decision pass.** Prepare G2, G3's derivation, G5's stored
identities, G6, G18's payload rule and G19 as one reviewable package, including
the domain-event mutation/dispatch boundary and every owning frame's audit
duties. Define the first real cross-module effect before choosing its event.
Any required ADR/amendment must be drafted and approved before implementation.
Only then register its tests, split the implementation into reviewable steps
and begin producer code. P02b-0 requires no new product or architecture choice
from the maintainer.

#### Validation

Local Release architecture tests: **203 passed, zero failed, zero skipped**.
ADR lifecycle/disclosure tests: **7 passed**. The changed-document sweep checked
590 relative links and 164 Markdown fragments, with no broken target. Added
prose satisfies the 88-column rule; `git diff --check` is clean. The residual
scan found only CLAUDE.md's unchanged prohibition on local analysis references,
not a new dependency on a private document. No local full integration, browser
or Lighthouse rerun was needed for this documentation-only packet; the exact
post-merge CI receipt above owns that inherited evidence.

<a id="p02b-1-proposed-decision-package-2026-10-12"></a>

### P02b-1 decision package (2026-10-12)

**Accepted — 2026-10-12, @cemil.** The maintainer approves the revised package
and [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
This is the producer decision record. The
[Step 1 record](#p02b-1-step-1--shared-execution-lifecycle-2026-10-12) tracks
the first runtime delivery; acceptance alone closes no implementation step. The
[Step 4 record](#p02b-1-step-4--producer-enforcement-and-closeout-2026-10-12) owns
final validation, reviews and the packet closeout.
Only the gate parts listed below are Accepted. ADR-0057 supplies the authority
for the coupled producer choices. It explicitly
replaces ADR-0032's step-7 flush/separate event-ID column and ADR-0040's
absorbable-failure rule only for successfully produced work. Other Accepted
rules and historical delivery records stay intact.

#### Document and code audit

The decision pass re-read the inherited event, transaction and audit decisions
(ADR-0006/0010/0032/0033/0038/0040/0042/0044), the producer/consumer architecture,
Database/Observability/Audit/Infrastructure standards, the three module specs
and their matrices, the production pipeline and persistence seams, the existing
transport and both composition roots, and the actual provisioning/Seeder paths.
The [entry audit](#p02b-0-entry-verification-2026-10-12) remains the authority for
P02b-0 evidence; it is not rewritten as a P02b-1 delivery claim.

Two independent read-only audits examined producer/audit boundaries
(GPT-6-astra, xhigh) and real subscriber usefulness/seed compatibility
(GPT-6.1-sol, xhigh). These pre-implementation code facts shape the decision:

- The outbox DDL already exists; it has `id`, not a separate `event_id`.
  `IOutbox` is absent and step 7 is a pass-through. A shared SaveChanges is
  neither necessary nor sufficient; the one ambient transaction is the boundary.
- Provisioning deliberately leaves `ITenantContext` unresolved and announces
  its new tenant on the transaction. The Seeder has neither a guaranteed
  correlation value nor an Activity listener. An HTTP-only enqueue check fails
  its first real producer.
- Joined UoW frames are not savepoints. Discarding an inner event queue cannot
  undo its saved state or already inserted outbox row.
- Audit entry/reconciliation is currently specific to `AuditLogBehavior`.
  A direct delivery needs common ownership rather than pretending a nested
  request owns a transaction it only joined.
- Typed settings are uncached; definition reads probe fresh generations. Neither
  needs an invented invalidation consumer. Audit already durably records source
  changes, so an asynchronous mirror adds no honest useful effect.
- The existing generic defaults are real data with a declared product purpose.
  Their automatic installation changes orchestration ownership; it is not a
  currently implemented consumer. Current seed verification pins their IDs,
  so P02b-3 needs an explicit handoff before enabling delivery races.

#### Accepted gate answers and implementation boundary

The ADR owns exact semantics and bounds; this table owns gate timing. Maintainer
approval accepts this package and ADR-0057 together. The decision-only acceptance
commit and Registered catalogue obligations precede runtime code. Acceptance is
not an implementation or passing-test claim.

| Gate / part | Accepted answer | Acceptance carrier / remaining owner |
|---|---|---|
| G2 | Immediate ambient INSERT; step 7 checks enrollment, while the owner drains domain events before MUST audit/commit | ADR-0057 bounded supersession of ADR-0032 Sub-decision 2; dated supersession disclosure and architecture producer-pattern reconciliation |
| G3 derivation | Explicit aggregate-ID or declared tenant-wide source; canonical key and falsifiable structural/behavioral proof | ADR-0057 and existing `Integration_Event_Declares_PartitionKey`; publish ordering remains P02b-2 |
| G5 stored identities | Assembly-qualified type; `id = EventId`, no extra column; runtime-type JSON with existing frozen options | ADR-0057 bounded supersession of ADR-0032 Sub-decision 12 and the Phase 02b deliverables table in Implementation Notes; dated supersession disclosure and Database Standards alignment; resolver failure policies remain P02b-2 |
| G6 producer/common owner | One collector/drain and reusable audit entry; outbox-only domain reactions; absorbed fail-after-produced-work poisons the unit, including descendant receipts | ADR-0057 bounded ADR-0040 nested-failure supersession; separate additive UoW snapshot-reader amendment and ADR-0044/0033 clarifications; production delivery/inbox atomicity remains P02b-3 after G1 |
| G18 payload | Identifier-only additional fields, inclusive 256 KiB canonical UTF-8 application cap, SQL NULL JSON metadata, no copied personal data or serializer escape | ADR-0057 new producer policy with dated ADR-0038 cross-reference; no outbox-size DDL CHECK claimed; sanitized errors P02b-2, erasure inventory P02b-8 |
| G19 | Completed tenant provisioning produces `TenantCreatedIntegrationEventV1`; two Customization subscribers initialize `card@1` and `plain@1` without overwriting | This package and ADR-0057; module-spec reconciliation at acceptance, publisher P02b-1; G1 must settle independent subscription identities before P02b-3 subscriptions and Seeder handoff |

The additional nested-refusal protection is deliberate, not a correction to an
old statement: ADR-0040's prior rule was valid when accepted. Its exception and
ordinary event-free refusal semantics survive. No independent scope may bypass
the producer/audit coordinator by opening a raw transaction and calling outbox.

The acceptance carriers disclose ADR-0057's exact bounded supersessions and
separate additive/clarifying amendments. Current producer guidance in
architecture 15, standards 02/05/10/18/20, module specs and event/review skills
uses owner-only completion and outbox-only domain reactions. The five new
catalogue obligations are Registered, with Implemented counts unchanged.
Consumer target guidance remains under its existing decisions until P02b-2's
G1 pass. Typed settings are uncached and definition reads probe fresh
generations; there is no useful settings-invalidation or Audit-mirror sample.
A later settings cache needs its own measured need and freshness decision.

#### Four authorized implementation steps

All work stays on `development`. Each step includes implementation tests and
an explicit commit, then **two independent review rounds with fresh agents**.
Each round covers security/isolation, correctness/lifecycle, architecture/audit,
test falsifiability and carrier accuracy. Use GPT-6-astra xhigh for transaction/
audit risks and GPT-6.1-sol xhigh for independent structural/test/doc review;
adjust scope rather than weakening critical review. Verify every finding before
fixing and commit confirmed fixes; close both rounds before the next step.

| Step | Deliverable | Required evidence before its review closeout |
|---|---|---|
| 1 — Shared execution lifecycle | Named `GetTenantContextAnnouncement` reader and immutable transaction-generation snapshot, producer invocation receipts, common audit entry/owner coordinator and guaranteed no-listener trace context; request pipeline retains existing outcomes | Owner/joiner and descendant receipts, event-free absorbed control, no context/setter expansion, same-scope cleanup, existing audit/public read-only regressions, controlled direct non-MediatR owner |
| 2 — Immediate outbox | `IOutbox`, ambient parameterized implementation, identity/metadata/payload admission and step-7 invariant guard; both compositions wired | Real `learnstack_app` INSERT/rollback and cross-tenant refusal, exact persisted envelope/runtime round-trip and SQL NULL JSON metadata, inclusive/multibyte byte-bound proofs, duplicate/oversized/malformed/stale/read-only refusal, no extra connection or publish, MUST-audit failure atomicity |
| 3 — Domain completion and real publisher | Successful-save collection, owner-only FIFO drain, outbox-only translation and completed-provisioning publisher used by API and Seeder | Multi-save/detach/delete, save failure/cancellation, absorbed direct/descendant producer refusal, bounded drain, no replay after rollback/scope reuse, exact one event per successful provision and zero on rollback; response version remains accurate |
| 4 — Enforcement and closeout | Production-subject architecture sweeps with planted controls, end-to-end producer regression, all affected docs/skills/catalogue states reconciled | Release/format and applicable complete backend suites with zero skips; exact test evidence, links/anchors and immutable ADR history; inspect required CI on the eventual PR without claiming unrun results |

Use the implementation workflow's `add-integration-event`,
`add-mediatr-handler`, `add-integration-test` and `add-architecture-test` skills
for the relevant surfaces, plus `run-tests-locally`, `standards-check`,
`code-review` and `commit-and-pr`. No producer schema change is authorized;
unexpected schema needs reopen planning rather than borrowing P02b-2's lease work.

No step is complete with review findings or required evidence pending. Partial
step commits are review checkpoints, not packet delivery claims. The final PR
remains for maintainer review; a local pass does not imply merge.

#### Named handoff to P02b-3

P02b-1 is authorized to supply the production publisher and pending rows.
Steps 1–3 implement producer runtime; dispatcher claims and delivery belong to
P02b-2/P02b-3. P02b-3 must deliver both named defaults in
separate one-root subscriber transactions after G1 settles distinct subscription
identities and delivery scope, preserve any authored/lifecycle decision
(including deletion), and prove redelivery and sibling failure recovery.
It also switches built-in seed verification to owner-resolved key/version
identity while preserving warm IDs and exact authored curriculum IDs. Fresh
seed/dispatcher races, repeated-run neutrality, affected audit fixtures and the
seed inventory must be verified together before that packet closes. That packet
also updates `BuiltInCustomizations.cs`'s creator/Seeder/Hub installation remarks
to the delivered subscriber ownership; no consumer is claimed delivered now.

G1/G3-ordering/G4/G5-resolution/G7/G8/G17 and G18's remaining parts remain open
for their named packets. This approval neither decides inbox ownership nor adds
jobs, authentication, Hub integration, Dapr or another worker process.

#### Decision-draft review and validation

Two fresh independent draft reviewers (GPT-6-astra xhigh for runtime/transaction
semantics; GPT-6.1-sol xhigh for corpus/gates/seed scope) approved the proposal
for maintainer review. Neither found a blocking issue. The corpus review's
carrier reminder was verified and incorporated above: Backend Standards 02 and
this phase's domain-event sketch must be reconciled on approval. These are
decision-draft reviews, not the two review rounds each implementation step owes.

**External-review revision — 2026-10-12.** The maintainer's review is not an
approval. ADR-0057 now classifies each normative supersession, additive seam
amendment and clarification separately; ADR-0041 correction mechanisms do not
authorize the changed decisions. It names the UoW snapshot getter and its
generation/cleanup obligations, closes the JSON metadata channel with SQL NULL,
and identifies the UTF-8 cap as application admission rather than a DDL CHECK.
G1 must settle independent subscription identities before consumer code;
P02b-3 explicitly owns the `BuiltInCustomizations.cs` remarks handoff. The event
version suffix already follows the authoring skill, so no event rename is
needed; its inconsistent input-order example is a named acceptance carrier.
Source-linked drivers, reopening criteria, deferred ownership and the missing
ADR references are also explicit. All P02b-1 gates remain open pending approval.

Two fresh independent revision reviewers (GPT-6-astra xhigh for governance and
the UoW seam; GPT-6.1-sol xhigh for payload, G1/G19 and carriers) approve the
revised draft for maintainer review. The governance review caught an incorrect
provisioning-amendment attribution; the reference now points to the existing
`IUnitOfWork` provisioning seam, and the fix was rechecked. Neither reviewer
reports a remaining actionable finding. These reviews do not accept the ADR or
count as implementation-step reviews.

The seven ADR lifecycle-parser tests pass. A local file-and-heading check resolves
all relative links/anchors in the changed draft carriers; it is not a new CI
anchor gate. Accepted ADR files and the P02b-0 entry record remain unchanged.
No backend runtime suite was needed or claimed for this documentation-only
preparation. Maintainer approval remains pending.

#### Maintainer acceptance — 2026-10-12

The maintainer explicitly approved the externally revised ADR-0057 and this
package after the draft reviews recorded above. The pending-approval statements
in that dated draft history describe the earlier review state. This acceptance
closes only the six gate parts in the accepted table. Five canonical proofs are
Registered; no runtime implementation, step review or runtime test is claimed
by this decision record.

### P02b-1 Step 1 — Shared execution lifecycle (2026-10-12)

**Step 1 complete; both independent review rounds passed.** Work is
on `development`, following the Accepted four-step package. This is not P02b-1
completion. No immediate enqueue, successful-save event collector, publisher or
production delivery is claimed by this step.

- `IUnitOfWork.GetTenantContextAnnouncement` returns an immutable normalized
  tenant/organization snapshot with its physical transaction generation. The
  PostgreSQL unit captures the values actually sent to SQL, clears authority
  before a fresh attempt and on every terminal path, and refuses absent/foreign
  transactions. Joins reuse the owner's announcement without retargeting it.
- Scoped `ProducerExecution` records sticky successful-production receipts on
  an invocation and every ancestor, separately from pending event counts. The
  common transaction coordinator poisons a refusing invocation before resolving
  its frame when it carries such a receipt. Ordinary event-free absorbed refusals
  retain their previous behavior. Production needs a live writable announcement
  and an enrolled owner. A W3C Activity exists even without telemetry listeners.
- `AuditLogBehavior` and `TransactionBehavior` are thin adapters over common
  coordinators registered by the pipeline helper in both API and Seeder. Audit
  classification precedes deferred direct-handler construction. Capture-owned
  audit frames determine outer reconciliation; the UoW owner writes MUST rows
  and reports actual commit durability. An independent DI scope has independent
  ownership even inside another audit execution's async flow.
- Architecture/audit/security guidance and the glossary describe the common
  ownership seams. The closed context-writer set, eight logical announcement
  paths, pipeline order and public read-only mode are unchanged.

**Verification before the implementation commit.** A fresh `CI=true` Release
build succeeds with zero warnings/errors. The focused pipeline unit run passes
83 cases; the complete unit rerun passes 1,713. The focused integration run
passes 125, including all UnitOfWork,
ambient UoW, tenant-context guard, audit pipeline/workflow/capture, public
read-only, provisioning, direct-producer and cross-cutting HTTP cases selected
for this step. Both runs report zero failures and zero skips. The integration
proofs use the application role; privileged connections only prepare fixtures.
The new direct-producer class contributes 13 cases. Its receipts are deliberately
recorded after positively observed writes; automatic INSERT/save-promotion
receipt sites are Step 2/3 obligations, not evidence supplied here.

The snapshot matrix adds 21 cases, including successful SQL values versus a
mutated source context, unresolved provisioning, join/foreign/stale transaction
checks, pooled same-session reuse, failed/cancelled announcement/setup and every
terminal path. The final complete architecture run passes 203 cases with zero
failures/skips. The local carrier check resolves 719 relative links and 153
Markdown fragments with no broken target; added prose width and
`git diff --check` are clean.
Required CI and final packet counts are not claimed by this local checkpoint.

#### Step 1 review round 1

Two fresh independent reviewers examined `b157650..cbab31a`: GPT-6-astra xhigh
for lifecycle/security/audit and GPT-6.1-sol xhigh for proof/architecture/carriers.
Two findings were confirmed: public `BeginInvocation(true)` could assert raw
transaction ownership, and the receipt test did not distinguish all ancestors
from only the immediate parent. An isolated assembly mutant implementing that
weaker propagation still passed all original 13 direct-producer cases.

Enrollment is now internal to core Application; only the common coordinator
opens frames. Raw announced owners and orphan joiners have explicit refusal
controls. A depth-four invocation case exercises receipt propagation past an
intervening successful ancestor. The UoW XML comment names the common coordinator
and adapter correctly. The reviewers' stale-ancestry observation was reproduced:
an old owner could lend ownership to a later transaction's joiner. The new test
failed on that behavior; inheritance now requires the parent's live announcement
generation to match. The same test passes only after that change, with outer
audit cleanup included. The fresh Release build has zero warnings/errors;
the expanded focused integration run passes 129 cases with zero failures/skips,
including all 17 direct-producer cases. The same isolated weak-propagation mutant
now fails exactly the depth-two-descendant case (1 failed, 16 passed); restoring
the unmodified assembly passes all 17. The complete architecture rerun passes
203 cases with zero failures/skips. The second independent review round remains
pending at this checkpoint.

#### Step 1 review round 2 and closeout

Two new independent reviewers examined `b157650..ab2c37d`: GPT-6-astra xhigh
for lifecycle/security/audit and GPT-6.1-sol xhigh for proof/architecture/carriers.
Both approve Step 1 with no Blocker/Major findings. Their independent reruns
pass 59 audit/transaction unit cases, 73 producer/UoW integration cases and
25 persistence architecture cases, with zero failures/skips. These are selected
subsets of the runs above, not additional distinct test totals.

The confirmed minor carrier findings are corrected in this closeout: the
catalogue names coordinator delegation and the outer audit coordinator's clear
ownership; the Security derivation header includes ADR-0057. Canonical rule names,
statuses and counts remain unchanged. The full producer contracts still await
Steps 2–4; this closeout does not claim automatic production receipts, domain
collection or completed P02b-1 delivery.

### P02b-1 Step 2 — Immediate outbox production (2026-10-12)

**Implemented; both independent review rounds complete.**
`IOutbox.EnqueueAsync` persists immediately through the admitted producer's
existing connection and transaction. API and Seeder register `PostgresOutbox`;
no transport, extra connection, commit or schema migration is added.

- Admission checks the unit's identity, live announcement/generation, writable
  non-poisoned frame and event tenant before serialization and again immediately
  before INSERT. Explicit aggregate or tenant-wide partition declarations must
  match the actual canonical GUID key. Organization comes from announced SQL
  authority, while the invocation captures the causal actor and full W3C span.
- The row retains producer `EventId`, concrete assembly-qualified type and all
  admitted identifier payload properties. Serializer escape shapes are refused;
  the complete canonical UTF-8 payload has an inclusive 262,144-byte bound.
  JSON metadata is SQL NULL. A duplicate INSERT is an error.
- Only a successful INSERT records a sticky receipt in the invocation and all
  ancestors. Step 7 checks live enrollment before and after dispatch; it does
  not flush a buffer or dispatch domain events. Event-free unresolved and
  public read-only work retain their admission semantics.

**Verification before the implementation commit.** The `CI=true` Release build
passes with zero warnings/errors. All 1,757 unit and 203 architecture cases pass
with zero failures/skips. The new real-PostgreSQL outbox class passes all 29
cases, including immediate visibility, atomic commit/rollback with business and
MUST audit rows, provisioning, stale transaction-object reuse, distinct-unit
refusal and exact canonical UTF-8 boundary values. Initial failures exposed two
fixture assumptions (telemetry subscription and SystemActor identity); both
fixtures were corrected before the passing run. No runtime defect is hidden by
those corrections. Domain-save collection and its first publisher are Step 3;
no dispatch or consumer delivery is claimed here.
The separate existing-flow regression passes 166 integration cases, including
the convergent seeder, audit, provisioning, UoW and public read-only paths, with
zero failures/skips. The carrier audit resolves 1,158 relative links and 216
Markdown fragments with no broken target; added prose width, formatting and
`git diff --check` are clean. Required CI is not claimed by this local checkpoint.

#### Step 2 review round 1

Two fresh independent reviewers examined `7541b73..db882e0`: GPT-6-astra xhigh
for transaction/security/envelope correctness and GPT-6.1-sol xhigh for
proof/architecture/carrier integrity. Both approve with no Blocker/Major
findings. The confirmed minor finding was an undated inherited-absence paragraph
still describing `IOutbox` and step 7 as absent. It now distinguishes the P02b-0
entry baseline from Step 2 delivery; the frozen entry audit is unchanged.

The lifecycle reviewer independently passes 79 focused unit and 29 real outbox
integration cases, with zero failures/skips. These overlap the implementation
runs above. The corpus reviewer independently resolves 723 relative links and
165 fragments in changed carriers, with no unresolved target. The second fresh
review round remains pending at this checkpoint.

#### Step 2 review round 2 and closeout

Two new independent reviewers examined `7541b73..37d95bd`: GPT-6-astra xhigh
for runtime/security and GPT-6.1-sol xhigh for proof/architecture/carriers.
Both approve with no Blocker/Major findings. Each independently passes 61
focused unit, 203 architecture and 29 outbox integration cases, with zero
failures/skips. These overlap earlier runs and are not additive suite totals.

The confirmed minor test gap was transport silence: an unsubscribed event would
not expose an accidental publish. A real API-root test now resolves a counting
transport, positively observes the pending and committed row, and proves zero
publishes through enqueue and owner completion. Calling that same resolved
transport afterwards proves the counter works. The fresh Release build has zero
warnings/errors; all 30 outbox cases pass with zero failures/skips.
Step 4 still owns the transitive producer/helper fence against publication,
extra connections and commit ownership, alongside the complete production
censuses. Step 3's save collector, domain causation and publisher remain pending.

### P02b-1 Step 3 — Domain completion and provisioning publisher (2026-10-12)

**Implemented; both independent review rounds complete.**

- A shared SaveChanges interceptor snapshots only changed sources of its saving
  context, then promotes facts after nonzero successful persistence. Failed,
  canceled, suppressed and zero-row saves promote nothing. Separate source
  retention clears uncollected or detached facts; it cannot promote them on an
  unrelated save. Repeat saves do not duplicate successfully promoted facts.
- The transaction owner alone drains the FIFO queue before producer invariants,
  MUST audit and commit. Saved facts retain their saving invocation, generation,
  W3C span and causal human through child disposal. Translation mints a new
  integration identity and records the domain identity as causation. Successful
  promotion marks every ancestor; a produced-work refusal poisons before its
  frame resolves. Total production is bounded at 256 domain events.
- API and Seeder install the same collector/interceptor. Owner termination and
  scope disposal clear source lists, staged/queued facts and promotion state;
  later successful transactions in the same DI scope cannot replay facts.
- The tenant's initial default-organization assignment raises
  `TenantCreatedDomainEvent`. The final successful provisioning save qualifies
  it for outbox-only translation into `TenantCreatedIntegrationEventV1`.
  One pending row commits with tenant, default organization/back-reference and
  both existing MUST audit rows. No dispatch or default subscriber is delivered;
  existing seed orchestration remains unchanged until P02b-3's named handoff.

**Verification before the implementation commit.** The fresh `CI=true` Release
build passes with zero warnings/errors; all 1,764 unit and 203 architecture cases
pass with zero failures/skips. The final focused producer/provisioning integration
run passes 68 cases with zero failures/skips, including both real API/Seeder
roots, exact metadata and successful/failed MUST provisioning. A separate
existing-flow run passes 152 integration cases with zero failures/skips,
covering audit, UoW, public read-only and convergent seed behavior. The focused
post-format unit rerun passes 28 provisioning/domain cases; it overlaps the full
unit total above.

The initial focused integration run exposed two fixture defects: EF refused a
duplicate alternate key before SQL, and two fault cases tried to reuse an
irreversibly poisoned UoW. The corrected cases reach the real database uniqueness
failure and verify poisoned-scope refusal; a separate successful-owner case
proves later-generation reuse without replay. The full focused rerun above is
green. These corrections change no runtime policy and conceal no failing case.
Step 4 still owns complete structural enforcement and catalogue promotion;
required CI and packet completion are not claimed at this checkpoint.

#### Step 3 independent reviews

Round 1 reviewed `937e36a..d6fbd85` with separate GPT-6-astra xhigh
runtime/transaction and GPT-6.1-sol xhigh proof/corpus reviewers. Both approved
without a verified actionable finding. The proof reviewer independently reran
28 domain/provisioning integration and 28 focused unit cases with zero
failures/skips. Round 2 used two new agents with the same separate lenses;
both approved, and its proof reviewer independently repeated those same
28 integration and 28 unit cases with zero failures/skips. These focused repeats
overlap the implementation evidence; they are not added to its totals.

The reviews checked real SQL/save qualification, retained child metadata,
ancestor receipts, FIFO/count bounds, owner/audit atomicity, terminal cleanup,
response versions and both production registrations. No runtime fix was needed.
Complete production censuses and catalogue promotion remain Step 4 work.

### P02b-1 Step 4 — Producer enforcement and closeout (2026-10-12)

**Step 4 complete; both independent review rounds passed.** Work remains on
`development`. The packet closeout below records full local verification;
the PR's required checks own fresh CI evidence. PR approval and merge remain
pending.

- `IntegrationEventContractTests` independently checks the complete production
  project/module-layer census, base record/required members, declared partition
  sources and identifier-only payloads. Stored-source IL and independently
  varied tenant/event/aggregate identifiers exercise key derivation. Invalid
  declarations, unrelated/constant keys, hidden members, serialization overrides
  and disallowed payload shapes have dirty/clean controls; concrete runtime JSON
  round-trips preserve approved identifiers. Unsupported computed partition
  derivations fail loudly rather than silently leaving the proof's scope.
- `DomainEventHandlerTests` follows assembly-scoped helpers, inherited/generic
  interfaces and generated async/lambda bodies across every production project.
  Planted roots/helpers exercise every module layer. Root/write-store mutation,
  nested requests, further facts/notifications, connection/UoW access, provider
  ports and service location are rejected; clean outbox translation stays clean.
  Static IL analysis does not claim arbitrary reflection/dynamic-code coverage;
  new provider ports need explicit capability controls.
- `OutboxProducerConventionTests` complements real transaction proofs with
  transitive no-new-connection, no-commit and no-publication controls. The
  topic-convention census now requires production events and rejects trailing
  newlines. Eight producer catalogue rules move to Implemented; delivery/inbox,
  dispatcher, jobs and auth rules keep their named later packets.
- Current carriers and event/architecture/review skills distinguish delivered
  production from remaining dispatcher/inbox work. ADR-0057's dated Amendment 1
  links delivery without rewriting its Accepted body or prior decision records.

**Verification checkpoint.** The Release build has zero warnings/errors and the
complete solution's format verification passes. All 1,764 unit, 261 architecture,
four contract and 1,148 integration cases pass: 3,177 total, zero failures/skips.
TRX counters were inspected per assembly; the 59 focused producer-guard cases
overlap the architecture total. The first full integration run had five failures
in two older test setups that called persistence registration without the
application pipeline, leaving the new collector dependency absent. Both fixtures
now install the shared application registration; the complete integration rerun
above passes. No runtime policy was weakened. Review fixes below have their own
verification and supersede affected counts at this checkpoint.


#### Step 4 independent review round 1

Round 1 reviewed `3473843..b6e3932` with separate GPT-6-astra xhigh
runtime/security and GPT-6.1-sol xhigh proof/corpus reviewers. The runtime review
found one Major structural backstop gap: both guard walks expanded interface
implementations but missed overrides reached through abstract or non-sealed
base-class helpers. No production handler currently uses the escaping path.
Both walks now include derived implementations and have separate dirty/clean
abstract and virtual dispatch controls. Reverting the walk to interface-only
makes two domain-handler controls and one outbox control fail; restored source passes
all 63 focused producer-guard cases and all 265 architecture cases, zero
failures/skips. Release build has zero warnings/errors and changed C# formatting
passes. These four additional architecture cases bring the complete backend
verification to 3,181 cases; unchanged unit/contract/integration evidence is the
full run above, not an additional rerun claim.

The proof reviewer independently passed 261 architecture and 41 focused unit
cases at the implementation checkpoint. Its one Minor carrier finding is fixed:
the P02b-2 error/lease migration must account for existing P02b-1 producer rows;
its decision pass still owns descriptor handling. Both findings were verified
against source. The fixes were committed in `98c22a8`; the runtime reviewer
independently reran its exact twelve dirty/clean probes and confirmed both
formerly escaping paths are caught while clean paths remain legal.

#### Step 4 independent review round 2

Two fresh reviewers (GPT-6-astra xhigh for runtime/security; GPT-6.1-sol xhigh
for proof/corpus) reviewed `3473843..98c22a8` and the adjacent producer/audit
lifecycle. Both approve with no additional runtime or guard finding. The runtime
reviewer independently passed all 63 focused architecture cases; the proof
reviewer independently passed all 265 architecture and 59 focused messaging
unit cases, zero failures/skips. These cases overlap the complete run above.
The proof reviewer's only Minor confirms the final carrier sweep: Architecture
15's stale conditional partition-proof wording now describes the delivered
persisted-value proof.

### P02b-1 packet closeout (2026-10-12)

**Complete on `development`; PR review and merge remain pending.** All four
implementation steps completed their commits, both independent review rounds
and verified fixes. Accepted ADR-0057 governs the delivered scope:

- Shared producer/audit execution and immutable successful transaction
  announcements, preserving ordinary event-free refusals while poisoning
  failures after direct or descendant successful production.
- Immediate parameterized outbox INSERT on the existing announced writable
  transaction, exact envelope/routing/identifier payload admission and no new
  connection, commit or transport publication.
- Successful-save domain collection, bounded owner-only FIFO completion before
  MUST audit/commit and one completed-provisioning publisher in API and Seeder.
- Nonempty production censuses with independent dirty/clean and mutation
  controls; eight producer catalogue rules are Implemented. Current architecture,
  standards, module specs, glossary and skills reflect the delivered boundary.

**Local verification:** Release build has zero warnings/errors; complete solution
format verification and changed-code formatting pass. Per-assembly TRX counters
record 1,764 unit, 265 architecture, four contract and 1,148 integration cases:
**3,181 passed, zero failed/skipped**. Integration includes actual PostgreSQL
`learnstack_app` isolation/atomicity and the existing seed/public-renderer
regressions. Unit/contract/integration ran at the Step 4 implementation
checkpoint; the subsequent proof-only class-dispatch fix added four architecture
cases and reran that complete suite. Focused repeats and deliberately failing
mutation experiments are not added to the passing total. The five corpus checks
and seven ADR lifecycle tests pass. A local sweep of 33 changed Markdown files
checks 2,900 relative links and 700 fragments with no broken target; this is
manual/local validation, not a new CI anchor gate. Added closeout prose satisfies
the 88-column rule and `git diff --check` passes.

No frontend production source, OpenAPI contract or schema migration changes. The PR's
seven required checks provide fresh CI evidence, including frontend and the
public demo/Lighthouse lifecycle; local success alone does not claim CI approval
or merge. Existing Accepted ADR changes are insertion-only dated disclosures;
ADR-0057's original acceptance content and P02b-0's entry record are preserved.

#### PR #29 CI verification

The first full [PR run](https://github.com/HodeTech/LearnStack/actions/runs/38228839007)
at `1e799a8` passes six of seven required jobs, including the public demo and
Lighthouse lifecycle. Docker integration reports 967 passed and one failed:
the existing stock-browser admission proof exits during its `browser 429`
phase. All other integration cases pass. The same focused proof passes locally;
the original CI diagnostic does not identify the failing browser operation.

Test-only browser diagnostics now expose closed phase names for startup,
hydration, navigation, wire assertions and cleanup. Raw exceptions, browser
output, URLs and private values remain excluded. A verified review follow-up
also preserves the first proof failure if shutdown fails, while always running
cleanup. Assertions, deadlines and production behavior are unchanged.

At `11871a2`, the [diagnostic run](https://github.com/HodeTech/LearnStack/actions/runs/38231443960)
passes all 968 Docker integration cases. Its non-Docker artifacts record 1,764
unit, 265 architecture, four contract and 180 integration cases: 3,181 backend
cases pass with zero failures/skips across both jobs. The original stock-browser
failure did not recur; its cause remains unestablished, not claimed fixed.

Both diagnostics and first-failure preservation completed two fresh independent
review rounds. Focused browser checks pass 18 cases; final guarded frontend tests
pass 1,542 cases across two packages with zero skips/todos. Lint/typecheck and
the real admission integration proof pass locally. Verified CodeRabbit findings
also reconcile delivered producer work, pending inbox delivery and process-local
versus durable ordering in architecture/glossary/XML guidance. Five corpus
checks and the changed-document link/anchor sweep pass after those corrections.
The [PR's required checks](https://github.com/HodeTech/LearnStack/pull/29/checks)
own the final-head CI verdict; passing local or intermediate evidence does not
claim approval or merge.

**Next: P02b-2 decision pass.** Reverify G1, G3 ordering, G4, G5 resolver policy,
G7/G8, G17 and G18 error descriptors before implementation. ADR-0046 remains
reserved and undrafted; any new decision package/ADR needs maintainer approval.
That packet owns lease/grant migration, existing-row handling, dispatcher
credential/boot guard, claim/publish/mark, retry/terminal policy and metrics/health.
P02b-3 subsequently owns inbox/subscriptions, both Customization defaults and
the Seeder identity handoff after G1. Pending outbox rows do not claim delivered
consumption; no producer work is silently transferred to those packets.

#### PR #29 external-review proof follow-up (2026-10-12)

The review of `d37f46c..cb2bb6d` found three Minor proof gaps and no
Blocker/Major runtime finding. Commit `d8fd919` strengthens the existing cases:

- The shared execution test enqueues through the registered `IOutbox` at
  descendant depths 0, 1 and 2. It observes both business and outbox rows inside
  the transaction, then verifies both disappear after the absorbed refusal.
- The committed-MUST reconciliation test requires one declared intent inside
  the handler before checking that reconciliation writes no duplicate.
- The nested transaction test observes zero joiner writes and one owner write
  in the same execution, including the actual commit-state transition.

Three temporary mutations independently remove the real outbox receipt, audit
declaration and owner audit write. Their respective three, one and one target
cases fail. Production files are restored byte-for-byte, then all 62 audit/
transaction unit cases and 71 producer integration cases pass again.

Release build has zero warnings/errors; changed C# formatting and
`git diff --check` pass. The complete 1,764 unit and 265 architecture cases and
the 71 focused PostgreSQL cases pass with zero failures/skips; focused repeats
overlap these counts. This follow-up changes no production code or case count.

The public-receipt suggestion needs no visibility change here: `PostgresOutbox`
is a real cross-assembly caller; invocation receipts also serve existing
integration proofs. A temporary probe of compiled `BeginInvocation` confirms
that a hierarchical ambient Activity receives a valid W3C child and is restored
on disposal. The suggested non-W3C fallback failure does not reproduce.

Two fresh independent review rounds cover `cb2bb6d..d8fd919` and this follow-up
record. Each pairs GPT-6-astra high runtime/fixture review with GPT-6.1-sol high
proof/corpus review; all four reviewers approve without verified findings.
Five corpus checks, seven ADR lifecycle tests and the changed-document
link/anchor sweep pass. The PR checks own fresh final-head CI evidence.

#### PR #29 systematic-review remediation (2026-10-12)

The review against `75fc79b` identifies a valid pre-COMMIT audit classification
fault. Commit `c7dae0c` distinguishes a still-live transaction from a physical
COMMIT whose acknowledgement failed. A live frame refusal now enters the guarded,
uncancelled rollback path; the original error survives cleanup failure. Actual
COMMIT faults retain the existing Indeterminate behavior.

The PostgreSQL regression first fails against the old code: a leaked frame
produces a permanent `indeterminate` audit row despite never attempting COMMIT.
After the fix, owner and absorbed-joiner cases observe actual business/outbox
INSERTs, then prove their rollback, a single `failed` audit row, cleared authority
and refused same-scope reuse. Scope disposal already collapsed leaked frames in
the old code; the review's claim that they stayed live after unwinding is not
reproduced. Two unit cases also preserve the frame error when rollback succeeds
or throws. API and Seeder tests document and enforce producer enrollment before
event-bearing saves, with successful event-free raw-save controls.

Other verified changes reconcile the current uncached-settings handoff without
promising a settings event, correct ADR-0032 clause locators through ADR-0041
errata, refresh amendment/index/example links and name concrete proof methods.
The parallelism census includes shared Application, Infrastructure and the
separate audit infrastructure project, with planted controls and exact-path
exceptions for singleton cache and pre-admission host lookup flights. Test-only
browser diagnostics distinguish launch,
DevTools readiness and private-carrier failures; cleanup preserves the first
failure and exposes only closed phase tokens.

The remaining runtime suggestions do not demonstrate a current failing path:
domain dispatch validates enrollment before owner access; outbox ancestry and
JSON shape are guarded before serialization checks; the Seeder test positively
observes promotion before its deliberate refusal; reflection and log-trigger
changes fail their existing controls loudly. Provider-port recognition remains
an explicitly enumerated limitation, not a future-port discovery claim. No
runtime visibility/marker, serializer, scheduling or ConfigureAwait refactor is
needed. The final announcement check remains defense in depth after drain.
The earlier temporary mutations are recorded manual experiments; their target
regression cases are permanent, without a new automated mutation-runner claim.

Focused verification passes 75 producer integration and 64 audit/transaction
unit cases, including overlap with the complete suites. Full regression and two
fresh independent review rounds remain in progress for this remediation.
PR review and merge remain pending.

### Durable outbox dispatch

P02b-1 Steps 2/3 implement `IOutbox`, replace Packet 3's step-7 shell with
an enrollment/invariant guard, and supply owner-side domain collection and the
first publisher. Dispatch and consumer delivery remain P02b-2/P02b-3.
The table shipped in Packet 6.

A handler calls `IOutbox.EnqueueAsync` and the row commits on the same **transaction**
as the aggregate change — the ambient one `IUnitOfWork` owns
([ADR-0040](../decisions/0040-ambient-unit-of-work.md)), not a shared `SaveChanges`, a
formulation [ADR-0033](../decisions/0033-audit-durability-model.md) withdrew. [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md)
accepts immediate INSERT through that connection/transaction. Step 7 is an
enrollment/invariant guard; the transaction owner drains domain events before
MUST audit and commit. Raw transactions without an enrolled producer frame
cannot enqueue. Production delivery wiring remains P02b-3.

#### Process topology

`OutboxProcessor` is a `BackgroundService` — the shape
[Events and Outbox § OutboxProcessor](../architecture/15-event-and-outbox.md#outboxprocessor-backgroundservice),
ADR-0006 Amendment 1, ADR-0010 and the glossary all specify, with a 200 ms poll no job
schedule expresses. This phase hosts it in `LearnStack.Api`, the only web host.
`LearnStack.Tools.Seeder` is a separate one-shot executable, not a worker host.
Nothing in `backend/src` implements `IHostedService` today. The corpus's gateway
route table puts `/admin/hangfire*` on that
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

**Implemented by P02b-1 Step 3 — ADR-0057.** The shared SaveChanges
interceptor stages source events and promotes them only after successful
persistence, in both API and Seeder compositions. Failed/canceled saves promote
nothing. Collection retains source/frame attribution and transaction generation;
repeated saves, detach and delete cannot lose or duplicate a successful event.

Only the transaction owner drains the collected FIFO queue, at one dispatch site,
before producer invariant checks, MUST audit composition/write and commit. At most
256 domain events belong to one transaction; exceeding this total fails the whole
unit. This is a total count, not recursive cascade support.

Domain-event handlers translate facts into outbox messages only. They do not
mutate roots, use write stores, send nested requests, raise further events,
publish notifications or invoke external providers. Unexpected enqueue/contract
faults throw and roll back. Expected business decisions stay in commands or later
integration consumers, preserving the returned version and one-root boundary.

Each producer invocation keeps a sticky receipt after a successful outbox INSERT
or successful-save promotion, also recorded in every ancestor. A `Result.Fail`
from any frame with that work poisons the shared unit before its UoW frame is
resolved, even when an ancestor absorbs the refusal. Ordinary event-free refusals
retain ADR-0040's semantics. Joiners neither dispatch nor clear the owner's queue;
every terminal path clears transaction-scoped source/collector state exactly once.

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
- Label sets are settled by **G17** before the first series ships, in the decision pass
  of `P02b-2`: a label added afterwards touches every emitter, and a series shipped with
  a guessed label cannot be renamed once a dashboard and an alert read it.

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
  current dev export has no organization claim. **G11** decides whether this phase
  mints one or deliberately refuses the authenticated organization-host case;
  "across both organizations" is not yet an accepted promise.
- **Keycloak realm roles.** The realm ships three `tenant-*` roles with full scope on
  both clients, so they appear in every token, while roles live in LearnStack and not in
  Keycloak. Either they leave the realm or they are declared inert with a rule proving
  no LearnStack assembly reads a role claim. A documentation link is not a mitigation
  for a leak the realm actively causes.

#### Correction: the realm emits no `tenant_id` claim at all

This is an unimplemented prerequisite of this phase's authenticated cross-check.
`infra/keycloak/realms/learnstack.json` stores `tenant_id` as a **user attribute** and
declares **zero protocol mappers**. Keycloak does not put user attributes into tokens
unless a mapper says so. The API also has no JwtBearer registration or
`UseAuthentication` today: it does not validate those tokens or populate the
resolver's reserved claim signal. Current public requests use the delivered
host-only admission, query filters and Row Level Security. Authenticated behavior
must not go live until G10–G12 settle the token and resolution contract.

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
  request and one logical replacement session version, including any cookie chunks;
  a terminal `invalid_grant` clears the session and the
  visitor gets the public page anonymously rather than an error.
- **Cookie scoping and CSRF.** Host-only cookies with no `Domain`, so two tenant hosts
  do not share a session, plus an `Origin` check and a session-bound token on every
  mutating route. `SameSite=Lax` does not separate hosts under one registrable domain,
  which is exactly the SaaS subdomain shape, and no CSRF rule exists in the catalogue
  today.
- **Login topology**, under **G12**: login is served on the visitor's tenant host,
  requiring one registered redirect URI per seed host. Phase 02d retains
  `demo-english.learnstack.local` and `demo-yoga.learnstack.local` under the native
  HTTPS launcher; hosts entries and certificate trust remain manual prerequisites.
  `P02b-0` re-verifies [G32](phase-02d-walking-skeleton.md#the-decision-register)
  and the ADR-0053/0054/0055 ingress, visitor/peer and single-bootstrap boundaries.
  G12 still settles the BFF client, callback and post-logout URLs; it preserves
  `Secure`. The login transaction's `state`, `nonce` and `code_verifier` live in a
  host-only transaction cookie, and return URLs are validated.
- **Logout**, which
  [Identity and Auth § Logout](../architecture/13-identity-and-auth.md#logout) describes
  and Phase 03 expects: the session cleared, the end-session endpoint called, and
  back-navigation not restoring an authenticated view.
- **Something that consumes the session.** One authenticated `/api/v1/*` read and one
  guarded page, both in the OpenAPI document and the regenerated SDK. Without them "a
  login completes" cannot be demonstrated, and
  `Backend_RequiresJwt_OnAllAuthenticatedRoutes` sweeps an empty route set.
- **Auth.js and `auth()` placed in `apps/web`**, the `(studio)` / `(portal)` redirect,
  the server SDK's bearer attachment, and server-only environment keys. The
  `add-frontend-route` and `add-feature-gated-ui` skills contain future auth
  illustrations rather than shipped helpers; P02b-7 replaces them with the real
  session API when it lands.

### Useful provisioning flow

[ADR-0057](../decisions/0057-transactional-event-production-and-completion.md#first-useful-production-flow)
accepts completed provisioning as the producer: `TenantCreatedIntegrationEventV1`
on `learnstack.tenancy.tenant`, partitioned by `TenantId`, with no extra payload
fields and null envelope organization. P02b-1 emits it only after tenant/default
organization/back-reference completion and persists pending rows.

After G1, P02b-3 supplies two independent Customization subscriptions for `card@1`
and `plain@1`. Each create-only effect owns one root, generation update and MUST
audit on its inbox transaction, preserves authored/lifecycle/deletion decisions,
and resolves incomplete pairs through retry. P02b-3 owns semantic key/version
Seeder verification, preserved warm IDs and fresh seed/delivery convergence.
Typed settings stay uncached; definition generations are freshly probed. This
phase promises no eager settings invalidation or asynchronous Audit mirror.

### Audit coverage wiring

The audit pipeline lit up in
[Phase 02a Packet 9](phase-02a-kernel-tenancy.md#delivery-record-packet-9) under
[ADR-0033](../decisions/0033-audit-durability-model.md). This phase adds the entry
points it does not reach, and both of Packet 9's hand-offs land here.

- **The delivery path has no transaction or audit frame today.**
  [ADR-0040](../decisions/0040-ambient-unit-of-work.md#consumers-do-not-have-a-request-and-still-need-all-of-this)
  assigns `Begin → SetTenantContext → handler → Commit` to this phase; the current
  transport restores only the ambient accessor and resolves a fresh handler scope.
  Adding that transaction alone would leave an audit gap:
  only `AuditLogBehavior` declares intents, only `TransactionBehavior` flushes them, and
  the catalogue is keyed by request type — so a consumer that writes its own aggregate
  through its `DbContext`, which is the shape the corpus teaches as canonical, has its
  changes would be captured into a buffer that holds no intent naming its type.
  **No row would be written and nothing would fail.** That is the window
  [ADR-0033](../decisions/0033-audit-durability-model.md) says must not exist.
  **G6's common-owner contract is Accepted under ADR-0057**; P02b-3 implements
  it in production delivery after G1. Its proof is a MUST-classified direct-write
  consumer,
  not only a consumer that sends a command.
- **The audit frame's owner on a non-pipeline entry point.** What is *already* decided
  is not reopened here:
  [ADR-0033 Amendment 2 § 2](../decisions/0033-audit-durability-model.md) and
  [ADR-0044 § 4](../decisions/0044-audit-write-path.md) both say only the owning frame
  writes, signals the boundary, reconciles and clears, and a joiner does none of it.
  ADR-0057 accepts common entry coordination for **G6**. The remaining P02b-3
  production wiring must repair this baseline: `AuditLogBehavior` takes its owner
  flag from an audit-frame counter rather than from unit-of-work ownership, and
  [Audit Subsystem](../architecture/31-audit-subsystem.md) records that split as
  deliberate for the HTTP path — at step 3 no transaction is open to answer the
  question. The two coincide today only because the pipeline is the only thing that
  opens a transaction with a command inside it. A transport-opened transaction breaks
  the coincidence: the inner command becomes a unit-of-work joiner while still being the
  outermost audit frame, so it reconciles before the delivery commits, writes its MUST
  row standalone as `failed` for a change that then commits, and clears the buffer the
  owner would have flushed. ADR-0044/0033 now clarify the common owner; P02b-1
  proves it through a
  controlled direct fixture before production transport wiring in P02b-3.
- **The discriminating case.** The pair this phase used to name — a consumer's
  MUST-class command recording `success` on the delivery's transaction, and a
  rolled-back delivery recording it standalone as `failed` — passes under **both**
  ownership designs. ADR-0057's cases distinguish an ordinary event-free refusal
  absorbed by a committing delivery from a produced-work refusal that poisons it,
  a faulted `COMMIT` producing exactly one
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
  of the Accepted **G6** producer/common-owner contract; production delivery
  supplies the trusted incoming event in P02b-3.
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

`LS0002` is planned, not shipped; its initial Warning severity does not itself
make a build fail. A roadmap document is not its diagnostic registry.
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
length bound in the canonical DDL. P02b-2 owns that migration beside the lease
columns and must account for existing P02b-1 producer rows; its decision pass
settles descriptor handling before applying the bound.

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

- **Producer rows delivered by P02b-1:** the announced-transaction write,
  produced-work refusal, owner-only completion, outbox-only domain handlers and
  identifier-only payloads. Later decision passes register and implement the
  ADR-0036 middleware-ordering rule, the
  routable-set bound (**G14**) and the Keycloak-role-claim rule — with the realm-role
  question it answers — in `P02b-5`, where the API first reads a claim; and `LS0002`
  under its rule name (**G16**) in `P02b-8`.
- **Widened assertions** where a catalogued rule is satisfied by the defect it exists to
  catch. P02b-1 extends `Outbox_Row_Carries_Correlation_Context` to exact stored
  identity, tenant/organization, full traceparent and partition values, including
  unresolved provisioning. Its partition declaration rule combines independent
  stored-source IL checks, varied identifier probes and real persisted keys;
  an uninitialized instance is not a value proof. Publish ordering stays P02b-2.
  `Outbox_Claim_IsHeld_Until_Dispatch_Completes` says "exactly once" unconditionally,
  which contradicts the at-least-once contract; it narrows to live-lease contention and
  gains a redelivery companion for crash and expiry.
  `Hangfire_Job_Payloads_Include_TenantId` gains the organization, actor and traceparent
  fields. `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` gains the hanging-handler
  case. Two rules the catalogue stamps **02a** also
  widen here, so the Phase-field sweep above does not reach them: the transport arm of
  `Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction`, whose second arm has no
  subject until the transport announces a tenant, and
  `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork`'s connection allow-list, which
  the dispatcher's keyed data source and the job storage both touch — a new entry there
  is a decision, taken in the pass of the packet that adds it: the dispatcher's in
  `P02b-2`, the job storage's under **G9**.
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
  no subject, so `P02b-6`'s decision pass, where **G12** settles the Keycloak
  reconciler, either makes that reconciler the first provider adapter and gives the rule
  its first subject here, or moves its owning-phase cell to the phase that lands one.

Two notes on what "green in CI" can mean. Several of this phase's rows are behavioural
rather than structural, and CI runs those in a separate job under a Docker trait filter,
so "the architecture suite is green" does not reach them. `backend integration
(Testcontainers)` is already a required check; the
[P02b-0 receipt](#p02b-0-entry-verification-2026-10-12) verifies all seven current
required contexts with strict up-to-date protection. `P02b-8` re-verifies those
live settings under the exact reported names. `scripts/assert-tests-ran.py`
already rejects a missing assembly, zero executed tests and any unexecuted test
in the required assemblies; test failures additionally fail the `dotnet test`
command. The phase retains zero skips, per assembly.

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
  table class, its policy taken by link and its grants; plus the erratum ADR-0023's
  DB-side minting list owes. This phase states the **contract** — how long a
  `(event, consumer)` pair stays replayable — and
  [Phase 11](phase-11-production-hardening.md) picks the purge interval that honours it.
- `IOutbox` and the `OutboxFlushBehavior` invariant guard, delivered by P02b-1
  under Accepted ADR-0057; immediate INSERT uses the announced transaction.
- `TenantCreatedIntegrationEventV1` from completed provisioning; after G1, two
  independent Customization subscriptions install untouched `card@1`/`plain@1`
  defaults with one-root effects and the P02b-3 Seeder identity handoff.
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
  frame's transaction, ordered before producer invariant checks and MUST audit.
  Domain reactions translate to immediate outbox INSERTs only.
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
  token-keyed rate-limit partitions going live with authentication — including the
  change **G14** settles to the existing pre-auth floor: 60/min per canonical
  visitor and 600/min peer acquisitions/allocations under ADR-0053/0054. These
  anonymous visitor/peer quotas remain distinct from the token-keyed budget.
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
  skills' illustrative `auth` references replaced with the shipped helper.
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
  phase's outbox dispatch as its obligation, since a polling dispatcher needs no such
  seam; and
  [Security Standards § The out-of-band setters](../standards/11-security.md#the-out-of-band-setters),
  whose transport row and reproduced count both change when the transport opens a
  transaction.

## Completion Criteria

Each criterion below is observable: a named test can pass or fail on it. Criteria whose
shape depends on an open gate say so, and are written when that gate is Accepted.

**Producer and dispatcher**

- An aggregate change and its outbox row commit together and roll back together,
  asserted as `learnstack_app`. An enqueue whose event `TenantId` differs from the
  successfully announced transaction tenant is refused, including unresolved
  ambient provisioning. Stale-generation, read-only and unenrolled producers
  fail before INSERT.
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
- A producer dead-letter writes exactly one MUST-class row under the slug **G7** names,
  and a forced failure of that write produces the behaviour G7 chose — the transition
  unmade and retried, or the duplicate that ordering accepts. The subscriber side
  already has both halves; the producer side needs them more, because its transition and
  its audit row are made by two roles.
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
- An ordinary event-free inner refusal can be absorbed while the delivery commits;
  a refusal after own/descendant produced work poisons the shared transaction.
  A faulted delivery `COMMIT` produces exactly one `indeterminate` row, and the
  outermost audit frame clears capture exactly once per delivery.
- A successfully saved domain event reaches its in-module translation handler on
  owner completion before MUST audit. Its exact outbox row commits with source
  state/audit; P02b-2/P02b-3 subsequently prove actual dispatch and consumption.
  Repeated saves, detach/delete and failed/canceled saves prove attribution.
- A translation fault or a 257th domain event leaves source state, audit and every
  outbox row uncommitted. Planted handlers using writes, nested sends, notification
  publication, raises or providers fail the complete module/helper/interface sweep.
  Rollback, refusal, cancellation, fault and same-scope reuse cannot replay sources.
- A handler that ignores its cancellation token does not stall the rest: the
  per-delivery deadline elapses, other keys keep dispatching, and the abandoned delivery
  commits nothing afterwards.
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
- Authenticated and id-addressed routes have tenant and organization isolation
  cases as `learnstack_app`: an identifier from another tenant or an invisible
  sibling organization reveals no resource, and each route has a visible-resource
  positive control. Host/JWT cross-check cases remain independently asserted.
- Completed provisioning yields one pending `TenantCreatedIntegrationEventV1`
  and zero on rollback. P02b-3 proves independent create-only `card@1`/`plain@1`
  delivery, sibling recovery, preserved authored/deleted definitions, semantic
  built-in identity verification and concurrent fresh seed/delivery convergence.
- Two principals behind one BFF do not share an authenticated quota: through the
  **whole** middleware chain, from one peer address, one exhausts that budget and
  the other's next request is not 429. G14 settles the delivered pre-auth visitor/
  peer floor's treatment of validated callers; anonymous unknown-host abuse stays
  bounded. The 429 carries the one Problem Details shape and `Retry-After`, and
  repeated mismatches from one token reach 429 and stop producing audit rows.
- A second `make seed` against a Keycloak database created from the pre-mapper realm
  JSON leaves every existing account and the `learnstack-hub` realm intact, applies the
  client scope and the mappers, and yields a token carrying a UUID-valued `tenant_id`. A
  third run changes nothing.
- `exp - iat` on a dev-realm token equals the lifetime the realm export declares, so
  raising one without the other fails.
- The callback route rejects a response whose `state` does not match the transaction
  cookie and one with no `code_verifier`; the session cookie is `HttpOnly`, `Secure`,
  `SameSite=Lax`, `Path=/` with no `Domain`; and no response body or script-readable
  cookie carries the refresh token. Asserting the header is not enough: on each real
  seed host a browser-level case shows the cookie **stored and sent back** on the next
  request through the retained native HTTPS launcher and manual hosts/trust setup
  that `P02b-0` re-verifies.
- Two concurrent requests with a near-expiry token produce exactly one token request at
  the issuer and one logical session version — counted as versions, not as `Set-Cookie`
  headers, because a session adapter may split one cookie across several and a correct
  single update can then emit more than one. Whatever the split, the chunks grow, shrink
  and are all cleared on logout. A terminal `invalid_grant` clears the session and
  serves the public page anonymously with a 200; logout ends the session at Keycloak and
  back-navigation does not restore an authenticated view.
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
  lesson Packet 9 recorded for its own readings. The mitigation is the decision pass
  each packet opens with: its decisions are its first commit, and its code follows them.
- **A gate relies on a premise that an earlier packet has moved.**
  [Phase 02d](phase-02d-walking-skeleton.md) delivered public GET/HEAD endpoints,
  retained native HTTPS tenant hosts and coordinated canonical visitor/physical-peer
  admission. **G14**, **G20** and **G12** must use those delivered contracts, including
  single-bootstrap rendering and exact peer accounting, rather than the former
  socket-peer-only assumption. Earlier packets move later premises the same way:
  the dispatcher's keyed data source can change the connection allow-list before
  **G9** decides the job storage's entry in it. The mitigation is
  [Roadmap § Decision Timing](README.md#decision-timing): `P02b-0` re-verifies the
  register once Phase 02d exits, and each gate closes in the decision pass of the first
  packet it shapes.
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
