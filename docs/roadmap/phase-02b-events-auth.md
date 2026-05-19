# Phase 02b: Events, Outbox, and Identity Integration

## Goal

Wire the **outbox dispatcher** and **identity integration** on top of the Phase 02a
foundation. Phase 02a already shipped `IEventBus`, `ICacheService`, `ISecretProvider`,
the Dapr sidecar in dev compose, and the audit infrastructure; this phase delivers the
durable outbox pattern (write → poll → dispatch via `IEventBus` to Dapr pub/sub →
Kafka), the per-module inbox guard, Hangfire background job conventions, and Keycloak
OIDC integration sufficient for admin login from the studio.

Phase 02c (LearnStack Hub Foundation, separate `learnstack-hub` repo) runs in
**parallel** with this phase — both depend only on the 02a sockets and do not block
each other.

Decisions made in this phase:

- [ADR-0004 Authentication Strategy](../decisions/0004-authentication-strategy.md)
  (Accepted: self-hosted Keycloak; Amendment 1 adds the `learnstack-hub` realm — that
  realm itself ships in Phase 02c)
- [ADR-0006 Events and Outbox](../decisions/0006-events-and-outbox.md) (Accepted;
  Amendment 1: Dapr pub/sub as the dispatch transport)
- [ADR-0010 Cross-Module Communication](../decisions/0010-cross-module-communication.md)
  (Amendment 1: outbox dispatch target is Dapr pub/sub)
- [ADR-0014 Adopt Dapr](../decisions/0014-adopt-dapr.md) (sidecar wired in 02a; the
  dispatch path lands here)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
  (outbox / Hangfire correlation propagation; Hub HTTPS surface correlation
  middleware)

## Scope

### Event Infrastructure

- Domain event publishing (in-process, MediatR-style, internal to the module).
- Integration event publishing via outbox; **`OutboxProcessor`** (BackgroundService)
  polls every 200ms with `FOR UPDATE SKIP LOCKED` and dispatches through `IEventBus`
  (which wraps Dapr pub/sub → Kafka in non-dev modes, in-process publisher in dev).
- Retry with exponential backoff (1s, 5s, 30s, 5min, 1h); dead-letter after max
  attempts (5). Dead-letter visible via the `OutboxStatusEndpoints` admin API.
- Outbox row attaches `tenant_id`, `organization_id?`, `correlation_id`, `event_id`,
  `occurred_at`, versioned `type` to every event (per
  [ADR-0032 § Sub-decision 12](../decisions/0032-exception-handling-logging-and-observability.md)).
  Consumer handler restores `ITenantContext` from the envelope and starts an
  `Activity` with `traceparent` set to the row's `correlation_id` so the
  end-to-end trace stays continuous.
- Versioned integration event types in `<Module>.Application.Contracts`, inheriting
  `IntegrationEventBase`.
- Per-module **`inbox_messages`** table + `IInboxGuard`. Every
  `IIntegrationEventHandler<T>` invokes `IsAlreadyProcessedAsync` before business
  logic and `MarkAsProcessed` inside the same `DbContext` SaveChanges as the business
  write.
- Tenant + organization context restored in every event handler scope from the event
  envelope.
- A worked sample flow (e.g., synthetic `PlatformPingedV1`) demonstrates end-to-end
  dispatch + idempotent consumption.
- Cross-instance L1 cache invalidation rides on `learnstack.cache.invalidation` topic
  (modules with their own L1 caches subscribe here).

Outbox table schema, dispatcher behaviour, and dead-letter handling:
[Events and Outbox](../architecture/15-event-and-outbox.md), enforced by
[Architecture Standards](../standards/01-architecture-standards.md) and
[Infrastructure Stack Standards](../standards/20-infrastructure-stack.md).

### Background Jobs

- Hangfire wired with Postgres storage; queue naming policy.
- Job activator restores tenant context from the payload before any work runs.
- Job payloads without `TenantId` fail at enqueue time (architecture test enforces).
- Dead-letter handling for poisoned jobs.
- Outbox dispatcher runs as a recurring job; pending count and dispatch latency are surfaced as metrics.

### Identity Integration

LearnStack identity domain (`Membership`, `Role`, `Permission`, `Invitation`) lands in
Phase 03. Phase 02b delivers the **authentication** plumbing for the `learnstack`
realm (the `learnstack-hub` realm is a separate concern that ships in Phase 02c):

- Keycloak realm configuration for `learnstack`.
- OIDC PKCE flow integration with the .NET API (JWT validation against Keycloak JWKS,
  key caching + rotation handling).
- BFF callback handler in the Next.js app for the cookie session (HttpOnly + Secure +
  SameSite=Lax).
- Silent refresh through the BFF; the frontend never holds a refresh token.
- Seed users for development (platform admin + 2 tenant admins per tenant, each tenant
  with 2 organizations, idempotent on `make seed`).
- Active access-token TTL ≤ 1 hour; refresh flow tested end to end.
- Tenant + organization claim cross-check: a request whose host-derived
  `(tenant_id, organization_id)` disagrees with the JWT claims returns 404.

LearnStack does not implement password hashing, password reset rendering, MFA
enrolment flows, or refresh-token rotation. Those live in Keycloak; see
[13-identity-and-auth.md](../architecture/13-identity-and-auth.md) for the split.

### Audit Coverage Wiring

The cross-cutting audit pipeline lit up in Phase 02a; Phase 02b now adds the
**event-stream consumers** to it: outbox dead-letter entries write through it,
Keycloak-mirrored events (`user.created`, `password.reset.requested`) flow as
`learnstack.identity.user` Dapr events into the audit module's consumers, and
platform-admin scope entries from auth-related operations get the same treatment.
The `AuditEntry` aggregate is owned by the **Audit module** ([ADR-0016](../decisions/0016-audit-log-subsystem.md))
and shipped in Phase 02a — Phase 03 plugs Identity's events into it, not the other
way around.

See [18-audit-coverage.md](../standards/18-audit-coverage.md).

### Architecture Tests (event + identity layer)

In addition to Phase 02a's rules, this phase adds:

- Integration event types are JSON-serialisable records inheriting
  `IntegrationEventBase` (`Integration_Events_Inherit_From_IntegrationEventBase`).
- Read-model tables follow the `public_<module>_<concept>` naming.
- Hangfire job payloads include `tenant_id` (and `organization_id?` where the job
  touches org-scoped state).
- Provider SDK types (LiveKit, Stripe, Keycloak, Kafka, Vault) are not imported in
  `Domain` or `Application`.
- Outbox writes happen inside the same transaction as the originating domain change
  (interceptor-instrumented).
- `Integration_Event_Handlers_Use_InboxGuard` — every consumer invokes the inbox
  guard before processing.
- `OutboxProcessor_NeverBlocks_OnSingleMessageFailure` — integration test asserts one
  poisoned message doesn't prevent others in the batch from processing.
- `Outbox_Row_Carries_Correlation_Context` — every persisted outbox row has
  non-null `tenant_id` and `correlation_id` per
  [ADR-0032 § Sub-decision 12](../decisions/0032-exception-handling-logging-and-observability.md).
- `Hangfire_Job_Payloads_Include_TenantId` — enqueue-time guard rejects
  payloads missing `tenant_id` or `correlation_id`.
- `Integration_Event_Handler_Restores_Tenant_Context` — handler scope has
  `ITenantContext.IsResolved == true` before the inner pipeline runs.

The three identifiers above are catalogued (assertion, type, source) in
[21-architecture-tests-catalogue.md § Cross-cutting: error handling, logging, observability](../standards/21-architecture-tests-catalogue.md);
the catalogue is the canonical reference, this list is a Phase 02b
shipping checklist.

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
