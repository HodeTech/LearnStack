# Keycloak — Two-Realm Identity (Dev)

The pinned development provider is Keycloak 26.6.2. Two realms remain isolated
under [ADR-0004](../../docs/decisions/0004-authentication-strategy.md) and
[ADR-0019](../../docs/decisions/0019-learnstack-hub.md). The
[P02b-6 accepted package](../../docs/roadmap/phase-02b-events-auth.md#p02b-6-decision-package-2026-10-20)
owns development issuance and safe warm-realm convergence. Authentication grants
no application membership or permission; Phase 03 owns those records.

## Realms and accounts

| Realm | Population | Clients |
|-------|------------|---------|
| `learnstack` | Tenant users; four declared development accounts | `learnstack-web`: confidential code/S256 BFF; `learnstack-api`: resource and legacy service account |
| `learnstack-hub` | Platform operators | `learnstack-hub-web`: public PKCE; unchanged by P02b-6 |

[SeedData.IdentityUsers](../../backend/src/LearnStack.Tools.Seeder/SeedData.cs)
owns actor UUIDs and the account/tenant association. The read-only Seeder
`--identity-manifest` projection opens no database. The realm export is checked
against it, including the host's default organization where applicable.

- `demo-admin@tenant-a.test`, `demo-learner@tenant-a.test`: existing tenant-wide
  accounts and passwords are preserved on upgrade.
- `demo-admin@tenant-b.test`, `demo-learner@tenant-b.test`: the second tenant's
  default organization is included in its identity claims.
- Fresh development accounts use `demo-dev-secret`. Existing passwords, provider
  account IDs, MFA, required actions and unrelated attributes are not reset.
- The operator account remains `demo-operator@learnstack.test`; its separate
  realm is untouched. Neither realm's token is accepted by the other surface.

## Local access and private sources

- Admin console: <http://localhost:8080>; local master defaults are
  `admin` / `admin-dev-secret`.
- Tenant discovery:
  <http://localhost:8080/realms/learnstack/.well-known/openid-configuration>.
- Operator discovery:
  <http://localhost:8080/realms/learnstack-hub/.well-known/openid-configuration>.
- `.env.example` declares `KEYCLOAK_WEB_CLIENT_SECRET`. Compose passes it only
  to Keycloak; the import resolves its environment placeholder. The development
  reconciler and the later P02b-7 BFF are its other consumers. The API, public
  renderer and .NET Seeder never receive this secret.
- Older private `.env` files must add that key explicitly. Convergence verifies
  an existing confidential secret; a mismatch requires operator correction,
  rather than automatic rotation. Conversion of the old public client installs
  the selected private source once.

These credentials are development-only. Production provisioning and rotation
belong to Phase 11 and [Infrastructure Standards](../../docs/standards/12-infrastructure.md#secrets-management).
Do not reuse local secrets in production or put them in command arguments,
URLs, logs or public configuration.

## Tenant client contract

`learnstack-web` permits authorization code with S256 PKCE and a server-held
secret. Password grants, implicit flow, service accounts and browser CORS origins
are disabled. Only these exact callbacks are registered:

- `https://demo-english.learnstack.local:3000/api/auth/callback/keycloak`
- `https://demo-yoga.learnstack.local:3000/api/auth/callback/keycloak`

Post-logout redirects are the corresponding exact HTTPS roots on port 3000,
joined with Keycloak's `##` attribute separator. No wildcard or localhost HTTP
callback remains. Host-file and trusted development TLS setup are still needed
for a browser; P02b-7 owns the callback/session implementation. Provider tests
capture the redirect without following it and do not claim a browser session.
Their isolated provider uses HTTPS with a generated fixture CA and normal
hostname verification; this does not change the selected Compose issuer or
install certificates in the developer's trust store.

`learnstack-api` has no browser code flow or redirects. Its legacy service account
and secret are retained; its tokens cannot satisfy the API's user-token profile.
Both named clients attach `learnstack-tenant-api` as a default scope:

- `user_id`, optional `tenant_id` and optional `organization_id` are scalar String
  user-attribute claims in access and ID tokens. Profile view/edit permission for
  all three attributes is admin-only; organization is protected too.
- The explicit `learnstack-api` audience is added to access tokens only.
- The export includes the actual pinned built-in scope definitions, especially
  `basic` and its `sub` mapper. Listing a missing built-in name is insufficient
  when an import supplies its own `clientScopes` collection.

The configured tenant-realm access-token lifespan is 900 seconds, without
per-client overrides. [Security Standards](../../docs/standards/11-security.md#authentication)
owns the distinction between configured lifespan, observed timestamps and the
API acceptance bound. Previously issued tokens are not immediately revoked.
The Hub realm retains its own settings.

Existing realm roles and `fullScopeAllowed: true` remain a development posture;
the API derives no membership, permission or platform authority from them.
Production client scope mapping, refresh rotation and MFA enforcement remain
Phase 11 work; Phase 03 owns tenant identity policy. Tenant MFA stays optional in
this development export; the operator realm keeps its declared TOTP action.

## Fresh import and warm upgrade

Keycloak starts with `start-dev --import-realm`, reading both committed exports
only when their realms do not already exist. Restarting a populated database
does not apply export changes. Dropping the database or replaying a whole import
is not an upgrade procedure.

P02b-6 Step 1 supplies the executable fresh import. Steps 2–3 implement the
bounded shared Admin REST reconciler and connect it to `make seed` / `make demo`
after application seed convergence. Until those steps ship, a populated realm
does not converge through those entry points. Direct .NET Seeder remains an
application-data tool; its identity manifest is a read-only declaration.

The accepted warm-upgrade contract preserves nonmanifest accounts, credentials,
provider IDs and all Hub state. Only known legacy seed attributes are migrated;
incompatible identities, effective mapper conflicts and private-source mismatch
refuse before writes. Partial Admin REST changes are durable, without automatic
rollback. A later explicit run re-reads and resumes; a completed rerun makes no
mutating Admin calls. Use the packet's delivery records for implementation and
qualification status.

Admin user responses are profile projections, not raw attribute storage. Before
interpreting a missing identity as absent, the reconciler requires an
admin-readable definition without a default or selector, or an explicitly
admin-readable unmanaged policy. Hidden identities and external user-storage
profiles refuse before any write. The tool never changes visibility to discover
whether a conflicting identity exists. For an older realm that hides these
values, an operator must first inspect the profile configuration, make the three
identity definitions admin-only readable, and inspect their stored values before
retrying. Do not enable end-user editing or replace a conflicting UUID to force
convergence. The existing unmanaged policy remains unchanged by reconciliation.

Protected claims must come from user attributes. Aggregation and inherited
group identity values are incompatible with that contract and refuse before
writes. Safe ordinary groups and memberships are preserved. The bounded group
inventory includes nested groups. Unpaginated membership reads follow the
mapper's group stream; any exposed membership outside that ordinary inventory
refuses before writes. The pinned cached model excludes Keycloak Organization
groups from that stream, including retained memberships after the feature is
disabled. Its actual-token control is separate from the defensive refusal for
unknown membership IDs. This is separate from LearnStack's own organization IDs. The named single-user import does not inherit
realm default groups; new-account membership is independently checked as empty.

The entire projected change must also fit the existing bounded readers and
request bodies. A configuration that would exceed those limits refuses during
planning, before an earlier unrelated change can be committed.

## What does not live here

- Production realm configuration: Phase 11 provisioning owns it; these JSONs are
  local fixtures, not a production source of truth.
- Application `User`, membership, role, permission and invitation state: Phase 03
  owns it in LearnStack; the provider export supplies authentication only.
- BFF cookie/session custody, CSRF, callback and logout implementation: P02b-7.
  Full browser OIDC journeys belong to Phase 06.
