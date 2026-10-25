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
- [P03-1 — Identity authority and membership foundation](#p03-1--identity-authority-and-membership-foundation) — 582 lines
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
