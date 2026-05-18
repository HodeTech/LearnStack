# Phase 08a: Assessment, Notifications, and Background Jobs

## Goal

Make the learning experience interactive without taking on live classroom complexity in the same milestone.

This phase covers assessment, notification workflows, background job foundations, and the event/outbox pipeline that later supports live sessions and recording jobs.

## Scope

### Assessment

- Assessment model.
- Question bank.
- Question model.
- Supported question types:
  - Single choice
  - Multiple choice
  - True/false
  - Short answer placeholder
  - Ordering placeholder
  - Matching placeholder
- Attempt.
- Attempt answer.
- Score.
- Pass/fail rule.
- Result visibility.

### Placement Test Readiness

The core remains generic per
[ADR-0018](../decisions/0018-tenant-driven-customization-model.md).

Core provides:

- Assessment, attempt, attempt-answer, score primitives.
- Attempt lifecycle (started → in-progress → submitted → graded → published).
- A **scoring engine** that evaluates `TenantScoringRule` DSL expressions against an
  attempt's answer map and returns a structured result. Built-in primitive operators
  cover sum, weighted-sum, threshold-band, and lookup-table operations; tenant rules
  compose these.
- Result-band → recommendation projection (e.g. a band `{min, max} → level_key`
  mapping that resolves against the tenant's `TenantLevelTaxonomy`).

The **English tenant** (Phase 10) ships its placement test as:

- A `TenantScoringRule` row that weights grammar / vocabulary / listening / speaking
  sections and emits a CEFR level recommendation.
- A `TenantLevelTaxonomy` row declaring CEFR (A1, A2, B1, B2, C1, C2).
- A `TenantContentType` for `SpeakingPrompt`, `VocabularyCard`, `GrammarTopic`
  question content.

A **yoga tenant** ships the same kind of placement test with completely different
data (sections: balance / flexibility / breath; result-band → asana-difficulty
recommendation) — **same code path, different `TenantScoringRule` and
`TenantLevelTaxonomy` rows**.

### Notifications

- Notification dispatch orchestration in the Notifications module.
- Channels: email (wired), SMS placeholder, WhatsApp placeholder, in-app placeholder.
- Notification preference per user.
- Dispatch job (Hangfire-backed).
- Delivery status.
- Template resolution via **`TenantTemplateLibrary`** (the
  customization-module-owned aggregate scaffolded in Phase 02a) — per-channel,
  per-locale, optional per-organization override. Templates author body + subject in
  Liquid / Handlebars; the dispatcher renders against the dispatch context.

### Notification Use Cases

- Invitation sent.
- Password reset.
- Course enrollment.
- Lesson reminder.
- Assessment completed.
- Live session reminder events prepared for Phase 08b.

### Background Jobs

- Hangfire setup.
- Tenant-aware job payload.
- Retry policy.
- Dead-letter state.
- Outbox dispatcher baseline.
- Job observability.

## Deliverables

- Assessment MVP.
- Notification engine MVP.
- Background job foundation.
- Outbox dispatcher baseline.
- Provider adapter foundation for notifications.

## Completion Criteria

- Admin can create a quiz.
- Learner can complete an assessment attempt and view the result.
- Notification templates can drive at least one email flow.
- Background jobs preserve tenant context.
- Retry and failure behavior is observable.
- Outbox-dispatched integration events are idempotent.

## Risks

- Building a full assessment engine too early.
- Hardcoding notification providers into core logic.
- Putting any domain-specific placement-test rule (CEFR mapping, asana difficulty,
  kyu/dan progression, …) into the core Assessment module. All such rules live as
  `TenantScoringRule` rows. The architecture test
  `Core_Modules_HaveNo_DomainSpecific_Names` enforces this.
- Running background jobs without tenant context.
- Choosing a scoring DSL engine ad-hoc — the engine choice is an ADR-pending item
  that **must land before this phase implements** the scoring engine. See
  [decisions/README.md § Open ADR Drafts](../decisions/README.md).

