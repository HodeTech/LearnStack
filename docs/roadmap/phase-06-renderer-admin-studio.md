# Phase 06: Public Site Renderer and Admin Studio

## Goal

Turn CMS and education catalog data into real product experiences: a complete public
tenant site renderer, a usable Admin Studio, and the portal shell that every
learner-facing and instructor-facing screen after this phase is built into.

[Phase 02d](phase-02d-walking-skeleton.md) already put a site in a browser — a catalog
page and a lesson page, on two hosts, for two tenants. That skeleton proved the request
path. It is not a website: it has no navigation, no SEO, no error pages, one block, and
no editing surface. Phase 06 **deepens** it into something a tenant can publish.

After this phase, LearnStack publishes a simple but real education website for a tenant,
and a non-developer tenant admin can maintain it.

## Scope

### What Phase 02d already shipped

| Already exists | Phase 06 adds |
|---|---|
| Host-based tenant + organization resolution, end to end | Per-organization branding override on the resolved context |
| Catalog page and lesson page, Server Components over the typed SDK | Navigation, SEO metadata, 404 and redirect handling, full page composition |
| One built-in content primitive | The complete two-tier block registry with safe-render placeholders |
| Branding tokens read from `TenantSettings` | The branding configuration surface that writes them |
| First frontend tests, replacing the `--passWithNoTests` placeholder | The browser-level end-to-end suite |

### Public site renderer

The Next.js `apps/web` app supports:

- Tenant + **organization** resolution by host via `IHostToTenantResolver`, reading
  `platform_host_to_tenant` and never the Hub
  ([ADR-0034](../decisions/0034-hub-contract-surface-invariant.md)).
- Published page rendering composed from the two-tier block resolver: built-in primitive
  blocks (Tier 1) plus tenant-defined blocks (Tier 2) via `TenantPageBlock`, with
  `UnknownVersionBlock` / `UnknownBlock` placeholders and a React error boundary per
  block ([ADR-0013](../decisions/0013-page-block-schema-versioning.md)).
- Navigation rendering — header, footer, nested items, internal page and catalog
  references.
- Course catalog page and course detail page, driven by tenant-defined blocks over the
  [Phase 05](phase-05-education-learning-content.md) catalog data.
- SEO metadata: per-page title and description, canonical URLs, Open Graph, sitemap and
  `robots.txt` per host.
- 404 and redirect handling, including the redirect model
  [Phase 04](phase-04-cms-media-pages.md) ships. An unknown **host** returns 404 with no
  platform disclosure; an unknown **path on a known host** returns the tenant's own 404
  page.
- Theme tokens with optional **per-organization branding override** merged on top of
  tenant defaults.
- **Entitlement-aware UI** — `useFeatureFlag(FeatureKey)` and `useLimit(LimitKey)` read
  the entitlement projection. Feature gating hides nav items and tabs rather than
  disabling them; limit visualisation shows `current/limit` in Studio. The UI mirrors
  the server's decision; it never is the decision.
- WCAG 2.2 AA across public site, Studio and portal
  ([Accessibility Standards](../standards/16-accessibility.md)).

### Block registry — one canonical location

Block and renderer keys have drifted across the corpus: the same identifier appears as a
built-in block in one document, as a composite renderer in another, and in the glossary
as an example of a set it belongs to in neither. `card-grid` is the concrete case — it
is named as a built-in block in
[17-page-builder.md](../architecture/17-page-builder.md), registered as a block in the
resolver example in
[14-frontend-architecture.md](../architecture/14-frontend-architecture.md), described as
a composite renderer key in the [glossary](../glossary.md) and in phase documents, and
it appears in **no** registry at all.

This phase is where the registry becomes real code, so this is where the drift is
settled. Two registries, two owners, no overlap:

| Key kind | Canonical location | Example |
|---|---|---|
| Built-in **block** keys (Tier 1 of the page-builder resolver) | [17-page-builder.md](../architecture/17-page-builder.md) | `hero`, `rich-text`, `image` |
| **Primitive** and **composite renderer** keys (what blocks and tenant-defined types dispatch to) | [32-tenant-customization-model.md § 2](../architecture/32-tenant-customization-model.md) | `markdown`, `default-card`, `content-list`, `lesson-shell` |

Rules that follow:

- `card-grid` is a **composite renderer key**. It is added to the composite set in
  32-tenant-customization-model and removed from the built-in block list in
  17-page-builder, which is where it was miscategorised.
- The [glossary](../glossary.md) **cites** these registries and never introduces a key.
  A term document that invents a member of a closed set makes the set open.
- A new key enters the registry document first, then the code, then any prose that
  mentions it — the same order [Standards 21](../standards/21-architecture-tests-catalogue.md)
  requires for test identifiers, and for the same reason.
- The frontend test suite asserts the shipped registry and the registry documents agree
  in both directions: every documented key resolves to a component, and every registered
  component is documented. A closed set that nothing checks is a convention.

### Tenant branding

- Logo.
- Primary and secondary colours.
- Typography tokens.
- Header / footer settings.
- Theme preview.
- **Per-organization branding override** (`OrganizationBranding`) — an optional row that
  merges on top of the tenant default at render time, applied when the resolved request
  carries an organization id.

### Admin Studio — screen ownership

Four documents have claimed overlapping sets of Studio screens. **This table is the
single ownership record**; a phase document lists its own screens, and this table says
which phase that is. The finished information architecture — how the screens group into
a menu — is described in
[32-tenant-customization-model.md § 9](../architecture/32-tenant-customization-model.md);
that tree is descriptive, not a delivery plan.

| Screen group | Owning phase |
|---|---|
| Studio shell, login, tenant switcher, org switcher, dashboard chrome, users, roles, invitations, `TenantCustomFieldDef` editor, tenant IdP federation surface | [Phase 03](phase-03-identity-admin.md) |
| Content types, content entries, pages, page builder, `TenantPageBlock` editor, media library, navigation editor, publish / preview controls | [Phase 04](phase-04-cms-media-pages.md) |
| Programs, courses, course structure, modules, lessons, lesson items, `TenantLessonItemType` editor, `TenantLevelTaxonomy` editor, `TenantCompletionRule` editor, course publish flow | [Phase 05](phase-05-education-learning-content.md) |
| Branding and theme configuration, per-organization branding override, tenant settings (feature-flag overrides, read-only entitlement projection viewer, read-only custom-domain status), audit log viewer, the customization editor consolidation, cross-phase Studio information architecture | **Phase 06** |
| Notification template library (`TenantTemplateLibrary`), assessment and question-bank screens, `TenantScoringRule` editor | [Phase 08a](phase-08a-assessment-notifications.md) |
| Instructor availability, session and booking management | [Phase 08b](phase-08b-scheduling.md) |
| Classroom session monitoring and recording metadata | [Phase 08c](phase-08c-classroom.md) |
| Orders, invoices, analytics dashboards | [Phase 09](phase-09-billing-integrations-analytics.md) |

Operator-facing screens — plan editing, tenant provisioning, custom-domain registration,
licence issuance — are **not** Admin Studio. They belong to the operator portal, a
separate application in the `learnstack-hub` repository
([ADR-0019](../decisions/0019-learnstack-hub.md)). Studio's custom-domain surface is
read-only status ([27-custom-domain-tls.md](../architecture/27-custom-domain-tls.md)).

**Phase 06's own Studio work**, in detail:

- Tenant branding configuration surface (logo, theme tokens, header / footer) plus the
  per-organization override editor.
- Tenant settings: feature-flag editor for `tenant_feature_flags` overrides
  ([21-feature-flags.md](../architecture/21-feature-flags.md)), read-only entitlement
  projection viewer for plan-level features and limits, read-only custom-domain status
  viewer.
- **Audit log viewer** — paginated, filterable view over the Audit module's read API.
  Tenant admins see their tenant; organization admins see their organization
  ([31-audit-subsystem.md](../architecture/31-audit-subsystem.md)).
- **Customization editor consolidation** — the per-aggregate editors ship with their
  owning phases; Phase 06 assembles them into one coherent surface with a shared JSON
  Schema editor component and a shared sandboxed-DSL editor component, so a tenant admin
  meets one editing idiom rather than four.
- Preview of published and draft content, using the production renderer in preview mode.
- The Studio information-architecture pass: navigation, permission-aware menu
  construction, and empty / loading / error states applied consistently across screens
  delivered by Phases 03 through 05.

### Portals — the `(portal)` route group and the instructor portal

Three later phases require an instructor-facing surface and none has owned it:
[Phase 08b](phase-08b-scheduling.md) needs availability editing and a booking digest,
[Phase 08c](phase-08c-classroom.md) has the instructor joining a session "from the
portal", and [Phase 10](phase-10-english-learning-mvp.md) walks an instructor through a
live class. **Phase 06 owns it.**

- Phase 06 delivers the `(portal)` route group in `apps/web`
  ([ADR-0009](../decisions/0009-frontend-single-app-first.md)): the authenticated shell,
  layout, role-aware navigation, permission-aware elements, and the tenant + organization
  + locale resolution the portal shares with the public site.
- Phase 06 delivers the **instructor portal's first screens** — the instructor's own
  courses drawn from the catalog, their profile with tenant-defined custom fields, and
  preview-as-learner for content they author.
- [Phase 07](phase-07-enrollment-learner-portal.md) delivers the **learner portal**
  screens inside this shell — my courses, course overview, lesson player, progress.
- Every later phase that needs an instructor screen adds it to the portal that exists
  from this phase: availability and booking digest in
  [Phase 08b](phase-08b-scheduling.md), session join in
  [Phase 08c](phase-08c-classroom.md). None of them invents a surface.

### UI foundation

- Shared UI package.
- Form components.
- Data table.
- Modal / drawer.
- Toast / notification.
- Empty / loading / error states.
- Permission-aware UI elements.

### API client

- Typed frontend SDK, generated from the OpenAPI document
  ([Phase 02a Packet 4](phase-02a-kernel-tenancy.md) ships the generation scaffold; this
  phase is where it has a full surface to generate from).
- Authenticated request handling.
- Tenant context handling.
- Error mapping from RFC 7807 Problem Details to typed client errors.

### End-to-end test stack — a carried dependency

Phase 06 is the first phase to run browser tests, so it is the first phase that depends
on the ephemeral end-to-end stack actually working. `infra/compose/e2e.yml` overlays the
development stack with `volumes: !reset []`, which discards more than the named volumes:
it also discards the **PostgreSQL init script** and the **SeaweedFS S3 identity file**.
A browser suite launched against that stack meets a database without the
`learnstack_app` / `learnstack_migration` roles the init script creates and an object
store with no S3 identity — so the run either fails at boot or, worse, proceeds against
a stack where the isolation roles are absent and every isolation assumption the browser
tests inherit is quietly untrue. The same overlay leaves Valkey on its named volume, so
cache and rate-limit state leaks between runs.

The fix lands in [Phase 02a Packet 3b](phase-02a-kernel-tenancy.md). It is recorded here
because Phase 06 is its consumer and the phase that notices if it regresses: a green
browser suite against a mis-provisioned stack is worse evidence than no suite.

## Deliverables

- A complete public site renderer: navigation, SEO, error and redirect handling, the
  full two-tier block registry with safe-render placeholders, and per-organization
  branding.
- The reconciled block / renderer registry, canonical in one document per key kind, with
  the code and the documents asserted to agree.
- Admin Studio's Phase 06 surface: branding, tenant settings, entitlement viewer, audit
  log viewer, customization editor consolidation, preview, and the cross-phase
  information-architecture pass.
- The `(portal)` route-group shell and the instructor portal's first screens.
- Shared UI package and the typed SDK over the full API surface.
- A browser-level end-to-end suite running against the fixed ephemeral stack.

## Completion Criteria

- A homepage can be published for a tenant and is visible on both the tenant-default
  host and a custom domain resolved via `IHostToTenantResolver`.
- Course catalog and course detail pages render with tenant-defined block shapes; an
  unknown block key renders the placeholder rather than breaking the page.
- Navigation, SEO metadata, sitemap, `robots.txt`, tenant 404 and redirects work per
  host.
- Tenant branding is reflected on the public site; the per-organization override applies
  when the resolved request carries an organization id.
- **Every block and renderer key that appears in any document resolves in the shipped
  registry, and every registered key is documented** — asserted by a test, in both
  directions.
- Admin Studio is usable for the content, catalog and customization workflows a
  non-developer tenant admin needs: authoring a new `TenantContentType` or
  `TenantPageBlock` end to end, with no code change.
- The entitlement projection viewer reflects the current tenant's plan feature and limit
  set; the audit log viewer surfaces the last 200 entries with filters by module, actor
  and outcome.
- An instructor signs in, reaches the portal, and sees their own courses; a learner
  reaching an instructor-only route is refused by the API, not merely hidden by the UI.
- The browser end-to-end suite runs green against `infra/compose/e2e.yml` with the
  PostgreSQL init script and the SeaweedFS S3 identity file **present**, and two
  consecutive runs are independent of one another.
- Both seed tenants — the English school and the yoga studio — produce visually and
  structurally different published sites from the same binary, now with navigation, SEO
  and full page composition rather than the two skeleton pages.

## Risks

- **Polishing Admin Studio instead of validating workflows.** The measure is whether a
  tenant admin completes an authoring task unaided, not how the screen looks.
- **Writing the renderer with a single-tenant assumption.** Two tenants have existed
  since [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) and both have been rendering
  since Phase 02d; a single-tenant assumption now is a regression, not an oversight.
- **Coupling block rendering to the editing model.** The public renderer must not import
  editor code; the editor composes the same registry, from the other side.
- **Treating SEO as a later patch.** Metadata, canonical URLs and sitemaps are structural
  in an App Router application, and retrofitting them means touching every route.
- **The registry drifting again.** Two canonical locations only work if new keys enter
  through them. Mitigated by the bidirectional registry test — the drift that produced
  `card-grid` was invisible precisely because nothing compared prose to code.
- **A green browser suite on a mis-provisioned stack.** If the Packet 3b compose fix
  regresses, the end-to-end suite still goes green while running without the isolation
  roles. Mitigated by asserting the init script and S3 identity are present as part of
  the suite's own preconditions.
- **The instructor portal being deferred again.** It is now owned here; a later phase
  that finds no portal to add a screen to should treat that as a Phase 06 exit failure,
  not build its own surface.

## Phase Exit Decision

[Phase 07](phase-07-enrollment-learner-portal.md) begins when:

- A tenant admin publishes a complete site — pages, navigation, catalog, SEO, error
  pages — for both seed tenants, without a code change, and the two sites differ in
  structure rather than only in strings.
- The block and renderer registries are canonical in one document per key kind, and the
  bidirectional registry test is green.
- The Admin Studio screen ownership table above matches what actually shipped in
  Phases 03 through 06; a screen owned by no phase is an exit blocker, not a backlog
  item.
- The `(portal)` shell exists with the instructor portal's first screens in it, so
  Phase 07 adds learner screens to a surface rather than creating one.
- The browser end-to-end suite is green against the repaired ephemeral stack, with the
  PostgreSQL init script and the SeaweedFS S3 identity file present and Valkey state
  reset between runs.
