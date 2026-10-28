---
name: add-permission
description: >
  Register a new permission key in a module — closed action set
  (`read | write | delete | admin`), explicit scope (Platform / Tenant /
  Organization), finite grant manifests, matrix entry, and `[Authorize(Policy = …)]`
  on the relevant endpoint. USE FOR: adding a permission for a new resource,
  splitting an existing resource into separate read/write capabilities, or
  promoting a "platform admin only" gate into a tenant-scoped permission. DO NOT
  USE FOR: free-form verbs (`publish`, `cancel`, `grade` — model these as
  sub-resources), domain-flavoured keys (CEFR, asana — forbidden), or runtime UI
  hiding without server-side enforcement.
---

# Registering a permission

## Purpose

Add an explicitly scoped definition through the module-owned source described in
[Permissions Standards](../../../docs/standards/19-permissions.md), bind the
admitted operation and update its matrix. Registration grants no Role authority.

> **Current implementation boundary.** P03-1 supplies `PermissionDefinition` and
> the retained `IdentityFoundationManifest.Version1` with actual seed consumers.
> Jointly Accepted ADR-0076/0077 select the
> [live contract](../../../docs/standards/19-permissions.md#live-permission-enforcement-contract);
> live policy/evaluator and grant-edit/adoption commands are implemented. The
> [Phase 03 Status block](../../../docs/roadmap/phase-03-identity-admin.md) owns
> current qualification and review state; the
> [phase decision register](../../../docs/roadmap/phase-03-identity-admin.md#the-decision-register)
> owns gate state. No `IModule` registration API or generated Roles facade is
> implied. Read the current source and accepted operation contract before coding;
> do not introduce a module loader to match an illustrative sketch.

## When to use

- A new resource needs an `[Authorize]` policy.
- A previously-coarse permission needs to split (`enrollment.write` →
  `enrollment.enrollment.write` + `enrollment.cohort.write`).
- A platform-admin-only gate is being promoted to a tenant or org permission.

## When not to use

- A free-form verb fits better. *Publishing* is `<resource>_publication.write` on a
  sub-resource (`education.course_publication.write`), not `course.publish`.
- A domain-specific key. `english.placement.write` is forbidden by ADR-0018; the
  English tenant uses the generic `assessment.attempt.write` against its own
  `TenantScoringRule` data.
- A UI-only hiding rule. Frontend visibility is mirror-only; the API must enforce.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Permission key | Yes | Dotted `{module}.{resource}.{action}`, snake_case multi-word resources. |
| Scope | Yes | `Platform` / `Tenant` / `Organization`. |
| Description | Yes | One short sentence shown in Studio's role editor. |
| Initial/adopted grant manifest | Yes | Explicit finite version; registration and seed reruns grant nothing to existing Roles. |

## Workflow

### Step 1: Pick the key

Format: `{module}.{resource}.{action}`. Examples:

```
education.course.read
education.course.write
education.course.delete
education.course_publication.write     ← "publish a course"
enrollment.enrollment.write
enrollment.course_access.read
tenancy.organization.admin
identity.impersonation.write
audit.export.read
platform.tenant.write                  ← Platform scope
```

Rules:

- `{action}` is **only** `read | write | delete | admin`. No `publish`, `approve`,
  `enroll`, `grade`. A verb that doesn't fit is modelled as a **sub-resource**.
- `{module}` matches the backend module key. `platform.*` is reserved for
  Platform-scope permissions.
- Snake_case for multi-word resources (`live_session`, `content_type_definition`).
- **No domain-flavoured keys.** Architecture test
  `Core_Modules_HaveNo_DomainSpecific_Names` rejects them.

### Step 2: Register in the module

Declare `(PermissionScope, PermissionKey)` through the admitted module definition
source. The current value-type shape is:

```csharp
new PermissionDefinition(PermissionScope.Organization, "enrollment.enrollment.write");
```

This example registers nothing by itself. The same key at Tenant and Organization
scope is two distinct definitions; Platform definitions cannot enter tenant Role
grants. Unknown/inactive pairs authorize nothing.

Pick the template/capability from the canonical
[Built-in Roles](../../../docs/standards/19-permissions.md#built-in-roles).
Keep definition composition separate from finite initial grants and explicit
adoption. Never extend retained v1 seed grants or the immutable admin kernel by
editing a registration's defaults. A new assignable pair also needs the actual
database registry guard/migration and approved grant contract before its writer.

### Step 3: Apply to the endpoint

Bind the endpoint to the admitted named ASP.NET policy and exact application
request policy from the actual composition source. Scoped pair identity is
explicit: a same-key Tenant/Organization pair is not one permission. Do not
infer a functioning evaluator from an attribute; use the registered evaluator
and policy APIs. Unknown, missing or conflicting classifications deny
at runtime. Application step 5 repeats the policy on direct/nested dispatch.

Public and special AccountSite classifications retain their independent
HostScope/physical mode boundaries. Platform and additional human-facing
operations remain gated by their owning phase; this workflow grants no route.

The admitted surface must prove the 4-step auth order
([11-security.md § Authorization](../../../docs/standards/11-security.md));
an attribute alone does not install it:

1. Authentication (valid JWT).
2. Tenant membership (Membership exists in resolved tenant).
3. Role / permission (Membership's roles include the policy's permission).
4. Resource scope (handler-level — instructor edits own course only).

### Step 4: Resource scope (when needed)

Use the admitted application-layer resource policy after capability admission
and visible-scope lookup. A missing or foreign-scope resource is masked 404; a
visible resource failing ownership is 403. The application policy consumes the
trusted actor and exact resource facts; module code acquires no ASP.NET types.

[Security's live policy contract](../../../docs/standards/11-security.md#live-authorization-contract)
owns this ordering. Declared tenant-wide reads use the immutable
[operation witness](../../../docs/standards/05-database.md#operation-bound-read-authority),
never a header/body Boolean, Role name or cleared organization write scope.

### Step 5: Update the matrix

Open `docs/modules/<module>/permissions.md`. Add a row:

```markdown
| Entitlement | ✓ | ✓ | – | – | tenant-admin: read+write; instructor: read |
```

Legend: ✓ = exists, – = N/A. The "Default role grants" column names the approved
finite initial manifest; it does not promise automatic grants on upgrade/rerun.

### Step 6: Tests

Add three tests per permission:

```csharp
[Fact]
public async Task Authorized_actor_succeeds() { ... }

[Fact]
public async Task Unauthorized_actor_is_403() { ... }

[Fact]
public async Task Cross_tenant_actor_is_404() { ... }   // tenant-scope mismatch returns 404, not 403
```

Plus, for org-scoped permissions, a cross-org test:

```csharp
[Fact]
public async Task Cross_org_actor_is_refused_without_disclosure() { ... }
```

Use the admitted surface's masking/denial contract for exact status assertions;
foreign resource scope and a visible ownership denial are different controls.
The canonical obligation `Permission_Registry_Has_DeniedTest` requires actual
denied coverage; verify its current catalogue status and nonempty subjects.

## Validation

- `dotnet build` and `dotnet test` pass.
- The exact scoped pair is in the admitted composition source; a missing or
  inactive definition denies, and registry additions do not mutate stored grants.
- The endpoint rejects unauthenticated, wrong-tenant, and wrong-permission callers
  with the right status codes.
- The permission appears in `docs/modules/<module>/permissions.md`.
- Check `Permission_Keys_Match_Convention`,
  `Permission_Scope_Matches_Resource_Scope` and
  `Permission_Registry_Has_DeniedTest` against their current catalogue status.
  Their P03-2 controls are Registered at acceptance; run the actual implemented
  owners before claiming green, and never promote an absent proof.

## Common pitfalls

- **Free-form action.** `course.publish` will be rejected. Model as
  `course_publication.write`.
- **Skipping scope.** A `Tenant`-scope permission checked against an
  `[OrganizationScoped]` resource without an org test is a bug; the matching
  architecture test catches it.
- **Duplicating key strings.** Reuse the owning compiled definition/constants;
  do not invent a generated catalogue that is not present in the source.
- **Frontend-only check.** Hidden buttons are not security. Every UI permission
  check has a server-side counterpart.
- **Storing the permission in Keycloak.** Keycloak handles authentication;
  LearnStack's admitted definition/Role contracts own application authorization.
- **Renaming an existing key.** A rename without a deprecation window
  invalidates every role assignment in the wild. Renaming is a deprecation cycle
  (one release with the old key warning-logged), not an in-place edit.
