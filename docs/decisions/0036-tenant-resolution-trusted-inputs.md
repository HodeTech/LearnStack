# ADR-0036: Trusted Inputs for Tenant and Organization Resolution

## Status

Accepted

**Date:** 2026-08-18 **Deciders:** @platform

## Decision Drivers

- **Four documents give four different answers, and the first consumer is about to be
  written.** [Phase 02a Packet 4](../roadmap/phase-02a-kernel-tenancy.md) lands the API
  conventions, including "tenant + organization header binding". The corpus it binds
  against does not agree with itself. [Standards 04 § Tenant
  Context](../standards/04-api-design.md) gives a three-entry resolution order — host,
  `tenant_id` JWT claim, explicit platform-admin override — with **no header**. The
  isolation flow in [architecture/09](../architecture/09-tenant-isolation.md) shows
  middleware reading tenant and organization **from JWT claims**.
  [architecture/13](../architecture/13-identity-and-auth.md) says LearnStack
  "**rejects** requests where the host-derived tenant disagrees with the claim" — a
  cross-check, not a priority order. And the Phase 02a scope lists "API request headers
  (`X-Tenant-Id`, `X-Organization-Id`)" as a resolution source in its own right. Packet
  4 writes the binding and Packet 7 writes the resolution, so the ambiguity has to close
  before the binding, not after it.
- **The component that would sanitise these headers is demand-gated to Phase 11.**
  APISIX terminates client requests and normalises headers
  ([ADR-0015](0015-api-gateway-apisix.md), [architecture/30 § plugin
  chain](../architecture/30-api-gateway.md)), but per
  [ADR-0035](0035-demand-gated-infrastructure.md) the gateway ships in [Phase
  11](../roadmap/phase-11-production-hardening.md) against a written trigger. From
  Packet 4 until that trigger fires, nothing between a hostile client and the API strips
  a client-supplied `X-Tenant-Id`. A security property that only holds once an
  unscheduled component arrives is not a security property. This ADR does not leave that
  gap unowned: [architecture/30](../architecture/30-api-gateway.md) already states that
  the gateway's responsibilities "are carried by ASP.NET middleware inside the API
  process" until Phase 11 and names rate limiting among them, and nothing delivers it.
  Packet 4 delivers it, as a deliverable of this ADR.
- **A client-controlled resolution input sits underneath every isolation layer, not
  beside them.** [ADR-0003](0003-tenant-isolation-defense-in-depth.md) makes isolation
  defense-in-depth: application context, EF global query filters, PostgreSQL RLS, a
  non-owning application role. Every one of those layers scopes faithfully to *whatever
  tenant the context says*. EF filters on `currentTenantId`; RLS reads `app.tenant_id`
  as the middleware set it. If the client picks that value, all four layers cooperate to
  serve the wrong tenant's data correctly. Defense in depth defends the path; it cannot
  defend the input.
- **The header has a legitimate producer, and the anonymous path depends on it.** The
  Next.js BFF resolves the tenant from the host and forwards it — [Standards 07 § Tenant
  Resolution](../standards/07-frontend-architecture.md) and the [architecture/14
  sequence](../architecture/14-frontend-architecture.md), which goes further and says
  "the API enforces the org scope from the headers". That is not a diagnostic nicety: an
  anonymous Server Component fetch carries no JWT, and the host the API sees is its own
  service host, so on the documented path the header is the *only* tenant signal that
  reaches the API. A rule that simply bans it turns every anonymous page render into a
  404. The need is real; what has to be bounded is the *authority* the header carries,
  not its existence.

## Considered Options

1. **Assertion, never a source** (chosen). `X-Tenant-Id` and `X-Organization-Id` are
   bound and compared against an independently resolved tenant. They can cause a request
   to be *rejected*; they can never cause a tenant to be *selected*.
2. **A trusted hop conveying the tenant** (rejected). Accept `X-Tenant-Id` as
   authoritative once the caller proves it is the BFF. Rejected on its **shape**, not on
   its availability — see § Why the hop conveys a host and not a tenant.
3. **A trusted hop conveying the host** (chosen, alongside option 1). The BFF states
   the *host* it received, over a hop authenticated by network position **and** a shared
   secret; LearnStack resolves that host itself against `platform_host_to_tenant`. The
   header names a lookup key with a closed codomain, never a tenant.
4. **Trusted unconditionally** (rejected). Bind the header and use it when present —
   the plain reading of the Phase 02a scope line, and a tenant-crossing vulnerability
   with a one-sentence exploit.
5. **Do not bind the headers at all** (rejected). Safe and lossy: it deletes the only
   automated detector for a BFF, cache or host mapping that resolved a different tenant
   than the API did, and those failures are silent by construction because the response
   is a valid page for the wrong tenant.
6. **A linear resolution order, first signal wins** (rejected). The shape this ADR
   originally carried. It contradicts
   [architecture/13](../architecture/13-identity-and-auth.md) and cannot produce the
   cross-check [Phase 02b](../roadmap/phase-02b-events-auth.md) lists as a deliverable
   *and* as a completion criterion, because under a fallback chain a present host answer
   means the claim is never examined.
7. **Put the tenant in the URL for the public read surface** (rejected). Contradicts
   [Standards 07](../standards/07-frontend-architecture.md) ("public-site URLs do not
   carry the tenant in the path") and does not help: a path segment is as
   client-controlled as a header and needs exactly the same rule.

## Decision

LearnStack resolves tenant and organization from **authoritative signals only**, and it
resolves them by **agreement, not by priority**. There is no fallback chain. Every
authoritative signal present on a request is resolved independently, and the request
proceeds only on their intersection. Absence of a signal is not disagreement; presence
of two that differ is.

> **Authoritative signals establish authority by agreement. Client-controlled inputs
> select nothing and narrow nothing. They can cause a request to be rejected, and that is
> the whole of their authority.**

This replaces the three-entry priority order this ADR previously carried, and the one
[Standards 04](../standards/04-api-design.md) carries today.

### The signals

| Signal | Value | Trusted for | Not trusted for |
|---|---|---|---|
| **H** — host lookup result | `IHostToTenantResolver.ResolveAsync(effectiveHost)` → `HostResolution(TenantId, OrganizationId?)` or null | Naming a tenant already publicly addressable at that hostname | Anything beyond the anonymous public surface |
| **J** — validated claims | `tenant_id` / `organization_id` on a signature-validated `learnstack`-realm JWT | Naming the tenant and organization the **session** was issued for | Proving the membership still exists — a token outlives revocation by its TTL |
| **M** — live membership | `ITenantMembershipReader.CoversAsync(userId, tenantId, organizationId?)` | Confirming J at request time | Selecting a tenant on its own |
| **P** — ambient payload | `JobParams.TenantId`, integration-event envelope | Non-HTTP entry points | HTTP requests |
| **A** — client assertions | `X-Tenant-Id`, `X-Organization-Id`, `?org=`, cookies | **Nothing.** Compared, recorded, discarded | Everything |

A `learnstack-hub`-realm token on a tenant-facing endpoint is **401**, never a signal.

### One header names a host, and it is still not a source

The corrected form of the central rule, and the one sentence in this ADR that changed
meaning rather than wording: **no request header names a tenant or an organization.**
Exactly one header — `X-LearnStack-Host`, honoured only over the authenticated hop
defined below — names a **host**, and LearnStack still resolves that host itself. The
header supplies a lookup key whose codomain is the contents of
`platform_host_to_tenant`; it does not supply an answer. `X-Tenant-Id` and
`X-Organization-Id` remain assertions in every topology and on every hop.

This is a real widening of what a header may *carry* and no widening at all of what a
header may *select*. It is required, not optional: without it the documented anonymous
SSR path has no tenant at all.

### Effective host and the trusted hop

The `Host` header is client-chosen — `AllowedHosts` is `"*"`, and it stays `"*"` because
tenant custom domains make a static list unmaintainable and LearnStack derives no
absolute URL from `Host`. So any claim that host is an "input the request cannot forge"
is **withdrawn**. What is trustworthy is the **lookup result** of a normalized host
against `platform_host_to_tenant`, and the ceiling that result carries.

**Effective host.** Exactly one type, `EffectiveHostAccessor`, computes it once per
request:

```text
trustedHop = socketPeer ∈ Tenancy:TrustedHop:Networks
             AND FixedTimeEquals(X-LearnStack-Hop-Secret, any configured secret)

raw        = trustedHop && exactly-one X-LearnStack-Host present
             ? X-LearnStack-Host
             : Request.Host.Value

effective  = EffectiveHost.Normalize(raw)     // null on any failure
```

Load-bearing details:

- **Both conditions, never either.** Network position alone fails on a Docker bridge or
  a pod CIDR, where everything in the mesh is the gateway's neighbour. The secret alone
  fails if it leaks into a bundle or a log. No single mistake is a tenant boundary —
  ADR-0003's posture applied to the input rather than the path.
- **`socketPeer` is read from `IHttpConnectionFeature.RemoteIpAddress`, never from
  `HttpContext.Connection.RemoteIpAddress` after `UseForwardedHeaders`.** With
  `XForwardedFor` enabled — which the API needs for rate limiting and audit —
  `Connection.RemoteIpAddress` becomes a client-supplied value for exactly the peers
  designated as the hop, and in SaaS the gateway legitimately forwards the visitor's
  address, so the check would be permanently false. The two lists stay separate:
  `ForwardedHeadersOptions.KnownNetworks` and `Tenancy:TrustedHop:Networks` answer
  different questions.
- **`ForwardedHeaders` never includes `XForwardedHost`.** That middleware overwrites
  `Request.Host` in place, collapsing "the host the socket was addressed to" and "a host
  a proxy claimed" into one indistinguishable property with no in-band signal.
- **A bespoke, single-valued header.** `X-Forwarded-Host` has append semantics, so a
  proxy in front of a client that already sent one produces a multi-valued header whose
  left-most entry is attacker-supplied. `X-LearnStack-Host` present more than once is
  **ignored entirely**. RFC 7239 `Forwarded` is never parsed anywhere.
- **An unauthenticated `X-LearnStack-Host` is ignored, not rejected**, so a corporate
  proxy that adds it is not a support ticket and a scanner gets no positive signal.
- **`-Secret`, not `-Key`.** `SensitiveTokenCatalog` carries `secret` as a redaction
  token and does not carry bare `key`, so existing Serilog and error-tracker redaction
  covers the header for free.

**Normalization is a total pure function.** `EffectiveHost.Normalize(string) → string?`
is the sole producer of both the lookup key and the `app.resolving_host` value. Every
failure returns `null` (⇒ unresolved ⇒ 404); nothing throws. Do **not** use
`HostString.FromUriComponent`: it performs an unwanted punycode *decode* and raises
`ArgumentException` on inputs such as `xn--`, `xn--a` and `a.xn--.b`, which would let an
anonymous remote client drive unhandled exceptions into the error tracker. The order is:
reject empty, over-253-character, whitespace, `/`, `@`, `%` or NUL → reject IPv6
literals by the `[`…`]` form and IPv4 literals by `IPAddress.TryParse` → strip a port
only when the tail after the last `:` is all digits → strip exactly one trailing dot,
reject two → `IdnMapping.GetAscii` inside a `catch (ArgumentException) ⇒ null` →
`ToLowerInvariant()`. Invariant lowering is stated explicitly because this team's
default culture is `tr-TR`, where `ToLower()` maps `I` to `ı` and would break every host
containing a capital I. Hosts are stored as A-labels: `türkçe.example.com` is stored as
`xn--trke-2oa7j.example.com`.

**Host classification applies to `/api/v1/*` only.** The non-classified prefixes are
enumerated and testable: `/healthz`, `/readyz`, `/openapi/*`, `/admin/hangfire*`, and
`/api/internal/*`. Within `/api/v1/*`, `HostClassificationMiddleware` produces exactly
one of:

- `TenantHost(T)` — a row with `organization_id IS NULL`, `is_active` and
  `is_publicly_live`.
- `OrgHost(T, O)` — the same, with an organization id.
- `PlatformHost` — the effective host is in `Tenancy:PlatformHosts`, a short static
  deployment list (`app.learnstack.dev`, `localhost`). This is the Studio / Portal entry
  host; it maps to no tenant, so it needs no row.
- otherwise **`UnknownHost` → 404** before any handler, counted on an unlabelled
  counter, never recorded durably.

**`is_publicly_live` is separate from `is_active`.** A row exists in
`platform_host_to_tenant` before DNS points at LearnStack — the custom-domain lifecycle
is submit → row → DNS instructions → activate — and the resolver cache holds a
deactivated mapping for its TTL. Without the distinction, guessing a hostname serves an
unlaunched tenant's pre-launch catalog, pricing and branding to a stranger, and a
released-then- re-registered domain serves the previous tenant's content for the cache
window. `HostOnly` requires `is_publicly_live`, and the resolver cache is invalidated on
the same transaction that flips either flag, not through `IEventBus`, whose default
implementation does not cross instances.

**Rate limiting comes first.** An ASP.NET in-process limiter keyed on the socket peer
runs **before** classification. Without it every novel `Host` costs one Postgres
transaction and one cache entry on an unauthenticated path. Unknown hosts are
negative-cached in a separately capped structure so a flood cannot evict real mappings.

### The reconciliation matrix

`T_h`/`O_h` come from H, `T_j`/`O_j` from J, `A` is the client assertions. Rows are
total over the signal space.

| # | Host class | Auth | Resolved | Origin | Failure |
|---|---|---|---|---|---|
| 1 | `UnknownHost` | any | — | — | **404** at classification. Counter only, no record |
| 2 | `TenantHost(T)` | none | `(T, null)` | `HostOnly` | Non-`[PublicSurface]` request → **404** |
| 3 | `OrgHost(T,O)` | none | `(T, O)` | `HostOnly` | as row 2 |
| 4 | any | `Authorization` present and **invalid** | — | — | **401** before the outcome is consumed. A rejected token is never treated as absence |
| 5 | any | `learnstack-hub`-realm token | — | — | **401** on every tenant-facing endpoint |
| 6 | `TenantHost(T)` | `(T, —)` | `(T, null)` | `HostAndClaim` | — |
| 7 | `TenantHost(T)` | `(T, O_j)` | `(T, O_j)` iff `O_j ∈ T` and M covers `(T, O_j)` | `HostAndClaim` | otherwise **404** + record |
| 8 | `TenantHost(T)` | `(T', …)`, `T'≠T` | **none** | — | **404** + record under `T`. This is the architecture/13 cross-check |
| 9 | `OrgHost(T,O)` | `(T, O)` | `(T, O)` | `HostAndClaim` | — |
| 10 | `OrgHost(T,O)` | `(T, —)` no org claim | `(T, O)` iff M covers `(T, O)` | `HostAndClaim` | otherwise **404** |
| 11 | `OrgHost(T,O)` | `(T, O_j)`, `O_j ≠ O` | **none** | — | **404** + record under `T` |
| 12 | `OrgHost(T,O)` | `(T', …)`, `T'≠T` | **none** | — | as row 8 |
| 13 | `PlatformHost` | none | **none** | — | Only `[AllowsUnresolvedTenantContext]` request types run. Everything else **404** |
| 14 | `PlatformHost` | `(T_j, O_j?)` | `(T_j, O_j?)` iff M returns an active membership covering it | `ClaimAndMembership` | otherwise **404** + record under `T_j` |
| 15 | `PlatformHost` | valid token, no tenant claim | **none** | — | Only `[AllowsUnresolvedTenantContext]` (e.g. `GET /api/v1/me/tenants`) |
| 16 | rows 2–15 | any | unchanged | unchanged | `A` equal → no effect. `A` different → **404** + record |
| 17 | non-HTTP | — | from `JobParams` / envelope | `Ambient` | Missing tenant fails at enqueue |

**Row 11 is a mismatch, not a scope change.** An earlier draft had the host's
organization win over a disagreeing claim, on the strength of
[ADR-0017](0017-tenant-organization-hierarchy.md)'s shareable branch links. That
citation describes `?org=<slug>` — a search parameter, which this ADR classifies as an
assertion trusted for nothing — so it does not support a host-wins rule. Shareable
branch links are served by an org-scoped host row, which is the same mechanism that
decides the anonymous organization scope. Making the disagreement a 404 also removes a
per-request durable write on a *happy* path: nothing re-issues the token, so the
disagreement would hold for the whole session and every subresource fetch would re-emit
the event.

> **Erratum — 2026-09-01.** The paragraph below says the `[PublicSurface]` set "is
> enumerated in the catalogue". It was enumerated nowhere, and "the catalogue" had three
> candidate referents in this corpus (architecture tests, audit coverage, permissions);
> shown by `git grep -n PublicSurface 803b381 -- docs/`, whose twelve hits across three
> files — this ADR, its row in the decisions index, and the architecture-tests catalogue
> — never name a marked request type. The catalogue's own entry sent the reader to "the
> catalogue's enumerated set", which is itself. The set now lives in
> [Standards 04 § Public surface](../standards/04-api-design.md), which this ADR's
> § Architecture tests already designates as the home of its day-to-day rules — so the
> location changed, not the rule. Every rule the paragraph states about the set is
> unchanged, and so is the Decision. Recorded in Amendment 3.

**`TenantContextOrigin` is the authority ceiling, and it is what makes a forged host
harmless.** `HostOnly` reaches only request types marked `[PublicSurface]` — the
corpus's existing `Portal Public` role, made mechanical — and `TenantContextBehavior` at
pipeline step 4 rejects anything else. The `[PublicSurface]` set is enumerated in the
catalogue with each entry's permitted methods; the default is `GET`/`HEAD`, an entry
declaring a mutating method must state why, no `[PublicSurface]` type may perform a
tenant-owned write, and no `[PublicSurface]` type may be classified MUST-class
`read-sensitive` — otherwise an anonymous `GET` becomes a durable standalone audit
write. Without the ceiling the trusted hop is a confused deputy: the BFF forwards the
visitor's own client-chosen `Host` under the API's trust credential, and the assertion
comparison cannot catch it because the edge derives its assertion from the same string.
With the ceiling, a forged host reaches exactly the pages that hostname already serves
to anyone who types it — and only while the row is publicly live.

**The cross-check is a fault detector, not an authorization control.** A client holding
a valid token for `T'` can address a `PlatformHost` and take row 14, where there is no
host to disagree with — which grants only their own tenant. This is stated so a later
reader does not treat a passing cross-check as evidence of attacker containment. The
control is that no signal outside the intersection can select a tenant.

**The anonymous organization scope is the host-mapping row**, not the tenant's
organization count. A tenant that wants its default organization's content on its public
site seeds `organization_id` into its `platform_host_to_tenant` row. That removes a code
branch and makes the behaviour visible, seedable and auditable as data.

`app.scope = 'tenant'` is derived from the actor's role plus a declared tenant-wide
operation. It is never set from a header, query parameter, cookie or body, and it is
unreachable under `HostOnly`.

### What the assertions do

`X-Tenant-Id` and `X-Organization-Id` bind to `Guid?`, are recorded, and are compared
against the value resolution produced:

- Present and **equal** to the resolved value → the request proceeds unchanged.
- Present and **different** → **404**, recorded per § Recording a rejected assertion.
  404 rather than 403 because saying "wrong tenant" confirms the other tenant exists.
- Present and **resolution produced nothing** → the request fails exactly as it would
  have without the header. An assertion never fills a gap it cannot fill. No durable
  record is written, because there is no tenant to write it under; the occurrence is
  counted on `learnstack_tenant_assertion_unresolved_total`.
- **Malformed or repeated** → **400 `validation_failed`**, counted, never stored as
  free text and never durably recorded. Attacker-authored strings do not reach
  `audit_log`. A header present more than once is refused rather than resolved by
  first-or-last, which is the classic header-confusion bug.
- An asserted organization must **belong to the resolved tenant**, confirmed by
  `IOrganizationScopeValidator` reading `organizations` by the composite key
  `(tenant_id, id)`. A valid organization id from another tenant is a mismatch, not an
  override.

### Recording a rejected assertion

The comparison lives in `TenantAssertionMiddleware`, registered after
`TenantResolverMiddleware` and (from Phase 02b) after `UseAuthentication`, before
`UseAuthorization`. Middleware rather than a MediatR behavior, because the assertion is
a property of the request binding: a non-MediatR endpoint must not be able to bypass a
tenant boundary, and the rejection must precede handler work.

**Which tenant the record carries: the resolved tenant, always.** `audit_log` is
tenant-owned, and `WriteStandaloneAsync` satisfies its `WITH CHECK` by issuing `SET
LOCAL app.tenant_id` from the draft. Writing the *asserted* id would mean setting that
GUC to an attacker-chosen value — handing an anonymous client a primitive that writes
rows into an arbitrary tenant's audit log. The resolved tenant is also the tenant whose
boundary was defended and whose admin needs to see the event, and `WITH CHECK` is then
satisfied by construction. The asserted values live in `metadata.assertedTenantId` /
`metadata.assertedOrganizationId`, typed as `Guid`, rendered as opaque identifiers.
**Nothing is ever written under a sentinel platform tenant id** — that would be an
unauthenticated, unbounded write into a pseudo-tenant no tenant admin watches. And when
resolution produced nothing there is no record at all, which is not a gap but the rule
this ADR already states.

**Two events, not one, because the two tiers have different amplification profiles.**

- `tenancy.tenant-assertion.reject` — `security-event`, **MUST**, outcome `denied`,
  written **for every occurrence** with **no coalescing and no per-tenant ceiling**, for
  mismatches carrying a **validated principal**. Bounded by token issuance and by the
  authenticated rate limit; the actor is the finding, so suppressing repeats would lose
  the signal. A per-tenant ceiling would be worse than the flood it prevents: ten cheap
  requests against a tenant of the attacker's choosing would silence every subsequent
  authenticated mismatch against that tenant for the window.
- `tenancy.tenant-assertion.anonymous-burst` — `security-event`, **MUST**, written once
  per `(resolved tenant, dimension, window)` when the anonymous mismatch counter crosses
  a configured threshold. An anonymous mismatch is **not itself** an audited operation;
  the burst is. This follows the precedent already in the [Standards
  18](../standards/18-audit-coverage.md) baseline table — "login failure burst beyond
  rate limit", not every failed login — so **no change to the MUST legend is required**:
  "every occurrence" of a burst event is every crossing.

Both keys are registered in the Security row of the baseline coverage table, which a
tenant `AuditConfig` may never narrow. An attacker who compromises one tenant admin must
not be able to switch off the detector that would catch the next cross-tenant probe.

**Burst state is in-process and never in `ICacheService`.** A cache outage must not
decide whether a MUST-class security event is recorded. Counters are keyed only by
`(resolved tenant, dimension)`, so cardinality is bounded by the tenant count, they are
held in a structure with no eviction inside a live window, and they are reclaimed only
when their window expires. The effective threshold is therefore per instance, which errs
toward *more* auditing; that is stated rather than hidden.

**Metrics** — `learnstack_` prefix, no PII and no attacker-chosen label:
`learnstack_tenant_assertion_mismatch_total{tenant,dimension,source,authenticated}`
(resolved requests only), `learnstack_tenant_assertion_unresolved_total{source}` (no
tenant label, because there is no tenant), and
`learnstack_host_classification_rejected_total` (unlabelled — the unknown-host set is
unbounded by definition). The effective host and the source IP are **never** labels.

**The response.** A mismatch returns **404**. The Problem Details `code` is
`tenant_mismatch` for an authenticated caller and `not_found` for an anonymous one. Both
already map to 404. The split closes an oracle the distinct code would otherwise open:
mapped host plus a wrong GUID yields `tenant_mismatch`, an unmapped host yields
`not_found`, and retrying without the header yields `not_found` in both cases — so the
header *adds* the distinguishing bit rather than reproducing it, anonymously, against
the property [Standards 05](../standards/05-database.md) states as "the application role
cannot enumerate the customer list". The diagnosability this ADR keeps the header for is
served by the record and the counter, which is where a first-party operator looks.

**A failed record does not change the response.** A rejected assertion has neither an
uncommitted-but-unaudited state change nor an ungranted-but-unaudited disclosure, so
fail-closed has nothing to protect here, while a 503 appearing under load an anonymous
attacker can generate is a remotely triggerable availability signal on a rejection path.
The failure logs at `Critical`, increments the standalone-write-failure counter, and
marks the audit health check unhealthy; beyond a configured window it fails closed at
the deployment level rather than serving indefinitely unrecorded. **This narrows the
unconditional rule in [ADR-0033 § Decision](0033-audit-durability-model.md)** — "a
MUST-class row that cannot be written durably (`audit_unavailable`, HTTP 503)" — for the
standalone case only, and it rests on **[ADR-0033 Amendment
1](0033-audit-durability-model.md)**, accepted alongside this ADR.

### The platform-admin override is not a resolution source

The third entry [Standards 04](../standards/04-api-design.md) carries — "explicit
override in `/api/v1/platform/...` endpoints with proper scope" — is **removed from the
resolution model**. It sits on no defined identity surface: operators authenticate
against the `learnstack-hub` realm ([ADR-0004 Amendment
1](0004-authentication-strategy.md)), their tokens are refused on every tenant-facing
endpoint, and the Hub↔LearnStack chain uses a Hub service-account client rather than an
operator JWT. A resolution list is also the wrong place to introduce a cross-tenant
capability: it would re-adopt, sideways, [ADR-0019 § Option B](0019-learnstack-hub.md),
which was rejected on its merits.

Operator-initiated cross-tenant work enters through `/api/internal/*` under the Hub auth
chain ([ADR-0034](0034-hub-contract-surface-invariant.md)). LearnStack-side cross-tenant
data access runs through the audited `EnterPlatformAdminScope(reason)` ([ADR-0003
Amendment 3](0003-tenant-isolation-defense-in-depth.md)), which resolves no tenant
because it runs with none, and which requires an authenticated principal holding a
Platform-scope permission, checked before the scope opens.

**Whether a `/api/v1/platform/*` URL space should exist at all is a separate question
this ADR does not settle.** Two documents already place endpoints there — [ADR-0013 §
Consequences](0013-page-block-schema-versioning.md) (`POST
/v1/platform/blocks/{key}/migrate`) and [architecture/20](../architecture/20-search.md)
(`POST /v1/platform/search/reindex`) — and neither is implemented. Settling their fate
means amending an Accepted ADR, so it gets its own decision record rather than riding
along in this one. What this ADR fixes is narrower and sufficient: **no route, in any
URL space, resolves a tenant from a request-supplied override.**

### There is no Development override

An earlier draft of this ADR carried a configuration flag that let the headers act as
the resolution source under `DeploymentMode.Development`. It is **retired before it
shipped**. Its only purpose — letting an integration test or a `curl` against a
workstation pick a tenant — is served by the trusted hop, whose loopback network and dev
secret let a `curl` supply an effective host that goes through the real resolver, the
real policy and the real matrix. There is now **no code path anywhere that assigns
`ITenantContext.TenantId` from a header**, in any mode, which is a stronger statement
than a mode guard and is what makes `Tenant_Headers_Are_Never_A_Resolution_Source` mean
something. Keeping it would also have meant [Phase
02d](../roadmap/phase-02d-walking-skeleton.md) running its browser demo in the one
configuration where the assertion detector records nothing by construction.

Retiring it also closes a defect the mode guard could not: `Deployment:Mode` ships as
`"Development"` in `backend/src/LearnStack.Api/appsettings.json`, the file that goes to
every environment, with the same value as the code default in `Program.cs` — and
`appsettings.Development.json` sets no `Deployment` key at all. Every
Development-guarded mechanism is therefore on by default in a deployment that never sets
the key. That inversion is corrected in the same change: the key moves to
`appsettings.Development.json` and the composition root throws when it is absent.

### `X-Correlation-Id`

Not governed by this ADR. It carries no authority over what a request may read, is
echoed-or-generated rather than trusted, and is governed by [Standards
10](../standards/10-observability.md).

## Context

### Why the hop conveys a host and not a tenant

An earlier draft rejected the authenticated hop for a reason that was factually wrong,
and that reason is withdrawn. It said a shared secret "would need `ISecretProvider` with
a real backend, which is itself demand-gated to Phase 11". `ISecretProvider` and
`ConfigurationSecretProvider` shipped in **Packet 3**, and
[ADR-0035](0035-demand-gated-infrastructure.md) records the latter as the working
default. Nothing new has to land for a shared secret to exist. Only the APISIX half of
that paragraph stands, and it is not load-bearing here.

What is rejected is the *shape* option 2 proposed, and the reason is a difference in
kind rather than in degree:

- Trusting a hop to supply a **tenant id** grants unbounded selection across the whole
  tenant set, and it destroys the only automated detector for a BFF, a cache, or a host
  mapping that resolved a different tenant than the API did — the failure this ADR keeps
  the header to catch, which is silent by construction because the response is a valid
  page for the wrong tenant.
- Trusting a hop to supply a **host** grants selection only inside
  `platform_host_to_tenant`, and LearnStack still performs the resolution. The assertion
  comparison keeps its whole diagnostic value, because the tenant the API resolves and
  the tenant the BFF asserted are still two independently produced values.

We take the strictly weaker grant. Rotation of the hop secret is a **redeploy**, not a
configuration reload: [Standards 20](../standards/20-infrastructure-stack.md) fixes that
every secret is bound at startup through `IOptions<T>`, and changing one under
`ConfigurationSecretProvider` is a redeploy. Rotation without a redeploy arrives with
ADR-0035's Vault trigger, in Phase 11; the hop secret is an instance of that trigger and
is named as one.

### Why option 4 was rejected

It is a tenant-crossing vulnerability with a plain-English exploit: set a header, read
another tenant's data. It reads as safe only if the reader assumes a gateway that
ADR-0035 explicitly did not schedule. This is the same shape of error as the Row Level
Security template corrected by [ADR-0003 Amendment
3](0003-tenant-isolation-defense-in-depth.md) — a mechanism that satisfies every
structural check while leaking across tenants at runtime — and the resemblance is worth
naming, because both survived review by looking like the surrounding correct code.

### Why option 5 was rejected

It is safe and it is lossy on two counts. The mismatch check is the only automated
detector for a BFF that resolved a different tenant than the API did, and those failures
are silent by construction. And banning the header without replacing it with the host
hop breaks the anonymous SSR path outright — which is the failure the first draft of
this ADR actually shipped.

### What would change our minds

- The gateway lands and terminates every non-development request, with a documented
  `proxy-rewrite` rule that strips inbound `X-Tenant-Id`, `X-LearnStack-Host` and
  `X-LearnStack-Hop-Secret` from client traffic. At that point the CORS `allow_headers`
  list in [architecture/30](../architecture/30-api-gateway.md), which today includes
  `X-Tenant-Id` and `X-Organization-Id`, should drop them: a browser has no legitimate
  reason to send an assertion cross-origin.
- A deployment topology where the BFF is the only reachable client of the API and is
  authenticated as such. Option 2's shape stays rejected regardless; what changes is how
  the hop is proved.

### What is out of scope, and what is not

- **`/api/internal/*` is not punted — it is a different surface with its own
  resolver.** Its tenant comes from the `{id}` path segment of an envelope verified by
  mTLS + RS256 JWT + HMAC ([ADR-0034](0034-hub-contract-surface-invariant.md)), and
  `HubCorrelationMiddleware` populates the context. `POST /api/internal/tenants` has no
  `{id}` and runs with no tenant context, carrying `[AllowsUnresolvedTenantContext]`.
  Host classification does **not** apply to that prefix.
- **Which store answers "does this organization belong to this tenant".** Packet 7's
  `IOrganizationScopeValidator`, reading `organizations` by the composite key
  `(tenant_id, id)` in its own short read-only transaction that sets `app.tenant_id` as
  its first statement — the same pattern `CachedHostToTenantResolver` uses for
  `app.resolving_host`. This ADR fixes the rule, not the query plan.
- **The Keycloak claim shape.** Phase 02b owns whether the active tenant travels as a
  `tenant_id` claim, a `memberships` array, or both. Nothing here depends on the answer,
  because a signed set of memberships is still not a selector: choosing among them
  requires a re-issued token, never a header.
- **Per-tenant API rate limits.** The platform-wide in-process limiter is a Packet 4
  deliverable of this ADR; the plan-differentiated limits of [Standards 11 § Rate
  Limiting](../standards/11-security.md) are not.

## Consequences

### Positive

- The absence of the API gateway stops being a security dependency. Packet 4 through
  Phase 11 is safe by construction rather than safe by deployment assumption.
- The documented anonymous SSR path works, and works without `X-Tenant-Id` being
  present at all — which is the property Phase 02d's browser test asserts.
- A misconfigured BFF, a stale host mapping, or a cache serving a cross-tenant page
  fails loudly with a 404 and a record, instead of serving the wrong tenant's data with
  every isolation layer reporting success.
- The four conflicting statements in the corpus collapse to one, and Standards 04,
  architecture/09, architecture/13 and the Phase 02a scope all link to the same place.
- The in-process rate limiter that [architecture/30](../architecture/30-api-gateway.md)
  has promised since Phase 01 acquires an owner and a packet.

### Negative

- Every request carrying the header pays a comparison, and the comparison needs the
  resolved value — so the check sits after resolution, not at the edge where a header
  check intuitively belongs.
- **Packets 4 through 8 record a rejected assertion to a log, not to `audit_log`.**
  `IAuditStore` lands in Packet 9. The corpus says "recorded" in that window and
  "audited" only from Packet 9. A log line that is honestly a log line is better than an
  audit row that does not exist.
- **The trusted hop is one configuration key away from being wrong.** Both conditions
  are required, and the composition root refuses to start outside `Development` when the
  secret list is empty or any entry is under 32 bytes. In local development and in the
  Phase 02d default topology the real boundary is the loopback bind, not the secret;
  that is stated rather than dressed up.
- **`DenyAllTenantMembershipReader` makes the Studio tenant switcher return 404 for
  everyone until Phase 03.** That is correct and it will look like a bug. It is named in
  the Phase 02a scope with its error code so nobody makes the default permissive to
  unblock a demo.
- **The anonymous organization scope now comes from the host-mapping row, not from the
  tenant's organization count.** Phase 02d's two seed tenants must set `organization_id`
  on their host rows deliberately, or one of the two sites renders only tenant-wide
  content and a reviewer reads that as a query-filter bug.
- **Two pre-pipeline transactions can precede the pipeline on a cold cache** — the host
  lookup and, when an organization is asserted or claimed, the scope validation. Both
  are cached; the negative cache for unknown hosts is separately capped.

### Neutral

- **The BFF changes twice, and both changes are small.** It stops treating
  `X-Tenant-Id` as load-bearing (it never was, downstream), and it starts stating the
  visitor host over the hop. `frontend/packages/sdk/src/server.ts` is the only place in
  the frontend that sets the hop headers.
- `X-Correlation-Id` is unaffected; it never carried authority.
- `platform_host_to_tenant` remains the only authority for `host → (tenant_id,
  organization_id?)`. `Tenancy:PlatformHosts` is a configuration list of hosts that map
  to **no** tenant, so it is not a second mapping authority and does not contradict
  [Standards 20 § Host → Tenant Resolution](../standards/20-infrastructure-stack.md).

## Implementation Notes

### Staging across packets

| Stage | What exists | What a mismatch produces |
|---|---|---|
| **Packet 4** | Rate limiter, effective host, normalizer, trusted hop, assertion comparison, `LoggingTenantAssertionRecorder`. No resolver, no claims, no `IAuditStore` | 404 + metric + `Warning` log. Unreachable in traffic — every request is already rejected by the unresolved-context guard — and exercised by unit tests over a stubbed context. **Packet 4 must not describe the outcome as audited.** |
| **Packet 6** | `platform_host_to_tenant` with `UNIQUE (host)`, the normalization `CHECK`, `is_publicly_live` | unchanged |
| **Packet 7** | Classification, resolver, `TenantContextFactory`, `TenantContextOrigin`, `IOrganizationScopeValidator`, `DenyAllTenantMembershipReader` | 404 + metric + `Warning`. Matrix rows 2, 3, 6, 9, 10 become live; rows 7 and 14 fail closed until Phase 03 — see the erratum below |
| **Packet 9** | `IAuditStore`, `audit_log`, `AuditingTenantAssertionRecorder` | MUST-class rows begin. `tenancy.tenant-assertion.reject` per occurrence for authenticated callers; `tenancy.tenant-assertion.anonymous-burst` per window |
| **Phase 02b** | Keycloak, `UseAuthentication`, the `tenant_id` claim | The H↔J cross-check goes live through the **same** recorder and the **same** operation key with `metadata.assertionSource = jwt-claim`. Phase 02b's completion criterion is met by this mechanism, not by a second detector |
| **Phase 03** | `Membership` | Rows 7 and 14 stop failing closed |

> **Erratum — 2026-09-02.** The Packet 7 row says matrix rows **2, 3, 6, 9, 10** become
> live. Rows 6, 9 and 10 do not: all three require a validated claim in their Auth
> column, and the paragraph immediately below this table says so — "the authenticated
> tier is dormant before Phase 02b — there is no `UseAuthentication` to be ordered
> after". Shown by `grep -rn UseAuthentication backend/src`, whose only hit is a comment
> saying it does not exist yet. The rows Packet 7 makes live are **1, 2, 3 and 13**, and
> it makes **16** reachable for the first time — the assertion comparison shipped in
> Packet 4 with nothing resolved to compare against. Row 1 is on the list because host
> classification is itself Packet 7's; it is decided before a `TenantResolutionAttempt`
> exists, which is why the factory's suite does not cover it. Rows 6, 9 and 10 become
> live in **Phase 02b**; 7 and 14 need Phase 02b to be reachable at all and Phase 03 to
> stop failing closed. The table's own Packet 4 row draws exactly this distinction —
> "unreachable in traffic" — and the Packet 7 row did not. Nothing about what the rows
> *decide* changes: `TenantContextFactory` decides the **twelve** rows expressible as a
> `TenantResolutionAttempt` — 2, 3 and 6–15 — and Packet 7 tests every one of them as a
> pure function. The other five are decided elsewhere and always will be: row 1 at host
> classification, rows 4 and 5 by an authentication outcome nothing implements yet, row
> 16 by `TenantAssertionMiddleware`, row 17 by `EventTenantContext.FromEnvelope`.
> Recorded in Amendment 5.

The authenticated tier is dormant before Phase 02b — there is no `UseAuthentication` to
be ordered after, `AuthorizationBehavior.Handle` is `return next()`, and the
`authenticated` metric label is constant-false. That is staged explicitly rather than
left for an implementer to discover: the ordering rule and its test are Phase 02b
deliverables, named in Phase 02b, and the anonymous tier is the only live tier until
then.

The Packet 7 → Packet 9 window is bounded by packet order inside one phase, not by a
demand trigger. A slip is a phase-exit blocker, not a tolerated state: a detector whose
output is not retained under audit retention is not a detector anyone can rely on.

### Rules

> **Erratum — 2026-08-30.** The second bullet below names the member
> `ITenantContextAccessor.SetTenant`. There is no such member and never was: the
> interface shipped on 2026-05-21 in Phase 02a Packet 3 carrying
> `ITenantContext? Current { get; set; }` and nothing else, exactly as its owning
> [ADR-0032 § Sub-decision 10](0032-exception-handling-logging-and-observability.md)
> specifies; shown by `backend/src/LearnStack.SharedKernel/Tenancy/ITenantContextAccessor.cs`
> and by `grep -rn SetTenant backend/src`, whose only hits are the unrelated
> `IUnitOfWork.SetTenantContextAsync`. Read the bullet as governing **writes to
> `ITenantContextAccessor.Current`**; what it decides — exactly four callers, and
> `EnterPlatformAdminScope` not among them — is unchanged. Current authority:
> [ADR-0032 § Sub-decision 10](0032-exception-handling-logging-and-observability.md)
> for the member, this bullet for the caller set. Recorded in Amendment 2.

- **Never** assign `ITenantContext.TenantId` or `OrganizationId` from a bound header.
  There is no exception and no mode in which there is one.
- `ITenantContextAccessor.SetTenant` has exactly the four callers the corpus already
  enumerates in [Phase 02a](../roadmap/phase-02a-kernel-tenancy.md):
  `TenantResolverMiddleware` (HTTP), `HubCorrelationMiddleware` (`/api/internal/*`), the
  Hangfire `JobActivator` (jobs), and the outbox / inbox handler scope (integration
  events). `EnterPlatformAdminScope` is **not** one of them — it opens a second
  connection and sets no tenant context.
- `TenantContext` is sealed with no public constructor. The single entry point is
  `TenantContextFactory.Create(TenantResolutionAttempt) → Result<TenantContext>`, which
  returns `Result.Fail` on any disagreement and never a partially populated context.
- **Pipeline placement.** Host classification runs before authentication — it must, to
  reject an unknown host cheaply — and context construction runs after it, so
  `TenantContextFactory.Create` is called once with both signals in hand. This splits
  the single step [architecture/27](../architecture/27-custom-domain-tls.md) currently
  describes as the resolver running "first, before JWT validation"; that sentence is
  amended rather than contradicted.

### Architecture tests

All catalogued in
[21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md)
with the **Phase** field filled in:

| Test | Lands |
|---|---|
| `Tenant_Headers_Are_Never_A_Resolution_Source` | Packet 4 |
| `Effective_Host_Computed_In_One_Place` (Roslyn analyzer) | Packet 4 |
| `Forwarded_Host_Header_Is_Never_Read_Directly` | Packet 4 |
| `Trusted_Hop_Requires_Network_And_Secret` | Packet 4 |
| `Trusted_Hop_Reads_The_Socket_Peer` | Packet 4 |
| `Deployment_Mode_Is_Required_Configuration` | Packet 4 |
| `Assertion_Recorder_Is_The_Only_Mismatch_Writer` | Packet 4 |
| `Assertion_Budget_Does_Not_Depend_On_ICacheService` | Packet 4 |
| `Api_Registers_Only_The_Tenant_Realm_Authority` | Phase 02b |
| `Host_Classification_Applies_To_Tenant_Facing_Routes_Only` | Packet 7 |
| `TenantContext_Is_Constructed_Only_By_The_Factory` | Packet 7 |
| `SetTenant_Callers_Are_The_Enumerated_Four` | Packet 7 |
| `PublicSurface_Marker_Set_Is_Enumerated` | Packet 7 |
| `PublicSurface_Requests_Are_Never_ReadSensitive` | Packet 7 |
| `Organizations_Are_Read_By_Composite_Key` | Packet 7 |
| `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input` | Packet 7 |
| `PlatformAdminScope_Entry_Requires_Platform_Permission` | Packet 7 |

`Effective_Host_Computed_In_One_Place` is a **Roslyn analyzer**, not NetArchTest:
NetArchTest resolves type references, and three of the four inputs it bans appear only
as string literals inside header lookups. It covers `HttpRequest.Host`,
`RequestHeaders.Host`, `HeaderDictionary` indexers with a `Host` / `X-Forwarded-Host` /
`X-LearnStack-Host` / `Forwarded` literal, and `UriHelper.GetDisplayUrl` /
`GetEncodedUrl`.

**Static tests are not the proof.** Data flow from a header into a tenant context is not
reliably provable by a type-reference scan — a helper, an interface, or an indirect
assignment slips past one. The binding evidence is the runtime matrix in [Phase
02a](../roadmap/phase-02a-kernel-tenancy.md), executed against a live PostgreSQL
connected as **`learnstack_app`**, covering at minimum: unknown host with a spoofed
tenant header; host A with asserted tenant B; host A with a JWT for tenant B; an
organization assertion from another tenant; a tenant-only host with an organization
claim; an org-host and claim disagreement; a duplicate and a malformed header; a direct
socket bypassing the hop; the normalization table including `xn--`, `a.xn--.b`, a
trailing dot, an IPv4 literal and a 300-character host; a `learnstack-hub` token on
`/api/v1/*`; and the Phase 02d anonymous two-host browser render.

- Day-to-day rules live in [Standards 04 § Tenant
  Context](../standards/04-api-design.md), which links here. [Standards 11 § Tenant
  Context](../standards/11-security.md) is the authority for RLS session-variable
  **placement** only; it also records that `app.resolving_host` carries the normalized
  effective host.

## Amendments

### 2026-08-20 — Amendment 1: the normalization order, corrected by measurement

The Decision is unchanged: `EffectiveHost.Normalize` is still the sole producer of
the lookup key and of `app.resolving_host`, still total, still returns `null` on
every failure, and still never throws. What this amendment corrects is the
**order of steps** written above, which Packet 4 found to be wrong in two
security-relevant ways when it implemented them.

**1. IPv4 rejection must come *after* the port is stripped, not before.**

The order as written rejects IPv4 literals by `IPAddress.TryParse` and only then
strips a port. Measured on .NET 10:

```text
IPAddress.TryParse("1.2.3.4:443")   => False
IPAddress.TryParse("1.2.3.4")       => True
IPAddress.TryParse("0x7f.1")        => True   (127.0.0.1)
IPAddress.TryParse("2130706433")    => True   (127.0.0.1)
```

So `1.2.3.4:443` passes the IPv4 gate — it is not a parseable address *with* the
port attached — and the next step removes the port, leaving `1.2.3.4` as an
accepted host name. The rejection this ADR asks for is bypassed by appending a
port. Stripping first and parsing second closes it, and also catches the
dotted-hex and integer spellings above, which the written order never reached.

**2. The character rejection must be an output whitelist, not only an input
denylist.**

The order as written scans the *raw input* for whitespace, `/`, `@`, `%` and NUL,
and ends at `ToLowerInvariant()`. Measured: `IdnMapping.GetAscii` performs a
compatibility mapping, so U+FF0F FULLWIDTH SOLIDUS arrives as a literal `/`,
U+FF20 as `@`, and U+FF05 as `%` — *after* the input scan has already run. The
function would therefore return a "normalised host" containing exactly the
characters it promises to reject, on its way to becoming a SQL lookup key and a
session variable. `;`, `'` and `"` were never on the input list at all.

Normalization therefore ends with a **whitelist over the produced value**:
letters, digits, hyphen and dot only, with no label starting or ending in a
hyphen. A whitelist is the right shape and a denylist never was — the set of
characters a hostname may contain is small and closed, and the set it may not is
neither.

> **Erratum — 2026-09-02.** The corrected order below still lets an IPv4 literal
> through, by the same mechanism it was written to close. It places **reject IPv4
> literals** before **strip exactly one trailing dot**, so `1.2.3.4.` reaches the
> strip as a name and leaves it as a literal — and `GetAscii`'s compatibility
> mapping folds U+3002 and U+FF0E into `.` after that. Measured on the shipped
> transcription: `1.2.3.4.`, `1.2.3.4.:443`, `127.0.0.1.`, `9.`, `2130706433.` and
> `010.010.010.010.` were all returned as accepted hosts, and every one then threw
> in `CacheKey.ForHostMapping` — a `500` and an error-tracker capture per request,
> from an unauthenticated caller, where a bodyless `404` was designed. The IPv4
> refusal belongs on the **produced value**, beside the character whitelist, for
> the reason point 2 below already gives for the whitelist. Recorded in
> Amendment 4.

**Corrected order.** Reject empty, whitespace-only, or over-253-character input →
reject the input outright if it contains whitespace, `/`, `@`, `%`, NUL, `\`, `?`
or `#` (a superset of the original list; the last three are equally not part of a
name) → reject IPv6 literals by the `[`…`]` form → **strip a port** when the tail
after the last `:` is all digits → **reject IPv4 literals** by `IPAddress.TryParse`
→ strip exactly one trailing dot, reject two → `IdnMapping.GetAscii` inside
`catch (ArgumentException) ⇒ null` → `ToLowerInvariant()` → **return only if the
result is letters, digits, hyphen and dot with no leading or trailing hyphen in
any label**, otherwise `null`.

Everything else in § Normalization stands, including the prohibition on
`HostString.FromUriComponent` and the reason invariant lowering is stated
explicitly.

Implemented in `LearnStack.SharedKernel.Tenancy.EffectiveHost`; covered by
`EffectiveHostTests`.

### 2026-08-30 — Amendment 2: the accessor member is `Current`, not `SetTenant`

**What was wrong.** § Rules' second bullet opens
"`ITenantContextAccessor.SetTenant` has exactly the four callers…". No member of
that name has ever existed on that interface, so the rule as written governs
nothing.

**How it was shown.** `backend/src/LearnStack.SharedKernel/Tenancy/ITenantContextAccessor.cs`
declares exactly one member, `ITenantContext? Current { get; set; }`, and has
since it shipped in Phase 02a Packet 3 on 2026-05-21 — three months before this
ADR was accepted. `grep -rn SetTenant backend/src` returns only
`IUnitOfWork.SetTenantContextAsync`, a different member on a different type with
a different job. The member this ADR should have named is fixed by
[ADR-0032 § Sub-decision 10](0032-exception-handling-logging-and-observability.md),
which decided the accessor's shape and is unchanged by this amendment.

**Why the code is not the thing corrected.** The shipped shape is the one its
owning ADR specifies, and the property setter is what the already-shipped fourth
caller needs: `InProcessEventBus` saves the previous context, writes its own, and
restores the previous — possibly `null` — on the way out. A `void SetTenant(ctx)`
cannot express that save-and-restore, so renaming the code to match this ADR
would break a working caller to satisfy a naming error.

**Every carrier changed.** This ADR (the inline erratum in § Rules and this
amendment);
[Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md),
where `SetTenant_Callers_Are_The_Enumerated_Four` is restated against writes to
`Current`; and [architecture/09 Tenant Isolation](../architecture/09-tenant-isolation.md),
which spells `SetTenant(...)` in four places. The test's canonical **name** is
unchanged — the catalogue's § Canonical names rule makes a rename its own
liability, and the name describes the caller set, which is what this ADR
actually decides.

**The Decision is unchanged.** Exactly four callers populate the ambient tenant
context — `TenantResolverMiddleware` (HTTP), `HubCorrelationMiddleware`
(`/api/internal/*`), the Hangfire `JobActivator` (jobs), and the outbox / inbox
handler scope (integration events) — and `EnterPlatformAdminScope` is not among
them, because it opens a second connection and sets no tenant context.

### 2026-09-01 — Amendment 3: where the `[PublicSurface]` set is enumerated

**What was wrong.** § The reconciliation matrix says the `[PublicSurface]` set "is
enumerated in the catalogue with each entry's permitted methods". It was enumerated in
no file, and "the catalogue" is not a resolvable referent: this corpus uses the word for
the architecture-tests catalogue, the audit-coverage catalogue and the permission
catalogue. A rule that reads against a set nobody wrote down cannot be implemented, and
`PublicSurface_Marker_Set_Is_Enumerated` is a Packet 7 deliverable that has to.

**How it was shown.** `git grep -n PublicSurface 803b381 -- docs/` — this ADR's
acceptance commit — returns twelve hits in three files: this ADR, its one-line row in
the decisions index, and two entries in the architecture-tests catalogue. Not one of
them names a marked request type or its permitted methods. The catalogue's
`PublicSurface_Marker_Set_Is_Enumerated` asserted that every marked type "appears in the
catalogue's enumerated set", which is a rule reading against itself; and the catalogue
disclaims owning rule content in its own opening section, so it was never the home.

**Every carrier changed.** This ADR (the inline erratum in § The reconciliation matrix
and this amendment); [Standards 04 § Public surface](../standards/04-api-design.md),
which now holds the marker's rules and the enumeration table — shipped **empty**, taking
its first rows with Phase 02d's two anonymous read endpoints; and
[the architecture-tests catalogue](../standards/21-architecture-tests-catalogue.md),
where both `PublicSurface_*` entries now point at Standards 04 rather than at
themselves. § Architecture tests of this ADR already named Standards 04 § Tenant Context
as the home of these day-to-day rules, so the correction moves the set to where this ADR
had already sent the reader.

**The Decision is unchanged.** Every rule about the set holds exactly as written: the
default is `GET`/`HEAD`, a mutating entry states why, no `[PublicSurface]` type performs
a tenant-owned write, and none is classified MUST-class `read-sensitive`.

### 2026-09-02 — Amendment 4: the IPv4 refusal belongs on the produced value

**What was wrong.** Amendment 1's corrected order places **reject IPv4 literals**
before **strip exactly one trailing dot**. Both steps were already in that order
when the amendment was written, so the bypass it exists to close was open in the
order it published: `1.2.3.4.` reaches the strip as a name and leaves it as a
literal.

**How it was shown.** Measured against the shipped transcription — the whole point
of a step order is that it is transcribed — `EffectiveHost.Normalize` returned
`1.2.3.4.`, `1.2.3.4.:443`, `127.0.0.1.`, `9.`, `2130706433.` and
`010.010.010.010.` as accepted hosts. Each then threw `ArgumentException` in
`CacheKey.ForHostMapping`, which `CachedHostToTenantResolver` calls as its first
statement, producing a `500` and an unsampled `IErrorTrackingProvider` capture per
request from an unauthenticated caller — where this ADR's own § The reconciliation
matrix row 1 specifies a bodyless `404`. The throw also precedes the negative
cache, so repeats never coalesce, and because only a host that reaches the
resolver can produce it, the `500` is a positive host-existence oracle against the
indistinguishability row 1 exists to provide.

**The general form, which is the part worth keeping.** Amendment 1 already made
this argument once, for the character set: "the character rejection must be an
output whitelist, not only an input denylist", because `GetAscii`'s compatibility
mapping produces characters the input scan never saw. The IPv4 refusal is the same
shape and was left on the input side. **Every rejection in `Normalize` is a
predicate on the produced value**; an input-side check is an optimisation, and an
optimisation that is also the only check is a gate the next normalization step
walks around. Two later steps can produce a literal the early check never saw —
the trailing-dot strip, and `GetAscii` folding U+3002 and U+FF0E into `.`.

**Every carrier changed.** This ADR — the inline erratum beside Amendment 1's
corrected order, and this amendment. `EffectiveHost` re-runs
`IPAddress.TryParse` on the value it is about to return, keeping the early check
as the cheap exit it always was. `EffectiveHostTests` gains the six spellings
above and, new, the pairing property nothing asserted: for every input `Normalize`
accepts, `CacheKey.ForHostMapping` must not throw — the two validators are the
same idea written apart, and checking either alone is how they drifted.
[Standards 21](../standards/21-architecture-tests-catalogue.md)'s
`Effective_Host_Normalization_Is_Total` cites this amendment beside Amendment 1 and
carries the pairing property, which is a distinct invariant and belonged in the
catalogue on its own account.

**The Decision is unchanged.** `EffectiveHost.Normalize` is still the sole
producer of the lookup key and of `app.resolving_host`, still total, still returns
`null` on every failure, and still never throws.

### 2026-09-02 — Amendment 5: which rows Packet 7 actually makes live

**What was wrong.** § Staging across packets' Packet 7 row claims matrix rows 2, 3, 6,
9 and 10 "become live". Rows 6, 9 and 10 each require a validated claim, and no packet
before Phase 02b has one. The sentence was false when it entered the record: the same
subsection's next paragraph already stated that the authenticated tier is dormant until
Phase 02b, and its own Packet 4 row already used the words "unreachable in traffic" for
precisely this distinction.

**How it was shown.** `grep -rn UseAuthentication backend/src` returns one hit, a comment
in `TenantAssertionMiddleware` noting that there is none to be ordered after. The Auth
column of rows 6, 9 and 10 reads `(T, —)`, `(T, O)` and `(T, —)` — a claim in every case.

**The corrected reading.** Packet 7 makes rows **1, 2, 3 and 13** live and row **16**
reachable. Row 1 — an unknown host, 404 at classification — is Packet 7's own: before
this packet the pipeline ran from the OpenAPI document straight to the assertion
comparison, with no classification step at all. Row 16 is the one worth naming: the assertion comparison shipped in Packet 4
against a context that never resolved, so every comparison was vacuous; Packet 7 is what
gives it a resolved value to disagree with.

**Why the distinction is worth an amendment rather than a shrug.** "Live" is the word a
later reader uses to decide whether a green suite is evidence. A packet that believes it
made the authenticated rows live will read `DenyAllTenantMembershipReader`'s untouched
code path as proof that rows 7 and 14 fail closed, when in fact nothing can reach the
call at all. Packet 7 tests, as a pure function of `TenantResolutionAttempt`, the twelve rows that
are expressible as one — 2, 3 and 6–15 — which is the honest form of that evidence and
is not the same claim. The remaining five are not the factory's and never will be: row 1
is decided at host classification, rows 4 and 5 by an authentication outcome, row 16 by
`TenantAssertionMiddleware` and row 17 by `EventTenantContext.FromEnvelope`. A later
reader deciding where rows 4, 5, 16 or 17 belong should not conclude from this amendment
that the factory already has them.

**Every carrier changed.** This ADR — the inline erratum beside the staging table, and
this amendment. No other document reproduces the row list;
[Phase 02a](../roadmap/phase-02a-kernel-tenancy.md) points here rather than restating it,
and the Packet 7 delivery record, when it is written at packet close, states the
corrected set directly rather than the staging table's original.

**The Decision is unchanged.** The matrix, the signals, the ceiling and the staging
order all stand; only the claim about which rows traffic can reach in Packet 7 is
corrected.

### 2026-09-03 — Amendment 6: `Tenancy:PlatformHosts` precedence, and who owns the check

**Why this is an amendment and not body text.** An earlier draft of Packet 7 wrote this
into § Decision directly. It does not merely explain the decision — it assigns an
obligation, and code now enforces it — so it is a change to what this ADR requires, and
Accepted ADRs take those as dated amendments
([Documentation Standards § Correcting and Amending ADRs](../standards/13-documentation.md)).

**The precedence.** A host on the static `Tenancy:PlatformHosts` list classifies
`PlatformHost` before the resolver is called at all, so a row in `platform_host_to_tenant`
naming the same host is inert — never read, never logged, never counted. That is the right
way round: the list is the operator's own entry point, and a tenant that acquired the
hostname must not be able to take it over.

**The problem it leaves.** The losing row is *silent*, so a deployment that creates one
gets no signal. There is no startup cross-check and no constraint, because the two live in
different places — one is application configuration, the other a table — and a database
constraint cannot see the first.

**Who owns the check.** Whichever packet builds the host-mapping writer. Packet 7 built it:
`MapHostToTenantCommandHandler` refuses a reserved host through `IReservedHostRegistry` — a
port, because the list is bound in the composition root and a module may not reference it —
and answers `lockey_host_reserved` rather than writing a row that would do nothing.

**The Decision is unchanged.** The matrix, the signals and the ceiling all stand.

## References

- [ADR-0003 Tenant Isolation Defense in
  Depth](0003-tenant-isolation-defense-in-depth.md) (Amendment 3)
- [ADR-0004 Authentication Strategy](0004-authentication-strategy.md) (Amendment 1 —
  the two-realm invariant)
- [ADR-0013 Page Block Schema Versioning](0013-page-block-schema-versioning.md) — one
  of the two `/v1/platform/*` dependents left open here.
- [ADR-0015 API Gateway with APISIX](0015-api-gateway-apisix.md)
- [ADR-0017 Tenant + Organization Hierarchy](0017-tenant-organization-hierarchy.md)
- [ADR-0019 LearnStack Hub](0019-learnstack-hub.md) — Option B, which a platform URL
  space would re-adopt sideways.
- [ADR-0033 Audit Durability Model](0033-audit-durability-model.md) (Amendment 1 — the
  standalone-write failure posture § Recording a rejected assertion depends on)
- [ADR-0034 Hub Contract Surface Invariant](0034-hub-contract-surface-invariant.md)
- [ADR-0035 Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [Standards 04 § Tenant Context](../standards/04-api-design.md)
- [Standards 05 § Table classes](../standards/05-database.md)
- [Standards 07 § Tenant Resolution](../standards/07-frontend-architecture.md) — the
  header's producer.
- [Standards 11 § Tenant Context](../standards/11-security.md)
- [Standards 18 Audit Coverage](../standards/18-audit-coverage.md)
- [Standards 20 § Host → Tenant Resolution](../standards/20-infrastructure-stack.md)
- [architecture/09 Tenant Isolation](../architecture/09-tenant-isolation.md)
- [architecture/13 Identity and Auth](../architecture/13-identity-and-auth.md)
- [architecture/14 Frontend Architecture](../architecture/14-frontend-architecture.md)
- [architecture/30 API Gateway](../architecture/30-api-gateway.md)
