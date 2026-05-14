# Phase 03: Identity, Authorization, and Admin Foundation

## Goal

Build LearnStack's identity domain (users, memberships, roles, permissions, invitations, audit) on top of the Keycloak OIDC integration delivered in Phase 02, and ship the first admin experience.

Authentication itself (password storage, MFA, token issuance, password reset, account recovery) is owned by Keycloak — see [ADR 0004](../decisions/0004-authentication-strategy.md) and [13-identity-and-auth.md](../architecture/13-identity-and-auth.md). This phase delivers the LearnStack-side identity model on top of that.

## Scope

### Identity Model

- `User` — global identity mirrored from Keycloak (`sub`, email, display name).
- `UserProfile` — LearnStack-owned profile attributes.
- `Membership` — per-tenant relationship; carries roles and tenant-specific profile data.
- `Role` — tenant-scoped except for built-in platform roles.
- `Permission` — fine-grained capability inside a role.
- `Invitation` — pending membership offer.
- `AuditLog` — append-only identity and admin activity.

### Authentication Integration

Keycloak owns credentials, password reset, email verification, MFA. Phase 03 wires LearnStack into that flow:

- OIDC token validation against Keycloak's JWKS (Phase 02 already configured the middleware).
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

### Audit

Events to record in `AuditLog`:

- Membership created/removed.
- Role assigned/removed.
- Permission set changed.
- Invitation created/accepted/revoked.
- Tenant setting changed.
- Platform-admin cross-tenant access.

Keycloak owns its own audit stream for login success/failure, password reset, MFA enrolment, and account lock — mirrored events propagate via Keycloak webhooks into the LearnStack `AuditLog` for cross-system queries.

## Deliverables

- Identity domain (User, Membership, Role, Permission, Invitation, AuditLog) on top of Keycloak.
- Admin login + tenant switcher via OIDC PKCE.
- Tenant-aware user management screens.
- Role and permission system with resource-scoped policy hooks.
- Invitation flow end to end.
- AuditLog wired into critical identity operations.

## Completion Criteria

- A tenant admin can only see users from their tenant.
- Role and permission changes are enforced by both API and UI.
- Unauthorized users cannot access admin endpoints.
- Invitation flow is covered by integration tests, including email-mismatch and revoked cases.
- AuditLog records critical identity events.
- Cross-tenant operations from a platform admin role are audited explicitly.

## Risks

- Re-implementing capabilities Keycloak already owns (password hashing, reset emails, token rotation). If a feature is in the Keycloak vs. LearnStack split table ([13-identity-and-auth.md](../architecture/13-identity-and-auth.md)), it stays in Keycloak.
- Designing authentication only around the first vertical product.
- Keeping roles too simple and postponing permissions until it is painful.
- Confusing global user identity with tenant membership.
- Relying only on frontend checks for admin authorization.

