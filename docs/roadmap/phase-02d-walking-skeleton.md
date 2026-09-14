# Phase 02d: Two-Tenant Walking Skeleton

> **Status (2026-09-14).** Phase 02d **in progress**. The kickoff, `P02d-0`, ships this
> plan — the inherited baseline, the packet table, the decision register, criteria that
> name their evidence, and the corrections to the documents that contradicted the phase
> — and no code. Every later packet opens with its decision pass and updates its own
> row, linking its delivery record.
>
> | Packet | Title | State |
> |---|---|---|
> | P02d-0 | Kickoff | ✅ this plan |
> | P02d-1 | Education schema and database-level isolation | ✅ complete — 2026-09-14; [delivery record](#delivery-record-p02d-1) |
> | P02d-2 | Writers and seed | not started |
> | P02d-3 | Read internals | not started |
> | P02d-4 | Public read API and contract checks | not started |
> | P02d-5 | Server-rendering path | not started |
> | P02d-6 | Public renderer | not started |
> | P02d-7 | Demo, full-stack CI and exit | not started |

## Goal

Put a working education site in a browser — twice, on two hosts, for two tenants in
unrelated domains, served by one backend binary
([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)), one `apps/web`
application and one database.

This is the first phase whose output someone who does not read C# can evaluate. It
exists because the alternative — reaching a visible artefact only after Phases 03
through 06 — puts the project's single most testable claim (the same code paths serve
unrelated education domains) five phases away from any evidence, and puts the first
piece of user-facing value even further.

Phase 02d is a **thin vertical slice through later phases**, not a replacement for them.
Every capability it touches is delivered shallowly here and completely in its owning
phase. Phases 04 through 07 keep their full scope; each records what 02d already shipped
so the two never claim the same work twice.

Depends on [Phase 02a](phase-02a-kernel-tenancy.md) — specifically the corrected Row
Level Security template, tenant + organization resolution, the two seed tenants, and the
customization aggregates with the validated write path that fills them. **The
customization read path is this phase's own work**: the projection keyed on
`customization_generations.generation`, and its generation-keyed cache, land here with
the renderer that is their first consumer
([§ Customization read path](#customization-read-path)), which is why 02a stops at the
data being resolvable and isolated
([the module spec](../modules/customization/README.md) is the single record of it). Runs
**before** [Phase 02b](phase-02b-events-auth.md): the skeleton is deliberately
anonymous, so it needs no identity provider.

### What this phase inherits

Most of what this phase meets is already shipped or already decided. Each item links its
owner; where a choice is still open, it names the register row that answers it.

- **The anonymous surface.** A context resolved from the host alone reaches only request
  types marked `[PublicSurface]`, and a refusal answers the unknown-host `404`
  ([API Standards § Public surface](../standards/04-api-design.md#public-surface)). That
  section's table is empty; its first rows are this phase's, and
  `PublicSurface_Marker_Set_Is_Enumerated` passes over an empty set until then. Every
  request type reaching the audit step is classified, `Off` included
  ([ADR-0044](../decisions/0044-audit-write-path.md)). **G28**.
- **The trusted hop.** The hop predicate, its header names, the startup refusal of a
  half-configured hop and `EffectiveHostAccessor` shipped in
  [Phase 02a Packet 4](phase-02a-kernel-tenancy.md#delivery-record-packet-4).
  [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) names this phase
  twice — § Consequences refers to "Phase 02d's browser test", and § Implementation
  Notes lists the anonymous two-host render in its binding runtime matrix — and that
  matrix's "direct socket bypassing the hop" row is exercised today only over a
  constructed `HttpContext`, never over HTTP.
  [Testing Standards § End-to-End Tests](../standards/06-testing.md#end-to-end-tests)
  gates this phase on a human opening the sites. **G33**.
- **The anonymous rate limiter.** One global limiter runs before host classification and
  partitions every request on its socket peer at the anonymous budget
  ([API Standards § Request and Response Limits](../standards/04-api-design.md#request-and-response-limits)
  holds the value; the catalogue's `Anonymous_Requests_Are_Rate_Limited_Per_Peer` holds
  the rule). This phase's pages fetch from the server, so every call they make reaches
  the API from the renderer's peer. **G34**.
- **The first minted cursor.**
  [API Standards § Pagination](../standards/04-api-design.md#pagination) leaves the
  payload to whoever mints it, and
  [Phase 02a](phase-02a-kernel-tenancy.md#completion-criteria) handed this phase the
  `400` for a cursor its minter cannot read. **G10**.
- **Tenant settings.** `tenant_settings` holds tenant-wide and organization-scoped rows,
  unique per `(tenant_id, organization_id, key)` with nulls not distinct; the
  organization-over-tenant fallback is documented on `TenantSetting` and implemented by
  no reader; the domain checks only that a value is well-formed JSON; and Phase 02a left
  the typed accessor to its first reader, this renderer. `tenancy.setting.write` is
  MUST-class and `(planned)` in [the Tenancy audit matrix](../modules/tenancy/audit.md).
  The Packet 7 isolation fixture writes raw `tz` and `theme` rows for both seed tenants.
  [Phase 03](phase-03-identity-admin.md) records that no command writing
  `tenant_settings` lands before the `[PiiSensitive]` decision on `TenantSetting.Value`.
  **G16**, **G17**, **G23**.
- **Tenant locales.** `tenant_locales` shipped in
  [Packet 6](phase-02a-kernel-tenancy.md#delivery-record-packet-6); neither seed tenant
  holds a row, and no command writes one. **G11**, **G13**.
- **The customization write path.** Both tenants hold the built-in `card` content type
  and `plain` taxonomy, owned per tenant, and every Customization command bumps the
  tenant's generation
  ([Packet 8](phase-02a-kernel-tenancy.md#delivery-record-packet-8)).
  `Customization.Application.Contracts` exposes no read; the module spec's § Primary
  read flow says "Not implemented". **G12**, **G22**.
- **The seed.** [Packet 7](phase-02a-kernel-tenancy.md#delivery-record-packet-7) seeds
  two tenants through the request path, maps the English school's host tenant-wide and
  the yoga studio's host to its default organization, and writes both host rows on names
  under `*.learnstack.local` that a browser reaches only after a hosts-file edit. Every
  seeder step after provisioning announces the tenant's default organization. **G7**,
  **G14**, **G32**.
- **The frontend scaffolds.** `frontend/apps/web/src/middleware.ts` answers `503`
  whenever `NODE_ENV` is `production` (so under `next start`), writes the raw host into
  `x-tenant-id`, copies every inbound header, matches `/api/healthz` too, and carries
  TODOs that assign the work to Phase 02a.
  `frontend/packages/sdk/src/{server,client}.ts` take a `tenantId` option and return
  `{}`, which stops compiling once the generated `paths` is non-empty, and the package
  root re-exports the server entry. `frontend/apps/web/.env.local.example` points the
  API origin at APISIX, which only `make dev-gated` starts. The root layout fixes
  `lang="en"` and a platform `<title>`. The Vitest harness, Packet 3b's placeholder page
  test and Packet 10's `src/test/lint-rules.test.ts` exist;
  `lib/customization/composites.ts` registers four composite keys and no component.
  **G31**, **G35**, **G36**, **G38**, **G43**.
- **CI and the development loop.** The `openapi diff` and `lighthouse budget` jobs are
  placeholders behind unset `vars.ENABLE_*` variables and report as skipped; activation
  takes the edits
  [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main)
  lists. The SDK pipeline is wired with empty output, and
  [Frontend Architecture Standards § SDK](../standards/07-frontend-architecture.md#sdk)
  gives its drift gate to this phase. The contract suite asserts only a `200`
  ([Testing Standards § API Contract Tests](../standards/06-testing.md#api-contract-tests)).
  The `backend integration (Testcontainers)` job runs on every pull request and is
  required as of P02d-1. The inherited required-check gaps, including the renamed `meta`
  check, are [repaired and verified in Step 3](#step-3--packet-completion-2026-09-14).
  `make dev` starts containers only, `dotnet run` reads no `.env`, and
  `make seed` waits on every default-profile service and both Keycloak realms
  ([Infrastructure Standards § Healthchecks and the readiness gate](../standards/12-infrastructure.md#healthchecks-and-the-readiness-gate)).
  No job starts the API or `next start` as a process a browser can reach. **G31**,
  **G44**, **G45**.
- **Standards that meet their first subject here.** A new module owes its spec and
  audit-coverage matrix
  ([Documentation Standards § Per-Module Specifications](../standards/13-documentation.md#per-module-specifications),
  [Audit Coverage Standards](../standards/18-audit-coverage.md)).
  [Accessibility Standards](../standards/16-accessibility.md) binds the first public
  surfaces, and the [standards index](../standards/README.md#honest-status-today) row
  for Localization Standards places the i18n runtime in Phase 04. **G39**, **G43**.
- **What Phase 02b expects of this phase.** The development-transport part of its gate
  **G12**
  ([Phase 02b § The decision register](phase-02b-events-auth.md#the-decision-register)),
  now **G32**; the shared-peer premise of its **G14**, now **G34**; and its criterion
  that both sites still render anonymously with Keycloak stopped.

P02d-1 step 2 implements Education's roots, translations, migration and isolation
proofs. What remains unimplemented: every Education command and endpoint;
every renderer component; the server SDK transport; any trusted-hop configuration;
any Lighthouse tooling; and a `make demo` target.

### Explicitly not in this phase

Named so that no reader has to guess, and so no later phase can assume it was done here:

| Capability | Owning phase |
|---|---|
| Authentication, sessions, login | [Phase 02b](phase-02b-events-auth.md) |
| Token-keyed authenticated and write rate-limit budgets, and what the pre-authentication limiter does once a caller is validated | [Phase 02b](phase-02b-events-auth.md) (G14) |
| Keycloak realm reconciliation (a seed step that writes to Keycloak) | [Phase 02b](phase-02b-events-auth.md) (G12) |
| The `learnstack.tenancy.settings` eager invalidation event | [Phase 02b](phase-02b-events-auth.md), per [the Tenancy spec](../modules/tenancy/README.md) |
| Isolation tests through authenticated or id-addressed routes | [Phase 02b](phase-02b-events-auth.md) |
| Identity domain, roles, permissions, invitations; permission keys for public reads; the rule that every endpoint carries `[Authorize]` or `[AllowAnonymous]` | [Phase 03](phase-03-identity-admin.md) |
| A tenant-scope read across all organizations on a tenant host (the `app.scope` carrier) | [Phase 03](phase-03-identity-admin.md) |
| CMS editing, page builder, media library and tenant media origins | [Phase 04](phase-04-cms-media-pages.md) |
| Per-locale publish readiness workflow, `tenant_route_slugs` registry | [Phase 04](phase-04-cms-media-pages.md) |
| Studio authoring of translated field values (titles, bodies, slugs) and the view of untranslated gaps — where it lives is an open question | [Phase 04](phase-04-cms-media-pages.md#admin-studio-cms-screens); its screen row goes in [Phase 06 § Admin Studio — screen ownership](phase-06-renderer-admin-studio.md#admin-studio--screen-ownership) |
| `Accept-Language` negotiation for API-returned messages; the per-tenant locale fallback chain; the `/{locale}/{slug}` page routing shape | [Phase 04](phase-04-cms-media-pages.md) |
| Authoring and translating a content type's field order and labels; the closed set of built-in primitive field types (number, boolean, date/time, select) | [Phase 04](phase-04-cms-media-pages.md) |
| The schema-version migration path for stored instances; deleting a content-type revision after a zero-instance count | [Phase 04](phase-04-cms-media-pages.md) |
| Customization deprecate and revise commands, and per-band taxonomy editing | [Phase 04](phase-04-cms-media-pages.md) and [Phase 05](phase-05-education-learning-content.md), consolidated in [Phase 06](phase-06-renderer-admin-studio.md) |
| Tenant-authored page composition (`TenantPageBlock` rows and their cache family) | [Phase 04](phase-04-cms-media-pages.md); the block resolver is [Phase 06](phase-06-renderer-admin-studio.md)'s |
| Deciding whether a customization change owes an integration event | [Phase 04](phase-04-cms-media-pages.md), per [the Customization spec § Integration-event catalogue](../modules/customization/README.md#integration-event-catalogue) |
| The `Redirect` model the CMS auto-creates when a published slug changes | [Phase 04](phase-04-cms-media-pages.md) |
| Search — the `ITenantSearch` port and its PostgreSQL default | [Phase 04](phase-04-cms-media-pages.md) |
| Search — the Meilisearch adapter behind that port | [Phase 09](phase-09-billing-integrations-analytics.md) |
| Course versioning, programs, lesson items, completion rules; richer publish-readiness validation and versioned publication; catalog visibility separate from publication | [Phase 05](phase-05-education-learning-content.md) |
| The migration that brings this phase's courses and lessons under course versions and modules | [Phase 05](phase-05-education-learning-content.md) |
| The authenticated course and lesson authoring surface, with the commands that edit a course or lesson or reorder lessons after the seed | [Phase 05](phase-05-education-learning-content.md) |
| The `Level` aggregate and the catalog API's levels resource; the `TenantLevelTaxonomy` editor | [Phase 05](phase-05-education-learning-content.md) |
| The `TenantLessonItemType` read, the batched reference walk and the measured customization cost model; the `embed-html` sanitisation contract | [Phase 05](phase-05-education-learning-content.md) |
| Admin Studio, navigation menus, SEO metadata beyond what G40 settles for translated public pages, full block registry | [Phase 06](phase-06-renderer-admin-studio.md) |
| The full `UnknownVersionBlock` / `UnknownBlock` placeholders and per-block error boundary; tenant-authored error pages; redirect handling | [Phase 06](phase-06-renderer-admin-studio.md) |
| Branding configuration surface — tenant-admin editor, theme preview, logo, header and footer settings; the per-organization branding override (`OrganizationBranding`) and the token merge | [Phase 06](phase-06-renderer-admin-studio.md) |
| Preview of draft courses and lessons through the production renderer | [Phase 06](phase-06-renderer-admin-studio.md) |
| Browser end-to-end flows (Playwright) and axe checks through Playwright | [Phase 06](phase-06-renderer-admin-studio.md), per [Testing Standards § End-to-End Tests](../standards/06-testing.md#end-to-end-tests); any narrow smoke this phase adopts is G38 |
| Enrollment, learner portal, progress tracking; [Course Access](../glossary.md) — whether content published anonymously here stays on the public path once enrollment exists | [Phase 07](phase-07-enrollment-learner-portal.md) |
| Live classroom | [Phase 08c](phase-08c-classroom.md) |
| Billing | [Phase 09](phase-09-billing-integrations-analytics.md) |
| Hub, entitlement gating | [Phase 02c](phase-02c-hub-foundation.md) |
| APISIX as the edge gateway for a non-development deployment; gateway or CDN caching; edge rate limiting | [Phase 11](phase-11-production-hardening.md#security), on [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger |
| Plan-differentiated API rate limits (`LimitKeys.ApiRatePerMinute`) | [Phase 11](phase-11-production-hardening.md#security) for the key's gate; [Phase 02c](phase-02c-hub-foundation.md) for its enforcement path |
| The L2 cache tier behind `ICacheService` (default `InMemoryCacheService`) | [Phase 11](phase-11-production-hardening.md), on [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger |
| Content-Security-Policy and the other secure headers | [Phase 11 § Security](phase-11-production-hardening.md#security), per the [standards index](../standards/README.md#honest-status-today) row for Security Standards |
| `GET /readyz`; the production Dockerfile and image pipeline; the performance baseline and load tests against [Performance Standards](../standards/15-performance.md) | [Phase 11](phase-11-production-hardening.md) |
| Custom-domain TLS automation | [Phase 11](phase-11-production-hardening.md), on [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger |

Decisions made or referenced in this phase:

- [ADR-0003 Tenant Isolation Defense in Depth](../decisions/0003-tenant-isolation-defense-in-depth.md)
  (Amendment 3's template, Amendment 5's organization write guards and the accepted
  Amendment 6 INSERT and parent-mirror correction every Education table takes)
- [ADR-0048 Publication Before Course Versioning](../decisions/0048-walking-skeleton-publication.md)
  (independent course and lesson state, combined anonymous-read eligibility, and
  Phase 05 preservation obligations)
- [ADR-0008 Localization Schema](../decisions/0008-localization-schema.md) (satellite
  translation tables; slugs on the translation row, unique within `(tenant_id, locale)`)
- [ADR-0010 Cross-Module Communication](../decisions/0010-cross-module-communication.md)
  (Education reads Customization and Tenancy only through their application contracts)
- [ADR-0013 Page Block Schema Versioning](../decisions/0013-page-block-schema-versioning.md)
  (the pin rule
  [Tenant Customization Model § 4](../architecture/32-tenant-customization-model.md)
  extends to content types, and the placeholder for an unknown version)
- [ADR-0017 Tenant / Organization Hierarchy](../decisions/0017-tenant-organization-hierarchy.md)
  (organization scope; `OrganizationBranding` as Phase 06's override)
- [ADR-0018 Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md)
  (genericity, the renderer architecture, and its 2026-09-04 key and locale amendment)
- [ADR-0023 Strongly-Typed Id Source Generator](../decisions/0023-strongly-typed-id-source-generator.md)
  (the new aggregates' identifiers)
- [ADR-0024 API Versioning Policy](../decisions/0024-api-versioning-policy.md) (what the
  breaking-change check freezes)
- [ADR-0032 Exception Handling, Logging, and Observability Architecture](../decisions/0032-exception-handling-logging-and-observability.md)
  (the pipeline, `Result<T>`, Problem Details, the controller shape)
- [ADR-0033 Audit Durability Model](../decisions/0033-audit-durability-model.md) and
  [ADR-0044 Audit Write Path](../decisions/0044-audit-write-path.md) (classification of
  the anonymous reads and of the MUST-class writes)
- [ADR-0034 Hub Contract Surface Invariant](../decisions/0034-hub-contract-surface-invariant.md)
  (host resolution never calls the Hub)
- [ADR-0035 Demand-Gated Infrastructure](../decisions/0035-demand-gated-infrastructure.md)
  (this scope fires no trigger)
- [ADR-0036 Tenant Resolution and Trusted Inputs](../decisions/0036-tenant-resolution-trusted-inputs.md)
  (host resolution, the trusted hop and the pre-classification limiter; G33 and G34 may
  owe it an amendment)
- [ADR-0039 The Optimistic Concurrency Token](../decisions/0039-optimistic-concurrency-token.md)
  (`row_version` and the one ETag derivation)
- [ADR-0040 Ambient Unit of Work](../decisions/0040-ambient-unit-of-work.md) (the closed
  setter set G15, G22 and G23 test against, and the read-only mode G28 may add)
- [ADR-0041 Correcting False Statements in Accepted ADRs](../decisions/0041-correcting-false-statements-in-accepted-adrs.md)
  (how an erratum a decision pass finds is written)
- [ADR-0042 Tenant Provisioning Cross-Aggregate Transaction](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)
  (each locale and setting row by its own command; one aggregate root per command)
- [ADR-0043 Customization Payload Validation](../decisions/0043-customization-payload-validation.md)
  (lesson-body validation on the command path; no compiled-validator cache)
- [ADR-0045 Entitlement and Feature-Flag Socket](../decisions/0045-entitlement-and-feature-flag-socket.md)
  (`tenancy.white_label_branding`, whose meaning G16 asks)
- The reserved, undrafted **ADR-0027** — the frontend i18n library, target Phase 04 in
  [decisions/README § Open ADR Drafts](../decisions/README.md#open-adr-drafts); G39 asks
  whether this phase's renderer Accepts it

## Scope

### Packets and decision gates

Phase 02a needed twelve packets for a narrower scope. This phase declares eight, in
dependency order. `P02d-0` writes no code. Every packet after it opens with the decision
pass [Roadmap § Decision Timing](README.md#decision-timing) describes: its gates are
Accepted, and its catalogue rows registered, before its first line of code.

| Packet | Contents | Cannot start until |
|---|---|---|
| **P02d-0** | Phase entry: this plan — the inherited baseline, the packet table, the decision register, criteria that name their evidence, and the corrections to the documents that contradicted the phase. It answers no gate. The required-check edits identified at kickoff belong to `P02d-1`; its [delivery record](#step-3--packet-completion-2026-09-14) records their repair | Phase 02a exits (met) |
| **P02d-1** | Education schema and database-level isolation: the Education migration chain (`courses`, `lessons`, both translation satellites) with its policies, grants, foreign-key indexes and immutability triggers; the insert-time organization control; aggregates and EF configuration; the structural guards; the schema-level Education isolation suite as `learnstack_app`; the Education module spec skeleton and its pinned-list entry | G1, G2, G3 (values), G4, G5 (column), G6 (a), G7, G8, G9, G10 (order), G26 (slug grammar) Accepted |
| **P02d-2** | Writers and seed: the Education write commands and the Tenancy locale and setting commands, with their catalogue sources, matrices and composition-root registration; the Customization contract the lesson writer calls; the seeder's acts, contexts, ordering and re-run behaviour; each tenant's own content type, taxonomy, locales, branding, courses and lessons; `SeederTests` and the Packet 7 suite recomputed | G3 (commands and seeded states), G5 (validation), G7 (child derivation), G11, G12 (contract), G13, G14, G15, G16 (a–e), G17, G18, G19, G20 (literal source), G21 (subresources), G23 (bound) Accepted; P02d-1 |
| **P02d-3** | Read internals, no HTTP: the generation-keyed customization projection and its cache families; the typed settings accessor | G12 (cache key), G22, G23 (accessor), G24 Accepted; P02d-2 |
| **P02d-4** | Public read API and contract checks: the `[PublicSurface]` reads, their table rows and classification; response contracts, read eligibility, the cursor and the locale matrix; the cache directive; the committed OpenAPI snapshot with the breaking-change check, the regenerated SDK types and the drift gate, activated and required; the request-level Education isolation suite with positive controls | G5 (unresolved band), G6 (b), G10 (codec), G12 (response), G16 (f, g), G24 (response), G25, G26, G27, G28, G29, G30, G31 Accepted; P02d-3 |
| **P02d-5** | Server-rendering path: the development hostnames and transport; the development trusted-hop configuration; the server SDK transport; the middleware replacement and entry behaviour; the anonymous limiter over the hop; the rendering mode; the hop runtime evidence; the frontend skip refusal and the hop predicates' tests | G6 (c), G20 (exemptions), G21 (cookies), G30 (headers), G32, G33, G34, G35, G36, G37, G38 (a, d), G44 (whether) Accepted; P02d-4 |
| **P02d-6** | Public renderer: the `(public)` route tree, pages and page states; the UI message layer; lesson field rendering and its fallbacks; theming injection; the accessibility minimum; the renderer's tests | G5 (unresolved band), G12 (page state), G16 (g), G20 (subjects), G38 (b, c), G39, G40, G41, G42, G43 Accepted; P02d-5 |
| **P02d-7** | Demo, full-stack CI and exit: `make demo` and its stop behaviour; the full-stack Lighthouse job if G44 activates it here; the Keycloak-stopped check; the standards-index transitions; the outbound carrier re-verification; the delivery record naming what `P02b-0` re-verifies; the exit checks | G20 (Implemented), G33 (CI), G38 (c's job), G44, G45 Accepted; P02d-6 |

`P02d-2` precedes `P02d-3` because the projection reads the definitions the seed
publishes, and because the lesson writer — not the renderer — is the first caller of the
Customization contract. `P02d-4` regenerates the SDK, so the SDK surface that must
compile against non-empty `paths` is decided there. The rendering mode and the entry
behaviour close in `P02d-5`, not `P02d-6`, because the middleware replacement and the
SDK transport are written there and change under a different answer. G32 closes in
`P02d-5` unless an earlier packet writes a seed-host literal outside `SeedData`.

#### The decision register

Each row is a question this phase must answer before the packet it blocks starts. The
**Leaning** column preserves the reviewers' original proposal and is **not** a
decision; where the reviews disagreed, it states each position. The **Decision status**
column links the accepted answer for each closed part. The **Vehicle** column
is what this repository's own rules require for the answer to count. A gate whose
vehicle is an amendment is Accepted **before** the code it governs is written, not
alongside it. It closes in the decision pass of the first packet whose code it shapes,
together with any coupled gate that blocks the same packet; where its parts shape
different packets, the **Blocks** cell names the part each packet waits on. Every
premise a row cites is re-verified at that pass rather than trusted.

| # | Question | Leaning | Vehicle | Blocks | Decision status |
|---|---|---|---|---|---|
| G1 | How does a row whose vehicle is a phase-doc statement, a standard or a catalogue row show that it is Accepted, so that the exit's "no row open" can be checked — and is the answer this phase's or roadmap-wide? | The row stays and gains a closed date and a link to the statement, and each packet's Status row and delivery record list the rows it closed. Roadmap-wide if Phase 02b's phase-doc rows should close the same way | A sentence in [Roadmap § Decision Timing](README.md#decision-timing) if roadmap-wide, or in this register's framing paragraph if local. No ADR | P02d-1 (the first pass to close such a row; it shapes no code) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G1 |
| G2 | Which aggregate does `Lesson` belong to, and what is its parent: an entity inside `Course`, its own root referencing `Course`, or a minimal `CourseVersion` and default `Module` now? With it: how the satellites are mapped (base type, markers, `deleted_at`), the `sort` invariant and tie-breaker, and what the shape obliges Phase 05 to preserve — course and lesson ids, published slugs, order, organization scope, the inline body | The reviews split. One brings the version spine forward so Phase 05 enriches rather than re-parents; two keep this phase thin and record the preservation obligations, with Phase 05 designing the move. Between the thin shapes: inside `Course` means `ON DELETE CASCADE` and one audit row, but a lesson edit mutates `Course` structurally, which [Domain Model § Education Catalog](../architecture/02-domain-model.md#education-catalog) says a published course never is; its own root means `RESTRICT`, its own `row_version` and its own audit subject | Contract: a dated phase-doc statement; no Accepted ADR holds the `Course` / `CourseVersion` hierarchy, so a new ADR only if the answer needs a cross-root write ([ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)). Detail: the Education spec's data model; an interim note in [Domain Model § Learning Content](../architecture/02-domain-model.md#learning-content) where the answer departs from it; the class count in [Database Standards § Foreign keys between tenant-owned tables](../standards/05-database.md#foreign-keys-between-tenant-owned-tables) if `Lesson` cascades | P02d-1 (the lessons foreign-key target, `ON DELETE`, `row_version`, the root mapping and satellite `deleted_at`) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G2 |
| G3 | Which of `courses` and `lessons` carry a publication state, with which values and transitions, and what does "published" mean to an anonymous reader — publicly readable, or only listed? Which command sets it, which states does the seed write, and may a course with no lessons, or untranslated in an enabled locale, be published? And for any transition or deletion this phase does not ship (unpublishing a course or lesson, deleting either), which phase owns it? | `courses` `draft` / `published`, meaning publicly readable (Phase 05 adds catalog visibility as its own concept); lessons carry a state and show only when both are published; draft → published only; an empty course may be published, since publish validation is Phase 05's. One review leaned "listed in the catalog" | Contract: a new ADR, or a dated phase-doc statement recording why a two-value, one-transition column is not the state machine Decision Timing reserves for a decision record; no Accepted ADR decides publication ([ADR-0018](../decisions/0018-tenant-driven-customization-model.md) reserves the lifecycle to LearnStack). Detail: the `CHECK` ([Database Standards § Constraints](../standards/05-database.md#constraints)), the Education spec's state diagram, the publish row [Audit Coverage Standards](../standards/18-audit-coverage.md) makes MUST | P02d-1 (column presence and value set), P02d-2 (publishing commands and seeded states; transition contract closed in P02d-1) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): values and publication/transition contract; P02d-2 commands and seeded states remain open |
| G4 | Where does a lesson body's binding to the content-type key and `schema_version` it was validated against live — on `lessons` or on each translation row — and where does the body live: its column, type, per-locale placement, and how non-translatable field values are carried? May a constraint cross into the Customization chain? What becomes of Localization Standards' `isLocalized` marker, which nothing implements? | A value pin `(content_type_key, schema_version)` on `lessons`, as Phase 04 plans for `ContentEntry`, with no foreign key; the field document per locale in `lesson_translations`, every locale validated against the one pin, duplicated non-translatable values accepted until Phase 05's lesson items retire them; the marker removed or given its introducing phase | Detail: this document's § Localization schema, the Education spec, [Localization Standards § Pattern A](../standards/08-localization.md#pattern-a--side-translation-table-default-for-content-shaped-entities) in the same diff. Contract: a dated ADR-0043 amendment if a localization keyword enters the schema profile; its own ADR or amendment if a cross-chain foreign key is chosen, as ADR-0044 § 9 did, with Phase 04 and [Database Standards § Migrations](../standards/05-database.md#migrations) in the same diff | P02d-1 (the first `lessons` and `lesson_translations` DDL; a pin added later needs a backfill that guesses between two Active content types) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G4 |
| G5 | Before Phase 05's `Level` exists, how does a course or lesson carry the level band criterion 1 shows? Does the reference pin a taxonomy revision, how is a band validated on write, and what renders when the resolved revision no longer declares the stored band? | The reviews split: (a) a nullable, non-translatable `(taxonomy_key, band_key)` on `courses`, resolved against the live revision, because the criterion names the catalog; (b) a revision-pinned triple; (c) no column, the band shown through a lesson-page `x-taxonomy` field, with the criterion reworded. No shipped path validates a band value under any of them | Detail: a phase-doc statement, the Education spec, and a Phase 05 inherited row if a reference ships. Contract: a dated ADR-0010 amendment or a new ADR if an Education table takes a foreign key into Customization | P02d-1 (whether and where a column exists), P02d-2 (validation, seeded references), P02d-4 and P02d-6 (the unresolved-band state) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): column; validation, seeded references and unresolved-band behavior remain open |
| G6 | Locale identity on the content path. (a) What spelling and column type do the satellites' `locale` columns store, and which rule replaces Localization Standards' "Lowercase", which the shipped `LocaleTag` does not follow? (b) Is the `locale` parameter canonicalized before lookup, the membership check and every cache or cursor key, or is a non-canonical spelling refused? (c) What does a non-canonical `/{locale}/` segment get? | (a) `LocaleTag`'s canonical case (`tr-TR`, `zh-Hans`) in `varchar(35)`, as `tenant_locales` stores it — [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)'s 2026-09-04 amendment already makes case variants one locale; (b) well-formedness, then canonicalization, then lookup; (c) a redirect to the canonical segment, decided with G36 | Detail: [Localization Standards § Locale Codes](../standards/08-localization.md#locale-codes) and the Database Standards satellite fence in the same diff. No ADR: ADR-0008 states no casing rule | P02d-1 (a: the first stored rows), P02d-4 (b: validators, cursor binding), P02d-5 (c, with G36) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): (a); (b) and (c) remain open |
| G7 | Organization write scope. (1) Does a lesson carry its course's organization scope? (2) What forces a satellite's — and a lesson's — mirrored `organization_id` to equal its parent's at insert: writer derivation alone, or that plus a database backstop, and which? (3) May an organization-scoped session `INSERT` a tenant-wide row through the `organization_id IS NULL` arm of `WITH CHECK`, which [ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md)'s Amendment 5 and Database Standards say it cannot and which it can at `HEAD`? | (1) Identical scope for a course, its lessons and every translation. (2) Writers derive the child's organization from the authorised parent; the reviews split on the backstop — a stored generated scope column with an organization-inclusive composite key, which structural sweeps can see, or a `BEFORE INSERT` trigger reading the parent under the caller's policies — and one review requires database enforcement. A nullable three-column key is already excluded, because `MATCH SIMPLE` skips the check. (3) Tighten, after the pass confirms no audit writer composes a null-organization row under an announced organization | Contract: one dated ADR-0003 amendment for (2) and (3), with an ADR-0041 erratum beside any sentence the pass finds false when it entered the record; the template replaced in place in [Database Standards](../standards/05-database.md) with its disclosure; forward migrations for `tenant_settings` and `audit_log` if (3) tightens. Detail: [Database Standards § Translation satellite tables](../standards/05-database.md#translation-satellite-tables); a catalogue row with a planted offender if a database mechanism is chosen | P02d-1 (policy SQL, the generated column or trigger, aggregate factories), P02d-2 (child derivation in the commands) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): database controls and factory derivation; P02d-2 command derivation remains open |
| G8 | Which structural guards does the Education chain register, so its tables cannot regress with the suite green: every foreign key between two tables carrying `tenant_id` includes it; every table carrying `organization_id` has the immutability trigger (and how `audit_log`'s append-only guard counts); the Pattern A rule, which would make [ADR-0008](../decisions/0008-localization-schema.md)'s "the migration linter rejects ad-hoc per-locale columns" true? And how does `fn_organization_id_immutable` — which reads `OLD.id` and is declared only in the Tenancy chain — serve satellites that have no `id`? | Three rows, each with a planted-offender companion; the function replaced by a Tenancy-chain migration that reports `OLD.organization_id` or reads the row key through `to_jsonb(OLD)`, which (as in the audit append-only guard's row comparison) never names a column the table may lack, with the cross-chain dependency recorded under Database Standards § Migrations | Detail: Standards 21 rows Registered and Implemented in the packet; the Database Standards immutability fence and § Migrations; `MigrationRollbackTests`. Contract, only if ADR-0008's sentence is left untrue: an ADR-0041 erratum if it was false when entered, otherwise a dated amendment | P02d-1 (a guard shipped with its first new subject is the only point its companion is written against real tables) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G8 |
| G9 | Education schema detail: the content slug's character shape, normalization, width and database backstop — including whether a GUID-shaped slug is refused, which G26's shared-slot path needs; whether an Education table holds a foreign key into `tenants`, `organizations` or `tenant_locales`; and each runtime role's privileges on the four tables | `UrlSlug`'s shape with its own width constant and a `ck_<satellite>_slug_format` backstop, since restrictive now is the reversible choice (ASCII-only slugs exclude native-script URLs, a product choice); no foreign key into Tenancy; `learnstack_app` `SELECT, INSERT` plus exactly what G11's commands need, `learnstack_platform` `SELECT` | Detail: Localization Standards § Pattern A for the shape; the Database Standards satellite fence and [§ GRANT matrix](../standards/05-database.md#grant-matrix); § Migrations only if a cross-chain key is chosen | P02d-1 (the creating migration writes the `CHECK` and the grants; the grants couple with G11) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G9 |
| G10 | What is the catalog's default order and tie-breaker, and what is the cursor it mints: its payload and version; what it binds (tenant, organization, locale, sort, filters, endpoint); its integrity (none, a MAC with a key version, or server-side state); its direction; what happens when a row changes between pages; which list parameters the endpoint binds; where it is decoded; whether the codec is this endpoint's or the kernel's; and which cursor classes answer `400`? | The reviews split between a keyless versioned payload with a binding fingerprint, decoded at binding so a garbage cursor opens no transaction, and an HMAC-authenticated cursor with key rotation. Both keep tenant and organization out of the cursor, and bind `CursorPaginationRequest` rather than `ListRequest`, whose `q` is Phase 04's search | Contract: a phase-doc statement if the codec is endpoint-local and keyless; a new ADR if it becomes a kernel rule later lists follow, or a MAC adds a secret and a rotation posture. Detail: [API Standards § Pagination](../standards/04-api-design.md#pagination), which drops "Nothing validates its *shape* yet"; Standards 21 rows | P02d-1 (the order part: an ordering column, publication timestamp or collation), P02d-4 (the codec part) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): order; P02d-4 codec remains open |
| G11 | The write surface the seed needs. Which Education commands write courses, lessons and their translations; is a translation written separately from create; is publishing its own command; which command reports a slug collision as `business_rule_violation` rather than a raw unique violation, and does Localization Standards' "from the publish command" still hold? What shape do the Tenancy commands raising `tenancy.locale.write` and `tenancy.setting.write` take? How are the non-baseline writes classified, and how does a re-run converge? | Create course, write course translation, add lesson, write lesson translation, publish course (MUST); one locale command over `Tenant.AddLocale` and `SetDefaultLocale`; a create-or-update setting command keyed on context scope and key; ordering taxonomy → content type → course → lessons; idempotent by conflict, with an ownership check per act and a second-run test. None has a route | Contract: a phase-doc statement plus the Education spec (README write sequence, `audit.md`, `permissions.md` as a forward declaration on [the Tenancy precedent](../modules/tenancy/permissions.md)). Detail: catalogue sources, the Tenancy `audit.md` and `permissions.md`, Localization Standards § Pattern A if the collision sentence changes. An ADR only if a handler must write two roots | P02d-2 (commands, handlers, catalogue sources, seeder acts) | Open |
| G12 | Through which `Customization.Application.Contracts` surface does an Education write obtain the schema a body is validated against — exact `(key, schema_version)` including Deprecated revisions, or a key that binds the Active one — and is it an interface or a MediatR query, classified how? Which revisions may a writer bind, and what refusal answers an absent, cross-tenant or ineligible one? On the read side: what the cache keys on, whether the lesson response carries the binding or resolved field descriptors, and what the API and the page show when a binding cannot be resolved | One exact-revision query, Deprecated included, never falling back to Active; only Active revisions bindable for new writes, since a Draft's body can still change; absent and cross-tenant refused indistinguishably as `validation_failed` naming the binding; resolved descriptors in the response; an unresolvable binding shows a bounded placeholder with a warning log, never a `500` and never another revision's fields ([ADR-0013](../decisions/0013-page-block-schema-versioning.md)'s placeholder rule) | Detail: the Customization spec's contract and § Primary read flow, the Education spec's invariants, a phase-doc statement. No ADR: ADR-0010 settles the mechanism. A dated ADR-0013 amendment only if the unresolvable outcome departs from the placeholder rule | P02d-2 (the contract and write eligibility: the lesson writer is its first caller), P02d-3 (the cache key), P02d-4 (descriptors, the unresolvable outcome), P02d-6 (the page state) | Open |
| G13 | May an Education translation be written for a locale absent from, or disabled in, `tenant_locales`, and how is membership checked across the module boundary? Does a read resolve under a disabled locale? What does a tenant with no locale rows serve — [Localization § Tenant Locale Configuration](../architecture/12-localization.md#tenant-locale-configuration) promises platform `en`, and nothing implements it? Does a platform registry bound the enabled set, as Localization Standards names one in a namespace that does not exist? What happens to translations when `RemoveLocale` runs? | A Tenancy application contract checks membership on write; a read resolves only an enabled locale, checked once per request; no cross-chain foreign key; no platform registry in this phase; a tenant with no locale rows serves nothing until it has one | Contract: a phase-doc statement over ADR-0010's application-contract mechanism. Detail: the Tenancy and Education specs; Localization architecture and Localization Standards § Locale Model reconciled in the same diff; Database Standards § Migrations only if a key is chosen | P02d-2 (the translation command's check and the locale command the seed uses; the read half is written to the same answer in P02d-4) | Open |
| G14 | Seed inventory. At what scope is each seeded row class written — courses, lessons, translations, branding settings — and from what seeder context, given that `SeedTenantContext` requires an organization? Where do the rows the criteria need live — a sibling-organization course, an organization-scoped course on the tenant host, a `(locale, slug)` held in both tenants, draft and wrong-course rows, more courses than one catalog page, a disabled locale holding translations — `make seed` or test-owned data? Which key the yoga taxonomy uses, which tenant is bilingual, what state do the built-in `card` / `plain` keep, which record holds it all, and how do the Packet 7 fixture's raw settings rows coexist with seeded ones? | English content tenant-wide; the yoga studio gets a tenant-wide, a Studio One and a Studio Two course; a seed context that announces no organization; branding tenant-wide; rows in the seed with `SeedData` as the record; built-ins stay Active and are never selected implicitly; expectations recomputed as enumerated sets. An English organization-scoped row is still needed for the tenant-host criterion, seeded or test-owned — the demo database's contents are the owner's preference | Detail: a phase-doc statement, the `SeedData` remarks, the `seed-tenant` skill, the writers delivery record. No ADR: [Security Standards § Forbidden](../standards/11-security.md#forbidden) already makes scope come from context | P02d-2 (seeder steps, the seed-context constructor, `SeedData`, `SeederTests`; moving placement later rewrites the seed and every request-level case) | Open |
| G15 | `SeedRunner` calls `IUnitOfWork.SetTenantContextAsync` on its own transaction, and neither [ADR-0040](../decisions/0040-ambient-unit-of-work.md)'s closed setter set nor [Security Standards § The out-of-band setters](../standards/11-security.md#the-out-of-band-setters) lists it. Is that method's caller set mechanically closed, and is the seeder's call reconciled by routing its ownership check through `ISender`, or by admitting the seeder? | Route the ownership check through `ISender`, and add a source scan that admits `TransactionBehavior` (and Phase 02b's transport) with a planted offender | Contract: a dated ADR-0040 amendment plus a setters-table row only if the seeder is admitted. Detail: a Standards 21 source-scan row with its companion | P02d-2 (the Education seed acts reach the ownership check's refusal arm today) | Open |
| G16 | The branding token contract. (a) Where does the settings key registry live, what does a descriptor carry, and does `tenancy.setting.write` refuse keys outside it? (b) Which branding keys exist — per-token keys or one theme document — and is a layout option among them? (c) What value does each accept, fonts and logos included, and what happens to a stored value that fails it? (d) Does a failed contrast check refuse the write or record a warning — [Accessibility Standards § Color and Contrast](../standards/16-accessibility.md#color-and-contrast) says a Studio warning? (e) What does an organization-scoped branding row do here — refused, ignored or applied? (f) Which tokens may leave an anonymous response? (g) Does `tenancy.white_label_branding` — which reads true under `NullEntitlementProvider`, whose projection grants every registered feature, falls back to its catalog default `false` from a projection that omits it, and which the Hub's Starter plan sets false — govern applying theme tokens or only removing LearnStack attribution? | (a) a registry beside `FeatureKeys` and `LimitKeys`, as `Tenant.SetFeatureFlag` already refuses unregistered keys; (b) per-token keys, at most one enumerated layout option or none; (c) `#rrggbb` colours, one font key from a closed self-hosted set, no remote logo; (d) refuse; (e) tenant-wide only, keeping Phase 06's override and ADR-0017's `OrganizationBranding` true; (f) a closed projection of publicly readable keys; (g) not gated — tokens are baseline presentation, and the key's meaning is agreed with the Hub. That token values are tenant settings is settled by [Frontend Architecture Standards § Tenant Branding](../standards/07-frontend-architecture.md#tenant-branding) | Contract: a phase-doc statement plus Frontend Architecture Standards § Tenant Branding; a new ADR if the registry becomes an admission rule for every `tenant_settings` key; a dated ADR-0017 amendment if (e) applies overrides; Accessibility Standards if (d) replaces the warning. Detail: the Tenancy spec and permission matrix, [Frontend Architecture § Theming](../architecture/14-frontend-architecture.md#theming), the `FeatureKeys` descriptor with a matching note in the Hub repository for (g) | P02d-2 (a–e: validation and the seeded keys, which Phase 06's editor later edits), P02d-4 (f, g: the anonymous projection the OpenAPI baseline freezes), P02d-6 (g: whether rendering consults the flag) | Open |
| G17 | Does `TenantSetting.Value` carry `[PiiSensitive]`? [Phase 03](phase-03-identity-admin.md) sequences the decision before the first command writing `tenant_settings`, and this phase ships that command | Not marked, provided `tenancy.setting.write` admits only G16's closed key set, so the answer cannot stretch to keys a tenant invents; modelling a sensitive part as its own property stays open to Phase 03 | Contract: a dated phase-doc statement, reflected in `TenantSetting.cs`, the Tenancy spec and `audit.md`. Whole-value redaction of `jsonb` is settled by [ADR-0044](../decisions/0044-audit-write-path.md) Amendment 4 § 1 | P02d-2 (the first MUST-class settings audit row is written by the seed, and rows cannot be redacted retroactively); closes with G16 (a) | Open |
| G18 | How is a tenant content type presented? `json_schema` is `jsonb`, which keeps no key order, and the schema profile collects only `x-renderer`, `x-taxonomy` and `x-language`. How are field order, a label per enabled locale and a composite's field roles carried; which registered composite draws a lesson for each seeded type; which primitives does this phase implement, and does `markdown` render; how do types with no primitive row (`integer`, `number`, `boolean`, enums) map; may a rendered type declare a field outside the subset; and is a presentation entry naming a missing property refused at save? | A LearnStack extension — `x-order` and `x-label`, or one ordered `x-fields` list — carrying Pattern B labels, resolved at write like `x-taxonomy`; one composite already in both registries; the reviews split on the subset — `text`, `list` and `link`, with `markdown` without raw HTML, or a placeholder until Phase 05's sanitiser; the seed uses only the subset | Contract: a dated ADR-0043 amendment for a keyword or a save-time refusal; a dated ADR-0018 amendment for a presentation column; a phase-doc statement for `title` plus `required`, which cannot carry two locales. Detail: [Tenant Customization Model § 2](../architecture/32-tenant-customization-model.md) and § 8.1, the Customization spec, the profile's extension and reference-graph skip lists, `composites.ts` | P02d-2 (the seed publishes both content types as `schema_version` 1 with their renderer keys and field kinds; a later answer needs successor revisions) | Open |
| G19 | URL and markup policy for tenant-authored values on an anonymous page: which schemes (`https` only, or `http` too), credentials and `target`, which media origins, whether the rule is enforced on write — in the Education command, or as a validation gate Phase 04's entries share — whether the public API filters too, and whether URLs inside markdown fall under it. The write-time check constrains structure, not schemes: `format: uri` admits `javascript:` and `data:` | The reviews split on `http`; all refuse `javascript:`, dangerous `data:` and credentials; checked on write by a LearnStack rule and again on render; no third-party media in the seed | Detail: one home for the scheme list — [Security Standards § XSS & Output Encoding](../standards/11-security.md#xss--output-encoding) or [Frontend Architecture Standards § Security](../standards/07-frontend-architecture.md#security), not both; the Education spec's write rules; Tenant Customization Model § 8.1 if checked on write. Contract: a dated ADR-0043 amendment if it becomes a shared validation gate | P02d-2 (the lesson command's validation and the seed values; the render-time check reuses the answer) | Open |
| G20 | What mechanically backs "no production code branches on which tenant it serves"? The shipped domain-term scan strips literals and exempts seed data. (a) The mechanism and its literal source; (b) its subjects, matching and the platform built-ins; (c) its exemptions, including development hosts in frontend or infrastructure configuration; (d) whether a ban on production references to `LearnStack.Tools.Seeder` and a behavioural same-code, different-data test accompany it | A Standards 21 sibling row scanning production backend and `frontend/` sources, comments stripped, for exact identity literals read from `SeedData` (slugs, ids, hosts, display names, customization keys), built-ins excluded, with planted offenders; plus the behavioural test. The exemption policy is the owner's judgement | Detail: a Standards 21 row Registered in the first pass that uses it and Implemented before exit; a phase-doc statement in § Genericity proof. No ADR | P02d-2 (a: every seed literal lives where the source reads it), P02d-5 (c: the first host outside `SeedData`), P02d-6 (b: frontend subjects), P02d-7 (Implemented and required) | Open |
| G21 | Does the anonymous public path set any cookie — the [Frontend Architecture Standards § Tenant Resolution](../standards/07-frontend-architecture.md#tenant-resolution) flowchart sets them — and may a public page load any cross-origin subresource, such as the CDN-hosted logo and font assets Frontend Architecture describes? | No cookies, since the locale is already in the path and a locale-less request redirects ([Localization Standards § URL Strategy](../standards/08-localization.md#url-strategy)); same-origin subresources only; both asserted by a check. Whether tenant branding may point visitors' browsers at third-party hosts is a data-protection choice for the owner | Detail: a phase-doc statement; the Standards 07 flowchart and Frontend Architecture § Theming reconciled in the deciding pass | P02d-2 (subresources, if G16 admits a URL-valued token), P02d-5 (cookies: the middleware replacement is the first code that could set one) | Open |
| G22 | How does the customization definition projection load and stay correct? In the request's ambient transaction, or as a ninth out-of-band tenant-context setter (ADR-0040's set is closed at eight)? In what order are the generation and the rows read; what does an absent generation row mean; how is a cache filled inside a transaction that bumped and rolled back kept unreachable, when the bump is an upsert increment that can reissue a number; what does an absent definition set return; which families are registered, and how does the adapter's exact-tuple `cache.name` mapping match generation-embedded names; what do the TTLs bound; and is the contract batched so a public read issues a bounded number of statements? | Load in the ambient transaction; read the generation first, then the rows; fill only from non-bumping transactions; treat cache faults as misses; restate the module's cache-hit budget; a batched contract, with statement-count assertions cold and warm | Contract: the Customization spec § Primary read flow and a [Tenant Customization Model § 8.2](../architecture/32-tenant-customization-model.md#82-cache-strategy) statement on how a request learns the generation; a dated ADR-0040 amendment and a setters row only if the loader is out-of-band. Detail: the [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md) cache table, the `cache.name` mapping, the Observability Standards metrics family list | P02d-3 | Open |
| G23 | The typed settings accessor and its freshness. With no `learnstack.tenancy.settings` event until Phase 02b and the seed writing from its own process, what bounds staleness: a TTL with a stated bound, a writer-coupled Tenancy settings generation counter, or no settings cache here? What are the accessor's name and glossary headword; how is a cached read keyed so tenant-wide and organization rows never cross organizations — a settings read depends on `app.organization_id` today, and the policy's tenant-scope read gains a carrier in Phase 03; and does its loader run in the ambient transaction? | The reviews split on freshness — a TTL bound until 02b, a counter, or no cache. For keys: tenant-wide rows loaded with an explicit `organization_id IS NULL` predicate under `CacheKey.ForTenant`, each organization's overrides under `CacheKey.ForOrganization`, merged in memory; an ambient loader. The documented tenant-only key is rejected, because it would serve one organization's overrides to another | Detail: if settings are cached, the Infrastructure Stack Standards cheat-sheet rows and `cache.name` mapping; the Tenancy spec's event row and budget; a glossary headword. Contract only for a counter (the Tenancy spec, Database Standards § Table classes and § GRANT matrix) or an out-of-band loader (an ADR-0040 amendment) | P02d-2 (a counter is bumped inside the setting command's transaction), P02d-3 (name, keys, loader) | Open |
| G24 | Display fallback. Which document owns the chain — [Localization § Fallback Rules](../architecture/12-localization.md#fallback-rules) or [Localization Standards § Locale Model](../standards/08-localization.md#locale-model), which state different chains, while the shipped `LocalizedText.Resolve` narrows one subtag at a time and ends at the first authored value? What is the terminal state of a nullable Pattern A field and of a Pattern B label? Does a response say which locale a fallback value resolved in, so the page can mark its language (WCAG 3.1.2)? | Localization architecture owns the chain and Localization Standards links it, both recording the shipped narrowing and the first-authored terminal for labels; a nullable Pattern A field renders absent; each fallback-capable field reports its resolved locale | Detail: Localization Standards § Locale Model linking its owner, reconciled with `LocalizedText` in the same diff; the Customization contract's signature; the response schema under G26. No ADR | P02d-3 (the first caller that passes a fallback chain), P02d-4 (response fields) | Open |
| G25 | Site data and the page set. How does the renderer get the per-host data none of the Education reads returns — enabled and default locales, branding tokens, taxonomy display values, content-type field lists: fields embedded in the course reads (which cannot supply a default locale before a locale is known), a separate `[PublicSurface]` read resolved from the effective host, or the edge host lookup [Frontend Architecture Standards § Tenant Resolution](../standards/07-frontend-architecture.md#tenant-resolution) and [Infrastructure Stack Standards § Host → Tenant Resolution](../standards/20-infrastructure-stack.md#host--tenant-resolution) prescribe today, which must then state the effective host over the hop? Does the frontend ever hold a tenant or organization id? And which `(public)` pages ship — catalog, course with ordered lesson links and lesson, or two pages with bounded lesson links in the catalog response? | One `[PublicSurface]` site-data read with no host parameter, returning a closed projection and no ids, and three pages, which gives the course-detail read a consumer; one review keeps two pages with an explicit catalog outline. The first two options change what two Active standards prescribe | Contract: a phase-doc statement in § Read API and § Public renderer; for the first two options, edits to the two standards named, with an ADR if the pass judges the change non-trivial (no ADR carries the edge-lookup rule). Detail: the API Standards § Public surface rows; the Frontend Architecture sketch, sequence diagram and cache rows; the Localization architecture's edge locale sentence; the glossary; Phase 06 § What Phase 02d already shipped; Phase 05's inherited row if the course-detail read changes | P02d-4 (the endpoint set and DTOs the OpenAPI baseline freezes; a two-page answer changes the catalog response) | Open |
| G26 | The v1 public read contract. The path shape beside Phase 05's authoring `/courses/{id}` — a shared slot, a distinct public prefix, or `/courses/by-slug/{slug}`; each response as an allow-list and what it never carries; the embedded lesson list's fields, order and bound, and whether an empty list is valid; per-locale alternates; how enums and envelopes stay additive; and which Problem Details responses each operation documents, given that no non-idempotent operation documents any today and a baseline of `200`s cannot see a status change | Fields limited to what the pages render; object envelopes, extensible enums, a deny-list contract test (`tenantId`, `organizationId`, `createdBy`, `updatedBy`, `deletedAt`, `rowVersion`, `slugKey`); the embedded list carries title, slug and order under a cap; `alternates` for enabled, translated locales; one shared transformer declaring each operation's statuses as `application/problem+json`. No review settled the path | Contract: a phase-doc statement recorded before the breaking-change check stores its baseline. Detail: the OpenAPI snapshot; [API Standards § URL Structure](../standards/04-api-design.md#url-structure) for a prefix class, § Pagination for an embedded list, § OpenAPI; the gateway's public-band row. [ADR-0024](../decisions/0024-api-versioning-policy.md) settles that later additions are non-breaking | P02d-1 (whether the slug grammar must refuse GUID shapes, with G9), P02d-4 (route templates, records, snapshot) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): slug grammar only; P02d-4 route and response contracts remain open |
| G27 | The cache posture of public reads. What directive do anonymous responses carry — the `200`s, the Problem Details `400`s and `404`s, the tenancy edge's unmapped-host `404` — what freshness do a newly published or unpublished course and a not-found have, and do anonymous reads emit an `ETag` and honour `If-None-Match`? [API Standards § Optimistic Concurrency](../standards/04-api-design.md#optimistic-concurrency) says mutable resources expose an `ETag`, and [ADR-0039](../decisions/0039-optimistic-concurrency-token.md) fixes one derivation, which a composite read cannot use without publishing `row_version` | An explicit `Cache-Control: no-store`, asserted by a test, and no `ETag` on anonymous reads — a response without explicit freshness may be cached heuristically by a shared cache. One review proposed no directive, stated | Contract: a phase-doc statement. Detail: API Standards — the directive, and a § Optimistic Concurrency sentence on anonymous read contracts, owed under either answer. A dated ADR-0039 amendment if a body-hash validator ships; [Performance Standards § Caching](../standards/15-performance.md#caching) if the answer caches | P02d-4 (the header-setting code and the headers the snapshot documents) | Open |
| G28 | Public-surface controls. (a) What audit class do `[PublicSurface]` requests register, and does a rule make `Off` the only permitted one? (b) `GET` only, or `GET` and `HEAD`, and what does the catalogue's permitted-methods leg compare a row against? (c) What mechanically stops a marked request from writing — a `READ ONLY` unit of work, a structural scan, or both? (d) What control beyond review keeps a controller dispatching only through `ISender` — a controller taking a module `DbContext` fails loudly, SQL on `IUnitOfWork.Connection` reads zero rows, and code that announces the tenant itself reads real rows? | (a) `Off` for every marked type — a SHOULD or MAY class would make every anonymous `GET` a best-effort write a caller controls — with a sibling rule and companion; (b) one review `GET` only, one the standard's `GET` / `HEAD`; (c) a `READ ONLY` transaction for marked requests, which three shipped setters already open before announcing, and which refuses any in-transaction MUST write, so it is checked against (a); (d) a type-reference rule over controller bodies with a planted offender | Detail: the API Standards § Public surface rows; Standards 21 rows and companions, including the two legs of `PublicSurface_Marker_Set_Is_Enumerated` not yet implemented; an [Error Handling Standards § Controller Mapping](../standards/09-error-handling.md#controller-mapping--resultt--iactionresult) sentence for (d). Contract: a dated ADR-0040 amendment if the unit of work gains a read-only mode | P02d-4 (the first marked query's registration, method attributes and handler; if P02d-3 writes on the read path, (c) closes there) | Open |
| G29 | Which rows do the anonymous reads serve, and what does every hidden row answer? The rule covers course state, lesson state (G3), the lesson's membership in the course its URL names — lesson slugs are unique per tenant, so a lesson resolves without its course segment unless the read checks — and soft deletion. Does every hidden cause (draft, deleted, wrong course, untranslated, other tenant, sibling organization, nonexistent) answer one `not_found` body with no per-cause detail, compared with `instance` and `correlationId` masked? | One eligibility rule used by every read; a lesson resolves only under its eligible parent, only when it belongs to it, only in the requested locale; lists show only eligible entries; deleted rows excluded now; one masked-equal body | Contract: a phase-doc statement whose single record is the Education spec. Detail: the failure constant. Settled and linked: a cross-tenant row is a `404` ([Security Standards § Error Messages](../standards/11-security.md#error-messages)), and an organization-scoped row is served only on its own organization's host ([Localization § Slugs and URLs](../architecture/12-localization.md#slugs-and-urls)) | P02d-4 (handlers, the failure constant, documented `404`s); the fixture rows close with G14 in P02d-2 if they live in the seed | Open |
| G30 | The locale error matrix and transport. On each read, what answers an empty, repeated, malformed, over-length, non-canonical, not-enabled or enabled-but-untranslated locale? Does a not-enabled locale answer the Active `unsupported_locale` `400` or the not-found body? Is `X-Locale`, which [Frontend Architecture Standards § Locale Resolution](../standards/07-frontend-architecture.md#locale-resolution) still names as the API carrier, withdrawn, so that locale reaches the API only as the query parameter? | Missing or malformed → `400` `validation_failed` naming `locale`; untranslated → an empty catalog page; the query parameter only. The reviews split on not-enabled — not-found, amending the Error Handling row, or the Active `400`; a uniform answer after the enabled check hides pre-launch rows under either | Detail: a phase-doc statement; the [Error Handling Standards](../standards/09-error-handling.md) table only if not-found; Standards 07 § Locale Resolution; the Frontend Architecture SDK sketch; the API Standards § Pagination example gains `locale` | P02d-4 (validators, OpenAPI parameters and responses), P02d-5 (the server SDK's header set) | Open |
| G31 | Contract checks and the SDK surface. The committed OpenAPI snapshot's path, and how the contract suite proves it equals the served document; how the base copy is read; how the first run behaves; which `oasdiff` version and fail level, and which ADR-0024 rows that level detects (a tightened validator or a changed status may be invisible to any diff); what is uploaded on failure. The drift gate's source — the committed snapshot through `LEARNSTACK_OPENAPI`, or a running API — and its job. Whether an activated deferred job keeps its `if: vars.ENABLE_*` condition, given that GitHub treats a skipped required job as passing, or loses it as the integration job's did. And what the SDK surface becomes when regeneration makes `paths` non-empty: a hand-written transport over `paths` or a typed client library, the fate of `createClientSdk`, and whether the package root keeps re-exporting the server entry | A committed snapshot the contract suite asserts equal, diffed against the base ref's copy with `oasdiff` pinned; drift generated from the snapshot in the required `frontend` job; the condition removed on activation; a thin hand-written transport, and no root re-export of the server entry | Detail: [Testing Standards § API Contract Tests](../standards/06-testing.md#api-contract-tests), the API Standards § OpenAPI links, `ci.yml`, [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main) (its activation procedure follows the answer), [Frontend Architecture Standards § SDK](../standards/07-frontend-architecture.md#sdk); a package pin with its licence verdict if a client library is chosen | P02d-4 (the first operation, its snapshot and assertion, the drift gate, and the regenerated types the factories must compile against) | Open |
| G32 | **The development-transport part of [Phase 02b](phase-02b-events-auth.md#the-decision-register)'s G12.** Which development hostnames and transport serve the two seed tenants — keep `*.learnstack.local`, with a hosts-file step and local TLS with a trust step, or move the seed hosts under `*.localhost`? What does a reviewer do between a clean checkout and both sites, and which carriers move, including host rows already on warm databases, where the host is the primary key and the seeder removes no mapping? | `*.localhost` over HTTP, provided the pass verifies in each browser the team uses and in CI's Chrome that both hosts resolve with no hosts entry and that a `Secure` cookie set on them is stored and returned; otherwise `*.learnstack.local` with local TLS and a named trust step. Tenants are never told apart by port: the effective host strips it | Detail: a dated phase-doc statement in [§ Host-based tenant resolution, end to end](#host-based-tenant-resolution-end-to-end), with `SeedData`, `scripts/seed.sh`, the README Quickstart, the `seed-tenant` and `local-dev-setup` skills and `apps/web`'s dev script and Next configuration in the same packet; Infrastructure Standards only if a TLS proxy publishes a port. No ADR | P02d-5 (the development transport, and the hop configuration a TLS proxy would change), or the first earlier packet that writes a seed-host literal outside `SeedData` | Open |
| G33 | The server-rendering topology and its evidence. Where do Next.js and the API run relative to each other — the workstation loopback, containers, gated APISIX; which networks are trusted; how does one hop secret reach both processes; what is the server-only API origin — and the same for the CI job that renders the pages? What evidence discharges ADR-0036's "Phase 02d's browser test" and its matrix rows "a direct socket bypassing the hop" and "the Phase 02d anonymous two-host browser render", given that Testing Standards gate this phase on a human? And ADR-0036 § Consequences says the root refuses to start outside Development when the secret list is empty or short, while the shipped rule, in every mode, refuses a half-configured hop, a network entry that is not CIDR and a blank secret or one under 32 characters (characters, not bytes), and admits both lists empty: is that recorded or restored, and is non-development hop configuration this phase's or Phase 11's? | Both processes on the loopback, networks `127.0.0.1/32` and `::1/128`, one generated secret from a single source and never under `NEXT_PUBLIC_`, with networks and secrets arriving together (committing networks without secrets breaks every Development-environment fixture); the API called directly; a per-run secret in CI. Evidence: request-level hop tests as `learnstack_app` — no `X-Tenant-Id`, a non-hop peer, a wrong secret, a repeated header — beside the human walkthrough, with one review adding an automated browser smoke. Record the shipped startup rule; non-development hop configuration is Phase 11's | Contract: one dated [ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md) amendment if the evidence departs from the ADR's words, recording the startup rule, and carrying G34 if G34 changes the key or budget. Detail: `.env.example`, `apps/web/.env.local.example`, `appsettings.Development.json` or the demo recipe; a Standards 21 row for the hop runtime test; a Testing Standards § End-to-End Tests sentence only if ownership moves; a Phase 11 scope row | P02d-5 (the hop configuration and the fixture shape — in-process test hosts have no socket peer), P02d-7 (the CI part) | Open |
| G34 | How does the pre-classification anonymous limiter treat a request arriving over the authenticated trusted hop? Every server-rendered call reaches the API from the renderer's peer, so every visitor of both tenants shares one partition, and one client sending random `Host` values through the renderer can starve both sites. The partition key, the budget and how the renderer derives any visitor identity it states — while unknown-host floods stay bounded before database work, and a direct peer is still limited per peer | The reviews differ: a visitor address stated over the hop, in a dedicated single-valued header or through `X-Forwarded-For` with the peer captured first, as ADR-0036 anticipates; a separate hop budget with limiting in the renderer; or an explicitly sized shared quota. A per-host ceiling as the only backstop multiplies under a random-`Host` flood | Contract: a dated ADR-0036 amendment if the hop changes the key or the budget, stating the new input's trust rule and whether it may be logged or audited; otherwise a phase-doc statement. Detail: API Standards § Request and Response Limits, [Security Standards § Rate Limiting](../standards/11-security.md#rate-limiting), the catalogue's per-peer rule. `P02b-0` re-verifies Phase 02b's G14 against the answer | P02d-5 | Open |
| G35 | The server SDK transport. Its options — visitor host, locale, an optional assertion, never a tenant id as selector; where the host comes from; how the API origin is configured; the server-only guard, timeouts and cancellation; and whether it forwards W3C `traceparent`. With it: which parts of [Observability Standards § Frontend Observability](../standards/10-observability.md#frontend-observability) — Next.js error capture, web vitals — ship here, and which phase owns the rest, since no phase names them | Host and locale plus an optional assertion; hop headers and origin from server configuration, never from request input; a `server-only` guard; Problem Details mapped to the `AppError` union [Error Handling Standards](../standards/09-error-handling.md) already fixes; `traceparent` forwarded; Next.js error capture and web vitals assigned to Phase 11, whose § Observability lists error tracking | Detail: Frontend Architecture Standards § SDK and the Frontend Architecture SDK sketch; a pin and licence verdict if a guard package is added; an exclusion row naming frontend observability's owner, with an Observability Standards sentence | P02d-5 (the transport's headers and configuration read); the ownership half closes by exit, because a deferral names its phase | Open |
| G36 | The edge middleware and entry behaviour. Does the middleware resolve anything (with G25)? What does it carry inward, and under which header name — the Frontend Architecture sketch reuses `x-learnstack-host`, the hop header's own name? Which inbound internal headers are removed or overwritten, including `x-organization-id` when resolution has none — a deny-strip or an allowlist rebuild? What does the matcher exclude? What do `/` and a locale-less path answer, with which status, target and default-locale source; what do a disabled, malformed or non-canonical locale segment (G6 c), a platform host and an unknown host answer; does this run in middleware or the route tree, and may an i18n library own the middleware? Does this phase build the locale-less redirect [Localization Standards § URL Strategy](../standards/08-localization.md#url-strategy) requires, or keep the standards index's i18n-runtime carve-out while Phase 06 claims redirect handling? | Normalise the host; strip every client-supplied `x-tenant-id`, `x-organization-id`, `x-locale` and `x-learnstack-*`; drop the scaffold's `503` guard and TODOs; `/` redirects to the tenant's default locale; a disabled locale is a `404` before any content call; a platform or unknown host gets a `404` with no platform text; the redirect built minimally here, with Phase 06's rows reworded to "deepens" | Detail: a phase-doc statement; the Standards 07 § Tenant Resolution flowchart and the Frontend Architecture middleware sketch; Phase 06's rows in the same diff; if less is built, a dated narrowing of the standards index row for Localization Standards naming the owning phase | P02d-5 (the middleware replacement rewrites the scaffold's locale fallback, so every placement answer changes it first) | Open |
| G37 | How do tenant-varying `(public)` routes render, and which Next.js caches may hold tenant data — the full-route cache, the fetch data cache, `unstable_cache`, `generateStaticParams` — so one host's page is never served on the other? A public URL carries no tenant, and both tenants send the same request line over the hop, so a path-keyed cache leaks. What freshness does a page have after a customization or content write? | All three reviews: dynamic rendering with uncached SDK fetches — no `revalidate`, no `generateStaticParams`, no `unstable_cache` — relying on the API's generation-keyed cache; no ISR here | Detail: [Frontend Architecture Standards § Public Site Renderer](../standards/07-frontend-architecture.md#public-site-renderer), which prescribes `revalidate` today, and [Frontend Architecture § Rendering Strategies](../architecture/14-frontend-architecture.md#rendering-strategies), rewritten with the `add-frontend-route` skill; [Performance Standards § Caching](../standards/15-performance.md#caching) if the answer caches; an ADR if the pass judges the Standards 07 change non-trivial | P02d-5 (one mechanism: a host-bearing rewrite target is middleware code and fetch cache options are transport code, both written before the renderer) | Open |
| G38 | The frontend test set. (a) Which predicates of the middleware and the server SDK does Vitest cover, and in which packages — `pnpm -r test` runs only packages with a test script, which `packages/sdk` lacks? (b) How are async Server Component pages and field components covered below the browser, given vendor guidance that Vitest does not render async Server Components? (c) What automated evidence, if any, re-proves the page-level two-host claim after exit — an HTTP smoke against `next start`, one narrow Playwright smoke pulled forward from Phase 06, or a dated manual record? (d) By what mechanism does the `frontend` job refuse skipped and todo cases, which it does not today, since `No_Architecture_Test_Is_Skippable`'s runner leg reads only the backend `.trx` files? | (a) host normalization and header stripping, locale parsing and entry answers, hop headers with no tenant selector, `not_found` mapping — each with an inversion companion; (b) async pages covered through their synchronous children; (c) the reviews split three ways; (d) a reporter-output check or a lint ban on disabled tests, failing also on a package with tests and no script, proven with a planted skip | Detail: a phase-doc statement; the test files; the Standards 21 entry `No_Architecture_Test_Is_Skippable` extended rather than a second name; `ci.yml`. Contract for (c) only if a browser smoke moves: a Testing Standards § End-to-End Tests edit citing this row, with its carriers | P02d-5 (a; d at the latest), P02d-6 (b, c), P02d-7 (c's job) | Open |
| G39 | Is [ADR-0027](../decisions/README.md#open-adr-drafts), the frontend i18n library, Accepted in this phase rather than Phase 04, or do this phase's pages meet a library-neutral message contract under the standards index's carve-out? Where does the one UI string catalogue live — the carriers name three paths? | The reviews differ: Accept at first use (`next-intl` composed with the tenant middleware, with its pin and licence verdict), or move only the minimum slice — catalogue loading, lookup, layout locale, `lang`. A third option, no platform-authored text, is hard for a skip link or a not-found page | Contract: ADR-0027 Accepted, with the decisions index row, Phase 04's ADR-0027 lines and the standards index row in the same diff — which, under [the decisions index SLA](../decisions/README.md#open-adr-drafts), makes it an exit blocker here — or a narrowed carve-out plus a phase-doc statement. Detail: [Localization Standards § Strings in Code](../standards/08-localization.md#strings-in-code), [Localization § UI String Catalogue](../architecture/12-localization.md#ui-string-catalogue), the `add-i18n-key` and `add-frontend-route` skills | P02d-6 (the first platform string, message loading and catalogue files) | Open |
| G40 | Route files, page states and site chrome. What happens to `(public)/page.tsx`'s platform placeholder, and to `/studio` and `/portal` on tenant hosts; is the `courses` segment fixed, and which phase owns localized section names? Does this phase ship `(public)` loading, error and not-found files — [Frontend Architecture Standards § Routing](../standards/07-frontend-architecture.md#routing) requires `loading.tsx` and `error.tsx` per route group, and a not-found page is this phase's own choice; how do SDK `not_found`, a cursor `validation_failed`, unavailable and `429` map to page states; what do an empty catalog and a course with no eligible lesson show; does the catalog render a next-page link? Does a minimal chrome ship? Are `hreflang`, canonical and `og:locale`, which [Localization Standards § SEO](../standards/08-localization.md#seo) requires on translated public pages, built here or carved out? | The placeholder leaves the public tree; a fixed `courses` segment with the section-name owner named; minimal state files in tenant tokens; `not_found` → an HTTP `404` page; unavailable or `429` → the route group's error page without disclosure; an explicit empty state; a next-page link over enough seeded courses; in-page links rather than chrome, keeping Phase 06's navigation rows true | Detail: a phase-doc statement; Phase 06's § What Phase 02d already shipped rows reworded; a dated carve-out in the standards index row for Frontend Architecture Standards or Localization Standards, naming the owning phase, for whatever this phase builds less of | P02d-6 | Open |
| G41 | How does the lesson page draw what the subset does not implement — an out-of-subset `x-renderer`, a missing optional field, an array of objects, a stored value whose type differs from its declaration, an `x-taxonomy` value and its missing band? And where do composite and primitive components live — Frontend Architecture Standards names `packages/blocks`, Frontend Architecture names `components/blocks/`, and the registry sits in `apps/web/src/lib/customization/`? | A safe placeholder, never an exception and never raw JSON; a missing optional field renders nothing; `integer`, `boolean` and enums as text; an `x-taxonomy` value as the band's display name in the requested locale. No review addresses the component home | Detail: Tenant Customization Model § 2 (the implemented subset, not a second list) and § 8.1; Frontend Architecture Standards § Public Site Renderer and the Frontend Architecture tree. A new primitive would be an ADR-0018 release, which this row must not assume, since Phase 04 owns the field-type set | P02d-6 | Open |
| G42 | How do validated branding tokens reach the server-rendered HTML — a `style` attribute on the root element, which needs `unsafe-inline` or `unsafe-hashes`; a nonce-compatible `<style>` element built from validated values; or a per-tenant stylesheet route — without constraining [Security Standards § HTTP Headers](../standards/11-security.md#http-headers)' nonce-based target before Phase 11 documents it per surface? | A `<style>` element built only from registry-validated values, emitting only the `--ls-*` vocabulary, never a `style` attribute; a nonce would force dynamic rendering (G37) | Detail: Frontend Architecture Standards § Tenant Branding for the mechanism; Frontend Architecture § Theming | P02d-6 (the layout's token injection; closes with G37's answer and G16's grammar) | Open |
| G43 | Which accessibility checks on this phase's pages fail a build — route tests asserting `lang`, one `<main>`, the heading outline, a skip link and a descriptive title; `jsx-a11y` at error severity, where most of the shipped config's rules warn; or jsdom axe, which Testing Standards puts through Playwright in Phase 06? Is catalog → lesson a critical flow that needs [Accessibility Standards § Testing](../standards/16-accessibility.md#testing)' screen-reader smoke test? | Route tests in the `frontend` job for the checkable semantics; keyboard, focus and 320 CSS px reflow in the manual record; failing `jsx-a11y` with a planted companion. No review addresses the screen-reader question | Detail: a phase-doc statement; [Accessibility Standards § Tooling](../standards/16-accessibility.md#tooling) if lint severity or component axe becomes a rule; the lint configuration. Whether failing lint enforces the standard couples with G44; its index row changes only in the enforcing pull request | P02d-6 (page components and their tests) | Open |
| G44 | The Lighthouse job. Does it activate in this phase, and on what full-stack harness — a migrated stack, a seed written as `learnstack_app`, the API as a process over the hop with a per-run secret, `next start` on a production build, both seed hosts reachable from CI's browser, a readiness and tenant-marker check before the audit — or does activation move to the phase that brings a browser harness? What does it assert: the URL set and which [Performance Standards](../standards/15-performance.md) row each page answers to (its LCP row names a landing page this phase does not ship); which rows, under which throttling preset; the 200 KB budget or the 250 KB forbidden line; hard or warning, run count and aggregation; categories, including the accessibility audit [Accessibility Standards § Tooling](../standards/16-accessibility.md#tooling) requires; the runtime ceiling, tool install and report destination; and what the standards index rows for Performance and Accessibility say afterwards? | Activate here, reusing the `make demo` entrypoint; catalog, course and lesson on both hosts plus the bilingual tenant's second locale; hard assertions on deterministic audits (script transfer size, CLS, the accessibility category or contrast over both palettes) and median-of-N timings as warnings, with TBT rather than a lab INP; tooling from the lockfile; reports to workflow artifacts only; Accessibility promoted if asserted hard, Performance kept Adopted as a pre-baseline check unless a hard budget makes it Active for what ships. The budget authority is settled: [Frontend Architecture Standards § Performance](../standards/07-frontend-architecture.md#performance) names Performance Standards | Contract: a phase-doc statement plus a committed Lighthouse configuration. Detail: `ci.yml`, CONTRIBUTING's activation edits, the `run-tests-locally` skill; Performance Standards only if a number or lab profile changes; the standards index rows and status headers in the enforcing pull request. Moving activation edits `ci.yml`, CONTRIBUTING and the skills, and leaves Phase 01's and 02a's records as written | P02d-5 (whether it activates, as an input to G32: the hosts must work in CI's browser), P02d-7 (the harness and assertions) | Open |
| G45 | What does `make demo` start and guarantee on a clean checkout? The process model — a foreground supervisor over detached compose, or detached processes with a stop target; the web app's mode; how environment reaches host processes, given `dotnet run` reads no `.env` and nothing creates `apps/web/.env.local`; readiness waits before Phase 11's `/readyz`; stale-`.env` detection; re-run and stop behaviour; whether its seed step keeps `make seed`'s wait on every default-profile service and both Keycloak realms; and what it prints, including the bilingual tenant's second-locale URLs | The reviews differ on attached versus detached processes; both reject a destructive reset, wait on both processes, keep re-runs idempotent and have CI reuse the entrypoint | Detail: the `Makefile` targets with help lines and a dated phase-doc statement; README § Quickstart, `scripts/seed.sh`'s closing output and the `local-dev-setup` and `seed-tenant` skills in the same packet; Infrastructure Standards § Healthchecks if the seed's gate narrows. No ADR | P02d-7 (with G44, whose job invokes the same entrypoint; if G44's harness moves earlier, this row moves with it) | Open |

Gates that define one mechanism are answered against each other. Where their parts shape
different packets' code, the mechanism is split as
[Roadmap § Decision Timing](README.md#decision-timing) splits a gate: parts that block
the same packet close together in its pass, and a part that blocks a later packet closes
with or after the earlier one, never contradicting it:

- G2 and G3 — the aggregate boundary decides where the state lives.
- G4 and G12 — the pin and the contract that resolves it.
- G7 — one ADR-0003 amendment — with G14's seed context in `P02d-2`.
- G8 and G9 — the Education chain's detail and the guards that hold it.
- G11, G14 and G15 — the writers pass.
- G16, G17 and G21 — the closed key set the PII answer rests on, and what a token may
  point at.
- G18 and G19 — what the seed may publish.
- G22, G23 and G24 — the read internals.
- G25 through G31 — one public contract and one OpenAPI baseline.
- G32 through G37 — one hop; G33 and G34 share any ADR-0036 amendment.
- G39 and G40 — routing and strings.
- G44 and G45 — one stack entrypoint.

### P02d-1 decision pass (2026-09-14)

**Accepted — 2026-09-14, verified against `6c58343`.** The maintainer approved
this decision package before implementation. The
[Education spec](../modules/education/README.md) owns the accepted schema detail and
matrices, with its pinned module-list registration. No production implementation is
claimed by this decision pass. The kickoff remains the historical entry record;
[P02d-1's delivery record](#delivery-record-p02d-1) records this pass separately.

#### P02d-1 accepted answers

| Gate or part | Accepted answer | Decision/detail owner |
|---|---|---|
| G1 | Roadmap-wide closure notation: preserve the original question, record `Accepted — YYYY-MM-DD` with an anchor to the answer and its accepted vehicle; identify the exact closed part of a split gate. Packet status and delivery record list the closed parts. A proposed document closes nothing | [Decision Timing](README.md#decision-timing) |
| G2 | Separate `Course` and `Lesson` roots; natural-key translation entities contained by their own root; Lesson → Course `RESTRICT`, translations → root `CASCADE`; no independent satellite soft deletion. Stable ids and URLs survive Phase 05's migration | [Education data model](../modules/education/README.md#data-model-and-invariants) |
| G3 — values and publication contract | Both roots have `draft` / `published`; publication permits anonymous reading, with both states required for a lesson. No version snapshot or cascade publication. The transition contract is decided with the column; command names and seed acts stay with P02d-2 | [ADR-0048](../decisions/0048-walking-skeleton-publication.md); P02d-2 still closes the remaining G3 details |
| G4 | One exact content-type revision pin on `lessons`; JSON object body per locale on `lesson_translations`; repeated non-translatable values remain per locale. No schema-profile keyword or cross-chain FK | [Education data model](../modules/education/README.md#data-model-and-invariants); Standards 08 removes the unimplemented `isLocalized` claim |
| G5 — column | Optional all-or-none, revision-pinned taxonomy/band triple on `courses`. A successor cannot relabel or remove the course's stored band by rebinding it | [Education data model](../modules/education/README.md#data-model-and-invariants); Phase 05 carries an explicit pin-preservation obligation |
| G6 (a) | The shipped `LocaleTag` spelling in `varchar(35)`, including script Title case and region uppercase | [Education locale identity](../modules/education/README.md#localization-and-url-identity); Standards 08 and Standards 05 satellite sketch |
| G7 — database control | Exact organization write scope, including INSERT; an invoker INSERT/UPDATE trigger checks each parent mirror in addition to factory derivation and tenant-composite FKs. No audit exception to the stricter INSERT rule | [ADR-0003 Amendment 6](../decisions/0003-tenant-isolation-defense-in-depth.md#amendment-6--insert-scope-and-parent-mirrors-2026-09-14); writer contract details remain in P02d-2 |
| G8 | Structural proofs for tenant-composite foreign keys, organization immutability, parent-scope mirroring and Pattern A storage, each with a planted offender; shared immutability errors must not read `OLD.id` | Register rows in Standards 21 before implementation; Standards 05 owns the SQL and chain dependency |
| G9; G26 — slug grammar | Content slugs: 1–160 lowercase ASCII characters with single interior hyphens; refuse UUID `N` and `D` shapes, and do not normalize invalid input silently. No Tenancy or Customization FK. Role grants are enumerated below | [Education URL identity](../modules/education/README.md#localization-and-url-identity); Standards 08 and Standards 05. G26 route templates remain open |
| G10 — order | Catalog `(created_at ASC, id ASC)`, oldest-created first; lessons `(sort ASC, id ASC)` with nonnegative sort and permitted ties | [Education data model](../modules/education/README.md#data-model-and-invariants); the cursor codec remains P02d-4's decision |

**Database privileges for P02d-1.** On both roots, `learnstack_app` gets
`SELECT, INSERT, UPDATE`; updates are needed for publication, root concurrency and
later translation writes. On both satellites it gets `SELECT, INSERT`; P02d-2's
writers add a locale once, and no replacement or editing command is introduced here.
`learnstack_platform` gets `SELECT` on all four tables. `learnstack_outbox_admin` and
`PUBLIC` get none. Migration ownership is unchanged. A later command needing more
privileges brings its own migration; grants do not anticipate that command. To prove
an UPDATE/DELETE policy independently of a missing privilege, a separate disposable
test database commits the owner's test-only grant, then asserts through a separately
authenticated `learnstack_app` connection. The database is discarded afterward. The
real grant matrix is tested against an untouched migrated fixture.

**Organization-immutability scope.** G8's shorthand "every table carrying
`organization_id`" needs a table-class qualification. The guard covers tables
classified organization-scoped by Standards 05. `platform_host_to_tenant` is a
platform-scoped host projection, and `outbox_messages` is tenant-wide with organization
metadata; neither becomes organization-scoped because it has that column.
`audit_log`'s append-only guard counts only if its behavior demonstrably refuses an
organization change. The catalogue must enumerate these distinctions and prove its
exemptions cannot hide a newly unguarded organization-scoped table.

#### Review findings and implementation obligations

The four accepted catalogue identifiers are
`Every_TenantOwned_Foreign_Key_Includes_TenantId`,
`Every_OrgScoped_Table_Has_An_Organization_Immutability_Guard`,
`Every_Organization_Mirroring_Child_Has_A_Parent_Scope_Guard` and
`Pattern_A_Content_Uses_Translation_Satellites`. They are Registered in Standards
21 by this decision pass and become Implemented with their proofs. The last rule
rejects ad-hoc per-locale columns as well as a misplaced translatable parent column.

Two independent pre-development reviews checked the model/localization and database
isolation surfaces. The following are confirmed against this pass's baseline:

- The `WITH CHECK` on `tenant_settings` and `audit_log` admits a null organization
  from an organization-scoped session. ADR-0003 Amendment 5 tightened UPDATE and DELETE
  only; the adjacent erratum and Amendment 6 correct its broader statement. Both
  existing tables need forward migrations; old migrations remain untouched.
- `fn_organization_id_immutable` refers to `OLD.id`, which neither satellite
  has. The shared function needs a Tenancy-chain replacement and a no-id regression
  case. Education must migrate after that dependency, including in rollback tests.
- A generated normalized scope key with a three-column FK worked in PostgreSQL but
  failed the natural EF graph mapping on the repository's pinned packages. The pass
  therefore chooses the invoker INSERT/UPDATE parent check, tested independently
  under the application role, rather than prescribing an unproven EF workaround.
- An unlocked parent lookup admitted a delete/reinsert race under test-only DELETE
  privileges: a parent could be replaced under the same id with another organization
  before the child FK ran. `FOR KEY SHARE` prevented the replacement; the child and
  parent retained matching scopes. Amendment 6 requires that lock.
- A BEFORE parent check can refuse a malformed child before RLS or the FK does. The
  Education suite needs a separate disposable fixture with only that parent guard
  removed by committed owner setup, followed by assertions through an authenticated
  `learnstack_app` connection, to prove each lower layer independently. The normal
  fixture proves all controls together; no production bypass is introduced.
- The level revision pin must be preserved by Phase 05's future `Level` projection;
  its current active-only wording cannot silently reinterpret a stored course.
- Plain satellites retain slug reservations after parent soft deletion. The spec
  states this explicitly; applying a soft-delete filter to a nonexistent column is
  not a valid uniqueness strategy.
- Standards 08 now records canonical locale casing and removes the unimplemented
  `isLocalized` marker. Schema `$ref` recursion and content-entry reference depth are different
  mechanisms: ADR-0043 refuses all schema cycles. The Phase 05 wording is corrected.
- The extension overview's stale phase table and runtime claims, and Phase 04's stale
  customization uniqueness premise, are corrected against the canonical owners.
- The decision-pass inspection found that live GitHub branch protection required
  `meta (commit hygiene + link audit)` while CI reported
  `meta (compose + commit hygiene + link audit)`, and did not require
  `backend integration (Testcontainers)`. The approved exact repair preserves the
  other checks and maintainer-approved settings; [Step 3](#step-3--packet-completion-2026-09-14)
  records its application and verification.

The relevant corpus includes the roadmap baseline and exit criteria; Domain Model,
module boundaries, tenant isolation, localization and customization architecture;
ADRs 0003, 0008, 0010, 0013, 0017, 0018, 0023, 0033, 0039–0044; the Tenancy,
Customization and Audit specifications; and Standards 01, 02, 05, 06, 08, 11, 13–15,
17–19 and 21. Accepted decision bodies are preserved, with the bounded ADR-0041
erratum beside Amendment 5's false statement. The approved records and their carrier
documents form the packet's first commit, before production implementation.

#### Approved implementation sequence

All work stays on `development`. No destructive migration, new package, Hub change or
cross-module Domain reference is planned.

0. **Decision commit.** Accept the records, close the exact gate parts above, register
   the structural rules, and reconcile the carrier documents and Education spec.
1. **Shared database controls.** Tighten the two existing INSERT policies by forward
   migrations, fix the shared immutability function, and add structural/regression
   proofs over existing tables, including audit, no-organization and pooled-GUC cases.
2. **Education schema and isolation.** Add both roots and their satellites, EF mapping,
   the Education migration chain, its role grants and parent-scope control. Add unit,
   schema-level isolation and Pattern A proofs with positive controls, and wire every
   fixture, migration runner and matrix-discovery list that must see the new chain.
3. **Packet completion.** Apply and re-read the exact GitHub required-check changes;
   run the complete relevant checks, verify fresh migration and reverse-chain behavior,
   update all counts/status carriers and the packet delivery record, then commit.

Each implementation step is committed after its checks, then receives two independent
review rounds from fresh agents. Confirmed findings are fixed and committed before
the next round or step. Reviewer assignments cover security/isolation, correctness,
concurrency, migration/rollback, tests that can fail, maintainability and corpus drift.
The available review models are used; this environment provides no Opus or Sonnet.

Validation includes warnings-as-errors builds, backend formatting, architecture,
unit, contract and integration suites (real PostgreSQL as `learnstack_app`, zero
skips), a documentation link audit and migration-chain coverage. The pre-change run on
2026-09-14 passed **2,039** tests: 1,373 unit, 175 architecture, 490 integration and one
contract, with zero skips. This is a baseline, not validation of the accepted schema.
P02d-1 does not claim the later packets' writer, HTTP or browser evidence.

### Education content — minimum viable

The [accepted Education spec](../modules/education/README.md) defines separate
`Course` and `Lesson` roots for the walking skeleton. A lesson references its course;
course versions and modules remain [Phase 05](phase-05-education-learning-content.md)'s
scope, with explicit data-preservation obligations. The
[Domain Model interim notes](../architecture/02-domain-model.md#learning-content)
distinguish this shape from Phase 05's target hierarchy.

- `Course` — tenant-owned, optionally organization-scoped, with independent
  `draft` / `published` state under [ADR-0048](../decisions/0048-walking-skeleton-publication.md).
  Its title, summary and per-locale slug live in `course_translations`; its optional
  level reference pins an exact taxonomy revision and band. G5's write validation and
  unresolved-band presentation remain with their later packets. No versioning,
  programs or cohorts.
- `Lesson` — ordered within a course; its title and per-locale slug likewise live in
  `lesson_translations`. Its body is drawn the way
  [ADR-0018 § Renderer architecture](../decisions/0018-tenant-driven-customization-model.md)
  draws every content type: a `TenantContentType` the tenant authored names a composite
  in its `renderer_key`, and each declared field maps to a primitive
  ([Tenant Customization Model § 2 and § 8.1](../architecture/32-tenant-customization-model.md)).
  The two tenants' lesson pages therefore differ in **shape**, not only in copy. Which
  composite draws a lesson and which primitives this phase implements are **G18**. There
  is no `ContentEntry` aggregate and no authoring surface, which are
  [Phase 04](phase-04-cms-media-pages.md)'s. The lesson body carries its field values
  inline and is bound to the content-type revision it was validated against (**G4**); on
  write it is validated through `IJsonSchemaValidator`
  ([ADR-0043](../decisions/0043-customization-payload-validation.md)) against that
  revision, and which revisions a writer may bind is **G12**. That check constrains
  structure, not URL schemes, so which URLs a field may hold is **G19**. The read path
  does no schema evaluation
  ([Tenant Customization Model § 8.1](../architecture/32-tenant-customization-model.md)).
  No lesson items, no lesson item types, no completion semantics. Its foreign key to
  `Course` is composite on `tenant_id`: PostgreSQL evaluates referential integrity
  with Row Level Security bypassed, so a single-column key would let one tenant's
  lesson reference another tenant's course invisibly. The same rule applies to each translation satellite's
  key back to its parent. See
  [Database Standards § Foreign keys between tenant-owned tables](../standards/05-database.md#foreign-keys-between-tenant-owned-tables).

Course, Lesson and their translations are written only through Education commands on the
request path, as the customization rows and settings are
([§ Writers and seed](#writers-and-seed)). The read path trusts what the database holds,
so a row that bypasses the validating command is never checked again. Each command
writes one aggregate root
([ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)). None
has an HTTP route: the authoring surface is Phase 05's, and until
[Phase 03](phase-03-identity-admin.md) reachability stands in for authorization, as
[the Tenancy permission matrix](../modules/tenancy/permissions.md) records. P02d-2
implements the publication commands; richer publish-readiness validation and versioned
publication remain Phase 05's.

Every table this phase creates carries `organization_id`, so each is **tenant-owned and
organization-scoped** (§ Table classes in
[Database Standards](../standards/05-database.md)): `[TenantOwned]` **and**
`[OrganizationScoped]`, a nullable `OrganizationId`, the organization-aware EF query
filter, and the full policy set from the canonical template — the permissive isolation
policy and the two `AS RESTRICTIVE` write guards — as
`Every_TenantOwned_Entity_HasFilterAndRlsPolicy` and
`Every_OrgScoped_Entity_HasOrgIdAndFilter` require. The two satellites are plain
contained entities with both markers and no independent soft deletion, as the
[Education data model](../modules/education/README.md#data-model-and-invariants) records. The isolation machinery
is exercised by **Education content** tables here, not only by the tables Phase 02a
applied it to (the [standards index](../standards/README.md#honest-status-today) row for
Database Standards carries the count, and `P02d-1` updates it). This is the first time
the template meets a table a learner's page reads, so it is the first chance to find out
it is wrong under that load: treat a surprising query result as a template bug, not a
data bug — once the row's `organization_id` and the host's class have been checked
against
[Localization § Slugs and URLs](../architecture/12-localization.md#slugs-and-urls). A
tenant host that does not show an organization-scoped course is that rule working, which
is the misreading
[ADR-0036 § Consequences](../decisions/0036-tenant-resolution-trusted-inputs.md#consequences)
names.

### Localization schema — the one-way door this phase walks through

[ADR-0008](../decisions/0008-localization-schema.md) is Accepted, it names `Course` and
`Lesson` explicitly among the side-translation-table entities, and its Consequences say
plainly: *"Slugs are stored on the translation row, not on the parent."* Phase 02d
creates the first real tenant-owned content tables in the whole roadmap, so it is the
phase that either honours that decision or spends
[Phase 04](phase-04-cms-media-pages.md) undoing it with the global migration ADR-0008's
Context exists to avoid — and no later phase budgets for that move. There is no
walking-skeleton exemption.

The four tables take their shape from the canonical artefacts, not from a list kept
here: the parent and its policy set in [Database Standards](../standards/05-database.md)
and its § Foreign keys between tenant-owned tables, the satellite in
[§ Translation satellite tables](../standards/05-database.md#translation-satellite-tables),
and the split between translatable and non-translatable columns in
[Localization Standards § Pattern A](../standards/08-localization.md#pattern-a--side-translation-table-default-for-content-shaped-entities).
What those documents already require of this phase's tables:

- The parents hold non-translatable columns only. `courses.slug_key` is a stable
  authoring handle that **nothing routes on**, unique per tenant among live rows. The
  satellites are keyed `PRIMARY KEY (<entity>_id, locale)`, each with a real `tenant_id`
  and a mirrored `organization_id`. `course_translations` holds the title, the summary
  and the routable `slug`; `lesson_translations` holds the title, the routable `slug`,
  and the translated JSON object `body`. The exact content-type revision pin lives on
  `lessons`, as the [Education spec](../modules/education/README.md) records.
- `courses` **and** `lessons` each carry `UNIQUE (tenant_id, id)`, because a composite
  foreign key references each of them.
- Every foreign key is composite on `tenant_id`, sets `ON DELETE` explicitly and has a
  supporting index
  ([Database Standards § Indexes](../standards/05-database.md#indexes)); a translation
  satellite cascades from its parent.
- A closed-set state column is `text NOT NULL` with a `CHECK`. An entity deriving
  `AuditableEntity<TId>` maps its audit, soft-delete and `row_version` columns, and a
  unique index on a table carrying `deleted_at` is filtered `WHERE deleted_at IS NULL`
  ([§ Soft Delete](../standards/05-database.md#soft-delete)).
- Each of the four tables has `ENABLE` + `FORCE ROW LEVEL SECURITY`, the full policy set
  and the `organization_id` immutability trigger. Row Level Security is per table and is
  never inherited from a parent through a check constraint; a satellite holding `title`
  and `slug` holds the content, so an unprotected satellite is a content leak.
- Each table's grants are written by the migration that creates it
  ([§ GRANT matrix](../standards/05-database.md#grant-matrix)).

The [accepted P02d-1 answers](#p02d-1-accepted-answers) close the aggregate and
satellite mapping, publication contract, exact content-type and taxonomy pins, stored
locale identity, slug grammar, foreign-key boundaries, grants and default orders.
The [Education spec](../modules/education/README.md) owns the detail; later writer,
read and routing parts remain explicitly open in the decision register.

**Organization mirrors require their own database guard.**
[ADR-0003 Amendment 6](../decisions/0003-tenant-isolation-defense-in-depth.md#amendment-6--insert-scope-and-parent-mirrors-2026-09-14)
requires a locked invoker check on INSERT and UPDATE as well as factory derivation,
tenant-composite foreign keys and organization immutability. It also closes the
organization-session tenant-wide INSERT gap in the inherited template and requires
an immutability function that does not read `OLD.id`. P02d-1 implements these controls
with forward migrations; accepting the correction does not claim it already runs.

The slug constraint is `UNIQUE (tenant_id, locale, slug)` — flat across organizations,
with `organization_id` deliberately excluded. Every column in that key is `NOT NULL`, so
PostgreSQL's nulls-are-distinct rule cannot apply and the constraint rejects every
duplicate it is meant to reject. The full reasoning, including why adding
`organization_id` to the key is the tempting move that breaks it, is in
[Localization Standards § Pattern A](../standards/08-localization.md#pattern-a--side-translation-table-default-for-content-shaped-entities)
and [Localization § Slugs and URLs](../architecture/12-localization.md#slugs-and-urls).
The key is the same on `lesson_translations`:
[ADR-0008 § Decision](../decisions/0008-localization-schema.md) makes every slug unique
within `(tenant_id, locale)`, so two lessons in two different courses of one tenant
cannot hold one slug in one locale, and the course segment of the lesson route does not
scope the lesson slug. Under this table-wide key a translation row holds its slug from
the moment it is inserted, published or not; which command reports the collision is
**G11**.

`tenant_locales` already exists —
[Phase 02a Packet 6](phase-02a-kernel-tenancy.md#delivery-record-packet-6) ships it and
already states it is required before any tenant-owned content table ships. The table
ships; the configuration does not. Neither seed tenant holds a row, and no command
writes one — `Tenant.AddLocale` and `SetDefaultLocale` have no caller outside tests.
[ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)
requires locale rows to be written by their own command in their own transaction: the
one raising `tenancy.locale.write`, `(planned)` in
[the Tenancy audit matrix](../modules/tenancy/audit.md). This phase ships it (**G11**).
Case variants of one tag are one locale
([ADR-0018](../decisions/0018-tenant-driven-customization-model.md)'s 2026-09-04
amendment), and how the shipped table spells a locale is in
[Localization § Locale Identifiers](../architecture/12-localization.md#locale-identifiers).
The satellites store `LocaleTag`'s canonical spelling; parameter canonicalization
remains **G6 (b)** and route-segment behavior **G6 (c)**. Whether a
translation may be written for, or a read resolve under, a locale absent from or
disabled in `tenant_locales` is **G13**.

What this phase does **not** build: per-locale publish readiness as a workflow and the
`tenant_route_slugs` cross-table registry, which are
[Phase 04](phase-04-cms-media-pages.md#localization-and-slug-uniqueness)'s; any Studio
surface for entering translated values, which this phase seeds and which Phase 04
registers as an open question in
[§ Admin Studio CMS Screens](phase-04-cms-media-pages.md#admin-studio-cms-screens); or
locale negotiation from `Accept-Language` for API-returned messages
([Error Handling Standards § Validation Errors](../standards/09-error-handling.md#validation-errors)),
which is also Phase 04's. What it builds is the shape, so that adding a locale later is
data — a `tenant_locales` row written through the Tenancy locale command, plus
translation rows — and not a migration.

The public routes this phase adds fall under
[Localization Standards § URL Strategy](../standards/08-localization.md#url-strategy),
including its locale-less redirect. The
[standards index](../standards/README.md#honest-status-today) row for that standard
places the i18n runtime in Phase 04, so how much of it this phase builds is **G36**.

### Writers and seed

The seed stays on the request path, as Phase 02a's is: every row it writes goes through
a command, and every command it sends crosses the pipeline, is classified in its
module's audit catalogue and registered in both composition roots.

- **Education commands** write courses, lessons and their translations, and publish
  courses. Course published / unpublished is baseline MUST-class in
  [Audit Coverage Standards](../standards/18-audit-coverage.md). The command set and
  re-run behaviour are **G11**; the Customization contract the lesson writer calls, and
  which revisions it may bind, are **G12**.
- **Tenancy commands** raise `tenancy.locale.write` (SHOULD) and `tenancy.setting.write`
  (MUST), both `(planned)` today. The setting command lands only once **G17** is
  answered, because no command writing `tenant_settings` precedes the `[PiiSensitive]`
  decision ([Phase 03](phase-03-identity-admin.md)); its keys and value checks follow
  **G16**, and it performs the contrast check
  [Accessibility Standards § Color and Contrast](../standards/16-accessibility.md#color-and-contrast)
  requires before a theme token is saved, with the outcome G16 records.
- **The Education module spec**, `docs/modules/education/` (`README.md`, `audit.md`,
  `permissions.md`), lands with the first Education aggregate in `P02d-1`:
  `Every_Module_With_An_Aggregate_Or_A_Request_Has_A_Matrix` fails without it, and
  `education` joins the list `Every_Module_Has_An_AuditCoverage_Matrix` pins in the same
  change. `permissions.md` is a forward declaration on
  [the Tenancy precedent](../modules/tenancy/permissions.md).
- **Composition.** Each new request type is registered in its module's
  `IAuditCatalogSource`, and the Education Application assembly joins both roots'
  MediatR handler scans. No structural test holds the scan: a missing assembly fails as
  "no handler for request" at the call site.
- **The seeder** gains an act and an ownership check per new write — a conflict on an
  act with no check stops the run, so a missing check fails only the second run — and
  every seeded row is written at the scope **G14** names. Scope comes from the seeder's
  announced context, never from a command field
  ([Security Standards § Forbidden](../standards/11-security.md#forbidden)), and a
  tenant-wide row can be updated only from a context that announces no organization. The
  seeder announces its context today by calling `IUnitOfWork.SetTenantContextAsync`
  itself, which ADR-0040's closed setter set does not list: **G15**.

Each tenant's **own** customization data — the table in § Genericity proof — is authored
through the Customization module's commands, as Phase 02a's seed is, and presented as
**G18** decides; its branding tokens are tenant settings written through
`tenancy.setting.write`. The surface a tenant admin writes them through is
[Phase 06](phase-06-renderer-admin-studio.md)'s. Phase 02a seeds both tenants with the
same built-in `card` content type and `plain` taxonomy, owned per tenant: that proves
the rows are isolated, not that they differ, and making them differ is this phase's.

### Customization read path

The design is decided and this section links it rather than restating it.
[Tenant Customization Model § 8.2](../architecture/32-tenant-customization-model.md#82-cache-strategy)
owns the cache families, their keys and the generation rule;
[ADR-0043](../decisions/0043-customization-payload-validation.md) § 6 removes the
compiled-validator cache and § 7 places the counter; and
[the Customization spec](../modules/customization/README.md)'s § Primary read flow is
the record this phase rewrites from "Not implemented".

Obligations already imposed:

- Other modules read Customization only through `Customization.Application.Contracts`,
  which names SharedKernel types only
  ([ADR-0010](../decisions/0010-cross-module-communication.md)). Education's lesson
  write needs that contract before any anonymous read does.
- Every MediatR request type added is classified
  ([ADR-0044](../decisions/0044-audit-write-path.md)).
- Keys are composed through `CacheKey`, tenant first, and each family is registered in
  [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md)' cache cheat
  sheet and the adapter's `cache.name` mapping together.
- The generation counter is domain state, never a cache entry, and correctness never
  lives in the cache.
- `InMemoryCacheService` is L1 only; the L2 tier arrives on ADR-0035's trigger in Phase
  11.
- A loader that announces `app.tenant_id` on its own transaction is a new out-of-band
  setter, owing a dated ADR-0040 amendment and a
  [Security Standards § The out-of-band setters](../standards/11-security.md#the-out-of-band-setters)
  row.
- The read path does not validate
  ([Tenant Customization Model § 8.1](../architecture/32-tenant-customization-model.md)).

Open: the contract (**G12**), how the projection loads and stays correct (**G22**), and
the display fallback it applies (**G24**).

**The typed settings accessor** over `tenant_settings`, which Phase 02a left to its
first reader, lands here too. Under Row Level Security a `tenant_settings` read returns
tenant-wide rows plus the caller's organization's rows, so its result depends on
`app.organization_id`, and the policy's tenant-scope read has no carrier until
[Phase 03](phase-03-identity-admin.md)
([Security Standards § Tenant Context](../standards/11-security.md#tenant-context));
resolution follows the organization-over-tenant fallback. The declared eager
invalidation, `learnstack.tenancy.settings`, is booked to Phase 02b in the Tenancy spec,
and this phase's settings writes come from the seed, which runs as its own process, so
nothing it writes reaches a cache inside the API process. The accessor's name, keys,
loader and staleness bound, if any, are **G23**.

### Read API

From [Phase 05](phase-05-education-learning-content.md), through the API conventions
established in
[Phase 02a Packet 4](phase-02a-kernel-tenancy.md#delivery-record-packet-4), the
Education reads — a working proposal whose paths, shapes and count are **G25** and
**G26**:

- `GET /api/v1/courses?locale={locale}` — the published courses the host's resolved
  scope can see, for the catalog page, cursor-paginated per
  [API Standards § Pagination](../standards/04-api-design.md#pagination). Which rows a
  tenant host and an organization host each serve is decided in
  [Localization § Slugs and URLs](../architecture/12-localization.md#slugs-and-urls).
- `GET /api/v1/courses/{slug}?locale={locale}` — course detail with its lesson list.
- `GET /api/v1/courses/{slug}/lessons/{lessonSlug}?locale={locale}` — lesson detail.

The pages also need per-host site data none of these returns: the tenant's enabled and
default locales, branding tokens, taxonomy display values and the lesson content type's
field list. How that data reaches the renderer, and which pages consume which read, is
**G25**, answered before the OpenAPI baseline is stored. Whatever ships, every public
read is anonymous and is a `[PublicSurface]` request type with its row in
[API Standards § Public surface](../standards/04-api-design.md#public-surface), which
owns the set.

`locale` is **required**; a request without it is a `400`, not a silent default. With
per-locale slugs a slug does not identify a course on its own — the same string can be
one course's Turkish slug and another's English slug inside one tenant — so locale is an
identifying input, not a preference, and a defaulted locale would let one URL resolve to
different courses as a tenant's locale set changes. It lives in the query string rather
than the path because [Localization Standards](../standards/08-localization.md) puts
locale in the path of **public URLs**, and the renderer route serving that URL supplies
this parameter; the route set is **G25**'s. No header changes which resource a URL
identifies. This section answers only a missing locale and a slug untranslated in the
requested locale; every other locale input is **G30**, and until it closes the Active
`unsupported_locale` row in
[Error Handling Standards](../standards/09-error-handling.md) binds.
[Frontend Architecture Standards § Locale Resolution](../standards/07-frontend-architecture.md#locale-resolution)
still names `X-Locale` as the API carrier, which G30 reconciles; `Accept-Language` in
the [Frontend Architecture](../architecture/14-frontend-architecture.md) SDK sketch is
the message-language input, not the identifying locale.

Because locale identifies the response, any cache in front of these reads, or in front
of the pages that render them, keys on the full path and query **and** on the tenant the
request resolves to, and the organization where applicable
([Security Standards § Multi-Tenant + Organization Isolation Review Checklist](../standards/11-security.md#multi-tenant--organization-isolation-review-checklist)).
On the anonymous server-side path both tenants send the same request line, and the host
reaches the API only as a forwarded header
([ADR-0036](../decisions/0036-tenant-resolution-trusted-inputs.md)), so a key built from
the URL alone serves one tenant's response to the other. Whether these reads are
cacheable at all is **G27**; how the renderer renders and which Next.js caches it may
use is **G37**.

Slug resolution is **exact** on `(tenant_id, locale, slug)`
([Localization Standards § Pattern A](../standards/08-localization.md#pattern-a--side-translation-table-default-for-content-shaped-entities)).
The display fallback chain, computed once per request
([ADR-0008](../decisions/0008-localization-schema.md)), applies to display fields after
the entity is resolved and **never** to the slug lookup: a course with no `en`
translation has no `en` URL, and requesting one is a `404`. For the same reason a lesson
with no translation in the requested locale is omitted from the course's lesson list
rather than rendered as a link that cannot resolve. Localization Standards § Locale
Model and Localization § Fallback Rules state different chains, and the shipped
`LocalizedText.Resolve` narrows one subtag at a time and ends at the first authored
value; which chain is the record is **G24**, and a per-tenant fallback configuration is
Phase 04's.

**Publication is not a Row Level Security term.** The canonical policy filters on tenant
and organization only, so the database does not keep an unpublished course or lesson off
these anonymous reads; the application does. [ADR-0048](../decisions/0048-walking-skeleton-publication.md)
requires published course and lesson states before serving a lesson. **G29** applies
that contract alongside URL membership and soft deletion, and decides what a caller
receives for a row it may not see. Settled, and linked rather than restated: another
tenant's row is a `404`
([Security Standards § Error Messages](../standards/11-security.md#error-messages)); an
organization-scoped row is served only on its own organization's host; and the public
renderer renders published content only
([Frontend Architecture Standards § Public Site Renderer](../standards/07-frontend-architecture.md#public-site-renderer)),
with draft preview in Phase 06.

**The catalog list is the first query in the platform that mints a cursor.** Its
default order is `(created_at ASC, id ASC)`, fixed by P02d-1. P02d-4 closes the
remaining **G10** codec parts: the cursor's payload and version, what it binds, whether
it carries an integrity tag, its direction, which list parameters the endpoint binds,
and where it is decoded.
API Standards leave the shape to whoever mints it, and the answer's detail lands there.
This is also where a cursor its minter cannot read becomes a **400** `validation_failed`
naming `cursor`, not a `500`. Phase 02a's completion criteria carried that clause with
nothing to mint a cursor; it lives here, with the query that does. Whatever a cursor
carries, tenant and organization come from the resolved context, never from the cursor
([API Standards § Tenant Context](../standards/04-api-design.md#tenant-context)). The
detail reads are addressed by slug; whether the course detail's embedded lesson list is
bounded, and which fields it carries, is **G26**.

**The public surface.** A host-only context reaches only request types marked
`[PublicSurface]`, and a refusal answers the unknown-host `404`. Every anonymous request
type this phase ships carries the marker, and its row in the API Standards table lands
in the same commit, because the rule reads the table in both directions. Every one is
registered in the audit catalogue, `Off` included; none may be MUST-class
`read-sensitive`; no write command this phase ships carries the marker; and each
anonymous endpoint carries `[AllowAnonymous]` with the one-line reason
[Permissions Standards § HTTP endpoints](../standards/19-permissions.md#http-endpoints)
requires. Their audit class, permitted methods, write ban and the control that keeps a
controller on the pipeline are **G28**.

The reads resolve tenant and organization from the host and return Problem Details in
the one shape of
[API Standards § Error Responses](../standards/04-api-design.md#error-responses) — a
host that resolves no tenant and a host-only request to an unmarked type answer the same
`not_found` — and every response is a purpose-built contract, never an EF entity
([API Standards § Forbidden](../standards/04-api-design.md#forbidden)). Each endpoint's
action dispatches its request through `ISender` and maps the result with
`ToActionResult()`
([ADR-0032 § Sub-decision 6](../decisions/0032-exception-handling-logging-and-observability.md));
§ Risks says what enforces that today. The pipeline is not new here: every Tenancy and
Customization command the seeder sends already crosses `TenantContextBehavior` and
`TransactionBehavior` ([Packet 7](phase-02a-kernel-tenancy.md#delivery-record-packet-7))
and writes its MUST rows through `AuditLogBehavior`
([Packet 9](phase-02a-kernel-tenancy.md#delivery-record-packet-9)), and Packet 7's
request-level suite already drives a marked probe query through the host-only ceiling.
What is new: the first business endpoints, the first production request types a
host-only context reaches, and the first production queries the audit catalogue
classifies. What the first contract freezes under
[ADR-0024](../decisions/0024-api-versioning-policy.md) is **G26**, **G27** and **G31**.

### Public renderer

From [Phase 06](phase-06-renderer-admin-studio.md), in `frontend/apps/web` under the
`(public)` route group, the renderer serves at least a catalog page and a lesson page,
and a visitor reaches a lesson from the catalog through rendered links. Whether that
path passes a course page, which consumes the course-detail read, or the catalog carries
bounded lesson links is **G25**, answered in the same pass as the site data because one
option changes the catalog response.

- Course catalog page — lists the published courses the host's resolved scope can see,
  through the catalog list.
- Lesson page — renders a lesson body.

Both are Server Components fetching through the typed SDK. The level taxonomy and the
lesson content types come from the Customization module, and the lesson page renders the
fields the content-type revision each lesson is bound to declares, not a fixed set. A
tenant may hold more than one content type — Phase 02a's built-in `card` sits beside the
tenant's own, in the state **G14** records — so the renderer never infers a lesson's
type by convention or by tenant. The stored schema carries no display order — JSON
Schema gives `properties` none, and `tenant_content_types.json_schema` is `jsonb`, which
does not keep key order — and nothing gives a field a label in each of the tenant's
locales: **G18**. How a field the implemented subset does not draw renders, how an
`x-taxonomy` value displays and which band a course shows are **G41** and **G5**.

The visual identity comes from branding tokens that are tenant settings, per
[Frontend Architecture Standards § Tenant Branding](../standards/07-frontend-architecture.md#tenant-branding),
not from a hard-coded theme. They are read through the typed tenant and organization
settings accessor this phase adds in Tenancy (§ Customization read path) and reach the
page only through a public read. The accessor is internal:
[the Tenancy permission matrix](../modules/tenancy/permissions.md) grants no anonymous
role a `TenantSetting` read, so no `[PublicSurface]` request returns settings by key,
and an anonymous response carries only the tokens **G16** names, for the tenant and
organization resolution produced. Which tokens exist, what each accepts, whether any
layout option is among them, and whether `tenancy.white_label_branding` governs any of
it are **G16**; how the tokens reach the document is **G42**; the per-organization
override and the token merge remain Phase 06's.

The lesson page renders tenant-authored values to anonymous visitors, under rules
already in force: `dangerouslySetInnerHTML` only in the sanitised-HTML primitive
([Frontend Coding Standards § Forbidden](../standards/03-frontend-coding.md#forbidden),
enforced by lint); outbound URLs validated against an allow-list before rendering
([Frontend Architecture Standards § Security](../standards/07-frontend-architecture.md#security));
markdown, if rendered, through an allowlist-sanitising library
([Security Standards § XSS & Output Encoding](../standards/11-security.md#xss--output-encoding));
theme variables in the `--ls-*` vocabulary. No Content-Security-Policy exists before
[Phase 11](phase-11-production-hardening.md#security), so how the renderer handles
output is the only control on this surface. The URL and markup policy is **G19**.

These are the platform's first public pages, and
[Accessibility Standards](../standards/16-accessibility.md) bind them from their first
render: the target is WCAG 2.2 AA, never a later improvement. Testing Standards assigns
the automated axe run to Phase 06; nothing else in the standard is deferred. Document
language and direction follow the route's locale per
[Localization Standards § SEO](../standards/08-localization.md#seo) and
[§ Right-to-Left](../standards/08-localization.md#right-to-left), so the scaffold root
layout's fixed `lang="en"` and platform `<title>` do not survive this phase. What
evidence fails a build is **G43**; the language of fallback-resolved fields is **G24**.

Open here, each a register row: the UI strings and the i18n library (**G39**); route
files, page states, catalog pagination and chrome (**G40**); the rendering mode
(**G37**); whether the path sets cookies or loads cross-origin subresources (**G21**);
and the test set (**G38**).

### Host-based tenant resolution, end to end

The full path from
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md#delivery-record-packet-7), exercised
for real — on the path a Server Component takes:

1. The browser's `Host` reaches Next.js. Both pages are Server Components, so the API
   call is made by the Next.js server, and the `Host` the API sees is not the visitor's.
2. The server SDK states the visitor's host to the API over the trusted hop
   ([ADR-0036 § Effective host and the trusted hop](../decisions/0036-tenant-resolution-trusted-inputs.md#effective-host-and-the-trusted-hop)),
   whose § Consequences names `frontend/packages/sdk/src/server.ts` as the only frontend
   place that sets the hop headers.
3. The API resolves that effective host through `platform_host_to_tenant` to a
   `(tenant_id, organization_id?)` pair, the singleton `ITenantContextAccessor` is
   written and the transient `ITenantContext` resolves from it on every access, the
   transaction sets `app.tenant_id` / `app.organization_id` with `SET LOCAL`, and Row
   Level Security filters every read.
4. Whatever the edge resolves is for rendering only, and any `X-Tenant-Id` is an
   assertion
   ([Frontend Architecture Standards § Tenant Resolution](../standards/07-frontend-architecture.md#tenant-resolution)).
5. In this phase's default topology the loopback bind, not the secret, is the boundary
   (ADR-0036 § Consequences).

None of that path is wired from the renderer's side yet — § What this phase inherits
lists the scaffolds this phase replaces. The topology and its evidence are **G33**, the
limiter in front of it **G34**, the SDK transport **G31** and **G35**, the middleware
and entry behaviour **G36**, and the rendering mode **G37**.

Two hosts are registered in local development, one per seed tenant:
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md#delivery-record-packet-7) wrote one
`platform_host_to_tenant` row per seed tenant on the `*.learnstack.local` names
`SeedData` carries, and a browser reaches them today only after a hosts-file edit. How a
browser and `make demo` reach them — the development hostnames and transport — is the
part of [Phase 02b](phase-02b-events-auth.md)'s gate **G12** that closes here, because
the session cookie that phase sets is `Secure`; it is **G32**, Accepted in the decision
pass of the packet its Blocks cell names, per
[Roadmap § Decision Timing](README.md#decision-timing). Moving the hosts later rewrites
the seed literals, the URLs `scripts/seed.sh` prints, the README Quickstart, the
`seed-tenant` and `local-dev-setup` skills and the demo URLs, and leaves stale host rows
on warm workstation databases. Neither answer changes how the API classifies or resolves
a host
([ADR-0036 § Effective host and the trusted hop](../decisions/0036-tenant-resolution-trusted-inputs.md#effective-host-and-the-trusted-hop)).

### Genericity proof

Both seed tenants — the English school and the yoga studio from
[Phase 02a Packet 7](phase-02a-kernel-tenancy.md#delivery-record-packet-7) — render
their own catalog and lesson pages from their own customization data:

| Tenant | `TenantLevelTaxonomy` | `TenantContentType` | Branding |
|---|---|---|---|
| English school | CEFR levels (A1 … C2), key `cefr` | `grammar-topic` | Its own tokens |
| Yoga studio | Difficulty levels (Foundation … Advanced) | `asana-pose` | Its own tokens |

Level keys follow
[ADR-0018's key rule](../decisions/0018-tenant-driven-customization-model.md#2026-09-04--customization-keys-and-item-keys-are-lowercase):
`A1` … `C2` and `Foundation` … `Advanced` are display names, not keys. **G14** records
the yoga taxonomy's key, which tenant carries two locales, and the state of the built-in
`card` / `plain` beside each tenant's own.

The two sites differ in taxonomy, content shape, copy and visual identity. The binary,
the schema and the query paths are identical. If a code path has to branch on which
tenant it is serving, [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)
is not being honoured and the branch is the bug.

The proof has two halves, each with a Completion Criterion. First, the data differs in
shape: the taxonomies in key and band set, and the lesson-body content types in property
set. Second, no production code branches on which tenant it serves, which **G20**'s
mechanism checks. Seeing the difference in a browser is the reviewer's confirmation, not
the evidence. Page composition is not tenant data in this phase: it becomes data with
[Phase 04](phase-04-cms-media-pages.md)'s `TenantPageBlock` rows, and this phase's pages
are hard-coded route segments.

### Local development, demo and CI

- **`make demo`** is the single command a reviewer runs on a clean checkout. It brings
  the development stack up, seeds through the request path as `make seed` does, starts
  the API and the web app, and prints where both tenant sites answer. **G45** fixes the
  rest; the host step a browser needs is **G32**'s. Both sites render anonymously once
  seeded — the baseline Phase 02b's criterion re-checks with Keycloak stopped.
- **The contract checks.** The OpenAPI breaking-change check is a committed snapshot per
  live major, diffed per
  [API Standards § OpenAPI](../standards/04-api-design.md#openapi) and
  [Testing Standards § API Contract Tests](../standards/06-testing.md#api-contract-tests);
  the SDK drift gate is
  [Frontend Architecture Standards § SDK](../standards/07-frontend-architecture.md#sdk)'s;
  both activate with the first public operation (**G31**).
- **The Lighthouse budget** measures the two tenants' public pages against
  [Performance Standards](../standards/15-performance.md), which
  [Frontend Architecture Standards § Performance](../standards/07-frontend-architecture.md#performance)
  names as the budget owner, and runs the accessibility audit
  [Accessibility Standards § Tooling](../standards/16-accessibility.md#tooling)
  requires. Whether it activates here, on what harness, and what it asserts are **G44**.
  Lighthouse is neither an end-to-end suite nor WCAG conformance.
- **Activation and required checks.** Each deferred job activates through the edits
  [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main)
  lists, with its skip condition per **G31**. P02d-1 [repairs and verifies](#step-3--packet-completion-2026-09-14)
  the two inherited required-check gaps — `backend integration (Testcontainers)` and
  the `meta` check under its current name — before the first Education migration merges,
  because
  [Git Workflow Standards § Checks](../standards/14-git-workflow.md#checks) makes
  tenant-isolation tests a merge condition and the Education isolation suites run in
  that job.
- **Frontend tests.** The case set and the frontend skip refusal are **G38**.

### Architecture tests

[Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md) is the
canonical reference, and no copy of it lives here. Every catalogue row whose Phase
names 02d is Implemented and green by exit. P02d-1's accepted G8 answer registers
four rules: tenant-composite foreign keys, organization immutability, parent-scope
mirroring and Pattern A translation storage. Each becomes Implemented with a planted
violation that its predicate refuses; registration alone proves no enforcement.
`PublicSurface_Marker_Set_Is_Enumerated` gets its first subjects here while two of its
catalogued legs are not implemented (**G28**), and the tenant-branching check is
**G20**'s.

## Deliverables

**Schema and isolation**

- `Course` and `Lesson` in `LearnStack.Modules.Education`, in the aggregate shape **G2**
  records, with migrations, EF configurations, query filters and Row Level Security
  policies, including each lesson body's content-type binding and its storage (**G4**).
- `course_translations` and `lesson_translations`, conforming to
  [Database Standards § Translation satellite tables](../standards/05-database.md#translation-satellite-tables),
  with the accepted slug grammar in the [Education spec](../modules/education/README.md#localization-and-url-identity).
- The organization immutability trigger on all four tables, with a function that works
  on a table without `id` (**G8**); an index supporting every Education foreign key; the
  insert-time organization control and its test, and the organization write-guard
  behavioural test extended to all four tables, asserting `UPDATE`, `DELETE` and
  `INSERT` with the outcome G7 records (**G7**).
- The four tables' rows in
  [Database Standards § GRANT matrix](../standards/05-database.md#grant-matrix), written
  by the migration that creates each table, with the exact-grant test and
  `SchemaFixture.KnownTables` extended (**G9**).
- The structural guards accepted by **G8**, each Implemented with a companion that plants
  its violation.
- Education isolation tests connected as `learnstack_app`, at schema level and at
  request level, over the four tables, across tenants and across organizations, per
  [Testing Standards § Tenant Isolation Tests](../standards/06-testing.md#tenant-isolation-tests)
  and the
  [Security Standards isolation checklist](../standards/11-security.md#multi-tenant--organization-isolation-review-checklist).
  The packet that ships the first request-level Education case also replaces that
  Testing Standards section's authenticated, id-addressed example with the shape these
  tests take, or links the shipped class. The Phase 02a request-level suite
  (`TenantIsolationHttpTests`) and `SeederTests` are updated to the grown seed without
  loosening any assertion.
- The Education module spec under `docs/modules/education/` (`README.md`, `audit.md`,
  `permissions.md`), with its state diagram and the eligibility rule **G29** records,
  and `education` added to `Every_Module_Has_An_AuditCoverage_Matrix`'s pinned list in
  the same change.

**Writers and seed**

- The Education write commands (**G11**) and the Tenancy commands raising
  `tenancy.locale.write` and `tenancy.setting.write`; each removes its matrix row's
  `(planned)` marker and registers its catalogue entry in the same change
  ([Audit Coverage Standards](../standards/18-audit-coverage.md)).
- The `Customization.Application.Contracts` surface — an interface or a query, as
  **G12** records — through which Education obtains the content-type revision a lesson
  body is validated against and bound to.
- An Education `IAuditCatalogSource` registered in the API and seeder composition roots,
  and the Education Application assembly in both roots' handler scans, with a
  request-level test dispatching each Education request through the API root and
  `SeederTests` through the seeder root.
- The seeder's acts, ownership checks, ordering and contexts (**G14**, **G15**), and
  each tenant's own content type, taxonomy, locales, branding, courses and lessons.
  Every seeded row is written at the scope G14 names, and the rows the isolation
  criteria read exist. One of the two tenants has **two** enabled locales with genuinely
  different slugs per locale, so the schema is exercised rather than merely declared;
  the other has one.
- The `[PiiSensitive]` record on `TenantSetting.Value` (**G17**) and the branding token
  contract (**G16**), with its detail in Frontend Architecture Standards § Tenant
  Branding.

**Read internals**

- The customization read path per Tenant Customization Model § 8.2: the Customization
  contract, classified wherever it adds request types, and the generation-keyed
  projection with its cache families (**G22**). Each family is registered in the
  Infrastructure Stack Standards cheat sheet, the adapter's `cache.name` mapping and the
  Observability Standards metrics family list. The module spec's § Primary read flow is
  rewritten with its diagram and budget.
- The typed tenant and organization settings accessor over `tenant_settings`, internal
  to the backend, with its interface name and glossary headword, key composition, loader
  and staleness bound, if any (**G23**), recorded in the cheat sheet when G23 caches
  settings, and in the Tenancy spec's event row and budget.

**Public API and contract checks**

- The anonymous public reads — the Education reads in § Read API and whatever **G25**
  adds — each with its `[PublicSurface]` marker, its row in API Standards § Public
  surface, its audit-catalogue registration in the class **G28** records,
  `[AllowAnonymous]` with its reason, OpenAPI documentation, and `locale` a required
  parameter on the Education reads. Each response is a purpose-built contract whose
  fields, embedded list, locale metadata, validator stance and documented Problem
  Details responses are **G26** and **G27**'s.
- The two unimplemented legs of `PublicSurface_Marker_Set_Is_Enumerated`, each with a
  companion that proves it can fail, and the catalogue entry's Status updated (**G28**).
- The `@learnstack/sdk` types regenerated from that document
  (`src/generated/schema.d.ts`) and the SDK surface that compiles against them
  (**G31**).
- The CI gates the corpus assigns here: the OpenAPI breaking-change check over a
  committed snapshot and the SDK drift gate (**G31**), each activated through
  CONTRIBUTING's edits and shown able to fail, and the Lighthouse budget likewise if
  **G44** activates it in this phase; and the two inherited required-check edits,
  [verified in P02d-1](#step-3--packet-completion-2026-09-14) before the first Education migration merges.

**Server-rendering path**

- The two seed host rows Phase 02a Packet 7 wrote, reachable from a browser and from
  `make demo` over the hostnames and transport **G32** records, with every committed
  carrier of the seed host names moved in the same packet if G32 moves them.
- The server SDK transport in `frontend/packages/sdk/src/server.ts`, stating the
  visitor's host over the trusted hop (**G35**).
- `frontend/apps/web/src/middleware.ts` no longer answering the scaffold's `503`,
  writing the raw host as a tenant id or carrying TODOs that assign the work to Phase
  02a; what it carries, removes and answers follows **G36**.
- A development trusted-hop configuration the API and the Next.js server agree on, with
  every variable it adds listed in `.env.example`
  ([Infrastructure Standards](../standards/12-infrastructure.md)); placement per
  **G33**.
- The hop runtime evidence **G33** chooses, and the dated ADR-0036 amendment if G33 or
  **G34** requires one.
- The anonymous limiter's treatment of trusted-hop traffic as **G34** decides, including
  a decision to leave it unchanged; any rule change ships with its carriers — API
  Standards § Request and Response Limits, Security Standards § Rate Limiting and the
  catalogue entry `Anonymous_Requests_Are_Rate_Limited_Per_Peer` — and a companion case.

**Renderer**

- The `(public)` routes **G25** and **G40** fix in `frontend/apps/web`, tenant-branded,
  rendering from customization data, with link navigation from catalog to lesson.
- The lesson-body renderer: the composite **G18** chooses, registered in `composites.ts`
  under the containment rule; primitive components for the implemented subset, in the
  home **G41** chooses; the render-time outbound URL check.
- Theming token injection in the `(public)` layout per **G42**, emitting only the
  `--ls-*` vocabulary.
- The UI message layer and catalogue at the location **G39** records.
- Frontend tests for the code this phase adds, with the case set **G38** decides. The
  harness exists (`vitest.config.ts`, jsdom, Testing Library), with Packet 3b's
  placeholder page test and Packet 10's `src/test/lint-rules.test.ts`. These tests do
  not prove host-to-tenant resolution — the API resolves the host with authority, and
  the isolation suites prove it as `learnstack_app`. There is no authenticated route to
  test against yet; that split arrives with [Phase 02b](phase-02b-events-auth.md)'s
  session.
- The required `frontend` job refusing a Vitest run that reports a skipped or todo case
  in every workspace package that carries tests, proven with a planted skip, with
  `No_Architecture_Test_Is_Skippable` updated in the same pull request (**G38**).
- The accessibility review
  [Accessibility Standards](../standards/16-accessibility.md#process) requires of every
  new screen, recorded in the renderer pull request's description: a manual keyboard
  walkthrough of each public page on both hosts, a contrast check of each seeded token
  set, and the accessibility confirmation. Which of it is also asserted by a test or
  lint rule is **G43**'s.
- The tenant-branching check **G20** selects, Registered in the first pass that uses it
  and Implemented with its companion before exit.

**Demo, CI and exit**

- `make demo` and its stop behaviour per **G45**, with README § Quickstart, the closing
  output of `scripts/seed.sh` and the `local-dev-setup` and `seed-tenant` skills updated
  in the same packet, and `run-tests-locally` if G44's job changes how Lighthouse runs.
- The full-stack Lighthouse job and its committed configuration, if **G44** activates it
  here.
- The [standards index](../standards/README.md#honest-status-today) rows this phase
  makes stale — the notes on Frontend Coding, Frontend Architecture and Accessibility
  Standards, the table count on Database Standards — each updated in the pull request
  that lands the code, plus any Performance or Accessibility status change G44 settles,
  made with its enforcer. The prose counts the Education chain makes stale —
  `SchemaFixture.KnownTables`' summary and the `add-integration-test` skill's note — are
  updated in `P02d-1`.
- The delivery record at exit names the Education model shipped, the preservation
  obligations G2 recorded, the seed fixtures Phase 05's migration must carry, and the
  premises this phase moved that `P02b-0` re-verifies: the first `/api/v1` endpoints,
  G32's hosts, G34's limiter answer, G14's seed context and the
  `learnstack.tenancy.settings` event G23 leaves to Phase 02b.

## Completion Criteria

Each criterion below is observable. Either a named test in a named CI job can pass or
fail on it, or it is marked **manual** because
[Testing Standards § End-to-End Tests](../standards/06-testing.md#end-to-end-tests)
leaves the browser check to a human; the packet's delivery record then records the
commit, the command run, each URL opened and what it showed. Criteria whose shape
depends on an open gate name that gate, and are written in full when it is Accepted.

**Two sites in a browser**

- Opening host A shows the English school's catalog, and host B the yoga studio's. Each
  shows its own branding with `NullEntitlementProvider` registered, which **G16** (g)'s
  answer must keep true, and level bands drawn from its own tenant's
  `TenantLevelTaxonomy`, each band named from the item's `display_name` in the requested
  locale. Where a band is observed, and what renders for a band the resolved revision
  does not declare, are **G5**'s. **Manual.**
- On either host, a visitor starting at the catalog reaches a lesson through rendered
  links alone, without typing a URL, and the lesson renders that tenant's lesson body;
  every link the catalog and any intermediate page render resolves on the same host and
  locale (**G25**). **Manual**, plus a render test in the `frontend` job asserting the
  links match routes that exist.
- Both hosts are answered by the same API build and the same `apps/web` build against
  one database and one schema; no build or deployment exists per tenant. **Manual.**
- The two lesson pages render **different field sets**, driven by each tenant's
  `TenantContentType` — not the same template with different strings — each field in the
  order, and under a label in the route locale, that **G18** defines, asserted against
  the content type as read back from `tenant_content_types`; on the two-locale tenant
  every rendered field label is in the route locale under both locale prefixes, and no
  property identifier appears as visible label text (`frontend` job, plus the manual
  record). The two seeded lesson-body schemas differ in property count or in at least
  one property's JSON type, not only in key names, and the two taxonomies differ in key
  and item keys (`SeederTests`, `backend integration`). The same lesson rendering code,
  fed each tenant's content type, renders different field sets with no tenant input, and
  a companion that swaps the schemas swaps the rendered fields (`frontend` job; **G38**,
  **G41**).
- Neither seeded lesson page, served by the running stack, renders a fallback or
  placeholder block (the full-stack job, **G44**; otherwise **manual**).
- `make demo` on a clean checkout — no `.env`, no `frontend/apps/web/.env.local`, no
  compose volumes, and no host configuration beyond **G32**'s host step — leaves both
  tenant sites answering at the addresses it prints, each showing its own tenant's
  content. Its re-run and stop behaviour are asserted in the shape **G45** fixes, and
  the delivery record names the browser and operating system the check ran on.
  **Manual**, or the full-stack job if **G44** builds one.
- If **G32** moves the seed hosts, re-running the documented seed or demo path against a
  database seeded with the old host names leaves both new hosts resolving to their
  tenants, and the step G32 records says what happens to the old
  `platform_host_to_tenant` rows (a `SeederTests` case against a pre-seeded old host
  row, `backend integration`; otherwise **manual**, in the blocking packet's delivery
  record).
- After seeding, with the compose `keycloak` service stopped, the catalog and a lesson
  page on both hosts still render anonymously — the baseline Phase 02b re-checks. A step
  in the full-stack job if **G44** builds one; otherwise **manual**.

**Schema and isolation** — Education schema-level and request-level suites in
`LearnStack.Tests.Integration`, connected as `learnstack_app`, in `backend integration`

- Two courses in one tenant cannot both hold one `(locale, slug)` pair; the second
  insert is rejected by the database, and the rejection also fires when one of the two
  is tenant-wide and the other organization-scoped. Two lessons in two different courses
  of one tenant cannot hold one `(locale, slug)` pair. Two courses in one tenant can
  hold one slug in two different locales, and each resolves to its own course.
- Read directly, each of `courses`, `lessons`, `course_translations` and
  `lesson_translations` returns no foreign tenant's rows, tenant-wide ones included, and
  no sibling organization's rows; each has a foreign-`tenant_id` write case, which
  `Every_WithCheck_Policy_Has_A_Foreign_Write_Case` already demands once the chain is
  applied. With the EF query filter removed, a read of each satellite under tenant A
  returns no tenant B row.
- Under tenant A, inserting a lesson whose course key names
  tenant B's row, or a translation whose parent names tenant B's row, is refused by the
  foreign key.
- A child row whose organization differs from its parent's — including a
  null-organization child planted under another organization's course and an
  organization child under a tenant-wide parent — is refused by the mechanism **G7**
  records, while matching inserts in the same test succeed. An organization-scoped
  session's `INSERT` of a tenant-wide row into `courses` and `tenant_settings` has the
  outcome G7 records, and
  `An_Organization_Scoped_Session_Cannot_Write_A_Tenant_Wide_Row` now asserts
  `INSERT`, `UPDATE` and `DELETE` of a
  tenant-wide row on both tables. On each of `courses`, `lessons`, `course_translations`
  and `lesson_translations`, an organization-scoped session's `UPDATE` or `DELETE` of a
  tenant-wide row affects no row or is refused, and its `INSERT` has the outcome G7
  records.
- Changing `organization_id` on a row of each of the four tables raises the immutability
  error from a session the restrictive guard admits (**G8**).
- `Every_Foreign_Key_Has_A_Supporting_Index` passes with the four tables in its swept
  set, and `Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows` with each of
  them that carries `deleted_at` under the mapping **G2** records; the grant-matrix test
  lists exactly the Education privileges § GRANT matrix records (**G9**). Each guard
  **G8** registers is Implemented, and its companion fails against a planted violation.
  Every migration chain, Education included, reverses to an empty schema.
- Before the first Education migration merges, **G2**'s row is closed, the Education
  spec's data model names `Course`'s and `Lesson`'s aggregate roles, and the lessons
  foreign key's delete rule matches that role. **Manual**, recorded in `P02d-1`'s
  delivery record.
- A publication-state value outside the `CHECK` set **G3** records is rejected by the
  database as `learnstack_app`.
- `docs/modules/education/audit.md` exists, and
  `Every_Module_Has_An_AuditCoverage_Matrix` names `education` (`backend` job).

**Writers and seed** — `backend integration`, as `learnstack_app`, unless named
otherwise

- After the seed, each seed tenant has one committed MUST-class audit row per published
  course and per `tenant_settings` write, whose entity id matches the row (**G3**,
  **G11**, **G17**). The `tenancy.setting.write` row's before and after for `Value`
  match the recorded `[PiiSensitive]` answer.
- No Tenancy or Education matrix row whose command ships still carries `(planned)`, and
  `Every_Shipped_Request_Is_Registered` and `CompositionRootCatalogueTests` pass for
  both roots.
- A second seed run exits 0 and changes no Education, `tenant_locales` or
  `tenant_settings` row; each seeded row carries the `organization_id` **G14** names;
  each seed tenant has exactly one enabled default locale, and the bilingual tenant two
  enabled locales.
- The Phase 02a request-level suite (`TenantIsolationHttpTests`) still names the exact
  rows it expects: its customization cases assert `BeEquivalentTo` an enumerated
  per-tenant set that includes the built-in `card` / `plain` rows. `SeederTests` asserts
  exact counts and generations, recomputed from the seed **G14** records, with each
  value derived in the writers delivery record. The suite's raw `tz` and `theme`
  `tenant_settings` inserts still succeed beside the seeded settings (**G14**).
- With migration history unchanged, a request-level test adds a third locale — a
  `tenant_locales` row through the Tenancy locale command plus translation rows through
  the Education commands — and the host answers `200` at the new slug while the existing
  locales' responses are unchanged (**G11**, **G13**, **G22**).
- An Education translation write for a locale absent from, or disabled in, the tenant's
  `tenant_locales` behaves as **G13** records, and a refused write commits no
  translation row. After the seed, no `course_translations` or `lesson_translations` row
  stores a `locale` spelling other than the one **G6** (a) records.
- A lesson write whose body fails its bound revision returns `validation_failed` with
  details keyed by JSON pointer and persists no lesson or translation row; any audit row
  the catalogue owes records outcome `failed`. A write binding an absent revision, or
  one that exists only in the other tenant, is refused indistinguishably, as is a
  revision **G12** declares ineligible. The English tenant's seeded lessons are bound to
  `grammar-topic` and the yoga tenant's to `asana-pose`, none to the built-in `card`.
- A duplicate slug written through the command **G11** names returns
  `Result.Fail(business_rule_violation)` and does not throw. A slug outside the shape
  **G9** settles is refused by every layer G9 names.
- A publication-state transition **G3** forbids answers
  `Result.Fail(business_rule_violation)` from the command, does not throw, and writes
  nothing (`backend integration`, plus a command or aggregate unit case in `backend`).
- A branding value outside **G16**'s grammar — `red;}body{background:url(//x)}`, a
  string containing `</style>` — never reaches a rendered document, and a refused write
  commits neither a `tenant_settings` row nor an audit row. A key outside the contract,
  and a failing contrast pair, behave as G16 records — never a silent save.
- A branding key written as an organization-scoped `tenant_settings` row behaves as
  **G16** (e) records, checked request-level through a host mapped to the organization:
  refused at write, committing neither a `tenant_settings` row nor an audit row; stored
  while the page served on that host is unchanged; or, if G16 (e) applies it, shown on
  that host's page and on no other host's.
- If **G19** includes a write-time rule, a lesson write holding a disallowed URL is
  refused with details naming the JSON pointer, and nothing is written.
- Whatever **G18** refuses at save — which may include a presentation entry naming a
  property absent from `properties`, a malformed label map or a field shape it does not
  admit — returns `validation_failed` with details naming the JSON pointer and writes no
  `tenant_content_types` row (`backend`).
- A course write naming a taxonomy revision or band
  key the tenant does not declare, including one only the other tenant declares, returns
  `validation_failed` naming the field and writes no row.
- Under **G15**'s answer, `SeedRunner` either no longer announces its own tenant context
  or appears in ADR-0040's setter table, and a planted caller fails any scan G15 adds
  (`backend`).

**Read internals** — `LearnStack.Tests.Integration` and `LearnStack.Tests.Unit`

- With the API running, publishing a successor content type or taxonomy for one tenant
  through its command changes what the next read returns for that tenant, within the
  bound **G22** states and without a restart; the other tenant's read is unchanged.
  After seeding, a breaking successor of the English tenant's lesson content type leaves
  every stored binding unchanged, and the lesson read still presents the original
  revision's fields (**G12**).
- A second warm read for the same tenant and generation issues no definition-set query;
  a publish that rolls back after its generation bump leaves reads on the prior
  definitions, including after a later committed bump reaches the same number; warm
  reads alternated between the two tenants return only that tenant's definitions,
  including for the built-in `card` both hold (**G22**).
- None of the public reads, and no definition or settings read, invokes
  `IJsonSchemaValidator`.
- If **G23** caches settings, then after that cache is warmed in the yoga studio's
  default organization, a read in the sibling organization's context and a tenant-wide
  read each return exactly what an uncached read in that context returns. After a
  settings write, the accessor's value changes within the bound G23 states —
  immediately, under a counter or with no cache — recorded where G23 places it.
- Every cache family this phase adds appears in the cheat sheet, the Observability
  Standards family list and the `cache.name` mapping, and none of its keys reports
  `other` (`InMemoryCacheServiceTests`).
- For a request in an enabled `tr-TR`, a label authored only under `tr` resolves as the
  chain **G24** records, and a value missing in both the requested and the default
  locale renders the terminal state G24 records.
- The out-of-band setter guard stays green: either the setter table is unchanged, or the
  ADR-0040 amendment and Security Standards row land with the loader.

**Public read API** — request-level tests through the real middleware chain, as
`learnstack_app`, in `backend integration`, unless named otherwise

- In one test, a course slug that exists only in tenant B returns `200` with that course
  on tenant B's host and `404` on tenant A's host, and a `(locale, slug)` held by one
  course in each tenant returns each host's own course, compared by id. A status alone
  proves nothing here: a route miss and an unknown host also answer `404` with the same
  `not_found` code, and the same route's `200` is what rules them out.
- In the same test, a course returns `200` at its slug in a locale it is translated into
  and `404` for that slug under an enabled locale it has no translation for; a lesson
  with no translation in the requested locale is absent from the course's lesson list
  while a lesson that has one is present; a course with no translation in locale L is
  absent from the catalog in L and still listed in its other locales.
- On the organization host, a sibling organization's course is absent from the catalog
  and its slug returns `404`, while a tenant-wide course and its own organization's
  course are present; on the tenant host, an organization-scoped course is absent and
  `404`, while a tenant-wide course is present. Each translation satellite, read on the
  request's own connection with no query filter, returns only the resolved scope's rows.
- A draft course is absent from the catalog, and its detail slug and every lesson
  requested under it answer per **G29**; a lesson of published course X resolves under X
  and answers per G29 under published course Y's slug, and Y's lesson list never
  contains it; a draft lesson in a published course
  is absent from the lesson list and no response carries its title or slug; a
  soft-deleted course or lesson answers per G29. Where G29 requires one body, the
  comparison is against a slug that exists nowhere, with `instance` and `correlationId`
  masked.
- Each of the reads without `locale` returns `400` `validation_failed` naming `locale`,
  never a `500` or a defaulted locale; sending a different `X-Locale` or
  `Accept-Language` with the same `locale` parameter returns the same resource; a
  malformed, over-length, empty, repeated, non-canonical or not-enabled locale —
  including a disabled locale holding translations — returns the answer **G30** records,
  never a `500`, and exposes no translated field.
- A cursor value the catalog list cannot read — malformed or truncated — returns `400`
  `validation_failed` naming `cursor`, never `500`. Walking `nextCursor` with a `limit`
  smaller than the host's visible course count returns each visible course exactly once,
  in **G10**'s order. No cursor value, whatever it carries, returns a row outside the
  requesting host's visible set; which further classes also answer `400` — an edited
  payload, or one minted on another host or organization, under another locale, sort or
  filter — is recorded by G10, and each recorded class is a named test case. The catalog
  operation in the committed OpenAPI document publishes only the list parameters **G10**
  records; if it publishes `sort`, an unpermitted field answers `400`
  `lockey_sort_field_not_allowed`. Where G10 records that a rejected cursor is refused
  before `TransactionBehavior`, a test asserts that no transaction opens for it.
- A course's embedded lesson list returns the same order on every read, including for
  any sort values **G2**'s invariant permits to tie.
- A lesson whose bound `(key, schema_version)` revision cannot be resolved (a fixture
  removes it) answers as **G12** records — never a `500`, and never another revision's
  fields.
- A stored band that the resolved taxonomy revision does not declare yields the state
  **G5** records, never a `500` and never another tenant's band name.
- On each seed host, each shipped anonymous read returns `200` under a host-only
  context, and the same request on an unknown host returns the unknown-host `not_found`
  body. The API Standards § Public surface table names exactly the anonymous request
  types this phase ships, and `PublicSurface_Marker_Set_Is_Enumerated` passes over that
  non-empty set (`backend`). Every `[PublicSurface]` type is registered with the class
  **G28** records, and `PublicSurface_Requests_Are_Never_ReadSensitive` runs over the
  production set. The permitted-methods and tenant-owned-write legs are Implemented,
  each companion failing on its planted probe, and if **G28** (c) adds a runtime
  control, a marked probe whose handler attempts a tenant-owned write is refused by it
  with nothing committed. The control G28 chooses for controllers is shown to fail on a
  planted offender, or § Risks says review is the only control. Each anonymous endpoint
  carries `[AllowAnonymous]` with a one-line reason (**manual**, listed in the delivery
  record).
- If **G28** records `Off`, serving the anonymous reads on both hosts adds no
  `audit_log` rows.
- Every anonymous response — a catalog `200`, a detail `404`, a bad-cursor `400` and an
  unmapped-host `404` — carries the directive **G27** decides, and the validator stance
  G27 records holds. The identical request sent over the trusted hop with host A, then
  B, then A returns each tenant's own courses every time, with every cache the API
  registers enabled.
- The committed OpenAPI document lists exactly the public operations the register
  decides, each with `locale` required on the Education reads, and a planted extra
  operation fails the contract test; no schema reachable from a `[PublicSurface]`
  operation carries a property on **G26**'s deny-list, with a failing companion; each
  operation documents the Problem Details statuses G26 names, and a host test observes
  each documented `4xx` (`LearnStack.Tests.Contract`, `backend`). The course detail's
  embedded lessons carry only the fields G26 names, in the order it records, and no
  lesson body; if G26 sets a bound, a seeded course above it returns the bound and its
  truncation indicator. If G26 adopts alternates, the bilingual tenant's course and
  lesson detail reads return the other enabled, translated locale's slug and never a
  disabled or untranslated one, checked against a seeded translation in a disabled
  locale; each fallback-capable field **G24** makes report its resolved locale does so.
- Every anonymous operation in the committed OpenAPI document is referenced from
  `apps/web` — a `(public)` route or the middleware — so no public read ships without a
  consumer; a companion fails on a planted unreferenced operation (`frontend`; **G25**).
- The contract suite fails when the served `/openapi/v1.json` differs from the committed
  snapshot, shown with a companion; the breaking-change job fails on a planted change
  the pinned tool reports at the chosen level, and the delivery record lists which
  ADR-0024 rows that level detects and which it cannot see (**G31**). The SDK drift gate
  fails on a planted stale `schema.d.ts` and passes on the phase's final commit, and the
  regenerated `paths` is non-empty with `apps/web` typechecking against it; no
  `(public)` page or component declares a hand-written response type for a public read,
  and removing from the document a field a page renders fails typecheck, shown with a
  companion (`frontend`).
- Each activated check has no `(deferred …)` suffix in `ci.yml`, appears in
  CONTRIBUTING's required list and in the live required contexts, and its skip condition
  is handled per **G31**; `backend integration (Testcontainers)` and
  `meta (compose + commit hygiene + link audit)` are live required contexts before the
  first Education migration merges, and CONTRIBUTING's "Two required-check edits are
  outstanding" note and its warnings on those two entries are removed in the same
  change. **Manual**: the dated
  `gh api repos/HodeTech/LearnStack/branches/main/protection/required_status_checks`
  output, and for each pull request from `P02d-1` on, a comparison of those contexts
  against the pull request's check rollup, in the delivery record.
- `Handlers_Return_Result` stays green with Education's handlers counted, and an
  Education handler changed to return a raw DTO fails the build (`backend`).

**Server-rendering path**

- On both seed hosts the catalog renders through the Next.js server while no
  server-to-API call carries `X-Tenant-Id`, each host showing only its own tenant's
  markers; an `X-LearnStack-Host` naming host B leaves the effective host at the
  request's own host when sent from a peer outside the trusted networks, with a wrong or
  missing secret, or repeated; with the Next.js server's secret removed or mismatched,
  neither host renders tenant content. In the form **G33** and **G38** (c) choose:
  request-level hop tests in `backend integration` and the manual walkthrough, plus any
  automated smoke G38 adopts.
- Neither the built client assets nor rendered HTML contain the hop secret value, and no
  `NEXT_PUBLIC_` variable carries it; only `frontend/packages/sdk/src/server.ts` sets
  the hop headers on a request to the API, and importing the server entry from a Client
  Component fails the build or lint; the server SDK's API origin comes from server
  configuration, never from the inbound `Host` or any request header (`frontend` job,
  each with a failing companion; **G35**).
- Client-supplied `x-tenant-id`, `x-organization-id`, `x-locale`, `x-learnstack-host`
  and `x-learnstack-hop-secret` never reach an SDK call's outbound headers unchanged;
  under `next build && next start`, no `(public)` route and not `/api/healthz` answers
  the scaffold's `503`; `/` and a locale-less path, a disabled or malformed locale
  segment, and a platform or unknown host answer as **G36** records — on a seed tenant
  whose default locale is not `en`, never `en` (`frontend` job). Once `P02d-5` merges,
  no comment under `frontend/apps/web/src` assigns unbuilt host wiring to Phase 02a or
  cites a `resolve-host` endpoint
  (`git grep -nE "resolve-host|(wired|lands|plug in) in Phase 02a|Phase 02a (wires|resolves|resolution)" frontend/apps/web/src`
  is empty, recorded in that packet's delivery record).
- With the development hop configuration committed, the API starts under the committed
  Development configuration with no `.env` present, and every Development-environment
  fixture stays green (`backend`, `backend integration`); every hop variable is listed
  in `.env.example`.
- A non-hop peer is still limited per socket peer, getting `429` with `Retry-After` over
  budget, and rotating `X-Forwarded-For` or any header **G34** introduces buys it
  nothing (`RateLimitingHttpTests`). Through the whole middleware chain and one
  trusted-hop peer, anonymous traffic is partitioned and budgeted as G34 decides, and a
  single source sending novel `Host` values through the hop is refused before more
  resolver lookups than G34's budget, with the unknown-host cache within its cap.
- If **G35** places it here, a server-to-API call carries a `traceparent` whose trace id
  matches the API's Problem Details `correlationId`.
- On a production build, one `(public)` path requested on host A, then B, then A returns
  each tenant's own markers every time; the build reports every tenant-varying
  `(public)` route in the rendering mode **G37** decides, and the frontend source holds
  no cache option G37 forbids, shown to fail on a planted one; after a customization
  write bumps a tenant's generation, a page on that host reflects it within G37's
  freshness and the other host's page is unchanged.

**Renderer** — `frontend` job, unless named otherwise

- Each rendered public page's `<html lang>` equals the locale in its path, on both
  locales of the bilingual tenant, and `dir` follows that locale.
- Each tenant-host page has one `<main>`, no skipped heading level, a skip link and a
  descriptive `<title>` rather than a fixed platform string, and zoom is not disabled —
  route tests or the manual record, as **G43** picks; whatever `jsx-a11y` findings G43
  makes failing, a planted violation in a public route proves `pnpm -r lint` fails on
  it.
- Catalog to lesson works keyboard-only on both hosts with focus always visible, each
  page reflows at 320 CSS px, and the text and UI-component colours of both seeded token
  sets meet
  [Accessibility Standards § Color and Contrast](../standards/16-accessibility.md#color-and-contrast).
  **Manual**, in the renderer pull request's accessibility record.
- On the two-locale tenant, every visible platform-authored string resolves through the
  message layer **G39** records, in both locales; Localization Standards § Strings in
  Code, Localization § UI String Catalogue and the `add-i18n-key` and
  `add-frontend-route` skills name one catalogue path, and it exists. At exit, either
  ADR-0027 is Accepted with its decisions-index row targeting 02d, or the standards
  index still records the i18n runtime carve-out and Phase 04 owns ADR-0027.
- Tenant B's course URL requested on host A returns an HTTP `404` page in the browser,
  not a `200` with error text and not a `500`; a tampered `?cursor=`, an API `429` and
  an unavailable API each yield the state **G40** records; an empty catalog renders a
  state distinct from the error state.
- A page whose course or lesson carries a band the resolved revision does not declare
  renders the state **G5** records, without throwing and without another tenant's name.
- Field values containing `<script>`, an `<img onerror>` fragment and a `javascript:`
  URL render inert, and a `data:` or `http:` URL is rendered only if **G19** admits it.
  A lesson whose content type names a composite the frontend does not register renders
  the fallback block and logs a warning without throwing. A lesson whose bound
  content-type revision cannot be resolved renders **G12**'s page state without throwing
  and shows no other revision's fields. A field outside the implemented subset renders
  **G41**'s fallback carrying neither the raw value nor HTML built from it.
- A branding token value containing `;`, `}`, `url(` or `</style>` never reaches
  rendered HTML, and the theme is emitted only as `--ls-*` custom properties (**G42**,
  **G16**). Public page responses conform to **G21**'s answer on `Set-Cookie` and
  cross-origin subresources, checked mechanically.
- A non-branding setting present for a tenant (the fixture's `tz` row) appears in no
  `[PublicSurface]` response body and in no rendered HTML, on either host
  (`backend integration`, and the smoke run if **G38** (c) adopts one).
- A change that adds `it.skip`, `describe.skip` or `it.todo` to any Vitest file under
  `frontend/` fails the required `frontend` check, and each predicate in **G38**'s set
  has a test that fails when the predicate is inverted. The page-level two-host claim is
  asserted by a CI job that fails on a `503` or an error page, or recorded at exit as a
  dated manual check naming the commit, as G38 decides. No committed text of this phase
  calls a Vitest, Lighthouse or HTTP check end-to-end or proof of host-to-tenant
  resolution.

**Genericity and governance**

- No production code branches on which tenant it serves. The mechanism is **G20**'s:
  registered in [the catalogue](../standards/21-architecture-tests-catalogue.md),
  Implemented and green in a required check before exit, asserting that it read
  non-empty backend and frontend subjects, with a companion that plants a violation in
  each and shows it failing.
  [`Core_Modules_HaveNo_DomainSpecific_Names`](../standards/21-architecture-tests-catalogue.md#core_modules_haveno_domainspecific_names)
  does not stand in for it: that rule reads names and strips literals.
- No Education type, member, column, key or DTO name carries a forbidden domain term,
  and Education references Customization only through its `Application.Contracts`
  assembly (`backend`).
- If **G44** activates it in this phase, the Lighthouse job runs real steps under a name
  without "(deferred to Phase 02d)", audits the URL set G44 decides on both hosts, runs
  the accessibility audit Accessibility Standards § Tooling requires, cannot pass on an
  error page, a `503` or the other tenant's page, and a planted regression on a
  hard-asserted budget turns it red; its tool install and report destination are the
  ones G44 records. If G44 moves activation, the job's name and CONTRIBUTING's entry
  name the owning phase G44 records. The Lighthouse run over both hosts, if G44 builds
  one, and a scripted click-through of both sites record zero API `429`s with the
  limiter still registered ahead of classification.
- Every register row is closed in the form **G1** chose; every Deliverable is observed
  by at least one criterion; no catalogue row whose Phase names 02d is Registered or
  Awaiting backfill; backend runs report zero skips (`scripts/assert-tests-ran.py`); and
  the standards headers and index rows agree after the phase's transitions
  (`Standard_Status_Headers_Match_The_Index`, `backend`).
- Every row in § Explicitly not in this phase names a phase whose own document carries
  that capability in its scope, its deliverables or a registered open question; every
  committed carrier that names a seed host names the hosts **G32** records; and the
  carriers that describe this phase's output — Phase 06 § What Phase 02d already
  shipped, the MVP scope's second-tenant bullet and genericity paragraph, and the
  platform vision's two-tenant bullet — match what shipped. **Manual**, in the exit
  packet's checklist.
- The documentation Deliverables are in the tree at exit: the Education spec's
  `README.md` and `permissions.md` beside `audit.md`, with its state diagram and the
  eligibility rule **G29** records; the Customization spec's § Primary read flow with
  its diagram and budget; Testing Standards § Tenant Isolation Tests' authenticated,
  id-addressed example replaced or the shipped class linked; the
  `SchemaFixture.KnownTables` summary and the `add-integration-test` skill's note
  matching the applied chains; and the exit delivery record naming the Education model,
  the preservation obligations G2 recorded and the seed fixtures Phase 05's migration
  must carry. **Manual**, in the exit packet's checklist.

## Risks

- **The slice grows.** Every capability listed under "explicitly not in this phase" has
  a plausible argument for inclusion. The exit gate is a browser, not a feature set: a
  change that neither moves a pixel on one of the two sites nor is required by this
  phase's Deliverables, Completion Criteria or register belongs to its owning phase.
- **A gate is skipped, or answered before its ground exists.**
  [Phase 02b § Risks](phase-02b-events-auth.md#risks) carries both and names the
  premises this phase moves for it. The mitigation is each packet's decision pass, and
  the delivery record listing those premises for `P02b-0`.
- **The second tenant becomes decorative.** A yoga studio whose data is a renamed copy
  of the English school's proves nothing. Its taxonomy and its lesson-body content type
  must differ in shape, not only in strings, and the Completion Criteria assert both.
  Page composition is not tenant data in this phase.
- **Tenant-specific branching creeps into the renderer.** The most likely place is the
  content-type and taxonomy resolution path, where a missing generic primitive is
  easiest to paper over with a conditional. Any such branch is a defect in the
  customization model and should be fixed there. The domain-term scan strips literals
  and cannot see such a branch; the controls are **G20**'s mechanism and the criterion
  that the two tenants' content types render as different field sets through the same
  code. A branch that compares against a value read at runtime carries no literal: only
  the behavioural criterion and review reach it.
- **The renderer walks `properties` in storage order.** It is stable and green, and it
  shows fields in `jsonb`'s key order under their property names, so the genericity
  proof reads as a data dump. **G18** closes before the seed writes its content types.
- **Shortcuts around the pipeline.** The public reads are simple enough to write without
  a handler. A controller that takes a module `DbContext` fails loudly: the context is
  refused outside the ambient transaction
  ([ADR-0040](../decisions/0040-ambient-unit-of-work.md),
  [Database Standards § Connection Management](../standards/05-database.md#connection-management)).
  Two shortcuts fail quietly instead. SQL issued on `IUnitOfWork.Connection` without an
  announcement reads zero rows, which looks like an empty catalog. Code that opens the
  unit of work and announces the host's tenant itself reads that tenant's real rows,
  unpublished ones included, and skips the authority ceiling, audit classification and
  the query's own filters. `Handlers_Return_Result` sees neither, because it inspects a
  handler's response type and neither has a handler. The mechanical control is
  **G28**'s; until it closes, review is the only control.
- **A missing marker looks like a resolver bug.** A host-only request to a type without
  `[PublicSurface]` answers the unknown-host `404` by design, so a public read shipped
  without its marker reads as a resolution or Row Level Security defect. The tempting
  repair, widening the authority ceiling, is wrong: the fix is the marker and its API
  Standards row (**G28**).
- **The seed takes the short path.** A `DbContext` or SQL write for courses, lessons,
  locales or settings passes every rendering criterion while skipping write-time
  validation, the MUST publish row and the tenant context. The catalogue join catches a
  new command, not a write that bypasses commands, so the control is `SeederTests`
  asserting the MUST rows the seed must produce.
- **The seed writes content at the wrong scope.** Every seeder step after provisioning
  announces the tenant's default organization. If a command takes its scope from that
  context, it writes the English school's content organization-scoped, where the
  tenant-wide English host cannot see it, and a tenant-wide row re-seeded under that
  context is filtered out of the update by the `AS RESTRICTIVE` guard. An empty English
  catalog is then a seed defect, not a template bug.
- **The seed grows into the Packet 7 fixture.** The isolation fixture inserts raw `tz`
  and `theme` settings rows after the seed. A seeded tenant-wide `tz`, or an
  organization-scoped `theme`, collides with them under the unique
  `(tenant_id, organization_id, key)` index, which treats nulls as equal. The per-tenant
  content types break the suite's exact `BeEquivalentTo` sets. In both cases the easy
  repair loosens an exact assertion instead of recomputing it.
- **Seed data outside `SeedData` fails the genericity guard.**
  `Core_Modules_HaveNo_DomainSpecific_Names` exempts the `SeedData` type, its nested
  types and the file `SeedData.cs`, and nothing else. A seed type, member or file named
  for either domain anywhere else under `backend/src` fails the build.
- **The translation tables get written as an afterthought.** The satellite is a
  tenant-owned table in its own right: its own `tenant_id`, its own policy, its own
  `FORCE`. The failure mode is quiet — the parent's policy looks like it covers the
  child, and the child is where the title and the slug actually live. An isolation test
  that reads only the parent will not find it; the satellite-only isolation criterion
  does.
- **Common lesson slugs collide across courses.** ADR-0008 makes lesson slugs unique per
  tenant and locale, so two courses cannot each hold an `introduction` lesson in one
  locale. Changing that is a dated amendment to ADR-0008, Accepted before `P02d-1`;
  afterwards, rows written under the flat key constrain the move.
- **A structural rule passes because nothing violates it.** Each accepted **G8**
  guard needs a planted offender. Education consumes Tenancy's shared immutability
  function; migration and rollback runners must honor that documented dependency.
  No Education foreign key crosses into the Tenancy chain.
- **Publication filtered in one read and forgotten in another.** Row Level Security has
  no state term, and the seed publishes everything a reviewer clicks. A handler that
  filters the catalog but resolves a detail slug, or a lesson slug without its course
  segment, passes every happy-path check and serves unpublished content anonymously. The
  visibility criteria exist to catch it.
- **A later lifecycle change bypasses the read contract.**
  [ADR-0048](../decisions/0048-walking-skeleton-publication.md) assigns unpublishing,
  deleting and reordering commands to Phase 05. That phase must compose them with
  the read-eligibility (**G29**) and cache (**G27**) rules established here.
- **The read path ships uncached, or on a scope-blind key.** Every functional criterion
  passes either way; only the cache criteria and **G22** and **G23** catch it. The
  generation bump is an upsert increment, so a cache filled inside a transaction that
  bumped and rolled back can later be served as a committed generation; under
  `READ COMMITTED`, rows read before the generation can be cached under the newer key.
  Under a TTL answer to **G23**, a seed re-run against a running API shows stale
  settings for the stated bound, which a demo reviewer reads as a defect.
- **Cross-host reuse of a cached page or response.** Every isolation layer reports
  success because the cache answers before the API is reached, and `next dev` does not
  cache the way `next build && next start` does, so the defect shows only under a
  production build. The controls are **G27**, **G37** and the cross-host criteria.
- **A response or page cache inherits Performance Standards' invalidation rule.**
  [Performance Standards § Caching](../standards/15-performance.md#caching) names the
  course catalog list and the published page render as read-through candidates
  invalidated by integration events from the producing module, and this phase ships no
  Education publish event; the generation-keyed cache covers customization definitions,
  not course content. A **G27** or **G37** answer that caches Education responses or
  rendered pages reconciles that rule in the same pass, and the pull toward a late cache
  to meet **G44**'s budget is the same risk.
- **One anonymous budget behind the renderer.** Server-rendered reads share the renderer
  peer's limiter partition. A few visitors, a Lighthouse run or a click-through of both
  hosts can draw `429`s, and one client sending random `Host` values through the
  renderer can starve both sites while a direct caller keeps its own budget. The
  tempting repair — raising, bypassing or header-keying the limiter in the
  pre-classification stage, or relaxing it only where the checks run — would make the
  exit evidence measure a configuration no deployment runs. A catalog that fetches one
  detail read per course multiplies that draw (**G25**). The mitigation is **G34**. With
  fewer seeded courses than the catalog's `limit`, catalog pagination is never exercised
  in the UI, so courses past the first cursor page can be unreachable without any check
  noticing (**G40**).
- **The hop secret drifts between processes.** The API refuses to start only on a
  misconfigured hop, so if the API's copy of the secret and the Next.js server's copy
  diverge, nothing errors: the hop is silently ignored and every anonymous render
  answers `404`, which reads as a seed or template defect. The mitigation is **G33**'s
  answer to how one hop secret reaches both processes, plus the mismatched-secret
  criterion under § Completion Criteria's server-rendering group.
- **The first public contract freezes early.** Once the breaking-change check stores its
  baseline, the site-data shape, an internal identifier, an unbounded embedded list, a
  validator header or a list parameter (`sort`, `q`) the endpoint does not implement
  becomes a v1 contract Phase 05 and Phase 06 inherit: under
  [ADR-0024](../decisions/0024-api-versioning-policy.md), removing a field, renaming a
  path segment or changing an outcome's status needs `/api/v2`. G25 through G31 close
  before that baseline is stored.
- **An activated check gates nothing.** Required checks match by name, and a job skipped
  by its `if:` condition satisfies a required check.
  [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main)
  records the five checks made required in P02d-1; admin bypass remains possible under
  the separate maintainer decision. The later OpenAPI and Lighthouse activations still
  need their own live verification. A
  first run over a base with no `/api/v1` operations cannot fail, and neither can a
  Lighthouse run over placeholder pages. The mitigation is the live required-check list
  recorded at exit, a per-pull-request rollup comparison, and a planted failure per
  check.
- **Phase 05 migrates this phase's shape rather than extending it.** The canonical model
  puts lessons inside a course version and under a module
  ([Domain Model § Learning Content](../architecture/02-domain-model.md#learning-content)).
  Rich content with version history carries `version` on its side table
  ([Localization Standards § Choosing between patterns](../standards/08-localization.md#choosing-between-patterns)),
  which a flat `(tenant_id, locale, slug)` key would reject, and changing a published
  slug is breaking. The mitigation is **G2**: it records the shape this phase ships and
  what that shape obliges Phase 05 to preserve, and Phase 05 designs the migration in
  its own decision pass. This phase's published state could also later be read as
  learner authorization; Phase 07 owns course access.
- **The proof pages fail the audience they are shown to.** Two seeded palettes become
  the screenshots everyone evaluates, and a Lighthouse score is not WCAG conformance.
  The contrast rule's outcome is also unsettled:
  [Accessibility Standards § Color and Contrast](../standards/16-accessibility.md#color-and-contrast)
  describes a Studio warning, which means nothing for a command with no interface or for
  the seed. Until **G16** closes, the writer and Phase 06's editor can implement
  different rules.
- **The anonymous path quietly acquires cookies or third-party requests.** The Frontend
  Architecture Standards flowchart sets cookies, and Frontend Architecture serves logo
  and custom-font assets from CDN URLs; followed literally, either reaches a visitor
  before anyone has reviewed it (**G21**).
- **The demo passes only on a warm workstation.** A running stack, a `.env` copied once
  and never refreshed, host entries, a warm Keycloak volume, or processes already
  holding the API or web port can let a "clean checkout" pass on the author's machine
  alone. The mitigation is **G45**'s contract, and, if **G44** activates its job here on
  the same entrypoint, that job's run on a fresh runner.
- **A hidden identity-provider dependency.** The seed waits on both Keycloak realms and
  the web example environment already carries OIDC variables, so a render path that
  touches Keycloak would surface only in Phase 02b. The mitigation is the
  Keycloak-stopped criterion.

## Phase Exit Decision

[Phase 02b](phase-02b-events-auth.md) begins when a reviewer, on a clean checkout, can
open two hosts in a browser and see two visually different education sites, whose
taxonomies and lesson field sets differ in shape as the Completion Criteria assert,
served by one backend binary, one `apps/web` application and one database — and:

- every Completion Criterion holds with its named evidence, the manual ones recorded in
  the delivery record;
- every register row is closed through its vehicle, in the form **G1** chose, and
  reflected in its carriers — including **G32**, the development-transport part of Phase
  02b's G12, recorded where that row points;
- the Phase 02a request-level isolation suite and the Education schema-level and
  request-level isolation tests are green under `learnstack_app`, across tenants and
  across organizations, with zero skipped cases, in a required check;
- the OpenAPI breaking-change check and the SDK drift gate run in required checks, and
  the Lighthouse job as **G44** settles, each shown able to fail as its criterion
  states, with the date and the live required-check list recorded, and no job name still
  reads "(deferred to Phase 02d)";
- `make demo` on a clean checkout prints both addresses and both answer, and both sites
  still render with Keycloak stopped;
- the tenant-branching check **G20** selects is Implemented and green in a required
  check;
- both sites meet the accessibility criteria, with the accessibility review recorded in
  the renderer pull request;
- no catalogue row whose Phase names 02d is Registered or Awaiting backfill, and the
  standards index rows this phase changes agree with their documents;
- the delivery record names the Phase 02b premises this phase moved, for `P02b-0`.

## Delivery Record (P02d-1)

**Decision pass — 2026-09-14.** Maintainer approval closes G1, G2, G3 (values and
publication/transition contract), G4, G5 (column), G6 (a), G7 (database controls and
factory derivation), G8, G9, G10 (order), and G26 (slug grammar), through the
[accepted answers](#p02d-1-accepted-answers). ADR-0048 is Accepted and ADR-0003 gains
Accepted Amendment 6. At this decision commit, the Education spec became design stable,
the module matrix list gained Education, and the four structural rules were Registered.
The following records distinguish that accepted design from its implementation and
verification.

### Step 1 — Shared database controls (2026-09-14)

The Tenancy and Audit forward migrations require exact organization scope for
INSERT while preserving reads, existing restrictive write policies and stored rows.
Tenancy's shared immutability function now supports natural-key rows without `id`.
The tenant-composite FK and organization-immutability catalogue rules are Implemented,
with planted violations and repaired positive controls over the applied schema.

Behavioral proofs use authenticated `learnstack_app` connections. They cover both
existing tables, the tenant reporting hatch, pooled-session reuse after commit and
rollback, the no-id function and audit's append-only alternative. Disposable databases
keep test-only grants and control removal outside the shared schema. A populated
historical-schema test proves upgrade, exact Down restoration and reapplication.

The full backend suite passes **2,082** cases: 1,373 unit, 175 architecture,
533 integration and one contract, with zero skips. Backend formatting and generated
SQL checks pass. The first full run exposed three legacy audit tests whose manually
declared intents omitted the organization their transaction announced; those fixtures
now copy the execution context exactly as `AuditLogBehavior` does. No production audit
exception or policy relaxation was needed. The localization overview and storage
examples also now distinguish accepted Education storage from later authoring behavior.

Commit `807049a` completed two independent review rounds: three reviewers in round 1,
two fresh reviewers in round 2, covering security, migrations, runtime writers, proof
soundness and corpus consistency. Both rounds approved without findings or a required
fix commit. Each round independently reran 39 focused schema/migration cases; round 2
also reran six architecture/corpus checks. All reported zero skips.

### Step 2 — Education schema and isolation (2026-09-14)

Both aggregate roots and their natural-key translations are implemented. Education's
fifth migration chain creates four tables with exact organization INSERT scope,
restrictive UPDATE/DELETE policies, immutable organization ids, tenant-composite FKs,
parent-scope checks and the accepted grants. The API and seeder resolve its context on
the existing ambient transaction. No command or endpoint is introduced here.

The parent-mirror and Pattern A catalogue rules are Implemented. Their companions
plant absent subjects, weakened trigger controls, misplaced localized fields and
widened slug uniqueness. The applied schema sweeps and populated fixture include all
four Education tables. Domain tests constrain independent publication, locale
identity, exact pins, slug grammar and mutation atomicity. Actual app-role EF writes
prove graph persistence, contained audit capture and optimistic concurrency for
translation additions. Raw and filter-bypassing reads independently prove RLS.

Disposable databases isolate tests that grant missing mutation/TEMP privileges or
remove the parent trigger to reach RLS and foreign keys separately. Parent checks are
tested on insertion and reparenting, against temporary-table shadowing, and while a
test barrier holds execution between the lookup and the FK. This distinguishes the
required lookup lock from the FK's later lock. Reversal removes dependent chains first;
reapplication then persists populated Education rows as `learnstack_app`.

The full backend run passes **2,363** cases: 1,453 unit, 177 architecture,
732 integration and one contract, with zero skips. The migration model has no pending
changes; backend formatting, generated Up/Down SQL and the documentation link audit pass. PostgreSQL text
validation reuses the shared predicate instead of introducing an Education copy.

Round 1 reviewed commit `e5b8187` with three independent reviewers. Domain/EF/audit
and security/migration reviews approved; the latter independently reran all twelve
parent-scope cases and the complete reverse/reapply case. The proof review found one
Major gap: Pattern A's parent-field detector omitted `description` and
`seo_description`, although Standards 08 explicitly forbids them on a parent. The
production schema contained neither field. Three new applied-schema companions
failed against the old detector (plain, locale-suffixed and SEO-prefixed descriptions),
then passed after both the schema and CLR/model predicates were corrected. Each
companion also removes its planted column and verifies the same detector is clean.
All 41 Education structural cases pass with zero skips after this repair.

Round 2 reviewed the implementation and fix with three fresh reviewers. All approved
without further findings. Independent focused runs passed 281 security/persistence
cases and 129 structural/schema/migration cases, each with zero skips. The final full
run after the repair passes **2,366** cases: 1,453 unit, 177 architecture, 735 integration
and one contract, with zero skips.

### Step 3 — Packet completion (2026-09-14)

The approved GitHub repair was applied only to `main`'s `required_status_checks`
endpoint. A fresh full-protection API read at **2026-09-14 13:04 UTC** verified the
following snapshot, comparing check identities as a set because GitHub reordered the
entries. All five retain GitHub Actions `app_id: 15368`; `strict` remains `true`.

```json
{
  "strict": true,
  "checks": [
    { "context": "backend (build + unit + arch + contract)", "app_id": 15368 },
    { "context": "frontend (typecheck + lint + build + test)", "app_id": 15368 },
    { "context": "secret scan (leakwatch)", "app_id": 15368 },
    { "context": "meta (compose + commit hygiene + link audit)", "app_id": 15368 },
    { "context": "backend integration (Testcontainers)", "app_id": 15368 }
  ]
}
```

Verification command:
`gh api repos/HodeTech/LearnStack/branches/main/protection/required_status_checks`.
The full before/after comparison also verified that every field outside
`required_status_checks` remained unchanged, including the separately approved
single-maintainer exceptions. CONTRIBUTING and both affected standards-index rows now
state the live setting. The later OpenAPI and Lighthouse gates retain their owning
packets and open decisions.

The previous packet's `main` merge commit was merged into `development` without
rewriting history or switching branches. The tree before and after that merge was
identical, and `origin/main` is now an ancestor of the packet branch. This satisfies the
strict up-to-date requirement without changing the reviewed implementation.

The final full backend run passes **2,366** cases with zero failures or skips; the
nonempty-run checker verifies all four assemblies. Fresh-schema, populated upgrade,
full reversal and app-role reapplication proofs pass. The manual aggregate review
also confirms the accepted roles match the applied foreign keys: Lesson is a separate
root with RESTRICT to Course; each plain satellite CASCADEs only to its own root.
Education commands and public-read eligibility remain the explicitly owned work of
P02d-2 and P02d-4.

Informational coverage combines successful unit and integration runs by source
line/branch identity, excluding generated `obj` and EF migration files: Education
Domain is **235/235 lines and 80/80 branches (100%)**; Infrastructure is **139/152
lines (91.45%) and 0/2 branches**. The uncovered lines and branches belong only to the
design-time factory, exercised separately by the EF CLI model and SQL checks. The
first solution-wide coverage attempt failed when a Coverlet-injected tracker type
could not be resolved by NetArchTest's IL/reflection sweep; it is not passing evidence.
The subsequent unit/integration coverage runs and uninstrumented **177-case**
architecture run pass. The local-test skill now records that working procedure;
no test or assertion was skipped or weakened.

Backend formatting, all 177 architecture cases, 1,479 changed-Markdown relative
path/anchor checks, the tracked-file `docs/analysis/` residual scan and strict commit
hygiene pass. The two fresh-agent review rounds and PR required-check rollup
comparison are recorded below.

Round 1 reviewed commit `7c50e01` with two fresh reviewers. The delivery reviewer
recomputed the TRX and coverage figures, checked the migration assertions and reran
the link audit without findings. The operational reviewer independently verified the
live protection setting and found one stale inherited-baseline paragraph still saying
integration was not required; it is corrected above. The author's PR preparation also
found that CONTRIBUTING and the commit skill prescribed headings different from
Standard 14. Both now follow its canonical description structure, with the skill's
existing isolation-risk disclosure retained. These are carrier corrections, not new
decisions.

Round 2 reviewed `4d46560..ddb3454` with two fresh reviewers. Both approved without
further findings. They independently recomputed test/coverage figures, reran the
1,479-link audit, checked migration evidence, verified the live required-check names
and attribution against CI, and confirmed that the merge tree matches its first
parent and all unrelated protection fields remain unchanged. All three implementation
steps and their two review rounds are complete.

#### PR verification (2026-09-14)

[PR #22](https://github.com/HodeTech/LearnStack/pull/22), `development` → `main`,
passed [CI run 34848230938](https://github.com/HodeTech/LearnStack/actions/runs/34848230938)
on head `158f67da62dd6d8b4b542ec27ca0107c980d3ff5`. At **13:20 UTC**, a fresh live
protection read was compared with the commit's check runs. Each of the five required
contexts appeared exactly once with `app_id: 15368`, status `completed` and conclusion
`success`; none was satisfied by a skipped job or a legacy name.

| Required context | Result |
|---|---|
| `backend (build + unit + arch + contract)` | [Success](https://github.com/HodeTech/LearnStack/actions/runs/34848230938/job/103989296320) |
| `frontend (typecheck + lint + build + test)` | [Success](https://github.com/HodeTech/LearnStack/actions/runs/34848230938/job/103989296248) |
| `secret scan (leakwatch)` | [Success](https://github.com/HodeTech/LearnStack/actions/runs/34848230938/job/103989295976) |
| `meta (compose + commit hygiene + link audit)` | [Success](https://github.com/HodeTech/LearnStack/actions/runs/34848230938/job/103989296236) |
| `backend integration (Testcontainers)` | [Success](https://github.com/HodeTech/LearnStack/actions/runs/34848230938/job/103989296276) |

The backend jobs reran the same **2,366** cases: 564 Docker integration, 171 HTTP
integration, 1,453 unit, 177 architecture and one contract, all with zero skips.
Both jobs' nonempty-run checks passed.

The deferred OpenAPI and Lighthouse placeholders are outside this required set and
retain their later packet gates. The PR's current check rollup is the verification
source for any later head. P02d-1 is complete and the PR is ready for the maintainer's
detailed review.

The PR's subsequent automated review identified three corpus corrections: a remaining
CODEOWNERS reference in the extension overview, omitted P02d-1 expansion metadata on
the migration-order rule, and an ambiguous Phase 04 revision paragraph. The first two
now match their current owners. The revision clarification records the shipped
Draft-only body guard and links the lifecycle question that ADR-0043 § 6 already leaves
with Phase 04; it does not introduce a successor-draft/history-storage design or change
the accepted versioned identity. These documentation corrections preserve P02d-1's
implementation and accepted scope.
