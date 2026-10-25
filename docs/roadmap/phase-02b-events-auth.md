# Phase 02b: Events, Background Jobs, Identity, and Session

> **Status (2026-10-25).** Phase 02b is **in progress**. P02b-0 through P02b-7 are
> complete. P02b-1–5 are merged through PRs #29–#33; P02b-6 through PR #34 and
> correction PR #35; P02b-7 through
> [PR #36](https://github.com/HodeTech/LearnStack/pull/36).
> [ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md) and
> [ADR-0071](../decisions/0071-uncertain-account-site-refusals.md) are Accepted and
> implemented. The P02b-7 merge closeout below links the source-bound A/B and
> final PR-head qualification. P02b-8's G16/G18 decisions are Accepted; its
> implementation Steps 1–3 and both review rounds per step are complete. Step 4
> source-bound qualification remains in progress; the
> [individual exit index](phase-02b-exit-evidence.md) names each criterion's owner.
>
> This document is the plan. Every dated decision package, step record, review
> round, correction and merge closeout lives in the [delivery log](phase-02b-delivery-log.md), split out
> on 2026-10-22 under
> [ADR-0069](../decisions/0069-phase-documents-and-delivery-logs.md). An Accepted
> decision package there is normative; the [decision register](#the-decision-register)
> links each answer.
>
> | Packet | Title | State |
> |---|---|---|
> | P02b-0 | Phase entry | ✅ complete — 2026-10-12; [entry verification](phase-02b-delivery-log.md#p02b-0-entry-verification-2026-10-12) |
> | P02b-1 | Producer | ✅ complete and merged — PR #29, 2026-10-12; [merge closeout](phase-02b-delivery-log.md#p02b-1-merge-and-closeout-2026-10-12) |
> | P02b-2 | Dispatcher | ✅ complete and merged — PR #30, 2026-10-13; [merge closeout](phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13) |
> | P02b-3 | Consumer | ✅ complete and merged — PR #31, 2026-10-15; [merge closeout](phase-02b-delivery-log.md#p02b-3-merge-and-closeout-2026-10-15) |
> | P02b-4 | Background jobs | ✅ complete and merged — PR #32, 2026-10-19; [merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) |
> | P02b-5 | API authentication | ✅ complete and merged — PR #33, 2026-10-20; [merge closeout](phase-02b-delivery-log.md#p02b-5-merge-and-closeout-2026-10-20) |
> | P02b-6 | Realm and seed | ✅ complete and merged — PR #34 and correction PR #35, 2026-10-23; [correction closeout](phase-02b-delivery-log.md#p02b-6-correction-merge-and-closeout-2026-10-23) |
> | P02b-7 | BFF session | ✅ complete and merged — PR #36, 2026-10-25; [merge closeout](phase-02b-delivery-log.md#p02b-7-merge-and-closeout-2026-10-25) |
> | P02b-8 | Exit enforcement | Decisions Accepted — [acceptance](phase-02b-delivery-log.md#p02b-8-acceptance-2026-10-25); [Step 1 complete with both reviews](phase-02b-delivery-log.md#p02b-8-step-1-review-round-2-and-closeout-2026-10-25); [Step 2 complete with both reviews](phase-02b-delivery-log.md#p02b-8-step-2-closeout-2026-10-25); [Step 3 complete with both reviews](phase-02b-delivery-log.md#p02b-8-step-3-review-round-2-and-closeout-2026-10-25); Step 4 in progress |

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

At the [P02b-0 entry audit](phase-02b-delivery-log.md#p02b-0-entry-verification-2026-10-12),
`IOutbox` and step 7's implementation were absent. P02b-1 supplies immediate
enqueue and the enrollment/invariant guard; P02b-2 the API-host `OutboxProcessor`
BackgroundService; P02b-3 `IInboxGuard` and `inbox_messages` as shared transport
infrastructure under ADR-0046; P02b-4 guarded Hangfire storage and worker
activation; P02b-6 the realm's protocol mappers. Packet 5's earlier `IInboxGuard`
references were planned comments, not an interface declaration.

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
  Packet 3b record assigns XML-documentation target-existence and changed-prose
  88-column checks on the trigger that a second contributor gains write access.
  [Phase 03](phase-03-identity-admin.md#cross-cutting-follow-up-at-phase-exit)
  inherits both with that unchanged trigger. Markdown fragment lint establishes
  neither check; the exit evidence records the live contributor census.
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
  [ADR-0047](../decisions/0047-transactional-background-job-runtime.md) and
  [P02b-4's package](phase-02b-delivery-log.md#p02b-4-decision-package-2026-10-15)
  are Accepted — 2026-10-15.

## Scope

### Packets and decision gates

Phase 02a needed twelve packets for a narrower scope. This phase declares nine, in
dependency order. `P02b-0` writes no code. Every packet after it opens with the decision
pass [Roadmap § Decision Timing](README.md#decision-timing) describes: its gates are
Accepted, and its catalogue rows registered, before its first line of code.

| Packet | Contents | Cannot start until |
|---|---|---|
| **P02b-0** | Phase entry: every premise the register below cites re-verified against `HEAD` — the code, the corpus, the Hub repository — and the register corrected where Phase 02d moved it. It drafts no record: each gate closes in the decision pass of the first packet it shapes, and each glossary headword this phase needs — `inbox`, outbox lease, dead letter, audit frame, job activator, integration-event consumer — is written in the pass whose gate fixes its meaning | [Phase 02d](phase-02d-walking-skeleton.md) exits |
| **P02b-1** | Producer: `IOutbox`, the `OutboxFlushBehavior` invariant guard, successful-save domain-event collection and owner-only completion, and the first event with its enqueue path | G2, G3 (the key's derivation), G5 (the stored identities), G6, G18 (the payload rule), G19 Accepted |
| **P02b-2** | Dispatcher: the lease migration and grant extension, claim / publish / mark, backoff, the producer terminal state, the credential and its boot guard, the metrics and the health check | G1, G3, G4, G5, G7, G8, G17, G18 (`last_error`) Accepted; P02b-1 |
| **P02b-3** | Consumer: durable acceptance, shared inbox/ledger DDL and `IInboxGuard`, the common per-delivery transaction, both real default subscriptions and Seeder handoff, independent attempts and audited terminal state, targeted internal ledger replay | G1, G6, G7 Accepted; P02b-2 |
| **P02b-4** | Background jobs: accepted H1 transactional/external profiles, confined storage/wakeups, tenant capacity and recurrence/fan-out; no production job/recurrence definition | G9 Accepted 2026-10-15; P02b-3 complete |
| **P02b-5** | API authentication: closed JWT profile, ordered reconciliation and attributed rejection recorder, additional subject quota and one physically read-only authenticated endpoint | G10, G11, G12 (the BFF's client), G14, G20 Accepted |
| **P02b-6** | Realm and seed: shared client scope/mappers, protected user-profile UUIDs agreeing with `SeedData`, safe warm reconciliation, narrow account self-service and real Keycloak/API proofs | G12, G13 (the access-token lifetime's authority) Accepted; P02b-5 |
| **P02b-7** | BFF session: native custody, opaque cookies, single-flight refresh, CSRF, logout, studio guard and native caller bearer attachment to the injected SDK | G13 Accepted; P02b-6 |
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
| G1 | What is the subscriber's fate when its handler exhausts retries, and what is the inbox table — one shared or per module, keyed `event_id` or `(tenant_id, consumer, event_id)`, claimed by the handler or by the transport? Three arms travel with it: the class of consumer whose effect cannot share the inbox transaction (an external call, the platform-scope erasure handler the Audit spec already carries) and what "effectively once" means for it; whether an event without the organization marker is delivered tenant-wide regardless of the producing request's organization; and whether the inbox entity joins [ADR-0044 § 7](../decisions/0044-audit-write-path.md)'s named audit-capture exclusion list once a delivery declares intents | One shared tenant-owned `inbox_messages` keyed by tenant/stable subscription/event, transport-claimed before construction; a separate delivery ledger owns retries, terminal state and targeted replay; completion shares the business/audit/outbox transaction | **ADR-0046** + a dated **ADR-0006 Amendment** (or an explicit supersession note in ADR-0046 naming Amendment 1's consumer-side paragraph, which fixes the per-module inbox) + a dated **ADR-0010 Amendment**, because its § Idempotency makes two things mandatory that the shared-table leaning changes — the inbox is "the module's **own** inbox table", and the guard is called by the consumer — + the `Integration_Event_Handlers_Use_InboxGuard` assertion re-written in the same record to name whoever ends up owning the call, since a transport-claimed delivery fails a structural rule that looks for the call inside the handler + Database Standards § Inbox + an ADR-0041 erratum beside ADR-0023's minting list | P02b-2 (the record and its amendments: the dispatcher's retry state and a failed delivery's next attempt are one state machine), P02b-3 (Database Standards § Inbox and the ADR-0023 erratum) | Accepted — 2026-10-12; decision/amendments delivered; P02b-3 Steps 1–6 implement subscriber runtime, inbox, defaults and production enforcement; complete and merged; [merge closeout](phase-02b-delivery-log.md#p02b-3-merge-and-closeout-2026-10-15) owns final verification |
| G2 | Does `IOutbox.EnqueueAsync` write the row immediately on the ambient transaction, or buffer for `OutboxFlushBehavior` at step 7 — and what is the flush point for the two paths that have no step 7? | Immediate write, with the behavior asserting no buffered message survives the handler | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 2's step-7 completion role, with a dated supersession disclosure in ADR-0032 under this acceptance; phase-doc + [Events and Outbox § Producer pattern](../architecture/15-event-and-outbox.md#producer-pattern) | P02b-1 | Accepted — immediate enqueue/owner completion |
| G3 | What replaces the unconditional same-key publish-order promise — a scoped bound, or strict per-key head-of-line blocking — and is the key's derivation checkable structurally, given that [§ Ordering](../architecture/15-event-and-outbox.md#ordering) admits an aggregate id or a deliberately declared tenant-wide key and nothing else? | Scope it: no column in `outbox_messages` carries an unconditional per-key guarantee, so the honest bound is one processor, no failure, deterministic eligible selection; no enqueue/commit-order guarantee | Derivation: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md). Ordering: Accepted ADR-0046 and the P02b-2 package, followed by [§ Ordering](../architecture/15-event-and-outbox.md#ordering) alignment under this acceptance | P02b-1 (whether the key's derivation is checked structurally: the first event declares a key), P02b-2 (the ordering bound) | Derivation and ordering Accepted; scoped ordering runtime [implemented in P02b-2](phase-02b-delivery-log.md#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12); complete and merged; [merge closeout](phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state |
| G4 | What counts as an attempt, what is `MaxAttempts` bound to, and what marker keeps a terminal row terminal as the clock advances? | Six attempts (one immediate plus the five documented delays) and an explicit terminal column, because a pushed-out `available_after` stops being terminal and never leaves the pending gauge | Accepted ADR-0046 + dated ADR-0006 disclosure under this acceptance; Database Standards § Outbox DDL, index and grant edits | P02b-2 | Accepted — 2026-10-12; producer attempts/terminal state [implemented](phase-02b-delivery-log.md#p02b-2-step-2--durable-transitions-and-terminal-audit-2026-10-12) |
| G5 | Does the stored event identity stay the assembly-qualified type name with declared compatibility mappings, and what happens to an unresolvable type, a retired type and an event with no subscriber? And what is the **row's** identity — a surrogate `id` plus an `event_id` column, or `EventId` as the key — given that two documents list an `event_id` the canonical DDL does not have? | Keep the type identity, add a bounded resolver reading through `PayloadJsonOptions` and declared mappings; dead-letter a retired type at once rather than spending its attempt budget; reconcile the row's column list in one direction | Stored identities: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 12 and the Phase 02b deliverables table in Implementation Notes, with a dated supersession disclosure under this acceptance; Database Standards alignment. ADR-0046 accepts declared resolution, Pending holds and intentional absence; dispatch runtime remains P02b-2 | P02b-1 (the stored type identity and the row's identity, both written at enqueue), P02b-2 (unresolvable, retired and unsubscribed types) | Stored identities and resolution outcomes Accepted; resolver/readiness runtime [implemented](phase-02b-delivery-log.md#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12); both real durable subscriptions Active in P02b-3 Step 5 |
| G6 | When the unit-of-work owner is not an `AuditLogBehavior`, who performs the four owner-side audit acts and enters the outermost audit frame — and does a state-mutating delivery declare its own intents, keyed by handler type? | The transport opens the outermost frame beside its transaction; a delivery declares its own intents and an unregistered handler is refused as an unregistered request is | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md): bounded ADR-0040 nested-failure supersession; separately enumerated additive UoW read-seam amendment; clarifying ADR-0044/0033 common-owner amendments. G1-dependent production delivery/inbox implementation remains P02b-3 | P02b-1, P02b-3 | Producer/common owner delivered; delivery/inbox contract Accepted; common owner and execution implemented by P02b-3 Steps 2–3 |
| G7 | For a dead-letter transition: what class, what `OperationType`, what slug, on which transaction, in what order against the state change, with what failure posture — and may a second non-MediatR caller reach `IAuditStore.WriteStandaloneAsync`? | MUST `security-event`; producer `platform.outbox.dead_letter` and reserved P3 `platform.delivery.dead_letter`; fenced terminal transition and audit INSERT on the same short transaction through a narrow fifth write seam; failed audit retries terminalization without publication | Accepted ADR-0046 + bounded ADR-0044 audit-grant replacement, separate additive fifth-writer seam amendment and ADR-0033 same-transaction clarification under this acceptance; `DeclareOffPath` registration and Audit Coverage baseline | P02b-2 (the producer dead-letter row its criteria assert), P02b-3 | Accepted — 2026-10-12; producer terminal audit [implemented](phase-02b-delivery-log.md#p02b-2-step-2--durable-transitions-and-terminal-audit-2026-10-12); subscriber terminal audit implemented by P02b-3 Step 4; job part Accepted — 2026-10-15 under ADR-0047: `platform.job.dead_letter`, actual-row sixth write and same-transaction MUST audit; job terminal/MUST audit implemented; [P02b-4 merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) records final qualification and merge |
| G8 | Does the dispatcher's `BYPASSRLS` use carry a per-invocation `security-event` row, or is it exempt — and on what written terms? | Exempt, on three terms: the bypass is grant-bounded and code-path-confined, dispatch attempts are logged rather than audited, and the dead-letter transitions are the audited events | Accepted ADR-0046 explicitly replaces the routine-dispatch coverage rule and bounds the ADR-0003 grant extension; dated disclosures under this acceptance, not an ADR-0041 correction | P02b-2 | Accepted — 2026-10-12; confined dispatcher exception/census [implemented](phase-02b-delivery-log.md#p02b-2-step-4--enforcement-and-closeout-2026-10-12); complete and merged; [merge closeout](phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state; job/recurrence part Accepted — 2026-10-15 under ADR-0047, conditional on exact grants/censuses, diagnostics, observation and terminal MUST audit; job/recurrence confinement, diagnostics, observation and terminal MUST controls implemented; [P02b-4 merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) records final qualification and merge |
| G9 | Which record decides the background-job runtime — its storage under the closed four-role model, the single tenant-context writer, the job frame's transaction owner, the enqueue-site rule *and who may enqueue whose job type*, the retry contract (whose class, slug and posture adopt **G7**'s), the tenantless platform-job class a purge needs, the ADR-0035 classification, the queue grammar, and the package pins with their licence verdict? | One record answering all of it; business execution uses the existing announced request owner through `ISender`, retaining the eight setters; the chosen storage/scheduler authority is separately explicit | **ADR-0047** + a dated **ADR-0003 Amendment** for any schema/role/grant-confinement extension (not merely a new tenant table under the existing policy) + a dated **ADR-0036 Amendment** disclosing the selected context-writer contract; then Standards 05/20. A decision change is not an erratum | P02b-4 | Accepted — 2026-10-15 under ADR-0047 and the nine-step P02b-4 package; bounded prior-ADR Amendments accompany acceptance; runtime and all nineteen canonical obligations implemented; [P02b-4 merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) records final qualification and merge |
| G10 | Does the active tenant travel as a scalar `tenant_id` claim, a `memberships` array, or both — and what value does `UserId` hold? | The scalar claim, whose 02b source is an admin-set user attribute; a second UUID-valued `user_id` attribute for the actor, with `sub` staying Keycloak's subject | Phase-doc: [ADR-0036 § What is out of scope](../decisions/0036-tenant-resolution-trusted-inputs.md#what-is-out-of-scope-and-what-is-not) hands the shape to this phase by name. Reconcile `architecture/13`'s array sketch in the same change | P02b-5 | Accepted — 2026-10-19; [P02b-5 package](phase-02b-delivery-log.md#p02b-5-decision-package-2026-10-19) |
| G11 | Which [ADR-0036 matrix](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix) rows resolve in this phase, given that row 10 consults a reader that denies everyone — and what row covers a signature-valid token carrying no `tenant_id` claim on a tenant host? | Issue a matching organization claim for row 9. Rows 7/10/14 remain membership-dependent refusals until Phase 03; a claimless principal retains public-only HostOnly authority. Row 14 records no tenant audit without a confirmed host tenant, as bounded by ADR-0064 | A dated **ADR-0036** amendment plus errata beside § Staging across packets and the 2026-09-02 erratum, both of which assign row 10 to this phase without noting that its `M covers (T, O)` term is denied until Phase 03 — which groups it with rows 7 and 14 rather than with 6 and 9 | P02b-5 | Accepted — 2026-10-19; [P02b-5 package](phase-02b-delivery-log.md#p02b-5-decision-package-2026-10-19) |
| G12 | Which Keycloak client does the BFF exchange the code with, where does sign-in happen across two tenant hosts, and how do the client scope, the mappers and the seed users reach a Keycloak database that has already consumed the import? Native HTTPS on retained `*.learnstack.local` hosts, with manual hosts/trust setup, is delivered by Phase 02d and re-verified by P02b-0 | A confidential BFF client with one redirect URI per seed host, retaining native HTTPS and `Secure` cookies; and an idempotent reconciler shared by `make seed` and `make demo` — a mapper added only to the import reaches no existing workstation | Phase-doc + the realm JSON + `scripts/seed.sh` + `infra/keycloak/README.md`; a new ADR only if the reconciler becomes ADR-0004's `IIdentityProvider` | P02b-0 re-verifies [Phase 02d G32](phase-02d-walking-skeleton.md#the-decision-register), ADR-0053/0054 and ADR-0055; P02b-5 (which client the BFF redeems the code with: the API pins `azp` against it), P02b-6 | Accepted — BFF-client choice, 2026-10-19; realm/reconciliation/callback choices, [2026-10-21 package](phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20) |
| G13 | What is the BFF session — its custody of the refresh token, its store, its idle and absolute lifetimes, its refresh serialisation under concurrent requests, its terminal `invalid_grant` behaviour, its CSRF control and its logout — and which document owns the access-token lifetime? | The `HttpOnly` cookie Security Standards already describes, encrypted by the session adapter, read only by server code, with single-flight refresh and one authority for the TTL | Phase-doc + one reconciled [Security Standards § Authentication](../standards/11-security.md#authentication); [ADR-0067](../decisions/0067-development-tenant-access-token-issuance.md) is the dedicated development-issuance authority; a new session ADR is required for a server-side store | P02b-6 (which document owns the access-token lifetime: the realm export and its reconciler write it), P02b-7 | Accepted — issuance-TTL part, [2026-10-21 package](phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20) and separate ADR-0067 acceptance on 2026-10-21; session part Accepted — [ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md) and [revised package](phase-02b-delivery-log.md#p02b-7-revised-decision-package-2026-10-23), [acceptance](phase-02b-delivery-log.md#p02b-7-acceptance-and-implementation-2026-10-23), 2026-10-23 |
| G14 | What bounds the request types reachable over HTTP while `AuthorizationBehavior` is `return next()`, and what is the shape of the token-keyed rate-limit stage behind a shared BFF connection? | An enumerated read-only routable set held by a catalogue-registered rule, and a limiter stage after `UseAuthentication` keyed on the validated subject. The early anonymous stage already runs before authentication: P02d-5 delivers 60/min per canonical direct or authenticated-hop visitor IP plus 600/min peer acquisitions/allocations under ADR-0053 and ADR-0054. Exhausted known visitors refuse before peer debit; unknown visitor allocation remains peer-gated. These remain IP/peer quotas, not token quotas; a request they reject never reaches the token stage. Re-verify the [P5 remediation decision](phase-02d-delivery-log.md#p02d-5-external-review-remediation-2026-10-09) before choosing authenticated accounting, preserving pre-classification protection against unknown-host abuse | Phase-doc + catalogue registration; a dated ADR-0032 Amendment for a deny-by-default step instead | P02b-5 (the token-keyed stage and the early stage's treatment of validated callers); G34's anonymous-hop policy and ADR-0054's bounded accounting replacement are Accepted and delivered in P02d-5 | Accepted — 2026-10-19; [P02b-5 package](phase-02b-delivery-log.md#p02b-5-decision-package-2026-10-19) |
| G15 | What initiates a replay in this phase? | Targeted internal subscriber replay in P02b-3; producer/operator reset and fencing require a separate Phase 03 contract | Phase-doc; the URL space is its own decision record, per ADR-0036 | — (answered in § The consumer side; recorded so the question travels with the deferral) | Accepted — internal subscriber replay implemented by P02b-3 Step 4; operator contract Phase 03 |
| G16 | What is `LS0002`'s rule name, which project trees does it run over, what does it inspect, and what escalates it to Error? | The name and scope recorded the way Amendment 1 recorded `LS0001`, with the scope reaching the assemblies that actually handle tokens | A dated **ADR-0032** amendment + a catalogue entry + an `AnalyzerReleases` row | P02b-8 | Accepted — 2026-10-25; [acceptance](phase-02b-delivery-log.md#p02b-8-acceptance-2026-10-25), [ADR-0032 Amendment 6](../decisions/0032-exception-handling-logging-and-observability.md#amendment-6--sensitive-exception-message-diagnostic-2026-10-25); implementation and proofs complete in [Step 1](phase-02b-delivery-log.md#p02b-8-step-1-review-round-2-and-closeout-2026-10-25) |
| G17 | What are the label sets for the event and job metrics, is the tenant axis permitted, and is it spelled `tenant` or `tenant_id`? | One note in the document that owns metric names, no raw tenant label on a per-event series, and an oldest-eligible-row age metric separate from the pending count | [Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics) edit | P02b-2 (the first series ships there, and a label added afterwards touches every emitter already written) | Event metrics Accepted — 2026-10-12; runtime/guards [implemented](phase-02b-delivery-log.md#p02b-2-step-4--enforcement-and-closeout-2026-10-12); complete and merged; [merge closeout](phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state; job label/observation contract Accepted — 2026-10-15 under ADR-0047; job instruments/guards implemented; [P02b-4 merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) records final qualification and merge |
| G18 | May an integration-event payload or a job argument carry personal data, what may `last_error` contain, and do the four new durable stores enter the erasure scope? | Identifiers only, a sanitized and length-bounded failure descriptor, and the stores named in the erasure inventory | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) decides the new producer payload/NULL JSON metadata policy and application byte cap; a dated ADR-0038 cross-reference under this acceptance. [Data Protection § Right to Erasure](../architecture/23-data-protection.md#right-to-erasure-right-to-be-forgotten), [the canonical inventory](../architecture/23-data-protection.md#durable-asynchronous-store-inventory), Phase 03 privileged erasure/redaction and Phase 11 retained-copy/purge operations own their respective scope | P02b-1 (the payload rule: the first payload ships there), P02b-2 (the `last_error` rule and its DDL bound), P02b-8 (the erasure inventory) | Payload and failure descriptors Accepted and implemented; job identifier-only payload/checkpoint and failure-descriptor bounds Accepted — 2026-10-15 under ADR-0047; job payload/checkpoint/failure bounds implemented; [P02b-4 merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) records final qualification; erasure inventory Accepted — 2026-10-25; [acceptance](phase-02b-delivery-log.md#p02b-8-acceptance-2026-10-25); the [canonical matrix](../architecture/23-data-protection.md#durable-asynchronous-store-inventory) includes the current event/job/vendor copies rather than the original four-store count; drift controls implemented and reviewed in [Step 2](phase-02b-delivery-log.md#p02b-8-step-2-closeout-2026-10-25) |
| G19 | Where do the sample flow's publisher and consumer live — a fixture pair in the integration assembly, or a real event a shipped module consumes? | A real event between two shipped domain modules, because a test-assembly-only consumer leaves every module-scoped sweep with zero subjects | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) and the P02b-1 package; reconcile Tenancy/Customization/Audit specs under this acceptance. ADR-0046 accepts distinct subscription deduplication and envelope delivery scope; P02b-3 implements the two effects and Seeder handoff. The provisioning publisher must prove enqueue from its transaction announcement while ambient context remains unresolved | P02b-1 | Flow and delivery contract Accepted; subscriptions/semantic Seeder handoff implemented by P02b-3 Step 5 |
| G20 | What does an anonymous request to a **routed non-public** request type on a live tenant host answer — and does the catalogued `Backend_RequiresJwt_OnAllAuthenticatedRoutes` narrow to a surface outside tenant resolution? A second half rides with it: the shipped assertion middleware says that from this phase the refusal code differs by caller, `tenant_mismatch` for an authenticated one and `not_found` for an anonymous one, written by the middleware itself | AccountSite absent credentials give masked `not_found` 404 on tenant/organization/platform hosts. Invalid present credentials on known hosts give 401, including public routes. Rows 8/11/12 and authenticated assertions use `tenant_mismatch`; membership refusals and anonymous assertions use `not_found`; the generic unresolved MediatR gate is unchanged | The dated **ADR-0036** amendment G11 needs + a catalogue edit to that rule's Asserts line | P02b-5 | Accepted — 2026-10-19; [P02b-5 package](phase-02b-delivery-log.md#p02b-5-decision-package-2026-10-19) |

### Durable outbox dispatch

P02b-1 Steps 2/3 implement `IOutbox`, replace Packet 3's step-7 shell with
an enrollment/invariant guard, and supply owner-side domain collection and the
first publisher. P02b-2 delivers hosted producer dispatch and production-census
enforcement; P02b-3 delivers independent subscriber execution and enforcement.
The table shipped in Packet 6.

A handler calls `IOutbox.EnqueueAsync` and the row commits on the same **transaction**
as the aggregate change — the ambient one `IUnitOfWork` owns
([ADR-0040](../decisions/0040-ambient-unit-of-work.md)), not a shared `SaveChanges`, a
formulation [ADR-0033](../decisions/0033-audit-durability-model.md) withdrew. [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md)
accepts immediate INSERT through that connection/transaction. Step 7 is an
enrollment/invariant guard; the transaction owner drains domain events before
MUST audit and commit. Raw transactions without an enrolled producer frame
cannot enqueue. P02b-3 delivers production acceptance and subscriber execution wiring.

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
  registered as a keyed guarded facade with an internal lazy pool and boot-time
  role guard, on
  the pattern `Platform_DataSource_Resolved_Only_By_PlatformAdminScope` already holds.
  `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` is **Implemented**
  with P02b-2 Step 4's nonempty credential caller census and passing planted
  controls.
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

**Delivered by P02b-3 Steps 1–6 under Accepted ADR-0046.** Construction-free manifests
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
P02b-3 delivers independent subscriber terminal/dedup signals and minimizes the old
transport diagnostics. Separate producer/subscriber failure signals describe
separate budgets. Health is registered, not mapped; `/healthz` stays liveness and
Phase 11 owns `/readyz` and production alert windows.

### Background jobs

P02b-4 delivers this scope under Accepted ADR-0047/0058/0059/0060/0061; its
[decision package](phase-02b-delivery-log.md#p02b-4-decision-package-2026-10-15) holds the accepted
answers. The pre-acceptance checklist below is historical preparation, not
current authority.

**Historical pre-acceptance preparation (not current authority).**

No Accepted record selects the general background-job runtime. ADR-0028 selects
Hangfire specifically for future audit maintenance; that narrower decision must
be retained or explicitly superseded, rather than described as absent. **G9** and
**ADR-0047** are the first work item; runtime implementation waits for acceptance.
The record has to close, at minimum:

- **Storage under the closed four-role model.** Infrastructure Stack Standards
  prescribes a `hangfire` schema; Database Standards says the database has one schema,
  `public`; all four roles are `NOCREATEDB` and none holds `CREATE` on the database, so
  no role can create that schema, and every structural Row Level Security sweep reads
  `public` only. Each default here breaks a written rule, which is why the storage
  decision owes a dated ADR-0003 Amendment for a changed schema/role/grant boundary,
  not merely for a tenant table following the existing policy.
- **One writer of `ITenantContextAccessor.Current` on the job path.** ADR-0036 § Rules
  names the Hangfire `JobActivator`; `architecture/09` names a `LearnStackJob<TParams>`
  base class; the catalogue carries both spellings. The writer allow-list in
  `TenantContextConstructionTests` currently names HTTP, bus and delivery-executor
  files (two logical entry classes). The job writer is added deliberately, and
  whichever competing job spelling loses is retired rather than left in place.
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

- **Ordered authentication.** `UseAuthentication` runs before tenant resolution;
  `UseAuthorization` follows tenant assertions. The resolver consumes only the
  validated `learnstack` principal. The catalogue's ordering rule includes a
  planted-order control.
- **One tenant API authority.** [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md)
  owns the closed issuer/audience/algorithm/purpose/claim profile and key-refresh
  behavior. Real signed HTTP and provider cases complement the structural
  authority census; `learnstack-hub` tokens are refused on tenant endpoints.
- **Stable actor identity, G10.** The protected UUID-valued `user_id` supplies
  `UserId`; `sub` remains Keycloak's subject. No identifier is minted per login.
  Phase 03 owns durable subject/actor mapping and the identity domain.
- **Explicit matrix outcomes, G11.** Matching tenant/organization claims enable
  rows 6/9 on the two seed hosts. Rows 7/10/14 remain membership-dependent
  refusals under `DenyAllTenantMembershipReader` until Phase 03. Claimless valid
  principals retain public-only HostOnly authority and a bounded diagnostic;
  they gain no authenticated tenant authority.
- **One attributed rejection recorder.** Host/JWT disagreements use the existing
  operation key with captured source, actor, correlation and trusted organization
  attribution. [ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md)
  bounds durable authenticated admission per defended tenant; suppressed
  occurrences retain diagnostics and their refusal. Platform-host membership
  refusal has no confirmed host tenant and writes no tenant audit row.
- **Closed HTTP outcomes, G20.** Absent AccountSite credentials return masked
  `not_found` 404 on known tenant, organization and platform hosts. Present
  invalid/undecidable credentials return 401 on known hosts, including public
  routes. Authenticated tenant/organization disagreements use 404
  `tenant_mismatch`; anonymous assertions and membership refusals use
  `not_found`, as specified by ADR-0064.
- **Bounded authenticated surface, G14.** The additional validated-subject tier
  runs behind the unchanged visitor/physical-peer floors; it promises no
  end-to-end 600/minute allowance. While authorization remains a pass-through,
  the routable census admits the existing public reads and the exact physically
  read-only AccountSite request, with separate infrastructure exclusions.
- **Realm roles convey no LearnStack authority.** The three `tenant-*` roles and
  full scope on both named clients remain in the development realm. Assigned
  role claims are inert: `Keycloak_Roles_Are_Not_Application_Authority` holds the
  production claim-consumer boundary. Phase 03 supplies LearnStack permissions.

#### Realm issuance and warm reconciliation

The committed tenant realm issues the accepted UUID profile through a shared
client scope: tenant/actor/organization attribute mappers and the API audience
mapper serve the named API and confidential BFF clients. The three identity
attributes are admin-only in the user profile. Four stable seed actors agree with
`SeedData`; both tenant and organization host outcomes are asserted.

`make seed` and local `make demo` invoke the shared bounded reconciler. It upgrades
qualified historical warm realms, preserves existing accounts/credentials/MFA and
the Hub realm and reads back the owned state. Real warm-provider controls include
an independently observed zero-write repeat.
[ADR-0068](../decisions/0068-development-warm-realm-qualification.md) owns the
limited profile preparation and stock account-console scope detachment;
[ADR-0067](../decisions/0067-development-tenant-access-token-issuance.md) owns
development issuance lifetime. Fresh-file tests and real warm-provider tests are
separate proof subjects: changing an import alone cannot update an existing realm.
The pre-mapper/claimless baseline remains in the dated delivery records, not as a
current implementation claim here.

#### The BFF session

[ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md) and the
[Accepted five-step package](phase-02b-delivery-log.md#p02b-7-revised-decision-package-2026-10-23)
settle G13. [Security 11](../standards/11-security.md#tenant-bff-sessions) owns
ongoing custody/lifetime/admission rules. This packet delivers:

- Library-validated OIDC code/S256/state/nonce on the two exact tenant callbacks,
  with server-only credentials and per-transaction browser/host proof.
- One native process session owner, bounded source/subject/global admission,
  on-demand/single-flight refresh, effective expiry and actual provider extension.
- Host-only opaque cookies and exact Origin/CSRF checks; local revocation before
  provider logout, fenced late work and real history/cookie isolation proof.
- Closed authenticated AccountSite transport and guarded Studio/Portal shells,
  with a read-only app-local `auth()` facade and localized recovery/timeout UI.
  No permission, membership, Server Action or general proxy is introduced.
- Durable selected development provider events, qualified warm retention,
  preservation/readback/zero-write controls and explicit local-only audit gaps.
- Real warm-provider/BFF/API lifecycle plus both-host browser cookie/history
  proof; existing anonymous sites remain independent of stopped Keycloak.
- The route/feature-gate skills use the shipped facade when delivered in Step 4;
  they must not infer future Phase 03 permissions from an authenticated actor.

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

- **The delivery path owns its transaction and audit frame.**
  [ADR-0040](../decisions/0040-ambient-unit-of-work.md#consumers-do-not-have-a-request-and-still-need-all-of-this)
  assigns `Begin → SetTenantContext → handler → Commit` to this phase. P02b-3
  prepares the app-role transaction, restores validated event context, claims the
  inbox and enters the common audit owner before handler construction. Handler
  identity is classified even for a direct business write; an unregistered handler
  is refused. This closes the old accessor-only transport's missing audit frame.
  **G6's common-owner contract is Accepted under ADR-0057**; P02b-3 implements
  it in production delivery under ADR-0046. Its proof is a MUST-classified direct-write
  consumer,
  not only a consumer that sends a command.
- **The audit frame's owner on a non-pipeline entry point.** What is *already* decided
  is not reopened here:
  [ADR-0033 Amendment 2 § 2](../decisions/0033-audit-durability-model.md) and
  [ADR-0044 § 4](../decisions/0044-audit-write-path.md) both say only the owning frame
  writes, signals the boundary, reconciles and clears, and a joiner does none of it.
  ADR-0057 accepts common entry coordination for **G6**. `AuditLogBehavior` takes
  its owner
  flag from an audit-frame counter rather than from unit-of-work ownership, and
  [Audit Subsystem](../architecture/31-audit-subsystem.md) records that split as
  deliberate for the HTTP path — at step 3 no transaction is open to answer the
  question. P02b-3 explicitly opens the outer audit frame around subscriber execution;
  nested commands join it and cannot reconcile or clear before delivery completion.
  The shared transaction owner flushes MUST rows before commit, then the outer audit
  owner alone reconciles and clears. P02b-1's controlled fixture and P02b-3's actual
  consumer and direct-write proofs enforce ADR-0044/0033's common ownership.
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
  without another publication. P02b-3 Step 4 delivers the corresponding subscriber
  operation and its off-path registration with the writer.
- **Routine dispatcher bypass — Accepted ADR-0046.** The confined credential,
  exact grants, bounded diagnostics and atomic terminal audit explicitly exempt
  routine polls/claims/acknowledgements/retries from per-invocation security audit.
  Existing `EnterPlatformAdminScope` entry audit and all other bypass obligations
  remain unchanged. This is no general background-job exemption.
- **Attributed rejected assertions.** Admitted authenticated mismatches use
  captured actor/source/correlation and trusted organization attribution under
  ADR-0064/0065. The standalone audit refusal path preserves the HTTP refusal on
  store failure. Suppressed events and no-confirmed-tenant refusals remain
  distinct diagnostics rather than fictitious durable rows.
- **Causal actor and causation.** P02b-3 supplies the validated incoming event ID
  to consumer audit intents and follow-up outbox production; causal actor remains
  separate from effective SystemActor. Nested commands inherit these identifiers,
  while a newly drained domain event supplies its own causation ID. The Accepted
  **G6** producer/common-owner contract owns these bounded metadata keys.
- **Total module operation classification.** The sample flow's commands and
  subscriber operations are registered in their owning catalogues/matrices.
  `GetAccountSiteQuery` is explicitly `Off`, with no audit row or permission key.
  Selected provider authentication events belong to Keycloak under
  [Standards 18](../standards/18-audit-coverage.md#operation-types), not module
  handler registration; local BFF lifecycle has no durable LearnStack ledger
  under ADR-0070. Phase 03 owns the authenticated identity-event ingress/mirror.

The `AuditEntry` aggregate is owned by the **Audit** module and shipped in Phase 02a;
rows arrive through the store's parameterised inserts, and
[Phase 03](phase-03-identity-admin.md) registers the Identity module's catalogue source
and coverage matrix against it. See
[Audit Coverage Standards](../standards/18-audit-coverage.md).

### Compile-time secret leakage (carried out of Phase 02a Packet 3)

Runtime property/tag redaction and compile-time exception-message checking are
complementary. Property redaction does not scrub arbitrary exception messages;
producer, delivery and job stores separately retain only their accepted closed,
bounded failure descriptors rather than raw exception text.

[ADR-0032 Amendment 6](../decisions/0032-exception-handling-logging-and-observability.md#amendment-6--sensitive-exception-message-diagnostic-2026-10-25)
defines `LS0002`; its canonical rule and proof owners are in the
[catalogue](../standards/21-architecture-tests-catalogue.md#learnstackexception-sensitiveexceptionmessage-roslyn-analyzer).
The rule covers every production C# project, preserving `LS0001`'s narrower
Domain/Application scope. Both runtime and analyzer compile the same internal
word matcher and complete token sets. Synthetic compilations, loaded token
parity, a two-word-only source mutation, evaluated project wiring and actual
CI-warning builds prove their respective boundaries. Initial Warning severity
does not fail the build; Phase 03 exit promotes both rules to Error.

The diagnostic is name-based, with bounded expression forms. It does not prove
secret absence, trace earlier assignments or cover TypeScript/JavaScript. Review,
closed diagnostic contracts and durable failure-descriptor bounds remain
necessary. [Error Handling](../standards/09-error-handling.md#sensitive-exception-messages)
owns the ongoing exception-message rule and its limits.

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
  identifier-only payloads. The middleware-ordering rule, routable-set bound
  (**G14**) and inert Keycloak-role-claim controls are implemented under ADR-0064;
  `LS0002` is implemented under ADR-0032 Amendment 6 (**G16**).
- **Widened assertions** where a catalogued rule is satisfied by the defect it exists to
  catch. P02b-1 extends `Outbox_Row_Carries_Correlation_Context` to exact stored
  identity, tenant/organization, full traceparent and partition values, including
  unresolved provisioning. Its partition declaration rule combines independent
  stored-source IL checks, varied identifier probes and real persisted keys;
  an uninitialized instance is not a value proof. Publish ordering stays P02b-2.
  `Outbox_Claim_IsHeld_Until_Dispatch_Completes` replaces the former unconditional
  exactly-once assertion with live-lease contention, expiry/crash redelivery and
  stale-transition refusal; Step 3 implements the actual two-worker proof.
  P02b-4's accepted `BackgroundJob_Submission_Is_Transactional_And_Stamped` replaces
  `Hangfire_Job_Payloads_Include_TenantId` with tenant/org/actor/trace and atomic
  submission proofs under Accepted ADR-0047; its canonical owner and controls
  are Implemented.
  `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` gains the hanging-handler
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
  catalogue's and is wider than the paraphrase this document carried. The
  [accepted P02b-6 package](phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20) introduces no
  runtime SDK subject and hands its owning-phase cell to Phase 03's first runtime
  identity-provider adapter, triggered by actual SDK introduction. It remains
  Awaiting backfill there; direct HTTP does not qualify as SDK coverage.

Two notes on what "green in CI" can mean. Several of this phase's rows are behavioural
rather than structural, and CI runs those in a separate job under a Docker trait filter,
so "the architecture suite is green" does not reach them. `backend integration
(Testcontainers)` is already a required check; the
[P02b-0 receipt](phase-02b-delivery-log.md#p02b-0-entry-verification-2026-10-12) verifies all seven current
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
  DDL and consumer runtime are delivered by P02b-3; acceptance of the ADR alone
  did not claim implementation.
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
  including bounded immutable snapshots. P02b-3 Step 4 delivers the separate
  subscriber signals and registered, unmapped `delivery` health check.
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
  CSRF control, RP-initiated logout, and the ADR-0070 RP/owner wired in `apps/web` with the two
  skills' illustrative `auth` references replaced with the shipped helper.
- The first authenticated `/api/v1/*` read and the first guarded page, both in the
  OpenAPI document and the regenerated SDK.
- A Keycloak test fixture over the committed realm JSON and an in-process JWT issuer for
  the ADR-0036 token rows, both carrying the Docker trait where they need a container
  and both visible to the trait sweep.
- `LS0002` shipping as a Warning under its catalogued rule name, with its ADR-0032
  amendment, its catalogue entry, its `AnalyzerReleases` row, its linked token source
  and compiler, token-parity, mutation, wiring and real-build proofs.
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
[Step 4 local closeout](phase-02b-delivery-log.md#p02b-2-step-4--enforcement-and-closeout-2026-10-12).
P02b-3's
[Step 6 proofs](phase-02b-delivery-log.md#p02b-3-step-6--production-enforcement-and-pr-closeout-2026-10-13)
also cover the combined HTTP/event-delivery tracing criterion: actual committed
rows match the enqueuing activity, announced tenant/org and exact nonempty
partition. The
[P02b-3 merge closeout](phase-02b-delivery-log.md#p02b-3-merge-and-closeout-2026-10-15) owns final
combined event verification. Packet state lives in the Status block at the top of
this document; this section names criteria and the records that prove them.

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
- An audit row uses only the captures belonging to its intent's declaring frame
  and declared aggregate type. A captured root of another type does not reach
  that row's snapshots or changes, under
  [ADR-0044 Amendment 5 § 2](../decisions/0044-audit-write-path.md#2-entity_type-and-entity_id-come-from-the-intents-declared-aggregate)
  and [Amendment 6 § 3](../decisions/0044-audit-write-path.md#3-each-intent-keeps-its-own-requests-result-the-transactions-state-says-only-what-it-can).
  ADR-0057's common owner preserves these composition and frame rules.
- A consumer handling an event caused by user U writes a row whose metadata names U and
  the consumed event, and the follow-up outbox row carries that event as its causation.
- Every registered off-path dead-letter slug has a matrix row in the module that writes
  it; removing the row fails the build.

**Background jobs**

P02b-4 meets these criteria. Its
[merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) records final PR-source
CI and merge verification; its dated local receipts retain their original scope.

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
  row under the host's tenant while durable audit allowance is available, carrying
  a non-null actor, the request's correlation id,
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
- The realm export declares `tenant_id`, `user_id` and `organization_id` in its
  user-profile configuration with admin-only view and edit; a variant marking any
  of them user-editable fails the same protection predicate. A seed user's own
  account token cannot write/delete any of them, while ordinary edits succeed;
  registration cannot inject them into the stored account.
- Authenticated and id-addressed routes have tenant and organization isolation
  cases as `learnstack_app`: an identifier from another tenant or an invisible
  sibling organization reveals no resource, and each route has a visible-resource
  positive control. Host/JWT cross-check cases remain independently asserted.
- Completed provisioning yields one pending `TenantCreatedIntegrationEventV1`
  and zero on rollback. P02b-3 proves independent create-only `card@1`/`plain@1`
  delivery, sibling recovery, preserved authored/deleted definitions, semantic
  built-in identity verification and concurrent fresh seed/delivery convergence.
- Two principals behind one BFF do not share the additional subject quota. The
  actual whole-chain control uses a reduced subject budget below the unchanged
  visitor/peer floors: one subject exhausts it while the other's next request
  from the same peer succeeds. Separate production-floor controls prove the
  earlier IP/peer stages can still refuse validated subjects; no end-to-end
  600/minute allowance is inferred. The 429 has the canonical Problem Details
  shape and `Retry-After`; quota refusal precedes the durable assertion recorder.
- A second `make seed` against a Keycloak database created from the pre-mapper realm
  JSON leaves every existing account and the `learnstack-hub` realm intact, applies the
  client scope and the mappers, and yields a token carrying a UUID-valued `tenant_id`. A
  third run changes nothing.
- The development realm declares the exact accepted access-token lifespan with
  no override on either named client. Fresh signed expiration fits the observed
  UTC/monotonic code-exchange window plus that configured lifespan, under the
  accepted [TTL semantics](phase-02b-delivery-log.md#accepted-g12g13-answers). Independent configuration,
  client-override and wrong-expiration controls detect drift; shorter late-session
  issuance is covered separately. Exact `exp - iat` equality is not required.
- The callback route rejects a response whose `state` does not match the transaction
  cookie and one with no `code_verifier`; the session cookie is `HttpOnly`, `Secure`,
  `SameSite=Lax`, `Path=/` with no `Domain`; and no response body or script-readable
  cookie carries the refresh token. Asserting the header is not enough: on each real
  seed host a browser-level case shows the cookie **stored and sent back** on the next
  request through the retained native HTTPS launcher and manual hosts/trust setup
  that `P02b-0` re-verifies.
- Two concurrent requests with a near-expiry token produce exactly one token request at
  the issuer and one logical server session revision, independent of cookie response
  count. The fixed-size opaque format rejects malformed/duplicate/oversized cookie
  families; unrelated transaction cookies survive failed callbacks. A terminal
  `invalid_grant` revokes the session and
  serves the public page anonymously with a 200; logout ends the session at Keycloak and
  back-navigation does not restore an authenticated view.
- A cross-origin POST between the two demo hosts is refused, and so is a POST with a
  correct `Origin` and no session-bound token.
- With BFF explicitly enabled, an unauthenticated exact `/studio` or `/portal`
  request redirects to sign-in and public pages do not. Disabled mode returns
  the bounded sign-in-unavailable 503 without provider work.
- Source/subject/global admission and actual-settlement ownership satisfy
  [ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md) independently; canceled
  callers and abandoned/closing work cannot manufacture reusable capacity.
- A valid ordinary AccountSite 401 withholds checked content without changing
  idle expiry; later matching recovery reuses the owner. Initial and refreshed
  revision publication still require a positive witness under
  [ADR-0071](../decisions/0071-uncertain-account-site-refusals.md).
- Qualify the actual warm-provider/BFF/app-role API chain separately from the
  controlled-grant/API browser cookie/history proof. Neither stands in for
  Phase 06's full browser-driven OIDC journeys.
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
  tested as distinct states. Job dimensions are implemented under Accepted G9/G17;
  the [P02b-4 merge closeout](phase-02b-delivery-log.md#p02b-4-merge-and-closeout-2026-10-19) records final
  qualification, while Standards 10 owns the exact inventory.
- **No row in the [catalogue](../standards/21-architecture-tests-catalogue.md) whose
  Phase names 02b is still Registered or Awaiting backfill.** This includes the
  inherited nineteen P02b-4 obligations and the analyzer/inventory additions.
  Each owner runs in its actual required context: backend base/architecture
  behind `backend`, real database/provider/native work behind `backend integration`,
  frontend boundaries/behavior in `frontend`, and offline CI controls in `meta`.
  None reports skipped cases. Each structural rule has a planted offender and
  a companion that fails when its predicate is removed.

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
- **Tenant context is forgotten in a background job.** Opaque Hangfire hints cannot
  establish tenant authority. The P02b-4 production census and real `learnstack_app`
  execution proofs must keep restoration tied to the validated durable job row
  before admission and handler construction.
- **The outbox is treated as optional** — "module X will adopt it later." No reflection
  scan can observe whether a row committed with the change that raised it, so the
  mitigation is an integration test that commits and rolls back both together,
  registered as a new catalogue row rather than assumed to exist.
- **Authentication goes live with authorization still a pass-through.** Between this
  phase and Phase 03 authorization does not enforce permissions. G14's closed
  read-only routable set and its census prevent that gap from exposing arbitrary
  request types; the additional subject limits bound admitted assertion traffic;
  ADR-0065 separately bounds aggregate audit admission per defended tenant.
- **The access-token TTL is the revocation window.** Before Phase 03, matching
  host/claim rows 6/9 do not re-read live membership and
  `AuthorizationBehavior` is a pass-through, so a token can retain that authority
  for its remaining lifetime after the backing attribute is removed. The
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
- **Provider events are mistaken for a durable session ledger.** Selected
  development events have finite retention and asynchronous expiry, not a row/
  disk capacity bound or atomic application-audit guarantee. They cannot witness
  local expiry or logout during a provider outage. Phase 03 owns the authenticated
  identity-event ingress/mirror; Phase 11 owns production retention and capacity.
- **A local BFF is mistaken for distributed continuity or outage immunity.**
  Restart loses sessions and shared global/source/subject caps can refuse new
  sign-ins across tenants. Refresh/publication still needs positive API/provider
  authority. ADR-0071 retains an uncertain ordinary 401 only until the existing
  refresh/deadline rules apply; masked 403/404 can still revoke. Phase 11's
  distributed adapter/proxy qualification precedes multiple web processes.
- **Keycloak claims leak into authorization decisions.** Keycloak authenticates;
  LearnStack authorizes. Development role claims remain present, so the
  implemented `Keycloak_Roles_Are_Not_Application_Authority` census must continue
  to reject their use as permission or membership authority.

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
- Background-job submission rejects missing owner/context, unknown definitions and
  invalid identifier-only parameters. A job runs under tenant/organization authority
  restored from its real durable row; opaque transport hints carry no tenant payload.
- Both Phase 02d sites still render anonymously with Keycloak stopped.
- `LS0002` ships as a Warning with its record, its catalogue entry and its release row.
- No catalogue row whose Phase names 02b is still Registered or Awaiting backfill; both
  CI checks are green with no skipped case in the assemblies this phase touches; and the
  behavioural job is a required check, with the branch-protection date recorded.

[Phase 02c](phase-02c-hub-foundation.md) is unblocked by this phase but does not gate
it. Phase 02c hangs off the spine and starts only when its own trigger fires — a tenant
that must be billed or plan-gated
([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)).
