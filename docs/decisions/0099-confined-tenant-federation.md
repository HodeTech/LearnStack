# ADR-0099: Confined Tenant Federation

## Status

Proposed — 2026-11-13, revised the same day after two independent reviews. Acceptance,
implementation and qualification are separate records; this proposal changes no runtime
behavior, realm, client or standard.

**Date:** 2026-11-13
**Deciders:** @cemil

This closes Phase 03 gate G12 for P03-8 when Accepted. It applies
[ADR-0004 Amendment 1](0004-authentication-strategy.md)'s realm-level brokering to
the first runtime Keycloak Admin adapter, which reopens the provider-boundary pass
that [P02b-6](../roadmap/phase-02b-delivery-log.md) reserved for that event.
[ADR-0072](0072-application-owned-identity-bootstrap.md)'s rejection of a runtime
Admin API concerned application-identity allocation; this record adds no allocator,
reissue or email merge. Dated Amendments, not body rewrites, record the changes to the
Accepted records listed under Implementation Notes.

The [P03-8 package](../roadmap/phase-03-delivery-log.md#p03-8-proposed-tenant-federation-package-2026-11-13)
owns the three implementation steps and their proof.

## Decision Drivers

- One realm serves every tenant. `manage-identity-providers` is realm-wide, so a
  tenant's authority over "its" broker exists only if LearnStack confines it; the
  provider cannot.
- Keycloak 26.6.2's JSON `import-config` fetches any supplied `fromUrl` through its
  HTTP client with no address check
  ([source](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/IdentityProvidersResource.java)),
  and a broker's token, JWKS, UserInfo, logout, introspection and SAML artifact/SLO
  endpoints are all fetched by Keycloak. Standard 11 A10 requires allow-listed
  outbound calls, while [P02b m-38](../roadmap/phase-02b-delivery-log.md) records
  private IdP networks as legitimate for SaaS, Dedicated and Self-Hosted.
- A tenant-controlled IdP asserts any `email` it likes. A broker that **creates** a
  user for an address nobody has registered yet lets a hostile tenant pre-create the
  future owner's account: the owner's later password reset sets `emailVerified`
  (Keycloak `ResetCredentialEmail`), the IdP link survives, and
  [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md)'s
  `email_verified == true` invitation check then passes for the attacker's session.
- Stock Keycloak adds linking paths a tenant must not reach: the `idp_link`
  application-initiated action is registered enabled and checks only `manage-account`,
  which every realm user holds through the default role, and the account console lists
  and links every broker whose `showInAccountConsole` is its default `ALWAYS`;
  `hideOnLogin` does not affect either.
- MUST audit commits with the state it describes
  ([ADR-0033](0033-audit-durability-model.md)); a provider write cannot join that
  transaction, and an unknown provider outcome must stay visible rather than guessed.
- Federation is additive. No failed, mistaken or malicious configuration may remove
  existing local sign-in, and anonymous pages stay independent of the provider
  ([ADR-0064](0064-tenant-api-authentication-boundary.md)).
- Existing seams are fixed: entitlement keys `identity.sso.saml`/`identity.sso.oidc`,
  registry-release adoption, the closed native administration dispatcher with its
  16 KiB input bound, and the shared new-subject budgets of
  [ADR-0078](0078-native-session-scope-and-self-entry.md).
- The maintainer decided on 2026-11-13: a real interactive test is required before
  activation, in the form this record gives it; an existing account links only through
  that account's own re-authentication; a tenant has one connection; and the broker
  creates no accounts.

## Considered Options

1. **LearnStack-owned connection registry, dedicated Admin adapter, fixed broker
   profile, link-only first-broker flow, tenant-bound brokered sessions and
   intent-first provider operations** (chosen).
2. **Operator-provisioned brokers, read-only tenant UI** (rejected). Confines
   trivially but delivers no usable tenant configuration (D11).
3. **Realm per tenant** (rejected). ADR-0004 Amendment 1 keeps it an enterprise
   opt-in; it multiplies issuers, BFF configuration and provisioning and leaves
   realm-level authority just as broad.
4. **Pass-through of Keycloak's IdP representation and `import-config fromUrl`**
   (rejected). Lets a tenant choose flows, mappers, trust flags and egress targets.
5. **Broker creates accounts for unknown addresses** (rejected). Enables the
   pre-creation takeover above. Admitting it later needs verified-domain control
   enforced inside the provider and its own decision; nothing here provides that.
6. **Verification-mail or email-matching linking, `trustEmail=true`** (rejected). Each
   turns an asserted claim into account takeover or cross-tenant invitation acceptance.
7. **Provider call inside the business transaction** (rejected: holds a connection
   across I/O and leaves an unaudited effect after a failed COMMIT) **or the ADR-0047
   job runtime** (rejected: its capacity/quiescence machinery protects irreversible
   external sends, which an idempotent desired-state call is not) **versus
   intent-first** commit, provider call, outcome commit (chosen).

## Decision

LearnStack stores one `FederationConnection` per tenant and is the sole confiner of
the realm-level broker. A dedicated confidential client `learnstack-federation-admin`,
holding only `realm-management/manage-identity-providers`, backs one Infrastructure
adapter that calls a fixed set of Admin endpoints for aliases LearnStack derived and
can prove it owns. Tenants author only protocol, display name and OIDC endpoints/
client credentials or SAML identifiers/certificates; LearnStack fixes every flag,
flow, mapper and trust setting. Tenant-supplied URLs are fetched during configuration
only by a LearnStack egress guard. The broker creates no accounts: a person with an
existing local account links it by re-authenticating that account, and a brokered
session is admitted only on its own tenant's host. A connection is advertised only
after a real brokered test sign-in on its current configuration. Every provider write
is preceded by a committed MUST audit intent and followed by a committed outcome, so a
crash or unknown result stays visible and retryable. Local sign-in is never affected.

## Context

### Verified baseline (HEAD `0e029dcf`, Keycloak 26.6.2)

- The `learnstack` export declares no `identityProviders`, `authenticationFlows`,
  `firstBrokerLoginFlow` or `requiredActions`, so stock brokering applies, including
  stock create-or-link and the registered `idp_link` action. Registration is open,
  email is the username (`registrationEmailAsUsername`), `verifyEmail` is false,
  duplicate emails are refused, and `learnstack-web` has `fullScopeAllowed=true`.
- No runtime Admin adapter exists. The runtime provider clients are the UserInfo
  recipient reader and the read-only `learnstack-event-reader` of
  [ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md).
- The BFF authorization request is fixed (`openid profile email`, optional
  `kc_action=VERIFY_EMAIL`) with no `kc_idp_hint`, and sign-in offers one local action.
  The closed token profile whitelists no `amr`/`acr`, so no LearnStack operation
  authorizes on assurance.
- `identity.sso.saml`/`identity.sso.oidc` are plan-projected, default-false,
  fail-closed keys in both repositories; `NullEntitlementProvider` grants them.
  Registry release v3 is current; a new Tenant key needs release v4, explicit adoption
  and an eleventh advisory capability.
- Keycloak treats a masked `clientSecret` (`**********`) as "keep the stored value", so
  LearnStack never reads back or retains a secret. A broker's alias is the user-session
  note `identity_provider`, which a stock user-session-note mapper can emit.
- Keycloak refuses an authentication whose user differs from the browser's existing
  SSO session (`DIFFERENT_USER_AUTHENTICATED`), including under `prompt=login`.

### Ownership and the confined adapter

- A connection's alias is `lsf-<connectionId:N>-<generation>`, 32 lowercase hex
  characters plus a decimal generation; LearnStack derives it and never accepts one.
  The provider representation carries a `learnstackOwner` entry naming tenant,
  connection and generation. The adapter refuses any read or write unless provider and
  row agree, so a stale row, foreign alias or manually created broker is never touched.
- An identity-bearing Configure creates the next generation's broker, then deletes the
  previous one. Keycloak removes a deleted broker's federated links, so repointing a
  connection to a different IdP never inherits existing links.
- The adapter admits only: the token endpoint; `POST` and `GET`/`PUT`/`DELETE`
  `identity-provider/instances[/{alias}]`; `GET instances/{alias}/mappers`; and the
  multipart `file` form of `identity-provider/import-config`. No JSON `fromUrl`, listing
  of other aliases, user, flow, realm, client or required-action call is admitted. A
  mapper on an owned alias is drift and refuses activation; the adapter creates none.
- Credentials: a fresh client-credentials token per operation, the secret through
  `ISecretProvider` (`Federation:ClientSecret`), HTTPS mandatory except explicit local
  Development loopback, no redirects, no ambient proxy credentials, and the adapter's
  `IProviderResilience` policy with retry disabled, finite timeouts and a breaker. As
  with `learnstack-event-reader`, the provider role is broader than the admitted
  calls; that is stated, not hidden. No BFF, master, demo or event-reader credential is
  substituted. The port is a new narrow `IFederationProvider` in the SharedKernel
  identity namespace, not ADR-0004's broader `IIdentityProvider`; this record is the
  decision P02b-6 required for a runtime provider boundary. The adapter lives in
  `LearnStack.Infrastructure.Identity.Federation`, beside the existing provider-events
  adapter. Vendor types and exceptions stay inside it and surface as
  `ProviderException`s; direct HTTP introduces no SDK, so
  `Provider_SDK_Types_NotImportedOutsideInfrastructure` stays Awaiting backfill.

### Fixed broker profile

LearnStack builds the provider representation from a closed per-protocol allow-list of
keys and never forwards `import-config` output or any tenant-supplied key. Every write
enforces, and every readback must equal, the exact key set and values below; an extra
or differing key is drift.

| Setting | Value | Why |
|---|---|---|
| `hideOnLogin` | true | The shared login page never lists another tenant's IdP |
| `showInAccountConsole` | `NEVER` | The account console neither lists nor links any tenant IdP |
| `trustEmail` | false | An asserted address is never verified; ADR-0093 stays intact |
| `linkOnly`, `storeToken`, `addReadTokenRoleOnCreate`, `authenticateByDefault`, `doNotStoreUsers`, `acceptsPromptNoneForwardFromClient` | false | No token retention, default redirect or silent forwarding |
| `firstBrokerLoginFlowAlias` | `learnstack-first-broker-login` | The only admitted linking behavior |
| `postBrokerLoginFlowAlias`, mappers, `organizationId` | none | No claim-to-role/group/attribute/organization authority |
| `syncMode` | `IMPORT` | The IdP never overwrites profile data |
| OIDC trust | signature validation on, JWKS URL, PKCE S256, nonce and type checks on, scope `openid email profile`, `jwtAuthorizationGrantEnabled` and `supportsClientAssertions` off, closed client-authentication choice | No tenant-selected trust relaxation or alternate grant |
| OIDC server-side URLs | `logoutUrl`, introspection and back-channel logout unset | Only guarded endpoints are fetched |
| SAML trust | signature validation on, signed assertions required, a fixed non-transient NameID policy, no IdP-initiated SSO | Unsigned, unsolicited or per-login-identity assertions refuse |
| SAML server-side URLs | artifact resolution, SLO, back-channel and `metadataDescriptorUrl` unset or off | Only guarded endpoints are fetched |

`enabled` is true for Draft and Active and false for Disabled. Exact keys live in the
Identity provider specification at acceptance. A client secret is refused when empty,
over 1,024 bytes, equal to the mask literal, or beginning `${`, which Keycloak would
resolve as a vault reference.

### Realm invariants

Realm reconciliation, not the adapter, owns and the Step 2 tests assert:

- The realm-owned `learnstack-first-broker-login` flow and the `learnstack-federation-admin`
  client (`manage-identity-providers` only).
- The `idp_link` required action is disabled, so no application-initiated action can
  link a broker.
- An ID-token-only user-session-note mapper on `learnstack-web` emitting the `idp` claim.
- Admin-event details stay disabled, because a Keycloak PUT logs the unstripped
  representation, which would record a rotated secret.

The adapter cannot read these; the production provisioning runbook (Phase 11) must
replicate them and cannot be assumed from this record.

### Bounded egress

LearnStack's `IFederationEgressFetcher` retrieves OIDC discovery, JWKS or SAML
metadata. Keycloak is never handed a URL to fetch during configuration.

- HTTPS only; port 443 unless allow-listed; no userinfo, fragment or IP-literal host;
  at most 2,048 characters.
- Every resolved address must be globally routable; loopback, link-local (including
  cloud metadata), private, CGNAT, documentation, multicast, reserved and mapped/ULA
  IPv6 ranges refuse. The connection is made to the validated address with SNI and
  certificate verification for the original host, so rebinding between check and
  connect cannot redirect it.
- No redirects, cookies, credentials or proxy; four-second timeout; bodies of at most
  64 KiB (discovery) or 256 KiB (JWKS, metadata); content type checked; XML read with
  DTD processing prohibited and no resolver.
- An operator allow-list `Federation:Egress:AllowedHosts` (exact `host` or `host:port`,
  no wildcard, empty by default) admits named private or non-443 endpoints. It is
  composition configuration, never tenant input, and modules never read `DeploymentMode`.
  This decides the deployment policy that m-38 left open.
- The guard runs at Configure, Enable, Activate and test start, independently on every
  authorization, token, JWKS and UserInfo endpoint and on every SAML SSO endpoint, because
  real IdPs span hosts. The adapter writes explicit endpoints, not a discovery URL.
- Keycloak itself still contacts the validated token, JWKS and UserInfo endpoints at
  sign-in, and the mandatory test sends the client secret to the chosen token endpoint.
  A later DNS change is a residual risk LearnStack cannot close from outside the
  provider. Network egress policy for the Keycloak process is a required deployment
  control in the realm runbook; Phase 11 owns its production qualification, and this
  record does not claim it.

### Linking, email trust and MFA

- **The broker creates no accounts.** `learnstack-first-broker-login` is: detect the
  existing user (required; an unknown address ends in a closed error), confirm the link,
  then re-authenticate that account with its password and its conditional OTP. No
  create-user, email-verification or auto-link step exists. A hostile IdP asserting an
  address creates nothing and links only if the account's own credentials are entered.
  Residual risks: an account owner deliberately entering their password after
  following a hostile IdP's link, and Keycloak's "forgot password" on that form, which
  can complete a pending link. Step 2 qualifies that the latter resumes only in the browser
  holding the pending authentication session, so an attacker-initiated reset mailed to
  the account owner links nothing; if it does link, the form's reset action is removed.
- SSO is therefore a sign-in convenience for an existing, email-verified local account,
  not an onboarding path. A new person first registers locally and verifies the address
  (the existing `kc_action=VERIFY_EMAIL` purpose), then signs in with SSO once to link.
  Brokered sign-in also never reaches the shared 10/minute, 100/day and 10,000-lifetime
  new-subject budgets of ADR-0078, because it creates no account.
- A tenant-trusted-email or per-domain setting is not provided, for the cross-tenant
  reason above. Federation grants no Membership, Role or group and there is no
  just-in-time provisioning, SCIM or claim mapping; a federated person enters a tenant
  through the existing invitation journey.
- **A brokered session belongs to its tenant's host.** The ID token carries the `idp`
  claim only for brokered sessions. At every credential publication, code redemption
  and refresh, the BFF refuses a token whose `idp` is present and not exactly the alias
  of the host tenant's Active connection, or when that alias cannot be read, for brokered
  sessions only. Context discovery and switching for a brokered session stay within the
  host's tenant. A session via tenant A's IdP therefore never enters tenant B, a hostile
  `kc_idp_hint` yields nothing, and Disable ends brokered sessions at their next refresh,
  within the access-token lifetime. Already issued API tokens keep the documented bound.
- A brokered sign-in carries no LearnStack MFA assurance. No mapper imports `acr` or
  `amr`, linking requires the account's own OTP, and the tenant IdP's MFA is neither
  verified nor attested. Architecture 13's statement that LearnStack reads `amr` is
  corrected. Any future MFA-gated operation takes assurance only from LearnStack-controlled
  Keycloak authentication; production MFA enforcement remains Phase 11.

### Lifecycle, test and activation

| State | Provider | Meaning |
|---|---|---|
| Draft | enabled, hidden | Configured; not advertised; tested at most for this configuration |
| Active | enabled, hidden | Advertised on the tenant sign-in page; requires a passed test |
| Disabled | disabled | Rolled back; not advertised; configuration retained |

- **Configure** replaces identity-bearing fields (endpoints or certificates, client ID,
  protocol-specific values). It starts a new generation, clears the test and returns the
  connection to Draft, except that a Disabled connection stays Disabled until **Enable**.
  **Rotate secret** keeps the alias and links but also clears the test and returns an
  Active connection to Draft, preserving "Active means this configuration, secret
  included, passed a test". A display-name edit keeps the state. A tenant changes
  protocol by deleting and recreating its single connection.
- **Test** means signing in through the IdP as yourself. The admin starts a single-use,
  ten-minute attempt bound to actor, revision and a random identifier. The BFF begins a
  `federation_test` purpose on `learnstack-web` with `kc_idp_hint=<alias>` and
  `prompt=login`, validates the ID token like any sign-in and requires the `idp`
  claim to equal the alias and the subject to equal the admin's own session subject.
  Keycloak refuses a brokered identity that resolves to anyone else, so the IdP
  account's email must match the admin's local account; a first test therefore also
  links that account, a persistent and stated side effect. The BFF publishes nothing,
  discards every token, reports a closed result under the admin's own session, and keeps
  that session. The result attests an admin-run check, not a security control.
- **Activate** is a registry-only transition: a passed test on the current
  configuration no older than 24 hours, a fresh readback equal to the fixed profile with
  no mapper, a fresh egress check, no pending operation, and the protocol's entitlement.
  **Disable** is the rollback: it disables the broker, clears the test and removes
  advertisement. **Enable** returns to Draft and needs a new test.
- Entitlement lapse or a Disabled state removes advertisement and refuses Configure,
  Rotate, Enable, Activate and test; Disable and Delete always remain. The broker may
  stay enabled but is useless, because no host accepts its sessions.
- Last-login recovery is structural: local credentials and password reset are
  untouched, no SSO-only enforcement exists, and Disable or Delete restores the
  pre-federation state from Studio. SSO-only enforcement would need a Keycloak
  extension and its own decision; no phase owns it and this record does not provide it.

### Intent-first provider operations

Configure, Rotate secret, Enable, Disable and Delete each run as two committed requests
around one idempotent provider call, sequenced by one Identity application service that
sends two top-level commands:

1. **Begin** validates, authorizes and records a pending operation (kind, identifier,
   actor, start time, desired digest) with its MUST audit intent in one transaction.
2. The provider call applies the desired state under its own timeout with no database
   transaction open. A secret exists only in memory for this call and is never
   persisted, logged, audited, idempotency-fingerprinted or returned.
3. **Complete** records `applied`, `failed` with a closed reason, or `uncertain`, with
   its own MUST audit row. It is an application-internal continuation, not an HTTP
   route, bound to the pending operation's identifier and actor: it settles that
   operation even if the actor's permission was revoked since Begin, and grants nothing
   else. A superseded identifier's late Complete changes nothing. It runs on a token not
   tied to the client request, so an abort after the provider call still settles.

Budgets inside the 10-second native deadline: egress four seconds (fetches in parallel),
provider call three seconds, each database step one second, Complete two seconds. A
second mutation while an operation is pending is refused as a conflict until the first
is older than 60 seconds; the new Begin then supersedes it, which is safe because each
call applies a full desired state. A crash or lost response leaves the operation
pending and visible; **Resume** re-reads the provider and completes Enable, Disable and
Delete, and completes Configure only when the non-secret readback equals the stored
desired digest. An interrupted Rotate, or any Configure whose readback differs, asks
the admin to resubmit. Delete removes the row only after the provider confirms absence.
A provider call that lands after its operation was superseded can leave an orphan broker
whose owner marker names a missing connection; it is foreign to every tenant, is never
adopted or touched by the adapter, and its removal is the operator sweep that P03-9 owns.

Mutations carry `Idempotency-Key` with the durable-receipt contract of
[ADR-0079](0079-bounded-identity-administration.md) and `If-Match` on the connection
revision; a replay returns the original outcome and never repeats the provider call.

### Sign-in exposure

An Active, entitled connection is exposed to the tenant host by one anonymous,
host-bound, read-only, `no-store` operation, `GET`/`HEAD /api/v1/public/sign-in`,
returning an object with a nullable `federation` member holding `alias` and
`displayName`. It reads LearnStack's database only. The BFF sign-in page adds a second,
clearly labeled "Sign in with {displayName}" action beside the unchanged local one; a
missing, slow or failed read hides it and nothing else changes. The display name is
1 to 80 characters, normalized, free of control, format and bidirectional characters, and
rendered escaped and isolated. The alias is the provider hint, not a secret. The BFF
never accepts an alias or URL from the browser. The Studio page shows the redirect/ACS
URL, SP entity ID and Keycloak's own descriptor URL derived from the alias and issuer
base; no further adapter call is needed.

### Authority, audit and storage

- One Tenant-scope key, `identity.federation.admin`, in registry release v4, adopted
  explicitly by existing Tenant Admin roles (seed admins adopt v1→v4 in order), with an
  eleventh advisory capability. Org Admin and every other role are unaffected; writes
  need the orgless tenant-wide native context. Keycloak realm roles grant nothing.
- Eleven operations under `identity.federation.*`: ten MUST (`configure`,
  `rotate_secret`, `rename`, `enable`, `disable`, `delete`, `resume`, `activate`,
  `test_record` and `apply`, the Complete outcome) and SHOULD `test_start`. This also covers Standard 18's
  baseline "provider credential created / rotated / revoked". Configuration reads are
  explicit Off. No row carries a secret, assertion or token; snapshots hold the non-secret
  fields and digests. ADR-0097's retained-event catalogue is unchanged: mirroring
  brokered-login or link events would need its own amendment.
- `federation_connections` is Identity-owned, tenant-owned and tenant-wide, unique per
  tenant, with the [canonical template](../standards/05-database.md)'s RLS and EF filter,
  `learnstack_app` grants only and a forward-only migration. It holds protocol,
  generation, display name, state, revision, source kind and URL, client ID, configuration
  digest, the pending operation, the test attempt and result, audit columns and row
  version, with CHECKs for states and exclusivity. It stores no secret, token or
  assertion. Actor references are classified as pseudonymous identifiers; every scalar
  has an explicit personal-data classification.

### User experience

Studio gains one Federation page with a three-step layout (configure, test, activate),
the values to paste into the IdP, a write-only secret control, pending, drift and uncertain
banners with Resume, plain explanations that SSO links an existing account and that the
test signs you in as yourself, and confirmed Disable and Delete. Configuration offers
a discovery or metadata URL, or manual endpoints/identifiers and at most two PEM
certificates, all within the 16 KiB native input bound; there is no metadata paste.
English and Turkish copy, keyboard and focus behavior, reflow and contrast follow the
existing C20 controls, and no identifier, provider message or secret enters product copy.

## Consequences

### Positive

- A tenant can self-serve SAML/OIDC without gaining realm authority, egress, account
  creation or linking beyond an owner's own credentials.
- A crash, timeout or provider outage leaves an auditable, retryable state, and local
  sign-in is untouched in every failure.
- A brokered session cannot cross tenants, repointing never inherits links, and
  Disable ends brokered sessions within the access-token lifetime.

### Negative

- A realm-wide provider role backs the adapter; confinement is LearnStack's alone, and
  realm invariants (`idp_link`, the flow, admin-event details) depend on reconciliation
  that production provisioning must replicate.
- Keycloak's later fetches follow DNS; only deployment network policy can bound them.
- SSO is not an onboarding path, and an administrator whose IdP email differs from
  their LearnStack email cannot pass the required test.
- Linking asks the owner for their password on a provider page; a successful test
  leaves a persistent link.
- The test result is admin-reported; a late provider call can orphan a foreign broker
  until P03-9's sweep.

### Neutral

- No Hub crossing, no new cross-module mechanism and no new Keycloak SDK.
- Production IdP provisioning, network egress policy and MFA enforcement stay Phase 11.
- Tenant offboarding removes the connection and broker inside P03-10's tenant-erasure
  inventory; until then Disable/Delete and P03-9's sweep apply.

## Implementation Notes

1. **At acceptance:** record G12; append dated Amendments to ADR-0004, 0021, 0052, 0064,
   0068, 0070, 0072, 0078, 0079 and 0097, and analyse ADR-0098's refresh and
   back-channel interplay in the ADR-0070 Amendment; reconcile Architecture 13,
   Standards 01/04/05/07/11/18/19/20/21, the Identity README/permissions/audit/providers
   specifications, the Keycloak README, Phase 03/11 documents, glossary and skills.
2. **Step 1 — contract and storage.** Aggregate, migration/RLS/census, registry v4 and
   capability, policy source and audit catalogue, entitlement gate, the Begin/Complete
   runner and commands/queries over a fake provider port, HTTP/OpenAPI census, and
   `learnstack_app` database, unit and architecture proof.
3. **Step 2 — provider and realm.** Egress fetcher, Keycloak adapter, fresh/warm realm
   reconciliation (admin client, link-only flow, `idp_link`, `idp` mapper, admin-event
   details), and real two-tenant OIDC and SAML brokers with linking, confinement, egress,
   secret, attribute-injection, account-console, reset-link and orphan negatives.
4. **Step 3 — native and Studio.** Public sign-in read, BFF hint, `federation_test`
   purpose and tenant-bound session checks at redemption and refresh, Studio page,
   SDK/native operations, i18n, accessibility and real end-to-end proof; full
   qualification and PR.

## Architecture Tests

- Existing Identity, permission, audit, RLS, endpoint-census, public-surface, SDK/native
  and BFF obligations extend to the new subjects with dirty controls; none is relaxed.
  Canonical names for the new obligations are assigned in Standard 21 at acceptance.
- New obligations: the adapter admits only the enumerated calls and aliases; no other
  code holds a federation client; the egress fetcher is the only tenant-URL fetcher; no
  `fromUrl`; the fixed-profile key-set equality; no secret in any audit, log, DTO,
  idempotency fingerprint or persisted column.
- Real-provider controls: two tenant brokers coexist and neither reads or changes the
  other's; an unknown address creates no account; a foreign address links only with the
  owner's credentials; a hidden broker is absent from the account console and `idp_link`
  is refused; a session via tenant A's broker is refused on tenant B's host and after
  Disable; brokered tokens keep the closed profile; the private, metadata and loopback
  endpoints refuse; a failed activation keeps local sign-in.

## References

- [Phase 03](../roadmap/phase-03-identity-admin.md) G12/D11/C13,
  [Authentication](../architecture/13-identity-and-auth.md#sso-and-federation),
  [Security 11](../standards/11-security.md), [Audit 18](../standards/18-audit-coverage.md),
  [Permissions 19](../standards/19-permissions.md), [Infrastructure Stack 20](../standards/20-infrastructure-stack.md).
- [ADR-0004](0004-authentication-strategy.md), [ADR-0033](0033-audit-durability-model.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md), [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md), [ADR-0078](0078-native-session-scope-and-self-entry.md),
  [ADR-0079](0079-bounded-identity-administration.md), [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md),
  [ADR-0095](0095-revision-pinned-membership-custom-fields.md), [ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md),
  [ADR-0098](0098-session-targeted-back-channel-logout.md).
- Keycloak 26.6.2:
  [IdentityProvidersResource](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/IdentityProvidersResource.java),
  [IdentityProviderResource](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/IdentityProviderResource.java),
  [IdpDetectExistingBrokerUserAuthenticator](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/authentication/authenticators/broker/IdpDetectExistingBrokerUserAuthenticator.java),
  [IdpLinkAction](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/authentication/requiredactions/IdpLinkAction.java),
  [LinkedAccountsResource](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/account/LinkedAccountsResource.java).
