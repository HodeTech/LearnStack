# ADR-0059: Owned Hangfire Server Generations

## Status

Proposed — maintainer approval pending.

**Date:** 2026-10-16 **Deciders:** @cemil

This proposal adds H1 server-lifecycle/ownership policy and a bounded
availability qualification of the two ADR-0047 clauses named below;
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
- Core also repeats its outer execution after normal return or catchable startup
  failure. Containing only heartbeat cannot make LearnStack the sole restart owner.
- H1 should retain stock workers, schedulers, SQL, assemblies and public extension
  seams. It must not silently acquire a vendor fork or private-reflection owner.
- Missing vendor server metadata is not proof that business or external work
  stopped. An operational restart cannot release a reservation or reset attempts.

## Considered Options

1. **LearnStack owns replacement after actual generation cleanup** (recommended).
   Public composition retains stock processes; a closed heartbeat adapter requests
   draining rather than allowing Core to replace live children. Other pools remain
   serviceable when their dependencies are healthy. A replacement waits for actual
   stock and LearnStack cleanup, accepting the whole-pool availability cost below.
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

7. **Drain without in-process replacement** (viable, not selected). It has a
   simpler closed lifecycle, but a recovered/clean pool remains stopped until a
   host restart. The unmapped health check currently cannot trigger one; Phase 11
   owns readiness/supervision. Replacement after actual cleanup restores service
   without making that future supervisor a present recovery dependency.
8. **At most two owned generations with shared capacity** (viable, not selected).
   It could use a spare slot after storage recovery while old work remains held.
   Both generations would need one shared dispatch budget, retained-work bound,
   reservation accounting and host deadline, safe old resources and a hard cap
   of two. This is different from unbounded stock restart; that dirty control
   does not disprove it. P4 selects simpler single-generation ownership and its
   availability cost rather than an unqualified cross-generation compositor.
   Selecting overlap requires its own qualification and decision before use.

## Decision

**Proposed decision:** LearnStack owns each pool's Hangfire server generation and
replaces it only after actual stock dispatchers, admitted work, callbacks and
cleanup from that generation finish. A public compositor uses pinned stock Core
workers/schedulers and the existing guarded provider. A closed per-pool storage seam
contains announcement,
metadata-heartbeat and removal paths and fences Core's outer repeat loop. Pool-local
drain closes fetch/admission without canceling running work; only the owner may
replace the generation after actual cleanup. The host's external shutdown
wait remains finite and shared. During generation-control drain, this decision
qualifies ADR-0047's unrelated-tenant progress promises: fresh starts in that pool
can stay blocked below four retained overruns, even with a spare slot after
storage recovers. Approval and real qualification precede activation.

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
renewal or expiration for healthy pools. This storage-wide fetched-job
heartbeat (`PostgreSqlHeartbeatProcess`) is distinct from per-server metadata
heartbeat (`JobStorageConnection.Heartbeat`) intercepted below.

Keep the same cached provider instance and fetched-job tracking across all pools.
The component inventory suppresses only `CountersAggregator`; removal of the
`StatisticsHistoryAttribute` filter is separate. Hosting ownership changes without
replacing the selected stock heartbeat/expiration code.
Each shared component has one actual invocation at a time, owned background work,
retained resources through completion and its own fixed health dimension. Apply
ADR-0058's positive wait limit of at most five minutes plus at most one second of
cleanup waiting to each; neither owns a business worker slot. For fetched-job
heartbeat this bounds waiting on an entire pass across tracked jobs, each with
finite native SQL, rather than its one-second pacing alone. A slow retained pass
makes health unhealthy; the owner does not overlap it or promise recovery time.
Expiration keeps
its one-millisecond internal interval and one-hour owner cadence. The fetched-job
heartbeat retains its stock one-second pacing; no expiration cadence is applied
to it. Both join under the same host deadline and outlive pool-local draining.
The exact process/queue/filter inventory remains pinned; every enabled acquisition
still crosses the confined OutboxDispatcher source.

The first supplied dispatcher builder synchronously captures that generation's
actual server context before it can admit supplied workers. An asynchronously
scheduled sentinel is insufficient: stopping can prevent its body ever running.
The capture itself supplies no tenant, request or completion authority.

All catchable supplied-builder failures after capture request draining and prevent
later starts while allowing Core to reach its actual child join. Match the pinned
Core `IsCatchableExceptionType` predicate: exclude exact `OutOfMemoryException`
and `StackOverflowException` types; do not narrow it to `InvalidOperationException`.
The builder must own any resources it acquires before throwing. A placeholder
cannot prove those resources completed. Startup failures,
including faults before capture and partial dispatcher construction, must fail
closed without pretending a ready-generation experiment proves their cleanup.
If the public seams cannot qualify this obligation, Step 4 remains blocked and the
decision is reopened; a timeout is never a substitute for the missing witness.

### Concrete storage seam and sole replacement owner

The proposed internal `PoolHangfireStorage : JobStorage` delegates to one shared,
guarded `PostgreSqlStorage`; it never creates another provider or physical source.
Its `GetConnection()` returns a closed `PoolHangfireConnection` derived from
`JobStorageConnection`, with lazy provider acquisition. This concrete base matters:
the stock schedulers cast to `JobStorageConnection` for advertised batched-set
features. A plain `IStorageConnection` wrapper would lose that capability.

The connection acquires the real provider connection, invokes the original
`AnnounceServer`, `Heartbeat` or `RemoveServer`, and finishes that connection's
actual disposal inside the corresponding lifecycle boundary. Outer `Dispose`
must not repeat that disposal. This contains acquisition/disposal faults before
Core's catches, without caller inspection, replacement SQL or private reflection.
Non-lifecycle methods and their disposal preserve ordinary provider behavior.
Return actual provider transactions and fetched-job objects unchanged; do not
replace their queue, lock, heartbeat tracking or feature identities.

Forward the complete pinned storage/connection surface, including `HasFeature`,
`LinearizableReads`, read-only connections, monitoring, state handlers, required
and storage-wide processes, options diagnostics and every extended connection
method. Copy and freeze the nonvirtual `JobExpirationTimeout` consistently with
the shared provider. Bootstrap materializes the exact component inventory once,
selects ADR-0058's two actual components and freezes those instances; forwarding
`GetComponents()` returns that selection rather than re-running the provider's
component factory. Provider-wide/required process inventories are also pinned.
No server automatically hosts storage components. Pass storage explicitly; no
`JobStorage.Current` get/set, global `UseStorage` or alternative hosted-server
registration bypasses this owner. Existing serializer configuration stays separate.
The obsolete `GetComponents`/
`IServerComponent` seam has narrow CS0618/CS0672 handling (or the matching
obsolete override annotation) and an upgrade gate;
it is not silently substituted with a different provider feature contract.

Behavioral parity compares the real guarded provider with the pool decorator for
all non-lifecycle operations, features and object identities, with only the named
ADR-0058 component selection and lifecycle interception as intentional differences.
A public-surface census fails on any missing/new/changed member or capability.
These real-provider controls remain mandatory; fake storage is not parity proof.

Allocate the owner before constructing Core; publish its actual server handle
once. A lifecycle fault before publication stays owned until `SendStop` completes;
it cannot escape into Core's retry loop or require a not-yet-run capture sentinel.
Only one announcement may reach the provider for that owner. A second attempt is
refused before SQL and requests the same stop. For an announcement/acquisition/
disposal fault, complete the stop fence before returning a closed failure to Core.
Every removal path, including normal completion or failed removal/disposal, also
completes that stop fence before Core's execution can return or throw. Thus Core's
outer stopping token is canceled, not merely its restart-linked child tokens.
`RestartDelay=Infinite` is not a substitute: Core normalizes nonpositive delays.

Supplied-builder guards cover catchable construction faults before they leave
`StartDispatchers`. Required-process construction and partial public-constructor
failures still require actual cleanup qualification; a removal fence alone cannot
undo an earlier premature child disposal. If those public paths cannot be safely
qualified, startup refuses and this choice reopens. No second owner or server is
created while the previous constructor, stop or cleanup remains unresolved.

### Heartbeat faults and draining

Keep the actual per-server metadata heartbeat SQL and exact role guard. Capture
`BackgroundServerGoneException` as an immediate missing-server drain. Retry
transient storage/connect/timeout faults inside the same owned callback, with
cancelable one-second pacing and a four-minute monotonic retry cutoff after the
last confirmed metadata heartbeat or initial announcement. This leaves margin
under the fixed five-minute server expiry, including the at-most-five-second SQL
attempt. Do not start an attempt at/past the cutoff; a late attempt may consume
only its existing finite I/O budget. This does not promise a fresh retry window
after every fault or that another watchdog can never remove metadata.
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

Set `BackgroundProcessingServerOptions.ShutdownTimeout` to
`Timeout.InfiniteTimeSpan` (the cancellation timer, not a join API). The owner
retains the actual completion join separately from the caller's wait deadline;
this setting grants no SQL/socket exception. Core's finite internal timer must not
dispose work after
the external host wait expires. The adapter must also prevent automatic restart;
the option alone is insufficient.

Set `StopTimeout=Timeout.InfiniteTimeSpan` as well. Pool-local drain cancels
Core's `StoppingToken`, closing fetch and admission, but must not schedule its
`StoppedToken` or cancel a running job's private execution source. In pinned Core,
that stopped token feeds native job cancellation and requeue. Neither stopping
nor stopped Core tokens are forwarded/linked to module cancellation. Host stop,
execution deadline and lease-loss gates separately request owned asynchronous
cancellation of private sources under ADR-0047; local drain supplies none of those
business signals. Their actual operations/callbacks stay retained. A fetched or
initializing wakeup not yet admitted to business execution may stop/requeue; local
drain spends no business attempt merely for that pre-execution interruption.

One actual stock generation exists per pool, including draining/retained work.
Before replacement, also join every LearnStack execution, renewal, callback and
cleanup admitted by that generation; a returned wrapper is not that proof. Do
not dispose its server, scopes, tokens or resources while actual work uses them.
A cleanup fault that leaves ownership unresolved retains the stopped/unhealthy
generation rather than granting a new one.

After confirmed cleanup, the owner may replace the affected pool with the same
finite manifest after a cancelable five-second backoff. Failed startup retries
use the same backoff and unhealthy state; they do not create overlapping owners
or allocate a new operation while the previous one remains live. Other healthy
pools and independent LearnStack control lanes can retain service, subject to
the timer/common-dependency limits below. No restart changes a job generation,
business attempt, reservation, external effect or tenant authority.

### Availability, timers and fixed server configuration

Single-generation ownership deliberately stops the entire affected pool, including
an unused slot, until all old work/cleanup finishes. A database/connectivity outage
past the retry cutoff can drain every pool. Storage recovery alone cannot reopen
a pool holding a long external operation. The default one-hour / maximum 24-hour
external wait bounds caller waiting, not actual cleanup; the outage can last
minutes, hours or indefinitely. Disjoint pools preserve capacity isolation when
dependencies are healthy, not availability through a common database failure.

Control-pool drain stops stock delayed promotion and recurring ticks until its
replacement. Already enqueued immediate work on a healthy target pool can still
run. The separate fair LearnStack bridge/occurrence/recipient loop is independent
of that generation: after database recovery it repairs due handoffs with the same
wake generation and materializes due logical recurrence slots without requiring
a vendor tick, preserving CoalesceLatest. Repair grace/poll/page bounds are not
an unconditional delivery-latency SLA under backlog or absent capacity. Step 4
must prove a delayed due job is repaired while the control generation stays held;
Step 8 must prove the missing-tick recurrence case, each with duplicates spending
no extra business attempt or logical occurrence.

Generation policy values are fixed internal constants, not new configuration
inputs or module overrides. Startup validates the exact resulting options and
manifest before starting a server; Step 4 adds the checks next to the existing
`BackgroundJobRuntimeOptions.Validate()`/composition gates. An upgrade must
requalify the matrix and complete acquisition/callback inventory.

| Setting | Selected value/source |
|---|---|
| HeartbeatInterval | 30 seconds |
| ServerTimeout / ServerCheckInterval | 5 minutes / 5 minutes |
| CancellationCheckInterval | 5 seconds |
| StopTimeout / ShutdownTimeout | Infinite / Infinite cancellation timers; owner retains actual joins |
| LastChanceTimeout | Zero; no extra per-server final-exit join |
| Owner retry cutoff / retry pacing / replacement backoff | 4 minutes after confirmed heartbeat, 1 second, 5 seconds |
| Core RestartDelay | Pinned 15 seconds; cannot grant server replacement because lifecycle exits first complete the stop fence |
| Core child RetryDelay | Preserve the pinned constructor default delegate, held by the closed compositor; no module/configuration override |
| Core server properties `Queues` / `WorkerCount` | Exact frozen pool `string[]` and positive `int` count from the same finite manifest used to construct workers |

`Queues`/`WorkerCount` are dictionary properties consumed by Core, not fields on
`BackgroundProcessingServerOptions`. Missing/default-zero or divergent metadata
refuses activation. The four-minute cutoff leaves a one-minute expiry margin;
clock/storage scheduling can still cause a missing-server result, which drains
safely. No timeout value is execution/quiescence authority.

### Host shutdown, bounds and observation

Host stop closes restart and acquisition gates before requesting all pool stops
and separately signals the retained private-source cancellation relays.
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
Step 4 must close/qualify the current `HangfireBoundedLogProvider` observer:
its existing supplied `Action` bounds data but can still throw or block. Core logs
before the heartbeat try; storage interception cannot contain that observer.
An injected throwing/blocking observer must refuse activation, not leak into Core.
Catchable owner/dispatcher callbacks use the same closed failure/retention policy.

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
`jobs` check unhealthy under a finite registry pool label and fixed state/reason.
That label is an observation dimension, not a readiness mapping. Keep actual
running work, retained work and confirmed storage heartbeat distinct. Log no
provider exception object, SQL, tenant, job, actor, parameter or trace value.
Phase 11 still owns readiness routing, alert thresholds and process supervision.

The physical-acquisition census counts one live generation per pool only after
these invariants are proved. Step 4 still enumerates nested/retained acquisitions,
enforces its qualified supported configuration and proves independent progress at
that boundary. This proposal chooses no guessed numeric floor or nonpooled rule.
The existing workers-plus-four, bridge-four and separate expiration bounds remain.

### Additive integration and bounded availability supersession

ADR-0047 says, “Use disjoint transactional/external/control capability queues with
separate stock Hangfire server pools and worker counts.” Its lifecycle section
also requires retained actual resources and one shared finite shutdown budget.
Neither it nor ADR-0058 selects `BackgroundJobServer`, a convenience constructor
or an automatic-restart policy. This ADR adds the concrete lifecycle integration
needed to satisfy those obligations; it does not replace an earlier selected
constructor/restart decision.

| Existing authority | New policy / vehicle | Preserved |
|---|---|---|
| ADR-0047 stock pools and lifecycle | Public compositor, one-shot storage lifecycle fence, synchronous context capture, explicit local-drain/host-stop distinction and owner-controlled replacement | Stock code/SQL, disjoint queues, actual-work/attempt/capacity ownership and one host wait budget |
| ADR-0047 affected-tenant progress in the two quoted clauses | Bounded supersession only during generation-control draining, including below four retained overruns | Healthy other pools, ordinary tenant-capacity accounting, hard retention bounds and independent orchestration |
| ADR-0058 component selection and resource retention | Independent shared fetched-job heartbeat hosting, frozen component instances, closed cancellation relay and qualified final process exit | Existing API-instance expiration owner/cadence, selective suppression and finite enabled I/O |

The constructor/restart seam is additive; the whole-pool availability exception
is a bounded normative change. This ADR supersedes only the affected-pool progress
reading of two actual ADR-0047 clauses during generation-control draining:

- [Tenant capacity and independent pools](0047-transactional-background-job-runtime.md#tenant-capacity-and-independent-pools):
  “A lost worker may reduce availability by quarantining capacity; unrelated
  tenants and transactional pools remain serviceable.”
- [Lifecycle and observation](0047-transactional-background-job-runtime.md#lifecycle-and-observation):
  “Retain/observe/renew all admitted work through real cleanup; affected job
  identities remain blocked while other pools/tenants progress.”

**Qualified rule:** while a pool generation is draining or its old work/cleanup
remains unresolved, unrelated tenants assigned to that same pool can also be
blocked until actual cleanup completes, including after storage recovery, with
an unused slot and below four retained overruns. Other healthy pools retain their
independence subject to shared dependencies. The existing four-overrun threshold
still stops ordinary retention in an otherwise serviceable generation; it is not
the only pool stop trigger. Worker/retention hard bounds, fair independent
orchestration, reservation ownership and positive quiescence remain unchanged.
No wider tenant-progress or whole-record replacement is selected.

On acceptance, a dated ADR-0047 Amendment discloses both the additive integration
and this named bounded availability supersession, preserving its prior Decision
and acceptance metadata. ADR-0058 receives only an additive ownership Amendment.
There is no previously selected convenience/restart sentence to supersede or
correct, and no ADR-0041 fact correction or whole-record retirement. This new
availability qualification requires maintainer approval rather than being treated
as an implicit condition of the earlier promise. No role, grant, schema, business
request, generic owner, context setter or public recovery API is added.

## Context

Core's `BackgroundServerProcess` links all three child context tokens to a restart
CTS. `WaitForDispatchers` catches cancellation of the linked shutdown token;
`DisposeDispatchers` then disposes unfinished dispatchers. `BackgroundDispatcher`
disposes its execution/countdown objects without joining the actual thread.
Heartbeat requests this restart on missing server metadata and sustained storage
faults. Independently, `BackgroundExecution.Run` repeats after successful return
and retries catchable exceptions until its outer stopping token is canceled.
Announcement happens before child capture and before the generation's finally;
removal follows normal/failed child execution. Containing only heartbeat is
therefore incomplete. The outer host can still run while another generation starts.

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
construction. Their owned variants preserve token/resource lifetime; the raw
builder-failure control
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
- A shared outage can drain every pool; one long/retained operation blocks all
  fresh starts in its pool, including a spare slot, after storage recovers.
- A pool can remain stopped indefinitely if actual work or cleanup never returns.
- Control-pool drain delays stock timers; independent authoritative repair remains
  required and does not supply an unconditional dispatch-latency guarantee.
- Heartbeat interception and startup failures require pinned upgrade proofs.

### Neutral

- Host waiting stays finite; no forced termination or exactly-once effect is added.
- Vendor operational success and authoritative business success remain separate.

## Implementation Notes

P02b-4 Step 4 owns the public compositor, synchronous capture, classified server
heartbeat window, per-pool generation owner, independent shared component owner,
closed cancellation relay, exact fixed options, actual shutdown retention and
process-exit proofs. Step 8 owns missing-tick recurrence recovery qualification.
Step 9 owns final observation/censuses and full regression. Existing Accepted
records, Standards 05/10/20/21, Events and Outbox architecture and current roadmap
carriers receive the named update only after maintainer acceptance and delivery
where appropriate. No dependent runtime is implemented by this Proposed record.

## Architecture Tests

Extend existing canonical obligations without inventing a parallel rule spelling:

- `Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role`: exact public stock
  process/component/filter inventory, synchronous generation capture and closed
  lifecycle storage/connection seam, full public-surface/feature parity, exact
  fixed options/metadata, acquisition/disposal fault containment and unchanged
  guarded source. Enumerate production construction/registration calls and
  reject `BackgroundJobServer`, `AddHangfireServer` and `UseHangfireServer` bypasses
  outside the closed compositor. Public member, obsolete-seam or vendor upgrade
  drift refuses before activation; no source-string-only bypass census.
- `BackgroundJob_Worker_Pools_Reserve_Independent_Capacity`: actual-generation
  nonoverlap, no early token/server/resource disposal, retained actual stop/callback
  ownership, sole outer-loop replacement owner, local drain without business
  cancellation, independent healthy pools/repair, supported floor and one host
  deadline. Two-generation budgets are not silently multiplied.
- `BackgroundJob_Storage_And_Credentials_Are_Confined`: enumerate the concrete
  compositor/connection/owner capability callers; no forwarding or generic bypass.

Mandatory controls include stock restart as a dirty control; missing-server and
persistent/transient metadata-heartbeat faults; successful heartbeat reset;
announcement/acquisition/disposal faults before capture, normal removal/return,
removal/disposal faults and contained startup exceptions; before-capture,
after-capture and partial-start failures; held SQL and cancellation/disposal;
control-pool draining while shared fetched-job heartbeat/healthy pools continue
and the independent bridge repairs a due delayed handoff; common database outage
then recovery with one long retained operation and a spare slot still closed;
infinite-versus-finite StopTimeout, separate host cancellation, fixed options and
nonempty queues/positive worker-count metadata;
direct-callback versus closed-relay process exit; host stop during draining/backoff;
late completion; exact one replacement only after both stock and LearnStack
cleanup; and truthful health while retained.
Use real pinned workers/PostgreSQL and actual role/grants in production proofs.
Restoring stock restart, skipping capture, returning a heartbeat fault early or
starting replacement before cleanup must fail the production predicates. The
isolated fake-storage experiments do not satisfy those production obligations.

## References

- [ADR-0047](0047-transactional-background-job-runtime.md).
- [ADR-0058](0058-bounded-stock-hangfire-maintenance.md).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs).
- [Infrastructure Standards](../standards/20-infrastructure-stack.md#background-jobs-and-hangfire).
- [Database Standards](../standards/05-database.md).
- [Observability Standards](../standards/10-observability.md).
- [Events and Outbox architecture](../architecture/15-event-and-outbox.md).
- [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Pinned Core generation owner](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundServerProcess.cs).
- [Pinned Core dispatcher](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Processing/BackgroundDispatcher.cs).
- [Pinned Core heartbeat](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/ServerHeartbeatProcess.cs).
- [Pinned public server composition](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundProcessingServer.cs).
- [Pinned provider connection](https://github.com/hangfire-postgres/Hangfire.PostgreSql/blob/e0a4001c6627695d61a0eea19d4403882e4cea34/src/Hangfire.PostgreSql/PostgreSqlConnection.cs).
- [Pinned Core outer execution loop](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Processing/BackgroundExecution.cs).
- [Pinned Core server options](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Server/BackgroundProcessingServerOptions.cs).
- [Pinned storage surface](https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/JobStorage.cs).
