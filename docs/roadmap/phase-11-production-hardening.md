# Phase 11: Production Hardening, Operations, and Scale

## Goal

Move LearnStack from MVP/demo quality to production-ready platform quality.

This phase is not only about performance. It covers security, observability, backups, deployment, data integrity, live classroom reliability, and operational maturity.

## Scope

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
  env files committed.
- Audit log coverage review against the per-module matrices.
- Tenant + **organization** isolation regression suite (expansion of the Phase 02a
  CI gate to cover every module from Phases 04–09).
- File upload security (MIME sniff, size limits, AV scan hook, key scoping).
- Live classroom token expiration and permission review.
- Provider webhook signature verification.
- **Hub contract surface review**: mTLS + signed JWT + HMAC pen-test, replay-window
  validation, four-endpoint surface confirmation (no creep).
- **APISIX standalone config review**: route allow-list, plugin order, mTLS guard on
  `/api/internal/*`.

### Reliability

- Health checks (LearnStack core + Hub).
- Readiness / liveness endpoints.
- Background job retry policy.
- Dead-letter handling (outbox DLQ + Hangfire DLQ).
- Outbox dispatcher reliability under multi-pod load.
- Idempotent webhook handling.
- Graceful shutdown.
- Live classroom provider failure handling.
- Recording job failure handling.
- **Hub outage tolerance** — LearnStack core continues to operate on the cached
  entitlement projection for at least 24h during a Hub outage; the 15-min TTL is the
  refresh cadence, the cached projection is the graceful-degradation buffer.
- **Self-Hosted phone-home failure tolerance** — 30-day grace period from
  ADR-0020 validated end-to-end.
- **Cross-instance L1 cache invalidation** via `learnstack.cache.invalidation` Dapr
  topic tested under failure (Kafka partition outage).

### Observability

- Structured logs.
- Correlation id.
- OpenTelemetry traces (LearnStack core + Hub share the same backend).
- Metrics.
- Error tracking.
- Slow query logging.
- Background job monitoring.
- Live classroom connection quality metrics.
- Provider usage and cost metrics.
- **Dapr-component health dashboards** (`dapr_component_pubsub_*`,
  `dapr_component_state_*`, `dapr_component_secretstores_*`) alongside LearnStack
  metrics.
- **APISIX gateway metrics** (route hit rate, plugin latency, mTLS handshake
  failures).
- **Hub-side metrics** (entitlement projection push lag, license-verify success
  rate, custom-domain pipeline state).
- **Outbox lag** (`learnstack_outbox_pending_count`) alerts above threshold per
  module.
- **Air-gapped OTLP file target** — wire the `SelfHostedAirGapped` telemetry
  path (Phase 02a Packet 3 left it as a documented seam) to a local file
  exporter under `/var/learnstack/otel/`, the contract target in
  [20-infrastructure-stack.md § Composition Root and Deployment Mode](../standards/20-infrastructure-stack.md).
  Packet 3 already guarantees no network egress in air-gapped mode (the OTLP
  network exporter is not wired there); this packet picks the concrete
  file-exporter package and points Serilog + the OTel SDK at the directory so
  air-gapped traces / metrics / logs land on disk for off-network shipping.
  This packet also defines the operational controls around that directory —
  rotation cadence and size/age retention limits, owning user/group and a
  restrictive permission mode, and the write-failure behavior when the
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
- Public page rendering performance.
- Classroom bandwidth profile testing.
- Recording and egress cost testing.

### Live Classroom Operations

LearnStack's classroom media stack is **self-hosted LiveKit OSS** ([ADR 0005](../decisions/0005-live-classroom-media-stack.md)). Phase 11 validates the self-hosted deployment for production and documents the Cloud-fallback path behind the same `ILiveClassProvider`.

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
- Restore drills.
- Migration strategy.
- Seed data strategy.
- Data retention policy.
- Soft delete and purge jobs.
- Recording retention and purge jobs.

### Deployment

- Production Dockerfiles for both LearnStack core and Hub.
- Environment configuration for the three production deployment modes
  (`SaaS`, `Dedicated`, `SelfHosted`) per
  [ADR-0020](../decisions/0020-triple-deployment-hybrid-license.md).
- CI / CD pipeline (separate pipelines for `learnstack` and `learnstack-hub` repos).
- Staging environment for both.
- Production environment for both.
- Rollback strategy (image-by-sha for both repos).
- Release checklist that covers the four-endpoint contract surface (any change to
  the surface is ADR-gated and ships in a coordinated cross-repo release).
- **Self-Hosted distribution** — packaged installer (Docker Compose bundle +
  Kubernetes Helm chart) with a documented license-key flow.

### Compliance Readiness

- GDPR/KVKK data handling review.
- User data export placeholder.
- User deletion/anonymization strategy.
- Consent tracking placeholder.
- Terms/privacy content support.
- Classroom recording consent and retention review.

## Deliverables

- Production deployment pipeline.
- Staging environment.
- Security baseline.
- Observability baseline.
- Backup/restore process.
- Performance baseline.
- Live classroom operations checklist.
- Release checklist.

## Completion Criteria

- Full user journey works in staging for all three deployment modes (`SaaS`,
  `Dedicated`, `SelfHosted`).
- Production deployment is repeatable and documented for LearnStack core **and**
  Hub.
- Backup restore test passes (LearnStack core + Hub independently).
- Tenant + organization isolation regression tests exist and are not skippable.
- Critical flows have sufficient logging and tracing including the Hub contract
  surface.
- Public site performance is acceptable.
- File uploads are safely constrained.
- In-app classroom flows are load-tested for the expected MVP usage.
- Live classroom cost monitoring exists before production launch.
- Hub outage tolerance proven (24h cached projection survival).
- Self-Hosted 30-day grace period proven end-to-end.

## Risks

- Underestimating operational debt when moving from MVP to production.
- Testing tenant isolation only manually.
- Taking backups without testing restore.
- Launching production without observability.
- Missing idempotency in critical payment or webhook flows.
- Launching in-app classes without bandwidth, region, TURN, recording, and cost controls.

