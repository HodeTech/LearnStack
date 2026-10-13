# Phase 02b: Events, Background Jobs, Identity, and Session

**Current status — 2026-10-13.** P02b-0 through P02b-2 are complete.
P02b-2 is merged through [PR #30](https://github.com/HodeTech/LearnStack/pull/30);
its [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns final verification.
P02b-3's [decision package](#p02b-3-decision-package-2026-10-13) is Accepted;
its six-step implementation is in progress. P02b-4–8 remain unimplemented.
Phase 02b remains in progress. Earlier dated receipts retain their as-of state.

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
backend verification and both completed review rounds. **P02b-1 is complete and
merged** through [PR #29](https://github.com/HodeTech/LearnStack/pull/29) on
2026-10-12. The [merge closeout](#p02b-1-merge-and-closeout-2026-10-12) owns final
verification. The P02b-2 merge closeout below owns final dispatcher verification.

**P02b-2 decision pass — 2026-10-12.** The
[decision package](#p02b-2-decision-package-2026-10-12) and
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md) are Accepted.
The four implementation steps are authorized on development. Step 1 is complete;
its [record](#p02b-2-step-1--decision-schema-and-confined-composition-2026-10-12) owns
delivery evidence and both completed review rounds. Step 2 implements durable
transitions and terminal audit; both independent review rounds pass. Step 3
implements hosted lifecycle and observation; its
[record](#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12) owns
verification and both completed review rounds. Step 4 enforcement and local
closeout are complete; all four steps completed both independent review rounds.
The [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns final verification.
P02b-3 implementation remains pending.

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
- **The claim and delivery protocol.** Accepted
  [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md) supersedes the
  batch-held fallback and unbounded ordering/exactly-once sketches. P02b-2 uses
  fresh acquisition tokens, short claim transactions, six normal claims and
  explicit terminal state. Dispatch stays at least once; P02b-3's shared inbox
  provides per-subscription database idempotency.
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
enqueue and the enrollment/invariant guard. P02b-2 Step 3 implements the
API-host `OutboxProcessor` BackgroundService, with both independent review
rounds complete. Still absent are `IInboxGuard`, `inbox_messages`, Hangfire and its
package pins, and the realm's protocol mappers. Packet 5 documented a planned
`IInboxGuard` seam in comments, without an interface declaration. P02b-3 owns its
shared transport implementation and table under ADR-0046.

### What this phase is not

**It is not the phase where a transport is chosen.** `IEventBus` and `InProcessEventBus`
shipped in [Phase 02a Packet 5](phase-02a-kernel-tenancy.md) as a **first-class
transport** with the shared `IIntegrationEventHandler<T>` contract and
tenant-context restoration. P02b-3 adds shared inbox durability and independent
subscription redelivery on top of that transport; it does not add a second one.

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

- **An operator-facing recovery API.** P02b-3 supplies internal targeted
  subscriber replay through its delivery ledger and executor. Producer reset,
  reason/permission, operator fencing and the runbook remain
  [Phase 03](phase-03-identity-admin.md) work. No operator outbox reset is settled
  here. `EnterPlatformAdminScope` still denies everyone until Phase 03; the URL
  space needs the record
  [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md#the-platform-admin-override-is-not-a-resolution-source)
  requires.
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
- [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md) is Accepted
  with its bounded delivery supersessions and separate additive audit seam.
  ADR-0047 (job runtime) remains undrafted in
  [Open ADR Drafts](../decisions/README.md#open-adr-drafts).

## Scope

### Packets and decision gates

Phase 02a needed twelve packets for a narrower scope. This phase declares nine, in
dependency order. `P02b-0` writes no code. Every packet after it opens with the decision
pass [Roadmap § Decision Timing](README.md#decision-timing) describes: its gates are
Accepted, and its catalogue rows registered, before its first line of code.

| Packet | Contents | Cannot start until |
|---|---|---|
| **P02b-0** | Phase entry: every premise the register below cites re-verified against `HEAD` — the code, the corpus, the Hub repository — and the register corrected where Phase 02d moved it. It drafts no record: each gate closes in the decision pass of the first packet it shapes, and each glossary headword this phase needs — `inbox`, outbox lease, dead letter, audit frame, job activator, integration-event consumer — is written in the pass whose gate fixes its meaning | [Phase 02d](phase-02d-walking-skeleton.md) exits |
| **P02b-1** | **Complete and merged — [PR #29 closeout](#p02b-1-merge-and-closeout-2026-10-12).** Producer: `IOutbox`, the `OutboxFlushBehavior` invariant guard, successful-save domain-event collection and owner-only completion, and the first event with its enqueue path | G2, G3 (the key's derivation), G5 (the stored identities), G6, G18 (the payload rule), G19 Accepted |
| **P02b-2** | **Complete and merged — [PR #30 closeout](#p02b-2-merge-and-closeout-2026-10-13).** Dispatcher: the lease migration and grant extension, claim / publish / mark, backoff, the producer terminal state, the credential and its boot guard, the metrics and the health check | G1, G3, G4, G5, G7, G8, G17, G18 (`last_error`) Accepted; P02b-1 |
| **P02b-3** | [Decision package Accepted](#p02b-3-decision-package-2026-10-13); implementation in progress. Consumer: durable acceptance, shared inbox/ledger DDL and `IInboxGuard`, the common per-delivery transaction, both real defaults subscriptions and Seeder handoff, independent attempts and audited terminal state, targeted internal ledger replay | G1, G6, G7 Accepted; P02b-2 |
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
| G1 | What is the subscriber's fate when its handler exhausts retries, and what is the inbox table — one shared or per module, keyed `event_id` or `(tenant_id, consumer, event_id)`, claimed by the handler or by the transport? Three arms travel with it: the class of consumer whose effect cannot share the inbox transaction (an external call, the platform-scope erasure handler the Audit spec already carries) and what "effectively once" means for it; whether an event without the organization marker is delivered tenant-wide regardless of the producing request's organization; and whether the inbox entity joins [ADR-0044 § 7](../decisions/0044-audit-write-path.md)'s named audit-capture exclusion list once a delivery declares intents | One shared tenant-owned `inbox_messages` keyed by tenant/stable subscription/event, transport-claimed before construction; a separate delivery ledger owns retries, terminal state and targeted replay; completion shares the business/audit/outbox transaction | **ADR-0046** + a dated **ADR-0006 Amendment** (or an explicit supersession note in ADR-0046 naming Amendment 1's consumer-side paragraph, which fixes the per-module inbox) + a dated **ADR-0010 Amendment**, because its § Idempotency makes two things mandatory that the shared-table leaning changes — the inbox is "the module's **own** inbox table", and the guard is called by the consumer — + the `Integration_Event_Handlers_Use_InboxGuard` assertion re-written in the same record to name whoever ends up owning the call, since a transport-claimed delivery fails a structural rule that looks for the call inside the handler + Database Standards § Inbox + an ADR-0041 erratum beside ADR-0023's minting list | P02b-2 (the record and its amendments: the dispatcher's retry state and a failed delivery's next attempt are one state machine), P02b-3 (Database Standards § Inbox and the ADR-0023 erratum) | Accepted — 2026-10-12; decision/amendments delivered; subscriber implementation pending P02b-3 |
| G2 | Does `IOutbox.EnqueueAsync` write the row immediately on the ambient transaction, or buffer for `OutboxFlushBehavior` at step 7 — and what is the flush point for the two paths that have no step 7? | Immediate write, with the behavior asserting no buffered message survives the handler | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 2's step-7 completion role, with a dated supersession disclosure in ADR-0032 under this acceptance; phase-doc + [Events and Outbox § Producer pattern](../architecture/15-event-and-outbox.md#producer-pattern) | P02b-1 | Accepted — immediate enqueue/owner completion |
| G3 | What replaces the unconditional same-key publish-order promise — a scoped bound, or strict per-key head-of-line blocking — and is the key's derivation checkable structurally, given that [§ Ordering](../architecture/15-event-and-outbox.md#ordering) admits an aggregate id or a deliberately declared tenant-wide key and nothing else? | Scope it: no column in `outbox_messages` carries an unconditional per-key guarantee, so the honest bound is one processor, no failure, deterministic eligible selection; no enqueue/commit-order guarantee | Derivation: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md). Ordering: Accepted ADR-0046 and the P02b-2 package, followed by [§ Ordering](../architecture/15-event-and-outbox.md#ordering) alignment under this acceptance | P02b-1 (whether the key's derivation is checked structurally: the first event declares a key), P02b-2 (the ordering bound) | Derivation and ordering Accepted; scoped ordering runtime [implemented in P02b-2](#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12); complete and merged; [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state |
| G4 | What counts as an attempt, what is `MaxAttempts` bound to, and what marker keeps a terminal row terminal as the clock advances? | Six attempts (one immediate plus the five documented delays) and an explicit terminal column, because a pushed-out `available_after` stops being terminal and never leaves the pending gauge | Accepted ADR-0046 + dated ADR-0006 disclosure under this acceptance; Database Standards § Outbox DDL, index and grant edits | P02b-2 | Accepted — 2026-10-12; producer attempts/terminal state [implemented](#p02b-2-step-2--durable-transitions-and-terminal-audit-2026-10-12) |
| G5 | Does the stored event identity stay the assembly-qualified type name with declared compatibility mappings, and what happens to an unresolvable type, a retired type and an event with no subscriber? And what is the **row's** identity — a surrogate `id` plus an `event_id` column, or `EventId` as the key — given that two documents list an `event_id` the canonical DDL does not have? | Keep the type identity, add a bounded resolver reading through `PayloadJsonOptions` and declared mappings; dead-letter a retired type at once rather than spending its attempt budget; reconcile the row's column list in one direction | Stored identities: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 12 and the Phase 02b deliverables table in Implementation Notes, with a dated supersession disclosure under this acceptance; Database Standards alignment. ADR-0046 accepts declared resolution, Pending holds and intentional absence; dispatch runtime remains P02b-2 | P02b-1 (the stored type identity and the row's identity, both written at enqueue), P02b-2 (unresolvable, retired and unsubscribed types) | Stored identities and resolution outcomes Accepted; resolver/readiness runtime [implemented](#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12); Active durable subscriptions pending P02b-3 |
| G6 | When the unit-of-work owner is not an `AuditLogBehavior`, who performs the four owner-side audit acts and enters the outermost audit frame — and does a state-mutating delivery declare its own intents, keyed by handler type? | The transport opens the outermost frame beside its transaction; a delivery declares its own intents and an unregistered handler is refused as an unregistered request is | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md): bounded ADR-0040 nested-failure supersession; separately enumerated additive UoW read-seam amendment; clarifying ADR-0044/0033 common-owner amendments. G1-dependent production delivery/inbox implementation remains P02b-3 | P02b-1, P02b-3 | Producer/common owner delivered; delivery/inbox contract Accepted, implementation pending P02b-3 |
| G7 | For a dead-letter transition: what class, what `OperationType`, what slug, on which transaction, in what order against the state change, with what failure posture — and may a second non-MediatR caller reach `IAuditStore.WriteStandaloneAsync`? | MUST `security-event`; producer `platform.outbox.dead_letter` and reserved P3 `platform.delivery.dead_letter`; fenced terminal transition and audit INSERT on the same short transaction through a narrow fifth write seam; failed audit retries terminalization without publication | Accepted ADR-0046 + bounded ADR-0044 audit-grant replacement, separate additive fifth-writer seam amendment and ADR-0033 same-transaction clarification under this acceptance; `DeclareOffPath` registration and Audit Coverage baseline | P02b-2 (the producer dead-letter row its criteria assert), P02b-3 | Accepted — 2026-10-12; producer terminal audit [implemented](#p02b-2-step-2--durable-transitions-and-terminal-audit-2026-10-12); subscriber terminal audit pending P02b-3 |
| G8 | Does the dispatcher's `BYPASSRLS` use carry a per-invocation `security-event` row, or is it exempt — and on what written terms? | Exempt, on three terms: the bypass is grant-bounded and code-path-confined, dispatch attempts are logged rather than audited, and the dead-letter transitions are the audited events | Accepted ADR-0046 explicitly replaces the routine-dispatch coverage rule and bounds the ADR-0003 grant extension; dated disclosures under this acceptance, not an ADR-0041 correction | P02b-2 | Accepted — 2026-10-12; confined dispatcher exception/census [implemented](#p02b-2-step-4--enforcement-and-closeout-2026-10-12); complete and merged; [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state |
| G9 | Which record decides the background-job runtime — its storage under the closed four-role model, the single tenant-context writer, the job frame's transaction owner, the enqueue-site rule *and who may enqueue whose job type*, the retry contract (whose class, slug and posture adopt **G7**'s), the tenantless platform-job class a purge needs, the ADR-0035 classification, the queue grammar, and the package pins with their licence verdict? | One record answering all of it; a job reaches the database only through `ISender`, which leaves ADR-0040's closed setter set at eight | **ADR-0047** + a dated ADR-0003 Amendment for the storage + a dated **ADR-0036 Amendment** (or an erratum) if the job-path writer is not the `JobActivator` its § Rules names in the closed four-caller set, then Standards 05 and Standards 20 | P02b-4 | Open |
| G10 | Does the active tenant travel as a scalar `tenant_id` claim, a `memberships` array, or both — and what value does `UserId` hold? | The scalar claim, whose 02b source is an admin-set user attribute; a second UUID-valued `user_id` attribute for the actor, with `sub` staying Keycloak's subject | Phase-doc: [ADR-0036 § What is out of scope](../decisions/0036-tenant-resolution-trusted-inputs.md#what-is-out-of-scope-and-what-is-not) hands the shape to this phase by name. Reconcile `architecture/13`'s array sketch in the same change | P02b-5 | Open |
| G11 | Which [ADR-0036 matrix](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix) rows resolve in this phase, given that row 10 consults a reader that denies everyone — and what row covers a signature-valid token carrying no `tenant_id` claim on a tenant host? | Either mint `organization_id` scoped to the host's organization, which makes row 10 need no membership read, or declare the organization host's authenticated 404 and test it as the expected outcome | A dated **ADR-0036** amendment plus errata beside § Staging across packets and the 2026-09-02 erratum, both of which assign row 10 to this phase without noting that its `M covers (T, O)` term is denied until Phase 03 — which groups it with rows 7 and 14 rather than with 6 and 9 | P02b-5 | Open |
| G12 | Which Keycloak client does the BFF exchange the code with, where does sign-in happen across two tenant hosts, and how do the client scope, the mappers and the seed users reach a Keycloak database that has already consumed the import? Native HTTPS on retained `*.learnstack.local` hosts, with manual hosts/trust setup, is delivered by Phase 02d and re-verified by P02b-0 | A confidential BFF client with one redirect URI per seed host, retaining native HTTPS and `Secure` cookies; and an idempotent reconciler inside `make seed` — a mapper added only to the import reaches no existing workstation | Phase-doc + the realm JSON + `scripts/seed.sh` + `infra/keycloak/README.md`; a new ADR only if the reconciler becomes ADR-0004's `IIdentityProvider` | P02b-0 re-verifies [Phase 02d G32](phase-02d-walking-skeleton.md#the-decision-register), ADR-0053/0054 and ADR-0055; P02b-5 (which client the BFF redeems the code with: the API pins `azp` against it), P02b-6 | Open |
| G13 | What is the BFF session — its custody of the refresh token, its store, its idle and absolute lifetimes, its refresh serialisation under concurrent requests, its terminal `invalid_grant` behaviour, its CSRF control and its logout — and which document owns the access-token lifetime? | The `HttpOnly` cookie Security Standards already describes, encrypted by the session adapter, read only by server code, with single-flight refresh and one authority for the TTL | Phase-doc + one reconciled [Security Standards § Authentication](../standards/11-security.md#authentication); a **new ADR** only for a server-side session store | P02b-6 (which document owns the access-token lifetime: the realm export and its reconciler write it), P02b-7 | Open |
| G14 | What bounds the request types reachable over HTTP while `AuthorizationBehavior` is `return next()`, and what is the shape of the token-keyed rate-limit stage behind a shared BFF connection? | An enumerated read-only routable set held by a catalogue-registered rule, and a limiter stage after `UseAuthentication` keyed on the validated subject. The early anonymous stage already runs before authentication: P02d-5 delivers 60/min per canonical direct or authenticated-hop visitor IP plus 600/min peer acquisitions/allocations under ADR-0053 and ADR-0054. Exhausted known visitors refuse before peer debit; unknown visitor allocation remains peer-gated. These remain IP/peer quotas, not token quotas; a request they reject never reaches the token stage. Re-verify the [P5 remediation decision](phase-02d-walking-skeleton.md#p02d-5-external-review-remediation-2026-10-09) before choosing authenticated accounting, preserving pre-classification protection against unknown-host abuse | Phase-doc + catalogue registration; a dated ADR-0032 Amendment for a deny-by-default step instead | P02b-5 (the token-keyed stage and the early stage's treatment of validated callers); G34's anonymous-hop policy and ADR-0054's bounded accounting replacement are Accepted and delivered in P02d-5 | Open |
| G15 | What initiates a replay in this phase? | Targeted internal subscriber replay in P02b-3; producer/operator reset and fencing require a separate Phase 03 contract | Phase-doc; the URL space is its own decision record, per ADR-0036 | — (answered in § The consumer side; recorded so the question travels with the deferral) | Accepted — internal subscriber replay P02b-3; operator contract Phase 03 |
| G16 | What is `LS0002`'s rule name, which project trees does it run over, what does it inspect, and what escalates it to Error? | The name and scope recorded the way Amendment 1 recorded `LS0001`, with the scope reaching the assemblies that actually handle tokens | A dated **ADR-0032** amendment + a catalogue entry + an `AnalyzerReleases` row | P02b-8 | Open |
| G17 | What are the label sets for the event and job metrics, is the tenant axis permitted, and is it spelled `tenant` or `tenant_id`? | One note in the document that owns metric names, no raw tenant label on a per-event series, and an oldest-eligible-row age metric separate from the pending count | [Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics) edit | P02b-2 (the first series ships there, and a label added afterwards touches every emitter already written) | Event metrics Accepted — 2026-10-12; runtime/guards [implemented](#p02b-2-step-4--enforcement-and-closeout-2026-10-12); complete and merged; [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state; job metrics pending P02b-4 |
| G18 | May an integration-event payload or a job argument carry personal data, what may `last_error` contain, and do the four new durable stores enter the erasure scope? | Identifiers only, a sanitized and length-bounded failure descriptor, and the stores named in the erasure inventory | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) decides the new producer payload/NULL JSON metadata policy and application byte cap; a dated ADR-0038 cross-reference under this acceptance. [Data Protection § Right to Erasure](../architecture/23-data-protection.md#right-to-erasure-right-to-be-forgotten), P02b-8 and Phase 11 retain erasure ownership | P02b-1 (the payload rule: the first payload ships there), P02b-2 (the `last_error` rule and its DDL bound), P02b-8 (the erasure inventory) | Payload and failure descriptors Accepted and implemented; erasure inventory open P02b-8 |
| G19 | Where do the sample flow's publisher and consumer live — a fixture pair in the integration assembly, or a real event a shipped module consumes? | A real event between two shipped domain modules, because a test-assembly-only consumer leaves every module-scoped sweep with zero subjects | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) and the P02b-1 package; reconcile Tenancy/Customization/Audit specs under this acceptance. ADR-0046 accepts distinct subscription deduplication and envelope delivery scope; P02b-3 implements the two effects and Seeder handoff. The provisioning publisher must prove enqueue from its transaction announcement while ambient context remains unresolved | P02b-1 | Flow and delivery contract Accepted; subscriptions/Seeder handoff pending P02b-3 |
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
browser diagnostics distinguish launch, DevTools readiness and private-carrier
failures; cleanup preserves the first
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

Two fresh independent review rounds pair GPT-6-astra xhigh runtime review with
GPT-6.1-sol xhigh proof/corpus review. Round 1 checks `75fc79b..3f4c121` and
identifies one additional Minor in the outer browser proof: a caller-supplied
closed phase token can masquerade as a sanitized error. Commit `9b2fcf6` requires
the existing private brand at that boundary. Its paired actual-export cases
first fail for the forged token, then pass while retaining genuine branded
error identity. The first-round proof reviewer verifies the fix. Both fresh
Round 2 reviewers approve `75fc79b..9b2fcf6` without findings.

Full local verification passes 1,766 unit, 266 architecture, four contract and
1,152 integration cases: 3,188 backend cases, zero failed/skipped. Focused
verification passes 75 producer integration and 64 audit/transaction unit cases,
overlapping those totals. Guarded frontend tests pass 1,550 cases across two
packages, including 26 browser diagnostics cases, with zero skips/todos. Release
build has zero warnings/errors; solution format, frontend lint/typecheck, seven
ADR lifecycle checks, the changed-document link/anchor sweep and
`git diff --check` pass. Original Accepted Status/Decision/prior amendments and
the frozen P02b-0 entry record remain byte-preserved. The
[PR checks](https://github.com/HodeTech/LearnStack/pull/29/checks) own the exact-head
CI verdict; local results do not establish that verdict or merge approval.
PR review and merge remain pending.

#### PR #29 outbox INSERT and ownership follow-up (2026-10-12)

All four follow-up comments reproduce against `b561c8f`. Commit `96e6f4d`
marks the existing unit rollback-only when the immediate outbox INSERT throws,
then rethrows the same exception. Only a successful INSERT records produced
work; validation refusals before execution retain their existing behavior.

The existing duplicate-EventId regression now covers propagation and absorption
of a real PostgreSQL INSERT failure. Both cases first fail against the old code
at the immediate rollback-only assertion, before coordinator unwinding can
poison the unit. The fixed cases retain the successful parent's receipt, create
no failed-child receipt, prove original exception identity or absorbed-error
refusal, and verify rollback and shared-state cleanup. All 31 outbox cases pass.

Architecture 31 records Steps 2–3's delivered enqueue, successful-save
collection/promotion and owner-only drain. Standards 02/18 distinguish UoW-owned
domain collector/source cleanup from reconciliation and audit capture cleanup
owned exclusively by the outermost `AuditExecutionCoordinator` frame. The
catalogue no longer claims inbox parity for `InProcessEventBus`. Dispatch and
consumer delivery/inbox remain P02b-2 and P02b-3; no ADR decision changes.

Two fresh independent review rounds pair GPT-6-astra xhigh runtime review with
GPT-6.1-sol xhigh proof/corpus review. Round 1 approves `b561c8f..96e6f4d`
without findings. Round 2 covers the same code/document changes and this dated
closeout; both reviewers approve without findings.

Full local verification passes 1,766 unit, 266 architecture, four contract and
1,153 integration cases: 3,189 backend cases, zero failed/skipped. Focused outbox
repeats overlap that total. Release build has zero warnings/errors; solution
format, seven ADR lifecycle checks, the changed-document link/anchor sweep and
`git diff --check` pass. No frontend source, schema, credential or accepted ADR
changes. The [PR checks](https://github.com/HodeTech/LearnStack/pull/29/checks)
own final-head CI evidence; PR review and merge remain pending.

### P02b-1 merge and closeout (2026-10-12)

The maintainer merged [PR #29](https://github.com/HodeTech/LearnStack/pull/29) at
2026-10-12 13:47:58 UTC. GitHub records final PR source
`8a97cd2b7c19bf54a1b0b1f1ad75175256417c9c` and squash merge
`b1f6f21cab556ee7ba497dfc0d64e51b8cf1dad8`, whose parent is the previous main
`d37f46ccf0ebbf909f02c81a59d9019af3e9a1ca`. Source and squash merge have the
identical tree `ba6f9b552c17598ec219ec9d2fc04ef5f235eec0`. Development incorporates
the main merge without switching branches or rewriting published history.

All seven required checks pass in the
[final PR run](https://github.com/HodeTech/LearnStack/actions/runs/38255804801)
for that source; CodeRabbit also reports success. The tested synthetic merge is
`50fe60560b7966a23c14ab359531aadfbf59a442`. Verified downloaded artifacts record:

- **3,189 backend passes**, zero failures/skips: 1,766 unit, 266 architecture,
  four contract, 180 Docker-free integration and 973 Docker integration cases.
- **1,550 guarded frontend passes**: 1,490 web and 60 SDK, zero
  failures/skips/todos; frontend lint, typecheck and builds also pass.
- Exactly 27 matching Lighthouse JSON/HTML report pairs across nine URLs, nine
  screenshots and matching source/checkout identities and report digests.
  Automated accessibility is 1 throughout, CLS is 0 and maximum normalized
  initial JS gzip is 106,552 bytes. Owned cleanup passes; timing medians remain
  advisory. These are final PR-run results, not a claim about a post-merge run.

**P02b-1 is closed.** All four steps and verified review fixes are delivered;
their two independent review rounds remain recorded in the
[packet closeout and follow-ups](#p02b-1-packet-closeout-2026-10-12). Earlier dated
delivery/review records retain their historical pre-merge wording. Accepted ADRs
and P02b-0's entry record are unchanged. Phase 02b remains in progress.

**Next: P02b-2 decision pass.** Reverify G1, G3 ordering, G4, G5 resolver policy,
G7/G8, G17 and G18 failure descriptors before dispatcher implementation.
ADR-0046 remains reserved and undrafted; the decision package and any new ADR
require maintainer approval before implementation. P02b-2 owns the lease/grant
migration, existing-row handling, dispatcher credentials/boot guard,
claim/publish/mark, retry/terminal policy and metrics/health. P02b-3 owns inbox
delivery, the two Customization defaults subscriptions and the Seeder handoff.
Producer rows are durable but do not yet claim delivered consumption. Background
jobs, API authentication, realm reconciliation, BFF sessions and phase-exit
enforcement remain with P02b-4–8 in the [packet table](#packets-and-decision-gates).

**Documentation-only closeout validation.** All 266 local Release architecture
cases and seven ADR lifecycle/workflow controls pass, zero failures/skips. The
five changed Markdown files resolve 585 relative links and 186 fragments without
errors; added prose wrapping and whitespace checks pass. Prior dated delivery
records and all Accepted ADR files are byte-preserved. Full runtime suites are
not rerun locally for this documentation-only change; the final PR artifacts
above supply their evidence.

### P02b-2 decision package (2026-10-12)

**Accepted — 2026-10-12, @cemil.** The revised decision package and ADR-0046
are approved, including all four implementation steps and their review loop.
The documentation/code audit used `development`
at `b1f6df34b1d0db9dff77b355743d7bf8885947b3`, the P02b-1 merge closeout.
Three independent read-only audits covered delivery, privilege/audit and runtime/
observability. They ran no tests and changed no files. The accepted package uses
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md); earlier
Accepted ADR bodies and prior dated delivery records remain intact. The dated
acceptance record below distinguishes approval from implementation evidence.

The dated P02b-1 closeouts' "Next" and "reserved and undrafted" descriptions
record the state before this package. This current package and the Active index
replace those historical navigation instructions; the old records remain intact.

#### Review findings and accepted answers

The current bus's zero-handler success is unsafe for P1's real provisioning rows:
their consumers do not exist until P3. Separately, the outbox role cannot write
the MUST audit required by terminalization, and cancellation alone cannot bound
a callback that ignores its token. ADR-0046 explicitly resolves these gaps.

| Gate / concern | Accepted answer | Delivery owner |
|---|---|---|
| G1 subscriber identity and isolation | Shared transport-owned inbox keyed by tenant, stable subscription ID and event ID; separate delivery ledger owns attempts, terminal state and targeted replay | Contract now; implementation P02b-3 |
| G3 ordering | Deterministic eligible selection by availability, occurrence and ID within one processor; no enqueue, commit, retry or cross-process ordering promise | P02b-2 |
| G4 attempts and terminal state | Six committed normal claims; five published retry delays; fresh-token crash recovery; explicit terminal request and audited terminal timestamp; no seventh publish | P02b-2 |
| G5 type and zero-subscriber policy | Declared concrete types/compatible aliases only; invalid/retired/unknown records terminalize; Pending required subscriptions preserve rows; only intentional absence acknowledges without publication | P02b-2; Active durable subscribers P02b-3 |
| G7 terminal audit | Fenced state transition plus MUST security-event on one transaction through `WriteEventTerminalAsync`, with a restricted transition witness binding audit tenant/org/event to the actual UPDATE result; `platform.outbox.dead_letter` now, reserved Tenancy-owned `platform.delivery.dead_letter` when P3's writer ships; failed audit retries terminalization without delivery | P02b-2 producer; P02b-3 subscriber |
| G8 bypass | Explicit routine-dispatch exception, confined credential and exact grants; existing platform-scope entry audit unchanged | P02b-2 |
| G17 metric dimensions | No tenant labels; registry-bounded event types; separate backlog, eligible age, deferred backlog and heartbeat; immutable gauge snapshots | P02b-2 |
| G18 failure storage | Closed descriptor, at most 256 UTF-8 bytes with CHECK; replace free-form legacy descriptors without changing fact/state | P02b-2; erasure inventory P02b-8 |
| Credential topology | API-owned keyed lazy source; exact physical outbox-role checks; boot connectivity; no Seeder/web credential propagation | P02b-2 |
| G6/G19 dependency | Preserve common owner, envelope organization and effective SystemActor; activate both real defaults subscriptions only with durable acceptance and Seeder handoff | P02b-3 |

The exact normative replacements and additive audit seam are enumerated in
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#bounded-supersession-and-additive-seams).
They are not ADR-0041 errata. At acceptance, append dated disclosures to
ADR-0006/0010/0038, ADR-0003's unchanged role model and extended grant-budget
navigation, the bounded audit-grant replacement disclosure and
separate fifth-writer seam amendment to ADR-0044, and the same-transaction
clarification to ADR-0033. Keep original acceptance blocks,
decisions and earlier amendments byte-preserved. G1's rule changes are recorded
now but their implementation remains Registered for P3.

#### Runtime bounds and observation

These approved bounds are not implemented defaults yet. Options reject invalid
or unbounded values at composition; tests can inject time and tighter positive
bounds. Lease ownership always uses PostgreSQL time, never a caller's clock.

| Setting / policy | Approved initial bound |
|---|---|
| Worker and claim capacity | One worker; one just-in-time normal claim |
| Idle poll | 200 ms |
| SQL operation / complete terminal transaction deadline | 5 s |
| Publish wait deadline | 10 s |
| Lease | 30 s; validate margin for publish plus fenced transition |
| Progress health bound | 45 s without actual completed progress |
| Snapshot refresh | After completed iterations; idle target 5 s; stale at the 45 s health bound |
| Cancellation-ignoring callbacks | At most four retained tasks; block their partitions; unhealthy while any remain; stop new claims at four |
| Shutdown wait | At most 30 s; retain live task-owned resources until completion or process exit |
| Attempts and backoff | Six normal claims total; 1 s, 5 s, 30 s, 5 min, 1 h |
| Durable failure descriptor | Closed vocabulary, at most 256 UTF-8 bytes |

A publish timeout cancels the callback and persists its fenced failure, or leaves
an unknown outcome for expired-lease recovery if persistence fails. The callback
keeps ownership of its scope until actual completion; its late result cannot
acknowledge the row. Exclude its partition from new local claims until it finishes.
Unrelated partitions can progress within the four-task allowance. Saturation
stops acquisition and reports unhealthy; no unbounded detached-task replacement
or forced managed-task termination is claimed. P3 must also fence its business
transaction before enabling Active production subscribers.

Its completion gate must reject a canceled/timed-out attempt before COMMIT even
if failure persistence did not replace its token. Cancellation after physical
COMMIT starts remains Indeterminate under ADR-0033; P3 recovers from atomic
business/inbox/ledger state rather than promise rollback at that boundary.

Pending count includes all unprocessed, nonterminal rows, including delayed,
leased, terminal-requested and manifest-deferred work. Eligible age uses the exact
normal-publication claim predicate: it excludes terminal requests, exhausted
attempts, future availability, live or unrecovered expired claims, and held/locally
blocked partitions. It is zero when none qualify and never negative. Deferred
count exposes manifest-held work.
Heartbeat advances only after actual bounded loop work, including an empty poll,
not from an independent timer. Failed refresh retains the last snapshot and
marks observation stale. Terminal-audit failure is unhealthy, never a fabricated
successful empty queue. `/healthz` remains liveness-only.

Standards 10 owns the final metric inventory at acceptance. Preserve existing
outbox duration/failure/dead-letter/lease-loss names with registry-bounded
`event_type` where applicable. Add unlabelled
`learnstack_outbox_oldest_eligible_age_seconds`,
`learnstack_outbox_deferred_count` and
`learnstack_outbox_heartbeat_age_seconds`; pending count becomes unlabelled.
Inbox counters remain reserved for P3, and job metrics for P4. Gauge callbacks
perform no SQL. Dispatcher-owned logs, traces and failure descriptors use validated
correlation and bounded outcomes, without exception text, payload, raw stored type
or connection data. Current bus/consumer exception telemetry is a separate P3
reconciliation obligation before Active production subscriptions; this packet
does not claim that existing transport diagnostics are already minimized.

#### Four approved implementation steps

Each step implements and commits on `development`, then runs two fresh independent
review rounds. Verify findings against current code, fix valid findings, validate
and commit fixes before the next round/step. A clean review needs no empty commit.
The loop continues automatically unless a blocker or new maintainer decision
requires input. No branch switch or history rewrite is part of this package.

1. **Decision reconciliation, schema and confined composition.** Accept ADR-0046
   and the gate parts above before code. Register catalogue obligations; update
   canonical Outbox DDL/indexes/grants, metric labels, audit policy, architecture
   and glossary. Add Platform-chain lease/terminal/descriptor/identity migration
   and Audit-chain INSERT-grant migration. Declare registry/aliases/readiness,
   keyed credential validation and physical-role guard. Adapt test-owned host
   composition explicitly; production registration stays enabled. Validate exact
   privilege sets, forward/down/reapply and invalid legacy input. No handler or
   arbitrary type is constructed to discover subscription metadata.
2. **Durable transitions and terminal audit.** Implement parameterized claim,
   recovery, success, backoff, terminal-request and atomic terminal-audit paths.
   Add the confined fifth store method, off-path catalogue/matrix row and narrow
   call-site/transition-witness guards. Prove token/expiry fences, same-worker
   reclaim, six-claim crash,
   no seventh publish, uncertain completion and audit rollback on real PostgreSQL
   using actual outbox/application roles. Intentional absence and Pending holds
   must have distinct non-vacuous controls.
3. **Hosted lifecycle and observability.** Activate the API BackgroundService;
   implement bounded waits/overruns/shutdown, progress health, safe traces and
   snapshot metrics. Adapt the API launcher/demo environment allowlist and its
   proofs without forwarding the credential to web/Seeder. Prove production
   registration, startup rejection/valid controls, poison-row progress, ignored
   cancellation, safe disposal and continued unrelated work within the bound.
4. **Enforcement and closeout.** Complete production-census and planted controls,
   full Release regression/format, mandatory Docker proofs and applicable demo/
   frontend tooling checks. Reconcile current-state docs, actual test counts,
   catalogue status and seven existing CI checks at the exact PR head. Create the
   PR for maintainer review only after every step/review is complete; claim no
   merge or P3 delivery from P2 closeout.

#### Decisive proof obligations

- Two independent processors contend under live leases; a release-on-read mutant
  fails. Expiry allows redelivery; both stale success and stale failure lose the
  token fence, including reacquisition by the same worker identity. This is not
  a claim of two separate processes or exactly-once publication.
- Retry boundaries use controllable time/database availability, not wall-clock
  sleeps. Crash recovery after claim six and audit refusal cannot cause a seventh
  publish. Reconcile lost/uncertain terminal COMMIT before retrying so a completed
  terminal audit is not duplicated. Terminal, delayed, leased, deferred and due
  rows distinguish all queue gauges.
- Audit refusal on a terminal request with fewer than six attempts never makes
  that row publishable again. Its pending contribution remains; its contribution
  to oldest publish-eligible age is excluded.
- Known V1 payloads remain readable after declared deployment aliases. Reverse
  commits, equal occurrence times and retry overtaking falsify stronger ordering.
  Unknown/retired/malformed/mismatched rows do not trigger arbitrary type loading.
- Real TenantCreated rows survive repeated Pending polls without attempts or
  constructions. P3's activation proof must later deliver them to both stable IDs.
- Missing/malformed/wrong-user credentials refuse without secret text; valid
  configuration is lazy; real physical guards reject elevated/transitive roles.
  Audit INSERT is allowed; audit read/update/delete and business access are not.
- Against the real bypass role, replace an audit draft's tenant, organization or
  event while retaining the successful fenced transition's restricted witness.
  Each mismatch must leave neither terminal state nor audit row committed. Prove
  a matching organization-scoped success and rejection of stale/wrong-transaction
  witnesses; GUC/RLS assertions cannot substitute for these controls.
- Cancellation-ignoring operations retain safe resource ownership, cannot write
  late acknowledgements and cannot grow beyond the approved bound. A blocked or
  saturated loop becomes unhealthy while `/healthz` still returns 200.
- Before P3 activates subscribers, its proof must include failed timeout
  persistence followed by late business completion and timeout during physical
  COMMIT on the final subscriber claim; atomic ledger/inbox state decides recovery.
- Structural censuses include production subjects and planted clean/dirty cases
  for each key's resolution, publication, type loading and write confinement.
  Behavioural controls mutate lease/terminal/audit safeguards to prove sensitivity.

The P02b-2 publication proof uses the shipped BackgroundService, registry and
transition/audit stores on real PostgreSQL and actual roles. Keep real TenantCreated
subscriptions Pending. Only isolated test composition supplies an Active synthetic
contract and controlled transport/handler to exercise publish/mark, failures,
recovery and deadlines; intentional absence is a separate control. Assert nonempty
production machinery, without inventing a production subscriber. P3 owns handler
and durable-acceptance censuses when those production subjects exist.

#### Scope retained by later owners

P3 owns shared Inbox/delivery DDL and RLS, per-subscription business/audit frames,
retries, terminal audit and targeted replay, both Customization defaults and
Seeder identity handoff. That boundary is required by the packet dependency,
not an unowned deferral. The companion Hub roadmap's per-module inbox reference
needs a coordinated dependency-pointer update before P3 closeout; Hub remains
roadmap owner and this package adds no Hub endpoint or runtime work. P4 owns jobs;
Phase 03 owns operator routes/permissions; P02b-8 owns erasure inventory; Phase 11
owns brokers, cross-process evidence, purge and readiness exposure.

#### Proposal review and validation (2026-10-12)

At proposal commit `cc5de26`, two independent review rounds completed before
maintainer approval. Round 1 used
separate GPT-6.1-sol xhigh delivery and security/corpus reviewers. Verified fixes
separate the audit-grant replacement from the additive fifth seam, allow safe
terminal auditing of malformed causal metadata, scope diagnostic minimization to
the dispatcher, and require atomic subscriber ledger completion plus a sticky
timeout gate. Cancellation after physical COMMIT starts remains Indeterminate.
Both reviewers approved the corrected proposal.

Round 2 used fresh GPT-6.1-sol xhigh delivery and GPT-6-astra high corpus/privilege
reviewers. Both approved without actionable findings. Reviews were read-only;
they did not run runtime suites or claim implementation evidence.

Documentation-only validation passes: 266 local Release architecture cases,
seven ADR lifecycle/workflow controls, 329 local links and 119 fragments across
the four changed Markdown files, prose wrapping and `git diff --check`. Prior
P02b-0/P02b-1 dated delivery records and all Accepted ADR files are byte-preserved.
Full runtime/Docker suites remain implementation-step obligations; no dispatcher,
inbox or gate delivery is claimed by this proposal.

#### External proposal review reconciliation (2026-10-12)

Two external reports were checked against the current draft, accepted contracts,
canonical DDL and production code. Their reported line numbers are not authority.
This revision remains Proposed and changes no runtime or Accepted ADR body.

| Finding | Verified disposition |
|---|---|
| Erasure assigned to Phase 11 | Already corrected in `cc5de26`: privileged redaction and its bounded caller contract belong to Phase 03; only retention purge belongs to Phase 11 |
| Two subscription IDs conflict with `card@1` / `plain@1` | Rejected: `BuiltInCustomizations.Card` is a `BuiltInContentType`; `Plain` is a `BuiltInTaxonomy`. ADR-0057 freezes those two independent effects, not two content types. The ADR now maps each ID explicitly |
| Bypass audit INSERT lacks row-bound identity proof | Valid: the seam now receives a restricted witness from the actual fenced UPDATE result; draft tenant/org/event/subscription must match it. Forged-identity and matching-success proofs are explicit |
| Terminal-request normal eligibility and undefined release | Explicit predicates now exclude terminal requests/exhausted attempts; recovery is separate. Pending holds precede payload validation, even for malformed payloads. Removed the unexplained release transition; success/retry/terminal paths clear leases |
| Inbox completion, identity grammar and replay ambiguity | Clarified uncommitted claim and atomic completion/rollback; completed inbox rows never reset; subscription IDs version deduplication semantics |
| Grant locator, legacy batch/exactly-once/DLQ designs and metric/off-path carriers | Named exact authorities and acceptance replacements. ADR-0003's role model remains unchanged; Database Standards owns actual grants. No competing current dead-letter design remains after acceptance |
| Envelope storage, narrow P3 SELECT, subscriber audit owner and P2 proof subjects | P3 must name durable envelope copies/references before DDL and inventory them for erasure; scheduler cannot read payload; subscriber audit is Tenancy-owned. P2 exercises shipped machinery with isolated test contracts while real subscriptions stay Pending |
| Final acceptance/handler failure and roadmap target checks | Failed/unknown durable acceptance consumes producer attempts; only accepted deliveries isolate business retry budgets. The phase's own undated zero-subscriber/replay sketches are now named acceptance replacements |
| Missing references and historical Next navigation | Added applicable ADR links and the ADR-0047 reservation link; added current navigation without rewriting dated P1 records. ADR-0045 is not used by this ADR and needs no artificial reference |

The internal executor performs delivery, not publication; it receives no exception
to the outbox-only publisher rule. SecurityEvent records a consequential bypass
terminal transition, not routine polling. The selected order receives matching
normal-claim and recovery/terminal indexes. These are contract clarifications,
not claims that the proposed mechanisms have shipped.

The revision received read-only GPT-6-astra high audit/privilege verification and
a fresh GPT-6-sol xhigh delivery/corpus review. The latter identified the Pending
validation priority, acceptance-failure budget and phase-carrier gaps recorded
above. Both reviewers approved after the verified fixes; neither claimed runtime
execution evidence.

Revision validation passes: 266 Release architecture cases, seven ADR workflow
controls, 337 local links and 124 fragments across the four proposal Markdown
files, prose wrapping and `git diff --check`. Accepted ADR files and dated
P02b-0/P02b-1 delivery records remain byte-preserved. Only this package and the
Proposed ADR changed in the revision. Maintainer approval still precedes runtime
implementation and acceptance-carrier edits.

### P02b-2 acceptance (2026-10-12)

**Accepted by @cemil.** The maintainer approves the revised ADR-0046 and this
decision package, and authorizes all four implementation steps on development
with two fresh independent review rounds per step. The accepted decision is
committed before runtime edits. G1, G3's ordering, G4, G5's resolver/readiness,
G7, G8, G17's event metrics and G18's failure-descriptor parts are Accepted.
P02b-3 still owns durable acceptance, inbox/ledger DDL, independent subscriber
execution/retries, both real default subscriptions and Seeder handoff; job and
erasure gates retain their later owners.

Acceptance appends bounded supersession disclosures and additive seams to the
named Accepted ADRs, reconciles the current standards/architecture/glossary/skill
carriers, and registers six catalogue obligations. Original ADR acceptance
metadata, decisions and prior amendments remain intact. Dated P02b-0/P02b-1
records remain byte-preserved. Registered rules and planned audit operations
claim no runtime enforcement; delivery evidence belongs to each step below.

### P02b-2 Step 1 — Decision, schema and confined composition (2026-10-12)

**Implemented; both independent review rounds and fresh fix review complete.**
Decision commit
`192810b` precedes runtime edits. Step 1 adds the Platform-chain migration
`20261012180355_add_outbox_dispatch_state` and Audit-chain migration
`20261012180433_grant_outbox_terminal_audit`. Existing migration bodies remain
unchanged. New state/identity/UTF-8 CHECKs and eligibility/recovery/terminal
indexes accompany exactly eight outbox status-column UPDATE privileges and
INSERT-only audit access. Legacy diagnostic replacement spans both tenants;
invalid legacy identities reject the migration and roll back its owner-only
backfill. Disposable-schema down/reapply preserves event facts and retains
redaction; live rollback requires a forward fix.

The API registers a keyed, lazy, DI-owned outbox source with exact configured
username checks and physical session/current-role, attribute and membership
checks. Missing configuration fails upon resolution; Step 3's mandatory worker
startup will resolve it and prove connectivity. Seeder/web receive no new source.
The immutable contract/alias/readiness registry constructs no handlers. Real
TenantCreated has its two distinct Pending subscriptions; Active, intentional
absence, unknown and retired identities have separate controls. The two existing
connection-resolution censuses explicitly admit the approved dispatcher builder
and composition root, without wildcard exemptions.

**Verification before review:** `CI=true` Release build has zero warnings/errors.
TRX counters and process exit codes record zero failed and zero skipped cases:

| Suite | Passed | Scope |
|---|---:|---|
| Unit | 1,800 | Complete unit suite, including registry/composition/configuration controls |
| Architecture | 266 | Complete non-skippable suite |
| Contract | 4 | Served OpenAPI/public SDK contract suite |
| Integration, no Docker | 180 | Complete HTTP suite |
| Integration, Docker | 128 | Migration/role/schema/grant/rollback classes affected by Step 1 |

The Docker slice includes `OutboxDispatchMigrationTests`,
`OutboxDispatcherRoleGuardTests`, `PlatformSchemaTests`, `TenancySchemaTests`,
`AuditSchemaTests` and `MigrationRollbackTests`. Role-mutation proofs own a
separate container; destructive schema probes own disposable databases. These
counts describe this step's completed checks, not a full Docker regression.
The six new catalogue rules remain Registered: transition/audit/worker proof
lands in later steps. No dispatcher execution or P02b-3 delivery is claimed.

#### Step 1 review round 1

Fresh read-only GPT-6-astra xhigh security/schema and GPT-6.1-sol xhigh
corpus/test reviewers examined `192810b..d9bc77b`. Security review found no
actionable defect. Corpus review found a stale handler-owned inbox instruction
in the code-review skill and an unqualified historical per-module inbox note in
Architecture 10. Both were verified and reconciled to ADR-0046's transport-owned
P02b-3 target. Original dated notes remain intact. No runtime change was needed;
the existing test evidence above remains applicable. The correction passes local
file/fragment checks, prose wrapping and `git diff --check`.

#### Step 1 review round 2

Fresh GPT-6.1-sol xhigh security/composition and GPT-6-astra high corpus/proof
reviewers examined `192810b..6a13455`. Corpus review approved without findings.
The composition review verified that an open generic Active handler could match
the discovery census despite being unconstructible. Active admission now requires
a closed type. A regression proves discovery sees the open generic offender,
refuses its matching manifest and accepts a closed generic control. The complete
Release unit suite passes 1,801 cases; the complete architecture suite passes
266, both with zero failures/skips. A fresh focused review checks this correction
before Step 2 begins. Removing the closed-type check makes the new regression
fail; restoring it makes all 17 registry cases pass. No migration, grant or
Pending policy changed.

Fresh GPT-6.1-sol high focused review of `6a13455..b6fc714` approved the correction
without findings and independently checked its accepting/rejecting controls and
saved TRX counters. The completed Step 1 commits are `d9bc77b`, `6a13455` and
`b6fc714`; the next authorized implementation step is durable transitions and
terminal audit.

### P02b-2 Step 2 — Durable transitions and terminal audit (2026-10-12)

**Implemented; both independent review rounds complete.** `OutboxDispatchStore` uses
short parameterized database-time transitions. Normal acquisition commits one
fresh token and one attempt before returning; expired ownership requires a fresh
recovery token without another attempt. Success, failure, terminal request and
terminalization require the current token and an unexpired lease. Five backoffs
follow six counted normal claims; exhausted or requested work never reenters
normal publication. Pending identities and aliases are excluded before payload
validation in every acquisition path. Local blocked partitions receive the same
exclusion.

`OutboxEnvelopeReader` uses only the registry's concrete type and frozen options.
It compares persisted fact/routing/time, tolerates PostgreSQL microsecond
truncation and checks the canonical reserialization byte cap, rather than jsonb
whitespace. Complete envelope admission precedes intentional acknowledgement;
Pending, unknown, retired and invalid outcomes remain distinct.

The fifth `IAuditStore` method preserves the existing four writes. Only
`OutboxDispatchStore.TryTerminalizeAsync` derives the minting capability and calls
it. The private immutable witness reads all identity and causal values from the
actual fenced UPDATE result, including the old acquisition token. The real audit
store requires the declared producer MUST/SecurityEvent, matching draft identity
and the same live connection/transaction. Both writes commit together. Invalid
causal values are omitted; invalid correlation uses a validated attempt trace.
No audit SQL is added outside `PostgresAuditStore`. Tenancy registers the producer
operation; the subscriber operation remains planned for P02b-3.

Terminal SQL plus audit share one five-second deadline. A lost COMMIT response
triggers a separate bounded durable-state read before returning/retrying; it
cannot insert a second audit into a completed terminal row. A test-owned TCP
proxy drops the real PostgreSQL COMMIT acknowledgement after physical commit,
proves reconnection and checks one durable terminal/audit pair. Production has no
fault hook. Failed audit retains the terminal request, including below attempt
six, and never permits another publication.

Tests use actual `learnstack_app` inserts with announced tenant/org and the
guarded `learnstack_outbox_admin` source for transitions. Disposable databases
isolate grant and state mutations. Nonempty whole-production Cecil censuses and
clean/dirty controls cover witness shape, minting/fifth-write callers, async and
delegate helpers, required catalogue injection and named dynamic type-loading
APIs. This is a bounded IL capability check, not arbitrary reflection/dataflow
analysis. SharedKernel has no friend assembly.

Four behavioural mutation controls each fail as intended: removing lease expiry,
admitting terminal requests for publication, omitting MUST audit and discarding
successful uncertain-COMMIT reconciliation. Every edit is restored before the
clean validation run. Six-attempt tests start at zero and commit all six claims;
they cover both an explicit sixth failure and an expired sixth claim. Snapshot
proofs distinguish due, future, leased, expired, requested, exhausted, deferred,
locally blocked, processed and completed-terminal rows.

The four transition/audit/type catalogue rules are Implemented. Hosted lifecycle,
publication/overrun proofs, credential caller enforcement and observation remain
Steps 3–4. Real default subscriptions stay Pending; no P02b-3 business effect,
inbox or delivery ledger is claimed.

**Verification before review:** Release solution build has zero warnings/errors;
formatting covers all 21 changed C# files. TRX counters and process exit codes
record zero failures/skips: complete unit 1,825, complete architecture 273,
contract 4, complete Docker-free integration 180 and affected Docker integration
110. The Docker slice covers `OutboxDispatchStoreTests`, `AuditStoreTests`,
`PlatformAdminScopeTests`, `TenantAssertionAuditTests` and
`PublicReadTransactionTests`; it is not a full Docker-suite claim. All four
behavioural mutants fail their named tests, then the restored code passes.
Local file/fragment, historical-record/Accepted-ADR preservation, prose-width and
`git diff --check` controls pass.

#### Step 2 review round 1

Fresh read-only GPT-6-astra xhigh security/concurrency and GPT-6.1-sol xhigh
proof/corpus reviewers examined `c6ceaa9..7976113`. The proof review approved;
its independent rerun passed 273 architecture and 24 focused unit cases. The
security review reproduced a stored-routing gap: deserialization regenerates
get-only Topic/PartitionKey values, masking changed or absent payload members.
The reader now checks those required string members in the original JSON before
using the declared CLR contract, then compares the reconstructed routing with
the row. Ten unit mutation cases and four real-database cases cover changed,
missing and malformed routing, terminal-only progression and minimized audit.
The unchanged payload remains an admitted control. Removing the original-field
check makes all ten new unit cases fail. After restoration, the complete unit
suite passes 1,835 cases, architecture passes 273 and the four new Docker cases
pass, all with zero failures/skips. Round 2 follows the verified correction;
hosted lifecycle remains Step 3.

#### Step 2 review round 2

Fresh read-only GPT-6.1-sol xhigh security/concurrency and GPT-6-astra high
proof/corpus reviewers examined `c6ceaa9..ffce05f`. Both approved without verified
findings. Independent Release no-build reruns passed 34 envelope/options unit
cases, 19 dispatch/audit architecture cases and all 53 dispatch-store Docker
cases, with zero failures/skips. The proof reviewer independently passed all seven
dispatch capability facts and checked the real COMMIT-response-loss result in
the restored database receipt. Both inspected the actual row-bound audit and
routing correction rather than accepting review notes as evidence. Step 2 is
closed; Step 3 may activate the mandatory worker and observation. No P3 delivery
or inbox effect is claimed.

### P02b-2 Step 3 — Hosted lifecycle and observability (2026-10-12)

**Complete; both independent review rounds passed.** The API owns
its mandatory `OutboxProcessor` BackgroundService, guarded dispatcher-source
startup resolution/connectivity, bounded publication lifecycle and retained
overrun ownership. Observation supplies immutable gauge snapshots, bounded
producer diagnostics and the registered, unmapped `outbox` health check.
`/healthz` remains liveness-only. Paired API launchers supply application and
confined dispatcher credentials; every Seeder invocation uses an explicit
application-only environment, and web receives neither database credential.

**Verification before review:** the Release solution build reports zero
warnings/errors. TRX counters record zero failures/skips in the complete unit
suite (1,845), contract suite (4), Docker-free integration suite (182), restored
architecture suite (273), and the actual-host PostgreSQL `OutboxProcessorTests`
slice (16). Worker helper/observation unit cases are included in the unit total.
The initial architecture run exposed the newly registered worker in the global
keyed-resolution allowlist; the exact outbox-key allowance is corrected and the
restored 273-case run passes. This is a focused Docker-runtime receipt, not a
full Docker-suite claim. All three actual API-launcher cases also pass after
correcting the environment reader to accept the explicit dispatcher credential.
The two affected frontend files pass 116 tests; web lint and typecheck pass.
Whitespace verification passes for all 39 changed C# files; affected TypeScript
and runtime-script Prettier checks, script syntax, diff whitespace and 2,172
relative links/396 fragments pass. Historical producer delivery records and
Accepted ADR bodies remain byte-preserved.

`OutboxProcessorTests` covers actual registration/guarded boot, invalid credential
refusal, real provisioning Pending holds, poison/ordinary failure progression,
explicit intentional absence, retained callback/cleanup ownership, four-task
saturation and finite shutdown. Observation tests cover monotonic stale health,
failed-refresh retention, terminal-audit failure, finite metric labels and fresh
trace identity, with bounded canonical correlation reconstructed from parsed
identity rather than optional traceparent extensions. Two independent actual API
workers prove no second publication/claim while the first live lease is held.
Never-blocks, live-ownership and Pending catalogue rules are Implemented. The full
health production census/planted controls remain Step 4; that rule is Registered.

Two behavioural mutations fail in the intended executed test: removing local
partition admission permits a sibling claim before retained cleanup completes;
removing live-lease admission permits a competing worker to claim the held row.
Both source files are byte-restored before the clean build/unit/worker rerun.
Step 3 completed both independent rounds and the verified corrections below.
Steps 1–2 completed both rounds in their
records above. Step 4 owns full credential/publication censuses, catalogue
enforcement and packet closeout. P02b-3 still owns durable acceptance, shared
inbox/ledger, independent subscriber retries, both real default subscriptions and
Seeder handoff. Real required subscriptions remain Pending; no consumer business
effect is claimed.

#### Step 3 review round 1

Fresh read-only GPT-6-astra xhigh security/lifecycle and GPT-6.1-sol xhigh
proof/corpus reviewers examined `d4c06a46..756c8523`. Security approved without
verified findings. The proof reviewer found that the demo's pre-existing
`--validate-demo-config` invocation forwarded all four credentials to Seeder,
contradicting the new confinement claim. Validation now runs inside the launcher
with a closed local connection grammar; the obsolete Seeder mode is removed.
Manifest and convergence modes share the same explicit child allowlist, and
standalone `make seed` uses it too, excluding ambient aliases and role passwords.
Stale role/password/database/host/port and malformed configuration controls move
with their owner instead of weakening the check. The complete Docker integration
regression at the implementation commit passes 1,083 cases with zero failures or
skips. This supplements the focused pre-review receipt above; final packet
verification will rerun the changed tooling and actual PR-head CI.
The correction's Release build has zero warnings/errors; the Docker-free
integration run passes 175 cases after seven obsolete parser cases moved to the
launcher suite. The guarded complete frontend run passes 1,608 cases (1,548 web
and 60 SDK), with zero skips/todos; 91 demo lifecycle cases include all four-role
configuration and actual shell/child-environment controls. Typecheck, affected
formatting, script syntax, diff whitespace and the delivery-record link check
pass. These are correction receipts, not a new full-Docker rerun.

#### Step 3 review round 2

Fresh read-only GPT-6.1-sol xhigh security/lifecycle and GPT-6-astra xhigh
proof/corpus reviewers examined `d4c06a46..da637e35`. Neither found a Blocker or
Major. Security identified one operability gap: the safe startup refusal omitted
the configuration key/form required by the accepted completion criterion. The
fixed message now names `ConnectionStrings:OutboxDispatcher`, Npgsql key/value
syntax and `learnstack_outbox_admin`, without provider or supplied-value text.
The real-host negative theory asserts that guidance for missing configuration;
all four credential cases pass after the correction. Independent review reruns
passed 91 demo lifecycle cases, 11 focused outbox unit cases and two construction
host cases; the security reviewer separately passed nine helper/observation cases.
Both inspected the prior receipts and production paths. They did not claim a new
full build or Docker rerun. Step 3 is closed; Step 4 may add the complete
credential/publication/health censuses and close the packet.

### P02b-2 Step 4 — Enforcement and closeout (2026-10-12)

**Implementation and local closeout complete; both independent review rounds
passed.** Current CI/review/merge state belongs to the
[PR handoff](#p02b-2-pr-handoff-2026-10-12).
Steps 1–3 completed both independent review rounds; Step 3 is closed at `02540d0`.
Step 4 adds nonempty whole-production credential/publication censuses, exact
keyed factories and consumer bindings, and planted helper/delegate/async controls.
Only the processor constructs the task-owned publication attempt; its private
callback is the sole event-bus publication path. The existing canonical module
bus rule now also enforces that complete production fence.

The five-subject observation graph follows async and production-owned
polymorphic contracts, rejects persistence/service-locator capabilities, and
inspects actual gauge callbacks. Actual DI controls reject missing/duplicate
workers/checks and wrong factories. Round 2 fixes at `606362b` pin exact credential
reader signatures, the external System clock and actual health/observation binding.
All 26 focused/corpus cases and both fresh fix reviews pass.
The literal-SQL census admits only exact producer enqueue, dispatcher
status-transition and migration backfill methods; quoted/schema/case/CTE and
missing/extra-writer controls prove its reach. These are bounded IL/literal-SQL
checks, not arbitrary reflection or dynamic-SQL dataflow analysis.

**Verification before review:** the full Release solution build reports zero
warnings/errors, and solution-wide `dotnet format --verify-no-changes --no-restore`
passes. All 21 focused census/publication cases pass with zero skips,
including compiled actual production subjects and every planted control. Role
and Health catalogue rules are Implemented; Retry also names its canonical
architecture method. The source/catalogue recount is 193 architecture test
methods, 192 Implemented rules, 118 in that assembly and 74 elsewhere.

The full Release backend regression passes 3,393 cases with zero failures/skips:
1,845 unit, 286 architecture, four contract, 175 Docker-free integration and
1,083 Docker integration. TRX counters were inspected directly; the nonempty-run
guard passes. The three additional case-insensitive credential-alias controls
also pass in the focused seven-case run. Step 3's corrected launcher/frontend
receipt remains the applicable local evidence: 91 lifecycle cases and 1,608
frontend cases. These checks are not claimed as new Step 4 frontend reruns.
The full PR-range documentation check passes 2,555 relative links/459 fragments,
and all seven ADR-status script tests pass. Historical records and Accepted ADR
text remain preserved. All four implementation steps and both independent review
rounds are locally complete; post-fix verification is recorded below. The
[PR handoff](#p02b-2-pr-handoff-2026-10-12) owns current CI/review/merge state.

P02b-3 still owns durable acceptance, shared inbox/ledger, independent subscriber
retries, both real default subscriptions and Seeder handoff. Required production
subscriptions remain Pending; test-owned Active contracts prove producer runtime
without claiming consumer business effects.

#### Step 4 review round 1

Fresh read-only GPT-6-astra xhigh boundary and GPT-6.1-sol xhigh proof/corpus
reviewers examined `02540d05..d2531f2`. They reproduced three structural test gaps,
without finding a current production dispatcher defect. Captured key/provider
fields could be reassigned after their original capture; valid comment-separated
SQL and a later `TRUNCATE` target escaped the literal matcher; an additional
factory-based hosted worker escaped the descriptor type filter.

The fixes close all three: captured-field mutations are restricted to the exact
factory parameter assignment, SQL recognizes those literal token separators and
all listed truncate targets, and the actual hosted descriptor set includes only
the singleton dispatcher and framework health publisher. Added dirty controls
cover key/provider writes in the factory, callback and helper, field/argument
address escapes and argument reassignment, SQL variants, and factory/instance
worker duplicates. No test method or catalogue count is added. All 21 focused
cases pass after the three corrections; the final seven boundary cases pass
again after the adjacent argument-mutation controls, with zero failures/skips.
The full regression above predates these test-only changes. The proof reviewer
independently passed 26 existing focused/corpus cases, with zero failures/skips;
neither reviewer claimed a new full suite. Round 2 remains pending.

#### Step 4 review round 2

Fresh read-only GPT-6.1-sol xhigh boundary and GPT-6-astra xhigh proof reviewers
examined `02540d05..2f8693e`. They verified three test-guard gaps without finding
a current production defect. Named worker/store source-reader overloads could
self-authorize; an external `TimeProvider` subclass was invisible to the
production-owned contract graph and DI checks; a same-type health factory detached
from the singleton observation passed the returned-type check.

The fixes are delivered at `606362b`: pin unique literal source-reader method
signatures, visibility and staticness with overload controls; require the actual
`TimeProvider.System` singleton instance with missing/duplicate/alternate-instance
and factory controls; and exercise the actual DI-registered health check through
Healthy → Unhealthy → Healthy against the singleton observation, with a detached
factory dirty control. The polymorphic graph covers production-owned contracts;
the external clock is admitted only through the explicit System-instance pin.
No test method, catalogue rule or published count is added.

Post-round-1 verification at `2f8693e` passes the full 286-case architecture suite
and five Corpus cases; the Release build has zero warnings/errors. After the
round-2 fixes at `606362b`, all 26 focused/corpus cases pass with zero
failures/skips. Fresh GPT-6.1-sol xhigh boundary and GPT-6-astra xhigh observation
fix reviewers both approve with no remaining verified findings. The boundary
reviewer independently rejects both named-method overload plants (two offenders
each), observes zero clean/restored offenders and passes three focused Release
cases. The observation reviewer independently detects the SQL-capable external
clock and detached same-type health factory, observes zero restored graph
offenders and passes two focused Release cases. Neither claims a new full suite.

The full post-fix Release build has zero warnings/errors, all 286 architecture
cases pass with zero failures/skips, and solution-wide format verification
passes. The final local documentation check passes 2,575 relative links and
479 fragments with zero errors. The 3,393-case regression above predates these
test-only guard fixes; its
TRX counters and the 26-case focused/corpus counters were checked directly. No
test method, catalogue rule or published count changes. All four P02b-2
implementation steps and both independent review rounds are locally complete.
PR-head CI and merge remain pending; this closes producer dispatch/enforcement
without claiming any P02b-3 consumer delivery.

### P02b-2 PR handoff (2026-10-12)

[PR #30](https://github.com/HodeTech/LearnStack/pull/30) opens development → main
from local closeout `7bf14a0`. All four implementation steps and both independent
review rounds per step are complete. The PR's checks and current-head evidence
in its description own live CI state; its review and merge state remain on the
same PR. Earlier pending statements in dated local receipts are as-of records,
not the current check verdict. No merge is claimed by opening the PR.

The seven existing required contexts and strict up-to-date protection were read
back from GitHub unchanged. Final verification must use the PR's actual current
head, not an earlier passing run. The PR description records run URLs and suite
counters after that verification. Five final local corpus checks pass with zero
failures/skips; this is not a CI result.

P02b-3 is next after P02b-2 review/merge and its own decision pass: shared inbox
and delivery ledger, durable acceptance, independent subscriber retries/audit,
both real defaults subscriptions and Seeder handoff. No consumer delivery,
background-job or authentication completion is claimed here.

### P02b-2 PR review follow-up (2026-10-13)

All seven required jobs pass on PR head `a80e06d`, including the complete
3,393-case backend regression and 1,608 frontend cases, with zero failures/skips.
[Run 38306262165](https://github.com/HodeTech/LearnStack/actions/runs/38306262165)
also supplies 27 matching Lighthouse JSON/HTML report pairs and nine screenshots.
All reports have zero CLS, accessibility score 1 and normalized gzip JavaScript
between 106,359 and 106,553 bytes. Proof finishes in 646.454 seconds; owned process,
Compose, private-file, environment and lock cleanup all pass. These are receipts
for that head, not evidence for a later correction.

Automated PR review identified a local-launcher validation defect: omitting both
a role's connection-string password and its selected password variable compared
two absent values as equal. Independent reproduction accepts that invalid state
for all four roles. Four new regression cases fail against the previous validator
and pass after requiring a nonempty configured string password. Existing quoted
password, whitespace and alias controls remain green; diagnostics remain fixed
and contain no supplied configuration. No runtime dispatcher or API contract
changes.

Local verification passes 95 lifecycle cases and the guarded frontend suite's
1,612 cases (1,552 web and 60 SDK), with zero failures/skips/todos; lifecycle cases
are a subset, not an additional suite count. Workspace lint/typecheck pass.
Fresh sequential GPT-6.1-sol high and GPT-6-sol high correction reviewers both
approve `a80e06d..a851b7c` with no findings. Each independently reproduces the four
parent/current differences and passes all 95 lifecycle cases. Five Corpus cases
and seven ADR-status script tests also pass; the final link check covers 2,586
relative links and 490 fragments with zero errors. Final correction-head CI
remains pending in this local receipt.
The [PR handoff](#p02b-2-pr-handoff-2026-10-12) continues to own live PR state.

### P02b-2 external review remediation (2026-10-13)

Both external reports against `44089707` were treated as review evidence and
checked against current code and Accepted ADR-0046. No new decision, consumer
delivery or grant change is part of this correction. Existing migrations and
Accepted ADR bodies remain unchanged.

- Recovery now states its index predicate explicitly, without relying on
  PostgreSQL to infer the lease CHECK. A new Platform migration replaces only
  the recovery index: both expired leases and unleased exhausted rows retain
  `(available_after, occurred_at, id)` order. Real migrated-database plans with
  50,000 Pending rows distinguish the predecessor's scan from indexed recovery;
  admission exclusions, ordering and down/reapply are asserted.
- A failed durability probe after uncertain terminal COMMIT preserves both
  exceptions, in COMMIT/probe order. The real response-dropping proxy exercises
  that failure and independently verifies one durable terminal/audit pair and
  no duplicate on retry. The unused persisted shutdown outcome was removed;
  shutdown continues to leave ownership for expiry recovery.
- The launcher exposes only the exact fixed local database diagnostic, with
  secret-bearing prefix/unknown-message controls. Absent configuration uses the
  same diagnostic. Root runtime-script formatting is restored. The audit marker
  count, transport-owned inbox wording, historical batch discussion, catalogue
  proof kind, glossary and later-phase navigation now match implementation.
- The generic iteration diagnostic no longer mislabels every exception as a
  storage failure. Normal dispatch avoids its unused registry lookup. Contention
  warms both independent hosts before its live-lease deadline. Shutdown advances
  exactly one controlled deadline with four retained callbacks; an isolated
  second-budget mutant fails, and the rebuilt clean control passes.

The remaining report suggestions were checked rather than adopted as new policy:

| Concern | Disposition |
|---|---|
| Final `44089707` CI remains outstanding | Already resolved: all seven required jobs pass in [run 38309098697](https://github.com/HodeTech/LearnStack/actions/runs/38309098697). The earlier local receipt is dated evidence, not live state; this correction needs its own head verification |
| Failure metric precedes a successful fence; gauges may straddle a refresh | Metrics observe attempts, not durable transitions. Every gauge reads an immutable snapshot; no scrape-wide atomicity is promised. The phase criterion below now names that exact invariant |
| Disabled test worker leaves outbox health unhealthy | Intentional and now documented in factory remarks. The private check reports the absent worker; static `/healthz` remains liveness-only |
| Invalid options are reported as credential failures | Not on the shipped DI path: the store validates the shared options before worker construction |
| Alternate worker registration, duplicate short type labels, permissive terminal-request kind | No current production violation: composition census pins the sole worker; the registry has one production contract; the worker requests terminal state only from Normal claims. Recovery is deliberately used by a store fixture |
| Witness acquisition is not compared with audit metadata | SQL fences the actual token and returns it into the transaction-bound witness. The audit draft has no acquisition field; adding one is not required by ADR-0046 |
| Invalid causal metadata changes the original correlation | Matches ADR-0046: invalid correlation/causation/actor uses a validated attempt trace and omits invalid causal values |
| Role guard should reject other principals inheriting dispatcher rights | The guard validates the dispatcher's effective role and escalating memberships. Reverse grant auditing is outside that connection contract; changing it would not sandbox compromised privileged SQL |
| Ambient local credentials, migration-child environment, quoted passwords | Ambient overrides are explicit existing loader policy and tested. The administrative migration child is distinct from the confined web/Seeder children. Single/double-quote parsing is the declared closed local grammar, with existing controls; no provider mismatch was demonstrated |
| Audit seam needs duplicate tests in another file; sub-microsecond and census optimization suggestions | The real transition tests already exercise the fifth seam and rollback substitutions. Quantization has explicit envelope controls. Duplicate tests or census caching do not repair a demonstrated contract violation |
| ADR navigation date, test placement and optional trace descriptor | Historical ADR navigation is preserved; local-loader tests sit with that loader. Standard 09 and ADR-0046 allow optional trace data without requiring its persistence |

Local validation passes 1,845 unit, 286 architecture, four contract and 175
Docker-free integration cases, plus 97 focused Docker cases (54 store, 27
migration, 16 worker), with zero failures/skips. Guarded frontend passes 1,616
cases (1,556 web, 60 SDK), with zero failures/skips/todos; lint/typecheck pass.
Release build reports zero warnings/errors; format, seven ADR-status script
controls, 2,587 relative links and 491 fragments pass. Planner predecessor and
isolated second-budget mutant controls fail; clean counterparts pass. Both fresh
independent correction review
rounds and complete correction-head CI remain pending in this local receipt;
the PR description owns live head/check evidence.

#### External correction review round 1

Fresh GPT-6-astra xhigh store/security and GPT-6.1-sol xhigh proof/corpus
reviewers approve `44089707..13cd632` without introduced findings. Independent
verification passes seven focused PostgreSQL cases and 99 lifecycle cases.
The PostgreSQL rerun uses the existing Release binaries after a whitespace-only
source correction; a fresh committed-source build and CI remain separate checks.

The corpus reviewer finds a pre-existing adjacent source-comment contradiction:
`IIntegrationEventHandler` still requires handler-owned per-module inboxes.
Its remarks now name ADR-0046's transport-owned shared inbox and P02b-3 boundary.
The adjacent `IIntegrationEvent` remarks also stop promising unconditional
partition ordering or describing the implemented contract guard as Registered.
These are documentation corrections, with no interface or runtime change.

#### External correction review round 2 and local closeout

Fresh GPT-6-astra xhigh runtime/security and GPT-6.1-sol xhigh proof/corpus
reviewers independently approve `44089707..41ac2924`, with zero findings across
all 29 changed files. Both read the complete correction and the first round's
source-comment fix. The runtime reviewer independently inspects the completed
TRX counters; neither repeats the root's complete suites or mutation experiment.
Both correction review rounds are complete. No Accepted ADR text, grant or
consumer-delivery contract changes in this remediation.

Complete local backend verification passes 3,396 cases: 1,845 unit, 286
architecture, four contract, 175 Docker-free integration and 1,086 Docker
integration, with zero failures/skips. The 97 focused Docker cases above are a
subset, not additional cases. Guarded frontend passes 1,616 cases (1,556 web,
60 SDK), with zero failures/skips/todos; lint/typecheck pass. A fresh Release
build of `41ac2924` reports zero warnings/errors after the source-comment and
whitespace corrections. Five corpus controls, seven ADR-status script controls,
2,693 relative links and 500 fragments pass; historical P02b-0/P02b-1 records
and Accepted ADR text remain preserved.

Correction-head PR CI and its real demo artifacts still require independent
verification after publication. This local receipt does not reuse the earlier
green head as evidence; the PR description owns live head/check evidence.

### P02b-2 review of 3531a2eb (2026-10-13)

The next external report was checked against `3531a2eb`, not adopted as an
instruction source. That baseline's seven required jobs and complete demo
artifacts pass in [run 38318773974](https://github.com/HodeTech/LearnStack/actions/runs/38318773974).
This follow-up requires its own review and CI evidence. No Accepted ADR body,
migration, grant, consumer-delivery contract or dependency changes here.

- The earlier reverse-membership disposition is reconsidered using a real
  direct/transitive `SET ROLE` control. The app guard already rejects elevated
  memberships on new app connections; an administrator's later grant can still
  elevate an existing app session. The dispatcher now detects incoming grants
  on its next physical connection, enforcing the existing four-role boundary.
  Superusers are excluded from that predicate because PostgreSQL implicitly
  treats them as members of every role. This is grant-drift detection, not
  continuous revocation. Four sync/async direct/transitive cases prove actual
  escalation, refusal and healthy reconnection after restoration. An isolated
  PostgreSQL comparison distinguishes the predecessor and corrected predicates.
- Credential census now rejects explicit configuration indexers with a
  same-body `ConnectionStrings:` key/prefix, including compiled concatenation,
  interpolation, formatting and static-readonly names. Unrelated configuration,
  dictionary indexers and diagnostic text remain clean. This bounded check
  does not claim arbitrary cross-method key dataflow. A separate planted control
  closes `Assembly.CreateInstance`; `Activator.CreateInstanceFrom` was already
  covered. Both omissions were reproduced against the predecessor detector.
- The constraint/RLS statement, shared-inbox and fifth-writer statuses, settled
  ordering pointers and catalogue proof references now match current code.
  Skills use a shipped public assembly marker in explicitly abridged examples.
  The architecture method count is 194; Implemented rule count remains 192.
- Publication tests now cover successful/throwing completion, cleanup and
  repeated/late cancellation. Wrong-kind store calls must leave the actual row
  and audit unchanged. Test shutdown and publish timeouts use distinct values.
  A source comment explains exact Npgsql microsecond truncation; the local API
  environment comment names all three forwarded credentials.

The remaining claims were checked against their actual scope:

| Concern | Disposition |
|---|---|
| Startup options failure is reported as a credential error | Not on the shipped DI path: `OutboxDispatchStore` validates the same options before worker construction or source resolution |
| Broaden COMMIT catch to cure sticky terminal health | No demonstrated shipped exception escapes the existing provider/network/cancellation set. The dispatcher configures no custom driver logger. Failed reconciliation remains an unknown outcome even with catch-all; conservative health clears after successful terminalization |
| Non-admitted Normal claim hot-loops | Immutable registry and SQL exclude Pending identities before acquisition; unknown/retired/invalid rows already request terminalization. No current path reaches that invariant arm |
| Reject every unmapped JSON member | ADR-0046 explicitly uses the frozen payload options and compares declared facts; producer contracts enforce identifier-only fields. Unknown fields are not passed to the handler. A new stored-wire rejection policy is not inferred from this suggestion |
| Producer witness kind/subscription should anticipate subscriber minting | The current closed minting census admits only the producer store. P02b-3 owns a distinct subscriber capability and its identity controls; no subscriber factory is introduced early |
| Legacy-row diagnostics, blocking migration and attempt constants | Invalid facts deliberately refuse migration. Existing migrations remain immutable; checks/index creation need an owner maintenance window, with no online-migration claim. Tests pin six claims and the full backoff/DDL state; copying a new constant into historical migrations would not eliminate drift |
| UTF-8 SQL literals escape the literal census | Npgsql's command entrypoints accept strings. Decoding bytes is dynamic construction/dataflow outside this explicitly bounded literal census; raw byte data alone is not an executable SQL call |
| Platform credential rule is no longer independently sufficient | The whole-production credential/factory/consumer census independently pins both exact roots and their distinct sources. Repeating it inside the companion source rule adds no uncovered current boundary |
| COMMIT/probe stack assertions are redundant | Both failures are `NpgsqlException`; the stage-specific stack assertions distinguish their order. They supplement the physical outcome/no-duplicate proof and currently pass with the pinned driver |
| Ambient public-api overrides and seed validation should cover four credentials | Explicit ambient API overrides are an existing tested local-loader policy. Standalone seed consumes only Default and Migration; other ambient credentials never reach its child. Shared demo validates all four before startup |
| Forward proxy/cache/home environment into Seeder | The narrow child allowlist deliberately excludes ambient credentials/diagnostics; proxy values can themselves contain credentials. No failure on the documented restored-dependency path was demonstrated; the API's separate allowlist intentionally excludes `DOTNET_CLI_HOME` |
| Print the failed connection-string suffix | The fixed diagnostic is now safely visible. More detailed refusal messages are optional; values remain excluded and no further operational regression was reproduced |
| Ingress env tests depend on file order | Each loader test writes its own selected file before reading it; they are sequential, not concurrent. The later configuration test uses an explicit object, not that file |
| Duplicated construction-only factory helper | The remarks explain the intentionally absent worker/unhealthy private check. Sharing helpers across separate test assemblies would add a dependency without correcting behaviour |
| Skill index claims handler-owned inbox | Not reproduced: its generic inventory names an inbox guard but assigns no handler ownership; detailed skills now identify the shipped/P02b-3 boundary |
| Persist traceparent or measure recovery/terminal as publication duration | Trace persistence is optional. The duration histogram measures publication; recovery and terminal transitions do not publish and retain their own spans/outcomes |
| Retained partition also delays terminal acquisition | Deliberate local ownership: no further acquisition for that partition until callback and scope cleanup actually finish, including terminal work; ADR-0046's four-overrun saturation applies |
| Assembly-qualified identities, closed generics and invalid disposition label | Exact assembly-qualified identity plus explicit compatible aliases is Accepted. A declared closed generic is a concrete known type, not arbitrary loading. Invalid-envelope reason and the fixed `invalid` label remain distinct from unknown-type reason |
| Legacy non-NULL metadata should be backfilled | ADR-0057 requires new producer metadata to be SQL NULL; ADR-0046 deliberately terminalizes invalid legacy envelopes rather than silently rewriting their facts |
| Alternate health-registration dedupe and audit trigger | The sole production composition is pinned. A new trigger/privilege design is Phase 11 work, not an unreviewed replacement for the Accepted row-bound terminal seam |
| Squash migrations or mechanically gate published fragment counts | Existing migrations/history are preserved. Link/fragment totals are explicitly local checks, not CI enforcement claims; the prior and current source scans remain separate evidence |

Local Release build reports zero warnings/errors; format verification passes.
Unit passes 1,847, architecture 287, contract four and Docker-free integration
175 cases, all with zero failures/skips. Twenty role controls and six focused
claim-kind/shutdown cases pass. The new compiled-probe helper's initial deferred
Cecil-body failure was corrected before the successful full architecture rerun;
the guard expectation was preserved. Seven ADR-status controls, 2,711 relative
links and 505 fragments pass. Historical delivery records and Accepted ADR text
remain unchanged. Complete Docker validation, both fresh independent review
rounds and correction-head CI remain pending in this initial local receipt;
the PR description owns final head/check evidence.

#### Review of 3531a2eb: verification closeout

Both fresh independent review rounds approve `3531a2eb..a5b1e723` with zero
findings across all 17 changed files. Each round uses a fresh GPT-6-astra xhigh
security/runtime reviewer and GPT-6.1-sol xhigh proof/corpus reviewer. All review
lenses were covered. Reviewers inspect source and TRX evidence without repeating
the root's full suites; the second proof reviewer independently confirms 194
architecture methods, 192 Implemented rules and all 13 explicit method links.
No further runtime correction was required after the implementation commit.

Complete local backend verification passes 3,408 cases: 1,847 unit, 287
architecture, four contract, 175 Docker-free integration and 1,095 Docker
integration, with zero failures/skips. The 20 role and six focused claim-kind/
shutdown cases are subsets of Docker integration, not additional cases. Release
build, format, seven ADR-status controls and the 2,711-link/505-fragment local
check pass. Historical delivery records and Accepted ADR text remain preserved.
No frontend source or dependency changed in this correction; the next real CI
run re-verifies frontend and demo artifacts along with the corrected backend.

Both correction rounds and local validation are complete. Exact correction-head
CI remains pending in this local receipt; the PR description owns live checks
and artifact-backed evidence after publication.

#### Snapshot interval correction (2026-10-13)

The dispatch loop no longer forces a full queue snapshot after every successful
claim. It checks `SnapshotInterval` after completed iterations, while `didWork`
still controls idle polling. A controlled monotonic-clock integration case
dispatches three real rows: the initial snapshot survives successful work and
the last tick before the interval; the exact boundary refreshes it after the
next completed dispatch. Release build reports zero warnings/errors; format,
1,847 unit, 287 architecture and all 17 worker integration cases pass, with zero
failures/skips. Two fresh sequential independent reviews of `3620b421..3baf3a22`
approve with no findings. In an isolated tracked-source copy, reverting only the
production call to forced refresh makes the new case fail its retained-snapshot
assertion (one executed, one failed); the corrected case passes. New-head CI
remains pending in this local receipt; the PR description owns live checks and
artifact-backed evidence after publication.

### P02b-2 merge and closeout (2026-10-13)

The maintainer merged [PR #30](https://github.com/HodeTech/LearnStack/pull/30) at
2026-10-13 07:43:06 UTC. GitHub records final PR source
`4b4e3e40cf9147262934652c4eb9621258356f4c` and squash merge
`6a277770e46a7f4ea3574d9d6891d1802cb8fa90`, whose parent is previous main
`b1f6f21cab556ee7ba497dfc0d64e51b8cf1dad8`. Source and squash merge have the
identical tree `c3d02e3368365a108cb8321ccb3fcbc9cec2e575`. Development incorporates
the main merge without switching branches or rewriting published history.

All seven required checks pass in the
[final PR run](https://github.com/HodeTech/LearnStack/actions/runs/38358520310)
for that source; CodeRabbit also reports success. The tested synthetic merge is
`d347a60a36fc0001656c3d95d7551bc35d818b91`. Verified artifacts record:

- **3,409 backend passes**, zero failures/skips: 1,847 unit, 287 architecture,
  four contract, 175 Docker-free integration and 1,096 Docker integration cases.
  The snapshot-interval regression passes on that exact source.
- **1,616 guarded frontend passes**: 1,556 web and 60 SDK, zero
  failures/skips/todos; frontend lint, typecheck and builds also pass.
- Exactly 27 matching Lighthouse JSON/HTML report pairs across nine URLs, nine
  screenshots and matching source/checkout identities and report digests.
  Automated accessibility is 1 throughout, CLS is 0 and normalized initial JS
  gzip is 106,357–106,552 bytes. Proof finishes in 631.094 seconds and owned
  cleanup passes; timing medians remain advisory.
- OpenAPI base/head specifications are byte-identical, with SHA-256
  `69333252a95d4179e319512e5f45db2781d12eb003d405a509fa047d64e8e012`.

These are final PR-head results, not a claim about a new post-merge or local
full-stack run. **P02b-2 is closed.** All four steps and verified review fixes
are delivered, including recovery-index alignment, credential confinement,
terminal COMMIT reconciliation and interval-bound observation. Two independent
review rounds per step and correction bundle remain recorded above. Earlier
dated receipts retain their pre-merge wording; all Accepted ADR files remain
unchanged by this closeout. Phase 02b remains in progress.

**Next: P02b-3 decision pass.** Reverify the Accepted G1/G6/G7 subscriber
contract and G19 handoff against the merged code, then plan delivery and its
proofs before implementation. P02b-3 owns durable acceptance, shared inbox and
independent delivery ledger, subscriber retries/terminal MUST audit, targeted
internal replay, both real Customization defaults subscriptions and semantic
key/version Seeder handoff. The effects remain `card@1` content type and
`plain@1` taxonomy, preserving existing authored/lifecycle state. Required
production subscriptions remain Pending until that boundary is implemented;
producer dispatch does not claim durable consumer effects.

Background jobs, API authentication, realm reconciliation, BFF sessions and
phase-exit enforcement remain P02b-4–8 in the
[packet table](#packets-and-decision-gates). Operator recovery permissions/routes
remain Phase 03; broker adapters and production readiness remain Phase 11 on
their recorded triggers. This closeout starts no later packet and makes no new
architectural decision.

**Documentation-only closeout validation.** Five local Release corpus controls
and seven ADR lifecycle/workflow controls pass, zero failures/skips. The 14
changed Markdown files resolve 1,577 relative links and 333 fragments without
errors; added prose wrapping and whitespace checks pass. All prior P02b
delivery/review receipts and every ADR file are byte-preserved. Runtime suites
are not rerun locally for this documentation-only change; the final PR artifacts
above supply their evidence.

### P02b-3 decision package (2026-10-13)

**Accepted — 2026-10-13, @cemil.** The maintainer approves the complete package
and six implementation steps, each with commit and two independent review rounds.
Acceptance precedes runtime implementation; it claims no delivered subscriber.
The audit baseline is `development` at
`652004290c9696e65e68817f223839d4a0f02137`, after PR #30's verified merge.
Three independent read-only audits covered the accepted contracts, storage/
privilege/owner lifecycle, and Customization/Seeder convergence. Their findings
were checked against current source; they report inspection, not passing tests.

[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md#subscriber-acceptance-and-inbox-ownership)
already accepts G1/G6/G7's shared transport-owned inbox, stable subscriptions,
durable acceptance, independent retries, atomic terminal audit and targeted
internal replay. [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md#first-useful-production-flow)
accepts both create-only default effects and the Seeder handoff. This package
concretizes the storage, runtime and enforcement details those records assign to
P02b-3. It does not reopen their decisions or authorize jobs/authentication.

#### Document and code review

The review follows the roadmap entry reading, then ADR-0003/0006/0010/0017/0023/
0033/0036/0038/0040/0042/0043/0044/0046/0057, applicable Backend, Database,
Testing, Observability, Security, Documentation, Audit, Infrastructure and
Architecture-test standards, architecture 03/09/10/15/29/31/32, the Tenancy/
Customization/Audit specs and matrices, and event/seed/migration/test skills.
The actual producer, bus, registry, transaction/audit coordinators, terminal
witness/store, Customization writers/readers and Seeder are the implementation
premises. Acceptance history remains the authority for any proposed ADR erratum.

| Verified premise | Required P02b-3 action |
|---|---|
| `InProcessEventBus` invokes and constructs handlers immediately; it owns no durable acceptance or inbox | Replace that publication outcome with durable acceptance; execute only through the independent delivery owner |
| `OutboxEventRegistry` owns stable IDs/readiness; the handler registry carries only concrete handler metadata | Preserve one manifest identity source; bind each Active ID to exactly its declared contract and handler |
| Common owner completion drains events, writes MUST audit and commits without a delivery completion participant | Add a closed delivery-specific final fence/gate while preserving request ownership and all existing announcement methods |
| `EventTerminalTransition` and the fifth audit seam accept only producer terminalization | Extend the restricted actual-row witness and operation census to the subscriber terminal writer |
| Customization's current readers exclude deleted rows and discover by ID or Active key | Add owner-local, tenant-protected concept history across every revision/status, including soft deletion |
| Register uniqueness protects one revision, not concurrent initialization versus another revision | Serialize initialization and concept registration on the same transaction-owned semantic key |
| Seeder requires fixed built-in IDs; its counts assume separate register/publish acts | Resolve built-ins semantically, preserve warm IDs, share initialization, and update inventory/fixtures without weakening authored-ID checks |
| Direct delivery production has no incoming-event causation source | Carry a trusted delivery entry through nested producer frames; keep tenant/org authority in the announcement |
| Existing aggregate/audit request censuses omit integration handlers | Add nonempty production-consumer coverage and planted violations before marking consumer rules Implemented |
| Existing bus logs raw exceptions and adds exception details to spans | Replace transport-owned diagnostics with bounded codes before enabling production subscriptions |

These gaps are expected P02b-3 work; they are not retroactive P02b-2 failures.
Required production manifests stay Pending until acceptance, execution and Seeder
handoff are ready together. Test-owned Active contracts exercise earlier steps.

#### Accepted storage and authority details

1. **Two shared infrastructure tables.** `event_deliveries` retains one copy of
   the validated producer envelope per `(tenant_id, subscription_id, event_id)`.
   `inbox_messages` holds the completion identity for that same tuple, with no
   surrogate UUID. Neither depends on a live producer row. A producer purge
   cannot remove a pending delivery's payload. No third dead-letter store/topic
   or arbitrary metadata is added. Retained copies and their retention dependency
   enter the P02b-8 inventory; actual purging remains Phase 11.
2. **RLS and classes.** The ledger is tenant-owned and organization-scoped because
   its envelope carries organization metadata and payload. Use the canonical
   tenant-and-organization policy, ENABLE and FORCE, with explicit WITH CHECK.
   The inbox is tenant-wide identity machinery with no payload/org authority.
   Both use explicit tenant predicates in application SQL. No foreign key to
   `outbox_messages` or cascade from business tables controls retention. The
   executor restores the stored organization before reading the envelope;
   an unmarked event never widens a present organization to null.
3. **Closed grant budget.** `learnstack_app` receives RLS-protected acceptance
   INSERT/SELECT and only the ledger completion-column UPDATE. Inbox claim is an
   INSERT of an immutable completion marker inside the uncommitted business
   transaction; uniqueness serializes duplicates before construction. Only owner
   success makes that marker durable; rollback removes it. Its timestamp records
   the claim; ledger completion records business completion. Successful app-role
   completion retains token/expiry as inert provenance because it cannot UPDATE
   those fields. Completed rows are excluded from acquisition, recovery,
   terminalization and replay. The bypass scheduler
   receives column SELECT for tenant/subscription/event identity, organization,
   declared type/topic/partition, occurrence, attempts, availability, lease,
   completion, terminal/replay state and bounded diagnostics; UPDATE covers only
   scheduling/status columns. It receives no payload or causal-envelope SELECT,
   no ledger INSERT/DELETE and no inbox privilege. Application INSERT grants name
   acceptance columns only, excluding caller-supplied scheduling/terminal state.
   The existing Audit-chain
   INSERT-only grant is reused; no fifth role or business-table grant is added.
4. **Canonical detail before code.** Standards 05 owns complete DDL, constraints,
   exact column grants and matching pending/recovery/terminal indexes. A new
   Platform-chain migration implements it; historical migrations stay intact.
   Identity, subscription grammar, nonnegative attempts, lease pairing,
   mutually exclusive completion/terminal state and the 256-byte error cap have
   CHECKs. Completion and terminal UPDATE predicates match the actual grants;
   tests run them as the real roles, not merely inspect grant text. No P3 role
   purges these stores; Phase 11 must decide the minimal retention grants and
   ordering before installing its purger.
5. **Accepted envelope is immutable.** Publication validates the envelope and
   Active manifest, restores its scope, and inserts every required delivery in
   one app-role acceptance transaction without constructing business handlers.
   An existing identical identity/envelope is idempotent; a conflicting envelope
   refuses acceptance without overwriting the first copy. Canonical serialized
   data, including organization and causal metadata, determines equality, not
   raw `jsonb` whitespace. An existing accepted subset can be completed safely.
   An uncertain acceptance COMMIT leaves the producer retryable; subscriber
   execution failures never flow back into the producer's retry budget.

Scoped application completion is a constrained call path, not a claim that column
grants sandbox arbitrary compromised app SQL. Likewise, the terminal audit's
tenant binding is the row witness, not BYPASSRLS GUCs. No tenant-status lookup is
added as an asynchronous hold/drop policy; Phase 02c retains that decision.

#### Accepted execution, retries and replay

Each attempt owns a fresh app-role scope. Announce the validated routing tenant/
organization before resolving a module context, read and revalidate the retained
envelope by its claimed identity/token under RLS, then claim the inbox before
handler construction. Completed duplicates skip construction. Effective actor is
SystemActor; original causal actor and incoming EventId remain trusted producer
metadata. Ordinary execution refuses privileged subscriptions. Phase 03's
erasure execution contract remains separate.

The outer audit coordinator classifies the declared handler before construction;
nested commands retain their own classified intents. No module writes audit SQL.
The common transaction owner drains domain events, writes pending MUST audit,
performs the delivery completion UPDATE with tuple/token/unexpired database lease,
and atomically admits physical COMMIT against a sticky completion gate. A failed
fence or a gate closed by cancellation poisons and rolls back all work. The gate
arbitrates Open -> Closed versus Open -> Committing; cancellation after commit
admission is an unknown/Indeterminate outcome, not guaranteed rollback. Durable
inbox/ledger state decides recovery, including an uncertain sixth-attempt COMMIT.
This adds no IUnitOfWork session setter, retargeting or independent business commit.

| Policy | Accepted initial bound |
|---|---|
| Host and acquisition | API-owned subscriber worker; one just-in-time normal claim; reuse the confined keyed dispatcher source |
| Normal claims | Six per subscription/event, including claims whose execution outcome is unknown |
| Five retry delays | 1 s, 5 s, 30 s, 5 min, 1 h, using database availability time |
| SQL / terminal transaction | 5 s |
| Complete execution wait | 10 s, including construction, handler and owned cleanup; timeout closes the commit gate |
| Lease | 30 s, with validated execution/transition margin; no renewal promise |
| Idle poll / snapshot interval | 200 ms / 5 s; successful work does not force a scan |
| Progress / stale health | 45 s |
| Cancellation-ignoring work | At most four retained tasks; block their local partition, unhealthy while retained, stop acquisition at capacity |
| Shutdown | One total 30 s wait; live task resources remain owned until actual completion or process exit |

The worker retains and observes cancellation-ignoring tasks with their live scope;
it does not dispose a running callback or promise managed-code termination.
This is one additional in-process subscriber worker in the existing API host;
it neither creates a new host nor changes the producer's existing bounds. Its
four-task and 30 s bounds apply to this worker, not an added whole-host shutdown
guarantee. All stop waits also observe the host's shared shutdown cancellation.
If scope disposal faults or times out after acknowledged business COMMIT, the
completed ledger/inbox remain definitive; cleanup cannot undo or rerun that
effect. Unknown completion reconciles durable state. Overrunning cleanup remains
task-owned and consumes capacity until actual disposal finishes.
Recovery replaces expired ownership before transitions and spends no additional
attempt. Ordinary failed/unknown outcomes use independent backoff. Expired claim
six requests terminalization without a seventh invocation. Requests for terminal
state are never normal execution candidates. Audit refusal retries terminalization
alone. Closed reasons distinguish invalid envelope/subscription from exhaustion;
descriptors expose bounded stage/category codes, never raw exception text.

Subscriber terminalization uses the same fifth audit seam and a subscriber witness
minted only from its successful fenced UPDATE result. It binds actual tuple,
organization, token, attempts, reason and live connection/transaction. The
Tenancy catalogue/matrix registers `platform.delivery.dead_letter` as off-path
MUST/SecurityEvent when this writer ships. Subscriber terminal audit uses the
validated attempt trace and bounded event/subscription/reason/attempt metadata;
it does not grant the bypass scheduler causal-envelope or payload access merely
to reuse the producer witness. Business execution retains original causality.

Internal replay takes exactly one declared tenant/subscription/event tuple. It
admits an Active ordinary subscription and completed terminal ledger row only;
live claims, pending terminalization, unknown/retired/Pending subscriptions and
completed business deliveries are refused. The fenced reset clears that row's
terminal request/timestamp, attempts and bounded error, makes it due and invalidates
old ownership. It never deletes/resets an inbox or republishes a broadcast.
The scheduler later invokes only that subscription. A completed inbox still
short-circuits business construction if an earlier uncertain attempt committed.
No replay HTTP route, permission grant or operator producer reset ships here.

Standards 10 owns subscriber metric names/dimensions and the registered, unmapped
health check. Keep no tenant/event/partition/trace labels; subscription/event-type
labels are finite declared-registry values only. Snapshot gauges perform no SQL;
refresh failure preserves the prior snapshot and reports stale/unhealthy state.
Producer and subscriber attempt/failure/terminal/dedup signals remain distinct.
The API's `/healthz` and anonymous public contract remain unchanged.

#### Accepted defaults and Seeder handoff

The two stable identities remain
`customization.initialize-content-type-default.v1` (`card@1` content type) and
`customization.initialize-taxonomy-default.v1` (`plain@1` level taxonomy).
Each effect owns exactly one root. New roots are validated, created and made
Active in one transaction/save, with a Customization-owned UUIDv7, one generation
increment and one classified initialization MUST audit. Shared owner-local
initialization commands let the Seeder use the same logic without a worker,
dispatcher credential, raw SQL or new tenant-context setter. Event handlers
coordinate only their own effect through those commands; they do not combine roots.

A transaction-owned semantic concept lock serializes each initializer with the
ordinary registration writers for `(tenant, aggregate-kind, key)`. Under that
lock, any revision in Draft/Active/Deprecated or soft-deleted history means
preserve, not initialize. A successor-only concept also counts as touched.
Whichever writer commits first defines whether the concept was untouched;
initialization never overwrites, republishes, revives or fills a missing revision
of an already touched concept. Parameterized owner-local history reads retain
explicit tenant predicates and RLS; no query-filter bypass is introduced.
Same-key locks do not create a cross-tenant authority path.

The initialization operation classifies preserved/no-op attempts explicitly;
it changes no root or generation. A new delivery may record an initialization
attempt without entity changes; a completed duplicate constructs no handler and
creates no second business audit. Seeder pre-verifies completed built-ins before
sending an initializer, so a completed rerun remains write/audit-neutral.
Fresh inventory counts reflect one initialization audit/bump per built-in,
not the old two register/publish acts. That is the uncontended creation count;
separately classified preserved attempts after a seed/delivery race may add an
audit without a generation bump. Tests distinguish creation from no-op attempts,
and count the actual deduplicated delivery identities rather than asserting that
every asynchronous interleaving has the Seeder-only audit total. Historical
receipts are not recounted.

Built-in Seeder verification discovers semantic key/version, then validates exact
Active state and built-in shape while retaining the stored ID. Existing matching
non-demo IDs pass. Preserved Draft, deprecated, deleted, successor-only or
mismatched definitions remain untouched; if they cannot satisfy the declared
demo inventory, seeding refuses with a bounded mismatch instead of claiming
completion. Tenant-authored definitions/courses/lessons retain exact declared
IDs and exact pins. Consumer and fresh Seeder races converge in both winning
orders; an unexpected database failure is not a blind retry/overwrite.

#### Decision vehicles and documentation actions

No new architecture decision is proposed: these details fit ADR-0046/0057's
explicitly delegated P02b-3 boundary. If implementation disproves that fit, stop
and prepare a new decision for maintainer review before changing the contract.
Acceptance of this package authorizes the following already-required changes,
with original Accepted bodies/metadata and historical receipts preserved:

- **ADR-0023:** verify the inbox minting-list wording at its acceptance commit;
  add the ADR-0041 inline erratum and its own dated amendment, because inbox
  identity reuses the producer EventId rather than minting a surrogate. This
  corrects the listed table, not the UUIDv7 decision.
- **ADR-0044:** append the additive capture-exclusion clarification for the
  shared inbox and delivery ledger; add subscriber fifth-seam/operation navigation
  already authorized by ADR-0046. Explicit business intents remain audited.
- **ADR-0046/0038/0057:** dated delivery/navigation notes when their P3 obligations
  actually ship; no premature completion claim or replacement of earlier bodies.
- **Mutable carriers:** canonical Inbox/ledger DDL and grant matrix in Standards
  05; lifecycle/diagnostics/audit rules in 02/09/10/11/18/20; canonical tests in 21;
  architecture 03/09/10/15/29/31/32; glossary; module specs/matrices; event/seed/test
  skills; Seeder inventory/fixtures and `BuiltInCustomizations` creator remarks.
  Reconcile only touched claims; do not duplicate an ADR or RLS template.
- **Hub pointer:** verify the sibling repository's per-module inbox dependency
  and coordinate its documentation pointer before P3 closeout, as ADR-0046 requires.
  The current sibling `docs/roadmap/README.md` P02c-3 row still names per-module
  inbox tables; replace only that dependency with a link to ADR-0046's shared
  transport-owned inbox contract when coordinating the documentation change.
  No Hub runtime, HTTPS contract or branch change is authorized by this package.

At acceptance, Standards 21 registers the following additional canonical rules
as Registered, with their behavioural/structural owners; no passing enforcement
is claimed before code:

- `Integration_Event_Publication_Is_Durable_Acceptance` — real-role acceptance,
  immutable duplicate/collision handling, partial/unknown COMMIT and no construction.
- `Delivery_Retries_And_Replay_Are_Subscription_Scoped` — independent budgets,
  terminalization, completion gates, final-claim recovery and targeted replay.
- `Delivery_Terminal_Audit_Identity_Is_Row_Bound` — restricted subscriber witness
  census and atomic matching/mismatched identity/transaction proofs.
- `Delivery_Execution_And_Observation_Are_Bounded` — actual hosted execution,
  retained resources, shutdown, closed diagnostics and snapshot/health emitters.
- `Customization_Default_Initialization_Preserves_Touched_Concepts` — one-root
  useful creation, all lifecycle preservation and serialized registration races.
- `Seeder_BuiltIns_Use_Semantic_Identity` — generated/warm IDs, exact shape and
  authored IDs, no-worker operation, rerun neutrality and real delivery races.

Extend the existing `Integration_Event_Handlers_Use_InboxGuard` and
`Integration_Event_Handler_Restores_Tenant_Context` obligations rather than
inventing alternate names for their claim/owner/scope assertions. Extend the
existing credential confinement census only for the named subscriber scheduler/
store; business handlers and application execution never receive bypass access.

#### Six accepted implementation steps

Each step commits on `development`, then completes two fresh independent review
rounds. Verify findings against current code, fix valid findings, validate and
commit fixes before the next round/step. A clean review needs no empty commit.
Continue automatically unless a real blocker or new maintainer decision arises.

1. **Accepted detail, schema and privileges.** Record approval before runtime;
   register canonical proof obligations, reconcile the named ADR instruments,
   implement Platform migration/table classes/RLS/grants/indexes and test
   forward/down/reapply, actual role permissions and tenant/org isolation.
   Production subscriptions remain Pending.
2. **Durable acceptance and inbox primitives.** Replace immediate broadcast
   execution with validated transactional acceptance; implement immutable envelope
   collision/duplicate semantics and transport-owned inbox claims. Prove partial
   and uncertain acceptance, independent identities, concurrent duplicates and
   construction-free publication with real PostgreSQL.
3. **Business owner and completion gate.** Implement fresh application scopes,
   restoration before resolution, classification, incoming-event causation,
   common owner completion and sticky commit arbitration. Prove business/inbox/
   outbox/MUST-audit/ledger atomicity, canceled/expired/stale attempts, no-listener
   metadata, owner cleanup and unknown COMMIT without activating production defaults.
4. **Subscriber scheduling, terminalization and replay.** Implement independent
   leases/attempts/recovery, witnessed terminal MUST audit, targeted reset, bounded
   task ownership, minimal diagnostics and snapshot health/metrics. Prove sibling
   independence, sixth-claim recovery, late completion, audit rollback, identity
   substitutions, safe shutdown and replay fences using the actual worker.
5. **Useful consumers and convergent seed.** Add the two one-root initializers,
   concept serialization/history reads and semantic verification; migrate Seeder
   and fixtures, then activate both real subscriptions together. Prove untouched
   creation, every preservation lifecycle, warm IDs, strict authored pins, repeated
   seeding and concurrent fresh seed/delivery in both winning orders.
6. **Enforcement and PR closeout.** Complete production-handler/call-path censuses
   with planted clean/dirty controls, full Release build/format/backend regression,
   affected frontend/launcher tests and real demo/Lighthouse proof. Reconcile all
   current-state docs and accurate test counts; open the PR only after every step
   and both reviews finish. Verify all seven required CI checks at the exact head;
   never claim merge or delivery of P02b-4–8.

#### Decisive acceptance proofs

- Actual app-role tenant/org isolation and real bypass column grants: scheduler
  payload/inbox reads and app retry/reset writes fail; allowed acceptance and
  completion succeed. Completed inbox rows persist across replay and producer purge.
- Two same-module subscriptions and concurrent duplicates yield one business
  effect each. A duplicate waits for the first transaction; rollback permits a
  new claim. Handler construction failure is independent of acceptance success.
- Fenced ledger completion, business changes, produced events and MUST audit all
  roll back on a stale/expired token or closed gate. Kill the fence/gate in a
  controlled mutant to prove sensitivity, including timeout persistence failure
  followed by an ignored-token late callback.
- Real lost-COMMIT acknowledgement recovery, including the final subscriber
  claim, does not repeat committed business work or terminal audit. Cancellation
  after commit admission never claims certain rollback. Postcommit disposal fault
  and timeout controls retain durable completion, prevent effect replay and keep
  live cleanup resources safely owned.
- Subscriber terminal draft tenant/org/event/subscription/operation/transaction
  substitution rolls back both transition and audit; matching org-scoped work
  commits. Audit refusal never makes terminal-requested work executable again.
- Targeted replay changes one terminal row only; completed sibling/inbox rows
  and producer attempts stay unchanged. Stale pre-replay callbacks cannot complete.
- Nonempty production censuses cover every module layer and the actual handler,
  manifest, owner, inbox, terminal-witness and credential sites. Planted direct/
  interface/helper bypasses and clean controls distinguish enforcement from an
  empty or narrowed subject set. Extend one-root and audit-registration coverage
  to integration handlers; do not only count request handlers.
- Logs/spans/descriptors/artifacts reject planted payload, secret, raw type and
  exception values. Finite telemetry labels, prior-snapshot retention, exact
  refresh cadence and no SQL gauge callbacks have positive/negative controls.
- Runtime proof includes actual TenantCreated backlog activation, both defaults,
  ordinary app-role isolation and warm/fresh Seeder races. Test-only subscribers
  alone cannot close the packet's useful-flow requirement.

#### Decision-package reviews and validation

Two fresh, sequential, read-only reviews examined the proposed working-tree
package against baseline `65200429` and the current accepted contracts/code:

- **Round 1 — GPT-6-astra, high:** Approve, no Blocker/Major. Clarifications for
  postcommit disposal, inert completed-row lease provenance and creation/no-op
  audit counts were verified and incorporated before round 2.
- **Round 2 — GPT-6.1-sol, high:** Approve, no Blocker/Major/Minor. Independently
  checked the completion/grant model, defaults/Seeder policy and ADR-0023's
  acceptance-time natural inbox identity. It found no additional decision gap.

Root validation passes all five `CorpusConsistencyTests` and seven ADR-status
controls, with zero failures/skips. The changed phase document has 184 checked
relative links and 98 fragments, with no missing targets/anchors. Added prose
passes the 88-column check with documented link/table exceptions; whitespace is
clean. Every Accepted ADR and all earlier P02b delivery/review receipts remain
byte-preserved. This is documentation validation, not a P02b-3 runtime test run.
The package remains Proposed until explicit maintainer approval; that approval
and the registered obligations precede the first runtime implementation commit.

#### Maintainer acceptance (2026-10-13)

The maintainer's explicit answer, "Paketi onaylıyorum; implementasyona geç",
accepts this package after both decision-package reviews. Their preceding receipt
records the pre-approval state. The six steps and two-round loop are authorized
on development. G1/G6/G7/G19 keep their accepted architectural answers; concrete
storage, authority, execution/replay, default and Seeder details now have this
accepted vehicle. Catalogue additions remain Registered until their proofs ship.
The decision acceptance, ADR-0023 erratum and ADR-0044 capture clarification are
committed before the first P02b-3 runtime change.

### P02b-3 Step 1 — shared storage and privileges (2026-10-13)

**Complete; both independent review rounds complete.** Decision acceptance
`63363ac` precedes this runtime change. EF generated Platform migration
`20261013083903_add_event_delivery_and_inbox`; its raw infrastructure tables add
no EF entity or model snapshot change. Standards 05 owns the complete applied
DDL, table classes, constraints, total-order indexes and exact column grants.

The org-scoped ledger retains a subscription envelope independently of producer
retention. The tenant-wide inbox uses the producer event identity and subscription
without a surrogate; database guards refuse UPDATE, DELETE and TRUNCATE even as
the owner. No P3 role receives purge privileges. The app role accepts only immutable
envelope columns and completes only `processed_at`; the scheduler receives routing/
status columns and nine scheduling UPDATE columns, excluding payload, causal
metadata, inbox access and business completion. Completed rows retain inert lease
provenance and are excluded from every scheduler index. Real subscriptions remain
Pending; this step claims no acceptance or business-delivery runtime.

`EventDeliveryMigrationTests` exercises 73 real-PostgreSQL cases: permitted SQL,
exact grants and forbidden capabilities, tuple identity/ASCII grammar/state,
immutable guards with removed-trigger dirty controls, matching index/order plans,
producer-retention independence, canonical-document catalog parity and disposable
Down/reapply. The existing all-table fixture now seeds both stores for both tenants,
including distinct ledger organizations, and marks fixture ledger canaries complete.
The general tenant-isolation, foreign-write and grant sweeps include them.
Ledger-specific organization and scope-hatch proofs are recorded below.
Standalone Platform migration proofs install Tenancy's shared organization guard
first, matching the documented deployment sequence; historical migrations are intact.

Validation before the implementation commit: Release build with `CI=true` has
zero warnings/errors; 164 focused schema/migration integration cases and all 287
architecture cases pass, zero failures/skips. The seven ADR-status controls pass.
Format and whitespace validation pass; 1,180 relative links and 286 fragments
resolve without errors. Earlier receipts and Accepted ADR text remain intact.
Unit/frontend/runtime-worker suites are
not rerun for this schema-only step. Subsequent steps own acceptance, execution,
subscriber scheduling, useful defaults and full packet regression.

#### Step 1 review round 1

Fresh GPT-6-astra/high reviewed every file in `63363ac..ad57a1b` through the
standards and code-review lenses. It requested changes for one missing required
proof: existing cross-organization/scope-hatch behavior tests name other tables,
so they do not establish the new ledger's organization boundary. Root independently
confirmed this gap; the current migration predicates were not found defective.

The correction adds app-role ledger read, exact/null/mismatched acceptance and
fenced completion tests, with and without the tenant read hatch. Five live leased
canaries distinguish tenant-wide, own/sibling organization and foreign-tenant rows.
Own-scope controls remain writable. Disposable policy mutants prove a missing
organization read term leaks the sibling, a read hatch copied into WITH CHECK
admits forbidden acceptance, and removing only the restrictive UPDATE guard
changes zero matched rows into the remaining WITH CHECK's RLS refusal. The last
control does not claim a write bypasses both protections. No production policy or
grant changes are needed. The correction passes 125 focused ledger/organization
integration cases, zero failures/skips, with `CI=true` Release. Review round 2
follows the correction commit.

#### P02b-3 Step 1 review round 2 and closeout

Fresh GPT-6-sol/high independently reviewed all 13 files in `63363ac..fe6f9c7`,
including the correction rather than only its receipt. Verdict: Approve, no
Blocker/Major/Minor. It verified the real-role organization proofs and weakened
policy controls, exact grants, RLS, immutable tuple inbox, ordered indexes,
canonical DDL parity, reversal and documentation scope. It made no edits and
inspected the tests; it did not independently rerun the reported suites.

Root's correction run passes 125 ledger/organization integration cases, including
all 83 current `EventDeliveryMigrationTests`, zero failures/skips. Earlier Step 1
verification passed the 164-case schema/migration group and 287 architecture cases.
The correction changes tests and their record only; format, documentation and
whitespace checks pass. Implementation commits are `6b1efef` and `ad57a1b`;
`fe6f9c7` closes the verified round-one proof gap. Step 2 follows automatically
under the accepted package; production subscriptions remain Pending.

### P02b-3 Step 2 — durable acceptance and inbox primitives (2026-10-13)

**Implemented; both independent review rounds complete.** `InProcessEventBus` now
validates the concrete canonical contract and required Active manifest, restores
publication context before creating a fresh application scope, and commits all
subscription envelope copies in one announced transaction. It constructs no
business handler. Exact identity plus canonical immutable fields distinguishes
idempotent duplicates from conflicting or inaccessible organization copies.
Matching subsets complete; a later collision rolls back earlier new copies.
Uncertain acceptance COMMIT remains producer-retryable. Only explicit intentional
absence may acknowledge without deliveries; Pending/unknown/retired contracts
refuse rather than silently reach zero handlers.

`IInboxGuard`/`PostgresInboxGuard` claim only the tuple on the live, writable,
unpoisoned announced app-role transaction. PostgreSQL uniqueness waits for a
competing transaction: commit yields an existing identity; rollback permits a new
claim. The primitive owns neither connection nor commit. Business completion,
classification, independent execution and timeout gates remain Steps 3–4;
production default subscriptions remain Pending until Step 5's complete path.

`DurableEventAcceptanceTests` and `InboxGuardTests` exercise 36 PostgreSQL cases:
construction-free acceptance, immutable-field collisions and JSON key-order
equivalence, matching/conflicting subsets, independent publishers and forced
unique-key waits in both commit/rollback orders, uncertain COMMIT acknowledgement,
actual tenant/org scope, fresh owner transactions, cancellation and inbox admission.
The fixture uses the production AsyncLocal accessor; a forced blocked publication
shares its provider with another tenant's independent successful publication.
Their blocker observers own finite cancellation deadlines. Root corrected the
initial duplicate test's shared process serializer before claiming SQL concurrency.
The canonical durable-acceptance catalogue rule is Implemented; the larger handler/
inbox ownership and production census rules remain Registered.

The former 32 immediate-handler bus unit cases no longer describe publish success.
Eight publication unit cases cover refusal boundaries, canonical runtime routing,
context before scope construction and restoration, intentional absence and queued
cancellation. Actual handler construction, invocation, internal handler dispatch,
per-subscription scopes, causal actor/trace, foreign cancellation, async faults and
cleanup are explicit Step 3/4 obligations; this step claims none of those execution
proofs. Existing producer worker timing/cleanup proofs use a disclosed scoped
publication wrapper around the real acceptance adapter. It deliberately models
cancellation-ignoring publication and real DI cleanup ownership. A separate
unwrapped-worker case proves durable acceptance without business construction.

#### Step 2 validation before review

The final `CI=true` Release solution build reports zero warnings/errors. The unit
suite passes 1,823 cases and architecture passes 287, with no failures/skips.
The 36 acceptance/inbox cases plus all 18 producer-worker cases pass together
(54 Docker cases). Foundation's canonical manifest registration replaces two
old Persistence-only composition fixture assumptions; the fixtures now exercise
the actual registration without opening a database or adding a second manifest.
The focused composition/corpus run also passes seven cases. Seven ADR-status
controls, format verification and whitespace checks pass. These are local receipts;
no PR-head CI or subscriber-owner delivery is claimed. Both fresh review receipts
follow below before Step 3 begins.

#### Step 2 review round 1

A fresh GPT-6-astra/high reviewer examined every file in `80a3261..78992fc` and
approves with zero Blocker/Major findings. It independently passes all 36 new
acceptance/inbox Docker cases; the other local receipts above were inspected,
not rerun. One Minor catalogue link still named the renamed immediate-handler
worker proof. Root verifies the method rename and updates the label to the actual
acceptance proof. No runtime change is needed. Documentation links/fragments,
historical-record preservation and whitespace checks pass after the correction.
The second fresh review follows the correction commit.

#### Step 2 review round 2 and closeout

A fresh GPT-6.1-sol/high reviewer independently examines all changed files in
`80a3261..bce0bc7` and approves with zero Blocker/Major findings. It independently
passes 36 acceptance/inbox, 18 producer-worker, ten focused publication/composition
unit and all 287 architecture cases, with zero failures/skips. It reports one
Minor XML-comment grammar error; root verifies and removes the extra word.
Documentation/fragment, historical-preservation and whitespace checks pass.
Implementation is `78992fc`; `bce0bc7` corrects the verified round-one reference.
No runtime fixes were required. Step 3 follows automatically under the accepted
package; production subscriptions remain Pending.

### P02b-3 Step 3 — business owner and completion gate (2026-10-13)

**Complete; both independent review rounds complete.**
The closed prepared-owner entry announces the routing tenant/org and binds the
actual frame, unit, transaction and generation before retained-envelope loading.
Full validated event context replaces any inherited caller context before module
resolution. Inbox claim precedes classification/handler construction. A duplicate
skips business classification/construction and completes under the same final fence.

Requests and deliveries share the common owner body: successful-save domain drain,
producer invariants, MUST audit, exact-row lock and fresh expiry check, sticky gate
admission, then physical COMMIT. No delivery opens a raw frame and calls the old
entry as a joiner. Incoming-event causation reaches nested production; immediate
domain-event identity takes precedence. SystemActor remains effective while closed
audit metadata preserves original causal actor and incoming event identity in both
writing paths. Shared audit fallback/reconciliation diagnostics suppress raw
exceptions only in event context; ordinary HTTP diagnostics remain unchanged.

The one-use completion gate distinguishes cancellation before admission, unknown
COMMIT after admission and acknowledged durability. Npgsql retains successful
transaction disposal for owned cleanup, so an acknowledged commit reaches its
receipt before disposal faults or hangs. Cleanup before reuse and stale-frame
protection remain enforced. The executor retains its callback, frame and scope
through actual cleanup; scheduling deadlines/capacity/shutdown remain Step 4.
Ordinary versus privileged execution is explicit manifest metadata; P3 supplies
no privileged executor. Retained Active work may execute despite a Pending sibling,
while the producer's whole-event Pending hold remains unchanged.

Production defaults remain Pending. Two fresh sequential review receipts follow
before Step 4 begins.

#### Step 3 validation before review

The `CI=true` Release solution build has zero warnings/errors. All 1,847 unit and
289 architecture cases pass, zero failures/skips. The 33 new delivery execution
and four physical Npgsql cleanup cases pass together as 37 Docker cases. They
exercise constructor/invocation/null-task failures, caller versus foreign
cancellation, SystemActor and original causal metadata before construction and
through cleanup, direct/nested/domain-event production, atomic five-leg rollback,
duplicate short-circuiting, ownership loss after saved work, expiry behind an
unchanged row lock, lost COMMIT acknowledgement at attempts one/six, and cleanup
faults/hangs after acknowledged durability. Unit proofs separately exercise the
closed capability/gate, fallback Activity, causal audit composition, capture
exclusions and actual nonempty bounded audit-failure diagnostics.

An isolated source copy passes all six gate/token/expiry controls before mutation.
Removing gate refusal admits a late callback when its token is deliberately not
canceled; the corresponding test fails. Removing only the final expiry check
fails both expired-work and blocked-lock-expiry cases while the token/gate controls
remain green. Removing token equality fails its ownership-loss case while the
other two controls remain green. These are compiled behavioral mutations, not
source-string assertions; production source is never mutated for this experiment.
The restored isolated baseline passes all six controls after the mutations.
Required format verification, whitespace, seven ADR-status controls and the
1,623-link/372-fragment corpus sweep pass. The broader integration regression,
excluding the long frontend rendering proof, is still running at this commit;
its outcome must be recorded before this step closes. Neither this local
receipt nor the held production manifests claims an active production consumer
or PR-head CI. Step 4 retains independent scheduling, timeout and replay proofs;
Steps 5/6 retain actual defaults, Seeder convergence and full packet closeout.

#### Step 3 review round 1

Two fresh complementary reviewers inspect `81ab105..77890ea` against the accepted
package and current code. GPT-6-astra/high approves the runtime/security path with
zero Blocker/Major/Minor findings. It independently passes 22 gate/audit-metadata/
diagnostic unit and two owner-census cases; integration/mutation evidence is
inspected, not rerun. GPT-6.1-sol/high independently passes those 22 unit, all 37
new PostgreSQL integration and two owner-census cases, zero failures/skips. It
approves with one Minor: the catalogue's updated 196-method total omits P02b-3
Step 3 from its delivery attribution. Root verifies the two new methods and adds
that provenance; no runtime correction is needed.

The broader integration run finishes after the implementation receipt: all 1,373
additional cases pass, zero failures/skips. This excludes the 37 new cases already
run and `PublicServerRenderingTests`, whose long frontend proof remains in the
full Step 6 regression. Together these runs pass 1,410 integration cases. The
catalogue correction and review record pass link/fragment, historical-preservation
and whitespace checks. Fresh round 2 follows the correction commit.

#### Step 3 review round 2 and closeout

Two new reviewers independently inspect `81ab105..5a8b85d`, including the verified
round-one correction. GPT-6-sol/high approves the runtime/security/concurrency
path with zero actionable findings and independently passes 22 focused unit and
37 integration cases. A fresh GPT-6-astra/high evidence reviewer approves with
zero Blocker/Major/Minor findings and independently passes those 22 unit, 37
integration and two owner-census cases. All reruns have zero failures/skips.
Full-suite and compiled-mutation receipts are inspected rather than rerun by
these reviewers. The initially selected evidence reviewer could not start because
its model was at capacity; it supplies no review evidence and was replaced by
the fresh reviewer named above.

Implementation is `77890ea`; `5a8b85d` records the broader regression and fixes the
verified catalogue attribution. No runtime review fix is required. Earlier
Accepted ADR text and historical delivery records remain intact. Step 4 follows
automatically under the accepted package; no new decision or privilege expansion
is needed for its independent subscriber scheduler.

### P02b-3 Step 4 — subscriber scheduling, terminalization and replay (2026-10-13)

**Complete; both independent review rounds and the verified correction complete.**
The API-owned subscriber worker shares the existing confined keyed data source,
with no new role, migration or credential reader. Its store claims one exact
tenant/subscription/event tuple, selects routing/status columns only, and uses
the existing ordered indexes. Pending and privileged targets are held by exact
type/subscription pair; an accepted Active sibling remains runnable. Normal
claims spend independent attempts, expired claims recover without execution or
another attempt, and the sixth claim never authorizes a seventh invocation.

The subscriber terminal writer extends the restricted actual-row witness and
fifth audit seam. Tenancy registers `platform.delivery.dead_letter` with that
writer; the terminal UPDATE and matching MUST/SecurityEvent audit share one
transaction. Targeted internal replay admits one completed terminal Active
ordinary tuple, invalidates its old ownership and never resets an inbox or
republishes a broadcast. Operator HTTP/permissions remain Phase 03.

Task-owned controlled execution closes its sticky gate before asynchronous
cancellation callbacks, retains the actual application scope through those
callbacks and cleanup, and rejects control reuse before acquiring cleanup
ownership. Acknowledged durability survives faulted or overlong cleanup; retained
tasks continue to block their local partitions and consume the four-task capacity.
The shutdown wait uses one budget and the host token. Snapshot refresh follows
the configured cadence even after failure; gauges read immutable memory only.
Finite event/subscription labels and bounded diagnostics add no envelope read to
the scheduler. The private delivery health check leaves `/healthz` unchanged.

Real-role store/worker tests and exact production-capability censuses with planted
controls pass local validation. Production defaults remain Pending until Step 5's
initializers and Seeder handoff are ready. Final test and two fresh sequential
review receipts follow before that step begins.

#### Implementation validation

The CI-equivalent Release build passes with zero warnings/errors. The final
candidate passes 1,855 unit, 298 architecture and 238 focused integration cases,
with zero failures/skips. The integration set includes 45 new store and 21 new
worker cases plus 172 producer/acceptance/inbox/owner/role regressions. TRX files
record each run; Docker-dependent tests use actual PostgreSQL and the guarded
application or confined scheduler role appropriate to the boundary.

The final replay control first terminalizes four real rows, then resets one;
terminal neighbours differing in tenant, subscription or event remain byte-for-byte
unchanged. Business execution also carries a distinct human causal actor while
effective audit actor remains SystemActor. No-listener trace proof asserts the
actual source has no listeners before checking a fresh canonical attempt ID.

Final format verification, all seven ADR-status controls, all five corpus guards
and whitespace checks pass. The changed-document sweep checks 1,818 relative
links and 398 fragments with zero missing targets/anchors; added prose passes
the 88-column check. Earlier Accepted ADR bodies and dated P02b receipts remain
byte-preserved.

Three catalogue rules become Implemented with their behavioural/structural owners.
The catalogue now records 205 architecture methods and 196 Implemented rules,
120 in the architecture assembly and 76 elsewhere. Consumer-handler censuses,
production activation and defaults/Seeder proofs remain Steps 5–6. Earlier dated
receipts retain their original counts and pending status.

#### Step 4 review round 1

Two fresh read-only reviewers inspected `3411424..262d4b2`:

- **Runtime/security — GPT-6-astra, high:** one verified Major. Subscriber
  execution and durable acceptance used the same singleton/raw partition lane;
  a retained handler or postcommit cleanup could delay producer acceptance and
  consume producer timeout/capacity. Its independent rerun passed 66 focused
  PostgreSQL and eight focused unit cases, showing the missing combined scenario.
- **Proofs/docs — GPT-6-sol, high:** Approve, no Blocker/Major/Minor/Suggestion.
  It inspected production capability censuses, actual-role/worker tests and
  documentation/history without independently rerunning the suites.

Root verified the shared lane and separates only the internal execution lane
with a fixed `delivery:` prefix. Validated event partitions remain canonical UUIDs;
consumer serialization and raw scheduler partition holds remain intact. A live
subscriber cannot delay a new same-partition publication's durable acceptance.
Two real-bus/PostgreSQL regressions hold the actual handler or acknowledged-COMMIT
cleanup and accept a second same-partition event before releasing the first.
In an isolated source copy, restoring the shared raw lane makes both cases fail;
no production source or branch is mutated for that control. This implements the
accepted independent failure domains and introduces no new policy or ADR.
Correction validation passes the Release build with zero warnings/errors, 1,855
unit cases, 41 affected architecture cases and 101 store/worker/executor PostgreSQL
cases, with zero failures/skips. Both isolated restored-baseline cases pass again.
Format, whitespace and the 1,818-link/398-fragment document sweep pass; the next
fresh review round follows the correction commit.

#### Step 4 review round 2 and closeout

Two new read-only reviewers independently inspected `3411424..14d5f93`, including
the complete Step 4 implementation and round 1 correction:

- **Runtime/security — GPT-6-astra, high:** Approve, zero
  Blocker/Major/Minor/Suggestion. Independently passed 101 PostgreSQL cases,
  16 lifecycle/observation/commit-gate unit cases and 36 architecture cases,
  with zero failures/skips. It verified lane separation, fresh-clock fences,
  row-bound terminal audit, exact replay and actual resource ownership.
- **Proofs/docs — GPT-6.1-sol, high:** Approve, zero
  Blocker/Major/Minor/Suggestion. Independently passed 101 PostgreSQL cases,
  41 architecture cases including all five corpus guards, and eight unit cases,
  with zero failures/skips. It verified the corrected binary's new tests/lane,
  planted census controls, catalogue counts and preserved historical records.

Both reviews used existing Release artifacts without independently rebuilding or
rerunning the isolated mutant. Their receipts distinguish actual reruns from
inspection of root's earlier build/format/link/mutant evidence. Neither changed
files or GitHub. The correction is committed at `14d5f93`; root's restored mutant
baseline passes both cases after the old-lane mutant failed both. Step 4 is closed;
Step 5 follows automatically with useful consumers and semantic Seeder handoff.

### Durable outbox dispatch

P02b-1 Steps 2/3 implement `IOutbox`, replace Packet 3's step-7 shell with
an enrollment/invariant guard, and supply owner-side domain collection and the
first publisher. P02b-2 delivers hosted producer dispatch and production-census
enforcement; all four steps and both independent review rounds are complete and
merged. The [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns final
verification; consumer delivery remains P02b-3.
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
P02b-2 Step 3 registers the mandatory hosted processor. The corpus's gateway
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
  `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` is **Implemented**
  with Step 4's nonempty credential caller census and passing planted controls;
  all four steps and both independent review rounds are locally complete.
  The [PR handoff](#p02b-2-pr-handoff-2026-10-12) owns CI/review/merge state.
- The three carriers that say the credential lives in "the worker host" —
  [Database Standards § Database roles](../standards/05-database.md#database-roles),
  `architecture/15` and `.env.example` — are derived cells, not a topology decision;
  ADR-0003's own role table has no host column. They are corrected to name the API host
  in the same change.

#### The dispatcher

- **The lease.** A committed normal claim uses a fresh canonical UUID token in
  `locked_by`, database-time expiry and one just-in-time row. Every acknowledgement,
  failure and terminal transition checks row ID, token and unexpired lease.
  Recovery first replaces an expired token with a fresh one; it never publishes
  or increments attempts. Stale callbacks cannot clear a later lease.
- **The migration** adds lease/terminal columns and exact status-column grants
  together. [Database Standards § Outbox](../standards/05-database.md#outbox) owns
  canonical DDL/checks/indexes. Audit-chain INSERT-only privilege enables the
  separate fifth audit writer on the same confined transaction.
- **Retry** counts six committed normal claims, including claim-before-crash.
  The five delays are 1 s, 5 s, 30 s, 5 min and 1 h. Failure clears the lease;
  exhausted or invalid rows request terminalization without another publication.
  A failed terminal MUST audit rolls back terminal state and retries only
  terminalization. Stored descriptors are closed codes capped at 256 UTF-8 bytes.
- **The SQL form.** Parameterized commands use only the confined data source;
  no EF materialization or set-based API owns dispatcher status. Required Pending
  subscriptions and their aliases are excluded before claim/payload validation;
  intentional absence is the only known-type zero-subscriber acknowledgement.
- **Deadlines.** The accepted bounds are in the P02b-2 package. Ignored
  cancellation retains and observes the task, blocks its local partition and
  makes health unhealthy; four retained overruns stop new claims. Late work
  cannot acknowledge the row. There is no forced task termination promise.

#### Ordering

ADR-0046 accepts deterministic eligible selection by
`(available_after, occurred_at, id)` within one processor. It promises no enqueue,
commit, retry, cross-process or unconditional same-key publish order. Interleaved
transactions and retry deferral are proof subjects. Partition-key derivation
remains the separately delivered ADR-0057 contract; a key alone proves no durable
sequence. See [Ordering](../architecture/15-event-and-outbox.md#ordering).

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

**Accepted contract; implementation pending P02b-3.** Construction-free manifests
carry stable versioned subscription IDs, compatible aliases and required
Pending/Active readiness. Active publication durably accepts every matching
delivery without running business handlers. The independent executor owns retry
budgets, leases and terminal state in a separate delivery ledger.

One shared `inbox_messages` is keyed by `(tenant_id, subscription_id, event_id)`.
The transport opens the app-role unit of work, announces envelope tenant and
organization, then claims before handler construction. Completed duplicates skip
construction. Business state, completed inbox, produced outbox, MUST audit and
fenced ledger completion share one physical transaction. Rollback removes a new
claim; completed inbox rows never reset. Ordinary delivery uses SystemActor and
refuses privileged work; Phase 03 owns erasure's separate audited boundary.

Timeout poisons the business owner, preventing late success. An uncertain COMMIT
is Indeterminate, including the final claim; atomic ledger/inbox state decides
recovery. The bypass scheduler sees only enumerated identity/routing/status
columns, never envelope payload. P02b-3 supplies canonical DDL, exact RLS/grants,
capture exclusions and the ADR-0023 minting correction before runtime.

A poisoned subscription leaves siblings untouched. Its fenced terminal state and
Tenancy-owned `platform.delivery.dead_letter` MUST security-event commit together.
Internal replay resets only that ledger pair and invokes its executor, never
`PublishAsync`. Phase 03 retains producer reset and operator permission/reason,
fencing and surface contracts. Phase 11 retains purge intervals and broker
binding. External effects need subscription/event idempotency or an outbox intent.

#### Observability

[Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics)
is the sole inventory of names and dimensions. P02b-2 supplies producer duration,
failure, terminal and lease-loss diagnostics, immutable backlog/eligible-age/
deferred/heartbeat snapshots and registered `outbox` health. No metric has a
tenant label; event-type labels come only from the bounded registry. Required
Pending rows, completed and terminal rows are excluded from normal eligibility.
P02b-3 owns independent subscriber terminal/dedup signals and minimizes the old
transport diagnostics. Separate producer/subscriber failure signals describe
separate budgets. Health is registered, not mapped; `/healthz` stays liveness and
Phase 11 owns `/readyz` and production alert windows.

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

Under ADR-0046, P02b-3 supplies independent Customization subscriptions for the
`card@1` content type and `plain@1` taxonomy. Each create-only effect owns one root,
generation update and MUST
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
  it in production delivery under ADR-0046. Its proof is a MUST-classified direct-write
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
- **Dead-letter transitions — Accepted ADR-0046.** Producer terminal state and
  Tenancy-owned `platform.outbox.dead_letter` MUST security-event commit on one
  short confined-role transaction. `WriteEventTerminalAsync` validates its
  restricted `EventTerminalTransition` witness against the actual fenced UPDATE
  and draft identity. Audit failure rolls back state and retries terminalization,
  without another publication. The corresponding subscriber operation remains
  P02b-3. Register each planned off-path slug only with its writer.
- **Routine dispatcher bypass — Accepted ADR-0046.** The confined credential,
  exact grants, bounded diagnostics and atomic terminal audit explicitly exempt
  routine polls/claims/acknowledgements/retries from per-invocation security audit.
  Existing `EnterPlatformAdminScope` entry audit and all other bypass obligations
  remain unchanged. This is no general background-job exemption.
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

P02b-2 bounds the durable failure descriptor under **G18** to closed stage,
reason and category codes, with only allowlisted SQLSTATE where applicable;
exception text/type, trace identity and payload are not persisted there. The
canonical DDL owns the byte bound. Step 1 implements the migration beside the
lease columns, including redaction of legacy descriptors without rewriting
event facts/state; Steps 2–3 implement transitions and bounded diagnostics.

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
  `Outbox_Claim_IsHeld_Until_Dispatch_Completes` replaces the former unconditional
  exactly-once assertion with live-lease contention, expiry/crash redelivery and
  stale-transition refusal; Step 3 implements the actual two-worker proof.
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

- **ADR-0046 Accepted** — event delivery, the shared inbox contract and separate
  delivery
  ledger: stable subscription identity, readiness, durable acceptance, attempts,
  cancellation, terminal transitions and targeted replay. Canonical inbox/ledger
  DDL remains P02b-3 work; acceptance claims no consumer runtime.
- **ADR-0047 Accepted** — the background-job runtime, with the dated ADR-0003 Amendment
  its storage decision owes.
- **The dated amendments the register names**, each Accepted before the code it governs:
  ADR-0044 / ADR-0033 / ADR-0040 for the delivery and domain-event frames (G6),
  ADR-0046 with ADR-0044/0033 disclosures and fifth-writer seam
  for terminal audit (G7), ADR-0046 with ADR-0003/0006 disclosures for bounded
  bypass/retry (G8/G4), ADR-0036 for the matrix staging
  (G11), ADR-0032 for `LS0002` (G16), ADR-0038 for the payload-content rule (G18).
- **Database Standards § Inbox** — the canonical inbox DDL beside § Outbox, with its
  table class, its policy taken by link and its grants; plus the erratum ADR-0023's
  DB-side minting list owes. This phase states the **contract** — how long a
  `(event, consumer)` pair stays replayable — and
  [Phase 11](phase-11-production-hardening.md) picks the purge interval that honours it.
- `IOutbox` and the `OutboxFlushBehavior` invariant guard, delivered by P02b-1
  under Accepted ADR-0057; immediate INSERT uses the announced transaction.
- `TenantCreatedIntegrationEventV1` from completed provisioning; P02b-3
  independent Customization subscriptions install untouched `card@1` content-type
  and `plain@1` taxonomy defaults with one-root effects and the P02b-3 Seeder
  identity handoff.
- A durable `OutboxProcessor` implementing the specified lease: a fenced claim, a
  per-delivery deadline, bounded retry, a terminal producer state, and the migration
  that adds `locked_by` / `locked_until` **and** extends `learnstack_outbox_admin`'s
  column-scoped `UPDATE` grant in the same change.
- The dispatcher's credential registered as a keyed data source behind a boot-checked
  `Lazy<>`, with `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` promoted
  from Registered only after the full census and planted controls pass.
- `partition_key` persisted onto the row and rebuilt into the envelope on dispatch.
  `PartitionKey` abstract on `IntegrationEventBase` and per-key serialisation in
  `InProcessEventBus` shipped in Packet 5; the durable hop is this phase's.
- `inbox_messages` and `IInboxGuard` over the canonical DDL, the per-`(event, consumer)`
  delivery transaction opened before handler construction, and tenant and organization
  context restored in every handler scope from the envelope.
- A subscriber-side ledger and terminal path accepted by ADR-0046: a terminal
  outcome per
  subscription, a sanitized failure descriptor, its counter, its MUST-class audit row,
  and targeted internal replay through the executor/inbox boundary. Producer
  reset and operator permission/reason/fencing remain Phase 03 contracts.
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
- The registered, unmapped `outbox` health check and accepted producer event
  metrics in
  [Observability Standards](../standards/10-observability.md#required-metrics),
  including bounded immutable snapshots. Subscriber signals remain P02b-3.
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

P02b-2-owned dispatcher criteria below are met by the
[Step 4 local closeout](#p02b-2-step-4--enforcement-and-closeout-2026-10-12).
The combined HTTP/event-delivery tracing criterion still requires P02b-3
consumer execution; the whole phase is not complete. The
[merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns final verification.

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
- Two coordinators on one PostgreSQL store force an interleaving with at most
  one live owner. Claims use the exact confined-role grant and fresh acquisition
  tokens; this proves neither two OS processes nor exactly-once publication.
- Expiry/recovery replaces the token, and stale success/failure/terminal writes
  affect zero rows. The six-claim crash case never publishes a seventh time.
- Producer terminal state and exactly one `platform.outbox.dead_letter` MUST
  security-event commit atomically. Audit refusal rolls back terminal state and
  permits only terminalization retry; no second publication consumes a budget.
- Retry uses the five accepted delays, clears the lease and stores only closed
  bounded descriptors. Terminal requests and exhausted rows leave normal
  eligibility; successful terminalization leaves backlog snapshots.
- A blocked dispatcher loop turns the `outbox` health check unhealthy within a bounded
  number of poll intervals, while `/healthz` still answers 200.
- With one terminal row, one row whose `available_after` is in the future and one due
  row in the table, the age metric reports the **due** row's age — the case that
  separates it from `min(occurred_at) WHERE processed_at IS NULL`.
- A `ConnectionStrings:OutboxDispatcher` naming any role other than
  `learnstack_outbox_admin` refuses API startup upon mandatory worker resolution;
  an absent or malformed value names its key and the expected form. Lazy registration
  constructs no data source and performs no connection; mandatory worker startup
  resolves the source and verifies connectivity. No message carries the password.
- Declared compatible aliases deserialize through frozen payload options;
  unknown/retired/invalid rows terminalize under closed reasons. Pending required
  subscriptions, including aliases, preserve rows byte-exact before claim and
  validation. Only intentional absence acknowledges without publication.
- Deterministic eligible selection is tested with interleaved commits and retry
  deferral; no unconditional same-key publish-order assertion is retained.
- Overrun tasks are retained/observed, block local partitions and make health
  unhealthy. Unrelated work progresses until the cap of four stops new claims;
  late completion never acknowledges the row.

**Consumer side — P02b-3**

- Sequential/concurrent duplicates yield one completed inbox/business effect per
  stable subscription/event. Distinct sibling subscriptions both execute. A
  completed duplicate skips handler construction; removing transport guard
  acquisition makes the proof fail.
- Faulted business work commits no business/inbox/outbox success. A poisoned
  subscriber advances only its own durable budget after rollback, leaves producer
  and healthy siblings unaffected and terminalizes atomically with its MUST audit.
- Targeted internal replay invokes only its executor; completed inbox state is
  never reset. Late work cannot defeat the new fence.
- Envelope tenant and organization are announced before resolving handlers,
  stores or module contexts. SystemActor is effective identity; a human actor is
  causal metadata only. Non-marked event types preserve envelope organization.
- Construction failure and cancellation count under the subscriber policy. A
  timeout poisons the owner; uncertain physical COMMIT, including on the final
  claim, resolves from atomic ledger/inbox completion without repeating success.
- Every production module-assembly sweep reports its nonzero subjects. Both real
  Customization defaults subscriptions, one-root audits, authored/lifecycle
  preservation and semantic key/version Seeder handoff pass through the actual
  delivery boundary.
- Inbox/ledger RLS isolates tenants; scheduler column-only grants cannot read
  payload or perform business writes. Migration chains apply together. Ordinary
  app-role execution refuses privileged work and external effects require their
  own idempotency or outbox intent.
- Persisted diagnostics reject raw exception/payload/identity text and values over
  256 UTF-8 bytes; no sensitive token leaks through retained failure descriptors.

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
- Ignored cancellation poisons the owner and blocks late success. Retained tasks
  remain observed and hold their local partition; unrelated work progresses
  within the accepted cap. An uncertain physical COMMIT is Indeterminate and
  recovers from atomic durable state rather than assuming rollback.
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
- Every event metric uses the literal name and bounded dimensions in
  [Observability Standards](../standards/10-observability.md#required-metrics).
  Refresh publishes one complete immutable queue snapshot; each gauge reads an
  immutable snapshot. Pending, deferred, terminal and normal eligibility are
  tested as distinct states. Job dimensions remain G9/G17 work.
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
- **Partition identity is mistaken for durable order.** ADR-0057 enforces the
  declared aggregate/tenant key. ADR-0046 separately bounds deterministic eligible
  selection; interleaved commits and deferred retries must remain proof subjects.
- **Terminal audit is detached from state.** The witness must come from the
  actual fenced transition and bind draft tenant/organization/event. A failed
  MUST insert rolls back terminal state and retries no business publication.
- **Consumer delivery leaks into the producer budget.** P02b-3 must durably accept
  before acknowledgement and use its separate executor/ledger. Shared inbox
  deduplication uses stable subscription identity, preserving healthy siblings
  and completed outcomes during targeted replay.
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
  consumption. Forced interleaving proves one live producer owner; publication
  stays at least once. **Forced redelivery** produces one committed business
  effect per subscription through the actual shared inbox boundary.
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
