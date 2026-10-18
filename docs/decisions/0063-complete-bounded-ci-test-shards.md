# ADR-0063: Complete Bounded CI Test Shards

## Status

Proposed — approval pending.

**Date:** 2026-10-18 **Deciders:** @cemil

This proposal reopens ADR-0062's execution arrangement after actual hosted
qualification fails its time budget. It authorizes no dependent implementation
before maintainer acceptance. Native collector authority remains exclusively
ADR-0062's; this proposal grants no additional host mutation or weaker proof.

## Decision Drivers

- [Run 39140874785](https://github.com/HodeTech/LearnStack/actions/runs/39140874785)
  discovers 2,401 ordinary Docker cases but completes only 503, all passing,
  before its allocated execution deadline. Another 1,898 cases have no completed
  result. Its recorded forced cleanup is failure, not a successful test outcome.
- The same source's non-Docker backend job reaches its twenty-minute ceiling.
  Unit 2,452, HTTP Integration 177 and Contract four cases have complete passing
  TRXs; Architecture has no completed TRX. Moving native cases alone does not
  qualify either remaining workload.
- The seven existing required names, strict branch protection, complete case
  coverage, zero skips and twenty-minute execution ceilings remain constraints.
  A missing final TRX cannot become an inferred success from partial output.
- Separate VMs isolate mutable process-wide test hooks and Hangfire filter
  state. Concurrent collections in one process do not provide that isolation.
  Repeated checkout/restore/build adds cost, so the topology must be finite.

## Considered Options

1. **Complete method-group shards on separate VMs** (recommended). Bound the
   number of execution VMs, keep every theory row with its actual discovered
   method, and require complete disjoint case-ID unions behind the existing
   backend and integration names. This trades runner/setup cost for wall time;
   actual fit remains a qualification gate.
2. **Increase job ceilings** (valid alternative, not recommended). Preserve
   serial execution with fewer moving parts, but change the existing
   twenty-minute constraint and accept slower failure feedback. Partial current data
   does not establish a sufficient new ceiling.
3. **Parallelize collections inside one test process** (not selected). Shared
   dynamic fixture hooks and process-global vendor filter restoration have
   confirmed cross-test state. Changing those test semantics just to increase
   concurrency risks a false passing isolation proof.
4. **First optimize remaining tests under the current topology** (valid
   alternative). Keep the arrangement unchanged, but require measured overhead
   to remove while preserving lease, timeout, retained-task and mutation proofs.
   Earlier indexed source-analysis corrections do not qualify the full current
   hosted workload. No demonstrated safe change closes the measured gap yet.
5. **Drop cases, mark skips or accept incomplete artifacts** (rejected). These
   weaken coverage rather than qualify it. An aggregate that ignores a failed
   or absent shard is the same defect at another layer.

## Decision

LearnStack partitions the complete non-Docker and ordinary Docker populations
into bounded deterministic method groups on separate standard hosted VMs. The
initial proposed topology is one backend-base VM, four Architecture shards and
ten ordinary Docker shards, plus ADR-0062's unchanged single native VM. The exact
existing backend and integration required names become fail-closed aggregates
of their complete
expected execution receipts. Every execution retains its twenty-minute
whole-job ceiling, existing assertions and zero-skip requirement. This proposed choice
does not claim measured fit before the actual final-source hosted proof.

### Population and assignment

- Discover the actual Release assemblies before assignment. Non-Docker retains
  the original `Requires!=Docker` population. Docker retains ADR-0062's complete
  native/ordinary complement. No manual class allowlist defines the population.
- Backend-base owns all non-Docker cases outside the explicitly declared
  Architecture project set. The four Architecture shards own that set's complete
  non-Docker population. Fresh all-project discovery must reconcile both sets;
  an unknown or omitted project is not silently excluded.
- Backend-base also owns existing format verification, normal Release build and
  Hangfire distribution/source/replacement controls. These are mandatory gates
  outside the case census; their recorded success gates the backend aggregate.
- Extend the bounded discovery projection with the actual fully qualified
  method identity supplied by the pinned adapter. Group by assembly and that
  identity, preserving all discovered theory/mutant case IDs together. Display
  names alone are neither a unique identity nor a safe method parser.
- A versioned, deterministic assignment consumes the complete manifest and the
  same source-controlled plan on every VM. Timing estimates may inform balance;
  they are advisory and never membership or success evidence. Unknown new
  methods still receive an owner. Repeated method identities across theory rows
  are expected. Missing identities, duplicate case IDs, conflicting method
  ownership and unsupported case-insensitive selector collisions refuse
  qualification rather than silently dropping cases.
- Each shard executes only its assigned methods using the pinned test
  platform's qualified method filter. Construct arguments directly, never
  through shell evaluation. Refuse unsupported identities/filter syntax;
  independently rediscover the generated filter's selected cases before running
  and require exact equality with the assignment. Then verify actual executed
  case IDs against that set. A filter is not its own selection proof.
- Execute a method once, with every row, on exactly one shard. Empty selected
  projects retain full discovery coverage; an empty whole shard is a topology
  error. If a single method cannot fit alone, reopen this choice rather than
  quietly splitting its rows or weakening its proof.

### Required-result and authority boundary

- Preserve exact `backend (build + unit + arch + contract)` and
  `backend integration (Testcontainers)`. Their aggregate jobs run even after
  execution failure and require backend-base and every expected shard's
  successful job outcome
  and complete valid artifact. No `continue-on-error`, fail-fast omission,
  optional shard or partial aggregate success is allowed.
- Non-Docker's shard union must equal its original full discovered population.
  Ordinary shards must be pairwise disjoint, their union exactly ordinary,
  and their union with native exactly the full original Docker population.
  Compare actual assembly/case identities, not row counts or display names.
- Require matching source/run/attempt/workflow identity, full manifests and
  assignment digest, exact shard index/count, unique receipts, complete TRXs,
  per-case outcome/duration and final hashes. Missing/extra/foreign/stale,
  skipped/canceled/failed or incomplete work keeps its required result red.
  Full canonical population manifests must agree across VMs. Each aggregate
  independently recomputes the deterministic assignment from that manifest and
  the source-controlled plan; matching worker-supplied digests alone is not proof.
- Keep the other five required names and every branch-protection setting.
  Independent API readback verifies the unchanged seven-check set and strict
  mode; acceptance does not authorize changing protection.
- Each execution VM uses an owned non-root process/cgroup boundary, independent
  build-server settings and bounded join/cleanup/export. Existing shared source
  and corpus guards run as real cases. Repeat normal builds rather than adding
  a cross-job executable build-artifact trust surface in this change.
- Only the native VM can invoke ADR-0062's fixed collector provisioning. Neither
  backend nor ordinary shards acquire that authority. Native assertions,
  collector refusal, crash inventory and unknown-state treatment stay intact.

### Bounds and qualification

- The initial upper bounds are one backend-base, four Architecture and ten
  ordinary execution VMs, plus the existing single native VM.
  The trusted workflow carries explicit finite indices; copied matrix
  identity is consistency evidence, not platform attestation. No unbounded
  dynamic matrix or automatic shard expansion exists.
- Each shard's finite setup/execution/cleanup/export allocations remain within
  twenty minutes. Reserve sizes are implementation-owned and require actual
  measured successful and failure-tail qualification; an estimate is not fit.
- Record setup, completed-case and cleanup/upload durations separately. Keep
  actual per-case IDs/outcomes/durations on interrupted paths without final
  TRX. Force-stop, missing process evidence and censored cases remain failures.
- Qualify all shards, both aggregates and the other five required checks on the
  same final PR source. Local or preceding-head success is insufficient. If
  the proposed finite topology cannot qualify, keep the PR blocked and reopen
  the topology or ceiling decision; do not auto-expand it.

## Context

The first actual split run uses source `6daa9a1c` and PR merge checkout
`853678c01c2c877e8bf5d7f81f559fc86ffbc9c4`. The ordinary manifest contains
2,432 original Docker IDs: 31 native and 2,401 ordinary. All 503 completed
ordinary records are passing and belong to the discovered set. No final
Integration TRX exists; its remaining cases are censored, not failed measurements
or inferred passes. Recorded failure cleanup takes 0.475 seconds and positively
joins the owned workload; it does not qualify a successful natural disposal path
or establish a worst-case reserve.

The native job fails earlier on an unidentified crash-inventory candidate, before
provisioning or tests. Meta exposes a synthetic event-file truncation race.
Those independent corrections and the native prerequisite investigation remain
with ADR-0062; distributing ordinary/backend tests cannot fix or waive them.
Frontend passes 1,625 tests, OpenAPI and secret scan pass, and demo supplies all
27 report pairs, nine screenshots and successful owned cleanup on that source.
These passing jobs do not make either backend population complete.

Prior full local/historical timings support investigation, not a hosted fit
claim. The unchanged non-Docker population passes 3,043 local cases at
`82d5307c` in 1,221.712 test-command seconds, excluding hosted setup. A historical
full Docker receipt is neither current-source qualification nor a zero-failure
baseline. Current partial per-method measurements cannot estimate every unseen
method reliably. The proposed shard counts are a bounded implementation plan
requiring actual validation, not a mathematical runtime guarantee.

### Candidate sizing and filter feasibility

The inspected VSTest 17.12.0 filter contract and xUnit adapter 2.8.2 support exact
`FullyQualifiedName` selection. The current sanitized discovery omits that field;
implementation must obtain and verify it from actual discovery before selecting
any shard. A method selector keeps every discovered theory row together. Current
historical method groups are much smaller than entire test classes, so grouping
by class would impose an avoidable large indivisible workload.

`Microsoft.NET.Test.Sdk` 17.12.0 is not a pin for the SDK's console executable.
The local observed console is `18.6.0-release-26329-109`; an actual two-method
selection and pre-owned diagnostic-file experiment passes on that SDK. This is
narrow local feasibility, not hosted selection or complete-shard qualification.
The decision's independent actual-filter rediscovery remains mandatory.

The historical ordinary receipt has 2,397 rows in 1,073 method groups and about
2,296 summed test-duration seconds. Ten deterministic longest-first bins are
about 230 local summed seconds each. The current hosted completed prefix has
499 name-matched historical rows: about 808 hosted summed seconds versus 356
local, an approximate ratio near 2.27. Display-name collisions make that historical
name match an estimate, not a case-ID correspondence. Applying the ratio suggests
roughly 522 seconds per
bin against the observed approximately 782-second post-setup execution allowance.
This is a planning extrapolation, not a bound on unseen methods or another VM.
All four new ordinary rows must enter fresh discovery and receive an owner.

The latest complete local Architecture receipt contains 410 cases in 254 method
groups, about 2,316 summed duration seconds and a largest method near 253 seconds.
Summed case durations overlap within a test process and differ from wall time.
Four largest-first bins are about 579 summed seconds apiece in that receipt.
Hosted Architecture completes only 152 console rows before cancellation; those
rows do not establish complete hosted method costs. Separate base/Architecture
owners preserve the cheap non-Docker suites and non-test gates while isolating
mutable process state. These measurements justify trying this finite topology;
only actual hosted execution can qualify its fit.

### Exact relationship to ADR-0062

From [ADR-0062 Decision](0062-native-crash-proof-ci-provisioning.md#decision):

> Ordinary Docker tests run on a separate VM; a fail-closed aggregate retains the
> existing required integration check name.

From [Complete partition and required result](0062-native-crash-proof-ci-provisioning.md#complete-partition-and-required-result):

> Both execution jobs keep twenty-minute whole-job ceilings, nonempty/zero-skip
> guards, complete TRX and completed-case console evidence.

Replace only the single ordinary execution VM with the finite ordinary shard
set and its complete aggregate. Preserve all listed execution requirements for
every shard. Extending the same complete-aggregate pattern to non-Docker is an
additive CI seam. This is a bounded decision change, not an ADR-0041 factual
correction. Acceptance appends a dated disclosure to ADR-0062 without rewriting
its Accepted body or metadata. Native authority and every excluded-host rule
remain unchanged.

### What would change this choice

Reopen for a method that cannot fit, a platform/adapter contract that cannot
select the exact discovered group, non-deterministic population/assignment,
unqualified ownership/cleanup, unsustainable runner cost or failure to fit the
bounded topology. A future validated test-overhead reduction may justify fewer
shards after complete qualification; it cannot justify missing coverage.

## Consequences

### Positive

- Keep the complete tests and named proof obligations while bounding feedback.
- Isolate process-global test state instead of making collection races possible.
- Preserve seven required results and refuse partial matrix success.

### Negative

- Fifteen backend/ordinary execution VMs replace two; native remains one. Repeated
  restore/build, full discovery and artifact work increase runner usage and
  queue pressure. Approval accepts this cost, not a promised wall-time result.
- Manifest/assignment/filter and aggregate verification add maintained CI code.
  Method-level grouping can leave uneven shards; balance is not correctness.
- Queued runners and unusually slow methods can still fail qualification.

### Neutral

- No application runtime, schema, permission or deployment topology changes.
- No additional vendor package, collector mutation or production readiness map.
- P02b-4 owns this qualification; PR #32 remains open until actual checks pass.

## Implementation Notes

After acceptance, commit the decision and dated disclosure before dependent code.
Update Standards 06/20/21, contributing required-check explanations, workflow,
existing CI owner/controls, roadmap/front-door status and probe instructions
where their execution arrangement changes. Preserve existing canonical rule and
method counts; proposed obligations do not become Implemented by acceptance.

Use the existing Python CI owner and pinned .NET test platform; no alternative
test engine or package is implicitly approved. Implementation steps are:

1. Complete method identity/assignment and planted clean/dirty controls.
2. Finite VM execution, bounded partial results and unchanged ownership controls.
3. Complete workflow/aggregate wiring, corpus updates and actual qualification.

Each implementation step receives a commit and two fresh independent review
rounds, with verified fixes committed before advancing. Final hosted proof owns
fit; no further runtime feature work is hidden inside this CI follow-up.

## Architecture Tests

Extend the existing CI-owner controls under
`Hangfire_Invocations_And_Diagnostics_Are_Closed` and retain the existing
`Every_Database_Test_Carries_The_Docker_Trait` source/native closure. At acceptance
name the actual control owner and keep new assertions pending until shipped in
the same enforcement change. Required planted controls include missing/duplicate/
foreign methods, all theory rows, new-method assignment, repeated identities,
wrong index/count/digest, overlap/gap, stale manifests, skipped/failed shards,
incomplete TRX, partial aggregate and interrupted exact-ID records. Structural
censuses must remain nonempty and distinguish clean from dirty controls.

## References

- [ADR-0062](0062-native-crash-proof-ci-provisioning.md).
- [ADR-0060](0060-fatal-hangfire-startup-and-unsafe-unwind.md).
- [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md).
- [Testing Standards](../standards/06-testing.md).
- [VSTest 17.12 filter contract](https://github.com/microsoft/vstest/blob/v17.12.0/docs/filter.md).
- [xUnit adapter 2.8.2 filter](https://github.com/xunit/visualstudio.xunit/blob/2.8.2/src/xunit.runner.visualstudio/Utility/TestCaseFilter.cs).
- [xUnit adapter execution](https://github.com/xunit/visualstudio.xunit/blob/2.8.2/src/xunit.runner.visualstudio/VsTestRunner.cs).
- [Infrastructure Standards](../standards/20-infrastructure-stack.md).
- [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md).
- [Documentation Standards](../standards/13-documentation.md).
- [P02b-4 hosted follow-up](../roadmap/phase-02b-events-auth.md#hosted-ci-qualification-follow-up-2026-10-18).
