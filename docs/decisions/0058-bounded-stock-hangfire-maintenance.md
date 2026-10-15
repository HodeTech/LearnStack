# ADR-0058: Bounded Stock Hangfire Maintenance

## Status

Proposed — maintainer approval pending.

**Date:** 2026-10-16 **Deciders:** @cemil

**Bounded supersession proposed:** only ADR-0047's command/socket-budget
requirement for the pinned stock `CountersAggregator` path, plus the shared-pool
minimum needed for one retained maintenance checkout. ADR-0047 remains
Accepted; its business authority, roles, schema, attempts and all other bounds
are unchanged. No dependent maintenance wrapper is implemented by this proposal.

## Decision Drivers

- H1 keeps the pinned vendor assemblies and aggregation SQL unmodified.
- A server SQL deadline does not bound a client waiting for a lost response.
- The pinned counter component explicitly overrides the driver's command timeout
  with zero. Npgsql's public connection factory cannot intercept that command.
- Unresolved synchronous work must stay owned, counted and observable; a finite
  caller wait must never be described as proof that its underlying I/O stopped.
- A narrow maintenance failure must not consume business execution capacity or
  cause unbounded replacement tasks, connection acquisitions or shutdown joins.

## Considered Options

1. **Owned stock component with an explicit I/O-timeout exception** (proposed).
   Preserve the vendor SQL/assemblies and bound caller waiting, outstanding work,
   pacing and shutdown. Accept that one counter command's read/write I/O can
   remain unresolved until network recovery or process termination.
2. **Qualified upstream command-timeout fix** (viable alternative). Prefer it
   when a concrete compatible release or accepted upstream change supplies the
   missing bound. No such replacement has been qualified by this packet.
3. **First-party replacement aggregation SQL** (not proposed). Public component
   substitution is possible, but it adds ownership of vendor counter transactions
   and upgrade semantics outside the selected stock H1 contract.
4. **Reflection, tracing side effects or operating-system keepalive as a hard
   deadline** (rejected). These do not provide a supported per-command guarantee
   against an intermediary that acknowledges TCP while withholding responses.
5. **Disable aggregation without replacement** (rejected). Counter compaction
   disappears and the vendor operational store can grow without its normal
   maintenance. Silent loss of that behavior is not a timeout correction.

## Decision

Subject to maintainer acceptance, LearnStack retains the pinned stock counter
component through the public `PostgreSqlStorage.GetComponents()` extension point.
A single API-instance maintenance owner bounds its caller wait and retains the
actual operation. Only this component's explicit zero-timeout command read/write
I/O is exempt from ADR-0047's underlying command/socket bound. Server
statement/lock deadlines and every other command/connection budget remain
mandatory. The existing shared privileged pool is enabled with a maximum of at
least two connections; a smaller or nonpooled configuration refuses startup.

### Ownership and pacing

- One actual counter invocation may exist per API instance, shared across all
  Hangfire pools. Returning from a timed-out wait does not free that slot.
- Configure the provider's positive internal counter interval to one millisecond.
  The owner supplies the ordinary five-minute cadence after actual completion.
  The stock component otherwise waits five minutes inside `Execute`; timing out
  that normal sleep would falsely report every healthy pass as an overrun.
- Request cancellation after the configured SQL budget, at most five seconds.
  Stop waiting after that budget plus the one-second cleanup allowance. Preserve
  the actual task, cancellation source and checked-out resources until cleanup.
- A held invocation prevents another counter start on every pool. It consumes
  no transactional/external worker slot or tenant reservation. It still occupies
  one physical connection in the shared OutboxDispatcher pool. The minimum of
  two leaves a connection for independent paths; ordinary pool contention and
  SQL budgets remain observable, not a promise of unlimited parallelism.
- Retained counter work makes `jobs` health unhealthy and emits one bounded
  outcome/progress signal. Do not log provider messages, SQL or exception text.
- Host stop requests cancellation and joins through the existing one shared
  shutdown budget. No per-pool/per-task reset and no claim of forced termination.
  Late cleanup remains observed; fresh acquisitions after shutdown are refused.
- The owner adds no timer/task per skipped start and no replacement connection
  while its prior invocation remains held. Held, not-due, completed and failed
  paths all honor a cancelable shared wait/cadence or bounded backoff. The stock
  host immediately repeats a successful component return, so skipping work must
  not spin a dedicated thread on each pool. An unknown aggregation result is
  operational state; it never changes a business job attempt or completion.

### Exact boundary

The exception covers `Hangfire.PostgreSql` 1.21.1's stock `CountersAggregator`
only, with Core/NetCore 1.8.25 and Npgsql 10.0.0. The other components, provider
calls and authoritative stores keep ADR-0047's bounds. No source fork, copied
aggregation SQL, new role, grant, business request or public recovery API is
introduced. A provider upgrade requalifies the exact component inventory, public
hook and timeout behavior. An absent or additional matching component refuses
startup instead of silently losing the owner.

## Context

The pinned provider calls Dapper with `commandTimeout: 0` during aggregation.
Npgsql 10 uses that command value ahead of the source default and selects an
infinite read/write timeout. Its connection is sealed; the provider factory
requires that concrete type. Server startup `statement_timeout=5000` and
`lock_timeout=5000` correctly bound database execution and survive pool reset,
but cannot make an acknowledged response reach the client.

P02b-4 Step 2's isolated exact-pin experiment starts the actual stock component
against PostgreSQL 18.4 through a response-withholding proxy. The task remains
pending after eight seconds despite both server deadlines; closing the owned
proxy sockets releases it with a driver failure. This is a feasibility receipt,
not a committed regression or proof of the proposed owner. The passing blocked
SQL tests remain valid for server execution and are not a network-loss proof.

The public `GetComponents()` override allows a lifecycle decorator while leaving
the vendor SQL and binaries intact. A wrapper alone cannot establish a socket
deadline, which is why this proposal changes the named ADR-0047 requirement
explicitly rather than calling it a clarification or an ADR-0041 erratum.

**What changes this decision:** a qualified upstream fix removes the exception;
an operational requirement that even this single maintenance socket must terminate
within a hard deadline requires a new upstream/provider or process-isolation
decision. TCP keepalive and a successful ordinary SQL test are insufficient.

**Explicitly outside this decision:** production readiness routing/alerts remain
Phase 11; the P4 registered `jobs` check is still unmapped. No other component
inherits this exception and no business side effect is treated as completed.

## Consequences

### Positive

- Preserve stock H1 counter transactions and LGPL replacement/source packaging.
- Keep maintenance failures bounded in count and caller/shutdown waiting.
- Distinguish healthy pacing, retained actual work and proven I/O termination.

### Negative

- A response blackhole can hold one counter task/connection with unbounded
  command read/write I/O while the process lives. Health remains unhealthy until
  actual cleanup.
- LearnStack owns a small maintenance lifecycle decorator and its upgrade proofs.
- Vendor counter statistics can be stale while aggregation is retained.

### Neutral

- At-least-once wakeups, business atomicity, retry budgets and capacity isolation
  remain ADR-0047's contract; vendor statistics are not business authority.

## Implementation Notes

On acceptance, append a dated bounded-supersession disclosure to ADR-0047 without
changing its body/acceptance metadata. Update Standards 05/20/21, the event/job
architecture and the P02b-4 Step 2/4 records with the exact exception. Record it in
the decisions index. The accepted Step 4 lifecycle implementation owns the
decorator; Step 9 owns full observation/enforcement and final regression.

Before acceptance, dependency/schema/ACL/isolation validation continues, but the
socket obligation remains unresolved and no full Step 2 closeout is claimed.

## Architecture Tests

Extend the already registered canonical obligations
`Hangfire_Bootstrap_Uses_Pinned_Schema_And_Confined_Role` and
`BackgroundJob_Worker_Pools_Reserve_Independent_Capacity`; do not publish a
second name.
Their implementation must prove:

- Exact nonempty stock component inventory and a single shared maintenance owner
  across multiple actual server pools; unexpected inventory refuses startup.
- Healthy aggregation/cadence, canceled multi-pass work, and no false overrun from
  the stock internal interval. Actual counters aggregate under the granted role.
- A real response-blackhole overrun, single outstanding invocation after many
  attempted ticks, bounded wrapper-entry counts across actual pools, finite
  wait/join, unhealthy state and independent business progress with the smallest
  supported two-connection pool. Releasing the proxy proves late cleanup and
  permits the next pass. Nonpooled/one-connection configurations refuse startup.
- Shutdown/disposal races retain existing resources, forbid fresh acquisitions
  and observe failures without secret/provider text. No task/CTS is disposed
  while callbacks or the actual operation still use it. Prove real source
  disposal closes idle connectors, leaves the retained checkout owned, and
  closes that connector on its eventual return.
- Planted removal of ownership/retention, duplicate starts and a false healthy
  snapshot fail the same predicates used by production controls.

These proofs are mandatory and pending; this Proposed record supplies no passing
runtime or enforcement claim.

## References

- [ADR-0047](0047-transactional-background-job-runtime.md).
- [P02b-4 decision package](../roadmap/phase-02b-events-auth.md#p02b-4-decision-package-2026-10-15).
- [Database Standards](../standards/05-database.md).
- [Infrastructure Standards](../standards/20-infrastructure-stack.md).
- [Architecture-test catalogue](../standards/21-architecture-tests-catalogue.md).
- [Documentation governance](../standards/13-documentation.md#correcting-and-amending-adrs).
- [Pinned counter component](https://github.com/hangfire-postgres/Hangfire.PostgreSql/blob/e0a4001c6627695d61a0eea19d4403882e4cea34/src/Hangfire.PostgreSql/CountersAggregator.cs).
- [Pinned public component hook](https://github.com/hangfire-postgres/Hangfire.PostgreSql/blob/e0a4001c6627695d61a0eea19d4403882e4cea34/src/Hangfire.PostgreSql/PostgreSqlStorage.cs).
- [Pinned Npgsql command timeout](https://github.com/npgsql/npgsql/blob/v10.0.0/src/Npgsql/Internal/NpgsqlConnector.cs#L2859).
- [Npgsql keepalive boundary](https://www.npgsql.org/doc/keepalive.html).
