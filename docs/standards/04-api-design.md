# 04 — API Design Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0003 — Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md), [ADR 0024 — API Versioning Policy](../decisions/0024-api-versioning-policy.md), [ADR 0036 — Trusted Inputs for Tenant and Organization Resolution](../decisions/0036-tenant-resolution-trusted-inputs.md), [ADR 0037 — What an Idempotency Key Identifies, Owns, and Replays](../decisions/0037-idempotency-key-contract.md),
[ADR 0039 — The Optimistic Concurrency Token](../decisions/0039-optimistic-concurrency-token.md).

REST conventions for LearnStack public and admin APIs.

## Style

- REST first. GraphQL only after a clear product driver.
- Resources are plural nouns (`/courses`, `/users`).
- HTTP verbs used idiomatically: `GET` read, `POST` create, `PUT` replace, `PATCH` modify, `DELETE` remove.
- JSON, `application/json`, UTF-8.
- URL versioned: `/api/v1/...` (canonical prefix per [ADR-0024](../decisions/0024-api-versioning-policy.md)).
- Bodies use `camelCase`.

## URL Structure

```
/api/{version}/{resource}/{id?}/{sub-resource?}
```

Examples:
- `GET  /api/v1/courses?cursor=...&limit=20`
- `POST /api/v1/courses`
- `GET  /api/v1/courses/{id}`
- `PATCH /api/v1/courses/{id}`
- `POST /api/v1/courses/{id}/versions`

Platform-admin endpoints live under `/api/v1/platform/...` and require platform-admin scope.

## Versioning

The full versioning policy lives in
[ADR-0024 — API Versioning Policy](../decisions/0024-api-versioning-policy.md).
Summary so reviewers don't have to chase a link to know the shape:

- **URL-based** under `/api/v{N}/` (the only canonical public route shape;
  matches APISIX gateway routes per ADR-0015). Header-based versioning is
  not used.
- **Two adjacent majors coexist**; deprecation window is **6 months**.
- **`/healthz` and `/readyz` are unversioned** infrastructure endpoints.
- The internal `/api/internal/*` Hub contract has its own versioning per
  [ADR-0019](../decisions/0019-learnstack-hub.md) and is not governed by
  ADR-0024.

For the breaking/non-breaking matrix, the exact header set
(`Deprecation` per RFC 9745, `Sunset` per RFC 8594, `Link:
rel="successor-version"`), OpenAPI extensions (`x-sunset` / `x-successor`
/ `x-migration-guide` / `x-extensible-enum`), the 410 Gone Problem Details
shape, and per-deployment-mode behaviour — see ADR-0024 directly.

## Status Codes

| Code | Use |
|------|-----|
| 200 | Success with body |
| 201 | Created; `Location` header set |
| 202 | Accepted for async work |
| 204 | Success, no body |
| 400 | Client validation failure |
| 401 | Missing/invalid authentication |
| 403 | Authenticated but not authorized |
| 404 | Not found (also used to hide cross-tenant existence) |
| 405 | Method not allowed; the `Allow` header lists the methods that are permitted |
| 409 | Concurrency or business-rule conflict |
| 413 | Request body over the configured limit |
| 410 | Permanently gone |
| 415 | Unsupported media type |
| 422 | Semantic validation failure (rarely needed; prefer 400) |
| 429 | Rate limited; `Retry-After` set |
| 500 | Server bug; correlation id in body |
| 503 | Dependency unavailable; `Retry-After` set |

## Error Responses

All errors use **Problem Details (RFC 7807)**, in exactly one shape, served as
`application/problem+json; charset=utf-8`. One spelling, deliberately: two —
one with the charset and one without — made a routing 404 tellable from an MVC
404 without reading the body, which
[ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) requires not
to be possible.
[09-error-handling.md § API Surface](09-error-handling.md) is the authority for
its fields; the example there is canonical and is not duplicated here.

**The rule is one shape for every error the application can see.** It does not
reach the two the server rejects before any middleware runs: an over-long
request line (**414**) and an over-large header block (**431**) are refused by
Kestrel, with no body at all — and over HTTP/2 the connection is reset with no
status. That is a property of where the rejection happens, not an exemption
anyone chose; § Request and Response Limits says which bounds those are and why
the edge is where such a limit can be given a body.

Two things a reader of this document needs, because both are easy to get wrong:

- **`title` carries the `lockey_*` key, not English prose, and there is no
  `detail`.** The backend never returns raw English — the frontend resolves the
  key against its i18n catalogue. An earlier version of this section showed
  `"title": "Validation failed"` and a `detail` field; both contradicted
  Standards 09 and the shipped `ProblemDetailsFactory`.
- **Every error carries it, including the ones no handler produces.** An
  unmatched route (404), a wrong method (405) and an unsupported media type
  (415) are framework-level and arrive with no body by default. Packet 4 gives
  them the same shape, so a client parses one thing.

`code` is a stable machine-readable string — `validation_failed`, `not_found`,
`method_not_allowed`, `unsupported_media_type`, `concurrency_conflict`,
`tenant_mismatch`, `recording_consent_required`, … — and always equals
`title` with the `lockey_` prefix stripped, by construction.

## Authentication

- Bearer JWTs issued by Keycloak in `Authorization: Bearer <jwt>`.
- Cookie sessions for Next.js apps; cookies are `HttpOnly`, `Secure`, `SameSite=Lax`.
- Service-to-service uses client-credentials grants with limited scopes.
- API keys are forbidden in the MVP; if added later, they live in `Integrations` with rotation + audit.

## Tenant Context

**[ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) is the single
authority for how tenant and organization are resolved.** It is not restated here,
because the ordered list this section used to carry was one of four incompatible
statements in the corpus and reading it as a priority chain produced the wrong answer.

The two rules an API author needs at the point of writing an endpoint:

- **Resolution is by agreement, not priority.** Every authoritative signal present on a
  request — the host lookup result, the validated JWT claims, live membership — is
  resolved independently, and the request proceeds only on their intersection. Two
  authoritative signals that disagree do not produce a winner; they produce a 404.
- **No request input selects a tenant.** Not a body field, not a query param, not a
  cookie, and not a header. `X-Tenant-Id` and `X-Organization-Id` are *assertions*: they
  are compared against the resolved value and can only cause the request to be rejected.

A request that cannot resolve a tenant returns **404** (not 403, to avoid disclosure).

## Pagination

Cursor pagination by default:

```
GET /api/v1/courses?cursor=eyJpZCI6Li4ufQ&limit=20
```

Response:

```json
{
  "items": [...],
  "pageInfo": {
    "nextCursor": "eyJpZCI6Li4ufQ",
    "previousCursor": null,
    "hasNext": true,
    "hasPrevious": false
  }
}
```

Rules:
- `limit` default 20, max 100. A larger `limit` is **clamped to 100, not
  rejected** — one behaviour, decided once in `CursorPagination` and not
  re-decided at the edge. A `limit` of zero or less **is** rejected, with
  `errors.limit` naming the parameter the client sent.
- Cursor is opaque; clients must never parse it. Nothing validates its *shape*
  yet: the payload belongs to whoever mints it, and the first minting query
  lands with the tenancy read paths.
- Offset pagination is allowed only for admin-bounded lists (≤ 10k total rows).

## Filtering and Sorting

- Filter via query params: `?status=published&level=B1`. Filters are
  **resource-specific** and each endpoint declares its own — the names and the
  value sets belong to the resource, so there is no generic filter type.
  Declaring them as parameters is what documents them in OpenAPI.
- Sort via `sort=field` or `sort=-field` (prefix `-` for descending).
- Multiple sort keys: `sort=-publishedAt,title`. The order is **priority
  order** and is preserved.
- Search via `q=...` for free text. No separate length cap: the URL bound in
  § Request and Response Limits is the host's request-line limit, and a third
  bound agreeing with neither it nor the gateway's would be worse than
  inheriting whichever actually rejects an over-long URL.

The `sort` grammar is enforced by `SortSpecification`, and its edges are
decided rather than left to each endpoint:

- A field is one or more dot-separated segments of ASCII letters and digits,
  each starting with a letter — the camelCase shape § Style fixes, with a dot
  for a nested path — and at most **64 characters**. Anything else is **400**.
- A permitted field comes back in the **allow-list's** spelling. The match is
  case-insensitive, so `?sort=PublishedAt` is accepted; handing a handler back
  a spelling the endpoint never declared is how a field name that reaches a
  `switch` or an `OrderBy` breaks on a casing nobody tested.
- An empty segment (`title,` or `a,,b`), a bare `-`, or the **same field
  twice** is **400**. Each is a typo, and accepting one silently drops or
  reorders a key the client believes it asked for.
- At most **four** keys. A sort is a query plan and each key is an index
  decision; unbounded, a client composes an arbitrarily expensive ordering.
- A **well-formed field the endpoint does not permit** is also **400**, naming
  the field. Parsing and authorising are separate steps: the endpoint owns the
  allow-list, and silently ignoring an unpermitted key returns a page in an
  order the client did not ask for with no way to notice.

Both report under `errors.sort` — the name the client sent — and the two are
deliberately different shapes, because they fail at different times:

- A **grammar** failure is a binding failure and answers exactly as one:
  `lockey_invalid_value`, no parameters. `?limit=abc` and `?sort=title,` produce
  the same body under different keys, which is the point.
- An **unpermitted field** is refused by the endpoint after binding succeeded,
  so it carries `lockey_sort_field_not_allowed` with the field in `params`.

## Idempotency

**Derives from:** [ADR-0037](../decisions/0037-idempotency-key-contract.md).

Unsafe operations with external side effects accept an `Idempotency-Key` header:

```http
POST /api/v1/orders
Idempotency-Key: 01HX7F...
```

**A key is a nonce, not an identity.** The stored record is addressed by
`(tenant, key)`, and it carries a **fingerprint** of the organization, the acting
principal, the method, the path, the query string and the body. A key presented
with a different fingerprint is refused — the client reused it for a different
request, and both alternatives (replaying the old answer, or running the new
one) fail silently.

Rules:

- The server stores the outcome for **24 hours** and replays it, marked
  `Idempotency-Replayed: true` — a client retrying after a timeout otherwise
  cannot tell whether its second call did the work or collected the first one's
  answer.
- **A replay is the whole response**, not its status line: the body, the content
  type and every header describing the outcome, including `Location` and `ETag`.
  Headers describing the exchange — framing, `Date`, `Server`, `Set-Cookie`,
  `X-Correlation-Id` — are not replayed.
- **The key space is scoped to the tenant.** A request with no resolved tenant is
  refused rather than served from an unscoped space.
- A key is 8–128 printable ASCII characters with no space. Malformed, absent, or
  repeated is **400** under `errors.idempotencyKey`.
- A **concurrent** request holding the same key gets **409**
  `request_in_progress` — retry with the same key. This is deliberately *not*
  `concurrency_conflict`, which tells a client to re-read and re-submit; one code
  cannot carry both instructions.
- A key presented for a **different request** gets **409**
  `idempotency_key_reuse`. Use a new key.
- A **5xx, a thrown attempt, a 408/425/429, or a response carrying
  `concurrency_conflict` / `rate_limited` / `dependency_unavailable`** releases
  its key. Each describes a condition rather than an outcome, and pinning one
  would answer it for the whole window.
- A response too large to retain (**256 KiB**, headers included) records a
  tombstone: the retry gets **409** `idempotency_outcome_unavailable` rather than
  re-running an operation that already happened. An endpoint that trips this is a
  design mistake and is logged as one.
- When the store is at capacity a **new** key is refused with **503**
  `dependency_unavailable`; an **existing** key is always served. Nothing
  unexpired is ever displaced to make room, because displacing a record lets the
  operation it describes run again.
- Required for: payment operations, notification sending, recording start/stop.
- Encouraged for: enrollment creation, invitation sending.

**Inbound webhooks do not use this mechanism.** A provider cannot be made to send
an `Idempotency-Key`; they deduplicate on `(provider, event_id)` from the
verified payload — see § Webhooks (Inbound).

**The guarantee is at-most-once while a claim is live, and at-least-once across
process death.** The response is recorded before it is delivered, so a client
disconnect is not a loss; a process that dies between the operation committing
and the record being written releases the key and the retry re-runs. An operation
whose duplicate execution is genuinely intolerable needs its own guard inside the
business transaction.

Marked per endpoint with `[Idempotent]`, because which operations have external
side effects is knowledge the endpoint has and the pipeline does not. The
attribute publishes its own OpenAPI contract — the required header and the
statuses it can answer before the action runs.


## Optimistic Concurrency

Mutable resources expose `ETag` (or `version` field).

```
GET /api/v1/courses/{id}        → ETag: "7"
PATCH /api/v1/courses/{id}
  If-Match: "7"
```

Version mismatch returns **409** with `concurrency_conflict` — not the 412 RFC
9110 describes for `If-Match`. That is this corpus's call: `HttpStatusMap` maps
`concurrency_conflict` to 409 and § Status Codes lists 409 and not 412, so a
412 would put a status on the wire that no error code maps to and the generated
SDK has no branch for.

- **Strong comparison, always** (RFC 9110 § 13.1.1). A weak tag says
  "semantically equivalent", and two versions of a row that are semantically
  equivalent are still two versions — one of which the client did not see.
- `If-Match: *` means "whatever version, as long as it exists".
- A **malformed** `If-Match` fails the precondition; it is never treated as
  absent. Reading "I could not parse your precondition" as "you did not send
  one" turns a conditional write into an unconditional one — exactly the
  overwrite the client was preventing.

## OpenAPI

- Generated from code by **`Microsoft.AspNetCore.OpenApi`**, not handwritten.
  Swashbuckle is not used: it ships no document generator for .NET 10, and
  [ADR-0024 § Implementation Notes](../decisions/0024-api-versioning-policy.md)
  fixes the built-in generator.
- **One document per live major**, at `/openapi/v{N}.json` — so
  `/openapi/v1.json` today. Each document holds only its own major's paths;
  without that filter an added `/api/v2` operation reads as a breaking change
  to `v1` under `oasdiff`.
- **Reference console at `/docs`** (redirects to `/docs/`), rendered by
  **Scalar**. `Microsoft.AspNetCore.OpenApi` ships no UI of its own. The
  console is deliberately **not** under `/openapi`: Scalar mounts a
  `{documentName?}` catch-all inside whatever prefix it is given, which
  shadowed the document namespace and answered `/openapi/garbage` with 200 and
  an HTML page. Scalar's request proxy is disabled, so its "Test Request"
  button never routes a LearnStack bearer token through a third party — which
  is also what makes the console usable under `SelfHostedAirGapped`.
- **An unknown document name is a 404 in the one error shape.** The document
  route is constrained to the documents actually registered; without that,
  `/openapi/v9.json` answered 404 in `text/plain` with English framework
  prose.
- The document is served in **every** environment, not only Development: it is
  the contract the SDK generates from and CI diffs, so an artefact no deployed
  instance serves is not the contract. Restricting who may reach it is an edge
  concern (APISIX), not an application one.
- **`servers` is omitted.** Its default value is derived from the request's
  `Host`, which is client-chosen — so the published contract would vary with
  the caller, and the `oasdiff` gate would diff the document against itself.
- Every operation carries `x-version-introduced`;
  [ADR-0024 § OpenAPI marking](../decisions/0024-api-versioning-policy.md) owns
  the full extension set. `deprecated` is derived from `[Obsolete]` and is
  absent when false — OpenAPI defines an absent `deprecated` as `false`, and
  the serializer omits defaults.
- TypeScript SDK `@learnstack/sdk` is generated from this spec;
  [Standards 07 § SDK](07-frontend-architecture.md) owns how and when.
- Breaking OpenAPI changes fail CI unless the version bumps.

## Request and Response Limits

Each row says what enforces it. A published limit that nothing enforces is not
a limit, and the first version of this table was four of those.

| Limit | Value | Enforced by |
|-------|-------|-------------|
| Request body (JSON) | **1 MiB** | `RequestBodyLimit` middleware, and `KestrelServerLimits.MaxRequestBodySize` behind it |
| Request headers, total | 32 KiB | Kestrel (`MaxRequestHeadersTotalSize`), server default |
| Request header count | 100 | Kestrel (`MaxRequestHeaderCount`), server default |
| URL length | 8 KiB | Kestrel (`MaxRequestLineSize`), server default |
| Multipart upload (excluding files) | — | No endpoint yet; [Phase 04](../roadmap/phase-04-cms-media-pages.md) |
| File upload, per content type | see [architecture/16 § Validation](../architecture/16-media-pipeline.md) | No endpoint yet; [Phase 04](../roadmap/phase-04-cms-media-pages.md) |
| Rate limit (anonymous) | 60 req/min per peer | `AddLearnStackRateLimiting` |
| Rate limit (authenticated) | 600 req/min per token | No token to key on yet; [Phase 02b](../roadmap/phase-02b-events-auth.md) |
| Rate limit (write endpoints) | 60 req/min per token | No token to key on yet; [Phase 02b](../roadmap/phase-02b-events-auth.md) |

429 responses include `Retry-After`.

**The body bound is middleware, not only a Kestrel option.** `TestServer` — what
the integration suite runs on — implements neither
`IHttpMaxRequestBodySizeFeature` nor `IHttpRequestBodySizeFeature`, so a Kestrel
limit or a `[RequestSizeLimit]` attribute is silently inert there and no test can
tell whether it is wired. The middleware is the authoritative bound because it is
the one that can be asserted; the Kestrel option is set to the same number behind
it, so an oversized body is refused before it is buffered. A declared
`Content-Length` over the limit is refused without reading anything; a request
that declares no length is counted as it is read.

**The header and URL rows are server bounds, not application bounds, and they
cannot carry the standard error shape.** Kestrel rejects an over-long request
line or header block before any middleware runs, so a **414** or **431** arrives
without the Problem Details body every other error on this surface carries — and
over HTTP/2 the connection is reset with no status at all. That is a property of
where the rejection happens, not something the application can wrap. The numbers
are Kestrel's defaults, written down here so the table describes the running
binary rather than an intention. Tightening them is an
[ADR-0015](../decisions/0015-api-gateway-apisix.md) / APISIX concern: the edge is
where a limit can be enforced *and* given a body.

**File-size limits are owned by
[architecture/16 § Validation](../architecture/16-media-pipeline.md)**, which
carries the per-category numbers and the tenant-override rule. This section and
[Standards 11 § File Upload](11-security.md) link there rather than restating
them — the three used to disagree three ways about the same image.


## Webhooks (Inbound)

- Endpoint: `/api/v1/webhooks/{provider}`.
- Verifies provider signature with a tenant-scoped or platform-scoped secret.
- Idempotent: `(provider, event_id)` stored; duplicates ignored.
- Returns **200** quickly; heavy work deferred to a job.
- Never trusts payload tenant id without cross-checking the stored provider account.

## Webhooks (Outbound)

- Signed with HMAC-SHA256; secret rotation supported.
- Retry with exponential backoff (max 5 attempts) and dead-lettering.
- Headers: `X-LearnStack-Event-Id`, `X-LearnStack-Event-Type`, `X-LearnStack-Signature`, `X-LearnStack-Timestamp`.
- Outbound events documented as a separate OpenAPI schema set.

## Health Endpoints

- `GET /healthz` — liveness.
- `GET /readyz` — readiness (all critical deps reachable).
- `GET /version` — build metadata: git sha, build time, version tag.

Public but rate-limited.

## Etiquette

- `POST /resources` returns the created resource with `201` + `Location`.
- `PATCH` accepts partial JSON; unknown fields → `400`.
- `DELETE` is idempotent; deleting a missing resource → `204`.
- `GET` is safe and idempotent; never has side effects.
- Sort parameters never affect filtering.

## Forbidden

- Tenant id read from a request body or query param.
- Tenant id **selected** from a request header. `X-Tenant-Id` / `X-Organization-Id`
  are assertions compared against the resolved value, never a resolution source
  ([ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md)).
- Domain logic in controllers.
- Returning EF entities directly.
- Mixing API versions in a single endpoint.
- Inconsistent casing.
- Echoing internal error messages to clients.
