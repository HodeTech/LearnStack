# Phase 08a: Assessment, Notifications, and Background Jobs

## Goal

Make the learning experience interactive — quizzes, placement tests, and the messages
that surround them — without taking on live classroom complexity in the same milestone.

This phase adds three things a learning product needs once learners exist
([Phase 07](phase-07-enrollment-learner-portal.md)): a way to ask them questions and
score the answers, a way to tell them something happened, and the scheduled work that
carries both. All three run on infrastructure that already exists; this phase builds
domain capability on top of it, not new plumbing.

### What this phase does not own

Three mechanisms are routinely mis-attributed to this phase. Stated once, here, so no
reader and no later phase has to guess:

| Mechanism | Owning phase | What Phase 08a adds |
|---|---|---|
| Outbox — `outbox_messages` table, `OutboxFlushBehavior` | [Phase 02a](phase-02a-kernel-tenancy.md) | Nothing |
| Outbox dispatcher — polling, retry, backoff, dead-letter, inbox guard, `IEventBus` transport | [Phase 02b](phase-02b-events-auth.md) | Event types, topics, and idempotent consumers |
| Hangfire — host, Postgres storage, queue policy, tenant-aware job activator, enqueue-time `tenant_id` guard | [Phase 02b](phase-02b-events-auth.md) | Job definitions that run on it |
| Scoring / completion DSL engine — ADR-0025, sandbox, evaluator | [Phase 05](phase-05-education-learning-content.md) | Callers, answer maps, and result-band projection |

Phase 08a writes **no** dispatcher, **no** job host, and **no** expression evaluator. If
it does, each of those has two implementations that will drift apart, and the second one
is invisible until it disagrees with the first in production.

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

`Assessment`, `QuestionBank`, and `Attempt` are `[TenantOwned]`; `Assessment` is
additionally `[OrganizationScoped]` where a branch runs its own question set. Each
carries an EF global query filter and a Row Level Security policy from the canonical
template in [Database Standards](../standards/05-database.md), per
[ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md). Attempt
graded and score published are baseline MUST-audit operations
([18-audit-coverage.md](../standards/18-audit-coverage.md)), written as durable intent
inside the grading transaction per
[ADR-0033](../decisions/0033-audit-durability-model.md).

### Placement Test Readiness

The core remains generic per
[ADR-0018](../decisions/0018-tenant-driven-customization-model.md).

Core provides:

- Assessment, attempt, attempt-answer, score primitives.
- Attempt lifecycle (started → in-progress → submitted → graded → published).
- A **scoring path** that hands an attempt's answer map to the `TenantScoringRule`
  evaluator and stores the structured result.
- Result-band → recommendation projection (a band `{min, max} → level_key` mapping that
  resolves against the tenant's `TenantLevelTaxonomy`).

**The evaluator is not built here.** The DSL engine behind `TenantScoringRule` is chosen
in **ADR-0025** (open; see
[decisions/README.md § Open ADR Drafts](../decisions/README.md)) and its sandboxed
runtime ships in [Phase 05](phase-05-education-learning-content.md), one phase before
this one, because `TenantCompletionRule` needs the same engine. Rule bodies are stored as
opaque `text` with a `dialect` discriminator from
[Phase 02a Packet 8](phase-02a-kernel-tenancy.md) onward precisely so that this
dependency can be authored against before the engine exists.

Phase 08a is a **consumer**: it supplies the answer map, invokes the evaluator, and
projects the result onto a level recommendation. Built-in primitive operators (sum,
weighted sum, threshold band, lookup table) are part of the Phase 05 engine's operator
set, not a parallel implementation in Assessment.

The **English tenant** ([Phase 10](phase-10-english-learning-mvp.md)) ships its placement
test as:

- A `TenantScoringRule` row that weights grammar / vocabulary / listening / speaking
  sections and emits a CEFR level recommendation.
- A `TenantLevelTaxonomy` row declaring CEFR (A1, A2, B1, B2, C1, C2).
- A `TenantContentType` for `SpeakingPrompt`, `VocabularyCard`, `GrammarTopic` question
  content.

The **yoga tenant** seeded in [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) ships the
same kind of placement test with completely different data (sections: balance /
flexibility / breath; result band → difficulty recommendation) — **same code path,
different `TenantScoringRule` and `TenantLevelTaxonomy` rows**. Both tenants exist by
this phase, so the claim is testable here rather than asserted.

### Notification Templates — `TenantTemplateLibrary`

`TenantTemplateLibrary` lands in this phase. It was scaffolded with the other
customization aggregates in [Phase 02a](phase-02a-kernel-tenancy.md)'s original plan and
re-homed to its consumer: the notification dispatcher is the only thing that reads it,
and an unread template store is a schema nobody validates.

- Owned by `LearnStack.Modules.Customization`, consistent with every other
  `Tenant*` aggregate ([32-tenant-customization-model.md](../architecture/32-tenant-customization-model.md)).
- Keyed by `(tenant_id, template_key, channel, locale)` with an optional
  per-organization override row that resolves ahead of the tenant default — a branch may
  sign its own reminders.
- Body and subject authored in Liquid / Handlebars; the dispatcher renders against the
  dispatch context.
- `[TenantOwned]` with query filter and RLS policy from the canonical template.
- Template created / updated / deleted is a baseline MUST-audit operation with both
  `before` and `after` snapshots ([18-audit-coverage.md](../standards/18-audit-coverage.md)) —
  a template is the text a learner receives, and changing it silently is a support
  incident.
- Resolution falls back locale → tenant default locale → built-in platform template, so a
  missing translation degrades to a delivered message rather than a dropped one.
- The Studio editor for this aggregate ships **here**, built on the shared customization
  editor components [Phase 06](phase-06-renderer-admin-studio.md) consolidates. The
  [Admin Studio screen ownership table](phase-06-renderer-admin-studio.md) is the single
  ownership record and assigns this screen to this phase — which is also the phase that
  first creates the aggregate, its migration, its policy and its audit coverage. A
  Phase 06 editor would target a table with no migration behind it.

### Notifications

- Notification dispatch orchestration in the Notifications module.
- Channels: email wired through the `IEmailProvider` adapter
  ([06-extension-model.md](../architecture/06-extension-model.md)); SMS, WhatsApp, and
  in-app are placeholders behind the same port shape.
- Notification preference per user, respected before dispatch.
- Dispatch runs as a Hangfire job on the host wired in
  [Phase 02b](phase-02b-events-auth.md) — this phase defines the job, its queue and its
  retry policy, not the runner.
- Delivery status, with failures visible rather than swallowed.
- Template resolution via `TenantTemplateLibrary`, above.

### Notification Use Cases

- Invitation sent.
- Password reset.
- Course enrollment.
- Lesson reminder.
- Assessment completed.
- Live session reminder trigger points prepared for
  [Phase 08b](phase-08b-scheduling.md), which supplies the events and template keys.

Each use case is driven by an integration event consumed from the outbox, not by a direct
call from the originating module. The producing module enqueues; Notifications consumes.
Consumers are idempotent through the `IInboxGuard` from
[Phase 02b](phase-02b-events-auth.md) — at-least-once delivery means "enrollment
confirmed" can arrive twice, and a learner must not receive it twice.

### Background Jobs

Additions to the existing host, not a new one:

- Notification dispatch job.
- Attempt-expiry job for abandoned attempts.
- Job-level retry policy and dead-letter state for this phase's jobs.
- Job observability: queue depth, execution duration, and failure counts surfaced
  through the existing OpenTelemetry pipeline.

Tenant-aware job payloads, the job activator, the enqueue-time `tenant_id` guard, and the
`Hangfire_Job_Payloads_Include_TenantId` architecture test all come from
[Phase 02b](phase-02b-events-auth.md). This phase's jobs inherit them and add no
infrastructure.

## Deliverables

- Assessment MVP: assessments, question bank, attempts, grading, result visibility.
- Placement-test path scored through the Phase 05 `TenantScoringRule` evaluator, with the
  result-band → level projection.
- `TenantTemplateLibrary` aggregate, migration, RLS policy, resolution order, and audit
  coverage.
- `TenantTemplateLibrary` Studio editor: per-channel, per-locale template authoring, the
  per-organization override row, and a render preview against a sample dispatch context.
- Notification engine MVP with the `IEmailProvider` adapter wired and other channels
  behind the port.
- Integration-event consumers for the notification use cases, idempotent via the inbox
  guard.
- Notification dispatch and attempt-expiry jobs on the Phase 02b Hangfire host.

## Completion Criteria

- An admin can create a quiz; a learner can complete an attempt and view the result.
- A placement-test attempt scores through a `TenantScoringRule` row and yields a level
  recommendation from the tenant's own `TenantLevelTaxonomy`.
- Both seed tenants run a placement test whose sections, bands, and recommended levels
  differ in shape, with no branch on tenant identity anywhere in the Assessment module.
- At least one email flow is driven end to end by a `TenantTemplateLibrary` template,
  including the per-organization override and the locale fallback.
- A tenant admin creates and edits a notification template in the Admin Studio, including
  a per-organization override, with no code change.
- A notification consumer that receives the same integration event twice sends one
  message.
- Background jobs preserve tenant context; retry and failure behaviour is observable in
  metrics and logs.
- MUST-class audit entries exist for attempt graded, score published, and template
  changed, committed with their business transactions.

## Risks

- **Building a full assessment engine too early.** The MVP question types are the closed
  set above; short answer, ordering, and matching stay placeholders until a tenant needs
  them.
- **Hardcoding notification providers into core logic.** Every channel goes through its
  port; the Notifications module must not import a provider SDK type.
- **Putting a domain-specific placement-test rule (CEFR mapping, asana difficulty,
  kyu/dan progression, …) into the core Assessment module.** All such rules live as
  `TenantScoringRule` rows. The architecture test
  `Core_Modules_HaveNo_DomainSpecific_Names` enforces it.
- **Re-implementing the scoring evaluator locally.** The likely trigger is a Phase 05
  evaluator that lacks one operator this phase wants. The fix is an operator added to the
  Phase 05 engine, not an expression parser in Assessment.
- **Rebuilding the dispatcher or the job host.** Both exist from Phase 02b. A "small
  local scheduler" for reminder emails is the classic version of this mistake, and it
  loses tenant context the first time it runs unattended.
- **Templates treated as configuration rather than content.** A template is user-visible
  text with a locale and an audit trail; editing it in a config file bypasses both.
- **Running background jobs without tenant context.** Mitigated by the Phase 02b activator
  and enqueue guard — provided this phase's jobs use the standard payload shape rather
  than passing ids by hand.

## Phase Exit Decision

[Phase 08b](phase-08b-scheduling.md) begins when a learner can complete an assessment and
receive its result, when a placement test scores through a tenant-authored
`TenantScoringRule` and returns a level from that tenant's taxonomy, when at least one
email flow renders from `TenantTemplateLibrary` and delivers through `IEmailProvider`,
when a redelivered integration event produces exactly one message, and when this phase's
jobs run on the Phase 02b Hangfire host with tenant context intact and failures visible.

Phase 08b depends on the notification engine directly — it contributes booking and
reminder trigger points and no dispatch machinery of its own — so a notification path that
is not yet end-to-end blocks it.
