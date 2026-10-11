# Phase 02d — individual exit evidence

**Reconciliation complete — 2026-10-11.** All four P7 steps completed both
independent review rounds. The real PR check receipts, verified artifacts,
seventh-check activation and positive exit verdict are recorded in the
[CI and exit closeout](phase-02d-walking-skeleton.md#p02d-7-ci-and-phase-exit-closeout-2026-10-11).
This document is an evidence index, not a new architecture decision or a merge
approval. PR #28 awaits maintainer review and merge.

The rows enumerate all 36 [deliverables](phase-02d-walking-skeleton.md#deliverables),
79 [completion criteria](phase-02d-walking-skeleton.md#completion-criteria),
39 [exclusions](phase-02d-walking-skeleton.md#explicitly-not-in-this-phase) and
nine [phase-exit obligations](phase-02d-walking-skeleton.md#phase-exit-decision),
in source order. Requirement locators abbreviate the canonical text; the linked
phase document owns the complete contract and every subordinate condition.

## Receipt basis

| Code | Required receipt and present boundary |
|---|---|
| B | Real PR `backend` passes: Release build/format, 1,705 unit, 203 architecture, four contract and 180 Docker-free integration; zero failures/skips and execution guards. |
| I | Real PR `backend integration (Testcontainers)` passes all 872 cases, including the three locale/branding/combined-scope cases and physical app-role/RLS/read-only production proofs; zero failures/skips. |
| F | Real PR guarded `frontend` passes 1,444 web + 60 SDK cases, zero failures/skips/todos, plus lint, typecheck, builds and controls. |
| O | Real PR `openapi diff` and generated SDK drift pass with retained planted incompatibility controls. |
| J | Real PR `public demo + lighthouse` passes with verified nine-page/27-audit artifacts and confirmed cleanup; authorized seventh-context activation and complete protection read-back pass. |
| M | Named source/document inspection and explicit future-owner reconciliation in Step 4; both fresh review rounds and verified fixes complete. |
| H | Attributed carried-forward P6 manual evidence below; no new final-head human/WCAG claim. |
| N | Conditional branch made inapplicable by its Accepted decision, as named below. |

Named backend classes live under `backend/tests/`; frontend tests live under
`frontend/apps/web/src/` or `frontend/scripts/`. Partial-class filenames are
identified separately where needed. P1–P6 refer to the merged packet records in
[Phase 02d](phase-02d-walking-skeleton.md); Step 1–3 refer to P7's records.
[Run 38108622191](https://github.com/HodeTech/LearnStack/actions/runs/38108622191)
supplies B/I/F/O/J for reviewed implementation source `d47ef17`, tested merge
`ba44563`; the [exact-head verification](phase-02d-walking-skeleton.md#reviewed-implementation-head-verification-2026-10-11)
records full identities and artifact validation. The earlier run owns
seventh-check activation history. The
[PR rollup](https://github.com/HodeTech/LearnStack/pull/28/checks) must also pass
on subsequent heads before merge; historic counts do not replace it.

## Local and manual evidence boundaries

Step 3's latest complete local lifecycle uses source-copy commit
`39cee01438c48c290ba557bc22de85b8b21269bb`, build `d0bzXjXoImUdH317X83rK`, invocation
`825982bee2ed568f43208368f99f332d`: setup 60,374 ms, proof 585,644 ms and cleanup
10,719 ms. It produced 27 JSON/HTML pairs, nine screenshots and 24 planted
visibility refusals. All 134 API observations were 200; none were 429, malformed
or pending. Normalized gzip was 106,383–106,574 bytes, CLS 0 and automated
accessibility 1 throughout. This is local feasibility, not receipt J or an
exact final-head CI result. The subsequent shared shutdown-bound fix has its own
causal focused proof; the final CI lifecycle must include it.

H retains PR #27's original manual attribution: commit `180dde0`, macOS 27.0.1,
Brave 154.1.96.61, both seeded hosts and EN/TR routes. The
[P6 closeout](phase-02d-walking-skeleton.md#p02d-6-packet-closeout-2026-10-10)
records keyboard/focus, 320 CSS-pixel reflow and long-string observations,
calculated contrast, and the maintainer's VoiceOver catalog → course → lesson,
missing-page and invalid-link recovery smoke. Seeded contrast ratios were
17.740/7.557/6.702 (English), 14.628/7.357/6.876 (Yoga) and
17.853/4.759/4.634 (defaults). This is not full WCAG, mobile-device or zoom
conformance, nor a rerun on P7's final head. P7's real browser and Lighthouse
checks supplement that record; they do not manufacture new human evidence.

## Deliverables

| ID | Requirement locator | Named evidence | Receipts |
|---|---|---|---|
| D01 | `Course` and `Lesson` in `LearnStack.Modules.Education`, in the aggregate shape **G2** records, with migrations, EF configurations, query filters and Row Level Security policies, including each … | P1; EducationPersistenceTests, EducationIsolationTests, CourseContentAccessMigrationTests. | B, I |
| D02 | `course_translations` and `lesson_translations`, conforming to Database Standards § Translation satellite tables, with the accepted slug grammar in the Education spec. | P1; EducationConstraintTests, EducationStructureTests; Education spec Localization and URL identity. | B, I, M |
| D03 | The organization immutability trigger on all four tables, with a function that works on a table without `id` (**G8**); an index supporting every Education … | EducationParentScopeTests, EducationIsolationTests, EducationStructureTests, OrgScopeSchemaTests/OrgScopeMigrationTests. | I |
| D04 | The four tables' rows in Database Standards § GRANT matrix, written by the migration that creates each table, with the exact-grant test and `SchemaFixture.KnownTables` … | EducationStructureTests, SchemaFixture.KnownTables; Database GRANT matrix and creating migrations. | I, M |
| D05 | The structural guards accepted by **G8**, each Implemented with a companion that plants its violation. | P1; tenant-FK, organization-immutability and Pattern-A catalogue guards, EducationStructureTests and planted companions. | B, I, M |
| D06 | Education isolation tests connected as `learnstack_app`, at schema level and at request level, over the four tables, across tenants and across organizations, per Testing … | EducationIsolationTests, PublicEducationHttpTests, TenantIsolationHttpTests, SeederTests. | B, I, M |
| D07 | The Education module spec under `docs/modules/education/` (`README.md`, `audit.md`, `permissions.md`), with its state diagram and the eligibility rule **G29** records, and `education` added to `Every_Module_Has_An_AuditCoverage_Matrix`'s … | docs/modules/education/{README,audit,permissions}.md; AuditCoverageTests. | B, M |
| D08 | The Education write commands (**G11**) and the Tenancy commands raising `tenancy.locale.write` and `tenancy.setting.write`; each removes its matrix row's `(planned)` marker and registers its catalogue … | EducationWriterTests, TenancyWriterTests, AuditCoverageTests, CompositionRootCatalogueTests. | B, I, M |
| D09 | The `Customization.Application.Contracts` surface — an interface or a query, as **G12** records — through which Education obtains the content-type revision a lesson body is … | Customization.Application.Contracts exact-definition surface; EducationWriterTests.Exact_active_bindings_and_existing_deprecated_pins_use_their_own_schema. | B, I |
| D10 | An Education `IAuditCatalogSource` registered in the API and seeder composition roots, and the Education Application assembly in both roots' handler scans, with a request-level … | CompositionRootCatalogueTests; EducationWriterTests and SeederTests exercise both roots. | B, I |
| D11 | The seeder's acts, ownership checks, ordering and contexts (**G14**, **G15**), and each tenant's own content type, taxonomy, locales, branding, courses and lessons. Every seeded … | SeederTests.Complete_inventory_has_exact_states_pins_translations_and_null_or_exact_write_scope plus repeat/race/refusal cases. | I |
| D12 | The `[PiiSensitive]` record on `TenantSetting.Value` (**G17**) and the branding token contract (**G16**), with its detail in Frontend Architecture Standards § Tenant Branding. | TenancyWriterTests.Whole_json_value_is_redacted_in_the_capture_and_durable_setting_audit; BrandingThemeTests. | B, I, M |
| D13 | The customization read path per Tenant Customization Model § 8.2: the Customization contract, classified wherever it adds request types, and the generation-keyed projection with … | CustomizationProjectionTests, CustomizationProjectionTests (composition partial), CustomizationProjectionTests (cache partial); InMemoryCacheServiceTests. | B, I, M |
| D14 | The typed tenant and organization settings accessor over `tenant_settings`, internal to the backend, with its interface name and glossary headword, key composition, loader and … | TenantSettingsAccessorTests, TenantSettingsTests; Tenancy spec/glossary; G23 selects no settings cache. | B, I, M |
| D15 | The anonymous public reads — the Education reads in § Read API and whatever **G25** adds — each with its `[PublicSurface]` marker, its row … | PublicSiteHttpTests, PublicEducationHttpTests, PublicSurfaceTests, OpenApiContractTests; API Public surface table. | B, I, M |
| D16 | The two unimplemented legs of `PublicSurface_Marker_Set_Is_Enumerated`, each with a companion that proves it can fail, and the catalogue entry's Status updated (**G28**). | PublicSurfaceTests, PublicReadTransactionTests; P4 physical write-refusal plus successful writable control and structural offenders. | B, I |
| D17 | The `@learnstack/sdk` types regenerated from that document (`src/generated/schema.d.ts`) and the SDK surface that compiles against them (**G31**). | frontend/packages/sdk/src/generated/schema.d.ts; SDK tests and apps/web typecheck. | F, O |
| D18 | The CI gates the corpus assigns here: the OpenAPI breaking-change check over a committed snapshot and the SDK drift gate (**G31**), each activated through … | P1/P4 dated activation/planted controls; six retained required contexts. Step3 local Lighthouse proof is preliminary. | O, J |
| D19 | The two seed host rows Phase 02a Packet 7 wrote, reachable from a browser and from `make demo` over the hostnames and transport **G32** … | P5 G32 retained *.learnstack.local; Step1 seeded demo readiness; Step3 nine-page browser feasibility. | J, M |
| D20 | The server-only configured transport in `frontend/apps/web/src/server/configured-public-client.ts`, stating the verified visitor's host over the trusted hop (**G35**). The injected GET wrappers in `frontend/packages/sdk/src/server.ts` remain authority-free. | configured-public-client.test.ts, public-boundaries.test.ts, PublicServerRenderingTests. | I, F |
| D21 | `frontend/apps/web/src/middleware.ts` no longer answering the scaffold's `503`, writing the raw host as a tenant id or carrying TODOs that assign the work to Phase … | public-entry.test.ts, public-admission*.test.ts, PublicAdmissionRenderingTests. | I, F, M |
| D22 | A development trusted-hop configuration the API and the Next.js server agree on, with every variable it adds listed in `.env.example` (Infrastructure Standards); placement per … | P5 configuration/private launchers; Step1 manifest/configuration tests; .env.example and web env example. | B, I, J, M |
| D23 | The hop runtime evidence **G33** chooses, and the dated ADR-0036 amendment if G33 or **G34** requires one. | P5 trusted-hop/native integration, ADR-0036 dated amendment, Step3 accepted loopback/native CI topology. | I, J, M, H |
| D24 | The anonymous limiter's treatment of trusted-hop traffic as **G34** decides, including a decision to leave it unchanged; any rule change ships with its carriers … | RateLimitingHttpTests and anonymous admission/lifecycle/request-identity tests; ADR-0053/0054. | B, I, J, M |
| D25 | The `(public)` routes **G25** and **G40** fix in `frontend/apps/web`, tenant-branded, rendering from customization data, with link navigation from catalog to lesson. | P6 public route/link tests and product fixture; Step3 actual linked three-lane browser feasibility. | I, F, J |
| D26 | The lesson-body renderer: the composite **G18** chooses, registered in `composites.ts` under the containment rule; primitive components for the implemented subset, in the home **G41** … | P6 lesson renderer tests, public-rendering-assertions.test.ts, production presentation fixture. | I, F, M |
| D27 | Theming token injection in the `(public)` layout per **G42**, emitting only the `--ls-*` vocabulary. | public-ui.test.ts, public-boundaries.test.ts, production product fixture; Step3 computed colors. | I, F, J |
| D28 | The UI message layer and catalogue at the location **G39** records. | WEB i18n/messages/{en,tr}/public.json, i18n/ICU controls, Accepted ADR-0027. | F, M |
| D29 | Frontend tests for the code this phase adds, with the case set **G38** decides. The harness exists (`vitest.config.ts`, jsdom, Testing Library), with Packet 3b's … | P5/P6 frontend and actual-production HTML/Flight tests; ADR-0055 admission proof; Step3 narrow browser proof. | I, F, J, M |
| D30 | The required `frontend` job refusing a Vitest run that reports a skipped or todo case in every workspace package that carries tests, proven with … | Guarded workspace runner and planted skip/todo/empty/omitted-workspace controls; No_Architecture_Test_Is_Skippable. | B, F |
| D31 | The accessibility review Accessibility Standards requires of every new screen, recorded in the renderer pull request's description: a manual keyboard walkthrough of each public … | PR27 description preserves attributed prior manual smoke and links Original manual evidence to P6 Step4; P6 manual/VoiceOver closeout. | H |
| D32 | The tenant-branching check **G20** selects, Registered in the first pass that uses it and Implemented with its companion before exit. | Step2 DomainGenericityTests.DemoIdentities.cs, demo-genericity.test.ts, Production_Projects_Do_Not_Reference_The_Seeder, schema-replacement fixture. | B, I, F, M |
| D33 | `make demo` and its stop behaviour per **G45**, with README § Quickstart, the closing output of `scripts/seed.sh` and the `local-dev-setup` and `seed-tenant` skills updated … | Step1 demo-lifecycle controls/shared supervisor; README, seed output and local-dev/seed/test skills updated to the delivered shared lifecycle. | J, M |
| D34 | The full-stack Lighthouse job and its committed configuration, if **G44** activates it here. | Step3 pinned browser/Lighthouse configuration, gatherer/report/gzip/admission controls and local 27-report feasibility. | J |
| D35 | The standards index rows this phase makes stale — the notes on Frontend Coding, Frontend Architecture and Accessibility Standards, the table count on Database … | Database counts updated previously; Accessibility already Active. Performance is Active for implemented hard P7 checks; all four behavioral P7 rules are Implemented. Permissions remains Adopted. | B, F, J, M |
| D36 | The delivery record at exit names the Education model shipped, the preservation obligations G2 recorded, the seed fixtures Phase 05's migration must carry, and … | Phase05 inherited baseline already preserves rows/IDs/URLs/order/scope/body/pins/policy. | M |

## Completion criteria

| ID | Requirement locator | Named evidence | Receipts |
|---|---|---|---|
| C01 | Opening host A shows the English school's catalog, and host B the yoga studio's. Each shows its own branding with `NullEntitlementProvider` registered, which **G16** … | P6 manual/product record; Step3 identity/computed-theme/field feasibility; NullEntitlementProvider default retained. | I, J, H |
| C02 | On either host, a visitor starting at the catalog reaches a lesson through rendered links alone, without typing a URL, and the lesson renders … | P6 route/link tests and manual record; Step3 actual linked navigation in three lanes. | I, F, J, H |
| C03 | Both hosts are answered by the same API build and the same `apps/web` build against one database and one schema; no build or deployment … | Step1 shared API/web/database lifecycle; Step3 receipt/build observations. | J |
| C04 | The two lesson pages render **different field sets**, driven by each tenant's `TenantContentType` — not the same template with different strings — each field … | SeederTests distinct schema/taxonomy inventory; Step2 orderedLessonFields controls and successor/rebinding product fixture; P6 locale/label tests; Step3 ordered browser fields. | B, I, F, J |
| C05 | Neither seeded lesson page, served by the running stack, renders a fallback or placeholder block (the full-stack job, **G44**; otherwise **manual**). | Step3 actual-page/lesson field admission refuses placeholder/wrong pages. | J |
| C06 | `make demo` on a clean checkout — no `.env`, no `frontend/apps/web/.env.local`, no compose volumes, and no host configuration beyond **G32**'s host step — leaves … | Step1 clean isolated lifecycle/repeat/stop controls; Step3 shared lifecycle feasibility. | J |
| C07 | If **G32** moves the seed hosts, re-running the documented seed or demo path against a database seeded with the old host names leaves both … | G32 Accepted retained *.learnstack.local; no host-name move occurred. | M, N |
| C08 | After seeding, with the compose `keycloak` service stopped, the catalog and a lesson page on both hosts still render anonymously — the baseline Phase … | Step3 asserts CI-owned Keycloak stopped before fresh browser navigation on every lane. | J |
| C09 | Two courses in one tenant cannot both hold one `(locale, slug)` pair; the second insert is rejected by the database, and the rejection also … | EducationConstraintTests.Localized_slug_uniqueness_spans_parents_and_organizations and Localized_slug_namespace_is_independent_between_tenants_and_locales; exact-locale HTTP cases. | I |
| C10 | Read directly, each of `courses`, `lessons`, `course_translations` and `lesson_translations` returns no foreign tenant's rows, tenant-wide ones included, and no sibling organization's rows; each has … | EducationIsolationTests, four-table WITH CHECK coverage and raw satellite reads. | I |
| C11 | Under tenant A, inserting a lesson whose course key names tenant B's row, or a translation whose parent names tenant B's row, is refused … | EducationIsolationTests and EducationParentScopeTests. | I |
| C12 | A child row whose organization differs from its parent's — including a null-organization child planted under another organization's course and an organization child under … | EducationParentScopeTests, EducationIsolationTests, OrgScopeSchemaTests. | I |
| C13 | Changing `organization_id` on a row of each of the four tables raises the immutability error from a session the restrictive guard admits (**G8**). | EducationIsolationTests four-table immutable organization cases. | I |
| C14 | `Every_Foreign_Key_Has_A_Supporting_Index` passes with the four tables in its swept set, and `Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows` with each of them that carries `deleted_at` under the mapping **G2** records; … | EducationStructureTests, MigrationRollbackTests, MigrationChainCoverageTests and structural planted companions. | B, I, M |
| C15 | Before the first Education migration merges, **G2**'s row is closed, the Education spec's data model names `Course`'s and `Lesson`'s aggregate roles, and the lessons … | P1 accepted G2 decision/delivery/merge record and Education data model. | M |
| C16 | A publication-state value outside the `CHECK` set **G3** records is rejected by the database as `learnstack_app`. | EducationConstraintTests publication-state CHECK cases. | I |
| C17 | `docs/modules/education/audit.md` exists, and `Every_Module_Has_An_AuditCoverage_Matrix` names `education` (`backend` job). | Education audit.md and AuditCoverageTests pinned module sweep. | B |
| C18 | After the seed, each seed tenant has one committed MUST-class audit row per published course and per `tenant_settings` write, whose entity id matches the … | SeederTests and TenancyWriterTests durable redaction; EducationWriterTests.Publication_requires_durable_must_audit_and_rolls_back_on_audit_failure. | I |
| C19 | No Tenancy or Education matrix row whose command ships still carries `(planned)`, and `Every_Shipped_Request_Is_Registered` and `CompositionRootCatalogueTests` pass for both roots. | AuditCoverageTests, CompositionRootCatalogueTests and Tenancy/Education matrices. | B, I, M |
| C20 | A second seed run exits 0 and changes no Education, `tenant_locales` or `tenant_settings` row; each seeded row carries the `organization_id` **G14** names; each seed … | SeederTests.Running_the_seed_twice_changes_nothing_and_still_succeeds, exact inventory/process-repeat cases; Step1 audit-neutral repeat. | I, J |
| C21 | The Phase 02a request-level suite (`TenantIsolationHttpTests`) still names the exact rows it expects: its customization cases assert `BeEquivalentTo` an enumerated per-tenant set that includes … | TenantIsolationHttpTests enumerated exact seed sets; SeederTests exact generations/counts; P2 writer derivation record. | I, M |
| C22 | With migration history unchanged, a request-level test adds a third locale — a `tenant_locales` row through the Tenancy locale command plus translation rows through … | PublicLocaleEvolutionHttpTests.Third_locale_authored_through_api_commands_serves_exact_new_urls_without_changing_existing_responses_or_migrations; local focused case passed; downloaded real PR receipt I passed. | I, M |
| C23 | An Education translation write for a locale absent from, or disabled in, the tenant's `tenant_locales` behaves as **G13** records, and a refused write commits … | EducationWriterTests.Locale_slug_lifecycle_and_stale_refusals_preserve_the_root_and_reserve_slugs_on_insert; Seeder exact canonical inventory. | I |
| C24 | A lesson write whose body fails its bound revision returns `validation_failed` with details keyed by JSON pointer and persists no lesson or translation row; … | EducationWriterTests.Exact_active_bindings_and_existing_deprecated_pins_use_their_own_schema; EducationWriterValidationTests; Seeder exact non-card bindings. | B, I |
| C25 | A duplicate slug written through the command **G11** names returns `Result.Fail(business_rule_violation)` and does not throw. A slug outside the shape **G9** settles is refused … | EducationWriterTests, EducationWriterValidationTests, EducationConstraintTests. | B, I |
| C26 | A publication-state transition **G3** forbids answers `Result.Fail(business_rule_violation)` from the command, does not throw, and writes nothing (`backend integration`, plus a command or aggregate unit … | EducationAggregateTests/EducationWriterTests forbidden lifecycle and no mutation. | B, I |
| C27 | A branding value outside **G16**'s grammar — `red;}body{background:url(//x)}`, a string containing `</style>` — never reaches a rendered document, and a refused write commits neither … | TenancyWriterTests branding refusal/atomicity, BrandingThemeTests, product theme defenses. | B, I, F |
| C28 | An organization-scoped branding command is refused without setting mutation; its Denied audit is retained, and the live organization host keeps the tenant palette. | PublicLocaleEvolutionHttpTests.Organization_branding_writer_is_refused_with_denied_audit_and_mapped_host_keeps_tenant_palette; local focused case passed; one expected Denied audit, no successful setting mutation; downloaded real PR receipt I passed. | I, M |
| C29 | If **G19** includes a write-time rule, a lesson write holding a disallowed URL is refused with details naming the JSON pointer, and nothing is … | G19 Accepted text profile introduces no active URL sink or scheme write-time rule. | N |
| C30 | Whatever **G18** refuses at save — which may include a presentation entry naming a property absent from `properties`, a malformed label map or a … | TextCardPresentationTests, customization command/schema validation tests. | B |
| C31 | A course write naming a taxonomy revision or band key the tenant does not declare, including one only the other tenant declares, returns `validation_failed` … | EducationWriterTests and EducationWriterValidationTests taxonomy/band binding validation. | B, I |
| C32 | Under **G15**'s answer, `SeedRunner` either no longer announces its own tenant context or appears in ADR-0040's setter table, and a planted caller fails any … | SeederConventionTests and contextual ownership verification/caller fence with planted offenders. | B |
| C33 | With the API running, publishing a successor content type or taxonomy for one tenant through its command changes what the next read returns for … | CustomizationProjectionTests/cache suites and production presentation successor/rebinding fixture. | B, I |
| C34 | A second warm read for the same tenant and generation issues no definition-set query; a publish that rolls back after its generation bump leaves … | CustomizationProjectionTests (cache partial), composition/publication concurrency tests. | B, I |
| C35 | None of the public reads, and no definition or settings read, invokes `IJsonSchemaValidator`. | CustomizationProjectionTests and architecture read-path guards. | B, I |
| C36 | If **G23** caches settings, then after that cache is warmed in the yoga studio's default organization, a read in the sibling organization's context and … | G23 selects no cache; TenantSettingsAccessorTests/TenantSettingsTests prove ambient scoped reads/current values. | B, I, N |
| C37 | Every cache family this phase adds appears in the cheat sheet, the Observability Standards family list and the `cache.name` mapping, and none of its … | InMemoryCacheServiceTests and three named cache-family carrier lists. | B, M |
| C38 | For a request in an enabled `tr-TR`, a label authored only under `tr` resolves as the chain **G24** records, and a value missing in … | PublicEducationDisplayTests and locale-resolution/projection tests; P3/P4 fallback records. | B, I |
| C39 | The out-of-band setter guard stays green: either the setter table is unchanged, or the ADR-0040 amendment and Security Standards row land with the loader. | Out-of-band setter guards and P2/P3 approved setter decisions. | B, M |
| C40 | In one test, a course slug that exists only in tenant B returns `200` with that course on tenant B's host and `404` on … | PublicLocaleEvolutionHttpTests.Public_host_locale_and_organization_contracts_hold_together_with_exact_request_connection_translation_scope pairs foreign-only/shared slugs with full seed-derived payloads. | I |
| C41 | In the same test, a course returns `200` at its slug in a locale it is translated into and `404` for that slug under … | The same PublicLocaleEvolutionHttpTests case pairs enabled/untranslated French with translated EN/TR course and lesson lists. | I |
| C42 | On the organization host, a sibling organization's course is absent from the catalog and its slug returns `404`, while a tenant-wide course and its … | The same PublicLocaleEvolutionHttpTests case proves three host scopes and exact raw satellite row sets on each of its 17 HTTP requests' own enlisted connection. | I |
| C43 | A draft course is absent from the catalog, and its detail slug and every lesson requested under it answer per **G29**; a lesson of … | PublicEducationHttpTests.Course_draft_branch_foreign_and_unknown_are_uniformly_hidden and Lesson_masks_draft_parent_draft_restricted_scope_wrong_parent_and_foreign_rows. | I |
| C44 | Each of the reads without `locale` returns `400` `validation_failed` naming `locale`, never a `500` or a defaulted locale; sending a different `X-Locale` or `Accept-Language` … | PublicEducationHttpTests query/detail/unsupported-locale cases including Well_formed_unsupported_locale_is_400_and_headers_do_not_replace_explicit_locale. | I |
| C45 | A cursor value the catalog list cannot read — malformed or truncated — returns `400` `validation_failed` naming `cursor`, never `500`. Walking `nextCursor` with a … | PublicEducationHttpTests query-shape/cursor/seek cases, PublicReadProtocolTests and OpenAPI exact parameter controls. | B, I, O |
| C46 | A course's embedded lesson list returns the same order on every read, including for any sort values **G2**'s invariant permits to tie. | PublicEducationStoreTests and PublicEducationHttpTests catalog/outline seek-order cases. | I |
| C47 | A lesson whose bound `(key, schema_version)` revision cannot be resolved (a fixture removes it) answers as **G12** records — never a `500`, and never … | PublicEducationHttpTests/PublicEducationDisplayTests and product presentation fault mode. | B, I |
| C48 | A stored band that the resolved taxonomy revision does not declare yields the state **G5** records, never a `500` and never another tenant's band … | PublicEducationDisplayTests, PublicEducationHttpTests and product band-fault mode. | B, I |
| C49 | On each seed host, each shipped anonymous read returns `200` under a host-only context, and the same request on an unknown host returns the … | PublicSiteHttpTests/PublicEducationHttpTests; PublicSurfaceTests/PublicReadTransactionTests runtime refusal/positive control and structural dispatch offenders. | B, I, M |
| C50 | Accepted **G28** records `Off`; serving anonymous reads on both hosts adds no `audit_log` rows. | PublicEducationHttpTests.Catalog_is_exact_locale_host_scoped_and_only_marketing_with_no_normal_audit and PublicSiteHttpTests. | I |
| C51 | Every anonymous response — a catalog `200`, a detail `404`, a bad-cursor `400` and an unmapped-host `404` — carries the directive **G27** decides, and … | Public HTTP no-store/validator cases and PublicServerRenderingTests A/B/A product fixture. | I |
| C52 | The committed OpenAPI document lists exactly the public operations the register decides, each with `locale` required on the Education reads, and a planted extra … | OpenApiContractTests and PublicEducationHttpTests exact public operation/DTO/status/bounded-list/alternate cases. | B, I, O |
| C53 | P02d-4 proves SDK wrapper coverage of all four logical GET contracts and HTTP coverage of all eight GET/HEAD operations, with a planted missing-operation companion. … | P4 SDK missing-operation/wrapper tests and eight GET/HEAD HTTP coverage; P5 configured/P6 every logical-page consumers. | B, I, F |
| C54 | The contract suite fails when the served `/openapi/v1.json` differs from the committed snapshot, shown with a companion; the breaking-change job fails on a planted … | OpenApiContractTests served snapshot/deny-list controls; P4 pinned oasdiff policy/offender suite; SDK drift/typecheck controls. | B, F, O |
| C55 | Each activated check has no `(deferred …)` suffix in `ci.yml`, appears in CONTRIBUTING's required list and in the live required contexts, and its skip … | P1-P6 dated merge/check/protection receipts exist. | O, J |
| C56 | `Handlers_Return_Result` stays green with Education's handlers counted, and an Education handler changed to return a raw DTO fails the build (`backend`). | Handlers_Return_Result with nonempty Education handlers and raw-DTO companion. | B |
| C57 | On both seed hosts the catalog renders through the Next.js server while no server-to-API call carries `X-Tenant-Id`, each host showing only its own tenant's … | PublicServerRenderingTests/hop HTTP tests and P6 manual walkthrough; Step3 browser feasibility. | I, J, H |
| C58 | Neither the built client assets nor rendered HTML contain the hop secret value, and no `NEXT_PUBLIC_` variable carries it. Only `frontend/apps/web/src/server/configured-public-client.ts` sets hop headers … | public-boundaries.test.ts/configured-public-client.test.ts and real production client-import/build probes. | I, F |
| C59 | Client-supplied `x-tenant-id`, `x-organization-id`, `x-locale`, `x-learnstack-host` and `x-learnstack-hop-secret` never reach an SDK call's outbound headers unchanged; under the production build and mandatory native launcher, public … | public-entry/admission/runtime tests and PublicAdmissionRenderingTests. | I, F, M |
| C60 | With private hop configuration kept outside version control, ordinary no-hop API startup works under committed Development settings with no `.env` present, and every Development-environment … | TrustedHopConfigurationTests and ordinary Development fixture baseline. | B, I, M |
| C61 | A non-hop peer is still limited per socket peer, getting `429` with `Retry-After` over budget, and rotating `X-Forwarded-For` or any header **G34** introduces buys … | RateLimitingHttpTests, anonymous request identity/admission/lifecycle and unknown-host cache tests. | B, I, J |
| C62 | If **G35** places it here, a server-to-API call carries a `traceparent` whose trace id matches the API's Problem Details `traceId`. | PublicServerRenderingTests transport mode and configured-public-client.test.ts W3C propagation/refusal controls. | I, F |
| C63 | On a production build, one `(public)` path requested on host A, then B, then A returns each tenant's own markers every time; the build … | PublicServerRenderingTests.Production_rendering_is_host_isolated_fresh_and_private_through_the_real_public_API and frontend forbidden-cache offenders. | I, F |
| C64 | Each rendered public page's `<html lang>` equals the locale in its path, on both locales of the bilingual tenant, and `dir` follows that locale. | P6 lang/dir product/route assertions; Step3 actual product-document checks. | I, F, J |
| C65 | Each tenant-host page has one `<main>`, no skipped heading level, a skip link and a descriptive `<title>` rather than a fixed platform string, and … | P6 public-ui/route/metadata/lint-rule tests and product assertions. | I, F, H |
| C66 | Catalog to lesson works keyboard-only on both hosts with focus always visible, each page reflows at 320 CSS px, and the text and UI-component … | P6 manual record on 180dde0: Brave154.1.96.61/macOS27.0.1, keyboard English/Turkish flow, visible focus, 320CSSpx desktop reflow, calculated palettes; maintainer VoiceOver confirms both flows. PR27 description preserves attribution and links original manual evidence. | H |
| C67 | On the two-locale tenant, every visible platform-authored string resolves through the message layer **G39** records, in both locales; Localization Standards § Strings in Code, … | ADR-0027 i18n catalogue/ICU controls and WEB messages. | F, M |
| C68 | Tenant B's hidden course URL on host A returns a local HTTP `307` to the fixed `/{locale}/status/not-found` URL, followed by visible localized HTTP `404` … | public-route/resource/admission tests; PublicServerRenderingTests/PublicAdmissionRenderingTests fixed redirect/status and fault modes. | I, F |
| C69 | A page whose course or lesson carries a band the resolved revision does not declare renders the state **G5** records, without throwing and without … | P6 UI and product band-fault tests. | I, F |
| C70 | Field values containing `<script>`, an `<img onerror>` fragment and a `javascript:` URL render inert, and a `data:` or `http:` URL is rendered only if … | P6 inert plain-text/fallback renderer tests, shared ordered-field predicate and product presentation fault modes. | I, F |
| C71 | A branding token value containing `;`, `}`, `url(` or `</style>` never reaches rendered HTML, and the theme is emitted only as `--ls-*` custom properties … | Theme and public-boundaries tests; real production fixture and Step3 network/product checks. | I, F, J |
| C72 | A non-branding setting present for a tenant (the fixture's `tz` row) appears in no `[PublicSurface]` response body and in no rendered HTML, on either … | PublicSiteHttpTests/PublicEducationHttpTests and product rendering privacy checks. | I |
| C73 | A change that adds `it.skip`, `describe.skip` or `it.todo` to any Vitest file under `frontend/` fails the required `frontend` check, and each predicate in **G38**'s … | Guarded frontend runner planted controls; P5/P6 product predicates; Step3 browser hard admission. | I, F, J, M |
| C74 | No production code branches on which tenant it serves. The mechanism is **G20**'s: registered in the catalogue, Implemented and green in a required check … | Step2 DomainGenericityTests.DemoIdentities.cs, demo-genericity.test.ts, Seeder-reference fence and production presentation successor companion. | B, I, F |
| C75 | No Education type, member, column, key or DTO name carries a forbidden domain term, and Education references Customization only through its `Application.Contracts` assembly (`backend`). | DomainGenericityTests and module-dependency guards. | B |
| C76 | If **G44** activates it in this phase, the Lighthouse job runs real steps under a name without "(deferred to Phase 02d)", audits the URL … | Step3 complete local nineURL x threeRun feasibility, report/gzip/admission/offender controls and zero429 positive observer. | J |
| C77 | Every register row is closed in the form **G1** chose; every Deliverable is observed by at least one criterion; no catalogue row whose Phase … | Four P7 behavioral entries are Implemented with Node owners, planted controls and the always-running public-demo job; CorpusConsistencyTests enforces status/count alignment. | B, I, F, J, M |
| C78 | Every row in § Explicitly not in this phase names a phase whose own document carries that capability in its scope, its deliverables or … | Owner audit X01–X39 below and synchronized current output/retained seed-host carriers. | M |
| C79 | The documentation Deliverables are in the tree at exit: the Education spec's `README.md` and `permissions.md` beside `audit.md`, with its state diagram and the eligibility … | Existing Education/Customization/Testing/schema/skill files and Phase05 preservation baseline. | M |

C07 is conditional: G32 retains `*.learnstack.local`, so no seed-host migration
is required. C29 is conditional: P6 introduces no authored URL/media sink;
Phase 04/05/06 retain the named future contracts. C36 is conditional: G23 selects
uncached settings, so no settings-cache eviction rule is claimed. Other named
rows retain their full canonical conditions even when a locator is abbreviated.

The three `PublicLocaleEvolutionHttpTests` cases passed locally with zero skips.
They use the normal API-root pipeline and actual mapped-host HTTP, under
`learnstack_app`, physical READ ONLY and non-BYPASSRLS. French-only new roots are
authored while Draft; published seeded roots are not edited to create the proof.
All five migration histories and complete pre-existing EN/TR DTOs remain equal.
The organization writer refusal commits exactly its expected Denied audit;
public reads remain audit Off. Downloaded receipt I includes all three Passed cases.

## Exclusions and future owners

These are explicit phase boundaries, not unfinished P7 implementations. The
named owners' documents carry the handoff; no future decision gate is accepted
by listing its phase here.

| ID | Exclusion locator | Named owner | Evidence and boundary |
|---|---|---|---|
| X01 | Authentication, sessions, login | [Phase 02b](phase-02b-events-auth.md) | Phase02b P02b-5/6/7, Identity/session scope and criteria. |
| X02 | Token-keyed authenticated and write rate-limit budgets, and what the pre-authentication limiter does once a caller is validated | [Phase 02b](phase-02b-events-auth.md) (G14) | Phase02b G14/P02b-5 and whole-chain two-principal quota criterion. |
| X03 | Keycloak realm reconciliation (a seed step that writes to Keycloak) | [Phase 02b](phase-02b-events-auth.md) (G12) | Phase02b G12/P02b-6 and warm-realm repeat-seed criteria. |
| X04 | The `learnstack.tenancy.settings` eager invalidation event | [Phase 02b](phase-02b-events-auth.md), per [the Tenancy spec](../modules/tenancy/README.md) | Phase 02b Event Bus scope, deliverable and completion criterion explicitly own learnstack.tenancy.settings; the Tenancy catalogue names the same owner. |
| X05 | Isolation tests through authenticated or id-addressed routes | [Phase 02b](phase-02b-events-auth.md) | Phase 02b completion criteria explicitly include authenticated and id-addressed tenant/organization isolation with app-role denial and successful controls. |
| X06 | Identity domain, roles, permissions, invitations; permission keys for public reads; the rule that every endpoint carries `[Authorize]` or `[AllowAnonymous]` | [Phase 03](phase-03-identity-admin.md) | Phase03 Identity/authorization/invitations scope and deliverables. |
| X07 | A tenant-scope read across all organizations on a tenant host (the `app.scope` carrier) | [Phase 03](phase-03-identity-admin.md) | Phase03 Authorization explicitly owns app.scope and tenant-wide cross-organization carrier. |
| X08 | CMS editing, page builder, media library and tenant media origins | [Phase 04](phase-04-cms-media-pages.md) | Phase 04 Media security owns the tenant media-origin allow-policy, active URL sinks and corresponding delivery/validation criteria. |
| X09 | Per-locale publish readiness workflow, `tenant_route_slugs` registry | [Phase 04](phase-04-cms-media-pages.md) | Phase04 Page Model/Localization and tenant_route_slugs deliverable. |
| X10 | Studio authoring of translated field values (titles, bodies, slugs) and the view of untranslated gaps — where it lives is an open question | [Phase 04](phase-04-cms-media-pages.md#admin-studio-cms-screens); its screen row goes in [Phase 06 § Admin Studio — screen ownership](phase-06-renderer-admin-studio.md#admin-studio--screen-ownership) | Phase04 Admin Studio CMS Screens explicitly registers question of translated-field/gap UI placement and points to Phase06 screen ownership table. |
| X11 | `Accept-Language` negotiation for API-returned messages; the per-tenant locale fallback chain; the `/{locale}/{slug}` page routing shape | [Phase 04](phase-04-cms-media-pages.md) | Phase04 Localization scope explicitly lists per-tenant fallback, /{locale}/{slug}, publish readiness and Accept-Language API-message negotiation. |
| X12 | Authoring and translating a content type's field order and labels; the closed set of built-in primitive field types (number, boolean, date/time, select) | [Phase 04](phase-04-cms-media-pages.md) | Phase 04 Content Type System explicitly owns field order and localized label authoring; built-in field types remain a platform release boundary. |
| X13 | The schema-version migration path for stored instances; deleting a content-type revision after a zero-instance count | [Phase 04](phase-04-cms-media-pages.md) | Phase04 exact revision deletion requires zero-instance count; lazy/bulk schema migration explicit. |
| X14 | Customization deprecate and revise commands, and per-band taxonomy editing | [Phase 04](phase-04-cms-media-pages.md) and [Phase 05](phase-05-education-learning-content.md), consolidated in [Phase 06](phase-06-renderer-admin-studio.md) | Phase 04 owns deprecate/revise commands; Phase 05 owns per-band taxonomy editing, with Phase 06 consolidating Studio screens. |
| X15 | Tenant-authored page composition (`TenantPageBlock` rows and their cache family) | [Phase 04](phase-04-cms-media-pages.md); the block resolver is [Phase 06](phase-06-renderer-admin-studio.md)'s | Phase04 TenantPageBlock aggregate/migration/editor/renderer deliverable; Phase06 complete block resolver. |
| X16 | Deciding whether a customization change owes an integration event | [Phase 04](phase-04-cms-media-pages.md), per [the Customization spec § Integration-event catalogue](../modules/customization/README.md#integration-event-catalogue) | Phase04 Content Type System explicitly owns whether customization change owes integration event. |
| X17 | The `Redirect` model the CMS auto-creates when a published slug changes | [Phase 04](phase-04-cms-media-pages.md) | Phase04 Page Model/Localization Redirect auto-creation and shared namespace. |
| X18 | Search — the `ITenantSearch` port and its PostgreSQL default | [Phase 04](phase-04-cms-media-pages.md) | Phase04 Search port/PostgreSQL default section. |
| X19 | Search — the Meilisearch adapter behind that port | [Phase 09](phase-09-billing-integrations-analytics.md) | Phase09 Search/Meilisearch adapter deliverables and trigger. |
| X20 | Course versioning, programs, lesson items, completion rules; richer publish-readiness validation and versioned publication; catalog visibility separate from publication | [Phase 05](phase-05-education-learning-content.md) | Phase05 Catalog/Course versioning/lesson items/completion/runtime scope. |
| X21 | The migration that brings this phase's courses and lessons under course versions and modules | [Phase 05](phase-05-education-learning-content.md) | Phase05 What Phase02d supplies explicitly preserves existing rows/IDs/URLs/order/scope/translated bodies/exact pins/access policy in migration; re-seeding forbidden. |
| X22 | The authenticated course and lesson authoring surface, with the commands that edit a course or lesson or reorder lessons after the seed | [Phase 05](phase-05-education-learning-content.md) | Phase 05 owns authenticated post-seed edit/reorder/unpublish/delete lifecycle and its decisions; no such writer is claimed by P7. |
| X23 | The `Level` aggregate and the catalog API's levels resource; the `TenantLevelTaxonomy` editor | [Phase 05](phase-05-education-learning-content.md) | Phase05 Catalog Level/resource inheritance and Studio TenantLevelTaxonomy editor explicit. |
| X24 | The `TenantLessonItemType` read, the batched reference walk and the measured customization cost model; the `embed-html` sanitisation contract | [Phase 05](phase-05-education-learning-content.md) | Phase05 inherited TenantLessonItemType read, batched reference/cost model and embed-html contract. |
| X25 | Admin Studio, navigation menus, SEO metadata beyond what G40 settles for translated public pages, full block registry | [Phase 06](phase-06-renderer-admin-studio.md) | Phase06 renderer/Studio/block registry/navigation/full SEO scope. |
| X26 | The full `UnknownVersionBlock` / `UnknownBlock` placeholders and per-block error boundary; tenant-authored error pages; redirect handling | [Phase 06](phase-06-renderer-admin-studio.md) | Phase06 renderer explicit UnknownVersionBlock/UnknownBlock/per-block boundary/tenant404/redirects. |
| X27 | Branding configuration surface — tenant-admin editor, theme preview, logo, header and footer settings; the per-organization branding override (`OrganizationBranding`) and the token merge | [Phase 06](phase-06-renderer-admin-studio.md) | Phase06 Tenant branding/own Studio work explicit logo/header/footer/preview/OrganizationBranding merge. |
| X28 | Preview of draft courses and lessons through the production renderer | [Phase 06](phase-06-renderer-admin-studio.md) | Phase06 own Studio work explicitly preview published/draft through production renderer. |
| X29 | Browser end-to-end flows (Playwright) and axe checks through Playwright | [Phase 06](phase-06-renderer-admin-studio.md), per [Testing Standards § End-to-End Tests](../standards/06-testing.md#end-to-end-tests); any narrow smoke this phase adopts is G38 | Phase 06 owns full Playwright Test/axe flows; the narrow P7 Playwright-library browser and Lighthouse proof is explicitly separated. |
| X30 | Enrollment, learner portal, progress tracking; Course Access — whether content published anonymously here stays on the public path once enrollment exists | [Phase 07](phase-07-enrollment-learner-portal.md) | Phase07 enrollment/portal/progress/CourseAccess and public-path-vs-enrollment boundary explicit. |
| X31 | Live classroom | [Phase 08c](phase-08c-classroom.md) | Phase08c Live classroom/ILiveClassProvider/LiveKit deliverables. |
| X32 | Billing | [Phase 09](phase-09-billing-integrations-analytics.md) | Phase09 billing scope/deliverables. |
| X33 | Hub, entitlement gating | [Phase 02c](phase-02c-hub-foundation.md) | Phase02c Hub-backed entitlement/provider/enforcement scope. |
| X34 | APISIX as the edge gateway for a non-development deployment; gateway or CDN caching; edge rate limiting | [Phase 11](phase-11-production-hardening.md#security), on [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger | Phase 11 explicitly owns gateway/CDN response caching, authority keys, invalidation, private-content exclusion and its demand trigger. |
| X35 | Plan-differentiated API rate limits (`LimitKeys.ApiRatePerMinute`) | [Phase 11](phase-11-production-hardening.md#security) for the key's gate; [Phase 02c](phase-02c-hub-foundation.md) for its enforcement path | Phase11 Security explicitly owns LimitKeys.ApiRatePerMinute gate and links Phase02c generic enforcement path. |
| X36 | The L2 cache tier behind `ICacheService` (default `InMemoryCacheService`) | [Phase 11](phase-11-production-hardening.md), on [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger | Phase11 explicit Valkey-backed ICacheService/L1-L2 trigger. |
| X37 | Content-Security-Policy and the other secure headers | [Phase 11 § Security](phase-11-production-hardening.md#security), per the [standards index](../standards/README.md#honest-status-today) row for Security Standards | Phase11 Security explicit secure headers/CSP. |
| X38 | `GET /readyz`; the production Dockerfile and image pipeline; the performance baseline and load tests against Performance Standards | [Phase 11](phase-11-production-hardening.md) | Phase11 readiness/deployment images/performance/load-test scope/deliverables. |
| X39 | Custom-domain TLS automation | [Phase 11](phase-11-production-hardening.md), on [ADR-0035](../decisions/0035-demand-gated-infrastructure.md)'s trigger | Phase11 explicit custom-domain TLS ACME/DNS/renewal trigger. |

## Phase-exit reconciliation

| ID | Obligation | Required evidence |
|---|---|---|
| E01 | Every Completion Criterion holds with named/manual evidence | All C01-C79 rows, the three locale/branding/combined-scope HTTP/pipeline cases; actual B/I/F/O/J/H receipts linked at exact final head where applicable. |
| E02 | Every register row closed through G1 vehicle and carriers, including G32 development transport | Dated closure/link census; Accepted ADRs retained, implementation receipts distinct from decisions; Phase02b native HTTPS carriers reconciled. |
| E03 | Phase02a and Education schema/request isolation green as app role, zero skips, required | Exact final backend/backend-integration TRX/rollup, role/isolation evidence and skip guard. |
| E04 | OpenAPI/SDK/Lighthouse required and shown able to fail; live list/date; no deferred job names | O/J offender controls, successful final real PR rollup, dated seven-context strict protection read-back, all other settings preserved. |
| E05 | Clean make demo prints both working addresses; sites render with Keycloak stopped | J clean lifecycle/readiness/Keycloak status/browser/artifact/cleanup receipt; H human context retained. |
| E06 | G20 Implemented and green required | B/F nonempty census and dirty/clean controls, I schema-replacement companion, Step2 record. |
| E07 | Accessibility and renderer PR record | D31/C66 PR27 link and original manual attribution plus F/I/J automated controls; no inferred new manual pass. |
| E08 | Zero 02d Registered/Awaiting catalogue rows and standard/index alignment | Four P7 promotions with actual behavioral owners and hard CI, computed counts and Standard_Status_Headers_Match_The_Index exact-head receipt. |
| E09 | Delivery record names moved P02b-0 premises | /api/v1 contract, *.learnstack.local native HTTPS/manual trust, visitor+peer quotas, seed default-org/tenant scopes, no-cache settings/eager event owner, single bootstrap; do not Accept auth/BFF future gates. |

The [Step 4 preservation and P02b-0 handoff](phase-02d-walking-skeleton.md#p02d-7-step-4--ci-enforcement-and-phase-exit-2026-10-11)
names the retained Education data, live API/host/trust/limiter/bootstrap premises
and future settings-event ownership. Phase 05 migrates the existing records; it
does not treat reseeding as migration. Course Marketplace ADR-0049 and Phase 09a
remain Proposed and are outside this exit.

All E01–E09 obligations pass against the receipts and named evidence above, as
recorded in the positive CI/exit closeout. Maintainer PR review and merge are
separate actions; subsequent PR heads retain the seven required checks.
