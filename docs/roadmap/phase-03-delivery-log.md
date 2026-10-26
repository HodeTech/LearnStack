# Phase 03 — delivery log

The dated record of
[Phase 03: Identity Domain, Authorization, and Admin Foundation](phase-03-identity-admin.md).
The phase document holds the plan and Status; this log holds dated records under
[ADR-0069](../decisions/0069-phase-documents-and-delivery-logs.md).

Entries are append-only after their packet ships. An explicitly Accepted decision
package is normative and its gate links the exact answer. Preparation records are
not acceptance of implementation choices. Navigate by the generated index below.

<!-- BEGIN GENERATED INDEX: scripts/delivery-log-index.py — run `make docs-index`; do not edit by hand -->

**Index**

- [P03-0 — Entry preparation](#p03-0--entry-preparation) — 83 lines
  - [Entry preparation (2026-10-25)](#entry-preparation-2026-10-25) — 81 lines
- [P03-1 — Identity authority and membership foundation](#p03-1--identity-authority-and-membership-foundation) — 1,904 lines
  - [P03-1 proposed decision package (2026-10-25)](#p03-1-proposed-decision-package-2026-10-25) — 130 lines
  - [P03-1 proposal review round 1 (2026-10-25)](#p03-1-proposal-review-round-1-2026-10-25) — 35 lines
  - [P03-1 proposal review round 2 (2026-10-25)](#p03-1-proposal-review-round-2-2026-10-25) — 36 lines
  - [P03-1 proposal readiness (2026-10-25)](#p03-1-proposal-readiness-2026-10-25) — 35 lines
  - [P03-1 maintainer review disposition (2026-10-25)](#p03-1-maintainer-review-disposition-2026-10-25) — 34 lines
  - [P03-1 revised decision package (2026-10-25)](#p03-1-revised-decision-package-2026-10-25) — 114 lines
  - [P03-1 revised proposal review round 1 (2026-10-25)](#p03-1-revised-proposal-review-round-1-2026-10-25) — 16 lines
  - [P03-1 revised proposal review round 2 and readiness (2026-10-25)](#p03-1-revised-proposal-review-round-2-and-readiness-2026-10-25) — 30 lines
  - [P03-1 second maintainer review and revised package (2026-10-25)](#p03-1-second-maintainer-review-and-revised-package-2026-10-25) — 99 lines
  - [P03-1 second revision review round 1 (2026-10-25)](#p03-1-second-revision-review-round-1-2026-10-25) — 23 lines
  - [P03-1 second revision review round 2 and approval readiness (2026-10-25)](#p03-1-second-revision-review-round-2-and-approval-readiness-2026-10-25) — 28 lines
  - [P03-1 joint acceptance and Step 1 (2026-10-25)](#p03-1-joint-acceptance-and-step-1-2026-10-25) — 41 lines
  - [P03-1 Step 1 review round 1 (2026-10-25)](#p03-1-step-1-review-round-1-2026-10-25) — 21 lines
  - [P03-1 Step 1 review round 2 and closeout (2026-10-25)](#p03-1-step-1-review-round-2-and-closeout-2026-10-25) — 16 lines
  - [P03-1 Step 2 implementation and local qualification (2026-10-25)](#p03-1-step-2-implementation-and-local-qualification-2026-10-25) — 36 lines
  - [P03-1 Step 2 review round 1 (2026-10-25)](#p03-1-step-2-review-round-1-2026-10-25) — 20 lines
  - [P03-1 Step 2 review round 2 and closeout (2026-10-25)](#p03-1-step-2-review-round-2-and-closeout-2026-10-25) — 18 lines
  - [P03-1 Step 3 global storage and common audit implementation (2026-10-26)](#p03-1-step-3-global-storage-and-common-audit-implementation-2026-10-26) — 66 lines
  - [P03-1 Step 3 final local pre-review controls (2026-10-26)](#p03-1-step-3-final-local-pre-review-controls-2026-10-26) — 25 lines
  - [P03-1 Step 3 retained-subject pre-commit correction (2026-10-26)](#p03-1-step-3-retained-subject-pre-commit-correction-2026-10-26) — 12 lines
  - [P03-1 Step 3 existing-adoption scope correction (2026-10-26)](#p03-1-step-3-existing-adoption-scope-correction-2026-10-26) — 24 lines
  - [P03-1 Step 3 review round 1 and corrections (2026-10-26)](#p03-1-step-3-review-round-1-and-corrections-2026-10-26) — 46 lines
  - [P03-1 Step 4 ordered-lock timing proposal (2026-10-26)](#p03-1-step-4-ordered-lock-timing-proposal-2026-10-26) — 39 lines
  - [ADR-0074 proposal review round 1 (2026-10-26)](#adr-0074-proposal-review-round-1-2026-10-26) — 12 lines
  - [P03-1 Step 3 review round 2 and local closeout (2026-10-26)](#p03-1-step-3-review-round-2-and-local-closeout-2026-10-26) — 40 lines
  - [ADR-0074 proposal review round 2 and readiness (2026-10-26)](#adr-0074-proposal-review-round-2-and-readiness-2026-10-26) — 16 lines
  - [ADR-0074 maintainer review correction and acceptance (2026-10-26)](#adr-0074-maintainer-review-correction-and-acceptance-2026-10-26) — 52 lines
  - [P03-1 Step 4 — Contextual foundation and owned seed (2026-10-26)](#p03-1-step-4--contextual-foundation-and-owned-seed-2026-10-26) — 90 lines
  - [P03-1 Step 4 review round 1 (2026-10-26)](#p03-1-step-4-review-round-1-2026-10-26) — 25 lines
  - [P03-1 Step 4 review round 2 and diagnostic correction (2026-10-26)](#p03-1-step-4-review-round-2-and-diagnostic-correction-2026-10-26) — 47 lines
  - [P03-1 Step 4 exported trace correction (2026-10-26)](#p03-1-step-4-exported-trace-correction-2026-10-26) — 37 lines
  - [P03-1 Step 4 local closeout (2026-10-26)](#p03-1-step-4-local-closeout-2026-10-26) — 22 lines
  - [P03-1 Step 5 — Existing-binding API admission (2026-10-26)](#p03-1-step-5--existing-binding-api-admission-2026-10-26) — 85 lines
  - [P03-1 Step 5 review round 1 and read-diagnostic correction (2026-10-26)](#p03-1-step-5-review-round-1-and-read-diagnostic-correction-2026-10-26) — 43 lines
  - [P03-1 Step 5 review round 2 and local closeout (2026-10-26)](#p03-1-step-5-review-round-2-and-local-closeout-2026-10-26) — 30 lines
  - [P03-1 Step 6 — Complete qualification inventories (2026-10-26)](#p03-1-step-6--complete-qualification-inventories-2026-10-26) — 46 lines
  - [P03-1 Step 6 review round 1 and carrier correction (2026-10-26)](#p03-1-step-6-review-round-1-and-carrier-correction-2026-10-26) — 36 lines
  - [P03-1 Step 6 full ordinary failures and bounded corrections (2026-10-26)](#p03-1-step-6-full-ordinary-failures-and-bounded-corrections-2026-10-26) — 78 lines
  - [P03-1 Step 6 review round 2 and complete parent-stack correction (2026-10-26)](#p03-1-step-6-review-round-2-and-complete-parent-stack-correction-2026-10-26) — 41 lines
  - [P03-1 Step 6 correction approvals and first hosted assignment (2026-10-26)](#p03-1-step-6-correction-approvals-and-first-hosted-assignment-2026-10-26) — 48 lines
  - [P03-1 Step 6 measured timing revision (2026-10-26)](#p03-1-step-6-measured-timing-revision-2026-10-26) — 39 lines
  - [P03-1 Step 6 qualification closeout and PR handoff (2026-10-26)](#p03-1-step-6-qualification-closeout-and-pr-handoff-2026-10-26) — 73 lines
  - [P03-1 PR 38 review corrections (2026-10-26)](#p03-1-pr-38-review-corrections-2026-10-26) — 98 lines

<!-- END GENERATED INDEX -->

## P03-0 — Entry preparation

### Entry preparation (2026-10-25)

**Scope.** Reconcile the phase plan and its connected contracts before the first
identity, membership or authorization writer. No runtime, schema, provider realm,
Hub crossing or deployment activation is changed. No Phase 03 implementation
decision package is Accepted in this entry.

**Verified baseline.** Preparation starts from development at
`819ef226053f06ffcf787ceaceefa09781240f32`, after completed Phase 02b. Relevant
production seams were rechecked against that source:

- `TenantTokenProfile` requires stable UUID actor claims; the native provider
  requires actor and tenant before callback publication. Development reconciliation
  only installs declared seed identity attributes, not arbitrary runtime accounts.
- `TenantResolverMiddleware` asks membership before installing tenant context.
  `AuthorizationBehavior` still passes through before the ordinary transaction.
  Membership and Platform admission defaults deny everyone.
- The authenticated native caller admits only AccountSite. Existing provider user
  attributes emit active scope; ordinary BFF refresh preserves the original scope.
- The shared JSON Schema profile requires nonempty root properties. Current
  Customization definitions are versioned; no custom-field aggregate is delivered.
- Platform scope has a separate bypass connection and no admitted actor consumer;
  ordinary event/job execution supplies no privileged redaction authority.
- The committed gateway scaffold has priorities and no Hub-secret reference, but
  retains version wildcard paths. Architecture 30 already documents their remedy.

**Reconciliation.** Each first-writer question now has a packet, vehicle and
observable proof in the phase's decision/delivery/criterion tables. Detailed
answers remain with their last responsible consumer under
[Decision Timing](README.md#decision-timing).

| Concern | Canonical disposition |
|---|---|
| Actor bootstrap before accepted login | G1 / P03-1; real nonseed and concurrent mapping proof in C01 |
| Pre-context membership/permission access | G2 / P03-1 contract and P03-2 execution; exact grants/setters and C05 |
| Platform population, surface and actor chain | G13 / first privileged consumer; preserve ADR-0036 and demand-gated Hub boundary |
| Per-session scope switching | G4 implications before shared schema, transition in P03-3; independent real refresh proof |
| Grant ceilings, seed defaults and revocation | G5 / foundation and authorization; Standard 19 identifies unresolved target choices |
| Membership/profile/lifecycle/global disclosure | G3 before migration; conceptual ER no longer implies a single role or cross-module Organization FK |
| Protected native/API operations | G6 / policy then transport; exact SDK/CSRF/ETag/replay/cache boundary in C06 |
| Invitation identity, races, atomicity and delivery | G8 before its first affected schema/writer; usable bounded handoff owned here, full engine remains Phase 08a |
| Subject/org/recipient DSAR authority | G15; RLS remains tenant/org isolation and never substitutes for subject predicates |
| Custom-field revision/classification and schema profile | G9; Customization owns definitions; unsafe global User helper and unversioned illustrative DDL removed |
| Provider-event topology and attribution | G10; authenticated durable intake, stable dedup and outbox-backed facts, no direct business publication |
| Tenant federation and account linking | G12; broker/email alone grants no membership, with scoped provider ownership/recovery proof |
| DSAR artifact/workflow/global closure | G16; private artifact and job prerequisites, retained authority, closure fence and privileged reconciliation |
| Denied/sensitive-read audit and enrichment | G7; actual HTTP/application evidence ownership, trusted request port and locators |
| Packet/register/exit completeness | Twelve packets, nineteen gates, D01–D16 and C01–C21 with inherited catalogue obligations |
| Gateway correction boundary | G18; explicit version paths and inactive scaffold correction here, reviewed public allowlist/lint/activation Phase 11 |
| Stale ownership/PII/target descriptions | Phase and Customization architecture reflect the delivered marker and Membership target; G19 owns first-PII census |
| HMAC threat statement | Gateway architecture distinguishes forged body signatures from independent JWT/mTLS admission |

**Validation.** Documentation-only checks pass on the reconciled sources:

- `make lint-docs`: 2,399 relative fragment links resolve, ADR-0069 plan/log/status
  structure conforms and three generated delivery-log indexes are current.
- Existing guard controls: 9 fragment, 12 roadmap-structure, 9 delivery-index and
  5 workflow-link tests pass, 35 in total.
- The changed-document file-target sweep resolves 1,014 relative links across
  17 Markdown files. Gate/deliverable/criterion identities are complete and unique;
  all nineteen implementation gates remain Open.
- `git diff --check` and the added-prose wrap check pass. No runtime/configuration
  file or Accepted ADR is edited; all changed-document references resolve to
  canonical repository sources.

The semantic self-review checks realm/actor boundaries, earliest schema/adapter
consumers, scope/filter/write-guard separation, aggregate/module ownership, audit
and privacy evidence, external-effect recovery and complete phase-exit coverage.
No backend/frontend/provider/browser suite is rerun: this record qualifies the
document preparation, not the undelivered runtime behavior in the criteria.

**Next boundary.** P03-1 begins with explicit acceptance of G1/G2/G3/G19 and the
schema-affecting G4/G5 parts. It includes G8/G16 only where their answers affect
the first identity tables. No later feature table or dormant writer may preempt
its own gate. Selecting a provider/platform mechanism that changes an Accepted
decision needs its correct disclosure or successor before implementation.

**Preparation versus delivery.** The document plan is ready for that decision
pass. Nonseed bootstrap, live authorization, operator admission and DSAR runtime
proof remain required implementation work; this entry claims none of them passed.

## P03-1 — Identity authority and membership foundation

### P03-1 proposed decision package (2026-10-25)

**Status: Proposed.** The maintainer requested P03-1 on `development`, with a
complete prior-document review, upfront decisions, approval before a new ADR is
implemented, and two fresh independent review rounds after each committed step.
No gate is Accepted here. No runtime, realm, schema, permission or grant changes.

**Source.** Review starts at `46da68132987759b7ca6a19230b525b5485c7441`, the
existing Phase 03 entry-preparation commit. Its predecessor closes merged Phase
02b. Preserve both commits and the unrelated untracked worktree directory.

**Problem and scope.** Establish durable application identity and contextual
membership/role foundations before live authorization. Existing strict bearer
validation and native BFF custody are inputs, not bootstrap or grant mechanisms.
P03-1 supplies D01/D08 and the foundation portions of C01/C02/C03/C10. P03-2 owns
live resolver/permission execution; P03-3 owns administration and usable scope
discovery/switching. This package does not accept their still-open policies.

#### Documentation and source review

Read the working agreement and `implement-task`/`start-task`, product vision,
MVP/roadmap/principles/glossary, current phase/entry record, identity and module
boundaries, database/tenant/permission/audit/privacy authorities and related
Accepted decisions. Trace current API/BFF/seed, transaction/filter/RLS, audit
capture/error sinks and architecture-test seams. Three independent read-only
audits separately cover G1/G2, schema/lifecycle/delegation and G19/privacy.
These are static design reviews, not provider/database/runtime qualifications.

| Verified fact | First-writer consequence |
|---|---|
| Identity's four packages contain assembly markers only | No existing identity schema/grant behavior decides the model |
| API and BFF require an actor claim; only four development personas receive one | A nonseed actor needs an explicit bootstrap contract before accepted login |
| Membership is asked before tenant context; permission behavior precedes transaction | No ordinary repository, hidden setter or Platform bypass can fill this gap |
| Four login roles and eight logical tenant setters are closed | New subject owners/candidate reader need explicit authority and census |
| Global User has no tenant; current global audit rejects this write shape | Decide global self RLS and same-transaction central audit, not a fake tenant |
| Tenant filters do not exclude deleted rows or carry app.scope; org validation checks belonging | Lifecycle predicates and eligibility cannot be inferred from those mechanisms |
| SeedData preserves four actor identities; it declares no application memberships | Adopt actual provider bindings explicitly, then create only declared grants |
| Snapshot PII markers/name redaction/LS0002 have bounded distinct scopes | Add a semantic mapped-property census and fixed writer diagnostics |
| Existing audit/event references survive six-column redaction | Decide anti-recreation/fence now; do not claim completed anonymisation |

Four current non-ADR carrier ambiguities are corrected in this preparation:
Module Boundaries now distinguishes native BFF custody from Identity and central
mutation audit from provider mirroring; Data Protection places tenant restriction
in membership context, never User; Audit architecture/catalogue distinguish
P03-1's User prerequisite from P03-10's locator/privileged erasure delivery.
Accepted ADR bodies and all historical delivery entries remain untouched.

#### Proposed answers and exact split gates

| Gate/part | Proposed answer / vehicle | Remaining named obligation |
|---|---|---|
| G1 | ADR-0072: application allocates one permanent actor from verified issuer/opaque sub; explicit bootstrap; trusted seed adoption | P03-4 completes real invitation enrollment; no email merge/provider Admin write |
| G2 storage/reader contract | ADR-0072 global self owner; ADR-0073 app-role candidate read owner, new enumerated setter and step-5/6 handoff | P03-2 execution, live rows 7/10/14 and protected 6/9; claim-only mismatch evidence before activation |
| G3 | ADR-0073: Membership contains one contextual profile and multiple assignments; null-safe natural key, lifecycle/successor and finite role scopes, including the explicit Organization Learner variant | P03-3 field/HTTP/admin/organization operations; P03-5 dynamic fields |
| G4 shared implications | No global active-scope column; actor-only bootstrap does not publish a tenant BFF session | P03-3 usable discovery, per-session issuance, switch/refresh/logout races and real A/B proof |
| G5 schema/bootstrap | Scoped permission identity; code definitions, finite initial grants, disabled later additions, last-admin serialization | P03-2 permission enforcement/delegation/denial and revocation execution; P03-3 UI |
| G7 only new global mutation | ADR-0072: first User and central MUST row share one app-role transaction; confined same-subject failed/indeterminate reconciliation preserves ADR-0033 | P03-2 normal HTTP/application denial/enrichment; no general standalone budget |
| G8 shared shape | No Invitation table/implicit multi-root exception; Membership natural key supports later reconciliation | P03-4 acceptance/delivery/aggregate atomicity decision before writer |
| G16 shared invariants | Permanent binding/tombstone, global admission lock, no implicit global closure on tenant erasure | P03-10 authority, holds, participants, transformations, artifacts, provider finalization and completion |
| G19 | ADR-0073: explicit mapped-property classification, fixed diagnostics and sink/capture canaries | P03-5 revision-aware dynamic values; P03-10 locator/erasure fulfillment |

The new global mutation slice replaces an audit restriction through its named
ADR. It adds no operator population, Platform permission, Hub crossing or provider
ingress; G10/G13 need not move into bootstrap under this selected design. If
implementation discovers such a dependency, its affected gate closes before code.

#### Maintainer decisions requested together

1. Adopt ADR-0072, knowingly replacing the required provider `user_id` with an
   application-owned binding and an optional consistency claim. Keep all other
   strict token checks; ordinary reads never create Users. This changes the API/
   BFF contract and introduces authenticated binding-store availability cost.
2. Admit the exact global-self POST, transactional global User/audit class and
   confined non-success reconciliation, subject RLS and owner-read closure guard.
   Linkable audit subject keys survive under security retention even without a
   User. No fifth login/bypass, operator grant, public directory or tenant
   authority is implied.
3. Adopt ADR-0073's contained membership/profile/grant model, null-safe lifecycle,
   scoped finite permission manifests and concurrent last-admin invariant.
4. Approve permanent anti-recreation binding retention as a first-schema choice.
   It retains linkable identifiers; P03-10 still owes the separate privacy/
   transformation decision. Approve no automatic new-permission grants on rerun.
5. Adopt the complete classification/diagnostic boundary and six implementation
   steps below. The two ADRs and this package are the approval surface; no
   separate runtime policy is hidden in a review receipt.

#### Six implementation steps

| Step | Concrete result | Main surfaces | Required validation before closure |
|---|---|---|---|
| 1 | Accept decisions, append exact dated disclosures, register obligations and reconcile canonical authorities | ADR-0003/0017/0033/0036/0040/0044/0064/0070; Standards 05/11/18/19/21; Identity spec and applicable architecture/glossary | Parser/status, exact clauses/carrier audit, whole-tree docs lint, no Accepted-body rewrite |
| 2 | Complete typed property classification census and safe diagnostic boundary before identity PII writes | SharedKernel metadata; production EF models; module PII/audit specs; observability/error tests | Nonempty model census; planted omission; real sink/capture positive and canary controls; existing redaction remains |
| 3 | Global User/binding storage, confined self owner, atomic central audit/non-success reconciliation and closure guard | Identity Domain/Infrastructure; common transaction/audit contracts; migration chain and fixtures | Real app-role RLS and uniqueness races; missing/mismatched success audit, rollback/loser/unknown-COMMIT/standalone failure, fresh post-lock/incompatible-isolation and owner/pooled/cancellation controls |
| 4 | Membership/Profile/Role foundation and semantic identity/member seed adoption | Identity packages/contracts; Tenancy lifecycle contract; Seeder/shared seed-demo handoff; role/PII matrices | Null-key/scope/child/audit/lifecycle and last-admin races; real warm provider seed readback, preserved credentials and no-write rerun |
| 5 | Explicit bootstrap HTTP, binding-based ordinary identity and coordinated native BFF/SDK activation | API authentication/global-self surface; generated SDK; native provider/session response owners; OpenAPI | Real actorless Keycloak tokens/native HTTPS; binding matrix; unknown-host/quota/body/protocol/late-publication controls; unchanged anonymous flow |
| 6 | Complete integrated qualification, corpus/CI assignment and source-bound closeout | Actual test owners; bounded shard plan if discovery changes; phase Status/log; reviewable PR description | Relevant complete backend/frontend suites, real provider/browser slice, full complementary A/B CI under existing ceilings/names, no skips/empty proof |

Each step is committed on `development`, receives two independent fresh review
rounds, and commits fixes only for verified findings. New reviewers inspect the
fixed source; receipts name commits, scope, commands/results and limitations.
Continue automatically between accepted steps. A new architectural decision or
verified blocker is surfaced before its dependent work. No historical review
claim is silently rewritten and no passing suite substitutes for the other round.

Implementation dispatches `add-tenant-owned-entity` only for contextual tables,
`add-ef-migration`, `add-mediatr-handler`, `add-audit-coverage`, `add-permission`,
`add-architecture-test`, `add-integration-test`, `seed-tenant` and the relevant
native/SDK workflow. Global User follows its accepted class, not a tenant template.
`run-tests-locally`, `standards-check` and `commit-and-pr` supply verification and
commit gates. No runtime provider SDK is needed by the selected binding strategy;
its conditional backfill is not claimed complete.

#### Proof boundaries and carry-forward

Foundation proof must use real PostgreSQL with `learnstack_app` for ordinary
isolation, pinned Keycloak for actual actorless/seed issuance and native HTTPS
for BFF publication. Structural checks have planted dirty/clean controls;
behavioral failures must identify the mechanism they exercise. Register actual
production model/route/adoption subjects, not test-only placeholders.

No generic protected route is admitted while AuthorizationBehavior remains a
shell. No invite, federation, operator, DSAR, global-profile editor, full Studio,
dynamic-field schema or distributed session store is delivered by this package.
Their first-writer contracts remain assigned in the phase register; no required
P03-1 prerequisite is relabeled as a later production-hardening task.

**Validation boundary.** The review and readiness entries below own completed
mechanical/static results. There is no backend/frontend/provider execution or
implementation qualification in this documentation-only decision pass.

### P03-1 proposal review round 1 (2026-10-25)

**Reviewed source:** `8cf352f40e57766206ea81ec2a1dd803242cbf46`. Two fresh,
independent reviewers use GPT-6 Astra / xhigh for authority/global SQL/audit and
GPT-6.1 Sol / xhigh for the membership/privacy/step model. Both run read-only
reviews, inspect current source and Accepted authorities, and execute no runtime
tests or provider writes. This records their actual scope, not implementation
qualification or maintainer acceptance.

Two Major proposal gaps are verified and corrected before the next round:

- The BFF currently rejects missing tenant before bootstrap. ADR-0072 now
  explicitly admits a strict pre-session provider-subject projection with actor
  and tenant optional. It requires the ordinary positive AccountSite scope
  witness before session publication and a real actorless/tenantless control.
- The declared Yoga learner has organization scope, but the role proposal had
  no explicit Organization Learner shape. ADR-0073 now selects that finite
  scope variant, exact assignment compatibility and other-branch denial, without
  silently making the learner a tenant-wide member.

Two design ambiguities are also resolved, without claiming observed runtime
defects: creation-audit reciprocity is an insertion-time deferred witness, not a
perpetual cross-module FK blocking retention purge; the User fence uses a named
transaction advisory lock and exact owner-policy read, not an unspecified row
lock requiring a second policy. A separate state-only Boolean read supports
last-admin eligibility; later closure joins the same tenant invariant locks.
The canonical new proof names and planned owners are now explicit.

Mechanical checks on the initial proposal pass: `make lint-docs` resolves 2,408
fragment links, roadmap structure and three generated indexes; the changed-doc
file sweep resolves 1,013 relative targets; both ADRs parse Proposed. Existing
ADR/parser/fragment/roadmap/index controls pass 37 tests in total. No application
suite is rerun for this documentation-only review. Corrected-source checks and
a second independent round remain required before requesting approval.

### P03-1 proposal review round 2 (2026-10-25)

**Reviewed source:** `c9a969f6`. Two different fresh reviewers use GPT-6.1 Sol /
xhigh for authority/SQL and GPT-6 Astra / xhigh for model/privacy/audit. Both
independently verify the first round's tenantless pre-session, scoped Learner,
insertion-only witness and advisory-lock corrections. Both remain read-only;
neither runs application/provider/database tests or changes source.

Two additional Major contract gaps are verified and corrected:

- Lock ownership alone does not supply a fresh snapshot. User/tenant invariant
  writers and database guards now require READ COMMITTED, refuse incompatible
  isolation and read again after the awaited lock in a fresh VOLATILE query.
  Held-lock competing-commit and stale-snapshot controls are mandatory.
- ADR-0033 requires failed/indeterminate MUST reconciliation, while the initial
  global proposal admitted only a transactional success row. ADR-0072 now names
  the central global non-success seam, same verified subject, exact duplicate
  witness, loser/rollback/unknown-COMMIT/failure disposition, cleanup ownership
  and ADR-0033 disclosure. Its immutable subject-key attribution is explicitly
  linkable PII under the existing security-audit retention; no new budget waives
  those rows. Normal known-binding reads stage no mutation/audit intent.

Final source reconciliation preserves ADR-0044 § 5's fresh standalone timestamp
and possible indeterminate pair, instead of introducing an unnecessary global
key/timestamp exception. User's success reference remains insertion-only; a
duplicate must match the exact operation/target/subject/outcome, and a durable
failed row is never evidence that User creation committed.

The subject EF filter and direct baseline catalogue ownership are also explicit;
no tenant override lookup or new context bypass is inferred for global rows.
The existing native API response budget remains ten seconds; cancellation ends
waiting/publication, not proof that centrally owned audit cleanup completed.
The corrected proposal is rechecked against source and the PostgreSQL 18
isolation/volatility/policy contracts. A targeted correction verification and
final mechanical checks follow before maintainer approval is requested.

### P03-1 proposal readiness (2026-10-25)

**Source reviewed:** `037fd56f`, after first draft `8cf352f4`, round-one fixes
`c9a969f6` and round-two fixes `d0fd67de`. Three initial read-only audits establish
the prior-document/source gaps. Two fresh reviewers per full round examine the
proposal; the second-round reviewers then independently verify the targeted
corrections at the final source. That verification is not a third full round.
Their reviewed scopes report no remaining concrete actionable finding. Neither
static review nor these documentation checks supplies runtime qualification.

The new authorities remain Proposed:

- [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md): application-
  owned binding, exact global-self operation, atomic global audit and confined
  failed/indeterminate reconciliation, seed preservation and closure fence.
- [ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md):
  contained contextual membership state, finite scoped grants/Organization
  Learner, last-admin/read-committed invariants and complete PII/diagnostic census.
- The [six-step package](#p03-1-proposed-decision-package-2026-10-25) identifies
  the user decisions, exact gate parts, proof owners and later packet boundaries.

The final audit preserves P03-0 history byte-for-byte outside the generated index,
changes no Accepted ADR/runtime/configuration file, leaves all nineteen gates
Open and stays on `development`. No foreign process/worktree or provider state
is changed. Commits are local; no PR or push is performed by this preparation.
The user's explicit approval-before-new-ADR requirement is the remaining gate;
acceptance/disclosures and all dependent implementation await that answer.

Final document validation is recorded on this proposal source before commit:
2,413 whole-tree fragment links, roadmap structure and three current indexes;
1,258 changed-document relative targets with none missing; both ADRs parse
Proposed; added ADR prose, whitespace and private-path residual checks pass.
Existing parser/fragment/structure/index guard controls pass 37 tests. No
application test is presented as run or passing in this proposal preparation.

### P03-1 maintainer review disposition (2026-10-25)

**Reviewed source:** `9211789342fd15c0a9a1cafaeb80ea7b7ca345d8`. The maintainer
returned review findings rather than approval. Both ADRs remain Proposed; all
nineteen gates remain Open. Earlier source-bound reviewer conclusions remain
historical. This follow-up reopens their readiness conclusion; it does not claim
the earlier reviewers verified findings they did not report.

Three read-only source verifications cover global SQL/auth/instruments,
membership/bootstrap/manifests, and privacy/retention/operational carriers.
They inspect current code and authorities, not instructions in review data.
No provider/database/application tests or state changes occur in that audit.

| Review items | Verified disposition / revision |
|---|---|
| F1/K1, Y4 | Valid incomplete global write/read contract and contradictory unmodified-policy wording. Proposed Standard 05 now declares seventh class, exact audit policy arms, owner evidence functions, grants and central seams. PlatformSentinel's second population is explicit, not operator authority. |
| F2 | Host-lookup definer rejection is real but host-specific, not a universal impossibility. Explicitly choose limited migration-owner execution with owner SELECT policies; compare projection/fifth-role alternatives. Migration credentials stay outside runtime. |
| K2/O7 | Key already uses issuer/sub, but its typed consumer requires actor. New provider projection/feature/quota handoff before binding is explicit; API/BFF sub limit is 255. This is a proposed replacement, not a claim current code supports it. |
| K3/Y3/F10 | New ADRs already supply a superseding instrument, but affected clauses/Amendments were underspecified. Append-ready drafts below name each bounded change, separate navigation and keep old bodies/metadata intact. No Amendment number is reserved. |
| F3/Y5 | Replace independently configured per-org Roles with tenant-wide capability templates; organization lives on Membership assignments. Exact contextual schema/FKs/guards are in the Proposed Identity spec. No per-org Role initialization writer is needed. |
| Y1/Y2/O8 | Define staged seed Role roots then first-admin Membership, no cross-module transaction. First/final-admin predicate covers Trial/Active/Suspended and every Membership mutation. Production first-admin belongs P03-9/G13; Tenancy archive race remains P03-3 before its writer. No writable bootstrap flag/table is invented. |
| Y7 | Include reactivation and authority-increasing assignment changes in User-before-tenant admission fencing; DB lifecycle prevents resurrection. Current global eligibility already denies closed accounts, so this was not demonstrated shipped access bypass. |
| F4 | Introduce stable authority registration, config-drift refusal and named P03-9 rebind owner before rename; no silent new identity domain or email-based continuity. |
| F5/O1/O2 | Choose permanent digest/actor tombstone now, with raw subject nullable and removed at Closed. No per-User raw issuer or unused email. Digest remains access-controlled, potentially guessable PII; metadata remains immutable under security retention. P03-10 locates failed attempts even without User. |
| F6 | Serialize subject before allocation/intent; benign waiters return the winner without failed mutation audit. Genuine failures/unknown COMMIT keep MUST reconciliation. Membershipless identity is allowed without verified email; provider registration cardinality cost is explicit. |
| F7 | 401 on ordinary binding outage preserves Accepted 0064/0071; mandatory 503/witness is not a bug fix. Compare and decline that separate policy change. Add closed diagnostic reasons and typed bootstrap 503 translation instead. |
| F8/O4/O5 | Proposed Standard 19 enumerates five production-command permission pairs, eight templates/seven role keys and exact two-pair kernel; one versioned compiled source generates verified guard artifacts. Registry scope identity and Organization Learner are explicit; later routes remain closed. |
| F9/O9 | Remove unused email/verification columns and parsing from P03-1. First proven mirror consumer, currently P03-6/G10, owns their later schema/freshness. |
| O3 | Seed already refused unauthorized repair by decision. Pin existing manifests/fingerprints; registry additions do not change v1 completion. Conflict stops later seed acts, leaving earlier commits. A destructive reset is not introduced. |
| O6/census ownership | Prior draft already named Step 2 and DataProtectionConventionTests; timeline was not TBD. Strengthen per-module declaration/coordinator ownership and automatic complete-model diff. Distinguish planned/unregistered, Registered, Awaiting backfill and actual Implemented states. |
| Missing contention detail | Specify bounded transaction lock waits and identity-free closed telemetry; Phase 11 owns field thresholds/load qualification. |
| Global-table glossary allegation | Not reproduced: the current term does not impose bypass-only access. Preserve it; fix actual AuditEntry/sentinel/class/method claims on acceptance. |
| Stale generated index | Reproduced at 92117893. Final readiness prose changed its line count without regeneration; the earlier lint claim did not qualify that final commit. Regenerate after the last edit and validate the committed index. |

### P03-1 revised decision package (2026-10-25)

**Status: Proposed; no maintainer acceptance.** This package replaces the earlier
draft as the current approval surface, not its historical review entries.
Joint [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md),
their clearly Proposed canonical annexes/specs and these Amendment drafts are
reviewed together. Existing Adopted standards and Accepted ADR text stay active
until explicit approval. No runtime/configuration/schema file changes here.

#### Approval surface and owners

| Contract | Canonical proposed owner / decision |
|---|---|
| Global class, anchor, key, grants, RLS/evidence reads, lock limits | [Standard 05](../standards/05-database.md#proposed-p03-1-identity-storage-annex): one authority anchor, digest/actor tombstone, no raw subject at Closed; enumerated owner privilege and two global audit seams |
| Provider-subject credential/quota and exact route | [Standard 11](../standards/11-security.md#proposed-p03-1-identity-admission-annex): no bearer → 404, invalid present → 401; quota before binding; ordinary outage uniform 401, exact bootstrap storage 503 |
| Contextual tables/first-admin/lifecycle | [Identity spec](../modules/identity/README.md): one Membership root and tenant-wide Role templates, guarded seed-only commands, no new ADR-0042 exception or bootstrap flag |
| Exact roles/permissions/kernel/upgrades | [Standard 19](../standards/19-permissions.md#proposed-identity-foundation-contract): five scoped pairs, finite v1 manifests, no new-key automatic grants; generated SQL/compiled-source equality |
| PII and retained-copy purpose | [Data Protection](../architecture/23-data-protection.md#proposed-identity-foundation-and-classification-ownership): per-module declarations, complete automated model diff in Step 2, explicit snapshot/diagnostic boundaries, Identity closure/Audit participants in P03-10 |
| Module matrices | [Permissions](../modules/identity/permissions.md), [audit](../modules/identity/audit.md): compiled internal seed commands are production subjects, not proof of live endpoint enforcement |

G1/G3/G19 and schema parts of G2/G4/G5/G16 close only on joint acceptance.
G7 closes only the explicit global mutation/reconciliation slice; G2 candidate
reader execution stays P03-2. G8 needs no Invitation table/exception here.
Production onboarding, live permissions, scope transition, provider mirroring,
operator recovery and closure remain with their named packets; none is an
unclaimed P03-1 dependency. No provider ingress or new Platform population is
required by the selected bootstrap mechanism.

#### Append-ready Amendment drafts

These are proposed texts for existing ADRs, not amendments already applied.
At acceptance, use the actual date/next available heading in each target, link
the newly Accepted vehicles, and preserve every original heading/body/metadata.
Append each under that ADR's Amendments; a table entry or the word disclosure
alone does not amend the old authority. No future number is reserved here.

**ADR-0036 — bounded global-self admission and candidate-reader contract.**

> ADR-0072/0073 bound this ADR's ordinary HTTP resolution rule for the exact
> POST /api/v1/account/bootstrap. Known-host credential verification and
> provider-subject quota precede it; missing bearer remains masked 404, invalid
> present bearer 401. Valid subject enters the new global-self owner without
> installing ITenantContext or choosing a tenant. Ordinary reconciliation and
> its rows remain route-independent. The exact GlobalSelfIdentity descriptor is
> separate from AllowsUnresolvedTenantContext; its provisioning allowlist and
> four TenantContext writers do not grow. PreContextIdentityAuthorizationReader
> confirms an already verified actor's candidate tenant/org in a short app-role
> READ ONLY owner; it installs no context. Its execution and real rows 7/10/14,
> protected 6/9 and claim-only mismatch evidence remain P03-2 prerequisites.

**ADR-0040 — global owners, nesting and the ninth logical tenant setter.**

> ADR-0072 adds exact global self-read/bootstrap/seed-adoption/reconcile owners
> with verified identity announcements specified in Standard 05. They connect
> as learnstack_app and never announce PlatformSentinel as tenant. Common frame
> generation, capture and cleanup govern them; tenant/global modes cannot join
> or retarget one another. ADR-0073 accepts the separately named future ninth
> tenant setter PreContextIdentityAuthorizationReader, using learnstack_app
> with exact candidate app.tenant_id/app.organization_id in its own physically
> READ ONLY transaction, closed before the ordinary request owner. The existing
> eight logical paths remain the current implemented set until P03-2 delivers
> and qualifies this adapter. Identity-only announcements are a separate census,
> not hidden tenant setters. No other setter/context writer is admitted.

**Other required dated Amendment bodies:**

| Target | Proposed appended text / instrument |
|---|---|
| ADR-0003 | ADR-0072 admits global self-identity as a seventh class, the identity_authority platform anchor, exact role-qualified global audit/owner-policy arms and named definer functions. Standard 05 is the sole SQL authority; existing tenant/org template and four login credentials remain. This names new owner execution power without runtime owner credentials. |
| ADR-0017 | ADR-0073 clarifies Membership's contained profile/assignments and tenant-wide Role templates: template capability scope is not storage/authoring scope. Organization authority derives only from exact Membership binding; tenant grants plus matching-org grants remain. Organization aggregate/CRUD stays Tenancy-owned. |
| ADR-0033 | ADR-0072 extends central decide/write/reconcile to global identity creation under the named app-role owners, including failed/indeterminate attempts. Success shares the User transaction; fresh standalone timestamps/indeterminate pairs and required failure signals remain. Amendment 5's old platform-only routing does not authorize or substitute for these new global seams. |
| ADR-0044 | ADR-0072 narrowly supersedes the sole sentinel population/writer and unresolved-no-row rules for identity.user.create MUST/SecurityEvent. Exact new app INSERT, owner evidence reads, central pending/reconcile seams and insertion-time witnesses are required. Tenant setters/ordinary writers still reject sentinel; platform entry remains its separate standalone owner. No metadata UPDATE or broad sentinel capability is granted. |
| ADR-0053 | ADR-0072 extends Amendment 11's confined authenticated BFF seam with exact pre-session POST /api/v1/account/bootstrap using the independently verified provider grant and host/visitor hop. The result establishes only matching actor association; AccountSite still witnesses ordinary session scope. No browser credential forwarding, general proxy or change to anonymous public wrappers follows. |
| ADR-0064 | ADR-0072 narrowly replaces required wire actor with verified provider-subject plus existing-binding normalization, and extends GET/HEAD-only surface with exact global-self POST. Quota source moves before binding; actor optionality is no anonymous fallback. Absent-bearer 404 and other strict validation remain. No ordinary permission route or scope authority follows. |
| ADR-0070 | ADR-0072 adds pre-session provider projection with 255-character subject and optional actor/tenant, followed by explicit bootstrap association. Only matching AccountSite publishes ordinary scope. Refresh resolves existing identity without allocation and preserves original claim presence/values and session fences. Actor-only entry publishes no tenant session. |
| ADR-0071 | Navigation only: ADR-0072 preserves the ordinary-read table and uniform 401 despite the additional binding-store uncertainty source. Bootstrap has separate initial storage failures; no new refusal witness, retry or refresh policy is inferred. |

Before dependent code, reconcile Standard 05's six-class, global-index exceptions,
closed GUC-name list, unmodified-audit and migration-never-runtime-execution
statements; Standard 11's owner/census; Standard 19's scoped identity/manifests;
CLAUDE/glossary sentinel
population and IAuditStore XML/method counts. Preserve source-bound current
history; do not edit Accepted bodies as though these decisions were factual
corrections. All new catalogue obligations are planned until actual registration.

#### Revised implementation steps and falsifiable proof

The original six-step dependency order is retained, with the following explicit
corrections; every committed step still receives two fresh independent reviews.

| Step | Required scope / evidence |
|---|---|
| 1 — Acceptance | Joint approval, actual dated Amendments above, promote only approved annexes, authority headers and planned catalogue rows. No dependent code precedes the acceptance commit. |
| 2 — Classification | Each mapped module owns complete declarations; Infrastructure Data Protection coordinator compares real EF properties automatically, including inherited IDs, raw subject and stable linkage. DataProtectionConventionTests and planted neutral-name omission are implemented here before Step 3. Existing snapshots/log/local/Sentry positive-plus-canary controls remain distinct. |
| 3 — Global storage/audit | Sixth Identity migration chain, deploy-only authority installer, subject-key serialization before intent, exact RLS/evidence functions, reciprocal success/failed/unknown-COMMIT/duplicate/standalone-failure controls, READ COMMITTED/fresh post-wait and incompatible-isolation controls. No operator credential in runtime. |
| 4 — Contextual foundation | Five contextual tables, tenant templates/five-pair registry, kernel artifact equivalence, null/scope/FK/lifecycle and reactivation/admin guards. Separate Role-root commits then first-admin Membership; fixed seed admission denied in API, real provider adoption, v1 interrupted/no-write/conflict reruns and preserved actors/credentials. |
| 5 — API/BFF activation | New provider-subject feature/quota handoff before SQL, exact global descriptor and HTTP matrix, 255 sub bound, SDK/native bootstrap, actorless/tenantless real provider and wrong-subject/capacity/cancellation/late-result controls. Anonymous public behavior and ordinary 401/ADR-0071 stay. |
| 6 — Integrated qualification | Complete relevant suites and source-bound shard/discovery accounting, actual PostgreSQL/Keycloak/native/browser slice, required A/B CI without skips or relaxed names/ceilings, updated corpus and reviewable PR. No first-admin production/onboarding, full authorization or closure claim from seed proof. |

`User_Aggregate_Has_No_TenantScoped_Columns` is currently Registered;
`Permission_Definitions_DeclareScope` awaits real backfill. The new classification
and global-isolation controls are planned, not yet registered/implemented. Step 1
registers named owners; Steps 2/3/4/5 supply actual subjects and positive/dirty
controls. Existing Implemented rules retain status with explicit pending
extensions; this proposal does not alter catalogue counts or claim coverage.

The final semantic review must independently assess SQL owner privileges,
authority installation/drift, digest/raw closure contract, input/feature/quota
handoff, generated manifest/kernel equality, staged seed recovery, derived first-
admin predicate and exact predecessor Amendments. Run fragment/file/status/
structure/index/whitespace/private-reference checks after the **last** edit.
Static checks qualify only the proposal; no runtime proof is claimed here.

### P03-1 revised proposal review round 1 (2026-10-25)

**Reviewed source:** `6bff3fe0`. Two fresh read-only reviewers independently
inspect the revised package and current source: global SQL/authentication and
authority instruments (GPT-6 Astra, xhigh), and model/permissions/lifecycle/
privacy (GPT-6.1 Sol, xhigh). Both approve within their stated scopes; neither
claims runtime proof or maintainer approval.

The SQL review identifies two acceptance-carrier omissions. The current closed
GUC list must explicitly adopt the two identity announcements, and ADR-0053
Amendment 11's authenticated AccountSite seam needs an Amendment for the exact
bootstrap caller. The revision above adds both obligations and the proposed
ADR-0053 text; Accepted predecessor text remains untouched. No other actionable
proposal contradiction is reproduced. Database/provider/application tests are
not run or inferred from this documentation review.

### P03-1 revised proposal review round 2 and readiness (2026-10-25)

**Reviewed source:** `26237b823a0bf72a1986dfffc849c6a28dd67355`. Two new
read-only reviewers inspect the complete revised approval surface independently:
authentication/global SQL/audit and source handoffs (GPT-6 Astra, xhigh), and
model/manifests/lifecycle/privacy and adoption carriers (GPT-6.1 Sol, xhigh).
Both approve within the Proposed documentation scope, with no actionable finding.
No reviewer edits files or claims implemented enforcement or maintainer consent.

The authentication review also checks a possible attribution ambiguity against
ADR-0044 Amendment 6: aggregate `created_by = SystemActor` when no actor exists
does not substitute SystemActor for the NULL authenticated audit principal.
That concern is not a finding and does not change the selected contract.

Parent validation at the reviewed source passes all 37 existing Python guard
controls and all five Release `CorpusConsistencyTests` cases, with zero skips.
The changed-document sweep resolves 884 relative file targets; both ADR statuses
parse as Proposed, and no temporary review report is referenced. ADR prose checks
exclude the documented table/link exceptions. Whole-tree fragment, roadmap
structure, regenerated-index and whitespace checks qualify the final record
after its last edit. These are documentation/corpus checks, not Identity SQL,
Keycloak, API, browser, concurrency or privacy-fulfillment qualification.

The revised package is ready for **joint maintainer review**, not Accepted.
Both ADRs and all nineteen gates remain Proposed/Open as applicable. No runtime,
schema or provider configuration changes accompany this preparation. Acceptance
and the six implementation steps wait for the explicit approval requested before
new ADR implementation; after approval, Step 1 applies the actual dated
Amendments and authority reconciliation before dependent code.

### P03-1 second maintainer review and revised package (2026-10-25)

**Status: Proposed; joint ADR acceptance is still pending.** The maintainer
returns a second set of review findings rather than approving ADR-0072/0073.
The prior readiness entries describe their reviewed sources; their conclusions
do not qualify the integration gaps verified below. This entry replaces the
previous package as the current approval surface, without rewriting its history.

**Accepted staging decision.** After the source-bound consumer analysis, the
maintainer explicitly selects P03-3 for new-user HTTP bootstrap and native BFF
entry. P03-1 retains global storage/common creation authority, central audit,
fixed seed adoption and route-independent existing-binding API admission. No
new POST, SDK operation or native no-session completion is activated here.
P03-3/G1/G6 must decide provider signup and durable creation budgets before
opening that surface; current rate-limit partitions do not bound durable rows.
This staging approval does not accept either ADR or authorize dependent code.

#### Verified finding dispositions

Three read-only source audits cover common persistence/audit, API/native seams,
and membership/seed contracts at `acd20764`. They inspect actual code and current
authorities. No provider/database state is changed and no runtime proof is claimed.

| Review item | Disposition / concrete correction |
|---|---|
| Prepared owner, intent order and creation key | Valid. Ordinary audit declares before transaction and unresolved paths declare nothing. Standard 05 now names prepared global ownership, serialized recheck, typed central intent/reference handoff and the original owner's completion, following the existing prepared delivery precedent. No Identity-private audit writer. |
| Deferred guard failure at COMMIT | Valid current classification gap. Force pending constraints IMMEDIATE after final SQL, before commit admission; failure uses known precommit rollback. Do not add a broad SQLSTATE shortcut or reinterpret a lost physical COMMIT acknowledgement. Real guard-refusal/unknown-COMMIT controls remain Step 3 requirements. |
| Owner-read policy and permanent bindings | Valid wording/protection gap. Single policy role-qualified USING arms are not SELECT-only policies. Add explicit User DELETE/TRUNCATE refusal including owner execution; disclose the DDL trust limit. Audit already has its own mutation/TRUNCATE guards. |
| Ordinary pending sentinel refusal | Valid. Shipped WritePendingAsync lacks the direct guard claimed by the draft. Before admitting global RLS, add hard ordinary audit/transaction/EF mode refusals and a global-discriminator refusal in platform entry. Typed global intent/composer metadata is central, not handler supplied. |
| Route-dependent bootstrap, missing token and wrong method | Valid proposed integration gaps, now outside P03-1 activation. Existing public anonymity, AccountSite absent404/valid405, route-invariant resolution and endpoint/SDK union remain. P03-3 must explicitly supersede route invariance if its selected bootstrap design needs it. |
| Duplicate evidence and cross-module SQL carriers | Partly already explicit, but carrier incomplete. New global duplicate evidence must match exact subject/key/target/source/outcome; non-success durability never proves User absence or success. Name the bounded central witness-installer exception in ADR-0010/0044 and structural controls. Do not generalize the ordinary 23505 shortcut. |
| Audit matrix and profile slug | Valid. Use Resource/Operation/Class/Why so the existing parser reads the class; add literal identity.user.create MUST/security-event. Profile operation uses identity.membership_profile.set_seed_profile, matching the permission resource. |
| Phase custom-field permission | Valid remaining phase spelling. Align the future profile-value writer to identity.membership_profile.write; G9 still owns dynamic-value semantics. Other foundation declarations were already consistent. |
| Tenantless/native outcome and bootstrap status table | Valid missing transport contract; belongs to the newly selected P03-3 activation gate. Current complete ProviderIdentity/LoginCompletion and positive AccountSite publication remain. A committed identity without session needs a typed, truthful outcome before activation. |
| Yoga admin scope | Valid handoff omission, not a reason to change its provider claim. Preserve b001's org claim and NULL-org Tenant Admin Membership; seed SQL uses explicit declared target scope. Tenant-wide writes later require explicit orgless session reissue. The read hatch never widens writes. |
| Assignment addition wording | Valid drift. All additions recheck User admission, including an initially empty template; do not substitute a heuristic for current authority increase. |
| Tenant lock granularity / final-admin predicate | Valid serialization cost. Use shared tenant locking for profile/nonadmin/org mutations and exclusive for first-admin/admin eligibility/assignment/kernel changes, with actual-transition classification and no upgrade. Preserve the total eligible-admin invariant; future closure cannot silently strand it. |
| Production first-admin owner | Partially documented before; missing in phase result/proof. P03-9/G13 now explicitly owns admitted first-admin/provisioning, partial-setup recovery and controlled issuer rebind. Production onboarding waits for that owner; seed-backed UI can be qualified earlier. |
| Tenant-owned to global User reference | Existing same-module FK is not forbidden by the tenant-to-tenant composite rule. Add the third named canonical shape and tests; do not weaken ordinary composite references or add User FKs to audit actors. |
| Kernel two versus initial admin three | Intentional, not a contradiction. The two-pair kernel preserves administration; profile-write is an explicitly removable initial grant outside that minimum. Clarify this distinction in Standard 19 and seed upgrade behavior. |
| Standards precedence and adoption text | Valid adoption incompleteness. Standards 05/19 now provide precise replacement text while their active prefixes stay unchanged before approval. Distinguish stored templates from enabled assignments, scoped pair identities, initial v1 grants and later audited adoption. |
| G3 lifecycle and seed accounting | Valid missing delivery detail. Name the bounded Tenancy lifecycle contract in Step 4 and its live P03-2 consumer. Extend actual seed audit accounting; keep the four authentication personas, with extra proof subjects fixture-local. |
| Durable cardinality / lookup cost | Risks already disclosed, not new demonstrated exploits. HTTP staging removes immediate open allocation. Step 5 additionally qualifies uncached lookup latency, pool pressure, cancellation and uniform401 under store failure; no measured p95 or production capacity guarantee is claimed. |

#### Current approval surface and predecessor Amendments

Joint [ADR-0072](../decisions/0072-application-owned-identity-bootstrap.md) and
[ADR-0073](../decisions/0073-scoped-membership-and-identity-data-foundation.md)
remain Proposed. Their canonical contracts are the Proposed annexes in Standards
05/11/19, the Identity specification/matrices and Data Protection's classification
section. Standard 05 now owns the prepared-owner, audit/evidence, lock and exact
adoption text; other documents link rather than repeat that algorithm.

The following complete Amendment drafts replace the prior package's drafts.
On actual joint acceptance, append each at its target's next available heading
and actual date. Do not reserve numbers, rewrite earlier bodies/metadata or
apply these to Accepted authorities before approval.

| Target | Append-ready text / bounded instrument |
|---|---|
| ADR-0003 | ADR-0072 adds global self-identity as a seventh table class and its platform-scoped authority anchor. Standard 05 owns the exact FORCE-RLS policy arms, immutable binding guards, same-module tenant-to-global User reference and five confined owner functions. The ordinary tenant/org template and four login roles remain. |
| ADR-0010 | ADR-0072 admits the enumerated central IdentityCreationWitnessInstaller persistence contract: reciprocal User/central-audit creation guards and exact evidence reads over declared key/discriminator fields. Central infrastructure contains that fixed SQL without module entity/assembly imports. Identity migration invokes it without copied audit SQL. This is the only new structural SQL exception; business cross-module joins and a fifth application communication mechanism remain forbidden. |
| ADR-0017 | ADR-0073 defines Membership as one profile/assignment root and Role as a tenant-wide capability template. Organization grants apply only through an exact Organization Membership; template storage does not confer cross-branch authority or authoring. Tenant grants plus matching-org grants remain. Tenancy owns Organization lifecycle. |
| ADR-0033 | ADR-0072 extends central MUST creation and reconciliation to exact prepared global identity owners without resolved tenant context. Serialized lookup precedes intent; only absence opens the common audit frame and creation-reference handoff. Success shares User COMMIT. Known precommit refusal and unknown COMMIT remain distinct; non-success preserves captured attribution and fresh standalone timestamp. No Identity-owned audit writer or provider transaction is added. |
| ADR-0036 | ADR-0072 inserts quota-before-binding, route-independent existing-binding normalization before ordinary tenant reconciliation. No new HTTP endpoint, context writer or unresolved marker is admitted. ADR-0073's separately named PreContextIdentityAuthorizationReader is a short READ ONLY app-role candidate read, with no ITenantContext; its live rows/HostScope/claim-only evidence remain P03-2 proof. |
| ADR-0040 | ADR-0072 adds common global read/prepared-creation/seed/reconciliation owners with exact identity announcements, connection/generation capture and one-shot prepared completion. Tenant/global modes cannot join or retarget. ADR-0073 registers the planned ninth tenant setter PreContextIdentityAuthorizationReader, executed only in P03-2 with exact candidate tenant/org and physical READ ONLY. Identity GUCs are a separate census, never sentinel tenant announcements. |
| ADR-0044 | ADR-0072 narrowly extends §§1/2/3/5/9/10 with a second sentinel population, identity.user.create MUST/SecurityEvent, typed global attribution and two central writer seams. Ordinary pending/standalone/best-effort/tenant entry refuses it; platform entry rejects this identity discriminator. Global success requires reciprocal User/audit evidence and precommit constraint validation. For this population, 23505 alone proves no outcome: exact-key/source/subject/target/outcome evidence is required, and a fresh non-success row proves only its own durability. Fixed central witness SQL receives only the enumerated structural exception. Existing append-only/redaction/retention authority is unchanged. |
| ADR-0064 | ADR-0072 replaces required API wire user_id with verified-subject admission followed by existing-binding normalization. Credential enforcement recognizes the verified stage; the unchanged issuer/sub quota precedes binding SQL. Unknown/conflicting/Closed/unavailable bindings uniformly refuse401 without allocation. Route invariance, the closed GET/HEAD surface, public anonymity and AccountSite404/405 remain. P03-3 needs separate acceptance before new-user HTTP activation. |
| ADR-0065 | Navigation only: ADR-0072's global MUST identity creation receives no host-tenant mismatch allowance or suppression. The independently defended-host admission key and budget remain unchanged. |
| ADR-0066 | ADR-0072's input stage admits verified provider subjects before application binding. The broader population retains issuer/sub keys, numeric bounds, once-per-request debit, cleanup/fault ownership and non-reset behavior; the registry bounds neither permanent Users nor audit rows. |
| ADR-0070 | Navigation only: P03-1 preserves complete native identity, ID-token/AccountSite association and publication/refresh fences. Align opaque subject validation to the API's 255-character contract. New optional pre-session identity, bootstrap association and no-session completion remain unaccepted P03-3 changes. |
| ADR-0071 | Navigation only: ADR-0072 preserves the ordinary-read refusal table despite the additional binding-store uncertainty source. No automatic retry, refresh or identity-refusal witness follows. |

ADR-0053's AccountSite seam receives no P03-1 extension; its future pre-session
caller waits for P03-3 acceptance together with ADR-0064/0070. Adopt Standard
05's exact replacement clauses and Standard 19's scope/default/manifest text,
reconcile Standards 01/04/07/10/11/18/21, architecture/CLAUDE/glossary/XML claims
and actual schema/method/guard censuses before their dependent code. Preserve
historical packet method counts. Existing Implemented tests retain status with
pending extensions; new controls are Registered only when actually catalogued.

#### Revised six steps and required proofs

| Step | Required closure evidence |
|---|---|
| 1 — Authority acceptance | Joint approval and the actual dated Amendments above; promote approved annexes, authority headers, exact adoption prose and named catalogue controls before code. G1 closes storage/common creation, G3 its foundation contract; transport/live/archive gates remain Open. |
| 2 — Classification | Complete real EF-property declarations and automatic per-module/coordinator model diff, inherited references and neutral-name missing controls. Fixed diagnostics plus positive/canary sink proof precede Identity writers. |
| 3 — Global persistence/audit | Sixth chain/authority installer; common prepared global owner/reference/intent lifecycle, ordinary seam refusals, single-policy arms and permanent-binding DELETE/TRUNCATE guards. Real app-role/owner-body/privilege/caller controls; benign race has one intent, precommit refusal differs from lost COMMIT, mismatched/foreign duplicate evidence fails. No mocked census can close this step. |
| 4 — Contextual foundation/seed | Five contextual tables, exact finite registry/kernel artifact, reference/lifecycle/scope guards and shared/exclusive fresh-lock proof with real held-lock competing commits. Deliver the bounded Tenancy lifecycle contract. Preserve all four authentication personas and target-derived Yoga seed scope; extra subjects stay fixture-local. Staged Role/first-admin/learner acts, actual extended audit accounting, interrupted/no-write/conflict/upgrade reruns and seed denial in API. |
| 5 — Existing-session admission | Stock verified-subject feature, credential gate, unchanged subject quota then global READ ONLY existing binding before route-independent resolver. Native complete-identity/AccountSite flow and current operation census remain; align sub255. Qualify actual provider/app-role lookup, plan/latency/pool/cancellation/store-fault paths. No new-user POST/BFF caller. |
| 6 — Complete qualification | Relevant full suites, corpus and source-bound shard/discovery accounting; real PostgreSQL/Keycloak/native existing-session slice and complementary A/B CI with zero skips and unchanged required names/ceilings. Update docs and prepare the reviewable PR without claiming later HTTP/onboarding/closure delivery. |

P03-2 owns live candidate reads, permission enforcement and explicit later grant
adoption. P03-3 owns new-user transport, registration/capacity decisions and
session/no-session continuation. P03-9 owns production first-admin, issuer rebind
and exceptional recovery; P03-10 owns closure/retained-copy fulfillment. Neither
schema nor seed evidence closes those later flows. Each implementation step still
receives a commit and two new independent review rounds with verified fixes.

### P03-1 second revision review round 1 (2026-10-25)

**Reviewed source:** `c63ca622`. Two fresh read-only reviewers independently
trace the revised proposal against current source: execution/audit/API contracts
(GPT-6 Astra, xhigh) and membership/manifests/seed/lifecycle contracts
(GPT-6.1 Sol, xhigh). No runtime proof is claimed.

The execution slice approves with a minor internal-result wording correction:
HTTP401 belongs only to the ordinary API column; the internal creation owner
returns typed refusal and P03-3 owns its HTTP mapping. The model slice requests
changes for one Major contradiction: the unqualified shared/exclusive admin
assertion also forbids initial Role roots before the first Membership.

Standard 05 now gives finite first Role creation its explicit shared-lock
exception, requiring exact admitted initial manifest/source and no historical
Membership. Existing Role edits, upgrades and repairs cannot take that path;
first-admin creation takes exclusive mode and every Membership/child mutation
retains the total invariant. ADR-0072's internal result wording is corrected.
No other actionable finding is reported within the reviewers' assigned slices.
Parent validation at the reviewed source passes 37 Python documentation controls
and 20 compiled CorpusConsistencyTests/AuditCoverageTests cases, zero skipped.
These checks do not qualify the proposed Identity runtime or database guards.

### P03-1 second revision review round 2 and approval readiness (2026-10-25)

**Reviewed source:** `cd8150c9`. Two new read-only reviewers independently
inspect execution/audit/API boundaries (GPT-6 Astra, xhigh) and model/lifecycle/
seed contracts (GPT-6.1 Sol, xhigh). Both approve within their assigned Proposed
design/source-review scopes, with no actionable findings. The model reviewer
explicitly falsifies the first Role-before-admin path and direct-write/lock
contracts; the execution reviewer traces current coordinators, Npgsql outcome,
store, credential and native handoffs. These verdicts do not establish runtime
confinement or accept either ADR.

The earlier round's Role-initialization contradiction and internal-result
wording are resolved at the reviewed source. Parent controls pass 37 existing
Python documentation cases and 20 compiled CorpusConsistencyTests/
AuditCoverageTests cases, with zero skips. Local file/fragment, inactive-annex,
ADR-status, whitespace and private-review-reference checks are separate mechanical
evidence, not proof of future Identity execution. Final text reflow/status and
this append-only receipt require regenerated indexes and whole-tree lint after
the last edit.

The current second-maintainer package is ready for joint review of ADR-0072/0073.
Only HTTP/new-user BFF staging to P03-3 is already approved. Both ADRs remain
Proposed, their acceptance-dependent gates remain Open, and no schema/runtime/
provider change has occurred. Steps 3/4 still require real owner-function/body/
privilege/caller and held-lock competing-commit proofs; metadata scans cannot
close them. No new approval is inferred from a review verdict or the staging
answer. Acceptance must be the first implementation step before dependent code.

### P03-1 joint acceptance and Step 1 (2026-10-25)

The maintainer explicitly approves revised ADR-0072, ADR-0073 and the complete
six-step second-maintainer package after `06b36199`. This is joint acceptance,
not an inference from the earlier review verdicts or HTTP-staging answer.
The approved P03-3 HTTP/new-user BFF boundary remains: P03-1 adds no new route.

Step 1 adopts the named authority before dependent code. Both ADR statuses
become Accepted; the decision index moves their rows to Active. Actual dated
Amendments append to ADR-0003/0010/0017/0033/0036/0040/0044/0064/0065/0066/0070/
0071, without changing predecessor bodies or metadata. Standards adopt their
exact canonical replacements and register proof owners; architecture, glossary
and module contracts distinguish approved obligations from runtime capability.
No runtime grant, Identity table, provider mutation or live authorization is
claimed by this acceptance commit.

G1/G3/G19 and foundation portions of G2/G4/G5/G7/G16 are decided to the scope
named in the register. Invitation acceptance remains G8/Open; this foundation
adds no Invitation table or ADR-0042 exception. Candidate-reader execution,
new-user HTTP/native transport, production first-admin, controlled issuer rebind
and closure remain their named later gates. Current deny-all live membership,
complete native identity and AccountSite operation union persist until their
respective qualified implementation steps.

The six approved steps retain the reviewed order and two new independent review
rounds per committed step. Step 1's authority diff receives both rounds before
Step 2 adds classification code. Steps 3/4 cannot close on schema metadata alone:
actual function/body/owner/privilege/caller controls, real held-lock competing
commits, exact duplicate/outcome evidence and first/final-admin behavior remain
mandatory. Lookup latency/pool/cancellation qualification belongs to Step 5;
full source-bound qualification and the reviewable PR belong to Step 6.

Step 1 parent validation passes all 37 existing Python documentation controls and
20 compiled corpus/audit-matrix controls, with zero skips. Both new ADRs parse
Accepted. A byte-prefix comparison proves all twelve predecessor bodies and
metadata are unchanged; only the new dated entries append. The changed-file
sweep resolves 2,705 relative file targets and contains Markdown only. Whole-tree
fragment/roadmap/index and whitespace checks run after the last edit. Registered
Identity obligations are not reported as implemented by those checks. The
acceptance commit is followed by two fresh independent Step 1 review rounds.

### P03-1 Step 1 review round 1 (2026-10-25)

**Reviewed source:** `e6272082`. Two fresh read-only reviewers inspect authority
adoption (GPT-6 Astra, xhigh) and corpus/model carriers (GPT-6.1 Sol, xhigh).
Both request documentation corrections; neither claims runtime qualification.

Both verify Architecture 31 incorrectly infers User absence from a non-success
row. The authority reviewer also verifies stale unmodified-audit-policy and sole
sentinel-population claims, plus Audit module status counting five rather than
six existing methods. These mutable carriers now distinguish current schema,
the accepted pending global extension and exact non-success evidence. A parent
sibling sweep also corrects Domain Model's provider-sub-as-actor, global-profile/
custom-field and audit-policy shorthand to the accepted canonical contracts.
No Accepted ADR body changes in this correction.

The corpus reviewer considers the ADR-0073 G8 shorthand and reproduces no separate
finding: its later invitation paragraph and the authoritative register expressly
leave G8 Open. Do not rewrite the now-Accepted body merely to restyle that
boundary. Both reviewers confirm predecessor-byte preservation, legitimate joint
approval, planned control statuses and the unchanged P03-3 transport boundary.

### P03-1 Step 1 review round 2 and closeout (2026-10-25)

**Reviewed source:** `7ded2585`. Two new read-only reviewers independently assess
authority/canonical execution contracts (GPT-6 Astra, xhigh) and corpus/model/
proof-state carriers (GPT-6.1 Sol, xhigh). Both approve within Step 1's scope,
with no actionable findings. They verify the earlier non-success/sentinel/policy/
method-count corrections, Domain Model alignment, predecessor-byte preservation,
registered proof owners, finite manifests and P03-3 transport staging.

Step 1 is complete. Acceptance/canonical adoption precedes all dependent code;
no runtime or schema qualification is inferred. Parent fragment, roadmap/index
and whitespace checks pass; current compiled corpus/audit-matrix controls pass
20 cases with zero skips. Step 2 proceeds automatically under the accepted plan:
explicit mapped-property declarations, common classification validation and a
neutral-name missing-declaration control before Identity writers.

### P03-1 Step 2 implementation and local qualification (2026-10-25)

Step 2 adds explicit personal-data category/purpose declarations and a shared EF
model-finalizing validator. The actual production census covers five contexts
(including the deliberately empty Platform context), 19 mapped entities and 201
scalar properties: Tenancy 83, Customization 39, Audit 34 and Education 45.
Inherited actor/audit fields and shadow properties participate. Each module owns
its semantic declarations; no field-name heuristic supplies a default category.
Classification does not itself authorize disclosure, redact snapshots, establish
consent or change retention. SQL-only stores retain their separate inventory.

The canonical mapped-property control is now Implemented in
`DataProtectionConventionTests`. Its eight methods produce eleven cases,
including a neutral shadow property added after normal production customization:
missing classification prevents model use, and the same property with an explicit
declaration succeeds. Invalid categories, blank/excessive purposes, conflicting
declarations and misspelled unmapped names refuse without values in diagnostics.
A relational comparison with all four existing migration snapshots is empty; this
step adds no schema, migration, Identity writer or authentication change.

Release build passes with zero warnings/errors. Local qualification passes 2,692
Unit cases, five Contract cases and 153 relevant real-PostgreSQL integration
cases, all with zero skips. The complete Architecture run executes 551 cases:
545 pass and six fail in existing corpus/inventory/module-matrix accounting.
The corrections preserve exact census checks: include the accepted Identity
module specification, name the implemented test owner in the catalogue's parsed
Status, and place pending global audit linkage under the existing audit custody
boundary rather than inventing another physical store. Rebuilt focused
Architecture qualification passes all 104 affected and classification cases,
with zero skips. The first complete run remains recorded as failed; its six
failures are not described as a green complete rerun.

Changed C# formatting verification passes. Documentation, generated-index and
whitespace checks run after this entry. The implementation commit precedes two
fresh independent review rounds; Step 2 is not closed by these local results.

### P03-1 Step 2 review round 1 (2026-10-25)

**Reviewed source:** `3261114c`. Two fresh read-only reviewers independently
inspect the common validator/model boundary (GPT-6 Astra, xhigh) and module
semantics/corpus (GPT-6.1 Sol, xhigh). The first approves without actionable
findings and independently passes all eleven classification cases with zero
skips. The second verifies one Minor documentation drift: the Identity spec
and G19 status still describe the current classification control as pending.
Those mutable statuses now distinguish the implemented current-model census
from pending Identity mappings and writer diagnostic proofs. No Accepted ADR
body changes.

Both verify explicit None purposes against actual institution/configuration/
curriculum semantics, conservative personal/reference declarations, value-free
diagnostics and actual snapshot/model relational comparison. Complex-leaf
recursion is structurally reviewed, not claimed as a separate production complex
property proof: no current production complex mapping exists. No verified runtime
finding remains within these review scopes. A second fresh review round follows
the correction commit.

### P03-1 Step 2 review round 2 and closeout (2026-10-25)

**Reviewed source:** `eccf2dff`. Two new read-only reviewers independently review
the model/finalization boundary (GPT-6 Astra, xhigh) and semantic declarations/
corpus (GPT-6.1 Sol, xhigh). Both approve within Step 2's scope, without actionable
findings. Each independently passes the eleven focused Release classification
cases with zero skips; the census and all four relational comparisons agree.
They verify the earlier mutable-status correction and leave Accepted bodies
unchanged. Their focused proof does not claim a new full-suite/provider run.

Step 2 is complete after both fresh review rounds. Explicit classification and
the missing-declaration counterexample precede the first Identity model/writer.
Steps 3/4 must add their actual properties to this complete census and retain
separate snapshot/diagnostic controls. Step 3 now proceeds automatically: global
User/authority persistence, the prepared common owner and central creation/
reconciliation seams, with real app-role/function/outcome proofs before closeout.
No new HTTP or native new-user flow is activated.

### P03-1 Step 3 global storage and common audit implementation (2026-10-26)

Step 3 implements the internal global foundation under ADR-0072/0073. One new
Identity migration chain creates User and the deployment authority singleton;
the applied population is 44 data tables plus six history tables, seven table
classes and three Identity SECURITY DEFINER functions. Five contextual tables
and two Membership owner functions remain Step 4. The 17 global mapped scalars
join the complete classification census: six contexts, 21 entities, 218 scalars.
No unused email mirror or global profile/custom-field storage is added.

The admitted internal source qualifies the fixed authority/issuer pair, opens a
fresh global app-role transaction, announces only its immutable subject scope,
locks/rechecks and returns an existing binding without mutation intent. Only
serialized absence enters the common typed audit frame. The centrally minted
creation reference binds target, original audit key, actual connection,
transaction and generation; handler construction remains deferred. The existing
common completion path writes the User and MUST audit together, validates all
deferred constraints before commit admission and retains known rollback versus
physical COMMIT uncertainty. Global and ordinary owners cannot join or retarget.
Exact context/connection/transaction capture admission precedes snapshot reads.
A non-allocating scoped enrollment reader preserves the lazy application-source
boundary for ordinary/platform audit entry.

PostgresAuditStore remains the sole audit INSERT owner, with eight closed seams.
The central migration installer owns the reciprocal/evidence SQL; Identity copies
no audit SQL. Actual applied body/signature/return/owner/ACL/caller inspection
compares the compiled migration contract, including three global definers and
their exact trigger callers. Committed disposable faults independently widen
RLS/function results, grant PUBLIC execution, remove callers/immutability or
introduce an unrelated definer; clean twins establish the effective boundaries.
Ordinary writers refuse global enrollment even before an intent exists.

A dedicated deployment child installs or verifies the configured authority on
the migration connection; it never repairs/rebinds. Make's explicit order applies
Audit before Identity, passes the credential only to no-build deployment children
and rejects partial authority configuration before migration writes. Runtime
qualification is read-only and installation is not registered in API DI. API
composition denies seed adoption; the ordinary seed/demo handoff remains Step 4,
existing-binding HTTP activation Step 5 and new-user HTTP/native entry P03-3.

Local Release qualification passes 2,727 Unit cases with zero skips. The affected
Architecture run passes 228 cases; after canonical status/model-name reconciliation,
114 focused corpus/model/owner cases also pass. The first affected run's five
failures identify assertion-overload, nested/generic IL-inspection and mutable
catalogue/matrix bookkeeping; exact controls are corrected without weakening
closed sets. The asynchronous inventory now consumes every mapped module's
compiled migration chain, explicitly classifying global synchronous stores.

The first global integration run has 30 passes and 16 failures before business
execution: the test provider omits the real cache needed by AuditConfigService.
The corrected fixture registers production InMemoryCacheService and propagates
early race-participant failures instead of hiding them behind a timeout. Rebuilt
qualification passes all 49 cases with zero skips: 23 schema/control cases,
16 production-service/provider/evidence cases, eight global unit-of-work cases,
one actual deployment-child case and the complete WITH CHECK proof census. The
other 303 requested schema/audit/rollback regressions pass; their earlier sole
census failure passes in that rebuilt global run. Thus all 352 distinct requested
integration cases pass. Owned real Keycloak/HTTPS evidence establishes signed
seed subject/actor and authenticated readback; no actorless/new-user transport is
claimed. Original failed TRX receipts remain separate from successful retries.

Final exact-evidence read-only hardening, full-solution/Contract/format/document
checks and the complete Architecture run are recorded with their actual results
below. No hosted source-bound qualification or step closeout is inferred here.
The implementation commit precedes both fresh independent review rounds.

### P03-1 Step 3 final local pre-review controls (2026-10-26)

The clean exact-evidence transaction now establishes physical READ ONLY before
announcing the captured subject. The existing native duplicate case instruments
its owned evidence function: production reconciliation sees read-only mode, a
direct writable control refuses with SQLSTATE 25006, and exact read-only plus
fresh-creation controls remain successful. All four evidence cases pass again,
zero skipped; no production function/policy or test-count change follows.

The complete Release solution builds with zero warnings/errors. The final Unit
run passes all 2,727 cases, zero skips, including sixteen issuer-registration
cases aligned with the existing API address grammar. Contract qualification
passes five cases, zero skips. Post-promotion corpus/model/owner qualification
passes 114 Architecture cases; the earlier broader affected set passes 228.
The implemented catalogue population is 241 rules (137 in Architecture), with
297 Architecture method declarations. User's global-only canonical rule now has
its actual model owner; contextual controls remain Registered. These counts do
not substitute for the complete Architecture run, which follows the commit.

The scoped formatter, document/index and whitespace checks run after the final
edit. The default-deny seed capability, two-global-table/three-definer staged
population and P03-3 HTTP boundary remain explicit. No Accepted ADR body changes,
new erasure authority, migration credential in runtime, or hosted/PR qualification
is claimed. Both fresh independent review rounds remain required before Step 4.

### P03-1 Step 3 retained-subject pre-commit correction (2026-10-26)

The unshipped initial Identity migration and its model/snapshot now match the
Accepted retained-subject lifecycle: Closing permits a NULL subject; a retained
subject remains bounded and nonempty. Active requires a subject and Closed
requires NULL. The applied PostgreSQL CHECK is evaluated over all fifteen
status/NULL/length controls, without introducing a lifecycle writer or widening
UPDATE grants. All 24 schema/control cases pass with zero skips; the three
global model/snapshot controls also pass. The distinct requested integration
population is now 353 successful cases, including this additional CHECK proof.
The prior failed runs and earlier 352-case successful population remain history.

### P03-1 Step 3 existing-adoption scope correction (2026-10-26)

Pre-commit inspection finds that a no-write seed adoption of an existing binding
disposes an unresolved prepared frame, poisoning its otherwise successful scope.
The owner now completes that no-intent transaction after constraint validation.
The new real app-role control also exposes a second-generation context enrollment
refusal; the confined binding lookup explicitly enrolls its scoped context in
the current owner before reading. It never repairs a detached mutation context:
the existing pre-snapshot detached-context refusal remains effective.

Rebuilt production-consumer qualification passes all seventeen cases with zero
skips, including two repeated existing adoptions and a subsequent tenant frame
in the same scope. User and success-audit counts remain one each, and repeat
adoptions declare no intent. The first control run (sixteen passes, one failure)
is retained separately. This adds one distinct integration proof, bringing the
requested successful population to 354. Formatting, final build and affected
owner/model controls run again before the implementation commit.

Final scoped formatting passes. The complete Release solution builds with zero
warnings/errors; all 2,727 Unit cases and seven focused owner/model Architecture
cases pass again, zero skipped. The complete Architecture run and both fresh
review rounds follow the implementation commit; neither is claimed complete by
these focused results.

### P03-1 Step 3 review round 1 and corrections (2026-10-26)

**Reviewed source:** `e3f56cb2`. Three fresh read-only reviewers independently
inspect the common owner/audit boundary (GPT-6 Astra, xhigh), global schema and
owner-function privileges (GPT-6.1 Sol, xhigh), and scoped consumers/deployment/
corpus (GPT-6.1 Sol, xhigh). The schema reviewer approves its assigned scope.
The other reviewers identify three verified runtime issues and one mutable
documentation gap; no runtime test is attributed to their initial static review.

The binding reader no longer eagerly constructs creation-only scoped
coordinators before tenant resolution. Common scoped factories defer them until
admitted creation, and the binding service checks the live context accessor.
Owner cleanup is awaited without replacing a primary failure or cancellation;
cleanup failure on a normal return becomes a bounded storage refusal and emits
a fixed diagnostic without exception payload. The pending global audit command
explicitly uses the ten-second command limit; independent reconciliation keeps
its separate budget. Mutable Standard 05/issuer-environment descriptions now
match the implemented owners, installer and ordinary writer refusals.

The complete Architecture baseline is interrupted after four verified closed-
census failures; it is not a complete or passing run. Exact tests now include
the extracted private transaction opener, the two guarded global coordinator
entries and the named physical completion/failure owners. Extra-call/delegate
and removed-guard controls remain, including removed-commit controls for all four
new physical commit owners. No job/delivery bypass or broad class exemption is
introduced. The failed/interrupted log remains separate from requalification.

The corrected Release solution builds with zero warnings/errors. All 25 native
global-consumer cases pass, zero skips: they include actual pre-context lookup
followed by resolved-context MediatR business/MUST audit, joined cleanup failures
with primary exception/cancellation preservation and value-free diagnostics,
and an observed sleeping audit INSERT under both deadline and caller cancellation.
Each stalled-write refusal has a successful clean twin and independent failed
reconciliation. The eight additional cases bring the distinct requested native
population to 362; this is not a complete integration-suite claim.

Formatting, Unit and focused corpus/owner checks run before the correction
commit. The complete Architecture rerun and a second fresh independent review
round follow that commit. Step 3 remains open until those controls close.

Final R1 pre-commit controls pass: scoped formatting, the complete Release build
with zero warnings/errors, 2,727 Unit cases and 147 focused Architecture cases,
all with zero skips. Documentation/index checks and whitespace checks also pass.
The focused set does not claim the interrupted closed-census controls passed;
their corrected execution remains part of the complete rerun below.

### P03-1 Step 4 ordered-lock timing proposal (2026-10-26)

[ADR-0074](../decisions/0074-membership-admission-under-ordered-locks.md) proposes
a bounded read-timing replacement before contextual writers. Step 4 feasibility
inspection exposes a literal conflict between the trigger-only User reader and
the complete pre-mutation lock plan for a parent with future administrator
assignments. A separate read-only analysis confirms the conflict and
the continuous-User-fence safety argument. This is design analysis, not runtime
qualification; no contextual table/function/writer is implemented.

The proposed delta retains User-before-tenant lock order, strongest complete
plans, fresh trigger admission, five definers and the existing grant boundary.
Only the requirement that the eligibility read itself precedes tenant acquisition
changes. A closed target can occupy a tenant fence until its check refuses.
Dependent code waits for the maintainer's explicit approval; Step 3 is unaffected.

On approval, append these dated Amendments at the next available heading and
actual acceptance date, preserving Accepted bodies/status/dates:

| Carrier | Append-ready bounded text |
|---|---|
| ADR-0072 | ADR-0074 narrowly replaces the delegated Membership eligibility-read timing: an admitted complete plan acquires the actual target's exclusive User fence before tenant fences and retains it through mutation/completion; the actual admission trigger may freshly verify eligibility after those fences are preheld. No missing User key is first acquired beneath tenant authority. The global subject owner, five-function confinement and absence of a generic User reader remain. |
| ADR-0073 | ADR-0074 separates fence acquisition from eligibility-read timing for complete Membership/child plans. User-before-tenant order, actual-row classification, strongest pre-mutation tenant mode, no upgrades and total eligible-admin invariants remain. The actual mutation trigger verifies the preheld target User fence and freshly refuses unknown/nonadmitting targets before mutation. No new scope, grant, Role variant or live endpoint follows. |

Standard 05's `fn_identity_membership_admission_guard()` row replaces its final
timing sentence with: “Requires the actual target's exclusive User fence already
held when tenant authority is held; freshly verifies eligibility before permitting
mutation. A missing User key beneath tenant authority refuses; no User fields are
returned.” Its lock section adds that complete plans may prehold both ordered
families before the fresh actual-trigger check, while retaining continuous
transaction ownership. Add ADR-0074 to the derivation header only on acceptance.
The canonical algorithm stays in Standard 05; other carriers keep references.

The existing Registered ordered-lock rule keeps that status until Step 4 delivers
actual app-role second-admin/shared-learner, held-lock closure/creation, missing-
fence, no-upgrade, isolation, timeout and rollback controls. No new Implemented
count or policy activation follows from this proposal. Independent proposal
reviews and mechanical checks are recorded below when actually completed.

### ADR-0074 proposal review round 1 (2026-10-26)

Two fresh read-only reviewers independently inspect the proposed four-document
package based on `111ca113`: ordered-lock safety (GPT-6 Astra, xhigh) and corpus/
authority/proof ownership (GPT-6.1 Sol, xhigh). Both approve for maintainer
consideration without actionable findings. They verify the conflicting canonical
clauses, the continuous-fence argument, actual unimplemented contextual boundary
and PostgreSQL 18 locking/snapshot documentation. No build, suite or contextual
runtime proof is attributed to these reviews. The existing rule remains Registered;
no dependent code or Accepted authority changes. A second fresh proposal round
follows this documentation commit before requesting the maintainer's decision.

### P03-1 Step 3 review round 2 and local closeout (2026-10-26)

**Reviewed source:** `111ca113`, whole Step 3 diff from `e4a8a0a1`. Two new
read-only reviewers inspect the common owner/audit/security boundary (GPT-6 Astra,
xhigh) and foundation/schema/DI/deployment/corpus (GPT-6.1 Sol, xhigh). Both
approve their assigned runtime scopes without further actionable runtime
findings. The latter identifies two stale mutable descriptions: repository README
and Architecture 03 still call Identity marker-only. They now point to the
implemented global foundation and the module specification's remaining scope.
These reviews inspect real controls, but execute no suites; qualification below
is the parent's evidence.

The complete Architecture rerun executes 559 cases: 558 pass, one fails, zero
skipped. The four prior job/delivery census failures are green. The remaining
failure correctly detects that IUserBindingStore is not counted by the existing
typed aggregate-port convention. It now derives from the existing
`IAggregateWriteStore<User,UserId>` instead of adding an exemption. Add remains
its inherited creation operation; an explicit default UpdateAsync refuses with
a fixed diagnostic before touching a payload or persistence. No User mutation
grant or lifecycle writer is introduced. The foundation reviewer separately
checks this exact follow-up diff and its production-store/generic-interface
control, approving without additional findings.

The final Release solution builds with zero warnings/errors. All 2,728 Unit
cases pass, including that additional refusal control. All 65 affected
Architecture cases pass, including the formerly failing aggregate census;
the real production creation/read/MUST-audit integration case passes again.
All have zero skips. The complete run remains recorded as 558/559 followed by
this narrow correction/requalification; it is not relabelled a complete green
rerun on a different tree. Earlier 362 distinct native cases and five Contract
cases remain their recorded results. Final source-bound full/shard/native A/B
qualification belongs to Step 6 after all implementation steps.

Step 3 is locally complete after both fresh review rounds and verified fixes.
Step 4's contextual code has not started: the separately Proposed ADR-0074
timing delta requires the maintainer's decision first. Global owners, storage and
audit need no reversal; no new HTTP/native-user activation or production
membership/closure authority is claimed. Scoped formatting and document/index/
relative-link/whitespace checks run before the closeout commit.

### ADR-0074 proposal review round 2 and readiness (2026-10-26)

**Reviewed proposal:** `1c3fb969`. Two new read-only reviewers independently
inspect safety/viability (GPT-6 Astra, xhigh) and corpus/acceptance/proof ownership
(GPT-6.1 Sol, xhigh). Both approve for maintainer consideration without actionable
findings. They independently check the exact contradictory clauses and the
second-administrator case, PostgreSQL advisory-lock/snapshot primitives, actual
unimplemented contextual source, and the bounded acceptance instruments.
No edit, build, suite or contextual runtime qualification is attributed to them.

The proposal is review-ready, remains Proposed and changes no active lock rule,
function/grant count, Accepted body or dependent runtime. The existing six-step
plan receives only its explicit Step 4 timing delta on approval. The actual
app-role race/negative/timeout/snapshot controls remain implementation closure
conditions, not evidence supplied by documentation review.

### ADR-0074 maintainer review correction and acceptance (2026-10-26)

The maintainer authorizes acceptance and continued implementation after verifying
and fixing the supplied review findings, without another approval request. Two
read-only verifiers (GPT-6 Astra / GPT-6.1 Sol, xhigh) independently check the
findings against `c4569ad5`, actual global DDL and canonical contracts. The
substantive gaps are valid; no additional product/authority choice is required.
Earlier scoped proposal verdicts remain history rather than a claim that this
later review contains no actionable findings.

| Verified finding | Disposition |
|---|---|
| Missing ownership mechanism and limits | Standard 05 now names the exact current-PID/database/advisory/granted/exclusive/single-bigint observation and canonical signed key. The trigger uses no acquisition or try-lock probe. Historical order, transaction kind/lifetime and CLR admission are separate closed caller/source/receipt and completion controls. |
| Session-only SQL rejection | Not adopted as a claim: the view cannot distinguish it. An actual session hold satisfies the same predicate. Source/caller controls reject session APIs; dedicated observations expose the limit and real transaction-end tests prove lifetime. |
| Broad “Membership writers” and inconsistent optional checks | User fences are required for creation, Suspended-to-Active and all assignment additions. Fresh target checking is mandatory; profile-only/removal paths take no automatic User fence. “Before data-mutating statements” replaces ambiguous SQL-dispatch wording. |
| Fault versus target refusal | Fixed internal plan faults use the existing identity-free lock-refusal `fault` signal; expected unknown/nonadmitting target refusals do not invent acquisition faults. Both preserve the same fixed outward refusal. |
| Impossible closure competitor | Disposable fixtures explicitly accommodate both unconditional `tg_users_no_update` and owner CHECK policy, perform the fixed owner transition under User→affected-exclusive-Tenant order, restore in `finally`, and verify restored body/ACL/policy/trigger plus owner UPDATE denial. No production closure writer/grant is added. |
| First-admin example and confined alternative | The admitted `CreateFirstSeedAdminMembership` is explicit; second-admin subjects remain fixture-local. Candidate-only `fn_identity_admin_admission_state` cannot become an arbitrary new-target reader. |
| Exclusive mode, snapshots, keys and costs | Preserve existing per-User serialization rather than claiming shared locks cannot exclude closure. Cite VOLATILE query snapshots. Step 4 requires User/Tenant C#/SQL vectors, foreign/wrong/shared/namespace observations, profile/removal negatives and actual trigger-path p95/query/pool/contention qualification. |
| Acceptance carriers and instruments | ADR-0072/0073 receive navigation Amendments; their bodies/status/dates stay intact. ADR-0074 owns the bounded canonical Standard 05 timing change. Headers, index, phase Status/G3/P03-10, signals/security/spec and catalogue Source/Asserts are reconciled. Registered remains Registered. |

A bounded mechanism probe uses the exact fixture pin `postgres:18.4-alpine`,
image digest `sha256:9a8afca54e7861fd90fab5fdf4c42477a6b1cb7d293595148e674e0a3181de15`,
actual version `180004`, with `learnstack_app` verified NOSUPERUSER/NOBYPASSRLS.
All fourteen checked predicate observations pass: positive/negative bigint keys,
wrong/shared-only/two-integer exclusions, foreign backend without acquisition,
transaction commit/rollback release, and the session hold surviving transaction
end. The owned socket-only/no-network container and foreign worker are cleaned
and joined; preexisting containers remain untouched. This is a mechanism probe,
not contextual schema/guard/race/performance qualification. Its source baseline
is `c4569ad51e8b52a2f89ce502abf20c9f5b10299f`; contextual code does not exist yet.

ADR-0074 is Accepted on 2026-10-26 under that conditional authority. Its timing
delta is incorporated before dependent Step 4 code. The original six steps and
two fresh independent runtime review rounds per step remain: Step 4 implements
the five contextual tables, finite manifests, actual guards/commands and owned
provider-witness seed handoff. Step 5 activates existing-session binding; Step 6
owns complete source-bound/shard/native A/B qualification and PR preparation.
HTTP/new-user BFF entry remains P03-3; production first-admin/rebind remains
P03-9 and closure fulfillment remains P03-10.

Two independent read-only reviewers (GPT-6 Astra / GPT-6.1 Sol, xhigh) inspect
the complete eleven-file acceptance diff and approve without actionable findings.
They verify the precise predicate, its limits, narrowed admission scope, fixture
prerequisites/restoration, append-only navigation Amendments and complete carriers.
Neither review runs contextual tests or independently reruns the mechanism probe.
The five existing CorpusConsistencyTests pass in Release with zero skips;
document/index/fragment checks resolve 2,556 links, the seven ADR-status parser
tests pass, and whitespace, added relative targets and private-reference scans
are clean. No prior proposal/readiness/review record is rewritten and no
contextual control is promoted on the strength of this document or probe.

### P03-1 Step 4 — Contextual foundation and owned seed (2026-10-26)

**Authority:** jointly Accepted ADR-0072/0073 and ADR-0074's accepted timing
correction. This implements the contextual portion of the approved six-step
package; independent review rounds follow the implementation commit. No HTTP,
new-user BFF activation, live permission enforcement or closure writer is added.

Five contextual tables join the existing Identity migration chain: Membership,
its Profile/Assignments, Role and grants. The applied census is 55 tables, six
chains, seven classes, eight central audit seams and exactly five Identity
SECURITY DEFINER functions. Exact applied bodies/owner/ACL/triggers and planted
widening controls include the new pair. The model census covers six contexts,
26 entities and 254 scalar properties, all explicitly classified. Compiled v1
owns definitions, templates, kernel, fingerprints and generated SQL values.
Module-local identifiers cross Contracts as Guid; shared User/Tenant/Organization
identifiers remain typed.

The complete plan derives from actual Role/parent facts, acquires ordered User
then strongest tenant fences, rechecks fresh state, and mutates only afterward.
Actual admission triggers observe their target fence and User eligibility;
last-admin, lifecycle, mirror, capability and complete-graph guards validate
before the common owner commits. The closed graph-save adapter temporarily
suspends automatic EF savepoints only for that save and restores the original
flag in finally. Any save failure poisons the whole unit. This preserves strict
same-top-transaction initial-Role evidence through graph preflight without a
released-subtransaction-lock heuristic. Ordinary/global saves retain their default;
success, real constraint refusal and cancellation prove restoration and rollback.
No general savepoint switch or partial repair path is admitted.

The argument-free data Seeder remains the first application convergence pass.
The shared realm owner then reconciles/verifies, reads its final authenticated
provider facts, and awaits a second owned child while retaining the realm lock.
Only a versioned, bounded private stdin envelope carries the exact issuer and
four actor/opaque-subject pairs; no Admin credential, argv subject or public
result projection is added. The child qualifies the explicit selected authority,
adopts four bindings, then separately commits sixteen Role acts, two first-admin
and two learner Membership acts, and four profile acts. There are 28 additional
successful identity audit rows. Predispatch exact postconditions skip complete
acts; interrupted reruns create missing acts only. Edited, revoked, suspended or
upgraded authority refuses without repair. Yoga admin remains NULL-org in the
application while its provider branch claim remains untouched; Yoga learner is
in the default studio. The environment example supplies an explicit local
stable authority/issuer pair; existing private files must add the pair and must
match any installed registration.

Qualification before implementation review:

- Release solution builds with zero warnings/errors; all 2,748 Unit cases pass.
- All 51 affected Architecture cases pass, including compiled command/caller,
  exact registry, classification, audit matrix and corpus controls. Three
  Registered obligations now have actual owners and implementation; the catalogue
  records 244 Implemented rules, 140 in the Architecture assembly, and 302
  method declarations. Final complete-source qualification remains Step 6.
- All 157 selected Integration cases pass with zero skips. These include actual
  app-role scoped reads/writes and independent RLS dirty twins, unknown/Closing/
  Closed target refusals, first/last-admin and shared/exclusive races, real
  competing commits, complete fixture guard/policy restoration, timeout/cancel
  rollback and same-backend pool reuse, finite metrics, no-write/interrupted/
  conflict seed reruns, central MUST failure rollback and actual child snapshots/
  redacted profile diffs. Existing-root child changes remain in qualified diffs;
  no unknown collection is newly represented as a complete snapshot.
- Eight realm Node files execute 409 cases; 228 native launcher/orchestration
  controls pass. Provider-witness and C# execution controls retain their actual
  scopes; final combined provider/browser/source-bound qualification remains
  Step 6 rather than being inferred from these cases.

A separate actual command-path experiment passes on two disposable databases.
Each executes the production pipeline's 24 contextual commits and observes a
real first-admin User wait before the competing COMMIT. The current-code
observation-cost counterfactual disables only the heldness function's observation
in its isolated database; it is not a historical or security-qualified baseline.
Both helpers still acquire their real locks. Exact function state is restored
and databases dropped. Four first-per-type and twenty subsequent-per-type samples
are reported separately; neither population means process-cold or pool-cold.
Measured warm p95 is 8 ms for the counterfactual and 11 ms with production guards,
against the 500 ms local budget. Production client-SQL-span p95 is 9.158 ms;
client command counts match, including internal trigger time rather than internal
statement enumeration. Each pool peaks at two used connections of four and
returns to zero. Observed acquired User waits are about 52/53 ms. The small local
sample establishes neither a speed ratio nor production capacity or throughput.
No identity, connection tag, SQL or function body is emitted by this experiment.

Qualification corrected real issues rather than relabelling failures: explicit
CASE grouping in trigger SQL; actual EF savepoint versus strict initial-Role
freshness; test counts under organization RLS; old 50/three-function census and
User TRUNCATE proof preempted by new FKs; and per-flow test metric observation.
The initial 153-case run was 150/153, followed by those verified expectation
corrections and the final expanded green population above. Full-step reviews
have not yet run; no local-complete or packet-complete claim follows here.

### P03-1 Step 4 review round 1 (2026-10-26)

**Reviewed source:** `0495f2b2`, full Step 4 diff from `dce6cad2`. Three new
independent read-only reviewers inspect SQL/storage/ordered-lock correctness
(GPT-6 Astra, xhigh), Domain/Application/privacy/corpus boundaries (GPT-6.1 Sol,
xhigh), and owned tooling/Seeder/native handoff (GPT-6.1 Sol, xhigh). They read
actual source, contracts and scoped receipts; none executes builds/tests or edits
files. All approve assigned runtime scopes with no verified blocking/major issue.

Two independently identify the same Minor current-spec drift: Identity's bottom
permission/audit/evidence inventory still calls contextual commands absent and
implemented seed/lock/classification controls pending. The current descriptions
now name actual owners and point to canonical catalogue/measurement records,
while preserving pending independent review and later live/HTTP/closure owners.
No Accepted body or historical delivery record changes.

Candidates are not promoted into findings without supporting current behavior:
per-act Tenancy lifecycle transition races remain the explicitly named P03-3
contract; no current value-bearing database-diagnostic leak is established; and
concurrent owner editing of the selected private file is an unreproduced
configuration-continuity caveat rather than a demonstrated authority bypass.
No speculative runtime change follows from those candidates. Full source/shard/
integrated provider qualification remains Step 6. Document/corpus checks qualify
the verified correction before a second fresh full-step review round.

### P03-1 Step 4 review round 2 and diagnostic correction (2026-10-26)

**Reviewed source:** `9c9ec4a7`. Two new independent read-only reviewers inspect
storage/SQL/privileges/ordered-lock behavior (GPT-6 Astra, xhigh) and
Application/Domain/tooling/privacy/corpus boundaries (GPT-6.1 Sol, xhigh). Both
approve their assigned runtime scopes. Neither edits files or executes tests.
Verified Minor corrections reconcile Standard 19's already implemented types
and Standard 05's contextual table-class and current grant inventories.

The Application review also identifies an evidence gap: the current module
inventory overstates Step 2's sink proof. Classification and generic baseline
controls do not establish neutral/nested value exclusion at Identity's actual
writer boundary. The Accepted package requires that proof before those writers;
it is completed here before Step 4 closes, rather than moved to Step 6. The
earlier review's absence of a demonstrated leak is not a safety proof.

New source-reaching tests demonstrate a real failure before the correction:
PostgreSQL returns neutral/nested/sensitive synthetic values from a tracked
profile write, with provider error detail disabled. EF's pre-adapter event 10000
(`SaveChangesFailed`) puts neutral/nested values into serialized Serilog and
the actual Sentry SDK envelope. The initial two-case run has one failure and
one passing validation control. Local archive and response exclusion alone do
not establish safety for the other sinks. This negative receipt is retained
separately from the subsequent successful run.

The narrow fix replaces only IdentityDbContext's EF save-failure event with
fixed event 3191 (`IdentityStorageSaveFailed`), retaining ordinary command
diagnostics and other modules' save-failure events. Unexpected contextual
provider/save/preflight failures become a fixed provider exception with no
original inner payload; owned conflict/target/plan classifications and caller
cancellation retain their existing behavior. The global binding adapter keeps
its existing fixed failure boundary. No general logging scrub, new authority,
grant, schema or HTTP operation is introduced.

Eight focused actual Integration cases pass with zero skips: immediate and
deferred profile failures, global binding failure, sibling-module confinement,
real validator attempted-value exclusion, and existing success/owned-conflict/
cancellation controls. Provider-side witnesses establish source reach; nonempty
positive controls establish serialized Serilog, real local archive, real Sentry
SDK envelope and L1 response execution before every canary exclusion assertion.
Sentry delivery uses an in-memory transport; the actual SDK serializes its
envelope, and the existing L1 handler writes response bytes directly. No remote
Sentry delivery or production HTTP endpoint is claimed. All 51 affected
Architecture and 191 affected Unit controls pass, with zero skips. Fresh scoped
independent verification of this correction remains required before local Step 4 closeout; complete
source/shard/provider qualification remains Step 6.

### P03-1 Step 4 exported trace correction (2026-10-26)

**Reviewed source:** `f814d97e`. Two fresh scoped read-only reviewers inspect the
diagnostic correction: privacy/source-reaching sinks (GPT-6 Astra, xhigh) and
storage/integration/corpus behavior (GPT-6.1 Sol, xhigh). The privacy slice
approves the four named sink controls. The integration slice identifies a
separate exported-trace path in the pinned EF instrumentation: its CommandError
handler copies the original provider message into span StatusDescription before
the adapter can translate it. This is a verified review finding only after a
new actual exporter reproduces it; the initial four-sink result alone does not
establish trace safety.

The actual API tracing composition now drives a synchronous test exporter that
serializes at Export time. The pre-fix immediate profile case exports an EF
Error span containing every planted synthetic value; the negative log/TRX is
retained separately. The deferred preflight uses direct Npgsql, which the current
tracing composition does not subscribe to; it still reaches real L1 exception
spans. Neither absence of that driver span nor later mutation is a proof of
exclusion from an already exported span.

The bounded correction marks only the actual Identity EF command Activity from
its DbContext interceptor, using a private object custom property with no values,
exported tags or inheritance. An API processor registered before exporters
replaces only marked EF Error descriptions with fixed text. Error status,
ordinary command evidence, trace parents and unmarked sibling descriptions stay
intact. It adds no SQL-text classifier, global scrub or new authority.

All eight focused Integration cases pass with the expanded trace assertions.
Immediate profile and global binding errors export fixed EF descriptions;
deferred failure exports two nonempty L1 Error spans with the fixed translated
type. A sibling-module Error description still equals its actual provider
message. Caller Activity restoration and EF trace parent checks pass. The
existing serialized log/local/Sentry/response controls remain green.
All 51 affected Architecture controls and 194 affected Unit controls pass with
zero skips; the latter selection also includes the existing tenant span
processor's three controls. No remote OTLP or Sentry delivery is claimed.

### P03-1 Step 4 local closeout (2026-10-26)

The same two independent diagnostic reviewers recheck `67d75e62` read-only.
Both approve the trace correction with no remaining verified runtime finding.
They confirm source-owned marker confinement, processor ordering, serialization
at Export, nonempty immediate/global and deferred L1 controls, sibling evidence
and Activity restoration. Their one Minor receipt correction is closed by a
rebuilt CI/Release run against the exact committed source: 8/8 Integration,
zero skips. The normal commit formatter reordered test imports after the earlier
hash; the earlier receipt remains intact and the committed-source hashes/result
are appended separately. Prior qualification is not silently relabelled.

Step 4 is locally complete after both full-step independent review rounds,
verified corrections, the actual diagnostic negative/positive controls and the
additional scoped verification above. Its original 157-case contextual selection
and later eight-case diagnostic/regression selection retain their separate
source/scope; their overlapping cases are not summed into a new total. Final
complete-suite, source-bound shard and combined provider/native qualification
remain Step 6. No HTTP/bootstrap, live authorization or closure delivery is
claimed. Implementation proceeds automatically to Step 5's approved existing
binding admission, with new-user transport still owned by P03-3.

### P03-1 Step 5 — Existing-binding API admission (2026-10-26)

The approved API handoff is implemented: stock validation produces immutable
`VerifiedProviderSubject`, whose optional actor remains compatibility evidence.
The existing credential gate and unchanged issuer/subject quota precede the new
existing-binding stage. Only an Active matching stored binding supplies the
nonnull `ValidatedTenantIdentity` actor before the route-independent resolver.
Unknown, conflicting, nonadmitting and unavailable bindings return the existing
fixed 401/Bearer response, including on credential-bearing public reads. The
closed internal outage reason is `binding_store_unavailable`. Ordinary HTTP
never creates, adopts or repairs a User and adds no endpoint or SDK operation.

The API-owned startup service qualifies the immutable deployment pair. Its
resolver lazily constructs the module reader only for admitted credentials;
anonymous and construction/schema hosts buy no identity connection. A short
physically READ ONLY global owner finishes before tenant reconciliation. Reentry
retains the original verified-subject quota provenance, clears any normalized
identity and rereads the binding; missing or replaced evidence cannot borrow an
earlier identity or permit. Caller cancellation and arbitrary unexpected faults
retain their existing exception handling.

Real provider fixtures use isolated databases per issuer on their owned
PostgreSQL container, not anchor rebinding. Authenticated provider readback feeds
the actual foundation runner before API startup and audit baselines. The warm
provider and native BFF preserve complete identity, refresh and logout contracts.
A real-provider control removes only a known persona's compatibility actor,
proves the signed access token lacks it, and still reads AccountSite with its
stored actor. Profile/credential restoration and a fresh complete-token positive
follow. This is not native actorless/new-user publication.

Local launchers preserve the selected authority/issuer pair only in API
configuration and conflict-check each input source. Web startup does not consume
that pair. Native subject validation aligns to 255 characters while retaining
the complete actor/tenant publication contract.

Qualification before implementation review:

- CI/Release solution build has zero warnings/errors. All 2,770 Unit, 306
  Docker-free Integration and five Contract cases pass with zero skips.
- The initial complete Architecture run is 563/570 with seven failures and
  zero skips. Those failures identify missed foundation census backfills:
  five synchronous contextual tables need explicit asynchronous-inventory
  exclusions; the sixth Organization SQL reader needs exact command/signature/
  binding controls; genuine typed Role/seed DTO readers need method-level read
  enumeration; and pure stdin-parser tests belong outside the Docker-only
  directory. The corrected classes run 90/90 with zero skips, preserving old
  predicates and adding dirty controls. No new aggregate write exception or
  production SQL change follows. These are separate receipts, not a relabelled
  all-green full run; final source-bound complete qualification remains Step 6.
- The first real-provider/native selection is 50/51. Its one failure is an
  exact fixture binding map omitting three legitimate acquisition-cleanup
  subjects. Only those names are added; unknown/actor-conflict refusal remains.
  All five quota cases rerun green together with ten actual binding HTTP cases
  (15/15, zero skips). Overlapping cases are not summed into a new population.
- Actual binding cases prove actorless/equal success, unknown/conflicting/
  Closing/Closed/collision refusal without allocation, early/quota precedence,
  physically read-only app-role/GUC ownership, startup missing/mismatched/wrong-
  role refusal, real store outage with serialized fixed diagnostics, and cleanup
  after a blocked lookup cancellation. One physical connector is reused only
  after identity/tenant GUCs and lock/transaction ownership are cleared.
- Four final cases pass for the matched measurement control and the relocated
  pure parser. Collision setup first proves the digest CHECK rejects mismatch,
  temporarily removes only that exact constraint in its isolated fixture,
  restores it and proves the independent constraint/trigger/policy/privilege
  state again. An EXPLAIN numeric-format assumption and stronger early authority
  configuration refusal are corrected in tests, without changing production.
- All 264 focused frontend cases pass, including signed 255/256 subject
  boundaries and API-only authority projection. Web typecheck and lint pass.

The small local measurement uses 32 audited bindings and twelve interleaved
samples per authenticated lane after warmup. A current-code observational control
returns one independently witnessed Existing result only for the exact verified
input; all other authentication, quota and tenant reading remain real. It is
neither historical nor security-qualified. Median control/production latency is
5.68/8.33 ms, a 2.65 ms observed difference. Driver statement totals are 60/108;
production includes twelve binding SELECTs and one observer proof SELECT per
lookup. The separate anonymous lane totals 60 statements. The unforced small-
population plan is Limit/Seq Scan, one actual row; planning/execution is
0.324/0.039 ms. Eight concurrent HTTP requests occupy four of four bounded pool
slots, then leave zero active/transaction owners. No production percentile,
capacity, throughput or speed-ratio guarantee follows.

Both fresh implementation review rounds remain required before Step 5 closes.
Complete source/shard/discovery and complementary A/B CI evidence remain Step 6.

### P03-1 Step 5 review round 1 and read-diagnostic correction (2026-10-26)

**Reviewed source:** `5f7a0625`. Three fresh independent read-only reviewers
inspect API authentication/quota/reentry (GPT-6 Astra, xhigh), binding storage/
cleanup/measurement (GPT-6.1 Sol, xhigh), and native/frontend/fixture/corpus
handoff (GPT-6.1 Sol, xhigh). None edits files or executes builds/tests. The API
slice approves. The storage slice requests one Major correction; the native
slice approves runtime and identifies Minor stale implementation-status text.

The verified Major is EF `QueryIterationFailed` (10100), emitted before the
binding adapter translates the provider exception. The earlier permission-fault
receipt proves raw exception logging but contains none of its selected personal
canaries. A new actual HTTP case independently reproduces value disclosure:
a restrictive fixture-only RLS function raises a PostgreSQL error containing
the retained subject's neutral/nested/sensitive values. HTTP still returns 401,
but pre-fix serialized Serilog contains those values. The negative receipt is
retained separately; trace/response exclusion alone is not log safety.

Identity now replaces only its EF query-failure event, alongside the existing
save-event replacement. Its command interceptor emits fixed event 3192
(`IdentityStorageReadFailed`) for LINQ/FromSql read failures without the provider
exception or payload. Sync/async paths retain an operational signal; the adapter
still owns the original failure, cancellation and uniform HTTP refusal. No
binding-store query, authentication mapping or global/sibling logging changes.

Fresh post-format CI/Release qualification passes 26 Integration, 51 Architecture
and 216 Unit cases, with zero skips. The actual HTTP canary reaches the provider
fault, returns 401 and exports a nonempty fixed Identity Error span, with no raw
provider type, SQLSTATE or canary in serialized logs, exported traces or response.
The RLS policy/function inventory is restored exactly and a fresh HTTP positive
passes. Separate sibling iteration-event/span and sync/async FromSql controls
remain positive. Existing save-failure, cancellation, connector reuse and cleanup
regressions are included; overlapping populations are not summed with earlier
receipts.

Current glossary, Data Protection, Security 11 and Identity module descriptions
now match implemented storage/proof ownership, preserving historical records.
The Contract factory's exact identity startup-worker removal was tested in the
working tree but accidentally omitted from the initial implementation commit.
It is included in this correction; an exact committed-source Contract rerun
follows before the second fresh full-step review. No production startup bypass
is added. Final complete-source/shard/provider qualification remains Step 6.

### P03-1 Step 5 review round 2 and local closeout (2026-10-26)

**Reviewed source:** `cb5937a8`, full Step 5 range from `7d29843a`. Two new
independent read-only reviewers inspect API/storage/quota/reentry and the
diagnostic correction (GPT-6 Astra, xhigh), and native/frontend/fixture/privacy/
corpus boundaries (GPT-6.1 Sol, xhigh). Both approve their assigned slices with
no verified actionable finding. Neither edits source or executes tests. Both
independently match committed-source/receipt hashes; the exact committed
Contract rerun passes 5/5 and the binding/diagnostic/cleanup selection 26/26,
with zero skips. Normal commit formatting reordered test imports, so these
receipts are appended separately rather than relabeling earlier source hashes.

The parent also runs the complete guarded frontend population. Its first result
is 2,272/2,277 in the first package: five migration-build controls still expect
alphabetic module order after Tenancy/Platform. The approved Makefile correctly
places Audit then Identity before the remaining discovered modules because of
the central creation witness. Only the independent test expectation gains those
two named priorities. The native/privacy reviewer separately approves this
test-only correction; discovery, added-chain, exact argv, credential confinement,
fresh build and failure propagation controls remain intact. All ten focused
cases and the complete guarded frontend rerun pass: 2,380 cases in two packages,
zero skips/todos. The failed full run remains a separate failed receipt.

Step 5 is locally complete after both fresh full-step review rounds, verified
corrections and committed-source requalification. Step 6 now owns the complete
source-bound suite/shard/discovery and PostgreSQL/Keycloak/native/browser/A–B
qualification, final current-corpus sweep and reviewable PR. Inherited Standard
05/06 current-state wording is included in that sweep. No packet-complete or
later onboarding/permission/closure delivery claim follows from local Step 5.

### P03-1 Step 6 — Complete qualification inventories (2026-10-26)

The final step reconciles the source-bound CI inventory and current carriers
before freezing the hosted qualification source. The actual fixture census grows
from 119 to 130 classes: eleven new Identity fixture consumers are added, with
none removed. Architecture/ordinary shard counts remain 6/12; algorithms, salt,
weights, filters, required contexts and all time/cleanup ceilings are unchanged.
This witness-only edit moves no case in either layout on the same manifest.

Actual local Release VSTest discovery supplies 6,394 cases: 3,651 non-Docker and
2,743 Docker, with the latter split into 2,712 ordinary and 31 native cases.
Production discovery parsing, manifest/union/selector predicates and both
assignment algorithms validate every case exactly once. A and B have genuinely
different co-membership, including shared-fixture selectors. This is local
discovery/assignment proof, not hosted execution, cleanup or wall-fit evidence.
Unmeasured method weights keep their existing advisory default.

The fresh local CI/Release solution build passes with zero warnings/errors;
all 2,770 Unit, 306 Docker-free Integration and five Contract cases pass with
zero skips. Frontend typecheck/lint and the Step 5 complete guarded 2,380-case
population pass. The eight-file development realm census passes all 409 cases.
Plan/result controls pass 72/30; native owner/provisioning controls pass 87/18;
the existing supply-chain, realm-gate, native-environment, ADR, fragment, roadmap
and log-index controls pass. OpenAPI and generated SDK snapshots are byte-equal
to current `main`.

The complete local ordinary run is still executing at this entry. It identifies
an obsolete reversal preflight: the test expects Step 3's 50 tables/five total
Identity functions, while Step 4 installs 55/twelve. That first failure occurs
before reversal, so it establishes no Down failure. Only the independent expected
counts and explanations change; real reversal, empty-policy/function checks,
reapplication and app-role positive evidence remain required before closeout.
No migration or production behavior changes in this correction.

A read-only current-corpus sweep verifies stale preimplementation claims against
the actual source and Step 3/4/5 records. Database/Testing/Permissions standards,
the test catalogue, isolation/audit/cross-cutting architecture and Identity's
audit matrix now describe implemented foundation subjects. Canonical discovery
and the phase Status block replace duplicated obsolete population/review state.
Accepted bodies and dated history remain intact; later live permission,
HTTP/new-user, operator and closure owners remain P03-2/3/9/10.

Both fresh Step 6 review rounds, actual reversal qualification, complete
Architecture/ordinary/native/provider/browser results and source-bound complete
hosted A/B qualification remain required. This entry does not close the packet.

### P03-1 Step 6 review round 1 and carrier correction (2026-10-26)

**Reviewed source:** `124ff4a`, whose tree equals `6c379f12`. Two fresh independent
read-only reviewers inspect CI/source/discovery accounting (GPT-6 Astra, xhigh)
and current corpus/reversal/authority boundaries (GPT-6.1 Sol, xhigh). Neither
edits files or executes tests. The CI slice approves: raw discovery messages
match every retained manifest part, both assignments are exact and genuinely
different, and the fixture-only edit cannot select, omit or move cases. The
reversal slice confirms the independent 55-table/twelve-function expectations
and unchanged removal/reapply controls, with actual execution still required.

One grouped Major corpus finding is verified: active standard introductions,
the standards index, catalogue introduction and conceptual hierarchy still say
Identity implementation/registry/Role or migration work is absent. These
contradict the real foundation and the corrected detailed sections. The stale
tails now yield to actual implementation facts and canonical current-state
pointers. The Permissions standard remains **Adopted** consistently in header
and index: the finite foundation exists, while live enforcement remains P03-2.
The conceptual relationship diagram exposes only the User key and links actual
columns to their owner, rather than implying unused email/raw-issuer storage.
Mutable decision-index pending tails also become owner pointers. No Accepted
ADR body, dated history or later-phase authority changes.

Before this round, the merged PR #37 ancestry is reconciled on `development`
without changing a file. `origin/main` at `7cad8402` and already incorporated
ancestor `b1f19f5b` have the identical tree
`9eb11ced9d70f66dc84ad629f409801f1aeebbcf`. The ancestry-only merge preserves the
complete current tree and makes the PR comparison contain only subsequent work;
no rebase, force push or branch switch is used.

Documentation/corpus checks qualify the verified carrier correction before a
second fresh source-review round. Running local ordinary work and final-source
hosted A/B/full-suite qualification retain their separate source and result
boundaries. This source review does not establish successful execution or close
Step 6.

### P03-1 Step 6 full ordinary failures and bounded corrections (2026-10-26)

The initial complete local ordinary run finishes in 47m08s: **2,630 Passed,
82 Failed, zero skipped, 2,712 total**. It uses the already built pre-correction
source from `83f701df`; intervening source edits do not requalify that binary.
The original log/TRX and per-case failure classification remain intact. This is
not a successful complete-suite receipt, and corrected subsets are not relabeled
as one.

Verified failures form these groups:

- Two migration-census cases retain Step 3 table/function expectations. The
  independent full reversal expectation and Platform-only surviving-table union
  now include the contextual subjects. Actual reversal, six-history-only empty
  schema, zero functions/policies and reapplication/app-role positives pass.
- Forty-six worker/default-delivery cases reach the new mandatory Identity
  startup service without owning an authority configuration. Three dedicated
  messaging factories exclude only that named unrelated hosted service. Actual
  producer/delivery workers, credentials, guards, health, deadlines and cleanup
  assertions remain intact. Production startup and real identity fixtures retain
  mandatory qualification.
- Twenty-six parent-scope structural cases find the two undeclared Membership
  children. The detector now joins both actual Identity EF relations and the
  independent applied schema, retaining all earlier Education/job predicates.
  Twenty-two added clean/dirty/restoration cases cover missing/wrong FKs, model/
  declaration omissions, trigger metadata, exact invoker body, timing, locks,
  tenant/parent/null comparison, SQLSTATE and classification metadata.
- Eight diagnostic cases expose additional actual EF spans. Distinct tracing
  compositions create nested spans for one command; marking only the innermost
  leaves outer provider messages unprotected. The full receipt's additional
  subscribers are not attributed to a guessed lifetime fault. A deliberate
  two-composition control establishes the defect independently.

The parent census also verifies an implementation mismatch with the existing
canonical `23514` parent-scope contract. A narrow forward migration replaces
only `fn_identity_contextual_mutation_guard`: missing/mismatched Membership
parents use `23514` with fixed `membership_parent_scope` constraint metadata;
revoked-parent and other existing `P0304` classifications stay. That metadata
preserves the adapter's existing fixed Graph descriptor rather than producing
a provider outage. Original migration output is byte-identical; no table,
trigger, grant, owner or data changes. A real fresh/predecessor/head/down/head
control proves exact function identity/OID, ACL, owner, invoker/volatility/search
path and unchanged siblings, with app-role error witnesses at every version.

Fresh qualification passes **363 Integration and 28 Architecture cases**, zero
skips, including all seven affected structural/worker/membership/rollback classes.
The added migration case and parent-census rows require refreshed final discovery.
The first CA1859 build error is retained separately from the corrected build.

For diagnostics, four sync/async × standalone/real nested-Tenancy controls first
fail on unchanged trace-boundary source. Both actual production tracing
compositions serialize two distinct Identity Error spans for one independently
witnessed provider command: the outer span contains neutral/nested/token
canaries. Independent started/stopped IDs, exporter coverage and ancestry
checks run before the failing exclusion assertion. No remote telemetry delivery
is claimed.

The correction uses a per-IdentityDbContext interceptor. It captures command ID
and the exact caller before synchronous command creation, consumes only the
matching post-broadcast creation event, validates the complete EF-only interval
and marks only its newly created spans. The existing caller is never marked,
including a real EF command from another module. The before-export processor
is unchanged. No SQL classifier, global scrub/registry, SDK reset or discarded/
deduplicated span supplies the proof.

All **23 diagnostic plus seven cleanup controls**, **216 Unit** and **51
Architecture** cases pass, zero skips. Both exporters cover every independently
observed EF ID; all Identity Error spans are fixed and canary-free. The actual
nested Tenancy command keeps its original provider message, and caller status,
ancestry and Activity restoration remain intact. These focused populations
retain their scopes rather than replacing the failed full run. A further 81
corpus/inventory/FK Architecture cases pass after the current-carrier updates.
Backend setup guidance now names the installed authority/runtime configuration
prerequisite and the managed local launcher.

Committed-source requalification, the fresh second Step 6 review round and
complete source-bound hosted A/B execution remain required before closeout.

### P03-1 Step 6 review round 2 and complete parent-stack correction (2026-10-26)

Two fresh independent read-only reviewers inspect `4fc7e1a7`: the SQL/privacy
slice (GPT-6 Astra, xhigh) and accounting/corpus/reversal slice (GPT-6.1 Sol,
xhigh). Both find the same remaining parent classification defect. The
admission trigger runs before the mutation guard for assignment INSERTs, so
missing or RLS-hidden Membership parents still return `P0302`. The prior
visible-parent mismatch witness did not exercise that branch. A real app-role
six-case control first reproduces the defect: four pass, two fail. The retained
committed-source selection also reports **409 Passed, five Failed, zero skipped,
414 total**; all five failures are the schema inspector expecting the original
function body because it reads only the first two migrations.

A second forward migration replaces only
`fn_identity_membership_admission_guard`. Missing/hidden Membership parents now
use the same fixed `23514` / `membership_parent_scope` refusal as the mutation
guard. Actual unknown or nonadmitting User targets retain `P0302`. No function,
table, trigger, data, owner or grant is added. Original migration SQL remains
byte-identical. Each correction's real round-trip control pins its own exact
migration version, preserves function OID/owner/ACL/security mode/volatility/
search path and sibling definitions, then converges with the fresh latest chain.

The inspector now derives effective function definitions from every compiled
Identity migration in actual EF order. It recognizes replacements by normalized
signature and retains the final body; duplicate creates, undeclared replacements
and unsupported declarations fail. Exact body/privilege/caller checks and the
twelve-function/five-definer/eighteen-caller census remain. Nine independent
parser controls exercise replacement order and rejected forms. The stale
Security introduction now points to the canonical phase Status block while
retaining P03-2 ownership of live membership execution.

The corrected parent-stack, function-schema, reversal and parent-structure
selection passes **159/159**, zero skips. Both child tables have missing,
RLS-hidden and visible NULL-organization-mismatch controls, each with an actual
app-role positive write before refusal. These focused results do not replace
complete source-bound qualification or imply packet closeout.

An additional **77/77 Integration** startup/worker/binding/diagnostic cases and
**28/28 Architecture** cases pass, zero skips. These selections remain separate
receipts; overlapping earlier populations are not added into a new suite total.

### P03-1 Step 6 correction approvals and first hosted assignment (2026-10-26)

Both second-round reviewers independently approve correction commit `3754e4ea`.
They verify source/artifact hashes, the exact two negative failures, corrected
rows and historical SQL equality without executing tests themselves. No concrete
actionable source finding remains in those correction verifications. Complete
Release compilation passes with zero warnings/errors. Fresh local Unit **2,770**,
offline Integration **306** and Contract **five** cases pass with zero skips.
Actual Release discovery contains **6,437** unique cases: 3,651 non-Docker and
2,786 Docker (2,755 ordinary plus 31 native). Both assignments cover the exact
population once and genuinely change co-membership. Local discovery is not a
hosted-execution or wall-fit receipt.

The first complete hosted A assignment is
[run 40270967586, attempt 1](https://github.com/HodeTech/LearnStack/actions/runs/40270967586),
on `3754e4ea`. **24 jobs succeed; ordinary-6, ordinary-9 and the Integration
aggregate fail.** All Architecture pieces, the backend aggregate, native proof,
frontend, meta, secret scan, OpenAPI and public demo pass. Retained immutable
archives and actual case IDs show **6,401 Passed, one Failed and 35 without a
result**. No complete-suite success is inferred from the passing pieces.

Both ordinary owners reach their unchanged deadline and require forced cleanup.
Ordinary-6 continues to pass renderer cases until 31.66 seconds before that
deadline; its remaining serialized public/diagnostic cases are queued. Actual
fresh account fixtures also successfully qualify Identity startup. Two read-only
source/evidence investigations find no shared Identity startup deadlock. This
does not prove that every unfinished case started. The original artifacts and
incomplete cleanup/ownership outcomes remain intact.

Ordinary-9 additionally contains a real browser failure after 28.38 seconds:
`browser-devtools-missing`. The owned stock Chrome remains live during the
ten-second readiness window but does not create its port file. This is before
CDP attachment, page navigation or authentication. The lower-level cause is not
established by the closed diagnostic; it is not labeled transient or repaired
by a blind retry.

A separate verified fixture dependency is corrected: the BFF browser now uses
`--password-store=basic` and `--use-mock-keychain`, matching the existing admission
fixture and
[Chrome 155's own test launcher](https://chromium.googlesource.com/chromium/src/+/refs/tags/155.0.8059.39/chrome/test/base/test_launcher_utils.cc#56).
Chrome's source explicitly avoids native password-store UI/timeouts in tests.
This two-line isolation correction changes no sandbox, HOME, readiness or
cleanup limit and adds no fallback. It does not establish causality for the
first hosted browser failure. Existing focused frontend controls pass **89/89**;
the actual local browser positive/history-counter case passes **1/1**, zero
skips. Actual Ubuntu browser and complete complementary A/B execution remain
required before closeout.

### P03-1 Step 6 measured timing revision (2026-10-26)

[ADR-0063's reviewed-plan mechanism](../decisions/0063-complete-bounded-ci-test-shards.md#versioned-plan-and-independence)
owns this timing-only revision. All 24 first-attempt archives are retained with
API digests and exact source/run/attempt identity. Selected discovery IDs bind
every completed row; complete passing owners also have exact TRX/prefix equality.
Of 1,577 observed selector groups, 1,563 are fully Passed and fourteen are
censored. An interrupted owner is never treated as a complete successful owner.

Weights use ceiling milliseconds of the complete selector's case-duration sum,
minimum one. Complete passing ordinary owners supply their current observations.
For interrupted ordinary owners, fully observed passing groups may add missing
measurements or increase existing measured weights; they never reduce an existing
measurement. Failed/partially observed groups retain their old weight or unknown
default. Historical slower Architecture observations remain floors. Each used
source names the run, source SHA, owner, artifact ID, entry digest, unit and scope;
the two interrupted JSONL prefixes are explicitly distinguished from complete
passing TRX receipts.

The revision measures 189 previously unknown selectors; five remain unknown.
Only `weights` and `sources` change. Literal JSON values for counts, algorithm,
salt, default, argument bounds, fixed-cost observations and the 130 fixture
classes are byte-identical. The same twenty execution owners, twenty-minute
whole-job ceilings and seven required names remain. No selector, trait or case
is removed. Both assignments retain all 6,437 unique cases exactly once, with
independent Architecture/ordinary/shared-fixture co-membership witnesses.

The reviewed plan digest is
`7e3357bf961f6ed215a306a72d8b616c7bf7462b098205c1094885d422062804`;
manifest digest remains
`29ded01d790e2f1ecdcc4dc73c349d67e52206afa4034c4e1240c751278be2b5`.
The full assignment diff moves 2,961 cases in A and 2,947 in B. Mixed advisory
ordinary maxima become 649.438s and 719.914s, respectively; these sums are not
execution-wall bounds. Actual first-attempt pre-owner/setup/discovery intervals,
test-process versus case-body differences and unobserved fixture costs remain
separate. The failed browser observation is not reduced into a cheaper positive
weight. Both full variants on the new final source remain required; neither
these estimates nor the preceding-head successes establish fit or close Step 6.

### P03-1 Step 6 qualification closeout and PR handoff (2026-10-26)

All six implementation steps complete their two fresh independent review rounds.
Verified correction reviews approve `3754e4ea`; supplemental reviews approve the
bounded browser fixture change `e05ba3ad` and timing revision `193d61cc`.
The preceding entries retain failed runs, actual corrections and proof limits.
No new decision or implementation scope is inferred from a passing test.

The final implementation source is `193d61cc17ab66e9c392039d5da2ad112b33bb8f`.
[Run 40274315368](https://github.com/HodeTech/LearnStack/actions/runs/40274315368)
completes **attempt 1 (A) and attempt 2 (B)** on that same source, workflow,
reviewed plan, test population, SDK/console and adapter identities. Both full
attempts have **27/27 successful jobs**, including the seven required contexts.
Each supplies 25 immutable, API-digest-verified artifacts and twenty producer
packages with complete natural ownership/cleanup, no forced stop, exact actual
TRX/completed-case identities and zero skips. Pure production aggregate
validators independently reproduce both protected results from retained data;
this does not simulate hosted execution on the workstation.

Each attempt passes **6,437 .NET cases**:

- 2,770 Unit, 570 Architecture, 306 offline Integration and five Contract cases;
- 2,755 ordinary Docker and 31 native cases.

Both complete frontend guards independently report **2,380 Passed in two
packages**, zero skips/todos, with lint, typecheck, production build and native
ingress controls. Both actual public-demo receipts qualify nine linked pages,
27 Lighthouse JSON/HTML report pairs, budgets and completed process/Compose/
private-file/environment/lock cleanup. Actual provider/native tests remain
separate from mocked protocol unit controls; serialized Sentry/telemetry proof
does not claim remote delivery or full manual accessibility coverage.

The two assignments genuinely change selector co-membership: A-only/B-only pairs
are **6,143/6,469 Architecture**, **61,057/66,502 ordinary** and **60,766/66,203
shared-fixture**. Base/native membership stays identical. The longest complete
job is **997s in A / 976s in B**, within the existing applicable ceilings.
The real browser positive/history-counter and two-host warm-realm refresh/logout
journeys pass under both arrangements. These successes do not retrospectively
establish the lower-level cause of the first hosted browser failure.

The plan digest is
`7e3357bf961f6ed215a306a72d8b616c7bf7462b098205c1094885d422062804`;
manifest digest is
`29ded01d790e2f1ecdcc4dc73c349d67e52206afa4034c4e1240c751278be2b5`.
Retained full verification receipts hash to
`8572744e36430812c476c8e59ef2ba9c77c262bb2038e03bf640652b2b595161`
(A) and
`b0cc4a5c0eff90852f5de763ff94c39ec1e79496f27d4d1a8cd23d089278d0e9`
(B). Original API metadata, archives, raw result rows and reports retain their
own source/attempt identity rather than being relabeled as another head.

The independent accounting reviewer verifies both attempts directly: all fifty
archive digests/sizes, package byte equality, actual TRX/JSONL/discovery outcomes,
disjoint artifact identities, natural cleanup, toolchain hashes, native/provider/
browser witnesses, public-demo report hashes and the three co-membership
comparisons. The cross-attempt audit reports no finding and explicitly limits
its qualification to `193d61cc`.

Only closeout evidence/status documentation changes after the qualified
implementation source. The final documentation head requires normal complete
PR qualification; the recorded A/B receipts remain explicitly bound to
`193d61cc`. The PR comparison uses `origin/main`, including the already recorded
ancestry reconciliation, and every non-merge commit passes the actual strict
commit-message hook. No branch switch, published-history rewrite or unrelated
worktree cleanup is performed.

P03-1 delivers audited global storage/common creation, contextual storage/seed,
existing-binding admission and privacy/diagnostic foundation. It opens no new
HTTP/SDK operation. Live membership/permission execution remains P03-2; usable
new-user HTTP/BFF onboarding remains P03-3; production first-admin/rebind and
global closure/redaction remain P03-9/P03-10. P03-2 is the next packet, not part
of this implementation. The phase Status block owns review/merge state.

### P03-1 PR 38 review corrections (2026-10-26)

Two maintainer-supplied reports review the `7cad8402` → `d32f3101` PR population.
Their findings are review data, not authority to change Accepted decisions. Three
independent verification slices check tooling/domain, SQL/audit and mutable
carriers against `d32f3101`; their source review does not claim executed suites.
The correction pass preserves ADR-0072/0073/0074 and the separately owned future
gates. No new HTTP route, role, owner function, tenant setter, credential claim,
creation budget or distributed-session policy is admitted.

| Finding population | Verified correction or disposition |
|---|---|
| P0-1, tooling source selection | `make migrate` now delegates execution to a bounded owner. The migration connection and optional authority pair come from one complete source; partial/mixed selections refuse before any .NET child. Complete process input takes precedence over file defaults. Installer children receive only the selected credential/pair and tooling prerequisites. Actual recipe controls cover both sources, schema-only input, refusals, build/order and private-environment isolation. |
| P0-2, first report's three documentation findings | Mutable navigation/model/startup carriers are reconciled to accepted Identity storage and Phase 03 Status. Direct API startup requires the installed matching authority even for anonymous reads; it does not load root `.env`. Unused global email/profile, triple-key roots, persisted Platform roles and unqualified contact/custom-field claims no longer describe the delivered foundation. Future field/UI decisions retain their named gates. |
| P0-3 | Actual app-role global audit INSERT controls now exercise eleven malformed attribution/scope cases with valid committed twins, owner/platform refusals, and an independently isolated WITH CHECK control. Missing scope can reach the BEFORE guard first; the proof does not require every refusal to be 42501. |
| P1-1 | The two-second transaction timeout remains. Save/preflight contention maps to the existing fixed conflict refusal, including Npgsql's exact transient execution-strategy wrapper. Real held-row tests exercise both adapter and seed handler, rollback-only state and no partial profile. Physical COMMIT uncertainty is unchanged. The report's claim that the existing provider exception maps to HTTP 500 is incorrect; it maps to 503 and current writers are internal seed commands. |
| P1-2 | Completed ordinary binding lookups dispose their transaction and return the physical connector before tenant dispatch, retaining the scoped connection object for its next owner. A one-slot real pool admits a second HTTP binding/tenant read while the first request's scope remains held; the original request subsequently reopens its own tenant owner. Cancellation/clean connector reuse remains covered. |
| P1-4 | An unclassified global creation releases its prepared owner before returning the central deployment error; the binding service no longer reports that configuration defect as storage availability. Created success additionally requires the common owner's recorded Committed outcome. |
| P1-6 | Ordered SQL operation digests pin the four existing Identity migrations' Up/Down output despite their shared helpers. The forward correction preserves the original helper outputs and model; current/predecessor transition controls compare exact function identity/body/ACL. |
| P1-7 | The profile seed declaration now binds its expected User and single Role assignment through closed admission. Both are checked before/after waiting; replaced User/assignment controls refuse without mutation. The retained v1 fingerprint algorithm and fixed non-null persona profiles are unchanged. Generic both-null profiles are not part of this seed contract. |
| P1-10 | Real competing first-admin commands now prove one successful graph/MUST row; final-admin checks have globally eligible/ineligible twins. Restrictive organization UPDATE/DELETE controls remove only the target policy and retain the independent WITH CHECK/parent guard, distinguishing normal zero-row filtering from the resulting closed exception before restoration. Exact per-command permission mapping detects a swap that preserves the distinct registry set. |
| 5.1–5.4, remaining source corrections | Already-cancelled requests perform no binding call; verified provider evidence has fixed formatting; disposed global-unit state clears. Nonstorable subject text refuses before hashing/storage; BFF code/refresh align whitespace and UTF-16 limits while preserving exact valid non-ASCII subjects. Membership targets require UUIDv7. An unused SQL-string property is removed. Ingress requires complete pairs per participating source; identity-child failures use the closed identity stage and the unchanged total pass budget. |

The child-write/revocation race in 5.2 is reproduced with actual PostgreSQL 18
app-role sessions: `FOR KEY SHARE` permits a new assignment to commit alongside
an uncommitted non-key revocation; final state is Revoked with that new child.
The forward, function-only migration
`20261026102944_serialize_identity_parent_lifecycle` changes the parent locking
read to `FOR NO KEY UPDATE`. The real tracked `Membership.Revoke` regression
then observes the waiting inserter refuse after the revocation commits. Its
predecessor twin still reproduces the defect. Revocation retains its shared
tenant plan and acquires no new User fence. No historical migration is rewritten.

Several requested changes are not current defects or would select new policy:

- Accepted ADR bodies and source-bound dated readiness/proof entries remain
  historical records. Mutable summaries are corrected; historical pending text
  is not rewritten into a live dashboard.
- Exact function bodies, five definers/eighteen callers and the Audit→Identity
  migration-order control already exist. The primary catalogue description is
  corrected; the existing configuration-construction scanner is not described
  as a census of every physical connection checkout.
- Audit-owned witness installation from the Identity chain, confined Boolean
  owner functions, broad contextual UPDATE grants, cross-module root-FK absence
  and the SQL-credential threat boundary are explicitly accepted. No newly
  demonstrated sanctioned writer bypass warrants redesigning those contracts.
- A typed tenant sentinel does not reserve the same bits in every identifier
  type. Qualification is startup-only and fail-closed; a new retry/readiness or
  public 503 policy is not inferred from the uniform-401 diagnostic. Provenance
  remains available to enforce re-entry without another subject debit.
- Neither all Error events nor arbitrary provider inners have been shown to
  escape the closed global boundary in this review. No ad hoc module tracker
  capture is added in conflict with L1 ownership. Deployment alert thresholds
  remain Phase 11; production Identity lifecycle/permission writers retain
  P03-2/3/9/10 ownership. Extra database UUID checks, new creation-attribution
  immutability and additional display-name restrictions are not silently adopted.
- Both A/B attempts retain twenty-five original archives each. Rehashing all
  fifty against original API metadata, extracted package bytes and twenty-seven
  Lighthouse report pairs per attempt supports the existing source-bound claims.
  Remote retrieval limits do not invalidate retained evidence. The intermediate
  159/77 TRX populations and both separately dated timing observations exist;
  the reports' unlocated 197/256 claim supplies no correction target.

Local qualification records 2,779 unit passes, 195 selected real Identity/API/
PostgreSQL/Keycloak passes, and 2,402 guarded frontend passes in two packages,
with zero skips/todos in those completed runs. Final local follow-ups pass the
44 Identity/auth/corpus architecture cases, 306 Docker-free integration cases
and five contract cases, with a zero-warning Release solution build. The full
571-case Release run precedes the final global-wrapper refinement; its affected
architecture population is the separately rerun 44-case set. The two additional
private-file recipe controls pass in the final 24-case tooling selection.
The local command sample observes
production-guard warm p95 7 ms (twenty warm samples; four cold samples), versus
12 ms in its same-code observation-only counterfactual, with peak two/four pool
slots and final zero busy slots. This is a local observation, not an enforced
CI SLO, historical baseline, production capacity or proof of a speedup.

Failed qualification is retained separately: the first selected real run has
188 passes/two failures exposing EF's contention wrapper. Its corrected run
passes 193/193 including the subsequently added organization controls. The final
195/195 run additionally covers real transient global EF read/write failures
with personal-data canaries and the EF-generated forward migration; the model
snapshot remains byte-identical. The global storage boundary translates only
Npgsql's exact transient wrapper shapes, preserving unrelated invalid operations
and cancellation. An
initial Debug architecture run is interrupted after repeated compiled-boundary
failures; the canonical Release controls pass, and the full Release suite is
passes 571/571 separately. An initial guarded frontend run overlaps the architecture
mutation census and fails two cold-development RSC proofs. Focused clean and
trace-mutant controls, then the entire serial guarded run pass without timeout
or protocol changes. Resource contention is plausible, not established as the
original failure's sole cause. The local test guide now uses CI's Release
configuration and serial heavy qualification.

The correction commit still requires two fresh independent review rounds and
current-source hosted checks. These local results do not relabel the earlier
`193d61cc` A/B receipts or claim a new hosted execution.
