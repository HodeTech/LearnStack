# ADR-0070: Owned Tenant BFF Sessions

## Status

Proposed — maintainer approval pending; no dependent runtime is implemented.

**Date:** 2026-10-23
**Deciders:** @cemil

This record answers G13's session part for
[P02b-7](../roadmap/phase-02b-events-auth.md#the-decision-register). Its
[decision package](../roadmap/phase-02b-delivery-log.md#p02b-7-decision-package-2026-10-23)
contains the implementation sequence. Acceptance of this proposal, its custody,
availability and audit costs, and that sequence precedes dependent code.

## Decision Drivers

- Concurrent requests must share one refresh result, and logout must prevent a
  late refresh or an older cookie from restoring a session.
- Credential-bearing cookies alone cannot provide immediate local revocation;
  retaining replacement tokens and revocation state already creates a store.
- The delivered web topology is one native Node process. PostgreSQL identity
  persistence and distributed cache/session adapters have not shipped.
- The public sites must still render with Keycloak stopped. Their existing
  caller must remain credential-free and their bootstrap request-local.
- The existing listener refuses POST; authentication needs a closed admission
  extension, not a general write proxy or Server Actions surface.
- Session lifetime, provider issuance and API token acceptance are different
  contracts. None can silently inherit another's lifetime or authority.

## Considered Options

1. **Native-process credential custody and an encrypted opaque cookie**
   (chosen). One bounded owner arbitrates refresh, expiry and revocation;
   restarting that process signs its sessions out.
2. **Encrypted credential-bearing JWT cookies alone** (rejected). Concurrent
   stale cookies can repeat refresh and restore signed-out state. Adding a
   replacement cache and tombstones recreates server-side authority with two
   competing copies of credentials.
3. **Durable PostgreSQL sessions now** (viable alternative). Survives restarts
   and can coordinate instances, but introduces a credential-bearing schema,
   retention, grants and distributed refresh ownership before the first browser
   session. No measured restart-continuity requirement justifies that scope now.
4. **Valkey sessions now** (rejected for this packet). It activates a gated
   dependency. A distributed adapter requires the same ownership proofs; using a
   cache interface alone does not implement atomic refresh or revocation.
5. **One unbounded process-global promise cache** (rejected). It bounds neither
   session retention nor in-flight ownership and cannot define logout/expiry.
6. **Replace Auth.js with a custom OIDC protocol** (rejected). Provider protocol,
   nonce/PKCE checks and cookie cryptography remain library responsibilities.

## Decision

LearnStack uses Auth.js for tenant OIDC and encrypted cookies, with credentials
held exclusively by a bounded, authoritative session owner in the native web
process. A host-bound opaque handle identifies that owner's session. Response
owners complete refresh and cookie changes before rendering; server components
consume a safe request projection. This is a single-process session service,
not durable or distributed identity storage.

### Provider and library boundary

Use confidential `learnstack-web` in the `learnstack` realm, Authorization Code
with S256, state and nonce. Preserve P02b-6's exact per-host HTTPS callback and
same-host root post-logout registrations. No password grant, Hub realm, membership
provisioning, role-derived permission or tenant switching is introduced.

Pin `next-auth` 5.0.0-beta.32 and its exact `@auth/core` 0.41.3 dependency;
direct refresh uses `oauth4webapi` 3.8.8. Their ISC/MIT licences are compatible.
The beta channel is an explicit maintenance cost; a floating tag is not a pin.
Use the supported JWT/account/session callbacks, default JWE cryptography and
provider `customFetch`; do not patch vendor code or implement an ORM adapter
with pretend durable User/Account records. Qualify these seams before activation.

All provider traffic uses private, validated issuer configuration, fixed OIDC
endpoint purposes, bounded decoded bodies and finite deadlines. Refuse redirects,
unexpected endpoint origins, malformed responses and token/profile mismatches.
Loopback HTTP is development-only. Request hosts, JWT URLs, forwarded headers
and provider error text cannot select an endpoint. Startup and a request without
session cookies contact no provider. Override every Auth.js logger method with
closed diagnostics; debug and raw exception/metadata logging stay disabled.

### Custody, topology and resource ownership

- `BffSessionStore` is an app-local server port. `InMemoryBffSessionStore` is
  installed once by the native launcher, separately from ADR-0055's request
  admission holder. Bundled middleware, route handlers and RSC facades resolve
  that exact instance; HMR never constructs another authority.
- The owner holds access, refresh and ID tokens and validated identity. The
  Auth.js encrypted cookie contains only a random 256-bit handle, canonical host
  binding and format metadata. It contains no provider credential or profile.
  Lookup checks the independently verified live host, expiry and active state.
  The API still reconciles host and signed claims; a cookie selects no tenant.
- Session and transaction cookies are `HttpOnly`, `Secure`, `SameSite=Lax`,
  `Path=/`, with no `Domain`. Use `__Host-` names. Explicitly project session JSON
  and RSC/client data: no handle, provider token or raw account/profile crosses
  that boundary. The session-bound CSRF token is deliberately readable.
- Support exactly one native process for this adapter. Restart, deployment,
  failover or a different process loses its sessions. Do not add sticky routing
  or claim multi-instance continuity. Phase 11 owns a distributed session
  adapter before more than one web process must serve one session; that trigger
  requires atomic refresh/revocation and interruption proofs, not `ICacheService`.
- Retain at most 1,024 owner entries, including closing/in-flight entries; at
  most 128 can be pending sign-ins. A pending reservation expires after five
  minutes and is consumed only by its verified login transaction. No live entry
  is evicted. New admission at capacity returns a closed no-store 503 while
  existing sessions continue. Failed/abandoned login releases its reservation;
  replay cannot create another session. Expiry never frees an in-flight owner's
  slot before its work actually settles.
- A logout reuses its existing session entry as a five-minute return transaction;
  it allocates no new entry and does not spend the 128 pending-sign-in allowance.
  That entry grants no authenticated access after local revocation. Logout can
  therefore proceed while sign-in admission is full. Failed/expired returns
  release only their settled entry; an absent session needs no logout entry.
- These are fixed initial single-process budgets, not measured throughput or
  fair-share guarantees. A maximum 32 KiB serialized credential bundle per entry
  bounds retained serialized credentials to 32 MiB; JS objects, protocol buffers
  and request work add overhead. A 64-entry rotating sweep every 30 seconds
  reclaims expired settled entries. Admission may inspect at most that batch;
  it never scans the whole registry or evicts a live entry to make space.
- Stop closes admission and publication before cancelling owned provider I/O.
  Join those operations and stop the sweep before reporting clean shutdown.
  A response timeout or process exit is not a cleanup witness.

### Lifetimes and refresh

Use a 30-minute idle limit and eight-hour absolute limit, measured by the owner
with monotonic elapsed time. Provider refresh expiry, when supplied, can shorten
both. The 900-second development access/ID expiry is not a session lifetime.
The cookie cannot extend registry authority, including library decode tolerance.

Authenticated document navigation, an authenticated API read or an explicit
CSRF-protected extension touches idle time. Assets, prefetch, passive session
status and background warning polls do not. Show an accessible two-minute idle
warning with an extension action; the absolute limit cannot be extended.
Provider expiry can end a session earlier and must not be represented as an
extendable idle timeout.

Within 60 seconds of access expiry, the owner starts at most one refresh for
that session version. Concurrent callers join it; cancelling a waiter does not
cancel the shared owner. Check active state and all expiries again after I/O.
Validate the returned issuer/subject and protected identity against the original
session; refresh cannot reassign an actor, tenant or organization. Publish one
new server revision atomically. Preserve an existing refresh token only when a
valid provider response legitimately omits its replacement.

`invalid_grant` revokes the session. Timeout, network uncertainty, malformed or
invalid tokens and other refresh failures also end the local session with their
own closed diagnostic reason: do not automatically replay a possibly consumed
refresh grant. This deliberately trades reauthentication during provider faults
for a small, fail-closed protocol. A public page continues anonymously with 200;
protected pages redirect to sign-in. Neither path retries login automatically.

Refresh, invalidation and cookie clearing occur in middleware/route handlers
that own a response, before streaming. App-local `auth()` reads the immutable
request projection; it performs no refresh, idle update or cookie write. One
server revision is the logical update, independent of how many ciphertexts or
cookie chunks Auth.js emits. Reuse the library's chunk cleanup for growth,
shrink and logout; do not manufacture large production cookies for a test.

### Exact HTTP and CSRF surface

Native admission keeps GET/HEAD elsewhere and production upgrade closure.
It admits POST only to the named Auth.js Keycloak sign-in, local sign-out and
session-extension handlers. No arbitrary auth action, provider, method, API
proxy target, Server Action or application write becomes reachable.

The decision package enumerates exact GET/POST paths and their owned input
grammar. Bound auth headers/cookie families and form bodies before Next dispatch;
reject ambiguous cookie names/chunks, duplicate security fields and unsupported
content types. No application `Authorization` header is accepted from a browser.
Middleware preserves only the validated auth-specific carriers in that branch;
public API requests keep their existing credential-free header set.

- Before a session exists, sign-in POST requires exact same-origin `Origin`
  plus Auth.js's pre-session CSRF check and the owned login reservation.
- The provider's GET callback intentionally permits cross-origin navigation;
  it instead requires the matching state, nonce, verifier, host and unexpired
  single-use reservation. Failures clear only that transaction's cookies.
- Authenticated sign-out and extension POST require exact same-origin `Origin`
  plus a constant-time checked token bound to the current server session.
  A sibling demo host is cross-origin despite `SameSite=Lax`.

Do not read `AUTH_URL`, `AUTH_TRUST_HOST`, forwarded authority or a submitted
absolute `callbackUrl` as trust. Build the library request/configuration from
verified native provenance after live API host admission. Return destinations
are same-host allowlisted local Studio/Portal/public paths, never arbitrary URLs.
Unknown hosts refuse before cookie allocation, discovery or token exchange.

### Logout and history

Local logout closes the session's publication gate before provider work and
clears every session/transaction-cookie chunk. An older response or late refresh
cannot reopen the absent/revoked entry. Then perform RP-initiated Keycloak
end-session at the validated endpoint with the ID-token hint and exact registered
same-host root return URI. Bind the return to a bounded logout transaction.
An ID-token hint in that provider-directed redirect is the narrow exception to
token-free browser URLs; access/refresh credentials never enter a Location,
HTML, Flight, session JSON or script-readable cookie. Use `no-referrer` and do
not retain the raw logout URL in logs or receipts.

Local invalidation succeeds even if Keycloak is unavailable; do not claim remote
SSO termination without its completion. Failures before navigation render a
bounded local signed-out recovery state. After the browser leaves LearnStack,
an outage can end on a browser/provider error page; automatic return cannot be
guaranteed. Revisiting LearnStack remains locally signed out. No redirect retry
loop is introduced. Provider logout ends
the current Keycloak SSO session: another host sharing it can lose refresh
authority although its cookie is host-only. Independent provider sessions are
not deliberately revoked. Already issued API JWTs retain ADR-0064's expiry.

Authenticated documents/session responses are no-store. On browser history or
page restoration revalidate before displaying protected content; use an actual
browser back-navigation control. No-store alone proves no browser behavior.

### Read and audit boundary

Use a separate server-only authenticated SDK adapter for exact AccountSite,
attaching the registry's bearer to the existing trusted host/visitor hop. No
token/host/header option enters UI or pure SDK consumers. Keep public wrappers
credential-free. Guard Studio and Portal before rendering; the first page
displays only the existing nonsensitive AccountSite projection, not role-based
admin capability. No additional backend endpoint or permission is authorized.

Provider authentication events remain Keycloak-owned. Local BFF establishment,
refresh and invalidation emit closed lifecycle diagnostics, not invented
application audit rows or AccountSite's `Off` registration. This proposal
explicitly scopes Standards 18's generic login/token-revocation example to
provider ownership for this browser protocol; existing LearnStack MUST events
and their durability remain unchanged. No durable LearnStack session trail is
promised. Phase 03's authenticated identity-event ingress/mirror remains its
named owner; this packet adds no unauthenticated audit-write API.

### Relationship and acceptance carriers

From Security 11 § Authentication:

> Refresh tokens stored as `HttpOnly`, `Secure`, `SameSite=Lax` cookies; never
> accessible to JS.

Replace credential custody with server-only custody and the secure opaque
cookie described here. The corresponding Phase 02b custody prose links this
decision. This does not change ADR-0067 issuance or ADR-0064 API acceptance.

From ADR-0054 § Native method and upgrade admission:

> Apply GET/HEAD admission to every HTTP path reaching the native listener callback,
> before Next dispatch and independently of middleware matcher exemptions.

The exact authentication POST set is its bounded extension; all other methods,
public paths, asset/health exemptions and upgrade controls remain unchanged.
From ADR-0053 § Configured caller and rendering:

> No Authorization, cookies, tenant/organization assertion or locale header is forwarded.

That public caller remains unchanged. A separately confined authenticated caller
adds only the owner's bearer for AccountSite; this is an additive seam, not a
replacement of public anonymity. ADR-0055's public request holder never becomes
the session store. ADR-0004's prohibition on .NET session storage is unchanged.

Acceptance appends dated disclosures to ADR-0053/0054 without rewriting their
Accepted bodies or metadata. Reconcile Standards 07/11/18/21, Architecture 13/14,
the phase's G13 and session plan, the glossary and development instructions.
Replace Architecture 14's explicit `Domain=` advice; distinguish shared SSO from
independent provider sessions and implicit session checks from CSRF enforcement.
Phase 03 distinguishes provider grant ownership from BFF credential custody;
Phase 11 receives the named distributed-store trigger. Historical delivery
records remain intact. Proposed text changes no active rule before acceptance.

## Context

At `395d078c`, Next 15.5.27/React 19 and the native HTTPS launcher are installed;
Auth.js is absent. Studio/Portal are unguarded shells. Middleware discards Cookie
and Origin while forwarding public requests, and all matching paths run through
public entry. The SDK has AccountSite types but no authenticated transport.
P02b-6 reserves callbacks and proves real issuance; it does not implement a BFF.

Tagged Auth.js sources show that RSC `auth()` discards generated Set-Cookie,
JWT session reads re-encode cookies, the session callback is client-visible, and
default logging includes provider failures. Its documented refresh recipe has
no built-in serialization. A precise response owner and safe projection are
therefore required, not optional optimizations. Source inspection establishes
feasibility only; pinned-Next runtime proof remains mandatory before activation.

Reopen this choice for required restart continuity, multi-process session
serving, measured capacity/fairness or reauthentication costs, a library seam
that cannot uphold these invariants, or changed provider logout/rotation
semantics. Do not silently add persistence, increase limits or bypass a proof.

## Consequences

### Positive

- One authority fences concurrent refresh, stale cookies and logout races.
- Provider credentials never live in a browser session cookie or public SDK.
- Anonymous rendering retains its independent provider-down behavior.

### Negative

- Deployments/restarts sign users out; concurrent web instances are unsupported
  by this adapter. Memory compromise still exposes credentials; JavaScript
  strings provide no secure-erasure guarantee.
- Global capacity can deny unrelated tenants new sessions/sign-ins. There is no
  tenant reservation, actor fairness or admission-throughput entitlement.
- A transient refresh failure can require sign-in again. Local logout cannot
  guarantee remote SSO logout or browser return during an outage; shared SSO
  couples later refresh.
- There is no durable application session audit trail. An ID-token logout hint
  reaches the trusted provider through the browser, and requires URL redaction.
- The project owns a native singleton, expiry/sweep lifecycle and protocol
  adapter atop a beta library. Future distributed custody needs separate proof.

### Neutral

- No database role, schema/grant, Hub contract, application identity domain,
  membership, permission or production realm change.
- Cookie transport expiry and library chunking do not define session authority.
  Real-provider token tests and stub-provider route/browser proofs stay distinct.

## Implementation Notes

The five-step package owns exact paths, test subjects and commits. First prove
the selected library hooks without activating HTTP authentication. Activation
waits for complete custody, CSRF, refresh, logout and read/guard implementations.
Every step has a commit and two fresh independent review rounds; verified
material corrections receive focused reinspection before proceeding.

Register these canonical obligations at acceptance, with real future owners and
`Registered` status until their controls ship:

| Obligation | Level and owner in `frontend/apps/web` |
|---|---|
| `Bff_Credentials_And_Projections_Are_Confined` | Structural/production canary: `src/test/bff-boundaries.test.ts`, existing public-boundary controls |
| `Bff_Session_Refresh_And_Revocation_Have_One_Owner` | Unit + actual route behavior: `src/server/auth/session-store.test.ts`, `src/test/bff-session-routes.test.ts` |
| `Bff_Auth_Admission_And_Csrf_Are_Closed` | Native/route behavior: `src/test/bff-auth-admission.test.ts`, `src/test/bff-session-routes.test.ts` |
| `Bff_Host_Cookies_And_History_Are_Isolated` | Native HTTPS browser: `scripts/verify-bff-session.mjs`, its wrapper/control tests; real BFF/warm-provider HTTP proof in backend Integration `TenancyIdentity/TenantBffSessionTests.cs` |

Require positive controls and planted violations for custody, raw logger output,
singleton duplication, missing provenance, provider/return URL substitution,
state/verifier/nonce removal, CSRF bypass, expiry, capacity, refresh duplication,
late publication, cookie growth/shrink/clear and history restoration. Preserve
real AccountSite scope proofs, public API-call counts and stopped-Keycloak demo.
The warm-provider proof signs a seed user into the actual BFF, consumes its
session for AccountSite and verifies real provider logout; unrelated backend
token issuance plus a stub BFF exchange cannot substitute for this chain.
Required checks retain complete discovery, no skips and finite whole-job budgets;
measure the added browser/provider costs before claiming hosted qualification.

## References

- [Phase 02b](../roadmap/phase-02b-events-auth.md), [Security 11](../standards/11-security.md#authentication), [Frontend 07](../standards/07-frontend-architecture.md#auth), [Audit 18](../standards/18-audit-coverage.md#operation-types), [Catalogue 21](../standards/21-architecture-tests-catalogue.md).
- [ADR-0004](0004-authentication-strategy.md), [ADR-0036](0036-tenant-resolution-trusted-inputs.md), [ADR-0053](0053-trusted-public-server-rendering.md), [ADR-0054](0054-bounded-public-renderer-admission.md), [ADR-0055](0055-public-renderer-bootstrap-failures.md), [ADR-0064](0064-tenant-api-authentication-boundary.md), [ADR-0067](0067-development-tenant-access-token-issuance.md).
- [Auth.js beta.32 server session and response handling](https://github.com/nextauthjs/next-auth/blob/next-auth%405.0.0-beta.32/packages/next-auth/src/lib/index.ts#L62-L96).
- [Core 0.41.3 session/cookie response owner](https://github.com/nextauthjs/next-auth/blob/%40auth/core%400.41.3/packages/core/src/lib/actions/session.ts#L38-L92), [callback](https://github.com/nextauthjs/next-auth/blob/%40auth/core%400.41.3/packages/core/src/lib/actions/callback/index.ts#L140-L171), [checks](https://github.com/nextauthjs/next-auth/blob/%40auth/core%400.41.3/packages/core/src/lib/actions/callback/oauth/checks.ts), [logger](https://github.com/nextauthjs/next-auth/blob/%40auth/core%400.41.3/packages/core/src/lib/utils/logger.ts).
- [Auth.js refresh limitations](https://authjs.dev/guides/refresh-token-rotation), [OIDC RP-Initiated Logout](https://openid.net/specs/openid-connect-rpinitiated-1_0.html), [Keycloak 26.6.2 logout implementation](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/endpoints/LogoutEndpoint.java).
