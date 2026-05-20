# 04 — API Design Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0003 — Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md).

REST conventions for LearnStack public and admin APIs.

## Style

- REST first. GraphQL only after a clear product driver.
- Resources are plural nouns (`/courses`, `/users`).
- HTTP verbs used idiomatically: `GET` read, `POST` create, `PUT` replace, `PATCH` modify, `DELETE` remove.
- JSON, `application/json`, UTF-8.
- URL versioned: `/v1/...`.
- Bodies use `camelCase`.

## URL Structure

```
/{version}/{resource}/{id?}/{sub-resource?}
```

Examples:
- `GET  /v1/courses?cursor=...&limit=20`
- `POST /v1/courses`
- `GET  /v1/courses/{id}`
- `PATCH /v1/courses/{id}`
- `POST /v1/courses/{id}/versions`

Platform-admin endpoints live under `/v1/platform/...` and require platform-admin scope.

## Versioning

Per [ADR-0024](../decisions/0024-api-versioning-policy.md):

- **URL-based:** `/v1`, `/v2`. Header-based versioning is not used.
- **No minor versions in the URL.** Non-breaking changes (additive fields,
  new optional query params, new endpoints under an existing resource,
  looser validation) ship to the same major.
- **Breaking changes require a new major.** The full breaking / non-breaking
  matrix lives in
  [ADR-0024 § What counts as a breaking change](../decisions/0024-api-versioning-policy.md).
- **Deprecation window: 6 months.** Every deprecated endpoint carries
  RFC 8594 `Sunset`, `Deprecation` (Unix-timestamp), and `Link:
  rel="successor-version"` HTTP response headers, plus OpenAPI
  `deprecated: true` + `x-sunset` / `x-successor` / `x-migration-guide`
  extensions for SDK codegen.
- **Two adjacent majors coexist.** Three concurrent majors is not supported.
- **Sunset response:** `410 Gone` with RFC 7807 Problem Details pointing at
  the successor endpoint and the migration-guide URL.
- **`/healthz` and `/readyz` are unversioned** infrastructure endpoints; the
  versioned API surface starts at `/api/v{N}/`.

The deprecation cadence applies to the *tenant-facing* `/api/v{N}/*` surface.
The internal `/api/internal/*` Hub contract has its own versioning per
[ADR-0019](../decisions/0019-learnstack-hub.md).

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
  "instance": "/v1/courses",
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

The tenant is **never** read from a request body or query param at the API edge. Resolution order:

1. Host header → registered domain → tenant id.
2. `tenant_id` claim in the JWT (studio / platform-admin contexts).
3. Explicit override in `/v1/platform/...` endpoints with proper scope.

A request that cannot resolve a tenant returns **404** (not 403, to avoid disclosure).

## Pagination

Cursor pagination by default:

```
GET /v1/courses?cursor=eyJpZCI6Li4ufQ&limit=20
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
POST /v1/orders
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
GET /v1/courses/{id}        → ETag: "7"
PATCH /v1/courses/{id}
  If-Match: "7"
```

Version mismatch returns **409** with `concurrency_conflict`.

## OpenAPI

- Generated from code (Swashbuckle), not handwritten.
- Available at `/openapi/v1.json`; Swagger UI at `/openapi/v1/`.
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

- Endpoint: `/v1/webhooks/{provider}`.
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
- Domain logic in controllers.
- Returning EF entities directly.
- Mixing API versions in a single endpoint.
- Inconsistent casing.
- Echoing internal error messages to clients.
