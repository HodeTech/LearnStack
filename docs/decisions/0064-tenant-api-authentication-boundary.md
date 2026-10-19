# ADR-0064: Tenant API Authentication Boundary

## Status

Proposed — maintainer approval pending. No dependent runtime is authorized.

**Date:** 2026-10-19
**Deciders:** @cemil

This proposal supplies the security-sensitive access-token profile and HTTP
admission contract for P02b-5. It retains ADR-0036's resolution matrix except for
the precisely named audit disposition below. Acceptance adds dated disclosures
before implementation; it does not rewrite any Accepted decision or accept the
realm, session or authorization packets.

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
   (recommended). Use ASP.NET Core JWT validation, one explicit claim boundary,
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

Subject to maintainer acceptance, LearnStack validates tenant access tokens with
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
  `platform` scope, as ADR-0004 Amendment 1 requires, but grant no permission from
  any other scope. Platform execution remains unavailable to this scheme.

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
- On a platform host, no-credential protected access may challenge with 401;
  a validated principal without a usable resolved tenant still gets 404 for the
  new tenant-scoped read. No platform or unresolved-context endpoint is added.
- Authenticated tenant/organization disagreements return 404 `tenant_mismatch`;
  anonymous assertion disagreements keep 404 `not_found`. Malformed/repeated
  assertion headers keep 400 `validation_failed`. Membership refusal is masked
  404 rather than a fabricated mismatch or permission denial.

### Resolution and attribution

One immutable validated profile feeds the reserved claim site in
`TenantResolverMiddleware`. No new tenant-context writer, setter or factory is
introduced. HTTP identity never becomes an asynchronous `SystemActor`.

| Input on known host | P02b-5 result |
|---|---|
| Tenant host and matching tenant-only token | Row 6 succeeds, `HostAndClaim` |
| Organization host and matching tenant/organization token | Row 9 succeeds, `HostAndClaim` |
| Organization host and tenant-only token | Row 10 remains 404 while the membership reader denies everyone |
| Tenant host with an organization claim; platform host with a tenant claim | Rows 7/14 remain membership-dependent 404 until Phase 03 |
| Host tenant or organization disagrees with valid claims | Rows 8/11/12: 404 and the existing rejection audit under the host tenant |
| Valid principal without tenant claim on a tenant/organization host | Explicit HostOnly case: public read may run, protected read is 404; fixed `tenant_claim_absent` diagnostic, no authority promotion |
| Valid principal without tenant claim on a platform host | Row 15: unresolved; the new tenant-scoped read is 404 |

P02b-6 supplies a matching `organization_id` for the Yoga seed identity so its
positive path is row 9. A tenant-only token does not gain that scope from a
header or host without row 10's live membership answer. Headers cannot repair a
missing mapper. Attribute issuance is admin-controlled; the Phase 03 reader and
durable `sub`/`UserId` mapping remain absent in P02b-5.

Extend the existing rejection record with a closed source, typed actor,
correlation and trusted organization scope. Resolver disagreements and header
disagreements use the same recorder and existing operation slug. A JWT mismatch
uses the mapped host's tenant/organization, never the disagreeing claim's tenant.
Capture attribution before discarding a rejected attempt; do not install a failed
tenant context to obtain it. One request records its first disagreement once.
Audit failure retains the refusal and existing Critical/health behavior.

Retain row 7's audit on organization-membership refusal under the known host
tenant, with the organization claim as the rejected assertion and the validated
actor/source. Row 10's membership refusal remains unaudited as its matrix cell
already specifies. Neither outcome authorizes the attempted organization scope.

For a platform-host membership refusal there is no confirmed request tenant.
Count/log the closed refusal and write no tenant audit row. Do not announce the
claimed tenant or a sentinel merely to record a denial. The narrowly superseded
row-14 audit clause is disclosed below; membership still fails closed.

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

Use the existing MediatR/audit owner and an explicit, exact-type read-only
transaction-mode entry. Do not mark this authenticated query public, classify it
by a naming suffix, open a second transaction or nest a public send inside a
writable outer frame. Register it as non-sensitive query audit coverage.
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

### Exact relationship to Accepted decisions

| Record and clause | Instrument at acceptance | Effect |
|---|---|---|
| ADR-0036, matrix row 14: otherwise **404 + record under `T_j`** | Named bounded supersession, disclosed in a dated ADR-0036 Amendment | A membership refusal without a confirmed host tenant is counted/logged without a durable tenant row. It never establishes business context; the 404 and membership requirement remain. |
| ADR-0036, delegated claim shape, matrix gaps and recording | Additive profile/claimless clarification and attribution seam in the same dated Amendment | Scalar UUID claims, purpose/client validation, rejected-attempt attribution and explicit HostOnly claimless case; existing factory and closed writers remain. |
| ADR-0036, staging table and 2026-09-02 staging erratum | Adjacent dated errata plus that ADR's own correction disclosure under ADR-0041 | Row 10 also requires membership and cannot succeed merely because authentication lands. Preserve original words and acceptance metadata. |
| ADR-0040 Amendment 8, public-only mode-ordering exception | Bounded scope extension plus additive exact-type selection, disclosed in a dated ADR-0040 Amendment | The one authenticated query also uses the existing physical read-only owner and mode-before-announcement ordering; setter and transaction-ownership contracts stay unchanged. |
| ADR-0032 canonical pipeline / authorization pass-through | No replacement | Preserve all eight steps; the outer HTTP route census constrains what can enter them. |
| ADR-0052 public provenance and read-only admission | No replacement | The new read uses the same proven host ceiling; it does not widen anonymous admission or public data. |
| ADR-0053/0054 ingress accounting | No replacement | All original visitor/peer accounting and credential-free rendering remain. |

The exact bounded source clauses are:

> ADR-0036 row 14: otherwise **404** + record under `T_j`.

> ADR-0040 Amendment 8: ADR-0052 authorizes a bounded
> public-only exception to the Decision sketch's first-statement ordering; this
> amendment does not authorize it independently.

The first is narrowed only for unconfirmed platform-host membership denial.
The second gains this one exact authenticated query, not arbitrary reads. These
are normative changes, not ADR-0041 fact corrections. The row-10 staging mistake
is a separate correction: membership was already part of that row when accepted.
A disclosure must name each instrument independently.

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
- Missing mappers cannot grant protected tenant access; revocation and key refresh
  have independently asserted limits.

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

The [P02b-5 package](../roadmap/phase-02b-events-auth.md#p02b-5-decision-package-2026-10-19)
owns steps, per-step reviews and current implementation status. Acceptance first
records the two dated ADR disclosures and row-10 errata, registers catalogue
obligations, and reconciles the named mutable carriers. Architecture 30's blanket
non-public 401 description changes with the catalogue; its no-authentication
status changes only with Step 4 activation. No dependent code starts
before the maintainer accepts both records.

Pin JwtBearer to the repository's ASP.NET Core 10.0.12 package line. Keep vendor
types at the API/infrastructure composition boundary; module contracts carry
typed LearnStack values only. No Keycloak admin adapter is introduced in P02b-5.

## Architecture Tests

Retain/implement `Api_Registers_Only_The_Tenant_Realm_Authority` and
`Backend_RequiresJwt_OnAllAuthenticatedRoutes`, narrowing the latter's blanket
401 promise to the explicit tenant/platform outcomes above. At acceptance,
register these additional canonical obligations in Standards 21:

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

Register before code; keep each status pending until its actual owner tests run.
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
  [Documentation Standards](../standards/13-documentation.md),
  [Architecture test catalogue](../standards/21-architecture-tests-catalogue.md),
  [Glossary](../glossary.md).
- [ASP.NET Core JWT bearer guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0),
  [10.0.12 handler](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Security/Authentication/JwtBearer/src/JwtBearerHandler.cs),
  [10.0.12 options](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Security/Authentication/JwtBearer/src/JwtBearerOptions.cs).
- [Keycloak 26.6.2 token manager](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/TokenManager.java),
  [26.6.2 protocol factory](https://github.com/keycloak/keycloak/blob/26.6.2/services/src/main/java/org/keycloak/protocol/oidc/OIDCLoginProtocolFactory.java).
