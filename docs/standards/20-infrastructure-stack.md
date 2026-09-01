# 20 — Infrastructure Stack Standards

**Status:** Active
**Derives from:** [ADR-0038 Cross-Cutting Port and Event Contracts](../decisions/0038-cross-cutting-port-and-event-contracts.md),
[ADR-0015 API Gateway: APISIX](../decisions/0015-api-gateway-apisix.md),
[ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md),
[ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md),
[ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md),
[ADR-0029 Object Storage — SeaweedFS](../decisions/0029-object-storage-seaweedfs.md),
[ADR-0030 Redis-compatible Store — Valkey](../decisions/0030-redis-compatible-store-valkey.md),
[ADR-0031 PostgreSQL — Start on 18.x](../decisions/0031-postgresql-major-version.md),
[ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md),
[ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md).

Two audit decisions are referenced here but **not owned** here, and the split matters
because the two are easy to conflate:
[ADR-0033](../decisions/0033-audit-durability-model.md) owns the audit **durability
contract** — what must commit with what — and
[ADR-0028](../decisions/0028-audit-log-partition-management.md) owns the `audit_log`
**partition lifecycle**. This standard only records that partitioning is demand-gated
and that modules never write `audit_log` directly.

This standard defines how application code uses the foundation infrastructure introduced
in the 2026-05-18 redesign: Dapr building blocks, the APISIX gateway, the Hub HTTPS
contract surface, the entitlement projection, and the deployment-mode-aware composition
root. The broader operational rules (containers, CI/CD, DB ops, observability) live in
[12-infrastructure.md](12-infrastructure.md); the two standards are complementary, not
overlapping.

## Demand-Gated Building Blocks

**Read this section before the rest of the document.** Most of what follows describes
Dapr, Kafka, APISIX and Vault in the present tense. Those are accepted decisions about
**what** LearnStack uses; per
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) they are not all wired
today, and this section says which are.

The discriminator is the **one-way-door test**
([00-principles.md § 16](00-principles.md)):

> If I add this six months from now, will I have to touch code that is already written?

A **yes** ships now — tenant and organization isolation, the `outbox_messages` table and
its ownership, strongly-typed identifiers, the localization schema. A **no** ships as a
**port with a working default implementation** now, and its vendor adapter lands in a
named phase when a written trigger fires.

| Building block | Port | Registered today | Adapter lands in | Trigger |
|---|---|---|---|---|
| Dapr pub/sub | `IEventBus` | `InProcessEventBus` | [Phase 11](../roadmap/phase-11-production-hardening.md) | A second process must consume an integration event |
| Kafka | behind `IEventBus` | `InProcessEventBus` | [Phase 11](../roadmap/phase-11-production-hardening.md) | Cross-process volume, replay, or ordering is required |
| Dapr state / Valkey | `ICacheService` | `InMemoryCacheService` | [Phase 11](../roadmap/phase-11-production-hardening.md) | More than one application instance runs concurrently |
| Vault | `ISecretProvider` | `ConfigurationSecretProvider` (**startup-only**, see below) | [Phase 11](../roadmap/phase-11-production-hardening.md) | A production secret must rotate without a redeploy, **or** more than one operator needs access to production secrets |
| APISIX | composition root | ASP.NET middleware | [Phase 11](../roadmap/phase-11-production-hardening.md) | A non-dev deployment needs edge rate limiting, host routing, or JWT pre-validation |
| Hub entitlement | `IEntitlementProvider` | `NullEntitlementProvider` | [Phase 02c](../roadmap/phase-02c-hub-foundation.md) | A tenant must be billed or plan-gated |
| Signed licence key | `IEntitlementProvider` | `NullEntitlementProvider` | [Phase 11](../roadmap/phase-11-production-hardening.md) | A Self-Hosted contract is signed |
| Custom-domain TLS automation | `IHostToTenantResolver` + `ITlsCertificateProvider` | `platform_host_to_tenant` rows managed by configuration | [Phase 11](../roadmap/phase-11-production-hardening.md) | A tenant needs its own domain in production |
| `audit_log` partitioning + retention | schema-internal | Single correct table | [Phase 11](../roadmap/phase-11-production-hardening.md) | Measured `audit_log` growth justifies partition maintenance |
| Durable idempotency store | `IIdempotencyStore` | `InMemoryIdempotencyStore` | [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) | The first endpoint carrying `[Idempotent]`, **or** more than one application instance runs concurrently |
| Meilisearch | `ITenantSearch` | PostgreSQL full-text search | [Phase 09](../roadmap/phase-09-billing-integrations-analytics.md) | Search quality or scale exceeds PostgreSQL FTS |
| LiveKit | `ILiveClassProvider` | none — scheduled, not gated; see the exception below | [Phase 08c](../roadmap/phase-08c-classroom.md) | Live classes become a product requirement |
| Managed video transcoding | `IVideoTranscoder` | ffmpeg-backed worker ([Phase 04](../roadmap/phase-04-cms-media-pages.md)) | [Phase 11](../roadmap/phase-11-production-hardening.md) | In-house transcode backlog or per-minute cost exceeds the managed alternative |

Rules that follow from this:

- **Application code does not change when an adapter lands.** Every rule in the rest of
  this document — cache key shape, topic naming, secret namespace, outbox ownership — is
  written against the port and holds for the default implementation too. If a rule only
  makes sense for the vendor adapter, it is a rule about the adapter and belongs in that
  adapter's section, not in module guidance.
- **`InProcessEventBus` is a first-class transport, not a stub.** Same
  `IIntegrationEventHandler<T>`, same `IInboxGuard`, same tenant-context restoration as
  the durable path. A development path that skips those never exercises the isolation
  code, and every consumer would end up needing two implementations.
- **A demand-gated block is not "deferred".** It qualifies only with all four of: a
  port, a working default implementation, an owning phase, and a written trigger. If a
  trigger fires earlier than its phase, the item moves to the phase where it fired and
  ADR-0035's table is amended.
- **Support claims follow the wiring.** `DeploymentMode` keeps all five values and the
  composition root keeps branching on it, but only `Development` and `SaaS` are wired end
  to end before Phase 11. `Dedicated`, `SelfHostedOnline` and `SelfHostedAirGapped` are
  prepared seams, not supported deployments, until their integration suites exist.

## Composition Root and Deployment Mode

Every host (`LearnStack.Api`, worker, background-service host) reads
`DeploymentMode` at startup. Per
[ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md), `SelfHosted` is
split into two values so the composition root can pick between phone-home and
signed-license-key entitlement providers without runtime branching:

```csharp
public enum DeploymentMode
{
    Development,
    SaaS,
    Dedicated,
    SelfHostedOnline,
    SelfHostedAirGapped
}
```

The composition root branches on `DeploymentMode` to pick provider implementations.
Rules:

- The branching happens **exactly once** in the composition root. Modules never read
  `DeploymentMode` directly. If a module needs different behavior in different modes,
  the answer is two adapter implementations of the same interface registered in the
  composition root — not a runtime `if`.
- A failure to pick an implementation (e.g. `Production` mode with no
  `IEntitlementProvider` registered) fails fast at startup, not at first request.
- An architecture test
  (`Modules_Do_Not_Reference_DeploymentMode`) ensures no module assembly references the
  enum.

| Concern | `Development` | `SaaS` | `Dedicated` | `SelfHostedOnline` | `SelfHostedAirGapped` |
|---|---|---|---|---|---|
| Event bus | `InProcessEventBus` | `DaprEventBus` → Kafka | `DaprEventBus` → Kafka | `DaprEventBus` → Kafka (single-broker OK) | `DaprEventBus` → Kafka (single-broker OK) |
| Cache | `InMemoryCacheService` | `DaprCacheService` → Valkey | `DaprCacheService` → Valkey | `DaprCacheService` → Valkey | `DaprCacheService` → Valkey |
| Secrets | `ConfigurationSecretProvider` | `DaprSecretProvider` → Vault | `DaprSecretProvider` → Vault | `DaprSecretProvider` → Vault | `DaprSecretProvider` → Vault or file |
| Entitlement | `NullEntitlementProvider` | `HubEntitlementProvider` | `HubEntitlementProvider` | `HubEntitlementProvider` (phone-home) | `SignedLicenseKeyEntitlementProvider` |
| Host → tenant | Config / single tenant | Hub-mirrored projection | Hub-mirrored projection | Hub-mirrored projection | Config / `.lic` claim |
| Phone-home | n/a | enabled | enabled | enabled (daily, 30-day grace) | disabled |
| Error tracking ([ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)) | `NoOpErrorTracker` | `SentryErrorTracker` | `SentryErrorTracker` | `SentryErrorTracker` (optional; `NoOp` if no DSN) | `LocalFileErrorTracker` |
| OTLP exporter target ([ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md)) | local OTel Collector (dev compose) | central Collector | central Collector | customer-managed Collector | local file `/var/learnstack/otel/` |

**Reading the table.** It is the **target** wiring, not the current wiring. Every `Dapr*`
cell resolves to the `Development` column's default implementation until that block's
trigger fires (§ Demand-Gated Building Blocks). The branch structure is real and
exercised from [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md); the
right-hand implementations arrive with their adapters.

## Dapr Building Blocks

LearnStack uses three Dapr building blocks: **pub/sub**, **state**, **secrets**. Other
building blocks (service invocation, workflow, bindings, actors) are **out of scope** per
[ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md); do not
introduce them without a new ADR.

### `IEventBus` (pub/sub)

**Decision authority:**
[ADR-0038 § Event contract](../decisions/0038-cross-cutting-port-and-event-contracts.md#event-contract)
governs the envelope, topic, handler and transport rules below;
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) governs when the Dapr
adapter replaces `InProcessEventBus`.

- The **only** sanctioned way to publish an integration event is `IEventBus.PublishAsync`
  from inside the `OutboxProcessor`. Modules never call `IEventBus` directly — they
  write to the outbox.
- Topic names follow `learnstack.{module}.{aggregate}` (`learnstack.identity.user`,
  `learnstack.enrollment.enrollment`, `learnstack.classroom.session`). The convention
  applies to `InProcessEventBus` too. Not because the in-process transport routes on it
  — it addresses handlers by CLR type — but because the topic is declared by the event
  type and travels with it to whichever transport is registered, so the convention is
  checkable, and worth checking, before the first broker exists. Two tests, not one:
  `Integration_Event_TopicNames_FollowConvention` is transport-independent, asserts the
  convention over the declared event types, and lands with `InProcessEventBus` in
  [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md);
  `Dapr_PubSub_TopicNames_FollowConvention` additionally checks the Dapr component
  bindings and lands with that adapter in
  [Phase 11](../roadmap/phase-11-production-hardening.md). Both are registered in
  [21-architecture-tests-catalogue.md](21-architecture-tests-catalogue.md).
- Hub-side topics use the same `learnstack.hub.*` prefix
  (`learnstack.hub.entitlement`, `learnstack.hub.custom-domain.activated`).
- Consumers implement `IIntegrationEventHandler<TEvent>` and **must** invoke
  `IInboxGuard.IsAlreadyProcessedAsync` before any business logic. Phase 02b adds the
  architecture test `Integration_Event_Handlers_Use_InboxGuard` with the first real
  consumers; it is registered in the catalogue today.
- Cross-instance L1-cache invalidation rides on `learnstack.cache.invalidation` (a
  small payload of `(tenant_id, cache_key)`). Modules that maintain L1 caches subscribe
  here.

### `ICacheService` (state)

**Decision authority:**
[ADR-0038 § Cache contract](../decisions/0038-cross-cutting-port-and-event-contracts.md#cache-contract)
governs key isolation, the four-method port and single-flight behavior;
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) governs the Valkey/Dapr
adapter trigger.

- All Valkey access goes through `ICacheService`. Direct `IConnectionMultiplexer` /
  `IDistributedCache` injections are forbidden by the architecture test
  `Modules_Do_Not_Inject_Valkey_Directly`.
- Cache keys are `{tenant_id}:{module}:{logical-name}`, or
  `{tenant_id}:{organization_id}:{module}:{logical-name}` when the value is scoped to
  one organization. The `tenant_id` segment comes **first** and is mandatory even when
  a value is platform-wide. The only platform-wide family is the normalized Hub host
  map, which uses the sentinel `"platform"`; every other family requires a tenant id.
  Compose with `CacheKey.ForTenant` / `CacheKey.ForOrganization` /
  `CacheKey.ForHostMapping`; every `ICacheService` implementation calls
  `CacheKey.EnsureValid`, and none re-prefixes. There is no query filter and no RLS
  policy in front of a dictionary, so the key is the entire isolation boundary —
  which is why the shape is validated rather than left to each call site to remember.
- TTL defaults: 60s for hot-path reads (entitlement projection cache,
  permission cache), 5min for medium-warm reads, 1h for cold lookups. Anything
  longer needs explicit justification in code review.
- **Correctness never lives in the cache.** A miss is not an error, and an
  implementation may evict at any moment for any reason, so a caller that treats a
  miss as a failure has made a component whose contract is "sometimes" into one it
  depends on. Eager invalidation bounds staleness; it does not make the cache
  authoritative.

#### Cache layer cheat sheet

The `ICacheService` has **two TTL knobs** (`CacheOptions.L1Ttl`, `CacheOptions.L2Ttl`).
The most-referenced read paths follow this layered policy; mismatches across docs
(e.g. "60s cache" vs "15-min TTL") refer to different layers of the same cache, not
different decisions:

| Key family | L1 (in-process `IMemoryCache`) | L2 (Dapr state → Valkey) | Eager invalidation event |
|---|---|---|---|
| `platform:hub:host-map:{normalized-host}` (host → tenant) | 2 min | 15 min | `learnstack.hub.custom-domain.activated/.deactivated` |
| `{tenant_id}:hub:entitlement` (plan projection) | 60 s | 15 min (upper bound; Hub-push refresh resets it) | `learnstack.hub.entitlement` |
| `{tenant_id}:tenancy:feature-flags` | 60 s | 15 min | generation key — see the rule below |
| `{tenant_id}:identity:permissions:{session_id}` | 60 s | session-scoped (no L2) | `learnstack.identity.role` / `.membership` events |
| `{tenant_id}:tenancy:settings` (low-churn) | 5 min | 1 h | `learnstack.tenancy.settings` |

Each of these is produced by a `CacheKey` factory, never by string
interpolation, and the mapping is written down because it is the part that
drifts:

| Family | Composed by |
|---|---|
| `platform:hub:host-map:{normalized-host}` | `CacheKey.ForHostMapping(normalizedHost)` |
| `{tenant_id}:hub:entitlement` | `CacheKey.ForTenant(tenantId, "hub", "entitlement")` |
| `{tenant_id}:tenancy:feature-flags` | `CacheKey.ForTenant(tenantId, "tenancy", "feature-flags")` |
| `{tenant_id}:identity:permissions:{session_id}` | `CacheKey.ForTenant(tenantId, "identity", "permissions", sessionId)` |
| `{tenant_id}:tenancy:settings` | `CacheKey.ForTenant(tenantId, "tenancy", "settings")` |

This table is also the allowlist for the low-cardinality `cache.name` metric label.
An unregistered family is emitted as `other`; full keys and tenant, organization, host,
session, or entity identifiers are never metric labels. Add a new stable family here and
to the adapter's metric-name mapping together. The instrument catalogue is in
[Standards 10 § Metrics](10-observability.md#metrics).

The last-but-one takes a **multi-part** logical name, and so does the host
lookup. That is why the factories take one: a caller joining the parts itself
would put a separator inside a single segment, which `CacheKey` rejects — so
without multi-part names these two families could only have been hand-built,
past the one place `Guid.Empty`, non-canonical identifier rendering and
separator injection are checked. The host segment is the normalized host per
[ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md), which has
already had its port stripped; a host that still carried `:8443` would be
refused rather than silently splitting into two segments.

The host lookup is the **one** family that legitimately carries the `platform`
sentinel, and it is worth saying why: it answers "which tenant is this?", so by
construction there is no tenant to key it by. Every other family knows its tenant,
so a `platform` sentinel there would be a bug wearing the sentinel's clothes —
and `CacheKey.EnsureValid` now refuses every platform family except the exact normalized
host-map shape.

> An earlier version of this table listed these as `hub:host:{host}`,
> `hub:entitlement:{tenant_id}` and `tenant_feature_flags:{tenant_id}` — module
> first, tenant in the middle or absent. Each contradicted the key rule stated a few
> lines above it, and `CacheKey.EnsureValid` now rejects all three.

Rules:

- **There is no prefix invalidation.** `ICacheService` has no
  `RemoveByPrefixAsync` — it was removed in
  [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md) per
  [ADR-0038 § Cache contract](../decisions/0038-cross-cutting-port-and-event-contracts.md#cache-contract), because the only
  implementable form iterated a process-local key set and never evicted what
  another instance wrote. A key family that needs to invalidate a set it cannot
  enumerate uses the **generation-key** pattern instead: a durable counter bumped
  inside the business transaction and embedded in the key template
  ([architecture/32 § 8.2](../architecture/32-tenant-customization-model.md)), so
  a write makes every stale key unreachable at once without deleting any of them.
  The counter is domain state, never a cache entry — an evicted counter would make
  abandoned keys addressable again.
- L1 protects per-pod hot path; cross-pod consistency relies on L2 + eager
  invalidation.
- The 15-min L2 figure is an **upper bound**, not the typical refresh window —
  eager invalidation via Dapr is the typical path; the TTL is the safety net.
- A "60s cache" reference in any other document refers to L1; a "15-min TTL"
  reference refers to L2. These are not in conflict.

### `ISecretProvider` (secrets)

- Secrets are read **at startup** through `ISecretProvider` and bound to
  `IOptions<T>`. Runtime re-fetches happen via `IOptionsMonitor<T>` with a
  Vault-driven refresh — never an ad-hoc `ISecretProvider.GetAsync` call inside a hot
  path.
- **Until the Vault adapter lands, that refresh path does not exist.** `Development` and
  `SaaS` both run on `ConfigurationSecretProvider`, which resolves through
  `IConfiguration` at composition time — including the Sentry DSN, which
  `ErrorTrackingRegistration` reads once during registration. There is no configuration
  reload and no options rebinding behind it, so **every secret is fixed for the lifetime
  of the process and changing one is a redeploy**. That is not an oversight; it is
  precisely what makes the Vault trigger above legible — "a production secret must rotate
  without a redeploy" is a condition this arrangement cannot satisfy by construction, so
  the day it is required is the day the adapter is required. Do not describe
  `IOptionsMonitor<T>` refresh as available before then.
- The secret namespace is `learnstack/{deployment}/{module}/{key}` (e.g.
  `learnstack/saas/notifications/email-provider-api-key`). The deployment segment
  matches `DeploymentMode` (lower-case).
- No secret may appear in code, in `appsettings.*.json` checked into git, or in
  container env vars. The pre-commit hook scans for high-entropy strings; CI fails on
  hits.

## APISIX Gateway

- APISIX runs in **standalone mode** (YAML hot-reload, no etcd) per ADR-0015.
- The gateway is the **only** ingress for tenant-facing traffic. Direct ingress to
  `LearnStack.Api` pods is blocked at the network level.
- Plugin chain order, per route:
  1. `cors` (preflight separated from authenticated cross-origin)
  2. `jwt-auth` (Keycloak `learnstack` realm token verification — defense-in-depth; the
     API re-verifies internally)
  3. `limit-req` / `limit-count` (rate limit, per-tenant)
  4. `proxy-rewrite` / `request-id` (correlation id injection)
  5. `prometheus` (metrics export)
- Hub-facing internal routes (`/api/internal/*`) live under a **separate APISIX
  instance** (or a separate route set bound to a dedicated SSL object that pins
  `client.ca` to the LearnStack-internal CA — mTLS in APISIX is SSL-object config,
  not a route plugin) plus a route-level `ip-restriction` for the Hub egress range;
  the client certificate must be signed by that CA per
  [ADR-0019](../decisions/0019-learnstack-hub.md). The commented `/api/internal/*`
  stub in `infra/apisix/apisix.yaml` documents the canonical shape.
- Gateway config lives in `infra/apisix/` as version-controlled YAML. Hot-reload via
  `apisix reload` after a config change; no in-place edit of running configs.

## Outbox and Inbox

- Every integration event is written to `outbox_messages` **in the same `DbContext`
  transaction** as the aggregate that produced it. `IOutbox.EnqueueAsync` enrolls in
  the ambient `DbContext`; do **not** open a new transaction.
- The shared outbox table is RLS-protected; `OutboxProcessor` connects with the
  `learnstack_outbox_admin` role that bypasses RLS.
- `OutboxProcessor` uses `FOR UPDATE SKIP LOCKED` to allow horizontal scaling. Each
  message has its own transaction; one failure does not roll back the batch.
- Retry backoff: 1s, 5s, 30s, 5min, 1h. After max retries (5), the message is
  dead-lettered and surfaces via the `OutboxStatusEndpoints` admin API; manual
  intervention is required.
- Consumers are **idempotent** via per-module `inbox_messages` tables. The
  `IInboxGuard.MarkAsProcessed(eventId, eventTypeName)` write enrolls in the consumer's
  business `DbContext` so the inbox marker and the business write commit atomically.

Full deep dive: [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).

## Hub HTTPS Contract Surface

This is the day-to-day reference for the surface;
[ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) is the decision that
governs it, and the Hub-side halves live in the `learnstack-hub` repository.

### The two invariants

The surface is governed by **two invariants, not by an endpoint count**. The corpus
previously said "exactly four", which was never true — ADR-0019's own decision section
enumerates six paths — and protecting the number caused real damage: TLS private keys
were tunnelled through the entitlement payload to avoid declaring a fifth endpoint, and a
host lookup was added to the Hub client without being recorded at all.

1. **The Hub stores no tenant content.** Courses, lessons, learners, enrollments,
   classroom sessions, media and content entries live exclusively in LearnStack. The Hub
   holds tenant *metadata* — plan, subscription, licence, custom domain, compliance caps,
   aggregated usage. Enforced by `Hub_NeverStores_TenantData` (a Hub-schema scan, owned
   by the Hub repository).
2. **Every LearnStack↔Hub crossing goes through a named adapter** —
   `IEntitlementProvider`, `IUsageReporter`, `IHubTenantSync`. No other type in the
   codebase may hold a Hub client. Enforced by
   `Hub_Client_Referenced_Only_By_Named_Adapters` and
   `LearnStack_Modules_DoNotReference_Hub`.

Adding an endpoint still requires an ADR — not because the count is sacred, but because
the surface is a cross-repository contract and both repositories have to agree on it.

### The endpoint set

**Hub → LearnStack** — served by the internal listener only, never routed publicly:

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/internal/tenants` | Create tenant + default organization |
| `PUT` | `/api/internal/tenants/{id}/entitlements` | Push the entitlement projection |
| `PUT` | `/api/internal/tenants/{id}/status` | Suspend / activate / archive |
| `DELETE` | `/api/internal/tenants/{id}` | Terminate |
| `GET` | `/api/internal/tenants/{id}/usage` | Pull aggregated usage |
| `PUT` | `/api/internal/tenants/{id}/host-mappings` | Push host → `(tenant_id, organization_id?)` mappings |

**LearnStack → Hub:**

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/internal/license/verify` | Verify / pull the entitlement projection |
| `POST` | `/api/v1/internal/license/refresh` | Scheduled phone-home refresh |
| `POST` | `/api/v1/usage/report` | Report a usage metric (idempotent) |
| `POST` | `/api/v1/internal/tenants/{id}/custom-domains` | Submit a custom domain on behalf of a tenant admin (proxied; `IHubTenantSync`) |

The Hub's own tenant-facing and operator-facing APIs (`/api/v1/tenants/*`,
`/api/v1/subscriptions/*`, `/api/v1/webhooks/*`) are **not** part of this surface. They
are the Hub's public API, governed by the Hub repository.

### Rules

- **Every** endpoint above carries the full auth chain: **mTLS** + **signed JWT (RS256,
  `aud=learnstack-internal`, `exp ≤ 5 min`, replay-protected `jti`)** + **HMAC body
  signature**. All three must validate — but they fail at **two different layers**, and
  the distinction is not cosmetic. `/api/internal/*` is bound to its own mTLS listener
  and is **never proxied by APISIX**
  ([30-api-gateway.md § Topology](../architecture/30-api-gateway.md)), so a missing,
  expired or untrusted client certificate is rejected **during the TLS handshake**:
  there is no HTTP request, therefore no status code and no response body. Only the JWT
  and HMAC checks can return `401`, and they do so with no detail leak. A document
  promising `401` for an mTLS failure is describing a listener that does not exist.
  See [11-security.md § Hub Contract Surface](11-security.md).
- **TLS certificates and private keys never travel in the entitlement payload.** That
  payload is cached in `platform_entitlement_cache`, logged, audited and mirrored — every
  property you do not want a private key to have. Certificate material moves between the
  Hub-owned and LearnStack-owned secret stores by secret-store replication, referenced
  from the `host-mappings` payload **by path, not by value**.
- **Host resolution never calls the Hub.** `IHostToTenantResolver` reads
  `platform_host_to_tenant` and nothing else; `IHubClient.LookupHostAsync` does not
  exist. Putting the Hub on the hot path of an anonymous page load means a Hub outage
  takes tenant marketing sites down — see § Host → Tenant Resolution below.
- The Hub URL is read once at startup via `ISecretProvider` and bound to an
  `IOptions<HubOptions>` instance. Inject `IOptionsMonitor<HubOptions>` where dynamic
  refresh is needed.
- The projection's wire shape is pinned by a checked-in `entitlement-v1.schema.json` and
  a snapshot test in **both** repositories. A shape change that lands in one repository
  and not the other is a contract break, and the snapshot tests are what catch it.
- The Hub-backed adapters themselves are demand-gated: `NullEntitlementProvider` is the
  registered implementation until a tenant must be billed or plan-gated, at which point
  the adapters land in [Phase 02c](../roadmap/phase-02c-hub-foundation.md)
  ([ADR-0035](../decisions/0035-demand-gated-infrastructure.md)).

## Entitlement Projection

- `platform_entitlement_cache` is **read-only** from every module. Writes happen only
  via `IEntitlementProvider.RefreshAsync` (called by the Dapr event handler for
  `learnstack.hub.entitlement` and by the periodic 15-min sweep).
- `IFeatureFlags.IsEnabledAsync(FeatureKey)` is the only sanctioned read path. Direct
  SQL against `platform_entitlement_cache` outside the Tenancy module's infrastructure
  is forbidden (architecture test `Modules_Do_Not_Read_Entitlement_Cache_Directly`).
- The resolution order is **normative**
  ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)):
  `L1 in-process → L2 ICacheService → platform_entitlement_cache → Hub`. The durable
  projection sits **between** the caches and the Hub precisely so a cold cache during a
  Hub outage falls through to a stored answer with a recorded `grace_until`, rather than
  throwing out of a feature-flag check. Each feature-key class declares fail-open or
  fail-closed explicitly.
- Cache TTLs: **L1 (in-process `IMemoryCache`)** = 60s; **L2 (Dapr state → Valkey)** =
  15-minute upper bound. Eager invalidation flows from the Dapr event
  (`learnstack.hub.entitlement` / `learnstack.cache.invalidation`); the TTLs are the
  safety net, not the typical refresh window.
- For air-gapped deployments, `SignedLicenseKeyEntitlementProvider` reads a signed
  `.lic` file and runs the same projection write path; the rest of the system is
  source-agnostic.

## Host → Tenant Resolution

- The `platform_host_to_tenant` table is the **only** authority for
  `host → (tenant_id, organization_id?)` mapping. It is populated through
  `PUT /api/internal/tenants/{id}/host-mappings` for SaaS / Dedicated and by
  configuration for Self-Hosted.
- `IHostToTenantResolver` is the only sanctioned read path, and it reads
  `platform_host_to_tenant` and **nothing else**. It does not call the Hub — not on a
  cache miss, not as a fallback, not ever
  ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)). Host resolution sits
  on the hot path of every anonymous public page load; a resolver that calls a control
  plane converts a Hub outage into a tenant-marketing-site outage.
- A cache miss re-reads the table. An unknown host is a 404, not a Hub lookup.
- The frontend edge calls the resolver via a thin API endpoint; the backend uses it
  directly for inbound request resolution.
- Custom-domain activations on Hub push a new host-mapping set; LearnStack updates
  `platform_host_to_tenant` and invalidates the resolver cache. Once the event-bus
  adapter lands, the same update also arrives as
  `learnstack.hub.custom-domain.activated` / `.deactivated` — the push endpoint remains
  the authority, the event is the invalidation signal.

## Audit Plumbing

- Audit capture is wired through the shared `LearnStack.Infrastructure.Audit`
  pipeline. Modules do **not** write to `audit_log` directly.
- A command/query/event becomes audited by the **catalog** (MUST/SHOULD/MAY
  classification per [18-audit-coverage.md](18-audit-coverage.md)) and the MediatR
  `AuditLogBehavior`; there is no per-module audit code.
- `IAuditStore` is the only sanctioned write path; the architecture test
  `Modules_Do_Not_Write_AuditLog_Directly` enforces this.

## Background Jobs and Hangfire

- Long-running domain work (cohort enrollments, recording transcoding) goes on
  Hangfire. Short-running idempotent work goes on `IEventBus` consumers; the two are
  not interchangeable.
- Job names are namespaced `{module}.{job-name}` (`enrollment.bulk_grant`,
  `media.recording_transcode`).
- Recurring jobs are declared in code and registered at startup; ad-hoc UI scheduling
  is disabled.
- Hangfire's storage is its own Postgres schema (`hangfire`), separate from any
  module's schema.

## Network and Service Topology

- LearnStack monolith → APISIX edge → backend pods (1+).
- Dapr sidecar runs alongside every backend pod.
- Kafka, Valkey, Vault are accessed only via the Dapr sidecar. No direct client
  libraries for these three in application code.
- Postgres is accessed directly (EF Core); Dapr's state-store sits on Valkey, not
  Postgres.
- SeaweedFS is accessed via the configured S3-compatible client (no Dapr binding).

## Forbidden

- Direct `IConnectionMultiplexer` / `IDistributedCache` injection.
- Direct `KafkaProducer` / `ConsumerBuilder` / Confluent.Kafka usage.
- Direct `VaultClient` / Vault HTTP API calls.
- Direct `Sentry.SentrySdk` usage — capture happens via
  `IErrorTrackingProvider` per
  [ADR-0032](../decisions/0032-exception-handling-logging-and-observability.md).
- Reading `DeploymentMode` from inside a module.
- Calling Hub endpoints from anywhere except the dedicated `IEntitlementProvider` /
  `IUsageReporter` / `IHubTenantSync` adapters.
- Resolving a host by calling the Hub. `IHostToTenantResolver` reads
  `platform_host_to_tenant` only.
- Carrying TLS certificates or private keys in the entitlement payload, or in any other
  payload LearnStack caches, logs, audits or mirrors.
- Writing `outbox_messages` from outside the `IOutbox` interface.
- Writing `audit_log` from outside the `IAuditStore` interface.
- Writing `platform_entitlement_cache` from outside `IEntitlementProvider.RefreshAsync`.
- Adding a fifth Dapr building block without a new ADR.
- Adding an endpoint to the Hub contract surface without a new ADR.
- Promoting a demand-gated adapter without recording which trigger fired, and amending
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s table.

## References

- [ADR-0038 Cross-Cutting Port and Event Contracts](../decisions/0038-cross-cutting-port-and-event-contracts.md)
- [ADR-0015 API Gateway: APISIX](../decisions/0015-api-gateway-apisix.md)
- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md)
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
- [ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md)
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)
- [29-dapr-integration.md](../architecture/29-dapr-integration.md)
- [30-api-gateway.md](../architecture/30-api-gateway.md)
- [33-cross-cutting-concerns.md](../architecture/33-cross-cutting-concerns.md)
- [24-learnstack-hub.md](../architecture/24-learnstack-hub.md)
- [25-deployment-models.md](../architecture/25-deployment-models.md)
- [12-infrastructure.md](12-infrastructure.md) — operational rules (CI/CD, DB ops,
  containers, observability).
