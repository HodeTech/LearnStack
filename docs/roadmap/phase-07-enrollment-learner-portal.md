# Phase 07: Enrollment, Learner Portal, and Progress Tracking

## Goal

Give a named learner state. Everything before this phase is either anonymous — the
public site from [Phase 02d](phase-02d-walking-skeleton.md) and
[Phase 06](phase-06-renderer-admin-studio.md) — or administrative — the Studio surfaces
from [Phase 03](phase-03-identity-admin.md) and Phase 06. Phase 07 is where a person
signs in, is granted access to a course, consumes lessons, and leaves a progress trail
behind.

This phase moves the platform from CMS and catalog capabilities into the first learning
product capabilities. It consumes the versioned course structure and lesson items from
[Phase 05](phase-05-education-learning-content.md), the identity and membership model
from Phase 03, and the outbox and background-job infrastructure from
[Phase 02b](phase-02b-events-auth.md).

### What Phase 02d did not build

Worth stating plainly, because a walking skeleton is easy to over-read:

[Phase 02d](phase-02d-walking-skeleton.md) is **anonymous and read-only**. It renders a
catalog page and a lesson page for two tenants through two anonymous `GET` endpoints.
There is no user, no enrollment, no course access, no progress row, and no learner-side
write path anywhere behind it. The lesson page it produced has no "mark complete"
control and nothing to store if it had one.

Manual enrollment, invitation enrollment, lesson progress, resume-learning, and the
learner portal shell are therefore **new construction in this phase** — not shells being
lit up. What Phase 07 inherits from 02d is narrow and read-side only: `Course` and
`Lesson` exist (deepened into `CourseVersion`, modules and lesson items by Phase 05),
host-based tenant and organization resolution works end to end, and the public content
path exists to contrast the entitled one against.

## Scope

### Enrollment

- Enrollment model, bound to a `CourseVersion` so a republished course does not move a
  learner mid-course.
- Manual enrollment (admin grants access directly) — first built here.
- Invitation-based enrollment (the [Phase 03](phase-03-identity-admin.md) invitation
  flow extends to carry a course).
- Readiness for program, course, and cohort access models.
- Enrollment status:
  - Active
  - Suspended
  - Completed
  - Cancelled
- Enrollment source enum is defined exhaustively; only `manual` and `invitation` are
  wired in this phase:
  - `manual` — admin grants directly (here).
  - `invitation` — tenant invitation accepted with a course attached (here).
  - `billing` — a paid order emits an integration event that Enrollment consumes
    ([Phase 09](phase-09-billing-integrations-analytics.md); see § Course access).
  - `integration` — external LMS / SSO push. Lands with the integration registry and
    LTI / xAPI readiness in [Phase 09](phase-09-billing-integrations-analytics.md).
  - `bulk_import` — **no phase in this roadmap builds it.** The enum value is reserved
    so that adding a CSV import tool later is a feature, not a migration.

### Cohort

Phase 07 **owns the `Cohort` aggregate**. It is specified in full by
[Domain Model § Enrollment & Access](../architecture/02-domain-model.md), its permission
keys (`enrollment.cohort.read` / `.write` / `.delete`) are already registered against the
Enrollment module in [Permission Standards](../standards/19-permissions.md), and
[Phase 08b](phase-08b-scheduling.md)'s `LiveBooking` binds "a learner **or** a cohort" to
a live session. Without an owning phase that binding references a type nothing builds.
Enrollment is the right owner: the foreign key lives on `Enrollment` (`CohortId`,
nullable), and cohort membership *is* enrollment membership.

Built here:

- `Cohort` aggregate — `[TenantOwned]` and `[OrganizationScoped]`, because a branch runs
  its own cohorts ([ADR-0017](../decisions/0017-tenant-organization-hierarchy.md)). EF
  global query filter plus a Row Level Security policy from the canonical template in
  [Database Standards](../standards/05-database.md), per
  [ADR-0003 Amendment 3](../decisions/0003-tenant-isolation-defense-in-depth.md).
- Many-to-one binding to `CourseVersion`. Many cohorts may share one version on
  different timelines.
- Lifecycle `open → in-progress → completed → archived`.
- `Enrollment.CohortId` nullable — one-off enrollments leave it null and behave exactly
  as they do without cohorts.
- Roster read and a **derived** cohort progress projection over the member enrollments'
  `Progress` records. A projection, not an aggregate: per-cohort progress has no
  independent truth.
- Cancelling a cohort does **not** cascade-cancel its enrollments. That is an operator
  action with its own audit entry; cancelling enrollments individually is the safer
  default.

Named out of scope here, with owners, so nothing about cohorts is homeless either:

| Cohort capability | Owning phase |
|---|---|
| Cohort ↔ live session binding through `LiveBooking` | [Phase 08b](phase-08b-scheduling.md) |
| Cohort classroom rooms and attendance | [Phase 08c](phase-08c-classroom.md) |
| Cohort-level reporting and analytics | [Phase 09](phase-09-billing-integrations-analytics.md) |

### Course access

Phase 07 owns the **`CourseAccess` aggregate** and the **enrollment-source grant**
path:

- The `CourseAccess` aggregate (id, tenant, user, scope, source, granted-at, revoked-at).
  Named per [the glossary's *Course Access* entry](../glossary.md) — it is **not** an
  `Entitlement`, whose subject is always a tenant and whose only author is the Hub.
- Source `manual` and `invitation` populated directly by this phase.
- Permissions `enrollment.course_access.read` / `enrollment.course_access.write`
  ([19-permissions.md](../standards/19-permissions.md)).
- Access checks at the lesson-content boundary use `CourseAccess`, not raw enrollment
  state — so paid access (Phase 09) and free access (Phase 07) share one read path.

**Billing-source course access** is produced in
[Phase 09](phase-09-billing-integrations-analytics.md): a paid `Order` emits `OrderPaidV1`
which the Enrollment module consumes and converts into a `CourseAccess` with
`source = billing`. Phase 07 ships the consumer contract; Phase 09 ships the producer.
The hand-off seam is the integration event, not a shared table.

**What `CourseAccess` is not.** It is a boolean grant, not a balance. A consumable
allowance — a ten-session credit pack, "three make-up classes per term" — is *stateful
course access*, and the genericity boundary in
[ADR-0018 Amendment (2026-08-08)](../decisions/0018-tenant-driven-customization-model.md)
places it outside tenant customization data: a ledger that is decremented, refunded,
expired and audited cannot be declared by a JSON Schema. No phase in this roadmap builds
one. When a paying tenant needs it, it arrives as a platform feature in a LearnStack
release — its own ADR, built on
[Phase 09](phase-09-billing-integrations-analytics.md)'s billing primitives and
[Phase 08b](phase-08b-scheduling.md)'s booking lifecycle — never as a customization
row.

### Learner Portal

Built on the Studio shell, permission system, and typed SDK from
[Phase 06](phase-06-renderer-admin-studio.md), under the `(portal)` route group:

- My courses.
- Course overview.
- Lesson player (dispatches to built-in lesson-item players for primitive types and to
  tenant-defined player composites via `TenantLessonItemType` for tenant-defined types).
- Lesson resources.
- Progress summary (driven by `TenantCompletionRule`).
- Profile basics (with optional tenant-defined custom fields via `TenantCustomFieldDef`
  on `User`).

### Progress Tracking

- Lesson progress.
- Module progress.
- Course progress.
- Completion timestamps.
- Last viewed lesson.
- Resume learning.

**Completion semantics come from the tenant — and the engine that evaluates them is not
built here.** Per
[ADR-0018](../decisions/0018-tenant-driven-customization-model.md), a tenant's
`TenantCompletionRule` decides when a lesson, module, or course is complete. The DSL
engine behind that rule is chosen in **ADR-0025** (open; see
[decisions/README.md § Open ADR Drafts](../decisions/README.md)) and its runtime
evaluator ships in [Phase 05](phase-05-education-learning-content.md).

Phase 07 is a **consumer** of that evaluator: Progress hands it the learner's lesson,
module, and attempt state and stores the verdict. It writes no rule-evaluation code of
its own. If it does, the platform has two engines and they will eventually disagree
about whether a learner finished a course.

Until a tenant supplies an override, the built-in default — "all required lessons
complete" — applies. It is a primitive check shipped by Phase 05, not a second engine.

### Learning Events

Recorded as versioned integration events through `IOutbox`:

- Course viewed.
- Lesson started.
- Lesson completed.
- Resource downloaded.
- Course completed.

The `outbox_messages` table ships in [Phase 02a](phase-02a-kernel-tenancy.md); the
dispatcher, retry, dead-letter and inbox guard ship in
[Phase 02b](phase-02b-events-auth.md). Phase 07 declares event types and topics and
builds no dispatch machinery. [Phase 09](phase-09-billing-integrations-analytics.md)
consumes the stream for reporting.

### Access Control

- Separate public course detail from enrolled course content.
- Learners can only access private lesson content for courses they are entitled to. An
  unentitled request returns 404, not 403 — the existence of another tenant's or another
  learner's content is not disclosed.
- Admin and instructor preview capabilities are separate from learner access.

### Isolation and Audit

- `Enrollment`, `CourseAccess`, `Cohort`, and `Progress` are `[TenantOwned]`; `Enrollment`
  and `Cohort` are additionally `[OrganizationScoped]`. Each carries an EF global query
  filter and an RLS policy from the canonical template in
  [Database Standards](../standards/05-database.md).
- Cross-tenant and cross-organization isolation integration tests run as
  `learnstack_app`, the non-owning application role — a test that runs as the table owner
  passes even when every policy is inert.
- Baseline MUST-audit coverage applies: enrollment created / suspended / cancelled /
  completed, and course access granted / revoked
  ([18-audit-coverage.md](../standards/18-audit-coverage.md)). Per
  [ADR-0033](../decisions/0033-audit-durability-model.md) those rows are durable intent
  written inside the same transaction as the business change. A granted course access whose
  audit row was silently dropped is precisely the failure ADR-0033 exists to prevent.

## Deliverables

- Enrollment API with manual and invitation sources, bound to `CourseVersion`.
- `Cohort` aggregate, roster, lifecycle, and derived cohort progress projection.
- `CourseAccess` aggregate with the `OrderPaidV1` consumer contract in place for
  [Phase 09](phase-09-billing-integrations-analytics.md).
- Learner portal MVP under the `(portal)` route group.
- Lesson player MVP dispatching built-in and tenant-defined item types.
- Progress tracking evaluated through the Phase 05 completion-rule runtime.
- Learning event recording through the outbox.
- Isolation integration tests for every new tenant-owned aggregate, running as
  `learnstack_app`.

## Completion Criteria

- An admin can enroll a user in a course manually, and an invited user lands enrolled
  after accepting.
- A learner sees their courses, opens a lesson, and completes it.
- Course progress is calculated by evaluating the tenant's active
  `TenantCompletionRule`, not by code in the Progress module.
- Both seed tenants exercise this path, and the English school's completion rule differs
  in shape from the yoga studio's — the same code path produces two different verdicts
  from two different rows.
- A cohort with at least two enrolled learners exists, its derived progress reads
  correctly, and it is bindable by id — so [Phase 08b](phase-08b-scheduling.md)'s
  `LiveBooking` has a real type to reference.
- An unentitled user requesting private lesson content receives 404.
- Cross-tenant and cross-organization isolation tests covering `Enrollment`,
  `CourseAccess`, `Cohort`, and `Progress` are green under `learnstack_app`.
- MUST-class audit entries for enrollment and course-access operations are present and
  committed with their business transaction.
- Learning events reach a consumer through the outbox at least once, idempotently.

## Risks

- **Confusing enrollment with billing.** Enrollment grants access; billing produces a
  reason to grant it. The seam is `OrderPaidV1`, not a shared table.
- **Storing progress only as frontend state.** Progress that lives in the browser is lost
  on a device change and cannot drive a completion rule or a report.
- **Leaving lesson content access control to the public renderer.** The renderer mirrors
  authorization; the API enforces it. A content endpoint that trusts the caller's route
  group is a data leak waiting for a direct `curl`.
- **Failing to define the `CourseVersion` ↔ enrollment relationship clearly.** An
  enrollment bound to `Course` rather than `CourseVersion` silently changes what a
  learner is enrolled in the next time the course is republished.
- **Reimplementing completion logic locally.** The tempting shortcut is a small
  `if (allLessonsComplete)` in Progress while waiting for the Phase 05 evaluator. It
  works, it ships, and it becomes the second engine nobody remembers to delete.
- **Cohort growing into a scheduling feature.** Cohorts are groups of people. The moment
  a cohort holds session times, capacity, or attendance, it has absorbed
  [Phase 08b](phase-08b-scheduling.md)'s `LiveSession` and the two will disagree.

## Phase Exit Decision

[Phase 08a](phase-08a-assessment-notifications.md) begins when a learner on either seed
tenant can be enrolled — manually and by invitation — open the portal, complete a lesson,
and see progress computed by that tenant's own `TenantCompletionRule` through the
Phase 05 evaluator; when an unentitled request for private lesson content returns 404;
when a cohort with real members exposes a correct derived progress read; and when the
isolation suite covering `Enrollment`, `CourseAccess`, `Cohort`, and `Progress` is green
under `learnstack_app` with MUST-class audit rows committed alongside their business
transactions.

If ADR-0025 is not yet Accepted and the Phase 05 completion-rule evaluator has not
shipped, this gate cannot be met: progress *recording* is buildable, tenant-defined
completion is not. That dependency is resolved in Phase 05, not worked around here.
