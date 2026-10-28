# ADR-0078: Native Session Scope and Self Entry

## Status

Proposed — no gate is accepted and no dependent runtime is implemented.
Joint acceptance with [ADR-0079](0079-bounded-identity-administration.md) and
the revised P03-3 packet is required.

**Date:** 2026-10-28
**Deciders:** @cemil

**Bounded supersession:** This proposal changes the trusted scope-source and
native publication rules named below. It is not an ADR-0041 correction.
Acceptance appends dated Amendments; historical bodies and metadata remain.

## Decision Drivers

- One person needs independent tenant/organization sessions through real refresh;
  user-global provider attributes cannot represent those sessions.
- Application Membership and lifecycle already establish current authority.
  Provider scope selection must not introduce a second permission registry.
- Verified static provider scopes require realm-wide client-management privilege
  and cross-system provisioning for each new organization in the current pin.
- New-user entry needs bounded permanent allocation and truthful completion
  without assuming a Membership or publishing a tenant session.
- Host isolation, native credential custody and committed authority must survive
  switches, cancellation, refresh and late browser responses.

## Considered Options

1. **Authenticated native candidates; live API confirmation** (proposed).
   A separate native-purpose credential distinguishes the server-owned candidate
   from browser assertions. No new token issuer or provider administrator is needed.
2. **Static optional provider scopes** (viable, not selected). Real pinned
   provider/code/refresh experiments establish per-grant independence. Arbitrary
   organization creation additionally requires broad client-management authority,
   configuration convergence and a separately qualified projection lifetime.
3. **Signed application context tickets** (viable, not selected). Still needs
   candidate admission and live Membership checks; adds signing/verification
   keys, a second credential format, token binding, expiry and renewal.
4. **A per-grant Keycloak extension** (viable, not selected). Adds provider-side
   code, deployment and upgrade qualification. Shared user/client-session notes
   alone do not establish per-grant independence.
5. **Shared attributes, untrusted headers or public-hop secret alone** (rejected).
   The first couples sessions; the others do not authenticate a native context
   owner. An existing trusted host hop does not select tenant authority.
6. **Immediate unrestricted registration** (rejected). Per-subject quotas and
   volatile session limits do not bound permanent User and creation-audit growth.

## Decision

LearnStack separates provider identity from application session scope. A confined
native caller proposes its immutable session context over an independently
credentialed trusted hop; the API confirms it against the real host, existing
binding, current Membership and lifecycle before ordinary policy admission.
Provider evidence remains separate and immutable. Exact global-self operations
support deliberate bounded creation and own-context discovery; they grant no
Membership or session. The native owner publishes only after a positive audited
API context admission and matching AccountSite witness.

## Context

### Verified baseline and alternative

At `575d8e32`, the API normalizes existing bindings after verified-subject quota.
Native provider validation requires actor/tenant claims; AccountSite independently
requires HostAndClaim. Scope comes from global user-attribute mappers. These are
real existing rules, not latent support for the proposed source.

The [entry review](../roadmap/phase-03-delivery-log.md#p03-3-entry-review-and-provider-feasibility-2026-10-28)
records the actual provider feasibility experiment and limits. Static scopes
survive same-client/same-SSO held-code and refresh interleavings. Multiple selected
scopes nevertheless overwrite scalar claims, and scope selection establishes no
application Membership. Creation requires type-wide client management, including
access to unrelated client secrets/configuration. A caller-side allowlist cannot
reduce a stolen credential's provider privilege. The proposal selects the
application boundary on total authority/lifecycle cost, not because provider
issuance is impossible. This alternative has not yet received application proof.

### Authenticated native source

The existing network plus host-hop-secret checks remain necessary. Add a distinct
random 256-bit `X-LearnStack-Native-Session-Secret`; the public-hop credential
cannot substitute. Native constructs `Authorization`, captured host/visitor hop
and exactly one canonical lowercase `X-LearnStack-Native-Tenant-Id`, with an
optional canonical `X-LearnStack-Native-Organization-Id`, from one server owner.
Organization absence means explicitly orgless. Empty, duplicate, malformed,
reserved or partial fields refuse; none falls back to provider/host selection.
The exact global-self set below accepts the native credential without scope
headers and rejects supplied scope headers. Scoped operations require tenant.
Browser headers are stripped, never forwarded. Public callers receive no source
secret or scope option; SDK consumers receive no raw bearer/header escape hatch.

The API validates the ordinary bearer (including the existing authorized party),
subject quota and binding before admitting a scoped candidate. It mints a distinct
immutable native-scope feature and `HostAndNativeSessionScope` origin. It never
rewrites VerifiedProviderSubject or claims that Keycloak issued application scope.
Request/credential/host/provenance feature references bind each candidate; stale,
foreign or retargeted evidence refuses. The existing four-slot candidate reader
still supplies fresh committed Membership/lifecycle checks, then releases its
physical owner before business/audit continuation. Ordinary operation/resource
checks remain independent. A source credential alone grants no user, enumeration,
SQL context, Membership, grant or Platform authority.

| Real host | Native candidate rule |
|---|---|
| Tenant host | Tenant matches; a selected organization must belong and have current coverage/lifecycle. Orgless remains orgless. |
| Organization host | Tenant and selected organization match exactly; explicit orgless or another branch refuses. |
| Platform/unknown host | No native scoped session/AccountSite admission; existing unknown-host precedence remains. |
| Provider scope also present | Require exact consistency; disagreement never chooses a winner. |

Legacy provider-claim/direct-bearer behavior remains. Existing `X-Tenant-Id` and
`X-Organization-Id` are still assertions. The new source is a named exception to
ADR-0036's prohibition on header-carried scope selection. AccountSite's authority
ceiling still requires the actual independently resolved host and exact scope;
only its admitted evidence origin expands. No platform-host AccountSite is added.

API secrets use the existing secret-provider boundary. Native holds only its
current sending value. API accepts at most current/previous values, with an
explicit absolute previous-value retirement time and no unlimited overlap.
Configuration rejects equal public/native secrets, placeholders and partial input.
Rotation installs acceptance, changes native sending, drains real old requests,
then retires the previous value. Mismatch/retirement fails closed. Public-only
mode needs no native secret. No secret, token or raw header enters artifacts,
product copy, client projections or logs. Compromise of native memory exposes its
bearers and source credential; this design cannot prove the initiating browser
inside the API. Native CSRF, binding and epoch ownership supply that obligation.

### Global-self operations and creation admission

These are separate exact descriptors after provider validation and quota, before
ordinary binding/tenant resolution. They require the native source, a known
Tenant/Organization host and the verified provider subject. They install no
TenantContext and cannot invoke the provisioning marker or generic MediatR path.
Ordinary requests keep their route-independent lookup and never allocate.

| Operation | Result / authority |
|---|---|
| GET `/api/v1/identity/self` | Own existing/missing binding state, with a server-only association witness when Active; no allocation. |
| POST `/api/v1/identity/self/bootstrap` | Empty JSON object; explicit person-confirmed creation through the common prepared owner, or matching existing binding. |
| GET `/api/v1/identity/self/contexts` | Bounded own-Membership context discovery; no supplied UserId, profile, email, Role graph or global directory. |
| POST `/api/v1/identity/session-scope/admissions` | Ordinary resolved native context; central MUST security event before returning an exact server-only actor/context association witness. |

An association witness contains authority, subject digest and stored actor plus
applicable context. Native computes the canonical digest from the independently
validated ID-token issuer/subject and configured stable authority, then compares.
A present compatibility actor must agree. Access-token decoding is not proof.
API and ID-token identity must name the same subject before credentials publish.
The admission success event uses a separately classified ordinary ReadWrite
request/common AuditLog + Transaction owner, with its Membership resource subject.
It is not a denial-evidence-lane write. Failed success-audit INSERT, constraint
validation or COMMIT prevents a success witness and session publication; unknown
COMMIT still proves neither rollback nor cookie delivery. Test each boundary.

Creation is opt-in deployment configuration; absent/false refuses new allocation
while existing bindings can continue. Enabling it explicitly accepts the configured
provider registration policy. Development keeps its documented open/unverified
provider registration; an unverified account may create only its own bounded
binding, never a Membership. No email mirror or email-based merge is introduced.
Production registration/abuse configuration remains Phase 11 qualification; there
is no production readiness claim or hidden verified-email bypass of P03-4.

The proposed initial durable budgets are **10 creation attempts per UTC minute,
100 per UTC day, and 10,000 retained User bindings per authority**, including
existing/seed bindings in the last bound. They are selected starting budgets,
not measured throughput or fairness. Exact existing bindings consume no creation
attempt. Following an initial missing result, one short app-role admission owner
commits the attempt debit before the prepared creation owner begins. Failed,
cancelled and uncertain attempts do not refund it; a racing winner can make the
subsequent prepared lookup return Existing. This prevents repeated failed MUST
creation attempts from bypassing the new-subject attempt budget.

A nonpersonal, authority-scoped control row stores fixed UTC-window counters and
permanent allocation count. Migration/deployment initializes it from actual
retained Users. A confined owner admission function alone updates attempt counters;
a User-INSERT guard atomically enforces/increments total capacity on the same
transaction as User and central MUST audit. Failure rolls back that increment.
Neither function projects another person or permits app counter reset. Clock
regression cannot reopen a spent window. Fixed seed adoption remains separately
witnessed and consumes total space, not HTTP attempt quota. Counter exhaustion
before mutation declaration creates no failed creation intent. Existing subject,
visitor and peer quotas remain; unrelated authorization-denial audit is unchanged.

Minute/day refusal is 429 with a bounded next-window hint; permanent capacity is
503. One source can consume the deployment's registration allowance with multiple
provider accounts; no tenant reservations or DDoS guarantee is claimed. Restart
cannot reset counters. Raising/resetting the selected policy needs explicit
reviewed migration/decision, not a browser input or silent config fallback.

### Discovery and pre-session ownership

Own-context discovery has a separately confined global-self descriptor and fixed
function deriving actor from the announced verified subject, not a supplied UUID.
It returns only that Active actor's current Membership scope keys, in bounded
cursor pages. Tenancy supplies names/destinations through its application contract,
without cross-module joins or Organization FK. Any derived tenant read announces
only a captured own-Membership scope through a separately enumerated, short
physically READ ONLY owner on the ordinary business source; no synthetic
TenantContext or hidden setter is allowed. The new logical setter is
`PreContextSelfDiscoveryScopeReader`, entered only with a captured exact
own-Membership witness from the subject-confined discovery operation. The four-slot
authorization lane keeps its existing grant/lifecycle-only query set; it does not
become a label directory. A label owner has one five-second I/O deadline, no
nested transaction, no profile read and no mutation/audit work. Its fresh
tenant/org announcement is bound to its actual transaction and cleared at actual
disposal, including cancellation and pooled reuse.
Global/candidate/label owners close sequentially: no outer connector is retained
while waiting for another lane. The exact setter/function/privilege census expands
with implementation; missing/nonadmitting subjects disclose no directory.

The response contains only tenant/org IDs, public scope names and independently
verified entry destinations. This self-only navigation read is explicitly Off,
like AccountSite; no other person's PII/profile/roles are disclosed. It remains
personal relationship data, no-store and excluded from diagnostics. Administrator
member reads retain MUST sensitive-read coverage. After selecting a tenant, a
bounded self-context query can enumerate its eligible Active organizations only
when current tenant-wide Membership covers them; branch-only Membership exposes
only its exact branch. Discovery is a suggestion, never an admission witness.

Native distinguishes verified provider identity, durable binding and published
session. Pre-session credentials stay within the existing 1,024 total/128 pending
owners, 16 pending per source and original five-minute transaction deadline.
After subject verification, pre-session/established/closing/logout-return entries
share the eight-owner subject ceiling. No passive poll extends entry lifetime.
A separate opaque host-only entry cookie and entry-bound CSRF authorize deliberate
creation, discovery retry or selection; no tenant session cookie is published.
Entry confirmation can show one escaped, bounded provider account label from
validated ID-token name/preferred_username (at most 128 characters), never the raw
profile or subject. Missing/malformed optional display data uses explicit
reauthentication recovery rather than blocking binding or displaying an actor ID.
The label is transient self-only personal data, retained only by the five-minute
entry; it creates no User mirror or authorization signal. Selection is explicit
confirmation of the destination account/context, not automatic callback publication.
No-Membership completion renders localized recovery/sign-out/retry, not failed
login or a fabricated empty tenant. A failed replacement preserves a live old
session. Expiry/logout cancels publication and joins actual work before releasing
capacity. Unknown creation responses imply neither rollback nor safe automatic
mutation retry; a deliberate retry resolves the existing binding if it committed.

### Selection, refresh and host transition

New native web issuance has no tenant/org claims and optional compatibility actor.
The existing actor/tenant/org/audience mappers actually share the
`learnstack-tenant-api` client scope, attached to both clients. Preserve that scope
and its API-client attachment unchanged. Replace only its web default attachment
with one fixed `learnstack-native-session` default scope containing actor and API
audience mappers. Do not add an optional legacy web attachment. Qualify the exact
web baseline separately from API/direct/default/optional graphs; foreign context
mappers or conflicting fixed-scope definitions refuse before writes.

Accounts, attributes, credentials, lifespan and unrelated mappings remain. This
extends development reconciliation authority without a runtime Admin API. New
development web grants deliberately become tenantless; no continued issuance of
legacy scoped web grants is promised. API acceptance of already issued scoped
bearer profiles remains. Old native sessions may need sign-in again. Real code/
refresh and warm no-write checks must prove the split, not a mapper count alone.

Initial selection and explicit same-host switching require exact Origin and the
current entry/session CSRF proof. The owner serializes refresh/transition work,
chooses one current credential revision, obtains the audited API admission and
positive AccountSite witness, and rechecks epoch/lifetime after every await.
Same-host switching consumes no new provider grant: the token authenticates the
person; API admission confirms application scope. It publishes a fresh handle/
CSRF/context only after closing old publication and settling actual old work.
Admission still reserves capacity; replacement is no capacity bypass. Failed
target admission preserves an otherwise-live source session; a possibly consumed
refresh still follows the existing stricter revoke rule.

Ordinary refresh preserves original application context and provider identity,
obtains positive API admission/AccountSite with the new access token, and publishes
one revision. It never allocates User or changes context. Switching inherits the
original absolute/provider deadlines; it cannot manufacture a new eight-hour
session. ADR-0071 ordinary uncertain-read retention/no-idle-touch remains.

Seed tenant-wide administration origins for both tenants separately from their
existing public hosts, with real mappings, TLS/native admission and exact provider
callbacks. In particular retain Yoga's public organization host. Cross-host
selection navigates to the independently verified destination and begins fresh
destination-bound OIDC/state/cookies. No source cookie, bearer or refresh token
crosses hosts; source sessions remain independent. It is a fresh destination
sign-in, not proof
of source-person continuity; destination account/context confirmation precedes
replacement of any live destination owner. Same-host switch always retains the
original verified subject. Destination URLs come from
admitted deployment/host facts, never a submitted absolute return URL.

The admission event records API approval for publication, not proof that a BFF
cookie reached the browser. Local session lifecycle still has ADR-0070's disclosed
ledger limitation. Logout closes epochs before provider work; late response
cookies can still force reauthentication despite old handles being unusable.

## Consequences

### Positive

- Independent application context survives provider refresh without shared
  attributes, realm-wide provider credentials or a second token-issuing system.
- Durable creation bounds survive restart; successful allocation/audit retain the
  common atomic owner and permanent subject binding.
- Zero-Membership entry and failed replacement have truthful recoverable outcomes.

### Negative

- A new trusted native scope source, purpose credential and confined discovery/
  budget functions expand security-sensitive authority and require real proofs.
- Registration budgets are shared and intentionally deny excess new accounts;
  retained bindings/digests/audit remain personal data. They are not erasure.
- Cross-host entry needs additional configured origins/certificates/callbacks.
  Key mismatch, provider failure, late responses and restart can require sign-in.
- This application-source alternative has design/source support, not completed
  API/BFF/browser activation evidence. Acceptance cannot waive that work.

### Neutral

- Four database logins, live per-act revocation, AccountSite's actual host ceiling,
  ordinary direct bearer semantics and volatile single-process custody remain.
- Production onboarding/operator/issuer rebind stays P03-9; invitations P03-4;
  back-channel logout P03-7; distributed/proxy/production hardening Phase 11.

## Implementation Notes

The P03-3 package owns exact bodies/statuses, bounds, key retirement, counter/
function/setter definitions and step/proof placement. Accept it jointly; no broad
unresolved marker, generic proxy, wildcard permission or reusable authority cache.

Acceptance appends dated bounded Amendments to ADR-0036 (new source/matrix),
ADR-0040 (own-discovery setter/owner census), ADR-0053/0054 (exact native callers/
methods), ADR-0064 (global-self route exception and native evidence), ADR-0070
(pre-session/publication/transition), ADR-0072 (creation activation/functions/
capacity), and ADR-0076 (candidate source). ADR-0068 and its accepted development
package record exact mapper authority; ADR-0071 receives navigation only.
Reconcile Standards 04/05/07/10/11/18/19/20/21, Architecture 09/13/14/23/25/31/33,
Identity/Tenancy specs/matrices, glossary, environment/setup and workflow skills.
Do not mark a gate Accepted or rule Implemented before its actual event/proof.
Reopen for source proliferation, changed custody, production ingress, budget/
creation policy, issuer migration, distributed ownership or failed source proofs.

## Architecture Tests

Extend actual credential/source/context-writer/candidate/setter/owner-function
censuses with planted missing/foreign/native-public-hop-only controls. Preserve
all existing public, legacy bearer, SQL/EF and AccountSite host controls.
Extend `Bff_Session_Refresh_And_Revocation_Have_One_Owner` and
`Bff_Auth_Admission_And_Csrf_Are_Closed` with real native A/B context across
provider refresh, source-key overlap/retirement, no-session/unknown-bootstrap,
quota-before-SQL, durable budgets/restart/races, association mismatch, wrong host,
orgless absence, held refresh/switch/logout and absolute-deadline controls.
Add real app-role discovery/function/body/ACL/caller and pool/cleanup tests.
Runtime qualification uses pinned provider plus actual native HTTPS/API paths;
the stock-scope feasibility probe substitutes for none of these controls.

## References

- [Phase 03](../roadmap/phase-03-identity-admin.md),
  [Identity](../modules/identity/README.md),
  [Database](../standards/05-database.md#identity-storage-contract),
  [Security](../standards/11-security.md#identity-admission-contract),
  [Audit](../standards/18-audit-coverage.md).
- [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md),
  [ADR-0064](0064-tenant-api-authentication-boundary.md),
  [ADR-0068](0068-development-warm-realm-qualification.md),
  [ADR-0070](0070-owned-tenant-bff-sessions.md),
  [ADR-0071](0071-uncertain-account-site-refusals.md),
  [ADR-0072](0072-application-owned-identity-bootstrap.md),
  [ADR-0076](0076-live-scoped-authorization.md).
- [Pinned provider scope permission](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/services/resources/admin/fgap/ClientPermissionsV2.java#L57-L122),
  [code-grant scopes](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/grants/AuthorizationCodeGrantType.java#L179-L180),
  [refresh scope source](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/TokenManager.java#L303-L314).
