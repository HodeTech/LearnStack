# ADR-0066: Bounded Tenant Subject Admission

## Status

Accepted — maintainer approval on 2026-10-20. Existing code already retains at
most 4096 subjects; this record selects that bound explicitly rather than
claiming ADR-0064 selected it. Acceptance precedes dependent implementation;
the Phase 02b delivery record owns its runtime and proof status.

**Date:** 2026-10-20
**Deciders:** @cemil
**Relationship:** An additive process-wide admission bound to ADR-0064's subject
quota, with dated source-attribution corrections in ADR-0064 and ADR-0065.

## Decision Drivers

- A validated issuer can supply arbitrarily many distinct subjects. Unlike the
  defended-host-tenant audit keyspace, this keyspace has no deployment tenant
  inventory bound.
- Retained partitions own rate-limiter state and replenishment resources. Both
  retained keys and resources awaiting disposal need explicit bounds.
- Evicting a live partition can restore spent quota. Changing tenant, host,
  credential, token identifier or actor must not create a fresh subject budget.
- Refusing a previously unseen subject affects availability across tenants; it
  is an admission decision, not an invisible retirement implementation detail.
- Operators need to distinguish spent quota, temporary registry pressure and a
  sticky cleanup fault without exporting subject, issuer, tenant or credentials.

## Considered Options

1. **Keep a fixed 4096-partition process bound, with bounded idle reclamation and
   closed refusal signals** (chosen). Retains the existing memory/resource
   ceiling and explicitly accepts shared-instance availability costs.
2. **Keep an unbounded subject dictionary** (rejected). Valid signatures do not
   bound the number of subjects or retained per-subject resources.
3. **Evict a live least-recently-used subject** (rejected). Reacquisition can
   reset a still-spent quota and rewards subject churn.
4. **Partition by tenant or actor** (rejected). That changes ADR-0064's stable
   issuer/subject identity and lets a subject multiply quota by changing scope.
5. **Make the limit configurable now** (rejected for this packet). There is no
   measured supported sizing range. An arbitrary setting would move the same
   availability/resource decision into unqualified deployment configuration.
6. **Install distributed admission now** (rejected for this packet). Phase 11
   owns measured distributed/edge capacity; this change does not qualify its
   adapter or infrastructure early.
7. **Reserve registry shares per tenant or host** (rejected for this packet).
   This differs from changing the quota key, but needs tenant inventory,
   attribution for claimless subjects and an explicit fair-share policy. A
   tenant can still exhaust its share; arbitrary reservations would reduce
   usable capacity without measured evidence. Phase 11 owns that comparison.
8. **Dispose up to 128 entries on an acquiring request** (rejected). One retired
   entry suffices to seek one slot. Keep the 128-entry inspection bound but
   permit only one synchronous disposal on this path; background batches retain
   the existing 128-entry bound.
9. **Return 429 for a sticky cleanup fault** (rejected). Unlike ordinary quota or
   capacity pressure, this state cannot clear through replenishment or idle
   retirement. Report dependency failure separately, including a fault-transition
   signal when no new request arrives.

## Decision

LearnStack retains at most 4096 `(validated issuer, sub)` partitions per API
instance, shared across tenants and hosts. A new key at capacity is refused
before partition allocation unless a bounded idle-only reclamation pass frees
capacity. Existing live keys retain their original quota. Quota and capacity
refusals return 429; a sticky cleanup fault refuses new keys with 503. Closed,
identity-free results and metrics distinguish them. ADR-0064's 600-per-minute
subject quota and earlier visitor/physical-peer limits remain unchanged.

### Admission and retirement

- Production singleton registration uses `MaximumPartitions = 4096`. The
  internal constructor's smaller-bound parameter is a test seam, not a
  deployment setting or throughput entitlement.
- New-key acquisition at capacity examines at most 128 queued entries before
  refusal and retires at most **one** eligible limiter. Retire only entries with
  every permit available and at least one complete window of idle time. Rotate
  inspected survivors; stop at the first eligible entry. Mere entry age, a
  rejected request or registry pressure never restores spent quota.
- A fully eligible idle registry therefore releases a slot on the next
  new-subject acquisition; a request does not wait for all 32 background batches.
  If the inspected batch is live, capacity refusal remains possible even when an
  eligible idle entry exists later in the queue. Later acquisitions/background
  passes advance the queue; there is no unbounded scan or admission promise.
- The timer inspects at most 128 entries every 30 seconds and retires at most
  128. Replenishment and retirement clocks retain separate ownership; injected
  retirement time does not control the stock limiter's replenishment timer.
- Timer and acquisition share one retirement owner, reserved under the registry
  lock before removing entries. No second scan/batch begins until the owned
  cleanup actually returns. Dispose outside the lock. Other requests never wait
  for that owner: an existing key uses its quota, a new key can use an available
  slot, and a full registry immediately refuses the new key with capacity 429.
- The cleanup-owning acquisition removes its one eligible entry, completes its
  disposal, then reacquires the lock and rechecks shutdown, fault, key existence
  and capacity **before** allocating or debiting for its own request. A competing
  request may take the freed slot; the owner then receives capacity 429. It does
  not reserve a slot, loop for another batch, or debit on refusal.
- The 4096 bound counts retained keys, not limiters awaiting disposal. Freed
  slots can be reused by other requests while cleanup runs, so the maximum is
  4224 owned limiters: 4096 retained plus one timer batch of 128. Acquisition can
  own only one extra limiter. A blocked disposal cannot accumulate batches.
- The first handled disposal exception publishes a sticky registry fault under
  the lock. From that point, no new partition allocation or retirement batch
  starts, even if slots are free. Already retained keys continue using their
  original quotas; a partition allocated before fault publication is retained.
  The acquiring request whose cleanup failed returns fault 503 without its own
  allocation, debit or downstream dispatch, even if another request installed
  its key while cleanup ran. Previously admitted requests are not revoked.
- Attempt disposal of every member of a faulting timer batch. Retain failed
  resources and exceptions; a thrown disposal is not proof that its resource
  was released. Timer faults remain observed by the owner, not unhandled
  callback exceptions. Do not retry a failed disposal or start another batch.
  Joined shutdown observes the retained failure.

### Result and HTTP contract

The limiter returns a closed `SubjectAdmissionOutcome` together with detached
retry metadata: `Acquired`, `QuotaExhausted`, `RegistryCapacity` or
`RegistryFault`. This replaces reliance on an unclassified `RateLimitLease`;
the result owns no partition or child lease. Middleware caches the first result
in the request feature and emits any rejection metric once, before responding.
Pipeline re-execution neither reacquires quota nor records a second rejection.

| Result | HTTP code / body code | `Retry-After` |
|---|---|---|
| `Acquired` | Continue downstream | None from subject admission |
| `QuotaExhausted` | 429 / `rate_limited` | Stock quota hint, rounded to integer seconds, minimum 1 |
| `RegistryCapacity` | 429 / `rate_limited` | 60 seconds |
| `RegistryFault` | 503 / `dependency_unavailable` | Existing common 503 fallback: 30 seconds |

All refusals use the existing Problem Details/no-store path, create no tenant
resolution or audit work, and queue no admission. Hints are not recovery
promises; a sticky fault requires instance replacement, not a 30-second wait.
An existing retained key can still receive quota 429 during a registry fault.
The fault middleware explicitly applies the common 503 retry helper; the outer
bodyless-status writer alone does not apply that header.

Capacity 429 is an explicit rate-admission choice: no new operation entered the
application. It is not a claim that all resource-capacity refusals use 429.
Standards 04's idempotency-store capacity refusal stays 503 because that store
cannot safely retain an operation outcome. Cleanup fault is likewise an
unavailable local dependency and uses 503, preserving the common header rule.

### Blocking and shutdown

Acquisition cleanup is synchronous and can delay its owning request by **one**
limiter disposal with no wall-clock bound. Other requests do not join it.
A blocked timer batch likewise holds only its single retirement ownership;
requests can use free slots but cannot initiate another reclamation pass.

Both shutdown paths close all admission under the lock, join the retirement
timer and any acquisition-owned cleanup, then attempt disposal of every
remaining retained partition. Concurrent shutdown callers share that completion.
Collect handled failures and propagate them through the joined shutdown; do not
dispose the failed retirement batch again or drop its ownership evidence.
Shutdown is not internally timed out and can wait indefinitely on blocked
disposal. A host/deployment grace deadline may end the process without a cleanup
witness; it does not free resources or prove graceful completion. This decision
adds no fatal-process call, automatic restart owner or host-grace override.
Elapsed time or a timeout is never evidence that disposal completed.

### Signals and operational ownership

Emit `learnstack_tenant_subject_admission_rejected_total{reason}` with only
`quota`, `registry_capacity` or `registry_fault`. Only rejected decisions count,
once per request including re-execution; admitted requests do not increment it.
Also emit `learnstack_tenant_subject_cleanup_failed_total` without labels once
when the owner first publishes a sticky cleanup fault. The timer path therefore
signals a fault even without a refused request. The acquiring fault owner also
produces its normal single request-rejection signal. Later disposal failures in
that instance do not repeat the transition counter.

No issuer, subject, token, actor, tenant, host or correlation becomes a label.
No new per-refusal Warning stream or durable audit write is introduced. Retained
exceptions are not exported as metric values or raw provider diagnostics.

Registry-capacity refusal is not a failed identity-provider check or an audit
store failure. Existing liveness/audit health does not diagnose it. The counter
and transition counter are explicit local signals; they do not install mapped
readiness. Phase 11 owns alert routing, supported instance sizing, distributed
admission and deployment readiness before production exposure. Its production
runbook must distinguish three branches: spent quota, live capacity pressure and
sticky cleanup fault. Replenishment/idle retirement can resolve the first two;
they cannot clear the third. Only the fault branch recommends qualified instance
replacement for recovery, retaining its shutdown limits. Do not restart or evict
live keys to replenish quota or evade ordinary capacity admission.

### Why 4096, and when to reopen

4096 is a conservative retained-resource ceiling already implemented in the
reviewed code, not a measured concurrency target or proof of production fit.
This record does not invent a memory-per-partition estimate. The earlier HTTP
limits still apply; 4096 partitions grant neither simultaneous users nor
4096 times 600 requests of end-to-end capacity.

Many valid subjects can occupy all live slots and deny first requests from
other legitimate subjects on the same instance, including other tenants. A
subject does not receive a reserved slot. Idle reclamation cannot solve a live
registry; multiple instances have independent state and multiply resources and
admission. These are accepted costs of this decision.

Reopen before a deployment needs more than 4096 retained subjects per instance,
or expected legitimate traffic produces capacity refusal. Phase 11 measures
the actual limiter resources and traffic/retirement pattern before selecting a
different bound, a supported configuration range or distributed admission.

## Context

ADR-0064 selects stable subject identity, 600 requests per fixed minute and safe
bounded retirement; it does not select a partition count. Later implementation
adds 4096, and ADR-0064 Amendment 1 and ADR-0065 refer to it as if ADR-0064 had
selected it. Code presence and those source-attribution statements are not a
substitute for the missing decision.

The existing timer examines 128 entries every 30 seconds. Scanning 4096 already
eligible idle entries can take 32 callbacks, up to about 16 minutes after they
become eligible. This proposal adds bounded acquisition-path reclamation while
preserving actual cleanup ownership; elapsed timeout is never cleanup evidence.

This is an additive decision, not a change to the 600 quota, and not a claim that
the missing bound was already decided. Acceptance adds dated disclosures to
ADR-0064 and ADR-0065 and corrects their false source attributions through
ADR-0041's inline erratum mechanism, retaining original rationale and metadata.
The independent target-tenant audit allowance remains ADR-0065's decision.

## Consequences

### Positive

- Partition allocation and retained resources have a falsifiable ceiling.
- Live quota cannot be reset by cardinality pressure or scope changes.
- Eligible idle state can release capacity without a full timer rotation.
- Capacity, quota and sticky cleanup fault have distinct bounded signals.

### Negative

- One instance's occupied registry can deny other valid subjects and tenants.
- A bounded scan can still refuse while an unexamined idle entry exists.
- 4096 is not yet measured production sizing; distributed capacity remains open.
- Cleanup ownership adds bookkeeping; shutdown cannot abandon retired resources.
- Cleanup can retain up to 128 extra limiter resources, and a cleanup fault can
  stop new subject allocation until the instance is replaced.
- One synchronous acquisition disposal can block that request indefinitely;
  joining it or a blocked timer disposal can also block shutdown indefinitely.
  Limiting counts does not bound disposal latency. Existing keys and free slots
  retain service until admission is closed or a sticky fault blocks allocation.
- Other callers may consume a freed slot before its cleanup-owning request can
  acquire it. A returned retry hint promises neither capacity nor fault recovery.

### Neutral

- No role, grant, schema, bearer profile, membership or permission changes.
- No tenant `AuditConfig`, deployment-configurable cap or new audit row; no
  subject/tenant/issuer identity becomes a metric label.

## Implementation Notes

Acceptance updates ADR-0064/0065 with their own dated disclosures and
inline attribution errata; update the decision index, Standards 04/10/11/21,
Architecture 13/33, glossary, phase roadmap, `CLAUDE.md`, roadmap README and
source ownership comments. Add ADR-0066 to the deriving headers/sources;
mutable carriers distinguish acceptance from pending runtime proofs.
Include this ADR in implementation trailers;
do not rewrite historical commits. The
existing `Authenticated_Rate_Limits_Use_Validated_Subject` obligation gains the
named cap/signal/cleanup proofs in the same change as their actual test owners;
no new catalogue rule or Implemented count is inferred from acceptance.

Update the served and snapshot OpenAPI 503 descriptions for AccountSite and
public routes reached with a validated subject; also update their 429 retry
descriptions so a capacity hint does not promise replenished quota or available
space. Regenerate the SDK. The error shapes/statuses already exist, but the new
local-dependency cause and capacity-hint meaning need disclosure.

### Tests and proofs required before implementation closeout

These are pending implementation obligations, not tests already executed.
Extend the existing behavioural catalogue owner's Asserts/Source in the same
change as these real owners; do not mark nonexistent controls Implemented.

**Unit — `TenantSubjectRateLimiterTests`** in
`backend/tests/LearnStack.Tests.Unit/Api/Authentication/`:

- Actual production registration/default 4096, 4097th-subject refusal without
  allocation/debit, and preserved existing spent quota. Internal small bounds
  supplement, rather than substitute for, the default-bound proof.
- Acquisition reads at most 128 idle states, rotates survivors, retires at most
  one, and progresses past a first all-live batch on a later acquisition. Full
  idle state works without waiting for a complete timer rotation.
- Block acquisition disposal: its request stays pending; existing keys/free
  slots proceed, full-capacity requests promptly refuse and no second batch
  starts. No owning allocation/debit occurs before cleanup succeeds. Exercise
  lost-slot, same-key race and shutdown-between-cleanup-and-allocation controls.
- Block a full 128-limiter timer batch, refill 128 free key slots, and prove the
  4224 owned-resource ceiling. Additional requests cannot create another batch;
  acquisition cleanup separately owns at most one extra limiter.
- Throw from acquisition/timer disposal: publish fault before remaining batch
  cleanup, attempt every batch member, retain failed ownership, start no further
  batch or new allocation, and preserve existing-key quota service. The acquiring
  owner gets `RegistryFault` with no debit even if another caller added its key.
- Timer faults do not escape the callback; the transition counter increments
  once. Both shutdown forms join timer/acquisition cleanup, drain all remaining
  partitions, observe retained failure, and do not redispose faulted entries.
  Blocking controls release their gates in `finally` and have bounded test waits;
  a test timeout is not a production disposal guarantee.
- Middleware caches all four results, dispatches only `Acquired`, and records
  one rejection across re-execution, zero on admission, with exact retry hints.

**Integration — `AuthenticatedRateLimitTests.cs`** in
`backend/tests/LearnStack.Tests.Integration/`:

- Signed-token production pipeline with closed quota/capacity/fault results:
  429/429/503, exact `rate_limited`/`dependency_unavailable` Problem Details,
  no-store and Retry-After stock hint/60/30. Refusals reach neither resolution
  nor the standalone audit writer; a fault does not reject an existing healthy
  subject. Add a blocked-cleanup owner response/dispatch control.
- Actual OTLP `TenantAuthenticationDiagnosticCollector.MetricPoints` observes
  all three rejection reasons and the unlabelled once-only fault transition.
  Verify only `reason` on rejection points, no labels on transition points, no
  identity/canary values, no admission increment and no re-execution duplicate.
  `SubjectQuotaObservation` remains a downstream/log observer, not a substitute
  for exported metric evidence.
- Served/snapshot contracts and generated SDK remain equal with the new 503
  cause; existing reduced-quota whole-chain and actual-600 component proofs keep
  their separate meanings. Dirty controls must fail the intended predicate.

## Amendments

### Amendment 1 — implementation and local proof ownership (2026-10-20)

The accepted contract is implemented by the API singleton, closed detached
admission decision, request-cached middleware and identity-free metrics. The
existing catalogue obligation gains its actual unit and signed PostgreSQL/OTLP
owners in the same change; served/snapshot OpenAPI and the generated SDK disclose
the capacity hint and sticky cleanup-fault cause. No policy in this ADR changes.
Its Accepted body, date and deciders remain intact.

The [implementation record](../roadmap/phase-02b-events-auth.md#adr-0066-acceptance-and-implementation-2026-10-20)
owns exact local results, two fresh implementation review rounds and final-source
hosted qualification. Local controls do not establish production sizing, latency,
mapped readiness or an exercised Phase 11 runbook.

## References

- [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0065](0065-bounded-tenant-assertion-diagnostics.md),
  [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md),
  [ADR-0053](0053-trusted-public-server-rendering.md),
  [ADR-0054](0054-bounded-public-renderer-admission.md).
- [API Design](../standards/04-api-design.md),
  [Observability](../standards/10-observability.md),
  [Security](../standards/11-security.md),
  [Documentation](../standards/13-documentation.md),
  [Architecture test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Identity architecture](../architecture/13-identity-and-auth.md),
  [Cross-cutting concerns](../architecture/33-cross-cutting-concerns.md),
  [Glossary](../glossary.md),
  [Phase 02b](../roadmap/phase-02b-events-auth.md),
  [Phase 11](../roadmap/phase-11-production-hardening.md).
- [Current limiter](../../backend/src/LearnStack.Api/Authentication/TenantSubjectRateLimiter.cs)
  and [middleware](../../backend/src/LearnStack.Api/Authentication/TenantSubjectAdmissionMiddleware.cs);
  [Common 503 retry policy](../../backend/src/LearnStack.Api/Common/RetryAfter.cs).
