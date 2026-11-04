# ADR-0092: SDK Completion and Frontend Job Windows

## Status

Accepted

**Date:** 2026-11-04
**Deciders:** @cemil
**Bounded supersession:** ADR-0090's SDK 90/100-second pair and
retained 180-second aggregate, as preserved by ADR-0091; ADR-0088's frontend
25-minute job ceiling, inherited through those successors. All three records
remain Accepted outside this scope. Accepted on 2026-11-04 under the maintainer's
explicit delegation to resolve the remaining P03-3 closure decisions and finish.
Implementation and full hosted qualification remain separate; merge stays with
the maintainer. The delegation removes repeated approval holds for necessary
closure choices, without granting credit to failed or unexecuted proof.

## Decision Drivers

- PR run `41709127582` executes unchanged head `ac8f4aae`, merge source
  `32f7e7e0`, tree `f7321b39` and base `e55aa5d7`. Attempts 1 and 3 each pass
  all 112 web files and 3,421 reported cases, then report the same SDK cold-lint
  case failure at 93,308/93,343 ms. The SDK package is subsequently stopped by
  its 180-second aggregate. The generic family error does not prove which
  command failed or that its ninety-second stop request caused the failure.
- Attempt 2 passes the entire frontend job and all 3,575 reported cases:
  web 3,421/112 files and SDK 154/nine files. SDK's Vitest interval is 145.05
  seconds. Different backend failures still prevent whole-run qualification.
  Neither a passing interval nor the failed intervals measure the unfinished
  tail or establish sufficient new settings.
- The unchanged SDK package passes locally in 77.94 seconds, 154/154. Native
  and public-demo pass in all three attempts. The prior VM-shape and browser
  discovery refusals remain independent observations, not repaired causes.
- Extending only the SDK inner pair leaves the exhausted aggregate first;
  extending only the aggregate retains the failing inner pair. This requires
  one coordinated choice, with explicit outer-job allowance, rather than
  another automatic sequence of individual increases or blind retries.

## Considered Options

1. **One coordinated SDK/package/job allowance** (chosen).
   Preserve every actual cold command, counterfactual and ownership assertion.
2. **Retain current settings and diagnose further** (viable, not selected).
   Keep the ready PR available for product review with failing qualification;
   no source repair or timing minimum is currently established.
3. **Change only the inner pair or only the SDK aggregate** (not selected).
   The existing evidence includes both a cold-case failure and aggregate expiry.
4. **Skip/narrow controls, reuse output, add runners/caches or more workers**
   (rejected). These alter the proof or execution topology rather than its
   bounded orchestration allowance.

## Decision

LearnStack selects the following four settings as maintainer-judged headroom,
not measured minima, a causal repair or a hosted-fit guarantee.

| Owner | Current | Selected |
|---|---:|---:|
| Canonical `packages/sdk` aggregate | 180 s | 360 s |
| `checkColdLintOutput` shared sequential typecheck/lint stop request | 90 s | 150 s |
| Both existing cold-lint Vitest cases | 100 s | 160 s |
| Frontend CI job | 25 min | 30 min |

The SDK setting is selected only from the discovered canonical workspace path
`packages/sdk`, alongside the existing `apps/web` selection. Web remains 900
seconds; every other package remains 180. Manifest names, caller/environment
values and receipts select no allowance. Existing symlink/path refusals remain.

Typecheck and lint stay sequential within each separate uncached/cached control.
The uncached guard accepts; the cached counterfactual refuses `output_unowned`.
Actual commands, private capture, unchanged-source/output/lease assertions,
physical-family joins and uncertain-root retention remain mandatory.

The AbortController requests stop; it does not bound actual return/retirement.
Vitest's asynchronous threshold includes preparation and grants no cleanup
credit. Two stop settings sum to 300 seconds; two case thresholds to 320.
The 360-second SDK aggregate reserves neither sum plus setup/joins/other work.
The ten-second pair gap and forty-second arithmetic remainder are not reserved
cleanup intervals. The package's SIGKILL request does not prove retirement of
independently detached families or safe reservation/root removal.

For today's two discovered tested packages, 900 + 360 = 1,260 nominal seconds;
the 1,800-second job leaves 540 nominal seconds for setup, build, other proof
and overhead. This is not a reservation or completion guarantee. Each package
and the workflow start its own clock. Other packages are still discovered and
executed if introduced; no fixed package-count shortcut is added.

All other settings remain: two web workers, provenance 90/100, router 20/90 and
five 10-second siblings, matrix 180/210/220, runtime-event 120, pnpm probes 15,
product HTTP/readiness/shutdown, all backend jobs/native inventory/203-second
reserve and public-demo 22/19. No authority, cache, runner or process topology
is added. This record authorizes no automatic further increase or retries.

## Context

The [hosted outcome and plan record](../roadmap/phase-03-delivery-log.md#p03-3-adr-0091-hosted-outcomes-and-sdk-completion-proposal-2026-11-04)
distinguishes partial passes, the passing second-attempt frontend and repeated
SDK failure/aggregate expiry. PR #40 stays ready for parallel product review
and unmerged. Failed attempts supply no qualification credit. Complete fresh
same-source A then whole-run B, with the existing original-archive verifier,
remain required after implementation. Manual accessibility remains deferred
and unexecuted; this proposal changes no approval or evidence classification.

## Consequences

### Positive

- Treats the observed inner/aggregate conflict as one bounded package decision.
- Preserves the real cold prefix, distinct counterfactuals and complete reporting.

### Negative

- SDK aggregate refusal may arrive three minutes later; frontend cancellation
  five minutes later. The inner stop/failure settings increase by sixty seconds.
- Stalled/slow work and independent backend/browser/VM refusals can still fail.
  These settings do not establish why the recorded failures occurred.

### Neutral

- Application policy, coverage, discovery, concurrency and physical ownership stay.
- Acceptance does not qualify the PR or permit merge.

## Implementation Notes

1. After approval, append dated successor disclosures to ADR-0088, ADR-0090
   and ADR-0091 without rewriting their bodies or preassigning amendment numbers.
   Earlier navigation reaches this record through those successors. Reconcile
   [Testing 06](../standards/06-testing.md#frontend-guarded-execution): split
   SDK 360 from other packages 180, update the SDK pair and frontend 30-minute
   row, and reconcile the arithmetic. Update both
   [reporting](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable)
   and [cold-prefix ownership](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned)
   carriers, CONTRIBUTING/run-tests-locally canonical pointers, index and Phase 03
   records. Add no architecture rule or status promotion.
2. Change only the four selected settings in `scripts/run-frontend-tests.mjs`,
   `frontend/packages/sdk/scripts/test-cold-lint-output.mjs`,
   `frontend/packages/sdk/src/cold-lint-output.test.ts` and the frontend job in
   `.github/workflows/ci.yml`. Update behavioral controls in
   `frontend/apps/web/src/test/frontend-runner.test.ts` and the exact frontend
   expectation in `frontend/apps/web/src/test/public-demo-ci.test.ts`; preserve
   every other job expectation. Do not replace other 90/100/180/25 literals.
3. Run relevant existing controls and the complete committed stable-source
   frontend guard, then two fresh implementation reviews. Push once for the
   normal complete PR A. Verify A originals before rerunning all jobs for B;
   verify B originals on the same head/merge-source/tree/base. No parallel manual
   workflow, partial rerun, combined failed attempts or merge.

## Architecture Tests

Extend the existing controlled-timer runner cases: actual discovered SDK selects
360, web retains 900, SDK-name/path lookalikes and other packages retain 180;
symlinks remain refused. Use actual discovery/expiry behavior, not a mirrored
literal-only test. The workflow test must require frontend 30 and every other
job's existing bound. Both real cold controls still require their distinct
`accepted`/`output_unowned` results, source integrity, actual families/leases
retired and private root removed. All discovered files and Vitest-reported cases
must pass with zero skips/todos; this is not independent scenario-ID discovery.

## References

- [ADR-0090](0090-sdk-cold-prefix-proof-settings.md) and
  [ADR-0091](0091-native-inventory-phase-allocations.md).
- [Frontend completion policy](0088-frontend-ci-completion-windows.md).
- [Testing 06](../standards/06-testing.md#frontend-guarded-execution).
- [Existing reporting owner](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable)
  and [cold-prefix owner](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
