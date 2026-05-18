---
name: start-task
description: >
  Bootstrap any LearnStack task with the right reading order, scope check, and skill
  selection. USE FOR: the first thing you do on any non-trivial change — agent
  invocation that starts implementation, refactoring, documentation, or
  investigation. Pick the right workflow-specific skill afterward.
  DO NOT USE FOR: trivial one-line fixes, typo corrections, or follow-up turns
  inside a task already in flight.
---

# Starting a LearnStack task

## Purpose

Make sure every task starts from the project's current shape — not stale assumptions.
Read the right docs in the right order, confirm the task fits the current phase,
pick the workflow-specific skill, and align on the deliverable before touching code.

## When to use

- The user has just opened a task and the agent is about to start work.
- You're picking up a branch you didn't author and need to orient.
- The change touches a module / surface you have not modified before.

## When not to use

- One-line typo fixes or comment edits.
- Follow-up turns in a task you already scoped.
- Operations against `docs/analysis/` (gitignored scratchpad — different rules).

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Task description | Yes | What the user is asking for. |
| Touched module(s) | No | If known, use it to narrow the standards / architecture reading. |
| Current branch | No | If the branch is not `main`, scan recent commits before adding more. |

## Workflow

### Step 1: Read in this order

For any task, walk this list before doing anything else. Each step is short; you
should be reading-not-skimming:

1. [README.md](../../../README.md) — project direction at a glance.
2. [docs/architecture/01-platform-vision.md](../../../docs/architecture/01-platform-vision.md)
   — what we are building.
3. [docs/architecture/05-mvp-scope.md](../../../docs/architecture/05-mvp-scope.md) —
   what is **in** / **out** / **deferred**.
4. [docs/roadmap/README.md](../../../docs/roadmap/README.md) — current phase and
   its dependencies. Check whether your task belongs to the current phase or a
   future one.
5. [docs/standards/00-principles.md](../../../docs/standards/00-principles.md) — the
   beliefs every standard descends from.
6. [docs/glossary.md](../../../docs/glossary.md) — terminology. Don't redefine
   project terms; link to the glossary.

If the task touches a specific module, **additionally** read its architecture
doc(s) under `docs/architecture/NN-*.md`. The most load-bearing ones for
implementation:

- [03-module-boundaries.md](../../../docs/architecture/03-module-boundaries.md) —
  the four-package layout (`Application.Contracts` / `Application` / `Domain` /
  `Infrastructure`) every module follows; this is the same shape that
  [add-backend-module](../add-backend-module/SKILL.md) scaffolds when a brand-new
  module is needed.
- [09-tenant-isolation.md](../../../docs/architecture/09-tenant-isolation.md)
- [15-event-and-outbox.md](../../../docs/architecture/15-event-and-outbox.md)
- [32-tenant-customization-model.md](../../../docs/architecture/32-tenant-customization-model.md)

### Step 2: Check phase fit

Open [docs/roadmap/README.md](../../../docs/roadmap/README.md) and confirm:

- The current phase is the one that **owns** this work. If the work belongs to a
  later phase, stop and surface that explicitly — the user may want to defer.
- Any **pending ADRs** ([decisions/README.md § Open ADR Drafts](../../../docs/decisions/README.md))
  that block this phase are Accepted. If they aren't, surface that.

### Step 3: Confirm the change is allowed

Run the change through the **hard rules** in [CLAUDE.md § Hard rules](../../../CLAUDE.md):

- Does it introduce a domain-specific name (CEFR, asana, kyu/dan, code-challenge, …)
  in core code? → forbidden by ADR-0018; use tenant customization data instead.
- Does it add a 5th cross-module communication mechanism? → forbidden by ADR-0010.
- Does it add a 5th Hub HTTPS endpoint? → requires a new ADR (ADR-0019).
- Does it inject `IConnectionMultiplexer` / `IDistributedCache` / `KafkaProducer` /
  `VaultClient` directly? → forbidden; use `ICacheService` / `IEventBus` /
  `ISecretProvider` (ADR-0014, standards/20).
- Does it write `audit_log` / `outbox_messages` / `platform_entitlement_cache`
  directly? → forbidden; use `IAuditStore` / `IOutbox` /
  `IEntitlementProvider.RefreshAsync`.

If any answer is yes and the user hasn't already chosen a different path, **stop and
ask** rather than coding the wrong solution.

### Step 4: Pick the workflow skill

From the [skill catalogue](../README.md), pick the one whose `description` matches
the task. Common starting points:

| Task shape | Skill |
|------------|-------|
| End-to-end substantive work (the most common entry point — "implement / geliştir this") | [implement-task](../implement-task/SKILL.md) |
| New aggregate or table | [add-tenant-owned-entity](../add-tenant-owned-entity/SKILL.md) |
| New command / query handler | [add-mediatr-handler](../add-mediatr-handler/SKILL.md) |
| Inter-module event flow | [add-integration-event](../add-integration-event/SKILL.md) |
| New permission key | [add-permission](../add-permission/SKILL.md) |
| New feature / limit | [add-feature-key](../add-feature-key/SKILL.md) |
| New EF migration | [add-ef-migration](../add-ef-migration/SKILL.md) |
| New module scaffold | [add-backend-module](../add-backend-module/SKILL.md) |
| Frontend route | [add-frontend-route](../add-frontend-route/SKILL.md) |
| Page block | [add-page-block](../add-page-block/SKILL.md) |
| Tenant customization | [add-tenant-content-type](../add-tenant-content-type/SKILL.md), [add-tenant-scoring-rule](../add-tenant-scoring-rule/SKILL.md), [add-tenant-completion-rule](../add-tenant-completion-rule/SKILL.md) |
| Integration test | [add-integration-test](../add-integration-test/SKILL.md) |
| ADR-worthy decision | [write-adr](../write-adr/SKILL.md) |
| Reviewing a diff / PR (security + bugs + optimisation + refactor) | [code-review](../code-review/SKILL.md) |
| Conformance audit against standards + hard rules | [standards-check](../standards-check/SKILL.md) |

For substantive work that touches code + tests + docs and needs a clean commit,
`implement-task` is the canonical end-to-end workflow — it wraps the scoping you
just did, the workflow-specific skill, the self-check, the linter / test run,
the documentation sweep, the commit, and the review-prompt step into one
disciplined pass.

If no skill matches, default to the relevant standard
([docs/standards/](../../../docs/standards/README.md)) and an architecture doc; flag
the gap so a new skill can be added later.

### Step 5: Plan before writing

For non-trivial work, briefly state:

1. **What** you're going to do (1 sentence).
2. **Which files** you'll touch (paths, not contents).
3. **Which validation** you'll run before declaring done (tests, lint, doc-link
   check, architecture tests).

Ask for confirmation when the plan touches:

- More than one module's `Domain` namespace.
- Any `Accepted` ADR.
- The Hub contract surface.
- The customization aggregates' schemas in a non-additive way.

## Validation

- You have an open editor on the standard(s) and architecture doc(s) that govern the
  change.
- You can name the phase the task belongs to.
- You can cite the skill you're about to run (or note that none matched).
- You have not started writing code yet.

## Common pitfalls

- **Skipping the roadmap check.** Phase 06 work in a Phase 04 branch creates
  cascading conflicts.
- **Treating `docs/analysis/` as authoritative.** It is gitignored research; never
  reference its paths from committed files.
- **Picking a skill on the description alone.** Read the SKILL.md's
  "When not to use" section too — many tasks look similar.
- **Re-reading every standard for every task.** Read the ones that govern the
  change; trust the corpus has been kept consistent.
