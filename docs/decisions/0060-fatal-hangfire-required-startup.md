# ADR-0060: Fatal Hangfire Required Startup

## Status

Proposed — maintainer approval pending.

**Date:** 2026-10-16 **Deciders:** @cemil

This proposal follows ADR-0059's explicit startup-qualification escape gate.
It would replace only its unconditional actual-cleanup/no-early-disposal promise
for inaccessible required-process construction before synchronous capture and
partially constructed public Core servers. It authorizes no dependent runtime.
ADR-0059 remains Accepted; worker activation remains unqualified.

## Decision Drivers

- Pinned Core starts its metadata heartbeat and mandatory control dispatchers
  before LearnStack's first supplied capture builder. Their construction is not
  exposed through a public replacement or interception seam.
- A required-construction exception can skip the child join and dispose already
  started controls before storage removal reaches LearnStack's stop fence.
  Keeping an uncaptured generation owned cannot reverse that disposal.
- Official .NET 10.0.10 source has a thread-startup wrapper exception distinct
  from exact OutOfMemoryException. Core's exact-type catchability exclusion does
  not itself exclude that wrapper. This is a source-level resource boundary;
  no ordinary SQL/configuration fault or genuine Linux resource failure has been
  reproduced by this investigation.
- H1 should keep stock assemblies/SQL and the public lifecycle seam. Pretending
  startup was qualified, or silently broadening the fatal-runtime exclusion,
  would contradict ADR-0059's explicit gate.
- Durable business attempts, external uncertainty and reservations must survive
  process failure. A missing thread/server is never positive quiescence.

## Considered Options

1. **Fatal process boundary for unowned required startup** (recommended).
   Close acquisition/replacement gates and terminate the API process with a
   fixed diagnostic. Accept interrupted unrelated traffic and no graceful
   cleanup claim for this narrowly identified construction boundary.
2. **Retain an uncaptured generation indefinitely** (not selected). It prevents
   replacement/business admission in that pool, but leaves Core's premature
   control disposal unchanged and cannot meet the literal accepted promise.
3. **Call ordinary host stop after this failure** (not selected). It still runs
   cleanup against a partially constructed private owner and can wait on lost
   child handles. It does not establish the missing join-before-dispose witness.
4. **Qualify an upstream lifecycle fix** (viable alternative). A concrete
   supported version must expose/retain the actual required construction and
   completion handles and pass all H1 regressions. None is qualified here;
   no assertion about the availability of a future fix is made.
5. **Own/fork Core's private required lifecycle** (not selected). It could make
   partial construction observable, but expands vendor lifecycle/upgrade and
   distribution responsibility beyond the selected stock H1 integration.
6. **Declare every resource-startup wrapper implicitly fatal** (rejected).
   The existing exact exception predicate and explicit qualification obligation
   do not say that. The exception and availability consequence need approval.

## Decision

**Proposed decision:** LearnStack treats failure of an already prevalidated Core
server constructor, or termination of required startup before synchronous capture,
as an unrecoverable API-process boundary. A closed owner closes new starts and
replacement gates using fixed memory operations and invokes
`Environment.FailFast(string)` with one constant message and no exception object.
It creates no replacement,
reports no graceful cleanup and releases no durable business reservation.
Ordinary announcement/storage faults and captured supplied-builder failures retain
ADR-0059's normal ownership, join and replacement policy.

### Exact state-based trigger

Validate/freeze public options, queues, worker counts, component/filter inventory,
dependency identity and closed callbacks before constructing any Core server.
An ordinary validation refusal fails startup before construction; it does not
enter this fatal path.

Allocate the generation owner before calling the actual public Core constructor.
If that constructor throws after invocation begins, there may be an unpublished
live server thread. A catchable failure cannot be treated as a clean retry merely
because the handle was not returned. The owner invokes the fatal boundary without
claiming constructor cleanup. Exact fatal runtime exceptions remain outside a
guarantee that managed handling/diagnostics will run at all.

Within an announced generation, the first supplied builder publishes synchronous
capture before supplied workers can start. If the removal boundary is reached
after confirmed announcement without that capture, required construction did not
reach the supported owner milestone. Invoke the fatal boundary before another
announcement or replacement. This trigger uses owned phase facts; it does not
parse provider text, inspect private fields or install first-chance hooks.

Announcement acquisition/SQL/disposal failures before children start are separate
classified lifecycle failures. They retain their normal stop fence and bounded
backoff; a database outage alone is not a fatal-startup trigger. After capture,
supplied-builder guards own their actual partial resources and join through the
normal policy. Captured execution faults, missing metadata, heartbeat exhaustion,
slow cleanup and a normal local drain do not become fatal under this ADR.

### Process and durable-state consequences

The fatal boundary affects the entire API process, including healthy pools and
in-flight HTTP requests. It does not promise an in-process restart, graceful host
shutdown, a finite OS termination deadline or completed cleanup. P4 has no mapped
readiness or automatic process supervisor; recovery requires a subsequent host
start. Phase 11 still owns supervision, restart/backoff and termination policy.

Use only a constant message and the overload without an exception object. Do not
invoke logging sinks, module callbacks, cancellation callbacks, SQL, audit writers
or per-pool shutdown waits on this path. FailFast bypasses normal finally/exit
cleanup; a child-process proof must distinguish it from an ordinary shutdown.
The runtime/OS can emit diagnostics or dumps independently of the supplied text.
P4 proof children use an explicit environment allowlist and disabled dump creation;
no dumps or secrets become test/CI artifacts. Phase 11 owns production dump policy.

No business handler has been admitted by the uncaptured generation. Other pools
may already have admitted work. Their committed receipts and uncertain COMMIT/
external outcomes remain durable for the existing lock/reconciliation paths.
No process-exit signal, missing metadata, elapsed lease or restart proves an
external effect stopped, frees occupancy or resets/spends a business attempt.
Normal resource retention, positive quiescence and atomic completion remain
unchanged outside this fatal boundary.

### Bounded supersession and acceptance vehicle

Only ADR-0059's public-constructor/required-process startup qualification changes:

- [Public composition and registration](0059-owned-hangfire-server-generations.md#public-composition-and-registration)
  currently requires before-capture/partial startup cleanup qualification.
- [Concrete storage seam and sole replacement owner](0059-owned-hangfire-server-generations.md#concrete-storage-seam-and-sole-replacement-owner)
  specifically requires actual cleanup for required-process construction and
  partial public constructors, refusing/reopening if the public path cannot qualify.

This new ADR would replace that unconditional cleanup obligation only with the
state-based fatal boundary above. It does not claim Core's disposal becomes safe,
that the original statement was false when accepted, or that the complete record
is superseded. Acceptance appends a dated ADR-0059 Amendment naming this change;
its Accepted body/metadata remain intact. ADR-0047/0058 business/maintenance
contracts and all ordinary ADR-0059 lifecycle/availability promises are preserved.

## Context

After acceptance at `6c52eaa1`, two independent source/design inspections identify
the required-startup ordering gap. Core prepends private watchdog/cancellation
builders; a failure before supplied capture skips WaitForDispatchers. Disposal of
the heartbeat and prior children precedes RemoveServer. The later storage fence
can stop repetition but cannot reverse these disposals.

Official .NET 10.0.10 source separates native-thread creation's exact OOM from a
post-creation initialization failure wrapped in ThreadStartException. The Unix
stack/TLS path is not only the Windows COM path. This source inspection establishes
neither frequency nor a real Linux fault reproduction. Invalid inputs, hostile
thread factories and a replaced throwing global logger are not offered as ordinary
supported-production counterexamples.

The nineteen ADR-0059 feasibility controls prove their recorded mechanisms, not
this private startup path. A synthetic required-construction injection can prove
Core's unwind order and the fatal owner trigger; it cannot be labelled a genuine
runtime resource-failure reproduction. Real worker/PostgreSQL proofs remain
mandatory for ordinary supported operation. No fatal runtime is implemented by
this Proposed record; independent bridge/closed-diagnostic/parity work may proceed.

**What changes this decision:** qualified upstream partial-construction ownership
or loss of the state-based trigger. Missing trigger/witness refuses activation;
it never permits an unrecorded fork, implicit cleanup claim or live replacement.

**Explicitly outside this decision:** runtime/resource-failure frequency studies,
new runtime/provider pins, a separate worker process/service, process supervision
and business recovery APIs. Existing Steps 5–9/Phase 03/Phase 11 retain their owners.

## Consequences

### Positive

- State the actual stock startup boundary without claiming unavailable cleanup.
- Preserve stock H1 and normal generation ownership through public seams.
- Keep durable business uncertainty/reservations authoritative across failure.

### Negative

- One pool's required startup failure can terminate unrelated HTTP/pool work.
- This source-level resource path has no genuine fault reproduction here.
- Failure produces no graceful cleanup and may require operator-driven restart.

### Neutral

- No role, grant, schema, business definition or new generic transaction owner.
- No new claim of quiescence, exactly-once effects or successful OS termination.

## Implementation Notes

P02b-4 Step 4 owns the closed startup phase/termination capability and child-process
proof. Step 9 owns exact production caller censuses and full regression. On
acceptance, update ADR-0059 by dated bounded-supersession disclosure, decisions
index, current phase/front-door status and Standards 20/21/architecture 15.
No current catalogue status/count or enforcement claim changes at proposal time.

## Architecture Tests

Extend the existing canonical `Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role`
and `BackgroundJob_Worker_Pools_Reserve_Independent_Capacity` obligations:

- Nonempty actual owner/termination caller census; no module/generic callback.
- Child-process constructor/required-before-capture failure controls stop without
  a second announcement or business admission, including an unpublished handle.
- A held prior control operation is not reported joined; normal finally/exit
  markers distinguish fatal termination from successful cleanup.
- Constant diagnostic/no raw exception, isolated environment and no dump artifact.
- Dirty suppression of the fatal gate must fail; ordinary announcement fault,
  captured builder fault, storage retry, local drain and host stop stay nonfatal.
- Durable attempt/intent/reservation state is not cleared by a fatal exit; later
  recovery retains existing uncertainty/quiescence rules.

These controls remain pending, and synthetic failure placement is explicitly
separate from any future genuine runtime resource-failure experiment.

## References

- [ADR-0047](0047-transactional-background-job-runtime.md).
- [ADR-0058](0058-bounded-stock-hangfire-maintenance.md).
- [ADR-0059](0059-owned-hangfire-server-generations.md).
- [P02b-4 Step 4 acceptance](../roadmap/phase-02b-events-auth.md#maintainer-acceptance-and-step-4-adaptation-2026-10-16).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs).
- [Infrastructure Standards](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire).
- [Events and Outbox architecture](../architecture/15-event-and-outbox.md#background-job-transport-and-maintenance).
- [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Pinned Core server process](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundServerProcess.cs).
- [Pinned Core dispatcher](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Processing/BackgroundDispatcher.cs).
- [.NET 10.0.10 thread-start dispatch](https://github.com/dotnet/runtime/blob/v10.0.10/src/coreclr/vm/comsynchronizable.cpp#L268-L277).
- [.NET 10.0.10 startup-failure wrapper](https://github.com/dotnet/runtime/blob/v10.0.10/src/coreclr/vm/threads.cpp).
- [FailFast process semantics](https://learn.microsoft.com/en-us/dotnet/api/system.environment.failfast?view=net-10.0).
