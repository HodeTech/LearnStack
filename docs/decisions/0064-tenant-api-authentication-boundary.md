# ADR-0064: Tenant API Authentication Boundary

## Status

Accepted — 2026-10-19. The maintainer approved this revised record and the
five-step P02b-5 package; implementation remains subject to their proof gates.

**Date:** 2026-10-19
**Deciders:** @cemil
**Bounded supersession:** ADR-0036 row 14 (audit clause only);
ADR-0040 Amendment 8 and ADR-0052 (exact authenticated read-mode scope).

This decision supplies the security-sensitive access-token profile and HTTP
admission contract for P02b-5. It retains ADR-0036's resolution matrix except for
the precisely named audit disposition below. Acceptance adds dated disclosures
before implementation; it does not rewrite an Accepted ADR's body or acceptance
metadata. It accepts neither the realm/session packets nor Phase 03 authorization
work. The named normative changes are disclosed separately from clarifications.

## Decision Drivers

- Authentication lifts the host-only ceiling while application authorization is
  still a pass-through. Only a closed, independently checked read surface can be
  exposed safely before Phase 03.
- A signature-valid token is insufficient: its issuer, audience, purpose, issuing
  client and stable actor must agree with the tenant API's contract.
- The two seed hosts have different scope: tenant-wide English and
  organization-scoped Yoga. A callback success cannot prove either scope works.
- A rejected JWT must not become an anonymous request through an
  `[AllowAnonymous]` endpoint. Anonymous rendering must still work with Keycloak
  unavailable when no credential was supplied.
- The existing ingress quotas precede host classification. Adding a subject
  quota cannot honestly promise independent throughput above those shared floors.
- Rejected claims need the shipped assertion recorder, stable attribution and
  bounded diagnostics rather than a second audit detector or token text in logs.

## Considered Options

1. **Strict bearer profile, unchanged ingress floors and closed reads**
   (chosen). Use ASP.NET Core JWT validation, one explicit claim boundary,
   the existing tenant factory and one production authenticated site read.
2. **JWT defaults plus endpoint `[Authorize]`** (rejected). It leaves token
   purpose, actor identity, failed credentials on public endpoints and the
   pass-through authorization window insufficiently constrained.
3. **Deliver live membership and permissions now** (rejected). Those require the
   Phase 03 identity domain; this packet can safely admit only nonsensitive reads
   whose data already belongs to the anonymous public projection.
4. **Replace early visitor/peer accounting for validated callers** (viable
   alternative, not recommended here). Independent 600/min subject throughput
   behind one BFF needs a different ingress design and its own cost/abuse proofs.
   An unvalidated `Authorization` header never earns an early bypass.
5. **Reject every claimless signed token** (rejected). ADR-0036 explicitly
   distinguishes absence from disagreement and permits its platform-host row 15.
   Missing tenant claims grant no authenticated tenant authority here.

## Decision

LearnStack validates tenant access tokens with
one strict `learnstack`-realm profile, constructs tenant context once through the
existing factory, and exposes only enumerated GET/HEAD reads before Phase 03.
The pre-authentication quotas remain unchanged; an additional quota uses the
validated issuer and subject. Authentication grants neither membership nor
permission, and the public renderer remains credential-free.

### Access-token profile

The API is a resource server. The BFF client is `learnstack-web`, confidential
with PKCE S256; the resource audience is `learnstack-api`. P02b-6 configures and
reconciles that client and its mappers; P02b-7 implements the code exchange.
The API takes no client secret and implements no password or token issuance.

- Register exactly one bearer scheme for the tenant API. Pin the configured
  issuer to an absolute URL ending `/realms/learnstack`, without userinfo, query
  or fragment. Metadata is that issuer's OIDC discovery URL; do not choose an
  authority from incoming claims or accept the `learnstack-hub` issuer.
- Require HTTPS except for explicitly configured loopback HTTP in
  `DeploymentMode.Development`. Startup validates configuration without requiring
  Keycloak connectivity; credential-free requests do not fetch metadata.
- Require a signed RS256 token, issuer equality, the `learnstack-api` audience,
  expiration and valid lifetime with 30 seconds of clock skew. Require integer
  `iat`/`exp`, `exp > iat`, no future `iat` beyond skew, and at most one hour
  between them. Validate `nbf` when present. This is an API acceptance bound;
  P02b-6's reconciled realm owns the actual issuance lifetime.
- Require JOSE header `typ = JWT`, payload `typ = Bearer` and
  `azp = learnstack-web`. Keycloak 26.6.2
  distinguishes access tokens from ID tokens through this payload type. Header
  `typ = JWT` alone cannot establish access-token purpose. ID/refresh tokens are
  refused even if a fixture gives them a trusted signature and API audience.
- The protected JOSE header contains exactly `alg`, `typ` and `kid`, each once
  as a nonempty string: `alg = RS256`, `typ = JWT`, and `kid` is at most 128
  characters. It selects a key only from the configured issuer's validated JWKS.
  Reject every other header parameter, including `crit`, `cty`, `jku`, `x5u`,
  `jwk`, `x5c`, certificate thumbprints and `b64`; do not dereference a token URL
  or accept an embedded key. Reject duplicate header names even when equal.
  This is the selected LearnStack profile, not a claim that every optional JOSE
  header is unsafe or that stock JwtBearer follows token-supplied URLs.
- A bounded compact-JWS/header shape check may reject before cryptographic
  validation, but confers no identity or authority. Only the stock handler's
  successful signature/profile validation produces the immutable identity.
- Require one nonempty opaque `sub` of at most 255 characters and one `user_id`
  UUID. Do not parse `sub` as `UserId`, hash it into one, generate one per login,
  or use an email/name/role as the actor. Reject nil UUIDs and `SystemActor`.
- `tenant_id` is absent or one UUID, never an array. Reject nil and
  `TenantId.PlatformSentinel`. `organization_id` is absent or one nonnil UUID
  and requires `tenant_id`. Present malformed identifiers are invalid
  credentials, not missing signals. Use exact D-format UUID strings without
  trimming; UUID letter case does not create a different identity.
- Reject duplicate security properties, including equal duplicates, wrong JSON
  shapes and empty security strings. Check the authenticated token's payload
  representation for `iss`, `sub`, `aud`, `exp`, `iat`, `nbf`, `azp`, `typ`,
  `user_id`, `tenant_id`, `organization_id` and `scope`; counting projected claims alone
  does not detect every duplicate JSON property. An audience may be a string or
  a nonempty array of distinct
  strings containing the API audience. Never deserialize claims into CLR types.
- Accept one `Authorization: Bearer <token>` value, at most 16 KiB for the token.
  Scheme comparison follows HTTP's case-insensitive rule. Repeated headers,
  comma-joined credentials, other schemes and empty/malformed credentials are
  refused. Do not accept query, cookie or body tokens.
- Disable inbound claim remapping, token persistence and provider error details.
  Do not interpret realm/client roles, email or display-name claims as application
  permissions. An optional `scope` is a scalar OAuth scope string; reject its
  `platform` scope everywhere this tenant scheme applies, but grant no permission
  from any other scope. This is stricter than ADR-0004 Amendment 1's non-platform
  endpoint rule within this scheme; Hub/internal authentication is not widened
  or replaced. Platform execution remains unavailable to this scheme.

Use the stock JWT handler and OIDC configuration manager for cryptography,
discovery and key refresh. Configure five-second backchannel waits, no redirects,
256 KiB metadata-body bounds, five-minute automatic refresh and a 30-second
on-demand refresh floor. Disallow last-known-good configurations from reaccepting a
withdrawn signing key after the new key set has been observed. Prove new-key
refresh and old-key rejection with the real handler and a controlled issuer;
framework minimum intervals must not be bypassed through an untested substitute.
An unavailable issuer never causes an invalid credential to fall back to absence.

### Pipeline and error precedence

The order is: existing anonymous admission and body limits; host classification;
`UseAuthentication`; explicit credential-result enforcement; validated-subject
quota; tenant resolution; tenant assertions; `UseAuthorization`; closed HTTP
surface admission; controller dispatch. No middleware may reconstruct a tenant
from headers or select a different resolution outcome by endpoint.
The tenant bearer handler ignores unclassified infrastructure/internal paths;
registering a default scheme must not cause health/OpenAPI requests to fetch
identity metadata or change their existing credential behavior.
Minimal hosting can insert authentication/authorization middleware when their
services exist. Defer production scheme/authorization registration as well as
explicit middleware calls until the complete Step 4 activation; isolated profile
tests use their own service provider. Omitting `UseAuthentication` alone is not
proof that an intermediate implementation has left authentication inactive.

- Unknown hosts keep their masked 404 before any JWT validation or discovery.
  Malformed trusted visitor metadata and exhausted early quotas keep their
  existing precedence too.
- On classified known hosts, a present invalid or undecidable credential is 401,
  including on public endpoints. Set a bare Bearer challenge and the existing
  Problem Details shape; expose no token, claim value, metadata URL or provider
  exception. The framework's authentication result alone does not enforce this.
- An absent credential on a live tenant/organization host may reach only public
  requests. A routed protected request returns the masked `not_found` 404,
  indistinguishable from an unknown host after normalizing request correlation.
  A generic authorization fallback must not turn this into 401.
- On a platform host, an absent credential on this protected endpoint also
  returns 404 `not_found`, preserving row 13's status. A validated principal
  without a usable host-backed tenant receives the same refusal. No platform
  or unresolved-context endpoint is added and no absent-token 401 is permitted.
- Keep `[Authorize]` on the endpoint and install a host-aware
  `IAuthorizationMiddlewareResultHandler` for the closed tenant HTTP surface.
  It maps the framework's missing-credential challenge to the existing masked
  404 shape; it does not defer to the default challenge. Present invalid
  credentials have already returned 401 at the credential-result gate. Leave
  infrastructure/internal behavior outside this handler's scoped surface.
- Rows 8/11/12's JWT/host disagreements and authenticated header-assertion
  mismatches return 404 `tenant_mismatch`. Rows 7/10/14's membership refusals and
  anonymous header mismatches return 404 `not_found`. Malformed/repeated assertion
  headers remain 400 `validation_failed`. Preserve the pure factory's shared
  `Refused`; the HTTP resolver classifies existing attempt facts and uses the
  shared Problem Details factory to write the authenticated mismatch code.
- The new endpoint's no-authority 404 is an outer HTTP admission result.
  Standards 04's generic unresolved MediatR gate still returns
  `tenant_mismatch`; do not silently change that gate or call an outer
  authentication challenge a clarification of its result.

### Resolution and attribution

One immutable validated profile feeds the reserved claim site in
`TenantResolverMiddleware`. No new tenant-context writer, setter or factory is
introduced. HTTP identity never becomes an asynchronous `SystemActor`.

| Input on known host | P02b-5 result |
|---|---|
| Tenant host and matching tenant-only token | Row 6 succeeds, `HostAndClaim` |
| Organization host and matching tenant/organization token | Row 9 succeeds, `HostAndClaim` |
| Organization host and tenant-only token | Row 10 remains 404 `not_found`, no audit, while the membership reader denies everyone |
| Tenant host with an organization claim; platform host with a tenant claim | Rows 7/14 remain membership-dependent 404 `not_found` until Phase 03; their different audit dispositions are explicit below |
| Host tenant or organization disagrees with valid claims | Rows 8/11/12: 404 `tenant_mismatch` and the shared recorder's rejection audit under the host tenant |
| Valid principal without tenant claim on a tenant/organization host | Explicit HostOnly case: public read may run, protected read is 404; fixed `tenant_claim_absent` diagnostic, no authority promotion |
| Valid principal without tenant claim on a platform host | Row 15: unresolved; the new tenant-scoped read is 404 |

P02b-6 supplies a matching `organization_id` for the Yoga seed identity so its
positive path is row 9. A tenant-only token does not gain that scope from a
header or host without row 10's live membership answer. Headers cannot repair a
missing mapper. Attribute issuance is admin-controlled; the Phase 03 reader and
durable `sub`/`UserId` mapping remain absent in P02b-5.

A validated claimless profile retains its stable `UserId` for attribution in a
host-only context. `HostOnly` describes tenant authority, not anonymity: actor
presence or `IsAuthenticated` never upgrades its ceiling. Correct that enum's
anonymous-page XML example and the glossary, not the origin set. Public reads
remain Off and public-only, the protected query refuses, and authenticated
header rejections can still be attributed to their real actor. Do not null the
actor or add a new origin to infer authority from authentication.

### Rejection recorder and diagnostic contracts

Extend `TenantAssertionRejection` while keeping its defended tenant nonnullable:

- A closed `TenantAssertionSource` maps only to `header` or `jwt-claim` in
  metadata/counters. Both recorder implementations consume the source rather
  than hard-coding `header` on JWT paths.
- Carry typed `ActorUserId`, captured request `CorrelationId` and nullable
  `TrustedOrganizationId` explicitly. Derive authenticated tier and actor from
  the validated projection; never a raw claim or a failed attempt's accessor.
- `AuditEntryDraft.OrganizationId` remains **null**, preserving the current
  tenant-wide security-row visibility. Store the trusted organization UUID or
  null as `metadata.trustedOrganizationId`; do not move it into the scope column.
  Resolver refusals take it from the independently classified host; later header
  refusals take it from the successfully resolved context.
- The draft uses the record's captured correlation, matching the request's shared
  correlation header/Problem Details value. It does not reread the ambient
  context after a rejected resolution. Capture without installing that context.

Resolver and header rejections use the existing `tenancy.tenant_assertion.reject`
operation through the same recorder. A JWT/host mismatch writes under the host
tenant, never the disagreeing claim's tenant. Implement row 7's already-required
but currently absent runtime audit: one row under the host tenant with rejected
organization, validated actor and `jwt-claim` source. Row 10 remains unaudited.
The existing slugs are retained; no existing row-7 runtime audit is claimed.

Add a separate synchronous `RecordNoTenantRefusal` method on that recorder for
row 14. It carries the closed source/dimension, validated actor and captured
correlation for attribution, but **no tenant or asserted tenant value**. Its
logging half increments the existing `learnstack_tenant_assertion_unresolved_total`
with only `source=jwt-claim` and `dimension=Tenant`, and emits a fixed
`membership_unconfirmed` diagnostic. Actor/correlation never become metric labels.
Its auditing decorator forwards without calling the audit store, opening a
connection or writing a row; an already constructed injected store is not I/O.
Do not announce the claimed tenant or a sentinel to record this denial.

One rejected request records its first failing comparison once because resolution
short-circuits before header assertions. Separate requests remain separate
occurrences: ADR-0036's per-occurrence, no-coalescing authenticated contract is
unchanged. Audit failure preserves the refusal and existing Critical/health
behavior. Prove row 7 has one audit; row 10 none; row 14 none plus one source-aware
counter increment, without fabricating a nullable durable audit tenant.

Emit `tenant_claim_absent` as one fixed **Debug log event** per admitted request
with a validated profile but absent tenant claim, after subject admission. It
contains no claim value or labels. It is neither a new metric nor a Problem
Details/message key: public 200 and protected 404 remain unchanged.

### Vendor diagnostic boundary

Suppress the exact vendor logger category
`Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler` at every level
before any production log sink sees it; a Warning threshold is insufficient.
This includes exception-bearing events 1 (`TokenValidationFailed`) and 3
(`ProcessingMessageFailed`) and inherited failure-message event 7. Preserve
unrelated application logging and emit the authentication outcome independently
under a LearnStack category using closed stage/reason values, never the raw
exception, its message/inner/data, token, opaque subject or issuer/audience text.
Do not rely on an authentication callback that runs after vendor logging.

Configure IdentityModel's PII/complete-security-artifact logging off and constrain
backchannel diagnostics as part of the same boundary. Qualify the actual
Serilog-to-OTLP/error-capture paths and test collector; a response-only flag or
an ASP.NET configuration entry bypassed by the real logger factory is not proof.
Token/issuer/audience/metadata/provider-exception canaries must not reach any
enabled sink. Explicitly sanctioned typed UUID/correlation attribution remains
allowed in the recorder; it is not permission to log arbitrary claim text.
Events 1/3/7 each need a negative canary and a positive closed-outcome control.

### Additional subject quota

Use a stable `(validated issuer, sub)` key, not the bearer text, `jti`, user agent,
host, tenant selector or claimed `user_id`. Rotation cannot reset quota, changing
tenant/organization cannot reset it, and distinct subjects have distinct state.
Keep 600 requests per one-minute fixed window, no queue, first refusal's
`Retry-After` and the standard 429 body. Run before durable mismatch recording.
Reclaim only full-quota idle partitions; bound retirement work and close/join
owned timers on disposal without resetting an unexpired quota.

The original 60/min visitor and 600/min physical-peer limits still apply to
every request. This proposal grants no 600/min end-to-end entitlement to a
principal behind one visitor or BFF peer. Prove subject independence through the
whole middleware chain with a reduced subject test limit below both early
floors, and separately prove the actual 600 boundary. Do not raise production
floors or manufacture visitor addresses to label the combined path independent.
Phase 11 owns distributed/edge capacity; a different authenticated ingress
policy requires an explicit decision rather than an implicit token bypass.

### Closed authenticated read

Add `GET /api/v1/account/site`, operation `GetAccountSite`, dispatching the
non-`[PublicSurface]` `GetAccountSiteQuery` in Tenancy. Return only the validated
actor id, resolved tenant/organization and the existing `PublicSite` projection
read from the live database. Accept no query parameters or client-selected ids.
No email, raw subject, roles, token or privileged configuration is returned.
The projection has the same public visibility/host scope as the anonymous site.

Before calling any configuration/settings reader, the query independently
requires `Origin == HostAndClaim`, a valid non-system actor and matching real
resolved/host tenant IDs: initialized, nonnil and not the platform sentinel.
Nonnull organization IDs must be initialized and nonnil, and a nonnull host
organization must match the resolved organization. Initialization alone is not
real identity; mirror the existing host-scope guard's UUID checks.
Missing/mismatched provenance or `ClaimAndMembership`
returns `Result.Fail(not_found)` without invoking a reader, even after Phase 03
provides a live membership reader. No host scope is synthesized from JWT claims.
Test that future row-14 success, including a planted apparently matching scope,
still cannot reach this host-backed query or turn into a reader exception/500.

Use the existing MediatR/audit owner and an explicit, exact-type read-only
transaction-mode entry. Do not mark this authenticated query public, classify it
by a naming suffix, open a second transaction or nest a public send inside a
writable outer frame. Register `builder.Off<GetAccountSiteQuery>()` in Tenancy's
audit catalogue and record that Off classification in its module audit document.
A normal query success/refusal writes no query audit row; independently rejected
assertions retain their sanctioned audit path. It is not ReadSensitive or MAY.
The database reader retains its independently checked host provenance and
physical read-only requirement. No migration, role/grant, membership, write
permission or authorization bypass is added.

The complete production route/method/request-type census is the eight existing
public GET/HEAD actions plus this GET, with separate enumerated infrastructure
exclusions. Prove transitive dispatch and fail on a planted extra controller,
minimal route, mutation or unregistered request. While authorization passes
through, no protected business write, authoring read or enrollment-only content
is routable. Emit `no-store` and document bearer security and all refusal shapes
in OpenAPI. Regenerate the typed SDK without treating all generated GETs as
public: keep an exact public operation set and a separate authenticated contract.
P02b-7 owns session-based bearer transport; the existing public transport never
starts forwarding credentials.

Use an explicit account resource route, for example `[Route("account")]` plus
`[HttpGet("site")]`; the controller-name convention alone is not this URL.
Extend the outer response policy for the exact account/site path before any
refusing middleware. Apply and reassert `Cache-Control: no-store`, removing
ETag/Last-Modified, on successes and early errors; the current public-only
prefix predicate does not cover this endpoint. Do not accidentally broaden the
anonymous public-route allowlist when extending response headers.

Split generated GET ownership into exact, disjoint public and authenticated
operation/path sets whose union remains exhaustive. Keep `PUBLIC_GET_WRAPPERS`
exhaustive only over the declared public subset; prove AccountSite belongs only
to the authenticated subset and an unowned or doubly owned GET fails the guard.

### Exact relationship to Accepted decisions

| Record and clause | Instrument at acceptance | Effect |
|---|---|---|
| ADR-0036, matrix row 14: otherwise **404 + record under `T_j`** | Named bounded supersession, disclosed in a dated ADR-0036 Amendment | A membership refusal without a confirmed host tenant is counted/logged without a durable tenant row. It never establishes business context; the 404 and membership requirement remain. |
| ADR-0036, delegated claim shape, matrix gaps and recording | Additive profile/claimless clarification and attribution seam in the same dated Amendment | Scalar UUID claims, purpose/client validation, rejected-attempt attribution and explicit HostOnly claimless case; existing factory and closed writers remain. |
| ADR-0036, staging table and 2026-09-02 staging erratum | Adjacent dated errata plus that ADR's own correction disclosure under ADR-0041 | Row 10 also requires membership and cannot succeed merely because authentication lands. Preserve original words and acceptance metadata. |
| ADR-0040 Amendment 8, public-only mode-ordering exception | Bounded scope extension plus additive exact-type selection, disclosed in a dated ADR-0040 Amendment | The one authenticated query also uses the existing physical read-only owner and mode-before-announcement ordering; setter and transaction-ownership contracts stay unchanged. |
| ADR-0032 canonical pipeline / authorization pass-through | No replacement | Preserve all eight steps; the outer HTTP route census constrains what can enter them. |
| ADR-0052 Decision and Context, public-read mode-ordering authorization | Bounded scope extension, disclosed in its own dated ADR-0052 Amendment | Extend that authorization to exact `GetAccountSiteQuery` while preserving public markers, public data and host ceiling. This is a normative extension, not a factual correction. |
| Standards 11 § Tenant Context / The rule: physical PublicSurface ReadOnly owner only | Mutable rule reconciliation at acceptance, citing ADR-0064 and both dated read-mode disclosures | Name the one exact authenticated query as an additional permitted physical ReadOnly owner; no naming heuristic or general authenticated-read exception. |
| Standards 04 § Public surface, platform gate code; Standards 10 diagnostics | Mutable carrier clarification | Preserve the generic MediatR gate while naming AccountSite's outer `not_found` refusal, source-aware counters and fixed diagnostics. |
| ADR-0053/0054 ingress accounting | No replacement | All original visitor/peer accounting and credential-free rendering remain. |

The exact bounded source clauses are:

> ADR-0036 row 14: otherwise **404** + record under `T_j`.

> ADR-0040 Amendment 8: ADR-0052 authorizes a bounded
> public-only exception to the Decision sketch's first-statement ordering; this
> amendment does not authorize it independently.

> ADR-0052 Decision: LearnStack serves every institution public read as a
> `[PublicSurface]` MediatR request dispatched through `ISender` in an explicitly
> read-only ambient transaction.

> Standards 11 § Tenant Context / The rule: For a physical PublicSurface
> ReadOnly owner only,

The first is narrowed only for unconfirmed platform-host membership denial.
ADR-0040's public-only ordering scope and ADR-0052's associated authorization
gain this one exact authenticated query, not arbitrary reads; Standards 11 must
name it too. ADR-0052's public-reader guarantee itself is not weakened. These are
normative scope changes, not ADR-0041 fact corrections. The row-10 staging
mistake is a separate correction: membership was already part of that row when
accepted. Each Accepted ADR receives its own dated disclosure.

## Context

The entry audit at `0e23cb14` found no JWT package/registration or authenticated
product route. The resolver leaves J absent, the assertion record has no source,
actor or correlation fields, and `AuthorizationBehavior` delegates unconditionally.
Pure factory tests do not prove a signed token can reach any matrix row.

The dev realm is Keycloak 26.6.2. It has no protocol mappers/client scopes,
`learnstack-web` is public, seed users carry `tenant-a` rather than tenant UUIDs,
and `make seed` checks realm readiness without reconciling it. P02b-5 therefore
qualifies the API through a controlled signed-token issuer and real middleware;
P02b-6 must independently qualify actual issued tokens, warm reconciliation,
admin-only attributes and preserved `basic` scope. Until then a callback/token
from the old export is not evidence of this API contract working.

What changes this choice: required authenticated throughput beyond shared
ingress floors, a permission-sensitive HTTP consumer, a different token issuer
profile, or live membership/identity synchronization. Reopen the affected
boundary before its first consumer; do not infer authorization from this phase.

## Consequences

### Positive

- One validated identity feeds resolution, rate limiting and rejection attribution.
- Real read-only production subjects make authentication and isolation proofs
  falsifiable without exposing the still-unprotected authoring commands.
- Public sites remain usable without credentials or identity-provider connectivity.
- Missing mappers cannot grant protected tenant access. Lifetime validation and
  rejection after an observed signing-key withdrawal have independent proofs.
  This does not establish immediate user/session revocation: a previously issued
  JWT can remain usable until expiry within its acceptance bound and clock skew.

### Negative

- Shared visitor/peer floors can throttle otherwise valid users below 600/min.
  The subject quota is additional protection, not independent connection capacity.
- Claimless valid users can still read public data; a fixed diagnostic and an
  explicitly failing protected read detect the missing tenant mapper.
- Platform-host membership denials lose the matrix's promised durable row; there
  is no tenant context to fabricate. Closed diagnostics retain the occurrence.
- Phase 03's missing membership model means tenant-only Yoga tokens, organization
  claims on tenant-wide hosts and claim-only platform access remain refused.
- Strict client/purpose/actor validation deliberately rejects old dev tokens.
  P02b-6 must converge the realm before the BFF can consume this path.

### Neutral

- P02b-6 owns actual realm changes and token TTL; P02b-7 owns sessions and browser
  login; P02b-8 owns LS0002 and phase exit. Phase 03 owns durable identity,
  membership, permissions and privileged routes. Phase 11 owns production identity
  provisioning, readiness and distributed limiter capacity.

## Implementation Notes

The [P02b-5 package](../roadmap/phase-02b-delivery-log.md#p02b-5-decision-package-2026-10-19)
owns steps, per-step reviews and current implementation status. Acceptance
records dated disclosures in ADR-0036, ADR-0040 and ADR-0052, plus row-10 errata,
and reconciles Standards 11's PublicSurface-only wording. It registers catalogue
obligations, and reconciles the named mutable carriers. Architecture 30's blanket
non-public 401 description changes with the catalogue; its no-authentication
status changes only with Step 4 activation. The maintainer accepted both records
before dependent code; acceptance alone does not establish runtime delivery.

Pin JwtBearer to the repository's ASP.NET Core 10.0.12 package line. Keep vendor
types at the API/infrastructure composition boundary; module contracts carry
typed LearnStack values only. No Keycloak admin adapter is introduced in P02b-5.

## Architecture Tests

Retain/implement `Api_Registers_Only_The_Tenant_Realm_Authority` and
`Backend_RequiresJwt_OnAllAuthenticatedRoutes`, narrowing the latter's blanket
401 promise to the explicit invalid-credential and absent-credential outcomes
above. Acceptance registers these additional canonical obligations in
Standards 21:

- `Tenant_Authentication_Profile_Is_Closed`: real-handler signature/profile,
  duplicate-property, purpose, metadata and key-refresh controls.
- `Authentication_Middleware_Order_Is_Enforced`: whole-chain ordering and a
  planted reorder which must fail.
- `Authenticated_Http_Surface_Is_Enumerated_And_ReadOnly`: nonempty production
  census, transitive dispatch, extra-route/write mutants and physical SQL refusal.
- `Keycloak_Roles_Are_Not_Application_Authority`: complete production call-site
  census and planted role/claim-based authorization violations.
- `Authenticated_Rate_Limits_Use_Validated_Subject`: stable identity, rotation,
  concurrent accounting, bounded lifecycle, whole-chain reduced-budget proof and
  actual-default 600 boundary, including audit suppression after refusal.

Register before code; keep each status **Registered** until its actual owner tests run.
Do not claim a source scan proves issuer validation, a supplied factory attempt
proves JWT handling, or a context-only stub proves database isolation.

## References

- [ADR-0004](0004-authentication-strategy.md), [ADR-0017](0017-tenant-organization-hierarchy.md),
  [ADR-0032](0032-exception-handling-logging-and-observability.md),
  [ADR-0036](0036-tenant-resolution-trusted-inputs.md),
  [ADR-0040](0040-ambient-unit-of-work.md),
  [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md),
  [ADR-0044](0044-audit-write-path.md),
  [ADR-0052](0052-anonymous-public-read-boundary.md),
  [ADR-0053](0053-trusted-public-server-rendering.md),
  [ADR-0054](0054-bounded-public-renderer-admission.md).
- [Identity architecture](../architecture/13-identity-and-auth.md),
  [API design](../standards/04-api-design.md),
  [Security Standards](../standards/11-security.md),
  [Observability Standards](../standards/10-observability.md),
  [Audit Coverage](../standards/18-audit-coverage.md),
  [Documentation Standards](../standards/13-documentation.md),
  [Architecture test catalogue](../standards/21-architecture-tests-catalogue.md),
  [API Gateway architecture](../architecture/30-api-gateway.md),
  [Tenancy module](../modules/tenancy/README.md),
  [Tenancy audit matrix](../modules/tenancy/audit.md),
  [Glossary](../glossary.md).
- [ASP.NET Core JWT bearer guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0),
  [10.0.12 handler](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Security/Authentication/JwtBearer/src/JwtBearerHandler.cs),
  [10.0.12 JWT log events](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Security/Authentication/JwtBearer/src/LoggingExtensions.cs),
  [10.0.12 base handler](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Security/Authentication/Core/src/AuthenticationHandler.cs),
  [10.0.12 options](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Security/Authentication/JwtBearer/src/JwtBearerOptions.cs).
- [Keycloak 26.6.2 token manager](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/TokenManager.java),
  [26.6.2 protocol factory](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/OIDCLoginProtocolFactory.java).
- [Keycloak 26.6.2 JWT encoder](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/jose/jws/DefaultTokenManager.java),
  [26.6.2 JWS builder](https://github.com/keycloak/keycloak/blob/26.6.2/core/src/main/java/org/keycloak/jose/jws/JWSBuilder.java),
  [RFC 7515 § 4](https://www.rfc-editor.org/rfc/rfc7515#section-4).

## Amendments

### Amendment 1 — bounded assertion diagnostics (2026-10-20)

> **Erratum — 2026-10-20.** The paragraph below calls the subject bound
> "ADR-0064's 4096-subject registry". The original Decision selects no count;
> § Additional subject quota and the subsequent implementation show the false
> attribution. The Decision is unchanged. Current bound authority:
> [ADR-0066](0066-bounded-tenant-subject-admission.md). Recorded in Amendment 2.

**Status: Accepted.** The maintainer accepts
[ADR-0065](0065-bounded-tenant-assertion-diagnostics.md) and authorizes its
implementation after review corrections without another approval gate. Its
acceptance precedes dependent code. This bounded normative supersession replaces
only the recorder's authenticated every-occurrence durable-write promise:
`TenantAssertionAuditBudget`, shared as a singleton across scoped recorders,
admits 600 write attempts per independently defended host tenant per instance in
each first-request-anchored monotonic minute. Failed writes consume allowance;
suppressed requests cannot extend expiry. Actors/sources/dimensions share it;
there is no tenant `AuditConfig` override or separate 4096-tenant ceiling.
ADR-0064's 4096-subject registry remains a distinct request-admission mechanism.

An attacker can consume that aggregate allowance, leaving later actors without
durable evidence during the window. Each occurrence retains its refusal,
mismatch count and exactly one Warning: normal 4001 or suppressed 4006
`AuditBudgetExhausted`, with typed tenant/actor/correlation properties. The closed
suppression counter has only dimension/source/reason labels. Suppression makes
no audit-store call; admitted events remain MUST and retain existing failure/
health behavior. General mutation MUST durability and tenant `AuditConfig` rules
are unchanged. Anonymous bursts, row-7/10/14 dispositions, subject/peer/visitor
quotas, authentication authority and HTTP refusal contracts remain intact.

ADR-0065 also ratifies the process-wide outbound native HTTP/DNS projection
already shipped in `c6108bf9`, with its ordinary dependency-metric attribution
cost. Native DNS retains only `error.type`; HTTP retains its bounded method,
status, protocol, scheme and error dimensions. Destination/path/query values
are absent from metrics for all requests. Metadata canaries remain absent from
all exported channels; ordinary HTTP traces/logs stay observable. Phase 11 owns
qualified finite dependency aliases before their first consumer and measured
production audit capacity. Original Accepted body/metadata remain verbatim;
this is not an ADR-0041 false-when-written correction. The prior
[Step 5 closeout](../roadmap/phase-02b-delivery-log.md#p02b-5-step-5--local-implementation-and-review-closeout-2026-10-19)
remains historical; the
[2026-10-20 remediation record](../roadmap/phase-02b-delivery-log.md#p02b-5-external-review-remediation-2026-10-20)
owns subsequent implementation, proofs and current PR-head qualification.

### Amendment 2 — explicit subject registry bound and attribution (2026-10-20)

The maintainer accepts [ADR-0066](0066-bounded-tenant-subject-admission.md), an
additive admission/resource decision. This ADR's original Decision chooses the
stable issuer/subject quota and safe retirement but no partition count.
Amendment 1's attribution of 4096 to this ADR was therefore false when written;
its adjacent inline erratum preserves the original text under ADR-0041.
The original Decision, date and deciders remain unchanged. ADR-0066 now owns the
exact bound, cleanup/fault protocol and closed refusal signals; the 600 quota,
authentication authority and ADR-0065's independent audit allowance are intact.

The correction carriers are ADR-0066, this erratum and amendment, ADR-0065's own
erratum/amendment, the [decision index](README.md), Standards
[04](../standards/04-api-design.md), [10](../standards/10-observability.md) and
[11](../standards/11-security.md), Architecture
[13](../architecture/13-identity-and-auth.md) and
[33](../architecture/33-cross-cutting-concerns.md), the
[glossary](../glossary.md), [CLAUDE.md](../../CLAUDE.md), the
[roadmap index](../roadmap/README.md), [Phase 11](../roadmap/phase-11-production-hardening.md)
and the [acceptance/delivery record](../roadmap/phase-02b-delivery-log.md#adr-0066-acceptance-and-implementation-2026-10-20).
Catalogue Asserts and runtime/test owners gain their new obligations together
with actual implementation; acceptance alone is not proof of delivery.

### Amendment 3 — read-mode instrument classification (2026-10-20)

The Status summary groups the instruments under “Bounded supersession.”
The exact relationship table is authoritative: only ADR-0036 row 14's audit
clause is replaced. ADR-0040 Amendment 8 and ADR-0052 receive bounded read-mode
scope extensions and exact-type selection, disclosed in each ADR's own dated
amendment. No new read-mode authorization follows from the summary label.

This clarification preserves the original Status, Decision, date, deciders and
rationale. The [decision index](README.md) links this clarification; the
[systematic correction record](../roadmap/phase-02b-delivery-log.md#p02b-5-systematic-proof-and-document-corrections-2026-10-20)
owns the associated mutable-carrier reconciliation. No decision changes.

### Amendment 4 — Verified subjects and existing application bindings (2026-10-25)

**Status: Accepted.** [ADR-0072](0072-application-owned-identity-bootstrap.md)
replaces required API wire `user_id` with stock-verified provider-subject admission
followed by existing-binding normalization. An optional wire actor is consistency
evidence; a conflicting, malformed or reserved actor refuses. Credential
enforcement recognizes the verified stage, and the unchanged issuer/sub quota
precedes binding SQL.

Unknown, conflicting, Closing/Closed or unavailable bindings uniformly return
401 without allocation. Remaining token checks, route invariance, the closed
GET/HEAD surface, public anonymity and AccountSite's absent-token 404/valid-token
wrong-method 405 remain. No optional API actor claim weakens the native complete
identity or AccountSite publication witness.

Joint ADR-0072/0073 approval accepts this bounded authentication change. P03-1
Step 5 owns provider/app-role admission, lookup load, pool, cancellation and
store-failure proofs. P03-3 needs separate acceptance before new-user HTTP/BFF
activation; no new endpoint is authorized here.

### Amendment 5 — Live authorization and one administrative read (2026-10-27)

Accepted [ADR-0076](0076-live-scoped-authorization.md) narrowly supersedes this
record's AccountSite-only authenticated surface. It admits one additional
`GET /api/v1/identity/memberships`, under Tenant `identity.membership.admin`,
HostAndClaim or confirmed ClaimAndMembership and ordinary MUST sensitive-read
durability. This administrative read can cross branches within its tenant;
it does not relax PublicSurface or AccountSite's independent HostScope ceiling.
No Organization grant or HostOnly context admits the directory.

AccountSite gains live Membership/lifecycle admission while retaining its
nonsensitive Off/physical READ ONLY shape, HostAndClaim and matching real host.
Present invalid/undecidable credentials retain fixed 401; absent protected
credentials/noncoverage remain masked 404; capability denial is 403. Candidate
store unavailability has fixed 503 dependency_unavailable, separately from
ADR-0072's binding-store uniform 401 contract. Resolution stays route-independent.

The exact [API contract](../standards/04-api-design.md) and
[Security contract](../standards/11-security.md) own bounds, pagination and
precedence. [ADR-0077](0077-trusted-authorization-evidence.md) supplies denied/
sensitive-read evidence before activation. No HTTP mutation, new-user bootstrap,
BFF proxy/allowlist expansion or generic authenticated read follows. P03-2 owns
implementation/qualification; approval alone changes no runtime. Other decisions
and historical Accepted body/status/date remain unchanged.
