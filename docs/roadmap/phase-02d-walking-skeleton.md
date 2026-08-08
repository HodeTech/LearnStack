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

- `Course` — tenant-owned, optionally organization-scoped, with slug, title, summary,
  and a published / draft state. No versioning, no programs, no cohorts.
- `Lesson` — ordered within a course, with slug, title, and a body rendered from a
  single built-in content primitive. No lesson items, no lesson item types, no
  completion semantics.

Both carry `[TenantOwned]`, an EF global query filter, and a Row Level Security policy
built from the canonical template in
[Database Standards](../standards/05-database.md) — the isolation machinery is
exercised by real domain tables here, not only by fixtures.

### Read API — two endpoints

From [Phase 05](phase-05-education-learning-content.md), through the API conventions
established in [Phase 02a Packet 4](phase-02a-kernel-tenancy.md):

- `GET /v1/courses/{slug}` — course detail with its lesson list.
- `GET /v1/courses/{slug}/lessons/{lessonSlug}` — lesson detail.

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
branding tokens and level taxonomy from customization data. Layout, typography and
colour come from `TenantSettings`, not from a hard-coded theme.

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
| Course versioning, programs, lesson items, completion rules | [Phase 05](phase-05-education-learning-content.md) |
| Admin Studio, navigation, SEO, full block registry | [Phase 06](phase-06-renderer-admin-studio.md) |
| Enrollment, learner portal, progress tracking | [Phase 07](phase-07-enrollment-learner-portal.md) |
| Search | [Phase 09](phase-09-billing-integrations-analytics.md) |
| Live classroom | [Phase 08c](phase-08c-classroom.md) |
| Billing | [Phase 09](phase-09-billing-integrations-analytics.md) |
| Hub, entitlement gating | [Phase 02c](phase-02c-hub-foundation.md) |

## Deliverables

- `Course` and `Lesson` aggregates in `LearnStack.Modules.Education`, with migrations,
  EF configurations, query filters and RLS policies.
- Two anonymous read endpoints with OpenAPI documentation and generated SDK clients.
- Two public route segments in `frontend/apps/web`, tenant-branded, rendering from
  customization data.
- Two hosts wired to two tenants in `platform_host_to_tenant`, resolvable in local
  development.
- Seed data extending [Phase 02a Packet 7](phase-02a-kernel-tenancy.md)'s two tenants
  with a course and a handful of lessons each.
- A demo script (`make demo` or equivalent) that boots the stack, seeds, and prints the
  two URLs.
- Frontend tests covering tenant resolution and the public / authenticated route split
  — the first real tests in `apps/web`, replacing the `--passWithNoTests` placeholder
  removed in [Phase 02a Packet 5](phase-02a-kernel-tenancy.md).

## Completion Criteria

- Opening host A shows the English school's catalog with CEFR levels and its branding;
  opening host B shows the yoga studio's catalog with its own difficulty taxonomy and
  branding. One binary, one database, one schema.
- Clicking through to a lesson on either host renders that tenant's lesson body.
- Requesting tenant B's course slug on tenant A's host returns 404 — not tenant B's
  course, and not a 500.
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

## Phase Exit Decision

Phase 02b begins when a reviewer, on a clean checkout, can open two hosts in a browser
and see two structurally and visually different education sites served by one binary,
with the cross-tenant isolation suite green under `learnstack_app` and no
tenant-conditional code anywhere in the request path.
