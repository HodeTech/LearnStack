# ADR-0091: Native Inventory Phase Allocations

## Status

Proposed

**Date:** 2026-11-03
**Deciders:** @cemil (approval pending)
**Bounded supersession on acceptance:** ADR-0090's retained native scan setting
only before owned-test launch, and the inherited fixed image-provenance command
allocation. Native final inventory remains twenty seconds; the native job,
203-second reserve, all test deadlines and ADR-0090's SDK settings remain.
Earlier successor navigation reaches this record through ADR-0090. No executable
allocation changes before maintainer approval.

## Decision Drivers

- PR run `41656897245`, attempt 1, head `b53ad494`, merge source `0472446e`,
  refuses native execution before owner/test launch. The receipt records
  `unexpected_crash_artifact_docker_overlay_dump_probe_refused_image_metadata_unavailable_info_command_timeout`.
  Neither the candidate's format nor read-only image membership is established.
- The execute interval is approximately 22.919 seconds, including prevalidation.
  `docker info` timed out, but the receipt does not distinguish its five-second
  command cap from the remaining shared twenty-second inventory deadline.
  It proves neither a necessary minimum nor an underlying Docker/host defect.
- The same twenty-second allocation currently covers preflight and final
  inventory. Preflight additionally competes with repeated positive image
  metadata/canonical checks and a complete rescan. Each metadata command uses
  the shared default five-second sublimit. Partial provenance cannot qualify.
- The earlier run's generic inventory refusal does not establish the same cause.
  Current A completes 25/27 jobs successfully: only native and its required
  integration aggregate fail. Frontend, public-demo and all nineteen non-native
  producers pass. SDK's reported Vitest duration is 177.02 seconds; its aggregate
  remains 180 seconds. That observed pass guarantees no future fit. Other jobs
  do not qualify the missing native producer.

## Considered Options

1. **Separate preflight allowance and scoped image-query allowance** (chosen proposal).
   Preserve complete scans and positive provenance while retaining final-inventory
   allocation and the cleanup reserve.
2. **Retain 20/5 and investigate further** (viable, not selected).
   Preserve the failed evidence and open product review. Additional observations
   may justify a source repair; none currently proves one.
3. **Enlarge the whole native job or cleanup reserve** (not selected).
   The current refusal is before test launch, not whole-job exhaustion. No such
   wider change follows from this evidence.
4. **Skip roots, trust a filename/format or cache image metadata** (rejected).
   A candidate stays unqualified until current positive provenance and a complete
   inventory succeed; these shortcuts weaken that proof.

## Decision

On acceptance, LearnStack selects only these source-owned allocations as
maintainer-judged headroom, not measured minima or a causal repair.

| Native inventory owner | Current | Selected |
|---|---:|---:|
| Preflight file/image inventory, before owned-test launch | 20 s | 60 s |
| Fixed read-only commands inside `image_layer_snapshot` | 5 s | 10 s |
| Final file/image inventory, within cleanup | 20 s | 20 s |

Both phases keep one absolute shared phase deadline, clipped by the existing
execution/cleanup deadline. Each command is additionally clipped by its own
sublimit. Ten seconds per command reserves no sum of commands within sixty or
twenty seconds. Native retains 1,200 seconds whole-job and the 203-second reserve;
preflight consumes execution/setup allowance rather than borrowing that reserve.
More preparation can leave less time for the actual tests. No fit is guaranteed.

A new closed internal preflight selection is used only at the existing verified
native `execute` call before `started=True` and `launch_unit`. Final inventory
uses the twenty-second default. The selection is not an environment, receipt,
CLI or arbitrary-duration override. `find_crash_candidate` inherits that selected
phase allocation; the metadata sublimit applies only to existing commands in
`image_layer_snapshot` (info/list/inspect, canonical/stat and their rechecks).
Other commands retain their existing bounds, including the file-format probe.

The same roots, `sudo -n`, no symlink following, `-xdev`, predicates, byte/count
bounds and current positive image inventory/canonical/readback checks remain.
Only exact proved image directories can be pruned, followed by the same complete
rescan and fresh consistency checks. A filename, format, earlier snapshot or
partial query never establishes absence. Unreadable, changed, timed-out or
unexpected candidates remain failed; no retry converts refusal into success.

All commands remain read-only with the same private stdout/discarded stderr and
closed reason labels. Allocations and stop requests grant no physical retirement
or cleanup credit. No new owner, role, privilege, runner, table, cache, host access
or diagnostic pipeline is introduced. Base/Architecture/ordinary and product
runtime policy remain unchanged. The scan is CI-only on the already qualified
exclusive hosted native VM, not developer/shared/production host authority.

## Context

[The observed refusal record](../roadmap/phase-03-delivery-log.md#p03-3-native-image-provenance-timeout-and-adr-0091-proposal-2026-11-03)
retains exact source/log identity and distinguishes observation from inference.
The candidate remains unproved. Longer allocations may still fail on a stalled
query, large/changing inventory, final inventory or actual test workload; this
choice authorizes no automatic further increase or relaxed acceptance.

PR #40 remains available for product review. Any executable change requires
fresh complete final-source PR A then whole-run B and existing original-archive
reconciliation. Neither partial passes nor failed predecessors earn credit.

## Consequences

### Positive

- Keeps the complete independent no-crash proof and current positive provenance.
- Separates preflight work from the retained cleanup ledger without extra VM/job time.

### Negative

- Preflight can request forty more seconds; scoped metadata commands five more,
  always inside their parent deadline. Actual return/retirement is not guaranteed.
- The same twenty-second final inventory can still refuse. No per-command maxima
  or actual native test duration are reserved by the parent allocations.

### Neutral

- Native final inventory twenty, reserve 203, whole-job twenty minutes, execution
  populations, VM cap, test bounds and required checks remain.
- Manual accessibility remains maintainer-deferred and unexecuted.

## Implementation Notes

1. Accept/index only after approval; append a dated ADR-0090 successor disclosure
   without rewriting its body. Reconcile the canonical
   [Testing 06 execution section](../standards/06-testing.md#complete-bounded-ci-assignments),
   [Infrastructure 20 pointer](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire),
   [existing native CI proof owner](../standards/21-architecture-tests-catalogue.md#hangfire_invocations_and_diagnostics_are_closed),
   [complete-shard owner](../standards/21-architecture-tests-catalogue.md#ci_test_shards_are_complete_and_bounded)
   and Phase 03 records, including their inherited operation-cap language.
   Add no architecture rule or status promotion.
2. In `scripts/ci-native-tests.py`, introduce fixed preflight/metadata constants
   and a closed internal preflight selector. Keep `SCAN_SECONDS=20` and the tail/
   reserve arithmetic unchanged. Only the initial native call selects preflight;
   cleanup retains its default. Apply the selected find allocation and explicit
   metadata cap without changing argv, evidence or refusal handling.
3. Extend existing `scripts/test-ci-native-tests.py` controls for both phase
   selections, parent clipping, metadata sublimits, preserved final ledger and
   exact call-site census. Retain malformed/changed/writable-candidate, full-rescan,
   closed-error and private-output counterfactuals. Run relevant existing controls,
   both fresh implementation reviews, then one final-source PR synchronization
   and full A/B/original-archive qualification. Do not run a duplicate manual CI
   or merge. Application/frontend suites need not be repeated for unchanged code.

## Architecture Tests

Existing copied-owner controls must prove only the pre-launch native path selects
sixty seconds; final inventory remains twenty and reserve remains 203. Unknown
selector shapes refuse. Metadata commands select ten seconds but parent deadlines
can stop them sooner. Timeout/OS/nonzero/oversized output remain refused, and no
candidate or partial scan supplies success. Prove unchanged roots, no symlink
following, exact image pruning, complete second scan and drift refusal. Local
controls supply no hosted ownership, native-case completion or fit claim.

## References

- [ADR-0090](0090-sdk-cold-prefix-proof-settings.md).
- [Native CI authority](0062-native-crash-proof-ci-provisioning.md).
- [Testing 06](../standards/06-testing.md#complete-bounded-ci-assignments).
- [Existing launcher](../../scripts/ci-native-tests.py) and
  [offline controls](../../scripts/test-ci-native-tests.py).
- [Phase 03](../roadmap/phase-03-identity-admin.md).
