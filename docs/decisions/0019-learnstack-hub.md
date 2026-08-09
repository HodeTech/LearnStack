# ADR 0019: LearnStack Hub — Separate Control Plane Application

## Status

Accepted — **amended by [ADR-0034](0034-hub-contract-surface-invariant.md) (2026-08-08)**

> **What ADR-0034 changed.** The separate-repository decision, the Hub → LearnStack
> mTLS + RS256 JWT + HMAC auth chain, and the "Hub holds tenant metadata, never tenant
> content" rule all stand unchanged. What ADR-0034 replaces is the **"closed at four
> endpoints"** framing that a later amendment to this ADR introduced, and the
> **LearnStack → Hub API key** below (§ Inter-system contracts, and the Decision
> section's `learnstack/hub/api-key` + 100 req/min rows): that direction now carries the
> same three-layer chain as the other one. See
> [ADR-0034 § One auth chain, both directions](0034-hub-contract-surface-invariant.md).
> A bearer key on a path returning a tenant's whole entitlement set has no replay
> protection and no per-request integrity, and the two directions holding different
> postures produced a live self-contradiction in
> [24-learnstack-hub.md](../architecture/24-learnstack-hub.md) — both spellings, twenty
> lines apart.
>
> That framing was never accurate: the Decision section below enumerates **six** paths
> and does not use the word "four". Protecting the number then damaged the design —
> [ADR-0022](0022-custom-domain-tls.md) Amendment 1 routes host mappings *and TLS
> private keys* through the entitlement-push payload specifically to avoid declaring a
> fifth endpoint.
>
> The Decision section below names the operator portal's app `learnstack-hub-web`. That
> is the name it had when this ADR was accepted; the app is now `operator-portal`
> (`frontend/apps/operator-portal`, asserted by the Hub's
> `Frontend_Has_Only_The_OperatorPortal_App` test). The Decision text is left as written —
> an Accepted ADR is a record of what was decided, and a rename is not a decision this ADR
> made. `learnstack-hub-web` survives as the Keycloak OIDC **client id**, which is a
> different identifier and does not change.
>
> ADR-0034 replaces the count with two enforceable invariants: **the Hub stores no
> tenant content**, and **every LearnStack↔Hub crossing goes through a named adapter**
> (`IEntitlementProvider` / `IUsageReporter` / `IHubTenantSync`). Adding an endpoint
> still requires an ADR — the surface is a cross-repository contract. Read ADR-0034 for
> the enumerated endpoint set.

## Date

2026-05-18

## Decision

LearnStack ships **LearnStack Hub** — a separate operator-facing application that owns
the platform control plane: tenant lifecycle, subscription/plan/billing, license
issuance, entitlement projection, custom-domain administration, compliance caps,
operator portal.

- **Codebase**: `learnstack-hub` — a separate git repository, separate CI/CD, separate
  release cadence.
- **Solution**: `LearnStack.Hub.Api`, `LearnStack.Hub.Domain`, `LearnStack.Hub.Infrastructure`,
  `LearnStack.Hub.Frontend` (Next.js operator portal).
- **Identity**: separate Keycloak realm (`learnstack-hub`) for LearnStack operators
  ("staff"). Tenant users are in their tenant's realm; never see the Hub.
- **Domain**: `Plan`, `HubSubscription`, `Entitlement`, `HubInvoice`, `HubInvoiceLine`,
  `WebhookLedger`, `LicenseKey`, `CustomDomain`, `CompliancePolicy`. Hub stores **tenant
  metadata only** — never tenant content / users / enrollments / classroom data.
- **Inter-system communication**:
  - **Hub → LearnStack**: mTLS + short-lived JWT (signed by `learnstack-hub` realm) + HMAC
    request signing. Calls hit `/api/internal/*` on the LearnStack API — never
    internet-exposed.
  - **LearnStack → Hub**: API key per tenant (Vault `learnstack/hub/api-key`), 100 req/min
    rate limit, scope = license verification + usage reporting only.
- **Phase placement**: Phase 02c — parallel track. Phase 02a (platform kernel) opens
  the `IEntitlementProvider` socket; Phase 02c (running in parallel with Phase 02b's
  events/auth wiring) implements `HubEntitlementProvider`. See Amendment 1 below.

## Context

LearnStack is a multi-tenant PaaS for education (ADR-0018). The product positioning
implies a sharp separation:

- **Tenants build their own education platforms** on LearnStack and run their own
  admin/portal experience for their learners and instructors. Tenants are LearnStack's
  customers.
- **LearnStack operators** (the staff who run LearnStack itself) need to do the work
  *no tenant should be able to do* — provision new tenants, set their plans, issue
  licenses, approve custom domains, set compliance caps that override tenant defaults,
  see aggregated usage and revenue. Operators sit *above* tenants.

The earlier design (pre-2026-05-18) implicitly had tenant CRUD inside the LearnStack admin
panel. This was wrong on three grounds:

1. **Authorization scope confusion** — tenant admins (LearnStack's customers) and platform
   operators (LearnStack's staff) are different user populations with different threat
   models, different audit obligations, and different SSO needs. Mixing them in one
   realm + one app violates defense in depth.
2. **Release cadence mismatch** — the LearnStack core ships on the product roadmap
   (Phase 02 → 11); the Hub ships on a business / operations cadence (Stripe rules change,
   new plans land, compliance frameworks come and go) decoupled from the product.
3. **Codebase coupling** — Stripe / Iyzico SDKs, billing webhook handlers, license-key
   RSA signing, and operator portal UI have no place inside the LearnStack tenant-facing
   process.

Nexora's **NMP** (Nexora Management Portal) — see
`Nexora/docs/architecture/MANAGEMENT_PORTAL.md`,
`Nexora/docs/decisions/0023-nmp-billing-model.md`,
`Nexora/docs/roadmap/phases/phase-NMP-track.md` — is the proven pattern. Two and a
half years of NMP operation have validated:

- The data-isolation guarantee ("NMP never holds tenant content data") is auditable and
  defensible.
- The mTLS + HMAC internal channel is the only acceptable cross-boundary contract for
  pushing entitlements into the runtime.
- The hybrid license model (phone-home + signed key) makes air-gapped on-prem viable.

LearnStack's PaaS positioning amplifies the value of a Hub: tenants will run on every
imaginable domain (yoga, coding, language, music, …); the Hub doesn't care — it only
manages plans, billing, licenses, domains, caps. The product → Hub interface is narrow
and stable precisely because LearnStack core is domain-agnostic (ADR-0018).

## Decision drivers

1. **Separation of concerns.** Operator workflows must not run in the tenant-facing
   process. Different SSO, different audit stream, different release cadence.
2. **Data isolation.** Hub holds tenant *metadata* (plan, subscription, license, domain);
   never tenant *data* (content, users, enrollments). This is a hard architectural
   invariant.
3. **Three deployment models** (ADR-0020): SaaS, Dedicated, Self-Hosted. Hub must work in
   all three:
   - **SaaS**: One Hub instance, many tenants.
   - **Dedicated**: One Hub, one tenant (managed for customer).
   - **Self-Hosted**: Customer runs both LearnStack and (optionally) Hub locally, OR
     LearnStack runs with a signed license key from the LearnStack-hosted Hub.
4. **License verification on every Tier-2+ feature gate.** The runtime queries
   `IEntitlementProvider.GetAsync(tenantId)` to determine if a feature is allowed. This
   port must exist from Phase 02 onward (with a `NullEntitlementProvider` default) so
   modules program against it from day one, just as Nexora's `ILicenseVerifier` did.
5. **Stripe + Iyzico support.** Global customers via Stripe; Turkish customers via Iyzico.
   Each side has its own provider port — `IPaymentProvider` (LearnStack core,
   tenant→learner storefront) and `IHubPaymentProvider` (Hub, LearnStack→tenant
   subscription billing). The interfaces share shape but live in different
   codebases and never run in the same process. See
   [Phase 09b § Payment Provider Adapters](../roadmap/phase-09b-hub-billing.md).
6. **Operator portal UX.** Tenant list, status, plan, MRR, license, audit, support;
   compliance caps editor; custom domain approval queue; usage metrics dashboard.
7. **Nexora pattern is transferable.** The team has just analysed NMP at length; reusing
   the proven shape (mTLS, signed JWT, HMAC, API key, license-cache table, entitlement
   projection) avoids reinvention.

## Considered options

### Option A — Hub as separate codebase + separate Keycloak realm (chosen)

`learnstack-hub` ships as its own repository. Communicates with LearnStack via mTLS internal
API. Operators authenticate against `learnstack-hub` Keycloak realm; tenants never see
Hub auth.

**Pros:**
- Clean separation, defensible audit boundary.
- Independent release cadence.
- Nexora-proven.

**Cons:**
- Two codebases to maintain.
- Cross-codebase contracts (license verify, entitlement push) must be versioned carefully.

### Option B — Hub as a module inside the LearnStack monorepo (rejected)

`LearnStack.Modules.PlatformAdmin` sits alongside other modules; operator UI lives in a
new route group `(operator)` in the main Next.js app; operators are users with
`Permission.Scope == Platform`.

**Pros:**
- Single codebase, single Keycloak realm.
- Simpler dev environment.

**Cons:**
- Mixes tenant-facing and operator-facing concerns in one process — bad defense-in-depth.
- Operator audit stream tangled with tenant audit; harder to satisfy regulator inquiries
  scoped to operator actions only.
- Stripe / billing webhook handlers in the LearnStack process — a billing exploit affects
  tenant runtime.
- Release cadence forced to align with product roadmap.
- Self-Hosted on-prem customers would either ship the operator portal too (security
  surface they don't need) or strip it (release-engineering complication).

### Option C — Outsource billing entirely to Stripe Customer Portal + Stripe-only product
(rejected)

Skip building Hub UI; use Stripe Customer Portal for tenant billing self-service. No
LearnStack-built operator console.

**Pros:**
- Less LearnStack code.
- Stripe handles all billing UX.

**Cons:**
- Stripe Customer Portal is tenant-facing, not operator-facing. Operators still need a
  dashboard.
- No Iyzico (Turkish customers) — Stripe Customer Portal is Stripe-only.
- License-key issuance, custom-domain approval, compliance-cap management, tenant
  lifecycle, usage metering — none of these belong in Stripe.

## Decision outcome

Adopt **Option A**: Hub as separate codebase + separate Keycloak realm.

### Hub responsibilities

| Domain | Description |
|--------|-------------|
| Tenant lifecycle | Create / suspend / archive / terminate. Provisioning: Keycloak realm setup, LearnStack tenant row insertion, default org creation, default plan assignment. |
| Plan catalog | Plan = `{ name, tier, features{}, limits{}, base_price, billing_cycle, currency }`. Operators author plans in Hub UI. |
| Subscription | Per-tenant subscription tracking. Trial → Active → PastDue → Canceled → Expired lifecycle. |
| Entitlement projection | Flattened, cached projection of plan + subscription per tenant. Returned by `/api/internal/license/verify`. Re-computed on every state change. |
| Invoicing | Stripe + Iyzico webhook ingestion → `HubInvoice` + `HubInvoiceLine` records. Append-only `WebhookLedger`. |
| License keys (on-prem) | RSA-signed JWT-style license with embedded entitlement projection. Phone-home refresh; manual entry fallback. |
| Custom domain admin | Tenant submits domain → DNS CNAME verification → Let's Encrypt cert request → tenant resolver mapping. ADR-0022. |
| Compliance caps | Per-tenant policy ceilings (`gdpr.hard_delete.enabled.forced`, `audit.retention.days.value`, `data.residency.region`). Pushed via `/api/internal/tenants/{id}/entitlements` under `compliance.caps`. |
| Operator portal | Next.js app at `hub.learnstack.dev`. Tenant list, search, detail; plan editor; invoice viewer; compliance caps editor; usage dashboard. |

### Hub data model (high level)

```
LearnStackTenant (mirrored read-only metadata)
├── HubSubscription
│   ├── Plan (FK)
│   └── HubInvoice
│       └── HubInvoiceLine
│
├── Entitlement (1:1 with tenant, re-computed on change)
├── LicenseKey (0..N for on-prem; revocable)
├── CustomDomain (0..N; one is primary)
├── CompliancePolicy (0..N entries; per cap key)
└── WebhookLedger (append-only)
```

Hub never references `Course`, `User`, `Enrollment`, `LiveSession`, etc. — those are
tenant-side. Hub's `LearnStackTenant` is a mirror of `{ id, slug, status, created_at }`
only.

### Inter-system contracts

#### Hub → LearnStack (push)

```
POST   /api/internal/tenants                     (create new tenant)
PUT    /api/internal/tenants/{id}/status         (suspend/activate)
PUT    /api/internal/tenants/{id}/entitlements   (push updated entitlement payload)
GET    /api/internal/tenants/{id}/usage          (pull aggregated usage metrics)
```

Authentication:
- **mTLS** between Hub and LearnStack (cert pinning).
- **Short-lived JWT** (5 min TTL) signed by `learnstack-hub` realm.
- **HMAC-SHA256 request signing** with shared secret from Vault
  (`learnstack/hub/internal-api-hmac-key`).

Endpoint visibility:
- `/api/internal/*` is bound to an internal-only listener (Kubernetes ClusterIP / Docker
  network internal), never proxied through APISIX, never internet-exposed.

#### LearnStack → Hub (pull / verify)

```
POST   /api/v1/internal/license/verify   { tenantId, featureKey } → entitlement projection
POST   /api/v1/usage/report              { tenantId, metric, value } (idempotent)
```

Authentication:
- **API key per tenant** stored in Vault (`learnstack/hub/api-key`).
- **Rate limit** 100 req/min per API key.
- Scope strictly limited to license verification + usage reporting; Hub does **not**
  trust tenant requests for any other purpose.

### Entitlement projection shape

```json
{
  "tenantId": "tenant-uuid",
  "tier": "growth",
  "features": {
    "classroom.recording.enabled": true,
    "custom_domain.enabled": true,
    "white_label_branding.enabled": true,
    "sso_saml.enabled": false,
    "advanced_analytics.enabled": false,
    "api_access.enabled": true
  },
  "limits": {
    "max_users": 500,
    "max_organizations": 10,
    "classroom_minutes_per_month": 50000,
    "recording_storage_gb": 500,
    "media_bandwidth_gb_per_month": 1000,
    "api_rate_per_minute": 6000
  },
  "compliance": {
    "caps": {
      "gdpr.hard_delete.enabled": { "allowed": true,  "forced": false },
      "audit.retention.days":     { "allowed": true,  "forced": true, "value": 365 },
      "data.residency.region":    { "allowed": false, "forced": true, "value": "eu-west" }
    }
  },
  "expires_at": "2027-05-18T00:00:00Z",
  "grace_until": null,
  "generation": 42
}
```

### Frontend topology

```
learnstack-web         → app.learnstack.dev          (tenant SaaS)
                       → {tenant-custom-domain}      (tenant production)
                       (one Next.js app, route groups (public)/(studio)/(portal))

learnstack-hub-web     → hub.learnstack.dev          (operator portal)
                       (separate Next.js app, in learnstack-hub repo)
```

Operators authenticate via `learnstack-hub` realm. Tenant users authenticate via their
tenant's realm (separate). No cross-app session sharing.

## Architecture tests

Four blocker-level architecture tests added in Phase 02c (the LearnStack-side test
`LearnStack_Modules_DoNotReference_Hub` lands as part of Phase 02a's day-one rules):

1. `LearnStack_Modules_DoNotReference_Hub` — No `LearnStack.Hub.*` namespaces appear in
   `LearnStack.Modules.*` or `LearnStack.Host`.
2. `Hub_Modules_DoNotReference_LearnStack_Internals` — Hub references only LearnStack
   `Application.Contracts` (DTOs); not Domain or Infrastructure.
3. `Internal_API_Endpoints_AreNot_Public` — endpoints under `/api/internal/*` are
   registered on the internal listener only; integration test asserts external request
   returns 404.
4. `Hub_NeverStores_TenantData` — Hub DB schema does not contain `course`, `lesson`, `user`,
   `enrollment`, `live_session`, etc. tables. Migration scan asserts.

## Consequences

### Positive

- Clean separation; Hub failures don't affect tenant-facing operation (Hub can be
  offline; runtime falls back to cached entitlement).
- Independent release cadence; billing/compliance changes deploy without touching tenant
  runtime.
- Operator audit stream is its own thing; regulator scope is unambiguous.
- Self-Hosted on-prem doesn't ship Hub UI (smaller attack surface, smaller image).
- Cross-codebase contracts are narrow and auditable.

### Negative

- Two codebases, two CI pipelines, two release processes.
- Cross-codebase contracts (license verify, entitlement push) need versioning discipline.
- Operator-side integration tests cross codebases — requires test environment with both
  Hub and LearnStack running.
- Twice the Keycloak realm setup.

### Neutral

- Hub's own Helm chart / docker-compose / Vault config — a parallel infra surface.

## Implementation notes

- Phase 01 — Repository scaffold: `learnstack-hub` repository created. Skeleton Hub solution
  + skeleton Next.js operator portal. Docker compose entry for Hub API + Hub DB schema
  initialised.
- Phase 02a (LearnStack side) — Platform kernel:
  - `IEntitlementProvider` interface in SharedKernel.
  - `NullEntitlementProvider` (all features allowed; default).
  - `platform_entitlement_cache` table (LearnStack PlatformDbContext) holds cached
    entitlement projection.
  - `/api/internal/tenants/{id}/entitlements` PUT endpoint (Hub → LearnStack).
  - `/api/v1/internal/license/verify` outbound call (LearnStack → Hub) via typed HttpClient.
- Phase 02c — Hub Foundation (parallel track, runs alongside Phase 02b):
  - Hub domain model: `LearnStackTenant`, `Plan`, `HubSubscription`, `Entitlement`,
    `CustomDomain`, `CompliancePolicy`.
  - Hub API: tenant lifecycle, plan management, entitlement projection, license verify.
  - mTLS + signed JWT + HMAC chain wired.
- Phase 03 — Identity (LearnStack side) integrates Hub authentication on the operator side;
  `learnstack-hub` realm setup; SSO between Hub UI and Hub API.
- Phase 09b — Hub Billing (parallel track):
  - Stripe + Iyzico integration via `IPaymentProvider` (ADR-0018-equivalent in Hub).
  - `HubInvoice`, `HubInvoiceLine`, `WebhookLedger`.
  - Operator UI for plan editor, invoice viewer, compliance caps editor.
- Phase 11 — Production hardening:
  - mTLS cert rotation procedure.
  - Hub's own production Helm chart.
  - Self-Hosted on-prem mode (LearnStack runs without Hub; signed license key from
    Hub-hosted-by-us covers entitlement).
- Phase 12 (optional) — Hub Marketplace: theme marketplace, content template marketplace,
  integration adapter marketplace.

The architecture deep dive, ER diagram, sequence diagrams for tenant provisioning, plan
upgrade, license verification, and webhook handling live in
[24-learnstack-hub.md](../architecture/24-learnstack-hub.md).

## References

- ADR-0014 — Adopt Dapr (Hub uses Dapr pub/sub for `tenant.entitlement.updated` events).
- ADR-0015 — API Gateway APISIX (Hub fronted by APISIX on `hub.learnstack.dev`).
- ADR-0017 — Tenant + Organization (Hub tracks orgs as aggregated usage signal).
- ADR-0018 — Tenant-Driven Customization (Hub stays out of customization decisions).
- ADR-0020 — Triple Deployment Model + Hybrid License.
- ADR-0021 — Feature-Based Entitlement Model.
- ADR-0022 — Custom Domain & TLS.
- [24-learnstack-hub.md](../architecture/24-learnstack-hub.md) — architecture deep dive.
- Nexora reference: `Nexora/docs/architecture/MANAGEMENT_PORTAL.md` (operator portal
  pattern), `Nexora/docs/decisions/0023-nmp-billing-model.md` (billing scope),
  `Nexora/docs/decisions/0030-license-hot-reload-mechanism.md` (mTLS + license cache),
  `Nexora/docs/operations/license-and-helm-upgrade.md`.

## Amendments

### 2026-05-18 — Phase number correction (02b → 02c)

When this ADR was authored the parallel-track Hub Foundation phase was tentatively
labelled "Phase 02b". The roadmap subsequently split the original Phase 02 into three
parts: **02a** (platform kernel + tenancy + foundation sockets + audit + customization),
**02b** (events / outbox / identity integration on the core side), and **02c** (Hub
Foundation in the separate `learnstack-hub` repo, running **in parallel** with 02b).
The Decision is unchanged: Hub is a separate codebase, separate Keycloak realm, mTLS
internal API, four-endpoint contract surface. Only the phase label is corrected
throughout this ADR's body — the Hub Foundation work belongs to **Phase 02c**, not
Phase 02b. See [phase-02c-hub-foundation.md](../roadmap/phase-02c-hub-foundation.md).

### 2026-05-19 — Implementation-notes phase split (Hub endpoints land in 02c, sockets in 02a)

The "Implementation notes" Phase 02a entry in this ADR's body lists the
`PUT /api/internal/tenants/{id}/entitlements` endpoint and the
`POST /api/v1/internal/license/verify` outbound call as Phase 02a deliverables.
That phrasing is corrected here without changing the Decision:

- **Phase 02a (LearnStack core side)** ships the *sockets* only:
  - `IEntitlementProvider` interface in SharedKernel + `NullEntitlementProvider`
    default,
  - `platform_entitlement_cache` table + read paths,
  - APISIX route group reserved for `/api/internal/*` (mTLS-guarded; the routes
    themselves carry no handlers yet).
- **Phase 02c (parallel Hub Foundation track)** lights up the four-endpoint
  contract surface end to end: the Hub-side authoring of
  `POST /api/internal/tenants`, `PUT /api/internal/tenants/{id}/entitlements`,
  and the LearnStack-side handlers for the same; the LearnStack-side typed
  `HttpClient` for the outbound `POST /api/v1/internal/license/verify` and
  `POST /api/v1/usage/report`; the `HubEntitlementProvider` implementation that
  consumes them.

The four-endpoint surface, the mTLS + signed JWT + HMAC chain, the closed-list
invariant, and the Hub's separate-codebase status are unchanged. Only the
phasing of who-ships-what is corrected.
