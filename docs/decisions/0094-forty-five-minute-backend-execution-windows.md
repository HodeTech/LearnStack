# ADR-0094: Forty-Five-Minute Backend Execution Windows

## Status

Accepted

**Date:** 2026-11-06
**Deciders:** @cemil
**Bounded supersession:** [ADR-0082](0082-current-source-ci-execution-windows.md)'s
twenty-five-minute Architecture and thirty-minute ordinary whole-job ceilings,
and the clauses of ADR-0062, ADR-0063 and ADR-0075 they restate. Dated
amendments in ADR-0062, ADR-0063, ADR-0075 and ADR-0082 disclose this change;
their historical bodies, statuses and dates remain intact.

The maintainer directs a 45-minute window so that hosted fit stops being a
recurring cause of failure. Only the two whole-job ceilings and their dependent
observation bounds change. The [Phase 03 delivery log](../roadmap/phase-03-delivery-log.md#p03-4-step-7-forty-five-minute-execution-windows-and-parser-timeout-2026-11-06)
owns the dependent implementation record. Acceptance proves no hosted fit;
fresh complete A/B on the final source remains the qualification gate.

## Decision Drivers

- On PR #41, four of the last five hosted runs lose a shard to its window with
  no failing case: Architecture 0 and 5 in earlier runs, ordinary 4, and in run
  42204664337 ordinary 8, where 294 of 295 selected cases Pass before the
  deadline. A different shard is cut each time.
- Passing shards sit beside the cut-off. The enclosing cut-off is about 26.7
  minutes for ordinary and about 21.7 for Architecture; the longest passing
  jobs observed are 26.4 and 21.1. Ordinary owner 8 takes 21.9 minutes in one
  run and is cut at 26.7 in the next, so the spread is hosted variance, not a
  code change.
- The population grows from 6,437 (P03-1) to about 8,290 cases while the
  six-plus-twelve owner counts and the 25/30-minute windows stay fixed. Each
  owner also repeats about ten minutes of checkout, setup, build and discovery
  before its first case.
- The remaining failures are probabilistic. Correcting one shard at a time
  cannot qualify two complete same-source attempts when each attempt has a
  large chance of losing one shard to variance.
- A window is a ceiling, not a duration. Runs that finish early pay for the
  minutes they use.

## Considered Options

1. **Architecture 45 minutes; ordinary 45 minutes** (chosen). One value for the
   two sharded executions. It moves the cut-off to about 41.7 minutes: about 58%
   over the longest observed ordinary job and about 98% over the longest
   Architecture job.
2. **Keep 25/30 and add owners** (valid, not selected). The count ranges and the
   twenty-execution-VM cap are already consumed, and every added VM repeats the
   fixed setup. It remains the better cure for steady growth.
3. **Smaller increases, such as 35 or 40 minutes** (not selected). They leave
   less headroom against the observed variance and reopen this decision at the
   next growth step.
4. **Re-weight the plan from measured timings only** (valid, complementary, not
   selected here). A replay of the layout A and B assignments with measured
   weights for the 95 unknown selectors lowers the heaviest ordinary owner by
   about 4% (A) and 8% (B). The 60-second default overstates those selectors
   (1,449 measured seconds against 5,700 assumed) but is conservative, so it
   does no harm; the change would need a source-bound plan revision.
5. **Borrow the reserve, extend test deadlines or skip work** (rejected). Each
   weakens a different boundary or loses complete proof.

## Decision

LearnStack gives verified Architecture and ordinary Docker producers a
2,700-second (45-minute) whole-job ceiling. Base and native retain 1,200
seconds; the two aggregates, meta, frontend and public-demo retain their
existing ceilings. The 203-second reserve, test-specific deadlines, fixture
image caps (110 seconds total, 55 per image), owner counts, assignment
algorithm, salt, weights and the exact A/B qualification rule are unchanged.

The window is selected only through the existing closed
`job_seconds(mode, job, counts)` policy after exact mode, job and count
validation, never from a job name or a receipt. The workflow timeouts equal that
policy.

## Context

### Evidence and its limits

The evidence is the retained shard ledgers and job walls of the PR #41 runs.
A shard that reaches its window reports `deadline_exhausted`; its unfinished
cases stay censored and red. The new ceiling removes that class only while a
shard needs less than about 41.7 minutes. It does not shorten any run, make a
slow shard faster or establish a fit guarantee.

### What the longer window costs

- A hung or stuck shard can now hold a runner for up to 45 minutes before the
  workflow timeout fires. Failing cases still fail fast; only the hang path
  lengthens.
- Slow growth is no longer stopped by the ceiling. The aggregate keeps recording
  each producer's wall time, and a producer approaching the ceiling is a signal
  to add owners or re-weight, not to raise the ceiling again.
- [Testing Standards](../standards/06-testing.md) still states a full-CI
  target of fifteen minutes. The ceiling is not a performance target and does
  not change it.

## Consequences

### Positive

- Hosted fit stops being the dominant failure. The remaining cause classes
  (browser start, parser, fixture readiness) can be corrected on their own
  evidence.
- Two complete same-source attempts become achievable without repeated window
  edits.

### Negative

- Longer worst-case feedback when a job hangs, and weaker pressure to keep
  shards short.

### Neutral

- Runner billing follows used minutes. Reruns, queue time and the
  twenty-VM cap are unchanged.

## Implementation Notes

1. Set `ARCHITECTURE_JOB_SECONDS` and `ORDINARY_JOB_SECONDS` to 2,700 in
   `scripts/ci-native-tests.py` and the two producer `timeout-minutes` to 45 in
   the workflow. `scripts/ci-test-shard-results.py` consumes the same policy.
2. Extend the offline controls in `scripts/test-ci-native-tests.py` and
   `scripts/test-ci-test-shard-results.py`; reconcile
   [Testing Standards](../standards/06-testing.md#complete-bounded-ci-assignments),
   [Infrastructure Stack Standards](../standards/20-infrastructure-stack.md) and
   the [catalogue rule](../standards/21-architecture-tests-catalogue.md#ci_test_shards_are_complete_and_bounded).
3. Qualify a complete literal A and a complete B on identical final source, as
   before.

## Architecture Tests

Extend the existing `CI_Test_Shards_Are_Complete_And_Bounded` offline controls;
no second rule is introduced. Otherwise valid Architecture and ordinary
observations and API walls are accepted in (1,200, 2,700] seconds and refused
above 2,700; base and native stay at 1,200. The workflow must carry exactly 45
minutes for the two producers and the former 25/30 values must be refused as
drift. Individual observations allow the inclusive ceiling; disjoint receipt
intervals sum strictly below it.

## Amendments

None.

## References

- [ADR-0062](0062-native-crash-proof-ci-provisioning.md).
- [ADR-0063](0063-complete-bounded-ci-test-shards.md).
- [ADR-0075](0075-ordinary-ci-job-window.md).
- [ADR-0082](0082-current-source-ci-execution-windows.md).
- [Testing Standards](../standards/06-testing.md#complete-bounded-ci-assignments).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
