# Phase 06: Public Site Renderer and Admin Studio

## Goal

Turn CMS and education catalog data into real product experiences: a public tenant site renderer and a usable Admin Studio.

After this phase, LearnStack should be able to publish a simple but real education website for a tenant.

## Scope

### Public Site Renderer

The Next.js `apps/web` app should support:

- Tenant + **organization** resolution by host via `IHostToTenantResolver`
  (`platform_host_to_tenant` projection mirrored from Hub).
- Published page rendering composed from the two-tier block resolver: built-in
  primitive blocks (Tier 1) + tenant-defined blocks (Tier 2) via `TenantPageBlock`.
- Navigation rendering.
- Course catalog page (driven by tenant-defined `level-card` / `card-grid` blocks
  over the catalog data).
- Course detail page (driven by tenant-defined `LessonPackage` content type
  composites when present).
- SEO metadata.
- 404 and redirect handling.
- Basic theme tokens with optional **per-organization branding override** merged on
  top of tenant defaults.
- **Entitlement-aware UI** — `useFeatureFlag(FeatureKey)` and `useLimit(LimitKey)`
  hooks read `platform_entitlement_cache`. Feature gating hides nav items / tabs;
  limit visualization shows `current/limit` in Studio.

### Tenant Branding

- Logo.
- Primary and secondary colors.
- Typography tokens.
- Header / footer settings.
- Basic theme preview.
- **Per-organization branding override** (`OrganizationBranding`) — optional row that
  merges on top of the tenant default at render time.

### Admin Studio — Content Surface

Phase 03 delivers the **identity-management** surface of Admin Studio (login, tenant
switcher, **org switcher**, users with org filter, roles, invitations). Phase 06
picks up where that ends and delivers the **content-management** surface; the two
should reuse the same shell, layout, and permission system.

- Content management (content types, content entries, draft / publish flow).
- Page builder / editor (block picker, reorder, schema-driven form, preview) that
  surfaces both built-in primitive blocks and tenant-defined `TenantPageBlock`
  entries.
- Media library (upload, folders, asset management).
- Navigation editor.
- Course catalog management (course CRUD, version selection, SEO metadata, optional
  org scope).
- Course structure editor (modules, lessons, lesson items with custom item-type
  renderers from `TenantLessonItemType`).
- Preview of published and draft content.
- Tenant branding configuration surface (logo, theme tokens, header / footer) +
  per-organization branding override editor.
- Tenant settings surfaces: feature-flag editor for `tenant_feature_flags` overrides
  (see [21-feature-flags.md](../architecture/21-feature-flags.md)), **read-only
  entitlement projection viewer** for plan-level features + limits, and
  **custom-domain status viewer** (registration happens in the operator portal —
  Studio is read-only here, see
  [27-custom-domain-tls.md](../architecture/27-custom-domain-tls.md)).
- **Tenant Customization editor** — full CRUD on `TenantContentType`,
  `TenantPageBlock`, `TenantLessonItemType`, `TenantLevelTaxonomy`,
  `TenantScoringRule`, `TenantCompletionRule`, `TenantCustomFieldDef`,
  `TenantTemplateLibrary` per
  [32-tenant-customization-model.md](../architecture/32-tenant-customization-model.md).
  JSON Schema editor for type schemas; sandboxed DSL editor for scoring / completion
  rules.
- **Audit log viewer** — paginated, filterable view over the Audit module's read API
  (tenant admins see their tenant; org admins see their org).

Login, tenant switcher, org switcher, dashboard shell, users, roles, and invitations
are **not** scoped to this phase — they are delivered in Phase 03 and are consumed by
Phase 06 as the surrounding shell.

### UI Foundation

- Shared UI package.
- Form components.
- Data table.
- Modal/drawer.
- Toast/notification.
- Empty/loading/error states.
- Permission-aware UI elements.

### API Client

- Typed frontend SDK.
- Authenticated request handling.
- Tenant context handling.
- Error mapping.

## Deliverables

- Working public site renderer.
- Usable Admin Studio.
- Basic tenant branding support.
- Shared UI and SDK foundation.

## Completion Criteria

- A homepage can be published for a tenant.
- Published pages are visible on the public site for both tenant-default hosts and
  custom domains resolved via `IHostToTenantResolver`.
- Course catalog and course detail pages render correctly with tenant-defined block
  shapes.
- Admin can preview draft pages.
- Tenant branding is reflected on the public site; per-org override applies when the
  resolved request carries an organization id.
- Admin Studio is usable for core content, catalog, and **customization** workflows
  (a non-developer tenant admin can author a new `TenantContentType` or
  `TenantPageBlock` end-to-end without code changes).
- Entitlement projection viewer accurately reflects the plan's feature / limit set
  for the current tenant.
- Audit log viewer surfaces the last 200 entries for the current tenant with filter
  by module / actor / outcome.

## Risks

- Polishing Admin Studio too early instead of validating workflows.
- Writing the renderer with a single-tenant assumption.
- Coupling page block rendering too tightly to the editing model.
- Treating SEO requirements as a later patch.

