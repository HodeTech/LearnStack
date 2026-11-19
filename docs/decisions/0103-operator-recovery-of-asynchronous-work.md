# ADR-0103: Operator Recovery of Asynchronous Work

## Status

Proposed — 2026-11-19, for the maintainer's review. It closes no gate, changes no
Accepted record and authorizes no code until Accepted. Acceptance would close gate G14
and, with [ADR-0101](0101-platform-operator-authority.md), decide the dashboard
question that D13 raises.

**Date:** 2026-11-19
**Deciders:** @cemil

## Decision Drivers

- [ADR-0046](0046-durable-event-delivery-and-recovery.md) says "operator replay is Phase
  03, with a separately specified reset/fencing contract before any endpoint ships".
  `DeliveryDispatchStore.ReplayAsync` exists and has no production caller. It takes no
  actor, reason or stale-state token, and writes no audit row, so a second operator can
  reset a row a first operator already re-terminalized.
- [ADR-0047](0047-transactional-background-job-runtime.md) keeps a quarantined job
  frozen for every role by trigger, retains its reservations, and releases capacity only
  on confirmed completion, no-start or quiescence. Operator authority, elapsed time and
  a missing worker are not that proof. The only external definition today, invitation
  email, ends in `Unknown`, which no operator can turn into proof.
- `learnstack_platform` holds no privilege on inbox, delivery or job tables
  ([Data Protection](../architecture/23-data-protection.md)), and the outbox role is
  confined by census to the dispatcher. A recovery owner needs a named principal and no
  silent widening.
- The Hangfire dashboard needs a browser session on an API with none
  ([ADR-0004](0004-authentication-strategy.md)). The runtime's storage runs on the
  privileged source, so a dashboard write control reaches past the job fences. The stock
  statistics are suppressed ([ADR-0058](0058-bounded-stock-hangfire-maintenance.md)), a
  vendor "Succeeded" is not business success, and the runtime pins no dashboard package.
  [ADR-0015](0015-api-gateway-apisix.md) still guides a backend filter on the realm role
  `platform-admin`, which [Standard 19](../standards/19-permissions.md) forbids.
- C15 requires dashboard and recovery to remain absent until admitted, and then proof of
  positive and denied routes, stale fences, producer-versus-subscriber separation,
  unchanged siblings, quiescence and reservations, MUST result audit and usable
  runbooks.

## Considered Options

1. **A fixed inspection and recovery set on a confined outbox-role owner, no dashboard**
   (chosen). Every transition carries a stale-state fence, an operator admission and a
   same-transaction result row.
2. **Admit the Hangfire dashboard read-only** (rejected now). It adds a package, a
   browser session path and a second source borrower, and shows state the runtime says
   is not authoritative. Its read-only form shows less than the inspection set below.
3. **Run recovery in the platform scope with new platform grants** (rejected). It widens
   a `BYPASSRLS` role on tables that the corpus holds closed, and none of the chosen
   acts needs to cross tenants.
4. **Release or requeue quarantined jobs on operator attestation** (rejected). It
   supersedes [ADR-0047](0047-transactional-background-job-runtime.md)'s quiescence rule
   and would let an unfenced external start run again.
5. **Admit nothing and ship a runbook** (rejected). An exhausted dead letter would have
   no remedy, and C15's stale-fence and sibling proofs would have no subject.

## Decision

LearnStack admits three operator transitions and a metadata-only inspection, and no
dashboard.

1. **Inspection.** `platform.delivery.read` returns cursor-paged metadata for
   dead-lettered or terminal-requested outbox rows, terminal delivery rows and
   quarantined jobs with their reservation occupancy. An explicit column list makes the
   projection free of envelope payloads, job parameters, plans and outcomes. Each row
   returns the fence its transition needs.
2. **Targeted subscriber replay.** `platform.delivery.write` resets one
   `(tenant, subscription, event)` delivery. The request carries the fence read at
   inspection: `replay_count`, `dead_lettered_at` and `terminal_reason`. The reset is
   refused unless those still match, the row is dead-lettered and unleased, and the
   subscription is an Active ordinary one. A completed delivery, its inbox row, every
   sibling delivery and the producer row are untouched, and a privileged, unknown or
   retired subscription is refused.
3. **Narrow producer reset.** The same permission resets a dead-lettered outbox row
   whose `terminal_reason` is `retry_exhausted` and which is not processed, under the
   fence `(attempts, dead_lettered_at)`. It clears the terminal fields and the lease,
   resets attempts and sets `available_after` to now. `invalid_envelope`, `unknown_type`
   and `retired_type` are refused, because an immutable envelope cannot become valid.
   The reset deliberately renews the six-attempt budget. A publication that already
   reached a subscriber is absorbed by stable event-id acceptance
   ([ADR-0046](0046-durable-event-delivery-and-recovery.md): terminal state does not
   prove that no subscriber accepted), and the next dispatch reaches every subscription
   that is Active now. Producer reset is not a power of subscriber replay: the two are
   separate requests with separate fences, and neither writes the other's table.
4. **Principal.** A new confined owner uses the outbox-role credential behind an
   [ADR-0101](0101-platform-operator-authority.md) admission. No platform scope is
   entered and no grant is added.
   `LearnStack_OutboxAdmin_Role_OnlyUsedBy_OutboxProcessor` gains exactly that owner,
   `ReplayAsync` moves behind it with its new signature, and no HTTP path reaches the
   dispatcher's store.
5. **Result audit.** The row is minted from the fenced `UPDATE ... RETURNING` witness,
   the way a terminal witness is, on the same outbox-role transaction. It carries the
   real event tenant, the closed reason slug, `metadata.operator` and `metadata.fence`
   (before and after). A forced audit failure rolls the transition back and answers 503
   with no state change. A stale fence answers 409 with no change and no row.
6. **Not admitted: job recovery.** No job requeue, retry, probe re-arm, reservation or
   capacity release exists. A quarantined job stays frozen, retains its reservation and
   keeps `jobs` unhealthy, exactly as
   [ADR-0047](0047-transactional-background-job-runtime.md) says. The trigger to admit
   one is the first external definition whose adapter can supply provider fencing or
   process-tree termination evidence. Its owning phase is the one that ships that
   adapter, [Phase 08a](../roadmap/phase-08a-assessment-notifications.md) for
   notification providers, with [Phase 11](../roadmap/phase-11-production-hardening.md)
   owning operational reconciliation. The invitation path's remedy stays the reissue of
   [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md).
7. **Not admitted: recurrence recovery.** No recurrence, occurrence or recipient skip,
   abandon, pause or resume exists. No production recurrence exists; Phase 11 owns it.
8. **Dashboard.** `/admin/hangfire*` stays unmapped and answers 404 in every deployment
   mode, and `BackgroundJob_Runtime_Has_No_Production_Definitions_Or_Dashboard` stays.
   The inspection set is the operator's view. A dashboard write control is never
   admitted. A read-only dashboard needs its own decision on authentication, the
   dependency and the source borrower.
9. **Erasure floor.** No replay floor after erasure is invented here. P03-10 (G16) owns
   it and re-verifies these transitions at its first writer. Until then the runbook
   forbids replaying an event of a tenant in erasure.
10. **Runbook.** `docs/runbooks/operator-recovery.md` covers the signal, inspection, the
    replay-versus-reset choice, what is not recoverable, the invitation reissue path and
    how to verify that the row became due and the real executor completed it.

## Context

The existing replay is correct in shape and unsafe in use: it checks the row state and
nothing about which state the operator saw. A fence token is the smallest change that
makes a second operator's stale decision refuse instead of act. The producer side is
admitted narrowly because its one recoverable case is real. A burst of dispatch failures
longer than the six-attempt budget strands an event with no remedy, and acceptance is
idempotent by stable identity, so a deliberate redispatch is safe. The three invalid
reasons are not recoverable by any operator and are refused by the same code that proves
the fence.

Job recovery is left closed because the closing condition is evidence the runtime cannot
get from an operator. That is a real limit, not a deferral for convenience: with a lost
SMTP reply the four tenant invitation slots can stay occupied, and the remedy lives in
the invitation contract. Admitting an attested release would reopen
[ADR-0047](0047-transactional-background-job-runtime.md) and is a decision for the phase
that has a provider able to prove quiescence.

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
  subjects, with no new grant and no widened bypass role.
- An operator sees exactly what a transition will act on, and a stale decision refuses.
- ADR-0015's false guidance is withdrawn with a dated disclosure.

### Negative

- The operator has no dashboard, only an API and a runbook, until a UI exists in the Hub
  operator portal.
- A stuck external job has no operator remedy beyond domain-level reissue, which is
  [ADR-0047](0047-transactional-background-job-runtime.md)'s rule restated, not
  softened.
- The producer reset renews the retry budget and may deliver a historical event to a
  subscription activated since. The runbook states both.

### Neutral

- [Phase 11](../roadmap/phase-11-production-hardening.md)'s statements that producer
  replay and job requeue exist are rewritten as conditional on this ADR.

## Implementation Notes

- Step 3 delivers the owner, routes, fences, the two witness-bound seams, census edits,
  the ADR-0015 and ADR-0046 disclosures and the runbook. The pinned grant expectations
  and the `DeliveryDispatchBoundaryTests` signature census change with the code.
- Proof: a replay with a current fence completes and the real executor runs once; a
  stale fence, a completed, live, pending, privileged and retired delivery each refuse;
  the sibling, another tenant, another event, the inbox and the producer row are
  byte-identical; a reset of each invalid reason and of a processed row refuses; a
  forced audit fault leaves no change; a tenant token, a wrong-purpose token and a
  different Platform permission fail before the outbox credential is resolved;
  `/admin/hangfire` is 404 in every mode; quarantined job rows are byte-identical after
  every act. The mutation targets are the fence comparison, the reason filter and the
  result row.

## Carriers owed at acceptance

Dated disclosures in [ADR-0015](0015-api-gateway-apisix.md) (Amendment 2: the realm-role
gate bullet quoted and withdrawn, the route stays absent),
[ADR-0046](0046-durable-event-delivery-and-recovery.md) (the replay and reset contract),
[ADR-0047](0047-transactional-background-job-runtime.md) (operator reconciliation is
inspection only), [ADR-0044](0044-audit-write-path.md) (the two witness kinds).
[Phase 11](../roadmap/phase-11-production-hardening.md)'s replay and requeue statements,
Standard 20's dashboard line, the Phase 03 D13 wording and the dangling fragment in
[Architecture 30](../architecture/30-api-gateway.md) follow.

## References

- [ADR-0101](0101-platform-operator-authority.md),
  [ADR-0102](0102-production-tenant-onboarding-and-authority-rebind.md)
- [ADR-0015](0015-api-gateway-apisix.md),
  [ADR-0046](0046-durable-event-delivery-and-recovery.md),
  [ADR-0047](0047-transactional-background-job-runtime.md),
  [ADR-0058](0058-bounded-stock-hangfire-maintenance.md),
  [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md)
- [Event and Outbox](../architecture/15-event-and-outbox.md),
  [Data Protection](../architecture/23-data-protection.md)
- [Phase 03](../roadmap/phase-03-identity-admin.md): G14, D13, C15
