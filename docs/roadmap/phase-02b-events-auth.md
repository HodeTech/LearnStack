# Phase 02b: Events, Background Jobs, Identity, and Session

**Current status — 2026-10-18.** P02b-0 through P02b-3 are complete.
P02b-2 is merged through [PR #30](https://github.com/HodeTech/LearnStack/pull/30);
its [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns final verification.
P02b-3 is merged through [PR #31](https://github.com/HodeTech/LearnStack/pull/31);
its [merge closeout](#p02b-3-merge-and-closeout-2026-10-15) owns final verification.
All six steps and the verified review corrections are delivered, with both
independent review rounds complete.
The [external review follow-up](#p02b-3-external-review-follow-up-2026-10-15)
records verified corrections and dispositions against the reviewed PR head.
The [second follow-up](#p02b-3-second-external-review-follow-up-2026-10-15)
owns the subsequent runtime and proof corrections.
P02b-4's [decision package](#p02b-4-decision-package-2026-10-15) and ADR-0047 are
Accepted — 2026-10-15. Nine implementation steps are authorized on `development`;
P02b-4 Step 1 implements contracts and ordinary execution guards; its
[record](#p02b-4-step-1--contracts-and-authority-2026-10-16) owns validation and
review status. Step 2 is complete under ADR-0058, Accepted — 2026-10-16;
its [closeout](#step-2-final-proof-and-closeout) records both fresh review rounds,
critical-fix verification and passing regression. Step 3 submission/admission is
complete; its [record](#p02b-4-step-3--submission-and-app-role-admission-2026-10-16)
owns both review rounds and passing 4,152-case backend verification. Step 4's
[lifecycle acceptance](#maintainer-acceptance-and-step-4-adaptation-2026-10-16) and
ADR-0059 are Accepted — 2026-10-16. Step 4 completes owned lifecycle and API
composition qualification under Accepted ADR-0060; business execution stays Step 5.
The [required-startup qualification](#step-4-required-startup-qualification-2026-10-16)
records ADR-0060's reviewed revision; its
[acceptance](#adr-0060-acceptance-and-step-4-continuation-2026-10-16) on 2026-10-16
authorizes dependent fatal/lifecycle implementation. The
[Step 4 closeout](#full-step-4-round-2-correction-validation-and-closeout-2026-10-16)
records passing qualification and both full review rounds with verified corrections.
Steps 4–6 are locally complete after both full review rounds and verified
corrections. The [Step 6 record](#p02b-4-step-6--tenant-capacity-reservations-2026-10-16)
owns capacity delivery and final local qualification. Step 7 external stages are
committed under Accepted ADR-0061; both full review rounds and verified corrections
are complete. Its
[record](#p02b-4-step-7--external-stages-and-recovery-2026-10-17) owns current evidence.
Step 8 durable recurrence and restricted fan-out is locally complete after both
full review rounds and verified corrections; its
[closeout](#step-8-full-review-round-2-2026-10-18) owns final qualification. Step 9
enforcement and P02b-4 are locally complete; the
[local closeout](#p02b-4-local-closeout-2026-10-18) owns exact qualification and
remaining PR/CI/merge work. P02b-5–8 remain pending. Production business and
recurrence definitions remain empty.
[ADR-0062](../decisions/0062-native-crash-proof-ci-provisioning.md) is Accepted —
2026-10-18. Its [CI acceptance](#adr-0062-acceptance-and-ci-implementation-2026-10-18)
authorizes the bounded native provisioning/partition follow-up; implementation
and exact-head hosted qualification remain in progress.
[ADR-0063](../decisions/0063-complete-bounded-ci-test-shards.md) proposes a bounded
CI topology after actual hosted backend/ordinary budget failure. Its
[review revision](#adr-0063-review-revision-2026-10-18) owns the revised proposal
and current source-specific evidence. Native passes on `3553ff16`; the separate
ordinary membership-I/O fault remains under ADR-0062. No dependent shard
implementation is authorized before maintainer approval.
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
P02b-3 was pending at that decision pass; its current implementation and review
status is recorded in [Step 6](#p02b-3-step-6--production-enforcement-and-pr-closeout-2026-10-13).

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
rounds complete. P02b-3 delivers `IInboxGuard` and `inbox_messages` as shared
transport infrastructure under ADR-0046. P02b-4 Step 2 delivers the Hangfire pins
and guarded storage; Step 4 retains vendor worker activation. The realm's
protocol mappers remain P02b-5 work. Packet 5's earlier `IInboxGuard` references
were planned comments, not an interface declaration.

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
  [ADR-0047](../decisions/0047-transactional-background-job-runtime.md) and
  [P02b-4's package](#p02b-4-decision-package-2026-10-15) are Accepted — 2026-10-15;
  all nine implementation steps are locally complete; the
  [local closeout](#p02b-4-local-closeout-2026-10-18) owns qualification and
  remaining PR/CI/merge work.

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
| **P02b-3** | **Complete and merged — [PR #31 closeout](#p02b-3-merge-and-closeout-2026-10-15).** Consumer: durable acceptance, shared inbox/ledger DDL and `IInboxGuard`, the common per-delivery transaction, both real default subscriptions and Seeder handoff, independent attempts and audited terminal state, targeted internal ledger replay | G1, G6, G7 Accepted; P02b-2 |
| **P02b-4** | **Locally complete — [local closeout](#p02b-4-local-closeout-2026-10-18).** Background jobs: accepted H1 transactional/external profiles, confined storage/wakeups, tenant capacity and recurrence/fan-out; all nine implementation steps and their review gates delivered; no production job/recurrence definition | G9 Accepted 2026-10-15; P02b-3 complete |
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
| G1 | What is the subscriber's fate when its handler exhausts retries, and what is the inbox table — one shared or per module, keyed `event_id` or `(tenant_id, consumer, event_id)`, claimed by the handler or by the transport? Three arms travel with it: the class of consumer whose effect cannot share the inbox transaction (an external call, the platform-scope erasure handler the Audit spec already carries) and what "effectively once" means for it; whether an event without the organization marker is delivered tenant-wide regardless of the producing request's organization; and whether the inbox entity joins [ADR-0044 § 7](../decisions/0044-audit-write-path.md)'s named audit-capture exclusion list once a delivery declares intents | One shared tenant-owned `inbox_messages` keyed by tenant/stable subscription/event, transport-claimed before construction; a separate delivery ledger owns retries, terminal state and targeted replay; completion shares the business/audit/outbox transaction | **ADR-0046** + a dated **ADR-0006 Amendment** (or an explicit supersession note in ADR-0046 naming Amendment 1's consumer-side paragraph, which fixes the per-module inbox) + a dated **ADR-0010 Amendment**, because its § Idempotency makes two things mandatory that the shared-table leaning changes — the inbox is "the module's **own** inbox table", and the guard is called by the consumer — + the `Integration_Event_Handlers_Use_InboxGuard` assertion re-written in the same record to name whoever ends up owning the call, since a transport-claimed delivery fails a structural rule that looks for the call inside the handler + Database Standards § Inbox + an ADR-0041 erratum beside ADR-0023's minting list | P02b-2 (the record and its amendments: the dispatcher's retry state and a failed delivery's next attempt are one state machine), P02b-3 (Database Standards § Inbox and the ADR-0023 erratum) | Accepted — 2026-10-12; decision/amendments delivered; P02b-3 Steps 1–6 implement subscriber runtime, inbox, defaults and production enforcement; complete and merged; [merge closeout](#p02b-3-merge-and-closeout-2026-10-15) owns final verification |
| G2 | Does `IOutbox.EnqueueAsync` write the row immediately on the ambient transaction, or buffer for `OutboxFlushBehavior` at step 7 — and what is the flush point for the two paths that have no step 7? | Immediate write, with the behavior asserting no buffered message survives the handler | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 2's step-7 completion role, with a dated supersession disclosure in ADR-0032 under this acceptance; phase-doc + [Events and Outbox § Producer pattern](../architecture/15-event-and-outbox.md#producer-pattern) | P02b-1 | Accepted — immediate enqueue/owner completion |
| G3 | What replaces the unconditional same-key publish-order promise — a scoped bound, or strict per-key head-of-line blocking — and is the key's derivation checkable structurally, given that [§ Ordering](../architecture/15-event-and-outbox.md#ordering) admits an aggregate id or a deliberately declared tenant-wide key and nothing else? | Scope it: no column in `outbox_messages` carries an unconditional per-key guarantee, so the honest bound is one processor, no failure, deterministic eligible selection; no enqueue/commit-order guarantee | Derivation: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md). Ordering: Accepted ADR-0046 and the P02b-2 package, followed by [§ Ordering](../architecture/15-event-and-outbox.md#ordering) alignment under this acceptance | P02b-1 (whether the key's derivation is checked structurally: the first event declares a key), P02b-2 (the ordering bound) | Derivation and ordering Accepted; scoped ordering runtime [implemented in P02b-2](#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12); complete and merged; [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state |
| G4 | What counts as an attempt, what is `MaxAttempts` bound to, and what marker keeps a terminal row terminal as the clock advances? | Six attempts (one immediate plus the five documented delays) and an explicit terminal column, because a pushed-out `available_after` stops being terminal and never leaves the pending gauge | Accepted ADR-0046 + dated ADR-0006 disclosure under this acceptance; Database Standards § Outbox DDL, index and grant edits | P02b-2 | Accepted — 2026-10-12; producer attempts/terminal state [implemented](#p02b-2-step-2--durable-transitions-and-terminal-audit-2026-10-12) |
| G5 | Does the stored event identity stay the assembly-qualified type name with declared compatibility mappings, and what happens to an unresolvable type, a retired type and an event with no subscriber? And what is the **row's** identity — a surrogate `id` plus an `event_id` column, or `EventId` as the key — given that two documents list an `event_id` the canonical DDL does not have? | Keep the type identity, add a bounded resolver reading through `PayloadJsonOptions` and declared mappings; dead-letter a retired type at once rather than spending its attempt budget; reconcile the row's column list in one direction | Stored identities: [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) bounded supersession of ADR-0032 Sub-decision 12 and the Phase 02b deliverables table in Implementation Notes, with a dated supersession disclosure under this acceptance; Database Standards alignment. ADR-0046 accepts declared resolution, Pending holds and intentional absence; dispatch runtime remains P02b-2 | P02b-1 (the stored type identity and the row's identity, both written at enqueue), P02b-2 (unresolvable, retired and unsubscribed types) | Stored identities and resolution outcomes Accepted; resolver/readiness runtime [implemented](#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12); both real durable subscriptions Active in P02b-3 Step 5 |
| G6 | When the unit-of-work owner is not an `AuditLogBehavior`, who performs the four owner-side audit acts and enters the outermost audit frame — and does a state-mutating delivery declare its own intents, keyed by handler type? | The transport opens the outermost frame beside its transaction; a delivery declares its own intents and an unregistered handler is refused as an unregistered request is | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md): bounded ADR-0040 nested-failure supersession; separately enumerated additive UoW read-seam amendment; clarifying ADR-0044/0033 common-owner amendments. G1-dependent production delivery/inbox implementation remains P02b-3 | P02b-1, P02b-3 | Producer/common owner delivered; delivery/inbox contract Accepted; common owner and execution implemented by P02b-3 Steps 2–3 |
| G7 | For a dead-letter transition: what class, what `OperationType`, what slug, on which transaction, in what order against the state change, with what failure posture — and may a second non-MediatR caller reach `IAuditStore.WriteStandaloneAsync`? | MUST `security-event`; producer `platform.outbox.dead_letter` and reserved P3 `platform.delivery.dead_letter`; fenced terminal transition and audit INSERT on the same short transaction through a narrow fifth write seam; failed audit retries terminalization without publication | Accepted ADR-0046 + bounded ADR-0044 audit-grant replacement, separate additive fifth-writer seam amendment and ADR-0033 same-transaction clarification under this acceptance; `DeclareOffPath` registration and Audit Coverage baseline | P02b-2 (the producer dead-letter row its criteria assert), P02b-3 | Accepted — 2026-10-12; producer terminal audit [implemented](#p02b-2-step-2--durable-transitions-and-terminal-audit-2026-10-12); subscriber terminal audit implemented by P02b-3 Step 4; job part Accepted — 2026-10-15 under ADR-0047: `platform.job.dead_letter`, actual-row sixth write and same-transaction MUST audit; job terminal/MUST audit implemented; [P02b-4 local closeout](#p02b-4-local-closeout-2026-10-18) owns qualification and remaining PR/CI/merge work |
| G8 | Does the dispatcher's `BYPASSRLS` use carry a per-invocation `security-event` row, or is it exempt — and on what written terms? | Exempt, on three terms: the bypass is grant-bounded and code-path-confined, dispatch attempts are logged rather than audited, and the dead-letter transitions are the audited events | Accepted ADR-0046 explicitly replaces the routine-dispatch coverage rule and bounds the ADR-0003 grant extension; dated disclosures under this acceptance, not an ADR-0041 correction | P02b-2 | Accepted — 2026-10-12; confined dispatcher exception/census [implemented](#p02b-2-step-4--enforcement-and-closeout-2026-10-12); complete and merged; [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state; job/recurrence part Accepted — 2026-10-15 under ADR-0047, conditional on exact grants/censuses, diagnostics, observation and terminal MUST audit; job/recurrence confinement, diagnostics, observation and terminal MUST controls implemented; [P02b-4 local closeout](#p02b-4-local-closeout-2026-10-18) owns qualification and remaining PR/CI/merge work |
| G9 | Which record decides the background-job runtime — its storage under the closed four-role model, the single tenant-context writer, the job frame's transaction owner, the enqueue-site rule *and who may enqueue whose job type*, the retry contract (whose class, slug and posture adopt **G7**'s), the tenantless platform-job class a purge needs, the ADR-0035 classification, the queue grammar, and the package pins with their licence verdict? | One record answering all of it; business execution uses the existing announced request owner through `ISender`, retaining the eight setters; the chosen storage/scheduler authority is separately explicit | **ADR-0047** + a dated **ADR-0003 Amendment** for any schema/role/grant-confinement extension (not merely a new tenant table under the existing policy) + a dated **ADR-0036 Amendment** disclosing the selected context-writer contract; then Standards 05/20. A decision change is not an erratum | P02b-4 | Accepted — 2026-10-15 under ADR-0047 and the nine-step P02b-4 package; bounded prior-ADR Amendments accompany acceptance; runtime and all nineteen canonical obligations implemented; [P02b-4 local closeout](#p02b-4-local-closeout-2026-10-18) owns qualification and remaining PR/CI/merge work |
| G10 | Does the active tenant travel as a scalar `tenant_id` claim, a `memberships` array, or both — and what value does `UserId` hold? | The scalar claim, whose 02b source is an admin-set user attribute; a second UUID-valued `user_id` attribute for the actor, with `sub` staying Keycloak's subject | Phase-doc: [ADR-0036 § What is out of scope](../decisions/0036-tenant-resolution-trusted-inputs.md#what-is-out-of-scope-and-what-is-not) hands the shape to this phase by name. Reconcile `architecture/13`'s array sketch in the same change | P02b-5 | Open |
| G11 | Which [ADR-0036 matrix](../decisions/0036-tenant-resolution-trusted-inputs.md#the-reconciliation-matrix) rows resolve in this phase, given that row 10 consults a reader that denies everyone — and what row covers a signature-valid token carrying no `tenant_id` claim on a tenant host? | Either mint `organization_id` scoped to the host's organization, which makes row 10 need no membership read, or declare the organization host's authenticated 404 and test it as the expected outcome | A dated **ADR-0036** amendment plus errata beside § Staging across packets and the 2026-09-02 erratum, both of which assign row 10 to this phase without noting that its `M covers (T, O)` term is denied until Phase 03 — which groups it with rows 7 and 14 rather than with 6 and 9 | P02b-5 | Open |
| G12 | Which Keycloak client does the BFF exchange the code with, where does sign-in happen across two tenant hosts, and how do the client scope, the mappers and the seed users reach a Keycloak database that has already consumed the import? Native HTTPS on retained `*.learnstack.local` hosts, with manual hosts/trust setup, is delivered by Phase 02d and re-verified by P02b-0 | A confidential BFF client with one redirect URI per seed host, retaining native HTTPS and `Secure` cookies; and an idempotent reconciler inside `make seed` — a mapper added only to the import reaches no existing workstation | Phase-doc + the realm JSON + `scripts/seed.sh` + `infra/keycloak/README.md`; a new ADR only if the reconciler becomes ADR-0004's `IIdentityProvider` | P02b-0 re-verifies [Phase 02d G32](phase-02d-walking-skeleton.md#the-decision-register), ADR-0053/0054 and ADR-0055; P02b-5 (which client the BFF redeems the code with: the API pins `azp` against it), P02b-6 | Open |
| G13 | What is the BFF session — its custody of the refresh token, its store, its idle and absolute lifetimes, its refresh serialisation under concurrent requests, its terminal `invalid_grant` behaviour, its CSRF control and its logout — and which document owns the access-token lifetime? | The `HttpOnly` cookie Security Standards already describes, encrypted by the session adapter, read only by server code, with single-flight refresh and one authority for the TTL | Phase-doc + one reconciled [Security Standards § Authentication](../standards/11-security.md#authentication); a **new ADR** only for a server-side session store | P02b-6 (which document owns the access-token lifetime: the realm export and its reconciler write it), P02b-7 | Open |
| G14 | What bounds the request types reachable over HTTP while `AuthorizationBehavior` is `return next()`, and what is the shape of the token-keyed rate-limit stage behind a shared BFF connection? | An enumerated read-only routable set held by a catalogue-registered rule, and a limiter stage after `UseAuthentication` keyed on the validated subject. The early anonymous stage already runs before authentication: P02d-5 delivers 60/min per canonical direct or authenticated-hop visitor IP plus 600/min peer acquisitions/allocations under ADR-0053 and ADR-0054. Exhausted known visitors refuse before peer debit; unknown visitor allocation remains peer-gated. These remain IP/peer quotas, not token quotas; a request they reject never reaches the token stage. Re-verify the [P5 remediation decision](phase-02d-walking-skeleton.md#p02d-5-external-review-remediation-2026-10-09) before choosing authenticated accounting, preserving pre-classification protection against unknown-host abuse | Phase-doc + catalogue registration; a dated ADR-0032 Amendment for a deny-by-default step instead | P02b-5 (the token-keyed stage and the early stage's treatment of validated callers); G34's anonymous-hop policy and ADR-0054's bounded accounting replacement are Accepted and delivered in P02d-5 | Open |
| G15 | What initiates a replay in this phase? | Targeted internal subscriber replay in P02b-3; producer/operator reset and fencing require a separate Phase 03 contract | Phase-doc; the URL space is its own decision record, per ADR-0036 | — (answered in § The consumer side; recorded so the question travels with the deferral) | Accepted — internal subscriber replay implemented by P02b-3 Step 4; operator contract Phase 03 |
| G16 | What is `LS0002`'s rule name, which project trees does it run over, what does it inspect, and what escalates it to Error? | The name and scope recorded the way Amendment 1 recorded `LS0001`, with the scope reaching the assemblies that actually handle tokens | A dated **ADR-0032** amendment + a catalogue entry + an `AnalyzerReleases` row | P02b-8 | Open |
| G17 | What are the label sets for the event and job metrics, is the tenant axis permitted, and is it spelled `tenant` or `tenant_id`? | One note in the document that owns metric names, no raw tenant label on a per-event series, and an oldest-eligible-row age metric separate from the pending count | [Observability Standards § Required Metrics](../standards/10-observability.md#required-metrics) edit | P02b-2 (the first series ships there, and a label added afterwards touches every emitter already written) | Event metrics Accepted — 2026-10-12; runtime/guards [implemented](#p02b-2-step-4--enforcement-and-closeout-2026-10-12); complete and merged; [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns CI/review/merge state; job label/observation contract Accepted — 2026-10-15 under ADR-0047; job instruments/guards implemented; [P02b-4 local closeout](#p02b-4-local-closeout-2026-10-18) owns qualification and remaining PR/CI/merge work |
| G18 | May an integration-event payload or a job argument carry personal data, what may `last_error` contain, and do the four new durable stores enter the erasure scope? | Identifiers only, a sanitized and length-bounded failure descriptor, and the stores named in the erasure inventory | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) decides the new producer payload/NULL JSON metadata policy and application byte cap; a dated ADR-0038 cross-reference under this acceptance. [Data Protection § Right to Erasure](../architecture/23-data-protection.md#right-to-erasure-right-to-be-forgotten), P02b-8 and Phase 11 retain erasure ownership | P02b-1 (the payload rule: the first payload ships there), P02b-2 (the `last_error` rule and its DDL bound), P02b-8 (the erasure inventory) | Payload and failure descriptors Accepted and implemented; job identifier-only payload/checkpoint and failure-descriptor bounds Accepted — 2026-10-15 under ADR-0047; job payload/checkpoint/failure bounds implemented; [P02b-4 local closeout](#p02b-4-local-closeout-2026-10-18) owns qualification; erasure inventory open P02b-8 |
| G19 | Where do the sample flow's publisher and consumer live — a fixture pair in the integration assembly, or a real event a shipped module consumes? | A real event between two shipped domain modules, because a test-assembly-only consumer leaves every module-scoped sweep with zero subjects | [Accepted ADR-0057](../decisions/0057-transactional-event-production-and-completion.md) and the P02b-1 package; reconcile Tenancy/Customization/Audit specs under this acceptance. ADR-0046 accepts distinct subscription deduplication and envelope delivery scope; P02b-3 implements the two effects and Seeder handoff. The provisioning publisher must prove enqueue from its transaction announcement while ambient context remains unresolved | P02b-1 | Flow and delivery contract Accepted; subscriptions/semantic Seeder handoff implemented by P02b-3 Step 5 |
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

### P02b-3 Step 5 — useful defaults and semantic seed handoff (2026-10-13)

**Complete; both independent review rounds complete.**
Step 4 is closed before these changes. This step implements the accepted pair of
one-root initializers and shared transaction-owned concept serialization, then
moves built-in seed verification to semantic identity. Authored identities and
exact pins retain their existing contract. Both production subscriptions activate
together only after the initializer and Seeder paths are ready.

The initial implementation supplies two Off-classified subscriber coordinators
and two MUST-classified initializer commands. Both production manifests are Active
and bind the discovered handlers exactly. Each untouched concept is published
before its single save, with one generated UUIDv7 identity, generation bump and
resource audit. Every existing lifecycle/revision is preserved under the same
transaction-owned concept lock used by ordinary registration.

Seeder discovers the exact Active built-in by key, verifies its full shape and
retains the discovered identity. Authored identities/pins remain strict. Fresh
seed inventory retains all definitions/content but uses six customization
generation bumps per tenant and 86 audit rows, replacing the earlier two-step
register/publish treatment of four built-ins. The P02d seed receipts remain
historical evidence of their original behavior.

Initial validation: full CI-mode Release build has zero warnings/errors; all
1,862 unit and 298 architecture cases pass. Focused app-role integration passes
24 initialization cases, 83 Seeder/audit/projection/manifest cases and eight real
API delivery cases, all with zero failures/skips. The real flow covers stored
backlog, fresh provisioning, audit-failure rollback/recovery, duplicate acceptance
and Seeder/subscriber races for both concepts in both winning orders. Test-owned
database gates coordinate real writes without a production registration hook.
The race observer uses the app role so PostgreSQL exposes its own role's lock
waits; the earlier owner-role observer could not see app-role activity and failed
the fixture, rather than detecting a product defect. The corrected full flow
passes all eight cases. The existing publication concurrency suite also passes
all ten cases, and the guarded frontend run passes 1,616 cases with zero
skips/todos. Two fresh sequential review receipts follow before Step 6.

#### Step 5 review round 1

Three fresh read-only reviewers examined `94d7d6b..d53ae27` before the second
round. GPT-6-astra xhigh reviewed production correctness, isolation and races;
GPT-6.1-sol xhigh reviewed proof quality and Seeder convergence; both approve with
no findings. They independently reran 92 integration cases and 100 focused
integration/unit cases respectively, all passing with zero skips. These overlap
root's receipts and are independent reruns, not additional distinct test cases.

GPT-6-sol high identified four mutable Architecture 31 passages still describing
real defaults as Pending, and the standards-check skill still describing inbox
acquisition as pending. Root verified both against the Active bindings and owner
path, corrected those current-state carriers and extended the phasing table to
Step 5. The historical ADR and Step 1–4 receipts remain unchanged. Root's updated
document check passes 2,209 relative links and 480 fragments with no errors;
the reviewer independently passes all five corpus guards. No runtime correction
was needed. Missing full-packet ADR delivery notes are not a Step 5 finding:
Step 6 owns them after production-census enforcement and final regression.

#### Step 5 review round 2

Fresh GPT-6-astra high and GPT-6.1-sol high reviewers independently reviewed
`94d7d6b..0302d8a`, including implementation and the R1 correction. Both approve;
no runtime finding remains. The runtime reviewer independently passes 92 focused
integration and 97 unit cases, with zero failures/skips. The proof/corpus reviewer
independently passes 92 integration cases and all five corpus guards, including
the final correction below. These are overlapping independent reruns.

The second reviewer found the Pending-policy catalogue entry still naming the
renamed production-composition test and calling actual subscriptions Pending.
Root verified the surviving test and explicit Pending fixture registrations,
then corrected the reference and separated the policy proof from current Active
bindings. The reviewer rechecked that correction and the 198/120/78 catalogue
counts with no open finding. Release binaries are unchanged. Root's document
check has zero link/fragment/width errors and preserves earlier receipts and ADR
text. Step 5 is closed. Step 6 follows with production censuses, remaining current
carriers, full regression, demo/Lighthouse and PR-head CI verification.

### P02b-3 Step 6 — production enforcement and PR closeout (2026-10-13)

**Implementation and both independent review rounds complete.**
[PR #31](https://github.com/HodeTech/LearnStack/pull/31) is open for maintainer
review; merge remains pending.
Step 5 is closed at `ac7bbee` before these changes. This step completes nonempty
production handler/manifest and call-path controls, extends aggregate/audit
coverage to integration handlers, reconciles current carriers and performs full
Release regression plus the real shared demo/Lighthouse lifecycle. The earlier
five delivery/review receipts remain unchanged.

The authorized Hub dependency pointer is reconciled in the existing sibling
`docs/hub-agent-handoff` branch at `dd243a0`: its P02c-3 dependency now links to
ADR-0046's shared transport-owned inbox. This is a local documentation-only commit;
no Hub runtime, HTTPS surface, branch change, push or PR is part of that update.
The Hub track remains frozen under its existing owner decision.

#### Step 6 implementation and local proof scope

The canonical inbox rule now discovers concrete integration handlers in every
production assembly/layer, compares inherited/explicit event contracts with the
actual API's two scoped Active bindings, and checks typed construction,
invocation, generic DI, helper, async and delegate capabilities. Missing assembly,
handler, registration and actual invocation subjects fail the same predicates.
The lifecycle companion pins exact typed unit/frame and raw transaction
completion owners, including site multiplicity and missing-owner controls.
These checks do not infer arbitrary reflection, raw SQL transaction control or
control-flow dominance; the app-role behavioural suites own execution order and
atomicity. They introduce no production runtime hook.

Audit discovery now fails on assembly/type load errors and classifies each
concrete handler, rather than its shared event. Actual API/Seeder catalogues must
include both Off coordinators even when their operation entries are unchanged;
an omitted-handler control and integration-only module control can fail.
The aggregate constructor-port census includes inherited integration handlers,
fused ports and separate-root controls. It does not infer nested ISender
request dataflow; the real default effects supply the one-root runtime proof.

The canonical restored-context test checks constructor/callback timing, inbox,
prior context cleanup, effective SystemActor and separate causal human identity.
It also reads the committed outgoing trace, tenant/org and exact nonempty
partition. A new test-only HTTP controller sends the existing provisioning
command through the real API pipeline and production outbox, then reads the
committed quartet from a separate app-role connection. This proves producer
tracing, not authorization or admission of a new production endpoint.

Self-check corrected test-harness overload/fixture lifetime issues without
weakening assertions. The first full architecture run reported the new partial
HTTP test file missing a source-visible Docker trait; the runtime class already
inherited it from its other part. The method now declares the trait explicitly.
No failing or partial run is counted as passed evidence.

Local Release proof receipts:

- Build: zero warnings/errors; solution-wide format verification passed.
- Unit: 1,862; contract: 4; full integration: 1,550, all passed with zero skips.
  The final integration receipt includes the new HTTP and strengthened delivery
  quartet proofs; its subsequent source change adds only the explicit method
  trait. The final full architecture rerun passes all 311 cases, zero skips;
  the source convention requires the shared `RequiresDocker` constants. The
  combined backend receipts total 3,727 passing cases. Catalogue self-checks
  confirm 218 architecture methods and 200 Implemented rules: 121 in that
  assembly and 79 elsewhere. Five focused HTTP/context/composition controls
  also pass on the final candidate build.
- The unchanged frontend passed its guarded 1,616-case suite, lint and typecheck
  in Step 5; those receipts remain applicable, without claiming a Step 6 rerun.
- A fresh isolated development clone at `ac7bbee` ran the full shared demo. Step 6
  changes only tests/docs, so its production runtime is identical. The proof
  finished in 580,109 ms with 27 JSON/HTML report pairs, nine URLs and three runs
  each: maximum normalized gzip JS 106,573 bytes, maximum CLS 0 and every
  automated accessibility score 1. Nine timing advisories remain warnings, not
  production p95 claims. Cleanup passed and every originally running external
  container was restored. This local receipt is not an exact final-PR-head CI
  claim; the required CI job reruns the actual head.
- Relative document links/fragments and 88-column prose checks pass. Earlier
  P02b delivery/review receipts and Accepted ADR bodies are byte-preserved;
  authorized dated delivery notes are additive. ADR parser/disclosure tests: 7.

#### Step 6 review round 1

Three fresh independent agents reviewed `ac7bbee..82623d8`: GPT-6-astra/xhigh
covered typed invocation/lifecycle capabilities; GPT-6.1-sol/xhigh covered
aggregate/audit discovery, actual roots and HTTP/delivery proofs; GPT-6-sol/high
covered the corpus and governance. Independent runs passed eight capability
cases, twenty aggregate/audit cases and five actual root/HTTP/context cases.
The whole backend/demo receipts above were inspected, not independently rerun.

They found four enforcement gaps: a same-simple-name integration handler could
hide inside the provisioning exception; a handler could inherit implementation
from a base without the interface; a closed generic helper could hide typed DI
resolution; and EF transaction completion APIs could bypass the typed lifecycle
census. Root read the actual predicates and independently reran both compiled
scratch reproductions, confirming dirty cases were missed and matched controls
were detected. No current production-runtime defect was demonstrated.

The fix pins the exact provisioning Type and adds independently failing name
collision controls. The capability fixes map inherited implementations, follow
closed generic helper specialization to known DI capabilities and enumerate EF
transaction/facade completion APIs. Existing Facts gain compiled dirty/clean
controls without increasing counts. Root's corrected Release build has zero
warnings/errors and all nine focused guard cases pass, including independent
same-name and recursive/ordinary generic controls. Rerunning the original
compiled capability probe now detects both inherited/generic misses and every
EF completion case, while the actual production baseline remains clean. The
first fix build caught instance-only probe methods that used no instance state;
their instance task fields preserve the intended inherited call shape without
an analyzer waiver. Full architecture passes all 311 cases with zero skips;
solution-wide format verification and document checks pass. The fix changes
tests/docs only; prior full unit/contract/integration and demo receipts retain
their explicitly recorded scope.
Current README, processor and skill guidance also retained delivered-as-pending
claims; those verified carriers are corrected. The new ADR-0046 delivery note
stays only in its dated Amendment 3, with mutable-index navigation; no new top
navigation is inserted into the Accepted body.

#### Step 6 review round 2 and local closeout

Three new independent agents reviewed the full `ac7bbee..84995d3` Step 6 range:
GPT-6-astra/xhigh covered capability/lifecycle enforcement; GPT-6.1-sol/xhigh
covered audit/aggregate/root/tracing proofs; GPT-6-sol/high covered the corpus.
The technical slices approve without an actionable finding. Independent runs
pass eight capability cases, twenty aggregate/audit cases and five real root/
HTTP/delivery cases, all with zero skips. A separate compiled probe detects
seven additional dirty generic/inherited/async/iterator/closure/local-function/
permutation shapes and leaves two unused-handler-argument controls clean.

The corpus reviewer approves with minor current-pointer corrections in
architecture 10/15, Standards 20 and the producer catalogue entry. Root verified
and fixed them, preserving Step 5 as dated evidence and linking current status
to this record; the reviewer rechecked the edits. A final glossary clarification
states context precedes business construction/DI resolution, while metadata
registry discovery happens earlier. Root verified the actual executor ordering.
Five corpus consistency cases and link/fragment checks pass; these final fixes
change documents only. No open verified finding remains from either round.

All six P02b-3 steps completed their implementation commits and both independent
review rounds. The local full-suite, focused fix and demo receipts retain the
explicit scopes above; no final-head CI success is inferred from them. The PR
handoff below will own the live seven-check verification and maintainer review.
This completes P02b-3 implementation and local review, not a merge or the whole
Phase 02b. P02b-4–8 remain unimplemented; the next packet is P02b-4's background-
job decision pass, including the reserved ADR-0047 before runtime work.

### P02b-3 PR handoff (2026-10-13)

[PR #31](https://github.com/HodeTech/LearnStack/pull/31) compares `development`
with `main`. The six-step implementation and both independent review rounds
are complete; initial enforcement is `82623d8`, verified first-round fixes are
`84995d3`, and local review/document closeout is `a8e9c9d`. The
[Step 6 record](#p02b-3-step-6--production-enforcement-and-pr-closeout-2026-10-13)
owns scoped local validation, independent receipts and corrected findings.
This handoff changes documents only and introduces no new implementation scope.

The [live PR checks](https://github.com/HodeTech/LearnStack/pull/31/checks) own
exact-head CI results. All seven required contexts remain active with strict
up-to-date protection: backend, frontend, secret scan, meta, backend integration,
OpenAPI diff and public demo/Lighthouse. The initial published head started CI;
this documentation receipt receives its own final-head run. No pending or
superseded run is counted as success. Maintainer review and merge remain pending.

P02b-4's background-job decision pass is next after this packet's review. It
must draft and obtain approval for the reserved ADR-0047 before runtime work.
Authentication, realm reconciliation, BFF/session and phase exit remain
P02b-5–8; Phase 02b is not complete.

### P02b-3 external review follow-up (2026-10-15)

The external PR #31 review compares `6a277770..683ea537`. Findings are review
inputs, not authority; each was checked against current code, Accepted ADR-0046
and the approved P02b-3 package. Earlier dated receipts and Accepted ADR bodies
remain unchanged. No new runtime decision, migration, role, endpoint or replay
permission is introduced by this follow-up.

Verified corrections:

- Live P02b-2 pointers now name its merge closeout; P02b-3 status points to Step 6
  and the PR handoff. Shared handler/audit remarks, canonical Inbox prose,
  glossary, decision index and review guidance describe delivered behavior.
- Failed snapshot reads no longer advance the successful refresh timestamp.
  Recovery is tested after restoring SELECT without advancing the fake clock.
  A normal claim's uncertain completion records `execute:unknown:commit`;
  expired-lease recovery retains `recovery:unknown:expired`.
- The existing lifecycle census now enumerates actual tenant-announcement calls
  through both the interface and concrete adapter, including inherited, async
  and delegate shapes. Missing real calls and planted extra callers fail it;
  ADR-0040's eight logical setter paths remain unchanged.
- Catalogue discovery reads the declared Status block rather than any bold
  Implemented mention elsewhere. A Registered entry with misleading Implemented
  prose is a negative control. The scheduling-index proof reads actual production
  eligibility/admission constants and includes time, lease, locking and LIMIT.
- Removed the duplicate executor registration. Added actual-store wrong-kind
  controls and producer terminal audit cases for absent versus invalid causal IDs.
  Composition/factory remarks and seed-audit count explanation are aligned.

Verified dispositions for the remaining review items:

| Review item | Disposition and current evidence |
|---|---|
| Health should tolerate one retained overrun | Rejected: the approved P3 runtime table and Standards 10 require unhealthy while any callback/cleanup overrun remains. Capacity four separately stops acquisition; ordinary running work is not this count |
| SQL NULL causal IDs should invalidate a valid original trace | Rejected: optional absence is valid; only empty IDs are invalid. Real terminal regressions now pin both outcomes; audit prose explicitly distinguishes absence |
| Unclassified delivery log should include raw handler type | Kept minimized: existing delivery proofs prohibit handler type text. The scheduler's correlated attempt already names finite subscription/event labels and outcome; no raw stored type or exception detail is added |
| Queue snapshot lacks a dedicated aggregate index | No schema change: the exact count/minimum covers all unfinished ledger states at the approved cadence, under the SQL deadline. Claim-index applicability is now tested against actual selection. This is not a claim of constant-time counting; Phase 11's existing retention/scale work owns aggregate cost tuning |
| Initializers need ordinary AggregateConflictException handling | No defect: initialization and registration share the concept lock, with both winning orders tested. Unexpected ID/constraint faults abort and follow subscriber retry |
| Initializer exception should include Result.Error detail | Kept bounded: actual consumers throw fixed effect-specific failures, tested on refusal. Raw details do not belong in delivery diagnostics |
| No-change initializer audit lacks a business snapshot | Accepted behavior: preservation attempts remain classified MUST successes without mutation or entity snapshots; creation separately requires captured Active state |
| Preserved non-Active built-in causes untested Seeder divergence | Rejected: Draft/Deprecated/deleted and successor/history tests preserve every business field, then refuse the exact Active demo postcondition; the seed skill already documents it |
| Seed skill implies old built-in registration | Rejected: its write-order step explicitly uses separate shared initialization commands and distinguishes authored registration/publication |
| Concept-history SQL duplicates mapping names | No refactor: owner-local constant SQL intentionally reads tombstones through explicit tenant predicates and RLS; actual schema/history/isolation/race tests cover it |
| Five-second acceptance/inbox command timeouts | Match the approved fixed SQL bound. No shared mutable configuration contract is promised |
| Per-subscription acceptance statements | Two declared subscriptions; separate equality reads deliberately obtain a fresh READ COMMITTED snapshot after a concurrent conflicting insert |
| Repeated writable-announcement checks | Deliberate boundary validation: detect changed transaction, context generation and tenant/org before SQL and commit |
| Retained envelope needs an owner trigger | Existing exact column grants implement the approved immutability boundary and have real-role forbidden-write controls. Inbox's owner trigger is a separate marker contract; no new owner-level policy is inferred |
| Capture exclusions name currently unmapped SQL machinery | Intentional ADR-0044 Amendment 11 policy; mapped probe types exercise actual interceptor exclusions even though production storage uses SQL |
| Integration friend assembly exposes all internals | C# friendship is assembly-wide; it enables actual internal execution/lifecycle proofs without exposing production APIs |
| DeliveryAttemptTests name, worker-test title and options binding | Naming/configuration suggestions only: the tests deliberately cover attempt cancellation control and exactly one OutboxProcessor; dispatcher options use approved fixed bounds, not a promised configuration section |
| Lifecycle/owner Facts need separate canonical rows | Existing Integration_Event_Handlers_Use_InboxGuard entry already names both companions. The expanded announcement proof is recorded there; no duplicate rule is added |
| Producer/subscriber rules share TerminalOffenders | Intentional common witness authority, with distinct producer and subscriber dirty controls; not a missing independent failure path |
| Bare First/Single in planted controls | Missing fixtures fail loudly rather than pass. No absent-subject success was reproduced; production subject and signature checks remain explicit |
| Nonempty/derived-capability assertions | Existing required-type, method, signature and exact-owner checks fail before mutation loops when a production subject disappears |
| Ignored consumer ActivityContext.TryParse result | Immutable envelope construction and the retained-envelope reader validate the trace before this call |
| Internal delivery partition lane needs explanation | Existing comment explains acceptance/execution separation and canonical UUID collision avoidance; the approved local-ordering bound remains |
| Subscription grammar copies need consolidation | Equivalent current contract, already tested through registry, storage and witness admission; optional refactor, no divergent behavior demonstrated |
| CancellationToken.None after commit admission | Accepted cancellation/unknown-outcome tradeoff; do not replace the approved sticky gate and reconciliation contract |
| Decision-index amendment numbering gaps | Selected navigation links do not enumerate all historical amendments; complete dated sections remain in each ADR |
| Built-in enumeration, meter factory and health Degraded suggestions | Optional abstractions/observability policy changes; no demonstrated current defect in the approved two-default, binary-health contract |
| Earlier P02d seed receipts and ADR-0046 top navigation | Historical evidence stays intact. P02b-3 Step 5 owns current generated built-in identities and semantic verification; current index links the preserved ADR-0046 Amendment 3 |
| Replay/privileged execution and future strict ordering | Existing Phase 03/new-decision triggers remain explicit; no operator entry point or privileged subscription is activated |
| Retention, inbox-trigger purge exception and erasure inventory | Existing Phase 11 retention/grant decision and P02b-8 inventory obligations remain; this follow-up does not grant purge or bypass access |

The original source head `683ea537` passed all seven required CI contexts in
[run 38424753309](https://github.com/HodeTech/LearnStack/actions/runs/38424753309).
Downloaded exact-head artifacts corroborate 1,862 unit, 311 architecture,
4 contract and 1,550 integration tests (3,727 backend; zero failed/skipped),
1,556 web plus 60 SDK tests, and 27 admitted browser/Lighthouse report pairs.
Maximum normalized gzip JS was 106,551 bytes, maximum CLS 0, all automated
accessibility scores 1; the proof finished in 621,330 ms with successful cleanup.
These are evidence for the reviewed head, not this correction's final-head CI.
Correction validation: Release solution build has zero warnings/errors;
solution-wide format and diff checks pass. All 1,862 unit, 311 architecture,
4 contract and 176 Docker-free integration tests pass, with zero skips. Focused
real PostgreSQL validation passes 73 worker/store/terminal cases plus the actual
candidate-index case. Seven ADR parser cases and the document link/fragment,
prose-width and historical-record checks pass. At this initial receipt the
catalogue counted 218 methods and 200 Implemented rules (121 architecture,
79 elsewhere); the round-2 correction below records the accurate current count.

The first full unit run exposed a cross-test metric listener race (one observed
failure counted another test's same-name meter). The corrected test filters its
owned Meter by reference, disposes its provider and verifies an unrelated meter
really emits while contributing zero to its count; the actual store must still
count exactly one. The focused case and corrected full unit suite pass.
Fresh review rounds and correction-head CI remain pending at this initial receipt.

#### External follow-up review round 1

Two fresh independent agents reviewed `683ea537..586ed75`. GPT-6-astra/xhigh
reviewed runtime, security, isolation, lifecycle and performance, independently
passing 72 focused PostgreSQL integration cases and three architecture cases.
GPT-6.1-sol/xhigh reviewed guards, tests, governance and all changed documents,
independently passing eight architecture cases, 18 recorder unit cases and the
real PostgreSQL candidate-index case. All runs have zero failures/skips.

The runtime review found no actionable issue. The proof/corpus review found two
Minor wording errors: the new machinery-write prohibition inadvertently required
all business handlers to send commands, although the Accepted classified direct
one-root path remains valid; and Security retained an obsolete one-setter count.
Root verified both against ADR-0044 Amendment 8/ADR-0057 and current transport
calls. The fixes remove the command-only restriction and describe the actual
announcement seam without a misleading count. The reviewer rechecked both and
approved with no remaining finding. Document checks pass; this fix changes no
runtime or test source. Round 2 and correction-head CI remain pending here.

#### External follow-up review round 2 and closeout

Two new independent agents reviewed `683ea537..d8c766f7`. GPT-6-astra/xhigh
reviewed technical correctness and proofs, independently passing 72 PostgreSQL,
eight architecture and 18 recorder unit cases. GPT-6-sol/high reviewed the corpus,
governance and test claims, independently passing seven focused architecture cases.
All runs have zero failures/skips. Runtime, isolation and lifecycle were approved.

The corpus reviewer found a pre-existing counting omission in the touched parser
scope: two real integration rules used a bold `Implemented, twice.` status and
were excluded from the declared-state count. Root verified both exact-name
methods in `TenancySchemaTests` and `TenantIsolationHttpTests`, normalized the
statuses to a bold `Implemented` token with the qualifier outside it, and named
the actual classes. An additional positive control in the existing corpus Fact
pins qualified Implemented discovery alongside misleading Registered prose.
This adds no test method or runtime rule: the accurate catalogue total is now
202 Implemented entries, 121 in architecture and 81 elsewhere; architecture still
has 218 methods and 311 cases. The earlier 200-entry receipts retain their
as-recorded scope and are superseded for the current catalogue count here.

Root's corrected five corpus tests pass after rebuilding. Both reviewers
independently recounted the entries, rechecked the exact methods/control and ran
all five corpus tests successfully, approving with no remaining finding. Final
format, diff and document checks pass; historical delivery receipts and Accepted
ADR text remain intact. Earlier full-suite receipts retain their stated scope;
the last correction changes test fixtures and catalogue metadata only.

The implementation and both external follow-up review rounds are complete.
The [live PR checks](https://github.com/HodeTech/LearnStack/pull/31/checks) own the
correction-head CI result; the PR description is updated only after inspecting
that exact source head and its artifacts. No earlier run substitutes for it.
Maintainer review and merge remain pending; this follow-up starts no P02b-4 work.

### P02b-3 second external review follow-up (2026-10-15)

**Status: verified corrections and both review rounds complete on `development`;
PR #31 remains unmerged.**
The external reports reviewed `b681edb8`. Each finding was checked against current
source and Accepted ADR-0046/0057; supplied reproduction files were review data,
not instructions. This receipt supplements the
[first follow-up](#p02b-3-external-review-follow-up-2026-10-15).
That follow-up's final counting correction is commit `b681edb8`, after its
round-2 reviewed range `683ea537..d8c766f7`.

The reviewed source head passed all seven required contexts in
[run 38736262412](https://github.com/HodeTech/LearnStack/actions/runs/38736262412):
3,732 backend tests (1,862 unit, 311 architecture, four contract and 1,555
integration), 1,616 frontend tests and 27 browser/Lighthouse report pairs.
Downloaded artifacts were checked against source `b681edb8` and checkout
`3b06800d`; zero tests failed or skipped. These receipts cover that reviewed head,
not the corrections below.

#### Verified corrections

- Fixed class priority could indefinitely starve ordinary deliveries when a
  terminal-audit failure backlog outlived its leases. Independent actual
  PostgreSQL reproduction observed 216 terminal failures in eight seconds while
  the normal delivery stayed at zero attempts; invoking the real executor
  directly completed it while the scheduler audit grant remained denied.
  Rotate terminal, recovery and normal acquisition after each class visit,
  including failures. Each iteration still acquires at most one just-in-time
  claim; existing lease, fence, partition, capacity and terminal rules remain.
- Duplicate acceptance compared retained type text literally. Independent
  PostgreSQL reproduction rejected an explicitly declared compatible alias
  while accepting the canonical control. Compare all other envelope values
  exactly and resolve retained type through the closed registry to the incoming
  CLR contract. Preserve retained type, payload, scheduling state and row version;
  no arbitrary CLR loading or payload normalization is added.
- Completion constructor calls now have an exact owner census, with additional
  direct/asynchronous caller and missing-site controls. Production project
  discovery includes differently named projects and ignores generated `obj`
  projects, with a temporary-tree control.
- Audit storage requires a non-null diagnostic context accessor in every
  construction. Undefined subscription execution scope has its own fixed
  diagnostic. Catalogue refusal has its own closed `execute:refused:catalogue`
  descriptor, with no-construction/business/inbox controls. Rollback/reapply
  proofs include the inbox trigger function, and
  lease-expiry polling has a deadline propagated through its database reads.
- Live state pointers, migration-chain naming, the naming table, scheduler index
  rationale, catalogue detector sharing and delivered subscriber wording are
  aligned. Earlier delivery/review receipts retain their stated scope.

#### Other findings and scope

| Review item | Disposition and current evidence |
|---|---|
| Unknown-COMMIT failure counter | Accepted process observation, not durable business accounting; Standards 10 explicitly documents that an unknown COMMIT may have succeeded |
| Startup should log exception types/details | Existing minimized dispatcher diagnostics deliberately omit provider/exception type text; current fixed failures and health remain bounded |
| Additional completion owner assertion | Already enforced by prepared-owner creation and `RequireLive`, including before fencing; no reachable missing check |
| Suppress post-acknowledgement disposal errors | Deliberately pinned by `UnitOfWorkCommitCleanupTests` and `DeliveryCommitGateTests`; delivery interprets definitive commit evidence. Changing ordinary request response policy needs a separate design |
| Acceptance round trips | Two current subscriptions; the separate equality statement intentionally gets a fresh READ COMMITTED snapshot after a conflicting INSERT |
| Duplicated SystemActor derivation | Not present: the coordinator derives it; producer envelope creation consumes captured causal context |
| Add a 200-character subscription ID limit | No such bound exists in the Accepted grammar/package; the cited 150-character cap is specific to killswitch keys. Finite registered IDs remain trusted configuration; no new numeric contract or migration is inferred |
| Initializer conflict catch | Shared concept lock plus any-history refusal handles both registration orders; unexpected ID/constraint faults must roll back |
| Taxonomy verification needs set equality | Unique stored band keys plus equal count and complete membership already establish unordered equality; real shape/history mismatch controls remain |
| Two stores must use separate pools | Existing contention proof promises two stores and uses distinct live connections/transactions; separate pools/processes are not its claim |
| Raise shortened fixture deadlines | No reproduced deadline defect; keep the intentionally shortened valid test bounds and controlled shutdown clock |
| Health should count ordinary awaited callbacks | The published count tracks retained overruns/cleanup; the ordinary serial await does not publish it. Any retained task remains unhealthy, with acquisition stopping separately at capacity four |
| Additional commit-owner abstraction/refactors | Exact declared owners remain intentional; no new helper, outcome router or diagnostics abstraction is needed for these corrections |
| Internal replay needs a production audit writer | Still unwired; Phase 03 owns operator permission/HTTP and its classified audit path. No new replay entry point is activated |
| Friendship, cross-cutting composition and N envelope copies | Existing explicit internal proof access and composition are unchanged; the accepted package already inventories per-subscription retained copies for P02b-8/Phase 11 |
| Further policy parity/concurrency/census optimization | Existing real-role policy, independent-connection contention and planted production controls remain; no missing behavior was demonstrated beyond the corrected discovery/constructor gaps |

Correction validation: Release solution build has zero warnings/errors; full
format and diff checks pass. All 1,864 unit, 312 architecture, four contract and
176 Docker-free integration cases pass with zero skips. Focused integration
passes 31 acceptance, 23 actual-worker and 113 store/audit/migration/composition
cases. The latter includes three Docker-free composition cases already covered
by the full Docker-free receipt; these are not extra distinct tests.

The fixed-priority and literal-type-equality mutants each fail their new
PostgreSQL regression. Original source was restored, the solution rebuilt and
both regressions rerun successfully. Seven ADR parser cases pass; document
checks validate 2,822 relative links and 631 fragments, prose width, historical
receipts and earlier Accepted ADR text. The catalogue counts 219 methods and
202 Implemented rules (121 architecture, 81 elsewhere).

The first architecture run correctly rejected the moved claim calls because its
exact privileged-consumer list still named `ExecuteAsync`. The corrected census
requires the private `ClaimNextAsync(string[], CancellationToken)` signature
instead, with an extra-overload control. It grants no whole-type exemption; the
full corrected architecture suite passes.

Two fresh independent review rounds and correction-head CI remain pending at
this initial receipt. Earlier CI evidence does not substitute for that head.

#### Second follow-up review round 1

Three fresh agents reviewed `b681edb8..92e296ed`. GPT-6-astra/xhigh reviewed
runtime, transaction/cancellation ownership, fairness and diagnostics; it passed
23 worker, 25 diagnostic/registry unit and 40 dispatcher architecture cases.
GPT-6.1-sol/xhigh reviewed guards, proofs, governance and corpus consistency,
independently passing all 312 architecture cases. GPT-6-sol/high reviewed alias
acceptance, concurrency, RLS and audit composition; it passed 31 acceptance,
23 worker and 25 focused unit cases. All runs have zero failures/skips.

All three approve with no verified actionable finding. The older prefix-scoped
request/handler discovery scans are separately documented scopes, not a claim
of this shared production census. No new rule or deferred decision is silently
introduced. Round 2 and correction-head CI remain pending here.

#### Second follow-up review round 2

Two new agents reviewed `b681edb8..a95ae2ec`. GPT-6-astra/xhigh reviewed runtime,
acceptance, isolation and audit composition, independently passing 163 focused
integration, 43 unit and 41 architecture cases. GPT-6.1-sol/xhigh reviewed proofs,
corpus, ownership controls and governance, independently passing all 312
architecture and 25 focused unit cases. All runs have zero failures/skips.
Both approve with no verified actionable finding against that correction scope.

The runtime reviewer separately identified the unchanged producer worker's same
fixed class-priority pattern. This concern was not counted as a verified finding
against the reviewed diff. The adjacent correction below owns its PostgreSQL
verification and any resulting implementation/review evidence; the delivery and
alias approvals do not substitute for reviewing that change.

#### Adjacent producer fairness correction

**Status: correction, validation and both independent review rounds complete.** The
producer worker's strict terminal/recovery/normal priority reproduced the same
starvation in real PostgreSQL. With 100 terminal requests and only the scheduler
role's audit INSERT denied, the regression passed its lease-cycle failure check
then failed at the independent normal-publication/recovery deadline. The baseline
producer source was unchanged. The fixed worker uses the same bounded rotating
acquisition as the subscriber; its exact private credential-consumer signature
and extra-overload control replace the prior call-site requirement.

Accepted ADR-0046 requires continued unrelated eligible work and does not require
strict claim-class priority. This is an implementation correction, with no new
ADR, grant, lease, attempt, terminal-audit or publication-ownership contract.
The regression requires actual unwrapped transport acceptance, no business
construction, unchanged recovery attempts and every failed terminal request
remaining intact without terminal audit while the grant stays denied.

The unchanged baseline fails the new progress regression after its first
lease-cycle check. After correction the full actual producer suite passes
19 cases, zero failed/skipped; the new regression passes in 10.634 seconds.
Release solution build has zero warnings/errors. All 1,864 unit, 312
architecture, four contract and 176 Docker-free integration cases pass again;
full format/diff and document checks pass. Two fresh independent reviews of
this additional producer correction remain pending at this initial receipt.

Producer correction review round 1 used two fresh agents against
`a95ae2ec..bd51a974`. GPT-6-astra/xhigh passed 80 producer/store integration,
40 architecture and 20 ownership/bounds/observation unit cases.
GPT-6.1-sol/high passed all 19 producer and 40 architecture cases. Both approve
without a verified actionable finding; no fix was needed before round 2.

Two new agents independently reviewed the same frozen correction in round 2.
GPT-6.1-sol/xhigh passed 80 producer/store integration, 40 architecture and
20 focused unit cases; GPT-6-astra/high passed all 19 producer and all 312
architecture cases. Both approve with no verified actionable finding. All
independent runs have zero failures/skips; neither round relies on earlier
scope approvals to cover the producer change.

The verified delivery/alias corrections and the adjacent producer correction are
complete, each with both fresh independent review rounds. Final document/diff
checks pass; the earlier receipts retain their exact as-of scope. The
[live PR checks](https://github.com/HodeTech/LearnStack/pull/31/checks) own
correction-head CI; its exact source and artifacts must be inspected before the
PR description records success. Maintainer review and merge remain pending;
this correction starts no P02b-4 work.

### P02b-3 merge and closeout (2026-10-15)

The maintainer merged [PR #31](https://github.com/HodeTech/LearnStack/pull/31) at
2026-10-15 16:45:06 UTC. GitHub records final PR source
`0fa7dc869d5b0f704a2d05313226d3d1b0f98510` and squash merge
`ed60157c14d33fad926fd201b49c08e2d24eed1f`, whose parent is previous main
`6a277770e46a7f4ea3574d9d6891d1802cb8fa90`. Source and squash merge have the
identical tree `57cab77ffb486689de18abbf741b6186dcf7de6f`. Development incorporates
the main merge without switching branches or rewriting published history.

All seven required checks pass in the
[final PR run](https://github.com/HodeTech/LearnStack/actions/runs/38755136480)
for that source; CodeRabbit also reports success. The tested synthetic merge is
`a131ca6b59f750c69d4465b46a0bd1a5717dcd0a`. The seven required contexts and strict
up-to-date protection were re-read from GitHub after merge and remain enabled.
Verified artifacts record:

- **3,745 backend passes**, zero failures/skips: 1,864 unit, 312 architecture,
  four contract, 176 Docker-free integration and 1,389 Docker integration cases.
  All ten alias/progress PostgreSQL regressions pass on this exact source.
- **1,616 guarded frontend passes**: 1,556 web and 60 SDK, zero
  failures/skips/todos; lint, typecheck, builds and native-ingress proofs pass.
- Exactly 27 matching Lighthouse JSON/HTML report pairs across nine URLs, with
  matching source/checkout identities and report digests. Automated accessibility
  is 1 throughout, CLS is 0 and normalized initial JS gzip is at most 106,554
  bytes. Proof finishes in 649.646 seconds within the 12-minute bound; owned
  cleanup passes. Nine timing advisories remain warnings, not production p95.

These are final PR-head results, not a new post-merge or local full-stack run.
**P02b-3 is closed.** All six steps and verified review corrections are delivered:
durable acceptance, shared inbox/ledger, independent retries and terminal MUST
audit, fenced business completion, both Active defaults and semantic Seeder
handoff, plus production-census enforcement. Compatible-alias retries preserve
retained envelopes; rotating acquisition keeps unrelated eligible producer and
subscriber work progressing during terminal-audit failures. Each step and
correction bundle retains its two independent review rounds above. Earlier dated
receipts keep their pre-merge wording; this closeout changes no Accepted ADR.
Publication remains at least once. Phase 02b remains in progress.

**Next: P02b-4 decision pass.** G9 and reserved, undrafted ADR-0047 must settle
job storage under the four-role model, tenant-context and transaction ownership,
safe enqueue, retries/terminal audit, queue/job identity, the adapter's demand
gate, and package/licence choices before runtime implementation. The
[background-job scope](#background-jobs) owns these questions; no job definition
or dashboard route belongs to that packet.

API authentication, realm reconciliation, BFF sessions and phase-exit enforcement
remain P02b-5–8 in the [packet table](#packets-and-decision-gates). P02b-8 owns
retained-envelope erasure inventory. Operator replay permissions/routes, audited
recovery and privileged erasure execution remain Phase 03; broker adapters,
mapped readiness, retention/purge grants and production measurements remain
Phase 11. This documentation closeout starts no later packet or new decision.

**Documentation-only closeout validation.** Five local Release corpus controls
and seven ADR lifecycle/workflow controls pass, zero failures/skips. Relative
links/fragments, added prose wrapping and whitespace checks pass. Every earlier
P02b delivery/review receipt and every ADR file is byte-preserved against the
accepted PR source. Only 11 Markdown files change; runtime suites are not rerun
locally for this closeout. The final PR artifacts above supply their evidence.

### P02b-4 decision package (2026-10-15)

**Accepted — 2026-10-15, @cemil.** The maintainer approves **H1: stock Hangfire
plus an authoritative LearnStack job record**, expanded P4 infrastructure,
[ADR-0047](../decisions/0047-transactional-background-job-runtime.md), its bounded
supersessions/additive seams and all nine implementation steps below.
Acceptance and dated prior-ADR disclosures commit before runtime, package,
migration or grant changes. Development stays on `development`; prior Accepted
ADR bodies/metadata and P02b-0–3 receipts remain unchanged. Runtime, dependencies,
storage/grants, enforcement and production job proofs remain unimplemented.

#### Problem and scope

Ship reusable durable tenant job infrastructure covering short transactional work,
long external prepare/execute/finalize, isolated dispatch slots, cross-instance
per-tenant admission, and recurrence/tenantless fan-out. Hangfire carries opaque
wakeup/tick references; LearnStack is the only business job authority. No dashboard,
production job/recurrence, ffmpeg implementation, Hub endpoint or privileged
maintenance executor is added. P02b-5–8 authentication/session work is separate.

The [ADR workload envelope](../decisions/0047-transactional-background-job-runtime.md#workload-envelope-and-first-consumer-obligations)
owns mechanism/feature boundaries. Phase 04 keeps real media providers, process/
resource limits, artifacts and coordinated concurrency keys; triggered 02c keeps
phone-home definitions; 08a/08b keep notification/reminder domain semantics; 11
keeps licence operational surround and privileged audit DDL/purge. These feature
owners no longer need to invent the selected P4 long-operation/fan-out foundation.

#### Verified premises and document review actions

Current transaction/producer/context/audit/entitlement seams and first consumers
were traced after the P02b-3 closeout. Three read-only design inspections cover H1
handoff (GPT-6-astra xhigh), external/capacity (GPT-6.1-sol xhigh) and recurrence/
privilege/schema (GPT-6.1-sol xhigh). They supplement, not replace, the earlier
comparison experiments and correction reviews recorded below. No runtime proof
is claimed by these source/design inspections.

| Verified gap | H1 contract/action |
|---|---|
| Vendor enqueue/state commits separately | Immediate authoritative INSERT + durable generation-fenced handoff/repair; callback never depends on ACK having arrived |
| Wakeup duplication can become later retry | JobId plus wake generation; only business transition advances generation; resend retains it |
| Stock retry count differs from six committed starts | Separate app-role admission/start receipt; capacity/duplicate/deferred work spends no attempt |
| Current request owner has no job participant | Closed root/stage/recipient participants, exact mode mapping and existing eight setters |
| Ambient/SystemActor is not a privilege boundary | Sticky positive request admission, root/nested/direct Platform/provisioning denial and separate bypass/admin-entry guards |
| Long ffmpeg does not fit a DB transaction | Durable plan/execute-intent/outcome checkpoints; external execution/reconciliation with no business UoW |
| Lease expiry does not stop provider work | Positive quiescence or quarantine; unresolved reservations never expire into free capacity |
| Organization-scoped capacity misses other orgs | Tenant-wide control mutex/reservations; plan/operational ceiling applies across orgs/instances |
| Framework can enumerate no tenant today | Named minimal lifecycle projection grant, bounded durable recipients and actual-row witness admission; no caller tenant selector |
| Recurrence revision can duplicate a logical slot | Unique recurrence-key/UTC-slot independent of revision, monotonic activation, stable jitter and checkpoint/recipient atomicity |
| Filters can be bypassed on vendor fallback | Migration-owned below-filter state/metadata guards and bounded log provider, pinned real-worker dirty controls |
| Whole-schema tests assume every table is app-readable/RLS tenant data | Exact table classes, expected vendor/control 42501 denial, no unclassified-table exclusions |
| No general runtime was Accepted | H1 retains audit-maintenance Hangfire choice; exact bounded identity/context/storage changes get dated Amendment navigation |

#### Accepted decisions

| Gate/contract | Accepted H1 contract | Vehicle |
|---|---|---|
| G9 runtime/storage | Pinned stock Hangfire wakeups, authoritative tenant/org job row, exact public vendor-control class and four roles | ADR-0047; ADR-0003 Amendment for grant/confinement extension; canonical Standards 05/20 on implementation |
| G9 ownership/submit | Typed own-module definitions, exact writable announcement, identifier-only frozen payload, immediate INSERT/ancestral receipt | ADR-0047; ADR-0057 extension and source guards |
| G9 context/owner | JobExecutor alone; sticky ordinary ceiling; exact ISender roots/framework requests; staged owner participants | ADR-0036/0032 replacements; additive ADR-0040 and closed ADR-0052 mode extension |
| G7 job part | Sixth WriteJobTerminalAsync, actual job-row witness, terminal state/MUST audit atomic | ADR-0044/0033/0046 navigation; Tenancy platform.job.dead_letter catalogue/matrix |
| G8 job/recurrence part | Conditional routing/status exemption with exact grants/censuses, bounded diagnostics, metrics/health and terminal MUST | ADR-0046 / Standards 18 Required Behaviours extension; no general background/privileged exemption |
| G9 profiles/capacity | Short transactional or staged external execution; renewable authority, positive quiescence, tenant reservations and disjoint pools | ADR-0047; real role/capacity/stale-effect proofs |
| G9 recurrence/fan-out | UTC interval/daily, CoalesceLatest, durable slot/jitter, minimal lifecycle reader and restricted recipient producer | ADR-0047; exact Standards 05 enumeration exception and ADR-0057 producer origin |
| G9 retry/readiness | Six normal starts; separate six finalize starts/recovery probe bounds; unknown/Pending defer, explicit retired tombstone | Standards 09/20, ADR-0047 mixed-version/capability queues |
| G17 labels/observation | Finite definition/profile/recurrence/group categories, no tenant/org/job/trace labels; memory snapshots | Standards 10 with first emitters |
| G18 job payload/diagnostics | Identifier-only 262,144-byte parameters, 65,536-byte plan/outcome checkpoints and 256-byte failure descriptors; durable-store erasure inventory remains P02b-8 | ADR-0047; bounds/guards with implementation, retention/purge authority remains Phase 11 |
| Dependency/licence | Core/NetCore 1.8.25, PostgreSql 1.21.1, Newtonsoft 13.0.4; stock shared assemblies, concrete graph/notices/source/relinking qualification | ADR-0047 and implementation packaging evidence; outside ADR-0035 gated set |

G9 and the new job parts of G7/G8/G17/G18 are Accepted — 2026-10-15.
Existing G7/G8/event accepted dates and receipts are preserved. At implementation,
production business and recurrence registries independently assert zero definitions;
nonempty real runtime subjects and fixture-only definitions carry enforcement and
behavior proofs. Acceptance claims no passing runtime census or production proof.

#### Runtime bounds and observation

Bounds below are accepted inputs for this expanded H1 contract. Invalid values or
relationships refuse startup before workers; SQL/database time and actual task
ownership remain authoritative.

| Bound | Accepted value |
|---|---|
| Independent stock server pools | Transactional 1, external 2, control 1 workers by default; finite dedicated feature pools, at most 8 pools/64 queues, 1–16 workers per pool, sum at most 32 per API instance |
| Operational tenant/group ceiling | Positive 1–1,024, default 1; optional Hard LimitKey intersects it; no tenant-supplied pool/group |
| Retained actual overruns/cleanup | Stop-new-start threshold 4 per pool; atomic accounting, retain every admitted task; hard bound configured workers + 4, at most 20 per pool; other pools progress |
| Outstanding synchronous bridge calls | At most 4; wait at most 10 s, retain actual operation; do not block other control lanes |
| SQL and external reconciliation probe | SQL positive, at most 5 s/default 5 s; provider probe positive, at most 30 s/default 10 s |
| Short root/prepare/finalize | Positive, at most 5 min/default 30 s; no long external operation inside |
| External run wait | Positive, at most 24 h/default 1 h; immutable per-attempt deadline |
| Lease / renewal | Lease 15 s–10 min/default 60 s; renewal 1–60 s/default 15 s; lease > twice renewal + twice SQL timeout + 5 s |
| Quiescence cancellation grace | Positive, at most 30 s/default 30 s; expiry never releases an unproved reservation |
| Control poll / snapshot | Poll 50 ms–1 s/default 200 ms; snapshot 1–60 s/default 5 s; successful work does not force scans |
| Handoff repair grace / batch | 15–300 s/default 60 s; at most 100 rows per quantum; same-generation resend |
| Recovery probe cadence / unsuccessful budget | 5–300 s/default 30 s; 6 unsuccessful probes then quarantine; Running probes bounded by attempt deadline/grace |
| Shared shutdown joins | Positive, at most 30 s/default 30 s across pools and bridge; no per-task reset |
| Progress heartbeat deadline | At least lease + 15 s, at most 15 min/default 75 s; renewal/control progress counts, not long work completion |
| Parameter / plan / outcome / failure descriptor | 262,144 / 65,536 / 65,536 / 256 UTF-8 bytes |
| Recurrence interval / fan-out page | 1 min–366 days, UTC anchor; jitter 0–min(period, 2 h); at most 100 routes/page |

A plan cap is resolved through IFeatureFlags, capped by a positive operational
ceiling. -1 means operational ceiling, 0 means no new admission, invalid/Soft/
undeclared keys fail safely. No Media LimitKey is invented by this packet.

Standards 10 owns final names/label inventory. Job observation includes pending,
oldest eligible, deferred, retained/quarantined/reserved capacity, bridge repair,
finalize/failure/lease loss, fan-out progress and heartbeat signals. Confirmed
failure differs from unknown outcome. Register jobs health, unmapped; preserve
healthz liveness and Phase 11 readyz/production-alert ownership. Normal valid
long-running work is healthy; stale/faulted/retained/quarantined work is not.

#### Approved implementation steps

Every step commits its implementation/validation, runs two fresh independent
review-agent rounds, verifies/fixes findings and commits corrections, then advances
automatically. Critical authority/state-machine reviews use GPT-6-astra xhigh;
independent proof/composition/corpus lenses use GPT-6.1-sol high/xhigh. Each round
uses new reviewers. A new decision outside this accepted package returns to the
maintainer before dependent work. Development stays on `development` throughout.

1. **Acceptance and typed contracts.** Commit final acceptance/dated Amendment
   disclosures first, then add scheduling/profile/recurrence/identity contracts,
   closed registries, request-scope admission and Jobs project boundaries. Register
   canonical catalogue obligations without claiming enforcement before it ships.
2. **Storage, dependencies and guarded bootstrap.** Add exact pins/graph/notices;
   Platform migrations for authoritative/control/capacity/recipient and stock
   vendor schema, immutable/transition/metadata guards, RLS/grants/indexes and
   canonical Standards 05. Prove fresh/Down, every-table classes, expected 42501,
   exact role/source guard, schema 23, below-filter containment, bounded vendor SQL
   and real index plans; keep the unguarded notification reconnect path disabled.
3. **Submission and app-role admission.** Implement trusted producer stamping,
   immediate INSERT/receipts, routing leases, scoped read-only envelope validation
   and separate committed start receipts. Prove rollback/absorbed refusal,
   getter/stale/read-only attacks, exact org/module scope, unknown/deferred readiness
   and no handler construction before admission. Capacity integration follows step 6.
4. **H1 wakeups and independent pools.** Implement bridge/reconciler, exact two
   wrapper targets, generation fencing, guarded native connection factory and
   disjoint server queues/pools. Prove real pinned worker create/init/ACK crashes,
   duplicates/early/stale callbacks, vendor loss/fallback, malformed invocation,
   bootstrap refusal, retained synchronous calls and construction-only fixture wiring.
5. **Transactional owner and terminal audit.** Add root participant/gate, scoped
   ordinary ceiling, async attribution and fair retry/recovery/terminal control.
   Prove business/outbox/jobs/audit/completion atomically, lock-wait expiry,
   Result/exception parity, uncertain COMMIT, no seventh start, actual-row sixth
   audit seam substitutions, and terminal-audit backlog progress beyond a lease.
6. **Tenant capacity reservations.** Implement group mutex/reservations and optional
   Hard LimitKey resolution. Prove two instances/orgs share a tenant cap, capacity
   refusal spends no attempt, unlimited/zero/downgrade/fault cases, exact app grants
   and no TTL release of unresolved external occupancy; keep independent pools live.
7. **External prepare/execute/finalize.** Implement immutable stage/attempt receipts,
   confirmed ExecuteIntent, no-UoW external adapter, renewal, typed reconciliation,
   quiescence proof/quarantine and bounded finalization. Prove each crash/unknown
   COMMIT boundary, provider accepted-before-ACK, paused-old-start/NotFound traps,
   stale outcomes/revisions, simultaneous overruns/cleanup saturation, no seventh
   effect and domain known-failure/terminal behavior without rerunning external work.
8. **Durable recurrence and restricted fan-out.** Implement frozen monotonic manifests,
   UTC slots/CoalesceLatest/jitter, minimal lifecycle projection, atomic pages/
   recipients and witness-bound app submission. Prove mixed revisions/ticks,
   suspended licence targets, cohort bounds, NULL-org dedup, forged/cross-tenant
   witnesses, child acknowledgement loss, target scope/parameter binding, bounded
   fairness and no general selector.
9. **Enforcement and packet closeout.** Finish full observation, exact production
   censuses/planted violations, all carrier/feature/skill handoffs and the new job
   skill. Run complete backend/frontend/demo regression and packaging/boot proof;
   open the development → main PR with exact evidence. Real CI and maintainer merge
   remain separate from local completion; zero production business definitions.

#### Expanded H1 proposal review

The final contract is drafted against the selected H1/expanded direction. The
historical receipts below approve only their earlier document revisions; they do
not substitute for two fresh reviews of this route-specific proposal. Current
review/validation evidence is appended here after it actually runs.

Round 1 reviewed proposal commit `fa539176` with three fresh independent reviewers:
GPT-6-astra xhigh (state/authority), GPT-6.1-sol xhigh (external/capacity) and
GPT-6.1-sol xhigh (recurrence/corpus). All approve the Proposed documentation;
none accepts the ADR or authorizes runtime. The state reviewer found one minor
carrier omission: Standards 01/17 now explicitly distinguish the authoritative
tenant envelope from opaque vendor wakeups/ticks. No confirmed lifecycle,
unknown-COMMIT, generation, terminal-audit or ordinary-authority defect was found.

Self-review additionally pins the disabled stock notification reconnect path and
server-side SQL deadlines, including the counter aggregator's unlimited driver
timeout. It makes future-due handoff checks and recurrence target parameter/scope
binding explicit. These are proposal clarifications, not runtime changes or proof.
Local validation passes five Release corpus tests, seven ADR workflow controls,
1,059 local links and 321 fragments, added prose wrapping and whitespace checks.
Prior ADR files and P02b-0–3 receipts remain byte-preserved against `71568e78`.
Fresh Round 2 reviewed the corrected complete proposal before final approval.

Round 2 paired new GPT-6-astra xhigh (architecture/security/state) and GPT-6.1-sol
xhigh (corpus/recurrence/proof) reviewers on `29cc4c15` and the correction captured
by `40c9f9b8`. The latter fixes the retained-overrun bound: four is the atomic
stop-new-start threshold; already admitted workers may overrun together and must
remain owned, with a conservative workers-plus-four bound. Both reviewers verified
that correction and approve the Proposed documentation with no remaining required
findings. Simultaneous-overrun behavior is an explicit implementation proof duty.

Final local checks again pass five Release corpus tests, seven ADR workflow tests,
1,059 links and 321 fragments with zero errors, prose/whitespace and preserved
historical records. All eleven changed files are Markdown; no runtime/dependency/
migration/grant change or job production proof is claimed. Final maintainer
acceptance of ADR-0047 and this nine-step package remains required before runtime.

#### Maintainer acceptance (2026-10-15)

The maintainer explicitly approves the final ADR-0047 and this nine-step package.
G9 and the new job parts above are Accepted. Dated numbered Amendments in the
fifteen named prior ADRs disclose only the bounded replacements/additions; every
original byte, acceptance metadata and earlier amendment remains preserved.
The decisions index, glossary, current status carriers and first-consumer handoffs
now distinguish this accepted contract from pending implementation.

The review/validation receipts above remain evidence of their Proposed revisions;
their pending-approval statements are historical. This acceptance creates no
runtime, dependency, migration, grant or enforcement evidence. The acceptance
commit precedes every implementation change; each later step owes its own
validation and two fresh independent review rounds.

#### Earlier proposal and correction receipts (historical)

These dated receipts precede the H1/expanded selection above. Their then-current
open-choice/scope statements describe those revisions, not this final proposal.

#### Decision review and approval readiness

The following receipts describe the earlier proposal reviews, not final current
readiness. The subsequent workload/corpus review reopens the gaps named above;
earlier review agreement does not select a runtime or override maintainer findings.

Two fresh independent review rounds inspected the Proposed ADR/package against
current source and Accepted records. Each round paired GPT-6-astra xhigh for
transaction/security architecture with GPT-6.1-sol xhigh for corpus/proof coverage.
The original three scoping inspections are separate from those four reviews.

- Round 1 corrections: explicit TenantWide/Organization declarations and exact
  announcement matching; closed transient/permanent outcomes; finite option
  maxima; trusted direct-consumer operation identity; named ADR-0040/0052
  framework read-only extension. Both reviewers rechecked the revised clauses
  and found no remaining approval-blocking issue.
- Round 2 corrections: explicit conditional G8 job-scheduler bypass exemption,
  ADR-0020's licence-job vendor/identity handoff and first-triggered 02c/08a
  recurrence ownership, with architecture 24/26 included. Follow-up inspection
  confirmed the blocking issues were closed. Related source checks also name
  ADR-0015's exact dashboard bullet, app-side UUIDv7 identity and the unaffected
  Hub/superseded-record boundaries. The package's earlier queue summary was
  aligned with the same recurrence answer.

Local validation passes: five Release corpus controls, seven ADR status/workflow
controls, relative links/fragments, added prose wrapping and whitespace checks.
All earlier P02b delivery/review receipts and every prior ADR file are byte-preserved
against `71568e78`. These are document/source checks; no job runtime exists and
no runtime acceptance proof is claimed. Maintainer approval remains pending.

#### Hangfire comparison follow-up (2026-10-15)

The maintainer requested this assessment rather than accepting the initial
PostgreSQL proposal. The [expanded ADR comparison](../decisions/0047-transactional-background-job-runtime.md#hangfire-alternative-assessment-2026-10-15)
owns alternatives, trade-offs, package/source pins and evidence limits. Stock
Hangfire plus an authoritative job record is viable; public transaction-aware
storage customization is also physically feasible. Neither is accepted here.

Three research agents inspected exact upstream/package sources, current owner
contracts, dependencies, storage privileges and lifecycle boundaries. Isolated
database experiments passed eighteen focused cases on fresh PostgreSQL 18.4;
the owned container was removed and no existing service/database was touched.
The native transaction spike used SDK 10.0.302 outside the repository, not pinned
10.0.112. Separate exact-version invocation probes and .NET 10 dependency/basic
load checks are recorded in the ADR. These do not prove production audit, RLS,
worker/retry/cancellation or complete provider support.

Two fresh reviewers, GPT-6-astra xhigh and GPT-6.1-sol high, checked the revised
comparison against sources and experiments. Both approved the evidence/framing
without required corrections; neither approved runtime implementation. Local
document/corpus/ADR controls pass. Runtime choice and final acceptance still
belong to the maintainer; a Hangfire choice revises the detailed design and steps
before any implementation.

#### Workload and corpus correction pass (2026-10-15)

Verified the maintainer's findings against current code and the original
authorities. Three read-only inspections covered early-consumer workloads
(GPT-6-astra xhigh), runtime correctness (GPT-6.1-sol xhigh) and corpus/governance
(GPT-6.1-sol high). These are source/document inspections, not new runtime tests.

- Phase 04 already requires long ffmpeg work, its own capacity and a tenant limit.
  The single transactional lane does not deliver that. The ADR now names the
  separate external prepare/execute/finalize contract and an explicit first-use
  gate, with a maintainer scope choice before route-specific acceptance.
- Phase 04's nightly media lifecycle can precede 02c/08a recurrence. Its trigger,
  restricted tenant enumeration and isolated tenant submissions are distinct from
  Phase 11's privileged audit DDL/purge. Phone-home's 02c ownership exists in
  ADR-0020 Implementation notes; its missing phase-plan entry is restored.
- The candidate now uses unlocked preflight and a final lock/time recheck, an
  ordered Result/exception retry table, deferred unknown/Pending definitions and
  explicit retirement, plus a SystemActor Platform ceiling for root/nested/direct
  execution. Strict org admission and role/column/index proof duties are explicit.
- Corrected exact authority locators, dated Amendment terminology, the implemented
  credential-rule widening obligation, conditional consequences and the decisions
  index. Proposed glossary terms precede implementation. Quartz.NET is a documented
  candidate requiring its own qualification; no unsupported rejection is made.
- Skipped the alleged missing Date/Deciders violation: neither canonical template
  requires those fields. The existing Draft date is honest Proposed metadata.
  Kept PreparedDeliveryOwner delivery-specific; participant and commit gate are
  separately named. Long URLs/tables remain exempt from prose-width limits.

No Accepted decision, runtime, migration, grant or previously completed delivery
receipt is changed. This pass prepares the choice; it does not accept or implement
one.

Correction review round 1 paired fresh GPT-6-astra xhigh (architecture/security)
and GPT-6.1-sol high (corpus/proof) reviewers over the corrections after `46ca923c`.
Both approved the Proposed documentation correction with no required findings;
neither approved a runtime or workload-scope selection. Local validation passes:
five Release corpus tests, seven ADR status/workflow controls, 1,035 local links
and 323 fragments, prose wrapping and `git diff --check`. All prior ADR files and
P02b-0–3 receipts remain byte-preserved against `71568e78`. These checks run on
documentation; no new runtime proof is claimed.

Correction commit `ec1a5520` was reviewed in a fresh second round by GPT-6.1-sol
xhigh (architecture/security) and GPT-6-astra high (corpus/evidence). The corpus
review found one wrong section locator for ADR-0017's Jobs row; the linked
Defense-in-depth heading replaces it, and focused reinspection verified the fix.
Both reviewers approve the documentation correction with no remaining required
findings. Final local link/fragment checks count 1,036 / 324 with zero errors;
earlier ADRs/receipts and the docs-only scope remain verified. Runtime selection,
workload scope and final route-specific ADR approval remain pending.

#### Validation and carrier ownership

Use the existing provider/migration/MediatR/audit/architecture/integration workflows
and run-tests-locally. Test real PostgreSQL, guarded roles and the actual pinned
Hangfire workers; no EF in-memory substitute establishes isolation/atomicity.
Stage dependencies/feature definitions stay explicit; no mock production job.

Acceptance/implementation reconciles ADR-0003/0010/0015/0017/0020/0023/0028/0032/
0033/0036/0040/0044/0046/0052/0057 through dated Amendment navigation/additions,
plus ADR-0047 and the index. Standards 01/02/05/06/09/10/11/15/17/18/20/21/index receive
only implemented current rules; canonical DDL stays 05, metrics 10, proof names 21.
Standards 01/17 distinguish the tenant-bearing authoritative job envelope from
opaque vendor wakeups and tenantless ticks without weakening business isolation.
Architecture 04/09/16/24/25/26/29/30/31/33, Tenancy audit/permission specs, glossary,
root guidance/readmes, compose/launcher fixture wiring and applicable/new skills
must follow the same delivered boundary. Phase 02c/04/08a/08b/11 handoffs keep
business definitions/provider/privilege owners explicit. Hub endpoints are unchanged.

P02b-8 inventories job/attempt/plan/outcome/reservation/recipient copies and vendor
control metadata for erasure. Phase 11 owns actual retention/purge authority and
intervals; stock vendor expiry deletes control metadata only, never authoritative
business intent. Phase 03 owns operator replay/reconciliation/permission/runbook;
no recovery route or unsafe reservation-release switch is exposed by P4.

### P02b-4 Step 1 — contracts and authority (2026-10-16)

**Complete — both fresh independent review rounds pass.**
The acceptance commit is `affa0aca`; this step follows it on `development`.

SharedKernel supplies typed job/attempt/occurrence/recipient identities, the typed
scheduler port, immutable definition/profile/request/capacity/recurrence metadata
and external Execute/Reconcile/finalization contracts. Frozen registries validate
module ownership, exact roots/nested requests, profile/queue/pool bindings, finite
readiness and recurrence scope. Identifier-only parameter/plan/outcome serialization
uses closed DTO shapes, canonical round-trips and frozen options/resolver state.
No production business or recurrence definition is added.

Application admission holds an immutable positive request ceiling independently of
later accessor changes. Exact framework pre-reads select physical ReadOnly through
the existing pipeline. Direct privileged/delivery/admin entry refuses even through
a fresh service scope or permissive platform gate. A guarded lazy bypass-source
facade checks every synchronous/asynchronous connection acquisition, including warm
pools, and owns cleanup. API/Seeder register the same empty job catalogues; neither
registration activates a scheduler, vendor, migration or worker.

Two architecture methods pin the current admission/credential callsites with
planted violations. The nineteen complete ADR-0047 obligations remain Registered;
these slice proofs do not claim the later runtime is implemented. Existing exact
credential/context/owner censuses stay enforced. The catalogue counts 221 methods.

Initial Release validation passes the solution build with CI warnings-as-errors,
1,979 unit, four contract, 176 Docker-free and 1,389 Docker integration tests, with
zero skips. The first full architecture run reports 312 passed and two stale
expectations: the published method count and TransactionBehavior constructor pin.
Both are corrected without widening module/DbContext permissions; an architecture
rebuild and six focused corpus/constructor reruns pass. This gives 314 unique
passing architecture cases and 3,862 unique passing backend cases across the
full runs and focused corrections; it is not a second full architecture run.
Formatting passes for all 67 changed C# files. The documentation check resolves
2,506 local links and 590 fragments, with no wrapping/whitespace errors. Every
ADR/index remains byte-identical to acceptance commit `affa0aca`; earlier delivery
and proposal-review receipts stay preserved. Both fresh independent review rounds
remain pending; later receipts record their actual results.

#### Step 1 review round 1

Two fresh independent reviewers inspect `affa0aca..94b126b4`: GPT-6-astra xhigh
for authority/lifecycle/credential confinement and GPT-6.1-sol xhigh for contracts,
registries/serialization/recurrence and corpus evidence. Both request changes for
one independently reproduced Major each; neither finds a Platform escape or a
current production job caller.

- A retained already-entered nested request can have an open top frame after its
  root closes. The common request/owner admission now checks every ancestor, while
  preserving awaited nesting and framework owner entry with no current request.
  Real ISender regression proves refusal before handler construction; its dirty
  case fails before the correction and its awaited positive control passes.
- A handwritten getter/init accessor can change the returned identifier after the
  canonical reads. Closed identifier DTOs now require compiler-generated automatic
  getters and init setters, preserving positional/nonpositional auto-properties
  and constructor round-trip checks. Three dirty registration/read cases fail
  before the correction and pass afterward. This is an immutable DTO convention,
  not an arbitrary CLR-code sandbox.

Root verifies both findings, adds six regression cases and applies the minimal
corrections. The corrected solution builds Release with CI warnings-as-errors;
1,985 unit, all 314 architecture, four contract and 176 Docker-free integration
cases pass with zero failures/skips. The 1,389-case Docker run above covers the
initial implementation; it is not rerun for these nonactivated job guards. No
vendor/schema/production definition is introduced. A fresh second review round
follows the correction commit before Step 2 starts.

#### Step 1 review round 2 and closeout

Fresh GPT-6-astra xhigh (authority/lifecycle/security) and GPT-6.1-sol xhigh
(contracts/registries/tests/corpus) reviewers inspect the complete corrected range
`affa0aca..bf433085`. Both approve Step 1 with no remaining required findings.
The authority reviewer independently passes 168 focused unit and 78 architecture
cases; the contracts reviewer passes 168 focused unit and 14 architecture cases.
All use existing Release binaries with zero failures/skips. These overlapping
checks are not added to the complete-suite counts above.

Both verify the ancestry/accessor corrections, unchanged Accepted ADRs, exact
credential/owner boundaries and the honest zero-runtime-subject scope. Recurrence
precision is a named next-step constraint: before Step 2 fixes PostgreSQL storage
representation, reject sub-microsecond manifest facts before fingerprinting.
No production job or recurrence is activated to satisfy a census.

The implementation commit is `94b126b4`; verified Round 1 corrections are
`bf433085`. Both fresh rounds and the validation recorded above are complete.
Step 2 now proceeds under the already accepted nine-step package. Remaining
runtime/dependency/storage/worker guarantees are not delivered by this closeout.

### P02b-4 Step 2 — storage and guarded vendor bootstrap (2026-10-16)

**Complete — both fresh review rounds and critical-fix verification passed.**
This step follows the completed Step 1 on `development` under Accepted ADR-0047.

Two Platform migrations introduce seven authoritative job/control tables and the
pinned stock Hangfire version-23 inventory. Standards 05 owns their canonical
DDL/grants and the explicit vendor-control table class. Existing schema sweeps
enumerate all public tables, classify expected application SELECT denial, and
continue actual tenant/organization isolation checks as `learnstack_app`.

The vendor connection factory crosses the existing guarded lazy source on every
acquisition. Reset-safe server statement/lock deadlines bound SQL execution;
Accepted ADR-0058 requires finite enabled driver I/O and adds five-second
idle-session cleanup. The post-acceptance implementation and real source/reset
proofs are recorded below. Bootstrap verifies version, guard bodies/triggers,
exact grants and
metadata before constructing the provider. No production wrapper, scheduler
or worker is activated by this step. Runtime admission, business completion,
external execution and recurrence remain Steps 3–9.

Recurrence facts now reject sub-microsecond precision before fingerprinting so
durable PostgreSQL reconstruction cannot silently change a declared manifest.
The dependency graph, corresponding-source/notice kit and copied publish output
are part of this step's validation. Passing evidence and review dispositions are
recorded after the actual checks; source-only inspection is not a runtime proof.

**New qualification boundary.** The exact stock counter component overrides the
driver timeout with zero. PostgreSQL statement/lock deadlines pass blocked-SQL
checks, but a response-withholding proxy leaves the actual component pending.
[ADR-0058](../decisions/0058-bounded-stock-hangfire-maintenance.md) is Accepted —
2026-10-16 for the selective maintenance configuration and ownership controls
reviewed in the [reassessment](#step-2-maintenance-reassessment). The earlier
socket-exception proposal is withdrawn. The
[acceptance receipt](#step-2-maintenance-acceptance-2026-10-16) records the approved
boundary; Step 4 runtime qualification remains pending. This step
claims no Step 4 runtime qualification. Source/reset and schema validation pass
after acceptance as recorded below; formal Step 2 review closeout remains pending.

#### Step 2 counter-maintenance decision review

The isolated exact-pin response-loss experiment is independently repeated by the
root agent on PostgreSQL 18.4: the actual stock counter task is still pending at
eight seconds; closing the owned proxy sockets settles it with NpgsqlException.
Both disposable containers/proxies are cleaned up. This proves the missing
driver bound, not the proposed replacement ownership behavior.

A fresh GPT-6-astra xhigh reviewer requests two corrections to Proposed ADR-0058:
name both read/write I/O, and pace held/not-due returns because stock Core
immediately reinvokes successful components. Root verifies and incorporates both.
The shared pool minimum and real disposal/late-cleanup proofs address the same
review's availability caveat. A new GPT-6.1-sol xhigh reviewer independently
approves the corrected whole proposal with no required findings after checking
the exact Core/provider/Npgsql source and governance. Neither review claims
passing runtime tests for the unimplemented maintenance owner.

#### Step 2 maintenance reassessment

The maintainer returns Request changes, not acceptance, on 2026-10-16. The
initial proposal/reviews above are historical receipts; they do not close the new
findings. Two independent source/research agents, GPT-6.1-sol xhigh and
GPT-6-astra xhigh, verify the exact pinned Core/provider/Npgsql sources. Root
independently reproduces PostgreSQL 18.4 timeout behavior.

| Review issue | Verified disposition |
|---|---|
| Supersession/additive scope conflated | Valid. Revised Proposed ADR-0058 distinguishes selective configuration replacement from additive ownership/health/test seams; its former socket exception is withdrawn. Acceptance will name both in ADR-0047's dated amendment. |
| Suppression loses all counter maintenance | False rejection premise in the earlier draft. Stock expiration deletes expired counters and compacts the two nonexpiring state totals; removal is now the proposed alternative. |
| Expiration's five-minute wait and minimum-two sizing | Valid distinction. Retry-loop/whole-pass occupancy differs from per-command I/O. Two was never a topology capacity proof; that proposed rule is withdrawn. Step 4 must enumerate and qualify the actual supported acquisition floor. |
| Aborted transaction retains former row locks | Not reproduced. A positive active lock is released on abort; a concurrent update succeeds before the timeout response is read. Idle-aborted session occupancy remains real and is tested separately. |
| Missing interval/inventory/proof specificity | Valid. Revised proposal pins exact component/filter inventories and the expiration interval, assigns enforcement to Step 4, and maps proofs to the two existing canonical rules. |
| Incorrect timeout anchor/ambiguous defaults | Corrected. The source link names Npgsql's actual timeout selection; driver defaults, statements and invocation waits are distinguished. |

The proposed direction suppresses only stock `CountersAggregator` and optional
`StatisticsHistoryAttribute`, preserving stock expiration/heartbeat and every
finite command/socket requirement. Expiration still needs single-flight retained
ownership: cancellation does not enter its synchronous SQL or lock-acquisition
loops. Healthy pass pacing, shared shutdown and independent progress remain
pending actual-runtime obligations, not consequences assumed from suppression.
No production owner, filter removal or new source deadline is activated here.

**Reproducible qualification commands** (Python 3, Docker and .NET 10):

```bash
python3 third-party/hangfire/qualification/probe-maintenance.py
python3 third-party/hangfire/qualification/probe-pg-timeouts.py
```

The first command builds only a disposable exact-package project and starts an
owned random-loopback PostgreSQL 18.4 container. It invokes actual stock
`ExpirationManager.Execute`, the public filter-removal hook and actual stock
aggregation through an ACKing response-withholding proxy. The second uses the
PostgreSQL simple-query protocol to withhold client reads while an independent
observer inspects backend/lock state. Both use synthetic data, finite waits and
owned cleanup. They touch no development database or shared repository binaries.
Neither substitutes for `learnstack_outbox_admin`/real worker/owner proofs.

**Measured counter footprint.** Exact stock version-23 schema, default indexes,
fresh/truncated table per case and autovacuum disabled for repeatable sizing.
The denominator is vendor transitions between cleanup passes, not business jobs.
Bytes are `pg_total_relation_size(counter)` including relation/index overhead;
they exclude WAL, replication, other vendor tables and production bloat.

| Transitions | One-row ordinary success | Two-row transition envelope |
|---:|---:|---:|
| 1,000 | 1,000 rows / 172,032 B | 2,000 rows / 286,720 B |
| 10,000 | 10,000 rows / 1,048,576 B | 20,000 rows / 2,007,040 B |
| 100,000 | 100,000 rows / 9,650,176 B | 200,000 rows / 19,210,240 B |

The actual stock expiration pass reduces those six cases to one/two live rows
and preserves the tested positive totals. Ordinary VACUUM does not shrink the
previously allocated footprint in these cases. Stock delete-then-insert compaction
is not atomic, can lose a subtotal and drops nonpositive raw sums; this experiment
does not prove exact statistics accounting. Removal loses new vendor timelines.
Cleanup outages and retained snapshots can grow backlog/disk, and failed vendor
wakeups have their own nonexpiring retention. No bounded-disk or recovery-rate
claim is made.

The actual aggregator remains pending at eight seconds despite requested
cancellation and five-second server/driver defaults. Releasing its proxy sockets
settles the owned task with a driver failure; this proves the removed path's
qualification gap, not a passing replacement owner. PostgreSQL's active lock
control holds one relation lock; the aborted state holds zero and an independent
update succeeds. Two 350 ms statements pass a 500 ms per-statement limit; a
500 ms idle-in-transaction limit terminates the remaining aborted backend.
Idle cleanup is session hygiene, not a guarantee that an ACKing proxy releases
its client socket.

Standards 20's existing `hangfire`-schema drift is independently corrected against
Accepted ADR-0047 (`public`). Proposed ADR-0058 changes remain approval-gated;
the two fresh proposal review rounds and their dispositions are recorded after
they actually run.

**Revised proposal Round 1.** A fresh GPT-6-astra xhigh reviewer approves the
whole revised proposal, carrier edits and qualification artifacts with no
remaining findings. The initial component-name mismatch is corrected to the
actual `PostgreSqlHeartbeatProcess` type. Root reruns both committed-source
experiments successfully: all six footprint cases, expired/future controls,
public selective component/filter removal, eight-second blackhole retention and
late cleanup, and all PostgreSQL timeout/lock controls pass. Both owned containers
and temporary projects are removed. The reviewer independently checks 450 local
links/fragments, Python syntax and scoped whitespace. This is proposal review,
not either formal Step 2 runtime/schema review or maintainer acceptance.

**Revised proposal Round 2.** A new GPT-6.1-sol xhigh reviewer independently
approves `f5dd3a44..446d0a5f` plus the qualification-source formatting and protected
timeout-probe cleanup edits, with zero required findings/nits. Both isolated
commands pass again in that review, including all six footprint measurements,
selective component/filter controls, actual expiration and the PostgreSQL
abort/session controls. Owned teardown completes. The probe's non-whitespace C#
characters are unchanged by formatting; folder-based format verification passes.
Neither reviewer modifies files or uses proposal approval as runtime acceptance.

Seven ADR-parser controls pass and ADR-0058 parses as Proposed. The changed-doc
audit checks 37 Markdown files, 2,673 local links and 621 fragments with zero
errors; whitespace is clean. Accepted ADR bodies remain byte-identical to the
P02b-4 acceptance commit `affa0aca`. These decision experiments are not added to
backend regression counts. Both fresh proposal rounds are complete; maintainer
acceptance and Step 2/4 runtime qualification remain pending.

#### Step 2 maintenance acceptance (2026-10-16)

The maintainer explicitly approves the revised ADR-0058. Its status is Accepted —
2026-10-16. ADR-0047 Amendment 1 records the maintenance configuration replacement
and additive ownership seams without rewriting its body or acceptance metadata.
The dated proposal/reassessment/review receipts above retain their original
as-of state; this receipt closes only their maintainer-acceptance gate.

The approved configuration suppresses only exact stock `CountersAggregator` and
`StatisticsHistoryAttribute`, preserving stock expiration/heartbeat, state counters
and required admission/retry filters. Finite enabled command/socket bounds remain;
there is no unlimited-command exception. The shared source gains reset-safe
`idle_in_transaction_session_timeout=5000`, with no shared `transaction_timeout`.
At this acceptance receipt the new setting is authorized, not implemented;
source/reset, successful outbox/delivery and real aborted-idle proofs remain Step 2
work. Full Step 2 validation and both fresh runtime/schema review rounds remain
pending.

Step 4 owns exact component/filter selection, one shared expiration owner,
one-millisecond internal interval, completion-based one-hour cadence, bounded
wait/cleanup, shared shutdown and truthful retained-maintenance health. It also
owns the final simultaneous physical-acquisition census, enforced supported
configuration/floor and independent progress under contention. No minimum-two or
nonpooled prohibition is accepted. Step 9 owns full observation/enforcement and
regression. Standards 05/20/21 and the Events and Outbox architecture carry the
accepted policy; the existing bootstrap and independent-capacity obligations
remain Registered. Acceptance claims no implemented maintenance owner, runtime
proof, packet closeout or production-capacity/disk guarantee.

#### Step 2 implementation and validation after acceptance

ADR-0058 is accepted in `14bf9c42` before its dependent runtime edits. The shared
guarded source now sends reset-safe five-second statement, lock and idle-session
deadlines. Explicit bootstrap refuses disabled/excessive idle deadlines. Real
`learnstack_outbox_admin` sessions prove cold settings, the same physical backend
after pool reset, idle and aborted-idle termination while the transaction remains
checked out, and healthy reacquisition. Dirty zero/5,001 ms startup controls fail
bootstrap beside a healthy production-source control. Existing outbox/delivery
store and execution proofs pass with the new shared-source setting.

The two Platform migrations and canonical Standards 05 deliver seven raw
authoritative/control tables and stock vendor schema 23 with exact four-role
grants. Tenant/org scope, immutable envelopes/checkpoints, reservation evidence,
recurrence slots/recipients, six-stage ceilings, schema rollback/reapply and actual
ordered locking index plans have real PostgreSQL controls. Whole-schema discovery
classifies all 47 public tables, including five migration histories, rather than
omitting denied control/vendor tables. App SELECT denial requires actual `42501`.
The genericity source census now classifies embedded production SQL and decodes
its literals; Docker-trait discovery resolves partial declarations structurally.
The existing Education parent-constraint sweep includes the seven new raw job
relations with explicit guard/FK dirty controls instead of excluding them.

**Validation before the final column-grant correction:** Release CI-style build passes
with zero warnings/errors; formatting and whitespace pass. Unit 1,995,
architecture 315, contract 4, Docker-free integration 176 and complete Docker
integration 1,552 pass: **4,042 backend cases, zero failures/skips**. The focused
197-case source/outbox/delivery run also passes and overlaps the complete run;
it is not added to that total. TRX runner checks confirm each assembly ran and
none skipped. Earlier failed intermediate runs and their corrected focused
receipts are superseded by this fresh full run. The final self-review correction
also refuses column-level `GRANT OPTION` during bootstrap. Its subsequent Release
build passes with zero warnings/errors and all 34 Hangfire storage cases pass,
including the new dirty grant and restored-role control. A fresh full run of the
correction commit is recorded with the independent reviews after it completes.

Framework-dependent API publish passes the actual graph/source/notice verifier:
four vendor assemblies match original NuGet bytes, both exact corresponding-source
archives and eleven licence/notice files verify. Ten planted distribution defects
refuse and explicit recipient-replacement inspection succeeds. Hosting 10.0.12
is verified in the referenced Infrastructure graph; API/publish use the shared
framework. The actual local host is .NET/ASP.NET Core 10.0.10, not an inferred
NuGet patch. The pinned provider SQL resources remain byte-identical upstream.
The complete Apache 2.0 text accompanies its original Dapper/Core notices.
Narrow stock-byte Git attributes preserve upstream line endings/notice whitespace;
forced `autocrlf=true` checkout verifies all 32 SQL/licence/notice input hashes.
First-party whitespace checks remain enabled.

No worker, wrapper, production definition or recurrence is activated here.
Step 4 still owns selective component/filter suppression, the retained expiration
owner, enabled-path response-loss and supported-pool-boundary progress controls;
Step 9 owns complete enforcement. Those obligations remain Registered. The
full Step 2 implementation is now committed for its first fresh review round;
both round dispositions and verified fixes are recorded after they run.

#### Step 2 review round 1 and verified corrections

Three fresh reviewers examine `1b4ec7f4..583374f5`: GPT-6-astra/xhigh owns
schema/isolation/state/indexes; GPT-6.1-sol/xhigh separately owns the vendor/source
boundary and the proof/corpus lens. Their formal reports precede their authorized
fix work. Root verifies four required findings against the implementation:

- **Eligibility proof:** volatile candidate clocks scan 6,000 future rows when
  fewer than ten rows are due. Stable statement-time cutoffs now supply actual
  index bounds. Four migrated-schema controls cover zero/five due rows and detect
  the old volatile scan; live post-lock lease checks remain separately required.
- **Stored metadata:** a guard re-enabled after an invalid durable write did not
  make the old partial startup scan reject every closed field. Shared read-only
  predicates now serve post-redaction writes and startup, covering every vendor
  metadata store. Twenty-six committed-drift/restored controls exercise them.
- **Stock shape:** names/version alone accepted a missing unique key required by
  stock `ON CONFLICT`. Startup now checks 58 column facts, 17 keys/constraints,
  29 index definitions and nine sequence configurations/associations. Eleven
  dirty/restored controls cover types/defaults/nullability/keys/indexes/sequences.
- **Embedded material:** stock Core merges Cronos 0.11.1. Its exact MIT notice and
  corresponding source are now in the kit, independently pinned to source commit
  `b2050abdf910bf5c3b7262d1a09d2c04c0f56f58`, with five new failure controls.

The live decisions index also distinguishes delivered source/reset proofs from
pending Step 4 ownership and Step 9 enforcement. Accepted ADR bodies and earlier
proposal/delivery receipts remain unchanged.

Correction validation passes: Release build with zero warnings/errors; **193
focused PostgreSQL cases**, zero failures/skips, in the final focused TRX. The
actual fresh API publish verifies three source archives, twelve licence/notice
files and original stock assemblies; fifteen planted defects refuse and recipient
replacement inspection passes. Canonical guard SQL matches its embedded resource;
first-party links/fragments, history preservation and whitespace pass.

The first full correction-head Docker rerun was interrupted when its existing
container stopped along with unrelated running containers. It is not passing
regression evidence. Full regression is rerun after this correction commit and
recorded with fresh Round 2. No worker/business activation is claimed here.

#### Step 2 review round 2 and external-outcome corrections

Three new reviewers independently examine `1b4ec7f4..a593cd22`: GPT-6-astra/xhigh
owns storage/state/isolation; two GPT-6.1-sol/xhigh reviewers separately own the
vendor/source and proof/corpus lenses. The latter two approve their code/proof
scope. Root verifies the corpus review's stale live CLAUDE entry and reconciles it
with the other status carriers, preserving dated historical receipts.

The storage reviewer reproduces a prohibited second ExecuteIntent after a durable
successful external outcome using real app/dispatcher roles with guards enabled.
Root also verifies that quiescence without a known outcome was incorrectly enough
for retry/release/terminalization. The correction now:

- refuses a new normal cycle after success or selected finalization;
- requires known outcome plus quiescence, or positive no-start, for a prior normal
  attempt, excluding only the newly inserted receipt during its own admission;
- keeps unknown effects from pending/finalize/terminal transitions and reservation
  release, retaining occupancy;
- preserves failed/cancelled retry and separately counted finalize stages.

Five real-role regression cases distinguish success, failed, cancelled, selected
failure finalization and unknown effect. The original twelve-receipt positive
fixture now records retryable known failures before its last successful normal
attempt. An independent scratch PostgreSQL control observes the old Unknown path
commit a second ExecuteIntent, then verifies the corrected refusals and both
known-failure positive controls. Its owned containers are removed.

**Validation boundary:** the preceding Release binaries pass Unit 1,995,
architecture 315, contract 4, Docker-free integration 176 and Docker integration
1,594: **4,084 cases**, zero failures/skips. The final outcome correction then
passes a fresh Release build with zero warnings/errors and **198 focused
PostgreSQL cases**, zero failures/skips. Full canonical DDL equality and first-party
link/fragment/history/whitespace checks pass. Fresh final-binary regression and an
additional independent critical-fix review follow the correction commit; neither
is claimed complete in this receipt.

#### Step 2 final proof and closeout

Initial implementation is `583374f5`; Round 1 verified corrections are
`a593cd22`; Round 2 outcome corrections are `5d55ee78`. A new GPT-6-astra/xhigh
reviewer independently approves `a593cd22..5d55ee78` with no confirmed findings.
It traces first/next admission, known/unknown effects, finalize, terminal and
reservation paths, SQL-null behavior and exact refusal-message controls; canonical
SQL parity and whitespace pass. This is source review, not a claimed test rerun.

Fresh final binaries from `5d55ee78` pass **4,089 backend cases**: Unit 1,995,
architecture 315, contract 4, Docker-free integration 176 and complete Docker
integration 1,599, zero failures/skips. The final focused 198-case run overlaps
that total. TRX runner checks require all four assemblies and zero unexecuted cases.
Release CI-style build has zero warnings/errors; formatting passes. These fresh
receipts supersede the interrupted Docker rerun and earlier regression heads.

The final API publish independently verifies three complete source archives,
twelve licence/notice files, the actual package/framework graphs and original
stock assemblies. Fifteen planted defects refuse; recipient replacement inspection
passes. A forced `autocrlf=true` checkout preserves all 36 upstream SQL/material/
archive hashes. Canonical SQL/resource equality, first-party links/fragments,
Accepted ADR/history preservation and whitespace pass. Owned test/probe containers
and proxies are cleaned up; the two previously running Saydin services are running.

Both required fresh Step 2 review rounds, all verified corrections and the extra
critical-fix review are complete. No unresolved finding remains in this step.
Step 3 now owns submission and app-role admission. Step 4 still owns actual worker/
component/filter/maintenance/pool qualification; Step 9 owns full observation and
enforcement. Both extended ADR-0058 catalogue obligations remain Registered.
No production job definition, recurrence or worker is activated by Step 2.

### P02b-4 Step 3 — submission and app-role admission (2026-10-16)

**Step 3 complete; both fresh independent review rounds passed.**
Accepted ADR-0047 and the approved Step 3 plan govern this work; no new decision
or worker activation is implied by this record.

Submission stamps the actual typed producer operation, live announcement and
physical transaction. The ordinary scheduling port accepts only its declared
parameter type and owning module, requires exact tenant/organization scope,
and inserts intent on that same transaction. Serialization and injected clock/ID
operations cannot borrow stale enrollment. Successful INSERT adds a sticky
produced-work receipt; the existing owner remains responsible for commit.

Routing claims are separate from business attempts. An opaque returned-row claim
authorizes only the exact framework read/start requests. App-role envelope
validation uses a physically read-only frame before installing the verified
Ambient/SystemActor context. A separate writable start frame locks/rechecks the
row and can authorize later execution only through an acknowledged-commit
receipt. Unknown COMMIT is not activation authority; acknowledged commit remains
distinguishable from subsequent cleanup failure.

The committed proof set covers real app-role isolation, immutable routing facts,
read-only validation, unknown/Pending/early/stale callbacks, concurrent starts,
lock-wait expiry, absorbed producer refusal and uncertain commit. No business
request/handler may be constructed by admission. Capacity-bound/external starts
remain deferred until their approved implementation steps.

The first full run exposes three obsolete proof expectations: synthetic request
metadata is no longer allowed to select physical read-only mode; failed audit
rows remain durable after rollback; organization-scoped audit reads need the
same organization announcement. The corrected expectations retain all business/
job rollback assertions. A planted overload also exposes a detector exception;
explicit declaration-count diagnostics now distinguish the violation from a
clean production baseline.

Root independently reproduces DI stopping disposal after the first throwing
async disposable. The executor retains each actual unit before handler resolution
and awaits its cleanup if scope disposal faults, while keeping context/authority
installed. Physical connection-close controls pass for failed pre-read cleanup
and post-acknowledgement start cleanup. Activity observer faults also preserve
acknowledged receipts and restoration.

Release CI-style build has zero warnings/errors. All **38 focused PostgreSQL
cases pass**, zero failures/skips, including real physical generation substitution
at constructor/clock/ID callbacks, read-only RLS and write-refusal controls,
forced concurrent starts, lock-wait expiry, dropped COMMIT acknowledgement and
cleanup faults. Typed Pending submission commits backlog but cannot route/start.
Unit 2,018, contract 4 and Docker-free integration 176 pass; the complete
architecture suite separately passes 315 cases, including dirty/clean capability
controls. Full solution formatting verifies 972 files without changes; seven ADR
parser controls and local link/fragment/history/whitespace checks pass.
The final combined Docker-free receipt and complete Docker regression remain
pending in this receipt. Neither formal review round is claimed by self-review.

#### Step 3 review round 1 and verified corrections

Initial implementation is `63a7f4ac`. Three fresh independent reviewers inspect
`483463fa..63a7f4ac`: GPT-6-astra/xhigh for authority/transaction boundaries,
GPT-6-astra/xhigh for PostgreSQL/admission/concurrency, and GPT-6.1-sol/xhigh
for proof/census/composition/corpus. Authority and proof reviews approve with
no findings. Storage requests one verified correction: submission inherits
the general app source's 30-second or unlimited command timeout, violating
the accepted positive SQL budget of at most five seconds.

The storage reviewer independently reproduces the pinned-driver defaults and
real PostgreSQL blocked-INSERT behavior in a private disposable database. Root
confirms the actual production command and accepted bound. Submission now validates
the existing runtime options, applies an explicit command timeout and links its
SQL cancellation deadline. Successful INSERT alone records produced work;
statement failure still poisons the same owner. The same source review also
identifies that announcement/COMMIT surrounding framework commands needs a bound;
the executor gives each exact read/start frame its own SQL deadline, retained
through actual cleanup. Unknown COMMIT still creates no activation receipt.

The combined Docker-free run passes all four assemblies: Unit 2,018,
architecture 315, contract 4 and integration 176 — **2,513 cases**, zero failures
or skips, with runner nonempty/zero-skip validation. The initial complete Docker
run executes 1,637 cases: 1,636 pass and one old domain-event cleanup test fails
because its reflection call still supplies three producer-enrollment arguments
after the signature gained two optional attribution arguments. Root checks every
reflection reference and adapts that test to five arguments without changing its
cleanup/rollback assertions. This failed run is not a passing regression receipt.

The corrected Release build passes with zero warnings/errors. All 66 focused
PostgreSQL cases pass, zero failures/skips: the 40 Step 3 cases include a real
FK-lock-blocked INSERT and a live session withholding the actual start COMMIT
acknowledgement; the other 26 cover domain-event production and the adapted
reflection call. The timeout controls use an unlimited actual app command default,
prove the production deadline wins before their independent proof timeout, and
retain poisoning, no-false-receipt, physical cleanup and restoration assertions.
Unit 2,018 and contract 4 pass again. The combined full backend rerun remains
in progress; no empty-filter run is counted as evidence. Round 2 has not started.
No runtime outside the approved Step 3 boundary is added.


#### Step 3 review round 2 and final closeout

Three new independent reviewers inspect the complete `483463fa..0ddcb3e7`
implementation and its `63a7f4ac..0ddcb3e7` correction: GPT-6-astra/xhigh for
application authority/owner boundaries, GPT-6-astra/xhigh for PostgreSQL/storage,
and GPT-6.1-sol/xhigh for proof/census/composition/corpus. All approve with zero
verified Blocker, Major or Minor findings. They inspect actual source and existing
receipts without shared builds or repository edits; their source reviews do not
claim independent test execution or a completed full regression.

Root's final CI-split regression at runtime head `0ddcb3e7` passes Unit **2,018**,
architecture **315**, contract **4**, Docker-free integration **176** and Docker
integration **1,639** — **4,152 backend cases**, zero failures/skips. The correct
`Requires=Docker` run includes the forty Step 3 PostgreSQL cases; the focused
66-case receipt additionally repeats all 26 domain-event production controls.
Every counted assembly/slice passes the nonempty/zero-skip runner assertion.
Release build remains zero warnings/errors; formatting, seven ADR parser controls,
local links/fragments, prior-ADR/history preservation and whitespace pass.

A mistaken `Category` filter mixed HTTP and Docker tests in one integration
process, exposing startup/Activity assertions; that run was canceled and is not
passing evidence. An empty filter was also rejected by the runner and is not
counted. The exact `Requires` slices above replace those commands without
weakening production code or test assertions. The earlier reflection-signature
failure remains recorded in round 1; its corrected domain-event test passes.

Both required review rounds and every verified correction are complete. Step 4
owns stock workers, bridge/repair, component/filter selection, expiration ownership
and supported-pool qualification. Step 5 owns business completion/retry/terminal
execution; capacity/external/recurrence and full enforcement retain their approved
Steps 6–9. No production business definition, recurrence, worker or dashboard is
activated by Step 3. Work remains on `development`; no push, PR or CI-head success
is implied by this local step closeout.

### P02b-4 Step 4 — server-generation decision (2026-10-16)

**Current authority:** [maintainer acceptance and the Step 4 adaptation](#maintainer-acceptance-and-step-4-adaptation-2026-10-16)
below accept ADR-0059 on 2026-10-16. The following preparation and review receipts
retain their original pre-acceptance state; implementation is in progress and full
production qualification remains pending.

**Preparation only; ADR-0059 Proposed, approval pending.** Step 3 closes at
`def48a1a`, with runtime `0ddcb3e7` and both required fresh review rounds complete.
The complete H1/expanded implementation remains authorized, but the newly exposed
server-lifecycle choice needs explicit approval: ADR-0047/0058 select H1 and
retention, but do not select a constructor or automatic-restart policy.
No dependent worker, maintenance or restart runtime is activated here.

Exact-pin source inspection exposes a lifecycle qualification gap: Core links
child shutdown to its restart token, catches canceled joins and disposes live
children before announcing a replacement. `ShutdownTimeout=Infinite` alone does
not fix automatic restart. A connection-floor calculation based only on one
configured generation is therefore conditional, not an accepted/proven number.
The stock missing-server and sustained-heartbeat-failure paths both matter.

[ADR-0059](../decisions/0059-owned-hangfire-server-generations.md) proposes an
owner per pool, public stock process composition, synchronous context capture,
classified heartbeat retries and replacement only after actual stock/LearnStack
cleanup. An affected pool can remain unhealthy/stopped while its work is unresolved;
healthy pools remain independent. Host stop retains the existing one finite
external budget. Stock SQL/binaries, roles/grants, job attempts, reservations and
business authority are unchanged. A restart never supplies external quiescence.

The decision-support probe uses actual pinned Core 1.8.25 dispatcher/server code,
provider 1.21.1 loaded identity and the repository's pinned Npgsql/Newtonsoft graph,
with fake storage to force lifecycle events. It uses no production credential,
existing service/database or shared repository build output. The launcher builds
in an owned temporary directory and takes versions from the central package file:

```sh
python3 scripts/probe-hangfire-lifecycle.py
```

| Probe | Required distinction |
|---|---|
| Stock restart | Second generation while first actual work remains live; old context token handles are disposed despite infinite internal shutdown |
| Fault before synchronous capture | Actual required watchdog is held; stop occurs first; capture still executes, supplied workers stay closed, no early disposal/replacement |
| Fault after worker start | One actual generation remains owned through finite caller wait; release precedes shutdown witness and actual join |
| Managed supplied-builder fault | Guarded failure requests draining, retains earlier actual work and skips later builders |
| Raw supplied-builder fault | Dirty control reproduces early joined outer owner and disposed tokens while actual work remains live |
| Direct process-exit callback | Dirty child remains live inside Core cancellation after the finite host wait; parent terminates and joins it |
| Closed process-exit relay | Actual Core exit runs without joining the held private callback/stock process; child exits zero, without a cleanup/quiescence claim |

These are assertion-bearing isolated feasibility controls, not LearnStack's
production worker/PostgreSQL regression or implemented catalogue enforcement.
Generic heartbeat retries, multiple pools, partial-resource construction,
source disposal, actual wrapper/business cleanup and restart-after-cleanup still
owe real production controls. Fatal memory/thread-start failures are not qualified
by the controlled managed-builder experiment. Missing context or elapsed waiting
cannot be interpreted as successful cleanup.

#### Proposal review round 1 and verified corrections

Two fresh independent reviewers inspect `def48a1a..f8ff691f`: GPT-6 Astra with
xhigh effort covers lifecycle/authority; GPT-6.1 Sol with xhigh effort covers
proofs/corpus/governance. The proof reviewer independently builds/runs all five
initial controls, checks links/history and interrupts the actual launcher to
verify owned-child cleanup. It approves without a finding. The lifecycle reviewer
confirms two Major decision gaps; root verifies both against pinned vendor source:

- The cached provider fetched-job heartbeat cannot belong to the replaceable
  control generation. The revision gives shared stock heartbeat/expiration an
  API-instance owner independent of every pool; all Core servers exclude provider
  component hosting. Control-pool-held-drain/healthy-pool renewal becomes an
  explicit real-production proof obligation.
- Core's synchronous process-exit cancellation is a separate boundary from the
  finite host wait. The revision selects zero last-chance wait and a closed,
  asynchronous cancellation relay, requires child-process qualification and
  forbids treating exit/forced token signals as cleanup or external quiescence.

The lifecycle reviewer then provides isolated exit-control implementation help;
that support is not a fresh review of the revision. Root inspects/integrates it
and independently passes all seven committed controls with zero build warnings
or errors. The unsafe direct callback stays held until parent-owned termination;
the positive observer confirms actual Core exit while private work remains held.
Every child is joined before its temporary build tree is removed. These remain
narrow feasibility controls, not production qualification. ADR-0047/0058 are
byte-preserved; acceptance and all dependent Step 4 runtime remain pending.

Root also interrupts the launcher while its unsafe exit callback is held:
launcher exit 143, the actual child is gone and the owned temporary tree is
removed. Five Release corpus controls and seven ADR parser controls pass; the
first-party sweep checks 37 Markdown files, 2,708 local links and 643 fragments
without errors and preserves all prior authorized history. These are focused
proposal checks; the full 4,152-case Step 3 result is not rerun or relabelled.

#### Proposal review round 2 and approval-readiness closeout

Two new independent reviewers inspect `def48a1a..878bcd49` after the verified
first-round corrections: GPT-6 Astra with xhigh effort covers lifecycle/authority;
GPT-6.1 Sol with xhigh effort covers proofs/governance. Both approve the Proposed
change with no Blocker, Major or Minor finding. Each independently builds/runs all
seven isolated controls with zero warnings/errors. The lifecycle reviewer verifies
committed source archive hashes and extracted bytes. The proof reviewer also
interrupts the actual unsafe-callback child and forces the external watchdog;
both paths join the real child and remove the owned temporary tree. A watchdog
failure is not reported as a successful experiment.

The initial proposal/probes are `f8ff691f`; verified first-round corrections and
exit controls are `2103f059`; the primary status pointers are `878bcd49`. Root
reads both final reports and execution logs, verifies focused final results of
five Release corpus controls and seven ADR parser controls, and repeats the
37-file sweep: 2,711 local links, 646 fragments, zero errors. All 53 previously
Accepted ADRs are byte-identical across this proposal range. No full backend,
production PostgreSQL/worker or multi-pool result is inferred from these checks.

Proposal review is complete; ADR-0059 remains Proposed pending maintainer
acceptance. This closeout changes only the review receipt, not the reviewed
policy/probes. The user's requirement to approve new ADRs before dependent
implementation remains the gate. Step 4's real qualification and Steps 5–9 are
pending; no worker, maintenance owner or replacement policy is activated here.


#### External proposal review and verified revision (2026-10-16)

The maintainer's response to the approval request is a review, not acceptance.
Root verifies its findings against `b49998d9`, the Accepted records and pinned
Core/provider source, with separate lifecycle, storage-surface and options support
investigations. Those investigators are not the fresh review rounds below.
ADR-0059 remains Proposed and no dependent runtime is authorized by this revision.

| Finding | Verified disposition |
|---|---|
| B1 outer retry/repeat owner | Valid. Core retries failed announcement before capture and repeats normal server return independently of heartbeat. Add a preallocated owner, lazy complete lifecycle boundary, one-shot announcement and terminal stop fence including removal/acquisition/disposal. |
| B2 concrete seam and surface | Valid. Name the per-pool `JobStorage` / `JobStorageConnection` decorators, lazy acquisition and in-boundary lifecycle disposal. Preserve extended capabilities, actual transactions/fetched jobs and shared inventory; pin the obsolete API/upgrade and real-provider parity gates. |
| B3 StopTimeout and running work | Valid. Fix both stop/shutdown timers to Infinite. Local drain closes fetch/admission without canceling admitted private work; host stop has a separate retained asynchronous cancellation signal. Pre-admission wakeups may requeue. |
| B4 availability and alternatives | Valid tradeoff. Document shared-outage/all-pool drain and whole-pool blocking despite a spare slot or storage recovery. Compare no replacement and bounded two-generation shared budgets honestly; current probes do not disprove qualified overlap. Retain the proposed one-generation choice for approval. |
| B5 delayed/recurring dependency | Valid. Control drain stops native timers. Require independent same-generation bridge repair in Step 4 and missing-tick durable occurrence recovery in Step 8, without inventing a universal latency SLA. |
| B6 fixed options/metadata/margin | Valid. Pin server intervals, four-minute heartbeat cutoff below five-minute expiry, one-second pacing/five-second owner backoff and exact Queues/WorkerCount dictionary values. Step 4 startup validates these constants; they are not silently new public options. |
| B7 replacement classification | Valid. Earlier ADRs selected stock pools/retention, not a convenience constructor or automatic-restart policy. Correct the Proposed ADR/live summary to an additive lifecycle decision; accepted history is untouched. |
| B8 census/catchability | Valid. Name forbidden facade/registration paths and exact production capability scans. Catchability matches the pinned Core exact-type OOM/StackOverflow exclusions; require actual managed fault premises and closed diagnostic callbacks, not arbitrary throwing/blocking observers. |
| B9 upstream issue | Suggestion, not an implementation requirement or authorization to publish. No public issue is filed; a concrete qualified upstream change remains an alternative, not assumed available evidence. |

The second report's smaller terminology/reference notes are also verified:
distinguish per-server metadata from storage-wide fetched-job heartbeat, separate
component/filter suppression, label the finite pool observation dimension without
mapping readiness, correct cancellation-timer wording and explain the whole-pass
heartbeat wait. Add the Database/Observability/Events and Outbox references.

Root integrates twelve additional isolated actual-Core controls into the same
owned launcher, alongside the original seven. The current builder/exit controls
use infinite stop timeout; exit cancellation now receives an explicit host signal,
not the pool-local stopping token. All nineteen pass with zero build warnings or
errors. The new controls distinguish:

- Raw announcement failure and normal-return repetition from stop-fenced variants.
- Announcement acquisition/disposal and removal/acquisition/disposal failures,
  including faults held before owner handle publication and without capture.
- An actual caught non-InvalidOperation managed builder fault, with no later start
  and earlier actual work/tokens retained; the premise cannot be skipped by stop.
- Infinite versus finite StopTimeout while actual work stays live, followed by
  an independent host signal with its real cancellation/relay tasks held and joined.

The normal-return pair deliberately allows a stock heartbeat restart with no held
old work to isolate outer repetition/removal fencing. It does not qualify supported
heartbeat containment. Fake lifecycle resources and held `IBackgroundProcess`
work prove neither real PostgreSQL/Worker cancellation nor partial mandatory
thread construction, complete provider parity, common-outage/multiple-pool repair
or actual replacement. All remain named production activation gates. The earlier
seven-control review receipts retain their as-of scope; they are not retrospectively
expanded to cover this external revision.

Root's isolated whitespace/Release verification passes after integration;
all nineteen distinct controls pass, along with five Release corpus cases and
seven ADR parser controls. The first-party history/link sweep is clean. These
checks do not rerun or relabel the earlier full 4,152-case Step 3 evidence.

#### External revision review round 1

Two fresh independent reviewers inspect `b49998d9..1853b48f`: GPT-6 Astra with
xhigh effort covers lifecycle/authority; GPT-6.1 Sol with xhigh effort covers
proofs/governance. Both independently pass all nineteen controls with zero
warnings/errors. Lifecycle reports no finding; proofs reports no Blocker/Major
and one Minor historical evidence-count error, verified and corrected below.
The proof reviewer also removes the actual managed exception in an isolated
mutant: the builder control turns red. Missing receipts, startup failure and
silent watchdog expiry are rejected; the actual timed-out child is joined.
Root reads both final reports/logs. These reviews do not certify production gates.

**Evidence-count correction — 2026-10-16.** The earlier proposal round-2 receipt's
“53 previously Accepted ADRs” is incorrect. Root and the new proof reviewer use
`scripts/adr-status.py`'s canonical `lifecycle()` parser, not a text occurrence
count: both `def48a1a` and `b49998d9` contain 51 currently Accepted canonical ADRs,
all byte-identical at `1853b48f`. The older receipt remains as-of text; this dated
correction replaces its denominator, not the byte-preservation result. It does
not claim the original reviewer ran this corrected count. ADR-0059 is Proposed.

#### External revision review round 2 and availability correction

Two new independent reviewers inspect `b49998d9..b4a6fec0`: GPT-6 Astra with
xhigh effort covers lifecycle/authority; GPT-6.1 Sol with xhigh effort covers
proofs/governance. Both independently pass all nineteen controls with zero
warnings/errors and reproduce the canonical 51-ADR count/byte preservation.
The proof reviewer independently rejects missing receipts, startup failure and
watchdog expiry, joins the real child and turns the managed-fault mutant red.

Both initially report a clean proposal. Root's final cross-check then identifies
an additional governance conflict: ADR-0047's unrelated-tenant progress promises
are broader than ADR-0059's whole-pool drain below four retained overruns. The
lifecycle reviewer independently confirms it and appends one Major, explicitly
narrowing its earlier verdict before acceptance. Root verifies the actual clauses;
their exception cannot be assumed implicit merely because the older contract
already had a four-overrun threshold.

The correction keeps constructor/restart composition additive, but names a
bounded availability supersession of ADR-0047's Tenant capacity and independent
pools / Lifecycle and observation progress clauses. Same-pool unrelated tenants
may wait during generation-control draining despite a spare slot after storage
recovery and below four overruns. Healthy other pools, hard bounds, reservations
and independent orchestration are preserved. Acceptance will disclose this precise
change in ADR-0047; ADR-0058's ownership note remains additive. Prior Accepted bytes
are untouched. This correction changes classification/explicit contractual scope,
not the already stated single-generation behavior or the nineteen probe sources.

#### External revision focused verification and approval-readiness closeout

Two new independent reviewers inspect `b4a6fec0..35b6985d`: GPT-6 Astra with
xhigh effort covers governance and normative scope; GPT-6.1 Sol with xhigh effort
covers corpus/history and evidence mechanics. Both confirm the earlier Major is
resolved without widening the affected-pool exception. The first reviewer flags
one Minor in `71221799`: the four-overrun threshold stops new starts, not actual
retention. Root verifies/fixes it in `35b6985d`; that reviewer preserves its first
report and appends exact-head verification. Both final focused verdicts approve
with no unresolved findings in their stated scope.

Root reads both final reports and verifies five Release corpus controls, seven
ADR parser controls and the first-party history/link sweep: 37 Markdown files,
2,716 local links, 648 fragments, zero errors. The canonical lifecycle parser
reproduces 51 Accepted ADRs at `def48a1a`, `b49998d9`, `b4a6fec0` and `1853b48f`,
all byte-identical in the final tree. ADR-0059 remains Proposed. The final
classification/wording corrections change only Markdown; the nineteen-control
sources are unchanged since `1853b48f`, so their existing passing receipts are
preserved without claiming a new runtime run. Diff/prose-width checks pass.

The external revision is `1853b48f`; first-round/count correction receipt is
`b4a6fec0`; bounded availability disclosure is `71221799`; retention precision
is `35b6985d`. The initial clean round-2 verdict and its later Major addendum
remain explicitly recorded above. This final receipt changes no reviewed policy
or probe. It is neither maintainer acceptance nor real production qualification.

The revised ADR and Step 4 adaptation are ready for maintainer approval under
the user's new-ADR gate. Same-pool blocking during generation-control drain,
including below four overruns after storage recovery, is an explicit decision
for that approval. Step 4's real worker/PostgreSQL, startup/resource, wrapper
parity, multi-pool and exit controls remain mandatory before activation; Steps
5–9 remain pending. Work stays on `development`; no dependent runtime, push,
PR or current-head CI result is implied by this proposal closeout.

#### Maintainer acceptance and Step 4 adaptation (2026-10-16)

**Accepted — 2026-10-16, @cemil.** The maintainer explicitly approves revised
[ADR-0059](../decisions/0059-owned-hangfire-server-generations.md) and the Step 4
adaptation reviewed at `0db27eb6` on `development`. This closes the new-ADR
approval gate and authorizes dependent implementation after the acceptance
commit. It completes no implementation step or production activation proof.
The nineteen-control experiment and earlier review/validation receipts retain
their recorded scope; acceptance neither reruns them nor expands their claims.

The accepted change has two distinct parts:

- **Additive lifecycle integration:** public stock process composition, one
  actual generation per pool, synchronous context capture, closed storage and
  lifecycle disposal fences, classified metadata-heartbeat retry, sole owner
  replacement after actual stock/LearnStack cleanup, separate shared component
  ownership and a qualified closed process-exit relay.
- **Bounded availability supersession:** only the two ADR-0047 progress clauses
  quoted in ADR-0059 are qualified during generation-control drain. Unrelated
  tenants in the same pool can remain blocked after storage recovery, with a
  spare slot and below four retained overruns, until actual cleanup completes.
  Healthy other pools remain independent subject to common dependencies.
  Four overruns still stop new starts in an otherwise serviceable generation;
  actual retention continues. Hard bounds, fair independent orchestration,
  attempts, reservation ownership and positive quiescence remain unchanged.

[ADR-0047 Amendment 2](../decisions/0047-transactional-background-job-runtime.md#amendment-2--owned-server-lifecycle-and-bounded-availability-supersession-2026-10-16)
discloses both parts. [ADR-0058 Amendment 2](../decisions/0058-bounded-stock-hangfire-maintenance.md#amendment-2--independent-shared-component-ownership-2026-10-16)
is additive component-ownership navigation only. Both preserve the prior
Accepted bodies, acceptance metadata and earlier amendments. No whole-record
supersession or ADR-0041 fact correction is selected.

**Adapted Step 4:** the original H1 wakeup/bridge/wrapper/pool work remains in
scope, with ADR-0058's component/filter and maintenance obligations. ADR-0059
adds the public compositor and complete concrete storage/connection surface,
one-shot announcement/removal stop fences, exact fixed options/queue metadata,
local drain without business cancellation, retained actual stock/LearnStack
joins and API-instance shared fetched-job heartbeat/expiration hosting.
The closed diagnostic observer, cancellation relay and process-exit boundary
must be qualified before worker activation; unresolved startup/resource cleanup
refuses activation and reopens the decision, rather than granting replacement.

Real pinned worker/PostgreSQL controls remain mandatory for full provider-surface
parity and capability census; acquisition/disposal/startup and heartbeat faults;
nonoverlap and no early token/resource disposal; control-pool drain with healthy
pool renewal and due delayed-handoff repair; common outage/recovery with a held
operation and a spare slot still closed; host stop/backoff/late completion; and
direct-callback versus closed-relay process exit. The final simultaneous physical
acquisition census must enforce its qualified supported configuration/floor and
prove progress at that boundary; no guessed numeric floor is accepted.

Step 8 retains missing-native-tick recurrence/CoalesceLatest qualification.
Steps 5–9 retain business completion/terminal audit, tenant capacity, external
effects, recurrence and final enforcement under their existing approved scope.
The nine-step sequence and two fresh independent review rounds per step remain
authorized. Phase 03 operator recovery and Phase 11 readiness/supervision remain
with those owners.

**Carrier inventory at acceptance:**

| Carrier | Acceptance effect / remaining implementation owner |
|---|---|
| [Standards 05](../standards/05-database.md#background-jobs) | Existing canonical schema/grants/source deadlines remain unchanged. Step 4 must qualify every concrete connection/compositor acquisition lane and the supported pool boundary; this acceptance adds no DDL or grant |
| [Standards 10](../standards/10-observability.md#health-checks) | Accepted lifecycle observation reference only. Step 4 supplies truthful drain/heartbeat/shared-component health; Step 9 owns final instruments/censuses. No new emitter or mapped readiness is claimed |
| [Standards 20](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire) | Accepted lifecycle and bounded availability reference; implementation/activation remain pending |
| [Standards 21](../standards/21-architecture-tests-catalogue.md) | ADR-0059 extends the existing bootstrap, independent-capacity and credential-confinement obligations by reference; all three remain Registered. Step 4 supplies real subjects/controls; Step 9 closes enforcement. No rule, method or Implemented count changes |
| [Events and Outbox architecture](../architecture/15-event-and-outbox.md#background-job-transport-and-maintenance) | Accepted server/component ownership and availability reference; delivered Steps 1–3 and pending Steps 4–9 remain distinct |

This documentation-only acceptance precedes dependent runtime changes. It
claims no new worker, maintenance owner, production qualification, full backend
rerun, CI result or packet closeout. Earlier pending-approval wording is dated
history; current implementation and activation remain pending.

#### Step 4 required-startup qualification (2026-10-16)

ADR-0059 acceptance commits at `6c52eaa1` before dependent work. Root verifies
two independent source/design inspections (GPT-6-astra xhigh and GPT-6.1-sol
xhigh) against pinned Core and official .NET 10.0.10 source. Normal lifecycle
containment is implementable, but the literal required-startup join-before-dispose
obligation remains unqualified through the current public seams.

Core starts metadata heartbeat and prepends private watchdog/cancellation builders
before supplied capture. If required construction throws, it skips the child join
and disposes prior controls before removal reaches the storage fence. Keeping that
uncaptured generation retained prevents replacement, but cannot undo disposal.
.NET distinguishes exact native-create OOM from a later initialization wrapper;
Core's exact exception predicate does not itself exclude ThreadStartException.
The identified Unix path concerns stack/TLS/runtime resource initialization.
No genuine Linux resource-failure experiment, ordinary SQL/configuration
counterexample or frequency estimate is claimed. A hostile global logger or
invalid input is not a supported-production counterexample.

[ADR-0060](../decisions/0060-fatal-hangfire-startup-and-unsafe-unwind.md) proposes the narrow
fatal-process alternative: after prevalidation, unowned constructor/required
startup failure closes starts/replacement and terminates the entire API without
a graceful-cleanup claim. This interrupts unrelated pools/HTTP traffic; durable
attempts, external uncertainty and reservations remain authoritative. Normal
announcement/database faults, captured builders, local drain and host stop retain
ADR-0059's policy. The proposal neither accepts itself nor authorizes termination
runtime. It applies ADR-0059's existing decision-reopening gate.

Independent bridge, closed diagnostics, selective component/filter inventory and
storage/parity work continue under the Accepted contracts. They do not qualify
the complete server owner or activate workers. New proposal review receipts and
actual implementation/test results will be recorded after they occur; no new
passing production test, full Step 4 completion or current-head CI is implied.

##### Required-startup proposal review round 1

Two fresh independent reviewers inspect `6c52eaa1..34be14e6`: GPT-6-astra xhigh
covers policy/lifecycle and GPT-6.1-sol xhigh covers proofs/governance. Both
approve the Proposed documentation for maintainer consideration with no
Blocker/Major, independently identifying one Minor: the consequence's “rare”
frequency qualifier is unsupported. Root verifies/removes it, preserving the
explicit absence of a genuine resource-failure experiment or likelihood estimate.
The source-based state trigger, entire-host consequence, bounded supersession
and pending implementation/proof gates remain unchanged.

Root reads both final reports. Seven ADR parser controls, the six-file link/
fragment sweep and diff/prose checks pass; all 52 baseline Accepted ADRs remain
byte-identical. These are proposal checks, excluding the uncommitted independent
bridge/wrapper/diagnostic slice. A new second review round follows this correction;
ADR-0060 remains Proposed and no fatal-process runtime is authorized.

##### Required-startup proposal review round 2

Two new independent reviewers inspect `6c52eaa1..ac5a1cd3`: GPT-6-astra xhigh
covers policy/lifecycle and GPT-6.1-sol xhigh covers proofs/governance. Both
approve the Proposed documentation for maintainer consideration with zero
Blocker/Major/Minor/Suggestion findings. Root reads both complete final reports.
The proof review independently checks all 52 baseline Accepted ADRs byte-for-byte,
ADR-0059 Accepted/ADR-0060 Proposed, 706 local links and 295 fragments with no
missing targets, unchanged catalogue bytes/counts, added prose/private-reference
checks and clean diff whitespace. Neither review runs shared builds, database
experiments or production qualification; no genuine Linux resource-failure or
frequency claim is introduced.

The proposal is ready for maintainer consideration. Its entire-API interruption,
operator restart and absence of graceful cleanup remain explicit costs; fatal
runtime and activation still require acceptance. Independent Step 4 foundation
validation proceeds under the existing Accepted decisions and is recorded
separately. Steps 4–9 remain incomplete; no push, PR or current-head CI is claimed.

##### Independent Step 4 foundation slice (2026-10-16)

This slice implements the already-authorized H1 transport and public storage
boundary while ADR-0060 remains Proposed. It does not implement fatal termination,
compose Core generations, host shared maintenance or activate API workers.
Full Step 4 and Steps 5–9 remain pending under their accepted ownership.

- The bridge claims committed intent in a short privileged transaction, retains
  at most four actual synchronous stock-client calls on background threads and
  suppresses ambient execution context/culture. Vendor IDs remain hints; absent,
  failed or succeeded vendor rows permit same-generation repair without spending
  business attempts. ACK/deferral locks the actual row before rechecking current
  database-time lease/token/generation fences; callback admission may precede ACK.
  Host stop closes acquisition and retains actual calls through late completion.
- Exact opaque wrappers and a five-token resolver reject arbitrary CLR lookup.
  The closed activator binds a registered pool, invocation and single-use scope;
  it reaches Step 3's framework admission only. Business completion stays Step 5;
  recurrence materialization stays Step 8 and cannot currently report success.
- Concrete pool storage/connection wrappers preserve the complete pinned public
  surface, provider features, extended casts, transactions, fetched jobs and locks.
  Only announcement, metadata heartbeat and removal enter the closed lifecycle
  operation. Failed disposal remains retained; this is no proof of the pending
  complete generation owner or every physical provider cleanup outcome.
- Fail-closed component/filter inventories retain exact stock instances and required
  filters while suppressing the counter aggregator/history. Vendor diagnostics use
  fixed memory-only counters without evaluating messages or invoking observers.
  Existing capability, authority, transaction and parallelism censuses enumerate
  the new exact callers and retain their planted extra/missing-site controls.

Real delayed creation exposed a predicate mismatch: stock explicit-queue scheduling
stores `queue:id` in the `schedule` set, while the prior guard admitted only numeric
IDs. The fix preserves immutable queue binding, restricts the added form to that
key and bounded queue/ID grammar, and reconciles the canonical SQL. An additive
Platform migration supports already-installed stores; reverse/reapply, malformed
and wrong-key controls exercise the boundary. Fixture corrections preserve the
actual ACK-after-lock-expiry and below-filter redaction assertions, and replace the
old missing-wrapper assumption with a positive load of the now-present wrapper.
Earlier recorded Step 2/3 evidence is unchanged.

**Initial slice validation:** Release warning-as-error build passes with zero
warnings/errors. Unit 2,056, Architecture 315, Contract 4, Docker-free HTTP
integration 176 and focused real-PostgreSQL bridge/parity/storage/rollback 94
pass: 2,645 tests, zero failures/skips. TRX counters are checked directly and by
the guarded test receipt. Seven ADR parser controls and a three-file sweep of
533 local links / 193 fragments pass. C# formatting and diff whitespace pass.
This is a focused slice receipt, not a full backend/renderer/demo/CI rerun.

The first runs found the queued-schedule guard mismatch, a completed-locker
rollback in the proof, an inadmissible RetryCount test value and the old absent
wrapper assumption. The corrected PostgreSQL set passes in full. Initial
architecture failures identify only newly sanctioned caller inventories; exact
entries and forwarding/removal mutants now pass the full architecture assembly.
No failing check is skipped or broadened to allow arbitrary callers.

Two fresh independent slice-review rounds follow the initial implementation
commit. They do not close full Step 4's real generation, maintenance,
cancellation/exit, multi-pool progress or connection-floor gates.

##### Independent foundation review corrections (2026-10-16)

The initial foundation review was interrupted before final reports; it is not
counted as a completed round. A resumed independent GPT-6-astra xhigh R1 inspects
the complete committed slice and surrounding runtime/census paths. It verifies
one Major: raw filter concatenation bypasses stock precedence and lets the global
retry execute before the wrapper's zero-retry attribute. The separately identified
bridge-source forwarding gap also needs exact capability enforcement.

Commit `9db4822d` uses the stock filter-provider collection, preserves the admitted
global instances and pins the bridge store's signatures, source captures,
acquisitions, consumers and holders. Actual StateMachine controls distinguish
failed-state retention from the raw-concatenation retry/schedule outcome. Two
fresh independent R2 reviewers (GPT-6-astra xhigh runtime and GPT-6.1-sol xhigh
proof/security) inspect the corrections. The proof reviewer finds one further
Major: a pre-creation global retry registered at order 100 wins stock precedence.
Root verifies it and refuses nonstock retry registration order before changing
history; both wrappers now have effective-filter and actual state-election controls.
Both reviewers independently verify the final fix with zero remaining findings.

Before the last two-line registration guard, Release build, Unit 2,058,
Architecture 317 and focused real-PostgreSQL 93 pass, with zero failures/skips.
After that guard and its added controls, Release build remains warning/error-free,
all 2,061 unit cases and four targeted credential/catalogue architecture cases
pass. Direct TRX counters and the guarded runner confirm execution and zero skips.
Pre-commit formatting leaves all four reviewed C# hashes unchanged. This is not a
new full backend/renderer/demo/CI receipt; no fatal runtime or worker is activated.
Full Step 4's complete-runtime review and qualification gates remain open.

##### ADR-0060 external-review revision (2026-10-16)

The maintainer requests changes to the Proposed record. The earlier two clean
proposal-review receipts remain historical; they do not close these later verified
gaps or authorize fatal runtime. Root rechecks pinned Core and official .NET source,
reads two independent state/policy analyses and revises the proposal before seeking
new approval. No genuine Linux resource failure, new runtime experiment or incidence
estimate is claimed.

The revised file is
[ADR-0060: Fatal Hangfire Startup and Unsafe Unwind](../decisions/0060-fatal-hangfire-startup-and-unsafe-unwind.md).
Renaming the Proposed file reflects its explicit before/after-capture scope; earlier
record links resolve forward to that current proposal. All Accepted ADR bodies,
metadata and prior amendments remain unchanged.

| Verified review gap | Revision / deliberate boundary |
|---|---|
| M1: fatal rationale relied on the old cleanup promise | Quarantine remains a viable policy alternative. Fatal is a conservative integrity/visible-failure preference with the whole-API cost, not technical necessity or proof that private controls execute business work |
| M2: Phase 11 ownership was absent from its roadmap | Reliability scope, deliverables and completion now explicitly own supervision/restart/backoff/crash-loop, termination/dump policies and the triggered worker-process topology decision; no present supervisor is claimed |
| M3: constructor predicate exceeded proved live-thread cases | Entire prevalidated constructor failure is explicitly conservative, including pre-thread false positives; no private instruction boundary is inferred from exception type/text |
| M4: capture did not cover Core post-builder/notification unwind | Separate progress and conditional normal-join facts; owned disposal, removal, second-announcement and outer-completion observations cover skipped waiting/removal. Closed logger/timer/final-exit premises must be qualified before activation |

The revision also names process-wide starts/ExecuteIntent gates and earlier-admission
uncertainty, host-stop versus final-teardown priority, retained unhealthy stalled
startup under the existing ProgressTimeout, finite pool/stage diagnostics, runtime/
Linux resource/collector dump prerequisites and orphan process-tree consequences.
Exact ADR-0059 qualification/availability clauses and its Option 4 are disclosed;
three existing canonical test obligations receive explicit prospective assignments.
No new rule/count/status or caller enforcement is declared implemented.

The suggested automatic exact-OOM crash outcome is not established and is not used
as a policy argument. Existing API blast-radius, manual-restart, dump-risk and durable
reservation disclosures were already present; their concrete controls/owners are
strengthened rather than presented as newly discovered guarantees. Synthetic fault
placement remains distinct from real stock and genuine resource evidence. Proposed
and Accepted policies are not silently conflated.

The independent foundation corrections above own their source capability and
filter proof/review receipt. Neither workstream completes Step 4 or permits
fatal activation. The following records own the proposal's fresh reviews.

**Revision review round 1:** two fresh independent reviewers (GPT-6-astra xhigh
lifecycle and GPT-6.1-sol xhigh governance/proofs) inspect the frozen eight-document
revision against `422519b0`. Both request changes for one Major and one Minor.
Root reads both full reports, verifies ADR-0047's preserved other-pool progress
clauses/Amendment 2 and adds its prospective dated bounded disclosure alongside
ADR-0059. The second-announcement fatal predicate now requires missing normal
child-join evidence, preserving nonfatal post-join cleanup. Known pre-child refusal
precedence and complete-boundary announcement confirmation are explicit.

Both independently confirm the state-model, conservative/quarantine tradeoff,
Phase 11 owners, dump/collector prerequisites and pending proof limits. Static
checks preserve all 52 Accepted ADRs; the governance review independently checks
1,290 local targets / 394 applicable fragments with no errors. Root's parser and
whitespace checks pass. No reviewer runs shared tests or resource experiments.
The independent bridge fix adds two architecture methods (222 → 224), without any
rule/status/Implemented-count promotion; its execution receipt is separate.
A fresh second revision review follows these corrections; approval remains pending.

**Revision review round 2:** fresh independent GPT-6-astra xhigh lifecycle and
GPT-6.1-sol xhigh governance/proof reviewers approve the corrected eight-document
proposal with zero findings. Both recheck the frozen hashes and all 52 unchanged
Accepted ADR files; the governance reviewer verifies the four exact authority
quotes, unique Registered proof names and unchanged Implemented-rule count.
Root reads both full reports and reruns seven ADR parser controls, the changed-doc
sweep (1,291 local links / 395 fragments, zero errors) and whitespace checks.
No reviewer executes a resource, fatal-child, worker or PostgreSQL experiment.
These are proposal-review receipts, not maintainer acceptance or runtime proof.
The revised ADR-0060 remains Proposed; dependent fatal implementation still waits
for approval, and real lifecycle qualification precedes activation.


##### ADR-0060 acceptance and Step 4 continuation (2026-10-16)

The maintainer approves the revised ADR-0060 at `189f3bcf` and explicitly requests
implementation. Acceptance precedes dependent runtime commits on `development`.
It selects the conservative opaque-construction/unsafe-unwind fatal policy after
comparing viable quarantine, including before-thread false positives and the
whole-API/healthy-pool availability cost. Stalled startup remains retained unhealthy
ownership, not timeout-based fatal selection. No exit releases durable uncertainty
or reservations; earlier-admission effects remain authoritative.

The acceptance carrier commit adds dated bounded supersession amendments to
ADR-0047 and ADR-0059, keeps their bodies/metadata intact, moves ADR-0060 into the
Active index and reconciles current front doors, Standards 20/21, architecture 15
and Phase 11's explicit operations ownership. Three existing canonical proof
assertions are extended without changing Registered status or published counts.
ADR-0058's ordinary maintenance contract remains unchanged.

Step 4 resumes the actual generation owner, shared components, host/exit gates,
closed fatal capability and real pinned worker/PostgreSQL qualification. It still
must complete its implementation commit, two fresh independent full-step review
rounds and verified fix commits before Step 5. Fatal child prerequisites must be
qualified before launch; unsupported dump/collector settings fail, never skip.
No acceptance receipt claims runtime delivery, production readiness or activation.

Focused independent acceptance audit passes after fixing the Active-table row and
derivation-header navigation. All 52 prior Accepted ADRs preserve their bodies;
the two named amendments add only new lines. Seven ADR parser controls, five
corpus architecture cases and a 1,543-local-link / 468-fragment sweep pass, with
zero failures/skips or broken targets. Published catalogue counts/statuses remain
unchanged. The acceptance commit contains documentation only.

##### Owned generation and maintenance foundation (2026-10-16)

**Implementation slice; Step 4 remains in progress.** ADR-0060 acceptance at
`e0d39bf5` precedes this dependent runtime. `HangfireGeneration` retains actual
construction, stop, startup observation and uncanceled Core completion separately
from caller waiting. A synchronous context capture and Core-facing wait-entry
marker distinguish progress from the qualified normal child-join witness.
Successful removal/disposal is also required before cleanup is acknowledged;
joined but unresolved cleanup stays retained. A stop that wins before Start has
its own positive no-Core completion path.

The shared component owner retains the two frozen stock components independently
of pool drain. Native startup, invocation, actual join and cancellation callbacks
have distinct owners. Each lane observes its five-minute wait plus one-second
cleanup bound without releasing resources or starting another pass early.
Expiration keeps its one-hour cadence; fetched-job heartbeat keeps stock pacing.

One closed process gate arbitrates host stop, final teardown and the sole
preallocated pool/stage fatal sink. Its final-exit callback registers before Core.
The gated native connection factory permits normal stock cleanup during host drain
and refuses fatal/final-teardown issuance. Bound activators/wakeups recheck the
same generation at activation, routing and app admission. The framework scope
retains that closed memory predicate and rechecks it after actual row-lock waits,
before spending an attempt. No module-supplied context or transaction owner changes.

Focused source feedback fixes pre-lifecycle expiration drift, stop-before-start,
missing removal/disposal evidence and owner clock/timer failures. Ordinary provider
failures remain contained through the real stop/child-join fence. Two new structural
facts pin the actual fatal capability/sink and public Core constructor, with
direct/delegate/async/overload/interface/forwarding dirty controls. The catalogue
method total becomes 226; all 19 P4 obligations remain Registered and the
Implemented-rule count remains 202. Per-detector body snapshots are rebuilt after
every mutation; no process-wide mutation cache hides added or removed subjects.

Validation currently records a zero-warning/error solution build, 2,083 Unit cases
and 133 focused PostgreSQL/process cases, with zero failures/skips. The latter
include ten real generation controls, app-role admission closure during a real row
lock, real stock parity/bridge/admission regressions, six Linux scenarios and
one in-memory receipt-predicate control.
The full Architecture run and final per-detector optimization rerun each pass
319 cases, with zero failures/skips.

The Linux sidecar runs the real production sink only in an isolated .NET 10.0.10
Arm64 child. A benign twin proves cleanup sentinels; fatal exit 134 omits them.
The shell verifies soft/hard core limits zero and the nonpiped `core` collector
before managed entry. Child environment is cleared, modern/legacy dumps disabled,
root/input binaries read-only, scratch isolated and network disabled. The initial
archive-copy setup refuses the read-only root; a read-only mount of staged proof
binaries fixes setup without changing dump/collector policy. Actual native and
supervisor exits, fixed diagnostics and artifact allowlists pass. These are direct
gate/synthetic-stage controls, not genuine Core resource-failure reproduction or
generation phase-dominance proof.

The production API does not construct or activate these owners yet. Full pool
replacement/retained LearnStack work, independent bridge/control composition,
supported physical-acquisition boundary and remaining lifecycle/fault/exit proofs
remain in Step 4. Its complete implementation commit and two fresh full-step
review rounds still precede Step 5. This slice claims no new business execution,
recurrence, readiness mapping, production recovery, CI success or packet closeout.

##### Foundation review round 1 and verified corrections (2026-10-16)

Two fresh independent reviewers inspect `e0d39bf5..e1e6db42`: GPT-6 Astra with
xhigh effort covers runtime authority/lifecycle; GPT-6.1 Sol with xhigh effort
covers proofs/corpus. Both use standards-check then code-review lenses. These
are focused foundation reviews, not the still-required full Step 4 rounds.

Root verifies one Major finding against the pinned provider source: fetched-job
renewal catches acquisition/SQL failures internally and returns normally. The
shared owner could incorrectly report that returned pass healthy. The correction
binds the closed diagnostics instance, preserves swallowed faults/ownership loss
through empty passes, and requires a clean pass containing actual renewal SQL
before clearing known storage failure. Return/join and storage health are distinct.
No provider message factory is evaluated and stock code/SQL remain unchanged.

The real disposable-PostgreSQL regression fetches a stock wakeup, revokes only its
renewal-column permission and proves unchanged fetched-at, recorded fault and an
unhealthy returned/joined pass. Restoring permission must produce actual fetched-at
progress and healthy recovery with historical faults preserved. Fixture-only
cadence acceleration is explicit; the stock one-second pass pacing is unchanged.
Ten shared-owner unit cases and eleven generation PostgreSQL cases pass with zero
failures/skips. The combined bridge/generation regression passes 33 cases.
The initial new fixture does not compile against the provider's internal Options
property; narrow test-only reflection corrects cadence access. A ten-case run of
the previous integration binary is excluded from the new regression evidence.

The proof reviewer reports no Blocker/Major/Minor. Both verified suggestions are
applied: the constructor-shape assertion includes public and nonpublic members;
the foundation receipt distinguishes six Linux scenarios from its one in-memory
predicate control. Complete Step 4 qualification/activation remains pending.

##### Foundation review round 2 (2026-10-16)

Two new independent reviewers inspect `e0d39bf5..28433f29`: GPT-6 Astra with
xhigh effort covers runtime and GPT-6.1 Sol with xhigh effort covers evidence/corpus.
Both approve the scoped foundation with no Blocker/Major/Minor. They independently
verify the swallowed-renewal mechanism, sticky health, positive storage recovery,
constructor census and six-Linux/one-memory proof boundary. They do not rerun the
toolchain or substitute their source review for the recorded execution receipts.

Root verifies/applies the one nonblocking suggestion: README now distinguishes
in-progress implementation from pending full qualification/activation and points
its current-phase link to Phase 02b. Both focused foundation review rounds are
complete; the remaining Step 4 integration and fresh full-step rounds stay open.

##### Pool replacement and framework cleanup ownership (2026-10-16)

**Further implementation slice; Step 4 remains in progress.** One closed
`HangfirePoolOwner` retains each actual generation and its supervision task.
Replacement requires positive generation cleanup, the fixed five-second backoff
and live owner/process gates. A startup-stalled generation stays closed even
after its late cleanup joins; stop-before-start has a positive no-issued-work path.

Generation-local work owns private cancellation sources and the actual routing
and admission tasks. Local drain closes starts without canceling that work;
host stop requests asynchronous cancellation independently of Core tokens.
Cancellation callbacks and failed cleanup remain owned after wrapper return.
Four retained entries close further starts, with workers-plus-four as the hard
ownership bound. Real stock joins do not substitute for private-work completion.

Routing publishes its opaque claim only after acknowledged COMMIT and actual
resource cleanup. Failed cleanup retains the concrete reader, command,
transaction and connection. Framework admission likewise carries concrete failed
scope/unit/activity/authority/source owners in its result: a completed async task
may clear its state-machine locals. This correction preserves the actual failed
DI scope and unit rather than relying on the completed task alone.

Bridge operations bind ownership before scheduling native startup and recheck the
process/local gates at actual creation and SQL acquisition. Startup return, native
end, observer return and positive cleanup have separate witnesses. Unknown startup
or observer cleanup cannot be pruned as completed; ordinary host stop preserves
durable late-handoff repair without a new local ACK acquisition.

Validation passes a zero-warning/error solution build, 2,109 Unit cases, 74 focused
PostgreSQL cases and the full 319-case Architecture suite, all with zero skips.
The PostgreSQL set includes six real pool-owner cases, seven routing-cleanup cases
and five added generation-work cases. Those cover actual routing/DI cleanup held
past stock joins, real native admission/cancellation, delayed callbacks and exact
failed-scope retention. Four retained entries in the threshold proof are explicitly
synthetic framework occupancy; native refusal/restored admission are real. The
throwing-disposable proof repairs only its synthetic fixture during teardown and
does not introduce a production recovery API.

Initial architecture runs expose the moved routing transaction's stale exact
caller inventories and the wrapper's declared sequential-task ownership exception.
Updating those precise owners preserves the privileged-entry checks and planted
controls; the fresh full rerun passes. A filter selecting no tests is excluded
from evidence. Earlier failed runs are not added to passing counts.

API facade/source-lifetime integration, supported physical capacity and full Core
phase qualification remain pending. The new Core-phase fixture's first actual run
fails during isolated PostgreSQL setup, before arming any fault; its receipt
predicate passes. This is setup feedback, not fatal-policy qualification. Neither
this slice nor its tests activate workers or close full Step 4's review gates.

##### Pool-cleanup slice review round 1 (2026-10-16)

Two fresh independent reviewers inspect `28433f29..d9fda174`: GPT-6 Astra with
xhigh effort covers runtime authority/lifecycle; GPT-6.1 Sol with xhigh effort
covers evidence/corpus. Both approve the exact slice with no confirmed
Blocker/Major/Minor. Their source reviews do not rerun the toolchain or close the
remaining full Step 4 gates.

Both reviewers identify the pre-existing ACK/deferral cleanup-fault qualification
boundary as remaining full Step 4 work: a returned store exception alone does not
prove positive connection cleanup. The ongoing source-lifetime integration owns
that correction and its decisive controls. Their report artifact writes succeed;
final readback is initially blocked while an unrelated local process burst
prevents new shell processes. The subsequent continuation receives both complete
reports and verifies their artifacts. No unavailable check is counted as passing.

The implementation commit's normal hook initially conflicts only on formatting
of two newly added tests while restoring its own WIP stash. Root compares both
versions, verifies equal nonwhitespace content, keeps the formatter output and
verifies all three tracked/five untracked pending proof files byte-for-byte.
The second normal hook succeeds; only the verified recovered hook backup is
removed, preserving the three pre-existing stashes.

##### Pool-cleanup slice review round 2 (2026-10-16)

Two new independent reviewers inspect the same exact committed
`28433f29..d9fda174` range: GPT-6 Astra/xhigh covers runtime and
GPT-6.1 Sol/xhigh covers tests, structural enforcement and corpus. Both approve
with no confirmed Blocker/Major/Minor or required correction. The evidence
reviewer independently reads the supplied build log and TRX records, confirming
the recorded 2,109 Unit, 74 focused PostgreSQL and 319 Architecture cases with
zero skips; this is receipt inspection, not another execution.

Both review rounds are complete for this slice. They do not close full Step 4,
review the subsequent working tree or establish worker activation. The baseline
bridge ACK/deferral cleanup ambiguity remains assigned to source-lifetime
integration rather than relabeled as a new slice regression.

##### Source-lifetime and facade continuation (2026-10-16)

An unrelated local test experiment exhausts the workstation's process limit and
blocks shell/test startup. With explicit maintainer authorization, only that
experiment's matching process path is terminated. The next process inventory
contains 659 processes and no matching experiment process; commands resume.
This environment repair is not product or CI evidence.

The working implementation adds concrete connection-cleanup seals to the three
privileged dispatch/bridge stores, a one-shot shared-source lifetime pin,
positive messaging startup/actual-loop witnesses and the API runtime facade.
The corrected source builds with zero warnings/errors; its then-current Unit
assembly passes 2,161 cases with zero skips. Synthetic driver-state and cleanup
controls prove retained ownership, not production failure incidence.

The isolated Core-phase fixture progresses through PostgreSQL preparation and
container qualification but still refuses its sanitized-output prerequisite.
Those failed runs arm no accepted fatal-policy proof and are not counted as
passing qualification. Full Core phase/mutation/native-exit evidence, composed
source lifetime, supported capacity and full Step 4 reviews remain in progress.

##### Core-phase and direct-facade qualification (2026-10-16)

The corrected isolated fixture passes 23 cases: seventeen unchanged-vendor Core
phase/native-exit scenarios, five actual child-only IL mutations and one separately
labelled receipt-predicate control. The mutations remove the production sink or
one exact constructor/capture-disposal/second-announcement/outer-completion
observer. The same normal predicate rejects each modified child's actual result;
a later fallback fatal call cannot conceal an observer that already returned.
These are synthetic fault placements in real pinned Core, not genuine native
thread/resource-exhaustion reproductions.

The fixture retains strict empty-stderr, native/supervisor exit, artifact, dump,
runtime and original-vendor-binary checks. Its earlier output refusal is optional
GSS library discovery in the minimal Linux image; the password-only fixture now
explicitly disables GSS encryption. Production connection settings are unchanged.
PostgreSQL preparation has one ephemeral loopback binding, preserved and checked
after bridge detachment; the fatal child has only the inspected internal network.
No claim that the PostgreSQL setup port is unpublished is made.

Six real-PostgreSQL facade cases pass. Actual default and maximum manifests use
the same guarded pooled settings and live stock worker/queue metadata: 45 and
151 connections respectively. A native framework invocation's held DI cleanup
keeps source disposal pending beyond the caller's stop deadline; releasing actual
cleanup permits Core removal, positive runtime completion and source closure.
Another case holds stock expiration's actual checkout with a competing database
lock while independent transactional admission succeeds at the supported default
floor. These cases do not assert simultaneous saturation of all reserved lanes.
Their two completed messaging tasks are explicit no-issued-work facts; the real
three-owner API composition requires its separate qualification.

The current Release solution build passes with zero warnings/errors and all
2,165 Unit cases pass, zero failures/skips. Full Architecture validation and the
composed messaging-source proof remain in progress. These receipts overlap earlier
focused runs and are not summed into a packet total. Full Step 4 implementation,
fresh full-step review rounds and later Steps 5–9 remain open.

Two additional composed PostgreSQL cases now pass with the actual API-hosted
jobs, outbox and delivery singletons and their exact bound completion tasks.
Each real terminal transaction and one matching MUST SecurityEvent audit commit
before a test-owned async disposable holds that terminal audit scope's cleanup.
The actual connection remains checked out and actual worker completion remains
pending through finite stop waiting and container disposal. Guarded acquisition
still succeeds; releasing the synthetic cleanup hold lets all three actual owners
join and the same source observer close the pool. Independent app-role reads
confirm unchanged terminal/audit durability, then no vendor server or tagged
backend remains. The synthetic disposable is not a driver-cleanup-fault claim.

The first composed fixture run fails genuine DI validation because its custom host
omits tenancy edge registration. The corrected fixture uses the real edge and
literal development configuration; validation stays enabled. A discarded network
hold design is replaced before acceptance because bounded driver cancellation
would make its pending checkpoints depend on a narrow scheduling window.

The full Architecture run exposes a detached Cecil target in a planted
missing-subject control; local guards now record the missing declaration/scope
instead of throwing. The already-failed unoptimized rerun is canceled and excluded
from qualification. A per-detector scan reuses type/call-site indexes only within
one synchronous evaluation; mutation and undo occur between disposed scans.
The existing companion verifies fresh async-subject addition/removal and cache
release. Its performance and the corrected full suite still await execution.

##### Complete Step 4 implementation validation (2026-10-16)

The current implementation passes a fresh CI-mode Release solution build with
zero warnings/errors and all **4,403 backend cases**: 2,165 Unit, 321 Architecture,
4 Contract and 1,913 Integration (176 HTTP and 1,737 Docker/PostgreSQL cases).
The runner guard confirms every assembly executes and no case is skipped. The
Docker run takes 9 minutes 55 seconds; Architecture takes 13 minutes 23 seconds.
The latter confirms the corrected detectors and mutation-scope cache pass, not
a measured speedup against the canceled failed run.

The complete Integration run includes the Core-phase/mutation, direct-facade and
three-owner lifetime cases described above. Their focused receipts overlap this
run and are not added to the total. Seven ADR parser controls, 1,603 local links,
466 fragments, added prose wrapping and whitespace checks pass. No frontend/demo
rerun or current-head CI is claimed; full packet regression remains Step 9.

This implementation is ready for the two fresh full Step 4 review rounds over
the changes after Step 3 closeout `def48a1a`. The earlier slice reviews remain
scoped receipts. Step 4 is not closed until both full rounds and their verified
corrections complete; Steps 5–9 remain pending.

##### Full Step 4 review round 1 (2026-10-16)

Three fresh reviewers inspect `def48a1a..a1832c6e`: GPT-6 Astra/xhigh covers
runtime/lifecycle; GPT-6.1 Sol/xhigh covers evidence/censuses; a second
GPT-6.1 Sol/xhigh covers security/composition/corpus. They inspect the passing
receipts without rerunning the toolchain. Four unique Major findings are verified
against current source; the security review independently confirms the same
metadata-heartbeat finding rather than adding another issue.

- A held routing/framework cleanup has no finite wrapper-wait transition to
  retained/unhealthy ownership. The correction reuses the existing short-work
  wait bound and preserves actual work/callback/source ownership after return.
- The facade drops the running generation's metadata-heartbeat fault during its
  retry window. Aggregate health must preserve that fault before drain and reset
  only after actual stored recovery.
- Shared expiration/fetched-job component state/outcome and ownership facts do
  not reach the registered health result. Fixed memory-only dimensions restore
  those distinct observations without mapping readiness.
- The production final-exit child holds actual Core control work but no actual
  private cancellation callback. The isolated prototype remains decision support;
  a combined shipped-relay positive and direct-Core-callback dirty control are
  required in the qualified production child.

Corrections and their regression qualification are in progress. The optional
scan-cost suggestion scopes repeated type/call/body inventories to one detector
evaluation, preserving fresh mutations/restoration and measuring the full suite
before any performance claim. No Accepted decision changes. Step 9 retains the
existing Standards 10/11 carrier sweep for legacy vendor-payload wording and the
retired `Hangfire_Job_Payloads_Include_TenantId` spelling. No later step or full
Step 4 review gate is closed by this record.

The first correction run passes 33 of 35 focused PostgreSQL/Core-phase cases,
including both real native framework expiry cases and all 24 Core-phase controls.
Its two health failures are investigated rather than relabeled as qualification.
One fixture fetches through a second provider whose renewal inventory is separate;
the corrected control uses the facade's actual provider and a fixed fixture-only
orphan queue with no native consumer. The metadata control also exposes PostgreSQL
`query_canceled` (57014), which the pinned driver's generic transient list omits.
The exact code now follows the already accepted timeout-retry policy; no other
classification or retry bound changes. Both corrected health cases pass in a
fresh 14-second run. Complete correction-head regression remains pending.

##### Full Step 4 round-1 correction validation (2026-10-16)

Root verifies all four findings against the current implementation and completes
their corrections. The finite framework wait owns its actual operation before
scheduling; expiry closes later admission and records retained ownership before
returning the native slot. Real native controls cover held asynchronous cleanup
and held synchronous framework execution, independent healthy admission below the
retained threshold, and positive source/generation cleanup after release.

Real PostgreSQL controls verify metadata-heartbeat timeout/recovery through the
registered health result, distinct failed fetched-job renewal/recovery, and held
expiration ownership. The production exit child now retains its actual private
callback through finite host waiting and native zero exit. Its separate unsafe
direct-Core-callback control requires supervised timeout, owned kill and actual
join; those facts are explicitly refused by the normal-exit predicate. These are
synthetic callback/fault controls over the shipped owners, not native resource
failure incidence or a claim of cleanup at process exit.

The corrected Release solution build passes with CI warnings-as-errors and zero
warnings/errors. Fresh complete suites pass 2,166 unit, 321 architecture, four
contract, 176 Docker-free integration and 1,741 Docker/PostgreSQL integration
cases: 4,408 backend cases, zero failures/skips. The Docker suite takes 10m10s;
the architecture suite takes 12m26s. The latter is a measured run duration, not a
controlled speedup claim. Scoped inventory caching preserves mutation/undo and
exception freshness controls without adding a global cache or a new rule.

All 22 changed C# files are formatted. Seven ADR parser controls, 1,917 local
links, 557 fragments, changed prose wrapping and whitespace checks pass. Earlier
failed focused runs are excluded from the passing complete-suite total; focused
controls overlap it and are not added again. No frontend/demo/current-head CI is
claimed here; Step 9 owns packet-wide reruns. Round 1's verified corrections are
qualified; a fresh full Step 4 round 2 follows their commit. Step 4 remains open
until that round and any verified corrections complete. Steps 5–9 remain pending.

##### Full Step 4 review round 2 (2026-10-16)

Round-1 corrections are committed as `53ec8a4a`; normal Leakwatch/format hooks pass
without changing the 22 validated C# source hashes. Three new independent
reviewers inspect the complete `def48a1a..53ec8a4a` range (123 files): GPT-6
Astra/xhigh for runtime/lifecycle and two GPT-6.1 Sol/xhigh reviewers for
evidence/censuses and security/composition/corpus. They inspect the 4,408-case
passing receipts without rerunning them and affirm all four first-round fixes.

One unique Major qualification gap remains: ADR-0059 explicitly requires sustained
metadata-heartbeat cutoff/reset and common database outage/recovery with an actual
held old operation, a spare slot and fewer than four retained entries. Current
controls prove real transient SQL recovery and separately requested drains, but
do not compose that mandatory matrix. The runtime reviewer independently confirms
the same gap; neither review alleges a demonstrated production-code failure.
Root verifies the accepted obligation and missing coverage. Isolated PostgreSQL
outage and owner-clock controls are being added before Step 4 closes; their native
Core/SQL observations and logical clock advances remain separately labeled.

The security/corpus review also verifies one Minor status mismatch: Standards 05
and Events/Outbox architecture still describe both full reviews as pending. Their
live summaries now delegate review dispositions to this owning record, preserving
earlier dated receipts. No new ADR, runtime policy, grant or business definition
is introduced by these corrections. New qualification and correction review remain
pending; Steps 5–9 do not start until Step 4's verified findings are closed.

##### Full Step 4 round-2 correction validation and closeout (2026-10-16)

The two added controls exercise the shipped lifecycle with actual PostgreSQL.
One holds the real server row, verifies transient failure without stored progress,
then observes a stored success resetting the owner cutoff. At the renewed exact
boundary, the owner refuses before provider acquisition or SQL. The other pauses
only a test-owned PostgreSQL container and lets all three actual native metadata
heartbeats fault. Owner-clock advances then drive past-cutoff drain. After real
storage recovery, the two clean pools replace their generations and serve work;
the affected pool retains its old actual scope cleanup despite a spare native
slot and one retained entry. Only positive cleanup permits its one replacement
and the pending admission. No shared fixture container is paused.

These controls keep native Core heartbeat cadence and SQL/socket bounds real;
the four-minute policy interval uses the owner's logical clock. The held scope
is a synthetic barrier around actual native framework work. Neither test claims
four minutes of wall-clock outage, genuine resource-exhaustion incidence,
external-effect quiescence or production supervision.

The final corrected-source focused run passes both controls. Three separately
compiled, temporary mutations of actual production source each fail at the
intended behavior: removed cutoff permits forbidden issuance; removed successful
reset refuses the next valid heartbeat; replacing actual cleanup with the stock
child join starts generation two while generation one still owns held cleanup.
Each mutant builds successfully and executes its one expected failing case with
zero skips. Original production bytes are restored and the CI-mode Release
solution rebuild passes with zero warnings/errors. These expected red controls
are not additional passing cases.

A fresh independent runtime reviewer inspects the complete correction and all
final receipts, verifies source hashes and restored production bytes, and finds
no remaining issue. This supplements the full second round rather than replacing
it. The status corrections in Database Standards and Events/Outbox architecture
now link their current qualification to this owning record. No Accepted policy,
grant, dependency, migration or production business definition changes.

The final complete Docker/PostgreSQL suite passes 1,743 cases, zero failures/skips,
in 12m33s. The unchanged non-Docker partitions retain the round-1 receipt: 2,166
unit, 321 architecture, four contract and 176 HTTP integration cases. Together
these cover 4,410 distinct backend cases; this is a combined receipt, not a fresh
run of all five partitions at this correction. The two focused passes overlap
that complete Docker suite and are not added again. Six current corpus/trait
checks and seven ADR parser controls pass. The changed-document audit resolves
1,919 local links and 559 fragments with zero errors; added prose wrapping and
whitespace checks pass. None of these overlapping checks adds to the case total.

Both full Step 4 reviews and all verified findings are closed. Step 4 is locally
complete; Step 5 proceeds automatically on development with the existing approved
transactional-owner, retry/recovery and terminal-audit scope. Steps 6–9 retain
capacity, external effects, recurrence and packet-wide enforcement/CI. No frontend,
demo, current-head CI or completed production business definition is claimed by
this closeout.

### P02b-4 Step 5 — transactional owner and terminal audit (2026-10-16)

**Locally complete after both fresh full review rounds, verified corrections
and independent focused fix verification.**
The existing approved package
owns this step; no new decision or approval is pending. Source implements the
closed ordinary root participant, acknowledged start publication, immutable job
attribution, final row/time fence and sticky cancellation-versus-COMMIT gate.
Business state, child jobs, outbox/domain completion and MUST audit share the
existing transaction owner. Positive COMMIT acknowledgement remains distinct
from subsequent scope/source cleanup; unknown COMMIT needs a real locked probe.

Independent finite renewal, retry/recovery and terminal lanes retain actual SQL,
probe and cleanup owners. Six acknowledged starts preserve the five accepted
backoffs; unknown/Pending definitions defer, explicit retirement terminalizes.
The sixth audit seam binds only actual returned job-transition values and the
same live transaction. Terminal audit failure does not advance business work.
Source and application-pool ownership are qualified against the formulas in
[Database Standards](../standards/05-database.md#shared-dispatcher-source-lifetime-and-capacity),
including timed-out auxiliary loaders and separately declared shared headroom.

CI-mode Release builds pass with zero warnings/errors. The complete unit run
passes 2,212 cases with zero skips. Initial PostgreSQL qualification exposed
reserved SQL naming, transition cleanup and two real concurrency defects:

- A joined admission probe could refresh the locked job header while retaining
  an older attempt snapshot. It now reads the immutable attempt in a fresh
  statement after locking the header, on the same connection/transaction. The
  restored original JOIN fails the committed-admission regression while its
  rollback control passes; restoring the correction makes both cases pass.
- Deferred count validation upgraded the parent lock to `FOR UPDATE`, blocking
  renewal COMMIT behind an unrelated child's key-share lock. A forward migration
  uses `FOR NO KEY UPDATE`, preserving count-writer serialization and applied
  history. Real child-lock renewal and exact one-function Down/Up controls pass.

Focused real PostgreSQL controls pass for business/outbox/child-job/MUST-audit
atomicity, stale authority, lock-wait expiry, uncertain COMMIT, terminal witness
substitution, and native terminal-audit backlog progress beyond two leases.
Actual application and privileged sources remain retained through ignored
cancellation, held callback and messaging cleanup controls. These overlapping
focused receipts are not a second complete backend run.

The first full architecture run records 322 passing and six failing cases out
of 328, with zero skips. Failures identify the published method count, the same
JobExecutor's additional partial path, exact physical transaction-owner lists
and retained-factory scanning. Corrections preserve finite ownership and planted
controls; four focused count/writer/parallelism reruns pass. The new orchestration
detector indexes each evaluation's actual field instructions once, retaining every
mutation/restoration check. The corrected complete architecture run passes all
328 cases in 17 minutes 31 seconds. A fresh complete run also passes 2,212 unit,
four contract and 176 Docker-free HTTP cases, all with zero skips.

The complete Docker run records 1,771 passing and 39 failing cases out of 1,810
in 13 minutes 1 second, with zero skips. All failures belong to two old fixtures:
DeliveryExecution's observed-unit factory still resolved the removed raw source
alias, and EducationPersistence passed the driver's password-free connection
string view to its API composition. The former now passes the existing lazy
source explicitly; the latter supplies original fixture configuration while
preserving the Seeder's caller-owned source. No raw alias or second DI disposer
is restored. After a clean Release build, both complete classes pass all 43 cases
in 19 seconds, with zero skips. Their four overlapping earlier passes are not
added twice. Together these runs establish 1,810 unique passing Docker cases and
4,530 unique passing backend cases; this is not a second complete-suite rerun.

Formatting, seven ADR parser controls, 1,755 local links and 500 fragments pass.
Both fresh full review rounds follow the validated implementation commit; no
completed review or Step 5 closeout is claimed. Steps 6–9 remain pending.

#### Step 5 review round 1

Three fresh independent reviewers inspected `4826b7df..9b634405`: owner,
authority and lifetime (GPT-6-astra xhigh), control/SQL/terminal audit
(GPT-6.1-sol xhigh), and proof/composition/corpus (GPT-6.1-sol xhigh). The last
lens reviewed all 109 changed files and independently recalculated the composite
TRX totals and original-JOIN mutation/restoration evidence. Two confirmed groups
required correction; no additional Blocker or verified finding remained.

- A cache caller could return while the original auxiliary task still cleaned
  its connection. The executor froze this pending state as a permanent cleanup
  failure, preventing eventual positive generation release. It now joins that
  exact task without the expired caller token before deciding cleanup. Business
  issuance closes first; framework's prior binding closes while its successful
  admission keeps the same owner open. Genuine disposal failure stays retained.
  Exact caller/signature controls reject extra, async, detached, missing and
  token-taking joins. Four unit cases and native clean/dirty controls exercise
  original loader cleanup, private callbacks, retained identity, healthy later
  work and both requested source disposals.
- Business COMMIT-acknowledgement loss and final lock-wait expiry previously had
  real PostgreSQL direct-executor controls only. New pinned native-worker cases
  stage organization, child-job, outbox and MUST-audit effects. Lost ACK retains
  the actual identity while a fresh database probe is causally locked, then
  resolves without a duplicate effect or vendor retry. Expired/live final-lock
  controls prove atomic rollback/commit and the actual retry disposition.

CI-mode Release builds pass with zero warnings/errors. The complete unit run
passes 2,216 cases; the complete architecture run passes all 328 in 18 minutes
3 seconds; 36 focused PostgreSQL cases pass, including the five new native rows.
All report zero skips. The initial focused run passed 34 of 36; both failures
were the new auxiliary fixture attempting a second rollback after its lock had
already been released. Explicit lock ownership fixes teardown, and the same
36-case rerun passes without weakening a production assertion.

The actual-source control restores the old pending-cleanup snapshot temporarily.
It builds, and both native auxiliary variants fail specifically because the
business task completed before its original cleanup. Restoring the exact source
bytes, rebuilding and rerunning makes both pass. Raw exception logging observed
in two existing helpers was not established as a new Step 5 disclosure or violated decision; it was not promoted to a finding
or bundled into this correction.

The focused run overlaps 31 prior Docker cases and adds five. Combining current
complete unit/architecture, prior contract/HTTP, and the existing corrected Docker
receipts gives 1,815 distinct passing Docker and 4,539 distinct passing backend
cases, using `(testId, testName)` identity. This is explicitly composite evidence,
not another complete-suite rerun or current-head CI claim. Formatting, the
nonempty/zero-skip runner, seven ADR parser controls and local links/fragments pass.

The second full fresh review round follows the correction commit. Step 5 and
Steps 6–9 are not declared complete by this first-round receipt.

#### Step 5 review round 2

Three new independent reviewers inspected the complete `4826b7df..9dbea7bf`
range: owner/authority/lifetime (GPT-6-astra xhigh), control/SQL/terminal audit
(GPT-6.1-sol xhigh), and proof/composition/corpus (GPT-6.1-sol xhigh). Three
verified groups required correction; timer and control findings were independently
corroborated. No additional Blocker or verified finding remained.

- Reconciliation silently stopped at a pending/faulted job auxiliary, then cleared
  the rolled-back MUST intents. Removing that precheck lets the actual standalone
  writer refuse another acquisition inside its existing reporting boundary.
  Every intent now receives the required bounded Critical, operation-only failure
  counter and unhealthy audit signal; the original business refusal stands.
  Native clean/dirty controls assert the absent rolled-back audit row, actual DI
  logger/meter/health signals and unchanged held resources. A two-intent unit
  companion proves independent reporting without resolving another source.
- Timer disposal preceded renewal closure. A late acknowledged renewal could
  rearm a disposed timer and cancel unrelated batch members through the shared
  error path. Renewal now closes permanently under the publication lock before
  disposal. A deterministic real-timer disposal-gap control keeps a second
  execution live and forbids early identity release.
- A positively cleaned pre-COMMIT control fault discarded its original permanent
  business decision. The request now retains that exact decision and retries on
  the bounded existing cadence; uncertain COMMIT and failed cleanup keep their
  separate paths. A native worker returns one permanent refusal, hits a real SQL
  lock timeout, retains its exact identity/request, then terminalizes with one
  MUST audit and no second business start, including after lease expiry and a
  duplicate native wakeup.

CI-mode Release builds pass with zero warnings/errors. The complete unit run
passes 2,218 cases; complete architecture passes 328 in 17 minutes 23 seconds;
37 focused PostgreSQL cases pass. The control-failure proof now captures progress
while its actual SQL is blocked, rechecks that wait, then observes a completed
failed iteration before separately asserting the retained request/identity. After
this test-only strengthening, a fresh clean build and the same 37-case run pass.
All passing runs report zero skips.

Three actual-source mutations compile and fail their intended assertions:
silent audit refusal fails one unit and both native health controls; late renewal
closure fails at the disposed real timer; discarded control decisions fail the
retained-request assertion with zero pending outcomes. Each exact source is
restored in a finally, rebuilt and its same regression cases pass. No mutation
remains in the tree. Formatting, seven ADR parser controls, local links/fragments
and the nonempty/zero-skip runner pass.


The focused run adds one Docker case to prior corrected receipts. Current
complete unit/architecture, prior contract/HTTP and corrected Docker receipts
establish 1,816 distinct passing Docker and 4,542 distinct passing backend cases
using `(testId, testName)` identity. These are composite receipts, not another
complete-suite rerun or current-head CI. All nineteen job obligations remain
Registered; Step 9 owns final enforcement and packet regression.

The corrections are committed at `6a2ded99`. Normal hooks pass eight-file
Leakwatch and seven-file C# formatting, with no C# byte changes after validation.
Two new independent reviewers verify that correction and its immediate
lifetime/authority and proof/corpus dependencies: GPT-6-astra xhigh and
GPT-6.1-sol xhigh. Both approve without verified findings. The proof reviewer
independently recalculates the composite counts, checks each intended mutant
failure, and compares exact restored source bytes. They inspect recorded local
receipts, without rerunning the toolchain or claiming current-head CI. This is
focused correction verification, not a third full review round.

Step 5 is locally complete. Step 6 proceeds under the existing approved package;
Steps 7–9 and packet-wide regression/PR/CI remain pending.

### P02b-4 Step 6 — tenant capacity reservations (2026-10-16)

**Locally complete after both full review rounds and verified corrections.**
Step 5 closes at correction `6a2ded99` after both full rounds and independent fix
verification. Accepted ADR-0047 and the approved nine-step package own this step;
no new capacity-policy decision or approval is pending.

- App-role admission locks the tenant/group mutex, resolves an optional registered
  Hard LimitKey and counts every unreleased reservation across organizations and
  instances. Deferral spends no normal attempt. Existing entitlement provider/key
  contracts and empty production definitions remain unchanged.
- Successful business completion releases its exact reservation on the existing
  business transaction. Failed or skipped work needs a distinct local actual
  cleanup witness and acknowledged nonexecuting recovery authority before a
  sequential app-role checkpoint can release occupancy. No executable receipt is
  reconstructed after lost admission acknowledgement.
- A forward storage change records truthful transactional cleanup separately from
  never-started or external-quiescence evidence. The original source owner stays
  open across sequential phases while every old binding closes permanently.
  Missing cross-instance cleanup evidence retains occupancy in recoverable parked
  state, permitting a live origin's eventual positive cleanup; TTL never releases.
- Control handles each checkpoint as a bounded owned operation, preserving fair
  service and actual stop/cleanup ownership. The existing transaction owner,
  tenant-setter inventory and bypass reservation-write prohibition remain intact.

Qualification covers two-instance/two-org caps, no-attempt refusal, unlimited/
zero/downgrade/fault limits, atomic success/cleanup release, admission and checkpoint
COMMIT uncertainty, held callbacks/resources, missing-witness parking, exact grants
and source bounds. Each implementation/correction commit and both fresh review
rounds are recorded here after they occur. Live external execution remains Step 7;
final observation/enforcement and full packet regression remain Step 9.

#### Step 6 implementation and qualification (2026-10-17)

The app-role start owner locks the job, then the tenant/group mutex. A separate
post-wait statement counts every unreleased reservation across organizations and
instances. The registered Hard projection intersects the operational ceiling;
missing/unlimited, zero, positive, oversized and invalid/faulted answers retain
the accepted bounded semantics. Capacity refusal spends no attempt. No production
job definition, entitlement key or provider is introduced.

Business success releases its exact reservation on the existing business/MUST/
outbox/child-job completion transaction. Other grouped transactional outcomes
must join the actual admission/business, auxiliary resources, observers and
original private cancellation callbacks before minting a local cleanup witness.
Genuine no-start requires an atomic unused-entry seal, including receipt-less
admission uncertainty. This witness grants neither execution nor database truth.

The confined recovery owner publishes a separate nonexecuting capability only
from acknowledged acquisition or an exact locked proof. The same original
application-source owner then enters one fresh sequential checkpoint. Its old
bindings remain permanently closed. Evidence and release commit together;
unknown checkpoint COMMIT probes read the descriptor's historical attempt after
the header lock, separately from current routing authority. Missing local cleanup
parks occupied work with a paced probe; neither TTL nor a replacement owner
manufactures cleanup. Stop retains and joins every issued checkpoint/callback.
Host stop admits no fresh cleanup checkpoint; a clean local stop cannot substitute
for cleanup evidence that never committed, so restart still retains that occupancy.

Forward migration `20261016210528_add_transactional_job_capacity_cleanup` adds
write-once transactional cleanup evidence, its exact release reason and three
existing guard replacements. The predecessor's non-key count lock remains.
Only the app UPDATE and dispatcher status-SELECT column budgets expand; bypass
reservation writes remain refused. Down refuses recorded new history rather
than erasing or relabeling it. Canonical fresh DDL comparison remains exact;
Down/reapply accounts only for PostgreSQL's advanced physical position of that
single dropped/readded column, preserving every other inventory fact.

Exact owner, framework-request, minting, command, task/callback, source and final
SQL-fence censuses gain closed enrolled paths and planted dirty/clean controls.
The existing transaction owner and ADR-0040's eight logical GUC setters remain;
the new executor partial belongs to the same ADR-0036 context writer. Its exact
accessor-writing file is enrolled explicitly; no new logical writer is added.
Sequential cleanup adds no simultaneous main/auxiliary checkout to the current
acquisition inventory.

Initial qualification exposed two missing SQL separators in the new checkpoint
builder; actual native tests now fail directly through a sanitized observer fault
signal instead of hiding the handler failure behind a timeout. Both separators
are fixed and the same four real native cleanup/acknowledgement-loss cases pass.
It also corrected the no-plan fixture's empty-provider assertion and the precise
physical-column comparison above. Initial failing receipts are retained.

#### Step 6 validation receipts (2026-10-17)

Local CI Release build completes with zero warnings/errors. The full Unit suite
passes 2,229 cases, Contract passes four, and integration without Docker passes
176, with zero failures/skips. These are separate local suites, not current-head
PR CI or full packet closeout evidence.

Focused PostgreSQL qualification passes all 192 cases after the SQL separator and
fixture corrections above. Eight additional native facade cases pass together,
including admission and capacity-checkpoint acknowledgement loss, private callback
retention and receipt-less genuine no-start. A first tight-budget no-start run
reached a disposal-barrier timeout once; that cause was not reproduced. The same
two cases and two repeated mixed eight-case runs then passed. The new no-start
fixture now uses the accepted five-second SQL budget and a ten-second transaction
budget, retaining every barrier and bounded failure diagnostic. This removes an
unnecessary cold-source/role-initializer/read constraint; it does not establish the
cause of that initial timeout.

Three actual production-source mutations compile and fail their intended
behavioral controls: counting before the real tenant/group mutex wait (two cases),
leaving sealed unused execution permission open (one), and omitting the actual
private cancellation-callback join (three). The initial callback observation let
one mutant pass; the held-callback observation was strengthened, and all three now
fail against that mutant. Every source file is restored byte for byte, freshly
rebuilt, and the same respective controls pass. No mutation remains in production.

The first full architecture run reports 330 cases, 327 passed and three failed.
Two failures identify stale exact admission/privileged-owner caller inventories;
the other exposes a detached-Cecil-method null dereference after the detector
already records the forbidden target. The corrected guard enumerates the actual
new owners and reuses the existing exact local-revalidation proof. The capacity
detector returns its known detached-target offenders before unsafe identity
inspection. No target filter, namespace exemption or weakened production boundary
is added. Both corrected clean guards and their complete planted-control tests
pass together: four cases, zero failures/skips.

The corrected full architecture baseline passes all 330 cases in 1,695.7 seconds.
The first full Docker run reports 1,848 cases: 1,847 passed and one native framework
lifetime row timed out before reaching its real cleanup barrier. Both original
rows subsequently pass with bounded diagnostic observations; that does not
establish the cause of the earlier timeout. The corrected lifetime fixture keeps
actual pinned native workers, physical SQL deadlines, held cleanup and source/
slot/stop assertions. It parks the three polling loops, issues exact native wakeups,
and advances only the owner observation clock after the actual barrier. This
intentionally removes incidental bridge ACK/cold-start timing from that lifetime
proof; separate real bridge/repair controls remain and pass with the common
PostgreSQL-outage proof: 21 cases. Both corrected lifetime rows also pass.

Two invocation-local/name-first detector filters preserve complete subjects,
identity/signature/opcode checks, caller multiplicity and every clean/dirty/restored
mutation. The same four complete guards pass in 311.2 seconds versus 515.3 before
these filters. Capacity dirty control falls from 251.6 to 113.2 seconds; authority
from 261.9 to 196.3. A source/equivalence preflight finds no actionable issue; it
supplies no full Step 6 review credit.

The final full architecture run passes **330/330** in 1331.9 seconds, and the
fresh full Docker integration run passes **1,848/1,848** in 889.3 seconds,
with zero failures/skips and identical case-name multisets to their baselines.
Together with Unit, Contract and Docker-free integration, these distinct local
suites pass **4,587** cases. Focused/negative/restoration receipts are overlapping
proof and are not added to that total. The local timing comparisons do not prove
the complete real CI backend job fits its 20-minute ceiling; Step 9 retains full
packet regression and real PR CI qualification.

At this qualification head, both full fresh review rounds and their verified
corrections remained pending. All nineteen job obligations remain Registered;
Step 9 owns complete packet enforcement/regression. External execution and
recurrence remain Steps 7–8.
ADR-0061 is separately Accepted in `82956666`; its dependent Step 7 implementation
starts only after Step 6 closes its two full review rounds.

#### Step 6 full review round 1 (2026-10-17)

Implementation commit `caf51297` passed normal hooks with a clean tree.
Three fresh independent reviewers inspect its complete 64-file scope and connected
paths: two GPT-6-astra reviewers at xhigh for ownership/lifecycle and storage/
isolation, and GPT-6-sol at xhigh for tests/corpus/performance. They inspect source
and actual receipts; they do not rerun the toolchain or claim CI qualification.

Both runtime reviewers independently confirm one Major local lifetime race. After
an exact historical cleanup probe releases its row lock, another instance can route
the released job to a new generation before local recovery reacquisition. A clean
null acquisition then repeats the permanently obsolete generation, retaining the
original invocation despite settled history. The corrected null branch reinstalls
its exact historical cleanup descriptor when a checkpoint is already committed.
Settlement still needs the locked original evidence plus changed routing; a live
peer lease remains pending. The production correction is five lines and changes
neither schema nor grants. A separate permanent-decision durability concern is not
confirmed as a new regression against existing crash/recovery handling of decisions
not yet recorded.

Three native takeover cases cover the probe-unlock interleaving, live-other-owner
retention and a lost recovery-acquisition COMMIT acknowledgement. The test proxy
holds the actual historical ROLLBACK response only after PostgreSQL unlocks; an
independent store performs real fenced routing. The live-lease control uses an
explicit privileged fixture lease and waits for another actual locked probe before
asserting retention. All preserve one original attempt/checkpoint and immutable
release history, then join the original source and native slot without host stop.
Before the final fixture strengthening, the first two cases failed the original
implementation's final-join assertion after their positive database premises.
Removing the re-probe correction from the final source compiles and fails all
three current takeover controls at their missing-probe or final-join assertion.
Exact source restoration and a fresh build pass the same three cases. This includes
the recovery-acquisition ACK-loss entrance and preserves live-peer retention.

The proof review also identifies two missing native stop windows. New controls
stop after a real recovery ACK before checkpoint issuance, and after issuance
while its actual private cancellation callback is held. The first observes the
actual control loop past the issuance window while a separate stop callback keeps
final request release held. The second observes the real app task's known rollback
separately from the checkpoint's still-pending callback join. Neither source nor
invocation is falsely released. Removing both issuance guards or the checkpoint's
actual private callback join compiles and fails the respective intended assertion.
Both production files are restored byte for byte, rebuilt, and pass those controls.

Initial takeover-harness runs do not supply runtime proof: a thirty-second proxy
lifetime obscured the primary failure, and a diagnostic run exposed a row-description
flag reset at each ReadyForQuery before the intended ROLLBACK hold. The corrected
proxy has a separate finite test lifetime, transaction-scoped observation and primary
failure preservation. A read-only fixture preflight strengthens the actual second
probe, exact peer lease fence and host-stop cancellation premises; it supplies no
full second-round credit.

The new release reason joins the existing real app-role forged-release refusal /
removed-trigger dirty control. Standards 05's stale pending-qualification wording
is corrected. Two further invocation-local/name-first Cecil filters preserve every
production subject, exact signature/opcode, caller multiplicity and planted control;
no global cache or owner exemption is added.

Fresh format and CI Release build pass with zero compiler warnings/errors. The
focused native/release set passes nine cases. Full Unit passes 2,229, Contract four,
Docker-free integration 176, Architecture **330/330**, and Docker integration
**1,854/1,854**, all with zero failures/skips: **4,593 distinct full-suite cases**.
The architecture case multiset is unchanged; all 1,848 previous Docker cases remain
and six new cases account for the difference. Focused and mutation receipts overlap
these suites and are not added to that total. Full architecture takes
1318.4 seconds versus 1331.9 at implementation qualification;
full Docker takes 933.2 seconds. These are local measurements,
not a real CI budget pass. Current-head CI remains unrun.

The first-round corrections are locally qualified. The second full fresh review
starts only after their normal local commit. All nineteen job obligations remain
Registered; the published architecture-method count remains 237. Step 6's second
full review gate is pending. Steps 7–9 and full packet/PR/CI closeout remain pending.

#### Step 6 full review round 2 and local closeout (2026-10-17)

First-round correction `d387f71f` passed normal hooks with a clean development
branch. Three new independent reviewers inspect the complete **68-file** Step 6
scope: GPT-6-astra at xhigh independently covers ownership/lifecycle and storage/
isolation, and GPT-6-sol at xhigh covers proof/corpus/performance. They inspect
source and parse actual receipts, without claiming personally executed tests or CI.

Both runtime reviewers independently identify one further Major admission-lifetime
race. A physical admission COMMIT can fail before reaching PostgreSQL, leaving no
original attempt or reservation while its local outcome correctly remains uncertain.
If a peer clears the expired routing token before original scope cleanup finishes,
the old probe sees changed authority. Discarding that admission descriptor then
retries recovery acquisition for history that cannot exist, retaining clean work.

The correction makes a fresh exact admission-token history read after acquiring
the actual job-row lock. Locked absence proves rollback independently of current
routing. Existing original history remains distinct from a peer's later attempt;
positive original release requires its exact reservation and corresponding immutable
evidence. Changed authority alone never settles the request, and its unresolved
historical descriptor remains retained. The read probe changes no peer state and
constructs neither executable nor recovery authority. No schema, grant or source
capacity changes are needed.

The new native control drops the actual second app COMMIT request before the server
receives it, holds real original scope disposal, and uses an independent real
recovery store to clear expired routing. It verifies absent original attempts/
reservations, uncertain admission with no executable receipt, no business constructor,
and original control/source/native join without host stop. The unchanged production
source fails at that final-join assertion; the correction passes. Four guarded
app-role history cases distinguish absent original with/without peer admission,
committed occupied original, and positively released original before a real second
peer admission. The positive history setup is explicitly a storage fixture rather
than a minted runtime cleanup witness. Peer header and occupancy remain unchanged.

Two additional fresh reviewers, GPT-6-astra and GPT-6-sol at xhigh, inspect the
complete correction and its connected owner/history/proxy controls without further
verified findings. The targeted set passes ten cases, including actual lock wait,
lock timeout and both existing native admission-ACK outcomes. Removing historical
probe dispatch compiles and fails the native join plus three history assertions;
the occupied-original control still passes. Making existing history falsely mean
rollback separately fails that occupied-original control. Both source mutants are
restored byte for byte with their modes, rebuilt, and the same five cases pass.
Focused receipts and negative mutations overlap the full suite; they are not extra
passing cases. Formatter normalization changes only test whitespace.

The first full Docker receipt reports 1,858 passed and one failed among 1,859
cases, with no skips. The common-outage fixture times out after the owned database
pause while waiting for all three original metadata-heartbeat fault observations.
It supplies no evidence of a failed pre-outage healthy job or a cause linked to the
admission-history correction. A separate read-only source diagnosis establishes no
deterministic production fault. Bounded elapsed-phase and failure-time memory-only
pool diagnostics are added before teardown; no wait, option or assertion changes.
The same focused scenario then passes in 38.2 seconds, with pause completed at
0.55 seconds and all native faults observed at 35.3 seconds. This later pass does
not establish the earlier failure's cause. Both independent correction reviewers
inspect the diagnostics and this evidence limit. The original failed receipt is
retained. The second complete Docker run passes that outage scenario, but the
new native rollback case fails before the intended COMMIT drop: zero drops, one
bridge ACK and no retained control execution. No history assertion is reached.
Source inspection confirms a permitted setup race: bridge ACK takes the job-row
lock, native routing can skip that lock, and the default 60-second repair grace
exceeds this fixture's 30-second wait. The receipt does not establish that race
as its cause. The fixture now mirrors the existing lifetime controls: park empty
owner polling, issue an explicit stock/native wakeup, then pulse only owner polling
for the actual original join. It asserts zero initial bridge ACKs and retains the
real second-COMMIT drop, peer takeover, zero attempts/reservations and source refusal.
The outage fixture similarly parks polling around its existing explicit wakeups;
its real heartbeat cadence, 90-second bound and all lifecycle assertions remain.
Both independent reviewers verify the actual eight-file candidate against its
manifest without a further confirmed finding. The repeated production removal
mutant reaches the real COMMIT drop and fails specifically at original final join;
exact restoration passes. Both complete failed receipts remain recorded; the
fresh complete Docker receipt below is a separate passing result.

Final full format verification and CI Release build pass with zero compiler
warnings/errors. The critical native/history/stop/release and lifecycle set passes
**24/24**.
The last changes affect only the two integration fixtures; the exact production
bytes and earlier full Unit/Contract/Docker-free/architecture subjects are unchanged.
Full Unit passes **2,229**, Contract **4**, Docker-free integration **176**,
Architecture **330/330**, and Docker integration **1,859/1,859**, with zero failures
or skips: **4,598 distinct full-suite cases**. Architecture names are unchanged;
all 1,854 preceding Docker cases remain and exactly five new cases account for
the increase. Full architecture takes 1315.5 seconds;
full Docker takes 960.2 seconds. These local measurements
provide no real GitHub Actions budget pass; current-head CI remains unrun.

Step 6 is locally complete after both full fresh reviews, verified corrections
and their independent verification. All nineteen background-job obligations stay
Registered and the published architecture-method count stays 237; Step 9 owns
complete packet enforcement/regression and real PR CI. Accepted ADR-0061 in
`82956666` authorizes the next external-stage implementation. Steps 7–9 and full
packet/PR/CI closeout remain pending; no production business definition is added.

### P02b-4 external completed-failure proposal (2026-10-16)

**Initial proposal status, before the 2026-10-17 acceptance: Proposed; the
maintainer's first review required changes and explicit reapproval.** Current
acceptance is recorded [below](#adr-0061-acceptance-and-step-7-contract-2026-10-17).
Step 7 preparation identified an unanswered report-policy input: direct completed
Failed/Cancelled reports carry outcome/quiescence, without classifying the failure
or granting retry of the same stable logical operation. Existing guards permit
both retry and finalization; they do not select between them.

The initial [ADR-0061](../decisions/0061-known-external-job-failure-policy.md) draft
at `8e0f982b` proposed separate failure class and same-operation permission,
write-once status and a terminal code when transient work cannot be repeated.
Its initial source/contract audit was against `6a2ded99`. No dependent Step 7 runtime or migration is implemented or
authorized by this proposal. Step 6 capacity work continues under Accepted
ADR-0047; the existing nine-step review/commit loop remains unchanged.

**Initial-draft reviews at `8e0f982b`:** two fresh independent proposal reviewers
(GPT-6-astra xhigh and GPT-6.1-sol xhigh) approve that initial draft for maintainer
consideration without verified findings. They
check the actual contract/guard/grant and reason carriers, the exact bounded
supersession, restart equivalence and discriminating pending proof obligations.
The corpus reviewer independently verifies the ADR's links/anchors, Proposed
status and prose width. No runtime test is claimed. Maintainer acceptance still
precedes dependent Step 7 implementation.

#### ADR-0061 revision after maintainer review (2026-10-17)

**Historical revision record, before acceptance below.**
The maintainer requested changes to the initial proposal; that review was not
approval. Source checks confirm the missing finalization/terminal policy guards,
the app/control ownership distinction and the absence of durable cancellation
origin. The [revised ADR](../decisions/0061-known-external-job-failure-policy.md)
remains Proposed and supplies the complete decision and proof inventory.

The new choices for reapproval are one nonzero three-state policy and Permanent
only for Failed. Cancelled explicitly states repeatability of the same operation;
malformed Permanent cancellation is rejected without rewriting it. Adapter truth
must survive reconciliation, while the runtime claims no certification of external
causality. App-owned selection, deterministic storage and actual-row-bound audit
cover retry, finalization and terminal reasons. The ADR separately maps module
reasons, capacity release and all seven affected Registered rules.

Two new independent reviewers, GPT-6-astra xhigh and GPT-6.1-sol xhigh, approve the
full revised proposal for maintainer consideration without Blocker/Major findings.
Their verified Minor corrections name stored outcome kind rather than an
identifier DTO, and publish a clearly Proposed glossary pointer now. Both reviewers
verify those corrections without further findings. These are source/design reviews;
no new runtime, migration or passing execution proof is claimed.

Error Handling's inherited five-attempt/jitter summary is independently reconciled
with already Accepted ADR-0047. This does not accept ADR-0061 or change its budgets.
No Accepted ADR body is edited. Explicit maintainer acceptance and its own decision
commit still precede dependent Step 7 implementation; Step 6 continues independently.

### ADR-0061 acceptance and Step 7 contract (2026-10-17)

**ADR-0061 is Accepted — 2026-10-17.** The maintainer's latest review considers
the previous seven gaps closed and finds no acceptance blocker, conditional on
correcting the catalogue Status step lists before the acceptance commit. This
decision includes Storage Steps 1–2, 6–9 and Retries Steps 4–5, 7 and 9; all seven
affected rules remain Registered, with counts unchanged. Their extended Asserts
record pending Step 7 qualification and Step 9 enforcement, not completed proofs.

[ADR-0061](../decisions/0061-known-external-job-failure-policy.md) selects the
three-state policy, Permanent only for Failed and deterministic database retry,
finalization and reason guards. The app owns the immutable attempt outcome/policy
and finalization selection; actual returned row facts bind terminal audit.
[ADR-0047 Amendment 4](../decisions/0047-transactional-background-job-runtime.md#amendment-4--known-completed-external-failure-policy-2026-10-17)
discloses the exact completed-report input replacement and additive seams without
rewriting the Accepted body. The decision index, glossary, Error Handling status
and all seven catalogue assertions are synchronized in this acceptance decision.

Winning owned stop closes new outcome-checkpoint and COMMIT admission even with
a live database lease; an already admitted COMMIT retains its actual result.
Durable confirmed outcomes win over later reports. Honored cancellation remains
Cancelled across restart. A nonrepeatable cancellation may finally fail the same
job during deploy through the prepared domain finalizer and atomic MUST audit;
the Transient taxonomy supplies no promise of resumability.

Step 7 now implements and qualifies this contract before external activation,
including the forward migration and normative implementation carriers. Step 9
closes full enforcement/regression. The nine-step sequence and two fresh
independent implementation review rounds per step continue; the historical draft
reviews above do not satisfy them. Step 6 capacity work remains independently
authorized under ADR-0047. Acceptance supplies no new runtime or passing proof.

### P02b-4 Step 7 — external stages and recovery (2026-10-17)

**Implementation, both full review rounds and verified corrections complete.**
The round-2 closeout below owns the final local receipts. Step 6
closes at `0ea50b15` after both full reviews, verified corrections and 4,598 passing
full-suite cases. Accepted ADR-0047 and ADR-0061 authorize this next step on
`development`; no further decision or permission is pending.

**Remaining closure units — 2026-10-17.** The original Step 7 scope remains
unchanged. Its long-running implementation is now tracked by these explicit
closure units; focused receipts below do not close the step or its full reviews:

- [x] Finish no-plan terminal cleanup, exact owner handoff and atomic terminal
  audit, including persistent reason and complete positive history guards.
- [x] Finish finalizer rollback/retry/refusal/exhaustion and prepared uninvokable
  terminal behavior, preserving the separate six-start finalization budget.
- [x] Close external/transactional contention, retained cleanup, exact owner/
  source/caller censuses and their planted violations; qualify the complete step.
- [x] Commit Step 7 and complete both fresh independent full review rounds,
  verified corrections and correction commits before advancing to Step 8.

The foundation separates sticky process/run stop from eight finite stage COMMIT
boundaries. Admission is prebound before SQL; physical acknowledgements survive
stop without reopening later stages. Transactional capacity cleanup retains its
own fresh boundary on the same process cell and original clean source. The actual
successful result reaches the existing sole completion participant. Closed report
factories require the explicit three-state policy; Completed is removed.

An early targeted source review identifies one P1 with two stop-refusal paths that
faulted owned tasks before their finite cleanup/publication handling. Both paths
are corrected. Two actual source mutants independently fail the corresponding
admission or grouped-business regression while its sibling passes. Exact source
and modes are restored, the CI Release builds have zero compiler warnings/errors,
and that correction's focused foundation suite passes 61 cases with zero failures/skips.
The reviewer independently verifies the correction, actual receipts and restoration.
These checks and that early review supply no full Step 7 review-round credit.
Two earlier failing fixture receipts remain recorded; the diagnostic identifies
an incorrect NeverStarted expectation, corrected to the existing positive owned
transactional-rollback result while separately asserting unused root entry.

The forward failure-policy migration and canonical guards now pass 189 actual
PostgreSQL storage cases. Two early source-review findings are reproduced and
corrected: terminal requests cannot set the incomplete-finalization flag before
the actual terminal transition, and incompatible retained predecessor selection
or outcome/release history refuses upgrade without inventing missing facts.
Independent correction inspection verifies those fixes. External denial also
covers pre-existing UoW frames before their state changes and the four concrete
owner-null auxiliary loaders; their original failing controls and corrected
receipts are retained.

The original-execution checkpoint foundation adds exact app-role read, Arm and
Outcome requests on the existing transaction coordinator. Prepare captures its
actual successful typed result, writes the immutable canonical plan and stamps
the original normal attempt in the same transaction. Its clean source remains
available for the next sequential checkpoint. These paths do not activate the
external routing engine.

An early checkpoint review identifies a canonical-byte defect: JSONB parameters
rewrote text-column payloads. Both plan and outcome now use text parameters.
Actual PostgreSQL execution separately exposes four missing SQL-concatenation
spaces; the initial six failures and diagnostic `42601` receipt remain recorded.
After correction, six state/policy cases and two typed byte-boundary cases pass.
The production Prepare and Outcome writers preserve 65,536-byte canonical JSON;
a 65,537-byte outcome refuses before mutation and leaves occupancy retained.
Reintroducing only the plan JSONB binding fails all eight cases, including the
actual plan byte CHECK. Reintroducing only the outcome binding fails seven while
the oversized-input refusal still passes. Exact source/mode restoration passes
all eight again. Independent correction review verifies source, receipts and
both restoration hashes; this supplies no full Step 7 review-round credit.

At the earlier checkpoint snapshot, the full Unit assembly passes 2,305 cases and the
focused PostgreSQL selection passes 349, with zero failures or skips. The latter
includes all 189 storage cases, the eight checkpoint cases and existing physical
owner/business/audit/helper/source controls. Its receipt validator initially
expected 342: the expanded class filter also selects seven unchanged source-owner
siblings. A separate exact-subject and 95-file source/hash audit confirms all
original subjects plus those seven passing cases; no failed test is reclassified.
Fixture-supplied admission and Audit Off business roots isolate checkpoint SQL;
these are not provider execution or domain-MUST-audit proofs.

The original Execute bridge now requires the exact command owner's physical Arm
acknowledgement, the current Arm boundary, the original clean source and a frozen
canonical plan. The external denial scope precedes adapter construction; the
actual task, immutable observation and failed cleanup owners remain retained.
An early review catches a command borrowing another command's gate acknowledgement;
the correction and its discriminating regression are verified independently.

Fresh recovery has a distinct eighteen-column status projection and never mints
a counted start receipt. Its new lease can outlive the original attempt deadline.
Acquisition/probe publication follows actual COMMIT acknowledgement or explicit
probe rollback acknowledgement and successful resource cleanup. A specialized
owner retains the originating operation, exact descriptor and process stop cell;
all confirmations share one receipt and one source/run binding. Early review
identifies original-cleanup substitution, duplicate receipt publication and a
cross-process-cell edge; all three are corrected and independently verified.

The corrected snapshot passes 2,331 full Unit cases and 75 focused PostgreSQL
cases with zero failures/skips. Four actual source mutants each fail exactly their
intended regression; baseline and every restored source pass all eight selected
cases. Exact source hashes/modes and the receipts are independently audited.
Two further PostgreSQL cases prove actual acquisition COMMIT-response loss with
repeat confirmation of the same receipt, and refusal after a real token takeover.
The complete seventeen-case external checkpoint selection passes after those
additions. These storage/ownership checks do not claim provider execution,
domain-MUST atomicity or full Step 7 review-round credit.

The three closed app requests now distinguish original admission from fresh
recovery authority. Original ledger identity remains separate from the current
recovery token; an expired original deadline does not invalidate a live recovery
lease. The read represents an absent plan explicitly and obtains causal metadata
from the actual app-role envelope. Its provisional recovery context invents no
trace, parent, incoming event or actor. Stored-fact parsing requires the exact
reservation and complete canonical plan/outcome, policy, quiescence and release
facts. Existing completed outcomes take precedence over conflicting later reports.

An early app-owner review identifies a reused-plan exhaustion defect: a sixth
normal admission stopping before Arm lacks the plan stamp required by failure
selection. Two actual PostgreSQL regressions reproduce the exact `23514`, both
for first no-start and for independently retained unstamped no-start history.
The correction copies the validated immutable plan identity in the same locked
no-start transaction, preserving any existing timestamp and release. Its first
rerun exposes a second guard ordering issue: validated recovery finalization is
checked before eligibility-cycle normalization clears its token. The forward
migration and canonical guard now admit that exact app transition; normalizing
the new cycle still clears all recovery ownership and advances generation.
The predecessor Down body remains unchanged. Both failures remain in the receipts.
Independent inspection approves both corrections without full-round credit.

The corrected snapshot passes all 2,331 Unit cases and 275 PostgreSQL cases:
189 storage, 26 external checkpoint, 20 capacity-admission and 40 control cases,
with zero failures/skips and a CI Release build with zero warnings/errors.
The checkpoint selection includes sixth-admission first/retained no-start,
recovery success and final-failure selection, token revocation and generation
advancement, no-plan recovery, armed no-start refusal and durable-outcome
precedence. These remain app/storage proofs with explicit fixture admission and
Audit Off roots; provider/native execution and domain-MUST atomicity are pending.

The subsequent short-checkpoint boundary captures a conservative monotonic
lease window from fresh SQL after the actual job lock and before writes.
Original work also observes its immutable deadline; recovery uses only its fresh
lease. After pending audit, the exact owner's COMMIT admission checks the elapsed
window inside the same run lock as the stage CAS. Expiry closes the run and rolls
back; it cannot borrow an earlier successful handler check. This adds no generic
participant, callback or transaction entry.

That boundary snapshot passes 2,333 Unit and all 32 PostgreSQL checkpoint cases.
Six cases park the actual handler before COMMIT admission without token
cancellation: Arm, original Outcome and recovery Outcome each have live and
expired-lease controls. PostgreSQL time establishes expiry; assertions inspect
actual rollback or committed intent/outcome/selection and capacity release.
Removing only the elapsed predicate fails exactly the three expiry cases while
all three live controls pass. Restoring exact source bytes and modes passes all
six again; both CI Release builds have zero warnings/errors. This remains a
targeted ownership proof, not external engine activation or full review credit.

Read completion now also binds the exact canonical candidate recorded by its
actual read-only owner. An unrelated successful result or an unchanged clone
cannot obtain that owner's acknowledgement. Three actual PostgreSQL controls
verify the physical read-only transaction and application role, reject missing
or substituted candidates and accept the real recorded object. This later
snapshot passes 2,333 Unit and all 35 checkpoint cases, with zero failures/skips
and a CI Release build with zero warnings/errors. Executor read-handle publication
and cleanup ownership are qualified in the subsequent snapshot below.

The named executor read driver now publishes an immutable handle only from the
exact acknowledged app-read candidate and positively completed cleanup. Recovery
uses provisional null causal metadata during Read; its handle retains only the
actual app-read envelope for later checkpoints. Actual held and throwing DI scope
controls cover both original and recovery origins. A physical read acknowledgement
survives cleanup failure, while the failed scope/unit owner stays retained and no
handle is published.

An early review reproduces a cancellation gap after registration disposal but
before Activity cleanup, together with the pre-cancelled-entry edge. Registration
now remains owned through all actual cleanup; a final token check closes the run
before publication. Both original red cases fail on the intended open-run state.
The corrected snapshot passes 2,333 Unit and all 44 PostgreSQL checkpoint cases,
with zero failures/skips and a CI Release build with zero warnings/errors.
Subsequent test-only Task.Run hardening first exposes a test-ordering race in
the pre-cancelled case. Awaiting that actual task before its assertion corrects
the test; the held-cleanup case still asserts closure before release. Both final
cases pass after recompilation. Independent review verifies the unchanged
production hashes, exact subjects and separate receipts, closing the focused
finding without full Step 7 review-round credit.

The named Arm, original/recovery Outcome and recovery-only NoStart drivers now
consume that exact read handle, retain their actual sealed request and await
scope, Activity, binding and cancellation cleanup. Original admission identity
stays separate from fresh recovery ownership. Only the actual app-read envelope
supplies later causal attribution; no provider report or bypass projection does.

An early writer review corrects lost Activity ownership when Start throws and
duplicate issuance borrowing an earlier gate's COMMIT history. Real observer and
repeated-Arm cases discriminate both fixes: each source mutation fails exactly
its intended controls, and exact source/mode restoration passes again. All four
CI Release builds have zero warnings/errors. The first causal fixture run retains
two pre-driver `23514` failures from a nonexistent parent; a real parent row fixes
that fixture. Independent review audits 119 restored source hashes/modes, 2,333
passing Unit cases and all 54 passing checkpoint cases, including ten named-writer
cases. This is a bounded slice receipt, not external activation.

Six additional cases pass in the complete sixty-case checkpoint rerun. Actual
TCP COMMIT-response loss leaves Arm/Outcome uncertain while an outside app read
observes the landed transaction. Stop after the real handler but before COMMIT
records actual rollback; pre-cancelled entry and cancellation during held Activity
cleanup close continuation without erasing historical ACK. One further passing
case distinguishes an unissued duplicate from an earlier command's known rollback
and then commits through a fresh gate without another counted attempt. Production
source is unchanged from the 2,333-case Unit snapshot; the sixty-plus-one receipts
cover 61 distinct checkpoint cases. Seven ADR-parser and five corpus checks pass.
Provider/native integration and full Step 7 reviews remain pending.

The original Execute bridge now accepts only the trusted read handle and exact
local Arm result. Fresh Reconcile accepts only its once-bound recovery and read
handle. Both retain the actual provider/cleanup task and external denial; stored
outcomes or absent intent skip provider construction without fabricating a report.
The last typed method-entry CAS remains separate from any earlier validation.
These helpers remain unwired to native execution and current-lease control.

The next snapshot passes 2,333 Unit and eleven actual PostgreSQL bridge cases.
An early test review corrects masked ACK-isolation coverage using a real read
and an explicitly synthetic negative result around a distinct unacknowledged
command. It also distinguishes the first scope-only receipt from constructor
proof: that host lacked the fixture's adapter registration. The revised test-only
binding invokes the exact frozen, unchanged throwing constructor and records its
actual fixed exception under external denial. All eleven revised cases pass.
Removing both command-derived ACK checks fails only the borrowed-ACK case while
six siblings pass; exact source/mode restoration passes all seven again. Both
CI Release builds have zero warnings/errors. A throwing constructor proves entry
or refusal, never successful Execute/Reconcile or native lifetime semantics.

The same start-observer ownership defect is then reproduced in the admission and
Prepare entry paths: both actual cases fail because the started Activity was lost
before assignment. Assigning the actual object before Start corrects both paths.
The subsequent CI Release build has zero warnings/errors; 2,333 Unit and 137
PostgreSQL cases pass with zero failures/skips. That selection comprises all 74
external-checkpoint cases, 23 admission, 20 business and 20 capacity-admission
cases. It includes both original red observer cases, the revised bridge controls
and the existing stop/cleanup/commit regressions. Full Step 7 review and native
activation obligations remain open.

The history foundation now retains an exact issued Prepare/Arm/Outcome/NoStart/
Finalize operation, with immutable business-result receipt and gate identity.
A separate job lock precedes its fresh, status-only 48-column original-attempt
query. Confirmation publishes only after explicit probe ROLLBACK acknowledgement
and actual cleanup; it never sets physical COMMIT ACK or rollback. Real lost-ACK
controls cover Prepare, Arm, Outcome and NoStart with open and stopped runs, plus
a closed duplicate Prepare that cannot borrow its predecessor's history.

Early review corrects two unqualified Finalize classifier branches and separates
historical Arm truth from current Execute permission. Both the outer bridge and
last run CAS now require the exact command, current original owner and a
conservative monotonic lease/deadline observation. Live, takeover with an open
old run, elapsed-lease and borrowed-command controls use actual app checkpoints;
their positive remains constructor-canary evidence, not a successful provider
method. This snapshot passes 2,333 Unit and 127 PostgreSQL cases: 87 checkpoint,
20 business and 20 capacity-admission, with zero failures/skips and a CI Release
build with zero warnings/errors. An earlier Unit run retained one obsolete
unissued-root rollback assertion; its correction preserves the independent
positive unused-entry and capacity-cleanup controls. No full review credit or
Finalize/native qualification follows from these bounded receipts.

Additional history controls distinguish COMMIT-request loss from response loss:
all four original checkpoint families remain Absent when COMMIT never reaches
PostgreSQL, without inventing a physical rollback acknowledgement or reopening
the old root. Two probe cases hold or lose the actual ROLLBACK response; a peer
acquires the released job lock before client acknowledgement, and confirmation
publishes only after acknowledgement and cleanup. Two later-admission cases keep
history bound to the original stamped attempt after current identity changes.
Replacing that query join with the current attempt fails exactly those two
controls; all sixteen same-current controls pass. Exact source/mode restoration
passes all eighteen. Both CI Release builds have zero warnings/errors.

The wider history snapshot passes 2,333 Unit and 179 PostgreSQL cases: 99
checkpoint, 40 control, 20 business and 20 capacity-admission. An independent
receipt audit verifies all 130 recorded source hashes/modes, exact subjects and
artifact hashes. These app/storage proofs do not exercise successful provider
methods or Finalize; socket-loss cases do not independently inject every disposal
failure.

The ordinary business completion owner now also carries one conservative
COMMIT budget from its fresh locked SQL observation. Prepare's final plan-stamp
cleanup and transactional reservation-release cleanup consume that interval.
The existing coordinator reaches a synchronous, exact-root wrapper; elapsed
time is checked inside the same run lock as COMMIT admission. One-way publication
states prevent concurrent or duplicate recording from replacing the first
timestamp or interval. No new participant, callback or transaction owner is added.

Four actual PostgreSQL controls pause the final Npgsql command after successful
SQL/reader cleanup, with no cancellation token or runtime timer closing the run.
Both expired-lease cases fail against the earlier implementation because it
acknowledges COMMIT; both live controls pass. The correction passes all four.
The final publication-state snapshot passes 2,333 Unit and 183 PostgreSQL cases:
103 checkpoint, 40 control, 20 business and 20 capacity-admission, with zero
failures/skips and a CI Release build with zero warnings/errors. Transactional
admission is the production path; Prepare still uses the explicit fixture
admission. These focused checks supply no full Step 7 review-round credit.

The existing owner-boundary detector now enumerates the exact budget methods,
callers, private publication fields, fresh SQL observations and protected COMMIT
ordering. Its actual-subject and planted-control cases both pass. Controls include
extra callers, deleted capabilities, changed projections, late capture, mutable
budget reset, missing live identity and a plain-CAS bypass. This finite structural
check supplements the actual PostgreSQL expiry controls; it does not prove every
possible value, branch or asynchronous control flow. Full census qualification
and both full Step 7 review rounds remain pending.

The shared app reader also validates the selected normal attempt, finalization
status and module reason. Its eleven focused cases cover completed policy rows,
sixth-attempt repeatable failures, positive no-start history and all partial-null
selection shapes. Projection-only dirty inputs reject mismatched identity, stamp,
status or reason without altering guarded durable data. An earlier run retained
one fixture failure from waiting for the original deadline instead of the lease;
the corrected helper and expanded matrix pass. The current snapshot passes all
2,333 Unit and 192 PostgreSQL cases: 112 checkpoint, 40 control, 20 business and
20 capacity-admission, with zero failures/skips and a CI Release build with zero
warnings/errors. These are reader/storage proofs, not actual Finalize execution,
successful provider methods or the domain/terminal two-MUST atomicity proof.

The actual app Finalize path now reads and cleans its selected-input owner before
ordinary root entry, admits a separate counted finalization attempt and constructs
the exact registered typed request. Finalize bypasses capacity admission and
creates no reservation. Its existing business owner rechecks the actual selected
normal facts after the job lock and retains the same monotonic COMMIT budget.
Successful domain failure writes terminal state and its MUST row on that pair;
the private app mint binds the actual returned row, old acquisition token, live
unit/source/announcement and selected policy. The existing bypass mint remains
separate. A duplicate consumed root has no new read or COMMIT history.

Four actual PostgreSQL cases cover success and permanent/nonrepeatable completed
failure. The first run passes success but refuses the three failure cases with
`42501`: the authored UPDATE unnecessarily names a control-only incomplete flag.
Removing that assignment and requiring the existing false value preserves the
column grants; all four pass. Final assertions bind audit identity, generation,
timestamp, policy and counters to actual stored facts. The corrected snapshot
passes 2,333 Unit and 196 PostgreSQL cases: 116 checkpoint, 40 control, 20 business
and 20 capacity-admission, with zero failures/skips and a CI Release build with
zero warnings/errors. Normal admission, observations and the routing claim remain
explicit fixture setup; app Finalize admission, receipt, read, root, fence and
terminal MUST write are production paths. These cases use an Audit Off root and
do not claim a domain mutation with two MUST rows, native provider execution,
independent witness-substitution coverage or full Step 7 review-round credit.

The physical-ACK Arm path also retains its original conservative SQL interval.
An actual PostgreSQL control demonstrates that ACK alone previously allowed
provider scope construction after that lease expired, without cancellation or
a native timer; the live control passes. The corrected gate publishes its one
immutable interval only for an issued open boundary. Both the pre-scope check and
the final Execute CAS check elapsed time; the latter runs inside the original run
lock. Premature or late rejected COMMIT calls cannot seed a window for a later
unbudgeted acknowledgement. Known entry expiry reports EntryRefused, preserving
the physical ACK and inventing no provider failure or outcome.

The corrected snapshot passes 2,335 Unit and 198 PostgreSQL cases: 118 checkpoint,
40 control, 20 business and 20 capacity-admission, with zero failures/skips and
a CI Release build with zero warnings/errors. The unchanged throwing adapter
canary verifies pre-scope refusal; a real held-constructor/provider-method timing
proof remains part of the positive SDK work. Lease renewal does not extend this
retained interval, and repeated history probes do not replace the gate's first
confirmation. These conservative refusals are distinct from permission to execute.

The separate positive SDK fixture now supplies genuine Contracts, Application and
Infrastructure assemblies, with actual nested Tenancy commands and their MUST
audits. Its adapter calls an independent provider database using a separate,
unprivileged credential. Normal External routing and app admission use the actual
opaque claim, tenant capacity mutex and counted ledger; Finalize uses its separate
real admission. The first routing run exposed an ungranted outcome-body read;
the corrected predicate uses the already granted outcome-kind status instead.
No dispatcher grant is widened. Production definitions remain empty and the
construction-only test host runs no native background worker.

Eight focused PostgreSQL cases pass: four actual provider outcomes, two exact
audit-INSERT faults and live/expired adapter-constructor barriers. A test-owned
SQL observer records the actual top-level transaction ID, preserving savepoint
semantics; domain state, successful business MUST, terminal state and terminal
MUST share the Finalize transaction. Each injected MUST failure rolls that work
back while prior Prepare/provider facts remain. The existing audit coordinator
may separately record the failed business attempt; this is never a success row.
The initial tuple-`xmin` assertion was invalid across EF savepoints and was
replaced by this observer, without changing production transaction handling.

The held constructor retains its actual task and scope through release and join.
With the final run-locked Arm-window check removed, the expired case fails and
the real provider journal records an unexpected Execute and effect; its live
control passes. Exact source bytes and mode are restored, then all eight cases
pass again. These cases establish actual provider-method timing and real-store
atomicity, not native handoff, duplicate-provider acceptance or recovery. The
2,335-case Unit run and expanded 247-case PostgreSQL regression pass on the
corrected snapshot, with zero failures/skips and a CI Release build with zero
warnings/errors. The old External-unavailable admission assertion is replaced by
an exact profile/definition mismatch refusal; the genuine SDK flow supplies the
positive External coverage. The broader run retains the existing checkpoint,
control, business, capacity, admission and submission cases.

The next native-control foundation binds the real External receipt's profile,
normal/finalize stage, counters and selected outcome to its existing execution
owner. Prepare acknowledgement no longer ends lease renewal. Its status-only
renewal probe checks the exact locked job and fresh immutable ledger, before
generic completion shortcuts. Two actual publication/control cases and seven
passive metadata substitutions per case cover both preparing and finalizing.

The Prepare case starts the real renewal lane and independently observes SQL
lease advancement and a successful rearm of the actual System timer. Restoring
the two old Prepare-ACK guards leaves SQL renewal working but fails that timer
assertion. Exact source/mode restoration passes all ten SDK cases. The frozen
150-source snapshot passes 2,335 Unit and fifty focused PostgreSQL cases (forty
control, ten SDK), with zero failures/skips. An independent receipt audit verifies
all source hashes/modes, artifact hashes and actual TRX subjects. A retained
zero-match run after an initial compile failure supplies no test credit. This
foundation does not yet prove complete native provider handoff or recovery.

The existing native execution owner now retains the optional Prepare, app read,
Arm, provider and Outcome tasks, with separate exact-family historical probes.
It skips Prepare when admission supplies the existing-plan stage. A normal return
closes that original run; unresolved effects retain reservations for fresh recovery.
An acknowledged Outcome stops renewal even during held cleanup, while actual
cleanup still gates identity release. Finalize remains a separate admission/root.

Four genuine SDK cases now pass through the real facade, stock bridge and native
worker pools, independent provider journal and fresh Finalize admission. Success,
permanent failure and both nonrepeatable failure/cancellation preserve one normal
and one finalize receipt, one provider Execute, and actual business/terminal MUST
atomicity. The earlier two-case red run reached Prepare but stayed `prepared`,
discriminating the missing native continuation from the explicit-driver proofs.

A held real adapter constructor exposes the former global short-wrapper timeout.
The existing invocation now owns one monotonic stage timer, armed before each
possible synchronous prefix, and retains every task after wrapper return. Its
finite mapping uses short-root, SQL, external-run and history-probe bounds. Lease
loss, deadline and host stop close/retain before signalling wrapper return; normal
capacity-cleanup cancellation remains separate. Actual timer disposal and private
cancellation callbacks precede source release. Deterministic controls distinguish
an old queued callback from the current stage and forbid refreshing elapsed work.

The qualified 158-source snapshot passes 2,339 Unit and one hundred PostgreSQL
cases: forty control, twenty-three admission, twenty business and seventeen SDK.
The SDK selection includes all four native results, the long-constructor wait and
host-stop/lease-loss cases that return the wrapper while actual cleanup stays held.
The initial Unit run retained one obsolete host-stop `Running` expectation; its
correction independently counts running plus retained ownership. Removing only
the actual cancellation join fails the held-callback test while its three siblings
pass. Exact source/mode restoration passes all 2,339 again. Builds have zero
warnings/errors; no full Step 7 review or fresh native recovery is claimed.

The receipt addendum includes the changed host-stop Unit fixture omitted from the
initial 157-path inventory. It preserves the original manifest and distinguishes
the two subsequent Unit-only corrections from unchanged production/PostgreSQL
sources; an independent audit verifies the complete 158-path inventory.

Four further genuine native cases hold actual COMMIT responses at Prepare, Arm,
Outcome and Finalize. Their clean, exact-family historical confirmation permits
continuation without inventing physical acknowledgement or duplicating provider
work. The Outcome case also handles a real early wakeup while the original
invocation remains retained: a pre-routing identity check avoids acquiring a
routing lease that the same owner must immediately refuse. The initial red run
fails only that Outcome control; the corrected four-case run passes.

A separate actual Outcome-ACK control holds Activity cleanup after the database
scope has closed. Its gate is acknowledged while the concrete task and invocation
remain retained. The live control lane neither rearms the settled lease timer nor
reports a renewal refusal; actual cleanup still precedes identity release. An
already issued renewal batch may finish. This does not claim zero SQL after ACK
or immediate next-stage service: a consumed early vendor hint can wait for repair.

The final 160-source snapshot passes all 2,339 Unit and 261 PostgreSQL cases:
118 checkpoint, forty control, twenty business, twenty capacity, twenty-three
admission, eighteen submission and twenty-two genuine SDK cases. There are no
failures/skips; CI Release builds have zero warnings/errors. The snapshot keeps
red/green epochs and the test-only repair-grace reduction separate. Fresh native
recovery, full architecture qualification and both full Step 7 reviews remain
pending.

Fresh recovery now carries a source-local Stopwatch observation taken before its
actual locked SQL projection. Binding and the final typed Reconcile entry check
the same remaining lease under the Run lock; expiry closes that Run permanently.
Neither binding, cleanup, a held constructor nor a later probe rebases its window.
The original attempt deadline may already have expired; it is not the new lease.

A genuine SDK live/expired pair first executes against the independent provider,
omits Outcome and then acquires fresh recovery for that exact old attempt. A held
real adapter constructor exposes the missing final check: the red expired case
incorrectly calls Reconcile; the correction prevents that actual method call,
while the live control reconciles the retained effect. No extra Execute, normal
attempt or effect is created. This is an explicitly arranged recovery route,
not native bridge/control recovery qualification.

Three additional real-socket controls hold the exact recovery probe's completed
ROLLBACK response. A peer can acquire the released row lock before the client ACK,
but no receipt publishes yet. Live ACK/cleanup permits the first binding; a clean
response delayed past the observed lease publishes history but refuses binding.
A lost response publishes nothing; a later clean first probe cannot rewrite its
failure or physical acquisition ACK. Repeated probes retain the same receipt.

All 2,342 Unit and 145 focused PostgreSQL cases pass with zero failures/skips and
zero-warning/error Release builds. The PostgreSQL selection contains 121 checkpoint
and twenty-four SDK cases. An Integration-only test cleanup correction then joins
the actual probe task using its existing deadline even on failed premises; its
three affected controls pass again. Earlier source epochs are retained separately.
The subsequent recovery-renewal foundation preserves the original ledger token
and expired deadline separately from the fresh recovery token. Its actual SQL
extends only a still-live exact recovery lease; ordinary execution remains capped
by its original deadline. Private passive evidence publishes only after physical
COMMIT acknowledgement and actual cleanup. The Run accepts it under its own lock
only while both windows remain live, without changing the acquisition receipt or
resetting provider entry. Observing expiry closes the Run and current checkpoint.

The actual expired-original case first fails against the old execution-only SQL.
Four genuine SDK controls then pass: a two-tenant mixed renewal batch, lost renewal
ACK without evidence publication, and timely/late evidence application around a
held real Reconcile constructor. Timely application permits one Reconcile after
the old window expires; late application cannot reopen it. These are delayed
applications of already acknowledged evidence, not held successful network ACKs.
Fourteen memory identity substitutions and separate binding/process/stop/order
controls exercise the acceptance boundary without claiming SQL provenance.

The frozen 164-source foundation passes 2,361 Unit and 189 focused PostgreSQL
cases with zero failures/skips and zero-warning/error Release builds. The latter
contains 121 checkpoint, forty control and twenty-eight SDK cases, including the
four renewal controls. A lost ACK's later status probe proves the recovery row's
renewal without publishing usable evidence; the ordinary mixed row is independently
checked in the direct-ACK case. Native renewal consumption, actual cleanup-fault
injection, native recovery handoff and retry/quarantine/finalizer failure routing
remain pending, as do both full Step 7 review rounds.

The native recovery owner now roots acquisition, exact historical confirmation,
app read, Reconcile and checkpoint tasks before issuance, without fabricating an
ordinary admission. Its fresh lease survives the expired original deadline;
actual renewal evidence feeds the existing Run boundary. Two genuine SDK controls
exercise direct/lost acquisition ACK and renewal across the first recovery window.
They arrange parking explicitly and do not qualify automatic scheduling.

NoStart's lost app COMMIT ACK now uses its exact history family. Real timer
construction/Change controls hold the actual call while host stop closes permission;
returned handles require actual disposal, while unreturned failed initialization
retains ownership. Renewal keeps its old expiry visible during Change, and Join
awaits the actual update before disposal. A clean late return can decline renewal
without inventing failed cleanup. Held-stop controls do not prove autonomous expiry
at a shorter deadline during an arbitrarily blocked supplied Change.

The frozen 167-source correction passes 2,362 Unit and 280 PostgreSQL cases:
121 checkpoint, forty control, twenty business, twenty capacity, twenty-three
admission, eighteen submission and thirty-eight SDK cases. There are zero
failures/skips; Release builds have zero warnings/errors. The new Change controls
first fail at the actual blocked stop/late identity boundaries, then pass after
correction. Earlier constructor/history and known-fault native epochs remain
separate. Automatic park/bridge/disposition and both full Step 7 reviews remain
pending.

The next automatic-recovery slice adds two bounded control selections for expired
external owners and retained unarmed recovery rows. A single eligibility cycle
arms a finite probe and advances the wake generation once; stock handoff/repair
retains that generation, the original normal attempt and its reservation. A
forward migration and canonical DDL guard live-owner clearing, unarmed/reset
transitions and unsafe downgrade. Five actual SDK controls plus canonical-schema
parity pass, including an automatic facade recovery and both pending routing
stages. This does not qualify every future-due, exhausted, contention or planner
case.

Its broader frozen 172-source run passes 493 of 494 PostgreSQL cases and exposes
one native lost-Outcome-ACK history failure: a competing row lock legitimately
returns a clean unavailable result. The correction retries only that exact clean
unavailable history read, after actual cleanup, inside the already armed single
history budget. It never treats unavailability as confirmation or resets the
original origin. A separate real transaction holds the exact job lock; the socket
observer ties the actual binary UUID/query to PostgreSQL's zero-row response.
That causal control first fails at the missing history value and then passes.
Seven focused history controls and all 2,362 Unit cases pass with zero skips and
zero-warning/error Release builds. The broader failed run remains a failed
receipt; these narrower passing controls do not imply a fresh full regression.

The application-source inventory now includes external invocation owners alongside
transactional owners. Its job-only floor is 22 by default and 118 at the maximum
business-pool topology; unrelated arrival headroom stays separate. The shared
privileged inventory remains 50/156 because each invocation's routing, recovery
acquisition, confirmation and history operations are sequential and cleanup-owned.
The maxima need separate calculations: the qualified eight-pool/four-worker
facade fixture has privileged 156 and application 32, without claiming that
every topology reaching privileged 156 has the same application floor.

The new count/kind and immutable 21/22 startup controls first fail against the
transactional-only calculation. After correction, 15 actual PostgreSQL cases
and all 2,365 Unit cases pass with zero skips and zero-warning/error builds.
Actual main/auxiliary connections fill 22/118 pools, with explicit zero/two
unrelated headroom controls; four native normal SDK cases and automatic recovery
use pooled application 22/privileged 50. These controls do not claim held external
versus transactional native contention or maximum-topology native execution.

Retained completed facts now have a recovery-only app selection mode. Its actual
trusted read supplies plan/operation identity; no caller supplies a replacement
report, payload, policy or quiescence fact. The handler re-reads canonical stored
facts under the job lock and applies their policy. Missing facts and early
repeatable outcomes are refused by both driver and actual handler; selection
never rewrites outcome, quiescence, NoStart or reservation-release stamps.

An existing marker alone no longer confirms a selection-only transaction.
Outcome history requires the exact policy-derived selected normal attempt,
status, reason and plan stamp; prepared-plan NoStart at six likewise requires its
exhaustion selection. Actual request-loss controls first expose both false
confirmations, then pass after correction. Actual response loss confirms the
selection while keeping physical ACK false, including after a distinct counted
Finalize admission advances the current header.

The native recovery owner consumes those retained facts through the same held
app checkpoint task and commit budget. A genuine SDK control first remains
Reconciling against the old early-return branch, then reaches completion after
the correction without another provider scope, Reconcile or Execute. Its peer
with absent Outcome still performs one Reconcile. Thirteen focused PostgreSQL
cases and all 2,365 Unit cases pass with zero skips; the broader checkpoint/SDK/
control regression is still pending at this receipt. This delivers selection,
not early-repeatable retry disposition, probe/quarantine or finalizer-fault policy.

That subsequent run executes 217 PostgreSQL cases: 216 pass and one native
nonrepeatable case remains FinalizePending at its fixed twenty-second assertion.
Its saved receipt contains no ownership/handoff snapshot, so its exact cause is
not retrospectively established. The source permits a successor hint to finish
while the old acknowledged Outcome still owns actual cleanup; the default
sixty-second repair grace exceeds that test's former wait.

Two causal controls, for success and nonrepeatable failure, now hold the actual
Outcome Activity cleanup. They observe the exact new-generation persisted vendor
hint as Succeeded, cleared bridge/ownership fields and a future database-time
handoff check while the old task is incomplete and routing is refused. Releasing
the real cleanup permits same-generation repair and one Finalize, with unchanged
normal count and independent provider counts. The NativeFlow fixture uses the
accepted fifteen-second repair minimum and a wait derived from repair grace plus
Finalize transaction/SQL budgets. Both causal cases and all four native flow
cases pass together. The complete forty-six-case SDK regression then passes
with zero skips and zero-warning/error builds. Only the two SDK test files
changed; production and all other frozen sources remain byte-preserved. Earlier
132 checkpoint and forty control passes, plus 2,365 Unit passes, stay tied to
their unchanged-production epoch; this is not a fresh 218-case run. The failed
217-case receipt stays distinct, and both full Step 7 reviews remain pending.

Known-fact retry now accepts only a closed original-completed or fresh-recovery
identity. Under the actual job lock, its status-only read validates every normal
ordinal and positive NoStart/release or repeatable completed-outcome/quiescence
history. The current original admission token, deadline, scope and generation
remain distinct from a fresh recovery token. Missing or unresolved facts cannot
return work to Pending.

For a live original owner, lease shortening, private nonexecuting recovery handoff
and fixed-backoff Pending selection share one transaction. Only the final returned
row permits COMMIT; refusal rolls back the intermediate ownership too. A fresh
recovery owner uses its live lease independently of the expired original deadline.
Scheduling advances one generation without adding an attempt, rewriting evidence
or starting provider work. The uncertainty descriptor retains the actual returned
schedule. Its locked probe distinguishes the exact old rollback shape, committed
same-cycle routing and a distinct later admission; no generic rollback fallback
or executable receipt is used.

The native owner retains the actual disposition and optional confirmation tasks
through cleanup. It issues them only after a clean acknowledged or exactly
confirmed app checkpoint; retained known recovery facts need no new provider report.
A focused source review removed an unnecessary recovery-side control call after
final-selecting outcomes. Twenty-eight real PostgreSQL cases pass after that
correction, with zero skips and zero-warning/error Release builds. The twenty-one
store cases cover all five backoffs, real COMMIT request/response loss, subsequent
routing/admission, unresolved/nonrepeatable refusals, final-UPDATE rollback and
positive no-plan NoStart after actual original-deadline expiry.

Seven actual SDK/native cases cover Failed/Cancelled original retry with normal
or lost Outcome ACK, plus recovery from retained facts, Reconcile and absent
ExecuteIntent. Held actual writer cleanup forbids early disposition issuance;
lost ACK requires its exact clean history confirmation. The subsequent counted
attempt preserves the plan/operation, reacquires capacity and performs its own
Execute before one fresh Finalize. Independent provider counts and domain rows
distinguish that progress from another Prepare, duplicate effect or invented
quiescence. The broader current regression then passes all 246 cases: 153
checkpoint, fifty-three SDK and forty control cases. All 2,365 Unit cases also
pass on the same frozen source with zero skips and zero-warning/error builds.
The twenty-eight focused cases are included in that broader result. This is a
fresh passing run, not a reinterpretation of the earlier failed 217-case receipt;
neither full Step 7 review round is closed by these tests.

The next bounded status slice validates unresolved ExecuteIntent under the same
exact normal identity and complete positive prior history. Running preserves the
unsuccessful-probe count; explicit failed probes increment once. The sixth failure
or a still-unresolved deadline/grace boundary quarantines without inventing an
outcome, releasing occupancy or admitting another normal attempt. Private lease
shortening and paced parking remain atomic behind the existing expiry-only wake
guard. Actual returned generation, counter, schedule and quarantine reason bind
the separate lost-COMMIT history probe.

Thirty-five focused PostgreSQL cases pass with zero skips: twenty-two new store
cases and thirteen existing filter matches. They cover the six-probe boundary,
Running between failures, retained occupancy, known-fact and stale-owner refusal,
actual deadline/grace, request/response COMMIT loss, changed ownership and an
injected final-park rollback. A fault followed by a repeatable outcome preserves
the count through Pending; only the next real counted admission resets it.
The initial fixture attempted to move an unowned probe schedule and was correctly
refused by the existing guard. Its failed receipt remains separate; the corrected
tests wait for the actual due time and leave that production guard intact.
Independent source/artifact inspection closes this store slice only. Native
timeout/report/callback qualification and durable quarantine-health observation
are still in progress; neither is claimed by these store cases.

The subsequent native regression executes 275 PostgreSQL cases: 273 pass and
two fail, with zero skips. Both failures record the only Reconcile timer callback
arriving before its owned monotonic deadline, by 1.288 ms and 1.200 ms. Discarding
that callback loses the future wake. A preliminary 1 ms tolerance therefore
does not qualify this path; its failed receipt is preserved separately. All
2,377 Unit cases passed at that same source, demonstrating the missing combined
timing coverage rather than disproving the native failures.

The correction restores actual elapsed-time checks and gives an early wake one
supplementary timer and one task under the existing invocation. That observer
alone rearms and asynchronously disposes its timer; stage changes and closure
signal it without calling timer operations under the state lock. Actual observer,
timer and cancellation cleanup precede private-source release. Twenty-three
focused Unit cases pass, including repeated early wakes, successor changes,
held timer construction/change/disposal and refused/faulted timer operations.
An additional nanosecond-clock control rejects premature expiry caused by
TimeSpan conversion. The current source then passes all 2,385 Unit cases and
all 275 PostgreSQL cases: 175 checkpoint, sixty SDK and forty control cases,
with zero skips and zero-warning/error builds. The actual late-report tests
assert preserved state and original job/attempt/operation identity. The earlier
failed receipt remains separate. This adds no database connection, executable
authority or full Step 7 review credit; it does not qualify the later durable
health slice or remaining terminal/finalization policies.

Durable quarantine health now uses a forward partial index and one status-only
read on the existing serial control lane. Acknowledged read rollback and actual
cleanup precede cache publication. Failed refresh retains the last good value
with an independent fault; unknown or stale samples cannot report healthy.
Snapshot cadence remains valid even when it exceeds the separate progress bound.
Health getters issue no SQL and do not become admission gates.

All 2,391 Unit cases pass, including six new memory/freshness controls. Eleven
focused PostgreSQL cases also pass with zero skips and zero-warning/error builds:
six control observations, three schema/canonical/reversal cases and two genuine
native quarantine flows. The latter waits until quarantine is the only health
fault; a fresh host, source, gate and runtime start with no cached sample and
independently observe the retained quarantine. They preserve occupancy and the
single normal admission without another Execute. The normal planner uses the
partial index against six thousand future rows; dropping it makes the same
detector expose the backlog scan. These focused results close no full review round.

The subsequent control/SDK/facade/storage regression executes 332 cases: 328
pass and four fail, with zero skips. Two historical recovery-migration tests
incorrectly include a later index migration in their refused-downgrade inventory;
EF commits each successful migration separately. They are corrected to qualify
the recovery migration at its own boundary without weakening the full inventory
comparison. One retained-outcome recovery case remains FinalizePending at a
thirty-second assertion despite its sixty-second repair setting; its original
cause is not established by that receipt. A separate facade case passes actual
cleanup and zero retained-owner assertions, then times out awaiting a follow-on
job. Controlled recovery-cleanup coverage and follow-on scheduling diagnostics
are in progress; the four failures are not credited as passing qualification.

The test-only correction run then passes nine PostgreSQL cases with zero skips
and a zero-warning/error build. It covers both historical migration modes, both
native recovery modes, both original held-Outcome cases, a new genuine stored
recovery selection with held real cleanup and same-generation repair, and both
auxiliary-cleanup modes. Production is unchanged from the preceding 332-case
epoch. The diagnostic-only facade rerun does not establish the original
follow-on timeout's cause; a source-reachable test-fixture collision between
the old job's legitimate retry and the new job is being tested separately.

The controlled old-retry-before-new-job experiment fails with the original
call-count mapping. Exact job-id mapping alone still fails: the two fixture
UUIDv7 ids also share the timestamp-only twelve-character slug prefix. Actual
handler diagnostics show the new job's `business_rule_violation`, independently
of clean source/owner release. The corrected fixture maps resources by actual
`JobTenantContext.JobId` and uses the full UUID in each slug. Two distinct ids
are deliberately generated at the same timestamp; the old retry commits first
without consuming the independent job's resource, and the new job completes
in one admission. Both clean and dirty auxiliary-cleanup cases pass with zero
skips and a zero-warning/error build. These constructed controls explain and
remove both fixture collision classes, without retrospectively assigning the
earlier receipt a cause its saved observations did not capture.

The no-plan terminal foundation now persists an exact finite request before
app NoStart, reservation release or terminal audit. Its fresh locked read checks
every normal admission and positive prior plan-free NoStart/release; committed
plans, effects and finalization are excluded. Its dedicated lost-COMMIT probe
binds the requested reason and immutable admission identity, preserving physical
ACK separately from history confirmation. Actual permanent Prepare refusal at
one/six, sixth recovery exhaustion, request/response loss, committed-plan and
expired-owner controls pass: seven PostgreSQL cases and all 2,391 Unit cases,
zero skips and zero-warning/error builds. This is the request/history primitive;
terminal-purpose app cleanup, stronger terminal storage guards, native routing
and final terminal audit integration remain pending and are not activated here.

The terminal-purpose cleanup foundation adds a fresh, immutable
`TerminalNoStart` recovery receipt. It grants only app-role NoStart and exact
reservation release; it cannot enter Prepare, Execute, Reconcile, ordinary
renewal or finalization. The app reads the actual causal envelope without
constructing module parameters or requiring an Active definition, then checks
complete normal history under the row lock. NoStart and `never_started` release
commit atomically. A purpose/reason-bound history probe preserves the original
physical ACK separately. All 2,396 Unit cases and thirteen focused PostgreSQL
cases pass with zero skips and zero-warning/error builds, including first/sixth
admissions, absent current definitions, acquisition/checkpoint COMMIT request
and response loss, and a reservation CHECK refusal after the marker UPDATE.
The initial failed fixture assertions remain separate receipts; the corrected
Unit descriptor preserves the production probe's strict phase/reason check.
The broader checkpoint/recovery regression then passes all 196 PostgreSQL cases
with zero skips, including those thirteen focused cases. This foundation does
not activate the native terminal lane, terminal audit or stronger terminal
storage guards.

The forward terminal-history migration freezes a non-NULL request reason and
requires complete positive no-plan history before the first dead-letter write.
It preserves the existing app NoStart/release guards, grants and four-role
boundary. Its initial eighteen-case run passes twelve and fails six: five
expose an ungranted payload-column read in the new guard, and one exposes a
dirty-control fixture that removed existing owner-clearing housekeeping. The
correction uses only permitted outcome metadata and restores the actual prior
function for dirty controls. The corrected build has zero warnings/errors and
all 217 PostgreSQL cases pass with zero skips, including canonical DDL parity,
exact prior-function Down/reapply, first/sixth actual NoStart, malformed prior
history, immutable reason and unadmitted retirement controls. A bounded reviewer
verifies both fixes and finds no further defect; this is no full review-round
credit and does not activate the terminal worker or its audit path.

#### Native terminal and finalizer policy qualification (2026-10-17)

The existing terminal lane now owns actual app NoStart cleanup independently of
ordinary terminal SQL. Its main/auxiliary application-source pair raises the
current application-pool floor to 24 and maximum supported floor to 120. Earlier
22/118 receipts above retain their historical meaning. A speculative empty
cleanup lookup does not increment pending outcomes. Stop joins the actual task,
scope and callback; an expired lease never substitutes for cleanup evidence.

The prepared terminal path retains plan, operation and selected outcome metadata.
Its existing witness has a prepared 40-column returned projection alongside the
unchanged NoPlan 13-column and app 37-column projections. A private-constructed
24-column prepared acquisition carries the exact additional metadata. The
forward migration requires positive complete history before stamping incomplete
business finalization; its Down restores the exact prior transition function.
Audit failure rolls back both terminal completion and the incomplete flag.

Actual native tests verify permanent Prepare refusal, sixth NoPlan recovery and
an already-recorded NoStart, including real cleanup-lease expiry and exactly one
MUST audit. Native Finalize tests roll back a nested business write, then admit a
fresh Finalize without rerunning the provider. PostgreSQL transaction identity
is captured while live: Npgsql can recycle its transaction object, so object
reference inequality was an invalid initial test premise. The corrected first
qualification passes sixteen PostgreSQL cases with zero skips.

A later execution deadline could discard an already-classified, positively
cleaned Permanent Prepare or Finalize result. Both now retain that actual result
in the existing bounded control queue; status accounting can outlive execution
permission without reopening app/provider entry. Native timer-disposal barriers
verify this window, host-stop refusal and actual handler cancellation returning
RetainOwnership. Unknown status COMMIT permits only exact historical probes
until rollback is established. Changed ownership ends the old authority without
claiming its COMMIT succeeded. Retained terminal and finalizer history dispatch
precedes generic probe interpretation, including copied-boundary refusals.

The expanded PostgreSQL qualification passes 214 of 216 cases; two planted-history
fixtures incorrectly ask the restored transition guard to accept damaged history
while expiring scheduling ownership. Correcting only that setup makes both pass.
The subsequent 42-case run passes 41; its remaining new foreign-owner fixture
incorrectly moves an already-expired lease forward. The corrected final focused
run passes fourteen cases with zero skips, including that refusal and a selected
external failure followed by six real Finalize admissions, all five backoffs and
one honest terminal MUST. Explicit fixture eligibility avoids waiting over an
hour; these are not six native delayed wakeups. The production source also has
2,396 passing Unit cases. All qualifying Release CI builds have zero warnings
or errors; initial migration/import build mistakes remain separate receipts.

The first complete architecture run passes 304 of 330 cases and fails 26. Exact
caller/signature/owner guards require alignment with the new external paths;
their nonempty subjects and planted violations remain required. This is a
recorded failing self-check, not completed architecture qualification or either
full review round.

Uninvokable cleanup and request routing are implemented without requiring an
Active definition or reconstructing module parameters. The candidate selector
proves positive cleanup history before its bounded page; unresolved earlier
rows cannot consume that page. Historical request probes retain their original
identity, compare selected timestamps independently of session time zone and
reject mixed specialized histories. A forward migration extends only the
positive NoPlan terminal reasons; prepared finalization guards stay unchanged.

The first foundation builds with zero warnings/errors and passes twenty-eight
existing PostgreSQL terminal-history cases. The subsequent 257-source checkpoint
passes all twenty-one dedicated uninvokable/invalid-envelope cases and the real
native application-source contention case. Its 272-case PostgreSQL run passes
271 cases and fails one index-name assertion: the normal planner legitimately
chooses the newly added matching retirement index for a pending query. All four
actual control-query plan cases, canonical DDL parity and full schema
rollback/reapplication pass. The assertion correction permits only that known
pending/retirement pair, retaining the cutoff, scan-bound and removed-index
controls. Its focused correction run passes all thirty-two PostgreSQL cases,
including the twenty-one uninvokable cases, all four actual control-query plans,
the five ordinary eligibility/index-removal cases and canonical/full-rollback
parity.

At that same source checkpoint, Unit passes 2,396 cases, Contract four and
Docker-free HTTP integration 176, all without skips. Architecture qualification
runs all 333 cases in two complementary groups: 326 pass and seven fail. The
failures expose stale exact-owner expectations, missing recovery-minter census
coverage, a Cecil mutation-restoration error, a composed table projection and
the published method count. All 1,074 external orchestration dirty/restoration
controls pass. The first correction run passes five architecture cases, including
the exact owner/minter baselines, terminal mutation controls, literal-SQL guard
and corrected count. Expanded minter mutation qualification then passes; the
owner mutation test exposes a further missing multiplicity check between the
two identical external budget projections. Requiring both exact occurrences
closes that false-pass control without removing either mutation.

The next correction build has zero warnings/errors and Unit again passes all
2,396 cases. Both orchestration facts pass in 4m53s, including all 1,074 external
dirty/restoration controls. The authority and owner group passes three cases in
6m16s, including the corrected budget mutations. Detector-local target-name and
identity indexes preserve fresh baseline/dirty/restored scans; detached targets
remain explicit offenders. These durations are receipts, not a controlled speedup
measurement or evidence that the complete CI backend job fits twenty minutes.
Step 9 retains that complete-job qualification. The first two helper analyzer
warnings and all seven original architecture failures are closed by focused
corrections; the original failed full run remains a separate receipt.

Commit preparation identifies whitespace/encoding-only corrections in fifty
changed files. They are applied after all running tests stop; final format/build
and representative post-format checks then pass. The complete solution verifies
format without changing any of its 1,268 files, and Release builds with zero
warnings/errors. Post-format Unit passes 2,396 cases, Contract four, architecture
seven and PostgreSQL thirty-three, including the native application-source
contention case. The normal nonempty/zero-skip TRX guard verifies all four test
assemblies. The 258-source SHA-256/mode manifest closes at
`b9187ae8e90976f7b974171cb1563c5b3a4df57e070809f845f3d040678600be`.
No complete current-head suite, PR/CI success or full review-round credit is
claimed. The changed-document audit passes 1,377 relative links and 423 fragments;
all seven ADR parser controls pass.

The remaining Step 7 checkpoints are explicit:

| Checkpoint | Current state |
|------------|---------------|
| External stages, durable policy, reconciliation and native terminal/finalizer paths | Implemented; earlier focused receipts above record their source-specific qualification |
| Uninvokable retirement/invalid-envelope cleanup and candidate progress | Implemented; twenty-one dedicated cases, actual retirement-index seek and corrected alternate-index assertions pass |
| Native application-source contention | Genuine contention regression passes with the provider task retained and an unrelated transactional job completing at the application-source floor |
| Complete exact census and relevant backend regression | Focused implementation qualification and correction checks pass; full packet regression/CI remain Step 9 |
| Step commit and two fresh full review rounds | Implementation committed as `6e90a52f`; both full review rounds and verified corrections complete; the round-2 correction and closeout share this commit |

Both full Step 7 reviews and their verified corrections are complete.
All nineteen packet obligations remain Registered; no
production business definition is added. Steps 8–9 and real PR/CI closeout remain pending.

#### Step 7 full review round 1 (2026-10-17)

Implementation commit `6e90a52f` passes normal hooks and leaves a clean tree.
Three fresh independent reviewers inspect the complete Step 7 range from
`0ea50b15`: GPT-6-astra at xhigh for runtime/authority, and two GPT-6.1-sol
reviewers at xhigh for storage/isolation/audit and proofs/composition/corpus.
These are full source reviews, distinct from the earlier slice preflights;
reviewers run no toolchain and supply no additional runtime or CI receipts.

The unique findings are external capacity deferral, the terminal NoStart
candidate cutoff, and required provider-causality qualification. The runtime
review independently corroborates the capacity and causality findings, with
no additional verified runtime defect. No new tenant/org isolation, privilege,
policy, release or terminal-audit atomicity defect is established.

External normal admission correctly refuses a full capacity group without
inserting an attempt or reservation. Its shared control disposition still
accepts only the transactional profile, silently leaving the external routing
lease. A real app-admission/native-control regression fails the unchanged
implementation at that retained routing owner. The correction admits the two
ordinary profiles through the same exact pending/routing/token/generation/live
lease fences; finalizer admission remains separate. The corrected real-path
case passes, including the recorded five-second wait, genuine provider/Outcome
release, new admission before the old lease expires, and stale-token refusal.
The twenty-one related PostgreSQL cases and all 2,396 Unit cases also pass with
zero skips and a zero-warning/error Release build. The original red receipt
remains separate; these focused checks are not a complete packet rerun.
A separate source inspection finds no actionable issue in this correction;
it supplies no second-full-review or complete native handoff credit.

The terminal NoStart candidate query used a volatile cutoff where selection
requires a stable statement timestamp. A real migrated-schema regression seeds
6,000 future terminal audit-backoff subjects with guarded NoStart/release history
and 6,000 unrelated occupied subjects. The original query fails its stable-range
assertion and records 36,000 loop-weighted plan-row visits, not distinct rows.
Stable cutoffs alone still fail the one-due-page work bound: a flattened
reservation EXISTS scans unrelated reservations. A local zero-offset fence keeps
that probe correlated to each due candidate. Full positive history remains before
LIMIT, and fresh post-lock authority checks remain unchanged.

The exact corrected query passes both idle/one-due cardinalities, the stable
index condition and fewer than 256 loop-weighted visits. Changing only its
available-after cutoff back to the volatile clock exposes the large idle scan.
All thirty-eight selected PostgreSQL cases, including existing control-query
plans and terminal/history controls, pass with zero skips; three architecture
checks pass and Release builds have zero warnings/errors. The first stable run's
integer-only EXPLAIN JSON parser failure is retained separately and corrected to
accept PostgreSQL's numeric row counts. The actual stable-only failures and their
plan remain separate receipts; no measured timeout, forced planner option or
new index/grant is claimed. Independent correction inspection finds no actionable
issue and supplies no second-full-review credit.
The existing provider tests preselected cancellation policy; they did not prove
an actual runtime cancellation becoming a durable Cancelled report and surviving
fresh-host Reconcile. Three new native cases now pass. An actually accepted
Running operation honors the private runtime token and commits Cancelled with
positive provider fencing in its independent database. Its stopped original
owner joins without writing Outcome or releasing occupancy. A fresh host/source/
Run with no original probe reconciles the same job/operation/attempt/deadline,
checkpoints its exact policy/evidence, releases capacity and commits domain
finalization with both MUST audits.

The companion permanently fails and durably closes its provider fence before
stop, while holding only the response across stop; fresh recovery preserves that
independent meaning. A third, explicitly dirty adapter changes only a genuine
Cancelled Reconcile report to Failed/Permanent. It leaves the provider journal
unchanged, actually reaches the wrong persisted result, and is rejected by the
same semantic assertion used by the clean companions. This proves adapter
qualification, not generic runtime certification of external causality.

Independent source inspection finds no actionable issue in the correction design.
The three actual native cases, all 2,396 Unit cases and four Contract cases pass
with zero skips and a zero-warning/error Release build. The initial three analyzer
warnings are corrected by explicitly awaiting provider-owned cleanup independently
of its already-cancelled input. The remaining seventy-one provider/SDK cases
also pass in 8m46s. Together the two disjoint filters execute all seventy-four
cases in that class, including genuine native, lost-reply/ACK, recovery, retry,
finalizer, cancellation/cleanup and source-contention controls. Four architecture
checks pass. The normal runner guard verifies nonempty execution and zero skips
in all four assemblies; its initial suffix-only invocation is retained as an
invocation failure, separately from the passing full-assembly-name invocation.

Format verification changes zero files. The seven-document audit passes all
1,377 relative links and 423 fragments. The final 261-source SHA-256/mode manifest
closes at `b1404b39d54434ba1fa3233bbe9fb742cc856d828139406d61a6ed11f9df005c`;
the corrected runner invocation has its own receipt without rewriting that manifest.
Capacity and candidate-scan fixes are committed as `ef3cf7b4` and `1ef18e64`.
This correction closes the three verified first-round findings; the second fresh
full review remains pending. No complete current-head backend/CI run or packet
obligation promotion is claimed; Steps 8–9 and the full closure gate remain open.

#### Step 7 full review round 2 (2026-10-17)

Three fresh independent reviewers inspect the complete Step 7 range
`0ea50b15..d0eb7596`: GPT-6-astra xhigh covers runtime/authority and a second
GPT-6-astra xhigh covers storage/security/audit; GPT-6.1-sol xhigh covers
proofs/composition/corpus. Runtime and storage report no actionable findings.
The proof review identifies one required coverage gap and one minor live-standard
correction. These are full reviews, distinct from earlier narrow inspections.

The genuine provider recovery tests did not exercise Failed/TransientNotRepeatable
or Cancelled/TransientRepeatable through Reconcile. The existing recovery theory
now retains its three cases and adds those two; no Fact/Theory method or catalogue
count is added. Original Execute returns a completed report without an exception,
but its application Outcome is deliberately absent. Fresh native recovery reads
the same independent provider attempt before any later execution.

An actual acknowledged recovered Outcome holds its cleanup before disposition.
App-role assertions bind state, policy, operation, fingerprint, evidence and atomic
quiescent release to the original provider journal. Repeatable cancellation has
no premature failure selection and later spends a distinct normal admission on
the same operation. Nonrepeatable failure commits its ordinary failure finalizer
and exact `external_retry_forbidden` MUST audit without another Execute. Both
new cases and all seven existing retry/recovery/ACK cases pass: nine cases, zero
failures/skips, with a zero-warning/error Release build.

A temporary Reconcile-only adapter mutation maps the two policies to different
valid policies while leaving provider storage unchanged. Exactly the two new
cases fail at the same stored-policy comparison; the three existing recovery
controls pass. This is an executed adapter-protocol negative control, not runtime
certification of provider truth. The exact fixture bytes and mode are restored
and independently verified against the clean 261-source snapshot. The restored
Release build has zero warnings/errors; its five-case native rerun passes with
zero failures/skips in 1m33s. The restored-source manifest closes at
`33ba204ead511e91fa7f0ad7c63394e07e5fa894874b5d0df14e5290f2ea46db`.

The live Infrastructure standard now describes the actual stage-specific wait
switch and correct completed Step 6 review status. Independent narrow inspection
finds no remaining issue in the correction or the actual clean/dirty receipts.
The initial clean source manifest closes at
`3654d96f7cb27241deeb049fbe05e8476211bff20ba54828778617ff220ec6e6`;
the separate expected-failure mutant receipt closes at
`2d6ace0de47a4babd374a605e2e6fd5cc11cd7865f5f19039d63320b8159312b`.

A fresh complete Architecture run on the frozen `d0eb7596` source passes all
333 cases with zero failures/skips in 30m41s; its 261-source manifest closes at
`bc2936a69e07c77c23afb684d7f225f9b192199f0f9e7bbd6f825bdd4200c9ad`.
This includes the earlier seven corrected controls and preserves their original
failing receipts. It precedes the test-only policy extension; it is not a complete
current-head backend/CI receipt. Step 9 still owns CI-duration qualification and
full packet regression. All nineteen obligations remain Registered. The round-2
correction commit closes Step 7 after both full reviews and verified corrections;
Step 8 follows under the already Accepted plan. No new maintainer decision is pending.

### P02b-4 Step 8 — durable recurrence and restricted fan-out (2026-10-18)

**Locally complete after both full review rounds and verified corrections.**
The [round-2 closeout](#step-8-full-review-round-2-2026-10-18) owns final evidence.
Step 7 closes at `69405b16` after both full
independent review rounds and verified corrections. Accepted ADR-0047 authorizes
this step on `development`; no new decision is pending.

The implementation is organized into three closure units within the unchanged
approved Step 8. They do not replace the implementation commit and two fresh full
review rounds required before Step 9:

- Frozen monotonic manifests, UTC slots, coalescing and bounded lifecycle pages.
- Actual-row recipient admission, scope recheck and atomic child/recipient receipt.
- Owned runtime/tick integration, fairness, capacity and complete fault proofs.

The first foundation adds idempotent manifest registration and one locked
materialization quantum. Two actual sources converge; obsolete or conflicting
revisions cannot overwrite the accepted manifest. Historical slots retain their
stored target/policy; a newer manifest cannot be materialized by an older binary.
CoalesceLatest atomically advances to the slot after the latest due occurrence.
Future slots enter the index only when due. Pending/unknown targets defer without
advancing and yield to healthy work. A denied occurrence INSERT rolls back both
occurrence and manifest progress; restoring the narrow grant permits a clean retry.

The focused foundation Release build has zero warnings/errors. Thirteen existing
recurrence Unit cases pass; the extended PostgreSQL class passes eight cases and
the production authority census passes one, all with zero failures/skips. Its
266-source hash/mode receipt closes at
`09aa9ca0275c531b65d11851a3674788762a8571c499a772e5140fcb62b765d1`.
An initial missing test-helper compilation failure remains a separate receipt;
no tests are credited to that failed build. These focused checks do not qualify
the remaining fan-out/runtime surface or either full Step 8 review round. No
production recurrence or business definition is added; nineteen obligations
remain Registered.

#### Lifecycle pages and foundation preflight

Tenancy declares the status meanings as closed metadata. Its additive migration
grants exactly eight SELECT columns; real dispatcher connections reject names,
wider reads and tenancy writes. Raw tenant/organization pages bound examined keys
before eligibility filtering. Frozen upper keys and recipient INSERTs advance in
one transaction; denied INSERT controls leave neither bounds nor progress behind.
The actual tenant query seeks past a 6,000-row ineligible prefix in a 12,000-row
fixture without rescanning it. This is a raw-key eligibility sweep, not an MVCC
snapshot cohort. No organization query-plan result is inferred from that test.

Two planted same-revision manifest conflicts initially fail at the semantic
materialization assertions. Both materializers now require the exact local hash
when the revisions match, while preserving supported historical targets under a
newer local manifest. Both cases pass in the restored clean run.

Two independent foundation preflights inspect runtime/isolation and proof/corpus
surfaces. Runtime has no verified finding. Proof review identifies that executing
the canonical grant block after applying the migration cannot detect an omitted
grant. The corrected test starts from a verified empty lifecycle ACL, proves the
same exact-budget assertion rejects a missing required column, then runs the
unaltered canonical block from an empty ACL and verifies its complete budget.
These are bounded preflights, not either full Step 8 review round.

The 275-source pre-correction receipt passes 35 PostgreSQL cases and both the
positive and planted-violation authority cases, with zero failures/skips. It closes
at `43873ad1a6b7c95bb578b4da04d2db8b7a6818e12be89236a50c949a977863e4`.
After the proof correction and formatting, the Release build has zero warnings/
errors; 35 PostgreSQL cases, all 2,396 Unit cases and the positive authority case
pass with zero failures/skips. That 275-source receipt closes at
`04894512bcfe8c444a82a5f16b956dd2c8e217d16146f4b88f860f842aa65b6c`.
Earlier compilation failures and the two meaningful conflict failures remain
separate receipts. These checks do not claim full packet regression or CI success.
Restricted child admission and owned runtime/tick integration remain within this
same Step 8, before its implementation/review closeout.

#### Recipient acquisition continuation

Foundation commit `69c3998a` preserves the preceding qualification and bounded
preflight correction. Normal hooks leave all 275 validated source hashes/modes
unchanged. Restricted recipient acquisition is the next unit, not a new step.

The actual locked recipient now yields an opaque single-use claim containing its
stored occurrence, route, manifest, target and fresh acquisition. The compatible
registered manifest supplies its declaration Type; no stored CLR name is resolved.
Publication requires positive claim COMMIT acknowledgement, actual cleanup and a
conservative remaining lease interval. Pending, unknown and same-revision
conflicting binaries defer without publishing authority. Two independent sources
cannot issue one live route twice; a competing application row lock is skipped.
Acquisition itself creates no child or business execution.

The 278-source qualification passes a zero-warning/error Release build, 41
PostgreSQL cases, all 2,396 Unit cases and the positive authority case, with zero
failures/skips. Its source/mode/artifact receipt closes at
`cd7961e2b5873fa94b326caedacf265decffaf81cb7552591718fbe927c3eb52`.
An earlier test assertion compilation error remains its own failed-build receipt;
no tests are credited to it. App-role child submission, its completion gate and
acknowledgement-loss behavior remain pending in this same Step 8. Full Step 8
reviews and runtime/capacity enforcement remain pending.

The narrow acquisition preflight finds two additional source issues: claim
ordering includes a column absent from the pending index, and declaration-kind
validation rejects metadata accepted at registration. The corrected query orders
by `(available_after, id)`, matching the canonical index. A real 3,000-recipient
zero-jitter backlog proves the actual query uses that index without sorting and
examines a bounded quantum. Restoring the obsolete order in the same query makes
the same detector fail and consumes the backlog before LIMIT. Interface, struct
and open-generic manifest declarations pass the actual registration-to-claim
flow without constructing any feature service; owning-assembly validation remains.

After these corrections, the Release build has zero warnings/errors; 45
PostgreSQL cases, all 2,396 Unit cases and the positive authority case pass with
zero failures/skips. The 278-source receipt closes at
`d12a0f5fdaebacf0cfb7e8d93e0df8b65cf32a7e9e107514b39e37add1e8090f`.
Independent correction verification checks the source and hashed artifacts and
closes both findings without a new verified issue. This remains bounded
preflight/correction evidence, not either full Step 8 review round.

#### App-role recipient submission continuation

Acquisition commit `2059a7ee` preserves the preceding evidence. The closed
recipient executor now installs the actual tenant/organization route without an
invented child or parent identity and sends one exact framework request through
the existing coordinator. Its participant binds the original source owner,
physical transaction and positive tenant announcement. The app-role owner locks
the recipient, evaluates a fresh database clock and rechecks current lifecycle
eligibility before creating one fixed occurrence-input child.

The common scheduler INSERT preserves ordinary producer checks and records
produced work only after successful SQL; failure marks the existing owner
rollback-only and rethrows. After owner drain and MUST audit, the participant
checks the full immutable route, original token and live lease and writes the
reciprocal receipt. A conservative monotonic budget covers SQL and cleanup until
the final commit gate. Only the existing coordinator records commit ACK. The
executor retains its source, scope and cancellation ownership through actual
cleanup; it constructs neither the target request nor its handler.

Real PostgreSQL cases cover tenant and organization success, lifecycle refusal,
single-use replay, a closed gate and independent child-INSERT/receipt-UPDATE
failures. Both writes commit together or neither remains. Construction controls
exercise the actual registered target and handler separately and prove the same
no-construction assertion rejects each. Exact production censuses enroll the new
framework/context/source/owner path and retain the original admission restrictions.

The 287-source qualification passes a zero-warning/error Release build, 75
PostgreSQL cases, all 2,403 Unit cases and twelve focused Architecture cases, with
zero failures/skips. Its source/mode/artifact receipt closes at
`464b9554dac3d4b0060b8ca7021a16127741abd8b8d8518948dadf2da3fe18f2`.
Narrow proof review identifies five planted field/constructor mutations that the
first detector incorrectly accepts. All five fail against that original detector;
the corrected detector checks full writer identity, opcode and exact private
constructor/flag shape, and the same controls pass in the qualification above.
Earlier build, SQL and detector failures remain separate receipts.

Final guard additions pin the recipient executor's exact internal signature and
currently empty production caller set. The shared INSERT proof binds its actual
SQL invocation and await acknowledgement to the same rollback-only/rethrow catch;
produced work must be invoked after acknowledgement and outside failure handlers.
Ten targeted controls move, remove, delegate or detach these actual instructions.
The earlier detector's incorrect compiled-signature assumptions remain a failed
qualification, not a runtime finding. Independent correction inspection verifies
both catch binding and invocation sensitivity without claiming test execution.

Only that Architecture test file changes after the preceding 287-source receipt.
Its final Release build has zero warnings/errors; all seventeen recipient
Architecture cases and two real auxiliary-cleanup regressions pass with zero
failures/skips. Full formatter verification reports no changes. This source/mode/
artifact receipt closes at
`65ba343bf9da98134a09e0e6297364d38daf752d57ef0aab2a03d3c2e6b3bd7a`.
These are local, source-specific receipts, not current-head full backend or CI
qualification.

At this continuation's commit, historical acknowledgement-loss confirmation and
confined skip/defer remain pending; the following record owns their delivery.
Additional fault controls and native runtime/fairness/capacity integration remain
pending. This is not complete Step 8 delivery or a full review round.
No production recurrence or business definition is added. All nineteen
obligations remain Registered; both fresh full Step 8 reviews precede Step 9.

#### Recipient history and confined disposition

App-role submission commit `a99e6a8a` leaves all validated source hashes/modes
unchanged after normal hooks. A new confined operation now requires the original
executor's positive cleanup before opening another physical source. Its original
result has immutable authority fields and a constructor restricted to that
executor. A locked-row observation checks the full stored route and reciprocal
child before the old token: the accepted child wins even after token clearing or
a later acquisition, without creating another identity or inventing the original
COMMIT acknowledgement. A skipped lock is unavailable, never proof of absence.

Without a final receipt, only the original DB-live token permits disposition.
Current lifecycle is rechecked; a deleted/ineligible route is skipped, while a
restored or otherwise eligible route receives bounded deferral. The final UPDATE
repeats the complete route/token/time fence. No app grant or business attempt is
added; this status operation publishes only after its own ACK and cleanup.

Nine new PostgreSQL cases use real server-COMMIT response loss and unforwarded
COMMIT requests, counted identity creation, actual row locks, actual replacement
acquisition, lifecycle restoration and a throwing scoped cleanup dependency.
They distinguish a stored child observation from the original ACK. The initial
qualification retains one fixture failure at the forbidden live-token rewrite
and the detector's compiled-string mismatch; neither is a runtime finding.
The fixture now expires the lease and reacquires through the actual store.

The final 289-source Release build has zero warnings/errors; 44 PostgreSQL cases
and 22 focused Architecture cases pass with zero failures/skips. All 2,403 Unit
cases pass before the two test-only corrections. Formatter verification reports
no changes. The final source/mode/artifact receipt closes at
`cbcc6cff06e17f089c4923e0819703e118454e8d68eed103288e896731e979d8`.
Narrow independent source inspection finds no unresolved runtime/test-evidence
issue and claims neither test execution nor full Step 8 review credit.
Remaining admission/fault controls and native integration still precede both
full Step 8 review rounds and Step 9. Nineteen obligations remain Registered.

#### Recipient admission fault qualification

History commit `f08f8bf1` preserves the preceding source qualification. Eleven
additional PostgreSQL cases exercise corrupted actual claim facts, context
replacement during identity creation, lease expiry after an observed row-lock
wait, and stop after the actual receipt UPDATE but before commit admission.
The tests check actual identity creation, SQL effects and rollback together;
no production seam or business definition is added.

Narrow independent inspection identifies that the first context-callback test
cannot distinguish refusal before INSERT from rollback afterward. The corrected
paired test observes an actual server-side INSERT notice. Removing only the
post-identity context guard makes the corrupt case fail that exact assertion
while its healthy counterpart passes. The original production bytes and mode
are restored and verified against HEAD before rebuilding. This deliberately
failing mutation remains a separate receipt, not a production failure.
Independent correction inspection verifies the same assertion's failure, exact
source restoration and passing paired cases; no further issue is identified.

The restored 290-source Release build has zero warnings/errors; all 55 cases in
the recurrence PostgreSQL class pass with zero failures/skips. Formatter
verification reports no changes. The source/mode/artifact receipt closes at
`022523338fa6f8ee3c3384f2b5ce878bb31b099035e16d07df338e65f995b8d0`.
Only Integration test sources change after the preceding committed production
qualification; Unit and Architecture suites are not repeated or claimed here.
Native runtime, fairness and capacity integration remain pending before both
full Step 8 review rounds. This focused qualification does not close Step 8.

#### Native recurring-write serialization

Recipient fault qualification is committed as `35c3e0d5`; normal hooks preserve
its validated source hashes/modes. Actual stock recurring `Job` writes now lock
the exact unsuperseded revision inside the vendor transaction. The explicit
volatile trigger takes a fresh snapshot after earlier waits, and bootstrap checks
the installed volatility with its existing body/language/search-path/ACL contract.
A generated additive migration upgrades already-migrated stores and restores the
preceding exact body on Down. Canonical SQL remains in Standards 05.

Ten focused PostgreSQL cases use the real stock manager without starting workers.
They cover old-over-new refusal, an unchanged Job omission, retained older hints,
volatility-only drift, independent migration Up and canonical replacement, and
three actual lock interleavings. The same assertions reject the KEY SHARE,
missing-active-predicate and stale nonlocking-snapshot controls. No elapsed sleep
establishes those barriers; the tests observe actual blocking backend identities.

Narrow source review identifies that applying canonical SQL before Up would mask
an omitted upgrade body. The corrected test first upgrades the actual old body
through the migration alone, then independently tests canonical replacement.
No further concrete issue is identified. Earlier SQL-cast and test transaction
cleanup failures remain separate receipts; a subsequent build-only receipt
grants no test pass.

The final 297-source Release build has zero warnings/errors. The ten focused
cases and the broader 143-case PostgreSQL selection pass; the broader selection
includes those ten cases. All 2,404 Unit cases pass, with zero failures/skips.
Formatter verification reports no changes. The source/mode/artifact receipt,
including current SQL and canonical carriers, closes at
`aa38ded6b0042cba7a3521bbf02ada212b6cf09ae74c4fdde3ca5deac89aaa97`.
This is local qualification of the guard/storage unit, not full Step 8 review or
packet-wide CI. Owned runtime/tick integration, fairness, physical capacity and
their real fault proofs remain within Step 8. All nineteen obligations remain
Registered; both fresh full reviews still precede Step 9.

#### Runtime connection and stored quarantine qualification

Guard/storage commit `c4a93deb` precedes this runtime unit. The existing facade
now owns a recurrence child, one independent native manager flight and at most
four actual recipient application owners. Durable registration, slot, page,
claim and original-receipt history quanta rotate on one SQL lane. Native ticks
only coalesce a memory signal; they construct neither recipients nor feature
work. Exact recipient IDs remain excluded while their original task, cleanup or
receipt history is retained. Failed cleanup supplies no replacement authority.

The shared observation reads stored recurrence, occurrence and recipient
quarantine independently of the local registry, including unknown older keys.
Each table has a reversible partial presence index. Both observation values
require actual reader/command cleanup and acknowledged ROLLBACK. Failed refreshes
preserve the preceding value and make the observation unhealthy. API composition
supplies Tenancy's existing closed lifecycle declaration as metadata.

The named capacity inventory adds two privileged connections and four application
main/auxiliary pairs; stock worker and retained-invocation bounds do not increase.
Standards 05 owns the reconciled formulas. Real default/maximum app inventories,
explicit unrelated headroom and actual default/maximum native facade composition
exercise their own boundaries; this does not claim simultaneous saturation of
every runtime lane. Existing owner/caller manifests admit only the new concrete
recipient and recurrence callers, without widening source-acquisition authority.

The initial runtime build retains three analyzer failures as a separate receipt.
An intervening Unit run passes 2,401 cases and fails three old capacity premises;
after their correction, all 2,404 Unit cases pass. The first two native fan-out cases
reach completed children before their test-created ticks fail the transport's
ambient-culture guard. The corrected test uses the real native owner's invariant
culture boundary; that guard remains unchanged. A subsequent 311-source build has
zero warnings/errors, 2,408 passing Unit cases and 78 passing PostgreSQL cases.
Three new quarantine refresh cases fail because revoking a column grant cannot
remove the existing table-level SELECT grant. Their corrected premise first
proves actual `42501` after revoking the real grant, before awaiting health change.

The final 322-source Release build has zero warnings/errors; all 2,408 Unit cases,
twelve focused PostgreSQL cases and four focused Architecture cases pass with
zero failures/skips. The PostgreSQL selection covers each corrected cold-runtime
quarantine path, expired-lease exclusion, invalid exclusion sets, populated
recipient query plans, three-index Down/Up, actual facade topologies and actual
native transactional progress at the new app floor. Earlier passing cases include
both tenant/organization fan-out paths, three duplicate stock ticks per path,
reciprocal receipts and each control index's decisive 3,000-row removal control.
The final selection does not rerun or relabel that earlier broad selection.
Formatter verification reports no changes. Its source/mode/artifact receipt closes
at `8722a6b2799ba23d3cc1d8271c56dc9357b649a5b684160df8a4f8beabcc08f8`.

Two narrow source preflights identify the missing isolated quarantine/index proof
and the exact existing caller enrollments, addressed above. They execute no tests
and grant neither full Step 8 review round. Complete new-owner/tick capability
guards and retained-work, fairness and shutdown fault qualification remain before
both fresh full Step 8 reviews. Nineteen obligations remain Registered; Step 9 is
pending and no packet-wide regression or CI success is claimed here.

#### Actual native hold and parent-source pins

Runtime commit `c500bcba` precedes this test-only qualification. An actual Npgsql
diagnostic callback holds the stock recurring manager on its owned native thread.
Two registered keys still produce two completed durable children. This is a
bounded synthetic callback hold, not a network-fault reproduction or a minimum
connection-pool proof. The fixture has explicit unrelated connection headroom.

The original one-key assertion could pass with the hard native-flight guard
removed because the same-key cooldown independently prevented replacement. A
narrow source review identified that masking premise. Two keys now establish a
second eligible native opportunity; the test reads the actual published owner
under its lock and verifies that its original thread remains the sole owner.
The first strengthened run fails only because a string assertion selects the
wrong overload; its corrected explicit collection assertion preserves the premise.

Before testing source retention, all ordinary pools, shared maintenance, bridge,
parent control lanes and recurrence loop/cancellation relay positively join.
The held native invocation still prevents parent completion and both actual
source lifetimes from disposing. Releasing the callback permits actual SDK/thread
return, parent completion and positive disposal without cleanup faults. An
independent nested-finally join retains the original thread even when an
assertion or parent shutdown fails.

Two separate temporary source controls are decisive: removing only the hard
native-flight guard admits the second key and fails the original-owner assertion;
removing only the parent's recurrence completion await makes stop finish early
and fails the finite-stop assertion. Each control builds without warnings/errors,
fails the same test and is restored byte-for-byte with its original file mode.
No deliberate violation is committed.

The final restored 323-source Release build has zero warnings/errors. All 76
recurrence PostgreSQL cases pass with zero failures/skips in 43 seconds, including
the actual native hold, tenant/organization fan-out, native races, recipient
receipts/history and stored quarantine paths. The runner's nonempty/zero-skip
guard passes and scoped formatting reports no changes. The source receipt closes
at `c9331866654efc91c83769cb718398416c97084897f38547a0a18f9cd2253416`.
The narrow review supplies no full Step 8 review credit. Remaining recipient
retention/fairness and exact capability guards still precede both full rounds.

#### Actual recipient ownership, expiry and source cleanup

Following `dd600e88`, the actual runtime exercises six due recurrence keys with
four real recipient application tasks held before their handler acquires a row
lock. Each retains its original unit, source owner and published operation.
PostgreSQL's clock establishes natural expiry of all four 15-second leases;
the fixture neither rewrites those leases nor substitutes local timestamps.
Durable registration and fan-out continue while all four application slots remain
occupied, including their timed-out actual tasks.

The otherwise-eligible production selector independently ranks a retained original
first. Three handlers then return through their real closed-gate refusal,
rollback, cleanup and history resolution. Exact original-object eviction permits
a fresh operation for the same ID. Another recipient commits its reciprocal child
and completes while the remaining held row preserves its original token and
operation. Releasing that old attempt cannot attribute a later child's ACK to it.
All six children eventually complete through the actual runtime.

Two separate source controls fail the same semantic assertions: enlarging the
ceiling from four to five admits six wrapper entries, and removing only the SQL
retained-ID exclusion replaces the otherwise-first held token. The rank helper
does not assert the removed SQL spelling, and full capacity does not mask the
exclusion check. Both controls build cleanly and are restored with exact hashes
and file modes before final qualification.

Two further cases separate a physically held original cancellation callback from
an actual DI scope's successful asynchronous disposal. The callback case first
proves that the executor, scope and application-source cleanup have returned;
the cancellation relay alone remains held. The scope case instead observes a
positive child COMMIT ACK while actual scope disposal and executor return remain
pending. Every sibling, including any native registration, positively joins
before the original operation and both source pins are checked. Replacing the
all-owner cancellation join with an any-owner wait fails the callback case while
the independent scope case still passes. The original join is restored exactly.

Narrow reviews correct the exact three-history-owner eviction observation and
the registration fixture's inline-cancellation race. Registration is itself an
owned actual task with a finite setup wait. All barriers release before shutdown;
nested finally blocks independently join registration cleanup and the original
operation. These reviews grant no full Step 8 review credit.

The initial application-test build records two analyzer errors, corrected without
changing production policy. Earlier clean qualification passes the application
case and both source-pin cases. The final restored 325-source Release build has
zero warnings/errors; all 79 recurrence PostgreSQL cases pass with zero skips in
1m08s. The four focused Architecture cases initially expose the stale published
240-method count; five already-added Step 8 methods make the actual total 245.
After that documentation correction all four pass, including Docker-trait and
planted-trait checks, the non-skip guard and catalogue counts. The runner verifies
nonempty execution and zero skips; explicit TRX checks separately require zero
failures in both final runs. Scoped formatting reports no changes. The source,
mode and artifact receipt closes at
`c463834754105981e5babcad10d39cbf0529a6c8a4b9f3b0358ba0ae94ebec6a`.

New-owner/native-manager/tick/exclusion capability guards and their planted
controls still precede both fresh full Step 8 review rounds. Nineteen packet
obligations remain Registered, Step 9 remains pending, and this qualification
claims neither packet-wide regression nor CI success.

#### Recurrence core capability qualification

After `b72ce6ce`, a new nonempty production census closes the four concrete
recurrence owners' constructors, startup/cancellation callbacks and parent
completion capability. It pins exact declaring assembly, method signature,
visibility, caller, opcode and multiplicity. The internal sealed native client's
primary constructor has public IL visibility; the census records that actual
shape without changing production accessibility. Null-conditional native stop
and thread-exit calls likewise retain their exact compiled opcodes.

A narrow review finds that name/count checks alone could accept a foreign
cleanup target. Cleanup checks now require the exact trusted .NET 10 facade
identity, full signature, instance/static shape and opcode. Actual configured
awaiter identities preserve multiplicity, including both non-generic native
awaits. Attached foreign capability, Thread and nested awaiter controls must fail
the same detector; each restores the original operand before removing its
temporary in-memory type. No assembly file or production source is mutated.

Earlier guard runs record incorrect constructor/opcode premises separately;
mutation cases failing their clean baseline receive no dirty-control credit.
The final 326-source Release build has zero warnings/errors. Thirteen focused
Architecture cases pass in twelve seconds with zero skips: the baseline, eleven
clean/dirty/restored controls and the catalogue count. The two added companion
methods bring the published count to 247. Scoped formatter verification reports
no changes. Its source/mode/artifact receipt closes at
`8d62c2fd3956e1bc2d182ea1dcda0f1ff61e7e00f6a5388480e971942ad4c4d2`.

This qualifies the core capability slice, not semantic task-dataflow enforcement
or the complete recurrence census. Field/holder and publication ownership,
native SDK invocation, memory-only ticks and exclusion-list guards remain before
both full Step 8 review rounds. Nineteen packet obligations remain Registered;
Step 9 is pending. Neither narrow source review supplies full-round credit.

#### Recurrence ownership, hint and publication guard qualification

After `59a19e15`, the same two companion methods close the new owners' field
inventories, resource types, exact access sites and whole-production holders.
Actual async attributes identify permitted state-machine holders. Generic
resource identities are checked recursively; same-name foreign resources and
forwarding accessors are rejected. Each completion source retains its actual
concrete owner. Existing physical-source, privileged-entry and rollback caller
allowsets are unchanged.

Native registration uses the exact stock manager seam, two manifest arguments,
control queue, UTC minute hint and Relaxed misfire policy. The census includes
the stock V2 recurrence interface. The fourteen-method tick closure admits only
its enumerated memory calls/fields and pinned framework/vendor leaves; injected
scheduling, indirect calls, changed tick identity or early readiness reads fail.
This establishes a closed work surface, not an absolute latency guarantee or
general argument/control-flow proof.

Recipient selection retains its sole caller, bounded collection snapshot,
actual UUID-array parameter and retained-ID SQL exclusion. Source-order checks
pin publication before Start, cancellation join acknowledgement before source
disposal, and positive completion after actual cleanup/thread-exit observation.
The previously recorded actual PostgreSQL/retained-task controls supply the
separate causal lifecycle evidence; structural order alone is not its substitute.

Narrow independent inspections identify and verify corrections to the V2
interface census, resource/generic identities, cancellation join acknowledgement,
provider invocation identity and tick token. Earlier build/clean-premise errors
are retained separately. The initial 43-case run has one passing catalogue case
and 42 clean-baseline failures caused by an ambiguous completion-signal selector;
none receives dirty-control credit. The corrected selector distinguishes the
returned signal from positive owner completion.

The final 330-source Release build has zero warnings/errors. All fifty focused
Architecture cases pass in one minute ten seconds, with zero skips: the baseline,
48 clean/dirty/restored cases and the unchanged 247-method catalogue count.
Scoped formatter verification reports no changes; its workspace-load warning is
not reported as a build warning. The source/mode/artifact receipt closes at
`cc7fcc480cd9234aa9d80ef189d3a602bc32c5e10b884e5393cd797bd22c7b4a`.

This completes the new guard slice. Both fresh full Step 8 review rounds remain
before Step 9; these narrow inspections award neither round. All nineteen packet
obligations remain Registered. No packet-wide regression or CI success is claimed.

#### Step 8 full review round 1 (2026-10-18)

Three fresh independent reviewers inspected `69405b16..332e7a4a`: GPT-6-astra
xhigh covered runtime/lifecycle, GPT-6.1-sol xhigh covered persistence/authority,
and GPT-6.1-sol high covered tests/corpus. These are full Step 8 reviews, distinct
from the earlier narrow preflights. The reviewers ran no toolchain or database
checks; the execution receipts below belong to the root implementer.

Two Major findings and one Minor corpus correction were verified:

- A stop winning before the new child's native-client binding or Start caused a
  setup exception. The parent/facade could then retain a false cleanup failure
  indefinitely despite no issued recurrence work. Both child entry points now
  return neutrally for that stopped-before-setup state. Already-bound/started
  duplicate refusals and an open nonempty owner's missing-client refusal remain.
- PostgreSQL's finite timestamp range exceeds the CLR range. A schema-legal BC
  activation or year-10000 anchor could repeatedly fail decoding the first due
  row before its invalid-control branch, starving healthy work. Readers retain
  the actual locked locator before decoding and treat unrepresentable dates as
  absent facts, never fabricated valid values. After actual reader cleanup, the
  exact recurrence, occurrence or recipient is quarantined without issuing work.
  Only deterministic conversion exceptions are contained; IO/cancellation/server
  failures retain the existing owner/cleanup path. Higher-revision registration
  compares the prior activation in PostgreSQL instead of decoding it into CLR.
- The live catalogue introduction still described completed Step 7 qualification
  as pending. Both current pointers now agree with its completed record; Step 9
  enforcement remains pending and all nineteen obligations stay Registered.

The first four regressions ran against unchanged production code and failed at
all four intended assertions, not fixture setup: two bad-date rows repeatedly
failed without quarantine/healthy progress; two late child setup calls threw.
That test-only receipt is
`6613d4652759320ee077261bc6186efef597258d74d832431dc407d584d24299`.
With the five production corrections, the same four cases pass; the first
corrected receipt is
`e54a9f08143b829cac78911e3291a2a7dfafe374e0a9dbee2e7548a6cb94948f`.
Neither receipt is a current-head whole-packet regression or CI claim.

The expanded qualification adds eight timestamp cases across recurrence
materialization, occurrence paging, recipient acquisition and higher-revision
registration, all under the real confined-role grants and unmodified guards.
Each invalid selected row confirms ACK/cleanup, no authority/progress beyond
quarantine, and healthy sibling progress. BC scheduled routes require BC manifest
activation, so their absent-manifest branch does not independently qualify the
recipient's own-date checks. A future year-10000 incumbent activation returns
Conflict without inserting a revision; a BC incumbent permits the later valid
revision and exact supersession/hash.

Four lifecycle cases use actual owners and sources. The late setup assertions
inspect the actual null client/native fields, unstarted flag, empty recipients
and positively completed null-loop witness. A synchronous hold on the actual
child monitor orders real parent Start past its admission guard before facade
stop, then joins the original start task, parent/facade completion and both
sources. The open/duplicate controls preserve programmer-error refusals. No
synthetic owner/task/source replaces the real cleanup chain; the parent barrier
is qualified on corrected code, not described as an original-code execution.

The expanded Release build has zero warnings/errors. All 2,408 Unit, 91
recurrence PostgreSQL and 57 focused Architecture cases pass with zero
failures/skips. The Architecture run includes all 49 recurrence capability cases,
four existing owner/recipient guards and four catalogue/skip/Docker-trait checks.
The seven changed C# files pass formatter verification with no changes; its
workspace-load warning is distinct from the warning-free build. The 332-source
hash/mode receipt closes at
`afdda5089283dd6eee49f527576466778817bf78f3a041c857d4385bccb00239`.
The local link audit passes 1,740 links and 519 fragments. These focused checks
do not replace Step 9's complete regression or real CI. Round 1 and its verified
corrections are complete; a fresh full Step 8 round 2 is still required.

Step 9 explicitly owns durable unknown/deployment-mismatch observation, including
an empty-registry restart. Local registration mismatch flags alone do not qualify
that cold durable-health surface. These corrections add no new ADR decision,
role/grant, production business definition or general lifecycle selector.

#### Step 8 full review round 2 (2026-10-18)

Three new independent reviewers inspected the complete `69405b16..f687924b`
range: GPT-6-astra xhigh (runtime/lifecycle), GPT-6.1-sol xhigh
(persistence/authority/security), and GPT-6.1-sol high (proofs/corpus). The
persistence lens verified no finding. Runtime and proofs independently found
the same Major acceptance-proof gap, counted once: ADR-0059 explicitly requires
recurrence progress while the actual control generation stays drained/held.
The earlier native-manager-thread hold and empty-registry delayed-job repair
proofs did not establish this composed scenario. No new Step 8 runtime defect
was verified. These full reviews ran no toolchain/database checks.

The new real-facade test preregisters a due 61-day-old, 30-day-interval manifest,
then locks that actual row before starting the runtime. It holds the actual
control generation's owned Invocation, requests drain, positively joins its
normal children and verifies zero occurrences/native jobs before unlocking.
The retention barrier is explicitly synthetic; no network/business fault is
claimed. A coalesced occurrence skips two slots and completes one reciprocal
recipient/child with one normal attempt while the same generation remains held.
Three real SDK ticks remain Enqueued during drain; three actual duplicate store
quanta add no slot. Releasing and positively joining the original generation
permits a replacement to execute those ticks without another occurrence, child,
normal attempt or feature execution. Actual runtime and both source lifetimes
must confirm cleanup/disposal.

A separate inherited Major parent-start race was verified against the Step 8
base as well as current code. Facade startup could pass its final admission,
then encounter an already stopped parent's throwing Start guard and retain a
false cleanup failure indefinitely. This is not attributed to the new child
race or Step 8 diff. It is corrected now within the packet: an already-started
parent still rejects duplicates, but stop before its first start returns
neutrally using RequestStop's actual empty-loop witness. No cleanup flag or
completion promise is reset.

The new stopped-parent leaf fails at the original guard on unchanged `f687924b`
production, then passes with the correction. Its executed negative receipt is
`6a01365667134f9e6ff9c03d74da65f5fa4e9eac756b7367333d54f2571a9970`.
An initial test-only blocking-Result analyzer error is recorded separately and
receives no execution credit. The corrected actual-facade test holds the real
parent monitor, observes the original facade task's Starting state after its
last admission, then calls stop before parent admission and releases the
monitor. It joins that same startup task, facade and actual sources without
awaiting under a monitor. A distinct already-admitted-parent case preserves
duplicate refusal. The full facade barrier is qualified on corrected code;
no old-code full-facade cleanup claim is made.

Both focused correction reviewers approve the inspected source and causal test
premises; these are verification of round-2 corrections, not a third full review.
The first corrected 333-source receipt records a zero-warning/error Release
build and four focused PostgreSQL cases passing with no skips:
`88e67c6e998d603c87874635eee1deeac615b124732359ab8860abd82186aa12`.

A deliberate, uncommitted source control couples control-generation drain to
the shared process start gate. The same held-generation test then fails at
post-drain durable child progress, after its actual join/row-lock premises,
and performs its ordinary barrier/owner cleanup. That dirty receipt is
`ba68cf1a3dd7a77c9490dc82fe1d3208c585569c88ae714cdda10c291ae968fe`.
The generation source is restored byte-for-byte and the corrected source/mode
inventory matches again. No mutant enters the change. The test demonstrates
its ability to reject the forbidden dependency; it is not a production fault
or unconditional delivery-latency guarantee.

Final clean-source qualification freezes 333 changed/new C# sources and records
a zero-warning/error Release build, 2,408 Unit cases, 94 recurrence PostgreSQL
cases and 57 focused Architecture cases passing with zero skips. The focused
Architecture run takes 4m12s. The final receipt is
`268a030f9ee059860732668f78061973caf5cd6da342d5a43191a526915d9431`.
Scoped formatter verification returns no changes. Both full Step 8 review rounds
and verified corrections are complete; this closes Step 8 locally.

Step 9 follows under the already Accepted plan. Its scope includes durable
unknown/deployment-mismatch observation, full job metrics and health, complete
nineteen-rule enforcement, the skill/carrier handoffs and full packet regression.
The historical complete Architecture run exceeds the existing backend CI budget;
Step 9 must measure and reduce repeated detector scans while preserving every
subject and clean/dirty/restored control. No nineteen-rule promotion, complete
backend/frontend/demo, packaging, CI-duration compliance or real PR-head CI
success is claimed by Step 8 receipts. No maintainer decision is pending.


### P02b-4 Step 9 — enforcement and packet closeout (2026-10-18)

**Current status:** the [local closeout](#p02b-4-local-closeout-2026-10-18) owns
completed implementation/reviews, exact qualification and remaining PR/CI/merge
work. Earlier slices below retain their as-of evidence.

**Initial status — implementation in progress.** Step 8 closes locally at
`390515b8` after both full review rounds and verified corrections. This follows the already Accepted
ninth step; no new maintainer decision is pending.

The step's three internal closure units are:

1. Complete full job observation, including bounded dimensions, memory-only
   immutable gauges, cold durable unknown/deployment-mismatch health and distinct
   confirmed-failure versus uncertain outcomes. Reduce repeated Architecture
   detector scans without dropping cases, production subjects or any mutation/
   restoration pass; the unchanged whole backend CI budget still requires proof.
2. Match all nineteen packet obligations to actual nonempty production censuses,
   behavioural proofs and planted controls; complete carrier/feature handoffs and
   the shared repository background-job skill. Catalogue promotion waits for
   complete enforcement and qualification.
3. Run complete backend/frontend/demo regression and packaging/boot proofs,
   commit, then run two fresh full multidimensional review rounds with verified
   corrections and commits. Prepare the development-to-main PR with exact local
   evidence; real PR-head CI and maintainer merge remain separate.

The initial performance preflight is read-only planning, not a benchmark or full
review. It finds repeated whole-production instruction traversals inside one
read-only detector evaluation. A current-source four-case baseline runs before
editing that mechanism. Any index must be fresh for every clean, dirty and
restored evaluation, include every actual operand/owner/opcode and retain exact
identity and multiplicity checks. No shorter filter or increased CI timeout is
proposed. Full observation/enforcement and packet qualification remain pending.

#### Initial scan and existing-boundary qualification

The first slice adds complete method/field operand indexes inside one disposable
detector evaluation. Name buckets only prefilter the existing full identity,
opcode, order and multiplicity checks. Every clean, dirty and restored evaluation
gets fresh indexes. Owner strings are computed after candidate selection, and
ordinary call-site ownership is formatted only when its body contains a method
operand. The attribute-based async-owner mapping remains unchanged.

The four-case current-source baseline passes in 288.801 seconds of summed case
time. Initial indexing passes the same four cases in 160.233 seconds; the later
owner-string refinement records 60.062, 38.801 and 47.214 seconds for the three
existing runtime/bridge cases, plus 3.651 seconds for the expanded cache companion.
These are focused measurements with additional cache controls, not a complete
workflow speedup or proof that the CI budget is met. A deliberate persistent-index
mutant fails the actual membership comparison. A separate stale-owner-string
mutant fails at the same method's `RenamedRun` versus cached `Run` identity. Both
test-only mutations are restored byte-for-byte before clean qualification.

The complete intermediate Architecture run executes 403 cases: 401 pass, two
fail, and none skip, in 1,837.15 seconds of wall time. Its two failures expose
older source guards that still reject Accepted recurrence readers/task owners.
They do not justify broader runtime permissions. A brief native sample cannot
attribute managed hotspots; no profiling-based attribution is claimed.

The organization guard now enumerates five exact SQL subjects across four actual
files: three composite point reads and the bounded composite inventory/bounds
queries. It pins direct command arguments, minimal projections, tuple seeks,
page limits and actual tenant/organization parameter bindings. The original
validator's READ ONLY/announcement ordering and the EF primary-key ban remain.
The parallelism guard permits only nine exact invocation spans in three retained
recurrence owners, with method/signature/count and suppressed-flow publication
checks; the rest of each file is still scanned. Neither change adds whole-file
exemptions or substitutes for the separate database grants and resource proofs.

Focused independent inspections identify and close reader-discovery and binding
false passes: JOIN/interpolated/constant-concatenated readers, transformed SQL
literals, nonexecuted bindings, protected-key overwrites and captured parameter
rebinding. The actual-source dirty/clean companions exercise the same detectors.
Two new companions bring the published Architecture method count to 249; all
nineteen P02b-4 obligations remain Registered. These inspections are not either
full Step 9 review round.

The final 338-source snapshot passes a CI Release build with zero warnings/errors
and all 39 selected Architecture cases, with zero failures/skips. The selection
includes both corrected source guards and their companions, cache sensitivity,
catalogue consistency and both complete orchestration mutation companions. Its
source/artifact receipt is
`832548288722970b30482e75a1f814e07675476210784f93d6913a7eb07f9b81`.
The final two orchestration cases take 54.310 and 239.371 seconds, supplying the
fresh baseline for the next bounded optimization. The unchanged twenty-minute
whole-backend CI budget remains unqualified. Full job observation, complete
packet regression and both full Step 9 review rounds remain pending.

The next two-file slice removes eager orchestration identity formatting while
retaining the complete declaration and field-access inventories. Actual delegate
resolution uses a name bucket followed by the unchanged exact identity and unique
match policy. Field owners remain actual attributed method references until the
original name/type filters select them; string deduplication and opcode/write/
address counts stay in their original positions. An independent companion
compares the old/new declaration policy with foreign namesakes and overloads,
then checks every field group and ordered occurrence before the full mutations.

Both complete orchestration companions pass with zero failures/skips after a CI
Release build with zero warnings/errors. Their summed case time changes from the
fresh 293.681-second baseline to 191.688 seconds (33.271 and 158.417 seconds).
This approximately 35% reduction applies only to those two companions, including
the additional equivalence checks. The final 338-source/artifact receipt is
`3a8555f7481c4fb74a8123676a9ab61c5141f6e71d8cdccd393db8e2962a43f6`.
A focused independent inspection finds no correction; it supplies no full Step 9
review credit or whole-workflow duration proof. The twenty-minute CI budget and
the remaining observation/enforcement/packet work stay open.

#### Bounded mutation-collection qualification

Eight existing heavy companion Facts move into three public nested sealed
partial classes. Each Fact still owns its independent Cecil graph and complete
clean/dirty/restored sequence. Canonical rule Facts remain in the outer class.
An explicit per-class collection attribute caps the Architecture assembly at two
runner threads; neither the CI filter nor its timeout changes.

All 249 source method token sequences and compiled signatures/attributes match
before/after. Complete runner discovery preserves all 405 cases with exactly
eight declaring-class substitutions and no missing, extra or duplicated cases.
The first comparison omitted the custom-display NetArchTest meta case from its
404-row namespace subset; the corrected comparison includes that unchanged case.
A scratch actual-detector control proves distinct module/type/method/instruction
objects, an offender in graph A with graph B clean, and both restored clean.
The pinned xUnit factory selected by the actual assembly attribute assigns four
distinct collection identities to the outer class and three nested classes.

The 341-source snapshot passes a zero-warning/error Release build, all eight
moved companions and six corpus/cache cases, with zero failures/skips. Scoped
formatter verification reports no changes; a focused independent source review
finds no correction. The source/artifact receipt is
`21c209eaefce9e70e5bc55117b02629cfa6be7fa45a9f9bddf0c7dac0dfd1482`.
The eight-case run takes 869.24 seconds of wall time; summed case durations are
1,433.773 seconds. Completion-time/duration reconstruction is consistent with
two overlapping collections. The adapter's TRX StartTime usually equals result
receipt time, so it is not independent execution-start telemetry. These figures
are workstation qualification, not a same-source speedup benchmark or CI proof.

Repeated/full Architecture and complete backend-budget qualification remain
pending, together with full job observation, nineteen-rule enforcement and both
full Step 9 review rounds. No catalogue promotion or packet completion follows
from this focused slice.

#### Durable observation foundation qualification

The existing serialized status-read owner now publishes one complete immutable
aggregate after acknowledged rollback and actual resource cleanup. Frozen local
metadata classifies unresolved jobs, relevant historical recurrence manifests,
unfinished occurrences/recipients and unreleased reservations. The dispatcher
reads no payload or causal envelope and receives no new grants, source, task or
physical acquisition slot. Four independent indexed quarantine aggregates remain
in the same statement. A failed or stale refresh retains the prior sample and
cannot report healthy empty work.

Cold native-runtime controls use genuinely empty definition and recurrence
registries. Four isolated fixtures retain future jobs/manifests or future retry
availability on unfinished children, without deployment-error markers. Completed
or fully superseded parents cannot mask a missing child aggregate. Each real
runtime reaches an otherwise healthy snapshot; only the copied deferred bit
changes its health result. Observation leaves the durable records untouched and
constructs no feature handler.

The actual SQL hash CTE agrees with the independent .NET registry fingerprint
for thirty-six schedule/policy/date combinations, including microsecond limits,
unequal activation/anchor atoms, pre-epoch instants and maximum positive revision.
Two additional revisions prove discovery of frozen history needed only by an
actual pending occurrence. Diagnostic projections retain all production CTEs and
bindings and run under two session timezone/display formats. Epoch-offset,
repeated-anchor, omitted-history and removed-date-bound mutants fail their same
assertions. The unmodified full reader also accepts the valid population.

An intermediate fourteen-case cohort passes eleven cases and fails three new
test assertions that incorrectly require a nonempty business queue in control-only
fixtures. Explicit empty-queue versus single-job assertions correct that test
error. The final 348-source snapshot passes a CI Release build with zero
warnings/errors and all twenty-three selected PostgreSQL cases, with no failures
or skips. This includes real rollback-response hold/loss, refresh grant refusal,
last-sample preservation and actual-query partial-index removal controls. Its
source/artifact receipt is
`606e8b2a5945cec409c20f23d7c1c25f0ae8d0cb74d0a920848bfc336d1f951a`.
Paired assertions in existing real admission, recovery, capacity and external
checkpoint tests qualify status eligibility against actual stored transitions.
They distinguish future original due time, retry availability, live/expired
routing, execution ownership, actual retained identities, unknown/Pending/retired
definitions and profile mismatch. Unreleased reservations remain counted after
expiry under their declared group or fixed unknown label. Repeatable/no-start
history admits real Normal routing after the proved backoff; selected outcomes
admit real Finalize routing independently of the six-normal-attempt ceiling.
These guarded state pairs do not claim independent removal tests for every
redundant storage defense.

The final eligibility cohort passes fifty-two PostgreSQL and sixteen model/health
cases with zero failures/skips after a zero-warning/error CI Release build. Its
348-source/artifact receipt is
`af8b73b5899ffa0136cc0cdcd942d6cb5a003c904456be321043b760dcca7e80`.
The six production files remain byte-identical to the focused source inspection
and earlier three passing Architecture owner baselines. Focused inspections close
three hash-fixture proof gaps; none supplies a full Step 9 review round.

Complete metric emission, nineteen-rule enforcement, full regression/CI duration
and both full Step 9 reviews remain in progress. No catalogue promotion or packet
closeout is claimed by this slice.

#### Native observation projection qualification

The immutable runtime snapshot now projects actual running/retained work,
generation counts and capture/join/cleanup facts from their existing owners.
Confirmed metadata timestamps remain nullable: an actual successful announcement
can confirm monotonic zero, while no confirmation remains absent. Returned pool
and maintenance collections reject replacement through their collection interfaces.
These additions introduce no scheduling, SQL, source or release authority.

The first two-case PostgreSQL run passes the existing facade health case and
rejects a new fixture that supplies a separately reconstructed topology. Using
the host's actual frozen registry corrects the fixture without weakening the
production identity check. A focused inspection also closes its failure-path
cleanup gap: generation stop starts before either heartbeat or stop is awaited.
The final 350-source cohort passes both cases with zero failures/skips after a
zero-warning/error CI Release build. Its source/artifact receipt is
`733c1d57536c2c870810d27bb826b2cbf614cb153609ac6db01218e3f6dcb598`.
Three Architecture owner cases pass against the same production projection.
The manual real metadata callback does not claim Core's scheduling-cadence proof.
The focused inspection has no open findings and supplies no full Step 9 review
credit. Metric emitters and complete packet qualification remain pending.

#### Memory-only job metric collector qualification

One required concrete observation singleton binds the actual API runtime after
its snapshot owners initialize and before either source pin. The shared
API/Seeder composition creates no runtime or worker. Five observable families
project counts, exact SQL eligibility age, confirmed heartbeat age, component
state and fixed monotonic control observations. Each callback captures one
immutable memory snapshot and materializes its finite sparse measurements.
The [metric inventory](../standards/10-observability.md#required-metrics) owns
names and dimensions. Queue age remains the stored SQL sample; the earlier
acquisition/freshness timestamp cannot extrapolate it. Retained aggregate and
per-pool series remain distinct even for a legal pool named `none`.

Actual MeterListener controls cover required DI, missing registration,
bind-once/disposal authority, conservative freshness, missing versus confirmed
heartbeats and complete empty-sample zeroes. Real PostgreSQL/native controls
observe cold unknown work under empty registries, preserve prior quarantine
samples through actual grant refusal and check complete metric identities for
the legal `none` pool. Fifty-eight unit and ten PostgreSQL cases pass with zero
failures/skips; their production and fixture sources remain unchanged through
the subsequent Architecture corrections.

Three new Architecture companions inspect actual singleton descriptors, consumed
instrument/callback arguments, binding receiver/order/dominance and the transitive
memory method graph, including reached static initializers. Balanced clean/dirty/
restored controls reject indirect SQL, arbitrary delegates, hidden or extra Meter
registrations, name/callback bait and skipped binding. An initial seven-case run
passes six cases and fails a new fixture's ambiguous instance/static constructor
selection. After that correction, a four-case run passes three and reveals an
unreachable skipped-binding mutation: existing branch targets bypass its prefix.
Moving the mutation onto the actual landing instruction corrects the control;
the final three metric companions pass with zero failures/skips. The complete
existing runtime/source-owner companion passes against the same shared walk.

The final 356-source snapshot builds in CI Release with zero warnings/errors;
its source/artifact receipt is
`0327dabd84b0f91c0db248312f7747e0f6439756491160756bd6bfae43740100`.
Focused collector and guard inspections close their verified findings; neither
supplies a full Step 9 review round. Stage/event instrumentation, complete
control-path accounting, nineteen-rule enforcement, whole regression/CI-budget
qualification and both full Step 9 reviews remain pending.

#### Actual executor stage measurement qualification

Three synchronous families add execution duration, confirmed stage failure and
uncertain write/effect observations to the five memory collectors. Seven actual
executor owners record after their existing scope/Activity/context/authority/
source/auxiliary/registration cleanup. Duration excludes subsequent metric
notification and the outer native owner's later private-token cleanup; a blocked
listener still belongs to the actual task. Each instrument contains catchable
listener faults independently, reports observation unhealthy and preserves the
original result. A physical acknowledgement remains successful through cleanup
or notification faults.

The private provider-issued bit records the existing typed bridge's successful
entry CAS immediately before the actual call. It grants no execution, checkpoint,
retry or release permission. Construction/entry faults before that point do not
become uncertain external effects. Valid Running reports remain pending;
explicit Unknown and issued-call faults remain distinct from provider Failed.
Capacity cleanup without acknowledgement or uncertain COMMIT uses neutral
`NotCommitted`, since its existing result does not distinguish refusal from
failure. Business cancellation registration disposal now completes before its
final classification and measurement.

All 364 Jobs unit cases and 25 selected real PostgreSQL cases pass with zero
failures/skips. The latter retain actual dropped-COMMIT-response, post-ACK scope
and Activity faults, held cleanup, throwing metric listeners, unissued provider
constructors, genuine provider/finalization, Running/Unknown and issued reconcile
fault/timeout premises. Measurement assertions accompany those durable facts;
they do not substitute for them. These cases do not independently qualify every
capacity, recipient or stored-selection metric branch.

The first seven-case Architecture cohort passes six and fails a new stage-call
scan on a deliberately detached method reference. Narrowing to an attached exact
observer target before formatting identity fixes that detector regression.
Focused review also closes call-reference versus call-opcode, synchronous unit/
numeric metadata and actual stage-root selection gaps. Balanced method-group,
seconds-to-milliseconds and direct SQL mutations exercise those same checks.
The hidden-Meter boundary permits only the exact instance constructor; a dirty
actual observer static initializer proves that distinction. The final seven
Architecture cases pass with zero failures/skips, including the complete prior
runtime/source and orchestration companions.

The final 357-source snapshot builds in CI Release with zero warnings/errors;
its source/artifact receipt is
`847271cf34791d5cb0d930202de07500776afac2adc5463249e0bfe7d524e616`.
Production and unit/PostgreSQL fixture sources remain byte-identical through the
final guard corrections. Focused implementation and guard inspections have no
open findings in this slice and supply no full Step 9 review credit. Named-source
tracing/Start ownership, remaining branch/lease/control instrumentation,
nineteen-rule enforcement, whole regression/CI-budget qualification and both full
Step 9 reviews still precede packet closeout.

#### Named-source tracing and cleanup ownership qualification

The required observation singleton owns `learnstack.jobs`; its finite factory
returns an unstarted W3C Activity. All eight actual owners capture it before
`Start`. Recipient and pre-envelope recovery reads start independent roots;
terminalization keeps the real suppressed control lane. Trusted envelopes supply
the other parents. Recipient startup clears the caller's context before central
enrichment; it invents no tenant tags before the actual route context exists.
All seven executor owners independently attempt Activity and tenant restoration,
including a restoration callback fault after an actual COMMIT acknowledgement.

Absent, refusing, propagation-only, sampled and throwing samplers preserve the
factory contract. The first unit run detects an all-zero fallback parent;
omitting `SetParentId` for a default parent produces a genuine root. The corrected
373 Jobs unit and 70 real PostgreSQL cases pass with zero failures/skips. Actual
recipient, capacity, stored-selection/no-start, recovery, held/faulted cleanup and
lost-ACK premises accompany source/parent/measurement assertions. The first PG
selection accidentally includes a helper-name filter and is canceled; it earns
no full passing receipt. The replacement uses 35 exact test-method identities.

The source census binds the consumed source name, readonly owner, factory and
disposal receivers, actual retained Activity slot and Start/Dispose instructions.
Its memory walk follows the actual factory through helpers and static
initializers. Name bait, early Start, spare slots, hidden sources, helper SQL and
helper Start controls pass in the corrected ten-case Architecture cohort. Final
disposal method-group controls additionally distinguish an actual call from a
captured reference; all ten final Architecture cases pass with zero failures or
skips. The final 362-source receipt is
`ca0549e92bcb4bad50ea5fc7d82218bc3831a2bc9bcad49c21c6c6000095c9f9`.
Only the tracing guard differs from the preceding 373-unit/70-PostgreSQL source
snapshot; all production and behavioral fixtures remain byte-identical. Caller
operation/parent policy is behavioral qualification, not a general IL dataflow
claim. Focused reviews supply no full Step 9 review-round credit.

Complete control/lease/late-settlement accounting, nineteen-rule enforcement,
whole regression and unchanged CI-budget qualification, and both full Step 9
reviews still precede packet closeout.

#### Control transition counter qualification

Actual control owners retain physical renewal, retry and terminal observations
even when subsequent cleanup refuses release. The original completion request
counts its retry once; a clean locked probe can confirm that exact transition.
Same-generation routing repair and changed ownership do not imply a retry.
The external execution owner likewise distinguishes its actual retry ACK from
its original locked confirmation. Renewal history has a separate fixed
`renewal_history_confirmed` cell and never reopens the closed execution lease.
No counter recorder creates a source, task, listener invocation or authority.

The new actual-owner PostgreSQL theory covers ACK, lost ACK with a real blocked
confirmation query, and a later real admission that changes the original owner.
All three corrected cases pass. The existing nine native known-outcome/recovery
cases additionally assert direct retry ACK totals. The initial new theory uses
an invalid 25 ms polling bound and leaves its probe pacing at the 30 s default;
those setup defects are corrected to the accepted 50 ms and 5 s bounds. Its
initial three failures do not constitute counter-runtime failures. The first
build also rejects a nonexistent test-row property, replaced by an independent
database generation read.

The final source census roots each of the three recorders and the finite retry
predicate in the existing memory walk. Reached/unreached SQL helpers and clean
restoration distinguish these paths; exact references and typed bookkeeping
owners remain bounded. Real-listener projection uses distinct acknowledged and
history-confirmed renewal values. Focused reviews grant no full Step 9 round.

The final CI-mode Release build has zero warnings/errors. All 373 Jobs unit and
six final Architecture cases pass with zero failures/skips. The three corrected
owner cases and nine native PostgreSQL cases cover byte-identical production and
PG fixture inputs; the final changes affect only four guard/unit files. The
final 363-source receipt is
`067bf593428c041bcbb0b0e1857c0c2e1c8a635f55b0bed3608eb6c6fd7362e8`.

#### Control branch qualification

Fourteen actual-owner PostgreSQL cases pass together with zero failures/skips.
They cover external retry ACK, lost ACK with blocked original-history confirmation
and changed ownership; both native Finalize retry paths; ordinary retry plus
actual grouped-capacity rollback/release; physical versus history-confirmed
renewal; and terminal MUST audit followed by held or refused scope cleanup.
The existing prebound late-renewal case also passes with the selective proxy.
History-confirmed renewal never reopens execution permission. Terminal counting
preserves an acknowledged audit even when actual cleanup refuses release.

The proxy holds the actual renewal COMMIT by its returned column inventory;
other control transactions cannot consume that fault. Actual owner results,
durable state and row-lock waiters constrain the observations independently.
The grouped retry case qualifies a physical ACK, not grouped lost-ACK history.
The deliberate terminal cleanup failure disposes its inner scope before throwing;
the finite observation task joins actual lanes while public cleanup remains
unconfirmed. Isolated external retry ownership does not establish native
Hangfire handoff. The focused source review reports no verified findings and
grants no full Step 9 review-round credit.

The final CI-mode Release build has zero warnings/errors; scoped formatting
reports no changes. Production source remains unchanged from `c581f798`.
The final 364-source receipt is
`fb03a92a9e6e96b22aae6953c233fe5b4106d02567421b6e355d1741627b06b1`.
Connection-cleanup fault combinations and grouped retry history are not claimed
by this bounded cohort. Recurrence/bridge/lease/late accounting, nineteen-rule
enforcement, whole regression and unchanged CI-budget qualification, and both
full Step 9 reviews remain open. No catalogue obligation is promoted here.

#### Recurrence progress qualification

The actual recurrence owner records six fixed process totals from original
acknowledged occurrence/page results before cleanup qualification. These cover
inserted occurrences, skipped slots, pages, raw routes examined, inserted
recipients and completed fanout cursors. Registration, native ticks, null results,
unacknowledged values and later history add no original ACK. The existing
eight-family metric inventory projects them through fixed control-total
operations without identity labels, new SQL, sources, tasks or authority.

Both-scope actual-runtime PostgreSQL cases examine three raw lifecycle routes,
including a suspended tenant, and create two recipients in two pages. Independent
stored state, child/context checks and actual listener samples establish one
occurrence, two skipped slots and one completed cursor. Repeated collection and
duplicate native ticks preserve the totals. Two isolated actual-store/owner-hook
cases lose the physical COMMIT response after proving the server write: their
nonnull results and later no-op queries never become acknowledged progress.
They qualify occurrence and empty-page uncertainty, not native-runtime handoff
or nonempty-page lost-ACK inserts.

Eleven unit cases feed the actual generic owner observation hook with synthetic
ACK facts and real resource-model cleanup. ACK plus failed cleanup preserves
the totals and retained ownership; no ACK remains zero. This is not genuine
PostgreSQL transaction/connection disposal-fault injection. Distinct scalar
listener projection is supplementary to actual-runtime producer qualification.
The transitive memory census includes the private recorder and rejects reached
SQL and an extra caller; exact typed field access and emitted compiler cache
owners remain pinned. The two source-review findings, a missing namespace import
and assertion-failure teardown path, are corrected before the first build.
The focused review has no remaining findings and supplies no full Step 9 credit.

The CI-mode Release build has zero warnings/errors. All 384 Jobs unit, 52 focused
Architecture and four PostgreSQL cases pass with zero failures/skips. The final
366-source receipt is
`10600bb7dd16c847e74001e3e1af3b375dc1e1057cbdf6c0ea1caeaab073a6b5`.
Bridge/lease/late accounting, nineteen-rule enforcement, full regression,
unchanged CI-budget qualification and both full Step 9 reviews remain open.
No catalogue obligation is promoted by this slice.

#### Bridge repair qualification

The locked bridge candidate classifies prior acknowledged hint, expired prior
token without a hint, or unmarked state before replacing its token. Hint takes
precedence. Two fixed process totals count the disjoint marked-repair subsets
only after actual startup/native exit and a true fenced ACK return. Unmarked is
not first-ever work: a prior creation-failure deferral can clear its markers.
Deployment-mismatch deferral now counts only a true fenced status change.
The existing eight metric families and authority/source/grant boundaries stay
unchanged.

Thirty-two PostgreSQL cases pass, including actual stock repairs for missing,
failed and succeeded vendor hints; old-marker precedence; an actual stale ACK;
and false mismatch deferral after real claim cleanup expires its fence. Two
runtime/stock-client cases project both repair cells through the live listener.
Their prior-hint setup uses a synthetic numeric hint, not a claimed earlier
stock creation; the separate stock repair cases cover that provider boundary.
Actual native startup/stop/join controls preserve zero repair counts while
ownership is retained.

The real status-connection cleanup is held after its matching COMMIT. A separate
database read proves the vendor ACK while counters remain zero until the actual
bool call returns. A throwing Closed-event cleanup control instead leaves zero
visible ACKs and one retained logical owner; the actual native thread, observer
and test callback have ended. The bool API cannot expose the ACK hidden behind
cleanup failure, so no current-row inference backfills it. Exact locker/role
barriers and nested finally cleanup constrain these tests.

The corrected CI-mode Release build has zero warnings/errors. All 384 Jobs unit,
six focused Architecture and 32 PostgreSQL cases pass with zero failures/skips.
The first build rejects a test-local variable-name collision, corrected without
runtime changes. Focused review also corrects the exact classification-getter
caller census before testing; no findings remain and no full Step 9 credit is
claimed. Exact typed fields, immutable classification and reached memory-only
observation remain guarded. The final 369-source receipt is
`ae0bb0721b6bce47230b38c07098cfa245a56fe51f16263d79a3e4254e62b799`.
Lease/late accounting, nineteen-rule enforcement, full regression, unchanged
CI-budget qualification and both full Step 9 reviews remain open. No catalogue
obligation is promoted here.

#### First lease-loss observation qualification

Two fixed process totals retain the first observed renewal refusal or elapsed
local lease per validated start publication or clean recovery receipt. The
execution's cause CAS precedes its Interlocked-only persistent parent recorder;
recording adds no task, source, grant, listener or execution authority. Existing
identity publication stays in place. Already-visible stop, settlement, join,
cleanup fault or permanent renewal closure suppress a new count. Gate observation
does not establish universal atomic ordering with concurrent host stop.

The seven-case unit matrix covers refusal, actual timer callback after controlled
clock advance, stop, join, settled work, missing publication and repeated recording
while Run remains open. The latter distinguishes a real first-cause CAS from
duplicate suppression supplied by a separately closed gate. Existing held timer
Change/disposal controls preserve zero counts. Real renewal-owner PostgreSQL cases
distinguish a physical ACK from lost ACK plus original locked history. Actual
Outcome ACK with held cleanup, including stored recovery selection, remains zero.

Eight recovery-timer cases use real acknowledged PostgreSQL acquisition and
actual held timer factory/Change handles. Six stop/fault cases preserve zero;
two advance only the local monotonic observation clock during a held lease timer
prefix, then observe one elapsed loss before identity publication and no provider
entry. These are controlled-clock proofs, not wall-clock lease-duration benchmarks.
Cleanup still owns the actual returned handle; a hidden failed handle remains
retained. They do not independently qualify expiry before recovery Bind or every
normal publication/renewal elapsed branch.

All 519 selected Jobs unit and five focused Architecture cases pass with zero
failures/skips. The first PostgreSQL run passes eleven cases and finds two fixture
shutdown omissions: elapsed-only cases had not closed generation starts before
awaiting completion. After that test-only correction, all thirteen pass in
94 seconds. Production and Architecture/unit source hashes are unchanged between
receipts. The corrected CI-mode Release build has zero warnings/errors; scoped
fourteen-file format verification reports no changes. The initial build's helper
return-type analyzer warning is corrected. Focused source review closes an async
metadata/discarded-task enforcement gap with transitive scheduling rejection and
balanced dirty/restored controls. This is not either full Step 9 review round.

The final 370-source receipt is
`8ef7ac676619345655f818c371ee63cbf995564009703bd9243408724862795e`.
Late settlement, nineteen-rule enforcement, full regression, unchanged CI-budget
qualification and both full Step 9 reviews remain open. All nineteen obligations
remain Registered.

#### Actual late-settlement qualification

Seventeen fixed `late_<stage>_settled` process totals distinguish an elapsed
stage from eventual positive cleanup of its actual invocation. Only the stage
clock records the effective elapsed-stage marker; host stop, lease loss, timer
fault and a returned wrapper wait cannot invent it. Consumption happens once,
after actual framework/execution, timer, cancellation-callback and private-resource
cleanup. The existing bound invocation forwards only a fixed Interlocked update.
No task, data source, grant, execution admission or new metric family is added.

Each stage requires its actual typed task and positive cleanup. Recovery
confirmation follows the current NoPlan/retry/acquisition branch and never
substitutes an older clean probe. Whole-framework completion covers history and
NoPlan loops beyond their final SQL slot. These are settlement observations,
not durable ACKs, successful business outcomes, rollback or provider-quiescence
certificates. Permanently retained cleanup contributes no settled count.

Five new classifier cases cover absent tasks, observer join, exact business
stage, current confirmation branch and positive disposition. A live Meter unit
case checks all seventeen fixed projections, zeros and repeated scrapes. Wait
controls preserve predecessor/prospective-stage semantics and zero markers for
host stop or timer fault. All 525 selected Jobs unit cases pass with zero skips.
Four real stock-worker/PostgreSQL cases pass: two held framework-scope paths and
two provider-recovery paths distinguish actual clock expiry from host stop.
The latter holds private cancellation cleanup after provider return and confirms
zero until release, then one elapsed settlement or zero for host stop. This is
not positive native coverage of all seventeen stage kinds.

The initial five-case Architecture run passes both orchestration censuses and
singleton registration; two metric companions expose exact emitted `call`
opcodes where the new guard expected `callvirt`. After guard-only corrections,
all three metric cases pass. Additional framework-task/cleanup-field ownership
and a planted extra-cleanup-writer control also pass those three cases. No
production, unit or PostgreSQL source changes between these receipts. The
CI-mode Release build has zero warnings/errors; scoped eighteen-file formatting
reports no changes. Initial missing namespace imports are corrected before that
qualified build. Focused independent source inspections leave no open finding
and provide neither full Step 9 review round.

The final 375-source receipt is
`7cd762f4870900a03420ed4e14b416d89d15eeddae890a98fb6b2d391f62c1cf`.
Nineteen-rule enforcement, shared workflow guidance, full regression, unchanged
CI-budget qualification and both full Step 9 reviews remain open. All nineteen
obligations remain Registered.

#### Shared background-job workflow guidance

The shared `add-background-job` skill now describes the actual typed scheduler,
explicit registry/catalog composition, ordinary transaction owner, external
prepare/execute/finalize and three-state failure policy, capacity declaration and
recurrence path. The common skill catalogue links it for every agent runtime.
A focused source review corrects release wording for known failed/cancelled but
quiescent effects, distinguishes capacity declarations from optional entitlement
keys, and preserves all seven framework requests when replacing empty defaults.
It provides no full Step 9 review-round credit or feature business definition.

The preceding late-settlement commit's hook changes only six property-pattern
indents and one trailing blank line. Reconstructing the exact pre-hook bytes
matches its recorded hash; no semantic tokens change. Its test receipts remain
pre-hook source receipts, rather than a newly claimed post-hook test run.

#### Production manifest and route qualification

A single fresh structural census compares every disk-discovered production
project and each module's four assemblies with the loaded population. It pins
both empty-registry factory paths and catalogue implementations, rejects
business/recurrence constructors in scaffold modules (including delegate/async
forms), and checks frozen registry state. The actual Program provider separately
resolves both registries, catalogue aliases, seven framework requests, exact
pool/queue/worker topology and nonempty runtime/health/hosted registrations before
asserting empty business/tombstone/recurrence manifests and unopened sources.
Missing/duplicate registrations and valid nonempty fixture registries exercise
the same predicates; recurrence rejection remains independent of definitions.

The actual fourteen-route/method/handler multiset includes eight public actions,
liveness, OpenAPI, Scalar UI and its three static assets. Real extra dashboard,
recovery, neutral-path and probe-controller endpoints fail that predicate.
Whole-production IL additionally pins all four endpoint-registration call sites
and concrete/inherited public controller actions, covering paths not executed by
the Development host. Pinned dashboard method/type and generic middleware
capabilities have dirty controls without adding a dashboard runtime dependency.
A focused source review closes conditional-route, default-dashboard-options and
inherited-action false passes; it is not either full Step 9 review round.

Seven focused Architecture/corpus/discovery cases and the actual API composition
case pass with zero failures/skips. Sixteen actual PostgreSQL policy-selection
cases pass, including the added accepted nonrepeatable Failed outcome at admission
six and rejected false-exhaustion projection; SQL source is unchanged by later
guard corrections. The initial host assertion omitted Scalar's static assets;
its approved exact inventory now includes them. The corpus counter exposed four
previous observation companion methods absent from the prose tally; the
mechanically verified total is 254, including the new manifest method. The final
CI-mode Release build has zero warnings/errors. Three-file format qualification
reports no changes.

The final 377-source receipt is
`532c29d65f6362a9ea6d25a3c3d2e30827baa19213211847429821748f457105`.
Constructor-time context proof, combined privileged capacity qualification,
complete nineteen-rule reconciliation, full regression/packaging/CI-budget proof
and both full Step 9 reviews remain open. All nineteen obligations remain
Registered.

#### Constructor-time context and trace qualification

A four-case real executor theory covers tenant-wide/organization work, each with
success or cancellation while its actual business task remains held. After real
routing/admission cleanup, observation-only fixture hooks capture parameter,
root-request and handler constructors plus Body entry/continuations. Every
mandatory capture sees the exact stored tenant/org, SystemActor, causal actor,
job/incoming identity, module and correlation. Both sampled ActivitySources retain
the stored causal trace: parameter/root observe `job.execution`, while handler
and Body keep their genuine MediatR child and execution ancestry. The unrelated
caller context and Activity retain their exact references throughout and after
actual cleanup.

The same assertion rejects a real parameter construction before job ownership.
A separate trace-only capture at the genuine constructor preserves correct job
context but selects the caller Activity; context facts pass and the independent
trace predicate rejects it. These are oracle controls, not a claimed production
source-reorder mutation. Static hooks restore their exact prior values only after
the actual task becomes terminal. A failed bounded join supplies no cleanup
claim. Caller AsyncLocal isolation alone does not prove each explicit finally
setter; existing cleanup/Activity fault cases remain complementary.

All 24 selected business PostgreSQL cases pass, including the four new cases,
with zero failures/skips in 17 seconds. The final CI-mode Release build has zero
warnings/errors and scoped two-file formatting reports no changes. Initial
generic-assertion/cancellation-forwarding analyzer warnings are corrected with
explicit intentional noncancellation of the held Body waits. Focused independent
source inspection finds no defect; it is not either full Step 9 review round.
No production context writer or execution authority changes.

The final 378-source receipt is
`e77b70cd875f2b208d46deaac82f248b21e508dbcbd9bb5598a9b0030cf14a7c`.
Combined privileged capacity, complete nineteen-rule reconciliation, distribution
and full regression/CI-budget qualification, and both full Step 9 reviews remain
open. All nineteen obligations remain Registered.

#### Published distribution gate qualification

The existing backend CI job now publishes the actual Release API into a fresh
owned temporary directory, then runs both existing offline distribution
verifiers against that publish and its exact API/Infrastructure build graphs.
Failure remains gating; only JSON receipts join its test-result artifact. The
required job name, filters and twenty-minute ceiling stay unchanged. The kit
README documents the prerequisite Release restore/build/publish commands.

A fresh local framework-dependent publish completes in 1.176 seconds. All four
stock shared DLLs, corresponding-source archives and licence/notice inventories
verify. Fifteen dirty distribution controls reject, and the recipient-modified
Core inspection succeeds without a runtime restriction. The original 178-file
publish remains byte-identical after controls. These checks take 0.801 seconds;
replacement inspection does not prove a modified assembly executes compatibly.
The exact new workflow shell block also passes locally, emits both JSON receipts
and removes its owned temporary publish. `actionlint` and diff checks pass.

This is artifact/graph and workflow-step qualification, not API boot, full CI
success or proof that the entire job fits its unchanged ceiling. Combined
privileged capacity, complete nineteen-rule reconciliation, full regression and
both full Step 9 reviews remain open. All nineteen obligations remain Registered.

#### Canonical obligation ownership alignment

Fourteen existing test methods now carry their catalogue identifiers in place.
Their complete source bytes reverse to the prior files after replacing only that
one identifier: bodies, attributes, Theory data, traits and dirty companions are
unchanged. The two existing canonical owners and the three substantive manifest/
constructor owners provide the other five names. Compiled Architecture and
Integration discovery finds all nineteen names with their real case rows. The
Architecture method total stays 254. CI-mode Release compilation has zero
warnings/errors; scoped fourteen-file formatting reports no changes.

This is naming/discovery qualification, not a new executed-test receipt, a claim
that any one named method covers its full assertion, or catalogue promotion.
Each complete obligation also retains its schema, native runtime and fault
companions. Combined privileged capacity and complete regression/packaging/boot/
CI-budget qualification remain pending before full Step 9 reviews and closeout.
All nineteen obligations remain Registered.

#### Combined physical-capacity qualification

The actual registered job runtime, outbox and delivery processors share the
captured keyed guarded source at Pooling=true/MaxPoolSize=52. Their actual
completion tasks remain bound; the fixture explicitly starts those instances,
without claiming GenericHost startup ordering. One real stock expiration
invocation waits on a unique-key conflict for its exact resource. An independent
observer identifies its backend through the setup transaction's blocking PID;
a finite caller wait expires while that actual invocation remains Retained.

Two independently selected terminal-audit scopes forward the genuine writer and
hold cleanup after their actual terminal/MUST commits, before connection disposal.
Three distinct confined-role, tagged backend PIDs and all pending cleanup facts
are checked before and after TenantB's actual native job creates an organization,
commits its business/MUST row and reaches stock Succeeded. An absent-owner control
fails the same conjunction. Observers use a separate source. Finally every hold
releases, all three real owners join, Busy reaches zero, native server rows vanish
and the disposed tagged pool has no remaining backends.

The [canonical ledger](../standards/05-database.md#shared-dispatcher-source-lifetime-and-capacity)
counts all current simultaneous/nested/retained owner classes.52 remains a
conservative supported floor, not a measured exact peak. Pinned source review
corrects an earlier planning premise: ordinary Worker state-lock disposal precedes
PerformJob, so its wrapper lifetime does not establish an extra nested checkout.
The existing actual composition51 refusal and maximum158 topology/cleanup proofs
remain complementary; no52-socket saturation or51 impossibility claim is made.

Validation: CI=true Release build has zero warnings/errors. Four real PostgreSQL
cases pass with zero failures/skips, including the new combined case, both actual
messaging/source lifetime cases and retained-expiration native progress. The
closed382-file source receipt is
`b1fbb3344ff9df6f905937705a5c6ce493e59792f7facdccc947a8074aa9b451`.
The initial new-case run reached its business observer but failed on a test-only
text/UUID comparison; the corrected observer and all four cases pass on the final
receipt. Independent focused source review confirms the corrected expiration
physical witness, selective audit forwarding and actual joins, with no remaining
verified finding. This is not a full Step9 review round or complete packet
regression. All nineteen rules remain Registered pending that qualification.

#### Step 9 full review round 1 (2026-10-18)

Three fresh independent reviewers inspect `390515b8..d2bb2ee7` and the necessary
adjacent paths: runtime/security/ownership, enforcement/test falsifiability, and
corpus/composition/CI/distribution. The corpus reviewer also inspects the later
two-file frontend inventory correction. These are full Step 9 reviews; earlier
focused preflights do not count toward this round. Reviewers perform read-only
source inspections and do not claim independently executed regression results.

No Blocker or Major is verified. One Minor finding identifies two live
observation summaries that still describe delivered stage/trace/lease/control
signals as pending. Both summaries are corrected and independently rechecked;
full packet qualification remains explicitly pending. All nineteen catalogue
obligations remain Registered.

The complete non-Docker CI filter executes 3,043 passing cases with zero failures
or skips: 2,452 Unit, 410 Architecture, 177 HTTP Integration and four Contract.
Its local wall time is 1,641.70 seconds, with part of the run sharing the host
with Docker validation. This is not a hosted-runner benchmark or proof of the
unchanged twenty-minute whole-job budget. Repeated observation-body selection
inside a fresh detector evaluation is the next measured optimization candidate;
case identity, every mutation/restoration and the parallelism bound must remain.

Full frontend typecheck, lint and build pass. Its first full test run exposes
unclassified offline Hangfire probe/kit files. An exact 32-path inventory fixes
that test-only classification; unknown probe/vendor/runtime paths still reject,
and runtime import modelling is unchanged. The corrected complete guarded test
run passes all 1,616 cases with zero skips/todos; corrected typecheck and lint
also pass. No application build output changes with these test-helper edits.

The complete Docker complement is still running on the same frozen C# source.
It exposes an exhaustion-plan fixture whose entire population makes the broad
and narrow indexes equivalent, and a routing-wait observer that requires an SQL
suffix beyond PostgreSQL's default activity-text buffer. Source cross-checks
identify test-fixture defects, not demonstrated production failures. Corrections
and actual PostgreSQL reruns remain pending until this frozen run ends. A third
failure compares the latest schema to a historical predecessor while assuming
only the terminal-history function changes; later indexes invalidate that
premise. The correction will test the exact target/predecessor boundary and
retain a separate complete latest job-inventory round trip. Full
regression, demo/boot, CI-budget qualification, promotion and fresh full round 2
remain open; this record does not declare Step 9 or P02b-4 complete.

The completed Docker run executes 2,428 cases: 2,425 pass, the three identified
fixture cases fail, and none are skipped. Local wall time is 2,287.77 seconds;
this also supplies no hosted-runner or whole-job budget claim. The closed
382-file first-run receipt is
`1f44da4ddb710b882952141858a2a4fc49bf4e962ce9741c5ed1c3660eecf25c`.

Corrections retain the real planner and exact exhausted-index assertion by adding
an independently counted due, zero-attempt population; stable cutoff and volatile
dirty-control bounds remain unchanged. The routing observer opens before the
operation and uses the exact blocking backend dependency, with absent/wrong-PID
controls and a genuinely pending operation before caller cancellation. The
rollback test retains singleton function differences at its exact target boundary,
then separately compares the entire latest job inventory. No production SQL,
lease/retry limit, migration, grant or schema exception changes.

The observation traversal now groups actual bodies by reference-identical owner
once per fresh detector evaluation. Every original root, diagnostic and
clean/dirty/restored visit remains; the existing companion independently checks
actual group identity/order/multiplicity, linked async bodies and missing owners
through its real metadata mutations. No Fact or case is removed or added.

CI-mode Release build has zero warnings/errors; scoped formatting reports no
changes. All twelve affected real PostgreSQL cases and eleven Architecture cases
pass with zero failures/skips, including the cache, credential, runtime, three
metric and five corpus guards. Their closed 383-file receipt is
`7337d9765902903797a50a222ad44adc41033444917c8a23e3d5ab2fb2bc3e95`.
Independent correction checks of all five C# files find no remaining issue; these
are round-1 fix verification, not full round 2. The runtime-lifetime case records
129.93 seconds versus 176.09 in the earlier full run, while the credential case
records 211.55 versus 204.60. Different cohorts/shared load prevent attributing
those durations to an isolated speedup or claiming CI-budget fit. Whole-job
qualification remains required without raising its ceiling.

#### Canonical enforcement reconciliation

All nineteen packet obligations now report Implemented in the
[catalogue](../standards/21-architecture-tests-catalogue.md#backgroundjob_submission_is_transactional_and_stamped).
Each status names its actual compiled canonical owner and links its exact source;
the full assertion also retains its structural, native runtime and PostgreSQL
companions. Ten canonical owners run in Architecture and nine in Integration,
including the actual Program composition case in the non-Docker partition.

The closed first full run executes all 35 case rows of those nineteen methods
successfully. The later runtime-detector correction also passes its canonical,
metric, credential and independent mutation-equivalence cohort. This executed
evidence replaces the earlier discovery-only receipt; identifier alignment alone
did not authorize promotion. The three failed full-run fixture cases are
separately corrected and qualified as recorded above.

The five corpus guards pass after promotion: 254 Architecture method declarations,
221 Implemented catalogue entries, 131 canonical Architecture entries and 90
outside that assembly. Integration statuses name their own canonical class;
Architecture companions do not silently reclassify those rows. Link/fragment and
diff checks pass. Related live infrastructure/architecture summaries now reflect
implemented enforcement and the completed Step 8 reviews. Full Step 9 round 2,
demo/boot, packet closeout and exact PR-head CI/job-budget qualification remain
separate pending work.

#### Step 9 full review round 2 (2026-10-18)

Three new independent reviewers inspect the complete 150-path Step 9 range
`390515b8..720fca12` and necessary adjacent paths. Runtime/security/ownership,
enforcement/falsifiability and corpus/composition/CI/distribution lenses close
with no verified Blocker or Major. Reviews inspect source and existing receipts;
they do not claim independently executed regression or CI.

Three deduplicated Minor prose inconsistencies are verified: the live catalogue
preface still calls promoted entries Registered, the glossary still calls stage
instrumentation remaining work, and the architecture summary still calls both
full reviews pending. Narrow corrections point to the owning status record and
retain the distinction between local qualification and packet/CI closure. Both
affected reviewers independently recheck the actual corrections and canonical
owners/counts, with no remaining verified finding. This is correction verification
within round 2, not another full review round. Both full Step 9 reviews and their
verified corrections are now complete.

An inspected provider-stage `NeverStarted` label is not treated as a defect:
stage telemetry preserves the raw pre-checkpoint report, while the durable owner
still treats the unresolved report after ExecuteIntent as Unknown and grants no
quiescence, retry or release. The current observation contract distinguishes
those meanings; no runtime or vocabulary change is justified by this candidate.

At that correction check, the catalogue, glossary and architecture hashes are
`35a184d078b587817fdffb1dcc07e2bb7ec332dbb8510adeac88548cc8aeae52`,
`b2597922840fa98a7992721f05de816565820a093f56133af27727999bdaa311`
and `bdaf367b047bed602dd8a2c4597eef90851b6f236522b3dd9e549b687a017494`.
These three prose corrections change no C#. The fresh complete non-Docker
rerun subsequently passes all 3,043 cases with zero failures/skips in 1,660.34
seconds. Its closed 383-file source/artifact receipt is
`f6f01737022f2013483967ddbff89e06daa4abd2eea6dd1ffbeed7a7de80e3dd`.
This local result does not establish fit under the unchanged twenty-minute
whole-job limit; real PR-head CI/job-budget qualification remains required.

#### Fresh demo and browser/audit qualification

A fresh actual `development` checkout at `d2bb2ee7` contains no private root/web
configuration or previous demo artifact. Later commits change only tests and
documentation; the functional sources are unchanged. The normal seven-service
CI lifecycle restores, migrates, seeds, stops Keycloak, builds and boots the real
API/native HTTPS renderer, then exercises all nine linked seeded pages. The
earlier archive-only preparation boots successfully but cannot pass the required
Git source-provenance check because it lacks `.git`; that failed setup attempt is
retained separately and is not described as a product or audit failure.

The actual clean-checkout `make demo DEMO_MODE=ci` exits zero in 662.69 seconds.
The proof completes in 582.168 seconds, within its unchanged twelve-minute bound.
All 27 JSON/HTML audit pairs and nine browser screenshots are present; every
automated accessibility score is 1, maximum normalized gzip JS is 106,575 bytes
and maximum CLS is zero. English catalog and Turkish lesson screenshots are also
visually inspected. These checks preserve earlier human/VoiceOver evidence and
do not claim a new complete manual accessibility audit.

The nine LCP medians remain advisory at 2,012.61–2,197.74 ms against the existing
1,500 ms warning threshold; response-time and blocking-time medians raise no
warning. No timing advisory is relabeled a hard-gate success. Cleanup passes:
owned processes stop, the disposable Compose project is empty, private files,
environment and lock disappear, and application ports are free. Only sanitized
receipt/report/screenshot artifacts are retained. Artifact/graph distribution
verification and real API boot are separate evidence; this demo runs the normal
Release build output and does not claim modified-library or publish-folder
execution. Real PR-head CI and whole-job budget qualification remain pending.

#### Final completion-budget scan qualification

After the second full review and the completed 3,043-case run, a source-only
performance correction replaces six whole-instruction walks for three owner
budget fields with the existing fresh field-name index. Exact declaring identity,
opcodes, private/instance declarations, permitted/forbidden owners and duplicate
write counts remain. The standalone canonical Fact opens the same disposable
detector scope already used by each mutation evaluation. No Fact, case, mutation,
restoration, production subject, filter, timeout or concurrency bound changes.

Independent applied-diff inspection verifies both hunks and all 35 unchanged
mutation call sites/fixtures. CI-mode Release build has zero warnings/errors and
scoped formatting reports no changes. All eight canonical, actual owner-mutation,
independent instruction-index and corpus cases pass, zero failures/skips, in
110.92 seconds. Their names match the preceding complete-run receipt. The owner
mutation case records 109.69 seconds versus 186.35 in that earlier full cohort;
different cohorts/load prevent claiming an isolated causal gain or CI-job fit.
The closed 383-file source/artifact receipt is
`e39d6d9a101b8d5b56246f262dd946c8ec82206c75db9bfd603ce363a6610dbe`.
This is qualification-correction verification, not a third full review round.
Real exact-head CI must still qualify the complete unchanged job budgets.

### P02b-4 local closeout (2026-10-18)

**Local implementation and review complete; [PR #32](https://github.com/HodeTech/LearnStack/pull/32) open.**
All nine approved steps are delivered on `development`. Every step completed both fresh
independent review rounds and its verified correction checks. The full Step 9
rounds and final qualification corrections are recorded above; focused checks do
not count as additional full rounds. Accepted ADR bodies/metadata and earlier
as-of receipts remain intact.

| Step | Delivered | Implementation / two review rounds |
|---|---|---|
| 1 | Typed contracts, closed registries and ordinary authority ceiling | Complete / complete |
| 2 | Authoritative/vendor storage, exact grants and guarded bootstrap | Complete / complete |
| 3 | Immediate transactional submission, trusted stamping and committed admission | Complete / complete |
| 4 | Recoverable wakeups, independent pools and owned lifecycle/cleanup | Complete / complete |
| 5 | Existing transaction owner, fenced completion and atomic terminal MUST audit | Complete / complete |
| 6 | Cross-instance tenant capacity and quiescence-bound reservations | Complete / complete |
| 7 | Durable external prepare/execute/finalize and known-failure policy | Complete / complete |
| 8 | Durable UTC recurrence and recipient-bound tenantless fan-out | Complete / complete |
| 9 | Complete observation, nineteen enforcement owners, skill/distribution and regression | Complete / complete |

LearnStack state remains authoritative; stock Hangfire carries only opaque wakeup
references. Production business and recurrence manifests remain empty. The actual
Program manifest/route census, context restoration, transaction/terminal witnesses,
capacity and external uncertainty, recurrence/fan-out, grants/RLS and real worker
proofs back the nineteen Implemented catalogue obligations. The catalogue records
221 Implemented rules: 131 Architecture and 90 elsewhere, with 254 Architecture
method declarations. Discovery-only alignment was not used as executed evidence.

**Local validation.** CI-mode Release build passes with zero warnings/errors;
scoped formatting passes. The final complete non-Docker command before the last
test-only optimization passes 3,043 cases, zero failures/skips: 2,452 Unit,
410 Architecture, 177 HTTP Integration and four Contract. Its closed receipt is
`f6f01737022f2013483967ddbff89e06daa4abd2eea6dd1ffbeed7a7de80e3dd`.
The completion-budget correction receipt above qualifies the final changed
detector and unchanged case identities. Local command duration remains explicit
above; it is not substituted for the unchanged real CI job limit.

The full Docker complement executes 2,428 cases, with three fixture failures
retained honestly in the first receipt. All three are corrected without changing
production code or test limits; twelve affected real PostgreSQL cases then pass
with zero failures/skips. The source is frozen separately for each run. No fully
current all-green local Docker rerun is claimed; the actual PR-head integration
job must execute the whole complement. Frontend typecheck/lint/build and all
1,616 guarded tests pass, zero skips/todos. The fresh real demo/browser/audit and
complete cleanup receipt is recorded above. Packaging checks qualify the actual
publish artifacts/dependency graph, both offline verifiers, replacement-source
kit and dirty controls; actual normal Release API/native-renderer boot is proved
separately by the fresh demo. No modified-library execution is claimed.

All PR commit messages pass the actual strict hook. ADR lifecycle, relative
links/fragments, source/count/no-skip guards, workflow lint and diff checks pass.
The working branch remains `development`; protected stashes are preserved.

**Accepted limits and next owner.** ADR-0059 permits a whole pool to wait until
actual work/cleanup ends; ADR-0060's bounded unsafe startup/unwind class can kill
the API process. Neither process exit nor lease expiry releases uncertain
external reservations. Phase 03 owns operator recovery permissions/routes and
privileged execution; Phase 04 owns actual media adapters/process-tree quiescence;
Phase 11 owns mapped readiness, supervision/dumps, retention and measured
production capacity. P02b-8 still owns the erasure inventory. These are the
Accepted workload boundaries, not undisclosed unfinished P02b-4 mechanisms.

The next packet is **P02b-5 API authentication**, beginning with G10/G11/G12
(client), G14 and G20 decision review. No authentication/session implementation
starts in this closeout. P02b-5–8 and the overall Phase 02b exit remain open.
Real exact-head seven-job CI, its unchanged duration ceilings and the maintainer's
PR review/merge remain separate; this local closeout does not claim them passed.

#### PR handoff and current CI ownership

[PR #32](https://github.com/HodeTech/LearnStack/pull/32) opens `development` →
`main`, based on `ed60157c14d33fad926fd201b49c08e2d24eed1f`. The locally qualified
implementation is committed at `199b033e`; this handoff adds only documentation.
The closed C# receipt above remains byte-identical after normal commit hooks.

The PR's [live checks](https://github.com/HodeTech/LearnStack/pull/32/checks) own
current exact-head CI results, durations and downloadable artifacts. The initial
[run](https://github.com/HodeTech/LearnStack/actions/runs/39120117263) starts on
`199b033e`; a subsequent documentation push supersedes that unfinished run and
requires a fresh complete check set. No partial or superseded result establishes
final-head success. All seven required names and strict freshness remain enabled;
backend, integration and demo keep their unchanged twenty-minute job ceilings.
The maintainer owns PR review and merge; no merge is claimed here.

#### Hosted CI qualification follow-up (2026-10-18)

The completed [run 39120501520](https://github.com/HodeTech/LearnStack/actions/runs/39120501520)
at `2e1d227ad1e0150fcbdcbde8df6d68374dc2f682` is **failed**. Frontend,
meta, secret scan and OpenAPI pass. Backend and integration reach their unchanged
twenty-minute ceilings; backend's completed TRXs retain 2,452 Unit, 177 HTTP and
four Contract passes, with Architecture unfinished. Integration has actual early
native-supervisor failures as well as cancellation; no complete Docker TRX passes.

The actual native preflight reports `collector_pipe` on the hosted kernel.
Zero inherited core limits do not qualify a piped collector. Earlier topology
failures may race that refusal, but their precise endpoint states are unrecorded.
[ADR-0062](../decisions/0062-native-crash-proof-ci-provisioning.md) proposes explicit
disposable-VM provisioning authority; it is not accepted or implemented. ADR-0060
does not allow a failed precondition to authorize a host-wide setting change.

The demo hits its separate sixteen-minute outer setup/proof budget during final
report handoff; its inner proof lasts 659.906 seconds. Twenty-seven clean report
pairs on disk do not repair the incomplete 26-row accepted receipt. Cleanup is
confirmed; the run remains failed. Independent corrections address repeated
architecture identity work and duplicate migration/API builds, preserving all
cases, assertions, complete filters and existing deadlines. Final exact-head
hosted success remains pending. The earlier local receipts and completed review
rounds retain their stated scopes.

The demo correction is committed at `bc3691fb`: normal migration entry performs
one fresh Release API build before all unchanged EF chains consume that build,
and the demo launches the resulting API without a duplicate build. Nine executed
Makefile controls cover freshness, chain discovery/order and sticky failures.
The guarded frontend suite passes 1,625 cases with zero skips/todos; typecheck and
lint pass. A fresh Git clone at that commit completes the actual CI-mode demo in
658.115 seconds: setup 71.168 seconds, proof 575.766 seconds and confirmed cleanup
10.686 seconds. All 27 accepted report pairs and nine screenshots are present;
maximum normalized gzip JavaScript is 106,574 bytes, CLS is zero and all 27
accessibility scores are one. Nine LCP medians remain advisory warnings. Owned
processes/containers, private configuration and locks are removed. This local run
overlaps Architecture CPU work and does not establish hosted budget fit or new
manual accessibility evidence.

The first census correction completes the unchanged non-Docker filter with 3,043
passes and zero skips: 2,452 Unit, 410 Architecture, 177 HTTP and four Contract.
Every case identity matches the preceding full run. Its closed 383-file C# receipt
is `c367357d21cf39ae2d339b0d166651a5969891cb1d6c55a4c1a7bbaad22dc9e0`.
Local duration is 1,565.200 seconds; this is not a passing twenty-minute CI claim.
A subsequent measured-cost correction uses fresh per-evaluation instruction/name
indexes while retaining exact identities, opcodes, duplicates, all original
mutation controls and independent raw-traversal comparisons. It additionally
requires the actual authority detector to reject an unrelated detached target.
Plain fixture setup SQL uses the same real PostgreSQL through an owned nonpooled,
non-enlisting connection; the roles script retains its actual `psql -f` path.
The four added transport rows and 311 existing role/guard/migration/provider cases
pass together (315 passes, zero skips, 73.715 seconds). They verify actual roles,
ambient isolation, intact batches, rollback and observed physical-session exit.
The short server-cancellation row does not prove expiration of the helper's own
thirty-second deadline. Independent targeted source reviews find no blocking or
major defect; they are correction checks, not another full Step 9 review round.
The latest ten selected Architecture cases also pass with zero skips in 390.999
seconds, including actual authority/application-source/runtime mutations, exact
raw/index parity and the catalogue/Docker-trait controls. Their requested case
identities match exactly. These and the 315 PostgreSQL cases close the unchanged
385-file C# receipt
`919b1ef74b5b6d456abd628ac6e664f5f61fbd9c6184fde9737234a219dd259c`.
The Release build has zero warnings/errors; scoped whitespace formatting is clean.
PostgreSQL qualification overlaps Architecture CPU work; these subset timings
are not causal speedup measurements. Final hosted full-filter results remain
pending; no complete current Docker run or seven-check success is inferred.


##### ADR-0062 revision and read-only baseline (2026-10-18)

The maintainer's review requests changes; it is **not approval** of ADR-0062.
The revised [proposal](../decisions/0062-native-crash-proof-ci-provisioning.md)
selects a dedicated native-proof VM, verified fixed-pattern setup without
restoration, and complete native/ordinary Docker partitions behind the existing
fail-closed required result. It preserves seven required names and each existing
twenty-minute execution ceiling. Host changes, the partition and its launcher
remain unimplemented pending approval. The unmeasured 17:30/150-second allocation
is withdrawn; actual fit remains a qualification gate. No Accepted ADR body or
protection setting changes in this proposal pass.

Two fresh independent targeted proposal reviews cover governance/authority and
CI/process/coverage ownership. Both find no Blocker/Major; the first review's
container-versus-VM wording is corrected. The final CI review has no remaining
finding. These are proposal reviews, not a third full Step 9 round or hosted
qualification. Private research also executes an isolated unprivileged
.NET 10.0.10 nondumpability spike against the existing file collector: every exec
resets the flag, per-child set/readback succeeds, actual FailFast gives SIGABRT
without cleanup markers, and the benign twin keeps both. No pipe is installed or
exercised; the pre-managed startup interval is not qualified by this experiment.
The ADR records that narrower alternative and its boundary rather than claiming
host mutation is technically unavoidable.

[Run 39131069558](https://github.com/HodeTech/LearnStack/actions/runs/39131069558)
measures source `0fe0db2b5b584d141fc2c37aeeaf102f8ba0781c` with read-only observation
and the unchanged complete filters. Its PR merge checkout is
`c705ea2eaa39df1c1ccafc0ee67b615b573383a1`; artifact metadata ties it to that source.
There is no provisioning, changed collector, new partition or shortened deadline.
The actual observation classifies `systemd_coredump`, four CPUs, VM present,
container markers absent, noninteractive sudo available, writable proc mount but
no caller write access, inherited host soft core zero/hard unlimited, and an
empty readable all-state Docker baseline. These are closed observations, not
exclusive-VM attestation, successful-write evidence or child-limit qualification.

The full Docker command is canceled at the twenty-minute job ceiling. Console
retains 629 recognized completed rows: 600 passes and 29 failures. Six direct failures report
native supervisor refusal; 23 phase failures comprise fifteen exact pipe refusals
and eight membership failures. The latter's endpoint states are still unknown;
do not relabel them. Ordinary completed rows continue until cancellation, but no
complete Integration TRX exists; three zero-selection assembly TRXs do not supply
that missing evidence. The full non-Docker command also reaches its ceiling after
16:21 in tests and 3:52 setup/format/build. Its final TRXs confirm 2,452 Unit,
177 HTTP and four Contract passes with zero skips; Architecture retains 346
completed passing console rows but no final TRX. No complete Architecture/job
result or budget fit is inferred from those partial rows.

Meta, secret scan, OpenAPI and demo pass. The actual demo retains all 27 accepted
report pairs, nine screenshots, maximum normalized gzip JavaScript 106,551 bytes,
CLS zero and all 27 accessibility scores one. Setup is 173.023 seconds, inner
proof 653.561 seconds and confirmed cleanup 12.742 seconds; whole job is 15:04.
This supersedes no historical failed demo receipt and supplies no new manual
accessibility claim.

Frontend's hosted run has 1,564 passes and one source-census failure: the two new
CI-only Python files lacked explicit non-runtime classification. All nine new
Makefile cases pass on actual hosted Ubuntu. Commit `641ba365` adds only those two
exact paths; the unknown-Python rejection and runtime import graph remain intact.
The corrected local guarded frontend runs all 1,625 cases with zero skips/todos;
typecheck/lint pass. This local correction is not a passing hosted frontend result.
Final full-source CI remains red; native authority approval and independently
measured backend/Docker budget corrections still block PR closure.


A subsequent independent budget correction retains typed owners until the capacity
and recipient detectors select their actual targets, and formats recipient field
writers only for the two target-type families the detector actually consumes.
Global detached-target checks, exact assembly/signature/opcode/duplicate checks,
all existing mutation schedules and fresh per-evaluation caches remain intact.
Three standalone recipient checks use the same disposable evaluation scope.
An independent applied source review has no finding. The CI-mode Release build
has zero warnings/errors. All 29 selected capacity/recipient rows, raw/typed/index
parity companion and five corpus guards pass with zero skips in 191.804 seconds;
case identities exactly match the previous complete run. The unchanged 385-file
source receipt is
`c2521f3b2fe67289f796c9442e77f0977f96a9ec6272d2bfa39f7ef3e8d96c09`.
These local subset timings are not a causal speedup measurement or a full hosted
budget result. Other completed hosted cost evidence and repeated fresh-database
migration work remain assigned to P02b-4's active CI qualification; no case,
required check, timeout or production behavior is removed to obtain a pass.

The complete unchanged non-Docker filter at `82d5307c` subsequently passes all
3,043 cases: 2,452 Unit, 410 Architecture, 177 HTTP and four Contract, with zero
skips and the exact previous case-identity multiset. The closed unchanged
385-file C# receipt is
`6ee75c41c60c1f4f48d7e096834b43f5e7c54574d2a90cc10d4e56acab0fdfe0`.
Local test-command duration is 1,221.712 seconds, excluding hosted setup and
artifact work; this does **not** qualify the twenty-minute whole-job budget.

A separate local setup measurement invokes the existing fresh database CREATE,
all five real migration chains and DROP six times on one owned fixture. The cold
migration observation is 0.770250 seconds; five subsequent observations have a
0.296865-second migration median and a 0.317290-second CREATE/migrate/DROP median.
The first scratch run's empty receipt is rejected; after correcting only its
writer initialization, all fixed numeric stage rows and successful disposal are
verified, with no new container remaining. The CSV receipt hash is
`15cb39f977c200bb5374819dfa7b21610ba071f5899edfdea4b1feadb8b29c1a`.
This private measurement is not a committed regression, per-test seed/host/body
measurement, template-equivalence proof or hosted fit claim. No template path is
adopted from it. A collection-only parallelization is also rejected: distinct
databases leave shared mutable dynamic hooks and Hangfire filter restoration
exposed to races. The original serial proof semantics remain intact.

A further narrow source-cost correction selects the three concrete completion
owner writes from the existing fresh field-name instruction index. Exact full
constructor identity, attached type, opcode, multiplicity, task constructor and
live instruction adjacency checks remain unchanged. Two independent targeted
source reviews find no issue. The CI-mode Release build has zero warnings/errors;
all twelve selected pool, metric, tracing, credential, raw/index-parity and corpus
cases pass with zero skips and unchanged identities in 106.375 seconds. Their
closed 385-file C# receipt is
`1da1475eb0d13b473007d10bbc69047903b7140c6a3a3081b8d814e9a115351e`.
This correction has focused validation; the complete 3,043-case receipt above
belongs to the preceding `82d5307c` source. No new full-run or hosted budget result
is inferred, and ADR-0062 remains Proposed pending maintainer approval.

##### ADR-0062 acceptance and CI implementation (2026-10-18)

The maintainer explicitly approves revised ADR-0062 at `51346376` and authorizes
implementation on `development`. Acceptance and ADR-0060's dated disclosure are
committed before dependent CI code. The exact required integration name remains
`backend integration (Testcontainers)`; it becomes a fail-closed aggregate of
both complete Docker execution jobs. The other six required names, strict
protection settings and twenty-minute execution ceilings remain unchanged.

This follow-up implements one coherent CI boundary: closed provisioning and its
offline meta controls; native process/cgroup ownership, complete complementary
partition and result validation; then actual hosted qualification with complete
artifacts and measured failure-cleanup reserve. It receives two fresh independent
review rounds, with verified findings fixed and committed before final CI closure.
Existing native child assertions and all ordinary test cases remain mandatory.
No passing hosted result, new catalogue enforcement or completed implementation
is claimed by this acceptance. The earlier failed and source-specific local
receipts remain historical evidence; native separation alone is no budget proof.


###### Native CI implementation and local qualification

Acceptance commit `c4e64b52` precedes this implementation. The closed provisioning
owner validates copied scalar workflow identity, actual platform/checkout,
container/VM evidence, empty all-state Docker baseline and eligible collector
before privilege. It independently compares actual reads and verifies either
no-change or the one fixed literal write. Sixteen offline copied-owner subprocess
controls pass; no developer host setting is changed by those controls.

The native launcher owns finite execution/cleanup/export allocations, a delegated
non-root systemd unit and separate supervisor/work cgroups. The live supervisor
retains the work subtree until actual child join and an existing populated-zero
witness; unit disappearance is not proof. Ordinary Docker receives equivalent
process ownership on its separate VM, with no collector provisioning. The same
required integration name is an always-running fail-closed aggregate of both
successful, complete matching artifacts. The launcher's 203-second reserved tail is an allocation, not measured fit.
Twenty offline launcher/result controls pass, including actual subprocess,
setsid, live/missing cgroup, precise discovery IDs, sanitized TRX, incomplete
aggregate, bounded export and composed pre-privilege collector controls. Actual
hosted ownership, failure-tail
measurements and whole-job fit remain pending.

The CI-mode Release build passes with zero warnings/errors. Nine selected source,
corpus and native predicate cases pass with zero skips; no canonical rule or test
method count is added. The source census covers qualified/partial identities,
shared traits, actual launch markers and transitive declared helper dependencies,
with planted dirty and clean controls. It does not prove arbitrary dynamic-call
analysis. The unchanged all-source snapshot covers 1,209 C# files; its snapshot-file
hash is `91e9be8b8c7fb77954ed70638168ace4dabd761283526d8b0b19a8eaa7120b27`.

Fresh actual Release discovery selects 2,432 Docker case IDs: 31 native and 2,401
ordinary, disjoint and complete; the other three test assemblies select none.
Twenty display-name collisions make a name-only set insufficient. The launcher
parses only bounded identities from private VSTest protocol diagnostics; raw
serialized theory inputs are not export artifacts.

The complete local native partition passes all 31 cases with zero skips and exact
fresh-discovery IDs, including benign twins and actual same-child mutants over
real PostgreSQL. Its test interval is 166.889 seconds. The source snapshot stays
unchanged; no new container remains and all 22 pre-existing container states are
unchanged. Its TRX hash is
`1cc3004dd5d67f18a577088bc52a06cd80e91e7ed984037dca63eb20750e63f7`.
This qualifies the existing local native fixture, not hosted provisioning,
systemd delegation or either twenty-minute whole-job path.

Guarded frontend validation passes all 1,625 cases with zero skips/todos;
typecheck and lint pass. Only the four new CI Python paths gain exact non-runtime
classification; unknown paths and runtime import coverage remain guarded. Seven
ADR-parser and five read-only observer controls, actionlint and changed-document
links/fragments pass. Two fresh follow-up review rounds and actual final-source
hosted qualification remain pending; PR #32 stays open and no merge is claimed.


###### Native CI follow-up review round 1

Commit `ee11bc94` contains the coherent implementation after acceptance
`c4e64b52`. Normal commit hooks pass and preserve all 24 validated source hashes.
Two new independent reviewers inspect the exact committed boundary through
process/authority and coverage/corpus lenses. Their four findings are verified
against that source before correction:

- A failed optional preflight-log inspection drops the already bounded topology
  facts from the exported reason. The exact topology form now permits an absent
  preflight suffix while retaining its finite fields and refusal allowlist.
- A contradictory positive TRX skip counter is accepted and then erased by
  sanitization. Counter parsing now checks nonnegative values, row reconciliation
  and zero error/skip/incomplete buckets before projecting a file for export.
- Display-name-only interrupted records cannot distinguish 20 duplicate display
  rows. The private incremental VSTest execution stream now supplies actual
  assembly/case IDs, outcome and duration independently of final TRX.
- A statically declared instance-extension helper escapes the source closure.
  Its planted dirty control fails the original detector; source method symbols,
  reduced extension declarations and ambiguous candidates now retain the helper
  edge. An unrelated same-named extension remains a clean control.

The extension correction builds with zero warnings/errors and all seven selected
source/corpus controls pass with zero skips. This modifies only the Architecture
source file; all native fixture and production C# files remain unchanged from the
31-case local native proof. No extra rule or test method is added.

Twenty-four corrected Python controls pass, including interrupted colliding IDs,
partial event lines, completed tails, missing/duplicate/wrong identities, truthful
failed rows, contradictory counters and missing optional diagnostics. The actual
pinned runner also executes the two existing pure native predicates with
`RunConfiguration.BatchSize=1`; each result arrives in its own StatsChange event,
and the real parser's five-field records exactly match both actual TRX rows.
Without that setting this small run delivers results only at Completed. Raw
execution diagnostics remain private. The setting changes event batching, not
case membership or assertions; nonempty projects receive distinct private streams
and zero-case projects retain complete discovery coverage.

This narrow wire experiment and offline controls do not qualify the hosted unit,
provisioning, full ordinary partition or tail budget. The
[VSTest 17.12 configuration](https://github.com/microsoft/vstest/blob/v17.12.0/src/Microsoft.TestPlatform.ObjectModel/RunSettings/RunConfiguration.cs)
and [result cache](https://github.com/microsoft/vstest/blob/v17.12.0/src/Microsoft.TestPlatform.CrossPlatEngine/Execution/TestRunCache.cs)
are the pinned source basis. The corrected commit and a second fresh independent
review round still precede actual hosted qualification; PR #32 remains open.

###### Native CI follow-up review round 2

The round-1 correction is committed at `779b6007`. Two fresh independent
reviewers inspect the complete acceptance-to-correction boundary: GPT-6-sol
xhigh reviews runtime/authority and GPT-6.1-sol xhigh reviews proof/partition
and corpus claims. The runtime review reports no verified finding; the proof
review identifies two additional issues, both verified before correction:

- GitHub's absent job-services context serializes as `null`, while both owners
  accept only an empty object. Both now accept exactly those two empty forms;
  missing, malformed, non-object and nonempty service contexts remain refusals.
  Copied-owner controls exercise the real absent-services input before launch.
  All 17 provisioning and 25 launcher controls pass.
- A native call under a compiler-defined conditional symbol is invisible to
  a parser with no defines. The source census now reuses the existing bounded
  MSBuild symbol reader against the actual Integration Release project,
  including SDK implicit defines. Whole-class and method-body controls under
  `NET10_0` and `TRACE` reject unclassified callers; inactive `DEBUG` source
  remains a clean control. The planted control fails the original parser.

The corrected Release build has zero warnings/errors; all seven selected
source/corpus cases pass with zero skips. Scoped whitespace verification passes
with the separately reported workspace-loading warning. No native fixture or
production C# source changes in this correction, and no rule/method count changes.

The actual complete 31-case native fixture also passes with private incremental
execution diagnostics and `RunConfiguration.BatchSize=1`. All five exported fields
for every result match the final TRX exactly, including fresh-discovery IDs;
there are zero skips, zero remaining new containers and unchanged states for
all 22 pre-existing containers. The TRX hash is
`03e826a07d94e06ec22f748ca80586eef1fe709de279a4914d29569c21729bf9`.
This is a local fixture/protocol receipt, not hosted systemd/provisioning,
ordinary-workload or whole-job timing qualification. Both full independent
follow-up rounds are complete; verified corrections and final-source hosted
qualification still precede PR closure. PR #32 remains open and unmerged.

###### First hosted split qualification and meta control correction

Source `6daa9a1c` is pushed normally on `development`; PR #32 remains open.
[Run 39140874785](https://github.com/HodeTech/LearnStack/actions/runs/39140874785)
starts actual hosted qualification. Independent API readback confirms all main
protection settings, including seven required contexts and strict mode, are
unchanged.

The meta job passes all 17 provisioning controls, then exposes a synthetic-file
race in the launcher suite: a forked fixture child truncates its fake
`cgroup.events` before writing `populated 0`, while the owner reads concurrently.
The strict production parser correctly refuses the impossible empty snapshot.
The fixture now publishes the complete snapshot through same-directory atomic
replacement; production checks are unchanged, and missing/malformed-event dirty
controls remain. All 25 launcher controls pass after correction. An independent
focused review confirms the race and correction without broadening the owner.
This correction does not make the failed meta receipt pass; final-source hosted
qualification remains required. Other jobs continue to measure their actual paths.

The native job builds successfully with zero warnings/errors, then refuses
before collector provisioning or test launch on `unexpected_crash_artifact`.
Its read-only environment receipt confirms an empty Docker baseline and
`systemd_coredump`; the retained receipt does not identify the matching file.
No legitimate source/build candidate is established, and no exclusion is added.
The scanner retains its exact roots, predicates, deadline and strict refusal,
using NUL-delimited private output to classify only a finite root/name category
in the refusal. Paths, contents and provider text are not exported. All 26
launcher controls pass; an independent focused review confirms that nonempty
or malformed candidates still refuse. Actual hosted requalification remains
pending; this diagnostic correction supplies no passing native proof.

###### First split-run result and bounded topology proposal

Run 39140874785 completes with failure on source `6daa9a1c`, merge checkout
`853678c01c2c877e8bf5d7f81f559fc86ffbc9c4`. Backend reaches its twenty-minute
ceiling: complete TRXs contain 2,452 Unit, 177 HTTP Integration and four Contract
passes with zero skips; Architecture has no final TRX. Its 152 recognized passing
console rows do not qualify the remaining population or a complete run.

Ordinary Docker discovers all 2,401 cases and records 503 distinct passing case
IDs before `deadline_exhausted`; 1,898 cases remain censored. There is no final
Integration TRX. Failure cleanup positively joins its owned workload in the
recorded 0.475-second path, with forced cleanup and no successful natural Docker
disposal claim. The aggregate correctly fails. This is measured failure of the
current arrangement, not permission to change timeouts or omit cases.

Frontend passes all 1,625 cases and its ingress proof; OpenAPI and secret scan
pass. Demo's verified artifact contains 27 JSON/HTML report pairs, nine screenshots,
accessibility 1 throughout, CLS 0 and maximum normalized gzip JavaScript 106,553
bytes. Its setup/proof intervals are 182.954/658.518 seconds; 10.647-second owned
cleanup passes. These successes remain specific to this source.

Corrections `14584012` and `37ff4c59` are pushed normally after that run finishes.
[Run 39142456800](https://github.com/HodeTech/LearnStack/actions/runs/39142456800)
requalifies the separate meta/native corrections. Meta, frontend, OpenAPI and
secret scan pass. Native still refuses before provisioning/test launch; its new
closed reason is `unexpected_crash_artifact_docker_dump`. The actual matching
file/format remains unknown. No candidate is excluded, no host setting is
changed and no native hosted pass is claimed.

A further read-only diagnostic invokes fixed `/usr/bin/file` only for the
private NUL-delimited candidate and maps its captured description to a finite
format label. It shares the original scan's twenty-second allocation. Every
candidate still refuses, including unknown formats and failed/timed-out probes;
no description, path or contents are exported. All 26 launcher controls pass,
and an independent focused review finds no defect. Actual classification and
native hosted qualification remain pending; this adds no collector authority
or file exclusion.

Diagnostic commit `b4ee65b2` is pushed normally with the Proposed ADR-0063
documents. Run 39142456800 is superseded/canceled after its independent
meta/native observations; no complete backend/ordinary result is inferred.
[Run 39143417261](https://github.com/HodeTech/LearnStack/actions/runs/39143417261)
again passes meta, frontend, OpenAPI and secret scan. Its native job builds
successfully but refuses before provisioning/tests on
`unexpected_crash_artifact_docker_dump_unknown`. Further fixed diagnostic labels
distinguish Docker storage regions and common formats/probe refusal, without
changing scan traversal, predicates or any failure decision. All 26 controls
pass and independent focused review approves. The candidate remains unexplained;
no allowlist, deletion, shard implementation or passing native result is claimed.

Diagnostic source `d39233d5` starts
[run 39144033895](https://github.com/HodeTech/LearnStack/actions/runs/39144033895).
Meta, frontend, OpenAPI, secret scan and demo pass. Native still refuses before
provisioning/tests, now classified as an overlay-area `.dump` with generic binary
data format. Neither its contents nor image ownership is established by that
classification. Ordinary Docker records 146 passing case IDs before
`owned_completion_missing`; its failed receipt records forced cleanup and a
positive cleanup-ownership witness, not successful execution or natural Docker
disposal. No final Integration TRX exists. This early failure is distinct from
the earlier measured deadline exhaustion and remains under investigation.

The next read-only diagnostic compares the private candidate's canonical path
with exact-ID-bound image-layer metadata from the fixed local Docker socket.
Only classic `overlay2` at the expected root is understood; stable inventory and
canonical paths are required. Only finite match/no-match/unavailable labels can
leave the owner. Every crash candidate still refuses, including a positive
image-layer match; no path is pruned or declared benign. A separate closed
workload-error fact supplements a failed ownership receipt without replacing its
failure or supplying completion evidence. Malformed ownership remains a failed
receipt; a planted control first exposes an unguarded cleanup shape and then
passes after the explicit dictionary guard. The provisioning owner is unchanged.
All 30 launcher controls, the unchanged 17 provisioning controls and seven ADR
parser controls pass. The independent focused source/proof review verifies the
corrections and reports no remaining finding. This is offline diagnostic
validation; actual hosted provenance, workload cause and full qualification
remain pending. Main protection is independently read back unchanged in full,
including strict mode and all seven required contexts.

Source `f53c36fc` starts
[run 39145489321](https://github.com/HodeTech/LearnStack/actions/runs/39145489321).
Meta, OpenAPI and secret scan pass. Its native job still deliberately refuses,
but now positively binds the private candidate to a presently inspected image
layer: `unexpected_crash_artifact_docker_overlay_dump_binary_data_image_metadata_match`.
This proves that candidate's path provenance on that source, not its content or
earlier runs' files. Docker's
[classic overlay2 documentation](https://docs.docker.com/engine/storage/drivers/overlayfs-driver/#image-and-container-layers-on-disk)
distinguishes image lower layers from the separate writable container layer.

The resulting applicability correction retains the first scan unchanged. Only a
fresh, stable exact-ID image snapshot containing that same candidate authorizes
a second scan over every original root, pruning solely verified existing image
`diff` directories. A complete matching snapshot after that scan is mandatory
before a clean return. Unsupported/unreadable metadata, non-directory layers,
drift, malformed output, budget exhaustion and all nonpruned candidates remain
failures. Each pre-launch and cleanup scan independently requalifies its own
snapshot and records only its verified-directory count. No filename, format,
overlay-parent or writable-container allowance is added; no file is deleted.

All 32 copied-owner controls pass. They include real `find` traversal in an owned
temporary tree: the image file is first, yet a later writable/prefix/merged/work/
volume artifact still appears; malformed and post-scan-changing metadata cannot
make the result clean. No developer Docker state or host collector is changed.
Focused review and actual hosted qualification still precede closure.

The first fresh focused scope review approves `7cd07a18` without findings and
independently passes all 32 controls. A second fresh independent review follows.
Run 39145489321's ordinary job separately records `workload_internal_refusal`;
its failure remains red. The child now classifies unexpected faults solely by
a fixed owner-stage map and finite exception families, never exception text,
provider type names, filenames or stack output. The closed fact supplements
the failed receipt; no retry, passing result or completion witness is invented.

The second fresh focused review approves `f53c36fc..e710bb61` without findings,
independently passing all 33 controls. On source `e710bb61`,
[run 39146395561](https://github.com/HodeTech/LearnStack/actions/runs/39146395561)
passes the complete hosted native job in 420 seconds. The verified artifact
contains 31 passing native cases, zero skips and exact fresh-discovery IDs;
all five completed-result fields equal the final TRX and every exported hash
matches. Actual setup/execution/cleanup intervals inside the launcher are
2.825/231.561/10.628 seconds. Ownership and natural Docker absence are positive;
forced cleanup is false. The collector changes from `systemd_coredump` to the
supported file policy with inherited zero limits. Independent pre/post scans
verify 78/94 image directories respectively; neither reuses the other's snapshot.
This qualifies that source's native job, not the complete PR or another source.
The same hosted meta job passes 17 provisioning and 33 launcher controls,
including actual GNU `find` traversal. Backend/ordinary fit remains separate.

An independent source/filesystem investigation also verifies a normal VSTest
diagnostic-rotation incompatibility. It does not establish the cause of any
hosted ordinary failure. The inspected release sources
[v17.12.0](https://github.com/microsoft/vstest/blob/v17.12.0/src/Microsoft.TestPlatform.PlatformAbstractions/common/Tracing/RollingFileTraceListener.cs)
and [v18.6.0](https://github.com/microsoft/vstest/blob/v18.6.0/src/Microsoft.TestPlatform.PlatformAbstractions/common/Tracing/RollingFileTraceListener.cs)
rename the current file to one expected backup before publishing its replacement.
The old reader rejects a shorter replacement; a reproduced exists/open race
throws `FileNotFoundError`. The package pin `Microsoft.NET.Test.Sdk` 17.12.0 does
not itself pin the SDK console; the observed local console is
`18.6.0-release-26329-109`. Release-source equivalence is not an assertion about
an unobserved hosted build commit.

The reader correction owns generation zero before launch, retains its descriptor
and requires exact device/inode/backup continuity before each handoff. It drains
old tails, preserves partial lines and defers only a live rename gap; a final
unresolved gap, missing generation, unknown backup, truncation or replacement
remains red. The cumulative 64 MiB bound spans generations; handoffs are bounded
and descriptors close on success/refusal. All 38 copied-owner controls pass,
including actual rename/race, hidden generation and cumulative-bound controls.
Two real local pure predicate cases also pass with the pre-owned file and actual
SDK writer: exact discovered IDs and all five final-result fields match TRX.
This small append/protocol receipt does not qualify actual rotation, Docker,
hosted cgroups or whole-job fit. Fresh review rounds and final-source hosted
qualification remain pending for this independent reader correction.

Reader implementation `9aad3c57` completes the first fresh review without
findings; the reviewer independently passes all 38 controls and verifies both
private append/protocol receipts. A further controlled local experiment fills
only its private pre-owned generation zero above 10 MiB, triggering the
unmodified SDK writer's actual normal rotation. The reader follows one handoff
across 10,613,523 bytes; both real predicate cases pass with exact discovered IDs
and all five result fields equal TRX. This is deliberate local feasibility, not
a reproduction of a hosted fault. The new reader still owes final-source hosted
qualification and its second fresh review.

Run 39146395561's ordinary artifact is independently verified: 517 unique passing
case IDs belong to the 2,401-case expected set; 1,884 cases remain censored and
no final Integration TRX exists. Its error is `deadline_exhausted`, distinct from
the earlier internal fault. The 0.777-second cleanup positively witnesses owned
join but is forced and supplies no natural Docker-disposal success. The
integration aggregate correctly fails despite the separate native success.

ADR-0062's reopen condition is met. Proposed
[ADR-0063](../decisions/0063-complete-bounded-ci-test-shards.md) describes one
backend-base VM retaining non-test gates and cheap non-Docker suites, four
Architecture method shards, ten ordinary Docker method shards and the unchanged
native VM. The two backend required names become complete fail-closed aggregates;
all seven names, strict protection and twenty-minute execution ceilings remain.
Actual Release discovery and exact method-filter rediscovery must prove every
theory row has exactly one owner, with independent aggregate recomputation.
Timing estimates are advisory; the proposed counts are not a hosted fit proof.
Repeated restore/build across fifteen base/Architecture/ordinary VMs replaces
two such VMs; the runner and queue cost is explicit. No shard code is implemented
before maintainer acceptance. The proposal names three implementation steps,
each requiring commit and two fresh independent review rounds.

Two independent proposal reviews (GPT-6-sol xhigh and GPT-6.1-sol xhigh)
approve the revised ADR and index without remaining findings. They independently
check retained failure artifacts, the pinned method-filter contract, exact
bounded supersession, cross-VM population agreement, aggregate-side independent
assignment recomputation, backend-base non-test gates and the sixteen-VM cost.
Actual selection, ownership, reserves and fit remain pending implementation and
hosted qualification. This is proposal review, not maintainer acceptance.

###### ADR-0063 review revision (2026-10-18)

The maintainer's response reviews the proposal; it does **not** accept it.
ADR-0063 remains Proposed. The initial `3f5f09e3` proposal and its first two
reviews above are historical inputs, not reviews of this revision. No shard
implementation or ADR-0062 amendment is authorized yet; `development` is retained,
PR #32 is open and P02b-5 has not started.

Fresh read-only API/artifact verification of
[run 39148006960](https://github.com/HodeTech/LearnStack/actions/runs/39148006960)
binds source `3553ff16` to merge checkout
`6de31fcce66d7f53e4d5d28fd163f65ab8af4d85`. Native succeeds in 465 job seconds:
31/31 cases, zero skips, exact fresh IDs/TRX fields/hashes, positive ownership,
natural Docker absence and no forced cleanup. The launcher-only intervals are
1.535/240.221/10.468 seconds; these exclude checkout/restore/build. Both fresh
reader correction review rounds are complete. Meta passes 17 provisioning and
38 launcher controls; frontend, OpenAPI, secret scan and demo also pass.

Backend again lacks a completed Architecture TRX and reaches the configured
ceiling. Its API interval includes the cancellation/post-step tail; that does
not extend its twenty-minute configuration. Ordinary is a distinct failed path:
416 passing IDs, 1,985 censored cases, no final TRX, `owned_completion_missing`
and `workload_internal_membership_io`. Forced 0.459-second cleanup witnesses
cleanup ownership, not successful workload completion. The retained facts do
not identify an errno or establish the root cause. ADR-0062 owns that correction;
sharding cannot count it as a measured deadline or waive it. The integration
aggregate correctly fails despite native success. Protection API readback still
has the same seven app-bound contexts and strict mode; no setting is changed.

Review dispositions are verified against the current tree and retained evidence:

| Review topic | Verified disposition |
|---|---|
| Stale native/meta Context and reader follow-up | Corrected source-scoped history: meta race fixed; image provenance and reader both reviewed; native now passes. Earlier ordinary internal/completion faults remain separate. |
| Four Architecture bins, 152 versus 346 rows | Confirmed weak sizing, not a fit proof. First/current/earlier console prefixes are 152/294/346 with no final TRX. Ordinary's ratio cannot predict CPU-bound Architecture. Local maximum is not a hosted upper bound. |
| Sum/wall confusion and 782-second window | Corrected: the window includes discovery/fixtures/test lifetime; 203 seconds already includes 60 seconds upload. Historical sums do not establish spare capacity or ordinary-only wall time. |
| Backend-base sizing | Newly extracted complete suite intervals and pre-test walls appear in ADR-0063. Base candidate remains one VM; its new discovery/owner/export and isolated fit still require measurement. |
| Repeated fixture/discovery costs | Confirmed: SharedSchema spans 75 classes; split VMs repeat container/migration/seed and other fixtures. Missing per-fixture/CPU measurements are unknown, never zero. |
| Fixed counts and new ADR per tuning attempt | Revised proposal permits reviewed manual Architecture 4–6 / ordinary 8–12, at most twenty backend/test execution VMs; initial 6/12. No automatic/dynamic expansion or larger ceiling. |
| Process composition and independence | Require two genuinely different A/B assignments on the same source via two full attempts; bin renumbering cannot qualify. Fix actual dependence if exposed. |
| Collection versus process/VM alternatives | Existing Integration collection parallelism and Architecture's two-thread cap clarified. Separate processes on fewer VMs are a credible unqualified alternative; VMs are not required merely to isolate static state. |
| Non-Docker cgroup privilege | Explicit new bounded hosted-only transient systemd authority requires acceptance; child stays non-root. Non-Docker gets no Docker baseline/cleanup, crash scan or collector authority. |
| Plan, timing/default costs and selector bounds | Proposed tracked JSON/owner, versioned frozen weights with provenance, unknown-method census, explicit 60-second estimate and finite direct-argv/filter bounds; actual selection still independently rediscovered. |
| Aggregate outcomes/names/ceilings | Backend requires base/all Architecture; integration requires all ordinary/native. Each unique required name belongs solely to an always-running aggregate, each bounded to five minutes. All seven protection settings remain. |
| Rerun/attempt/artifact semantics | Single-attempt receipts with immutable artifact IDs and attempt-specific job verification. Failed/aggregate-only reruns cannot reuse old siblings; recovery is Re-run all jobs. |
| General control owner and meta ceiling | Proposed general `CI_Test_Shards_Are_Complete_And_Bounded`, Registered only at acceptance, then Implemented with actual Python/meta controls. Native/Hangfire and Docker-trait rules keep their own scope; meta stays five minutes. |
| Cost, queue, toolchain and filter identity | Public standard minutes are free; effective organization concurrency is unknown. Queue extends feedback, not running ceiling. Each receipt records actual console/adapter; actual xUnit FQN producer is cited. |
| Probe path, terminology, diagnostics | Full probe README named; base is project-owned, only Architecture/ordinary selector-sharded. Finite/private diagnostics remain bounded; raw provider output is not added. |

The first fresh full revision review (GPT-6.1-sol, xhigh) approves with no
Blocker/Major/Minor finding. It independently verifies native result integrity,
base intervals, the current ordinary failure, source/merge identity and all seven
strict app-bound protection contexts. Its suggestions are incorporated: explicit
initial A/B algorithms and the 56-second current meta baseline with a provisional
30-second new-control allocation. Only aggregates propose
job-level `actions: read` beside `contents: read`; no execution token gains
permission. A second fresh
full review follows. This is proposal review, not acceptance or hosted fit.

The revised three steps are specified once in
[ADR-0063 Implementation Notes](../decisions/0063-complete-bounded-ci-test-shards.md#implementation-notes):
actual selector/plan controls; bounded execution/phase evidence; workflow/aggregate
and two-assignment qualification. Each retains commit and two fresh independent
review rounds before advancing. New general-rule registration and ADR-0062's dated
bounded replacement/additive disclosure happen **only after acceptance**. Their
future implementation must update actual catalogue counts in the enforcement
change, not preclaim them here. Full execution fit and the independent ordinary
owner failure remain open qualification work; no runtime feature is added.

### Durable outbox dispatch

P02b-1 Steps 2/3 implement `IOutbox`, replace Packet 3's step-7 shell with
an enrollment/invariant guard, and supply owner-side domain collection and the
first publisher. P02b-2 delivers hosted producer dispatch and production-census
enforcement; all four steps and both independent review rounds are complete and
merged. The [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns final
verification. P02b-3 delivers independent subscriber execution and enforcement;
it is complete and merged, with final CI/review/merge verification in its
[merge closeout](#p02b-3-merge-and-closeout-2026-10-15).
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
  with Step 4's nonempty credential caller census and passing planted controls;
  all four steps and both independent review rounds are complete and merged.
  The [merge closeout](#p02b-2-merge-and-closeout-2026-10-13) owns final verification.
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

**Current implementation — 2026-10-18.** P02b-4's nine implementation steps and
review gates are locally complete under Accepted ADR-0047/0058/0059/0060/0061.
The [local closeout](#p02b-4-local-closeout-2026-10-18) owns exact qualification
and remaining PR/CI/merge work. The pre-acceptance checklist below is historical
preparation; the Accepted decision package and delivery records above own current
authority.

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
- **The rejected-assertion row names its actor once there is one.** The recorder writes
  a null actor on the authenticated tier today, because no principal exists in the
  process and `IsAuthenticated` is a tier rather than an identity. ADR-0036 makes the
  actor the finding on that tier, so the realm integration above populates it from the
  validated token's user — the observable that also proves the assertion middleware runs
  after authentication.
- **Causal actor and causation.** P02b-3 supplies the validated incoming event ID
  to consumer audit intents and follow-up outbox production; causal actor remains
  separate from effective SystemActor. Nested commands inherit these identifiers,
  while a newly drained domain event supplies its own causation ID. The Accepted
  **G6** producer/common-owner contract owns these bounded metadata keys.
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
  P02b-4's accepted `BackgroundJob_Submission_Is_Transactional_And_Stamped` replaces
  `Hangfire_Job_Payloads_Include_TenantId` with tenant/org/actor/trace and atomic
  submission proofs under Accepted ADR-0047; it is not Implemented today.
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
P02b-3's
[Step 6 proofs](#p02b-3-step-6--production-enforcement-and-pr-closeout-2026-10-13)
also cover the combined HTTP/event-delivery tracing criterion: actual committed
rows match the enqueuing activity, announced tenant/org and exact nonempty
partition. The
[P02b-3 merge closeout](#p02b-3-merge-and-closeout-2026-10-15) owns final combined
event verification. P02b-4's job infrastructure is locally complete, with exact
qualification in its [local closeout](#p02b-4-local-closeout-2026-10-18). P02b-5–8
remain unimplemented; the whole phase is not complete.

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

P02b-4's local evidence and Accepted-contract reconciliation of these criteria
are recorded in its [local closeout](#p02b-4-local-closeout-2026-10-18); real
PR-head CI/merge remain separate.

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
  tested as distinct states. Job dimensions are implemented under Accepted G9/G17;
  the [P02b-4 local closeout](#p02b-4-local-closeout-2026-10-18) owns qualification,
  while Standards 10 owns the exact inventory.
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
