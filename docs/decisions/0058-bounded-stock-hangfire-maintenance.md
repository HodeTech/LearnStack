# ADR-0058: Bounded Stock Hangfire Maintenance

## Status

Proposed — maintainer approval pending.

**Date:** 2026-10-16 **Deciders:** @cemil

**Proposed configuration replacement:** suppress the optional stock counter
aggregator and statistics-history filter in ADR-0047's H1 integration. The
previous proposal to exempt an enabled command from the command/socket budget is
withdrawn. **Additive seams:** exact component selection, shared expiration
ownership, maintenance observation and its test obligations. On acceptance,
ADR-0047 receives a dated amendment disclosing both the configuration change and
these seams. Its command/socket bounds, business authority, roles, grants,
schema, attempts and existing execution/bridge ownership bounds remain unchanged.
No dependent runtime is implemented by this proposal.

## Decision Drivers

- H1 preserves pinned vendor assemblies and SQL, with no dashboard or dependency
  on vendor statistics for business authority.
- The pinned aggregator overrides the driver's command timeout with zero. Its
  missing I/O bound need not become an accepted exception if it is not enabled.
- Stock expiration already maintains counters. Optional historical statistics
  have a measurable storage cost and are not a P02b-4 requirement.
- Finite SQL/I/O calls do not bound an entire maintenance pass or repeated lock
  acquisition. Actual work and resources remain owned after caller waiting ends.
- Counter totals, live-row backlog, allocated disk and authoritative business
  outcomes are different quantities; qualification must not conflate them.

## Considered Options

1. **Suppress aggregation/history; retain owned stock expiration** (proposed).
   Use public hooks, preserve finite driver deadlines and retain stock cleanup.
   Accept absent vendor timelines and the stock compactor's approximate totals.
2. **Retain an owned stock aggregator with an I/O exception** (not proposed).
   Single-flight retention can bound count/wait/shutdown, but one command can
   retain a connection/thread while its read/write I/O remains unresolved. This
   adds an exception and owner without a required product statistics benefit.
3. **Qualified upstream timeout fix** (viable later). Requalify a concrete release
   before enabling it. No replacement has been qualified by this packet.
4. **First-party aggregation SQL** (not proposed). Component substitution is
   possible, but adds ownership of vendor counter transactions and upgrades.
5. **Reflection or tracing interception** (rejected). No supported per-command
   handle is exposed through the sealed connection/factory contract; tracing is
   conditional on listeners. Neither is the selected integration seam.
6. **Keepalive as the command deadline** (rejected). It does not establish a hard
   deadline against a proxy that acknowledges TCP while withholding responses.
7. **Disable only aggregation, retain history** (not proposed). Expiration still
   cleans up, contrary to the earlier draft's rejection premise. It retains raw
   daily/hourly history until expiry and increases backlog without a dashboard.

## Decision

Subject to maintainer acceptance, remove only the exact pinned
`CountersAggregator` from the public `PostgreSqlStorage.GetComponents()` result
and remove only `StatisticsHistoryAttribute` from the global filter collection,
through its public exact-type removal API, before any worker starts. Keep stock
`ExpirationManager`, sliding-invisibility heartbeat, state-counter handlers,
wrapper admission, retry configuration and all other filters. Do not replace
aggregation SQL, modify the vendor assemblies or introduce a socket exception.

### Component selection and ownership

The unmodified provider must expose exactly `ExpirationManager`,
`CountersAggregator` and `PostgreSqlHeartbeatProcess` under the selected options.
Match pinned full type/assembly identities, not an unqualified suffix. Unexpected,
missing or duplicate inventory refuses startup. All pools share the same
provider heartbeat/fetched-job tracking; suppression must not remove heartbeat.
The startup filter inventory verifies history removal and retention of the
required admission/retry filters instead of clearing all filters.

One API-instance owner runs the actual stock expiration component, shared across
pools. No second pass starts while that operation or its cleanup remains live.
Run synchronous work on an owned background thread; do not occupy an unbounded
number of thread-pool workers. It adds no business worker slot or reservation and
is accounted separately from ADR-0047's workers-plus-four and bridge-four limits.

Set the provider's positive `JobExpirationCheckInterval` to one millisecond and
verify that exact value at startup. The owner supplies the normal one-hour
cadence after actual completion, retaining the vendor's default cadence without
mistaking its internal sleep for blocked work. Held, not-due and failed returns
use a cancelable shared cadence/backoff; Core's immediate reinvocation must not
spin threads or allocate replacement tasks.

A maintenance invocation has a positive finite wait budget, at most five minutes
and five minutes by default, plus at most one second of cleanup waiting. Request
cooperative cancellation when that budget expires; it does not enter synchronous
vendor SQL or interrupt its distributed-lock retry loop. Retain the actual task,
cancellation source and checked-out resources until real completion. A legitimate
large expiry backlog can overrun this wait; overrun is not proof of SQL failure.

Retained maintenance makes the registered, unmapped `jobs` check unhealthy under
ADR-0047's retained-cleanup rule. Expose a separate fixed maintenance dimension
and bounded progress/outcome code, so it is distinguishable from business job
failure. Phase 11 owns readiness routing and alert policy; no readiness endpoint
is added here. No provider message factory, SQL or exception text is logged.

Host stop cancels and joins through the existing one shared shutdown budget.
Do not reset that budget per pool/pass or dispose resources still used by actual
work. Observe late completion, forbid fresh acquisitions after stop, and close a
retained connector on its eventual return. A background thread must not keep the
process alive after the finite host join. All other stock wait paths retain the
same ownership obligation; this owner does not qualify them by implication.

### SQL, session and pool boundaries

The shared OutboxDispatcher pool retains reset-safe statement/lock deadlines of
at most five seconds, finite driver command/connect/cancellation bounds and the
existing physical role guard. Statement deadlines apply per statement, lock
deadlines per lock acquisition; neither limits a whole maintenance pass.
No enabled provider command may override the finite driver budget with zero.

Add a reset-safe `idle_in_transaction_session_timeout=5000` on this confined
source for idle/aborted backend-session cleanup. Verify warm reset, successful
outbox/delivery transactions and a real aborted-idle control. Do not add a shared
`transaction_timeout`: its interaction with aborted-state timer selection and
existing transaction budgets requires separate qualification. This session
cleanup is not a client-socket termination guarantee and is not needed to release
locks already released by transaction abort.

The previous minimum-two/nonpooled startup rule is withdrawn with its retained
counter design. Two connections only left one checkout after one known retained
operation; it never proved the full topology could progress. Npgsql's default
maximum of 100 is a default, not a throughput or independence proof.

Step 4 must enumerate the final topology's simultaneous physical-acquisition
lanes, including workers, scheduler/core processes, bridge operations, confined
LearnStack schedulers, maintenance, nested and retained acquisitions. Record the
resulting supported configuration/floor, enforce it before workers start, and
prove other pools progress at that boundary under contention. This is the
existing ADR-0047 runtime acceptance obligation, not a guessed sizing formula.
Phase 11 owns measured production capacity and PgBouncer qualification, including
startup-setting propagation and applicable query/idle-transaction deadlines.

### Statistics and growth contract

Without history production, ordinary Processing-to-Succeeded emits one raw
counter row; a state transition emits at most two when old/new state handlers
both write. Duplicate wakeups can each succeed operationally, so no finite row
ratio per authoritative business completion is promised.

Stock expiration deletes expired counter/history rows and compacts nonexpiring
`stats:succeeded` and `stats:deleted`. Monitoring reads raw plus aggregated
totals. Its compactor commits deletion before replacement insertion, can lose a
subtotal on interruption, and drops nonpositive raw sums. These are approximate
vendor statistics; no accounting-equivalence claim or business metric uses them.
New vendor historical timelines are unavailable; old history expires normally.

Successful cleanup reduces live rows, not necessarily allocated relation size.
Backlog can grow during cleanup failure, timeouts or sustained arrivals. VACUUM,
retained snapshots and high-water allocation affect disk usage. Neither this
choice nor aggregation bounds the whole vendor store: failed vendor wakeups can
remain nonexpiring. Phase 11 owns operational retention/space policy; P4 retains
observable cleanup/progress and no claim of bounded disk or backlog recovery rate.

## Context

The original exact-pin experiment proved that `CountersAggregator` explicitly
passes `commandTimeout: 0`. Npgsql selects that value before the connection-string
`CommandTimeout` default and uses infinite read/write timeouts. An ACKing
response-withholding proxy leaves the actual task pending after eight seconds
although server SQL/lock deadlines are five seconds; closing the proxy settles
it. Ordinary packet loss can instead reach operating-system failure detection.
Keepalive is defense in depth, not a proof for the ACKing fault model.

The maintainer's review correctly identifies stock expiration compaction omitted
from the first comparison. Source inspection finds no other enabled explicit
zero-timeout command after aggregation suppression; runtime qualification remains
mandatory. Stock expiration still has longer waits: its nominal five-minute
lock-acquisition loop is not token-cancelable and can repeat across tables before
observing cancellation. Delayed/recurring schedulers also have longer lock waits.
Finite per-command I/O does not make those whole invocations five-second work.

A PostgreSQL 18.4 experiment also narrows the reported server risk: an aborted
transaction releases its locks although its backend can remain idle-aborted.
An independent update succeeds before the client reads the timeout response.
Idle-in-transaction timeout then terminates the remaining backend. Two 350 ms
statements succeed with a 500 ms statement budget; that budget is per statement.
The commands, measurements and their limitations belong to the
[Step 2 decision receipt](../roadmap/phase-02b-events-auth.md#step-2-maintenance-reassessment).

**What changes this decision:** a required vendor dashboard/history contract or
qualified finite-timeout upstream aggregator triggers renewed comparison. Loss
of the selected public hooks or bounded/progress qualification failure refuses
startup; it does not silently enable the removed component.

**Explicitly outside this decision:** no new role, grant, schema, business request,
public recovery API or production topology. The source/notice/replacement kit
and the stock H1 business-authority contract are preserved.

## Consequences

### Positive

- Remove the demonstrated infinite-command path without exempting it from bounds.
- Preserve stock cleanup, binaries and LGPL source/replacement packaging.
- Avoid storing unused history and distinguish business outcomes from statistics.

### Negative

- Vendor timelines disappear and raw state totals retain stock compaction losses.
- A shared expiration owner and its upgrade/lifecycle proofs remain necessary.
- Long maintenance/lock loops can retain one operation after finite caller wait.
- Successful cleanup does not guarantee disk shrinkage or backlog recovery rate.

### Neutral

- At-least-once wakeups, business atomicity, attempts and tenant reservations remain
  ADR-0047's contract. Per-pool and bridge ownership bounds are unchanged.

## Implementation Notes

On acceptance, append a dated ADR-0047 amendment named **maintenance configuration
replacement and additive ownership seams**. Preserve its body and acceptance
metadata; name both configuration suppression and the new owner/health/proofs.
This is not an ADR-0041 fact correction. Update Standards 05/20/21, the exact
[Events and Outbox architecture](../architecture/15-event-and-outbox.md), decisions
index and P02b-4 Step 2/4 records. Standards 21's two named `Asserts` below receive
this exact meaning at acceptance, remaining Registered until real enforcement.

Step 2 owns source-deadline/reset and schema qualification after acceptance.
Step 4 owns component/filter inventory, expiration ownership, interval validation,
final pool-acquisition census/configuration refusal and actual worker progress.
Step 9 owns full observation/enforcement and regression. Independent schema/ACL
validation continues before approval, but no full Step 2 closeout or runtime
maintenance claim is made by this Proposed record.

## Architecture Tests

Extend the existing canonical obligations, with no second rule name:

- `Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role`: exact stock component
  and filter inventory, selective suppression, unchanged pinned assemblies,
  positive one-millisecond internal expiration interval, finite enabled commands,
  startup/idle deadlines and warm reset. Actual stock cleanup under the granted
  role must preserve required state/queue behavior; dirty inventory/filter/deadline
  controls must refuse before workers start.
- `BackgroundJob_Worker_Pools_Reserve_Independent_Capacity`: one actual shared
  expiration invocation across actual pools, no duplicate starts or spin, bounded
  wait/join, truthful unhealthy retained state, final physical-acquisition census
  and independent progress at the enforced supported pool boundary. Real prolonged
  lock contention, shutdown/disposal and late cleanup retain resources safely.

The qualification experiment reproduces the removed aggregator's response
blackhole; regression must also prove enabled paths terminate their command I/O
under the same fault and that selective removal survives real worker startup.
Planted owner removal, duplicate starts, missing heartbeat, restored history and
false healthy state must fail production predicates. No task/CTS is disposed
while actual work/callbacks use it. Passing experiments do not replace these
mandatory, still-pending runtime controls.

## References

- [ADR-0047](0047-transactional-background-job-runtime.md).
- [P02b-4 decision package](../roadmap/phase-02b-events-auth.md#p02b-4-decision-package-2026-10-15).
- [Database Standards](../standards/05-database.md).
- [Infrastructure Standards](../standards/20-infrastructure-stack.md).
- [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs).
- [Pinned counter component][counter], [expiration component][expiration]
  and [public component hook][hook].
- [Pinned history filter][history] and [public exact-type removal][removal].
- [Pinned Npgsql command timeout][npgsql], [keepalive boundary][keepalive].
- [PostgreSQL 18 timeout definitions][timeouts] and [18.4 abort implementation][abort].

[counter]: https://github.com/hangfire-postgres/Hangfire.PostgreSql/blob/e0a4001c6627695d61a0eea19d4403882e4cea34/src/Hangfire.PostgreSql/CountersAggregator.cs#L63
[expiration]: https://github.com/hangfire-postgres/Hangfire.PostgreSql/blob/e0a4001c6627695d61a0eea19d4403882e4cea34/src/Hangfire.PostgreSql/ExpirationManager.cs#L73
[hook]: https://github.com/hangfire-postgres/Hangfire.PostgreSql/blob/e0a4001c6627695d61a0eea19d4403882e4cea34/src/Hangfire.PostgreSql/PostgreSqlStorage.cs#L125
[history]: https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/StatisticsHistoryAttribute.cs#L30
[removal]: https://github.com/HangfireIO/Hangfire/blob/1d4778c23410b365b5f9ce35458202efe0b0f3ea/src/Hangfire.Core/Common/JobFilterCollection.cs#L99
[npgsql]: https://github.com/npgsql/npgsql/blob/v10.0.0/src/Npgsql/Internal/NpgsqlConnector.cs#L2866-L2867
[keepalive]: https://www.npgsql.org/doc/keepalive.html
[timeouts]: https://www.postgresql.org/docs/18/runtime-config-client.html#GUC-STATEMENT-TIMEOUT
[abort]: https://github.com/postgres/postgres/blob/REL_18_4/src/backend/access/transam/xact.c#L2976-L2978
