# ADR-0039: The Optimistic Concurrency Token

## Status

Accepted

**Date:** 2026-08-27 **Deciders:** @platform

## Decision Drivers

- **The choice was deferred in writing, and then made three times by
  accident.** [Database Standards § Concurrency](../standards/05-database.md)
  says `row_version bigint` "… `xmin`-based tokens are an alternative; pick one
  project-wide" and stops there;
  [04-technical-architecture.md](../architecture/04-technical-architecture.md)
  repeats the same fork twice. In the absence of a decision, three shipped
  artefacts each picked differently: the canonical DDL declares
  `row_version bigint`, `IOptimisticConcurrency.Version` is `uint`, and Packet
  4's already-published `EntityTag.For(long)` / `SetEntityTag(…, long)` surface
  is `long`. Nothing has broken yet only because no table exists.
  [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md) writes the first
  one, and a token type is a column type.
- **The token is client-visible, so it outlives the row's storage.** Packet 4
  shipped ETag / `If-Match` concurrency: the token is minted into a response
  header, held by a client for an unbounded time, and echoed back. That makes
  its stability a contract with a third party, not an implementation detail.
- **Reversing it later is a destructive change on every mutable table.** Adding
  or dropping a concurrency column after rows exist is the two-step deploy
  [Database Standards § Migrations](../standards/05-database.md) reserves for
  destructive changes, on every tenant-owned table in the system at once.
- **One-way door.** Per
  [ADR-0035](0035-demand-gated-infrastructure.md)'s test: added six months from
  now, this touches every migration, every entity configuration and every
  conditional-request handler already written.

## Considered Options

1. **`row_version bigint`, with the kernel widened to `long`** (chosen). The
   column stays in the canonical template; `IOptimisticConcurrency.Version` and
   `AuditableEntity.Version` move from `uint` to `long`; the increment happens
   in the one primitive every audited mutation is routed through.
2. **PostgreSQL's `xmin` system column** (rejected). Mapped through Npgsql's
   `UseXminAsConcurrencyToken()`; the kernel keeps `uint`; `row_version` is
   deleted from the template.
3. **Both — `xmin` for the tenancy tables, `row_version` for domain tables**
   (rejected).

## Decision

LearnStack uses an explicit **`row_version bigint`** column as the optimistic
concurrency token, project-wide, on every table whose entity implements
`IOptimisticConcurrency`. The CLR type is `long`.

The value is incremented in `AuditableEntity`, by the same primitive that stamps
`UpdatedAt` / `UpdatedBy`, so that an audited mutation is a versioned mutation.
Today two methods stamp — `MarkUpdated` and `SoftDelete`, the latter by
assigning the fields itself — and Packet 6 routes both through one primitive
before adding the increment; see § Why `MarkUpdated` and not an interceptor for
why doing it the other way round ships a soft delete that no ETag notices.

There is no second token type and no per-table exception.

`xmin` is not used as a concurrency token anywhere.

## Context

### What the three-way split actually is

| Artefact | Type | Where |
|---|---|---|
| The canonical DDL | `row_version bigint NOT NULL DEFAULT 0` | [Database Standards § Tenant-Owned Tables](../standards/05-database.md) |
| The kernel marker | `uint Version` | `LearnStack.SharedKernel/Persistence/IOptimisticConcurrency.cs` |
| The audit base | `uint Version` | `LearnStack.SharedKernel/Domain/AuditableEntity.cs` |
| The shipped HTTP surface | `long` | `LearnStack.Api/Common/EntityTag.cs` |

`uint` is not an arbitrary third answer — it is the Npgsql convention for an
`xmin` token specifically, which is what makes the split a real fork rather
than a typo. The kernel was written expecting `xmin`; the DDL and the HTTP
surface were written expecting a counter.

### Why the client-visible token settles it

Two properties were measured against `postgres:18.4-alpine` rather than
recalled, because the widely-repeated version of each is wrong in one
direction or the other:

- **`VACUUM FREEZE` does *not* change `xmin`.** Measured: `753` before, `753`
  after. Since PostgreSQL 9.4 freezing sets an infomask bit and leaves the
  original xmin in the tuple header, so the commonly-cited "freezing rewrites
  xmin" objection is false and is *not* a reason to reject option 2.
- **A dump/restore *does* change it.** Measured: `753` before, `757` after the
  same row round-tripped through `pg_dump` / `psql`. Logical replication has
  the same shape for the same reason — the row is re-inserted by a new
  transaction, so it gets that transaction's id.

That second property is the one that matters, because the token is in a client's
hands. A `row_version` survives a restore, a logical-replication cutover and a
major-version upgrade unchanged, because it is data. An `xmin` does not,
because it is storage metadata about a tuple that no longer exists. After a
restore every outstanding `If-Match` in the wild would compare against a value
that changed for a reason no client can observe, and the failure is a
`412 Precondition Failed` storm indistinguishable from real contention.

### Why not `xmin`, given that its usual objection was false

Three remaining reasons, in order of weight:

1. **It cannot back a client-visible ETag across a maintenance window**, per
   the measurement above.
2. **`xid` is 32-bit and wraps.** A token whose value space wraps needs a
   comparison the application does not perform — the ETag comparison is
   string equality on a formatted number, and a wrapped xid can repeat a value
   a client is still holding.
3. **It would rewrite an already-published API surface.** `EntityTag.For(long)`,
   `ReadAssertion`, `Evaluate(…, long)` and `SetEntityTag(…, long)` shipped in
   Packet 4. Option 1 changes two kernel properties that nothing consumes yet;
   option 2 changes the public surface that already does.

Option 3 was rejected on the same ground both times it was considered: two
token types means two ETag derivations, two concurrency-failure paths, and a
per-table question at every future `[Idempotent]` or `If-Match` endpoint. The
standard's own phrasing — "pick one project-wide" — forecloses it.

### Why `MarkUpdated` and not an interceptor

[Database Standards § Audit Columns](../standards/05-database.md) previously
said "a shared EF interceptor populates these on `SaveChanges`", which no
shipped code does and which
[ADR-0033](0033-audit-durability-model.md) contradicts:
`AuditChangeTrackerInterceptor` is the only sanctioned `SaveChanges`
interceptor and it deliberately writes nothing. `AuditableEntity.MarkUpdated`
is the method that already exists and already refuses `default(UserId)`.

**But it is not currently the only path that stamps an update, and that has to
be fixed before the counter can live there.** `AuditableEntity.SoftDelete`
assigns `UpdatedAt` / `UpdatedBy` **directly** rather than delegating to
`MarkUpdated`. Incrementing the counter inside `MarkUpdated` alone would
therefore leave a soft delete un-versioned: a client holding the pre-delete
ETag would still satisfy `If-Match` on the row it had already deleted, and the
next conditional update would pass a precondition that is no longer true. The
guarantee this ADR wants — *an audited mutation is a versioned mutation* — is
not a property of the shipped code; it is a property Packet 6 has to create, by
routing `SoftDelete` through the same stamp-and-increment primitive.

`MarkCreated` leaves `Version` at its `0` default; the column's
`DEFAULT 0` and the CLR default agree, so an insert needs no special case.

## Consequences

### Positive

- One token type, one ETag derivation, one concurrency-failure path.
- The token survives restore, logical replication and major-version upgrade, so
  a client's `If-Match` means the same thing across a maintenance window.
- Packet 4's shipped `long` ETag surface needs no change.
- The version cannot advance without an audit stamp advancing with it.

### Negative

- Every mutable table carries an extra 8-byte column and every update writes it.
- A mutation that bypasses `MarkUpdated` — raw SQL, a bulk `ExecuteUpdate` —
  does not advance the token. `xmin` would have covered those for free. The
  mitigation is that such writes are already outside the audit trail and
  already require review; this ADR does not create the exposure, it declines to
  paper over it.
- `IOptimisticConcurrency.Version` and `AuditableEntity.Version` change type
  from `uint` to `long`. Nothing consumes them yet, which is precisely why the
  change is made now.

### Neutral

- `xmin` remains available for diagnostics and for any future read-side
  change-detection that is not client-visible. This ADR forecloses it as a
  *concurrency token*, not as a column.

## Implementation Notes

- **Phase 02a Packet 6, step 2** — widen `IOptimisticConcurrency.Version` and
  `AuditableEntity.Version` to `long`; route `SoftDelete` through the same
  stamp-and-increment primitive as `MarkUpdated`; declare
  `row_version bigint NOT NULL DEFAULT 0` on every mutable tenancy table and
  configure it as the EF concurrency token with
  `IsConcurrencyToken().ValueGeneratedOnAddOrUpdate()` — **not** `IsRowVersion()`,
  which maps to a provider-generated `bytea`/`rowversion` this design does not use.
- **Phase 02a Packet 6, step 1 — the propagation this ADR is not, on its own.**
  Until these land the corpus answers the question twice, and an implementer
  reading a standard rather than this ADR gets the withdrawn answer:

  | Carrier | What it still says | 
  |---|---|
  | [Database Standards § Concurrency](../standards/05-database.md) | "`row_version bigint` (incremented by an EF interceptor) … `xmin`-based tokens are an alternative; pick one project-wide" — wrong on the mechanism and still offering the rejected option |
  | [Database Standards § Audit Columns](../standards/05-database.md) | "A shared EF interceptor populates these on `SaveChanges`" — no such interceptor exists, and [ADR-0033](0033-audit-durability-model.md) reserves the only sanctioned `SaveChanges` interceptor for snapshot capture, which writes nothing |
  | [04-technical-architecture.md](../architecture/04-technical-architecture.md) | leaves the fork open twice — "using `xmin` or `row_version` column" and "`row_version` (`xmin` or explicit `bigint` column)" |
  | Database Standards / API Standards `**Derives from:**` headers | neither cites this ADR |
  | [21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md) | carries no entry for the rule below |
  | [Phase 02a § ADR commitments](../roadmap/phase-02a-kernel-tenancy.md) | does not list this ADR |
- **Phase 02a Packet 6** — the two infrastructure tables that carry no
  aggregate (`outbox_messages`, `idempotency_keys`) are not
  `IOptimisticConcurrency` entities and carry no `row_version`. Their write
  paths are a lease and a fencing token respectively
  ([ADR-0006](0006-events-and-outbox.md),
  [ADR-0037](0037-idempotency-key-contract.md)), which are stronger, not weaker.
- **Every later phase** — a new mutable aggregate inherits the column from the
  template. No per-table decision remains.

## Architecture Tests

Two rules. Neither exists yet; both are **Phase 02a Packet 6 deliverables**, and
registering them in
[21-architecture-tests-catalogue.md](../standards/21-architecture-tests-catalogue.md)
is part of shipping them. Naming them here is a commitment, not a description.

- `Aggregates_With_Optimistic_Concurrency_Map_RowVersion` — every entity
  implementing `IOptimisticConcurrency` has its `Version` configured as the
  concurrency token against a `row_version` column, and no configuration uses
  `IsRowVersion()`.
- `SoftDelete_Advances_The_Row_Version` — a behavioural test, because the
  structural one cannot see it: `SoftDelete` must leave `Version` strictly
  greater than it was, for the reason in § Why `MarkUpdated` and not an
  interceptor. Delete the increment and this test fails; that is the whole
  point of writing it down.

## Amendments

None.

## References

- [ADR-0002 — Initial Architecture](0002-initial-architecture.md)
- [ADR-0003 — Tenant Isolation Defense in Depth](0003-tenant-isolation-defense-in-depth.md)
- [ADR-0031 — PostgreSQL: Start on 18.x](0031-postgresql-major-version.md)
- [ADR-0033 — Audit Durability Model](0033-audit-durability-model.md)
- [ADR-0035 — Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [ADR-0037 — Idempotency Key Contract](0037-idempotency-key-contract.md)
- [Database Standards](../standards/05-database.md)
- [API Standards § Optimistic Concurrency](../standards/04-api-design.md)
