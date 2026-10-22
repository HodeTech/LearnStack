# Phase 02d — delivery log

The dated record of
[Phase 02d: Two-Tenant Walking Skeleton](phase-02d-walking-skeleton.md).
The phase document holds the plan and the current Status; this log holds what
each packet decided and delivered, as
[ADR-0069](../decisions/0069-phase-documents-and-delivery-logs.md) decides.

- **Append-only.** A new entry is a `###` heading under its packet's `##`
  section, with its date in the heading. A shipped packet's entries are not
  rewritten; a false citation gets the dated inline correction
  [Documentation Standards § Doc Types](../standards/13-documentation.md#doc-types)
  prescribes.
- **Decision packages are normative.** An Accepted package's answers bind exactly
  as the phase document would; the phase document's decision register links each
  one.
- **Moved, not rewritten.** Every entry dated up to 2026-10-22 moved here from the
  phase document with its heading text and anchor unchanged. Heading levels shifted
  to sit under the packet sections, and links whose targets now live in the other
  file were retargeted. Position words inside a moved entry — *above*, *below*,
  *the register* — still mean the single document it was written in; the plan
  sections they point at now live in the phase document. The
  [Status archive](#status-archive) keeps the retired Status block.

## P02d-1 — Education schema and database-level isolation

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

### Delivery Record (P02d-1)

**Decision pass — 2026-09-14.** Maintainer approval closes G1, G2, G3 (values and
publication/transition contract), G4, G5 (column), G6 (a), G7 (database controls and
factory derivation), G8, G9, G10 (order), and G26 (slug grammar), through the
[accepted answers](#p02d-1-accepted-answers). ADR-0048 is Accepted and ADR-0003 gains
Accepted Amendment 6. At this decision commit, the Education spec became design stable,
the module matrix list gained Education, and the four structural rules were Registered.
The following records distinguish that accepted design from its implementation and
verification.

#### Step 1 — Shared database controls (2026-09-14)

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

#### Step 2 — Education schema and isolation (2026-09-14)

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

#### Step 3 — Packet completion (2026-09-14)

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

##### PR verification (2026-09-14)

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


##### Independent PR review follow-up (2026-09-14)

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

##### Merge and closeout (2026-09-14)

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

## P02d-2 — Writers and seed

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

## P02d-3 — Read internals

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
started. The [packet gate table](phase-02d-walking-skeleton.md#packets-and-decision-gates) owns the complete
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

## P02d-4 — Public read API and contract checks

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
fails before the public-method filter and passes afterward. A fresh GPT-6.1-sol
xhigh reviewer approves `6b8e743`: clean production GET/HEAD pass, ordinary and
decorated private sends fail, and removing only IsPublic in a temporary assembly
restores the decorated bypass. No actionable correction remains. The final clean
Release build has zero warnings/errors and all 195 architecture cases pass with
zero failures/skips. Step 2 and both independent review rounds are complete;
Education implementation proceeds in Step 3.

### P02d-4 Step 3: public Education reads

**Implementation — 2026-10-04; both review rounds complete.** Education serves all three
GET/HEAD pairs through marked audit-Off queries and the read-only pipeline. The
source store intersects immutable host scope with normal EF filters and RLS.
Published/nondeleted exact-locale marketing is separate from public-parent
inventory/body eligibility; the body SELECT requires its eligible public parent
and exact URL relationship in the same SQL statement. Restricted courses retain
marketing with a null outline; hidden content has the same masked not_found.

Purpose-built DTOs expose ordered string fields and authored label locales only.
Customization carries admitted `required` metadata through the internal display
contract; no read evaluates a JSON Schema. Optional missing fields are omitted,
unknown properties are omitted and malformed selected/required content yields a
whole unavailable state. Missing exact pins/bands remain bounded without rebinding.
Enabled content locales are exact, while display fallback remains Pattern B.

The bounded v1 cursor codec checks canonical unpadded base64url, exact JSON members,
version, lower-case scope/UUIDs and UTC microsecond timestamps or nonnegative sort.
Scope binds normalized host digest, host tenant/organization, canonical locale and
endpoint/order; page size is excluded. Syntax/known-scope checks precede the data
transaction; an outline's parent is checked after eligible course lookup and before
inventory/definition loading. PostgreSQL tuple seeks preserve timestamp/sort ties.

A new no-schema-evaluation architecture guard inspects all four public handlers
and follows production helpers/implementations, with direct, concrete and generic
interface offenders plus a clean control. Controller guards retain all previous
planted bypasses. The persistence walk distinguishes a value object's declared
identifier interface from acquisition of an injected identifier-interface service;
clean metadata, direct domain-ID and hidden service implementations are controls.
Approved read methods are explicitly enumerated in the aggregate-write census;
key-only writes remain visible. The route census covers all eight GET/HEAD actions.

HTTP/database cases cover exact locale and alternate URL pairs, nullable values,
source lifecycle withdrawal after warmed definition reads, hidden draft/restricted/
foreign/sibling/wrong-parent content, body/definition corruption, cursor boundaries,
claim/host narrowing, scope hatches, transaction admission and no normal audit rows.
Fixture mutations assert affected rows and announce their explicit write scope;
owner-only satellite corruption is setup, never the observed public connection.
Public execution is proved as non-superuser/non-BYPASSRLS learnstack_app, READ ONLY.

**Local measurement.** The actual production SELECTs are captured and explained
with ANALYZE/BUFFERS as the application role, without forcing indexes. The tiny
seed uses the course live-key and translation indexes; both outline and body plans
also use `ix_lessons_tenant_id_course_id_sort_id`. The catalog ordered partial index
is available but not selected at this cardinality; no representative large-data or
production p95 claim is made. Captured catalog/marketing/outline queries fetch no
lesson body, and the body query includes access/publication/scope/locale predicates.

A local Docker sample records a first HTTP request, one warmup and ten observations
per Education route and tenant. Catalog/course/lesson warm medians in one run are
4.16/6.57/6.17 ms for English and 7.65/7.70/7.18 ms for Yoga. Payloads are respectively
551/415/479 and 733/504/662 UTF-8 bytes. Full plans and timing ranges are test output;
these six local medians are neither a production p95 nor a rendering budget proof.
OpenAPI snapshot, SDK/drift and required-check activation remain Step 4.

**Pre-review verification.** The final clean CI=true Release build has zero
warnings/errors. All 2831 .NET cases pass with zero failures/skips: 1643 unit,
198 architecture, one contract, 171 Docker-free integration and 818 Docker
integration. The final architecture run follows the production-census control;
TRX counters preserve all five suite results. Disabling only transitive helper
walking or implementation walking makes the new planted guard controls fail;
both deliberately broken variants are restored before the final clean run.
Formatting verifies zero changed files and diff hygiene passes. The link/anchor
sweep and unchanged P02d-1–3 delivery records/ADR-0052 prefix are verified.
Both independent review rounds follow this implementation commit.

**Round 1.** Independent GPT-6-astra and GPT-6.1-sol xhigh reviewers inspect
`b1bff6f..b5c3be2`. The security pass finds no additional isolation, eligibility
or cache issue. The protocol pass finds a malformed-Unicode cursor exception:
JSON strings materialize lazily, outside the original parse exception boundary.
A compiled production probe independently reproduces the unexpected exception.
The fix strictly decodes UTF-8 and refuses escaped-surrogate materialization
failures in both codecs. Unit and real GET/HEAD cases prove bounded 400 before
data access, including property-name corruption and a valid escaped-string control.
The review's coverage limits are closed too: masked 404 fields are checked in
full, and real database cases prove deleted-anchor continuation and current-row
restart for both catalog and outline. The clean Release build and 33 protocol,
198 architecture and 81 Education database/HTTP cases pass with zero skips.
Formatting and diff hygiene pass; a fresh second review follows the fix commit.

**Round 2 and step closeout.** Fresh GPT-6-astra and GPT-6.1-sol xhigh reviewers
independently approve `b1bff6f..6c09888`, including the Unicode refusal fix and
stronger HTTP/pagination proofs. Both run the 45 protocol/display unit cases;
the protocol reviewer also runs 13 public-surface/aggregate-write architecture
cases. All pass with zero failures/skips; neither reviewer mutates files. No
verified finding remains. Step 3 is complete after both review rounds; Step 4
continues with the approved OpenAPI, SDK and CI contract.

### P02d-4 Step 4: contract, SDK and CI

**Complete — 2026-10-04; PR #25 awaits maintainer review and merge.**
The production
OpenAPI transformer documents manually parsed locale/cursor/page parameters,
shared Problem Details, closed enums and bodyless HEAD. The sole committed v1
snapshot matches the actual production composition. Contract controls assert all
four paths/eight operations, exact status/query inventories, required public fields,
recursive schema/CLR leaves and complete error shape. Planted missing operations,
parameters/statuses, reordered arrays and nested private/raw fields fail.

The SDK root exports generated types only; `/server` implements four generated
GET contracts through an injected transport. Encoded paths/queries, no authority
options, allowlisted success shapes and validated Problem Details have fake
transport controls. API errors project into Standards 09's closed AppError union;
unknown extensions are ignored. Malformed JSON, invalid response shape, transport
failure and caller cancellation remain separate. The unused client export is gone.
P02d-5 still owns the configured trusted server adapter and first consumer.

The always-running `openapi diff` workflow installs checksum-verified oasdiff and
uses the explicit severity policy. Sixty mutation/control fixtures cover
representable ADR-0024 rows and verified placement/type-direction tool gaps.
A deterministic companion traverses reachable request/response schemas and local
object/schema references. It enforces type changes, closed enum additions and
open enum closure/removal, normalizing only matching open enum lists. A combined
open addition plus another breaking change still fails. Current public enums stay
closed; this does not claim an open-enum generator/consumer has shipped.

PR base SHA, push-before SHA and explicit manual base ref select verified git
snapshots. Missing/deleted heads or failed ref/read/proof fail. Only an actual
operation-free base production composition with no snapshot permits bootstrap;
the verified phase-entry base was built and served with zero v1 operations. Eight
snapshot-admission controls accompany this proof. Artifacts retain original and
normalized specs, refs/hashes, pinned tool/policy and readable/machine reports.
Required frontend CI regenerates the committed SDK and refuses scoped drift.
The approved live sixth check is applied only after the real job runs; no settings
mutation is claimed by this implementation note.

**Full-suite fixture correction.** The first complete Docker run exposes test
host lifetime leakage: disposed clients retain child hosts and their independent
Npgsql pools until collection teardown. PostgreSQL reports SQLSTATE 53300, and a
new three-client control observes connections rising from one to four. Client
ownership now disposes the private child host and pool too. This changes the test
harness, not the production read contract; targeted and complete regression
results are recorded after execution.


**Pre-review verification.** Clean CI=true Release build: zero warnings/errors.
All 2843 .NET cases pass with zero failures/skips: 1644 unit, 198 architecture,
four contract, 171 Docker-free integration and 826 Docker integration. The new
client-lifetime control fails against the original fixture (four connections
instead of one), and passes after host ownership; the combined 102 public HTTP
cases and full Docker run pass. Frontend typecheck/lint/build and all 66 Vitest
cases pass (53 SDK, 13 web). Twenty-three real pinned-tool policy controls and
eight snapshot-admission controls pass. The actual base Program bootstrap has
zero v1 operations. Formatting, actionlint, diff hygiene and changed-document
links/anchors pass. Served snapshot and generated SDK are refreshed from the
current production composition. Both independent review rounds follow this
implementation commit; live check registration follows the real CI job.

**Round 1 and verified fixes.** Independent GPT-6-astra and GPT-6.1-sol xhigh
reviewers inspect `5d72c2f..5f49f62`. They reproduce three Major contract/policy
findings and two Minor SDK/documentation findings; no Blocker. Actual pinned CLI
probes show root closed-enum additions, referenced open-enum closure and numeric
type changes passing the original gate. The companion now enforces these ADR-0024
rules in both request and response positions, including local object references;
nine added controls cover root, array, reference and request/type directions.
All 32 pinned-tool controls pass (22 breaking, ten nonbreaking), alongside eight
snapshot-admission controls. The original root-enum reproduction now fails the
policy even though the underlying tool still reports no break.

Problem Details parameter values now match the existing string-valued
`LocalizedMessage` carrier in served OpenAPI, snapshot, generated types and SDK
parsing. Contract controls compare the nested schema with CLR and actual factory
serialization; empty/number/object schemas fail. SDK controls refuse numeric,
object and null parameters. Lazy URL construction keeps ill-formed surrogate
paths within an `invalid-request` result; already-aborted calls stay `cancelled`
without transport. The sibling frontend standard no longer treats G31 as open.

The complete .NET suite is repeated: all 2843 cases pass with zero failures/skips
(1644 unit, 198 architecture, four contract, 997 integration, including 826 Docker
cases). SDK typecheck/lint and all 58 cases pass. These fixes change the
schema/SDK/policy, not database read behavior.
Fresh Round 2 review follows the fix commit.

**Round 2 and verified fixes.** Fresh GPT-6-astra and GPT-6.1-sol xhigh reviewers
inspect `5d72c2f..db9bab6`, independently rerunning 32 policy controls, eight snapshot
controls, 58 SDK and four contract cases; the SDK reviewer also runs the client
disposal control. All pass, but review reproduces three additional policy defects
and two Minor SDK/test gaps. Per-occurrence local reference materialization and
immutable analysis now prevent an open enum normalization from erasing a closed
consumer; both object-key orders have controls. Effective operation parameters
honor inheritance/overrides and the CLI explicitly flattens parameters. Parameter
and header schema/content forms are mirrored into temporary policy-only bodies
with source-location metadata, so the pinned tool checks their fields/validators.
The runtime snapshot is unchanged by this comparison normalization.

The shared-owner reproduction now refuses even where the raw tool already reports
the break. Inherited tightening/removal/requiredness and relocated type changes
refuse; equivalent relocation remains compatible. Content-form property removal
and request-validator tightening refuse. A response minimum-length increase is
kept as a compatible stronger output guarantee, rather than misclassified as an
input rejection. Fifty real CLI controls pass (36 breaking, fourteen nonbreaking)
with eight snapshot controls. The SDK refuses exact dot segments before dispatch;
all 60 SDK cases/typecheck/lint pass. Added method and path mutants fail the
eight-operation contract control; all four contract cases pass with zero skips.
A fresh focused review verifies these final policy/SDK corrections before closeout.

**Focused verification and recursive fix.** A fresh GPT-6-astra xhigh reviewer
independently verifies all 50 controls, eight snapshot cases, 60 SDK cases and
four contract cases. It confirms the earlier bypasses are closed, then reproduces
a recursive open-enum addition wrongly refused at an untouched component cycle
edge. Recursive edges now point to private effective schema copies, identified by
stable declaration locations so documentation changes do not alter identity.
Analysis/normalization reach these cycle copies while preserving closed consumers.
Six added controls cover open addition, unchanged recursion, documentation edits
and mixed open/closed schemas in both orders. All 56 real CLI controls pass
(39 breaking, seventeen compatible), with all eight snapshot controls.

**Final recursive verification fix.** A fresh GPT-6-astra xhigh reviewer verifies
all 56 controls and 27 additional probes, then reproduces an unchanged inline
recursive schema crashing the normalizer. The same occurs inside a Response
component. Stable declaration identity now applies to every local schema location,
rather than only Schema components; four added real CLI controls cover unchanged
and open-addition cases in both placements. Fresh independent verification at
`ef5c4d8` approves: all 60 real CLI controls pass (39 breaking, 21 compatible), all
eight snapshot cases and 27 additional probes pass, and all four original
reproductions return compatible. Both Step 4 review rounds and focused fix review
are complete. No verified finding remains.

**Pre-PR closeout.** All four implementation steps and their two independent
review rounds are complete on development. Final frontend validation passes
typecheck, lint, build and all 73 cases (60 SDK, thirteen web). Complete .NET
regression remains 2843 passed, zero failed/skipped, as recorded above; the later
compatibility fixes change scripts/fixtures only, and the final operation mutants
pass all four contract cases. Formatting, actionlint, link/anchor and frozen-record
checks pass. Live CI and sixth required-check API read-back follow PR creation;
packet exit is not yet claimed. P02d-5 is next, with its decision pass not started.

**Live CI and approved check rollout — 2026-10-04.**
[PR #25](https://github.com/HodeTech/LearnStack/pull/25) opens from development to
main. At head `0a14c7474f7cb383a789adf1966d28e67dbc3954`, all six jobs pass in
[the real PR run](https://github.com/HodeTech/LearnStack/actions/runs/37188555958):
backend, Docker integration, frontend, meta, secret scan and `openapi diff`.
Lighthouse is the sole deferred job, owned by P02d-6/G44. Backend TRX artifacts
confirm 2017 Docker-free cases and 826 Docker cases, with zero failures/skips.
The Linux OpenAPI artifact confirms pinned oasdiff 1.33.0, all 60 controls
(39 breaking, 21 compatible), verified base
`d1a47369d82f8b6d91cb325f56d4c42382ec77b6` and actual operation-free baseline
composition; the comparison passes.

After that successful `openapi diff` job, the approved PATCH adds only its GitHub
Actions context (`app_id=15368`). Fresh full protection read-back proves the five
existing contexts remain, all six have Actions attribution, strict up-to-date
checking remains true, and every other protection field is identical. This includes
zero required approvals and admin enforcement off under the existing maintainer
deferral; no unrelated protection is changed.
[CONTRIBUTING](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main) owns
the current list. All six live contexts match the successful PR check rollup.

P02d-4 implementation and rollout are complete; maintainer review/merge remains.
The final documentation commit reruns the same six jobs before handoff. P02d-5
owns the next decision pass, trusted server transport and first consumer. P02d-6
owns pages/rendering; P02d-7 owns the two-host demonstration and full-stack exit.
No browser delivery, marketplace commerce or production p95 is claimed.

**Final external documentation review.** All six required jobs also pass at
`ddc96a566cd5babf2af3779314471aac33642b95` in
[the final documentation run](https://github.com/HodeTech/LearnStack/actions/runs/37189085766).
CodeRabbit then reports three Minor documentation comments. Current code confirms
cursor/bootstrap delivery and implemented endpoint/write-refusal controls; the
standards now reflect them. ADR-0040 Amendment 9 adds dated frame-delivery evidence
without rewriting its acceptance-time status. The catalogue identifies the table
parser’s actual limit: row method cells remain manual, while the separate endpoint
census mechanically enforces exact GET/HEAD. No production code changes.

**External review remediation — 2026-10-05.** Findings against `1d938388` are
verified against current code. ADR-0036 Amendment 9 and ADR-0050/0051 Amendment 3
record delivered provenance, access enforcement and presentation. ADR-0052
Amendment 12 adds current-delivery navigation. All acceptance statements and
earlier amendments remain unchanged; dated Status disclosures identify them as
history. Browser rendering and authenticated grants retain their named owners.

Unavailable catalog levels now emit one count-only Warning per summary batch,
rather than one per row. A 100-row regression proves the bound and absence of
private diagnostic fields. The nullable-default refusal remains defensive:
a broken reader contract is refused, as a focused unit case demonstrates.
Cleanup proofs now inspect a fresh live writable transaction on the same
connection after both pre/post-activation setup faults, while the poisoned unit
still refuses another frame. Injected Npgsql logging uses the shared application
data-source guard; direct and transitive bypass-role cases exercise its logged
overload with restored-role positive control.

The suggested common provenance refactor introduces no correctness fix and is
not bundled. The Education spec explicitly records the controller/helper guard's
scope and independent handler-connection residual risk already accepted by
ADR-0052; current handlers use their approved read ports. The pre-existing
membership-reader DI backstop is outside this PR, rather than a P02d-4 defect;
production still registers the deny-all reader.

Local verification: clean Release build with zero warnings/errors, format and
link/anchor checks pass. Full .NET regression is **2847 passed, zero failed or
skipped**: 1646 unit, 198 architecture, four contract and 999 integration. The
four added cases are two display/configuration facts and two logged role-guard
theory cases. Contract tests again verify the production-served v1 document;
the committed OpenAPI/SDK contract is unchanged. Live main protection still
requires all six Actions contexts with strict checking; no settings are changed.

**Remediation review closeout.** Two fresh independent rounds review
`1d938388..ef86f64`: Round 1 code and documentation reviewers both Approve;
Round 2 code and documentation reviewers both Approve. No actionable finding
remains. Both documentation reviewers independently verify preserved ADR history,
delivery claims, phase ownership and all 590 relative links/anchors in the six
changed Markdown files. The complete local regression above verifies the code
fix; this closeout adds delivery evidence only.

### P02d-4 merge and closeout (2026-10-08)

The maintainer merged [PR #25](https://github.com/HodeTech/LearnStack/pull/25) at
2026-10-08 15:25:18 UTC. GitHub records final PR head
`f8adb110ff7fca2a15bbd3f87c1546c62852dc4c` and merge commit
`b83175538cf6f9c5415294a8f77dadd9c82588dd`. The merge's second parent is that
head, and both commits have the identical tree. Development remains the working
branch; the fetched main ref verifies the merge without changing branches.

- All six required checks pass on the
  [final PR head](https://github.com/HodeTech/LearnStack/actions/runs/37794924088).
  Downloaded TRX artifacts confirm **2847 passed, zero failed or skipped**:
  1646 unit, 198 architecture, four contract, 171 Docker-free integration and
  828 Docker integration. Frontend CI passes typecheck, lint, build, SDK drift
  verification and **73 tests** (60 SDK, thirteen web).
- All six required checks also pass in the
  [merge-commit run](https://github.com/HodeTech/LearnStack/actions/runs/37800631591).
  Independently downloaded merge-run TRX confirms the same 2847 passing cases
  and zero failures/skips. Its OpenAPI artifact matches the merge SHA and
  snapshot hash. Read-only protection verification confirms all six Actions
  contexts with `app_id=15368` and `strict: true`; no setting is changed.
- OpenAPI artifacts match the final head, verified base and committed snapshot
  hash. All **60 policy controls** pass (39 breaking, 21 compatible). The final
  workflow correction adds six actual-event selection controls to the eight
  admission/bootstrap controls: **14 Python tests** pass locally and in CI.
  Four local selector mutants fail. Zero-before pushes select the head's parent;
  ordinary push, PR and manual refs retain their existing contracts.
- The final review corrections retain unchanged ADR decisions and align delivery
  navigation. Two fresh focused reviewers approve each final follow-up
  (`7e07613..7ad4b91` and `7ad4b91..f8adb11`). CodeRabbit succeeds at the final
  head; all five inline threads are resolved. The four implementation steps'
  two-round review records above remain unchanged.

**P02d-4 is closed.** Host-scoped anonymous site/catalog/course/lesson GET/HEAD,
physical read-only frames, exact enabled-locale/access eligibility, bounded public
projections and cursors, the served OpenAPI baseline, typed SDK and compatibility
gate are delivered. No database migration is added by P02d-4.

**Next: P02d-5 — Server-rendering path.** Its implementation and decision pass
have not started. The [packet gate table](phase-02d-walking-skeleton.md#packets-and-decision-gates) owns its
complete prerequisites; the [decision register](phase-02d-walking-skeleton.md#the-decision-register) still
leaves development transport/hosts, trusted-hop topology and rate limiting,
server SDK transport, middleware/locale entry, rendering/cache policy and the
frontend test/skip fence open. P02d-4 supplies the completed API prerequisite;
it does not accept those later decisions.

P02d-6 owns the three public pages, text-card renderer, safe theme injection,
language attributes and accessibility. P02d-7 owns `make demo`, the two-host
full-stack evidence and phase exit. G44 retains the Lighthouse activation
decision. Phase 02d remains in progress; no browser delivery, authenticated
learner grants or Course Marketplace commerce is claimed by this closeout.

**Documentation-only closeout validation.** Five local corpus-consistency tests
pass with zero failures/skips. The seven changed Markdown files pass the local
relative-link/fragment audit (825 link occurrences, 342 fragments), added-prose
width and diff checks. ADR-0052 changes are insertion-only; previous P02d-4
delivery notes and the P02d-1 suffix remain byte-identical. No production code
changes or new decisions are introduced.

## P02d-5 — Server-rendering path

### P02d-5 decision package (2026-10-08)

**Accepted — 2026-10-08, verified against development `88f52c4`.** The maintainer
approved ADR-0053 and this package before implementation. P02d-4 is merged;
[ADR-0053](../decisions/0053-trusted-public-server-rendering.md) owns the bounded
replacement rules. Only the named P5 gate parts below close; delivery remains
separate from acceptance.

#### Baseline and document review

The preparation baseline is development `88f52c4`. Three independent read-only
reviews cover trust/topology/limiting, frontend/runtime semantics and governance.
Their conclusions are checked against source, not accepted as instructions.

- P02d-4 supplies bootstrap, Education GET/HEAD, public DTOs, READ ONLY, no-store,
  cursors, a served snapshot, typed injected SDK and required diff/drift checks.
- SDK Vitest tests and AppError parsing are already delivered. G38(a)'s older
  missing-script premise and G35's implied missing parser are historical premises.
- Middleware still forwards client headers, writes host into `x-tenant-id`, invents
  `en` and refuses production requests with `503`; no configured caller exists.
- Installed Next 15.5.18 preserves a supplied `x-forwarded-for` when adding socket
  metadata. Middleware cannot turn it into reliable visitor identity.
- API limiting is 60 calls/minute per socket peer. Forwarded-header middleware is
  unwired; reading the connection feature alone does not protect a mutated peer.
- API startup permits both hop lists empty in every mode, refuses partial lists,
  invalid CIDRs and blank/secrets shorter than 32 characters. ADR-0053 explicitly
  adopts that policy in place of ADR-0036's older non-Development/byte wording;
  a dated ADR-0036 Amendment discloses it without rewriting the Accepted body.
- Standards 07's delivered injected SDK contradicts ADR-0036's older exact file
  chosen to set hop headers. The new decision must disclose its replacement.
- Standards 07 and Architecture 14 still prescribe cached public rendering under
  open G37; a changed ongoing rule requires an ADR, not a quiet code exemption.
- The route skill's obsolete P4 stub description is corrected during preparation.
  Frozen P1–P4 delivery records and Accepted ADR bodies remain unchanged.

The reviewed carrier families are CLAUDE/README, vision/MVP, roadmap timing and this
phase; ADR-0009/0035/0036/0040/0050/0051/0052 and the ADR index; architecture
09/12/14/30; standards 03/04/06/07/08/09/10/11/12/13/15/16/20/21; the route,
local-dev, seed and test workflows; current API/SDK/web/compose/CI source. Future
renderer decisions G39–43 are inputs, not accepted by this package.

#### P02d-5 accepted answers

| Gate part | Accepted answer and authority |
|---|---|
| G6(c) | Canonical enabled locale prefixes; `308` for enabled noncanonical spelling, `307` for default-locale entry, masked `404` for malformed/disabled/unknown prefixes. Exact matrix below |
| G20: exemptions | Demo identity literals only in `SeedData`, seed/development launch/configuration, operational docs and test-owned assertions/fixtures. No host/domain-specific branch in middleware, transport, renderer or modules; a generic host normalizer is not a demo exemption. P6 decides renderer subjects, P7 implements the complete Registered guard |
| G21: cookies | Public entry neither sets nor uses cookies; no Authorization/session forwarding. Preserve P2's no new remote subresource contract. Phase 02b owns session cookies; Secure-cookie TLS proof here is a test-owned transport control, not authentication implementation |
| G30: headers | Native ingress verifies captured host/peer provenance; middleware rebuilds private context; one configured server adapter emits the closed API-hop set in ADR-0053. Education locale is query-only; no tenant/organization assertions |
| G32 | [Accepted — 2026-10-08](#local-setup-and-ingress-details): local hosts and TLS |
| G33 | [Accepted — 2026-10-08](#local-setup-and-ingress-details): local topology, shared source and ingress |
| G34 | [Accepted — 2026-10-08](../decisions/0053-trusted-public-server-rendering.md#api-hop-and-anonymous-budgets): authenticated visitor and peer budgets |
| G35 | [Accepted — 2026-10-08](../decisions/0053-trusted-public-server-rendering.md#configured-caller-and-rendering): configured caller/deadlines; [Frontend Observability](../standards/10-observability.md#frontend-observability) owns Phase 11 logger/Sentry/web-vitals |
| G36 | [Accepted — 2026-10-08](#public-entry-matrix): membership-first entry and redirects |
| G37 | [Accepted — 2026-10-08](../decisions/0053-trusted-public-server-rendering.md#configured-caller-and-rendering): dynamic no-store renderer policy |
| G38(a) | Vitest covers normalization/stamps/header stripping, locale/entry matrix, configured origin/header construction, errors, deadlines/body/cancellation and tracing; production-build/socket fixtures cover runtime boundaries. SDK tests remain inherited |
| G38(d) | Extend existing non-skippability rule to tested frontend workspaces. Fail skipped/todo, empty/unreadable reports and discovered test packages without an execution script; planted actual runner cases prove failure |
| G44: whether | Activate full-stack Lighthouse in P02d-7, after P6 pages and G45's shared demo harness. P5 updates live deferred-job ownership carriers; it does not audit placeholders. P7 still selects tools, URLs, assertions/budgets and status promotions before harness code |

This acceptance closes only the named P5 parts. G38(b,c), G39–43, G44's harness
details and G45 remain with P6/P7. No market, auth, CMS, media or grant decision moves.

#### Local setup and ingress details

Keep the two hosts already declared in `SeedData`; do not create a second hardcoded
host registry in production code. The explicit workstation step adds those names
to `127.0.0.1` in the hosts file and creates a leaf certificate with their exact SANs
and `localhost` for readiness. The documented tool is mkcert (BSD-3-Clause); its CA
trust/install step is performed explicitly by the developer. No script edits hosts,
installs system trust, ignores TLS errors or shares the CA private key. Leaf/key
paths live under gitignored `.data/`; examples contain paths, never key material.

The ingress owns port 3000 on IPv4 loopback and delegates requests/upgrades to Next's
supported custom-server interface. It strips inbound forwarding/internal/framework
override headers before minting the single provenance envelope specified by
[ADR-0053](../decisions/0053-trusted-public-server-rendering.md#ingress-and-provenance).
Middleware verifies it before bootstrap, rebuilds downstream request headers from
the allowlist, and the server caller verifies it again before SDK use. Direct stock
`next dev/start` is unsupported and must fail closed if callers forge plain carriers;
a valid production-build control proves the supported launcher actually works.

One ignored local source provides a freshly generated 32-byte random ASCII-encoded
hop secret to both processes. API networks and secret list arrive together through
launch configuration; do not commit a network-only Development default. The native
launcher/Next caller use the same private source for domain-separated ingress MACs.
`.env.example` owns the variable vocabulary; the web example documents its narrow
projection and matching/conflict checks. The loader must not shell-evaluate env
values or echo credentials. Empty/malformed renderer configuration refuses startup;
ordinary no-hop API startup and existing Development fixtures continue to work.

P5 tests use isolated certificates and trusted test roots, never disabled certificate
verification. Browser TLS/Secure-cookie checks are evidence to obtain, not claims
already proved on this workstation. P7's CI harness uses isolated per-run roots,
host resolution and secrets; it never installs a permanent developer CA.

#### Public entry matrix

The fixed public section name is `courses`; section-name localization remains
Phase 06. After bootstrap, enabled-locale membership takes precedence over every
shorthand or scaffold name. No locale is rejected just because its grammar-valid
name is `courses`, `studio` or `portal`.
For a well-formed request, bootstrap runs before locale/redirect decisions. Unknown
or platform host, unavailable live scope and valid empty locale configuration return
masked `404`. Dependency/invalid stored configuration or transport failure returns
bounded `503`; API `429` remains `429` with validated Retry-After. No body embeds
provider errors, host IDs or internal headers. P6 owns localized page-state rendering.

| Input on a bootstrapped live host | Answer |
|---|---|
| `/` | `307` to `/{defaultLocale}/courses` |
| `/courses`, `/courses/{slug}`, `/courses/{slug}/lessons/{lessonSlug}`, when `courses` is not an enabled locale | `307` to the same path with the configured default prefix |
| `/{enabledCanonicalLocale}` or its one trailing slash | `307` to `/{locale}/courses` |
| Enabled noncanonical locale prefix on a supported public path | `308` to its canonical prefix; only after enabled membership succeeds |
| Canonical enabled prefix on a supported public content path | Continue with verified host/peer and canonical route locale; P6 supplies pages |
| Exact current `/studio` or `/portal` scaffold root on a live host, when that name is not an enabled locale | Continue to the existing scaffold without a public locale or authentication claim; no prefix-wide exemption |
| Malformed, disabled, unknown or overlength locale prefix; unknown section/path | Masked `404`, no fallback and no content request |
| Malformed/missing Host, socket provenance or ingress signature | Refuse before bootstrap; never use a forwarded/default host |

Locale parsing matches the shipped LocaleTag grammar, canonicalization and
35-character bound without trimming. First validate/canonicalize the first segment
and check enabled membership; only on a nonmember consider the exact scaffold
roots or the `courses` shorthand. Otherwise it is an unsupported/malformed prefix,
so `en_US` never becomes a default-locale resource path. Percent-encoded locale
prefixes are refused rather than decoded into another locale identity. No
`Accept-Language`, cookie or `?locale=` overrides the path. Explicit query locale on
Education calls always comes from that path.

When `courses` is enabled, `/courses` is its locale root and `/courses/courses` its
catalog; `/courses/{slug}` shorthand is intentionally unavailable on that host.
Its full localized `/{locale}/courses/{slug}` path remains available. Default and
nondefault `courses`/`studio`/`portal` fixtures must prove terminal redirects and
normal localized admission. P6/Phase 02b's future static/auth routes must preserve
these public paths rather than shadow them; those packets own their distinct UI
namespaces. No known-locale registry or locale-writer restriction is introduced.

Redirects use the successfully bootstrapped normalized visitor host with the
configured HTTPS scheme/ingress port and a local computed path. They never use
`request.url`'s possibly substituted origin, forwarding headers or a query redirect
target. Preserve query bytes as inert query data; bound/validate request targets,
reject authority/encoded separator or traversal ambiguities and test those cases.
Disable implicit pre-middleware slash/URL redirects where they bypass this policy.
Every tenant-bearing redirect/refusal is no-store; no cookies are emitted.

The matcher excludes Next assets/HMR, exact `/api/healthz` and favicon. It does not
exclude `studio`/`portal` prefixes: enabled locale paths need normal admission.
Health remains live without a tenant bootstrap; native ingress sanitation still
applies. The exact scaffold-root continuation above requires successful live-host
bootstrap and grants no API or authentication authority. Platform/unknown hosts
still receive `404`. Future BFF/auth routes require their own Phase 02b contract;
P6/G40 decides the product behavior of the remaining scaffold screens.

**P02d-6 entry extension — Accepted 2026-10-09.** In addition to the delivered
P5 matrix above, exact `/{enabledCanonicalLocale}/status/not-found` continues
through live admission with middleware HTTP `404`. Noncanonical enabled prefixes
canonicalize under the same rule; sibling/extra status segments remain masked
`404`. This status route carries no Education authorization. P6's decision package
owns the original content request's 307→404 navigation chain. Implementation is
not claimed by this acceptance note.

#### P02d-5 implementation plan

The first commit accepts this package and ADR-0053, records the bounded ADR-0036
replacement/navigation and updates the ongoing carriers below.
Then every implementation step is committed, reviewed by fresh agents twice, and
each confirmed correction is validated/committed before the next step.

1. **Native ingress and local topology.** Implement the mandatory launcher, private
   configuration/stamp protocol, TLS path/readiness, fixed loopback bindings and
   shared-source launch recipe. Update web scripts/config, environment examples,
   README/local-dev/seed workflows. Prove missing/forged/bypassed ingress refusal,
   valid production-build startup, no-env API startup, TLS and secret containment.
2. **API visitor admission and budgets.** Add strict trusted visitor metadata,
   canonical IP partitioning, peer ceiling and pre-lookup refusal. Keep network
   plus secret checks and forwarded-header ban. Add actual socket and HTTP/Postgres
   positive/negative/flood controls as `learnstack_app`, preserving no-store/DTOs.
3. **Configured caller and public entry.** Implement the single server-only
   transport, narrowly scoped fetch-lint exemption, Node middleware/bootstrap,
   locale matrix, origin-safe redirects and dynamic/no-store policy. Prove slow
   bodies, decoded-size bounds, cancellation, redirect secret containment,
   trace-ID agreement and every
   malformed/disabled/noncanonical entry case with a non-English default.
4. **Mechanical frontend fences and closeout.** Enforce server/cache/header
   boundaries and frontend skip/todo refusal with clean/planted controls. Complete
   production-build integration evidence, update all current carriers, run relevant
   suites and both final reviews; open the P5 PR for maintainer review.

P5 production-build fixtures use test-owned routes importing the real ingress,
middleware and caller against the real public API; no diagnostic route enters the
shipped app. Entry/bootstrap runtime evidence is distinct from P6's catalog/course/
lesson UI and P7's two-host browser demo. Where a phase-level criterion requires
those pages, its evidence owner stays P6/P7; P5 cannot mark it complete with a fake
transport. API-only proofs remain named separately.

#### Acceptance carriers and validation

Acceptance updates ongoing rules; delivery updates each owner with its enforcing
implementation:

- ADR index and current packet register/navigation; append-only ADR-0036 note for
  the bounded limiter/setter replacements and mode-independent startup policy.
  Name it a dated Amendment under ADR-0041/Standards 13, with source/history evidence
  and carrier disclosure; the new ADR owns changed rules, not an in-place correction.
- Standards 04/11 rate limits; 07/15 dynamic rendering/caller; 08 path entry; 10 and
  Phase 11 frontend observability ownership; Architecture 09/12/14/30 as applicable.
- Existing Standards 21 hop/peer/non-skippability entries, plus Registered proof
  names only when selected; claims become Implemented with actual enforcing tests.
- Route/local-dev/seed/test skills; root/web environment examples; web scripts,
  Next configuration, README Quickstart and Makefile launch help where introduced.
- Phase 02b G12/G14 re-verification handoff; Phase 06 redirect/cache inheritance;
  CI/CONTRIBUTING/test-skill Lighthouse ownership explicitly P02d-7/G44. Historical
  shipped records are not rewritten to disguise the transition.

Validation includes Release/format, architecture/unit/contract and relevant Docker
integration suites; all frontend workspace typecheck/lint/build/tests and SDK drift;
real production-build/socket/TLS cases; actual skip/todo/import/cache mutants; changed
Markdown relative links/anchors, width/residual checks and ADR-history preservation.
No test count, browser observation or CI success is claimed until it is run.

**Preparation verification — 2026-10-08.** Two fresh proposal-review rounds
checked security/runtime and corpus/decision completeness. The confirmed locale
name collision was corrected with membership-first entry; both second-round
reviewers approve the revised proposal. Two fresh focused reviewers also approve
the external-review corrections to secret cardinality, provenance carriers and
Amendment/standard ownership. The existing `CorpusConsistencyTests` pass 5/5,
with zero failures/skips. The six preparation files have 683 relative link
occurrences and 321 fragment targets, all resolving; added prose wrapping,
`git diff --check` and Accepted-ADR/historical-body preservation checks pass.
These are document-preparation results, not P5 implementation or browser evidence.

#### Approval boundary

The maintainer approved the exact ADR/package on 2026-10-08: the native custom
launcher, existing names with manual local TLS, dynamic public rendering and the
explicit 60/IP plus 600/peer API budgets. Automatic trust-store modification is not
authorized. Implementation now proceeds on development through the four steps and
two fresh review rounds per step; acceptance alone marks no delivery criterion done.

### P02d-5 delivery record

#### P02d-5 Step 1 — Native ingress and local topology

**Implementation delivered — 2026-10-08; review rounds pending.** The mandatory
native Node launcher binds HTTPS to `127.0.0.1:3000`, validates the private source
and delegates requests/upgrades to Next 15.5.18. It captures the real socket peer,
canonicalizes IP identities, validates exactly one Host and bounded request target,
strips incoming private/forwarding/framework carriers, then mints the HMAC envelope.
Verification binds host, peer, method and raw target and refuses forged/repeated or
noncanonical envelopes. No ingress code resolves tenant/organization authority.

`make public-env` generates an ignored 32-byte random ASCII-encoded secret without
hosts/trust changes. `make public-api` supplies only runtime database credentials,
fixed loopback network and matching secret; `make public-web` and `pnpm dev/start`
use the native launcher. Root/web environment examples and README/local-dev/seed
workflows describe explicit mkcert trust, leaf creation and manual hosts aliases.
The API's ordinary no-hop startup remains unchanged.

The old middleware's tenant-header placeholder and invented `en` fallback are
removed. Its interim Node admission returns no-store `503` after valid provenance
and masked `404` otherwise. Health/assets are excluded from tenant entry, but all
requests pass native sanitation. Step 3 replaces the interim refusal with real
bootstrap and locale entry. Next implicit URL/slash redirects and Server Component
HMR caching are disabled. P6 pages and P7's demo remain separate delivery scopes.

**Validation.** The frontend suites pass 128 cases (60 SDK, 68 web), including 55
ingress/config/socket cases; workspace lint, typecheck and production build pass.
The production companion verifies test-root TLS readiness, private-header stripping,
no carrier/secret in responses or retained fixture diagnostics, and stock-Next
forged-carrier refusal. Existing API hop-configuration tests pass 5/5 with zero
failures/skips, preserving empty-hop mode-independent startup. TLS controls use
isolated trusted roots and a test-only Secure-cookie response header; no browser
cookie acceptance or local CA installation is claimed.

**Step 1 round 1 — confirmed corrections (2026-10-08).** Independent security
and quality reviewers identify two boundary failures in the committed foundation.
The production companion reproduces a valid raw query returning `404` because Next
serializes its search parameters and removes `_rsc`; it also reproduces an interior
repeated slash causing a pre-middleware redirect. The fix preserves the signed raw
target, compares only its explicit pinned-Next projection at middleware admission,
and rejects repeated pathname slashes before delegation. Ten focused cases and
production controls cover query rewriting, terminal `?`, RSC query stripping,
method/path/query substitutions and no-store refusal without an implicit redirect.
OpenSSL prerequisites and the paired/API-only restart guidance are clarified.
The corrected frontend suite passes 138 cases with zero failures/skips;
production TLS/query/bypass controls, typecheck, build, native/config lint and
changed-link/history checks pass. Second-round review remains pending.

**Step 1 round 2 — closeout (2026-10-08).** Fresh GPT-6-astra xhigh and
GPT-6.1-sol xhigh reviewers inspect the cumulative implementation. Confirmed
corrections remove all pinned-Next internal routing/resumption/revalidation
headers at native admission and prevent Next's automatic upgrade listener from
receiving rejected inputs. The supported `httpServer` option registers that
listener on a non-listening sink; native TLS admission alone delegates upgrades.
Real production controls reject duplicate Host before Next, admit one sanitized
upgrade, and fail when the sink option is removed. An isolated development fixture
receives `101` for admitted HMR and no delegation for its rejected control.

The companion also recognizes children already terminated by signal, observes
exit before signaling and bounds forced cleanup. Real already-exited, running and
SIGTERM-resistant controls pass. The corrected 138 frontend cases, production
TLS/query/upgrade/bypass controls, production build, typecheck and lint pass with
zero failed/skipped cases. Both reviewers approve the corrected scope; Step 1 is
complete and Step 2 follows. No workstation-browser proof or P6 page is claimed.

#### P02d-5 Step 2 — API visitor admission and budgets

**Implemented — 2026-10-08; independent reviews pending.** The API captures one
request-local visitor identity through the existing network-and-secret hop
predicate. Its bounded IP parser rejects abbreviated/integer IPv4, ports, lists,
zones and whitespace; IPv4-mapped IPv6 shares the direct IPv4 namespace. Unknown
peers retain one fixed fallback partition. No header selects a tenant.

Chained fixed-window limiters apply 600 calls/minute per physical peer first,
then 60 calls/minute per canonical visitor IP, without queues. This ordering
prevents exhausted peers from creating additional visitor partitions. Missing,
malformed and repeated trusted metadata consumes fallback quota, then receives
masked `404` before host classification; untrusted forwarding metadata is ignored.
Existing `429` Problem Details and `Retry-After` remain authoritative.

**Validation.** The 28 new identity cases and 18 existing hop/configuration cases
pass. Fifteen new real-Kestrel/PostgreSQL cases and both existing anonymous HTTP
controls pass, with zero failures/skips. Positive public reads reach the production
reader as `learnstack_app`, with READ ONLY and no superuser/BYPASSRLS privileges.
Controls cover shared direct/hop quotas, independent visitors, canonical IPv6,
rotation, repeated raw headers, novel-host bounds and both budget ceilings.
Invalid metadata and over-budget requests do not call the host resolver. The
initial API Release build passes with zero warnings/errors. Steps 3–4 remain open.

**Step 2 round 1 — confirmed corrections (2026-10-08).** Independent
GPT-6-astra xhigh and GPT-6.1-sol xhigh reviews identify premature physical-peer
exhaustion and a quota-test oracle gap. ASP.NET retries a refused synchronous
acquisition asynchronously; re-running the peer-first fixed-window chain charges
its earlier peer permit twice. A real-Kestrel regression fails on request 331
after 60 accepted and 270 refused calls. A request-local no-queue wrapper preserves
the first refusal and its metadata without repeating the acquisition; each
framework attempt owns a separate lease. Peer-first ordering remains unchanged.

The corrected mixed-traffic control admits the remaining allowance through actual
request 600 and refuses 601 before lookup. Independent assertions fix the accepted
60/IP, 600/peer and one-minute values. All 18 focused HTTP controls and 47
identity/hop/configuration cases pass with zero failures/skips. The pre-correction
full regression passed 2,890 cases but did not cover this newly reproduced boundary;
it is not evidence for the corrected head. Second-round review follows.

**Step 2 round 2 — lifecycle correction (2026-10-08).** Fresh GPT-6-astra
xhigh and GPT-6.1-sol xhigh reviewers confirm the corrected quota behavior and
identify incomplete limiter shutdown. The framework chain does not own its child
budgets; the wrapper's asynchronous path and the options registration also lacked
ownership. DI now owns the wrapper, which disposes both budgets exactly once on
synchronous and asynchronous shutdown. Four focused lifecycle cases pass.
Both reviewers independently verify the correction, including actual provider
shutdown, completed child timers and refusal after disposal, and approve.

The independent security probes also confirm that rotating visitor metadata after
peer exhaustion creates no further visitor partitions, while an already-cancelled
acquisition creates none. These are test-owned probes, not production telemetry.
Both review rounds are complete. The final corrected Release build has zero
warnings/errors; **2,896 backend cases** pass: 1,679 unit, 198 architecture,
four contract and 1,015 integration, with zero failures/skips. The nonempty-run
check covers all four assemblies. The two changed Markdown files pass the local
link/fragment audit (552 links, 295 fragments), added-prose width and diff checks.
Frozen P1–P4 history and Accepted ADR bodies remain unchanged. Step 2 is complete;
Steps 3–4 follow automatically under the approved plan.

#### P02d-5 Step 3 — Configured caller and public entry

**Complete after both review rounds — 2026-10-08.** One server-only
adapter supplies the four injected SDK GETs with private origin and a closed
authenticated hop. It re-verifies the envelope and derives locale only from the
canonical signed route. Redirects are refused; caller cancellation is distinct
from the ten-second total header/body deadline. Decoded response bytes are counted
while consuming, up to 8 MiB; oversize is a transport failure without truncation.
Cleanup does not wait indefinitely for stream cancellation or late fetches.

Node middleware verifies before request-local live bootstrap, then applies the
accepted membership-first entry matrix. Non-English default, disabled/malformed
prefixes and default/nondefault `courses`/`studio`/`portal` locale collisions are
covered. Redirect authority comes from the bootstrapped captured host and fixed
HTTPS port, preserving signed raw query bytes. Continuation rebuilds request
headers and emits no cookie or ordinary provenance response header. The public
layout is dynamic with zero revalidation and force-no-store fetches.

**Validation.** All **280 frontend cases** pass: 220 web and 60 SDK, zero
failures/skips. The 39 adapter controls include real loopback HTTP gzip expansion,
exact decoded size, redirect credential containment, caller cancellation and a
ten-second deadline spanning delayed headers and body. Eighty-two entry and 21
middleware cases cover admission, raw queries, error mapping and request isolation.
Workspace lint/typecheck and the production build pass. The production TLS
companion exercises the real middleware/caller against a test-owned bootstrap
server, proving live redirects, raw-query retention and stock-Next refusal before
bootstrap. It does not replace Step 4's real-API integration or P6 pages.
The real-Kestrel/PostgreSQL trace control also passes: the API's Problem Details
uses its own span with the adapter's propagated trace identifier. The HTTP test
disables the test HttpClient's ambient trace injection so it observes the intended
wire header; the production caller itself remains unchanged.

**Step 3 round 1 — harness failure cleanup (2026-10-08).** Independent
GPT-6-astra xhigh approves the security/runtime boundary. GPT-6.1-sol xhigh
reproduces an asynchronous fixture-listener assertion bypassing the outer cleanup,
leaving a Next child and temporary tree alive. The listener now records refusal
without printing credential values; the awaited outer flow raises the failure
where cleanup owns all resources. A source-planted wrong-path control runs the
real production harness, exits red and verifies its child and tree are gone.
The unmodified production TLS companion also passes. No API/entry policy changes.


**Step 3 round 2 and closeout (2026-10-08).** Fresh GPT-6-astra xhigh
approves security and runtime behavior, including both production companions and
no-store scaffold continuation. Fresh GPT-6.1-sol xhigh reproduces a test-oracle
gap: a 36-letter locale already fails primary-subtag grammar, so removing only
the 35-character guard leaves the entry suite green. Grammar-valid 35/36-character
controls now isolate the bound; the reviewer verifies all 84 cases pass and the
removed-guard mutant fails exactly the 36-character case. Production policy is
unchanged. Both independent rounds and fix verification pass; Step 4 follows.


#### P02d-5 Step 4 — Frontend fences and production integration

**Implemented — 2026-10-09; independent reviews pending.** Two reserved frontend
rules now name actual Vitest assertions with a nonempty production TypeScript
census. AST analysis follows runtime imports/reexports and constant dynamic imports,
with alias/computed fetch and header, server-only, raw-authority, layout-policy and
cache controls. Request-local React cache remains allowed. Arbitrary eval, runtime
reassignment and external implementation bodies are outside the structural claim;
actual production/socket proofs remain separate.

The root frontend runner discovers test packages from the workspace declaration,
including ones pnpm would otherwise skip. It refuses missing scripts, missing or
invalid/empty reports, failures, skipped/todo cases and omitted discovered files.
Actual isolated Vitest clean/skip/todo/missing-script/report/omission controls prove
refusal. The existing frontend CI context uses this runner and executes the native
production TLS/failed-listener cleanup companions after build.

`PublicServerRenderingTests` compiles a disposable app with test-owned routes that
import the real ingress/middleware/caller and SDK against real Kestrel/PostgreSQL.
Both seed hosts render the same locale/slug in cold/interleaved HTML and RSC without
cross-host values. The fixture proves exact lesson projection, non-English default
redirect, stock-Next forgery refusal before any API/database bootstrap, nonempty
client asset and native/API log containment, and next-request publication freshness
in the same native process. Its real API reads use `learnstack_app`, physical READ
ONLY frames and the existing isolation observer. Only test setup mutates owner rows,
with restoration in `finally`; it is not the role used to prove isolation.

A real Client Component import of the configured caller fails with the server-only
diagnostic; deleting that test-owned route yields a clean production build. Private
runtime configuration is supplied only after compilation. Bootstrap/page reads
share one valid trace identifier for supplied, missing and malformed incoming
contexts; the middleware creates a request-local context before either caller.
No trace or private carrier is emitted to the browser.

Pinned Next streams RSC not-found with HTTP 200 and its exact
`NEXT_HTTP_ERROR_FALLBACK;404` digest, while the document response is 404. The
fixture requires the real API's parsed 404, the digest and absence of previous
content; other API/transport failures fail the proof. It stays below the existing
60-call visitor budget and owns only isolated ports/certificates/build trees.
Backend integration CI receives Node/pnpm and frozen frontend dependencies; no
required check name or branch-protection setting changes.

Test-owned routes are P5 transport/runtime evidence. Public catalog/course/lesson
product UI is P6; browser demo and Lighthouse remain P7/G44/G45. This step adds no
production diagnostic route, page UX, auth, CMS or database migration.


**Step 4 pre-review verification (2026-10-09).** Release build has zero
warnings/errors; format verification passes. All **2,898 backend cases** pass:
1,679 unit, 198 architecture, four contract and 1,017 integration, with zero
failures/skips. Every assembly passes the nonempty-run checker and direct TRX
counter comparison. The complete guarded frontend run passes **381 cases**:
321 web and 60 SDK, zero failures/skips/todos. Workspace lint/typecheck and the
production build pass. Focused production integration also passes independently;
normal native TLS and planted listener-cleanup controls pass. Both `pnpm test`
and `make test-frontend` route through the guarded runner. SDK snapshot drift and
the final per-step review evidence are recorded below when complete.

**Step 4 round 1 — proof repairs (2026-10-09).** Independent GPT-6-astra
xhigh runtime/security review approves the production boundary. It reruns the real
API fixture and verifies that compiled shared-result and credential-log mutants
fail their intended isolation/containment assertions; owned listeners and build
trees are cleaned on failure. GPT-6.1-sol xhigh quality/standards review identifies
three proof gaps: the missing-report control fails before reaching report parsing,
namespace aliases can hide Next caching, and destructured headers can hide raw
authority reads. Each gap is independently reproduced before repair.

The report control now runs a successful script without a report and requires
`ENOENT`; empty and unreadable reports have separate reason assertions. Source
analysis follows namespace variable/import/reexport aliases and object-binding
headers, including a transitive barrel control. All 77 source controls and 26
runner controls pass, and the complete guarded frontend run passes **388 cases**:
328 web and 60 SDK, with zero failures/skips/todos. Web typecheck/lint pass. Three
workflow skills now describe Accepted G25/G36/G37/G44 instead of their prior open
questions; no accepted decision or production behavior changes in these repairs.
The quality reviewer independently confirms all three fixes and passes 127 focused
source/runner/middleware cases. Both first-round reviewers approve the corrected
tree; second-round review remains pending.

**Step 4 round 2 — source and cleanup repairs (2026-10-09).** Fresh
GPT-6-astra xhigh security/runtime and GPT-6.1-sol xhigh quality/standards reviewers
verify the current implementation independently. The confirmed source gaps are
awaited header collections, named/default/star/namespace export aliases, private
environment bindings and exported global fetch. Reused constant expressions need
path-local cycle detection, and explicit runtime exports must shadow star exports.
All original counterexamples fail before their fixes; dirty and clean controls now
separate these cases without interpreting function bodies or runtime reassignment.

The final source suite passes **104 cases**. The quality reviewer independently
executes 28 additional in-memory controls over the production census: all 20 dirty
cases are detected and all eight clean cases are accepted. The complete guarded
frontend run passes **420 cases**: 360 web and 60 SDK, with zero failures/skips/todos.
Workspace lint/typecheck pass. Focused-test instructions use the package command;
the guarded root runner deliberately accepts no filters. Stale App Shape, SSR risk
and text-card projection status clauses are synchronized without changing decisions.

Owned test process groups can outlive their leader, so both native fixtures share
bounded group cleanup. Only an absent group closes the proof; transient Darwin
`EPERM` counts as present, and persistent refusal fails at the deadline. Five clean
and planted cleanup cases pass. The security reviewer independently passes four
consecutive native runs, the failed-listener companion and the real-API production
rendering fact in Release with `CI=true`, with zero failures/skips. The helper's ESM
declaration uses the existing shared typed ESLint parser. No production trust rule
changes in this round. Both second-round reviewers approve the corrected tree;
Step 4's two independent rounds and fix verification are complete.

#### P02d-5 packet closeout (2026-10-09)

**Implementation-complete, unmerged.** All four planned steps and two fresh
independent review rounds per step are complete. Confirmed findings are repaired
and committed; no verified P5 issue or maintainer decision remains open. The native
ingress, authenticated visitor budgets, configured server caller, live locale entry,
dynamic/no-store policy and their mechanical/runtime proof surfaces are delivered.

Local verification:

- Release build: zero warnings/errors; format verification passes.
- All **2,898 backend cases** pass: 1,679 unit, 198 architecture, four contract and
  1,017 integration, with zero failures/skips; direct TRX counters and the nonempty
  assembly checks agree. After the final cleanup repair, the independent security
  reviewer reruns the real-API production rendering fact successfully. The final
  architecture rerun also passes 198/198.
- The final guarded frontend run passes **420 cases**: 360 web and 60 SDK across two
  tested packages, with zero failures/skips/todos. Workspace typecheck/lint,
  production build and regenerated SDK snapshot drift checks pass.
- Native socket/TLS, failed-listener cleanup, actual skipped/todo/missing-report
  controls and production Client Component import refusal pass. HTML/RSC separation,
  eligibility freshness, trace continuity and secret containment use the real API;
  they are transport evidence, not a shipped product-page or browser-demo claim.
- Manual changed-document file/fragment validation, added-prose wrapping,
  `git diff --check`, strict commit messages and Accepted-ADR/P02d-1–4 history
  preservation pass. CI's existing link check covers files, not fragment targets.

These local runs use Node 22.23.1 and .NET SDK 10.0.302; they do not claim the pinned
CI Node 20.11.0/.NET SDK 10.0.112 run. The P5 PR's actual checks establish that
separate evidence before maintainer handoff. No live protection setting or required
check name changes. This packet adds no database migration or public OpenAPI/SDK
contract change.

**Next: P02d-6 — Public renderer.** Re-verify G5/G12/G16/G20/G38/G39–G43 against
the delivered transport, then accept its page/state, text-card, theme and
accessibility contracts before implementation. P6 consumes the configured SDK and
preserves uncached rendering and live eligibility. P7 still owns `make demo`, the
two-host browser harness, Lighthouse and phase exit. Phase 02d remains in progress;
P5 does not complete the full walking skeleton or deliver authentication, CMS,
enrollment grants, commerce or production ingress/distributed quotas.

**CI lint preflight correction (2026-10-09).** The first P5 PR run exposes a
clean-checkout dependency: native launcher lint resolves generated
`.server/ingress.js` before the later build step emits it. Existing local output
hid this ordering gap. Removing that owned output reproduces the precise import
refusal; the web lint command now compiles ingress first, then retains all lint
rules. The staged-web hook follows the same preflight. Full workspace lint passes
from absent generated output; no runtime policy or test count changes.
Two fresh GPT-6.1-sol high reviewers independently approve this narrow correction;
shell syntax, failure propagation, ignored output and staged-file isolation pass.

**PR handoff — 2026-10-09.**
[PR #26](https://github.com/HodeTech/LearnStack/pull/26) proposes development into
main for maintainer review. Main synchronization uses a normal merge on development
and preserves the validated tree; no branch switch or history rewrite occurs.
The PR's actual required-check rollup owns pinned CI evidence separately from the
local runs above. P02d-5 remains unmerged; P6/P7 delivery is not claimed.

**Review evidence clarification — 2026-10-09.** The Step 4 and packet-closeout
counts above describe `e580444f0dbaf11151bf008a06a75688f2bd6e4a`: 104 source cases
and 420 frontend cases (360 web + 60 SDK), confirmed by
[CI run 37852875524](https://github.com/HodeTech/LearnStack/actions/runs/37852875524).
The subsequent `5db8daedfd93f816ef23e545b8741ee54395e2f8` remediation adds four
compiler-erasure controls: inline type-only imports/reexports retain runtime edges,
while declaration-level type-only forms are erased. Both inline controls fail
before the guard fix. Two fresh independent reviewers approve the correction.
That tree passes 108 source cases and **424 frontend cases** (364 web + 60 SDK),
with zero failures/skips/todos. All six required checks pass in
[CI run 37855253417](https://github.com/HodeTech/LearnStack/actions/runs/37855253417);
TRX counters confirm 2,898 backend passes, including 846 Docker cases and the
real-API production renderer. These named runs qualify the earlier dated evidence;
they do not rewrite it as a claim about every later commit.

**Review sanitation correction — 2026-10-09.** Client-supplied `next-url` is
removed from both native header collections and the middleware's downstream
allowlist under ADR-0053's existing client-carrier sanitation boundary. The
middleware control first reproduces its prior forwarding; the real TLS control
checks parsed/raw header removal while retaining successful native admission.
Next uses this carrier for interception routes, which P5 does not implement;
normal RSC/navigation protocol headers remain available.

The other suggestions do not identify current failures. Port 3000 is the Accepted
local topology; Phase 11 owns its replacement. ADR-0052 limits the API to GET/HEAD,
and the configured caller exposes only those reads; a blanket web-method rule
would also govern scaffold and future Phase 02b BFF/auth paths. The caller consumes
only API origin and secret, never its computed TLS paths; the native launcher alone
reads certificate/key files, resolved against the repository root.

### P02d-5 external-review remediation (2026-10-09)

**Current status — Complete and merged (2026-10-09).** The
maintainer approved ADR-0054 and all five correction steps on development.
Runtime, proof and corpus corrections complete both fresh independent review
rounds and verified fixes. Step 5 records full local validation. The
[merge closeout](#p02d-5-merge-and-closeout-2026-10-09) records PR #26's final
head, six required checks and merge verification. Original delivery, proposal and
review notes below remain historical; P6/P7 scope remains separate.

Review reports are evidence to verify. The triage below compares their claims with
`07016405`, installed Next 15.5.18/.NET 10 behavior and owned isolated reproductions.

#### Decision package

[ADR-0054](../decisions/0054-bounded-public-renderer-admission.md) is Accepted —
2026-10-09. It replaces only these ADR-0053 contracts:

1. Refuse an exhausted known visitor before peer debit; keep unknown visitor
   creation peer-gated. Own the actual visitor limiters and coordinate acquisition,
   idle retirement and request-result replay. Preserve 60/IP, 600/peer, fixed
   windows, no queue, the direct/hop shared namespace and pre-lookup enforcement.
   Retain a 600-per-window peer-debit/allocation cap, not a total refusal-traffic
   cap. One process-local owner lock serializes acquisition and bounded cleanup
   batches; Phase 11 owns contention measurement and upstream protection.
2. Admit GET/HEAD on the current native public/scaffold surface; give other methods
   masked no-store `404` before Next. Close all production upgrades; development
   retains only required validated HMR. Future BFF/auth routes require explicit
   admission in Phase 02b. No public API method or authentication rule changes.
   The native rule covers every HTTP path, including middleware-exempt health and
   assets; future Phase 06 Server Actions/write routes need their own admission.
3. Preserve inert query values, duplicates and ordering in redirects, allowing the
   supported serializer's equivalent percent encoding. Stop promising identical
   raw query bytes. Keep raw signed route identity and pinned framework projection.

The current peer-first order is explicit in ADR-0053 Amendment 3, so this is a
bounded supersession, not a false-when-written correction. Dated navigation is
appended to ADR-0053/0036; this record explicitly replaces G34's accounting order
and G36's query-byte promise only. Their original accepted answers remain
historical; membership-first precedence and P6/P7 open parts remain unchanged. No
new global quota, distributed limiter, database migration, transport-suffix route
alias or schema/API contract is introduced.

Keep existing valid traceparent continuation. Sampling participation, IPv6-prefix
aggregation and production tracing policy remain Phase 11; they are not tenant
authority. Keep the existing local HMAC key distribution/wire format. Fix diagnostic
containment now; freshness/nonces or a process-specific key require separate
production trust/distribution evidence, owned by Phase 11.

#### Verified current findings and actions

| Finding group | Verified result | Action in this PR |
|---|---|---|
| Shared peer budget, B1 | One visitor's 600 requests admit 60 and refuse 540 while exhausting the peer; a fresh visitor is then refused. Bare reversal allocates unknown visitors after peer exhaustion | Implement Accepted ADR-0054; prove refusal fairness, bounded work per refusal and peer-gated allocation independently. Retain the admitted-call/allocation cap; total refusal traffic is outside it |
| Framework endpoint retry | Current global-only wiring charges once. Adding an endpoint policy reproduces a second global debit after success | Save successful and refused request outcomes in the new owner; test actual endpoint-policy retry now |
| Next URL projection, M1 | Valid query values ending `.rsc` are changed by the adapter's full-URL normalization and rejected by the current verifier | Mirror pinned adapter processing, while retaining the signed raw target for route/locale identity. Test query cases and unrelated routes; add no suffix aliases |
| Proxy header sanitation, M2 | Native parsed/raw headers retain `x-real-ip`, bare `x-forwarded` and vendor authority spellings. Current downstream/API allowlists prevent authority escalation | Strip the explicit forwarding/authority carrier set from both collections; prove mixed-case controls and unchanged signed socket identity |
| Framework debug output | Installed Next logs full request headers under `DEBUG=next:*`, including the signed envelope | Refuse/suppress unsafe framework diagnostic configuration before request handling; prove containment with an enabled-debug canary |
| Methods/upgrades | TRACE/TRACK can fail before user middleware. Unmatched production upgrades can remain open because Next expects another WS consumer | Apply the Accepted native GET/HEAD and production upgrade rule; retain proven development HMR, no-store and bodyless HEAD |
| Redirect bytes | Literal apostrophe query values become `%27`; meaning is retained | Apply the Accepted equivalent-encoding contract; test actual runtime Location, not only the helper |
| Authority/source guard, M4 | `new Headers(request.headers)` and `Object.fromEntries(request.headers)` reads escape tracking, including spread of the converted object | Add bounded constructor/conversion tracking, named planted/clean controls and a census of supported tsconfig aliases; fail unsupported local mappings |
| Compiler evidence, M7 | Existing tests measure tsc verbatim emission. Installed Next SWC erases inline type-only imports/reexports | Keep the conservative graph policy; label the tsc proof accurately and add pinned SWC evidence with value/mixed-edge positive controls |
| Ingress fixture lifecycle, M6 | Setup occurs before try/finally; interruption leaves detached children/temp material | Put setup under one idempotent resource owner; cover setup failure, SIGINT/SIGTERM and parent/control-pipe closure |
| Fixture readiness | Ingress readiness can accept a stale 3011 listener; the real-API fixture already preflights its ports | Preflight both owned fixed ports, require owned-child bind/readiness and child liveness. Fail clearly on collision; do not attach to or kill an existing server |
| Fixture TLS/log proof | Ingress inherits TLS/debug overrides and can forget a leak after 64 KiB of later output | Allowlist child environment, explicitly verify TLS, and scan before truncation with chunk overlap and sticky verdicts |
| Renderer cleanup | Broken stdout can raise unhandled EPIPE before finally; ordinary stdin/signal cancellation is already handled | Route output-pipe errors through idempotent cleanup; prove an owned broken-pipe case |
| Upgrade/build containment evidence | Client-side timeout can masquerade as server closure; IPC is not synchronized. Secretless build only proves its declared secretless path | Make timeout fail, await owned IPC observations, add actual closure controls and a nonempty configured-build canary for private asset/diagnostic exclusion |
| Fixed-window tests | Exact accounting over 600 sequential HTTP requests assumes no minute rollover | Add deterministic owner/factory/sweep seams and barrier controls, plus retained real-framework/HTTP proof. Keep production window/limits unchanged |
| Hook/CI/DX | Hook omits `.mts/.cts`; SDK lint claim exceeds invocation; root config/generated-file selection differs; `.server` is formatted | Align explicit lint subjects/parser handling, cover SDK lint, exclude generated output, and add representative failure controls |
| Runtime support | Node 20 is EOL; CI still pins 20.11.0, while local P5 evidence uses 22.23.1 | Pin supported Node 22.23.1 across current tool/configuration carriers, then rerun actual CI. Historical Node 20 evidence stays historical |
| Narrow evidence/consistency | HttpClient merges repeated secrets; `v1.` substring is overly broad; favicon matcher has an unescaped dot; some guard assertions are only nonempty | Add raw-wire repeated-secret and mapped-peer controls, structural token matching, exact favicon matching and named guard assertions |
| Corpus and workflows, M3 | Current owner/status prose and route/seed launch guidance lag delivered G31–38/G44 | Align current documents and skills with accepted ownership; preserve P6/P7 open parts and immutable records |
| ADR disclosure guard | First nonempty Status line is a blockquote in ADR-0052, so CI's current awk misses its Accepted lifecycle | Parse the lifecycle line within Status, ignoring banners; plant Accepted-with-banner controls. Preserve original status/history |

The Node support finding is verified against the
[official release schedule](https://github.com/nodejs/Release#release-schedule):
20.x reached EOL on 2026-04-30; 22.x remains Maintenance LTS through 2027-04-30.
The [22.23.1 archive](https://nodejs.org/en/download/archive/v22.23.1) identifies
the selected local-tested patch. No particular HTTP-parser CVE/exploit is claimed.

Ongoing corpus fixes in this preparation cover Architecture 04/05/14/25,
Standards 03/06/07/11/12/15/index, glossary placement and frontend entry READMEs.
G32–37 answer links now point to their actual detail owners. Security Standards
carry the accepted private carrier, five API-hop headers, 10-second/8-MiB bounds,
secret/client/log exclusions and the narrow local Node configuration exception.
Remaining route-skill/seed/hook/CI edits ship with their corresponding code/tests.

#### Rejected, qualified and later-owned claims

| Claim | Disposition and reason |
|---|---|
| IPv4-mapped peers fail `IPNetwork.Contains` | Refuted on current .NET 10; the loopback IPv4 network contains mapped loopback. Add a pinning test, no parser workaround |
| Invalid visitor metadata violates an “only 600” rule | Refuted: existing policy specifies two budgets and peer-IP fallback. Clarify both debits; the proposal changes only exhausted-visitor accounting |
| Direct `.rsc`/segment paths must become public aliases | Not established for the supported non-minimal Node mode; segment cache is not enabled. Fix the valid query bug without broadening route identity |
| Plain `{...request.headers}.host` is a Next authority escape | Refuted for WHATWG Headers; it does not create a string host property. Converted plain-object spread remains a real guard gap |
| TRACE's ordinary framework 500 lacks no-store | Refuted by installed Next error rendering. Native refusal still avoids unsupported-method framework dispatch |
| Fixed 3000 is an accidental product port | Refuted: Accepted local topology. Test collision must fail rather than reuse a foreign listener. Phase 11 owns production topology |
| `make demo` is falsely claimed shipped | Refuted: README assigns it to P7. Do not add P7's target as a P5 repair |
| OpenSSL is undocumented; every PEM needs a global ignore | Refuted/scoped: prerequisites name OpenSSL and `.data/` protects accepted private certificate paths. Broad PEM ignores could hide public fixtures |
| All parent-death cleanup/TLS/log scanning is absent | Overstated: the real-API harness already watches stdin/signals, allowlists environment and scans before truncation. Fix its verified EPIPE path and the separate ingress deficiencies |
| Slowloris waits forever | Not reproduced; default Node timeout closes the reported case. Keep production timeout/topology tuning in Phase 11; enforce bounded fixture waits now |
| PID reuse warrants skipping every exited group leader | Rejected fix: surviving descendants require group cleanup. Reduce stale handles after confirmed group absence and retain the surviving-descendant control; no unrelated-group kill was reproduced |
| Envelope has no replay expiry | True protocol fact, not a supported-listener replay bypass. Submitted envelopes are replaced; Step 2 addresses the verified debug leakage. Phase 11 owns production replay/key lifecycle |
| Restart every visitor trace, change sampling, aggregate IPv6 prefixes | New observability/rate policies, not demonstrated P5 defects. Preserve accepted continuation; Phase 11 owns these choices |
| New static/auth/robots/sitemap paths must already bypass admission | P6/G40 and Phase 02b own their explicit namespaces. Existing exact exclusions remain; correct only the favicon regex defect now |
| Bootstrap reuse / Link prefetch already required in P5 | No current product pages exist. P6 owns request-local consumption/prefetch choices before page code, including call-count evidence; no caching or shared bootstrap is authorized |
| SDK response-parser/body/Retry-After notes | SDK is inherited from P4 and outside this PR's changed transport. P5's configured caller already bounds decoded bytes/deadline; re-verify SDK contracts with P6 consumers rather than change them speculatively |
| PlatformAdmin is required by `make public-api` | Refuted: optional credential is copied only when present; composition registers its lazy guarded source and boots without it. Public reads use the app role. Prove launch without it; do not grant public paths bypass access |
| Merge versus squash blocks remediation | Neither changes the code contract. Maintainer chooses merge mode; dated source/CI references remain scoped evidence, not a demand for a merge strategy |
| CLAUDE status length / model diversity proves a runtime bug | No. Keep task-specific current state honest and evidence scoped; broad guidance restructuring and unsupported transcript claims are not corrective production changes |

Historical ADR-0053 delivery amendments remain intact. Acceptance appends the
bounded supersession note and clarifies ADR-0036's existing navigation wording
through dated navigation, preserving old text. CI currently checks relative
file targets, not anchors; keep manual anchor evidence labeled honestly. No new
required check or protection weakening is proposed.

#### Implementation and review sequence

Maintainer approval on 2026-10-09 authorizes these five correction steps on
development. The current status above and per-step records below own progress.
For each:
implement and validate, commit, run two fresh independent review rounds, validate
and commit confirmed fixes, then proceed automatically. Preserve main and unrelated
local work; do not rewrite the branch or merge PR #26.

1. **Coordinated anonymous admission.** Implement the owned visitor limiter and
   cleanup/retry semantics; deterministic accounting, parallel last-permit/creation,
   safe retirement, endpoint-retry, selected Retry-After, cancellation/disposal and
   real HTTP controls. Replace the named peer-refusal accounting test identified in
   ADR-0054, retain admitted-traffic controls, and prove bounded per-refusal work,
   non-overlapping/eventually complete sweeps and shutdown/acquisition safety.
2. **Native ingress and URL boundary.** Fix pinned projection, header sanitation,
   debug containment, method/upgrade admission, query wording/Location proof,
   powered-by and exact matcher. Retain real HTML/RSC, valid TLS and development HMR
   controls; verify protocol headers through the actual middleware path.
3. **Fixture reliability and containment.** Resource-own setup/termination/EPIPE,
   synchronize owned readiness/upgrades, enforce TLS/env allowlists and sticky
   scanning, add configured-build canary and structural-token controls. Mutants for
   omitted cleanup, early leaks, disabled TLS checks, stale listeners and unclosed
   upgrades must fail with an independent clean control.
4. **Source/runner/tooling proof.** Close constructor/conversion and alias gaps,
   make compiler claims exact, align lint/hook/formatter subjects and Node pins,
   fix lifecycle parsing and strengthen narrow evidence controls. Keep every guard
   falsifiable; an empty production client-component census is not bundle evidence.
5. **Corpus and PR closeout.** Update current skills/seed/setup/standards/catalogue
   and ADR navigation with actual enforcement. Run applicable full Release suites,
   lint/typecheck/build, production socket/TLS/rendering fixtures, SDK drift,
   Markdown links/anchors/width, immutable-history and strict commit checks. Push
   development, verify all six required checks on the exact new head and update
   PR #26's description/evidence for maintainer review.

No remediation acceptance or passing-test count is inferred from the earlier
`07016405` CI run. Preparation checks and independent document reviews are recorded
after they actually run; implementation evidence belongs to the five steps above.

#### Remediation Step 1 — Coordinated anonymous admission

**Complete — 2026-10-09; both independent review rounds passed.** The API owns actual
visitor fixed-window limiters, serialized acquisition and bounded periodic
retirement. Exhausted known visitors return their unchanged refusal metadata
before peer debit; peer refusal prevents unknown visitor allocation. Successful
and refused HTTP outcomes are copied into independent request-local leases,
including ASP.NET's retry after an endpoint policy refuses. Teardown closes
admission, joins active sweeping and disposes children outside the owner lock.

Focused controls cover both final-permit races, single creation, both-budget
Retry-After selection, cancellation, replay/lease independence, more than two
sweep batches, non-overlap, full-quota idle retirement, actual .NET automatic
replenishment and concurrent teardown. Real HTTP accounting freezes only test
budget replenishment, retaining actual fixed-window implementations, so minute
rollover cannot invalidate exact accounting. The application-role/read-only
controls retain their original runtime path. No incoming-traffic or production
throughput bound is claimed.

**Validation — 2026-10-09.** Strict Release solution build passes with zero
warnings/errors. All 2,922 .NET tests pass without failures/skips: 1,702 unit,
198 architecture, four contract, 172 Docker-free integration and 846 Docker
integration. The focused accounting/lifecycle set passes 27 cases; the real
endpoint-retry proof and 17 trusted-visitor HTTP cases pass. TRX execution/zero-skip
checks and changed-source format verification pass; the format workspace loader
reports a warning without a formatting violation. Corpus consistency is rechecked
at 5/5 after the status edits.

**Review round 1.** Fresh GPT-6-astra xhigh runtime/security review approves the
implementation; fresh GPT-6.1-sol high proof/corpus review identifies one confirmed
test gap: eventual traversal did not constrain lock-batch size. The strengthened
300-entry control blocks the first outside-lock disposal, observes exactly 128
idle inspections, admits another request before release and verifies complete
exact-once traversal. An actual `int.MaxValue` batch mutation fails the intended
128-versus-300 assertion; restored-source focused tests pass 27/27 with zero
skips. The production limiter is unchanged. The correction is committed as
`a107df5`, following implementation commit `208d64f`.

**Review round 2.** Fresh GPT-6-sol xhigh runtime/security and GPT-6.1-sol high
proof/corpus reviewers approve the corrected tree. Their independent runs pass
56 focused unit cases and one endpoint-retry integration case, and 27 focused
accounting/lifecycle cases plus the endpoint retry respectively, with zero skips.
The remaining current-sequence status sentence is aligned in this closeout;
no additional production change is needed. Step 1 is complete; Step 2 starts next.

#### Remediation Step 2 — Native ingress and URL boundary

**Complete — 2026-10-09; both independent review rounds passed.** Native HTTP admission
accepts GET/HEAD before Next on every callback path; other callback methods receive
masked no-store 404. Node retains parser-level refusals, including TRACK, and no
CONNECT tunnel is admitted. Production closes every upgrade. Development permits
only exact validated GET HMR, bounds its initial 101 handshake and closes missing,
failed or ignored delegation while retaining established HMR connections.

The launcher disables framework DEBUG before importing Next, including later
framework dotenv loading. Forwarding/authority carriers are removed from both
parsed and raw headers. `poweredByHeader` is disabled; the favicon matcher uses a
literal dot. URL verification applies pinned Next's full-URL `.rsc` normalization
before parsing and `_rsc` removal, then returns the signed raw context. Route and
locale admission still refuse suffix aliases. Redirect proofs compare actual
Location authority, path and ordered decoded query pairs, including apostrophes.

Flight protocol preservation is tested through the real adapter and middleware;
the direct middleware mock no longer claims to exercise adapter-hidden headers.
The disposable route emits only named booleans, and its five GET probes plus HEAD
must produce exactly six live API bootstrap calls. No product route is introduced.

**Validation — 2026-10-09.** All 469 frontend cases pass through the workspace
outcome guard (409 web, 60 SDK), with zero skips/todos. Lint and typecheck pass;
production build passes. The real API/PostgreSQL production-rendering case passes
1/1, including the six protocol/bootstrap calls and existing tenant isolation,
trace, freshness and containment. The actual native TLS fixture passes all-path
method/HEAD, matcher, query, production closure, DEBUG and retained development
HMR controls; its original planted cleanup control also passes. Corpus consistency
passes 5/5 and diff checks are clean.

The DEBUG canary caught a pinned-library detail during implementation: an empty
DEBUG is deleted by debug initialization. The final nonempty `-*` exclusion is set
before importing Next, survives initialization/dotenv and is exercised with enabled
process and dotenv canaries. Next development route discovery also requires a
fixture-owned source copy, whereas production uses its existing compiled output.
These are verified fixture/runtime corrections, not new product contracts.

**Review round 1.** Fresh GPT-6-astra xhigh runtime/security and GPT-6.1-sol high
proof/corpus reviewers approve implementation commit `458a1dd` without verified
findings. Independent focused runs pass 257 and 218 frontend cases respectively;
the runtime reviewer also compares ten targets with the installed Next adapter.

**Review round 2.** Fresh GPT-6-sol xhigh runtime/security and GPT-6.1-sol high
proof/corpus reviewers approve the same tree without verified findings. Focused
runs pass 125 and 218 frontend cases; the latter also passes all five corpus
consistency cases. These reviewers inspect the committed fixed-port fixture
proofs without rerunning them. Step 2 is complete; no corrective runtime commit
is required. Fixture ownership/containment and source/tooling remediation remain
Steps 3–4.

#### Remediation Step 3 — Fixture reliability and containment

**Implemented — 2026-10-09; independent reviews pending.** Test fixtures share
resource ownership, positive environment construction and bounded sticky output
scanning. Setup occurs inside cleanup ownership; signals, explicit parent control
closure and broken output pipes join the same cleanup before exit. Children stop
before private trees are removed. Process handles coalesce repeated cleanup and
retire permanently after confirmed absence; exited leaders still require cleanup
of surviving descendants. This narrows stale-handle exposure without claiming an
atomic POSIX process-identity signalling API.

Both fixed ports are preflighted. Readiness requires evidence from the actual
owned child before accepting a healthy response; a foreign listener is neither
reused nor stopped. HTTPS, raw TLS and WebSocket clients explicitly verify trust.
Wrong-CA and wrong-name controls fail before bootstrap. Private-output verdicts
remain sticky after truncation and across chunk boundaries; structural envelope
checks distinguish ordinary API/version text from private wire values.

The real renderer builds with nonempty private configuration and an actual
public-only Client Component. A successfully compiled secret-leaking component
fails emitted-asset scanning; the restored component builds cleanly with nonempty
client output. The API observes no request during compilation. Existing SDK,
host/trace isolation, six Flight/bootstrap calls, same-process freshness and stock
launcher refusal remain covered. Actual stdin closure and EPIPE controls require
exit 1 and removal of the owned temporary build tree.

**Validation — 2026-10-09.** The guarded frontend runner passes 491 cases
(431 web, 60 SDK), with zero skips/todos; workspace lint and typecheck pass.
`PublicServerRenderingTests` passes 15/15: the real production path, twelve
structured-log controls and two actual parent-pipe cleanup cases. The strengthened
pipe controls also pass a focused rerun. Release integration build reports zero
warnings/errors; changed-source format verification passes with a workspace-load
warning. The shared scanner/environment/owner and process-handle controls pass
27/27. Corpus consistency passes 5/5 with zero failures/skips, and ADR-0054's
change is insertion-only.

The real ingress source-mutant matrix passes 18/18: early OpenSSL failure,
asynchronous bootstrap refusal, setup/omitted cleanup, both signals, explicit IPC
closure, EPIPE, secure environment/TLS and disabled-TLS rejection, benign large
output and early split leaks, stale/bind failures, and upgrade closure/refusal.
Each intended failure is checked by its own oracle; cleanup rescue runs only
after the absence verdict and never re-signals a saved PID after confirmed absence.
Independent normal native/TLS/HMR verification remains the clean control.

**Review round 1.** Fresh GPT-6-astra xhigh runtime/security and GPT-6.1-sol high
proof/corpus reviewers inspect `e2f92b7`. Three verified findings are corrected:
failed child cleanup now retains the private tree, final child output drains
before shutdown containment, and token boundaries at chunk ends wait for a real
delimiter or EOF. A shutdown-only source mutant and split malformed-MAC controls
prove the fixes; the early-output control now includes an actual delimiter.
Focused helpers pass 31/31, the real ingress matrix passes 19/19 and the guarded
frontend runner passes 495 cases (435 web, 60 SDK), with zero skips/todos.
Changed-source lint passes. The original implementation counts above remain
historical. The real API/PostgreSQL renderer rerun also passes 15/15.

**Review round 2.** Fresh GPT-6-sol xhigh runtime/security and GPT-6.1-sol high
proof/corpus reviewers approve `131a933` without actionable findings. The runtime
reviewer independently passes 31 helper/process-group cases, the normal real
native ingress and all 19 source-mutant controls. The proof reviewer independently
passes 31 helpers and five Release corpus cases. Step 3 is complete after both
rounds and verified fixes. Source/tooling and final closeout remain Steps 4–5.

#### Remediation Step 4 — Source, runner and tooling proof

**Complete — 2026-10-09; both independent review rounds passed.** Bounded AST tracking
covers global Headers constructors, converted records, constant aliases,
destructuring and converted-record spread, with named clean/dirty controls and
real public-entry mutations. Plain WHATWG Headers spread stays inert. Effective
inherited tsconfig paths and workspace exports are checked against the resolver;
unsupported production module extensions fail explicitly. Real pinned Next SWC
and TypeScript verbatim emission are separate proofs; the conservative graph
keeps inline type-only edges even where SWC erases them. No production Client
Component is invented to make the census appear nonempty.

Web CI and staged lint share one source/script/root-config census, with generated
output/declarations excluded explicitly. Native `.mts`/`.cts` parsing is explicit;
the hook also invokes SDK/UI lint. Root Tailwind configuration now participates
in typecheck; its shared preset declares the existing Tailwind peer dependency.
The lock changes only that importer, without changing a resolved package version.
CI, `.nvmrc`, engine minimum and current setup guidance use Node 22.23.1. Generated
`.server` output is excluded from Prettier. Test discovery refuses symlinked
workspace packages or sources rather than silently skipping them.

The ADR disclosure job reads the lifecycle declaration inside Status, ignoring
banners and consuming the complete input. A real workflow-block control rejects
an undisclosed Accepted-with-banner edit and accepts its dated disclosure. The
original Accepted ADR status/body/history remains unchanged.

Raw HTTP proves two actual secret fields reach Kestrel, spend the exhausted
socket-IP fallback and bypass no lookup; one valid field then admits a fresh
visitor. Mapped-peer and noncanonical dotted-tail controls pin .NET behavior.
The actual public-local API launcher starts with only the application credential,
an empty owned user-secret root and no local env fallback, then serves both seeded
hosts through the trusted hop without PlatformAdmin. It uses a dynamic API port;
the accepted fixed renderer topology is unchanged.

**Validation — 2026-10-09.** The guarded frontend run passes 557 cases (497 web,
60 SDK), with zero skips/todos. Focused source-boundary cases pass 157/157;
lint-tooling/runner controls pass 39/39. Workspace typecheck/lint and frozen offline
installation pass. Seven Python lifecycle/workflow controls pass. The focused
backend units pass 45/45; trusted visitor and real local-launch integration pass
18/18, with zero skips. Release solution build reports zero warnings/errors;
owned backend formatting and diff checks pass. Both fresh review rounds remain
pending; final full regression, real CI and PR closeout belong to Step 5.

**Review round 1.** Fresh GPT-6-astra xhigh source/security review identifies two
confirmed static-alias gaps: destructured global constructors and separately
bound header iterators escaped the authority guard. Fresh GPT-6.1-sol high
tooling/backend review approves its scope after seven lifecycle controls,
39 tooling/runner cases, 45 unit cases, two real integration cases and web lint.
The correction follows verified global destructuring and gives header iterators
a distinct origin, consumed only by conversion/clone operations. Four planted
violations fail before the fix; named shadowed/inert/iterator controls and both
real render-helper mutations pass afterward. All 171 focused boundary cases and
571 guarded frontend cases (511 web, 60 SDK) pass with zero skips/todos;
workspace typecheck/lint pass. No production consumer exploits either gap.
**Review round 2.** Fresh GPT-6-sol xhigh source/security review confirms the
first-round fixes, then identifies the same static-constructor gap in the
separate API-hop-header setter fence. The correction reuses the verified global
constructor predicate; qualified/aliased/bound setters and a real helper mutation
are refused, while inert/shadowed constructors and the adapter exemption stay
clean. The before-fix run exposes all three setter escapes, the helper escape and
the old shadowed-constructor false positive. All 178 focused boundary cases and
578 guarded frontend cases (518 web, 60 SDK) pass afterward with zero skips/todos;
workspace typecheck/lint pass. Fresh GPT-6.1-sol high tooling/backend review
approves after seven lifecycle, 39 tooling/runner, 45 unit and 18 real integration
cases, web lint and diff checks. The source reviewer independently verifies
`ced7911`: all three setter escapes change from missed to flagged, shadowed/inert
controls stay clean and all 178 boundary cases pass. Step 4 is complete after both
rounds and verified fixes; Step 5 owns final corpus/CI/PR closeout.

#### Remediation Step 5 — Corpus and PR closeout

**Local closeout complete — 2026-10-09; both independent review rounds passed.**
Current architecture/security prose and Phase 02b G14 now reflect delivered
ADR-0054 behavior. CLAUDE, README and the ADR/roadmap indexes link this current
record instead of maintaining separate remediation-step status lists. Original
Accepted ADR bodies and dated delivery evidence remain unchanged; ADR-0054's
new delivery navigation points here.

Route guidance identifies the API as tenant/organization authority and marks the
Keycloak/session example as Phase 02b work requiring explicit ingress admission.
Seed output points to manual hosts/CA/leaf preparation and the paired public
launchers, without claiming P6 pages or P7's demo. Setup/test guidance distinguishes
random Testcontainers PostgreSQL ports from fixed renderer ports, and current
deployment-mode defaults from the Phase 02c Hub adapter. The seed-role diagnostic
correctly distinguishes FORCE RLS ownership from a privileged bypass credential.

**Final local validation — 2026-10-09.** Strict Release solution build reports
zero warnings/errors. All 2,940 .NET cases pass: 1,705 unit, 198 architecture,
four contract, 172 Docker-free integration and 861 Docker integration, with zero
failures/skips. Actual TRX counters and the nonempty/no-skip runner are checked.
All 578 guarded frontend cases pass (518 web, 60 SDK), with zero skips/todos.
Workspace typecheck/lint/build and regenerated SDK drift checks pass. Native
HTTPS/Next verification and all 19 independent failure controls pass; the full
Docker suite includes real production HTML/RSC, configured-build containment,
parent-pipe cleanup and the actual app-only API launcher.

Seven ADR lifecycle/workflow and 14 OpenAPI CI controls pass. Both compose
projections validate with and without the gated profile using the public example
configuration. Full backend format verification passes with a workspace-loader
warning and no formatting violation. Post-acceptance ADR changes are insertion-only.
The strict commit hook, changed-document relative links/fragments, added-prose
wrapping, corpus consistency and diff checks pass after the closeout edits.
Fragment verification is a manual local audit; CI's link job checks file existence.
All six live required checks and strict protection remain configured. [PR #26](https://github.com/HodeTech/LearnStack/pull/26)
records their execution on the final published head and the final PR description;
these local checks do not imply a successful remote run.

Remaining scope is unchanged: P6 owns product pages, request-local bootstrap reuse,
prefetch decisions and accessibility; P7 owns the browser/demo/Lighthouse harness.
Phase 11 owns distributed/edge limits, contention/load evidence, production
topology, replay/key lifecycle and tracing participation. These corrections add no
API schema, database migration, tenant resolver, authentication flow or Hub crossing.

**Review round 1.** Fresh GPT-6-astra xhigh corpus/governance and GPT-6.1-sol high
DX/evidence reviewers verify `2b5b6ac` against current source and actual reports.
One confirmed Minor finding is fixed: a second mutable step-status sentence still
said Step 4 reviews/Step 5 were pending. It now points to the section's current
status and per-step records. Whole-PR added-prose verification also wraps two
long standard/catalogue lines. Both reviewers approve after the verified
correction; no production change is needed. Round 2 and exact-head CI remain pending.

**Review round 2.** Fresh GPT-6-sol high corpus/governance and GPT-6.1-sol high
DX/evidence reviewers approve `869145e` without actionable findings. They
independently confirm the real TRX/frontend/native-control results, append-only
ADR history, current API authority, local TLS/port behavior and P6/P7/Phase 11
ownership. Link/fragment, prose, strict commit and diff checks pass; the five
Release corpus cases pass with zero skips. The later 578-case workspace report
is the frontend evidence; the earlier 557-case run remains historical.
Both rounds and verified corrections are complete. Development is the only
working branch; PR #26's final head/checks and description own remote closeout.

**Preparation verification — 2026-10-09.** Independent GPT-6-astra xhigh security
and GPT-6.1-sol xhigh corpus reviews find no remaining major issue in the proposal.
Confirmed wording/header/ownership corrections are applied. Release
`CorpusConsistencyTests` pass 5/5, with zero failures/skips. Manual inspection of
all 18 changed/new Markdown files resolves 1,226 relative links and 441 fragment
targets; added prose wrapping and `git diff --check` pass. Existing Accepted ADR
files are unchanged. These are proposal/document checks, not runtime remediation
or new CI evidence.

**Second independent proposal review — 2026-10-09.** Fresh GPT-6-sol xhigh design
and GPT-6.1-sol high corpus reviewers approve `d8d540a` for maintainer decision
approval with no remaining actionable findings. The design review independently
checks zero-permit framework behavior and actual redirect value/order preservation.
This closes the two proposal-review rounds; ADR-0054 remains Proposed and dependent
implementation still requires the maintainer's explicit approval.

**External ADR review clarification — 2026-10-09.** All six findings are verified
against the current limiter, HTTP tests, native launcher and app route census.
ADR-0054 now distinguishes the admitted-call/allocation budget from total refusal
traffic, names the test whose peer-debit expectation must change, and specifies
the process-wide lock, refusal metadata precedence and lifecycle proof obligations.
It records native admission across middleware exemptions, separate identification
control and the missing carrier/reference links. The app currently has GET health
and no POST handler or Server Action; real launcher controls remain implementation
obligations. Prior reviews/counts remain historical. ADR-0054 stays Proposed;
these documentation clarifications accept no decision and deliver no runtime fix.
Release `CorpusConsistencyTests` pass 5/5, with zero failures/skips. The two changed
Markdown files resolve 456 relative links and 283 fragment targets in a manual
audit; added prose wrapping and `git diff --check` pass. All phase text outside
this remediation section and every Accepted ADR file remain unchanged.

**Clarification review closeout — 2026-10-09.** Two fresh independent rounds review
`1a7c6fd`: GPT-6-astra xhigh / GPT-6.1-sol high first, then GPT-6-sol xhigh /
GPT-6.1-sol high. Both rounds report no actionable design or corpus findings after
checking the changed contract against source, existing tests and pinned framework
behavior. This approves the revised proposal for maintainer decision review;
it does not accept ADR-0054 or establish runtime remediation/CI evidence.

### P02d-5 merge and closeout (2026-10-09)

The maintainer merged [PR #26](https://github.com/HodeTech/LearnStack/pull/26) at
2026-10-09 15:45:36 UTC. GitHub records final PR head
`f09a4edf3e00e7043f6775642e10e40024ab9393` and merge commit
`863f5c1b0051c318a0326357c2fb65dded75afca`. The merge's second parent is that
head, and both commits have the identical tree. Development is fast-forwarded to
the verified merge without changing branches or rewriting history.

- All six required checks pass on the
  [final PR head](https://github.com/HodeTech/LearnStack/actions/runs/37943884658).
  Downloaded TRX confirms **2,940 passed, zero failed or skipped**: 1,705 unit,
  198 architecture, four contract, 172 Docker-free integration and 861 Docker
  integration. Frontend CI confirms **578 passed** (518 web, 60 SDK), zero
  skips/todos, and successful typecheck, lint, production build and SDK drift.
- Native HTTPS/Next verification and all **19 failure controls** pass in CI.
  Real API/PostgreSQL HTML/RSC, build containment and parent-pipe cleanup proofs
  remain part of the Docker suite. These are transport/runtime proofs, not P6
  product-page or P7 browser/Lighthouse delivery.
- The final review correction isolates fixture Git/hook environments from inherited
  `GIT_*` variables and reconciles README/catalogue status. Eleven tooling and
  seven ADR workflow cases pass locally under both invalid inherited Git settings
  and the normal environment, with typecheck, lint, format and link checks clean.
  The original four steps and five remediation steps retain their recorded
  independent review rounds and verified fixes.
- Read-only branch-protection verification confirms the same six required Actions
  contexts and `strict: true`; no protection setting changes.

All six required checks also pass in the
[merge-commit run](https://github.com/HodeTech/LearnStack/actions/runs/37954123158).
Independently downloaded merge-run TRX confirms the same 2,940 passing .NET cases
with zero failures/skips. Frontend logs confirm the same 578 guarded cases and
19 ingress controls. Both runs use the delivered Node 22.23.1 pin; the deferred
Lighthouse job remains owned by P02d-7.

**P02d-5 is closed.** The trusted native ingress, fair known-visitor accounting,
bounded configured caller, live-host/locale entry, uncached rendering policy and
source/runtime controls are delivered under ADR-0053/0054. No database migration,
public API schema change or authentication/Hub flow is added by this packet.

**Next: P02d-6 — Public renderer.** Its decision pass has not started. Re-verify
G5's unavailable-band behavior, G12's page states, G16's rendering part, G20's
subjects, G38(b,c) and G39–G43 against the delivered API and transport before
implementation. The packet owns catalog, course-detail/outline and lesson pages,
UI messages, ordered text-card rendering, safe theme injection, language/SEO
metadata, page/error/empty/locked states and accessibility checks. Request-local
bootstrap reuse and prefetch policy must also be settled before page consumers.

P02d-7 owns `make demo`/stop behavior, the two-host browser/full-stack harness,
Lighthouse activation and phase exit. Phase 11 owns production/distributed limits,
load evidence, ingress topology, replay/key lifecycle and tracing participation.
Phase 02d remains in progress; neither remaining packet nor Course Marketplace
commerce is delivered by this closeout. Earlier dated records remain unchanged.

**Documentation-only closeout validation.** Five local Release corpus cases and
seven ADR lifecycle/workflow controls pass, with zero failures/skips. The eight
changed Markdown files resolve 901 relative links and 393 fragments in the local
audit; prose wrapping and diff checks pass. ADR-0053/0054 changes are insertion-only;
earlier dated delivery records and P02d-1's decision/scope suffix are byte-preserved.
This closeout changes no production code or accepted decision.

## P02d-6 — Public renderer

### P02d-6 decision package (2026-10-09)

**Accepted — 2026-10-09, re-verified against `2d821c4`.** P02d-1–5 are complete
and merged. The maintainer explicitly approved revised
[ADR-0027](../decisions/0027-frontend-i18n.md), all gate answers and the status/URL
tradeoff below. The decision commit precedes implementation and claims no product
page or dependency delivery. Earlier preparation/review records remain historical.

#### Reviewed inputs and verified premises

The documentation pass covers the platform vision and MVP boundary; Principles;
the glossary and decisions index; ADR-0008/0009/0018/0035/0036/0040/0042/0043/
0050/0051/0052/0053/0054; Localization, Frontend Architecture and Tenant
Customization Model; Standards 03/06/07/08/09/11/13/15/16/21; Phase 02d and its
Phase 04/05/06/07 consumers. The `implement-task`, `add-frontend-route`,
`add-i18n-key`, `write-adr` and commit workflows are included.

Code verification covers the public SDK DTOs/results, native ingress and verified
entry, configured caller, middleware, root/public layouts, CSS tokens, renderer
registry, frontend source fences, guarded runner and real API/Next fixture.
In particular:

- Four public GET operations and their eligible alternates already exist. Pages
  need no new endpoint, write method, entity, migration, tenant id or Hub call.
- The only implemented lesson presentation is ADR-0051's ordered plain-string
  `default-card`. The larger registry does not implement additional renderers.
- Public theme DTO validation currently checks string shape, not color grammar.
  Safe HTML injection must validate the complete four-token value again.
- Middleware consumes and discards live site bootstrap. `React.cache` can share
  RSC work within a render, not between middleware and RSC execution.
- The production fixture currently injects a disposable transport-probe page.
  It proves P5 transport, not the actual P6 product pages.
- Root HTML still has fixed English/platform metadata. No UI catalogue/runtime
  is installed. Existing seed inventory does not fill a normal 20-item page.

The pass corrects current mutable carriers that prematurely claim product pages,
leave the delivered G12 interface open, or require an outbound URL sink in this
plain-text packet. Accepted ADR bodies and shipped delivery records are unchanged.

#### P02d-6 accepted answers

| Gate / part | Accepted answer |
|---|---|
| G5, G12 page states | Preserve public DTO eligibility and exact pins. Omit a null level; localize an unavailable level. Unavailable content has a bounded placeholder, never raw JSON or another revision. Distinguish empty catalog, empty public outline, restricted outline, empty ready content and unavailable content. |
| G16(g) | Render the complete safe tenant palette independently of plan. The effective `showPlatformAttribution` value alone controls platform attribution; no plan inference in the renderer. |
| G20 subjects | Census production frontend TS/JS and JSON, including renderer/message values, against the exact decoded seed identity literals from G20(a). Preserve the accepted platform-built-in and development/test exemptions, without a blanket catalogue exemption. P7 implements the existing registered rule across backend and frontend. |
| G38(b,c) | Vitest covers synchronous views and pure mappings; real production HTML/RSC tests cover async pages. Extend the existing required Docker integration fixture to unchanged product pages while preserving the isolated P5 transport probe. P7 still owns shared demo/browser/Lighthouse activation. |
| G39 | ADR-0027 is Accepted at first use: exact next-intl 4.14.9, server-first configuration using shared verified request admission, app-local `src/i18n/messages/{en,tr}/public.json`, dotted feature UI keys and one checked ICU catalogue contract. Backend `lockey_*` remains a separate wire namespace; the web app owns closed page-outcome mapping, not the SDK. No i18n routing middleware or preference cookie. |
| G40 | Three fixed content routes, minimal tenant chrome, both opaque pagination surfaces, exact-locale metadata and explicit page states. The maintainer explicitly approved the status/navigation choice below. |
| G41 | Render only the API's ordered plain-string fields through app-local synchronous components in `apps/web/src/components/public/`. Unsupported renderer or unresolved presentation renders the bounded fallback. No HTML, Markdown, linkification, authored URL sink or new primitive. |
| G42 | Emit a server-generated style element containing only four fixed `--ls-*` color properties after atomic `#rrggbb` validation. Null or malformed theme retains the entire existing CSS default palette. No per-token merge, style attribute, URL, font or organization override. |
| G43 | Enforce applicable jsx-a11y rules at error severity with actual-config planted controls; assert visible semantics/languages/titles. Catalog → course → lesson is a critical flow requiring keyboard, focus, reflow, contrast and a real screen-reader smoke record. Status promotion occurs with enforcement, not acceptance. |

UI catalogue fallback is not content fallback. An enabled language without an
authored UI catalogue remains admissible; UI groups and API-resolved labels carry
their actual language. Document language/direction follow the admitted route as
specified by ADR-0027. A test-owned enabled RTL locale and eligible content prove
document direction and English UI fallback language independently, without
rewriting the historical seed or promising an Arabic UI catalogue. No tenant
identity selects markup, translations or fields.

#### G40 status and navigation decision

Pinned Next **15.5.18** was exercised in an owned disposable production fixture,
without modifying this repository. These are planning experiments, not passing
product tests:

| Strategy | Observed initial document |
|---|---|
| `notFound()` below a loading boundary | Streamed HTTP 200 with not-found digest |
| Await resource outside child loading, then `notFound()` | HTTP 404, but generic error HTML without visible localized fallback or root `lang`/`dir`; fallback text exists only in Flight |
| Remove loading entirely | The same generic HTTP 404 error document |
| Local Suspense / access-fallback boundary | Visible fallback with HTTP 200, not a hard 404 |
| Await lookup, redirect to an explicitly admitted fixed error page; middleware supplies its 404 status | Local HTTP 307 followed by visible localized HTML 404 with root language/direction and no-store; HEAD is bodyless |

The maintainer accepted the last strategy, **including its URL/status tradeoff**.
Root/route admission awaits the request-cached resource
before emitting a document shell. A missing/hidden resource redirects to the
same host's fixed `/{locale}/status/not-found` page, which middleware admits only
after live host/locale validation and marks HTTP 404. That page is ordinary
server rendering, not a thrown `notFound()`. It echoes no original slug, query,
cursor or failure detail. Its title/robots are localized and noindex; it has a
catalog recovery link and the same safe theme/chrome. It never queries Education.

The original request is **307, followed by 404**, and the browser URL changes.
This is not a claim that the original document returns a direct 404. The
acceptance commit updates G40's completion criterion to name that exact chain and
adds this error namespace to the entry matrix and route skill. Middleware's
existing masked
unknown-host/provenance refusals remain direct, neutral responses. No new signed
data carrier, shared bootstrap cache or outbound header is introduced.

Returning a localized HTTP 200 error as though it were a 404 is rejected. A
middleware content preflight followed by another content read is also rejected:
it costs a fourth call on every success and still races the later read. A new
authenticated DTO-transfer protocol is not justified for this packet. Preserving
the original URL with a branded direct 404 would require a separately designed
response-ownership change; it is not silently promised by this decision.

Metadata, layout and page use the same request-local resource loader. Layouts do
not serialize child execution, so each page honors the loader result itself.
Required `loading.tsx`/`error.tsx` files remain, but no loading boundary may flush
the document before redirect admission. Loading UI is not promised during that
initial pre-admission wait. Unexpected framework errors retain the framework's
pre-stream 500 / post-stream 200 behavior; `error.tsx` cannot set arbitrary status.

Known content-call failures use controlled, translated, noindex page states:
invalid cursor → explanation and a relative reset link; 429 → retry-later state;
transport/invalid response/unavailable API → unavailable state. These are ordinary
HTTP 200 representations, not claimed HTTP 400/429/503 responses. Bootstrap
refusals retain middleware's real 404/429/503 and bounded Retry-After. A fresh
bootstrap failure on the error-route request remains that neutral refusal rather
than inventing a tenant theme or configuration. A closed mapping selects owned UI
keys for supported outcomes; unknown codes/failures use the unavailable state.
No Problem Details message key, title, field error or parameter becomes a lookup
identifier or visible copy.

#### Routes, pagination, metadata and chrome

- Keep `/{locale}/courses`, `/{locale}/courses/{slug}` and
  `/{locale}/courses/{slug}/lessons/{lessonSlug}`. Fixed section names remain until
  Phase 06; the platform placeholder leaves the public product path. Existing
  exact `/studio` and `/portal` scaffolds remain without invented authentication.
- Use ordinary same-host relative anchors for product navigation, including
  pagination: no automatic prefetch and no reliance on retained client Router
  Cache. A new document request re-reads current API state.
- Parse only owned pagination parameters from the verified raw signed target;
  observed Next `searchParams` is not their authority. Refuse duplicate, empty,
  malformed or oversized owned values. Cursors stay opaque and are never decoded
  by the renderer. Catalog uses `cursor`/`limit`; outline uses
  `lessonCursor`/`lessonLimit`, with the API's bounds and a default page size 20.
- Show restart/next links as applicable; do not fabricate a previous cursor.
  A test-owned inventory above the page bound proves both pagination surfaces;
  do not reduce the product page size or rewrite the historical seed inventory.
- A restricted course shows marketing metadata and a translated access notice,
  with no lesson count, lesson link, purchase/login/enrollment control or body.
  An empty public outline is distinct. Missing optional text is omitted.
- Minimal chrome contains tenant display name, catalog navigation, skip link and
  conditional platform attribution. Full menus, logo/media, authored errors,
  preview, organization theme overrides and Studio editing remain Phase 06.
- Canonical and Open Graph URLs use only the verified live host, existing HTTPS
  listener convention and validated local segments. Course/lesson hreflang uses
  the API's actual eligible alternate slugs, never the current slug substituted
  into another locale. Catalog uses enabled locales. Paginated views are noindex
  with the cursor-free first-page canonical; error states are noindex with no
  misleading resource alternates. Metadata shares the same data read as the page.

#### Rendering, theme and accessibility detail

Course lists use semantic lists; ordered outlines use ordered lists; field cards
use definition lists with authored order and language-bearing labels. Each page
state has one main, one descriptive h1, sequential headings, a skip target and
visible keyboard focus. Long unbroken strings wrap and logical CSS supports RTL.

Strings resembling scripts, HTML, `javascript:`, `data:` or HTTP URLs remain
escaped, inert text. Absent optional fields render nothing. Unknown composites
and unavailable content emit only bounded placeholders and at most one diagnostic
per request containing a count/state, not field values, keys or private context.
The original G41 candidate's integer/boolean/enum/taxonomy expansion is not
authorized by ADR-0051; those richer primitives remain Phase 04/05 and Phase 06.

The theme emitter admits the complete four-color grammar before producing fixed
CSS. Foreground/muted text retains its 4.5:1 background requirement. Primary has
only a 3:1 background guarantee, so normal text links use foreground plus an
underline; do not assume white text on primary is accessible. Primary may supply
non-text focus/border accents where its contrast is sufficient. CSP delivery
remains Phase 11; output must be safe without it.

Manual evidence names commit, environment, both hosts/locales, keyboard path,
focus, 320 CSS px reflow/zoom, long strings, contrast and the screen reader used.
DOM assertions do not replace that assistive-technology smoke. If it cannot be
performed, the record stays pending and packet completion is not claimed.
Full Playwright/axe remains Phase 06; Lighthouse activation remains P02d-7.

#### Request accounting and validation

Normal content document: middleware bootstrap + request-local RSC bootstrap +
one shared content operation = **three API calls**. A missing document followed
to the accepted error page adds two bootstrap calls = **five total**. Repeated
metadata/layout/page consumers must not add calls; the next incoming document
must re-read. No retry or shared validated-DTO cache is added.

The existing 60-call visitor and 600-call peer windows therefore give theoretical
ceilings of 20/200 successful three-call documents before other calls, not page
throughput promises. No limiter number, deadline, byte cap or partition changes.
Phase 11 owns production/distributed capacity and measured optimization triggers.

The actual-page fixture must retain learnstack_app, RLS/read-only transactions,
native HTTPS ingress, closed hop headers and existing containment/failure controls.
Keep the synthetic P5 probe in a separate mode so its catch-all cannot shadow
product routes. Assert visible HTML/DOM separately from Flight strings: status,
language/direction, headings, theme, attribution, both hosts/locales, cross-tenant
absence, overlapping concurrent requests across both hosts/locales, freshness in
one process, controlled failures, pagination, exact schema swaps and inert values.
Verify the 307→404 chain and no direct internal bypass.
These HTTP/RSC proofs are not a browser end-to-end claim.

#### Accepted implementation steps

0. **Decision commit.** Accept approved ADR/gate parts; reconcile every normative
   carrier and the changed G40 status criterion before writing implementation.
1. **Localization and document foundation.** Install/pin i18n, author complete
   catalogues and ICU controls; verified request-local loaders, language/direction,
   safe theme/chrome and controlled state components. Establish status-route
   admission with production regression, preserving all P5 controls.
2. **Catalog and course.** Implement actual list/detail/outline pages, both
   pagination surfaces, restricted/empty states and metadata from shared loaders.
3. **Lesson presentation.** Implement ordered default-card fields, unavailable
   fallbacks, language attribution, schema-swap and inert-value proofs; complete
   semantic/focus/lint controls across all page states.
4. **Product proof and closeout.** Complete the real product fixture, no-leak and
   request-count/freshness controls, manual accessibility evidence and all carrier
   updates. Validate, then open the implementation PR for maintainer review.

Every numbered implementation step is committed, then reviewed by fresh agents
in two independent rounds. Verify each finding against current code, commit
confirmed fixes, rerun affected checks and only then advance. Reviewer model and
effort follow criticality; security/runtime and proof integrity have separate
review lines. No branch/worktree change; all commits remain on `development`.

Validation comprises the guarded frontend suite, typecheck, lint, production
build/native verification, relevant Release backend architecture/contract/unit and
real Docker integration suites, formatting, diff checks and relative-link/anchor
audits. Record actual passing counts only after execution. Browser/assistive
technology observations are reported separately from automated test evidence.

#### Approval boundary and acceptance carriers

The maintainer approved these together on 2026-10-09:

1. ADR-0027's library, pin, catalogue home and UI/content-locale separation.
2. G40's explicit local 307→404 error-route behavior and URL change, three-call
   normal path/five-call missing path, plain-anchor navigation and controlled
   HTTP 200 noindex API-failure states. The original direct-404 wording is not
   claimed as implemented by this plan.
3. The remaining gate answers, four implementation steps and mandatory manual
   accessibility proof above.

The acceptance commit updates the decisions index; Standards 03/06/07/08/09/16/21
and index;
Localization/Frontend/Customization architecture; Phase 04/05/06/07 inheritance;
glossary terms where introduced; the frontend route/i18n/block and test skills;
and this register,
renderer scope/completion criteria and decision record. Only implemented tests
may be labelled Implemented. Accepted ADR-0053/0054 remain unchanged: this selects
their delegated page composition, without another authority carrier or budget.
If review finds an Accepted decision must change, draft its replacement and seek
approval before dependent code. P02d-7 and Phase 11 ownership remains unchanged.

#### Acceptance validation (2026-10-10)

The acceptance-only reconciliation passes five Release corpus-consistency cases
and seven ADR-status workflow cases, with zero failures/skips, plus added-prose
wrapping, relative-link/fragment and diff checks. A fresh independent acceptance
review confirms Registered proof statuses, unchanged previously Accepted ADR bodies
and the pending implementation boundary. Its confirmed stale block-skill carrier
is reconciled before the decision commit. No dependency or runtime is delivered.

#### Preparation review and validation

Two independent draft-review rounds complete: GPT-6-astra high and
GPT-6.1-sol high review runtime/corpus first; fresh GPT-6-sol high and
GPT-6.1-sol high review the corrected package second. The separate i18n review
also checks the pinned package evidence. Confirmed fixes add a complete message
key example, ICU formatting in the selected UI language, callsite-to-catalogue
coverage with planted controls, and overlapping host/locale request proofs.
Both final round-two reviews report no actionable issue; maintainer acceptance
is still required.

Documentation-only validation: five Release corpus cases and seven ADR
lifecycle/workflow cases pass, zero failures/skips. Local relative-file/fragment
audits, added-prose wrapping and `git diff --check` pass across the ten changed
Markdown files. ADR-0027 parses as Proposed. No Accepted ADR file or production
code is changed; no frontend/product, browser or accessibility test is claimed
passing by this preparation record.

#### External i18n draft review reconciliation (2026-10-09)

The review of `64eefe8` is checked against the SDK, current entry/caller and
mutable documentation. Its six finding groups are addressed: general UI keys
use dotted feature names in the proposal; backend `lockey_*` resources remain
intact; the false SDK translation-map claim is removed. Standards 09, the glossary
and the key skill now distinguish wire-message data from consumer-owned copy.
Acceptance explicitly reconciles Standards 03/07/08/09 and the catalogue sketches.

ADR-0027 now names the shared verified admission source for request configuration,
whole-catalogue fallback versus missing-key build failure, the future Standards 21
section, actual repository dependency pins and test-owned RTL inventory. The
review's prefix claim is qualified: `lockey_` is a backend invariant, not a blanket
ban on frontend error resources. No Accepted backend localization contract is
removed. No gate is accepted and no implementation or dependency is added.

Fresh first-round runtime/corpus reviews use GPT-6-astra high and GPT-6.1-sol
high; an independent second round uses GPT-6-sol high. All report no actionable
finding in the corrected proposal. Maintainer acceptance remains required.

Five Release corpus cases and seven ADR workflow cases pass with zero failures or
skips. Relative-file/fragment, added-prose wrapping and diff checks pass. These are
documentation checks; no product-runtime or accessibility proof is claimed.

### P02d-6 Step 1: Localization and document foundation

**Foundation implemented — 2026-10-10; both independent review rounds complete.**
Work stays on `development`. This record covers the foundation only;
actual catalog/course pages remain Step 2 and lesson presentation remains Step 3.
It does not close P02d-6 or any product/manual accessibility criterion.

#### Delivered foundation

- Exact `next-intl` 4.14.9 dependency/lockfile, App Router plugin and server-only
  request configuration, with complete app-local English/Turkish `public`
  catalogues. ICU/key/argument and production-callsite census controls include
  planted missing-key, absent-from-all, misspelled-callsite, malformed-ICU and
  argument-mismatch failures. Formatter failures select bounded translated copy.
- Shared server-only request-local admission and content loaders re-verify ingress,
  read live site configuration and take content locale/route/pagination identity
  from the signed target. The request loader imports neither next-intl nor
  messages. UI fallback and runtime direction selection preserve content identity.
  A private weak identity memo keyed only by Next's exact request-store headers
  object retains same-request admission/content work across error rendering; no
  header value, host or envelope keys a representation cache. `getPublicUi` uses
  React cache only to reuse selected messages/translators inside an RSC render;
  it does not own admission or content-read deduplication.
- Root document language/direction, atomic validation of all four color slots
  before fixed style-element emission, and tenant chrome with one main/skip
  target, catalog anchor and effective conditional platform attribution. Null or
  malformed themes retain the entire existing CSS default palette.
- Closed translated state views, loading and unexpected-error boundaries. The
  interactive retry boundary receives bounded labels only. The admitted fixed
  status page supplies localized title/noindex metadata and a catalog recovery
  link, echoes no original target and performs no Education read. Shared resource
  admission selects the approved local 307→404 path for missing content.

The production fixture adds a separate foundation-consumer mode while retaining
P5's isolated transport probe. The temporary consumer exercises foundation code;
it does not deliver or prove unchanged catalog/course/lesson product pages.
The real production fixture verifies three API calls for a normal document,
three again on its next request and five across the missing-resource redirect
chain. Parallel status requests, bodyless HEAD, renewed host/locale refusals and
canonical redirects pass. Enabled `ar`, `tr` and `tr-TR` coverage checks visible
localized status HTML/metadata, content direction and labelled English fallback
without an Education read. The historical seed remains unchanged.

#### Verification and review boundary

The guarded frontend run passes **817 tests: 757 web and 60 SDK**, with zero
skips/todos. All **198 Release architecture cases** pass. The production build
passes with the dynamic status route and root language/theme injection; the real
fixture checks that building performs no API prerender calls. Workspace lint and
typecheck pass. The focused real-API production integration run passes **16 cases,
zero failures/skips**, retaining the separate P5 transport and cleanup controls.
Native ingress regression verification passes TLS, method/upgrade admission,
query redirects, production containment and development HMR. Missing static assets
are refused before tenant bootstrap even when Next invokes the root fallback.
No unchanged content-page,
browser E2E, Lighthouse or manual accessibility pass is claimed here.

All five P02d-6 architecture-test rules remain **Registered** until their complete
named proof obligations are verified; a foundation test alone does not promote the
broader page-state or accessibility rule. Accessibility remains **Adopted**.

#### Step 1 review round 1 (2026-10-10)

Fresh GPT-6-astra xhigh and GPT-6.1-sol xhigh sessions review `c14ecf2..568ecc2`
independently. Runtime/security has no confirmed finding; the proof/corpus review
finds one stale live packet-table cell. It is verified against the delivered code
and corrected to link this foundation record and distinguish the remaining steps.
No production change is required by this round.

Independent verification includes 216 focused runtime cases and 360 focused proof
cases; the proof reviewer also reruns all 817 guarded frontend cases with zero
skips/todos. Both reviewers approve the Step 1 scope. They inspect the production
fixture but do not rerun its native/Docker execution; the root's validation above
owns that evidence. Product/manual accessibility work remains Steps 2–4.

#### Step 1 review round 2 (2026-10-10)

Fresh GPT-6-astra xhigh and GPT-6.1-sol xhigh sessions independently review
`c14ecf2..73b9d53`. Runtime/security has no confirmed finding. Proof/corpus review
confirms two Minor workflow-document issues: stale installation status in the
i18n/route skills, and a route example whose narrowed translator namespace does
not match the implemented callsite census. Both are corrected against the shipped
foundation; the reviewer verifies the resulting skill diff. No production code
change is needed.

The runtime reviewer passes 374 focused cases across ten files and verifies the
pinned Next/React CSS ordering with a no-port render. The proof reviewer passes
269 focused cases, five Release corpus cases and seven ADR workflow cases.
Both report zero failures/skips;
neither reruns native/Docker fixtures or claims browser/manual accessibility.
The root checks all 30 relative links/fragments in the two changed skills and
`git diff --check`. Both independent rounds are complete; Step 2 follows.

### P02d-6 Step 2: Catalog and course pages

**Implementation — 2026-10-10.** The actual catalog and course routes consume the
shared verified resource loader independently in page and metadata. The obsolete
platform placeholder is removed; live middleware retains default-locale entry.
Synchronous server views expose semantic course/outline lists, distinct empty and
restricted states, actual label language and independent opaque next/restart links.
Restricted marketing never emits lesson counts, links or access-purchase controls.

Canonical/Open Graph URLs use the verified live host, fixed HTTPS listener and
validated local segments. Course alternates use actual eligible translated slugs;
catalog alternates use enabled locales. Cursor pages are noindex with first-page
canonicals; controlled failures are localized noindex states without resource
alternates. No metadata/page consumer adds another content read or client cache.

The foundation fixture now copies actual catalog/course routes unchanged and
keeps the isolated P5 transport probe. A separate pagination mode uses test-owned
inventory above 20 items and a fresh unchanged visitor budget; historical seed
declarations stay unchanged. Lesson presentation remains Step 3; full product and
manual accessibility closeout remains Step 4.

**Validation — 2026-10-10.** The root passes 981 guarded frontend cases (921 web,
60 SDK), zero failures/skips/todos, workspace typecheck/lint and production build.
Both product routes are dynamic; the build reports about 103 kB First Load JS,
which is a local build observation, not a Lighthouse or production performance
claim. All 198 Release architecture cases and seven ADR workflow controls pass,
with zero failures/skips. The solution Release build has zero warnings/errors.
Native ingress verification passes TLS, method/HEAD admission, redirects,
production closure, DEBUG containment and development HMR.

All 17 focused Release `PublicServerRenderingTests` cases pass, zero failures/skips.
The actual foundation mode observes exactly 52 API calls; the independent default-
pagination mode observes 18. Both stay within a fresh unchanged 60-call visitor
budget. Positive host-scope assertions derive from the seed declaration: Yoga's
mapped host includes tenant-wide and its own organization's published marketing,
while excluding the other organization. These are HTTP/RSC proofs, not browser
or manual accessibility evidence. Formatting, local links/fragments, added prose
and diff checks pass. Review closeout precedes Step 3.

**Step 2 review round 1 — 2026-10-10.** Fresh GPT-6-astra xhigh runtime/security
and GPT-6.1-sol xhigh proof/corpus agents independently reviewed
`785c8c5..917f9f8`. Both approve with no actionable findings. The runtime reviewer
ran 283 focused frontend cases; the proof reviewer ran the 166 new view/metadata/
path cases, fixture syntax and diff checks. Both traced adjacent admission and
resource loaders, approved metadata/pagination boundaries and real fixture
accounting. Neither started a build, server or Docker fixture; their pure checks
supplement the implementation validation above. Round 2 remains pending.

**Step 2 review round 2 — 2026-10-10.** Fresh GPT-6-astra xhigh runtime/security
and GPT-6-sol xhigh proof/corpus agents reviewed `785c8c5..86f8ea1`. Both
independently confirmed a missing course hreflang self-reference: the real API
excludes the current locale from its alternate list, unlike the unit fixture.
The fix explicitly uses the current validated canonical, preserves other eligible
API slugs and refuses a supplied self alias. The corrected DTO fixture and actual
production HTML assert self/translated/reciprocal URLs. Protected-content absence
canaries now derive from seed lesson titles, slugs and values, with a nonempty
control and full HTML/Flight checks. Two current architecture-tree comments now
acknowledge delivered catalog/course views.

Both reviewers inspected and approve the focused fixes, with no remaining
concrete finding. The root passes 284 focused frontend cases, workspace typecheck,
targeted lint, fixture syntax, scoped formatting and diff checks. All 17 focused
Release production-rendering cases pass again, zero failures/skips, with the
unchanged 52/18 call accounting and new real metadata/containment assertions.
Local links/fragments and added prose checks pass. Step 2 is complete after both
independent review rounds; Step 3 follows.

### P02d-6 Step 3: Ordered lesson presentation

**Implementation — 2026-10-10; both independent review rounds complete.** The actual
lesson route consumes the shared verified resource loader in page and metadata,
without a separate content read. Its synchronous view renders the API's ordered
plain-string fields as definition pairs, preserves present empty strings, and
attributes each resolved label's language/direction independently of the content
and UI locales. The course backlink uses the admitted request locale. No field
name, raw body/schema, active URL, HTML or richer primitive becomes visible.

Empty ready content differs from unavailable/unknown presentation. Unsupported
renderers short-circuit before reading their descriptors and emit at most one
request-local bounded count/state warning; API-reported unavailable definitions
retain their own existing diagnostic. Lesson canonical/Open Graph and eligible
hreflang URLs use validated current and actual translated course/lesson slugs.
Unavailable/unknown presentation is noindex with its eligible canonical preserved.

All 31 active recommended jsx-a11y rules are blocking through the actual app
configuration. A literal census and per-rule dirty/clean controls detect missing
rules, downgraded severity and vacuous subjects. Synchronous view and page-dispatch
units supplement the production fixture; they do not claim async Server Component
DOM support. Full product/manual accessibility closeout remains Step 4, and the
Accessibility standard stays Adopted.

**Validation — 2026-10-10.** The guarded frontend suite passes 1,059 tests
(999 web and 60 SDK), zero failures/skips/todos. Workspace typecheck and full web
lint pass. All 18 Release production-rendering cases pass, zero failures/skips.
The new presentation mode observes exactly 46 API calls; foundation and pagination
retain 52 and 18 respectively, each in a fresh unchanged visitor budget.

Actual English and Yoga lesson HTML/RSC proves ordered text, resolved label
languages, eligible translated metadata and tenant absence. A test-owned successor
revision first leaves the deprecated exact pin unchanged, then changes descriptors
only after switching the lesson pin. Inert script/markup/URL strings, absent
optional fields, empty ready and unavailable content are exercised in one running
process. Setup restores the original body, pin, lifecycle and generation. Both
cross-host and protected lesson requests follow the localized 307→404 chain:
original path segments may occur in Next's redirect routing tree, while private
content stays absent and the final status document omits even the original slug.
Native ports, fixture directories and containers are clean after completion.

Workspace production build passes with all three content routes dynamic; its
about 103 kB First Load JS is a local observation, not a Lighthouse result.
All 198 Release architecture cases and seven ADR workflow cases pass, zero
failures/skips. Local relative links/fragments, added-prose and formatting checks
pass. Native verification passes TLS, method/HEAD admission, redirects, production
closure, DEBUG containment and development HMR. The solution Release build passes
with zero warnings/errors. No browser or manual
accessibility pass is claimed; full product/manual proof remains Step 4.

#### Step 3 review round 1 (2026-10-10)

Fresh GPT-6-astra xhigh runtime/security and GPT-6.1-sol xhigh proof/corpus agents
review `c250677..0be38e8` independently. No Blocker/Major is found. The runtime
review confirms a misleading recovery destination: the translated catalog anchor
reloads the failed lesson. The lesson route and the adjacent course route now
send unavailable/rate-limited recovery to the admitted locale's catalog; invalid
cursor reset retains its original route. Dispatch controls render the synchronous
state and assert translated label and destination together.

The proof reviewer independently confirms a stale narrow ADR-0051 catalogue row.
It now names the delivered component and actual presentation fixture as
Implemented, with its published counts reconciled. The five broader P6 rules stay
Registered
and Accessibility stays Adopted pending Step 4. Each reviewer passes 164 focused
frontend cases, zero failures/skips; neither runs native/Docker or manual checks.
The root passes 167 focused frontend cases, five Release corpus cases, workspace
typecheck and diff checks after the fixes. A fresh second round follows before
Step 4.

#### Step 3 review round 2 (2026-10-10)

Fresh GPT-6-astra xhigh runtime/security and GPT-6-sol xhigh proof/corpus agents
review `c250677..3ac4ee1` independently. Runtime approves with no actionable issue
and passes 173 focused frontend cases. Proof/corpus confirms one defensive recovery
edge: a lesson API validation failure selects cursor reset although lessons have
no cursor. The closed mapping now reserves cursor failure for HTTP 400 catalog/
course reads; lesson and other-status validation failures select unavailable.
The result matrix tests both boundaries without displaying backend diagnostics.
This is a defensive case, not a reproduced admitted normal-path API failure.

Both reviewers approve the focused mapping fix with no new concrete finding. The
root passes 176 focused frontend cases, zero failures/skips/todos. Final typecheck
requires the negative test DTO's mandatory empty `fieldErrors`; it is corrected,
and all 53 resource cases and workspace typecheck pass. Diff checks pass. Both
independent rounds are complete; Step 4 follows.
Actual product RTL/fault/concurrency and manual accessibility obligations remain
explicit.

### P02d-6 Step 4: Product proof and accessibility closeout

**Complete — 2026-10-10.** The unchanged product routes now
have dedicated `product-isolation` and `product-freshness` modes in
`PublicServerRenderingTests` / `verify-public-rendering.mjs`. P5's synthetic
transport mode and the foundation, pagination and presentation modes remain
separate and mandatory. This step adds proof inventory; it changes no production
route, API, rate budget or historical seed declaration.

Test-owned Arabic translations use the same ASCII course/lesson slugs on both
hosts, with each course's original organization scope. A four-target content
barrier admits both hosts × English/Arabic before continuing the real API pipeline;
HTML and Flight checks reject opposite-tenant and protected authored markers.
Actual document direction, authored content and independently labelled fallback
UI are observed. Both seeded four-color palettes and effective attribution are
checked independently; one malformed theme must fall back atomically to the
whole local default palette, then recover after restoration.

Freshness runs in one native server process. Publishing state changes remove the
course from the catalog and send course/lesson detail through the approved local
307→fixed localized 404 chain; restoring publication restores eligible content
without changing the other host. A naturally invalid cursor reaches the real
API. Strict one-shot exact host/locale/path content-boundary faults exercise
429 and 503 page states while bootstrap still uses the real application role and
read-only pipeline. These two faulted Education requests intentionally stop at
the test-owned API boundary; they do not claim production handler execution.

Known content failures expose bounded translated HTTP 200/noindex views and
accurate cursor-reset/catalog recovery. Next may retain the request's own cursor
in its private Flight routing tree; the visible state/title and recovery link do
not echo it. Problem details and private provider copy stay absent from the
complete response. Fixture mutations restore locales, translations, theme and
publication in `finally`; every mode uses a fresh unchanged visitor budget.

**Validation and reviews.** The guarded frontend run passes 1,065 cases
(1,005 web, 60 SDK), zero failures/skips/todos. All 20 focused Release
production-rendering cases pass, zero failures/skips.
The new isolation/freshness modes observe exactly 43/52 API calls; the earlier
foundation/pagination/presentation modes retain 52/18/46. Full backend regression
passes as recorded below; both review rounds are complete after their fixes.
Automated checks do not establish manual accessibility. The dated walkthrough
below records keyboard, focus, 320 CSS px reflow and contrast observations;
the maintainer's subsequent VoiceOver confirmation closes the remaining
screen-reader check in the packet closeout below.

#### Step 4 review round 1 (2026-10-10)

Fresh GPT-6-astra xhigh runtime/security and GPT-6.1-sol xhigh proof/corpus agents
review `e2b8de4..e073075` independently. Neither finds a Blocker/Major. Both
independently reproduce a narrow containment-test false negative: the injected
Problem Details type URI uses a different marker from the other private fields.
The URI now includes the common private sentinel; both reviewers confirm that a
type-only leak is rejected and a clean response remains accepted. No production
leak is demonstrated. The proof reviewer also confirms stale scaffold-only
wording in README; it now distinguishes implemented public pages from the
Studio/portal scaffolds. Related mutable roadmap, frontend setup and contributor
carriers are aligned without claiming completion or browser evidence.

The root passes the full Release Docker integration suite: 866 passed, zero
failures/skips. After rebuilding the one-line fixture fix, both product modes
pass again (2/2); exact 43/52 request counts remain unchanged. Earlier current
Release results are 1,705 unit, 198 architecture, four contract and 172 Docker-free
integration cases, all passing with zero skips. TRX counters for the newly run
suites are inspected directly. Reviewers run read-only helper probes, syntax and
link checks; neither claims independent full-suite or manual execution. Both
fresh second-round reviews follow the fix commit. Manual accessibility remains
an explicit packet-completion requirement.

#### Step 4 review round 2 and automated verification (2026-10-10)

Fresh GPT-6-astra xhigh runtime/security and GPT-6-sol xhigh proof/corpus agents
review `e2b8de4..b86e20d` independently. No production defect is demonstrated. The
proof review identifies two assertion gaps: the malformed-theme check only sees
primary-token style elements, and fixed-status containment only checks `p6-`
requested slugs. The first now rejects any of the four color-token declarations
in style elements or style attributes. The second rejects every requested course
and lesson slug on the final fixed 404, including historical seed slugs. Own
requested route segments remain permitted in Next's 307 Flight tree, as already
recorded in Step 3; private content remains forbidden on both responses.

Both reviewers independently verify the fixes with extracted real helpers and
clean/planted controls. The root's ten planted violations are all rejected;
both real product modes then pass again, zero failures/skips, preserving exact
43/52 API call counts. Current-state documents distinguish implemented automated
product proof from pending manual observation. ADR-0027 Amendment 2 is insertion
only and changes no accepted decision. The optional suggestion to revisit the
original faulted resource after catalog recovery is not required: the accepted
G40 catalog-navigation contract is directly exercised and passes. No automatic
retry or broader recovery guarantee is introduced.

Full Release backend verification totals 2,945 distinct passing cases: 1,705 unit,
198 architecture, four contract, 172 Docker-free integration and 866 Docker
integration. There are zero failures/skips; recorded TRX counters are inspected.
The architecture suite is rerun after the current-status documentation changes.
Guarded frontend verification passes 1,065 cases (1,005 web, 60 SDK), zero
failures/skips/todos. Workspace lint/typecheck, JavaScript syntax, Prettier and
the seven ADR workflow tests pass. Production build evidence remains the earlier
Step 3 build; the review fixes change proof assertions and documentation only.
A local audit checks 2,194 relative links and 666 fragments across 33 changed
Markdown files against `origin/main`, excluding one explicit catalogue-link
template placeholder. No target is missing. This manual script audit does not
claim that CI's file-only link job enforces fragments. Added prose respects the
88-column rule and `git diff --check` passes.

#### Manual accessibility handoff (2026-10-10)

A temporary production fixture is prepared from `b86e20d`, with the real API,
`learnstack_app`, read-only frames, actual public routes and test-owned Arabic
long-string content. A separate Brave window opens both institution hosts. The
browser refuses the temporary test certificate before any product page loads;
the computer-use policy requires the user to pass that security interstitial.
The requested handoff is pending. No keyboard, focus, 320 CSS px reflow, contrast
or screen-reader pass is claimed. The temporary server and its owned children
are stopped cleanly before the automated rerun; ports 3000/3011 are free and no
test container remains. Existing developer services are untouched.

Resume the actual manual walkthrough after the browser handoff, record its
commit/environment/hosts/locales/assistive technology and fix any observed issue
before closing P6 or promoting Accessibility and the five registered P6 rules.
The packet remains in progress; P02d-7 and its Lighthouse job have not started.

#### Manual browser observations and remaining AT check (2026-10-10)

The fixture is restarted and both product pages load after the user handles the
temporary certificate interstitial. The tested production routes are unchanged
at `180dde0`; the disposable fixture adds Arabic translations and a 240-character
unbroken `W` value. Observation uses Brave 154.1.96.61 on macOS 27.0.1, with the
real API/application role and native HTTPS ingress. The manual fixture uses the
default entitlement provider; the distinct attribution branches are proved by
the automated product-isolation mode, not by this walkthrough.
The tested hosts are `english.localhost:3000` (`en`, `ar`) and
`yoga.localhost:3000` (`tr-TR`, `ar`).

- Keyboard-only English catalog → English foundations → Present simple and
  Turkish Yoga catalog → Stüdyo temelleri → Ağaç duruşu navigation succeeds.
  The skip link becomes visible on first Tab, Enter moves to main content and
  subsequent links follow content order. Blue/green focus outlines are visible.
- DevTools explicitly reports a 320 CSS px viewport. English and Turkish
  catalogs, the Turkish course and lesson, and both hosts' Arabic lessons reflow
  without observed horizontal clipping or overlapping text. The long unbroken
  value wraps; scrolling reaches the subsequent labelled fields. Arabic authored
  content remains right-to-left while fallback UI/definition labels remain
  left-to-right. This is a desktop responsive-viewport observation, not a mobile
  device or zoom-conformance claim.
- At 320 CSS px, the English invalid-cursor view has readable status text and a
  visible focused reset link; Enter opens the query-free catalog. A missing Yoga
  lesson arrives at `/tr-TR/status/not-found`; the Turkish branded status and
  focused catalog recovery link fit, and Enter returns to the catalog. HTTP
  status/noindex assertions remain automated evidence.

Contrast is calculated from the exact four-color palettes separately from the
visual observations, using WCAG relative luminance. Ratios against each theme's
background are:

| Palette | Foreground | Muted text | Primary focus outline |
|---|---|---|---|
| English | 17.740:1 | 7.557:1 | 6.702:1 |
| Yoga | 14.628:1 | 7.357:1 | 6.876:1 |
| Local defaults | 17.853:1 | 4.759:1 | 4.634:1 |

These exceed 4.5:1 for the text pairs and 3:1 for the focus pair. They do not
constitute a full WCAG audit. Normal viewport/zoom are restored after observation.

**Real screen-reader evidence is still pending.** VoiceOver is initially off;
its caption-panel preference is already on. Enabling VoiceOver through System
Settings changes the switch to on, but the native app inventory still reports
VoiceOver as not running; no speech or caption output is observed. Direct
VoiceOver app access times out; VO navigation produces no observed output.
The switch is restored to off without changing the caption preference.
Accessibility-tree inspection is not substituted for an
actual screen-reader pass. The maintainer is asked to perform the remaining
English/Turkish catalog → course → lesson screen-reader smoke. P6 and standards
promotion remain pending that evidence; no production defect is demonstrated.

The maintainer explicitly leaves the real screen-reader check pending on
2026-10-10; this does not waive the accepted completion requirement. The
temporary fixture stops cleanly with child exit 0, ports 3000/3011 are free and
its disposable test container is gone. Existing developer services remain
untouched. Resume this final check in a fresh manual fixture before packet
closeout, standards promotion or the completed-implementation PR handoff.

#### P02d-6 packet closeout (2026-10-10)

The preceding pending notes record earlier attempts. The manual fixture is
restarted from unchanged production routes at `180dde0`, using the same
Brave/macOS environment recorded above, real API/application role, read-only
transactions and native HTTPS. The maintainer performs the actual VoiceOver
smoke and explicitly confirms both checks:

- English (`english.localhost:3000/en`): catalog → English foundations →
  Present simple passes without an observed issue.
- Yoga (`yoga.localhost:3000/tr-TR`): catalog → Stüdyo temelleri → Ağaç duruşu
  passes without an observed issue.
- On both flows, headings, links, skip navigation and lesson field label → value
  order are understandable. The maintainer also confirms reading the missing-page
  and invalid-link messages and following their catalog recovery links.

This is maintainer-reported real VoiceOver evidence, separate from the agent's
earlier keyboard, focus, 320 CSS px desktop reflow and calculated contrast checks.
It does not claim a full WCAG audit, mobile-device test, zoom-conformance result
or Arabic screen-reader pass. The owned fixture stops cleanly with child exit 0;
ports 3000/3011 are free, its disposable container is removed and existing
developer services remain untouched.

All four implementation steps and both fresh review rounds per step are complete.
The automated totals and scope remain those recorded above: 2,945 passing backend
cases and 1,065 passing guarded frontend cases, zero failures/skips/todos.
The manual closeout permits Accessibility Standards to become Active and the
five P6 catalogue entries to become Implemented; the automated checks named by
those entries are already present. The closure change updates documentation only.
Final corpus/link validation and two fresh documentation review rounds complete
before the implementation PR is handed to the maintainer, as recorded below.

Closeout validation passes all 198 Release architecture cases, including the
corpus guards, with zero failures/skips, and all seven ADR-workflow tests. A local
inline-link audit, excluding fenced and inline code, checks 2,217 relative links
and 678 fragments in 34 Markdown files changed against `origin/main`; no file or
fragment is missing. This remains a manual audit, not a new CI anchor check.
Added prose and diff checks pass. No production code or test changes accompany
this closeout.

Fresh GPT-6.1-sol high corpus and GPT-6-astra high evidence/governance reviewers
independently review `27a02ba..7d98228`. Both approve with one verified Minor:
the catalogue's current status-consistency assertion quotes the former
nineteen/three split. It now references the index-owned counts instead. Both
reviewers verify the five promoted entries against existing tests, 165 catalogue
rules (107 architecture, 58 outside), twenty Active/two Adopted standards,
maintainer-attributed VoiceOver scope and insertion-only ADR history. Neither
claims new broad runtime execution.

Fresh GPT-6-sol high consistency and GPT-6.1-sol high history/evidence reviewers
independently review `27a02ba..2b6dbb4` after the fix. Both approve without findings.
They verify current status, catalogue and standards counts, bounded manual
evidence, preserved historical records and insertion-only ADR changes. The
history reviewer independently checks 1,832 relative links and 565 fragments in
the 23 closeout Markdown files; none is missing. Neither reruns a broad runtime
suite. Both closeout review rounds are complete.

P02d-6 implementation is complete; PR review and merge remain separate. P02d-7
owns `make demo`, the browser/full-stack CI harness, Lighthouse activation and the
Phase 02d exit checks. Its decision pass must close G20/G33/G38/G44/G45's remaining
parts before implementation. Full Playwright/axe and the expanded renderer/Studio
remain Phase 06; no later packet is delivered by this closeout.

#### P02d-6 external-review remediation (2026-10-10)

The external review of PR #27 at `47f9c12` approves without Blocker/Major
findings. Its three Minors and three documentation comments are rechecked against
the current tree; all remain valid and receive bounded fixes:

- The cross-tenant course probe derives a foreign-only published slug and locale
  from `SeedData`, not a second JavaScript literal. The other host's asserted
  catalog keeps a visible control for that course. C# independently checks the
  refused API path, locale and 404; the shared slug cannot serve this purpose
  because it exists in both tenants. Normal/missing course and hidden lesson
  checkpoints also assert API status. Next's 307/Location/no-store/404 chain
  remains owned by the Node artifact; the API observer does not observe Next
  response headers. The broad 1–59 check remains the visitor-budget ceiling;
  existing exact per-mode counts and checkpoints remain unchanged.
- The malformed-theme proof removes its redundant root-style-attribute check.
  Its existing scan still rejects four-color token declarations in any style
  element or inline style, and checks the complete compiled default palette. These HTML
  proofs inspect emitted CSS; they do not measure a browser's computed cascade.
  The earlier contrast numbers remain calculated palette ratios.
- Missing content without an admitted locale fails through `notFound()` rather
  than constructing `/null/status/not-found`; a defensive unit control covers
  the otherwise unreachable inconsistent request.
- Error Handling records the delivered mapping and links Steps 1–3. README
  inventory distinguishes the public pages from Studio/portal scaffolds. All
  five P6 rule headings use H3 under H2; the corpus reader admits H3/H4 with
  mixed-level planted controls so no rule falls out of the catalogue census.
- Failure containment uses each scenario's actual private code, retaining the
  common private-value/message-key canaries. The ICU parser suggestion does not
  require a dependency change: the lockfile uses 3.5.18 for both the direct
  structural parser and `use-intl`'s runtime `intl-messageformat` dependency.
  Build-time `icu-minify` uses 3.5.21 separately; existing actual formatting and
  production-build controls remain mandatory. No general parser-parity claim is
  introduced.

Validation passes 1,066 guarded frontend cases (1,006 web, 60 SDK), lint and
typecheck; 198 Release architecture cases; and all 20 real renderer/cleanup
integration cases, including all six production modes. All report zero failures
and skips/todos where applicable. The H4-only parser mutation fails the mixed
heading control; the restored parser passes the full architecture suite. Format
verification and diff checks pass. A local audit of the four changed Markdown
files checks 1,017 relative links and 353 fragments without a missing target.
No broad backend-suite rerun is claimed by these focused local checks.

Two fresh read-only remediation reviews approve `47f9c12..e871e88` on 2026-10-10:
GPT-6.1-sol at high effort, then GPT-6-astra at high effort. Both report no verified
actionable findings after standards, isolation, correctness, test-proof and
documentation checks. Neither reviewer claims a runtime-suite rerun. The
[PR #27 description](https://github.com/HodeTech/LearnStack/pull/27) owns the
exact-head CI evidence.

#### P02d-6 systematic-review decision package (2026-10-10)

**Maintainer response recorded — 2026-10-10.** The two external reports at
`ed6f81e` are review data, not new authority. Rechecks confirm guidance/status
drift, ordinary static source-fence gaps, missing dirty/clean response controls
and a second-bootstrap failure-classification gap. The exact-response-owner
design remains Proposed; the Open Graph and security maintenance parts below
are separately approved.

The existing G40 content-error HTTP 200/noindex and cursor-free canonical/noindex
policies are accepted choices, not implementation drift. Google's
[pagination guidance](https://developers.google.com/search/docs/specialty/ecommerce/pagination-and-incremental-page-loading)
recommends individual canonicals; its
[status guidance](https://developers.google.com/crawling/docs/troubleshooting/http-status-codes)
explains temporary-error and soft-404 tradeoffs. These recommendations justify
recording the tradeoff, not silently reopening the approved policies.

G10 explicitly defines editable base64url JSON seek cursors without confidentiality
or authentication. G40's opacity means the renderer does not interpret the token;
it does not hide anchor UUIDs from visitors. Scope/eligibility remains independently
enforced. Encryption, MAC rotation or server-side cursor state would need a new
decision and is not a correction to this PR.

##### Maintainer decisions (2026-10-10)

1. **Second bootstrap — design requested; not accepted.** The maintainer did not
   approve the neutral 500 proposal and requested a fuller response-owner design
   preserving exact 429/503. Revised proposed
   [ADR-0055](../decisions/0055-public-renderer-bootstrap-failures.md) selects one
   live middleware-owned read and an immutable native request-local snapshot for
   RSC. This removes the second-read failure and explicitly proposes two normal
   document calls / three followed-missing calls in place of three/five. It also
   proposes a closed Retry-After bound. These ownership, DTO handoff and accounting
   changes await approval; no dependent code or current-contract change is made.
   Independent design review rejected response-write interception as fragile;
   the revised concrete ALS singleton design has no remaining architecture
   finding, subject to actual Next 15.5.27 feasibility and isolation proofs.
2. **Open Graph locale — approved.** Emit `language_TERRITORY` only when the
   admitted tag has an explicit two-letter territory and no script/variant
   information would be silently discarded. Omit unrepresentable locale/alternate
   properties rather than inventing a territory. `tr-TR` becomes `tr_TR`;
   `en`, `tr`, `zh-Hant` and numeric-region tags are omitted. Document language,
   route and hreflang keep
   their exact BCP-47 tags. The [protocol](https://ogp.me/) makes these properties
   optional; omission can leave consumers applying their own default. This adds
   the bounded G40 projection and qualifies Standards 08's blanket rule.
3. **Security maintenance — approved.** Pin Next and eslint-config-next to
   15.5.27, disable the unused image optimizer, inspect/patch affected transitive
   versions within compatible bounds, and re-prove native ingress and actual
   production HTML/RSC.
   The fresh audit reports 46 findings (2 critical, 28 high, 15 moderate, 1 low),
   not 46 reachable public flaws. The AVIF
   [advisory](https://github.com/advisories/GHSA-2xp9-vwfh-vxw4) affects the current
   dependency version; attacker-controlled image input is not demonstrated.
   The [15.5.27 metadata](https://registry.npmjs.org/next/15.5.27) supplies a
   compatible 15.x backport. A Next bump alone does not clear transitive findings;
   record each remaining advisory's actual input path and maintenance disposition.

The approved ADR-0054 maintenance note is appended only after passing replacement
proofs. Its exact text is:

> **Amendment 7 — Next security maintenance (2026-10-10).** The current verified
> runtime pin is Next 15.5.27 with matching eslint-config-next. The signed raw
> target, full-URL RSC projection and equivalent redirect-query contract are
> unchanged. The unused image optimizer is disabled. P02d-6's remediation record
> owns the replacement native/production proofs and dependency triage; historical
> 15.5.18 observations remain unchanged.

No speculative native-addon/install-script policy is introduced. Existing exact
pins, frozen lockfile and real builds remain mandatory. Dependency patching does
not establish air-gapped packaging support.

##### Remediation plan and review boundaries

1. Correct current guidance/status carriers and skill examples. Strengthen the
   existing response assertions, source-retention/prefetch fences, production
   formatter-path tests and applicable accessibility controls. Align decimal
   limit parsing and closed cursor-error classification with existing contracts.
   These corrections need no new behavior decision.
2. Implement the approved G40 Open Graph projection. Separately prepare the fuller
   exact-bootstrap response-owner design; its implementation waits for approval.
   The eventual approved design must prove second-bootstrap faults through the
   actual production fixture without weakening provenance, read-only/RLS or the
   admitted content-error policy.
3. Apply the approved compatible security patches and close unused image
   optimization; re-run affected full/native/real-API suites, audit, formatting
   and local link/fragment checks. Record fresh exact-head PR CI. Append the
   approved maintenance note only after its replacement proofs pass.

Each group is committed and receives two fresh independent review rounds, with
verified findings corrected before the next group. All work stays on development.
The maintainer's earlier browser/VoiceOver report remains attributed to its tested
commit; later defensive/proof changes do not imply a new manual pass. P02d-7 and
Phase 06/11 retain their explicitly named browser, full accessibility and hardening
work. This package makes no new passing runtime or merge-readiness claim.

##### Remediation group 1 — existing-contract corrections

**Implemented; both review rounds and focused fix verification complete —
2026-10-10.** No approval-gated decision above is implemented by this group.
ADR-0055 remains Proposed.

The Step 1 status and current i18n/theme carriers now match delivery. Skills use
locale-bearing public folders, the shared resource loader, supported Next page
exports and explicit catalogue wiring. Future block/entitlement sketches are
labelled as targets. Phase 06 explicitly owns localized section names and full
Playwright/axe completion; Phase 11 explicitly owns frontend telemetry.

The production response predicates are shared with dirty/clean companions:
inline Flight escaping, entity-encoded attributes, opposite/protected values,
whole/partial palette emission, attribution and localized status metadata.
Emitted viewport checks reject zoom restrictions; they are not browser zoom proof.
The two unused ICU sentinels detect unchanged complete catalogues, not arbitrary
partial copy leakage. Static import fences supply the separate client boundary.
The bounded source analyzer now covers retained local factories/collections,
module mutable bindings, static/global writes and Next Link/router imports.
Admitted Studio/portal scaffolds are render roots and explicitly noindex.

Direct tests exercise the actual request configuration and public UI wrapper;
callsite census follows ordinary aliases/Promise tuples and requires a nonempty
production subject. Actual lint controls cover image, object, area and image-input
alt text. A prefixed style-breakout value tests the theme regex's start anchor.
Loading uses a status live region. Counts explicitly describe the current page.
Positive decimal limits saturate at the API bound without rejecting long valid
values; only exclusive owned-cursor errors with a supplied cursor select reset UI.

The following review claims do not justify changing accepted behavior:

- Cursor confidentiality, self-canonical pagination and content-error HTTP status
  are already decided; their tradeoffs and pending approval boundaries are above.
- Studio/portal bootstrap is the admitted scaffold contract, not an implemented
  authentication surface. Their full UI remains Phase 04/06/07.
- Missing error-label context is outside the current provider-wrapped public
  tree. No whole catalogue or hardcoded fallback is added to its Client Component.
  The assertion that `reset` can never recover is not reproduced; the supported
  framework reset and catalog recovery link remain.
- Security headers remain Phase 11's explicit scope. Arbitrary reflective/eval
  source analysis, a complete WCAG audit and browser-computed theme/zoom proof are
  not newly claimed by the strengthened bounded controls.
- ADR-0027's Decision explicitly authorizes the contract below it; the Context
  placement does not make that contract nonbinding. Accepted history is preserved.
- Packet 10's 19/3 standards count is historical; its timestamp is now explicit.
  Phase 06's P6 review/merge-pending statement is still true for this open PR.
- The review skill's conditional manual-evidence completion gate remains valid
  for future work. Branding has no authored-language field; inventing a language
  for the institution name would not establish correct pronunciation.

Validation at `cc626e8`: the guarded frontend runner passes 1,197 cases (1,137 web,
60 SDK), with zero skips/todos; workspace lint/typecheck pass. Five Release corpus tests
and seven ADR-workflow cases pass. The final focused Release integration run passes
20 cases: six real production Next/native HTTPS/API/PostgreSQL modes and fourteen
cleanup/log-control companions, with zero skips. No backend full-suite rerun or new
manual accessibility pass is inferred from these focused checks.

**Round 1 — fresh independent reviews of `ed6f81e..cc626e8`.** GPT-6-astra
(high) reviewed runtime/source-boundary behavior; GPT-6.1-sol (high) reviewed
proofs, guidance and documentation. Three findings were verified: shorthand
factory returns and destructured shared-storage aliases escaped the bounded
retention fence, and the attribution dirty control failed on its palette before
reaching attribution. Four new dirty cases fail before the retention fix; they
and two request-local clean cases pass afterward. The attribution control keeps
a valid palette and checks the specific failure in both entitlement directions.
Focused validation passes all 263 boundary cases and 36 response-helper cases;
web typecheck passes. Neither review claims a Docker/full-suite/manual rerun.

**Round 2 — fresh independent reviews of `ed6f81e..69b382a`.** GPT-6.1-sol
(high) reviewed runtime/source boundaries; GPT-6-astra (high) reviewed proofs and
documentation. The proof/documentation review approved. Two further bounded
retention gaps were verified: resolved local object member factories and
destructured closure captures. Seven additional dirty controls fail before the
fix, while seven request-local or pure-value controls remain clean. The fix follows
literal members and selected named/indexed binding values, without treating an
unrelated sibling collection as a captured value. All 277 boundary cases and web
typecheck pass. Fresh independent verification of `69b382a..bdb88bb` approved
without actionable findings and independently passed all 277 boundary cases.
The verifier confirmed the seven dirty cases escape the prior analyzer and the
selected-binding clean controls remain clean. This closes group 1.

##### Remediation group 2 — approved Open Graph locale projection

**Implemented; both independent reviews complete — 2026-10-10.** The approved
G40 addendum above
separates Open Graph's optional locale format from BCP-47 identity. Only canonical
language plus an explicit two-letter territory is projected; script, variant,
extension, bare-language and numeric-region tags are omitted. Eligible and enabled
alternates keep their exact URLs and hreflang while unrepresentable Open Graph
alternates are omitted. No additional API reads or territory inference is added.
Unit projections and actual production-document checks cover both conversion and
omission. All 61 metadata cases and web typecheck pass. On the concurrently
patched Next 15.5.27 runtime, the guarded frontend suite passes 1,229 cases
(1,169 web / 60 SDK), zero skips/todos. The actual API/PostgreSQL production-render
suite passes all 20 cases, zero skips; its first run exposed an overly narrow
fixture-alternate expectation, corrected before this passing run. The production
build passes. Two independent review rounds follow the implementation commit.
ADR-0055 remains Proposed and has no dependent code in this group.


**Round 1 — independent review of `ca03942..75c3e61`.** GPT-6.1-sol (high)
independently passed 61 metadata cases and web typecheck. It found no production
projection error, but verified that the runtime alternate loop accepted missing
and ineligible serialized alternates. The corrected shared predicate compares
complete current/alternate arrays against the representable eligible hreflang
set. Six committed dirty/clean controls cover missing, ineligible, duplicate,
malformed and invented territory output. All 103 metadata/response-helper cases
pass.

**Round 2 — fresh independent review of `ca03942..f16d937`.** GPT-6-astra
(high) approved without verified findings and independently passed the same
103 cases. The actual API/PostgreSQL fixture then passed all 20 cases, zero skips,
with the complete serialized-array predicate active. This closes group 2.

##### Remediation group 3 — approved security maintenance

**Implemented; replacement proofs and both independent review rounds pass —
2026-10-10.** The approved
paired Next/eslint-config-next 15.5.27 update disables unused image optimization.
Compatible lockfile patches cover PostCSS 8.5.23, sharp 0.35.5, js-yaml 4.3.2,
brace-expansion's existing major lines, nanoid 3.3.18, source-map-js 1.2.2,
browserslist 4.28.7, baseline-browser-mapping 2.11.0 and selector-parser 6.1.3.
Next pins an older PostCSS exactly, so only its dependency edge has a permanent
8.5.23 override. Redocly core 1.34.20 remains within the SDK's existing range and
updates its exact js-yaml dependency. Normal and frozen installation pass.

Comparable fresh audits distinguish deployment from development tooling:

| Scope | Before maintenance | After compatible patches |
|---|---|---|
| Production | 46 findings: 2 critical / 28 high / 15 moderate / 1 low | 2 findings: 1 high / 1 moderate |
| Full workspace | 59 findings: 5 critical / 32 high / 21 moderate / 1 low | 11 package/advisory records: 3 critical / 2 high / 6 moderate; 10 distinct GHSAs |

The original 46-finding report was production scope. The full baseline uses the
pre-maintenance committed manifests/lockfile in a disposable directory. Neither
comparison claims every advisory is a reachable public flaw or a clean audit.
Current residual input paths and dispositions are:

| Advisory | Current input and disposition |
|---|---|
| [GHSA-vfj7-8cjw-p6xm](https://github.com/micromatch/braces/issues/70) | Braces processes checked-in build/lint/watch globs through Tailwind/Next ESLint. No fixed release exists; retain visibility and reassess upstream or any request-supplied glob use. |
| [GHSA-rj75-hqrm-r3gf](https://github.com/postcss/postcss-selector-parser/security/advisories/GHSA-rj75-hqrm-r3gf) | Selector-parser 6.x processes checked-in CSS through Tailwind/PostCSS Nested. Fix 7.1.6 exceeds their declared range; no request-time selector input is identified. Track an upstream backport or separate compatible parent migration. |
| [GHSA-67mh-4wv8-2f99](https://github.com/evanw/esbuild/security/advisories/GHSA-67mh-4wv8-2f99) | Esbuild's serving feature through Vite/Vitest is not used by repository scripts. Proposed test-toolchain migration below removes the affected branch. |
| [GHSA-4w7w-66w2-5vf9](https://github.com/vitejs/vite/security/advisories/GHSA-4w7w-66w2-5vf9), [GHSA-v6wh-96g9-6wx3](https://github.com/vitejs/vite/security/advisories/GHSA-v6wh-96g9-6wx3), [GHSA-fx2h-pf6j-xcff](https://github.com/vitejs/vite/security/advisories/GHSA-fx2h-pf6j-xcff) | Vite development HTTP/editor/file-serving paths; repository scripts expose no Vite dev-server workflow. Windows-specific prerequisites are not a blanket exemption. Migration below replaces Vite 5. |
| [GHSA-5xrq-8626-4rwp](https://github.com/vitest-dev/vitest/security/advisories/GHSA-5xrq-8626-4rwp), [GHSA-82fw-gwwq-j7x9](https://github.com/vitest-dev/vitest/security/advisories/GHSA-82fw-gwwq-j7x9) | Vitest UI/API/mocker paths; scripts use run mode without UI/API/browser configuration or public mocker exports. These remain reported findings; migration below replaces both Vitest/mocker branches. |
| [GHSA-5gmw-xhrv-c9v3](https://github.com/tinylibs/tinypool/security/advisories/GHSA-5gmw-xhrv-c9v3), [GHSA-85c8-ppgw-ccpr](https://github.com/tinylibs/tinypool/security/advisories/GHSA-85c8-ppgw-ccpr) | Test-worker construction/options need prior prototype pollution and applicable attacker-controlled loading input; none is identified. Migration below removes tinypool rather than forcing an incompatible major into Vitest 2. |

The P02d-6 remediation owns this triage and the approval request below. The
maintainer owns any remaining backport/parent-migration decision; applicable
Security Standards patch deadlines remain unchanged. No test-toolchain major is
silently included in the already approved compatible-patch package.


Replacement validation passes on Next 15.5.27: frozen install; production build;
workspace lint/typecheck; guarded frontend **1,235 cases** (1,175 web / 60 SDK),
zero skips/todos; and all **20** actual API/PostgreSQL production-render cases,
zero skips. All nine pinned SWC emission cases run against the installed patch.
Native ingress passes TLS, GET/HEAD, query projection/redirects, DEBUG containment,
production upgrade closure and retained HMR. The image proof decodes a valid raw
PNG, sees disabled GET/HEAD 404, enables only a disposable copied configuration,
then decodes optimized 200 and proves the same disabled predicate rejects it.
All **19** native lifecycle/negative controls pass. No shared build is mutated.
The approved ADR-0054 maintenance note is appended after these replacement proofs;
historical 15.5.18 observations remain unchanged. Two independent review rounds
follow the implementation commit. No new manual browser/VoiceOver pass is claimed.

**Round 1 — fresh independent reviews of `f16d937..3f6f37f`.** GPT-6-astra
(high) reviewed dependency ranges, resolved versions, audit triage and ADR history;
GPT-6.1-sol (high) reviewed runtime proof controls, copied-build ownership and
cleanup. Both approved without verified actionable findings. These were read-only
reviews; syntax and diff checks pass, but neither claims a new runtime-suite run.
The full Release architecture suite also passes all 198 cases, zero skips; all
seven ADR-workflow cases pass. The second independent review round follows.

**Round 2 — fresh independent reviews of `f16d937..05a4313`.** GPT-6.1-sol
(high) independently checked dependency ranges, lockfile deltas and saved audit
totals; GPT-6-astra (high) checked native proof falsifiability, runtime config
loading, cleanup and preserved gates. Both approved without verified actionable
findings. They passed syntax/diff checks and inspected installed packages/source;
neither performed another build or runtime-suite run. A local audit of the 22
changed Markdown files checks 1,703 relative file targets and 567 fragments, with
inline/fenced examples excluded and no missing targets. Added prose respects
88 columns; CI's existing link gate still checks file existence only. This closes
the approved compatible-security group. ADR-0055 and the separate test-toolchain
migration remain pending approval and have no implementation in these commits.
Fresh exact-head PR CI remains the final validation step.

###### Test-toolchain migration — approved (2026-10-10)

The separately reviewable candidate pins Vitest **4.1.11** and Vite **6.4.3** in
both web and SDK, resolving esbuild **0.25.12** within Vite's range. Vitest 4 removes
tinypool; no tinypool major override is proposed. React plugin 4.7.0, jsdom 26.1.0,
Node 22.23.1, TypeScript 5.6.3 and React 19 remain. Narrow compatible resolution
pins may keep Vitest on Vite 6 and Vite on the selected esbuild patch; Vite 8 must
not appear as an accidental second tree.

The [official migration](https://v4.vitest.dev/guide/migration) changes pool and
mock-constructor behavior. The existing arrow `Intl.Locale` constructor mock
must become a constructible function/class and still prove its intended error.
Inspect mock cleanup; retain explicit Testing Library cleanup and `globals:false`.
The JSON reporter keeps the counters/status/file fields consumed by the guarded
runner; all its skip/todo/missing/omitted-file negative controls must still fail.

Before completion: frozen install, resolved graph/advisory audit, guarded full
frontend suite, lint, typecheck and production build; two independent review
rounds and verified fixes. Preserve package/control deadlines and all existing
proof gates. The maintainer separately approved this major test-toolchain upgrade
on 2026-10-10; implementation and fresh two-round review follow that approval.
It accepts no new architecture/ADR contract. The earlier compatible-security
group's pending-approval statement records its own commit-time boundary.

###### ADR-0055 draft review reconciliation (2026-10-10)

The maintainer's review is a request for draft corrections, not ADR acceptance.
The revised Proposed record names ADR-0054's unchanged Retry-After instruction and
limits the proposed qualification to renderer forwarding; API refusal selection
and metadata stay unchanged. It also names ADR-0054's deferred request-local reuse
and ADR-0027's admission loader, aligns the title with the existing filename, and
requires canonical Registered catalogue rules before dependent production code.

Next 15.5.27 native-to-middleware-to-RSC context propagation must be demonstrated
before acceptance. An isolated feasibility experiment is research, not permission
to ship the proposed runtime. Maintainer approval is still required after review.

The draft first appeared in mixed fix commit `cc626e8`, was revised in `bdb88bb`,
and received its decision-scoped commit in `ca03942`. That draft packaging is
recorded rather than rewritten. No Accepted ADR-0055 decision or dependent runtime
exists; acceptance-before-production implementation has not been bypassed.

ADR-0054's original Next 15.5.18 pin and retired test name were accurate at
acceptance. Preserve that history: its existing Amendment 7 identifies the
current 15.5.27 maintenance baseline. No accepted-body erratum is appropriate.

Two fresh independent reviews of `46bc8a4..2bf9d57` approve these draft corrections
without verified findings: GPT-6.1-sol (high), then GPT-6-astra (high). Both checked
the accepted sources, actual duplicate reads, historical pin/test and five changed
relative links/fragments. Neither claims a runtime feasibility proof. Seven
ADR-workflow tests pass. A local three-document audit resolves 442 relative
targets and their fragments; added prose and diff checks pass.

##### Remediation group 4 — approved test-toolchain migration

**Implementation recorded before independent review — 2026-10-10.** Both web
and SDK pin Vitest 4.1.11 and Vite 6.4.3. Narrow overrides keep Vitest on Vite 6
and Vite on compatible esbuild 0.25.12; the existing Next/PostCSS override remains.
The resolved graph removes Vitest 2, Vite 5, tinypool and vite-node, without
introducing Vite 7/8. Runtime dependencies and application behavior are unchanged.

The `Intl.Locale` mock uses a constructible function and proves that the intended
RangeError was thrown by identity, preventing an accidental constructor TypeError
from satisfying the fallback case. Configurations, explicit cleanup, globals,
runner/report validation, package/control deadlines and CI gates stay unchanged.

Frozen installation and 46 focused locale/runner cases pass. The guarded frontend
suite passes all 1,235 cases (1,175 web / 60 SDK), zero skips/todos; workspace lint,
typecheck and production build pass. The Release architecture suite passes 198
cases, zero skips. The full audit falls from 11 records / 10 distinct advisories
to two records / two advisories: zero critical, one high and one moderate. The
production audit retains the same two residuals already triaged in group 3;
neither audit is described as clean. Independent review rounds follow this commit.

**Round 1 — independent review of `2bf9d57..0761d84`.** GPT-6.1-sol (high)
reviews dependency compatibility and approves without verified findings. A fresh
resolved graph matches the saved graph and installed lock; surviving package
records and runtime dependency declarations are unchanged. GPT-6-astra (high)
reviews runtime/test proofs and also approves without findings. It independently
passes 110 focused locale, runner, caller, HMR and cleanup cases, verifies the
26-web/one-SDK file census, and checks the installed JSON reporter semantics.
Neither reviewer claims another full frontend/build/architecture execution.

**Round 2 — fresh independent review of `2bf9d57..a97f6ee`.** GPT-6-astra
(high) approves the dependency/documentation lens without verified findings,
independently matching the installed lock and fresh graph to the committed tree.
GPT-6.1-sol (high) approves runtime/proof integrity without findings. It passes
178 focused cases, including 68 shuffled DOM/request/mock cases, and validates
both reports with the actual repository validator. Its direct installed-spy
control distinguishes the intended RangeError from the old arrow's TypeError
and proves restoration of native locale behavior. No full-suite, build, install
or audit rerun is attributed to these reviewers. This closes group 4; fresh
exact-head CI follows. ADR-0055 remains Proposed and unimplemented.

##### ADR-0055 isolated feasibility observation (2026-10-10)

The root independently replays a disposable native HTTPS / production Next
15.5.27 fixture on Node 22.23.1. All 37 requests pass the propagation matrix:
eight simultaneous identical HTML, eight Flight, eight prefetch, eight host/locale
HTML/Flight combinations, HEAD and HTML/Flight missing/error cases. Native,
middleware and RSC compare actual holder/store references; request IDs are
observations, not context lookup keys. Unbound and duplicate-ALS controls each
refuse with 503, without snapshot publication or RSC consumers. Prefetch runs
middleware without dynamic RSC consumers in this synthetic fixture.

Valid HEAD exposes a lifecycle caveat: Next can resume an already-started
not-found component after response finish. The store is correctly disposed, its
snapshot cleared, and late access refused; Next logs the bounded fixture error.
ADR-0055 now explicitly requires disposal checks after awaits and neutral
production diagnostics. Response finish does not imply synchronous reclamation
of already captured local references or completion of all React work.

The experiment uses synthetic site publication. It proves neither real API/
provenance/RLS nor the proposed two/three-call accounting, cancellation, active
shutdown, keep-alive, HMR or browser navigation. Certificates, children and owned
build output are cleaned; a local reproducible source/results bundle is retained
for maintainer inspection. No experiment source enters the production tree.
This supports feasibility of the proposed same-process design only. ADR-0055
remains Proposed; dependent production implementation still requires approval
and all named production proofs.

**Independent feasibility review — two rounds.** GPT-6-astra (xhigh) checks
the fixture/driver, retained diagnostics and root replay. It verifies one wording
gap: the RSC context-error paragraph also covered expected post-finish HEAD work.
Commit `5b5f5a9` limits that paragraph to active requests; the reviewer rechecks
and approves the narrow feasibility claim without remaining findings. It does
not characterize observation-only summary fields as comprehensive mutation tests.
GPT-6.1-sol (high) then independently reviews `22a9775..5b5f5a9`, confirms the
matrix and active/late distinction, and approves without findings. Both reviews
inspect evidence rather than run another build. The root's separate replay passes
the same matrix and both negative controls. No review constitutes ADR acceptance.

##### ADR-0055 acceptance and implementation plan (2026-10-10)

**Accepted — 2026-10-10; implementation not yet delivered.** After the isolated
Next 15.5.27 feasibility experiment and two independent feasibility reviews, the
maintainer explicitly approves revised
[ADR-0055](../decisions/0055-public-renderer-bootstrap-failures.md) and dependent
implementation. The earlier draft/review/feasibility notes record their own
pending-approval boundaries; this dated acceptance supersedes those current-status
readings without rewriting them. The decision-only acceptance commit precedes
production changes. All work stays on development.

**Bounded G40 supersession.** The original G40 acceptance and its three/five-call
delivery remain historical. ADR-0055 replaces only duplicate bootstrap ownership,
request-local snapshot handoff and call accounting: middleware owns one live site
read and exact neutral 404/429/503; RSC re-verifies provenance and consumes the
immutable snapshot bound to that native request. A completed product document
costs two API calls, a fixed status/scaffold document one, and a followed missing
detail three. Actual HEAD/RSC/prefetch and Flight fallback counts require separate
production proof. Changes after bootstrap appear in the next request's site value;
content eligibility still runs live at the API.

Renderer Retry-After forwarding accepts only eligible parsed integer zero-to-sixty
delta-seconds on supported 429/503 cases. Ineligible values are omitted without
clamping or invention. API refusal selection/metadata, numeric quotas, content
HTTP 200/noindex, cursor SEO and the local missing-detail 307-to-404 chain remain
unchanged. ADR-0053/0027/0054 carry dated bounded supersession navigation.

Five canonical rules are Registered before code in the
[catalogue](../standards/21-architecture-tests-catalogue.md#p02d-6-request-local-bootstrap-admission-controls):
active native context, exact bootstrap HTTP responses, one immutable per-request
snapshot, native lifecycle completion and preserved production ingress boundaries.
Registered status is a commitment, not an executable or passing-test claim.

###### Implementation and review sequence

1. **Native context, facade and lifecycle units.** Add the sole versioned native
   ALS holder and server-only facade, binding verified host/peer/method/target to a
   fresh pending/ready/closed request store. Publish a bounded deeply immutable
   snapshot only once; cancel/clear exactly once on finish/close/shutdown. Unit
   controls cover missing/duplicate/mismatched context, abort, late publication,
   deep immutability and overlapping identical requests. Extend source fences only
   for this explicit owner/facade boundary. Commit, run fresh independent review
   round 1, verify/fix/commit findings, then use new reviewers for round 2 and
   verify/fix/commit its findings.
2. **Middleware publication and RSC consumption.** Middleware owns one bootstrap,
   bounded Retry-After and refusal/redirect terminal states; RSC/resource/i18n
   consumers use only the verified ready snapshot and native cancellation signal.
   Remove the second site read without changing locale authority or content-state
   policy. Add mapping and fault controls, update affected unit expectations, and
   commit. Complete the same two fresh independent review/fix rounds before Step 3.
3. **Actual production proof and closeout.** Exercise the shipped native launcher,
   pinned Next and real API/PostgreSQL as `learnstack_app`: first-wire refusals,
   zero Education/theme after failure, exact counts, identical/differing concurrent
   host/locale requests, HTML/RSC/prefetch/status/scaffolds, next-request freshness,
   keep-alive, abort/shutdown/HMR and valid HEAD late work with neutral diagnostics.
   Retain stock-launch, method/upgrade, exempt asset/health, no build bootstrap and
   private-value containment controls. Plant failures beside clean controls. Run
   affected full suites/build/lint/typecheck, catalogue and local link/fragment
   checks; commit and complete both fresh independent review/fix rounds. Only then
   mark proven rules Implemented, update delivery navigation and the existing PR,
   and verify fresh exact-head CI. Merge remains a maintainer action.

No step or review round is complete at acceptance. Missing propagation or lifecycle
proof cannot be replaced by another bootstrap, DTO headers, shared data or an
unapproved response owner; a required architectural change returns for a new
decision. P02d-7 and Phase 06/11 retain their named browser/accessibility/hardening
work. No new manual browser/VoiceOver result is implied by this approval.

###### ADR-0055 Step 1 — Native request context and lifecycle

**Implemented — 2026-10-10; both independent review rounds complete.** The native
launcher installs the sole versioned ALS holder before Next preparation and wraps
each admitted HTTP delegation in a fresh verified binding. Server-only consumers
retrieve the existing holder lazily; no bundled runtime constructs a replacement.
The holder copies only the closed public Site fields, bounds the copied snapshot
to the existing decoded-response ceiling and freezes its nested values. Refusal,
finish, close, abort and shutdown prevent later publication/use and release the
holder's snapshot reference and owned listeners. Request-input `close` and handler
promise resolution do not terminate the response lifetime.

Forty-eight focused unit cases cover state/binding conflicts, source mutation,
the inclusive UTF-8 byte ceiling, eight overlapping identical requests, escaped
handles, singleton descriptor checks and completed HEAD/late work. Source fences
refuse runtime imports of the native owner, facade fallback construction and
Client Component access, with dirty/clean controls. The shared transport limits
retain the existing ten-second and 8 MiB values.

The guarded frontend run passes **1,284** cases: **1,224 web and 60 SDK**, with zero
skips/todos. Typecheck, lint, production build and all **198 architecture cases**
pass. Native ingress verification and all **19 setup/cleanup controls** pass,
including production upgrades, development HMR, signal/IPC/EPIPE and owned
fixture cleanup. These unit/native-foundation results do not claim the complete actual
middleware-to-RSC handoff: Step 2 still owns publication/consumption and removal
of the second bootstrap; Step 3 owns actual product integration and lifecycle
proofs. The five new catalogue rules remain Registered until that proof closes.

**Review round 1 — `166ffb3`.** Fresh security/lifecycle and proof/structure
reviewers independently run all 326 focused cases. They verify two gaps: completed
handles retain native request/response objects through the disposal closure and
the default abort exception's lazy stack; ordinary `call`/`apply`/`bind` wrappers
also evade the new facade installer fence. The fixes clear the retained closure,
use a fixed primitive cancellation reason on both terminal paths and recognize
the three installer wrappers. Dirty/clean controls cover every wrapper; the
original proof reviewer verifies its finding is closed. An isolated GC regression
retains completed handles while checking that both native objects are released,
with live-object positive controls. Both original and closure-only mutants fail
at the collection assertion for ready/refused paths; the repaired 327-case
focused run, typecheck and lint pass. Actual middleware/RSC integration remains
pending in the next steps.

**Review round 2 — `af34e29`.** Fresh runtime and proof reviewers independently
run 367 and 446 focused cases. They verify that ordinary literal object/array
aliases evade the facade installer check and that the new signal handler leaves
a real development HMR connection/process alive. The source check now resolves
those static aliases, with dirty and clean controls. The launcher owns upgraded
sockets, cancels admission, awaits native/Next cleanup and bounds termination.
The native proof keeps HMR open through SIGTERM and SIGINT and requires exit code
zero and socket closure without fixture escalation. The original runtime reviewer
independently repeats both signals on real Next 15.5.27: both close and exit in
7 ms without SIGKILL, and closes the finding. The proof reviewer verifies its
minimal alias fix and all 278 boundary cases. Root revalidation passes 327 focused
cases, typecheck, lint, native ingress and all 19 setup/cleanup controls. Step 1
is complete; no second-bootstrap removal or full product proof is claimed here.


###### ADR-0055 Step 2 — Single bootstrap publication and checked consumers

**Implemented — 2026-10-10; both independent review rounds complete.** Middleware
requires the matching active native context before transport, performs the sole
site call and publishes only after successful route admission and response
construction. Redirects and refusals terminalize without a snapshot. Controlled
404/429/503 responses are bodyless for HEAD; the renderer forwards only eligible
integer Retry-After values from zero through sixty, without clamping.

The RSC loader independently verifies provenance, consumes that native snapshot
and constructs its content caller with native cancellation. It never calls
`getSite()`. Invalid provenance and exempt transport fallbacks retain their prior
neutral behavior; active context/configuration defects fail with fixed internal
diagnostics. Identity checks distinguish simultaneous identical bindings. Private
memoized loaders have checked outer wrappers, and resource/UI/i18n/document
consumers recheck after their awaits; completed cache hits and retained translators
cannot revive an ended native request.

The guarded frontend run passes **1,342** cases: **1,282 web and 60 SDK**, zero
skips/todos. Typecheck, lint, production build and all **198 architecture cases**
pass. The native ingress proof passes with one site call for status/scaffold
requests, real middleware/RSC consumption and retained TLS, method, optimizer,
DEBUG, HMR and signal controls. Focused tests include 88 middleware/admission and
18 actual-memo resource lifecycle cases. These are Step 2 results; Step 3 still
owns real-API/PostgreSQL product accounting, exact wire failures, broader lifecycle
and navigation proofs. The five catalogue rules remain Registered.

**Review round 1 — `578dcd8`.** Fresh runtime/security and proof/structure
reviewers approve without actionable findings. Independent focused replays pass
122 and 400 cases. They verify single bootstrap ownership, exact middleware
refusals, bodyless HEAD, eligible Retry-After bounds, immutable snapshot identity,
post-await and memo-hit lifetime checks, cancellation and retained source fences.
Neither review claims Step 3's actual API/PostgreSQL or navigation obligations.

**Review round 2 — `578dcd8`.** New runtime/security and proof/structure reviewers
approve without actionable findings. Independent focused replays pass 122 and
439 cases. They confirm real memo/runtime controls reject completed and
cross-store values before Education or result inspection, while preserving
request cancellation and the server-only boundary. Step 2 is complete; the
remaining production proof obligations stay with Step 3.

###### ADR-0055 Step 3 — Production admission proof and closeout

**Implemented — 2026-10-10; both independent review rounds complete.** The six
existing production modes consume the shipped native admission snapshot.
Release execution through real API/PostgreSQL passes all six cases, with zero
skips: transport **39**, foundation **33**, pagination **12**, presentation **30**,
product isolation **28** and product freshness **34** API calls. Exact checkpoint
assertions preserve the approved two/one/three-call contract, prior content-state
behavior and existing isolation, containment, build and stock-launch controls.

`PublicAdmissionRenderingTests` and `verify-public-admission.mjs` add one actual
production case with three separately budgeted phases: wire **32**, lifetime
**48** and browser/HMR **10** API calls. Healthy calls use the real API and
PostgreSQL as `learnstack_app`, with positive READ ONLY and non-bypass observations.
Test-owned boundary faults exercise first-wire GET/HEAD 404/429/503, bounded
Retry-After, malformed/oversized responses, transport failures, deadlines and
invalid site admission. Refusals load no Education or tenant theme. A wrong-status
mutant fails the wire oracle, followed by a passing clean control.

The lifetime phase forces overlapping identical HTML/RSC/prefetch requests and
mixed host/locale requests, proves distinct deeply frozen snapshots, and checks
zero retained contexts/snapshots after completion. Four concurrent prefetch lanes
make exactly eight API calls. Physical keep-alive reuse starts fresh admission;
client aborts cancel held bootstrap/content calls. A valid HEAD response finishes
before test-owned deferred work resumes; that work is refused before Education.
Shutdown cancels two held content requests, clears both snapshots and exits
without fixture escalation. Missing, duplicated and mismatched native contexts
fail closed without an API call.

Stock Brave 154.1.96.61 proves actual refused Flight navigation at 429 and 503
falls back to a new document request. The browser uses a disposable profile and
fixture certificate trust, without changing workstation trust. Development HMR
changes both source and live API data in the same process and proves fresh
admission with two site calls and no Education call. A bounded TLS WebSocket
watcher waits for the pinned Next compiler's `serverComponentChanges` message;
sync/client-only frames cannot release the source-change barrier. These are
narrow navigation/lifecycle proofs, not P02d-7's browser demo, Lighthouse or a new
manual accessibility audit.

Two observed gaps were corrected before this commit. A successful JSON-shaped
response with the wrong media type was accepted by the configured caller; it now
requires the existing success/Problem Details media types before SDK parsing,
with cancellation, reader-release and positive controls. The initial full Docker
run exposed an HMR source-watcher race despite a passing focused run. The compiler
completion barrier replaces the racing source-write/request sequence; the focused
production case and subsequent full Docker run pass without retry or sleeps.

Final local validation passes **2,946 backend cases**: **1,705 unit**, **198
architecture**, **4 contract**, **172 Docker-free integration** and **867 Docker
integration**, zero skips. The guarded frontend run passes **1,402 cases**:
**1,342 web and 60 SDK**, zero skips/todos. Release build has zero warnings/errors;
format, workspace lint/typecheck, production build, native ingress and all
**19** ingress cleanup controls pass. The five admission catalogue rules are
Implemented with executable owners and planted controls. Accepted ADRs retain
their original bodies and receive dated delivery navigation. The reviews and
subsequent CI investigation are recorded below.

**CI investigation and native completion proof.** The first Linux run on
`37ec51e` passes 866 Docker cases but fails the new production Fact during its
healthy wire control, after both real API calls return 200. An independent initial
console-only local attempt also fails without a retained substage; its captured
unchanged replay and five root replays pass. A diagnostic commit splits the closed
wire-control stages without printing provider or response data.

The proof had inferred native completion from client response EOF. Node's socket
write callback emits server `finish`; client EOF and a later IPC message do not
establish that event has run. A test-only independent finish/close observer now
registers after production disposal listeners and waits for actual native terminal
events before asserting zero counts. Held-active observations stay immediate.
Six dirty/clean cases prove client/delegation completion cannot release the barrier
and missing runtime cleanup still fails after the barrier resolves. No sleep,
count-poll, production lifetime change or API retry is added. The actual production
Fact and guarded 1,402-case frontend run pass locally with this proof correction.
The diagnostic-only `9f785fc` then passes all six required jobs, including all
867 Docker cases and the new production Fact in
[run 38054051927](https://github.com/HodeTech/LearnStack/actions/runs/38054051927).
The exact first Linux substage remains unknown; this passing replay does not
establish the original cause. The completion correction addresses an independently
verified ordering assumption, not a claimed reconstruction of that failure.

**Review round 1 — `7a2b4b2..37ec51e`.** GPT-6-astra and GPT-6.1-sol, both at
xhigh effort, review runtime/security and production proof/documentation
respectively. Both approve without actionable findings and independently pass
94 focused cases. They do not claim another full production-suite run.

**Review round 2 — `7a2b4b2..37ec51e`.** Fresh GPT-6-astra and GPT-6.1-sol
reviewers at xhigh effort check runtime/lifecycle and proof/documentation. Static
runtime checks and 94 focused cases pass, but the runtime review's first
console-only production attempt fails as recorded above; its retained unchanged
replay passes. The proof reviewer records the early catalogue-status promotion:
`37ec51e` marked five rules Implemented after passing execution but before both
review rounds, contrary to the approved sequence. This process deviation is
preserved here rather than rewriting the plan. The later completed reviews and
fix verification support the current Implemented status.

**Focused fix review round 1 — `9f785fc..b8d5199`.** Fresh GPT-6-astra at xhigh
effort verifies native observer ordering, independence from runtime counts and
unchanged active observations. It finds a real proof gap: one microtask wait lets
an already-resolved empty-pending mutant pass all six controls. Independent
verification reproduces that behavior; `100534f` crosses a deterministic
`setImmediate` turn before both pending assertions. The actual Vitest mutant now
fails three cases; restored production code passes all six. No wall-clock sleep or
runtime change is added.

**Focused fix review round 2 — `9f785fc..100534f`.** Fresh GPT-6.1-sol at xhigh
effort approves without actionable findings, independently passes all six cases
and rejects the empty-pending mutant at all three corrected assertions. It
verifies native completion cannot conceal missing runtime cleanup and that the
existing ten-second IPC bound and API accounting remain unchanged. The guarded
full frontend replay passes 1,402 cases, zero skips/todos.

The repeated optimizer-child cleanup comment is not applicable: `start()`
registers the child with its owner immediately, and failure unwinds through the
existing top-level `finally`/`owner.dispose()`. The implementation agent and an
independent proof reviewer verify that path; no redundant local cleanup is added.
The original manual browser/VoiceOver evidence retains its tested scope and commit.

Closeout verification passes 198 architecture cases and seven ADR-workflow cases,
with no failures or skips. A local audit of the 37 changed Markdown files checks
2,392 relative links and 735 fragments without missing targets; inline/fenced
examples are excluded. Added prose respects 88 columns. CI's link gate still
checks file existence only.

**Subsequent Linux capture failure — `b8d5199`.** Native completion checks pass,
but the admission Fact rejects an empty real API log capture; 866 other Docker
cases and five required jobs pass. The pinned Serilog hosting implementation uses
a process-global logger by default: another concurrent API host can replace it,
and disposing that host can close it. This verified shared-state mechanism does
not establish the exact historical interleaving. The public-read xUnit collection
now explicitly opts out of parallel scheduling with other collections, preserving
production logger configuration, enrichment, sink disposal and the nonempty-log
assertion. Forced overlapping HTTP requests inside each proof remain concurrent.
No logger replacement, synthetic event, polling or weakened assertion is added.
**Log-capture fix review round 1 — `ef05ab0`.** Fresh GPT-6-astra at xhigh
effort approves without actionable findings. It verifies the pinned xUnit runner
awaits parallel collections and their fixture disposal before starting an isolated
collection. It independently confirms the pinned Serilog global-logger mechanism,
sequential API phase ownership and the unchanged forced HTTP overlap gates.

**Log-capture fix review round 2 — `ef05ab0`.** Fresh GPT-6.1-sol at xhigh
effort independently approves without actionable findings. It checks collection
and child-host lifetimes, awaited/idempotent fixture disposal, real-log assertions
and the four-arrival request barriers. Neither reviewer starts another Docker or
browser run or claims to reconstruct the earlier Linux interleaving.

**Fix verification.** The full local Release Docker suite passes **867 cases**,
including the admission Fact and all six retained production modes, with zero
failures or skips. Release build has zero warnings/errors; format verification,
all **198 architecture cases**, seven ADR-workflow cases and the local link audit
pass. The previous guarded frontend execution remains **1,402 cases**, zero
skips/todos; this scheduling fix changes no frontend source. Two overlong prose
lines in this P6 delivery record are wrapped without changing their meaning.

All three implementation steps and both subsequent focused fixes have completed
their two independent review rounds. The [PR #27 description](https://github.com/HodeTech/LearnStack/pull/27)
owns final exact-head CI after this documentation commit. Maintainer review and
merge remain pending. P02d-7 retains the full browser demo and Lighthouse;
Phase 06/11 retain their named broader accessibility and production-hardening work.

**Latest external review follow-up — 2026-10-10.** The review against `d1cc011`
reports no Blocker or Major. Its fixed-port observation is valid: native listening,
Next's port option and redirect authority now share `PUBLIC_HTTPS_PORT`, preserving
the Accepted local HTTPS port 3000. Middleware explicitly narrows the verified
envelope instead of using a non-null assertion.

The admission proof now also holds four scaffold requests at one real API barrier:
two hosts, each requesting `/studio` and `/portal`. Distinct seed palettes provide
positive controls; each HTML response contains its own palette and excludes the
other tenant's palette and name. Four separate immutable snapshots are released
after native completion. The driver asserts exactly four site calls and no
Education call for this checkpoint. The lifetime phase now uses **52** API calls;
wire **32** and browser/HMR **10** remain unchanged. The earlier **48**-call receipt
above describes the previous proof revision.

The suggested silent/shared logger replacement is not applied. The unsupported
renderer warning is intentionally bounded to one event per request, contains only
fixed state/count fields and has a passing privacy/once-only test. Phase 11 owns
the broader frontend observability contract; removing this signal would weaken
the current diagnostic contract.

**Follow-up review round 1 — `1496269`.** Fresh GPT-6-astra at xhigh effort
approves without actionable findings. It verifies shared port authority, explicit
envelope narrowing, real overlap, independent seed palettes, snapshot identity,
native completion, exact accounting and the bounded scaffold coverage claim.

**Follow-up review round 2 — `1496269`.** Fresh GPT-6.1-sol at xhigh effort
independently approves without actionable findings. It reconciles the **32/52/10**
phase totals and checks provenance, cleanup, falsifiability and documentation.
Both rounds are read-only source reviews; runtime execution belongs to the parent.

Focused local verification passes: the real admission Fact **1/1**, retained
rendering **20/20** (including all six production modes), architecture **198/198**,
guarded frontend **1,402/1,402**, typecheck, native compilation, lint and changed
C# format verification. TRX counters confirm zero failures/skips; the frontend
runner also refuses skips/todos. The PR description owns final exact-head CI
after this review-record commit. Maintainer review and merge remain pending.

**Ingress instrumentation follow-up — `b073f35`.** The shared-port refactor also
changes a literal launcher anchor used by the separate native ingress fixture.
Its first [CI execution](https://github.com/HodeTech/LearnStack/actions/runs/38060818584)
rejects missing automatic-upgrade observations. The disposable copy now matches
the shared constant and checks that both instrumentation anchors occur exactly
once before rewriting. The fix changes no production code or assertion strength.
Local production build, native TLS/ingress proof, all **19** ingress cleanup/
negative controls and lint pass.

**Instrumentation fix review round 1 — `7ea14c5`.** Fresh GPT-6-astra at high
effort approves without actionable findings. In-memory checks reject missing and
duplicate anchors and confirm that the transformed launcher parses. It verifies
observer installation before Next's first-request upgrade registration, unchanged
upgrade assertions and compatible cleanup controls.

**Instrumentation fix review round 2 — `7ea14c5`.** Fresh GPT-6.1-sol at high
effort independently approves without actionable findings. It checks actual
transformation syntax, one shared-constant listener, adjacent source anchors,
registration order and cleanup. Both rounds are read-only and start no runtime
services. The PR description owns final exact-head CI after this record commit.

### P02d-6 merge and closeout (2026-10-10)

The maintainer merged [PR #27](https://github.com/HodeTech/LearnStack/pull/27) at
2026-10-10 17:07:50 UTC. GitHub records final PR head
`46ed971bf976514fb451a162cc9450d147ed0441` and merge commit
`378346126949c90bb286b5217c24513fc2044f06`. The merge's second parent is that
head, and both commits have the identical tree. Development is fast-forwarded to
the verified merge without changing branches or rewriting history. The final
head includes the testing-standard correction marking both ADR-0055 Step 3
review rounds complete.

- All six required checks pass on the
  [final PR head](https://github.com/HodeTech/LearnStack/actions/runs/38070332107)
  and the [merge commit](https://github.com/HodeTech/LearnStack/actions/runs/38070479860).
  Independently downloaded merge-run TRX confirms **2,946 passed**, zero failures
  or skips: 1,705 unit, 198 architecture, four contract, 172 Docker-free
  integration and 867 Docker integration cases.
- Merge-run frontend logs confirm **1,402 passed** (1,342 web, 60 SDK), successful
  typecheck, lint, production build and SDK drift verification. Native production
  ingress verification and all **19** setup/cleanup and negative controls pass.
  Release builds report zero warnings and errors. These are merge-run receipts;
  the documentation closeout does not rerun the full runtime suites.
- Read-only verification confirms `strict: true` and the same six required Actions
  contexts, including OpenAPI diff and Docker integration. No protection setting
  changes. Lighthouse remains disabled with activation owned by P02d-7.

**P02d-6 is closed.** Localized catalog, course/outline and ordered plain-text
lesson pages, safe four-color themes, exact-locale metadata, scoped pagination
and controlled public states are delivered. ADR-0055 adds middleware-owned single
bootstrap admission, immutable request-local reuse and production lifecycle/
isolation proofs. All implementation steps and focused fixes retain their
recorded independent review rounds. The
[packet closeout](#p02d-6-packet-closeout-2026-10-10) owns scoped keyboard, focus,
reflow and contrast observations and the maintainer's VoiceOver smoke; this
merge adds no new manual observation or full WCAG-conformance claim.

**Next: P02d-7 — Demo, full-stack CI and exit.** Its decision pass and
implementation have not started. Resolve the remaining G20/G33/G38/G44/G45 parts
against the delivered renderer before harness implementation:

- Define `make demo` and stop/re-run ownership on a clean checkout, environment
  projection, readiness, non-destructive seeding and the printed host/locale URLs
  (G45). Local use and CI reuse the selected entrypoint.
- Select Lighthouse's tools, both-host/locale URL set, assertions, budgets,
  aggregation and artifacts; activate its currently disabled job and reconcile
  standards/required-check carriers with the chosen enforcement (G44).
- Complete the shared full-stack/browser demo and trusted-hop CI evidence
  (G33/G38), including anonymous catalog-to-lesson flows with Keycloak stopped.
- Implement and prove G20's remaining production/seed and frontend genericity
  fences in required checks, preserving tenant-data-driven differences.
- Reconcile every exit criterion and register row with its evidence and record
  the Education/seed preservation obligations and premises `P02b-0` re-verifies.
  [Phase 02b](phase-02b-events-auth.md) follows only after the Phase 02d exit
  decision, not this packet merge.

Phase 06 retains full Studio/portal, authored composition and Playwright/axe;
Phase 11 retains broader production hardening and telemetry. Course Marketplace
commerce is not delivered by this closeout. Earlier dated delivery records,
including their pre-merge pending statements, remain historical and unchanged.

**Documentation-only closeout validation.** All **198** local Release architecture
cases and seven ADR lifecycle/workflow controls pass, with zero failures/skips.
The six changed Markdown files resolve **873** relative links and **371** fragments
with zero errors in the local audit. Added prose wrapping and diff checks pass.
Earlier dated phase records are byte-preserved; no Accepted ADR or production
code changes.

## P02d-7 — Demo, full-stack CI and exit

### P02d-7 decision package (2026-10-10)

**Accepted — 2026-10-11; package and revised ADR-0056 approved.** Read-only
inspection starts from `f857437` on `development`, after the P6 merge closeout.
The maintainer approved the lifecycle, genericity, four-step review plan and
bounded seventh-check activation after real success. No implementation, passing
P7 test, enforcement delivery or phase exit is claimed. The user requires new
ADRs to be reviewed and approved before dependent work. Accepted
[ADR-0056](../decisions/0056-public-demo-browser-and-lighthouse-enforcement.md)
owns the new browser/Lighthouse measurement and required-check contract.

P7 completes the walking skeleton's operational proof: a reviewer starts the
actual seeded sites with one command; the same lifecycle supplies CI's browser
and audit job; genericity and every phase-exit obligation receive named evidence.
Education writes, public DTOs, tenant authority, native ingress, anonymous quotas,
and request-local admission retain their Accepted contracts.

#### P02d-7 inspection and document actions

Three independent read-only review lenses covered demo/orchestration,
browser/Lighthouse, and genericity/exit. Source inspection was cross-checked with
the current branch and official tool metadata. This preparation executes no
runtime suite and does not reuse P6's counts as P7 results.

| Area reviewed | Current premises and required action |
|---|---|
| README, CLAUDE, platform vision, MVP scope, roadmap index, principles and glossary | P1–P6 are merged; P7 is the remaining exit packet. Preserve the white-label/data-driven boundary and Proposed marketplace status. Keep current packet surfaces synchronized without rewriting dated delivery history. |
| ADR-0018/0035/0036/0040/0043/0048/0050/0051/0052/0053/0054/0055 and ADR-0027 | Existing content, RLS, read-only, exact-pin, TLS/provenance, quota and one-bootstrap contracts survive. G20/G45 implementation detail needs no ADR; new measurement/tool enforcement is Proposed ADR-0056. |
| Standards 03/06/07/08/11/12/13/14/15/16/20/21, CI and CONTRIBUTING | The Lighthouse job is still disabled. Qualify Testing 06's “None exist / first browser flow Phase 06” when P7's narrow flow ships; retain full Phase 06 Playwright/axe ownership. Update Performance status only with hard enforcement and distinguish CI targets from timeouts. |
| Makefile, compose dev/e2e, seed/public-local launchers, ingress/configuration and public server | No demo target exists. `make seed` runs migrations before the script's readiness gate. Wait for PostgreSQL before migration; parameterize one compose selection through start/wait/migrate/seed/stop. Health endpoints alone cannot prove seeded readiness or listener ownership. |
| SeedData, source-reader/name guards, module-reference guards and frontend source census | The existing source proof reads all strings, not just identities. Derive a typed identity vocabulary for G20 and canonical built-ins; add the whole-production Seeder-reference fence. Existing HTML/Flight schema-replacement proof supplies a behavioral companion, rather than a duplicate production mutation surface. |
| Production renderer fixture, admission browser, cleanup helpers, seed/local-dev/integration/test skills, compose and web READMEs | Reuse owned-process/TLS/private-output controls. Correct stale seeder activation, P6 merge and raw API restart guidance. Narrow old “CI uses Testcontainers only” and human-only browser statements when the compose-backed proof ships. |
| Phase 02b/05/06/11 and module handoff documents | Fix Phase 02b's mutable plain-HTTP/open-G32 premise; carry native HTTPS, visitor/peer accounting and single-bootstrap inputs into P02b-0 re-verification. Preserve Phase 05 migration obligations and Phase 06/11 ownership. |

No accepted ADR body or prior delivery record is rewritten. Current contradictions
outside the new package receive bounded carrier fixes in the owning step; the exit
matrix verifies they are all resolved.

**Preparation review — 2026-10-10.** Independent GPT-6-astra and GPT-6.1-sol
reviews verified the proposal against current contracts/source. Corrections
cover the package heading, complete seeded-ID vocabulary, actual Lighthouse
audit/navigation set and live status carriers. The maintainer's external review
additionally exposed the ADR lifecycle parser incompatibility; it was reproduced
and fixed. Metric/batch wording, tool pin/license placement, decimal gzip units
and the exact Testing 06 carrier action are now explicit. Earlier dated
“not started” statements describe their record time; this package and the top
packet table own current state, and Step 4 verifies that navigation at closeout.

Documentation preparation passes all 198 Release architecture cases and all
seven ADR lifecycle/workflow controls, with zero failures/skips. These results
validate the draft corpus, not the unimplemented browser/Lighthouse harness.

**Additional maintainer review — 2026-10-11.** The review identified ambiguity
between per-audit timeouts and the total deadline. Revised ADR-0056 specifies
one serial worker, nested deadlines and the 244-second quiet-window cost; the
first complete run must prove feasibility before activation, with no silent
parallelism or reduced census. It makes the potentially stricter gzip window,
origin coverage and exclusions explicit, names the four budgeted audits and
keeps FCP/Speed Index unasserted. The source-verified category name replaces the
imprecise agentic term. Tool ownership, behavioral script-rule ownership and
G33's retained human evidence are explicit. The revised ADR remains Proposed.

An independent GPT-6-astra review found no unresolved revision finding.
Read-only upstream verification confirmed the audit/filter selection and the
pinned DevTools source for slow-4G constants. Revision validation passes 198
Release architecture cases, seven ADR lifecycle/workflow controls and the
changed-document relative-link/anchor, wrapping and diff checks. No browser run
or timing feasibility is claimed by these preparation checks.

#### P02d-7 accepted answers

The maintainer approved the package on 2026-10-11. G20 detail, G33 CI topology,
G38's narrow browser-job scope and G45 lifecycle are Accepted planning decisions;
their implementation and proof remain pending. The maintainer subsequently
approved revised ADR-0056 on the same date, closing G44's measurement decision.
The seventh-check activation is authorized only after the approved ADR's complete
real PR run succeeds. The combined decision commit precedes implementation.

| Gate | Approved scope / remaining decision authority |
|---|---|
| G20(b/c/d) | Typed exact identity matching, bounded canonical built-ins/exemptions and a whole-production Seeder-reference ban; the section below owns the detail. Implement the existing Registered rule and adopt the real schema-replacement companion. |
| G33 CI part | Same workstation-style loopback API/native TLS topology in the CI runner, one per-run secret, real migrated database and `learnstack_app`. Disposable CI certificates/resolver mapping; no system-trust mutation or additional trusted transport. |
| G38(c) job | A narrow managed-Chromium library script follows real catalog → course → lesson links in the three seed host/locale lanes. Existing product HTML/Flight proofs remain required; full Playwright/axe stays Phase 06. |
| G44 remaining detail | Accepted ADR-0056 owns direct pinned Lighthouse/Chromium, nine URLs × three runs, actual-page admission, hard gzip/CLS/accessibility checks, advisory timings, serial real-window pacing and artifacts. Its revised measurement contract was approved on 2026-10-11. Bounded activation of `public demo + lighthouse` as the seventh required check is approved only after the complete approved proof succeeds on a real PR, with protection read-back. |
| G45 | Foreground production-demo supervisor, shared full seed gate, persistent non-destructive local data and owned-process cleanup; the lifecycle below owns the details. |

#### P02d-7 demo lifecycle

`make demo` is the local entrypoint; `make demo DEMO_MODE=ci` selects isolated
automation through the same lifecycle. The mode selects environment/compose
ownership and the proof driver, not different product code or a bypass. Make
arguments are validated; startup, readiness, migration, seeding and cleanup share
one explicit compose project/file/env selection.

1. Check installed prerequisites and refuse concurrent ownership or occupied
   API/web listeners; never adopt or kill a foreign process. Restore locked
   frontend, .NET and tool dependencies without changing Git hook configuration.
   Prepare missing private root configuration from `.env.example`, validate
   required values and refuse stale/disagreeing projections rather than overwrite
   user values. Generate a missing hop secret through the existing preparation.
2. Local hosts/CA/leaf preparation remains the explicit manual G32 prerequisite.
   Verify host resolution, certificate/key match, validity and trust with
   actionable sanitized errors before infrastructure starts. Never edit system
   hosts or install a root CA automatically. CI creates private disposable
   configuration/certificates in its owned checkout and uses ADR-0056's scoped
   browser trust and independent Node TLS checks.
3. Start the existing seven-service default compose stack, wait for PostgreSQL
   before any migration, then retain the existing full default-service and both
   Keycloak-realm seed gate. Apply all current migration chains with the migration
   role and seed via the normal contextual request path as `learnstack_app`.
   No destructive reset, overwrite or direct seed SQL is added.
4. Build the web production bundle and API, then launch the real API and mandatory
   native HTTPS web listener in owned process groups. Use existing paired private
   configuration, role validation and child-environment boundaries. These are
   production builds with the accepted local Development deployment mode;
   stock `next start` is not substituted for native ingress.
5. Declare ready only after owned-child readiness, verified TLS and positive
   host/locale/content checks. Print catalog/course/lesson URLs for English
   `en` and Yoga `tr-TR`/`en`, derived from the seed declaration. Static
   `/healthz` or `/api/healthz` success alone is insufficient.
6. Ctrl+C, parent/control closure, child failure or a startup error stops and
   joins owned API/web process trees. Local compose/data remains available;
   `make down` is the explicit infrastructure stop. Re-run after shutdown
   converges without reset; simultaneous invocations fail clearly.
7. CI uses the existing e2e overlay and a unique owned project. Stop its Keycloak
   after successful seeding and assert the stopped state before fresh browser
   navigation. On every exit, remove only that invocation's processes, project
   and private files. Confirm child exit before deleting sensitive trees; fail
   and preserve private diagnostics if cleanup cannot be established.

Browser/Lighthouse uses actual limiter windows under ADR-0056. No API restart,
quota inflation, forged visitor or 429 retry is part of a successful proof.
API status/call observation must have a nonempty positive control; missing or
truncated observation fails. Reuse structured host completion events where
possible, retaining only sanitized counters/receipts in artifacts.

#### P02d-7 genericity enforcement

The typed `SeedData` declaration remains the single identity source. Build the
G20 vocabulary from every declared entity ID, including built-in row and theme
IDs; tenant/organization slugs, hosts and display names; course/lesson keys and
localized slugs/titles; customization keys, item keys, schema property names and
localized definition/field/band labels. Do not classify
ordinary body prose, locale codes, publication/access values or JSON-schema
keywords as tenant identities simply because the existing reader sees them.

Compare complete decoded string values ordinally; compare GUID literals by
their parsed GUID value. This is an exact-literal fence, not a claim to prove
every possible dynamically assembled branch. Read C# literals, frontend TS/JS
literals and JSON values with syntax-aware readers, excluding comments. Fail on
unreadable/empty identity sources or production subjects. Preserve the frontend
census's refusal of unsupported source forms; no silent extension/import gap.

Canonical platform built-in keys/labels come from their owning declarations/registries,
not copied into an expanding arbitrary allow-list. Preserve the accepted G20(c)
bounded exemptions: `SeedData`, explicitly identified seed/development launch
data, operational documentation and test-owned fixtures/assertions. Production
middleware, ingress, transport, renderer and message catalogues are subjects.
No blanket `scripts`, configuration or catalogue directory exemption is added.

Additionally forbid every non-test production project except the Seeder itself
from referencing `LearnStack.Tools.Seeder`, both through compiled references and
restore-resolved project references. This catches identity comparisons through
`SeedData` symbols that contain no local literal.

Planted failing controls cover backend identity/customization-ID comparisons,
renderer/transport branches, a shared frontend helper, message JSON values and
Seeder references.
Passing controls cover comments, canonical platform built-ins and specifically
allowed development/test data. Prove the actual production census is nonempty.
Re-run the existing real API/PostgreSQL/HTML/Flight presentation successor-revision
proof as the behavioral companion, with a field-assertion failure control; retain
original-pin and unaffected-tenant checks. The new browser proof observes
distinct ordered authored fields, without adding a production mutation endpoint.

#### P02d-7 implementation plan

Work remains on `development`; no alternate branch or worktree. Each step lands
its implementation commit, then two fresh independent agent review rounds.
Verify findings against current code, fix valid findings and commit before the
next round/step. A new architectural decision pauses only its dependent work.

| Step | Delivery | Validation before completion |
|---|---|---|
| 1 — Shared demo lifecycle | Compose selection, pre-migration wait, locked restoration, private configuration, foreground ownership, production build/start, seeded readiness, non-destructive repeat/stop and local guidance. | Clean-checkout preparation with the documented manual TLS prerequisite; repeat convergence; stale/missing configuration; port collision/foreign listener; child failure, signal/parent closure and owned cleanup controls. |
| 2 — Genericity and exit foundations | Existing G20 production literal rule, whole-production Seeder-reference fence, canonical built-ins/exemptions, behavioral companion and preliminary criterion/evidence inventory. | Nonempty backend/frontend/JSON census; planted dirty/clean controls; architecture/unit/contract and relevant integration tests; existing exact-pin/schema-replacement product proof. |
| 3 — Browser and audit proof | Pinned tools/browser; seed-derived nine-URL manifest; Keycloak-stopped linked navigation; computed themes/ordered fields; actual audited-page admission, gzip collector, metrics/census validator, rate pacing and sanitized artifacts. | Real same-build/same-database run; no API 429; positive call observer; wrong/error page, invalid report, over-budget and missing-source controls; TLS and failure cleanup; all 27 report pairs. |
| 4 — Required CI and phase exit | Always-running seventh job, bounded protection activation after real success, standards/carrier alignment, complete evidence matrix and P02b-0/Phase 05 handoff. | All backend/frontend suites with zero skips; real CI and live protection read-back; links/anchors/wrapping; every gate/deliverable/criterion reconciled; zero 02d Registered/Awaiting rows; two review rounds and explicit exit verdict. |

The new job does not replace any existing required suite or fixture. Preserve
the accepted P6 keyboard/focus/reflow/contrast and maintainer VoiceOver record,
with its actual limits; record new automated observations separately. Promoting
Performance to Active is conditional on delivering the proposed hard checks,
not on accepting this package. Its index/header/count change lands with the
enforcer; Accessibility is already Active for its delivered public-page scope.

#### P02d-7 approval and exit boundary

The complete lifecycle and G20/G33/G38/G45 answers, four-step review plan and
bounded seventh-check activation were approved on 2026-10-11. Revised ADR-0056's
tools/measurement/browser contract was subsequently approved on the same date.
Its register dependencies are recorded in the combined decision commit before
implementation.
No other unresolved product choice was found in this inspection. Unexpected
architectural changes need a separate reviewable decision; implementation details
inside the approved package proceed without further approval.

At closeout, map every Deliverable and Completion Criterion to current named
evidence, including carried-forward evidence and its limits. Reconcile current
Phase 02b native-HTTPS/quotas/single-bootstrap premises without accepting its
auth/BFF gates. Record independent Course/Lesson roots, stable IDs/URLs, ordering,
scope, translated bodies, exact pins and access policy as Phase 05 preservation
obligations; re-seeding is not a migration.

Do not declare Phase 02d exited merely because P7 code or its PR is complete.
The exit verdict must state which obligations passed, identify evidence/CI/protection
receipts and leave no unresolved 02d-owned gate or Registered/Awaiting rule. The
maintainer's PR review and merge remain separate from implementation completion.

### P02d-7 Step 1 — shared demo lifecycle (2026-10-11)

**Complete — both review rounds and their fixes verified.**
The foreground `make demo` supervisor shares explicit Compose selection,
PostgreSQL-before-migration readiness, full seven-service/both-realm admission,
contextual seeding, production builds and owned process cleanup. Local data is
retained; CI uses a unique project, disposable private configuration and the e2e
overlay. Its browser/audit driver remains Step 3 work and is not claimed here.

`DemoManifest.Create` derives all three host/locale lanes, nine URLs, ordered field
expectations, palettes and excluded course titles from `SeedData`. Its export
requires no database. Configuration validation uses Npgsql's real parser to
refuse stale role/password/database/host/port combinations before infrastructure.
The runtime environment helper is shared with the existing paired API launcher;
migration and worker credentials are excluded. TLS prerequisites do not modify
hosts or system trust. The exclusive demo lock never kills a recorded foreign PID.

**Initial verification.** The Release Seeder build has zero warnings/errors.
All 1,705 unit, 198 architecture, four contract, nine focused integration and
42 Seeder integration cases pass with zero failures/skips. Focused integration
includes eight manifest/configuration cases and the real-API/PostgreSQL local
launcher proof without a PlatformAdmin credential. The guarded frontend runner
passes 1,413 cases without skips/todos; the final 13-case focused suite also
covers structured API-listener readiness and an invocation-owned native cache.
Lint and typecheck pass. Node controls cover
explicit selection, readiness refusal, runtime credential isolation, foreign
listeners, bounded bodies, certificate mismatch, real process timeout/cancellation
and owned trees. These initial checks precede the complete startup and review
evidence below.

**Complete isolated lifecycle proof.** An owned temporary checkout copy starts
all seven services with the e2e overlay, passes both realm gates, applies every
migration chain and seeds through the application role. Its owned production
API/native renderer passes positive lesson checks in all three seed host/locale
lanes. Re-running the ordinary Seeder leaves the nonempty audit count unchanged.
Teardown confirms process/project absence, removes its private configuration,
certificate tree and lock, and releases the API/web listeners. This is a Step 1
startup/neutrality/cleanup proof; it is not Step 3 browser/Lighthouse evidence.

**Workstation port prerequisite.** The first attempt correctly refused occupied
5080/5432 ports. After explicit maintainer authorization on 2026-10-11, the two
foreign containers were stopped only for validation and restarted afterward;
both returned healthy. LearnStack volumes remain intact. Its local PostgreSQL
and Keycloak cannot run concurrently while the foreign database retains 5432;
no port mapping or foreign project configuration was changed.

**Step 1 review round 1 — `3160714..82459cb`.** Fresh GPT-6-astra and
GPT-6.1-sol reviewers independently covered the full diff through lifecycle/
security and evidence/corpus lenses. Four distinct findings were verified:
selected-env migration fallback, terminal hangup cleanup, split UTF-8/byte-bound
capture and restricted-course marketing exclusions in the demo manifest.
Corrections add actual Make/configuration, process/signal/parent, split-output
and marketing-disjoint controls. Combined seeding now rejects disagreeing ambient
credentials before stack/migration actions. Default-service enumeration is shared
as reinforcement; the suspected gated-port defect was not reproduced. The
suspected parallel prerequisite race was rejected because global `.NOTPARALLEL`
already serializes those acts. The 18-case focused suite, typecheck, eight
manifest/configuration cases and all 198 architecture cases pass. Lint and the
guarded frontend run pass all 1,420 cases with zero skips/todos. The complete
isolated lifecycle rerun also passes real SIGHUP/repeated-signal cleanup and
listener release. The two authorized foreign containers return healthy afterward.

**Step 1 review round 2 — `3160714..4966dca`.** Fresh GPT-6-astra and
GPT-6.1-sol reviewers independently inspected lifecycle/security and evidence/
corpus. Both reran all 18 focused cases. Two additional findings were confirmed:
Docker can read `COMPOSE_PROFILES` from the selected dotenv file, and Development
API configuration can consult workstation user secrets through its profile.
The selected-file control now refuses reserved Compose assignments, including
export/whitespace/colon forms. A read-only real Compose control observes exactly
seven default services and refuses the planted file profile before Docker.
The demo API receives an empty invocation-owned HOME/APPDATA/TMPDIR; restoration
and builds retain their own profiles. A real API/PostgreSQL test accepts the
configured hop secret and rejects an additional secret planted in a foreign
profile. No user profile is modified. All 24 focused lifecycle cases, typecheck
and the real launcher cases pass with zero failures/skips. Lint, all 1,426
guarded frontend cases and all 198 architecture cases pass. A fresh focused fix
review found that the foreign-secret request omitted its visitor header, allowing
an unrelated refusal to mask the regression. The corrected three-case real API
suite supplies that header and removes the profile boundary only in an owned
copy: the foreign secret now produces 200 in the mutant and 404 in the clean
runtime. All three cases pass. Both required review rounds and their verified
fixes are complete; Step 2 follows.

### P02d-7 Step 2 — genericity and exit foundations (2026-10-11)

**Implementation and both independent review rounds complete.** The exact-literal
G20 fence reads categorized `DemoIdentities.Create` values derived from typed
`SeedData`, including all declared IDs and translated identity/definition/field
labels. Ordinary body prose, locale/status/access values and schema keywords do
not become identities. Canonical platform values come from
`BuiltInCustomizations`, including collisions with demo values.

Backend Roslyn and frontend TypeScript AST readers compare complete decoded
strings ordinally and GUIDs by value, including alternate spellings. Embedded
JSON strings and standalone JSON values are inspected. Source parse errors,
empty/malformed vocabulary and missing production subjects fail loudly. The
frontend fixture is derived, not a second declaration; architecture tests compare
it to the current typed projection. Regenerate it after editing `SeedData`:

```sh
dotnet run --project backend/src/LearnStack.Tools.Seeder -c Release -- \
  --demo-identities > frontend/apps/web/src/test/fixtures/demo-identities.json
```

The backend scans Seeder orchestration as production and exempts only its exact
`SeedData.cs` declaration. Frontend scans app/package/configuration/native code
and root Node scripts, with exact named test-proof and development-launch data
exemptions. The existing unsupported-runtime-source/import census is preserved.
Every production project except the Seeder is checked for both compiled and
restore-resolved Seeder references, preventing symbolic `SeedData` access.

The existing real API/PostgreSQL/HTML/Flight presentation successor proof calls
one shared ordered-field assertion. Planted label, value, ordering, active-markup,
language and internal-name controls exercise the same predicate without changing
production rendering. Original-pin and unaffected-tenant observations remain.

**Initial verification.** Release suites pass 1,705 unit, 203 architecture and
four contract cases with zero failures/skips. The existing presentation-mode
real API/PostgreSQL/native production proof passes (one case), exercising both
HTML and Flight, successor/rebinding, original pins and the unaffected tenant.
Lint, typecheck, formatting and all 1,442 guarded frontend cases pass with zero
skips/todos. This is not Step 3 browser/Lighthouse evidence.

**Review round 1.** Fresh GPT-6-astra and GPT-6.1-sol reviewers independently
checked backend identity/reference coverage and frontend subjects/behavioral
controls. Four verified omissions were fixed: conditional C# branches, UTF-8
literals, emitted JSX values and runtime helpers hidden by test exemptions.
The initial fix exposed conditional branches conservatively; round 2 below
replaces that approach to preserve lexical semantics. JSX handling uses TypeScript's
emitter. Exact test directories replace name-wide directory exclusions, and
runtime relative imports include helpers even behind test filenames. Native HMR
and the shared process-stop helper are production subjects. Planted dirty/clean
controls cover all four findings. Release architecture (203), guarded frontend
(1,444), lint, typecheck, formatting and the documentation link audit pass.
Second-round review follows the fix commit.

**Review round 2.** Fresh GPT-6-astra and GPT-6.1-sol reviewers verified three
census omissions and one ordering-control gap. Flattening C# branches could let
an inactive comment hide active code. The reader now parses feasible symbol
configurations independently, discovers conditional symbols to a fixed point,
and refuses more than eight symbols or unreadable configurations. UTF-8 and raw
literal semantics remain intact. Frontend generated-output exclusions now name
actual roots; nested source directories remain subjects. Runtime imports resolve
local module URL query/fragment suffixes and percent escapes, while computed
imports fail loudly. Literal `require.resolve` loads remain countable. The
ordering offender now swaps intact label/value pairs rather than corrupting them.
Release architecture (203) and all guarded frontend cases (1,446), lint,
typecheck, formatting and link/anchor checks pass. Fresh fix verification follows.

**Fresh fix verification.** The prior omissions are closed. The verifier found
one new resolver-options gap: a relative `require.resolve` input could redirect
through `paths`. That unmodeled form now fails loudly, with a planted control;
the actual literal external-package ESLint load remains accepted. Both review
rounds and their verified findings are complete. Step 3 follows.

#### P02d-7 preliminary exit evidence inventory

This is an inventory, not a phase-exit verdict. Step 4 reconciles every individual
Deliverable and Completion Criterion against exact-head evidence, current CI and
protection receipts; historical delivery records retain their original limits.

| Criterion group | Existing evidence to retain/recheck | P7 completion work |
|---|---|---|
| Two sites, linked catalog/course/lesson, distinct field sets and branding | P6 product/presentation HTML/Flight fixture; maintainer keyboard/focus/reflow/VoiceOver record; `DemoManifest` typed expectations. | Step 3 real Chromium linked navigation and computed themes, three host/locale lanes; same API/web/database with owned Keycloak stopped. |
| Clean preparation, non-destructive repeat and owned stop | Step 1 isolated seven-service/two-realm startup, audit-neutral Seeder repeat and signal/parent/process/TLS controls. | Step 3 shares the lifecycle; Step 4 records complete CI receipt and cleanup. |
| Education schema, RLS, scope, satellites, parents, privileges, migrations | `EducationIsolationTests`, migration/tenant-scoping/index/grant sweeps and P1 delivery record. | Re-run full backend suites at exit; Phase 05 preservation handoff. |
| Writer invariants, exact pins, locale/slug/state/body validation, MUST audit, branding and seed convergence | `EducationWriterTests`, `TenancyWriterTests`, `SeederTests`, P2 record. | Re-run with zero skips; retain typed inventories and bounded branding/access contracts. |
| Definition/settings reads, generations, rollback safety, fallback, no read validation | `CustomizationProjectionTests` and composition/cache/settings suites; P3 record. | Re-run; retain existing generation/family metrics and settings freshness boundary. |
| Public eligibility, cursor/scope/locale, DTO/HEAD/OpenAPI/SDK, no-store, read-only pipeline | P4 public HTTP, contract, transaction and dispatch guards. | Re-run SDK/contract/OpenAPI drift and all required checks; retain current seven-check read-back only after success. |
| Provenance/ingress/hop/visitor limits, private carriers, native production freshness, single bootstrap | P5 remediation and ADR-0055 real native/API proofs; P6 bootstrap/refusal coverage. | Step 3 uses identical ingress/limiter and records positive API calls with zero 429. |
| Localized renderer, inert authored text, metadata/status, manual accessibility | P6 i18n/route/HTML-RSC/a11y guards and recorded human checks. | Step 3 observes actual browser documents; Lighthouse supplements the bounded manual record. |
| G20 exact-literal/reference fence and schema replacement | This Step 2 backend/frontend dirty/clean controls and existing presentation successor fixture. | Both independent review rounds; Step 3 ordered fields. Computed comparisons remain a review/behavioral boundary. |
| Performance/accessibility audits, report/census/gzip guards | ADR-0056 accepted obligations, four Registered behavioral rules. | Step 3 implements 27 report pairs/hard checks; Step 4 activates mandatory CI/protection and updates statuses with enforcement. |
| Governance, all gates/catalogue rows, module/standard/skill/vision/MVP carriers and named exclusions | Existing decision register and P1–P6 records. | Step 4 complete criterion matrix, zero 02d Registered/Awaiting rows, owner-document checks and P02b-0/Phase 05 handoffs. |

### P02d-7 Step 3 — browser and audit proof (2026-10-11)

**Complete — both independent review rounds and verified fixes pass.**
The shared CI lifecycle invokes `verify-public-demo.mjs` over the same seeded
production API/native renderer. `DemoManifest` also derives hidden lesson titles
and field values, excluding values shared with eligible public content. The
browser follows the actual catalog/course/lesson links in all three host/locale
lanes and compares computed theme colors, identity and ordered authored fields.

Exact development-tool pins are Lighthouse 13.5.0 and Playwright 1.64.0, using
its managed full Chromium 156.0.8078.4, revision 1248. The audit configuration
has an independently recorded 125-audit/14-artifact census and complete category
weights. A custom initial-navigation gatherer observes the actual document and
all delivered Script bodies; it never refetches or navigates. The shared policy
checks original inline source, complete decoded bodies, normalized gzip and the
actual audited product DOM before admitting a JSON/HTML report pair.

The structured API completion observer retains only aggregate status/path-family
counts. A fresh HTTPS product read supplies its positive control. Four serial
batches preserve the real admission policy, with the approved quiet intervals
and subordinate deadlines. The public receipt records setup/proof/cleanup
milliseconds, versions, configuration, source digests, report inventory and API
counts. Browser profiles, certificates, private configuration and raw logs stay
outside the public evidence. CI cleanup confirms owned process/project absence
and removes owned private files, environment and lock within its separate bound.

**Initial verification.** All 43 focused frontend controls and eight existing
manifest/configuration integration cases pass with zero failures/skips. Lint
and typecheck pass. The guarded frontend runner passes all 1,489 cases
(1,429 web + 60 SDK); all 203 architecture cases pass, with zero skips.
The controls include actual installed Lighthouse configuration
resolution, measured over-budget sources, wrong/error product documents, invalid
reports/census, missing/truncated/foreign-target sources and split API events.
The first complete serial feasibility run passes: lifecycle setup 61,468 ms,
proof 549,269 ms (9 min 9 s), and confirmed cleanup 10,656 ms. It produces all 27
JSON/HTML pairs and nine browser screenshots. All hard checks pass: normalized
initial JavaScript is 106,381–106,573 bytes, CLS is zero, and scored automated
accessibility is 1 on every run. The four proof batches observe exactly
18/36/36/36 successful API calls; the separate observer control observes two.
Both untrusted/wrong-SPKI browser controls refuse before reaching the API.
The CI-owned Keycloak stays stopped. Owned processes/project/private files,
environment and lock are removed; the two authorized foreign containers restart.

LCP samples are 2,008.544–2,166.836 ms, producing an advisory median warning for
each of the nine URLs. Server response is 15–23 ms and TBT 4–16 ms. These are
local simulated lab samples, not field/p95 performance or a new manual WCAG pass.
This is working-copy feasibility evidence over the `69ce613` baseline and build
`vCA7HXnit3froptth4IYD`; it is not an exact-head CI claim. Subsequent receipt-only
hardening records the complete expected/configuration census and startup/final
counters; the final committed CI run must validate those changes. Step 4 owns
CI enforcement, protection activation and phase exit.

The first runtime attempts exposed harness defects before any audit verdict:
macOS's certificate verifier stalled when Chromium inherited the isolated Node
HOME, and optional `undefined` configuration properties differed from their JSON
receipt. A causal disposable TLS experiment confirms certificate refusal with
the real account HOME; only the managed browser receives it, with an explicit
owned disposable profile. Worker/API environments remain isolated. Configuration
comparison now uses the serializable receipt and an actual-resolution control.
The linked nine-page browser path subsequently passed, with exactly 18 successful
API calls and no 429, before the configuration comparison stopped that attempt.
These observations are preliminary validation, not the complete feasibility gate.

**Transitive review.** Both direct tools are Apache-2.0 development dependencies.
The added tree includes MPL-2.0 `axe-core` 4.14.0, used by Lighthouse's development
accessibility audit; retain its package license/source obligations if distributing
that tool. Existing runtime package versions are unchanged. Resolving the added
OpenTelemetry API peer changes some lockfile peer identities without adding
application instrumentation. The full-workspace audit reports zero critical,
one high and one moderate advisory; neither path comes from the new tools.
The existing [security triage](#remediation-group-3--approved-security-maintenance)
owns Braces' unfixed build-glob recursion issue and the incompatible-major
selector-parser fix. Their maintainer/backport/parent-migration ownership and
Security Standards deadlines remain unchanged; this is not a clean-audit claim.

#### Step 3 review round 1

Fresh GPT-6-astra (xhigh) runtime/measurement and GPT-6.1-sol (xhigh)
proof/corpus/dependency reviews cover `69ce613..d99d10b`. Both request changes;
three verified findings concern the proof, with no production-code defect.
Independent root reproductions confirm each failure before correction.

- Completion observation silently ignored damaged JSON and unterminated records;
  the receipt preceded final API output drain. Separate stdout/stderr decoders,
  pending-tail refusal, joined EOF finalization and post-cleanup final census now
  prevent malformed, late or 429 traffic from disappearing. Actual record controls
  include split/interleaved clean output, damaged/newline/EOF records and late
  success/refusal events.
- Lighthouse treats its navigation timeout as a warning. A real disposable
  Chromium/Lighthouse run took 22,116 ms and admitted an incomplete image load.
  Report admission now refuses warnings. The shared proof includes that actual
  soft-timeout control with an otherwise passing report counterpart.
- Ancestor opacity and zero-area rendering hid product markers while the DOM gate
  admitted them. Browser-native visibility plus positive-area rectangles now
  apply to markers and individual field labels/values. Each real lesson navigation
  plants ancestor-opacity, zero-area and hidden-label mutations, verifies refusal,
  restores the document and re-admits the visible control, without new API calls.

All 74 focused proof/lifecycle cases pass with zero failures/skips. Disposable
managed-Chromium controls verify visible admission and hidden/zero-area refusal.
A complete rerun and fresh independent round 2 remain pending; the earlier local
feasibility result is preserved and does not validate these subsequent changes.

#### Step 3 review round 2

Fresh GPT-6-astra (xhigh) runtime/measurement and GPT-6.1-sol (xhigh)
proof/corpus reviews cover `69ce613..38c15d9`. Both request changes. Three
verified proof gaps remain; the existing 50 proof controls passed independently.

- A completion record missing its opening brace or carrying a prefix was ignored,
  as was an unexpected endpoint completion. The owned API emits structured
  startup/output; only empty plaintext separators are now admitted. Damaged and
  unexpected completion records refuse the entire census, including a 429.
- Process-group absence preceded child `close` and buffered stream EOF. An actual
  child with deliberately deferred stdout demonstrated a 429 arriving after a
  prematurely passing final census. Cleanup now joins `close` with a separate
  shared five-second stop/output-drain bound before observer finalization. The
  regression holds and releases the real pipe, proves cleanup waits, then refuses
  the 429.
- Clip/filter/mask and offscreen/overflow styles could hide laid-out markers.
  The bounded seeded-page predicate now refuses these ancestor styles and
  horizontally unreachable/negative-area markers. Each lesson navigation checks
  eight actual DOM mutations, restores and re-admits its positive control;
  the receipt requires all 24 completed controls. This is not a general CSS
  occlusion or visual-accessibility audit.

All 78 focused proof/lifecycle cases, typecheck and lint pass. The full guarded
runner passes 1,500 cases (1,440 web + 60 SDK), with zero failures/skips/todos.
Fresh GPT-6-astra (xhigh) and GPT-6.1-sol (xhigh) fix reviews approve
`38c15d9..39cee01` and the final shared cleanup-deadline adjustment. Independent
controls reproduce the old observer/drain gaps, confirm joined stdout/stderr,
refuse daemon output errors and bound a TERM-resistant child plus held output
to 5,003 ms. Managed Chromium refuses all eight DOM mutants and re-admits each
restored counterpart; the five added cases passed the old predicate.

The complete `38c15d9` rerun passes: setup 60,270 ms, proof 571,089 ms,
cleanup 10,688 ms, all 27 report pairs and nine screenshots, actual soft-timeout
refusal, nine visibility controls and 134 successful API completions, with zero
429/malformed/pending records. The authorized foreign containers restart healthy.
That receipt precedes round-2 fixes. The complete `39cee01` rerun also passes:
setup 60,374 ms, proof 585,644 ms and cleanup 10,719 ms. Build
`d0bzXjXoImUdH317X83rK`, invocation `825982bee2ed568f43208368f99f332d`,
produces all 27 report pairs, nine screenshots and 24 completed visibility
controls. The actual soft-timeout and both TLS refusal controls pass; final
observation contains 134 successful calls, zero 429/malformed/pending records.
All owned cleanup checks pass and the authorized foreign containers restart.
The subsequent shared-bound change has focused verification above and must also
run in final CI. Both independent review rounds and fresh fix verification are
complete. Step 4 owns CI enforcement and phase exit; neither is claimed here.

### P02d-7 Step 4 — CI enforcement and phase exit (2026-10-11)

**Implementation, both independent review rounds and real CI complete.** The
[CI and exit closeout](#p02d-7-ci-and-phase-exit-closeout-2026-10-11) records
successful artifacts and the activated seventh required context.
The always-running `public demo + lighthouse` job replaces the disabled
placeholder. It installs the exact locked browser tools and managed Chromium,
then invokes `make demo DEMO_MODE=ci` over the shared seven-service lifecycle.
The 20-minute job includes setup, the 12-minute serial proof, separate bounded
cleanup and artifact handling. An unconditional bounded workflow receipt records
failure even when setup prevents a product receipt. Uploads allow only public
JSON/HTML reports, screenshots and sanitized workflow/proof receipts.

The [individual exit evidence matrix](phase-02d-exit-evidence.md) maps all
36 Deliverables, 79 Completion Criteria, 39 exclusions and nine exit obligations.
It preserves prior human/PR evidence and names the verified CI receipts;
implementation alone does not constitute phase exit.

**Criterion reconciliation.** Published roots cannot receive new translations
under ADR-0050. The third-locale proof therefore creates and publishes new
French-only roots through supported commands, preserving the existing EN/TR
public responses and all five migration histories. Organization branding writers
remain unrouted. The branding criterion now separates API-root command refusal
from mapped-host reads and distinguishes a successful business audit from the
required standalone denied-attempt audit. This corrects the mutable criterion's
no-audit overclaim; ADR-0033/0044 and production auditing are unchanged.

**Education preservation handoff.** Phase 05 inherits separate Course/Lesson
roots and their contained Pattern A translations. Its migration preserves stable
IDs and published localized URLs, `(sort,id)` lesson order, tenant/organization
scope, translated bodies, exact content-type/taxonomy revision pins and course
access policy. `SeedData.All` supplies both tenant curricula: eight courses,
ten lessons and 27 translations at their declared scopes, including draft and
restricted controls. Carry those persisted fixtures through the migration;
reseeding is not a migration strategy.

> **Reconciliation — 2026-10-12.** The settings-event ownership below records
> P7's expectation at closeout. Accepted
> [ADR-0057](../decisions/0057-transactional-event-production-and-completion.md#first-useful-production-flow)
> selects completed provisioning as the first useful event; the
> [Tenancy catalogue](../modules/tenancy/README.md#integration-event-catalogue)
> promises no settings-invalidation event. Settings remain uncached. Any future
> cached consumer needs its scope/key/freshness decision; Phase 11 owns the
> distributed adapters on their existing triggers, without a settings-event promise.
> The dated handoff text is retained.

**P02b-0 re-verification handoff.** Re-read the four `/api/v1/public` GET/HEAD
pairs and contract; retained `*.learnstack.local` seed hosts over native HTTPS,
manual local hosts/CA setup and private paired hop configuration; coordinated
60/min visitor and 600/min physical-peer accounting; tenant-wide English versus
organization-scoped Yoga seed context; one live bootstrap with two calls per
normal product document and three for a followed missing-content chain; and the
uncached settings reader plus `learnstack.tenancy.settings` eager-invalidation
event owned by Phase 02b. These delivered premises do not Accept future
session/BFF/authenticated-rate-limit decisions.

**Initial validation at `bf5bc9c`.** Both third-locale/branding cases pass locally
with zero skips. Release build has zero warnings/errors; 1,705 unit, 203 architecture,
four contract and 180 Docker-free integration cases pass, as do all 1,500 guarded
frontend cases. Typecheck, lint, whitespace and 2,042 relative links (666 fragments)
pass. All 871 Docker integration cases pass with zero skips; execution guards pass.
Two fresh review rounds, a real
successful PR job and the authorized seventh-context activation/read-back remain.
No PR, live check activation or phase exit is claimed yet.

#### Step 4 review round 1 (2026-10-11)

Fresh GPT-6-astra (xhigh) and GPT-6.1-sol (xhigh) agents reviewed
`06d46ad..bf5bc9c` for runtime/CI ownership and corpus/exit evidence. Their
confirmed findings concern proof completeness and CI prerequisites, not a
production contract defect. C40–C42 now share one HTTP test: 17 requests pair
foreign-only/shared slugs, exact-locale omissions and organization/tenant host
controls with exact raw translation sets on each request's own enlisted
application connection. The third-locale case now enables French on the foreign
host and proves its positive catalog before checking the same French detail
URLs are hidden. Missing Cache-Control can no longer bypass its assertions.

The CI job starts an absolute deadline before checkout, bounds setup steps and
interrupts the shared lifecycle by minute 16, reserving four minutes inside the
unchanged 20-minute ceiling for owned cleanup, policy cleanup and artifacts.
Four controls execute the actual workflow shell: first-step reservation,
remaining-budget arguments and two exhausted-budget refusals. An actual GNU
timeout → make → shared Node-owner experiment exits 124 only after child cleanup.
The internal 12-minute serial proof deadline remains unchanged.

The GitHub-hosted Linux prerequisite loads only an invocation-owned AppArmor
userns profile for the exact managed Chromium when the kernel restriction needs
it, then performs a sandbox-enabled launch smoke. Fifteen isolated controls
exercise ownership, literal paths, failed load/removal and cleanup without
changing workstation policy. A real Linux job remains required; the initial
runner risk was source-supported inference, not a reproduced hosted failure.
The exit matrix, stale review guidance, Phase 02b risk premises and mutable
renderer/writer scope descriptions now match delivered contracts.

All three focused locale/branding Docker cases pass with zero skips and the
execution guard. The guarded frontend rerun passes 1,504 cases (1,444 web + 60
SDK), zero failures/skips/todos; typecheck, lint, scoped backend formatting and
whitespace pass. The initial full backend totals above precede the added combined
case. Final complete PR CI, round 2 and protection read-back remain required.

#### Step 4 review round 2 (2026-10-11)

Fresh GPT-6-astra (xhigh) and GPT-6.1-sol (xhigh) agents reviewed
`06d46ad..100b022` across runtime/CI ownership, security, correctness,
maintainability, test quality and the complete changed corpus/exit matrix.
Both approve with no remaining Blocker or Major. The corpus reviewer confirmed
two Minor navigation/census remnants: the matrix still called the expanded
three-case class two cases, and CONTRIBUTING still described completed
ADR-0055 reviews as remaining. Both are corrected; the reviewer independently
checked the bounded follow-up. One overlong P7 prose line is wrapped without
changing its content, and catalogue navigation names the new deadline/sandbox
controls. Earlier P1–P6 delivery records remain unchanged.

The runtime reviewer independently passes all 15 Chromium-prerequisite controls,
four actual-workflow deadline controls and all three focused Docker HTTP cases,
with zero skips, plus whitespace. The corpus reviewer verifies all 36/79/39/9
inventory counts, complete named evidence, 175 Implemented rules, 21 Active/one
Adopted standards and future-owner scope. Root checks pass seven ADR lifecycle
and 14 OpenAPI-CI controls. The complete PR's 33 unique Markdown files resolve
2,188 relative links and 686 fragments with no errors before these closing
navigation additions. Fenced and inline examples are excluded from link counts.
Both review rounds and verified fixes are complete. Actual successful Linux PR
CI, complete artifact receipts and authorized protection activation/read-back
remain required before a positive phase-exit verdict.

#### P02d-7 CI and phase-exit closeout (2026-10-11)

**Verdict: implementation and Phase 02d exit verification complete.** All four
P7 steps completed both fresh independent review rounds; verified findings are
fixed and committed. [PR #28](https://github.com/HodeTech/LearnStack/pull/28)
remains open for maintainer review and merge. No merge is claimed.

All seven jobs passed in the first real
[PR run](https://github.com/HodeTech/LearnStack/actions/runs/38106505383) for source
head `8605fc94f7ba343d5cd7cc15d28001d99349735b`. GitHub tested synthetic merge
`15109c3a80a41ee7b6d6f9832880c7b2b13877f5`, whose parents are that source and
main `378346126949c90bb286b5217c24513fc2044f06`. The sanitized workflow receipt
records both identities. Downloaded TRX files confirm 1,705 unit, 203 architecture,
four contract, 180 Docker-free integration and 872 Docker integration passes:
2,964 backend tests, zero failures/skips. All three locale/branding/combined-scope
cases are present and Passed. Frontend job output confirms 1,444 web + 60 SDK
passes, zero failures/skips/todos; typecheck, lint, builds and SDK drift pass.
Meta, secret scan and OpenAPI compatibility pass too.

The `public-demo-lighthouse` artifact contains exactly 27 JSON/HTML report pairs,
nine screenshots and two sanitized receipts; no private logs, profiles, env files
or keys are included. Report digests, nine URL/three-run census, configuration,
invocation, build and synthetic checkout identities match. The invocation is
`aab02c37c71c02569f71ef8829a96c09`; build is `yTmMjFs6JVE2ea_W-jFzX`.
Node 22.23.1, Lighthouse 13.5.0, Playwright 1.64.0 and Chromium revision 1248 /
156.0.8078.4 run on the four-CPU Ubuntu 24 hosted runner. Its sandbox-enabled
launch passes with the exact-executable temporary AppArmor prerequisite;
the owned policy removal passes afterward.

All nine linked navigations and 24 planted visibility refusals pass. Every audit
has normalized initial JS gzip 106,359–106,553 bytes, CLS 0 and automated
accessibility 1. Per-URL timing medians remain advisory, not production p95.
All 134 API observations are 200, with zero 429, malformed or pending records:
six startup + two observer-control + 18 linked-browser + 3 × 36 audit calls.
Both TLS refusal controls and the actual audit-timeout control pass. Keycloak is
stopped before anonymous navigation. Setup/proof/cleanup take
228,127 / 650,154 / 11,041 ms; the proof stays inside 12 minutes and cleanup
inside 30 seconds. The complete job takes 15 minutes 51 seconds, within its
20-minute ceiling and absolute setup/proof deadline.

After that successful run, the authorized API update adds only
`public demo + lighthouse` to main's required contexts. A fresh full read-back
confirms seven GitHub Actions contexts (`app_id: 15368`), strict up-to-date
checking, all six prior contexts retained and every unrelated protection setting
identical to the immediately preceding API response. The current list lives in
[CONTRIBUTING](../../.github/CONTRIBUTING.md#branch-protection-settings-on-main).

The [individual evidence matrix](phase-02d-exit-evidence.md) reconciles all
36 deliverables, 79 criteria, 39 explicit exclusions and E01–E09. All 45 register
rows have dated Accepted closure links; no Phase 02d-owned Registered/Awaiting
catalogue rule remains. All four ADR-0056 rules are Implemented; corpus checks
confirm 175 Implemented catalogue entries and 21 Active/one Adopted standards.
The existing P6 human/VoiceOver evidence retains its original attribution and
limits; this closeout claims no new final-head manual WCAG or zoom audit.
Phase 05 preservation and P02b-0 premises are handed off above and in their
owning documents; no future-phase decision is accepted by this exit.
Closing-document checks pass all five CorpusConsistencyTests with zero skips;
the 33 changed/new Markdown files resolve 2,197 relative links and 694 fragments
with no errors. Added prose respects 88 columns; ADR-0056 changes are insertion-only.

##### Reviewed implementation head verification (2026-10-11)

The first run above owns activation history. The separately downloaded
[reviewed-head run](https://github.com/HodeTech/LearnStack/actions/runs/38108622191)
passes all seven jobs for source `d47ef17c8473e17e0950361dc35e419ba6c0825c`,
tested merge `ba44563af66fbd7744b6625673e61286a8e77fa4` with the same main parent.
Its invocation is `170c02e7741fbc6c79895ce718cecd89`; build is
`rlL6ubYmcf7_VQaPBN-fA`. TRX/frontend receipts confirm the same 2,964 backend
and 1,504 frontend passes, zero failures/skips/todos.

All 27 report pairs, nine screenshots, nine navigations and 24 visibility
refusals pass. Report digests and source/build/invocation/configuration identities
match. Normalized gzip is 106,358–106,551 bytes, CLS 0 and automated
accessibility 1 throughout. Both TLS refusals, the audit-timeout control and all
134 API observations (200 only, no malformed/pending records) pass.
Setup/proof/cleanup take 214,616 / 644,207 / 12,962 ms; all cleanup flags pass.
Sandbox-enabled Chromium and owned AppArmor cleanup pass. These numbers belong
to the exact implementation head reviewed externally, not to later commits.

##### External review follow-up (2026-10-11)

The maintainer's review of `3783461..d47ef17` reported no Blocker/Major and
four bounded documentation/maintenance findings. All four are verified and
addressed: the reviewed-head receipt above supplements activation history;
runtime imports move to the file header; installed tool/browser metadata feeds
the shared census and remains checked against independent committed pins; and
ADR-0056 gains dated acceptance-baseline navigation without changing history.
Tool versions, budgets, admission and cleanup behavior are unchanged.

Local validation passes 1,454 web + 60 SDK tests, zero failures/skips/todos,
typecheck, lint and all 203 architecture tests. Ten added cases cover installed
metadata/pin mismatch, malformed or missing browser metadata, stale census and
wrong-version report refusal, each retaining a clean passing control. Both fresh
independent remediation rounds reviewed `d47ef17..dca3a7b` and approved without
findings. Each independently checked installed dependency resolution, fail-closed
metadata/census comparison and the downloaded historical receipts. All five
corpus tests pass; the complete PR's 33 changed/new Markdown files resolve
2,201 relative links and 698 fragments without errors. The new remediation head
must still pass full seven-job CI through the PR rollup before merge.

##### Systematic review remediation (2026-10-11)

The later review examined source `aebefee`. Its findings were re-verified against
current code before changes; this is a separate record from the historical
`d47ef17` review above. No new architecture decision or budget change is needed.
Work remains on `development`; PR #28 is not merged by this record.

1. **Lifecycle and launcher — complete.** `cd6affd` removes the unsupported
   Seeder launcher argument, attempts independent process/observer/Compose
   cleanup stages even when a preceding stage fails, and retains failure causes
   internally without echoing private diagnostics. Private files are removed
   only after confirmed cleanup. Socket errors distinguish occupied ports from
   other bind failures; declaration files describe the actual lifecycle API.
   `/artifacts/` is ignored. All 34 lifecycle tests, lint and typecheck pass.
   Two first-round reviewers and a fresh second-round reviewer approved without
   findings.
2. **Genericity census — complete.** `db6bcdf`, `90114f0` and `c6cd69f` close
   source-root/format admission and supported static import gaps. Local and
   inherited-config candidate ambiguities are conservatively included or
   refused; workspace manifests have explicit ownership. Backend evaluation
   checks `Compile` and `ProjectReference` coverage, exact output roots and
   bounded build shapes/properties. First-round frontend/backend reviewers
   identified five verified resolution/build-admission gaps, fixed in
   `90114f0`. The fresh second round found inherited-config shadowing, fixed in
   `c6cd69f`; its same counterexample now refuses and its clean control passes.
   All 18 frontend genericity cases, seven backend genericity cases, all 203
   architecture cases, lint, typecheck and targeted format pass. This remains a
   bounded exact-literal/static-import fence, not arbitrary program analysis.
3. **Proof helpers and carriers — complete.** `9a0d285` extracts actual worker
   network-inventory reconciliation and verifier advisory aggregation for fast
   dirty/clean controls. Navigation counts come from completed
   observations; upload globs admit only named reports/screenshots/receipts.
   Current architecture, infrastructure, standards and route-authoring guidance
   describe delivered G44/G45 enforcement. Two first-round reviewers and a fresh
   second-round reviewer approved without findings. The second round also
   reproduced all historical advisory medians from the downloaded receipt.
   All 128 focused tests, 1,537 frontend cases (1,477 web + 60 SDK) and 2,092
   Docker-free backend cases (1,705 unit + 203 architecture + four contract +
   180 integration) pass with zero failures/skips/todos. Release build, format,
   lint, typecheck and production frontend builds pass; 15 sandbox-policy and
   seven ADR-status controls pass. The complete PR's 36 Markdown files resolve
   2,289 relative links and 719 fragments; added prose respects 88 columns.
   The final PR rollup must still pass all seven required jobs, including the
   real Docker and 27-audit lifecycle proof, before merge.

**Late automatic-review follow-up.** Run 38125035732 passes all seven jobs for
source `4541d63`, including 2,964 backend and 1,537 frontend cases, the real
27-audit proof and cleanup. A later CodeRabbit comment exposed a local-DX gap:
ignored `.env.local` and OS/editor files reached raw filesystem classification.
The follow-up admits only explicitly known machine filenames that Git also
ignores. Ignored source files, unknown formats and compiler coverage retain
fail-closed behavior. The first independent round approved `d16d2d7`. The fresh
second round reproduced an automatically imported `.csproj.user` build hook;
`.user` is therefore excluded from the machine-file exemption in `9343e99`,
with explicit refusal controls. The reviewer independently re-ran the actual
collector: clean ignored files pass and the original build-hook counterexample
refuses. Both review rounds are complete, with no remaining findings. All 19
frontend and seven backend genericity cases pass; the complete guarded frontend
rerun passes 1,538 cases (1,478 web + 60 SDK), zero failures/skips/todos.
Owned temporary ignored-file controls pass both actual raw collectors and are
removed afterward. Whitespace and the complete PR's link/fragment audit pass.
The final PR rollup below owns replacement final-head CI; all seven jobs remain
required before merge.

**CI stability follow-up.** Run 38126790757 reports one unit-test failure in
`A_Factory_That_Ignores_Its_Token_Is_Still_Bounded`; all 203 architecture cases,
including the new admission controls, pass. This pre-existing test races an
80 ms cache deadline against a 600 ms factory delay. It now holds the factory
behind an explicit release signal until the cache's own timeout is observed,
checks that a replacement waits and retains the late-result/cache assertions.
The bounded test wait cannot masquerade as the cache timeout: its message must
identify the cache's own deadline. The release is guaranteed in `finally`.
Production cache behavior and its budget are unchanged. `32a7951` delivers
this test-only fix. Fresh GPT-6.1-sol (high) and GPT-6-astra (high) reviews approve
in two separate rounds with no findings; the second reviewer independently
passes the focused Release case. All 51 cache cases and all 1,705 unit cases
pass with zero skips. Replacement final-head CI remains required by the rollup
below; the initial failed job is not counted as passing evidence.

**Other finding dispositions.** The gzip adapter equality checks wiring; the
independent Script observer checks source-window coverage, not a second byte
measurement. Existing fast tests already cover report admission, source-byte
normalization, completion accounting and gatherer cleanup; full orchestration,
TLS refusal, real visibility and report-file checks remain real lifecycle/CI
proofs. The added helpers supplement those proofs rather than replacing them.
The review's zero-fast-coverage claim was therefore broader than the actual gap.

The reported PR-body count/gzip mismatch compared different receipts. Downloaded
[run 38119659152](https://github.com/HodeTech/LearnStack/actions/runs/38119659152)
for source `aebefee`, tested merge `0bb897c`, verifies 2,964 backend and 1,514
frontend passes, gzip 106,358–106,552 bytes. Historical `d47ef17` numbers above
remain correct for that earlier head; neither substitutes for final remediation
CI. The newer proof took 619,309 ms inside the accepted 720,000 ms deadline;
cleanup took 10,595 ms inside 30,000 ms. No deadline increase is justified.

Ordinal complete-value matching and strict readable JSON are accepted G20
behavior, so host case-folding and JSON-comment tolerance are not introduced.
Below-fold laid-out content remains valid full-page evidence; adding a viewport
bottom cutoff would reject legitimate long lessons. Bounded child ownership
records are retained through process-group/EOF confirmation; no sustained leak
was demonstrated. The older `public-local` exit behavior, dormant platform
branches and test-project placement are unchanged maintenance suggestions.
Census regeneration instructions already name the authoritative Seeder command.

Mutable Action tags are pre-existing throughout CI, with no accepted SHA-pinning
policy. This patch does not introduce a repository-wide dependency policy;
[Security Standards](../standards/11-security.md#dependency-hygiene) already assign
update automation to Phase 11. Accepted ADR-0053/0056 baseline and dated notes
remain historical; their then-pending wording is not rewritten as a correction.
Current carriers link delivered P7 evidence instead. No new human/WCAG evidence
is claimed by these orchestration and proof changes.

Closing documentation commits must also pass the seven required jobs before
merge; the [PR check rollup](https://github.com/HodeTech/LearnStack/pull/28/checks)
owns that moving-head result. Next after maintainer review and merge is
**P02b-0 re-verification and decision pass**, before Phase 02b implementation.

### P02d-7 merge and closeout (2026-10-11)

The maintainer merged [PR #28](https://github.com/HodeTech/LearnStack/pull/28) at
2026-10-11 09:38:58 UTC. GitHub records final PR source
`8d9d2195cfadf1d83807613ea22d792b7ce329f4` and squash merge
`d37f46ccf0ebbf909f02c81a59d9019af3e9a1ca`, whose parent is the previous main
`378346126949c90bb286b5217c24513fc2044f06`. Source and squash merge have the
identical tree `7f83bee243fe17626ef10af051e54a9218fd7aaa`. Development incorporates
the main merge without switching branches or rewriting published history.

All seven required checks pass in the
[final PR run](https://github.com/HodeTech/LearnStack/actions/runs/38127402847)
for that source. The tested synthetic merge is
`2d8ff6b23cd1b27691ea0038859944ca6c0ce395`, with the final source and previous
main as parents. Independently verified downloaded receipts confirm:

- **2,964 backend passes**, zero failures/skips: 1,705 unit, 203 architecture,
  four contract, 180 Docker-free integration and 872 Docker integration cases.
- **1,538 guarded frontend passes**: 1,478 web and 60 SDK, zero
  failures/skips/todos; lint, typecheck, builds and SDK drift also pass.
- Exactly 27 Lighthouse JSON/HTML report pairs, nine screenshots and two
  sanitized receipts. Report digests, source/checkout/build/invocation identities
  and the nine-URL/three-run census match. Normalized initial JS gzip is
  106,358–106,552 bytes, CLS 0 and automated accessibility 1 throughout.
  Per-URL timing medians remain advisory.
- Nine linked navigations, 24 planted visibility refusals, both TLS refusals and
  the actual audit-timeout control pass. All 134 API observations are 200,
  with zero 429, malformed or pending records. Setup/proof/cleanup take
  170,462 / 618,055 / 10,695 ms; all owned-cleanup flags pass.
- A fresh protection read-back retains `strict: true` and all seven GitHub
  Actions contexts, including `public demo + lighthouse`. This closeout changes
  no protection setting.

The [post-merge main run](https://github.com/HodeTech/LearnStack/actions/runs/38129121225)
tests the squash commit separately; its result is not inferred from source CI.
At this documentation check, five jobs pass; Docker integration and the
demo/Lighthouse job are still running.

**P02d-7 and Phase 02d are closed.** All seven implementation packets are merged;
all four P7 steps and verified fixes retain their recorded independent reviews.
The [individual exit evidence](phase-02d-exit-evidence.md) reconciles all
36 deliverables, 79 completion criteria, 39 exclusions and nine exit obligations.
Earlier dated delivery/review records, Accepted ADRs and their acceptance-time
or pre-merge wording remain historical and unchanged. No new human/VoiceOver,
full WCAG, zoom, production-p95 or deployment-readiness claim is added.

**Next: P02b-0 — phase-entry re-verification.** The
[Phase 02b packet table](phase-02b-events-auth.md#packets-and-decision-gates)
requires re-reading code, corpus and Hub premises against HEAD, including the
[P7 handoff](#p02d-7-step-4--ci-enforcement-and-phase-exit-2026-10-11).
P02b-0 has not started; each later packet closes its own gates in its decision
pass. No Phase 02b implementation or auth/BFF decision is accepted here.

Phase 05 retains versioned-curriculum migration and preservation of existing
Education/seed records; Phase 06 retains full renderer/Studio and Playwright
Test/axe; Phase 07 retains enrollment, learner portal, progress and access grants.
Phase 11 retains production hardening, telemetry and demand-gated adapters.
Course Marketplace ADR-0049 and Phase 09a remain Proposed; commerce, commissions,
payouts and cross-installation federation are not delivered.

**Documentation-only closeout validation.** All 203 local Release architecture
cases and seven ADR lifecycle/workflow controls pass, with zero failures/skips.
The seven changed Markdown files resolve 1,088 relative links and 478 fragments
with zero errors; added prose wrapping and whitespace checks pass. Prior dated
phase delivery records and all Accepted ADR files are byte-preserved. Full runtime
suites are not rerun locally for this documentation-only change; the final PR
receipts above supply their evidence.

## Status archive

The Status block the phase document carried until 2026-10-22, kept verbatim. Its
in-document links were retargeted to whichever file now holds their target. The
phase document's current Status block replaces it; nothing below is current.

> **Status (2026-10-11).** Phase 02d is **complete and merged** through
> [PR #28](https://github.com/HodeTech/LearnStack/pull/28). The
> [merge closeout](#p02d-7-merge-and-closeout-2026-10-11) records final verification
> and the completed phase exit. P02b-0 phase-entry re-verification is next.
> The kickoff, `P02d-0`, ships this
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
> | P02d-4 | Public read API and contract checks | ✅ complete and merged — 2026-10-08; [merge closeout](#p02d-4-merge-and-closeout-2026-10-08) |
> | P02d-5 | Server-rendering path | ✅ complete and merged — 2026-10-09; [merge closeout](#p02d-5-merge-and-closeout-2026-10-09) |
> | P02d-6 | Public renderer | ✅ complete and merged — 2026-10-10; [merge closeout](#p02d-6-merge-and-closeout-2026-10-10) |
> | P02d-7 | Demo, full-stack CI and exit | ✅ complete and merged — 2026-10-11; all four steps completed both independent review rounds; [merge closeout](#p02d-7-merge-and-closeout-2026-10-11) |

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

**P02d-4 implementation complete — 2026-10-04, unmerged.** All four steps and
their two independent review rounds are complete. The
[delivery record](#p02d-4-step-4-contract-sdk-and-ci) records passing real CI and
verified activation of the sixth required check.
[PR #25](https://github.com/HodeTech/LearnStack/pull/25) awaits maintainer review
and merge. P02d-5 is next; its decision pass has not started.

**P02d-4 merge complete — 2026-10-08.** The preceding notes record pre-merge
milestones. P02d-4 is now closed through
[PR #25](https://github.com/HodeTech/LearnStack/pull/25); its
[merge closeout](#p02d-4-merge-and-closeout-2026-10-08) records final verification.
Phase 02d remains in progress. P02d-5 is next; its decision pass has not started.

**P02d-5 preparation — 2026-10-08.** The
[decision package](#p02d-5-decision-package-2026-10-08) and
[ADR-0053](../decisions/0053-trusted-public-server-rendering.md) are Proposed after
baseline review. The preceding merge note records the state at closeout. No P5
gate is Accepted, no implementation is started and maintainer approval is pending.

**P02d-5 acceptance — 2026-10-08.** The maintainer approved ADR-0053 and the
[decision package](#p02d-5-decision-package-2026-10-08) after the external-review
corrections. Its named gate parts close; implementation starts on development,
with four steps and two fresh review rounds per step. P6/P7 gates remain open.

**P02d-5 implementation complete — 2026-10-09, unmerged.** All four steps and
their two independent review rounds are complete; confirmed fixes are verified and
committed. The [packet closeout](#p02d-5-packet-closeout-2026-10-09) records the final
local verification and scope. P02d-5 awaits maintainer PR review and merge. P02d-6's
public-renderer decision pass is next; no P6/P7 gate is closed by this delivery.

**External-review remediation — 2026-10-09.** ADR-0054 is Accepted and its runtime
and proof corrections are delivered. The [current remediation record](#p02d-5-external-review-remediation-2026-10-09)
owns the five steps, their reviews and final PR/CI readiness. Earlier delivery
notes remain historical; PR #26 is not merged.

**P02d-5 merge complete — 2026-10-09.** The preceding notes record pre-merge
milestones. P02d-5 is now closed through
[PR #26](https://github.com/HodeTech/LearnStack/pull/26); its
[merge closeout](#p02d-5-merge-and-closeout-2026-10-09) records the final head,
verification and merge. Phase 02d remains in progress. P02d-6's public-renderer
decision pass is next; P6/P7 implementation has not started.

**P02d-6 preparation — 2026-10-09.** The
[decision package](#p02d-6-decision-package-2026-10-09) and
[Proposed ADR-0027](../decisions/0027-frontend-i18n.md) are ready for maintainer
review. All P6 gate parts remain unaccepted. The package explicitly proposes the
404 navigation/status tradeoff exposed by the pinned framework experiment;
implementation waits for approval, not an assumed framework guarantee.

**P02d-6 acceptance — 2026-10-09.** The maintainer approved revised ADR-0027 and
the complete [decision package](#p02d-6-decision-package-2026-10-09), including
the local 307→404 URL change, HTTP 200/noindex content-failure states and four
implementation steps. The acceptance commit closes its named gate parts and
reconciles their carriers before code. Every step uses commits and two fresh
independent review rounds. Development remains the working branch.

**P02d-6 implementation complete — 2026-10-10, unmerged.** All four steps and
both independent review rounds per step are complete. The
[packet closeout](#p02d-6-packet-closeout-2026-10-10) records automated verification,
manual browser observations and the maintainer's passing VoiceOver smoke.
P02d-6 awaits maintainer PR review and merge. P02d-7's demo, browser/Lighthouse
harness and phase-exit decision pass are next; they have not started.

**P02d-6 merge complete — 2026-10-10.** The preceding notes record pre-merge
milestones. P02d-6 is now closed through
[PR #27](https://github.com/HodeTech/LearnStack/pull/27); its
[merge closeout](#p02d-6-merge-and-closeout-2026-10-10) records the final head,
verification and merge. Phase 02d remains in progress. P02d-7's demo, Lighthouse
and phase-exit decision pass is next; implementation has not started.
