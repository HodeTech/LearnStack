# ADR-0060: Fatal Hangfire Startup and Unsafe Unwind

## Status

Accepted — 2026-10-16.

**Date:** 2026-10-16 **Deciders:** @cemil

**Maintainer acceptance — 2026-10-16.** The maintainer approves the reviewed
revision at `189f3bcf` and authorizes dependent P02b-4 Step 4 implementation on
`development`. This decision uses ADR-0059's qualification/reopening gate and
selects a process boundary for opaque construction or Core unwind without
qualified normal child-join evidence, including after capture. It changes the
remedy and healthy-pool availability; fatal termination is not technically
inevitable. ADR-0047 and ADR-0059 retain their historical bodies with dated bounded
supersession amendments. Acceptance is not implementation, enforcement, worker
activation or production qualification; those gates remain mandatory.

## Decision Drivers

- Pinned Core starts private controls before synchronous supplied capture. It can
  also skip child waiting after capture, in its own startup logging/notification
  code. A later removal fence cannot reverse earlier disposal.
- Public construction can throw before any thread or after an outer dispatcher
  starts without publishing its handle. The public call does not reveal that
  instruction boundary. Core disposal can also skip removal entirely.
- H1 means stock Hangfire transports wakeups while LearnStack owns durable business
  state. The integration should retain public seams and unchanged vendor assemblies/
  SQL, without manufacturing a child join from capture, a timeout or outer completion.
- Permanent pool quarantine is a coherent availability alternative. This decision
  instead prefers an explicit process failure over continued service with unqualified
  private lifecycle ownership, accepting loss of healthy pools and HTTP work.
- A fixed diagnostic and process exit give an operator/process observer a visible
  failure boundary. The current unmapped `jobs` check and static liveness endpoint
  do not supply automated unhealthy-pool recovery. No present restart policy is assumed.
- Durable attempts, external uncertainty and reservations remain authoritative;
  parent-process death is neither external quiescence nor process-tree cleanup.

## Considered Options

1. **Fatal boundary for unqualified construction/unwind** (chosen).
   Close process-wide job-start/replacement gates and request termination with a
   closed diagnostic. This is a conservative containment/operability preference,
   not proof that every selected state has a live leaked child.
2. **Permanently quarantine the affected pool** (viable, not selected). Close its
   admission/replacement gates, retain available handles and report unhealthy;
   healthy pools/HTTP may continue. Uncaptured private controls perform control
   work, not business handlers, and may exit after their next iteration. No public
   witness proves that every control exited safely after premature disposal.
   Quarantine cannot undo that disposal, but neither can fatal termination. Both
   choices require changing ADR-0059's qualified-cleanup remedy. The decision
   chooses a visible process boundary over retaining this unresolved state;
   quarantine has not been disproved or runtime-qualified by this investigation.
3. **Ordinary host stop as the remedy** (not selected). It can stop known handles,
   but cannot reconstruct missing ones or prove an opaque unwind joined children.
   A finite host wait does not establish that private cleanup completed.
4. **Qualify an upstream ownership fix** (viable alternative). A supported version
   must expose/retain partial construction and child completion and pass H1 proofs.
   None is qualified here; no claim about future availability is made.
5. **Own/fork Core's private lifecycle** (not selected). It could expose the missing
   phases, at the cost of internal lifecycle, upgrade and distribution ownership.
6. **Implicitly treat every resource wrapper as already fatal** (rejected).
   Core's exact exception predicate does not say this. No automatic .NET OOM crash
   outcome or genuine Linux resource-failure experiment is used as the rationale.

## Decision

LearnStack selects nongraceful API-process termination for
an opaque prevalidated constructor failure or an observed Core unwind lacking
qualified normal child-join evidence. Capture alone is insufficient. A closed
owner seals process-wide starts/replacement with fixed memory transitions, then
calls `Environment.FailFast(string)` without an exception object. It reports no
successful cleanup and releases no reservation. Ordinary classified pre-child
faults and normally joined lifecycle failures retain ADR-0059; a stalled startup
is retained unhealthy ownership, not a fatal event merely because time elapsed.

### State facts and public observations

Prevalidate/freeze options, finite queues/pools, worker counts, dependencies,
components/filters and closed callbacks before invoking Core. Allocate/register
one owner and its independent startup observer first. Validation refusal before
that call is nonfatal. Publish a returned handle once, to the same owner.

| Fact / public observation | Classification |
|---|---|
| Prevalidated public constructor throws before publishing a handle | Opaque ownership failure; fatal selection |
| First confirmed announcement, then owned dispatcher disposal/removal without qualified child join | Unqualified unwind, before or after capture; fatal selection |
| Second announcement after confirmed announcement without qualified normal child-join evidence | Refuse before provider acquisition/SQL; fatal selection even if disposal skipped removal |
| Actual outer completion after confirmed announcement without required normal-join evidence | Unqualified unwind; outer completion cannot repair skipped child waiting |
| Classified announcement acquisition/SQL/disposal failure before children start | Existing retained stop fence/backoff; nonfatal |
| Guarded supplied-builder fault with all partial resources owned and normal join | Existing draining/retention; nonfatal |
| Disposal/removal/cleanup fault after qualified joins | Retain unhealthy ownership; no replacement on unresolved cleanup; nonfatal |
| Constructor/required or supplied startup remains in flight past observation deadline | Close pool starts/replacement, retain actual invocation, unhealthy `startup_stalled`; no timeout-based fatal/join |

Classified pre-child refusals take precedence over the unwind rows: the contained
announcement boundary has established that Core could not start children. Confirmed
announcement means the whole lifecycle boundary succeeded, including provider
disposal; SQL acknowledgement/uncertainty is a separate fact. Every second attempt
is refused before SQL, including known-joined but unresolved-cleanup cases; those
post-join cleanup cases remain retained/nonfatal.

The constructor-wide rule deliberately includes pre-thread failures. Core starts
its outer dispatcher before later event/shutdown registration, but the public
call gives no supported instruction-level witness. A single thread's initialization
failure can mean its managed body never ran; an inner start can follow earlier live
controls. Exception type/text does not distinguish those owner states. This
conservative overlap is an explicit availability cost, not evidence of leaked work.

Capture records the actual context synchronously before supplied workers start.
A closed final marker dispatcher's Core-facing `WaitAsync` records entry into
child-wait setup after startup logging and `NotifySucceeded`. The owner never calls
that interface to manufacture readiness. This is progress, not child completion.
Worker/fetch/business admission waits for handle publication, that progress
milestone and open gates; builders cannot block waiting for a later milestone.

There is no direct public private-child join callback. A captured shutdown-token
signal is a qualified normal child-join milestone only under the pinned closed
model: infinite Stop/Shutdown timers, contained metadata acquisition/SQL/disposal,
a closed nonthrowing diagnostic path that cannot enter Core's heartbeat restart
catch, faithful actual waits, and no final process teardown. Record the phase
synchronously before cancellation returns; a delayed continuation is insufficient.
The signal does not prove heartbeat, outer or LearnStack cleanup completion.
A raw token, wait entry, canceled wait or supplied joins alone is never that witness.
Failure to qualify these premises refuses activation and reopens the decision.

Observe owned supplied-dispatcher disposal, removal, second announcement and actual
outer completion. None alone covers all exits. Post-builder log/notification faults
can bypass waiting after capture; a required `Dispose` fault can skip supplied
observers and removal. The second-announcement permit is consumed/refused before
SQL on every path, independently of removal. A second attempt before any confirmed
announcement remains a normal closed refusal if the pre-child facts are established.
Unknown private work is not reported joined; a later fence cannot undo disposal
that preceded it. Ordinary job/control exceptions that reach qualified normal
joins do not become fatal solely because they occurred after capture.

### Stalled startup, stop priority and process-wide gates

Use the already validated `BackgroundJobRuntimeOptions.ProgressTimeout` as one
monotonic startup-observation deadline, from owner/constructor entry until Core
child-wait setup: default 75 seconds, maximum 15 minutes. Unrelated pool activity
or partial milestones do not reset it. An observer delayed by runtime starvation
has no absolute OS scheduling guarantee. A held constructor/Thread.Start keeps
one actual owned background invocation, no replacement or timeout disposal; late
publication is stopped/retained by that same owner, never promoted after closure.

Ordinary host-stop request closes global starts/replacement and retains its shared
finite wait. It neither triggers fatal termination nor excuses an independently
observed unqualified unwind. Such a failure can therefore cut the remaining normal
drain short. Final `ProcessExit`/`DomainUnload` is different: ADR-0059's pre-registered
closed final-exit gate invalidates join inference and permits its already-approved
nongraceful final teardown without claiming cleanup. Serialize fatal selection
against that gate: an already selected fatal state cannot be downgraded; final
teardown selected first closes all gates and supplies no replacement/join authority.
Host-stop request alone does not select final teardown.

Before invoking FailFast, seal process-wide job acquisition, native/business start,
replacement, independent bridge/control starts and external ExecuteIntent admission
across every pool. Check gates at actual issuance/linearization, not task scheduling.
The terminal action performs no logging, cancellation, SQL, audit or joins. Admissions
that linearized earlier may finish a COMMIT or external effect during termination;
fixed memory closure cannot retract them. Preserve their uncertainty and reservations.
No claim that the OS terminates instantly or healthy work finishes is made.

### Diagnostics, dumps and durable consequences

Precompute a finite immutable diagnostic set from registered pool keys and fixed
stages (`constructor`, `startup_unwind`, `lifecycle_unwind`) before Core starts.
Select one string at the sole no-exception FailFast sink; include no server/job/
tenant/trace ID, provider text or arbitrary callback. The supplied message is closed;
runtime stderr/stack reporting is not wholly controlled by that message.

Fatal proof children require a secret-free environment allowlist, explicit runtime
minidump/crash-report disablement including applicable legacy aliases, and verified
Linux soft/hard `RLIMIT_CORE=0` before managed entry. Qualify OS collector behavior
too: piped `core_pattern` collectors can bypass that limit. Use an isolated qualified
proof environment, inspect applicable crash/core destinations and export only
allowlisted artifacts. An unsupported collector/dump precondition fails qualification
before launch; it is not a skipped passing test or permission to change host-wide
crash settings. Production dump/access/storage/export/retention policy belongs to
[Phase 11 Reliability](../roadmap/phase-11-production-hardening.md#reliability).
No Windows dump-suppression or production-policy qualification is claimed here.

Termination interrupts all API/pool work and supplies no normal finally/exit cleanup,
in-process restart or finite OS deadline. P4 has no automatic supervisor recovery;
a subsequent host start is operator-driven. Phase 11 explicitly owns supervision,
restart/backoff/crash-loop and termination policy, plus the triggered worker-process
topology decision. A separate worker host is not authorized by this decision.

External providers and local child/process trees, including future ffmpeg work, may
outlive the API. Parent death, missing metadata and expired leases do not fence late
starts or prove quiescence. Existing attempts, committed receipts and uncertain
COMMIT/effect outcomes remain durable. Step 7 owns framework reconciliation, Phase
04 owns ffmpeg/process-artifact supervision, and Phase 03 owns operator recovery;
until those sanctioned paths prove release, quarantined reservations stay held.
Restart cannot reset attempts, release occupancy or authorize Execute again.

### Bounded supersession and acceptance vehicle

ADR-0059's reopening gate is used, not removed. Its historical body remains intact.
The following exact clauses identify the changed remedy/availability; they do not
assert that the original qualification promise was an implemented guarantee:

From [Public composition and registration](0059-owned-hangfire-server-generations.md#public-composition-and-registration):

> Startup failures,
> including faults before capture and partial dispatcher construction, must fail
> closed without pretending a ready-generation experiment proves their cleanup.
> If the public seams cannot qualify this obligation, Step 4 remains blocked and the
> decision is reopened; a timeout is never a substitute for the missing witness.

From [Concrete storage seam and sole replacement owner](0059-owned-hangfire-server-generations.md#concrete-storage-seam-and-sole-replacement-owner):

> Required-process construction and partial public-constructor
> failures still require actual cleanup qualification; a removal fence alone cannot
> undo an earlier premature child disposal. If those public paths cannot be safely
> qualified, startup refuses and this choice reopens. No second owner or server is
> created while the previous constructor, stop or cleanup remains unresolved.

ADR-0059 Option 1 states “Other pools remain serviceable when their dependencies
are healthy.” Option 4 rejects whole-host drain on every heartbeat fault and states
“Fatal process failures remain the deployment supervisor's concern.” This decision
preserves ordinary heartbeat retry/drain, but expressly selects application-detected
fatal refusal for the opaque/unqualified states above, including during replacement
of a previously healthy pool. Initial boot refusal can terminate the API; runtime
replacement refusal can terminate an already serving host. It deliberately qualifies
the healthy-pool/HTTP availability promise at that boundary. The supervisor still
owns subsequent recovery; no present supervisor is inferred.

The same whole-process exception additionally qualifies the original progress
clauses still preserved by
[ADR-0047 Amendment 2](0047-transactional-background-job-runtime.md#amendment-2--owned-server-lifecycle-and-bounded-availability-supersession-2026-10-16):

> A lost worker may reduce availability by quarantining capacity; unrelated tenants
> and transactional pools remain serviceable.

> Retain/observe/renew all admitted work through real cleanup; affected job
> identities remain blocked while other pools/tenants progress.

Amendment 2 expressly preserves “Other healthy pools remain independent subject to
shared dependencies” and selects “No wider progress or whole-record supersession”.
This decision adds the named fatal-boundary exception to that narrower availability
reading, preserving normal draining/retry and all business/capacity authority.

Only the selected unqualified construction/unwind remedy, conservative overlap and
whole-process availability change. Actual cleanup is not retroactively claimed.
No second owner/replacement, durable authority, attempts/reservations and ordinary
qualified ownership remain unchanged. Acceptance appends dated bounded-supersession
amendments to both ADR-0059 and ADR-0047, naming these deltas and preserving bodies,
metadata and previous amendments. No factual erratum or whole-record retirement
is selected. ADR-0058's ordinary shared-component/maintenance contract is unchanged
and needs no additional amendment for this exception.

## Context

After ADR-0059 acceptance at `6c52eaa1`, independent source inspections identify
required-before-capture construction that skips child waiting. Subsequent review
identifies the analogous post-capture Core startup/notification and skipped-removal
paths. Synchronous capture supplies no missing child-join guarantee.

.NET 10.0.10 distinguishes native thread creation's exact OOM from initialization
failure wrapped in ThreadStartException. Core excludes exact OOM/StackOverflow
from its catches, not that wrapper. Its startup synchronization also has no
application deadline. These are source facts, not genuine Linux reproduction,
frequency estimates or proof of automatic OOM process termination. A hostile logger,
invalid input or synthetic fault is not relabelled a normal production incident.

Nineteen earlier ADR-0059 feasibility controls retain their original limited scope.
Synthetic Core fault placement can test the owner state machine/unwind consequence;
it is not an unmodified production resource failure. Real pinned worker/PostgreSQL,
closed logger, parity, cancellation/exit and lifecycle qualification remain mandatory.
The independent committed bridge/storage foundation at `422519b0` implements no
fatal policy, complete generation owner, business completion or recurrence engine.

**What changes this decision:** qualified upstream ownership, loss of a public
state discriminator, or inability to qualify the conditional join model. Any such
loss refuses activation/reopens the decision; no timeout or implicit fork substitutes.
[Phase 11](../roadmap/phase-11-production-hardening.md#reliability) owns a worker-process
separation decision when measured shared failure/contention breaches approved
availability targets or independent scaling is required. If that trigger fires
before Phase 11, the active consumer phase obtains a new ADR first.

**Explicitly outside this decision:** resource-failure incidence studies, new vendor/
runtime pins, separate worker implementation, production supervision and business
recovery APIs. They have the named owners above; no current operational readiness
is asserted by assigning that work.

## Consequences

### Positive

- Prefer an explicit diagnostic/process-failure boundary to silent unqualified service.
- Preserve stock H1 and ordinary qualified ownership through public seams.
- Keep durable uncertainty/reservations authoritative across failure.

### Negative

- A conservative constructor classification can terminate the API even with no live
  child; the source-level fault class has no genuine Linux reproduction here.
- Healthy HTTP/pools and an ongoing normal host drain can be interrupted.
- No graceful cleanup or automatic P4 recovery; surviving external children and
  quarantined reservations can require later sanctioned operator reconciliation.

### Neutral

- No role, grant, schema, business definition or generic transaction owner changes.
- Fatal and quarantine are policy alternatives; neither proves private cleanup.
- No exactly-once, positive quiescence or successful OS termination claim.

## Implementation Notes

P02b-4 Step 4 owns phase facts, startup observation, global gates, the closed fatal
capability and child proofs; Step 9 owns full caller enforcement/regression. This
acceptance updates ADR-0059 and ADR-0047 by dated amendments, the decisions
index/current phase/front doors, Standards 20 § Background Jobs and Hangfire,
Standards 21's named assertions below, and architecture 15 § Background job transport
and maintenance before dependent implementation. Phase 11's explicit
supervision/dump/topology work is a carrier.
No catalogue status/count promotion or runtime proof is claimed by acceptance.

## Architecture Tests

- `Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role` owns exact prevalidation,
  constructor/phase/conditional-join premises and the nonempty actual termination
  capability/sink census. Pin the sole `Environment.FailFast(string)` call, frozen
  diagnostic selection and its closed owner/callers; reject exception overloads,
  alternate `Environment.Exit`/`Process.Kill` sinks, forwarding and direct/helper/
  interface/delegate/async escapes with dirty/clean controls. Structural census
  does not prove state dominance; child controls supply that evidence.
  Future child-process supervision needs its own enumerated capability; it cannot
  borrow a blanket exemption to terminate the API.
- `BackgroundJob_Worker_Pools_Reserve_Independent_Capacity` owns constructor and
  before/after-capture unwind, post-builder log/NotifySucceeded, disposal-skips-removal,
  second announcement, outer-completion and stalled-start controls. Exercise
  process-wide gate races, late handle publication and host-stop/final-exit priority.
  Held work is not reported joined; no replacement or new admission escapes closure.
  Ordinary pre-child faults, qualified builder/join/cleanup and heartbeat drain remain
  nonfatal. Synthetic source placement is labelled separately from real stock proof.
- `Hangfire_Invocations_And_Diagnostics_Are_Closed` owns finite pool/stage diagnostics,
  no exception object, child environment/resource/collector prerequisites, no dump
  artifact and normal-finally/exit marker distinction. Suppressing the fatal gate
  must make the same child control fail; normal host/final exit must not falsely
  report an unsafe construction event.
- Existing capacity/external-recovery obligations retain durable attempt/intent/
  reservation and surviving process-tree controls in Steps 5–7/9. A fatal exit or
  later API restart cannot be the positive release witness.

All extended assertions remain pending/Registered until their owners ship. Loss of
any required discriminator, join premise, collector precondition or decisive control
refuses qualification; no skip or synthetic-to-production inference closes the gate.

## References

- [ADR-0047](0047-transactional-background-job-runtime.md),
  [ADR-0058](0058-bounded-stock-hangfire-maintenance.md),
  [ADR-0059](0059-owned-hangfire-server-generations.md).
- [P02b-4 Step 4 acceptance](../roadmap/phase-02b-delivery-log.md#maintainer-acceptance-and-step-4-adaptation-2026-10-16).
- [Phase 11 Reliability](../roadmap/phase-11-production-hardening.md#reliability).
- [Phase 04](../roadmap/phase-04-cms-media-pages.md),
  [Phase 03](../roadmap/phase-03-identity-admin.md).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs),
  [Infrastructure Standards](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire),
  [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md),
  [Events and Outbox architecture](../architecture/15-event-and-outbox.md#background-job-transport-and-maintenance).
- [Pinned Core server process](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundServerProcess.cs#L99-L125).
- [Pinned public constructor](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundProcessingServer.cs#L96-L124).
- [Pinned dispatcher](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Processing/BackgroundDispatcher.cs#L55-L88).
- [Pinned execution loop](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Processing/BackgroundExecution.cs#L259-L269).
- [Pinned catchability predicate](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/ExceptionTypeHelper.cs#L27-L34).
- [.NET 10.0.10 native-create OOM](https://github.com/dotnet/runtime/blob/v10.0.10/src/coreclr/vm/comsynchronizable.cpp#L252-L260).
- [.NET 10.0.10 startup synchronization](https://github.com/dotnet/runtime/blob/v10.0.10/src/coreclr/vm/comsynchronizable.cpp#L290-L300).
- [.NET 10.0.10 initialization](https://github.com/dotnet/runtime/blob/v10.0.10/src/coreclr/vm/threads.cpp#L1729-L1799),
  [startup-failure wrapper](https://github.com/dotnet/runtime/blob/v10.0.10/src/coreclr/vm/threads.cpp#L1885-L1927).
- [FailFast semantics](https://learn.microsoft.com/en-us/dotnet/api/system.environment.failfast?view=net-10.0),
  [runtime dump/crash-report configuration](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/collect-dumps-crash),
  [Linux core limits and pipe collectors](https://man7.org/linux/man-pages/man5/core.5.html).

## Amendments

### Amendment 1 — native CI provisioning boundary (2026-10-18)

[ADR-0062](0062-native-crash-proof-ci-provisioning.md), Accepted 2026-10-18,
boundedly supersedes only this record's lack of host-change authority for its
named CI provisioning owner. The original requirement remains:

> An unsupported collector/dump precondition fails qualification
> before launch; it is not a skipped passing test or permission to change host-wide
> crash settings.

The new owner may verify an already supported file pattern or set only fixed
`core` on a standard exclusive disposable GitHub-hosted native-proof VM. It never
restores a pipe; VM decommission is the platform backstop. Native children retain
all independent preconditions, topology, benign/mutant and no-dump assertions.
Ordinary Docker execution stays on a separate VM; both complete partitions remain
mandatory behind the existing integration result, with seven required names and
twenty-minute execution ceilings unchanged. All excluded machines retain the
original host-change restriction and isolated-environment requirement.

This is a newly approved authority, not an ADR-0041 correction. This record's
Accepted body, acceptance metadata, fatal policy and durable-state guarantees
remain intact. Provisioning/partition/launcher implementation and their actual
hosted qualification remain pending at acceptance; no passing result is inferred.
