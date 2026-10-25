# 19 — Permissions Standards

**Status:** Adopted
**Derives from:** [11-security.md](11-security.md) § Authorization,
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0064 Tenant API Authentication Boundary](../decisions/0064-tenant-api-authentication-boundary.md)
(realm roles and provider scope do not grant application authority),
[01-architecture-standards.md](01-architecture-standards.md),
[18-audit-coverage.md](18-audit-coverage.md).
Identity foundation definitions, manifests and role ownership derive from Accepted
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md).

LearnStack uses permission-based RBAC layered on top of tenant membership. This standard pins the **naming convention**, **closed action set**, **registration pattern**, **enforcement points**, and the **per-module matrix template** that every module must produce.

**Implementation boundary.** No permission registry/grants are shipped yet.
Accepted ADR-0073 settles scoped definition identity, tenant-wide templates and
the finite initial seed manifests in the [Identity foundation contract](#identity-foundation-contract).
P03-1 Step 4 implements them; P03-2 owns live enforcement/delegation/adoption and
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
tenant Roles. Unknown/inactive pairs authorize nothing; Step 4 implements the
nonempty foundation registry.

| Scope | Examples | Granted to |
|-------|----------|------------|
| **Platform** | `platform.tenant.write`, `platform.feature_flag.admin`, `platform.audit.read` | Only platform admins / Hub operators; never assignable to a tenant role. |
| **Tenant** | `education.course.write`, `enrollment.course_access.read`, `tenancy.organization.write`, `customization.content_type.write` | Tenant roles; permission check requires a `Membership` in the target tenant. |
| **Organization** | `education.course.write` (org-scoped role), `enrollment.enrollment.write` (org-scoped role) | Org-scoped roles; permission check requires `Membership.OrganizationId` matches the resource's `OrganizationId` (or the role is tenant-scoped). |

Tenant permissions are only meaningful inside a resolved tenant context. Org-scoped
permissions further require the actor's `OrganizationId` match (or the actor have the
tenant-wide variant of the same role). Platform permissions bypass tenant scope but
require platform-admin authentication and write a `platform-admin` audit entry (see
[18-audit-coverage.md](18-audit-coverage.md)).

Registered architecture obligation
`Permission_Scope_Matches_Resource_Scope` requires permissions registered as
`Organization`-scope are only checked against `[OrganizationScoped]` resources, and
that `Tenant`-scope permissions never gain `OrganizationId` filtering at runtime
without an explicit `organization-scope` role binding.

## Module-Driven Registration

Permissions are declared in code. Module definition sources compose the available
scoped capabilities; registration grants nothing to existing Roles. The accepted
[manifest composition contract](#immutable-admin-kernel-and-one-manifest-source)
supplies finite initial grants and the admin kernel from one versioned source.
Its named types await Step 4; no `IModule` interface is introduced.

Rules:

- Renaming a permission key is a breaking change. Add the new key, dual-write during migration, drop the old key in a separate release. Renaming under the radar breaks every role assignment.
- Removing a permission requires an ADR + grace period (one minor release with a deprecation log).
- Adding a permission grants nothing to existing Roles. New-tenant initialization
  selects an explicit finite initial manifest version; later grant adoption is a
  separately admitted audited act under P03-2.

Seed/registry reruns preserve adopted grants and never perform upgrades. A
legitimate later manifest adoption takes precedence over fixed-v1 seed state;
that seed refuses a differing adopted version instead of repairing it. Changes
to the closed role/delegation contract require their owning decision.

## Enforcement Points

A permission is **only meaningful** if every code path that could expose the resource checks it. The list below is mandatory.

### HTTP endpoints

Every endpoint has one of:

- `[Authorize(Policy = "education.course.write")]` — explicit permission requirement.
- `[Authorize(Policy = "platform.*.<action>")]` — explicit platform-scope requirement.
- `[AllowAnonymous]` with a one-line comment explaining why (public site renderer, health checks, webhook receivers).

Architecture test fails the build for any endpoint missing one of these attributes.

### Background jobs and event handlers

Tenant-scoped jobs and integration-event handlers call `IAuthorizationService.AuthorizeAsync(actor, permission)` before mutating MUST-audit resources. System actors (outbox dispatcher, scheduled jobs) use a stable system identity with explicit permissions; ad-hoc bypass is forbidden.

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
`Portal Public` is the anonymous capability model, not a stored membership: its
eventual registry integration preserves the `HostOnly`/PublicSurface ceiling under
ADR-0036 and grants no protected authority.

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

**Status: Adopted — Accepted 2026-10-25; implementation pending.** Derived from
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md).
Current source has no registry, Role/Membership model or commands below. No route,
operator admission, grant or live permission is activated.

<a id="adoption-replacements"></a>

### Adopted scope and grant boundary

The active Scope, Module-Driven Registration and Built-in Roles sections above
adopt this contract. Implementation and the grants it guards remain pending.

P03-1's five pairs and version-1 grants below are the complete foundation set.
P03-2 owns the separately admitted finite live-authorization/grant-adoption
contract; P03-3 owns protected operations and UI/scope transitions. Neither a
later operation nor an older built-in-role example silently expands v1 grants.

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
validated orgless reissue, preserving the canonical write guards. The
`app.scope = 'tenant'` hatch remains read-only; a permission or Tenant assignment
cannot clear an authenticated branch scope implicitly.

### Exact foundation registry and operation owners

Version 1 contains **exactly five pairs**:

| Scope | Key | Planned production consumer, compiled in P03-1 Step 4 |
|---|---|---|
| Tenant | `identity.membership.admin` | `CreateFirstSeedAdminMembership`; Tenant variant of `CreateSeedMembership` |
| Organization | `identity.membership.admin` | Organization variant of `CreateSeedMembership` |
| Tenant | `identity.role.admin` | `InitializeSeedRole`, including Organization-capability templates |
| Tenant | `identity.membership_profile.write` | Tenant variant of `SetSeedMembershipProfile` |
| Organization | `identity.membership_profile.write` | Organization variant of `SetSeedMembershipProfile` |

These are planned compiled production commands, not existing types/test fixtures.
Finite scope metadata derives from immutable target/validated declarations.
Closed seed authority is checked independently before dispatch, default-denied
in API composition. P03-2 qualifies live permission admission before protected
activation; registration/attributes/seed success grant no HTTP route.

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
Matching completed seed acts do no writes; revoked/suspended/operator-edited
authority conflicts without repair/reset. A legitimate later manifest adoption
that differs from the fixed v1 seed also makes that seed refuse, preserving the
adopted state. Seed is not an upgrade reconciler. Realm roles/SystemActor cannot
supply manifests or authority.
[Identity's seed sequence](../modules/identity/README.md#first-admin-and-seed-sequence)
owns ordering and interrupted-run disposition.

### Enforcement and proof boundary

P03-1 Step 4 supplies nonempty actual-command registry, templates, reference/
scope guards and kernel artifact. P03-2 owns default deny/delegation, per-act
revocation/freshness, grant adoption and denied controls. P03-3 owns HTTP/new-user
BFF activation, protected UI and explicit scope transitions;
P03-9/G13 owns production first-admin/recovery before its production provisioner.
The development seed capability is not that future authority.

Canonical tests keep current status with explicit pending Identity extensions.
The [catalogue](21-architecture-tests-catalogue.md) registers the semantic property
census for Step 2 and foundation/manifests/scope controls for Step 4. Registered
controls remain unimplemented until actual clean/dirty subjects ship. Empty
censuses, stored Roles and diagrams are not implementation proof.
