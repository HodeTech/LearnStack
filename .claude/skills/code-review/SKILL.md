---
name: code-review
description: >
  Perform a thorough code review across security, bugs (and potential bugs),
  optimisation, refactor opportunities, and LearnStack-specific structural
  rules — or compose a detailed review-agent prompt that a separate agent will
  execute. USE FOR: self-review of your own diff before commit, end-to-end PR
  review, post-implementation gate before declaring `implement-task` done,
  producing the review-agent prompt to dispatch a second-opinion agent.
  DO NOT USE FOR: standards-conformance check only (use `standards-check` —
  narrower scope), running the test suite (use `run-tests-locally`), writing
  new tests (use `add-integration-test` / `add-architecture-test`), or quick
  "does this compile" sanity checks.
---

# Code review

## Purpose

Look at a diff with the same depth a careful reviewer would: read intent first,
then walk security, bugs, performance, and design — anchored to the LearnStack
standards corpus so the review is project-aware, not generic. Produces either a
findings report (for self-review) or a review-agent prompt (for delegation).

## When to use

- You finished implementing and want a structured self-review before commit.
- A PR landed on your queue and the user has asked for a review.
- The `implement-task` workflow has reached Step 10 and you need the
  review-agent prompt to dispatch.
- You suspect a specific risk (security, perf) and want a targeted lens.

## When not to use

- Pure standards conformance — use [standards-check](../standards-check/SKILL.md).
  It overlaps with this skill but stays narrower and more mechanical.
- Running tests — use [run-tests-locally](../run-tests-locally/SKILL.md).
- Writing new tests / architecture tests — different skills.
- "Quick look" — `code-review` is intentionally thorough. If the user wants
  a glance, give a glance; don't invoke the full workflow.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Scope | Yes | Branch / commit range / PR number / specific files. |
| Intent | Yes | What the change is *trying* to do. Read the commit message + PR description + linked issue / ADR. |
| Lens | No | If the user asks for a targeted review (e.g. "security only"), narrow the workflow. Default: all lenses. |
| Output mode | Yes | `report` (findings) or `prompt` (compose a review-agent prompt). |

## Workflow

### Step 1 — Understand intent before judging code

A review without intent is a syntax check. Before reading the diff:

1. Read the commit message / PR description in full.
2. Read any cited ADR / standard / architecture doc.
3. If the diff is part of a phase deliverable, read the phase's
   Completion Criteria.
4. Restate, in one paragraph, what the change is trying to do. If you can't,
   ask the author.

You're now allowed to read the code.

### Step 2 — Read the diff once, end to end, without judging

A first pass to build a mental map. Note (don't act on):

- The files touched and how they relate.
- The dependencies one hop out.
- Any surface that looks adjacent but is untouched (often the bug).

### Step 3 — Security lens

Walk every change against the project's
[security standard](../../../docs/standards/11-security.md) and OWASP top 10:

| Concern | What to check |
|---------|---------------|
| **Tenant + organization isolation** | New tenant-owned entity has `[TenantOwned]` + EF filter + RLS policy + canonical `app.tenant_id` session var. `[OrganizationScoped]` covered the same way. Any `IgnoreQueryFilters` outside platform-admin paths is a zero-tolerance blocker per [17-code-review.md § Zero Tolerance](../../../docs/standards/17-code-review.md). |
| **4-step auth order** | Every write use case checks in order: (1) Authn → (2) Tenant membership → (3) Role / permission → (4) Resource scope. Failure at each step returns the right Problem Details code (`unauthorized` / `tenant_mismatch` / `forbidden` / `resource_scope_violation`). Source: [11-security.md § Authorization](../../../docs/standards/11-security.md), [19-permissions.md § Enforcement Points](../../../docs/standards/19-permissions.md). |
| **Authn / authz** | Every new endpoint has `[Authorize(Policy=…)]` or `[AllowAnonymous]` with a comment. Policy keys exist in the permission registry. Resource-scope handlers (`instructor` edits own course only) present where needed. |
| **Tenant id from JWT only** | Never from request body / query / header that isn't authenticated. Check command and DTO surfaces. |
| **SQL injection** | EF Core LINQ or `FromSqlInterpolated`; never raw string concatenation. Raw SQL has parameters. |
| **XSS** | No `dangerouslySetInnerHTML` outside a sanitisation wrapper; CSP nonces in place; markdown via allowlist. |
| **CSRF** | Server Actions / Auth.js session check; non-Action mutating fetches carry CSRF tokens. |
| **Secrets** | No secret in source / appsettings / env-file-committed; reads through `ISecretProvider`. No log of token / password / national id. |
| **PII redaction** | `[PiiSensitive]` fields stripped from audit snapshots; logs don't carry raw PII. |
| **File upload** | MIME sniff + extension allowlist + size limit + EXIF strip + tenant-scoped key. |
| **Webhook receivers** | HMAC verification + replay protection + tenant id from stored provider account, never from payload. |
| **Hub HTTPS surface** | The 4-endpoint set unchanged unless a new ADR added a 5th. mTLS + JWT + HMAC verifications all run. |
| **Two Keycloak realms** | `learnstack-hub` realm tokens rejected on tenant-facing endpoints; `learnstack` realm tokens rejected on `/api/internal/*`. |
| **CORS** | Default deny; explicit allow-list per environment. |

Anything that fails a check is a **Blocker** finding; record it precisely
(file:line, problem, suggested fix).

### Step 4 — Bug / potential-bug lens

Walk the diff for correctness issues. Common shapes:

| Pattern | What to check |
|---------|---------------|
| **Null handling** | New nullable property carrying through control flow; null-guard on inputs at module boundaries. |
| **Off-by-one** | Pagination, range, percentage-band thresholds (Phase 10 placement-test bands are a classic). |
| **Async/await** | `.Result` / `.Wait()` deadlock risk; cancellation token threaded through; `ConfigureAwait(false)` where the library expects it. |
| **Race condition** | Read-modify-write without optimistic concurrency (`row_version`) or a transaction. Cross-instance state without `ICacheService` invalidation. |
| **Resource leak** | `DbContext` / `HttpClient` / file stream not disposed; long-lived `DaprClient` reuse via DI, not `new`. |
| **Idempotency** | Command with external side effects (LiveKit room, Stripe charge) missing `Idempotency-Key` handling. Integration-event consumer missing `IInboxGuard.IsAlreadyProcessedAsync`. |
| **Outbox atomicity** | Outbox row written in the same `DbContext` SaveChanges as the aggregate. No separate transaction. |
| **Tenant context restoration** | Background job + integration-event handler restores `TenantContext` before any work. |
| **Edge cases** | Empty list, zero, negative, max-int, very-long string. Test or guard. |
| **Time / clock** | Hard-coded UTC / DateTime.Now / DateTime.UtcNow vs `IClock` injection. |
| **Exception vs Result** | Expected failures use `Result.Fail`; exceptions reserved for unexpected. ProblemDetails mapping correct. |

Each finding → severity (Blocker / Major / Minor) + concrete fix.

### Step 5 — Optimisation lens

| Pattern | What to check |
|---------|---------------|
| **N+1 query** | `.Where(...).ToList()` then `.Select(x => ...load related)` inside the loop. Use `.Include` / projection. |
| **Premature materialisation** | `.ToList()` before final `.Where` / `.Select`. Keep `IQueryable` until the boundary. |
| **Missing index** | New query predicate on a non-indexed column for a `[TenantOwned]` table. `tenant_id` is index column 1; check the rest. |
| **Cache stampede** | `ICacheService.GetOrSetAsync` factories that are heavy and called concurrently for the same key. Single-flight pattern. |
| **Hot-path entitlement reads** | `IFeatureFlags.IsEnabledAsync` called many times in a request → cache the answer for the request. |
| **Bundle size (FE)** | Large dependency imported into a public-route Client Component. Lighthouse JS budget violated. |
| **Re-render storm (FE)** | Context value computed inline in the provider — every consumer re-renders on every render. |
| **Suspense boundary missing** | Server Component awaits slow data on the critical path; missing `<Suspense>` for streaming. |
| **Per-tenant SSR cardinality** | Cache key includes tenant + org + locale + slug; reviewer can confirm memory budget. |

### Step 6 — Refactor / Clean Code lens

Apply this lens **only** to code touched by the diff — don't bundle drive-by
cleanups, but flag them as follow-ups.

| Pattern | What to check |
|---------|---------------|
| **Single responsibility** | Function does one thing; class owns one concern. A 200-line handler probably has three handlers inside it. |
| **Naming** | Strongly-typed ids (no raw `Guid` in commands). Action names from the closed permission set. No domain-flavoured names in core (forbidden). |
| **Magic strings / numbers** | Constants or `FeatureKey` value objects. |
| **Cyclomatic complexity** | Nested `if` / `switch` chains; consider polymorphism or pattern-matching. |
| **Duplication** | Three similar lines is fine; three similar blocks invite a helper. Don't pre-abstract. |
| **Dead code / commented-out** | Delete on sight. |
| **`TODO` without date+owner** | Required format `// TODO(2026-05-19, @owner): …`. |
| **Long parameter lists** | > 4 parameters → group into a record / DTO. |
| **Public surface bloat** | `internal` by default; `public` only when another module / consumer needs it. |
| **Feature flag for incomplete work** | Forbidden; incomplete work lives behind a branch, not a flag. |

### Step 7 — LearnStack-specific structural lens

This is the lens that generic reviewers miss. Walk:

- The architecture-test set: would any of them fail on this diff? (Run them if
  in doubt — see [run-tests-locally](../run-tests-locally/SKILL.md).)
- Four sanctioned cross-module mechanisms only. No fifth.
- `IModule.Register` / `RegisterPermissions` / `RegisterAuditCoverage` updated
  consistently.
- `docs/modules/<m>/audit.md` / `permissions.md` updated.
- For frontend changes: route group is correct, SDK is the only API path,
  middleware-resolved `x-tenant-id` / `x-organization-id` honoured, no
  hand-rolled `fetch('/v1/...')`.
- For customization changes: data-only, no domain term in core code.

If the change is doc-only, the equivalent checks: no `docs/analysis/` refs,
correct cross-links, glossary present for new terms, ADR cited where rules
change.

### Step 8 — Tests coverage lens

| Check | Standard |
|-------|----------|
| Isolation pair present for new `[TenantOwned]` entity | Always |
| Cross-org pair for `[OrganizationScoped]` | Always when applicable |
| Outbox round-trip test for new integration event | Always |
| Permission denied test for every new permission key | Always |
| Boundary tests for every DSL band threshold | Scoring / completion rules |
| Lighthouse / axe-core for public-route changes | Frontend — **from Phase 02d**; neither is wired today, so do not raise a Blocker for a missing run |

A change without tests is incomplete; flag as Blocker unless the user
explicitly deferred the test.

### Step 9 — Output: findings report

Group findings by severity:

```markdown
## Code review — <commit / branch / PR>

**Verdict.** <Approve / Request changes / Reject with rationale.>

### Blocker (must fix before merge)
- **[file:line]** <problem in one sentence>
  - Why: <one line>
  - Fix: <one line>
- …

### Major (should fix; clearly impacts quality)
- …

### Minor (style, naming, small refactor)
- …

### Suggestion (non-blocking; alternative approach)
- …

### Follow-ups (out of scope for this diff)
- …
```

Rules:

- Each finding is **actionable**. "This code is bad" is not a finding.
- Each finding cites file:line.
- Each Blocker has a concrete fix proposal.
- Style nitpicks live in `Minor`, never in `Blocker`.
- If you have zero Blockers, say "Approve" up front; reviewers who bury an
  approval under fifteen Minor points cost time.

### Step 10 — Output: review-agent prompt

When the output mode is `prompt`, compose a self-contained instruction for a
separate agent to run the review. Template:

````markdown
You are the code-review agent for LearnStack, a multi-tenant PaaS for building
education products. The repository is pre-implementation; current corpus is
documentation under `docs/`. Read `CLAUDE.md` first for hard rules and the
documentation layout.

## Scope of this review

Branch / commit / PR: `<value>`
Files: <list, or "every file in the diff">
Author intent: <one-paragraph restatement>

## What you must do

1. Read the change end-to-end with intent in mind. Do not judge before you
   understand what the change is trying to do.
2. Walk the LearnStack standards corpus under `docs/standards/` for the
   surfaces this diff touches. The skill at
   `.claude/skills/standards-check/SKILL.md` is the canonical walk.
3. Apply five lenses, in this order: **Security → Bugs / Potential bugs →
   Optimisation → Refactor / Clean Code → LearnStack-specific structural
   rules**.
4. Confirm test coverage matches the project's policy (isolation pairs,
   outbox round-trip, permission denied test, DSL boundary tests where
   applicable).
5. Run / inspect the architecture-test set's pass condition for the diff.

## Project-specific hard rules to enforce

- **Defense-in-depth tenant + organization isolation** (`[TenantOwned]` +
  `[OrganizationScoped]` + EF filter + RLS with canonical session vars
  `app.tenant_id` / `app.organization_id`).
- **Four cross-module communication mechanisms only** (application contract,
  domain event in-process, integration event via outbox + Dapr pub/sub,
  read-model projection). No fifth.
- **No domain-specific names in core code** (`Cefr`, `English`, `Yoga`,
  `Asana`, `Kyu`, `CodeChallenge` — all forbidden; live as
  `TenantContentType` / `TenantLevelTaxonomy` / `TenantScoringRule` data).
- **No `Verticals/` folder.** ADR-0018 superseded ADR-0011.
- **Hub HTTPS contract surface governed by two invariants** (the Hub stores no tenant
  content; every crossing goes through a named adapter — ADR-0034). Adding an endpoint
  requires a new ADR.
- **No direct `IConnectionMultiplexer` / `IDistributedCache` / `KafkaProducer` /
  `VaultClient` injection** (per [CLAUDE.md hard rules](../../../CLAUDE.md) and
  [20-infrastructure-stack.md § Forbidden](../../../docs/standards/20-infrastructure-stack.md)).
  Use `ICacheService` / `IEventBus` / `ISecretProvider`.
- **No `Dapr.Client.*` imports outside `LearnStack.Infrastructure.{Caching,Messaging,Secrets}`**
  — a separate rule per [ADR-0014 § Architecture tests](../../../docs/decisions/0014-adopt-dapr.md)
  and [29-dapr-integration.md § 8](../../../docs/architecture/29-dapr-integration.md);
  architecture test `Dapr_SDK_Types_NotImportedOutsideInfrastructure`.
- **No direct write to `audit_log` / `outbox_messages` /
  `platform_entitlement_cache`.** Use `IAuditStore` / `IOutbox` /
  `IEntitlementProvider.RefreshAsync`.
- **Two Keycloak realms** (`learnstack` for tenants, `learnstack-hub` for
  operators) — tokens must not cross.
- **No `docs/analysis/` paths in committed files.**

## Output format

Produce a Markdown report grouped by severity (Blocker / Major / Minor /
Suggestion / Follow-ups). Each finding cites file:line, states the problem in
one sentence, explains why in one line, and proposes a fix in one line.
Lead with a one-line **Verdict** (Approve / Request changes / Reject).

## Tone

- Be specific. "This is wrong" is not a finding.
- Be kind. Critique code, not people.
- Be context-aware. Read the surrounding doc / ADR before flagging.
- Be honest. If the diff is solid, say so up front and keep the report short.
````

Hand the prompt back to the user as a code-fence so they can copy and dispatch
the review agent.

## Validation

- Every lens (security / bugs / optimisation / refactor / LearnStack-specific
  / tests) walked, even if a lens has zero findings.
- Every finding is actionable, severity-tagged, file:line-cited.
- Verdict matches the findings (no "Approve" with three Blockers).
- For `prompt` mode: the dispatched agent can run the review without
  additional questions back.

## Common pitfalls

- **Reviewing without reading the intent.** First page → first finding. Avoid.
- **Nitpicks burying real issues.** Severity discipline is everything.
- **Generic checklists.** Walk the LearnStack-specific lens; that's the value
  add over a generic reviewer.
- **Bundling drive-by cleanups into Blockers.** Out-of-scope refactors go to
  Follow-ups.
- **Style preferences as Major.** They're Minor at most; often Suggestion.
- **Missing context in delegated prompts.** A review agent that has to ask
  "what does the project do?" is a prompt that under-specified context.
- **Self-review with affection bias.** Apply the same severity scale to your
  own diff that you'd apply to someone else's. The point of self-review is
  finding what the reviewer would.
