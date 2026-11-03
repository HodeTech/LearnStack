# ADR-0082: Current-Source CI Execution Windows

## Status

Accepted

**Date:** 2026-11-01
**Deciders:** @cemil
**Bounded supersession:** ADR-0063's twenty-minute Architecture ceiling
(restated in ADR-0075), ADR-0075's twenty-five-minute ordinary ceiling, and the
execution-window clauses they inherited from ADR-0062. Dated amendments in all
three records disclose this change; their historical bodies, statuses and dates
remain intact.

Maintainer acceptance changes only the two whole-job ceilings and their
dependent observation bounds. Executable budget implementation follows
acceptance; no grant or other policy changes. The
[acceptance record](../roadmap/phase-03-delivery-log.md#adr-0082-maintainer-corrections-acceptance-and-step-1-2026-11-01)
owns the approved clarifications and three-step delivery/review sequence.
Implementation and actual hosted qualification remain separate gates.

## Decision Drivers

- Four complete hosted A attempts fail on successive P03-3 source revisions.
  Each is historical evidence for its own source; subsequent corrections are
  not qualified by those runs. The latest exhausts five Architecture and three
  ordinary owner windows. Passing siblings and censored cases qualify none.
- Six Architecture and twelve ordinary owners already consume the Accepted
  upper count bounds and, with base/native, the twenty-execution-VM cap.
  Each VM repeats real build, discovery and ownership work.
- Local source corrections preserve proofs but do not establish hosted fit.
  A mixed indexing trial produces no claimed gain and its field-key component
  is discarded; a small clean-evaluation profile is not a full-suite
  performance result.
- Preserve all cases, test-specific deadlines, the 203-second reserve, natural
  disposal, exact current-attempt evidence and two complete distinct layouts.

## Considered Options

1. **Architecture 25 minutes; ordinary 30 minutes** (chosen). Add
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
retain 1,200 seconds. The two aggregates, meta and all remaining jobs retain
their existing ceilings; the 203-second reserve, test-specific bounds, counts,
assignment, resource policy and complete A/B qualification stay fixed.
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
complete their case populations with seven provider-startup failures and one
browser cleanup failure, while their ownership receipts remain incomplete.
A longer job window repairs none of these nine failed cases. The 28 cases
without results remain censored; their unobserved tails establish no duration.

Six provider results share one failed fixture initialization and the seventh
owns another startup. Both exhaust the existing 120-second startup bound;
their internal cause remains unproved. The browser failure identifies the
third `unavailable_pages` journey's native-close observation, not a pending
shutdown branch. Separate corrections/diagnostics must preserve these failures
until real complete executions pass. This decision extends neither 120-second
provider startup nor frontend's 180-second package guard.

The [delivery log](../roadmap/phase-03-delivery-log.md#p03-3-step-10-fourth-hosted-failure-and-isolated-protocol-proofs-2026-10-31)
owns earlier failed A receipts and subsequent local work. No B exists for these
sources. ADR-0063's example of both assignments failing is not the observation;
reopening follows finite-envelope exhaustion and the still-unsatisfied complete
two-layout qualification, as distinguished in ADR-0075. It is a deliberate
policy request, not an automatic exception to the current ceiling.

### Budget arithmetic

| Verified owner | Predecessor job / nominal work window | Accepted job / nominal work window |
|---|---|---|
| Architecture | 1,200 / 997 seconds | 1,500 / 1,297 seconds |
| Ordinary | 1,500 / 1,297 seconds | 1,800 / 1,597 seconds |
| Base/native | 1,200 / 997 seconds | Unchanged |

Nominal work is job time minus the unchanged 203-second reserve. For the five
timed-out Architecture owners, observed pre-owner work is 238.197–272.233
seconds: remaining nominal work changes from 724.767–758.803 to
1,024.767–1,058.803 seconds. For the three timed-out ordinary owners,
273.545–316.041 seconds of pre-owner work leaves 980.959–1,023.455 under the
predecessor ceiling and 1,280.959–1,323.455 under this decision. Setup, discovery,
execution and export still consume this remainder; these numbers predict no test completion.

Weights are advisory, summed case duration is not parallel wall time, and
censored tails establish no safe minimum. Fresh complete final-source A/B
decides sufficiency. Further exhaustion reopens this choice rather than
silently raising a clock. The current corrective work adds four offline
provider cases to base; qualification freezes the actual final discovered
population instead of reusing the historical 7,940 count.

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
  read-only evidence permissions, both five-minute aggregates and five-minute
  meta stay fixed.
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
   decision index and Phase 03 Status, and remove the local-test guide's stale
   uniform-window wording by linking Testing 06. The existing rule's Implemented
   status is not a claim that this extension is delivered.
2. Change only `backend-architecture` to 25 minutes and
   `backend-integration-tests` to 30 in the workflow. Extend the existing
   `job_seconds(mode, job, counts)` policy in `scripts/ci-native-tests.py`. After
   exact mode/job/count validation, select by mode: base/native-shard 1,200,
   Architecture 1,500 and ordinary 1,800 seconds; never select by job alone.
   Parent/leaf clocks and
   `scripts/ci-test-shard-results.py` consume that same validated policy.
   `workflow_topology`, `verify_jobs`, `verify_receipt` and `finite_seconds`
   choose bounds from independently expected owners, never receipt partitions.
   Architecture receipt/workload/project observations receive 1,500, ordinary
   1,800 and base/native 1,200. Disjoint receipt intervals sum strictly below
   the selected ceiling; nested measurements are not additional intervals.
   Fixture preparation uses the same ordinary window but retains its existing
   110-second total, 55-second image and two-minute step caps. No global
   observation-limit expansion or second independent duration policy is added.
   Extend `scripts/test-ci-native-tests.py` and
   `scripts/test-ci-test-shard-results.py`, then run the complete relevant
   control suites. Re-measure the combined plan/result controls against meta's
   existing 27-second TERM / three-second kill allocation; added cases do not
   imply that budget still fits. Record this extension as implemented in the
   existing catalogue Status paragraph only after its actual controls and
   reviews pass, keeping hosted fit explicitly pending.
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
Architecture observations/API walls in (1,200, 1,500] and ordinary in
(1,500, 1,800]; their former boundary values remain valid. Values above
1,500/1,800 respectively and unchanged base/native counterexamples refuse.
Individual observations allow the inclusive ceiling; disjoint receipt intervals
sum strictly below it. Test NaN/infinity/negative values, strict interval
sums, nested measurement bounds, wrong/unknown modes, indices, expected jobs,
forged partitions and unchanged reserve/topology/operation limits. Configuration
and synthetic controls prove policy enforcement; only complete fresh hosted
A/B proves actual fit and natural lifecycle completion.

## Amendments

### Amendment 1 — Public-demo outer-window successor (2026-11-02)

Accepted [ADR-0085](0085-public-demo-ci-execution-window.md) supersedes only
the public-demo application of this record's all-remaining-job ceiling clause.
Public-demo selects twenty-two job minutes and nineteen first-step work minutes.
Architecture/ordinary, base/native and every other job remain unchanged, as do
the demo's 180-second nominal reserve, internal 720-second proof, operation
limits, population, serial isolation, admission and owned cleanup. The separate
clock starts do not guarantee the full reserve remains at every instant. This
record's body, date, acceptance and rationale stay historical. The
[Phase 03 acceptance record](../roadmap/phase-03-delivery-log.md#adr-0085-maintainer-corrections-acceptance-and-step-1-2026-11-02)
owns implementation/reviews and fresh complete final-source A/B; acceptance
is neither executable delivery nor hosted fit.

### Amendment 2 — Native web package-window successor (2026-11-03)

Accepted [ADR-0086](0086-native-web-test-package-window.md) supersedes only
this record's unchanged test-specific-bound clause for the web package's
aggregate guard: the validated web path selects 360 seconds with two file
workers; SDK/other packages retain 180 seconds. All individual test/process,
HTTP/readiness and shutdown bounds, case/file discovery, internal concurrency,
cleanup, job windows, VM cap and complete same-source A/B remain unchanged.
This record's body and original rationale remain historical. The
[acceptance record](../roadmap/phase-03-delivery-log.md#adr-0086-acceptance-and-bounded-implementation-2026-11-03)
separates decision, implementation and actual qualification.

### Amendment 3 — Selected proof orchestration windows (2026-11-03)

Accepted [ADR-0087](0087-native-frontend-proof-windows.md) supersedes only
§ Decision's preserved test-specific-bounds clause for its named frontend
proof orchestration rows. ADR-0086's web aggregate becomes 600 seconds; closed
matrix groups/wrappers/cases become 180/210/220 seconds. § Context's separate
120-second provider-startup bound is unchanged, as are backend job/resource/
reserve policy and all unlisted bounds. This body remains historical.

## References

- [ADR-0062](0062-native-crash-proof-ci-provisioning.md).
- [ADR-0063](0063-complete-bounded-ci-test-shards.md).
- [ADR-0075](0075-ordinary-ci-job-window.md).
- [Testing Standards](../standards/06-testing.md#complete-bounded-ci-assignments).
- [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md).
- [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md#ci_test_shards_are_complete_and_bounded).
- [Documentation Standards](../standards/13-documentation.md).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
