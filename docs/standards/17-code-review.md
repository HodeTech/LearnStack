# 17 — Code Review Standards

**Status:** Active

How LearnStack reviews pull requests. The goal is faster, safer ship — not gatekeeping.

## Reviewer Responsibilities

A reviewer reads enough of the PR to be willing to:
- Ship the change themselves.
- Page on a Saturday if it breaks production.

If either feels uncomfortable, leave a request for change.

## Zero Tolerance — Automatic Blockers

The following findings are always `blocker:`. No discussion needed; the PR does not merge until the listed standard is satisfied.

| Finding | Standard |
|---------|----------|
| Tenant-owned entity missing `[TenantOwned]`, EF query filter, or RLS policy | [01-architecture-standards.md](01-architecture-standards.md) § Tenant-Scoped Code, [05-database.md](05-database.md) |
| `IgnoreQueryFilters()` used outside platform-admin code paths | [11-security.md](11-security.md) § Tenant Isolation |
| `app.current_tenant_id` not set on a tenant-scoped DB connection | [05-database.md](05-database.md) § Connection Management |
| Raw SQL with interpolated user input | [05-database.md](05-database.md) § Raw SQL, [11-security.md](11-security.md) § SQL & ORM |
| Secret, token, or password committed to the repo | [11-security.md](11-security.md) § Secrets |
| Tenant id read from request body or query param at API edge | [04-api-design.md](04-api-design.md) § Tenant Context |
| Provider SDK type imported in `Domain` or `Application` | [01-architecture-standards.md](01-architecture-standards.md) § Provider Adapters |
| Background job without `TenantId` in payload | [01-architecture-standards.md](01-architecture-standards.md) § Tenant-Scoped Code |
| Hardcoded user-facing string in JSX or backend response | [08-localization.md](08-localization.md) § Strings in Code |
| `dangerouslySetInnerHTML` without sanitisation wrapper | [11-security.md](11-security.md) § XSS, [03-frontend-coding.md](03-frontend-coding.md) § Forbidden |
| Token stored in `localStorage` or `sessionStorage` | [11-security.md](11-security.md) § Authentication |
| Cross-module SQL JOIN against another module's table | [01-architecture-standards.md](01-architecture-standards.md) § Cross-Module Communication |
| Live classroom recording started without consent flow | [16-media-pipeline.md](../architecture/16-media-pipeline.md) § Consent Flow |
| Public-read ACL on a tenant-scoped object | [11-security.md](11-security.md) § File Uploads |
| `Result<T>.Ok(default!)` or other null-success pattern | [09-error-handling.md](09-error-handling.md) § Forbidden |
| `DateTime.UtcNow` / `DateTime.Now` in domain or application code | [02-backend-coding.md](02-backend-coding.md) § Time |

These are not opinions — they map directly to existing standards. If you find one, cite the standard line and request changes.

## Author Self-Review Gate

Before requesting reviews, run the relevant checklist on your own diff:

### Backend self-review

- [ ] Every new endpoint has `[Authorize(Policy = ...)]` or explicit `[AllowAnonymous]` with a comment justifying it.
- [ ] Every new tenant-owned entity has `[TenantOwned]`, an EF query filter, and a Postgres RLS migration.
- [ ] Every new MediatR handler validates input via FluentValidation and returns `Result<T>` for expected outcomes.
- [ ] Every new integration event ships in the outbox in the same transaction as the domain change.
- [ ] Every new background job has `TenantId` in its payload and restores ambient tenant context.
- [ ] Every new provider call has an explicit timeout, retry policy, and `ProviderException` mapping at the adapter boundary.
- [ ] Strongly-typed ids are used; no raw `Guid` on the public surface.
- [ ] No `DateTime.UtcNow` / `DateTime.Now` in domain or application code (use `IClock` / `TimeProvider`).
- [ ] Architecture tests still pass locally.
- [ ] Tenant-isolation test added for the new surface.

### Frontend self-review

- [ ] No hardcoded user-facing strings in JSX (grep for new strings outside `t('...')`).
- [ ] Strict TypeScript: no `any`, no `// @ts-ignore` (use `@ts-expect-error` with a comment if absolutely necessary).
- [ ] Server Components are the default; `"use client"` only where interactivity needs it.
- [ ] Cache keys include `tenantId` and `locale` where relevant.
- [ ] New forms use React Hook Form + Zod and render errors at the field level.
- [ ] New routes have `error.tsx` and `loading.tsx` where appropriate.
- [ ] Accessibility: every interactive element keyboard-reachable; labels associated; color contrast checked; axe-core tests pass.
- [ ] Bundle size delta acceptable on public routes (< 200 KB gzipped per route).

If a checkbox doesn't apply to your diff, omit it. If you cannot tick a checkbox, fix the gap before requesting review.

## Priority Order

Review for, in this order:

1. **Correctness.** Does the code do what the PR claims?
2. **Security.** Tenant isolation, authorization, secret handling, injection surface.
3. **Tenant isolation.** Every tenant-scoped query / job / event carries `TenantId`.
4. **Module boundaries.** No cross-module entity imports, no cross-module SQL joins, no provider SDK types in domain code.
5. **Data integrity.** Migrations safe, concurrency considered, idempotency where needed.
6. **Test coverage.** Tenant isolation tests, unit tests for new logic, integration tests for new code paths.
7. **Operational impact.** Backwards compatibility, rollback plan, log/metric coverage, performance budget.
8. **Readability.** Naming, decomposition, comments where the *why* is non-obvious.

## Do Not Block On

- Personal style preferences already covered by the formatter.
- Naming bikeshedding when clarity is not harmed.
- Micro-optimizations without measured need.
- Adding tests for trivial getters/setters.
- Reformatting unrelated lines.

If the standard you'd cite is "I'd do it differently," it's a suggestion, not a blocker.

## Comment Style

- Be specific. "This breaks tenant isolation because `TenantId` is missing on the filter at line 42" beats "isolation issue."
- Reference standards or ADRs by link when relevant.
- Distinguish:
  - **`nit:`** stylistic, optional.
  - **`suggestion:`** non-blocking idea.
  - **`question:`** seeking clarification.
  - **`blocker:`** must be fixed before merge.
- Prefer suggestions in the form "what about X?" rather than statements of opinion.

## Approval

- Approve when the change is correct, safe, and follows the standards — even if not how you'd write it.
- Two approvals required for: security-sensitive changes, schema migrations, provider integrations, cross-module contract changes.
- Self-review your own PR before requesting reviews; catch the obvious things yourself.

## Review SLAs

- Initial response within one business day.
- Quick changes (< 100 lines): same day if possible.
- Larger changes: 2 business days.
- Mark a review as **"in progress"** if you start but cannot finish.

## When a PR Is Stuck

- The PR author drives resolution. If a reviewer goes silent, ping after 24 hours, then escalate.
- Disagreements about a standard or pattern become a PR against the standard, not an argument in the review.
- A second reviewer's tie-breaking opinion is welcome.

## Code Review Checklist

The following questions are asked on every PR by the reviewer (mentally, or in the description checklist):

- [ ] Does the PR title match the change?
- [ ] Is the description accurate?
- [ ] Are tests adequate?
- [ ] Is tenant isolation preserved?
- [ ] Is authorization enforced server-side?
- [ ] Are migrations safe and reversible (or two-step)?
- [ ] Is observability adequate (logs, traces, metrics)?
- [ ] Are error responses Problem-Details-compliant?
- [ ] Are new public APIs documented in OpenAPI?
- [ ] Are new translatable strings hooked into i18n?
- [ ] Are new external dependencies justified?
- [ ] Is the rollback plan clear for risky changes?

## Author Etiquette

- Self-review your diff before requesting reviews.
- Address each comment, either by changing the code or by replying explaining why.
- Don't force-push during an active review unless you preserve commit messages or note the reason.
- "@" only the people whose input you need; don't spam.
- Thank reviewers for catching things.

## Reviewer Etiquette

- Critique code, not people.
- Assume the author considered alternatives; ask before declaring something wrong.
- Praise good design.
- Don't pile on after another reviewer's blocking comment — let it resolve first.
- Don't be the bottleneck for trivial style preferences.

## Forbidden

- Approving without reading the diff.
- Blocking on personal style when no standard supports the request.
- Force-pushing over an active review without communication.
- Merging your own security-sensitive PR.
- Auto-approving via tooling for non-trivial changes.
- "LGTM" with no evidence of having read the change.
