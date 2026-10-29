# Identity — Permission Matrix

**Status: Foundation definitions, finite seed admission and live enforcement implemented.** Derived from jointly Accepted [ADR-0072](../../decisions/0072-application-owned-identity-bootstrap.md)
and [ADR-0073](../../decisions/0073-scoped-membership-and-identity-data-foundation.md).
The live contract is jointly Accepted in
[ADR-0076](../../decisions/0076-live-scoped-authorization.md) /
[ADR-0077](../../decisions/0077-trusted-authorization-evidence.md).
The directory, finite Role/Membership administration, own navigation and exact
policy source use independent canonical HTTP/application admission. The generated
OpenAPI/SDK contract owns their closed transport shapes.
The [module spec](README.md)
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
separate. P03-2 reuses the existing Tenant admin pairs for the directory and operations below;
new assignable definitions require their own approved finite manifest.

## Delegation and scope boundary

- Tenant-capability templates require NULL-org Membership. Organization-capability
  templates require non-NULL-org Membership, which supplies the exact branch.
- Eligible Tenant grants compose with that organization's grants. Another branch
  or a no-org Tenant check cannot consume an Organization grant. Tenant association
  for discovery is not administrative authority.
- Branch-scoped sessions use eligible Tenant grants for branch operations and
  explicitly declared tenant-wide reads, including the accepted directory.
  Tenant-wide writes require the explicit orgless native admission transition owned by
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
  implications refuse.
- The two-pair admin kernel is deliberately smaller than the three-pair initial
  Tenant Admin bundle. P03-2 owns explicit audited grant adoption/removal outside
  that kernel. P03-3's exact v2 adoption adds only the approved new-key delta;
  ordinary warm seed preserves a valid adopted graph without restoring removed
  grants or acting as an upgrade writer.
- A per-act committed snapshot defines revocation: later admissions see the change;
  already admitted work is not retroactively cancelled. JWT/session/UI caching
  cannot establish that boundary. P03-3 owns UI freshness/target transitions.

## Live operation bindings

| Operation | Existing capability | Boundary |
|---|---|---|
| `ListMembershipsQuery` | Tenant `identity.membership.admin` | Exact protected policy with invocation-bound tenant-wide reads, six approved contextual fields and MembershipId continuation. Fresh committed authority is required for every act. Ordinary ReadWrite; MUST sensitive-read audit. |
| `GET /api/v1/identity/memberships` | Tenant `identity.membership.admin` | Declared tenant-wide administrative read; an eligible NULL-org Tenant Admin Membership can authorize it from a branch session. Organization grants cannot. MUST sensitive-read audit; no native BFF expansion. |
| `ChangeRoleGrantsCommand` | Eligible Tenant Admin and Tenant `identity.role.admin` | Orgless write, current delegation ceiling/kernel, expected Role row version; recheck actor/target authority after invariant-lock wait. Exact protected HTTP writer. |
| `AdoptRoleGrantManifestCommand` | Same admin gate | Exact first v2 delta, current concurrency/audit guards and monotonic adoption marker; historical v1, repeated and unknown adoption refuse. Adoption is never automatic; exact protected HTTP writer. |
| `GetMembershipDetailQuery` | Tenant or exact-current-Organization membership-admin | Only the matched Tenant clause may mint tenant-wide read authority. The Organization clause stays scoped; ordinary ReadWrite owner commits MUST audit before the bounded detail DTO. |
| `GetOwnMembershipProfileQuery` | Exact own Membership coverage | Separate `OwnMembershipRead` policy, Off and module-declared physically ReadOnly. Exact actor/current organization/Active Membership, no target selector and no NULL-org fallback; no administration or profile-write grant required. |
| `ReadMembershipReceiptQuery` | Tenant or exact-current-Organization membership-admin | Scoped ordinary ReadWrite; exact original retained Membership, fresh live disclosure checks and MUST read audit; no mutation replay or successor substitution. |
| `ReadMemberProfileReceiptQuery` | Same current membership-admin disclosure | Exact original Membership receipt; former profile-write eligibility is not required. Scoped ordinary ReadWrite and MUST read audit. |
| `ReadOwnMembershipProfileReceiptQuery` | Exact current actor-owned Membership coverage | `OwnMembershipRead`, Scoped ordinary ReadWrite and MUST read audit; original actor and exact nullable organization, no profile-write requirement or NULL-org fallback. |
| `ReadRoleReceiptQuery` | Tenant `identity.role.admin` | Exact original retained Role; Scoped ordinary ReadWrite and MUST read audit. Reads require neither orgless scope nor grant-change/adoption eligibility. |
| `SetMembershipProfileCommand` | Applicable membership-admin AND profile-write | Four exact Tenant/Organization conjunctions; Active target in the current write scope, expected Membership version, fresh post-lock checks and enabled PreferredLocale. MUST update; no read hatch. |
| `SetOwnMembershipProfileCommand` | Applicable profile-write and exact ownership | Tenant or exact-current-Organization profile pair plus the actor's exact Active current Membership. No supplied member/User selector or NULL-org fallback; same version/lifecycle/locale/MUST boundary. |
| `ChangeMembershipStatusCommand` | Applicable membership-admin | Exact write scope, expected version and current graph; reactivate checks User admission, status/administrator effects determine fences, and last-admin protection remains. MUST security event; Revoked is terminal. |
| `ReplaceMembershipRoleAssignmentsCommand` | Applicable membership-admin and whole-Role delegation | At most eight distinct compatible Role IDs; Active/Suspended target in exact write scope. Added Roles' whole current grants must fit the live ceiling under retained Role-root locks, with actual User/administrator fences, fresh graph/version/plan checks and MUST audit. |
| `ListOrganizationMembershipsQuery` | Tenant or exact-current-Organization membership-admin | Non-NULL current organization; both alternatives remain Scoped, with MUST sensitive-read audit before the bounded page. |
| `ListRolesQuery`, `GetRoleDetailQuery`, `GetPermissionRegistryQuery` | Tenant `identity.role.admin` | Scoped current configuration reads; no orgless mutation/adoption predicate. MUST sensitive-read audit before disclosure. |
| `GetOwnMembershipContextsQuery` | Current own Membership coverage | Off, physical ReadOnly; exact current Tenant navigation derives active branches only from fresh tenant-wide own coverage. Opaque continuation is actor/scope-bound. |
| `GetOwnCapabilitiesQuery` | Current own Membership coverage | Off, physical ReadOnly; exactly nine advisory flags from current facts. False flags create no denial event and never authorize a later act. |

The historical v1 five-pair registry, eight templates and fixed initial seed
remain; the finite v2 registry adds three Tenancy pairs with separately admitted
first adoption. It changes no Identity template count or admin kernel.
Virtual Portal Public definitions are nonassignable and describe existing public
reads only; [Standard 19](../../standards/19-permissions.md) owns their exact set.
Default-deny HTTP and independent application checks consume fresh committed
policy facts. P03-9 retains Platform admission; a SystemActor UUID is no bypass.
`IdentityOperationPolicySource` classifies the six existing seed requests as
finite Seed operations, native session admission, the internal directory, two
orgless Role grant commands, the six Membership administration requests and
four Identity receipt reads above. Own-profile, own contexts, own-capability and
own-profile receipt reading have the separate coverage-only kind; the other
reads/writes are Protected. The fresh evaluator distinguishes coverage from capability and returns
bounded Membership/Role references only. All four Membership writers retain
Scoped read metadata and exact target write scope; a detail read witness never
authorizes their mutation. Protected HTTP replay independently admits current
original-target disclosure and never reuses the former mutation permission.
The [Standard 05 read authority contract](../../standards/05-database.md#operation-bound-read-authority)
owns immutable invocation/transaction scope and EF/RLS agreement.

## Proof ownership

P03-1 Step 4 proves actual command metadata, the nonempty five-pair registry,
finite grants, assignment guards, disabled new-key additions, fixed drift refusal
and complete no-write rerun. Missing production subjects fail rather than leaving
an empty population. Organization Learner has own-studio positive and other-studio
denied controls. P03-2 adds live HTTP/application default-deny, freshness,
delegation and pre-disclosure audit controls. The phase Status block owns
source-bound qualification; this matrix is not a test receipt.

## Bounded administration contract

**Accepted under [ADR-0078](../../decisions/0078-native-session-scope-and-self-entry.md)
and [ADR-0079](../../decisions/0079-bounded-identity-administration.md).**
The live table above names the application bindings. Closed administration
HTTP/SDK carriers share these policies; the six Identity/Tenancy receipt reads
support current-authority protected replay. The
[phase Status block](../../roadmap/phase-03-identity-admin.md) owns remaining
transport qualification and review.
[Standard 19](../../standards/19-permissions.md#bounded-administration-and-version-2-adoption)
owns exact scoped pairs, release adoption and finite compiled policy combinations.
An applicable pair means the declared eligible Tenant alternative or exact-current-
Organization alternative; another branch contributes nothing. Every actual act
rechecks current capability and target facts. The table adds no blanket role-name
grant or SQL/EF write widening.

| Operation family | Capability and target boundary |
|---|---|
| Own binding/bootstrap/global contexts | Exact native source and verified provider subject; no Tenant capability. Only opt-in bounded creation; no Membership or tenant-session grant. |
| Context selection/admission | Own current coverage/lifecycle and real host ceiling; no administration key. Initial selection/explicit switch commits MUST admission. Refresh preserves original scope and separately checks the new token/association and AccountSite; it does not repeat the admission event. |
| Tenant member directory | Tenant membership-admin; declared tenant-wide read witness, no write hatch. |
| Organization member directory | Applicable membership-admin and non-NULL current organization; exact branch SQL/EF even for the Tenant alternative. |
| Member detail | Applicable membership-admin; only its explicitly declared Tenant read alternative may widen reads. |
| Other member profile | Applicable profile-write AND membership-admin; exact target write scope. |
| Membership status/assignments | Applicable membership-admin; actual User/lifecycle/last-admin and whole-Role delegation/fence checks remain mandatory. |
| Own profile | Read exact actor-owned current Membership Off; write needs applicable profile-write, exact actor and write scope. |
| Role/permission registry reads | Tenant role-admin; no orgless condition on reads. Registry returns exactly eight assignable scoped pairs after v2 registration. |
| Role grant edit | Current eligible Tenant Admin with both kernel pairs, Tenant role-admin and orgless context; current delegation ceiling and protected kernel. |
| First v2 adoption | Same eligible actor/scope and exact approved release/target/version; only the three-new-key exception and monotonic marker in Standard 19. |
| Protected receipt read | Fresh original-target disclosure policy, exact actor/scope/self predicate and MUST read audit; no original mutation replay or successor substitution. |

The no-store, Off own-capability response contains exactly these advisory flags;
it has no ETag or grants. Each flag presupposes current User/coverage/lifecycle.
False previews create no denial event, and true previews authorize no mutation.

| Flag | Prerequisite from the operation table |
|---|---|
| `canReadTenantMembers` | Tenant member-directory capability |
| `canReadOrganizationMembers` | Current-branch directory capability, including its non-NULL-org condition |
| `canWriteMemberProfiles` | Applicable profile-write AND membership-admin; target scope checked separately |
| `canManageMemberships` | Applicable membership-admin; transition/delegation checked for each actual target |
| `canReadRoles` | Role-read capability |
| `canChangeRoleGrants` | Eligible Tenant Admin/role-admin and orgless write prerequisites |
| `canAdoptRoleRelease` | Same prerequisites; selected built-in key/release marker independently controls the action |
| `canWriteOwnProfile` | Applicable profile-write and exact actor-owned current Membership |
| `canManageOrganizations` | Tenant `tenancy.organization.admin` and orgless context |

Per-pair `canGrant` previews describe additions within the current ceiling and
explicit same-key Tenant-to-Organization bridge. False does not prevent removing
an existing nonkernel grant. Protected resource DTO ETags do not validate these
separate current previews. Native transport admits only the joint package's exact
operations; neither capability flags nor the permission registry creates a generic
proxy, Server Action or browser credential.
