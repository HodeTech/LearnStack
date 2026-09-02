# ADR-0040: The Ambient Unit of Work

## Status

Accepted

**Date:** 2026-08-27 **Deciders:** @platform

## Decision Drivers

- **Three documents name `IUnitOfWork` and none says what it wraps.**
  [ADR-0033](0033-audit-durability-model.md) calls it "the seam
  `TransactionBehavior` uses to open, commit and roll back the ambient
  transaction without naming a module's `DbContext`, and through which
  `IAuditStore` reaches the ambient connection", and makes it a
  [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) deliverable.
  [Backend Coding Standards § MediatR pipeline](../standards/02-backend-coding.md)
  says step 6 "opens the ambient transaction through `IUnitOfWork`". The
  roadmap says the Packet 3 shell "lights up here once the per-module
  `DbContext` exists". No document says what the seam owns, how a second
  `DbContext` relates to it, or what a nested begin does. The
  architecture-test catalogue registers nothing.
- **`SET LOCAL` is connection- *and* transaction-local, which makes the
  connection count a correctness property rather than a performance one.**
  [ADR-0003 Amendment 3](0003-tenant-isolation-defense-in-depth.md) puts
  `app.tenant_id` inside the ambient transaction. A `DbContext` that opened its
  own connection never saw that statement, so under the corrected RLS policy
  every read through it returns **zero rows** — silently, because a policy that
  filters everything is indistinguishable from a table with no matching data.
- **The durable audit write needs the ambient connection, not a context.**
  ADR-0033 puts `IAuditStore.WritePendingAsync(uow, ct)` immediately before
  `COMMIT` on the same transaction as the business write, as parameterised SQL
  against a table no module's `DbContext` maps. It belongs to no module and must
  not depend on one.
- **A wrong answer is invisible until Phase 03.** Packet 6 creates
  `TenancyDbContext` and nothing else, and never reads a tenant-owned table on a
  request path. Every candidate shape passes every test the packet can write.
  The cost lands when the second context appears with handlers already written
  against the seam.

## Considered Options

1. **One connection per request scope, owned by `IUnitOfWork`; every context
   and every cross-cutting writer enlists on it** (chosen).
2. **Resolve the owning module from the request type** (rejected). The behavior
   inspects the request's declaring assembly and opens on that module's
   context. It cannot give `IAuditStore` a connection, and a handler reading
   another module's data through an application contract still reads zero rows.
3. **A scoped `IUnitOfWork` per module** (rejected). Option 2 with more moving
   parts and the same two failures.
4. **Distributed transactions / `TransactionScope`** (rejected). Two
   connections to one database promoted to two-phase commit, to solve a problem
   that exists only because there were two connections — and a coordinator
   every self-hosted install ([ADR-0020](0020-triple-deployment-hybrid-license.md))
   would then have to carry.

## Decision

LearnStack's unit of work is **one database connection per scope**, and
`IUnitOfWork` owns it.

The member names below match the reference body in
[31-audit-subsystem.md](../architecture/31-audit-subsystem.md), which was written
against this seam before it was specified.

`IUnitOfWork` is a scoped service holding one `DbConnection` from the
application `NpgsqlDataSource` and, once opened, one `DbTransaction` on it.
Every module `DbContext` resolved in that scope is constructed against that same
connection and enlisted in that same transaction; `IAuditStore` and `IOutbox`
reach the same connection through the same seam.

> **Erratum — 2026-09-01.** The `SetTenantContextAsync` doc-comment in the sketch below
> reads "Issues SET LOCAL app.tenant_id / app.organization_id / app.scope". It issues the
> first two and not `app.scope`: `ITenantContext` carries no scope member, so the method
> has nothing to read one from — which Amendment 1 of this ADR already records, making the
> sketch inconsistent with its own document. `app.scope` has no carrier anywhere; see
> § Amendment 1. The Decision is unchanged. Current authority:
> [Security Standards § Tenant Context](../standards/11-security.md).

```csharp
// LearnStack.SharedKernel.Persistence
public interface IUnitOfWork : IAsyncDisposable
{
    /// The ambient connection. Opened on first access, never before.
    DbConnection Connection { get; }

    /// The ambient transaction; null before the first begin and after the terminal call.
    DbTransaction? Transaction { get; }

    /// True once a transaction has been opened and not yet resolved.
    bool HasActiveTransaction { get; }

    /// Joins the ambient transaction if one is active; otherwise opens it.
    /// Returns a handle whose Complete() is a no-op for a joiner — see § Nesting.
    /// (The handle shipped as CompleteAsync / FailAsync / IsOwner; this sketch is
    /// the accepted shape, and Amendment 2 records what changed and why.)
    Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken ct = default);

    /// Issues SET LOCAL app.tenant_id / app.organization_id / app.scope as the
    /// first statement inside the transaction. It lives here, not in the
    /// behavior, because it is SQL and Standards 02 keeps SQL out of the
    /// Application layer. A no-op for a joiner — see § Nesting.
    Task SetTenantContextAsync(ITenantContext context, CancellationToken ct = default);

    /// Commits. Throws if the transaction is marked rollback-only.
    Task CommitAsync(CancellationToken ct = default);

    Task RollbackAsync(CancellationToken ct = default);

    /// Marks the ambient transaction as unable to commit. Irreversible.
    void MarkRollbackOnly();
}
```

### What the ambient transaction spans — and what it does not

It spans **one business module's write, plus the cross-cutting infrastructure
rows that must commit with it, plus any reads**:

| Inside the transaction | Why |
|---|---|
| One aggregate's write, through its module's `DbContext` | The unit of work |
| The MUST-class `audit_log` row | [ADR-0033](0033-audit-durability-model.md) — it commits with the change it describes or not at all |
| The `outbox_messages` row | [ADR-0006](0006-events-and-outbox.md) — the outbox *is* the transactional boundary |
| Reads through any module's `DbContext`, including another module's application contract | They need the `SET LOCAL` that only exists on this connection |

It does **not** license a cross-aggregate or cross-module *write*.
[Architecture Standards § Aggregate Ownership](../standards/01-architecture-standards.md)
forbids cross-aggregate writes inside a single transaction and requires an
integration event, and [ADR-0010](0010-cross-module-communication.md) makes the
outbox row the boundary precisely so that extracting a module later changes the
transport and nothing else. **This ADR does not relax either rule.** A handler
that needs a second aggregate changed enqueues an integration event; that
enqueue is a row in `outbox_messages`, which is in the table above, so the
mechanism and this transaction model are the same mechanism.

The shared connection therefore exists for **reads, audit and outbox** — not to
make a forbidden write legal. Stated the other way round: if the only writes in
a transaction are one aggregate plus `audit_log` plus `outbox_messages`, why
share a connection at all? Because of the read row, and because the two
infrastructure writers are not `DbContext`s.

### Why this is not the Forbidden-list rule

[Database Standards § Forbidden](../standards/05-database.md) currently forbids
"Multiple `DbContext` instances within one logical transaction". Taken
literally, a handler that reads through another module's contract violates it.
The rule's target is *two independent contexts each opening its own
transaction* — two connections, two commit points, and a window where one has
committed and the other has not. Enlisting several contexts on one owned
connection removes that window rather than creating it.

**Packet 6 restates the rule** as: *more than one connection, or more than one
transaction, within one logical transaction.* Module boundaries are unaffected
— they are enforced by assembly references and architecture tests, not by
connection count, and each `DbContext` still maps exactly its own module's
entities.

### Nesting

An application contract may reach a second handler through `ISender`, so a
second `BeginTransactionAsync` on a live transaction is reachable and must be
defined:

- **The outermost `BeginTransactionAsync` owns the transaction.** It opens it
  and is the only caller whose `CommitAsync` commits.
- **A nested `BeginTransactionAsync` joins.** It returns a handle whose
  completion is a no-op; it never commits, never rolls back, and its paired
  `SetTenantContextAsync` does nothing — re-issuing `SET LOCAL` inside the same
  transaction would let an inner frame silently retarget the outer frame's
  tenant.
- **A nested failure marks the transaction rollback-only.** `CommitAsync` on a
  rollback-only transaction throws rather than committing a partial unit; the
  outermost frame rolls back. An inner `Result.Fail` that the outer handler
  deliberately absorbs is *not* a failure and does not mark it — only an
  exception, or an explicit `MarkRollbackOnly()`, does.
- **Concurrent use of the ambient connection is forbidden.** One connection
  means one command at a time; a handler that fans out with `Task.WhenAll` over
  two module contexts corrupts the protocol. `Modules_Do_Not_Parallelize_Over_The_Ambient_Connection`
  is owed for this.

### Consumers do not have a request, and still need all of this

The integration-event path does not go through MediatR:
`InProcessEventBus` creates the per-subscription scope and invokes
`IIntegrationEventHandler<T>.HandleAsync` **directly**, so `TransactionBehavior`
never runs. Yet [Phase 02b](../roadmap/phase-02b-events-auth.md) requires the
inbox check, the business write and the inbox marker to be atomic, and all
three touch tenant-owned tables that need `app.tenant_id`.

**The transport wraps each delivery in the same shape the behavior uses**:
`BeginTransactionAsync` → `SetTenantContextAsync` from the scope's
`EventTenantContext` → handler → `CommitAsync`, with a handler exception rolling
back. It is the same
`IUnitOfWork`, the same three statements and the same commit boundary; only the
entry point differs, because there are exactly two entry points into the
application — a MediatR request and an event delivery — and a transaction model
that covers one of them is not a model.

The alternative considered and rejected: making each handler an adapter that
re-sends an inner MediatR command purely to acquire a transaction. It puts the
whole pipeline — validation, authorization, audit classification — on a path
whose input is already trusted and already audited as the system actor, and it
makes the handler contract a lie about what a handler is.

Phase 02b implements this; Packet 6 ships the seam it needs. The corresponding
Phase 02b line still says the inbox marker goes in "the same `SaveChanges` as
the business write" — a formulation ADR-0033 **withdrew** in favour of the same
*transaction*. Packet 6 corrects that sentence.

### Who sets `app.tenant_id`, completely

> **Erratum — 2026-08-30.** The paragraph and table below read that the set "is
> closed" at six. It is seven. The seventh is `IOrganizationScopeValidator`,
> which reads `organizations` by `(tenant_id, id)` "in its own short read-only
> transaction that sets `app.tenant_id` as its first statement"; shown by
> [ADR-0036 § What is out of scope, and what is not](0036-tenant-resolution-trusted-inputs.md),
> Accepted 2026-08-18 — nine days before this ADR. The Decision is unchanged.
> Current authority: this subsection as corrected, reproduced in
> [Security Standards § The out-of-band setters](../standards/11-security.md).
> Recorded in Amendment 3.

The set is closed. Each entry either **is** the ambient transaction or owns a
short transaction of its own on its own connection, because it runs where no
ambient transaction exists yet:

| Setter | Transaction | Why not `TransactionBehavior` |
|---|---|---|
| `TransactionBehavior` | the ambient one | — the general case |
| The event transport, per delivery | the ambient one | There is no MediatR request; see above |
| `IIdempotencyStore` (durable) | its own short one | A claim is taken **before** the pipeline reaches step 6 ([ADR-0037](0037-idempotency-key-contract.md)) |
| `IAuditStore.WriteStandaloneAsync` | its own short one | ADR-0033: an audit row that must survive the rollback of the thing it describes |
| `IAuditStore.WriteBestEffortAsync` | its own short one | ADR-0033: SHOULD/MAY class, failures logged and dropped |
| The `AuditConfig` override loader | its own short read | An out-of-band cached projection, never a request-path query |

`app.resolving_host` has exactly one setter — `CachedHostToTenantResolver`, in
its own short read-only transaction — because the host is read *in order to
determine* the tenant ([Database Standards § Table classes](../standards/05-database.md)).

## Context

### What Packet 6 can and cannot prove

Packet 6 ships one context and reads no tenant-owned table on a request path,
so neither the multi-context property nor the zero-rows failure is observable
in it. The packet ships the **shape** — the owned connection, the shared
registration helper, the enlist call site, and the structural tests below — and
the properties become testable in Phase 03, when the second `DbContext` exists.
This is recorded so a reviewer does not read Packet 6's green suite as proof of
a property it cannot exercise.

## Consequences

### Positive

- One transaction, one commit point, one connection — so `SET LOCAL` protects
  every statement in the unit rather than the ones that happened to share a
  connection.
- `TransactionBehavior` stays generic and never references a module assembly.
- `IAuditStore` gets the ambient connection ADR-0033 requires, with none of the
  cross-context machinery that ADR explicitly withdrew.
- The consumer path and the request path have one transaction model, not two.
- No distributed-transaction coordinator in any deployment mode.

### Negative

- `DbContext` construction is no longer the EF default: contexts are built
  against a supplied connection, so a developer adding a module must use the
  shared registration helper rather than `AddDbContext<T>(o => o.UseNpgsql(cs))`.
  An architecture test carries this.
- **Nothing may read a tenant-owned table before the transaction opens**, and
  the pipeline puts `AuthorizationBehavior` at step 5, *before* step 6. A
  capability check (`AuthorizeAsync(actor, permission)`) is unaffected — it
  reads no table. But
  [Permission Standards § Resource-scope checks](../standards/19-permissions.md)
  also describes a **policy class** evaluating a resource against the actor; if
  such a class loads the resource itself it would run outside the transaction
  and read zero rows. **Packet 7 owns the resolution** — either policy classes
  receive an already-loaded aggregate from the handler, or a sanctioned
  pre-transaction read is defined — and must not leave it to be discovered by
  the first policy class written.
- One connection per scope means a long-running request holds a pooled
  connection for its whole life, including across an `await` on an external
  provider. `IProviderResilience<TPort>` bounds that and provider calls belong
  outside the transaction, but the coupling is real, and is why the connection
  is acquired on first use rather than at scope start.
- Concurrency inside a handler is constrained: no `Task.WhenAll` across two
  module contexts.
- The Forbidden-list rule has to be re-read by anyone who learned its old
  wording.

### Neutral

- `IUnitOfWork` exposes no `SaveChangesAsync`. Contexts save themselves; the
  unit of work owns only the transaction boundary. ADR-0033 already withdrew
  "the same `SaveChanges`" as the atomicity formulation.
- **Connection ownership.** `NpgsqlUnitOfWork` is `IAsyncDisposable` and is the
  sole owner: contexts are constructed with `contextOwnsConnection: false`, so
  disposing a `DbContext` does not return the connection to the pool underneath
  its siblings. Disposal order is transaction, then connection. Disposal of a
  unit of work with a live transaction rolls it back — a scope that ends without
  an explicit terminal call has failed, and committing on dispose would commit
  work nobody claimed was finished.

## Implementation Notes

- **Phase 02a Packet 6, step 1 — the corpus edits this ADR requires, none of
  which exist yet:**
  - [Database Standards § Forbidden](../standards/05-database.md) — restate the
    multiple-`DbContext` rule as stated above, and add this ADR to the
    `**Derives from:**` header.
  - [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md)
    — register the three rules below.
  - [Phase 02b](../roadmap/phase-02b-events-auth.md) — replace "inside the same
    `SaveChanges` as the business write" with the ambient-transaction
    formulation ADR-0033 substituted for it.
  - [Phase 02a § ADR commitments](../roadmap/phase-02a-kernel-tenancy.md) — list
    this ADR.
- **Phase 02a Packet 6, step 6** — `IUnitOfWork` and `IUnitOfWorkScope` in
  `LearnStack.SharedKernel/Persistence`; `NpgsqlUnitOfWork` in
  `LearnStack.Infrastructure/Persistence`; the shared `DbContext` registration
  helper; `TenancyDbContext` as its first consumer; and the
  `TransactionBehavior` body replacing the Packet 3 shell. The `SET LOCAL`
  statements are written here and read `UnresolvedTenantContext` until Packet 7
  populates it — correct and fail-closed, per ADR-0003 Amendment 3's note that
  between the two packets no tenant-owned table is read on a request path.
- **Phase 02a Packet 7** — `TenantResolverMiddleware` populates
  `ITenantContext`, the `SET LOCAL` statements start carrying real values, the
  isolation suite runs as `learnstack_app`, and the resource-scope question in
  § Consequences is resolved.
- **Phase 02a Packet 9** — `IAuditStore.WritePendingAsync(uow, ct)` immediately
  before `COMMIT`.
- **Phase 02b** — the event transport wraps each delivery as described in
  § Consumers, and the inbox marker commits with the business write.
- **Phase 03** — the second module `DbContext`, and with it the two behavioural
  tests this ADR's central properties are owed: a cross-module read inside the
  ambient transaction returns rows, and an outer failure after an inner write
  leaves zero rows in both modules.

## Architecture Tests

The first two are **Phase 02a Packet 6 deliverables** and shipped with it; the
third is registered in Packet 6 and **backfilled in Phase 03**, because no module
code exists for it to scan until the second module does. All three are registered
in
[21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md),
which carries their status.

- `Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork` — no `DbContext`
  registration configures its own connection string; every one goes through the
  shared helper. **Shipped**, Packet 6.
- `TransactionBehavior_Does_Not_Reference_A_Module_Assembly` — the behavior
  names `IUnitOfWork` and no `DbContext`. **Shipped**, Packet 6.
- `Modules_Do_Not_Parallelize_Over_The_Ambient_Connection` — no module code
  passes two `DbContext`-bound operations to `Task.WhenAll` / `Task.WhenAny`.
  **Awaiting backfill**, Phase 03.

## Amendments

### Amendment 1 — `SetTenantContextAsync`, and the member names (2026-08-27)

§ Decision's interface sketch was edited after this ADR was accepted, in the
commit that propagated it into the corpus. Recording it rather than leaving the
edit silent, because an Accepted ADR whose Decision changes without a note is the
thing amendments exist to prevent:

- **`SetTenantContextAsync(ITenantContext, CancellationToken)` was added.** The
  reference body in
  [31-audit-subsystem.md](../architecture/31-audit-subsystem.md) already called
  it, and it belongs on the seam rather than in `TransactionBehavior`: the
  statement it issues is SQL, and
  [Backend Coding Standards](../standards/02-backend-coding.md) keeps SQL out of
  the Application layer. The original sketch left the behavior issuing raw SQL,
  which contradicted a standard this ADR cites.
- **`BeginAsync` was renamed `BeginTransactionAsync`**, matching the same
  reference body. A seam with two spellings is a seam an implementer has to
  choose between.

Neither changes what the ADR decides — one connection per scope, owned by
`IUnitOfWork`, with every context and cross-cutting writer enlisted on it.

**`app.scope` is not settable from `ITenantContext` as shipped.** The interface
(`LearnStack.SharedKernel/Tenancy/ITenantContext.cs`) carries `TenantId`,
`OrganizationId`, `UserId`, `CausalActorUserId`, `CorrelationId` and `ModuleName`
— no scope member. `SetTenantContextAsync` therefore issues `app.tenant_id` and
`app.organization_id` from it today. Whether `app.scope` becomes a context member
or arrives another way is
[Packet 7](../roadmap/phase-02a-kernel-tenancy.md)'s to decide, with
`TenantResolverMiddleware`; until then no caller sets it and the tenant-scope read
hatch is simply unused, which is the correct default.

### Amendment 2 — the handle's shape, and what a joiner's rollback does not do (2026-08-28)

§ Decision specifies `IUnitOfWork` member by member and leaves `IUnitOfWorkScope`
at one sentence: "Returns a handle whose `Complete()` is a no-op for a joiner."
Implementing it in Packet 6 step 6 fixed three things that sentence does not, and
two of them were found by a review measuring the first implementation against
this ADR. Recording them here rather than leaving the divergence silent, on the
Amendment 1 precedent.

**The handle is `CompleteAsync` / `FailAsync` / `IsOwner`, and resolving through
it is the guarded path.** `Complete()` in the sketch becomes `CompleteAsync`,
matching every other member. `FailAsync` is added because which terminal call a
caller makes depends on the outcome it is reporting — `TransactionBehavior`
chooses by the `Result` the handler returned — and the alternative was the
frame-blind `IUnitOfWork.RollbackAsync`.

*Frame-blind* is the word that matters. `CommitAsync` and `RollbackAsync` take no
argument, so they resolve whatever frame is innermost. Measured: a nested frame
nobody resolved makes the outer `CommitAsync` decrement the depth, return without
committing, and hand back a success — nothing written, nothing raised. The handle
carries its own depth, so `CompleteAsync` refuses to resolve while a frame opened
after it is still open, and `TransactionBehavior` uses the handle for exactly that
reason. The bare calls remain for a caller that has no handle to hand.

**A joiner's rollback does not mark the unit.** § Nesting already decides this —
"an inner `Result.Fail` that the outer handler deliberately absorbs is *not* a
failure and does not mark it — only an exception, or an explicit
`MarkRollbackOnly()`, does" — and the first implementation contradicted it by
setting the flag inside `RollbackAsync` before the joiner check. Measured against
a real database: the outer handler absorbed the inner failure, reported success,
and its own committed row was discarded. The rule is realised by putting the mark
on the outermost frame only, and by having `TransactionBehavior` call
`MarkRollbackOnly()` explicitly on the exception path — which is the one cause
§ Nesting names that a terminal call cannot distinguish on its own.

**Two robustness rules follow from "irreversible" and from what a rollback is
for.** `MarkRollbackOnly` is sticky for the life of the unit of work, not of the
transaction — the sketch says irreversible, and a poison a later `BEGIN` clears is
not — so `BeginTransactionAsync` refuses to open a transaction on a marked unit.
And `RollbackAsync` on a unit with nothing left to resolve is a no-op rather than
an error, because rollback is the cleanup path and cleanup must never throw over
the exception it is cleaning up after. The strict form was measured replacing
every commit-time exception with "no transaction frame is open" — including the
`OperationCanceledException` that `AuditLogBehavior`, `HttpStatusMap` and
`IErrorTrackingProvider` each key on, so a client disconnecting mid-commit was
audited as a failure, captured, and answered `500` instead of `499`. A faulted
`COMMIT` leaves the outcome genuinely unknown, which is
[ADR-0033](0033-audit-durability-model.md)'s `Indeterminate` rather than something
to roll back; `TransactionBehavior`'s catch is filtered so it does not run after
one.

**`CompleteAsync` is loud about a leaked frame and `FailAsync` is silent, deliberately.**
Completing while a deeper frame is still open would commit nothing and report success, so it
throws. Failing while a deeper frame is still open is not ambiguous in the same way —
everything opened after a frame that failed has failed too — so it collapses instead:
marks the unit and rolls the whole thing back, without raising. Raising there would put a
bookkeeping exception on top of whatever the caller was already reporting, which is the
same mistake as the strict `RollbackAsync` above. `IUnitOfWorkScope.DisposeAsync` goes
through `FailAsync` for that reason: a frame that ends unresolved has failed, and it has
failed in exactly the way `FailAsync` already handles.

**"Frames, not savepoints" describes *this* mechanism, not the connection.** A joiner
issues no SQL, and the depth counter is in-process. EF Core is separate and unaffected by
it: its automatic-savepoint feature issues a real `SAVEPOINT` / `RELEASE SAVEPOINT` on the
ambient connection around **every** `SaveChangesAsync` that runs inside an externally
supplied transaction, at any frame depth, and nothing here turns that off. It is the
behaviour we want — a failed `SaveChanges` rolls back to its savepoint and leaves the
ambient transaction usable — and it is recorded because a reader of § Nesting alone would
conclude no nested SQL exists.

None of this changes what the ADR decides — one connection per scope, owned by
`IUnitOfWork`, with every context and cross-cutting writer enlisted on it.

### Amendment 3 — the setter set is seven, not six (2026-08-30)

**What was wrong.** § Who sets `app.tenant_id`, completely opens "The set is
closed" over a six-row table. The set was already seven when that sentence
entered the record.

**How it was shown.**
[ADR-0036](0036-tenant-resolution-trusted-inputs.md) § What is out of scope, and
what is not — Accepted **2026-08-18**, nine days before this ADR's 2026-08-27 —
schedules `IOrganizationScopeValidator`, "reading `organizations` by the
composite key `(tenant_id, id)` in its own short read-only transaction that sets
`app.tenant_id` as its first statement — the same pattern
`CachedHostToTenantResolver` uses for `app.resolving_host`". It is a setter of
`app.tenant_id` by that sentence's own terms, and it is not in the table. It
cannot be `TransactionBehavior`'s ambient transaction either: the organization
assertion is validated in the request edge, before the pipeline reaches step 6.

**Every carrier changed.** This ADR (the inline erratum above and this
amendment) and
[Security Standards § The out-of-band setters](../standards/11-security.md),
which reproduces the count and the table — "six" becomes seven, "four own a
short transaction of their own" becomes five, and the table gains an
`IOrganizationScopeValidator` row. No other document reproduces the table; a pointer
that only names the count — `.claude/skills/add-ef-migration/SKILL.md` is one — stays
correct by naming the corrected number rather than by carrying a second enumeration.

**The canonical list** remains this subsection, as corrected. Security Standards
reproduces it because that section is the placement authority; it is not a second
enumeration.

**The Decision is unchanged.** One connection per scope, owned by `IUnitOfWork`,
with every context and cross-cutting writer enlisted on it. The seventh setter
obeys the same rule as the four before it — its own short transaction, on its own
connection, connected as `learnstack_app` — which is the property the enumeration
exists to hold.

### Amendment 4 — one bounded cross-aggregate write is now sanctioned (2026-08-30)

Not a correction. § What the transaction spans says the transaction covers "one
aggregate's write" and closes with "**This ADR does not relax either rule.**"
Both statements were true when written and remain true of *this* ADR: it relaxes
nothing.

[ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md) does, once
and by enumeration. Tenant provisioning writes `Tenant` and its default
`Organization` in one transaction, because `tenants.default_organization_id`
carries an invariant no eventual-consistency mechanism can deliver. The table row
is therefore an incomplete description of the sanctioned transaction, and this
amendment is the pointer that keeps a reader of this ADR alone from concluding
the exception does not exist.

Nothing else changes. Cross-**module** writes remain forbidden with no exception,
which is the property ADR-0010's outbox boundary exists to protect, and the
exception's holder is a literal allow-list of one.

### Amendment 5 — the seam gains a read member for the tenant-context guard (2026-09-02)

**What changed.** § Decision enumerates the `IUnitOfWork` seam member by member. Packet 7
step 8 adds one: `bool IsTenantContextIssuedOn(DbTransaction? transaction)`. Recorded
here for the same reason Amendment 1 exists — that sketch is the contract, and an
addition to it that goes unrecorded is an addition nobody reviewed.

**Why it belongs on the seam and not beside it.** The guard —
`TenantContextGuardInterceptor`, registered on every module `DbContext` — has to ask
whether a sanctioned setter announced the transaction a command is about to run on. Put
on a side interface, a future `IUnitOfWork` implementation could omit it and be silently
unguarded; on the seam, the compiler makes every implementation answer, which is what the
addition buys.

> **Erratum — 2026-09-02.** The sentence below writes the check with **two** terms. The
> code that shipped in the same commit has **three**, and the missing one is load bearing:
> `transaction is not null && ReferenceEquals(transaction, _transaction) &&
> _tenantContextIssued`. After a commit `_transaction` is null and nothing clears the flag
> there, so without the first term `ReferenceEquals(null, null)` is true and the unit
> vouches for any command carrying no transaction at all. Shown by removing it: exactly
> one case fails, `A_Second_Transaction_Does_Not_Inherit_The_First_Ones_Announcement`.
> The statement was false when it entered the record rather than having aged, which is
> what makes this an erratum. What the amendment decides — a read member on the seam,
> taking the transaction, with no writer — is unchanged. Recorded in Amendment 6.

**Why it takes the transaction rather than returning a flag.** The check is
`ReferenceEquals(transaction, _transaction) && _tenantContextIssued`, and the reference
half is load-bearing: measured on Npgsql 10, a pooled data source hands back the **same**
`NpgsqlTransaction` instance across sequential open/begin/dispose cycles, so a bare flag —
or anything keyed on the transaction object — would vouch for a later transaction on the
strength of an earlier one's announcement. Keeping the comparison inside the type that
owns `_transaction` is what makes that impossible to get wrong at the call site.

**There is deliberately no writer.** The only code that may mark a transaction is
`SetTenantContextAsync`, which sets the flag after the `set_config` round trip returns —
so a failed announcement vouches for nothing — and the flag is cleared in the one block
that runs once per physical transaction. A module able to set it could silence the guard.

**The setter set is unchanged.** This adds a reader, not an eighth setter; Amendment 3's
seven stand.

**The Decision is unchanged.** One connection per scope, owned by `IUnitOfWork`, with
every context and cross-cutting writer enlisted on it.

### Amendment 6 — Amendment 5 wrote the check with a term missing (2026-09-02)

**What was wrong.** Amendment 5's § Why it takes the transaction rather than returning a
flag gives the check as `ReferenceEquals(transaction, _transaction) &&
_tenantContextIssued`. The shipped member has a third term first:
`transaction is not null`.

**How it was shown.** Removing that term and running the guard suite produces exactly one
failure — `A_Second_Transaction_Does_Not_Inherit_The_First_Ones_Announcement`, "found
True". After a commit `_transaction` is null and nothing clears the flag there, so
`ReferenceEquals(null, null)` is true and the unit would vouch for any command carrying no
transaction at all. The formula appears in no other document; Standards 05 and 11 describe
the marker without writing it out.

**Why it is an erratum rather than an amendment to a stale sentence.** Amendment 5 and the
three-term code landed in the same commit, `087b95c`. The sentence never described the
code, so it was false when it entered the record — ADR-0041's inline-erratum case, not the
supersede-what-has-aged case.

**Every carrier changed.** This ADR: the inline erratum beside Amendment 5's formula, and
this amendment. The code is unchanged and was already correct; its test coverage was added
in `8ff3743`, which is what made the discrepancy visible.

**The Decision is unchanged**, and so is Amendment 5's: one read member on the seam, taking
the transaction, with no writer.

## References

- [ADR-0002 — Initial Architecture](0002-initial-architecture.md)
- [ADR-0003 — Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md)
- [ADR-0006 — Events and Outbox](0006-events-and-outbox.md)
- [ADR-0010 — Cross-Module Communication](0010-cross-module-communication.md)
- [ADR-0032 — Exception Handling, Logging, and Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0033 — Audit Durability Model](0033-audit-durability-model.md)
- [ADR-0037 — Idempotency Key Contract](0037-idempotency-key-contract.md)
- [ADR-0039 — The Optimistic Concurrency Token](0039-optimistic-concurrency-token.md)
- [Architecture Standards § Aggregate Ownership](../standards/01-architecture-standards.md)
- [Backend Coding Standards § MediatR pipeline](../standards/02-backend-coding.md)
- [Database Standards](../standards/05-database.md)
- [Permission Standards § Resource-scope checks](../standards/19-permissions.md)
- [Security Standards § Tenant Context](../standards/11-security.md)
