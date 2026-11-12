# ADR-0099: Confined Tenant Federation

## Status

Proposed — 2026-11-13. Acceptance, implementation and qualification are separate
records; this proposal changes no runtime behavior, realm, client or standard.

**Date:** 2026-11-13
**Deciders:** @cemil

This closes Phase 03 gate G12 for P03-8 when Accepted. It applies
[ADR-0004 Amendment 1](0004-authentication-strategy.md)'s realm-level brokering to
the first runtime Keycloak Admin adapter, which reopens the provider-boundary pass
that [P02b-6](../roadmap/phase-02b-delivery-log.md) reserved for that event.
[ADR-0072](0072-application-owned-identity-bootstrap.md)'s rejection of a runtime
Admin API concerned application-identity allocation; this record adds no allocator,
reissue or email merge. Dated Amendments, not body rewrites, record the changes to
ADR-0004/0064/0070/0072/0078/0079/0097 listed under Implementation Notes.

The [P03-8 package](../roadmap/phase-03-delivery-log.md#p03-8-proposed-tenant-federation-package-2026-11-13)
owns the three implementation steps and their proof.

## Decision Drivers

- One realm serves every tenant. `manage-identity-providers` is realm-wide, so a
  tenant's authority over "its" broker exists only if LearnStack confines it; the
  provider cannot.
- Keycloak 26.6.2's JSON `import-config` fetches any supplied `fromUrl` through its
  HTTP client with no address check
  ([source](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/IdentityProvidersResource.java)).
  A tenant-supplied URL must not reach it unguarded. Standard 11 A10 requires
  allow-listed outbound calls, while
  [P02b m-38](../roadmap/phase-02b-delivery-log.md) records private IdP networks as
  legitimate for SaaS, Dedicated and Self-Hosted.
- A tenant-controlled IdP asserts any `email` it likes. [ADR-0093](0093-verified-invitation-acceptance-and-handoff.md)
  admits an invitee only on UserInfo `email_verified == true`, so trusting an IdP's
  assertion would let one tenant's IdP accept another tenant's invitation.
- MUST audit commits with the state it describes
  ([ADR-0033](0033-audit-durability-model.md)); a provider write cannot join that
  transaction, and an unknown provider outcome must stay visible rather than guessed.
- Federation is additive. No failed, mistaken or malicious configuration may remove
  existing local sign-in, and anonymous pages stay independent of the provider
  ([ADR-0064](0064-tenant-api-authentication-boundary.md)).
- Existing seams are fixed: entitlement keys `identity.sso.saml`/`identity.sso.oidc`,
  registry-release adoption, the closed native administration dispatcher and the
  shared new-subject budgets of [ADR-0078](0078-native-session-scope-and-self-entry.md).
- The maintainer answered three product forks on 2026-11-13: a real interactive test
  is required before activation; an existing-account link is permitted only through
  that account's own re-authentication; and a tenant has one connection.

## Considered Options

1. **LearnStack-owned connection registry, dedicated Admin adapter, fixed broker
   profile and intent-first provider operations** (chosen). Tenants author a closed
   set of fields; LearnStack owns every flag, flow and egress decision.
2. **Operator-provisioned brokers, read-only tenant UI** (rejected). Confines
   trivially but delivers no usable tenant configuration (D11).
3. **Realm per tenant** (rejected). ADR-0004 Amendment 1 keeps it an enterprise
   opt-in; it multiplies issuers, BFF configuration and provisioning, and leaves
   realm-level authority just as broad.
4. **Pass-through of Keycloak's IdP representation and `import-config fromUrl`**
   (rejected). Lets a tenant choose flows, mappers, role assignment, trust flags
   and egress targets, i.e. realm authority.
5. **Orchestration alternatives for provider writes.** Provider call inside the
   business transaction (rejected: holds a connection across I/O and still leaves an
   unaudited effect on a failed COMMIT); the ADR-0047 job runtime (rejected: its
   per-operation capacity/quiescence machinery protects irreversible external sends;
   an idempotent desired-state PUT does not need it); **intent-first** commit, provider
   call, outcome commit (chosen).
6. **Email-based or verification-mail linking, `trustEmail=true`** (rejected). Both
   convert an asserted claim into account takeover or cross-tenant invitation
   acceptance.

## Decision

LearnStack stores one `FederationConnection` per tenant and is the sole confiner of
the realm-level broker. A dedicated confidential client
`learnstack-federation-admin`, holding only `realm-management/manage-identity-providers`,
backs one Infrastructure adapter that calls a fixed set of Admin endpoints for aliases
LearnStack derived and can prove it owns. Tenants author only protocol, display name,
OIDC endpoints/client credentials or SAML metadata; every flag, flow, mapper and trust
setting is fixed by LearnStack. Tenant-supplied URLs and metadata are fetched only by
a LearnStack egress guard, never by Keycloak. A connection becomes advertised only
after a real brokered test sign-in on its current revision. Brokered email is never
trusted, never merged and grants nothing; an existing account links only through its
owner's own re-authentication. Every provider write is preceded by a committed MUST
audit intent and followed by a committed outcome, so a crash or unknown result stays
visible and retryable. Local sign-in is never affected.

## Context

### Verified baseline (HEAD `0e029dcf`, Keycloak 26.6.2)

- The `learnstack` export declares no `identityProviders`, `authenticationFlows` or
  `firstBrokerLoginFlow`, so stock brokering would apply, including its
  existing-account linking. Registration is open, `verifyEmail` is false and
  duplicate emails are refused. No runtime Admin adapter exists; the only runtime
  provider clients are the UserInfo recipient reader and the read-only
  `learnstack-event-reader` of [ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md).
- The BFF authorization request is fixed (`openid profile email`, optional
  `kc_action=VERIFY_EMAIL`); it carries no `kc_idp_hint`, and the sign-in page offers
  one fixed local action. The closed token profile whitelists no `amr`/`acr`
  claim, so no LearnStack operation authorizes on assurance today.
- `identity.sso.saml`/`identity.sso.oidc` are plan-projected, default-false,
  fail-closed keys that exist in both repositories; `NullEntitlementProvider` grants
  them. Registry release v3 is current, and a new Tenant key needs release v4,
  explicit adoption and an eleventh advisory capability.
- Keycloak update treats a masked `clientSecret` (`**********`) as "keep the stored
  value", so a secret never has to be read back or retained by LearnStack. The
  broker's alias is recorded as the user-session note `identity_provider`, which a
  stock user-session-note mapper can emit.

### Ownership and the confined adapter

- A connection's alias is `lsf-` plus the connection ID in 32 lowercase hexadecimal
  characters; LearnStack derives it and never accepts one. The provider
  representation carries a `learnstackOwner` configuration entry holding tenant and
  connection IDs. The adapter refuses any read or write unless both sides agree,
  so a stale row, a foreign alias or a manually created broker is never touched.
- The adapter admits only these calls: token endpoint, `POST` and `GET`/`PUT`/`DELETE`
  `identity-provider/instances[/{alias}]`, `GET instances/{alias}/mappers`, and the
  multipart-`file` form of `identity-provider/import-config`. No JSON `fromUrl`, list
  of other aliases, user, flow, realm or client call is admitted. A non-empty mapper
  list on an owned alias is drift and refuses activation; the adapter never creates a
  mapper.
- Credentials: a fresh client-credentials token per operation, the secret through
  `ISecretProvider` (`Federation:ClientSecret`), HTTPS mandatory except the explicit
  local-Development loopback, no redirects and no ambient proxy credentials. As with
  `learnstack-event-reader`, the provider role is broader than the admitted calls;
  that is stated, not hidden. No BFF, master, demo or event-reader credential is
  substituted. Vendor types and exceptions stay inside
  `LearnStack.Infrastructure.Identity.Federation` and surface as `ProviderException`s;
  direct HTTP introduces no SDK, so `Provider_SDK_Types_NotImportedOutsideInfrastructure`
  keeps its Awaiting-backfill status.

### Fixed broker profile

Every write enforces, regardless of input, and every readback must equal:

| Setting | Value | Why |
|---|---|---|
| `hideOnLogin` | true | The shared login page never lists another tenant's IdP |
| `trustEmail` | false | An asserted address is never verified; ADR-0093 stays intact |
| `linkOnly`, `storeToken`, `addReadTokenRoleOnCreate`, `authenticateByDefault` | false | No account-console linking, token retention or default redirect |
| `firstBrokerLoginFlowAlias` | `learnstack-first-broker-login` | The only admitted linking behavior |
| `postBrokerLoginFlowAlias`, IdP mappers | none | No claim-to-role/group/attribute authority |
| `syncMode` | `IMPORT` | Profile data is imported once, never overwritten by the IdP |
| OIDC | signature validation on, JWKS URL, PKCE S256, scope `openid email profile`, closed client-authentication choice | Standard-conformant, no tenant-selected trust relaxation |
| SAML | signature validation on, signed assertions required, no IdP-initiated SSO | Unsigned or unsolicited assertions refuse |

`enabled` is true for Draft and Active and false for Disabled. The exact keys and
representation live in the Identity provider specification at acceptance.

### Bounded egress

LearnStack's `IFederationEgressFetcher` retrieves OIDC discovery, JWKS or SAML
metadata. Keycloak is never handed a URL to fetch during configuration.

- HTTPS only; port 443 unless allow-listed; no userinfo, fragment or IP-literal host;
  at most 2,048 characters.
- Every resolved address must be globally routable. Loopback, link-local (including
  cloud metadata), private, CGNAT, documentation, multicast, reserved and mapped/ULA
  IPv6 ranges refuse. The connection is made to the validated address itself, with
  SNI and certificate verification for the original host, so a rebinding between check
  and connect cannot redirect it.
- No redirects, cookies, credentials or proxy; five-second timeout; bodies of at most
  64 KiB (discovery) or 256 KiB (JWKS, metadata); content type checked; XML read with
  DTD processing prohibited and no resolver.
- An operator allow-list `Federation:Egress:AllowedHosts` (exact `host` or `host:port`,
  no wildcard, empty by default) admits named private or non-443 endpoints. A named
  host is the only exception; it is composition configuration, never tenant input,
  and the module never reads `DeploymentMode`. This decides the deployment policy that
  m-38 left open.
- The guard runs at Configure, Enable, Activate and test start. Each discovered
  endpoint (authorization, token, JWKS, UserInfo) is guarded independently, because
  real IdPs legitimately span hosts; the adapter then writes explicit endpoints, not a
  discovery URL, so Keycloak does not re-discover.
- Keycloak itself still contacts the validated token, JWKS and UserInfo endpoints at
  sign-in. A later DNS change is a residual risk that LearnStack cannot close from
  outside the provider. Production network egress policy for the Keycloak process is a
  required deployment control, recorded in the realm runbook; Phase 11 owns its
  production qualification. This ADR does not claim it.

### Linking, email trust and MFA

- `learnstack-first-broker-login` is a realm-owned flow: review profile on missing
  attributes, create the user when unique, otherwise confirm and **re-authenticate
  the existing account** (password and its conditional OTP). The "verify existing
  account by email" alternative and any auto-link authenticator are absent. A foreign
  or malicious IdP asserting a victim's address therefore creates nothing and links
  nothing unless the victim's own credentials are presented. The residual risk is a
  victim deliberately entering their password on a Keycloak page after following a
  hostile IdP link; the page copy and runbook say so, and the maintainer accepted it
  over the usability cost of refusing every duplicate.
- A brokered new user has an unverified address. Invitation acceptance then uses the
  existing `kc_action=VERIFY_EMAIL` purpose, with no new code. A per-tenant or
  per-domain trusted-email setting is not provided: trusting tenant A's assertion for
  tenant B's invitation is the cross-tenant acceptance this ADR exists to prevent, and
  it would need its own decision.
- Federation grants no Membership, Role or group. There is no just-in-time
  provisioning, SCIM, or claim mapping; a federated person enters a tenant through the
  existing invitation journey. A brokered new subject uses the existing opt-in
  bootstrap and its shared 10/minute, 100/day and 10,000 lifetime budgets. A hostile
  tenant IdP can consume that allowance exactly as open registration already can;
  operator-initiated disabling of a tenant connection is the Platform-scope recovery
  that P03-9 owns.
- A brokered sign-in carries no LearnStack MFA assurance. No IdP mapper imports `acr`
  or `amr`, linking requires the existing account's own OTP, and the tenant IdP's MFA is
  neither verified nor attested. Architecture 13's statement that LearnStack reads
  `amr` is corrected. Any future MFA-gated operation takes assurance only from
  LearnStack-controlled Keycloak authentication; production MFA enforcement remains
  Phase 11.
- User-initiated account-console linking to a tenant IdP is not a supported path. Step 2
  qualifies on the pinned provider that a hidden IdP cannot be linked from the account
  console; if it can, reconciliation removes the realm-wide linked-account capability
  and records a dated Amendment to ADR-0070's development account-console note.

### Lifecycle, test and activation

| State | Provider | Meaning |
|---|---|---|
| Draft | enabled, hidden | Configured; not advertised; tested at most for this revision |
| Active | enabled, hidden | Advertised on the tenant sign-in page; requires a passed test |
| Disabled | disabled | Rolled back; not advertised; configuration retained |

- **Configure** (create or replace identity-bearing fields: endpoints/metadata, client
  ID, protocol) returns the connection to Draft, increments its revision and clears the
  test result. **Rotate secret** and display-name edits keep the state. A tenant
  changes protocol by deleting and recreating its single connection.
- **Test** is interactive. The admin starts a single-use, ten-minute attempt bound to
  actor, revision and a random identifier. The BFF begins a new `federation_test`
  purpose on the existing `learnstack-web` client with `kc_idp_hint=<alias>` and
  `prompt=login`, validates the returned ID token like any sign-in, and requires its
  `idp` claim to equal the alias. That claim is the only new token claim: an
  ID-token-only user-session-note mapper on `learnstack-web`, and the API's closed access
  token profile is unchanged. The BFF then discards every token, publishes no session,
  admits no subject, reports a closed result (`passed`/`failed`, address present and
  verified flags) to the API under the admin's own session, and navigates to the
  provider end-session endpoint so the brokered Keycloak session ends. The result
  attests an admin-run check, not a security control; the security controls are the
  fixed profile and confinement.
- **Activate** is a registry-only transition: it requires a passed test on the current
  revision no older than 24 hours, a fresh provider readback equal to the fixed
  profile with no mappers, a fresh egress check, no pending operation and the
  protocol's entitlement. **Disable** is the rollback: it sets the provider disabled,
  clears the test and removes advertisement; **Enable** returns to Draft and needs a
  new test.
- Entitlement lapse, killswitch or a Disabled state removes advertisement and refuses
  Configure/Enable/Activate/test. It does not by itself disable an enabled provider
  broker, which stays unadvertised and reaches only a subject with no authority. A
  Platform-scope disable is P03-9; the tenant admin can always Disable.
- Last-login recovery is structural: local credentials and password reset are
  untouched, no SSO-only enforcement exists, and Disable or Delete restores the
  pre-federation state from Studio. SSO-only enforcement would need a Keycloak
  extension and its own decision; no phase owns it and this ADR does not provide it.

### Intent-first provider operations

Configure, Rotate secret, Enable, Disable and Delete each run as two committed
requests around one idempotent provider call:

1. **Begin** validates and authorizes, records a pending operation (kind, revision,
   identifier, start time) and its MUST audit intent in one transaction. Nothing has
   touched the provider.
2. The provider call applies the desired state with its own short timeout and no
   database transaction open. A secret exists only in memory for this call and is never
   persisted, logged, audited or returned.
3. **Complete** records `applied`, `failed` with a closed reason, or `uncertain`, with
   its own MUST audit row, and settles the state. Delete removes the row only after
   the provider confirms absence.

A second mutation while an operation is pending is refused as a conflict until the
first is older than 60 seconds, after which the new Begin supersedes it, which is safe
because each call applies a full desired state. A crash or lost response leaves the
operation pending and visible; Resume re-reads the provider and completes Enable,
Disable and Delete, and completes Configure only when the non-secret readback equals the
desired digest. An interrupted Rotate asks the admin to resubmit the secret, because
LearnStack cannot compare it. Configuration, readback and all fixed-profile checks
precede Complete.

### Sign-in exposure

An Active, entitled connection is exposed to the tenant host by one anonymous,
host-bound, read-only, `no-store` operation, `GET /api/v1/public/sign-in`, returning
`{alias, displayName}` or `null`. It reads LearnStack's database only. The BFF sign-in
page adds a second, clearly labeled "Sign in with {displayName}" action beside the
unchanged local one; a missing, slow or failed read hides it and nothing else
changes. The alias is the provider hint, not a secret: it already appears in the
browser during login. The BFF never accepts an alias or URL from the browser.

### Authority, audit and storage

- One Tenant-scope key, `identity.federation.admin`, in registry release v4, adopted
  explicitly by existing Tenant Admin roles, with a corresponding advisory
  capability. Org Admin and every other role are unaffected; writes need the
  orgless tenant-wide native context. Keycloak realm roles grant nothing.
- Mutations and results are MUST (`update` or `security-event` as classified in the
  Identity audit matrix); the secret rotation is the baseline "provider credential
  rotated" MUST; test start is SHOULD; configuration reads are explicitly Off. No row
  carries a secret, an assertion or a token; snapshots hold the non-secret fields and
  the configuration digest.
- `federation_connections` is Identity-owned, tenant-owned and tenant-wide: unique per
  tenant, RLS and EF filter by the [canonical template](../standards/05-database.md),
  `learnstack_app` grants only, forward-only migration, explicit personal-data
  classification of every mapped scalar. It stores no secret, token or assertion.
  Identity owns the aggregate; the adapter and the egress fetcher are infrastructure.

### User experience

Studio gains one Federation page with a three-step, state-aware layout (configure, test,
activate), the redirect/ACS and SP-metadata values to paste into the IdP, a clear
write-only secret control, pending/drift/uncertain banners with Resume, and
confirmed, plainly worded Disable and Delete. English and Turkish copy, keyboard and
focus behavior, reflow and contrast follow the existing C20 controls. No internal
identifier, provider message or secret enters product copy.

## Consequences

### Positive

- A tenant can self-serve SAML/OIDC without gaining realm authority or egress.
- A crash, timeout or provider outage leaves an auditable, retryable, never-silent
  state, and local sign-in is untouched in every failure.
- No cross-tenant invitation acceptance, email merge or claim-driven authority.

### Negative

- A realm-wide provider role backs the adapter; confinement is LearnStack's alone.
- Keycloak's later fetches follow DNS; only the deployment's network policy can bound
  them.
- Linking an existing account asks its owner for their password on a provider page.
- Federated users need an invitation and a Keycloak address verification before they
  hold any membership.
- The test attestation is admin-reported.

### Neutral

- No Hub crossing, no new cross-module mechanism, no new Keycloak SDK.
- Production IdP provisioning, network egress policy and MFA enforcement stay Phase 11.

## Implementation Notes

1. **At acceptance:** record G12; append dated Amendments to ADR-0004, 0064, 0070, 0072,
   0078, 0079 and 0097; reconcile Architecture 13, Standards 04/05/07/11/18/19/20/21,
   Identity README/permissions/audit/providers, the Keycloak README, glossary and
   skills.
2. **Step 1 — contract and storage.** Aggregate, migration/RLS/census, registry v4,
   capability, policy and audit catalogue, entitlement gate, application commands and
   queries over a fake provider port, HTTP/OpenAPI census and database/unit/
   architecture proof.
3. **Step 2 — provider and realm.** Egress fetcher, Keycloak adapter, realm
   reconciliation (`learnstack-federation-admin`, the first-broker flow, the `idp`
   mapper), real two-tenant OIDC and SAML brokers, linking, email, confinement and
   egress negatives.
4. **Step 3 — native and Studio.** Public sign-in read, BFF hint and test purpose,
   Studio page, SDK/native operations, i18n, accessibility and real end-to-end proof;
   full qualification and PR.

## Architecture Tests

- Existing Identity, permission, audit, RLS, endpoint-census, SDK/native-census and
  BFF obligations extend to the new subjects with dirty controls; none is relaxed.
- New: the Admin adapter admits only the enumerated calls and aliases; no module or
  adapter other than it holds a federation client; the egress fetcher is the only
  tenant-URL fetcher; no `fromUrl`; the fixed-profile equality check; no secret in
  any audit, log, DTO or persisted column.
- Real-provider controls: two tenant brokers coexist, one cannot read or change the
  other's, foreign-address linking fails without credentials and succeeds only with
  them, brokered tokens keep the closed profile, a failed activation keeps local
  sign-in, and the private/metadata/loopback endpoints refuse.

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
  [IdpCreateUserIfUniqueAuthenticator](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/authentication/authenticators/broker/IdpCreateUserIfUniqueAuthenticator.java).
