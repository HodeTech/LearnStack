# ADR 0004: Authentication Strategy

## Status

Accepted

## Decision

LearnStack delegates user authentication to a self-hosted **Keycloak** OIDC provider. Authentik is the documented alternative if a tenant or environment cannot run Keycloak. LearnStack does not implement password authentication, token issuance, or session storage inside the .NET application.

The core identity model (User, Membership, Role, Permission, Invitation, AuditLog) remains provider-independent — Keycloak is the identity provider, LearnStack owns authorisation and tenant context.

## Context

Password login, refresh token rotation, account recovery, MFA readiness, brute-force protection, and session invalidation are high-risk capabilities. Hand-rolling them is a security liability and a recurring maintenance cost.

Alternatives considered:

1. **Keycloak (selected).** Battle-tested OIDC + federation + admin UI; self-hostable; controls data, deployment, upgrades.
2. **OpenIddict with ASP.NET Core Identity.** Tighter .NET integration but moves password/MFA/email-verification responsibility back into the application.
3. **Managed providers (Auth0, Clerk, Supabase Auth).** Fastest to ship but reintroduces vendor lock-in for a security-sensitive layer.

Keycloak fits the team's stated preference for self-hosted infrastructure and is consistent with the modular monolith's "providers behind adapters" rule.

## Consequences

- Phase 03 delivers identity domain (Membership, Role, Permission, Invitation, AuditLog) on top of Keycloak — no password hashing inside LearnStack.
- Keycloak realm strategy is single-realm with multi-tenant claims; realm-per-tenant remains an option for enterprise tenants on case-by-case basis (see [13-identity-and-auth.md](../architecture/13-identity-and-auth.md)).
- Federation/SSO is configured per-tenant in Keycloak; LearnStack exposes a tenant-admin UI surface to manage it.
- Provider-specific code stays in `LearnStack.Infrastructure.Identity.Keycloak`; the domain depends only on `IIdentityProvider`.
- Migration away from Keycloak (e.g., to Authentik) is mechanical because emails and stable user ids are the only contract LearnStack relies on; refresh tokens would not migrate and users would re-authenticate.

## References

- [13-identity-and-auth.md](../architecture/13-identity-and-auth.md) — full identity architecture.
- Superseded ADR file: [_redirects/0004-identity-strategy.md](_redirects/0004-identity-strategy.md) (kept for old links).

