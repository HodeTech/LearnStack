# ADR 0018: Tenant-Driven Customization Model

## Status

Accepted

## Date

2026-05-18

## Decision

LearnStack core is **100% domain-agnostic**. The previous "Vertical Extension Points" model
(ADR-0011) — where verticals shipped as code packages registering compile-time-typed
plug-in classes — is **superseded**.

Tenants customize the platform by declaring **data** in their own database:

| Customization surface | Stored as | Schema |
|-----------------------|-----------|--------|
| Content types | Rows in `tenant_content_types` | JSON Schema for field definitions |
| Page blocks | Rows in `tenant_page_blocks` | JSON Schema + renderer key |
| Lesson item types | Rows in `tenant_lesson_item_types` | JSON Schema + player key |
| Level taxonomies | Rows in `tenant_level_taxonomies` | Flat list with metadata |
| Scoring rules (assessment) | Rows in `tenant_scoring_rules` | YAML-like rule DSL |
| Completion rules | Rows in `tenant_completion_rules` | Boolean expression DSL |
| Email/SMS templates | Rows in `tenant_template_library` (aggregate `TenantTemplateLibrary`) | Liquid / Handlebars per channel + locale |
| Custom fields on built-in entities (e.g. `User`, `Course`) | Rows in `tenant_custom_field_defs` | JSON Schema |

Front-end renderers (Next.js) resolve these definitions at runtime via a **generic primitive
renderer**: `text`, `markdown`, `image`, `video`, `audio`, `pdf`, `code`, `math`, `link`,
`list`, `tabs`, `embed-html` (sanitized). Any tenant-defined block / content type / lesson
item type composes these primitives.

**No vertical package code exists. No `english.placement.scoring` namespace exists.**
A tenant that runs an English learning platform, a yoga platform, a coding bootcamp, or a
music school uses the same compiled binary; the difference lives in their database rows.

## Context

The earlier ADR-0011 ("Vertical Extension Points") committed LearnStack to a plug-in model:
verticals like English Learning, Exam Prep, Corporate Academy would ship as separate code
packages, register typed extensions (`ILevelExtension`, `IAssessmentScoringStrategy`,
`IPageBlockDefinition`, etc.) at startup, and live behind feature flags per tenant.

That model assumed a **closed list of verticals** LearnStack would build and maintain. When
we revisited the platform thesis on 2026-05-18, the actual product positioning surfaced:

> LearnStack is not an education platform; it is a platform on which third parties build
> their own education platforms in arbitrary domains and disciplines.

A customer could build:

- An online English-learning platform with CEFR levels and placement tests.
- A yoga studio platform with asana taxonomy, sequence-based lessons, and a teacher-finder.
- A coding bootcamp with code-challenge lesson items and automatic test-runner grading.
- A music school with score-reading lesson items, MIDI playback, and audio-recording
  submissions.
- A meditation app with timed practice sessions and habit-streak tracking.
- A driving school with vehicle scheduling and progress-checkpoint workflows.
- An art workshop with portfolio uploads and peer-review assessments.

These domains share fundamentals (users, courses, lessons, enrollments, progress, content,
scheduling) but **differ in every domain-specific detail**. ADR-0011's model — LearnStack
ships English + Exam Prep + Corporate vertical packages — was a poor fit:

- We don't want to be in the business of building and maintaining vertical packages for
  every conceivable education domain.
- A customer who wants to build a yoga platform shouldn't need LearnStack engineers to
  ship "yoga vertical pack" code.
- The "extension registry" model (compile-time-typed registrations from third-party DLLs)
  is high-risk operationally — DLL load order, dependency injection collisions, version
  drift — and high-risk politically (whose code lives in our process?).

Nexora's experience with the analogous "tier 3 vertical modules" (Education, Fundraising)
showed even **first-party** vertical packs accumulate domain debt and force core changes —
the "core stays generic" principle is hard to honour when the team also ships verticals.

## Decision drivers

1. **PaaS positioning.** LearnStack is infrastructure for education platforms, not an
   education platform with extension points. The model should reflect that.
2. **Tenant self-service.** A customer must be able to define their domain-specific
   content types / scoring rules / completion rules **without engineering involvement**
   beyond initial onboarding.
3. **Open-ended domain coverage.** Yoga, music, code, language, art, professional
   certification — LearnStack should support all without LearnStack engineers writing
   per-vertical code.
4. **Single binary, single deployment.** Same `LearnStack.Host` runs every tenant; tenants
   differ only by data.
5. **No DLL plug-ins.** Customer code does not run in our process. Customer customization
   is declarative data + sandboxed expression DSLs (when needed).
6. **JSON Schema as the lingua franca.** Frontend and backend agree on a single declarative
   format for "what fields does this type carry"; renderer composes primitives.
7. **Audit and migration safety.** Data-driven customization is versionable, diff-able,
   and rollback-friendly. Code-driven customization is not.
8. **Architecture-test enforceability.** Architecture tests forbid any LearnStack module
   from referencing a domain-specific concept (no `Cefr`, no `Asana`, no `CodeChallenge`
   type, no English-, Yoga-, Coding-prefixed namespace). This is mechanically checked.

## Considered options

### Option A — Tenant-driven customization (chosen)

Customization surfaces are tenant-database rows declaring JSON Schemas and DSL expressions;
frontend composes primitives.

**Pros:**
- Single binary, no DLL plug-ins.
- Tenants self-service.
- No domain-specific code in the LearnStack codebase.
- Migration-safe, audit-safe, rollback-safe.
- Architecture test forbids domain-specific names in modules.

**Cons:**
- Loses compile-time type safety of plug-in interfaces.
- Renderer must handle arbitrary JSON shapes (sandboxing, validation, error states).
- Some advanced domain logic (e.g. live audio analysis for pronunciation scoring) can't be
  expressed in declarative data and falls outside the customization surface — those become
  Tier-2 LearnStack features available to all tenants (e.g. "speech-to-text scoring is a
  Plan feature").

### Option B — Vertical packages (rejected — supersedes ADR-0011)

Compile-time-typed `IModuleExtension` registry; verticals ship as separate DLLs.

**Pros:**
- Type-safe registrations.
- Familiar plug-in model for engineers.

**Cons:**
- Requires LearnStack to either ship verticals (committing to ongoing per-vertical
  maintenance) or accept third-party DLLs in our process (security and version-management
  nightmares).
- Doesn't fit the PaaS positioning.
- Forces customers either to fit into one of our verticals or to commission a custom
  vertical from LearnStack engineers.

### Option C — Hybrid (generic-only core + opt-in first-party vertical packages) — rejected

Same as Option A, but LearnStack also ships an "English" or "Yoga" vertical pack as opt-in
add-on with pre-baked content types and rules.

**Pros:**
- Out-of-the-box starting point for common domains.

**Cons:**
- Returns to maintaining per-vertical code (Nexora pattern of Tier 3 modules).
- Two parallel customization models (data + code) double the surface area to test and
  document.
- The "starting point" can be a **template** loadable by tenants from a catalog —
  achievable without code packages (see ADR-0018 implementation notes for the
  "content template marketplace" Phase 12 idea).

## Decision outcome

Adopt **Option A**: tenant-driven customization, no code-packaged verticals.

ADR-0011 is **superseded** by this ADR. The `IModuleExtension` interface, the
`IExtensionRegistry`, the `[Verticals/...]` source folder, and the `tenant_extensions` table
described in ADR-0011 are **removed from the design** before any of it is implemented.

### Architecture: domain-agnostic core + customization surfaces

```
                          ┌────────────────────────────────────────┐
                          │  Tenant A — English Hero                │
                          │                                         │
                          │  tenant_content_types:                  │
                          │   - VocabularyCard                      │
                          │   - GrammarPoint                        │
                          │   - PronunciationExercise               │
                          │                                         │
                          │  tenant_lesson_item_types:              │
                          │   - SpeakingPractice                    │
                          │                                         │
                          │  tenant_level_taxonomies:               │
                          │   - cefr: [A1,A2,B1,B2,C1,C2]           │
                          │                                         │
                          │  tenant_scoring_rules:                  │
                          │   - cefr_placement_rule                 │
                          └────────────────────────────────────────┘

                          ┌────────────────────────────────────────┐
                          │  Tenant B — Anatolia Yoga Studio        │
                          │                                         │
                          │  tenant_content_types:                  │
                          │   - AsanaPose                           │
                          │   - SequenceTemplate                    │
                          │   - BreathTechnique                     │
                          │                                         │
                          │  tenant_lesson_item_types:              │
                          │   - GuidedSequence                      │
                          │                                         │
                          │  tenant_level_taxonomies:               │
                          │   - difficulty: [beginner,…,master]     │
                          │                                         │
                          │  tenant_scoring_rules: (none)           │
                          └────────────────────────────────────────┘

         ┌─────────────────────────────────────────────────────────────────┐
         │  LearnStack core — same binary, same modules, same database     │
         │                                                                  │
         │  Identity | Tenancy | Organization | Content | Catalog |        │
         │  Enrollment | Progress | Classroom | Scheduling | Audit |       │
         │  Notification | Reporting | Media                               │
         │                                                                  │
         │  Generic primitives: text, markdown, image, video, audio,       │
         │  pdf, code, math, link, list, tabs, embed-html                  │
         └─────────────────────────────────────────────────────────────────┘
```

### Content type definition (example)

`tenant_content_types` row for "VocabularyCard" (English Hero):

```json
{
  "id": "ct-vocab-01",
  "tenant_id": "tenant-eh-uuid",
  "key": "vocabulary-card",
  "display_name": "Vocabulary Card",
  "schema_version": 1,
  "json_schema": {
    "type": "object",
    "required": ["word", "definition"],
    "properties": {
      "word": { "type": "string", "minLength": 1 },
      "pronunciation": { "type": "string" },
      "definition": { "type": "string" },
      "example_sentences": { "type": "array", "items": { "type": "string" } },
      "audio_url": { "type": "string", "format": "uri" },
      "level": {
        "type": "string",
        "enum": ["A1", "A2", "B1", "B2", "C1", "C2"]
      }
    }
  },
  "renderer_key": "default-card",
  "created_at": "2026-05-18T...",
  "updated_at": "2026-05-18T..."
}
```

### Page block definition (example)

`tenant_page_blocks` row for "VocabularyGallery" (English Hero):

```json
{
  "id": "pb-vocab-gallery-01",
  "tenant_id": "tenant-eh-uuid",
  "key": "vocabulary-gallery",
  "display_name": "Vocabulary Gallery",
  "schema_version": 1,
  "json_schema": {
    "type": "object",
    "properties": {
      "level_filter": { "type": "string", "enum": ["A1","A2","B1","B2","C1","C2","all"] },
      "limit": { "type": "integer", "minimum": 1, "maximum": 100, "default": 12 },
      "layout": { "type": "string", "enum": ["grid","carousel","list"], "default": "grid" }
    }
  },
  "data_source": {
    "content_type": "vocabulary-card",
    "query": {
      "filter": { "level": "{level_filter}" },
      "limit": "{limit}",
      "order_by": "random"
    }
  },
  "renderer_key": "content-list"
}
```

### Scoring rule DSL (assessment)

`tenant_scoring_rules` row for "CEFR Placement" (English Hero):

```yaml
key: cefr-placement-v1
display_name: CEFR Placement Test
schema_version: 1
rules:
  - condition: "section.id == 'grammar' && score < 50"
    weight: 1.0
    band_contribution: A1
  - condition: "section.id == 'grammar' && score >= 50 && score < 70"
    weight: 1.0
    band_contribution: A2
  - condition: "section.id == 'grammar' && score >= 70 && score < 85"
    weight: 1.0
    band_contribution: B1
  # ... etc

aggregation: weighted_majority
output_taxonomy: cefr
output_format: { type: "string", enum: ["A1","A2","B1","B2","C1","C2"] }
```

The DSL is **sandboxed**: condition expressions execute in a constrained evaluator (no I/O,
no arbitrary code, no infinite loops; CEL or a tiny subset of Lua-restricted-mode).
Architecture decision: which sandbox engine — to be made in a follow-up ADR when scoring
rules land (Phase 08a).

### Level taxonomy (example)

`tenant_level_taxonomies` row for "CEFR":

```json
{
  "id": "lt-cefr-01",
  "tenant_id": "tenant-eh-uuid",
  "key": "cefr",
  "display_name": "Common European Framework of Reference",
  "items": [
    { "key": "A1", "display_name": "Beginner",       "sort": 1, "metadata": { "color": "#e74c3c" } },
    { "key": "A2", "display_name": "Elementary",     "sort": 2, "metadata": { "color": "#e67e22" } },
    { "key": "B1", "display_name": "Intermediate",   "sort": 3, "metadata": { "color": "#f39c12" } },
    { "key": "B2", "display_name": "Upper-Int.",     "sort": 4, "metadata": { "color": "#27ae60" } },
    { "key": "C1", "display_name": "Advanced",       "sort": 5, "metadata": { "color": "#2980b9" } },
    { "key": "C2", "display_name": "Mastery",        "sort": 6, "metadata": { "color": "#8e44ad" } }
  ]
}
```

The yoga tenant has:

```json
{
  "id": "lt-yoga-difficulty",
  "tenant_id": "tenant-anatolia-uuid",
  "key": "difficulty",
  "display_name": "Difficulty Level",
  "items": [
    { "key": "beginner", "display_name": "Beginner",  "sort": 1 },
    { "key": "intermediate", "display_name": "Intermediate", "sort": 2 },
    { "key": "advanced", "display_name": "Advanced", "sort": 3 },
    { "key": "master", "display_name": "Master", "sort": 4 }
  ]
}
```

Both tenants run the same LearnStack core; both have a "level" concept; both define their
own taxonomy.

### Renderer architecture (frontend)

A generic React component map:

```tsx
const PRIMITIVE_RENDERERS = {
  'text': TextPrimitive,
  'markdown': MarkdownPrimitive,
  'image': ImagePrimitive,
  'video': VideoPrimitive,
  'audio': AudioPrimitive,
  'pdf': PdfPrimitive,
  'code': CodePrimitive,
  'math': MathPrimitive,
  'link': LinkPrimitive,
  'list': ListPrimitive,
  'tabs': TabsPrimitive,
  'embed-html': SanitizedHtmlPrimitive,
};

// Composite renderers built atop primitives:
const COMPOSITE_RENDERERS = {
  'default-card': DefaultCardRenderer,         // image + title + description
  'content-list': ContentListRenderer,         // grid/carousel/list of items
  'media-gallery': MediaGalleryRenderer,
  'rich-page': RichPageRenderer,
  // tenants pick from this fixed set; no custom JSX
};
```

A tenant-defined content type with JSON Schema fields `word`, `definition`, `audio_url`
renders via `default-card` composite: title = `word`, description = `definition`, audio
control = `audio_url`. The renderer iterates the type's JSON Schema, maps each field to a
primitive based on its JSON Schema type and format, composes the result.

### What cannot be customized (LearnStack-owned)

- **Core aggregates and their lifecycle** — `Tenant`, `Organization`, `User`, `Course`,
  `Module`, `Lesson`, `Enrollment`, `Progress`, `LiveSession`, `AuditEntry`. Tenants can
  extend these with custom fields (`tenant_custom_field_defs`); they cannot redefine the
  aggregate's lifecycle.
- **Authentication and authorization plumbing** — Keycloak realm structure, JWT
  emission, permission policy evaluation.
- **Storage layout** — SeaweedFS bucket prefixes, object key conventions.
- **Outbox / event-bus contract** — integration event shapes are LearnStack-defined.
- **Generic primitive set** — adding a new primitive (e.g. `whiteboard`) is a LearnStack
  release, not a tenant action. Tenants compose existing primitives.
- **Built-in scoring engines** — the safe expression sandbox (Phase 08a) supports a fixed
  set of operators. Tenants cannot bring custom code.

## Architecture tests

Three blocker-level architecture tests added in Phase 02:

1. `No_DomainSpecific_Names_In_Modules` — `Cefr`, `Asana`, `CodeChallenge`, `EnglishPlacement`,
   `YogaSequence`, etc. — no LearnStack module type / namespace / file contains these names.
2. `No_Per_Vertical_Folders` — `src/LearnStack.Verticals/`, `src/Modules/English/` etc.
   do not exist.
3. `Generic_Primitives_Only_In_Renderer` — frontend `PRIMITIVE_RENDERERS` map contains
   only the documented closed set; PR adding a new primitive is a LearnStack-team-only
   change (CODEOWNERS rule).

## Consequences

### Positive

- LearnStack scales to **arbitrary education domains** without per-domain engineering.
- Customer self-service: a yoga studio can launch on LearnStack without LearnStack writing
  a single line of yoga-specific code.
- Single binary, single deployment, predictable operational behaviour.
- No third-party DLL plug-ins in our process — security and version-management win.
- ADR-0011's "extension registry" complexity is removed before being implemented (sunk
  cost prevented).

### Negative

- Some domain-specific features that genuinely need code (audio waveform analysis,
  proctoring with face detection, generative-AI feedback) cannot be expressed as
  declarative data. These become LearnStack-team-built features available to all tenants
  on appropriate plans (Hub-entitlement, ADR-0021).
- JSON Schema authoring is harder than writing a C# class. The Admin Studio needs a
  visual schema editor (Phase 06+).
- Composite renderers are a closed set; a tenant wanting a truly novel UI pattern must
  request LearnStack to add a new composite (slow path) or compose existing primitives
  (fast path with constraints).

### Neutral

- A future ADR may introduce a "content template marketplace" — pre-built JSON Schema +
  scoring rule + level taxonomy bundles that tenants can install with one click. This is
  **data sharing**, not code sharing; the marketplace stays within Option A's model.
  Tracked as Phase 12.

## Implementation notes

- Phase 02 — Platform kernel: domain model adds the customization aggregates
  (`TenantContentType`, `TenantPageBlock`, `TenantLessonItemType`, `TenantLevelTaxonomy`,
  `TenantScoringRule`, `TenantCompletionRule`, `TenantCustomFieldDef`).
- Phase 04 — CMS / page builder: Admin Studio surfaces JSON-Schema editor (initial),
  block instance authoring against tenant-defined block types. Renderer composite set
  finalised.
- Phase 05 — Catalog & learning content: lesson item type registry; renderer composite
  for lesson playback.
- Phase 06 — Admin Studio polish: visual schema editor for content types / blocks.
- Phase 08a — Assessment: scoring rule DSL sandbox + execution engine + UI.
- Phase 12 (optional) — Content template marketplace.

The full data model, ER diagram, schema versioning rules, and Admin Studio screens live
in [32-tenant-customization-model.md](../architecture/32-tenant-customization-model.md).

## References

- **Supersedes** ADR-0011 (Extension Points).
- ADR-0017 — Tenant + Organization (custom fields are org-scoped or tenant-scoped).
- ADR-0019 — LearnStack Hub (Hub does not need to know about content types).
- ADR-0021 — Feature-Based Entitlement (some customization surfaces gated by plan tier:
  e.g. unlimited custom content types are a Growth+ feature).
- [32-tenant-customization-model.md](../architecture/32-tenant-customization-model.md) —
  architecture deep dive.
- [06-extension-model.md](../architecture/06-extension-model.md) — rewritten to reflect
  this ADR; the old vertical-pack model is removed.
