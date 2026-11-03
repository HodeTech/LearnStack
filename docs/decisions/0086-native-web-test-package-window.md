# ADR-0086: Native Web Test Package Window

## Status

Proposed

**Date:** 2026-11-03
**Deciders:** @cemil
**Bounded supersession on acceptance:** ADR-0082's unchanged test-specific
bounds only for the web package's aggregate 180-second guard. ADR-0082 remains
Accepted for every other execution, observation and qualification boundary.
No executable deadline changes before maintainer approval.

## Decision Drivers

- Complete hosted A runs on `fcbfa35d` and `9480a48c` fail frontend tests;
  their remaining jobs are cancelled and neither run qualifies P03-3.
- Lowering file workers from four to three does not qualify hosted execution.
  Local complete guarded runs with three and two workers also exhaust the
  180-second aggregate package window. The four priority native proof files
  pass in each local run before the later aggregate cancellation; neither run
  provides a complete package report or a web/SDK union.
- The package contains real copied Next builds, native process families and
  physical retirement controls alongside ordinary unit tests. Its total wall
  time is not the pure-unit speed target in Testing 06.
- Close the MVP using existing tests and CI, preserving every discovered case,
  internal concurrency witness, operation deadline and cleanup requirement.

## Considered Options

1. **Two web file workers and a 360-second web package window** (chosen).
   Bounded maintainer-judged headroom permits less competing fixture work.
   It is not a measured minimum or proof of hosted fit.
2. **Retain 180 seconds and continue fixture optimization** (not selected).
   The latest runs do not fit. More speculative proof infrastructure work
   delays MVP delivery and requires additional equivalence/lifetime evidence.
3. **Split jobs, buy larger runners or redesign test ownership** (not selected).
   These add topology, capacity or coverage-accounting changes to the release.
4. **Skip cases, weaken assertions, retry until green or extend inner deadlines**
   (rejected). These do not establish the required complete, bounded proof.

## Decision

LearnStack gives the validated `frontend/apps/web` test package a 360-second
aggregate execution window and limits its parallel test-file workers to two.
Every other package, including the SDK, retains 180 seconds. Selection uses
its discovered canonical workspace path, never a self-declared package name,
receipt, caller argument or environment override. All individual test/process,
HTTP/readiness and shutdown bounds, internal concurrency controls, complete
file/case discovery and zero-skip/todo requirements remain unchanged.

## Context

[The current failure record](../roadmap/phase-03-delivery-log.md#p03-3-frontend-envelope-exhaustion-and-adr-0086-proposal-2026-11-03)
distinguishes the two hosted source revisions and local worker experiments.
Hosted startup/recovery HTTP observations and a router control fail at their
own bounds; aggregate exhaustion follows. Reduced concurrency and a longer
aggregate window are a proposed scheduling/budget combination, not a proven
explanation for those individual failures. Fresh hosted execution must still
pass them under their unchanged bounds.

The guard starts when the workspace test subprocess is spawned; the web
command includes its existing ingress compilation. This is neither an HTTP
budget nor cleanup reserve. A stuck web command can run up to three additional
minutes; total workflow feedback also depends on other jobs and queueing.
The frontend job stays at twenty minutes. Backend windows, the twenty execution
VM cap, 203-second backend reserve, public-demo 22/19-minute windows and its
180-second nominal reserve remain unchanged.

If the final source still fails, this decision authorizes no automatic further
increase or retries that discard failures. The recorded failure is investigated
as a delivery blocker. Complete same-source A/B and original-archive validation
remain required before packet closeout; manual accessibility stays deferred
and unexecuted.

## Consequences

### Positive

- Keeps all coverage in the existing discovered workspace/CI chain.
- Bounds aggregate test work without a new runner, benchmark or worker topology.

### Negative

- A hung web test package may delay failure by three more minutes.
- Two workers may lengthen ordinary test execution; 360 seconds is unqualified
  headroom until the complete current-source runs pass.

### Neutral

- No product permission, data schema, runtime request or provider policy changes.
- Acceptance, implementation and actual complete qualification remain distinct.

## Implementation Notes

1. On acceptance, append a dated navigation disclosure to ADR-0082, retaining
   its historical body. Move this decision to Active in the decision index;
   update Phase 03's current Status and append its delivery record.
2. Update `scripts/run-frontend-tests.mjs` with closed canonical-path selection,
   and `frontend/apps/web/vitest.config.ts` with two workers. Extend
   `frontend/apps/web/src/test/frontend-runner.test.ts` using controlled timers
   to prove exact web/non-web bounds, timeout failure and wrong-identity refusal
   without six-minute real sleeps. Preserve report/discovery and planted
   failure/skip/todo/omission controls.
3. Reconcile Testing 06's frontend runner clause, Catalogue 21's existing
   `No_Architecture_Test_Is_Skippable` entry, the frontend test instructions in
   `run-tests-locally`, and the guarded-runner description in CONTRIBUTING.
   Run relevant tests and document checks, both fresh implementation review
   rounds, then one complete final-source A and B with the existing archive
   reconciler. No new private diagnostic pipeline is part of this plan.

## Architecture Tests

Extend existing frontend-runner controls rather than inventing a new rule.
Canonical web path selects exactly 360 seconds; SDK/other validated packages
retain exactly 180. Renaming a package, environment variables and external or
symlinked paths cannot obtain the web window. Expiry still fails and uses the
existing owned-process cleanup. All previous real discovery and refusal cases
remain. A complete guarded local web/SDK union and complete hosted A/B must pass;
partial native successes do not qualify the package.

## References

- [ADR-0082](0082-current-source-ci-execution-windows.md).
- [Testing 06](../standards/06-testing.md#flaky-tests).
- [Architecture test catalogue](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
