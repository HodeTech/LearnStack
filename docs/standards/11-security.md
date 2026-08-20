# 11 — Security Standards

**Status:** Active
**Derives from:** [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
(Amendment 1: Organization Scope; **Amendment 3: corrected RLS template, database role
model, and session-variable placement**),
[ADR-0004 Authentication Strategy](../decisions/0004-authentication-strategy.md)
(Amendment 1: `learnstack-hub` realm),
[ADR-0015 API Gateway: APISIX](../decisions/0015-api-gateway-apisix.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md),
[ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md),
[ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md),
[ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md),
[ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md).

Security is layered. No single control is sufficient. The standards here apply to every PR.

## Threat Model Summary

LearnStack defends against:

- Cross-tenant data leakage (the highest-severity class).
- Authentication bypass (token theft, weak session handling).
- Authorization bypass (privilege escalation within a tenant).
- Code injection (SQL, XSS, command, deserialization).
- Webhook spoofing.
- File-upload abuse (malware, polyglot files, path traversal).
- Denial of service (rate, payload size, expensive queries).
- Sensitive data exposure (logs, errors, exports).

## OWASP Top 10 (2021) Coverage

Every security review walks this lens before signing off.

| OWASP | LearnStack control |
|-------|---|
| A01 Broken Access Control | 4-step auth order (auth → tenant membership → role/permission → resource scope); `[Authorize(Policy = ...)]` on every endpoint; RLS as defense in depth. |
| A02 Cryptographic Failures | TLS 1.2+; secrets in secret manager; Keycloak-managed credentials; no handwritten crypto. |
| A03 Injection | EF Core LINQ + parameterised raw SQL; React auto-escape; CSP nonces; DOMPurify wrapper for `dangerouslySetInnerHTML`. |
| A04 Insecure Design | Domain invariants in aggregates, not controllers; cross-module rules go through [01-architecture-standards.md](01-architecture-standards.md) § Distributed-Consistency Tiers. |
| A05 Security Misconfiguration | Strict secure headers (HSTS, CSP, COOP, CORP); container hygiene; no public buckets. |
| A06 Vulnerable & Outdated Components | Renovate/Dependabot; CVE patch SLA 7 days; locked install in CI. |
| A07 Identification & Auth Failures | OIDC via Keycloak; PKCE only; `HttpOnly` cookies; MFA enforcement per role. |
| A08 Software & Data Integrity Failures | Webhook HMAC verification; outbox + idempotency keys; signed images by digest. |
| A09 Logging & Monitoring Failures | OpenTelemetry traces/metrics/logs; correlation id end-to-end; Sentry on errors; audit log on privileged ops ([18-audit-coverage.md](18-audit-coverage.md)). |
| A10 SSRF | Outbound calls allow-listed; rendered outbound URLs validated against an allow-list before emit. |

## Multi-Tenant + Organization Isolation Review Checklist

A PR that touches tenant- or organization-owned data, queries, or background jobs must
answer **yes** to each item, or attach a written justification:

- [ ] Every new tenant-owned entity is `[TenantOwned]` and has both an EF query filter
      and a Postgres RLS policy.
- [ ] Every new **organization-scoped** entity carries `OrganizationId` (nullable when
      the entity may be tenant-wide) and has an org-aware EF query filter + RLS policy
      per [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md). The
      architecture test `Every_OrgScoped_Entity_HasOrgIdAndFilter` checks the marker.
- [ ] No `IgnoreQueryFilters()` outside platform-admin code paths (Roslyn-allowlisted +
      audit-logged).
- [ ] Every background job payload carries `TenantId` (and `OrganizationId?` when
      relevant); the worker sets ambient tenant + org before any work.
- [ ] Every integration event payload carries `tenant_id` (and `organization_id?` when
      relevant); consumers restore tenant + org context before handling.
- [ ] Raw SQL queries (if any) include `tenant_id` (and `organization_id` when
      applicable) in the predicate explicitly.
- [ ] Cache keys, search index names/filters, storage prefixes, and metric labels all
      carry tenant id (and org id where applicable) — never as a high-cardinality
      metric label, see [10-observability.md](10-observability.md).
- [ ] At least one tenant-isolation integration test asserts that tenant A cannot read
      tenant B's data via the new surface, **and** that org X cannot read org Y's data
      within the same tenant where the entity is org-scoped.

## Transport

- **TLS everywhere.** HTTP redirects to HTTPS at the edge.
- HSTS enabled (`max-age=31536000; includeSubDomains; preload`).
- Minimum TLS version: 1.2; prefer 1.3.
- Certificates auto-renewed via Let's Encrypt or cloud-managed certs.

## HTTP Headers

Every response from the API and the rendered apps must set:

| Header | Value |
|--------|-------|
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` (except embeddable LTI surfaces) |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Permissions-Policy` | restrict to required APIs only (`camera`, `microphone` for classroom routes) |
| `Content-Security-Policy` | strict with nonces; documented per app surface |
| `Cross-Origin-Opener-Policy` | `same-origin` |
| `Cross-Origin-Resource-Policy` | `same-site` |

## Authentication

LearnStack runs **two Keycloak realms** per
[ADR-0004 Amendment 1](../decisions/0004-authentication-strategy.md):

- `learnstack` — the tenant-facing realm. All tenant admins, instructors, and learners
  authenticate here. The realm includes the tenant id in token claims; the API
  re-validates against the resolved tenant context.
- `learnstack-hub` — the **operator realm**, used **only** by the Hub operator portal
  (`hub.learnstack.dev`). Tenant-facing apps **never** accept `learnstack-hub` tokens;
  the internal API (`/api/internal/*`) **only** accepts `learnstack-hub` tokens plus
  the mTLS client certificate.

General rules:

- OIDC. No handwritten password code; no handwritten token rotation.
- Refresh tokens stored as `HttpOnly`, `Secure`, `SameSite=Lax` cookies; never
  accessible to JS.
- Access tokens short-lived (≤ 1 hour) and refreshed silently by the BFF.
- MFA enrollment supported for tenant-admin roles; required for platform-admin **and**
  for every Hub operator account.
- Password policy delegated to Keycloak: minimum 12 chars, breach check via HIBP, no
  password reuse for the last 5.
- Account lockout after 5 failed attempts in 10 minutes; lifted after 15 minutes or
  admin unlock.

## Ingress / Gateway (APISIX)

All tenant-facing traffic enters through **APISIX** in standalone mode per
[ADR-0015](../decisions/0015-api-gateway-apisix.md). The gateway is a defense-in-depth
layer, not the sole control — the API re-verifies everything.

> **When this becomes live.** APISIX is demand-gated per
> [ADR-0035](../decisions/0035-demand-gated-infrastructure.md): its trigger is the first
> non-development deployment that needs edge rate limiting, host routing, or JWT
> pre-validation, and it lands in
> [Phase 11](../roadmap/phase-11-production-hardening.md). Until then ASP.NET middleware
> carries the same responsibilities in-process. Nothing in this section is optional once
> the gateway is in front of production traffic; none of it is a reason to weaken the
> in-process control, because the API re-verifies either way.

- `jwt-auth` plugin verifies the access token against the `learnstack` realm's public
  key set; the API also verifies. Both must pass.
- `cors` plugin handles preflight; authenticated cross-origin traffic is allow-listed
  per environment.
- `limit-req` / `limit-count` plugins enforce the rate-limit policy below.
- The `/api/internal/*` route set is gated by **mTLS configured on the APISIX SSL
  object** (`client.ca` + `client.depth` per APISIX 3.x SSL-config; mTLS is not a
  route plugin) plus an `ip-restriction` (and, when applicable, `consumer-restriction`)
  route plugin that only admits the documented Hub egress; the client certificate
  must be signed by the LearnStack-internal CA. The route-level pattern is shown in
  the commented `/api/internal/*` stub in `infra/apisix/apisix.yaml`.
- Gateway config lives in `infra/apisix/` as YAML, version-controlled. No live edits.

Direct ingress to backend pods (bypassing APISIX) is blocked at the network policy
level.

## Hub Contract Surface

The LearnStack ↔ Hub API is a **separate, narrow** surface with stronger controls than
tenant-facing endpoints. Per [ADR-0019](../decisions/0019-learnstack-hub.md) and
[ADR-0034](../decisions/0034-hub-contract-surface-invariant.md):

- **mTLS** with LearnStack-internal CA-signed client certs. Certs are rotated yearly.
- **Signed JWT (RS256)** carrying `iss`, `aud=learnstack-internal`, `exp ≤ 5 min`,
  `jti` (replay-protected via short-TTL inbox).
- **HMAC body signature** in the `X-Signature` header (HMAC-SHA256 of the raw body
  with a per-deployment shared secret).
- All three layers must validate on **every** endpoint in the surface, but they fail at
  two different layers. `/api/internal/*` is bound to its own mTLS listener and is never
  proxied by APISIX, so a missing, expired or untrusted client certificate is rejected
  **during the TLS handshake** — no HTTP request reaches the application, so there is no
  status code and no body to leak. Only the JWT and HMAC checks return `401`, with no
  detail. Do not write a handler that returns `401` for a certificate failure; it would
  never be reached, and its existence implies a listener that terminates TLS without
  requiring the client certificate.
- The surface is governed by two invariants, not by an endpoint count: the Hub stores no
  tenant content, and every crossing goes through `IEntitlementProvider`,
  `IUsageReporter`, or `IHubTenantSync`. The enumerated endpoint set lives in
  [20-infrastructure-stack.md § Hub HTTPS Contract Surface](20-infrastructure-stack.md);
  adding an endpoint still requires an ADR, because the surface is a cross-repository
  contract.
- **TLS certificates and private keys never travel in the entitlement payload.** That
  payload is cached, logged, audited and mirrored. Cert material moves by secret-store
  replication and is referenced from `PUT /api/internal/tenants/{id}/host-mappings` by
  path, never by value ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)).
- **Host resolution never calls the Hub.** `IHostToTenantResolver` reads
  `platform_host_to_tenant` and nothing else, so a Hub outage cannot take anonymous
  public pages down.

## Authorization

Every write use case checks, in order:

1. **Authentication** (valid token).
2. **Tenant membership** (user has a Membership in the resolved tenant).
3. **Role / permission** (user's roles include the required permission).
4. **Resource scope** (e.g. instructor can only edit their own courses).

A failure at any step returns a Problem Details response with the right code (`unauthorized`, `tenant_mismatch`, `forbidden`, `resource_scope_violation`).

## Tenant + Organization Isolation

See [docs/architecture/09-tenant-isolation.md](../architecture/09-tenant-isolation.md)
for the full strategy. Standards-side:

- Every `[TenantOwned]` entity has a query filter and an RLS policy. Architecture tests
  enforce this.
- Every `[OrganizationScoped]` entity additionally carries an `OrganizationId` column
  and an org-aware EF filter + RLS policy
  ([ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)). Nullable
  `OrganizationId` means the row may be tenant-wide.
- `IgnoreQueryFilters()` is allowed only in platform-admin code paths with a
  Roslyn-allowlist attribute and an audit-log call.
- Background jobs **must** receive `TenantId` (and `OrganizationId?`) in their
  payload; jobs without it fail at registration.
- The `app.tenant_id` and `app.organization_id` session variables are set with
  `SET LOCAL` inside the ambient transaction — see § Tenant Context immediately below,
  which is the single authority for that placement.

## Tenant Context

**This section is the single authority for RLS session-variable placement.** Every other
document links here rather than restating the mechanism.

Row Level Security predicates read four PostgreSQL session variables — `app.tenant_id`,
`app.organization_id`, `app.scope`, and `app.resolving_host` — whose canonical spellings
and canonical policy templates live in
[05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md) and
[05-database.md § Table classes](05-database.md). This section fixes **where** the first
three are set. `app.resolving_host` is set by `CachedHostToTenantResolver` alone, in its
own short read-only transaction before the host lookup, because the row that determines
the tenant must be readable before any tenant context exists; it is read by exactly one
policy, on `platform_host_to_tenant`. Its value is the **normalized effective host** as
`EffectiveHost.Normalize` produces it — the single producer of both this variable and
the lookup key, per
[ADR-0036 § Effective host and the trusted hop](../decisions/0036-tenant-resolution-trusted-inputs.md).
That ADR, not this section, is the authority for *which* request input the host comes
from; this section remains the authority for session-variable **placement** only.

### The rule

`app.tenant_id`, `app.organization_id` **and `app.scope`** are set with **`SET LOCAL`,
inside the ambient transaction, as the first statement after it opens** — in practice by
`TransactionBehavior` (step 6 of the MediatR pipeline), from the `ITenantContext` that
`TenantContextBehavior` asserted at step 4.

`SET LOCAL` and `set_config(name, value, true)` are **transaction-local**. PostgreSQL
discards them when the transaction ends, and they have no effect at all outside one. Two
consequences follow, and both are load-bearing:

- **Not from a MediatR behavior that runs before `TransactionBehavior`.** A value set at
  step 4 is gone before the step-6 transaction opens, so every subsequent query runs with
  an unset `app.tenant_id`.
- **Not from a `DbConnectionInterceptor`.** Interceptors fire when the connection opens,
  not when a transaction starts. Under PgBouncer transaction pooling the connection is
  shared across transactions, so the value is either absent or — worse — left over from
  another tenant's transaction.

Because every `current_setting` read is called with its missing-OK argument (`true`)
**and** wrapped in `NULLIF(…, '')`, both an unset and a reset variable yield `NULL` and
the policy predicate filters the row out. The failure mode is an empty result set, not a
leak — but an empty result set arriving from production is an outage, so a
`DbCommandInterceptor` additionally asserts that `TransactionBehavior` has already issued
the `SET LOCAL` pair before any command against a `[TenantOwned]` table runs, and throws
`TenantContextMissingException` when it has not. It cannot be a connection-checkout
interceptor, for the same reason it cannot be a `DbConnectionInterceptor` that *sets* the
values: checkout precedes the transaction, so the transaction-local value is not there to
be observed ([05-database.md § Connection Management](05-database.md)).

### Corrections this supersedes

Six other places previously described different placements. All are corrected; if a
stale copy surfaces, this section wins:

| Document | Previously said | Now |
|---|---|---|
| [02-backend-coding.md § Pipeline Behaviors](02-backend-coding.md) | Pipeline step 4 (`TenantContextBehavior`) sets the variables via a `DbConnectionInterceptor` | Step 4 asserts and carries tenant context; step 6 sets the variables inside the transaction, and links here |
| Phase 02a Packet 3 `TenantContextBehavior` code TODO | Names a `DbConnectionInterceptor` as the mechanism | Corrected in [Phase 02a Packet 3b](../roadmap/phase-02a-kernel-tenancy.md) to point here and at Packet 7, which implements it |
| [33-cross-cutting-concerns.md § Pipeline](../architecture/33-cross-cutting-concerns.md) | Step 4 sets the RLS GUCs via a `DbConnectionInterceptor` | Step 4 asserts only; step 6 issues `SET LOCAL` inside the transaction |
| [ADR-0032 § Sub-decision 2](../decisions/0032-exception-handling-logging-and-observability.md) diagram | `TenantContextBehavior (assert resolved; set RLS GUC)` | Corrected in that ADR's Amendment 2, item 3 |
| [09-tenant-isolation.md § Isolation flow](../architecture/09-tenant-isolation.md) mermaid | The accessor issues `SET LOCAL` from middleware, before any transaction exists | The transaction issues it at step 6 |
| [Phase 02a § Cross-cutting Concerns](../roadmap/phase-02a-kernel-tenancy.md) | `TenantContextBehavior` (asserts resolved + sets RLS GUCs) | Asserts only; `TransactionBehavior` sets them |

Neither error was visible in review because the corpus described the *layers* correctly
while describing the *ordering* wrongly, and no code exercised the ordering yet. The
implementation lands in
[Phase 02a Packet 7](../roadmap/phase-02a-kernel-tenancy.md).

### Related authorities

- [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md) — the
  corrected RLS policy template, the four-role database model, and the rule that
  isolation tests connect as `learnstack_app`.
- [05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md) — the
  single canonical SQL template. It is not repeated here, or anywhere else.
- [05-database.md § Connection Management](05-database.md) — PgBouncer transaction-mode
  pooling, which this rule depends on.

A test that connects as the table owner or as a `BYPASSRLS` role passes even when every
policy is inert. Isolation tests connect as `learnstack_app`; the suite is a
[Phase 02a Packet 7](../roadmap/phase-02a-kernel-tenancy.md) deliverable.

## Secrets and Configuration

- Every secret read goes through **`ISecretProvider`**. The registered implementation is
  `ConfigurationSecretProvider` until Vault's trigger fires — a production secret must
  rotate without a redeploy, or more than one operator needs access to production
  secrets — at which point
  `DaprSecretProvider` → Vault takes over per
  [ADR-0014](../decisions/0014-adopt-dapr.md) and
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md). Call sites are identical
  either way. `.env.example` is checked in; `.env` is gitignored.
- Secret namespace: `learnstack/{deployment}/{module}/{key}`. The deployment segment is
  `development | saas | dedicated | selfhosted`.
- Production secrets rotated at least every 90 days where rotation is feasible (DB
  passwords, provider API keys, Hub HMAC shared secret, mTLS client certs).
- Secret access via `ISecretProvider` is logged.


## File Uploads

- Validate MIME type with content sniffing, not just the `Content-Type` header.
- Validate extension against an allow-list per content type.
- Enforce per-content-type size limits. The numbers and the tenant-override rule are
  owned by [16-media-pipeline.md § Validation](../architecture/16-media-pipeline.md);
  three documents used to state them and disagreed three ways about the same image.
- Strip EXIF where appropriate.
- Store in tenant-scoped object storage prefix.
- Never trust the original filename. Generate a server-side key under the canonical tenant prefix: `tenants/{tenantId}/{category}/{uuid}.{ext}` (with `organizations/{orgId}/` segment for org-scoped assets), per [09-tenant-isolation.md § Storage (SeaweedFS)](../architecture/09-tenant-isolation.md) and [16-media-pipeline.md § Key Layout](../architecture/16-media-pipeline.md).
- Virus scan hook (ClamAV or cloud equivalent) before files become accessible.
- Signed URLs for private files; TTL ≤ 1 hour.

## SQL & ORM

- Use parameterized queries everywhere. No string interpolation into SQL.
- EF Core LINQ is preferred; raw SQL only with explicit `FromSqlInterpolated` and parameterized values.
- No string concatenation with user input.
- Stored procedures are not used; the application owns the logic.

## XSS & Output Encoding

- React encodes by default — preserve that.
- `dangerouslySetInnerHTML` only through a sanitization wrapper (DOMPurify) with a documented policy.
- CSP nonces enforce inline-script restrictions.
- Markdown rendered via a library with allowlist sanitization.
- Email templates use a templating engine with HTML-escape default.

## CSRF

- Cookie sessions use `SameSite=Lax`.
- Server Actions verify the Auth.js session implicitly.
- Mutating fetches from JS include a CSRF token bound to the session.
- Webhook endpoints verify signatures, not CSRF tokens.

## CORS

- Default deny.
- Allowed origins explicit per environment.
- Studio and Portal apps share a single origin with the API (no cross-origin requests needed).
- Tenant custom domains use server-side rendering; client-side cross-origin to the API uses CORS preflight on a controlled allow-list.

## Rate Limiting

| Surface | Limit |
|---------|-------|
| `/api/v1/auth/*` (login, password reset, register) | 5 req/min per IP |
| Anonymous API | 60 req/min per IP |
| Authenticated API | 600 req/min per token |
| Write endpoints | 60 req/min per token |
| Webhook endpoints | 1000 req/min per provider |
| Hub internal API (`/api/internal/*`) | 60 req/min per mTLS client cert |

429 responses include `Retry-After`. Rate-limit policy lives at **APISIX**
(`limit-req` / `limit-count` plugins) plus a per-handler ASP.NET layer for finer grain
where plan-level `LimitKeys.MaxApiRequestsPerHour` differs per tenant.

## Webhooks (Inbound)

- HMAC signature verification before any work runs.
- Reject events older than 5 minutes (replay protection).
- Idempotency: `(provider, event_id)` stored; duplicates ignored.
- Tenant id derived from the stored provider account, never trusted from the payload.

## Logging Hygiene

- Never log secrets, passwords, tokens, full card numbers, full national ids, or full email bodies.
- Redact at log-builder level via a property filter.
- PII-sensitive fields (email, phone) are hashed in analytics; logged in plain only when necessary, with explicit allow-list.
- Tracing tags exclude `Authorization` headers.

## Error Messages

- Server-side messages explain what happened; client-facing messages avoid disclosure.
- 404 used to hide cross-tenant existence.
- Stack traces never reach clients.
- Internal correlation id surfaced to clients to support back-channel debugging.

## Dependency Hygiene

- Renovate / Dependabot enabled.
- Critical / high vulnerabilities patched within 7 days.
- Lockfiles committed.
- No transitive dependency mismatch — `npm ci` / `dotnet restore --locked-mode` in CI.

## Container & Infrastructure

- Base images pinned by digest.
- Run as non-root.
- Read-only filesystem where possible.
- Drop unnecessary Linux capabilities.
- Image vulnerability scan in CI (Trivy or equivalent).
- Cluster ingress restricted to documented ports.

## Live Classroom Specifics

- Join tokens scoped per user + room + role; TTL ≤ 1 hour.
- Identity includes tenant id (`{tenantId}:{userId}`) to prevent room-name collision across tenants.
- Recording indicator shown to all participants when recording is active, regardless of who started it.
- Recording consent enforced; see [docs/architecture/16-media-pipeline.md](../architecture/16-media-pipeline.md).
- LiveKit webhook secret rotated quarterly.

## Audit Log

Every privileged operation writes an audit log entry per
[ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
(supersedes ADR-0016) and the [18 Audit Coverage Standard](18-audit-coverage.md).
Coverage is **MUST / SHOULD / MAY** per module-operation; the MediatR
`AuditLogBehavior` writes through `IAuditStore` — modules never write `audit_log`
directly. Entries are append-only and queryable by tenant admins for their own tenant
(org-admins for their org).

Security-relevant durability rules:

- **MUST-class audit fails closed.** The row is enrolled in the same
  `DbContext.SaveChanges` as the business write, so a privileged operation cannot commit
  unaudited. It also means the insert runs while `app.tenant_id` is set, which is what
  lets Row Level Security accept it.
- **A failure to read `AuditConfig` fails closed too.** A tenant override may narrow
  SHOULD/MAY coverage; it may never remove baseline MUST coverage.
- SHOULD/MAY-class audit stays best-effort, and its accepted loss is written down rather
  than assumed.
- Monthly partitioning and the retention job land in
  [Phase 11](../roadmap/phase-11-production-hardening.md) per
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) — audit correctness
  cannot be retrofitted, audit scale can.

## Incident Response

- A security incident playbook lives in `docs/runbooks/security-incident.md` (Phase 11 deliverable).
- The team rotates an on-call slot for production.
- Critical incidents trigger pager + Slack channel; post-mortem within 7 days.

## Forbidden

- Storing passwords or password derivatives in any LearnStack table.
- Storing third-party tokens in plain text — encrypt at rest if storage is unavoidable.
- Trusting `tenant_id` or `organization_id` from a request body or query param. Both
  come from authenticated context only.
- Implementing custom crypto. Use established libraries.
- Disabling RLS in production for any reason short of an investigated incident with an
  ADR.
- Echoing user-supplied HTML without sanitization.
- Logging full request/response bodies.
- Calling Hub endpoints from anywhere except the dedicated `IEntitlementProvider` /
  `IUsageReporter` / `IHubTenantSync` adapters (see
  [20-infrastructure-stack.md](20-infrastructure-stack.md)).
- Accepting `learnstack-hub` realm tokens on tenant-facing endpoints, or accepting
  `learnstack` realm tokens on `/api/internal/*` endpoints.
- Bypassing APISIX with direct backend ingress.
