# ADR-0053: Trusted Public Server Rendering

## Status

Accepted — 2026-10-08. The maintainer approved this ADR and the P02d-5 package.

**Date:** 2026-10-08
**Deciders:** @cemil

The [P02d-5 decision package](../roadmap/phase-02d-walking-skeleton.md#p02d-5-decision-package-2026-10-08)
contains the exact defaults, entry matrix, delivery steps and proof obligations.
At acceptance, implementation has not started. The bounded replacements below
apply; all other Accepted contracts remain authoritative.

## Decision Drivers

- P02d-4 delivers public reads and an injected SDK, but no configured renderer.
- Middleware cannot prove a visitor IP from client-supplied forwarding headers.
- Socket-only API limiting makes every renderer visitor share one quota.
- Public paths contain no tenant identity; path-only rendering caches can mix sites.
- SSR must preserve API-owned host resolution, public eligibility and READ ONLY.
- Development must exercise the same ingress and transport as production-build tests.

## Considered Options

1. **Native Node ingress, authenticated transport and dynamic SSR** (recommended).
   Capture connection provenance before Next, retain the SDK seam and avoid shared
   tenant representations in the renderer.
2. **Stock Next with inbound forwarding headers** (rejected). Middleware has no
   independently verified socket identity; signing a client header at the API hop
   does not make its visitor address trustworthy.
3. **One shared renderer quota** (rejected). One visitor can exhaust both sites;
   per-host quotas alone also multiply under novel-host floods.
4. **Activate APISIX as a prerequisite** (rejected for this local packet). Its
   non-development trigger and deployment proof remain Phase 11's responsibility.
5. **Tenant-keyed ISR now** (rejected). Requires representation ownership,
   invalidation and revocation contracts before the first renderer demonstrates a
   need. Existing API definition caches already have their own authority.

## Decision

LearnStack serves institution public requests through a native Node
ingress that delegates rendering to Next.js, authenticates captured host/address
provenance to its middleware and server caller, and uses the injected public SDK
over ADR-0036's network-and-secret API hop. Public representations are dynamically
rendered without shared Next.js data or route caching. Anonymous limiting uses a
verified visitor address with a separate socket-peer ceiling. The API remains the
only tenant/organization authority under ADR-0052.

### Ingress and provenance

- The supported web launcher is the sole listener. It captures the actual
  `IncomingMessage.socket.remoteAddress` before Next handles the request and
  canonicalizes IPv4, IPv6 and IPv4-mapped IPv6 to one IP identity.
- Client tenancy, internal `x-learnstack-*`, forwarding and middleware-control
  headers are stripped before Next; duplicate/malformed Host and missing socket
  identity fail closed. No ingress code looks up a tenant or organization.
- The sole provenance carrier is `X-LearnStack-Ingress-Provenance`, containing
  exactly one bounded, versioned envelope. A domain-separated HMAC authenticates
  its captured host, canonical peer, method and request target using a server-only
  secret. Verifiers refuse duplicate, malformed or conflicting envelopes. Request
  matching accounts explicitly for the pinned Next version's URL processing.
- The ingress captures connection inputs, strips client carriers, then mints the
  envelope before delegating to Next. Middleware verifies it before bootstrap,
  then rebuilds downstream **request** headers from an allowlist, retaining only
  the verified envelope as provenance. The server caller verifies it again before
  SDK use and derives host/peer from its authenticated payload, never unsigned
  parallel headers. The envelope is neither a response header nor an API-hop header.
- Direct stock-Next launch, missing or forged stamps and conflicting carriers
  cannot invoke the configured caller. A plain internal header is never proof.
- The stamp and private carriers are not response headers, HTML/RSC data, client
  assets, retained logs or audit values. Visitor IP is limiter metadata, not a
  tenant selector, user identity or audit actor.
- P02d-5 uses loopback HTTPS for the web listener and loopback HTTP for the API.
  Non-development proxy topology, TLS termination and scaling belong to Phase 11;
  this decision does not claim production deployment readiness.

### API hop and anonymous budgets

- Renderer traffic requires network membership **and** exactly one
  `X-LearnStack-Hop-Secret` value matching any entry in the configured secret list.
  The list continues to permit overlapping rotation; the renderer sends one active
  secret, not the list. The API's no-hop startup remains valid, but the configured
  renderer requires a complete hop. No API forwarded-header middleware is enabled.
- Exactly one bounded `X-LearnStack-Visitor-Address` IP literal supplies the
  anonymous IP key only after that hop predicate succeeds. Other requests ignore
  this header and all forwarding headers, using the actual socket peer.
- Both paths use one canonical IP-key namespace: direct and SSR calls do not
  create separate visitor quotas. Unknown peers share one fixed fallback key.
- Preserve 60 API requests per fixed one-minute window per IP, no queue and
  existing `429` Problem Details/`Retry-After`. A second ceiling permits at most
  600 API requests per fixed one-minute window per physical socket peer. Neither
  key includes host, port, cookies, user agent or an attacker-issued identifier.
- Trusted-hop missing, malformed or repeated visitor metadata consumes the
  bounded peer fallback budget and then returns masked `404` before host lookup.
  Invalid metadata never creates a new partition or an unlimited path.
- Budgets apply before classification/database work. Shared NAT and local
  loopback visitors still share an IP quota; these are API-call, not page-load,
  budgets. The socket ceiling is a local backstop, not distributed DDoS protection.

### Configured caller and rendering

- `packages/sdk` remains the pure injected four-GET contract delivered by P02d-4.
  One server-only adapter in `apps/web` constructs API-hop headers. SDK consumers
  cannot supply tenant IDs, arbitrary headers, origin or hop credentials.
- Its API origin is validated private configuration, never derived from a request
  host or URL. Relative operation paths stay on that origin; HTTP redirects are
  refused so the hop secret cannot follow a redirect.
- Outbound headers are an allowlist: JSON Accept, trusted host, hop secret,
  verified visitor address and validated W3C `traceparent`. No Authorization,
  cookies, tenant/organization assertion or locale header is forwarded.
- A ten-second total deadline covers headers **and** body consumption. Caller
  cancellation remains distinct from timeout/transport failure. No automatic
  retry hides a refusal or multiplies the anonymous budget. Count decoded response
  bytes while reading, with an 8 MiB ceiling; timeout/oversize is a transport
  failure, not a valid API refusal or permission to truncate JSON. This is a
  consumer resource budget, not a new stored-content or OpenAPI size guarantee.
- Bootstrap supplies enabled/default locales. The route locale alone supplies
  Education's query locale; anonymous entry sets no cookies or negotiated default.
- Tenant-varying public routes use dynamic rendering and no-store transport.
  No ISR, positive `revalidate`, `generateStaticParams`, `unstable_cache`, shared
  bootstrap cache or host-blind representation cache is permitted. Request-local
  reuse may share a validated value only inside the same incoming request.
  Freshness means the next new server/document request, not an already open page
  or client Router Cache history. Disable Server Component HMR caching in local
  development; production evidence uses a production build.
- P02d-6 owns public page composition; P02d-7 owns the shared demo/browser harness.
  P02d-5's ingress/adapter tests do not claim those pages are delivered.

### Relationship to existing decisions

This is a bounded replacement of ADR-0036's socket-only anonymous partition rule
for authenticated renderer traffic, and of its exact SDK-file header-setter rule.
Its reconciliation matrix, host normalization, network-and-secret predicate and
assertion-only tenancy inputs remain unchanged. ADR-0052's public DTO, eligibility,
cursor, no-store and READ ONLY contracts also remain unchanged.

The startup policy is explicit: both hop lists empty are allowed in every API
mode; partial configuration, invalid CIDRs and blank/short secrets are refused.
The minimum is 32 characters; the renderer uses generated ASCII entropy and
requires a complete hop. This adopts shipped behavior in place of ADR-0036's older
non-Development/empty-list/32-byte wording, not through a silent clarification.

Acceptance appends a dated **Amendment** to ADR-0036 under
[ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md) and
[Documentation Standards](../standards/13-documentation.md#correcting-and-amending-adrs).
It links the bounded replacements, source/history evidence and changed carriers.
ADR-0053 owns the changed rules; the Amendment supplies navigation and disclosure,
not a rewrite of the Accepted Decision or historical notes.

Acceptance also reconciles these ongoing carriers explicitly:

- [Standards 07 — Public Site Renderer](../standards/07-frontend-architecture.md#public-site-renderer):
  replace public `revalidate` with dynamic/no-store rendering; its Tenant Resolution
  and SDK sections name the single configured adapter outside the pure SDK.
- [Frontend Architecture — Rendering Strategies](../architecture/14-frontend-architecture.md#rendering-strategies):
  replace the institution `(public)` ISR-like sketch with the chosen dynamic path.
- [Standards 15 — Caching](../standards/15-performance.md#caching): distinguish the
  uncached public renderer from independent API definition caches.
- [Standards 04 — Request and Response Limits](../standards/04-api-design.md#request-and-response-limits)
  and [Standards 11 — Rate Limiting](../standards/11-security.md#rate-limiting): replace
  the anonymous socket-only partition with the authenticated visitor policy and
  separate peer ceiling; other surface policies retain their existing owners.

## Context

At development `88f52c4`, Next is pinned to 15.5.18. Its installed server preserves
an existing `x-forwarded-for` value when adding socket metadata; v15 also removes
`NextRequest.ip`. Middleware forwarding that value would launder attacker input
into the trusted API hop. Native socket capture addresses the missing source,
while a verified stamp prevents a bypassed stock launcher accepting plain headers.

P02d-4 already supplies SDK tests, AppError parsing and no-store requests. It also
forbids hop secrets and authority options in that package. ADR-0036's older exact
setter path cannot silently override this later seam.

The custom launcher adds maintenance and limits deployment options: Next documents
lost optimizations and incompatibility with standalone output. Only the ingress
boundary changes; Next still owns routing and rendering. Reconsider it when Phase
11 supplies an equivalently proved trusted ingress that can preserve a stock Next
server. Any replacement must prove peer origin, direct-bypass refusal and secret
containment before changing the deployment shape.

Local TLS avoids assuming all browsers resolve `.localhost` identically or accept
Secure cookies on HTTP. Existing seed names and warm database mappings remain
unchanged. Installing/trusting a development CA is an explicit workstation step,
not an automatic side effect of repository commands.

## Consequences

### Positive

- The API receives host lookup input and verified rate-limit metadata separately.
- Forged forwarding headers cannot mint visitor partitions through the renderer.
- Uncached SSR preserves host isolation and fresh API eligibility on every request.
- One SDK seam and one configured header setter remain reviewable and enforceable.

### Negative

- The repository owns a native launcher, ingress integrity protocol and TLS setup.
- Per-IP and socket ceilings can reject legitimate bursts; NAT fairness is unchanged.
- Repeated bootstrap/content calls spend quota and latency; no ISR hides that cost.
- Local hosts and CA trust require explicit developer setup; standalone is unavailable.

### Neutral

- No database migration, Hub endpoint, tenant resolver or new authentication surface.
- Existing API no-hop startup remains usable; a configured renderer is stricter.
- Distributed limits and production ingress are Phase 11 capabilities, not P5 claims.

## Implementation Notes

The [packet plan](../roadmap/phase-02d-walking-skeleton.md#p02d-5-implementation-plan)
owns defaults, carrier updates, review rounds and scope by step. Acceptance updates
ongoing standards and appends ADR-0036 navigation before code. Delivery notes record
implementation separately; acceptance is not runtime evidence.

## Architecture Tests

These are accepted proof obligations, not passing-test or delivery claims. The
catalogue records implementation status separately:

- Extend existing hop/anonymous-peer controls for verified visitor metadata,
  preserved socket origin, one-IP quota and pre-lookup flood bounds.
- Prove server-only imports, the single fetch/header-setter adapter, no shared
  public cache and secret/stamp exclusion with nonempty production subjects and
  clean/planted controls. Real socket tests prove the ingress, not source scans.
- Extend `No_Architecture_Test_Is_Skippable` to all tested frontend workspaces;
  actual skipped/todo fixtures and a missing test script must fail the runner.
- Preserve existing public scope, READ ONLY, SDK drift and contract controls.

## Amendments

### Amendment 1 — Native ingress foundation delivered (2026-10-08)

P02d-5 Step 1 implements the mandatory native HTTPS launcher, socket-derived
host/address envelope, strict verification and inbound private-header sanitation.
The root private environment source, optional matching web projection and paired
loopback API launcher are delivered. The native launcher provides readiness;
stock Next with forged provenance is refused by the middleware foundation.

The [Step 1 record](../roadmap/phase-02d-walking-skeleton.md#p02d-5-step-1--native-ingress-and-local-topology)
owns validation and review evidence. Step 2 still owns visitor budgets; Step 3
owns configured API transport/bootstrap/entry; Step 4 owns mechanical fences and
complete production integration. TLS socket/production-launch controls are not a
workstation-browser or P6 page-delivery claim. The original acceptance body remains
unchanged.

### Amendment 2 — Native ingress review closeout (2026-10-08)

Step 1's two independent review rounds and fix verification are complete.
Authenticated raw targets are matched against explicit pinned-Next query
processing; repeated pathname slashes are refused before delegation. Native
sanitation also removes Next's routing, resumption and revalidation controls.
Next's documented `httpServer` option points at a non-listening upgrade sink:
only admitted native TLS upgrades reach it, including development HMR.

The [Step 1 record](../roadmap/phase-02d-walking-skeleton.md#p02d-5-step-1--native-ingress-and-local-topology)
owns production/socket, mutation and cleanup evidence. Steps 2–4 retain their
scopes; this note changes no Accepted decision or browser-readiness claim.

### Amendment 3 — API visitor admission and budgets delivered (2026-10-08)

P02d-5 Step 2 implements exactly-one bounded visitor metadata after the existing
network-and-secret hop predicate, strict canonical IP keys shared with direct
traffic, and chained 600/physical-peer plus 60/visitor-IP fixed-window budgets.
The peer ceiling runs first; invalid trusted metadata spends fallback quota and
receives masked `404` before host classification. Untrusted metadata is ignored.

The [Step 2 record](../roadmap/phase-02d-walking-skeleton.md#p02d-5-step-2--api-visitor-admission-and-budgets)
owns validation and review evidence. Empty-hop startup, API tenant authority,
rotation-list support and existing public response contracts remain unchanged.
Configured frontend transport and full renderer proofs still belong to Steps 3–4.

### Amendment 4 — Configured caller and public entry delivered (2026-10-08)

P02d-5 Step 3 implements the server-only configured SDK adapter, private origin
and closed hop headers, validated/generated W3C trace context, total ten-second
deadline and decoded 8 MiB consumer limit. Node middleware verifies provenance
before live bootstrap, applies membership-first locale entry, preserves inert raw
query bytes and rebuilds downstream request headers. The public layout is dynamic
and no-store. Neither bootstrap nor SDK responses enter a shared renderer cache.

The [Step 3 record](../roadmap/phase-02d-walking-skeleton.md#p02d-5-step-3--configured-caller-and-public-entry)
owns validation and reviews. Step 4 still owns complete production-build integration
and mechanical frontend fences; P6 owns public page composition. This delivery
note changes no Accepted decision or browser-readiness claim.

### Amendment 5 — Frontend fences and production integration delivered (2026-10-09)

P02d-5 Step 4 implements source guards for private server imports, the single
fetch/header adapter, dynamic/no-store policy and raw-authority refusal. The
workspace runner discovers tested packages and refuses missing scripts/reports,
empty outcomes, skipped/todo cases and omitted files; actual planted runs prove it.

The disposable production fixture imports the real middleware/caller against the
real Kestrel API and PostgreSQL as `learnstack_app`. Same-path HTML/RSC requests on
two hosts stay separate; publication changes affect the next request. A real Client
Component import fails the build, stock-Next forgeries cannot bootstrap, and private
values stay out of responses, client assets and logs. Bootstrap/rendering share a
request-local trace even when incoming trace context is absent or invalid.

The [Step 4 record](../roadmap/phase-02d-walking-skeleton.md#p02d-5-step-4--frontend-fences-and-production-integration)
owns validation and independent reviews. Test-owned routes are not P6 product
pages or P7 browser/Lighthouse delivery. The original Accepted body is unchanged.

## References

- [ADR-0036](0036-tenant-resolution-trusted-inputs.md)
- [ADR-0035](0035-demand-gated-infrastructure.md)
- [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md)
- [ADR-0009](0009-frontend-single-app-first.md)
- [ADR-0052](0052-anonymous-public-read-boundary.md)
- [Frontend Architecture](../architecture/14-frontend-architecture.md)
- [Frontend Architecture Standards](../standards/07-frontend-architecture.md)
- [Documentation Standards](../standards/13-documentation.md)
- [Node HTTP socket](https://nodejs.org/api/http.html#messagesocket)
- [Next.js 15 custom server](https://nextjs.org/docs/15/app/guides/custom-server)
- [NextRequest version history](https://nextjs.org/docs/app/api-reference/functions/next-request)
- [Next.js 15 route configuration](https://nextjs.org/docs/15/app/api-reference/file-conventions/route-segment-config)
- [mkcert setup and trust stores](https://github.com/FiloSottile/mkcert)
