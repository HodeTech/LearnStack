# ADR-0063: Complete Bounded CI Test Shards

## Status

Accepted — 2026-10-18.

**Date:** 2026-10-18 **Deciders:** @cemil

**Maintainer acceptance — 2026-10-18.** The maintainer approves the reviewed
revision at `b3ee4ea6`, with both independent review receipts recorded at
`2e1fedc7`, and authorizes the three dependent implementation steps on
`development`. This reopens ADR-0062's execution arrangement after actual hosted
budget failure. Native collector authority remains exclusively ADR-0062's;
non-Docker process-owner authority includes no collector or Docker authority.
Acceptance is not implementation or hosted fit. The new general catalogue rule
is Registered; each implementation step still requires a commit and two fresh
independent review rounds before advancing.

## Decision Drivers

- [Run 39140874785](https://github.com/HodeTech/LearnStack/actions/runs/39140874785)
  discovers 2,401 ordinary Docker cases but completes only 503, all passing,
  before its allocated deadline. Another 1,898 cases have no completed result.
  Forced cleanup and a missing final TRX cannot become inferred success.
- Backend also reaches its twenty-minute ceiling. Its complete Unit, HTTP and
  Contract TRXs establish those populations' results, not Architecture's. Native
  separation alone does not qualify either remaining population.
- Preserve all seven required names, strict protection, complete case coverage,
  zero skips and twenty-minute execution ceilings. A different process grouping
  must prove its independence rather than merely balance an estimated duration.
- Process isolation contains mutable test hooks and vendor filter state.
  Separate VMs also isolate fixture resources, at the expense of repeated build,
  discovery and fixture work. Process isolation itself does not require a VM.
- Standard public runners currently carry no runner-minute charge. Finite
  organization concurrency, repeated VM work, artifact usage and queue delay
  still matter; neither simultaneous scheduling nor fixed CPU speed is promised.

## Considered Options

1. **Complete selector-group shards on separate VMs** (chosen). Retain every case,
   bound manual topology tuning and require complete unions behind the existing required
   names. Actual wall-time fit and a second distinct assignment must qualify before
   rollout closes.
2. **Increase job ceilings** (valid alternative, not recommended). Avoid new ownership
   machinery, but change the twenty-minute constraint. Historical ordinary durations
   extrapolate to roughly 87 summed hosted minutes; local Architecture alone has roughly
   39 summed minutes. These are overlapping test durations, not job walls or a
   sufficient new ceiling. They show the scale of the unfinished work without proving
   how much extra time would suffice.
3. **Increase collection parallelism in one process** (not selected). Integration
   already uses xUnit's collection parallelism; its 75-class `SharedSchema` collection
   remains serial. Architecture explicitly caps parallel threads at two. More collection
   concurrency does not isolate process-global hooks or repair order dependence. The
   shared-hook/filter-restoration constraint is recorded in the [hosted
   follow-up](../roadmap/phase-02b-delivery-log.md#hosted-ci-qualification-follow-up-2026-10-18).
4. **Several isolated test processes per VM** (credible alternative, not selected for
   this rollout). Reuse checkout/build and isolate static state in distinct testhosts.
   This needs one owner for all children and Docker lifecycle, private result paths,
   collision controls and measured CPU/memory/container contention. No such
   multi-process hosted resource profile is retained. Separate VMs choose simpler
   resource ownership, not a claim that same-VM processes cannot work.
5. **Optimize remaining tests first** (valid alternative). Preserve the current
   topology, but demonstrate sufficient improvement without weakening lease,
   retained-task, native or mutation proofs. Earlier source-analysis corrections do not
   close the measured current-source gap. Measured reductions can later reduce the
   proposed counts under the same qualification requirements.
6. **Drop cases, mark skips or accept incomplete artifacts** (rejected). These erase
   coverage. Ignoring a failed or absent shard in an aggregate has the same effect.

## Decision

LearnStack uses a versioned, finite CI plan: backend-base owns non-Docker projects
outside Architecture and existing non-test gates; separate Architecture and ordinary
Docker VMs own complete discovered selector groups; ADR-0062's native VM remains
separate. Manual counts are bounded to four–six Architecture and eight–twelve ordinary
shards, with one base and one native VM: at most twenty backend/test execution VMs. The
proposed initial plan uses six and twelve, respectively. Each execution keeps its
twenty-minute whole-job ceiling. Exactly one always-running aggregate owns each existing
backend/integration required name. Both aggregates have five-minute whole-job ceilings.
Acceptance authorizes this tuning envelope, not automatic expansion or an assertion of
measured fit.

### Complete populations and actual selection

- Fresh discovery covers all actual Release test projects. Preserve original
  `Requires!=Docker`; Docker remains ADR-0062's complete native/ordinary complement. A
  manual class list never defines the population. Base ownership is by project; only
  Architecture and ordinary use selector-group assignment. An unknown test project
  receives an explicit base or Docker owner and its complete discovery; it cannot
  silently disappear.
- Base retains format verification, normal Release build and Hangfire
  distribution/source/replacement checks. Their finite success evidence gates its
  aggregate in addition to case results. Base fit is independently measured; future
  growth cannot silently move a gate, omit a project or raise its ceiling.
- Read actual `FullyQualifiedName` from discovery. The pinned xUnit adapter's
  `VsDiscoverySink.CreateVsTestCase` constructs it from class and method descriptors,
  independently of display name and case ID. The indivisible key is assembly plus this
  exact selector identity, not a CLR overload signature. Every theory/overload case
  sharing that key stays together.
- Missing identities, duplicate case IDs, unsupported selector syntax and
  case-insensitive aliases refuse qualification. Assembly-qualified ownership and
  per-assembly filters distinguish equal selectors in different assemblies. Never parse
  method identity from display text or serialized theory inputs.
- Construct direct argv without shell evaluation. Before execution, independently
  rediscover each generated filter and require exact equality with its assigned case-ID
  set. Then reconcile actual results, not only selection, with that set. Refuse oversize
  before spawning: initially filter arguments are bounded to 32 KiB UTF-8, all argv
  strings including separators to 64 KiB per invocation. These are LearnStack ceilings,
  not claimed platform maxima; their actual hosted boundary behavior must qualify. Do
  not silently split or run unfiltered.
- Execute each case once per complete assignment. Empty selected projects still retain
  full discovery; an empty whole shard is a plan error. If an indivisible selector
  cannot fit, reopen the decision rather than splitting its rows.

### Versioned plan and independence

- `scripts/ci-test-shards-plan.json` is the proposed tracked JSON authority for schema
  version, counts, assignment algorithm/version, literal variants A/B, tie-breakers,
  finite argument bounds and advisory method weights. The proposed owner is
  `scripts/ci-test-shards.py`; execution remains with the existing
  `scripts/ci-native-tests.py`. Neither planned file exists at proposal time.
- Weight keys use assembly plus actual selector. Retain each timing source's
  source/run/artifact identity, unit and scope. Summed case milliseconds are weights,
  never wall-time bounds. New selectors receive a declared nonzero default, initially
  60,000 ms, and appear in an unknown-weight census. That default is an estimate, not a
  conservative upper bound or membership authority.
- Assignment is deterministic from the full manifest and reviewed plan. Freeze weights
  within a plan revision; do not fetch or rewrite timings during a run.
  Timing/count/algorithm changes require a reviewed plan revision, assignment diff and
  fresh qualification. New cases may change ownership; publish the manifest and
  assignment digests so unlike plans are not compared as like bins.
- Initially A orders selectors by descending advisory weight, then ordinal UTF-8
  assembly/selector key. B orders by SHA-256 of a fixed versioned B salt and
  length-prefixed UTF-8 key components, then that key. Each assigns the next whole
  selector to the least cumulative method-weight bin, breaking ties by index.
  These weights balance estimates only. The actual A/B co-membership check must
  pass; two layouts cannot prove absence of every possible order dependency.
- The workflow lists finite literal indices matching the committed counts. Manual tuning
  stays within the accepted ranges and total cap, with recorded evidence. No
  discovery-generated matrix, automatic expansion, ceiling increase or paid
  runner/concurrency upgrade is authorized. Exhausting this envelope reopens the
  decision. Counts and plan cannot disagree and still launch.
- Qualify two materially different complete assignments on the same final source,
  toolchain and case population. A/B must change actual selector co-membership across
  bins, including shared-fixture methods; renumbering identical bins is not a second
  assignment. Fix verified order/shared-state dependencies rather than tuning a lucky
  partition. Both assignments require all execution jobs, complete unions, both
  aggregates, the other five checks and zero skips.
- Use two full attempts of the same workflow run: odd attempts choose literal A, even
  attempts literal B. Record both plan/assignment digests and attempt-specific proof.
  This deliberately changes grouping on a full retry, with no arbitrary selector input.
  The rollout and subsequent plan/algorithm/fixture-lifetime changes require both
  variants; normal future pushes still execute one complete assignment. Each aggregate
  validates only its own attempt, never a mixed union.

### Required results, artifacts and retries

- Preserve exact `backend (build + unit + arch + contract)` and
  `backend integration (Testcontainers)`, each on exactly one aggregate across
  repository workflows.
  Matrix/execution jobs cannot carry either name. Retain the other five names, strict
  mode and all expected GitHub App bindings. Independent API readback verifies unchanged
  protection; no settings mutation is authorized.
- Both aggregates run after failures and explicitly refuse every absent, skipped,
  canceled, timed-out, failed or incomplete producer. Backend requires base and all
  Architecture shards. Integration requires all ordinary shards **and** the native job.
  No fail-fast omission, `continue-on-error` or optional leg exists.
- Base plus Architecture must equal the original full non-Docker population. Ordinary
  shards must be disjoint and exactly ordinary; their union with native must exactly
  equal original Docker. Compare actual assembly/case IDs, not counts or display names.
  Canonical full manifests must agree across producers.
- Each aggregate recomputes assignment from its own reviewed plan and the full
  manifests. Require source/run/attempt/workflow identity, shard index/count, complete
  TRXs, exact case outcomes/durations, non-test gates and final hashes. Matching worker
  digests or copied matrix context alone is not attestation.
- Artifact names include run, attempt and unique producer identity. Require immutable
  artifact IDs, full attempt-specific job API outcomes and matching payload identities.
  Refuse overwrite, duplicate/stale producers and merged extraction that can replace
  another producer's files. Preserve bounded artifact download/validation/upload
  allocations within each five-minute aggregate.
- Aggregate jobs receive only job-scoped `actions: read` alongside existing
  `contents: read` for job/artifact verification. Execution jobs receive no added
  token permission. Unavailable/inconsistent API evidence fails qualification;
  worker payloads cannot replace it. Keep PR head and actual merge-checkout SHA
  as distinct, explicitly related identities rather than equating their hashes.
- Recover with **Re-run all jobs**. GitHub failed-job or single-job reruns do not
  promise to execute successful siblings; a missing producer in the aggregate's current
  attempt keeps the result red. Never reuse an earlier attempt's passing siblings.
  Original event SHA/ref stay fixed; run attempt increments.

### Execution authority and wall-time qualification

- New execution jobs select standard `ubuntu-24.04` x64 VMs. Validate actual
  image/toolchain and existing hosted-exclusive identity prerequisites; a label does not
  pin a weekly image. Repeat normal builds, with no cross-job executable artifact trust
  surface. Record resolved SDK, actual VSTest console and loaded adapter
  versions/identities in every execution receipt.
- Native alone retains ADR-0062 collector setup, actual crash inventory and independent
  native assertions. Ordinary shards retain that decision's existing Docker
  process/container owner. Each has an independent empty all-state Docker baseline and
  must finish owned disposal; forced cleanup remains failure.
- This decision additionally authorizes the fixed root-managed transient systemd cgroup
  owner for base/Architecture on those exclusive hosted VMs. Its test child is non-root;
  only its exact owned subtree can be killed/joined. Keep persistent build servers
  disabled and retain positive child/subtree completion before collection. Non-Docker
  owners perform **no** Docker baseline, Docker stop/remove, collector provisioning or
  crash inventory. No local/shared/self-hosted grant, general root command or additional
  sysctl authority follows.
- Model each job by **disjoint wall intervals**: checkout/toolchain/restore/build and
  non-test gates; discovery/filter validation; test-process lifetime including fixture
  initialization; owner cleanup; evidence export/upload. Do not add summed test
  durations to those intervals as though they were another wall component. Report
  first-result/startup gaps and individual fixture-start counts where instrumented;
  unobserved fixture or CPU costs remain unknown, not zero.
- Method sharding can repeat the full `SharedSchema` container, five migration chains
  and seed on every participating VM. Public-read, production-rendering and class
  fixtures can repeat too. The plan's per-shard estimate explicitly includes discovery,
  testhost/fixture startup and non-test fixed costs. Historical sums exclude or obscure
  them; they cannot establish spare capacity. Qualify actual cold fixture paths and peak
  resource use on both assignments.
- Keep each twenty-minute ceiling and a finite cleanup/upload reserve. Reserve changes
  require measured successful and interrupted paths; borrowing that reserve to make a
  paper estimate fit is not qualification. Setup/deadline exhaustion refuses launch.
  Capture exact completed IDs/outcomes/durations on interruption; censored cases, forced
  stops or missing completion remain red.
- Record queue delay separately from running-job wall time. Actual whole-job fit,
  full-sized artifacts, failure-tail cleanup and meta's five-minute fit must pass on the
  final source. A timing estimate, local run or preceding-head success cannot close
  P02b-4. Independent ownership/internal faults retain their own corrective work under
  ADR-0062; sharding neither fixes nor waives them.

## Context

### Observed coverage, failures and corrections

The first split run, source `6daa9a1c`, merge checkout
`853678c01c2c877e8bf5d7f81f559fc86ffbc9c4`, discovers 2,432 Docker IDs: 31 native and
2,401 ordinary. Its 503 completed ordinary results pass; 1,898 remain censored without
final Integration TRX. Forced failure cleanup takes 0.475 seconds and witnesses owned
join, not natural disposal or a worst-case reserve. Backend has 2,452 Unit, 177 HTTP and
four Contract passes, with only 152 recognized Architecture console rows and no
completed Architecture TRX.

In **that first run**, native refuses before provisioning/tests on a then
unclassified crash candidate, and meta exposes a synthetic event-file race.
Meta is corrected at `14584012`. The candidate is later bound to an actual image
layer and narrowly requalified at `e710bb61` in
[run 39146395561](https://github.com/HodeTech/LearnStack/actions/runs/39146395561):
31/31 native cases, zero skips, natural disposal and a 420-second whole job.
Its launcher-only setup/execution/cleanup intervals are 2.825/231.561/10.628
seconds; they exclude checkout/restore/build and are not full-shard setup costs.

Reader rotation is independently corrected at `9aad3c57`; it is not established
as the cause of an earlier hosted fault. Both fresh focused review rounds pass.
[Run 39148006960](https://github.com/HodeTech/LearnStack/actions/runs/39148006960)
on source `3553ff16`, merge `6de31fcce66d7f53e4d5d28fd163f65ab8af4d85`, passes
31 native cases with exact IDs/TRX fields/hashes and natural disposal; meta passes
17 provisioning and 38 launcher controls. Native launcher intervals are
1.535/240.221/10.468 seconds. This is qualification of that native source, not
of the complete PR.

Ordinary failures are not exclusively budget failures: run 39144033895 records
`owned_completion_missing`; run 39145489321 records `workload_internal_refusal`. Run
39146395561 completes 517 passing IDs then `deadline_exhausted`, leaving 1,884 censored.
Run 39148006960 instead records `owned_completion_missing` with
`workload_internal_membership_io`; the receipt does not establish its errno or root
cause. It retains 416 passing IDs, 1,985 censored cases and no final TRX; forced cleanup
takes 0.459 seconds. All remain failed, independently owned corrections. Backend again
reaches its ceiling; the passing native/meta/frontend/OpenAPI/secret/demo jobs cannot
qualify the missing backend/ordinary population.

### Timing evidence and its limits

The local non-Docker receipt at `82d5307c` passes 3,043 cases in 1,221.712 command-wall
seconds, excluding hosted setup. A historical full Docker receipt has failures and is
not a current green baseline. There is no complete current hosted Architecture/ordinary
TRX or retained per-fixture CPU profile.

The three base suites already have separate complete platform intervals:

| Hosted run | Unit wall seconds | HTTP wall seconds | Contract wall seconds | Overlapping envelope seconds | Before test step seconds |
|---|---:|---:|---:|---:|---:|
| 39131069558 | 22.709 | 20.035 | 7.442 | 23.149 | 232 |
| 39140874785 | 14.289 | 10.734 | 4.102 | 14.602 | 160 |
| 39148006960 | 17.757 | 14.120 | 4.103 | 17.894 | 182 |

These TRX start/finish intervals overlap inside the existing solution command; their
union is not a measured future base job. Current pre-test work includes 83 seconds of
format, 74 of build and two seconds of distribution/replacement controls, plus
restore/tool setup. Fresh discovery, the new owner, evidence export/upload and an
isolated base job remain unmeasured. These facts support one base candidate, not an
exemption from its ceiling or additional instrumentation.

The historical ordinary receipt has 2,397 rows in 1,073 selector groups and 2,296 summed
case seconds. Ten largest-first bins give about 230 **summed local** seconds. Of the
first hosted prefix, 499 actual case IDs match the historical receipt, with roughly 808
hosted versus 356 local summed seconds, a ratio near 2.27. The later 517-case prefix
matches 513 historical IDs at a ratio near 2.18. Matching IDs improves correspondence;
different sources and completed-only sampling still make these weight estimates, not
unseen-case bounds. Extrapolating gives about 87 summed minutes for that historical
population. It does not give test-process wall time, fixture startup or a maximum bin
duration.

The first run's approximately 782-second proof step is a source-specific gross wall
window, not a test-only allowance. The owner's calculation uses 1,200 seconds minus 203
total reserve (143 cleanup/export plus 60 upload) minus about 215 seconds since its
first-step clock. API job-start to proof is 219 seconds; those clocks differ. Discovery,
validation, fixtures and tests all consume this window. Twelve all/native/ordinary
project discovery commands in the present owner and extra selected-filter discovery
consume that window. No measured fixed-cost deduction or spare 260-second margin is
established.

The local Architecture receipt has 410 cases in 254 selectors, about 2,316 summed
seconds and a largest selector near 253 seconds. Four bins are roughly 579 local summed
seconds each; six are roughly 386. The ordinary DB prefix's 2.27 ratio is **not
transferred to Architecture**. Architecture's two-thread cap and overlapping durations
do not prove hosted speedup. The first split run's 152 rows do not describe all hosted
observations: an earlier run retained 346 completed rows without qualifying the final
population; the current run has 294. Neither is a full method cost map. Completed hosted
displays include 8 minutes 15 seconds on that older source and 5 minutes 34 seconds on
the current source. The local 253-second maximum is not a hosted upper bound; advisory
weights must retain those known slow observations rather than apply the ordinary ratio.
Six initial Architecture shards reduce a speculative initial load; actual final-source
fit, including one indivisible slow selector, remains open.

Likewise twelve initial ordinary shards lower only the advisory per-bin sum, not known
fixture/discovery costs. Base's retained three complete suite intervals must be measured
independently of their overlap with Architecture and combined with
format/build/distribution, discovery, owner and evidence costs. Unknown costs are
explicit implementation qualification gates, not a claim that base is cheap. The finite
tuning envelope avoids a new ADR for each measured adjustment inside it while preserving
review and a hard cap.

The inspected VSTest 17.12 filter contract and pinned adapter 2.8.2 support exact
case-insensitive FQN selection. Package `Microsoft.NET.Test.Sdk` 17.12.0 does not pin
the SDK console: `global.json` allows `latestFeature`, and the local observed console is
`18.6.0-release-26329-109`. A two-method local filter/real diagnostic rotation
experiment is feasibility only. Every actual hosted filter still owes independent
selection proof on its resolved console.

### Exact relationship to ADR-0062

From [ADR-0062 Decision](0062-native-crash-proof-ci-provisioning.md#decision):

> Ordinary Docker tests run on a separate VM; a fail-closed aggregate retains the
> existing required integration check name.

From [Complete partition and required
result](0062-native-crash-proof-ci-provisioning.md#complete-partition-and-required-result):

> Both execution jobs keep twenty-minute whole-job ceilings, nonempty/zero-skip
> guards, complete TRX and completed-case console evidence.

Replace only the single ordinary execution VM with the bounded, manually chosen ordinary
shard set. Preserve those requirements for every execution. Non-Docker
population/aggregate ownership and its expressly limited transient cgroup owner are
additive CI seams. This is a bounded decision change, not an ADR-0041 factual
correction. Acceptance appends a dated disclosure to ADR-0062 naming both the
replacement and additions; its Accepted body/metadata remain unchanged. Native collector
authority and excluded-host rules remain unchanged.

### What would change this choice

Reopen for an indivisible selector that cannot fit, unqualified filters or
ownership/cleanup, non-deterministic populations, uncorrected test dependence, failure
of both assignments within the finite count envelope, changed billing or platform
premises, or unacceptable queue/resource pressure. Fewer VMs or several processes per VM
remain candidates after measured complete qualification; neither permits omitted
coverage or a silent ceiling increase.

## Consequences

### Positive

- Preserve complete coverage, named proofs and the seven protected results.
- Isolate process state and give independent fixture lifetimes actual tests.
- Bound manual tuning without authorizing automatic expansion or partial success.

### Negative

- The initial plan replaces two backend/ordinary execution VMs with nineteen (one base,
  six Architecture, twelve ordinary); native remains one. Alongside two aggregates and
  five unchanged jobs, that is 27 jobs per full workflow. The organization
  plan/concurrency entitlement is unverified; not all jobs may start together. Public
  standard minutes are free, not setup/artifact capacity.
- Two distinct complete attempts add qualification work. Single/failed-job retries
  cannot reuse earlier successful siblings; operators must rerun all jobs.
- More discovery, repeated fixture setup, assignment/filter verification and aggregate
  code require maintenance. Different grouping may expose real shared state/order
  defects; those need correction, not omission or lucky rebalance.
- Queue delay extends feedback without consuming the job's running ceiling. Canceled
  queued/running jobs still leave qualification incomplete.
- Non-Docker ownership adds a fixed privileged systemd/cgroup seam on exclusive hosted
  VMs. It is accident containment, not protection from malicious PR code with the
  runner's existing sudo access. Raw provider diagnostics remain private; sanitized
  case/finite fault evidence can limit initial failure diagnosis.

### Neutral

- No application runtime, schema, permission or deployment topology changes.
- No new package, production readiness map or non-native collector authority.
- P02b-4 owns qualification; PR #32 stays open until actual checks pass.

## Implementation Notes

After acceptance, commit this decision and ADR-0062's dated disclosure before code.
Update Standards 06/20/21, contributing required-check explanations, workflow and
front-door/phase status. Update
`backend/tests/LearnStack.Tests.JobLifecycleProbe/README.md` where the execution
arrangement changes. Existing published method/Implemented counts remain until actual
enforcement changes; acceptance registers the new general rule below.

Three implementation steps, each with a commit and two fresh independent review rounds
before advancing:

1. Complete actual selector discovery, versioned plan/assignment, independent
   selection/union controls and the general CI control owner. Seed advisory weights with
   honest provenance; record unknown and repeated fixed costs.
2. Bounded execution for base/Architecture/ordinary, exact interrupted results, actual
   toolchain/phase/resource receipts and the explicit authority split. Preserve native
   assertions and correct separately verified owner faults.
3. Finite workflow and aggregate wiring, retry/name-collision controls, corpus
   reconciliation and two-assignment actual hosted qualification. Measure all
   execution/aggregate ceilings and meta's existing five-minute budget.

Final-source all-check proof owns fit; acceptance ships no test shard or passing CI
claim. There is no further application feature work in this CI follow-up.

## Architecture Tests

Acceptance registers proposed `CI_Test_Shards_Are_Complete_And_Bounded` as
**Registered**, owned by P02b-4's three steps, with proposed enforcement in
`scripts/ci-test-shards.py`, `scripts/test-ci-test-shards.py` and workflow aggregates.
It owns general populations/plan/assignment, selectors, artifact and attempt identity,
finite topology and unique required-name closure. Mark it Implemented only with actual
planted controls running in `meta` in the same change; recompute catalogue counters
then. It is not another Hangfire-only claim.

Retain `Hangfire_Invocations_And_Diagnostics_Are_Closed` for the existing native owner
and ADR-0062 assertions, `Every_Database_Test_Carries_The_Docker_Trait` for source
classification and its `The_Docker_Trait_Sweep_Can_Actually_Fail` companion. The native
designation retains ADR-0062's nonempty class/discovery closure.

Required controls include missing/duplicate/foreign selectors, all theory rows,
case-insensitive aliases, new-project/method ownership, bound/grammar refusal, wrong
plan/count/index/digest, overlap/gap, distinct A/B co-membership, incomplete TRX,
interrupted exact IDs, non-test gates, skipped/failed/missing producers, canonical-name
collisions and stale siblings on partial retry. Test the actual source owner with clean
and dirty controls, including wrong aggregate assumptions. Offline controls stay in
`meta`, not Docker/backend execution. Measure their incremental wall time against meta's
retained five-minute ceiling. Run 39148006960's complete meta wall is 56 seconds;
an initial 30-second allocation for the new offline controls is a proposed budget,
not a measured increment. Remove no existing check and claim no fit until actual
final-source meta passes with the new controls.

## References

- [ADR-0062](0062-native-crash-proof-ci-provisioning.md).
- [ADR-0060](0060-fatal-hangfire-startup-and-unsafe-unwind.md).
- [ADR-0041](0041-correcting-false-statements-in-accepted-adrs.md).
- [Testing Standards](../standards/06-testing.md).
- [Infrastructure Standards](../standards/20-infrastructure-stack.md).
- [Architecture Tests Catalogue](../standards/21-architecture-tests-catalogue.md).
- [Documentation Standards](../standards/13-documentation.md).
- [P02b-4 hosted follow-up](../roadmap/phase-02b-delivery-log.md#hosted-ci-qualification-follow-up-2026-10-18).
- [Native probe instructions](../../backend/tests/LearnStack.Tests.JobLifecycleProbe/README.md).
- [VSTest 17.12 filter contract](https://github.com/microsoft/vstest/blob/v17.12.0/docs/filter.md).
- [xUnit adapter identity producer](https://github.com/xunit/visualstudio.xunit/blob/2.8.2/src/xunit.runner.visualstudio/Sinks/VsDiscoverySink.cs#L71-L85).
- [xUnit adapter filter](https://github.com/xunit/visualstudio.xunit/blob/2.8.2/src/xunit.runner.visualstudio/Utility/TestCaseFilter.cs).
- [xUnit parallelism](https://xunit.net/docs/running-tests-in-parallel).
- [GitHub reruns](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs).
- [GitHub variables](https://docs.github.com/en/actions/reference/workflows-and-actions/variables).
- [Attempt-specific jobs](https://docs.github.com/en/rest/actions/workflow-jobs#list-jobs-for-a-workflow-run-attempt).
- [Public standard runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners#standard-github-hosted-runners-for-public-repositories).
- [Actions limits](https://docs.github.com/en/actions/reference/limits#job-concurrency-limits-for-github-hosted-runners).
- [Job timeout](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idtimeout-minutes).
- [Unique required names](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches).
- [Artifact upload contract](https://github.com/actions/upload-artifact/blob/v4/README.md).
- [Artifact download contract](https://github.com/actions/download-artifact/blob/v4/README.md).
