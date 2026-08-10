---
name: add-permission
description: >
  Register a new permission key in a module — closed action set
  (`read | write | delete | admin`), explicit scope (Platform / Tenant /
  Organization), default role grants, matrix entry, and `[Authorize(Policy = …)]`
  on the relevant endpoint. USE FOR: adding a permission for a new resource,
  splitting an existing resource into separate read/write capabilities, or
  promoting a "platform admin only" gate into a tenant-scoped permission. DO NOT
  USE FOR: free-form verbs (`publish`, `cancel`, `grade` — model these as
  sub-resources), domain-flavoured keys (CEFR, asana — forbidden), or runtime UI
  hiding without server-side enforcement.
---

# Registering a permission

## Purpose

Add a permission to the module's
[IPermissionRegistry](../../../docs/standards/19-permissions.md) using the closed
naming convention, register it on the right endpoint, and update the per-module
permission matrix.

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
| Default role grants | Yes | Which built-in roles get this on tenant provisioning. |

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

In `<Module>.Application/<Module>Module.cs`:

```csharp
public void RegisterPermissions(IPermissionRegistry registry)
{
    registry.Tenant(
        key: "enrollment.course_access.read",
        description: "View entitlements",
        defaultGrants: [Roles.TenantAdmin, Roles.OrgAdmin, Roles.Instructor]);

    registry.Tenant(
        key: "enrollment.course_access.write",
        description: "Grant or revoke entitlements",
        defaultGrants: [Roles.TenantAdmin]);

    registry.Organization(
        key: "enrollment.enrollment.write",
        description: "Create or update enrollments (org-scoped)",
        defaultGrants: [Roles.OrgAdmin, Roles.OrgInstructor]);
}
```

`registry.Tenant(...)` / `registry.Organization(...)` / `registry.Platform(...)`
declare scope explicitly; the three registries are disjoint, asserted at seed.

`Roles.*` references come from the **Built-in Roles** catalogue authoritative
at [19-permissions.md § Built-in Roles](../../../docs/standards/19-permissions.md)
(`TenantAdmin`, `Instructor`, `Learner`, `Guardian`, `Editor`, `OrgAdmin`,
`OrgInstructor`, `PortalPublic`, `HubOperator`, `PlatformAdmin`). Do not invent
role names; pick from that table or extend it via PR + standard update.

### Step 3: Apply to the endpoint

```csharp
[ApiController]
[Route("v1/enrollments")]
public sealed class EnrollmentsController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "enrollment.enrollment.read")]
    public Task<IActionResult> List(...) => ...;

    [HttpPost]
    [Authorize(Policy = "enrollment.enrollment.write")]
    public Task<IActionResult> Create(...) => ...;
}
```

The 4-step auth order ([11-security.md § Authorization](../../../docs/standards/11-security.md))
runs automatically:

1. Authentication (valid JWT).
2. Tenant membership (Membership exists in resolved tenant).
3. Role / permission (Membership's roles include the policy's permission).
4. Resource scope (handler-level — instructor edits own course only).

### Step 4: Resource scope (when needed)

For "instructor edits only own courses", add an authorization handler:

```csharp
public sealed class CourseOwnedByActorHandler
    : IAuthorizationHandler<CourseOwnedByActorRequirement, Course>
{
    public Task HandleAsync(AuthorizationHandlerContext ctx, ...)
    {
        if (resource.OwnerId == ctx.Actor.UserId) ctx.Succeed();
        return Task.CompletedTask;
    }
}
```

The application-layer policy class combines with the permission key:
permission grants the **capability** (`education.course.write`), policy decides
**on which rows** (own course only).

### Step 5: Update the matrix

Open `docs/modules/<module>/permissions.md`. Add a row:

```markdown
| Entitlement | ✓ | ✓ | – | – | tenant-admin: read+write; instructor: read |
```

Legend: ✓ = exists, – = N/A. The "Default role grants" column summarises
`defaultGrants` from the registration call.

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
public async Task Cross_org_actor_within_same_tenant_is_403() { ... }
```

Architecture test `Permission_Registry_Has_DeniedTest` ensures every registered
key has at least one denied test.

## Validation

- `dotnet build` and `dotnet test` pass.
- The permission is visible in `IPermissionRegistry` (assert via a one-off
  startup-test).
- The endpoint rejects unauthenticated, wrong-tenant, and wrong-permission callers
  with the right status codes.
- The permission appears in `docs/modules/<module>/permissions.md`.
- The architecture tests `Permission_Keys_Match_Convention` and
  `Permission_Scope_Matches_Resource_Scope` are green.

## Common pitfalls

- **Free-form action.** `course.publish` will be rejected. Model as
  `course_publication.write`.
- **Skipping scope.** A `Tenant`-scope permission checked against an
  `[OrganizationScoped]` resource without an org test is a bug; the matching
  architecture test catches it.
- **Hard-coding the key string in policy code.** Use the generated catalogue
  (`Permissions.Education.Course.Write`); typos in raw strings silently pass at
  compile time.
- **Frontend-only check.** Hidden buttons are not security. Every UI permission
  check has a server-side counterpart.
- **Storing the permission in Keycloak.** Keycloak handles authentication, not
  LearnStack authorization. Permissions live in `IPermissionRegistry` only.
- **Renaming an existing key.** A rename without a deprecation window
  invalidates every role assignment in the wild. Renaming is a deprecation cycle
  (one release with the old key warning-logged), not an in-place edit.
