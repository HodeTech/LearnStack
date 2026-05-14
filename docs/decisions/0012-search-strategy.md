# ADR 0012: Search Strategy

## Status

Accepted

## Decision

LearnStack uses **Meilisearch** as its search engine, with the following isolation model:

- **One Meilisearch instance per environment** (dev, staging, production).
- **Indexes named `<env>-<kind>-<locale>`** — e.g. `prod-course-en`, `prod-course-tr`, `prod-content-entry-tr`.
- **Tenant isolation is enforced as a query-time filter**, never as index-per-tenant. Every query includes `tenant_id = "{currentTenant}"` as its first filter, enforced by a single helper (`ITenantSearch`) that is the only sanctioned call path.
- **Locale isolation is enforced as an index split.** A search for `tr` content runs against the Turkish index only.
- **Indexing is event-driven via the outbox.** Direct writes from request handlers are forbidden; the indexer worker consumes integration events emitted by the owning module.

Architecture tests forbid direct Meilisearch client use outside the helper and forbid registering a search index without a `tenant_id` filterable attribute.

## Context

LearnStack needs full-text search across courses, content entries, media metadata, and lesson item titles. The first vertical (English Learning) ships with at least Turkish and English content; later verticals may add more locales.

Options considered:

1. **PostgreSQL `tsvector`** — same database, lower operational surface, but weak multilingual tokenisation (especially for Turkish) and limited support for typo tolerance, prefix queries, and faceted filters at the scale a course catalog needs. Acceptable as a fallback; not a primary plan.
2. **Meilisearch (selected)** — purpose-built; strong out-of-box ICU tokenisation per locale; typo tolerance; faceted search; HTTP API; self-hostable; Apache 2.0. Already in the team's stack from Phase 01.
3. **OpenSearch** — more powerful but operationally heavier (Java heap, cluster management). Defer until Meilisearch hits a capability ceiling.
4. **Managed providers (Algolia, Typesense Cloud)** — fast to ship; per-request and per-document pricing makes the cost trajectory non-linear; managed lock-in conflicts with the self-hosted preference.

### Why filter-by-tenant, not index-per-tenant

- 10,000+ tenants × 4 locales × 4 kinds → 160,000+ indexes. Meilisearch is not optimised for this cardinality.
- Per-tenant indexes mean per-tenant downtime during reindex.
- Platform-admin cross-tenant search (legal hold, abuse investigation) requires fan-out across all tenant indexes; with the filter-based model it is one query.
- The risk — a forgotten filter leaks one tenant's documents to another — is mitigated by architecture tests, a single sanctioned query helper, and integration tests that assert cross-tenant queries return zero hits.

### Why index-per-locale, not filter-by-locale

- ICU tokenisation is language-specific; a Turkish query against an English-tokenised index produces wrong results.
- Stop-word lists and stemming rules differ per locale.
- Cross-language false positives ("read" matching both English and Turkish documents) are common with shared indexes.
- The cost of one index per locale is bounded (LearnStack's locale list is small — currently `en`, `tr`).

## Consequences

- A `[Search.Indexable]` attribute (or equivalent) marks aggregates the indexer should track; the indexer subscribes to the corresponding integration events via the outbox.
- Every search document has a mandatory `tenant_id` field; the index registration declares `tenant_id` as a filterable attribute, asserted at startup.
- The `ITenantSearch` helper is the only allowed call site for tenant-scoped queries; platform-admin search uses `IPlatformSearch` which writes a `platform-admin` audit entry on every call.
- A nightly reconciliation job walks each kind's source-of-truth table per tenant and rebuilds drifted documents; drift is reported in dashboards.
- Reindex is a platform-admin operation; live deployments stream into a new index alias and swap atomically.
- Adding a new search kind requires a `[Search.Indexable]` registration **and** a migration adding the indexer's projection table. CI rejects a new kind without the tenant-filter test.
- Migration away from Meilisearch (e.g., to OpenSearch) is mechanical because the application talks to `ITenantSearch` / `IPlatformSearch`, not the SDK directly.

## References

- [20-search.md](../architecture/20-search.md) — implementation reference.
- [11-security.md](../standards/11-security.md) § OWASP A01 — broken access control.
- [18-audit-coverage.md](../standards/18-audit-coverage.md) — platform-admin cross-tenant search auditing.
- [15-event-and-outbox.md](../architecture/15-event-and-outbox.md) — outbox-driven indexing path.
