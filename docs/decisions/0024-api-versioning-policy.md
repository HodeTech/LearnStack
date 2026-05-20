# ADR-0024: API Versioning Policy

## Status

Accepted

**Date:** 2026-05-20
**Deciders:** @platform

## Decision Drivers

- **OpenAPI spec generation starts in Phase 02a Packet 4.** The first endpoint
  (`GET /v1/healthz`) ships today; before more endpoints land we owe a written
  versioning policy so the contract is stable from the first byte. Late-arriving
  policies create retroactive breaking-change classification ambiguity.
- **The URL prefix `/v1/` is already in code.** [Standards 04 § Versioning](../standards/04-api-design.md)
  commits to URL-based versioning (no header-based versioning). This ADR codifies
  *the rest* of the policy that bullets in that standard sketched but never
  fixed: deprecation cadence, exact header set, OpenAPI marking, SDK-generation
  implications, and the breaking/non-breaking rule.
- **Tenant-facing SDK consumers are the constraint.** The Hub HTTPS contract
  ([ADR-0019](0019-learnstack-hub.md)) is internal mTLS at four endpoints — its
  versioning is governed by the Hub repo's own ADR set, not this one. The
  externally-exposed contract is the *tenant-facing* `/api/v1/*` surface, hit by
  the typed SDK generated from OpenAPI, by tenant-side integrations, by webhook
  consumers, and (in Phase 04+) by the public storefront. Their upgrade cadence
  determines our deprecation window, not internal engineering velocity.
- **SaaS continuous-deploy vs. SelfHosted release cadence are different.** SaaS
  tenants get the latest binary on the next deploy; SelfHosted instances upgrade
  on their own clock (potentially quarterly). A deprecation window that works
  only for SaaS leaves SelfHosted tenants stuck on dead endpoints; one that
  works only for SelfHosted slows everyone else. The policy has to accommodate
  both via the **30-day grace** model of ADR-0020.
- **RFC 8594 (`Sunset` header) and `Deprecation` header (Internet-Draft / `IETF
  draft-ietf-httpapi-deprecation-header`) are the industry-standard signalling
  mechanism.** Stripe, GitHub, Google, Twilio all use this pair. Inventing a
  proprietary signalling scheme on a multi-tenant SaaS in 2026 is a self-inflicted
  wound.

## Considered Options

1. **6-month deprecation window + RFC 8594 `Sunset` header + `Deprecation` header
   + OpenAPI `deprecated: true` + `x-sunset` extension** (chosen).
2. **3-month deprecation window** (rejected). Faster internal iteration, but
   too tight for tenant-side SDK consumers — a tenant on a quarterly release
   train cannot reasonably adapt to a Q1 deprecation announcement and ship the
   fix before Q2.
3. **12-month deprecation window** (rejected). Safer for the slowest SelfHosted
   tenants, but doubles the cost of dual-version maintenance on every breaking
   change. With Phase 11+ projected to see real version-2 endpoints, paying
   12-month maintenance debt on each one is more than the gain.
4. **Header-based versioning (`X-API-Version: 1`)** (rejected at the Standards 04
   level already). URL versioning is industry-default for tenant-facing APIs;
   it makes routing trivial in APISIX, lets gateways enforce version-specific
   rate limits and quotas, and shows up plainly in access logs.

## Decision

LearnStack's tenant-facing HTTP API uses **URL-based versioning** (`/v1/*`,
`/v2/*`, …) with a **6-month deprecation window** signalled by RFC 8594-style
headers and OpenAPI metadata.

### The version axis

- **Major versions in the URL.** A new major version is the only way a breaking
  change reaches a tenant. Mainline development continues in `/v1`; `/v2`
  appears only when a breaking change is unavoidable and a new major has been
  ADR'd at architecture-doc level.
- **No minor versions in the URL.** All non-breaking changes (additive fields,
  new optional query params, new endpoints under an existing resource, looser
  validation) ship to the same major.
- **Two adjacent majors coexist.** While `/v2` is current, `/v1` runs in parallel
  for its full deprecation window. Three concurrent majors (`/v1` + `/v2` + `/v3`)
  is not supported; cutting `/v3` requires `/v1` to have reached its Sunset
  date first.

### What counts as a breaking change

| Breaking | Non-breaking |
|---|---|
| Removing a field from a response | Adding a field to a response |
| Renaming a field in a response | Adding a new endpoint |
| Removing a field from a request | Adding an optional query param |
| Changing the type of a field | Loosening a validator (more inputs accepted) |
| Adding a required request field | Adding a new value to an open-set enum, when documented |
| Removing an enum value from a response | Adding a header to a response |
| Tightening a validator (rejecting inputs that used to pass) | Re-ordering JSON object keys |
| Changing HTTP status codes for an outcome | Improving an error message under an unchanged code |
| Renaming a path segment | Changing internal implementation |

Closed-set enums (e.g. `OrderStatus`) treat *additions* as breaking from a
consumer perspective unless the OpenAPI spec marks the enum as **open-set**
(`x-extensible-enum: true`) — open-set enums explicitly invite forward-compat
clients.

### Lifecycle of a deprecated endpoint

1. **T = 0 (announcement).** New major `/v2/*` ships. The old `/v1/*` endpoint
   acquires:
   - `Deprecation: @<unix-timestamp>` HTTP response header on every call
     (per `draft-ietf-httpapi-deprecation-header-06`).
   - `Sunset: <RFC 9651 HTTP-date>` HTTP response header pointing 6 months
     into the future (per RFC 8594).
   - `Link: <https://docs.learnstack.dev/v2/migration>; rel="successor-version"`
     HTTP response header pointing at the migration guide.
   - OpenAPI spec marks the operation `deprecated: true` and adds
     `x-sunset: <ISO 8601>` and `x-successor: /v2/...` extensions for SDK
     codegen to surface.
2. **T = 0 → T = 6mo (parallel run).** Both versions accept traffic, are
   monitored, and emit identical audit-log entries (same operation key, version
   stamped in `metadata.api_version`). Per-tenant usage telemetry surfaces
   "tenant X is still on `/v1/Y`" so account managers can reach out.
3. **T = 6mo (sunset).** The `/v1/*` endpoint returns
   `410 Gone` with RFC 7807 Problem Details:
   ```json
   {
     "type": "https://learnstack.dev/problems/api-version-sunset",
     "title": "API version sunset",
     "status": 410,
     "detail": "GET /v1/courses was sunset on 2027-01-15. Use GET /v2/courses.",
     "successor": "/v2/courses",
     "migrationGuide": "https://docs.learnstack.dev/v2/migration"
   }
   ```

### Per-deployment-mode behaviour

- **SaaS:** the 6-month clock starts on the SaaS release cutting the `/v2`. All
  SaaS tenants migrate within the window; tenant-managed integrations that miss
  the window get `410 Gone`.
- **Dedicated:** identical to SaaS; the dedicated cluster operates on the same
  binary cadence.
- **SelfHosted (Online + Air-Gapped):** the 6-month clock starts on the
  SelfHosted release that introduces `/v2`. Self-hosted operators may delay
  upgrading past sunset; their `/v1` traffic continues to return `410 Gone` on
  the binary that has the new release applied. Operators who want longer parallel
  coexistence can stay on the prior binary inside their 30-day grace; this is a
  customer-side choice, not a platform commitment.

### OpenAPI marking

Every operation in the OpenAPI spec carries:

```yaml
get:
  operationId: getCourse
  deprecated: false
  x-version-introduced: v1
  responses:
    '200':
      ...
```

Deprecated operations add:

```yaml
get:
  operationId: getCourse
  deprecated: true
  x-version-introduced: v1
  x-sunset: 2027-01-15T00:00:00Z
  x-successor: /v2/courses/{id}
  x-migration-guide: https://docs.learnstack.dev/v2/migration#getCourse
```

The SDK generator reads these to:

- Mark generated SDK methods `[Obsolete("...")]` with the sunset date.
- Surface the migration-guide URL in the method's XML doc comment.
- Emit a compile-time warning that turns into an error 30 days before sunset.

## Context

### Why 6 months and not 3 / 12

We measured against three signals:

- **Tenant-side SDK regeneration cadence.** Tenant integrations are typically
  on Node or .NET stacks with quarterly release trains. A 3-month window asks
  every tenant to ship two consecutive releases against a breaking change; a
  6-month window comfortably fits one cycle.
- **Dual-maintenance cost.** Every endpoint in a deprecation window has to be
  kept correct in both majors, audited in both majors, tested in both majors.
  Doubling that cost to 12 months for every breaking change is more dual-version
  carrying than the slow-SelfHosted tail warrants.
- **Industry benchmarks.** Stripe → 12 months (very conservative, billing-grade
  external surface). GitHub → 12 months (DX-sensitive). Twilio → 6 months
  (similar shape to LearnStack — multi-tenant SaaS with SDK consumers).
  Auth0 → 6 months (very similar mix of tenant-facing + admin surface).
  LearnStack's "tenant-facing API + SDK + internal Hub" mix puts us in the
  Twilio/Auth0 cluster, not the Stripe billing cluster.

### Why URL versioning and not header versioning

- **APISIX (ADR-0015) routes on URL prefix.** Different version routes can carry
  different rate-limit budgets, JWT realm requirements, plugin chains without
  any conditional logic. Header-based versioning would push routing decisions
  into post-gateway middleware.
- **Caching layer (Valkey via Dapr) keys on URL.** Header-based versioning would
  require explicitly varying cache keys on `X-API-Version`; URL versioning is
  cache-friendly by construction.
- **Observability is cleaner.** Access logs, OTel traces, audit-log entries
  all carry the URL natively; we never have to "lookup the header to find the
  version" downstream.

### Why we accept the cost of dual-version maintenance

Every breaking change carries a real cost: tests run against both majors, audit
config covers both, RLS policies are version-agnostic but the endpoints that
hit them differ. This cost is the *price* of being a reasonable steward of the
contract; the alternative (no versioning, breaking changes shipped silently)
is not a platform, it's a script we run.

### What would change our minds

- A tenant-facing SDK consumer base large enough that 6 months is empirically
  insufficient (telemetry: > 5% of tenants still on `/v1/X` after 5 months from
  announcement on three consecutive breaking changes).
- A regulatory requirement forcing 12-month coexistence (e.g. financial-sector
  audit reproducibility).
- An OpenAPI/SDK toolchain change that makes dual-version maintenance cheap
  enough to justify a longer window — for instance, if every endpoint were
  expressible as a versioned schema with an `If-Match`-style version negotiator
  built in.

### What we explicitly punted on

- **Per-endpoint deprecation overrides.** All deprecations get 6 months; we do
  not allow per-endpoint shorter or longer windows. A "this endpoint needs 3
  months because it's wrong" case is rare enough that the policy refuses to
  encode it; revisit if it actually happens.
- **Pre-v1 / `/api/v0`.** No `/v0/*` endpoints exist or will exist; `/v1` is
  the first contract.
- **Internal `/api/internal/*` (Hub) versioning.** That surface uses its own
  versioning per [ADR-0019](0019-learnstack-hub.md); this ADR governs only
  tenant-facing `/api/v*/*`.

## Consequences

### Positive

- Single, documented breaking-change policy from the first endpoint forward.
- Tenant-side SDK consumers get RFC-standard signalling — they can build their
  own automation against `Sunset` / `Deprecation` headers without
  LearnStack-specific tooling.
- OpenAPI spec is the contract; SDK generation reads it directly. No
  out-of-band documentation lookup.
- APISIX routing and observability stay clean (URL-based).
- 410 Gone with Problem Details means a tenant migration that lapsed gets a
  precise, machine-readable error — not an opaque 404.

### Negative

- Every major version doubles maintenance temporarily (6 months parallel).
  Architecture tests + integration tests run against both surfaces; CI cost
  grows linearly with the number of deprecation windows currently open.
- The 6-month commitment ties LearnStack's hands on the slowest-moving tenant.
  A tenant whose CTO took a sabbatical mid-window will be locked out at month
  6+1; the customer-success process has to surface this proactively.
- Open-set enum decisions (`x-extensible-enum: true`) become semi-permanent —
  flipping an enum from open to closed is itself a breaking change.

### Neutral

- SelfHosted tenants opt into their own version cadence by their upgrade
  schedule; the platform doesn't try to enforce a global clock.
- The Hub contract is unaffected and governed elsewhere.

## Implementation Notes

- **Controller / endpoint registration:** every endpoint sits under `/api/v{N}/`
  in the routing table; no version-less endpoints exist. Phase 02a Packet 4
  wires the URL convention via ASP.NET Core route conventions.
- **OpenAPI generation:** Phase 02a Packet 4 also wires Microsoft.OpenApi /
  `Microsoft.AspNetCore.OpenApi` to emit `/openapi/v{N}.json` per major and to
  read the `x-version-introduced`, `x-sunset`, `x-successor`, `x-migration-guide`,
  and `x-extensible-enum` extensions from attribute metadata.
- **Sunset header emission:** an ASP.NET Core middleware
  (`ApiVersioningHeadersMiddleware`) reads attribute metadata on the matched
  endpoint and appends `Deprecation` / `Sunset` / `Link` headers when the
  endpoint is marked `[Deprecated(sunset: "...", successor: "...")]`.
- **Architecture test (lands when the first `/v2` endpoint is added):**
  `Every_Deprecated_Endpoint_Has_Sunset_And_Successor` — every controller
  action with `[Obsolete]` declares `Sunset` + `Successor`. Catalogued under
  [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md).
- **Architecture test (lands in Phase 02a Packet 4):**
  `Every_Endpoint_Is_Under_Versioned_Route` — no controller action exists
  outside `/api/v{N}/...`.
- **`/healthz` exemption:** `GET /healthz` and `GET /readyz` are unversioned;
  they are infrastructure endpoints consumed by the orchestrator, not the
  versioned API surface. The architecture test allow-lists them by name.
- **SDK codegen:** the generated typed SDK (`packages/sdk`) ships a class per
  major; `LearnStackClient.V1` and (later) `LearnStackClient.V2` coexist for
  one cycle. The codegen reads `x-sunset` to emit `[Obsolete]` with the
  sunset date.
- **Migration guide URL convention:** `https://docs.learnstack.dev/v{N}/migration`
  is the public landing page; the `x-migration-guide` extension narrows to the
  per-endpoint anchor (`#getCourse`, …). The actual hosting of `docs.learnstack.dev`
  is a deployment concern — for the pre-MVP phase the URL resolves to a 404
  placeholder; the *header pattern* is the contract from Day 1.

## Amendments

_(none yet)_

## References

- [Standards 04 § Versioning](../standards/04-api-design.md)
- [ADR-0015 API Gateway with APISIX](0015-api-gateway-apisix.md) — APISIX routes on
  URL prefix; URL versioning is gateway-friendly.
- [ADR-0019 LearnStack Hub](0019-learnstack-hub.md) — the four-endpoint internal
  `/api/internal/*` surface has its own versioning rules.
- [ADR-0020 Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md)
  — SaaS / Dedicated / SelfHosted cadence model; the 30-day grace touches
  per-deployment-mode behaviour here.
- [RFC 8594 — The Sunset HTTP Header Field](https://datatracker.ietf.org/doc/html/rfc8594).
- [draft-ietf-httpapi-deprecation-header — The Deprecation HTTP Header Field](https://datatracker.ietf.org/doc/html/draft-ietf-httpapi-deprecation-header).
- [RFC 7807 — Problem Details for HTTP APIs](https://datatracker.ietf.org/doc/html/rfc7807).
