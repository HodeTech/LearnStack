# Phase 03: Identity, Authorization, and Admin Foundation

## Goal

Build LearnStack's identity domain (users, memberships with triple-key
`(user_id, tenant_id, organization_id)`, roles, permissions, invitations) on top of
the Keycloak OIDC integration delivered in Phase 02b, and ship the first admin
experience.

Authentication itself (password storage, MFA, token issuance, password reset, account
recovery) is owned by Keycloak — see
[ADR-0004](../decisions/0004-authentication-strategy.md) and
[13-identity-and-auth.md](../architecture/13-identity-and-auth.md). The **audit
trail** is owned by `LearnStack.Modules.Audit` — Identity does **not** own an audit
table; it emits MUST-audit events that the central pipeline captures
([ADR-0016](../decisions/0016-audit-log-subsystem.md)). The audit module and capture
pipeline were already wired in Phase 02a. This phase delivers the LearnStack-side
identity domain on top of those primitives.

## Scope

### Identity Model

- `User` — global identity mirrored from Keycloak (`sub`, email, display name).
- `UserProfile` — LearnStack-owned profile attributes.
- `Membership` — per-tenant **and per-organization** relationship; triple key
  `(user_id, tenant_id, organization_id)` per
  [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md). Carries roles and
  membership-specific profile data. A user can have memberships in multiple tenants
  and multiple organizations within one tenant.
- `Role` — Platform / Tenant / Organization-scoped per the scope catalogue in
  [19-permissions.md](../standards/19-permissions.md).
- `Permission` — fine-grained capability inside a role with an explicit scope
  (Platform / Tenant / Organization).
- `Invitation` — pending membership offer, bound to email + tenant + organization.

> **Audit ownership.** The `AuditEntry` aggregate lives in the **Audit** module
> ([ADR-0016](../decisions/0016-audit-log-subsystem.md)), not in Identity. Identity
> publishes integration events (`learnstack.identity.user`,
> `learnstack.identity.membership`, `learnstack.identity.role`) that the central
> capture pipeline turns into audit entries — and command-side mutations also flow
> through the shared `AuditLogBehavior`. There is **no** Identity-owned audit table.

### Authentication Integration

Keycloak owns credentials, password reset, email verification, MFA. Phase 03 wires LearnStack into that flow:

- OIDC token validation against Keycloak's JWKS (Phase 02b already configured the middleware).
- BFF session handling for the Next.js Studio: HTTP-only cookies, silent refresh, end-session at Keycloak on logout.
- Post-login membership lookup: resolve memberships for the authenticated user, surface the active tenant via host + claim cross-check.
- Tenant-specific Keycloak federation surface in Admin Studio (configure SAML/OIDC IdP per tenant) — UI delivered here, runtime in Keycloak.
- Mapping of Keycloak `sub` to LearnStack `UserId`; idempotent on first login.

LearnStack does **not** implement password hashing, password reset email rendering, refresh token storage, or brute-force protection — those are Keycloak responsibilities.

### Authorization

- Role-based authorization.
- Permission-based authorization.
- Tenant-scoped permission checks (`Membership.Roles → Permissions`).
- Resource-scoped policies (e.g., "instructor edits only own courses").
- Admin and Studio route guards.
- API authorization policies.

### Invitation Flow

- Tenant admin invites a user (email + role).
- Invitation token bound to the email; mismatched signup is rejected.
- New invitee redirected to Keycloak signup with prefilled email; existing user redirected to Keycloak login.
- After callback, LearnStack creates the `Membership` and marks the invitation accepted.
- Invitation expiry (default 14 days), accept/revoke endpoints.

### Admin Foundation (identity-management screens only)

Phase 03 ships the identity-management surface in Admin Studio. CMS, page-builder, and catalog screens are owned by Phase 06.

- Login (delegates to Keycloak).
- Tenant switcher (for multi-tenant operators).
- Users list and detail.
- Roles and permissions management.
- Invitations.
- Tenant member settings basics.

### Audit Coverage Wiring

Identity emits the following MUST-audit operations through the **central audit
pipeline** ([ADR-0016](../decisions/0016-audit-log-subsystem.md)); the
`AuditLogBehavior` in the MediatR pipeline writes entries via `IAuditStore` — Identity
itself never touches `audit_log` directly:

- Membership created / removed (per `(user_id, tenant_id, organization_id)`).
- Role assigned / revoked.
- Permission set changed.
- Invitation created / accepted / revoked.
- Tenant setting changed.
- Platform-admin cross-tenant access (`actor.platformAdmin = true` with a required
  `reason` field).
- Hub-operator access to a tenant resource (`actor.hubOperator = true`).

Keycloak owns its own audit stream for login success/failure, password reset, MFA
enrolment, and account lock. The Identity module **subscribes** to Keycloak webhooks
and re-publishes the relevant events as `learnstack.identity.user` integration events;
the Audit module consumes these and writes audit entries via `IInboxGuard`-protected
handlers. See [18-audit-coverage.md](../standards/18-audit-coverage.md) for the
MUST/SHOULD/MAY matrix.

## Deliverables

- Identity domain (`User`, `UserProfile`, `Membership` triple-keyed,
  `Role` with scope, `Permission` with scope, `Invitation`) on top of Keycloak.
- Admin login + tenant switcher + **organization switcher** via OIDC PKCE.
- Tenant- and org-aware user management screens.
- Role and permission system with explicit scope (Platform / Tenant / Organization)
  and resource-scoped policy hooks.
- Invitation flow end to end (tenant + organization bound).
- Identity events wired through the central audit pipeline (Audit module captures);
  Keycloak webhook → Dapr integration event → Audit consumer working end-to-end.

## Completion Criteria

- A tenant admin can only see users from their tenant; an org admin can only see
  users from their organization.
- Role and permission changes are enforced by both API and UI; permission-scope
  rejection (Tenant vs Organization) returns the right Problem Details code.
- Unauthorized users cannot access admin endpoints.
- Invitation flow is covered by integration tests, including email-mismatch and
  revoked cases, and across-organization invitations.
- MUST-audit identity events appear in `audit_log` via the central pipeline with
  before/after snapshots where applicable; no Identity-owned `audit_*` table exists.
- Cross-tenant operations from a platform admin role are audited explicitly with the
  required `reason` field; Hub-operator actions are audited with
  `actor.hubOperator = true`.

## Risks

- Re-implementing capabilities Keycloak already owns (password hashing, reset emails,
  token rotation). If a feature is in the Keycloak vs. LearnStack split table
  ([13-identity-and-auth.md](../architecture/13-identity-and-auth.md)), it stays in
  Keycloak.
- Designing the identity model only around the first tenant's needs (the English-
  learning showcase). Triple-key membership and org-scoped roles must serve every
  tenant shape, not only the showcase.
- Keeping roles too simple and postponing permissions until it is painful.
- Confusing global user identity with tenant membership.
- Building an Identity-owned audit table by accident. The Audit module is the only
  owner; Identity emits events.
- Relying only on frontend checks for admin authorization.

