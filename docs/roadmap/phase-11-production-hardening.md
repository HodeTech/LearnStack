# Phase 11: Production Hardening, Operations, and Scale

## Goal

Turn a working product into an operable one, and build the infrastructure that was
deliberately not built earlier.

**Tenant authentication handoff — P02b-5.**
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md) qualifies the
closed API boundary with a controlled issuer; P02b-6/7 own actual realm/session
integration. This phase owns production IdP provisioning, key/lifetime operational
qualification, mapped readiness and distributed/edge quota capacity. Existing
visitor/peer floors still apply; the in-process subject limit promises no
independent end-to-end throughput or immediate JWT/session revocation.

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

**Distributed BFF sessions** — trigger: multiple native web processes must serve
one browser session. P02b-7's app-local `BffSessionStore` uses the volatile
`InMemoryBffSessionStore` under
[ADR-0070](../decisions/0070-owned-tenant-bff-sessions.md) and ADR-0035's dated
additive inventory. This phase supplies/qualifies the distributed adapter,
credential security/retention, atomic refresh/revocation/interruption and
production provider-event storage/capacity. Sticky routing is not continuity.

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
[ADR-0038](../decisions/0038-cross-cutting-port-and-event-contracts.md), which superseded
[ADR-0014](../decisions/0014-adopt-dapr.md) and keeps its Dapr choice, and
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
path, and the cross-instance invalidation topic. It does **not** carry generation counters:
Packet 5 removed `RemoveByPrefixAsync` rather than redesigning it, and the generation
pattern that replaces it is a caller-side convention over durable domain state, which no
cache adapter can own. See [ADR-0030](../decisions/0030-redis-compatible-store-valkey.md).

**Vault behind `ISecretProvider`** — *trigger: a production secret must rotate without
a redeploy, or more than one operator needs access to production secrets.* KV mount
layout, per-environment policies, the application
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
Until that skeleton lands, `NullEntitlementProvider` is the registered implementation for
the Self-Hosted modes as it is for every other one — the default in **every** mode, not
`Development` only
([ADR-0045 § 4](../decisions/0045-entitlement-and-feature-flag-socket.md),
[ADR-0020 Amendment](../decisions/0020-triple-deployment-hybrid-license.md)) — and
`IEntitlementProvider_Implementations_Are_Three` bounds the ceiling rather than requiring
the count. The provider is not this phase's deliverable; what follows is what that
skeleton is missing:

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
[Phase 02a Packet 9](phase-02a-kernel-tenancy.md) ships `audit_log` as a single **plain,
unpartitioned** table with the composite primary key `(id, timestamp)` from
[ADR-0033](../decisions/0033-audit-durability-model.md); nothing in ADR-0028 is executed
by that migration
([ADR-0028 Amendment, 2026-09-07](../decisions/0028-audit-log-partition-management.md)).
This phase creates the partitioned parent, attaches the Packet 9 table to it, and
recreates the indexes and the Row Level Security policy on the parent — the operation the
composite key exists to keep cheap, since PostgreSQL has no `ALTER TABLE … PARTITION BY`.
It also lands the two Hangfire jobs
[ADR-0028](../decisions/0028-audit-log-partition-management.md) specifies:
`learnstack:audit:partition-management` (two-month create-ahead horizon, platform-maximum
drop policy) and `learnstack:audit:retention-purge` (per-tenant, per-class row deletes
inside still-attached partitions), both on a daily cadence.
`Partition_Manager_Job_Is_Registered_AtStartup` moves with the job it guards: it is a
**Phase 11** rule, registered in
[the architecture-test catalogue](../standards/21-architecture-tests-catalogue.md) by
this phase rather than by Packet 9 — a rule registered against a job no phase has built
can only be satisfied by deleting it.
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
- **Hangfire queue fairness and per-tenant concurrency limits.** Authoritative
  LearnStack job rows carry the trusted tenant identity under
  [ADR-0047](../decisions/0047-transactional-background-job-runtime.md); vendor
  wakeups contain opaque references. P02b-4 already supplies tenant capacity
  reservations and independent pools. A tenant that enqueues fifty thousand
  notification jobs
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

Edge rate limiting at APISIX and the plan-level `LimitKeys.ApiRatePerMinute` cap are
the other two halves of this problem — they bound request *arrival*. This subsection
bounds request *cost* once a request is inside. Neither substitutes for the other.

### Security

- Qualify the APISIX `openid-connect` block against the tenant realm before
  gateway activation. Phase 03 corrects the route table; it does not activate
  the gateway. Adopt that corrected file rather than the older architecture
  sketch.
- Gate `/admin/hangfire*` at APISIX in addition to Phase 03's application
  Platform-scope permission and dated ADR-0015 disclosure. Prove both denied
  layers; a realm role alone grants no application authority.
- Secure headers (HSTS, CSP with nonces, COOP/CORP, Permissions-Policy).
- CORS policy enforced at APISIX (`cors` plugin) + per-handler ASP.NET layer.
- CSRF strategy for non-Action mutating routes.
- Rate limiting at APISIX (`limit-req` / `limit-count`) + per-handler ASP.NET layer
  for plan-level `LimitKeys.ApiRatePerMinute`. The enforcement **path** — the
  refusal a `Hard` limit key produces and the `usage.alert.soft_limit_reached` signal a
  `Soft` one produces — lands in [Phase 02c](phase-02c-hub-foundation.md)
  ([ADR-0045 § 6](../decisions/0045-entitlement-and-feature-flag-socket.md)); what this
  phase ships is that key's own gate, which arrives with the edge rate limiting it pairs
  with.
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
  no tenant `AuditConfig` override has narrowed baseline MUST coverage. A tenant-override
  **read** failure does not reject the request: classification falls back to the
  in-process catalogue, which carries the same MUST floor, and the failure is logged at
  `Error` — not on the audit health check, which answers only whether a MUST-class row can
  be written ([ADR-0033 § Fail-closed, stated precisely, and Amendment 6](../decisions/0033-audit-durability-model.md)).
  What fails closed is an unclassified operation, and a MUST-class row that cannot be
  written durably for an operation that would otherwise have **succeeded** — a standalone
  row recording an operation already being refused keeps its own 403 / 404
  ([ADR-0033 Amendment 1](../decisions/0033-audit-durability-model.md)).
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
- **Dependency automation and locked restores** — the three
  [Security Standards § Dependency Hygiene](../standards/11-security.md) rules the backend
  does not meet yet: an update bot (Renovate or Dependabot) opening version PRs, NuGet lock
  files (`RestorePackagesWithLockFile`), and `dotnet restore --locked-mode` in CI against
  them. Until they land, the backend's gate is the one Phase 02a Packet 9 shipped: NuGet
  audit over the whole graph, at any severity, failing CI.

### Tenant authentication production activation

Before production use of tenant authentication, complete P02b-6 realm issuance and
P02b-7 sessions and qualify the following operational work in this phase. An earlier
production rollout must bring these tasks forward through its decision pass; local
P02b-5 proofs do not establish production readiness. This is operational ownership,
not a request to add an unused module, adapter or port.

- **Issuer topology.** Prove DNS resolution, connection routing and TLS trust from
  the deployed API to its configured issuer, including its discovery and JWKS paths.
  [API Gateway § Current issuer topology](../architecture/30-api-gateway.md#current-issuer-topology-and-activation-limits)
  owns the current same-issuer constraint. A split metadata origin needs a separate
  accepted design and containment proofs before deployment; do not substitute an
  internal issuer merely to make the network request succeed.
- **BFF host and proxy activation.** Register each newly activated tenant host's
  exact HTTPS callback and root post-logout URI on the confidential web client
  before enabling sign-in there; domain verification alone grants no redirect
  registration. Qualify provider-event retention/storage and secret provisioning,
  including replacement of published development credentials. The current native
  listener counts its verified TCP peer as a source; proxy-derived visitor trust,
  IPv6 prefix fairness and multi-process continuity need explicit deployment
  decisions and proofs before promising those properties. P02b-7's two seed-host
  and controlled-issuer evidence supplies none of them.
- **BFF operational signals.** Before production activation, qualify closed
  lifecycle/failure visibility, session/pending/source/subject capacity pressure,
  alert thresholds, and a recovery runbook for the native owner.
  The current failure warnings are not capacity telemetry or a durable session
  ledger. Decide bounded signals and safe deployment attribution without token,
  actor, session-handle or arbitrary host labels; test actual exported privacy
  and volume before relying on dashboards or alerts.
- **Native BFF resource and clock qualification.** Qualify the deployment's
  listener/header/body/connection limits, trusted outbound TLS/proxy path and
  suspend/resume behavior before production activation. Current monotonic
  lifetimes and finite Node/form defaults do not promise suspended-host wall-time
  continuity or a production denial-of-service bound. Changing those semantics
  requires explicit review of the existing lifetime and transport contract.
- **Outage and key rotation.** Exercise cold start without metadata, a warmed known
  signing key during an outage, a new key while refresh fails, recovery, and rejection
  of a withdrawn unexpired key after the replacement set has been observed. Current
  configuration caching is distinct from last-known-good fallback: disabling the
  latter does not make every refresh failure a hard 401 outage, and it supplies no
  guaranteed outage grace period or immediate revocation. The existing controlled
  issuer proofs do not establish production DNS/TLS or mixed-version rollout behavior.
  The five-minute automatic refresh interval is asserted as configuration; manual
  `RequestRefresh()` and reduced on-demand intervals are not elapsed five-minute
  automatic-refresh evidence. Run that automatic cadence and mixed-version rollout
  drill here against the actual deployment.
- **Diagnostic contract and volume.** Current auth outcomes log at Information with
  stages `credential`, `profile` or `validation` and reasons `accepted` or `rejected`.
  The profile's internal `TenantTokenFailure` distinctions are not emitted, and there
  is no dedicated closed authentication outcome metric. These signals cannot
  distinguish expired-token traffic, issuer failure and key-rotation faults. Decide
  the required bounded taxonomy, counters, alert thresholds and success sampling or
  level before activation, preserving the raw vendor-data exclusion in
  [ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md#vendor-diagnostic-boundary).
  Authentication precedes subject admission, so its per-attempt Information volume
  is not bounded by the later subject quota. Measure cost under accepted and rejected
  traffic instead of silently changing the current log contract.
- **Correlation and proof limits.** Early auth failure events occur before tenant
  context is installed; the context-based enricher need not supply `correlation.id`
  on those events. Current privacy/positive-outcome tests do not prove correlation
  on every production failure. Qualify a safe pre-resolution correlation contract
  here without exporting credential or arbitrary claim text. Vendor event 3 coverage
  uses an interface-only configuration adapter to reach that handler catch boundary;
  natural production initial-metadata failure is covered through event 1. The event 3
  control is not evidence that the current production manager naturally follows it.
- **Organization-host refusal visibility.** ADR-0064 row 10, a tenant-only token on
  an organization host, remains 404 while membership denies everyone. It emits the
  resolver's generic Debug refusal, with no durable rejection row or dedicated
  mismatch counter. Phase 03 owns live membership; this phase decides and tests
  bounded operational visibility for refused membership/scope cases before production.
  An observability change must preserve the accepted no-durable-row rule unless a
  new decision explicitly changes it.
- **Subject capacity.** [ADR-0066](../decisions/0066-bounded-tenant-subject-admission.md)
  is Accepted — 2026-10-20. Implementation and local runtime/exported proofs are
  delivered; the [implementation record](phase-02b-delivery-log.md#adr-0066-acceptance-and-implementation-2026-10-20)
  owns independent reviews and final-source CI qualification.
  Its 4096 retained keys are shared across tenants, so occupied
  live slots can refuse other valid subjects. Quota/capacity use 429 with
  stock-quota/60-second hints; new-key sticky cleanup fault uses 503 with the
  common 30-second helper. Existing retained subjects keep their quota service.
  Qualify the accepted implementation's live/idle pressure, actual cleanup,
  4224 retained-plus-retiring resource bound and multiple-instance behavior here;
  measure supported sizing and publish refusal thresholds. Evaluate retained occupancy/headroom signals, actual live/idle retirement
  cost and the timer-only full-rotation horizon when selecting those thresholds;
  current rejection counters alone are not proactive capacity evidence.
  Production operations must route `learnstack_tenant_subject_admission_rejected_total{reason}` for
  `quota`, `registry_capacity` and `registry_fault`, plus the unlabelled once-only
  `learnstack_tenant_subject_cleanup_failed_total` transition. Accepted counters
  and local export proofs do not install alerts or mapped readiness. The future
  production runbook must distinguish quota replenishment, capacity pressure and
  sticky cleanup fault; only the fault branch recommends qualified instance
  replacement. Exercise blocked-disposal/request/shutdown behavior: one synchronous
  disposal has no wall-clock bound and a host grace deadline is not cleanup proof.
  Preserve the single cleanup owner and no new allocation/debit before the
  acquisition owner's successful cleanup/recheck. Do not use a restart or live
  eviction to restore spent quota or bypass ordinary capacity admission.
- **Readiness and incident response.** There is no mapped auth readiness check today;
  `/healthz` proves liveness. Decide whether an unavailable IdP changes whole-instance
  readiness or authenticated-traffic admission, preserving the credential-free public
  contract. Keep IdP state, subject capacity and audit-store failure distinguishable.
  Platform operations owns runbooks under `docs/runbooks/` for DNS/TLS/metadata failure,
  signing-key rollout and withdrawal, authentication-volume spikes and capacity
  refusal. Each runbook names the signal, diagnostic access, safe recovery and an
  exercised recovery control before production activation.

**Assertion-audit suppression is a separate incident.**
[ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md) accepts that an
attacker can consume a target tenant's durable allowance and later actors lose
durable rows during that window. Suppression attempts no store write; it is not an
audit-store failure, and the `audit` health check can remain healthy. It does not
mean complete durable actor evidence exists. Platform operations must alert on
`learnstack_tenant_assertion_audit_suppressed_total` deltas over a named window and
triage event 4006 `AuditBudgetExhausted` with its sanctioned typed attribution.
Exercise suppression and actual store-write failure separately, publish thresholds
and recovery instructions, and retain the budget and privacy boundary. Warnings and
counters continue during suppression; their volume and retention need sizing too.

### Reliability

- **Process supervision and crash recovery.** Select and qualify deployment-specific
  supervision, normal shutdown grace, a hard-termination backstop, bounded restart/
  backoff and crash-loop alerts/runbooks. Preserve
  [ADR-0059's host/exit boundary](../decisions/0059-owned-hangfire-server-generations.md#host-shutdown-bounds-and-observation);
  [ADR-0060's fatal remedy](../decisions/0060-fatal-hangfire-startup-and-unsafe-unwind.md)
  is implemented and qualified by P02b-4's isolated runtime proofs. Those proofs
  promise no automatic supervisor restart or production deployment qualification.
  A restarted API supplies no positive external quiescence or reservation release.
- **Production crash diagnostics.** Own runtime and OS crash-collector/dump policy:
  default enablement, secret exposure, access, storage, export and retention/deletion.
  Runtime dump switches alone do not control Linux core collectors or other OS
  crash reporting. Qualify the actual deployment policy before production use;
  P02b-4's isolated proof-child controls do not establish it.
- **Job-worker process boundary.** Revisit API-hosted workers when measured shared
  failure/resource contention violates the approved availability target or independent
  worker scaling is required. This phase owns the topology decision and credential/
  shutdown/diagnostic proofs; if the trigger fires earlier, that consumer phase must
  obtain a new ADR before extracting a worker service. No separate worker host or
  Dapr requirement is implied merely by proposing this decision task.
- Health checks and readiness / liveness endpoints. `GET /readyz` is promised by
  [API Standards § Health Endpoints](../standards/04-api-design.md) and served by no
  phase before this one — the API maps `/healthz` only — so this is where it lands, with
  the `audit` check [Packet 9](phase-02a-kernel-tenancy.md) registers as one of its
  entries.
- **Connection-pool sizing for the short second connection.** A request holds the unit of
  work's connection for its whole scope, and several reads and writes take a second, short
  one from the same application data source while it does: a flag, killswitch or
  audit-override cache miss, an organization-scope check, and the audit reconcile's
  standalone write — the last on exactly the requests that are already failing. A pool
  sized for one connection per request exhausts under a burst of those at half the load it
  was sized for, so the pool size and its saturation alert are set here, against that
  count.
- Background job retry policy under multi-pod load. The single-process retry and
  dead-letter contract — producer-side, subscriber-side and job-side — is
  [Phase 02b](phase-02b-events-auth.md)'s; this phase verifies it holds across pods and
  adds the fair-share and alerting surround, and does not rebuild it.
- Dead-letter operability: the alerting thresholds on each of the two **event**
  dead-letter counters and on the **job** terminal counter, the operator runbook, and
  the multi-pod behaviour of the replay path. The
  dead-letter mechanism itself — the terminal state, the counter and the MUST-class
  audit row — ships in [Phase 02b](phase-02b-events-auth.md).
- Outbox dispatcher reliability under multi-pod load. The claim protocol is already
  specified — [Events and Outbox § The claim protocol](../architecture/15-event-and-outbox.md#the-claim-protocol)
  describes [ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md)'s
  single-row acquisition tokens and fenced transitions. P02b-2 implements that
  producer protocol; P02b-3 owns durable subscriber execution. The
  [simpler-alternative section](../architecture/15-event-and-outbox.md#the-simpler-alternative-and-its-cost)
  records the supersession of the batch-held transaction fallback. This phase adds the
  multi-instance evidence the single-instance phase cannot produce: two real dispatcher
  processes across pods, lease-duration tuning against measured tail latency, and the
  lost-lease alert.
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
  check. Each feature key declares fail-open or fail-closed explicitly, on the descriptor
  [Phase 02a Packet 9](phase-02a-kernel-tenancy.md) ships; the classes themselves, and
  the key families that carry each one, are the table in
  [Hybrid License Model § Failure policy by key class](../architecture/26-hybrid-license-model.md).
- **Self-Hosted phone-home failure tolerance** — the 30-day grace period from
  [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md) validated end to end
  as part of licence-key hardening above.
- **Cross-instance L1 cache invalidation** via the `learnstack.cache.invalidation` topic,
  tested under failure (broker partition outage). This test becomes meaningful only once
  the Dapr and Valkey adapters land in this phase; before that there is one instance and
  one cache.

### Observability

- Frontend structured logging, Sentry and web-vitals telemetry, owned here by
  [Frontend Observability](../standards/10-observability.md#frontend-observability).
  P02d-5/6 provide no web-vitals hook.
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
- Cache strategy, including gateway/CDN response caching for public API responses
  and rendered pages. This phase settles host/tenant/organization/locale and
  eligibility partitioning, cache keys, validators and invalidation before enabling
  it. The Phase 02d API and renderer retain `no-store`; the generation-keyed
  definition cache does not authorize response or page caching.
- CDN strategy for media and public assets.
- Pagination enforcement.
- Search indexing optimization.
- Public page rendering performance, including the customization runtime's cost model —
  schema validation timing, block resolution caching, and the N+1 risk in a page composed
  of many `data_source` blocks.
- Classroom bandwidth profile testing.
- Recording and egress cost testing.
- The load tests required by [15-performance.md § Load Testing](../standards/15-performance.md).
- The audit write path's cost on the command path — the change-tracker capture, the
  MUST-class flush immediately before `COMMIT`, and the standalone reconcile — measured
  under those load tests and given a budget in
  [15-performance.md](../standards/15-performance.md).
  [Phase 02a Packet 9](phase-02a-kernel-tenancy.md) shipped it correct and unmeasured; a
  tenant's `AuditConfig` can narrow SHOULD/MAY coverage, and MUST-class overhead is not
  negotiable, so the budget is what tells a slow command from a mis-classified one.

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

This phase extends [Phase 03](phase-03-identity-admin.md)'s tenant-scoped DSAR
export/erasure, separate global closure and audit redaction across later modules
and retained copies. Phase 03 retains those implementations, subject mapping and
request authority. The canonical
[durable asynchronous store inventory](../architecture/23-data-protection.md#durable-asynchronous-store-inventory)
and its [first-writer prerequisites](../architecture/23-data-protection.md#erasure-authority-and-first-writer-prerequisites)
bound this work; this handoff chooses no retention period, purge principal or
new erasure permission.

- Backup strategy.
- Restore drills — a drill counts only when a backup is restored to a fresh instance and
  the integration suite passes against it.
- Retained database/WAL/backup, provider-event and diagnostic-copy reconciliation,
  including a post-restore control that does not reintroduce erased references.
  Source-row deletion and local BFF invalidation do not prove removal from these
  copies. Qualify the actual retention/deletion and operational evidence here.
- Migration strategy, including the two-step plan every destructive change requires.
- Seed data strategy.
- Data retention policy.
- Soft delete and purge jobs.
- Processed outbox, delivery, inbox and job-history purge decisions against the
  inventory. Among its first-party asynchronous tables, only
  `outbox_messages` currently grants Platform SELECT/DELETE;
  inbox, delivery and LearnStack job tables grant Platform no access. Immutable
  completion/execution facts and no-delete guards remain effective even with
  `BYPASSRLS`. Before the first purge writer, a bounded decision names the exact
  principal, minimum grants and required migration, eligible facts, dependency
  order, purge interval and retention/replay floor. Reuse Phase 03's established
  DSAR/redaction authority; any extension for later stores or purge requires its
  own bounded decision before its writer. A recurring job cannot borrow the
  dispatcher's credential or treat ordinary SystemActor delivery as authority.
  Retention must preserve deduplication throughout the maximum redelivery window;
  erasure cannot release unresolved reservations or prove external quiescence.
- Recording retention and purge jobs.
- `audit_log` retention and partition lifecycle — see **Demand-gated building blocks**
  above; the jobs and their cadence are specified by
  [ADR-0028](../decisions/0028-audit-log-partition-management.md). Per-tenant retention
  enforcement lands here too: Packet 9's `audit_config` carries classification overrides
  only, and no retention column, job or per-tenant override ships before this phase
  ([ADR-0044 § What we explicitly punted on](../decisions/0044-audit-write-path.md)).

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
- Complete Phase 03's export and erasure workflows across later modules and
  retained copies, preserving its global-`User`-versus-tenant-owned-profile
  boundary and first-writer prerequisites.
- Purge/retention and provider/diagnostic/backup operations, with exercised
  reconciliation evidence against the canonical inventory.
- Consent tracking.
- Terms / privacy content support.
- Classroom recording consent and retention review.

## Deliverables

- Dapr, Kafka, Valkey, Vault and APISIX adapters registered behind their existing ports,
  with the `DeploymentMode` composition root resolving each per mode.
- Integration suites for `Dedicated`, `SelfHostedOnline` and `SelfHostedAirGapped`, and
  the support claim updated from two modes to five.
- When multiple native web processes must serve one browser session, a distributed
  adapter behind `BffSessionStore` with qualified credential custody/retention and
  atomic refresh, revocation and interruption behavior before that topology is
  activated. A sticky route does not satisfy this trigger.
- `audit_log` converted to monthly range partitions — the parent created, the Packet 9
  table attached, indexes and policy recreated on it — with the partition-management and
  retention-purge jobs registered and monitored, and
  `Partition_Manager_Job_Is_Registered_AtStartup` catalogued here and green.
- Licence-key operational surround: rotation procedure, revocation-list distribution,
  hot-reload runbook, grace-period load-test results.
- Custom-domain TLS automation — ACME client, DNS provider adapters, renewal job, and
  the edge certificate installation path — with certificate material travelling by
  secret-store replication rather than by payload. The `host-mappings` endpoint itself
  shipped in [Phase 02c](phase-02c-hub-foundation.md).
- Managed-transcoder adapter behind `IVideoTranscoder`, or a recorded decision that its
  trigger has not fired.
- Air-gapped telemetry file target with its operational controls and its no-egress test.
- `GET /readyz` mapped and reading the registered health checks — the `audit` check from
  [Packet 9](phase-02a-kernel-tenancy.md) and the `outbox` check from
  [Phase 02b](phase-02b-events-auth.md) — plus the deployment-level backstop that stops
  serving past a configured unhealthy window. `/healthz` stays a liveness probe. The
  route is already in the host-classification bypass set and is mapped by no earlier
  phase.
- Resource-fairness controls: role-scoped `statement_timeout`, per-tenant pool
  partitioning, query cost ceiling, Hangfire fair-share dispatch with per-tenant
  concurrency caps, and bounded `data_source` queries.
- Production deployment pipeline, staging environment, rollback path.
- Qualified process supervision/restart/backoff, shutdown/termination and crash-dump
  policies, with crash-loop alerting and a recovery runbook. Record the worker-process
  topology decision against measured shared-failure/scaling triggers.
- Security baseline and the expanded tenant + organization isolation regression suite.
- Observability baseline: dashboards, alerts, and per-tenant resource metrics.
- [Tenant authentication activation](#tenant-authentication-production-activation)
  evidence, explicit diagnostic/readiness decisions, and exercised IdP, key,
  subject-capacity and assertion-suppression runbooks.
- Backup and restore process with a completed drill.
- Later-module and retained-copy DSAR reconciliation, bounded purge/retention
  decisions and operational controls against the canonical asynchronous-store
  inventory, preserving Phase 03's implementation and authority boundary.
- Performance baseline against the budgets in
  [15-performance.md](../standards/15-performance.md).
- Gateway/CDN response-cache policy and isolation/freshness proofs before activation,
  preserving the existing uncached public contract until that decision ships.
- Live classroom operations checklist.
- Release checklist and operational runbooks under `docs/runbooks/`.
- ADR-0026 Accepted, and
  [14-git-workflow.md § Tagging and Releases](../standards/14-git-workflow.md) updated to
  cite it instead of saying "ADR-pending".

## Completion Criteria

- The selected process topology passes an actual crash/restart and crash-loop drill
  under its deployment supervisor, including finite shutdown waiting, diagnostic/dump
  access and artifact handling. Recovery preserves uncertain jobs and reservations;
  neither API restart nor missing/orphan child processes release external occupancy.
  The worker-host separation decision and any fired trigger have an explicit owner.
- Every `DeploymentMode` value has an integration suite that runs a full user journey in
  CI, and the documented support claim matches what those suites prove.
- A second application instance can be started without a correctness regression:
  distributed cache, cross-instance invalidation, and outbox dispatch under two
  dispatchers all behave.
- Before multi-process web sessions are activated, two real web owners exercise
  the distributed BFF adapter: one refresh revision is published, revocation
  fences held/late work on either owner, interrupted grants are never blindly
  replayed, and actual settlement governs cleanup. Restart/failover, credentials
  and provider-event retention have explicit evidence; sticky routing or the
  existing volatile store alone cannot establish continuity.
- An integration event crosses a process boundary through Dapr and is delivered
  **at-least-once** to a subscriber in another process, with tenant context restored on
  the consumer side — and a forced redelivery of the same envelope produces exactly one
  effective business side effect, absorbed by `IInboxGuard`. That contract is stated by
  [Events and Outbox](../architecture/15-event-and-outbox.md#what-the-processor-guarantees)
  and by the glossary, and a cross-process transport does not change it. The retry and
  dead-letter contract being exercised here is
  [Phase 02b](phase-02b-events-auth.md)'s, unchanged.
- Two dispatcher processes in separate pods drain the same queue with no duplicate
  business effect; a lease that expires mid-dispatch is reclaimed, the stale processor's
  writes are no-ops under the ownership fence, and `learnstack_outbox_lease_lost_total`
  both increments and pages. The lease duration is set against measured tail latency
  rather than left at its single-instance default.
- Every dead-letter alert fires only when a **real** terminal transition is driven —
  exhausting the attempt budget, not inserting a terminal row directly, which increments
  nothing — and none fires on ordinary retry. The producer alert is additionally
  asserted on the **rate** of `learnstack_outbox_deadletter_total` over a named window;
  a cumulative threshold alone does not satisfy the shape
  [Events and Outbox § Dead-letter](../architecture/15-event-and-outbox.md#dead-letter-two-sides-two-failure-domains)
  states — "the alert is on the counter's rate, not on the table's size". The subscriber
  and job alerts keep their own thresholds, and the three are asserted separately, with
  a runbook entry for each.
- Each failure domain's recovery is asserted on its own path, because the two sides fail
  independently and the job side is a third: a producer-side replay resets the row and
  the next poll dispatches it, the consumer's guard absorbing the duplicate; a
  subscriber-side replay resets one `(event, consumer)` pair, reaches that subscription
  and no other, and a late success racing it is absorbed by `IInboxGuard`; and a
  terminal job is recovered by requeue and runs once. Each case names the single effect
  it expects, and each runs while a second pod holds the lease.
- Each asynchronous-store purge writer satisfies its bounded decision and exact
  migration/grant contract, using a named principal admitted by the required
  audited gate. Existing Platform outbox DELETE is not inbox, delivery or job
  authority. The inbox retention floor is at least the maximum redelivery window,
  asserted by advancing retention and showing a still-replayable event performing
  its effect once rather than twice. Immutable facts, uncertain effects and
  reservations survive until their independently required evidence permits change.
- No module references a Dapr, Kafka, Valkey or Vault client type.
  `Modules_Do_Not_Reference_DeploymentMode` is green.
- Production deployment is repeatable and documented.
- `GET /readyz` reads **both** registered checks — `audit` from Packet 9 and `outbox`
  from Phase 02b — and reports unhealthy when **either** is unhealthy; the orchestrator
  stops routing to that instance, and the deployment-level backstop stops serving once
  the configured unhealthy window elapses. `GET /healthz` answers 200 throughout — the
  liveness-versus-readiness split
  [Observability Standards § Health checks](../standards/10-observability.md#health-checks)
  sets.
- Backup restore test passes on a fresh instance.
- Export/erasure reconciliation covers later modules and retained provider,
  diagnostic, database/WAL/backup copies in the canonical inventory. Restoring a
  backup does not reintroduce erased references; local BFF invalidation and source
  cleanup are not used as evidence for independent copies.
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
- Every [tenant authentication activation task](#tenant-authentication-production-activation)
  has deployment-specific evidence. Cold/warm cache, failing refresh, key withdrawal,
  capacity refusal, assertion suppression and real audit-store failure have distinct
  operational controls; no liveness success substitutes for them.
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
  certificate renewal failure, broker outage, a tenant saturating a shared resource,
  and the [tenant authentication activation incidents](#tenant-authentication-production-activation).

Anything not true at this gate is either finished before launch or removed from the
support claim. There is no third option — a mode or capability that is documented,
untested and unsupported is the failure mode this roadmap's sequencing principle exists
to prevent.
