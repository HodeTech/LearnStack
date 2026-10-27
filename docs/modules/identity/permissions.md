# Identity — Permission Matrix

**Status: Finite foundation definitions and seed commands implemented; live enforcement remains P03-2.** Derived from jointly Accepted [ADR-0072](../../decisions/0072-application-owned-identity-bootstrap.md)
and [ADR-0073](../../decisions/0073-scoped-membership-and-identity-data-foundation.md).
The live contract is jointly Accepted in
[ADR-0076](../../decisions/0076-live-scoped-authorization.md) /
[ADR-0077](../../decisions/0077-trusted-authorization-evidence.md); runtime remains pending.
The internal directory query, finite Role grant commands, fresh operation evaluator and exact policy source
are staged; no Identity HTTP route or live permission admission is activated. The [module spec](README.md)
owns storage/lifecycle; [Standard 19's accepted foundation contract](../../standards/19-permissions.md#proposed-identity-foundation-contract)
owns the exact registry, grant manifests and kernel without a second copy here.

## Foundation operation bindings

These **production** commands compile in P03-1 Step 4 and are called
only through the closed seed composition, not test placeholder types. Permission
metadata supplies actual registry subjects. P03-1 separately checks finite seed
authority before dispatch; normal API composition denies it. P03-2 owns live
permission admission. An attribute, realm role or SystemActor value grants no route.

| Command | Capability metadata | Independent P03-1 admission |
|---|---|---|
| `CreateFirstSeedAdminMembership` | Tenant `identity.membership.admin` | Exact declared admin, existing v1 Tenant Admin template, trusted adopted actor; User/tenant guards |
| `CreateSeedMembership` | Tenant or Organization `identity.membership.admin` | Exact declared learner tuple/target scope; no arbitrary actor/tenant/org/Role |
| `SetSeedMembershipProfile` | Tenant or Organization `identity.membership_profile.write` | Declared Membership and bounded contextual fields, through the Membership root |
| `InitializeSeedRole` | Tenant `identity.role.admin` | Exact tenant/template/v1 manifest, including Organization-capability templates; one Role root |

Scope selection derives from immutable target Membership/validated declaration,
never a claimed scope string. Finite metadata alternatives count the exact five
scoped pairs, not merely three different strings. Canonical actor/provider
adoption supplies identity facts; it does not independently grant seed authority.

## Resource matrix

| Resource | read | write | delete | admin | Foundation defaults |
|---|:---:|:---:|:---:|:---:|---|
| `Membership` | – | – | – | Tenant + Organization | Exact Standard 19 v1 manifest; includes contained assignments |
| `Role` | – | – | – | Tenant | Tenant Admin alone; authoring stays tenant-wide |
| `MembershipProfile` | – | Tenant + Organization | – | – | Exact v1 manifest, bounded contextual fields |

Empty cells mean no foundation key exists. Bootstrap/seed verification reads
do not invent directory/read keys; their exact owner/audit classification is
separate. P03-2 reuses the existing Tenant admin pairs for the internal directory and planned operations below;
new assignable definitions require their own approved finite manifest.

## Delegation and scope boundary

- Tenant-capability templates require NULL-org Membership. Organization-capability
  templates require non-NULL-org Membership, which supplies the exact branch.
- Eligible Tenant grants compose with that organization's grants. Another branch
  or a no-org Tenant check cannot consume an Organization grant. Tenant association
  for discovery is not administrative authority.
- Branch-scoped sessions use eligible Tenant grants for branch operations and
  explicitly declared tenant-wide reads, including the accepted directory.
  Tenant-wide writes require the explicit orgless reissue transition owned by
  P03-3; `app.scope` widens reads only. The [seed handoff](README.md#first-admin-and-seed-sequence)
  preserves Yoga admin's branch claim while using its declared NULL-org target.
- Org Admin cannot edit templates. Editing requires an eligible Tenant Admin
  binding and Tenant `identity.role.admin`; that capability is reserved to Tenant
  Admin and cannot be delegated to another built-in/Organization binding.
- Live writes cannot exceed the current delegation ceiling, self-escalate or
  remove the immutable kernel. The accepted authoring relation permits an eligible
  Tenant Admin with Tenant `identity.role.admin` to add an exact registered
  Organization pair only while holding that same key's Tenant pair. This is not
  evaluation equivalence; other-key, Organization-to-Tenant and unregistered-pair
  implications refuse. P03-2 proves this before activation.
- The two-pair admin kernel is deliberately smaller than the three-pair initial
  Tenant Admin bundle. P03-2 owns explicit audited grant adoption/removal outside
  that kernel. A later adopted manifest does not make the fixed v1 seed an
  upgrade writer; a mismatch, including a legitimate upgrade, refuses unchanged.
- A per-act committed snapshot defines revocation: later admissions see the change;
  already admitted work is not retroactively cancelled. JWT/session/UI caching
  cannot establish that boundary. P03-3 owns UI freshness/target transitions.

## Accepted P03-2 operation bindings — staged internal operations

| Operation | Existing capability | Boundary |
|---|---|---|
| `ListMembershipsQuery` (internal) | Tenant `identity.membership.admin` | Exact protected policy with invocation-bound tenant-wide reads, six approved contextual fields and MembershipId continuation. Fresh committed authority is required for every act. Ordinary ReadWrite; MUST sensitive-read audit. |
| `GET /api/v1/identity/memberships` | Tenant `identity.membership.admin` | Declared tenant-wide administrative read; an eligible NULL-org Tenant Admin Membership can authorize it from a branch session. Organization grants cannot. MUST sensitive-read audit; no native BFF expansion. |
| `ChangeRoleGrantsCommand` | Eligible Tenant Admin and Tenant `identity.role.admin` | Orgless write, current delegation ceiling/kernel, expected Role row version; recheck actor/target authority after invariant-lock wait. No HTTP writer. |
| `AdoptRoleGrantManifestCommand` | Same admin gate | Separate approved finite v1 manifest/diff and same concurrency/audit rules; unknown manifests refuse, no v2 bundle or automatic adoption. No HTTP writer. |

The five assignable scoped pairs, eight templates and fixed v1 seed remain.
Virtual Portal Public definitions are nonassignable and describe existing public
reads only; [Standard 19](../../standards/19-permissions.md) owns their exact set.
Default-deny HTTP and independent application checks consume fresh committed
policy facts. P03-9 retains Platform admission; a SystemActor UUID is no bypass.
`IdentityOperationPolicySource` classifies the six existing seed requests as
finite Seed operations, the internal directory and the two orgless Role grant
commands. The fresh evaluator distinguishes coverage from capability and
returns bounded Membership/Role references only. Opt-in registration does not
activate the HTTP gate or native transport.
The [Standard 05 read authority contract](../../standards/05-database.md#operation-bound-read-authority)
owns immutable invocation/transaction scope and EF/RLS agreement.

## Proof ownership

P03-1 Step 4 proves actual command metadata, the nonempty five-pair registry,
finite grants, assignment guards, disabled new-key additions, fixed drift refusal
and complete no-write rerun. Missing production subjects fail rather than leaving
an empty population. Organization Learner has own-studio positive and other-studio
denied controls. Live permission/default-deny/delegation proof stays P03-2;
no live result is claimed here.
