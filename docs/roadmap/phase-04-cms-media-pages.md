# Phase 04: Headless CMS, Page Builder, and Media Library

## Goal

Turn LearnStack into an education-aware headless CMS and page composition platform, not merely a course management system.

This phase enables landing pages, blog content, catalog pages, campaign pages, and product-specific page blocks.

Decisions consumed in this phase:

- [ADR-0008 Localization Schema](../decisions/0008-localization-schema.md). All
  tenant-owned content tables introduced here adopt the side-table or JSONB-localized
  pattern declared in the ADR. The `tenant_locales` table (owned by the Tenancy
  module) is created in this phase if it has not landed earlier.
- [ADR-0013 Page Block Schema Versioning](../decisions/0013-page-block-schema-versioning.md).
  Every block ships with a `(key, schemaVersion)` tuple, an immutable JSON schema, and
  a registered renderer. Lazy migration and the `UnknownVersionBlock` / `UnknownBlock`
  placeholders are part of the renderer contract from day one.
- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
  (supersedes ADR-0011). **Content types** are declared as `TenantContentType` rows
  (JSON Schema). **Page blocks** are declared as `TenantPageBlock` rows
  (JSON Schema + composite-renderer key). Both surfaces are tenant data, not code; the
  customization aggregates were scaffolded in Phase 02a, and Phase 04 lights up the
  editing + rendering paths.

## Scope

### Content Type System

Content types are **tenant-defined** per
[ADR-0018](../decisions/0018-tenant-driven-customization-model.md). The
`TenantContentType` aggregate (already scaffolded in Phase 02a) gains its full editor
and runtime surface here:

- `TenantContentType` JSON Schema editor in Admin Studio (tenant admin defines a
  schema; the system validates it on save).
- Built-in primitive field types composed by the JSON Schema:
  - Text, rich text, number, boolean, date/time
  - Media reference, entry reference
  - Select / multi-select
  - JSON / object
- `ContentEntry` CRUD per type (the entry's `data` column stores the JSON shape
  matching the active schema version).
- Draft and published states.
- Schema-version migration path (lazy on entry save; bulk migration as a tenant-admin
  operation).

### Page Model

- Page.
- Page version.
- Slug.
- SEO metadata.
- Locale readiness.
- Draft/publish workflow.
- Preview token.
- Redirect model.

### Page Blocks (Two-Tier Registry)

The page-builder resolver consults two tiers in order
([17-page-builder.md](../architecture/17-page-builder.md)):

**Tier 1 — Built-in primitive blocks** (code-registered, closed set, changes only
with a LearnStack release):

- Hero, rich text, image, video, CTA.
- Feature list, FAQ, testimonial.
- Generic composite renderers: `content-list`, `card-grid`, `default-card` (these are
  the composites that tenant-defined blocks dispatch to).

**Tier 2 — Tenant-defined blocks** via `TenantPageBlock` rows. A tenant defines a
schema + composite-renderer key (e.g. `vocabulary-list` → `content-list`) and the
runtime renders by reading the schema, querying the data, and dispatching to the
chosen composite. There are **no vertical-prefixed block keys** — a tenant's
"vocabulary list" block is a `TenantPageBlock` row, not a code identifier.

The block system ships with:

- Versioned block schema (`(key, schemaVersion)` per
  [ADR-0013](../decisions/0013-page-block-schema-versioning.md)).
- Safe rendering with `UnknownVersionBlock` / `UnknownBlock` placeholders.
- Per-tenant block enablement via `TenantPageBlock` rows (no flag table).
- Schema-driven editor form for both built-in and tenant-defined blocks.

### Navigation

- Header menu.
- Footer menu.
- Nested menu items.
- External links.
- Internal page references.
- Course and catalog references.

### Media Library

- MinIO upload.
- Asset metadata.
- Folder and tag organization.
- Image dimensions.
- File type validation.
- File size limits.
- Signed/private access readiness.
- Public asset URL strategy.

### Admin Studio CMS Screens

- Content type list/detail (over `TenantContentType` rows — JSON Schema editor,
  schema-version tracking, sample-data preview).
- Content entry list/detail (schema-driven form derived from the active
  `TenantContentType`).
- Page list/detail.
- Page builder/editor (two-tier block picker; both primitive and tenant-defined
  blocks).
- Page-block editor (`TenantPageBlock` CRUD: schema + composite renderer key).
- Media library.
- Navigation editor.
- Publish/preview controls.

## Deliverables

- Tenant-aware headless CMS.
- Page builder data model.
- Media upload and asset library.
- Draft/publish workflow.
- APIs for public rendering.

## Completion Criteria

- A tenant admin can create a new `TenantContentType` (e.g. `VocabularyCard`) from
  Studio with a JSON Schema, create entries against it, and reference those entries
  from a page — **without any LearnStack code change**.
- A tenant admin can create a `TenantPageBlock` (e.g. `vocabulary-list`) that
  dispatches to a built-in composite renderer (e.g. `content-list`) — again, **without
  any LearnStack code change**.
- A page can contain multiple renderable blocks across the two-tier registry.
- A media asset can be uploaded and used inside a page block.
- Draft and published versions are separated.
- Published pages do not accidentally show draft content.
- Preview flow works for admin users.
- The same Studio editor and rendering pipeline serves two tenants with totally
  different content-type / page-block sets without code branching.

## Risks

- Building a visually complex page builder too early.
- Ignoring block schema versioning.
- Designing media access without public/private separation.
- Building CMS and education catalog as disconnected systems.
- Re-introducing per-domain block keys (`english.*`, `yoga.*`) into code — explicitly
  forbidden by ADR-0018. Tenant data, not code, expresses the per-domain shape.

## Phase Exit Decision

When this phase is complete, public rendering and Admin Studio UI work can become more serious.

