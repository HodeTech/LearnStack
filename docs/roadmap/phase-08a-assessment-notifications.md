# Phase 08A: Assessment, Notifications, and Background Jobs

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

The core remains generic.

Core provides:

- Assessment.
- Scoring.
- Result bands.
- Attempt lifecycle.

The English vertical adds:

- CEFR mapping.
- Grammar, vocabulary, listening, or speaking sections.
- Level recommendation rules.

### Notifications

- Notification template.
- Notification channel:
  - Email
  - SMS placeholder
  - WhatsApp placeholder
  - In-app placeholder
- Notification preference.
- Dispatch job.
- Delivery status.
- Template variables.

### Notification Use Cases

- Invitation sent.
- Password reset.
- Course enrollment.
- Lesson reminder.
- Assessment completed.
- Live session reminder events prepared for Phase 08B.

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
- Putting English-specific placement-test rules into the core assessment module.
- Running background jobs without tenant context.

