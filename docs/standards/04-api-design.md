# 04 — API Design Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0003 — Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md), [ADR 0024 — API Versioning Policy](../decisions/0024-api-versioning-policy.md), [ADR 0036 — Trusted Inputs for Tenant and Organization Resolution](../decisions/0036-tenant-resolution-trusted-inputs.md).

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
| 405 | Method not allowed; the `Allow` header lists the methods that are |
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
- Search via `q=...` for free text. No separate length cap. The 2 KB URL bound
  in § Request and Response Limits is a **target**, not something the
  application enforces: today the real ceiling is the host's request-line and
  header limits, and the gateway's once it fronts the app. A third bound
  agreeing with neither would be worse than inheriting whichever actually
  rejects an over-long URL.

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

`POST` operations with external side effects accept an `Idempotency-Key` header:

```
POST /api/v1/orders
Idempotency-Key: 01HX7F...
```

Rules:
- Server stores `(idempotency_key, response)` for 24 hours.
- Subsequent requests with the same key return the stored response.
- Required for: payment operations, webhook processing, notification sending, recording start/stop.
- Encouraged for: enrollment creation, invitation sending.

## Optimistic Concurrency

Mutable resources expose `ETag` (or `version` field).

```
GET /api/v1/courses/{id}        → ETag: "7"
PATCH /api/v1/courses/{id}
  If-Match: "7"
```

Version mismatch returns **409** with `concurrency_conflict`.

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
- TypeScript SDK `@learnstack/sdk` is generated from this spec in CI.
- Breaking OpenAPI changes fail CI unless the version bumps.

## Request and Response Limits

| Limit | Default |
|-------|---------|
| Request body (JSON) | 1 MB |
| Multipart upload (excluding files) | 1 MB |
| File upload | per content type, default 100 MB |
| Headers | 8 KB total |
| URL length | 2 KB |
| Rate limit (anonymous) | 60 req/min per IP |
| Rate limit (authenticated) | 600 req/min per token |
| Rate limit (write endpoints) | 60 req/min per token |

429 responses include `Retry-After`.

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
