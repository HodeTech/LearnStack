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
| 20 | [Infrastructure Stack Standards](20-infrastructure-stack.md) | Dapr building blocks (`IEventBus`, `ICacheService`, `ISecretProvider`), APISIX gateway, Hub HTTPS contract surface, entitlement projection, outbox/inbox usage. |
| 21 | [Architecture Tests + Analyzers Catalogue](21-architecture-tests-catalogue.md) | Single source of truth for the identifier, assertion, and source ADR / standard of every non-skippable architecture test or Roslyn analyzer. Cross-link target so renames touch one place. |

## Status of Each Standard

Standards have one of three states:

- **Active.** Currently enforced; PRs must comply.
- **Adopted.** Agreed, but not yet enforced by tooling — manual review only.
- **Draft.** Proposed; open for discussion.

Each document declares its state at the top.

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
