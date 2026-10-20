# ADR-0066: Bounded Tenant Subject Admission

## Status

Proposed — maintainer approval pending. Existing code already retains at most
4096 subjects; this record proposes that bound explicitly rather than claiming
ADR-0064 selected it. Approval precedes the dependent admission refinements.

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
- Operators need to distinguish subject-quota refusal from registry-capacity
  refusal without exporting subject, issuer, tenant or credential identities.

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

## Decision

LearnStack retains at most 4096 `(validated issuer, sub)` partitions per API
instance, shared across tenants and hosts. A new key at capacity is refused
before partition allocation unless a bounded idle-only reclamation pass frees
capacity. Existing live keys retain their original quota. Both exhausted quota
and unavailable registry capacity return the standard 429 response, no queue and
an integer `Retry-After`; their internal reasons remain distinguishable through
closed, identity-free metrics. ADR-0064's 600-per-minute subject quota and earlier
visitor/physical-peer limits remain unchanged.

### Admission and retirement

- `TenantSubjectRateLimiter.MaximumPartitions` is exactly 4096. This is a fixed
  platform invariant, not a tenant setting or a throughput entitlement.
- New-key acquisition at capacity examines at most 128 queued entries before
  refusal. Retire only entries with every permit available and at least one
  complete window of idle time. Mere entry age, a rejected request or registry
  pressure never restores spent quota.
- A fully eligible idle registry therefore releases a slot on the next
  new-subject acquisition; a request does not wait for all 32 background batches.
  If the inspected batch is live, capacity refusal remains possible even when an
  eligible idle entry exists later in the queue. Later acquisitions/background
  passes advance the queue; there is no unbounded scan or admission promise.
- Keep the existing bounded background retirement cadence. Replenishment and
  retirement clocks retain separate ownership; injected retirement time is not
  represented as control of the stock limiter's replenishment timer.
- Dispose retired limiters outside the registry lock. Shutdown closes admission,
  joins the timer and all in-flight retirement cleanup, then disposes remaining
  partitions. A returned lease is a detached snapshot and owns no partition.
- The 4096 bound counts retained keys, not disposal witnesses. Timer and
  acquisition share one retirement owner with at most one 128-limiter batch.
  No second batch begins until actual disposal completes. Freed key slots may
  be reused while cleanup runs, so at most 4224 limiters can remain owned:
  4096 retained plus 128 retiring. A blocked disposal cannot accumulate batches.
- A disposal error does not abandon later entries. Attempt cleanup for the whole
  owned batch, retain failure/ownership evidence, stop new partition allocation
  and further retirement batches, and propagate the failure through joined
  shutdown. The acquiring request that owns failed cleanup does not continue to
  business dispatch. Timer cleanup faults are observed by the same owner rather
  than escaping as an unhandled callback failure. No timeout proves disposal.
- Capacity refusal supplies a one-minute `Retry-After` hint. It does not promise
  that capacity exists when the client retries. Subject-quota refusal preserves
  the stock limiter's retry hint. Both retain the existing standard body.

### Signals and operational ownership

Emit `learnstack_tenant_subject_admission_rejected_total{reason}` with only
`quota` or `registry_capacity`. It counts the first subject-admission decision
once per request, including pipeline re-execution. No issuer, subject, token,
actor, tenant, host or correlation becomes a metric label. No new per-refusal
warning stream or durable audit write is introduced by this decision.

Registry-capacity refusal is not a failed identity-provider check or an audit
store failure. Existing liveness/audit health does not diagnose it. The counter
is its explicit local signal; Phase 11 owns alert routing, supported instance
sizing, distributed admission and deployment readiness before production
exposure. The production runbook distinguishes capacity pressure from spent
subject quota without recommending live eviction or a restart to replenish it.

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
admission. These are accepted costs if this proposal is approved.

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
the missing bound was already decided. On approval, append dated disclosures
to ADR-0064 and ADR-0065. Correct their false source attributions through
ADR-0041's inline erratum mechanism, retaining original rationale and metadata.
The independent target-tenant audit allowance remains ADR-0065's decision.

## Consequences

### Positive

- Partition allocation and retained resources have a falsifiable ceiling.
- Live quota cannot be reset by cardinality pressure or scope changes.
- Eligible idle state can release capacity without a full timer rotation.
- Capacity and quota refusal have distinct bounded operational signals.

### Negative

- One instance's occupied registry can deny other valid subjects and tenants.
- A bounded scan can still refuse while an unexamined idle entry exists.
- 4096 is not yet measured production sizing; distributed capacity remains open.
- Cleanup ownership adds bookkeeping; shutdown cannot abandon retired resources.
- Cleanup can retain up to 128 extra limiter resources, and a cleanup fault can
  stop new subject allocation until the instance is replaced.

### Neutral

- No role, grant, schema, bearer profile, membership or permission changes.
- No tenant `AuditConfig`, configurable cap, new audit row or metric identity.

## Implementation Notes

After approval, update ADR-0064/0065 with their own dated disclosures and
inline attribution errata; update the decision index, Standards 04/10/11/21,
Architecture 13/33, glossary, roadmap and source ownership comments. The
existing `Authenticated_Rate_Limits_Use_Validated_Subject` obligation gains the
named cap/signal/cleanup proofs in the same change as their actual test owners;
no new catalogue rule or Implemented count is inferred from this proposal.

Unit `TenantSubjectRateLimiterTests` owns the exact default 4096 constant,
allocation-before-refusal control, a 4097th-subject default-bound case,
preserved spent existing quota, bounded/live/idle scan distinctions, and
shutdown joining acquisition/timer retirement cleanup with blocking controls.
They also prove the single-batch/4224 total-resource bound and drain-all behavior
when one retired limiter's disposal throws; shutdown observes that failure.
Middleware/integration subject-admission proofs own the two closed reasons,
exact 429/retry headers and one signal across re-execution. Existing whole-chain
reduced-quota and component actual-600 proofs retain their stated limits.

## References

- [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0065](0065-bounded-tenant-assertion-diagnostics.md),
  [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md).
- [API Design](../standards/04-api-design.md),
  [Observability](../standards/10-observability.md),
  [Security](../standards/11-security.md),
  [Documentation](../standards/13-documentation.md),
  [Architecture test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Identity architecture](../architecture/13-identity-and-auth.md),
  [Phase 02b](../roadmap/phase-02b-events-auth.md),
  [Phase 11](../roadmap/phase-11-production-hardening.md).
