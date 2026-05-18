# Phase 05: Education Catalog and Learning Content

## Goal

Build the core education domain: program, course, course version, module, lesson, lesson
item models — all **domain-agnostic**. Domain-specific shapes (CEFR levels, vocabulary
cards, asana catalogs, code-challenge runners, …) live as **tenant customization data**
([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)) in the
customization aggregates already scaffolded in Phase 02a; this phase lights up the
runtime read paths against those aggregates.

Decisions consumed:

- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
  — `TenantLevelTaxonomy`, `TenantLessonItemType`, `TenantCompletionRule` resolve at
  runtime.
- [ADR-0013 Page Block Schema Versioning](../decisions/0013-page-block-schema-versioning.md)
  — same `(key, schemaVersion)` semantics apply to lesson item types.

## Scope

### Catalog

- Program.
- Course (+ optional `organization_id` for org-scoped catalogs).
- Course version.
- Category.
- **Level** — `Level` rows are looked up by `(tenant_id, taxonomy_key, key)` against
  the tenant's `TenantLevelTaxonomy` (CEFR for an English tenant, `Difficulty` for a
  yoga tenant, `Track` for a coding bootcamp tenant). The taxonomy itself is data, not
  code; the `Level` aggregate just holds whatever items the active taxonomy declares.
- Tag.
- Instructor profile reference (with tenant-defined custom fields via
  `TenantCustomFieldDef` for fields like `dialectsTaught` or `certifications`).
- Catalog visibility.
- Featured courses.
- Course SEO metadata.

### Course Versioning

Course and CourseVersion must be separated.

Reasons:

- Published course changes should not break existing learner experiences.
- Draft course structure can be prepared before publishing.
- Existing enrollments can remain attached to the correct version.

Required capabilities:

- Draft version.
- Published version.
- Version clone.
- Change summary.
- Publish validation.

### Learning Structure

- Module.
- Lesson.
- Lesson item.
- Lesson ordering.
- Optional and required lessons.
- Estimated duration.
- Prerequisite readiness.

### Lesson Item Types (Two-Tier Registry)

Lesson items follow the same two-tier pattern as page blocks:

**Tier 1 — Built-in primitive item types** (code-registered, closed set):

- Rich text lesson.
- Video.
- File / download.
- Embedded content.
- Quiz reference.
- Assignment reference placeholder.
- Live session reference placeholder.

**Tier 2 — Tenant-defined item types** via `TenantLessonItemType` rows. A tenant
declares a JSON Schema for the item payload and points at a player composite key
(e.g. `SpeakingPracticeItem` → `live-session-player`, `VocabularyDrillItem` →
`flashcard-player`). The lesson-item renderer dispatches by reading the schema and
the player key — no LearnStack code change is required to add a tenant-specific item
type.

### Completion Rules

Built-in primitive completion checks:

- Mark as complete.
- Video watched placeholder.
- Quiz passed placeholder.
- All required lessons completed.

Per-tenant completion semantics resolve through `TenantCompletionRule` — a sandboxed
boolean DSL expression that the runtime evaluates against the learner's
progress + attempt state. Example: an English lesson-package may require
*"all lessons complete AND speaking session attended AND vocab drill score ≥ 70%"*;
that whole rule is one `TenantCompletionRule` row, not code.

### Admin Studio Education Screens

- Program list/detail.
- Course list/detail (with optional org-scope filter).
- Course structure editor.
- Module editor.
- Lesson editor.
- Lesson item editor (schema-driven for both built-in and tenant-defined item types).
- `TenantLessonItemType` editor (the schema + player key surface).
- `TenantLevelTaxonomy` editor (items + metadata; tenant admin declares CEFR /
  difficulty / track / kyu-dan / …).
- `TenantCompletionRule` editor (sandboxed DSL editor with live validation).
- Course publish flow.

## Deliverables

- Education catalog API.
- Versioned course structure.
- Lesson content management.
- Course publish workflow.
- Public catalog rendering data.

## Completion Criteria

- Admin can create a course and add modules and lessons.
- Course can be edited as draft and then published.
- Published course detail can be read by the public site.
- CourseVersion behavior is covered by integration tests.
- Lesson items can be stored with different types.

## Risks

- Making Course directly mutable.
- **Hardcoding a domain-specific level system into the core.** Forbidden by ADR-0018.
  The answer is always a `TenantLevelTaxonomy` row — never a `CEFRLevel` enum, a
  `KyuRank` table, or an `AsanaDifficulty` value object.
- Freezing the built-in lesson-item primitive set too early; tenant-specific item
  types belong in `TenantLessonItemType`, not the primitive set.
- Overdesigning completion rules before the progress phase; `TenantCompletionRule`
  carries the per-tenant logic so the platform stays generic.
- Letting a "first vertical = English" shortcut sneak English keywords into Catalog or
  Learning Content code; the architecture test
  `Core_Modules_HaveNo_DomainSpecific_Names` will reject it.

