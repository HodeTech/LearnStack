# ADR-0089: Provenance Control Timer Settings

## Status

Proposed

**Date:** 2026-11-03
**Deciders:** @cemil (approval pending)
**Bounded supersession on acceptance:** only ADR-0088's retained unlisted
settings as applied to the provenance wrapper's 30-second signal setting and
35-second Vitest setting. ADR-0088's four selected settings remain unchanged.
Earlier records retain their existing successor links through ADR-0088; this
record changes no other inherited clause. No executable setting changes before
maintainer approval.

## Decision Drivers

- Hosted A `41638792928` on `c944b453` again reports only the provenance
  wrapper's thirty-second timeout. All 112 web files finish: 111 pass and one
  fails, with 3,420 passed cases and one failure in 681.27 seconds. The
  900-second package does not expire. Public-demo succeeds; the cancelled
  whole run does not qualify.
- The independently reproduced JSON marker race is fixed and tested before
  this attempt. The subsequent timeout remains unattributed; neither log
  exposes the inner command's progress. Another speculative correctness fix
  or source-result cache has no evidence basis.
- The wrapper encloses setup and 112 serial controls. Setup qualifies/copies
  59 installed subjects; real gates retain fresh verification. Qualified pnpm
  discovery also performs two sequential external probes with fifteen-second
  signal settings. These components provide no complete-suite timing bound.
- The same current executable source passes the complete local guard; its
  provenance wrapper takes 23.519 seconds under the retained thirty-second
  setting. Local fit is not hosted fit or proof of a necessary minimum.

## Considered Options

1. **One paired provenance timer update** (chosen proposal).
   Add finite signal/failure allowance inside the existing package/job owners.
2. **Keep 30/35 and continue diagnosis** (viable, not selected).
   Preserve the fixture correction. More targeted evidence may reveal a cause,
   but none currently justifies another source repair or freshness shortcut.
3. **Change runners, workers, job layout or verifier caching** (not selected).
   These widen capacity, coverage or trust decisions beyond the single failure.
4. **Omit controls, accept the partial run or retry unchanged source** (rejected).
   These do not supply complete source-bound qualification.

## Decision

LearnStack selects only these settings on acceptance, as maintainer-judged
headroom rather than measured minima or a claimed repair of the hosted cause.

| Provenance wrapper setting | Current | Selected |
|---|---:|---:|
| `execFileSync` termination-signal threshold | 30 s | 90 s |
| Its Vitest failure setting | 35 s | 100 s |

Both live in `frontend/apps/web/src/test/framework-provenance.test.ts`. The
command, environment, working directory, buffer allowance and all 112 TAP
completion/refusal assertions remain. No other timer is globally replaced.

The Node setting requests termination and can keep waiting for child exit;
Vitest cannot preempt the synchronous callback. The ten-second numeric gap is
not a reserved return, observation or cleanup interval. The independent
900-second package owner and 25-minute workflow job retain their existing
expiry/cancellation roles without detached-family retirement credit. No hard
inner watchdog or stronger physical-cleanup promise is selected.

ADR-0088's web 900 seconds, closed 31-control Node 20 seconds, combined router
case 90 seconds and frontend job 25 minutes remain. SDK 180 seconds, five
router Node siblings ten seconds, two workers, matrix/group/event/runtime
settings, backend/demo jobs and all cleanup reserves remain unchanged. The
unrelated combined router case is not this provenance wrapper.

## Context

[The current-source failure record](../roadmap/phase-03-delivery-log.md#p03-3-provenance-wrapper-timeout-and-adr-0089-proposal-2026-11-03)
retains exact source and original-log identity. The preceding publication fix
removes a demonstrated empty-JSON race. Passing targeted/full local controls
and source review does not establish what the hosted child was doing at thirty
seconds, whether it stalled or when it would have completed. The new settings
make no such claim.

The package has not exhausted 900 seconds on either recent attempt. This
choice adds no package/job time, resource tier or concurrency. The selected
pair is not a guarantee that the 112-control command fits, nor permission for
a further automatic increase. Complete fresh same-source whole A/B and existing
original-archive reconciliation remain required; failed/cancelled predecessors
receive no credit.

## Consequences

### Positive

- Keeps all actual source, installation, ownership and counterfactual checks.
- Leaves the enclosing CI and all product runtime policy unchanged.

### Negative

- Signal request is sixty seconds later; the Vitest failure setting is 65
  seconds later. Actual synchronous return can outlast either setting.
- Extra allowance may still fail to complete the command; the hosted cause
  and sufficiency remain unproved.

### Neutral

- No verifier cache, installed-byte mutation, new authority, runner, worker,
  API, schema or diagnostic pipeline is introduced.
- Discovered-file/reported-case accounting, zero skips/todos, actual ownership
  and cleanup evidence remain. No generic independent scenario census appears.
- Manual accessibility remains maintainer-deferred and unexecuted.

## Implementation Notes

1. On approval, accept/index this record and append a dated ADR-0088 successor
   disclosure without rewriting its historical body. Reconcile the canonical
   [Testing 06 table](../standards/06-testing.md#frontend-guarded-execution),
   [existing catalogue rule](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable)
   and Phase 03 records; retain current guide pointers. Older ADR navigation
   reaches this narrow successor through ADR-0088. Add no architecture rule.
2. Change only the two settings in `framework-provenance.test.ts`. Retain the
   actual 112-control command and its complete TAP assertions, source and
   installed-input qualification, marker regression and ownership checks.
3. Run relevant existing controls, commit before the full stable-source guard,
   and complete both fresh implementation review rounds. Run complete A then
   whole-run B on one final source and use the existing original-archive
   reconciler. PR normally follows qualification; the maintainer may explicitly
   authorize an unqualified draft for parallel product review. That grants no
   qualification, merge permission or exemption from the remaining checks.

## Architecture Tests

Use existing controls: the provenance wrapper still requires exactly 112 tests
and passes with zero failures, cancellation or skips; the generic guard requires
all discovered web/SDK files and all reported cases passed, with zero skips/todos.
The two selected source settings and all unlisted settings remain reviewable
literals. Existing source, metadata, identity, refusal and actual-family tests
remain decisive. Signal invocation, a partial TAP stream and CLI exit alone
cannot qualify missing outcomes or physical cleanup.

## References

- [ADR-0088](0088-frontend-ci-completion-windows.md).
- [Testing 06](../standards/06-testing.md#frontend-guarded-execution).
- [Catalogue 21](../standards/21-architecture-tests-catalogue.md#no_architecture_test_is_skippable).
- [Provenance caller](../../frontend/apps/web/src/test/framework-provenance.test.ts).
- [Existing controls](../../frontend/scripts/test-framework-provenance.mjs).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
