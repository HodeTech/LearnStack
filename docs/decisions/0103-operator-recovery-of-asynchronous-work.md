# ADR-0103: Operator Recovery of Asynchronous Work

## Status

Accepted — 2026-11-19, after the maintainer's decisions on the dashboard, the producer
reset, the operator client and the grant store, and after three independent reviews and
the corrections they led to. The dated record is the
[Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#p03-9-package-review-dispositions-and-acceptance-2026-11-19).
Acceptance closes gate G14 and, with [ADR-0101](0101-platform-operator-authority.md),
decides the dashboard question that D13 raises. It authorizes the code it describes and
is not proof that anything is implemented.

**Date:** 2026-11-19
**Deciders:** @cemil

## Decision Drivers

- [ADR-0046](0046-durable-event-delivery-and-recovery.md) says "operator replay is Phase
  03, with a separately specified reset/fencing contract before any endpoint ships".
  `DeliveryDispatchStore.ReplayAsync` exists and has no production caller. It takes no
  actor, reason or stale-state token, filters no terminal reason and writes no audit
  row, so a second operator can reset a row a first operator already re-terminalized,
  and an `invalid_envelope` delivery can be reset to fail again.
- [ADR-0047](0047-transactional-background-job-runtime.md) keeps a quarantined job
  frozen for every role by trigger, retains its reservations, and releases capacity only
  on confirmed completion, no-start or quiescence. Operator authority, elapsed time and
  a missing worker are not that proof. The external definitions today are the two
  invitation-email ones, whose outcome can end `Unknown`, and the mail adapter offers no
  fence an operator could cite.
- `learnstack_platform` holds no privilege on inbox, delivery or job tables
  ([Data Protection](../architecture/23-data-protection.md)), which says any change
  needs its own bounded decision and migration. The outbox role holds table-wide
  `SELECT` on `outbox_messages`, so a payload exclusion there would rest on code
  discipline alone.
- Only ADR-0046's confined dispatcher writes outbox status today; a second writer is a
  decision, not an implementation detail.
- The Hangfire dashboard needs a browser session on an API with none
  ([ADR-0004](0004-authentication-strategy.md)). The runtime's storage runs on the
  privileged source, so a dashboard write control reaches past the job fences. The stock
  statistics are suppressed ([ADR-0058](0058-bounded-stock-hangfire-maintenance.md)), a
  vendor "Succeeded" is not business success, and the runtime maps no dashboard: an
  architecture rule refuses any dashboard type.
  [ADR-0015](0015-api-gateway-apisix.md) still guides a backend filter on the realm role
  `platform-admin`, which [Standard 19](../standards/19-permissions.md) forbids.
- C15 requires dashboard and recovery to remain absent until admitted, and then proof of
  positive and denied routes, stale fences, producer-versus-subscriber separation,
  unchanged siblings, quiescence and reservations, MUST result audit and usable
  runbooks.

## Considered Options

1. **Metadata inspection through the platform scope on column-limited grants, and fenced
   transitions on a confined outbox-role owner, with no dashboard** (chosen). The
   database enforces the projection, the scope entry row audits each inspection, and
   each transition carries a stale-state fence and a same-transaction result row.
2. **Admit the Hangfire dashboard read-only** (rejected now). It adds a browser session
   path and a second source borrower, and shows state the runtime says is not
   authoritative. Its read-only form shows less than the inspection set below.
3. **Run the transitions in the platform scope with new platform write grants**
   (rejected). It widens a `BYPASSRLS` role with writes on tables the corpus holds
   closed, and none of the transitions crosses tenants.
4. **Inspect through the outbox role** (rejected). It reads cross-tenant on a role whose
   `SELECT` is table-wide, with no per-call audit.
5. **Release or requeue quarantined jobs on operator attestation** (rejected). It would
   supersede [ADR-0047](0047-transactional-background-job-runtime.md)'s quiescence rule
   and let an unfenced external start run again.
6. **Admit nothing and ship a runbook** (rejected). An exhausted dead letter would have
   no remedy, and C15's stale-fence and sibling proofs would have no subject.

## Decision

LearnStack admits a metadata-only inspection and two fenced transitions, and no
dashboard.

| Operation | Permission | Principal | Audit |
|---|---|---|---|
| `platform.delivery.inspect` | `platform.delivery.read` | platform scope, column grants | scope entry row |
| `platform.delivery.replay` | `platform.delivery.write` | outbox-role owner | result row, `delivery_replay` |
| `platform.delivery.reset` | `platform.delivery.write` | outbox-role owner | result row, `delivery_reset` |

1. **Inspection.** Cursor-paged metadata for dead-lettered outbox rows, rows whose
   terminal request is pending, terminal delivery rows and quarantined jobs with their
   reservation occupancy. A migration gives `learnstack_platform` `SELECT` on named
   metadata columns of `outbox_messages`, `event_deliveries`, `background_jobs` and
   `background_job_reservations`, excluding every envelope, parameter, plan, outcome and
   causal column, so the database refuses what the projection must not return. Grant
   tests read column privileges. Each row returns the fence its transition needs. A row
   whose terminal request is pending shows its state and is not actionable.
2. **Targeted subscriber replay.** `platform.delivery.replay` resets one
   `(tenant, subscription, event)` delivery. The request carries the fence read at
   inspection: `replay_count`, `dead_lettered_at` and `terminal_reason`. It is refused
   unless the fence still matches, the row is dead-lettered and unleased, the
   subscription is an Active ordinary one and the event's tenant is Active. A delivery
   that ended `invalid_envelope` is always refused, because an immutable envelope cannot
   become valid, and `unknown_type` and `retired_type` follow the registry at replay
   time. A completed delivery, its inbox row, every sibling delivery and the producer
   row are untouched, and a privileged subscription is refused.
3. **Narrow producer reset.** `platform.delivery.reset` resets a dead-lettered outbox
   row whose `terminal_reason` is `retry_exhausted` and which is not processed, under
   the fence `(attempts, dead_lettered_at)`. It clears the terminal fields and the
   lease, resets attempts and sets `available_after` to now. Every other reason is
   refused. The reset deliberately renews the six-attempt budget. A publication that
   already reached a subscriber is absorbed by stable event-id acceptance
   ([ADR-0046](0046-durable-event-delivery-and-recovery.md): terminal state does not
   prove that no subscriber accepted). Acceptance never reopens an existing delivery
   row, so the next dispatch reaches the Active subscriptions that have no delivery row,
   and a delivery that already ended terminal needs its own subscriber replay. Producer
   reset is not a power of subscriber replay: the two are separate requests with
   separate fences and neither writes the other's table.
4. **Principal.** A new confined owner uses the outbox-role credential behind an
   [ADR-0101](0101-platform-operator-authority.md) admission. It is a second writer of
   outbox and delivery status beside the dispatcher, recorded by an
   [ADR-0046](0046-durable-event-delivery-and-recovery.md) disclosure.
   `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` gains exactly that owner,
   `ReplayAsync` moves behind it with its new signature, and no HTTP path resolves the
   dispatcher's store directly. The tenant lifecycle test reads the existing lifecycle
   projection the outbox role already holds.
5. **Result audit.** A new witness kind, not the terminal witness, is minted from the
   fenced `UPDATE ... RETURNING` on the same outbox-role transaction and writes the row
   with the real event tenant, the closed reason slug, `metadata.operator` and
   `metadata.fence` (before and after). The terminal witness demands the system actor, a
   null reason and no metadata, so it cannot carry an operator. A forced audit failure
   rolls the transition back and answers 503 with no state change. A stale fence answers
   409 `stale_fence`, a refused state 409 `not_recoverable`, and neither writes a row or
   changes state.
6. **Not admitted: job recovery.** No job requeue, retry, probe re-arm, reservation or
   capacity release exists. A quarantined job stays frozen, retains its reservation and
   keeps `jobs` unhealthy, exactly as
   [ADR-0047](0047-transactional-background-job-runtime.md) says. The trigger to admit
   one is the first external definition whose adapter can supply provider fencing or
   process-tree termination evidence; none can today, and the phase that ships such an
   adapter owns the decision, with
   [Phase 11](../roadmap/phase-11-production-hardening.md) owning operational
   reconciliation. For the invitation path, reissue helps only while a tenant slot is
   free: it does not reconcile or release the old effect
   ([ADR-0093](0093-verified-invitation-acceptance-and-handoff.md)), and four unresolved
   effects can hold every slot with no operator remedy. That limit is stated, not
   softened.
7. **Not admitted: recurrence recovery.** No recurrence, occurrence or recipient skip,
   abandon, pause or resume exists. No production recurrence exists; Phase 11 owns it.
8. **Dashboard.** `/admin/hangfire*` stays unmapped and answers 404 in every deployment
   mode, and the catalogue's dashboard-absence rule stays. The inspection set is the
   operator's view. A dashboard write control is never admitted. A read-only dashboard
   needs its own decision on authentication, the dependency and the source borrower.
9. **Erasure.** The Active-tenant test in § 2 and § 3 is the guard until P03-10 (G16)
   defines a replay floor after erasure, and P03-10 re-verifies these transitions at its
   first writer. The runbook states the limit.
10. **Signals and runbook.** Counters record each operation by outcome and fence
    refusals separately, so a monitoring owner can see a replay storm.
    `docs/runbooks/operator-recovery.md` covers the signal, inspection, the
    replay-versus-reset choice, what is not recoverable, the invitation limit and how to
    verify that the row became due and the real executor completed it.

## Context

The existing replay is correct in shape and unsafe in use: it checks the row state and
nothing about which state the operator saw. A fence token is the smallest change that
makes a second operator's stale decision refuse instead of act. Inspection moves to the
platform scope because it is the only act that reads across tenants, and column grants
make the projection a database fact with the scope's entry row as its audit. The
transitions stay on the outbox role because they cross no tenant and need no new write
grant.

The producer side is admitted narrowly because its one recoverable case is real. A burst
of dispatch failures longer than the six-attempt budget strands an event with no remedy,
and acceptance is idempotent by stable identity, so a deliberate redispatch is safe.

Job recovery is left closed because the closing condition is evidence the runtime cannot
get from an operator. That is a real limit, not a deferral for convenience.

The dashboard is the one place this ADR departs from the roadmap's wording of D13. The
roadmap says the dashboard is admitted behind the Platform gate. The findings above show
that the gate cannot make it safe: authentication is the wrong shape, and its controls
write around the fences. The inspection set gives the operator the same facts in a
contract this repository owns and tests.

What would reopen this: a Hub or Phase 11 need for a read-only dashboard, a provider
adapter that supplies quiescence proof, or a measured need to re-arm reconciliation.

## Consequences

### Positive

- C15's stale-fence, sibling, producer-versus-subscriber and audit proofs have real
  subjects, and the projection is enforced by grants.
- An operator sees exactly what a transition will act on, and a stale decision refuses.
- ADR-0015's false guidance is withdrawn with a dated disclosure.

### Negative

- A column-grant migration on a `BYPASSRLS` role, and a second outbox status writer.
- The operator has no dashboard, only an API and a runbook, until a UI exists in the Hub
  operator portal.
- A stuck external job has no operator remedy, which is
  [ADR-0047](0047-transactional-background-job-runtime.md)'s rule restated, not
  softened.
- The producer reset renews the retry budget, and the runbook says so.

### Neutral

- [Phase 11](../roadmap/phase-11-production-hardening.md)'s statements that producer
  replay and job requeue exist are rewritten as conditional on this ADR.

## Implementation Notes

- Step 4 delivers the grants migration with the canonical SQL, the owner, routes,
  fences, the two witness kinds, census edits, the ADR-0015 and ADR-0046 disclosures and
  the runbook. The grant expectations and the delivery-boundary signature census change
  with the code.
- Proof: a replay with a current fence completes and the real executor runs once; a
  stale fence, a completed, live, pending, privileged, retired, `invalid_envelope` and
  inactive-tenant delivery each refuse; the sibling, another tenant, another event, the
  inbox and the producer row are byte-identical; a reset of every other reason and of a
  processed row refuses; a forced audit fault leaves no change; a tenant token, a
  wrong-purpose token and a different Platform permission fail before any credential is
  resolved; the platform role cannot select an excluded column; `/admin/hangfire` is 404
  in every mode; quarantined job rows are byte-identical after every act. The mutation
  targets are the fence comparison, the reason filter, the tenant test and the result
  row.

## Carriers

Dated disclosures in [ADR-0015](0015-api-gateway-apisix.md) (Amendment 2: the realm-role
gate bullet quoted and withdrawn, the route stays absent),
[ADR-0046](0046-durable-event-delivery-and-recovery.md) (the replay and reset contract
and the second writer), [ADR-0047](0047-transactional-background-job-runtime.md)
(operator reconciliation is inspection only, and the platform column grants),
[ADR-0044](0044-audit-write-path.md) (the two witness kinds).
[Phase 11](../roadmap/phase-11-production-hardening.md)'s replay and requeue statements,
Standards 05 and 20, the Tenancy audit matrix, the Phase 03 D13 wording, and the stray
fragment in the checklist of [Architecture 30](../architecture/30-api-gateway.md)
follow.

## References

- [ADR-0101](0101-platform-operator-authority.md),
  [ADR-0102](0102-production-tenant-onboarding.md)
- [ADR-0015](0015-api-gateway-apisix.md),
  [ADR-0046](0046-durable-event-delivery-and-recovery.md),
  [ADR-0047](0047-transactional-background-job-runtime.md),
  [ADR-0058](0058-bounded-stock-hangfire-maintenance.md),
  [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md)
- [Event and Outbox](../architecture/15-event-and-outbox.md),
  [Data Protection](../architecture/23-data-protection.md)
- [Phase 03](../roadmap/phase-03-identity-admin.md): G14, D13, C15

## Amendment 1 — Implementation contract of the recovery owner (2026-11-20)

Step 4 of the platform-authority packet implements the decision. Each item below fixes
something the Decision left open or states where the code meets it; items 2, 3 and 4
correct a statement the code could not satisfy as written. None of them changes the
Decision's choice of option, its two transitions, its permissions or its refusal of job
recovery.

1. **Where the code lives.** The owner is `OperatorRecovery`, a singleton behind
   `IOperatorRecovery`. It holds the two dispatcher stores and no credential, so the
   credential census gains no reader and no keyed binding. `ReplayAsync` stays on
   `DeliveryDispatchStore` with its new signature, and `ResetAsync` is new on
   `OutboxDispatchStore`; both are partial-class files, and neither store's claim, fail
   or terminalize code changed. Each store gained a third connection slot, reserved for
   the operator lane: the dispatcher's own two slots (the main owner and its
   uncertain-COMMIT reconciliation) are never taken by an operator, a second concurrent
   operator call on a store is refused as unavailable instead of waiting, and the
   physical-capacity ledger grows by the two lanes, from 52 to 54 for the default
   topology and from 158 to 160 for the maximum.
2. **The platform role already read `outbox_messages` whole.** § 1 says a migration
   gives the role `SELECT` on named columns so that the database refuses the rest. The
   role held table-wide `SELECT` and `DELETE` there since the first platform migration,
   so a column grant beside it would exclude nothing. The migration revokes the
   table-wide `SELECT`, grants twelve named columns, keeps `DELETE` for the retention
   purge that Phase 11 owns (its predicate reads only granted columns), and grants named
   columns on the other three tables: fifteen on `event_deliveries`, twenty-seven on
   `background_jobs` and every column of `background_job_reservations`. No envelope,
   parameter, plan, outcome, causal, trace, lease-token or operation-key column is among
   them. It adds two partial indexes on the dead-lettered rows, because no index served
   that scan. The canonical SQL is in Database Standards, and the platform-role tests
   that read `correlation_id` moved to the owner and dispatcher roles.
3. **One witness class with two kinds, and no audit DDL.** § 5 says a new witness kind,
   and the carrier disclosures say two. The code has one sibling class,
   `OperatorRecoveryTransition`, whose protected minting seam is subtyped only by a
   private nested class in each of the two stores and whose two factories are the replay
   and the reset. It is not a third factory on the terminal witness, whose census pins
   two. One new `IAuditStore` method, `WriteOperatorRecoveryAsync`, writes the row,
   which makes fourteen writes. The outbox role already holds `INSERT` on `audit_log`
   and bypasses row security, so no policy arm or grant changed: the binding of the row
   to the real event tenant is the witness's, which composes the exact `metadata` (the
   operator attribution, the event and subscription, and the fence before and after) and
   refuses a draft that differs.
4. **The error vocabulary.** The API's status map is closed and answers an unknown
   top-level code with `500`, so `stale_fence` and `not_recoverable` are not codes. A
   stale fence is `409` `business_rule_violation` with the reason `lockey_stale_fence`
   on the field `fence`, and a refused state is `409` with `lockey_not_recoverable` on
   the field `state`, as the onboarding acts already answer. A missing row is `404`. A
   MUST row that cannot be written, an unavailable lane or database, and a commit whose
   answer was lost are `503` with `Retry-After`, the last with its own reason
   `outcome_unknown`.
5. **"The event's tenant is Active."** The test is the lifecycle projection the
   dispatcher role already reads, read in the same transaction as the reset: the tenant
   is not deleted, has its default organization and is Trial or Active, which is the
   serving-tenant meaning the job runtime uses. Suspended and Archived tenants are
   refused. A producer reset also refuses a row whose stored type the registry has since
   retired or no longer knows, because a reset would only dead-letter it again with a
   second MUST row.
6. **Inspection.** Each page is one entered platform scope on the platform role's column
   grants, so the scope's entry row, which names `platform.delivery.inspect` and carries
   the ticket and the operator's attribution, is the audit. The scope reads and changes
   nothing, so it writes no result row and ends by disposal; the slug is still declared
   off-path, because the scope refuses an operation that is not. Pages are keyset
   cursors bound to their resource and state, 1 to 100 rows, and a cursor carries no
   authority. The fence timestamp is the stored microsecond, returned and compared
   exactly.
7. **An unknown COMMIT is not reconciled automatically.** A reset and its row commit
   together, so a lost answer leaves nothing to repair: the operator inspects, and the
   fence of a landed reset has moved, so a repeat answers `409` on the fence. The
   runbook says so.
8. **Signals.** `learnstack_platform_operator_recoveries_total{operation,outcome}`
   counts each transition as applied, stale fence, not recoverable, not found,
   unavailable or commit unknown, so a replay storm is visible.

The Decision, Status, Date and prior amendments of this record are unchanged.
Acceptance of the linked records is not proof that anything they decide is implemented.
