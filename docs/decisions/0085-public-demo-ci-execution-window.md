# ADR-0085: Public Demo CI Execution Window

## Status

Proposed

**Date:** 2026-11-02
**Deciders:** @cemil
**On acceptance, bounded supersession:** ADR-0056's 20-minute public-demo CI job
ceiling, only the public-demo application of ADR-0075's other-workflow-job
ceiling boundary, and only that application of ADR-0082's all-remaining-jobs
clause. All three records remain Accepted; dated amendments disclose the
specific change without rewriting their historical bodies.
No executable clock or Accepted policy changes while this record is Proposed.
The [three-step package](../roadmap/phase-03-delivery-log.md#adr-0085-proposed-public-demo-ci-window-package-2026-11-02) owns acceptance, implementation and qualification.

## Decision Drivers

- A complete current-source hosted A fails when the external absolute deadline
  cancels the final of 27 required audits. Partial evidence cannot qualify.
- The proof's 12-minute limit, four 61-second quiet intervals, serialized fresh
  workers, admission rules, report census and owned cleanup protect distinct
  contracts. The job must reserve time for teardown and evidence.
- Complete final-source hosted A and B are still required; a failed A cannot
  supply a B or establish a duration for the censored worker.

## Considered Options

1. **22-minute job, 19-minute nominal work window** (proposed). Add two minutes
   to each outer limit while preserving the three-minute reserve. This is bounded
   maintainer judgment, not a measured minimum or a fit guarantee.
2. **Keep 20/17 and measure or optimize** (viable, not selected). The existing
   limits remain defensible, but no safe optimization with demonstrated savings
   has yet been established. Any optimization still needs its own proof.
3. **Borrow reserve, remove quiet time, shrink the census, parallelize or reuse
   browsers** (not selected). These change cleanup, admission, isolation or proof
   contracts rather than only the outer window.
4. **25-minute job with a matching work-window increase** (not selected). More
   headroom is uncalibrated by the censored result and costs more when a job hangs.

## Decision

LearnStack gives the `public demo + lighthouse` job a 22-minute whole-job ceiling
and sets its absolute work deadline 19 minutes after the first step starts. The
existing 180-second nominal reserve, internal 720-second proof deadline, all
operation limits, proof population, serialized isolation and owned cleanup stay
fixed. All other job ceilings and the complete final-source A/B qualification
contract remain unchanged.

## Context

[Hosted A run 41385487207, attempt 1](https://github.com/HodeTech/LearnStack/actions/runs/41385487207)
uses commit `d6281d7f1207acc1b07c20f893ee187f50ca8066` and tree
`4880835d702d56fb41e0644440aae08371d2b327`. The retained public artifact contains
26 matching JSON/HTML report pairs and nine screenshots. The final worker reports
cancelled with its internal stage unknown; a hang and completion with more time
are both unproved. The required complete report census and validations did not
finish. The receipt reports setup of 313.853 seconds, proof of 663.381 seconds
and passing owned cleanup in 12.539 seconds. These observations are not a
successful run or an independent host-wide cleanup proof.

The first-step deadline started at 03:54:17 UTC; the demo was ready at 04:00:12.
The configured external deadline was 04:11:17 UTC; SIGINT near that time is
inferred from the recorded deadline and unchanged timeout contract, rather than
a separate signal-delivery observation. The command returned 124 at 04:11:29.
The 17-minute absolute work limit could end the proof before its own 720-second
limit. The unchanged reserve gives `1,200 - 180 = 1,020` work seconds today;
the proposal gives `1,320 - 180 = 1,140`. The observed first-step-to-ready interval
is about 355 seconds, leaving roughly 665 work seconds today or 785 under the
proposal for a proof whose internal allowance is 720 seconds. Setup variation
and the censored worker still prevent a fit claim. The whole-job and first-step
clocks have different starts; the nominal reserve is not a guarantee of 180
seconds remaining at every point.

The 180-second reserve remains 35 seconds for owner stop, 20 for sandbox
cleanup, 20 for receipt, 60 for upload and 45 for runner/post work. Four quiet
intervals consume 244 seconds of the internal proof. No measured phase cost
identifies the final worker's internal state or proves an optimization. Further
exhaustion calls for a new evidence-based decision; it does not authorize a
silent increase.

## Consequences

### Positive

- Additional outer headroom preserves the full proof and cleanup contracts.
- The internal proof deadline still bounds proof work independently of the job.

### Negative

- A slow or hung public-demo job can consume two more execution minutes per
  attempt. This is not a workflow-latency or billing prediction.
- The increase establishes neither hosted fit nor the final worker's outcome.

### Neutral

- Acceptance, implementation and reviews, and fresh hosted qualification are
  separate gates. The independent clean-install issue is not resolved by time.

## Implementation Notes

After maintainer acceptance, change only the public-demo workflow ceiling and
its first-step deadline, and update its actual-shell controls. Preserve the
reserve, timeout ownership and failure behavior. The P03-3 delivery record owns
the reviewed sequence and qualification; no executable change precedes approval.

## Architecture Tests

The existing `Public_Demo_Preserves_Admission_And_Cleanup` controls exercise the
actual workflow shell and lifecycle; extend those controls for the new outer
window and unchanged reserve. They do not prove hosted fit. Complete same-final-
source hosted A and B, with all required jobs and original evidence independently
audited, remain mandatory.

## References

- [ADR-0056](0056-public-demo-browser-and-lighthouse-enforcement.md).
- [ADR-0075](0075-ordinary-ci-job-window.md).
- [ADR-0082](0082-current-source-ci-execution-windows.md).
- [Testing Standards](../standards/06-testing.md).
- [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md#public_demo_preserves_admission_and_cleanup).
