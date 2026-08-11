# Phase 11: Production Hardening, Operations, and Scale

## Goal

Turn a working product into an operable one, and build the infrastructure that was
deliberately not built earlier.

Phase 11 carries two distinct bodies of work that share one exit gate.

The first is the classical hardening surface: security posture, reliability, backups,
observability backends, performance budgets, live-classroom operations, deployment
pipelines, and compliance readiness. That work has always belonged here.

The second is new. [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) applies
the one-way-door test to every infrastructure choice and routes the additive ones —
Dapr, Kafka, a distributed cache, Vault, APISIX, signed licence keys, custom-domain TLS
automation, `audit_log` partitioning — behind ports that ship in
[Phase 02a Packet 5](phase-02a-kernel-tenancy.md), with their vendor adapters landing
here against written triggers. Phase 11 is therefore not a checklist appended to a
finished product. It is a real phase that builds real subsystems, and it must be planned
as one.

Two consequences follow, and both are stated plainly rather than implied:

- **Until this phase ships, LearnStack supports `Development` and `SaaS` end to end.**
  `Dedicated`, `SelfHostedOnline` and `SelfHostedAirGapped` are prepared seams in the
  composition root — the enum values exist, the branches exist, the ports exist — but no
  integration suite proves them. Phase 11 builds those suites, and until it does, the
  product claim is two modes, not five.
- **A trigger may fire early.** Each demand-gated item below carries an observable
  condition, not a date. If one fires during an earlier phase, the item moves to that
  phase and ADR-0035's table is amended. Nothing here is waiting on the calendar.

## Scope

### Demand-gated building blocks

Most items below have a port in `LearnStack.SharedKernel` and a default implementation
from [Phase 02a Packet 5](phase-02a-kernel-tenancy.md), with a trigger condition recorded
in [ADR-0035 § The gated set](../decisions/0035-demand-gated-infrastructure.md), which is
the authority for those rows. Three items are gated work with no port and no row there —
the `audit_log` partition conversion (a migration, not a swap), the air-gapped OTLP file
target, and the deployment-mode integration suites — and their triggers are stated inline
below. `IVideoTranscoder`'s default ships in [Phase 04](phase-04-cms-media-pages.md)
rather than Packet 5. What follows is the work each one turns into once its trigger
fires.

**Dapr sidecar, pub/sub, state and secret components** — *trigger: a second process must
consume an integration event.* `DaprEventBus` behind `IEventBus`, the component YAML per
topic under the `learnstack.{module}.{aggregate}` convention, sidecar injection
annotations for the deployment target, and the Dapr health and readiness surface wired
into the platform's own probes. The `InProcessEventBus` shipped in Packet 5 keeps its
place as the `Development` transport, and keeps the same `IIntegrationEventHandler<T>`
interface, the same `IInboxGuard` and the same tenant-context restoration — which is
precisely what makes this swap a composition-root change rather than a refactor. See
[ADR-0014](../decisions/0014-adopt-dapr.md) and
[29-dapr-integration.md](../architecture/29-dapr-integration.md).

**Kafka** — *trigger: event volume, replay or ordering across processes is required.*
Broker topology, per-topic partition count and the partition key that guarantees
per-aggregate ordering, retention policy, consumer-group naming, and the subscriber-side
dead-letter destination. Kafka sits behind `IEventBus` through Dapr; no module ever holds
a producer. See [15-event-and-outbox.md](../architecture/15-event-and-outbox.md).

**Valkey-backed distributed cache behind `ICacheService`** — *trigger: more than one
application instance runs concurrently.* The `InMemoryCacheService` from Packet 5 is
correct for exactly one process and silently wrong for two, so this adapter lands the
moment a second replica does. Includes the L1/L2 layering used by the entitlement read
path, the cross-instance invalidation topic, and — if the generation-key redesign from
Packet 5 was chosen over removal — the generation counters that replace
`RemoveByPrefixAsync`. See [ADR-0030](../decisions/0030-redis-compatible-store-valkey.md).

**Vault behind `ISecretProvider`** — *trigger: a production secret must rotate without a redeploy, or more than one operator needs access to production secrets.* KV mount layout, per-environment policies, the application
role and its lease renewal, and the rotation cadence below under **Security**.
`ConfigurationSecretProvider` remains the `Development` implementation. No module imports a
Vault client; the swap happens once at the composition root.

**APISIX edge gateway** — *trigger: a non-dev deployment needs edge rate limiting, host
routing or JWT pre-validation.* Standalone file-driven configuration promoted to a
reviewed production artefact: the route allow-list, plugin ordering, the mTLS guard on
`/api/internal/*`, the CORS and rate-limit plugins, and the route-priority ordering that
keeps a public route from falling through to the authenticated catch-all. Until this
lands, the equivalent controls run as ASP.NET middleware in the application itself. See
[ADR-0015](../decisions/0015-api-gateway-apisix.md) and
[30-api-gateway.md](../architecture/30-api-gateway.md).

**`SignedLicenseKeyEntitlementProvider` hardening** — *trigger: a Self-Hosted contract is
signed.* The provider skeleton — `.lic` parsing, `kid` resolution, RS256
verification, payload-schema validation, and serving lookups from the embedded
projection — lands with the Hub repository's `P02c-6` as a coordinated pull request.
The provider is not this phase's deliverable; what follows is what that skeleton is
missing:

- Signing-key rotation procedure, including how a key issued under the previous
  generation stays verifiable through its remaining validity.
- Signed revocation-list distribution and the offline verification path for an
  air-gapped installation that cannot fetch one.
- `SIGHUP` hot-reload runbook — how an operator installs a renewed key without dropping
  in-flight requests, and what the process does when the new key fails verification.
- Multi-day grace-period load testing: the 30-day grace window from
  [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md) exercised end to end
  with the Hub unreachable for the whole window, not simulated with a clock jump on an
  idle instance.

See [26-hybrid-license-model.md](../architecture/26-hybrid-license-model.md).

**Custom-domain TLS automation** — *trigger: a tenant needs its own domain in
production.* Certificate issuance, renewal and revocation, the ACME client and DNS
provider adapters, the gateway-side certificate installation path, and the renewal job at
scale. The `PUT /api/internal/tenants/{id}/host-mappings` endpoint and its LearnStack-side
handler are **not** in this phase — they ship in
[Phase 02c](phase-02c-hub-foundation.md), because host resolution is a one-way door while
the automation that populates the mapping is additive
([27-custom-domain-tls.md § 11](../architecture/27-custom-domain-tls.md)). Certificate
material
moves between the Hub-owned and LearnStack-owned secret stores by secret-store
replication and is referenced from the host-mapping payload by path, never by value. Host
resolution itself never calls the Hub in any deployment mode: `IHostToTenantResolver`
reads `platform_host_to_tenant` and nothing else, and that is already true from
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md). See
[ADR-0022](../decisions/0022-custom-domain-tls.md) and
[27-custom-domain-tls.md](../architecture/27-custom-domain-tls.md).

**`audit_log` monthly partitioning and the retention job** — *trigger: measured
`audit_log` growth justifies partition maintenance.*
[Phase 02a Packet 9](phase-02a-kernel-tenancy.md) ships `audit_log` as a single plain
table with the corrected composite primary key `(id, timestamp)` from
[ADR-0033](../decisions/0033-audit-durability-model.md). This phase converts it to a
range-partitioned table and lands the two Hangfire jobs
[ADR-0028](../decisions/0028-audit-log-partition-management.md) specifies:
`learnstack:audit:partition-management` (two-month create-ahead horizon, platform-maximum
drop policy) and `learnstack:audit:retention-purge` (per-tenant, per-class row deletes
inside still-attached partitions), both on a daily cadence, plus the
`Partition_Manager_Job_Is_Registered_AtStartup` architecture test.
**ADR-0028 stands as a decision** — Hangfire over `pg_partman`, monthly range partitions,
the create-ahead and drop policies — and nothing in it is reopened. Only its schedule
moved: audit *correctness* is a Phase 02a concern, audit *scale* is this phase's.

**Air-gapped OTLP file target** — *trigger: a `SelfHostedAirGapped` deployment exists.*
Carried out of [Phase 02a Packet 3](phase-02a-kernel-tenancy.md), which left the
telemetry path for that mode as a documented seam with no network exporter wired. This
phase picks the file-exporter package, points Serilog and the OTel SDK at
`/var/learnstack/otel/`, defines the operational controls around that directory, and adds
the test asserting no network telemetry exporter is ever wired when `DeploymentMode` is
`SelfHostedAirGapped`. Specified in full under [Observability](#observability) below.

**`Dedicated` / `SelfHostedOnline` / `SelfHostedAirGapped` integration suites** —
*trigger: a contract or a deployment exists for the mode.* These three `DeploymentMode`
values are prepared seams until this phase builds their suites. **Until then, LearnStack
supports `Development` and `SaaS` end to end and nothing else.** A suite here means: the
composition root resolves every port to the implementation that mode requires, a full
user journey runs against that resolution in CI, and the mode's distinctive constraints
are asserted rather than assumed — no network egress for `SelfHostedAirGapped`, a
separate database and Keycloak realm per customer for `Dedicated`, licence-key
entitlement rather than Hub entitlement for both Self-Hosted variants. The composition
root keeps branching once on `DeploymentMode`; modules never read it, and
`Modules_Do_Not_Reference_DeploymentMode` keeps holding.

**Managed video transcoding behind `IVideoTranscoder`** — *trigger: in-house transcode
backlog or per-minute cost exceeds the managed alternative.*
[Phase 04](phase-04-cms-media-pages.md) ships an ffmpeg-backed worker as the single
registered implementation of `IVideoTranscoder`. Mux, AWS MediaConvert and Cloudflare
Stream sit behind the same port; adopting one is a composition-root change plus the
provider's credential wiring, its webhook receiver for completion callbacks, and a
cost-comparison record against the in-house baseline. See
[16-media-pipeline.md](../architecture/16-media-pipeline.md).

### Resource fairness

No earlier phase owns resource fairness.
[25-deployment-models.md § 2](../architecture/25-deployment-models.md#2-saas-mode) draws
the correctness / contention split and states why Row Level Security cannot close the
second half: RLS is a visibility predicate, and a filtered query consumes exactly as much
of the database as an unfiltered one. A single tenant running a pathological query
degrades every other tenant on the instance while all four isolation layers stay green.
That document names the gap; this phase closes it.

Resource fairness is a separate mechanism and this phase builds it.

- **`statement_timeout`.** A ceiling set per database role, not globally: a short one on
  `learnstack_app` (the request path, including anonymous public page renders), a longer
  one on `learnstack_platform` for administrative and reporting work, and an explicit
  exemption path for the migration role. A cancelled statement surfaces as an
  `InfrastructureException` mapped to RFC 7807 Problem Details with the correlation id —
  not as an unhandled 500. See
  [Database Standards § Connection Management](../standards/05-database.md).
- **Per-tenant connection-pool partitioning.** One shared Npgsql pool means one tenant's
  slow queries can hold every connection in it. The pool is partitioned so that no single
  tenant occupies more than a bounded share, with the remainder reserved so that a
  saturated tenant cannot make the platform unreachable. The partitioning must preserve
  PgBouncer **transaction**-pooling mode, which is a hard prerequisite for RLS —
  `SET LOCAL app.tenant_id` is transaction-scoped, and statement-mode pooling would reset
  it mid-transaction. `Db_Connection_String_Is_TransactionPooled` stays green through this
  work.
- **Query cost ceiling.** A planner-cost threshold above which a query is rejected before
  execution rather than cancelled after it has already consumed resources. This applies
  first and hardest to queries the platform did not write — see the `data_source` limit
  below — and second to any endpoint that accepts a caller-supplied filter or sort.
- **Hangfire queue fairness and per-tenant concurrency limits.** Every job payload
  already carries a tenant id (`Hangfire_Job_Payloads_Include_TenantId`), which is what
  makes fairness implementable. A tenant that enqueues fifty thousand notification jobs
  must not delay every other tenant's jobs behind them. The work: a per-tenant concurrency
  cap, a fair-share dispatch order across tenants rather than strict FIFO across the whole
  queue, a bounded per-tenant queue depth with a defined rejection behaviour when it is
  exceeded, and separate queues for latency-sensitive and bulk work so a bulk backlog
  cannot starve an interactive job.
- **A limit on the `data_source` query surface.**
  [ADR-0018](../decisions/0018-tenant-driven-customization-model.md) grants tenants a
  query surface inside `TenantPageBlock.data_source` — a content type, a filter, a limit
  and an ordering — and that surface currently has no ceiling. A tenant author can request
  `order_by: random` with a large limit over the tenant's whole content set on a page that
  every anonymous visitor loads. The bounds this phase sets: a maximum `limit` per query, a
  maximum number of `data_source` queries a single page render may issue, a requirement
  that filter fields be indexed, an explicit cost treatment for random ordering, and a
  render-time budget after which the block degrades to a cached or empty result rather
  than holding the request. This is the tenant-facing half of the query cost ceiling, and
  it sits inside the genericity boundary
  ([ADR-0018 Amendment, 2026-08-08](../decisions/0018-tenant-driven-customization-model.md)):
  the tenant declares *what* to fetch, the platform decides *how much*.

Edge rate limiting at APISIX and the plan-level `LimitKeys.MaxApiRequestsPerHour` cap are
the other two halves of this problem — they bound request *arrival*. This subsection
bounds request *cost* once a request is inside. Neither substitutes for the other.

### Security

- Secure headers (HSTS, CSP with nonces, COOP/CORP, Permissions-Policy).
- CORS policy enforced at APISIX (`cors` plugin) + per-handler ASP.NET layer.
- CSRF strategy for non-Action mutating routes.
- Rate limiting at APISIX (`limit-req` / `limit-count`) + per-handler ASP.NET layer
  for plan-level `LimitKeys.MaxApiRequestsPerHour`.
- Keycloak hardening review for **both realms** (`learnstack` + `learnstack-hub`):
  password policy, brute-force protection, MFA enforcement for tenant-admin /
  platform-admin / Hub-operator roles, refresh-token rotation. See
  [11-security.md](../standards/11-security.md). LearnStack does not implement any of
  these; configuration is reviewed in Keycloak.
- Secret management: Vault rotation cadence (90 days for provider keys, yearly for
  Hub HMAC shared secret + mTLS client certs), no secrets in repo, no secrets in
  env files committed. The Vault adapter itself lands in this phase — see
  **Demand-gated building blocks** above.
- Audit log coverage review against the per-module matrices, including confirmation that
  no tenant `AuditConfig` override has narrowed baseline MUST coverage and that a
  config-store failure still fails closed
  ([ADR-0033](../decisions/0033-audit-durability-model.md)).
- Tenant + **organization** isolation regression suite (expansion of the Phase 02a
  CI gate to cover every module from Phases 04–09). The suite runs as `learnstack_app`,
  the non-owning application role — a suite that runs as the table owner passes even when
  every policy is inert
  ([ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md)).
- File upload security (MIME sniff, size limits, AV scan hook, key scoping).
- Live classroom token expiration and permission review.
- Provider webhook signature verification.
- **Hub contract surface review**: mTLS + signed JWT + HMAC pen-test, replay-window
  validation on `jti`, and confirmation against the enumerated endpoint set in
  [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md). The property under
  review is not a count. It is the two invariants: the Hub stores no tenant content, and
  every LearnStack↔Hub crossing goes through `IEntitlementProvider`, `IUsageReporter` or
  `IHubTenantSync` — asserted by `Hub_NeverStores_TenantData` and
  `Hub_Client_Referenced_Only_By_Named_Adapters`.
- **APISIX standalone config review**: route allow-list, plugin order, route-priority
  ordering, mTLS guard on `/api/internal/*`.

### Reliability

- Health checks and readiness / liveness endpoints.
- Background job retry policy.
- Dead-letter handling (outbox DLQ + Hangfire DLQ), including the subscriber-side
  dead-letter destination for events that exhaust their retries.
- Outbox dispatcher reliability under multi-pod load — the claim mechanism must hold
  across the whole batch or use a lease column, so two dispatchers cannot both claim the
  same rows.
- Idempotent webhook handling.
- Graceful shutdown.
- Live classroom provider failure handling.
- Recording job failure handling.
- **Hub outage tolerance** — LearnStack continues to operate through a Hub outage on the
  entitlement read path fixed by
  [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md): L1 in-process cache →
  L2 distributed cache → `platform_entitlement_cache` (durable, carrying `valid_until`
  and `grace_until`) → Hub. The distributed-cache TTL is a refresh cadence; the durable
  projection and its grace window are the degradation buffer, and a cold cache during an
  outage falls through to the projection rather than throwing out of a feature-flag
  check. Each feature-key class declares fail-open or fail-closed explicitly.
- **Self-Hosted phone-home failure tolerance** — the 30-day grace period from
  [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md) validated end to end
  as part of licence-key hardening above.
- **Cross-instance L1 cache invalidation** via the `learnstack.cache.invalidation` topic,
  tested under failure (broker partition outage). This test becomes meaningful only once
  the Dapr and Valkey adapters land in this phase; before that there is one instance and
  one cache.

### Observability

- Structured logs.
- Correlation id end to end.
- OpenTelemetry traces.
- Metrics.
- Error tracking.
- Slow query logging — the diagnostic counterpart to the `statement_timeout` ceiling
  under **Resource fairness**.
- Background job monitoring.
- Live classroom connection quality metrics.
- Provider usage and cost metrics.
- **Dapr-component health dashboards** (`dapr_component_pubsub_*`,
  `dapr_component_state_*`, `dapr_component_secretstores_*`) alongside LearnStack
  metrics, once the Dapr adapter lands.
- **APISIX gateway metrics** (route hit rate, plugin latency, mTLS handshake
  failures).
- **Hub-side metrics** (entitlement projection push lag, license-verify success
  rate, custom-domain pipeline state), consumed from the same backend the Hub exports to.
- **Outbox lag** (`learnstack_outbox_pending_count`) alerts above threshold per
  module.
- **Per-tenant resource metrics** — statement duration percentiles, connection-pool
  occupancy, job queue depth and `data_source` render cost, all keyed by tenant. Without
  these, the fairness controls above are untunable: an operator cannot set a ceiling they
  cannot observe being approached.
- **Air-gapped OTLP file target** — wire the `SelfHostedAirGapped` telemetry
  path ([Phase 02a Packet 3](phase-02a-kernel-tenancy.md) left it as a documented seam)
  to a local file exporter under `/var/learnstack/otel/`, the contract target in
  [20-infrastructure-stack.md § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md).
  Packet 3 already guarantees no network egress in air-gapped mode (the OTLP
  network exporter is not wired there); this phase picks the concrete
  file-exporter package and points Serilog + the OTel SDK at the directory so
  air-gapped traces / metrics / logs land on disk for off-network shipping.
  It also defines the operational controls around that directory —
  rotation cadence and size/age retention limits, owning user/group and a
  restrictive permission mode, and the write-failure behaviour when the
  volume fills (best-effort, logged, never blocking the request path, per
  the pattern `LocalFileErrorTracker` already follows for
  `/var/learnstack/errors/`) — and adds a test asserting no network
  telemetry exporter is ever wired when `DeploymentMode` is
  `SelfHostedAirGapped`.

### Performance

- Database indexes.
- Query review.
- Cache strategy.
- CDN strategy for media and public assets.
- Pagination enforcement.
- Search indexing optimization.
- Public page rendering performance, including the customization runtime's cost model —
  schema validation timing, block resolution caching, and the N+1 risk in a page composed
  of many `data_source` blocks.
- Classroom bandwidth profile testing.
- Recording and egress cost testing.
- The load tests required by [15-performance.md § Load Testing](../standards/15-performance.md).

### Live Classroom Operations

LearnStack's classroom media stack is **self-hosted LiveKit OSS**
([ADR-0005](../decisions/0005-live-classroom-media-stack.md)). Phase 11 validates the
self-hosted deployment for production and documents the Cloud-fallback path behind the
same `ILiveClassProvider`.

- Validate the self-hosted LiveKit deployment under expected MVP load.
- Define the first production region strategy.
- Configure TURN/STUN: Coturn capacity, UDP/TCP ports, TLS for TURNS, network review when TURN traffic exceeds ~30% of total bandwidth.
- Confirm the LiveKit Cloud fallback path is still configurable in one switch (provider registration), even if not active.
- Define recording storage and retention rules.
- Define session recording consent workflow if recordings are enabled.
- Monitor participant minutes, outbound data transfer, and recording/transcoding usage per [08-livekit-cost-model.md](../architecture/08-livekit-cost-model.md).
- Define fallback behavior when classroom provider is unavailable (graceful error, retry queue, optional manual-link fallback when explicitly configured).

### Data Operations

- Backup strategy.
- Restore drills — a drill counts only when a backup is restored to a fresh instance and
  the integration suite passes against it.
- Migration strategy, including the two-step plan every destructive change requires.
- Seed data strategy.
- Data retention policy.
- Soft delete and purge jobs.
- Recording retention and purge jobs.
- `audit_log` retention and partition lifecycle — see **Demand-gated building blocks**
  above; the jobs and their cadence are specified by
  [ADR-0028](../decisions/0028-audit-log-partition-management.md).

### Deployment

- Production Dockerfile and image pipeline for LearnStack.
- Environment configuration for every `DeploymentMode` value, per
  [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md), with the integration
  suites described under **Demand-gated building blocks** proving each one.
- CI / CD pipeline.
- Staging environment.
- Production environment.
- Rollback strategy (image-by-sha).
- **Release-tag scheme.** [ADR-0026](../decisions/README.md) is reserved for this phase
  and is currently the open question behind
  [14-git-workflow.md § Tagging and Releases](../standards/14-git-workflow.md), which
  today says "`vYYYY.MM.DD.<n>` or `vMAJOR.MINOR.PATCH`. Specifically: ADR-pending". The
  decision must reconcile a continuously-deployed SaaS instance with a Self-Hosted
  release cadence where a customer runs a pinned version for months, and it must be
  **Accepted before this phase exits** — a Self-Hosted distribution without a stated
  version scheme cannot be supported.
- Release checklist covering the Hub contract surface: any change to the endpoint set in
  [ADR-0034](../decisions/0034-hub-contract-surface-invariant.md) is ADR-gated and ships
  as a coordinated cross-repository release, with the `entitlement-v1.schema.json`
  snapshot test green in both repositories.
- **Self-Hosted distribution** — packaged installer (Docker Compose bundle +
  Kubernetes Helm chart) with a documented licence-key flow.

> The Hub's own production pipeline, staging and production environments, and release
> process live in the `learnstack-hub` repository's roadmap. This phase owns only
> LearnStack's side and the coordination points between them.

### Compliance Readiness

- GDPR/KVKK data handling review.
- User data export.
- User deletion / anonymization strategy, honouring the global-`User`-versus-tenant-owned-profile
  boundary established in [Phase 03](phase-03-identity-admin.md).
- Consent tracking.
- Terms / privacy content support.
- Classroom recording consent and retention review.

## Deliverables

- Dapr, Kafka, Valkey, Vault and APISIX adapters registered behind their existing ports,
  with the `DeploymentMode` composition root resolving each per mode.
- Integration suites for `Dedicated`, `SelfHostedOnline` and `SelfHostedAirGapped`, and
  the support claim updated from two modes to five.
- `audit_log` converted to monthly range partitions, with the partition-management and
  retention-purge jobs registered and monitored.
- Licence-key operational surround: rotation procedure, revocation-list distribution,
  hot-reload runbook, grace-period load-test results.
- Custom-domain TLS automation — ACME client, DNS provider adapters, renewal job, and
  the edge certificate installation path — with certificate material travelling by
  secret-store replication rather than by payload. The `host-mappings` endpoint itself
  shipped in [Phase 02c](phase-02c-hub-foundation.md).
- Managed-transcoder adapter behind `IVideoTranscoder`, or a recorded decision that its
  trigger has not fired.
- Air-gapped telemetry file target with its operational controls and its no-egress test.
- Resource-fairness controls: role-scoped `statement_timeout`, per-tenant pool
  partitioning, query cost ceiling, Hangfire fair-share dispatch with per-tenant
  concurrency caps, and bounded `data_source` queries.
- Production deployment pipeline, staging environment, rollback path.
- Security baseline and the expanded tenant + organization isolation regression suite.
- Observability baseline: dashboards, alerts, and per-tenant resource metrics.
- Backup and restore process with a completed drill.
- Performance baseline against the budgets in
  [15-performance.md](../standards/15-performance.md).
- Live classroom operations checklist.
- Release checklist and operational runbooks under `docs/runbooks/`.
- ADR-0026 Accepted, and
  [14-git-workflow.md § Tagging and Releases](../standards/14-git-workflow.md) updated to
  cite it instead of saying "ADR-pending".

## Completion Criteria

- Every `DeploymentMode` value has an integration suite that runs a full user journey in
  CI, and the documented support claim matches what those suites prove.
- A second application instance can be started without a correctness regression:
  distributed cache, cross-instance invalidation, and outbox dispatch under two
  dispatchers all behave.
- An integration event crosses a process boundary through Dapr and is consumed exactly
  once by a subscriber in another process, with tenant context restored on the consumer
  side.
- No module references a Dapr, Kafka, Valkey or Vault client type.
  `Modules_Do_Not_Reference_DeploymentMode` is green.
- Production deployment is repeatable and documented.
- Backup restore test passes on a fresh instance.
- Tenant + organization isolation regression tests exist, are not skippable, and run as
  `learnstack_app`.
- **A single tenant cannot degrade another tenant's request latency beyond a stated
  bound.** Demonstrated, not asserted: a load test that runs a pathological query pattern
  and a large job burst on tenant A while measuring tenant B's p95 on the public read
  path.
- `audit_log` partitions exist for the current and next two months, the retention purge
  runs on schedule, and dropping a partition never removes a row still inside its
  retention class.
- No network telemetry exporter is wired under `SelfHostedAirGapped`, asserted by test
  rather than by review.
- Critical flows have sufficient logging and tracing, including the Hub contract surface.
- Public site performance meets the stated budgets, with the customization runtime's cost
  measured on a page composed of multiple `data_source` blocks.
- File uploads are safely constrained.
- In-app classroom flows are load-tested for the expected MVP usage, and classroom cost
  monitoring exists before production launch.
- Hub outage tolerance proven: entitlement resolution survives a Hub outage on a cold
  cache by falling through to `platform_entitlement_cache` and honouring `grace_until`.
- Self-Hosted 30-day grace period proven end to end on a running instance.
- ADR-0026 is Accepted.

## Risks

- **The phase is treated as a checklist.** It is the largest phase in the roadmap by
  volume of new subsystems. Planning it as a hardening pass at the end of a release
  guarantees it gets compressed, and the compressed part will be whichever item has no
  customer waiting on it — usually the fairness controls and the air-gapped mode.
- **The demand-gated adapters are written against an unfamiliar codebase.** They land
  after every consumer exists rather than before. The ports bound this cost, but an
  adapter written by someone who was not there when the port was designed will find
  assumptions the port never wrote down.
- **A trigger fires early and nobody notices.** Adding a second replica is a routine
  operational act that silently invalidates `InMemoryCacheService`. The triggers must be
  written into the operational runbooks, not only into
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md), so that the person
  scaling the deployment is the person who reads them.
- **Fairness controls are set once and never tuned.** A `statement_timeout` chosen
  without production percentile data is either useless or an outage. The per-tenant
  metrics are a prerequisite for the ceilings, not a companion to them.
- **Tenant isolation tested only manually,** or tested as the owning role where every
  policy is inert.
- **Backups taken without testing restore.**
- **Production launched without observability**, or with dashboards that show platform
  aggregates and no per-tenant breakdown — the shape that hides exactly the problem this
  phase exists to prevent.
- **Missing idempotency in payment or webhook flows.**
- **In-app classes launched without bandwidth, region, TURN, recording and cost
  controls.**
- **The support claim outruns the suites.** Announcing five deployment modes before their
  integration suites are green reintroduces the problem
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md) was written to remove.

## Phase Exit Decision

LearnStack is production-ready when all of the following hold:

- Every `DeploymentMode` value that the product claims to support has a green integration
  suite, and any value without one has been publicly reclassified as unsupported.
- Every demand-gated building block whose trigger has fired has shipped its adapter;
  every block whose trigger has not fired still has a working default, a named phase and
  a written trigger.
- A load test shows one tenant's worst-case behaviour leaving another tenant's p95 inside
  a stated bound, on both the request path and the job queue.
- A backup has been restored to a fresh instance and the integration suite passed against
  it, within the last quarter.
- The isolation regression suite covers every module shipped in Phases 04–09, runs as
  `learnstack_app`, and is not skippable.
- ADR-0026 is Accepted and the release process follows it.
- The operational runbooks under `docs/runbooks/` cover the incidents this phase's
  subsystems can produce: Hub outage, licence expiry, partition-management failure,
  certificate renewal failure, broker outage, and a tenant saturating a shared resource.

Anything not true at this gate is either finished before launch or removed from the
support claim. There is no third option — a mode or capability that is documented,
untested and unsupported is the failure mode this roadmap's sequencing principle exists
to prevent.
