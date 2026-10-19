# 10 — Observability Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md), [ADR 0006 — Events and Outbox](../decisions/0006-events-and-outbox.md) (outbox + tenant-context propagation across async boundaries), [ADR 0032 — Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md) (Sentry / OTel boundary, Serilog wiring, span attribute propagation, deployment-mode branching).
Producer additions derive from
[ADR-0057](../decisions/0057-transactional-event-production-and-completion.md).
Delivery/recovery additions derive from
[ADR-0046](../decisions/0046-durable-event-delivery-and-recovery.md).
Accepted job-lifecycle observation derives from
[ADR-0059](../decisions/0059-owned-hangfire-server-generations.md);
implementation remains P02b-4 Steps 4 and 9.
Tenant-authentication diagnostics derive from
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md);
P02b-5 Steps 1 and 3 implement the components; Step 4 activates the HTTP boundary.
Bounded assertion diagnostics and process-wide outbound metric projection derive
from [ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md); its
2026-10-20 acceptance precedes dependent audit-budget implementation.

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
- Background jobs — trusted submission stores tenant/org and W3C correlation on
  the authoritative LearnStack row; `JobExecutor` restores that origin before
  business invocation. Hangfire wakeups carry opaque references only
  ([ADR-0047](../decisions/0047-transactional-background-job-runtime.md#ordinary-execution-authority))
- Outbox dispatcher — row schema carries `tenant_id`, `organization_id?`,
  `correlation_id`, `id` (the event's `EventId`), `occurred_at`, `type`; the consumer
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

Accepted [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md)
requires the producer execution boundary to supply a W3C Activity even without a
telemetry listener, including the Seeder. Enqueue persists the full traceparent;
transaction announcement supplies tenant/organization while trusted execution
supplies correlation, causation and optional causal actor. Domain translation
allocates a new integration EventId and retains the domain EventId as causation.
Delivery-originated production uses the trusted incoming event identity,
with effective `UserId.SystemActor` and causal human retained separately. No
caller-supplied header establishes causation; JSON `metadata` stays SQL NULL.
P02b-1 implements this producer metadata boundary in both API and Seeder.
P02b-3 Step 3 implements restoration and incoming-event causation in the common
delivery owner; Step 5 activates both real defaults and semantic Seeder handoff.

The propagation primitive is **W3C `traceparent`** end to end. Every
cross-boundary write (outbox row, authoritative LearnStack job row, Hub envelope)
carries it; Hangfire wakeups carry only opaque references, not trace metadata.
The receiving side resumes the trace by setting `Activity.ParentId =
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

### Tenant-authentication diagnostics

**Accepted contract — 2026-10-19; HTTP activation in P02b-5 Step 4.**
[ADR-0064](../decisions/0064-tenant-api-authentication-boundary.md) extends the
existing rejection recorder; it introduces no second detector or audit slug.

- The closed `TenantAssertionSource` maps only to `header` or `jwt-claim`.
  Recorder metadata/counters consume that source. Arbitrary claims, opaque
  subjects, actors and correlation IDs never become metric labels.
- Rejection records capture typed actor, request correlation and trusted
  organization explicitly. A host-backed JWT rejection is recorded under the
  host tenant, with `OrganizationId=null` and trusted organization in metadata;
  failed resolution does not install a tenant context for attribution.
- Row 7 gains its required single rejection audit; row 10 stays unaudited.
  Row 14 uses synchronous `RecordNoTenantRefusal`: one existing
  `learnstack_tenant_assertion_unresolved_total` increment with only
  `source=jwt-claim`, `dimension=Tenant`, and fixed `membership_unconfirmed`
  diagnostic. Its decorator performs no audit-store I/O and writes zero rows;
  no claimed tenant or sentinel supplies an audit target. The glossary names this
  [tenantless membership refusal](../glossary.md#multi-tenancy).
- A validated profile with absent tenant claim emits one fixed
  `tenant_claim_absent` **Debug log event** per admitted request after subject
  admission. It has no claim value or labels, creates no metric or error/message
  key, and preserves public 200/protected 404 and the actor's attribution.
- Resolution short-circuits at its first disagreement, before header assertions.
  Separate authenticated requests remain distinct diagnostic occurrences.
  Subject-quota refusal precedes durable mismatch recording; admitted audit
  failure preserves the refusal and existing Critical/health behavior.
- [ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md) admits 600
  authenticated audit attempts per independently defended host tenant per API
  instance in a first-request-anchored monotonic minute. A singleton
  `TenantAssertionAuditBudget` shares allowance across scoped recorders, actors,
  sources and dimensions. Failed writes spend permits. Later occurrences make
  no audit-store call and retain their refusal. There is no tenant configuration
  override or independent tenant-registry cap; suppressed calls do not extend
  expiry. Anonymous bursts and tenantless recording retain their contracts.
- Every mismatch increments
  `learnstack_tenant_assertion_mismatch_total{dimension,source,authenticated}`;
  tenant UUIDs are absent. Every suppressed occurrence also increments
  `learnstack_tenant_assertion_audit_suppressed_total{dimension,source,reason}`,
  with only `reason=audit_budget_exhausted`. Actor/subject/correlation values are
  absent from all metric labels. A positive counter delta directly counts skipped
  durable attempts, without joining logs to rows; Phase 11 owns production alert
  routing and thresholds.
- Emit exactly one Warning occurrence event: normal 4001, or suppressed 4006
  `AuditBudgetExhausted`, retaining typed tenant/actor/correlation properties.
  A suppressed occurrence emits no additional 4001. Its reason belongs to logs
  and telemetry, never `AuditEntryDraft.Reason` or Problem Details. Aggregate
  real-time log volume remains unbounded; an attacker can exhaust the durable
  tenant allowance and later actors have no durable row during that window.
  Counters/warnings do not restore complete durable actor evidence.

The Step 1 vendor boundary suppresses the exact category
`Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler` at every level
before any production sink. This includes stock failure events 1/3 and inherited
event 7. Emit independent LearnStack closed stage/reason outcomes without vendor
exceptions, their message/inner/data, token, subject or issuer/audience text.
IdentityModel PII/security-artifact output and backchannel diagnostics follow
the same boundary. Actual Serilog-to-OTLP/error-capture and collector canary
controls must prove both absence of raw failures and presence of closed outcomes;
a Warning threshold or response-only setting is insufficient. Unrelated logging
and sanctioned typed UUID/correlation recorder attribution remain observable.

Metadata HTTP spans are suppressed. Native `System.Net.Http` and
`System.Net.NameResolution` metrics do not honor request-local suppression.
[ADR-0065](../decisions/0065-bounded-tenant-assertion-diagnostics.md) ratifies the
API export projection shipped in `c6108bf9`: HTTP retains only
`http.request.method`, `http.response.status_code`, `network.protocol.version`,
`url.scheme` and `error.type`; DNS retains only `error.type`. Counts and durations
remain observable. Hostname/address/port, DNS question, full URL, path and query
are omitted from metrics for every outbound request, including ordinary Hub,
payment and media dependencies. Destination attribution is unavailable there.
Ordinary HTTP traces and application logs remain observable. Phase 11 qualifies
a finite source-declared dependency identity before its first consumer.

`TenantAuthenticationDiagnosticsTests.Metrics` proves real HTTP/DNS duration and
bounded-tag positives. Metadata hostname/address canaries must be absent from
all exported channels. A separate ordinary HTTP request with a distinct IP/port
and path/query canaries must omit those values from metrics while preserving its
ordinary trace positive; ordinary destinations are not forbidden in all payloads.
The actual singleton-budget suppression path is also exported through the root's
`LearnStack.Api` meter subscription. Its test waits for the correlated Warning
while the logger owner is alive: a metrics flush is not a log-delivery receipt.
The new ordinary-destination controls remain pending with ADR-0065's implementation.

Step 3 implements recorder/claimless/quota diagnostics; Steps 4–5 qualify the
complete active path. Acceptance alone claims none of these mechanisms delivered.

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
- Outbox dispatcher attempts (P02b-2 Step 3; no batch span).
- Subscriber execution/terminal attempts (P02b-3 Step 4; no batch span).
- Valkey client calls.
- SeaweedFS/S3 SDK calls.
- LiveKit provider calls.

Manual spans:
- Use-case handlers wrap interesting subdivisions: `provider_create_room`, `token_issuance`, `event_dispatch`.
- Naming: `<module>.<operation>`: `education.publish_course`, `classroom.create_room`.

Background-job stages use the required observation owner's `learnstack.jobs`
source. Admission, business execution, external execute/reconcile, external read,
checkpoint, capacity cleanup, recipient submission and terminalization retain
their finite `job.*` names. The factory returns an unstarted Activity; the actual
stage owns it before `Start` and through existing cleanup. An absent/refusing
sampler preserves W3C correlation through an unstarted fallback. A catchable
sampler fault also marks observation unhealthy; it does not retry notification
or replace an Activity after `Start`.

Validated envelopes supply declared parents. A recovery read before the app-role
envelope acknowledgement, recipient submission before a child exists, and the
payload-free terminal lane start independent roots. Recipient startup clears the
caller's tenant context before `Start`, then installs its route context using the
actual span ID. Its root has no invented tenant attribution; submission spans
inside that context receive normal enrichment. External provider and terminal
spans acquire no tenant authority for tracing. Activity restoration and tenant
restoration are attempted independently through arbitrary observer faults.
Step 9's delivery record owns qualification; named tracing alone does not close
the packet's execution, observation or lifecycle obligations.

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
`HubCorrelationMiddleware` (`/api/internal/*`), `JobExecutor` from its validated
job row (background jobs), and the integration-event delivery owner. Modules never call `Activity.Current?.SetTag("tenant.id", ...)`
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
| `learnstack_outbox_pending_count` | gauge | — |
| `learnstack_outbox_dispatch_duration_seconds` | histogram | event_type |
| `learnstack_outbox_dispatch_failed_total` | counter | event_type |
| `learnstack_outbox_deadletter_total` | counter | event_type |
| `learnstack_outbox_lease_lost_total` | counter | — |
| `learnstack_outbox_oldest_eligible_age_seconds` | gauge | — |
| `learnstack_outbox_deferred_count` | gauge | — |
| `learnstack_outbox_heartbeat_age_seconds` | gauge | — |
| `learnstack_delivery_execute_duration_seconds` | histogram | event_type, subscription |
| `learnstack_delivery_failed_total` | counter | event_type, subscription |
| `learnstack_delivery_pending_count` | gauge | — |
| `learnstack_delivery_oldest_eligible_age_seconds` | gauge | — |
| `learnstack_delivery_deferred_count` | gauge | — |
| `learnstack_delivery_lease_lost_total` | counter | — |
| `learnstack_delivery_heartbeat_age_seconds` | gauge | — |
| `learnstack_inbox_dedup_total` | counter | event_type, subscription |
| `learnstack_inbox_deadletter_total` | counter | event_type, subscription |
| `learnstack_job_duration_seconds` | histogram | job_name, profile, stage, outcome, cleanup |
| `learnstack_job_failed_total` | counter | job_name, profile, stage, reason |
| `learnstack_job_unknown_outcome_total` | counter | job_name, profile, stage, reason |
| `learnstack_job_state_count` | observable gauge | kind, pool, profile, capacity_group, recurrence, artifact, reason |
| `learnstack_job_oldest_eligible_age_seconds` | observable gauge | profile |
| `learnstack_job_heartbeat_age_seconds` | observable gauge | component, pool |
| `learnstack_job_component_state` | observable gauge | component, pool, state, reason |
| `learnstack_job_control_total` | observable counter | operation, pool |
| `learnstack_provider_request_duration_seconds` | histogram | provider, operation, outcome |
| `learnstack_classroom_active_sessions` | gauge | tenant |
| `learnstack_classroom_participants_active` | gauge | tenant |
| `learnstack_classroom_recording_minutes_total` | counter | tenant |
| `learnstack_search_query_duration_seconds` | histogram | tenant |
| `learnstack_cache_hit_total` | counter | `cache.name` |
| `learnstack_cache_miss_total` | counter | `cache.name` |
| `learnstack_cache_store_total` | counter | `cache.name` |
| `learnstack_cache_coalesced_total` | counter | `cache.name` |
| `learnstack_cache_eviction_total` | counter | `cache.name`, `reason` |
| `learnstack_cache_factory_duration_seconds` | histogram | `cache.name`, `outcome` |
| `learnstack_audit_standalone_write_failures_total` | counter | `operation` |
| `learnstack_audit_standalone_duplicates_total` | counter | `operation` |
| `learnstack_entitlement_source_total` | counter | `source` |
| `learnstack_entitlement_unresolved_total` | counter | — |

Outbox metrics above are implemented in P02b-2 Step 3 under ADR-0046;
both independent review rounds are complete. The subscriber/inbox inventory is
accepted by the [P02b-3 package](../roadmap/phase-02b-events-auth.md#p02b-3-decision-package-2026-10-13);
Step 4 implements it with real worker, finite-label and memory-only observation
proofs. P02b-4 Step 9 implements the five observable and three stage families above under
[ADR-0047](../decisions/0047-transactional-background-job-runtime.md#lifecycle-and-observation).
Stage, tracing, lease-loss and control-path instrumentation are implemented with
source-specific qualification; the
[Step 9 record](../roadmap/phase-02b-events-auth.md#p02b-4-step-9--enforcement-and-packet-closeout-2026-10-18)
owns those receipts and full-regression, review, packet-closeout and remaining
PR/CI qualification status.
`event_type` comes only from the finite declared registry plus fixed
unknown/retired/invalid categories. `subscription` comes only from declared stable
subscription IDs plus fixed `invalid`; a well-formed undeclared ID is still
`invalid`. Never label event metrics by tenant, event, partition or trace identity.
Subscriber failure, deduplication and terminal counters are distinct from producer
publication counters; one subscriber's failure is not a producer failure.
Counters describe observed attempts and fenced transition refusals, not a durable
accounting ledger. An unknown COMMIT can already have completed business work;
its refused later fence does not prove that business work failed or should repeat.

Pending count includes all unprocessed, non-dead-lettered rows, including delayed,
leased, terminal-requested and manifest-deferred work. Oldest eligible age uses
the exact normal claim predicate, excluding future, exhausted, terminal-requested,
live/unrecovered-expired leases and manifest/local partition holds. It is zero
when no row qualifies and never negative. Deferred count isolates manifest holds.
Heartbeat advances after actual bounded loop work, including empty polls.

Gauges read immutable memory snapshots and perform no SQL. Refresh follows
completed iterations, targets 5 s idle and becomes stale at the approved 45 s
health bound. Failure retains the prior snapshot and reports unhealthy. The
[P02b-2 bounds](../roadmap/phase-02b-events-auth.md#runtime-bounds-and-observation)
own deadlines and task limits. Dispatcher-owned logs/spans use validated
correlation and bounded outcomes, without exception text/stacks, payload, raw
stored type or connection data. P02b-3 Steps 3–4 minimize transport-owned
execution/audit diagnostics before activating production consumers. Original
causal metadata is restored only in the app-role executor; the bypass scheduler
emits a fresh validated attempt trace without reading payload or causal-envelope
columns. No raw exception details, stored type/subscription text or payload enters
its diagnostics.

Job observation binds one required concrete singleton to the actual API runtime
after its snapshot owners initialize and before either source pin. Shared
API/Seeder registration constructs no runtime, worker or source. Collection
captures one immutable runtime snapshot per callback; it performs no SQL, DI
resolution, arbitrary callback invocation or scheduling. Unbound or unobserved
durable state emits no healthy empty-queue sample. A complete empty sample emits
explicit finite zero cells. Refresh faults preserve prior values and expose the
failure and conservative freshness age separately.

Job pending counts include every unresolved non-dead-lettered job. Queue age is
the stored SQL statement-time sample for exact normal eligibility; it is not
extrapolated from the earlier acquisition/freshness timestamp. Missing confirmed
metadata heartbeats emit `not_observed` state without a fabricated zero age;
confirmed monotonic zero is valid. Pool/group/recurrence labels use frozen
registries, while profiles, artifact categories, reasons and operations are fixed.
No tenant/org/job/attempt/operation/trace identity or payload is a job metric tag.
Aggregate retained work uses `kind=retained_total`; per-pool work uses `retained`,
so a legitimate pool named `none` cannot alias the aggregate series.

Job stage duration measures through actual executor-owned scope, Activity,
context, authority, source, auxiliary and owned registration cleanup. It excludes
the outer native invocation's later private-token/owner cleanup and never ends
at a caller's finite-wait timeout. The interval ends before synchronous metric
notification; a held notification still retains the actual task.
`cleanup=retained` preserves failed cleanup;
it cannot reverse a physical COMMIT acknowledgement. Confirmed stage failure and
uncertain write/effect counters are separate; neither counts unique failed jobs.
A successfully acknowledged Finalize remains successful even when it applies a
selected failed provider outcome. An inferred clean no-COMMIT path is not a
physical rollback acknowledgement.

An explicit provider Failed report can increment stage failure before its durable
outcome checkpoint; the report grants no release or retry authority. Running is
pending. Unknown is an uncertain effect; a provider fault counts that uncertainty
only after the typed bridge actually admits the provider call. Scope/adapter
construction faults before that point remain unissued faults. Capacity cleanup
without an acknowledgement or uncertain COMMIT reports neutral `NotCommitted`;
its result does not distinguish refusal from a failed write. Job names use the
frozen definition registry plus fixed `unknown`/`retired`, and every other stage
dimension is closed. Catchable synchronous metric-listener faults mark observation
unhealthy without changing the completed result or retrying notification. A
blocked listener stays part of the actual owned task.

Job control totals keep a fixed label per monotonic source: generation creation,
shared maintenance invocations, bridge ACK/failure/deferral, renewal ACK and
separate locked-history renewal confirmation,
confirmed retry/terminal transitions and uncertainty observations. A confirmed
terminal total can include an exact locked-history receipt after lost ACK;
it does not assert the original physical acknowledgement. These process
observations are distinct from durable accounting and from business failures.
Retry confirmation requires the original transition's expected next generation;
same-generation routing repair and changed ownership do not count. Physical ACK
accounting precedes cleanup refusal without granting execution or cleanup
authority. The fixed counter recorders invoke no listener and perform no SQL.

Recurrence progress uses six fixed control-total operations: original acknowledged
occurrences, skipped slots, recipient pages, raw routes examined, recipients
inserted and completed fanouts. A completed fanout is its durable cursor marker,
not completed child execution. Empty completed pages count; ineligible raw routes
contribute to examined routes without fabricating recipients. Only an original
physical COMMIT acknowledgement plus its typed progress result increments these
process totals, before cleanup qualification. A returned value or later durable
history cannot backfill a lost ACK. Native ticks and repeated collection add no
progress. All six use `pool=none` without recurrence/tenant/identity labels.

Bridge repair totals are disjoint subsets of visible acknowledged handoffs.
`bridge_repair_prior_ack_hint_acknowledged` uses a prior acknowledged hint;
`bridge_repair_expired_token_acknowledged` uses an expired prior claim without
that hint. Classification reads the locked candidate before replacing its token;
hint takes precedence. An expired token proves acquisition, not native execution.
Unmarked work is not necessarily first-ever work because deferral clears tokens.
Only actual native exit followed by a true fenced ACK return adds a repair.
The existing bool status API can hide a committed ACK behind failed cleanup;
these process totals conservatively omit it, retain ownership and never backfill
it from current row state. Deployment-mismatch deferral counts only a true
fenced mutation. Operational bridge failures remain distinct from job failures.

Lease-loss totals preserve the first observed cause per acknowledged execution:
`lease_loss_renewal_refused` or `lease_loss_elapsed`, both with `pool=none`.
Only a validated start publication or clean acknowledged recovery receipt enables
observation. Elapsed means the actual local lease clock has reached its bound;
it is not inferred from a failed timer update, a refused recovery proof or an
overall stage deadline. Already-visible stop, settled work, join, failed cleanup
or permanent renewal closure suppress a new observation. This gate read and the
cause CAS do not establish a universal atomic ordering with concurrent host stop.
The first-cause cell prevents relabelling; persistent parent totals survive actual
execution removal. Recording performs only fixed-memory atomics and grants no
renewal, execution or cleanup authority.

Late settlement uses seventeen literal `late_<stage>_settled` control operations
for the existing finite invocation-stage set, all with `pool=none`. Only actual
stage-clock closure records the effective stage. An elapsed predecessor keeps its
name; a prospective shorter rearm that has not accepted its stage stays unnamed.
Host stop, lease loss, timer faults and wrapper return do not create this cause.

Record once after the actual published framework flight and its positive cleanup,
execution joins, stage-timer/wake joins and private cancellation cleanup finish.
Non-framework stages additionally require their exact concrete typed task and
positive cleanup; missing tasks, wrong checkpoint/business kinds and older probes
cannot substitute. History and NoPlan flights include their enclosing loop, not
only an intermediate SQL result. The current retry/NoPlan confirmation takes
precedence over an older acquisition probe. Unresolved status work remains under
its existing Execute/Reconcile wait; observation adds no stage rearm.

These process totals survive owner removal and repeat collection. Settlement is
local flight/cleanup completion, not successful business work, provider quiescence,
physical ACK, rollback or a unique-job count. A clean refusal/unknown report can
settle its local flight. A permanently retained cleanup cannot. A clean parent
status result that issued no SQL supplies no durable-ACK claim. Existing stage
duration/outcome measurements retain their earlier executor-owned boundary.

Cache `cache.name` is a governed, low-cardinality family from the Standards 20
inventory (`hub:host-map`, `hub:entitlement`, `identity:permissions`,
`tenancy:feature-flags`, `tenancy:settings`, `audit:config`, or
`tenancy:killswitch`, `customization:content-types`, or `customization:taxonomies`).
The Customization families are Accepted by P02d-3's G22 pass under ADR-0040/0043;
their adapter mapping is implemented in Step 3. An unregistered family is
reported as `other`; adapters never derive a label from a full cache key, tenant or
organization id, host, session id, or entity id. `reason` is one of `explicit`, `expired`,
or `capacity`; `outcome` is one of `success`, `faulted`, or `cancelled`.

The two audit counters are labelled by `operation` — the catalogue's dotted slug, a
bounded set nobody outside the deployment chooses — and never by tenant. They answer
different questions and both are required by
[ADR-0033 Amendment 3](../decisions/0033-audit-durability-model.md): a **write failure**
is an operation that succeeded with no record of it, which also takes the `audit` health
check unhealthy and logs at `Critical`; a **duplicate** is the opposite signal, positive
evidence that a business `COMMIT` landed whose outcome the process could not observe, so a
rate that moves is about the database connection rather than about any one request.

The two entitlement counters belong to the Hub-backed provider's read path
([Hybrid License Model § Failure policy by key class](../architecture/26-hybrid-license-model.md))
and land with `HubEntitlementProvider` in
[Phase 02c](../roadmap/phase-02c-hub-foundation.md): `NullEntitlementProvider`, the one
registered today, has no cache, durable row or Hub to report on. `source` is one of
`cache`, `durable`, `hub` or `floor`. `learnstack_entitlement_unresolved_total` carries
**no** label: the answer it counts — no cache, no durable row, no Hub — is a platform
condition that reaches every cold tenant at once, so the tenant goes in the `Error` log
line and the span, and a per-tenant label would add one series for every tenant an outage
touched.

### Health checks

| Check | Registered as | Unhealthy when |
|---|---|---|
| Audit write path | `audit` | The most recent MUST-class standalone audit write failed and no later one has succeeded |
| Outbox dispatcher (P02b-2 Step 3) | `outbox` | No actual progress or observation refresh for 45 s, any retained callback overrun, or terminal-audit failure |
| Subscriber dispatcher (P02b-3 Step 4) | `delivery` | No actual progress or observation refresh for 45 s, any retained callback/cleanup overrun, or terminal-audit failure |

The `audit` check is registered in Phase 02a Packet 9; P02b-2 Step 3 registers
`outbox`, with both independent review rounds complete. P02b-3 Step 4 adds
`delivery`; its delivery record owns current validation and review status.
All three are **not mapped**: `/healthz` stays
a liveness probe, because a process that cannot write audit rows is still worth leaving
alive. The readiness surface that reads the check, and the deployment-level backstop that
stops serving past a configured unhealthy window, are demand-gated to
[Phase 11](../roadmap/phase-11-production-hardening.md) —
[ADR-0033 Amendment 3](../decisions/0033-audit-durability-model.md) states all four of
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s requirements for it.

**Job-lifecycle observation.** The unmapped `jobs` health check implements
ADR-0059's generation draining/retention, actual running work, last confirmed
server-metadata heartbeat and independent shared-component fault observations.
Durable transactional jobs parked for unproved capacity cleanup make `jobs`
unhealthy while retaining their reservations; only positive cleanup/recovery
clears that condition. Elapsed time never releases occupancy.

Pool labels come only from the finite registry, with fixed state/reason codes.
Interception or elapsed caller waiting cannot restore health, and a missing server
row proves neither business completion nor external quiescence. Closed vendor
callbacks record fixed memory state only; provider exceptions, SQL and tenant/job/
actor/trace/parameter values stay out. The
[Step 9 record](../roadmap/phase-02b-events-auth.md#p02b-4-step-9--enforcement-and-packet-closeout-2026-10-18)
owns delivered instruments and censuses; the
[phase status](../roadmap/phase-02b-events-auth.md) owns current qualification.
Phase 11 retains readiness routing, alert thresholds and process supervision.

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

**Accepted P02d-5 scope — 2026-10-08.** G35/ADR-0053 requires valid W3C
`traceparent` propagation from the configured server caller to the API; Step 3
delivers it. Step 4 preserves one request-local trace across middleware bootstrap
and subsequent rendering, including missing/malformed incoming context. Real API
observations verify agreement. Next error capture, web-vitals and browser trace
injection belong to Phase 11's frontend observability work before production
rollout. No Sentry client or web-vitals hook is claimed by P5.

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
| Outbox oldest eligible age / heartbeat | > 45 s; validate production window in Phase 11 | page |
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
