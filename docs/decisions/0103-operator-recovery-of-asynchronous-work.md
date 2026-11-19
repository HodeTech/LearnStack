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
