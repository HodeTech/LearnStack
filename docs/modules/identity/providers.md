# Identity — Provider Adapters

**Derives from:** [ADR-0093](../../decisions/0093-verified-invitation-acceptance-and-handoff.md),
[ADR-0097](../../decisions/0097-retained-provider-events-and-tenant-audit-mirror.md)
and [ADR-0099](../../decisions/0099-confined-tenant-federation.md).
These are the bounded invitation, retained-event and tenant-federation provider
contracts; the [Phase 03 Status block](../../roadmap/phase-03-identity-admin.md) owns
implementation and qualification. Acceptance is not evidence of a configured production
sender.

## Current recipient UserInfo

| Boundary | Source binding |
|---|---|
| Application port | Identity Application.Contracts `ICurrentInvitationRecipientReader`; only the current inspection or initial acceptance invocation |
| Credential owner | API `ValidatedInvitationCredential`, captured from the actual stock-validated bearer; neither a reparsed header nor a caller-supplied token is authority |
| Provider adapter | API `CurrentInvitationRecipientReader` and its fixed backchannel; configured tenant issuer only, no supplied endpoint, redirect, cookie or certificate override |
| Observation | One fresh request/token/invocation-bound evidence object; normalized mailbox matching belongs to the exact offer handler, with no User email mirror or reusable inspection proof |
| Accepted recovery | `GetOwnInvitationResultQuery` does not call this provider; it reads the original same-actor Membership outcome under normal host/lifecycle availability |

[Security Standards](../../standards/11-security.md#recipient-atomicity-and-fences)
own the claim grammar, response/deadline limits and no-business-lock observation
boundary. Actual transport cleanup precedes evidence publication; provider
exception payloads do not cross the adapter. The finite
[API contract](../../standards/04-api-design.md#verified-invitation-transport)
owns bounded failure responses. No realm-admin credential or account search is
introduced. Keycloak owns verification email separately from LearnStack's
invitation SMTP sender; the [realm setup](../../../infra/keycloak/README.md)
and phase qualification must establish that provider journey.

## Retained user-event reader

`IIdentityEventProvider` is the common read-only port. Its internal sealed
`KeycloakIdentityEventProvider` obtains a fresh service token for each operation
and admits only the pinned token, event-policy and retained-user-event paths.
Normalization exposes selected typed facts or closed malformed/oversized
positions; raw provider bodies, user IDs, credentials and provider exceptions do
not cross the adapter. `IdentityEventKindCodes` owns the selected twenty-kind map.

The API composes one retained HTTP owner, the separately qualified machine
database source and fresh tenant production scopes. A provider outage stops its
read lane while already accepted targets remain eligible for outbox production.
The machine source never lends its connector to an application handler. The
[database contract](../../standards/05-database.md#provider-event-intake-storage)
owns receipts, claims, problem recovery and grants; the
[realm runbook](../../../infra/keycloak/README.md) owns exact configuration,
credential rotation and operational recovery. Session revocation uses the
separate signed native path in
[ADR-0098](../../decisions/0098-session-targeted-back-channel-logout.md);
retained provider facts confer no revocation authority.

## Tenant federation adapter

[ADR-0099](../../decisions/0099-confined-tenant-federation.md) § Ownership, § Fixed broker
profile and § Bounded egress decide this boundary; this section is its exhaustive
specification.

| Boundary | Contract |
|---|---|
| Ports | SharedKernel `IFederationProvider` and `IFederationEgressFetcher`; held only by `FederationOperationRunner` |
| Adapter | Internal sealed `KeycloakFederationProvider` in `LearnStack.Infrastructure.Identity.Federation`; direct HTTP, no SDK |
| Fetcher | Internal sealed `FederationEgressFetcher` in the same namespace; the only fetcher of a tenant-supplied URL |
| Selection | API `FederationComposition` replaces the fail-closed defaults only when `Federation:ClientSecret` is configured; `FederationAdapterFactory` is the only entry. The choice is made when the port is first resolved (it follows `ISecretProvider`), so an empty or invalid `Authentication:Tenant:Issuer` with the secret present refuses at that first resolution, not at host startup |
| Credentials | Client `learnstack-federation-admin`; secret through `ISecretProvider` key `Federation:ClientSecret`; a fresh client-credentials token per port call |
| Resilience | `IProviderResilience<IFederationProvider>`, configuration `Resilience:federation`: retry disabled, a 2.5 s timeout, circuit breaker on, a bulkhead of exactly 4 with no queue; these are fixed values, and composition refuses any other value at startup. The enabled breaker's parameters are bounded: failure ratio in (0, 1], minimum throughput 2 to 100, sampling duration 1 to 60 s, break duration 1 to 300 s (defaults 0.5, 10, 30 s, 30 s); a value outside a range refuses at startup |
| Failures | Transport faults, 5xx and 429 are `FederationProviderException` (a `ProviderException`) inside the pipeline and leave as `Unavailable`; no provider text crosses the port |

### Admitted calls

The adapter builds every route in `KeycloakFederationRoutes`, and only these:

| Call | Use |
|---|---|
| `POST {realm}/protocol/openid-connect/token` | Client credentials for `learnstack-federation-admin`; the token must carry `azp` of that client, the tenant issuer and exactly the `realm-management` role `manage-identity-providers`, or nothing else is sent |
| `GET admin/realms/learnstack/identity-provider/instances/{alias}` | Owned readback; precedes every write and delete |
| `POST admin/realms/learnstack/identity-provider/instances` | Create, only when the owned alias is absent |
| `PUT admin/realms/learnstack/identity-provider/instances/{alias}` | Full-state replace of an owned broker. Refused without a call when it would change the stored issuer or SAML entity identifier, or, without a fresh secret, the token endpoint, client identifier or client authentication ([ADR-0099 Amendment 5](../../decisions/0099-confined-tenant-federation.md#amendment-5--step-3-review-egress-adapter-and-linking-decisions-2026-11-14)) |
| `DELETE admin/realms/learnstack/identity-provider/instances/{alias}` | Delete of an owned broker; an absent alias is already deleted |
| `GET admin/realms/learnstack/identity-provider/instances/{alias}/mappers` | Mapper count for the readback; any mapper is drift |

The `{alias}` is only `lsf-` and the connection identifier's 32 lowercase hexadecimal
characters, from `FederationBrokerOwner`. No other alias, user, flow, realm, client or
required-action route exists, and the JSON `import-config` `fromUrl` form is never sent.
ADR-0099 also admits the multipart `file` form of `import-config`; Step 3 does not call
it, because the egress fetcher's own hardened parser already produces the three SAML
values the profile consumes and the port carries no metadata document (the delivery log
records the decision). The pinned 26.7.5 parser was observed to return
`validateSignature=true` for metadata whose `WantAuthnRequestsSigned` is false; the
adapter never forwards its output either way.

### Ownership and readback

- Every write carries `config.learnstackOwner` = `{tenantId}:{connectionId}`. The
  adapter reads the alias first; a marker naming another tenant or connection, or an
  owned alias that no longer reads as an admitted protocol, is `Foreign` and is never
  written, read further or deleted.
- An OIDC create without a fresh secret is refused: no secretless broker is created. A
  write without a fresh secret sends the provider's mask literal, which keeps the stored
  secret; omitting the member would erase it. The secret is never read back: the
  readback requires the mask literal and returns no secret.
- A readback conforms only when its top-level members and its configuration key set and
  values equal the representation below exactly (`internalId` and `types` are
  provider-computed and ignored). Any extra, missing or differing key is drift.

### Fixed broker profile

Top level, both protocols: `alias`, `displayName` (the connection's display name),
`providerId` (`oidc` or `saml`), `enabled`, `trustEmail=false`, `storeToken=false`,
`addReadTokenRoleOnCreate=false`, `authenticateByDefault=false`, `linkOnly=false`,
`hideOnLogin=true`, `firstBrokerLoginFlowAlias=learnstack-first-broker-login`.
`postBrokerLoginFlowAlias`, `organizationId` and `updateProfileFirstLoginMode` are absent.

Configuration, both protocols: `learnstackOwner`, `showInAccountConsole=NEVER`,
`syncMode=IMPORT`, `doNotStoreUsers=false`, `passMaxAge=false`, `loginHint=false`,
`allowedClockSkew=0`, `caseSensitiveOriginalUsername=false`, `sendClientIdOnLogout=false`,
`sendIdTokenOnLogout=false`, `backchannelSupported=false`.

OIDC adds exactly: `issuer`, `clientId`, `clientSecret` (written; reads back as the
mask), `clientAuthMethod` (`client_secret_post` or `client_secret_basic`),
`authorizationUrl`, `tokenUrl`, `jwksUrl`, `userInfoUrl` (only when configured),
`disableUserInfo` (`true` exactly when no UserInfo endpoint), `useJwksUrl=true`,
`validateSignature=true`, `pkceEnabled=true`, `pkceMethod=S256`, `disableNonce=false`,
`disableTypeClaimCheck=false`, `defaultScope=openid email profile`,
`supportsClientAssertions=false`, `jwtAuthorizationGrantEnabled=false`,
`acceptsPromptNoneForwardFromClient=false`, `filteredByClaim=false`,
`isAccessTokenJWT=false`, `uiLocales=false`. `forwardParameters`, `prompt`, `logoutUrl`,
`tokenIntrospectionUrl` and every client-assertion key are absent.

SAML adds exactly: `idpEntityId`, `singleSignOnServiceUrl`, `signingCertificate` (one or
two base64 DER certificates, comma-separated), `validateSignature=true`,
`wantAssertionsSigned=true`, `wantAssertionsEncrypted=false`,
`wantAuthnRequestsSigned=false`,
`nameIDPolicyFormat=urn:oasis:names:tc:SAML:2.0:nameid-format:persistent`,
`principalType=SUBJECT`, `postBindingResponse=true`, `postBindingAuthnRequest=true`,
`postBindingLogout=false`, `artifactBindingResponse=false`,
`useMetadataDescriptorUrl=false`, `forceAuthn=false`, `allowCreate=true`,
`addExtensionsElementWithKeyInfo=false`, `signSpMetadata=false`,
`xmlSigKeyInfoKeyNameTransformer=KEY_ID`. `metadataDescriptorUrl`,
`singleLogoutServiceUrl`, `artifactResolutionServiceUrl` and `entityId` (the SP
identifier stays the realm default) are absent. No realm client carries an IdP-initiated
SSO URL name, so no unsolicited SAML response has a target.

### Bounded egress

`FederationEgressFetcher` fetches OIDC discovery and JWKS and SAML metadata, and checks
every endpoint the provider later contacts (authorization, token, JWKS, UserInfo, SAML
SSO) without fetching it:

- HTTPS only; an ASCII DNS host (no IP literal, no internationalized name: enter its
  punycode form), no userinfo or fragment, at most 2,048 characters; port 443 unless
  the operator allow-list names the port.
- The host is resolved once. Every answer must be globally routable: loopback,
  link-local (169.254.0.0/16 cloud metadata included), private, CGNAT, documentation,
  benchmarking, multicast, reserved, unspecified and every IPv4-embedding IPv6 form
  (mapped, compatible, NAT64, 6to4, Teredo), unique-local and site-local IPv6 refuse;
  IPv6 must be global unicast. One refused answer refuses the host.
- The connection dials only a validated address; TLS then verifies the certificate and
  SNI for the original host, so a DNS change between check and connect cannot redirect
  it. One handler per fetch, no connection reuse across validations.
- No redirect is followed (anything but 200 refuses), and no cookie, credential or proxy
  is used; `Accept-Encoding: identity`, and any `Content-Encoding` refuses.
- Bodies at most 64 KiB (discovery) or 256 KiB (JWKS, metadata), one byte over refuses;
  content types `application/json` (discovery), `application/json` or
  `application/jwk-set+json` (JWKS), `application/samlmetadata+xml`, `application/xml`
  or `text/xml` (metadata).
- Discovery's `issuer` must be the document URL's prefix; a JWKS with private key
  members refuses; SAML metadata is one `EntityDescriptor` with one SAML 2.0
  `IDPSSODescriptor`, its single HTTP-POST SSO location and one or two signing
  certificates, parsed with DTDs prohibited and no resolver.
- One fetch, its DNS resolution included, is bounded by 3 seconds (a resolver that does
  not answer cannot hold it longer), and the runner's 3-second egress budget bounds the
  whole phase. A timeout, connection failure, 5xx or 429 is `Unavailable`; a TLS
  validation failure and every rule above are `Refused`.

`Federation:Egress:AllowedHosts` is the operator allow-list: exact `host` (port 443) or
`host:port` entries, no wildcard, scheme, path or IP literal, at most 64, empty by
default. A listed host may use the named port and may resolve to a private address
(RFC 1918 IPv4 or unique-local IPv6) besides a global one; nothing else widens.
Loopback, link-local and cloud metadata (169.254.0.0/16, `fe80::/10`, and the AWS
IPv6 metadata range `fd00:ec2::/32` inside unique-local), CGNAT, documentation,
reserved, multicast, unspecified, scoped and IPv4-embedding IPv6 answers still refuse.
An entry admits the host for every tenant. It is composition configuration read once
at startup; tenant input never extends it. Keycloak's own later fetches follow DNS;
only deployment egress policy bounds them (Phase 11).

## Invitation SMTP

| Boundary | Contract |
|---|---|
| Ordinary staging | `IInvitationEmailStager`, called only by declared Identity invitation writers; encrypted provider envelope and typed job share their existing business/audit transaction |
| Typed job port | Identity Application.Contracts `IInvitationEmailOperation : IBackgroundJobExternalOperation<InvitationEmailPlan, InvitationEmailOutcome>`; exactly Execute/Reconcile |
| Job adapter | Identity Infrastructure `InvitationSmtpOperation`; prepare/finalize remain exact ordinary Application requests |
| SMTP transport | Common `IEmailProvider`, implemented in common Infrastructure using MailKit/MimeKit 4.18.2; SDK types/exceptions stay inside the adapter namespace |
| Resilience | `IProviderResilience<IEmailProvider>`, configuration `Resilience:email`; retry disabled, finite timeout/circuit-breaker/bulkhead configuration; actual task/socket cleanup remains owned |
| Capacity | Tenant-wide `identity.invitation_email`, ceiling 4, no new Hard limit; existing external queue/pool/worker counts |
| Custody | Common Infrastructure SQL-only provider envelope/attempt records and the exact provider connector; no Identity aggregate or additional invitation root |

The [database contract](../../standards/05-database.md#invitation-smtp-provider-storage)
is the sole owner of encryption/key-ring format, provider tables and grants, the
two SECURITY DEFINER functions, receipt authority, connector bounds and uncertain
COMMIT handling. The [infrastructure contract](../../standards/20-infrastructure-stack.md#bounded-invitation-smtp-consumer)
owns composition with the existing job runtime. Neither general Phase 08a
notification orchestration nor a tenant-selectable SMTP provider is introduced.

Create/reissue freezes the actual enabled tenant locale tag in `EntryLocale`.
Email copy selects the Turkish catalogue for a `tr` language subtag and otherwise
the English fallback, following the
[UI/content distinction](../../decisions/0027-frontend-i18n.md). A regional tag
such as `tr-TR` or `en-US` does not require a separate literal `tr` or `en` tenant
locale. The frozen tag and enabled content locales are not rewritten by copy
fallback. Email links use the fixed `/invitations` path, with the code only in
the fragment; no content-locale prefix selects an invitation route. The message
body also prints the code on its own line, after the clean path, so a recipient
without JavaScript can paste it into the clean form
([ADR-0093](../../decisions/0093-verified-invitation-acceptance-and-handoff.md)).
Only that stored, encrypted message copy and the recipient's own mailbox hold it.

## Deployment configuration

- One operator-configured SMTP host/port and sender apply to this deployment.
  Message content and caller input cannot select a host, credentials, sender or
  alternate transport. Validate addresses and reject header injection.
- Production requires authenticated TLS with certificate/hostname validation;
  implicit TLS or required STARTTLS is explicit configuration, without plaintext
  fallback or opportunistic downgrade. Missing or invalid configuration fails
  qualification; a log-only/null sender cannot report success.
- Development uses the existing local Mailpit endpoint. A development plaintext
  connection is an explicit Development-only composition choice; the module never
  branches on DeploymentMode. A reachable local capture service proves local SMTP
  submission, not an external mailbox delivery or production configuration.
- Resolve credentials and the named encryption key ring at startup through
  `ISecretProvider`, following the existing fixed-for-process secret lifecycle.
  Keys/credentials do not enter checked-in settings, DTOs, jobs, logs or audit.
  Retain decryption versions while provider envelopes need them; a lost key is a
  visible custody failure, not permission to generate another token silently.
- Disable SMTP protocol logging and reject provider exception/message/body text
  at the boundary. Expose only closed outcome/status diagnostics. Preserve the
  SDK-exception translation and sensitive-message controls from the common
  provider-adapter standards.

## Delivery and recovery meaning

`queued`, `submission_accepted`, `failed` and `unresolved` describe provider work;
only the Invitation acceptance owner can report an accepted membership offer.
A final SMTP 250 establishes submission acceptance, never inbox delivery.
Definite no-effect temporary failures may use the existing bounded job retry;
permanent rejection is final. Positive pre-send cancellation fences the old
provider claim and remains distinguishable from framework no-ExecuteIntent proof.

Reconcile reproduces the exact durable provider-attempt receipt without sending.
A crash or lost final response before that receipt remains Unknown with its
reservation retained. A stable Message-ID is identification, not deduplication.
An explicit administrator resend creates new generation/token/job and invalidates
the old acceptance token; it cannot release an old unknown reservation or promise
that previously started mail will not arrive. Duplicate or delayed notices never
authorize a second membership grant.

Provider qualification includes final-250-before-runtime-checkpoint restart,
crash-before-provider-receipt quarantine, fenced delayed Execute, exact terminal
receipt replay/conflict refusal, late original-owner evidence, uncertain provider
COMMIT, no hidden retry after possible acceptance, retained actual cleanup and
cross-tenant/org ciphertext/function refusal. Local qualification and production
SMTP configuration remain separate evidence.

## Configuration surface and source bindings

The API resolves `InvitationEmail:KeyRing` through `ISecretProvider` once at
startup. Its JSON shape is `{ "active": "version", "keys": { "version":
"base64-encoded-32-byte-key" } }`; one to four distinct keys and a declared active
version are required. This is a secret value, never a checked-in example key.
SMTP settings are `InvitationEmail:Smtp:Host`, `Port`, `Sender`, and `Tls`
(`RequiredStartTls` or `ImplicitTls`); `Username` and `Password` resolve through
`ISecretProvider`. `DevelopmentPlaintext` is admitted only by Development host
composition. Missing configuration prevents startup. No connection is made by
configuration validation.

The paired local API launcher selects only these seven email configuration
keys from matching private-file/process sources. Values are compact single-line
strings of at most 8 KiB in UTF-8; SMTP credentials may be empty for explicit
Development plaintext. The API owns key-ring, address, TLS and credential tuple
validation. API file selection uses one no-follow handle and requires a regular,
single-link, current-user-owned source with owner-only permissions, valid UTF-8
and at most 256 KiB. Ordinary web and Seeder child environments receive none of
these settings. CI public-demo construction uses disposable synthetic settings and
does not qualify SMTP delivery; local operator configuration has no generated
fallback key or sender.

For the paired launcher's value-free `Local public configuration or launch
failed` diagnostic, check the selected source's file properties, UTF-8/size and
matching settings first. After verifying that the path is your own regular,
nonsymlink, single-link file, use `chmod 600` on that exact file if permissions
need correction. Check compact key-ring JSON and the SMTP tuple privately;
printing the environment is not a diagnostic step. A source-selection fix does
not establish SMTP connectivity or delivery.

Two active definition keys, `identity.invitation_email_tenant.v1` and
`identity.invitation_email_organization.v1`, bind four exact ordinary roots:
`PrepareTenantInvitationEmailCommand`, `FinalizeTenantInvitationEmailCommand`,
`PrepareOrganizationInvitationEmailCommand`, and
`FinalizeOrganizationInvitationEmailCommand`. Their shared parameters/plan are
three GUIDs (InvitationId, GenerationId, SpoolId); the outcome is only ReceiptId.
The scope-specific roots share logical audit operations, not request authority.

The stager and provider admission verify the actual database logging settings
listed in [Database Standards](../../standards/05-database.md#invitation-smtp-provider-storage).
The invitation creation/reissue owner must qualify privacy before its first
person-field write. A clean provider connection does not retroactively protect an
earlier business INSERT. The staging implementation accepts only those exact
writer identities. Create and Reissue call the real stager/scheduler/envelope
writer on their announced business owner. A failed MUST write rolls their work
back together; no test-only producer exemption supplies production authority.
Admin status uses only the authorized root's current JobId. Missing-job data is
finite Unavailable; SQL failure/private timeout fails the whole audited read with
503 and rollback-only state, while actual caller cancellation remains cancellation.
A page shares a five-second status budget under the parent request deadline.

## Setup, key rotation and restoration runbook

The deployment operator owns these actions through the existing
[secret configuration](../../standards/12-infrastructure.md#secrets-management)
boundary. Never place keys, credentials or captured invitation messages in a
ticket, terminal transcript, repository, test report or deployment log.

1. Generate a cryptographically random 32-byte encryption key in the approved
   secret store. Give it a new version identifier and configure the key ring and
   fixed SMTP settings above. The default configuration provider reads these
   exact configuration keys; environment variables use `__` in place of `:`.
   Development plaintext requires the Development environment and explicit TLS
   mode; production uses authenticated required TLS.
2. Apply the [database privacy settings](../../standards/05-database.md#database-diagnostic-privacy-qualification)
   before enabling invitation writes. Qualify independent collectors separately.
   Startup configuration validation makes no SMTP connection and proves neither
   credentials nor delivery. Use the packet's isolated provider controls before
   enabling the real invitation producer.
3. For rotation, retain every old key required by retained envelopes, add a
   distinct key/version and select it as active. Restart through the normal
   owned shutdown/startup path because the key ring is fixed for the process.
   New envelopes use the new version; old envelopes keep their original version.
   Never reuse a version for different key bytes. Four retained versions are the
   limit, not permission to discard an older required key to make room.
4. Retire a version only after an authorized retention assessment proves no
   retained envelope or required restoration backup needs it. Application
   credentials intentionally cannot enumerate provider custody. Phase 11 owns
   the production retention/purge workflow; expiry or a terminal job alone is
   not key-retirement evidence.
5. Restore missing keys from the protected backup under their original version
   identifiers, then restart. Never substitute a new key or regenerate the
   encrypted link. An unclaimed job can continue through normal admission;
   already-Sending/Unknown work remains uncertain until authoritative recovery.
   Restoring a key is not proof that SMTP never accepted an earlier attempt.
   [P03-9](../../roadmap/phase-03-identity-admin.md) owns privileged unresolved-work
   recovery. Explicit reissue neither releases that reservation nor prevents
   already-prepared old mail from arriving.
