# Phase 02b: Events, Outbox, and Identity Integration

## Goal

Wire the asynchronous and identity-integration backbone on top of the Phase 02a foundation: domain + integration events, the outbox dispatcher with retry / DLQ, Hangfire background job conventions, and Keycloak OIDC integration sufficient for admin login from the studio.

This is the half of the original Phase 02 that depended on the tenant-resolution and architecture-test machinery being in place first.

Decisions made in this phase:

- [ADR 0004 — Authentication Strategy](../decisions/0004-authentication-strategy.md) (Accepted: self-hosted Keycloak)
- [ADR 0006 — Events and Outbox](../decisions/0006-events-and-outbox.md) (Accepted)

## Scope

### Event Infrastructure

- Domain event publishing (in-process, MediatR-style, internal to the module).
- Integration event publishing via outbox; dispatcher with retry / backoff and dead-letter handling.
- Outbox dispatcher attaches `tenant_id`, `correlation_id`, `event_id`, `occurred_at`, versioned `type` to every event.
- Versioned integration event types in `<Module>.Application.Contracts`.
- Tenant context restored in every event handler scope.
- A worked sample flow (e.g., synthetic `PlatformPingedV1`) demonstrates end-to-end dispatch + idempotent consumption.

Outbox table schema, dispatcher behaviour, and dead-letter handling: [Events and Outbox](../architecture/15-event-and-outbox.md), enforced by [Architecture Standards](../standards/01-architecture-standards.md).

### Background Jobs

- Hangfire wired with Postgres storage; queue naming policy.
- Job activator restores tenant context from the payload before any work runs.
- Job payloads without `TenantId` fail at enqueue time (architecture test enforces).
- Dead-letter handling for poisoned jobs.
- Outbox dispatcher runs as a recurring job; pending count and dispatch latency are surfaced as metrics.

### Identity Integration

LearnStack identity domain (`Membership`, `Role`, `Permission`, `Invitation`, `AuditLog`) lands in Phase 03. Phase 02b delivers the **authentication** plumbing:

- Keycloak realm configuration for `learnstack`.
- OIDC PKCE flow integration with the .NET API (JWT validation against Keycloak JWKS, key caching + rotation handling).
- BFF callback handler in the Next.js app for the cookie session (HttpOnly + Secure + SameSite=Lax).
- Silent refresh through the BFF; the frontend never holds a refresh token.
- Seed users for development (platform admin + 2 tenant admins, idempotent on `make seed`).
- Active access-token TTL ≤ 1 hour; refresh flow tested end to end.
- Tenant claim cross-check: a request whose host-derived tenant disagrees with the JWT `tenant_id` claim returns 404.

LearnStack does not implement password hashing, password reset rendering, MFA enrolment flows, or refresh-token rotation. Those live in Keycloak; see [13-identity-and-auth.md](../architecture/13-identity-and-auth.md) for the split.

### Audit Coverage Wiring

The cross-cutting audit pipeline becomes routable in this phase: every platform-admin scope entry, every Keycloak-mirrored event (`user.created`, `password.reset.requested`), every outbox dead-letter entry writes through it. The `AuditLog` aggregate itself is owned by the Identity module and ships in Phase 03 (see [02-domain-model.md § Identity](../architecture/02-domain-model.md) and [phase-03-identity-admin.md](phase-03-identity-admin.md)); this phase provides the pipeline (interceptor, actor propagation, redaction) that Identity plugs into. Per-module audit matrices land with their modules.

See [18-audit-coverage.md](../standards/18-audit-coverage.md).

### Architecture Tests (event + identity layer)

In addition to Phase 02a's rules, this phase adds:

- Integration event types are JSON-serialisable records.
- Read-model tables follow the `public_<module>_<concept>` naming.
- Hangfire job payloads include `tenant_id`.
- Provider SDK types (LiveKit, Stripe, Keycloak) are not imported in `Domain` or `Application`.
- Outbox writes happen inside the same transaction as the originating domain change (interceptor-instrumented).

## Deliverables

- Domain and integration event infrastructure with a sample flow.
- Outbox dispatcher with retry, backoff, dead-letter, and dashboards.
- Hangfire wired with tenant-aware job activator.
- Keycloak OIDC integration sufficient for admin login from the studio.
- BFF session cookie established; silent refresh works.
- Audit pipeline accepts entries from platform-admin scope and from Keycloak-mirrored events.
- Full architecture test suite green.

## Completion Criteria

- Domain + integration event infrastructure works with at least one sample flow end to end (publish → outbox → dispatch → idempotent consumer).
- A failed dispatch retries with backoff and dead-letters cleanly after the max attempt.
- A background job rejects payloads without a tenant id.
- A login flow against Keycloak completes in dev with the seed admin user; the JWT validates against JWKS; refresh works through the BFF.
- A request with mismatched host tenant vs JWT tenant returns 404 and writes an audit entry.
- The full architecture test suite is green and not skippable.

## Risks

- Forgetting tenant context in background jobs; mitigated by the job activator + enqueue guard.
- Treating outbox as an optional layer ("we'll add it for module X later"); mitigated by architecture test requiring same-transaction writes.
- Mixing Keycloak claims with LearnStack-side authorization decisions — see [13-identity-and-auth.md](../architecture/13-identity-and-auth.md) for the strict split.
- Keycloak as a sign-in bottleneck; mitigated by HA from the start with PostgreSQL as Keycloak's store and JWKS caching.

## Phase Exit Decision

Phase 03 (Identity domain, RBAC, Admin Foundation) can begin when the event/outbox baseline, background-job tenant context, and Keycloak login are stable and green in CI.
