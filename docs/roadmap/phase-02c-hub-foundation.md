# Phase 02c: LearnStack Hub Foundation (parallel track)

## Goal

Bootstrap the **separate `learnstack-hub` repository** and stand up the foundation
surface that gates SaaS / Dedicated deployments and feeds the entitlement projection
in LearnStack core. This phase runs **in parallel with Phase 02b**: both depend only
on the 02a sockets (`IEntitlementProvider`, `IHostToTenantResolver`,
`platform_entitlement_cache`, `platform_host_to_tenant`) and do not block each other.

Phase 02c does **not** complete the Hub. Billing, invoicing, marketplace, and richer
operator UI ship in Phase 09b (Hub Billing) and the optional Phase 12 (Hub
Marketplace). Phase 02c delivers the **minimum viable Hub** — enough to provision a
tenant, push an entitlement projection, register a custom domain, and verify a
license — so that the SaaS deployment mode is exercisable end-to-end before MVP exit.

Decisions made / referenced in this phase:

- [ADR-0004 Authentication Strategy](../decisions/0004-authentication-strategy.md)
  Amendment 1 — `learnstack-hub` Keycloak realm.
- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md) — separate
  repository, mTLS internal API, operator portal.
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
  — `Plan`, `HubSubscription`, license-key issuance.
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md)
  — entitlement projection contract.
- [ADR-0022 Custom Domain + TLS](../decisions/0022-custom-domain-tls.md) — DNS-01 /
  HTTP-01 issuance, APISIX hot-reload contract.

## Scope

### Repository Bootstrap (`learnstack-hub`)

- New repo, modular monolith layout mirroring LearnStack core:
  `backend/`, `frontend/`, `docs/`, `infra/`.
- .NET 10 + ASP.NET Core API + EF Core + PostgreSQL + Hangfire stack.
- Next.js 16 App Router for the operator portal (`learnstack-hub-web`).
- Independent CI/CD pipeline; independent versioning. Build-time-only dependency on a
  later-extracted `packages/ui` design system (not required Day 1 — duplicate
  primitives if needed).

### Hub-Side Aggregates

Per [ADR-0019](../decisions/0019-learnstack-hub.md) and
[24-learnstack-hub.md](../architecture/24-learnstack-hub.md):

- `Plan` (catalog of plans + their feature / limit / compliance defaults).
- `HubSubscription` (per-tenant plan binding + lifecycle state).
- `Entitlement` (snapshot of the tenant's effective feature + limit + compliance set;
  the source of truth for the projection LearnStack core mirrors into
  `platform_entitlement_cache`).
- `LicenseKey` (RSA-2048 signed `.lic` file metadata for Self-Hosted).
- `CustomDomain` (per-tenant host + DNS / TLS validation state).
- `CompliancePolicy` (per-plan cap set: regions, retention, audit retention floor).
- `Tenant` mirror (a thin Hub-side view of the LearnStack-side `Tenant` aggregate;
  Hub is the authoritative source for *plan-related* fields, LearnStack is the
  authoritative source for *operational* fields).

### Internal API Surface (mTLS + signed JWT + HMAC)

The four-endpoint surface per
[ADR-0019](../decisions/0019-learnstack-hub.md) and
[20-infrastructure-stack.md § Hub HTTPS Contract Surface](../standards/20-infrastructure-stack.md):

- `POST /api/internal/tenants` — Hub → LearnStack: create tenant + default organization.
- `PUT /api/internal/tenants/{id}/entitlements` — Hub → LearnStack: push entitlement
  projection (replaces `platform_entitlement_cache` row + invalidates cache via
  `learnstack.hub.entitlement` Dapr event).
- `POST /api/v1/internal/license/verify` — LearnStack → Hub: phone-home verification
  for SaaS / Dedicated.
- `POST /api/v1/usage/report` — LearnStack → Hub: usage telemetry (concurrent
  classroom sessions, monthly minutes, storage GB, etc.).

The endpoint set is **closed at four**; adding a fifth requires a new ADR.

All four:
- mTLS with LearnStack-internal CA-signed client certs.
- Signed JWT (RS256), `aud=learnstack-internal`, `exp ≤ 5min`, replay-protected via
  short-TTL inbox on `jti`.
- HMAC-SHA256 body signature in `X-Signature` with per-deployment shared secret.

### Keycloak Realm: `learnstack-hub`

- Separate realm from `learnstack` (different user pool, different domain).
- Operator role hierarchy (`hub-platform-admin`, `hub-operator`, `hub-billing-viewer`).
- MFA required for every operator account.
- The realm is **never** trusted by the LearnStack tenant-facing API; the tenant-facing
  gateway rejects `learnstack-hub` realm tokens.

### Operator Portal (`learnstack-hub-web`)

- Tenant list with filters (plan, status, region).
- Tenant detail: plan, entitlement projection, custom-domain status, license-key
  state, usage chart.
- Plan editor (CRUD on `Plan` and its feature / limit / compliance defaults).
- Per-tenant entitlement override editor (operators can grant a feature outside the
  plan default for support cases; every override is audited).
- Custom-domain admin (register, trigger DNS / HTTP-01 challenge, view validation
  state).
- License-key issuance for Self-Hosted tenants (signed `.lic` file download).
- Operator audit log (every operator action against a tenant resource is audited
  with `actor.hubOperator = true`).

### Custom-Domain Lifecycle

Per [ADR-0022](../decisions/0022-custom-domain-tls.md):

- DNS-01 challenge for wildcard subdomain certs; HTTP-01 for single-host customs.
- Let's Encrypt provider adapter (`ITlsCertificateProvider`); pluggable to ZeroSSL /
  enterprise CA.
- Successful issuance pushes the host → `(tenant_id, organization_id?)` row to
  LearnStack core via the internal API, which mirrors into `platform_host_to_tenant`
  and hot-reloads APISIX's route table.

### License-Key Issuance (Self-Hosted)

Per [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md):

- RSA-2048 key pair (Hub holds the private key; Self-Hosted instances ship with the
  public key).
- Operator issues a `.lic` file with claims: `tenant_id`, `plan_code`, `features`,
  `limits`, `compliance`, `valid_from`, `valid_until`, `issued_at`, `signature`.
- Phone-home contract: Self-Hosted instance attempts a verify call every 24h;
  cached projection survives **30 days** of failed verifications before refusing
  to operate (the "grace period").
- Fully air-gapped operation supported: an instance configured with
  `phone_home_enabled = false` runs purely on the `.lic` file; the operator manually
  re-issues yearly.

### Hub-Side Observability

- OpenTelemetry stack mirrored from LearnStack core (Tempo / Prometheus / Loki /
  Grafana).
- A grafana dashboard for operator portal: tenant count by plan, custom-domain
  pipeline state, license-verify success rate, entitlement projection push lag.

## Deliverables

- Independent `learnstack-hub` repo with CI / CD pipeline.
- Hub-side aggregates + EF + Postgres schema + Hangfire jobs (DNS validation, license
  verify, periodic re-push).
- `learnstack-hub` Keycloak realm with operator role hierarchy.
- Operator portal (Next.js) deployed at `hub.learnstack.dev` (or equivalent).
- Internal API on a dedicated APISIX route guarded by mTLS + signed JWT + HMAC.
- Custom-domain lifecycle: DNS / HTTP-01 challenge runner, Let's Encrypt adapter,
  push to LearnStack core.
- License-key issuance UI + signed `.lic` download.
- LearnStack core's `HubEntitlementProvider` + `SignedLicenseKeyEntitlementProvider`
  implementations land here (in the LearnStack repo, not the Hub repo — Phase 02c
  PRs into the LearnStack core).
- End-to-end SaaS scenario rehearsable: operator creates a tenant in Hub → tenant
  appears in LearnStack core with default organization + projection mirrored → tenant
  admin can log in and see their plan's feature set in Studio.

## Completion Criteria

- An operator can create a tenant on Hub; within seconds, the tenant exists in
  LearnStack core with its default organization and the entitlement projection
  populated.
- Flipping a feature on the tenant's plan on Hub propagates to the tenant's
  `IFeatureFlags.IsEnabledAsync` reads within seconds (eager invalidation via Dapr
  pub/sub).
- A Self-Hosted instance bootstrapped with a signed `.lic` file runs without phone
  home; flipping `phone_home_enabled = true` produces successful verify calls.
- A custom-domain registration on Hub completes the DNS challenge, issues the cert,
  and the host resolves to the right tenant on LearnStack core through APISIX —
  end-to-end.
- The internal API rejects requests missing any of the three security layers
  (mTLS / JWT / HMAC).
- The operator portal is unreachable via the `learnstack` realm token; the tenant
  surface is unreachable via the `learnstack-hub` realm token.
- The Hub-side architecture test suite is green; the LearnStack-side architecture
  test `LearnStack_Modules_DoNotReference_Hub` continues to hold.

## Risks

- **Two-repo coordination drift.** Mitigated by the closed four-endpoint contract;
  changes to the contract are ADR-gated.
- **Hub becomes a single point of failure for SaaS tenants.** Mitigated by the
  15-minute TTL cache on `platform_entitlement_cache` + the cached-projection grace
  period that lets LearnStack core continue operating during a Hub outage.
- **mTLS cert / HMAC secret rotation outages.** Mitigated by yearly rotation cadence
  + dual-key support window during rotation.
- **Operator portal abuse.** Mitigated by mandatory MFA + every action audited with
  `actor.hubOperator = true` + rate limit on the internal API.

## Phase Exit Decision

Phase 02c is complete when SaaS deployment mode is exercisable end-to-end (operator
creates tenant → LearnStack core picks up the projection → tenant admin logs in)
and Self-Hosted deployment mode is exercisable end-to-end (signed `.lic` issued →
Self-Hosted instance boots with the projection). The completion does not block
Phase 03 (Identity domain in LearnStack core) — they can finish in either order.
