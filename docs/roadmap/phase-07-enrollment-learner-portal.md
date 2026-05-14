# Phase 07: Enrollment, Learner Portal, and Progress Tracking

## Goal

Allow users to enter a real learning experience: course access, learner portal, lesson consumption, and progress tracking.

This phase moves the platform from CMS/catalog capabilities into the first learning product capabilities.

## Scope

### Enrollment

- Enrollment model.
- Manual enrollment (admin grants access directly).
- Invitation-based enrollment (existing Phase 03 invitation flow extends to course access).
- Readiness for program, course, and cohort access models.
- Enrollment status:
  - Active
  - Suspended
  - Completed
  - Cancelled
- Enrollment source enum is defined exhaustively but only `manual` and `invitation` are wired in this phase. Other sources land later:
  - `manual` — admin grants directly (here).
  - `invitation` — tenant invitation accepted with course attached (here).
  - `billing` — order paid emits an integration event consumed by Enrollment (Phase 09; see § Entitlements).
  - `bulk_import` — out of scope; not on the MVP path.
  - `integration` — external LMS / SSO push; deferred.

### Entitlements

Phase 07 owns the **Entitlement aggregate** and the **enrollment-source entitlement** path:

- The `Entitlement` aggregate (id, tenant, user, scope, source, granted-at, revoked-at).
- Source `manual` and `invitation` populated directly by this phase.
- Permission `enrollment.entitlement.read` / `enrollment.entitlement.write` (see [19-permissions.md](../standards/19-permissions.md)).
- Access checks at the lesson-content boundary use `Entitlement`, not raw enrollment state — so paid access (Phase 09) and free access (Phase 07) share one read path.

**Billing-source entitlements** are produced in Phase 09: a paid `Order` emits `OrderPaidV1` which the Enrollment module consumes (via the Phase 02b outbox) and converts into an `Entitlement` with `source = billing`. Phase 07 ships the consumer contract; Phase 09 ships the producer. The hand-off seam is the integration event, not a shared table.

### Learner Portal

Initial screens:

- My courses.
- Course overview.
- Lesson player.
- Lesson resources.
- Progress summary.
- Profile basics.

### Progress Tracking

- Lesson progress.
- Module progress.
- Course progress.
- Completion timestamps.
- Last viewed lesson.
- Resume learning.

### Learning Events

Example events:

- Course viewed.
- Lesson started.
- Lesson completed.
- Resource downloaded.
- Course completed.

### Access Control

- Separate public course detail from enrolled course content.
- Learners can only access private lesson content for courses they are entitled to.
- Admin and instructor preview capabilities are separate from learner access.

## Deliverables

- Enrollment API.
- Learner portal MVP.
- Lesson player MVP.
- Progress tracking.
- Learning event recording.

## Completion Criteria

- Admin can enroll a user in a course.
- Learner can see their courses.
- Learner can complete a lesson.
- Course progress is calculated.
- Unauthorized users cannot access private lesson content.
- Learning events are recorded for analytics.

## Risks

- Confusing enrollment with billing.
- Storing progress only as frontend state.
- Leaving lesson content access control to the public renderer.
- Failing to define the CourseVersion and enrollment relationship clearly.

