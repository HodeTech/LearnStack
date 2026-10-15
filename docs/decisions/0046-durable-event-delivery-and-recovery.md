# ADR-0046: Durable Event Delivery and Recovery

## Status

Accepted — 2026-10-12. Implementation is authorized; delivery remains pending.

**Date:** 2026-10-12
**Deciders:** @cemil

Delivery updates: [Amendment 1](#amendment-1--producer-runtime-delivery-2026-10-12)
and [Amendment 2](#amendment-2--producer-enforcement-and-local-closeout-2026-10-12).
The Status above records acceptance-time state; Amendment 2 records local producer
closeout with PR-head CI and merge pending.
Current CI/review/merge navigation (2026-10-12): the
[PR handoff](../roadmap/phase-02b-events-auth.md#p02b-2-pr-handoff-2026-10-12)
links the live PR; the delivery notes retain their as-of status.

The [P02b-2 decision package](../roadmap/phase-02b-events-auth.md#p02b-2-decision-package-2026-10-12)
owns implementation sequencing and numerical runtime bounds. This reserved record
decides producer dispatch and the coupled P02b-3 subscriber state machine before
either is implemented. Acceptance does not claim delivery.

## Decision Drivers

- P02b-1 already commits real `TenantCreatedIntegrationEventV1` rows. The current
  bus acknowledges zero subscribers; its two default consumers arrive in P02b-3.
  Starting dispatch without an explicit admission policy would discard their work.
- Producer retries must not repeatedly execute a sibling subscription that already
  committed. Two consumers in the same module require distinct durable identities.
- The existing outbox role cannot insert audit rows. A terminal transition and its
  MUST audit must commit together without granting business-table access.
- Leases, unknown outcomes and cancellation require at-least-once recovery, not an
  exactly-once transport claim. A timeout cannot terminate arbitrary managed code.
- Four database roles, the ambient application transaction, tenant/organization
  isolation and demand-gated cross-process transport remain the baseline.

## Considered Options

1. **Grant-bounded dispatcher and durable per-subscription delivery** (recommended).
   Preserve required but unready work; separate producer and subscriber outcomes.
2. **Handler-owned, per-module inbox and repeated broadcast** (rejected). It
   cannot distinguish two same-module subscriptions or isolate their retry budgets.
3. **Disable all production dispatch until P02b-3** (rejected). It avoids premature
   acknowledgement by leaving P02b-2's production runtime unexercised.
4. **Audit terminalization on a separate connection** (rejected). A state change
   can commit without its MUST row, or a success row without the state change.
5. **Strict head-of-line ordering per partition** (rejected). Poison and delayed
   work would block later eligible facts; no current consumer requires that cost.
6. **Dapr/Kafka now** (rejected). No second consumer process triggers ADR-0035.
   A broker would not decide inbox ownership, independent retries or audit policy.

## Decision

LearnStack uses a leased, grant-bounded outbox dispatcher with explicit
subscription readiness, fenced retries and atomic audited terminalization.
Durable subscriber acceptance and a transport-owned shared inbox separate each
subscription's business outcome from producer dispatch. The following contract
is normative; packet delivery records own implementation evidence.

### Producer dispatch and recovery

- `id = EventId` and the assembly-qualified stored type identity survive ADR-0057.
  Resolve only explicitly registered concrete contracts from known contract
  assemblies, with explicit compatible aliases and retired identities. Never
  load an assembly or resolve an arbitrary CLR type from stored text. Duplicate
  identities, alias collisions and invalid registrations refuse composition.
- For identities not held by the Pending policy below, deserialize using frozen
  `PayloadJsonOptions`. Check payload identity, tenant,
  topic, partition and occurrence against the row; timestamp comparison accounts
  for PostgreSQL microsecond precision. Validate the complete envelope and the
  canonical reserialized payload's existing UTF-8 cap; `jsonb`'s text formatting
  is not the producer byte-budget representation. Unknown, retired, malformed or
  mismatched records request terminalization immediately with a closed reason.
- Construction-free manifests distinguish **Pending**, **Active** and explicitly
  **IntentionallyUnsubscribed** contracts. Missing policy is not intentional
  absence. Any required Pending subscription excludes that contract and its
  aliases before acquisition; polling changes none of its payload, attempts,
  lease, processed or terminal state, even if its stored payload is malformed.
  This hold precedes payload validation/terminalization; activation later admits
  those checks. Active declarations without their required
  handler registration refuse composition. Intentional absence may acknowledge
  without publication; it is a separately tested policy, not a zero-handler guess.
- `TenantCreatedIntegrationEventV1` declares two required Pending subscriptions:
  `customization.initialize-content-type-default.v1` initializes the `card@1`
  content type (`BuiltInCustomizations.Card`);
  `customization.initialize-taxonomy-default.v1` initializes the `plain@1` level
  taxonomy (`BuiltInCustomizations.Plain`). These are ADR-0057's existing two
  effects, not two content types. P02b-3 activates them together with durable
  acceptance. No placeholder consumer or global worker-disable flag substitutes
  for this policy.
- Claim one eligible row just before invocation. A short transaction assigns a
  fresh acquisition token to `locked_by`, stamps `locked_until` and increments
  `attempts` once. Stable worker identity is diagnostic, not an ownership token.
  Eligibility and expiry use database time. Lease fields are both null or both
  present; processed and dead-lettered states are mutually exclusive.
- Normal publication claims require `processed_at IS NULL`,
  `dead_lettered_at IS NULL`, `terminal_reason IS NULL`, `attempts < 6`, due
  availability and no live lease, and must pass manifest/local-partition admission.
  Expired prior claims go through recovery before becoming publishable again.
  Requested or exhausted rows enter terminalization only, never normal publication.
- Every success, failure, recovery and terminal transition requires row identity,
  the current token and an unexpired lease. Recovery first acquires its own fresh
  token by atomically replacing an expired lease. A zero-row transition is lost
  ownership: no late acknowledgement, error overwrite, lease clearance or audit.
- Six total attempts mean the initial claim and five retries, delayed by 1 s,
  5 s, 30 s, 5 min and 1 h. A claim committed before shutdown/crash counts even
  when publication's outcome is unknown. Expired claims 1–5 enter backoff before
  another normal claim; an expired sixth claim never invokes a seventh publish.
- An exhausted or invalid row records a closed `terminal_reason` request. A
  terminalizer acquires it without incrementing attempts or publishing, then
  atomically writes `dead_lettered_at` and its MUST audit. Audit failure retains
  the request for terminalization retry; it cannot cause another delivery.
  `terminal_reason` alone is not completed dead-letter state. Successful, retry
  and terminal transitions clear their lease in the same short transaction.
  Terminal state does not prove that no subscriber accepted an event: a timed-out
  publication may already have committed durable acceptance.
- Dead-lettered rows are permanently excluded from dispatch and pending gauges.
  A far-future `available_after` is not a terminal marker. Producer terminal rows
  retain their envelope for investigation; operator replay is Phase 03, with a
  separately specified reset/fencing contract before any endpoint ships.
- Eligible selection has a deterministic total order
  `(available_after, occurred_at, id)`. This promises selection order for one
  processor without failure, not enqueue/commit order, retry order or cross-process
  order. Per-key in-process serialization does not strengthen that promise.
  The normal pending partial index uses that same three-column order and excludes
  processed, terminal-requested and dead-lettered rows; recovery/terminalization
  receive separate matching indexes in the canonical DDL and migration.

### Subscriber acceptance and inbox ownership

P02b-3 implements this coupled contract; P02b-2 does not claim an inbox or
effectively-once business effects.

- Give each subscription an explicit stable ID, independent of handler class or
  assembly name. Its closed ASCII grammar is `{module}.{subscription}.v{N}`:
  lowercase letter/digit module, lowercase letter/digit/hyphen subscription,
  both starting with a letter, and positive decimal N without leading zeros.
  N versions the subscription's deduplication semantics, not the event or built-in
  revision. Renaming creates a new identity and needs an explicit migration/replay
  decision; a handler refactor preserves its ID. Duplicate IDs refuse composition.
  A shared tenant-owned `inbox_messages` uses
  `(tenant_id, subscription_id, event_id)` as its deduplication identity.
- The transport owns the inbox call. Inside an announced application-role
  transaction, a uniqueness-backed claim serializes duplicate attempts **before
  handler construction**. Business state, completed inbox marker, produced outbox,
  MUST audit and fenced delivery-ledger completion commit together through
  ADR-0040/0057's common owner lifecycle. Before owner commit, the ledger UPDATE
  must match its token and database-time unexpired lease. A sticky completion gate
  also refuses a timed-out/canceled attempt even if its token is unchanged. Zero
  rows or a closed gate poison and roll back the whole transaction before COMMIT.
  A failed transaction leaves neither completed inbox nor business effect.
  Cancellation after physical COMMIT starts is Indeterminate under ADR-0033,
  not guaranteed rollback; recover from the atomic ledger/inbox state.
- The inbox claim is an uncommitted row on that same business transaction; only
  successful completion persists it with a completed marker. Rollback removes a
  new claim. A committed completed row is never reset by targeted replay; the
  delivery ledger owns incomplete/terminal/replay state, not a failed inbox row.
- A separate tenant-owned delivery ledger durably retains the validated envelope,
  attempts, availability, lease and terminal/replay state. A terminal subscriber
  failure never marks its inbox processed. P02b-3 owns its canonical DDL, RLS,
  exact grants and failure/terminal audit; no second dead-letter topic or duplicate
  dead-letter table is required. Its envelope must survive producer-row purge.
  P02b-3 must choose a per-subscription copy or shared tenant/event envelope before
  DDL, name the resulting copies/references and retention dependency, and add all
  retained envelope stores to P02b-8's erasure inventory. No new business payload
  or arbitrary metadata is introduced by retaining the producer envelope.
- `IEventBus.PublishAsync` keeps its non-generic envelope signature. Once P02b-3
  installs durable acceptance, success means every required delivery was durably
  accepted, not that every handler succeeded. Duplicate acceptance is idempotent;
  partial acceptance can safely be retried. Unknown acceptance leaves the producer
  retryable. A subscriber scheduler retries one `(tenant, subscription, event)`
  through an internal delivery executor, never through another broadcast.
  The executor invokes that subscription, not `PublishAsync`; the outbox processor
  remains the only production publisher and receives no new publication exception.
- The existing outbox role schedules cross-tenant delivery machinery using only
  the column-level scheduling-identity/routing/lease/status SELECT and status-UPDATE
  grants P02b-3 enumerates on that ledger; no envelope/payload SELECT is granted.
  The app role receives only tenant-RLS-protected acceptance and completion
  permissions, including the named completion-column UPDATE; it cannot claim,
  reset retry counters or schedule another tenant's work. Business execution
  always opens a separate `learnstack_app` scope. It restores
  the validated envelope tenant and organization before resolution, announces the
  transaction, reads and validates its envelope under RLS by claimed identity/token,
  then constructs the handler and executes as `UserId.SystemActor`; causal actor
  stays metadata.
  Absence of the organization marker does not erase a present envelope org. A
  truly tenant-wide fact deliberately carries null; the marker requires an org.
- After durable acceptance, a subscription's business failure cannot spend a
  sibling's or producer's retry budget. Failed/unknown durable acceptance still
  consumes producer attempts; it is not a subscriber execution outcome.
  Targeted replay resets only that subscription with fresh ownership fencing;
  completed siblings remain deduplicated. P02b-3 supplies the internal replay
  operation; Phase 03 supplies operator permission and HTTP surface.
- External effects need provider idempotency keys and reconciliation; an inbox
  transaction cannot guarantee exactly once outside its database. Audit's
  platform-scope erasure consumer requires its own privileged execution contract
  in Phase 03, not a disguised ordinary application-role delivery. Its privileged
  effect uses the audited `EnterPlatformAdminScope` path and idempotent progress/
  reconciliation; an app-role inbox cannot make two connections atomic. P3's
  ordinary executor refuses privileged subscriptions until that explicit owner
  supplies their execution contract. Phase 11 owns retention purge, not redaction.

### Atomic terminal audit and privilege boundary

- Keep the four-role model. Extend `learnstack_outbox_admin` only with the new
  outbox status-column UPDATE grants and **INSERT-only** on `audit_log`; no audit
  SELECT/UPDATE/DELETE, business-table access or privileged role memberships.
  The audit grant belongs in a new Audit-chain migration, after `audit_log` exists.
- Add the fifth, narrow audit write seam
  `IAuditStore.WriteEventTerminalAsync(AuditEntryDraft, EventTerminalTransition,
  DbConnection, DbTransaction, CancellationToken)`. The immutable transition
  witness is minted only from the successful fenced terminal `UPDATE ... RETURNING`
  in that live transaction, never from caller-supplied expected identity. Restricted
  construction and a planted architecture census confine that minting path.
  The witness binds connection/transaction, acquisition, actual stored tenant/org,
  event, producer/subscriber kind, reason, attempts and, for a subscriber, its ID.
  Before INSERT, the seam validates the declared operation/class/type, live caller
  transaction and every corresponding draft identity against that witness; a
  mismatch throws and requires rollback of both writes. It inserts only and
  opens/commits no transaction itself.
  P2 admits only the dispatcher terminal writer and the producer operation below.
  P3 reserves the subscriber terminal writer and `platform.delivery.dead_letter`,
  as off-path MUST/SecurityEvent in the same Tenancy catalogue/matrix, with stable
  subscription/event identity and the same-transaction proof; register that
  operation only when its writer ships. Reject other callers, undeclared
  operations and wrong operation class/type. `PostgresAuditStore` remains the only
  audit SQL writer. Existing four writes retain their contracts.
- Declare `platform.outbox.dead_letter` off-path as `OperationClass.Must` and
  `OperationType.SecurityEvent` in the Tenancy catalogue/matrix, beside the
  existing infrastructure-owned `platform.admin_scope.enter` precedent. It needs
  no synthetic business aggregate. SecurityEvent records the consequential
  BYPASSRLS terminal state change; routine polling/claims retain the separate
  bounded exemption below. The row records the stored real tenant/org,
  SystemActor, event ID, closed reason, attempts and validated causal metadata.
  Outcome success describes the committed terminal transition, not publication.
  If stored correlation/causation/actor metadata is invalid, use a fresh validated
  attempt trace and omit invalid causal values; never copy malformed metadata or
  require a valid original envelope in order to audit its terminal refusal.
  Closed terminal reason/attempts/subscription values live in bounded metadata;
  `AuditEntryDraft.Reason` remains reserved for platform-scope entry reasons.
- The fenced terminal UPDATE and audit INSERT share one transaction. Failure of
  either rolls back both; a stale claimant writes neither. An uncertain COMMIT is
  reconciled from durable state before retrying; it cannot append a second audit
  to an already terminal row. Metrics remain process observations, not an
  exactly-once durable event ledger.
- The audit INSERT's tenant binding is a witness/call-path invariant, not RLS:
  the outbox role bypasses that policy. Neither announcing GUCs nor INSERT-only
  grants can bind it to one tenant. Tests must substitute another tenant, org or
  event against a real fenced transition and prove both writes roll back, alongside
  a matching organization-scoped success. This confines sanctioned code; it does
  not sandbox arbitrary SQL using a compromised bypass credential.
- Routine confined dispatcher bypass does not write one security-event audit per
  poll/claim/mark. The exemption requires grant confinement, structured bounded
  attempt outcomes, metrics/health and MUST-audited terminal transitions. It
  changes no `EnterPlatformAdminScope` pre-entry audit or other bypass operation.

### Diagnostics and execution bounds

- Persist at most 256 UTF-8 bytes in `last_error`, enforced by a CHECK. Use closed
  stage/reason/category codes, an optional allowlisted SQLSTATE and canonical
  validated attempt traceparent. Never store exception messages, stacks, provider
  details, SQL, connection strings, payloads or raw stored type text. Replace
  existing free-form descriptors with a fixed legacy descriptor before the CHECK;
  do not truncate potentially sensitive text. Preserve fact identity and state.
- New identity CHECKs refuse empty event/tenant IDs, the platform tenant sentinel
  and empty optional organization IDs. Invalid legacy identities fail migration
  for explicit repair; they are not silently rewritten or discarded to make an
  audit insert possible.
- A named keyed, lazy dispatcher data source is API-host-owned, never Seeder- or
  web-owned. Configuration validates the exact username without connecting or
  exposing parse input. Boot verifies connectivity and every new physical
  connection verifies exact session/current role, BYPASSRLS, NOSUPERUSER,
  NOCREATEDB, NOCREATEROLE, NOREPLICATION and no privilege-escalating membership.
- Publish deadlines bound the dispatcher's wait, not arbitrary callback lifetime.
  Cancel the callback, retain and observe its task-owned scope until it actually
  finishes, and fence all late transitions. Do not dispose live resources or
  accumulate unbounded abandoned tasks. Bounded overruns may let unrelated keys
  progress; block their own partitions, mark health unhealthy and stop acquisition
  when the approved outstanding-task bound is reached. Shutdown has a finite
  wait; process exit and leases provide recovery, not forced managed-task killing.
  P02b-3 must additionally fence its business transaction against late completion.
- Metrics prohibit `tenant_id`, `event_id`, `partition_key`, trace identifiers and
  other unbounded identity labels. `event_type` is allowed only from the finite
  declared registry plus fixed unknown/retired/invalid categories.
  Gauge callbacks read immutable memory snapshots, never perform SQL. Failed
  refreshes retain their prior snapshot and become unhealthy rather than report
  a fabricated empty queue. The outbox health check is registered, not publicly
  mapped; `/healthz` stays liveness-only and Phase 11 owns readiness exposure.

### Bounded supersession and additive seams

On acceptance, the following replacements take effect only in their named scope.
They are new decisions, **not ADR-0041 corrections**. Original Accepted bodies
remain intact, with dated disclosure links naming this record and activation.

| Authority | Replaced clause | Replacement / activation |
|---|---|---|
| [ADR-0006 Amendment 1](0006-events-and-outbox.md#amendment-1--dapr-pubsub-as-dispatch-transport-2026-05-18) | Consumer-side handler calls and own-module inbox/deduplication | Transport-owned shared inbox, stable subscription key; P02b-3 |
| [ADR-0010 Amendment 1](0010-cross-module-communication.md#amendment-1--dapr-pubsub-as-outbox-dispatch-target-2026-05-18) | Idempotency's own-module inbox and handler-called guard, including its assertion | Shared transport-owned guard and corresponding catalogue rule; P02b-3 |
| [ADR-0038 Event contract](0038-cross-cutting-port-and-event-contracts.md#event-contract) | Publish reports immediate subscriber construction/invocation/disposal failures; future adapters preserve that outcome | Publish reports durable acceptance; separate subscription execution owns these failures; P02b-3 |
| [Database Standards GRANT matrix](../standards/05-database.md#grant-matrix) and [Outbox grants](../standards/05-database.md#outbox) | Outbox-only SELECT and four-column status UPDATE; no audit privilege | Named status writes plus audit INSERT in P02b-2; only named delivery machinery scheduling grants in P02b-3 |
| [ADR-0044 § 9](0044-audit-write-path.md#9-audit_log-and-audit_config-are-tenant-owned-org-scoped) | Outbox role has no `audit_log` permission | INSERT-only for confined atomic terminal audit; P02b-2 |
| [Audit Coverage](../standards/18-audit-coverage.md) | Every platform-bypass invocation receives a security-event row | Confined routine dispatcher exception above; P02b-2 |

The fifth audit method is an **additive ADR-0044 seam amendment**, distinct from
the bounded grant-cell replacement above. Neither changes ADR-0033's
same-transaction guarantee. ADR-0003 Amendment 3's four roles and RLS postures
remain unchanged; its dated disclosure points to the actual grant-budget extension
in Database Standards, rather than claiming an outbox-only clause it never stated.
ADR-0033 receives a clarification of
that guarantee's new caller. P02b-3 adds inbox/delivery machinery to ADR-0044's
capture exclusions because explicit intents audit business effects; retry state
is not a second business aggregate. ADR-0023's inbox minting-list correction and
canonical Inbox DDL remain P02b-3 obligations, verified against acceptance history.
ADR-0038's envelope, frozen serializer, effective SystemActor, organization
restoration, per-key serialization and producer-only publication rules survive.

## Context

The current `PostgresOutbox` writes on the producer transaction, but no dispatcher
exists. `InProcessEventBus` constructs a scope per subscriber and collects their
failures. Treating that broadcast result as a durable per-subscriber outcome would
re-execute successful siblings. Treating zero handlers as unconditional success
would discard P1 provisioning events before their real subscribers exist.

The current schema has neither leases nor explicit terminal state. The outbox
role can update four status columns and cannot write audit. Existing standalone
audit writes open another transaction; platform-scope writes have a different
credential and gate. Neither preserves atomic terminal-state auditing here.

The queue and audit are first-party correctness machinery inside the existing
modular monolith. This selects no broker, fifth cross-module mechanism, new worker
host, operator endpoint or generic job runtime. Cross-process adapters still need
ADR-0035's trigger. G9 and ADR-0047 remain the next job-runtime decision, after P3.

### What would change this decision

A consumer requiring strict partition sequence, a second consumer process or
provider side effects without idempotent reconciliation requires a new bounded
decision. Schema aliases cannot silently turn an old fact into a breaking V2.

## Consequences

### Positive

- P2 can activate its real production worker without losing P3's useful events.
- Subscriber deduplication and retries follow stable independent identities.
- Terminal state has a durable atomic audit and a privilege boundary tests can pin.
- Crash recovery, ordering and timeouts have falsifiable rather than absolute claims.

### Negative

- Pending contracts intentionally retain backlog until P3 activates their consumers.
- Terminalization adds a small explicit state machine and a narrow audit privilege.
- In-process callbacks that ignore cancellation can exhaust the bounded overrun
  allowance. Health becomes unhealthy; an operator may need to restart the API.
- At-least-once recovery can repeat transport delivery; P3's inbox remains essential.

### Neutral

- No public contract changes; dead-letter operator routes belong to Phase 03.
- Erasure inventory belongs to P02b-8, privileged redaction to Phase 03 and
  retention purge to Phase 11, matching the Audit module's existing ownership.
- The companion Hub roadmap's per-module-inbox dependency needs a coordinated
  pointer update before P02b-3 closeout; this does not change Hub HTTPS contracts
  or authorize Hub runtime work.

## Implementation Notes

P02b-2 implements producer claims/recovery/terminal audit, registry/manifests,
credential confinement, API lifecycle, diagnostics and health. P02b-3 implements
durable acceptance, inbox/delivery ledger, independent subscriber retries/audit,
targeted replay, both Customization defaults and Seeder semantic-ID handoff.
P3 must fence timed-out business transactions before enabling Active consumers.
It also reconciles existing bus/consumer exception logging and span capture with
its bounded failure policy; P2's minimized diagnostics cover the dispatcher-owned
path and do not claim existing transport telemetry is already minimized.

At acceptance, reconcile Standards 05/09/10/11/18/20/21, architecture 15/31, glossary,
the phase register, Tenancy/Audit specs and applicable skills with the named
scope. Register tests before code; mark Implemented only with passing evidence.
Update `.env.example`, API/public-runtime/demo credential propagation and fixtures
with runtime delivery. An isolated test-owned worker replacement must not become
a production disabled-by-default switch.

The acceptance reconciliation explicitly replaces these non-ADR carriers, without
leaving competing current designs. Target P3 text stays labelled unimplemented:

| Carrier | Text to reconcile | Replacement / activation |
|---|---|---|
| Architecture 15: claim protocol, simpler alternative and processor guarantees | BatchSize 100, processor-ID lease, batch-held fallback and lease clearing | One just-in-time claim, fresh acquisition token and only the fenced transitions above; P02b-2 |
| Architecture 15: Inbox table and consumer pattern; glossary `IInboxGuard` | Per-module table and handler-called guard | Shared transport-owned claim/completion contract; decision at acceptance, implementation P02b-3 |
| Architecture 15: Dead-letter, both sides | Future-availability terminal hack, `learnstack.dlq.{module}`, `dead_letter_messages`, broadcast replay and `OutboxStatusEndpoints` | Explicit producer terminal state and subscriber delivery-ledger terminal state; targeted replay internal P02b-3, operator routes Phase 03, broker adapter still demand-gated |
| Glossary `OutboxProcessor` and `locked_by` descriptions | Batch claim and stable processor identity | Single-row acquisition tokens; stable worker identity is bounded diagnostic context, not persisted ownership; P02b-2 |
| Standards 09: Outbox | Catch handler failures, then increment producer attempts and log exceptions | Claim-counted producer attempts, bounded dispatcher diagnostics, and separate post-acceptance subscriber outcomes in P02b-3 |
| Standards 10: Spans and Required Metrics | Dispatcher batch spans and tenant-labelled pending count | Per-attempt spans, unlabelled pending count and the package's bounded metric inventory; P02b-2 |
| Standards 18 join grammar and Standards 21 audit-matrix rule | Seven off-path rows | Reconcile actual/planned row enumeration with producer registration in P02b-2 and subscriber registration in P02b-3 |
| Standards 21 `Outbox_Claim_IsHeld_Until_Dispatch_Completes` | Unconditional exactly-once dispatch, batches and two processes | At most one live owner under two independent processor instances, plus expiry/crash redelivery; P02b-2 |
| Standards 21 `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` | Rest-of-batch premise | Continued unrelated eligible work with ordinary failure and bounded overruns; P02b-2 |
| Phase 02b: packet targets, Durable outbox dispatch and Completion Criteria | Unconditional zero-subscriber acknowledgement, batch/terminal sketches and dispatcher-connection replay as a settled operator contract | Explicit intentional absence versus Pending hold, the claim/terminal rules above, and separately decided Phase 03 operator replay; reconcile at acceptance, preserve dated P02b-1 history |

The superseded consumer subscription sketch does not introduce
`IModule.ConfigureEventHandlers`: no such repository type exists. P02b-3 uses
the declared manifest and the actual composition APIs. Historical P02b-1 "Next"
and "reserved and undrafted" statements are as-of records; the current P02b-2
package and Active index own navigation to this Accepted record.

## Architecture Tests

Existing rules to backfill or reconcile without weakening their intent:

- `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` — exact credential
  confinement, with separate platform-key controls.
- `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` — ordinary failure and
  bounded cancellation-ignoring overrun, including safe scope ownership.
- `Outbox_Claim_IsHeld_Until_Dispatch_Completes` — two independent processors with
  live leases; expiry, same-worker reacquisition and stale success/failure controls.
- `Integration_Event_Handlers_Use_InboxGuard` — P3 transport-owned invocation;
  two same-module subscriptions and concurrent duplicates are distinct cases.

Additional canonical rules to register on acceptance:

- `Outbox_Retry_And_Terminal_Transitions_Are_Fenced`.
- `Outbox_Terminal_Transition_And_Must_Audit_Are_Atomic`.
- `Outbox_Terminal_Audit_Identity_Is_Row_Bound` — restricted witness construction
  census and mismatched tenant/org/event/transaction rollback controls.
- `Outbox_Type_Resolution_Is_Declared_And_Bounded`.
- `Outbox_Pending_Subscriptions_Preserve_Events`.
- `Outbox_Health_And_Metrics_Are_Bounded`.

Behavioural rules live in integration tests; structural rules live in architecture
tests. Every structural census includes production subjects and planted dirty and
clean controls. P02b-2's production subjects are the registered worker, resolver,
claim/transition store and audit writer, not nonexistent Active handlers. Real
Pending rows prove holds; test-owned Active subscriptions exercise the same shipped
publish/mark path with real PostgreSQL. Handler/durable-acceptance censuses remain
Registered until P02b-3 and must not pass on an empty production-handler set.
The package names the decisive migration, deadline, contention, recovery and
privilege proofs.

## Amendments

### Amendment 1 — Producer runtime delivery (2026-10-12)

P02b-2 Steps 1–3 deliver the producer runtime authorized above: schema/grants and
confined composition, fenced claims/recovery/transitions with row-bound atomic
terminal MUST audit, and the mandatory API-host processor with bounded
publication ownership, observation and unmapped health. All three steps completed
both independent review rounds. The
[Step 3 delivery record](../roadmap/phase-02b-events-auth.md#p02b-2-step-3--hosted-lifecycle-and-observability-2026-10-12)
owns validation, reviewed corrections and the runtime proof scope.

[Step 4 enforcement and closeout](../roadmap/phase-02b-events-auth.md#p02b-2-step-4--enforcement-and-closeout-2026-10-12)
implements the full production censuses and planted controls; the focused 21-case
census/publication run passes with zero skips and the remaining role/health
catalogue rules are Implemented. Final regression, independent reviews and
exact-head CI remain in progress. This
amendment records delivered producer runtime, not completed P02b-2 packet closeout
or a merge, and changes no decision, rationale, grant budget or subscriber policy.

P02b-3 remains unimplemented: durable acceptance, shared inbox/delivery ledger,
independent subscriber retries and terminalization, both real default
subscriptions and Seeder handoff stay with that packet. Required production
subscriptions remain Pending; test-owned Active subscriptions supply producer
runtime proof without claiming durable consumer delivery.

### Amendment 2 — Producer enforcement and local closeout (2026-10-12)

P02b-2 producer runtime and enforcement are locally complete. All four
implementation steps completed both independent review rounds. The
[Step 4 delivery record](../roadmap/phase-02b-events-auth.md#p02b-2-step-4--enforcement-and-closeout-2026-10-12)
records nonempty production credential/publication and observation censuses,
planted controls, review findings and verified corrections. Test-only round-2
fixes at `606362b` pin exact source-reader method signatures, the actual
`TimeProvider.System` singleton and DI health binding to singleton observation.
All 26 focused/corpus cases and both fresh fix reviews pass. The post-fix Release
build has zero warnings/errors, all 286 architecture cases pass with zero
failures/skips, and solution-wide format verification passes. The full 3,393-case
backend regression predates those test-only guard fixes; the delivery record
states its scope and applicable earlier launcher/frontend evidence.

The catalogue remains 193 architecture test methods and 192 Implemented rules:
118 in that assembly and 74 elsewhere. This amendment changes no accepted
decision, grant budget or consumer policy. PR-head CI and merge remain pending.
P02b-3 is unimplemented: durable acceptance, shared inbox/delivery ledger,
independent subscriber retries/terminalization, both real default subscriptions
and Seeder handoff remain its obligations. Required production subscriptions
remain Pending; test-owned Active contracts prove producer runtime only.

### Amendment 3 — Consumer runtime and enforcement delivery (2026-10-13)

**Delivery note; no decision change.** P02b-3 implements shared envelope retention
and immutable inbox storage under the accepted grants, durable all-subscription
acceptance, independent six-claim subscriber retries/recovery, the app-role
business owner and timeout completion gate, row-bound terminal MUST audit and
internal targeted replay. Controlled execution retains actual task/scope cleanup;
publication acceptance and business execution use distinct local lanes. Both
real Customization subscriptions are Active and preserve existing concept
history. Seeder shares the initializer commands and verifies semantic identity.

Production enforcement includes all module layers, exact handler/manifest/DI
bindings, typed invocation and transaction-completion sites, concrete-handler
audit classification and the one-root constructor-port rule. Planted missing,
dirty and clean controls exercise these bounded checks; real PostgreSQL tests
own ordering, isolation and atomicity. They do not claim arbitrary reflection or
SQL transaction dataflow analysis. The
[Step 6 record](../roadmap/phase-02b-events-auth.md#p02b-3-step-6--production-enforcement-and-pr-closeout-2026-10-13)
owns validation, independent reviews and PR/CI status. Earlier delivery notes
retain their dated state. No exactly-once publication, cross-process ordering,
operator replay route, purge, broker adapter or mapped readiness is delivered.


### Amendment 4 — Bounded job control and terminal-audit extension (2026-10-15)

**Status: Accepted.** [ADR-0047](0047-transactional-background-job-runtime.md#attempts-terminal-audit-and-diagnostics)
extends **Atomic terminal audit and privilege boundary**'s fifth write with the
job-specific sixth, and the routine dispatcher exemption with confined
job/recurrence routing/status operations. The extension requires exact grants,
added caller censuses, bounded diagnostics, metrics/health and atomic terminal
MUST audit. Removing those controls invalidates it.

Business mutations, platform-admin entry and privileged maintenance receive no
general exemption. The producer/subscriber delivery policy, five existing audit
writes and their shipped receipts remain unchanged. P02b-4 implements the new job
control/seam and their proof obligations; acceptance claims no job runtime.

## References

- [ADR-0006](0006-events-and-outbox.md), [ADR-0010](0010-cross-module-communication.md)
  and [ADR-0038](0038-cross-cutting-port-and-event-contracts.md).
- [ADR-0003](0003-tenant-isolation-defense-in-depth.md),
  [ADR-0017](0017-tenant-organization-hierarchy.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md) and
  [ADR-0057](0057-transactional-event-production-and-completion.md).
- [ADR-0023](0023-strongly-typed-id-source-generator.md),
  [ADR-0035](0035-demand-gated-infrastructure.md),
  [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md) and
  [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md).
  [ADR-0047's reservation](README.md#open-adr-drafts) is the later job-runtime
  decision, not an existing ADR file.
- [ADR-0033](0033-audit-durability-model.md),
  [ADR-0044](0044-audit-write-path.md),
  [Database Standards](../standards/05-database.md),
  [Observability Standards](../standards/10-observability.md) and
  [Events and Outbox](../architecture/15-event-and-outbox.md).
- [PostgreSQL 18 SELECT](https://www.postgresql.org/docs/18/sql-select.html)
  (`SKIP LOCKED` queue use and locking privileges).
- [.NET hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
  (cooperative cancellation and bounded host shutdown).
