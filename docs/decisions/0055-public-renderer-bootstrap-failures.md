# ADR-0055: Handle Public Renderer Bootstrap Failures in Middleware

## Status

Proposed — 2026-10-10. Maintainer approval is pending.

**Date:** 2026-10-10
**Deciders:** @cemil (approval pending)

This replaces the unaccepted neutral-500 and native-response-gate proposals.
The maintainer must approve the single live read, request-local DTO handoff and
changed counts before code. Accepted decisions/history remain unchanged; separate
Open Graph/security-update approval does not accept this admission design.
Keep this record Proposed until the Next 15.5.27 context-propagation feasibility
proof passes and the maintainer approves the revised decision. An isolated
feasibility experiment does not authorize production implementation.

## Decision Drivers

- Bootstrap failure needs an HTTP response owner before RSC rendering begins.
- Preserve exact 404/429/503 and bounded Retry-After without intercepting Next's
  streaming writes, adding a preflight or relying on arbitrary RSC status control.
- A second failing site read creates a refusal the RSC owner cannot express exactly.
- Keep API-owned tenant authority, authenticated ingress and fresh admission on
  every new incoming request; no shared representation or serialized DTO carrier.
- Failed bootstrap permits no Education read or newly emitted tenant theme.
- Make ownership, freshness and quota changes explicit; do not preserve redundant reads.

## Considered Options

1. **One live middleware bootstrap with a request-local validated snapshot**
   (recommended candidate). Middleware already owns a `NextResponse` before RSC.
   RSC re-verifies provenance and consumes only that incoming request's snapshot.
   Shared runtime/context propagation remains a mandatory feasibility gate.
2. **Native response gate around a second RSC read** (rejected). Intercepting
   commitment, early hints, compression, backpressure and terminal stream behavior
   adds a fragile version-sensitive response adapter to preserve a duplicate read.
3. **Native-owned bootstrap before Next** (viable larger alternative). The native
   listener can emit exact refusals before delegation, but must acquire configured
   transport/SDK build support and route/locale/redirect ownership currently in
   middleware. It still needs a validated request-local handoff to RSC. Prefer this
   only through a separate concrete decision if the recommended boundary fails.
4. **RSC exceptions or error components** (rejected). They cannot promise arbitrary
   429/503 and Retry-After; framework error rendering can already have committed 200.
5. **Extra preflight or two serial native reads** (rejected). Another read consumes
   quota and creates another race. Retaining a count is not a reason for duplication.
6. **DTO header or shared site cache** (rejected). A serialized carrier adds a new
   trust protocol; a shared representation changes freshness and isolation scope.

## Decision

LearnStack performs one live public site bootstrap in its existing Node middleware
HTTP response owner. After verified successful admission, middleware publishes a
bounded, immutable validated site snapshot into a native-created request-local
context. RSC independently re-verifies provenance and consumes that exact request's
snapshot without another site call. Middleware emits bootstrap 404/429/503 before
RSC starts. This proposed replacement changes the accepted duplicate-read and
request-count contracts and requires approval plus production feasibility proof.

### Native context and middleware admission

1. The native listener retains method admission, socket capture, carrier stripping
   and provenance minting before Next. It creates a fresh `AsyncLocalStorage`
   context bound to the captured canonical host, peer, method, signed raw target
   and native request lifetime, then delegates through the existing Next handler.
2. Middleware verifies provenance and its existing explicit Next URL projection.
   It also requires the active native context to match the verified original
   host/peer/method/target. No client header, URL or trace ID looks up a context.
3. Middleware makes one live `getSite()` through the existing configured caller,
   then evaluates the existing membership-first route/locale entry policy.
   Failure returns its controlled response; redirects retain their current policy.
4. Only a successful continuation publishes the validated snapshot and advances
   the native context from pending to ready. Refusal/redirect is terminal and
   publishes no site snapshot. Missing, disposed, mismatched or conflicting
   context cannot continue into RSC; a context invariant failure returns neutral 503.
5. Middleware retains its downstream header allowlist, including the verified
   provenance envelope. The snapshot/context is never serialized into request or
   response headers, HTML/Flight payloads, cookies or client assets.

The native context contains only bounded ownership/binding state, cancellation
capability and the approved site snapshot. It is not a registry: there is no map
keyed by host, path, envelope, client identifier or framework request metadata.
Identical simultaneous requests have distinct native lifetimes and stores.

### Snapshot and RSC consumption

The snapshot contains only the validated public Site DTO, not a raw body, Response,
SDK client, provider error or tenant/organization ID. The existing ten-second total
transport deadline and **8 MiB decoded-response ceiling** apply before parsing.
Retain one snapshot, freeze nested values and do not retain the raw response.

RSC re-verifies the authenticated provenance envelope and checks the active native
context's binding and ready state before consuming the snapshot. The signed raw
request target remains the route authority; Next's transformed metadata does not
select the host, tenant, organization or locale. The snapshot is a rendering value
from the API's host-resolved read, not a tenant-authority grant or an API bypass.
All Education calls still use the verified configured caller and independently
pass live API admission, public eligibility, READ ONLY and RLS enforcement.

The existing request-local admission/resource loaders remain the shared consumer
for metadata, layout, page and i18n configuration. Their admission loader no longer
calls `getSite()`. Each consumer honors its result before Education or theme use;
layouts alone do not serialize child execution. Content clients carry the native
cancellation signal and retain the existing transport controls.

A missing, disposed or mismatched RSC snapshot is an internal lifecycle error,
not API 404 or permission to fetch another site or reuse data. Fail closed before
Education/theme with a sanitized framework failure. Arbitrary framework-error
statuses are not guaranteed; production proofs must prevent context failures.

### Freshness, accounting and response scope

Every matched tenant-capable HTTP request starts with a new context and live
bootstrap; health/assets retain their exemptions. No site value survives for
another request, host, navigation or HMR render. Consumers share
one admitted snapshot: the second site revalidation is removed. Changes between
middleware and RSC appear on the next request; content API eligibility remains live.

| Incoming request or followed chain | Public API calls |
|---|---:|
| Completed admitted product document GET | 2: site + shared content operation |
| Completed fixed status/scaffold document GET | 1: site only |
| Bootstrap refusal reaching the API | 1: site attempt, no Education |
| Missing-detail GET followed to fixed localized 404 | 3: original site/content + destination site |

HEAD, RSC and prefetch have one mandatory site call; content is called only when
Next executes the shared resource loader. Prove their actual counts separately.
Invalid provenance/context rejected before transport makes no API call. A locale/
entry redirect uses one site call; following it is a new counted request. Admitted
invalid-cursor states may omit content transport as under the existing policy.
Metadata/layout/page/UI reuse cannot add calls. Preserve 60-call visitor and
600-call peer budgets; these remain API-call budgets, not page quotas.

The exact-status guarantee covers valid execution and bootstrap failures handled
by middleware before RSC. It does not promise 429/503 for arbitrary later framework
errors or rewrite an already streamed status. Content-error HTTP 200/noindex,
cursor SEO and missing-detail 307-to-fixed-404 remain accepted.

### Refusal and Retry-After policy

| Bootstrap outcome | Original HTTP response |
|---|---|
| Invalid provenance, client verification refusal, API 404 or entry 404 | Neutral 404 |
| Valid API 429 | Neutral 429; eligible bounded Retry-After |
| API 503 or other non-404/non-429 site failure | Neutral 503; eligible bounded Retry-After |
| Timeout, transport/invalid response, oversized body, configuration failure or entry 503 | Neutral 503; no invented Retry-After |
| Middleware context invariant failure | Neutral 503; no invented Retry-After |
| Client disconnect | Cancel work; no response-delivery guarantee |

Refusals retain `Cache-Control: no-store`, fixed plain-text content type/copy and
bodyless HEAD. No site snapshot, Education operation or new tenant-themed failure
response follows failed bootstrap. No raw Problem Details, submitted/private value
or provider diagnostic enters response output or retained logs.

One renderer sanitizer accepts optional parsed Retry-After integer delta-seconds
**0 through 60 inclusive**, only on 429/503 from the closed supported rate-limit/
unavailable error cases. Preserve eligible values; omit malformed, HTTP-date,
negative and out-of-range values without clamping or inventing a retry duration.
The SDK's current safe-integer parser alone does not implement this proposed bound.

If accepted, this qualifies ADR-0054's
[Anonymous accounting](0054-bounded-public-renderer-admission.md#anonymous-accounting)
instruction to preserve the selected refusal's Retry-After unchanged at the
renderer forwarding boundary only: forward an eligible value unchanged, otherwise
omit it. The API's visitor-first refusal selection and original response metadata
remain unchanged. Record this bounded renderer qualification explicitly in
ADR-0054's dated navigation note; it is not a replacement of API accounting.

### Lifecycle, runtime and navigation

Cancellation reaches bootstrap/content transport, without promising forced
cancellation of arbitrary React work. Native `finish`/`close` or shutdown closes
the store exactly once, releases its snapshot/listeners and prevents late use.
Do not call ALS `disable()` per request or dispose on a delegating promise's return.
Response completion does not prove React work has ended. Check disposal before
publication or downstream work and again after awaits. Clearing the store's
snapshot reference does not synchronously reclaim references held by already
running consumers. Late continuations must fail closed without republishing,
starting another bootstrap or Education read, or exposing the context/DTO in
diagnostics. Production proofs must cover valid HEAD and late component work.
Expected refusal after completion is distinct from an active-request context
defect and cannot change the already completed HTTP response.

Native startup installs the sole ALS holder as a versioned, non-enumerable
`globalThis[Symbol.for('learnstack.public-admission.v1')]`. Server-only facades
retrieve and validate that instance; bundles never create a fallback. Only native
ingress calls `run()` with a fresh store. This is a same-process Node candidate;
workers, Edge or serverless need a new decision. Prove actual singleton/context
reuse through middleware, RSC and error rendering on Next 15.5.27. Missing context
fails closed; no header/cache/extra-read fallback or Next API guarantee is implied.

Product navigation retains plain anchors. Stock Next can turn a non-success Flight
response into full-document navigation, causing another genuine request and quota
charge; prove and count that chain separately. A recovered follow-up may differ.
Failed Flight cannot erase the old document's painted theme: the guarantee is no
new tenant-themed failure response, not removal of existing browser DOM.

Fixed status and studio/portal share admission without new authentication. Preserve
tenant-free health/asset exemptions and missing-asset fallbacks, native GET/HEAD,
production upgrade closure and HMR rules; HMR must not reuse old snapshots.

## Context

At `69b382a`, middleware discards its site read; RSC reads again and collapses
failures to `null`, which `public-resource.ts` turns into `notFound()`. G40 spends
three calls on a normal document and five on a followed missing document. This
proposal replaces that ownership/count choice instead of intercepting streaming.

This is the request-local bootstrap reuse explicitly left undelivered by
[ADR-0054's Context](0054-bounded-public-renderer-admission.md#context).
It changes the admission loader described in
[ADR-0027's execution boundary](0027-frontend-i18n.md#dependency-and-execution-boundary):
the loader consumes this request's validated live snapshot instead of calling
`getSite()` again. Provenance verification, signed-route locale selection and
enabled-locale membership checks remain required before UI or content consumers.

Historical source inspection of Next 15.5.18 shows `pipe-readable.js` flushing
headers before body writes, `app-render.js` mapping ordinary errors to 500 and
Node middleware executing through its adapter. Official custom-server/streaming
and Node ALS documentation establish the available primitives, not this shared
context's correctness. Delivery requires production proofs on Next **15.5.27**;
15.5.18 source observations are not runtime evidence for the patched baseline.

### Isolated feasibility observation (2026-10-10)

An isolated native HTTPS / production Next 15.5.27 experiment on Node 22.23.1,
independently replayed by the root agent, confirms the same ALS holder and store
references through bundled Node middleware, metadata, layout and page, including
after awaits. It covers 37 requests: eight overlapping identical requests each
for HTML, Flight and prefetch; eight host/locale HTML/Flight combinations; HEAD;
and HTML/Flight not-found/error cases. Prefetch executes middleware without dynamic
RSC consumers in this fixture. Unbound and duplicate-ALS controls return 503 with
no snapshot publication or RSC consumer. Publication is synthetic, not `getSite()`.

On a valid HEAD, an already-started not-found component resumes after native
`finish`. ALS still carries the correct disposed store; its snapshot is cleared
and the facade refuses late access. The wire remains bodyless 200. This supports
the disposal checks above; it does not prove all React work or local references
have ended. The fixture's bounded error is logged by Next, so production must
also verify neutral diagnostics during this path.

This establishes the same-process propagation premise only. Actual provenance,
route binding, API/RLS, call accounting, abort/shutdown, keep-alive, HMR and browser
navigation remain production proof obligations. The experiment does not accept
this decision or satisfy those obligations. Maintainer approval remains pending.

## Consequences

- **Positive:** bootstrap failure has an existing pre-render HTTP owner; one site
  call eliminates the second-read failure race and reduces quota use.
- **Negative:** a new validated request-local DTO handoff and cross-bundle runtime
  dependency require proof; the second within-request revalidation is removed.
- **Neutral:** no response-write interceptor, new listener, endpoint/schema/SDK wire
  change, shared cache or authority change. Stock Flight fallback can add requests.

## Implementation Notes

P02d-6 remediation owns the proposed replacement of G40's duplicate RSC bootstrap,
response ownership and three/five-call contract. Approval explicitly accepts the
request-local DTO handoff and two/three-call replacement. Record bounded dated
supersession/navigation in ADR-0053, ADR-0027 and the G40/current guidance, plus
ADR-0054's renderer Retry-After qualification and deferred-reuse delivery boundary;
retain Accepted bodies and historical delivery records unchanged. The acceptance
decision must be committed before dependent production implementation.

Scope covers native context/lifecycle, runtime facade, middleware publication,
RSC/resource/i18n consumers and proofs. Failed feasibility requires a new decision,
not duplicated reads, bootstrap 500/200, shared data or DTO headers. Native-owned
bootstrap needs its own ownership/build design.

## Architecture Tests

These are mandatory obligations, not passing-test or Implemented-catalogue claims.
At acceptance, register canonical rule names and their owning test paths as
Registered in the
[architecture-test catalogue](../standards/21-architecture-tests-catalogue.md)
before dependent production code. Mark a rule Implemented only after its
executable proof and planted failing/passing controls pass:

- Actual Next 15.5.27 production context propagation and same-request snapshot reuse;
  missing, duplicated, disposed or mismatched context fails closed before consumers.
- First-wire 404/429/503, no-store, bodyless HEAD and Retry-After boundaries/omissions
  for API refusal, transport/timeout, malformed/oversized response and invalid site.
- No Education, new tenant theme, private marker or raw error after failed bootstrap;
  planted broken admission/context controls must fail the proof.
- Exact two/one/three-call paths and zero additional site calls across concurrent
  metadata/layout/page/UI/error consumers; recovered next request re-reads live data.
- Same/different hosts/locales and identical targets/envelopes under concurrency;
  snapshot binding, deep immutability and no cross-request retention or lookup.
- Product/status/scaffold HTML, RSC, prefetch and navigation; separately counted
  Flight-to-document fallback; retained content 200/noindex and missing 307-to-404.
- Abort/late publication, normal finish, early close, shutdown and keep-alive next
  request; zero active contexts/snapshots after completion and no HMR reuse.
- Invalid host/locale/stamp, stock launcher bypass, exempt asset/health fallback,
  native method/upgrade refusal, no build-time bootstrap and retained HMR behavior.

## References

- [ADR-0027 — Frontend UI Localization](0027-frontend-i18n.md)
- [ADR-0036 — Tenant Resolution Trusted Inputs](0036-tenant-resolution-trusted-inputs.md)
- [ADR-0052 — Anonymous Public Read Boundary](0052-anonymous-public-read-boundary.md)
- [ADR-0053 — Trusted Public Server Rendering](0053-trusted-public-server-rendering.md)
- [ADR-0054 — Bounded Public Renderer Admission](0054-bounded-public-renderer-admission.md)
- [P02d-6 review decision package](../roadmap/phase-02d-walking-skeleton.md#p02d-6-systematic-review-decision-package-2026-10-10)
- [Native launcher](../../frontend/apps/web/scripts/public-server.mjs)
- [Middleware](../../frontend/apps/web/src/middleware.ts)
- [RSC admission loader](../../frontend/apps/web/src/server/public-request.ts)
- [Next 15 custom server](https://nextjs.org/docs/15/app/guides/custom-server)
- [Next 15 streaming status codes](https://nextjs.org/docs/15/app/api-reference/file-conventions/loading#status-codes)
- [Node asynchronous context](https://nodejs.org/api/async_context.html#asynclocalstoragerunstore-callback-args)
