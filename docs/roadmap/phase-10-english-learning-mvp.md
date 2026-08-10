# Phase 10: Tenant Customization Showcase (online English education)

## Goal

Build one complete, commercially plausible education product — an online English school
— entirely out of tenant customization data, and in doing so exercise **every**
customization aggregate LearnStack ships. The question this phase answers is not "can
the platform serve more than one domain?" but "**is the customization surface complete
enough for one domain to be finished on it?**"

That is a change of direction from the earlier plan, and the reason is that the original
question is already answered. Two tenants in unrelated domains — an English school and a
yoga studio — exist from
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md) onward, render side by side from
[Phase 02d](phase-02d-walking-skeleton.md), and are carried through every phase in
between. Genericity is therefore proven **continuously**, by construction, not
retroactively in a showcase. What no earlier phase proves is *depth*: each of them
touches the two or three aggregates its own slice needs. Phase 10 is the first place a
single tenant uses all eight at once, in one coherent product, with real content and a
real learner journey.

The failure mode this phase hunts is specific: an aggregate that exists, has tests, and
still cannot express what a real tenant actually needs — a content type whose schema
cannot describe a vocabulary card's audio reference, a page block that cannot compose
the page the tenant wants, a completion rule that cannot say what "finished" means for a
speaking lesson. Those gaps are invisible against seed fixtures and obvious against a
product.

The bar for any change to LearnStack core during this phase is unchanged: *does this
belong in the core because every education product needs it, or does it belong as
customization data because only some domains need it?* If the latter, the answer is to
extend a customization aggregate — never to add a module or a domain identifier.

## Scope

### The eight customization aggregates

Each aggregate is delivered by an earlier phase. Phase 10 is where one tenant fills all
eight, and where a gap in any of them stops being theoretical.

| Aggregate | Delivered by | What the English tenant puts in it |
|---|---|---|
| `TenantContentType` | [Phase 02a Packet 8](phase-02a-kernel-tenancy.md) | `VocabularyCard` (word + part of speech + definition + examples + audio reference), `GrammarTopic` (title + summary + examples + level reference), `SpeakingPrompt` (prompt + difficulty + sample answers), `LessonPackage` (composite referencing courses + supplementary content) |
| `TenantLevelTaxonomy` | [Phase 02a Packet 8](phase-02a-kernel-tenancy.md) | CEFR levels A1 … C2. The taxonomy declares the vocabulary; the `Level` table holds items keyed by `(tenant_id, taxonomy_key, key)` |
| `TenantCustomFieldDef` | [Phase 03](phase-03-identity-admin.md) | `User.preferredAccent` (BrE / AmE / AusE / other), `InstructorProfile.dialectsTaught`, `Enrollment.preferredPace` |
| `TenantPageBlock` | [Phase 04](phase-04-cms-media-pages.md) | `vocabulary-list`, `level-card`, `placement-test-entry`, `instructor-grid` — each pointing at a built-in `content-list` / `card-grid` / `default-card` composite renderer |
| `TenantLessonItemType` | [Phase 05](phase-05-education-learning-content.md) | `SpeakingPracticeItem` (prompts + live-session reference), `VocabularyDrillItem` (deck reference + drill mode), `GrammarExerciseItem` (exercise reference + scoring config) |
| `TenantScoringRule` | [Phase 05](phase-05-education-learning-content.md) | Placement test → CEFR level recommendation. Takes an attempt's answer map, returns a level key from the tenant's own taxonomy |
| `TenantCompletionRule` | [Phase 05](phase-05-education-learning-content.md) | Lesson-package completion: all lessons done **and** speaking session attended **and** vocabulary drill score above the tenant's threshold |
| `TenantTemplateLibrary` | [Phase 08a](phase-08a-assessment-notifications.md) | English-locale templates for invitation, enrollment, placement-test result, speaking-session reminder, password reset |

### The genericity boundary applies to the data set

The English tenant's customization data must stay inside the boundary drawn by the
[ADR-0018 genericity amendment](../decisions/0018-tenant-driven-customization-model.md).
Content shape, presentation, and pure rule evaluation over already-recorded facts are
tenant data. Two things are not, and the showcase must not need either:

- **Stateful entitlement.** A pack of ten prepaid speaking sessions, or a "three make-up
  classes per term" allowance, requires a balance that is decremented, refunded, expired
  and audited. A JSON Schema declares shape; it cannot declare a ledger. If the English
  tenant's commercial model needs one, that is a **platform feature** requiring its own
  ADR and a LearnStack release — it does not arrive through this phase, and it is not
  smuggled in as a custom field.
- **External capability invocation.** Running submitted code, or scoring pronunciation
  from an audio clip, needs a sandbox, a runtime, a resource budget and a security
  boundary. A rule DSL evaluates; it does not execute programs.

`SpeakingPracticeItem` sits on the correct side of that line: it declares a shape that
*references* the live-classroom capability [Phase 08c](phase-08c-classroom.md) already
ships. It does not invoke anything the platform cannot already do.

**Why the coding bootcamp is not the showcase.** An earlier draft of this phase offered
a coding bootcamp as a candidate second tenant. It was dropped for exactly this reason:
its defining feature — running a learner's submitted code — is external capability
invocation, outside the customization boundary. Choosing it would have forced either a
domain-specific runner module (violating the platform's central rule) or a showcase that
quietly omitted the one thing that made the domain interesting. A yoga studio, whose
distinctive content and taxonomy are pure shape, is honest about what the model can
actually do.

### The second tenant

The second tenant already exists. The yoga studio seeded in
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md) has been running on the same code paths
since the kernel landed, and every phase from [Phase 02d](phase-02d-walking-skeleton.md)
onward has been exercised against both tenants. Phase 10 does not create it, deepen it,
or re-prove it. It keeps the yoga tenant in the isolation suite and in the regression
path — nothing the English showcase adds may break it, and any core change that only
works for one of the two is the defect this arrangement exists to catch.

### Public site (English tenant)

Composed entirely from customization data:

- Home page — hero, course preview, level cards, instructor grid, testimonials, all from
  tenant-defined blocks.
- Courses page — `Course` data rendered through the tenant's `level-card` block.
- Level detail pages — driven by `TenantLevelTaxonomy` items.
- Placement-test landing page — renders the `placement-test-entry` block.
- Instructor page — renders `instructor-grid` over `InstructorProfile`s carrying the
  tenant's custom fields.
- Pricing / packages page.
- Blog / resources page.
- Contact / lead-capture page.

### Learning experience

- Learner onboarding.
- **Placement-test attempt scored by `TenantScoringRule`, producing a recommended level
  key from the tenant's own taxonomy.** No English-specific code executes anywhere in
  that path.
- Recommended level and course.
- Enrolled-course dashboard.
- Lesson player rendering custom lesson-item types through the `TenantLessonItemType`
  registry.
- Vocabulary resources driven by `VocabularyCard` entries.
- Speaking-session booking against instructor availability from
  [Phase 08b](phase-08b-scheduling.md).
- Classroom entry from the learner portal.

### In-app speaking sessions

The English tenant uses the classroom capability from
[Phase 08c](phase-08c-classroom.md) unchanged — one-on-one sessions, small-group
readiness, instructor and learner join from their own portals, a session material panel
sourcing content through `TenantContentType` references, attendance, session notes, and
recording metadata with recording itself tenant-configurable and off by default.

Not built here, and no phase owns them: AI pronunciation scoring, live transcription,
automatic speaking feedback, breakout rooms, and advanced whiteboard sit in the post-MVP
backlog recorded in [MVP Scope](../architecture/05-mvp-scope.md). The first three are
external capability invocation and are outside the customization boundary regardless of
when they are built.

### Instructor experience

Instructor profile, availability management, session list, join-classroom action,
learner notes backed by `TenantCustomFieldDef`, attendance marking.

### Admin experience

- Manage `TenantLevelTaxonomy` items — the same Studio screen the yoga tenant uses for
  its difficulty taxonomy, with different data.
- Manage courses, instructors, and lesson packages.
- Manage placement-test attempt history and scoring-rule versions.
- View leads and enrollments.
- View the speaking-session schedule.
- **Manage every customization aggregate** in the Studio editor shipped in
  [Phase 06](phase-06-renderer-admin-studio.md). This phase is the first time that editor
  is driven across its full surface by someone building a product rather than a fixture.

### Commercial flow

Manual enrollment, manual payment approval, an optional online payment adapter from
[Phase 09](phase-09-billing-integrations-analytics.md)'s storefront billing, and lead-form
follow-up. The tenant's own LearnStack subscription is Hub-side
([Phase 09b](phase-09b-hub-billing.md)) and appears read-only in Studio; the showcase
also runs on `NullEntitlementProvider` with no Hub at all for non-SaaS demonstrations.

## Deliverables

- A complete English-school customization data set filling all eight aggregates, loaded
  at provisioning through the `seed-tenant` path — no code, no migration, no module.
- Public site, learner portal, instructor portal and admin screens for that tenant,
  rendered from that data.
- Placement test scored end to end through `TenantScoringRule` against
  `TenantLevelTaxonomy`.
- Lesson completion resolved through `TenantCompletionRule`, including the
  speaking-session and drill-score conditions.
- In-app speaking classroom working for the tenant's own session types.
- A written **customization gap list**: every place the English tenant wanted something
  an aggregate could not express, with each entry resolved as an aggregate extension, a
  named platform feature outside the boundary, or an accepted limitation.
- The yoga tenant still green: cross-tenant isolation suite
  (`Tenant_A_cannot_read_Tenant_B_data`, `Org_X_cannot_read_Org_Y_within_TenantA`)
  passing as `learnstack_app`, and both sites still rendering.
- `Core_Modules_HaveNo_DomainSpecific_Names` still green — no CEFR, English, yoga, or
  track string anywhere in the LearnStack core source tree.

## Completion Criteria

- A visitor opens the English tenant's public site and every page is composed from
  customization data.
- A visitor starts a placement test; the test produces a CEFR-level recommendation
  through `TenantScoringRule` evaluation against the tenant's own taxonomy, with no
  English-specific code involved.
- A tenant admin enrolls that visitor in the recommended course.
- A learner opens a course, completes a lesson, and completion resolves through
  `TenantCompletionRule` including its speaking-session condition.
- A learner books and joins a speaking session; the instructor joins from the instructor
  portal.
- **All eight customization aggregates hold real English-tenant rows** and each one is
  reachable and editable from the Studio editor.
- The gap list is written and every entry is resolved — an extension shipped, a platform
  feature named with its owning decision, or a limitation accepted in writing.
- The yoga tenant is unchanged and unbroken, and the isolation suite is green.
- Zero domain-specific names in LearnStack core; the architecture test enforces it.

## Risks

- **Polluting core boundaries to ship the showcase faster.** The pressure is highest
  here, because the showcase is the first thing that looks like a product and the
  temptation is to make one screen work rather than make the aggregate express it.
  Architecture tests catch the naming; only review catches the intent.
- **Hardcoding CEFR into the generic level model.** Rejected — CEFR is a
  `TenantLevelTaxonomy` instance, not a core concept. The same screen must keep serving
  the yoga studio's difficulty taxonomy without a branch.
- **An English-specific module "just for now".** Rejected. The answer is always an
  aggregate extension, and an extension that cannot express the need is a genuine
  finding for the gap list rather than a reason to write a module.
- **Smuggling a platform feature in as tenant data.** Session credit packs are the likely
  candidate — they look like a custom field and behave like a ledger. If it holds a
  balance or calls out of the process, it is not customization data
  ([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)).
- **Letting landing-page polish outrank the gap list.** The showcase's value is the list
  of things the customization surface could not do. A beautiful site with an empty gap
  list means the phase was run as marketing, not as validation.
- **Treating this phase as the genericity proof again.** It is not. If the yoga tenant
  has quietly rotted since Phase 02d, that is a regression from an earlier phase, and it
  is found here far too late.

## Phase Exit Decision

[Phase 11](phase-11-production-hardening.md) begins when the English tenant is a product
a real school could operate — public site, placement test, enrollment, lesson delivery,
speaking sessions and admin, all driven by data in all eight customization aggregates —
**and** the customization gap list is closed, with every entry either shipped as an
aggregate extension, named as a platform feature with an owning decision, or accepted in
writing as a limitation. The yoga tenant must still be running unchanged on the same
binary with the isolation suite green.

If the gap list contains an unresolved entry that blocks the tenant from operating, the
phase does not exit: a customization surface that a single real tenant cannot finish on
is not ready to be hardened for production.
