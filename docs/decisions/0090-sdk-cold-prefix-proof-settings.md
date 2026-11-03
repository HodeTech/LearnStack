# ADR-0090: SDK Cold Prefix Proof Settings

## Status

Accepted

**Date:** 2026-11-03
**Deciders:** @cemil
**Bounded supersession:** ADR-0089's retained unlisted settings
only for the SDK cold typecheck/lint prefix's sixty-second stop request and
its two ninety-second Vitest cases. The SDK aggregate remains 180 seconds;
ADR-0089's provenance settings and all other inherited settings remain.
ADR-0088 retained this pair; its successor ADR-0089 preserved it while changing
only provenance. This record narrows that retained SDK pair without changing
either predecessor's historical Decision. Accepted after maintainer review on
2026-11-03; implementation and hosted qualification remain separate.

## Decision Drivers

- PR #40's first complete-source A, run `41647864236` on head `9135e6b3`
  and merge source `5eeccd12`, passes all 112 discovered web files and 3,421
  reported web cases in 709.21 seconds. This includes ADR-0089's provenance
  wrapper; it is not whole-run qualification.
- SDK finishes nine discovered files in 126.67 seconds: eight files pass,
  one fails, 152 reported cases pass and two fail. Both failures are the
  existing cold-lint controls, at 63,386 and 62,178 ms. They report the generic
  process-family refusal. The SDK 180-second aggregate does not expire.
- Each cold-lint control prepares a qualified private source and runs the real
  sequential workspace typecheck and lint commands under one sixty-second
  AbortController timer. Its Vitest setting is ninety seconds. The observed
  durations are consistent with that stop request, but the generic error does
  not independently prove timer cause, stage or the uncompleted tail.
- The two controls pass in the recorded local full guard. Local fit supplies
  no hosted minimum. No verified product defect, unsafe compiler output or
  justification for a freshness shortcut follows from this hosted result.

## Considered Options

1. **One paired cold-prefix allowance** (chosen).
   Keep the real commands, private source, positive/cache counterfactual and
   all source/ownership/output assertions with more orchestration allowance.
2. **Keep the current settings and continue diagnosis** (viable, not selected).
   The current draft remains available for product review with failing CI;
   additional evidence may justify a repair, but no such repair is proved.
3. **Skip controls, narrow the workspace commands or reuse verified results**
   (rejected). These weaken the actual cold CI-prefix proof or freshness.
4. **Increase SDK/package/job limits, workers or runner capacity** (not selected).
   The current package has not exhausted its 180-second guard; no such wider
   change follows from this evidence.

## Decision

LearnStack changes only these two settings as maintainer-judged
headroom, not measured minima or a claimed root-cause repair.

| Owner | Current | Selected |
|---|---:|---:|
| `checkColdLintOutput` shared typecheck/lint stop-request timer | 60 s | 90 s |
| Both existing `cold-lint-output.test.ts` Vitest cases | 90 s | 100 s |

SDK retains its 180-second aggregate. Two controls can together approach that
window before setup, joins and other work; 180 seconds reserves none of their
individual maxima. This choice therefore supplies no complete-package fit
claim.

The first literal lives in
`frontend/packages/sdk/scripts/test-cold-lint-output.mjs`; the second is the
shared `test.each` setting in
`frontend/packages/sdk/src/cold-lint-output.test.ts`. No other literal is
replaced. Typecheck and lint remain sequential within each control; the
positive and cache counterfactual remain separate controls.

An AbortController expiry requests stop through the existing actual process
family. It does not prove physical retirement, release leases or remove the
private root. Existing actual-family observation and uncertain-ownership
retention remain. Vitest's failure setting grants no physical cleanup credit;
the ten-second numeric gap is not a reserved join/cleanup interval.

Web remains 900 seconds, frontend CI twenty-five minutes, provenance
90/100 seconds, both pnpm probes fifteen seconds, two web file workers and all
matrix/router/event/product settings remain. Native scan twenty seconds,
base/native twenty-minute jobs, backend reserve 203 seconds and public-demo
22/19-minute policy remain unchanged.

## Context

[The PR failure record](../roadmap/phase-03-delivery-log.md#p03-3-pr-40-web-pass-sdk-cold-prefix-failure-and-adr-0090-proposal-2026-11-03)
distinguishes the completed web result, two SDK failures and a separate native
pre-launch inventory refusal. That native receipt folds multiple subprocess
failure classes together. Its separately reviewed diagnostic refinement uses
the existing classifier without changing scan/time/acceptance policy. Neither
that refinement nor this proposal claims to repair its unproved cause.

The draft PR supports parallel product review. Failed/partial attempts receive
no qualification credit. After acceptance and implementation, complete fresh
same-source A then whole-run B and existing original-archive reconciliation
remain required. No automatic further increase or blind qualification retry
is authorized by this record.

## Consequences

### Positive

- Preserves the real cold typecheck/lint commands and both output counterfactuals.
- Keeps the existing aggregate/job owners and all product runtime boundaries.

### Negative

- The stop request is thirty seconds later and Vitest failure ten seconds later.
- The unfinished hosted tail and necessity/sufficiency remain unmeasured;
  the unchanged SDK aggregate can still stop the package first.

### Neutral

- No production authority, dependency, cache, runner or diagnostic pipeline is added.
- Discovered-file/reported-case accounting and zero skips/todos remain.
- Manual accessibility remains maintainer-deferred and unexecuted.

## Implementation Notes

1. Accept/index this record only after approval; append a dated ADR-0089
   successor disclosure, preserving its body. Reconcile the existing
   [Testing 06 table](../standards/06-testing.md#frontend-guarded-execution),
   [catalogue rule](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable)
   and Phase 03 pointers. Add canonical rows
   `SDK cold typecheck/lint stop-request timer | 90 s` and
   `Its asynchronous Vitest failure threshold | 100 s`. Link the direct
   [cold-prefix behaviour owner](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned)
   alongside the general reporting rule. Earlier successors navigate through
   ADR-0089.
2. Change only the two selected literals. Keep actual commands, strict capture,
   source/extra-input hashes, no-cache positive/cached refusal, output/lease
   assertions, physical family joins and uncertain-root retention unchanged.
3. Run relevant existing controls and full stable-source frontend guard; complete
   both fresh implementation reviews. Push the final source once for the normal
   PR workflow, without a duplicate manual run. Bind complete A then whole-run B
   and original-archive reconciliation to exact head/merge-source/tree/base.
   Keep the PR draft/unmerged until the maintainer chooses its review/merge path.

## Architecture Tests

Use the two existing real cold-prefix controls, including the enabled-cache
counterfactual. Both require commands succeeded, expected cache, unchanged
sources, actual families/leases retired and root disposed. The uncached positive
control requires guard `accepted`; the cache-enabled counterfactual requires
`output_unowned` refusal. These are distinct proof obligations.
A timer or CLI exit alone supplies none of these assertions. The generic guard
still requires all discovered SDK/web files and all reported cases Passed with
zero skips/todos. Add no mirrored literal-only test or new architecture rule.

## References

- [ADR-0089](0089-provenance-control-timer-settings.md).
- [Frontend process families](0084-owned-frontend-runtime-process-families.md).
- [Testing 06](../standards/06-testing.md#frontend-guarded-execution).
- [Catalogue reporting rule](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable)
  and [direct cold-prefix owner](../standards/21-architecture-tests-catalogue.md#next_response_cleanup_is_installed_and_owned).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
