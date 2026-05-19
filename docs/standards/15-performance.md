# 15 — Performance Standards

**Status:** Active
**Derives from:** [ADR 0002 — Initial Architecture](../decisions/0002-initial-architecture.md) (initial budgets in [04-technical-architecture.md § Performance Budgets](../architecture/04-technical-architecture.md)), [ADR 0005 — Live Classroom Media Stack](../decisions/0005-live-classroom-media-stack.md) (classroom join + bandwidth budgets).

Performance budgets, the rules that keep them, and the test discipline that protects them.

## Initial Budgets

| Surface | Budget |
|---------|--------|
| Public landing page (TTFB) | < 200 ms server |
| Public landing page (LCP) | < 1.5 s on fast 4G |
| Course catalog server response | < 300 ms |
| Lesson player initial load | < 500 ms |
| API p95 (read) | < 200 ms |
| API p95 (write) | < 500 ms |
| API p99 (read) | < 500 ms |
| Classroom join time (token + connect) | < 1.5 s |
| Frontend INP (interaction-to-next-paint) | < 200 ms |
| Frontend CLS (cumulative layout shift) | < 0.05 |
| Cold start (worker, after restart) | < 5 s |

Budgets are reviewed quarterly against measured production metrics.

## Backend Rules

### Database Access

- Index every foreign key.
- Index `tenant_id` as the first column of composite indexes for tenant-owned tables.
- Avoid N+1 — projection (`Select(...)`) preferred over `Include` chains.
- Avoid `AsTracking` for read-only queries.
- Slow queries (> 500 ms) logged with `slow_query=true` and reviewed weekly.
- Each module's hottest read paths must have a covering index.

### Caching

- Read-through cache for stable, public, read-heavy data (published page render, course catalog list).
- Cache invalidation triggered by integration events from the producing module.
- Cache keys include `tenant_id` and `locale` where relevant.
- TTL chosen per content type; default 5 minutes for catalog, 1 minute for course detail.
- Cache hit ratio per cache name surfaced as a metric.

### Pagination

- All list endpoints paginated. Default `limit = 20`, max `limit = 100`.
- Cursor pagination by default; offset only for bounded admin lists.
- API rejects requests without explicit pagination on resource collections.

### Background Work

- Anything > 200 ms server time that does not need to block the response is moved to a Hangfire job.
- Job duration p95 monitored; jobs > 30 s have a long-running designation and a dedicated queue.
- Long-running jobs are checkpointable / resumable when possible.

### External Calls

- Every outbound provider call has a timeout (≤ 10 s for synchronous, ≤ 60 s for jobs).
- Retries with exponential backoff for transient failures.
- Circuit breaker pattern for provider outages.
- Outbound calls are not made inside DB transactions.

### Memory

- Avoid loading whole result sets when streaming would do.
- Stream large file uploads to SeaweedFS/S3; never buffer the whole file in memory.
- Avoid string concatenation in tight loops; use `StringBuilder` or pooled buffers.

## Frontend Rules

### Bundle Size

- Initial JS payload on a public route: < 200 KB gzipped.
- Studio routes may exceed this; budget reviewed per route.
- Lazy load below-the-fold blocks and modals.
- Code-split heavy client libraries (rich-text editor, video player) via `dynamic(...)`.

### Rendering

- Server Components by default.
- Streaming with `<Suspense>` to ship hero content first.
- Parallel data fetches in RSC via `Promise.all`.
- Avoid sequential request waterfalls.

### Images

- `next/image` everywhere.
- Explicit `width`/`height` to prevent layout shift.
- Use modern formats (AVIF, WebP) where the browser supports them.
- `priority` for above-the-fold hero images only.

### Fonts

- `next/font` self-hosted.
- Subset to the languages actually used.
- `font-display: swap`.

### Web Vitals

- Web Vitals collected via the Next.js reporting hook.
- Dashboards track LCP, INP, CLS, FCP per route.
- Regression on a critical route is a Sev-2 issue.

## Live Classroom

- Join time < 1.5 s p95.
- Token issuance < 200 ms.
- LiveKit SFU node sized per [12-infrastructure.md](12-infrastructure.md) — ~250 concurrent participants per 2 vCPU.
- Egress workers separate from the SFU node.
- TURN traffic monitored; > 30% sustained triggers a network review.

## Load Testing

Required load tests (Phase 11 deliverable):

- Public landing page at 1k RPS.
- Course catalog list at 500 RPS.
- Login + session at 100 RPS.
- Classroom join at 50 RPS.
- Recording start/stop at 10 RPS.

Tests run against staging weekly and before a major launch.

## Performance Reviews

- Quarterly performance review meeting.
- Top 10 slowest endpoints inspected.
- Top 10 most-expensive queries inspected.
- Bundle size deltas reviewed.
- Mobile Core Web Vitals reviewed.

## Forbidden

- Adding a route without a pagination cap.
- Adding a list endpoint without an index supporting it.
- Shipping a public route bundle > 250 KB gzipped without an ADR.
- Calling external providers inside a database transaction.
- Loading whole tables into memory ("just to filter in code").
- Building dashboards that query without `tenant_id` as the first WHERE column.
