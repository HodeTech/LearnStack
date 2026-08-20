# 10 — Observability Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0006 — Events and Outbox](../decisions/0006-events-and-outbox.md) (outbox + tenant-context propagation across async boundaries), [ADR 0032 — Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md) (Sentry / OTel boundary, Serilog wiring, span attribute propagation, deployment-mode branching).

Three signals — logs, traces, metrics — bound by a single correlation id. Everything we ship is observable from day one.

Conceptual deep dive (logging pipeline, tracing pipeline, span enrichment
seam, correlation across async boundaries) lives in
[33-cross-cutting-concerns.md](../architecture/33-cross-cutting-concerns.md).
This standard contains the day-to-day rules.

## Stack

- **OpenTelemetry** SDK for traces + metrics.
- **Serilog** as the primary logger; modules log via `ILogger<T>`
  (`Microsoft.Extensions.Logging`), the Serilog implementation is wired once
  at the composition root. Logs flow Serilog → OTLP sink → OTel Collector →
  log backend. The OTel `LoggerProvider` (`AddOpenTelemetry().WithLogging()`)
  is **not** registered alongside; double-export would duplicate every line.
  Per [ADR-0032 § Sub-decision 8](../decisions/0032-exception-handling-logging-and-observability.md).
- **OTel Collector** as the ingestion layer; forwards to backends.
- **Backends:**
  - Traces → Tempo / Grafana Cloud Tempo / Jaeger.
  - Metrics → Prometheus / Grafana Mimir.
  - Logs → Loki / Elastic / equivalent.
  - Errors → Sentry — accessed exclusively through `IErrorTrackingProvider`
    (per
    [ADR-0032 § Sub-decision 9](../decisions/0032-exception-handling-logging-and-observability.md));
    composition root selects the implementation by `DeploymentMode`:

    | `DeploymentMode` | `IErrorTrackingProvider` |
    |---|---|
    | `Development` | `NoOpErrorTracker` |
    | `SaaS` | `SentryErrorTracker` (DSN via `ISecretProvider`) |
    | `Dedicated` | `SentryErrorTracker` (per-tenant DSN allowed via Hub config) |
    | `SelfHostedOnline` | `SentryErrorTracker` (optional; `NoOpErrorTracker` if DSN absent) |
    | `SelfHostedAirGapped` | `LocalFileErrorTracker` (writes JSON to `/var/learnstack/errors/`) |

    Modules never import `Sentry.SentrySdk` directly — the architecture test
    `Modules_Do_Not_Reference_Sentry_SDK_Directly` enforces it.

## Correlation

Every request, job, event handler carries:

| Field | Source | Notes |
|-------|--------|-------|
| `trace_id` | OTel trace context | W3C `traceparent` propagated end to end |
| `span_id` | OTel | Per operation |
| `correlation_id` | Per request | Full W3C traceparent (`Activity.Current.Id`, `00-<trace>-<span>-<flags>`); embeds the trace id and is stable across retries. Surfaced on the Problem Details body + error-tracker captures so all three correlate |
| `tenant_id` | Resolved tenant | Always present where applicable |
| `organization_id` | Resolved organization | Present where the resource is `[OrganizationScoped]` (per [ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)); nullable for tenant-wide resources |
| `user_id` | Authenticated user | Present where applicable |
| `module` | Logical module | `education`, `classroom`, etc. |
| `request_path` | HTTP route template | Not the raw URL |
| `request_id` | Per HTTP request | Same as correlation id for HTTP requests |

These propagate into:
- HTTP server middleware
- HTTP clients (typed clients via `IHttpClientFactory`)
- MediatR pipeline behaviors
- Background jobs (Hangfire activator + filter) — payload carries `tenant_id`
  + `correlation_id`; activator restores `ITenantContext` before invocation
- Outbox dispatcher — row schema carries `tenant_id`, `organization_id?`,
  `correlation_id`, `event_id`, `occurred_at`, `type`; consumer handler
  restores `ITenantContext` from the envelope and starts an `Activity` with
  `traceparent` set to the row's `correlation_id`
- Integration event handlers
- Provider SDK calls (where the SDK exposes a hook)
- **Hub HTTPS contract surface (`/api/internal/*`)** — `HubCorrelationMiddleware`
  respects inbound `traceparent`; tenant context is read from the request
  envelope's `tenantId` field after HMAC verification. Outbound calls
  (LearnStack → Hub `POST /api/v1/internal/license/verify`, `POST
  /api/v1/usage/report`) inject the current `traceparent`. Per
  [ADR-0032 § Sub-decision 11](../decisions/0032-exception-handling-logging-and-observability.md).

The propagation primitive is **W3C `traceparent`** end to end. Every
cross-boundary write (outbox row, Hangfire payload, Hub envelope) carries it;
the receiving side resumes the trace by setting `Activity.ParentId =
traceparent`.

## Logging

### Format

Structured JSON, one event per line. Example:

```json
{
  "timestamp": "2026-05-14T12:34:56.789Z",
  "level": "Information",
  "message": "Course published",
  "trace_id": "01H...",
  "tenant_id": "...",
  "user_id": "...",
  "module": "education",
  "course_id": "...",
  "version_id": "...",
  "duration_ms": 87
}
```

### Levels

| Level | Use |
|-------|-----|
| `Trace` | Step-by-step internal debugging. Off in staging+. |
| `Debug` | Developer diagnostic. Off in production. |
| `Information` | Business-meaningful events (course published, enrollment created, session joined). |
| `Warning` | Recoverable abnormal condition (retry succeeded, fallback applied). |
| `Error` | Failed operation; user impact. |
| `Fatal` | Process cannot continue. |

### Rules

- Use **structured properties**, never string interpolation: `_logger.LogInformation("Course {CourseId} published", courseId)`.
- One log per business event; not per code path.
- Never log secrets, passwords, full tokens, raw payment data, raw HTTP `Authorization` headers, or full email bodies.
- Redaction filter strips configured fields before emission.
- Stack traces logged at `Error` and `Fatal` only.
- Avoid logging large objects; log identifiers and a sample summary instead.

### Sampling

- Production sampling: 100% of `Information`+ logs by default.
- High-volume events (classroom heartbeats) sampled at 1% with sample-id tagged.
- Errors always 100%.

## Tracing

### Spans

Auto-instrument:
- ASP.NET Core HTTP server.
- `HttpClient` outbound calls.
- Entity Framework Core queries.
- MediatR commands and queries.
- Hangfire job invocations.
- Outbox dispatcher batches.
- Valkey client calls.
- SeaweedFS/S3 SDK calls.
- LiveKit provider calls.

Manual spans:
- Use-case handlers wrap interesting subdivisions: `provider_create_room`, `token_issuance`, `event_dispatch`.
- Naming: `<module>.<operation>`: `education.publish_course`, `classroom.create_room`.

### Span Attributes

Required (auto-enriched by `TenantContextSpanProcessor`):
- `tenant.id`, `organization.id`, `user.id`, `module`, `correlation.id`.

The `TenantContextSpanProcessor : BaseProcessor<Activity>` is registered
once at the composition root (per
[ADR-0032 § Sub-decision 10](../decisions/0032-exception-handling-logging-and-observability.md));
its `OnStart` hook reads from the singleton `ITenantContextAccessor`
(AsyncLocal-backed) and tags every span — including spans produced by
auto-instrumentation libraries (EF Core, HttpClient, Valkey via Dapr,
SeaweedFS S3 SDK, LiveKit) — without per-call enrichment. The accessor is
populated at scope start by `TenantResolverMiddleware` (HTTP),
`HubCorrelationMiddleware` (`/api/internal/*`), Hangfire `JobActivator`
(background jobs), and the outbox / inbox handler scope (integration
events). Modules never call `Activity.Current?.SetTag("tenant.id", ...)`
themselves.

Common (set by the respective auto-instrumentation library):
- `http.method`, `http.route`, `http.status_code`.
- `db.system`, `db.operation`, `db.table`.
- `messaging.system`, `messaging.destination`, `messaging.message_id`.
- `provider.name`, `provider.operation`.

Forbidden attributes:
- Full URLs with query strings (truncate).
- Request bodies.
- Tokens or secrets.

### Error span semantics

Per
[ADR-0032 § Sub-decision 7](../decisions/0032-exception-handling-logging-and-observability.md)
(see also
[09-error-handling.md § Sentry vs OpenTelemetry — Error Capture Boundary](09-error-handling.md)),
the L1 `IExceptionHandler` calls `Activity.Current.RecordException` and
`SetStatus(Error, ...)` on an unhandled exception — with two exceptions of its
own, both listed in that table. A client disconnect leaves the span `Unset` and
records nothing, and a fault the **caller** committed — a provider's 4xx, or a
`BadHttpRequestException` carrying one — gets `SetStatus(Error)` without the
exception event, because attaching a stack trace to a normal 413 makes it look
like an incident. `Result.Fail` is **not**
an error span — the HTTP response is still a structured outcome (the Problem
Details with the correct 4xx status), so `SetStatus(Ok)` is the right value.
Putting `SetStatus(Error)` on every refused request would make the trace
backend treat business rejections as system failures.

### Sampling

- Tail-based sampling at the collector.
- 100% of error traces.
- 10% of non-error traces in production.
- 100% in staging.

## Metrics

### Required Metrics

| Metric | Type | Labels |
|--------|------|--------|
| `learnstack_http_request_duration_seconds` | histogram | route, method, status |
| `learnstack_http_request_total` | counter | route, method, status |
| `learnstack_db_query_duration_seconds` | histogram | module, operation |
| `learnstack_outbox_pending_count` | gauge | tenant |
| `learnstack_outbox_dispatch_duration_seconds` | histogram | event_type |
| `learnstack_outbox_dispatch_failed_total` | counter | event_type |
| `learnstack_job_duration_seconds` | histogram | job_name |
| `learnstack_job_failed_total` | counter | job_name |
| `learnstack_provider_request_duration_seconds` | histogram | provider, operation, outcome |
| `learnstack_classroom_active_sessions` | gauge | tenant |
| `learnstack_classroom_participants_active` | gauge | tenant |
| `learnstack_classroom_recording_minutes_total` | counter | tenant |
| `learnstack_search_query_duration_seconds` | histogram | tenant |
| `learnstack_cache_hit_total` | counter | cache_name |
| `learnstack_cache_miss_total` | counter | cache_name |

### Business Metrics

In addition to system metrics, business KPIs:
- Enrollments created (per tenant).
- Lessons completed (per tenant).
- Live sessions held (per tenant).
- Recording minutes consumed (per tenant) — directly funds cost dashboards.
- Bandwidth consumed by classroom (per tenant).

## Errors

- All `Error`+ events flow to `IErrorTrackingProvider` (Sentry in
  SaaS / Dedicated / SelfHostedOnline; `LocalFileErrorTracker` in
  air-gapped; `NoOpErrorTracker` in Development) with full context (trace id,
  tenant id, organization id, user id, request path).
- Provider events are tagged with `tenant_id` (and `organization_id` where
  applicable) to allow per-tenant inspection.
- PII redaction applied before the provider receives the event.
- Sentry release tags match the deployed git sha.
- The capture boundary — which exceptions go to the provider and which only
  to OTel — is in
  [09-error-handling.md § Sentry vs OpenTelemetry — Error Capture Boundary](09-error-handling.md).
- Modules never reference `Sentry.SentrySdk` directly. Architecture test
  `Modules_Do_Not_Reference_Sentry_SDK_Directly` enforces it.

## Frontend Observability

- Sentry on the client and on Next.js server.
- Web vitals sent via the Next.js reporting hook.
- Client errors include `correlation_id` from the last server request.
- Trace propagation: server-issued `traceparent` injected into the page; client follow-up fetches continue the trace.

## Dashboards

The first set of dashboards (Phase 11 deliverable):

1. **API health** — request rates, latency, error rates, slow queries.
2. **Background jobs** — queue depth, success/failure, retries, dead letters.
3. **Outbox** — pending count, dispatch latency, failure rate, by event type.
4. **Tenant activity** — DAU, enrollments, lessons completed, sessions held.
5. **Classroom usage & cost** — participant minutes, recording minutes, bandwidth, per tenant.
6. **Database** — connection count, slow queries, replication lag, vacuum/autovacuum stats.
7. **Cache** — hit ratio, eviction rate.
8. **Provider health** — payment, email, SMS, search, storage success rates.

## Alerting

| Alert | Threshold | Severity |
|-------|-----------|----------|
| API 5xx rate | > 1% for 5 min | page |
| API latency p95 | > 2× budget for 10 min | page |
| Outbox pending count | > 10k for 10 min | page |
| Background job failures | > 5% for 10 min | page |
| Classroom join failure | > 5% for 5 min | page |
| Disk usage (DB) | > 80% | warn |
| Disk usage (DB) | > 90% | page |
| Recording job failure | > 5% in 1 hour | warn |
| Provider failure | error rate > 5% in 5 min | warn |
| Cost (recording minutes) | tenant exceeds plan by > 50% | warn |

Alerts route via PagerDuty / Opsgenie; warn-level alerts go to Slack.

## Privacy

- No PII in metric labels (no email, no name).
- Trace attributes redact PII fields.
- Logs redact configured sensitive fields.
- Per-tenant access to dashboards is gated by tenant-admin role.

## Forbidden

- `Console.WriteLine` in production paths.
- Catching an exception only to log and ignore.
- Logging at `Information` inside tight loops.
- Adding cardinality-explosion labels (e.g. user id as a metric label).
- Custom log formatters that bypass redaction.
- Backend metric names without the `learnstack_` prefix.
- Registering the OpenTelemetry `LoggerProvider`
  (`AddOpenTelemetry().WithLogging()`) alongside the Serilog OTLP sink.
  Logs go through Serilog only; the OTel logger seam stays unused.
- Importing `Serilog.ILogger` from any module assembly. Modules use
  `ILogger<T>` from `Microsoft.Extensions.Logging`. Architecture test
  `Logging_Goes_Through_Microsoft_Extensions_Logging` enforces this.
- Per-call `Activity.Current?.SetTag("tenant.id", ...)` enrichment from
  module code. The `TenantContextSpanProcessor` does this centrally.
- Importing `Sentry.SentrySdk` from any module assembly. Use
  `IErrorTrackingProvider`.

The architecture tests that enforce the rules above are listed in
[21-architecture-tests-catalogue.md § Cross-cutting: error handling, logging, observability](21-architecture-tests-catalogue.md);
that catalogue is the canonical reference for every identifier.
