# ADR-0088: Frontend CI Completion Windows

## Status

Accepted

**Date:** 2026-11-03
**Deciders:** @cemil
**Bounded supersession:** ADR-0087's web aggregate, retained
Node-control and combined-source-control bounds, and twenty-minute frontend
job; the same retained frontend/test-window clauses in ADR-0086/0083/0084;
and only the frontend application of ADR-0075/0082/0085's other-job boundaries.
Those records remain Accepted elsewhere. The maintainer authorizes acceptance
and implementation after the review corrections recorded on 2026-11-03. The independent healthy-peer fixture correction is not
this policy and proceeds under the existing response-cleanup contract.

## Decision Drivers

- Complete hosted A `41621718722` on `cfa58aad` exhausts the web 600-second
  package window. Five of 112 discovered files have no completed-file marker.
  Finished markers and partial results do not qualify the package.
- The combined native spec finishes eight cases in 492.879 seconds with one
  reported failure: the 31-control Node subprocess exceeds ten seconds after
  all 31 per-control success lines. Its process completion/summary is not
  proved. The evidence does not establish why it missed the deadline.
- Another failure is an independent stock-baseline bodyless peer-lifetime
  assumption. Its verified fixture correction preserves all 124 controls and
  strict patched cleanup; it does not establish aggregate fit.
- Finish existing complete proofs without more workers, runner tiers, shards,
  reduced populations, weaker physical ownership or speculative parser caches.

## Considered Options

1. **One closed frontend completion-window update** (chosen).
   Add finite orchestration headroom while retaining product runtime policy.
2. **Keep the current clocks and optimize more source** (not selected).
   No verified redundant-work correction explains the Node deadline or provides
   sufficient aggregate savings. The independent fixture correction remains.
3. **Change runners, split jobs or add concurrency** (not selected).
   These add capacity/cost or coverage-accounting decisions and require their
   own evidence; the current failures alone select none of them.
4. **Retry the unchanged source, omit tests or count partial passes** (rejected).
   These do not establish complete bounded qualification.

## Decision

LearnStack selects the following closed orchestration timer settings.
They are maintainer-judged headroom, not measured minima or hosted-fit evidence.
Every unlisted bound, test, assertion and completion requirement remains.

| Owner / setting | Predecessor | Selected |
|---|---:|---:|
| Discovered canonical `apps/web` package | 600 s | 900 s |
| `framework-router-fault.test.mjs` timeout signal threshold in its six-control caller | 10 s | 20 s |
| That combined router source-control Vitest failure threshold | 25 s | 90 s |
| `frontend` CI whole job | 20 min | 25 min |

The Node exception is selected only from the existing closed literal script
list in `framework-router-faults.cases.ts`. Other Node controls stay at ten
seconds. These `execFileSync` timeout settings request a termination signal;
the synchronous call can keep waiting until the child exits. They are not
return-time maxima. Vitest cannot interrupt the synchronous callback; its
90-second setting is a failure threshold, not an independent preemptive limit.
The 20 + 5 × 10 + 20 = 90 arithmetic explains the selected allowance only; it
reserves no guaranteed completion or observation interval. Its initialized-router
sibling stays at 150/160 seconds.

The independent package-runner timer still requests process-group SIGKILL and
refuses expiry; the workflow job applies its own cancellation allowance. These
outer owners can stop/report work despite a blocked Vitest callback, but neither
signal invocation proves physical retirement of detached families. No independent
hard inner watchdog or stronger cleanup mechanism is selected by this decision.

SDK/other packages stay at 180 seconds; file workers stay at two. Selected
matrix groups and wrappers stay at 180/210/220 seconds. The independent
runtime-event schemas stay at inclusive 0..120,000 ms from each runtime start;
initial/router drivers, provider startup, runtime HTTP/readiness/shutdown,
backend/public-demo windows, VM counts and cleanup reserves do not change.

## Context

[The dated failure and correction package](../roadmap/phase-03-delivery-log.md#p03-3-adr-0087-hosted-failure-and-adr-0088-proposal-2026-11-03)
retains exact source and original-log identity. Required frontend failure
prevents qualification; the remaining run is cancelled, not credited as success.
No failed-only retry or repeated historical archive audit is selected.

The Node diagnostic proves 31 sequential success lines, not clean process
exit, a complete TAP summary or a teardown leak. One unchanged local control
passes 31/31 in 2.778 seconds, so that reproduction does not identify the hosted
cause. Source inspection finds no verified resource or redundant-work defect.
Extra time is an explicit policy choice, not a claimed repair of such a defect.

A focused ordinary-peer ordering probe reproduces stock handler retention after
bodyless peer auto-close; the patched counterpart joins its encoder and handler.
That mechanism does not retroactively reveal the exact hosted event trace.
Owned keepalive agents now keep healthy comparison peers through actual response
finish; explicit peer-close cases and all wire/header/patch assertions remain.

The frontend job starts at 10:44:12 UTC and its test step at 10:47:29 on this
attempt: 197 seconds precede the step. This is not the package-owner start or a
future setup guarantee. The test step lasts 601 seconds. Five uncompleted-file
markers censor their remaining durations; they establish no necessary minimum.

Selected package timer settings sum to 900 + 180 = 1,080 seconds. A 1,500-second job leaves
420 nominal seconds for all other steps, the same arithmetic gap as the previous
600 + 180 inside 1,200. No guaranteed reserve or total nested-maxima reservation
follows. Setup variability, inner failures and scheduling still determine fit.
Full same-source A/B and existing original-archive reconciliation remain required.
Further failure grants no automatic timer increase or blind retry.

## Consequences

### Positive

- Keeps the existing complete file/report accounting and qualification path.
- Makes the remaining four related clock choices reviewable together.

### Negative

- The Node signal threshold is ten seconds later; the combined Vitest failure
  setting is 65 seconds later. Web expiry and job cancellation allowances each
  gain five minutes. Actual synchronous return may outlast the inner settings.
- Workflow feedback also depends on concurrency/queueing. Sufficiency remains
  unproved until complete final-source hosted execution succeeds.

### Neutral

- No product API, schema, permission, runtime deadline, runner or worker change.
- Frontend evidence remains discovered files plus Vitest-reported cases, without
  an independent generic scenario-ID census. Backend case-ID proof is unchanged.
- Package-group SIGKILL proves no universal detached-family retirement, release
  or private-root cleanup. Existing native ownership evidence remains separate.
- Manual accessibility remains maintainer-deferred and unexecuted.

## Implementation Notes

1. Acceptance indexes this record and appends dated successor disclosures
   to ADR-0075/0082/0083/0084/0085/0086/0087 without changing historical bodies.
   Reconcile canonical [Testing 06](../standards/06-testing.md#frontend-guarded-execution),
   [Catalogue 21](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable),
   Phase 03 and current contributor/test-guide pointers. Add no new rule count.
2. Change only `scripts/run-frontend-tests.mjs`, its controlled-timer expectation
   in `frontend-runner.test.ts`, the one closed script selection and combined
   case timeout in `framework-router-faults.cases.ts`, and only the frontend
   whole-job ceiling in `.github/workflows/ci.yml`. Include
   `frontend/apps/web/src/test/public-demo-ci.test.ts`: change only its frontend
   expectation to 25, retaining the full map and mutants for other job ceilings.
   Extend `framework-proof-closed-boundary.test.ts`'s existing control for the
   closed Node exception and unchanged siblings; keep the same rule/case count.
   Preserve exact canonical identity and the existing timeout/refusal controls.
   No other 10/25/600/1200 literal is globally replaced.
3. Run relevant checks and commit before the full guarded source-stability run.
   Complete both fresh review rounds, then new whole A/B on one source using
   the existing original-archive reconciler. Open the P03-3 PR for maintainer
   review only after qualification; do not merge it.

## Architecture Tests

Extend existing controls without deleting cases: canonical web selects exactly
900 seconds, SDK/lookalikes 180; only the literal 31-control script selects 20
seconds and its combined case 90, all five sibling scripts remain ten. The
frontend job alone selects 25 minutes; `public-demo-ci.test.ts` pins that
expectation alongside every unchanged job ceiling and its wrong-bound mutant.
Threshold controls do not claim preemption of synchronous work or physical
completion by the inner deadline. Preserve the full eight-case native spec,
all Node counts, runtime-event 120-second schemas, independent driver and
runtime boundaries, timeout failure, late-success refusal, report-file equality
and zero skip/todo checks. Qualification requires complete outcomes, not merely
31 success lines or a zero CLI exit.

## Amendments

### Amendment 1 — Existing stdio control fixture binding (2026-11-03)

The closed Node timeout expression now references the existing literal `script`
loop value. The existing `framework-proof-stdio.test.ts` VM evaluates that real
options object; its fixture must bind the selected literal script too. The first
focused run exposed two reference errors at this dependent control, rather than
a product or ownership failure. Include this one test-fixture binding alongside
the named implementation carriers. All twelve subprocess sites, both stdio
controls and literal channel assertions remain; no production binding, caller,
loader exemption or additional timeout is introduced. The Decision and selected
values are unchanged. The acceptance/implementation delivery records own checks
and complete qualification; this amendment supplies no hosted evidence.

### Amendment 2 — Provenance timer successor (2026-11-03)

[ADR-0089](0089-provenance-control-timer-settings.md) narrowly supersedes this
record's retained unlisted settings only for the existing provenance wrapper:
a ninety-second Node termination-signal threshold and one-hundred-second
Vitest failure setting. This record's four selected settings and historical
Decision remain unchanged. Both pnpm discovery probes retain fifteen-second
signal settings. Neither synchronous setting guarantees return, preemption or
physical cleanup. The Phase 03 Status block owns implementation and complete
same-source qualification; an explicitly authorized draft PR supplies no
qualification or merge approval.

## References

- [ADR-0087](0087-native-frontend-proof-windows.md),
  [ADR-0086](0086-native-web-test-package-window.md),
  [ADR-0083](0083-owned-next-response-compression-cleanup.md),
  [ADR-0084](0084-owned-frontend-runtime-process-families.md).
- [ADR-0075](0075-ordinary-ci-job-window.md),
  [ADR-0082](0082-current-source-ci-execution-windows.md),
  [ADR-0085](0085-public-demo-ci-execution-window.md).
- [Testing 06](../standards/06-testing.md#frontend-guarded-execution),
  [Infrastructure 20](../standards/20-infrastructure-stack.md),
  [Catalogue 21](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
