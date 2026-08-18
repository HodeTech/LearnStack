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
| 409 | Concurrency or business-rule conflict |
| 410 | Permanently gone |
| 415 | Unsupported media type |
| 422 | Semantic validation failure (rarely needed; prefer 400) |
| 429 | Rate limited; `Retry-After` set |
| 500 | Server bug; correlation id in body |
| 503 | Dependency unavailable; `Retry-After` set |

## Error Responses

All errors use **Problem Details (RFC 7807)**.

```json
{
  "type": "https://errors.learnstack.dev/validation",
  "title": "Validation failed",
  "status": 400,
  "code": "validation_failed",
  "detail": "One or more fields are invalid.",
  "instance": "/api/v1/courses",
  "correlationId": "01H7F...",
  "errors": {
    "title": ["Title is required."],
    "slug": ["Slug already exists in this tenant."]
  }
}
```

Rules:
- `type` is a stable grouping URL.
- `code` is a stable machine-readable string (`validation_failed`, `not_found`, `concurrency_conflict`, `tenant_mismatch`, `recording_consent_required`, ...).
- `correlationId` matches the trace id in logs.
- Stack traces, table names, query text never appear in responses.
- See [09-error-handling.md](09-error-handling.md).

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
- `limit` default 20, max 100.
- Cursor is opaque; clients must never parse it.
- Offset pagination is allowed only for admin-bounded lists (≤ 10k total rows).

## Filtering and Sorting

- Filter via query params: `?status=published&level=B1`.
- Sort via `sort=field` or `sort=-field` (prefix `-` for descending).
- Multiple sort keys: `sort=-publishedAt,title`.
- Search via `q=...` for free text.
- Each filter is documented in OpenAPI.

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
- **Reference UI at `/openapi`** (redirects to `/openapi/`), rendered by
  **Scalar**. `Microsoft.AspNetCore.OpenApi` ships no UI of its own.
- The document is served in **every** environment, not only Development: it is
  the contract the SDK generates from and CI diffs, so an artefact no deployed
  instance serves is not the contract. Restricting who may reach it is an edge
  concern (APISIX), not an application one.
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
