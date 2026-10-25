# ADR-0073: Scoped Membership and Identity Data Foundation

## Status

Proposed — maintainer approval required before acceptance or dependent code.

**Date:** 2026-10-25
**Deciders:** @cemil

**Relationship:** Complete Phase 03's G3/G19 and schema/bootstrap parts of
G2/G4/G5/G8/G16. This derives from ADR-0017's hierarchy and ADR-0003's tenant
isolation. It adds no live permission, tenant-scope hatch or Platform admission.
The global subject and audit authority is separately proposed in
[ADR-0072](0072-application-owned-identity-bootstrap.md).

## Decision Drivers

- A membership can be tenant-wide or belong to one organization. Nullable keys,
  rejoin behavior and profile/grant ownership must be explicit before migration.
- Atomic membership/profile/assignment changes must respect the one-aggregate
  rule. Sharing a DbContext does not license another ADR-0042 exception.
- The same permission key exists at Tenant and Organization scope; a bare string
  or realm role cannot define the identity of an application grant.
- Disabled-by-default permission additions conflict with automatic Tenant Admin
  expansion. The first seed must choose a finite, repeatable upgrade contract.
- A concurrent pair of last-admin removals needs a shared serialization point;
  unrelated row versions do not preserve the tenant's administrative access.
- Snapshot markers and name-based diagnostics do not constitute a personal-data
  census or prevent a provider/database exception from echoing personal values.

## Considered Options

1. **Membership root with contained contextual profile/assignments; scoped closed
   roles and explicit classifications** (chosen). Match transaction and ownership
   boundaries, preserve the hierarchy, and make missing declarations falsifiable.
2. **Independent profile and assignment roots** (rejected). Atomic creation and
   removal would require a new multi-root exception without a benefit here.
3. **One global or tenant-wide profile for all branches** (rejected). Collapses
   contextual ownership; canonical RLS exposes NULL-org data to every branch.
4. **Custom role names, wildcard admin or automatic new-key grants** (rejected).
   Contradicts the closed role/permission model or broadens existing authority on
   an upgrade. Explicit finite grant adoption is independently reviewable.
5. **Only active membership uniqueness; recycle closed User subjects** (rejected).
   Suspended duplicates complicate resumption and a new identity can resurrect a
   closed account. Stable membership successors and permanent binding differ.
6. **Rely on PiiSensitive/LS0002 or infer PII from names** (rejected). A snapshot
   marker has no log semantics; LS0002 is a bounded name check. Neutral-name
   values and exception messages need an explicit safe representation.

## Decision

LearnStack uses Membership as the aggregate boundary for a person's relationship
to one tenant and optional organization. It contains its contextual profile and
role assignments. Role is a separate tenant-owned definition; permission
definitions are code-owned, identified by `(scope, key)`. Personal-data ownership
and diagnostic representations are declared before their first writes.

### Membership, profile and lifecycle

| Concern | Selected foundation |
|---|---|
| Identity | UUIDv7 `MembershipId`, plus `(UserId, TenantId, OrganizationId?)` natural key |
| NULL organization | Tenant-wide membership; never a default-org placeholder |
| Current uniqueness | NULLS NOT DISTINCT on the natural key where `deleted_at IS NULL`; Active and Suspended both occupy it |
| Lifecycle | Active → Suspended → Active; Active/Suspended → Revoked with soft deletion; revoked rows do not reactivate |
| Rejoin | New MembershipId, same natural key once predecessor is retired; fresh assignments, no automatic restoration |
| Profile | At most one contained profile per membership; create with the membership, same tenant/org and lifecycle |
| Assignments | Multiple contained role assignments; exact parent tenant/org mirror, no duplicate role reference |
| Global User | Stable reference; no cascading global deletion and no email-based relation |

The nullable-key behavior follows PostgreSQL 18's
[unique-constraint contract](https://www.postgresql.org/docs/18/ddl-constraints.html#DDL-CONSTRAINTS-UNIQUE-CONSTRAINTS);
the partial index still determines which lifecycle rows participate.

The profile initially carries optional contextual display/contact fields and a
locale preference, with bounded validated values. Absence uses an explicit safe
UI fallback; it never copies or overwrites a global person field on an ordinary
read. A tenant-wide profile is visible as tenant-wide data under canonical RLS:
it contains no branch-private notes. Organization membership profiles remain
independent, including two branches of one tenant.

Self may edit their contextual display/contact/locale fields only. An admitted
administrator may edit those fields within their delegated scope. Neither can
edit provider-canonical email/verification, global identity, roles through a
profile DTO, arbitrary settings or consent evidence. Required UI operations and
their exact requests/field allowlists are P03-3/G6; this proposal grants no HTTP
write route. Custom values/definitions and their revision pins remain P03-5/G9;
do not create a dormant `custom_fields` blob in P03-1. Consent remains append-only
purpose/version evidence, not an editable profile boolean.

Membership, contained profile and assignments carry tenant/org markers, EF
filters, canonical RLS and explicit lifecycle predicates. Same-module parent FKs
include tenant, use null-safe organization mirrors and immutable scope; no
cross-module Organization FK/navigation/join is added. All child writes must be
captured in their Membership audit row with accurate partial/complete collection
semantics. A child repository is not an independent write boundary.

### Roles, permissions and bootstrap

- Store only the closed tenant-side built-ins from Standard 19: Tenant Admin,
  Instructor, Learner, Guardian, Editor, Org Admin and Org Instructor. Tenant
  roles have NULL organization; organization role definitions are independently
  scoped to their exact organization. Platform Admin/Hub Operator have no rows
  in these tables. Portal Public remains a virtual anonymous capability.
- The closed Learner key has both Tenant and Organization variants. This is an
  explicit extension of Standard 19's Tenant-only Learner example, not a custom
  role name. An organization Membership can reference only role definitions of
  that exact organization; a tenant-wide Membership only Tenant definitions.
  The Organization Learner holds only Organization permission identities and
  grants no other-branch or Tenant capability. Other built-ins retain Standard
  19's listed scopes. Adoption updates that canonical scope table explicitly.
- A role definition's tenant, organization, built-in key and scope are immutable.
  Permission-grant changes belong to that Role root. Assignment changes belong
  to Membership; neither operation writes the other root implicitly.
- A grant identifies `(PermissionScope, PermissionKey)`, not a bare key. Keys
  retain the existing module/resource/closed-action grammar. A Tenant role can
  hold only Tenant definitions; an Organization role only Organization
  definitions. Unknown or inactive registry entries never authorize an act.
- Module code declares definitions and finite built-in grant manifests through
  explicit composition contracts, not a hypothetical `IModule` API. Persisted
  grants are selected from the composed registry; no tenant-defined permission,
  wildcard or realm/client role is an alternative source.
- Newly provisioned tenants receive the current finite initial manifests.
  Existing tenants do not automatically gain a new permission on registry/seed
  rerun. A separately admitted explicit grant change adopts it. The immutable
  Tenant Admin minimum is the tenant-scoped membership/role administration
  kernel; future additions outside that kernel start disabled for existing roles.
- Initial admin/member assignment is a declared bootstrap act, not inference
  from email, a Keycloak role or `UserId.SystemActor`. Seed manifests name the
  exact actor, tenant/org and role assignments and verify their semantic result.
  Ordinary repeat seed verifies existing grants; it does not repair revoked or
  operator-edited authority. A conflict refuses with a fixed diagnostic.

The foundation registry defines only keys its actual production operations use.
P03-2 completes enforcement and broader module registrations with their own
finite manifests. A registered key or stored role does not expose a route while
the current authorization shell remains pass-through.

### Scope composition, serialization and lifecycle eligibility

An active tenant-wide membership covers its tenant and its organizations; an
active organization membership covers only that organization. Having an org
membership also establishes association to its tenant for discovery, but grants
no tenant-wide administrative permission. A tenant-context permission check with
no organization cannot use an organization-only grant. Within an organization,
eligible tenant grants and that organization's grants compose; another branch's
assignments never participate.

Coverage also requires the global User to admit new work and the Tenancy-owned
tenant/organization lifecycle to permit it. Existing organization *belonging*
validation is not eligibility. Identity obtains current lifecycle facts through
a Tenancy application contract, not a database join. P03-2 owns live resolver and
permission execution; P03-3 owns safe target-scope transitions. A tenant-wide
permission or read hatch never widens organization INSERT/UPDATE/DELETE.

Before any authority-removing Membership or protected Role change, serialize the
tenant's administrative-invariant decision on a transaction-scoped tenant lock.
The common lock precedes the read/check and remains held through commit. No
second mutable aggregate is written merely to obtain a lock. Deferred database
guards cover direct/concurrent changes, including role grants that would strip
the admin kernel. Initial foundation provisioning admits a declared empty-to-
first-admin transition; it does not create a generally writable bootstrap flag.
These owners and guards require READ COMMITTED and refuse incompatible database
isolation. Acquire the lock before a separate fresh invariant query; VOLATILE
guard reads must observe a competing commit after a wait. No STABLE expression,
preloaded membership set or earlier transaction snapshot proves last-admin safety.

Preserve at least one active, globally eligible tenant-wide Tenant Admin for an
active tenant after bootstrap. Removing/suspending/revoking the last one or
stripping its required kernel refuses. Two independent membership row-version
checks are not sufficient. P03-9/G13 owns exceptional operator recovery; no
Development bypass is introduced. Tenant termination/global closure must settle
their exceptional final-admin case before their writer, under G16/G13.

Each Membership creation also acquires ADR-0072's User admission lock before the
tenant lock. Global closure takes the User lock before changing its admission
state; membership creation then observes Active or refuses. Role and membership
writers use this fixed lock order; future closure cannot acquire tenant first
and invert it. Last-admin eligibility uses the separately confined Boolean User
state read, not a global User SELECT or a second User lock while holding tenant.
Later global closure/status writers must acquire the affected tenant locks in
stable order before changing eligibility, and resolve the final-admin exception
under G16/G13 before activation. Tenant erasure or removal of the last membership
never closes the
global provider account. Last/default-organization archive remains Tenancy-owned
and cannot strand this invariant; its concurrency contract ships before P03-3's
organization endpoint, not as a foreign-key shortcut here.

### Pre-context reader and later authorization handoff

G2 accepts one new, named pre-context identity-authorization reader. It uses
`learnstack_app`, a short physically READ ONLY transaction, exact actor/candidate
tenant/org inputs and minimal coverage/grant results. Its input authority comes
from the validated provider/application identity; candidate scope is only a
question, not an installed tenant context. Return no profile or member directory.
Close the owner before ordinary request transaction creation. Explicitly add
this logical tenant setter to ADR-0040 and Security 11 before adapter execution.

P03-2 builds the immutable actor/tenant/org/operation-bound authorization result
at step 5; step 6 announces it on the ordinary physical transaction. Its read-
scope carrier narrows/expands EF and RLS consistently under the existing rule;
HostScope remains an independent ceiling. A check admits work based on its
committed snapshot: revocation stops subsequently admitted acts, not work already
admitted. No global permission cache, session grant or fresh JWT substitutes for
that check. Exact protected execution/denial witnesses remain P03-2/G5/G7.

P03-1 ships the schema/port/ownership contract, not a live reader. DenyAllTenant-
MembershipReader, the current HTTP surface and unset `app.scope` remain until
P03-2 qualifies the reader, rows 7/10/14, protected 6/9 and denied controls together.
Successful claim-only header mismatch needs its own evidence authority under
G2/G7 before row 14 activation; ADR-0065's host-tenant allowance is not generalized.

### Global identity data and diagnostic classification

User stores issuer/opaque subject, stable actor, admission lifecycle and the
minimum provider-canonical email/verification snapshot when present in the
validated bootstrap access token. Mirror fields are optional, type/bounds/
duplicate checked and separately classified. Missing email does not block actor
establishment; malformed supplied mirror fields refuse without persisting them.
Neither a request body nor an email lookup supplies these fields. Repeat
bootstrap does not overwrite them. A token snapshot is not proof of the current
verified email required by P03-4 invitation acceptance; P03-6/G10 owns later
provider-event ordering/update freshness before its writer.

No tenant/org, role, active-session selector, custom field, credential, refresh
token or browser binding lives on User. Person-owned display/avatar/preferences
are additive at their first separately admitted writer; P03-1 does not add
unused profile fields. P03-3 directories project authorized MembershipProfile
fields; an arbitrary UserId does not open global User or canonical provider data.

G19 introduces an explicit, typed personal-data declaration for every current
mapped application subject/property, including inherited actor references.
Include ADR-0072's new immutable audit subject-key/proposed-target paths; hash
values remain personal linkage and are not safe diagnostic labels.
Each is classified in the canonical closed categories, or explicitly None with
its nonpersonal purpose. Inventory is not discovered by searching for sensitive
names or counting only marked properties. Cross-check the declarations against
the actual nonempty EF models; a newly mapped property without a reviewed
classification fails. SQL-only/control/vendor copies retain the canonical
Data Protection inventory and their existing owner declarations; EF coverage
does not claim those stores were discovered by EF.

Module audit specs own their classifications and exact payload/reference paths;
do not duplicate the durable-store inventory. Mark personal scalar/whole JSON
values for audit snapshot redaction separately from their category. Arbitrary
tenant content is assessed explicitly for its actual purpose; publication alone
does not make every value safe for diagnostic capture.

Identity handlers/adapters use fixed diagnostic descriptors, bounded closed
reasons and surrogate correlation. They never log profile DTOs, JWT/UserInfo,
raw request bodies or provider/database exception text containing input.
Translate expected database conflicts and bounded upstream failures before a
value-bearing exception reaches the L1 logger/error tracker; do not retain the
original exception as an inner payload. Unexpected paths still emit a fixed
failure descriptor and required operational signal. This is a boundary on these
new writers, not a claim that global exception messages are scrubbed.

Snapshot redaction, logger name matching and LS0002 remain distinct controls.
LS0001/LS0002 stay Warning until P03-11. P03-5 extends classification to dynamic
definition revisions/custom values; P03-10 completes applicable reference
locators and export/erasure. Retained actor/subject linkage is pseudonymous,
not anonymous, and this foundation does not change retention or deletion grants.

### G4/G8/G16 storage implications

Active tenant/org belongs to the provider/session transition, never a mutable
global User selector. This foundation stores memberships, not a per-user active
scope. P03-3 must choose scope issuance that survives independent simultaneous
A/B refresh; editing provider user-global attributes is not that proof.

Invitation, custom-definition, export and closure-workflow tables do not land in
P03-1. G8's later Invitation-plus-Membership outcome needs its own accepted
atomicity/reconciliation vehicle before writing two independent roots; this
proposal adds no ADR-0042 exception. The Membership natural key and successor
contract support reconciliation without preselecting that workflow. G16's
permanent binding/admission fence closes now under ADR-0072; request authority,
participants, retained-copy transformations, artifact jobs and provider finalization
remain P03-10's mandatory workflow, not a waived foundation obligation.

## Context

At `46da68132987759b7ca6a19230b525b5485c7441`, Identity contains only markers.
Existing tenant filters do not supply a soft-delete predicate or `app.scope`
carrier. OrganizationScopeValidator proves belonging and nondeletion, not Active
status. SeedData has four authentication personas and two organizations in each
tenant; no application memberships or permission grants exist.

The first seeds preserve all four personas and give the declared administrators
tenant-wide Tenant Admin memberships. The English learner has a tenant-wide
Learner membership; Yoga's learner has an Organization Learner membership in the
host's default organization, with an explicit other-branch denial. No role scope
is inferred from the Keycloak role name. A separately declared cross-tenant/
multi-org proof subject exercises one
person in both tenants and two organizations, alongside another same-tenant
person; it is not an email merge or mutation of a legacy identity.

### Relationship and acceptance carriers

ADR-0017's flattened tenant-grants plus matching-org grants remains. ADR-0003's
canonical tenant/org policies and write guards remain. Its NULL organization arm
is deliberate visibility, not a private-profile promise. ADR-0042's only existing
multi-root exception remains Tenant plus default Organization provisioning.

From Database Standards § Soft Delete:

> A unique index on a table with a `deleted_at` column carries
> `WHERE deleted_at IS NULL`.

Membership successors follow it. The global issuer/sub/actor binding deliberately
does not recycle, under ADR-0072; acceptance adds its exact natural-key exemption
to the canonical standard and architecture guard. It does not broadly exempt
Identity tables or all User indexes.

Standard 19's disabled-default additions prevail over the historical automatic
Tenant Admin expansion; the finite initial/upgrade manifests make that choice
explicit. The scoped Learner variant is also a new named extension. Acceptance
updates that standard's authority and examples, not grants.
The P03-1 package identifies closed gate parts and keeps later execution Open.

Acceptance adds a dated ADR-0017 clarification for contained membership data and
scope composition, an ADR-0040 disclosure for the new candidate reader, and
ADR-0044 navigation for global identity audit/PII and later locator timing.
Existing Accepted text/metadata stays intact. Standards 02/05/11/18/19/21,
Architecture 03/09/13/23/28/31/33, glossary and Identity specs receive applicable
implementing details with their owners. Newly extended catalogue obligations
remain Registered until their actual controls ship; existing Implemented controls
retain status with named pending extensions.

Reopen for custom roles, different membership/profile granularity, a changed
last-admin/rejoin invariant, automatic grant expansion, global canonical-profile
disclosure or dynamic-field diagnostic capture. Those changes need a decision
before their first writer; a product screen cannot silently choose them.

## Consequences

### Positive

- One membership transaction owns contextual state and accurate mutation audit.
- Nullable keys, rejoin and scoped grants are deterministic under races.
- Explicit manifests avoid permission growth on an ordinary seed/upgrade rerun.
- A model-versus-declaration census detects missing classifications even when a
  property's name looks harmless.

### Negative

- Tenant-wide profiles are visible across branches by design. Branch-private
  data needs an organization membership, not a NULL-org record.
- Administrators explicitly adopt later permission additions; a new capability
  may remain disabled until that act. No wildcard administrator hides this work.
- Tenant administration mutations serialize for the invariant, and closure uses
  the global User fence. Contention/cancellation must fail without partial grants.
- Revoked memberships create successors; historical profiles/grants remain
  subject to retention and later erasure, not automatic resurrection or purge.
- A property census needs maintenance and human semantic classification. It
  cannot infer whether arbitrary text contains personal information.

### Neutral

- No Platform grant, Hub contract, live authorization, extra provider service,
  invitation/custom-field/DSAR table, storage adapter or UI is activated here.
- P03-2/3/4/5/10 retain their named execution/product obligations. Foundation
  ports/tests do not claim those routes or privileged workflows are delivered.

## Implementation Notes

The [P03-1 package](../roadmap/phase-03-delivery-log.md#p03-1-proposed-decision-package-2026-10-25)
owns the sequence and independent review cycles. Identity's complete module spec,
permission/audit matrices and source-bound proof accompany the first implementation.
Declare every table class and guard; do not copy the canonical tenant RLS template
into this ADR. New fixture data is semantic and credential-safe.

## Architecture Tests

Extend the canonical existing rules rather than inventing alternate spellings:

- `User_Aggregate_Has_No_TenantScoped_Columns` gains a real User model and planted
  tenant-authored/custom-field violation.
- Tenant/org marker/filter/RLS, composite FK/mirror, aggregate-write, audit capture
  and unique-index owners gain actual Identity subjects and isolated controls.
- `Permission_Definitions_DeclareScope` covers the nonempty foundation registry;
  broader resource-scope/denied enforcement remains P03-2's implementation.
- Register `Mapped_Properties_Have_Explicit_Personal_Data_Classification`, owned
  by `LearnStack.Tests.Architecture/DataProtectionConventionTests.cs`, with the
  complete mapped-property census and a planted unclassified neutral-name
  property. This planned owner lands in Step 2; registration alone is not an
  Implemented status. Diagnostic and database controls are behavioral proof.

## Behavioral Proofs

- App-role PostgreSQL NULL-org duplicate races, cross-tenant/branch reads and
  writes, lifecycle/successor behavior, parent mirrors and no global directory.
- Tenant/org grant composition, no reserved/unknown-role/key escalation, finite
  initial manifests, disabled later additions and completed seed no-write rerun.
- Concurrent last-admin revoke/suspend/role-kernel changes, first-admin bootstrap,
  cancellation, post-lock competing commit, incompatible isolation and User-
  admission/closure lock order. Dirty controls must prove
  each guard matters; a held handler is not evidence of a committed result.
- Profile/assignment child changes reach their one Membership audit; forced MUST
  failure rolls back writes. Tenant/global/profile PII and neutral-key nested
  canaries are absent from logs, responses, durable snapshots, local error archive
  and captured Sentry envelope, with a positive event received at each sink.
- Preserve the current whole-TenantSetting-value redaction proof and all four
  actor/provider identities. No skipped/empty population closes an obligation.

## References

- [ADR-0003](0003-tenant-isolation-defense-in-depth.md),
  [ADR-0017](0017-tenant-organization-hierarchy.md),
  [ADR-0032](0032-exception-handling-logging-and-observability.md),
  [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md),
  [ADR-0044](0044-audit-write-path.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md).
- [Permissions](../standards/19-permissions.md),
  [Database](../standards/05-database.md),
  [Data Protection](../architecture/23-data-protection.md),
  [Phase 03](../roadmap/phase-03-identity-admin.md).
- [Identity model](../architecture/13-identity-and-auth.md),
  [TenantQueryFilters](../../backend/src/LearnStack.Infrastructure/Persistence/TenantQueryFilters.cs),
  [organization validator](../../backend/src/LearnStack.Infrastructure/MultiTenancy/OrganizationScopeValidator.cs),
  [SeedData](../../backend/src/LearnStack.Tools.Seeder/SeedData.cs).
