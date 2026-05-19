# Keycloak — Two-Realm Identity (Dev)

Self-hosted Keycloak in dev compose, per
[ADR-0004 (Authentication Strategy)](../../docs/decisions/0004-authentication-strategy.md)
and [ADR-0019 (LearnStack Hub)](../../docs/decisions/0019-learnstack-hub.md). Two
realms, **hard-isolated from each other** — neither realm trusts tokens issued by
the other. This is an architectural invariant, not a configurable policy.

## Realms

| Realm | Purpose | User population | Clients | Demo users |
|-------|---------|-----------------|---------|------------|
| `learnstack` | Tenant-facing: admins, instructors, learners | All customer users | `learnstack-api` (confidential, service-account) + `learnstack-web` (public PKCE) | `demo-admin@tenant-a.test`, `demo-learner@tenant-a.test` (both `demo-dev-secret`) |
| `learnstack-hub` | Operator-facing: platform admin, support, billing-viewer | LearnStack staff only | `learnstack-hub-web` (public PKCE) | `demo-operator@learnstack.test` (`demo-dev-secret`) |

## Access

- **Admin console** — http://localhost:8080 (Keycloak Admin UI)
- **Master admin** — `admin` / `admin-dev-secret`
- **Tenant realm OIDC discovery** — http://localhost:8080/realms/learnstack/.well-known/openid-configuration
- **Hub realm OIDC discovery** — http://localhost:8080/realms/learnstack-hub/.well-known/openid-configuration

## Dev credentials are dev credentials

Every secret in this directory (master admin password, client secret, demo user
passwords) is **dev-only**. Production deployments load these from Vault via
`ISecretProvider` per [Standards 12 § Secrets Management](../../docs/standards/12-infrastructure.md)
and [Standards 20](../../docs/standards/20-infrastructure-stack.md). Do not reuse
any of these strings outside local Docker.

## Two realms, zero cross-trust

The realm separation is a **hard architectural invariant** per
[ADR-0004 Amendment 1](../../docs/decisions/0004-authentication-strategy.md#amendment-1):

- A `learnstack-hub` token MUST be rejected on every tenant-facing endpoint
  (the gateway + the backend both check the `iss` claim against the realm URL).
- A `learnstack` token MUST be rejected on every `/api/internal/*` endpoint
  (the Hub-internal contract gates on mTLS + signed JWT, not on user tokens).
- An operator account cannot also be a tenant user under the same identity, and
  vice versa.

The Phase 02b OIDC integration enforces this in code; the dev seed mirrors it
in data (no shared users, no shared client IDs).

## MFA

- `learnstack-hub` realm: `CONFIGURE_TOTP` declared as a required action so the
  TOTP enrolment flow is visible in dev; production enforces MFA mandatory for
  every operator per ADR-0004 Amendment 1.
- `learnstack` realm: MFA is **optional in dev**; tenant policy may make it
  mandatory per-tenant in Phase 03.

## How the realms are seeded

Keycloak boots with `start-dev --import-realm`. The JSON files under
`realms/` are bind-mounted to `/opt/keycloak/data/import/` and consumed once
at first start. To re-seed:

```bash
docker compose -f infra/compose/dev.yml down keycloak
docker compose -f infra/compose/dev.yml up -d keycloak
```

Re-import overwrites the realm only if the realm did NOT already exist (Keycloak
default behaviour). To force a clean re-seed of the realm itself, wipe the
Keycloak database first:

```bash
docker compose -f infra/compose/dev.yml exec postgres \
  psql -U learnstack -d learnstack -c "DROP DATABASE keycloak;"
docker compose -f infra/compose/dev.yml exec postgres \
  psql -U learnstack -d learnstack -c "CREATE DATABASE keycloak OWNER learnstack;"
docker compose -f infra/compose/dev.yml restart keycloak
```

## What does NOT live here

- Production realm configuration — managed via terraform / keycloak-config-cli
  per environment; the dev JSONs are not the source of truth for production.
- LearnStack-side identity domain (`User`, `Membership`, `Role`, `Permission`,
  `Invitation`) — that's Phase 03, owned by `LearnStack.Modules.Identity`. The
  realms here only define authentication; authorisation is application-side.
- OIDC code integration in the .NET API — Phase 02b wires `AddJwtBearer` and
  the BFF callback handler.
