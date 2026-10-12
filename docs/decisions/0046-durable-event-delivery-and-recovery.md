# ADR-0046: Durable Event Delivery and Recovery

## Status

Proposed — maintainer approval pending. No implementation or gate is complete.

**Date:** 2026-10-12
**Deciders:** @cemil (approval pending)

The [P02b-2 decision package](../roadmap/phase-02b-events-auth.md#p02b-2-decision-package-2026-10-12)
owns implementation sequencing and numerical runtime bounds. This reserved record
decides producer dispatch and the coupled P02b-3 subscriber state machine before
either is implemented. Acceptance does not claim delivery.

## Decision Drivers

- P02b-1 already commits real `TenantCreatedIntegrationEventV1` rows. The current
  bus acknowledges zero subscribers; its two defaults consumers arrive in P02b-3.
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

LearnStack proposes a leased, grant-bounded outbox dispatcher with explicit
subscription readiness, fenced retries and atomic audited terminalization.
Durable subscriber acceptance and a transport-owned shared inbox separate each
subscription's business outcome from producer dispatch. The following contract
becomes normative only on acceptance.

### Producer dispatch and recovery

- `id = EventId` and the assembly-qualified stored type identity survive ADR-0057.
  Resolve only explicitly registered concrete contracts from known contract
  assemblies, with explicit compatible aliases and retired identities. Never
  load an assembly or resolve an arbitrary CLR type from stored text. Duplicate
  identities, alias collisions and invalid registrations refuse composition.
- Deserialize using frozen `PayloadJsonOptions`. Check payload identity, tenant,
  topic, partition and occurrence against the row; timestamp comparison accounts
  for PostgreSQL microsecond precision. Validate the complete envelope and the
  canonical reserialized payload's existing UTF-8 cap; `jsonb`'s text formatting
  is not the producer byte-budget representation. Unknown, retired, malformed or
  mismatched records request terminalization immediately with a closed reason.
- Construction-free manifests distinguish **Pending**, **Active** and explicitly
  **IntentionallyUnsubscribed** contracts. Missing policy is not intentional
  absence. Any required Pending subscription excludes that contract and its
  aliases before acquisition; polling changes none of its payload, attempts,
  lease, processed or terminal state. Active declarations without their required
  handler registration refuse composition. Intentional absence may acknowledge
  without publication; it is a separately tested policy, not a zero-handler guess.
- `TenantCreatedIntegrationEventV1` declares two required Pending subscriptions:
  `customization.initialize-content-type-default.v1` and
  `customization.initialize-taxonomy-default.v1`. P02b-3 activates them together
  with durable acceptance. No placeholder consumer or global worker-disable flag
  substitutes for this policy.
- Claim one eligible row just before invocation. A short transaction assigns a
  fresh acquisition token to `locked_by`, stamps `locked_until` and increments
  `attempts` once. Stable worker identity is diagnostic, not an ownership token.
  Eligibility and expiry use database time. Lease fields are both null or both
  present; processed and dead-lettered states are mutually exclusive.
- Every success, failure, release and terminal transition requires row identity,
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

### Subscriber acceptance and inbox ownership

P02b-3 implements this coupled contract; P02b-2 does not claim an inbox or
effectively-once business effects.

- Give each subscription an explicit stable ID, independent of handler class or
  assembly name. A shared tenant-owned `inbox_messages` uses
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
- A separate tenant-owned delivery ledger retains each subscription's envelope,
  attempts, availability, lease and terminal/replay state. A terminal subscriber
  failure never marks its inbox processed. P02b-3 owns its canonical DDL, RLS,
  exact grants and failure/terminal audit; no second dead-letter topic or duplicate
  dead-letter table is required.
- `IEventBus.PublishAsync` keeps its non-generic envelope signature. Once P02b-3
  installs durable acceptance, success means every required delivery was durably
  accepted, not that every handler succeeded. Duplicate acceptance is idempotent;
  partial acceptance can safely be retried. Unknown acceptance leaves the producer
  retryable. A subscriber scheduler retries one `(tenant, subscription, event)`
  through an internal executor, never through another broadcast.
- The existing outbox role schedules cross-tenant delivery machinery using only
  the minimal SELECT/status-UPDATE grants P02b-3 enumerates on that ledger.
  The app role receives only tenant-RLS-protected acceptance and completion
  permissions, including the named completion-column UPDATE; it cannot claim,
  reset retry counters or schedule another tenant's work. Business execution
  always opens a separate `learnstack_app` scope. It restores
  the validated envelope tenant and organization before resolution, announces the
  transaction, and executes as `UserId.SystemActor`; causal actor stays metadata.
  Absence of the organization marker does not erase a present envelope org. A
  truly tenant-wide fact deliberately carries null; the marker requires an org.
- One failed subscription cannot spend a sibling's or producer's retry budget.
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
  `IAuditStore.WriteEventTerminalAsync(AuditEntryDraft, DbConnection,
  DbTransaction, CancellationToken)`. It verifies the declared terminal operation
  and caller transaction, inserts only, and opens/commits no transaction itself.
  P2 admits only the dispatcher terminal writer and the producer operation below.
  P3 reserves the subscriber terminal writer and `platform.delivery.dead_letter`,
  with stable subscription/event identity and the same-transaction proof; register
  that operation only when its writer ships. Reject other callers, undeclared
  operations and wrong operation class/type. `PostgresAuditStore` remains the only
  audit SQL writer. Existing four writes retain their contracts.
- Declare `platform.outbox.dead_letter` off-path as `OperationClass.Must` and
  `OperationType.SecurityEvent` in the Tenancy catalogue/matrix, beside the
  existing infrastructure-owned `platform.admin_scope.enter` precedent. It needs
  no synthetic business aggregate. The row records the stored real tenant/org,
  SystemActor, event ID, closed reason, attempts and validated causal metadata.
  Outcome success describes the committed terminal transition, not publication.
  If stored correlation/causation/actor metadata is invalid, use a fresh validated
  attempt trace and omit invalid causal values; never copy malformed metadata or
  require a valid original envelope in order to audit its terminal refusal.
- The fenced terminal UPDATE and audit INSERT share one transaction. Failure of
  either rolls back both; a stale claimant writes neither. An uncertain COMMIT is
  reconciled from durable state before retrying; it cannot append a second audit
  to an already terminal row. Metrics remain process observations, not an
  exactly-once durable event ledger.
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
- Metrics have no tenant/event/partition/trace ID labels. Event types come from
  the finite declared registry plus fixed unknown/retired/invalid categories.
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
| [ADR-0010 Amendment 1](0010-cross-module-communication.md) | Idempotency's own-module inbox and handler-called guard, including its assertion | Shared transport-owned guard and corresponding catalogue rule; P02b-3 |
| [ADR-0038 Event contract](0038-cross-cutting-port-and-event-contracts.md#event-contract) | Publish reports immediate subscriber construction/invocation/disposal failures; future adapters preserve that outcome | Publish reports durable acceptance; separate subscription execution owns these failures; P02b-3 |
| [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) | Outbox role restricted to outbox-only permissions | Named status writes plus audit INSERT in P02b-2; only named delivery machinery scheduling grants in P02b-3 |
| [ADR-0044 § 9](0044-audit-write-path.md#9-audit_log-and-audit_config-are-tenant-owned-org-scoped) | Outbox role has no `audit_log` permission | INSERT-only for confined atomic terminal audit; P02b-2 |
| [Audit Coverage](../standards/18-audit-coverage.md) | Every platform-bypass invocation receives a security-event row | Confined routine dispatcher exception above; P02b-2 |

The fifth audit method is an **additive ADR-0044 seam amendment**, distinct from
the bounded grant-cell replacement above. Neither changes ADR-0033's
same-transaction guarantee. ADR-0033 receives a clarification of
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

At acceptance, reconcile Standards 05/10/18/20/21, architecture 15/31, glossary,
the phase register, Tenancy/Audit specs and applicable skills with the named
scope. Register tests before code; mark Implemented only with passing evidence.
Update `.env.example`, API/public-runtime/demo credential propagation and fixtures
with runtime delivery. An isolated test-owned worker replacement must not become
a production disabled-by-default switch.

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
- `Outbox_Type_Resolution_Is_Declared_And_Bounded`.
- `Outbox_Pending_Subscriptions_Preserve_Events`.
- `Outbox_Health_And_Metrics_Are_Bounded`.

Behavioural rules live in integration tests; structural rules live in architecture
tests. Every structural census includes production subjects and planted dirty and
clean controls. P3 obligations remain Registered until P3 delivery. The package
names the decisive migration, deadline, contention, recovery and privilege proofs.

## References

- [ADR-0006](0006-events-and-outbox.md), [ADR-0010](0010-cross-module-communication.md)
  and [ADR-0038](0038-cross-cutting-port-and-event-contracts.md).
- [ADR-0003](0003-tenant-isolation-defense-in-depth.md),
  [ADR-0017](0017-tenant-organization-hierarchy.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md) and
  [ADR-0057](0057-transactional-event-production-and-completion.md).
- [ADR-0033](0033-audit-durability-model.md),
  [ADR-0044](0044-audit-write-path.md),
  [Database Standards](../standards/05-database.md),
  [Observability Standards](../standards/10-observability.md) and
  [Events and Outbox](../architecture/15-event-and-outbox.md).
- [PostgreSQL 18 SELECT](https://www.postgresql.org/docs/18/sql-select.html)
  (`SKIP LOCKED` queue use and locking privileges).
- [.NET hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
  (cooperative cancellation and bounded host shutdown).
