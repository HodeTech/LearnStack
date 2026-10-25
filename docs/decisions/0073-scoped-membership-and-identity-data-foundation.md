# ADR-0073: Scoped Membership and Identity Data Foundation

## Status

Proposed — maintainer approval required before acceptance or dependent code.

**Date:** 2026-10-25
**Deciders:** @cemil

**Relationship:** Joint acceptance with [ADR-0072](0072-application-owned-identity-bootstrap.md)
is required. Complete G3/G19 and the foundation portions of G2/G4/G5/G8/G16;
later execution gates remain Open. This record and its Proposed module contract
introduce no current schema, grant, route or authorization capability.

## Decision Drivers

- Membership/profile/assignment changes need one aggregate boundary; nullable
  scope, revoked successors and direct database writes need independent controls.
- Permission identity is `(scope, key)`. Capability scope must not accidentally
  dictate Role storage scope or template-authoring authority.
- The product needs exact-organization assignments, not independently configured
  copies of each built-in Role in every branch.
- Initial grants, later additions and the Tenant Admin minimum need finite
  manifests, with an explicit first-admin and concurrent final-admin contract.
- Personal-data classification and safe diagnostics precede personal-data writes.

## Considered Options

1. **Contained Membership state, tenant-wide Role templates and code-owned scoped
   grants** (chosen). An Organization-capability template derives its effective
   organization only from the Membership assignment.
2. **Independent profile/assignment roots** (rejected). Atomic membership changes
   would require another multi-root exception without a benefit here.
3. **Independent Role definitions per organization** (rejected). No selected product
   requirement needs branch-specific built-in Role configuration. It adds
   organization-initialization/reconciliation writes and a lifecycle dependency
   on Tenancy; tenant-wide templates remove both without widening assignments.
4. **A shared global or tenant profile for all branches** (rejected). It collapses
   contextual ownership; canonical RLS exposes NULL-org data across branches.
5. **Custom roles, wildcard administrators or automatic new-key grants** (rejected).
   These broaden the closed model or silently increase existing authority.
6. **Only active uniqueness or recycled User bindings** (rejected). Suspended
   duplicates complicate resumption; recycled subjects can resurrect closed accounts.
7. **PII/name heuristics alone** (rejected). Snapshot markers and LS0002 do not
   classify neutral-name properties or prevent value-bearing diagnostics.

## Decision

Membership owns one contextual Profile and its Role assignments. Active and
Suspended rows occupy the same null-safe natural key; Revoked rows are retired
and rejoin creates a successor with fresh assignments. User remains global under
ADR-0072, without tenant-authored fields or an active-session selector.

Role is a **tenant-wide template**, carrying `TenantId`, a closed built-in key
and capability `PermissionScope`, but no `OrganizationId`. Grants are contained
Role children. Tenant Admin alone edits templates; an Organization-capability
template becomes effective only through an exact-organization Membership
assignment. Organization creation writes no Identity rows. Learner has Tenant
and Organization variants: an explicit scope extension, not a new role name.

The [Proposed permission annex](../standards/19-permissions.md#proposed-identity-foundation-contract)
owns the exact five foundation permission pairs, version-1 grant manifest and
two-pair immutable Tenant Admin kernel. One versioned compiled manifest generates
the SQL guard artifact, checked for equivalence; Role data cannot redefine it.
New keys stay disabled for existing Roles until an explicit grant change.

Every Membership mutation leaves an Active, globally eligible, tenant-wide
Tenant Admin. The first Membership therefore establishes that administrator;
later mutations cannot remove the final one. Role roots initialize in separate
transactions beforehand. Completed foundation is the verified required templates
plus the actual eligible admin, without a bootstrap table or writable flag.

This preserves administration for Trial, Active and Suspended tenants. Archived
termination/global closure needs a separately admitted G13/G16 exception before
its writer; foundation guards infer none from status. Creation, reactivation and
assignment additions recheck the User fence. User admission locks precede tenant
invariant locks; writers/guards require READ COMMITTED and VOLATILE fresh
post-wait reads through commit.

The [Identity module contract](../modules/identity/README.md) owns exact tables,
guards, seed sequence and execution/proof owners. Seed-only commands have a
closed admission capability, default-denied in the API, with no live route.
G19 adds semantic property classification and fixed diagnostics before PII
writes. Live authorization remains P03-2; retained linkage remains pseudonymous.

## Context

At `92117893`, Identity's four packages contain assembly markers only. No
Identity schema, registry or grants exist. Tenant filters do not exclude deleted
rows or carry `app.scope`; OrganizationScopeValidator proves belonging and
nondeletion, not lifecycle eligibility. Tenant creation starts Trial; public
eligibility admits Trial and Active. These facts are inputs, not Identity proof.

SeedData declares four authentication actors, two tenants and two organizations
per tenant, without application memberships. The Proposed [seed sequence](../modules/identity/README.md#first-admin-and-seed-sequence)
preserves bindings/credentials and declares exact assignments. Role templates
are tenant-wide even for Organization capabilities; only Membership/Profile/
Assignment rows carry branch scope. Separate explicit proof subjects exercise
cross-tenant/multi-org behavior and another same-tenant person, without email merging.

### Boundaries and acceptance instruments

| Authority | Exact relationship | Acceptance instrument / owner |
|---|---|---|
| ADR-0072 | Required global identity, admission fence, confined owner reads and global audit contract | Joint acceptance; P03-1 Steps 1/3; no independent global SQL or retention variant here |
| ADR-0017 | Preserve tenant grants plus matching-org grants; clarify contained Membership data and tenant-wide Role storage | Dated clarification on acceptance, preserving Accepted body; P03-1 Step 1 |
| ADR-0042 | Preserve its only exception: Tenancy's Tenant/default Organization pair | No replacement or new exception; external seed owner commits Role/Membership commands separately |
| ADR-0040 / Security 11 | Candidate reader needs an explicitly enumerated owner/setter | Dated Amendment and exact registration before execution; P03-1 contract, P03-2 adapter |
| Standard 19 | Resolve initial/upgrade grants with finite manifests; extend Learner capability scope | Adopt Proposed annex only with both ADRs; P03-1 Step 1, enforcement P03-2 |
| ADR-0044 / Standards 18/21 | Child capture, personal-data declarations and actual proof subjects need named owners | Applicable dated navigation/registered extensions, then actual backfill; P03-1 Steps 1–4 |

No current Accepted ADR body, privilege or runtime surface changes through this
Proposed record. The [revised P03-1 package](../roadmap/phase-03-delivery-log.md#p03-1-revised-decision-package-2026-10-25)
owns approval/implementation sequencing. This revised proposal's exact manifest
and module contracts are part of the joint approval surface.

### Later execution boundaries

- **G2 / P03-2:** qualify the app-role candidate reader, step-5/6 handoff, consistent
  EF/RLS read scope and independent HostScope ceiling. The current DenyAll reader,
  HTTP surface and unset `app.scope` stay until then. Successful claim-only header
  mismatch evidence needs separate G2/G7 authority before activation.
- **G4 / P03-3:** scope discovery/issuance is per session. No global User selector
  or provider user-global attribute proves independent simultaneous A/B refresh.
- **G8 / P03-4:** invitation acceptance/delivery needs its own accepted atomicity/
  reconciliation vehicle. No Invitation table or multi-root exception lands here.
- **G13 / P03-9:** production first-admin/provisioning admission and exceptional
  recovery close before their production provisioner; the fixed seed capability
  grants no operator or SystemActor bypass.
- **G16 / P03-10:** closure authority, participants, retained-copy transformations,
  artifacts/jobs and provider finalization remain mandatory. Tenant erasure or
  Membership removal never implicitly closes the global provider account.
- **G19 / P03-1 Step 2:** classify every mapped subject/property, including
  inherited actor references and ADR-0072 audit linkage, in closed categories or
  explicit None with purpose. Compare the actual nonempty models, not sensitive
  names. SQL-only/control/vendor inventory stays with Data Protection. Translate
  new Identity conflicts/failures to fixed bounded descriptors before sinks;
  never retain input-bearing DTO/token/body/provider/database exception text,
  including inner payloads. Unexpected paths retain fixed operational signals.
  P03-5 extends revision-aware dynamic values; P03-10 completes reference locators.
  LS0001/LS0002 remain Warning until P03-11.

P03-1 stores no optional token-derived canonical email/verification mirror.
Its first consumer, currently P03-6/G10, decides the later writer/freshness
contract. A token snapshot is not current verified email for invitation acceptance.
Snapshot redaction and diagnostics remain distinct; no global exception scrubber,
global-profile editor, dormant custom-fields blob or retention change is implied.

## Consequences

### Positive

- One Membership transaction owns contextual state and accurate child audit.
- Tenant-wide templates remove per-branch initialization/configuration drift.
- Finite manifests make grants, kernel safety and no-write reruns inspectable.
- A semantic property census detects omissions even when names look harmless.

### Negative

- Branch-specific Role configuration reopens this decision.
- Tenant-wide profiles remain branch-visible; private data needs org Membership.
- New capabilities require explicit grant adoption; seed drift refuses repair.
- Administration mutations serialize. Revoked successors and retained linkage
  still need later privacy fulfillment.
- Semantic classification needs maintenance and cannot infer arbitrary text's PII.

### Neutral

- No Platform grant, Hub crossing, production first-admin provisioner, invitation,
  dynamic-field/DSAR workflow, live authorization or UI is activated.
- Table/proof declarations are planned contracts, not executable controls.

## Implementation Notes

The Proposed [module specification](../modules/identity/README.md), [permissions](../modules/identity/permissions.md)
and [audit matrix](../modules/identity/audit.md) assign current/planned subjects
without calling an empty census implemented. Do not copy canonical tenant RLS
or ADR-0072's global SQL into this ADR.

Reopen for independent branch configuration, custom roles, different profile
granularity, changed lifecycle/admin invariants, automatic grant expansion,
global canonical-profile disclosure or dynamic-field diagnostic capture.

## Architecture Tests

Existing canonical controls retain their current implementation status; Identity
extensions await subjects/backfill. Planned coverage includes marker/filter/RLS,
tenant-composite FK/mirror/unique/aggregate/audit rules and nonempty scoped
definitions. The semantic property census is planned for Step 2, with a planted
unclassified neutral-name property; it is not yet a catalogue registration.

## Behavioral Proofs

- Real app-role PostgreSQL uniqueness/lifecycle/scope controls, first/final-admin
  races, fresh post-wait competing commits, isolation refusal and closure versus
  creation/reactivation/assignment additions, with independent dirty controls.
- Exact manifests, separate root transactions, interrupted seed resume, fixed
  drift refusal, disabled new-key grants and complete no-write rerun.
- All four preserved provider/actor identities, Organization Learner branch denial
  and explicit cross-tenant/multi-org/same-tenant-second-person subjects.
- Child audit/forced MUST rollback and per-sink sensitive/neutral-key nested
  canaries with positive received events; preserve TenantSetting redaction.

## References

- [ADR-0003](0003-tenant-isolation-defense-in-depth.md), [ADR-0017](0017-tenant-organization-hierarchy.md),
  [ADR-0032](0032-exception-handling-logging-and-observability.md),
  [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md),
  [ADR-0044](0044-audit-write-path.md), [ADR-0072](0072-application-owned-identity-bootstrap.md).
- [Permissions](../standards/19-permissions.md), [Database](../standards/05-database.md),
  [Data Protection](../architecture/23-data-protection.md), [Phase 03](../roadmap/phase-03-identity-admin.md).
