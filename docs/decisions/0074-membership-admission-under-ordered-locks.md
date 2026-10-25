# ADR-0074: Membership Admission Under Ordered Locks

## Status

Proposed — dependent Membership writers remain unimplemented.

**Date:** 2026-10-26
**Deciders:** @cemil
**Bounded supersession:** The Membership eligibility-read timing delegated by
[ADR-0072](0072-application-owned-identity-bootstrap.md) and
[ADR-0073](0073-scoped-membership-and-identity-data-foundation.md) to Standard 05.
User-before-tenant lock acquisition and the five-function capability set remain.

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
   caller/projection/privacy controls without a selected product need.
3. **Let the parent trigger acquire all locks before planning children**
   (rejected). The parent cannot infer a future administrator assignment. Always
   selecting exclusive mode loses the adopted nonadministrator concurrency;
   selecting shared mode can require a forbidden upgrade.
4. **Write the graph before locking, or use a synthetic trigger probe**
   (rejected). The first changes the pre-mutation guarantee; the second adds an
   unadmitted preparation mode instead of checking the real mutation.

## Decision

Membership writers acquire their complete exclusive User fence set before their
strongest complete tenant fence set. The actual admission trigger freshly checks
the target User before permitting mutation, including when those fences were
preheld before SQL dispatch. It requires the actual target's exclusive User key
to be held before entering under tenant authority; a missing key refuses without
acquiring a new User key beneath a tenant key. This narrowly replaces the earlier
requirement that the eligibility read itself precede tenant acquisition.

## Context

Verified against `111ca113`: global storage is implemented; contextual writers
and the two Membership owner functions remain P03-1 Step 4. The canonical
[function contract](../standards/05-database.md#confined-owner-functions) says the
Membership trigger locks/rechecks User before tenant locking. The
[ordered-lock contract](../standards/05-database.md#lock-snapshot-and-adoption-conditions)
also requires the complete strongest lock set before mutation and derives mode
from actual transitions/protected capabilities, not caller labels.

Consider a second Tenant Admin in an already initialized tenant. The application
can plan its root and child assignments from actual referenced Role rows, but
the parent trigger cannot read an assignment that is not stored yet. Locking the
User and exclusive tenant first meets the complete-plan rule, while the first
permitted global eligibility read then occurs in the trigger. Entering the
trigger first cannot generically choose the future child plan's tenant mode.
The original five capabilities supply no ordinary pre-mutation User reader.

The proposal separates acquisition order from check timing. The transaction holds
the target's exclusive admission fence continuously through check and commit.
A cooperating closure writer either commits before that fence is acquired or
waits until this transaction ends. The fresh trigger read therefore observes a
nonadmitting predecessor or a state protected against that concurrent writer.
This is a design argument; no contextual implementation/race proof exists yet.

PostgreSQL's transaction advisory locks end with their transaction, and a backend
can reacquire a lock it already owns. READ COMMITTED supplies a new snapshot to
each command. These primitives support the proposed ordering; they do not prove
the application's correct lock plan or trigger body. See the PostgreSQL 18
[advisory-lock documentation](https://www.postgresql.org/docs/18/explicit-locking.html#ADVISORY-LOCKS)
and [isolation documentation](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-READ-COMMITTED).

An already-owned key may be reacquired transactionally without waiting on a new
User key. A shared hold is insufficient for the required exclusive fence.
Session ownership alone does not replace the required transaction lifetime.
SQL checks cannot reconstruct arbitrary earlier lock history or authenticate a
CLR caller: the existing declared source/caller controls and compromised-SQL-
credential boundary remain part of Standard 05's model.

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
- Correctness depends on the complete plan, real exclusive User ownership and
  continuous transaction lifetime. Reentrant acquisition is not proof of the
  original order; source/caller and race controls remain necessary.

### Neutral

- Shared/exclusive classification, no upgrades, fresh post-wait reads, total-admin
  invariants, User lifecycle, privacy and seed-only authority are unchanged.
- Step 3's subject-key global owner is unaffected. P03-2/3 live/HTTP activation
  and P03-9/10 operational/closure ownership remain separate deliveries.

## Implementation Notes

On acceptance, append dated Amendments to both ADR-0072 and ADR-0073, preserving
their bodies/status/dates, and replace only the conflicting timing clause in
Standard 05. Its canonical contract owns the algorithm; Identity's specification,
Security 11 and the catalogue link it rather than duplicating it. Record the
approved Step 4 delta in the Phase 03 delivery log before dependent code.

Step 4 requires an announced READ COMMITTED owner, a plan derived from actual
Role/parent facts and intended changes, sorted User acquisition, then sorted
strongest tenant acquisition. A separate post-wait read revalidates classification;
changed/ambiguous plans refuse without upgrading. The existing real mutation
trigger verifies its actual target/fence and eligibility before permitting the
write. Complete-graph/admin validation and central MUST audit precede commit.
No precheck Boolean or caller-supplied scope label substitutes for these checks.

## Tests and Proofs

These extend the already Registered
`Identity_Membership_Invariants_Use_Fresh_Ordered_Locks`; acceptance is not an
Implemented status. Step 4's `IdentityMembershipTests` owns real app-role proofs:

- Hold the User fence, start a competing closure transition, observe its lock
  wait, commit Membership, then allow the fixture-only competitor to proceed.
- Commit a nonadmitting transition while a creator waits for the User fence;
  its later trigger check must freshly refuse. Retain an admitting clean twin.
- Create a second Tenant Admin and contained assignments under a preheld exclusive
  plan; create a learner under a shared plan. Missing/wrong/shared-only User
  fences under tenant authority refuse without acquiring a missing key.
- Preserve changed-classification, no-upgrade, two-admin retirement, shared/
  exclusive coexistence, incompatible-isolation, timeout/cancellation and MUST
  rollback controls, with actual PostgreSQL lock-wait observations.
- Prove exact five-definer bodies/ACL/callers and clean/dirty controls. Neither
  a generic User probe nor a synthetic trigger mode may appear.

Fixture-only closure controls introduce no production lifecycle writer or grant.

## References

- [ADR-0072](0072-application-owned-identity-bootstrap.md)
- [ADR-0073](0073-scoped-membership-and-identity-data-foundation.md)
- [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)
- [Identity storage and lock contract](../standards/05-database.md#identity-storage-contract)
- [Identity specification](../modules/identity/README.md)
- [Identity foundation permissions](../standards/19-permissions.md#identity-foundation-contract)
- [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md)
- [Phase 03](../roadmap/phase-03-identity-admin.md)
