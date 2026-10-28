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
- [P03-1 — Identity authority and membership foundation](#p03-1--identity-authority-and-membership-foundation) — 2,768 lines
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
  - [P03-1 PR 38 correction review round 1 (2026-10-26)](#p03-1-pr-38-correction-review-round-1-2026-10-26) — 55 lines
  - [P03-1 PR 38 correction review round 2 (2026-10-26)](#p03-1-pr-38-correction-review-round-2-2026-10-26) — 31 lines
  - [P03-1 PR 38 correction fixture census (2026-10-26)](#p03-1-pr-38-correction-fixture-census-2026-10-26) — 15 lines
  - [P03-1 PR 38 measured timing correction (2026-10-26)](#p03-1-pr-38-measured-timing-correction-2026-10-26) — 42 lines
  - [P03-1 PR 38 independent mutation collections (2026-10-26)](#p03-1-pr-38-independent-mutation-collections-2026-10-26) — 77 lines
  - [P03-1 PR #38 current-source A/B and ordinary window proposal (2026-10-26)](#p03-1-pr-38-current-source-ab-and-ordinary-window-proposal-2026-10-26) — 98 lines
  - [ADR-0075 proposal review round 1 (2026-10-26)](#adr-0075-proposal-review-round-1-2026-10-26) — 29 lines
  - [ADR-0075 proposal review round 2 (2026-10-26)](#adr-0075-proposal-review-round-2-2026-10-26) — 27 lines
  - [ADR-0075 maintainer review and revised package (2026-10-27)](#adr-0075-maintainer-review-and-revised-package-2026-10-27) — 103 lines
  - [ADR-0075 revised proposal review round 1 (2026-10-27)](#adr-0075-revised-proposal-review-round-1-2026-10-27) — 20 lines
  - [ADR-0075 revised proposal review round 2 (2026-10-27)](#adr-0075-revised-proposal-review-round-2-2026-10-27) — 25 lines
  - [ADR-0075 acceptance and Step 1 (2026-10-27)](#adr-0075-acceptance-and-step-1-2026-10-27) — 28 lines
  - [ADR-0075 Step 1 review round 1 (2026-10-27)](#adr-0075-step-1-review-round-1-2026-10-27) — 14 lines
  - [ADR-0075 Step 1 review round 2 and closeout (2026-10-27)](#adr-0075-step-1-review-round-2-and-closeout-2026-10-27) — 13 lines
  - [ADR-0075 Step 2 closed budget implementation (2026-10-27)](#adr-0075-step-2-closed-budget-implementation-2026-10-27) — 44 lines
  - [ADR-0075 Step 2 review round 1 (2026-10-27)](#adr-0075-step-2-review-round-1-2026-10-27) — 16 lines
  - [ADR-0075 Step 2 review round 2 and qualification handoff (2026-10-27)](#adr-0075-step-2-review-round-2-and-qualification-handoff-2026-10-27) — 24 lines
  - [ADR-0075 Step 3 fresh hosted A/B evidence (2026-10-27)](#adr-0075-step-3-fresh-hosted-ab-evidence-2026-10-27) — 54 lines
  - [ADR-0075 Step 3 review round 1 (2026-10-27)](#adr-0075-step-3-review-round-1-2026-10-27) — 30 lines
  - [ADR-0075 Step 3 review round 2 correction (2026-10-27)](#adr-0075-step-3-review-round-2-correction-2026-10-27) — 28 lines
  - [ADR-0075 three-step closeout (2026-10-27)](#adr-0075-three-step-closeout-2026-10-27) — 33 lines
  - [P03-1 merge and closeout (2026-10-27)](#p03-1-merge-and-closeout-2026-10-27) — 58 lines
- [P03-2 — Live membership and authorization](#p03-2--live-membership-and-authorization) — 2,690 lines
  - [P03-2 proposed decision package (2026-10-27)](#p03-2-proposed-decision-package-2026-10-27) — 167 lines
  - [P03-2 proposal review round 1 (2026-10-27)](#p03-2-proposal-review-round-1-2026-10-27) — 32 lines
  - [P03-2 independent preflight carrier correction (2026-10-27)](#p03-2-independent-preflight-carrier-correction-2026-10-27) — 19 lines
  - [P03-2 proposal review round 2 and decision readiness (2026-10-27)](#p03-2-proposal-review-round-2-and-decision-readiness-2026-10-27) — 35 lines
  - [P03-2 maintainer review and revised package (2026-10-27)](#p03-2-maintainer-review-and-revised-package-2026-10-27) — 139 lines
  - [P03-2 revised proposal review round 1 (2026-10-27)](#p03-2-revised-proposal-review-round-1-2026-10-27) — 21 lines
  - [P03-2 revised proposal review round 2 and readiness (2026-10-27)](#p03-2-revised-proposal-review-round-2-and-readiness-2026-10-27) — 42 lines
  - [P03-2 joint acceptance and Step 1 (2026-10-27)](#p03-2-joint-acceptance-and-step-1-2026-10-27) — 117 lines
  - [P03-2 Step 1 review round 1 and setup correction (2026-10-27)](#p03-2-step-1-review-round-1-and-setup-correction-2026-10-27) — 26 lines
  - [P03-2 Step 1 review round 2 and closeout (2026-10-27)](#p03-2-step-1-review-round-2-and-closeout-2026-10-27) — 26 lines
  - [P03-2 Step 2 — Confined candidate and committed authority (2026-10-27)](#p03-2-step-2--confined-candidate-and-committed-authority-2026-10-27) — 89 lines
  - [P03-2 Step 2 review round 1 and corrections (2026-10-27)](#p03-2-step-2-review-round-1-and-corrections-2026-10-27) — 43 lines
  - [P03-2 Step 2 review round 2 and closeout (2026-10-27)](#p03-2-step-2-review-round-2-and-closeout-2026-10-27) — 32 lines
  - [P03-2 Step 3 — Policy and operation-bound execution (2026-10-27)](#p03-2-step-3--policy-and-operation-bound-execution-2026-10-27) — 112 lines
  - [P03-2 Step 3 review round 1 and corrections (2026-10-27)](#p03-2-step-3-review-round-1-and-corrections-2026-10-27) — 31 lines
  - [P03-2 Step 3 review round 2 and closeout (2026-10-27)](#p03-2-step-3-review-round-2-and-closeout-2026-10-27) — 19 lines
  - [P03-2 Step 4 — Trusted evidence and finite grant acts (2026-10-27)](#p03-2-step-4--trusted-evidence-and-finite-grant-acts-2026-10-27) — 96 lines
  - [P03-2 Step 4 review round 1 (2026-10-27)](#p03-2-step-4-review-round-1-2026-10-27) — 41 lines
  - [P03-2 Step 4 review corrections (2026-10-27)](#p03-2-step-4-review-corrections-2026-10-27) — 52 lines
  - [P03-2 Step 4 review round 2 (2026-10-27)](#p03-2-step-4-review-round-2-2026-10-27) — 36 lines
  - [P03-2 Step 4 correction verification and closeout (2026-10-27)](#p03-2-step-4-correction-verification-and-closeout-2026-10-27) — 57 lines
  - [P03-2 Step 5 — Live HTTP and application activation (2026-10-27)](#p03-2-step-5--live-http-and-application-activation-2026-10-27) — 82 lines
  - [P03-2 Step 5 review round 1 and corrections (2026-10-27)](#p03-2-step-5-review-round-1-and-corrections-2026-10-27) — 72 lines
  - [P03-2 Step 5 review round 2 and closeout (2026-10-27)](#p03-2-step-5-review-round-2-and-closeout-2026-10-27) — 29 lines
  - [P03-2 Step 6 — Complete populations and qualification (2026-10-27)](#p03-2-step-6--complete-populations-and-qualification-2026-10-27) — 51 lines
  - [P03-2 Step 6 review round 1 and full-suite fixture diagnosis (2026-10-27)](#p03-2-step-6-review-round-1-and-full-suite-fixture-diagnosis-2026-10-27) — 43 lines
  - [P03-2 Step 6 R1 corrections and regression verification (2026-10-27)](#p03-2-step-6-r1-corrections-and-regression-verification-2026-10-27) — 70 lines
  - [P03-2 Step 6 review round 2 and final source corrections (2026-10-27)](#p03-2-step-6-review-round-2-and-final-source-corrections-2026-10-27) — 73 lines
  - [P03-2 Step 6 measured-plan review and qualification freeze (2026-10-27)](#p03-2-step-6-measured-plan-review-and-qualification-freeze-2026-10-27) — 48 lines
  - [P03-2 Step 6 failed qualification and owned cleanup correction (2026-10-28)](#p03-2-step-6-failed-qualification-and-owned-cleanup-correction-2026-10-28) — 50 lines
  - [P03-2 Step 6 Architecture timing correction (2026-10-28)](#p03-2-step-6-architecture-timing-correction-2026-10-28) — 58 lines
  - [P03-2 Step 6 scoped census performance correction (2026-10-28)](#p03-2-step-6-scoped-census-performance-correction-2026-10-28) — 39 lines
  - [P03-2 census performance review round 1 (2026-10-28)](#p03-2-census-performance-review-round-1-2026-10-28) — 17 lines
  - [P03-2 census performance review round 2 and source freeze (2026-10-28)](#p03-2-census-performance-review-round-2-and-source-freeze-2026-10-28) — 27 lines
  - [P03-2 authority proof decomposition and preservation (2026-10-28)](#p03-2-authority-proof-decomposition-and-preservation-2026-10-28) — 40 lines
  - [P03-2 decomposed authority review round 1 and measured plan (2026-10-28)](#p03-2-decomposed-authority-review-round-1-and-measured-plan-2026-10-28) — 40 lines
  - [P03-2 authority decomposition review round 2 and final source freeze (2026-10-28)](#p03-2-authority-decomposition-review-round-2-and-final-source-freeze-2026-10-28) — 27 lines
  - [P03-2 final-source A exhaustion and resolution profiling (2026-10-28)](#p03-2-final-source-a-exhaustion-and-resolution-profiling-2026-10-28) — 59 lines
  - [P03-2 resolution correction review round 1 (2026-10-28)](#p03-2-resolution-correction-review-round-1-2026-10-28) — 17 lines
  - [P03-2 resolution correction review round 2 and source freeze (2026-10-28)](#p03-2-resolution-correction-review-round-2-and-source-freeze-2026-10-28) — 19 lines
  - [P03-2 failed provider startup and bounded diagnostics (2026-10-28)](#p03-2-failed-provider-startup-and-bounded-diagnostics-2026-10-28) — 47 lines
  - [P03-2 startup diagnostic review round 1 (2026-10-28)](#p03-2-startup-diagnostic-review-round-1-2026-10-28) — 14 lines
  - [P03-2 startup diagnostic review round 2 and source freeze (2026-10-28)](#p03-2-startup-diagnostic-review-round-2-and-source-freeze-2026-10-28) — 18 lines
  - [P03-2 failed browser readiness and recovery settlement controls (2026-10-28)](#p03-2-failed-browser-readiness-and-recovery-settlement-controls-2026-10-28) — 46 lines
  - [P03-2 browser readiness review round 1 (2026-10-28)](#p03-2-browser-readiness-review-round-1-2026-10-28) — 15 lines
  - [P03-2 browser readiness review round 2 (2026-10-28)](#p03-2-browser-readiness-review-round-2-2026-10-28) — 10 lines
  - [P03-2 native recovery positive settlement scheduling (2026-10-28)](#p03-2-native-recovery-positive-settlement-scheduling-2026-10-28) — 32 lines
  - [P03-2 recovery scheduling review round 1 (2026-10-28)](#p03-2-recovery-scheduling-review-round-1-2026-10-28) — 15 lines
  - [P03-2 recovery scheduling review round 2 (2026-10-28)](#p03-2-recovery-scheduling-review-round-2-2026-10-28) — 16 lines
  - [P03-2 clean local managed browser proof (2026-10-28)](#p03-2-clean-local-managed-browser-proof-2026-10-28) — 21 lines
  - [P03-2 browser and recovery correction source freeze (2026-10-28)](#p03-2-browser-and-recovery-correction-source-freeze-2026-10-28) — 17 lines
  - [P03-2 zero-adopted standard census correction (2026-10-28)](#p03-2-zero-adopted-standard-census-correction-2026-10-28) — 25 lines
  - [P03-2 zero-standard census review round 1 (2026-10-28)](#p03-2-zero-standard-census-review-round-1-2026-10-28) — 11 lines
  - [P03-2 zero-standard census review round 2 and freeze (2026-10-28)](#p03-2-zero-standard-census-review-round-2-and-freeze-2026-10-28) — 17 lines
  - [P03-2 Step 6 qualification and review handoff (2026-10-28)](#p03-2-step-6-qualification-and-review-handoff-2026-10-28) — 66 lines
  - [P03-2 final documentation review and PR preparation (2026-10-28)](#p03-2-final-documentation-review-and-pr-preparation-2026-10-28) — 21 lines
  - [P03-2 PR 39 low-priority review corrections (2026-10-28)](#p03-2-pr-39-low-priority-review-corrections-2026-10-28) — 31 lines
  - [P03-2 PR 39 correction review round 1 (2026-10-28)](#p03-2-pr-39-correction-review-round-1-2026-10-28) — 17 lines
  - [P03-2 PR 39 correction review round 2 and handoff (2026-10-28)](#p03-2-pr-39-correction-review-round-2-and-handoff-2026-10-28) — 23 lines
  - [P03-2 PR 39 role-guard cancellation control (2026-10-28)](#p03-2-pr-39-role-guard-cancellation-control-2026-10-28) — 36 lines
  - [P03-2 PR 39 cancellation control review round 1 (2026-10-28)](#p03-2-pr-39-cancellation-control-review-round-1-2026-10-28) — 18 lines
  - [P03-2 PR 39 cancellation control review round 2 (2026-10-28)](#p03-2-pr-39-cancellation-control-review-round-2-2026-10-28) — 17 lines
  - [P03-2 merge and closeout (2026-10-28)](#p03-2-merge-and-closeout-2026-10-28) — 62 lines

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
failures; the canonical Release controls pass, and the full Release suite
passes 571/571 separately. An initial guarded frontend run overlaps the architecture
mutation census and fails two cold-development RSC proofs. Focused clean and
trace-mutant controls, then the entire serial guarded run pass without timeout
or protocol changes. Resource contention is plausible, not established as the
original failure's sole cause. The local test guide now uses CI's Release
configuration and serial heavy qualification.

The correction commit still requires two fresh independent review rounds and
current-source hosted checks. These local results do not relabel the earlier
`193d61cc` A/B receipts or claim a new hosted execution.

### P03-1 PR 38 correction review round 1 (2026-10-26)

Two fresh independent reviewers inspect committed `413c9bde` against `d32f3101`:
Astra/xhigh covers every backend diff and surrounding ownership/proofs;
Sol/high covers tooling, frontend and mutable documentation. Neither reviewer
edits source or executes .NET suites. Both request changes, rather than treating
the preceding local passes as exhaustive proof.

The tooling reviewer reproduces an explicitly empty process authority key being
discarded into schema-only mode. Presence now differs from absence; empty identity
keys refuse before any .NET child, while the existing empty migration-connection
sentinel can still select a complete private file. Four additional actual-recipe
controls pass in the 28-case selection. The reviewer also finds the remaining
triple-key/organization-owned-role wording in Risks and one grammatical error;
both mutable sentences are corrected without changing historical ADR text.

The backend reviewer identifies non-owned transient EF wrappers that bypass the
contextual save translation. A real 55000 provider canary gives three passes/one
failure on `413c9bde`, then four/four passes with the exact wrapper sanitized to
`IdentityStorageProviderException`. Owned mutation conflicts, cancellation,
programming failures and physical COMMIT classification retain their meanings.
The same G19 contract already covers seven production seed read methods; one
confined provider-only read boundary and real source/sink controls complete that
population. No public read endpoint or current HTTP disclosure is inferred.

A separately retained synthetic 53300 observation breaks the Npgsql connector
and makes direct rollback produce a secondary exception containing its original
provider cause. The common owner still preserves the primary failure. Its
secondary rollback diagnostic now drops the exception for declared Identity
operations or the fixed Identity storage provider descriptor, including read
queries; an ordinary-operation control retains the preceding diagnostic behavior.
This is diagnostic confinement, not new transaction authority or a changed
rollback/COMMIT outcome.

Hosted run [40311209276](https://github.com/HodeTech/LearnStack/actions/runs/40311209276)
tests head `413c9bde` through merge source `4b8dbe33`. Twenty jobs pass, including
all architecture producers, backend aggregate, frontend/native ingress, public
demo, OpenAPI and metadata gates. Six ordinary producers and their integration
aggregate fail: the independent whole-body parent-guard expectation still names
`FOR KEY SHARE` after the new forward correction. The workload reports
`tests_failed`; failed ownership/forced-cleanup receipts are not accepted as
complete qualification. The expectation now independently requires the stronger
Membership-parent lock, and a planted downgrade back to KEY SHARE must fail.
The full 96-case parent/read/sink selection passes locally after this correction;
its output remains separate from the failed hosted attempt.

Full current-source qualification and a second fresh review round follow the
correction commit. The earlier A/B receipts remain bound to their recorded source.

The round-1 follow-up passes 2,782 unit cases, 281 selected real Identity/
parent-scope/provider/diagnostic cases, 45 related architecture cases and the
complete guarded frontend population of 2,408 cases in two packages. Frontend
lint/typecheck and documentation checks pass. These are local results with zero
skips/todos, not a replacement for current-source hosted qualification.

### P03-1 PR 38 correction review round 2 (2026-10-26)

Two new independent reviewers inspect `d32f3101` → `26923143`: Astra/xhigh
reviews all changed backend production/test files and surrounding ownership;
Sol/high reviews tooling, frontend and mutable documentation. Neither executes
.NET or provider/browser qualification. The backend reviewer approves with no
concrete residual finding, explicitly limiting that verdict to source review.

The tooling reviewer finds that the shared migration-role parser accepts the
first recognized username assignment even when another alias follows. That
ambiguity now refuses without printing credentials, before any .NET child.
Four actual-recipe negative controls cover duplicate/synonymous assignments,
including equal duplicate values; a quoted password containing a role-like
segment remains valid. The focused migration/demo selection passes 227/227.
This establishes refusal before child execution, without making an unmeasured
claim about Npgsql's duplicate-alias precedence.

The same reviewer narrowly rechecks the parser, refusal-before-child path,
controls and Status edit, confirms both findings closed, and reports no residual
issue in that scope. This recheck does not claim a new full review or suite run.

The reviewer also identifies the Status row's unqualified completion wording.
It now distinguishes implemented work from pending correction qualification;
the earlier six-step closeout and its source-bound A/B receipts remain history.
Both fresh correction review rounds are complete. Current-source hosted
qualification remains required and is not inferred from reviewer approval or
the focused local checks.

The final guarded frontend run passes 2,413 cases in two packages with zero
skips/todos; canonical frontend lint/typecheck and the documentation gates pass.

### P03-1 PR 38 correction fixture census (2026-10-26)

Hosted run [40317627293](https://github.com/HodeTech/LearnStack/actions/runs/40317627293)
at `492f3f67` rejects the metadata control because the new real contextual-read
sink class is absent from the closed shared-fixture census. The census now
includes that actual class. Shard counts, assignment algorithm, weights,
resource limits and time ceilings are unchanged; no producer is omitted to
obtain a pass. All 72 planner controls pass before the next push, and the
tooling reviewer independently confirms the exact source/census match and
status/evidence wording in a narrow read-only recheck. Because this
changes the plan digest, final qualification requires both complete A/B layouts
at the same correction source; the original `193d61cc` A/B evidence is not
relabeled. The phase Status block records committed implementation and review
state without predicting the current-head checks' result.

### P03-1 PR 38 measured timing correction (2026-10-26)

Run [40318359878](https://github.com/HodeTech/LearnStack/actions/runs/40318359878)
tests head `c14ec6a7` through tree-equivalent merge source `5e02e3cc`. Complete
A passes all 27 jobs and 6,490 distinct .NET cases: 2,782 unit, 571 architecture,
3,132 integration and five contract cases. Twenty owners finish naturally;
all 25 original archives, package bytes and 27 Lighthouse JSON/HTML report pairs
are retained and independently checked with the production aggregate validators.

Full B is not qualified. Twenty-five jobs pass; `ordinary-7` reaches the existing
deadline and its integration aggregate refuses. Its 194/231 completed cases all
pass; 37 have no result. Across B, 6,453 cases pass. The failed receipt reports
forced cleanup, incomplete ownership and `dockerAbsent=false`;
`cleanupOwnershipComplete=true` does not turn this into natural cleanup.
All 24 B archives and raw metadata are retained and independently audited.
The failure is not labeled an assertion failure or a proven transient flake.

The timing-only correction uses the maximum measured duration from complete
A/B selector groups. It measures 17 previously unweighted selectors and raises
23 existing ordinary weights whose measured sum exceeds their prior value by
at least ten seconds. No prior measured weight decreases. B's partial Seeder
method (11/20 cases) and 21 methods without completed cases contribute no B
estimate; A's complete observations remain available. Every changed weight names its original
TRX or exact completed-case prefix digest, run/attempt, artifact and source.
The 72 planner and 30 aggregate controls pass. Architecture weights, all shard
counts, salt, assignment algorithm, default weight and resource/time limits
remain unchanged.

The plan digest changes from
`c9437dbd00f644fd1b13c2ff21c038abd1f359595283d5c5a6d0f4d0db0c84fb`
to `b61cda34c8c8a762cef07baaa22862da1299bd56fbbdc895577ded8e76b90719`.
The same 6,490-case manifest remains
`e113e14c162b7691f80e2e4afae75b0fca0435335920a727db7f97fba17b00e3`.
On that unchanged manifest, the planner moves 2,527 cases in A and 2,426 in B;
architecture, ordinary and shared-fixture co-membership remain genuinely distinct. The
observed maximum case-body envelope for B falls from 901 to 681 seconds in the
read-only comparison. These sums can overlap and exclude setup, discovery,
fixture, export and cleanup costs; they do not prove wall-clock fit or establish
the failure's sole cause. Two complete attempts at the revised source/plan
remain the required qualification. Historical successful receipts are not
reused as that qualification.

### P03-1 PR 38 independent mutation collections (2026-10-26)

At head `dfdf9cab`, run
[40326601181](https://github.com/HodeTech/LearnStack/actions/runs/40326601181)
passes complete A: 27 jobs and 6,490 cases. Independent original-archive
validation confirms all 25 ZIPs, 147 producer-package files, twenty natural
owners and 27 public report pairs. Full B is not qualified: `architecture-4`
reports `deadline_exhausted` after 76/77 passing cases; `ordinary-0` reports
`unit_observation_failed` near the owner budget after 350/355 passing cases.
Their dependent aggregates refuse. All 23 B original archives are retained;
the six cases without results are not described as failed assertions or skips.
The observation refusal alone does not identify its lower-level cause.

Further measured-weight alternatives do not establish fit for both layouts.
With xUnit's class collections, multiple independent long-running mutation
Facts in one class serialize. The rejected alternatives retain an observed
771-second class body path before setup/discovery/other work; changing only
method weights does not remove that class constraint.

Nineteen independent mutation methods are grouped by concern into separate
class collections. Each method already owns and disposes its own Cecil graph.
Declaration-only moves preserve exact body bytes; wrapped companions change
only uniform indentation, with exact dedented bodies and all 48 recurrence
InlineData attributes preserved. Their clean, dirty and restored predicate
passes, canonical catalogue rule names and the two-thread cap remain unchanged.
No detector, resolution-walk algorithm or cache change is included.

| Predecessor nested class | Independent nested class | Existing method concern |
|---|---|---|
| `JobExecutionMutationTests` | `JobOwnerMutationTests` | Owner admission and retained scope |
| `JobExecutionMutationTests` | `JobOrchestrationMutationTests` | External orchestration and resource carriers |
| `JobLifecycleMutationTests` | `JobCapacityMutationTests` | Capacity and checkpoint authority |
| `JobLifecycleMutationTests` | `JobRecipientMutationTests` | Recipient admission and fences |
| `JobLifecycleMutationTests` | `HangfireRuntimeMutationTests` | Runtime fatal/constructor boundaries |
| `OutboxDispatchConventionTests` | `JobRecurrenceMutationTests` | Recurrence authority and cleanup; 48 cases |
| `OutboxDispatchConventionTests` | `ResolutionMutationTests` | Resolution graph mutation controls |
| `JobAuthorityMutationTests` | `JobAuthorityCensusMutationTests` | Authority activation and live guards |
| `OutboxDispatchConventionTests` | `CleanupOwnerMutationTests` | Privileged cleanup ownership |
| `OutboxDispatchConventionTests` | `DeliveryLifecycleMutationTests` | Delivery lifecycle graph |
| `OutboxDispatchConventionTests` | `BridgeCredentialMutationTests` | Two bridge credential controls |
| `OutboxDispatchConventionTests` | `CredentialBindingMutationTests` | Two credential binding controls |
| `OutboxDispatchConventionTests` | `IntegrationInvocationMutationTests` | Integration invocation graph |
| `OutboxDispatchConventionTests` | `DeliveryCredentialMutationTests` | Delivery credential consumers |
| `OutboxDispatchConventionTests` | `JobOrchestrationDeclarationMutationTests` | Finite orchestration declarations |
| `OutboxDispatchConventionTests` | `JobMetricMutationTests` | Two metric callback/census controls |

The plan adopts 39 source-bound, complete-group timing observations from the
last actual A/B evidence and explicitly aliases the nineteen renamed selectors.
Eight aliases overlap those timing changes: 50 semantic methods are affected.
Alias source cards retain predecessor measurement identity and disclose that
the new selector name has not yet executed in hosted CI. The 95 source records
remain within the existing cap. No incomplete method contributes a duration;
the earlier large current-source reset is not adopted.

Actual local Release VSTest discovery confirms 6,490 cases, with exactly 66
old IDs replaced by 66 new IDs and 6,424 byte-equal case records unchanged.
The manifest digest is
`8d017042f7e439273a7164326d9a1ae7ef3ea4ea3dea9a1bfa4c315a133bef21`;
the plan digest is
`115e474202360d0d7d99e1a8c69a0b40f7eb84669364b23ce90c94341969113b`.
Both actual discovered assignments cover the exact union and retain distinct
architecture, ordinary and shared-fixture co-membership. Shard counts, salt,
assignment algorithm, quotas and all resource/time ceilings remain unchanged.
Release build passes with zero warnings/errors. The five declaration-only
mutation Facts pass 5/5 locally, and the two wrapped companions pass 49/49,
including all recurrence variants. The remaining twelve moved companions pass
12/12; all 66 cases pass with zero skips. Full runtime and two-layout hosted
qualification remain required; source/body equality is not that proof.

The full 303-method census, rather than one candidate assignment alone, checks
remaining class serialization. Its maximum complete-observation class envelope
is 617 seconds across all methods in each class; every assigned class plus the
historical fixed-time proxy is below the existing nominal owner window. These
are advisory scheduling diagnostics, not a future wall-time guarantee. The
actual complete A/B runs remain the acceptance witness for hosted fit.


### P03-1 PR #38 current-source A/B and ordinary window proposal (2026-10-26)

**Source and outcome.** Complete run
[40382715371](https://github.com/HodeTech/LearnStack/actions/runs/40382715371)
uses head `7aabc6d8a3e2109a6e6dfa0c439442b0dcd008d9`, actual merge checkout
`2c692b51adc5feb7fb0c843d4124938a61775a7b` and common tree
`217d237966b20215ed25a15d5440c4d05a18717c`. Its frozen plan and manifest are
those recorded in the preceding mutation-collection entry. Attempt 1 selects
literal A; attempt 2 selects literal B. The attempts are retained separately.

A succeeds in all 27 jobs, with 6,490 .NET passes, zero skips and exact case
unions. All 25 original archives are retained. Independent offline audit by
`pr38_layout_r2_plan` verifies API source/attempt identity, archive size/digest
and CRC, 147 original producer files, production aggregate validation, all twenty
natural execution owners and 27 public JSON/HTML report pairs. This audit is
source-bound A evidence, not a second assignment or a new runtime execution.

B succeeds in 24 jobs; all Architecture producers and the backend aggregate
pass. Two ordinary producers report `deadline_exhausted`:

| Producer / actual job | Selected | Completed Passed | No result |
|---|---:|---:|---:|
| [ordinary-3](https://github.com/HodeTech/LearnStack/actions/runs/40382715371/job/121796927865) | 262 | 259 | 3 |
| [ordinary-7](https://github.com/HodeTech/LearnStack/actions/runs/40382715371/job/121796928433) | 267 | 264 | 3 |

The dependent integration aggregate refuses. Whole B contains 6,484 completed
passing cases and six without results. Both failed producers report forced
cleanup and no proved Docker absence; ownership of forced cleanup is not natural
completion. No assertion failure is observed in completed cases; the censored
cases establish neither a pass nor an assertion failure. All 24 available B
original ZIPs, API metadata and extracted proof are retained under
`artifacts/p031-step6/hosted/40382715371/2/`; A is retained under sibling `1/`.
Raw receipts remain private verification artifacts, not committed source.
No complete current-source A/B qualification is claimed.

**Decision boundary.** [ADR-0075](../decisions/0075-ordinary-ci-job-window.md)
is Proposed. It recommends only an ordinary Docker whole-job window of
25 minutes, preserving the other ADR-0063 limits. Its Context owns the budget
arithmetic, censored-tail limitation and alternatives. Source-bound complete
A and failed B expose a remaining wall-time limit; more weight tuning is not a
substitute for final-source qualification. No policy or runtime implementation
is authorized by this proposal. Maintainer approval is required because the
maintainer requested review before implementing each new ADR.

**Acceptance text prepared for ADR-0063 — not appended there while Proposed:**

> Amendment 1 — Ordinary Docker whole-job window (acceptance date)
>
> The maintainer accepts ADR-0075's bounded supersession of the ordinary
> Docker portion of the Decision sentence, “Each execution keeps its
> twenty-minute whole-job ceiling”, and the execution rule, “Keep each
> twenty-minute ceiling and a finite cleanup/upload reserve.” Ordinary Docker
> producers instead have a twenty-five-minute whole-job ceiling. The
> “No … ceiling increase … is authorized” plan clause remains the prohibition
> on unapproved expansion; this explicitly approved ordinary exception is its
> only change.
>
> All other ADR-0063 decisions remain in force: the existing producer count
> ranges and twenty-VM cap, complete selector ownership and two-layout
> qualification, algorithm/salt/weights/filters, resource ownership/accounting,
> cleanup and upload reserves, all other job ceilings and the seven required names.
> Expected verified producer identity controls the longer interval; a receipt
> cannot choose it. Acceptance authorizes dependent work, not actual hosted
> fit. ADR-0075 and the Phase 03 delivery log own the new decision and its
> implementation/qualification receipts. Historical text and metadata above
> remain intact.

The amendment number is checked again at acceptance; ADR-0063 currently has no
Amendments section. The acceptance date records actual approval rather than
backdating this proposal. This is a policy change, not an ADR-0041 correction.

**Three-step implementation plan, pending approval:**

1. Accept ADR-0075 and append ADR-0063's dated amendment; reconcile Testing 06
   and Infrastructure Stack 20's corresponding ordinary-ceiling statement,
   the existing `CI_Test_Shards_Are_Complete_And_Bounded` catalogue Source/Asserts,
   the decision index and Phase 03 Status block. Keep the catalogue's prior
   implementation distinct from the not-yet-delivered extension.
2. Apply the closed expected-owner budget policy in the ordinary workflow,
   launcher, fixture preparation and receipt/API/workflow verifiers named in
   ADR-0075. Add positive and counterfactual controls for exact thresholds,
   unknown modes, forged partitions, invalid indices and mismatched job identity.
   Run the relevant existing Python suites and documentation guards; no test
   population, resource policy, assignment or reserve change is included.
3. Qualify two complete fresh same-final-source A/B attempts. Require every
   job, exact full discovered unions, zero skips, complete original packages and
   natural lifecycle evidence, followed by independent cross-attempt audit.
   Retain each attempt before rerunning all jobs. Historical A cannot qualify
   the changed policy source. Update closeout and PR evidence only from actual
   completed results.

Every step is committed and receives two fresh independent review rounds;
verified findings are fixed, committed and revalidated before advancing. The
proposal itself receives two fresh document-review rounds before the maintainer
question. No new catalogue rule or Implemented extension is claimed by authoring
this decision package. PR #38 remains open for review and is not merge-qualified.


### ADR-0075 proposal review round 1 (2026-10-26)

Two fresh independent read-only reviewers inspect proposal commit `2d2a6f94`:
`pr38_adr75_r1_source` (GPT-6 Astra, xhigh) checks actual budget authorities and
preserved source contracts; `pr38_adr75_r1_evidence` (GPT-6.1 Sol, high) checks
retained original artifacts, source/assignment identity, arithmetic and decision
instruments. Neither executes .NET, starts services or changes the worktree.

The source review finds two valid documentation issues: Infrastructure Stack 20
also carries the twenty-minute statement and must be reconciled at acceptance;
the draft's claimed 3 GB/four-CPU child quotas are absent from the launcher,
which owns processes and accounts CPU/memory without those numeric limits.
Both are corrected: the acceptance list names Standard 20; ADR and package
preserve resource ownership/accounting without adding or claiming quotas. The
reviewer narrowly rechecks and closes both. Its remaining index wording is also
changed from quotas to resource policy. Phase Status explicitly names measured
source `7aabc6d8`, and the count alternative states that it keeps six Architecture
producers. No runtime policy changes result.

The evidence reviewer independently verifies all 49 original ZIP sizes, SHA-256
digests and CRCs, producer-file equality, both selected unions and the exact
6,490/6,484 completed outcomes. It recomputes source/tree/plan/manifest identity,
twenty natural A owners and the 997-to-1,297-second arithmetic, with no finding.
The historical 617-second advisory class envelope is outside that reviewer's
reconstruction; it supplies no qualification claim. Root separately rechecks
all archive digests/sizes/CRCs and unique case totals. These are offline evidence
checks, not another hosted run. Fresh second-round review remains required.


### ADR-0075 proposal review round 2 (2026-10-26)

Two different fresh independent read-only reviewers inspect corrected proposal
commit `e50e6d13`: `pr38_adr75_r2_source` (GPT-6.1 Sol, high) traces all
launcher/leaf, API/receipt, workflow and image-preparation budget authorities;
`pr38_adr75_r2_evidence` (GPT-6 Astra, high) independently recomputes the retained
archive/case/source evidence and checks acceptance governance. Both approve with
no remaining concrete finding. Neither edits files, starts services or executes
.NET tests.

The source reviewer confirms closed expected-owner authority, retained reserves,
other ceilings, counts, runner tier and test-specific deadlines, plus the
Standard 20 and resource-accounting corrections. Only the four proposal/carrier
documents differ from measured code source `7aabc6d8`; no policy implementation
precedes approval. The evidence reviewer independently checks all 49 original
ZIP hashes/sizes/CRCs and extracted-file equality, exact A/B selected and
completed unions, twenty natural A owners, identities and budget arithmetic.
It does not reconstruct the advisory class-envelope analysis or rerun aggregate
validation; those limitations do not become new qualification claims.

Whole-tree Markdown fragments, roadmap structure, current delivery indexes,
ADR lifecycle parsing and whitespace checks pass. ADR-0075 remains Proposed;
maintainer approval is the next policy gate. After approval, the three-step plan
above still requires actual implementation reviews and fresh complete hosted
A/B. These document reviews do not establish twenty-five-minute fit.


### ADR-0075 maintainer review and revised package (2026-10-27)

**Authority.** The maintainer supplies two review reports against proposal head
`b048412d`; the response requests pre-acceptance clarification rather than
accepting the decision. ADR-0075 remains Proposed. This entry replaces only the
acceptance carrier list and interval clarification in the
[original three-step proposal](#p03-1-pr-38-current-source-ab-and-ordinary-window-proposal-2026-10-26).
Its historical draft and review receipts stay intact. No Accepted ADR, workflow
or executable budget policy changes before approval.

**Verified disposition against current source:**

| Finding | Disposition |
|---|---|
| ADR-0062 Amendment 1 still says all execution jobs keep twenty minutes | Valid. Acceptance now owes its own dated ordinary-only amendment in ADR-0062 as well as ADR-0063. Native remains twenty minutes. Both append-ready texts follow. |
| Five minutes could be read as a whole-workflow feedback bound | Valid. ADR-0075 now limits the statement to additional running time per ordinary job; queueing/concurrency determine total feedback delay. |
| B1: twenty-five minutes is a judgement choice | Already disclosed; strengthened to name maintainer acceptance of headroom, with no proved minimum or safe margin. Fresh final-source A/B remains mandatory. |
| B2: A passes, so “failure of both assignments” is not observed | Valid clarification. Reopening follows ADR-0063's separate envelope-exhaustion clause and requirement to qualify both layouts; the proposal claims neither that both fail nor that a change is pre-authorized. |
| B3: continuity with ADR-0063's rejected ceiling option | Valid clarification. The ordinary-only part of its previously valid, unselected alternative is now explicitly named. |
| M1: globally changing `finite_seconds` to 1,500 would weaken other producers | Valid. Observational bounds are supplied only from independently verified expected-owner identity, across receipt and measurement validation. Nonordinary remains 1,200. |
| M1: retain every ordinary measurement at 1,200 | Not adopted. `shard_workload` records its whole worker interval and assembly invocation walls; these are observations under the enclosing job deadline, not separate operation limits. A 1,250-second workload with short pre-owner work can fit 1,500 minus the unchanged 203-second reserve. A separate measurement cap would add a workload policy. Actual test/fixture operation deadlines remain unchanged. |
| M2: fixture preparation shares the launcher's duration source | Valid; explicit single-policy ownership is added. Its total 110 seconds, per-image 55 seconds and two-minute step remain fixed. |
| M3: `verify_jobs` must use expected owner in either aggregate scope | Valid; explicitly stated, including base/native inside integration. A prefix or receipt partition cannot grant the longer window. |
| Owner setup arithmetic provenance | Valid clarification. The ADR names the observed `result.json.setupSeconds` values 0.191/0.198 rather than an unexplained approximate constant. |
| Option 5 and the no-unapproved-increase clause | Clarified together with B1/B2. Neither twenty-two/twenty-three nor twenty-five has measured fit yet; accepting this bounded exception leaves all other expansion unapproved. |

The M1 distinction is checked against `ci-native-tests.py`'s actual
`shard_workload`, `leaf` and `deadlines`, and `ci-test-shard-results.py`'s
`verify_receipt`/`finite_seconds`. Nested measurement observations are not summed
as extra disjoint time. Only the four disjoint receipt intervals sum below the
verified owner's ceiling. No runtime changes or new deadline semantics are
implemented by documenting this distinction.

**Revised acceptance text for ADR-0062 — not appended while Proposed:**

> Amendment 2 — Ordinary Docker execution window (acceptance date)
>
> Accepted ADR-0075 supplies an ordinary-only exception to Amendment 1's
> statement, “All execution jobs keep twenty-minute ceilings”. Verified
> ordinary Docker producers have a twenty-five-minute whole-job ceiling;
> native, base and Architecture execution retain twenty minutes. ADR-0063
> receives its own dated amendment for the same bounded change.
>
> Native collector authority, excluded-host rules, process/Docker ownership,
> unchanged cleanup/export/upload reserve, complete result requirements and
> the other decisions above remain intact. Expected verified producer identity
> controls the longer observation/whole-job window; no receipt chooses it.
> Acceptance is not implementation or actual hosted fit. ADR-0075 and the
> Phase 03 delivery log own the dependent implementation and fresh two-layout
> qualification receipts. The historical text, metadata and Amendment 1 are
> retained without rewriting. This is a bounded decision change, not an
> ADR-0041 factual correction.

**Revised acceptance text for ADR-0063 — not appended while Proposed:**

> Amendment 1 — Ordinary Docker whole-job window (acceptance date)
>
> Accepted ADR-0075 supplies an ordinary-only exception to the Decision's
> “Each execution keeps its twenty-minute whole-job ceiling” and the rule,
> “Keep each twenty-minute ceiling and a finite cleanup/upload reserve.”
> Verified ordinary Docker producers instead have a twenty-five-minute
> whole-job ceiling. This also qualifies the inherited ordinary requirement
> in the Exact relationship to ADR-0062 section; ADR-0062 receives its own
> dated amendment. Native, base and Architecture retain twenty minutes.
> The plan's prohibition on unapproved ceiling increases keeps only this
> explicitly approved ordinary exception.
>
> All other decisions remain in force: producer count ranges and the
> twenty-VM cap, selector ownership and two-layout qualification,
> algorithm/salt/weights/filters, resource ownership/accounting, the
> unchanged cleanup/export/upload reserve, other job ceilings and the
> seven required names. Expected verified producer identity controls the
> longer enclosing interval; a receipt cannot choose it. Actual operation
> deadlines remain unchanged. Acceptance authorizes dependent work, not
> actual hosted fit; the Phase 03 delivery log owns its implementation and
> qualification receipts. Historical text and metadata remain intact.

Both amendment numbers are checked again at acceptance. Actual acceptance dates
replace the placeholders; they are not backdated to proposal or review dates.

**Revised three-step plan, pending maintainer acceptance:**

1. Accept ADR-0075, append the two own-record amendments above and reconcile
   Testing 06, Infrastructure Stack 20, the existing CI catalogue Source/Asserts,
   decision index and Phase 03 Status. The existing catalogue implementation
   does not imply the not-yet-delivered extension.
2. Implement the one closed expected-owner budget policy across launcher/leaf,
   fixture preparation, workflow and API/receipt/observation validation. Include
   positive ordinary observations above 1,200 within the longer enclosing job,
   negative nonordinary/forged-owner controls, unchanged operation caps and
   finite/threshold checks. The population, plan, reserve and other boundaries
   stay fixed; run the relevant Python suites and documentation guards.
3. Qualify complete fresh same-final-source A and B, retain original packages
   before rerunning all jobs, independently cross-audit exact full unions and
   natural completion, then update actual closeout and PR evidence. Historical
   A or failed B cannot stand in for these new proofs.

Each step retains commits and two fresh independent review rounds before
advancing. The revised proposal also receives two fresh review rounds before
being returned for approval. Acceptance and executable implementation remain
pending; this package is not new hosted qualification.


### ADR-0075 revised proposal review round 1 (2026-10-27)

Two fresh independent read-only reviewers inspect revision `d5ebab86`:
`pr38_adr75_maintainer_r1_intervals` (GPT-6.1 Sol, high) traces enclosing
measurement/deadline semantics and expected-owner budget authority;
`pr38_adr75_maintainer_r1_docs` (GPT-6 Astra, high) checks the amendment texts,
predecessor decisions, retained boundaries, Proposed status and plan consistency.
Both approve with no remaining actionable finding. Neither changes files,
starts services, executes .NET or reruns hosted jobs or archive validation.

Both verify the partial M1 disposition against actual instrumentation: worker
and assembly observations can legitimately exceed 1,200 within the proposed
ordinary enclosing budget; a separate cap would add a workload restriction.
Other producers and actual operation deadlines stay unchanged. Both own-record
amendments, native twenty minutes, per-job feedback cost and the explicit
A-passes/B-fails reopening rationale are confirmed. This establishes reviewed
proposal semantics, not maintainer acceptance, implementation or hosted fit.
A fresh second review round remains required before returning the package.


### ADR-0075 revised proposal review round 2 (2026-10-27)

Two different fresh independent read-only reviewers inspect revision `9ba876f3`:
`pr38_adr75_maintainer_r2_source` (GPT-6 Astra, high) traces enclosing execution,
operation caps, observations and counterfactual obligations;
`pr38_adr75_maintainer_r2_docs` (GPT-6.1 Sol, high) checks own-record amendments,
predecessor/standard consistency, status and historical preservation. Both
approve with no remaining actionable finding.

The source review confirms that a 1,250-second worker/assembly observation can
fit the proposed window when pre-owner/setup work is short, supporting the
recorded partial M1 disposition. Both verify that expected identity, not a
receipt or prefix, selects observational and whole-job bounds; nonordinary,
actual operation caps and the 203-second reserve stay fixed. The corpus review
confirms historical delivery text is preserved and both Accepted predecessor
files, Standards 06/20/21, executable carriers, workflow and plan remain
byte-identical to `b048412d`. Only four proposal/carrier documents differ.

Whole-tree document/roadmap/index guards, ADR Proposed parsing and whitespace
checks pass. Neither reviewer changes files, starts services, executes .NET,
reruns hosted jobs or repeats archive qualification. This closes the revised
proposal reviews; maintainer acceptance, implementation reviews and fresh full
A/B remain required. No twenty-five-minute hosted fit is claimed.


### ADR-0075 acceptance and Step 1 (2026-10-27)

**Maintainer acceptance.** The maintainer explicitly approves the revised
ADR-0075 and three-step plan at `c274fa6e` with “Revize ADR ve planı
onaylıyorum; uygula”. Acceptance covers the revised interval distinction:
ordinary enclosing observations follow independently verified 1,500-second job
identity; nonordinary remains 1,200, actual operation deadlines and the
203-second reserve stay unchanged. It accepts per-job extra running time and
no overall workflow-delay or hosted-fit guarantee.

Step 1 records ADR-0075 as Accepted, appends own-record dated amendments to
ADR-0062 and ADR-0063, moves the index row to Active and reconciles Testing 06,
Infrastructure Stack 20, the existing CI catalogue Source/Asserts and Phase 03
Status. Existing catalogue enforcement stays Implemented; its ADR-0075 extension
is explicitly pending implementation. Historical Accepted bodies/metadata and
prior delivery records remain intact. No workflow, plan or executable budget
changes occur in this step.

The [revised package](#adr-0075-maintainer-review-and-revised-package-2026-10-27)
is now the accepted three-step implementation authority. Each step receives a
commit and two fresh independent review rounds, with verified fixes committed
and revalidated before advancing. Step 2 implements and locally proves the
closed expected-owner policy; Step 3 requires fresh complete same-final-source
A/B and independent original-evidence cross-audit. Earlier A and failed B remain
historical evidence and do not qualify the changed source. Acceptance is neither
implementation nor actual twenty-five-minute hosted fit.


### ADR-0075 Step 1 review round 1 (2026-10-27)

Fresh independent reviewers `adr75_step1_r1_records` (GPT-6.1 Sol, high) and
`adr75_step1_r1_contract` (GPT-6 Astra, high) inspect `c274fa6e..2a1917cf`.
Both approve with no actionable finding. The records reviewer reconstructs the
predecessor bytes by removing only new amendment blocks and compares both
amendments to the accepted package. Historical delivery text is preserved
outside the generated index. Both confirm the nine-file documentation-only
scope, accepted index/status, pending catalogue extension and separation of
acceptance from implementation/hosted fit. Document, lifecycle and whitespace
checks pass. Neither reviewer mutates files, starts services, executes .NET or
revalidates original hosted evidence. Fresh second-round review remains.


### ADR-0075 Step 1 review round 2 and closeout (2026-10-27)

Fresh reviewers `adr75_step1_r2_records` (GPT-6 Astra, high) and
`adr75_step1_r2_contract` (GPT-6.1 Sol, high) independently inspect
`c274fa6e..7dc3f8b7`; both approve with no actionable finding. Both reconstruct
predecessor bytes, check the accepted amendment texts and verify every carrier
keeps implementation and new hosted fit pending. Historical delivery text is
preserved outside its generated index. Lifecycle, fragments, roadmap/index and
whitespace checks pass. Neither executes runtime tests, starts services or
requalifies archives. Step 1 is complete; Step 2 may now implement the approved
closed policy. This closeout supplies no executable or hosted-fit evidence.


### ADR-0075 Step 2 closed budget implementation (2026-10-27)

One `job_seconds(mode, job, counts)` policy in `ci-native-tests.py` owns the
1,500-second ordinary and 1,200-second other execution windows. It refuses
unknown/prefix/alias modes, wrong job identity and out-of-plan indices. Both
owner and leaf deadlines charge the same pre-checkout clock and retain every
component of the 203-second reserve. No environment budget override is added.

The result owner uses this same policy for expected-owner API running walls,
workflow topology, four disjoint receipt intervals and enclosing measurement
observations. Receipt identity is bound before selecting its limit. The finite
observation helper requires an explicit verified ceiling; no global widening
or self-declared partition selects ordinary authority. Nonordinary remains
1,200 even inside integration; nested measurement observations are not summed
as additional disjoint work.

Only the ordinary job ceiling changes to twenty-five minutes. Fixture
preparation now receives the exact matrix mode and the same source/workflow
metadata as execution, validates the real existing identity path before image
I/O, and uses the common budget. Its 110-second total, 55-second per-image and
two-minute step caps stay unchanged. Topology requires the two metadata maps to
agree; real runtime identity still independently rejects corrupted metadata.
The CLI accepts exactly one mode argument for preparation and retains exact
arity elsewhere. Counts, plan, salt, algorithm, population, other job ceilings,
resource policy, protection names and application code remain unchanged.

**Local validation.** The combined plan/result suite passes 81 controls
(including 38 aggregate controls) in approximately 5.2 seconds under a local
27-second guard, within meta's existing 30-second allocation. The complete real
launcher offline suite passes 89 controls in approximately 22.2 seconds. After
self-review makes the long-observation positive fixture's nested intervals
coherent, the complete combined suite passes again. Counterexamples cover
expected identity, exact thresholds/strict sums, nonfinite/boolean/negative
fields, longer ordinary worker/assembly observations, nonordinary refusals,
preparation context/pull caps and workflow/CLI drift. Topology, Python compile,
document guards and whitespace checks pass. Private logs are under
`artifacts/pr38-review/adr75-step2/`.

These are offline source/policy controls using disposable seams, not systemd,
Docker lifecycle or hosted-fit evidence. No .NET/application test source or
runtime is changed; fresh full hosted A/B remains Step 3's required witness.
Step 2's two independent review rounds remain before that qualification starts.


### ADR-0075 Step 2 review round 1 (2026-10-27)

Fresh independent reviewers `adr75_step2_r1_source` (GPT-6 Astra, xhigh) and
`adr75_step2_r1_tests` (GPT-6.1 Sol, high) inspect `1d609d30..c2695f2d`.
Both approve with no actionable finding. The source reviewer traces exact
mode/job/index authority, full preparation source/runner identity before Docker,
owner/leaf clock/reserve, expected-owner observation bounds and one-hop
artifact/CLI/failure paths. The tests reviewer confirms preserved controls,
coherent long ordinary positives and meaningful forged-identity/threshold/
nonfinite counterexamples; it independently reruns all 81 combined controls in
5.010 seconds and all 89 native-owner controls in 23.371 seconds, both passing.
The plan, application/population source and nonordinary limits stay unchanged.
These are read-only source/offline reviews; no services, .NET or hosted
qualification occur. A fresh second round remains before Step 3.


### ADR-0075 Step 2 review round 2 and qualification handoff (2026-10-27)

Fresh independent reviewers `adr75_step2_r2_source` (GPT-6.1 Sol, high) and
`adr75_step2_r2_proof` (GPT-6 Astra, high) inspect `1d609d30..6bb91ede`.
Both approve with no actionable finding. The source reviewer follows identity,
source checks, owner/leaf environment and deadlines, cleanup, CLI and artifact
paths. The proof reviewer verifies meaningful complete-evidence counterfactuals
and preservation of every existing method: launcher 87 to 89, plan 42 to 43,
results 30 to 38. Only ordinary workflow time grows; preparation and operation
caps, reserve, counts, plan, population source and meta allocation stay fixed.
Neither mutates files, starts services or reruns .NET/hosted execution; no
redundant suite rerun is needed after unchanged passing local/R1 controls.
Document/whitespace checks pass. Step 2 is complete.

**Step 3 handoff.** Freeze this reviewed source and push it for a complete A
attempt. Preserve the original current-attempt API metadata and all producer,
aggregate and auxiliary packages before a full rerun for literal B. Both must
use the same head/merge source, plan, toolchain and complete 6,490-case population,
with every job successful, exact disjoint unions and twenty natural owners.
Only then can independent two-round evidence review close Step 3. No current
hosted-fit claim follows from this handoff; all earlier source receipts remain
historical.


### ADR-0075 Step 3 fresh hosted A/B evidence (2026-10-27)

**Qualified implementation source.**
[Run 40412041726](https://github.com/HodeTech/LearnStack/actions/runs/40412041726)
executes head `ea114d25266d7db7729d276c07d0011a7ba180fd` and actual merge checkout
`c633763c0a8fb086c1f8fd09231075e8149da1c4`. Their common tree is
`53daaf89c9527ccf370f108ef8dfb0f7bca8edfe`. These hashes identify different
commits with equal trees. The same source, plan, manifest, toolchain and image
identities qualify both attempts; no code, weight or assignment change occurs
between them. A is attempt 1; B is a full rerun, attempt 2.

| Actual evidence | A | B |
|---|---:|---:|
| Successful jobs | 27 / 27 | 27 / 27 |
| Passing .NET cases; zero skips | 6,490 | 6,490 |
| Exact backend / Docker union | 3,664 / 2,826 | 3,664 / 2,826 |
| Ordinary / native cases | 2,795 / 31 | 2,795 / 31 |
| Original immutable archives retained | 25 | 25 |
| Natural complete execution owners | 20 | 20 |
| Public JSON/HTML report pairs | 27 | 27 |
| Maximum execution-producer API wall | 938 seconds (ordinary-2) | 1,058 seconds (ordinary-11) |

A's original API metadata and packages are retained and production-validated
before requesting B with Re-run all jobs. GitHub's latest artifact listing after
B contains only B's packages; the retained original A snapshots and ZIPs are
therefore necessary, not reconstructed from B or job logs. Private originals,
producer files and verification receipts live under
`artifacts/p031-step6/hosted/40412041726/{1,2}/`. Short-lived download URL files
are removed after acquisition. No runner environment or execution is simulated.

The production validators independently recompute each attempt's exact selected
and completed/TRX unions from actual immutable packages and API jobs. Root also
checks all fifty ZIP sizes, SHA-256 digests and CRCs, producer-file equality,
source/attempt/toolchain/images, natural completion and all public report
JSON/HTML digest pairs. The six IDs missing from the preceding twenty-minute B
all complete Passed in the new B; historical failed evidence remains unchanged.

Actual A/B co-membership differs in both directions: Architecture has 6,203
A-only and 6,915 B-only method pairs; ordinary has 62,463 / 65,656; SharedSchema
methods within its actual 85-class collection have 40,173 / 42,648. The wider
131-class fixture-bearing plan census has 62,165 / 65,388. These are materially
different complete assignments, not bin renumbering. The plan remains
`115e474202360d0d7d99e1a8c69a0b40f7eb84669364b23ce90c94341969113b` and manifest
`8d017042f7e439273a7164326d9a1ae7ef3ea4ea3dea9a1bfa4c315a133bef21`.

This establishes observed fit for the exact implementation source and two
assignments, not a measured minimum, a future fit guarantee or a five-minute
whole-workflow delay bound. Every other ceiling, reserve, operation cap, VM
count and natural-cleanup requirement remains unchanged. The documentation
closeout has its own later commit identity and must pass normal current-head
protected checks; these execution receipts are never relabelled as that commit.
Step 3's two fresh independent evidence-review rounds remain before closeout.


### ADR-0075 Step 3 review round 1 (2026-10-27)

Fresh independent reviewers `adr75_step3_r1_archives` (GPT-6.1 Sol, high) and
`adr75_step3_r1_lifecycle` (GPT-6 Astra, high) inspect evidence record `0db4b42f`
and original run 40412041726 attempts 1/2 for implementation source `ea114d25`.
Both approve with no actionable finding. They do not trust prepared summary
JSON as authority: original API metadata, ZIPs, producer files and public report
contents supply their checks.

The archive reviewer freshly extracts all forty producer archives through the
production safe unpacker, verifies 147 byte-identical producer files per
attempt, recomputes both production aggregate scopes and checks API job/artifact
bindings. Both reviewers verify all fifty archive sizes/SHA-256/CRCs, exact
6,490-case selected/completed/TRX unions, twenty natural owners per attempt,
all thirteen Docker-owner absence flags, native confinement/core/crash evidence,
source/merge parents/tree, toolchain/images, plan/manifest and actual different
co-membership. Both confirm the six previously missing exact IDs now pass.

The lifecycle reviewer additionally checks all 54 public JSON/HTML pairs across
A/B against digest, URL/run/source/build identity, full configured inventory,
category weights, accessibility/JS/CLS gates and successful cleanup. Expected
producer budgets hold, maxima are 938/1,058 seconds, and meta/aggregates remain
below 300 seconds. Original acquisition ordering is corroborated by filesystem
birth times (last A archive 22:49:25 UTC; verification 22:49:28; B starts
22:50:09) and the contemporaneous tool-call sequence; those local timestamps are
not independently signed chronology. Neither reviewer reruns hosted work,
starts services or changes repository files. This qualifies observed evidence,
not a minimum ceiling or future fit. Fresh second-round review remains.


### ADR-0075 Step 3 review round 2 correction (2026-10-27)

Fresh independent reviewers `adr75_step3_r2_union` (GPT-6 Astra, high) and
`adr75_step3_r2_public` (GPT-6.1 Sol, high) inspect record `146f743b` and the raw
A/B packages for qualified source `ea114d25`. Both verify the hosted
qualification; the union reviewer finds one minor documentation error before
this closeout is shipped. The earlier draft labels the plan's wider 131-class
fixture-bearing pair count as SharedSchema. Root independently reproduces both
populations: fixture-bearing pairs are 62,165 / 65,388, while the actual
85-class SharedSchema collection has 40,173 / 42,648. The unshipped Step 3 record
is corrected to report both; prior reviewed revisions remain in git history.
No source, plan, package, execution or qualification result changes. A narrow
post-fix check remains before final closeout.

Both reviewers independently check all fifty original ZIP sizes/SHA-256/CRCs,
fresh producer files and API/attempt/source bindings; exact complete A/B
selected/completed/TRX unions and natural lifecycle evidence pass. The public
reviewer additionally verifies all 54 JSON/HTML pairs, embedded LHR identity,
complete configured audit inventories/category weights, accessibility score 1,
JS payload below 200,000 bytes, CLS 0, TLS/timeout refusals and cleanup. It
recomputes meta/backend/integration aggregate walls: A 113/22/29 seconds;
B 88/18/38, all below 300. The six historical missing IDs pass in both new
attempts. Proof limits remain explicit: actual source qualification is not
later documentation-head qualification, signed acquisition chronology or a
future fit guarantee. No reviewer edits the repository, executes .NET, starts
services or reruns hosted work.


### ADR-0075 three-step closeout (2026-10-27)

All three approved steps and both independent review rounds per step are
complete. Step 1 records maintainer acceptance, each predecessor's own dated
amendment and current carriers. Step 2 supplies one verified expected-owner
policy for ordinary-only twenty-five-minute whole-job/enclosing observations,
with 81 combined plan/result and 89 launcher controls. Other ceilings, the
203-second reserve, operation caps, plan, counts, population and resource policy
stay unchanged.

Step 3 qualifies implementation source `ea114d25` / actual merge `c633763c`
with complete run 40412041726 A/B: each 27 successful jobs, 6,490 Passed cases,
zero skips, twenty natural owners and 25 original archives. All fifty originals
and exact API/result/public/lifecycle evidence are retained and independently
recomputed in two fresh review rounds. The historical six missing IDs pass in
both attempts. Observed producer maxima are 938/1,058 seconds; these establish
neither a minimum ceiling nor future fit.

R2's one minor evidence-label finding is fixed in `468e9c65`. The original
reporter `adr75_step3_r2_union` and the public/lifecycle reviewer narrowly
recheck and approve the corrected 85-class SharedSchema versus 131-class
fixture-bearing populations; no actionable finding remains. No code, plan,
package or execution changes after qualification. Whole-tree document/index/
roadmap/lifecycle and whitespace checks pass.

This later documentation closeout has a distinct commit identity and is not
relabelled as the executed source. Its normal current-head protected checks
remain mandatory before merge; actual results belong to that check run rather
than a prediction here. PR #38 remains open for maintainer review and merge.
No merge is performed. P03-2 is not started; P03-3 still owns new-user HTTP/BFF
activation. This CI correction changes no application authority or deployment.


### P03-1 merge and closeout (2026-10-27)

Read-only GitHub verification confirms
[PR #38](https://github.com/HodeTech/LearnStack/pull/38) merged at
`2026-10-27T05:43:45Z` (08:43:45 Europe/Istanbul), squash commit
`012e67cc9294cbeea7379bb7d9664ef4af640d8e`. The final reviewed PR head is
`58e5a1d939ddc9f6641eb2dffeef46a5707f3043`. Fetching the merge confirms both
commits have tree `bb74b3b7eae2e2c5e8e100e88c0f82b14de13b6b`; their file
contents are identical. Development incorporates main's squash history with a
same-tree merge, without switching branches or rewriting published history.

The [ADR-0075 closeout](#adr-0075-three-step-closeout-2026-10-27) owns the fresh
implementation-source A/B qualification for `ea114d25` in
[run 40412041726](https://github.com/HodeTech/LearnStack/actions/runs/40412041726).
Each attempt completes 27 successful jobs and 6,490 Passed .NET cases with zero
skips, twenty natural execution owners and 25 retained original archives. The
final documentation head separately passes its normal current-head checks in
[run 40420048044](https://github.com/HodeTech/LearnStack/actions/runs/40420048044):
27 successful jobs, all seven required contexts and 6,490 Passed .NET cases.
The retained packages and verification keep these source identities separate.
This closeout rechecks merge/run metadata and tree equality; it does not rerun
tests or relabel pre-merge qualification as a post-merge execution. At this
verification, main's separate
[push run 40456966945](https://github.com/HodeTech/LearnStack/actions/runs/40456966945)
is queued; no result for that run is asserted here.

All six implementation steps, both independent review rounds per step, the
verified PR correction bundles and ADR-0075's three-step correction are closed.
The shipped [Identity foundation](../modules/identity/README.md) supplies global
User/subject binding, atomic creation with central MUST audit, fixed seed
adoption, contextual Membership/Profile/Role/grant storage and database-backed
lifecycle/last-admin guards. Existing-session API admission checks the durable
binding after subject quota admission. Personal-data classification and actual
diagnostic/snapshot controls cover the delivered models. Accepted ADR-0072/0073
and the ADR-0074 timing delta govern these boundaries; the linked step records
own their real PostgreSQL, Keycloak, HTTP and concurrency proofs.

ADR-0075 changes only ordinary CI execution windows to twenty-five minutes;
other execution windows stay at twenty minutes, with the 203-second reserve and
test-operation caps unchanged. Its exact-source A/B evidence establishes
observed fit, not a future timing guarantee. Historical failed runs and their
limits remain recorded.

P03-1 is complete; Phase 03 remains in progress. P03-2 is next: live membership
and permission readers, authorization/default-deny enforcement, scoped reads
and trusted audit enrichment. Its decision pass must close G2 execution, G5
enforcement, G6 policy ordering and G7 denial/sensitive-read evidence before the
first affected writer or protected activation. The
[phase plan and decision register](phase-03-identity-admin.md#packets-and-decision-gates)
own that remaining scope. This closeout starts no P03-2 implementation.

New-user HTTP bootstrap, explicit no-session/multiple-membership BFF entry and
management UI remain P03-3. Production first-admin/issuer-rebind authority
remains P03-9; DSAR/global closure remains P03-10. Invitations, custom fields,
provider ingress, back-channel logout, federation and phase-exit enforcement
retain their named packets. No phase-wide criterion is marked complete by this
foundation merge alone.

## P03-2 — Live membership and authorization

### P03-2 proposed decision package (2026-10-27)

**Status: Proposed; runtime not started.** The maintainer requests P03-2 on
`development`, complete document review before implementation, upfront decisions,
and two fresh independent review rounds after each committed implementation step.
New ADRs require maintainer approval before acceptance/implementation. This entry
neither closes a gate nor starts P03-3. Baseline: `daf03a48`, following PR #38's
verified merge. Preexisting untracked `.claude/worktrees/` remains untouched.

**Problem.** P03-1 supplies real bindings, Membership/Role storage and finite
seed authority. Ordinary API membership still denies everyone; application
authorization passes through, and neither the runtime classification gate nor
the authorized SQL/EF scope carrier exists. P03-2 makes those contracts executable
and supplies their trusted denial/disclosure evidence without activating new-user
entry, editing UI or Platform authority.

#### Document and source review

The initial review separates current facts from activation choices. Three
independent read-only reviewers examine candidate/scope ownership, permission/
policy execution and audit/evidence; the parent checks their findings against
the current source. This is a preflight, not implementation qualification.

| Surface reviewed | Verified baseline / disposition |
|---|---|
| CLAUDE, README, vision, MVP, roadmap/Decision Timing, principles and glossary | Current phase/packet ownership retained; English docs, no branch change or historical-log rewrite. |
| Phase 03 complete plan/register; P03-1 accepted/revised package, relevant Step 3–5/merge records; completed 02a/02d/02b handoffs | G2 execution/G5 enforcement/G6 policy/G7 remain open. Their foundation acceptance does not authorize live execution. |
| ADR-0003/0010/0017/0032/0033/0036/0040/0044/0052/0064/0065/0072–0074 and their relevant Amendments | Four-role RLS, canonical pipeline, ninth candidate-reader authority, fixed manifests, public/AccountSite ceiling and plural audit survive. New execution/HTTP/evidence choices need named vehicles. |
| Standards 04/05/10/11/18/19/21; Architecture 03/09/13/23/31/33; Identity/Tenancy/Education/Customization/Audit module contracts/matrices | Read scope, sensitive-read transactions and denied evidence have explicit pending owners; proposed carrier changes are listed below. |
| Actual resolver/factory/recorders, credential/binding admission, UoW/announcement/interceptor/filter, lifecycle port, contextual stores/DDL, manifest/seed source and audit coordinators/composer | Deny-all, absent carrier, Off/best-effort denial gaps and host-budget misuse after successful row 14 are verified latent activation gaps. |
| Endpoint/runtime census, public/AccountSite tests, framework-job/source admission and real Customization initializer consumers | Exact existing classifications must be backfilled; neither Ambient nor SystemActor grants a general exception. |
| implement-task/start-task dispatch, write-adr, add-permission, commit-and-pr | add-permission's IModule/default-grants sketch is stale against adopted Standard 19 and must be replaced on acceptance before use. |

The obsolete IModule/default-grants sentence also appears in Tenancy and
Customization permission matrices. A separate preflight correction adopts the
already Accepted module-source/finite-manifest authority in these mutable
carriers; no new module-loader interface follows. Existing matrix
examples are not a declaration of every future permission in this packet.

Primary-source checks: PostgreSQL 18 documents separate READ COMMITTED statement
snapshots; ASP.NET Core 10 documents that authenticated fallback alone is not a
missing-classification denial and that AllowAnonymous bypasses authorization
failure. [ADR-0076](../decisions/0076-live-scoped-authorization.md) links those
sources. No package upgrade is selected.

#### Upfront decision surface

| Gate | Proposed answer and vehicle | Remaining boundary |
|---|---|---|
| G2 execution | ADR-0076: exact verified candidate, fresh isolated READ ONLY app-role owner, ninth logical setter, per-check freshness and operation-bound tenant-read witness; ADR-0077 supplies successful claim-only assertion evidence | No new context writer, global User reader, cache, synthetic context or bypass. Lifecycle/closure transition races retain P03-3/10 ownership. |
| G5 enforcement | ADR-0076: explicit scoped policy registry, per-act committed grant query, exact finite system/seed sources, virtual Portal Public and separate ordinary grant-edit/release-manifest adoption | No wildcard, automatic new-key grants, custom role, implicit seed upgrade or Platform permission. |
| G6 policy ordering | ADR-0076: runtime HTTP classification plus independent step-5 application check; capability before resource lookup; one useful administrative member-directory GET moves forward from P03-3 | Native authenticated allowlist, write endpoints, editing UI and scope reissue remain P03-3. |
| G7 | ADR-0077: one central denial-event owner, separate existing resource intents, bounded immutable trusted provenance, MUST sensitive disclosure and per-occurrence claim-context assertion event | No generic audit-every-404 policy, new suppression budget, email mirror or sentinel route. Privileged operator evidence remains G13/P03-9. |

The principal maintainer tradeoffs are fresh uncached database dependence; a
five-second candidate admission deadline; already-admitted work and separate
cross-module snapshots; administrative cross-branch directory disclosure; and
per-occurrence durable denial volume under existing retention. ADR-0077 gives
illustrative storage arithmetic, not a load qualification. Both proposals must
be accepted together with this package before code.

The directory reuses Tenant `identity.membership.admin`; it needs no v2 grant or
auto-adoption. The three virtual-public definitions are nonassignable and preserve
existing anonymous visibility. Real grant editing/adoption uses only accepted v1
pairs/manifest in P03-2. A newly registered assignable key would still need its
own approved finite manifest and database registry-guard migration.

#### Acceptance instruments and carrier edits

The acceptance step appends the following **dated** Amendment content with the
next free numbers at that time. These are proposed texts, not current authority:

- **ADR-0036:** “ADR-0076 specifies the previously admitted candidate reader and
  live protected-act checks without changing the resolution matrix. Successful
  ClaimAndMembership resolution followed by a conflicting header uses ADR-0077's
  separately classified Membership-confirmed assertion event. Refused row 14,
  row 10, host-backed evidence and public/AccountSite host ceilings are unchanged.”
- **ADR-0040:** “ADR-0076 specifies the ninth logical candidate-reader setter
  through an isolated physically READ ONLY owner with exact candidate/generation
  announcement. The ordinary step-6 owner additionally consumes immutable
  operation read authority for SQL/EF scope. Same read-scope joins preserve it;
  incompatible joins poison rather than retarget. No new logical tenant setter,
  context writer or bypass role is admitted.”
- **ADR-0064:** “ADR-0076 narrowly replaces the AccountSite-only authenticated
  surface with one additional administrative GET /api/v1/identity/memberships.
  It admits HostAndClaim or confirmed ClaimAndMembership under Tenant
  identity.membership.admin and ordinary MUST sensitive-read durability.
  AccountSite gains live membership/lifecycle admission but retains its separate
  HostScope and Off/READ ONLY shape. Present invalid credentials remain uniform
  401; absent protected credentials/noncoverage remain masked 404; capability
  denial is 403. No resolution-by-endpoint, mutation or native proxy follows.”
- **ADR-0073:** “ADR-0076 authorizes live execution and explicit audited edits/
  adoption outside the immutable kernel. The existing five assignable pairs,
  eight templates and v1 seed manifest remain; virtual public definitions grant
  no stored Role authority. Existing seed verification is not an upgrade writer.”
- **ADR-0033/0044, in each record:** “ADR-0077 adds the common typed authorization
  denial event and trusted immutable enrichment. Its MUST security-event row is
  distinct from each operation's existing plural resource intents and does not
  promote their ordinary classification. HTTP and application adapters share
  ownership; nested refused acts retain their own event. Refusal-audit failure
  preserves refusal; successful sensitive disclosure still requires its own
  same-transaction MUST row before response. Tenantless/validation omissions and
  the two existing sentinel classes remain unchanged.”
- **ADR-0065:** “ADR-0077's authorization and Membership-confirmed header events
  do not consume this host-tenant allowance. Its independently defended host key,
  suppression semantics, counters and scope remain unchanged.”

Update decisions index/phase Status/register only after explicit acceptance.
Standards 04/11/19 own exact policy/route/error/freshness rules; Standard 05 owns
candidate/ordinary announcement and EF/RLS witness lifetime without a copied
template; Standard 18 and module matrices own exact new evidence slugs/populations;
Standard 10 owns closed diagnostics. Standard 21 updates canonical Asserts/owners
and honest status. Architecture 09/13/23/31/33 and glossary link those sources.
ActorEmail stays null. Include current code XML and the obsolete permission skill/
matrix carriers; no claim that an acceptance commit implements those controls.

#### Six implementation steps

Every step is committed, reviewed by fresh agents in two independent rounds,
fixed only for independently verified findings and revalidated before the next.
Review records name the source revision, scope, actual commands and limitations.
No invented review transcripts or count-only proof substitutes are recorded.

| Step | Implementation and closure condition |
|---|---|
| 1 — Accept and reconcile | Accept both ADRs/package; append exact affected-record Amendments and canonical carrier updates; classify complete current production requests/endpoints/sources and register proof obligations without pretending implementation. Recheck the independently corrected skill/matrix guidance. Validate whole-tree docs and exact source census; no live activation. |
| 2 — Candidate and live authority | Implement narrow candidate announcement/UoW owner, bounded actual Identity query and Tenancy lifecycle handoff, trusted source descriptors and immutable results. Prove real app-role READ ONLY/no profile/global directory, wrong/stale/foreign candidate, lifecycle/coverage/grants, cancellation/outage/cleanup and grant-revocation races. Keep deny-all production registration until evidence is ready. |
| 3 — Policy and scoped execution | Implement immutable policy composition, independent HTTP/application classification and step-5 capability checks, finite public/seed/event/job classes, read-witness mint/consume and exact SQL/EF scope. Prove missing/forged/reused witnesses, nested mismatch poisoning, no org write widening, real system/default-initializer positives and unrelated-command negatives. Activation remains held for Step 5. |
| 4 — Evidence and grant acts | Implement typed central denial/event ownership and captured provenance, new assertion provenance path, contextual sensitive query/catalogue and audited grant-edit/v1-adoption commands. Preserve plural intents and kernel/last-admin/lock rules. Prove exact Off/SHOULD/MAY/override-Off and nested populations, MUST rollback, delegation/current-authority checks and hostile sink canaries. No human write route. |
| 5 — Useful HTTP activation | Add exact administrative directory controller/DTO/pagination/OpenAPI/SDK, runtime classification and live production wiring together with evidence. Qualify actual rows 6/7/9/10/14, revoked matching-host control, real claim-only directory positive/header mismatch, AccountSite's independent host ceiling, public zero-grant-lookup behavior and storage failure outcomes. Do not enlarge native BFF operation allowlist. |
| 6 — Complete qualification and handoff | Run relevant full backend architecture/unit/integration/contract populations, source-bound inventories and meaningful seeded/provider/native compatibility checks; regenerate SDK, shard plan/manifest as required by changed population, preserving ADR-0063/0075 bounds. Complete fresh hosted A/B where source/distribution requires it, preserve raw evidence, update catalogue/docs/packet state honestly, then open the reviewable development→main PR. |

Workflow dispatch: add-permission (after its adopted-contract repair),
add-mediatr-handler, add-ef-migration if actual schema/registry-guard changes are
needed, standards-check/code-review, run-tests-locally and commit-and-pr. No
destructive migration is planned. Schema updates do not gain authority merely
because they are additive.

#### Mandatory proof ownership

- Extend `TenantAuthenticationReconciliationTests`, rejection/recorder/budget and
  actual HTTP/runtime surface suites; use real Membership rows, not a substitute
  affirmative membership reader. Use a distinct real directory subject for row 14.
- Add Identity authorization-reader/permission/HTTP suites; retain
  `IdentityScopeLifecycleTests`, real app-role clean/dirty controls and explicit
  two-connection revocation synchronization. Global binding eligibility is a
  separate earlier point, not a new generic owner function.
- Extend `TenantQueryFilterTests`, UoW/transaction tests and
  `Tenant_Scope_Widening_Is_Never_Set_From_Request_Input`; compare real SQL/EF and
  pooled generation state. Catalogue statuses reflect actual tests only.
- Complete `Permission_Definitions_DeclareScope`,
  `Permission_Scope_Matches_Resource_Scope`, `Permission_Keys_Match_Convention`,
  `Permission_Registry_Has_DeniedTest` and the real authorization-denied clause of
  `MustClass_Audit_Writes_Share_The_Business_Transaction`. Public virtual and
  confined system/seed classifications have separate negative controls.
- Extend `AuditLogBehaviorTests` and real database/HTTP evidence tests for the
  separate denial event/resource intents and same-transaction sensitive read.
  Source/actor/role/IP/route spoofing and raw-value sink canaries need positive
  event witnesses; no audit loss on cancellation is inferred from static scans.
- Real pinned Keycloak/native compatibility covers existing four personas,
  independent host scope and no new browser bearer/API operation. Production
  p95/full WCAG and later UI/closure/archive behaviors are not claimed here.

**Initial review limits.** No runtime test or provider was run in this preflight.
No implementation step is complete. Two independent proposal-review rounds and
mechanical document validation must finish before the maintainer decision.

### P03-2 proposal review round 1 (2026-10-27)

**Reviewed source:** `0389defd`, against merged baseline `daf03a48`. Two fresh
independent `gpt-6-astra` reviewers at xhigh effort examine authority/execution
and evidence/privacy/corpus respectively. Both inspect actual adjacent source;
neither edits files or runs runtime/provider tests. The parent separately
verifies each disposition. No implementation approval is inferred.

- **Major, fixed:** define the ordinary delegation ceiling for Organization
  templates. Orgless Tenant Admin holds Tenant pairs, not Organization pairs.
  ADR-0076 now explicitly admits same-key Tenant-to-Organization authoring under
  current role-admin authority, without evaluation equivalence, reverse scope,
  other-key implication or a general self-escalation exception. Its behavioral
  positive/negative obligations are explicit.
- **Minor, fixed:** enrichment follows the owning row's retention, not uniformly
  SecurityEvent. Retained metadata linkage remains outside six-column redaction;
  P03-10's Audit participant locates/reports it, without a new UPDATE grant.
- **Minor, fixed:** use 1,000 bytes/row for the decimal 864 MB/day illustration.
  Acceptance-time Amendment drafts say specifies/authorizes, not implemented.
- **Proof refinement, adopted:** the separate denial queue cannot enter the
  normal pending MUST flush or be suppressed by the outer capture's Committed
  state. Explicitly test an absorbed Off denial under a READ ONLY owner.
- **Parent clarifications:** name actual grant commands/audit slugs and post-wait
  recheck; bound the navigation-only cursor; limit the directory to current
  Active/Suspended rows; retain unmatched routing semantics and keep new
  claim-only assertion evidence free of raw asserted UUIDs.

The authority reviewer reports no further actionable finding in its slice; the
evidence reviewer approves its slice subject to the listed corrections. These
are document reviews, not proof of code, PostgreSQL or provider behavior. The
corrected proposal proceeds to a new independent second round before presentation.

### P03-2 independent preflight carrier correction (2026-10-27)

The already Accepted ADR-0073/Standard 19 contract, not either new proposal,
licenses this documentation repair. `add-permission`, Tenancy's permission
matrix and Customization's permission matrix still prescribed nonexistent
`IModule.RegisterPermissions` and registration-time automatic defaults.
The current `PermissionDefinition` and retained `IdentityFoundationManifest`
source verify the mismatch. They now describe scoped definition sources,
separate finite grant manifests and the implemented-foundation/live-enforcement
boundary. No registry, grants, endpoint, schema or runtime code changes.

The skill also stops claiming a generated catalogue exists or an attribute
automatically installs authorization. Exact denial status follows the owning
admitted surface; current/registered test status is checked rather than assumed.
Both second-round proposal reviewers receive these additional mutable-carrier
diffs for inspection. Validation: whole-tree docs/structure/index, seven ADR
parser controls and `git diff --check` pass. This is not runtime qualification
or acceptance of ADR-0076/0077.

### P03-2 proposal review round 2 and decision readiness (2026-10-27)

**Reviewed source:** corrected proposal `7159c983`, plus the independent guidance
repair at `9f0edb66`, against `daf03a48`. Two new independent `gpt-6-astra`
reviewers at xhigh effort cover authority/execution and evidence/privacy/corpus.
Neither participated in the first proposal round. Both compare the proposals
with adjacent source/Accepted contracts, inspect the three guidance repairs,
and approve their slice with no remaining verified actionable finding.

The authority review checks candidate ownership/deadline, source trust,
classification, virtual-public separation, witness nesting, directory/BFF staging,
revocation, grant adoption and acceptance instruments. The evidence review checks
the separate denial queue against the real pending/committed shortcuts, nested
refusals, plural intents, claim-only attribution, trusted address/UA, retention/
redaction and sensitive disclosure. Their approval is of a Proposed design,
not implementation or a production safety/availability guarantee.

**Validation.** Whole-tree Markdown fragments, roadmap structure and generated
indexes pass; the seven ADR parser controls pass, both new records parse
Proposed, and `git diff --check` passes. No backend/runtime/provider tests were
run for this documentation-only pass. No runtime, database privilege/schema,
provider configuration, BFF allowlist, protection rule or external service was
changed. Work stays on `development`; the preexisting worktree directory remains
untouched. The commits are local pending the maintainer's decision.

**Decision requested.** Jointly approve ADR-0076, ADR-0077 and the six steps in
the proposed package above. This includes the minimum administrative directory
GET in P03-2, fresh database admission and separate per-occurrence denial evidence.
G2 execution/G5 enforcement/G6 policy/G7 remain Open until that acceptance.
The maintainer's explicit new-ADR approval requirement is the remaining gate;
the independent repairs already licensed by ADR-0073 are complete. After approval,
acceptance/reconciliation is Step 1, followed by the committed implementation and
two-fresh-review-round cycle for every step. There is no approval request for an
unspecified later redesign or extra runtime scope.

### P03-2 maintainer review and revised package (2026-10-27)

**Status: Proposed; acceptance withheld.** The maintainer reviews `b81ed063`
and requests correction of nested-owner liveness, absorbed-denial durability,
occurrence identity and retention wording. All four findings are valid against
the current proposal/code; none is a claim of an already exposed runtime flaw.
The previous readiness record describes its then-reviewed source, not closure
of these subsequently identified risks. The new ADRs remain Proposed and all
four P03-2 execution gates remain Open. No runtime implementation begins.

#### Verified findings and choices

| Finding | Source verification | Revised contract |
|---|---|---|
| F1 — nested/post-lock pool starvation | `PersistenceCompositionExtensions` supplies one app source; ordinary `NpgsqlUnitOfWork` retains its connector until disposal; fresh nested checks need another checkout | Dedicated authorization source: four owners/connectors, no admission wait queue, same app role and confined READ ONLY queries. Outer business owners cannot consume that lane. Excess work fails immediately; actual cleanup owns its slot. |
| F2 — outer COMMIT precedes finally-only denial write | `TransactionExecutionCoordinator` commits and marks capture before `AuditExecutionCoordinator` finally reconciles; a crash/fault in between leaves committed business with only an in-memory event | Pre-success barrier: successful ReadWrite owner inserts unresolved events on its own transaction; ReadOnly/ownerless success requires acknowledged standalone evidence first. Audit failure prevents otherwise-successful commit/disclosure; actual refused callers preserve their refusal. |
| F3 — occurrence identity/duplicate semantics undefined | Ordinary reconciliation uses a fresh time; `audit_log` key is `(id,timestamp)` and generic standalone accepts 23505 without exact row comparison | One central occurrence reference with fixed id/microsecond decision time and complete frozen draft. Confined store seams verify expected-key duplicates in a clean scoped read transaction. Exact event evidence never proves business success. No cross-restart/request deduplication is promised. |
| F4 — retention wording implies implemented purge | Standard 18's Retention section explicitly declares the schedule a policy target with Phase 11 mechanisms absent | Describe seven-/two-year targets beside metadata and capacity cost; automatic purge/override/partition lifecycle is unimplemented, so retained data can outlive its target. |

Two additional read-only design investigations verify adjacent constraints:
an extra Npgsql pool can still share a saturated PgBouncer backend pool, and a
new candidate UoW cannot silently claim a job's existing main-unit slot. These
are included in F1's actual source/lifetime/capacity contract, not deferred to
generic load testing. The parent checks the actual source and primary Npgsql,
PostgreSQL 18 and PgBouncer documentation linked from ADR-0076.

**Chosen resource layout.** Keep the ordinary business source. Add a confined
four-slot authorization source and two-slot standalone evidence source under
the same `learnstack_app` role, with no raw DI aliases/generic callbacks. At most
six additional physical connectors per process are owned, including opens and
retained cleanup. Neither auxiliary owner borrows from the other source or
business source while held. Authorization uses only nonlocking MVCC statements;
evidence uses only exact INSERT/verification, never business dispatch/locks.
Track both job auxiliary lanes and join actual cleanup before source disposal;
do not suppress trusted execution markers. The 4/2 caps are explicit bounded
concurrency choices, not a measured production requirement or guaranteed fairness.

`ConnectionStrings:IdentityAuthorization` and
`ConnectionStrings:AuthorizationEvidence` are a coupled explicit source pair.
Only Development may derive both from direct-PostgreSQL Default without another
credential; each remains a physically separate bounded source. Other modes need
both guarded strings and matching installed identity authority. PgBouncer
transaction mode uses separate database aliases
`learnstack_identity_authorization` / `learnstack_authorization_evidence` for
those lanes, targeting the same actual database/app role. Cross-alias limits,
total PostgreSQL capacity and admitted instance count must preserve headroom
outside business capacity. Application names are not backend reservations.

Small-pool progress through real PostgreSQL and a pinned transaction-pooling
PgBouncer fixture is mandatory **in P03-2** before activation. Hold every business
connector while admitted nested and post-lock authorization and ReadOnly
standalone evidence complete. A shared-backend dirty topology must fail the
same progress check. Separately prove auxiliary saturation, immediate excess
refusal, no use of the parent's uncommitted grants, delayed/failed cleanup and
tracked job shutdown. Phase 11 still owns actual production topology/capacity
qualification before production activation; that does not replace these tests.

**Chosen denial guarantee.** Each event has per-owner written/acknowledged/
rollback/uncertain state independent of the business capture flag. After
domain-event draining, the owner enforces the barrier before final commit
admission, retaining the existing no-await commit boundary. A joiner never
acknowledges or clears the event. An acknowledged ReadWrite commit carries it
atomically. ReadOnly success cannot disclose before its separate acknowledgement.
Unknown business COMMIT retains its uncertainty even if exact denied-event
evidence exists. A crash before any applicable write commits can lose an
uncompleted in-memory refusal; no durable recovery queue or universal replay
ledger is introduced. Refusal-audit outage remains an explicitly signaled loss.

The fixed reference is reused only for that occurrence's owned attempts. A
new request/restarted delivery or genuine nested evaluation gets another one.
Normalize UTC to PostgreSQL microseconds once. The new denial seams reuse the
sole central INSERT/binder and exact captured app-role tenant/org announcement,
but not ordinary standalone's fresh timestamp/broad-23505 behavior. Only the
expected `audit_log_pkey` duplicate admits clean-transaction, null-safe full draft
comparison. Missing/foreign/mismatched/unreadable evidence fails closed. A later
legitimate redaction can prevent an exact match; no indefinite replay witness or
extra redaction privilege is promised. Existing resource intents keep their
ordinary different-timestamp reconciliation and plural attribution unchanged.

#### Revised acceptance and six-step closure

The original six steps, directory/public/BFF/grant scope and all retained P03-3/
P03-9/P03-10 boundaries stand, with these binding additions on acceptance:

- **Step 1:** canonical source/owner/lifetime/capacity census includes the 4/2
  lanes and two guarded keys. Extend Standard 05 Connection Management/job
  ledger, Standards 10/11/12/18/21, environment/development/Compose/Seeder setup
  and Phase 11's actual topology qualification trigger. New keys carry no extra
  credential into restore/build. Include exact denial-method/occurrence/barrier
  obligations; acceptance still claims no runtime proof.
- **Step 2:** candidate execution includes the dedicated source and complete
  borrower/job tracking, bounded open/cleanup ownership and small-pool progress.
  Its statements remain committed-only, physically READ ONLY and free of parent
  lock/callback dependencies. Do not substitute an outer writer's snapshot.
- **Step 3:** nested/post-lock policies use that same confined lane, preserving
  invocation/read-scope lifetime. Prove saturation does not create a circular
  checkout dependency; excess admission remains an explicit 503.
- **Step 4:** implement the per-event barrier, fixed-reference denial store seams
  and independent two-slot evidence lane. Prove ReadWrite commit/crash, rollback,
  ReadOnly/ownerless disclosure, store/composer fault, exact/mismatched/unknown
  COMMIT, stable timestamp and real distinct-versus-reentered invocation cases.
- **Step 5:** activation waits for those actual direct/PgBouncer progress and
  crash/fault controls, not only a successful directory HTTP request. Preserve
  real rows 6/7/9/10/14 and all earlier host/public/BFF ceilings.
- **Step 6:** full inventories include actual new sources/jobs/fixture images and
  test populations with pinned receipts. Performance tuning remains distinct
  from these correctness gates; no unrun topology or crash proof is claimed.

The following exact Amendment additions supplement the original append-ready
acceptance text; use next free numbers only after maintainer acceptance:

- **ADR-0040:** “ADR-0076's candidate owner uses its confined authorization source,
  not a nested checkout from the retained business pool. ADR-0077's evidence lane
  completes standalone events before otherwise-successful ReadOnly/ownerless
  publication. Each lane has bounded retained owners, exact app-role scope and
  downstream headroom; neither adds a logical tenant setter or context writer.”
- **ADR-0033 and ADR-0044, each:** “ADR-0077's denial occurrence has a fixed id and
  decision timestamp across owned writes/reconciliation. Expected-key duplicate
  evidence requires an exact scoped draft match and proves no business outcome.
  The enclosing successful ReadWrite commit atomically carries unresolved denial
  events; ReadOnly/ownerless success requires their standalone acknowledgement
  first. Audit failure prevents outer success before commit/disclosure, while
  an actual refused caller retains its refusal. Process loss before any event
  commit can lose an uncompleted refusal; no durable invocation/replay ledger is
  added. Ordinary plural resource reconciliation remains unchanged.”
- **ADR-0047:** “ADR-0076/0077 add explicitly tracked authorization/evidence
  auxiliary lanes to the application-source owner. Their separate global caps,
  actual-cleanup retention/join and physical source lifetimes supplement the
  existing main/auxiliary business ledger. No second main unit, ambient-marker
  suppression, broader SystemActor permission or quiescence shortcut is admitted.”

**Tradeoffs for approval.** Six auxiliary connectors per process and production
backend reservations; immediate 503/audit-unavailable under auxiliary saturation;
absorbed denials can prevent outer success; and no universal restart deduplication
or recovery of an uncompleted refusal. The directory/grant/evidence volume and
retained-linkage tradeoffs in the initial package remain. These revisions are
Proposed; new independent review rounds and document validation precede renewed
maintainer acceptance. No runtime, schema, grant or protection setting changes.

### P03-2 revised proposal review round 1 (2026-10-27)

**Reviewed source:** `ead0a22b`, against the maintainer-reviewed `b81ed063`.
Two new independent `gpt-6-astra` reviewers at xhigh effort examine F1/resource
liveness and F2/F3/F4/evidence durability respectively. Neither performed the
preceding design investigations or original proposal reviews. Both inspect the
actual adjacent implementation/Accepted constraints and approve their scope
without a verified actionable decision gap. No runtime or database tests run.

The liveness reviewer confirms retained ordinary connectors, explicit job-lane
ownership, separate four/two-slot sources and downstream PgBouncer reservations.
Its useful execution note is incorporated: the pool proof must hold actual open
server transactions and check backend PIDs, not only client connectors that a
transaction pool may leave unassigned. Dirty shared-backend topology remains
mandatory. The evidence reviewer confirms pre-success barriers for all owner
modes, fixed occurrence/time/full-field duplicate semantics, truthful retention
targets and accepted-carrier compatibility. These approvals concern the
Proposed contracts; source implementation, runtime progress/crash proofs and
production capacity remain unqualified. Parent edits otherwise only clarify
already-repaired guidance and reflow prose. A fresh second round follows.

### P03-2 revised proposal review round 2 and readiness (2026-10-27)

**Reviewed source:** `55662b56` against maintainer-reviewed `b81ed063`. Two new
independent `gpt-6-astra` reviewers at xhigh effort cover resource/liveness and
durability/identity/privacy. Neither performed the revised first round or design
investigations. Both independently inspect current adjacent source and Accepted
constraints, and approve their review scope without a verified actionable
decision/proof gap. No source implementation or runtime test is claimed.

The resource review confirms independent four/two-slot lanes, no reverse
business/evidence/authorization acquisition, tracked job cleanup, explicit
configuration, downstream cross-alias/user/instance headroom and real server-
transaction/PID dirty-control requirements. The durability review confirms all
pre-success owner cases, event state versus capture state, stable microsecond
occurrence identity, clean scoped full-draft duplicate matching, unknown-COMMIT
limits, refusal preservation and truthful retention targets. It additionally
reads the parent's narrow wording clarification: one evidence operation shares
one five-second I/O budget across open/INSERT/COMMIT and duplicate verification;
verification never starts a fresh budget. Actual cleanup remains retained.

**Maintainer findings disposition:** F1, F2, F3 and F4 are all valid and corrected
in the proposed contracts, acceptance carrier texts and mandatory proof plan.
None is skipped. The implementation gates remain tests to run, not documented
test passes. In particular, no small-pool, PgBouncer, process-crash, cancellation
or ambiguous-COMMIT runtime result exists for this proposed source yet.

**Validation:** whole-tree Markdown fragment/roadmap/index checks, seven ADR
parser controls, both Proposed lifecycle declarations and `git diff --check`
pass. The two revised ADRs and six-step delta remain local on `development`;
the existing untracked worktree directory is untouched. No runtime/schema/grant,
provider, protection or external-service change is made.

**Decision requested:** jointly accept the revised ADR-0076/0077 and the revised
package above. Added costs are bounded auxiliary sources/production headroom,
possible immediate 503 under saturation, retained cleanup/shutdown ownership,
and failure of otherwise-successful outer work when required denial evidence
cannot commit. No durable restart/replay ledger or automatic purge is implied.
The maintainer withheld acceptance in the preceding review; that decision is
not inferred from correction completion or reviewer approval. Until explicit
acceptance, G2 execution/G5 enforcement/G6 policy/G7 remain Open and runtime
implementation stays held under the maintainer's new-ADR approval instruction.

### P03-2 joint acceptance and Step 1 (2026-10-27)

**Acceptance receipt.** The maintainer explicitly accepts revised ADR-0076,
ADR-0077 and the six-step package at `725d255a`: “Revize ADR’leri ve planı
onaylıyorum; uygula”. This closes their contract approval gate, not an
implementation or production-readiness gate. Both ADRs become Accepted jointly
on 2026-10-27. Work remains on `development`; no new-user HTTP/BFF flow, native
operation allowlist expansion, mutation route or Platform admission is added.

The approved F1–F4 corrections are binding: independent four-owner authorization
and two-owner evidence sources, actual downstream backend reservation/progress,
pre-success evidence for absorbed denials, fixed occurrence identity/time with
full-draft reconciliation, and policy-target retention without implemented purge.
The bounded directory uses the existing Tenant membership-admin grant. The
revised six-step package above remains the implementation plan; every step has
its own commit, two fresh independent review rounds and verified corrections.

**Canonical reconciliation.** Own-record dated Amendments are appended to
ADR-0033/0036/0040/0044/0047/0064/0065/0073, preserving their historical bodies,
statuses and dates. Standards 04/05/10/11/12/18/19, the module matrices/specs,
architecture 09/13/23/31/33, glossary and operational/Phase 11 handoffs reconcile
with the approved scope. Standard 05 owns source/cleanup capacity and read
witness mechanics; Standard 11 owns placement; Standard 18 owns evidence and
retention; Standard 19 owns definitions/delegation; Standard 04 owns the directory
wire contract. Architecture and deployment notes link those owners.

The phase register records accepted G2 execution/G5 enforcement/G6 policy and
directory/G7 evidence slices. P03-3 retains native/mutation/UI and new-user
activation decisions; G13 retains privileged population. Acceptance alone does
not replace deny-all membership wiring or the application pass-through shell.

**Baseline source-classification census.** The inspected production source at
`725d255a` contains 46 concrete MediatR request declarations. This declaration
inventory scopes the complete classification implementation; it is neither a
compiled census proof nor an exemption from runtime default deny. Step 3 compares
the actual compiled requests/handlers and trusted owner call sites in both
directions and plants missing/extra/forged-source controls.

| Population | Count | Required classification |
|---|---|---|
| Existing public queries | 4 | Exact virtual Portal Public policy; unchanged host/publication/READ ONLY ceiling |
| AccountSite query | 1 | Exact special host-backed protected policy, no assignable permission and Off success |
| Contextual seed verification queries | 12 | Exact finite seed-owner request authority, no human route |
| Identity seed commands | 4 | Existing private witness/owner admission, never SystemActor-by-value bypass |
| Other seed/system writers | 18 | Exact existing seed composition; two named initialization commands also have admitted delivery owners |
| Framework job requests | 7 | Existing exact framework source/profile gates; no general business-job authority |
| **Total** | **46** | Every concrete request accounted for once |

- Public: `GetPublicSiteQuery`, `GetPublicCoursesQuery`, `GetPublicCourseQuery`,
  `GetPublicLessonQuery`. Their three virtual definitions add no stored grants.
- AccountSite: `GetAccountSiteQuery`, retaining matching real HostScope and
  HostAndClaim in addition to its new live membership/lifecycle check.
- Tenancy seed verification: `GetTenantSeedStateQuery`,
  `GetOrganizationSeedStateQuery`, `GetHostMappingSeedStateQuery`,
  `GetSettingSeedStateQuery`.
- Customization seed verification: `GetContentTypeSeedStateQuery`,
  `GetTaxonomySeedStateQuery`, `GetActiveContentTypeSeedRevisionQuery`,
  `GetActiveTaxonomySeedRevisionQuery`.
- Education seed verification: `GetCourseSeedStateQuery`, `GetLessonSeedStateQuery`.
- Identity seed verification: `GetRoleSeedStateQuery`, `GetMembershipSeedStateQuery`.
- Identity seed commands: `InitializeSeedRoleCommand`,
  `CreateFirstSeedAdminMembershipCommand`, `CreateSeedMembershipCommand`,
  `SetSeedMembershipProfileCommand`.
- Tenancy seed writers: `ProvisionTenantCommand`, `CreateOrganizationCommand`,
  `MapHostToTenantCommand`, `AddTenantLocaleCommand`,
  `SetDefaultTenantLocaleCommand`, `SetTenantBrandingCommand`.
- Customization writers: `RegisterTenantContentTypeCommand`,
  `PublishTenantContentTypeCommand`, `RegisterTenantLevelTaxonomyCommand`,
  `PublishTenantLevelTaxonomyCommand`, `InitializeTenantContentTypeDefaultCommand`,
  `InitializeTenantLevelTaxonomyDefaultCommand`.
- Education seed writers: `CreateCourseCommand`, `AddCourseTranslationCommand`,
  `PublishCourseCommand`, `CreateLessonCommand`, `AddLessonTranslationCommand`,
  `PublishLessonCommand`.
- Framework jobs: `ReadBackgroundJobEnvelopeQuery`,
  `AdmitBackgroundJobAttemptCommand`, `ReadBackgroundJobExternalStateQuery`,
  `RecordBackgroundJobArmCommand`, `RecordBackgroundJobOutcomeCommand`,
  `RecordBackgroundJobCapacityCleanupCommand`, `SubmitBackgroundJobRecipientCommand`.

The baseline HTTP consumer inventory is eight public GET/HEAD actions and the
single AccountSite GET, plus explicitly classified infrastructure health/OpenAPI
surfaces. Routing misses keep 404/405 semantics. The planned directory is the only
additional routed operation. No current seed command becomes a human permission
operation merely because it is reachable through the Seeder.

Non-MediatR trusted entries are also explicit: Customization's
`InitializeContentTypeDefaultHandler` and `InitializeTaxonomyDefaultHandler`
consume their exact two initialization commands through the validated delivery
owner; existing global binding/self lookup, seed adoption and creation/
reconciliation retain the prepared global owner. These remain separate from the
ordinary tenant policy population and add neither Platform nor HTTP creation
capability. The compiled source/caller census must include these boundaries and
actual job auxiliary lanes, not rely on an ambient UUID or a comment.

**Proof registration.** Standard 21 registers four new owner/policy/evidence/
provenance obligations and moves three permission placeholders from Awaiting
backfill to Registered with named proof owners. Existing Implemented rules keep
their shipped subjects and explicitly pending P03-2 extensions; no new runtime
control is promoted. Five planned audit rows are present once in the owning
Identity/Tenancy matrices, with the central denial/assertion rows also off-path.

**Step 1 review state.** Acceptance/carrier implementation is under validation;
the two fresh independent review rounds follow the initial committed source.
No authorization source, database privilege/schema, policy adapter, live membership
wiring or new endpoint is implemented by this documentation step. Runtime,
PostgreSQL/PgBouncer progress, cleanup, crash and ambiguous-COMMIT evidence remain
mandatory in the later steps. The preexisting untracked worktree directory is
untouched.

**Step 1 initial validation.** `make docs-index` regenerates the log index;
`make lint-docs` passes with 2,729 resolved fragment links and all phase/index
structure checks. All seven ADR parser controls pass; both records parse
Accepted. Release `CorpusConsistencyTests` and `AuditCoverageTests` pass 20/20
with zero skips, including the new planned/off-path matrix population. Changed
relative targets, private-scratch reference scan and `git diff --check` pass.
These tests validate documentation/catalogue compatibility, not the new runtime
contract. The two fresh implementation-review rounds remain next.

### P03-2 Step 1 review round 1 and setup correction (2026-10-27)

**Reviewed source:** `e8266bbb` against approved `725d255a`. Two new independent
`gpt-6-astra` reviewers at xhigh effort inspect authority/execution/census and
evidence/privacy/corpus. Neither authored the carrier changes. The authority
review independently reproduces the 46-request/nine-action baseline and checks
candidate/witness/delegation/HostScope and own-record Amendment boundaries. The
evidence review confirms the approved barrier, occurrence matching, crash limits,
retention and planned/off-path distinctions. These are documentation/source
reviews; no new runtime progress or durability proof is claimed.

**Verified finding:** the environment template and local-development skill lack
the approved pending setup handoff. Their four-login-role baseline is preserved,
but their wording now distinguishes roles from additional app-role sources and
links the coupled source/topology ledger. Executable environment assignments
remain unchanged; API/Seeder projections change with qualified runtime wiring.
The parent also completes the approved permission-workflow reconciliation:
replace illustrative endpoint/resource-handler snippets with the accepted scoped
HTTP/application policy, resource ordering and operation-witness contract, without
inventing an implementation API. Registered proof status remains explicit.

The authority slice approves its inspected contract scope; the evidence slice
approves with the setup minor above. The parent verifies/corrects that finding;
a second fresh independent round reviews the committed correction and complete
Step 1 delta. Work stays on `development`; no runtime wiring is changed.

### P03-2 Step 1 review round 2 and closeout (2026-10-27)

**Reviewed source:** `c7000844` against approved `725d255a`. Two new independent
`gpt-6-astra` reviewers at xhigh effort inspect the complete authority/security/
source-classification and evidence/privacy/durability slices. Neither authored
Step 1 or performed its first review round. Both approve their read-only scope
with no verified actionable finding. They independently verify the setup/
permission-guidance correction, unchanged executable environment assignments,
46-request/nine-action census, immutable historical prefixes, pending proof
statuses and the approved barrier/source/scope limits against adjacent code.

**Validation:** both reviewers run the whole-tree docs/structure/index checks
(2,735 resolved fragment links) and range diff checks successfully. The initial
Release 20/20 catalogue/audit-matrix controls and seven parser controls remain
the applicable tests; no new runtime proof is inferred from their review.
No finding is left unresolved or silently deferred by this closeout.

**Step 1 complete.** Joint acceptance, canonical reconciliation, baseline source
classification and proof registration have their commit and two fresh review
rounds. Step 2 proceeds automatically: implement the confined candidate reader,
trusted input/child-owner seam and independent bounded sources, then qualify
committed-only facts, physical READ ONLY, lifetime and direct/PgBouncer progress.
Production deny-all/live HTTP wiring remains held until the approved activation
step and its complete evidence paths. No further maintainer approval is needed
for the accepted six-step scope.

### P03-2 Step 2 — Confined candidate and committed authority (2026-10-27)

**Implementation.** The exact HTTP candidate minter consumes quota-admitted,
normalized existing-binding evidence. A raw principal/UserId, replaced subject,
quota/host/identity feature, canceled request or disposed source cannot vouch for
its captured candidate. Kernel construction stays private and its production
minter/caller set has compiled clean/dirty controls. No production membership
registration is changed: the API still registers the deny-all adapter until
Step 5's complete policy/evidence activation.

`IdentityAuthorizationReadSource` owns four retained slots/connectors with no
waiting admission queue. A dedicated child is READ COMMITTED, physically READ
ONLY and exactly announced before data; it installs no TenantContext and exposes
no raw source or caller-supplied SQL/callback API. The immutable generation/view
cannot join, retarget, widen or independently resolve the owner. Expired/canceled
views stop vouching while the underlying cleanup remains retained. The coupled
configuration selector is implemented; production/launch composition and evidence
execution remain later steps. Direct-PostgreSQL derivation is a supported topology
precondition, never an endpoint-string/port inference.

The actual Identity statement reads only current eligible Memberships, compatible
assignments, nonretired closed Role templates and active assignable grants. It
bounds facts to two Memberships/eight Roles, refuses excess/ambiguous evidence,
ignores unknown/virtual stored grants, and projects no Profile/User/provider data.
Tenancy's narrow authorization overload runs its separate lifecycle statement on
that same child/generation; its older seed/lifecycle entry remains compatible.
Both statements see committed state, not a parent's uncommitted authority. Store,
deadline or cleanup failure yields fixed Unavailable without prepared facts;
actual request cancellation propagates after owned cleanup.

**Role-guard deadline correction.** Inspection of pinned
[Npgsql 10.0.0's connector initializer](https://github.com/npgsql/npgsql/blob/v10.0.0/src/Npgsql/Internal/NpgsqlConnector.cs#L500-L519)
shows its initializer delegate lacks an operation-token argument. Reusing the
ordinary builder would leave that guard query outside the accepted total budget.
The confined source instead awaits the existing shared role SQL explicitly on
every checkout with its operation token before BEGIN. The ordinary builder's
initializer is unchanged. A real blocked `pg_authid` control verifies cancellation
finishes while the catalog lock remains held; elapsed time releases no slot.

Named job authorization/evidence lanes enter the actual retained-resource and
cleanup-join census, preserving the one main unit and exact four business helper
seams. Evidence has no production caller yet. No ambient marker is suppressed.
Concrete Npgsql connection-disposal delay/fault twins prove retained source/job
ownership; the separate reported-command-fault control proves the denial-only
reporting seam and does not pretend to inject an actual command disposal fault.

**Progress proof.** The pinned
[PgBouncer fixture image](https://github.com/edoburu/docker-pgbouncer/releases/tag/v1.26.0-p0)
uses transaction pooling over the same actual app-role database. All business
server transactions stay held while actual nested/post-lock Identity/Tenancy reads
complete, directly and through independently reserved aliases. The fixture probes
the actual child backend PID and separately checks it and every parent through
app-role metadata; application names supply no reservation proof. The intentionally
shared saturated downstream alias fails the same progress control and recovers
only after parent release. A raw held 2/4/2-backend control proves configured alias
headroom only; production evidence-owner liveness remains Step 4. Phase 11's real
deployment/instance sizing is not claimed.

**Validation.** Release builds use `CI=true`, with zero warnings/errors. The
complete unit suite passes 2,810/2,810 with zero skips. The 52-case scoped Docker
population comprises nine source, 29 reader, eight existing lifecycle and six
pool-progress cases. The initial combined run passes 50/52; only two PgBouncer
PID-observation assertions fail after successful Covered/grant results. Replacing
the fixture's application-name inference with its actual-child PID probe makes
all six progress cases pass on the corrected source. The other 46 unchanged
cases passed the combined run. Earlier fixture SystemActor/API-shape issues were
corrected without weakening production admission.

The 71-case relevant architecture population passes 70/71 initially; its only
remaining job census still expects one cleanup task after the admitted two named
lanes add two more. The exact three-call census is corrected and that mutation
Fact passes independently. All five new compiled source/async/SQL controls pass;
the two earlier detector assumptions were corrected against actual Release IL
(reference equality is lowered to branches; the concrete command executes an
inherited DbCommand method), preserving planted failure controls. Standard 21's
method count becomes 308; the overall Registered live-policy/evidence obligations
are not promoted by this partial implementation.

**Review state.** Step 2 implementation/validation is ready for its first committed
source and two fresh independent review rounds. No HTTP route, grant/schema,
provider configuration, native BFF allowlist or production policy wiring changes.

**Commit gate.** Whole-tree docs/structure/index checks pass (2,737 fragments),
seven ADR parser controls pass, and relative-target/private-scratch/diff checks
pass. Leakwatch initially flags three nonfunctional connection-string canaries
in the configuration unit fixture. They are represented with the existing typed
Npgsql builder pattern instead of raw DSN literals; no scan is disabled or ignored.
All six affected configuration cases pass again. This changes no runtime value.

### P03-2 Step 2 review round 1 and corrections (2026-10-27)

**Reviewed source: `8ec7ee18`, against `5ee659b1`.** Two fresh independent
GPT-6 Astra reviewers at xhigh effort examine resource/configuration/job ownership
and candidate/query/trust boundaries. Both request changes. The parent reproduces
the existing authentication-authority architecture failure and verifies the job
gate and missing role-bound controls against source; all three findings remain
valid. No finding is dismissed as stale.

- `BeginAuxiliary` reconciles positively completed authorization/evidence lanes
  before its admission gate. Deterministic tests retain the owner's monitor while
  completing the real task, so its queued release continuation cannot run first;
  either lane then permits the ordinary helper. Incomplete or failed cleanup
  still refuses. The exact field/call census and planted deletion controls grow
  with this correction.
- The existing authentication-authority census enumerates the new minter's exact
  signatures, typed getters and private retained witness. The witness is a sealed
  immutable class with fixed text, avoiding a record's generated formatting and
  equality members. Only the exact newly constructed witness may travel through
  the candidate's opaque evidence argument. Shape, extra-member and substituted
  constructor controls retain the existing export/forgery refusals; no type-wide
  or assembly-wide exception is added.
- Real app-role dirty controls add a duplicate eligible template below the bound
  and nine eligible Role IDs. Each refuses with empty facts and restores its exact
  index/data. Nine roles necessarily repeat one of the eight finite templates;
  these runtime controls do not pretend to isolate the numeric guard. Separate
  compiled mutations remove/change the actual role-ID count/bound/branch/throw
  path, independently exercising that guard.

**Validation.** The corrected 35-case architecture population and 24-case
candidate/job unit population pass with zero skips. All 29 actual PostgreSQL
reader cases pass, including both new dirty Role graphs; the five compiled reader
controls pass again with the numeric-guard mutations. Release builds report zero
warnings/errors. No launch registration, HTTP operation, grant or schema changes
in this correction. A second fresh independent review round follows the commit.

**Count correction.** The prior Step 2 record transposes two cases in its
population breakdown: the original 52 cases comprise nine source, **27 reader**,
**ten lifecycle** and six pool cases. Its 50/52 combined outcome and corrected
six-case progress rerun remain accurate. The reader population becomes 29 only
with this correction's two added controls. This is a reporting correction, not
additional execution evidence.

### P03-2 Step 2 review round 2 and closeout (2026-10-27)

**Reviewed source: `7a7bb306`, against `5ee659b1`.** Two new independent
GPT-6 Astra reviewers at xhigh effort review the complete step and its first-round
corrections. The trust/query/lifecycle/proof reviewer and the separate resource/
configuration/job/pool reviewer both approve with no verified actionable findings.
Neither edits the repository or inspects preexisting worktrees.

The trust reviewer verifies the exact closed witness/forwarding census, actual
app-role three/nine-role controls and restoration, independent materialization
guard mutations, query bounds, committed lifecycle handoff and fail-closed
publication. The resource reviewer verifies completed-lane reconciliation,
retained cleanup/capacity/deadline ownership and actual downstream reservation
controls. Its independent Release no-build/no-restore unit run passes all 13
job-lane/configuration cases with zero skips. Both run diff checks; neither
reruns Docker, full suites or hosted qualification. Recorded parent runs remain
author evidence rather than independently repeated execution.

**Closeout.** Step 2 and both review rounds are complete. Its confined sources,
candidate and reader are implemented; overall live-policy/evidence obligations
remain Registered and launch still uses deny-all membership and the application
shell. Step 3 proceeds automatically under the approved plan.

**Step 3 dependency ordering.** Bring the already approved internal member-directory
query/handler/read port and its MUST catalogue entry forward from Step 4, so the
operation-bound read witness has a real production consumer in its implementation
and SQL/EF proofs. This changes sequencing only: the existing Tenant
`identity.membership.admin` pair, finite manifest, sensitive disclosure contract,
HTTP activation in Step 5 and unchanged native BFF allowlist remain. The shared
application coordinator uses a SharedKernel decision port; Identity retains its
own facts and evaluation. No common Application-to-module dependency is added.

### P03-2 Step 3 — Policy and operation-bound execution (2026-10-27)

**Implementation.** `OperationPolicyCatalog` freezes actual module definitions
and exact production request policies. It retains the five assignable foundation
pairs, adds only the three accepted virtual-public definitions, and distinguishes
public, AccountSite, protected and finite trusted acts. Missing/conflicting
declarations refuse; registration changes no Role grant or retained manifest.
The current census covers 47 requests, including the internal directory brought
forward from Step 4. Compiler-level missing/extra request and source controls
compare the whole production universe with the actual composition.

The common scoped coordinator owns each act and its nested lifetime; Identity
evaluates fresh committed facts behind the SharedKernel decision port. Neither
the same request object, resolver outcome nor earlier decision is reused as a
grant. AccountSite retains matching real HostScope; public reads call no human
candidate or membership evaluator. Exact descriptor minters bind seed authority
to its owned per-act DI scope, event authority to validated delivery/subscription/
prepared owner, and job authority to the existing retained Frame/RequestFrame.
The job adapter captures rather than re-admits an already claimed request.
Closed/faulted bindings and inherited tasks cannot revive those owners.

Only an eligible Tenant capability and declared tenant-wide read mint immutable
`OperationReadWitness` authority. Its invocation retains one-time consumption
state; the actual ordinary unit binds unit/transaction/generation and compatible
per-frame scope. Both mixed nesting directions poison outer commit, even if a
handler absorbs the inner failure. Stale/canceled witnesses refuse raw unit
access, EF and commit. The common context registration attaches the actual live
view; EF's context-instance parameter and RLS consume the same read scope while
retaining the tenant predicate and organization write restrictions. Narrow and
provisioning setup explicitly clear `app.scope`, including no-reset pool reuse.
This extends the existing logical setter, not TenantContext writers.

The real internal directory has a contract, validator/cursor, handler, EF adapter
and common MUST ReadSensitive catalogue entry. It returns only the approved
six-field projection with current Active/Suspended rows and bounded canonical
actor/tenant/org/operation-bound navigation. The bounded status enum serializes
as `Active`/`Suspended` through a type-local converter. Its ordinary writable
owner commits the read evidence before result disclosure. No directory controller
or native transport operation is activated. HTTP controller/infrastructure
metadata and named requirement values are prepared against the same policies;
an actual built endpoint census and corrupted metadata twins exercise the
classifier separately from future middleware activation.

**Composition boundary.** The explicit proof graph replaces only slot 5 with
the actual scoped adapter and dispatches the actual directory through the common
eight-stage audit/transaction pipeline. Launch retains its previous shell and
deny-all membership until Steps 4/5 jointly activate trusted denial evidence.
The new handler's real read-store port is registered in ordinary Identity
composition so API/Seeder graphs remain complete; that adapter still requires
the actual bound witness. The handler workflow skill's obsolete no-auth/implicit-
grant instructions are reconciled with the accepted source contract.

**Runtime validation.** Strict Release builds pass with zero warnings/errors.
The complete unit population passes 2,894/2,894 with zero skips. The first relevant
integration run passes 117/121: all read-scope, directory adapter, endpoint and
existing owner/filter cases pass. Three new pipeline cases first expose a missing
job catalogue in the proof graph; the real initializer control exposes the
new read-store dependency missing from ordinary DI. Both graphs are corrected
through their real registrations. Those four changed cases then pass together.
The directory's success test observes its actual COMMIT blocked at a deferred
audit gate with no visible row/result; release permits the row and result.
Real INSERT and deferred COMMIT faults withhold the result and preserve the
existing audit-error/primary-exception distinctions.

All ten direct/PgBouncer pool cases pass: the four added cases execute the actual
coordinator and Identity evaluator while every business server transaction remains
held, nested and post-lock. The saturated shared downstream alias refuses the
same policy path and recovers only after parent release. This proves policy
progress, not directory data publication or production instance sizing.

**Architecture validation.** The complete architecture run passes 575/586 cases
and identifies eleven failures. It catches a real dependency-direction
violation in the earlier combined reader: Infrastructure consumes another
module's lifecycle contract. Orchestration moves to Identity Application behind
its own confined session factory; SQL and physical ownership stay in
Infrastructure. The exact SQL bytes are unchanged, and no dependency rule is
relaxed. The related factory/session/caller controls follow the actual split.
A new descriptor-shape detector's
initial field list omits its two readonly auto-property backing fields; the exact
compiled five-field shape is corrected without a production exemption. The
actual generated coordinator lambda is normalized to its exact owner family,
preserving call multisets. The typed directory seek method joins the existing
exact read-method census; its MembershipId remains typed, with a removed-entry
control. This is not another aggregate writer exception. The organization SQL
census verifies the private shared literal at both real executed argument sites.

HTTP traversal distinguishes only the two declared event-initializer type
arguments and their two existing Off catalogue registrations as data metadata.
Actual factory/builder bodies must be data-only; fields, constructors, locals,
handler calls, delegates and extra registrations retain dirty controls. Endpoint
composition counts the three fixed infrastructure metadata attachments in
addition to the four existing mappings, with exact object-array/getter/receiver
provenance. No general metadata, source-type or assembly exemption follows.

The correction population passes 122/126; a subsequent seven-case run passes all
remaining failures and adjacent metadata/ownership controls. Every original
failure now has current green evidence. The unaffected cases are not represented
as another complete rerun. After the layer split, all 47 reader/directory/MUST/
pool cases pass again, and the complete current unit population passes
2,894/2,894 again. Compiled architecture method count is 313; its self-count guard
passes. These local runs are not hosted CI/shard qualification, which remains
Step 6.

**Review state.** Step 3 implementation and relevant validation are ready for its
commit and two fresh independent review rounds. The broader live-policy/evidence
obligations remain Registered until their remaining subjects and launch paths
are qualified; no Step 3 closeout or live activation is claimed here.

The final lifetime check makes invocation-frame closure atomic across inherited
execution contexts. All 13 coordinator unit cases and the compiled ownership
rule pass after that change; the latter retains its corrupted closure controls.

### P03-2 Step 3 review round 1 and corrections (2026-10-27)

**Reviewed source: `a15716d3`, against `cf9454e5`.** Three fresh independent
GPT-6 Astra reviewers at xhigh effort inspect policy/trusted sources,
transaction/EF/RLS scope and directory/evaluator/HTTP metadata separately.
They make no edits or test runs; author receipts remain author evidence.
The policy/source reviewer finds no actionable defect. Two findings are verified
against the current implementation and corrected:

- The directory's new deferred audit COMMIT test expects the ordinary primary
  PostgreSQL exception, which maps to 500. ADR-0077 requires 503 and no disclosure
  for this sensitive read. The physical completion catch now retains the actual
  transaction outcome and original cause, then reports `audit_unavailable` only
  for the matching active tenant-wide read witness with exclusively MUST
  ReadSensitive intents and no domain changes/produced work. Cancellation and
  ordinary mutation/unknown-business-COMMIT paths retain their original behavior.
- The interceptor XML/error text and Database standard still describe its role
  as diagnostic only. They now state its live-witness protection for retained EF
  commands while SQL scope remains announced until cleanup. No runtime guard is
  removed or widened.

**Validation.** All four real PostgreSQL directory pipeline cases pass, including
the mapped-503/no-result/cause check and a new ordinary-owner counterexample
with the same sensitive-read intent but no policy witness: its original exception
and Indeterminate cause remain intact. All 87 transaction/audit/coordinator unit
cases pass, including ordinary COMMIT and cancellation preservation. The compiled
operation-ownership rule and both lifecycle census/mutation cases pass. The first
architecture filter matches only the ownership rule; the two lifecycle cases are
then selected by their actual compiled names and run separately. No broader
rerun or second-round approval is implied.

### P03-2 Step 3 review round 2 and closeout (2026-10-27)

**Reviewed source: `489746d8`, against `cf9454e5`.** Three new independent
GPT-6 Astra reviewers at xhigh effort review execution/trust, Identity/directory
and HTTP/compiled-proof/documentation slices, including the first-round fixes.
All three approve within those scopes with no actionable verified findings.
They make no edits, run no tests/Docker and inspect no preexisting worktrees;
the execution reviewer independently runs a clean diff check. Supplied runtime
receipts remain author evidence rather than independently repeated execution.

**Closeout.** Step 3 and both review rounds are complete. The policy catalogue,
finite source adapters, fresh evaluator, operation-bound SQL/EF execution and
internal audited directory are implemented in the staged proof composition.
The broader live-policy/evidence rules remain Registered; production launch
retains the previous shell/deny-all behavior pending Steps 4/5. Step 4 proceeds
under the approved plan with central fixed-occurrence evidence, its pre-success
barrier, trusted provenance and separate explicit Role grant acts. No new
maintainer decision or HTTP activation is implied by this closeout.

### P03-2 Step 4 — Trusted evidence and finite grant acts (2026-10-27)

**Implementation.** The common `AuthorizationEvidenceCoordinator` owns a separate
fixed occurrence per refused policy act. It freezes the complete row and UTC
microsecond decision time, keeps real nested acts distinct and serializes re-entry
with the same decided reference. Ordinary capture's Committed flag acknowledges
none of these events. Successful ReadWrite completion flushes unresolved events
after domain draining and before final commit admission; physical ReadOnly and
ownerless success require a standalone acknowledgement. Event rollback/uncertainty
retains the same denied occurrence. Acknowledging a live transaction refuses, and
each pending write rechecks the original physical reference and generation after
I/O. The existing final event/job admission-to-COMMIT gap gains no await.

Audit, policy and actual transaction scopes retain the population through their
real enclosing owner. Failed/uncertain events reconcile before ordinary capture
clears; a still-live prepared owner retains its events for its own barrier.
Already-refused callers preserve 403/404 on audit loss. Failed standalone attempts
are signaled by the MUST store and not retried by the same finally. No restart
ledger or universal recovery promise is introduced.

`AuthorizationEvidenceSource` owns two retained slots/connectors under the same
guarded application role and installed authority. One shared five-second budget
covers opening, role guard, setup, INSERT/COMMIT and sequential exact verification.
The failed INSERT transaction's actual disposal finishes before a fresh physical
ReadOnly verification transaction starts on that connector. Job accounting retains
the physical connection and actual session task across both concrete transactions;
cleanup faults retain source/job capacity and joined shutdown. The source publishes
no general datasource/SQL callback. The two confined `IAuditStore` methods reuse the
sole INSERT/binder; only the expected primary-key duplicate admits full null-safe
22-field/typed-JSONB comparison. Exact event evidence establishes no business result.

Typed immutable HTTP, finite seed/event/job and live decision provenance reaches
the existing columns and versioned authorization metadata. Actor absence in an
actual finite seed owner remains null, including unresolved provisioning's ordinary
resource frame; no SystemActor is invented. A resource frame binds one exact
invocation, so a direct inner act without its own frame cannot replace its parent;
a fresh post-lock recheck of that act can refresh its context. Membership/Role IDs
remain bounded/sorted and contextual. HTTP sources reuse the admitted visitor,
fall back to the physical peer, sanitize/bound user agent and accept only canonical
endpoint identity. Claim-only assertion observations freeze confirmed scope and
dimension without asserted UUIDs or a host-budget debit. The adapters are staged;
real resolver/HTTP activation remains Step 5.

`ChangeRoleGrantsCommand` and `AdoptRoleGrantManifestCommand` each own one Role,
expected version, current eligible admin/delegation ceiling and immutable kernel.
The complete strongest tenant lock mode is selected before waiting; fresh actor
and target checks precede mutation afterward. Orgless Tenant-to-Organization
same-key authoring is explicit; no evaluation fallback, new key, v2 manifest,
automatic seed upgrade or writer route follows. Contained grant changes retain
references and delete only actually removed tracked children under the existing
restrictive FK. Their actual common MUST rows carry the Role and its child diff.
The policy census is 49 requests: 41 finite, four public, one AccountSite and three
protected, with the same five assignable and three virtual definitions.

**Validation and corrections.** Strict Release production builds pass with zero
warnings/errors. The complete unit population passes 2,961/2,961 with zero skips;
subsequent affected API-source/central-owner controls pass 34/34, including physical
generation, precommit acknowledgement and actual admission/transport canaries.
Actual SeedComposition provenance controls preserve null actors without opening
a database or claiming the fixture intent became durable.

The first 80-case PostgreSQL/PgBouncer group passes 67/80. Three source assertions
incorrectly infer disposal from a null/new NpgsqlTransaction object; the corrected
controls observe distinct server transaction IDs, immutable announcement generations
and fresh physical ReadOnly state after an actual aborted transaction. Three grant
tests incorrectly reuse a poisoned/validated scoped owner; each act now uses a
fresh complete scope. The grant-diff assertion checks the actual RFC 6901-escaped
composite key and exact before/after values. The pool fixture now announces legal
orgless role writes for its uncommitted-authority dirty control and compares the
actual empty contributor metadata on denial. One uncertainty test exposes ordinary
capture clearing before event reconciliation; the owner now reconciles first.
All 30 correction cases pass. The final relevant group passes **84/84**, including
both actual precommit/postcommit SIGKILL controls, 22-field duplicate mismatches,
source/job cleanup faults, all 15 direct/PgBouncer pool cases and the directory's
physical MUST barrier. A separately staged test-only executable supplies the crash
boundary; no production test hook or existing job probe is changed.

The legitimate seed-rerun control now changes grants through the actual audited
command, then proves the fixed v1 runner refuses without repair or another audit
write; that case passes separately. The architecture population initially passes
28/29; the new detector's fallback-throw and reference-comparison expectations are
aligned with actual Release IL, retaining dirty controls. A transitive HTTP graph
then exposes the classifier's fixed controller-type metadata as a false handler
dependency. Only its exact data-only table tokens are distinguished, while real
controller acquisition/execution/delegates remain covered by planted twins.
All six current metadata/provenance cases pass. Adjacent store, catalogue count,
aggregate and lifecycle checks pass except an incomplete job detector subject list;
its two already-enumerated evidence subjects are added and the exact ownership/
escape test then passes, including its planted controls. No
complete architecture rerun or hosted qualification is claimed by these receipts.

**Review state.** Step 4's targeted implementation validation is complete. Its
implementation commit precedes two fresh independent review rounds. Broader
rules remain Registered and launch still retains the prior shell/
deny-all behavior; Step 5 owns joint production activation and its real HTTP matrix.

### P03-2 Step 4 review round 1 (2026-10-27)

**Reviewed source:** `c1a61dd4..6780597f`. Three fresh independent
`gpt-6-astra` reviewers at xhigh effort examine central barriers/provenance,
confined source/store ownership, and grant acts/architecture respectively. They
make no repository edits and run no builds. Their source review identifies two
Major findings and one subsequently verified Minor finding; the implementation
does not pass this round unchanged.

- **Major — grant audit type.** Both successful grant changes and explicit
  manifest adoption are MUST but incorrectly classified as `Update`. Standards
  19/18 require permission changes to be `SecurityEvent`. Correct the two
  catalogue entries, matrix and unit/real-database assertions. Role snapshots
  and contained diffs remain, and no retention mechanism is claimed.
- **Major — refusal observation precedes cleanup.** The outer authorization
  observer receives a handler's failed Result only after transaction rollback.
  A rollback exception prevents observation; cancellation during rollback closes
  the invocation before later observation. The transaction owner must record
  the exact current act's already-decided closed refusal before cleanup. It
  must not classify an arbitrary thrown exception, 503, validation or concurrency
  result as a denial. Fault/cancellation controls and invocation deduplication
  are required before closure.
- **Minor — exact duplicate lookup.** The source/store reviewer reproduces the
  nonnullable `(id,timestamp)` null-safe-predicate concern on an isolated pinned
  PostgreSQL 18.4 fixture with 200,000 synthetic rows, the four audit indexes and
  forced tenant/org RLS under `learnstack_app`. The complete comparison scans
  6,895 shared-hit blocks; equality for only the two nonnullable key fields uses
  the primary-key index and four blocks. Both comparisons match. This is a
  representative planner control, not the complete migrated application or a
  production latency/timeout claim. Preserve null-safe comparison for the other
  twenty fields, typed JSONB, scoped verification and the unchanged deadline.
  Both owned temporary database fixtures are removed.

The parent independently verifies the two source/control-flow findings and the
index predicates. Interpretation-sensitive observations about missing-resource
404s and contributing-role attribution do not establish an additional contract
violation and are not promoted into scope changes. The full architecture run is
also started against the committed implementation; its first failures identify
the exact new source/cleanup and Role-lock caller censuses that need extension.
Corrections, their validation receipts and a separate second review follow.

### P03-2 Step 4 review corrections (2026-10-27)

The parent accepts both Major findings and the representative PostgreSQL lookup
finding. `identity.role.change_grants` and `identity.role.adopt_manifest` now
classify as MUST `SecurityEvent`, preserving ordinary Role snapshots/contained
diffs. Only the two nonnullable duplicate-verification key predicates change to
equality; all twenty other persisted fields retain null-safe comparison and the
existing typed binder.

The central evidence owner gains an exact transaction-result observer. It
requires the actual current Application invocation, matching canonical request
type and matching actual operation type. The transaction coordinator calls it
inside the failed-result branch before rollback or any later fallible cleanup.
The existing closed refusal filter and occurrence map prevent exception-text
classification and duplication by the outer observer. Twenty-two new controls
use the actual common audit/policy/transaction graph with a faulting/cancelling
unit: all closed refused codes retain their fixed pre-cleanup event, clean
cleanup keeps one occurrence, unrelated operations and non-policy failures mint
none. This is a deterministic unit fault control, not a new physical rollback
fault-injection claim.

**Validation.** The rebuilt focused Unit population passes **55/55**
with zero skips. An isolated output layout first prevents all 54 selected
integration cases from locating the repository SQL fixture; no application
statement is tested by that failed setup. The correct ordinary working-tree
layout then passes **54/54**, including both grant audit types, complete duplicate
comparisons and actual precommit/postcommit process-kill barriers. No fixture
root rule is weakened to accommodate the incorrect test invocation.

The long full-architecture diagnostic run is cancelled after its known failures
while corrections are being made; it is not a successful full-suite receipt or
a frozen final-source qualification. Its 33 failing cases reduce to five exact
census groups: the evidence datasource constructor, its retained-owner cleanup
join, the new Role lock caller, the two new protected Role policies, and the
transport adapter's typed-identity acquisition/getters with their dirty twins.
Only exact subjects are added. The cleanup proof keeps its actual-completion
join/body/parameter controls; type/witness/raw-claim/opaque-export guards remain.
The existing provenance fact also checks the actual failed-result branch and
exact invocation/type gates before the first rollback, with planted bypasses.
The first rebuilt architecture target passes 31 cases and still rejects all 29
authentication-authority controls on one common clean baseline: Release IL
formats the User-Agent `StringValues` through a constrained `Object.ToString`.
The detector now distinguishes only the actual header's single-assignment local,
conditional value/null flow and immediate final string argument to the fixed
transport minter. It grants no general formatting or owner exemption. Planted
wrong receiver, missing header acquisition and actual identity/object/object-array
forwarding in that same approved method still fail. The rebuilt authentication
population then passes **29/29**, zero skips. The other 31 cases, including the
new pre-rollback/SQL predicate dirty controls, already pass; no production code
changes after their validation. The correction commit precedes a second fresh
review. Full final-source architecture qualification remains Step 6.

### P03-2 Step 4 review round 2 (2026-10-27)

**Reviewed source:** `c1a61dd4..16667e76`. Three new independent
`gpt-6-astra` reviewers at xhigh effort examine durability/provenance, confined
resource ownership, and grants/census respectively. None authored Step 4 or
reviewed it in the first round. They inspect the complete step and its fixes;
the resource and grant/census reviewers approve without an actionable finding.
The durability reviewer requests a correction for one further Major boundary.

During that review the parent rebuilds and runs the complete Unit project:
**2,983/2,983 pass**, zero skips. This covers the new pre-cleanup observer's
generic transaction path alongside existing audit, job, producer, public and
identity controls. The 55-case focused Unit, 54-case corrected real-PostgreSQL,
31 non-authentication architecture and 29 authentication-authority receipts above
remain the step's completed targeted controls. Full architecture/integration/
contract inventories and hosted A/B remain the later final-source qualification.

**Major — cancellation before result observation.** The first correction captures
a failure before rollback, but still asks the live `CurrentInvocation` for it.
If an admitted handler returns a closed refusal and its own token cancels before
the transaction continuation consumes that Result, the live invocation is null
and both observers drop it. In the meaningful nested case the outer token stays
live, an event-free joiner does not poison its parent, and an absorbed failure
can therefore leave no denial for the outer commit barrier. The parent verifies
this source path; the reviewer runs no fault reproduction.

This finding is accepted for correction. Retain a private observation of the
exact admitted invocation/context through consumption of its actual failed
Result, including cancellation before cleanup. Its lexical owner must reject
reuse after completion. This is audit attribution, not renewed permission to
execute, query or commit under cancelled authority: candidate/read-witness/
`CurrentInvocation` checks remain live. Closed-code filtering, exception
exclusion, post-lock attribution and one occurrence per actual invocation remain
required. A deterministic pre-observer cancellation control and an absorbed
inner-token/live-outer control precede Step 4 closeout.

### P03-2 Step 4 correction verification and closeout (2026-10-27)

**Correction after `16667e76`.** The deterministic new Unit control first fails
against unchanged production: a completed handler refusal followed by immediate
inner-token cancellation reaches cleanup with zero recorded decision-time reads.
The correction introduces an internal opaque audit observation captured before
the actual handler await. It binds the existing execution scope, concrete policy
owner, exact invocation, request reference and request type. Its one-use
consumption accepts only the actual closed failed Result, uses that invocation's
latest already-verified immutable context, and keeps occurrence deduplication.
Current invocation, candidate and read-witness authority retain their live
cancellation checks. The internal audit-frame check retains current top-frame,
request, source-disposal and closed-ancestor checks. No historical execution
projection, tenant setter or additional async-local stack is introduced.

The complete focused Unit population passes **33/33**, including cancellation
before observation, cleanup faults, ReadOnly/ownerless cases, latest post-lock
attribution, cancellation before admission, closed source/scope, foreign request/
scope, changed current invocation and consumed-result replay controls. The full
rebuilt Unit project then passes **2,994/2,994**, zero skips.

The current real-PostgreSQL store/grant/barrier group passes **57/57**. Three new
cases admit a real inner handler, form its refusal, cancel its independent token,
and prove the outer authority stays live. The ReadWrite owner atomically commits
its business row, two ordinary resource rows and one fixed denial without opening
the evidence lane. ReadOnly/ownerless cases cannot publish while the actual
standalone evidence COMMIT is held; they proceed only after acknowledgement.
The existing process-kill, uncertain-COMMIT and full duplicate-comparison controls
remain green. Test cleanup preserves xUnit's captured execution context.

The existing closed invocation/read-witness ownership and canonical eight-step
pipeline facts pass. The canonical evidence/provenance fact is rebuilt after the
new observation census and passes with all planted scope/type/request/result/
await/replay bypasses. Its first baseline shape assertion is corrected to the
actual CLR representation: the nested observation type is internal and sealed,
while its primary constructor is emitted Public. Exact construction remains
confined to the sole central capture site; a planted publicly exposed type,
nonconstructor reference write or other caller still fails. The indexed
twenty-two-field SQL and physical transaction/generation controls remain covered.

The R2 durability reviewer independently verifies only this finding's correction,
inspects the reproduced failure and 33-case green receipt, and closes the Major
without another finding. This is a narrow correction check, not a third full
review round. The other two R2 reviewers already approve their complete slices.
The parent verifies the current 57-case PostgreSQL and 2,994-case Unit receipts,
whole-tree documentation lint and diff hygiene. Both requested independent
review rounds and their verified corrections are complete for Step 4.

**Handoff.** Central evidence, confined resources, provenance adapters and finite
grant acts are implemented and verified in their explicit compositions. Launch
still retains its shell/deny-all registration until Step 5 jointly activates the
real HTTP/application boundary. Whole activation-dependent catalogue rules remain
Registered. Step 5 proceeds with the directory/controller, named/runtime policy,
live resolver/AccountSite admission, distinct claim-assertion source, OpenAPI/SDK
and real HTTP compatibility matrix. Step 6 owns complete final-source suites,
source-bound inventories, hosted A/B qualification and the reviewable PR.

### P03-2 Step 5 — Live HTTP and application activation (2026-10-27)

**Implementation.** Starting from Step 4 closeout `56f95547`, the API and Seeder
register the common frozen policy catalogue and actual canonical
`AuthorizationBehavior` at slot 5 of the unchanged eight-stage pipeline. The
staged replacement and pass-through invocation adapter are removed. Exact finite
seed, delivery and retained-job sources remain independent of SystemActor values.
Identity's human evaluator defers its reader until an actual protected act.

The API replaces the deny-all membership port with the real committed reader and
qualifies its independent four-owner authorization and two-owner evidence lanes
against the installed authority and actual business database name. Both auxiliary
configuration keys remain coupled; Development-only direct-PostgreSQL derivation
preserves independent sources. Startup rejects missing anchors, owner credentials,
partial configuration and a second database carrying the same authority. Source
failure diagnostics contain no provider inner exception. The Seeder owns one
qualified evidence source across its per-act providers. Selected runtime children
receive authority/mode/auxiliary keys without exporting credentials to restore,
build, web or manifest projection.

Runtime endpoint classification precedes dispatch, including AllowAnonymous
endpoints. Default/fallback authorization supplies no authenticated catch-all;
the two protected named requirements must match exact module policy metadata.
The sole added route is `GET /api/v1/identity/memberships`, with existing Tenant
administrative capability, six contextual fields, scope-bound UUID continuation,
strict query names and the common MUST ReadSensitive commit before disclosure.
AccountSite keeps its independent real HostScope and physical ReadOnly ceiling.
The routing-owned method-rejection endpoint preserves earlier 404/405 semantics;
lookalike application endpoints do not inherit that exception.

A confirmed ClaimAndMembership assertion mismatch reaches its distinct central
MUST event through a confined API port, without host allowance or asserted UUID
metadata. Earlier host-backed assertion admission and resolver refusal populations
retain their separate boundaries. Fresh HTTP and application decisions do not
reuse resolver membership as a grant.

**Actual integration correction.** The first real HTTP run passes 50 of 60 cases
and exposes nine directory failures caused by one shared integration defect:
binding lookup creates the scoped Identity context on a global transaction; the
later ordinary tenant owner must explicitly re-enroll that same context. The
contextual adapter now requires its clean, live, already-announced tenant owner,
matching dynamic tenant/org filters and the same physical connection before
attaching the current transaction. No GUC, setter, read-scope or filter authority
changes. The actual five-row directory matrix passes after this repair. Separate
PostgreSQL controls reject a foreign connection and pending tracked mutation
without retargeting, saving or clearing it. The remaining initial failure is an
incorrect test expectation that an unrelated routing miss carries directory
response policy; only that fixture assertion changes.

**Validation before review.** The rebuilt strict Release integration group passes
**67/67**, zero skips: actual HTTP rows 6/7/9/10/14, capability/noncoverage/store
refusals, matching-host revocation, public zero-identity lookup, strict pagination,
claim-assertion budget independence, MUST insert/COMMIT failure and held-COMMIT
non-disclosure, AccountSite/binding/seed compatibility, and the seven directory
adapter controls. The additional actual-host startup test passes **1/1** across
both auxiliary lanes' wrong role, missing anchor, same-authority/different-database
and partial-pair variants, followed by healthy recovery with no repair/allocation.

Served OpenAPI supplies the exact ten-operation union and normalized string
membership status. The snapshot refresh is an explicit local opt-in; CI refuses
that opt-in and leaves the snapshot unchanged. Normal Contract passes **5/5**;
SDK tests pass **103/103**, with typecheck, lint and native build. The SDK owns the
new generated contract without adding a native BFF method: AccountSite remains
the sole authenticated native operation. The six selected-runtime environment
controls pass. Current standards, module matrices, architecture and tooling skills
now describe this wiring; historical records and Accepted ADR bodies stay intact.

The 49 unique targeted Architecture cases pass on the same strict Release
source: 29 closed-authority controls and 20 endpoint/policy/source/evidence/EF
ownership cases. The 62-case Docker-free HTTP regression group passes, including
a matched test-only undeclared controller under the actual Program: anonymous
404, normalized signed 403, zero handler dispatch and no business source creation.
The exact middleware-order census includes classification between assertions and
authorization; endpoint and role-metadata dirty controls remain executable.
Workspace typecheck also passes for SDK, UI and web.
The full rebuilt Unit project passes **2,994/2,994**, zero skips.

The phase Status block owns packet state. Whole activation-dependent catalogue
obligations remain Registered until Step 6 completes final-source qualification.
Full backend/provider/native suites, source/image/shard inventories and fresh
hosted A/B qualification are not inferred from these focused receipts.

### P03-2 Step 5 review round 1 and corrections (2026-10-27)

Three new independent `gpt-6-astra` / `xhigh` reviewers inspect implementation
`02bce2a1` against base `56f95547`: HTTP/EF isolation (`p032_s5_r1_http`),
execution/startup/launch (`p032_s5_r1_execution`) and proofs/SDK/docs
(`p032_s5_r1_proofs`). All are read-only source reviews; none claims a test run.
The HTTP slice approves. The other slices request concrete corrections below;
the parent independently verifies each before changing it.

- **Selected API credentials.** The API launch environment drops both explicit
  auxiliary keys, unlike the new Seeder runtime helper. The correction preserves
  the selected pair byte-for-byte, refuses partial/empty/control-bearing input
  before spawning the API, and atomically selects a complete explicit process
  pair before file fallback. It never combines one process key with a file key
  or imports an unrelated ambient credential into the runtime helper. Demo's
  pre-existing selected-stack conflict refusal remains. Nineteen new controls
  include a real copied CLI/marker-child boundary: old source fails eighteen of
  them, corrected source passes all nineteen. The four-file frontend group passes
  **253/253**; web typecheck/native SDK compilation and targeted ESLint pass.
- **Common database execution.** Role, authority and database-name agreement alone
  cannot distinguish a restored same-name database on another cluster. API and
  Seeder startup now use the bounded live business-database witness documented
  once in Standard 05. Both used lanes must see the exact active transaction
  marker before qualification; runtime caps, roles, grants, schema and tenant
  setter counts stay unchanged. Cleanup remains awaited and joined, with final
  deadline checks after actual cleanup. This implements the Accepted common-
  database requirement without claiming backend-reservation/topology proof.
- **Real protocol controls.** The same-name/same-authority second-cluster test
  first proves that each clone is independently reachable/qualified and that a
  fully self-consistent clone host can start. Crossing either lane into it then
  refuses; original rows/anchor stay unchanged and the original host recovers.
  Direct and pinned PgBouncer positives catch an initial driver-PID assumption;
  qualification uses the actual SQL backend PID. A raw migration-source control
  catches a missing exact-role check in the new factory. Those three prototype
  cases initially yield one pass/two failures, then all pass after the correction.
  The complete qualification group passes **14/14**, zero skips, including actual
  marker cleanup, both lanes, disposal/cancellation/real five-second expiry,
  foreign database, no unbound-result upgrade and no proof retargeting.
- **Legacy component fixtures.** The parent's broad Docker-free run exposes probe
  controllers now correctly refused by production default deny; some old negative
  assertions could consequently pass at the wrong boundary. That run is stopped
  with known failures and supplies no completion receipt. Explicit component-only
  hosts now assemble the production routing/MVC/body/error/tenancy components
  these tests own, remove production controllers, and admit only their selected
  test controllers. They make no Program/authentication claim. Application
  dispatch retains canonical slot 5 and exact test-owned policy/provenance;
  existing no-database tests invent neither physical transactions nor durability.
  Actual Program/undeclared-controller/authorization proofs remain separate and
  unchanged. The affected offline group passes **164/164**; real app-role tenant
  isolation and HTTP outbox controls pass **9/9**. The full Docker-free integration
  population subsequently passes **322/322**, zero skips.
- **Closed contracts.** The production job manifest adds only the exact tenth
  membership-directory action; extra-route/dashboard controls remain. The existing
  OpenAPI catalogue rule now says ten operations/six GET contracts, retaining four
  public wrappers and the sole native AccountSite method.

The startup witness's closed factory/binder/root callers, guarded role check and
bound production overloads extend the existing independent-owner Architecture
fact. Planted extra calls/function pointers, removed gates and substitution of an
unbound overload all refuse. That fact and the updated shared-role-guard caller
fact pass **2/2**. The original execution reviewer independently checks the narrow
correction, actual fourteen-case receipt and launcher controls and closes both
findings without another issue; this is correction verification, not a third full
review round.

After corrections, the rebuilt full Unit project passes **2,994/2,994** and the
current real HTTP/directory/AccountSite/binding/seed selection passes **62/62**.
The current Contract suite passes **5/5**; documentation lint and diff hygiene
pass. These focused receipts complement, rather than replace, Step 6's complete
backend/provider/native populations, source/image/shard inventories and hosted
A/B qualification. R1 corrections are committed before the requested fresh R2.

### P03-2 Step 5 review round 2 and closeout (2026-10-27)

Three new independent `gpt-6-astra` / `xhigh` reviewers inspect current
`effc9c5a` over the complete Step 5 base `56f95547`, including the committed R1
corrections: resources/startup (`p032_s5_r2_resources`), HTTP/application boundaries
(`p032_s5_r2_boundaries`) and proofs/docs/tooling (`p032_s5_r2_proofs`). All three
approve their source slices with no verified actionable finding. Each explicitly
runs no build/test and makes no changes; supplied test receipts remain the
parent's evidence, not independent reruns.

Their checks cover the bounded live database witness and actual-cleanup lifetime,
production bound overloads, role/authority/source and job-resource ownership,
canonical default deny, exact HTTP/application metadata, candidate and read-scope
lifetimes, directory cursor/projection/pre-disclosure audit, distinct assertion
population and AccountSite/native ceilings. Proof review additionally verifies
real clone/PgBouncer counterfactuals, closed opcode/caller controls, explicit
component-versus-Program separation, atomic selected credentials and contract
closure. No third full review round is asserted.

**Step 5 is closed.** Both requested fresh review rounds and all independently
verified R1 corrections are committed. Current focused/whole-offline validation
is recorded immediately above; this source review claims no additional runtime
qualification. Step 6 proceeds automatically with complete backend populations,
actual Keycloak/native compatibility, source-bound fixture/image/shard inventories
and fresh hosted A/B evidence, followed by the reviewable development-to-main PR.
Whole activation-dependent catalogue obligations remain Registered until that
qualification completes; no unrun provider, browser or production topology is
represented as delivered.

### P03-2 Step 6 — Complete populations and qualification (2026-10-27)

Step 6 adds actual-provider directory coverage for all four seed personas and
extends the real native learner journey through a committed Membership revocation.
The API refusal, local session invalidation, no-store response, absence of protected
rendering and retained provider SSO are independently observed. The selected real
provider/native/reader group passes **36/36**, zero skips. A separate 32-read warm
app-role candidate-owner sample reports p50 **4.3778 ms**, p95 **11.4101 ms**, maximum
**12.5116 ms**. This local reader sample excludes setup/provider/HTTP costs and
establishes neither request p95 nor production capacity.

The fixture census grows from 131 to **144** actual shared-fixture classes. CI
preparation includes pinned PgBouncer `v1.26.0-p0` beside the existing PostgreSQL
and Keycloak images. The 110-second preparation budget, per-image bound, retained
cleanup reserve, VM cap and ordinary/native job windows do not change. Fixture
controls pass **40/40**, native-owner controls **89/89**, and runtime Seeder
environment controls **6/6**. The non-test evidence process remains explicitly
staged without joining the native-test class list.

The first full Architecture diagnostic completes **588** cases: **575 pass and
13 fail**. Those failures identify three shared causes: auxiliary credential reads
outside the existing composition owner, a stale closed controller set, and a stale
qualification-overload caller census. It is a failed diagnostic, not qualification
or a passing timing source. Reads move into the existing persistence composition
file with frozen configuration captures; the two-file credential boundary remains
closed. Exact controller and guarded overload/proof callers gain corresponding
removed/extra/delegated controls. The corrected focused Architecture group passes
**4/4**. The complete Release solution builds with **zero warnings/errors**.

Registry closeout also exposes a missing production denied-test cross-reference.
Five new real app-role cases prove each assignable pair's legal delegation target
and refused target, unchanged refused state and committed denial evidence. Role
administration remains confined to Tenant Admin with its kernel intact; no new
authority is introduced. All **5/5** pass. The canonical Architecture map checks
these exact cases, virtual-public and finite-owner controls, and removed
pairs/methods/assertions. Existing policy and durability proofs take their canonical
names. This adds one Architecture method/case and five Integration cases; actual
rediscovery, complete-suite receipts and measured planner refresh remain required.

Guarded frontend execution passes **2,432** cases across both tested packages,
zero skips/todos; lint, typecheck and production build pass. Six initial lifecycle
failures identify a stale test allowlist for the runtime Seeder's installed
authority and explicit Development mode. Its exact argument-free application-seed
boundary now checks those selected values; restore/build environments stay confined.

**Qualification remains open.** The initial discovery and receipts are local
working-tree observations, not hosted evidence or an exact committed-head claim.
Complete rebuilt populations, updated advisory weights and both fresh hosted
assignments are still required. Both Step 6 review rounds precede closeout; no
whole-obligation catalogue promotion or packet completion is claimed here.

### P03-2 Step 6 review round 1 and full-suite fixture diagnosis (2026-10-27)

The implementation is committed as `2e00aba9`. Three fresh independent
`gpt-6-astra` / `xhigh` reviewers inspect `070aa676..2e00aba9`: ownership/callers
(`p032_s6_r1_owners`), provider/native and denied-proof quality
(`p032_s6_r1_proofs`), and CI/inventory/documentation
(`p032_s6_r1_inventory`). Each approves the assigned source slice without a
verified actionable finding. They edit nothing and run no build/test; the
inventory reviewer additionally uses read-only Python census and diff checks.
The unrefreshed timing data and pending complete-suite/hosted qualification are
explicitly outside those approvals. Updated plan data receives separate narrow
verification before the fresh second review round.

At this clean committed source the Release build has zero warnings/errors;
Unit **2,994/2,994**, Contract **5/5**, Docker-free Integration **322/322**, and
complete Architecture **589/589** pass with zero skips. The Architecture run takes
**29.0419 minutes** locally. Source/tree and all four test-assembly hashes are
captured before execution and verified unchanged afterward. This is local
implementation-source evidence, not a hosted job-fit claim.

The following ordinary Docker diagnostic is deliberately stopped after **858
reported passes and 314 reported failures**; it does not complete the population,
and neither its prefix nor its exit status supplies timing/qualification data.
The failures repeatedly hit stale test composition: dynamic job requests have
no explicit operation policy, and human audit-workflow actors no longer qualify
for production seed provenance. The isolated four-process test group receives
SIGINT and exits completely. The prior complete Architecture receipt remains
valid for its recorded source; the ordinary population still needs a complete
passing rerun.

Read-only investigation also identifies component negatives that can pass at the
wrong early boundary. The correction therefore inventories the component roots,
including module writer/query, producer, submission and worker fixtures. It
retains real pipeline/audit/transaction/SQL, explicitly declares only each
fixture's selected requests, and separates these mechanism proofs from actual
Program/live-authorization proofs. No production seed permission, API allowlist,
role grant, job authority or startup qualifier is relaxed. Existing seed-only
organization administration remains refused from a job; fixture-owned aggregate
operations exercise the real store/audit mechanism separately. Actor/context
lifetime, intended handler/SQL/error boundaries and actual worker ownership are
part of correction verification. Both the correction and complete ordinary
qualification remain open at this record.

### P03-2 Step 6 R1 corrections and regression verification (2026-10-27)

The complete fixture audit produces explicit component admission for only the
named mechanism requests. The shared helper preserves every unlisted production
policy/definition, freezes the supplied list, binds scoped provenance at first
capture to the exact context and authority fields, and revokes it on scope
change/disposal. It retains all canonical behaviors and real persistence/audit
registrations. A new Docker-free Fact checks these boundaries and actual unit/store
construction without claiming a database or HTTP authorization proof. Actual
Program/live-authorization and undeclared-HTTP controls remain separate.

Audit and producer fixtures no longer give a human actor production seed
provenance. Legitimate seed mechanisms retain the real Seeder. Module/query
fixtures opt into exact test requests, preserving handler entry, physical READ
ONLY, SQLSTATE, post-save/rollback and actual worker witnesses. Their 403/404 cases
use explicitly caller-owned, real app-role evidence sources with installed matching
authority and awaited disposal. Business Off remains separate from the central
MUST denial row. The AccountSite app-role readback initially misses branch rows;
the independent proof reviewer identifies its missing organization announcement.
Exact organization GUC/predicate and matching context call sites close that finding.
The corrected core group passes **48/48**, zero skips.

Job fixtures classify only their actual dynamic requests and retain production
job provenance. Separate fixture commands exercise the real Organization/store/
MUST audit; a real seed-only production organization command remains explicitly
refused from a job. Stale-authority negatives now witness the actual preflight
SQL. The broad affected run completes **844** cases, **814 passed / 30 failed**;
it is a diagnostic, not qualification or a timing source. Its remaining failures
identify cross-Application fixture ownership, mandatory Identity DI registration,
new audit UUIDs sharing a test counter, overinstalled component provisioning
provenance, and empty evidence completion.

The queued fixture corrections preserve the original strict checks: the combined
source test uses the owning fixture's exact nested type; bare global/lifetime
roots register the real Identity authorizer graph; delivery provenance hosts have
no setup-only provisioning source; recipient negatives match the issued UUID to
an independently readable committed denial and retain zero child state.

**Production correction.** An empty or fully acknowledged authorization-evidence
set owes no further I/O. The previous empty completion still demanded a tenant
announcement, and a post-COMMIT success could wait on an already-cancelled
standalone semaphore. This falsely marked acknowledged SDK work as cleanup
failure. The bounded guard retains owner/depth/active/rollback and composition
checks, then skips only the empty/fully-acknowledged path. Pending, failed,
rolled-back and uncertain evidence still requires the existing acknowledgement,
announcement, generation and cancellation checks. No decision/grant or durability
floor changes. Four new unit cases and the existing exact-COMMIT control cover
empty, acknowledged, pending/cancelled and missing-announcement outcomes.

The evidence unit group passes **21/21**. The actual source-contention, positive
COMMIT/cancellation, recipient/no-child, lifecycle, delivery and outbox regression
group passes **43/43**, zero skips. The strict Release solution builds with zero
warnings/errors. Existing source/corpus/permission checks additionally pass
**10/10** before the production guard; refreshed relevant checks follow below.

The original ownership reviewer independently verifies the bounded helper/job
correction and then the production empty-evidence and final fixture corrections.
The proof reviewer verifies module/evidence ownership, requests the exact-org
readback correction, and closes it after re-reading the actual diff. Both edit
nothing and run no build/test. These are R1 correction checks, not additional full
review rounds. Complete passing ordinary partitions, current rediscovery/weights,
fresh R2 and hosted A/B remain required before Step 6 closes.

After the correction freeze, the complete Unit population passes **2,998/2,998**,
Docker-free Integration **323/323**, and Contract **5/5**. Refreshed Identity audit,
operation-policy, database-fixture and corpus Architecture controls pass **16/16**.
All four raw TRX receipts report zero failures/skips. Documentation/index checks
pass. These local receipts do not replace the remaining ordinary partitions or
final-source hosted A/B qualification.

### P03-2 Step 6 review round 2 and final source corrections (2026-10-27)

Three fresh independent reviewers, `p032_s6_r2_ownership`,
`p032_s6_r2_proofs` and `p032_s6_r2_inventory` (`gpt-6-astra`, `xhigh`), inspect
`070aa676..309e6d25` across ownership/liveness/audit, actual proof boundaries and
CI/inventory/corpus. Their read-only source reviews approve their slices;
qualification remains separate. A later narrow carrier verification checks
actual canonical owners, preserved anchors and the seven proposed promotions.
No reviewer builds, edits or substitutes source inspection for runtime evidence.

**Clean predecessor receipts.** At `309e6d25`, the complete Architecture population
passes **589/589**, zero skips, and the affected ordinary group passes **844/844**
(including one Docker-free helper). Independent before/after source/tree and four
DLL observations agree. Actual post-completion Architecture process inspection
finds no surviving testhost. Complete no-build discovery then finds **6,922**
cases: 3,915 non-Docker and 3,007 Docker (2,976 ordinary / 31 native). Both A/B
layouts independently rediscover all 20 owners and exact unions; all 64 discovery
commands agree with their raw diagnostic/console evidence. These remain
predecessor-source observations after the correction below.

The disjoint complementary run completes **2,133** cases: **2,108 passed / 25
failed**, zero skips. It is a failed diagnostic, never a passing timing source.
All failures resolve to three verified causes:

- A delivery-only worker fixture removes the old Identity startup owner but
  retains the new authorization-source startup owner without an Identity anchor.
  The exact removal list now includes both. Its existing registration proof
  requires their absence and exactly one real DeliveryProcessor; the actual
  worker, app-role SQL, source, audit, trace and lifetime checks remain intact.
  A complete scan of the five old-startup removal sites identifies this sole
  missing companion.
- The direct Seeder process Fact supplies only Default, omitting the installed
  authority and approved runtime settings. It now installs the fixture-owned
  anchor through the actual deployment installer, supplies the same authority/
  issuer and Development mode, and clears inherited auxiliary endpoints so the
  two lanes derive from its owned direct PostgreSQL Default. Credentials remain
  environment-only. Repeat/no-write and mismatch/snapshot assertions remain.
  The external-process census identifies this sole stale writing Seeder child;
  the runtime wrappers already supply the approved environment.
- The new public HTTP classifier changes unresolved platform public reads from
  the generic `tenant_mismatch` to `not_found`. ADR-0064 and Standard 04 explicitly
  preserve that generic result. The narrow correction restores it before any
  authorization owner or dispatch; resolved contexts lacking public HostScope
  still receive `not_found`, and unknown hosts still refuse in the resolver.
  Existing real GET/HEAD cases remain unchanged. A new Docker-free Fact scans all
  eight actual public controller endpoints and checks the generic code/no-store
  with zero authorization-owner resolution or dispatch.

The ownership reviewer independently approves the delivery correction and actual
public classifier/control; the proof reviewer approves the Seeder correction.
These are R2 correction verifications, not fresh full review rounds. The first
37-case correction group passes all 36 production regressions; its new inventory
Fact initially includes three infrastructure endpoints. The corrected scan names
only actual public controller endpoints, preserving the separate infrastructure
census. The complete corrected Docker-free Integration suite passes **324/324**;
Unit **2,998/2,998** and relevant Architecture **18/18** also pass, zero skips.
Release builds with zero warnings/errors.

Active carriers remove stale pass-through/no-carrier/unregistered-runtime claims,
retaining historical ADR/delivery records, existing fragment anchors and phase
qualification ownership. Catalogue promotions and packet/gate completion remain
pending. The canonical proof-owner correction replaces three nonexistent-class
references only when its separately reviewed catalogue patch is applied.

**Timing and final qualification.** ADR-0063 permits each advisory timing source's
own source/run/artifact identity. Passing predecessor Architecture/broad receipts
and a later corrected complement may supply separate observations covering
current selectors; matching IDs do not prove unchanged implementation or a
same-source local whole-suite run. Every input retains its own clean source/tree/
DLL observers, raw all-Passed TRX hash and complete selector mapping. Failed
complement evidence is excluded. Fresh same-final-source/plan hosted A/B remains
the final qualification; no mixed-source local qualification is claimed.

### P03-2 Step 6 measured-plan review and qualification freeze (2026-10-27)

At corrected commit `f3b8d764`, the full complementary ordinary group passes
**2,133/2,133**, zero failures/skips, with unchanged clean source/tree and four
DLL hashes across the run. Contract additionally passes **5/5**. Actual rebuilt
discovery finds **6,923** cases: Unit 2,998, Integration 3,331 (324 offline /
2,976 ordinary / 31 native), Architecture 589 and Contract 5. The base owner has
3,327 cases; backend aggregate 3,916 and integration aggregate 3,007. All 64
preweight list/diagnostic commands agree with their raw console/ID evidence.

Independent R1 ownership and R2 inventory reviewers recompute all three passing
raw TRX hashes/counters, each source/tree/four-DLL binding, own/current case names
and IDs, decimal duration sums, ceiling milliseconds and historical floors.
The ordinary timing observations cover **843 + 2,133 = 2,976** distinct current
cases; exactly one offline helper is excluded from the 844-case predecessor
receipt. The private validator initially includes similarly named Unit classes
when predicting a command that actually executes only Integration. Its exact
Integration-assembly restriction corrects the prediction without weakening raw
receipt, selector or union checks. No failed diagnostic supplies timing data.

The reviewed candidate adds three independently attributed local sources:
**98 sources / 1,717 weights**, **122 additions / 62 increases**, zero unknown
weighted selectors, and the existing **144** shared-fixture classes. Every other
static plan setting remains unchanged. Both reviewers approve the exact candidate
and independently reproduce its assignment differences and genuine A/B grouping.
The written plan matches the reviewed preview bytes exactly. Its digest is
`2cfb260303db173aa3c7f29139e570cf558f3359f629f13860161251ad2d6573`.
Architecture/broad observations remain attributed to predecessor `309e6d25`;
complement observations remain attributed to `f3b8d764`. Their complete four-path
code delta is retained. These are advisory observations covering current
selectors, not unchanged implementation or a mixed-source local qualification.

A separate fresh output directory preserves all original binding artifacts while
actual weighted discovery reruns all **64** no-build commands. A/B each
independently rediscover all **20 owners**, exact filters and complete unions,
with zero unknown weights. The unchanged population digest is
`5abdc7f3f7d46c18ccd78ec856f2e3f1a9bc0b46e371cc7b9026c34b07e6a1ba`.
Maximum filter sizes are A **18,842** / B **28,198** bytes (limit 32,768);
maximum exact command argument sizes **19,193 / 28,550** bytes (limit 65,536).
The observer honestly records `f3b8d764` plus only the reviewed plan diff; source
and DLLs remain stable. It makes no clean-commit or hosted-fit claim.

Weighted planner controls pass **83/83**, result/fixture controls **40/40**,
workflow topology is complete, and native-owner controls pass **89/89** before
the weight-only change. Review/source work is frozen for fresh full hosted A/B
qualification. Catalogue promotions, final gate/packet closeout and PR handoff
remain pending that actual evidence.

### P03-2 Step 6 failed qualification and owned cleanup correction (2026-10-28)

Full hosted A attempt [40577851910/1](https://github.com/HodeTech/LearnStack/actions/runs/40577851910)
at source `696671b5`, tree `33c27d67`, completes **25/27** jobs. Its twenty
producer archives select **6,923** distinct cases; **6,737** report Passed,
zero report Failed, and **186** have no result. Only ordinary-8 is incomplete:
**44/230** Passed, then the owner exhausts its existing deadline. Its receipt
honestly records `complete=false`, `forcedCleanup=true` and `dockerAbsent=false`.
The integration aggregate consequently fails. This is failed qualification;
no B attempt or catalogue promotion follows from it.

All **24** actual artifacts are retained with API identity, exact byte sizes,
SHA-256 and ZIP CRC checks. The last completed provider cases do not identify
the stalled operation: all four selected shared-provider cases complete,
whereas SharedSchema completes only **15/181**. The old launcher retains
completion events but no active-case diagnostic, so neither a particular test
nor provider cleanup is established as the hosted root cause. The hosted SDK is
**10.0.403**, selected by the existing `latestFeature` roll-forward policy;
it is not the workstation's SDK or an exact 10.0.112 execution claim.

Independent source inspection verifies a narrower cleanup defect in
`AuthorizationReadSourceTests`: a cancellation/assertion race can return an
owner that its test finally never disposes. Source shutdown correctly waits for
actual disposal, so this test failure can hide behind an indefinite join.
The repair always joins the pending open and disposes a returned owner, even
when the blocker task faults; the warm owner also has unwind coverage. Production
ownership rules and the original guard/cancellation/recovery assertions remain
unchanged. The original isolated role-guard test passes **1/1**. A temporary,
restored source overlay forces both successful-open/assertion-failure and
independent blocker failure: **2/2** deliberate failures complete promptly,
without a timeout. The normal source and DLL are rebuilt afterward.

The launcher additionally retains optional, bounded start observations in project
measurements. It accumulates validated start deltas and removes actual results;
only selected assembly/ID/name values are exported. Missing/malformed start data
remains unknown and supplies no completion, ownership or qualification evidence.
Collection fixture initialization/disposal remains outside this diagnostic.
Five new controls cover concurrency, late starts, privacy and deadline retention.
Native-owner controls pass **94/94**, planner **83/83** and result/fixture
controls **40/40**. An independent reviewer approves the complete correction.
A local run of the exact 230-case A selection and fresh full final-source hosted
A/B remain required; the failed attempt is preserved rather than relabelled.

The corrected local run of the exact failed owner's **230** selected cases now
passes **230/230**, zero failures/skips, including the role-guard cancellation
case. Its controlled fault twins and the clean rerun demonstrate repaired test
unwinding; they still do not establish the missing hosted active-case boundary.
Topology and documentation checks pass. The correction is frozen for a new full
hosted A/B pair, retaining the same plan, assignment counts and job ceilings.

### P03-2 Step 6 Architecture timing correction (2026-10-28)

The corrected source `84abac5e` completes full hosted A attempt
[40583371696/1](https://github.com/HodeTech/LearnStack/actions/runs/40583371696)
with **25/27** successful jobs. All ordinary producers, native, frontend, public
demo and the integration aggregate pass. Architecture-5 and the backend aggregate
fail. The twenty producers select **6,923** distinct cases; **6,895** report
Passed, zero Failed, and **28** lack results. All **24** actual artifacts are
preserved with API identity, exact size, SHA-256 and ZIP CRC verification. B does
not start. This attempt supplies no whole-workflow qualification.

Architecture-5 completes **147/175** cases before `deadline_exhausted`. The new
optional diagnostic identifies recipient-census and recurrence mutation cases
without completed results. Source inspection finds finite Cecil graph mutations;
the named `memory_readiness_wait` control rearranges IL and executes no runtime
wait. This does not prove an implementation hang. The same 175-case selection
previously passed in **740.791** test-process seconds, but the new cutoff is
**704.669**. Pre-owner setup grows **190.610 -> 248.559** seconds and discovery
**33.763 -> 43.368**, together consuming **67.554** extra seconds before tests.
The prior passing provenance control took **357.260** seconds against its stale
**126.011**-second advisory weight. The failed producer's partial durations are
diagnostic only; they supply no new weights.

Two independent reviewers verify the six complete, naturally cleaned Architecture
producers from prior run **40577851910/1**: exact disjoint **589 cases / 316 whole
selectors**, all Passed, with raw TRX, receipt and immutable artifact hashes.
That overall run failed elsewhere and remains failed qualification. Its passing
producer observations retain their actual source `696671b5`, run, artifact hash,
whole-selector scope and summed milliseconds. They do not claim current-source
wall-time bounds or unchanged implementation.

The reviewed plan corrects **110** Architecture weights upward, retaining every
historical scalar floor and every selector. It removes exactly **17** originally
zero-reference source records from the active provenance map; their complete
values remain preserved in Git and the evidence archive. Six actual TRX sources
are added, giving **87 sources / 1,717 weights** within the unchanged 100-source
bound. All non-Architecture weights and ordinary/base/native assignments remain
unchanged. Counts remain **six Architecture / twelve ordinary / twenty execution
VMs**; two Architecture threads, job ceilings, **203-second** cleanup reserve,
whole-method grouping, salts, argument bounds and fixture census remain unchanged.

The complete six-producer refresh is selected over a selective failed-shard-only
refresh. Reusing prior observed summed work, its maximum A projection changes
**1,346.572 -> 1,139.973** seconds; B changes **1,331.677 -> 1,379.367**. B's
projection is worse, so this is not a fit or guaranteed-headroom claim. Complete
measured weights are preferred to selecting a favorable subset; only new full
final-source A/B can qualify the revised plan. Its digest is
`c043f60588779307372be621e032dbc60e3437f525fab954136e15efb87027a0`.

The rebuilt Release solution passes with zero warnings/errors. Actual no-build
filter discovery repeats all **64** requests and independently reconciles both
**20-owner** assignments to the same **6,923-case** population, with zero unknown
weights and unchanged manifest digest. Source and all four DLL hashes remain
stable across discovery. Native-owner controls pass **94/94**, planner **83/83**,
and result/fixture controls **40/40**. The second independent review recomputes
raw provenance, rounding, historical floors and candidate differences before the
new commit. New hosted A/B remains the qualification gate.

### P03-2 Step 6 scoped census performance correction (2026-10-28)

Full A attempt [40587983457/1](https://github.com/HodeTech/LearnStack/actions/runs/40587983457)
at reviewed plan source `72d76f20` completes **23/27** jobs. All ordinary
producers and the integration aggregate pass. Architecture 0/2/4 and the backend
aggregate fail. Exact producer unions contain **6,923** cases: **6,920** Passed,
zero Failed, and **three** without results. All **24** actual artifacts retain
API identity, exact bytes, SHA-256 and ZIP CRC checks. B never starts; no
qualification or catalogue promotion follows from this run.

Each failed Architecture producer lacks one expensive Fact: authority census
(**79/80** complete), delivery credential mutation (**111/112**) and business-owner
census (**171/172**). Their execution windows are **720.243 / 727.732 / 730.834**
seconds. Matched completed-case durations grow **14.5% / 33.8% / 13.4%** against
the prior complete observations. The cgroup records substantial CPU usage; case
start times are unavailable, so this does not prove a hang or distinguish exact
late-starting from slow execution. No production or Architecture source changes
occur between those hosted attempts. Another weight-only rerun is not selected.

Independent source inspection and local sampled-thread traces identify repeated
Cecil graph scans and identity formatting in the shared credential detector.
Sampling is diagnostic thread time, not an isolated CPU percentage or hosted
fit measurement. A three-file test-only correction uses existing per-detector
method/field-name instruction buckets, retains full declaring-type checks, and
formats only selected business-owner identities through an evaluation-local,
reference-keyed dictionary. It adds no cross-mutation cache. Exact inventories,
opcodes, duplicate occurrences, attribute-linked owners, detached targets and
every clean/dirty/restored detector evaluation remain.

Release compilation passes with zero warnings/errors. The isolated authority
Fact passes before and after with the same actual case ID, zero failures/skips:
**226.070 -> 190.739** seconds (**15.6%** lower). The baseline is profiled; this
single local comparison is advisory and cannot prove the final Linux fit.
Other affected mutation/index controls and two fresh independent review rounds
remain open at this development commit. Whole-obligation final-source hosted
A/B remains required. The plan, six/twelve owner counts, two Architecture
threads, VM and time ceilings, cleanup reserve and all production code remain
unchanged by this correction.

### P03-2 census performance review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6-astra`, xhigh) inspect
`72d76f20..6d1a43bf` across detector semantics and proof/evidence ownership.
Both approve without verified findings. They check exact assembly/type identity,
async owner mapping, duplicate and opcode preservation, detached targets and
per-evaluation cache lifetime. The proof reviewer independently recomputes the
24 failed-run archive checks, exact three missing Facts, timing comparisons and
both local TRX outcomes. No review-agent build, test or source edit occurs.

The fresh Release solution build at `6d1a43bf` passes with zero warnings/errors.
The other affected delivery-credential/business-owner controls and the existing
raw/index/clean-dirty-restored cache control pass **3/3**, zero failures/skips.
Together with the separately recorded isolated authority pass, this validates
the focused correction; it is not a complete local or hosted qualification.
The next fresh review round and final-source full hosted A/B remain open.

### P03-2 census performance review round 2 and source freeze (2026-10-28)

Two new independent reviewers (`gpt-6-astra`, xhigh) inspect the complete scoped
performance correction through `c3adebe9`. Both approve without verified
findings. They independently check equivalent caller/field populations, exact
identity, attribute-linked owners, duplicate/opcode preservation, malformed and
detached targets, and fresh cache state for every clean/dirty/restored evaluation.
The proof reviewer recomputes all 24 failed-run archive hashes and exact case
unions, plus the focused before/after and **3/3** passing control receipts.
Neither reviewer edits files, builds or runs .NET tests.

Actual discovery at `c3adebe9` repeats all **64** no-build requests against the
unchanged focused-test binaries. Both **20-owner** assignments rediscover and
reconcile the complete **6,923-case** population, with zero unknown weights;
source and all four DLL hashes stay stable across discovery. The manifest remains
`5abdc7f3f7d46c18ccd78ec856f2e3f1a9bc0b46e371cc7b9026c34b07e6a1ba`
and the plan remains
`c043f60588779307372be621e032dbc60e3437f525fab954136e15efb87027a0`.
The only delta from the freshly built/tested `6d1a43bf` is the first review's
documentation receipt; this is selection validation, not hosted qualification.

Both fresh performance-review rounds and focused controls are closed. No
production code, test population, timing weight, owner count, thread setting,
job ceiling or cleanup reserve changes after the correction. Source work freezes
for new complete hosted A/B. Catalogue promotions, gate/packet closeout and PR
handoff still wait for that actual evidence.

### P03-2 authority proof decomposition and preservation (2026-10-28)

Full A attempt [40593558449/1](https://github.com/HodeTech/LearnStack/actions/runs/40593558449)
at source `33142a89` completes **25/27** jobs. Five Architecture producers,
all ordinary producers and the integration aggregate pass. Architecture-0 and
its backend aggregate fail. The complete producer union has **6,923** selected,
**6,922** Passed and exactly **one** without a result: the monolithic authority
mutation Fact. All **24** actual artifacts retain API/size/SHA-256/CRC checks.
No B starts. This remains failed qualification, with no inferred test start time
or claim that the standalone selector intrinsically cannot fit.

Two independent source/contract assessments select a cohesive test-source
refactor under ADR-0063's versioned population/plan rules. Five independently
meaningful Facts replace the one orchestrating Fact in the same nested class:
admission activation/live guards; routing store/holder escapes; generation-bound
issuance/binding; executor/framework capability dispatch; opaque provenance and
publication gates. Each owns a fresh census. One unchanged 21-capability factory
serves the two related proof families. Every newly discovered method remains a
whole selection unit; execution never slices a method's rows or mutation loop.
No production code, detector, concurrency, time ceiling or reserve changes.

A reproducible source-token ledger reconstructs all five moved blocks and the
complete old helper, preserving string/character literals, token order, callbacks,
undo actions, loop inventories and assertions. It finds the same **27** mutation
source sites, **21** capabilities and **two** explicit positive evaluations.
A separate temporary local instrumentation records the actual old/new runs:
**1/1 -> 5/5** passing Facts, zero failures/skips, with exactly equal multisets
of **374** mutation descriptions/detectors and **1,124** detector/result-count
evaluations. The source-token and runtime checks complement each other; repeated
descriptions alone would not prove the capability inventory. All temporary
instrumentation is restored byte-for-byte to the committed wrapper afterward.
Those diagnostic runs are not timing-weight sources or hosted qualification.

The catalogue's compiled Fact/Theory method count changes **316 -> 320**;
Implemented-rule counts and the canonical ordinary-job authority rule remain
unchanged. Historical logs and the retired selector's weight remain historical.
Clean uninstrumented whole-Fact timing receipts, actual full-population discovery,
a reviewed plan revision, fresh reviews and complete final-source hosted A/B
remain required before closeout.

### P03-2 decomposed authority review round 1 and measured plan (2026-10-28)

Two fresh independent reviewers (`gpt-6-astra`, xhigh) approve
`33142a89..23262e95` without verified findings. They independently reconstruct
all moved source blocks, the unchanged capability factory, runtime mutation/result
multisets and failed-attempt case evidence. Fresh census ownership and whole-method
selection remain explicit; diagnostic instrumentation never ships or supplies
weights.

A clean Release solution build at `23262e95` passes. With source/tree and all
four DLL hashes unchanged before/after, the uninstrumented run passes **7/7**:
five complete new Facts and both catalogue controls. Their actual whole-Fact
weights are **8,967 / 22,619 / 80,941 / 45,128 / 37,824 ms** (sum case duration,
round upward). The raw TRX SHA-256 is
`d944c6abf53848f6e9ae7f425e4397a712d885e4d91634cb2efae239976ca8bc`.
The proof reviewer independently joins actual TestMethod metadata to results,
checks both catalogue exclusions and the clean source/binary bindings, and
approves the candidate plan. No display-text parsing or division of a historical
combined duration produces these weights.

The revised plan adds one explicitly local timing source and five weights:
**88 sources / 1,722 weights**. All **1,717** prior rows and **87** prior sources,
including the retired selector's **603,500 ms**, remain unchanged. The new digest
is `f5161ae946c9e3268c48be8d365cb653a190f218bbb1492731a0c7097e8d0c0d`.
Every other plan setting is unchanged. These are advisory local observations,
not hosted wall-time bounds.

Actual discovery repeats all **64** requests, independently reconciles both
**20-owner** assignments, and finds exactly one removed/five added selectors.
All other case identities remain unchanged. The population is **6,927**:
Unit **2,998**, Integration **3,331** (offline **324**, ordinary **2,976**, native
**31**), Architecture **593**, Contract **5**. Backend has **3,920** cases;
integration has **3,007**. Both assignments have zero unknown weights. Maximum
filter sizes remain A **18,842** / B **28,198** bytes, below 32,768. The manifest
is `1fa6c70c812d63948a2e7adc0bc762907b4c3496130088f818061c56ba67964e`.
Comparison of old/new plans uses this same new population, not unlike historical
bins. Planner controls pass **83/83**, result/fixture **40/40**, native owner
**94/94**. The fresh second review round and complete final-source hosted A/B
remain open.

### P03-2 authority decomposition review round 2 and final source freeze (2026-10-28)

Two new independent reviewers (`gpt-6-astra`, xhigh) approve the complete
`33142a89..3ccdfc40` decomposition and measured-plan revision without verified
findings. They independently reconstruct all control bodies and the capability
factory; check fresh census/evaluation ownership, callbacks/undo, async and
detached-target semantics; recompute the **374 / 1,124** runtime multisets;
and join clean TRX method metadata to all five weights. They verify every prior
weight/source remains unchanged and distinguish instrumented diagnostics from
clean timing advice.

Both reviewers independently reconcile all **64** actual discovery protocols,
exactly one removed/five added selectors and unchanged remaining identities.
The final inventory is **6,927 cases / 320 Architecture methods**, with two
complete, unique **20-owner** layouts and zero unknown weights. The plan remains
`f5161ae946c9e3268c48be8d365cb653a190f218bbb1492731a0c7097e8d0c0d`;
the manifest remains
`1fa6c70c812d63948a2e7adc0bc762907b4c3496130088f818061c56ba67964e`.
No reviewer edits source, builds or executes .NET tests. No production code,
detector, execution owner/count, concurrency, job ceiling or reserve changes.

Both fresh decomposition review rounds are closed. Source work freezes for new
complete hosted A/B using this same source, toolchain, plan and population.
Catalogue implementation promotions, phase gate/packet closeout and PR handoff
remain open until actual successful qualification; historical failed attempts
remain preserved.

### P03-2 final-source A exhaustion and resolution profiling (2026-10-28)

Full A [40600994059/1](https://github.com/HodeTech/LearnStack/actions/runs/40600994059)
at `05a546348eac7f6d7104780fbd3cef875d219231`, tree
`fe4918f05f942c001a9d36be3af6402885f162d5`, completes **25/27** jobs.
Architecture-5 and its dependent backend aggregate fail; all other Architecture,
ordinary/native producers and the integration aggregate pass. The **20** original
producer archives reconcile **6,927** selected, **6,926** Passed and exactly
**one** missing result, with no recorded failed/skipped outcome. All **24**
actual archives retain original API metadata, size, SHA-256 and CRC verification.
Plan/manifest remain `f5161ae9` / `1fa6c70c`; no B starts and this is not
qualification.

Architecture-5 selects **107**, completes **106**, and reports
`deadline_exhausted`. The missing Fact is
`Resolution_walk_detects_loading_construction_async_and_polymorphic_helpers`,
case ID `a3347c4a-fea0-ac0a-ce6d-983c34ba3096`; the five decomposed authority
Facts complete. Pre-owner/discovery take **243.380 / 42.636 seconds** and owned
cgroup CPU consumption is **1,383.118 seconds**. Empty `measurements.projects`
provides no case start, complete test-process wall duration or remaining tail;
CPU consumption alone neither excludes blocking nor attributes that work.

An independent evidence assessment reconciles this and the three preceding
Architecture failures directly from all twenty producer ZIPs per attempt.
The missing Fact has prior complete observations of **321.649 / 330.988 /
364.338 / 348.666 seconds**; one belongs to an otherwise incomplete producer.
Those unchanged-body observations do not establish the latest start time or
future fit. A separate carrier assessment identifies the exact bounded policy
change that a longer Architecture window would require, including own-record
Amendments in ADR-0062/0063/0075. No such policy is accepted or implemented.

The evidence assessment instead identifies a concrete source candidate:
`ResolutionOffenders` repeats its complete subtype/`Derives` scan for every
reference to the same nonsealed helper. Before changing a time ceiling, measure
one evaluation-local expansion set, separate from dequeue visitation. Preserve
the direct helper walk, transitive subtype semantics, all **15** mutations and
**45** clean/dirty/restored evaluations. The existing three whole resolution
controls, source/assembly bindings and unchanged control bodies are the focused
verification boundary; local timings cannot establish hosted fit.

Both clean Release builds pass with zero warnings/errors on local SDK
**10.0.302**. The same **three** whole resolution controls pass before and after,
with identical case IDs, zero failures/skips and stable source/all four test
assembly hashes within each run. The mutation Fact changes from **122.297** to
**6.486 seconds** (approximately **94.7%** lower local case duration); focused
run wall changes **123.007 -> 7.179 seconds**. These are local advisory timings,
not hosted qualification or replacement plan weights. Raw TRX SHA-256 values are
`610f9162935249cb5a1138dd52062d939db9e6b83440e253757181d4cb771baf`
and `2051af1501b855c524ea72fcd7fd882d7169813932fc5d39c378998112ee9849`.

The tracked source differs only by one method-local expansion set and its
repeated-expansion guard. A byte-for-byte reconstruction verifies every other
byte of the file is unchanged, including all control bodies, callbacks/undo,
`Derives`, dynamic-loading checks and direct helper enqueues. The expansion set
is separate from dequeue visitation and is recreated for every clean, dirty and
restored detector call. No production code, plan weight, selector, population,
concurrency, deadline or reserve changes. Fresh independent review rounds and
new complete hosted A/B remain open; no Architecture-window ADR is introduced.

### P03-2 resolution correction review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6-astra`, xhigh) approve
`05a54634..c1f1d4c1` without verified findings. The semantic review proves the
separate expansion/visitation sets retain direct and transitive helper coverage
and fresh per-evaluation state. Both reviewers reconstruct every unchanged source
byte and independently check the raw three-case TRXs and stable binary bindings.
The evidence reviewer also reconciles all **24** failed-attempt archives, all
**20** producer unions and the four disclosed historical case durations.

Fresh actual discovery at `c1f1d4c1` completes all **64** protocols with unchanged
source/four DLLs, no untracked backend/script source and the same **6,927** cases.
Both A/B layouts have **20** unique owners and zero unknown weights. Plan
`f5161ae9` and manifest `1fa6c70c` remain unchanged; no weight, selector, policy
or qualification requirement changes. The second fresh review round and complete
final-source hosted A/B remain open.

### P03-2 resolution correction review round 2 and source freeze (2026-10-28)

Two new independent reviewers (`gpt-6-astra`, xhigh) approve
`05a54634..ad7cb5e8` without verified findings. They reconstruct the exact
source change, per-evaluation graph semantics and all unchanged control bodies;
independently check raw TRX hashes, identical three-case outcomes and current
source/four-DLL bindings; and retain local timing as advisory. The evidence
reviewer independently parses all **64** discovery protocols, reproduces both
assignments, reconciles all **24** hosted archives and confirms the sole missing
case and four historical observations.

Both fresh review rounds are closed. No further source or plan change follows:
**6,927** cases, **320** Architecture methods, **20** owners per layout, zero
unknown weights, plan `f5161ae9`, manifest `1fa6c70c`. Only the Architecture test
DLL changes across the intentional optimization. Production and all other test
binaries stay identical. The source freezes for new complete hosted A/B with the
existing job windows/reserve; catalogue/gate/packet closeout and PR handoff remain
open until successful qualification and independent evidence audit.

### P03-2 failed provider startup and bounded diagnostics (2026-10-28)

Full A [40605700294/1](https://github.com/HodeTech/LearnStack/actions/runs/40605700294)
at `82414331e7ceea875c5c426bddb9f0e075030700`, tree
`d7fa288a2f89a5c786d881afdd55438573868491`, completes **25/27** jobs.
All six Architecture producers and the backend aggregate pass. Ordinary-3 and
the integration aggregate fail. All **24** original archives retain API/size/
SHA-256/CRC verification; the **20** producer unions reconcile **6,927** results:
**6,921 Passed, six Failed, zero missing/skipped**. No B starts. This is failed
qualification, despite completed Architecture and the earlier local speedup.

Ordinary-3 completes all **237** assigned cases, **231 Passed / six Failed**.
Five closed startup records show `provider_start/readiness_timeout` and cleanup
`completed`; four share the realm collection fixture and one uses a separate
class fixture. Historical stock's sixth failure is only `invalid_state`: its
**121.342-second** case duration does not prove startup expiry. Two independent
read-only assessments verify these facts and identify its omission from the
finite diagnostic class list. The exact preceding ordinary-3 selection passes
**237/237** with matching SDK/console/adapter hashes; its **536.139-second**
test process and this attempt's **526.139** are not startup measurements.

The failed receipt's `tests_failed` workload status explains
`owned_completion_missing`. For failed Docker work, `forcedCleanup=true` is
unconditional and `dockerAbsent=false` is not a retained absence census. These
fields do not prove a resource leak. `cleanupOwnershipComplete=true` witnesses
joined process cleanup; fixture cleanup `completed` means its disposal calls
returned, not an independent Docker-absence proof. Startup overlap, Docker
resource pressure and the provider cause remain unobserved; the changed runner
image/kernel is not established as causal.

A bounded diagnostic correction includes the exact stock-baseline class,
provider `Created`/`Starting`/`Started` event milestones and startup cancellation
state captured at failure before cleanup. The closed parser accepts only the
finite labels and an optional literal boolean; legacy sentences leave it
unknown. Stages remain last-reached SDK observations, not exact failed operations.
No raw provider output, configuration, credentials, message or stack is exported;
case streams/TRX remain sanitized. No startup/job deadline, concurrency, fixture
sharing, selected case, assertion or successful ownership condition changes.

Local Release build passes with zero warnings/errors. The **95** native-owner
controls pass, including stock-class coverage, malformed/ambiguous/oversized
messages, both cancellation values and private-canary/case-output controls.
The real HTTPS provider/owned-CA/loopback smoke passes **1/1**, zero skips. These
verify the diagnostic correction, not the unknown hosted cause or qualification.
Fresh independent review rounds and new complete same-source hosted A/B remain
required; no policy increase or new ADR is introduced.

### P03-2 startup diagnostic review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6-astra` / `gpt-6.1-sol`, high) approve
`82414331..09ac7c85` without verified findings. They verify event-only stage
updates, cancellation capture before cleanup, exact finite class/vocabulary
admission, legacy omission and separation from completion authority. The
protocol reviewer independently passes **95/95** native-owner controls and
checks raw smoke TRX/hash, all **24** archive hashes/CRCs and the complete
**6,927**-case failure union. Documentation keeps the provider cause unknown.

The second fresh review round and complete final-source hosted A/B remain open.
No readiness, startup/job bound, concurrency, sharing or acceptance condition
changes; a diagnostic observation is not a lifecycle correction or hosted fit.

### P03-2 startup diagnostic review round 2 and source freeze (2026-10-28)

Two new independent reviewers (`gpt-6.1-sol` / `gpt-6-astra`, high) approve
`82414331..01d8234b` without verified findings. They confirm event-only milestones,
pre-cleanup cancellation capture, exact bounded projection and unchanged lifecycle/
completion authority. The protocol reviewer independently passes **95/95**
controls and reconciles raw smoke evidence, all **24** archived packages and
all **6,927** completed results. Both fresh diagnostic review rounds are closed.

The source freezes for one new full A; B follows only after successful A and
original-archive verification. If provider startup fails again, the new milestone
and cancellation observations guide diagnosis before a lifecycle correction is
selected. Timeout increases, fixture serialization and a provider root cause are
not inferred. Expected population remains **6,927** with the unchanged plan
`f5161ae9`; actual hosted discovery and both exact unions must re-establish it.
No packet/gate/catalogue promotion or PR handoff precedes successful complete
same-source A/B and independent evidence audit.

### P03-2 failed browser readiness and recovery settlement controls (2026-10-28)

Full A [40609744140/1](https://github.com/HodeTech/LearnStack/actions/runs/40609744140)
at `9385c9e8c59f2496e760e08d4f539b992ece8981`, tree
`e50ef647890fd27c531992f9a5a05c2454b0ceca`, finishes **23/27** jobs successfully.
All six Architecture producers and the backend aggregate pass. Public demo,
ordinary-4/9 and the integration aggregate fail. All **24** original packages
retain API/size/SHA-256/CRC verification. Twenty producer unions reconcile
**6,927** results: **6,925 Passed / two Failed**, zero missing/skipped. The
preceding provider-startup failures do not recur; their cause remains unknown.
No B starts and this remains failed qualification.

The demo receipt fails at `startup-port/port-invalid`, audit lane 2/catalog/run 1,
before the worker starts. Nine earlier audits, nine browser screenshots and
**62** HTTP-200 API completions survive; cleanup removes private profiles. The
original port bytes are unavailable. A read-only source/artifact assessment
finds an actual readiness gap: existence is followed by one numeric read, so an
existing empty file immediately refuses instead of using the remaining startup
allowance. Numeric prefixes without a completed first line and noncanonical
number forms can also be admitted. This is a verified control weakness, not a
recovered account of the original browser failure.

The bounded correction reuses the direct-read parser and waits for a newline-
complete decimal port in `1..65535`. Missing/empty/incomplete publication stays
within the original **10-second** startup and **12-minute** total bounds. Every
observation checks owner/cancellation/native child status, including before
publishing a ready port. Invalid/unreadable files, deadline and cancellation
retain closed diagnostics; no private endpoint bytes enter artifacts. The shared
admission/BFF proof parser also rejects incomplete numeric prefixes. No product
route, browser trust, API traffic allowance or worker assertion changes.

Twenty-six new readiness controls cover actual-file publication, canonical/range
checks, deadline, owner death, cancellation and private errors. Focused frontend
controls pass **110/110**, with typecheck/lint/format clean. The first full local
workspace run refuses an existing Python bytecode cache as unclassified source;
no census exemption or local-file deletion is used to hide it. Guarded full
frontend and real managed-demo proof move to a clean development copy.

The two backend failures are browser discovery and a native-recovery persisted-
phase assertion. Their closed evidence is retained independently. The recovery
source permits a conservative renewal refusal before Outcome acknowledgement;
the exact CI interleaving is not recovered. Deterministic positive settlement
scheduling is investigated separately, preserving real timers, all assertions
and existing lease/stage bounds. Fresh reviews and complete final-source A/B
remain required before any completion promotion.

### P03-2 browser readiness review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6-astra` / `gpt-6.1-sol`, high) approve
`9385c9e8..7dbf68f6` without verified findings. They verify actual coordinator
wiring, shared newline-complete parsing, before/after owner/cancellation/native
checks, existing deadlines, private diagnostics and all **26** new controls.
The controls reviewer independently repeats the guarded workspace run in the
clean development copy: **2,458 Passed / 73 files / two packages**, zero
skips/todos, unchanged source and clean tracked tree before/after. The raw log
SHA-256 is `9a3ada9d1f039de8e6911190eb385340428609ae9de2d09125da1b60311ce2a5`.

Uncommitted native-recovery work is explicitly outside this browser review.
The second fresh browser review, actual managed-demo proof, native scheduling
validation and complete final-source hosted A/B remain open.

### P03-2 browser readiness review round 2 (2026-10-28)

Two new independent reviewers (`gpt-6.1-sol` / `gpt-6-astra`, high) approve
`9385c9e8..8a43eb9d` without verified findings. They confirm complete-line parsing,
actual coordinator wiring, before/after lifetime checks, fixed startup/overall
allowances, closed errors and unchanged cleanup. The controls reviewer verifies
the independent **2,458**-case raw log and meaningful counterexamples. Both
browser review rounds are closed; the actual managed-demo run uses the clean
`7dbf68f6` development copy. Native scheduling and final-source A/B remain separate.

### P03-2 native recovery positive settlement scheduling (2026-10-28)

Source review of the ordinary-9 failure finds a permitted positive-oracle race:
Outcome holds the job row, a concurrent `SKIP LOCKED` renewal can acknowledge
an empty result, and conservative pre-ACK refusal can cancel that Outcome.
The artifact proves a renewal loss and a later persisted-phase assertion failure,
not this exact interleaving. Production refusal behavior is unchanged.

The test now allows real renewals through every original expired-window,
open-token and provider-count assertion, then holds the next actual pacing
callback after its preceding SQL flight completes. Construction releases only
after callback entry; Outcome settles while the same live lease remains held.
A distinct validated **1,125 ms** pacing cadence separates this test seam from
one-second waits. System clocks and all other timers stay real. Lease/SQL/probe/
progress remain **15/1/30/30 seconds**; hold plus settlement share the existing
**15 seconds**, and control/timer/factory cleanup share the existing **20**.
All original assertions remain; stopped execution is not used as a pacing tool.

Cleanup seals selection, releases every selected callback, joins the actual
factory/timer disposal and disposes its barrier only after confirmed control
completion. Release build passes without warnings/errors. Both recovery theory
rows plus publication controls pass **4/4**; unchanged Outcome/Prepare/Finalize
refusal controls pass **2/2**. An isolated development-copy dirty twin injects a
failure after callback entry: both expected failures retain their first fault and
emit confirmed control/generation/timer cleanup witnesses. Its source and all
four test DLL hashes restore exactly afterward. This intentionally failing
**two-case** counterfactual is never a passing test result or timing-weight input.

Native scheduling's fresh review rounds, actual browser proof and complete
same-final-source hosted A/B remain open. No production code, selected case,
startup/job ceiling, database guard or permitted refusal is relaxed.

### P03-2 recovery scheduling review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6-astra`, xhigh / `gpt-6.1-sol`, high)
approve `8a43eb9d..6ea5e67f` without verified findings. They verify previous-flight
selection, real clocks, original assertions/provider counts, shared existing
budgets and preserved conservative refusal. Lifetime review covers cancellation
before callback entry, synchronous disposal from the callback, factory/timer
join, selection closure, barrier disposal and first-failure preservation.

The proof reviewer independently parses **4/4** clean and **2/2** refusal passes,
both expected dirty failures and cleanup witnesses, and matches both committed
source files and all four restored overlay DLLs. No original CI interleaving or
hosted qualification is claimed. The second fresh review and complete final-
source A/B remain open; the actual managed browser proof continues separately.

### P03-2 recovery scheduling review round 2 (2026-10-28)

Two new independent reviewers (`gpt-6.1-sol`, high / `gpt-6-astra`, xhigh)
approve `8a43eb9d..16614b3e` without verified findings. They confirm selection
only after all original renewal/provider assertions and a completed SQL flight,
unchanged real clocks and deadlines, callback-entry acknowledgement and retained
timer disposal. Cancellation before entry, synchronous disposal inside the
callback, factory/control joins and first-failure preservation remain covered.

The proof reviewer independently reconciles **4/4** clean passes, **2/2** refusal
passes and the two expected dirty failures with joined cleanup witnesses. Both
source files and all four restored DLL hashes match their records. The dirty
counterfactual covers failure after callback entry; other lifetime paths receive
source review rather than a claimed dirty execution. Both fresh native review
rounds close without a production change or recovered historical CI cause.

### P03-2 clean local managed browser proof (2026-10-28)

The clean development copy at `7dbf68f689e44a3dcfe6202ca5533cc6fc093611`, tree
`388364446e3089ce1110b7acc582d5bc13e8030f`, completes `make demo DEMO_MODE=ci`
with exit zero and unchanged tracked source. Its original receipt SHA-256 is
`990b8b92af7f56c20643c2e0ffe89f71d070b6bf2409108c1ba13709da52a277`.
All **27** original JSON/HTML pairs match their receipt hashes. Nine browser
navigations, **24** visibility controls, two TLS refusals and the timeout
counterfactual pass. The exact completion union is **134 HTTP 200**, with no
429 or malformed/pending record. Proof duration is **566,466 ms**, within the
unchanged twelve-minute bound; the whole local command takes **637.557 seconds**.
Cleanup confirms stopped processes, empty Compose, removed private files,
environment and lock. All nine existing LCP advisories remain warnings, not
newly enforced performance claims.

This local browser proof uses macOS, Node `22.23.1`, Playwright `1.64.0`,
Lighthouse `13.5.0` and managed Chromium revision `1248`. It does not substitute
for final-source hosted A/B or qualify the separate native scheduling change.
The complete same-source hosted pair and independent original-evidence audit
remain mandatory before packet/gate/catalogue promotion or PR handoff.

### P03-2 browser and recovery correction source freeze (2026-10-28)

The actual native admission/browser-fallback regression passes **1/1**, with no
skip, after the clean managed demo releases its local build/test window. Both
fresh review rounds for browser readiness and native positive settlement are
closed. Release build, focused controls, clean full frontend and original local
demo evidence precede the new source freeze. The unchanged plan `f5161ae9` and
manifest `1fa6c70c` still select **6,927** backend cases; no case, timing weight,
deadline, production refusal or resource limit changes in these corrections.

One new complete hosted A follows this freeze; only its successful original-
archive verification permits a complete B rerun. The final pair must share
source, toolchain, plan and population. The prior failed attempts remain failed
evidence, and local controls do not substitute for this qualification. Packet,
gate and catalogue completion and PR handoff stay gated on the pair and its
independent original-evidence audit.

### P03-2 zero-adopted standard census correction (2026-10-28)

Final carrier preparation finds an actual corpus-control gap before promotion:
making Permissions Active gives **22 Active / zero Adopted**, but
`CorpusConsistencyTests` recognizes only number words one through twenty-two.
The existing status control therefore cannot validate the truthful final split.
The correction adds only `zero = 0` to that dictionary, without changing its
parser, assertions, population or refusal behavior. The pending six-file carrier
preview also updates the summary split and the obsolete Permissions sentence.

An isolated development copy applies the future permission documents. Before
the code correction, **4/5** corpus controls pass; exactly the status-header/index
control fails on `zero`. With the one-line correction, **5/5** pass. Applying the
pending catalogue preview as well passes **5/5**, including the **251** implemented
rules, **146** Architecture / **105** elsewhere and unchanged **320** method census.
Both Release builds report zero warnings/errors. These future-document controls
are not qualification, delivered status or timing-weight evidence.

Full A [40616215040/1](https://github.com/HodeTech/LearnStack/actions/runs/40616215040)
at `07a0431a` is deliberately cancelled during execution because this necessary
test-source correction invalidates its freeze. The **27**-job API record and all
**six** available original packages are retained; no complete population or fit
is claimed, no B starts, and cancellation is not classified as a new product
failure. Fresh review rounds precede one new complete same-source A/B freeze.

### P03-2 zero-standard census review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6.1-sol` / `gpt-6-luna`, medium) approve
`07a0431a..fb7a8928` without verified findings. They confirm the one-line code
delta, unchanged parser/assertions/population, both future status sentences and
the before/after/full-preview **4+1 / 5 / 5** raw TRX results and hashes. The
evidence reviewer independently validates all six cancelled-attempt original
ZIP sizes, hashes and CRCs and the complete **27**-job API record. No cancelled
attempt is promoted to fit or qualification. The second fresh review round and
complete new-source A/B remain open.

### P03-2 zero-standard census review round 2 and freeze (2026-10-28)

Two new independent reviewers (`gpt-6.1-sol` / `gpt-6-luna`, medium) approve
`07a0431a..bf794047` without verified findings. They recheck the one-line
dictionary addition, unchanged parser/assertions/discovery, identical five test
identities, original TRX hashes and zero-warning/error builds. The evidence
reviewer independently reconciles the cancelled run's **27** jobs and all six
original ZIP sizes, hashes and CRCs. Both fresh census review rounds are closed.

The source freezes again before one full A, original-archive verification and
one full B. No selected case, plan/manifest digest, weight, resource bound or
production policy changes. Final permission and catalogue promotion remains a
prepared documentation delta; its combined real corpus controls already pass
in the isolated future-document copy. Only complete successful same-source
hosted A/B and independent original-evidence audit permit that promotion and
PR handoff. No earlier failed or cancelled attempt substitutes for the pair.

### P03-2 Step 6 qualification and review handoff (2026-10-28)

Qualification uses implementation source
`c39ab614970b17c1edac25e36f5c6b7755ceec07`, tree
`f69d45a63125066301fb2d29c86da5f7f854e323`, on `development`.
[Full A and full B](https://github.com/HodeTech/LearnStack/actions/runs/40617050240)
are separate complete attempts 1/2, not failed-job reruns. Each passes **27/27**
jobs and all seven required names. Both select **6,927** backend cases and pass
every case with zero failures/skips: **2,998** Unit, **3,331** Integration,
**593** Architecture and **five** Contract. The selected/discovery/TRX/JSONL
unions are complete and disjoint within each attempt and identical across them;
A/B assignments differ. Backend **3,920** and Docker Integration **3,007**
aggregates reconcile exactly.

All **50** original ZIPs retain API artifact identity, byte size, SHA-256 and
CRC verification. Both attempts bind plan `f5161ae9` and manifest `1fa6c70c`,
the same .NET `10.0.403`, VSTest `18.7.0-release-26523-112` and xUnit adapter
`2.8.2.0`, including observed toolchain binary hashes. All twenty execution
owners prove natural ownership/disposal, no forced cleanup, and positive
memory observations; observed VM memory is not an enforced quota. Existing job
ceilings, the **203-second** reserve, fixture count and **20** execution-VM
topology remain unchanged.
Maximum producer API job walls (queue time excluded) are **983 / 1,046 seconds**
for A's backend/Docker populations and **1,001 / 1,039** for B. Per-owner
validation preserves native/base/Architecture twenty-minute and ordinary
25-minute ceilings. The independent assignment replay moves **2,730** ordinary
and **427** Architecture cases between owners; the full population stays identical.

Both hosted frontend jobs pass **2,458** tests across **73** files/two packages,
zero skips/todos, plus typecheck/lint/build and native ingress cleanup. Each
actual hosted demo retains **27** original JSON/HTML pairs, nine browser
navigations, **24** visibility controls, two TLS refusals, timeout refusal and
**134 HTTP 200** completions. Proof and cleanup finish within their existing
bounds. LCP remains advisory; raw medians and warnings remain visible in each
receipt. OpenAPI compares the frozen source with main `012e67cc`; original
comparison/fixture policy and secret-scan checks pass.

Two independent reviewers (`gpt-6-astra` / `gpt-6.1-sol`, high) audit original
hosted evidence rather than only summaries. The backend reviewer reconstructs
both assignments, all case
unions and TRX identities/durations, resource receipts, aggregate/API associations
and cross-attempt toolchains. The web reviewer checks all required jobs,
OpenAPI committed byte identities, secret scan, real hosted frontend logs and
paired demo metrics/control/cleanup evidence. Both approve without verified
findings after B and the cross-pair comparison. Earlier failed/cancelled runs
remain retained separately and are not replaced by this pair.

The final documentation delta promotes the seven qualified catalogue rules and
Permission Standard together with their index, matrices and phase gates. The
catalogue reports **251 Implemented = 146 Architecture + 105 elsewhere**, with
**320** compiled test methods unchanged. The standards split is **22 Active /
zero Adopted**. Actual post-update compiled corpus controls pass **5/5**, with
zero skips, after a zero-warning/error Release build. Documentation lint passes
with **2,796** resolved fragment links. Fresh documentation review remains
required before handoff. These documentation-only commits
follow the qualified implementation source; they are not described as that
source's hosted execution.

All six implementation steps and their two fresh review rounds are closed.
G2 execution, G5 enforcement, G6 policy/directory and G7 runtime evidence are
qualified only within their named slices. P03-3 retains new-user HTTP/BFF entry,
validated scope transitions, native mutation/UI transport and delegation screens.
P03-9 retains Platform/production recovery authority; P03-10 retains closure,
redaction and applicable locator workflows. Phase 03 remains in progress.
The next packet is P03-3, after this packet's PR review; it is not started here.

### P03-2 final documentation review and PR preparation (2026-10-28)

Two fresh independent reviewers (`gpt-6-astra` / `gpt-6.1-sol`, high) approve
the actual nine-file `c39ab614..6c6747ec` documentation delta without verified
findings. Earlier conditional carrier reviews are now complemented by review of
the committed updates. Permission review checks finite source counts, actual
HTTP/application enforcement and exact Role scope; Audit definitions remain
future declarations. Catalogue review checks all seven promoted canonical
methods, **28** named proof-owner classes, **251 / 146 / 105** totals and the
unchanged **320** compiled method population. Both preserve P03-3/9/10 ownership,
Phase 03 in-progress and PR review pending, without claiming a merge.

Reviewers distinguish the qualified implementation source from later
documentation commits. The actual final build and **5/5** corpus TRX are retained;
the latter's SHA-256 is
`85025f9f9b7d167103b9251e34bf15a855130be24f5aa75c63d8ac5c334fde52`.
They perform read-only code/document checks, not another hosted execution; the
separate original-ZIP backend/web audits supply that qualification evidence.
All implementation and correction review rounds are closed. The review handoff
uses `development` into `main`; no branch switch or merge occurs here.

### P03-2 PR 39 low-priority review corrections (2026-10-28)

Both reported findings are reproduced against `c37c3df4` and corrected within
their existing contracts. One shared auxiliary-source selector now enforces the
API's rule in seed and demo: both keys absent permit Development derivation;
explicit empty, whitespace, control-character or partial pairs refuse. Complete
selected values remain byte-exact and ambient partners supply no authority.
Seed and demo validate before stack startup, migrations or application seed.
Existing application-role/credential diagnostics retain their precedence.

Eight shared-input controls exercise seed/API/demo projections; fifteen real
entry-owner controls exercise empty/partial rejection across seed, local demo
and CI demo before stateful work. The four affected frontend files pass
**246/246** tests. Typecheck, lint and changed-test formatting pass. An initial
new-test union-index typing error is corrected before commit; it changes no
runtime behavior. Complete guarded frontend execution and fresh correction
reviews follow this commit.

Six mutable carriers now describe implemented operation witnesses, joint API
policy/evidence composition, actual Role grant commands and the authenticated
directory. The permission skill uses the registered policy/evaluator API;
related stale introduction/performance phrases are corrected in the same scope.
Packet state points to the Phase 03 Status block. Accepted ADRs, delivery history
and P03-3/9/10 obligations retain their boundaries. Documentation lint and
fragment/structure/index checks pass.

Review scope remains these two findings. Earlier implementation-source A/B
evidence stays attributed to `c39ab614`; the follow-up's local/hosted checks
are recorded separately. The maintainer's broader review coverage limits remain
explicit rather than becoming an unconditional merge approval.

### P03-2 PR 39 correction review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6.1-sol` / `gpt-6-luna`, high) approve
`c37c3df4..74254c8b` without verified findings. Tooling review checks the shared
pair, preserved role/credential precedence and API diagnostics, early real
entry-owner refusals and fixture dependency copies. The existing control-byte
predicate remains specifically CR/LF/NUL; this correction adds no broader
connection-string grammar. Documentation review verifies all six carriers
against actual composition and Accepted ADR-0076/0077, preserving later packet
ownership and the maintainer's review scope limits.

Guarded full frontend execution in a clean development copy at `74254c8b`
passes **2,481** tests across **73** files/two packages, zero skips/todos and
unchanged source. The **23** new controls complement the prior population.
Typecheck, lint, format, targeted **246/246** tests and documentation checks
also pass. The second fresh review round remains required before PR update.

### P03-2 PR 39 correction review round 2 and handoff (2026-10-28)

Two new independent reviewers (`gpt-6.1-sol` / `gpt-6-luna`, high) approve
`c37c3df4..39c98e4f` without verified findings within the correction scope.
They verify the common selected-pair rule, actual preflight ordering, fixed
diagnostics and six current documentation carriers. Tooling review independently
matches the full frontend log SHA-256
`e08534d67c1a1cc17a9dff4113de671462161dd2780345eb64807f0e086603a4`:
**2,481** passes, **73** files/two packages, zero skips/todos.

The final dependency census additionally finds the real .NET launcher's isolated
copy of `public-runtime.mjs`. One `File.Copy` supplies `demo-database.mjs` beside
it, matching the two frontend fixture updates. The tooling reviewer independently
checks all tracked consumers and this one-line follow-up; no test declaration or
assertion changes. The actual PostgreSQL/API launcher cases pass **3/3**, including
the foreign-user-secret dirty control. Native seed environment controls pass
**6/6**. All changed frontend sources still match the fully tested `74254c8b` copy.

Both fresh correction review rounds are closed. The follow-up checks qualify
these two findings; the maintainer's remaining full-PR review limits and pending
merge decision retain their stated scope. The original implementation A/B remains
bound to `c39ab614`; the updated PR head receives its own protected checks.

### P03-2 PR 39 role-guard cancellation control (2026-10-28)

[Run 40662644606/1](https://github.com/HodeTech/LearnStack/actions/runs/40662644606)
at head `6cf8c714`, merge source `5a2de796`, passes **25/27** jobs. Only
ordinary-8 and its dependent integration aggregate fail. All **24** available
original ZIPs pass API size/SHA-256/CRC verification. Twenty complete producer
unions contain **6,927** results: **6,926 Passed / one Failed**, zero missing or
skipped. The failed cancellation case executes for **8.088 seconds**; its closed
exception kind is `cancellation` and no source location was registered. The
aggregate correctly refuses `producer_not_successful`. Completion/forced-cleanup
flags on a failed worker do not independently prove a container leak.

The unchanged case passes locally. Independent source diagnosis finds a concrete
test race: its holder predicate accepted any granted catalog lock, including a
shared reader, before the intended exclusive blocker acquired. An eight-second
sleep also released the blocker by elapsed time and obscured which five-second
monitor failed. The sanitized historical evidence cannot identify that exact
interleaving or establish a production cancellation defect.

The fixture now acknowledges completion of the real exclusive `LOCK`, keeps its
privileged connection private, and releases/joins actual rollback and disposal
on explicit caller signal. Its existing thirty-second control bound fails closed.
The test warms an exact PID/mode/numeric-OID observer before locking, positively
exercises a shared reader and rejects it as an exclusive blocker. It observes the
actual source waiter, cancels it within the unchanged three-second assertion,
and checks that the acknowledged blocker's PID still holds its exclusive lock.
Cleanup retains the prior join/dispose path and capacity-recovery assertion.
Production's five-second admission deadline and all job ceilings stay unchanged.

The corrected real PostgreSQL class passes **9/9**, zero skips. All **95** native
owner controls pass, including private-value canaries and the expanded finite
source-location census. The optional `authorization_read_source` code now retains
bounded file/line diagnostics; waiter observation has a fixed timeout diagnostic.
No case declaration, selector, timing weight, permission or production path
changes. Fresh correction reviews and updated-head CI remain required.

### P03-2 PR 39 cancellation control review round 1 (2026-10-28)

Two fresh independent reviewers (`gpt-6.1-sol` / `gpt-6-luna`, high) approve
`6cf8c714..6ed0f12b` without verified findings. Lifetime review checks positive
acquisition, exact PID/mode observation, all early-failure release/join paths,
unexpected returned-owner disposal and real capacity recovery. Evidence review
independently verifies all **24** ZIP hashes/CRCs, the complete failed-attempt
population, **9/9** corrected local cases and **95/95** native control results.
Both preserve the unknown historical interleaving and unchanged runtime limits.

An isolated development-copy counterfactual then removes effective mode
qualification. The actual shared-reader negative control fails exactly as
expected, before the exclusive blocker starts. The single intentional failure
is not a passing result or historical reproduction. Its TRX SHA-256 is
`47a621c43ec82beceb953e5f546558a568c5448e8721e4b3fd6c904c0491af30`.
Source and Integration/Infrastructure/API DLL hashes restore exactly afterward.
The second fresh review round and updated-head hosted checks remain required.

### P03-2 PR 39 cancellation control review round 2 (2026-10-28)

Two new independent reviewers (`gpt-6.1-sol` / `gpt-6-luna`, high) approve
`6cf8c714..ca82e241` within their lifetime and evidence/diagnostic slices, without
verified findings. They recheck positive acquisition, exact holder/waiter
identity, all release/join paths, actual capacity recovery and bounded optional
diagnostics. The original **24** ZIPs, complete **6,927** outcomes, **9/9** corrected
cases, **95/95** native controls and expected wrong-mode counterfactual are
independently reconciled. The dirty source and three restored DLL hashes match.

Both fresh correction review rounds are closed. The historical failure stage
remains unknown; the demonstrated predicate weakness and its deterministic
control are the correction's evidence. Production paths and deadlines, the
case population, weights and completion authority are unchanged. A new PR-head
workflow must establish hosted success; neither local passes nor the earlier
implementation-source A/B are attributed to that new head.

### P03-2 merge and closeout (2026-10-28)

Read-only GitHub verification confirms
[PR #39](https://github.com/HodeTech/LearnStack/pull/39) merged at
`2026-10-28T12:03:27Z` (15:03:27 Europe/Istanbul), squash commit
`e55aa5d7bfcba4ccc2357fa0c2276025e14f8d55`. The final PR head is
`4406c1d31c69f55320d920d7dbc7be6bda3433ea`. Both commits have tree
`c5b2d8ce1dbf43b2df06cdc807d52f1e2e00e64a`; their file contents are identical.
Development incorporates main's squash history with a same-tree merge, without
switching branches or rewriting published history.

The [Step 6 closeout](#p03-2-step-6-qualification-and-review-handoff-2026-10-28)
owns complete implementation-source A/B qualification at `c39ab614` in
[run 40617050240](https://github.com/HodeTech/LearnStack/actions/runs/40617050240),
attempts 1/2. The final PR head separately passes its protected merge-head checks
in [run 40676287076/1](https://github.com/HodeTech/LearnStack/actions/runs/40676287076):
**27/27** successful jobs, all seven required contexts and **6,927/6,927** Passed
backend cases, zero failures/skips. These are **2,998** Unit, **3,331** Integration,
**593** Architecture and **five** Contract cases. All **25** original ZIPs pass
API identity, byte-size, SHA-256 and CRC verification; the two aggregate
populations form the complete union of 6,927 distinct cases. The previously
failing ordinary-8 cancellation case passes, alongside that producer's other
**229** cases.

The PR workflow executes merge source
`6313c3644f0612325fb5b0161eb6949634d2f0f4`, whose second parent is the final PR
head and whose tree matches it. The retained evidence keeps this identity
separate from the earlier A/B source and main's squash commit. This closeout
rechecks merge/run identities and tree equality; it does not rerun tests or
relabel pre-merge qualification as post-merge execution. Main's separate
[push run 40684736041](https://github.com/HodeTech/LearnStack/actions/runs/40684736041)
is not claimed as successful here. The failed cancellation attempt and its
unknown historical interleaving remain disclosed in their original records.

All six implementation steps, both fresh review rounds per step, the two verified
low-priority PR findings and the cancellation-control correction are closed.
The shipped Identity boundary enforces current Membership and scoped permissions
at HTTP and application dispatch, with default denial, operation-bound EF/RLS
read authority, bounded administrative directory reads and finite audited Role
grant acts. Separate bounded authorization/evidence lanes preserve progress;
denied invocations and sensitive reads use the central MUST evidence contract.
[ADR-0076](../decisions/0076-live-scoped-authorization.md) and
[ADR-0077](../decisions/0077-trusted-authorization-evidence.md) govern these
boundaries. The merge records the maintainer's decision; scoped correction
reviews do not become an unconditional review of every PR line.

P03-2 is complete; Phase 03 remains in progress. P03-3 is next and not started.
Its decision pass closes G1 activation, G4 scope transition, G5 delegation/UI and
G6 native/mutation transport before their first affected writers. It owns new-user
HTTP/BFF entry with explicit no-session recovery, independent tenant/org session
scope and switching, member/role screens, Tenancy organization endpoints and the
defined member settings slice. The
[phase plan and decision register](phase-03-identity-admin.md#packets-and-decision-gates)
own that scope and its proofs.

Production first-admin/issuer-rebind authority remains P03-9; seed-backed P03-3
journeys do not prove production onboarding. Invitations, custom fields, provider
event ingress, back-channel logout, federation, DSAR/global closure and exit
enforcement retain their named packets. No remaining phase-wide criterion is
marked complete by this merge, and no new ADR, permission or runtime is introduced
by this documentation closeout.
