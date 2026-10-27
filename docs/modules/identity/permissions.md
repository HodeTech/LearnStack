# Identity — Permission Matrix

**Status: Finite foundation definitions and seed commands implemented; live enforcement remains P03-2.** Derived from jointly Accepted [ADR-0072](../../decisions/0072-application-owned-identity-bootstrap.md)
and [ADR-0073](../../decisions/0073-scoped-membership-and-identity-data-foundation.md).
No Identity HTTP route or live permission admission is implemented. The [module spec](README.md)
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
separate. P03-2/3 registers broader production operations before activation.

## Delegation and scope boundary

- Tenant-capability templates require NULL-org Membership. Organization-capability
  templates require non-NULL-org Membership, which supplies the exact branch.
- Eligible Tenant grants compose with that organization's grants. Another branch
  or a no-org Tenant check cannot consume an Organization grant. Tenant association
  for discovery is not administrative authority.
- Branch-scoped sessions use eligible Tenant grants only for branch operations.
  Tenant-wide writes require the explicit orgless reissue transition owned by
  P03-3; `app.scope` widens reads only. The [seed handoff](README.md#first-admin-and-seed-sequence)
  preserves Yoga admin's branch claim while using its declared NULL-org target.
- Org Admin cannot edit templates. Editing requires an eligible Tenant Admin
  binding and Tenant `identity.role.admin`; that capability is reserved to Tenant
  Admin and cannot be delegated to another built-in/Organization binding.
- Later live writes cannot delegate beyond current authority, cross scope, edit
  self into higher authority or remove the immutable kernel. P03-2/G5 qualifies
  these controls before a protected administration path activates.
- The two-pair admin kernel is deliberately smaller than the three-pair initial
  Tenant Admin bundle. P03-2 owns explicit audited grant adoption/removal outside
  that kernel. A later adopted manifest does not make the fixed v1 seed an
  upgrade writer; a mismatch, including a legitimate upgrade, refuses unchanged.
- A per-act committed snapshot defines revocation: later admissions see the change;
  already admitted work is not retroactively cancelled. JWT/session/UI caching
  cannot establish that boundary. P03-3 owns UI freshness/target transitions.

## Proof ownership

P03-1 Step 4 proves actual command metadata, the nonempty five-pair registry,
finite grants, assignment guards, disabled new-key additions, fixed drift refusal
and complete no-write rerun. Missing production subjects fail rather than leaving
an empty population. Organization Learner has own-studio positive and other-studio
denied controls. Live permission/default-deny/delegation proof stays P03-2;
no live result is claimed here.
