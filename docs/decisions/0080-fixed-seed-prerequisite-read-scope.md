# ADR-0080: Fixed Seed Prerequisite Read Scope

## Status

Proposed

**Date:** 2026-10-28
**Deciders:** Maintainer approval pending
**Bounded supersession:** ADR-0076's human-capability-only read-hatch source and
ADR-0040's closed scope-announcement sites, solely for the temporary fixed-seed
verification described below. ADR-0079's preservation and pristine-resume policy
is retained; this proposal supplies its missing authority rather than weakening it.

This proposal authorizes no implementation. Existing Accepted bodies and dates
remain unchanged. Acceptance appends the named dated Amendments and reconciles
the canonical carriers before adding this capability.

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
  definers and ten logical Tenant setters; introduce no connector or read lane.

## Considered Options

1. **Temporary, separately witnessed fixed-seed prerequisite reads on the
   existing owner** (proposed). Verify the target Tenant's finite installation
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

Only the four fixed Identity seed writers and four original Tenancy continuation
writers consume this operation: Role initialization, first-admin Membership,
other Membership and profile initialization; additional Organization, original
host mapping, locale append and theme creation. Provisioning keeps its existing
INSERT-first path and gains no widened pre-insert read. Ordinary verification
queries, human requests, origin adoption, jobs, events and nested dispatch cannot
inherit this capability.

The complete User-before-exclusive-Tenant plan is already held; an existing
target root is locked before the authoritative check. An absent-root insertion
retains its exclusive Tenant absence fence and database uniqueness guards.
Inspection acquires no additional User lock beneath Tenant. Its separate fresh
READ COMMITTED statements use the same physical owner and existing deadlines.

This is a bounded new narrow-to-verification-to-narrow transition. Tenant and
organization announcements remain exact and unchanged. No global/self,
authorization-lane or second ordinary owner participates. The existing ten
logical Tenant setters and four context writers remain unchanged; the new
scope-only enable/clear SQL sites enter their own exact caller census.

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
history. Human edits advance root versions and carry their actual actor; initial
seed acts carry the closed original stamps. Return only admission/refusal or
bounded internal verification evidence. No raw relationship or editor metadata
enters diagnostics, telemetry, public DTOs, a new cache or durable receipt.

Tenancy and other module facts remain behind their owning Application.Contracts
ports on that owner. No cross-module SQL join is introduced. This is a
per-target-Tenant fenced prefix, not an atomic global User/provider or two-Tenant
snapshot. It preserves valid completed state and rejects edited incomplete state;
it grants no repair, reactivation or recreation authority.

`app.scope='tenant'` physically widens reads across the target Tenant's
organization-scoped tables. It is not a row-level capability restricted to the
fixed IDs. The closed source, projection, caller census and lifetime establish
that narrower application contract. This owner remains ReadWrite: same-scope and
Tenant-wide writes are physically possible. Existing WITH CHECK and restrictive
UPDATE/DELETE guards still reject cross-organization writes. The closed operation
issues reads only; EF mutation/flush and commit while verification is active
refuse. It makes no physical READ ONLY claim.

The common owner explicitly clears the transaction-local hatch and restores its
matching EF read view before releasing usable verification evidence. Validate
source, invocation, plan and owner continuity after each await and restoration.
Mixed or nested entry, reuse, wrong generation/source, cancellation, failed
enablement/clear or unknown cleanup marks the complete owner rollback-only.
Cleanup uses the existing bounded owner policy independently of a cancelled
caller; it does not release capacity or report success on a timeout alone.

No new externally callable operation or audit classification is selected. The
fixed metadata check is an internal prerequisite of the existing classified
seed act. Current MUST floors and central commit/reconciliation remain intact;
it does not disclose another person's profile values or turn a sensitive read
into an unaudited directory.

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

### Neutral

- No role, grant, definer, table, installation receipt, context writer, logical
  Tenant setter, human permission, endpoint or native/browser behavior is added.
- P03-3 remains in progress. Acceptance and document checks close none of its
  runtime, provider, browser, manual accessibility or final A/B obligations.

## Implementation Notes

Acceptance first appends dated bounded Amendments to ADR-0076 and ADR-0040, and a
dated ADR-0079 navigation Amendment naming this supporting authority. Reconcile
Standard 05's read-authority/seed contract, Security 11's source and setter census,
Standard 19's trusted-operation boundary, Standard 18's internal metadata-check
classification, Identity/Tenancy specifications, Architecture 23's relationship
data boundary and the architecture-test catalogue. Historical bodies, dates,
statuses, human minters and the reusable SQL policy template remain unchanged.

Within Step 3, implement the private source/witness and common owner transition,
then the fixed Identity prerequisite projection and writer integrations. Verify
the full target-Tenant prefix before all eight relevant writers, including later
stage/history refusal for missing earlier facts. Close actual misuse, cleanup,
isolation and competing-commit proofs before the original Step 3 commit and its
two fresh independent implementation-review rounds. Steps 4–10 retain their
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
commit, nested entry and pooled-connector reuse. No metadata-only inspection is
substituted for these behavioral controls.

## References

- [ADR-0040: Ambient Unit of Work](0040-ambient-unit-of-work.md).
- [ADR-0076: Live Scoped Authorization](0076-live-scoped-authorization.md).
- [ADR-0079: Bounded Identity Administration](0079-bounded-identity-administration.md).
- [Database Standards](../standards/05-database.md#p03-3-storage-and-replay-contract).
- [Security Standards](../standards/11-security.md#live-authorization-contract).
- [Permissions Standards](../standards/19-permissions.md#bounded-administration-and-version-2-adoption).
- [Identity specification](../modules/identity/README.md#first-admin-and-seed-sequence).
- [Data protection](../architecture/23-data-protection.md).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
