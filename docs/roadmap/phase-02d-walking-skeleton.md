# Phase 02d: Two-Tenant Walking Skeleton

> **Status (2026-10-03).** Phase 02d **in progress**. The kickoff, `P02d-0`, ships this
> plan — the inherited baseline, the packet table, the decision register, criteria that
> name their evidence, and the corrections to the documents that contradicted the phase
> — and no code. Every later packet opens with its decision pass and updates its own
> row, linking its delivery record.
>
> | Packet | Title | State |
> |---|---|---|
> | P02d-0 | Kickoff | ✅ this plan |
> | P02d-1 | Education schema and database-level isolation | ✅ complete and merged — 2026-09-14; [merge closeout](#merge-and-closeout-2026-09-14) |
> | P02d-2 | Writers and seed | ✅ complete and merged — 2026-10-02; [merge closeout](#p02d-2-merge-and-closeout-2026-10-02) |
> | P02d-3 | Read internals | ✅ complete and merged — 2026-10-03; [merge closeout](#p02d-3-merge-and-closeout-2026-10-03) |
> | P02d-4 | Public read API and contract checks | decision pass Accepted — 2026-10-03; implementation starting — [package](#p02d-4-decision-package-2026-10-03) |
> | P02d-5 | Server-rendering path | not started |
> | P02d-6 | Public renderer | not started |
> | P02d-7 | Demo, full-stack CI and exit | not started |

**Acceptance update — 2026-10-02.** P02d-1 remains merged. The maintainer accepted
ADR-0050/0051 and the [P02d-2 package](#p02d-2-decision-package-2026-10-02), including
its four implementation steps. Required decision bookkeeping is complete; this
update delivers no code. Implementation has not started and waits at the maintainer's
explicit request. ADR-0049 and Phase 09a remain Proposed.

**Implementation resumed — 2026-10-02.** The maintainer's implementation request
revokes the acceptance-time wait. [Delivery](#p02d-2-implementation-delivery-2026-10-02)
records all four completed implementation steps and their two independent review
rounds. At that pre-merge milestone, P02d-2 was ready for PR review, its merge
closeout was pending, and P02d-3 was next.

**Merge complete — 2026-10-02.** The acceptance and implementation notes above
record the pre-merge milestones. P02d-2 is now closed through
[PR #23](https://github.com/HodeTech/LearnStack/pull/23); its
[merge closeout](#p02d-2-merge-and-closeout-2026-10-02) records verification.
At that closeout, Phase 02d remained in progress and P02d-3 was next, with its
decision pass still open.


**P02d-3 complete — 2026-10-02, unmerged.** The preceding notes record earlier
milestones. The [decision package](#p02d-3-decision-package-2026-10-02) is Accepted;
all three implementation steps and both fresh review rounds per step are complete.
The [delivery record](#delivery-record-p02d-3) records code, verified fixes and
2637 passing tests. The packet is ready for maintainer PR review. P02d-4 is next:
its public-read decision pass and contracts are not started.

**Merge complete — 2026-10-03.** The preceding P02d-3 note is its pre-merge
milestone. P02d-3 is now closed through
[PR #24](https://github.com/HodeTech/LearnStack/pull/24); its
[merge closeout](#p02d-3-merge-and-closeout-2026-10-03) records the final correction,
verification and merge. P02d-4's public-read decision pass is next; no public
endpoint or browser implementation is claimed by this closeout.

**P02d-4 preparation — 2026-10-03.** The
[decision package](#p02d-4-decision-package-2026-10-03) and
[ADR-0052](../decisions/0052-anonymous-public-read-boundary.md) are Proposed after
document and code review. No gate is Accepted by this preparation and no code has
been written. The maintainer reviews one concrete package before implementation.

**P02d-4 Accepted — 2026-10-03.** The maintainer approved ADR-0052 and the
[decision package](#p02d-4-decision-package-2026-10-03), including read-only setup,
host provenance, public contracts and the sixth required OpenAPI check. Decision
records and carrier updates precede implementation; the delivery record will own
actual code, reviews and checks. Development remains the working branch.

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

This is the Phase 02a entry baseline. The dated delivery records below own current
implementation; P02d-2 now supplies the locale, branding and Education writers.

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

P02d-1 implements Education's roots, translations, migration and isolation.
P02d-2 implements its six commands and seeded data. What remains unimplemented:
public Education endpoints; every renderer component; the server SDK transport;
any trusted-hop configuration; any Lighthouse tooling; and a `make demo` target.

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
| G3 | Which of `courses` and `lessons` carry a publication state, with which values and transitions, and what does "published" mean to an anonymous reader — publicly readable, or only listed? Which command sets it, which states does the seed write, and may a course with no lessons, or untranslated in an enabled locale, be published? And for any transition or deletion this phase does not ship (unpublishing a course or lesson, deleting either), which phase owns it? | `courses` `draft` / `published`, meaning publicly readable (Phase 05 adds catalog visibility as its own concept); lessons carry a state and show only when both are published; draft → published only; an empty course may be published, since publish validation is Phase 05's. One review leaned "listed in the catalog" | Contract: a new ADR, or a dated phase-doc statement recording why a two-value, one-transition column is not the state machine Decision Timing reserves for a decision record; no Accepted ADR decides publication ([ADR-0018](../decisions/0018-tenant-driven-customization-model.md) reserves the lifecycle to LearnStack). Detail: the `CHECK` ([Database Standards § Constraints](../standards/05-database.md#constraints)), the Education spec's state diagram, the publish row [Audit Coverage Standards](../standards/18-audit-coverage.md) makes MUST | P02d-1 (column presence and value set), P02d-2 (publishing commands and seeded states; transition contract closed in P02d-1) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): commands and seed states; independent lifecycle retained. Anonymous access superseded by [ADR-0050 / dated G3 record](#g3-supersession-2026-10-02); original P02d-1 answer retained as history |
| G4 | Where does a lesson body's binding to the content-type key and `schema_version` it was validated against live — on `lessons` or on each translation row — and where does the body live: its column, type, per-locale placement, and how non-translatable field values are carried? May a constraint cross into the Customization chain? What becomes of Localization Standards' `isLocalized` marker, which nothing implements? | A value pin `(content_type_key, schema_version)` on `lessons`, as Phase 04 plans for `ContentEntry`, with no foreign key; the field document per locale in `lesson_translations`, every locale validated against the one pin, duplicated non-translatable values accepted until Phase 05's lesson items retire them; the marker removed or given its introducing phase | Detail: this document's § Localization schema, the Education spec, [Localization Standards § Pattern A](../standards/08-localization.md#pattern-a--side-translation-table-default-for-content-shaped-entities) in the same diff. Contract: a dated ADR-0043 amendment if a localization keyword enters the schema profile; its own ADR or amendment if a cross-chain foreign key is chosen, as ADR-0044 § 9 did, with Phase 04 and [Database Standards § Migrations](../standards/05-database.md#migrations) in the same diff | P02d-1 (the first `lessons` and `lesson_translations` DDL; a pin added later needs a backfill that guesses between two Active content types) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G4 |
| G5 | Before Phase 05's `Level` exists, how does a course or lesson carry the level band criterion 1 shows? Does the reference pin a taxonomy revision, how is a band validated on write, and what renders when the resolved revision no longer declares the stored band? | The reviews split: (a) a nullable, non-translatable `(taxonomy_key, band_key)` on `courses`, resolved against the live revision, because the criterion names the catalog; (b) a revision-pinned triple; (c) no column, the band shown through a lesson-page `x-taxonomy` field, with the criterion reworded. No shipped path validates a band value under any of them | Detail: a phase-doc statement, the Education spec, and a Phase 05 inherited row if a reference ships. Contract: a dated ADR-0010 amendment or a new ADR if an Education table takes a foreign key into Customization | P02d-1 (whether and where a column exists), P02d-2 (validation, seeded references), P02d-4 and P02d-6 (the unresolved-band state) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): column; [Accepted — 2026-10-02](#p02d-2-accepted-answers): validation and seeded references; [Accepted — 2026-10-03](#p02d-4-accepted-answers): unresolved public-band response. Renderer behavior remains P02d-6 |
| G6 | Locale identity on the content path. (a) What spelling and column type do the satellites' `locale` columns store, and which rule replaces Localization Standards' "Lowercase", which the shipped `LocaleTag` does not follow? (b) Is the `locale` parameter canonicalized before lookup, the membership check and every cache or cursor key, or is a non-canonical spelling refused? (c) What does a non-canonical `/{locale}/` segment get? | (a) `LocaleTag`'s canonical case (`tr-TR`, `zh-Hans`) in `varchar(35)`, as `tenant_locales` stores it — [ADR-0018](../decisions/0018-tenant-driven-customization-model.md)'s 2026-09-04 amendment already makes case variants one locale; (b) well-formedness, then canonicalization, then lookup; (c) a redirect to the canonical segment, decided with G36 | Detail: [Localization Standards § Locale Codes](../standards/08-localization.md#locale-codes) and the Database Standards satellite fence in the same diff. No ADR: ADR-0008 states no casing rule | P02d-1 (a: the first stored rows), P02d-4 (b: validators, cursor binding), P02d-5 (c, with G36) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): (a); [Accepted — 2026-10-03](#p02d-4-accepted-answers): (b); URL-segment redirects (c) remain P02d-5 |
| G7 | Organization write scope. (1) Does a lesson carry its course's organization scope? (2) What forces a satellite's — and a lesson's — mirrored `organization_id` to equal its parent's at insert: writer derivation alone, or that plus a database backstop, and which? (3) May an organization-scoped session `INSERT` a tenant-wide row through the `organization_id IS NULL` arm of `WITH CHECK`, which [ADR-0003](../decisions/0003-tenant-isolation-defense-in-depth.md)'s Amendment 5 and Database Standards say it cannot and which it can at `HEAD`? | (1) Identical scope for a course, its lessons and every translation. (2) Writers derive the child's organization from the authorised parent; the reviews split on the backstop — a stored generated scope column with an organization-inclusive composite key, which structural sweeps can see, or a `BEFORE INSERT` trigger reading the parent under the caller's policies — and one review requires database enforcement. A nullable three-column key is already excluded, because `MATCH SIMPLE` skips the check. (3) Tighten, after the pass confirms no audit writer composes a null-organization row under an announced organization | Contract: one dated ADR-0003 amendment for (2) and (3), with an ADR-0041 erratum beside any sentence the pass finds false when it entered the record; the template replaced in place in [Database Standards](../standards/05-database.md) with its disclosure; forward migrations for `tenant_settings` and `audit_log` if (3) tightens. Detail: [Database Standards § Translation satellite tables](../standards/05-database.md#translation-satellite-tables); a catalogue row with a planted offender if a database mechanism is chosen | P02d-1 (policy SQL, the generated column or trigger, aggregate factories), P02d-2 (child derivation in the commands) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): database controls and factory derivation; [Accepted — 2026-10-02](#p02d-2-accepted-answers): command derivation |
| G8 | Which structural guards does the Education chain register, so its tables cannot regress with the suite green: every foreign key between two tables carrying `tenant_id` includes it; every table carrying `organization_id` has the immutability trigger (and how `audit_log`'s append-only guard counts); the Pattern A rule, which would make [ADR-0008](../decisions/0008-localization-schema.md)'s "the migration linter rejects ad-hoc per-locale columns" true? And how does `fn_organization_id_immutable` — which reads `OLD.id` and is declared only in the Tenancy chain — serve satellites that have no `id`? | Three rows, each with a planted-offender companion; the function replaced by a Tenancy-chain migration that reports `OLD.organization_id` or reads the row key through `to_jsonb(OLD)`, which (as in the audit append-only guard's row comparison) never names a column the table may lack, with the cross-chain dependency recorded under Database Standards § Migrations | Detail: Standards 21 rows Registered and Implemented in the packet; the Database Standards immutability fence and § Migrations; `MigrationRollbackTests`. Contract, only if ADR-0008's sentence is left untrue: an ADR-0041 erratum if it was false when entered, otherwise a dated amendment | P02d-1 (a guard shipped with its first new subject is the only point its companion is written against real tables) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G8 |
| G9 | Education schema detail: the content slug's character shape, normalization, width and database backstop — including whether a GUID-shaped slug is refused, which G26's shared-slot path needs; whether an Education table holds a foreign key into `tenants`, `organizations` or `tenant_locales`; and each runtime role's privileges on the four tables | `UrlSlug`'s shape with its own width constant and a `ck_<satellite>_slug_format` backstop, since restrictive now is the reversible choice (ASCII-only slugs exclude native-script URLs, a product choice); no foreign key into Tenancy; `learnstack_app` `SELECT, INSERT` plus exactly what G11's commands need, `learnstack_platform` `SELECT` | Detail: Localization Standards § Pattern A for the shape; the Database Standards satellite fence and [§ GRANT matrix](../standards/05-database.md#grant-matrix); § Migrations only if a cross-chain key is chosen | P02d-1 (the creating migration writes the `CHECK` and the grants; the grants couple with G11) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): G9 |
| G10 | What is the catalog's default order and tie-breaker, and what is the cursor it mints: its payload and version; what it binds (tenant, organization, locale, sort, filters, endpoint); its integrity (none, a MAC with a key version, or server-side state); its direction; what happens when a row changes between pages; which list parameters the endpoint binds; where it is decoded; whether the codec is this endpoint's or the kernel's; and which cursor classes answer `400`? | The reviews split between a keyless versioned payload with a binding fingerprint, decoded at binding so a garbage cursor opens no transaction, and an HMAC-authenticated cursor with key rotation. Both keep tenant and organization out of the cursor, and bind `CursorPaginationRequest` rather than `ListRequest`, whose `q` is Phase 04's search | Contract: a phase-doc statement if the codec is endpoint-local and keyless; a new ADR if it becomes a kernel rule later lists follow, or a MAC adds a secret and a rotation posture. Detail: [API Standards § Pagination](../standards/04-api-design.md#pagination), which drops "Nothing validates its *shape* yet"; Standards 21 rows | P02d-1 (the order part: an ordering column, publication timestamp or collation), P02d-4 (the codec part) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): order; [Accepted — 2026-10-03](#p02d-4-accepted-answers): resource-local cursor codec |
| G11 | The write surface the seed needs. Which Education commands write courses, lessons and their translations; is a translation written separately from create; is publishing its own command; which command reports a slug collision as `business_rule_violation` rather than a raw unique violation, and does Localization Standards' "from the publish command" still hold? What shape do the Tenancy commands raising `tenancy.locale.write` and `tenancy.setting.write` take? How are the non-baseline writes classified, and how does a re-run converge? | Create course, write course translation, add lesson, write lesson translation, publish course (MUST); one locale command over `Tenant.AddLocale` and `SetDefaultLocale`; a create-or-update setting command keyed on context scope and key; ordering taxonomy → content type → course → lessons; idempotent by conflict, with an ownership check per act and a second-run test. None has a route | Contract: a phase-doc statement plus the Education spec (README write sequence, `audit.md`, `permissions.md` as a forward declaration on [the Tenancy precedent](../modules/tenancy/permissions.md)). Detail: catalogue sources, the Tenancy `audit.md` and `permissions.md`, Localization Standards § Pattern A if the collision sentence changes. An ADR only if a handler must write two roots | P02d-2 (commands, handlers, catalogue sources, seeder acts) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): commands, failures, audit classifications and convergence |
| G12 | Through which `Customization.Application.Contracts` surface does an Education write obtain the schema a body is validated against — exact `(key, schema_version)` including Deprecated revisions, or a key that binds the Active one — and is it an interface or a MediatR query, classified how? Which revisions may a writer bind, and what refusal answers an absent, cross-tenant or ineligible one? On the read side: what the cache keys on, whether the lesson response carries the binding or resolved field descriptors, and what the API and the page show when a binding cannot be resolved | One exact-revision query, Deprecated included, never falling back to Active; only Active revisions bindable for new writes, since a Draft's body can still change; absent and cross-tenant refused indistinguishably as `validation_failed` naming the binding; resolved descriptors in the response; an unresolvable binding shows a bounded placeholder with a warning log, never a `500` and never another revision's fields ([ADR-0013](../decisions/0013-page-block-schema-versioning.md)'s placeholder rule) | Detail: the Customization spec's contract and § Primary read flow, the Education spec's invariants, a phase-doc statement. No ADR: ADR-0010 settles the mechanism. A dated ADR-0013 amendment only if the unresolvable outcome departs from the placeholder rule | P02d-2 (the contract and write eligibility: the lesson writer is its first caller), P02d-3 (the cache key), P02d-4 (descriptors, the unresolvable outcome), P02d-6 (the page state) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): contract and write eligibility. [Accepted — 2026-10-02](#p02d-3-decision-package-2026-10-02): cache key; [Accepted — 2026-10-03](#p02d-4-accepted-answers): public response; page behavior remains P02d-6 |
| G13 | May an Education translation be written for a locale absent from, or disabled in, `tenant_locales`, and how is membership checked across the module boundary? Does a read resolve under a disabled locale? What does a tenant with no locale rows serve — [Localization § Tenant Locale Configuration](../architecture/12-localization.md#tenant-locale-configuration) promises platform `en`, and nothing implements it? Does a platform registry bound the enabled set, as Localization Standards names one in a namespace that does not exist? What happens to translations when `RemoveLocale` runs? | A Tenancy application contract checks membership on write; a read resolves only an enabled locale, checked once per request; no cross-chain foreign key; no platform registry in this phase; a tenant with no locale rows serves nothing until it has one | Contract: a phase-doc statement over ADR-0010's application-contract mechanism. Detail: the Tenancy and Education specs; Localization architecture and Localization Standards § Locale Model reconciled in the same diff; Database Standards § Migrations only if a key is chosen | P02d-2 (the translation command's check and the locale command the seed uses; the read half is written to the same answer in P02d-4) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): write eligibility, no platform registry and no implicit no-row locale; public reader implementation remains P02d-4 |
| G14 | Seed inventory. At what scope is each seeded row class written — courses, lessons, translations, branding settings — and from what seeder context, given that `SeedTenantContext` requires an organization? Where do the rows the criteria need live — a sibling-organization course, an organization-scoped course on the tenant host, a `(locale, slug)` held in both tenants, draft and wrong-course rows, more courses than one catalog page, a disabled locale holding translations — `make seed` or test-owned data? Which key the yoga taxonomy uses, which tenant is bilingual, what state do the built-in `card` / `plain` keep, which record holds it all, and how do the Packet 7 fixture's raw settings rows coexist with seeded ones? | English content tenant-wide; the yoga studio gets a tenant-wide, a Studio One and a Studio Two course; a seed context that announces no organization; branding tenant-wide; rows in the seed with `SeedData` as the record; built-ins stay Active and are never selected implicitly; expectations recomputed as enumerated sets. An English organization-scoped row is still needed for the tenant-host criterion, seeded or test-owned — the demo database's contents are the owner's preference | Detail: a phase-doc statement, the `SeedData` remarks, the `seed-tenant` skill, the writers delivery record. No ADR: [Security Standards § Forbidden](../standards/11-security.md#forbidden) already makes scope come from context | P02d-2 (seeder steps, the seed-context constructor, `SeedData`, `SeederTests`; moving placement later rewrites the seed and every request-level case) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): inventory, ownership and test-owned controls |
| G15 | `SeedRunner` calls `IUnitOfWork.SetTenantContextAsync` on its own transaction, and neither [ADR-0040](../decisions/0040-ambient-unit-of-work.md)'s closed setter set nor [Security Standards § The out-of-band setters](../standards/11-security.md#the-out-of-band-setters) lists it. Is that method's caller set mechanically closed, and is the seeder's call reconciled by routing its ownership check through `ISender`, or by admitting the seeder? | Route the ownership check through `ISender`, and add a source scan that admits `TransactionBehavior` (and Phase 02b's transport) with a planted offender | Contract: a dated ADR-0040 amendment plus a setters-table row only if the seeder is admitted. Detail: a Standards 21 source-scan row with its companion | P02d-2 (the Education seed acts reach the ownership check's refusal arm today) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): contextual verification and Registered caller fence |
| G16 | The branding token contract. (a) Where does the settings key registry live, what does a descriptor carry, and does `tenancy.setting.write` refuse keys outside it? (b) Which branding keys exist — per-token keys or one theme document — and is a layout option among them? (c) What value does each accept, fonts and logos included, and what happens to a stored value that fails it? (d) Does a failed contrast check refuse the write or record a warning — [Accessibility Standards § Color and Contrast](../standards/16-accessibility.md#color-and-contrast) says a Studio warning? (e) What does an organization-scoped branding row do here — refused, ignored or applied? (f) Which tokens may leave an anonymous response? (g) Does `tenancy.white_label_branding` — which reads true under `NullEntitlementProvider`, whose projection grants every registered feature, falls back to its catalog default `false` from a projection that omits it, and which the Hub's Starter plan sets false — govern applying theme tokens or only removing LearnStack attribution? | (a) a registry beside `FeatureKeys` and `LimitKeys`, as `Tenant.SetFeatureFlag` already refuses unregistered keys; (b) per-token keys, at most one enumerated layout option or none; (c) `#rrggbb` colours, one font key from a closed self-hosted set, no remote logo; (d) refuse; (e) tenant-wide only, keeping Phase 06's override and ADR-0017's `OrganizationBranding` true; (f) a closed projection of publicly readable keys; (g) not gated — tokens are baseline presentation, and the key's meaning is agreed with the Hub. That token values are tenant settings is settled by [Frontend Architecture Standards § Tenant Branding](../standards/07-frontend-architecture.md#tenant-branding) | Contract: a phase-doc statement plus Frontend Architecture Standards § Tenant Branding; a new ADR if the registry becomes an admission rule for every `tenant_settings` key; a dated ADR-0017 amendment if (e) applies overrides; Accessibility Standards if (d) replaces the warning. Detail: the Tenancy spec and permission matrix, [Frontend Architecture § Theming](../architecture/14-frontend-architecture.md#theming), the `FeatureKeys` descriptor with a matching note in the Hub repository for (g) | P02d-2 (a–e: validation and the seeded keys, which Phase 06's editor later edits), P02d-4 (f, g: the anonymous projection the OpenAPI baseline freezes), P02d-6 (g: whether rendering consults the flag) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): (a–e); [Accepted — 2026-10-03](#p02d-4-accepted-answers): public projection and attribution (f/g); injection remains G42/P02d-6 |
| G17 | Does `TenantSetting.Value` carry `[PiiSensitive]`? [Phase 03](phase-03-identity-admin.md) sequences the decision before the first command writing `tenant_settings`, and this phase ships that command | Not marked, provided `tenancy.setting.write` admits only G16's closed key set, so the answer cannot stretch to keys a tenant invents; modelling a sensitive part as its own property stays open to Phase 03 | Contract: a dated phase-doc statement, reflected in `TenantSetting.cs`, the Tenancy spec and `audit.md`. Whole-value redaction of `jsonb` is settled by [ADR-0044](../decisions/0044-audit-write-path.md) Amendment 4 § 1 | P02d-2 (the first MUST-class settings audit row is written by the seed, and rows cannot be redacted retroactively); closes with G16 (a) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): generic whole-value PII redaction before the writer |
| G18 | How is a tenant content type presented? `json_schema` is `jsonb`, which keeps no key order, and the schema profile collects only `x-renderer`, `x-taxonomy` and `x-language`. How are field order, a label per enabled locale and a composite's field roles carried; which registered composite draws a lesson for each seeded type; which primitives does this phase implement, and does `markdown` render; how do types with no primitive row (`integer`, `number`, `boolean`, enums) map; may a rendered type declare a field outside the subset; and is a presentation entry naming a missing property refused at save? | A LearnStack extension — `x-order` and `x-label`, or one ordered `x-fields` list — carrying Pattern B labels, resolved at write like `x-taxonomy`; one composite already in both registries; the reviews split on the subset — `text`, `list` and `link`, with `markdown` without raw HTML, or a placeholder until Phase 05's sanitiser; the seed uses only the subset | Contract: a dated ADR-0043 amendment for a keyword or a save-time refusal; a dated ADR-0018 amendment for a presentation column; a phase-doc statement for `title` plus `required`, which cannot carry two locales. Detail: [Tenant Customization Model § 2](../architecture/32-tenant-customization-model.md) and § 8.1, the Customization spec, the profile's extension and reference-graph skip lists, `composites.ts` | P02d-2 (the seed publishes both content types as `schema_version` 1 with their renderer keys and field kinds; a later answer needs successor revisions) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): ADR-0051 profile and first-render subset. Component placement/fallback remains G41 |
| G19 | URL and markup policy for tenant-authored values on an anonymous page: which schemes (`https` only, or `http` too), credentials and `target`, which media origins, whether the rule is enforced on write — in the Education command, or as a validation gate Phase 04's entries share — whether the public API filters too, and whether URLs inside markdown fall under it. The write-time check constrains structure, not schemes: `format: uri` admits `javascript:` and `data:` | The reviews split on `http`; all refuse `javascript:`, dangerous `data:` and credentials; checked on write by a LearnStack rule and again on render; no third-party media in the seed | Detail: one home for the scheme list — [Security Standards § XSS & Output Encoding](../standards/11-security.md#xss--output-encoding) or [Frontend Architecture Standards § Security](../standards/07-frontend-architecture.md#security), not both; the Education spec's write rules; Tenant Customization Model § 8.1 if checked on write. Contract: a dated ADR-0043 amendment if it becomes a shared validation gate | P02d-2 (the lesson command's validation and the seed values; the render-time check reuses the answer) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): no active sink in the seeded text profile; future URL/markup contracts precede Phase 04/05 sinks |
| G20 | What mechanically backs "no production code branches on which tenant it serves"? The shipped domain-term scan strips literals and exempts seed data. (a) The mechanism and its literal source; (b) its subjects, matching and the platform built-ins; (c) its exemptions, including development hosts in frontend or infrastructure configuration; (d) whether a ban on production references to `LearnStack.Tools.Seeder` and a behavioural same-code, different-data test accompany it | A Standards 21 sibling row scanning production backend and `frontend/` sources, comments stripped, for exact identity literals read from `SeedData` (slugs, ids, hosts, display names, customization keys), built-ins excluded, with planted offenders; plus the behavioural test. The exemption policy is the owner's judgement | Detail: a Standards 21 row Registered in the first pass that uses it and Implemented before exit; a phase-doc statement in § Genericity proof. No ADR | P02d-2 (a: every seed literal lives where the source reads it), P02d-5 (c: the first host outside `SeedData`), P02d-6 (b: frontend subjects), P02d-7 (Implemented and required) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): (a) SeedData literal source and Registered guard. Subjects, exemptions and behavioral proof remain open for P02d-5/6/7 |
| G21 | Does the anonymous public path set any cookie — the [Frontend Architecture Standards § Tenant Resolution](../standards/07-frontend-architecture.md#tenant-resolution) flowchart sets them — and may a public page load any cross-origin subresource, such as the CDN-hosted logo and font assets Frontend Architecture describes? | No cookies, since the locale is already in the path and a locale-less request redirects ([Localization Standards § URL Strategy](../standards/08-localization.md#url-strategy)); same-origin subresources only; both asserted by a check. Whether tenant branding may point visitors' browsers at third-party hosts is a data-protection choice for the owner | Detail: a phase-doc statement; the Standards 07 flowchart and Frontend Architecture § Theming reconciled in the deciding pass | P02d-2 (subresources, if G16 admits a URL-valued token), P02d-5 (cookies: the middleware replacement is the first code that could set one) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): subresources. Cookies remain open for P02d-5 |
| G22 | How does the customization definition projection load and stay correct? In the request's ambient transaction, or as a ninth out-of-band tenant-context setter (ADR-0040's set is closed at eight)? In what order are the generation and the rows read; what does an absent generation row mean; how is a cache filled inside a transaction that bumped and rolled back kept unreachable, when the bump is an upsert increment that can reissue a number; what does an absent definition set return; which families are registered, and how does the adapter's exact-tuple `cache.name` mapping match generation-embedded names; what do the TTLs bound; and is the contract batched so a public read issues a bounded number of statements? | Load in the ambient transaction; read the generation first, then the rows; fill only from non-bumping transactions; treat cache faults as misses; restate the module's cache-hit budget; a batched contract, with statement-count assertions cold and warm | Contract: the Customization spec § Primary read flow and a [Tenant Customization Model § 8.2](../architecture/32-tenant-customization-model.md#82-cache-strategy) statement on how a request learns the generation; a dated ADR-0040 amendment and a setters row only if the loader is out-of-band. Detail: the [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md) cache table, the `cache.name` mapping, the Observability Standards metrics family list | P02d-3 | [Accepted — 2026-10-02](#p02d-3-decision-package-2026-10-02) |
| G23 | The typed settings accessor and its freshness. With no `learnstack.tenancy.settings` event until Phase 02b and the seed writing from its own process, what bounds staleness: a TTL with a stated bound, a writer-coupled Tenancy settings generation counter, or no settings cache here? What are the accessor's name and glossary headword; how is a cached read keyed so tenant-wide and organization rows never cross organizations — a settings read depends on `app.organization_id` today, and the policy's tenant-scope read gains a carrier in Phase 03; and does its loader run in the ambient transaction? | The reviews split on freshness — a TTL bound until 02b, a counter, or no cache. For keys: tenant-wide rows loaded with an explicit `organization_id IS NULL` predicate under `CacheKey.ForTenant`, each organization's overrides under `CacheKey.ForOrganization`, merged in memory; an ambient loader. The documented tenant-only key is rejected, because it would serve one organization's overrides to another | Detail: if settings are cached, the Infrastructure Stack Standards cheat-sheet rows and `cache.name` mapping; the Tenancy spec's event row and budget; a glossary headword. Contract only for a counter (the Tenancy spec, Database Standards § Table classes and § GRANT matrix) or an out-of-band loader (an ADR-0040 amendment) | P02d-2 (a counter is bumped inside the setting command's transaction), P02d-3 (name, keys, loader) | [Accepted — 2026-10-02](#p02d-2-accepted-answers): no settings cache in P02d-2/3. [Accepted — 2026-10-02](#p02d-3-decision-package-2026-10-02): typed ambient accessor/scoped merge |
| G24 | Display fallback. Which document owns the chain — [Localization § Fallback Rules](../architecture/12-localization.md#fallback-rules) or [Localization Standards § Locale Model](../standards/08-localization.md#locale-model), which state different chains, while the shipped `LocalizedText.Resolve` narrows one subtag at a time and ends at the first authored value? What is the terminal state of a nullable Pattern A field and of a Pattern B label? Does a response say which locale a fallback value resolved in, so the page can mark its language (WCAG 3.1.2)? | Localization architecture owns the chain and Localization Standards links it, both recording the shipped narrowing and the first-authored terminal for labels; a nullable Pattern A field renders absent; each fallback-capable field reports its resolved locale | Detail: Localization Standards § Locale Model linking its owner, reconciled with `LocalizedText` in the same diff; the Customization contract's signature; the response schema under G26. No ADR | P02d-3 (the first caller that passes a fallback chain), P02d-4 (response fields) | [Accepted — 2026-10-02](#p02d-3-decision-package-2026-10-02): internal fallback; [Accepted — 2026-10-03](#p02d-4-accepted-answers): public response locale fields; renderer language attributes remain P02d-6 |
| G25 | Site data and the page set. How does the renderer get the per-host data none of the Education reads returns — enabled and default locales, branding tokens, taxonomy display values, content-type field lists: fields embedded in the course reads (which cannot supply a default locale before a locale is known), a separate `[PublicSurface]` read resolved from the effective host, or the edge host lookup [Frontend Architecture Standards § Tenant Resolution](../standards/07-frontend-architecture.md#tenant-resolution) and [Infrastructure Stack Standards § Host → Tenant Resolution](../standards/20-infrastructure-stack.md#host--tenant-resolution) prescribe today, which must then state the effective host over the hop? Does the frontend ever hold a tenant or organization id? And which `(public)` pages ship — catalog, course with ordered lesson links and lesson, or two pages with bounded lesson links in the catalog response? | One `[PublicSurface]` site-data read with no host parameter, returning a closed projection and no ids, and three pages, which gives the course-detail read a consumer; one review keeps two pages with an explicit catalog outline. The first two options change what two Active standards prescribe | Contract: a phase-doc statement in § Read API and § Public renderer; for the first two options, edits to the two standards named, with an ADR if the pass judges the change non-trivial (no ADR carries the edge-lookup rule). Detail: the API Standards § Public surface rows; the Frontend Architecture sketch, sequence diagram and cache rows; the Localization architecture's edge locale sentence; the glossary; Phase 06 § What Phase 02d already shipped; Phase 05's inherited row if the course-detail read changes | P02d-4 (the endpoint set and DTOs the OpenAPI baseline freezes; a two-page answer changes the catalog response) | [Accepted — 2026-10-03](#p02d-4-accepted-answers): bootstrap and page set; page implementation remains P02d-6 |
| G26 | The v1 public read contract. The path shape beside Phase 05's authoring `/courses/{id}` — a shared slot, a distinct public prefix, or `/courses/by-slug/{slug}`; each response as an allow-list and what it never carries; the embedded lesson list's fields, order and bound, and whether an empty list is valid; per-locale alternates; how enums and envelopes stay additive; and which Problem Details responses each operation documents, given that no non-idempotent operation documents any today and a baseline of `200`s cannot see a status change | Fields limited to what the pages render; object envelopes, extensible enums, a deny-list contract test (`tenantId`, `organizationId`, `createdBy`, `updatedBy`, `deletedAt`, `rowVersion`, `slugKey`); the embedded list carries title, slug and order under a cap; `alternates` for enabled, translated locales; one shared transformer declaring each operation's statuses as `application/problem+json`. No review settled the path | Contract: a phase-doc statement recorded before the breaking-change check stores its baseline. Detail: the OpenAPI snapshot; [API Standards § URL Structure](../standards/04-api-design.md#url-structure) for a prefix class, § Pagination for an embedded list, § OpenAPI; the gateway's public-band row. [ADR-0024](../decisions/0024-api-versioning-policy.md) settles that later additions are non-breaking | P02d-1 (whether the slug grammar must refuse GUID shapes, with G9), P02d-4 (route templates, records, snapshot) | [Accepted — 2026-09-14](#p02d-1-accepted-answers): slug grammar; [Accepted — 2026-10-03](#p02d-4-accepted-answers): route and response contracts |
| G27 | The cache posture of public reads. What directive do anonymous responses carry — the `200`s, the Problem Details `400`s and `404`s, the tenancy edge's unmapped-host `404` — what freshness do a newly published or unpublished course and a not-found have, and do anonymous reads emit an `ETag` and honour `If-None-Match`? [API Standards § Optimistic Concurrency](../standards/04-api-design.md#optimistic-concurrency) says mutable resources expose an `ETag`, and [ADR-0039](../decisions/0039-optimistic-concurrency-token.md) fixes one derivation, which a composite read cannot use without publishing `row_version` | An explicit `Cache-Control: no-store`, asserted by a test, and no `ETag` on anonymous reads — a response without explicit freshness may be cached heuristically by a shared cache. One review proposed no directive, stated | Contract: a phase-doc statement. Detail: API Standards — the directive, and a § Optimistic Concurrency sentence on anonymous read contracts, owed under either answer. A dated ADR-0039 amendment if a body-hash validator ships; [Performance Standards § Caching](../standards/15-performance.md#caching) if the answer caches | P02d-4 (the header-setting code and the headers the snapshot documents) | [Accepted — 2026-10-03](#p02d-4-accepted-answers): no-store without response validators |
| G28 | Public-surface controls. (a) What audit class do `[PublicSurface]` requests register, and does a rule make `Off` the only permitted one? (b) `GET` only, or `GET` and `HEAD`, and what does the catalogue's permitted-methods leg compare a row against? (c) What mechanically stops a marked request from writing — a `READ ONLY` unit of work, a structural scan, or both? (d) What control beyond review keeps a controller dispatching only through `ISender` — a controller taking a module `DbContext` fails loudly, SQL on `IUnitOfWork.Connection` reads zero rows, and code that announces the tenant itself reads real rows? | (a) `Off` for every marked type — a SHOULD or MAY class would make every anonymous `GET` a best-effort write a caller controls — with a sibling rule and companion; (b) one review `GET` only, one the standard's `GET` / `HEAD`; (c) a `READ ONLY` transaction for marked requests, which three shipped setters already open before announcing, and which refuses any in-transaction MUST write, so it is checked against (a); (d) a type-reference rule over controller bodies with a planted offender | Detail: the API Standards § Public surface rows; Standards 21 rows and companions, including the two legs of `PublicSurface_Marker_Set_Is_Enumerated` not yet implemented; an [Error Handling Standards § Controller Mapping](../standards/09-error-handling.md#controller-mapping--resultt--iactionresult) sentence for (d). Contract: a dated ADR-0040 amendment if the unit of work gains a read-only mode | P02d-4 (the first marked query's registration, method attributes and handler; if P02d-3 writes on the read path, (c) closes there) | [Accepted — 2026-10-03](#p02d-4-accepted-answers): Off, GET/HEAD, mandatory dispatch and read-only controls |
| G29 | Which rows do the anonymous reads serve, and what does every hidden row answer? The rule covers course state, lesson state (G3), the lesson's membership in the course its URL names — lesson slugs are unique per tenant, so a lesson resolves without its course segment unless the read checks — and soft deletion. Does every hidden cause (draft, deleted, wrong course, untranslated, other tenant, sibling organization, nonexistent) answer one `not_found` body with no per-cause detail, compared with `instance` and `correlationId` masked? | One eligibility rule used by every read; a lesson resolves only under its eligible parent, only when it belongs to it, only in the requested locale; lists show only eligible entries; deleted rows excluded now; one masked-equal body | Contract: a phase-doc statement whose single record is the Education spec. Detail: the failure constant. Settled and linked: a cross-tenant row is a `404` ([Security Standards § Error Messages](../standards/11-security.md#error-messages)), and an organization-scoped row is served only on its own organization's host ([Localization § Slugs and URLs](../architecture/12-localization.md#slugs-and-urls)) | P02d-4 (handlers, the failure constant, documented `404`s); the fixture rows close with G14 in P02d-2 if they live in the seed | [Accepted — 2026-10-03](#p02d-4-accepted-answers): explicit eligible reads and uniform masking |
| G30 | The locale error matrix and transport. On each read, what answers an empty, repeated, malformed, over-length, non-canonical, not-enabled or enabled-but-untranslated locale? Does a not-enabled locale answer the Active `unsupported_locale` `400` or the not-found body? Is `X-Locale`, which [Frontend Architecture Standards § Locale Resolution](../standards/07-frontend-architecture.md#locale-resolution) still names as the API carrier, withdrawn, so that locale reaches the API only as the query parameter? | Missing or malformed → `400` `validation_failed` naming `locale`; untranslated → an empty catalog page; the query parameter only. The reviews split on not-enabled — not-found, amending the Error Handling row, or the Active `400`; a uniform answer after the enabled check hides pre-launch rows under either | Detail: a phase-doc statement; the [Error Handling Standards](../standards/09-error-handling.md) table only if not-found; Standards 07 § Locale Resolution; the Frontend Architecture SDK sketch; the API Standards § Pagination example gains `locale` | P02d-4 (validators, OpenAPI parameters and responses), P02d-5 (the server SDK's header set) | [Accepted — 2026-10-03](#p02d-4-accepted-answers): query/error matrix; trusted server header transport remains P02d-5 |
| G31 | Contract checks and the SDK surface. The committed OpenAPI snapshot's path, and how the contract suite proves it equals the served document; how the base copy is read; how the first run behaves; which `oasdiff` version and fail level, and which ADR-0024 rows that level detects (a tightened validator or a changed status may be invisible to any diff); what is uploaded on failure. The drift gate's source — the committed snapshot through `LEARNSTACK_OPENAPI`, or a running API — and its job. Whether an activated deferred job keeps its `if: vars.ENABLE_*` condition, given that GitHub treats a skipped required job as passing, or loses it as the integration job's did. And what the SDK surface becomes when regeneration makes `paths` non-empty: a hand-written transport over `paths` or a typed client library, the fate of `createClientSdk`, and whether the package root keeps re-exporting the server entry | A committed snapshot the contract suite asserts equal, diffed against the base ref's copy with `oasdiff` pinned; drift generated from the snapshot in the required `frontend` job; the condition removed on activation; a thin hand-written transport, and no root re-export of the server entry | Detail: [Testing Standards § API Contract Tests](../standards/06-testing.md#api-contract-tests), the API Standards § OpenAPI links, `ci.yml`, [CONTRIBUTING § Branch protection](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main) (its activation procedure follows the answer), [Frontend Architecture Standards § SDK](../standards/07-frontend-architecture.md#sdk); a package pin with its licence verdict if a client library is chosen | P02d-4 (the first operation, its snapshot and assertion, the drift gate, and the regenerated types the factories must compile against) | [Accepted — 2026-10-03](#p02d-4-accepted-answers): snapshot, SDK, diff/drift and approved required-check activation |
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

### Pending Course Marketplace proposal

**Pending direction review — 2026-09-17.** At the maintainer's request, P02d-2's
decision pass first considers institution sites alongside a
[Course Marketplace](../decisions/0049-institution-sites-and-course-marketplace.md).
ADR-0049 is Proposed; this note accepts no gate and assigns no marketplace delivery
scope to P02d-2. Its
[acceptance checklist](../decisions/0049-institution-sites-and-course-marketplace.md#implementation-notes)
records the unresolved product, delivery and commercial decisions.

**Review follow-up — 2026-10-01.** The
[scoping companion](../architecture/34-course-marketplace-scoping.md) separates
live-product delivery, operations, privacy, regional topology and commerce recovery
from this packet. Public-only P02d-2 has no technical dependency on those capabilities;
the maintainer's planning hold remains until they resolve or release it. No review
recommendation selects protected authoring or accepts marketplace delivery scope.

**Historical boundary before exact acceptance.** G3's publication answer was accepted
on 2026-09-14 under ADR-0048; direction endorsement alone did not reopen it. The
maintainer's subsequent exact approval of ADR-0050 supplies the superseding contract
and [dated G3 supersession](#g3-supersession-2026-10-02) below. The original question,
accepted answer and delivery record remain intact.

**Maintainer endorsement — 2026-10-02.** The maintainer approved following the
recommendations and completing preparation. The hybrid direction and proposed
[Phase 09a](phase-09a-course-marketplace-pilot.md) are planning targets, not accepted
commerce contracts. The preparation hold is released. Protected authoring is prepared
in ADR-0050. The subsequent exact approval is recorded below; it does not accept
marketplace commerce. Future commerce feasibility does not block this packet.

### P02d-2 decision package (2026-10-02)

**Accepted — 2026-10-02.** The maintainer approved ADR-0050, ADR-0051 and the exact
packet statements, inventory and four-step plan below, then requested documentation
updates and a wait. Current gate cells and ADR lifecycle/index records are updated;
new source proofs were Registered at acceptance. The original G3 question,
P02d-1 accepted answer and delivery record are preserved. At acceptance, implementation
had not started; the [delivery record](#p02d-2-implementation-delivery-2026-10-02)
tracks the subsequently resumed work.

#### G3 supersession (2026-10-02)

ADR-0050 supersedes ADR-0048's public-only implication. Independent `draft → published`
states, one-root publication and no version snapshot remain unchanged. Explicit
Course content policy is inherited by lessons: `public` or `enrollment_required`.
Legacy rows backfill restricted; the migration was planned for P02d-2 and is now
recorded in the Step 1 delivery below.

This dated entry governs the current P02d-2/4/6 criteria wherever the inherited
packet text below describes publication as sufficient for anonymous body access:

- P02d-2 requires explicit policy, restricted legacy backfill, six separate Education
  commands and exact seed convergence; publication does not grant access.
- P02d-4 admits published marketing fields under either policy but exposes no
  restricted lesson inventory, count, descriptor, body or media URL. Direct hidden
  lesson lookup remains indistinguishable `not_found`; eligibility precedes public
  serialization, caching and conditional responses.
- P02d-5/6 preserve that public DTO boundary and render a bounded locked state with
  no invented payment or enrollment action. Credentials never unlock anonymous reads.
- Phases 04/05/07 own protected media, version/policy evolution and grant evaluation;
  unsafe live rollback is prohibited under ADR-0050's containment contract.

The accepted inventory and text-card subset below govern seed and rendering scope
where older packet planning differs. Remaining transport/cache/OpenAPI/renderer
gates stay open for their named packets; acceptance claims no implementation.

<a id="p02d-2-proposed-answers"></a>

#### P02d-2 accepted answers

| Gate part | Accepted answer and detail owner |
|---|---|
| G3: access, commands and seed states | [ADR-0050](../decisions/0050-publication-and-course-content-access.md) separates publication/access, backfills restricted policy and denies protected lesson inventory. Lifecycle stays independent draft → published. [Education writer plan](../modules/education/README.md#p02d-2-accepted-writer-contract) names six commands; seed states are explicit below |
| G5: level validation | New course binding requires the exact Active taxonomy revision and declared band; never resolve a live key. The [Customization contract](../modules/customization/README.md#p02d-2-accepted-exact-write-contract) owns eligible revision rules. Unresolved public labels remain G5's P02d-4/6 decision |
| G7: child derivation | Scope comes from trusted context and authorized parent; missing/cross/sibling parent is `not_found`, visible but unwritable parent scope is `resource_scope_violation`. No request tenant/organization authority; database guards remain unchanged |
| G11: writers, failures and convergence | Separate create, translation-add and publish commands per Education root; translation insertion reports known slug uniqueness as `business_rule_violation`. Tenancy locale and whole-theme commands write one root. The specs own matrices; seed never treats a generic lifecycle failure as success |
| G12: contract | Uncached Customization application interface returns immutable value DTOs for exact revisions; new binds Active, existing-pin writes Active/Deprecated. No foreign Domain/Infrastructure reference, FK, public marker or cache. Snapshot eligibility is at validation read, not a claim of Active-at-commit |
| G13: locale | Uncached Tenancy application contract requires canonical enabled membership. No platform locale registry; use existing LocaleTag grammar/canonicalization/35-character bound. No rows means no content locale, not implicit `en`; label fallback does not change URL/body eligibility. Removal/disable retains Education data and later reads recheck membership; the lifecycle commands belong to Phase 03 |
| G14: seed | Inventory and test-owned controls below; all literal identities and expected counts move into `SeedData` with implementation. Tenant-wide branding/content announce null organization. Built-in `card`/`plain` stay unchanged and Active |
| G15: setter fence | Ownership verification becomes contextual `ISender` queries, explicitly audit Off; no direct seeder transaction/context setter. [Caller fence implemented in Step 4](../standards/21-architecture-tests-catalogue.md#seeder_does_not_call_tenant_context_setters) has a planted-caller companion; no new ADR-0040 setter admission |
| G16(a–e): branding | One tenant-wide `branding.theme` document, four closed color fields, complete replacement and contrast refusal; exact version for replacement. Command-local registry preserves generic settings. Organization overrides refused; no fonts, logo, URL or layout setting in this packet |
| G17: PII | Mark generic `TenantSetting.Value` `[PiiSensitive]` before its writer, including whole JSON audit redaction. Public branding allowlisting is a separate boundary, not permission to expose generic settings |
| G18: presentation | [ADR-0051](../decisions/0051-ordered-text-card-presentation.md) extends ADR-0043 with optional strict root `x-fields`; seed opts into ordered localized plain-string cards. Legacy schemas remain valid, unchanged; no renderer-key or presentation-column change |
| G19: active content | The seeded profile has no active URL/markup sink; output is escaped text, never linkification or Markdown/HTML. ADR-0051 names first-sink owners; generic schema validation is not navigation/media authorization |
| G20: literal source | `SeedData` owns all demo identities, literals and expected inventory. The [Registered literal guard](../standards/21-architecture-tests-catalogue.md#production_code_does_not_branch_on_demo_tenant_literals) consumes that declaration when implemented; production subjects/exemptions and behavioral genericity proof stay with their later packet parts |
| G21: subresources | No new remote asset/font/logo or cross-origin subresource from seed/theme/text cards. Cookie behavior remains P02d-5; public output still needs later transport/render gates |
| G23: freshness bound | No settings cache in P02d-2/3: no seed-process staleness, generation migration or out-of-band loader. P02d-3 owns the ambient typed accessor and scoped merge; latency is a later measurement, not a passed budget |

G18/G19's selected vehicles are accepted ADR-0051 and append-only ADR-0043
Amendment 5. The original register's Leaning/Vehicle text remains review history;
this acceptance records the selected vehicle explicitly. Existing ADR decisions
are preserved; ADR-0048's Status and dated supersession are lifecycle bookkeeping.

#### Seed inventory and ownership

Step 4 delivers this inventory; both review rounds and final verification passed.
Exact IDs, schema/body literals, slugs, labels, palettes and computed counts live
in `SeedData`; no
production branch knows `demo-english`, `demo-yoga`, `grammar-topic` or `asana-pose`.
The existing fixed tenant, organization, host and built-in customization IDs stay.

| Data | English tenant | Yoga tenant |
|---|---|---|
| Host context | Existing tenant host, organization null | Existing Studio One host |
| Locales | `en`, enabled default | `tr-TR`, enabled default; `en`, enabled |
| Tenant content type | `grammar-topic`, revision 1 Active, `default-card`; plain-string `concept`/`example` | `asana-pose`, revision 1 Active, `default-card`; plain-string `pose`/`instruction`/`breathing` |
| Taxonomy | `cefr`, revision 1 Active; six declared bands | `yoga-difficulty`, revision 1 Active; three declared bands |
| Branding | One tenant-wide `branding.theme`, complete valid palette | One tenant-wide `branding.theme`, distinct complete valid palette |
| Courses | Four: tenant-wide published public, tenant-wide draft public, tenant-wide published restricted, Kadıköy-scoped published public | Four: tenant-wide published public, Studio One published public, Studio Two published public, Studio One published restricted |
| Lessons | Five: two under public tenant-wide course (published/draft), one published under draft parent, one published under restricted parent, one published Kadıköy-scoped | Five: one tenant-wide, two Studio One public, one Studio Two, one Studio One restricted; published |
| Translations | One `en` translation per course/lesson | Both `tr-TR` and `en` per course/lesson; genuinely different translated slugs/labels |
| Cross-tenant positive control | Published public tenant-wide course uses `en` slug `foundation` | Published public Studio One course also uses `en` slug `foundation`; no uniqueness across tenants |

All eight courses and ten lessons have explicit pins and access/state choices;
lesson policy is inherited. The declared translations total 27: nine English and
18 Yoga. No remote media is seeded. Wrong-course proof uses the two visible Yoga
courses; organization controls are hidden/visible against the appropriate host.

Pagination uses **test-owned** courses and a test-selected bounded page size in
P02d-4, not arbitrary mass demo data or a premature G10 default. A retained
disabled-locale translation is also a P02d-4 historical-state fixture, with an
explicit test setup; `make seed` does not bypass locale admission to fabricate it.
Schema mismatch, absent presentation, missing labels and unresolved pins are negative
test fixtures rather than invalid demo rows. Packet 7's raw `tz`/organization `theme`
fixtures remain legal and are counted separately from the new seed theme.

Every act derives a fresh composed trusted seed scope, with nullable organization.
Tenant locales, customization definitions, branding and tenant-wide courses run in
tenant-wide context; organization courses/lessons run in their exact organization.
Translations and publication use the root's write scope. No admin role, superuser,
RLS bypass, direct EF mutation or ad hoc seed SQL supplies normal writes.

Seed order is provisioning/organizations/hosts, enabled locales, built-ins and tenant
types/taxonomies (create then publish), branding, course/lesson drafts and translations,
then each selected root's publication. Translation precedes publication on a new root.
Restricted controls depend on accepted ADR-0050 and its applied migration.

Before skipping an existing act, the ownership query verifies exact ID, parent, scope,
pins, enabled locale, translated slug/text, semantic JSON, policy and state as relevant.
Absent acts are written; exact completed acts are skipped without mutation/audit.
Creation verification accepts only the declared draft intermediate state or intended
final publication state, with identical immutable data; later acts still verify all
translations and final state. It does not demand draft on a completed published root
or accept arbitrary states. This rule also covers customization create/publish acts.
A typed uniqueness, concurrency or lifecycle race triggers one fresh-scope ownership
check against that act's completed postcondition, never blind retry or
matching by display name. Mismatch fails nonzero without editing, unpublishing,
rebinding or selecting another revision. Existing published translations are checked
and skipped before calling the draft-only translation command. A published mismatch
is not accepted merely because that command returns `translation_requires_draft`.
If a competing runner has not completed the exact postcondition, fail safely for a
later explicit rerun rather than returning false success or spinning indefinitely.
Interrupted partial seeds converge; a second completed run adds no roots or audit rows.

#### Implementation steps and review loop

All steps run on **development**. No branch switch, push or PR is part of preparation.
Each implementation step follows the maintainer's requested loop: implement and
commit; first fresh independent multidisciplinary review; verify/fix valid findings
and commit; second fresh review agents; verify/fix and commit; continue automatically.
Agent models/effort follow complexity and available models. Unverified findings are
rejected with evidence; an unresolved material defect blocks that step's completion.

| Step | Coherent change | Required evidence before step completion |
|---|---|---|
| 1 — policy and contract foundation | Education access value/configuration and additive migration; exact Customization/Tenancy read DTO contracts; ADR-0051 profile parser/resolver; module-scoped seed verification queries | Domain/profile positive and negative cases, exact revision and locale isolation, policy/default/legacy preservation, migration forward/down/reapply and pending-model check; unsupported schemas remain valid but not implicitly renderable |
| 2 — Tenancy writers | Locale commands and pre-mutation guards; default-enabled CHECK migration; whole-theme registry/command, contrast and concurrency; PII capture; matrices/composition | Disabled-first→enabled-second, disabled promotion leaves root/audit unchanged, invalid legacy refusal, injected second-save rollback, scoped writes, palette and concurrent replacement/create conflicts, whole-value audit redaction |
| 3 — Education writers | Six separate commands, validation against exact schema/taxonomy and enabled locale, parent scope, named-constraint failures, audit and ambient transaction | Body/pin/locale/slug failures, one-root lifecycle, publication audit atomicity, RLS mirrors, uniqueness races, stale versions, and swallowed nested failures cannot later flush dirty state |
| 4 — convergent seed and packet closeout | Contextual verification replaces direct setter; complete SeedData acts; source guard and planted companion; seed tests and Packet 7 counts recomputed; documents match delivery | Fresh seed, second-run no-change, interrupted recovery, concurrent seed convergence, mismatches fail nonzero without writes, host/scope positive controls, no caller-fence escape, full required verification and two review rounds |

Checks scale to each step's change. Final closeout runs Release build/format and all
required unit, architecture, contract, Docker-free and Docker integration suites with
zero skips; applicable EF chains' pending-model/forward/rollback/reapply checks;
seed convergence; Markdown links/anchors and `git diff --check`. Record actual commands,
counts and results in the delivery record, not a speculative passing total here.

#### Approval boundary and readiness

**Preparation verification — 2026-10-02.** The reviewed documentation commit is
`7bc8e63`, against `6bbcb1e`; no implementation is included. First-round security
and governance findings were verified and fixed: post-build extension resolution,
explicit lesson seed states, no platform locale registry and endorsed participation
wording. Two fresh second-round agents independently returned Approve, with no
remaining actionable findings. Reviews used `gpt-6-astra` (high) for security and
`gpt-6.1-sol` (xhigh) for governance/seed/corpus consistency.

- Architecture: 177 passed, zero failures/skips, Release `--no-build --no-restore`;
  the TRX execution-count check passed. This verifies existing guards, not future
  policy migrations, writers or public readers.
- Documentation: 33 Markdown files, 1,715 local references and 444 fragments checked;
  added prose wrapping and `git diff --check` passed. External provider/legal evidence
  was not reassessed in this preparation pass.
- Accepted ADRs were unchanged. The entire P02d-1 decision/delivery suffix was
  compared byte-for-byte with the baseline and is unchanged.
- Commit hooks, including staged Leakwatch and commit-message validation, passed.
  Work remains on development; no push or PR was performed.

**Exact acceptance and wait — 2026-10-02.** The maintainer approved ADR-0050/0051
and this package, with the explicit instruction to complete documentation and wait.
Lifecycle, dated G3 supersession, current gate statuses, corpus references and new
Registered source-proof rows are updated. P02d-2's decision pass is complete and
the document is ready for Step 1 when implementation is resumed. No code, migration,
handler or new seed data has been delivered; the packet is not marked complete.

The marketplace's company/country, provider, selected region, legal roles, seller
corridors and detailed Phase 09a contracts remain its own first-consumer gates.
ADR-0049 and Phase 09a remain Proposed. They neither expand P02d-2 nor hold its
independent protected-content work. No implementation, push or PR follows this update.

**Acceptance verification — 2026-10-02.** Two independent reviewers returned Approve
after verified bookkeeping findings were fixed: `gpt-6.1-sol` (xhigh) for governance
and `gpt-6-astra` (high) for security/corpus consistency. Both checked the final scope,
partial gate closure and absence of implementation claims.

- Existing Release architecture suite: 177 passed, zero failures/skips; the TRX
  execution-count guard passed. Future Registered proofs are not included in that count.
- Documentation: 33 changed Markdown files, 2,117 local links and 486 fragments checked;
  added prose wrapping and `git diff --check` passed.
- ADR-0043's pre-existing text and ADR-0048's original decision body are unchanged;
  additions are dated lifecycle/extension disclosures. The complete P02d-1 suffix
  remains byte-for-byte unchanged against `3f849d1`.
- The two new catalogue rows remain Registered; all 144 Implemented entries are
  unchanged. No backend/frontend implementation or deployment operation is included.


### P02d-2 implementation delivery (2026-10-02)

The maintainer resumed implementation after exact approval. The earlier acceptance
and wait record remains historical; work now follows the four accepted steps on
**development**. No public endpoint, marketplace commerce or authorization grant
is introduced by P02d-2.

**Step 1 — policy and contract foundation, complete.** Course creation takes
an explicit closed policy. The additive Education migration defaults/backfills
`enrollment_required`, preserving existing content and scope; technical Down is
proved only in a disposable database, not approved as a live public-reader rollback.
Exact revision/locale ports, ordered text-card semantic resolution and contextual
seed verification queries are registered in both composition roots. Read queries
are Off; no seed context-setter change or writer completion is claimed yet.

**Step 1 pre-review verification.** Release build completed with zero warnings/errors.
Docker-free tests: 1,489 unit, 177 architecture, 171 integration and one contract,
all passing with zero skips. Fifteen focused Docker integration cases passed,
including both composition roots, exact revision/locale reads, Education persistence
and migration forward/down/reapply. Full format verification and the TRX execution
count/zero-skip checks passed. Markdown validation checked 2,137 local references
and 487 fragments across 34 files against the preparation baseline; P02d-1's frozen
record and all Accepted ADR bodies remain unchanged. `git diff --check` passed.

**Step 1 review round 1.** The policy/contract implementation is `682f858`. Two
independent agents found no production defect. Verified gaps in positive/sibling/foreign
setting proofs and disabled/foreign locale proofs were corrected independently of
invalid-default configuration checks. All five strengthened foundation tests pass,
including a disposable predecessor-schema proof for a disabled legacy default.
Current README, glossary and module/standard
status statements were aligned with the delivered foundation. A broader Docker
regression run passed all 228 cases, with zero skips. Fixes are `a8a51ce`.

**Step 1 review round 2.** Fresh agents independently returned Approve for
`45805c8..a8a51ce`, with no actionable findings. Security review used `gpt-6-astra`
(high); profile/corpus review used `gpt-6.1-sol` (xhigh). Independent runs passed 116
profile/registration unit cases, six foundation/migration Docker cases and all 177
architecture cases, with zero skips. Links/anchors and the frozen P02d-1 suffix passed.
Step 1 is complete after both requested rounds.

**Step 2 — Tenancy writers, complete after both review rounds.**
Three unrouted tenant-wide commands implement locale addition/default selection and
complete branding creation/exact replacement. Existing locale configurations are
validated before mutation; the first enabled locale is promoted even after disabled
rows. Two-pass default saving also handles newly Added defaults. The additive CHECK
refuses disabled defaults and never repairs legacy data automatically. Generic setting
JSON is wholly redacted at capture. Palette fields and CSS mappings share a closed
registry, with per-pair contrast admission and no entitlement gate. Failed saves after
mutation mark the ambient unit rollback-only. Steps 3–4 remain pending.

**Step 2 pre-review verification.** Release build passed with zero warnings/errors.
Docker-free tests passed: 1,538 unit, 177 architecture, 171 integration and one
contract. The expanded Docker regression group passed all 75 cases, including
composition, locale/default and branding writers, whole-value audit redaction,
concurrent creates/replacements, MUST audit failure, injected second-save rollback,
invalid legacy refusal and migration forward/down/reapply. Execution-count checks
confirmed zero skips. The broader group also exposed a test-isolation defect in
Step 1's writing foundation case: its independent audit rows survived the business
rollback in the shared fixture. That case now owns a disposable database; the full
75-case group passes without leaking rows into other tests.

**Step 2 review round 1.** Security (`gpt-6-astra`, high) found one verified
classification defect: the shared EF helper wrapped unknown unique constraints with
a business error before the handler could distinguish them. New Tenancy writer
methods now supply owned constraint sets; unowned failures stay database faults.
Two planted PostgreSQL constraints prove `500 internal_error`, unchanged state and
no successful audit. Existing helper callers retain their contract. The corpus/theme
review (`gpt-6.1-sol`, xhigh) found two Minor documentation gaps; prose wrapping and
public contract XML documentation are corrected. Palette key-order and exact/over-cap
input proofs also cover its optional test suggestion. After fixes, Release build
passed with zero warnings/errors; 77 Docker regression cases, 185 focused unit and
177 architecture cases passed with zero skips. The implementation is `1b98352`;
first-round fixes are `63ed4bc`.

**Step 2 review round 2.** Fresh security (`gpt-6-astra`, high) and corpus/theme
(`gpt-6.1-sol`, xhigh) agents independently returned Approve for
`a8a51ce..63ed4bc`. Each independently passed 160 focused unit and 12 PostgreSQL
writer/migration cases, with zero failures/skips. The one verified Minor was a stale
Tenancy component description; its root/writer claim, command/store inventory and
table count now match code. Links/anchors, wrapping and the frozen P02d-1 record pass.
Step 2 is complete; Steps 3–4 remain pending.

**Step 3 — Education writers, complete with both review rounds.**
Six separate commands write one Course or Lesson and its contained translations.
New binds resolve exact Active revisions; existing lesson pins remain eligible after
deprecation. Enabled locale admission and schema evaluation precede mutation. Visible
but incompatible write scope is refused; hidden roots remain indistinguishable misses.
Translation insertion reserves its tenant-local slug; diagnostics identify only visible
conflicts. Publication remains independent and MUST-audited. Known post-save failures
poison the ambient unit, including when an outer handler absorbs the refusal.
No public endpoint, protected reader or seed execution is claimed by this step.

**Step 3 pre-review verification.** Release build passed with zero warnings/errors.
The full Docker-free run passed 1,557 unit, 177 architecture, 171 integration and
one contract case, without failures/skips. The focused database regression run
passed 249 cases, including all 14 Education writer cases, the existing Education
isolation suite, Tenancy writers and migration reversal. Both known post-save
refusal variants were absorbed by an outer handler that saved another root; the
owning transaction still refused commit and rolled back both writes. Concurrent
publication and localized-slug insertion produced one durable winner. Unknown
unique constraints retained `internal_error`; hidden collisions disclosed no root
identity. Full format verification, Markdown links/anchors and `git diff --check`
passed. This step changes no EF model or migration. Two fresh review rounds follow.

**Step 3 Round 1.** Fresh GPT-6 Astra (high) security/transaction and Claude Sonnet 5
(high) contract/validation/corpus reviews both returned Approve. The security reviewer
independently reran all 14 Education writer database cases. Two verified Minor test
findings were fixed: validator tests now resolve the registered internal validators
through DI, and organization-scoped translation/publication attempts against a visible
tenant-wide Lesson explicitly assert refusal and unchanged state. The optional
unreachable policy-switch suggestion is not a defect: the composed validator admits
exactly the two explicit values, and the domain guards its closed enum. No production
change was justified. Round 2 uses new review sessions after the fixes.
Release build, all 16 validator cases and all 14 writer database cases passed again,
without failures/skips. Full format verification exited zero; links/anchors and the
unchanged P02d-1 suffix check passed.

**Step 3 Round 2.** Two fresh read-only Codex review sessions (high and xhigh)
reviewed `8b981c7..a4a29d3`. The security reviewer returned Approve; the contract
reviewer requested changes. All findings were verified against current code.
Internal parent and collision ports now retain module-local typed IDs; conversion
occurs at the command boundary and unwraps only for exported diagnostics. The
aggregate-write census excludes typed keys without exempting the reader by name;
its planted companion still detects direct, nested generic, array, by-ref and mixed
object writes. The existing 1 MiB instance cap now has one shared owner, and the
Education validator checks UTF-8 size before any JSON parse. Inclusive ASCII and
multibyte boundary tests and a composed writer refusal/unchanged-state proof cover
it. New null suppression is removed or justified by the exception filter. README,
Database Standard and the old Backend Coding publication example now name the
shipped writer contract. Additional focused review follows these production fixes.
The attempted fresh Claude sessions reached the provider's session limit and
produced no review; they are not counted as completed rounds.

**Step 3 fix verification and closeout.** Fresh xhigh read-only review of
`a4a29d3..2e498ce` returned Approve with no blocking findings. Its two low-priority
suggestions were verified and applied: the oversize proof now counts every audit
outcome, and a stale XML description no longer attaches the old size limit to
`BuildOptions`. The fixed census passes all 178 architecture cases; its planted
controls also cover inherited interface signatures. Release build has zero
warnings/errors, 1,559 unit cases and the contract case pass, and all 780 integration
cases pass, including 15 Education writers. No failures/skips remain; format,
local links/anchors and the frozen P02d-1 suffix check pass. Step 3 is complete;
Step 4 follows below.

**Step 4 — convergent seed, complete after both review rounds.**
The fixed identities remain unchanged. SeedData owns every declaration and expected
count: two tenants, four organizations, two hosts, three locales, four content types,
four taxonomies with fifteen bands, two distinct whole themes, eight courses, ten
lessons and twenty-seven translations. Published/draft and public/restricted cases
include tenant-wide and both organization scopes. English and Yoga have their own
schemas, labels and bodies; Yoga has complete tr-TR/en translations. No remote media,
marketplace fields or P02d-4-only historical/negative fixtures are introduced.

Every verification and write goes through contextual ISender requests. Completed acts
skip before a writer, partial declared acts resume, and a typed race gets exactly one
fresh postcondition read. Mismatches fail without overwrite; no private transaction,
setter or direct persistence path remains. Internal reader ports retain typed IDs.
The caller-fence guard and planted companion are Implemented; G20(a) reads all literal
sources from SeedData, while the full production branch guard remains P02d-7's owner.
The seed skill, host/isolation projections, catalogue and current-state documents
now match implementation. Tests own disposable databases rather than shared cleanup.

**Step 4 verified consumer defects.** Concurrent seed execution exposed a real
Customization publication race: a competing publish between two READ COMMITTED reads
caused EF to return the already-tracked Draft as its own Active incumbent. Both
handlers now return typed concurrency before mutation. A failed publication save now
marks rollback-only even without an incumbent, so an absorbing outer request cannot
flush dirty publication state later. Six controlled database cases cover both roots,
the precise race and post-save conflict/concurrency absorption. An old unit expectation
that assumed no retirement meant no dirty state was corrected against these proofs.
No new ADR or schema decision is introduced.

**Step 4 pre-review verification.** The 49 focused database cases pass without
failures/skips: thirty-five Seeder, eight host/isolation and six Customization race
cases. These include full inventory/scopes, all-outcome audit neutrality on repeat,
two real interruption points, coordinated concurrent seed, seventeen mismatch cases,
semantic JSON equivalence and the executable's actual zero/nonzero exit behavior.
A fresh complete seed produces ninety successful audit rows; provisioning writes two
aggregate audit rows, and all counts derive from SeedData. Full verification and
both fresh review rounds were still required at this pre-review stage.

**Step 4 broader regression corrections.** The complete Docker run exposed two
inherited audit groups tied to the old seed: fixed counts and direct repeat writers
had been inferred from a runner that now skips completed acts, and shared cleanup
could not remove the new restricted dependencies. AuditPipelineTests and
AuditWorkflowTests now own disposable databases and focus their declaration on
provisioning/built-ins. Rollback audit proof sends explicit refused commands and
retains the two provisioning intents plus the standalone organization refusal.
All eighteen cases pass; the expanded 67-case seed/host/Customization/audit group
passes without failures/skips. Complete Docker regression then passed 638 cases.

The two seeded schemas now differ in property count, as the inherited phase criterion
requires: Yoga also declares ordered, bilingual plain-string breathing text. The
inventory proof checks actual persisted schema shape, beyond changed names/labels.
A verified publication mismatch could otherwise retire a different Active revision
under the same key. Seed sets both publish commands' optional `RequireNoIncumbent`
precondition, which refuses before mutation and retains ordinary succession by default.
Two additional seed cases prove unchanged definitions/versions/generations and no
successful audit on that refusal; normal failed-command audit remains legitimate.
The two new cases and six controlled race/rollback cases pass. Release build has
zero warnings/errors; all Docker-free suites pass again. At that stage, fresh review
closure and final complete verification remained pending.

**Step 4 Round 1.** Fresh read-only Codex xhigh security review requested changes;
Claude Sonnet 5 high contract/corpus review returned Approve with one cosmetic Minor.
The security finding is verified: publishing the declared revision could retire an
incompatible Active revision. Contextual Off queries now check the logical key's
Active identity before registration, and publication checks `RequireNoIncumbent`
inside its transaction before retirement. Four pre-existing absent/Draft cases and
two coordinated different-revision winners accompany the same-ID race proofs;
failed seeds preserve existing content, versions, generations and audit successes.
The cosmetic extra blank line is removed. The earlier 67-case regression corrections
and schema-shape proof are included in this fix set; a fresh second round follows.

**Step 4 Round 2 and packet closeout.** Two fresh read-only Codex review sessions
(high for contracts/corpus; xhigh for security/transactions) reviewed
`951acd2..fecdc76` and independently returned Approve. No Blocker or Major remained.
Both found the stale README bootstrap statement; the contract review also found the
old Step 4 review heading. Both verified Minor findings are fixed, and current-state
carriers and the seed skill now name completion. No additional production change was
required. The implementation is `5081724`, with first-round fixes at `fecdc76`.

**Final verification — 2026-10-02.** All required local checks pass:

- Release build: `dotnet build backend/LearnStack.slnx --no-restore -c Release`, zero
  warnings/errors. Full format verification uses `dotnet format` with
  `--no-restore --verify-no-changes` and exits zero.
- Backend: Release `dotnet test --no-build --no-restore` with separate
  `Requires!=Docker` and `Requires=Docker` runs; 1,559 unit, 181 architecture,
  one contract, 171 Docker-free integration and 644 Docker integration cases pass.
  Total: **2,556**, zero failures/skips. TRX counters were inspected for failures;
  `scripts/assert-tests-ran.py` also proves execution and zero skips per assembly.
- The focused seed/host/Customization/audit group passes all 73 cases: 39 Seeder,
  eight host/isolation, eight controlled publication and eighteen audit cases.
  Actual CLI exits, interrupted/concurrent recovery, exact repeat neutrality,
  incompatible Active winners and transaction absorption are covered.
- All five EF chains report no pending model changes using the pinned tool in
  `backend/`, Release `--no-build` and a design-time-only placeholder connection.
  The complete Docker run includes every-chain forward/down/reapply, populated
  access-policy preservation and disabled-default migration refusal. This is
  disposable-database reversal evidence, not approval to remove live access policy.
- Frontend: frozen install, typecheck, lint, build and all thirteen Vitest cases pass.
  Four default/gated dev/e2e compose combinations and actionlint pass. These surfaces
  did not change after verification; no browser-rendered demo is claimed.
- The PR documentation sweep checks 45 Markdown files, 2,868 local links and 571
  fragments. Added prose wrapping and `git diff --check` pass. Analysis-directory
  hits are the existing prohibition/history wording, not links to scratch material.
  The complete P02d-1 suffix is byte-identical to the acceptance baseline. Accepted
  ADR bodies have no implementation-time edits.
- Commit hooks pass, including staged Leakwatch, formatter and strict message checks.
  The pre-closeout PR range's seventeen non-merge commits pass the same strict hook.
  Final closeout is checked with the resulting PR range before publication.

**Disposition.** P02d-2 implementation, both review rounds per step and required
verification are complete on development. PR review and merge remain the maintainer's
next step. Phase 02d remains in progress. P02d-3 owns generation-keyed customization
read internals and the uncached ambient typed settings accessor; its remaining gates
open before that work. P02d-4 owns public reads and hidden-response/query-plan proofs;
P02d-5–7 own transport, rendering and the running two-site demo. No marketplace,
authentication, enrollment, public API or browser-delivery completion is inferred.

#### PR review corrections (2026-10-02)

External PR #23 findings were verified against `6fce655` before changes. Current
carriers now separate delivered P02d-2 policy, migration, writers, presentation
resolution and seed from P02d-4 public reads, P02d-6 rendering and Phase 07 grants.
ADR-0050/0051 retain their acceptance-time Status and Decision text; dated
implementation disclosures and Amendment 1 record delivery. The indexes, glossary, audit
review state, implemented setter fence, contrast refusal and unrestricted default
entitlement provider are aligned. No access, branding, marketplace or governance
decision changes.

The exact presentation reader now returns `/properties` validation failures for missing
or non-object members and non-object roots. Eleven regressions preserve its Result
contract and optional legacy profile behavior. Migration reversal locates the policy
migration by stable ID; both Up applications prove the restricted backfill. Lesson and
Course post-save refusal absorption, both Customization MUST-audit replacement rollbacks
and the incumbent rollback-only branch have direct proofs. A controlled non-provisioning
translation race witnesses two real save attempts, one successful act and one audited
refusal, fresh completed-state recovery and an unchanged all-outcome repeat snapshot.

Theme replacement-version requirements do not apply to create-only seed writes.
Reference admission remains with the authoritative exact-revision writers; no new
whole-declaration preflight contract is introduced. Existing inventory comparisons
already prove both tenants' shared English slug and foreign-tenant invisibility.
Existing repeat snapshots include every audit outcome; the new race additionally retains
an observed failed audit. These findings require no production expansion.

Release build has zero warnings/errors; 1,570 unit, 181 architecture, 171 Docker-free
integration, one contract and all 69 affected database cases pass with zero skips.
Relative links, fragments, added prose wrapping, format and `git diff --check` pass; the
P02d-1 suffix remains byte-identical.

**Independent review round 1.** The correction commit is `40d207d`. A fresh Codex
reviewer at xhigh effort traced parser callers, migration targeting, rollback and seed
race evidence; a fresh Sonnet 5 reviewer at high effort checked corpus/governance and
source claims. Both returned Approve with no material findings. A separately detected
89-column prose line is wrapped without changing its meaning.

**Complete backend verification.** After the corrections, all 2,573 cases pass: 1,570
unit, 181 architecture, one contract, 171 Docker-free integration and 650 Docker
integration. TRX counters show zero failures/errors/skips; the execution guard passes
for every assembly. All 69 affected database cases pass within the full run. Release
build has zero warnings/errors, and full format verification exits zero. The PR
documentation sweep checks 45 Markdown files, 2,892 local links and 577 fragments. No
frontend or operational surface changed in this correction. The second independent round
and its correction are recorded below.

**Independent review round 2.** Fresh Codex (xhigh) and Sonnet 5 (high) sessions
reviewed `6fce655..a436a51`. Sonnet approved the corpus. Codex found one valid proof
gap: identical translation contenders did not falsify removal of the loser's required
post-race recheck. All other code/security, rollback, migration and documentation
boundaries were clean.

The test now covers identical and divergent payloads for the same course/locale. A
conflicting loser must refuse its exact state before reporting the act completed; the
independent later final-state check cannot supply this proof. A controlled mutant that
skipped only the losing-race recheck passed the identical case and failed the divergent
case at the completion assertion. `SeedRunner.cs` was restored byte-for-byte; no
production seed behavior changes. Both positive variants and the complete backend suite
then passed. The standards index's current-state date now matches the delivered
2026-10-02 table.

**Targeted follow-up review.** A fresh Codex session at high effort reviewed
`a436a51..163eb90` and returned Approve: the completion assertion kills the missing
recheck mutant, winner selection is arrival-order independent, real save/audit witnesses
and cleanup remain intact, and the revised documentation counts agree. The correction
and both independent review rounds are complete; the only verified second-round finding
is fixed and independently rechecked. Final hooks, wrapping, links/fragments and the
frozen-record check pass. PR #23 carries the updated range and validation; merge remains
the maintainer's decision.

#### Additional PR review corrections (2026-10-02)

The subsequent review was verified against `eaa72aa`; findings and suggested edits
were treated as evidence, not instructions. No decision, public route or migration
changes were needed. The following corrections retain the accepted packet scope:

- The tenant write lookup excludes soft-deleted roots, preserving both navigations,
  identity matching and cancellation. A real application-role test proves retained
  locales/flags on a live root and refusal of locale writes after deletion.
- The aggregate census shares its production predicate with planted fused-port,
  two-port, notification, non-public-constructor and negative controls. Key-only
  writes remain visible; read exemptions enumerate methods rather than all typed IDs.
  Four controlled mutants (first construction only, omitted notifications, blanket
  key exclusion and public-only constructors) each fail the intended companion.
  The source is restored byte-for-byte before positive verification.
- Both runtime hosts build application pools through the shared role guard. Direct
  seed execution now refuses owner/bypass credentials and transitive bypass access;
  malformed credential exceptions carry no raw inner parse exception. Unit canaries
  inspect full exception text; real direct/transitive refusal runs through both callers.
- The exact presentation resolver converts invalid JSON syntax into a root-located
  validation failure. The seed fence also checks IL for direct EF mutation and ad hoc
  commands, while admitting immutable data construction and request dispatch.
- Additional proofs cover foreign-context Education writes, locale tenant isolation
  and concurrent exact-version changes, identifier-conflict details, renderer-registry
  non-emptiness, CHECK removal on Down and refusal of an undeclared persisted locale.
- Current documentation records accepted/delivered branding and writers, the exact
  current/planned route boundary, the Tenant locale audit subject and accepted anchors.
  ADR-0050/0051 show their dated disclosure inside `#status` and preserve the original
  acceptance text under Amendment 2. The catalogue records delivered presentation
  proofs separately from Registered public projection/rendering obligations.

**Verified dispositions.** No remaining item is silently assigned to a later packet:

| Finding | Disposition and reason |
|---|---|
| Inline tenant lookup / ADR-0049 / permissions | Fixed against the current store, delivery record and Phase 02c route contract. |
| M1 | Fixed; the same production census now has permanent planted controls and four failing mutants. |
| m2–m4 | Fixed: current-state references, JSON syntax refusal and the shared seed/runtime role guard. |
| m5 | Documented per-act durability: a competing Active revision may appear after a registration pre-read, leaving a committed Draft/generation/audit before refusal. Explicit reconciliation is required; seed never rolls back completed acts. |
| m6 | Added the narrow requested proofs, retaining real application-role transactions. |
| m7 | Skipped: ADR-0051 requires existing localized messages plus offending JSON Pointers, which the resolver already supplies; distinct cause keys are not required. |
| m8 | No missing registration found. Both roots are exercised by composed writer/publication tests; a separate parity scanner is optional protection, not a current correction. |
| m9 | Skipped: ADR-0050 explicitly permits disposable Down/reapply and denies live rollback authorization; the forward-only operational rule already applies. |
| m10 | Fixed: the existing seed caller fence now includes direct persistence writes, with positive and negative planted controls. |
| m11 | Refuted: every existing locale must match a declaration under `SeedVerification.Tenant`; the new persisted-extra-locale test proves refusal without a write. |
| m12–m14 | Fixed: audit subject, unused proposed anchor aliases and Status navigation with dated historical disclosure. |
| s1–s5 | No defect: supported calls validate the closed policy; repeated subject designation is sanctioned; titles/summaries map to unbounded text; no unchanged-version promise exists for default-setting commands; parent reads already use `AsNoTracking`. |
| s6 | Skipped: an admitted named race still requires exact fresh-scope postconditions, already mutation-proved; it never accepts a refusal alone. |
| s7–s8 | Ambient is the sanctioned trusted origin. Exit codes are documented; malformed credential failure text is now proven secret-free. |
| s9 | No current public oracle: explicit-ID writers are unrouted trusted operations and replacement lookup hides foreign roots. Phase 03 owns authoring authorization before reachability. |
| s10–s11 | No supported incumbent-deprecation uniqueness collision or registered authoring route found. Unexpected faults already poison the transaction; anonymous admission remains fenced. |
| s12 | Fixed: delivered profile/exact-order evidence and future renderer/sink obligations are catalogued separately. |
| s13–s15 | No current defect: no production lock incident/online-migration mandate; seed DTOs are private verification contracts; raw foundation inserts intentionally model isolated/legacy fixture states, not provisioning. |
| s16 | Historical counts are retained. Supplemental link counts count occurrences outside fenced blocks across PR-changed Markdown and linked ADR/phase carriers; CI's changed-target audit has a different scope and excludes fragments. |
| Legacy profile boundary | Compatibility applies to schemas without root `x-fields`. Its presence explicitly opts into ADR-0051's profile; a formerly inert annotation at that location is now semantic. |
| PR-external follow-ups | Unchanged scripts and legacy conflict helpers are outside this correction. G20's remaining branch fence stays P02d-7; public reads/rendering/grants retain their existing named owners. |

The shared [seed workflow](../../.claude/skills/seed-tenant/SKILL.md) records direct
role enforcement, failure codes and the committed-Draft race boundary.

**Positive verification.** Release build has zero warnings/errors. All 2,590 backend
cases pass: 1,577 unit, 184 architecture, one contract, 171 Docker-free integration
and 657 Docker integration. TRX counters show zero failures/errors/skips, and the
execution guard passes for every assembly. Full format verification exits zero.
Eight controlled mutants fail the intended guard; both source files are restored
byte-for-byte and the positive structural suite is re-run before commit.
The supplemental documentation sweep checks 45 Markdown files, 2,901 local link
occurrences and 580 fragments; `git diff --check` passes and the frozen P02d-1
suffix remains byte-identical. Independent reviews follow the correction commit.

**Independent correction review — round 1.** Fresh GPT-6.1-sol (`xhigh`)
reviewed the backend/security/proof boundary at `05682d8`; fresh GPT-5.5 (`high`)
reviewed documentation/governance. Neither found a correctness or isolation defect.
The code reviewer found one unused API redaction helper left by extraction; it is
removed, leaving the shared implementation as the only caller-backed helper. The
parent also corrected one added catalogue prose line exceeding 88 columns.
Release build remains warning/error-free; credential guard tests pass 20/20,
architecture passes 184/184 and scoped format verification exits zero. Links and
the frozen suffix remain unchanged. Fresh round 2 reviews the complete correction
range after this fix commit.

**Independent correction review — round 2 and closeout.** Two new GPT-5.5
sessions (`xhigh` for backend/security/proofs, `high` for documentation/governance)
review the full `eaa72aa..15123ca` correction range and both approve without an
open actionable finding. They independently inspect the positive TRX, mutation
and restoration evidence; the documentation reviewer also verifies the unchanged
1,799-line frozen suffix. The reviewed production code is complete. This closeout
adds only the review record; the PR remains open for maintainer review and merge.

#### Branding tenant-existence correction (2026-10-02)

The outside-diff finding is verified against `3a4bb5d`. Branding now refuses a
missing or soft-deleted announced tenant with the existing `not_found` result,
after scope validation and before any setting lookup or write. A scalar, uncached
Tenancy reader is registered in both runtime composition roots; the handler still
holds one aggregate write capability. Live Trial tenants remain supported.

Four application-role regression cases cover missing/deleted tenants and
create/replace intent. A throwing setting store proves refusal precedes any
setting access; fresh reads prove unchanged values/versions and no additional
successful setting audit. The existing seeder-root deletion proof also covers both
branding writes. Unit scope guards prove no existence read before scope admission.

Release build has zero warnings/errors. Final suite runs pass all 2,594 backend
cases: 1,577 unit, 184 architecture, one contract, 171 Docker-free integration and
661 Docker integration. TRX counters and execution guards report no failures,
errors or skips. Full format, local links/fragments and the frozen P02d-1 record
check pass. Two fresh independent review rounds follow the correction commit.

**Independent reviews and closeout.** Two fresh GPT-5.5 (`high`) read-only
sessions review the exact `3a4bb5d..50518e7` correction range independently. Both
approve without an actionable Blocker, Major or Minor finding. They verify scope
ordering, live Trial support, both DI registrations, refusal before setting access
and the positive build/TRX evidence. No further production change is required;
this documentation-only closeout records the completed rounds. PR #23 remains
open for maintainer review and merge.

### P02d-2 merge and closeout (2026-10-02)

[PR #23](https://github.com/HodeTech/LearnStack/pull/23) merged into `main` at
**11:41:47 UTC**, with final PR head `161314313eeb0d87758fb38c20af5e4c4c1b5766`
and merge commit `8edbb032b81313aae7e635b2782af511a9fe02cc`. Their trees are
identical. `development` was fast-forwarded to the merge commit without switching
branches or rewriting history. This closeout changes documentation only.

- [x] Accepted P02d-2 gate parts and all four implementation steps are complete;
  each step and the subsequent verified PR corrections completed both review rounds.
- [x] ADR-0050's policy, restricted backfill and Education writers are delivered;
  ADR-0051's profile parsing and resolution are delivered. Public-read enforcement
  remains P02d-4, rendering P02d-6 and course access grants Phase 07.
- [x] Three Tenancy and six Education writers, exact-definition/locale validation
  and convergent two-tenant seed execution are delivered and registered.
- [x] The final tenant-existence correction refuses both branding write intents
  before setting access; live Trial tenants remain supported.
- [x] The final PR head passed all five required checks; CodeRabbit also succeeded.
- [x] The merge commit passed the same five required checks.

| Verified revision | CI evidence | Result |
|---|---|---|
| Final PR head `1613143` | [Run 36986675925](https://github.com/HodeTech/LearnStack/actions/runs/36986675925) | All five required jobs succeeded |
| `main` merge commit `8edbb03` | [Run 37002423112](https://github.com/HodeTech/LearnStack/actions/runs/37002423112) | All five required jobs succeeded |

Final implementation verification records **2,594 passing backend cases**: 1,577
unit, 184 architecture, one contract, 171 Docker-free integration and 661 Docker
integration, with zero failures or skips. Release build has zero warnings/errors;
format and link/fragment checks pass. The historical P02d-1 record remains unchanged.

The live required-check list still contains the five recorded contexts with
`strict: true`. The documentation closeout also passes 184 architecture cases,
execution/zero-skip guards, added-prose wrapping and relative-link/anchor checks.

**P02d-2 is closed. Phase 02d remains in progress.** P02d-3 through P02d-7 have
not started. No public business endpoint or browser demo is delivered by this merge.
ADR-0049 and the Course Marketplace pilot, Phase 09a, remain Proposed.

#### P02d-3 entry readiness

P02d-2's merged definitions, settings and seed satisfy the implementation dependency.
The next action is P02d-3's decision pass, followed by read internals with no HTTP:
generation-keyed Customization projections/cache families and a typed Tenancy
settings accessor. The packet table and decision register remain authoritative:

- G12's cache-key part, G22's ambient loader/generation/rollback/cache contract and
  G24's display fallback remain to be accepted.
- G23's no-settings-cache bound is already Accepted. The accessor's name, ambient
  loading and tenant/organization scope contract remain P02d-3's decision work;
  organization branding overrides and their token merge remain Phase 06.
- Cold/warm statement-count, cache-fault and rollback safety proofs belong with
  these readers. Public contracts/eligibility remain P02d-4; transport, rendering
  and the final browser/CI demo remain P02d-5, P02d-6 and P02d-7 respectively.

### P02d-3 decision package (2026-10-02)

**Accepted — 2026-10-02, verified against `0dec43b`.** The maintainer approved
the three decisions and implementation steps below before source changes. This
closes G12's cache-key part, G22, G23's accessor part and G24's internal fallback
part; it claims no implementation. Detail owners are synchronized in this first
commit. Original questions and shipped delivery records remain intact. Public
response and page-state decisions remain with P02d-4/6.

#### Verified premises and document review

The review baseline is `0dec43b` on `development`, following merged PR #23.
The required context reading, relevant module specs, localization/cache/isolation
architecture, ADR-0008/0010/0013/0038/0040/0043/0050/0051 and governing standards
were checked against the current adapters and composition roots. Two fresh
read-only review sessions independently examined cache/transaction safety and
settings/localization boundaries. A third reviewed the completed proposal and
verified no blocker or major finding. These sessions ran no tests and changed
no source.

- The four Customization writers bump the durable tenant generation inside their
  business transaction. A rollback discards that increment; a later commit can
  reuse its value. An uncommitted cache fill would then become reachable.
- The ambient unit opens the default PostgreSQL isolation level. Under
  [READ COMMITTED](https://www.postgresql.org/docs/18/transaction-iso.html#XACT-READ-COMMITTED),
  successive queries may see different committed states; a generation-first
  query alone does not prove a coherent definition snapshot.
- `ICacheService.GetOrSetAsync` has a shared factory lifetime. An ambient
  connection must not be captured by a factory that may outlive its request or
  be shared with another request. This contract uses awaited get/load/set calls.
- P02d-2's exact writer reader is uncached and purpose-aware. It remains so;
  display reads must not weaken NewBinding eligibility or change stored pins.
- G23 already rejects settings caching in P02d-2/3. Standards 20's stale TTL rows
  are corrected to a reserved, unused family; its metric spelling remains intact.
- The localization architecture, standard and `LocalizedText.Resolve` describe
  different fallback chains. G24 below reconciles them before the first caller.
- Organization branding/token merge remains Phase 06. The glossary now states
  that ownership explicitly; generic setting scope is not branding authorization.

The selected vehicles remain the ones assigned by the register: module specs,
architecture/standard details and this dated package under existing ADRs. No new
ADR is required by the accepted ambient design. A different setter, transaction
mode, access policy or cross-module mechanism requires its decision record and
maintainer approval before implementation.

#### Accepted gate answers

| Gate part | Accepted answer | Detail owner |
|---|---|---|
| G12: cache key | Cache immutable, untranslated definition families per tenant/generation; indexes inside each family use exact `(key, schema_version)`, including Active and Deprecated, excluding Draft/deleted. Never substitute Active for an unresolved pin. The writer reader stays uncached | Customization spec, Education pin invariant; architecture 32 § 8.2 |
| G22: loader and correctness | Ambient caller transaction only; fresh generation probe per batch, coherent generation/rows snapshot on a miss, mutation-scope cache bypass, bounded statements, cache-fault fallback and generation-driven freshness as specified below | Customization spec § Primary read flow; architecture 32 § 8.2; standards 10/20 |
| G23: accessor | `ITenantSettingsAccessor`, uncached and ambient; typed registered settings, explicit tenant/current-organization selection and whole-value precedence. `branding.theme` remains tenant-wide. No caller-supplied tenant/organization authority | Tenancy spec and glossary; standards 20's existing no-cache answer |
| G24: display fallback | Localization architecture owns the chain; the standard links it. Exact requested tag, progressive narrowing, exact tenant default, platform `en`, then deterministic first-authored Pattern B label. Every resolved label carries its actual locale. Nullable Pattern A display fields end absent; URL/body lookup never falls back | Localization architecture § Fallback Rules; standard 08; SharedKernel and Customization contract; public fields remain P02d-4 |

#### Customization projection and cache contract

`ICustomizationDefinitionProjectionReader` is an application
interface returning immutable values through mechanism 1 of ADR-0010. This is an
in-memory projection, not a new `public_*` table or an integration-event consumer.
It resolves a batch of exact content-type and taxonomy revision pins with one
caller-provided display-locale context. No foreign Domain/Infrastructure type,
public marker, HTTP endpoint, schema validator or compiled-validator cache is added.

Unresolved/ineligible individual pins remain identifiable as missing members of
the batch, without substituting another revision or failing unrelated members.
The API's placeholder/refusal contract, warning attribution and protected-content
eligibility remain G12/G5/G26 in P02d-4. Stored labels stay immutable in the cache;
resolved labels are produced per call, so a locale is not needed in these keys.

Two families contain all eligible revisions, including taxonomy bands. Loading
both sets together prevents N+1 work for lists and gives one coherent snapshot:

| Family | Key via `CacheKey.ForTenant` | Stable `cache.name` |
|---|---|---|
| Content types | `{tenant_id}:customization:content-types:v{generation}` | `customization:content-types` |
| Taxonomies | `{tenant_id}:customization:taxonomies:v{generation}` | `customization:taxonomies` |

The generation is a separate logical-name component passed to the existing
multi-part factory, never a hand-built separator. These templates replace
architecture 32's generation-embedded names and per-key taxonomy example in
this decision pass; the registry, metrics list and adapter mapping change together.
`TenantPageBlock` and its cache family remain Phase 04 work.

- Require a resolved real tenant and an active, correctly announced/enlisted
  transaction before every read, including cache hits. Keys come from that
  trusted context, never from a request tenant id. RLS remains effective.
- Read the durable generation afresh for every batch; do not memoize or cache it.
  A fully warm read uses one generation SELECT and no definition query.
- On any miss/fault, load generation and both eligible sets in one read-only SQL
  statement snapshot on the same connection. Use parameterized, explicit tenant
  predicates over Customization-owned tables only, following
  [Database Standards § Raw SQL](../standards/05-database.md#raw-sql).
  Use that statement's generation for both returned sets and cache fills;
  discard earlier cache hits if the probe's generation changed. Cold/partial-hit
  reads use at most two SELECT statements, independent of the number of pins.
- An absent counter with no definitions yields an empty projection; no built-in
  values are synthesized. Do not cache the absent-counter result. A nonempty
  definition set with no counter is a bounded configuration refusal and is not
  cached. A present counter with empty sets is valid.
- Before a supported Customization store mutation or generation bump, mark this
  scoped reader state dirty. Dirty or rollback-only scopes bypass cache get and
  set entirely; they can read their saved database changes through the ambient
  snapshot. The flag is sticky for the DI scope, avoiding transaction-object
  reuse and nested-frame resets. Pending tracked changes are not auto-flushed by
  the reader. A fresh scope regains normal caching.
- A clean scope can fill before its own read transaction commits because its
  snapshot contains committed Customization data only. Rolling that read back
  cannot publish speculative definitions. Fill safety must be proved for reads
  before and after nested writers and for rollback/reissued generation values.
- Await each cache operation in the caller's lifetime; do not pass the ambient
  loader to `GetOrSetAsync`, spawn work or parallelize module queries. Concurrent
  cold callers may each load the bounded snapshot; this trades coalescing for
  explicit transaction ownership without changing the cache port.
- Cache read/write faults degrade to database results with a bounded diagnostic;
  caller cancellation propagates. Database errors are not cache misses. No
  partial cache result may make a batch look complete.
- L1 TTL is 60 seconds; future L2 remains 15 minutes on Phase 11's trigger.
  Generation reads bound definition freshness, while TTLs reclaim stranded keys.
  Each instance's L1 follows the same durable counter; no event or L2 is required
  for this family's invalidation correctness.

The warm/cold statement counts are acceptance proofs, not measured latency.
The existing `< 1 ms` hit target must distinguish in-memory resolution from the
mandatory generation database probe. Record end-to-end timings and query plans
for the seeded fixture; do not claim a production p95 from a small local sample.
The family-wide load has a data-volume cost: it includes retained revisions, not
just the bounded requested pin list. Report rows/bytes with the measurements;
Phase 04's larger authoring workload re-evaluates that cost before expanding it.

#### Typed settings and display fallback contract

`ITenantSettingsAccessor` exposes typed registered reads through Tenancy contracts,
without a raw string-key/JSON export or a generic settings HTTP surface. The
production registration initially admits only the delivered `branding.theme`
palette. Reuse its grammar/contrast policy; return a typed four-color value and a
bounded absent/invalid outcome, never a partial palette or raw JSON. P02d-4 owns
safe public defaults/allowlisting; P02d-6 owns CSS injection.

For a registered setting that permits organization scope, select tenant-wide and
exact current-organization rows explicitly, excluding soft-deleted rows; no
organization context selects tenant-wide only. Merge the organization value over
the tenant value as one whole value, without deep JSON/token merge. Invalid selected
values return a typed configuration refusal, without silently adopting another
scope. Synthetic test registrations prove this generic precedence; no speculative
production setting key or organization-branded palette is added. Even a future
`app.scope = 'tenant'` hatch cannot introduce sibling overrides into this selection.
The branding registration selects tenant-wide only in every organization context.

The accessor opens no transaction, announces no context and caches neither values
nor per-scope snapshots; a supported write is visible to the next read under the
ambient database isolation. Audit and permissions matrices identify this internal
interface as unrouted, not as a new audited write. Any request used only by tests
stays test-only; any production MediatR query added must be classified Off.

For G24, add a locale-carrying resolution result to `LocalizedText` and retain the
existing string-returning API as a compatible wrapper over the same algorithm.
First-authored means canonical locale keys ordered ordinally, as the shipped
`ImmutableSortedDictionary` implements; it does not mean JSON insertion order.
Do not widen `tr` to `tr-TR` or narrow the tenant/platform default candidates
implicitly. The platform `en` candidate is for display labels only; it authorizes
no content locale. The caller supplies the tenant default once for the batch.

Pattern A rules concern optional display fields after an exact routable translation
has been found. They never locate another slug/translation/body. Required content
fields keep the authored translation; a nullable field with no permitted display
fallback remains absent. P02d-4 specifies field-level response applicability and
resolved-locale fields; P02d-6 emits the corresponding language attributes.

#### Implementation steps and required review loop

| Step | Scope and evidence |
|---|---|
| 1 | Locale-carrying fallback and uncached typed settings accessor. Unit fallback/grammar tests; app-role tenant/org/no-org selection, invalid/missing/soft-deleted settings, future tenant-scope hatch, read-after-write and no-cache proofs; register both API and Seeder roots |
| 2 | Batched exact-pin display contract and coherent ambient snapshot loader, initially uncached. Active/Deprecated versus Draft/deleted, mixed missing pins, tenant separation, empty/missing generation, no validation on read, resolved locales and coordinated generation/row race proofs |
| 3 | Generation cache families, scoped mutation bypass and metrics. Cold/warm counts, partial/cache-fault/cancellation behavior, cross-tenant warm alternation, nested write/read, rollback/reissue and independent-process freshness proofs; measured budgets, composition parity, full validation and packet closeout |

Each step: implementation and focused validation, commit on `development`, fresh
independent security/correctness/contracts/documentation review, verify findings,
fix and commit; then a second fresh review round and verified fix commits before
the next step. Models/effort follow the complexity of each review surface. No
branch change. Packet completion updates the current-state docs and delivery
record; the PR is opened for maintainer review after all three steps pass.

Relevant workflow skills are `add-integration-test`, `add-architecture-test`,
`update-glossary`, `standards-check`, `code-review`, `run-tests-locally` and
`commit-and-pr`. No new handler, entity or migration is planned; if implementation
requires one, dispatch its matching skill before the change.

At acceptance, synchronize the Customization/Tenancy specs and matrices, glossary,
architecture 09/12/32, standards 08/10/15/20 and any catalogue rows for new guards.
Preserve existing Accepted ADR bodies and dated P02d-1/P02d-2 delivery history.
README, CLAUDE and roadmap index follow the actual decision/implementation state.
The full backend validation includes Release build, formatting, positive unit,
architecture, contract and both integration populations, with zero failed/skipped
cases. Documentation checks cover links/anchors, prose width and frozen delivery
history. No frontend behavior or public endpoint changes in this packet.

#### Maintainer approval

The maintainer approved the package together:

1. G12/G22's ambient coherent-snapshot cache contract, batched exact pins,
   two family keys, dirty-scope bypass and bounded database statement counts.
2. G23's uncached typed accessor, whole-value generic scope precedence
   and explicitly tenant-wide branding registration.
3. G24's architecture-owned fallback, ordinal-first label terminal and
   actual resolved-locale metadata while retaining exact content/URL admission.

The three-step plan is part of the accepted package. This decision commit precedes
implementation, as required by the maintainer and
[implement-task Step 1](../../.claude/skills/implement-task/SKILL.md#step-1--scope-and-alignment).

### Delivery record: P02d-3

**Complete, unmerged — 2026-10-02.** The accepted decision commit is `307bbcd`.

#### Step 1: typed settings and locale resolution

Implemented `ResolvedLocalizedText` and the compatible string wrapper, plus the
registered typed settings accessor in both composition roots. The sole production
registration is tenant-wide branding; synthetic tests prove generic whole-value
organization precedence. Explicit predicates, soft deletion and ambient admission
protect reads without a cache or raw configuration export.

Release build: zero warnings/errors. Unit: 1584 passed; architecture: 184 passed;
integration: 241 passed (Docker/settings, writer/seed and Docker-free cases).
All three populations have zero failed/skipped; formatting and document checks pass.
Both independent review rounds passed; Step 2 follows. Public consumers/metadata
remain P02d-4/6.

**Step 1 review round 1.** Two fresh GPT-5.5 high sessions reviewed
`307bbcd..9293202`. No verified Blocker/Major. Two verified Minor findings were
fixed: the localization illustration used a second stale fallback helper, and
both composition roots overstated feature flags as the only module-facing read.
The illustration now calls the shipped resolver; comments describe their own
read. No behavior changed.

**Step 1 review round 2.** Two fresh GPT-5.5 xhigh sessions reviewed
`307bbcd..5848066`. Both approved the code; no verified Blocker/Major.
They independently identified the same stale delivery-status sentence above,
which is corrected in this closeout. Related current-state carriers now record
both rounds as passed. Documentation link/fragment and diff checks pass.


#### Step 2: batched coherent definition reads

Implemented the immutable exact-pin display contract and ambient loader in both
composition roots. One SQL statement returns the generation, both eligible
families and taxonomy bands using their composite revision key. Individual
invalid/ineligible pins remain missing without revision substitution or dropping
valid neighbors. Actual display locales and authored descriptor order survive;
raw schemas stay inside the loader. The writer's purpose-aware reader is unchanged.

The reader is deliberately uncached in this step. Every batch probes the durable
counter and loads the coherent statement snapshot; cache, dirty-scope behavior
and warm counts remain Step 3. The no-validation-on-read architecture guard walks
module helper dependencies and has planted direct/helper and clean controls.
Release build has zero warnings/errors; 1584 unit, 186 architecture and 21 focused
Docker integration cases pass with zero failures/skips. The real guard rejects a
planted validator dependency in the production snapshot helper, then passes after
restoration. Formatting and documentation checks pass. Both review rounds passed.
No cache implementation is claimed.

**Step 2 review round 1.** Two fresh GPT-5.5 high sessions reviewed
`4829414..77197b9`. No verified code or SQL finding; two Minor document carriers
still treated G23 or Step 1 review as pending. Both are synchronized. The root's
additional guard check demonstrated a concrete validator adapter escaped the
interface-only ban; a planted concrete probe failed before the fix and passes
with the adapter census. The full architecture suite passes after the fix.
Round 1 is complete.

**Step 2 review round 2.** Two fresh GPT-5.5 xhigh sessions reviewed
`4829414..94a84ab`. Both approved, with no verified findings. Current-state
carriers record both rounds as passed; link/fragment and wrapping checks pass.
Step 3 follows. No public consumer or cache implementation is claimed here.


#### Step 3: generation cache and read safety

Implemented the two untranslated immutable definition families, awaited cache
get/load/set and stable metric names. Every read probes the durable generation;
a miss loads both families and generation from the same statement snapshot.
Supported stores and generation bumps mark a sticky scoped dirty flag before
mutation. Dirty and rollback-only reads bypass cache get/set entirely. Cache
fault diagnostics omit private exception/key payloads; cancellation and database
failures propagate with their own meaning. L1 is 60 seconds; L2 remains Phase 11.

Release build: zero warnings/errors. Unit: 1586 passed; architecture: 186 passed;
focused Docker integration: 33 passed, including 23 projection/cache/composition
cases and ten publication-failure/concurrency cases. All have zero failures/skips.
The tests prove cold/warm statement counts, partial-hit publication coherence,
locale-neutral families, tenant alternation, independent L1 freshness, nested
write/read, store-save-before-bump, absorbed post-save refusal, rollback/reissued
keys, faults/cancellation, missing-counter admission and API/Seeder root parity.
An attempted test-only transaction reopen after rollback was correctly refused;
the proof uses a fresh scope, preserving ADR-0040's irreversible rollback-only rule.
The first full run passed unit, architecture, contract and Docker-free suites.
It exposed four missing scoped-state registrations in two legacy test fixtures;
those fixtures now supply the new state. A separate Seeder case encountered a
connection-open timeout before reaching its seeded race. All six focused fixture
and seed-race cases passed after the fix; the full Docker rerun was pending at
the implementation commit. No retry
or weakened
assertion was added. The review closeout below records completion of both rounds.

**Local seeded measurement.** The executable
`Seeded_local_measurement_records_statement_plans_payload_volume_and_end_to_end_timings`
case uses a disposable PostgreSQL database through `learnstack_app`, the full
P02d-2 seed and 20 observations after warmup. Tenant `demo-english`, generation 8:
two content types, two taxonomies and nine bands. UTF-8 JSON payload from the
coherent statement is 1984 bytes; this measures wire JSON, not managed heap size.

| End-to-end path | Minimum / median / maximum, ms | SELECT statements |
|---|---|---|
| Cold | 0.736 / 0.781 / 0.975 | 2 |
| Warm | 0.235 / 0.259 / 0.304 | 1 |
| Typed branding setting | 0.259 / 0.300 / 0.352 | 1; uncached |

**Measurement erratum — 2026-10-03.** The historical “median” column above
reported upper medians, not the average of both middle observations. The routine
is corrected; the new sample and its limits are recorded in
[PR #24 review remediation](#pr-24-review-remediation-2026-10-03).

`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` on the actual parameterized statements
uses `pk_customization_generations` for the probe and composite tenant/key/version
indexes for both families; bands use `ux_tenant_level_taxonomy_items_taxonomy_sort`.
Probe execution: 0.007 ms, two shared-buffer hits. Snapshot execution: 0.117 ms,
12 shared-buffer hits; both have zero shared-buffer reads. The snapshot's nested
band aggregate remains one SQL statement. These small local observations are not
production p95 evidence, do not isolate the in-memory `< 1 ms` target and do not
prove the cold `< 20 ms` or settings `< 5 ms` production budgets. Retained revision
volume, concurrent load and Phase 04's larger authoring workload require renewed
measurement before extending the families. No latency threshold is hard-coded
into the test.


**Step 3 review round 1.** Two fresh GPT-5.5 xhigh read-only sessions reviewed
`287318f..7ac197f`, covering transaction/cache safety and contracts/test evidence,
performance and corpus consistency. Both approved with no verified findings.
No source fix was necessary; Round 2 was pending at that milestone.

**Complete backend verification after fixture repair.** Release build: zero
warnings/errors; formatting passes. All 2637 cases passed with zero failures/skips:
1586 unit, 186 architecture, one contract, 171 Docker-free integration and 693
Docker integration. Positive TRX counters confirm `passed = executed = total` in
each suite; the full Docker rerun includes the earlier seed timeout case and
legacy fixture cases. Markdown links/fragments, diff checks and frozen P02d-1
suffix checks pass. This evidence precedes the second independent review round;
it does not mark the packet merged.


**Step 3 review round 2 and packet closeout.** Two fresh GPT-5.5 xhigh read-only
sessions independently reviewed `287318f..5b50995`. No verified code, security,
transaction, cache, performance or test finding. One reviewer identified a Minor
status ambiguity in the phase's opening P02d-2 milestones, which still described
P02d-3 as next/open. Those sentences now explicitly describe their historical
moment; the current completion note and packet table name the accepted, delivered
P02d-3 state. Current-state carriers record both rounds as passed. The verified
fix changes documentation only; the complete backend execution evidence above
still applies to the unchanged source. Final links/fragments, formatting, diff
and architecture metadata checks pass.

All three implementation steps and both review rounds per step are complete.
No new ADR, migration, HTTP endpoint, context setter or transaction mode was
required. P02d-3 is ready for PR review and remains unmerged. Phase 02d remains
in progress; P02d-4 owns the next decision pass for public eligibility, response
contracts, locale/cursor/cache rules, Off classification and read-only controls,
OpenAPI/SDK drift gates and request-level Education isolation. P02d-5/6 provide
SSR and rendering; P02d-7 provides the full-stack demonstration and exit proof.


**PR documentation correction — 2026-10-02.** After PR #24 opened, two CodeRabbit
Minor findings were verified against the current files: the Education spec still
named P02d-3 as next, and the roadmap index lacked a sentence terminator. Both
are corrected. Education now records P02d-3 complete/unmerged and P02d-4 next.
The fix changes no backend source or test; the 2637-case execution evidence
remains applicable. Final Markdown link/fragment and diff checks pass; required
CI is rechecked against the final documentation head before handoff.


#### PR #24 review remediation (2026-10-03)

The maintainer supplied two independent reviews of `8edbb032..a317d389`.
Findings were verified against current source before changes; two additional
read-only agents checked documentation scope and runtime contract claims.
No new ADR, endpoint, migration or accepted decision is introduced.

- **B1/M1/M14:** the validator guard now follows assembly-scoped references across
  every production project, including Customization Domain/Contracts and core
  helpers. Planted controls cover parameter/return/generic/event attributes,
  wrapped generics, constraints, catches, lambdas and state machines. A narrowed
  production census mutant fails on the planted Domain helper; the wrapped
  generic control failed before the IL correction. Restored source passes.
- **M2:** the old probe barrier legitimately allowed a caller to warm the other.
  The test now parks both independent snapshot loaders before either fills,
  proving exactly two SELECTs and complete values per caller without flakiness.
- **M3–M8:** recoverable cache faults still fall back with bounded diagnostics;
  fatal process exceptions propagate. Invalid setting-token values return a
  bounded failure, settings admission checks its context's enlistment, branding
  uses one canonical key, redundant cached revision fields are removed and
  malformed-pin comments describe the actual per-revision behavior. Internal
  definition refusals use neutral `lockey_invalid_value`, not a schema-extension
  message. Malformed band labels omit their entire pin and preserve neighbors.
- **D1/D2/M10–M12:** editable Scope and current-state carriers now identify the
  delivered locale/accessor/fallback work, planned organization branding and
  generation-driven L1 consistency. Frozen P02d-1 accepted answers and delivery
  record, and dated P02d-2 closeout/readiness, remain unchanged.

**Disposition of remaining suggestions.** The accepted registry is an explicit
server-owned value, not an additive DI-registration API; its replacement rule is
now documented rather than inventing a new extension mechanism. The Phase 04
`blocks-v{generation}` example is a legal, explicitly unimplemented target.
Public surface enforcement remains P02d-4's G30, not a delivered P02d-3 gate.
L2/serialization, cache-fault metrics and cold-load coalescing are not claimed by
this packet. Missing-counter detection intentionally includes Draft/deleted roots;
nonpositive generations remain invalid. Test-only non-null assertions fail loudly
if required measurement commands are absent; they do not hide a skipped proof.

**Measurement correction.** The earlier table's historical “median” values were
upper medians (the eleventh of twenty observations). The routine now averages
both middle observations. A new twenty-observation sample after warmup, from
`learnstack_app` and the same 1984-byte fixture, records:

| End-to-end path | Minimum / median / maximum, ms | SELECT statements |
|---|---|---|
| Cold | 0.598 / 0.738 / 0.973 | 2 |
| Warm | 0.184 / 0.209 / 0.275 | 1 |
| Typed branding setting | 0.190 / 0.235 / 0.292 | 1; uncached |

Probe/snapshot execution is 0.008/0.146 ms with 2/12 shared-buffer hits and zero
reads. These observations still prove no production percentile or latency budget.
The measurement case asserts statement/data invariants, not unstable timings.

**Local validation:** Release build has zero warnings/errors. Unit 1586,
architecture 187, contract 1, Docker-free integration 171 and Docker integration
697 pass: **2642 cases, zero failed or skipped**, with positive TRX execution
counters checked. Full format, Markdown links/fragments and diff checks pass.
Fresh correction review rounds follow the implementation commit; PR #24 remains
unmerged.


**Correction review round 1 — 2026-10-03.** Two fresh read-only reviewers,
GPT-6-astra and GPT-6.1-sol at xhigh effort, reviewed `a317d38..ab2a8b3` across
architecture/test proof and runtime/documentation. Both approved with no verified
findings. They ran no builds or tests; the primary executed the checks above.

**Correction review round 2 — 2026-10-03.** Two new read-only reviewers using
the same models/effort independently reviewed that range with the lenses
exchanged. One Minor was identified and confirmed by both: Standards 20's new
“other families” sentence still generalized L2 to no-L2/uncached families. The verified
fix now links the
canonical per-family policy instead. No further finding or backend change.
Required CI and CodeRabbit passed on reviewed code head `ab2a8b3`; there are no
unresolved review threads. Links/fragments, metadata and diff checks pass after
the wording fix. Final documentation-head CI is rechecked before handoff.
PR #24 remains unmerged.


#### PR #24 depth and settings correction (2026-10-03)

The maintainer's review of `8edbb032..825e4f57` identified two valid reader
defects. Both were verified against current source and reproduced through real
PostgreSQL as `learnstack_app` before the production correction.

- **Snapshot depth:** the accepted raw JSON limit remains 64. The SQL snapshot
  adds two containers around content-type schemas and four around taxonomy band
  metadata. Explicit, bounded reader limits of 66 and 68 preserve that source
  contract. No schema admission, publication, tenant predicate or cache policy
  changes.
- **Settings parsing:** `JsonException` and `InvalidOperationException` from the
  selected registration's parser return the existing bounded `validation_failed`
  outcome. An invalid organization override never falls back to the tenant value.
  Existing unsuccessful results remain refusals; cancellation and unrelated I/O
  failures still propagate.

Nine new database cases cover the two reported inputs, both raw-64 boundaries,
raw-65 write refusal, unrelated cold pins, warm reads, either partial-cache
direction, malformed setting roots/fields and non-shape parser failures. Before
the correction, the four depth and three shape cases fail at the expected parser;
the two non-shape controls pass. No Accepted ADR, migration or public API changes.

**Local validation:** Release build has zero warnings/errors. Unit 1586,
architecture 187, contract 1, Docker-free integration 171 and Docker integration
706 pass: **2651 cases, zero failed or skipped**. Positive TRX execution counters
and all nine new regression outcomes are verified. Full format, Markdown
links/fragments and diff checks pass. Two fresh independent review rounds follow
the correction commit. PR #24 remains unmerged.

### P02d-3 merge and closeout (2026-10-03)

The maintainer merged [PR #24](https://github.com/HodeTech/LearnStack/pull/24).
GitHub records final PR head `79d1539c681e5188ae57fb3af47bdf6f5049990a` and merge
commit `d1a47369d82f8b6d91cb325f56d4c42382ec77b6`. The merge contains that head and
has the identical tree; the JSON-depth/settings correction above is included.

- All five required checks pass on the
  [final PR head](https://github.com/HodeTech/LearnStack/actions/runs/37111808188).
  CodeRabbit's check succeeds and no review thread remains unresolved. The
  [merge-commit CI run](https://github.com/HodeTech/LearnStack/actions/runs/37120044890)
  also succeeds. Deferred OpenAPI/Lighthouse jobs are not executed proof.
- Local Release build, format and **2651 tests** pass with zero failures/skips:
  1586 unit, 187 architecture, 1 contract, 171 Docker-free integration and 706
  PostgreSQL integration. TRX verifies positive execution, including all nine new
  boundary/parser cases. The historical delivery totals above remain unchanged.
- The packet delivers internal exact-pin display reads, coherent snapshots,
  generation-keyed caching, typed tenant settings and locale-carrying fallback.
  Its source dependencies and approved G12/G22/G23/internal-G24 parts are complete.
  No public endpoint, authentication, browser renderer or marketplace ships here.

**Next: P02d-4 — Public read API and contract checks.** Its implementation has not
started. The [packet gate table](#packets-and-decision-gates) owns the complete
prerequisite list. Its open decisions cover unresolved bands, public response and
branding projections, entitlement/attribution, canonical locale handling, the cursor,
site data/route set, HTTP cache policy, public-surface/read-only/audit fences,
eligible-row refusals and the OpenAPI/SDK drift checks.
Publication and content access remain governed by
[ADR-0050](../decisions/0050-publication-and-course-content-access.md); protected
content cannot become anonymous merely because a row is published.

P02d-5 owns server rendering and the trusted transport; P02d-6 owns the public
renderer; P02d-7 owns the two-host demo, full-stack CI and phase exit. Phase 02d
therefore remains in progress. No new decision is accepted by this merge closeout.

**Correction/closeout review round 1 — 2026-10-03.** Fresh read-only reviewers,
GPT-6-astra and GPT-6.1-sol at xhigh effort, reviewed the source correction
`825e4f57..79d1539`, its evidence and the closeout documents. Source review has no
findings. One Minor incomplete P02d-4 gate summary was verified and fixed by linking
the canonical packet prerequisites; both reviewers approve. They inspected the
stored execution evidence and ran no builds/tests. A second fresh round follows
the closeout commit.

**Correction/closeout review round 2 — 2026-10-03.** Two fresh read-only
reviewers, GPT-6-astra and GPT-6.1-sol at xhigh effort, reviewed
`825e4f57..2276edf` across runtime correctness, isolation, parser boundaries,
regression evidence and document consistency. Both return Standards Pass and Code
Review Approve, with no actionable findings. They independently inspect the
stored TRX and live merge/check metadata; neither reruns builds/tests. The final
closeout documentation passes the corpus metadata, relative-link/fragment and
diff checks. Development remains the working branch; P02d-4 awaits its own
accepted decision package.

### P02d-4 decision package (2026-10-03)

**Status: Accepted — 2026-10-03.** The maintainer approved this complete package,
ADR-0052, its two dated clarifications, the four-step review plan and the exact
sixth required-check addition. This decision pass closes only the listed gate
parts; implementation starts after the decision/carrier commit. P02d-5/6/7 gates
remain open, and no endpoint, runtime proof or external setting is delivered by
acceptance.

#### Verified premises and document review

Reviewed against LearnStack `100bfefd6518df879fb9121499a70d7ceb6409ef`, on
`development`. P02d-1/2/3 are merged; production public controllers, a non-empty
OpenAPI snapshot and browser consumers do not exist yet.

- Foundation: guidance/skills, principles, vision/MVP scope, glossary, roadmap and
  ADR index; this phase's register, earlier delivery records, read contract,
  criteria and risks. Proposed ADR-0049/Phase 09a do not govern this packet.
- Authority: ADR-0003/0010/0017/0033/0036/0040/0044, tenant resolution and audit
  rules; API/gateway/isolation architecture and standards; factory, middleware,
  ambient UoW, transaction/audit behaviors and request-surface guards.
- Content: ADR-0008/0043/0050/0051, Education/Tenancy/Customization specs and
  matrices; locale/fallback, profile, exact-pin projection, settings and seed code;
  frontend, accessibility, errors, database, cache and performance guidance.
- Contract: ADR-0023/0024/0035, SDK source/generator/lockfile, served OpenAPI
  transformers and contract fixture; CI, CONTRIBUTING and architecture catalogue.
- Entitlement: ADR-0021/0034/0045, feature/license architecture, effective feature
  evaluation and Null provider. Hub's contract pointer, key and Starter/Growth
  plans were checked at `6adf93638e76114eda9c18a5095d0fe3c0fd43b1`, without edits.

The read-only review found these implementation-relevant premises:

- Normal organization context can come from a claim on a tenant host. Preserve
  host provenance independently; never replace the reconciliation matrix.
- Host mapping admission does not check Tenant/Organization lifecycle. Tenancy
  must own that check through an application contract.
- The existing locale writer port collapses several failures. Its contract stays
  unchanged; a typed public configuration read distinguishes valid emptiness,
  unavailable scope, unsupported locale and invalid stored configuration.
- RLS has no publication/access term. Eligible-body SQL must check the parent and
  `public` policy itself before loading a body.
- Existing PublicSurface tests prove names and selected audit exclusions. They do
  not yet prove actual methods, Off-only registration or controller write barriers.
- SDK factories returning `{}` stop compiling when generated paths become
  non-empty. Their unused placeholder authority options must not enter v1.
- The pinned generator does not implement `x-extensible-enum` unknown branches.
  New policy/state enums therefore use ADR-0024's closed-set default.
- Oasdiff defaults differ from ADR-0024. Explicit policy overrides and planted
  changes are required; a fail level alone is insufficient.

No build/test run is claimed by this document review. The implementation plan
below names the runtime proof, including real database HTTP requests and plans.

Preparation used independent authority/UoW, locale/branding and contract/CI review
tracks (GPT-6-astra and GPT-6.1-sol, xhigh). Draft rechecks verified fixes for the
Education slug owner, required-field outcomes, cursor payload, sticky rollback-only
and the first-statement ordering exception. No remaining actionable finding in
those document scopes was reported. These are preparation reviews, not the two
implementation reviews required for each step below.

The maintainer-supplied ADR review was checked against the current implementation
and Accepted contracts. ADR-0052 now states mandatory marked dispatch, limits the
runtime barrier to the enlisted transaction and names Registered guards. A focused
independent authority recheck found no remaining actionable issue. This preparation
record precedes maintainer approval; implementation proof remains pending.

Baseline fact corrections are separate in `cdca081`: the glossary follows ADR-0045
Amendment 3, and frontend architecture records the delivered P02d-2/3 theme
foundation. They do not form part of the public-read decision.

Pre-acceptance preparation validation against `100bfefd`: all eight changed/new Markdown
files
pass local link and anchor checks (975 links, 331 fragments), changed-prose
wrapping and diff hygiene.
At that preparation boundary, frozen P02d-1 records and existing Accepted ADR
files remained unchanged. Acceptance appends only the approved dated clarifications. No
runtime/build proof is substituted for the implementation evidence below.

#### P02d-4 accepted answers

| Gate part | Accepted answer | Decision/detail owner |
|---|---|---|
| G5 unresolved band | No pin: null level. Missing exact vocabulary/band: bounded unavailable level, never rebound or raw key text | This contract and Education public-read spec |
| G6(b) | Canonicalize the shipped bounded locale grammar before membership, lookup and cursor binding; no trimming | Locale contract below and localization owners; URL redirects remain P02d-5 |
| G10 codec | Resource-local, keyless, forward keyset cursors for catalog and outline; bounded, versioned, context-bound, without confidentiality or MAC claims | Pagination contract below and API/Education detail; no SharedKernel codec or signing secret |
| G12 response | Exact Active/Deprecated pins, allowlisted text descriptors and bounded unavailable content; no raw body/schema or revision rebinding | Public DTO contract below, ADR-0050/0051 and module detail; page states remain P02d-6 |
| G16(f/g) public part | Whole typed four-color theme or null; baseline colors independent of entitlement; effective WhiteLabelBranding removes attribution only | Theme contract below, descriptor and architecture/standards detail; existing Hub pointer authority replaces a duplicated matching note |
| G24 response | Exact Pattern A content; Pattern B display labels report actual authored locale through `{value,locale}` | Locale/display contract below and localization owner; renderer language attributes remain P02d-6 |
| G25 | Host-resolved site bootstrap plus catalog/course/lesson reads; no edge tenant-ID lookup or route/query/body host/tenancy selector; effective host remains ADR-0036's input | ADR-0052 and frontend/infrastructure detail; three page consumers delivered P02d-6 |
| G26 route/response | Dedicated public prefix, four logical reads with explicit GET+HEAD, bounded pageable outline and closed DTO allowlists | Public contract below, API/module specs and OpenAPI; slug grammar remains the accepted P02d-1 contract |
| G27 | No-store on all public-prefix successes/errors; no ETag, Last-Modified or 304; internal definition cache remains independent | Cache contract below and API/performance detail; ADR-0039 validator derivation is unchanged |
| G28 | Mandatory PublicSurface requests through ISender, Off-only classification, GET+HEAD equality, structural bypass detection and READ ONLY on the enlisted ambient transaction | ADR-0052, ADR-0040 Amendment 8 below and Registered catalogue rows before code |
| G29 | Shared explicit eligibility; hidden Education outcomes use identical masked not_found; credentials never widen public access | Eligibility contract below, ADR-0050, ADR-0036 Amendment 8 and Education spec |
| G30 | Required single query locale on Education, validation_failed vs unsupported_locale 400; headers cannot choose content | Locale/error contract below and standards; trusted-hop header transport remains P02d-5 |
| G31 | One served/committed snapshot, policy-complete pinned diff check, typed SDK/drift proof and sixth required live check | Contract/CI plan below, standards, workflow and CONTRIBUTING; Lighthouse remains G44 |

G5/G12/G16 renderer parts, G6(c), G30 header transport and G32–G45 retain their
named later packet boundaries. No marketplace or enrollment gate is reopened.

#### Public routes and DTO contract

All paths are relative to `/api/v1/public`. Each explicitly supports GET and
HEAD: **four logical read contracts, eight method/path OpenAPI operations**.
Every institution public-data endpoint dispatches a `[PublicSurface]` MediatR
request through `ISender`; its data access uses the enlisted read-only ambient
transaction. ADR-0052 owns that mandatory boundary, its bypass limits and proposed
guard names. Structural controls detect bypasses; real HTTP/database tests prove
the dispatched runtime path. No direct persistence path through a controller,
minimal API handler or helper is admitted.

| Path | Query parameters | Response |
|---|---|---|
| `/site` | None | `PublicSite` |
| `/courses` | Required `locale`; optional `cursor`, `limit` | Catalog page |
| `/courses/{slug}` | Required `locale`; optional `lessonCursor`, `lessonLimit` | Course detail and optional outline page |
| `/courses/{slug}/lessons/{lessonSlug}` | Required `locale` | Lesson detail |

No host, tenant, organization, actor, filter, search or sort selector is published
in route, query or body. The effective host still follows ADR-0036's direct-request
and authenticated trusted-hop rules.
Unknown query parameters and repeated single-valued parameters are rejected with
bounded `400 validation_failed`. Slugs retain the accepted
[Education slug grammar](../standards/08-localization.md#education-slug-grammar):
1–160 characters, with UUID N/D spellings excluded. An invalid slug returns
`400 validation_failed` naming `slug` or `lessonSlug`; do not synthesize a slug.

DTOs are purpose-built public contracts; never reuse seed or internal projection
DTOs. Names below also specify the serialized allowlist:

- `PublicSite`: `displayName` (Tenant's display name), `enabledLocales`,
  `defaultLocale`, `theme`, `showPlatformAttribution`. A successful bootstrap has
  at least one enabled locale and exactly one enabled default. Locale order uses
  configured sort then canonical tag as a deterministic tie-breaker.
- `theme`: either null or exactly `primary`, `background`, `foreground`, `muted`
  from the validated typed whole value. Null means the renderer's safe CSS
  defaults; do not copy another palette into backend DTO code.
- Course summary: `slug`, `title`, nullable `summary`, `contentAccess`, nullable
  `level`. No root identifier, timestamp, version, scope or authoring pin is needed.
- Level: `{state:"ready",label:{value,locale}}` or
  `{state:"unavailable",label:null}`. An absent optional pin gives `level:null`.
- Catalog: `{locale,items:[course summary],pageInfo}`. Use the shared PageInfo
  field vocabulary, without pretending backward navigation exists.
- Course detail: `{locale,course,alternates,lessons}`. Course is the summary above.
  Alternate items contain only `{locale,slug}`. Restricted courses return
  `lessons:null`, with no count, title, slug, order, descriptor or truncation flag.
- Public-course outline: `{items:[{slug,title,sort}],pageInfo}`. It contains no
  body, definition or count. A public course without eligible lessons has a real
  empty page, distinct from restricted `lessons:null`.
- Lesson detail: `{locale,course:{slug,title},lesson:{slug,title},alternates,content}`.
  Alternate items contain `{locale,courseSlug,lessonSlug}`; both translations must
  exist in that enabled locale. Exclude the current locale and order consistently.
- Ready content: `{state:"ready",rendererKey,label:{value,locale},fields}`.
  `fields` are descriptor-ordered `{name,label:{value,locale},value}` plain strings
  from the selected supported text-card profile. Missing optional fields are
  omitted; unknown body properties are omitted. Missing required fields,
  non-object stored body and selected non-string values make the whole content
  unavailable. Derive requiredness as internal presentation metadata from the
  admitted schema's `required` list; carry it through Customization's field
  projection without exposing a public required flag or evaluating JSON Schema.
  No coercion or schema evaluation occurs on read.
- Unavailable content: `{state:"unavailable"}`. Missing/deleted/Draft exact pins,
  malformed definitions, unsupported renderer/profile, zero presentation fields
  or invalid selected values expose neither body nor descriptor, and never use
  another revision. Record bounded operator diagnostics without payload values.

Policy values `public`/`enrollment_required` and state values `ready`/`unavailable`
are closed enums. Additions and removals are breaking under ADR-0024. `rendererKey`
is a string; `default-card` is the currently supported value, not a closed future
renderer registry. Rendering an unknown key remains G41's bounded fallback.

Recursively exclude tenant/organization/actor IDs, root IDs/versions, audit columns,
settings keys/raw JSON, JSON Schema, arbitrary body objects, taxonomy metadata,
internal pin/revision/status/generation data and unpublished translations. A cursor
is the separately documented protocol exception: it contains seek identifiers,
not exposed authority or a secrecy guarantee.

#### Host authority, eligibility and public errors

The normal resolved context and host ceiling both apply. A nullable immutable
`HostScope` contains host TenantId and OrganizationId, constructed only by
`TenantContextFactory` from the attempt's existing host signals after its current
agreement/membership gates. Claim-only, ambient and unresolved contexts have none.

Every public request requires a real matching host scope. Explicit host predicates
intersect with normal EF filters and RLS: tenant host means tenant-wide rows only;
organization host means tenant-wide plus that organization's rows. An org claim on
a tenant host cannot expose org content; a platform-host claim cannot turn that
host into an institution public site. Keep the factory independent of routing.

The API hashes the normalized effective host from `HostClassification` for cursor
binding. Only the digest enters the query's pagination input. Do not put the host
string into the resolution attempt/context, public response or retained logs.

Tenancy's typed internal configuration contract checks Tenant and, where present,
Organization under the current announced scope, on the same connection. Serve only
nondeleted Trial/Active tenants and nondeleted Active organizations. Missing,
Suspended, Archived or deleted scope gives generic `404 not_found`. This explicitly
selects Suspended behavior for public content; it is not an existing resolver check.

Eligible course marketing requires matching scope, published/nondeleted course,
enabled canonical locale and its exact translation. Eligible lessons additionally
require published/nondeleted child, URL-parent membership, exact translation and
parent `content_access = public` before inventory, body or descriptor reads.
The protected-body SELECT includes all those parent/access conditions itself.
READ COMMITTED does not promise linearizable revocation across later statements.

Nonexistent, draft/deleted, wrong-parent, untranslated, sibling-org, other-tenant
and restricted lesson outcomes share one `not_found` body after masking dynamic
`instance`/`correlationId`. Hidden course outcomes likewise match a nowhere slug.
Credentials do not produce grants or unlock this anonymous contract. Preserve
existing resolver outcomes: platform host retains HTTP 404 `tenant_mismatch`,
unknown host HTTP 404 `not_found`, and assertion/claim failures are not bypassed.

Valid no-locale/all-disabled configuration remains representable internally. It
returns bootstrap `404 not_found` because no navigable public site is configured;
every well-formed Education locale is unsupported. Do not synthesize `en`.
Malformed stored tags or enabled locales without exactly one enabled default give
generic `503 dependency_unavailable` plus bounded internal diagnostics. Missing or
invalid theme alone yields `theme:null`, not a site failure. Unexpected database
faults retain the existing exception path; do not swallow cancellation or all errors.

Success is 200; business/input errors publish 400/404/503 as applicable. Document
framework 405/429 and the existing unexpected 500 path in the shared Problem
Details component. HEAD has identical admission, status and applicable headers
with no response body, including errors. Tests observe the documented HTTP outcomes.

#### Locale and display applicability

Site bootstrap takes no locale. Each Education read requires exactly one `locale`
query value. Missing, empty, repeated, malformed or longer-than-35 values return
`400 validation_failed` with `errors.locale`, before opening the ambient read
transaction. Do not trim whitespace or extend the accepted LocaleTag grammar.
Canonicalize valid case variants before membership, SQL and cursor binding.

Well-formed absent/disabled tags return uniform `400 unsupported_locale`, including
when historical translations remain stored. Enabled but untranslated gives an
empty catalog, detail 404 and omitted outline/alternate entries. Neither `X-Locale`
nor `Accept-Language` selects content. P02d-5 owns how its transport sets headers.

Pattern A title, summary, slug and body are exact requested-locale data; a null
summary remains null. Pattern B labels use the existing single-owner chain in
[Localization § Fallback Rules](../architecture/12-localization.md#fallback-rules),
and carry the actual authored locale. A fallback label never authorizes reading
another content translation. Resolve the validated tenant default once per request
and reuse it in batched definition reads; do not evaluate JSON Schema on read.

#### Catalog and outline continuation

Use resource-local keyless codecs, not a new SharedKernel codec or signing-key
lifecycle. Catalog order is `(created_at ASC,id ASC)`; outline order is
`(sort ASC,id ASC)`, matching the existing lesson invariant. Seek/fetch `limit+1`
eligible rows using PostgreSQL timestamp precision and UUID ordering.

Both page sizes default to 20, reject malformed/non-positive/repeated input and
clamp above 100. `pageInfo` is `{nextCursor,previousCursor:null,hasNext,
hasPrevious:false}`; nextCursor exists only when an additional eligible row exists.
Outline continuation stays on the course endpoint, so every lesson is reachable.
Restricted courses never produce an outline cursor or touch lesson inventory.

Codec version is **1**. Encode UTF-8 JSON using canonical unpadded base64url, with
at most 1024 encoded ASCII characters and 768 decoded bytes; no compression.
Catalog payload has exactly `{v,scope,createdAt,id}`; outline payload has exactly
`{v,scope,parent,sort,id}`. UUIDs use canonical lowercase D format and are nonempty;
outline sort is a nonnegative Int32. `createdAt` is a representable UTC timestamp
with exactly six fractional digits (`yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'`), preserving
PostgreSQL microsecond precision. No token logs or input reflection.

`scope` is lowercase hex SHA-256 of a UTF-8 JSON array in this exact component
order: `[1,endpoint,hostDigest,tenantUuid,organizationUuidOrNull,locale,order]`.
UUIDs/locale are canonical, null remains JSON null, endpoint is `catalog` or
`course-outline`, and order is `created_at:id:asc` or `sort:id:asc`. `hostDigest` is
SHA-256 of UTF-8 normalized effective host, represented as lowercase hex. Tagged
components avoid concatenation ambiguity. Both scopes come from the preserved
host ceiling, not a claim-selected organization. Outline `parent` must match the
resolved eligible course UUID. Page size is excluded so it can change. Decoded
identifiers are continuation data, never selectors.

Reject noncanonical encoding, excessive length, duplicate/unexpected fields,
wrong types, unknown version, invalid UTC timestamp/order/UUID, truncated data and
fingerprint mismatch with bounded `400 validation_failed` naming `cursor` or
`lessonCursor`, never reflecting the token. Validate syntax and known host/locale/
endpoint binding before transaction entry; check outline parent binding after the
eligible parent resolves, without loading any body or outline before refusal.

The fingerprint is not authenticated. A deliberately edited, otherwise valid
anchor may skip/repeat within the requesting host's independently eligible set;
it cannot enlarge it. Do not promise tamper rejection, confidentiality or stable
snapshot pagination. No anchor-row lookup is required. Earlier additions are not
rediscovered, later eligible additions can appear and deletes can shorten a page.

#### Branding, entitlement and cache boundary

Resolve theme through the existing typed accessor; it remains tenant-wide even on
an organization host. Null/invalid whole theme uses `theme:null`, never partial
colors. P02d-6 owns safe CSS injection and the existing frontend default palette.

Baseline validated colors apply regardless of plan. Derive only
`showPlatformAttribution = !IFeatureFlags.IsEnabledAsync(WhiteLabelBranding)`;
do not expose flags, inspect provider storage or contact Hub directly. Null provider
grants hide attribution; false/omitted/degraded effective grants show it. Existing
authoring and provider/overlay failure contracts are unchanged.

G16's vehicle is explicitly reconciled with Hub's pointer-only contract policy:
core owns the first-consumer meaning; Hub's existing contract pointer is the
matching authority, not a second duplicated rule. Its shipped key/boolean and
Starter/Growth values need no wire or endpoint change. No sibling branch is edited.
The observed Hub degraded-summary drift is a separate existing documentation
correction owned by Hub before Phase 02c's real provider consumer, under its own
branch authorization; it neither changes current Null behavior nor blocks P4.

Set `Cache-Control: no-store` at the public-prefix boundary before middleware can
short-circuit, including 200/400/404/405/429/503 and existing exception responses.
Do not emit ETag or Last-Modified, expose versions or honor If-None-Match/304.
No Education response or site bootstrap cache is introduced. Internal generation
cache remains independent; it cannot decide publication, access or locale admission.
API/performance standards need a scoped exception, without changing write validators.

#### Accepted ADR clarifications

The maintainer approved the following clarifications with ADR-0052 on 2026-10-03.
They are appended to ADR-0036 and ADR-0040; existing bodies remain unchanged.
ADR-0052 owns the new public decision and bounded read-only setup-order exception;
the amendments disclose its composition rather than authorizing it independently.

**ADR-0036, Amendment 8 — public host provenance (2026-10-03).**
The Decision and reconciliation matrix are unchanged. P02d-4 needs to distinguish
the host's public scope from a valid context narrowed/selected by claims. Under
ADR-0052, `ITenantContext.HostScope` is nullable and immutable. Only
`TenantContextFactory` constructs it from existing `HostTenantId` and
`HostOrganizationId`, after all existing reconciliation/membership checks succeed.
Claim-only, unresolved and ambient contexts carry null. No host string, route or
new lookup enters `TenantResolutionAttempt`; no context-accessor writer or SQL
setter is added. Public query admission requires real matching host scope and
explicit host predicates intersect with normal filters/RLS. Row 7 cannot expose
organization content through a tenant host; row 14 has no institution host scope.
Claims/assertions retain their refusals and never widen the anonymous response.
Carrier updates: API/security standards, context/gateway/isolation architecture,
frontend standards, Education/Tenancy public specs, glossary and catalogue; the
phase package owns the concrete public route/eligibility contract.

**ADR-0040, Amendment 8 — read-only public frames (2026-10-03).**
Connection/transaction ownership, shared module contexts, the tenant setter set
and default writable behavior are unchanged. ADR-0052 authorizes a bounded
public-only exception to the Decision sketch's first-statement ordering; this
amendment does not authorize it independently. ADR-0052 adds
explicit ReadOnly/ReadWrite intent to `BeginTransactionAsync`; existing callers
remain ReadWrite by default. TransactionBehavior chooses ReadOnly for PublicSurface
requests before dispatch. A physical ReadOnly owner opens READ COMMITTED, executes
and awaits `SET TRANSACTION READ ONLY`, then returns its owner frame. The existing
tenant/organization announcement follows before any data SQL or handler dispatch;
that mode-control statement is its only permitted predecessor. Default writers
retain first-statement tenant announcement. Begin cleans up its partial transaction
on setup failure/cancellation, marks rollback-only and rethrows before returning a
frame; it cannot rely on TransactionBehavior's later cleanup try block.
A same-mode join uses existing frame ownership. A
mixed-mode join is refused before invoking the inner handler and marks the unit
rollback-only even if an outer caller absorbs it. It never promotes/demotes the
existing transaction or opens a second connection. Direct application-contract
reads inherit the ambient mode. READ COMMITTED stays unchanged. Mode belongs to
one physical transaction and resets on completion/rollback/disposal; no pooled
session state leaks. This never clears sticky rollback-only poisoning: successful
read-only commit permits a later writer on the same reusable unit; owner rollback,
cancellation or mixed-mode failure requires a fresh unit/scope. Verify pooled
connection mode reset separately. Normal Off reads write no audit row; independent
rejection audits retain their sanctioned path. PostgreSQL app-role proofs and
planted guards cover EF/SQL write attempts, both nesting directions, cancellation,
rollback and a later writable transaction on a valid unit, with poisoned-unit
refusal controls.
Carrier updates: backend/database/security standards, request-pipeline/isolation
architecture, module specs and catalogue.

#### OpenAPI, SDK and required-check plan

Use `backend/openapi/v1.json` as the only committed v1 snapshot. Contract tests
compare it with the production composition root's served `/openapi/v1.json`, without
test probe controllers. Canonical comparison may ignore object-key order, not
arrays, schemas, paths or response content. Assert all eight operations as a
non-empty positive control, closed DTO reachability and complete Problem Details.

Pin oasdiff **v1.33.0**, commit `322d7ae815e5fb80f415bd4e8ef16d4b30cd8bdf`,
Apache-2.0. Verify the fixed Linux AMD64 release archive before extraction/execution
against SHA-256 `43a4e328e2d13ba1552d760aa68d2485c75c5621f309f6ff64ae895188345247`,
then assert its version; no latest-tag or unpinned installer fallback. Sources:
[pinned licence](https://github.com/oasdiff/oasdiff/blob/322d7ae815e5fb80f415bd4e8ef16d4b30cd8bdf/LICENSE),
[archive/checksums](https://github.com/oasdiff/oasdiff/releases/download/v1.33.0/checksums.txt).
Use `breaking --fail-on WARN` with an explicit checked-in severity file
for ADR-0024; promote request-field removals, optional/required response-field
removals and response enum removals/additions as policy requires. Commit fixtures
for every representable ADR-0024 breaking row and nonbreaking controls; any tool
gap needs a deterministic companion check, not a silently excluded policy row.
Validator/status behavior absent from OpenAPI needs named runtime tests. Official
[severity configuration](https://github.com/oasdiff/oasdiff/blob/v1.33.0/docs/BREAKING-CHANGES.md#customizing-severity-levels)
and [release](https://github.com/oasdiff/oasdiff/releases/tag/v1.33.0) are supporting
sources, not claims that the repository has installed or run it.

At minimum set these exact checks to `err`: `request-property-removed`,
`response-optional-property-removed`, `response-required-property-removed`,
`response-property-enum-value-added`, `response-property-enum-value-removed` and
`response-mediatype-enum-value-removed`. Pinned
[check registrations](https://github.com/oasdiff/oasdiff/blob/322d7ae815e5fb80f415bd4e8ef16d4b30cd8bdf/checker/rules.go)
make the optional field and enum-removal overrides necessary. Existing extensible
enum policy is not automatically supported by this generator/tool pair; when an
open response enum first ships, its consumer/compatibility companions must close
that gap. This packet's public response enums remain closed.

Diff PR base SHA's snapshot against head. Push uses the push-before SHA; manual
runs use an explicit base ref. A first-baseline exception is permitted only when
the verified base has no v1 operations and no committed snapshot. Fetch/read errors,
missing snapshots after bootstrap or deleted head snapshots fail. Failure artifacts
include both specs, SHAs, tool/version/policy and readable/machine reports.

Regenerate from that file through `LEARNSTACK_OPENAPI`; keep the lockfile's
openapi-typescript 7.13.0. The SDK root exports generated types only. Replace the
unused `/server` stub with a thin typed public GET transport accepting an injected
Fetch-compatible transport; operation/request/response types derive from generation.
The supplied transport resolves relative SDK URLs; there is no global-fetch default
or implied working server hop. Parse errors as unknown, validate Problem Details
and project them into the existing closed AppError union including its unknown
branch, ignoring unrecognized extension members rather than trusting an unchecked
cast. Fake-transport tests cover the four-route inventory, encoded parameters,
absence of authority headers, success/errors, unknown codes and malformed bodies.
Remove unused `/client` factory/export. Do not add a runtime client library, tenant
ID option, header authority, hop secret or frontend request-header lookup here.
G35 selects the trusted server adapter/options in P02d-5.

Provide four logical GET wrappers and a typed success/Problem Details envelope;
malformed JSON, transport failures and caller cancellation stay distinct from a
valid API error. Tests cover exact URL/query construction and generated typing.
P4 proves SDK operation coverage and drift; P5 supplies the first server consumer;
P6 proves every logical GET has a public-page consumer. HEAD is the HTTP companion,
not an invented browser JSON consumer. The phase's consumer criterion follows
this split.

Replace the deferred CI placeholder with always-running **`openapi diff`**, removing
ENABLE_OPENAPI_DIFF and its deferred suffix. Add snapshot-based SDK regeneration/
scoped generated-file drift to the required frontend job. Keep Lighthouse under G44.
After a real job runs, add `openapi diff` with GitHub Actions attribution (15368) to
live `main` protection, preserving the five current checks, strict=true and all
other settings. The maintainer approved this exact external setting change on
2026-10-03;
verify it by API read-back and record the date/list, without claiming an edit here.

#### Implementation steps and two-round review loop

First commit the Accepted decision records and carrier updates. Then implement on
`development`, without another branch or worktree:

1. **Authority and read-only foundation.** Factory host provenance, public admission,
   explicit UoW mode/setup/nesting/reset, pipeline selection and non-vacuous
   structural companions. Validate factory/matrix controls, existing writer/audit
   behavior and app-role READ ONLY EF/SQL refusal with writable/mixed-mode controls,
   mode active when Begin returns and setup failure/cancellation cleanup.
2. **Public site and HTTP boundary.** Tenancy configuration contract, live-scope and
   locale/configuration decisions, typed theme/effective attribution, site endpoint,
   explicit HEAD, no-store and shared Problem Details. Validate production middleware
   plus real database, safe fallback and swapped-provider controls.
3. **Education reads and continuation.** Central eligibility, three queries, exact
   translations, catalog/outline cursors, descriptor-ordered lesson projection and
   alternates. Add internal requiredness to Customization presentation fields;
   prove optional omission, required absence and non-object/non-string refusal
   without a schema validator. Validate all leakage/masking/locale/pagination cases
   with positive controls; inspect real app-role EXPLAIN plans for consumer query
   shapes and record representative cold/warm measurements with their limits.
4. **Contract/SDK/CI and closeout.** Snapshot equality/allowlist, breaking-policy
   mutation fixtures, generated types/typed transport/drift, CI activation and
   approved protection update; final documentation and required PR evidence.

For **each** step: implement and commit; first independent multi-dimensional agent
review; verify findings, fix and commit; second fresh review; verify/fix/commit.
No empty fix commits. Choose model/effort by risk: authority, UoW, isolation and
policy reviews use the strongest available deep reviewer; contract, tests and
corpus get an independent implementation/docs reviewer. Re-review material fixes.
Proceed automatically to the next step unless a new approval boundary blocks it.

Required packet evidence includes Release build/format, unit/architecture/contract,
Docker-free and Docker integration with no empty selection/skips; frontend checks;
snapshot regeneration/SDK drift; policy companions; changed-document link/anchor
checks and diff hygiene. Record actual counts/results only after execution.

The behavioral inventory includes lifecycle/no-locale/invalid-default outcomes;
locale casing, 35/36 bounds, whitespace/newlines, repeated/missing/disabled values
and header invariance; exact fallback locale; parent/child policy/publication/
deletion and cross-host/org masking; zero protected reader invocations; full catalog
and outline walks/ties/deleted anchors/restart/scope mismatch; GET/HEAD/error/no-store
and If-None-Match; zero normal audit writes; warm-cache policy controls; served
snapshot extra/removed operations and recursive forbidden fields. Use distinct
allowlisted fixture values when public IDs are intentionally absent.

#### Acceptance carrier actions and authorization

Acceptance updates the following owners together, before code:

- ADR-0052/index and approved dated ADR-0036/0040 amendments; this register/status,
  packet-specific criteria and Registered catalogue obligations. Preserve frozen
  P02d-1/2/3 decisions and delivery history.
- API, backend, database, security, frontend, localization, error, performance,
  infrastructure and architecture-test standards/index; derive changed nontrivial
  rules from the accepted ADRs. Distinguish Registered proofs from implemented ones.
- Frontend, request pipeline, tenant isolation, localization, gateway, feature flags,
  hybrid licence and customization architecture; replace public edge-ID lookup,
  clarify query locale, exact display applicability, attribution and no-store.
- Education/Tenancy/Customization spec, audit/permission matrices and glossary;
  public descriptors are separate from internal read/seed DTOs. Fix stale
  ADR-0048-only access language and premature public-ID/consumer criteria.
- CI/CONTRIBUTING/SDK detail: record approved activation and later execution; do not
  label a proposed job, transport, endpoint or required setting delivered.

Maintainer approval covers this package, ADR-0052, the two appended clarifications,
four-step implementation/review plan and the exact sixth-check protection addition.
The gates listed above are now Accepted; later packet parts retain their owners. No new
schema, infrastructure adapter,
marketplace direction, protected learner access or renderer decision is implied.

**Acceptance validation — 2026-10-03.** Against `cdca081`, 39 changed/new Markdown
files pass 2576 local-link and 538 fragment checks. Frozen P02d-1/2/3 decisions and
delivery records are unchanged; the existing ADR-0036/0040 bodies are byte-identical
after removing the two appended clarifications. Five CorpusConsistencyTests pass
in Release with zero failures/skips; diff hygiene is clean. Independent security,
locale/branding and contract/corpus checks found no decision change needed;
verified wording/table findings were corrected. This is acceptance validation,
not implementation delivery or either review round of an implementation step.

#### P02d-4 Step 1: authority and read-only foundation

**Implementation — 2026-10-03.** Factory-only immutable HostScope retains the
host tenant/organization independently of claim-selected scope. Public admission
requires matching real provenance; existing unmarked origins and resolution
inputs retain their contract. An explicit transaction mode defaults existing
callers to ReadWrite. PublicSurface selects ReadOnly before dispatch; mode setup
completes before Begin returns. Same-mode joins preserve ownership; mixed-mode
attempts poison the outer unit before handler invocation. Partial setup failure
or cancellation cleans up immediately. Physical completion resets mode, while
rollback-only remains sticky and successful read-only completion permits reuse.

The HostScope construction guard has mutable/public/second-site planted controls.
Real app-role PostgreSQL cases exercise EF/raw SQL refusal, identical writable
controls, same/mixed-mode nesting, setup faults/cancellation and successful/fresh
scope reuse. The existing HTTP isolation probe now proves the read-only barrier;
a separate writable frame retains WITH CHECK refusal and an own-tenant positive
control. No production endpoint, eligibility reader or public response is shipped
in this step; Steps 2–4 retain those obligations.

**Pre-review verification.** Release build has zero warnings/errors. All 2672
.NET tests pass with zero failures/skips: 1598 unit, 189 architecture, one contract,
171 Docker-free integration and 713 Docker integration. Format and changed-document
link/anchor checks pass. At this pre-review boundary both review rounds were
pending; completion is recorded below.

**Review round 1.** Independent GPT-6-astra and GPT-6.1-sol xhigh reviewers
examined `f79e4a4..77b13f5`. Neither found a production defect or Blocker/Major.
Verified Minor findings: two editable current-state carriers still described
Step 1 as pending, and the driver-log fault injection ran before Npgsql sent BEGIN.
Carrier wording now separates delivered foundations from Steps 2–4. The original
pre-send cases remain, with corrected wording; two additional cases observe server
read-only mode and inject fault/cancellation from the driver's safe ReaderClosed
callback before Begin returns. They assert cleanup before DI-scope disposal and
sticky poisoned-unit refusal. The pinned-driver reflection seam exists only in
tests and fails loudly if its members change. No production test hook is added.

**Round 1 fix verification.** All nine transaction-mode cases pass with zero
failures/skips, including both post-server setup failures. Five corpus consistency
cases pass. A clean CI=true rebuild (rather than an incremental cached build)
exposed CA1852 on the deliberately unsealed planted offender. Its narrow, explained
test-only suppression preserves that offender; the clean Release rebuild then
passes with zero warnings/errors. Production code is unchanged by these fixes.

**Review round 2.** Fresh GPT-6-astra and GPT-6.1-sol xhigh reviewers
examined `f79e4a4..1020611`. No Blocker/Major was found. A double-marked request
could pass unresolved admission without HostScope; no production request uses
that combination. PublicSurface now refuses that combination under unresolved
context, with a planted unit control, preserving ordinary provisioning and the
existing tenant_mismatch refusal. The HTTP isolation class summary now separates
its read-only refusal from the writable WITH CHECK controls. All 21 focused
admission unit cases pass with zero failures/skips. The contracts reviewer also
independently ran a clean Release build (zero warnings/errors), 189 architecture,
63 focused units, nine mode cases and the changed HTTP control on `1020611`, all
passing with zero failures/skips. A fresh GPT-6-astra xhigh focused review of
`1020611..248b5e3` found
no remaining actionable issue. Step 1 and both review rounds are complete.

#### P02d-4 Step 2: public site bootstrap

**Implementation — 2026-10-03.** The internal typed Tenancy configuration port
checks live tenant/mapped-organization lifecycle on the announced/enlisted
read-only transaction. Valid locale emptiness stays distinct from invalid stored
configuration. Site GET/HEAD dispatch a marked Off query through ISender; they
project only the approved display name, sorted enabled locales, default locale,
whole validated theme or null and effective attribution. No caller scope selector,
new permission, generic settings route or palette is added.

An early public-prefix response policy covers successes and all errors, including
routing, rate limiting and exception handling: no-store, no validators and no HEAD
body. Structural Off, independent action dispatch/method and transitive controller
persistence guards include offending and clean controls. A production-composition
HTTP fixture uses real writer seeds and the application database role; it observes
server read-only mode and non-bypass role before each configuration read. Cases
cover lifecycle withdrawal after warm host resolution, locale order/emptiness,
invalid configuration, uncached whole-theme/null, attribution provider outcomes,
query refusal before data, no normal audit rows, exact endpoint inventory and
GET/HEAD error/no-store behavior. Education and contract/SDK/CI remain Steps 3–4.

**Pre-review verification.** A clean CI=true Release build with --no-incremental
passes with zero warnings/errors. All 2710 .NET cases pass with zero failures/skips:
1599 unit, 195 architecture, one contract, 171 Docker-free integration and 744 Docker
integration. TRX counters retain each suite separately and agree with the runner.
After strengthening token/case/action-suffix route discovery and its controls, the
clean build and all 195 architecture cases pass again; all 29 focused site HTTP
cases pass. Formatting, diff hygiene and 1540 local links/400 fragments across the
15 affected Markdown files pass. Existing ADR-0052 text and frozen P02d-1–3 records
remain intact. At this implementation boundary both review rounds were pending.

**Review round 1.** Independent GPT-6-astra and GPT-6.1-sol xhigh reviewers
confirm the production site behavior and identify two structural guard gaps.
The dispatch check independently found a marked construction and a Send call;
it now verifies the actual directly constructed argument, permits exactly one
Send and refuses unverifiable origins. Discarded marked, mixed-send and variable
controls fail. The persistence walk now follows production implementations of
reachable interfaces and abstract helpers; planted independent-connection
helpers fail through both shapes. The testing standard's endpoint status is
aligned. The clean Release build has zero warnings/errors; all six focused
public guard cases pass. A fresh second round follows the correction commit.

**Review round 2.** Fresh GPT-6-astra and GPT-6.1-sol xhigh reviewers find no
production site defect. Their probes expose generic helper references that
compare closed instances against open definition keys, and a clean direct Send
followed by an unmarked helper Send. The walk normalizes generic definitions and
refuses sender acquisition in reachable non-controller helpers. Safe-return
interface and abstract probes now isolate implementation traversal, including
closed generic and clean controls. The helper-Send regression fails before the
fix and passes afterward. Generated action state machines remain part of their
owner instead of separate sender-acquiring helpers; an initial full-suite run
caught that false positive and the subsequent correction restores the clean
production control. Lambda extra-Send controls also fail. Current theme and
performance carriers record Step 2 delivery. A fresh focused review checks these
final corrections.

**Focused fix verification.** The fresh review identifies an additional private
controller method that sends after the clean marked action. A controller-wide
Send census now equals the approved HTTP action count, while each action still
proves its actual marked argument. A planted private helper fails the control.
The follow-up control also gives the private helper a GET attribute: private
methods are not MVC actions and cannot offset an extra Send. This regression
fails before the public-method filter and passes afterward.

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
shipped before this phase. At phase entry, neither seed tenant held a locale row
and `Tenant.AddLocale` and `SetDefaultLocale` had no caller outside tests.
[ADR-0042](../decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)
requires locale rows to be written by their own command in their own transaction: the
one raising `tenancy.locale.write` in
[the Tenancy audit matrix](../modules/tenancy/audit.md). P02d-2 delivered those
commands and seeded locale configuration; see its [delivery record](#p02d-2-implementation-delivery-2026-10-02).
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

P02d-3 delivered **G12**'s cache contract, the loader/correctness contract (**G22**)
and internal display fallback (**G24**) under the
[accepted gate answers](#accepted-gate-answers). Public response and page-state
parts remain P02d-4/6.

**The typed settings accessor** over `tenant_settings`, which Phase 02a left to its
first reader, is delivered in P02d-3. Under Row Level Security a `tenant_settings`
read admits tenant-wide rows plus the caller's organization's rows, so its result
depends on `app.organization_id`, and the policy's tenant-scope read has no carrier
until
[Phase 03](phase-03-identity-admin.md)
([Security Standards § Tenant Context](../standards/11-security.md#tenant-context));
the accessor selects organization-over-tenant whole-value precedence only for
registrations that permit it; `branding.theme` stays tenant-wide. Seed writes run
in their own process, so **G23** selects the uncached, ambient `ITenantSettingsAccessor`
under the
[accepted typed settings contract](#typed-settings-and-display-fallback-contract).

### Read API

**P02d-4 Accepted — 2026-10-03; implementation pending.** The
[accepted public contract](#public-routes-and-dto-contract) fixes four logical reads
under `/api/v1/public`, each GET/HEAD: site bootstrap, course catalog, course detail
with pageable outline, and lesson detail. The DTO allowlists, alternates and cursor
codec are fixed before the first v1 snapshot; they expose no raw settings, schema,
body document or resource IDs. Pattern A fields use exact enabled query locale;
Pattern B display labels report their authored locale. Headers cannot select content.

[ADR-0052](../decisions/0052-anonymous-public-read-boundary.md) requires marked
MediatR requests through ISender, audit Off, factory host ceiling and READ ONLY
ambient frames. Host predicates intersect normal context/filters/RLS. Tenancy owns
live scope and locale configuration through an application contract. Education owns
[publication/access eligibility](../modules/education/README.md#primary-read-flow)
under ADR-0050: hidden rows are masked not_found, and eligible parent/public policy
is checked before inventory, body or descriptor loading. Exact revision pins never
rebind to a successor.

The public prefix uses no-store on successes/errors, without validators/304 or a
site/Education representation cache. Internal definition caching remains independent.
The [locale contract](#locale-and-display-applicability),
[pagination contract](#catalog-and-outline-continuation) and
[contract/SDK/CI plan](#openapi-sdk-and-required-check-plan) own the detailed decisions.
Production marker/table rows land with their request types; acceptance leaves the
currently empty set and implemented guard coverage honest. New guards/proofs are
Registered before code; runtime verification and real consumer query plans are
required before packet exit. P02d-5 owns trusted server transport; P02d-6 owns pages.

### Public renderer

From [Phase 06](phase-06-renderer-admin-studio.md), in `frontend/apps/web` under the
`(public)` route group, P02d-6 implements the accepted three-page path: catalog, course
detail with a
pageable ordered outline, and lesson. P02d-4 delivers their API contracts; P02d-5
owns server transport and locale placement.

- Course catalog page — lists the published courses the host's resolved scope can see,
  through the catalog list.
- Course-detail page — walks the ordered, pageable public outline.
- Lesson page — renders the bounded public content state.

All three are Server Components fetching through the typed SDK. The level taxonomy and
the
lesson content types come from the Customization module, and the lesson page renders the
fields the content-type revision each lesson is bound to declares, not a fixed set. A
tenant may hold more than one content type — Phase 02a's built-in `card` sits beside the
tenant's own, in the state **G14** records — so the renderer never infers a lesson's
type by convention or by tenant. P02d-2 delivers G18's ordered text-card profile,
and P02d-3 supplies localized descriptors. P02d-4 accepts their bounded public
projection using internal requiredness metadata, which is never serialized.
How a field the implemented subset does not draw renders, how an
`x-taxonomy` value displays and which band a course shows are **G41** and **G5**.

The visual identity comes from branding tokens that are tenant settings, per
[Frontend Architecture Standards § Tenant Branding](../standards/07-frontend-architecture.md#tenant-branding),
not from a hard-coded theme. They are read through the typed tenant and organization
settings accessor this phase adds in Tenancy (§ Customization read path) and reach the
page only through a public read. The accessor is internal:
[the Tenancy permission matrix](../modules/tenancy/permissions.md) grants no anonymous
role a `TenantSetting` read, so no `[PublicSurface]` request returns settings by key,
and the accepted anonymous projection returns the whole four-color theme or null.
Baseline colors are independent of plan; effective WhiteLabelBranding removes
attribution only. G42 still selects document injection in P02d-6; organization
overrides and token merges remain Phase 06 scope.

P02d-6's text-card path uses escaped React text under ADR-0051, with no HTML,
Markdown, linkification or authored URL attributes. Broader sanitized-HTML and
URL allowlist rules apply when future producers introduce those representations;
they do not expand this packet's plain-text contract. Theme variables use the
`--ls-*` vocabulary through G42's safe injection decision. No Content-Security-Policy
exists before [Phase 11](phase-11-production-hardening.md#security); output handling
must remain safe independently.

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
  each companion failing on its planted probe. Accepted G28(c) requires a real
  app-role marked write probe to fail with nothing committed, alongside a successful
  writable control. Controller/dispatch guards must fail on planted offenders;
  structural detection is not the runtime proof. Each anonymous endpoint
  carries `[AllowAnonymous]` with a one-line reason (**manual**, listed in the delivery
  record).
- Accepted **G28** records `Off`; serving anonymous reads on both hosts adds no
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
- P02d-4 proves SDK wrapper coverage of all four logical GET contracts and HTTP
  coverage of all eight GET/HEAD operations, with a planted missing-operation
  companion. P02d-5 supplies the first configured server consumer; P02d-6 supplies
  every logical GET's public-page consumer. HEAD needs no browser JSON consumer.
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
  accepted in **G28**: mandatory marked dispatch and structural guards complement
  the physical read-only barrier. P02d-4 Step 1 delivers the physical read-only barrier and host admission.
  Endpoint dispatch/dependency guards remain with Step 2; the barrier covers only
  enlisted transactions.
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
  The accepted contrast refusal must be preserved:
  [Accessibility Standards § Color and Contrast](../standards/16-accessibility.md#color-and-contrast)
  records the refusal delivered by P02d-2. Phase 06's editor must report that
  failure rather than invent a warning-only save.
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


#### Independent PR review follow-up (2026-09-14)

The maintainer's review report was assessed against `35e54eb` and the accepted
P02d-1 answers. Two bounded follow-up steps address its verified findings without
adding production behavior or changing an accepted decision.

**Step 1 — proofs (`c98bcb2`, `58afa6d`).** All eight Education field-level refusal
keys now have exact field and message-key assertions. Invalid summary controls cover
NUL and unpaired high/low surrogates while preserving prior content and the root's
version. Pattern A detects localized slug/locale suffixes in the applied schema and
uses separate probes for unmapped CLR fields and EF shadow columns. The old detector
fails six new controls; the repaired detector passes all 49 structural cases.
Application-role checks now cover the remaining constraint and organization-scope
connection boundaries, including both pool checkouts.

Two fresh reviewers approved round 1. Round 2 used two different reviewers and found
that publication tests sampled invalid values without proving the database's complete
closed set. `58afa6d` compares the validated applied CHECK's whole predicate and value
set with each root's actual EF converter. Four planted constraints cover extra states
and an OR bypass on both roots; each is rejected by the comparison and passes after
repair. The reviewer who identified the gap verified the fix and closed the finding.

**Step 2 — corpus reconciliation.** The result of every report item is recorded below;
items that proposed a new rule are distinguished from defects in the accepted scope.

| Report item | Disposition |
|---|---|
| H1 | Corrected Database Standards' stale P02d-1 completion statement. |
| H2 | Exact field/reason assertions cover all eight Education keys, including dedicated Summary refusals. |
| H3; T1; T2 | Repaired slug/locale suffix detection; added applied, unmapped-CLR and shadow-column controls. |
| H4; Doc4 | Kept the accepted Education column inventory; aligned Standard 08's illustrative satellite with `summary` and clarified the inventory's scope in the test catalogue. Pattern A does not require every possible SEO field on every entity. Also corrected the stale publish-only collision wording: a draft reserves its slug on translation insertion. |
| H5 | Indexed existing Amendments 4–6. Preserved ADR-0003's Status under [the immutable-metadata rule](../standards/13-documentation.md#when-the-body-says-something-false). No ADR body or decision changed. |
| D1 | No nonblank rule exists for optional Summary in the accepted model. Whitespace acceptance is not a demonstrated defect; the proposed rejection policy was not introduced. |
| D2 | Canonicalization remains the shipped application responsibility, per [Localization architecture](../architecture/12-localization.md#tenant-locale-configuration). A new database locale-shape policy was not part of the accepted P02d-1 contract. |
| D3 | Parent scope and public eligibility are separate contracts. [ADR-0048](../decisions/0048-walking-skeleton-publication.md) assigns combined parent/deletion eligibility to reads; an active-parent creation rule was not silently added. |
| D4 | The private helper's `nameof(key)` names its actual parameter. Assigned-id helper reuse, parsing consolidation and cached read-only wrappers are optional refactors without a demonstrated correctness or workload defect; no production cleanup was bundled. |
| C1 | Corrected the ordering claim: fixtures and `make migrate` both honor Tenancy's dependencies; their independent Audit/Customization order need not match. |
| C2 | Documented the full-index versus live-only ordered-index purposes in the [Education performance section](../modules/education/README.md#performance-budget); retained the indexes. |
| C3 | Aligned the illustrative `created_at` DDL with the existing application-clock audit convention. No database timestamp fallback was added. |
| C4 | Compared actual converter output with the applied closed-set constraint, including the additional round-2 mutation controls above. |
| T3 | Added explicit authenticated/effective application-role and no-superuser/no-bypass assertions at the reported connection boundaries. |
| Doc1 | Not a defect: the earlier wording names the repository policy scan, not a private artifact. Its exact historical wording is preserved under the [delivery-record rule](../standards/13-documentation.md#doc-types). |
| Doc2 | Added glossary entries for publication, revision pin and parent organization mirror, with links to their existing authorities and from the module spec. |
| Doc3 | Kept normative slug grammar in [Standard 08](../standards/08-localization.md#education-slug-grammar) and executable checks in Standard 05; architecture and module prose link to them. The accepted-answer table remains its historical decision summary. |
| Doc5 | Added the current resource/action/scope matrix with no registered permissions or default grants, preserving the named owners of later command surfaces. |
| Doc6 | Distinguished Standard 14's canonical PR sections from the commit skill's existing risk and attribution supplements. |
| Doc7 | Stale at the reviewed head: the final-check checkbox was already checked for `35e54eb`. Later heads are verified through the live PR rollup. |

The report's R1–R5 observations introduce no new packet defect. The
[isolation template and role model](../standards/05-database.md#tenant-owned-and-organization-scoped-tables),
[migration dependency and reversal rules](../standards/05-database.md#migrations),
[accepted database privileges](#p02d-1-accepted-answers) and
[Education scope/locale contract](../modules/education/README.md#data-model-and-invariants)
remain their authorities. No new production writer, cross-chain foreign key or
publication/deletion policy is inferred from those observations.

The full Release backend run after the test fixes passes **2,381** cases:
**1,456 unit, 177 architecture, 747 integration and one contract**, with zero failures
or skips. The nonempty-run checker verifies every assembly. The earlier 2,366-case
and coverage figures above remain dated evidence for their original heads. This
follow-up changes no production source or migration. [PR #22](https://github.com/HodeTech/LearnStack/pull/22)
remains the current check-rollup and review surface.

#### Merge and closeout (2026-09-14)

[PR #22](https://github.com/HodeTech/LearnStack/pull/22) merged into `main` at
**20:22:17 UTC**, with final PR head `bc181702e379fec99da43015b2ee357ad772d6d1`
and merge commit `1d3a0f717523bebbf8c397ae9facba7c80eafae9`. The merge tree is
identical to the final PR head. `development` was fast-forwarded to that merge
commit without switching branches, rewriting history or changing file contents.

- [x] P02d-1's accepted decision parts, all three implementation steps and their
  two review rounds are complete, as recorded above.
- [x] The final review's three verified documentation findings are resolved in
  `bc18170`: README delivery status, the Course/Lesson glossary distinction between
  Phase 02d and Phase 05, and Phase 02b G19's stale module count.
- [x] The README refresh and the requested removal of mandatory commit coauthor
  attribution (`dde8e0b`) are included in the merged head.
- [x] The final PR-head and merge-commit CI runs both completed successfully.

| Verified revision | CI evidence | Result |
|---|---|---|
| Final PR head `bc18170` | [Run 34892449509](https://github.com/HodeTech/LearnStack/actions/runs/34892449509) | All five required jobs succeeded |
| `main` merge commit `1d3a0f7` | [Run 34892508893](https://github.com/HodeTech/LearnStack/actions/runs/34892508893) | All five required jobs succeeded |

The live required-check list still has the five contexts recorded in Step 3, with
GitHub Actions `app_id: 15368` and `strict: true`. The merge run verifies **2,381
backend tests**: 1,456 unit, 177 architecture, 171 Docker-free integration,
576 Docker integration and one contract, with zero failures or skips. The frontend,
meta and secret-scan jobs also pass. The OpenAPI and Lighthouse placeholders remain
outside the required set, with their existing P02d-4 and P02d-7 decision gates.
Both completed runs were verified at **20:25 UTC**.

**P02d-1 is closed. Phase 02d remains in progress.** P02d-2 through P02d-7 have not
started. P02d-2 first resolves its writer, customization-contract, locale, branding,
seed-context and data-safety decisions, then implements the commands, audit wiring
and repeatable tenant-specific seed. The packet table and decision register above
own its exact scope and open gate parts. Public reads, rendering and the browser
demo remain the later packets' work; this merge does not complete those surfaces.
