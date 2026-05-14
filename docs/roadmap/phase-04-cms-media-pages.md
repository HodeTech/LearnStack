# Phase 04: Headless CMS, Page Builder, and Media Library

## Goal

Turn LearnStack into an education-aware headless CMS and page composition platform, not merely a course management system.

This phase enables landing pages, blog content, catalog pages, campaign pages, and product-specific page blocks.

## Scope

### Content Type System

- Content type definition.
- Field definition.
- Field validation.
- Required and optional fields.
- Field types:
  - Text
  - Rich text
  - Number
  - Boolean
  - Date/time
  - Media reference
  - Entry reference
  - Select/multi-select
  - JSON/object
- Content entry CRUD.
- Draft and published states.

### Page Model

- Page.
- Page version.
- Slug.
- SEO metadata.
- Locale readiness.
- Draft/publish workflow.
- Preview token.
- Redirect model.

### Page Blocks

Initial block types:

- Hero.
- Rich text.
- Image.
- Video.
- CTA.
- Feature list.
- Course list.
- Instructor list.
- FAQ.
- Testimonial.
- Pricing teaser.
- Custom embed.

The block system should be ready for:

- Tenant-specific block enablement.
- Product-specific block registration.
- Versioned block schema.
- Safe rendering.

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

- Content type list/detail.
- Content entry list/detail.
- Page list/detail.
- Page builder/editor.
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

- A tenant admin can create and publish a page.
- A page can contain multiple renderable blocks.
- A media asset can be uploaded and used inside a page block.
- Draft and published versions are separated.
- Published pages do not accidentally show draft content.
- Preview flow works for admin users.

## Risks

- Building a visually complex page builder too early.
- Ignoring block schema versioning.
- Designing media access without public/private separation.
- Building CMS and education catalog as disconnected systems.

## Phase Exit Decision

When this phase is complete, public rendering and Admin Studio UI work can become more serious.

