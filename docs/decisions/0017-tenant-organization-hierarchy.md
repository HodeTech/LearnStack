# ADR 0017: Tenant + Organization Two-Level Hierarchy

## Status

Accepted

## Date

2026-05-18

## Decision

LearnStack adopts a **two-level hierarchy** for the entities a customer can manage:

- **Tenant** — an independent education platform built on LearnStack. Each tenant has its
  own domain (custom or subdomain), brand, locale set, plan, content, and user pool.
  Tenants are fully isolated from one another at every layer (DB rows via RLS, storage
  prefixes, search filters, cache prefixes, identity realm mapping, audit rows).
- **Organization** — a sub-unit *within* a tenant. Typical use cases: chain of language
  schools with multiple branches, a yoga studio franchise with several locations, a
  university with multiple faculties, a corporate L&D customer with multiple departments.
  Organizations within a tenant share that tenant's content catalog, branding, and plan,
  but have their own user rosters, role assignments, and (optionally) their own sub-domain.

LearnStack's domain model adds an `Organization` aggregate in the Identity / Tenancy module.
Every tenant-owned entity that is org-relevant gains a nullable `organization_id` column.

Permission scope is extended to **three levels**:

- **Platform scope** — LearnStack staff operations (Hub-managed; ADR-0019).
- **Tenant scope** — tenant-admin / tenant-user operations across all orgs in the tenant.
- **Organization scope** — operations bounded to a single org within the tenant.

## Context

The earlier LearnStack design (pre-2026-05-18) treated a tenant as a flat unit: one tenant =
one brand = one set of users. This works for a single-school customer ("My English Hero").
It does not work for several customer classes we want to serve:

- **Language school chain** with branches in Istanbul, Ankara, Izmir. Each branch has its
  own admissions, instructors, and operations team, but shares the same course catalog and
  brand identity.
- **Yoga studio franchise** where each studio runs its own schedule and recruits its own
  members, but the franchise owner sees consolidated reporting across studios.
- **University extension program** with separate departments (Engineering Ed, Business Ed,
  Music Ed) sharing the university brand and identity backend.
- **Corporate Learning & Development** where the tenant is a multinational company; each
  regional office is an org with its own administrative ownership but consolidated audit /
  billing / compliance up to corporate.

Nexora's experience (see `Nexora/docs/decisions/0012-tenant-management.md` and
`Nexora/docs/decisions/0025-org-scoped-compliance-config-with-platform-caps.md`)
confirmed that **Tenant +
Organization** is a sweet spot:

- Flat (tenant only) misses the chain / franchise / multi-department use cases.
- Multi-level arbitrary-depth hierarchy adds 2-3x complexity (recursive RLS, recursive
  permission evaluation, recursive UI) without proportional benefit for our target market.
- Two levels covers ≥ 95% of realistic education customers; the rare deeper hierarchy is
  modelled as flat orgs with an explicit `parent_org_id` for reporting only — not enforced
  in isolation.

## Decision drivers

1. **Customer fit.** Most education-platform customers we expect (language schools, yoga
   chains, corporate L&D, university extension programs) have a natural two-level
   structure.
2. **Defense-in-depth compatibility.** Adding an `organization_id` filter is a clean
   extension of ADR-0003's tenant-isolation model — same defense layers, one extra
   predicate.
3. **Avoid arbitrary-depth recursion.** Recursive RLS predicates, recursive permission
   walks, and recursive UI are non-trivial and rarely paid back. Two levels is the simplest
   model that covers the use cases without forcing the customer into a flat-tenant
   workaround.
4. **Reporting needs.** Tenant-level reports must aggregate over all orgs in the tenant;
   org-level reports must scope strictly. Both must be performant; org_id indexing supports
   this directly.
5. **Permission delegation.** A franchise owner (tenant admin) wants to delegate "manage
   your own studio" to a studio owner (org admin) without giving access to other studios.
   This is a first-class need.
6. **Audit clarity.** Every audit row carries `tenant_id` (mandatory) and `organization_id`
   (when applicable). A regulator inquiring about a specific branch can be answered without
   ambiguity.
7. **Battle-tested in Nexora.** Same Tenant + Organization model has shipped across CRM,
   Identity, Contacts, Documents, Notifications — no observed shortcoming.

## Considered options

### Option A — Flat (tenant only) — rejected

A tenant is a single unit. Multi-branch customers handle org separation in their own
naming or "tags" on user/contact rows.

**Pros:**
- Simplest possible model; least code; least cognitive load.

**Cons:**
- Forces multi-branch customers to fork tenants (3 branches = 3 tenants = 3 plans = 3 sets
  of duplicate course catalog data) or build their own org tagging on top of LearnStack
  (which means reinventing permission delegation, reporting filters, etc.).
- Permission delegation ("manage your own branch") cannot be expressed.
- Multi-tenant fork solution multiplies operational cost (3x plan price, 3x admin work).

### Option B — Tenant + Organization (chosen)

Two levels. Organization within tenant; users can be members of multiple orgs in the same
tenant.

**Pros:**
- Covers the customer use cases we expect.
- Same defense-in-depth pattern extended by one filter.
- Permission delegation expressible (org-admin role).
- Nexora-proven.

**Cons:**
- Domain model gains one more aggregate; every org-scoped entity gains one more column;
  every org-scoped query gains one more filter.

### Option C — Tenant + Organization + Sub-organization (multi-level) — rejected

Arbitrary depth org tree.

**Pros:**
- Maximum flexibility.

**Cons:**
- Recursive RLS predicates (`organization_id IN (SELECT id FROM org_tree WHERE root_id = ...)`)
  add EXPLAIN cost and lock contention.
- Permission inheritance walks become recursive and harder to reason about.
- UI: tree views, drag-drop, ancestor selection — all add Phase-09+-level complexity.
- Audit traces ambiguous ("the action was at org X which inherits from org Y which inherits
  from tenant Z").
- Rare actual need at our customer profile.

## Decision outcome

Adopt **Option B**: Tenant + Organization, two levels strict.

### Domain model addition

```csharp
namespace LearnStack.Modules.Identity.Domain.Entities;

public sealed class Organization : AuditableEntity<OrganizationId>
{
    public TenantId TenantId { get; private set; }
    public string Slug { get; private set; }                 // unique per tenant, used in URLs
    public string DisplayName { get; private set; }
    public string? CustomSubdomain { get; private set; }     // e.g. istanbul.englishhero.com
    public OrganizationStatus Status { get; private set; }   // Active | Suspended | Archived

    // Branding inheritance: inherits from tenant unless overridden
    public OrganizationBranding? BrandingOverride { get; private set; }

    // Self-reference for non-enforced "reporting parent" (rare cases)
    public OrganizationId? ReportingParentId { get; private set; }

    public static Organization Create(TenantId tenantId, string slug, string displayName)
    {
        // Guards, then construct. Emits OrganizationCreatedDomainEvent.
    }
}
```

Every existing org-scoped entity (e.g. `User` membership, `Course` ownership when org-scoped,
`Enrollment`, `LiveSession`, `Document`, audit rows) gains:

```csharp
public OrganizationId? OrganizationId { get; private set; }   // null = tenant-wide
```

### Defense-in-depth (extending ADR-0003)

| Layer | Mechanism |
|-------|-----------|
| Tenant isolation | `tenant_id` column + EF query filter + RLS policy + architecture tests |
| **Organization filter** | `organization_id` column + EF query filter + RLS policy + arch test |
| Identity | Keycloak realm-per-tenant; `organization_id` JWT claim populated from active org |
| Cache | Cache keys auto-prefixed `{tenant_id}:{organization_id}:{key}` when org context set |
| Files (SeaweedFS) | Bucket prefix `tenants/{tenant_id}/organizations/{org_id}/...` |
| Search (Meilisearch) | Query filter `tenant_id = X AND (org_id = Y OR org_id = null)` (tenant-wide content visible to all orgs) |
| Jobs (Hangfire) | Job payload carries both `TenantId` and `OrganizationId?` |
| Audit | Every audit row carries `tenant_id` (mandatory) + `organization_id?` |

### RLS policy template

```sql
ALTER TABLE <tenant_owned_table> ENABLE ROW LEVEL SECURITY;

CREATE POLICY <table>_tenant_isolation ON <table>
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY <table>_organization_isolation ON <table>
    USING (
        organization_id IS NULL                                                   -- tenant-wide row, visible to all orgs in tenant
        OR organization_id = current_setting('app.organization_id', true)::uuid   -- org-scoped row, only matching org
        OR current_setting('app.scope', true) = 'tenant'                          -- tenant-scope operation (admin, reporting) sees all orgs
    );
```

### Permission scope extension

Permission key format remains `{module}.{resource}.{action}`. Three scopes:

```csharp
public enum PermissionScope
{
    Platform,        // Hub-managed; LearnStack staff
    Tenant,          // Cross-org within one tenant
    Organization     // Scoped to one org within a tenant
}
```

A role can be Tenant-scoped or Organization-scoped:

- **Tenant-scoped role** (e.g. "Tenant Admin", "Brand Manager") — actions apply across all
  orgs in the tenant.
- **Organization-scoped role** (e.g. "Branch Admin", "Studio Manager", "Department Head") —
  actions scoped to one org. The user's `Membership` carries `(tenant_id, organization_id,
  role_id)` triples.

### Authorization handler

`PermissionAuthorizationHandler` (ADR-0011 superseded by ADR-0018, but the registry pattern
itself is preserved) — extended:

```csharp
protected override async Task HandleRequirementAsync(...) {
    var keycloakUserId = context.User.GetKeycloakUserId();
    var tenantId      = context.User.GetTenantId();
    var organizationId = context.User.GetOrganizationId();   // may be null

    var perms = await userPermissions.GetUserPermissionsAsync(tenantId, keycloakUserId,
        organizationId, ct);

    if (perms.Contains(requirement.Permission))
        context.Succeed(requirement);
}
```

`IUserPermissionService` returns a flattened set: tenant-scoped permissions (apply
everywhere in the tenant) ∪ org-scoped permissions (apply only when `organization_id`
matches the request context).

### Frontend implications

- Topbar: tenant switcher + org switcher (when user has multiple orgs in a tenant).
- Org switcher state in URL search param (`?org=istanbul`) for shareable links.
- `useOrganization()` hook reads active org; permission-checked components honour both
  tenant and org scope.

### Hub implications (ADR-0019)

Hub manages tenants and (optionally, opted-in tenants) organization scaffolding. Tenant-
admin can create / suspend / archive orgs through the tenant Admin Studio without Hub
involvement, but the operator-facing dashboard surfaces org counts and aggregated usage
per org for billing-tier sizing.

## Architecture tests

Five blocker-level architecture tests added in Phase 02:

1. `Every_TenantOwned_Entity_HasTenantId` — already required by ADR-0003.
2. `Every_OrgScoped_Entity_HasOrganizationId_Nullable` — auto-discovered by `[OrganizationScoped]`
   attribute or interface marker.
3. `Every_OrgScoped_Entity_HasOrgQueryFilter` — EF model assertion.
4. `Every_OrgScoped_Table_HasOrganizationRlsPolicy` — migration scan.
5. `Permission_Definitions_DeclareScope` — every registered permission declares its
   `PermissionScope` (Platform / Tenant / Organization).

## Consequences

### Positive

- Customer use cases (multi-branch, multi-department, franchise) supported natively.
- Same defense-in-depth pattern; one extra filter.
- Permission delegation expressible without forking tenants.
- Audit / reporting / search / files all carry org context cleanly.
- Nexora pattern transferable.

### Negative

- Every org-scoped table gains a column + a filter + an RLS policy + an architecture test.
- Frontend gains an org switcher and an `organization_id` URL parameter convention.
- Cross-org reporting (tenant-wide) requires either RLS bypass for tenant-admin role or
  explicit `app.scope = tenant` context-switching; the policy template above handles this.

### Neutral

- The vast majority of LearnStack customers will use **single-org tenants**. The
  organization layer is *opt-in* in the sense that a tenant without explicit orgs has one
  default org auto-created; UI hides the org switcher when only one org exists.

## Implementation notes

- Phase 02 — Platform kernel: `Organization` aggregate, `OrganizationId` strongly-typed ID,
  EF entity config, RLS policy template, `[OrganizationScoped]` attribute, architecture tests.
- Phase 03 — Identity module: `Organization` CRUD endpoints (tenant-admin scope), Keycloak
  attribute mapping for `organization_id`, `Membership` extension for org-scoped role
  assignments, JWT claim emission.
- Phase 06 — Admin Studio: org switcher in Topbar, org list / detail / create UI in
  tenant-admin section.
- Phase 09 — Billing: per-org usage aggregation surfaced to Hub for plan-tier sizing.

The conceptual model, ER diagram, RLS policy worked example, and onboarding flow live in
[28-platform-tenant-organization.md](../architecture/28-platform-tenant-organization.md).

## References

- ADR-0003 Amendment 1 (Organization scope addition).
- ADR-0011 — Superseded by ADR-0018 (Tenant-Driven Customization Model).
- ADR-0019 — LearnStack Hub.
- [28-platform-tenant-organization.md](../architecture/28-platform-tenant-organization.md) —
  architecture deep dive.
- [09-tenant-isolation.md](../architecture/09-tenant-isolation.md) — defense-in-depth
  details (revised to include organization scope).
- Nexora reference: `Nexora/docs/decisions/0012-tenant-management.md`,
  `Nexora/docs/decisions/0025-org-scoped-compliance-config-with-platform-caps.md`,
  `Nexora/docs/architecture/multi-tenancy.md`.

## Amendments

### 2026-05-19 — Identity row terminology

The "Identity" row in the defense-in-depth table reads "Keycloak realm-per-tenant".
Read this as a reference to the **realm-per-tenant opt-in** described in
[ADR-0004 Amendment 1](0004-authentication-strategy.md); the default Keycloak
strategy is **single-realm `learnstack` with a `tenant_id` JWT claim** (and
`organization_id` JWT claim populated from the active org membership). Realm-per-
tenant is an enterprise opt-in for compliance-driven isolation. Both strategies
satisfy the defense-in-depth requirement of this ADR; the live architecture guide
[09-tenant-isolation.md](../architecture/09-tenant-isolation.md) reflects the
corrected wording. This is a clarification; the Decision is unchanged.
