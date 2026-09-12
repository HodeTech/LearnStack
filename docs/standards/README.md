# LearnStack Engineering Standards

This directory contains the engineering rules that apply across the LearnStack codebase. Architecture docs (`docs/architecture/`) explain *what* we are building; standards explain *how* we build it.

## How to Use This Directory

- **Author-side:** before opening a PR, skim the standards that touch your change.
- **Reviewer-side:** reference standards in review comments rather than re-litigating preferences.
- **Newcomer-side:** read [00-principles.md](00-principles.md) first; the rest is reference material.
- **Disagreement:** propose a change via PR against the standard itself. Standards are versioned documents, not folklore.

## Index

| # | Document | Scope |
|---|----------|-------|
| 00 | [Principles](00-principles.md) | The handful of beliefs every other standard descends from. |
| 01 | [Architecture Standards](01-architecture-standards.md) | Module boundaries, dependency direction, ports & adapters, aggregate ownership. |
| 02 | [Backend Coding Standards](02-backend-coding.md) | C# / .NET style, async, nullability, records, MediatR, EF Core. |
| 03 | [Frontend Coding Standards](03-frontend-coding.md) | TypeScript / React / Next.js style, components, hooks, data fetching. |
| 04 | [API Design Standards](04-api-design.md) | REST, Problem Details, pagination, idempotency, versioning. |
| 05 | [Database Standards](05-database.md) | Schema conventions, migrations, indexing, tenant-aware patterns. |
| 06 | [Testing Standards](06-testing.md) | Unit / integration / architecture / E2E / contract tests; pyramid; coverage targets. |
| 07 | [Frontend Architecture Standards](07-frontend-architecture.md) | App Router layout, server vs client components, tenant context, SDK shape. |
| 08 | [Localization Standards](08-localization.md) | i18n rules for strings, content, URLs, SEO, formatting. |
| 09 | [Error Handling Standards](09-error-handling.md) | Exception hierarchy, Problem Details, frontend error boundaries, user-facing copy. |
| 10 | [Observability Standards](10-observability.md) | Logging, tracing, metrics, correlation, redaction. |
| 11 | [Security Standards](11-security.md) | Auth, tenant isolation enforcement, OWASP, secrets, file uploads, headers. |
| 12 | [Infrastructure Standards](12-infrastructure.md) | Docker, CI/CD, environments, configuration, deployment. |
| 13 | [Documentation Standards](13-documentation.md) | ADRs, code comments, diagrams, doc style. |
| 14 | [Git Workflow Standards](14-git-workflow.md) | Branching, commits, PRs, reviews. |
| 15 | [Performance Standards](15-performance.md) | Budgets, caching, query shape, frontend perf. |
| 16 | [Accessibility Standards](16-accessibility.md) | WCAG targets, semantic HTML, keyboard, screen readers. |
| 17 | [Code Review Standards](17-code-review.md) | What to look for, what *not* to block on, etiquette. Zero-tolerance blockers and author self-review gate. |
| 18 | [Audit Coverage Standards](18-audit-coverage.md) | Which operations must be audited; payload contract; retention; per-module classification matrix. |
| 19 | [Permissions Standards](19-permissions.md) | `{module}.{resource}.{action}` naming, closed action set, registry pattern, matrix template, built-in roles. |
| 20 | [Infrastructure Stack Standards](20-infrastructure-stack.md) | Demand-gated building blocks, the foundation ports (`IEventBus`, `ICacheService`, `ISecretProvider`), APISIX gateway, Hub contract surface + its two invariants, entitlement projection, outbox/inbox usage. |
| 21 | [Architecture Tests + Analyzers Catalogue](21-architecture-tests-catalogue.md) | Single source of truth for the identifier, assertion, status, and source ADR / standard of every non-skippable architecture test or Roslyn analyzer. Cross-link target so renames touch one place. |

## Status of Each Standard

Standards have one of three states:

- **Active.** Currently enforced; PRs must comply. There is code, tooling, or a live
  process that the standard governs **today**.
- **Adopted.** Agreed and binding on the code that will implement it — but that code does
  not exist yet, so nothing enforces it beyond review.
- **Draft.** Proposed; open for discussion.

### The rule that makes the model mean something

> **A standard with no implementing code is `Adopted`, not `Active`.**

Each document declares its state at the top, and until 2026-08-08 **all twenty-two
declared `Active`** — including standards governing endpoints, migrations, permissions
and audit rows that do not exist. A three-state model whose every member sits in one
state is decorative: it tells a reader nothing, and it quietly overstates how much of the
corpus is load-bearing.

"Adopted" is not a weaker commitment. It is the honest one. It says: this is the rule the
implementing PR must satisfy, and there is nothing standing between a violation and
`main` except a reviewer who remembers. That is exactly the situation in which a reviewer
most needs to know.

Promotion `Adopted → Active` happens in the PR that lands the enforcement — the
migration, the endpoint, the analyzer, the architecture test — not in a separate
bookkeeping pass.

### Honest status today

The table below is the current, accurate state as of 2026-09-12, at HEAD with
[Phase 02a](../roadmap/phase-02a-kernel-tenancy.md) Packets 0–3, 3b and 4–10
shipped.

**The documents and this table say the same thing, and a test holds them to it.**
[Phase 02a Packet 10](../roadmap/phase-02a-kernel-tenancy.md) reconciled the twenty-two
status lines with this table, together with the architecture-test statuses, so the two
views of "what is actually enforced" changed in one commit —
`Standard_Status_Headers_Match_The_Index` fails the build if they part again, counts
included.

| # | Standard | Status | What does or does not enforce it today |
|---|---|---|---|
| 00 | [Principles](00-principles.md) | **Active** | Governs every PR and every ADR; principles 1, 16 and 17 are already deciding live scope questions. |
| 01 | [Architecture Standards](01-architecture-standards.md) | **Active** | Module layout shipped; `ModuleDomain_DoesNotDependOn_*` and the planted-violation meta-test are green. |
| 02 | [Backend Coding](02-backend-coding.md) | **Active** | MediatR pipeline, `Result<T>`, `IClock`, the `LS0001` analyzer and the pipeline-order test all ship. Packet 6 brought the first `DbContext`, the first aggregates and the ambient unit of work, so its EF Core and domain-modelling clauses are live too — for one module. |
| 03 | [Frontend Coding](03-frontend-coding.md) | **Active** | ESLint and TypeScript strict mode are configured, the Vitest harness runs, and Packet 10 added the two rules this document's § Forbidden names — the direct-`fetch` ban and `dangerouslySetInnerHTML` outside the sanitised-HTML primitive — with a test that lints fixtures through the app's own configuration, because a configured rule and a missing one look identical from a green `pnpm lint`. `apps/web` is otherwise still a scaffold; its first real components land in [Phase 02d](../roadmap/phase-02d-walking-skeleton.md). |
| 04 | [API Design](04-api-design.md) | **Active** | Packet 4 shipped the versioned route convention and its startup guards, one Problem Details shape on every error including the framework-minted ones, cursor pagination, the sort grammar, idempotency keys, ETag concurrency, correlation ids, the request-body limit and the tenancy edge — each with tests in the required `backend` check. No *business* endpoint exists yet; the conventions they will land into do. |
| 05 | [Database](05-database.md) | **Active** | Packet 6 applied it — two migration chains, ten tables — and Packets 8 and 9 took it to four chains and seventeen tables: the four-role model, and the canonical RLS template this document owns — `ENABLE` **and** `FORCE`, one `AND`-ed policy per table, an explicit `WITH CHECK` — asserted against a real PostgreSQL as `learnstack_app`. Its § Concurrency, § Table classes, § Indexes and § GRANT matrix each have a test that fails without them. Partitioning and the retention job are still ahead. |
| 06 | [Testing](06-testing.md) | **Active** | Unit, architecture, contract **and** integration suites all run in the required `backend` job — Packet 4 removed the filter that used to exclude the integration assembly, which by then held the only tests that could catch an unversioned route. The Docker-bound `backend-integration` job activated in Packet 6 with the four-role provisioning suite; the split is by `[Trait("Requires","Docker")]` and the two jobs' filters are exact complements. |
| 07 | [Frontend Architecture](07-frontend-architecture.md) | **Active** | The one-app rule is mechanical — `Frontend_Has_Only_The_Web_App` fails a second application in this repository — and the route groups it prescribes exist as layouts. The rest, the server/client split and the tenant context an SDK call carries, is exercised first in [Phase 02d](../roadmap/phase-02d-walking-skeleton.md): Active for what ships, and the phase that adds components is the one that tests them. |
| 08 | [Localization](08-localization.md) | **Active** | Packet 6 shipped `tenant_locales` and the slug schema, and "exactly one default locale per tenant" is enforced twice: a partial unique index `UNIQUE (tenant_id) WHERE is_default` and an aggregate guard that carries the message. `LocalizedText` and `LocalizedMessage` ship with their own cases, and every error the API returns is keyed rather than written. The i18n **runtime** — routing, negotiation, formatting — lands in [Phase 04](../roadmap/phase-04-cms-media-pages.md). |
| 09 | [Error Handling](09-error-handling.md) | **Active** | L1 `IExceptionHandler`, the exception hierarchy, `ProblemDetailsFactory` and `HttpStatusMap` shipped in Packet 3. |
| 10 | [Observability](10-observability.md) | **Active** | Serilog → OTLP, OpenTelemetry SDK, `TenantContextSpanProcessor` and the redaction enrichers shipped in Packet 3. |
| 11 | [Security](11-security.md) | **Active** | No authentication yet, and the isolation half of this document is live and mechanical. Row Level Security with the four roles and the isolation suite that runs as `learnstack_app`; the tenancy edge, the trusted-hop predicate and the anonymous rate limiter from Packet 4; and, since Packet 10, § The out-of-band setters is mechanical — every announcer of a session variable is one the table names and each reader opens its transaction read-only, with `App_Role_Cannot_Enumerate_Tenants`, `App_Role_Cannot_Enumerate_Host_Map` and `Tenant_A_Cannot_Repoint_Tenant_B_Host` proving the role cannot read or repoint what the policies bar. Authentication and authorisation land in [Phase 02b](../roadmap/phase-02b-events-auth.md) and [Phase 03](../roadmap/phase-03-identity-admin.md). Three sections are **not** enforced by anything today and are the document's own carve-out: § Transport, § HTTP Headers and § CORS — nothing sets HSTS, `nosniff`, a CSP or an origin policy, at the edge or in either app. [Phase 11 § Secure headers](../roadmap/phase-11-production-hardening.md) owns them, at APISIX and in the ASP.NET layer beside it. |
| 12 | [Infrastructure](12-infrastructure.md) | **Active** | Compose stack, `Makefile`, CI workflow, pre-commit hooks and secret scanning all live since Phase 01. |
| 13 | [Documentation](13-documentation.md) | **Active** | Governs this corpus; the CI link audit walks changed Markdown. |
| 14 | [Git Workflow](14-git-workflow.md) | **Active** | Conventional Commits, hooks and required checks are live. Two branch-protection settings — `Require approvals` and `Do not allow bypassing` — are **deferred by maintainer decision (2026-08-10)** while the repository has one active contributor; the trigger and what activating them involves are recorded in [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md). Everything else in Standards 14 is enforced today. |
| 15 | [Performance](15-performance.md) | **Adopted** | No budget is measured and no load test exists. Enforcement lands in [Phase 11](../roadmap/phase-11-production-hardening.md). |
| 16 | [Accessibility](16-accessibility.md) | **Adopted** | No user interface to audit. First surfaces render in [Phase 02d](../roadmap/phase-02d-walking-skeleton.md); automated axe checks in [Phase 06](../roadmap/phase-06-renderer-admin-studio.md). |
| 17 | [Code Review](17-code-review.md) | **Active** | Applied to every pull request merged so far; the zero-tolerance blocker list is in live use. |
| 18 | [Audit Coverage](18-audit-coverage.md) | **Active** | Packet 9 lit the write path under [ADR-0033](../decisions/0033-audit-durability-model.md) and [ADR-0044](../decisions/0044-audit-write-path.md): classification at step 3, MUST rows on the business transaction before `COMMIT`, `audit_log` / `audit_config` with their append-only layers, and the catalogue ↔ matrix join enforced in both directions and per request type by `AuditCoverageTests`. Retention and partitioning are [Phase 11](../roadmap/phase-11-production-hardening.md)'s. |
| 19 | [Permissions](19-permissions.md) | **Adopted** | No permission key, policy or role exists. Lands in [Phase 03](../roadmap/phase-03-identity-admin.md). |
| 20 | [Infrastructure Stack](20-infrastructure-stack.md) | **Active** | `ISecretProvider` shipped in Packet 3, the foundation ports and their defaults in Packet 5, and the entitlement socket in Packet 9; `DeploymentMode` branching happens once, at the composition root. Packet 10 made the bans this document states mechanical: no module reaches a cache client, a Hub namespace, `audit_log` or `platform_entitlement_cache`, and every entitlement key a call site names is a registry member. The adapters themselves — Dapr, Kafka, Valkey, Vault, APISIX — arrive on [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s triggers, which is the model rather than a gap in it. |
| 21 | [Architecture Tests Catalogue](21-architecture-tests-catalogue.md) | **Active** | The catalogue's own § Implemented today carries the counts, and `The_Catalogue_Counts_Its_Own_Rules` recomputes them, so this row does not keep a third copy. Rules run in the architecture assembly and beside it in the unit, integration and frontend suites — each where it can actually fail, against an applied schema, a real host or a real ESLint configuration. `Every_Implemented_Rule_Names_A_Test_That_Exists` holds each Implemented entry to a method of that name, and `No_Architecture_Test_Is_Skippable` to the policy that none of them can be turned off. |

Nineteen `Active`, three `Adopted`. Packet 10 moved five: the frontend rules, the one-app
rule, the locale invariants, the out-of-band setters and the port bans are all mechanical
now, and a standard whose rules a test enforces is `Active` by this document's own
definition. The three that remain are the honest ones — no budget is measured, there is no
interface to audit, and no permission key exists — and each names the phase that will
promote it. That split is far more useful to a reviewer than twenty-two identical labels.

## Relationship to ADRs

| Document type | Purpose |
|---------------|---------|
| ADR (`docs/decisions/`) | A one-time decision with status, context, decision, consequences. Immutable history, corrected only by the two bounded mechanisms in [13-documentation.md § Correcting and Amending ADRs](13-documentation.md). |
| Standard (`docs/standards/`) | An ongoing rule that the team applies day to day. Editable as the team learns. |

When a standard is established, an ADR records the moment of adoption. The ADR then points at the standard for the living detail.

## Tooling

Where a standard can be enforced by automation, it must be:

- Roslyn analyzers / `.editorconfig` for backend.
- ESLint / TypeScript strict mode for frontend.
- Custom architecture tests (NetArchTest / ArchUnitNET) for module-boundary rules.
- Test conventions enforced by CI.
- Commit / PR rules enforced by GitHub Actions and CODEOWNERS.

Manual-only rules are flagged in each document.
