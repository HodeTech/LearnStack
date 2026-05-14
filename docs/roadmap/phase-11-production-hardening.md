# Phase 11: Production Hardening, Operations, and Scale

## Goal

Move LearnStack from MVP/demo quality to production-ready platform quality.

This phase is not only about performance. It covers security, observability, backups, deployment, data integrity, live classroom reliability, and operational maturity.

## Scope

### Security

- Secure headers (HSTS, CSP with nonces, COOP/CORP, Permissions-Policy).
- CORS policy.
- CSRF strategy for non-Action mutating routes.
- Rate limiting at edge + per-handler.
- Keycloak hardening review: password policy, brute-force protection, MFA enforcement for tenant-admin / platform-admin roles, refresh-token rotation. See [11-security.md](../standards/11-security.md). LearnStack does not implement any of these; configuration is reviewed in Keycloak.
- Secret management: rotation cadence, secret-manager wiring, no secrets in repo.
- Audit log coverage review.
- Tenant isolation regression suite (expansion of Phase 02 CI gate to cover new modules from Phases 04-09).
- File upload security (MIME sniff, size limits, AV scan hook, key scoping).
- Live classroom token expiration and permission review.
- Provider webhook signature verification.

### Reliability

- Health checks.
- Readiness/liveness endpoints.
- Background job retry policy.
- Dead-letter handling.
- Outbox dispatcher reliability.
- Idempotent webhook handling.
- Graceful shutdown.
- Live classroom provider failure handling.
- Recording job failure handling.

### Observability

- Structured logs.
- Correlation id.
- OpenTelemetry traces.
- Metrics.
- Error tracking.
- Slow query logging.
- Background job monitoring.
- Live classroom connection quality metrics.
- Provider usage and cost metrics.

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

- Production Dockerfiles.
- Environment configuration.
- CI/CD pipeline.
- Staging environment.
- Production environment.
- Rollback strategy.
- Release checklist.

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

- Full user journey works in staging.
- Production deployment is repeatable and documented.
- Backup restore test passes.
- Tenant isolation regression tests exist.
- Critical flows have sufficient logging and tracing.
- Public site performance is acceptable.
- File uploads are safely constrained.
- In-app classroom flows are load-tested for the expected MVP usage.
- Live classroom cost monitoring exists before production launch.

## Risks

- Underestimating operational debt when moving from MVP to production.
- Testing tenant isolation only manually.
- Taking backups without testing restore.
- Launching production without observability.
- Missing idempotency in critical payment or webhook flows.
- Launching in-app classes without bandwidth, region, TURN, recording, and cost controls.

