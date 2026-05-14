# 10 — Observability Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0006 — Events and Outbox](../decisions/0006-events-and-outbox.md) (outbox + tenant-context propagation across async boundaries).

Three signals — logs, traces, metrics — bound by a single correlation id. Everything we ship is observable from day one.

## Stack

- **OpenTelemetry** SDK for traces + metrics + (eventually) logs.
- **Serilog** for structured logs in .NET, exported via OTLP to the collector.
- **OTel Collector** as the ingestion layer; forwards to backends.
- **Backends:**
  - Traces → Tempo / Grafana Cloud Tempo / Jaeger.
  - Metrics → Prometheus / Grafana Mimir.
  - Logs → Loki / Elastic / equivalent.
  - Errors → Sentry.

## Correlation

Every request, job, event handler carries:

| Field | Source | Notes |
|-------|--------|-------|
| `trace_id` | OTel trace context | W3C `traceparent` propagated end to end |
| `span_id` | OTel | Per operation |
| `correlation_id` | Per request | Stable across retries; equals trace id at request boundary |
| `tenant_id` | Resolved tenant | Always present where applicable |
| `user_id` | Authenticated user | Present where applicable |
| `module` | Logical module | `education`, `classroom`, etc. |
| `request_path` | HTTP route template | Not the raw URL |
| `request_id` | Per HTTP request | Same as correlation id for HTTP requests |

These propagate into:
- HTTP server middleware
- HTTP clients (typed clients via `IHttpClientFactory`)
- MediatR pipeline behaviors
- Background jobs (Hangfire activator + filter)
- Outbox dispatcher
- Integration event handlers
- Provider SDK calls (where the SDK exposes a hook)

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
- Redis client calls.
- MinIO/S3 SDK calls.
- LiveKit provider calls.

Manual spans:
- Use-case handlers wrap interesting subdivisions: `provider_create_room`, `token_issuance`, `event_dispatch`.
- Naming: `<module>.<operation>`: `education.publish_course`, `classroom.create_room`.

### Span Attributes

Required:
- `tenant.id`, `user.id`, `module`, `correlation_id`.

Common:
- `http.method`, `http.route`, `http.status_code`.
- `db.system`, `db.operation`, `db.table`.
- `messaging.system`, `messaging.destination`, `messaging.message_id`.
- `provider.name`, `provider.operation`.

Forbidden attributes:
- Full URLs with query strings (truncate).
- Request bodies.
- Tokens or secrets.

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

- All `Error`+ events flow to Sentry with full context (trace id, tenant id, user id, request path).
- Sentry events tagged with `tenant_id` to allow per-tenant inspection.
- PII redaction applied before Sentry receives the event.
- Sentry release tags match the deployed git sha.

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
