# 19 — Permissions Standards

**Status:** Active
**Derives from:** [11-security.md](11-security.md) § Authorization,
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0064 Tenant API Authentication Boundary](../decisions/0064-tenant-api-authentication-boundary.md)
(realm roles and provider scope do not grant application authority),
[01-architecture-standards.md](01-architecture-standards.md),
[18-audit-coverage.md](18-audit-coverage.md).
Identity foundation definitions, manifests and role ownership derive from Accepted
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md).
Live enforcement and evidence additionally derive from Accepted
[ADR-0076](../decisions/0076-live-scoped-authorization.md) and
[ADR-0077](../decisions/0077-trusted-authorization-evidence.md).
Native scope admission and bounded administration additionally derive from
[ADR-0078](../decisions/0078-native-session-scope-and-self-entry.md) and
[ADR-0079](../decisions/0079-bounded-identity-administration.md). The
[phase Status block](../roadmap/phase-03-identity-admin.md) owns implementation
and qualification state.
The separate fixed-seed prerequisite source derives from
[ADR-0080](../decisions/0080-fixed-seed-prerequisite-read-scope.md).
The separate fixed-origin completion source derives from
[ADR-0081](../decisions/0081-fixed-seed-completion-before-origin-adoption.md).

LearnStack uses permission-based RBAC layered on top of tenant membership. This standard pins the **naming convention**, **closed action set**, **registration pattern**, **enforcement points**, and the **per-module matrix template** that every module must produce.

**Implementation boundary.** The finite Identity definitions, template grants
and confined seed commands are implemented in P03-1 Step 4.
Accepted ADR-0073 settles scoped definition identity, tenant-wide templates and
the finite initial seed manifests in the [Identity foundation contract](#identity-foundation-contract).
The [live contract](#live-permission-enforcement-contract) supplies canonical
HTTP/application enforcement, delegation and explicit adoption;
P03-9/G13 owns operator admission. Adoption of this standard grants no runtime
authority. The closed key/action/scope rules remain binding.

## Naming Convention

Permissions are dotted, lowercase, three-part keys:

```
{module}.{resource}.{action}
```

Examples:

- `education.course.write`
- `enrollment.course_access.read`
- `classroom.recording.delete`
- `tenancy.feature_flag.admin`
- `identity.membership.write`

Rules:

- `{module}` matches the backend module key (`education`, `enrollment`, `classroom`,
  `tenancy`, `identity`, `customization`, `audit`, ...). There are **no vertical
  prefixes** — domain shapes are tenant data, not code. A permission key MUST NOT
  contain a domain term (`english`, `cefr`, `yoga`, `asana`, …).
- `{resource}` is the aggregate or sub-resource name in lowercase singular form.
  Multi-word resources use snake_case (`live_session`, `course_version`,
  `content_type_definition`).
- `{action}` is from the **closed set** below.

## Closed Action Set

| Action | Meaning |
|--------|---------|
| `read` | Read or list the resource. |
| `write` | Create or update the resource. |
| `delete` | Remove or soft-delete the resource. |
| `admin` | Full control including configuration, ownership transfer, and operations the other three do not cover. |

The set is **closed**: no `publish`, `approve`, `refund`, `enroll`, `cancel`, `grade`, `start_recording`, etc.

If a verb does not fit, model the verb as a **distinct sub-resource**:

- "Publish a course" → `education.course_publication.write` (a sub-resource that owns the publish lifecycle).
- "Grade an attempt" → `assessment.attempt_grade.write`.
- "Start a recording" → `classroom.recording.write` (creating a recording starts it).
- "Refund an order" → `billing.refund.write`.
- "Impersonate a user" → `identity.impersonation.write`.

This rule prevents permission sprawl and keeps the matrix template stable.

## Scope: Platform / Tenant / Organization

Permissions are identified by **`(PermissionScope, PermissionKey)`** at
registration time under ADR-0017/0073. Tenant and Organization can register the
same key as distinct definitions. Platform definitions are never assignable to
tenant Roles. Unknown/inactive pairs authorize nothing; P03-1 Step 4 implements the
nonempty foundation registry.

| Scope | Examples | Granted to |
|-------|----------|------------|
| **Platform** | `platform.killswitch.write`, `platform.tenant.write`, `platform.tenant_bootstrap.write`, `platform.identity_authority.admin`, `platform.delivery.read`, `platform.delivery.write`, `platform.federation.read` and `platform.federation.admin` (registered); the rest of the [ADR-0101 § 5](../decisions/0101-platform-operator-authority.md) catalogue joins with the step that ships the operation using it, never ahead of it | Only platform admins / Hub operators; never assignable to a tenant role. |
| **Tenant** | `education.course.write`, `enrollment.course_access.read`, `tenancy.organization.write`, `customization.content_type.write` | Tenant roles; permission check requires a `Membership` in the target tenant. |
| **Organization** | `education.course.write` (org-scoped role), `enrollment.enrollment.write` (org-scoped role) | Organization-capability templates assigned to an eligible Membership for the resource's exact organization; Tenant grants are separately evaluated. |

The registered Platform catalogue is `PlatformPermissions` in `LearnStack.SharedKernel`,
apart from the tenant registry and never assignable, so the served tenant document does
not change when it grows ([ADR-0101](../decisions/0101-platform-operator-authority.md) §
5). A key is registered with the operation that has a denied proof for it:

| Key | Operation | Packet |
|---|---|---|
| `platform.killswitch.write` | `tenancy.killswitch.toggle` | P03-9 Step 1 |
| `platform.tenant.write` | `tenancy.tenant.create` (tenant provisioning), `tenancy.hostmapping.write` (host mapping) | P03-9 Step 2 |
| `platform.tenant_bootstrap.write` | `identity.role.initialize`, `identity.membership.create_first_admin`, `identity.membership.recover_admin` | P03-9 Step 2 |
| `platform.identity_authority.admin` | `identity.authority.rebind` (the controlled issuer rebind, an offline tool and not a route) | P03-9 Step 3 |
| `platform.delivery.read` | `platform.delivery.inspect` (metadata-only inspection of dead-lettered work and quarantined jobs, in the platform scope) | P03-9 Step 4 |
| `platform.delivery.write` | `platform.delivery.replay`, `platform.delivery.reset` (the two fenced transitions, on the dispatcher's outbox role) | P03-9 Step 4 |
| `platform.federation.read` | `platform.federation.report` (the report of orphaned, unreadable and nonconforming identity-provider brokers, in the platform scope) | P03-9 Step 5 |
| `platform.federation.admin` | `platform.federation.remove`, `platform.federation.disable` (the two provider changes, each with an intent and a completion row) | P03-9 Step 5 |

The five onboarding acts of
[ADR-0102](../decisions/0102-production-tenant-onboarding.md) run under the
`PlatformOperator` authorization source, which is not a Membership candidate: their
policies are `FiniteTrusted` with that single source, the business actor of every row
they write is `UserId.SystemActor`, and the operator is recorded in audit only. The
grant is evaluated by the gate on every act, never cached across acts. The rebind of
[ADR-0104](../decisions/0104-controlled-issuer-rebind.md) is not an act of that executor
and not a route: the scope refuses an operation that needs no platform role, and the
executor runs only the closed catalog of five onboarding acts, which refuses the rebind
operation by construction (`PlatformOperatorActCatalog`). The offline tool that consumes
the admission validates the same token and evaluates the same grant. The two recovery
transitions of [ADR-0103](../decisions/0103-operator-recovery-of-asynchronous-work.md)
are likewise not pipeline acts: each operation records where it runs
(`PlatformOperationExecutor`: the pipeline, the platform scope, the offline tool or the
recovery owner), and the catalog admits only the first.

Tenant permissions are only meaningful inside an admitted tenant scope.
Organization grants require an eligible Membership for that exact organization.
An explicit eligible Tenant grant can independently authorize the declared
operation; same-key pairs never imply one another. Platform admission is decided by
[ADR-0101](../decisions/0101-platform-operator-authority.md) (operator scheme, per-act
grants held by LearnStack, a separate non-assignable catalogue) and ships in P03-9 with
its audit contract (see [18-audit-coverage.md](18-audit-coverage.md)).

Registered architecture obligation
`Permission_Scope_Matches_Resource_Scope` requires permissions registered as
`Organization`-scope are only checked against `[OrganizationScoped]` resources, and
that capability identity is never inferred from a storage scope or role name.
The Accepted operation witness below governs tenant-wide reads; a Tenant grant
alone never removes organization filters or widens writes.

## Module-Driven Registration

Permissions are declared in code. Module definition sources compose the available
scoped capabilities; registration grants nothing to existing Roles. The accepted
[manifest composition contract](#immutable-admin-kernel-and-one-manifest-source)
supplies finite initial grants and the admin kernel from one versioned source.
Its finite manifest and scoped definition types are implemented in P03-1
Step 4; no `IModule` interface is introduced.

Rules:

- Renaming a permission key is a breaking change. Add the new key, dual-write during migration, drop the old key in a separate release. Renaming under the radar breaks every role assignment.
- Removing a permission requires an ADR + grace period (one minor release with a deprecation log).
- Adding a permission grants nothing to existing Roles. New-tenant initialization
  selects an explicit finite initial manifest version; later grant adoption is a
  separately admitted audited act under P03-2.

Seed/registry reruns preserve adopted grants and never perform upgrades. A
legitimate later manifest adoption takes precedence over fixed-v1 seed state;
warm seed verifies valid installed state without restoring earlier grants. Missing
facts resume only under the confined pristine-prefix contract below. Changes
to the closed role/delegation contract require their owning decision.

## Enforcement Points

A permission is **only meaningful** if every code path that could expose the resource checks it. The list below is mandatory.

### HTTP endpoints

Every endpoint has an exact classification under the Accepted
[live policy boundary](11-security.md#live-authorization-contract): public,
AccountSite, protected scoped capability or finite trusted operation.
Named ASP.NET requirements agree with application metadata; infrastructure
endpoints are explicitly classified. The runtime missing-classification guard
supplements the endpoint census; authentication alone is no fallback permission.
Application step 5 independently repeats authorization on direct/nested dispatch.
The API and canonical application behavior enforce this contract.

### Background jobs and event handlers

Trusted seed, event and job sources keep their finite validated source/request
contracts. They do not gain a blanket SystemActor exemption, Platform permission
or authority from a causal actor field. Unknown sources/requests deny.
[Standard 05](05-database.md#authorization-and-evidence-source-ledger) owns their
tracked auxiliary authorization/evidence lanes and actual-cleanup lifetime.

### Resource-scope checks

Permission grants the **capability**; resource-scope decides **on which rows**. Two patterns:

- **Ownership filter** — applied as part of the EF query (`Where(c => c.OwnerId == currentUser.Id)`). Required for instructor-owns-their-courses style policies.
- **Policy class** — `IAuthorizationHandler<Operation, Resource>` evaluating the resource against the actor at the application layer.

Combining: an `instructor` role has `education.course.write`, but a policy class limits writes to courses where `Course.OwnerId == actor.UserId`.

### Frontend

Permission-aware UI hides actions the user cannot perform; the API is
authoritative. The target generated catalogue facade
(`usePermissions().can("education.course.write")`) is not shipped. G5 specifies
API revocation and UI invalidation/freshness; fetching grants once per session
alone proves neither. A stale enabled control still cannot authorize a mutation.

## Permission Matrix Template

Every module ships this matrix as part of its module spec under
`docs/modules/<module>/permissions.md`. The rows below are an example of its shape:

| Resource | read | write | delete | admin | Default role grants |
|----------|:----:|:-----:|:------:|:-----:|---------------------|
| `Course` | ✓ | ✓ | ✓ | ✓ | tenant-admin: all; instructor: read+write (own); learner: read (published) |
| `CourseVersion` | ✓ | ✓ | ✓ | – | tenant-admin: all; instructor: read+write (own) |
| `Lesson` | ✓ | ✓ | ✓ | – | tenant-admin: all; instructor: read+write (own course) |

Legend: ✓ = permission exists; – = does not apply (the closed action set means some cells are intentionally empty).

The "Default role grants" column documents the owning operation's approved finite
initial manifest. Illustrative example bundles do not register or grant keys;
P03-1's exact manifest below takes precedence for the foundation population.

## Built-in Roles

| Role | Capability scope | Template / assignment contract |
|---|---|---|
| `Platform Admin` | Platform | No tenant template; production admission remains P03-9/G13. |
| `Tenant Admin` | Tenant | Tenant-wide template; finite v1 initial grants and immutable kernel below. |
| `Instructor` | Tenant | Tenant-wide template; v1 grants empty. |
| `Learner` | Tenant and Organization | Two tenant-wide templates with separate scoped grants and compatible Membership assignments. |
| `Guardian` | Tenant | Tenant-wide template; v1 grants empty and no initial assignment. |
| `Editor` | Tenant | Tenant-wide template; v1 grants empty. |
| `Portal Public` | Tenant | Virtual anonymous capability; no stored Membership or template. |
| `Org Admin` | Organization | Tenant-wide template; compatible branch Membership supplies organization authority, with no initial assignment. |
| `Org Instructor` | Organization | Tenant-wide template; v1 grants empty and no initial assignment. |
| `Hub Operator` | Platform | No tenant template; authenticated chain/exact permissions remain P03-9/G13. A Hub token grants no tenant-route authority. |

P03-1 stores all eight tenant templates in Step 4. Template existence supplies no
authority: only eligible Membership assignments and active registered scoped
grants contribute. Off-by-default means no initial assignment, not an omitted
template. Capability scope never selects storage/authoring scope; Role has no
OrganizationId. The exact [v1 manifest](#version-1-built-in-grant-manifest) replaces
the earlier broad built-in examples and never expands automatically.

Tenant-driven customization **does not add roles**. Tenants extend behavior through
customization data; they configure role grants by composing the closed set above.

The historical proposed operator key used `operator` as its action, outside the
closed set. It is not a registration exception; G5/G13 choose a conforming key.
`Portal Public` is the virtual anonymous capability model, not a stored
Membership. Its Accepted exact definitions are in the
[live contract](#live-permission-enforcement-contract). Its API/application
integration grants no protected authority.

## Testing Requirements

Every endpoint and every job has, at minimum:

- An **authorised** test: actor with the required permission succeeds.
- A **denied** test: actor without the required permission receives `403 forbidden` (or `404 not_found` for tenant-scope mismatch).
- A **registration** test: `AssertPermissionIsRegistered("education.course.write")` proves the permission exists in the platform registry.

For resource-scoped permissions:

- A **cross-tenant** test: actor in tenant A with `education.course.write` cannot mutate a course in tenant B (returns 404).
- A **cross-owner** test: instructor with `education.course.write` cannot mutate another instructor's course (returns 403).

These tests are part of every module's test suite. Architecture tests verify that every registered permission has at least one denied test in the suite.

## Audit Cross-Reference

Permission grants and revocations are MUST-audit security-events ([18-audit-coverage.md](18-audit-coverage.md)). The audit entry records:

- The role mutated.
- The permission added or removed.
- The decided effect on active requests/sessions and the applicable revocation
  boundary under G5. Forcing provider JWT refresh alone does not enforce a
  LearnStack role/permission change; realm roles remain nonauthoritative.

## Forbidden

- Free-form action names outside the closed set (`grade`, `publish`, `cancel`, ...). Model them as sub-resources.
- Permissions whose name does not include a module prefix (`course.write` is wrong; `education.course.write` is correct).
- Storing permissions inside Keycloak realm roles. Keycloak only knows authentication; permissions live in LearnStack.
- Hard-coding permission strings inside policy code. Use the generated permission catalogue (`Permissions.Education.Course.Write`).
- Bypassing the registry by calling `_db.Permissions.Add(new Permission(...))` in a migration. Permissions are code-defined and seeded.
- Frontend-only enforcement: every UI permission check has a server-side counterpart.

## References

- [11-security.md](11-security.md) — security headers, transport, secrets, threat model.
- [18-audit-coverage.md](18-audit-coverage.md) — audit entries for permission changes.
- [13-identity-and-auth.md](../architecture/13-identity-and-auth.md) — Keycloak vs LearnStack identity split (Keycloak does authentication, LearnStack does authorization).
- [01-architecture-standards.md](01-architecture-standards.md) — module layout for `IPermissionRegistry`.

<a id="proposed-identity-foundation-contract"></a>

## Identity foundation contract

**Status: Foundation registry, models and seed commands implemented.** Derived from
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md).
API composition denies the seed capability. No route, operator admission or live
HTTP permission is activated by the foundation.

<a id="adoption-replacements"></a>

### Adopted scope and grant boundary

The active Scope, Module-Driven Registration and Built-in Roles sections above
adopt this foundation contract. The live boundary below enforces each act.

P03-1's five pairs and version-1 grants below are the complete foundation set.
P03-2 owns the separately admitted finite live-authorization/grant-adoption
contract, including its administrative GET. Protected HTTP operations and native
UI/scope transitions follow the [P03-3 frontend contract](07-frontend-architecture.md#native-entry-and-administration);
[Phase 03](../roadmap/phase-03-identity-admin.md) owns qualification and packet status.
Neither a later operation nor an older built-in-role example silently expands v1 grants.

### Capability scope and template ownership

`PermissionDefinition(PermissionScope, PermissionKey)` identifies a definition.
The same key at Tenant/Organization scope is two definitions; unknown/inactive
entries never authorize. Existing key grammar and closed action set remain.

Role templates are tenant-owned, **tenant-wide** roots. Capability scope does
not dictate storage scope or authoring: Role has TenantId, built-in key and
PermissionScope, with no OrganizationId. Only an eligible tenant-wide Tenant
Admin with Tenant `identity.role.admin` edits templates, including Organization
variants. No independent branch copy exists; needing that product capability
reopens ADR-0073. Organization creation consequently writes no Identity rows.

Assignments belong to Membership. Tenant templates require NULL-org Membership;
Organization templates require non-NULL-org Membership and apply to that exact
organization alone. Grants mirror Role capability scope. Storage as a tenant-wide
template never promotes an Organization grant to Tenant capability or widens writes.

A branch session may consume eligible Tenant grants for operations on that
branch. Tenant-wide Membership/Profile/template writes require P03-3's explicit
validated orgless native context admission, preserving canonical write guards. The
`app.scope = 'tenant'` hatch remains read-only; a permission or Tenant assignment
cannot clear an authenticated branch scope implicitly.

### Exact foundation registry and operation owners

Version 1 contains **exactly five pairs**:

| Scope | Key | Production foundation consumer |
|---|---|---|
| Tenant | `identity.membership.admin` | `CreateFirstSeedAdminMembership`; Tenant variant of `CreateSeedMembership` |
| Organization | `identity.membership.admin` | Organization variant of `CreateSeedMembership` |
| Tenant | `identity.role.admin` | `InitializeSeedRole`, including Organization-capability templates |
| Tenant | `identity.membership_profile.write` | Tenant variant of `SetSeedMembershipProfile` |
| Organization | `identity.membership_profile.write` | Organization variant of `SetSeedMembershipProfile` |

These are actual compiled production commands, distinct from test fixtures.
Finite scope metadata derives from immutable target/validated declarations.
The seed profile declaration binds its expected User and single Role assignment;
the handler verifies both before and after its tenant-lock wait.
Closed seed authority is checked independently before dispatch, default-denied
in API composition. Live protected admission separately rechecks current
capabilities; registration/attributes/seed success grant no HTTP route.

### Version-1 built-in grant manifest

Keep seven stored built-in keys; Learner has two capability variants, yielding
eight templates per tenant. Platform Admin/Hub Operator have no tenant rows;
Portal Public remains virtual anonymous capability. Empty means empty, without
future automatic expansion.

| Built-in key | Capability scope | Exact version-1 grant set |
|---|---|---|
| Tenant Admin | Tenant | Tenant `identity.membership.admin`; Tenant `identity.role.admin`; Tenant `identity.membership_profile.write` |
| Instructor | Tenant | Empty |
| Learner | Tenant | Tenant `identity.membership_profile.write` |
| Guardian | Tenant | Empty |
| Editor | Tenant | Empty |
| Org Admin | Organization | Organization `identity.membership.admin`; Organization `identity.membership_profile.write` |
| Org Instructor | Organization | Empty |
| Learner | Organization | Organization `identity.membership_profile.write` |

Organization Learner explicitly extends the earlier Tenant-only example, without
Tenant/other-branch grants. Instructor/Org Instructor/Guardian/Editor remain empty
until real owning operations register keys and separately admitted grants adopt
them. Later broader P03-2 manifests are finite; reruns cannot fill existing Roles.

### Immutable admin kernel and one manifest source

The minimum is **exactly two pairs**: Tenant `identity.membership.admin` and
Tenant `identity.role.admin`. Profile writing belongs to the initial bundle,
outside that minimum, and P03-2 may remove it through an explicitly admitted
audited grant change. Role-admin is reserved to Tenant Admin template/eligible
actors; an arbitrary grant or renamed Role cannot delegate template authorship.

One versioned compiled `IdentityFoundationManifest` supplies the adopted
`PermissionDefinitionSource`, `BuiltInRoleGrantManifest` and
`TenantAdminKernelManifest` composition. These are not current interfaces or an
`IModule` API. It generates the immutable SQL guard artifact; an equivalence
control compares complete scoped values/version/fingerprint with the compiled
manifest. No app-editable kernel table, wildcard/prefix or independently
maintained SQL list defines the minimum. Changes need the decision and guard
migration before dependent grants.

New tenants receive the explicitly selected current finite initial version.
Existing Roles retain adopted grants; registry/seed reruns add no new-key grants.
P03-2 owns separately admitted audited grant additions/removals outside the kernel.
Keep historical manifest versions/fingerprints for completed-act verification.
Matching completed seed acts do no writes. A valid installed graph may contain
suspension, historical Revoked Memberships, changed profiles/assignments and
adopted grants; ordinary warm seed preserves it without reactivation, successors,
reset or grant repair. Missing contained grants/assignments are never inferred to
be uninstalled from absence alone. Only a recognized pristine incomplete prefix
may resume, with current fence/version evidence rechecked at each possible write
under [Standard 05](05-database.md#p03-3-storage-and-replay-contract).
Incompatible/foreign or edited partial state refuses before remaining writes;
earlier committed facts remain. Ordinary seed never adopts v2 or v3. Realm roles and
SystemActor cannot supply manifests or authority.
[Identity's seed sequence](../modules/identity/README.md#first-admin-and-seed-sequence)
owns ordering and interrupted-run disposition.

### Enforcement and proof boundary

P03-1 Step 4 supplies nonempty actual-command registry, templates, reference/
scope guards and kernel artifact. P03-2 owns default deny/delegation, per-act
revocation/freshness, grant adoption and denied controls. P03-2 also owns the one
administrative directory GET. New-user HTTP/BFF activation, protected UI and
explicit scope transitions follow the [P03-3 frontend contract](07-frontend-architecture.md#native-entry-and-administration);
[Phase 03](../roadmap/phase-03-identity-admin.md) owns qualification and packet status;
P03-9/G13 owns production first-admin/recovery before its production provisioner.
The development seed capability is not that future authority.

The [catalogue](21-architecture-tests-catalogue.md) records the implemented
semantic property census and foundation/manifests/scope controls with actual
clean/dirty subjects. Empty censuses, stored Roles and diagrams alone are not
implementation proof. Current qualification state lives in the
[phase Status block](../roadmap/phase-03-identity-admin.md).

## Live permission enforcement contract

**Frozen definitions, live evaluation and explicit grant adoption are implemented.**
ADR-0076 replaces the live
admission/delegation deferrals above while retaining P03-1's exact five assignable
pairs, eight templates, two-pair admin kernel and fixed v1 seed manifest.
ADR-0079 adds the exact v2 contract below; ADR-0095 registers the explicit
v2→v3 delta in the [custom-field extension](#custom-fields-and-version-3-adoption).
Registration remains distinct from grants. Implementation and qualification state remain in the
[phase Status block](../roadmap/phase-03-identity-admin.md).

| Virtual Tenant definition | Existing PublicSurface requests |
|---|---|
| `tenancy.public_site.read` | `GetPublicSiteQuery` |
| `education.public_course.read` | `GetPublicCoursesQuery`, `GetPublicCourseQuery` |
| `education.public_lesson.read` | `GetPublicLessonQuery` |
| `identity.public_sign_in.read` | `GetPublicSignInQuery` ([ADR-0099](../decisions/0099-confined-tenant-federation.md#sign-in-exposure); Identity-owned) |

These four Portal Public definitions are unavailable as stored Role grants.
They create no Membership/template and widen neither publication, HostScope,
tenant/org scope nor physical READ ONLY bounds. Credential-free public requests
perform no identity/grant lookup; these definitions do not expand the assignable
foundation manifest.

Every protected act uses fresh committed Membership/lifecycle/grant admission
under [Security](11-security.md#live-authorization-contract). Eligible NULL-org
Tenant grants compose with the exact requested branch's eligible Organization
grants; coverage alone supplies no capability. Resolver, HTTP and earlier nested
results are not reusable grants. Tenant-wide read widening requires the
[operation witness](05-database.md#operation-bound-read-authority), never an
Organization grant or implicit clearing of authenticated branch write scope.

The separate [fixed-seed prerequisite witness](05-database.md#fixed-seed-prerequisite-read-authority)
derives only from the exact active finite Seed invocation and independently
captured fixed declaration. It grants no human permission and does not generalize
Protected-only `OperationReadWitness` minting. The closed temporary check runs
after the complete ordered plan, clears before any usable result or mutation and
cannot be inherited by human, event/job, origin-adoption or content writers.
Content seed writers instead obey the
[ordinary-scope per-write proof](05-database.md#every-tenant-bound-seed-write).
Standard 05 owns both contracts and their closed source/caller populations;
acceptance supplies no implementation proof or production provisioner authority.

The distinct [fixed-origin completion witness](05-database.md#fixed-seed-completion-read-authority)
admits only the fixed `AdoptSeedAdministrationOriginCommand` and its two existing
declarations. It binds the actual finite Seed invocation and immutable prepared
target snapshot under the retained complete plan; caller versions, hashes or
Booleans confer no authority. It never borrows the pristine witness or human
`OperationReadWitness`, adds no permission pair and grants no login, Role,
Membership or reactivation power. Its retained local administrator predicate
keeps both kernel pairs and preserves lawful nonkernel removals; it supplies no
fresh global User/provider eligibility claim. Qualified marker 2 plus the exact
live mapping permits only the runner's write-free repeat, not current protected
authority or a current mutable-graph proof. Standard 05 owns completion and
repeat rules; the phase Status block owns qualification.

`ChangeRoleGrantsCommand` and `AdoptRoleGrantManifestCommand` are separate
production application acts, each with one Role root and expected Role row version.
Both require current Tenant Admin/role-admin authority, orgless write scope,
concurrency/kernel guards and post-lock reauthorization. Ordinary grant changes
retain the current delegation ceiling and cannot grant authority the actor lacks;
only the exact first-v2-adoption exception below admits its three new keys, and
the separately accepted v2→v3 exception admits its one new pair.
An eligible Tenant Admin holding Tenant `identity.role.admin` may author an exact
registered Organization pair only while holding the same key's Tenant pair.
That is an explicit authoring relation, not scoped-key equivalence or evaluation
fallback. Other-key, Organization-to-Tenant and unknown-pair implications refuse.

Grant edits and finite approved release-manifest adoption have separate MUST
`identity.role.change_grants` / `identity.role.adopt_manifest` audit transactions,
including contained grant changes. Adoption accepts only the approved finite
manifest/diff; P03-2 proves retained v1, unknown manifests refuse, and registration
or rerunning seed never upgrades grants. Valid differing adopted state is preserved
by the accepted warm contract above. Human-facing edit/adoption routes belong to
P03-3; arbitrary Roles, wildcard grants and Platform admission are not added.

Actual denial evidence and successful sensitive disclosure obey
[Standard 18](18-audit-coverage.md#trusted-authorization-evidence), independently
of an ordinary operation's Off/SHOULD/MAY classification. Real HTTP, direct,
post-lock revocation, scoped SQL/EF and finite-source dirty controls must qualify
this contract before activation; acceptance alone advances no runtime proof.

## Bounded administration and version-2 adoption

**Accepted contract.**
Derived from [ADR-0079](../decisions/0079-bounded-identity-administration.md).
The finite registry has these versioned additions; the version-1 table and grant
manifest above remain historical inputs, not a seed repair target.

| Release | Assignable registry | Grant adoption |
|---|---|---|
| v1 | Exactly the five Identity scoped pairs above | Eight fixed initial templates; unchanged two-pair Tenant Admin kernel |
| v2 | Exactly Tenant `tenancy.organization.admin`, Tenant `tenancy.locale.write`, Tenant `tenancy.setting.write` in addition to v1; eight pairs total | Only the exact three-key Tenant Admin delta, by explicit first adoption |

No Organization Tenancy pair, custom Role or generic settings power is selected.
Tenant `tenancy.organization.admin` covers Organization create/rename/archive and
the Tenant-root default-reference sub-operation; target guards still prove the
selected Organization belongs to that Tenant and is Active/nondeleted. Locale
writing concerns contained Tenant locales; setting writing covers whole
tenant-wide `branding.theme` and the exact invitation policy extension below.
Their full editors and any separately selected
Organization-capability variants belong to Phase 06.

### Invitation capability extension

**Derives from:** [ADR-0093](../decisions/0093-verified-invitation-acceptance-and-handoff.md).
This Accepted contract adds no assignable pair, grant release, template or
automatic grant. P03-4 runtime qualification remains in the phase Status block.
Existing holders of Tenant `tenancy.setting.write` gain the additional exact
orgless operation on `identity.invitation.ttl_days`, 1–30 days. A v1 tenant
without that pair uses the 14-day default but cannot edit it. The explicit v2
adoption below remains the only exceptional path for those three v2 keys; registration,
warm seed and invitation creation cannot grant or restore the pair. An additional
invitation-policy permission is not selected. No generic setting writer follows.

Invitation administration uses applicable Tenant or exact-current-Organization
`identity.membership.admin`, exact current target/write scope and fresh complete
delegation; another branch contributes nothing. One pinned compatible Role is
Tenant-scoped iff the offer organization is NULL, Organization-scoped iff it is
non-NULL; the delegation bridge permits no cross-scope Role assignment. The Role's
whole current grants must fit the inviter's current ceiling, including the
existing same-key Tenant-to-Organization rule. A separate invitable-Role summary
does not require or confer `identity.role.admin` or general registry access.
Acceptance reads the stored inviter's current authority from the exact locked
offer; it does not mint an HTTP candidate for that other actor. The current
verified recipient, offer/host witness and exact invocation supply only the
finite inspection/acceptance/own-result admission, never membership-admin or
ordinary directory authority. Membership creation alone publishes no session.
The [Identity matrix](../modules/identity/permissions.md#verified-invitation-contract)
owns the finite operation bindings; [Security](11-security.md#verified-invitation-admission)
owns source, witness and owner limits.

### Explicit first v2 adoption

First v2 adoption requires an Active NULL-org Membership whose User admits work,
assigned the Tenant Admin template with both kernel pairs, current Tenant
`identity.role.admin`, orgless scope, expected Role version and exact approved
release/fingerprint. Its exclusive tenant fence and fresh post-wait checks admit
only the three new keys; prior nonkernel removals remain removed. Monotonic
adopted-release version/fingerprint on that Role prevents repeat adoption after a
new key is removed. Registration, ordinary grant edits and warm seed cannot use
this exception. Unknown releases/deltas refuse. New seeds retain v1 until a
separately selected finite initial manifest changes that declaration.

Compiled operation policies can express enumerated alternatives/conjunctions,
without a user policy language or global key implication. A declared branch act
may accept eligible Tenant authority or its exact branch's Organization pair.
Another branch contributes nothing. Each successful alternative retains its own
declared read scope; the organization directory never gains a Tenant read hatch.
Own profile requires applicable profile-write and exact actor ownership; another
person's profile additionally requires membership-admin. Write scope never widens.
Assignment changes recheck the actual target Role graph and current delegation
ceiling under retained ordered locks; [Standard 05](05-database.md#p03-3-storage-and-replay-contract)
owns the lock, adoption and isolated durable-receipt contracts.

Own-capability flags and per-pair addition hints are advisory, no-store, audit Off
and carry no resource ETag. A false preview emits no denial; each actual request
independently reauthorizes. [Identity's permission matrix](../modules/identity/permissions.md#bounded-administration-contract)
owns their exact operation bindings. Permission-registry/Role disclosures and
protected receipt reads are separately MUST audited before disclosure. Stored
successful receipts require current target-specific disclosure authority, not a
cached permission or reexecution of the original mutation.


## Custom fields and version-3 adoption

**Derives from:** [ADR-0095](../decisions/0095-revision-pinned-membership-custom-fields.md).
**Accepted contract; P03-5 implementation/proof pending in the
[Phase 03 Status](../roadmap/phase-03-identity-admin.md).** v3 is exactly v2 plus
Tenant `customization.custom_field.admin`: nine assignable pairs total, with no
Organization counterpart, wildcard, new template or automatic Editor grant.
Define the pair once in SharedKernel's bounded permission definitions;
Customization's module policy source registers it. Registration, definition
creation, startup, migration and warm seed grant nothing. Current new seeds
remain on their selected v1 manifest; valid installed v3 state is preserved.

### Explicit v2 to v3 adoption

Only the exact built-in orgless Tenant Admin role at exact v2 may deliberately
adopt v3, under current Active NULL-org Membership/global admission, both kernel
pairs, Tenant role-admin authority, expected Role ETag and retained exclusive
Tenant fence. Validate source marker/fingerprint and requested release/fingerprint
before the atomic one-pair delta, v3 marker advance and MUST audit. A v1 role must
first adopt v2; direct v1→v3, downgrade, custom/Organization role, unknown/mismatched
release or stale source refuses. Same-release replay retains the existing
outcome contract. Both exact v1→v2 and v2→v3 transitions remain discoverable in
registry DTO/UI.

The v3 fingerprint uses the existing unsigned-32-bit big-endian length-framed
SHA-256 algorithm over domain `learnstack.identity.role.release.v3`, exact v2
fingerprint, `TenantAdmin:Tenant`, then ordered delta
`Tenant:customization.custom_field.admin:True`. Historical v1/v2 manifest bytes,
fingerprints and shipped v2 helper/guard behavior remain unchanged, including on
a fresh migration chain. A forward migration adds v3 validation; it does not
rewrite the old generated artifact. The one-pair delta preserves prior nonkernel
v1/v2 removals, including profile-write or Tenancy pairs. No manifest restore or
seed repair is allowed.

### Exact operation authority

Definition list/exact revision/concept history and register/revise/publish/retire/
discard require Tenant `customization.custom_field.admin` in the applicable
orgless context. Existing Tenant/Organization `identity.membership_profile.write`
holders gain custom-value writes only under existing exact own/admin target
predicates; another person's write additionally requires membership-admin.
Own and administrative PATCH requests remain distinct. These grants supply no
global User mutation, broad definition directory, cross-org write or consent
creation power. The accepted package and module matrices own the closed request/
method/path census; generic proxy dispatch is not admitted.

GrantOwnMembershipFieldConsentCommand requires the actor's own current Active
Membership, admitted lifecycle/host/session and explicit exact notice confirmation,
without admin or profile-write permission. WithdrawOwnMembershipFieldConsentCommand
and retained history/discovery use only the distinct
[OwnConsentEntry contract](11-security.md#retained-consent-subject-admission).
They require verified Active global binding plus real-host-capped retained
subject evidence, without current Active Membership or administrative grant.
OwnMembershipRead remains read-only; these two exact commands confer no general
own-mutation authority. Same-key receipts reauthorize the original actor/target;
stale different-key predecessor conflicts and never silently retries.

A subject's own data requests use two key-less policy kinds
([ADR-0106 § 4](../decisions/0106-subject-data-requests-authority-and-status.md)).
`OwnDataRequest` requires the actor's own Active Membership in exactly the announced
group (a tenant-wide Membership does not cover an organization's group, nor the
reverse), through a live session, and consults no grant or Role. `OwnDataRequestEntry`
is the retained own scope: it is admitted only by the subject's own scope witness under
the `HostAndOwnDataRequestScope` origin
([Database Standards § Retained own data-request scope](05-database.md#retained-own-data-request-scope)),
so it also serves a Suspended person and a person whose Membership is gone, and it
supplies no Membership authority. Administrator requests are `Protected` with the
existing `[identity.membership.admin at Tenant, at Organization]` alternatives. No
assignable key is added and the grant union is unchanged.

`canManageCustomFields` is the tenth advisory administration capability: current
Tenant definition-admin authority in applicable orgless write context. Identity's
projection evaluates the registered SharedKernel pair with fresh facts/catalogue
without importing Customization.Domain. It is no grant, ETag or reusable mutation
admission. DTO/OpenAPI/SDK/native exact parsers, labels, manifests and capability
controls change together with activation; both API and direct application
policies remain authoritative after a stale/revoked flag. Exact current-target
protected receipt disclosure and the existing denial/audit mechanisms apply;
[Standard 05](05-database.md#custom-field-replay-key-custody) owns only the new
P03-5 HMAC fingerprint/key-metadata extension, preserving historical hashes.

## Federation and version-4 adoption

**Derives from:** [ADR-0099](../decisions/0099-confined-tenant-federation.md).
**P03-8 Step 1 (2026-11-13): registry, capability and adoption implemented; the
federation operations that consume the pair are Step 2's, and qualification is
owned by the [Phase 03 Status](../roadmap/phase-03-identity-admin.md).** v4 is
exactly v3 plus Tenant `identity.federation.admin`: ten assignable pairs total, with
no Organization counterpart, wildcard, new template or automatic grant. The pair is
defined once in SharedKernel (`FederationPermissions`); Identity's module policy
source registers it as Assignable. Registration, startup, migration and warm seed
grant it to no Role, and Keycloak realm roles grant nothing.

| Release | Assignable registry | Grant adoption |
|---|---|---|
| v4 | Exactly Tenant `identity.federation.admin` in addition to v3; ten pairs total | Only the exact one-key Tenant Admin delta, by explicit adoption from exact v3 |

### Explicit v3 to v4 adoption

The v3 rules above apply unchanged to the next exact transition. Only the built-in
orgless Tenant Admin role at exact v3 may adopt v4. A seed or existing
administrator adopts v1→v2→v3→v4 in order, each step through the existing
`AdoptRoleGrantManifestCommand`; direct v1→v4 or v2→v4, a wrong fingerprint, a
custom or Organization role, a stale source and a downgrade refuse. The one-pair
delta preserves every earlier nonkernel removal. The permission registry read
accepts source release 1, 2 or 3 and returns the next release, so v3→v4 is
discoverable in the registry DTO and the role page.

The v4 fingerprint uses the same length-framed SHA-256 algorithm over domain
`learnstack.identity.role.release.v4`, the exact v3 fingerprint,
`TenantAdmin:Tenant`, then `Tenant:identity.federation.admin:True`. Historical
v1–v3 bytes, fingerprints and shipped guards are unchanged; a forward migration
replaces the release guard and the registry/adoption CHECKs.

Every grant-array bound tracks the registry size, as the v3 transition did under
[ADR-0096](../decisions/0096-p035-administration-contract-baseline.md) and the v4
transition does under [ADR-0100](../decisions/0100-p038-federation-contract-transition.md):
the
registry `items`, `release.tenantAdminDelta`, Role detail `grants` and the
`RoleGrantsInput` body are each bounded at ten. The server's grant-change validator
bounds the request at the current release's definition count, and the SDK and BFF
strict parsers admit the same ten; a Role still holds only pairs of its own scope.

### Federation operation authority

Every federation operation that Step 2 adds is a Protected policy on Tenant
`identity.federation.admin` that declares `requiresOrglessWrite`, so a branch
context contributes nothing. Org Admin and every other role are unaffected.

P03-8 Step 2 (2026-11-13) registers these exact policies in
`IdentityOperationPolicySource`; each is Protected on Tenant `identity.federation.admin`
with `requiresOrglessWrite`, and none is a receipt or Idempotency-Key operation:

| Policy id | Request type |
|---|---|
| `identity.federation.read` | `GetFederationConnectionQuery` |
| `identity.federation.configure` | `BeginConfigureFederationCommand` |
| `identity.federation.rotate_secret` | `BeginRotateFederationSecretCommand` |
| `identity.federation.rename` | `BeginRenameFederationCommand` |
| `identity.federation.test_start` | `BeginFederationTestStartCommand` |
| `identity.federation.disable` | `BeginDisableFederationCommand` |
| `identity.federation.delete` | `BeginDeleteFederationCommand` |
| `identity.federation.resume` | `ResumeFederationOperationCommand` |
| `identity.federation.apply` | `CompleteFederationOperationCommand` (no route) |
| `identity.federation.reconcile` | `ReconcileFederationConnectionCommand` (no route) |
| `identity.federation.enable` | `EnableFederationCommand` |
| `identity.federation.activate` | `ActivateFederationCommand` |
| `identity.federation.test_record` | `RecordFederationTestResultCommand` |

Complete authorizes like any protected request: an actor whose authority was revoked
since Begin is refused, the operation stays pending and any current administrator's
Resume settles it. The handlers recheck the decision under the Identity tenant fence.
The public sign-in read is `PublicRead` on the virtual definition above.

`canManageFederation` is the eleventh advisory administration capability: current
Tenant federation-admin authority in an orgless context. It is computed by the same
projection as the other ten, is no grant, ETag or reusable admission, and the API
and application policies remain authoritative after a stale or revoked flag. The
DTO, OpenAPI snapshot, SDK and native strict parsers carry it together.
