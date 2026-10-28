# ADR-0080: Fixed Seed Prerequisite Read Scope

## Status

Accepted

**Date:** 2026-10-29
**Deciders:** @cemil
**Bounded supersession:** ADR-0076's human-capability-only read-hatch source and
ADR-0040's closed scope-announcement sites, solely for the temporary fixed-seed
verification described below. ADR-0079's preservation and pristine-resume policy
is retained; this proposal supplies its missing authority rather than weakening it.

Accepted after the maintainer's 2026-10-29 review corrections. The named dated
Amendments and canonical carriers record this bounded authority before its
implementation. Existing Accepted bodies and dates remain unchanged; acceptance
is not runtime qualification.

## Decision Drivers

- ADR-0079 requires authoritative installation-prefix checks after the exclusive
  Tenant fence, including an edit committed while the next seed act waits.
- An orgless seed owner cannot read another organization's Membership history
  through the existing organization predicate or the ordinary seed projection.
- A prepared real PostgreSQL regression observes the current implementation
  completing instead of refusing after such an edit. Preliminary console reads
  and target-only rechecks are insufficient.
- The existing human read witness requires a live Protected Tenant capability.
  Seed provenance must not impersonate that source or weaken its controls.
- Preserve the same-owner transaction, scoped writes, four-role model, eight
  definers and ten logical Tenant setters; introduce no physical owner or connector.

## Considered Options

1. **Temporary, separately witnessed fixed-seed prerequisite reads on the
   existing owner** (chosen). Verify the target Tenant's finite installation
   metadata after its complete fence/root-lock plan, then clear the read scope
   before mutation. Accept the explicit read-authority and owner-lifetime costs.
2. **Refuse every partial state whose pristine history is outside ordinary
   scope** (viable, not selected). This safely avoids widening but withdraws part
   of the accepted interrupted-v1 resume promise. It requires an explicit
   recovery contract; no repair/reset procedure is implied by refusal.
3. **Treat the runner's earlier reads or the next target alone as sufficient**
   (rejected). The observed schedule violates ADR-0079's post-wait requirement.
4. **Reuse a human witness, retarget a joined owner or weaken RLS** (rejected).
   None supplies the required source or lifetime. Request fields, SystemActor
   attribution and a caller-provided manifest hash are not read authority.
5. **A new seed definer or separate cross-scope read owner** (not selected).
   A definer expands runtime owner execution and policy/ACL obligations. A second
   physical owner changes the selected same-owner contract and adds connector,
   progress, cancellation and cleanup qualification. Neither is needed here.

## Decision

LearnStack uses a distinct fixed-seed verification witness for a closed
prerequisite-check operation on the existing ordinary ReadWrite owner. After its
complete ordered fences and applicable target-root lock, that operation
temporarily announces the existing Tenant read hatch, verifies the target
Tenant's finite v1 installation metadata, and explicitly restores narrow scope
before mutation or continuation. It acquires no additional connection, User
fence, Tenant context or global authority. Failure to establish or restore the
scope poisons the owner; the existing human read-witness contract is unchanged.

## Context

### Observed gap

The uncommitted P03-3 Step 3 implementation follows the accepted target locks and
rechecks its target Membership, referenced Role and Tenancy prerequisites.
`SeedIdentityFoundationRunner` checks the full declared Identity population
before entering those separate write transactions. The ordinary retained seed
reader additionally fixes Membership/Profile/Assignment SQL to the current
organization. A fresh target read cannot establish another branch's history.

The regression
`IdentitySeederTests.A_branch_profile_edit_while_orgless_incomplete_seed_waits_refuses_the_next_seed_profile`
sets up the old v1 interruption after all Membership roots exist, with the two
English profiles complete and the Yoga profiles still pending. A real app-role
branch writer holds the Tenant fence. The test observes the orgless Yoga seed
writer waiting, commits a valid branch profile edit, and resumes the seed. The
current source completes instead of throwing the required refusal. The deployed
application-role RLS remains active; no owner/bypass isolation claim is made.

The [Phase 03 record](../roadmap/phase-03-delivery-log.md#p03-3-step-3--seed-prefix-authority-gap-and-proposal-2026-10-28)
owns the exact command, image, source scope and result. This is an implementation
proof of the missing check, not qualification of the proposed solution.

### Closed source and physical owner

The witness is private-minted from the actual active Application invocation,
canonical finite Seed policy and independently captured fixed-v1 declaration.
The closed producer belongs to the Seeder composition; API composition denies
it. Bind exact request reference/type/values, immutable manifest/root pins,
announced Tenant/organization, current unit, transaction and generation. Neither
an injectable admission Boolean nor a seed-shaped context can mint it.

Exactly eight writer types consume the temporary read authority. These are four
Identity writers and four of the five original Tenancy continuation operations;
`ProvisionTenant` deliberately keeps INSERT-first, without a widened pre-insert
read. The eight are not the census of all seed writes.

| Temporary-read consumer | Required fences and target preparation |
|---|---|
| Role initialization | Exclusive Tenant; absent-root fence and uniqueness |
| First-admin Membership creation | Target User, then exclusive Tenant; absent-root fence and uniqueness |
| Other Membership creation | Target User, then exclusive Tenant; absent-root fence and uniqueness |
| Profile initialization | Target User, exclusive Tenant, then retained Membership row lock |
| Additional Organization creation | Exclusive Tenant; absent-root fence and uniqueness |
| Original host mapping | Exclusive Tenant; absent-projection fence and uniqueness |
| Locale append | Exclusive Tenant, then existing Tenant row lock |
| Branding/theme setting creation | Exclusive Tenant, then target setting absence/row probe |

The caller holds its single complete plan before verification: User fences only
for the three admission/profile consumers above, always before Tenant. Existing
roots that will change are locked before the authoritative check; an absent
root has no row to lock. Inspection acquires no new User lock beneath Tenant and
does not reacquire a second plan. Separate fresh READ COMMITTED statements use
the same physical owner and existing deadlines.

Ordinary queries, human requests, origin adoption, jobs, events and nested
dispatch cannot inherit this temporary capability. The complementary seed-write
contract below does not make its twelve content writers additional consumers.

This is a bounded new narrow-to-verification-to-narrow transition. Tenant and
organization announcements remain exact and unchanged. No global/self,
authorization-lane or second ordinary owner participates. The existing ten
logical Tenant setters and four context writers remain unchanged; the new
scope-only enable/clear SQL sites enter their own exact caller census.

### Every tenant-bound seed write

ADR-0079's existing per-write obligation remains complete: the v1 runners send
21 writer types (five Tenancy, six Customization, six Education, four Identity).
The fixed origin-adoption act is a separate twenty-second tenant-bound writer.
Global User adoption stays on its separate subject-key owner; the refused
`SetDefaultTenantLocaleCommand` adds no actual writer.

The twelve content types are both built-in initializers and register/publish for
ContentType and Taxonomy; Course/Lesson create, translation append and publish.
Before each fixed content write, module-local guards bind the actual invocation
to the exact fixed declaration, take one exclusive Tenant plan and applicable
module/root locks, then freshly read their prerequisites through owning
Application.Contracts ports on that owner. Customization takes Tenant before its
quota/concept keys; Education takes Tenant before its parent/target row locks.
Tracked pre-wait objects are not fresh evidence.

These twelve do not need a Membership directory or temporary hatch. They freshly
require pristine Tenancy prerequisites and **no retained Identity Role history**
for the target Tenant, through a closed ordinary-scope Identity contract. All
Roles precede Memberships in v1; Role storage is Tenant-wide, DELETE is denied,
and tombstones remain. Any Role therefore proves the later Identity stage began,
including when a branch profile/grant/assignment is invisible to the content
owner. The check includes all retained Role rows, not only active or declared IDs.

Their owning contracts also verify exact retained target/predecessor state,
natural keys, expected versions and immutable authored pins in ordinary scope.
The fixed source admits only canonical content advancement, not arbitrary
authoring values. Cross-organization immutable prerequisites remain protected by
that closed writer population and the existing publication rules; this is not an
all-branch content snapshot or proof against arbitrary database mutation.
Initial taxonomy/built-in versions follow their real aggregate transitions,
not an assumed version-zero rule. Phase 06 must reopen this staging proof before
adding mutable content administration. Ambiguous or conflicting history refuses
without repair; valid interrupted fixed installations still require resume proof.

The built-in IntegrationEvent source remains distinct and receives no seed
witness. Its participating writes take at least the canonical shared Tenant
fence before quota/concept locks. Origin adoption retains its separately fenced
topology/completion checks and plural audit; it receives no pristine-resume hatch.
Both populations enter the complete writer/competitor census and Step 3 proofs.

Module-local guards use owning contracts; they do not put module request types
inside the common Application coordinator. A closed composition service joins
the fixed readers for the eight hatch consumers. Tenancy prerequisite reads run
narrow first; the separate Identity metadata projection alone runs inside the
hatch; scope is cleared before the caller receives a result. The Identity reader
does not depend on the Tenancy guard, avoiding a runtime dependency cycle. No
nested `SendAsync`, new provider scope or second acquisition of the lock plan is
used to make a prerequisite check.

### Owner state and write barrier

Use a separate sealed `SeedPrerequisiteReadWitness` with a private constructor
and exact-census minter. `OperationReadWitness` remains sealed and Protected-only;
its existing human binding is not generalized to FiniteTrusted Seed policies.
A separate common-owner interface exposes the closed
`VerifySeedPrerequisitesAsync(witness, cancellationToken)` operation, with no
callback, SQL argument or scope lease. Its constructor-registered fixed reader
is resolved on the same scoped owner after construction; API composition denies
the source. Module business handlers call only after their complete plan/target
preparation.

The common owner keeps seed state separate from human `_tenantWideRead` and
per-frame human witnesses: Narrow → Enabling → Verifying → Clearing → Narrow.
Any misuse or uncertain outcome becomes sticky Faulted/rollback-only. Entry is
only at physical depth one on a clean, announced ordinary READ COMMITTED
ReadWrite owner, without human/global/self authority or an earlier consumed
witness. Every await revalidates the captured invocation, fixed pins, exact plan,
transaction and positive generation.

Dedicated transaction-local statements change **only** `app.scope`; clearing
must preserve the exact Tenant/organization announcement and generation. Do not
call `ClearTenantContextAnnouncement` or the ordinary tenant setter to toggle
this scope. The dynamic EF read view is wide only after acknowledged enable and
while Verifying, unavailable while Enabling/Clearing, and narrow after clear.
The owner returns no usable result before acknowledged restoration and recheck.

A common first SaveChanges interceptor refuses synchronous/asynchronous flushes
before audit/domain-event capture, including same-organization and empty saves.
Enrolled contexts undergo change detection at entry/exit; pending Added,
Modified or Deleted state refuses. The EF command guard additionally enforces
the verification phase and command source without parsing SQL. Common owned raw
writes, including pending audit and outbox INSERT, check the same write barrier.
Commit, nested Begin, context setters and write/constraint-validation paths also
refuse and poison while verification is active. These are application-owner
guards; arbitrary SQL through a retained physical connection is not made READ
ONLY. Existing restrictive RLS guards still govern its write scope.

### Verification and confinement

The operation has fixed readers and no arbitrary callback, submitted SQL, general
directory API or reusable module-facing scope lease. Its Identity projection
checks all relevant declared predecessors, required absence/history and graph
conditions for the target Tenant, including other branches and retained terminal
roots. Trusted per-root scopes come from the immutable declaration. The ordinary
seed reader's current-organization predicates remain unchanged.

Use identifiers, lifecycle, versions, creation/update attribution, required child
existence and finite grant/assignment metadata. Do not select provider subject,
email, display-name or locale-preference values merely to verify pristine
history. Admitted application edits advance versions and carry their actual
actor; original seed evidence retains SystemActor creation, expected initial
versions and required update stamps. Return only admission/refusal or an opaque
one-use result, not `MembershipSeedDto` or raw editor evidence. No relationship
or editor metadata enters diagnostics, telemetry, public DTOs, a new cache or
durable receipt.

These persisted stamps are not cryptographic provenance. Arbitrary SQL using
application database credentials can forge SystemActor/version/history without
an owner or witness; owner/bypass credentials are not required. The metadata
check detects violations by the closed admitted writer population, not forged
database history or credential compromise. Existing target-value checks remain
where already required; this decision adds no profile hash, HMAC key or receipt
store and makes no database-tamper-detection claim.

Tenancy and other module facts remain behind their owning Application.Contracts
ports on that owner. No cross-module SQL join is introduced. This is a
per-target-Tenant fenced prefix, not an atomic global User/provider or two-Tenant
snapshot. It preserves valid completed state and rejects incompatible incomplete
state under the admitted-writer boundary above;
it grants no repair, reactivation or recreation authority.

`app.scope='tenant'` physically widens reads across the target Tenant's
organization-scoped tables. It is not a row-level capability restricted to the
fixed IDs. The closed source, projection, caller census and lifetime establish
that narrower application contract. This owner remains ReadWrite: same-scope and
Tenant-wide writes are physically possible. Existing WITH CHECK and restrictive
UPDATE/DELETE guards still reject cross-organization writes. The closed operation
issues reads only; the common write/flush/commit barriers above refuse while
verification is active. It makes no physical READ ONLY claim.

The common owner explicitly clears the transaction-local hatch and restores its
matching EF read view before releasing usable verification evidence. Validate
source, invocation, plan and owner continuity after each await and restoration.
Mixed or nested entry, reuse, wrong generation/source, cancellation, failed
enablement/clear or unknown cleanup marks the complete owner rollback-only.
Cleanup uses the existing bounded owner policy independently of a cancelled
caller; it does not release capacity or report success on a timeout alone.

No new externally callable operation or audit classification is selected. This
closed metadata check is an internal prerequisite of the classified seed act,
not a directory disclosure under Standard 18's other-person sensitive-read rule.
The eight consuming acts retain their existing mutation classifications and
MUST floors. Refusal keeps the act's existing failed/denied audit and genuine
unknown-COMMIT reconciliation; it creates no separate successful read event.
The fixed content acts retain their existing classifications too. Editor IDs
and relationship metadata stay internal and are personal data. An independently
callable or value-disclosing reader would require separate sensitive-read
classification before activation, under ADR-0077 and Standard 18.

### What would change this choice

Reopen before a generic/production provisioner, arbitrary Tenant population,
profile-value read, submitted manifest/query, new SQL owner/lane, global User
prerequisite, cross-Tenant transaction, general scope lease or independently
callable verification API consumes this seam. P03-9 owns production provisioning
and recovery; this ADR does not supply that authority.

## Consequences

### Positive

- A committed edit in another branch can invalidate the next incomplete seed act
  on the actual fenced writer instead of escaping a preliminary snapshot.
- Pristine interrupted installations retain automatic continuation without
  repairing valid human edits or adding another physical owner.

### Negative

- The Seeder gains a new temporary Tenant-wide read source and a scope transition
  that the existing human-only contract excludes.
- Fixed prerequisite reads extend exclusive Tenant-fence duration. Existing
  deadlines bound attempts; no production latency or fairness result is claimed.
- RLS read widening exceeds the fixed projection's logical row set. Correct
  source/lifetime and closed execution are required; this is not SQL READ ONLY.
- Pristine metadata is not authenticated history against a database-credential
  holder. It deliberately avoids value hashes and another durable receipt.

### Neutral

- No role, grant, definer, table, installation receipt, context writer, logical
  Tenant setter, human permission, endpoint or native/browser behavior is added.
- P03-3 remains in progress. Acceptance and document checks close none of its
  runtime, provider, browser, manual accessibility or final A/B obligations.

## Implementation Notes

The P03-3 Step 3 owner completes this acceptance checklist before capability code:

- [x] `decisions/README.md`: move 0080 from Proposed to Active.
- [x] ADR-0040 Amendment 17, **Fixed seed scope-only verification**: authorize
  the separate owner state, scope-only enable/clear sites and write barrier;
  preserve ten Tenant setters, four context writers and historical bodies.
- [x] ADR-0076 Amendment 2, **Separate fixed-seed prerequisite source**: name the
  bounded source exception without changing Protected-human witness semantics.
- [x] ADR-0079 Amendment 2, **Complete seed-write census and prerequisite proof**:
  navigate to this authority and the complementary twelve-writer narrow proof;
  retain every tenant-bound write's accepted fence/fresh-check obligation.
- [x] ADR-0073 Amendment 4, **Fixed seed prerequisite navigation**: distinguish
  the temporary verification from the original NULL-org seed target scope.
- [x] Standard 05's warm-seed/read-authority contract: canonical state, guard,
  source/caller, ordinary content-stage and complete writer census rules.
- [x] Security 11's source/census: replace the stale eight-setter statement with
  the existing ten-setter set; enumerate scope-only sites separately.
- [x] Standards 18/19, Identity/Tenancy and Customization/Education specs:
  internal metadata/audit boundary, exact fixed admission and per-write fencing.
- [x] Architecture 23: personal editor/relationship metadata and forged-history
  residual, with no new durable store or profile-value fingerprint.
- [x] Standard 21: register
  `Fixed_Seed_Prerequisites_Use_A_Closed_Temporary_Read_Scope` (owners
  `FixedSeedPrerequisiteConventionTests`, `FixedSeedPrerequisiteReadTests`) and
  `Tenant_Bound_Seed_Writes_Recheck_Under_Ordered_Fences` (owners
  `SeedContinuationConventionTests`, `SeedContinuationIntegrationTests`). Extend
  existing human-source/scope-carrier and seed-manifest controls; retain their
  Implemented baseline and mark new obligations Registered until proved.
- [x] Phase 03 Status/G5 and indexed delivery log: record this conditional
  maintainer acceptance, verified/skipped finding dispositions and Step 3 delta.

Historical bodies, dates, statuses, human minters and the reusable SQL policy
template remain unchanged. ADR-0072/0077 are related retained authorities, not
decisions superseded by this bounded source extension.

Within Step 3, implement the private source/witness and common owner transition,
then the fixed Identity prerequisite projection and eight writer integrations.
Also close the twelve content writers' ordinary-scope fences/staging/source
checks, built-in event competitors and separate origin-adoption obligations.
Verify relevant target-Tenant prerequisites before every actual tenant-bound
write, including later-stage/history refusal for missing earlier facts. Close
actual misuse, cleanup, isolation and competing-commit proofs before the
original Step 3 commit and its two fresh independent implementation-review rounds. Steps 4–10 retain their
approved scope/order. The phase Status block owns any approval wait.

## Architecture Tests

Extend the existing operation-read authority and scope-carrier caller controls
with exact new sources/borrowers/sites. Preserve their planted human-witness,
nesting, one-use and write-guard controls. Add closed producer/manifest binding,
same-owner/fence prerequisites, fixed metadata-only query and clear-before-write
obligations; acceptance registers these, not an Implemented claim.

Actual PostgreSQL controls cover both competing-commit orders; the observed Yoga
branch edit with unchanged successful profile count and target state; untouched
interrupted resume; valid completed no-write repeat; unrelated Role/Membership
edits; absent/retained/conflicting roots; foreign Tenant masking; cross-org write
refusal while active; narrow reads after restoration; lost enable/clear
acknowledgements, cancellation, source expiry, leaked scope, attempted flush/
commit, same-org EF/raw-owned write refusal, preserved Tenant/org/generation,
nested entry and pooled-connector reuse. Content controls cover all twelve writer
types and real held-Tenant/first-Role competitors, both commit orders, retained
history, exact fixed-source denial and untouched interruption at every stage.
No metadata-only inspection is
substituted for these behavioral controls.

## References

- [ADR-0040: Ambient Unit of Work](0040-ambient-unit-of-work.md).
- [ADR-0076: Live Scoped Authorization](0076-live-scoped-authorization.md).
- [ADR-0072: Identity Bootstrap](0072-application-owned-identity-bootstrap.md).
- [ADR-0073: Membership Foundation](0073-scoped-membership-and-identity-data-foundation.md).
- [ADR-0077: Trusted Evidence](0077-trusted-authorization-evidence.md).
- [ADR-0079: Bounded Identity Administration](0079-bounded-identity-administration.md).
- [Database Standards](../standards/05-database.md#p03-3-storage-and-replay-contract).
- [Security Standards](../standards/11-security.md#live-authorization-contract).
- [Permissions Standards](../standards/19-permissions.md#bounded-administration-and-version-2-adoption).
- [Audit Standards](../standards/18-audit-coverage.md#native-entry-and-protected-administration).
- [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Identity specification](../modules/identity/README.md#first-admin-and-seed-sequence).
- [Tenancy specification](../modules/tenancy/README.md).
- [Customization specification](../modules/customization/README.md).
- [Education specification](../modules/education/README.md).
- [Data protection](../architecture/23-data-protection.md).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
