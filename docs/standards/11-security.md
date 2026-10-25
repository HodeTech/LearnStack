# 11 — Security Standards

**Status:** Active
**Derives from:** [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
(Amendment 1: Organization Scope; **Amendment 3: corrected RLS template, database role
model, and session-variable placement**),
[ADR-0004 Authentication Strategy](../decisions/0004-authentication-strategy.md)
(Amendment 1: `learnstack-hub` realm),
[ADR-0067 Development Tenant Access-Token Issuance](../decisions/0067-development-tenant-access-token-issuance.md)
(the configured development issuance lifespan and provider timestamp semantics),
[ADR-0070 Owned Tenant BFF Sessions](../decisions/0070-owned-tenant-bff-sessions.md)
(native custody, browser transactions and development provider event storage),
[ADR-0071 Uncertain AccountSite Refusals](../decisions/0071-uncertain-account-site-refusals.md)
(ordinary-read status mapping and positive revision publication),
[ADR-0068 Development Warm-Realm Qualification](../decisions/0068-development-warm-realm-qualification.md)
(the bounded preparation and stock-console assignment authority),
[ADR-0015 API Gateway: APISIX](../decisions/0015-api-gateway-apisix.md),
[ADR-0017 Tenant + Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md),
[ADR-0036 Tenant Resolution and Trusted Inputs](../decisions/0036-tenant-resolution-trusted-inputs.md)
(the resolution matrix and the authority ceiling),
[ADR-0040 The Ambient Unit of Work](../decisions/0040-ambient-unit-of-work.md)
(§ Tenant Context's closed setter set),
[ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md),
[ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md),
[ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md),
[ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md),
[ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md),
[ADR-0044 The Audit Write Path](../decisions/0044-audit-write-path.md)
(the audit row's tenant, capture, redaction, and the append-only layers).
Public-read additions derive from
[ADR-0052](../decisions/0052-anonymous-public-read-boundary.md); the private web
ingress/caller and anonymous visitor budgets derive from
[ADR-0053](../decisions/0053-trusted-public-server-rendering.md); coordinated accounting,
native admission and redirect/query rules derive from [ADR-0054](../decisions/0054-bounded-public-renderer-admission.md).
Delivery/recovery additions derive from
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md).
The exact authenticated read-mode extension derives from
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md). The composition
declarations
are frozen once in a singleton exact-type catalogue; request-scoped transaction
behaviors share that immutable snapshot. The retained-subject bound, retirement
ownership and closed refusal/fault signals derive from
[ADR-0066](../decisions/0066-bounded-tenant-subject-admission.md), Accepted —
2026-10-20. Implementation and local runtime/exported proofs are delivered; the
[implementation record](../roadmap/phase-02b-delivery-log.md#adr-0066-acceptance-and-implementation-2026-10-20)
owns independent reviews and final-source CI qualification.
The bound grants no throughput entitlement.
Identity admission/owner contracts additionally derive from Accepted
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md);
their P03-1 implementation is pending.
Bounded authenticated assertion recording derives from
[ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md); its
implementation and local proofs are delivered in the
[remediation record](../roadmap/phase-02b-delivery-log.md#p02b-5-external-review-remediation-2026-10-20). Its tenant-only audit budget is separate from the
subject request registry and adds no tenant-registry admission cap.

Producer announcement and common execution ownership derive from
[ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).

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
- [ ] No `IgnoreQueryFilters()` outside the audited `EnterPlatformAdminScope(reason)`
      call path.
- [ ] Background jobs use the typed scheduler; trusted submission stamps tenant
      and optional organization on the authoritative row, and `JobExecutor`
      restores them before business work. Vendor wakeups carry opaque references
      ([ADR-0047](../decisions/0047-transactional-background-job-runtime.md#typed-definitions-submission-and-scope)).
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
- Refresh/access/ID tokens stay in the native BFF session owner. Browser cookies
  contain only opaque handles/proofs under the tenant BFF rules below.
- Access tokens short-lived (≤ 1 hour) and refreshed silently by the BFF.
- MFA enrollment supported for tenant-admin roles; required for platform-admin **and**
  for every Hub operator account.
- Password policy delegated to Keycloak: minimum 12 chars, breach check via HIBP, no
  password reuse for the last 5.
- Account lockout after 5 failed attempts in 10 minutes; lifted after 15 minutes or
  admin unlock.

**Development warm-realm qualification — Accepted 2026-10-22.**
[ADR-0068](../decisions/0068-development-warm-realm-qualification.md) owns the
exception allowing qualified missing-definition preparation before a fresh full
plan and the three stock-console assignment detachments. All identity-independent
refusals precede preparation; all other writes require complete planning. Existing
readable definitions, credentials and unrelated state remain preserved. This is
account-preserving reconciliation with named assignment deletion, not general
delete authority. All three correction steps complete local implementation and
both independent review rounds. The
[qualification record](../roadmap/phase-02b-delivery-log.md#adr-0068-step-3--complete-historical-path-qualification-2026-10-22)
owns actual historical lifecycle, usable credential/MFA, signed API and stored
self-service proof, together with exact hosted assignments and evidence limits.
Acceptance or a partial experiment alone does not establish qualification.

**Development access-token issuance — Accepted 2026-10-21.** This standard owns
the issuance-lifetime rule decided by
[ADR-0067](../decisions/0067-development-tenant-access-token-issuance.md), retaining
the earlier approval in the
[P02b-6 package](../roadmap/phase-02b-delivery-log.md#p02b-6-decision-package-2026-10-20):
the tenant development realm uses a configured 900-second access-token lifespan.
[`learnstack.json`](../../infra/keycloak/realms/learnstack.json) owns the executable
value; the development reconciler enforces it with no conflicting override on
either named client. P02b-6 Steps 1–3 supply the fresh-import definition and
bounded reconciliation through both seed/demo entries; their delivery records
own actual local qualification. Step 4 delivers complete real-token/API proof
after both independent reviews; its
[record](../roadmap/phase-02b-delivery-log.md#p02b-6-step-4--real-identity-qualification-and-closeout-2026-10-21)
owns local proof/review closure and source-bound qualification receipts. The
required CI checks qualify their exact source and assignment. ADR-0064's one-hour
API acceptance ceiling and 30-second skew remain separate from issuance.
ADR-0070 owns BFF custody/lifetimes; P02b-7 delivers that Accepted contract.

The pinned provider also copies the access-token expiration into generated ID
tokens, including shorter session-clamped issuance. ID tokens remain identity
credentials rejected by the tenant API; this shared expiration does not select
a BFF session lifetime.

Keycloak 26.6.2 reads issuance and expiration clocks separately. The configured
lifespan is not an exact `exp - iat` guarantee or a fixed 901-second ceiling.
Qualify exact realm configuration and absence of client overrides independently;
fresh signed expiration must fit the observed UTC/monotonic code-exchange window
plus that lifespan. Any explicit fixture/provider clock tolerance is at most the
API's existing skew; an indeterminate clock/window fails qualification. Preserve
session-clamping coverage for shorter late-session issuance and independent
mutants with wrong expiration or configuration. Existing tokens retain their API
acceptance bound; a shorter issuance setting does not revoke them immediately.

### Tenant BFF sessions

[ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md), Accepted 2026-10-23,
selects openid-client and native single-process custody behind `BffSessionStore`.
P02b-7's five-step package owns implementation; acceptance alone enables no route.
Use 256-bit random opaque `__Host-` session cookies: HttpOnly, Secure,
SameSite=Lax, Path=/, no Domain. Credentials and raw profiles enter no browser
projection. Verified host, browser binding, epoch and server state are required.
The delivered opaque session/browser cookie format uses an eight-hour Max-Age;
the standalone form-CSRF and login/logout proof cookies use 300 seconds, with
Max-Age zero for an authorized deletion. These browser retention bounds never
extend server idle, provider or absolute authority and do not make restart
survivable. The real cookie controls compare emitted headers, not browser defaults.

- Idle is 30 minutes, absolute eight hours; effective expiry also respects the
  provider refresh deadline. Response owners refresh expired/near-expiry access,
  touch qualifying activity and publish one revision. `auth()` is read-only.
  Explicit extension refreshes the provider; a timestamp alone does not extend it.
  The safe projection includes effective expiry and whether the known absolute
  deadline limits it. UI never promises extension at that maximum or when the
  provider returns an unchanged deadline. Full document navigation and the
  protected-document history veil require fresh network admission on return.
- Retain 1,024 total owners, including unsettled work; 128 pending for five minutes,
  16 pending per verified visitor IP, eight session/logout-return owners per
  validated issuer/subject. Same-browser replacement settles old work first.
  No live other-device eviction or capacity bypass; source/subject refusal is
  429 and global capacity refusal is 503. Full expiry scans precede global refusal.
  AccountSite also remains subject to the API's shared peer/visitor limits under
  ADR-0064; neither session capacity nor a subject allowance grants independent
  end-to-end throughput to a host or browser.
- Revocation/epochs fence late callbacks and refresh. Unknown/mismatched state
  changes no cookies; verified failure clears only its own transaction cookie.
  Logout revokes locally first, discards tokens after URL construction and keeps
  only its bounded return proof/accounting. Late cookie delivery can force sign-in.
- [ADR-0071's status table](../decisions/0071-uncertain-account-site-refusals.md#decision)
  distinguishes ordinary checks from new revision publication. An uncertain
  ordinary 401 or other availability failure retains a live owner without idle
  touch and denies checked authenticated content. 403/404 and actor/scope
  conflicts revoke; a masked host/hop configuration refusal can therefore close
  a session too. Callback publication always requires positive API authority;
  every failed post-refresh check revokes. A valid API 401 produces BFF POST
  503/`provider_refused` for callback/extension, not a retryable-grant promise.
  Public pages remain anonymous; unavailable protected checked reads return 503.
  Passive projections describe local state only. Genuine 401 revocation can be
  delayed; retention does not extend the existing refresh threshold or deadline.
  The [route failure classification](../decisions/0070-owned-tenant-bff-sessions.md#amendment-3--implementation-controls-and-diagnostic-scope-2026-10-24)
  separates malformed input from unavailable upstream/server work; it does not
  change grant-publication or revocation rules.
- Return destinations are the closed local set `/`, `/studio` and `/portal`.
  No browser URL, unverified Host/forwarded header, provider error or callback
  query chooses the issuer, API origin, provider endpoint or post-logout authority.
  Callback/logout origins use only the independently verified native host/port.
  Fixed discovery/token/JWKS purposes use the private issuer, no redirects, one
  operation-wide deadline through body consumption and a decoded-byte bound.
- Only the native AccountSite caller attaches its owned bearer to the fixed
  authenticated endpoint. The public caller stays credential-free; neither
  forwards browser Authorization or tenant/organization selectors. Source
  admission uses the verified socket provenance, not a browser-supplied address.
  Proxy deployment requires Phase 11's provenance qualification first.
- BFF diagnostics emit only their closed lifecycle/failure categories. Do not
  expose raw provider exceptions, responses, URLs, subjects, handles or tokens.
  A category such as `provider_refused` is an observation, not a causal verdict,
  proof of settled cleanup or a durable application session-audit record.
- Explicit BFF opt-in validates issuer/secret together. Disabled mode keeps public
  requests independent of Keycloak; auth paths are 404 and protected shells 503.
  Restart signs sessions out. Multiple web processes require Phase 11's distributed
  adapter and actual refresh/revocation/interruption qualification first.
- Before importing Next, the native process closes its raw development URL trace
  sink; Next configuration also disables automatic request/fetch logging. This
  suppresses framework performance events for public development requests too,
  while preserving separate build traces and API/business telemetry. A real
  enabled-development callback/logout canary scans console and trace artifacts;
  removing the trace guard must fail even with console logging disabled.

Tenant **development** provider events use explicit LOGIN/LOGIN_ERROR,
LOGOUT/LOGOUT_ERROR, CODE_TO_TOKEN/CODE_TO_TOKEN_ERROR and
REFRESH_TOKEN/REFRESH_TOKEN_ERROR storage. Merge the existing effective selection;
preserve listeners/admin policy. Fresh/default-empty retention is seven days.
Warm adoption requires bounded empty-history proof; preserve finite retention
of at least seven days, refusing shorter/indefinite/ambiguous custom policy before
any write. Separate readback and zero-write rerun are mandatory. Provider records
contain personal data, stay out of raw artifacts and are not an atomic BFF ledger.
[Audit 18](18-audit-coverage.md#operation-types) owns that narrow audit boundary;
Phase 11 owns production retention/capacity/security qualification.

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

Every ordinary tenant write use case checks, in order:

1. **Authentication** (valid token).
2. **Tenant membership** (user has a Membership in the resolved tenant).
3. **Role / permission** (user's roles include the required permission).
4. **Resource scope** (e.g. instructor can only edit their own courses).

A failure at any step returns a Problem Details response with the right code (`unauthorized`, `tenant_mismatch`, `forbidden`, `resource_scope_violation`).
The exact internal global-identity creation class instead uses the
[adopted subject-confined owner](#identity-admission-contract), with no tenant
selection or membership authority. It is not a routed HTTP write in P03-1.

## Tenant + Organization Isolation

See [docs/architecture/09-tenant-isolation.md](../architecture/09-tenant-isolation.md)
for the full strategy. Standards-side:

- Every `[TenantOwned]` entity has a query filter and an RLS policy. Architecture tests
  enforce this.
- Every `[OrganizationScoped]` entity additionally carries an `OrganizationId` column
  and an org-aware EF filter + RLS policy
  ([ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)). Nullable
  `OrganizationId` means the row may be tenant-wide.
- `IgnoreQueryFilters()` is allowed only inside the audited
  `EnterPlatformAdminScope(reason)` call path, which is itself the record of the
  access: the scope writes its `security-event` row through
  `IAuditStore.WritePlatformScopeAsync` on its own platform-role connection **before**
  the operation runs, and commits it on its own transaction rather than the scope's —
  which an abandoned frame rolls back — so an operation that later fails is still
  recorded ([ADR-0044 § 10](../decisions/0044-audit-write-path.md),
  [05-database.md § How `EnterPlatformAdminScope(reason)` reaches `learnstack_platform`](05-database.md)).
  The architecture test `No_IgnoreQueryFilters_Outside_PlatformAdminScope` is a
  **path check**: no per-call-site attribute or comment marker exempts a call
  ([09-tenant-isolation.md § Platform admin access](../architecture/09-tenant-isolation.md),
  [21-architecture-tests-catalogue.md](21-architecture-tests-catalogue.md)).
- Background jobs use `IBackgroundJobScheduler` inside the enrolled writable
  transaction. Tenant/org authority comes from trusted submission and the
  authoritative job row; `JobExecutor` restores context before business work.
  Hangfire wakeups contain only opaque references. See the
  [submission rule](21-architecture-tests-catalogue.md#backgroundjob_submission_is_transactional_and_stamped)
  and [ADR-0047](../decisions/0047-transactional-background-job-runtime.md).
- The `app.tenant_id` and `app.organization_id` session variables are set with
  `SET LOCAL` inside the ambient transaction — see § Tenant Context immediately below,
  which is the single authority for that placement.

### Confined event dispatcher

ADR-0046 accepts this P02b-2 boundary. Step 1 implements source/role guards;
both review rounds are complete. Step 3 implements mandatory worker startup
resolution and connectivity checks; both independent review rounds are complete.
Step 4 credential/publication enforcement and local closeout are complete; all
four steps completed both independent review rounds. The [merge closeout](../roadmap/phase-02b-delivery-log.md#p02b-2-merge-and-closeout-2026-10-13)
owns CI/merge status. The named keyed, lazy
dispatcher source belongs only to the API host. Configuration validates the exact
username without connecting or
echoing parse input. Worker startup must verify connectivity. Every new physical
connection verifies exact session/current
`learnstack_outbox_admin`, BYPASSRLS, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
NOREPLICATION and no outgoing role membership or incoming membership by a
non-superuser, directly or transitively. Superusers inherently reach every role;
this check detects grant drift on a new physical connection, not continuous
revocation of an existing session. No Seeder/web forwarding or shared app
credential is allowed. [Database Standards](05-database.md#grant-matrix)
owns exact status grants and audit INSERT-only. ADR-0047 additionally permits the
[closed Tenancy lifecycle projection](05-database.md#job-lifecycle-projection)
for durable job fan-out. Other business columns and tenancy mutations remain
denied, as do audit read, update and delete.

Routine confined poll/claim/mark receives ADR-0046's bounded audit exception;
terminal state is MUST/SecurityEvent and atomic with its row-bound witness audit.
INSERT-only and GUCs cannot tenant-bind a bypass credential. Real mismatch controls
must roll back terminal state and audit together. Existing platform-admin entry
audit and gate stay unchanged. P02b-3 Steps 3–4 implement separate app-role
business scopes, restore validated tenant/organization and announce RLS context
before reading the envelope and constructing a handler. The subscriber scheduler
shares the confined source for enumerated routing/status columns; it receives no
payload, causal-envelope or inbox SELECT privilege. The ordinary executor refuses
privileged subscriptions until their Phase 03 execution contract exists.

## Tenant Context

**This section is the single authority for RLS session-variable placement.** Every other
document links here rather than restating the mechanism.

Row Level Security predicates read four PostgreSQL session variables — `app.tenant_id`,
`app.organization_id`, `app.scope`, and `app.resolving_host` — whose canonical spellings
and canonical policy templates live in
[05-database.md § Tenant-Owned and Organization-Scoped Tables](05-database.md) and
[05-database.md § Table classes](05-database.md). This section fixes **where** the first
three belong. `app.resolving_host` is set by `CachedHostToTenantResolver` alone, in its
own short read-only transaction before the host lookup, because the row that determines
the tenant must be readable before any tenant context exists; it is read by exactly one
policy, on `platform_host_to_tenant`. Its value is the **normalized effective host** as
`EffectiveHost.Normalize` produces it — the single producer of both this variable and
the lookup key, per
[ADR-0036 § Effective host and the trusted hop](../decisions/0036-tenant-resolution-trusted-inputs.md).
That ADR, not this section, is the authority for *which* request input the host comes
from; this section remains the authority for session-variable **placement** only.

### The rule

`app.tenant_id` and `app.organization_id` are announced inside the ambient
transaction by the step-6 `TransactionBehavior` adapter through
`TransactionExecutionCoordinator`, from the context admitted at step 4.
Default ReadWrite transactions announce them as their first SQL statement.

For a physical PublicSurface ReadOnly owner or the exact authenticated
`GetAccountSiteQuery` ReadOnly owner only,
[ADR-0052](../decisions/0052-anonymous-public-read-boundary.md) and
[ADR-0040 Amendment 8](../decisions/0040-ambient-unit-of-work.md#amendment-8--read-only-public-frames-2026-10-03)
with each ADR's 2026-10-19 dated scope-extension disclosure under
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md),
authorize one predecessor: begin READ COMMITTED, execute and await
`SET TRANSACTION READ ONLY`, then return the owner frame. The existing tenant/
organization setter follows before any data SQL or handler dispatch. Mode setup
alone never stamps the tenant-announced marker. If setup fails or is cancelled,
Begin cleans up the partial transaction and marks rollback-only before rethrowing.
Mode resets per transaction; poisoning remains sticky for the unit's lifetime.

The authenticated-query extension is implemented by
[P02b-5 Step 2](../roadmap/phase-02b-delivery-log.md#p02b-5-step-2--real-read-only-application-subject-2026-10-19);
Step 4 owns HTTP activation. Selection is by exact request type, never a
query-name suffix, public marker or general authenticated
read category. It reuses the existing physical owner and tenant announcement;
the closed setter set and transaction-ownership contracts do not change.

Every institution public read dispatches a marked request through ISender, audit
Off and GET/HEAD only. Factory HostScope is an independent host ceiling intersected
with normal reconciliation, query filters and RLS; it cannot widen through claims
or the tenant-scope read hatch. Publication, access and lifecycle remain explicit
application predicates. The four context writers and eight tenant setters are
unchanged. Host resolution, flag-loader connections and rejected-assertion audit
keep their separately sanctioned paths.

READ ONLY protects the enlisted ambient transaction. Structural guards detect
controller/helper/independent-connection bypasses; real app-role HTTP/database
proofs are required. No public-data endpoint may use a direct persistence path.

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

**`app.scope` has no carrier.** No application path sets it, and
[Packet 7](../roadmap/phase-02a-kernel-tenancy.md) ships nothing that does:
`ITenantContext` carries no `app.scope` authority member; ADR-0052's immutable
HostScope is an independent narrowing ceiling, never a scope-widening carrier
([ADR-0040 Amendment 1](../decisions/0040-ambient-unit-of-work.md)), and the flag
derives from the actor's role plus a declared tenant-wide operation, and **the role is
the part that does not exist yet**. [Phase 02b](../roadmap/phase-02b-events-auth.md)
delivers the authenticated principal the carrier needs and says in its own scope that it
delivers "only the authentication plumbing"; `Membership`, `Role` and `Permission` land in
[Phase 03](../roadmap/phase-03-identity-admin.md), which is therefore the earliest phase
that can own a working carrier. The deferral is forced, not chosen.
Until it lifts, the cross-organization read hatch in the policy template is unreachable at
runtime — the hatch term reads an unset variable and is never true, so reads stay inside
the caller's organization plus the tenant-wide rows — which is the correct default. The
placement rule above is unchanged and governs `app.scope` the moment a carrier exists.

P02b-1 Step 1 adds a read-only announcement snapshot through
`IUnitOfWork.GetTenantContextAnnouncement(DbTransaction?)`, under
[ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
Its generation/normalized values describe successful SQL on the current physical
transaction. It is cleared before attempts and terminal transitions. Foreign
transactions and failed/no-tenant announcements supply no usable snapshot;
unresolved ambient provisioning still uses its sanctioned announcement. Frames
compare the live snapshot with their enrolled generation. This adds no setter,
context writer, factory privilege or tenant retargeting authority.

### The out-of-band setters

`TransactionBehavior` delegates the general announcement path to
`TransactionExecutionCoordinator`. Eight logical setter paths currently exist.
The adopted [candidate-reader extension](#owner-and-setter-census) registers a
ninth for P03-2 implementation; no ninth setter is shipped. The current set is closed
([ADR-0040 § Who sets `app.tenant_id`, completely](../decisions/0040-ambient-unit-of-work.md),
with its Amendments 3 and 7, is the authority; it is reproduced here because this section
is the placement authority). Two set it on the **ambient** transaction; six own a **short
transaction of their own**, because they run where no ambient transaction exists
yet:

| Setter | Transaction | Why it is not `TransactionBehavior` |
|---|---|---|
| `TransactionBehavior` → `TransactionExecutionCoordinator` | ambient | — the existing general announcement path; common execution adds no authority |
| The integration-event transport, per delivery | ambient — it opens it | `DeliveryExecutor` uses the common transaction owner and validated retained envelope before restoring `EventTenantContext`, claiming inbox and constructing the handler. `InProcessEventBus` owns durable acceptance only; nested commands join the prepared business transaction |
| `IOrganizationScopeValidator` | its own short read-only one | The organization assertion is validated in the request edge, before the pipeline reaches step 6 ([ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md)) |
| `IIdempotencyStore` (durable) | its own short one | A claim is taken **before** the pipeline reaches step 6 ([ADR-0037](../decisions/0037-idempotency-key-contract.md)) |
| `IAuditStore.WriteStandaloneAsync` | its own short one | An audit row that must survive the rollback of the operation it describes cannot share that operation's transaction ([ADR-0033](../decisions/0033-audit-durability-model.md)) |
| `IAuditStore.WriteBestEffortAsync` | its own short one | Same shape, SHOULD/MAY class; failures are logged and dropped |
| The `AuditConfig` override loader | its own short read-only one | An out-of-band cached projection, never a request-path query |
| The tenant-flag loader (`FeatureFlags`) | its own short read-only one | A cached projection of `tenant_feature_flags`, read on a cache miss wherever `IFeatureFlags` is asked — inside a request's transaction or outside any ([ADR-0045 § 2](../decisions/0045-entitlement-and-feature-flag-socket.md), [ADR-0040 Amendment 7](../decisions/0040-ambient-unit-of-work.md)) |

> **`IOrganizationScopeValidator` is registered and has no reachable caller yet.** Its
> only non-vacuous caller is the reconciliation matrix's row 7. P02b-5 activates
> validated claims, but `DenyAllTenantMembershipReader` refuses row 7 before
> organization-scope validation. Phase 03 owns the live membership path that can
> reach it. The assertion path ADR-0036 § What the assertions do names as its caller is
> subsumed by `TenantAssertionMiddleware`, which refuses on any difference between the
> asserted and the resolved value before belonging can matter. This table lists it because
> the set of setters is closed and a closed set is worth stating whole; it is not evidence
> that a seventh short transaction runs on any Packet 7 request path.

> **P02d-2 closes G15 without a new setter.** Seed ownership verification now sends
> module-owned contextual `ISender` queries in fresh trusted scopes; its old private
> transaction and direct `SetTenantContextAsync` call are removed.
> [The caller fence](21-architecture-tests-catalogue.md#seeder_does_not_call_tenant_context_setters)
> and planted companion reject direct calls, method groups, private transactions,
> accessor assignment and announcing SQL. G22/G23's projection loader and settings
> reader decisions remain owned by P02d-3; this change admits no out-of-band reader.

**Both audit writers announce both variables.** `WriteStandaloneAsync` and
`WriteBestEffortAsync` issue `app.tenant_id` **and** `app.organization_id` from the
draft, as the first statements of their short transaction, because `audit_log` is
organization-scoped: a row whose `organization_id` is non-null while the organization
variable is unset fails `WITH CHECK`
([ADR-0033 Amendment 2 § 5](../decisions/0033-audit-durability-model.md),
[ADR-0044 § 9](../decisions/0044-audit-write-path.md)).

Every one of the eight setters connects as `learnstack_app`. A setter that reached
for `learnstack_platform` would be invisible to the isolation suite, which is the
failure mode [ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md)
names by hand.

Because every `current_setting` read is called with its missing-OK argument (`true`)
**and** wrapped in `NULLIF(…, '')`, both an unset and a reset variable yield `NULL` and
the policy predicate filters the row out. The failure mode is an empty result set, not a
leak — but an empty result set arriving from production is an outage, so
`TenantContextGuardInterceptor` asserts that **a sanctioned setter** has already issued
the `SET LOCAL` pair on this transaction before any command a module `DbContext` issues
runs on it.

**Keyed on the transaction, not on the table**, and marked through the ambient
unit's announcement seam. Matching `[TenantOwned]` table names would put a parser
between every query and the database to decide something the transaction already
answers.
Of the eight logical paths, the MediatR transaction owner and P02b-3's integration
transport mark the ambient transaction through sanctioned `IUnitOfWork`
announcement methods. Durable acceptance and prepared subscriber execution both
announce before their first app-role command; the production call-site census
closes these implementations without adding a ninth logical setter. The durable
idempotency store remains unimplemented. The five other paths,
`OrganizationScopeValidator`,
the two standalone audit writers and the two cached-projection loaders, issue raw
`NpgsqlCommand`s on connections of their own, which EF interception never sees. `CachedHostToTenantResolver` is not in this set at all: it sets
`app.resolving_host`, not `app.tenant_id`. That is also why the
exemption list is empty, and why `PlatformAdminScope`, whose `BYPASSRLS` connection
announces no tenant by design, is invisible to the guard by construction rather than by a
hand-written exception. The concern the earlier wording had — that naming
`TransactionBehavior` alone would reject legitimate transport and audit writes,
and the future idempotency store's writes on its own short transaction — is real,
and is answered by the guard reading a marker rather than a behavior's name:
a setter marks the transaction it
opens if and only if it reaches EF. The audit store's writers have landed and reach none;
the durable idempotency store will mark its own if it does. It throws
`TenantContextMissingException` when it has not, which
[`Tenant_Context_Guard_Fires_Only_On_An_Unmarked_Transaction`](21-architecture-tests-catalogue.md)
asserts in both directions. **Packet 7 owns it**: Packet 6 ships
the setter (`IUnitOfWork.SetTenantContextAsync`) and the policies it will back up, and
the first tenant-owned read on a request path is Packet 7's, which is where the guard
belongs. It cannot be a connection-checkout interceptor, for the same reason it cannot be
a `DbConnectionInterceptor` that *sets* the values: checkout precedes the transaction, so
the transaction-local value is not there to be observed
([05-database.md § Connection Management](05-database.md)).

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

- Backend secret reads go through **`ISecretProvider`**. The registered implementation
  is `ConfigurationSecretProvider` until Vault's trigger fires — a production secret
  must rotate without a redeploy, or more than one operator needs access to production
  secrets — at which point
  `DaprSecretProvider` → Vault takes over per
  [ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md) and
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md). Call sites are identical
  either way. `.env.example` is checked in; `.env` is gitignored.
- Secret namespace: `learnstack/{deployment}/{module}/{key}`. The deployment segment is
  `development | saas | dedicated | selfhosted`.
- Production secrets rotated at least every 90 days where rotation is feasible (DB
  passwords, provider API keys, Hub HMAC shared secret, mTLS client certs).
- Secret access via `ISecretProvider` is logged.

ADR-0053's local Node launcher is a bounded exception to the .NET provider interface:
the paired private local configuration supplies the ingress/API-hop secret without
shell evaluation. It is never a `NEXT_PUBLIC_*` value, browser input or SDK default.
This exception does not authorize a production secret-store adapter; Phase 11 owns
the production ingress/secret lifecycle. See
[Infrastructure Standards](12-infrastructure.md#local-infrastructure-docker-compose).

### Private public-rendering boundary

ADR-0053 owns the protocol; ADR-0054 replaces its accounting, native admission
and redirect/query rules. These are the ongoing security obligations:

- Only the native listener captures host/socket provenance. Strip client tenancy,
  forwarding, internal and framework-override carriers before Next; validate the
  signed envelope again before bootstrap and before the configured API caller.
- The envelope, hop secret and visitor address never enter responses, client assets,
  retained logs or audit data. Framework debug output and test diagnostics are part
  of that exclusion. A marker or imported server-only module grants no tenant scope.
- The configured caller uses its private loopback origin and builds exactly five
  application headers: `Accept`, `X-LearnStack-Host`, `X-LearnStack-Hop-Secret`,
  `X-LearnStack-Visitor-Address` and `traceparent`. The HTTP Host remains the API
  origin.
  Rebuild that set from verified context; never forward browser cookies,
  Authorization, tenant/organization assertions or proxy headers.
- The complete headers/body operation has one 10-second deadline and an 8 MiB
  decoded-body bound, with no retries or redirects. Rendering and responses remain
  dynamic/no-store. The injected SDK owns no host, secret or global fetch default.

P02d-5 delivers this boundary and ADR-0054's accounting, native admission and
URL controls. The [remediation record](../roadmap/phase-02d-delivery-log.md#p02d-5-external-review-remediation-2026-10-09)
owns execution, independent reviews and PR closeout evidence.
Native paths, including matcher exemptions, admit GET/HEAD; ADR-0070 adds only
the three exact proof-bearing POST actions named in Frontend Standards.
Other methods remain masked. Production upgrades close and development retains
only validated GET HMR. See
[Frontend Standards § Native Admission and URL Identity](07-frontend-architecture.md#native-admission-and-url-identity)
for the native controls and their delivery/review boundary.


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
- BFF sign-in POST requires exact Origin and a random host-only double-submit
  proof; authenticated sign-out/extension requires exact Origin and a random
  constant-time checked session-bound token. Apply admission before allocation.
- Sign-in HTML uses `same-origin` referrer policy so native forms retain that
  Origin proof. Provider redirects and the ID-token-hint logout exception retain
  `no-referrer`. Missing or `null` Origin remains a refusal.
- The cross-origin provider callback instead requires its single-use state,
  nonce, PKCE verifier and host/browser/epoch-bound transaction cookie.
- Auth cookie families and decoded forms each have a 16 KiB ceiling. Native
  auth POST admission requires a bounded Content-Length and no transfer encoding
  before Next can clone a body; the form parser independently bounds actual bytes,
  UTF-8, declared scalar fields and duplicates.
- Only the exact auth POST set is open. SameSite and implicit framework checks
  authorize neither sibling origins nor Server Actions.
- Webhook endpoints verify signatures, not CSRF tokens.

## CORS

- Default deny.
- Allowed origins explicit per environment.
- Studio and Portal apps share a single origin with the API (no cross-origin requests needed).
- Tenant custom domains use server-side rendering; client-side cross-origin to the API uses CORS preflight on a controlled allow-list.

## Rate Limiting

**Two layers, two jobs, two key spaces — and they are not the same numbers.**
The edge (APISIX) limits a noisy *client* to protect the platform, keyed on the
remote address; its policy is owned by
[30-api-gateway.md § 6](../architecture/30-api-gateway.md) and is deliberately
much looser per second. The table below is the **application** policy: fair use per *principal*, on
whatever names one — a token where the caller is authenticated, the peer where
it is not, the mTLS client certificate for the Hub surface, the provider account
for a webhook. Only a principal key can express "this tenant's plan allows N",
which is why the edge cannot own these rows. A request passes both layers or
neither.

Until the gateway fronts the app ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)
gates it to Phase 11), the application enforces anonymous limits in process.
**Accepted P02d-5 policy — 2026-10-08. Derives from:**
[ADR-0053](../decisions/0053-trusted-public-server-rendering.md). Use one canonical-IP
quota for direct socket peers and authenticated-hop visitor addresses, plus a
separate physical-peer ceiling. Neither forwarded headers nor a host/cookie creates
a visitor partition. Trusted malformed visitor metadata spends the fallback budget,
then receives masked `404` before lookup. P5 Step 2 implements both budgets and
pre-lookup admission, with real-socket controls. This is local limiting, not
distributed DDoS protection. NAT/local loopback callers share an IP quota; budgets
count API calls.

**ADR-0054 accounting implemented — 2026-10-09; both review rounds passed.**
Remediation Step 1 owns the actual visitor limiters, with one process-local owner
lock across all visitor keys and physical peers. The lock serializes positive
acquisition, creation and
retirement; no network/database work, `await` or disposal runs under it. One-minute
fixed windows, 60/min per canonical IP, 600/min per peer and no queue remain. No
request retains a limiter removed by retirement.

- Probe an existing visitor with zero permits. Its refusal returns unchanged
  Retry-After before any peer acquisition. Otherwise acquire the peer permit;
  peer refusal neither debits the visitor nor creates an unknown visitor limiter.
  Create an unknown visitor only after peer admission, then acquire one visitor
  permit. No competing debit/retirement separates the known-visitor probe and debit.
- First refusal wins, including the visitor when both budgets are exhausted. Do
  not combine metadata, debit to obtain failure metadata, or use a partition
  lookup that allocates merely to check membership. Reuse successful and refused
  outcomes on framework retry of the same HTTP request; returned leases have
  independent lifetimes. Cancellation, Retry-After and disposal need explicit proofs.
- Retire only after full-quota idle state lasts a complete window. One
  non-overlapping sweep visits the whole registry in bounded batches; removal
  shares the acquisition lock and disposal occurs outside it. Recreated state
  never restores unexpired quota. DI shutdown closes admission, joins the sweep
  and disposes every remaining limiter exactly once on both teardown paths.
- The 600/min peer budget bounds successful peer acquisitions and new visitor
  allocations. It is not a fixed global cardinality cap or a bound on all
  attempts/refusal work. Known exhausted visitors can generate arbitrarily many
  429 responses without spending peer permits; their path performs no registry
  sweep, new limiter allocation or host/database lookup. No unbounded parallel
  identity registry is permitted. Phase 11 owns upstream transport/edge protection
  and contention/load measurement; no measured throughput is claimed here.

| Surface | Limit |
|---------|-------|
| `/api/v1/auth/*` (login, password reset, register) | 5 req/min per IP |
| Early API admission (all credentials) | 60 req/min per canonical IP + 600 req/min per physical peer (P02d-5 Step 2) |
| Authenticated API | Additional 600 req/min per validated issuer + subject (ADR-0064) |
| Write endpoints (planned; Phase 03) | 60 req/min per token |
| Webhook endpoints | 1000 req/min per provider |
| Hub internal API (`/api/internal/*`) | 60 req/min per mTLS client cert |

P02b-5 Step 3 implements the subject component; Step 4 activates it in production.
The stable issuer/subject key survives token rotation and tenant selection; actor,
host and credential text do not select a partition. It uses a one-minute fixed
window with no queue. Accepted
[ADR-0066](../decisions/0066-bounded-tenant-subject-admission.md) adds the
process-wide 4096 retained-key bound; the internal smaller-bound constructor is
only a test seam. Retirement reclaims only full-quota partitions idle for a
complete window. Acquisition inspects at most 128 entries and disposes at most
one; the timer inspects/retires at most 128 every 30 seconds. One shared cleanup
owner prevents overlapping batches; at most 4224 limiters are owned, including
retirement. Exhausted live quotas cannot be evicted or reset by pressure.

The acquiring cleanup owner must complete disposal, then recheck shutdown,
fault, key existence and capacity before its own allocation or debit. Other
requests do not wait for cleanup and may take freed slots. A sticky cleanup
fault stops new allocations and batches; already retained keys keep their
original quota. Joined shutdown closes admission and observes timer/acquisition
cleanup and retained failures. One blocked disposal can indefinitely delay its
request and shutdown; a host deadline is not cleanup evidence and adds no fatal
process policy. Local ownership, HTTP and actual OTLP proofs exercise these
refinements; their review and final-source CI status live in the implementation
record.
The earlier visitor/peer limits apply to every credential, so this is an extra
ceiling, not a promise of independent 600/min end-to-end throughput. Whole-chain
reduced-budget proofs isolate the subject limit below those early floors; a
separate real-default proof exercises the 600 boundary. Subject refusals stop
before reconciliation and durable rejection audit. Quota/capacity use 429 with
stock-quota/60-second retry hints; new-key cleanup fault uses 503
`dependency_unavailable` and the common 30-second retry helper. Hints do not
promise capacity or recovery. See
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md#additional-subject-quota).

[ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md) separately
limits authenticated rejection audit attempts to 600 per independently defended
host tenant per API instance per first-request-anchored monotonic minute. The
singleton audit budget is shared by all scoped recorders and has no actor,
subject, source or dimension split. Failed writes spend permits; suppression
retains the refusal, count and exactly one attributed Warning without database
I/O or extending expiry. Admitted events remain MUST; tenant `AuditConfig` cannot
override the allowance or weaken general mutation durability. Ten admitted IPs
can exhaust the allowance through the physical-peer floor, so later actors may
have no durable evidence in that window; aggregate log volume remains unbounded.
Subject admission therefore supplies no independent actor-coverage guarantee.
Anonymous bursts and tenantless refusals retain their separate contracts.

429 responses include `Retry-After`. The edge half runs on **APISIX**
(`limit-req` / `limit-count` plugins); the application half runs in ASP.NET, where
the plan-level API-rate limit — `limits.api_rate_per_minute`, in the Hub's key
vocabulary, which LearnStack adopts
([ADR-0045 Amendment 1](../decisions/0045-entitlement-and-feature-flag-socket.md),
[ADR-0021](../decisions/0021-feature-based-entitlement.md)) — can differ per tenant
and the edge has no way to know it.

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

- Exception strings contain no credentials or sensitive personal values. Runtime property
  redaction does not scrub arbitrary exception messages; the bounded compile-time
  guard and its limits are in
  [Error Handling § Sensitive Exception Messages](09-error-handling.md#sensitive-exception-messages),
  under ADR-0032 Amendment 6. Review and closed diagnostic controls still apply.
- Server-side messages explain what happened; client-facing messages avoid disclosure.
- 404 used to hide cross-tenant existence.
- Stack traces never reach clients.
- Internal correlation id surfaced to clients to support back-channel debugging.

## Dependency Hygiene

- Renovate / Dependabot enabled. **Not yet:** no update bot is configured, and
  [Phase 11](../roadmap/phase-11-production-hardening.md) enables one.
- Critical / high vulnerabilities patched within 7 days. The backend's restore reports an
  advisory of **any** severity anywhere in the graph, transitive packages included, and CI
  fails on it (`NuGetAuditMode` `all`, `NuGetAuditLevel` `low`, warnings as errors under
  CI) — so the seven days are a deadline for a fix, never for noticing.
- Lockfiles committed — `frontend/pnpm-lock.yaml` today. The backend has no NuGet lock
  files yet; they arrive in [Phase 11](../roadmap/phase-11-production-hardening.md).
- No transitive dependency mismatch — `pnpm install --frozen-lockfile` in CI today, and
  `dotnet restore --locked-mode` once the backend has lock files to lock to (Phase 11).

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

Every privileged operation writes an audit log entry, subject to the confined
routine dispatcher exception above, per
[ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md)
(supersedes ADR-0016),
[ADR-0044 The Audit Write Path](../decisions/0044-audit-write-path.md) and the
[18 Audit Coverage Standard](18-audit-coverage.md). Coverage is
**MUST / SHOULD / MAY** per module-operation; rows are written only through
`IAuditStore` — modules never write `audit_log` directly. Entries are append-only and
queryable by tenant admins for their own tenant (org-admins for their org).

Security-relevant durability rules:

- **MUST-class audit fails closed.** Its rows — one per audited
  `(resource, operation)`, not one per request — are written on the same
  **transaction** as the business write: `AuditLogBehavior` classifies and parks the
  intents through `AuditExecutionCoordinator`, and
  `TransactionExecutionCoordinator` calls `IAuditStore.WritePendingAsync` on the
  **owning** unit-of-work frame, immediately before `COMMIT`, draining every intent in
  the scope ([ADR-0044 §§ 3–4](../decisions/0044-audit-write-path.md)). So a privileged
  operation cannot commit unaudited. It also means the insert runs while
  `app.tenant_id` is set, which is what lets Row Level Security accept it. "The same
  `DbContext.SaveChanges`" was the earlier formulation and
  [ADR-0033](../decisions/0033-audit-durability-model.md) **withdrew** it: the
  guarantee is the transaction, which is what a reader of `audit_log` observes and
  which needs no cross-context machinery. Exactly two failures reject the operation —
  an operation the catalogue does not classify (`audit_unclassified_operation`, 500)
  and a MUST-class row that cannot be written durably (`audit_unavailable`, 503).
- **A standalone MUST-class write failure follows the operation's own outcome.** A
  standalone row recording an access that is being **granted** still rejects with
  `503 audit_unavailable` — without it, data leaves the system unaudited. A standalone
  row recording an operation that is **already being refused** — a `denied`
  authorisation outcome, a rejected tenant assertion — keeps its own 403 / 404, because
  nothing proceeds unaudited and downgrading the refusal would tell an anonymous caller
  more, not less. Either way the failure logs at `Critical`, increments the
  standalone-write-failure counter and marks the audit health check unhealthy
  ([ADR-0033 Amendment 1](../decisions/0033-audit-durability-model.md)). The
  in-transaction class above is untouched by this.
- **A failure to read a tenant's `AuditConfig` overrides does not reject.**
  Classification falls back to the in-process catalogue, which carries the same MUST
  floor, so nothing proceeds unaudited — and denying every request platform-wide
  because one cached projection is unreachable is the worse compliance outcome. The
  failure logs at `Error`; the audit health check does not move, because it answers only
  whether a MUST-class row can be written
  ([ADR-0033 Amendment 6](../decisions/0033-audit-durability-model.md)). A tenant override
  may narrow SHOULD/MAY coverage; it may never remove baseline MUST coverage
  ([ADR-0033 § Fail-closed, stated precisely](../decisions/0033-audit-durability-model.md);
  asserted by `Audit_Classification_Does_Not_Read_The_Database_On_The_Request_Path` in
  [21-architecture-tests-catalogue.md](21-architecture-tests-catalogue.md), which observes
  that a MUST is answered without the loader ever being asked for a connection, alongside
  `AuditConfigServiceTests` and `AuditLogBehaviorTests`).
- **Snapshots are redacted at capture and bounded there.**
  `AuditChangeTrackerInterceptor` captures every `Added` / `Modified` / `Deleted`
  entry minus a named exclusion list, and two gates run before anything is parked: a
  property marked `[PiiSensitive]` or matched by the shipped `SensitiveTokenCatalog`'s
  name tokens has its value replaced with the redaction sentinel — the property is
  kept, so the diff still records *that* it changed — and each of `before_state`,
  `after_state` and `changes` is capped at `JsonValue.MaxRowBytes` (256 KiB), above
  which the value becomes an explicit elision record
  (`{"_elided": true, "bytes": …, "sha256": …}`) that preserves the column's JSON
  type. Never a silent truncation and never a dropped field
  ([ADR-0044 §§ 7–8](../decisions/0044-audit-write-path.md)).
- **`audit_log` is tenant-owned and organization-scoped; `audit_config` is
  tenant-owned and tenant-wide** — it has no `organization_id`, so it carries the tenant
  term only and no restrictive write guards
  ([ADR-0044 Amendment 1](../decisions/0044-audit-write-path.md)). Both take the
  canonical RLS template for their class in the current applied schema.
  [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) accepts the
  exact global-identity departure for `audit_log`, pending Step 3; `audit_config`
  remains unmodified. [Database Standards](05-database.md#global-central-audit-branch)
  owns that policy, without a second template here. A tenant admin's read is
  bounded by the policy, not by the
  query. `audit_log` carries no foreign key to `tenants`: the record of what happened
  to a tenant has to outlive the tenant.
- **Append-only holds in three layers, and each stops a different actor.**
  `learnstack_app` holds `SELECT, INSERT` only and is stopped by the absent privilege;
  `learnstack_platform` holds `UPDATE` on the redaction columns alone — any other
  column is refused by the column-level grant, before the trigger runs — plus the
  retention purge `DELETE`; and the table owner is stopped by neither, because it holds
  every privilege implicitly and, under `FORCE`, the policy constrains it by **tenant**
  rather than by immutability — measured, an owner `UPDATE` returns `UPDATE 0` with no
  tenant announced and `UPDATE 1` with one, so `audit_log_append_only_guard` is the one
  layer binding `learnstack_migration`. GDPR redaction and retention purge are the only
  sanctioned mutations, and `IAuditStore` has no update method
  ([ADR-0044 § 9](../decisions/0044-audit-write-path.md),
  [18-audit-coverage.md § Storage](18-audit-coverage.md); asserted by
  `AuditLog_Update_Is_Column_Restricted`).
- SHOULD/MAY-class audit stays best-effort, and its accepted loss is written down rather
  than assumed.
- Monthly partitioning and the retention job land in
  [Phase 11](../roadmap/phase-11-production-hardening.md) per
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) — audit correctness
  cannot be retrofitted, audit scale can. Phase 02a ships `audit_log` as a single plain
  table ([ADR-0028 Amendment, 2026-09-07](../decisions/0028-audit-log-partition-management.md)).

<a id="proposed-p03-1-identity-admission-annex"></a>

## Identity admission contract

**Status: Adopted — Accepted 2026-10-25; implementation pending.** Derived from
[ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md).
Acceptance admits the bounded contract below, not implemented code or grants. The current
authentication surface, eight tenant setters and deny-all membership reader above
remain the implemented baseline. The
[adopted database contract](05-database.md#identity-storage-contract)
owns the global class, authority anchor, subject-key encoding, exact policies,
owner functions and lock/snapshot rules; they are not duplicated here.

<a id="proposed-credential-quota-and-binding-order"></a>

### Credential, quota and binding order

Stock cryptographic/provider validation creates an immutable
`VerifiedProviderSubject` projection. It carries the exact validated subject and
issuer, matched to the installed stable authority anchor, plus validated optional
actor/scope evidence. It is distinct from the normalized application identity;
only a successful existing binding lookup creates that identity on P03-1's HTTP
path. Neither raw claims nor a supplied UserId substitutes for either proof.
Present malformed/reserved/duplicate actor or scope claims still refuse; absence
of the compatibility actor claim permits only resolution of an existing Active
binding. Ordinary HTTP never allocates or adopts a User.

Qualify the authority anchor and configuration before enabling identity admission;
requests use that immutable admitted pair, with no anchor lookup before the quota.
A mismatch refuses without deriving a new authority, repairing the anchor or
using an email. The classified HTTP pipeline remains route-independent:

| Stage | P03-1 handoff |
|---|---|
| Existing admission and host classification | Preserve visitor/peer/body bounds and precedence; unknown host remains masked 404 before token/provider work. |
| Provider validation and credential-result gate | Produce `VerifiedProviderSubject`; the gate recognizes that verified stage without requiring the later normalized actor. Present invalid/undecidable credentials remain fixed 401 with bare Bearer challenge. |
| Subject admission | Debit the existing validated issuer/sub quota before any binding SQL. Preserve its refusal/fault result and request-local ownership. |
| Existing-binding lookup | Use Standard 05's short physically READ ONLY self owner; resolve the stored actor only for an Active matching subject and absent/equal actor evidence, then close before tenant resolution. |
| Ordinary tenant pipeline | Feed normalized `ValidatedTenantIdentity` into the existing resolver, assertions, authorization and surface admission. No endpoint selects a different resolution outcome or bypasses those stages. |

The subject quota consumes `VerifiedProviderSubject` before any binding database
access, including ordinary authenticated requests. Its current consumer of an
actor-bearing `ValidatedTenantIdentity` must be replaced explicitly; merely
omitting `user_id` from a token cannot reach that consumer. Preserve the existing
validated issuer/subject key, 600-per-window quota, 4096-partition registry,
retirement/fault protocol, early quotas and refusal precedence. Re-entry consumes
no second permit. A changed actor, tenant, host or bearer never resets the quota;
no application actor is required to debit it.

<a id="proposed-ordinary-outcomes-and-source-handoffs"></a>

### Ordinary outcomes and source handoffs

| Observation | Adopted response / database work; implementation pending |
|---|---|
| Credential-free public GET/HEAD | Existing anonymous behavior; no binding lookup or allocation. |
| AccountSite, absent bearer | Existing masked 404; no binding lookup. This is not the rule for anonymous public reads. |
| AccountSite, admitted valid identity and host-backed authority, wrong method | Existing routing 405 with `Allow: GET`; absent credentials remain masked 404. |
| Subject admission refuses | Existing 429 or registry-fault 503; no binding access. |
| Unknown binding, conflicting actor, collision or Closing/Closed binding | Fixed 401; no allocation, adoption, alias, recreation or replacement actor. |
| Binding lookup unavailable | Fixed 401 under ADR-0064/0071; no anonymous fallback or automatic retry. |
| Active matching binding | Continue ordinary reconciliation with its stored actor; authentication supplies no new membership or scope authority. |

Error bodies and challenges disclose no claim values or internal refusal detail.
`binding_store_unavailable` is a closed internal diagnostic reason, not a new
public Problem Details code or a trusted identity-refusal witness. Translate
expected binding-store availability failures at the new boundary while preserving
uniform 401; arbitrary exceptions do not establish an identity outcome. ADR-0065's
host-tenant assertion allowance remains independent and supplies no global
creation-audit budget.

The ordinary API's uniform 401 and ADR-0071's uncertain AccountSite policy remain:
withhold authenticated content, retain the ordinary live owner without idle
renewal, and add no automatic retry/refresh. Initial and refreshed publication
still require the independently verified ID-token identity and matching positive
AccountSite actor/scope witness, with existing late-publication fences. A failed
post-refresh check revokes. The native provider identity still requires complete
actor/tenant claims, and `LoginCompletion` still represents a published session.
Align native opaque subject validation to the API's 255-character bound; no
pre-session optional-actor/tenant projection is introduced here.

P03-1 adds no POST, SDK operation or endpoint-census entry. Its source changes
must cover the actual handoffs rather than only relaxing a claim parser:

| Source seam | Required change / preserved boundary |
|---|---|
| `TenantTokenProfile`, `TenantCredentialFeature`, `TenantAuthenticationEvents` | Separate the verified provider subject from normalized application identity; only stock-validated facts enter either stage. |
| `TenantCredentialAdmissionMiddleware` | Recognize verified-subject success before binding normalization; keep invalid-present refusal and absent-credential behavior. |
| `TenantSubjectAdmissionMiddleware`, `TenantSubjectRateLimiter` | Consume the verified stage, retaining issuer/sub key, once-per-request debit and closed quota/capacity/fault outcomes. |
| New binding-admission stage and `TenantResolverMiddleware` | Resolve only existing bindings after quota and before the existing route-independent resolver; no allocation or endpoint-dependent bypass. |
| `TenantAuthenticationPipelineProof` and current HTTP/OpenAPI/SDK controls | Qualify the new stage's order and real owner; retain the exact existing public/AccountSite operation union and native publication contract. |

### P03-3 new-user HTTP and native activation gate

P03-3/G1/G6 owns the following decisions before adding a bootstrap HTTP caller.
They are prerequisites, not policies selected or activated by this annex:

- Provider signup/admission policy and the durable User/creation-audit budget.
  The retained subject registry and per-subject quota do not bound lifetime
  new-subject cardinality.
- Exact route/method, absent-token and masking behavior, and an explicit bounded
  supersession of ADR-0064's prohibition on endpoint-dependent resolution.
- A separate bootstrap caller and exact SDK/OpenAPI ownership, cache/input/body
  bounds, verified host/source/hop contract and complete operation census.
- An independently verified pre-session provider projection and exact bootstrap
  issuer/subject/actor association before ordinary session publication.
- Typed session/no-session completion with an explicit response, cookie, cleanup
  and expiry table, including tenantless success and preservation of an existing
  session during a failed replacement.
- Exact native handling of bootstrap 400/401/404/429/503, cancellation and unknown
  results, with no automatic mutation retry or inference of rollback from a
  missing response. Do not inherit AccountSite's read classification by accident.
- Real actorless and tenantless provider/native controls, wrong association,
  response loss, capacity, cancellation and late-publication qualification.

The necessary ADR-0053/0064/0070 changes and G1/G6 authority acceptance precede
that activation. Standard 05 remains the canonical prepared global owner/audit
contract; its existence admits no HTTP endpoint or native caller.

<a id="proposed-owner-and-setter-census"></a>

### Owner and setter census

Standard 05's global self lookup, prepared creation, trusted seed adoption and
central reconciliation owners announce verified subject authority only. Their
immutable connection/transaction marker is a separate mode of the common guard,
not a table-name exemption or a tenant context. Only their enumerated setters may
announce identity variables derived from verified/captured facts, never payload
or headers.
Missing markers, foreign subject reuse, nested tenant/global ownership and pooled
authority leakage refuse. Ordinary tenant setters, factories and audit writers
must hard-refuse `TenantId.PlatformSentinel` and global owner mode. Ordinary
pending gains its missing explicit refusal before admitting the global branch;
the source-bound requirements are in Standard 05.

The adopted global audit methods and confined definer functions are enumerated
in Standard 05. Their bounded target/operation/subject evidence is the only new
authority. Sentinel attribution conveys neither PlatformAdminScope nor operator
permission. Runtime never receives a migration credential; the new functions do
execute with migration-owner rights and need their own exact privilege/body/caller
controls before admission. No general SQL executor, global User directory or
direct self audit read follows from them.

`PreContextIdentityAuthorizationReader` is the planned ninth logical tenant
setter, owned by P03-2 execution under G2. It uses `learnstack_app`, NOBYPASSRLS,
and a short physically READ ONLY transaction. It binds the verified application
actor and exact candidate tenant/organization to a minimal coverage/grant read,
announces only that candidate scope, and closes before the request owner opens.
It installs no `ITenantContext` and returns no profile or member directory.
Candidate scope remains a question; validated identity and the confined reader
contract supply input authority. P03-1 specifies the port/storage contract only.
Accepted ADR-0040's disclosure and this census admit only that ninth logical path;
P03-2 must implement/register/qualify its actual adapter before activation. The
eight current tenant setters remain the implemented set. The global subject
owners do not add tenant setters or ITenantContext writers.

<a id="proposed-bounded-contention-and-proof"></a>

### Bounded contention and pending proof

Use Standard 05's canonical owner, lock, snapshot, deadline and cleanup contracts;
no second allocation/reconciliation algorithm is defined here. The canonical
[lock signal contract](10-observability.md#identity-lock-signals) owns names and
closed labels. Phase 11 owns production alert thresholds and capacity tuning;
bounded acquisition and cancellation are P03-1 prerequisites.

P03-1 qualification covers actorless API credentials for an existing adopted
binding, unknown/conflicting/closed bindings without allocation, quota before
every binding read, unchanged anonymous/AccountSite method behavior and uniform
401 during store failure. Existing seed provider/native sessions retain their
complete identity and independent AccountSite witness. Standard 05 owns the
creation/audit, RLS, race, reconciliation and owner-cleanup proof obligations;
P03-3 owns new-user HTTP/native qualification above.

Uncached binding admission adds a database dependency to every authenticated
request, including credential-bearing public reads. Qualify actual app-role lookup
plans, representative before/after local latency, bounded pool checkout and
cancellation/connection cleanup under contention and outage. A lock timeout alone
does not establish pool capacity or a complete request deadline. No measured
production p95 or extension of ADR-0047's qualified capacity follows; Phase 11
retains production sizing. These are pending controls, not evidence of provider,
database or application qualification or shipped owners/setters/functions.

## Incident Response

- A security incident playbook lives in `docs/runbooks/security-incident.md` (Phase 11 deliverable).
- The team rotates an on-call slot for production.
- Critical incidents trigger pager + Slack channel; post-mortem within 7 days.

## Forbidden

- Storing passwords or password derivatives in any LearnStack table.
- Storing third-party tokens in plain text — encrypt at rest if storage is unavoidable.
- Trusting `tenant_id` or `organization_id` from a request body or query param. Both
  come from the resolved tenant context only
  ([API Standards § Tenant Context](04-api-design.md#tenant-context)), which an
  anonymous request resolves from its effective host.
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
