# ADR-0098: Session-Targeted Back-Channel Logout

## Status

Proposed — 2026-11-12. G11 remains Open; no runtime or provider configuration
change is authorized until maintainer acceptance.

**Date:** 2026-11-12
**Deciders:** @cemil (approval pending)

On acceptance this extends [ADR-0070](0070-owned-tenant-bff-sessions.md)'s native
session owner and [ADR-0054](0054-bounded-public-renderer-admission.md)'s closed
method admission with one machine-purpose route. It narrows the browser-only
Origin/host-bootstrap reading for that route, not ordinary browser operations.
[ADR-0078](0078-native-session-scope-and-self-entry.md)'s independent application
scope, [ADR-0071](0071-uncertain-account-site-refusals.md)'s ordinary-read outcomes
and [ADR-0084](0084-owned-frontend-runtime-process-families.md)'s actual runtime
ownership remain. Dated Amendments preserve Accepted bodies and metadata.

The [P03-7 package](../roadmap/phase-03-delivery-log.md#p03-7-proposed-back-channel-logout-package-2026-11-12)
owns three implementation steps and their proof. Proposal review is not executed
provider, native, browser or hosted qualification.

## Decision Drivers

- A provider-ended SSO session can have several independent application scopes
  and browser owners. Its notification must close exactly those owners.
- A callback awaiting token redemption has no known provider identity yet.
  Closing only already-published sessions permits late resurrection.
- Existing native ownership is volatile and single-process. Restart signs users
  out; neither a new durable session ledger nor distributed continuity is needed.
- Authentication of machine notification is separate from browser Origin/CSRF
  and from the retained provider-event mirror delivered in P03-6.
- Finite replay, verification and race-fence storage must refuse saturation
  without evicting live sessions or pretending cancellation completed work.
- A provider container must really reach the native handler over trusted TLS.
  A signed-token unit test or browser-to-provider connection proves neither.

## Considered Options

1. **Signed, session-targeted native ingress** (chosen). Add one fixed-purpose
   provider adapter and extend the existing owner; preserve unrelated SSO sessions.
2. **Subject-wide notification** (not selected). Simpler lookup, but an ordinary
   logout could close independent devices sharing a subject. The selected client
   registration requires provider session identity instead.
3. **Infer revocation from P03-6 history** (rejected). Retained pull has capture,
   delay and attribution limits; an audit fact is not a logout credential.
4. **Refresh/expiry only** (existing fallback, insufficient alone). Works during
   notification outage but cannot provide the requested prompt local invalidation.
5. **Durable/distributed sessions or a new broker** (not selected). Adds credential
   persistence and cross-process coordination without a current deployment need.
   Phase 11 retains the existing distributed-store trigger.

## Decision

LearnStack accepts a separately validated, signed OIDC Logout Token at one native
machine POST route and revokes owners matching its exact issuer/provider-session
identity across hosts. The existing session owner atomically retains replay and
late-publication fences before acknowledging the notification. A callback-only TLS
bridge in the existing native runtime child supplies container reachability without
widening the ordinary native listener or opening a generic proxy. Browser sign-out,
application scope, actual-work retirement and existing refresh/expiry fallback
remain independent.

## Context

### Verified baseline and provider

At `778161b6`, `provider-core.ts` retains issuer/sub but no `sid`;
`session-store.ts` has subject capacity accounting, not a remote-revocation index.
Code redemption precedes identity discovery. The native listener binds loopback;
no back-channel route or client registration currently exists.

The [OIDC specification](https://openid.net/specs/openid-connect-backchannel-1_0.html)
requires separate logout-purpose validation, including `exp` in errata set 1.
Pinned [Keycloak 26.6.2 token generation](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/jose/jws/DefaultTokenManager.java)
sets a two-minute expiration and supplies `sid` when session-required is true.
Its logout category uses the ID-token signing algorithm. These are source findings,
not a newly executed provider experiment.

### Fixed token profile

- Use the existing exact `learnstack` issuer and confidential `learnstack-web`
  client. Register back-channel session-required true, front-channel logout false
  and offline-token revocation false; retain RS256 and no ID-token encryption.
  No offline access, new provider administrator or new realm is introduced.
- Initial and refreshed signed ID tokens require a nonempty bounded opaque `sid`.
  Refresh preserves it exactly, alongside current issuer/sub continuity. Missing
  or changed sid refuses publication; no fallback to subject-wide targeting.
- Pin the already resolved MIT `jose` 6.2.13 as a direct web dependency for this
  adapter. Cryptographic verification remains library-owned; openid-client's
  grant, nonce and PKCE paths remain. Prove one resolved copy of each dependency.
- Require compact signed JWS, `alg=RS256`, `typ=logout+jwt`, a bounded nonempty
  `kid`, and signature verification against the fixed issuer JWKS. Reject JWE,
  unsigned/HMAC algorithms, token-selected key URLs/keys and unsupported critical
  headers. Bounded JSON parsing rejects duplicate member names before use.
- Require exact issuer and audience `learnstack-web` (string or singleton array),
  safe integer `iat`/`exp`, nonempty `jti` and `sid`; optional `sub` must satisfy
  the existing exact opaque-subject grammar. Sid/jti are at most 255 UTF-8 bytes,
  without controls, trimming or case folding. Subject-only tokens refuse.
- Require `0 < exp - iat <= 300` seconds, age at most 300 seconds and future
  issuance at most 30 seconds; expiration admits at most 30 seconds clock skew.
  Pinned Keycloak's ordinary two-minute token fits this selected ceiling.
  Absent, malformed or overlong times refuse independently of signature.
- Require `events` to contain the back-channel logout URI with an empty object;
  prohibit `nonce` even if null. Ignore otherwise unknown claims/form values;
  they confer no additional action. ID/access tokens never substitute.
- Discovery/JWKS retain the fixed endpoint, no redirects, 256 KiB decoded response
  and existing ten-second provider-operation deadline. Cache bounded public key
  material and single-flight retrieval; unknown-key refresh cannot create an
  unbounded request queue or detached fetch. No provider call occurs at startup
  or ordinary anonymous rendering.

These are a qualified Keycloak profile, not support for every optional OIDC mode.
Provider upgrades re-run exact claims, signature, sid and delivery qualification.

### Exact machine route and reachability

`POST /api/auth/backchannel/keycloak` is native-owned, before Next dispatch.
It accepts only the privately configured callback authority and exact path, with
no query/fragment/alias. Disabled BFF or absent explicit back-channel activation
masks it as 404. Native admission captures actual socket provenance; supplied
forwarding/provenance/tenant headers grant nothing.

This route deliberately has no browser Origin/CSRF, cookie or live tenant-host
bootstrap requirement. Its authority is the validated logout credential for the
fixed issuer/client; it cannot choose tenant context or call business APIs.
An archived tenant or unavailable API must not prevent another host's revocation.
Ordinary auth/forms keep their existing host/Origin/CSRF and bootstrap guards.
Neither the route nor its configured bridge accepts browser bearer authority,
sets cookies, redirects, reflects inputs or forwards arbitrary requests.

The form body is URL-encoded UTF-8, at most 16 KiB including ignored values, with
exactly one nonempty `logout_token`. Reject malformed encoding and duplicate token
fields. Input and provider stages use the existing ten-second signal settings;
neither setting proves physical completion. Empty no-store 200 acknowledges a
valid committed local revoke, including an unknown/already-ended target. Invalid
input/token returns empty no-store 400. Admission saturation or unavailable key
verification returns bounded no-store 503, with no partial revoke or success ACK.

Explicit opt-in requires a fixed operator-owned HTTPS callback URL and agreeing
provider registration. `LEARNSTACK_BFF_BACKCHANNEL_ENABLED` absent/false retains
the existing fallback; true additionally requires BFF enabled and a valid
`LEARNSTACK_BFF_BACKCHANNEL_URL`. The URL is configuration, never request input.
Normal local setup includes a callback-only TLS bridge with its explicit certificate,
private key, listen address/port and fixed loopback native destination. It accepts
only this method/path, bounds input/concurrency, preserves token bytes, and uses
verified TLS plus a fixed callback Host when forwarding. No caller selects its
destination, Host, key or protocol. The existing web listener stays loopback.

The bridge's fixed forwarded Host is the configured external callback authority,
not a selected tenant host. Native machine admission recognizes it only on this
exact route; ordinary paths still enforce their existing host/port rules. The
outbound TLS destination is the fixed loopback listener with independently fixed
native certificate name/SNI and trust. Host routing never disables TLS hostname
verification. Normal Docker setup uses a bridge bind reachable from its private
container network and explicit `host.docker.internal` host-gateway mapping where
needed; callback certificate SAN and mounted public-CA trust match that hostname.
No Docker DNS alias alone is considered reachability evidence.

The shared development preparation owns only the named client attributes
`backchannel.logout.url`, `backchannel.logout.session.required` and
`backchannel.logout.revoke.offline.tokens`, plus the top-level
`frontchannelLogout=false` field and required container host mapping/public-CA
trust. Conflicting callback configuration refuses before mutation unless explicitly
adopted through the operator setup; disabled mode grants no silent retargeting.
Preserve credentials, provider IDs, unrelated client/realm/event configuration and
`learnstack-hub`. Existing private certificates/trust are not overwritten or
installed into machine trust.

The bridge is a module/listener in the existing private native runtime child,
not a new worker process or independent credential owner. Register its setup,
listener and request ownership before asynchronous creation/bind. ADR-0084's
irreversible startup-stop/parent-channel-loss latch covers every bridge phase:
an early stop prevents later binding, partial resources are closed, and all
already-started setup/listener/request work is joined through actual retirement.
The existing first-request shutdown watchdog reports failure; its expiry grants
no retirement credit or late-start permission. Normal local usage has a runnable
setup and uses the same launcher/demo stop ownership, not just a test relay.
Its externally reachable socket exposes only signed logout admission. Production
TLS edge/proxy provenance and distributed deployment remain Phase 11; a callback
URL alone is not evidence that either has shipped.

### Targets, replay and late-work fences

Index credential-bearing and retiring owners by exact `(issuer, sid)`. If `sub`
is supplied it must also match. Shared SSO across application hosts is one target;
another sid, subject or issuer remains untouched. Return/closing and invitation
credential owners retain their index association until actual retirement.
No browser-supplied scope/actor or provider mirror event can select targets.

The store records a logout fence before scanning/revoking known matches. A
callback with as-yet unknown identity checks retained fences immediately after
redemption, before any credential custody, entry, scope admission or publication.
Selection, refresh, replacement, invitation work and snapshot publication perform
the same owner-liveness checks after awaits. Closing a matching credential owner
also invalidates linked transitions/custody without retiring unrelated owners in
its browser group. Cancellation alone supplies no publication or cleanup credit.

Use separate bounded stores, each with at most 4,096 entries:

- **Replay:** `(issuer, jti)` plus a digest of the verified token and its acceptance
  deadline. A byte-identical valid duplicate returns 200 without repeating state
  effects; the same jti with changed bytes refuses. Retain through `exp + skew`.
- **Logout fences:** exact target plus a native acceptance sequence. Retain until
  at least the later of acceptance plus five minutes and `exp + skew`, mapped to
  the captured monotonic bound, and through physical settlement of every
  identity-unknown owner admitted before that horizon, plus indexed matching
  closing work. Check new callbacks against any retained matching fence too.
  Later independent sign-in has a fresh provider sid and remains possible.

The sequence is process-local ordering, not provider wall-time order. Raw tokens
are not retained. Native indexes reference at most the existing 1,024 owners;
identifier bounds prevent unbounded key sizes. The fence horizon covers ordinary
five-minute pending admission; uncooperative work can extend retention indefinitely
inside the hard entry cap. Do not expire that safety reference merely because its
request or token timed out. Sweeps retire only proven-unneeded entries.

Replay reservation, needed fence reservation and known-owner gate closure form
one synchronous store transition after validation, with no await in between.
If any required slot is unavailable, change nothing and return 503; never evict
live evidence or partially acknowledge. Allow at most eight actual in-flight
notification verifications, including body/provider cleanup, with no waiting
queue. Malformed or unauthenticated input allocates no replay/fence entry.
Clock rollback cannot make a retired token acceptable again: derive retention
from captured UTC/monotonic bounds and fail notification validation closed on
unsafe clock discontinuity. Do not silently shorten retention on a clock change.

A successful response acknowledges closed local publication gates, not physical
completion of all retained operations, provider logout completion, remote retry
durability or browser cookie delivery. Actual I/O and response work remain owned
through retirement and shutdown under ADR-0070/0083/0084.

### User experience, evidence and failure boundary

- The next checked protected navigation/action requires sign-in; public pages
  continue anonymously. Reuse localized accessible recovery and history guards.
  No new administrative sign-out-everywhere button or session-management UI.
- A remote POST cannot clear another browser's cookies or recall displayed data
  and already-sent responses. Old handles remain unusable; fresh unrelated
  sessions must remain usable. Browser tests re-admit restored protected pages.
- Pinned provider delivery is not established as a durable retry queue. Native,
  bridge, network, JWKS or capacity failure can miss prompt notification; current
  refresh/expiry remains the fallback. An arbitrary account-disable edit is not
  promised to emit a notification. Qualify actual provider session logout acts.
- Already-issued API JWTs retain ADR-0064 acceptance. This is local native-session
  revocation, not API token introspection or universal instantaneous disablement.
- Emit closed notification accepted/duplicate/refused/unavailable categories and
  scalar occupancy only. No token, sid, sub, jti, cookie, raw exception or request
  body in logs/traces/browser/test receipts. Local action is not a durable session
  audit row; P03-6's retained provider facts keep their independent semantics.
  Extend Standard 18's existing BFF evidence boundary explicitly.

## Consequences

### Positive

- Provider logout promptly closes matched native gates when delivery succeeds.
- Unknown-identity callbacks and held refresh cannot revive revoked owners.
- Independent devices/scopes and existing public rendering retain their boundaries.

### Negative

- A shared SSO logout can sign out several institution tabs despite independent
  application scope; a distinct provider session is preserved.
- Local operators must configure bridge TLS/reachability and provider trust.
  Public listener widening and disabled certificate checks are not substitutes.
- Verification, replay and fence limits can refuse legitimate notification bursts.
  Unsettled work can retain capacity indefinitely; no successful delivery guarantee.
- Restart loses volatile sessions and replay state; multi-instance continuity is
  unsupported. Already-admitted external work and sent responses are irreversible.

### Neutral

- No database migration, login, SECURITY DEFINER, tenant setter, permission,
  application-audit writer, Hub crossing, new broker or distributed session store.
- Existing BFF budgets, browser/API/runtime deadlines and CI ceilings stay fixed.
  New notification budgets are selected bounds, not measured throughput claims.
- This packet adds no new product screen. It requires existing UI regression proof;
  changed interaction/copy, if required, receives actual accessibility evidence.

## Implementation Notes

1. After acceptance, record G11 and append bounded dated Amendments to ADR-0054
   and ADR-0070; ADR-0078 and ADR-0084 receive navigation to their retained
   scope/race and startup/stop ownership boundaries.
   Reconcile Standards 06/07/10/11/12/18/20/21, Architecture 13/14/25/31, glossary,
   native/web and Keycloak/Compose setup, environment examples and applicable
   local setup/provider/test skills. Keep the existing audit/grant censuses.
2. **Step 1 — protocol and owner.** Pin direct jose; extend `provider-core.ts`,
   `provider-transport.ts`, session contracts/store/holder with sid, native-only
   validation, atomic replay/fence admission and actual-work cleanup. Keep route
   activation closed until Step 2. Add independently reachable signed-token,
   lifecycle, clock, capacity and held-work controls; commit, fresh review round
   one/fixes/commit, then fresh round two/fixes/commit.
3. **Step 2 — native/provider integration.** Wire the exact ingress, native request
   owner and callback-only local bridge; reconcile fresh/warm provider configuration
   with separate readback and zero-write rerun. Extend the real
   `TenantBffSessionTests`, owned Node fixtures and browser/session/history controls.
   Prove real provider initiation through trusted TLS reaches the actual native
   handler, and bridge-disabled negatives fail that same proof. Commit and complete
   both fresh review/fix rounds before Step 3.
4. **Step 3 — qualification and PR.** Complete relevant Release backend, full guarded
   frontend, docs and native/browser/public regressions, then the step's two fresh
   review/fix rounds. Commit current documentation and freeze the reviewed source;
   open the PR to run its complete CI A/B and original-archive reconciliation.
   Retain honest failures and measured setup/proof/cleanup cost. Update PR proof
   after qualification; a material source fix is reviewed and qualified again.
   No agent merge. Prior packets' open evidence remains separately tracked; no
   old receipt qualifies new code.

## Architecture Tests

Extend the existing canonical `Bff_Credentials_And_Projections_Are_Confined`,
`Bff_Session_Refresh_And_Revocation_Have_One_Owner`,
`Bff_Auth_Admission_And_Csrf_Are_Closed` and
`Bff_Host_Cookies_And_History_Are_Isolated` obligations. Preserve existing proof
status and name the extension separately until controls ship; no new rule count
or status promotion follows from approval alone.

- Signed positive; independent wrong purpose/type/algorithm/key/signature, issuer,
  audience, time/exp, events, nonce, sid/sub, duplicate claims and input negatives.
  Every refusal proves zero unrelated revocation and no private diagnostic output.
- Replay/concurrent duplicate/jti collision, replay/fence/verifier saturation,
  clock discontinuity, expiry/actual settlement, stop and zero-eviction controls.
- Same-sid two-host and different-sid same-subject cases, mismatched optional sub,
  unknown target, later fresh sid; all credential phases including invitation.
- Held redemption before identity is known, refresh, API selection, replacement,
  snapshot/response and shutdown work ignoring abort; no late publication, no
  premature credit release, eventual retirement and preserved independent owner.
- Actual native method/authority/provenance/body guards and browser-CSRF dirty
  controls. Production raw trace/body/token canaries include the bridge and new
  purpose; no direct public/SDK credential access.
- Stop and parent-channel loss before/during bridge creation and binding, held
  setup/read/forward work and partially started listeners: no later bind, no
  admission after stop, no false clean shutdown and eventual actual retirement.
- Real pinned provider claims/registration, shared versus independent SSO and
  actual callback reachability; stale configuration, untrusted TLS and unreachable
  bridge negatives. Preserve account/client/event/secret continuity and safe
  provider refusal behavior. Mock ingress alone does not satisfy C12.

## References

- [Phase 03](../roadmap/phase-03-identity-admin.md),
  [Authentication](../architecture/13-identity-and-auth.md#logout),
  [Security 11](../standards/11-security.md#tenant-bff-sessions),
  [Audit 18](../standards/18-audit-coverage.md#operation-types),
  [Catalogue 21](../standards/21-architecture-tests-catalogue.md#p02b-7-tenant-bff-obligations).
- [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0071](0071-uncertain-account-site-refusals.md),
  [ADR-0078](0078-native-session-scope-and-self-entry.md),
  [ADR-0097](0097-retained-provider-events-and-tenant-audit-mirror.md).
- [OIDC Back-Channel Logout](https://openid.net/specs/openid-connect-backchannel-1_0.html),
  [Keycloak 26.6.2 ResourceAdminManager](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/managers/ResourceAdminManager.java),
  [Keycloak AuthenticationManager](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/managers/AuthenticationManager.java),
  [jose 6.2.13](https://github.com/panva/jose/tree/v6.2.13).
