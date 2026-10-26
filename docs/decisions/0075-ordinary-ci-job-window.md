# ADR-0075: Ordinary CI Job Window

## Status

Proposed

**Date:** 2026-10-26
**Deciders:** Maintainer — pending approval.
**Supersedes:** Only ADR-0063's twenty-minute ordinary Docker execution clauses.

This proposal requires maintainer acceptance before implementation. It authorizes
no present ceiling change. ADR-0063 remains Accepted; its other decisions remain
in force. On acceptance, a dated amendment there records this bounded
supersession and links here. This is a changed policy, not an erratum; this
proposal does not edit the Accepted record.

## Decision Drivers

- Complete A passes, but complete B on the same source stops two ordinary
  producers at their owner deadline. Six selected cases have no result; passing
  completed cases and the preceding A cannot qualify B.
- The current six Architecture and twelve ordinary producers already use
  ADR-0063's upper count bounds and twenty execution VMs. More ordinary VMs
  change both the accepted count range and total cap.
- Repeated build, discovery and fixture startup consume real wall time that
  summed case weights do not represent. Further lucky weight tuning does not
  establish complete execution under both materially different assignments.
- Preserve complete case ownership, zero skips, natural disposal, resource
  ownership/accounting, seven required check names and strict current-attempt
  evidence.

## Considered Options

1. **Twenty-five minutes for ordinary producers only** (chosen). Add bounded
   wall-time headroom without adding VMs or changing assignment. Hosted fit
   remains a required proof, not a consequence of accepting the ceiling.
2. **Keep twenty minutes and optimize fixtures/tests further** (valid, not
   selected). This retains the policy but needs further test-only lifecycle
   work and proof that sharing, startup or disposal changes preserve isolation
   and assertions. No sufficient measured improvement is established here.
3. **Increase ordinary producers to fourteen or sixteen** (valid, not selected).
   With the current six Architecture producers, this exceeds the accepted
   eight–twelve ordinary range and twenty-VM cap, duplicates
   build/discovery/fixture work and increases organization queue pressure.
4. **Adopt fixture-aware assignment** (valid, not selected). A new algorithm
   needs instrumentation, versioning and independent A/B qualification.
   Current fixture-start counts and startup costs are unobserved; case weights
   cannot supply that missing measurement.
5. **Choose twenty-two or twenty-three minutes** (not selected). Censored tails
   do not establish a measured minimum or prove either shorter window fits.
   Twenty-five is a bounded headroom choice, not the minimum derived from data.
6. **Drop/skip cases, ignore failed producers or combine attempts** (rejected).
   These remove proof of the complete population and cannot qualify a release.

## Decision

LearnStack gives only the verified ordinary Docker producers a twenty-five-minute
(1,500-second) whole-job ceiling in place of twenty minutes (1,200 seconds).
The other job ceilings, 203-second cleanup/export/upload reserve, resource
ownership/accounting, assignment rule and complete two-layout qualification remain
unchanged. The longer window follows the verified expected producer identity
through a closed mode allowlist; no receipt, environment override or arbitrary
producer chooses it.

## Context

### Retained same-source evidence

[Run 40382715371](https://github.com/HodeTech/LearnStack/actions/runs/40382715371)
uses PR head `7aabc6d8a3e2109a6e6dfa0c439442b0dcd008d9` and actual merge
checkout `2c692b51adc5feb7fb0c843d4124938a61775a7b`. Their tree is equal:
`217d237966b20215ed25a15d5440c4d05a18717c`. These are related source identities,
not interchangeable commit hashes. The retained plan digest is
`115e474202360d0d7d99e1a8c69a0b40f7eb84669364b23ce90c94341969113b`;
the full manifest digest is
`8d017042f7e439273a7164326d9a1ae7ef3ea4ea3dea9a1bfa4c315a133bef21`.

Original metadata, ZIPs and extracted proof remain under
`artifacts/p031-step6/hosted/40382715371/{1,2}/`. Attempt 1 selects A; attempt 2
selects B. The [Phase 03 delivery log](../roadmap/phase-03-delivery-log.md)
owns the dated execution and independent audit receipts.

- **A:** All 27 jobs succeed. Exact unions contain 3,664 backend and 2,826
  Docker cases: 6,490 passes, zero skips. All 25 original ZIPs are retained and
  independently audited, including twenty natural execution owners. The
  maximum producer job wall is 920 seconds.
- **B:** Twenty-four jobs succeed. `ordinary-3`, `ordinary-7` and the dependent
  integration aggregate fail; the backend aggregate and all Architecture
  producers pass. All 24 available original ZIPs are retained. Completed
  evidence contains 6,484 passing cases and six cases without results.
- **B ordinary-3:** 262 selected, 259 completed, all completed outcomes Passed.
  Pre-owner work takes 255.148 seconds; discovery/selection takes 42.654.
  The persona theory completes two of four rows and the historical-upgrade
  Fact completes zero of one.
- **B ordinary-7:** 267 selected, 264 completed, all completed outcomes Passed.
  Pre-owner work takes 256.526 seconds; discovery/selection takes 43.896.
  The cross-tenant-token theory completes zero of two rows and the real
  backchannel Fact completes zero of one.
- Both failed receipts report `deadline_exhausted`, `forcedCleanup: true` and
  `dockerAbsent: false`. Cleanup ownership does not establish natural disposal.
  They remain unqualified; no missing case becomes an assertion failure,
  a skip or an inferred pass.

The preceding [independent mutation collection change][mutation-collections]
moves nineteen method groups without changing runtime code or test bodies;
all 66 affected local cases pass. The 303-method Architecture census across
53 classes has a historical maximum complete-observation class envelope of
617 seconds. This diagnoses class serialization; it does not establish a future
wall bound. Current B now completes Architecture. Earlier ordinary weight-only
revisions did not qualify complete B, and further partition luck is not proof.

### Budget arithmetic and uncertainty

The current nominal owner window before pre-owner/discovery work is
`1,200 − 203 = 997` seconds. The proposal makes it
`1,500 − 203 = 1,297` seconds. With the two observed B pre-owner and discovery
intervals plus approximately 0.2 seconds of owner setup, nominal remaining
test-process time changes from approximately 699/696 to 999/996 seconds.
This ledger excludes other overhead; it is not a prediction that the six
censored cases finish, a fixture profile or a future fit guarantee.

The censored tails do not reveal their full duration or a precise necessary
ceiling. Twenty-five minutes adds five bounded minutes to an already measured
path while preserving topology. Actual final-source A/B execution decides
whether that choice suffices. Further exhaustion reopens the decision rather
than silently extending the window again.

### Boundaries retained

- Current counts stay six Architecture and twelve ordinary; approved ranges
  stay four–six and eight–twelve, with one base and one native VM and at most
  twenty backend/test execution VMs.
- Algorithm/version, B salt, advisory weights, tie-breakers, filters, argument
  bounds and the complete discovered case population stay unchanged.
- Existing child resource ownership and CPU/memory accounting stay unchanged;
  these observations are not enforced numeric CPU or memory quotas. Native
  collector authority, Docker/process ownership and the 203-second reserve stay
  unchanged.
  Every test-specific startup, operation, transport, cancellation and cleanup
  deadline keeps its existing bound; this is only a whole-job policy change.
- Base, Architecture and native execution jobs retain twenty minutes. Both
  aggregates and meta retain five minutes. Every other workflow job keeps its
  existing ceiling, including any ceiling already below twenty minutes.
- All seven required names, strict protection, read-only evidence permissions,
  natural disposal and exact current-attempt case/TRX/artifact validation stay
  unchanged. No runner-tier or concurrency upgrade is included.
- No deployment/environment override or application authentication, token,
  session, authorization or tenant-isolation change follows from this decision.

## Consequences

### Positive

- Ordinary jobs gain 300 seconds of bounded headroom without additional VMs,
  changed case ownership or weaker success evidence.
- Launcher deadlines, workflow configuration and independent validation have
  one explicit policy boundary that can be tested against forged identities.

### Negative

- A slow or hung ordinary producer can delay feedback by up to five additional
  minutes and consume more running-job time.
- The larger window guarantees neither runner availability nor hosted fit. It
  repairs no assertion failure and changes no CPU/memory policy or runner tier.

### Neutral

- Queued time remains separate from running-job wall time. No current pricing,
  billing or available organization concurrency is assumed by this proposal.
- Acceptance approves the policy and dependent work; implementation, review
  and hosted qualification remain separate gates.

## Implementation Notes

Implementation starts only after maintainer acceptance and proceeds in three
reviewable steps. Each step is committed and receives two fresh independent
review rounds; verified findings are fixed, committed and revalidated before
the next step starts.

1. **Acceptance carriers.** Mark this ADR Accepted with the maintainer receipt;
   append the dated bounded-supersession amendment to ADR-0063. Update
   [Testing Standards § Complete bounded CI
   assignments](../standards/06-testing.md#complete-bounded-ci-assignments)
   and its derivation reference. Reconcile the corresponding twenty-minute
   statement in [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md)
   through the same ordinary-only exception and reference. Extend the existing
   [`CI_Test_Shards_Are_Complete_And_Bounded` catalogue entry][ci-rule]
   with this source and the closed ordinary-budget assertion; its existing
   Implemented status does not assert the extension is delivered. Move the
   decision index row to Active and update Phase 03 status and the delivery
   record. Do not rewrite ADR-0063's historical rationale or unrelated rules.
2. **Closed budget implementation.** Change only ordinary whole-job policy in
   the carriers below, preserve all retained boundaries, run the relevant
   offline controls and obtain two fresh independent review rounds before
   hosted qualification. Review findings receive fixes and fresh verification.
3. **Fresh hosted proof.** Run two full attempts, literal A and B, on the same
   final source, plan, toolchain and case population. Each needs all 27 jobs,
   all 6,490 cases with exact unions and zero skips, 25 original archives and
   twenty natural owners, plus independent archive/API/case cross-audit.
   Preserve each attempt before the next rerun. Historical A is context, never
   replacement proof for the newly implemented source; failed-job reruns and
   mixed-attempt unions remain refused.

The source budget carriers are:

- [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml): only
  `backend-integration-tests` changes to `timeout-minutes: 25`; literal indices,
  clocks before checkout, uploads, other jobs and required names stay unchanged.
- [`scripts/ci-native-tests.py`](../../scripts/ci-native-tests.py): replace the
  uniform `JOB_SECONDS` use at `deadlines()` for the execution owner and leaf
  with one closed mode-to-window policy. Validate the exact supported mode,
  expected job and shard index before granting 1,500 seconds. Unknown modes,
  prefix lookalikes and forged mode/job pairs refuse. Preserve every reserve
  component and its failure-tail authority; no environment budget knob exists.
- [`scripts/ci-test-shard-results.py`](../../scripts/ci-test-shard-results.py):
  align `workflow_topology`, `verify_jobs`, `finite_seconds` and
  `verify_receipt` with the same expected-producer policy. Choose the receipt
  interval ceiling from the independently expected/verified owner, never the
  payload's self-declared `partition`. Bind the receipt to that owner before
  applying any longer budget. Bind `prepare_fixture_images` to verified
  ordinary workflow identity and the same window; retain its 110-second total,
  55-second per-image limits and two-minute workflow step ceiling. Aggregate
  acquisition/download/validation/upload allocations remain five-minute work.

## Architecture Tests

This ADR introduces no application architecture rule. Extend the existing
offline launcher/result/plan controls and workflow validation; do not invent a
second application-test catalogue name for a CI policy.

- **Counterfactual controls:** ordinary API job walls above 1,200 through
  1,500 seconds can pass only with otherwise valid complete expected ordinary
  identity/evidence. Individual receipt intervals remain finite and at most
  1,500; their sum stays strictly below 1,500. Above 1,500 refuses. Other
  producer kinds retain the corresponding 1,200-second limits.
- **Identity controls:** unknown modes, out-of-range indices, prefix lookalikes,
  forged receipt partitions and mode/job mismatches cannot acquire the longer
  window. Launcher, image preparation and validator agree on the closed set.
- **Workflow controls:** reject any ordinary ceiling other than 25, any increase
  elsewhere, changed counts/resource policy/reserve, bypassed identity or weakened
  aggregate/meta allocations. Existing completeness and tamper controls remain.
- **Proof levels:** synthetic counterfactuals establish policy enforcement;
  unit/offline controls establish bounded behavior; workflow checks establish
  configuration agreement. Only complete final-source hosted A/B establishes
  actual wall fit, natural lifecycle completion and the full artifact union.

## References

- [ADR-0063: Complete Bounded CI Test Shards](0063-complete-bounded-ci-test-shards.md).
- [ADR-0062: Native Crash Proof CI Provisioning][native-ci].
- [Testing Standards](../standards/06-testing.md).
- [Documentation Standards](../standards/13-documentation.md).
- [Phase 03](../roadmap/phase-03-identity-admin.md).

[mutation-collections]: ../roadmap/phase-03-delivery-log.md#p03-1-pr-38-independent-mutation-collections-2026-10-26
[native-ci]: 0062-native-crash-proof-ci-provisioning.md
[ci-rule]: ../standards/21-architecture-tests-catalogue.md#ci_test_shards_are_complete_and_bounded
