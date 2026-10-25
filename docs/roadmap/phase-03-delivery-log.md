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
- [P03-1 — Identity authority and membership foundation](#p03-1--identity-authority-and-membership-foundation) — 197 lines
  - [P03-1 proposed decision package (2026-10-25)](#p03-1-proposed-decision-package-2026-10-25) — 130 lines
  - [P03-1 proposal review round 1 (2026-10-25)](#p03-1-proposal-review-round-1-2026-10-25) — 35 lines
  - [P03-1 proposal review round 2 (2026-10-25)](#p03-1-proposal-review-round-2-2026-10-25) — 30 lines

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

**Current validation.** Pending on the proposed documentation source. There has
been no backend/frontend/provider execution or implementation qualification in
this decision pass; review and mechanical results are appended after completion.

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

The subject EF filter and direct baseline catalogue ownership are also explicit;
no tenant override lookup or new context bypass is inferred for global rows.
The existing native API response budget remains ten seconds; cancellation ends
waiting/publication, not proof that centrally owned audit cleanup completed.
The corrected proposal is rechecked against source and the PostgreSQL 18
isolation/volatility/policy contracts. A targeted correction verification and
final mechanical checks follow before maintainer approval is requested.
