# Phase 06: Public Site Renderer and Admin Studio

## Goal

Turn CMS and education catalog data into real product experiences: a public tenant site renderer and a usable Admin Studio.

After this phase, LearnStack should be able to publish a simple but real education website for a tenant.

## Scope

### Public Site Renderer

The Next.js `web` app should support:

- Tenant resolution by host.
- Published page rendering.
- Page block rendering.
- Navigation rendering.
- Course catalog page.
- Course detail page.
- SEO metadata.
- 404 and redirect handling.
- Basic theme tokens.

### Tenant Branding

- Logo.
- Primary and secondary colors.
- Typography tokens.
- Header/footer settings.
- Basic theme preview.

### Admin Studio — Content Surface

Phase 03 delivers the **identity-management** surface of Admin Studio (login, tenant switcher, users, roles, invitations). Phase 06 picks up where that ends and delivers the **content-management** surface; the two should reuse the same shell, layout, and permission system.

- Content management (content types, content entries, draft / publish flow).
- Page builder / editor (block picker, reorder, schema-driven form, preview).
- Media library (upload, folders, asset management).
- Navigation editor.
- Course catalog management (course CRUD, version selection, SEO metadata).
- Course structure editor (modules, lessons, lesson items).
- Preview of published and draft content.
- Tenant branding configuration surface (logo, theme tokens, header / footer).

Login, tenant switcher, dashboard shell, users, roles, invitations, and audit log views are **not** scoped to this phase — they are delivered in Phase 03 and are consumed by Phase 06 as the surrounding shell.

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
- Published pages are visible on the public site.
- Course catalog and course detail pages render correctly.
- Admin can preview draft pages.
- Tenant branding is reflected on the public site.
- Admin Studio is usable for core content and catalog workflows.

## Risks

- Polishing Admin Studio too early instead of validating workflows.
- Writing the renderer with a single-tenant assumption.
- Coupling page block rendering too tightly to the editing model.
- Treating SEO requirements as a later patch.

