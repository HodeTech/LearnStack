# ADR-0074: Membership Admission Under Ordered Locks

## Status

Accepted — 2026-10-26, after the maintainer's conditional authorization and
verified review corrections. Dependent Membership implementation/proofs remain
P03-1 Step 4; acceptance alone installs no contextual function or writer.

**Date:** 2026-10-26
**Deciders:** @cemil
**Canonical contract change:** Membership eligibility-read timing in Standard 05,
whose algorithm is delegated by
[ADR-0072](0072-application-owned-identity-bootstrap.md) and
[ADR-0073](0073-scoped-membership-and-identity-data-foundation.md).
The conflicting clause is in Standard 05; those ADRs delegate its algorithm.
Their dated Amendments are navigation, preserving their decisions and bodies.
User-before-tenant lock acquisition and the five-owner-function set remain unchanged.

## Decision Drivers

- A Membership and its assignments need their strongest complete lock set before
  mutation; a new Tenant Admin assignment requires exclusive tenant mode.
- The admitted User eligibility reader is a real BEFORE trigger. A new parent
  row alone cannot reveal its not-yet-persisted child assignments.
- Nonadministrator writes retain shared tenant mode; unconditional exclusive
  locking or shared-to-exclusive upgrades would change the approved contract.
- An additional callable User reader would enlarge owner execution and expose
  another global-state probe. The existing five functions should suffice.

## Considered Options

1. **Prehold User fences, then tenant fences; check eligibility in the actual
   admission trigger before mutation** (chosen). Stable User ownership permits
   the fresh check after tenant acquisition without another reader capability.
2. **Add a callable pre-mutation User eligibility reader** (rejected). It makes
   the original read timing implementable but adds a capability/signature and
   caller/projection/privacy controls; the actual trigger supplies the required
   admission check without that additional global-state probe.
3. **Let the parent trigger acquire all locks before planning children**
   (rejected). The parent cannot infer a future administrator assignment. Always
   selecting exclusive mode loses the adopted nonadministrator concurrency;
   selecting shared mode can require a forbidden upgrade.
4. **Write the graph before locking, or use a synthetic trigger probe**
   (rejected). The first changes the pre-mutation guarantee; the second adds an
   unadmitted preparation mode instead of checking the real mutation.

## Decision

Membership creation, Suspended-to-Active transitions and every assignment addition
acquire their required exclusive User fences before their complete strongest
tenant fences and before data-mutating statements. The actual admission trigger
verifies the actual target's currently held exclusive fence and freshly checks
eligibility before permitting mutation; the eligibility read can follow tenant
acquisition. Missing ownership refuses without acquiring a User key in the
trigger. Other Membership mutations retain Standard 05's existing lock requirements.

## Context

Verified against `111ca113`: global storage is implemented; contextual writers
and the two Membership owner functions remain P03-1 Step 4. The canonical
[function contract](../standards/05-database.md#confined-owner-functions) says the
Membership trigger locks/rechecks User before tenant locking. The
[ordered-lock contract](../standards/05-database.md#lock-snapshot-and-adoption-conditions)
also requires the complete strongest lock set before mutation and derives mode
from actual transitions/protected capabilities, not caller labels.

The admitted `CreateFirstSeedAdminMembership` command creates a Membership and
contained administrator assignment under the
[finite manifest](../standards/19-permissions.md#exact-foundation-registry-and-operation-owners).
Its absence-of-history condition permits an exclusive first-admin plan. A second
administrator is a fixture-only proof subject, outside the four-person seed
manifest; no second-admin production command is added by this decision.
That case exposes the general parent/child issue: the application can plan its
graph from actual referenced Role rows, but the parent trigger cannot read an
assignment that is not stored yet. Locking the
User and exclusive tenant first meets the complete-plan rule, while the first
permitted global eligibility read then occurs in the trigger. Entering the
trigger first cannot generically choose the future child plan's tenant mode.
The five owner functions supply no ordinary pre-mutation User reader.
`fn_identity_admin_admission_state` only checks a stored candidate Membership in
an admitted final-admin evaluation and takes no User lock; using it for arbitrary
new targets would broaden that contract.

The proposal separates acquisition order from check timing. The transaction holds
the target's exclusive admission fence continuously through check and commit.
A cooperating closure writer either commits before that fence is acquired or
waits until this transaction ends. The fresh trigger read therefore observes a
nonadmitting predecessor or a state protected against that concurrent writer.
This is a design argument; no contextual implementation/race proof exists yet.

PostgreSQL's transaction advisory locks end with their transaction. READ COMMITTED
and fresh queries inside VOLATILE functions support the required post-wait
observation. These primitives do not prove the application's correct plan/body.
See the PostgreSQL 18
[advisory-lock documentation](https://www.postgresql.org/docs/18/explicit-locking.html#ADVISORY-LOCKS)
and [function-snapshot documentation](https://www.postgresql.org/docs/18/xfunc-volatility.html).

The trigger uses the exact current-backend/database exclusive bigint-key
observation in [Standard 05](../standards/05-database.md#membership-fence-checks),
through `pg_catalog.pg_locks`; it uses no acquisition or try-lock function to
test ownership. The view cannot distinguish session from transaction ownership,
earlier acquisition order or future lifetime. Session-only ownership can satisfy
that SQL observation; it is not an admitted writer replacement. Transaction-only
acquisition and retention are separate caller/source/receipt and completion-test
obligations. Nor does the view authenticate a CLR caller or freeze the entire
cluster. These limits and collection costs are documented by
[PostgreSQL](https://www.postgresql.org/docs/18/view-pg-locks.html).

Exclusive User mode preserves the already approved per-User admission
serialization. Shared ownership could exclude an exclusive closure writer, but
this decision does not change the stronger existing contract. A change to shared
admission would need its own contention/duplicate-plan decision and proofs.

Reopen if a real operation cannot determine its complete target family before
mutation, needs a new global reader, or cannot retain the User fence through
completion. A closure writer that ignores this fence is not made safe by the
read's timing; P03-10 must implement and prove its side before activation.

## Consequences

### Positive

- Complete root/child plans and shared nonadministrator concurrency coexist with
  the existing trigger-only eligibility authority.
- No new definer function, grant, table, User projection or HTTP surface follows.
- The trigger checks the actual target and refuses unknown/nonadmitting targets
  uniformly before they can mutate Membership state.

### Negative

- An ineligible target may hold a tenant fence until the fresh trigger check
  refuses. That cost moves after tenant acquisition; existing deadlines remain.
- Each admission row observes `pg_locks`; repeated collection can contend with
  PostgreSQL's lock manager. Step 4 measures actual trigger-path/query/pool cost
  within the existing write-p95 budget, including representative contention.
  This document supplies no latency or production-capacity result.
- Correctness depends on the complete plan, real exclusive User ownership and
  continuous transaction lifetime. Reentrant acquisition is not proof of the
  original order; source/caller and race controls remain necessary.

### Neutral

- Shared/exclusive classification, no upgrades, fresh post-wait reads, total-admin
  invariants, User lifecycle, privacy and seed-only authority are unchanged.
- Step 3's subject-key global owner is unaffected. P03-2/3 live/HTTP activation
  and P03-9/10 operational/closure ownership remain separate deliveries.

## Implementation Notes

Acceptance appends dated navigation Amendments to ADR-0072/0073, preserving their
bodies/status/dates. Standard 05 replaces the conflicting function-row timing,
owns the exact observation/refusal/lock/proof clauses and adds this ADR to its
derivation header. Standards 10/11/21 and Identity's specification carry their
signal/security/proof references. The catalogue extends Source/Asserts while
retaining Registered; the decision index moves this ADR to Active, and Phase 03's
Status/G3 plus delivery log record acceptance before dependent code. No prior
proposal/review record is rewritten.

Step 4 checks closed seed authority and scope lifecycle before lock acquisition;
P03-2 owns live permission enforcement. It uses an announced READ COMMITTED owner,
a plan derived from actual Role/parent facts and intended changes, sorted required
User acquisition for the admission operations above, then sorted
strongest tenant acquisition. A separate post-wait read revalidates classification;
changed/ambiguous plans refuse without upgrading. The existing real mutation
trigger verifies its actual target/fence and eligibility before permitting the
write. Complete-graph/admin validation and central MUST audit precede commit.
No precheck Boolean or caller-supplied scope label substitutes for these checks.

A missing/wrong/shared-only/foreign fence is a fixed internal plan fault; an
unknown/Closing/Closed target is an expected admission refusal. Both yield the
same fixed outward refusal without User-existence details. Only plan faults use
the existing lock-refusal `fault` signal; expected target refusal does not invent
an acquisition failure. Canonical classification belongs to Standard 05 and
finite, identity-free signal dimensions to Standard 10.

## Tests and Proofs

These extend the already Registered
`Identity_Membership_Invariants_Use_Fresh_Ordered_Locks`; acceptance is not an
Implemented status. Step 4's `IdentityMembershipTests` owns real app-role proofs:

- Hold the User fence, start a competing closure transition, observe its lock
  wait, commit Membership, then allow the fixture-only competitor to proceed.
- Commit a nonadmitting transition while a creator waits for the User fence;
  its later trigger check must freshly refuse. Retain an admitting clean twin.
- Exercise the actual first-seed-admin command and complete contained graph;
  keep second-admin subjects fixture-local.
- Create a second Tenant Admin and contained assignments under a preheld exclusive
  plan; create a learner under a shared plan. Missing/wrong/shared-only User
  fences under tenant authority refuse without acquiring a missing key.
- Observe a correct key held only by another backend, a two-integer namespace,
  signed keys and session-only ownership. The session control demonstrates the
  view's limit; source/caller controls reject session acquisition APIs and real
  transaction-end tests prove release. C#/SQL User and Tenant vectors agree.
- Profile changes and assignment removal acquire no automatic User fence.
  Plan-fault and target-refusal diagnostics differ internally with identical
  outward refusal and no identity-bearing metric dimensions.
- Preserve changed-classification, no-upgrade, two-admin retirement, shared/
  exclusive coexistence, incompatible-isolation, timeout/cancellation and MUST
  rollback controls, with actual PostgreSQL lock-wait observations.
- Prove exact five-definer bodies/ACL/callers and clean/dirty controls. Neither
  a generic User probe nor a synthetic trigger mode may appear.
- Measure actual per-row observation and mutation latency/p95, query/pool cost
  and contended behavior; no empty-view microbenchmark establishes API capacity.
- Exercise immediate admission followed by complete-graph constraint validation
  and failed MUST audit: neither admits a partial committed graph.

Closure competitors use disposable databases only: preserve the applied guard
and policy, narrowly disable `tg_users_no_update` and add a migration-owner-only
fixture WITH CHECK arm. The fixture owner takes the same User then affected
exclusive tenant fences for its fixed status transition. Restore guard/policy in
`finally`; verify exact restored body/ACL/trigger/policy census and that owner
UPDATE again refuses. A separate dirty/clean control proves the fixture's
temporary accommodation was effective and fully removed. App-role Membership
operations remain the subjects; neither superuser nor BYPASSRLS proves isolation.
No production lifecycle writer, grant or closure fulfillment is introduced.

## References

- [ADR-0072](0072-application-owned-identity-bootstrap.md)
- [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md)
- [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)
- [Identity storage and lock contract](../standards/05-database.md#identity-storage-contract)
- [Identity specification](../modules/identity/README.md)
- [Identity foundation permissions](../standards/19-permissions.md#identity-foundation-contract)
- [Identity lock signals](../standards/10-observability.md#identity-lock-signals)
- [Identity admission security](../standards/11-security.md#identity-admission-contract)
- [Ordered-lock proof](../standards/21-architecture-tests-catalogue.md#identity_membership_invariants_use_fresh_ordered_locks)
- [Phase 03](../roadmap/phase-03-identity-admin.md)
- [Phase 03 delivery log](../roadmap/phase-03-delivery-log.md)
