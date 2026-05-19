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
| 0003 | [Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md) | Query filters + RLS + audit + architecture tests (Amendment 1: Organization scope, 2026-05-18) |
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
| 0016 | [Audit Log Subsystem](0016-audit-log-subsystem.md) | `LearnStack.Modules.Audit`; EF interceptor + `IAuditStateCapture` + `AuditLogBehavior`; partitioned `audit_log` table; retention |
| 0017 | [Tenant + Organization Hierarchy](0017-tenant-organization-hierarchy.md) | Two-level: Tenant → Organization; permission scope Platform / Tenant / Organization |
| 0018 | [Tenant-Driven Customization Model](0018-tenant-driven-customization-model.md) | Generic-only core; tenants define content types, page blocks, scoring rules as **data**, not code (supersedes ADR-0011) |
| 0019 | [LearnStack Hub](0019-learnstack-hub.md) | Separate codebase `learnstack-hub`; tenant lifecycle, plans, entitlements, billing, custom domain admin; mTLS internal API |
| 0020 | [Triple Deployment + Hybrid License](0020-triple-deployment-hybrid-license.md) | SaaS / Dedicated / Self-Hosted from one codebase; phone-home + RSA-signed key + 30-day grace |
| 0021 | [Feature-Based Entitlement](0021-feature-based-entitlement.md) | Feature flags + numeric limits per plan; typed `FeatureKeys` / `LimitKeys` registries |
| 0022 | [Custom Domain & TLS](0022-custom-domain-tls.md) | Hub-owned custom domain admin; DNS-01 + HTTP-01 + Let's Encrypt; APISIX hot-reload |
| 0029 | [Object Storage — SeaweedFS](0029-object-storage-seaweedfs.md) | Self-hosted SeaweedFS behind the existing `IStorageProvider` S3 contract; partially supersedes ADR-0002's MinIO row |
| 0030 | [Redis-compatible Store — Valkey](0030-redis-compatible-store-valkey.md) | Valkey (Linux Foundation, BSD-3-Clause) for the cache + Dapr state-store backend; RESP-protocol drop-in; partially supersedes ADR-0002's Redis row |
| 0031 | [PostgreSQL — Start on 18.x](0031-postgresql-major-version.md) | Pin primary RDBMS major version to PostgreSQL 18; native `gen_uuid_v7()` + async I/O + longest LTS runway; partially supersedes ADR-0002's PostgreSQL row |

## Superseded ADRs

- **ADR-0011 — Vertical Extension Points** — superseded by [ADR-0018: Tenant-Driven
  Customization Model](0018-tenant-driven-customization-model.md) on 2026-05-18. The
  original file is retained in place (`0011-extension-points.md`) with a Superseded
  status banner and a "why superseded" section linking forward. Never implemented; no
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
| 0023 | Strongly-typed ID source generator (Vogen vs StronglyTypedId vs custom; emitter spec) | **Phase 02a** — interceptors and value converters need the generator at compile time | [02-backend-coding.md](../standards/02-backend-coding.md) |
| 0024 | API versioning policy (URL prefix `/v1/` stays the convention; this ADR codifies deprecation cadence, sunset headers, and the rule for breaking changes) | **Phase 02a** — OpenAPI spec + SDK generation start here | [04-technical-architecture.md § API Strategy](../architecture/04-technical-architecture.md), [04-api-design.md § Versioning](../standards/04-api-design.md) |
| 0025 | Scoring + completion DSL sandbox engine (CEL vs restricted Lua vs custom; sandbox boundary; allowed function set) | **Phase 05** — `TenantCompletionRule` runtime evaluator lights up here; Phase 08a's assessment scoring depends on it | [ADR-0018](0018-tenant-driven-customization-model.md), [phase-05-education-learning-content.md](../roadmap/phase-05-education-learning-content.md), [phase-08a-assessment-notifications.md](../roadmap/phase-08a-assessment-notifications.md) |
| 0026 | Release-tag scheme (`vYYYY.MM.DD.<n>` vs SemVer; SaaS continuous-deploy reconciliation; Self-Hosted release cadence) | **Phase 11** — production hardening checklist owns this | [14-git-workflow.md § Tagging and Releases](../standards/14-git-workflow.md) |
| 0027 | Frontend i18n library pick (`next-intl` vs `react-intl` vs `lingui`) | **Phase 04** — the first CMS / page-builder surface ships locale-aware copy | [12-localization.md](../architecture/12-localization.md), [08-localization.md](../standards/08-localization.md) |
| 0028 | `audit_log` monthly partition management (Hangfire job vs `pg_partman` extension; failure-mode comparison) | **Phase 02a** — partition policy is Day 1; the choice can be retrofitted later but must be ADR'd before production load | [ADR-0016](0016-audit-log-subsystem.md), [31-audit-subsystem.md](../architecture/31-audit-subsystem.md) |

**Reservation rule:** the numbers above are *reserved but not yet drafted*. When a
draft lands, take its reserved number; do not let another ADR claim it. If the
decision is dropped, leave the number unused — never recycle.

**SLA:** any draft whose target phase is currently in progress without an Accepted ADR
is a blocker on the phase exit checklist for that phase. The roadmap's Phase Exit
Decision lines reference this list.

## Authoring an ADR

1. Pick the next available 4-digit number.
2. Use the template (see existing ADRs).
3. Status starts as `Proposed`; flip to `Accepted` once team agrees.
4. Link the ADR from the relevant architecture or standard document.
5. If superseding an earlier ADR, mark the older one `Superseded by` and link forward.
