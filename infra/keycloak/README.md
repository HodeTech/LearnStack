# Keycloak — Two-Realm Identity (Dev)

The pinned development provider is Keycloak 26.6.2. Two realms remain isolated
under [ADR-0004](../../docs/decisions/0004-authentication-strategy.md) and
[ADR-0019](../../docs/decisions/0019-learnstack-hub.md).
[ADR-0067](../../docs/decisions/0067-development-tenant-access-token-issuance.md)
decides development issuance;
[Security 11](../../docs/standards/11-security.md#authentication) owns its ongoing
rule. The
[P02b-6 accepted package](../../docs/roadmap/phase-02b-events-auth.md#p02b-6-decision-package-2026-10-20)
retains the earlier approval and owns safe warm-realm convergence.
Authentication grants no application membership or permission; Phase 03 owns
those records.

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
- The four personas declare only the built-in `account/manage-account`
  self-service capability in addition to their existing inert realm roles.
  This permits owner credential/linked-account management; it grants no
  LearnStack permission or membership. The
  [approved adjustment](../../docs/roadmap/phase-02b-events-auth.md#account-console-self-service-capability--proposal-2026-10-21)
  owns this explicit exception to role-assignment preservation.
- The operator account remains `demo-operator@learnstack.test`; its separate
  realm is untouched. Neither realm's token is accepted by the other surface.

## Local access and private sources

- Admin console: <http://localhost:8080/admin/>; local master defaults are
  `admin` / `admin-dev-secret`.
- Tenant discovery:
  <http://localhost:8080/realms/learnstack/.well-known/openid-configuration>.
- Operator discovery:
  <http://localhost:8080/realms/learnstack-hub/.well-known/openid-configuration>.
- `.env.example` declares a public development example for
  `KEYCLOAK_WEB_CLIENT_SECRET`; newly scaffolded private files inherit that example.
  File permissions do not make a published value confidential. Compose passes it
  only to Keycloak; the import resolves its environment placeholder. The development
  reconciler and the later P02b-7 BFF are its other consumers. The API, public
  renderer and .NET Seeder never receive this secret.
- Older private `.env` files must add that key explicitly, matching an already
  imported confidential client's secret. Convergence verifies that existing
  secret; a mismatch requires operator correction rather than automatic rotation.
  Conversion of the old public client installs
  the selected private source once.
- Before `make dev`, `make seed` or `make demo`, prepare the selected private
  file and preserve its secret across restarts. A missing key refuses before
  container creation or reconciliation; neither supplies an automatic secret
  fallback. See
  [secret continuity and recovery](#secret-continuity-and-recovery).

Compose and the reconciler read the selected file through one no-follow handle
and verify that it is a regular file owned by the current user, has one hard link
and has no group/other permission bits. A new Makefile-created `.env` is private;
existing files are never repaired silently. This prerequisite also applies to
lifecycle commands: an older `0644` file is refused before Docker. After verifying
an older path is your own regular, nonsymlink, single-link file, explicitly run
`chmod 600 .env` (or the selected private path). Do not print its contents or copy
it to a report.
Compose, seed and reconciliation report these file-property refusals as
`preflight/private_file`, with the same fixed ownership/permission instruction.

Recognized `down`, `logs`, `ps`, `stop` and `config --services` invocations can
parse an older private file without the web key by using a process-only nonsecret
placeholder. This does not write the private file or install a provider secret.
Container-creating commands, full configuration output and unknown argument forms
retain the required-secret check. The private-file prerequisite still applies.

These credentials are development-only. Production provisioning and rotation
belong to Phase 11 and [Infrastructure Standards](../../docs/standards/12-infrastructure.md#secrets-management).
Do not reuse local secrets in production or put them in command arguments,
URLs, logs or public configuration.

## Secret continuity and recovery

`KEYCLOAK_WEB_CLIENT_SECRET` is create-once local state, not a new value on every
seed. Before the first Keycloak import, set it in the selected private file.
Use the same value for Compose, reconciliation and the later BFF. The API,
public renderer and application Seeder never receive it.

- `missing_web_secret`: add the key explicitly before starting the stack, matching
  the existing imported client when present. A previously populated file is not
  supplemented with a fallback automatically.
- `secret_conflict`: stop the affected local consumers and inspect the selected
  confidential `learnstack-web` client privately in the Admin Console. Recover
  its existing secret into the selected private source if that source was lost
  or changed accidentally. Reconciliation will not overwrite the provider secret.
- If an import used the committed public development example, treat it as
  exposed. The operator must explicitly rotate that local client secret in the
  Admin Console and update the selected private source together; the reconciler
  does not perform this rotation. Restart only the owned affected consumers,
  then explicitly rerun convergence.

Do not reset users, delete the client, drop provider data or enable a public
client to bypass the refusal. Never put the recovered/rotated secret on argv,
in logs, in a URL or in committed configuration.

## Lock recovery

One local owner protects each selected container/realm. The default lock is
under `os.tmpdir()/learnstack-realm-<uid>/`, named
`sha256(<selected-container-id> + "/realms/learnstack").lock`. This is separate
from the demo lifecycle's project lock. The parent is current-user-owned `0700`;
the exclusive regular lock file is `0600`. No age or guessed PID proves a lock
is stale, and the tool never reclaims one automatically.

On `lock_busy`, first identify the exact selected project, container and realm,
and verify that every previous seed/demo/reconciler owner for that target has
finished and joined its children. Do not stop foreign processes. If cleanup is
uncertain, keep the lock and investigate; a timeout is not cleanup evidence.
Only after establishing that no owner remains may the local operator remove
that exact lock, after checking its directory, ownership, file type, permissions
and single-link status. Never remove the whole directory, glob locks or delete
a lock based only on elapsed time. Explicitly rerun convergence afterwards;
partial Admin REST writes remain durable and are re-read.

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
The web client's retained roles scope can put the `account` audience and eligible
account roles into its access token. That token can authorize Keycloak owner
self-service, including ordinary profile updates; it is not restricted to the
LearnStack API. The configured issuance lifetime limits token duration, not its
capability. P02b-7 owns server-side BFF token custody; narrowing this already
approved development capability requires a separate decision.
The tenant export enables open registration with email as username, password
reset and remember-me, while email verification is disabled. It declares no
password policy or event-persistence/listener configuration. These are local
development settings; production provisioning owns its own hardened configuration.
Production client scope mapping, refresh rotation and MFA enforcement remain
Phase 11 work; Phase 03 owns tenant identity policy. Tenant MFA stays optional in
this development export; the operator realm keeps its declared TOTP action.

The ordinary Account REST profile update does not establish an email/login rename:
Keycloak's [email condition](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/userprofile/DeclarativeUserProfileProviderFactory.java#L156-L187)
makes email read-only in that context under the current realm settings. A 204 can
therefore leave email unchanged. Separate required-action or administrator
changes use different paths. If a reserved login is changed,
reconciliation refuses ownership instead of adopting another account or resetting
credentials. Verify the existing provider ID and protected actor privately before
restoring an intended reserved login; never repair a conflict by changing actor IDs.

## Fresh import and warm upgrade

Keycloak starts with `start-dev --import-realm`, reading both committed exports
only when their realms do not already exist. Restarting a populated database
does not apply export changes. Dropping the database or replaying a whole import
is not an upgrade procedure.

P02b-6 Step 1 supplies the executable fresh import; Step 2 supplies the bounded
shared Admin REST reconciler. Both completed their local review rounds. Step 3
connects `make seed` and `make demo` to the same awaited operation after
application seed convergence. Its actual lifecycle qualification and reviews
are recorded in the [Step 3 delivery record](../../docs/roadmap/phase-02b-events-auth.md#p02b-6-step-3--shared-seeddemo-lifecycle-2026-10-21).
The exact pre-P02b-6 export still has unsupported warm prerequisites: its stock
profile hides identity values and its account-console bootstrap carries broader
scope assignments. Identity-visibility guards still refuse the unprepared
historical state before writes; complete automatic upgrade remains pending.
[ADR-0068](../../docs/decisions/0068-development-warm-realm-qualification.md)
is Accepted — 2026-10-22 after verified review corrections under conditional
maintainer authority. Its three-step implementation and automatic-path proof
are tracked in the [acceptance record](../../docs/roadmap/phase-02b-events-auth.md#adr-0068-acceptance-and-implementation-2026-10-22).
The [first correction step](../../docs/roadmap/phase-02b-events-auth.md#adr-0068-step-1--qualified-stock-assignment-implementation-2026-10-22)
adds fixed historical graph qualification and assignment-only normalization,
with both independent review rounds complete. Automatic preparation and full
upgrade qualification remain pending.
Current identity-visibility guards remain active. Do not remove them, wipe the
database or silently normalize customized scopes by name.

Direct .NET Seeder remains an application-data tool; its identity manifest is a
read-only declaration. Both combined entries build its fresh Release DLL and
keep private realm credentials out of that child.

An older private environment must add `KEYCLOAK_WEB_CLIENT_SECRET` explicitly
before either combined entry starts stack or database work. Do not wipe the
provider database to upgrade. The tool reports fixed refusal reasons, keeps
partial Admin changes durable and requires an explicit verified rerun. The
[accepted account-console adjustment](../../docs/roadmap/phase-02b-events-auth.md#account-console-self-service-capability--proposal-2026-10-21)
is implemented; its actual stock-parity, warm-preservation, profile/API
qualification and both completed independent review rounds are recorded with
Step 4. Qualification applies only to the recorded source and scope; subsequent
corrections require their own validation and independent reviews. Merge remains
a separate maintainer action.

Fresh import declares both stock `account` and `account-console` definitions,
including the stock role/composite and scope-mapping baseline. Warm reconciliation
does not replace these clients, role definitions or composites. It qualifies
their existing bounded capability and scope allowance before any mutation,
adds only a missing named capability for a managed persona and preserves every
prior mapping. An inherited effective capability needs no redundant grant.

The console receives one fixed-account role emitter in access/introspection
tokens, retaining its stock S256, audience resolver and restricted full scope.
It emits no realm/all-client roles or ID/user-info/lightweight-token roles.
Incompatible role, scope or mapper customization refuses before writes.
Compatible existing emitters retain their identities. Because the console is
shared, its mapper may also restore self-service for nonmanifest users already
holding eligible account roles; their account data and grants stay unchanged.

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
profiles refuse before any write. The current implementation does not change
visibility to discover
whether a conflicting identity exists. ADR-0068's accepted preparation exception
remains pending implementation and proof; it permits metadata only after all
identity-independent preconditions pass. For an older realm that hides these
values, an operator must first inspect the profile configuration, make the three
identity definitions admin-only readable, and inspect their stored values before
retrying. This is an explicit administrator action outside automatic tooling;
inspect the full profile and preserve every unrelated field. It is not permission
to infer ownership from a name or silently remove a custom scope. Do not enable
end-user editing or replace a conflicting UUID to force
convergence. The existing unmanaged policy remains unchanged by reconciliation.

Protected claims must come from user attributes. Aggregation and inherited
group identity values are incompatible with that contract and refuse before
writes. Safe ordinary groups and memberships are preserved. The bounded group
inventory includes nested groups. Unpaginated membership reads follow the
mapper's group stream; any exposed membership outside that ordinary inventory
refuses before writes. The pinned cached model excludes Keycloak Organization
groups from that stream, including retained memberships after the feature is
disabled. Its actual-token control is separate from the defensive refusal for
unknown membership IDs. This is separate from LearnStack's own organization
IDs. The named single-user import does not inherit realm default groups;
new-account membership is independently checked as empty.

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
