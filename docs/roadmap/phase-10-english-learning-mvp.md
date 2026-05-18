# Phase 10: First Tenant Customization Showcase (online English education)

## Goal

Prove that LearnStack can produce a real, end-to-end education product **without
LearnStack writing any domain-specific code**. The first showcase happens to be an
online English-learning platform, but every CEFR-level, vocabulary-card, placement-test
scoring rule, and speaking-practice item is loaded as **tenant customization data**
([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)) at tenant
provisioning, not compiled into the binary.

A second, non-English tenant (e.g. a coding bootcamp or a yoga studio) ships in
parallel as the **substrate-genericity proof**: the same code paths must serve both
tenants and the cross-tenant isolation tests must pass.

The bar for any change to LearnStack core during this phase: *Does this belong in the
core because every education product will need it, or does it belong as tenant
customization data because only some domains need it?* If the latter, the answer is
to extend a customization aggregate, **not** to add a module or a domain identifier.

## Scope

### Tenant Customization Data — English Tenant

Loaded at provisioning into the customization aggregates from Phase 02a:

- **`TenantLevelTaxonomy`** — CEFR levels (A1, A2, B1, B2, C1, C2). Items declared
  in the taxonomy; the `Level` table holds the items keyed by
  `(tenant_id, taxonomy_key, key)`.
- **`TenantContentType`** — `VocabularyCard` (word + part-of-speech + definition +
  examples + audio reference), `GrammarTopic` (title + summary + examples + level
  reference), `SpeakingPrompt` (prompt + difficulty + sample answers), `LessonPackage`
  (composite content type referencing courses + supplementary content).
- **`TenantLessonItemType`** — `SpeakingPracticeItem` (prompts + live-session
  reference), `VocabularyDrillItem` (deck reference + drill mode), `GrammarExerciseItem`
  (exercise reference + scoring config).
- **`TenantScoringRule`** — placement-test → CEFR-level recommendation DSL.
  Receives an attempt's answer map, returns the recommended level key.
- **`TenantCompletionRule`** — English lesson-package completion semantics (all
  lessons + speaking session attended + vocab drill score ≥ threshold).
- **`TenantPageBlock`** — `vocabulary-list`, `level-card`, `placement-test-entry`,
  `instructor-grid` (each pointing at a built-in `content-list` / `card-grid` /
  `default-card` composite renderer).
- **`TenantCustomFieldDef`** — `User.preferredAccent` (enum: BrE/AmE/AusE/other),
  `InstructorProfile.dialectsTaught`, `Enrollment.preferredPace`.
- **`TenantTemplateLibrary`** — English-locale email templates for invitation,
  enrollment, placement-test result, speaking-session reminder, password reset.

### Tenant Customization Data — Second Tenant (genericity proof)

A non-English tenant ships in parallel. Recommended candidates:

- **Coding bootcamp** — `Track` taxonomy (Frontend / Backend / DevOps), `CodeChallenge`
  content type, `CodeRunnerItem` lesson item, track-based completion rule.
- **Yoga studio** — `Difficulty` taxonomy, `AsanaPose` content type, `BreathExerciseItem`
  lesson item, session-attendance completion rule.

The second tenant must exercise **every** customization aggregate to prove no
customization gap exists.

### Public Site (English tenant)

Initial pages composed from the customization data:

- Home page (composed from tenant-defined blocks: hero, course preview, level cards,
  instructor grid, testimonials).
- Courses page (driven by `Course` + tenant-defined `level-card` block).
- Level detail pages (driven by `TenantLevelTaxonomy` items).
- Placement test landing page (renders the `placement-test-entry` block).
- Instructor / teacher page (renders the `instructor-grid` block over
  `InstructorProfile`s with the tenant's custom fields).
- Pricing / packages page.
- Blog / resources page.
- Contact / lead form page.

### Learning Experience

- Learner onboarding.
- **Placement-test attempt → scored via `TenantScoringRule` → recommended level
  produced**. No English-specific code in any module.
- Recommended level / course.
- Enrolled course dashboard.
- Lesson player rendering custom lesson-item types (`SpeakingPracticeItem`,
  `VocabularyDrillItem`, etc.) via the `TenantLessonItemType` registry.
- Vocabulary resources (driven by `VocabularyCard` entries).
- Speaking session booking.
- In-app classroom entry from the learner portal.

### In-App Speaking Sessions

English learning uses the LearnStack classroom capability for speaking sessions
unchanged from Phase 08c. Initial scope:

- One-on-one speaking session.
- Small group speaking session readiness.
- Instructor joins from instructor portal.
- Learner joins from learner portal.
- Session material panel (sources content via `TenantContentType` references).
- Attendance.
- Session notes placeholder.
- Recording metadata placeholder (recording itself stays tenant-configurable and
  off by default).
- Speaking-session learning events.

Deferred (post-MVP, unchanged from earlier roadmap):

- AI pronunciation scoring.
- Live transcription.
- Automatic speaking feedback.
- Breakout rooms.
- Advanced whiteboard.

### Instructor Experience

- Instructor profile.
- Availability management.
- Session list.
- Join classroom action.
- Learner notes placeholder (tenant-customizable via `TenantCustomFieldDef`).
- Attendance marking.

### Admin Experience

- Manage `TenantLevelTaxonomy` items (CEFR levels for the English tenant; `Track`
  for the coding tenant — same UI, different data).
- Manage courses, instructors, lesson packages.
- Manage placement test attempt history and scoring rule overrides.
- View leads / enrollments.
- View speaking session schedule.
- **Manage tenant customization aggregates** in the Studio editor (this is the editor
  shipped in Phase 06).

### Commercial Flow

MVP options (unchanged):

- Manual enrollment.
- Manual payment approval.
- Optional online payment adapter (Phase 09's storefront billing).
- Lead form to admin follow-up.

The tenant's own LearnStack subscription is on the Hub side (Phase 09b); the tenant
admin sees it read-only in Studio. The MVP can also run with `NullEntitlementProvider`
(no Hub) for non-SaaS demonstrations.

## Deliverables

- Two tenants live: one English-learning, one non-English domain.
- Tenant customization data sets for both tenants loaded via the customization
  aggregates.
- Placement-test → scored via `TenantScoringRule`.
- English course catalog + non-English catalog.
- Learner portal flow.
- Teacher / session workflow.
- In-app speaking classroom MVP.
- Cross-tenant isolation suite green (`Tenant_A_cannot_read_Tenant_B`,
  `Org_X_cannot_read_Org_Y_within_TenantA`).
- Architecture test `Core_Modules_HaveNo_DomainSpecific_Names` continues to hold —
  i.e. *no* CEFR / English / Yoga / Track string appears anywhere in the LearnStack
  core source tree.

## Completion Criteria

- A visitor can open the English tenant public site rendered entirely from tenant
  customization data.
- A visitor can start a placement test.
- The test produces a CEFR-level recommendation **through `TenantScoringRule`
  evaluation** against the tenant's `TenantLevelTaxonomy` — no English-specific code
  involved.
- A tenant admin can enroll the visitor in the recommended course.
- A learner can open a course and complete a lesson (with completion resolved via
  `TenantCompletionRule`).
- A learner can book or access a speaking session.
- Instructor and learner can join the session inside the portal.
- The second tenant runs the same code paths against its own customization data set
  and passes the cross-tenant isolation tests.
- **Zero domain-specific names** in LearnStack core source tree; architecture test
  enforces.

## Risks

- **Polluting core boundaries** to ship the English showcase faster. Architecture
  test + code review are the discipline.
- **Hardcoding CEFR concepts** into the generic Level model. Rejected — CEFR is a
  `TenantLevelTaxonomy` instance, not a core concept.
- **Adding an English-specific module** ("just for now"). Rejected — the answer is
  always to extend a customization aggregate.
- **Starting pronunciation and AI feedback** before the classroom workflow is stable.
  Deferred unchanged.
- **Letting landing-page polish outrank platform validation.** Both tenants exercising
  the substrate is the actual MVP exit criterion.
