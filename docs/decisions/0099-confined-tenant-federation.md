# ADR-0099: Confined Tenant Federation

## Status

Proposed — 2026-11-13, revised after three review rounds. Acceptance, implementation
and qualification are separate records. This proposal changes no runtime behavior,
realm, client, Keycloak pin, Standard or Accepted ADR.

**Date:** 2026-11-13
**Deciders:** @cemil

This closes Phase 03 gate G12 for P03-8 when Accepted. It applies
[ADR-0004 Amendment 1](0004-authentication-strategy.md)'s realm-level brokering to the
first runtime Keycloak adapter that writes to the Admin REST API, which reopens the
provider-boundary pass that [P02b-6](../roadmap/phase-02b-delivery-log.md) reserved for
that event. [ADR-0072](0072-application-owned-identity-bootstrap.md)'s rejection of a
runtime Admin API concerned application-identity allocation; this record adds no
allocator, reissue or email merge. Dated Amendments, not body rewrites, record the
changes to the Accepted records listed under Implementation Notes.

The [P03-8 package](../roadmap/phase-03-delivery-log.md#p03-8-proposed-tenant-federation-package-2026-11-13)
owns the four implementation steps and their proof.

## Decision Drivers

- One realm serves every tenant. `manage-identity-providers` is realm-wide, so a
  tenant's authority over "its" broker exists only if LearnStack confines it; the
  provider cannot.
- **The pinned provider is affected by advisories that touch this surface.** Keycloak
  26.6.2 is below the 26.6.5 fix line for CVE-2026-16443 (SAML metadata import disables
  response signature validation), CVE-2026-16442 (SAML IdP-initiated login bypasses the
  link-only restriction), CVE-2026-11800 (JWT algorithm confusion, fixed in 26.6.4),
  CVE-2026-15573 (authorization bypass through unnormalized URI matching), CVE-2026-15572
  and CVE-2026-16102 (dynamic client registration), CVE-2026-16100 (metric cardinality) and
  CVE-2026-16071 (LDAP user search). This record's contract is written for 26.6.5 or later;
  the pin decision is open ([Implementation Notes](#implementation-notes)).
- Keycloak 26.6.2's JSON `import-config` fetches any supplied `fromUrl` through its HTTP
  client with no address check
  ([source](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/IdentityProvidersResource.java)).
  Keycloak also fetches a broker's token, JWKS, UserInfo, logout, introspection and SAML
  artifact/SLO endpoints. Standard 11's OWASP A10 (SSRF) row requires allow-listed outbound
  calls, while [P02b m-38](../roadmap/phase-02b-delivery-log.md) records private IdP
  networks as legitimate for SaaS, Dedicated and Self-Hosted.
- A broker that **creates** a user for an address nobody has registered yet lets a hostile
  tenant pre-create the future owner's account. The owner's later password reset sets
  `emailVerified` (Keycloak `ResetCredentialEmail`), the IdP link survives, and
  [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md)'s `email_verified == true`
  check then passes for the attacker's session.
- Stock Keycloak adds linking paths a tenant must not reach. The `idp_link`
  application-initiated action is registered enabled and accepts either `manage-account`
  or `manage-account-links`, which realm users hold through the default role. The
  deprecated linked-accounts builder needs `manage-account` and ignores
  `showInAccountConsole`. The account console lists every broker whose
  `showInAccountConsole` is its default `ALWAYS`. `hideOnLogin` affects only the login page.
- `kc_idp_hint` starts any **enabled** broker, hidden or not. A provider-enabled broker
  is therefore reachable from any host that knows its alias, whatever LearnStack's state
  says.
- Linking gives a tenant IdP's controller a Keycloak session for the linked account. Stock
  Keycloak then lets a fresh session run credential and OTP required actions
  (`kc_action` needs re-authentication only after 300 seconds). No LearnStack-side check
  confines those actions, and Keycloak cannot deny them per session without breaking
  password reset and TOTP enrollment.
- Keycloak 26.6.2's identity-provider update takes a whole representation with no version
  or ETag check. A provider write therefore cannot be fenced, and a delayed request can land
  after its outcome was recorded.
- MUST audit commits with the state it describes
  ([ADR-0033](0033-audit-durability-model.md)). A provider write cannot join that
  transaction, and an unknown provider outcome must stay visible rather than guessed.
- Federation is additive. No failed, mistaken or malicious configuration may remove
  existing local sign-in, and anonymous pages stay independent of the provider
  ([ADR-0064](0064-tenant-api-authentication-boundary.md)).
- Existing seams are fixed: entitlement keys `identity.sso.saml` and `identity.sso.oidc`,
  registry-release adoption, the closed native administration dispatcher with its 16 KiB
  input bound, and the shared new-subject budgets of
  [ADR-0078](0078-native-session-scope-and-self-entry.md).
- Maintainer decisions of 2026-11-13: a real interactive test is required before
  activation, in the form this record gives it; an existing account links only through
  that account's own re-authentication; a tenant has one connection; the broker creates no
  accounts; and linking is accepted as delegating the linked account to the IdP's
  controller, documented rather than enforced inside Keycloak.

## Considered Options

1. **LearnStack-owned connection registry, dedicated Admin adapter, fixed broker profile,
   link-only first-broker flow, provider enablement only while needed, host-bound
   brokered sessions and serialized, readback-verified provider writes** (chosen).
2. **Operator-provisioned brokers, read-only tenant UI** (rejected). Confines trivially but
   delivers no usable tenant configuration (D11).
3. **Realm per tenant** (rejected). ADR-0004 Amendment 1 keeps it an enterprise opt-in; it
   multiplies issuers, BFF configuration and provisioning and leaves realm-level authority
   just as broad.
4. **Pass-through of Keycloak's IdP representation and `import-config fromUrl`** (rejected).
   Lets a tenant choose flows, mappers, trust flags and egress targets.
5. **Broker creates accounts for unknown addresses** (rejected). Enables the pre-creation
   takeover above. Admitting it later needs verified-domain control enforced inside the
   provider and its own decision; nothing here provides that.
6. **Verification-mail or email-matching linking, `trustEmail=true`** (rejected). Each turns
   an asserted claim into account takeover or cross-tenant invitation acceptance.
7. **A Keycloak provider extension denying credential, profile and linking actions on
   brokered sessions** (not provided here). It needs a new Java build, image and release
   line for every deployment mode and its own decision. It would close the delegation
   residual; it is not part of this contract.
8. **Provider-side fencing by alias generation, one new broker per write** (rejected).
   Every credential or configuration change would change the callback path that the tenant
   registered at its IdP, and links would be lost on every rotation. Keycloak offers no
   conditional update to fence with (see Context).
9. **Provider call inside the business transaction** (rejected: holds a connection across
   I/O and leaves an unaudited effect after a failed COMMIT), **or the ADR-0047 job
   runtime** (rejected: its capacity and quiescence machinery protects irreversible
   external sends, which an idempotent full-state call is not). **Intent-first** commit,
   provider call, outcome commit is chosen.

## Decision

LearnStack stores one `FederationConnection` per tenant and is the sole confiner of the
realm-level broker. A dedicated confidential client `learnstack-federation-admin`, holding
only `realm-management/manage-identity-providers`, backs one Infrastructure adapter that
calls a fixed set of Admin endpoints for an alias LearnStack derived and can prove it owns.
The alias is fixed for the connection's life and is the broker's callback path. Tenants
author only protocol, display name and OIDC endpoints and client credentials, or SAML
identifiers and certificates. Changing the issuer or SAML entity identifier is not an edit:
it requires a new connection.

The broker is enabled at the provider only while its connection is in a test window, has
passed its test, or is Active. Every provider write is a full desired state, serialized per
connection, verified by readback and preceded and followed by committed MUST audit rows. A
crash, timeout or unknown result stays visible and retryable. The broker creates no
accounts: a person with an existing local account links it by re-authenticating that
account, which delegates that account to the IdP's controller. A brokered LearnStack session
is admitted only on its own tenant's host, only while that tenant's connection is Active and
entitled, and only with matching provenance. A connection is advertised only after a real
brokered test sign-in on its current configuration. Local sign-in is never refused by any
LearnStack check.

## Context

### Verified baseline (HEAD `0e029dcf`, Keycloak 26.6.2)

- The `learnstack` export declares no `identityProviders`, `authenticationFlows`,
  `firstBrokerLoginFlow` or `requiredActions`, so stock brokering applies, including stock
  create-or-link and the registered `idp_link` action. Registration is open, email is the
  username (`registrationEmailAsUsername`), `verifyEmail` is false, duplicate emails are
  refused, and `learnstack-web` has `fullScopeAllowed=true`.
- Runtime Keycloak clients are the UserInfo recipient reader ([ADR-0093](0093-verified-invitation-acceptance-and-handoff.md))
  and the read-only `learnstack-event-reader` ([ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md)).
  `KeycloakIdentityEventProvider` performs GET requests only, so no runtime adapter writes
  to the Admin REST API today.
- The BFF authorization request is fixed (`openid profile email`, optional
  `kc_action=VERIFY_EMAIL`) with no `kc_idp_hint`, and sign-in offers one local action. The
  closed token profile whitelists no `amr` or `acr` claim, so no LearnStack operation
  authorizes on assurance.
- `identity.sso.saml` and `identity.sso.oidc` are plan-projected, default-false,
  fail-closed keys in both repositories; `NullEntitlementProvider` grants them. Registry
  release v3 is current. A new Tenant key needs release v4, explicit adoption and an
  eleventh advisory capability.
- Keycloak treats a masked `clientSecret` (`**********`) as "keep the stored value", so
  LearnStack never reads back or retains a secret.
- A brokered login records the alias in the user-session note `identity_provider`
  (`Details.IDENTITY_PROVIDER`). The note stays on the user session, and refresh re-issues
  the ID token from the session's notes. The user session is the shared provider (SSO)
  session, not one application session.
- Keycloak refuses an authentication whose user differs from the browser's existing SSO
  session (`DIFFERENT_USER_AUTHENTICATED`), including under `prompt=login`.
- `storeToken=false` controls only persistence of the federated identity record.
  `storeTokenInSession` defaults to `true`; Keycloak honors a `false` value only under the
  preview `IDENTITY_BROKERING_API_V2` feature, which the compose configuration does not enable
  (`KC_FEATURES=token-exchange,admin-fine-grained-authz`). Brokered tokens therefore remain in
  the user session. LearnStack never reads them, and no fixed-profile setting removes them.
- The deprecated linked-accounts builder (`GET /account/linked-accounts/{alias}`) and the
  linked-account `DELETE` both require `manage-account`. Stock Keycloak 26.6.2 therefore has
  no narrow self-unlink role, and the account console is the only self-service surface for
  linked accounts.
- `IdpUsernamePasswordForm` pre-fills the detected account's username. The field is editable,
  and a normal login clears the context user before validating the submitted username and
  password. The account that gets linked is whichever account the submitted username and
  password validate.
- A reset started from the re-authentication form resumes a pending link only in the browser
  that holds the pending authentication session.

### Advisory baseline of the pin

| Advisory | Severity | Relevance to this surface |
|---|---|---|
| CVE-2026-16443 | high | SAML metadata import can disable response signature validation. Direct: SAML brokers. |
| CVE-2026-16442 | high | SAML IdP-initiated login bypasses the link-only restriction. Direct: first-broker linking. |
| CVE-2026-11800 | high | JWT algorithm confusion, fixed in 26.6.4. Direct for the JWT grant; kept off by the profile. |
| CVE-2026-15573 | high | Authorization bypass through unnormalized URI matching in the path matcher. Realm-wide. |
| CVE-2026-15572, CVE-2026-16102 | high | Dynamic client registration policy and mapper issues. Not enabled here; realm-wide. |
| CVE-2026-16100, CVE-2026-16071 | medium | Metric cardinality; LDAP user search bounds. Realm-wide. |

Every entry is fixed in 26.6.5 (and 26.7.1 for the 26.7 line). CVE-2026-2092 (encrypted SAML
assertions) does not affect 26.6.2. Two IDs proposed in the round-three review,
CVE-2026-9087 and CVE-2026-4874, do not appear in the Keycloak security advisory list and
are excluded.

### Ownership and the confined adapter

- The alias is `lsf-` followed by the connection identifier's 32 lowercase hexadecimal
  characters. LearnStack derives it once and stores it. It is never accepted from input, and
  it does not change for the connection's life. A recreated connection has a new identifier
  and therefore a new alias.
- The provider representation carries `learnstackOwner`, naming tenant and connection. The
  adapter refuses every read or write unless the owner marker and the stored row agree, so a
  stale row, foreign alias or manually created broker is never touched.
- At first successful configuration the connection stores its issuer (the OIDC discovery
  `issuer`) or SAML entity identifier. A configuration that changes either is refused as an
  edit and must create a new connection. The old broker is deleted, its links end, and the
  tenant updates the IdP's callback for the new alias. Existing `(alias, subject)` links
  therefore never point at a different identity source.
- The adapter admits only: the token endpoint; `POST instances` for a new connection;
  `GET`, `PUT` and `DELETE instances/{alias}`; `GET instances/{alias}/mappers`; and the
  multipart `file` form of `identity-provider/import-config`. It admits no JSON `fromUrl`, no
  other alias, user, flow, realm, client or required-action call. A mapper on an owned alias
  is drift and refuses activation. The adapter creates none.
- Credentials: a fresh client-credentials token per operation, the secret through
  `ISecretProvider` (`Federation:ClientSecret`), HTTPS mandatory except explicit local
  Development loopback, no redirects, and no ambient proxy credentials. The provider role is
  broader than the admitted calls, as with `learnstack-event-reader`; this is stated, not
  hidden. No BFF, master, demo or event-reader credential is substituted.
- The port is a narrow `IFederationProvider` in the SharedKernel identity namespace beside
  `IIdentityEventProvider`, not ADR-0004's broader `IIdentityProvider`. This record is the
  decision P02b-6 required for a runtime provider boundary. The adapter lives in
  `LearnStack.Infrastructure.Identity.Federation`. It uses the `Resilience:federation`
  configuration with retry disabled, because its provider writes are not safe to retry
  automatically. It has finite timeouts and a circuit breaker. Vendor types and exceptions
  stay inside the adapter and surface as `ProviderException`s. Direct HTTP introduces no SDK,
  so `Provider_SDK_Types_NotImportedOutsideInfrastructure` stays Awaiting backfill.

### Fixed broker profile

LearnStack builds the provider representation from a closed per-protocol allow-list and never
forwards `import-config` output or any tenant-supplied key. Every write enforces, and every
readback must equal, the exact key set and values below. An extra or differing key is drift.

| Setting | Value | Why |
|---|---|---|
| `hideOnLogin` | true | The shared login page never lists another tenant's IdP |
| `showInAccountConsole` | `NEVER` | The account console neither lists nor links any tenant IdP |
| `trustEmail` | false | An asserted address is never verified; ADR-0093 stays intact |
| `storeToken` | false | No persisted federated identity record. Session storage is not controllable (see Context) |
| `linkOnly`, `authenticateByDefault`, `addReadTokenRoleOnCreate`, `doNotStoreUsers`, `acceptsPromptNoneForwardFromClient`, `passMaxAge` | false | No default redirect, silent forwarding, or IdP control of session freshness |
| `firstBrokerLoginFlowAlias` | `learnstack-first-broker-login` | The only admitted linking behavior |
| `postBrokerLoginFlowAlias`, mappers, `organizationId` | none | No claim-to-role, group, attribute or organization authority |
| `syncMode` | `IMPORT` | The IdP never overwrites profile data |
| OIDC trust | signature validation on, JWKS URL, PKCE S256, nonce and type checks on, scope `openid email profile`, `jwtAuthorizationGrantEnabled` and `supportsClientAssertions` off, closed client-authentication choice | No tenant-selected trust relaxation or alternate grant |
| OIDC server-side URLs | `logoutUrl`, introspection and back-channel logout unset | Only guarded endpoints are fetched |
| SAML trust | signature validation on, signed assertions required, a fixed non-transient NameID policy, no IdP-initiated SSO | Unsigned, unsolicited or per-login-identity assertions refuse |
| SAML server-side URLs | artifact resolution, SLO, back-channel and `metadataDescriptorUrl` unset or off | Only guarded endpoints are fetched |

The Identity provider specification enumerates the exhaustive key list per protocol, including
`forwardParameters`, `loginHint`, `filteredByClaim`, `allowedClockSkew` and `principalType`.
Any key outside it is drift. The provider display name equals the connection's display name,
which is 1 to 80 characters, normalized, and free of control, format and bidirectional
characters. Rename writes it through the same provider path.

A client secret is refused when empty, over 1,024 bytes, equal to the mask literal, or
beginning `${`, which Keycloak interprets as a vault reference.

### Realm invariants

Realm reconciliation owns these, and the Step 3 tests assert them:

- The realm-owned `learnstack-first-broker-login` flow and the `learnstack-federation-admin`
  client (`manage-identity-providers` only).
- The `idp_link` required action is disabled, so no application-initiated action links a
  broker.
- The `account` and `account-console` clients are disabled, and `manage-account` and
  `manage-account-links` are removed from the default role and from the development personas.
  Password change, profile editing and the linked-accounts API then leave the console, and
  credential recovery uses the reset flow. This retires the development account-console
  self-service capability. [Decision Q1](#implementation-notes) decides whether a narrow
  self-unlink path comes back.
- On `learnstack-web`, a user-session-note mapper maps the `identity_provider` note to the
  `idp` ID-token claim. Enablement of any broker requires a readback of this mapper.

The adapter cannot read these invariants. Phase 11's production identity provisioning must
replicate them; this record does not assume that.

### Bounded egress

`IFederationEgressFetcher` retrieves OIDC discovery and JWKS documents or SAML metadata.
Keycloak is never handed a URL to fetch during configuration.

- HTTPS only; port 443 unless allow-listed; no userinfo, fragment or IP-literal host; at most
  2,048 characters.
- Every resolved address must be globally routable. Loopback, link-local (including cloud
  metadata), private, CGNAT, documentation, multicast, reserved and mapped or unique-local
  IPv6 ranges refuse. The connection is made to the validated address with SNI and certificate
  verification for the original host, so rebinding between check and connect cannot redirect
  it.
- No redirects, cookies, credentials or proxy; a four-second timeout; bodies of at most 64 KiB
  (discovery) or 256 KiB (JWKS, metadata); content type checked; XML parsed with DTDs
  prohibited and no resolver.
- An operator allow-list `Federation:Egress:AllowedHosts` (exact `host` or `host:port`, no
  wildcard, empty by default) admits named private or non-443 endpoints. It is composition
  configuration, never tenant input, and modules never read `DeploymentMode`.
- The guard runs at Configure, test start and Activate. It applies to every authorization,
  token, JWKS, UserInfo and SAML SSO endpoint, because real IdPs span hosts. The adapter writes
  explicit endpoints, not a discovery URL.
- **Standard 11 exception (open, Q2).** The OWASP A10 row requires allow-listed outbound calls.
  Tenant-supplied public IdP hosts cannot be enumerated by operators without contradicting
  self-service. This record therefore proposes a bounded exception: address-class checks for
  tenant-supplied public hosts, TLS and no redirects; private hosts only through the operator
  allow-list. The exception is recorded as a dated Amendment to Standard 11 at acceptance if
  the maintainer approves it.
- Keycloak itself still contacts the validated token, JWKS and UserInfo endpoints at sign-in,
  and the mandatory test sends the client secret to the configured token endpoint. A later DNS
  change is a residual risk that LearnStack cannot close from outside the provider. Network
  egress policy for the Keycloak process is a deployment control, owned by Phase 11's Keycloak
  hardening.

### Linking, email trust and MFA

- **The broker creates no accounts.** `learnstack-first-broker-login` detects an existing user
  (required; an unknown address ends in a closed error), confirms the link, then re-authenticates
  an account with its password and conditional OTP. No create-user, email-verification or
  auto-link step exists.
- **The asserted address selects no account.** The form pre-fills the detected username, but
  the field is editable, and the linked account is whichever submitted username and password
  validate. A hostile IdP therefore links an account only when someone enters that account's
  credentials. Whether an address has an account is visible to the browser that completes the
  flow.
- **Residuals.** A person may enter their password on the genuine provider page after following
  a hostile IdP's link. Any tenant can send any user such a link, with the tenant's chosen
  display name. The "forgot password" action on the form can complete a pending link. Step 3
  qualifies that it resumes only in the browser holding the pending session; if it resumes
  elsewhere, the form's reset action is removed.
- **No verified-email precondition.** The stock flow does not require the local account's email
  to be verified. Registration is open and `verifyEmail` is false, so an account can exist with
  an unverified address registered by someone else. Linking that account still requires its
  credentials, and the reset path resumes the link only for the mailbox owner. This
  pre-registration residual already exists under open registration and is not created by
  federation.
- **Delegation.** A linked IdP's controller can start a Keycloak session as that account. The
  credential and OTP required actions remain available to a fresh session, so the controller can
  add local credentials. The host binding below confines LearnStack sessions, not Keycloak's
  actions. A person with memberships in several tenants should link only an IdP whose controller
  they trust with all of them. Studio, the runbook and the confirm-link copy say so. Recovery is
  Decision Q1.
- **Onboarding.** SSO is a sign-in convenience for an account that already exists. A new person
  registers locally, verifies the address when the invitation requires it, and then links SSO.
  Brokered sign-in creates no Keycloak account. A person with a verified Keycloak account but no
  LearnStack binding enters through ADR-0078's opt-in bootstrap and draws its shared budgets
  exactly as on a first local entry.
- **Not provided.** A trusted-email or per-domain setting, just-in-time provisioning, SCIM,
  claim-to-role mapping and SSO-only enforcement. Each would need its own decision for
  cross-tenant or authority reasons. [Decision Q4](#implementation-notes) decides how the corpus
  records them.
- **MFA.** A brokered sign-in carries no LearnStack MFA assurance. No mapper imports `acr` or
  `amr`, linking requires the account's own OTP, and the tenant IdP's MFA is neither verified nor
  attested. [Architecture 13](../architecture/13-identity-and-auth.md)'s statement that LearnStack
  reads `amr` is corrected at acceptance. Any future MFA-gated operation takes assurance only from
  LearnStack-controlled Keycloak authentication. Production MFA enforcement stays in Phase 11.

### Host binding

The ID token carries `idp` only on brokered authentications. The BFF records the value as the
session's provenance at publication. Every publication (code redemption, the invitation custody
handoff, the consent and active refreshes, selection, creation and context switching) requires:

- `idp` absent (a local authentication) or exactly equal to the host tenant's current Active
  alias; and
- on refresh, equal to the provenance recorded for that session.

The host tenant's alias, state and entitlement come from the authenticated native self call that
publication already makes. The refresh path uses no anonymous read. That call returns a nullable
`federationAlias` for the host tenant, which is a field of the self contract that Step 2 adds.

Outcomes:

- **Definitive mismatch** (different alias, connection not Active, entitlement lapsed, changed
  provenance): refuse the publication, end the BFF session, and end the provider session through
  OIDC end-session ([ADR-0070](0070-owned-tenant-bff-sessions.md)). Other sessions that share the
  provider session end too, which is the documented consequence of a shared SSO note.
- **Indeterminate** (self call timeout, 429, 503, store unavailable): refuse this publication and
  keep the existing session without advancing its idle deadline, following
  [ADR-0070 Amendment 1](0070-owned-tenant-bff-sessions.md). Do not end the provider session, and
  fail new brokered sign-ins closed. Local sign-in follows the existing publication rules.
- The federation test callback never reaches subject reservation or credential assignment.
- Brokered tokens keep the closed access-token profile.

**Residual.** Removing the `idp` mapper makes brokered tokens look local. Enablement requires a
mapper readback, and startup reconciliation detects drift. Manual realm edits by an operator are
outside this contract.

### Lifecycle and test

| State | Provider enabled | Advertised | Brokered sessions admitted |
|---|---|---|---|
| Draft | no | no | no |
| TestOpen (10 minutes) | yes | no | no; only the test callback |
| TestPassed (10 minutes to Activate) | yes | no | no |
| Active | yes | yes, when entitled | yes, on the host's own tenant |
| Disabled | no | no | no |

- **Configure** writes the new configuration with the broker disabled and moves the connection to
  Draft, clearing any test. Configuration is refused when the issuer or entity identifier changed.
- **Rotate secret** is a Configure with a new secret. An Active connection therefore becomes Draft
  and stops being advertised until it passes a new test. Availability is traded for correctness:
  a wrong secret cannot remain advertised.
- **Test start** is allowed from Draft. It enables the broker for 10 minutes, and the page explains
  that the window is open.
- **Test** means signing in through the IdP as yourself. The BFF begins a `federation_test` purpose
  on `learnstack-web` with `kc_idp_hint=<alias>` and `prompt=login`. It validates the ID token like any
  sign-in and requires the `idp` claim to equal the alias and the subject to equal the administrator's
  session subject. Keycloak refuses a brokered identity that resolves to anyone else. The credentials
  entered at re-authentication must be the administrator's own, and a first test links that account
  to the IdP. The link is persistent and stated. The BFF publishes nothing, discards every token and
  reports a closed result. The result is recorded only by the BFF's server-side callback, never by a
  browser-invocable operation, and the attempt identifier stays server-side. The test then signs the
  administrator out, because the shared provider session now carries the brokered note. The page says
  so before the test starts. The result attests an admin-run check, not a security control.
- **Test result.** A pass moves the connection to TestPassed. A failure, abandonment or expiry disables
  the broker and returns the connection to Draft.
- **Activate** moves TestPassed to Active. It requires a readback equal to the desired digest, the
  mapper invariant, enabled state, a fresh egress check, no pending operation and the protocol's
  entitlement. It performs no provider write, so it is not intent-first, and its I/O runs before the
  state transaction.
- **Disable** moves Active to Disabled and writes enabled=false. Brokered sessions end at their next
  refresh, because their alias stops being Active.
- **Enable** moves Disabled to Draft. It performs no provider write, because the broker is already
  disabled, and a new test is required.
- **Delete** removes the broker, confirms its absence and then removes the row. Keycloak removes its
  federated links, and sessions through it end.
- **Rename** writes the display name with the broker's current enabled value.
- **Entitlement lapse** refuses sessions at once, through the host binding, and hides the public
  option. The provider broker is disabled by the next federation operation or readback. Until then it
  is inert for sessions. A linked account's owner can still sign in through it and link or act within
  Keycloak's rules; this residual is accepted and tied to Q1.
- **Expired windows.** A TestOpen or TestPassed broker left enabled past its window is inert for
  sessions and is disabled at the next operation or readback.
- **Last-login recovery.** Local credentials are untouched, and no SSO-only enforcement exists. Disable
  and Delete stop brokering and end brokered sessions. They do not revert credentials or OTP added
  through a brokered session. Account review (credentials, sessions, owner notice) is a Phase 11 runbook
  item and a P03-9 operator recovery step, not an automatic rollback. Q1 decides whether a user can
  unlink their own account.

### Provider writes: serialization, readback and the bounded residual

- No provider-side fence exists (see Context), and this record does not claim one.
- At most one provider write per connection is unsettled at a time. A Begin is refused while another is
  pending. A pending write whose Complete is uncertain becomes eligible for Resume only after a quiet
  window of 90 seconds. This judged bound exceeds the adapter's maximum call duration. It is not
  measured, and it is not a guarantee about Keycloak's processing time.
- Every readback compares the provider representation with the desired digest and the mapper invariant.
  A mismatch is drift: the connection moves to Draft, and a corrective full-state write with the broker
  disabled becomes the next operation. The owner sees that drift occurred.
- Readbacks occur at Resume, test start, test result, Activate, and each Studio detail load under the
  request deadline.
- **Residual.** A request delayed beyond the quiet window can land after the last readback. The next
  readback detects it. While undetected, the exposure is availability (a wrong secret) or an enabled
  broker that no host admits. Step 3 tests this with a controlled delayed proxy and asserts serialization,
  drift detection and downgrade.

### Intent-first operations

Configure, Rotate secret, Rename, test start, Disable and Delete run as committed requests around provider
writes. Enable and Activate perform no provider write; they read the provider at most. One Identity application
service sends these top-level commands through `ISender`, each in its own frame:

1. **Begin**, one request type per operation, validates, authorizes and records a pending operation
   (kind, identifier, actor, start time, desired digest) with its MUST audit intent in one transaction.
   Nothing has touched the provider yet.
2. The provider call applies the full desired state under its own timeout, with no database transaction
   open. A secret exists only in memory for this call, in a redacting type. It is never persisted, logged,
   audited or returned.
3. **Complete**, one shared request type, records `applied`, `failed` with a closed reason, or `uncertain`
   as a successful result with its own MUST audit row. A failed or uncertain outcome is settled, not rolled
   back. Both rows carry the operation identifier. Complete is an ordinary protected internal request under
   the Tenant key, dispatched without an HTTP route, and it authorizes like any protected request. If the
   actor's permission was revoked since Begin, the operation stays pending, and any current administrator's
   **Resume** settles it. A superseded identifier's Complete changes nothing and records drift.

Budgets are unmeasured, judged limits within the 10-second native deadline. Egress has 3 seconds in total,
covering discovery and then JWKS, because the second URL comes from the first. Each database step has
1 second. Provider calls have 2.5 seconds in total, covering a PUT, a readback GET and a mapper GET. Complete
has 1.5 seconds on a detached token, so a client abort still settles. Cancellation of the detached Complete
is a deliberate exception to request-cancellation propagation, documented here. An operation that exceeds a
budget is uncertain.

A second mutation while an operation is pending is refused as a conflict until the first has settled or its
quiet window has passed. Resume completes the pending kind. It completes Configure only when the non-secret
readback equals the stored desired digest. An interrupted Rotate, or a Configure whose readback differs, asks
the administrator to resubmit. Delete removes the row only after the provider confirms absence.

A create that lands after its connection row was removed leaves an orphan broker. The owner marker names a
missing connection, so the orphan is foreign to every tenant, is never adopted or touched, and its removal is
the operator sweep P03-9 owns. Its exposure is limited to its own tenant's IdP.

The mutations accept no `Idempotency-Key`. They converge through `If-Match` on the connection revision, the
pending identifier, a unique-per-tenant row giving a recoverable 409, and a fresh read that reconciles a lost
response. This keeps them out of the protected-receipt census. The native transport admits no DELETE, so Delete
is a POST to an action resource, and the other operations use PUT or POST. The sign-in read and the native self
read use the credential-free public transport or the authenticated self call respectively.

### Sign-in exposure

An Active, entitled connection is exposed to its tenant host by one anonymous, host-bound, read-only, `no-store`
operation, `GET` and `HEAD /api/v1/public/sign-in`. It returns an object with a nullable `federation` member
holding `alias` and `displayName`. It reads LearnStack's database only. The BFF sign-in page adds one labeled
"Sign in with {displayName}" action beside the unchanged local one. A missing, slow or failed read hides only that
action. The alias is a provider hint, not a secret. The BFF never accepts an alias or URL from the browser. A
non-Tenancy module may own a public read, as Education does (`EducationOperationPolicySource.cs`). The public census
then closes in Standard 04's public-surface table, an ADR-0052 Amendment, `OperationHttpClassification`, the
public-surface marker and its registration test, and the OpenAPI/SDK snapshot. The read is audit Off, because it is
anonymous and carries no personal data, and that reason is stated in the Standard 18 row. Each sign-in page view adds
one anonymous call to the per-peer budget of [ADR-0054](0054-bounded-public-renderer-admission.md). The Studio page shows
the redirect/ACS URL, SP entity identifier and Keycloak's descriptor URL, all derived from the alias and the issuer base.

### Authority, audit and storage

- One Tenant-scope key, `identity.federation.admin`, in registry release v4, registered Assignable with orgless
  writes and adopted explicitly by existing Tenant Admin roles. Seed admins adopt v1→v2→v3→v4 in order. The
  eleventh advisory capability is `canManageFederation`. Org Admin and every other role are unaffected. Keycloak
  realm roles grant nothing.
- A default `IFederationProvider` registration fails closed when the admin client is not configured.
- The Hub-side plan row that carries `identity.sso.oidc` is Hub plan data, not a contract change. Until it exists,
  OIDC is unentitled on a real Hub.
- Eleven operations are MUST under `identity.federation.*`: `configure`, `rotate_secret`, `rename`, `enable`,
  `disable`, `delete`, `resume`, `activate`, `test_start`, `test_record` and `apply` (the Complete outcome). Each Begin,
  Resume and Complete is its own request type. Standard 18's baseline row for provider credential rotation covers
  `rotate_secret`. Configuration reads are explicit Off, because they hold no personal data, and the Standard 18
  row states that reason. No audit row carries a secret, an assertion or a token. Snapshots hold non-secret fields and
  digests. ADR-0097's retained-event catalogue is unchanged. Mirroring brokered-login or link events needs its own
  Amendment.
- `federation_connections` is Identity-owned, tenant-owned and tenant-wide, unique per tenant. It uses the
  [canonical RLS template](../standards/05-database.md)'s policy and EF filter, grants only to `learnstack_app`, and a
  forward-only migration. It holds protocol, alias (fixed), issuer or entity identifier, display name, state,
  configuration revision, configuration digest, desired digest, provider digest of the last readback, the pending
  operation with its quiet-window end, the test attempt identifier, expiry and result, client identifier, audit
  columns and row version. CHECKs cover states and exclusivity. A mutation-guard trigger enforces the state machine.
  The Identity tenant fence and lock ordering apply. The applied census, `SchemaTableClasses`, model snapshot and one
  personal-data declaration per scalar change with it. The table stores no secret, token or assertion. Actor references
  are pseudonymous identifiers.

### User experience

Studio gains one Federation page with a three-step layout: configure, test and activate. It shows the values to paste
into the IdP, a write-only secret control, and pending, drift and uncertain banners with Resume. It explains plainly that
SSO links an existing account and delegates it to the IdP, that the test signs you in as yourself and then signs you
out, and that Disable and Delete do not revert credentials added through a brokered session. Disable and Delete require
confirmation. Configuration offers a discovery or metadata URL, or manual endpoints and identifiers with at most two PEM
certificates, all within the 16 KiB native input bound. There is no metadata paste. English and Turkish copy, keyboard and
focus behavior, reflow and contrast follow the existing C20 controls. No identifier, provider message or secret enters
product copy.

## Consequences

### Positive

- A tenant can self-serve SAML and OIDC without gaining realm authority, egress, account creation, or linking beyond an
  owner's own credentials.
- Provider exposure is limited to windows the administrator opened, plus Active connections. A wrong secret cannot remain
  advertised.
- Crashes, timeouts and provider outages leave an auditable, retryable state. Local sign-in is untouched in every failure.
- A brokered session cannot cross tenants. Repointing never inherits links.

### Negative

- A realm-wide provider role backs the adapter. Confinement is LearnStack's alone, and realm invariants (`idp_link`, the flow,
  the disabled account console, the `idp` mapper) depend on reconciliation that production provisioning must replicate.
- The account console leaves the realm for every user. Password change, profile editing and linked-account self-service move to
  the reset flow, and the development personas lose the capability.
- A linked account is delegated to its IdP's controller, who can add local credentials to it inside Keycloak. Only a provider
  extension or a realm per tenant would stop that.
- Provider writes cannot be fenced. The design serializes them, verifies them by readback and detects drift. A delayed request
  beyond the quiet window is detected only at the next readback.
- Rotating a secret requires a new test, so SSO is unavailable until it passes.
- Keycloak's later fetches follow DNS. Only deployment network policy can bound them.
- SSO is not an onboarding path. An administrator whose IdP email matches no local account cannot pass the test, and the test ends
  in a sign-out.
- The test result is admin-reported. A create that lands after its row was removed leaves an orphan until P03-9's sweep.
- The pinned Keycloak must move to 26.6.5 or later before this contract's SAML steps can be qualified.

### Neutral

- No Hub crossing, no new cross-module mechanism and no new Keycloak SDK.
- Production IdP provisioning, network egress policy, MFA enforcement and the Keycloak pin qualification stay in Phase 11, with the
  pin decision recorded below.
- Tenant offboarding removes the connection and broker inside P03-10's tenant-erasure inventory. Until then, Disable, Delete and
  P03-9's sweep apply.

## Implementation Notes

**Open decisions for the maintainer (not yet decided).**

- **Q1 — Self-unlink and recovery.** Stock Keycloak 26.6.2 offers no narrow self-unlink: the account `DELETE` needs `manage-account`.
  Options: (A) no self-unlink; recovery through the tenant's Disable or Delete and the account-review runbook (recommended for P03-8);
  (B) a LearnStack-mediated unlink on the owner's own re-authentication, which needs `manage-users` on the federation client, so the
  adapter gains realm-wide user authority; (C) defer to a Keycloak extension decision owned by Phase 11.
- **Q2 — Egress and Standard 11.** Option (A): record the address-class exception to A10 for tenant-supplied public hosts, with a
  dated Standard 11 Amendment (recommended). Option (B): strict operator allow-list per tenant host, which removes self-service.
- **Q3 — Keycloak pin.** Move 26.6.2 to 26.6.5 (recommended, before Step 3) as a separate infrastructure change with dev-realm import and
  warm-upgrade verification, or accept the advisories listed above until P03-8 ships.
- **Q4 — Non-goals.** Record trusted-email, per-domain, just-in-time provisioning, SCIM, claim mapping and SSO-only enforcement as explicit
  rejections in this record (recommended), or assign each to a named phase.

**At acceptance:** record G12 and its decisions; append dated Amendments to ADR-0004, 0021, 0052, 0064, 0068, 0070, 0072, 0078, 0079, 0093,
0097 and 0098 where their Accepted text changes. ADR-0068 must record the account-client disablement, which supersedes its three detachments
for that client. ADR-0098's host-check and end-session interplay is analysed in the ADR-0070 Amendment. Reconcile Architecture 13 (direct
admin API statement, MFA, the `amr` correction, the public read), Standards 01, 04, 05, 07, 09, 10, 11, 18, 19, 20 and 21, the Identity README,
permissions, audit and providers specifications, the Keycloak README, the Phase 03 and Phase 11 documents, the glossary and the skills.
Phase 11 gains explicit entries for Keycloak network egress policy, the Keycloak pin and advisory qualification, and the Q1 decision if (B) or
(C) is chosen.

**Steps.**

1. **Step 1 — authority and storage.** Registry v4 with `canManageFederation`, across the domain, migration, SDK and OpenAPI contracts and the BFF
   strict parsers, with the explicit v2→v4 adoption chain. The `federation_connections` aggregate, migration, RLS, census and `learnstack_app`
   proof.
2. **Step 2 — application and API.** The Begin, apply and Complete service, the commands and queries over a fake provider port, the entitlement
   gate, the audit catalogue and matrix, the HTTP and OpenAPI census, the public sign-in read, and the `federationAlias` field on the self contract.
   Unit, architecture and database proof.
3. **Step 3 — provider and realm.** The egress fetcher, the Keycloak adapter, realm reconciliation (client, flow, `idp_link`, account clients, mapper)
   with its zero-write rerun, and real two-tenant OIDC and SAML brokers. Negatives: linking with and without the owner's credentials, confinement, egress
   refusals, secret refusal, attribute injection, account console and deprecated builder refusal, reset-link resumption, delayed-request serialization
   and drift detection with a controlled proxy, orphan handling, and mapper readback. Requires the Keycloak pin decided by Q3.
4. **Step 4 — native and Studio.** The BFF hint, the `federation_test` owner with mutually exclusive callback purposes, the host binding at every publication path,
   the Studio page, SDK and native operations, i18n, accessibility, and end-to-end proof. Then full qualification, document reconciliation and the PR.

## Architecture Tests

- Existing Identity, permission, audit, RLS, endpoint-census, public-surface, SDK, native and BFF obligations extend to the new subjects with dirty controls.
  None is relaxed. Canonical names for the new obligations are assigned in Standard 21 at acceptance.
- New obligations: the adapter admits only the enumerated calls and aliases; no other code holds a federation client; the egress fetcher is the only
  tenant-URL fetcher; no `fromUrl`; fixed-profile key-set equality; no secret in any audit, log, DTO, persisted column or idempotency fingerprint
  (no fingerprint is taken for these mutations); the issuer or entity identifier cannot change in place.
- Real-provider controls: two tenant brokers coexist, and neither reads or changes the other's; an unknown address creates no account; a foreign
  address links only with the owner's credentials; a disabled or hidden broker is not reachable with `kc_idp_hint`; the account console, deprecated
  builder and broker link paths refuse a brokered session; a session from tenant A's broker is refused on tenant B's host and after Disable; the
  `idp` mapper and provenance checks refuse a mid-session change; indeterminate self-call outcomes keep the session without ending the provider
  session; brokered tokens keep the closed profile; private, metadata and loopback endpoints refuse; a failed activation keeps local sign-in.

## References

- [Phase 03](../roadmap/phase-03-identity-admin.md) G12, D11 and C13,
  [Authentication](../architecture/13-identity-and-auth.md#sso-and-federation),
  [Security 11](../standards/11-security.md), [Audit 18](../standards/18-audit-coverage.md),
  [Permissions 19](../standards/19-permissions.md), [Infrastructure Stack 20](../standards/20-infrastructure-stack.md).
- [ADR-0004](0004-authentication-strategy.md), [ADR-0033](0033-audit-durability-model.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md), [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md), [ADR-0078](0078-native-session-scope-and-self-entry.md),
  [ADR-0079](0079-bounded-identity-administration.md), [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md),
  [ADR-0095](0095-revision-pinned-membership-custom-fields.md), [ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md),
  [ADR-0098](0098-session-targeted-back-channel-logout.md).
- Keycloak 26.6.2 sources:
  [IdentityProvidersResource](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/IdentityProvidersResource.java),
  [IdentityProviderResource](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/IdentityProviderResource.java),
  [IdpDetectExistingBrokerUserAuthenticator](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/authentication/authenticators/broker/IdpDetectExistingBrokerUserAuthenticator.java),
  [IdpLinkAction](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/authentication/requiredactions/IdpLinkAction.java),
  [LinkedAccountsResource](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/account/LinkedAccountsResource.java),
  [IdentityProviderModel](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi/src/main/java/org/keycloak/models/IdentityProviderModel.java).
- Keycloak security advisories for the CVEs listed in the advisory baseline (fixed in 26.6.4 or 26.6.5).
