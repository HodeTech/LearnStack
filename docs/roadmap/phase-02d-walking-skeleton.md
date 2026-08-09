# Phase 02d: Two-Tenant Walking Skeleton

## Goal

Put a working education site in a browser — twice, on two hosts, for two tenants in
unrelated domains, served by one binary and one database.

This is the first phase whose output someone who does not read C# can evaluate. It
exists because the alternative — reaching a visible artefact only after Phases 03
through 06 — puts the project's single most testable claim (the same code paths serve
unrelated education domains) five phases away from any evidence, and puts the first
piece of user-facing value even further.

Phase 02d is a **thin vertical slice through later phases**, not a replacement for
them. Every capability it touches is delivered shallowly here and completely in its
owning phase. Phases 04 through 07 keep their full scope; each records what 02d already
shipped so the two never claim the same work twice.

Depends on [Phase 02a](phase-02a-kernel-tenancy.md) — specifically the corrected Row
Level Security template, tenant + organization resolution, the two seed tenants, and
the customization runtime read paths. Runs **before**
[Phase 02b](phase-02b-events-auth.md): the skeleton is deliberately anonymous, so it
needs no identity provider.

## Scope

### Education content — minimum viable

From [Phase 05](phase-05-education-learning-content.md), the two aggregates without
which there is nothing to render:

- `Course` — tenant-owned, optionally organization-scoped, with a published / draft state
  and an ordered lesson list. Its title, summary and per-locale slug live in
  `course_translations`, not on the parent — see § Localization schema below. No
  versioning, no programs, no cohorts.
- `Lesson` — ordered within a course, with a body rendered from a single built-in content
  primitive; its title and per-locale slug likewise live in `lesson_translations`. Which
  fields that primitive renders is driven by the tenant's own `TenantContentType`, so the
  two tenants' lesson pages differ in **shape**, not only in copy — no `ContentEntry`
  aggregate and no authoring surface, which are
  [Phase 04](phase-04-cms-media-pages.md); the lesson body carries its field values
  inline and validates against the declared schema on write. No
  lesson items, no lesson item types, no completion semantics. Its foreign key to
  `Course` is **composite on `tenant_id`**
  (`FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id)`): PostgreSQL
  evaluates referential integrity with Row Level Security bypassed, so a single-column
  key would let one tenant's lesson reference another tenant's course, invisibly. The
  same rule applies to each translation satellite's key back to its parent. See
  [Database Standards § Foreign keys between tenant-owned tables](../standards/05-database.md).

Both carry `[TenantOwned]`, an EF global query filter, and the Row Level Security
policy set from the canonical template in
[Database Standards](../standards/05-database.md) — the permissive isolation policy plus,
where the table is org-scoped, the two `AS RESTRICTIVE` write guards. The isolation
machinery is exercised by real domain tables here, not only by fixtures. This is the
first time the template is applied to anything, so it is also the first chance to find
out that it is wrong: treat a surprising query result as a template bug, not a data bug.

### Localization schema — the one-way door this phase walks through

[ADR-0008](../decisions/0008-localization-schema.md) is Accepted, it names `Course` and
`Lesson` explicitly among the side-translation-table entities, and its Consequences say
plainly: *"Slugs are stored on the translation row, not on the parent."* Phase 02d creates
the first real tenant-owned content tables in the whole roadmap, so it is the phase that
either honours that decision or spends [Phase 04](phase-04-cms-media-pages.md) undoing it
with the global migration ADR-0008's Context exists to avoid — and no later phase budgets
for that move. There is no walking-skeleton exemption.

The parents hold non-translatable columns only:

- `courses` — `id`, `tenant_id`, `organization_id` (nullable), `slug_key` (a stable
  authoring handle that **nothing routes on**), published / draft state, audit columns,
  `row_version`, plus `UNIQUE (tenant_id, id)` so `lessons` can carry its composite
  foreign key.
- `lessons` — `id`, `tenant_id`, `organization_id`, `course_id`, `sort`, state, audit
  columns.

The translatable columns live in `course_translations` and `lesson_translations`, each
keyed `PRIMARY KEY (<entity>_id, locale)` and each holding `title`, `summary` / `body`,
and the routable `slug`. Both satellites carry a real `tenant_id` column, a mirrored
`organization_id`, their own `ENABLE` + `FORCE ROW LEVEL SECURITY` with the full canonical
policy set, and a composite foreign key on `(tenant_id, <entity>_id)`. Row Level Security
is per table and is never inherited from a parent through a check constraint; a satellite
holding `title` and `slug` holds the content, so an unprotected satellite is a content
leak.

The slug constraint is `UNIQUE (tenant_id, locale, slug)` — flat across organizations,
with `organization_id` deliberately excluded. Every column in that key is `NOT NULL`, so
PostgreSQL's nulls-are-distinct rule cannot apply and the constraint rejects every
duplicate it is meant to reject. The full reasoning, including why adding `organization_id`
to the key is the tempting move that breaks it, is in
[Localization Standards § Pattern A](../standards/08-localization.md) and
[Localization § Slugs and URLs](../architecture/12-localization.md).

`tenant_locales` already exists — [Phase 02a Packet 6](phase-02a-kernel-tenancy.md) ships
it and already states it is required before any tenant-owned content table ships. This is
the phase that makes good on that.

What this phase does **not** build: a translation editor, per-locale publish readiness as a
workflow, the `tenant_route_slugs` cross-table registry, or locale negotiation from
`Accept-Language`. Those are [Phase 04](phase-04-cms-media-pages.md). What it builds is the
shape, so that adding a locale later is an `INSERT` and not a migration.

### Read API — two endpoints

From [Phase 05](phase-05-education-learning-content.md), through the API conventions
established in [Phase 02a Packet 4](phase-02a-kernel-tenancy.md):

- `GET /api/v1/courses/{slug}?locale={locale}` — course detail with its lesson list.
- `GET /api/v1/courses/{slug}/lessons/{lessonSlug}?locale={locale}` — lesson detail.

`locale` is **required**; a request without it is a 400, not a silent default. With
per-locale slugs a slug does not identify a course on its own — the same string can be one
course's Turkish slug and another's English slug inside one tenant — so locale is an
identifying input, not a preference, and a defaulted locale would let one URL resolve to
different courses as a tenant's locale set changes. It lives in the query string rather
than the path because [Localization Standards](../standards/08-localization.md) puts locale
in the path of **public URLs**, and the public URL is the renderer's
`/{locale}/courses/{slug}` route, which supplies this parameter. Any cache in front of
these endpoints keys on the query string.

Slug resolution is **exact** on `(tenant_id, locale, slug)`. The fallback chain applies to
display fields after the entity is resolved and **never** to the slug lookup: a course with
no `en` translation has no `en` URL, and requesting one is a 404. For the same reason a
lesson with no translation in the requested locale is omitted from the course's lesson list
rather than rendered as a link that cannot resolve.

Both are anonymous, both resolve tenant and organization from the host, both return
RFC 7807 Problem Details on failure, both flow through the MediatR pipeline and return
`Result<T>`. Nothing bypasses the pipeline — the tenant-context and audit machinery has
its first real caller here.

### Public renderer — two pages

From [Phase 06](phase-06-renderer-admin-studio.md), in `frontend/apps/web` under the
`(public)` route group:

- Course catalog page — lists the tenant's published courses.
- Lesson page — renders a lesson body.

Both are Server Components fetching through the typed SDK. Both read the tenant's
branding tokens, level taxonomy and lesson-body `TenantContentType` from customization
data — the lesson page renders the field list the tenant declared, not a fixed one.
Layout, typography and colour come from `TenantSettings`, not from a hard-coded theme.

### Host-based tenant resolution, end to end

The full path from [Phase 02a Packet 7](phase-02a-kernel-tenancy.md), exercised for
real: an inbound request's `Host` header resolves through `platform_host_to_tenant` to
a `(tenant_id, organization_id?)` pair, the request-scoped `ITenantContext` is
populated, the transaction sets `app.tenant_id` / `app.organization_id` with
`SET LOCAL`, and Row Level Security filters every read.

Two hosts are registered in local development, one per seed tenant.

### Genericity proof

Both seed tenants — the English school and the yoga studio from
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md) — render their own catalog and lesson
pages from their own customization data:

| Tenant | `TenantLevelTaxonomy` | `TenantContentType` | Branding |
|---|---|---|---|
| English school | CEFR levels (A1 … C2) | `GrammarTopic` | Its own tokens |
| Yoga studio | Difficulty levels (Foundation … Advanced) | `AsanaPose` | Its own tokens |

The two sites differ in taxonomy, content shape, copy and visual identity. The binary,
the schema and the query paths are identical. If a code path has to branch on which
tenant it is serving, [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)
is not being honoured and the branch is the bug.

### Explicitly not in this phase

Named so that no reader has to guess, and so no later phase can assume it was done
here:

| Capability | Owning phase |
|---|---|
| Authentication, sessions, login | [Phase 02b](phase-02b-events-auth.md) |
| Identity domain, roles, permissions, invitations | [Phase 03](phase-03-identity-admin.md) |
| CMS editing, page builder, media library | [Phase 04](phase-04-cms-media-pages.md) |
| Translation editor, per-locale publish readiness workflow, `tenant_route_slugs` registry | [Phase 04](phase-04-cms-media-pages.md) |
| Course versioning, programs, lesson items, completion rules | [Phase 05](phase-05-education-learning-content.md) |
| Admin Studio, navigation, SEO, full block registry | [Phase 06](phase-06-renderer-admin-studio.md) |
| Enrollment, learner portal, progress tracking | [Phase 07](phase-07-enrollment-learner-portal.md) |
| Search — the `ITenantSearch` port and its PostgreSQL default | [Phase 04](phase-04-cms-media-pages.md) |
| Search — the Meilisearch adapter behind that port | [Phase 09](phase-09-billing-integrations-analytics.md) |
| Live classroom | [Phase 08c](phase-08c-classroom.md) |
| Billing | [Phase 09](phase-09-billing-integrations-analytics.md) |
| Hub, entitlement gating | [Phase 02c](phase-02c-hub-foundation.md) |

## Deliverables

- `Course` and `Lesson` aggregates in `LearnStack.Modules.Education`, with migrations,
  EF configurations, query filters and RLS policies.
- `course_translations` and `lesson_translations` satellites, each with its own
  `tenant_id`, mirrored `organization_id`, `ENABLE` + `FORCE ROW LEVEL SECURITY` and full
  policy set, composite foreign key on `(tenant_id, <entity>_id)`, and
  `UNIQUE (tenant_id, locale, slug)`.
- Two anonymous read endpoints with OpenAPI documentation and generated SDK clients,
  taking `locale` as a required parameter.
- Two public route segments in `frontend/apps/web`, tenant-branded, rendering from
  customization data.
- Two hosts wired to two tenants in `platform_host_to_tenant`, resolvable in local
  development.
- Seed data extending [Phase 02a Packet 7](phase-02a-kernel-tenancy.md)'s two tenants
  with a course and a handful of lessons each. One of the two tenants has **two** enabled
  locales with genuinely different slugs per locale, so the schema is exercised rather
  than merely declared; the other has one.
- A demo script (`make demo` or equivalent) that boots the stack, seeds, and prints the
  two URLs.
- Frontend tests covering host-to-tenant resolution and `(public)` route rendering —
  the first real tests in `apps/web`, replacing the `--passWithNoTests` placeholder
  removed in [Phase 02a Packet 3b](phase-02a-kernel-tenancy.md). There is no
  authenticated route to test against yet; that split arrives with
  [Phase 02b](phase-02b-events-auth.md)'s session.
- **Two CI jobs activate here.** [Phase 01](phase-01-repository-tooling.md) scaffolded
  three `if: false` placeholders against the phase each expected to unblock it; two of
  them unblock now, earlier than that phase predicted:
  - **OpenAPI breaking-change check** — Phase 01 expected Phase 03, because that was
    where the first real `/api/v1/*` endpoint was going to replace `/healthz`. The two
    read endpoints above are that first endpoint.
  - **Lighthouse budget** — Phase 01 expected Phase 04, because that was where the
    first content-bearing public page was going to ship. The catalog and lesson pages
    above are that first page, and they are the right ones to hold a budget against:
    they are what a visitor actually loads.

  The third placeholder, the integration-test job, activates earlier still — in
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md), with the first isolation test.

## Completion Criteria

- Opening host A shows the English school's catalog with CEFR levels and its branding;
  opening host B shows the yoga studio's catalog with its own difficulty taxonomy and
  branding. One binary, one database, one schema.
- Clicking through to a lesson on either host renders that tenant's lesson body.
- The two lesson pages render **different field sets**, driven by each tenant's
  `TenantContentType` — not the same template with different strings. A reviewer can see
  the difference without reading the seed data.
- Requesting tenant B's course slug on tenant A's host returns 404 — not tenant B's
  course, and not a 500.
- The two-locale tenant serves the same course at two different slugs under two locale
  prefixes, and adding a third locale is an `INSERT` into `tenant_locales` plus
  translation rows — no migration.
- Two courses in one tenant cannot both hold one `(locale, slug)` pair; the second insert
  is rejected by the database, and the rejection also fires when one of the two is
  tenant-wide and the other organization-scoped. An integration test attempts both,
  connected as `learnstack_app`.
- Requesting a slug in a locale the course has no translation for returns 404, and a
  lesson with no translation in the requested locale does not appear in the course's
  lesson list.
- The isolation integration tests from
  [Phase 02a Packet 7](phase-02a-kernel-tenancy.md) still pass, now with real
  `Course` and `Lesson` rows rather than fixtures, and still run as `learnstack_app`.
- No code path branches on tenant identity. A reviewer can grep for the two tenant
  slugs and find them only in seed data and tests.
- `make demo` on a clean checkout produces both working sites.

## Risks

- **The slice grows.** Every capability listed under "explicitly not in this phase" has
  a plausible argument for inclusion. The exit gate is a browser, not a feature set; if
  a change does not move a pixel on one of the two pages, it belongs to its owning
  phase.
- **The second tenant becomes decorative.** A yoga studio whose data is a renamed copy
  of the English school's proves nothing. Its taxonomy, content type and page
  composition must differ in shape, not only in strings.
- **Tenant-specific branching creeps into the renderer.** The most likely place is the
  block or content-type resolution path, where a missing generic primitive is easiest to
  paper over with a conditional. Any such branch is a defect in the customization model
  and should be fixed there.
- **Shortcuts around the pipeline.** Two read endpoints are simple enough to write as
  direct queries. Doing so skips the tenant-context behavior and the RLS session
  variables — the exact machinery this phase exists to exercise.
  `Handlers_Return_Result` catches the shape; reviewers catch the intent.
- **The translation tables get written as an afterthought.** The satellite is a
  tenant-owned table in its own right: its own `tenant_id`, its own policy, its own
  `FORCE`. The failure mode is quiet — the parent's policy looks like it covers the child,
  and the child is where the title and the slug actually live. An isolation test that
  reads only the parent will not find it.

## Phase Exit Decision

Phase 02b begins when a reviewer, on a clean checkout, can open two hosts in a browser
and see two structurally and visually different education sites served by one binary,
with the cross-tenant isolation suite green under `learnstack_app` and no
tenant-conditional code anywhere in the request path.
