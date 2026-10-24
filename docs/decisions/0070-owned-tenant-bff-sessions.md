# ADR-0070: Owned Tenant BFF Sessions

## Status

Accepted — 2026-10-23, after separate maintainer approval of the revised record
and five-step package. No dependent runtime exists at acceptance.

**Date:** 2026-10-23
**Deciders:** @cemil

This record answers G13's session part for
[Phase 02b](../roadmap/phase-02b-events-auth.md#the-decision-register).
The [revised package](../roadmap/phase-02b-delivery-log.md#p02b-7-revised-decision-package-2026-10-23)
owns its five implementation steps. Earlier draft reviews remain historical
source inspections; the dated acceptance record captures maintainer approval.

## Decision Drivers

- One owner must serialize refresh and prevent expired state, stale cookies or
  late callbacks from restoring a locally revoked session.
- Server custody already requires transaction/session state; encrypting an opaque
  handle adds key management without concealing additional credential data.
- The delivered web topology is one native Node process. Durable identity and
  distributed session storage have not shipped.
- Sign-in capacity needs source/principal limits before the shared global cap;
  otherwise one caller can deny every tenant new sign-ins.
- Provider-owned event attribution is insufficient while provider storage is
  disabled. Local-only diagnostics still cannot be a durable session ledger.
- Anonymous rendering, API-owned host/claim authority and closed HTTP writes
  remain independent of the new session and its provider.

## Considered Options

1. **openid-client, native credential custody and plain opaque cookies**
   (chosen). A maintained RP client sequences OIDC checks and token grants;
   LearnStack owns its bounded browser transactions and session lifecycle.
2. **Direct oauth4webapi with the same server store and cookies** (viable).
   Supplies protocol validation, PKCE/state/nonce generation, client authentication
   and signature verification. This is not custom cryptography. It leaves more
   grant/response/JWKS sequencing to the application than openid-client.
3. **Auth.js with opaque JWT cookies and custom ownership** (rejected here).
   Its provider/framework integration, CSRF and sealed cookies are real benefits.
   This design nevertheless replaces custody, refresh, expiry, projection,
   transport, logging, logout and response ownership. No installed integration
   needs preserving. The v5 beta and maintenance transition add costs without a
   necessary remaining responsibility. Encrypted credential-bearing cookies
   alone would also need replacement/revocation state to uphold this contract.
4. **Better Auth** (viable framework, not selected). It supports stateless and
   memory modes; a database is not mandatory. Stateless account-data cookies
   conflict with exclusive server custody. Its stateful User/Account/session
   machinery and development memory adapter require further adaptation to our
   bounded refresh/revocation owner. An RP-only client avoids that model now.
5. **Durable PostgreSQL or Valkey sessions now** (viable alternative). Offers
   restart/multi-instance continuity, but adds credential persistence, retention
   and distributed ownership. No measured continuity requirement justifies that
   scope for the delivered single-process topology. The store port ships now.
6. **Per-tenant reservations or oldest-session eviction** (not selected).
   Reservations need a tenant inventory/fair-share policy. Source and subject
   caps bound the obvious monopolies without displacing another live session.
   Oldest-session eviction could sign another device out; only an explicitly
   authenticated same-browser replacement retires its own prior local session.
7. **OIDC Back-Channel Logout now** (not selected). Useful for prompt remote
   invalidation, but needs authenticated logout-token ingress, replay handling
   and provider-to-session indexing. Phase 03 owns it with identity disablement/
   sign-out-everywhere, before those features promise immediate browser
   revocation. Until then they are observed on refresh or local expiry.

## Decision

LearnStack uses openid-client for tenant OIDC and one authoritative session
owner in the native web process. Credentials stay server-side; host-only opaque
cookies bind the browser to that owner's state. Response-owning middleware and
routes complete session decisions before rendering, while app-local `auth()`
reads a safe immutable request projection. The default store is single-process,
volatile and bounded; the tenant development realm records provider user events.

### Provider and library boundary

Pin `openid-client` 6.8.8, with lock-resolved `oauth4webapi` 3.8.8 and `jose`
6.2.13, all MIT. Prove one resolved copy of each dependency. No Auth.js or Better
Auth runtime is installed by this choice. Provider protocol and cryptographic
algorithms remain library-owned; no password authentication or JWT crypto is
implemented by LearnStack.

Use confidential `learnstack-web` in `learnstack`, S256, state and nonce, with
P02b-6's exact HTTPS callbacks and same-host root post-logout registrations.
Require ID tokens, explicit issuer/client/lifetime/nonce validation and
`enableNonRepudiationChecks` for library signature verification on code and
refresh ID tokens. Do not assume token-endpoint TLS processing verifies a JWS.
Use `authorizationCodeGrant`, `refreshTokenGrant`, `buildEndSessionUrl` and the
bounded `customFetch` seam. Configure no DPoP or automatic grant retry.

The API remains the access-token authority. Before publishing a new session or
refresh revision, use that exact access token for the closed AccountSite read
and match the validated ID token's protected `user_id`/`tenant_id` to its actor/
tenant. Any present `organization_id` must match the API-resolved organization;
absence does not have to equal a host-resolved organization. Accept resolved
scope only from that successful API read under the independently verified host;
the BFF supplies no missing membership/organization authority. Preserve original
protected claims, including absence, and the original resolved scope on refresh.
AccountSite has no issuer/subject fields; `sub` is not an actor UUID. Validate
ID-token issuer/subject independently and preserve that original pair on refresh.
The provider's token response associates the two tokens; AccountSite witnesses
access-token actor/scope, not its subject. An ID-token decode is not access-token
validation. Self-registration alone cannot create the required actor profile.

All discovery, token and JWKS traffic uses private issuer configuration, fixed
endpoint purposes, no redirects and one operation-wide finite deadline through
body consumption. Reject unexpected origins, malformed/oversized responses and
profile conflicts. No cookie, claim URL, forwarded authority or error text
selects an endpoint. Loopback HTTP is development-only. Startup and ordinary
public requests without session cookies contact no provider; approved sign-in/
callback transactions are explicit exceptions. Emit closed diagnostics only,
never library exceptions, token responses or raw request/redirect URLs.

### Activation and custody

`LEARNSTACK_BFF_ENABLED` absent/false selects public-only operation: no BFF secret
projection/provider call; auth routes are masked 404 and Studio/Portal return a
bounded 503 sign-in-unavailable state without protected content. Explicit true
requires an agreeing private issuer and existing web-client secret. The latter
already serves realm tooling and alone is not partial BFF configuration.
There is no new session-encryption key. Existing private files are not silently
repaired. Neither mode contacts Keycloak at startup.

`BffSessionStore` is the app-local port; `InMemoryBffSessionStore` is installed
once by the native launcher, separately from ADR-0055's public request holder.
Middleware, routes and RSC facades share it across bundles/HMR. It owns credentials,
validated identity, transaction state, epochs and in-flight work. Browser-group
indexes exist only while their owner entries exist and cannot form an unbounded
second registry. Stop closes admission/publication, cancels owned provider I/O,
joins actual operations and stops maintenance before claiming clean shutdown.

The session cookie is one random 256-bit opaque value. A separate random browser
binding groups that host's login transactions and local session. Neither is a
tenant selector or permission. Both use `__Host-`, `HttpOnly`, `Secure`,
`SameSite=Lax`, `Path=/`, and no `Domain`. Server lookup checks the independently
verified live host, browser binding, current epoch, active state and expiry.
No credentials, identity/profile or version metadata are stored in these cookies.
There is no chunked session format: reject duplicate/malformed names/values and
oversized cookie families instead of reconstructing arbitrary chunks.

Session JSON/RSC/client data explicitly projects only the required state,
validated nonsensitive display fields, effective expiry and session-bound CSRF
value. It exposes no opaque handle, binding, provider token or raw profile.

Support exactly one native web process. Restart, deployment or failover signs
its sessions out; sticky routing is not distributed continuity. A distributed
adapter behind `BffSessionStore` belongs to Phase 11 before multiple web
processes must serve one session, with atomic refresh/revocation/interruption
proofs. No new backend schema, database grant or Hub surface is introduced.

### Admission and reclamation

- Retain at most 1,024 owner entries, including closing/in-flight owners; at most
  128 may be pending sign-ins with a five-minute lifetime. Expiry never frees a
  slot before its owned work actually settles.
- Allow at most 16 concurrent pending sign-ins per canonical verified visitor IP,
  across all hosts. Source-cap refusal is 429 with a bounded retry hint; global
  capacity refusal is 503. Neither creates a reservation or begins provider I/O.
  Sixteen allows concurrent NAT users, but one source cannot fill all 128 slots.
  Eight distributed sources can still do so; this is not DDoS or tenant fairness.
- Retain at most eight established/closing sessions **including logout-return
  transactions** per validated `(issuer, sub)` across hosts. Additional
  independent sessions are refused after identity validation, before publication,
  with a closed 429. Do not evict another device or perform provider logout as
  capacity cleanup. A ninth grant may already have
  been issued; no remote rollback or cancellation of sibling SSO is promised.
- A successful login replaces only its bound browser group's current local
  session. Failed validation leaves it intact. Close its publication gate and
  settle owned old I/O before transferring its subject credit/publishing the new
  session. Retain the pending entry/credit meanwhile. No other host/device is
  retired by a subject lookup. Global/pending admission is still required before
  authentication; replacement is not a capacity bypass.
- A logout converts its existing entry into a five-minute return transaction,
  consuming no sign-in slot. Once its redirect is built, discard provider tokens;
  retain only the host/browser-bound return proof, minimal subject-accounting key
  and necessary in-flight owner. Keep its subject credit until return consumption
  or expiry **and** actual operation settlement. Repeated login/logout without
  following the provider return cannot bypass the eight-owner subject limit.
- Sweep all at most 1,024 entries every 30 seconds and run one complete expired-
  state pass before refusing global capacity. This is a finite scalar-state scan,
  with no provider/database I/O. Do not leave settled expiry waiting eight minutes
  for rotation; do not claim microsecond latency or measured throughput.

These are selected initial budgets, not a throughput entitlement. A maximum
32 KiB serialized credential bundle per owner bounds retained serialized
credentials to 32 MiB; JS/request/protocol overhead remains additional. Subject
caps permit, for example, four device/browser profiles on two hosts; someone
controlling an account can exhaust that account's allowance. Registration with
no valid protected actor does not spend an established-session slot.

### Browser transactions and response races

Use library-generated independent state, nonce and verifier in server transaction
records. A per-transaction `__Host-` cookie uses the state identifier in its name
and an independent random secret as its value; the record also binds the host's
separate browser-group cookie. Bound transaction count by source/global
admission and the whole incoming auth-cookie family. A callback requires exact
state and that transaction's browser/host binding; consume it atomically before
code exchange.
Missing/mismatched state leaves every existing cookie and unrelated transaction
untouched. A verified failed transaction may clear only its own cookie.

A browser group has an owner epoch. The first successfully validated callback
publishes its new session and invalidates competing transactions from the prior
epoch; their later callbacks cannot publish or clear the session cookie. Logout
closes the current session and every earlier pending transaction in that browser
group before provider work. New explicit sign-in after logout uses a fresh epoch.
Check the epoch again after every await and before publication. Retain in-flight
transactions in capacity/index ownership until actual settlement.

No server can retract Set-Cookie already sent. A delayed older response may
replace/clear the newest browser cookie and require reauthentication. Owner
epoch/revocation makes the old handle unusable; do not promise uninterrupted
newest-cookie availability or treat a browser response as server authority.
No failed callback deletes a primary session cookie.

### Effective lifetime, activity and refresh

The BFF idle limit is 30 minutes and absolute limit eight hours, using monotonic
elapsed time. Effective expiry is the minimum of last qualifying activity plus
idle limit, original absolute deadline and the current provider refresh deadline.
Derive the latter conservatively from the validated response's finite positive
`refresh_expires_in` and the request's measured start/elapsed time; refuse an
unknown/unbounded value in this development profile. Do not confuse it with the
900-second access/ID expiry or extend it on a local timestamp alone.

Qualifying document GET navigation, non-prefetch RSC navigation (`RSC: 1`) and
an authenticated API read update idle state in their response owner after
successful admission. Assets, HEAD, prefetch and passive session/status polls do
not refresh or touch idle time. These are protocol classifications, not proof
of physical user activity. App-local `auth()` performs none of those writes.

On a qualifying request, refresh once if access is expired **or** at most 60
seconds from expiry, while the effective session deadline is still live.
An expired access token alone never forces re-login. Concurrent callers join one
owner for that revision; cancelling a waiter does not cancel it. Recheck epoch,
revocation and all deadlines after I/O and publish one revision atomically.
Preserve an existing refresh token only when a valid response omits replacement.

Show a two-minute warning against effective expiry. A CSRF-protected extension
must join/start a refresh even if access is still fresh, so Keycloak idle and
credentials advance with BFF idle atomically. Return the actual resulting
expiry; the original absolute deadline cannot move, and an absolute/provider
maximum must not be presented as extendable. Prove this with the real provider.

`invalid_grant`, malformed/invalid responses, timeout, network uncertainty and
other refresh failures revoke locally with distinct closed reasons; never replay
a possibly consumed grant automatically. Public rendering continues anonymously
with 200; protected pages require sign-in. Provider faults can therefore demand
reauthentication. Idle updates, refresh, invalidation and cookie changes belong
to response-owning middleware/routes before streaming. RSC consumes only the
immutable safe projection. A logical revision is server state, not cookie count.

### Closed HTTP, CSRF and logout

Retain GET/HEAD admission elsewhere and production upgrade closure. Admit POST
only to exact Keycloak sign-in, local sign-out and session-extension routes.
No generic proxy, arbitrary provider/action, Server Action or application write
is opened. The package owns the exact route/field matrix and bounded input.
Unknown hosts refuse before cookie allocation, discovery or token exchange.
Only the verified native host/HTTPS port builds return/callback URLs; trust no
`AUTH_URL`, forwarding header or submitted absolute `callbackUrl`.

- Pre-session sign-in POST: exact Origin plus a random host-only double-submit
  CSRF proof. Its cookie is HttpOnly; the same-origin form receives the matching
  token. It grants no session authority. Apply pending-source admission before
  reservation/provider work; a CSRF token is not a quota identity.
- Provider GET callback: cross-origin navigation is intentional; require the
  state/nonce/S256 transaction and browser binding instead of an Origin match.
- Authenticated sign-out/extension POST: exact Origin and a constant-time checked
  random token bound to the current server session. SameSite does not separate
  sibling tenant origins. Cookie presence alone never proves CSRF.

Logout revokes locally and closes the browser epoch first, then clears the
current session/sign-in cookies and performs RP-initiated end-session with the
stored ID-token hint and registered same-host root return. Issue a separate
per-transaction `__Host-` logout-proof cookie with the same security attributes,
bound to that validated browser group and single-use returned state. Its return
clears only that proof, never a newer session or browser binding. The
provider-directed ID-token-hint
Location is the narrow token-in-URL exception: use no-referrer and never retain
that raw URL.
Access/refresh credentials enter no Location, body, Flight, session JSON or
script-readable cookie. Return-state correlation is not an independent proof
that every provider session was terminated; real-provider tests check logout.

Local revocation is independent of Keycloak availability. Before navigation,
failures can render local recovery. After leaving LearnStack the browser may
remain on a provider/error page; automatic return is not guaranteed. Revisiting
LearnStack remains locally signed out. Shared Keycloak SSO can cause another
host's later refresh to fail; independent provider sessions are not intentionally
revoked. Already issued API JWTs retain ADR-0064's expiry. No immediate remote
admin-disable/sign-out-everywhere guarantee exists before Phase 03's backchannel.

Authenticated responses are no-store. Real browser history/page-restoration
proof must revalidate before protected display; headers alone establish no
browser behavior.

### Reads and durable event ownership

The authenticated server SDK remains exact AccountSite with the owner's bearer
and trusted host/visitor hop. No raw token/host/header option enters UI or pure
SDK consumers. Public wrappers remain credential-free. Studio/Portal initially
display only that nonsensitive projection, with no role-based administration,
new permission, membership or backend endpoint.

Enable Keycloak user-event storage in the **tenant development realm**, fresh
and warm, for LOGIN, LOGIN_ERROR, LOGOUT, LOGOUT_ERROR, CODE_TO_TOKEN,
CODE_TO_TOKEN_ERROR, REFRESH_TOKEN and REFRESH_TOKEN_ERROR. Add them to the
existing effective selection, retaining default-saved and custom prior events,
listeners, admin-event policy and unrelated realm fields. Refresh events are
not saved by default; `eventsEnabled` alone is insufficient.

Fresh/default-empty realms use 604800 seconds (seven days) retention. Adopt that
value on a warm disabled/default-zero store only after a bounded read proves
there are no stored events. Preserve an existing finite retention of at least
seven days; refuse shorter, indefinite or ambiguous customized retention before
any write rather than erase history or silently change its policy. Send explicit
enablement, retention and merged types through the fixed events-config endpoint;
verify separately and require a zero-write completed rerun. Do not replay realm
imports, alter credentials/roles/SSO timeouts or modify `learnstack-hub`.

Seven days is a development review window, not Standards 18's application-audit
retention or a row/disk capacity bound. Provider expiry is asynchronous. Stored
provider events include identity/address details; they remain in the owned
provider database, never raw CI artifacts. Phase 11 owns production provisioning,
retention/security/capacity qualification.

Provider events do not witness local expiry, an unreceived token request or
local logout during provider outage. Those BFF acts have only closed lifecycle
diagnostics until Phase 03's authenticated identity-event ingress/mirror. This
is an explicit narrow Standards 18 ownership exception, **not** AccountSite Off
or permission to skip existing LearnStack MUST events. There is no durable
application session ledger and no new audit-write API. Storage failure is a
provider availability issue; do not claim provider events commit atomically with
a BFF state change.

### Relationship and acceptance carriers

From Standards 07 § Auth:

> Auth.js for session management.

Replace that framework choice with the selected RP library/native session owner.
Reconcile the related Phase 02b Auth.js/chunking illustrations and skill examples;
retain an app-local read-only `auth()` facade. From Security 11 § Authentication:

> Refresh tokens stored as `HttpOnly`, `Secure`, `SameSite=Lax` cookies; never
> accessible to JS.

Replace cookie-held credentials with exclusive server custody and opaque cookies.
ADR-0064 API acceptance, ADR-0067 issuance and Keycloak authentication stay intact.
From ADR-0054 § Native method and upgrade admission:

> Apply GET/HEAD admission to every HTTP path reaching the native listener callback,
> before Next dispatch and independently of middleware matcher exemptions.

Add only the exact auth POST set. From ADR-0053 § Configured caller and rendering:

> No Authorization, cookies, tenant/organization assertion or locale header is
> forwarded.

Keep that public caller unchanged; add the separately confined AccountSite seam.
ADR-0055's request holder never becomes the session store. ADR-0004's prohibition
on .NET session storage is unchanged. The new development event fields extend
P02b-6's owned reconciliation allowance; all other preservation limits remain.

Acceptance appends dated disclosures to ADR-0035/0053/0054 and the Accepted
P02b-6 package. ADR-0035's additive inventory amendment names `BffSessionStore`,
`InMemoryBffSessionStore`, Phase 11 and its multi-web-process trigger; it adds no
exception to the four-part rule. Do not rewrite Accepted bodies/metadata.
Reconcile Standards 06/07/10/11/18/20/21; Architecture 13/14/**25/31**; Phase 03/11;
G13 and the phase session plan; glossary BFF/session terms; realm/setup guidance
and both route/feature-gate skills. Architecture 25's auto-scaled SaaS topology
is a target requiring the distributed adapter, not current session support.
Acceptance establishes authority, not runtime qualification.

## Context

At `74740158`, no BFF/Auth.js runtime exists. Next is 15.5.27 with React 19;
native ingress rejects POST, middleware strips Cookie/Origin and the SDK has
AccountSite types without authenticated transport. The realm lacks event-store
configuration and the reconciler owns only realm access-token lifespan.
P02b-6's callbacks and real-token proofs do not establish browser sessions.

The initial draft selected Auth.js; maintainer review prompted a fresh official
source comparison. The September 2025 Better Auth and Auth.js announcements
promise security/urgent maintenance and recommend Better Auth for new projects.
Auth.js's security policy describes current-latest support, while Next v5 remains
a beta channel. This is not a claim of abandonment or proof that beta.32 lacks
patches. Its framework conveniences are unnecessary for this selected custody
model. openid-client is an actively maintained RP layer over oauth4webapi;
its APIs still require explicit signature checks and our owned session protocol.

Source inspection supports feasibility, not runtime qualification. Reopen for
required restart/multi-process continuity, measured admission/fairness/retention
or refresh costs, a failed library/native seam, or changed provider semantics.
Do not silently increase budgets, add persistence or waive a missing proof.

## Consequences

### Positive

- One authority fences refresh, replay, expiry and local revocation without
  credential-bearing/encrypted session cookies or a parallel framework model.
- Source/subject limits prevent the simplest single-caller capacity monopoly;
  full finite expiry scans reclaim settled capacity before refusal.
- Warm and fresh development providers retain observable authentication events;
  anonymous rendering and API host/claim authority remain independent.

### Negative

- Restart/failover signs sessions out; multiple web processes are unsupported.
  Memory compromise exposes credentials; JS strings offer no secure erasure.
- NAT users share 16 pending slots, a subject shares eight session/logout-return
  owners across hosts, and distributed sources/accounts can still fill global
  capacity. No admission throughput, distributed fairness or live-session
  eviction is promised.
- Late cookie responses and transient refresh failure can require sign-in again.
  Logout may leave the browser at the provider; shared SSO couples later refresh.
- Provider event storage adds personal-data/volume cost and a seven-day retention
  policy. Customized warm retention can require explicit operator resolution.
  No durable local BFF ledger, atomic audit or prompt remote-disable guarantee
  is provided; ID-token logout hints need browser/URL containment.
- LearnStack owns browser CSRF/binding, transaction consumption, expiry and
  shutdown around the RP client. Correctness needs real negative/race controls.

### Neutral

- No business identity schema, grants, Hub contract, membership or permission.
  Only named development event configuration changes; production provisioning
  and the full real-browser OIDC journey retain their named phases.

## Implementation Notes

The revised package owns five steps, exact route grammar and proof placement.
It registers four new obligations at acceptance, with actual test owners and
`Registered` status until controls ship. No catalogue rule is claimed Implemented
by proposal acceptance. Existing issuance/reconciliation obligations gain the
provider-event preservation/retention controls with their first implementation.

| Obligation | Level and owner |
|---|---|
| `Bff_Credentials_And_Projections_Are_Confined` | Frontend structural/production canary: `src/test/bff-boundaries.test.ts`, existing public fences |
| `Bff_Session_Refresh_And_Revocation_Have_One_Owner` | Unit/actual routes: `src/server/auth/session-store.test.ts`, `src/test/bff-session-routes.test.ts` |
| `Bff_Auth_Admission_And_Csrf_Are_Closed` | Native/route behavior: `src/test/bff-auth-admission.test.ts`, `src/test/bff-session-routes.test.ts` |
| `Bff_Host_Cookies_And_History_Are_Isolated` | Native HTTPS browser: `scripts/verify-bff-session.mjs`; real warm BFF/provider chain: backend Integration `TenancyIdentity/TenantBffSessionTests.cs` |

Require reachable positives and planted negatives for signature/state/nonce/
verifier/issuer/profile validation, raw diagnostics, singleton duplication,
input/CSRF bypass, pending-source/subject capacity, replacement, browser epochs,
late responses, expiry/on-demand refresh/extension, full reclaim and shutdown.
Include repeated sign-in/logout without provider return, subject credit held
through unsettled work, changed refresh subject and independent actor/scope
mismatch. A late logout return cannot clear a newer session's cookies.
Test independent per-transaction cookie deletion and fixed small session-cookie
rejection; no artificial chunking proof substitutes for this format.
Warm real BFF login/AccountSite/logout/event evidence and both-host real browser
storage/send/history proof remain mandatory, alongside public call counts and
stopped-Keycloak rendering. CI retains complete discovery, no skips and existing
whole-job ceilings; new fixture cost requires measured qualification.

## Amendments

### Amendment 1 — Delivered proof owners and read-failure clarification (2026-10-24)

The delivered `verify-bff-session.mjs` owns the real-provider HTTP chain, while
`verify-bff-browser.mjs` owns the native HTTPS browser proof. The original table
records the planned assignment before runtime existed; this dated clarification
names the delivered owners without changing the required proof. The original
row and this record's decision, metadata and rationale remain intact. This is
not an ADR-0041 claim that nonexistent runtime contradicted the accepted plan.

The existing refresh uncertainty rule applies after a grant may have been
consumed. An ordinary AccountSite read consumes no grant: temporary availability
failure denies that response without revoking a still-live session or advancing
its idle deadline. A proven identity refusal or actor/scope conflict still
revokes. Public responses remain anonymous; protected reads return unavailable
until authority can be checked. A failed AccountSite validation after refresh
still revokes, because its old grant cannot safely be replayed. This clarifies
the existing read/refresh distinction; it does not relax grant publication.

Source admission counts canonical verified IP addresses, not people, networks
or IPv6 prefixes. One client with several addresses can consume several source
allowances; a reverse proxy's shared TCP peer can combine unrelated visitors.
The accepted budgets provide no subnet fairness or production proxy support.
Production provisioning and distributed/proxy qualification remain Phase 11's
named responsibilities.

### Amendment 2 — Explicit AccountSite status decision (2026-10-24)

[ADR-0071](0071-uncertain-account-site-refusals.md), Accepted 2026-10-24,
completes the ordinary AccountSite status mapping and ratifies the availability
choice recorded in Amendment 1 as an explicit policy decision. Amendment 1
names no HTTP status table; it does not establish that a bare 401 proves
identity rejection. Its heading/body and this record's original decision,
metadata and rationale remain intact. This is not an ADR-0041 factual erratum.

ADR-0064 deliberately returns the same 401 for invalid and undecidable
credentials. ADR-0071 changes that response's caller category to
`provider_refused`: an ordinary read with no preceding refresh retains a live
owner without touching idle time and withholds checked authenticated content.
Public documents render anonymously; protected checked reads return 503.
Masked 403/404 continue to revoke as access-boundary refusals, including the
accepted possibility of host/hop configuration failure. The status table and
delayed genuine-401 revocation cost live in ADR-0071.

Initial callback publication still requires a positive API witness. Every
failed post-refresh authority check still revokes its current owner because
the new package cannot publish a revision without that witness. Callback and
extension AccountSite 401 consequently return BFF 503/`provider_refused`, rather
than 400/`identity`; the owner result remains refuse/revoke. API response codes,
grant replay rules, cookie/session lifetimes and capacity are unchanged.

## References

- [Phase 02b](../roadmap/phase-02b-events-auth.md), [Security 11](../standards/11-security.md#authentication), [Frontend 07](../standards/07-frontend-architecture.md#auth), [Audit 18](../standards/18-audit-coverage.md#operation-types), [Catalogue 21](../standards/21-architecture-tests-catalogue.md).
- [ADR-0004](0004-authentication-strategy.md), [ADR-0035](0035-demand-gated-infrastructure.md#the-gated-set), [ADR-0036](0036-tenant-resolution-trusted-inputs.md), [ADR-0037](0037-idempotency-key-contract.md), [ADR-0053](0053-trusted-public-server-rendering.md), [ADR-0054](0054-bounded-public-renderer-admission.md), [ADR-0055](0055-public-renderer-bootstrap-failures.md), [ADR-0064](0064-tenant-api-authentication-boundary.md), [ADR-0067](0067-development-tenant-access-token-issuance.md).
- [Architecture 25](../architecture/25-deployment-models.md#supported-today-versus-prepared-seam), [Architecture 31](../architecture/31-audit-subsystem.md), [Phase 03](../roadmap/phase-03-identity-admin.md), [Phase 11](../roadmap/phase-11-production-hardening.md).
- [openid-client 6.8.8](https://github.com/panva/openid-client/tree/v6.8.8), [code grant](https://github.com/panva/openid-client/blob/v6.8.8/docs/functions/authorizationCodeGrant.md), [signature checks](https://github.com/panva/openid-client/blob/v6.8.8/docs/functions/enableNonRepudiationChecks.md), [oauth4webapi 3.8.8](https://github.com/panva/oauth4webapi/tree/v3.8.8).
- [Better Auth announcement](https://better-auth.com/blog/authjs-joins-better-auth), [Auth.js team announcement](https://github.com/nextauthjs/next-auth/discussions/13252), [Auth.js security policy](https://authjs.dev/security), [Better Auth 1.7.12 memory adapter](https://github.com/better-auth/better-auth/blob/v1.7.12/packages/memory-adapter/src/memory-adapter.ts).
- [Keycloak 26.6.2 event types](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/events/EventType.java), [event persistence](https://github.com/keycloak/keycloak/blob/26.6.2/server-spi-private/src/main/java/org/keycloak/events/EventBuilder.java), [realm event update](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/managers/RealmManager.java), [token/refresh timing](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/TokenManager.java).
- [OIDC RP-Initiated Logout](https://openid.net/specs/openid-connect-rpinitiated-1_0.html), [OIDC Back-Channel Logout](https://openid.net/specs/openid-connect-backchannel-1_0.html).


### Amendment 3 — Implementation controls and diagnostic scope (2026-10-24)

The existing fixed native host/HTTPS-port clause is enforced before auth and
protected-shell admission; anonymous public Host normalization remains intact.
The native launcher suppresses raw development query/trace sinks under the
existing diagnostic-privacy rule. Its proof exercises both streams and the
unprotected counter; this creates no telemetry authority to publish credentials.

The existing local lifecycle diagnostics include once-only `expired` when sweep
retires an expired owner and `revoked` after successful local signout. Reasons
remain closed and contain no host, actor, credential or request identifier.
They are local observations; [Phase 11](../roadmap/phase-11-production-hardening.md)
owns production capacity signals, alerting and the operational runbook.

Malformed HTTP input remains 400. Invalid upstream response/configuration and
unexpected server failures return a bounded 503; source/subject admission stays
429, while existing transaction/identity/expired/revoked/invalid-grant refusals
retain 400. Input parsing and server failure are exercised independently in the
[route controls](../../frontend/apps/web/src/test/bff-session-routes.test.ts).
This HTTP presentation does not alter publication, grant replay or revocation.
The earlier Registered-until-shipped wording records acceptance-time status;
the [catalogue](../standards/21-architecture-tests-catalogue.md#p02b-7-tenant-bff-obligations)
owns current control status.
