# ADR-0082: Current-Source CI Execution Windows

## Status

Proposed.

**Date:** 2026-11-01
**Deciders:** @cemil
**Bounded supersession:** ADR-0063's twenty-minute Architecture ceiling,
ADR-0075's twenty-five-minute ordinary ceiling, and their inherited execution
window clauses in ADR-0062. Acceptance requires a dated amendment in all three
records; their historical bodies, statuses and dates remain intact.

This proposal changes no clock, grant or Accepted policy. Maintainer acceptance
precedes its dependent budget implementation. Independent P03-3 correctness,
diagnostic and local proof work continues under the current ceilings.

## Decision Drivers

- Four complete hosted A attempts fail to qualify the current P03-3 source.
  The latest exhausts five Architecture and three ordinary owner windows;
  passing siblings and censored cases cannot qualify an attempt.
- Six Architecture and twelve ordinary owners already consume the Accepted
  upper count bounds and, with base/native, the twenty-execution-VM cap.
  Each VM repeats real build, discovery and ownership work.
- Local source corrections preserve proofs but do not establish hosted fit.
  A mixed indexing trial increases five-case local wall time and its field-key
  component is discarded; a small clean-evaluation profile is not a full-suite
  performance result.
- Preserve all cases, test-specific deadlines, the 203-second reserve, natural
  disposal, exact current-attempt evidence and two complete distinct layouts.

## Considered Options

1. **Architecture 25 minutes; ordinary 30 minutes** (chosen proposal). Add
   300 seconds to each affected owner's whole-job window. This is bounded
   maintainer-judged headroom, not a measured minimum or a fit guarantee.
2. **Keep 20/25 and continue optimizing guards/fixtures** (valid, not selected).
   Correctness fixes still ship. Further shared metadata/fixture work needs
   its own equivalence, isolation and lifecycle proof; current local timings
   do not establish sufficient complete hosted execution.
3. **Change shard counts, runner tier or assignment** (valid, not selected).
   More owners exceed the current upper counts/cap; a different allocation or
   algorithm changes another policy and still needs complete A/B evidence.
4. **Choose smaller increases** (not selected). Incomplete tails reveal no
   necessary ceiling. Smaller values share the uncertainty with less headroom.
5. **Borrow reserve, extend test deadlines, skip work or mix attempts**
   (rejected). These weaken different boundaries or lose complete proof.

## Decision

LearnStack gives verified Architecture producers a 1,500-second whole-job
ceiling and verified ordinary Docker producers 1,800 seconds. Base and native
retain 1,200 seconds; other jobs, the 203-second reserve, test-specific bounds,
counts, assignment, resource policy and complete A/B qualification stay fixed.
The existing closed `(mode, expected job, plan counts)` policy selects every
execution and observation bound; payloads and environment overrides select none.

## Context

### Observed failure and evidence limits

The [fourth A attempt](https://github.com/HodeTech/LearnStack/actions/runs/41160562129)
uses `f0de02cda3bbd5a78c1d6cd5e5c9a28aa3d5d1d1`, tree
`451da9c1e74917ecf7e857b39730e2ae75b054b9`. Fourteen jobs succeed and thirteen
fail. All 23 available original ZIPs are retained with API sizes/SHA-256, CRC
and bounded member checks; both failed aggregate ZIPs are absent.

Independent parsing reconciles 7,940 selected cases: 7,903 Passed, nine Failed
and 28 without results. Architecture has 594/1/9 of 604; ordinary 3,410/8/19 of
3,437; all 3,868 base and 31 native cases pass. Five Architecture and three
ordinary producers exhaust their owner deadlines. Two other ordinary owners
complete their populations with seven provider-startup failures and one browser
cleanup failure. A longer job window repairs none of these nine failures.

Six provider results share one failed fixture initialization and the seventh
owns another startup. Both exhaust the existing 120-second startup bound;
their internal cause remains unproved. The browser failure identifies the
third `unavailable_pages` journey's native-close observation, not a pending
shutdown branch. Separate corrections/diagnostics must preserve these failures
until real complete executions pass. This proposal extends neither 120-second
provider startup nor frontend's 180-second package guard.

The [delivery log](../roadmap/phase-03-delivery-log.md#p03-3-step-10-fourth-hosted-failure-and-isolated-protocol-proofs-2026-10-31)
owns earlier failed A receipts and subsequent local work. No B exists for these
sources. ADR-0063's example of both assignments failing is not the observation;
reopening follows finite-envelope exhaustion and the still-unsatisfied complete
two-layout qualification, as distinguished in ADR-0075. It is a deliberate
policy request, not an automatic exception to the current ceiling.

### Budget arithmetic

| Verified owner | Current job / nominal work window | Proposed job / nominal work window |
|---|---|---|
| Architecture | 1,200 / 997 seconds | 1,500 / 1,297 seconds |
| Ordinary | 1,500 / 1,297 seconds | 1,800 / 1,597 seconds |
| Base/native | 1,200 / 997 seconds | Unchanged |

Nominal work is job time minus the unchanged 203-second reserve. For the five
timed-out Architecture owners, observed pre-owner work is 238.197–272.233
seconds: remaining nominal work changes from 724.767–758.803 to
1,024.767–1,058.803 seconds. For the three timed-out ordinary owners,
273.545–316.041 seconds of pre-owner work leaves 980.959–1,023.455 now and
1,280.959–1,323.455 under the proposal. Setup, discovery, execution and export
still consume this remainder; these numbers predict no test completion.

Weights are advisory, summed case duration is not parallel wall time, and
censored tails establish no safe minimum. Fresh complete final-source A/B
decides sufficiency. Further exhaustion reopens this choice rather than
silently raising a clock. The current corrective work adds four offline base
cases; qualification freezes the actual final discovered population instead
of reusing the historical 7,940 count.

## Consequences

### Positive

- Existing execution owners gain bounded headroom without more VMs, fewer
  cases, changed assignment or weaker natural-completion evidence.
- One validated owner policy continues to govern workflow, launcher, fixture
  preparation, API wall time, receipt intervals and enclosing measurements.

### Negative

- Each affected slow/hung job may run five additional minutes. Eighteen
  affected owners can consume up to 90 additional execution minutes per full
  attempt; this is not a workflow-latency or billing prediction. Queueing and
  concurrency determine aggregate feedback delay.
- Larger whole-job windows guarantee neither fit nor fixture reliability.
  Failed assertions, startup limits and ownership failures remain failures.

### Neutral

- Six Architecture, twelve ordinary, one base and one native owner remain;
  Accepted ranges, twenty-VM cap, runner tier and resource observations stay.
- Native collector authority, Docker/process ownership, strict required names,
  read-only evidence permissions and both five-minute aggregates stay fixed.
- Acceptance, implementation/reviews and fresh hosted qualification are
  separate gates. Manual accessibility observations remain separately deferred.

## Implementation Notes

The [three-step package](../roadmap/phase-03-delivery-log.md#adr-0082-proposed-current-source-ci-window-package-2026-11-01)
owns append-ready amendments and delivery/review order. No budget code precedes
maintainer acceptance.

1. Accept this ADR; append own-record dated amendments to ADR-0062, ADR-0063
   and ADR-0075. Reconcile [Testing Standards](../standards/06-testing.md#complete-bounded-ci-assignments),
   [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md),
   the [existing CI catalogue rule](../standards/21-architecture-tests-catalogue.md#ci_test_shards_are_complete_and_bounded),
   decision index and Phase 03 Status. Existing rule status claims no new proof.
2. Change only `backend-architecture` to 25 minutes and
   `backend-integration-tests` to 30 in the workflow. Extend the existing
   `job_seconds(mode, job, counts)` policy in `scripts/ci-native-tests.py`;
   keep base/native's constant at 1,200. Parent/leaf clocks and
   `scripts/ci-test-shard-results.py` consume that same validated policy.
   `workflow_topology`, `verify_jobs`, `verify_receipt` and `finite_seconds`
   choose bounds from independently expected owners, never receipt partitions.
   Architecture receipt/workload/project observations receive 1,500, ordinary
   1,800 and base/native 1,200. Disjoint receipt intervals sum strictly below
   the selected ceiling; nested measurements are not additional intervals.
   Fixture preparation uses the same ordinary window but retains its existing
   110-second total, 55-second image and two-minute step caps. No global
   observation-limit expansion or second independent duration policy is added.
3. Qualify full literal A and full B on identical final source/tree, plan,
   toolchain and actual discovered population. Each needs all 27 successful
   jobs, twenty natural owners, exact zero-skip case/TRX unions and 25 original
   ZIPs with independent API/archive/case audit. Retain A before full rerun B.
   Failed-only reruns, historical passes and mixed unions remain refused.

Each step is committed and receives two fresh independent review rounds;
verified findings are fixed, committed and revalidated before progression.

## Architecture Tests

Extend the existing `CI_Test_Shards_Are_Complete_And_Bounded` offline controls;
no second application architecture rule is introduced. Require otherwise valid
Architecture observations/API walls above 1,200 through 1,500 and ordinary
above 1,500 through 1,800; refuse values above their own ceiling and unchanged
base/native counterexamples. Test NaN/infinity/negative values, strict interval
sums, nested measurement bounds, wrong/unknown modes, indices, expected jobs,
forged partitions and unchanged reserve/topology/operation limits. Configuration
and synthetic controls prove policy enforcement; only complete fresh hosted
A/B proves actual fit and natural lifecycle completion.

## References

- [ADR-0062](0062-native-crash-proof-ci-provisioning.md).
- [ADR-0063](0063-complete-bounded-ci-test-shards.md).
- [ADR-0075](0075-ordinary-ci-job-window.md).
- [Documentation Standards](../standards/13-documentation.md).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
