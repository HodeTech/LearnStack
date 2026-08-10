# Phase 04: Headless CMS, Page Builder, and Media Library

## Goal

Turn LearnStack into an education-aware headless CMS and page-composition platform, not
merely a course-management system. This phase enables landing pages, blog content,
catalog pages, campaign pages, and tenant-defined page blocks.

[Phase 02d](phase-02d-walking-skeleton.md) already renders two tenants' catalog and
lesson pages from customization data. It does so with hard-coded route segments and a
single built-in content primitive. This phase replaces that with an authored,
versioned, localized content system that a tenant admin drives from Admin Studio — and
it is the phase where four long-standing modelling conflicts in the corpus get an
answer, because every one of them becomes load-bearing the moment content is authored
rather than seeded.

Decisions consumed in this phase:

- [ADR-0008 Localization Schema](../decisions/0008-localization-schema.md). Every
  tenant-owned content table introduced here adopts the side-table or JSONB-localized
  pattern the ADR declares. `tenant_locales` is owned by the Tenancy module and lands in
  [Phase 02a Packet 6](phase-02a-kernel-tenancy.md); this phase consumes it.
- [ADR-0013 Page Block Schema Versioning](../decisions/0013-page-block-schema-versioning.md).
  Every block carries a `(key, schemaVersion)` tuple, an immutable validation contract,
  and a registered renderer. Lazy migration and the `UnknownVersionBlock` /
  `UnknownBlock` placeholders are part of the renderer contract from day one.
- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md).
  Content shapes are `TenantContentType` rows; page blocks are `TenantPageBlock` rows.
  Both are tenant data, not code. The 2026-08-08 Amendment's genericity boundary applies
  directly here: presentation and content shape are inside it, so nothing in this phase
  needs a code branch per tenant.

Decision required **before this phase exits**:
[ADR-0027 — frontend i18n library pick](../decisions/README.md) (`next-intl` vs
`react-intl` vs `lingui`), reserved against this phase in the decisions README's open
drafts table.

## Scope

### Content Type System — one concept, one aggregate

The corpus models a content-type schema twice. The
[Domain Model](../architecture/02-domain-model.md) lists `ContentType` under **Content**
("Schema for structured content; tenant-scoped") and `TenantContentType` under **Tenant
Customization** ("JSON Schema for a content type"). They are the same aggregate with two
owning modules, two write paths, two validators, and two permission keys. Search already
picks a side arbitrarily — [Search](../architecture/20-search.md) maps its
`content_type_key` field to `ContentType.Key`.

**`TenantContentType` survives.** It is the only aggregate in LearnStack that declares a
content shape. It lives in `LearnStack.Modules.Customization`, ships in
[Phase 02a Packet 8](phase-02a-kernel-tenancy.md), and gains its editor and runtime
surface here. `ContentType` is deleted from the domain model rather than renamed, and
`Search`'s `content_type_key` resolves to `TenantContentType.Key`.

The Content module keeps the half that is genuinely its own:

- `ContentEntry` — an instance of a content shape, with draft and published states. It
  stores `content_type_key` and `schema_version` as plain columns and validates its
  `data` payload against the referenced schema revision on every write.
- **No database foreign key crosses the module boundary.** `content_entries` lives in
  the Content module's schema and `tenant_content_types` in Customization's; a
  cross-schema key would be exactly the cross-module join
  [ADR-0010](../decisions/0010-cross-module-communication.md) and
  [Database Standards](../standards/05-database.md) forbid. The reference is validated
  through Mechanism #1 — an application contract in
  `Customization.Application.Contracts` that resolves a `(tenant_id, key,
  schema_version)` tuple to its JSON Schema and reports whether the revision is still
  publishable.
- Referential integrity is therefore enforced in the application, and the failure mode
  is explicit: deleting a schema revision requires a zero-instance count across the
  tenant, per [ADR-0013](../decisions/0013-page-block-schema-versioning.md).

Also in scope:

- `TenantContentType` JSON Schema editor in Admin Studio; the schema is validated as a
  schema on save, not merely stored.
- Built-in primitive field types composed by the JSON Schema: text, rich text, number,
  boolean, date/time, media reference, entry reference, select / multi-select, JSON /
  object. The set is closed and changes only with a LearnStack release.
- `ContentEntry` CRUD per type, with draft and published states.
- Schema-version migration path: lazy on entry save, plus bulk migration as a
  tenant-admin operation with a dry run.

### Customization Key Shape and Immutable Schema Versions

[ADR-0013](../decisions/0013-page-block-schema-versioning.md) requires immutable
versioned schemas: a breaking change ships as a new `schemaVersion` and the previous
version stays supported while any instance references it.
[Tenant Customization Model § 11](../architecture/32-tenant-customization-model.md)
requires `UNIQUE (tenant_id, key)` on every customization table. The two cannot both
hold — the same document's own § 4 example shows `vocabulary-card` at
`schema_version = 1` and `schema_version = 2` in `tenant_content_types`, and the unique
constraint rejects the second row. Written as published, the first breaking change a
tenant makes fails on a constraint violation.

The key shape LearnStack ships, for every versioned customization aggregate
(`TenantContentType`, `TenantPageBlock`, and `TenantLessonItemType` when
[Phase 05](phase-05-education-learning-content.md) adds it):

| Constraint | Purpose |
|---|---|
| `UNIQUE (tenant_id, key, schema_version)` | Identity of one immutable schema revision |
| `UNIQUE (tenant_id, key) WHERE status = 'active'` (partial index) | At most one publishable revision per concept |

`(tenant_id, key)` names the concept; `(tenant_id, key, schema_version)` names the
revision. The partial index preserves what § 10's rule was actually protecting — a
tenant cannot have two live definitions of `vocabulary-list` — without forbidding the
version history the ADR requires.

Immutability is scoped precisely, because ADR-0013 permits additive edits within a
version and the corpus does not currently say which edits those are:

- The **validation contract** of a `(tenant_id, key, schema_version)` tuple is
  immutable after first publish. Any change a stored instance could fail — a removed
  field, a narrowed type, a new required field, a tightened enum — raises
  `schema_version`.
- Strictly additive changes — an optional field with a default, a widened enum, a new
  facet — raise `schema_revision` within the same `schema_version`. Instances pin
  `schema_version` only; `schema_revision` is not part of any unique key.
- The classification is not left to the author's judgement. On save, the editor diffs
  the submitted schema against the current revision and refuses an additive claim that
  removes or narrows anything.
- `status` moves `draft → active → deprecated`. After first publish, `status` and
  presentation metadata are the only mutable columns on the row.

### Page Model

- `Page`, `PageVersion` (draft and published snapshots), `PageBlock` instances inside a
  version.
- Per-locale slugs and SEO metadata through the side-table pattern below.
- Locale readiness — which locales a page is publishable in.
- Draft / publish workflow, preview token, and a `Redirect` model that the CMS
  auto-creates when a published slug changes.

### Localization and Slug Uniqueness

[Phase 02d](phase-02d-walking-skeleton.md) already ships the side-table pattern for
`Course` and `Lesson`, with the constraint below, and
[Localization Standards § Pattern A](../standards/08-localization.md) already publishes it
as the rule every translation table follows. This phase applies that rule to every routable
entity it introduces — `Page`, `ContentEntry`, `Redirect`, navigation items — and adds the
one thing a per-table constraint cannot do.

- Every `<entity>_translations` table carries `tenant_id` as a real column and declares its
  own Row Level Security policy. Row Level Security is per table; it is not inherited
  through a parent check constraint, and a translation table without a policy is
  unprotected while holding the title and the slug.
- Slug uniqueness is `UNIQUE (tenant_id, locale, slug)` — **flat across organizations**.
  `organization_id` is not in the key. Two reasons, and the second survives fixing the
  first: a nullable column in a standard `UNIQUE` constraint does not constrain the rows
  where it is null, because PostgreSQL treats nulls as distinct — so
  `UNIQUE (tenant_id, organization_id, locale, slug)` constrains no tenant-wide row at all;
  and even repaired with `NULLS NOT DISTINCT`, an organization-scoped row and a tenant-wide
  row could still claim one slug, while a host resolving to `(tenant_id, organization_id)`
  serves **both** tiers and would have to pick a winner at render time. Preferring the more
  specific row is an unauthored slug change at a stable URL with no redirect — the failure
  [Localization § Risks](../architecture/12-localization.md) already calls breaking.
- The per-locale primary key `(<entity>_id, locale)` stays. It expresses "one translation
  row per entity per locale" and is not the slug constraint.
- Entity types that share one URL segment namespace — `Page` and `Redirect` both live at
  the tenant root — resolve through a single `tenant_route_slugs` registry keyed
  `UNIQUE (tenant_id, locale, path)`, on the same flat namespace and for the same reason.
  Per-table uniqueness cannot see across tables, and a page silently shadowing a redirect
  is the same class of defect as an organization row shadowing a tenant-wide one.
- A slug collision returns `Result.Fail(business_rule_violation, …)` from the publish
  command. It names the conflicting entity when the caller may read it — tenant-wide rows
  and the caller's own organization's rows both qualify under the canonical policy — and
  otherwise names only the slug and the locale, because naming a row in another
  organization would leak across the boundary Row Level Security exists to hold. It is
  never resolved by picking a winner at render time.

Also in scope: locale fallback chain per tenant, the `/{locale}/{slug}` routing shape,
and per-locale publish readiness. The frontend i18n library is chosen in ADR-0027 (see
the Phase Exit Decision).

### Page Blocks — Two-Tier Registry

The page-builder resolver consults two tiers in order
([Page Builder](../architecture/17-page-builder.md)):

**Tier 1 — built-in primitive blocks**, code-registered, a closed set that changes only
with a LearnStack release:

- Hero, rich text, image, video, CTA.
- Feature list, FAQ, testimonial.
- Generic composite renderers: `content-list`, `card-grid`, `default-card` — the
  composites that tenant-defined blocks dispatch to.

**Tier 2 — tenant-defined blocks** via `TenantPageBlock` rows. A tenant declares a JSON
Schema plus a composite-renderer key (for example `vocabulary-list` → `content-list`);
the runtime reads the schema, queries the data, and dispatches to the chosen composite.
There are **no vertical-prefixed block keys in code** — a tenant's "vocabulary list" is
a row, not an identifier.

**`TenantPageBlock` lands here in full**, moved out of
[Phase 02a Packet 8](phase-02a-kernel-tenancy.md), which now ships only
`TenantContentType` and `TenantLevelTaxonomy`. This phase owns the aggregate, its
migration under the key shape above, its Studio editor, and its renderer path. Packet 8
kept the two aggregates the walking skeleton needs to render two tenants; a block
aggregate with no editor, no renderer and no consumer for two phases is schema shipped
ahead of its rules — and the versioning rules are decided in this phase.

The block system ships with:

- Versioned block schemas under the `(tenant_id, key, schema_version)` shape above.
- Safe rendering: known key and version renders the component, known key with an unknown
  version renders `UnknownVersionBlock`, an unknown key renders `UnknownBlock`. Each
  block renders inside an error boundary, so one bad block does not take down the page.
- Per-tenant block enablement through the existence of the `TenantPageBlock` row itself
  — there is no separate flag table.
- A schema-driven editor form serving both tiers from the same code path.

### Navigation

- Header and footer menus, nested menu items.
- External links, internal page references, course and catalog references.
- Menu items are localized through the same translation pattern as pages, and a menu
  item pointing at an unpublished page in the requested locale is hidden rather than
  rendered as a dead link.

### Media Library and the Transcoding Pipeline

Storage and asset management:

- Upload to SeaweedFS through `IStorageProvider`
  ([ADR-0029](../decisions/0029-object-storage-seaweedfs.md)), under the tenant- and
  organization-scoped key layout in [Media Pipeline](../architecture/16-media-pipeline.md).
- `MediaAsset` and `MediaVariant`, asset metadata, folder and tag organisation, image
  dimensions, file-type validation, size limits.
- Public / tenant-scoped / per-user access tiers with signed URL minting, and the public
  asset URL strategy.

**This phase owns the media processing pipeline.** No phase currently does:
[Media Pipeline](../architecture/16-media-pipeline.md) describes `IVideoTranscoder`,
ffmpeg workers, HLS renditions and a managed-service scale path in full detail, and the
roadmap never names a phase that builds any of it — while
[Phase 08c](phase-08c-classroom.md) explicitly states that classroom recordings are
*not* transcoded there. Media is authored in this phase, so the pipeline belongs here.

- **Images** — `IImageProcessor` with an in-process implementation: variants at `w400`,
  `w800`, `w1600` and a 128×128 thumbnail, WebP plus a JPEG/PNG fallback, synchronous
  under 5 MB and queued above it. EXIF stripped except orientation. SVG sanitised
  (no `<script>`, no external `xlink:href`, no event handlers) before it is ever served.
- **Video** — `IVideoTranscoder` in `LearnStack.SharedKernel`, with an **ffmpeg-backed
  worker as the default implementation**: `ffprobe` metadata, HLS renditions at 480p /
  720p / 1080p, poster image, sprite-sheet thumbnail strip. The worker runs on the
  Hangfire infrastructure wired in [Phase 02b](phase-02b-events-auth.md), on its own
  queue, with the ffmpeg binary version pinned in the container image and asserted at
  startup.
- **The managed alternative is not built here.** Mux, AWS MediaConvert and Cloudflare
  Stream sit behind the same `IVideoTranscoder` port and land in
  [Phase 11](phase-11-production-hardening.md), on the demand-gating discipline of
  [ADR-0035](../decisions/0035-demand-gated-infrastructure.md), under the trigger:
  in-house transcode backlog or per-minute cost exceeds the managed alternative. Until
  that trigger fires there is one implementation, and adopting a managed service is a
  composition-root change.
- Source files are retained for 30 days and then archived, so an encoding-profile change
  can be replayed.
- Transcoding is a per-tenant resource cost. Concurrent transcode jobs per tenant are
  bounded, and the bound is a `LimitKey` so it becomes plan-gated the moment
  [Phase 02c](phase-02c-hub-foundation.md) supplies a real entitlement projection.

### Search — the port and its PostgreSQL default

[ADR-0035](../decisions/0035-demand-gated-infrastructure.md) demand-gates Meilisearch
behind `ITenantSearch`, with a PostgreSQL full-text default shipping now. This is the
first phase with content to index, so the port and its default land here.

- `ITenantSearch` and `IPlatformSearch` in `LearnStack.SharedKernel`, with the signatures
  [ADR-0012](../decisions/0012-search-strategy.md) specifies.
- A PostgreSQL `tsvector` implementation over `content_entries` and the page model, with
  a per-locale generated `tsvector` column and a GIN index per locale.
- The mandatory `tenant_id` predicate is composed **inside** the implementation, not by
  callers, along with the organization term where the entity is organization-scoped.
  Direct SQL search from a handler is forbidden.
- Cross-tenant search isolation is asserted by integration test from this phase, not from
  [Phase 09](phase-09-billing-integrations-analytics.md).
- Meilisearch is **not** built here. Its trigger — search quality or scale exceeds
  PostgreSQL full-text — is expected to fire in Phase 09.

### Hosts and Canonical URLs

Page SEO metadata, canonical URLs and redirect targets need to know which hosts serve a
tenant. They read `platform_host_to_tenant` through `IHostToTenantResolver`, and
**never** call the Hub — the correction is already landed in
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md) per
[ADR-0034](../decisions/0034-hub-contract-surface-invariant.md), and this phase is the
first substantial consumer of it. An anonymous page load, and the canonical URL it
renders, must not depend on a control plane being reachable.

Registration of a custom domain remains Hub-side
([ADR-0022](../decisions/0022-custom-domain-tls.md),
[Phase 02c](phase-02c-hub-foundation.md)); the tenant-facing status viewer is
[Phase 06](phase-06-renderer-admin-studio.md). This phase reads the mapping, and does
not write it.

### Admin Studio CMS Screens

- Content type list and detail: JSON Schema editor, revision history, additive-vs-breaking
  diff on save, sample-data preview.
- Content entry list and detail: schema-driven form derived from the active revision.
- Page list and detail.
- Page builder / editor: two-tier block picker, reorder, schema-driven form, inline
  preview through the production renderer.
- Page-block editor: `TenantPageBlock` CRUD — schema, composite renderer key, version
  lifecycle.
- Custom field values on content entries, using the `TenantCustomFieldDef` surface
  delivered in [Phase 03](phase-03-identity-admin.md).
- Media library with upload, folders, variant status, and transcode job state.
- Navigation editor.
- Publish and preview controls, including per-locale readiness.

The visual drag-and-drop schema builder is [Phase 06](phase-06-renderer-admin-studio.md);
this phase ships the picker-and-reorder Studio MVP the page-builder architecture
describes.

## Deliverables

- Tenant-aware headless CMS over a single content-shape aggregate, `TenantContentType`,
  with `ContentType` removed from the domain model.
- `TenantPageBlock` complete: aggregate, migration, Studio editor, renderer path.
- The versioned key shape — `UNIQUE (tenant_id, key, schema_version)` plus the partial
  active-revision index — applied to every versioned customization aggregate, with the
  additive-vs-breaking diff enforced at save time.
- Page, page version, per-locale slug, SEO metadata, redirect and preview model.
- Translation tables for every routable entity this phase introduces, each carrying
  `tenant_id` and its own Row Level Security policy, with slug uniqueness on
  `UNIQUE (tenant_id, locale, slug)` and `organization_id` absent from that key, plus a
  `tenant_route_slugs` registry keyed `UNIQUE (tenant_id, locale, path)` for the shared
  root namespace.
- Navigation model and editor.
- Media library on SeaweedFS with `IImageProcessor` and `IVideoTranscoder`, the
  ffmpeg-backed default worker, and a per-tenant concurrent-transcode limit expressed as
  a `LimitKey`.
- `ITenantSearch` / `IPlatformSearch` with a PostgreSQL full-text implementation over
  content entries and pages, tenant-filtered inside the port.
- Admin Studio CMS screens per the list above.
- Public read APIs for the renderer, versioned per
  [ADR-0024](../decisions/0024-api-versioning-policy.md).
- ADR-0027 Accepted, and the chosen i18n library wired into `frontend/apps/web`.

## Completion Criteria

- A tenant-scoped search returns that tenant's content entries and zero rows belonging to
  the other seed tenant, proven by integration test.
- A tenant admin creates a new `TenantContentType` from Studio with a JSON Schema,
  creates entries against it, and references those entries from a page — **without any
  LearnStack code change**.
- A tenant admin creates a `TenantPageBlock` that dispatches to a built-in composite
  renderer — again, **without any LearnStack code change**.
- Publishing a breaking schema change creates `schema_version = 2`, leaves existing
  entries rendering against version 1, and does not violate a unique constraint.
- An additive edit submitted as a `schema_revision` bump that in fact removes a field is
  rejected at save time with the offending field named.
- Exactly one `active` revision exists per `(tenant_id, key)` at all times, asserted by
  an integration test that attempts to activate a second.
- Two different courses in one tenant cannot both publish `/en/courses/beginner`; the
  second publish returns a business-rule failure naming the first. The same holds for a
  page and a redirect competing for one root path, and for an organization-scoped entity
  competing with a tenant-wide one. An integration test attempts all three and the
  database rejects each, connected as `learnstack_app`.
- When the conflicting row belongs to another organization, the failure names the slug and
  the locale but not the row — asserted by a test, because the constraint is enforced with
  Row Level Security bypassed and the handler has to make that choice deliberately.
- A page and a course in **different** tenants may hold the same slug, and each host
  serves its own.
- A page can contain multiple renderable blocks across both registry tiers, and a block
  whose `TenantPageBlock` row was deleted renders `UnknownBlock` without taking the page
  down.
- An image asset uploaded through Studio produces its variants and is usable inside a
  page block; a video asset produces an HLS master playlist, a poster and a thumbnail
  strip through the ffmpeg worker.
- An SVG containing a `<script>` element is stored sanitised and served without it.
- Draft and published versions are separated, published pages never show draft content,
  and the preview flow works for admin users.
- The same Studio editor and rendering pipeline serve both
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) seed tenants with completely
  different content-type and page-block sets, with no code branching on tenant identity.
- Canonical URLs and redirect targets resolve from `platform_host_to_tenant` with the
  Hub unreachable.

## Risks

- **Building a visually complex page builder too early.** The Studio MVP is a picker and
  reorder buttons with schema-driven forms. Drag-and-drop is
  [Phase 06](phase-06-renderer-admin-studio.md), and pulling it forward costs more than
  it looks because every interaction has to survive the two-tier registry.
- **Ignoring block schema versioning under deadline pressure.** Editing a published
  schema in place is one line and works in every test a developer writes, because their
  test data is new. It breaks the tenant whose pages are three months old. The save-time
  diff exists precisely because the failure is invisible to the person making it.
- **Re-introducing per-domain block keys into code.** `english.*` and `yoga.*`
  identifiers are forbidden by ADR-0018 and caught by
  `Core_Modules_HaveNo_DomainSpecific_Names` from
  [Phase 02a Packet 10](phase-02a-kernel-tenancy.md). Tenant data expresses the
  per-domain shape.
- **The unified content-type decision half-landing.** Deleting `ContentType` from the
  domain model is easy; the risk is a Content-module class quietly re-creating it
  because reading Customization through an application contract is slightly more work
  than a navigation property. A cross-module EF navigation is forbidden by ADR-0010 and
  is what a reviewer should look for first in this phase's diffs.
- **Slug uniqueness drifting back into per-table folklore.** The rule lives in
  [Localization Standards § Pattern A](../standards/08-localization.md) as the pattern every
  translation table follows, and [Phase 02d](phase-02d-walking-skeleton.md) already applies
  it to `Course` and `Lesson`. The recurring temptation is to add `organization_id` to the
  key when an entity is `[OrganizationScoped]` — it reads as the careful thing to do, and it
  is the defect: it stops constraining tenant-wide rows entirely, and it re-opens the
  cross-tier collision. A migration review that sees `organization_id` inside a slug unique
  key should stop there.
- **The transcoding pipeline becoming an operational surprise.** ffmpeg workers are
  CPU-heavy and easy to under-provision. The per-tenant concurrency bound and the
  queue's own metrics ship with the worker, not after the first backlog.
- **Designing media access without public / private separation**, or building CMS and
  the education catalog as disconnected systems. Both are corpus-level regressions;
  the catalog in [Phase 05](phase-05-education-learning-content.md) composes the same
  blocks and the same content types.
- **ADR-0027 slipping past the exit gate.** An i18n library chosen after the Studio and
  the public renderer already have strings is a mechanical but wide refactor. The gate
  is there to make the cost visible while it is still small.

## Phase Exit Decision

[Phase 05](phase-05-education-learning-content.md) begins when all of the following
hold:

- A tenant admin composes a complete, localized, published page from tenant-defined
  content types and page blocks, with media, without a LearnStack code change — on both
  seed tenants, with different shapes.
- The versioned key shape is in the schema, a breaking change ships as a new
  `schema_version`, exactly one revision is active per key, and the additive-vs-breaking
  diff rejects a mis-declared edit.
- `ContentType` no longer exists anywhere in the corpus or the codebase; every content
  shape resolves through `TenantContentType`.
- Slug uniqueness is enforced by a constraint that can actually reject a row — proven by
  integration tests for the same-tier, cross-tier and cross-table cases — and
  `tenant_route_slugs` covers the shared root namespace that no per-table constraint can
  see.
- Image and video assets are processed end to end by the in-house pipeline, and
  `IVideoTranscoder` has exactly one registered implementation with the managed
  alternative recorded against [Phase 11](phase-11-production-hardening.md) and its
  trigger.
- ADR-0027 is **Accepted** and the chosen library is wired, not merely selected.
- Public rendering and Admin Studio work can proceed against a stable content contract —
  which is what [Phase 06](phase-06-renderer-admin-studio.md) assumes.
