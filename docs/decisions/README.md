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
| 0002 | [Initial Architecture](0002-initial-architecture.md) | .NET 10 + EF Core + PostgreSQL + Redis + MinIO + Next.js; modular monolith |
| 0003 | [Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md) | Query filters + RLS + audit + architecture tests |
| 0004 | [Authentication Strategy](0004-authentication-strategy.md) | Off-the-shelf identity provider preferred over hand-rolled auth |
| 0005 | [Live Classroom Media Stack](0005-live-classroom-media-stack.md) | LiveKit OSS self-hosted by default; LiveKit Cloud optional; no custom SFU |
| 0006 | [Events and Outbox](0006-events-and-outbox.md) | Domain + integration events; outbox pattern; idempotent handlers |
| 0007 | [Documentation Language and Conventions](0007-documentation-language-and-conventions.md) | English; Mermaid; ADR + standards split |
| 0008 | [Localization Schema](0008-localization-schema.md) | Side translation tables vs JSONB localized fields |
| 0009 | [Frontend — Single Next.js App First](0009-frontend-single-app-first.md) | One Next.js app with route segments; multi-app split deferred |
| 0010 | [Cross-Module Communication](0010-cross-module-communication.md) | Four sanctioned mechanisms, no fifth |
| 0011 | [Vertical Extension Points](0011-extension-points.md) | Typed registry; verticals never modify core |
| 0012 | [Search Strategy](0012-search-strategy.md) | Meilisearch; one instance per env; index-per-(kind, locale); tenant_id as query filter |
| 0013 | [Page Block Schema Versioning](0013-page-block-schema-versioning.md) | `(key, schemaVersion)` tuple; immutable schemas; lazy + bulk migration; placeholder on unknown version |

## Superseded / Redirect ADRs

These live under `_redirects/` so older links keep working without polluting the main numeric sequence:

- [_redirects/0004-identity-strategy.md](_redirects/0004-identity-strategy.md) — redirects to ADR 0004 Authentication Strategy.
- [_redirects/0005-i18n-schema.md](_redirects/0005-i18n-schema.md) — redirects to ADR 0008 Localization Schema.
- [_redirects/0006-frontend-single-app-first.md](_redirects/0006-frontend-single-app-first.md) — redirects to ADR 0009 Frontend Single App First.

ADR numbers are sequential. New ADRs take the next free number; superseded ADRs leave a redirect stub under `_redirects/` so older links keep working. A small set of early-draft ADRs (0004 — Authentication Strategy; 0005 — Live Classroom Media Stack; 0006 — Events and Outbox) were renumbered before any ADR was relied on at the code level; the redirect stubs preserve that history. Going forward, once an ADR is accepted, its number is fixed for that topic; renaming the topic is allowed, reassigning the number is not.

## Open ADR Drafts

The following decisions are referenced as `ADR-pending` in other docs and need an ADR before the rule is treated as stable:

| Topic | Referenced from |
|---|---|
| API versioning policy (URL vs header, deprecation cadence) | [04-technical-architecture.md § API Strategy](../architecture/04-technical-architecture.md), [04-api-design.md § Versioning](../standards/04-api-design.md) |
| Release-tag scheme (`vYYYY.MM.DD.<n>` vs SemVer) | [14-git-workflow.md § Tagging and Releases](../standards/14-git-workflow.md) |
| Audit-coverage retention floors (currently in [18-audit-coverage.md](../standards/18-audit-coverage.md)) | [18-audit-coverage.md](../standards/18-audit-coverage.md) |
| Permission closed-action set (`read \| write \| delete \| admin`) | [19-permissions.md](../standards/19-permissions.md) |
| Distributed-consistency Tier framing (Tier 1 / 2A / 2B / 3) | [01-architecture-standards.md § Distributed-Consistency Tiers](../standards/01-architecture-standards.md) |

## Authoring an ADR

1. Pick the next available 4-digit number.
2. Use the template (see existing ADRs).
3. Status starts as `Proposed`; flip to `Accepted` once team agrees.
4. Link the ADR from the relevant architecture or standard document.
5. If superseding an earlier ADR, mark the older one `Superseded by` and link forward.
