# ADR-0062: Native Crash Proof CI Provisioning

## Status

Proposed — approval pending.

**Date:** 2026-10-18 **Deciders:** @cemil

The revised proposal selects a dedicated disposable native-proof VM with one
fixed collector write and no restoration. It separates native and ordinary Docker
execution behind the existing required check. Both choices need maintainer
approval; neither is implemented. Read-only observation is independent of this
authority. ADR-0060 remains Accepted and its body remains intact.

## Decision Drivers

- [Run 39120501520](https://github.com/HodeTech/LearnStack/actions/runs/39120501520)
  proves a pipe-collector refusal, not permission to bypass it. Linux pipes can
  ignore `RLIMIT_CORE=0` and execute in the host's initial namespaces. Containers
  share that host kernel; they are not separate VMs.
- Both backend jobs reached their twenty-minute ceilings. Native refusals do not
  explain the unfinished ordinary suite. Full coverage, completed-case evidence,
  zero skips and all seven required check names remain mandatory.
- A qualified file collector protects the existing pre-managed-entry boundary.
  Managed-entry `PR_SET_DUMPABLE=0` is narrower authority, but leaves an earlier
  loader/CLR interval and must be reapplied after every exec.
- Restoring a pipe over potentially live work needs stronger ownership than an
  empty running-container snapshot. A disposable native-only VM avoids that
  second write, at the cost of a longer altered-policy window.

## Considered Options

1. **Separate native VM; set once and discard** (proposed selection). Keep the
   fixture unchanged, isolate its workload and collector policy from ordinary
   integration, and retain the file pattern until GitHub decommissions the VM.
2. **Save, set and restore on the full integration VM** (not selected). Shorten
   the policy window after successful cleanup, but add private original state,
   competing-value detection, positive closure of every start authority and a
   second privileged write/readback. The previous running-only census and fixed
   reserve do not establish those properties.
3. **Set once on the full integration VM** (valid simpler alternative). Avoid
   restoration without partitioning, but expose more job activity to the changed
   policy and retain the combined suite's unqualified time budget.
4. **Managed-entry nondumpability** (credible alternative, not selected). Source
   inspection and an isolated file-collector spike support the deliberate fatal
   phase. Exec resets dumpability; a setter in `Main` cannot protect startup
   before `Main`. Accepting that secret-free interval needs a different bounded
   safety decision and private-input handshake. A wrapper followed by exec does
   not close the gap. No actual pipe experiment qualifies this route.
5. **Already-qualified separate VM/kernel** (valid alternative). Avoid a host
   writer if an available environment satisfies every prerequisite. A drifting
   OS label is not evidence. Nested virtualization is experimental/unsupported
   on hosted runners and has no measured fit for this workload.
6. **Keep qualification blocked** (safe fallback). Preserve the failing check.
   The P02b-4 maintainer/implementer owns the next route decision after the
   observations below; packet/PR closeout cannot bypass this gate or count skips.
7. **Accept pipes, fake proc contents or remove cases** (rejected). These erase
   the actual no-dump prerequisite rather than qualify it.

## Decision

The proposal uses a standard GitHub-hosted `ubuntu-24.04` x64 VM exclusively for
the complete native crash-proof partition. A closed CI owner verifies an already
supported file pattern unchanged, or writes only fixed `core` to the actual
`/proc/sys/kernel/core_pattern` and verifies readback. That value remains until VM
destruction; it never restores a pipe. The unchanged fixture
independently qualifies real collector/resource/topology/native-exit evidence.
Ordinary Docker tests run on a separate VM; a fail-closed aggregate retains the
existing required integration check name. Approval authorizes this bounded CI
arrangement, not developer/shared/self-hosted/production changes or weaker proofs.

### Trust and platform boundary

- Trust reviewed workflow/actions/test code and GitHub's standard hosted-VM
  lifecycle. Hosted Linux has passwordless sudo. This is accident containment,
  not isolation from malicious PR code, another same-user/root-capable process,
  compromised actions or a compromised platform. Modes and fixed writers cannot
  supply that missing security boundary.
- Authority comes from the trusted workflow's literal standard VM selection and
  GitHub's exclusive-job contract. Require actual step-level workflow context
  for hosted/Linux/x64, no job container/services, matched run/attempt/job/source
  identity and runtime consistency checks before privilege. Environment strings,
  CPU count and virtualization hints are not cryptographic VM attestation.
- Refuse a single CPU, container markers or container virtualization, missing or
  contradictory evidence, self-hosted/shared/reusable machines and unexpected
  runner contracts.
  The OS label does not pin weekly images; retain actual image/runtime identity
  and independently requalify prerequisites every run.
- Require an empty **all-state** Docker baseline before setup. Created, running,
  paused, restarting, removing, exited, dead, unknown and failed inspections are
  not an empty baseline. Never stop an unrelated container to obtain one.

### One-way setup and independent proof

- Read the bounded actual setting without printing its command/arguments. Only
  known Apport/systemd pipe classes or supported `core`/`core.%p` file patterns
  are eligible. Unknown/unreadable/malformed settings refuse setup. A read-only
  sudo probe is a capability observation, not proof that the write will succeed.
- Supported original file patterns take a verified no-change path. Otherwise
  compare a second actual read, issue the fixed literal write and independently
  require exact `core` readback before tests. Failed or uncertain setup fails the
  job and never authorizes launch. Provision after build immediately before the
  native command to shorten the policy window.
- Use fixed privileged executable/target arguments and literal stdin, without
  shell evaluation, arbitrary sysctl keys, environment-selected commands or a
  production CLI override. Keep no original collector command on disk. No
  restoration state machine or second privileged collector write exists. No
  other sysctl, daemon configuration, mount or production policy is changed.
- The owner and test launcher lower their own inherited soft/hard core limits
  to zero. Native children still independently enforce actual limits, dump
  aliases, secret-free environment, mounts, topology, benign twins, native exit,
  sentinels and artifact assertions. No fixture accepts a pipe or substitutes
  the setup receipt for its own qualification.
- Host limits do not constrain Docker-daemon-created PostgreSQL/Ryuk containers
  or unrelated runner actions. This unchanged-fixture choice does not certify
  those processes' memory against unexpected crashes; PostgreSQL carries
  ephemeral fixture credentials. Their exposure is part of the cost below.
- Do not restore on success, failure, cancellation or uncertainty. GitHub's
  later VM destruction is a lifecycle dependency, not a teardown event guest
  code can itself witness or promise within a finite interval.

### Complete partition and required result

- Mark exactly `HangfireFatalProcessTests` and `HangfireGenerationPhaseTests`,
  including every theory/mutant row, with a shared native-proof trait. An enforced
  nonempty source/discovery census owns that designation and rejects an
  unclassified fatal proof class.
- Native uses `Requires=Docker & NativeCrashProof=true`; ordinary Docker uses
  `Requires=Docker & NativeCrashProof!=true`. Prove their disjoint union equals
  the original Docker population. Keep backend's `Requires!=Docker` filter.
  All existing/future cases retain an explicit owner; omit no assembly/case.
- Both execution jobs keep twenty-minute whole-job ceilings, nonempty/zero-skip
  guards, complete TRX and completed-case console evidence. Native success alone
  cannot make the old required integration check pass.
- Retain exact `backend integration (Testcontainers)` on an `if: always()`
  aggregate that explicitly requires **both** jobs to succeed and their complete
  expected result artifacts to validate. Missing, skipped, canceled, timed-out,
  failed or incomplete work fails it. Keep the other six required names and
  strict protection settings. Add execution jobs, not a nonrequired native proof.

### Bounded execution, diagnostics and completion

- Measure current setup, native/ordinary execution and failure cleanup/upload
  separately. Withdraw the unqualified 17:30 deadline and 150-second reserve.
  Timing constants belong to the implementing owner, not this Decision. Derive
  a finite reserve inside each existing ceiling from actual paths, including a
  full-sized completed artifact and interrupted tests. If a partition does not
  fit, fail qualification and reopen; do not extend ceilings or reduce filters.
- The planned native launcher owns one root-managed non-root cgroup v2 subtree
  through a transient systemd unit before any test process starts. Qualify fixed
  unit ownership on the hosted image; refuse tests if unavailable. Descendants
  calling `setsid` remain accounted for. Join the launcher and require its owned
  subtree's `cgroup.events` to report no live descendants. A PID/PGID, stop request
  or timeout is not positive exit evidence. Preserve completion evidence before
  unit/cgroup collection; absence after auto-removal is not that witness. Disable
  persistent build-server reuse and inspect actual testhost membership so a
  pre-existing build process cannot create test children outside the subtree.
- Docker daemon-created containers/Ryuk are outside that process subtree.
  Require actual native supervisors/children to finish under existing assertions,
  await owned disposal and bounded all-state container absence. Unknown/changing/
  unreadable state or unresolved starts fail completion. Forced termination is
  failure cleanup, never a passing native exit. No census authorizes restoration.
- An absolute deadline leaves owned termination/inspection/export time; exhausted
  setup never launches tests. Retain completed-case identity/outcome/duration in
  console output even without final TRX. An in-flight case remains censored,
  not a measured pass. Hard runner termination may prevent cleanup; it stays red.
- Preserve finite topology failure facts before private stdin: internal-network
  boolean, capped endpoint counts, expected-member booleans, running/OOM/closed
  exit class and existing allowlisted refusal. Keep exact membership equality;
  do not retry a failed check into success, export IDs/IPs/provider logs, or let
  a diagnostic fault replace the original failure.
- Inspect applicable child scratch, host collector/file destinations, workspace
  and runner-temp roots. Unexpected crash/core artifacts fail qualification and
  are never uploaded. Export only named sanitized receipts and TRX. Child proof
  does not certify absence across every VM process or destination.

## Context

Run 39120501520 has six direct exit-78 failures, sixteen exact phase pipe refusals
and seven membership-stage failures. Endpoint states were not retained; early
supervisor exit is a hypothesis for the seven, not an established cause. Its
artifact has three zero-selection TRXs, no completed Integration TRX. Backend has
2,452 Unit, 177 HTTP and four Contract passes, no completed Architecture TRX.
Both jobs hit their ceilings; silence after the last failure is not completion.
Integration setup took 2:13, leaving only 15:17 under the old proposed deadline.

Three older green Docker test steps took 12:20, 12:52 and 16:52 before this native
workload. They warn against the old reserve; they do not measure current source.
The owning [CI follow-up](../roadmap/phase-02b-events-auth.md#hosted-ci-qualification-follow-up-2026-10-18)
records the fresh read-only baseline and local corrections separately. Neither
establishes fit for an unimplemented split or a qualified native workload.

The nondumpability investigation inspected pinned Linux and .NET 10.0.10 fatal
paths and ran an isolated unprivileged file-collector spike. Each exec child
started dumpable, set/read zero, and the actual fatal child exited by SIGABRT
without finally/ProcessExit markers or a core artifact. The benign twin retained
both markers. No pipe was installed or exercised. Zero file-core limits alone
would also suppress a file artifact: this cannot prove absence of a late reset
or host helper on a pipe. It is feasibility, not replacement proof or production
qualification. Managed-entry startup coverage remains the deciding difference.

### Exact bounded supersession and acceptance vehicle

From [ADR-0060 Diagnostics, dumps and durable consequences](0060-fatal-hangfire-startup-and-unsafe-unwind.md#diagnostics-dumps-and-durable-consequences):

> Use an isolated qualified
> proof environment, inspect applicable crash/core destinations and export only
> allowlisted artifacts.

> An unsupported collector/dump precondition fails qualification
> before launch; it is not a skipped passing test or permission to change host-wide
> crash settings.

Retain isolated qualification, pre-launch refusal and all child assertions.
Replace only the lack of host-change authority for this named CI owner and its
exclusive disposable native VM's one setting. This is a new bounded decision,
not an ADR-0041 correction of a statement false when accepted. After approval,
append a dated disclosure to ADR-0060, preserving its body/acceptance metadata.
Every excluded machine keeps the original rule. Phase 11 keeps production
dump/supervision ownership.

Reopen if platform exclusivity/lifecycle changes, known collector setup cannot
qualify, owned completion cannot be observed, either partition cannot fit, or a
qualified lower-authority environment becomes preferable. P02b-4 remains blocked
for final CI/PR closure until these gates pass.

## Consequences

### Positive

- Preserve pre-managed native proof without accepting pipes or dropping cases.
- Isolate collector policy/workload from ordinary integration.
- Eliminate original-command storage and restoration over possibly live work.
- Keep seven required names while making both complete partitions mandatory.

### Negative

- A privileged VM-global change remains. Unrelated services, runner actions and
  PostgreSQL/Ryuk may have nonzero limits and produce file cores until VM
  destruction; their original crash collection can be suppressed. That longer
  policy window and ephemeral-credential exposure require explicit acceptance.
- VM discard is a platform dependency, not guest-observed finite teardown.
- Two execution jobs repeat setup/build work and consume extra runner minutes.
  The aggregate and exact partition add maintenance obligations.
- Cgroup/container ownership, failure paths and budgets still need real hosted
  qualification. Naming a mechanism does not prove it works.

### Neutral

- No product runtime, role, grant, schema, fatal policy or reservation changes.
- No protection-setting change or eighth required check is proposed.
- Local/shared machines continue to refuse unsupported collectors.

## Implementation Notes

Approval precedes acceptance/disclosure and dependent implementation. Then update
the decision index, P02b-4 record, Infrastructure/Testing Standards, probe README
and shared guidance together; retain historical failed/local receipts. Probe
README must distinguish unchanged fixture behavior from named CI provisioning.

Planned owners: `scripts/ci-native-provisioning.py` and
`scripts/ci-native-tests.py`; their `scripts/test-ci-native-provisioning.py` and
`scripts/test-ci-native-tests.py` controls also own workflow/partition results.
The native launcher owns the single timing-constant source. Production CLI
exposes no path/command/key override.
Offline tests verify original bytes, execute a temporary copy with only fixed
proc/executable constants replaced by fixtures, and verify every other byte
unchanged. Exercise real subprocess/stdin/exit semantics, not a parallel model.
Cheap controls run in `meta`, outside execution budgets. Classify new CI-only
source paths with their files.

`Hangfire_Invocations_And_Diagnostics_Are_Closed` is Implemented for its shipped
scope. These **additive provisioning/partition/launcher obligations remain pending**.
Acceptance names planned owners without promoting undelivered Asserts or counts.
Extend the Implemented assertion inventory with actual owner paths in the **same
change** that delivers and runs those controls.

Required clean/dirty controls cover:

- platform/context/container/single-CPU/unknown-collector/nonempty-baseline refusal
  before privilege; exact known classes; no-change and drift between actual reads;
- fixed literal stdin/target without evaluation; partial write, failed readback,
  no launch after uncertainty and no raw collector/environment output;
- actual cgroup membership including `setsid`, positive join/exit, live descendant/
  Ryuk/container states, deadlines and failed cleanup remaining red;
- unchanged native phases/mutants, benign twins, limits and artifact proof;
- source/discovery partition, complete nonempty/zero-skip results and fail-closed
  aggregate for failed/skipped/canceled/missing-artifact execution jobs;
- YAML identity/order/target, finite deadline/reserve, explicit exports, seven
  required names and unchanged whole-job ceilings.

Qualify actual hosted setup, both complete populations, failure cleanup and all
seven final-source checks. Source controls, local spikes and passing subsets do
not establish those outcomes. If fit/ownership fails, report the exact boundary
and obtain a revised decision; do not silently split again or weaken assertions.

## References

- [ADR-0060](0060-fatal-hangfire-startup-and-unsafe-unwind.md#diagnostics-dumps-and-durable-consequences).
- [P02b-4 CI follow-up](../roadmap/phase-02b-events-auth.md#hosted-ci-qualification-follow-up-2026-10-18).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs).
- [Infrastructure](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire),
  [Testing](../standards/06-testing.md),
  [architecture catalogue](../standards/21-architecture-tests-catalogue.md).
- [Probe README](../../backend/tests/LearnStack.Tests.JobLifecycleProbe/README.md).
- [Linux core collectors](https://man7.org/linux/man-pages/man5/core.5.html),
  [dumpability](https://man7.org/linux/man-pages/man2/PR_SET_DUMPABLE.2const.html),
  [exec reset](https://man7.org/linux/man-pages/man2/execve.2.html).
- [Linux v6.17 coredump order](https://github.com/torvalds/linux/blob/e5f0a698b34ed76002dc5cff3804a61c80233a7a/fs/coredump.c#L1077-L1134),
  [pinned .NET fatal path](https://github.com/dotnet/runtime/blob/8f030f80c0dd2722eb2f618984e9db6784765963/src/coreclr/pal/src/thread/process.cpp#L2613).
- [Cgroup v2](https://docs.kernel.org/admin-guide/cgroup-v2.html),
  [transient systemd execution](https://www.freedesktop.org/software/systemd/man/latest/systemd-run.html).
- [Hosted VM/nesting contract](https://docs.github.com/en/actions/concepts/runners/github-hosted-runners),
  [decommissioning](https://docs.github.com/en/actions/how-tos/manage-runners/github-hosted-runners/use-github-hosted-runners),
  [administrative privileges](https://docs.github.com/en/actions/reference/runners/github-hosted-runners#administrative-privileges).
- [Required dependent checks](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/troubleshooting-required-status-checks).
