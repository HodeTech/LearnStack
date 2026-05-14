# 19 — Permissions Standards

**Status:** Active
**Derives from:** [11-security.md](11-security.md) § Authorization, [01-architecture-standards.md](01-architecture-standards.md), [18-audit-coverage.md](18-audit-coverage.md).

LearnStack uses permission-based RBAC layered on top of tenant membership. This standard pins the **naming convention**, **closed action set**, **registration pattern**, **enforcement points**, and the **per-module matrix template** that every module must produce.

## Naming Convention

Permissions are dotted, lowercase, three-part keys:

```
{module}.{resource}.{action}
```

Examples:

- `education.course.write`
- `enrollment.entitlement.read`
- `classroom.recording.delete`
- `tenancy.feature_flag.admin`
- `identity.membership.write`

Rules:

- `{module}` matches the backend module key (`education`, `enrollment`, `classroom`, `tenancy`, `identity`, ...). Verticals namespace by their own prefix (`english.placement.write`).
- `{resource}` is the aggregate or sub-resource name in lowercase singular form. Multi-word resources use snake_case (`live_session`, `course_version`).
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

## Scope: Platform vs. Tenant

Permissions live in one of two registries; the registries are disjoint and validated at seed time.

| Scope | Examples | Granted to |
|-------|----------|------------|
| **Platform** | `platform.tenant.write`, `platform.feature_flag.admin`, `platform.audit.read` | Only platform admins; never assignable to a tenant role. |
| **Tenant** | `education.course.write`, `enrollment.entitlement.read`, `classroom.recording.delete` | Tenant roles only; permission check requires a `Membership` in the target tenant. |

Tenant permissions are only meaningful inside a resolved tenant context. Platform permissions bypass tenant scope but require platform-admin authentication and write a `platform-admin` audit entry (see [18-audit-coverage.md](18-audit-coverage.md)).

## Module-Driven Registration

Permissions are declared in code, not configured in a database. Each module exposes its permissions at startup; the platform registry composes them and seeds the database.

```csharp
public sealed class EnrollmentModule : ILearnStackModule
{
    public void RegisterPermissions(IPermissionRegistry registry)
    {
        registry.Tenant("enrollment.enrollment.read",   "View enrollments");
        registry.Tenant("enrollment.enrollment.write",  "Create or update enrollments");
        registry.Tenant("enrollment.enrollment.delete", "Cancel enrollments");
        registry.Tenant("enrollment.entitlement.read",  "View entitlements");
        registry.Tenant("enrollment.entitlement.write", "Grant or revoke entitlements");
        registry.Tenant("enrollment.cohort.read",       "View cohorts");
        registry.Tenant("enrollment.cohort.write",      "Create or update cohorts");
        registry.Tenant("enrollment.cohort.delete",     "Delete cohorts");
    }
}
```

Rules:

- Renaming a permission key is a breaking change. Add the new key, dual-write during migration, drop the old key in a separate release. Renaming under the radar breaks every role assignment.
- Removing a permission requires an ADR + grace period (one minor release with a deprecation log).
- Adding a permission is non-breaking; it ships disabled-by-default in seeded roles until a tenant admin enables it.

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

Permission-aware UI hides actions the user cannot perform — but the API is authoritative. The frontend never decides authorization on its own. UI checks call a generated permission catalogue (`usePermissions().can("education.course.write")`) that fetches the actor's permissions once per session and caches them.

## Permission Matrix Template

Every module ships this matrix as part of its module spec under `docs/modules/<module>/permissions.md`:

| Resource | read | write | delete | admin | Default role grants |
|----------|:----:|:-----:|:------:|:-----:|---------------------|
| `Course` | ✓ | ✓ | ✓ | ✓ | tenant-admin: all; instructor: read+write (own); learner: read (published) |
| `CourseVersion` | ✓ | ✓ | ✓ | – | tenant-admin: all; instructor: read+write (own) |
| `Lesson` | ✓ | ✓ | ✓ | – | tenant-admin: all; instructor: read+write (own course) |

Legend: ✓ = permission exists; – = does not apply (the closed action set means some cells are intentionally empty).

The "Default role grants" column declares which built-in roles receive which permissions on tenant provisioning.

## Built-in Roles

| Role | Scope | Auto-provisioned | Notes |
|------|-------|------------------|-------|
| `Platform Admin` | Platform | At platform install | Superset of platform permissions; never a tenant role. |
| `Tenant Admin` | Tenant | On tenant creation | Superset of tenant permissions for that tenant. Receives newly registered tenant permissions on next provisioning cycle. |
| `Instructor` | Tenant | Module-defined seeds | Read/write on courses, lessons, sessions, attendances where they are the owner/host. |
| `Learner` | Tenant | Module-defined seeds | Read on published catalog; write on own attempts, bookings, profile. |
| `Guardian` | Tenant | Off by default | Read on linked learner's progress and recordings (per consent). |
| `Editor` | Tenant | Module-defined seeds | Read/write on content, pages, media; no enrollment / billing access. |
| `Portal Public` | Tenant | Anonymous | Read on published public surfaces only. |

Verticals **add** roles; they do not modify these.

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
- Whether the change affects active sessions (forces JWT refresh).

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
