# ADR-0081: Fixed Seed Completion Before Origin Adoption

## Status

Proposed

**Date:** 2026-10-29
**Deciders:** @cemil (approval pending)
**Proposed bounded extension:** ADR-0080's closed source, consumer and projection
census, solely for the separate fixed administration-origin completion check
below. Its eight pristine-continuation consumers remain unchanged. The proposal
also narrowly supersedes ADR-0079's marker-1 completion shortcut and clarifies
its completed-foundation condition for this topology act, including its relation
to ADR-0073's globally eligible administrator. It does not confer global User or
provider admission authority. Marker 2, not an existing marker 1, identifies the
new qualified path.

No implementation authority follows from ADR-0080's acceptance or this draft.
The [Phase 03 Status block](../roadmap/phase-03-identity-admin.md) owns approval
and delivery state.

## Decision Drivers

- ADR-0079 requires a valid completed foundation before either fixed management
  origin is adopted. Tenant provisioning alone cannot establish that condition.
- The orgless adoption owner cannot see declared branch Memberships, profiles,
  Courses or Lessons through ordinary organization scope. Role history proves
  that Identity began, not that the foundation completed.
- Preserve valid completed installations with legitimate later edits, including
  revoked Memberships, edited profiles, changed grants, locales and branding.
- Keep the authoritative check after the exclusive Tenant fence and actual
  target locks, on the same physical owner, without another connector or global
  User/provider owner.
- Keep pristine continuation and completed-installation topology adoption as
  distinct purposes; neither grants repair or a general directory capability.

## Considered Options

1. **A separate closed completion-purpose read on the existing owner**
   (recommended). Reuse ADR-0080's scope transition, barriers and cleanup, while
   explicitly authorizing one additional logical consumer and fixed owning-module
   projections. Verify current completed state rather than pristine v1 equality.
2. **A preliminary sequence of ordinary per-organization reads with a monotonic
   completion witness.** Potentially viable, but requires a new source/lifetime
   contract and proof that every relevant fact survives intervening admitted
   changes. An earlier planner Boolean is insufficient under the current fresh,
   fenced completion obligation. This is more complex than one existing owner.
3. **Carry ephemeral completion evidence from the fresh Seeder run.** Does not
   serve the explicit standalone upgrade of a completed legacy installation or
   a restarted two-Tenant adoption without an additional recovery protocol.
4. **Define completion as Tenant provisioning alone.** An honest alternative
   product decision, but it changes ADR-0079's promise and admits management
   origins for incomplete installations. Not selected.
5. **Defer adoption and fail closed.** Preserves confinement but withholds the
   accepted completed-legacy upgrade. This is the fallback while the authority
   is unapproved, not the selected completed behavior.

## Decision

LearnStack gives the fixed `AdoptSeedAdministrationOriginCommand` a separate,
one-use completion-purpose witness on its existing ordinary ReadWrite owner.
After the exclusive Tenant fence and Tenant/mapping target preparation, fixed
owning-module contracts verify the target Tenant's completed retained foundation
through a temporary Tenant read scope. The owner explicitly restores narrow
scope before returning admission or changing the mapping/marker. The eight
ADR-0080 pristine consumers retain their existing source and predicates.
Only fully qualified `0→2` / `1→2` transitions establish the new completion
marker; only 2 permits the exact-mapping write-free repeat. This topology act
checks retained current local state, not original profile-initializer history
or fresh global User/provider eligibility.

## Context

### Observed gap

The current adoption handler checks its fixed declaration, the retained Tenant,
current default Organization and management mapping. A newly provisioned Tenant
already has version 1 and a valid empty locale configuration. Those checks can
therefore admit adoption before the additional Organization, original public
host, theme, content, Roles or Memberships have been installed.
`Origin_adoption_refuses_a_retained_Tenant_without_a_completed_v1_installation`
observes that missing refusal after provisioning alone.

Existing legacy-adoption fixtures run the content `SeedRunner` without the
Identity foundation runner. They establish mapping/marker/audit behavior, not
the full completed-foundation precondition. The
[Step 3 gap record](../roadmap/phase-03-delivery-log.md#p03-3-step-3--origin-completion-authority-gap-2026-10-29)
owns the actual application-role regression, source scope and result. A regression
against current code establishes the missing check, not qualification of this
proposal.

ADR-0080 deliberately excludes origin adoption from its eight-consumer authority.
Using that witness for this act, or interpreting its pristine metadata reader as
a completion reader, is not an implementation-only change.

### Closed source, purpose and owner

Exactly one additional writer type consumes this authority:
`AdoptSeedAdministrationOriginCommand`, for the two existing declarations in
`SeedAdministrationOriginManifest`. Their fixed Tenant IDs, slugs, management
hosts, NULL organization and live flags remain unchanged. This is **eight
pristine-continuation consumers plus one distinct completion consumer**, not a
general seed-read population. The complete twenty-two tenant-bound writer census
does not change.

A separate sealed `SeedCompletionReadWitness` has a private constructor and one
closed Seeder minter. It binds the actual active Application invocation, canonical
finite Seed policy by reference, the live descriptor issued by that console
source, independently copied fixed foundation/topology declarations, exact request
reference/type/values, and the actual prepared Tenant version/mapping evidence.
`ExpectedVersion` or a submitted declaration/hash is not authority. API
composition denies the producer. The captured ordinary unit, transaction,
positive generation, unchanged Tenant/NULL-org announcement and exact exclusive
Tenant plan also bind the witness.

The handler acquires that one plan and locks the existing Tenant root and any
existing management mapping before verification. An absent mapping uses the
Tenant absence fence and global host uniqueness; a foreign/conflicting host
refuses. Owning target preparation supplies actual read evidence, not a
caller-provided admission Boolean. No User fence, second plan, nested dispatch,
new provider scope or second physical owner is acquired for verification.

A separate common-owner `VerifySeedCompletionAsync(witness, cancellationToken)`
entry selects one constructor-registered closed completion operation. It accepts
no callback, SQL, general manifest or reusable scope lease. The same scoped
operation resolves after owner construction. The owner shares one private typed
transition implementation and the same two exact scope-only SQL sites; it does
not duplicate the sites or expose a general callback/reader adapter. Pristine
and completion witnesses
cannot substitute for one another; their exact minter, consumer and reader
censuses remain separate. Human `OperationReadWitness` semantics are unchanged.

### Meaning of completed foundation

The closed declaration names the original required roots and immutable authored
pins. Existing valid administration data can coexist with them, but cannot stand
in for a missing declared root. Retained terminal roots are inspected as history;
they are not recreated. Each module verifies its own current graph and returns
only admission/refusal through Application.Contracts on this owner.

| Owning module | Completion facts |
|---|---|
| Tenancy | Original Tenant and Organization identities/natural keys and recognized creation/history stamps; valid current Tenant/default-Organization topology and locale configuration; original public mapping's exact Tenant/organization association; retained declared theme root/key/scope and valid current palette. Existing eligibility for the adoption target and exact management mapping remain required. |
| Customization | Required built-in and authored ContentType/Taxonomy roots, finite child graphs, exact immutable revision/content pins and declared completed publication state. |
| Education | Every declared Course/Lesson across all original organizations, exact root/parent/scope/natural-key and immutable definition/translation pins, required child existence and completed publication state. |
| Identity | All declared Role/Membership identities and immutable scopes/User associations, recognized retained lifecycle and attribution/version metadata, current retained profile disposition and required contained child existence, valid current finite grants/assignments and the origin-only local administrator predicate below. |

Completion does not require original mutable names, active status for every
retained Organization/Membership, exact v1 grants/assignments, original locale
membership, original theme colors or original profile values. Legitimate adopted
Role releases and later edits remain preserved. Each required Membership/profile
child must exist with valid retained stamps/current graph and aggregate version
greater than zero. This is the current installed-state disposition: a lawful
profile, status or assignment edit may advance an originally empty Membership.
It is not evidence that the original seed-profile initializer ran. Version-zero
unfinished state refuses; lawful later empty profiles and retained terminal
nonadmin Memberships are preserved, without initialization or reactivation.
Missing required roots, conflicting immutable identity/pins or invalid graphs
refuse without repair. The closed content authorship/publication rules remain
the basis for immutable pins; Phase 06 reopens this proof before introducing
mutable content administration.

Tenancy and tenant-wide Customization reads run narrow after target preparation.
Only the fixed Education and Identity branch completion readers run through the
temporary hatch
on the same owner; they do not depend back on the Tenancy guard. There is no
cross-module SQL join. Immutable content pins may be compared internally by their
owning readers, but no content or relationship-bearing result escapes them.
Identity selects metadata and finite graph facts, not display names, locale
preferences, email or provider subjects. Existing domain/database validity guards
remain required; a metadata projection does not independently validate arbitrary
forged profile values.

### Explicit global/provider boundary

For this topology-only act, ADR-0079's **valid completed foundation** means the
retained Tenant-local foundation above, including exact Membership associations
to the declared permanent application `UserId` values. The existing foreign keys
and immutable global binding rules establish retained application association;
the check neither reads global User rows nor reacquires provider evidence. No
raw subject, subject digest, credential or MFA state is selected.

This is an explicit proposed origin-only clarification of ADR-0079, not a claim
that a Tenant read proves live provider identity or current global User eligibility.
The local administrator predicate requires an Active, undeleted NULL-org
Membership assigned a live Tenant-scoped TenantAdmin Role with both immutable
kernel permission pairs and a valid current local graph. It does **not** freshly
attest ADR-0073's globally eligible administrator invariant or call the
trigger-only global admission function outside a mutation trigger. That full
invariant and its guards remain unchanged for existing Membership/Role writers.
Origin adoption may proceed when global/provider state has changed but this
required Tenant-local foundation remains valid. It grants no login, Membership,
Role or reactivation authority. Authentication and ordinary warm Identity
validation retain their own checks and Closed/conflicting-binding refusals.
If adoption itself must freshly refuse a changed global/provider condition, this
option is insufficient and that additional authority must be decided before
implementation. No global or external-provider snapshot is implied.

### Transition, mutation and repeat behavior

The completion operation reuses ADR-0080's distinct seed state:
Narrow → Enabling → Verifying → Clearing → Narrow, with sticky Faulted/rollback-only
on misuse or uncertain outcome. Entry requires depth one, READ COMMITTED,
ReadWrite, a clean announced owner and no human/global/self authority. One-use
source, purpose, prepared target, plan, transaction and generation are revalidated
after every await and after restoration. Tenant/organization announcements never
change.

The existing scope-only enable/clear SQL sites, dynamic EF view, first
SaveChanges barrier, entry/exit change detection, EF command-source guard and
owned raw audit/outbox write barriers apply unchanged. Commit, nested Begin,
context setters and write/constraint-validation paths refuse during verification.
Cancellation or lost enable/clear acknowledgement poisons the owner; bounded
cleanup uses the existing cancellation-independent policy and must actually
restore or close ownership before releasing resources. The physical transaction
remains ReadWrite: this is neither SQL READ ONLY nor protection against arbitrary
SQL through a retained connection.

Only after acknowledged narrow restoration and a positive completion result may
the following transitions occur:

| Retained marker | Qualified disposition |
|---|---|
| 0 | Install the absent fixed mapping or verify its exact existing live association; advance directly to 2. |
| 1 | Require the exact existing live mapping, verify the whole completed foundation, then advance to 2. Missing/disabled/conflicting mapping refuses without repair. |
| 2 | Require the exact live mapping and stop before dispatching another mutation or MUST intent. |
| Other | Refuse. |

Both `0→2` and `1→2` carry the existing two MUST intents in one transaction.
The exact existing mapping is designated as its unchanged audit projection;
no fictitious UPDATE is introduced. A forward migration changes the existing
range/transition guards to permit those two transitions, reject fresh `0→1`,
every decrement and arbitrary jumps, and preserve existing marker-1 history.
It adds no column/table or deletion/reset path. A marker-1 installation produced
by the current unqualified code must pass the new proof; it cannot use the
marker-2 repeat shortcut. An incomplete marker-1 installation refuses unchanged.
Mapping projection and Tenant transition remain atomic. Unknown-COMMIT recovery
freshly observes the marker and exact mapping: 2 converges to the write-free
repeat, 0/1 requires a deliberate new fully qualified attempt, and an unknown
observation remains indeterminate. It never infers rollback from a transport error.
Refusal retains the existing failed/denied audit and genuine unknown-COMMIT
reconciliation. The internal Boolean completion check adds no separate successful
sensitive-read classification or durable receipt; any independently callable or
value-disclosing reader requires a new decision and audit coverage.

Marker 2 plus its exact live mapping is a write-free repeat; a missing,
disabled or conflicting mapping refuses without restoration. A marker produced
by this qualified path records the completed adoption, not permission to mutate
or a current provider-health assertion. Required retained roots/immutable pins
and the defined version-positive profile disposition are monotonic under the
admitted writer population;
later lawful edits preserve their relevant graph invariants. This supports the
existing repeat path without a new hatch on every no-write check. It does not
authenticate a marker previously forged or written by unqualified code, prove
arbitrary database history, or make mutable graph values monotonic. A future
writer that invalidates that premise must reopen the repeat contract.

### Confinement and residuals

The hatch physically exposes target-Tenant organization-scoped rows beyond the
fixed projection. Closed source, purpose, owning readers and lifetime constrain
the application operation; RLS still enforces Tenant isolation and existing
cross-organization write restrictions. Other seeds, events, jobs, human requests
and nested work receive no completion witness. No inspection result, editor ID,
relationship metadata or immutable payload enters logs, telemetry, public DTOs,
cache entries or a new durable receipt.

Persisted SystemActor/version/stamp evidence is not cryptographic provenance.
Application database credentials can forge it without owner/bypass credentials.
The proof covers admitted writers, not credential compromise or arbitrary SQL.
This remains one target Tenant's transaction, with no atomic two-Tenant,
global-User or provider snapshot.

## Consequences

### Positive

- An incomplete or conflicting foundation cannot acquire a management origin
  merely because provisioning created its Tenant/default Organization.
- Completed legacy installations retain the explicit preserving upgrade, including
  valid branch edits and per-Tenant interrupted adoption.

### Negative

- One more logical seed source/consumer receives Tenant-wide reads, and its
  projections cover more owning modules than ADR-0080's pristine reader.
- Full completion verification extends the exclusive Tenant fence. Existing
  deadlines bound attempts; no production latency/fairness claim follows.
- The origin-only global/provider boundary and current-state profile disposition
  are explicit approval choices, not original initializer/global eligibility proofs.
- Existing marker-1 installations need a one-time fully checked, audited upgrade
  to 2. This adds a forward migration and preserves their prior history.

### Neutral

- No login role, grant, definer, physical owner, connector, Tenant setter, context
  writer, table, column, profile hash or key store is added.
- The eight pristine consumers, twelve narrow content writers, existing marker column,
  fixed hosts, plural audit and protected human authority remain separately owned.
- Approval is not runtime qualification or completion of P03-3 Step 3.

## Implementation Notes

Before capability code, acceptance reconciles these named carriers; this draft
changes none of them:

- ADR-0080 Amendment 1, **Separate fixed origin completion purpose**: disclose
  the additional logical consumer/source and owning-module projection census;
  preserve its eight pristine consumers and original body.
- ADR-0079 Amendment 3, **Fenced origin completion and global boundary**: name
  exact completion/repeat evidence, qualified marker 2 with `0/1→2`, and the
  origin-only global/provider/current-profile clarification.
- ADR-0073 Amendment 5, **Origin-only retained completion boundary**: point to
  this bounded local predicate without weakening globally eligible-admin guards
  for its original writers or asserting historical profile-initializer provenance.
- ADR-0040 Amendment 18, **Fixed completion operation on the existing owner**:
  register the distinct witness/entry and reuse of existing state/sites/barriers.
- ADR-0076 Amendment 3, **Separate fixed completion read source**: extend the
  finite seed exception's exact source/purpose census; preserve human minters.
- Standards 05/11: canonical owner/source/consumer/projection ledger, target
  preparation, completion facts, marker-2 forward migration/transition guards and
  restoration obligations. Standards 18/19:
  internal result/audit boundary, fixed source and preserved current authority.
- Standard 21: register `Fixed_Seed_Origin_Adoption_Requires_Completed_Foundation`,
  owned by `SeedOriginCompletionConventionTests` and `SeedOriginCompletionTests`;
  extend existing seed-read and complete-writer controls without claiming
  implementation before behavioral proof.
- Tenancy, Customization, Education and Identity module specifications: owning
  completion contracts and their exact mutable/immutable boundaries. Architecture
  23: internal editor/relationship metadata, provider boundary and forged-history
  residual. No new persisted data category follows.
- Decision index, Phase 03 Status and indexed delivery log: record separate
  approval, proposal reviews, actual regression and implementation proof scope.

P03-3 Step 3 owns implementation and qualification after explicit acceptance.
Historical Accepted bodies/dates/statuses remain intact; amendments disclose
bounded changes. Production provisioning/recovery stays P03-9. A general
provisioner, different population, new source, global check, new connector or
value-disclosing consumer reopens this decision.

## Architecture Tests

Exact controls keep eight pristine consumers plus one distinct completion
consumer, separate minters/purposes, API denial, owning-contract reads, same-owner
target preparation and clear-before-write. Plant cross-purpose witness reuse,
forged source/policy/declaration, wrong request/version/owner/generation, missing
plan/root preparation and accidental profile/global/provider value projections.

Actual application-role PostgreSQL controls must cover:

- Refusal after provisioning alone and at every incomplete stage, including the
  final other-organization Membership profile and a branch content pin.
- Completed legacy/fresh positives with the full foundation, valid later profile,
  grant, assignment, locale, theme and Organization edits, and retained terminal
  roots; missing/conflicting declared roots remain refusals.
- Version-positive nonadmin suspension/revocation before original profile
  initialization follows the selected current-state disposition; version zero
  refuses. No control claims original initializer provenance or live global eligibility.
- Both held-Tenant competing-commit orders, all applicable target locks, unchanged
  mapping/marker and successful audit counts on refusal, with normal failed/denied
  evidence asserted separately; exact-existing-mapping positive,
  plural-MUST rollback, independent two-Tenant resume and zero-write repeat.
- Genuine old-code marker 1 on an incomplete graph refuses unchanged; completed
  marker 1 upgrades once to 2 with exact existing mapping and plural MUST evidence.
  Fresh 0 upgrades directly to 2. Dirty direct `0→1`, decrements and jumps refuse;
  unknown-COMMIT recovery cannot misreport a qualified transition or recreate a mapping.
- Foreign-Tenant masking, wide branch reads limited to the closed operation,
  narrow restoration, unchanged announcement/generation, same-org/cross-org write
  barriers, nested/mixed entry, lost enable/clear acknowledgements, cancellation,
  source expiry, cleanup and pooled-connector reuse.
- The explicit global boundary: no global/provider read owner, retained exact
  application association, no new authentication/Role authority, and no claim
  that a mapping proves current provider eligibility.

Existing origin tests are extended with real completed Identity/content fixtures;
their earlier mapping/audit coverage is retained. Source inspection and metadata
census do not replace these behavioral controls or the two fresh proposal and
implementation review rounds.

## References

- [ADR-0079: Bounded Identity Administration](0079-bounded-identity-administration.md).
- [ADR-0080: Fixed Seed Prerequisite Read Scope](0080-fixed-seed-prerequisite-read-scope.md).
- [ADR-0040: Ambient Unit of Work](0040-ambient-unit-of-work.md).
- [ADR-0072: Application-Owned Identity Bootstrap](0072-application-owned-identity-bootstrap.md).
- [ADR-0073: Scoped Membership and Identity Data Foundation](0073-scoped-membership-and-identity-data-foundation.md).
- [ADR-0076: Live Scoped Authorization](0076-live-scoped-authorization.md).
- [ADR-0077: Trusted Authorization Evidence](0077-trusted-authorization-evidence.md).
- [Database Standards](../standards/05-database.md#fixed-seed-prerequisite-read-authority).
- [Data Protection](../architecture/23-data-protection.md).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
