# LearnStack Agent Skills

This directory contains reusable, task-focused instruction packs ("skills") for AI
coding agents working on LearnStack. Each subdirectory is a single skill; its
`SKILL.md` carries YAML frontmatter (`name`, `description`) that lets the agent
runtime pick or skip the skill without reading the whole body.

Skills are **project-local** and live in this repository (`.claude/skills/`). They
apply to every agent runtime that respects the Claude Code / `AGENTS.md` skills
convention.

## How to use

### Which skill runs first?

Pick the entry point that matches the user's intent. Only **one** entry point
runs per task — it dispatches the rest internally.

| User's intent | Entry point | What it does |
|---------------|-------------|--------------|
| "Implement / geliştir / yap / ekle / refactor X" (substantive work) | **[implement-task](implement-task/SKILL.md)** | The default for any non-trivial change. Step 1 dispatches `start-task` internally; subsequent steps walk the workflow-specific skill, run linter + tests, update docs, commit, and produce a review-agent prompt. |
| "Plan / scope / orient me on X, don't implement yet" | [start-task](start-task/SKILL.md) | Standalone scoping pass. Reading order + hard-rule walk + which workflow-specific skill the implementation will eventually need. Stops when the plan is clear. |
| "Review this diff / PR" | [standards-check](standards-check/SKILL.md), then [code-review](code-review/SKILL.md) | Run `standards-check` first (5-min mechanical conformance gate), then `code-review` (security + bug + optimisation + refactor + LearnStack-specific lenses). |
| "Explain / what is …" (informational) | none — answer directly | Don't load a skill to chat. |
| One-line typo / comment fix | none — edit directly | Skills are for workflows, not single-line edits. |

### Why `implement-task` is the default

The most common pattern in this project is: user describes a task →
implementation → tests → docs → commit → review. Running each phase as a
separate skill (start → workflow → tests → docs → commit) is exactly what
`implement-task` orchestrates as a single disciplined pass. Calling
`implement-task` is **not** "running the long workflow on a small task" —
trivial steps (no docs to update, no migration, …) drop out naturally.

### Composition

- `implement-task` **dispatches** `start-task` in Step 1, then `add-*` workflow
  skill(s) in Step 4, then `run-tests-locally` in Step 6, then
  `update-glossary` / `commit-and-pr` / `code-review` where applicable.
- `start-task` is **standalone** — it stops at planning. Use it only when you
  explicitly want to scope without implementing.
- `code-review` and `standards-check` are **post-implementation gates**.
  `implement-task` Step 10 emits a review-agent prompt; the user dispatches a
  separate agent that runs the two review skills.

If no skill matches, default to the [Standards](../../docs/standards/README.md)
index plus the relevant [Architecture](../../docs/architecture/) doc.

## Skill catalogue

### Process

| Skill | When to use |
|-------|-------------|
| [implement-task](implement-task/SKILL.md) | The default entry point for substantive work — scope, implement, self-check, run linter + tests, update docs, commit, produce a review-agent prompt. |
| [start-task](start-task/SKILL.md) | Lightweight scoping-only entry point. Reading order + alignment check. Use when you don't need the full end-to-end workflow. |
| [write-adr](write-adr/SKILL.md) | Capturing a one-time architectural decision. Uses Decision Drivers + Considered Options template. |
| [commit-and-pr](commit-and-pr/SKILL.md) | Conventional Commit + AI trailer + PR body conventions. |
| [update-glossary](update-glossary/SKILL.md) | Introducing a new project-specific term anywhere in the corpus. |

### Review

| Skill | When to use |
|-------|-------------|
| [code-review](code-review/SKILL.md) | End-to-end review across security, bugs / potential bugs, optimisation, refactor / Clean Code, and LearnStack-specific structural rules. Also composes the review-agent prompt for delegation. |
| [standards-check](standards-check/SKILL.md) | Mechanical conformance pass against the standards corpus + CLAUDE.md hard rules. Narrower than `code-review` and faster; run it first. |

### Backend — core workflows

| Skill | When to use |
|-------|-------------|
| [add-backend-module](add-backend-module/SKILL.md) | Scaffolding a new modular-monolith module (`LearnStack.Modules.<Name>.*` four-package layout). |
| [add-tenant-owned-entity](add-tenant-owned-entity/SKILL.md) | Adding a `[TenantOwned]` / `[OrganizationScoped]` aggregate with EF filter + RLS migration + isolation tests. |
| [add-mediatr-handler](add-mediatr-handler/SKILL.md) | Adding a command or query handler that participates in the MediatR pipeline (Result, FluentValidation, audit). |
| [add-integration-event](add-integration-event/SKILL.md) | Publishing or consuming an integration event (outbox + inbox guard + Dapr topic). |
| [add-ef-migration](add-ef-migration/SKILL.md) | Producing an EF Core migration with RLS-aware patterns and the project naming convention. |
| [add-permission](add-permission/SKILL.md) | Registering a permission key with the closed action set and scope (Platform / Tenant / Organization). |
| [add-feature-key](add-feature-key/SKILL.md) | Adding a `FeatureKey` / `LimitKey` to the typed registry and wiring entitlement-projection reads. |
| [wire-dapr-pubsub](wire-dapr-pubsub/SKILL.md) | Declaring a Dapr pub/sub topic with the `learnstack.{module}.{aggregate}` convention and `InProcessEventBus` dev fallback. |

### Backend — guard rules

| Skill | When to use |
|-------|-------------|
| [add-architecture-test](add-architecture-test/SKILL.md) | Encoding a structural rule (cross-module reference ban, naming, marker presence) as a non-skippable test. |
| [add-audit-coverage](add-audit-coverage/SKILL.md) | Extending a module's MUST/SHOULD/MAY audit matrix and wiring `AuditLogBehavior` coverage. |

### Frontend

| Skill | When to use |
|-------|-------------|
| [add-frontend-route](add-frontend-route/SKILL.md) | Adding a route under `(public)` / `(studio)` / `(portal)` with tenant + organization + locale resolution. |
| [add-page-block](add-page-block/SKILL.md) | Built-in primitive block or tenant-defined `TenantPageBlock` row. Two-tier registry. |
| [add-i18n-key](add-i18n-key/SKILL.md) | Translation key with the proper namespace + ICU MessageFormat. |
| [add-feature-gated-ui](add-feature-gated-ui/SKILL.md) | Hiding / showing UI based on `useFeatureFlag(FeatureKey)` or `useLimit(LimitKey)`. |

### Tenant Customization

| Skill | When to use |
|-------|-------------|
| [add-tenant-content-type](add-tenant-content-type/SKILL.md) | Authoring a `TenantContentType` JSON Schema (data, not code). |
| [add-tenant-scoring-rule](add-tenant-scoring-rule/SKILL.md) | Authoring a `TenantScoringRule` DSL expression for assessment scoring. |
| [add-tenant-completion-rule](add-tenant-completion-rule/SKILL.md) | Authoring a `TenantCompletionRule` boolean expression for lesson / module / course completion. |

### Tests

| Skill | When to use |
|-------|-------------|
| [add-integration-test](add-integration-test/SKILL.md) | Testcontainers integration test with the mandatory tenant + organization isolation pair. |
| [run-tests-locally](run-tests-locally/SKILL.md) | Running unit / integration / architecture / E2E suites locally and interpreting failures. |

### Operational

| Skill | When to use |
|-------|-------------|
| [local-dev-setup](local-dev-setup/SKILL.md) | Bringing up the local stack: Postgres, Redis, Vault, Kafka, Dapr sidecar, APISIX, Keycloak, SeaweedFS, LiveKit, Meilisearch. |
| [seed-tenant](seed-tenant/SKILL.md) | Provisioning a tenant (with its default organization, customization data, seed users) for local development. |

## Authoring a new skill

Each `SKILL.md` carries YAML frontmatter:

```yaml
---
name: <skill-name>            # kebab-case; matches the directory name
description: >
  <one-line summary + when-to-use + when-not-to-use>
---
```

The body follows the structure: **Purpose**, **When to use** / **When not to use**,
**Inputs**, **Workflow** (numbered steps with checkpoints), **Validation**, **Common
pitfalls**. Keep each skill under ~250 lines; split into reference files in the
skill's directory when content grows. Prefer linking to the canonical doc (ADR,
standard, architecture doc) over copying its content.

> **Known overrun.** Three skills currently exceed the 250-line guideline:
> [standards-check](standards-check/SKILL.md) (~390), [code-review](code-review/SKILL.md)
> (~316), and [implement-task](implement-task/SKILL.md) (~297). The catalogue's
> documented remediation is to split the bulk of each into reference files in the
> skill's directory (e.g. `standards-check/checklists/NN-<name>.md`); this is a
> follow-up commit, not a blocker on the catalogue itself.

## What skills are not

- **Not duplicates of standards.** A skill is a *workflow* for a specific task, not a
  rewrite of [docs/standards/](../../docs/standards/). Skills cite standards; they
  don't restate them.
- **Not decisions.** Decisions live in [ADRs](../../docs/decisions/). A skill assumes
  the decision is made and walks the agent through executing the resulting work.
- **Not scratch space.** Exploratory notes belong in `docs/analysis/` (gitignored),
  not in skills.
