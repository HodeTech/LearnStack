---
name: standards-check
description: >
  Validate a change against the LearnStack standards corpus and the project's
  hard rules — the mechanical checklist a careful reviewer runs before merge.
  USE FOR: pre-commit conformance pass on your own diff, gap analysis between
  a draft (code or docs) and the corpus, identifying which standards / ADRs a
  change needs to cite or update, walking a PR's structural compliance before
  the deeper `code-review`. DO NOT USE FOR: code-quality / bug / security
  review (use `code-review` — broader scope), automated test execution (use
  `run-tests-locally`), introducing a brand-new rule (use `write-adr` to add
  the rule first; this skill enforces existing rules), or scoping a task
  before knowing what to do (use `start-task`).
---

# Standards check

## Purpose

Walk every applicable LearnStack standard + the hard-rule list in
[CLAUDE.md](../../../CLAUDE.md) over a specific diff and report gaps. The skill
is intentionally **mechanical**: it asks "does the change satisfy rule X?",
not "is the design good?". The latter is [code-review](../code-review/SKILL.md).

A clean `standards-check` is a **necessary but not sufficient** condition for
shipping; a clean `code-review` covers the rest.

## When to use

- Before commit, after you implemented and self-reviewed for quality.
- Reviewing a PR: run this first; the structural gate is fastest.
- Auditing a draft document for corpus consistency.
- Inheriting an unfamiliar branch and wanting a one-pass gap list.

## When not to use

- Code-quality review — use [code-review](../code-review/SKILL.md).
- Running the test suite — use
  [run-tests-locally](../run-tests-locally/SKILL.md).
- Defining a new rule — the rule must already exist in the corpus (an
  Accepted ADR or a standard). Use [write-adr](../write-adr/SKILL.md) to add
  it first.
- Triaging a failing CI architecture test — fix the underlying violation; the
  test message already names the rule.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Scope | Yes | Branch / commit range / PR / file list. |
| Change shape | Yes | Code (backend / frontend) / docs / both. Drives which standards apply. |
| Touched modules / route groups | No | Narrows the audit-matrix + permission-matrix walks. |
| Output format | Yes | `report` (gap list) or `pass` (boolean + log). |

## Workflow

### Step 1 — Classify the change shape

What does the diff touch?

| Shape | Treat as |
|-------|----------|
| `.cs` under `backend/src/Modules/*/Domain/` | Backend domain layer. |
| `.cs` under `backend/src/Modules/*/Application/` | Backend application layer. |
| `.cs` under `backend/src/Modules/*/Infrastructure/` | Backend infrastructure. |
| `.cs` under `backend/src/*/Migrations/` | EF migration — RLS + naming rules. |
| `.tsx` / `.ts` under `frontend/apps/web/` | Frontend. |
| `.md` under `docs/architecture/` | Architecture doc. |
| `.md` under `docs/decisions/` | ADR. |
| `.md` under `docs/standards/` | Standards doc. |
| `.md` under `docs/roadmap/` | Phase doc. |
| `.md` at the root | CLAUDE.md / AGENTS.md / README.md / glossary. |
| `infra/` | Operational infra (compose, Dapr components, APISIX). |
| `.claude/skills/` | Skill catalogue. |

A diff can span multiple shapes — walk each shape's rule set.

### Step 2 — Hard rules pass (every diff)

Walk every item in [CLAUDE.md § Hard rules](../../../CLAUDE.md) and
[CLAUDE.md § Things to never do](../../../CLAUDE.md). For each, mark
✅ / ❌ / N/A:

- [ ] **English documentation language.**
- [ ] **Mermaid diagrams** in fenced blocks with text fallback.
- [ ] **Single source of truth.** No duplicated content across docs;
  link instead.
- [ ] **ADR numbers sequential, never reused.** Any new ADR uses the next
  free number or a reserved slot from `decisions/README.md`.
- [ ] **Standards changes cite an ADR** when non-trivial.
- [ ] **Four cross-module mechanisms.** No fifth.
- [ ] **Tenant + organization isolation defense-in-depth from day one** —
  marker + filter + RLS + arch test wherever a tenant-owned entity lands.
- [ ] **Self-hosted infrastructure preferred** (Keycloak with two realms,
  LiveKit OSS, SeaweedFS, Meilisearch, Kafka, Vault).
- [ ] **No domain-specific code in any module.** Domain shape = tenant data
  per ADR-0018.
- [ ] **Irreversible now, additive on demand — the one-way-door test**
  (ADR-0035). Tenant + org isolation, the outbox table, typed IDs, the
  localization schema, audit *correctness*, and the foundation **ports** are
  Phase 02a. The Dapr / Kafka / APISIX / Vault **adapters**, the Hub
  integration, signed licence keys, custom-domain TLS automation and
  `audit_log` partitioning are demand-gated to their owning phase against a
  written trigger — shipping them early is a finding, not a bonus.
- [ ] **Provider adapters everywhere.** No SaaS lock-in in Domain /
  Application.
- [ ] **Hub contract surface honours its two invariants** (ADR-0034): the Hub
  stores no tenant content, and every crossing goes through
  `IEntitlementProvider` / `IUsageReporter` / `IHubTenantSync`. Nothing else
  holds a Hub client; nothing resolves a host by calling the Hub. Adding an
  endpoint still needs an ADR — it is a cross-repository contract.
- [ ] **Three deployment modes, one binary.** Module code never branches on
  `DeploymentMode`.

Things to never do (forbidden — block the change if any tripped):

- [ ] Edit an Accepted ADR's Decision section (add an Amendment instead, or
  write a superseding ADR).
- [ ] Introduce a 5th cross-module communication mechanism.
- [ ] Add domain-specific code (CEFR, exam, English placement, kyu/dan,
  asana, code-challenge, …) to any module.
- [ ] Add a 5th Hub HTTPS endpoint without an ADR.
- [ ] Call Hub endpoints outside the dedicated adapters
  (`IEntitlementProvider` / `IUsageReporter` / `IHubTenantSync`).
- [ ] Inject `IConnectionMultiplexer` / `IDistributedCache` /
  `KafkaProducer` / `VaultClient` directly.
- [ ] Read `DeploymentMode` from inside a module.
- [ ] Write `audit_log` / `platform_entitlement_cache` / `outbox_messages`
  directly.
- [ ] Accept `learnstack-hub` realm tokens on tenant-facing endpoints; accept
  `learnstack` realm tokens on `/api/internal/*`.
- [ ] Reuse an ADR number.
- [ ] Add an architecture / standard doc that duplicates ownership.
- [ ] Mention a feature as "deferred to a later phase" without naming the
  owning phase.
- [ ] Reference `docs/analysis/` from a committed file.

A single ❌ on the forbidden list is a Blocker — stop and surface to the user
before continuing the rest of the walk.

### Step 3 — Standards corpus walk

For each applicable standard, run the listed checks. Skip standards whose
domain the diff doesn't touch.

#### `00-principles.md`
- [ ] Change does not violate any of the 15 principles. Particularly:
  Core Stays Generic (1), Tenant Isolation Is a Boundary Condition (2),
  Modules Talk Through Contracts (3), Providers Are Adapters (4), Explicit
  Over Implicit (5), Single Source of Truth (14).

#### `01-architecture-standards.md`
- [ ] New backend code follows the four-package layout
  (`<Module>.Application.Contracts` / `Application` / `Domain` /
  `Infrastructure`).
- [ ] Dependency direction respected (Domain → Kernel only; Application →
  Domain + others' Contracts; Infrastructure → Application + provider SDKs).
- [ ] No cross-module Domain / Infrastructure import.
- [ ] Aggregate root is the only entry point for state changes inside its
  aggregate.
- [ ] Distributed-Consistency Tier is stated for any cross-boundary command
  (1 / 2A / 2B / 3 in code comment or XML doc).
- [ ] No `LearnStack.Verticals.*` namespace / folder.

#### `02-backend-coding.md`
- [ ] Strongly-typed ids in command / query / DTO surfaces (no raw `Guid`).
- [ ] Records for DTOs / commands / queries.
- [ ] `Result<T>` for expected outcomes; exceptions only for unexpected.
- [ ] Nullable reference types respected.
- [ ] `IClock` / `IGuidFactory` injected, not static `DateTime.UtcNow` /
  `Guid.NewGuid()` in domain logic.

#### `03-frontend-coding.md`
- [ ] TypeScript strict mode honored.
- [ ] No `any` without an inline justification.
- [ ] No hand-rolled `fetch('/v1/...')`; SDK is the only API path.
- [ ] No `dangerouslySetInnerHTML` outside a sanitisation wrapper.
- [ ] Hooks called at the top level, conditional hooks absent.

#### `04-api-design.md`
- [ ] URL versioning `/v1/...`.
- [ ] Problem Details (RFC 7807) for errors.
- [ ] Cursor pagination for list endpoints.
- [ ] Idempotency-Key header on write endpoints with external side effects.
- [ ] ETag / `If-Match` on aggregates with `row_version`.
- [ ] Correlation ID present + logged.

#### `05-database.md`
- [ ] Naming conventions (snake_case, plural tables, `ix_` / `ux_` / `ck_` /
  `tg_` / `fn_` prefixes).
- [ ] Every `[TenantOwned]` table has `tenant_id` + index, RLS enabled,
  policy keyed on `current_setting('app.tenant_id')`.
- [ ] Every `[OrganizationScoped]` table additionally has nullable
  `organization_id` + index + RLS policy on `app.organization_id`.
- [ ] Mutable aggregates carry the audit columns (`created_at` /
  `created_by` / `updated_at` / `updated_by` / `row_version`).
- [ ] Migrations forward-only by default; destructive change has a two-step
  plan documented.
- [ ] PgBouncer transaction-pooling assumption respected (no statement-mode
  patterns).
- [ ] `correlation_id` columns are `text NULL` (canonical type).

#### `06-testing.md`
- [ ] Test pyramid respected (unit > integration > E2E in volume).
- [ ] Every `[TenantOwned]` entity ships with the mandatory isolation pair
  in `LearnStack.Tests.Integration`.
- [ ] Architecture tests **non-skippable** — no `[Skip]` / `[Fact(Skip=…)]`.
- [ ] Coverage targets respected: Domain ≥ 90%, Application ≥ 80%,
  Infrastructure ≥ 50%.
- [ ] Real Postgres via Testcontainers for integration tests; no in-memory
  substitution.

#### `07-frontend-architecture.md`
- [ ] Route group correct (`(public)` / `(studio)` / `(portal)`).
- [ ] Server Components default; `"use client"` only when needed.
- [ ] Tenant + organization + locale resolution via middleware; not
  re-implemented in the page.
- [ ] Folder structure under `frontend/apps/web/`.

#### `08-localization.md`
- [ ] No `if (locale === "xx") ...` branching.
- [ ] ICU MessageFormat for plurals / select.
- [ ] Keys feature-namespaced, snake_case.
- [ ] `I18n:` trailer present if user-facing keys added / renamed / removed.

#### `09-error-handling.md`
- [ ] Expected outcomes use `Result.Fail(LocalizedMessage.Of(...))`.
- [ ] `LocalizedMessage` keys carry the `lockey_*` prefix on the wire.
- [ ] Exceptions for unexpected only; mapped to Problem Details by the
  pipeline.
- [ ] Frontend error boundaries (`error.tsx`) per route group.

#### `10-observability.md`
- [ ] Correlation id propagated across spans + logs.
- [ ] Structured logs (no string interpolation of fields).
- [ ] Tenant id never used as a high-cardinality metric label.
- [ ] OpenTelemetry traces emitted for handlers, outbox dispatch,
  integration-event consumption.

#### `11-security.md`
- [ ] 4-step auth order on every write (Authn → Tenant membership →
  Role/permission → Resource scope).
- [ ] Tenant id from JWT only, never from request body.
- [ ] Secrets via `ISecretProvider`; no plaintext in source / `appsettings`.
- [ ] Secure headers set (HSTS, CSP, COOP, CORP).
- [ ] File-upload validation (MIME sniff, size, EXIF strip, scoped key).
- [ ] Two-realm separation enforced.

#### `12-infrastructure.md`
- [ ] Container hygiene (pinned base image, non-root, read-only FS where
  feasible, dropped caps).
- [ ] Deployment-mode composition correct (Development / SaaS / Dedicated /
  SelfHosted).
- [ ] Migrations run as a separate job before app start.

#### `13-documentation.md`
- [ ] ADR template followed (Decision Drivers + Considered Options).
- [ ] Standards have `Derives from:` header.
- [ ] Diagrams in Mermaid with text fallback.
- [ ] Glossary updated for new project-specific terms.
- [ ] **No `docs/analysis/` references in committed files.**

#### `14-git-workflow.md`
- [ ] `type(scope): subject` commit format; subject imperative ≤ 72 chars.
- [ ] AI-assisted commit carries `Co-Authored-By` trailer (Claude or Codex).
- [ ] `ADR:` / `Module:` / `I18n:` trailers where applicable.
- [ ] No `--force` on `main`; no `--amend` on a published commit.

#### `15-performance.md`
- [ ] Public-route Lighthouse budgets respected (LCP < 2.5s, INP < 200ms,
  CLS < 0.05).
- [ ] Backend latency budget per module respected.

#### `16-accessibility.md`
- [ ] WCAG 2.2 AA target.
- [ ] `axe-core` clean on changed components.
- [ ] Keyboard navigation + focus order reviewed.
- [ ] Color contrast verified per brand token.

#### `17-code-review.md`
- [ ] None of the **Automatic blockers** triggered (missing `[TenantOwned]`,
  `IgnoreQueryFilters` outside platform-admin, `app.tenant_id` not set,
  secret committed, tenant id from request body, provider SDK in
  Domain/Application, job without TenantId, …).

#### `18-audit-coverage.md`
- [ ] Module's `docs/modules/<m>/audit.md` matrix updated for new
  operations.
- [ ] MUST / SHOULD / MAY classification matches the operation class
  (`create` / `update` / `delete` / `read-sensitive` / `security-event` /
  `platform-admin`).
- [ ] No direct `IAuditStore.WriteAsync` call from outside the audit
  infrastructure.
- [ ] `[PiiSensitive]` fields redacted in snapshots.

#### `19-permissions.md`
- [ ] Permission key matches `{module}.{resource}.{action}` and `action` is
  in the closed set (`read` / `write` / `delete` / `admin`).
- [ ] Scope (Platform / Tenant / Organization) declared at registration.
- [ ] Module's `docs/modules/<m>/permissions.md` matrix updated.
- [ ] Endpoint has `[Authorize(Policy = "...")]` matching the registered
  key.
- [ ] Denied test present per registered key.

#### `20-infrastructure-stack.md`
- [ ] `IEventBus` / `ICacheService` / `ISecretProvider` used; direct SDK
  injection absent.
- [ ] Topic naming `learnstack.{module}.{aggregate}`.
- [ ] APISIX in standalone YAML-reload mode; routes under `infra/apisix/` per
  [30-api-gateway.md § 2](../../../docs/architecture/30-api-gateway.md).
- [ ] Hub contract surface still satisfies ADR-0034's two invariants; any new
  endpoint carries its ADR and lands in both repositories.
- [ ] Outbox + inbox usage correct (atomic with aggregate write; inbox guard
  in every consumer).

### Step 4 — Architecture-test coverage

For each rule introduced or implied by the diff:

- [ ] Does an existing architecture test enforce it? If yes, will it pass on
  this diff?
- [ ] If the rule is new, is the architecture test scheduled (use
  [add-architecture-test](../add-architecture-test/SKILL.md))?

If the architecture-test set wouldn't catch a regression of the rule, the
rule is effectively documentation-only; flag this.

### Step 5 — Cross-document update audit

A change in one corner of the corpus often needs sibling updates. Walk this
checklist:

- [ ] New domain term → glossary updated.
- [ ] New ADR → `decisions/README.md` index updated.
- [ ] Standard changed → its `Derives from:` header references the relevant
  ADR.
- [ ] Architecture doc changed → cross-references to standards + ADRs
  refreshed.
- [ ] Phase deliverable changed → `docs/roadmap/phase-NN-*.md` updated.
- [ ] Module added / module spec changed → `docs/modules/<m>/audit.md` and
  `docs/modules/<m>/permissions.md` updated.
- [ ] Frontend i18n key changed → `I18n:` commit trailer present.
- [ ] Hub-side change in this repo → flagged for coordination with
  `learnstack-hub` repo (the contract surface is shared; see ADR-0034).

### Step 6 — Output

For `report` mode, group gaps by severity:

```markdown
## Standards check — <commit / branch / PR>

**Verdict.** <Pass / Pass with minor gaps / Fail.>

### Blocker (hard-rule violation — forbidden by CLAUDE.md or an Accepted ADR)
- **[file:line or doc]** <rule> — <how it's violated> — <fix>

### Major (standard violation requiring change before merge)
- …

### Minor (small gap; consistent fix expected but not strictly blocking)
- …

### Cross-document update needed (sibling docs out of sync)
- …

### Notes (informational; no action required)
- …
```

For `pass` mode, return a one-line verdict and the gap count by severity.

## Validation

- Every applicable standard walked, even with zero findings (silence is
  ambiguous; affirmative pass is not).
- Every Blocker / Major gap has a concrete fix proposal.
- No gap is silently dropped — Minor and informational gaps are visible.
- The verdict matches the gaps (no "Pass" with three Blockers).

## Common pitfalls

- **Stopping at the hard-rule pass.** Hard rules are a floor; per-standard
  rules add depth. Walk both.
- **Treating "no architecture test for this rule" as "rule doesn't apply".**
  It applies; the test gap is itself a finding.
- **Skipping the cross-document audit.** A change that's locally correct but
  doesn't propagate to glossary / matrix / ADR creates rot.
- **Conflating with code review.** This skill is mechanical — "does the rule
  hold?". Quality / design / security depth is
  [code-review](../code-review/SKILL.md)'s job.
- **Flagging style preferences.** This skill enforces written rules; if a
  rule isn't in the corpus, don't flag it — open an ADR or a PR against the
  standard.
- **Domain-flavoured names slipping through.** Easy to miss; grep the diff
  for `english`, `cefr`, `yoga`, `asana`, `kyu`, `dan`, `code_challenge`
  if you have any doubt.
- **`docs/analysis/` residual references.** Common slip in copy-paste from
  research notes. Re-scan before declaring pass.
