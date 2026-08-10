# Architecture Decision Records

This directory contains LearnStack ADRs. Each ADR captures a one-time decision with its context, the decision, and the consequences.

## Status Values

- **Proposed** — Drafted, not yet accepted.
- **Accepted** — Active. New code should comply.
- **Superseded** — Replaced by a newer ADR. Kept as a redirect.
- **Deprecated** — No longer applies. Kept for history.

Accepted ADRs are not rewritten. A new decision is a new ADR, possibly superseding the old one.

## Active ADRs

| # | Title | Topic |
|---|---|---|
| 0001 | [Platform Name](0001-platform-name.md) | Name and naming conventions |
| 0002 | [Initial Architecture](0002-initial-architecture.md) | .NET 10 + EF Core + PostgreSQL + Valkey + SeaweedFS + Next.js; modular monolith |
| 0003 | [Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md) | Query filters + RLS + audit + architecture tests (Amendment 1: Organization scope, 2026-05-18; Amendment 2: identity row terminology, 2026-05-19; **Amendment 3: corrected RLS policy template + database role model, 2026-08-08**) |
| 0004 | [Authentication Strategy](0004-authentication-strategy.md) | Off-the-shelf identity provider preferred over hand-rolled auth (Amendment 1: `learnstack-hub` realm, 2026-05-18) |
| 0005 | [Live Classroom Media Stack](0005-live-classroom-media-stack.md) | LiveKit OSS self-hosted by default; LiveKit Cloud optional; no custom SFU |
| 0006 | [Events and Outbox](0006-events-and-outbox.md) | Domain + integration events; outbox pattern; idempotent handlers (Amendment 1: Dapr pub/sub dispatch transport, 2026-05-18) |
| 0007 | [Documentation Language and Conventions](0007-documentation-language-and-conventions.md) | English; Mermaid; ADR + standards split |
| 0008 | [Localization Schema](0008-localization-schema.md) | Side translation tables vs JSONB localized fields |
| 0009 | [Frontend — Single Next.js App First](0009-frontend-single-app-first.md) | One Next.js app with route segments; multi-app split deferred |
| 0010 | [Cross-Module Communication](0010-cross-module-communication.md) | Four sanctioned mechanisms, no fifth (Amendment 1: Dapr pub/sub backend, 2026-05-18) |
| 0011 | _Superseded — see below_ | Was: Vertical Extension Points |
| 0012 | [Search Strategy](0012-search-strategy.md) | Meilisearch; one instance per env; index-per-(kind, locale); tenant_id as query filter |
| 0013 | [Page Block Schema Versioning](0013-page-block-schema-versioning.md) | `(key, schemaVersion)` tuple; immutable schemas; lazy + bulk migration; placeholder on unknown version |
| 0014 | [Adopt Dapr](0014-adopt-dapr.md) | Dapr building blocks for pub/sub (Kafka), state (Valkey), secrets (Vault); abstracted behind SharedKernel interfaces |
| 0015 | [API Gateway with APISIX](0015-api-gateway-apisix.md) | APISIX standalone mode; JWT + rate limit + CORS + correlation-id at the edge; defense-in-depth |
| 0016 | [Audit Log Subsystem](0016-audit-log-subsystem.md) | **Superseded by [ADR-0033](0033-audit-durability-model.md).** `LearnStack.Modules.Audit`; EF interceptor + `IAuditStateCapture` + `AuditLogBehavior`; partitioned `audit_log` table; retention. Read for context; ADR-0033 carries the binding durability rules |
| 0017 | [Tenant + Organization Hierarchy](0017-tenant-organization-hierarchy.md) | Two-level: Tenant → Organization; permission scope Platform / Tenant / Organization (Amendment 1: identity row terminology, 2026-05-19; **Amendment 2: the `Organization` aggregate is declared in `LearnStack.Modules.Tenancy.Domain`, 2026-08-10**) |
| 0018 | [Tenant-Driven Customization Model](0018-tenant-driven-customization-model.md) | Generic-only core; tenants define content types, page blocks, scoring rules as **data**, not code (supersedes ADR-0011; Amendment 2026-08-08 draws the genericity boundary — stateful entitlement and external capability invocation are platform features, not customization) |
| 0019 | [LearnStack Hub](0019-learnstack-hub.md) | Separate codebase `learnstack-hub`; tenant lifecycle, plans, entitlements, billing, custom domain admin; mTLS internal API |
| 0020 | [Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md) | SaaS / Dedicated / Self-Hosted from one codebase; phone-home + RSA-signed key + 30-day grace |
| 0021 | [Feature-Based Entitlement](0021-feature-based-entitlement.md) | Feature flags + numeric limits per plan; typed `FeatureKeys` / `LimitKeys` registries |
| 0022 | [Custom Domain & TLS](0022-custom-domain-tls.md) | Hub-owned custom domain admin; DNS-01 + HTTP-01 + Let's Encrypt; APISIX hot-reload |
| 0023 | [Strongly-Typed ID Source Generator — Vogen](0023-strongly-typed-id-source-generator.md) | Vogen as the source generator for both IDs and value objects; `[ValueObject<Guid>]` annotation; EF + JSON + ASP.NET + OpenAPI emitters out of the box |
| 0024 | [API Versioning Policy](0024-api-versioning-policy.md) | URL-based `/v{N}/`; 6-month deprecation window; RFC 8594 `Sunset` + `Deprecation` headers; OpenAPI `deprecated` + `x-sunset` extensions; 410 Gone with RFC 7807 on sunset |
| 0028 | [`audit_log` Partition Management — Hangfire Recurring Job](0028-audit-log-partition-management.md) | Daily `learnstack:audit:partition-management` Hangfire job; create-ahead 2 months; drop only on platform-max retention horizon; row-level purge separate; no `pg_partman` dependency |
| 0029 | [Object Storage — SeaweedFS](0029-object-storage-seaweedfs.md) | Self-hosted SeaweedFS behind the existing `IStorageProvider` S3 contract; partially supersedes ADR-0002's MinIO row |
| 0030 | [Redis-compatible Store — Valkey](0030-redis-compatible-store-valkey.md) | Valkey (Linux Foundation, BSD-3-Clause) for the cache + Dapr state-store backend; RESP-protocol drop-in; partially supersedes ADR-0002's Redis row |
| 0031 | [PostgreSQL — Start on 18.x](0031-postgresql-major-version.md) | Pin primary RDBMS major version to PostgreSQL 18; native `gen_uuid_v7()` + async I/O + longest LTS runway; partially supersedes ADR-0002's PostgreSQL row |
| 0032 | [Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md) | `IExceptionHandler` + 8-step MediatR pipeline + `Result.Fail`-only validation + `DomainException`-is-bug discipline + `IProviderResilience<TPort>` (Polly v8) + Sentry vs OTel error capture boundary + Serilog primary + `TenantContextSpanProcessor` + `IErrorTrackingProvider` deployment-mode branching |
| 0033 | [Audit Durability Model](0033-audit-durability-model.md) | **Supersedes ADR-0016.** MUST-class audit written as durable intent inside the business transaction (fails closed); SHOULD/MAY-class stays best-effort; corrected partitioned `audit_log` primary key; `AuditConfig` cannot remove baseline MUST coverage |
| 0034 | [Hub Contract Surface Invariant](0034-hub-contract-surface-invariant.md) | Replaces "closed at four endpoints" with two enforceable invariants (Hub stores no tenant content; every crossing goes through a named adapter); enumerates the real endpoint set; TLS key material leaves the entitlement payload; host resolution never calls the Hub |
| 0035 | [Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md) | The one-way-door test; ports + default implementations ship now, vendor adapters ship on a named trigger in a named phase; uncontracted deployment modes may not decide technical choices |

## Superseded ADRs

- **ADR-0011 — Vertical Extension Points** — superseded by [ADR-0018: Tenant-Driven
  Customization Model](0018-tenant-driven-customization-model.md) on 2026-05-18. The
  original file is retained in place (`0011-extension-points.md`) with a Superseded
  status banner and a "why superseded" section linking forward. Never implemented; no
  migration needed.
- **ADR-0016 — Audit Log Subsystem** — superseded by [ADR-0033: Audit Durability
  Model](0033-audit-durability-model.md) on 2026-08-08. The subsystem design survives
  intact; the **durability contract** changed. ADR-0016 applied "audit never blocks
  business logic" uniformly, which contradicted
  [Audit Coverage Standards](../standards/18-audit-coverage.md)'s same-transaction
  requirement and would have been silently defeated by Row Level Security once
  [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) landed. The
  original file is retained in place with a Superseded banner. Not yet implemented; no
  migration needed.

## Redirect ADRs

These live under `_redirects/` so older links keep working without polluting the main
numeric sequence:

- [_redirects/0004-identity-strategy.md](_redirects/0004-identity-strategy.md) — redirects to ADR 0004 Authentication Strategy.
- [_redirects/0005-i18n-schema.md](_redirects/0005-i18n-schema.md) — redirects to ADR 0008 Localization Schema.
- [_redirects/0006-frontend-single-app-first.md](_redirects/0006-frontend-single-app-first.md) — redirects to ADR 0009 Frontend Single App First.

ADR numbers are sequential. New ADRs take the next free number; superseded ADRs leave a redirect stub under `_redirects/` so older links keep working. A small set of early-draft ADRs (0004 — Authentication Strategy; 0005 — Live Classroom Media Stack; 0006 — Events and Outbox) were renumbered before any ADR was relied on at the code level; the redirect stubs preserve that history. Going forward, once an ADR is accepted, its number is fixed for that topic; renaming the topic is allowed, reassigning the number is not.

## Open ADR Drafts

Each draft below has a **target phase** by which it must be Accepted (later than the
target is a blocker for that phase) and a tentative **reserved number** so the slot
doesn't drift. Owners are tracked in the project's CODEOWNERS / task tracker; this
table records the phase commitment so reviewers can flag late drafts.

| Reserved # | Topic | Target phase (must be Accepted before) | Referenced from |
|---|---|---|---|
| 0025 | Scoring + completion DSL sandbox engine (CEL vs restricted Lua vs custom; sandbox boundary; allowed function set) | **Phase 05** — `TenantCompletionRule` runtime evaluator lights up here; Phase 08a's assessment scoring depends on it | [ADR-0018](0018-tenant-driven-customization-model.md), [phase-05-education-learning-content.md](../roadmap/phase-05-education-learning-content.md), [phase-08a-assessment-notifications.md](../roadmap/phase-08a-assessment-notifications.md) |
| 0026 | Release-tag scheme (`vYYYY.MM.DD.<n>` vs SemVer; SaaS continuous-deploy reconciliation; Self-Hosted release cadence) | **Phase 11** — production hardening checklist owns this | [14-git-workflow.md § Tagging and Releases](../standards/14-git-workflow.md) |
| 0027 | Frontend i18n library pick (`next-intl` vs `react-intl` vs `lingui`) | **Phase 04** — the first CMS / page-builder surface ships locale-aware copy | [12-localization.md](../architecture/12-localization.md), [08-localization.md](../standards/08-localization.md) |

**Reservation rule:** the numbers above are *reserved but not yet drafted*. When a
draft lands, take its reserved number; do not let another ADR claim it. If the
decision is dropped, leave the number unused — never recycle.

**SLA:** any draft whose target phase is currently in progress without an Accepted ADR
is a blocker on the phase exit checklist for that phase. The roadmap's Phase Exit
Decision lines reference this list.

## Authoring an ADR

1. Pick the number: if your topic is in § Open ADR Drafts above, use its reserved
   number; otherwise take the next number **after the highest in the Active list**.
   Never take a reserved number for an unrelated topic, and never reuse a superseded
   one — see § Reservation rule.
2. Use the template (see existing ADRs).
3. Status starts as `Proposed`; flip to `Accepted` once team agrees.
4. Link the ADR from the relevant architecture or standard document.
5. If superseding an earlier ADR, mark the older one `Superseded by` and link forward.
