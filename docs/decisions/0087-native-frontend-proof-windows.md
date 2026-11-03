# ADR-0087: Native Frontend Proof Windows

## Status

Accepted

**Date:** 2026-11-03
**Deciders:** @cemil
**Bounded supersession:** ADR-0086 § Decision's web aggregate window and
unchanged test/process bounds only for the selected orchestration rows below;
ADR-0082 § Decision's test-specific-bounds clause; ADR-0083 § Consequences / Neutral's
preserved test-deadlines clause; and ADR-0084 § Implementation Notes, step 3's
preserved proof-deadlines clause, only for those same selected rows. These
records remain Accepted elsewhere. ADR-0082 § Context's distinct 120-second
provider-startup bound is unchanged. The maintainer authorizes acceptance and
implementation after the review corrections recorded on 2026-11-03.

## Decision Drivers

- Source `c32fe391` passes 3,575 local guarded cases and both review rounds;
  its complete hosted A `41594645147` still fails. Startup completes 12 cases
  and workers completes three before their 120-second group boundaries.
  The web aggregate then exhausts 360 seconds. Unobserved tails are censored,
  not passed or measured durations.
- These groups contain repeated real builds, starts, initialized workers and
  physical retirement controls. The aggregate-only ADR-0086 increase does not
  change their separate nested orchestration bounds.
- The repository is public and the workflow uses `ubuntu-latest`, whose
  documented standard is four vCPU/16 GB. This is configuration evidence, not
  measured utilization or proof that a paid tier would solve the failures.
- Finish the MVP with complete existing proofs and explicit finite budgets;
  preserve actual request, readiness, shutdown and resource-ownership rules.

## Considered Options

1. **One bounded update to the selected test orchestration windows** (chosen).
   Preserve the wrapper gaps and allocate headroom to whole multi-case groups.
2. **Retain the bounds and continue source optimization** (not selected).
   Existing cleanup corrections remain. More fixture/parser/admission redesign
   has no demonstrated sufficient saving and prolongs MVP delivery.
3. **Change runner tier, jobs or proof distribution** (not selected).
   This adds capacity, cost or coverage-accounting decisions. No larger runner
   is selected by the observed timeouts alone.
4. **Skip cases, accept partial runs or extend runtime deadlines** (rejected).
   These weaken different product or evidence boundaries.

## Decision

LearnStack selects the following finite orchestration windows for the existing
native frontend proofs. No case, assertion, internal concurrency witness or
physical cleanup condition changes. Every unlisted bound remains unchanged.

| Owner / scope | Current | Selected |
|---|---:|---:|
| Canonical web package aggregate | 360 s | 600 s |
| Outer ownership-matrix driver and private matrix, each with its existing start | 120 s | 180 s |
| `runFrameworkOwnershipGroupCore` subprocess wrapper | 150 s | 210 s |
| Startup, workers and recovery Vitest cases | 160 s | 220 s |
| SDK and other package aggregates | 180 s | 180 s |
| Node source-control subprocesses | 10 s | 10 s |
| Initialized-router subprocess / case and combined source-control case | 150 / 160 / 25 s | unchanged |
| Frontend CI job | 20 min | 20 min |

Only `runFrameworkOwnershipMatrix`'s outer timer and `runPrivateMatrix`'s
private cancellation timer in `framework-ownership-matrix.mjs` change from
120 to 180 seconds. The separate `runInitialOwnershipControl` timer in
`verify-framework-runtime-ownership.mjs` and the driver timer in
`verify-framework-router-matrix.mjs` both retain 120 seconds. Provider startup's
separate 120-second bound also remains. Do not globally replace 120-second values.
The current two file workers and sequential eight-case registration remain.
The web path selects its aggregate window; manifest names and environment
variables select none. Expiry still fails, cleanup grants no synthetic credit,
and the final expiry check still refuses a late zero exit.

## Context

[The observed failure record](../roadmap/phase-03-delivery-log.md#p03-3-native-proof-orchestration-exhaustion-and-adr-0087-proposal-2026-11-03)
retains the failed source and evidence. Neither the failure nor a local pass
proves a necessary minimum or hosted fit. The chosen 180/210/220 and 600 seconds
are maintainer-judged headroom. In particular, 12 of 17 startup cases and three
of five worker cases do not measure the unfinished cases.

The existing wrapper gaps remain thirty seconds above a group and ten seconds
above its subprocess wrapper. These are observation/cancellation allocations,
not guaranteed physical cleanup time. Outer/private group clocks keep their
different starts. No expired group may turn successful because cleanup later
finishes. The private driver keeps its actual cancellation/receipt ownership.

### Independent observation and accounting boundaries

The matrix `validateEvents` guard and the TypeScript
`validateFrameworkOwnershipGroupCore` receipt validator retain the inclusive
`0..120,000 ms` runtime-event limit. It is measured from that runtime process's
own start, not the group start, and is selected here as a separate retained
observation bound. A 180-second multi-runtime group does not give one runtime
180 seconds of accepted event history. This policy does not attribute the
last hosted failure to that observation limit.

Frontend evidence means equality of discovered source test files and reported
files, plus nonempty, internally consistent, all-passed **Vitest-reported**
case results. The generic runner does not independently enumerate expected
scenario IDs and cannot detect a case conditionally never registered inside
an otherwise reported file. Existing native registration/callback guards
supply their own narrower source checks; they are not a generic scenario census.
Backend case-ID discovery and archive reconciliation are unchanged.

Package expiry invokes SIGKILL on the runner-owned process group. That action
and the controlled-timer child-process double do not prove physical retirement
of every independently detached native descendant, release of every output
reservation, or deletion of private roots. Actual native-family retirement
uses its separate handles, receipts and absence checks. This does not imply
that every package timeout leaks a process; uncertain cleanup receives no credit.

This changes test orchestration, not API request, native readiness, response,
session or shutdown policy. The runtime's five-second shutdown observer,
existing HTTP/body and twenty-second readiness limits, uncertain-work retention,
patch qualification and security release policy remain. Backend windows,
the twenty backend execution-VM cap, public-demo 22/19-minute windows and all
cleanup reserves remain. No runner or billing tier changes.

The web package can use four more minutes within the existing twenty-minute
job ceiling. With the SDK's separate 180 seconds, these package maxima total
780 seconds and leave 420 seconds inside that ceiling for other steps; no fit
or guaranteed reserve follows from that arithmetic. Three sequential groups
near 180 seconds alone use roughly 540 seconds before router/source controls
and wrapper overhead. The web aggregate can therefore expire at 600 seconds
even when every inner control stays within its own bound. The aggregate is not
a reservation of all nested maxima. Queueing and sibling jobs
still determine workflow feedback. Complete final-source A/B and original
archive/case reconciliation remain required. Further exhaustion authorizes no
automatic increase, skipped population or blind retry.

## Consequences

### Positive

- Handles all currently exhausted nested proof windows in one reviewed decision.
- Reuses existing tests, VM count, workflow and archive reconciliation.

### Negative

- A stalled group can take sixty more seconds; a web package can consume four
  more minutes. Workflow delay also depends on queueing and other jobs.
- The new values remain unqualified until complete current-source runs pass.

### Neutral

- No product capability, data schema, runtime timeout or provider policy changes.
- Manual accessibility remains maintainer-deferred and unexecuted.

## Implementation Notes

1. On acceptance, append dated successor disclosures to ADR-0082/0083/0084/0086
   without rewriting their bodies. Move this row to Active; reconcile Phase 03,
   Testing 06, Catalogue 21, CONTRIBUTING and `run-tests-locally`. Current guides
   should link the canonical Testing 06 bounds rather than copy all values.
2. Change the canonical web constant in `scripts/run-frontend-tests.mjs`, both
   selected 120-second timers (outer and private) in `framework-ownership-matrix.mjs`,
   `runFrameworkOwnershipGroupCore`'s subprocess timeout, and only the startup,
   workers and recovery case bounds in their root/helper files. Keep
   `framework-router-faults.cases.ts` unchanged, including 150/160/25-second
   bounds. Keep both runtime-event schema limits and the two separate initial/
   router driver timers at 120 seconds. Update the existing frontend-runner
   controlled-timer cases and one-case deadline source control.
   Preserve every existing refusal, Node count and callback/stdio guard.
3. Run relevant checks; commit before the complete guarded source-stability run.
   Two fresh review rounds and the complete local union precede final-source
   hosted A/B and original-archive validation, then the P03-3 PR. No new private
   benchmark, source freezer, diagnostic pipeline or speculative optimization.

## Architecture Tests

Extend existing controls, not the rule count. Canonical web selects exactly
600 seconds; SDK/lookalikes remain 180. The real matrix callback retains its
private cancellation-owner path and late-success refusal at 180 seconds.
Source/stdio/callback controls preserve all cases and independent runtime
limits. All discovered web/SDK test files must be reported, and every reported
case must be internally accounted for and passed with zero skips/todos. No
independent expected-scenario-ID set is claimed. The retained runtime-event
schema bounds remain 120,000 ms even under 180-second groups. Existing
registered limits outside this table remain unchanged.

## References

- [ADR-0082](0082-current-source-ci-execution-windows.md),
  [ADR-0083](0083-owned-next-response-compression-cleanup.md),
  [ADR-0084](0084-owned-frontend-runtime-process-families.md),
  [ADR-0086](0086-native-web-test-package-window.md).
- [Testing 06](../standards/06-testing.md#frontend-guarded-execution).
- [Catalogue 21](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable).
- [GitHub runner specifications](https://docs.github.com/en/actions/how-tos/write-workflows/choose-where-workflows-run/choose-the-runner-for-a-job).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
