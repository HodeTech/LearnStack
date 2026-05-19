# 20 — Infrastructure Stack Standards

**Status:** Active
**Derives from:** [ADR-0014 Adopt Dapr](../decisions/0014-adopt-dapr.md),
[ADR-0015 API Gateway: APISIX](../decisions/0015-api-gateway-apisix.md),
[ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md),
[ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md),
[ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md),
[ADR-0029 Object Storage — SeaweedFS](../decisions/0029-object-storage-seaweedfs.md),
[ADR-0030 Redis-compatible Store — Valkey](../decisions/0030-redis-compatible-store-valkey.md),
[ADR-0031 PostgreSQL — Start on 18.x](../decisions/0031-postgresql-major-version.md).

This standard defines how application code uses the foundation infrastructure introduced
in the 2026-05-18 redesign: Dapr building blocks, the APISIX gateway, the Hub HTTPS
contract surface, the entitlement projection, and the deployment-mode-aware composition
root. The broader operational rules (containers, CI/CD, DB ops, observability) live in
[12-infrastructure.md](12-infrastructure.md); the two standards are complementary, not
overlapping.

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
| Event bus | `InProcessEventBus` (MediatR) | `DaprEventBus` → Kafka | `DaprEventBus` → Kafka | `DaprEventBus` → Kafka (single-broker OK) | `DaprEventBus` → Kafka (single-broker OK) |
| Cache | `InMemoryCacheService` | `DaprCacheService` → Valkey | `DaprCacheService` → Valkey | `DaprCacheService` → Valkey | `DaprCacheService` → Valkey |
| Secrets | `EnvironmentSecretProvider` | `DaprSecretProvider` → Vault | `DaprSecretProvider` → Vault | `DaprSecretProvider` → Vault | `DaprSecretProvider` → Vault or file |
| Entitlement | `NullEntitlementProvider` | `HubEntitlementProvider` | `HubEntitlementProvider` | `HubEntitlementProvider` (phone-home) | `SignedLicenseKeyEntitlementProvider` |
| Host → tenant | Config / single tenant | Hub-mirrored projection | Hub-mirrored projection | Hub-mirrored projection | Config / `.lic` claim |
| Phone-home | n/a | enabled | enabled | enabled (daily, 30-day grace) | disabled |

## Dapr Building Blocks

LearnStack uses three Dapr building blocks: **pub/sub**, **state**, **secrets**. Other
building blocks (service invocation, workflow, bindings, actors) are **out of scope** per
ADR-0014 non-goals; do not introduce them without a new ADR.

### `IEventBus` (pub/sub)

- The **only** sanctioned way to publish an integration event is `IEventBus.PublishAsync`
  from inside the `OutboxProcessor`. Modules never call `IEventBus` directly — they
  write to the outbox.
- Topic names follow `learnstack.{module}.{aggregate}` (`learnstack.identity.user`,
  `learnstack.enrollment.enrollment`, `learnstack.classroom.session`). The
  architecture test `Dapr_PubSub_TopicNames_FollowConvention` enforces this.
- Hub-side topics use the same `learnstack.hub.*` prefix
  (`learnstack.hub.entitlement`, `learnstack.hub.custom-domain.activated`).
- Consumers implement `IIntegrationEventHandler<TEvent>` and **must** invoke
  `IInboxGuard.IsAlreadyProcessedAsync` before any business logic. The architecture
  test `Integration_Event_Handlers_Use_InboxGuard` enforces this.
- Cross-instance L1-cache invalidation rides on `learnstack.cache.invalidation` (a
  small payload of `(tenant_id, cache_key)`). Modules that maintain L1 caches subscribe
  here.

### `ICacheService` (state)

- All Valkey access goes through `ICacheService`. Direct `IConnectionMultiplexer` /
  `IDistributedCache` injections are forbidden by the architecture test
  `Modules_Do_Not_Inject_Valkey_Directly`.
- Cache keys are `{tenant_id}:{module}:{logical-name}`. The
  `tenant_id` prefix is **mandatory** even when a value is platform-wide — use the
  sentinel `"platform"` tenant id rather than omitting the prefix.
- TTL defaults: 60s for hot-path reads (host → tenant, entitlement projection cache,
  permission cache), 5min for medium-warm reads, 1h for cold lookups. Anything
  longer needs explicit justification in code review.
- Eager invalidation publishes to `learnstack.cache.invalidation`; do not rely on TTL
  expiry for correctness.

#### Cache layer cheat sheet

The `ICacheService` has **two TTL knobs** (`CacheOptions.L1Ttl`, `CacheOptions.L2Ttl`).
The most-referenced read paths follow this layered policy; mismatches across docs
(e.g. "60s cache" vs "15-min TTL") refer to different layers of the same cache, not
different decisions:

| Key family | L1 (in-process `IMemoryCache`) | L2 (Dapr state → Valkey) | Eager invalidation event |
|---|---|---|---|
| `hub:host:{host}` (host → tenant) | 2 min | 15 min | `learnstack.hub.custom-domain.activated/.deactivated` |
| `hub:entitlement:{tenant_id}` (plan projection) | 60 s | 15 min (upper bound; Hub-push refresh resets it) | `learnstack.hub.entitlement` |
| `tenant_feature_flags:{tenant_id}` | 60 s | 15 min | `learnstack.cache.invalidation` (key prefix) |
| Permission lookup per session | 60 s | session-scoped (no L2) | `learnstack.identity.role` / `.membership` events |
| Tenant settings (low-churn) | 5 min | 1 h | `learnstack.tenancy.settings` |

Rules:

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

- The LearnStack core talks to the Hub through **exactly four** endpoints:
  - `POST /api/v1/internal/license/verify` (LearnStack → Hub: license check)
  - `POST /api/v1/usage/report` (LearnStack → Hub: usage telemetry)
  - `PUT /api/internal/tenants/{id}/entitlements` (Hub → LearnStack: projection push)
  - `POST /api/internal/tenants` (Hub → LearnStack: tenant create)
- All four use **mTLS** + **signed JWT (RS256)** + **HMAC body signature**. No
  additional endpoints get added to this surface without a new ADR.
- The Hub URL is read once at startup via `ISecretProvider` and bound to an
  `IOptions<HubOptions>` instance. Inject `IOptionsMonitor<HubOptions>` where dynamic
  refresh is needed.
- An architecture test
  (`LearnStack_Modules_DoNotReference_Hub`) ensures no module assembly references the
  Hub URL directly; only the dedicated `IEntitlementProvider` / `IUsageReporter`
  adapters in `LearnStack.Infrastructure` do.

## Entitlement Projection

- `platform_entitlement_cache` is **read-only** from every module. Writes happen only
  via `IEntitlementProvider.RefreshAsync` (called by the Dapr event handler for
  `learnstack.hub.entitlement` and by the periodic 15-min sweep).
- `IFeatureFlags.IsEnabledAsync(FeatureKey)` is the only sanctioned read path. Direct
  SQL against `platform_entitlement_cache` outside the Tenancy module's infrastructure
  is forbidden (architecture test `Modules_Do_Not_Read_Entitlement_Cache_Directly`).
- Cache TTLs: **L1 (in-process `IMemoryCache`)** = 60s; **L2 (Dapr state → Valkey)** =
  15-minute upper bound. Eager invalidation flows from the Dapr event
  (`learnstack.hub.entitlement` / `learnstack.cache.invalidation`); the TTLs are the
  safety net, not the typical refresh window.
- For air-gapped deployments, `SignedLicenseKeyEntitlementProvider` reads a signed
  `.lic` file and runs the same projection write path; the rest of the system is
  source-agnostic.

## Host → Tenant Resolution

- The `platform_host_to_tenant` table is the **only** authority for
  `host → (tenant_id, organization_id?)` mapping. It is populated by the Hub for
  SaaS / Dedicated and by config for SelfHosted.
- `IHostToTenantResolver` is the only sanctioned read path. The frontend edge calls it
  via a thin API endpoint; the backend uses it directly for inbound request resolution.
- Custom-domain activations on Hub publish `learnstack.hub.custom-domain.activated`
  (and `.deactivated`) Dapr events; LearnStack listens, updates
  `platform_host_to_tenant`, and invalidates the resolver cache.

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
- Reading `DeploymentMode` from inside a module.
- Calling Hub endpoints from anywhere except the dedicated `IEntitlementProvider` /
  `IUsageReporter` / `IHubTenantSync` adapters.
- Writing `outbox_messages` from outside the `IOutbox` interface.
- Writing `audit_log` from outside the `IAuditStore` interface.
- Writing `platform_entitlement_cache` from outside `IEntitlementProvider.RefreshAsync`.
- Adding a fifth Dapr building block without a new ADR.
- Adding a fifth Hub endpoint without a new ADR.

## References

- [ADR-0014 Adopt Dapr](../decisions/0014-adopt-dapr.md)
- [ADR-0015 API Gateway: APISIX](../decisions/0015-api-gateway-apisix.md)
- [ADR-0019 LearnStack Hub](../decisions/0019-learnstack-hub.md)
- [ADR-0020 Triple Deployment + Hybrid License](../decisions/0020-triple-deployment-hybrid-license.md)
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md)
- [29-dapr-integration.md](../architecture/29-dapr-integration.md)
- [30-api-gateway.md](../architecture/30-api-gateway.md)
- [24-learnstack-hub.md](../architecture/24-learnstack-hub.md)
- [25-deployment-models.md](../architecture/25-deployment-models.md)
- [12-infrastructure.md](12-infrastructure.md) — operational rules (CI/CD, DB ops,
  containers, observability).
