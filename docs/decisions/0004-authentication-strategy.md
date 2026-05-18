# ADR 0004: Authentication Strategy

## Status

Accepted (Amendment 1: 2026-05-18 — adds `learnstack-hub` realm for LearnStack operators;
see bottom of document)

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

---

## Amendment 1 — `learnstack-hub` realm for operators (2026-05-18)

Per [ADR-0019](0019-learnstack-hub.md), LearnStack ships a separate operator-facing
application (Hub) with its own user population. This amendment formalises the realm split.

**Realm topology:**

| Realm | Purpose | User population |
|-------|---------|-----------------|
| `learnstack` (existing) | Tenant users — admins, instructors, learners, parents, guardians. Single realm with multi-tenant `tenant_id` JWT claim. | All LearnStack customer users. |
| **`learnstack-hub` (new)** | LearnStack operators — billing, support, compliance, plan management, custom domain approval, tenant lifecycle. | LearnStack staff only. |

**Per-tenant SSO** (enterprise tenants) remains a realm-level configuration inside
`learnstack` realm (Keycloak identity-provider brokering), unchanged from the original
decision.

**Realm-per-tenant** (for compliance-driven enterprise tenants who require isolated
identity) remains an option, also unchanged.

**Hub-side authentication:**

- Operators authenticate against `learnstack-hub` via OIDC Authorization Code + PKCE.
- Operator JWT carries `scope: "platform"`; the LearnStack runtime rejects platform-scope
  tokens on any non-platform endpoint. Conversely, `learnstack-hub` realm rejects any
  attempt to authenticate against tenant routes.
- mTLS + signed JWT + HMAC chain between Hub and LearnStack internal APIs is **independent
  of operator JWT** — service-to-service auth uses a Hub service-account client in the
  `learnstack-hub` realm.

**MFA requirements:**

- `learnstack-hub` realm enforces MFA mandatory for every operator.
- `learnstack` realm enforces MFA optional but recommended for tenant admins; tenant policy
  may make it mandatory.

**Audit:**

- Operator actions audit via Hub's own audit stream (ADR-0019).
- Cross-stream correlation by `correlation_id`.

The two-realm separation is a hard architectural invariant: an operator cannot be a tenant
user under the same identity, and vice versa.

