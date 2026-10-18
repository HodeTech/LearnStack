# ADR-0062: Native Crash Proof CI Provisioning

## Status

Proposed — approval pending.

**Date:** 2026-10-18 **Deciders:** @cemil

This proposes one bounded exception to [ADR-0060](0060-fatal-hangfire-startup-and-unsafe-unwind.md#diagnostics-dumps-and-durable-consequences):
explicit provisioning of the disposable integration-job VM's core collector.
Approval precedes implementation. No host setting or dependent workflow is changed
by this draft. ADR-0060's Accepted body remains intact.

## Decision Drivers

- [Run 39120501520](https://github.com/HodeTech/LearnStack/actions/runs/39120501520)
  reports exact `collector_pipe` refusals from the actual Linux container
  preflight. The collector command/service is not identified by this evidence.
- Linux piped collectors can bypass `RLIMIT_CORE=0`. Accepting that prerequisite
  would weaken the existing no-dump proof; another container shares the kernel.
- ADR-0060 explicitly withholds authority to change host-wide crash settings.
  A test failure cannot silently authorize a VM-global change.
- The existing full integration filter, seven required checks and twenty-minute
  ceilings remain binding. Native qualification cannot become a passing skip.

## Considered Options

1. **Provision the exclusive disposable hosted VM** (proposed selection). Save
   the original privately, configure fixed `core`, independently qualify the
   unchanged fixture, and restore only after positive container exit.
2. **Supply an already-qualified separate VM/kernel** (valid alternative). Avoid
   mutation, but require an actually verified compatible runner. A different
   drifting Ubuntu label is not evidence that its collector qualifies.
3. **Keep qualification blocked** (safe fallback). Preserve refusal and fail the
   required check. No native safety assertion is weakened, but delivery waits.
4. **Accept pipes, fake proc contents or remove cases** (rejected). None proves
   that the actual host collector cannot receive a fatal child's memory.
5. **Design a new native no-dump launcher** (not selected). This needs separate
   pre-runtime/exec-inheritance proofs and changes more than CI provisioning.

## Decision

If accepted, LearnStack permits a closed provisioning helper in the existing
`backend integration (Testcontainers)` job to set only
`/proc/sys/kernel/core_pattern` to `core` on that job's exclusive, disposable
GitHub-hosted Linux VM. It privately saves and verifies the original first,
restores it after positive absence of running containers, and verifies the
restoration. Developer, production, shared and self-hosted machines are excluded.
The unchanged native fixture independently enforces every existing prerequisite.

### Authority and private state

- Require the workflow's actual `runner.environment == 'github-hosted'` context,
  Linux, `GITHUB_ACTIONS=true`, `RUNNER_ENVIRONMENT=github-hosted` and valid
  run/attempt/job identity. Refuse container-hosted/single-CPU runners; only the
  existing standard `ubuntu-latest` VM job is authorized. These checks must fail
  the job on mismatch, rather than skip the setup and continue tests.
- Require no running containers before mutation. Never stop an unrelated
  container or infer that it belongs to this job.
- Use a fresh identity-bound directory under canonical `RUNNER_TEMP`, owned by
  the current runner user with mode 0700. Original bytes and versioned
  ownership/phase files are regular, single-link, private 0600 files. Reject
  foreign, pre-existing, symlink, malformed or missing required state.
- Bound the original to one kernel setting; reject NUL or unexpected line
  structure. Save it durably and compare a second actual read before mutation.
  Do not print/upload the collector command or private state.
- Record mutation-pending before the fixed write; mark applied only after exact
  actual `core` readback. A partial or failed write remains uncertain and needs
  cleanup. An already compatible `core` or `core.%p` original must take a
  verified no-change path, retaining its owned state and independent readbacks.
- Pass bytes through stdin to a fixed privileged writer and fixed proc target.
  No shell evaluation, arbitrary sysctl key, environment-selected command or
  production CLI path override is allowed. Test injection cannot widen the CLI.

### Execution and cleanup

- Setup gates the existing full Docker command. Shell/managed preflight still
  reads the real setting and inherited soft/hard zero limits before fatal entry.
  CLR dump aliases, environment, mounts, exact topology, native exit, benign
  twins, sentinels and artifact assertions remain unchanged.
- An `always()` cleanup step precedes artifact upload and covers partial setup
  and test failure. Reserve 150 seconds from the twenty-minute job before
  checkout: the full test command has an absolute deadline at job start plus
  seventeen minutes thirty seconds, up to 35 seconds for owned termination,
  sixty seconds for cleanup, 35 seconds for artifact upload and twenty seconds
  for runner transitions/finalization before the whole-job ceiling.
  Setup is bounded within the test-phase allocation. Deadline exhaustion fails;
  it never reduces the full filter or counts incomplete results as passing.
  Failed setup never launches tests. Actual fit remains to be qualified.
- Before restoring a potentially piped original, require no running containers
  after a bounded wait for ordinary owned disposal. First positively finish/join
  the owned test launcher and any remaining container-start authority; seal new
  starts before the final census. A timeout is not exit evidence. Do not
  force-stop containers and relabel the test as successful.
- Restore the exact original through stdin and independently verify readback.
  Any failed write/readback, invalid state or unresolved running container fails
  cleanup and the job. Keep private evidence until verified restoration; publish
  only fixed categories and booleans.
- If child absence is unconfirmed, retain the safer `core` state and fail the
  job; do not restore a pipe over potentially live fatal work. VM destruction is
  the backstop. Hard runner termination can prevent `always()`; unconditional
  restoration under OS/agent loss is not promised. Reusable runners are excluded.
- The helper and host test launcher lower their own inherited soft/hard core
  limits to zero. This does not alter unrelated process limits. The child proof
  is not a VM-wide no-dump claim: existing host services can have different
  limits/destinations while the VM-global pattern is applied. Inspect applicable
  host/container destinations and workspace/runner-temp artifact roots; unexpected
  core artifacts fail qualification and are never uploaded. Export only named
  sanitized receipts and TRX files. Residual host-service exposure lasts until
  restoration or VM destruction; no absence claim covers every host daemon.
- Remove only the helper's owned state after verified restoration. No other
  sysctl, service, Docker privilege or production policy changes.

## Context

The completed hosted run fails the existing prerequisite correctly. Its direct
probes observe refusal exit 78; phase probes preserve exact pipe refusals. Some
earlier topology failures may race supervisor exit, but their endpoint states
were not retained and no independent topology regression is established.

This is a new authority decision, not an ADR-0041 factual correction or permission
inferred from failed CI. Acceptance adds a dated bounded-supersession disclosure
to ADR-0060; its original no-host-change rule continues everywhere outside this
named provisioning owner. The fixture itself never changes the host.

GitHub documents a fresh VM for each standard hosted job, with a single-CPU
exception. That lifecycle is an explicit dependency of this choice. Reopen it if
the runner becomes reusable, shared or container-hosted, if provisioning cannot
qualify, or if an already-qualified separate kernel becomes the preferable route.
Phase 11 retains production dump/supervision ownership.

## Consequences

### Positive

- Actual native proofs can run without accepting unsafe collector prerequisites.
- Setup and restoration become explicit, falsifiable CI operations.
- Local/shared-host refusal, test scope and required-check limits remain intact.

### Negative

- A privileged VM-global setting affects the exclusive job while applied.
- Unrelated host processes can retain different dump limits; container-child
  no-dump evidence does not certify all host processes or destinations.
- Restoration can fail or be interrupted; unconfirmed cleanup fails qualification.
- The helper needs ownership/partial-write controls and actual hosted evidence.

### Neutral

- No product runtime, role, grant, schema, fatal policy or reservation rule changes.
- A separately qualified VM remains a valid alternative.

## Implementation Notes

Only after approval, commit acceptance and ADR-0060's dated disclosure before
dependent implementation. Update the decision index, P02b-4 CI follow-up,
[Infrastructure Standards](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire),
probe README and shared test guidance. Preserve historical failed/local receipts.

Extend the existing `Hangfire_Invocations_And_Diagnostics_Are_Closed` assertion
inventory in the [catalogue](../standards/21-architecture-tests-catalogue.md),
with the helper/workflow control suite's actual owner path. Add no new canonical
rule or implied status/count promotion. Execute the real helper with temporary
state and stubbed proc/privileged commands, preserving the actual subprocess and
stdin path. Require clean/dirty controls for:

- platform/identity/baseline refusals before privilege; ownership/mode/symlink
  rejection; save-before-write ordering and changed original;
- partial write, failed readback, no-change original and setup-error cleanup;
- exact literal stdin containing shell metacharacters, without execution/logging;
- successful/failed test cleanup; failed restore, foreign state and live
  containers or surviving start authorities failing without unsafe restoration;
- owned host limits, unexpected core artifacts and explicit export allowlist;
- actual YAML setup/test/always-cleanup order, absolute test deadline/reserved
  cleanup, and unchanged name/filter/whole-job ceiling.

Qualify actual hosted setup, all native cases/mutants, full Docker execution and
verified restoration, then all seven checks at the final source. Offline controls
alone do not establish hosted success or fit within the job limit.

## References

- [ADR-0060](0060-fatal-hangfire-startup-and-unsafe-unwind.md#diagnostics-dumps-and-durable-consequences).
- [P02b-4 local closeout](../roadmap/phase-02b-events-auth.md#p02b-4-local-closeout-2026-10-18).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs).
- [Probe README](../../backend/tests/LearnStack.Tests.JobLifecycleProbe/README.md).
- [Linux pipe collectors](https://man7.org/linux/man-pages/man5/core.5.html),
  [kernel core_pattern](https://docs.kernel.org/admin-guide/sysctl/kernel.html#core-pattern).
- [GitHub-hosted runner lifecycle](https://docs.github.com/en/actions/concepts/runners/github-hosted-runners).
- [GitHub-hosted VM decommissioning](https://docs.github.com/en/actions/how-tos/manage-runners/github-hosted-runners/use-github-hosted-runners).
