# Phase 05: Education Catalog and Learning Content

## Goal

Build the core education domain — program, course, course version, module, lesson,
lesson item — and light up the tenant customization runtime that gives those structures
their per-tenant shape. Every model here is **domain-agnostic**. Domain-specific shapes
(CEFR levels, vocabulary cards, asana sequences, submitted-code exercises) live as
**tenant customization data**
([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)), never as code.

This phase **deepens** what [Phase 02d](phase-02d-walking-skeleton.md) already shipped
rather than creating it. `Course` and `Lesson` exist in thin form from the walking
skeleton — slug, title, published state, an ordered lesson list, and a body rendered
from a single primitive. Phase 05 adds the structure a real catalog needs: programs,
versioning, modules, lesson items, and tenant-defined item types.

This phase also carries the decision the customization model has been running without.
**[ADR-0025](../decisions/README.md#open-adr-drafts) — the scoring and completion DSL
sandbox engine — must be Accepted in this phase.** It is reserved with Phase 05 as its
target, and it is an exit blocker: [Phase 02a Packet 8](phase-02a-kernel-tenancy.md)
deliberately stores rule bodies as opaque `text` with a `dialect` discriminator because
the three candidate engines do not share a column type. Rules can be authored before the
engine exists; they cannot be **evaluated** before it exists.

Decisions consumed:

- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
  — `TenantLevelTaxonomy`, `TenantLessonItemType`, `TenantScoringRule` and
  `TenantCompletionRule` resolve at runtime. Its **2026-08-08 Amendment** draws the
  genericity boundary this phase must respect: content shape, presentation and pure rule
  evaluation are tenant data; stateful entitlement and external capability invocation
  are platform features.
- [ADR-0013 Page Block Schema Versioning](../decisions/0013-page-block-schema-versioning.md)
  — the same `(key, schemaVersion)` semantics apply to lesson item types.
- [ADR-0021 Feature-Based Entitlement](../decisions/0021-feature-based-entitlement.md)
  — the runtime limits below are `LimitKeys` entries, so a plan raises them up to a
  platform ceiling rather than the ceiling being hard-coded per tenant.

## Scope

### What Phase 02d already shipped

Phase 05 does not re-create these; its migrations are additive, and the one destructive
change (lesson bodies become lesson items) goes through the two-step deprecation window
in [Database Standards](../standards/05-database.md).

| Already exists | Phase 05 adds |
|---|---|
| `Course` — slug, title, summary, published / draft | Program membership, versioning, categories, tags, SEO, catalog visibility |
| `Lesson` — ordered within a course, single-primitive body | Module membership, lesson items, required / optional, duration, prerequisites |
| Two anonymous read endpoints | The authenticated authoring surface and the versioned read path |
| `[TenantOwned]` markers, EF query filters, RLS policies | The same layers on every new table, with no exception |

### ADR-0025 — the scoring and completion DSL engine

The one decision this phase cannot ship without. The ADR settles:

- **The engine.** CEL (Common Expression Language, `cel-dotnet`), a restricted Lua
  (NLua / MoonSharp with a stripped standard library), or a custom AST interpreter over
  a closed grammar. The choice determines the stored representation, so it also
  determines what the `dialect` discriminator from
  [Phase 02a Packet 8](phase-02a-kernel-tenancy.md) resolves to.
- **The sandbox boundary.** No file, network or process I/O. No reflection. No
  host-object graph traversal. No unbounded loops or recursion. Deterministic: time and
  randomness reach the expression only through the injected `IClock` / `IRandom`
  abstractions, so a rule replayed over the same facts yields the same answer.
- **The allowed function set.** A closed, versioned catalogue. Adding a function is a
  LearnStack release and a catalogue revision, not a tenant action — the same closure
  rule the primitive renderer set already carries.
- **The evaluation budget.** Wall-clock ceiling, step ceiling and memory ceiling per
  evaluation, enforced by the host rather than trusted to the expression.
- **The failure posture**, which must fail closed in both directions. A rule that
  exceeds its budget or throws must never silently mark a lesson complete and never
  silently assign a scoring band. The evaluation returns a failure the caller surfaces;
  the attempt is flagged, and the failure is audited.
- **The migration path.** Rows authored under one `dialect` before the decision either
  migrate or are rejected on first evaluation with a message the tenant admin can act
  on. Silent reinterpretation of a rule body under a different dialect is not an option.

[Phase 08a](phase-08a-assessment-notifications.md) consumes this engine for assessment
scoring and does not re-decide it.

### Catalog

- Program.
- Course (+ optional `organization_id` for org-scoped catalogs).
- Course version.
- Category.
- **Level** — `Level` rows are looked up by `(tenant_id, taxonomy_key, key)` against the
  tenant's `TenantLevelTaxonomy` (CEFR for the English school, `Difficulty` for the yoga
  studio, `Track` for a coding bootcamp). The taxonomy is data, not code; the `Level`
  aggregate holds whatever items the active taxonomy declares.
- Tag.
- Instructor profile reference, with tenant-defined custom fields via
  `TenantCustomFieldDef` (which lands in [Phase 03](phase-03-identity-admin.md)) for
  fields such as `dialectsTaught` or `certifications`.
- Catalog visibility.
- Featured courses.
- Course SEO metadata.

### Course versioning

`Course` and `CourseVersion` are separate aggregates because:

- Editing a published course must not change what an enrolled learner is working
  through.
- Draft structure has to be assembled before it is visible.
- Existing enrollments stay attached to the version they started against
  ([Phase 07](phase-07-enrollment-learner-portal.md) binds to `CourseVersion`, not
  `Course`).

Required capabilities: draft version, published version, version clone, change summary,
publish validation.

### Learning structure

- Module.
- Lesson.
- Lesson item.
- Lesson ordering.
- Optional and required lessons.
- Estimated duration.
- Prerequisite readiness.

### Lesson item types — two-tier registry

`TenantLessonItemType` lands **here**, moved out of
[Phase 02a Packet 8](phase-02a-kernel-tenancy.md), because this is the phase that gives
it a consumer. Shipping an aggregate several phases before anything reads it produces a
schema tuned to an imagined reader.

**Tier 1 — built-in primitive item types** (code-registered, closed set):

- Rich text lesson.
- Video.
- File / download.
- Embedded content.
- Quiz reference.
- Assignment reference placeholder.
- Live session reference placeholder.

**Tier 2 — tenant-defined item types** via `TenantLessonItemType` rows. A tenant
declares a JSON Schema for the item payload and points at a player composite key
(`SpeakingPracticeItem` → `live-session-player`, `GuidedSequenceItem` →
`lesson-shell`). The lesson-item renderer dispatches by reading the schema and the
player key — no LearnStack code change adds a tenant-specific item type. The composite
and primitive renderer sets those keys resolve against are closed and canonical in
[32-tenant-customization-model.md § 2](../architecture/32-tenant-customization-model.md);
[Phase 06](phase-06-renderer-admin-studio.md) owns their reconciliation.

### Scoring and completion rule runtime

The `TenantScoringRule` and `TenantCompletionRule` **runtime** lands here — also moved
out of Phase 02a Packet 8, and blocked until ADR-0025 is Accepted.

Built-in primitive completion checks:

- Mark as complete.
- Video watched placeholder.
- Quiz passed placeholder.
- All required lessons completed.

Per-tenant completion semantics resolve through `TenantCompletionRule`: a boolean
expression evaluated in the ADR-0025 sandbox against the learner's already-recorded
progress and attempt state. A lesson package may require *"all lessons complete AND
speaking session attended AND drill score ≥ 70%"* — one row, not code. Scoring rules
follow the same path with a band-contribution shape instead of a boolean.

Two constraints follow from the [ADR-0018
Amendment](../decisions/0018-tenant-driven-customization-model.md)'s genericity
boundary, and both are load-bearing here:

- A rule **evaluates over facts that already exist**. A rule that needs to decrement a
  session balance, hold a credit, or expire an allowance is asking for stateful
  entitlement, which is a platform feature — not a customization row.
- A rule **does not invoke external capability**. Running a learner's submitted code or
  scoring pronunciation from audio is a sandbox with a runtime and a resource budget,
  not an expression. Those arrive as platform features through provider adapters.

### Customization runtime cost model

The customization runtime is the platform's central read path — every rendered page,
lesson and catalog entry passes through schema lookup, taxonomy lookup, reference
resolution and, on the learner path, rule evaluation. Its cost model is written here
because this is the phase where the read path first carries real payloads, and because a
cost model retrofitted after the caches exist is a rewrite. The conceptual model lives
in
[32-tenant-customization-model.md](../architecture/32-tenant-customization-model.md);
this phase implements and measures it.

**Validation timing — write time, not read time.**

- JSON Schema validation happens when a schema is saved (is this a valid schema?) and
  when an instance is saved (does this payload match its schema version?). Both are
  authoring-path operations with a human waiting on one request.
- The read path does **not** re-validate. Entries pin `schema_version` at creation
  ([ADR-0013](../decisions/0013-page-block-schema-versioning.md)) and schema versions are
  immutable, so a stored payload cannot drift out of conformance with the version it
  declares. An entry whose declared version no longer resolves renders the
  `UnknownVersionBlock` placeholder — it does not trigger validation on a hot path.
- Validating on read would put a schema compile and a full document walk in front of
  every catalog page, for a defect the write path already prevents.

**Cache strategy.**

- Three read-through caches: `TenantContentType` by `(tenant, key, version)`,
  `TenantLessonItemType` by `(tenant, key, version)`, `TenantLevelTaxonomy` by
  `(tenant, key)`.
- Because schema versions are immutable, a versioned entry **never needs invalidation**.
  Only the *active version pointer* for a key changes, and only on publish. Cache the
  immutable body aggressively; keep the pointer on a short TTL behind a per-tenant
  generation key that publish bumps.
- Invalidation uses that generation key, not a prefix scan.
  `ICacheService.RemoveByPrefixAsync` was removed or redesigned in
  [Phase 02a Packet 5](phase-02a-kernel-tenancy.md) precisely because prefix eviction
  cannot be honoured across instances; this phase must not reintroduce the assumption.
- Compiled artefacts — the compiled JSON Schema validator and the compiled rule — live
  in a per-process L1 cache only and are never serialised to L2. The L1 cache is a
  bounded LRU with a global entry cap, so a tenant with a thousand schema versions
  cannot evict every other tenant's working set.
- Cold start after a deploy pays compilation once per `(tenant, key, version)` actually
  requested. That cost is measured, not assumed.

**Reference resolution and the N+1 path.**

- The naive implementation resolves each `$ref` when the renderer reaches it. A guided
  sequence with forty pose references becomes forty round trips, multiplied by the
  number of items on the page. This is the single most likely performance defect in the
  customization runtime, and it does not appear in a two-item development fixture.
- The rule is a **two-phase walk**: collect every reference across the payload tree by
  depth level, then issue one batched read per `(content type, depth level)`. With the
  depth cap below, a render costs at most three batched reads per referenced content
  type regardless of fan-out.
- Taxonomy and schema lookups never reach the database on a warm cache.
- The budget is asserted by a test that **counts database round trips** for a
  representative lesson render, not by code review. A round-trip count is a number a
  regression can move; a review comment is not.

**Limits.**

Each limit is enforced at **write time** — a payload no reader can render must not be
storable — and each is a `LimitKeys` entry a plan may raise up to a platform ceiling
that no plan exceeds.

| Limit | Default | Why it exists |
|---|---|---|
| `$ref` depth | 3 | Bounds resolution round trips and makes cycle detection cheap |
| Block instances per page version | 100 | Bounds render fan-out and editor payload size |
| Lesson items per lesson | 100 | Same, on the learning path |
| Array items per field | 200, and every array schema must declare `maxItems` | An unbounded array is an unbounded render |
| Resolved references per render | 200 | Caps total fan-out even when each level is within depth |
| Customization row payload size | 256 KB | Keeps schema and rule bodies inside one round trip |

Reference cycles are rejected at save. A schema that references itself within the depth
cap is legal; one that cannot terminate is not.

**The `embed-html` sanitisation contract.**

`embed-html` is the one primitive that accepts author-supplied HTML inside a set the
corpus otherwise calls closed and safe. Its author is a tenant content editor — lower
trust than a platform operator, higher than a learner — and its output is served to
every learner on that tenant's host. The contract:

- **The backend sanitises on write and the renderer sanitises on render.** Server-side
  sanitisation is authoritative because the API serves HTML to any client, not only to
  the browser that runs DOMPurify. Re-sanitising on render means tightening the
  allow-list applies to rows already stored.
- **Closed allow-list** of tags and attributes. No `<script>`, no `<style>`, no
  event-handler attributes, no `javascript:` / `vbscript:` URLs, and no `data:` URLs
  except `data:image/*`. `<iframe>` survives only when its host matches an allow-list
  declared in the tenant's settings; otherwise it is stripped.
- **Content Security Policy without `unsafe-inline`.** A renderer that needs inline
  script or style to display embedded HTML has already lost the argument.
- **Host-scoped session cookies**, never wildcard-domain. Tenants are separated by host
  ([Phase 02a Packet 7](phase-02a-kernel-tenancy.md)); a cookie scoped to the parent
  domain would let a single sanitiser miss on one tenant's host reach another tenant's
  session, defeating at the browser what Row Level Security enforces at the database.
  [Security Standards](../standards/11-security.md) is the authority.
- **Rejected at save, stripped at render.** A save that violates the allow-list fails
  with the offending node reported, so the editor learns what is disallowed. A render
  that finds a violation in an older row strips it silently and increments a counter —
  by then there is no one to tell.
- **Plan-gated and audited.** `embed-html` availability is a feature key, so a tenant
  with no use for an HTML sink cannot be attacked through one, and its use is a row in
  the Customization module's audit-coverage matrix
  ([Audit Coverage Standards](../standards/18-audit-coverage.md)).

### Admin Studio education screens

Built against the shell [Phase 03](phase-03-identity-admin.md) ships. The full
cross-phase screen ownership table lives in **one** place —
[Phase 06](phase-06-renderer-admin-studio.md) — and these are Phase 05's rows in it:

- Program list / detail.
- Course list / detail (with optional org-scope filter).
- Course structure editor.
- Module editor.
- Lesson editor.
- Lesson item editor (schema-driven for both built-in and tenant-defined item types).
- `TenantLessonItemType` editor (schema + player key).
- `TenantLevelTaxonomy` editor (items + metadata; the tenant admin declares CEFR /
  difficulty / track / kyu-dan).
- `TenantCompletionRule` editor — sandboxed DSL editor with live validation against the
  ADR-0025 engine.
- Course publish flow.

## Deliverables

- ADR-0025 **Accepted**, with the engine, sandbox boundary, allowed function set,
  evaluation budget and failure posture written down.
- The rule evaluation runtime: compile, cache, evaluate, budget-enforce, audit —
  consumed by `TenantCompletionRule` here and by assessment scoring in
  [Phase 08a](phase-08a-assessment-notifications.md).
- `TenantLessonItemType` aggregate, migrations, editor and renderer dispatch.
- Education catalog API — programs, courses, versions, categories, levels, tags, SEO.
- Versioned course structure with draft / publish / clone.
- Lesson content management with lesson items across both registry tiers.
- Public catalog rendering data, extending the two read endpoints
  [Phase 02d](phase-02d-walking-skeleton.md) shipped.
- The customization runtime cost model implemented and **measured**: validation on the
  write path, the two-tier cache with generation-key invalidation, the batched
  reference walk, the enforced limit set, and the `embed-html` sanitisation contract.

## Completion Criteria

- ADR-0025 is Accepted, and a `TenantCompletionRule` expression that attempts file,
  network or reflection access is **rejected at evaluation time** by an integration
  test — the sandbox invariant in
  [32-tenant-customization-model.md § 10](../architecture/32-tenant-customization-model.md).
- A rule that exceeds its evaluation budget fails closed: the lesson is not marked
  complete, no scoring band is assigned, and the failure is audited.
- An admin creates a course, adds modules and lessons, edits it as a draft, and
  publishes it; the published course detail is readable by the public site.
- `CourseVersion` behaviour is covered by integration tests, including that an existing
  enrollment reference stays pinned to its version when a new version publishes.
- Both seed tenants from [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) — the English
  school and the yoga studio — have a lesson built from a **tenant-defined**
  `TenantLessonItemType` rendering through a built-in player composite, with their own
  level taxonomy, and no code path branches on which tenant it is.
- A representative lesson render issues a **bounded, asserted number of database round
  trips** independent of reference fan-out.
- A payload that exceeds any limit in the table above is rejected at save with a message
  naming the limit.
- HTML pasted into an `embed-html` field with a `<script>` tag, an `onerror` attribute or
  a `javascript:` URL is rejected at save and stripped at render.
- `Core_Modules_HaveNo_DomainSpecific_Names` is green — no `Cefr`, `Asana`,
  `CodeChallenge` or comparable identifier appears in any module.

## Risks

- **ADR-0025 slipping past this phase.** It is the phase's only hard blocker, and the
  temptation is to ship the catalog and leave rule evaluation for
  [Phase 08a](phase-08a-assessment-notifications.md). That is how the schema decision
  gets made implicitly by whichever engine someone prototypes with. Mitigated by the
  exit gate below.
- **Making `Course` directly mutable.** Editing a published course in place breaks
  learners mid-course and destroys the version history enrollment depends on.
- **Hardcoding a domain-specific level system into the core.** Forbidden by ADR-0018.
  The answer is always a `TenantLevelTaxonomy` row — never a `CEFRLevel` enum, a
  `KyuRank` table or an `AsanaDifficulty` value object.
- **Freezing the built-in lesson-item primitive set too early**; tenant-specific item
  types belong in `TenantLessonItemType`, not in the primitive set.
- **A cost model that exists only on paper.** Every item above is cheap to write and
  cheap to skip, and each one is invisible in a development fixture with two content
  entries. Mitigated by making the round-trip count and the limit rejections completion
  criteria with tests behind them, not review items.
- **`embed-html` treated as one more primitive.** It is an HTML sink written by a
  lower-trust actor inside a set the corpus calls closed and safe, and it is the most
  likely cross-tenant compromise path in the whole customization model. Mitigated by the
  written contract, the feature gate and the host-scoped cookie rule.
- **A "first vertical = English" shortcut** leaking English keywords into Catalog or
  Learning Content code. The architecture test rejects it; reviewers should reject the
  intent earlier.

## Phase Exit Decision

[Phase 06](phase-06-renderer-admin-studio.md) begins when:

- **ADR-0025 is Accepted** and its engine is implemented, sandboxed, budgeted and
  covered by a sandbox-escape test. This is an unconditional blocker — the phase does
  not exit with rule bodies still opaque.
- A course can be authored, versioned, published and read end to end, with lesson items
  from both registry tiers.
- Both seed tenants render structurally different lessons from their own customization
  data, on the same code paths.
- The customization runtime cost model is implemented **and measured**: write-time
  validation, generation-key cache invalidation, a bounded round-trip count for a
  representative render, enforced limits, and the `embed-html` contract with tests
  behind each clause.
