# ADR-0059: Owned Hangfire Server Generations

## Status

Proposed — maintainer approval pending.

**Date:** 2026-10-16 **Deciders:** @cemil

This proposal changes the H1 server restart policy and adds a generation owner;
it does not accept itself or authorize dependent runtime. Accepted ADR-0047 and
ADR-0058 remain the current contracts. P02b-4 Step 3 is complete; Step 4's
lifecycle qualification exposed the issue below before worker activation.

## Decision Drivers

- ADR-0047 bounds actual retained work and requires independent pool progress.
  A count of configured workers is insufficient if old server generations remain
  live while replacement dispatchers start.
- ADR-0058 retains resources through actual completion and gives host shutdown
  one finite external join budget. Canceling a wait does not complete its work.
- Pinned Core 1.8.25 cancels its restart-linked shutdown token, abandons an
  unfinished child join and disposes the child's cancellation state. An isolated
  assertion-bearing experiment reproduces two live generations and disposed
  old-context tokens even with an infinite internal shutdown option.
- H1 should retain stock workers, schedulers, SQL, assemblies and public extension
  seams. It must not silently acquire a vendor fork or private-reflection owner.
- Missing vendor server metadata is not proof that business or external work
  stopped. An operational restart cannot release a reservation or reset attempts.

## Considered Options

1. **LearnStack owns replacement after actual generation cleanup** (recommended).
   Public composition retains stock processes; a closed heartbeat adapter requests
   draining rather than allowing Core to replace live children. Other pools remain
   serviceable. A replacement waits for actual stock and LearnStack cleanup.
2. **Keep stock automatic restart and increase the connection pool** (rejected).
   Repeated restarts can overlap generations. A larger pool neither retains their
   cancellation state nor proves a finite topology or independent capacity.
3. **Use only an infinite stock shutdown option** (rejected). It prevents the
   normal shutdown timer abandoning child joins, but restart cancellation bypasses
   it. The stock experiment reproduces this exact configuration.
4. **Drain the whole API host on every heartbeat fault** (not selected). It avoids
   automatic replacement but makes a pool-local control fault interrupt unrelated
   API traffic and healthy pools. It also discards the normal transient retry
   window. Fatal process failures remain the deployment supervisor's concern.
5. **Qualify an upstream lifecycle fix** (viable alternative). A concrete release
   must prove ownership, the full dependency graph and all H1 regression controls.
   No such replacement has been qualified; availability is not assumed.
6. **Fork Core's internal generation owner** (not selected). It could preserve
   automatic restart semantics, but takes responsibility for internal worker,
   watchdog, heartbeat and cancellation lifecycles and their upgrades.

## Decision

**Proposed decision:** LearnStack owns each pool's Hangfire server generation and
replaces it only after actual stock dispatchers, admitted work, callbacks and
cleanup from that generation finish. A public compositor uses pinned stock Core
workers/schedulers and the existing guarded provider. A closed heartbeat adapter
converts missing-server or exhausted heartbeat faults into pool draining; it does
not let stock Core start a replacement over live work. The host's external shutdown
wait remains finite and shared. Approval and real qualification precede activation.

### Public composition and registration

Use public `BackgroundProcessingServer` and dispatcher-builder seams rather than
the convenience `BackgroundJobServer` constructor. Construct stock `Worker`,
`DelayedJobScheduler`, `RecurringJobScheduler`, factory, performer and state changer
through their public APIs. Do not use the obsolete custom-performer constructor,
copy vendor source, replace SQL or depend on private fields/reflection.

Only the control pool runs delayed/recurring schedulers. Business pools retain
their disjoint stock workers and Core's required server processes. Every stock
server excludes automatic provider-component hosting: one API-instance owner,
independent of all server generations, runs the shared stock fetched-job heartbeat
and ADR-0058's expiration owner. Draining the control pool cannot stop fetched-job
renewal or expiration for healthy pools. This provider heartbeat is distinct from
the per-server metadata heartbeat intercepted below.

Keep the same cached provider instance and fetched-job tracking across all pools.
The component inventory still suppresses only the counter aggregator; hosting
ownership changes without replacing the selected stock heartbeat/expiration code.
Each shared component has one actual invocation at a time, owned background work,
retained resources through completion and its own fixed health dimension. Apply
ADR-0058's positive wait limit of at most five minutes plus at most one second of
cleanup waiting to each; neither owns a business worker slot. Expiration keeps
its one-millisecond internal interval and one-hour owner cadence. The fetched-job
heartbeat retains its stock one-second pacing; no expiration cadence is applied
to it. Both join under the same host deadline and outlive pool-local draining.
The exact process/queue/filter inventory remains pinned; every enabled acquisition
still crosses the confined OutboxDispatcher source.

The first supplied dispatcher builder synchronously captures that generation's
actual server context before it can admit supplied workers. An asynchronously
scheduled sentinel is insufficient: stopping can prevent its body ever running.
The capture itself supplies no tenant, request or completion authority.

Managed supplied-builder failure after capture requests draining and prevents
later starts while allowing Core to reach its actual child join. Startup failures,
including faults before capture and partial dispatcher construction, must fail
closed without pretending a ready-generation experiment proves their cleanup.
If the public seams cannot qualify this obligation, Step 4 remains blocked and the
decision is reopened; a timeout is never a substitute for the missing witness.

### Heartbeat faults and draining

Keep the actual stock provider heartbeat SQL and exact role guard. Capture
`BackgroundServerGoneException` as an immediate missing-server drain. Retry
transient storage/connect/timeout faults inside the same owned callback, with
cancelable one-second pacing and a monotonic deadline at most five minutes after
the last confirmed heartbeat or initial announcement. Five minutes is the pinned
stock server-timeout default; this does not promise a separate fresh five minutes
after every fault. Each native SQL attempt still has its existing finite budget.
A confirmed stored heartbeat resets the deadline. Exhaustion requests draining;
unexpected authority/configuration faults drain immediately with a closed reason.

Do not forward transient exceptions to Core or return synthetic success: its
independent failure timer could cross a threshold after the adapter checked its
own. The callback returns normally only after an actual stored heartbeat succeeds;
otherwise its final failure remains held through draining. Health becomes unhealthy
on the fault. Last confirmed heartbeat and fault state remain distinct; successful
interception or elapsed caller waiting cannot restore health. Acquisition and
disposal failures on the heartbeat path must not escape into uncontrolled restart.

Draining atomically closes new admission for the affected pool, requests stock
stop once and retains the actual stop/cancellation operations. Do not synchronously
block propagation to other pools on one slow or throwing cancellation callback.
The heartbeat's failure cannot return into Core's restart path until the captured
generation's normal-lifecycle child join has finished. The captured shutdown
token is only a synchronization signal: final process teardown can cancel it
early. Replacement additionally requires the actual outer join, all retained
LearnStack cleanup and live host/restart gates. Use asynchronous completion
signaling; do not join children from a synchronous cancellation callback.

Set Core's internal shutdown join to `Timeout.InfiniteTimeSpan`. This is an actual
completion join retained by the owner, not the caller's wait deadline and not an
SQL/socket exception. Core's finite internal timer must not dispose work after
the external host wait expires. The adapter must also prevent automatic restart;
the option alone is insufficient.

One actual stock generation exists per pool, including draining/retained work.
Before replacement, also join every LearnStack execution, renewal, callback and
cleanup admitted by that generation; a returned wrapper is not that proof. Do
not dispose its server, scopes, tokens or resources while actual work uses them.
A cleanup fault that leaves ownership unresolved retains the stopped/unhealthy
generation rather than granting a new one.

After confirmed cleanup, the owner may replace the affected pool with the same
finite manifest after a cancelable five-second backoff. Failed startup retries
use the same backoff and unhealthy state; they do not create overlapping owners
or allocate a new operation while the previous one remains live. Other pools and
the serialized control lanes retain service. No restart changes a job generation,
business attempt, reservation, external effect or tenant authority.

### Host shutdown, bounds and observation

Host stop closes restart and acquisition gates before requesting all pool stops.
It uses ADR-0047's one existing external shutdown budget across pools, bridge,
maintenance and cancellation/cleanup waits. It never resets the budget for a
replacement or disposes unresolved work when caller waiting ends. Actual late
joins stay observed on owned background execution and cannot authorize fresh
starts after stop. Owned worker threads are background threads; that fact alone
does not qualify Core's separate synchronous process-exit handlers.

#### Process exit and closed cancellation relay

Normal host waiting and final OS process termination are separate boundaries.
Core subscribes to `ProcessExit`/`DomainUnload` and synchronously cancels its token
sources before its last-chance wait. Infinite internal shutdown does not disable
that path. A blocking callback can therefore hold process exit after the external
host wait has returned; zero last-chance timeout alone is insufficient.

Register a closed nonblocking final-exit gate before creating stock servers;
normal host stop already closes restart gates before exit. Final teardown cannot
reopen them or turn a token signal into an actual-cleanup witness.
Set stock `LastChanceTimeout=TimeSpan.Zero` to avoid a second per-pool exit join.
Keep captured Core tokens inside the closed generation owner: its registrations
only update fixed in-memory state or signal asynchronous completion. Never expose
a Core token to a module/provider, link it directly to a source with arbitrary
callbacks, join/dispose work in that callback or invoke a blocking observer. Relay
cancellation to private execution sources through separately retained asynchronous
cancellation operations. The vendor diagnostic observer on these paths also only
records fixed memory state; sinks and supplied delegates do not run there.

The closed relay and pinned vendor callbacks require explicit process-exit
qualification. A dirty direct callback must reproduce the hang; the supported
relay must let the child exit while its private background callback remains held.
If this public configuration cannot qualify the boundary, refuse activation and
reopen the decision; do not silently rely on foreground/background thread labels.
Phase 11 still owns supervisor grace/hard-termination policy, not a hidden P4
cleanup guarantee.

On final process exit, Core can cancel its shutdown token before actual retained
work finishes. Such termination is not graceful cleanup, business completion or
external quiescence. Retained external reservations and uncertainty remain durable
for recovery; no process-exit signal releases them. This proposal claims a finite
normal host wait and qualified nonblocking exit callbacks, not successful cleanup
of work terminated by the OS or an absolute OS scheduling deadline.

Retained/draining generations and heartbeat faults make the registered, unmapped
`jobs` check unhealthy under a finite pool key and fixed state/reason. Keep actual
running work, retained work and confirmed storage heartbeat distinct. Log no
provider exception object, SQL, tenant, job, actor, parameter or trace value.
Phase 11 still owns readiness routing, alert thresholds and process supervision.

The physical-acquisition census counts one live generation per pool only after
these invariants are proved. Step 4 still enumerates nested/retained acquisitions,
enforces its qualified supported configuration and proves independent progress at
that boundary. This proposal chooses no guessed numeric floor or nonpooled rule.
The existing workers-plus-four, bridge-four and separate expiration bounds remain.

### Configuration replacement and additive seams

| Existing authority | Proposed change | Preserved |
|---|---|---|
| ADR-0047 H1 stock pools and Lifecycle and observation | Replace convenience-server/automatic restart policy with public stock composition and owner-controlled replacement after actual cleanup | Stock workers/schedulers/assemblies/SQL, H1 business authority, disjoint queues, attempt and capacity semantics |
| ADR-0058 child-resource retention and shared host join | Add synchronous generation capture, server-heartbeat containment, actual-generation joins and closed cancellation relay; explicitly qualify final process exit separately from graceful cleanup | Selective counter/history suppression, finite enabled SQL/socket budgets and one shared external shutdown deadline |
| ADR-0058 shared provider heartbeat/expiration | Host both selected components independently of pool generations, with single-flight owned invocations | The same stock code/cached tracking, expiration cadence, retained resources and no business worker/reservation |

The policy replacement and additive owner seams are new normative content. On
acceptance, dated Amendments in ADR-0047 and ADR-0058 disclose/link them while
preserving all earlier bytes and acceptance metadata. This is not an ADR-0041
fact correction or whole-record retirement. There is no role, grant, schema,
business request, generic owner, context setter or public recovery API addition.

## Context

Core's `BackgroundServerProcess` links all three child context tokens to a restart
CTS. `WaitForDispatchers` catches cancellation of the linked shutdown token;
`DisposeDispatchers` then disposes unfinished dispatchers. `BackgroundDispatcher`
disposes its execution/countdown objects without joining the actual thread.
Heartbeat requests this restart on missing server metadata and sustained storage
faults. The outer host can still be running, so another generation can start.

The initial exact-pin experiment holds an actual `IBackgroundProcess`, injects
missing-server heartbeat metadata and observes a second generation while the
first remains live. The old context's three token wait handles throw
`ObjectDisposedException`; infinite internal shutdown does not prevent this.
Fake storage isolates the stock Core lifecycle; it proves no PostgreSQL query,
provider role/deadline, actual worker or complete proposed owner.

A second experiment holds the heartbeat fault until a ready shutdown witness.
It retains one generation and accessible tokens until the actual operation is
released. That experiment alone does not prove startup: a queued sentinel can
be skipped after stop. Synchronous public builder experiments then force failure
before capture, after worker start and during managed supplied-builder
construction. Their owned
variants preserve token/resource lifetime; the raw builder-failure control
reproduces premature disposal. These qualify only the tested public mechanism,
not arbitrary partial-resource construction, fatal runtime failure or production
PostgreSQL/multiple-pool behavior.
Two child-process controls distinguish normal host waiting from final process
exit. A direct Core shutdown callback remains blocked after the finite host wait;
the parent terminates and joins that dirty child. With only closed asynchronous
signals on Core tokens, the positive child exits while an actual stock process
and a private cancellation callback remain held. A test-only observer confirms
Core reached its forced shutdown signal. This proves neither actual cleanup nor
production callback confinement; final teardown aborts unresolved background work.
The [Step 4 decision receipt](../roadmap/phase-02b-events-auth.md#p02b-4-step-4--server-generation-decision-2026-10-16)
owns reproducible commands, results and limits; experiments are not shipped
enforcement or acceptance.

**What changes this recommendation:** a qualified upstream lifecycle fix, loss
of the required public seams, or failure of startup/actual-join/pool-progress
qualification. Such failure reopens the choice; it does not permit live overlap,
an unrecorded fork or smaller ownership claims.

**Explicitly outside this decision:** business completion and terminal audit
remain Step 5; tenant capacity, external effects and recurrence remain Steps 6–8.
No restart supplies quiescence, erases retained attempts or frees unknown external
occupancy. Phase 03 operator recovery and Phase 11 supervision remain unchanged.

## Consequences

### Positive

- One actual generation per pool makes capacity/ownership claims testable.
- Stock vendor code and the guarded shared source remain in use.
- A pool-local fault waits for its old work without restarting healthy pools.

### Negative

- LearnStack owns a public process compositor, generation drain and retry policy.
- A pool can remain stopped indefinitely if actual work or cleanup never returns.
- Heartbeat interception and startup failures require pinned upgrade proofs.

### Neutral

- Host waiting stays finite; no forced termination or exactly-once effect is added.
- Vendor operational success and authoritative business success remain separate.

## Implementation Notes

P02b-4 Step 4 owns the public compositor, synchronous capture, classified server
heartbeat window, per-pool generation owner, independent shared component owner,
closed cancellation relay, actual shutdown retention and process-exit proofs.
Step 9 owns final observation/censuses and full regression. Existing Accepted
records, Standards 05/10/20/21, Events and Outbox architecture and current roadmap
carriers receive the named update only after maintainer acceptance and delivery
where appropriate. No dependent runtime is implemented by this Proposed record.

## Architecture Tests

Extend existing canonical obligations without inventing a parallel rule spelling:

- `Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role`: exact public stock
  process/component/filter inventory, synchronous generation capture and closed
  heartbeat path, acquisition/disposal fault containment, finite enabled I/O and
  unchanged guarded source. Unsupported/missing owners refuse before activation.
- `BackgroundJob_Worker_Pools_Reserve_Independent_Capacity`: actual-generation
  nonoverlap, no early token/server/resource disposal, retained actual stop/callback
  ownership, independent healthy pools, supported floor and one host deadline.
- `BackgroundJob_Storage_And_Credentials_Are_Confined`: enumerate the concrete
  compositor/connection/owner capability callers; no forwarding or generic bypass.

Mandatory controls include stock restart as a dirty control; missing-server and
persistent/transient heartbeat faults; successful heartbeat reset; before-capture,
after-capture and partial-start failures; held SQL and cancellation/disposal;
control-pool draining while shared provider heartbeat/healthy pools continue;
direct-callback versus closed-relay process exit; host stop during draining/backoff;
late completion; exact one replacement only after both stock and LearnStack
cleanup; and truthful health while retained.
Use real pinned workers/PostgreSQL and actual role/grants in production proofs.
Restoring stock restart, skipping capture, returning a heartbeat fault early or
starting replacement before cleanup must fail the production predicates. The
isolated fake-storage experiment does not satisfy those obligations.

## References

- [ADR-0047](0047-transactional-background-job-runtime.md).
- [ADR-0058](0058-bounded-stock-hangfire-maintenance.md).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs).
- [Infrastructure Standards](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire).
- [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Pinned Core generation owner](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundServerProcess.cs).
- [Pinned Core dispatcher](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Processing/BackgroundDispatcher.cs).
- [Pinned Core heartbeat](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/ServerHeartbeatProcess.cs).
- [Pinned public server composition](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundProcessingServer.cs).
- [Pinned provider connection](https://github.com/hangfire-postgres/Hangfire.PostgreSql/blob/e0a4001c6627695d61a0eea19d4403882e4cea34/src/Hangfire.PostgreSql/PostgreSqlConnection.cs).
