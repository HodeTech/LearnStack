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

The table below is the current, accurate state as of 2026-08-18, at HEAD with
[Phase 02a](../roadmap/phase-02a-kernel-tenancy.md) Packets 0–3 and 3b shipped.

**The individual documents still declare `Active` in their own headers.** Reconciling the
twenty-two status lines with this table is a
[Phase 02a Packet 10](../roadmap/phase-02a-kernel-tenancy.md) deliverable, landed
together with the architecture-test reconciliation so the two views of "what is actually
enforced" change in one commit. Until that lands, **this table wins**.

| # | Standard | Status | What does or does not enforce it today |
|---|---|---|---|
| 00 | [Principles](00-principles.md) | **Active** | Governs every PR and every ADR; principles 1, 16 and 17 are already deciding live scope questions. |
| 01 | [Architecture Standards](01-architecture-standards.md) | **Active** | Module layout shipped; `ModuleDomain_DoesNotDependOn_*` and the planted-violation meta-test are green. |
| 02 | [Backend Coding](02-backend-coding.md) | **Active** | MediatR pipeline, `Result<T>`, `IClock`, the `LS0001` analyzer and the pipeline-order test all ship. Its EF Core and domain-modelling clauses are `Adopted` until Packet 6 brings a `DbContext`. |
| 03 | [Frontend Coding](03-frontend-coding.md) | **Adopted** | ESLint and TypeScript strict mode are configured, and Packet 3b stood up the Vitest harness (jsdom + Testing Library) with one render test — so the required `frontend` check now asserts something. `apps/web` is otherwise still a scaffold with no components. First real code: [Phase 02d](../roadmap/phase-02d-walking-skeleton.md). |
| 04 | [API Design](04-api-design.md) | **Adopted** | No endpoint exists. Problem Details, cursor pagination, idempotency and ETag land in Packet 4. |
| 05 | [Database](05-database.md) | **Adopted** | No `DbContext` and no migration exist. The canonical RLS template it now owns is applied by Packet 6's first migration. |
| 06 | [Testing](06-testing.md) | **Active** | Unit, architecture and contract suites run in CI, and the meta-test proves the architecture suite can fail. The **integration** suite does not: the backend job filters it out (`FullyQualifiedName!~LearnStack.Tests.Integration`) and its own job is gated on an unset `vars.ENABLE_BACKEND_INTEGRATION` until Phase 02a Packet 7 lands the first isolation test. |
| 07 | [Frontend Architecture](07-frontend-architecture.md) | **Adopted** | Route groups exist as empty layouts; server/client split, tenant context and SDK shape are exercised first in [Phase 02d](../roadmap/phase-02d-walking-skeleton.md). |
| 08 | [Localization](08-localization.md) | **Adopted** | `tenant_locales` and the slug schema land in Packet 6; the i18n runtime in [Phase 04](../roadmap/phase-04-cms-media-pages.md). |
| 09 | [Error Handling](09-error-handling.md) | **Active** | L1 `IExceptionHandler`, the exception hierarchy, `ProblemDetailsFactory` and `HttpStatusMap` shipped in Packet 3. |
| 10 | [Observability](10-observability.md) | **Active** | Serilog → OTLP, OpenTelemetry SDK, `TenantContextSpanProcessor` and the redaction enrichers shipped in Packet 3. |
| 11 | [Security](11-security.md) | **Adopted** | No auth, no RLS, no header middleware yet. Tenant isolation lands in Packet 7, authentication in [Phase 02b](../roadmap/phase-02b-events-auth.md). Its § Tenant Context is nonetheless the binding authority the implementing PR must follow. |
| 12 | [Infrastructure](12-infrastructure.md) | **Active** | Compose stack, `Makefile`, CI workflow, pre-commit hooks and secret scanning all live since Phase 01. |
| 13 | [Documentation](13-documentation.md) | **Active** | Governs this corpus; the CI link audit walks changed Markdown. |
| 14 | [Git Workflow](14-git-workflow.md) | **Active** | Conventional Commits, hooks and required checks are live. Two branch-protection settings — `Require approvals` and `Do not allow bypassing` — are **deferred by maintainer decision (2026-08-10)** while the repository has one active contributor; the trigger and what activating them involves are recorded in [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md). Everything else in Standards 14 is enforced today. |
| 15 | [Performance](15-performance.md) | **Adopted** | No budget is measured and no load test exists. Enforcement lands in [Phase 11](../roadmap/phase-11-production-hardening.md). |
| 16 | [Accessibility](16-accessibility.md) | **Adopted** | No user interface to audit. First surfaces render in [Phase 02d](../roadmap/phase-02d-walking-skeleton.md); automated axe checks in [Phase 06](../roadmap/phase-06-renderer-admin-studio.md). |
| 17 | [Code Review](17-code-review.md) | **Active** | Applied to every pull request merged so far; the zero-tolerance blocker list is in live use. |
| 18 | [Audit Coverage](18-audit-coverage.md) | **Adopted** | `AuditLogBehavior` is a shell and `audit_log` does not exist. Lands in Packet 9 under [ADR-0033](../decisions/0033-audit-durability-model.md). |
| 19 | [Permissions](19-permissions.md) | **Adopted** | No permission key, policy or role exists. Lands in [Phase 03](../roadmap/phase-03-identity-admin.md). |
| 20 | [Infrastructure Stack](20-infrastructure-stack.md) | **Adopted** | `ISecretProvider` shipped in Packet 3 and `DeploymentMode` branching is real, but the ports land in Packet 5 and the Dapr / Kafka / APISIX / Vault adapters are demand-gated to [Phase 11](../roadmap/phase-11-production-hardening.md) per [ADR-0035](../decisions/0035-demand-gated-infrastructure.md). |
| 21 | [Architecture Tests Catalogue](21-architecture-tests-catalogue.md) | **Active** | Fourteen tests run in CI; the catalogue's own per-row status column distinguishes those from the registered-but-unimplemented majority. |

Eleven `Active`, eleven `Adopted`. That split is the honest picture of a platform whose
foundation is real and whose domain has not been written yet — and it is far more useful
to a reviewer than twenty-two identical labels.

## Relationship to ADRs

| Document type | Purpose |
|---------------|---------|
| ADR (`docs/decisions/`) | A one-time decision with status, context, decision, consequences. Immutable history. |
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
