# ADR-0037: What an Idempotency Key Identifies, Owns, and Replays

## Status

Accepted

**Date:** 2026-08-20 **Deciders:** @platform

## Decision Drivers

- **The mechanism ships before its first consumer, and its failures are
  silent.** [Phase 02a Packet 4](../roadmap/phase-02a-kernel-tenancy.md) lands
  `[Idempotent]` with no endpoint carrying it;
  [Standards 04 § Idempotency](../standards/04-api-design.md) names the first
  ones as payment operations, notification sending and recording start/stop.
  Every way this mechanism can be wrong ends with a caller receiving a normal
  `2xx` — a duplicate charge, or a stale answer about a payload the client did
  not send. A subtly wrong idempotency layer is worse than none, because the
  client stops defending itself against the thing it no longer believes can
  happen.
- **Standards 04's rules were written for the happy path.** They fix the
  header, the 400 shape, the 24-hour window and the tenant scoping. They do not
  say what happens when the same key arrives for a *different request*, when an
  attempt outlives its own claim, when the client disconnects between the work
  completing and the answer being recorded, which parts of a response a replay
  has to reproduce, or what the store does when it runs out of room. Each of
  those is a decision, and each was being made implicitly by the
  implementation.
- **The durable store is a schema change, so its contract has to be right
  first.** The in-memory default is instance-local and dies with the process.
  The Postgres-backed implementation lands with the tenancy schema in Packet 6,
  and its columns, its transaction boundary and its RLS policy are this ADR's
  decisions in DDL form. Getting the port shape wrong now means a migration
  later against rows that already exist.

## Considered Options

1. **The key is one input to a request identity, the claim is owned, and
   capacity is an admission decision** (chosen). `(tenant, key)` addresses the
   record; a fingerprint of everything narrower decides whether replaying it
   answers the question that was asked; a fencing token decides who may write
   or release it; and a full store refuses new keys rather than dropping live
   ones.
2. **The key alone is the identity** (rejected). `(tenant, key)` addresses the
   record and any request presenting it gets the stored response.
3. **The key plus the endpoint** (rejected). A middle position: bind the key to
   the method and path, but not to the organization, the principal or the
   payload.
4. **Wait for the in-flight attempt instead of answering** (rejected). A
   duplicate that arrives while the first is running is held until the first
   finishes, then served its response.
5. **Evict to make room** (rejected). When the store is full, drop the oldest
   records so a new key always fits.

## Decision

An `Idempotency-Key` is a **client-chosen nonce within a tenant's key space**.
It is not an identity on its own, it does not confer ownership of the record it
creates, the answer it replays is the whole response rather than its status
line, and it is never displaced to make room for another.

- **Identity.** A stored record is addressed by `(tenant, key)` and carries a
  **fingerprint** — a SHA-256 digest over the tenant, the organization, the
  acting principal, the HTTP method, the path, the query string and the request
  body. Components are length-prefixed, not delimiter-separated. A key
  presented with a different fingerprint is **409 `idempotency_key_reuse`** —
  neither replayed nor run.
- **Ownership.** A claim issues a **fencing token**. Recording an outcome or
  releasing a key requires it; a caller that no longer owns the key is ignored,
  and told so, rather than obeyed.
- **Concurrency.** A duplicate arriving while the first attempt is still
  running is **409 `request_in_progress`** — a distinct code from
  `concurrency_conflict`, because the two ask the client for opposite things.
- **Replay fidelity.** A replay reproduces the status, the content type, the
  body, and every response header that describes the **outcome**. Headers that
  describe the *exchange* are not replayed; § Replay fidelity below lists them.
- **What is recorded.** An outcome is recorded; a condition is released. A
  completed operation whose answer could not be retained is recorded as a
  **tombstone** and its retry is refused, never re-run. § What is recorded
  below gives the full classification.
- **Capacity.** The store's ceilings are **admission** limits, not eviction
  policy. Expiry is the only reason a record leaves the store.
- **Guarantee.** At-most-once **while a claim is live**; at-least-once across
  lease expiry and process death. § The guarantee below states this precisely,
  because the looser reading — "exactly-once" — is not true and the difference
  is what a payment endpoint has to design around.

Port `IIdempotencyStore` and record `IdempotentResponse` live in
`LearnStack.SharedKernel.Idempotency`; the HTTP-surface rules live in
`LearnStack.Api.Idempotency`; the default `InMemoryIdempotencyStore` lives in
`LearnStack.Infrastructure.Idempotency`.

### Scope: tenant, organization, principal

The key space is addressed by `(tenant, key)`; **the organization is in the
fingerprint, not in the address**. Both would work; the fingerprint is chosen
because it makes a collision *visible* — a key reused across two organizations
answers `idempotency_key_reuse` instead of silently opening a second key space
the client did not know it had.

The distinction is not academic.
[ADR-0017](0017-tenant-organization-hierarchy.md) puts organizations inside
tenants, and a user can belong to more than one. Without the organization in
the digest, that user's key from Org A would replay Org A's response body
inside an Org B request — a cross-organization read through a boundary the
corpus otherwise defends at four layers.

The **principal** component is `user:<id>` for an authenticated caller and the
literal `anonymous` otherwise. Two anonymous callers in one tenant therefore
share it, and that is deliberate: with no authenticated subject, and with the
organization, method, path, query and body all equal, the two requests are
indistinguishable to the server, and replaying is the same answer to the same
question.

The **effective host is deliberately excluded**. A tenant may serve several
hosts ([ADR-0036](0036-tenant-resolution-trusted-inputs.md)), and the same
operation reaching the same tenant through two of them is one operation.
Including the host would turn a client that failed over between hosts into a
duplicate execution — the exact failure this mechanism exists to prevent.

### What is recorded

The classification reads the **error code** where the response carries one, and
falls back to the status where it does not. Status alone cannot separate "this
is the answer" from "ask again": both arrive as 409.

| Outcome | Store | On retry |
|---|---|---|
| 2xx, 3xx | Record | Replay |
| 4xx not listed below | Record | Replay |
| 408, 425, 429 | Release | Run again |
| 5xx, or a thrown attempt | Release | Run again |
| `concurrency_conflict`, `rate_limited`, `dependency_unavailable` | Release | Run again |
| Over the replay cap | **Tombstone** | **409 `idempotency_outcome_unavailable`** |

A recorded 4xx is the operation's answer and is the same answer every time; a
client that wants a different one has changed its request, and a changed
request is a different fingerprint. A `concurrency_conflict` is the exception
that proves the rule: it tells the client to re-read and re-submit, so
recording it would answer "conflict" for the whole window and the client could
never succeed with that key.

The tombstone is the case worth stating plainly. When a response exceeds the
replay cap the operation has already run, so releasing the key would let a
retry run it again — with a `2xx` both times, on the surface Standards 04
reserves for payments. The store records that the outcome happened without its
body, and the retry is told so. An endpoint that trips this is a design
mistake, and it is logged as an error, not silently absorbed.

### Replay fidelity

Replayed: the status, the content type, the body, and every response header not
listed below — `Location`, `ETag`, `Content-Language`, `Retry-After` when the
operation chose it, and anything an endpoint adds to describe what it did.

Not replayed, because each describes **this** exchange rather than the
operation's outcome:

| Header | Why |
|---|---|
| `Content-Length`, `Transfer-Encoding`, `Connection`, `Keep-Alive`, `Upgrade`, `Trailer` | Framing, recomputed for the new response |
| `Date`, `Server` | Properties of the current exchange |
| `Set-Cookie` | Bound to the first attempt's session, not to the work it did |
| `X-Correlation-Id` | Belongs to the request asking now; replaying it points a support engineer at the wrong trace |
| `Idempotency-Replayed` | Set by the replay itself — absent on the first answer, `true` on every later one |

**One caveat, stated because it is not fixable at this layer.** A replayed
error body is reproduced verbatim, and
[Standards 09](../standards/09-error-handling.md) puts `correlationId` **inside**
the Problem Details body. A replayed 4xx therefore carries the first attempt's
correlation id in its body and the current one in its header. That is the cost
of replaying a body verbatim; regenerating the body would mean re-running the
handler, which is the thing a replay must not do. Support tooling reading a
replayed error should treat the header as the live trace and the body's field
as the trace of the outcome being described.

### The guarantee

Precisely:

- **While a claim is live**, at most one attempt executes. A duplicate is
  refused.
- **A claim is a lease**, not a lock. It expires after five minutes. When it
  does, the store stops treating the first attempt as the owner — but it cannot
  stop that attempt from still running, so a second attempt can execute
  alongside an overrunning first. The fencing token stops the older attempt
  from *overwriting the record*; it does not stop its side effect.
- **Across process death**, at-least-once. The operation commits, then the
  filter records. A process that dies between those two points releases the key
  and the retry runs the operation again.

Recording happens **before** the response is delivered and on a cancellation
token that does not follow the connection, so a client disconnect — the case
that produces the retry — is not a loss. That narrows the at-least-once window
to actual process death rather than to every dropped connection.

An endpoint whose duplicate execution is genuinely intolerable needs its own
guard inside the business transaction; the idempotency key narrows the window,
it does not close it.

### Authorization and replay

MVC runs authorization filters **before** resource filters, so a replay still
passes `[Authorize]` and any policy attached to the endpoint. It does **not**
re-run permission or resource-scope checks that live inside the handler
([Standards 19](../standards/19-permissions.md)), because the handler does not
run. A principal whose in-handler permission was revoked after the first
attempt can therefore replay its own stored response for the rest of the
retention window.

That is accepted rather than overlooked: the response describes an operation
that was authorized when it happened, the replay is to the same principal in
the same organization, and re-authorizing a completed outcome would make a
retry answer differently from the call it is retrying. It is recorded here so
that a future endpoint whose response must not outlive a permission does not
discover it by accident.

### Webhooks are a different mechanism

Standards 04 lists "webhook processing" under idempotency, and that reads as if
`[Idempotent]` applies. It does not.
[Standards 04 § Webhooks](../standards/04-api-design.md) deduplicates on
`(provider, event_id)` from the payload, which LearnStack controls; an
`Idempotency-Key` header is chosen by the caller, and no provider can be made
to send one. Inbound webhooks use the provider mechanism. This ADR governs
client-supplied keys only, and Standards 04 is amended to say so.

### Fixed values

| Value | Setting | Why |
|---|---|---|
| Retention | 24 hours | Standards 04's stated window |
| Claim lease | 5 minutes | Longer than any request the surface allows; short enough that a dead process does not hold a key for a day |
| Replay cap | 256 KiB, headers included | An operation's outcome is an identifier, a receipt, a status — far under this. Exceeding it is a design mistake |
| Per-tenant ceiling | 1 000 records | Keeps the global ceiling from being a shared resource with no owner |
| Global ceiling | 10 000 records | Bounds the in-memory default; the durable store replaces it before the number matters |

These bind the in-memory default. The durable store inherits the retention and
the lease, which are contract; the ceilings are properties of holding records
in a process and do not survive into a table.

### The durable store

The Postgres-backed implementation lands in
[Packet 6](../roadmap/phase-02a-kernel-tenancy.md) as a **tenant-owned,
tenant-wide table**, and three things about it are decided here because the
port shape depends on them:

- **Its own transaction, and its own tenant setting.** A claim is taken before
  the action runs, so it is outside the MediatR
  `TransactionBehavior` that [Standards 11](../standards/11-security.md) relies
  on to `SET LOCAL app.tenant_id`. Each of `TryClaim`, `Complete` and `Abandon`
  therefore opens a short transaction whose **first statement** sets
  `app.tenant_id` from the trusted resolved context. Without this the RLS
  policy would reject every insert and return zero rows for every read.
- **`learnstack_app`, with no bypass.** The connection uses the ordinary
  application role. A store that reached for `learnstack_platform` would be
  invisible to the isolation tests, which is the failure mode
  [ADR-0003](0003-tenant-isolation-defense-in-depth.md) names by hand.
- **The claim is one statement.** `INSERT … ON CONFLICT (tenant_id, key) DO
  UPDATE … WHERE <the existing row has expired> RETURNING …` decides acquire
  versus in-flight versus replay in a single round trip. The fencing token is
  the `WHERE claim_token = $1` predicate on `Complete` and `Abandon` — which is
  why it belongs to the port and not to one implementation.

## Context

### Why the key alone is not an identity (option 2 rejected)

The key is chosen by the client, which makes it the least trustworthy part of
the request. Four consequences follow, and all four were reachable:

- **A tenant is not a principal.** Two users under one tenant can pick the same
  key — by collision, or because one of them went looking. The second is then
  handed the first's response body.
- **A tenant is not an organization.** The same user in two organizations
  collects one organization's answer inside the other.
- **A key is not an endpoint.** The same key sent to a second `[Idempotent]`
  endpoint replays the first endpoint's answer and silently skips the second
  operation. The client is told something succeeded that never ran.
- **A key is not a payload.** The classic client bug is a key reused after the
  request was edited — the amount changed, the key did not. Replaying reports
  success about the amount that was *not* sent. Stripe's API refuses this case
  by fingerprint for the same reason.

Option 3 (key plus endpoint) closes the third of these and none of the others,
at the same implementation cost as closing all four.

### Why a mismatch is refused rather than resolved

Two resolutions are available and both are silent failures: replay the stored
response (answering a question the caller did not ask) or run the operation
(defeating the key). Refusing is the only outcome that tells the client
something it can act on. **409** rather than 400, because the request is
well-formed and conflicts with state the server holds; `idempotency_key_reuse`
rather than `validation_failed`, because the remedy is a new key, not a
corrected field.

### Why the in-flight case is not `concurrency_conflict`

Standards 09 defines `concurrency_conflict` as an optimistic-concurrency token
mismatch, and
[Standards 04 § Optimistic Concurrency](../standards/04-api-design.md) tells a
client receiving one to re-read the resource and re-submit against the current
version. A client receiving the in-flight case must do the opposite: retry
**the same request with the same key**, changing nothing. One code cannot carry
both instructions, and the generated SDK has one branch per code.

### Why waiting is rejected (option 4)

Holding the connection open occupies a server thread and a client timeout for
work the server cannot speed up, and it converts one slow operation into two
stalled requests. It also has no answer for the case that matters — the first
attempt dying — where the waiter waits out the full lease and then gets
nothing. Answering immediately is honest and costs the client one retry.

### Why capacity is admission, not eviction (option 5 rejected)

An unexpired record is a promise for the rest of its window. Dropping one to
make room means the operation it describes can run a second time, so an
eviction policy is a capacity control that quietly cancels the guarantee it
exists to protect — and a tenant can trigger it on itself, or on its
neighbours, simply by minting keys in a loop.

Refusing a **new** key instead costs that caller a retry and costs the
guarantee nothing. An **existing** key is always served, so a client holding
one is never locked out of its own retry by somebody else's flood. The refusal
is `dependency_unavailable` (503), because it is a server-side resource
condition the client can retry through, and the per-tenant allowance keeps one
tenant's traffic from producing it for another.

### Why the claim is owned

Without a fencing token, an attempt that overran its lease can still call back.
Its `Complete` overwrites the record of the attempt that replaced it — the
newer answer silently replaced by the older one for the rest of the window —
and its release deletes the successor's live claim, so a third request runs the
operation alongside the second. The token makes both calls no-ops, and both
report that they were ignored: an operation that produced a side effect nobody
will replay is worth a log line rather than silence.

### What we punted on, and what would change our minds

- **The body is hashed in full on every `[Idempotent]` request.** For the
  payload sizes these endpoints carry this is not measurable. If an
  `[Idempotent]` endpoint ever accepts a large upload, hashing it twice — once
  here, once by the handler — becomes a real cost, and the answer is to
  fingerprint a declared subset of the request rather than to drop the payload
  from the identity.
- **At-least-once across process death is accepted, not solved.** Closing it
  means the record and the business transaction committing together, which
  needs the durable store and the outbox in the same database — available from
  Packet 6, and worth revisiting when the first payment endpoint is designed
  rather than now.
- **The port takes a raw `Guid` tenant.**
  [Standards 02](../standards/02-backend-coding.md) bars raw `Guid` on a public
  surface, and this matches the existing `ITenantContext.TenantId` rather than
  fixing it. The strongly-typed `TenantId` lands with the tenancy schema in
  Packet 6, and both move together.

## Consequences

### Positive

- The four cross-request leaks a client-chosen key enables — cross-user,
  cross-organization, cross-endpoint, cross-payload — are closed by one
  mechanism with one error code.
- A replayed `201` still says where the resource is. A replay is the same
  answer, not the same status.
- A disconnect at the moment the work completes — the case that produces the
  retry — no longer loses the record.
- No capacity condition can cause a duplicate side effect.
- The durable implementation's columns, transaction boundary and RLS role are
  decided before the table exists.

### Negative

- Every `[Idempotent]` request pays a SHA-256 pass over its body and a buffered
  read.
- A tenant at its allowance is refused new keys until its oldest records
  expire — availability traded for correctness, deliberately.
- Three new error codes on a surface whose whole argument is that it has few.
- The port carries a fencing token the in-memory default needs only for
  correctness under lease expiry, and that reads as ceremony until the durable
  store uses it as a SQL predicate.

### Neutral

- The client contract gains one rule — *a key belongs to one request* — that
  well-behaved clients already follow and careless ones were previously not
  told about.

## Implementation Notes

- Architecture test `Idempotent_Endpoints_Are_Unsafe_Methods` — an
  `[Idempotent]` safe method has no side effect to protect, and only makes a
  read fail for clients that did not send a header no read needs. Catalogued in
  [Standards 21](../standards/21-architecture-tests-catalogue.md).
- Error codes `request_in_progress`, `idempotency_key_reuse` and
  `idempotency_outcome_unavailable` are added to the
  [Standards 09](../standards/09-error-handling.md) table, to its frontend
  `AppError` union, and to `HttpStatusMap`; all three are **409**. Their
  localization keys carry the usual prefix — `lockey_request_in_progress`,
  `lockey_idempotency_key_reuse`, `lockey_idempotency_outcome_unavailable`.
- `[Idempotent]` publishes its own OpenAPI contract: the required header with
  its length bounds, the 400, and the three meanings of its 409. Without that
  the generated SDK omits the header and every call it makes is answered 400.
- **Port, default, phase, trigger** per
  [ADR-0035](0035-demand-gated-infrastructure.md), whose gated set gains a row
  in the same change: port `IIdempotencyStore`; default
  `InMemoryIdempotencyStore`; owning phase
  [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md), which ships the
  table alongside the rest of the tenancy schema; trigger **the first endpoint
  carrying `[Idempotent]` — expected in
  [Phase 09](../roadmap/phase-09-billing-integrations-analytics.md) — or the
  first deployment running more than one instance, whichever comes first**. The
  default is correct for one instance and wrong for two, and says so on its own
  type.

## Amendments

### Amendment 1 — Packet 6 ships the table; the store ships on its trigger (2026-08-27)

§ Decision Drivers and § The durable store said the Postgres-backed
implementation "lands with the tenancy schema in Packet 6". § Implementation
Notes, in the same document, gives the ADR-0035 four-part gating precisely:
owning phase Packet 6, "**which ships the table**", trigger "the first endpoint
carrying `[Idempotent]` … or the first deployment running more than one instance".
[Standards 20 § Demand-gated building blocks](../standards/20-infrastructure-stack.md)
carries the same row. **The gating row is right**; the two prose sentences are
loose, and are superseded by this amendment — read both as *the table* lands with
the tenancy schema in Packet 6. `PostgresIdempotencyStore` itself lands when the
gating row's trigger fires. The body is left as written, per
[Documentation Standards § ADR Amendments](../standards/13-documentation.md).

The distinction is the one ADR-0035 exists to draw. The **table** is one-way-door
schema: adding it later means a migration against a system that has already been
answering `[Idempotent]` requests out of an instance-local dictionary. The
**implementation** is additive: it replaces a registration at the composition
root and touches nothing already written. So the table ships now and
`InMemoryIdempotencyStore` stays registered — correct for one instance, wrong for
two, and saying so on its own type — until the trigger fires.

The canonical DDL is
[Database Standards § Idempotency](../standards/05-database.md), derived column
by column from the shipped port rather than restated here.

### Amendment 2 — The claim statement, corrected (2026-08-27)

§ The durable store said "**The claim is one statement.** `INSERT … ON CONFLICT
(tenant_id, key) DO UPDATE … WHERE <the existing row has expired> RETURNING …`
decides acquire versus in-flight versus replay in a single round trip." Measured
against the canonical DDL as `learnstack_app`: **it does not.** When the `DO
UPDATE`'s `WHERE` is false PostgreSQL performs no update and `RETURNING` emits
nothing, so *blocked by a live claim* and *blocked by a completed row* are both
`(0 rows)` and indistinguishable — and neither surfaces the stored `fingerprint`
that `Mismatched` needs or the four response columns a replay needs.

The decision the statement realises is unchanged. The statement is:

```sql
INSERT INTO idempotency_keys
    (tenant_id, key, fingerprint, claim_token, state, expires_at)
VALUES (@tenant, @key, @fingerprint, @token, 'in_flight', now() + interval '5 minutes')
ON CONFLICT (tenant_id, key) DO UPDATE SET
    -- Fires unconditionally so RETURNING always has a row; the expiry test moves
    -- into the SET expressions. `reclaimable` is expiry AND fingerprint equality,
    -- repeated because a SET expression cannot see a name bound elsewhere in the
    -- same statement.
    --
    -- The fingerprint term is not optional. With expiry alone, an expired lease
    -- met by a DIFFERENT request overwrote the stored fingerprint and RETURNING
    -- handed the caller back its own — so the caller could not detect the
    -- mismatch, and a changed request took over a key while the original attempt
    -- may still have been running. Measured on postgres:18.4-alpine: the reclaim
    -- returned `FINGERPRINT-B` where the row held `FINGERPRINT-A`, and the outcome
    -- table below says `Mismatched` wins over every row in it, this one included.
    fingerprint  = idempotency_keys.fingerprint,
    claim_token  = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN EXCLUDED.claim_token  ELSE idempotency_keys.claim_token  END,
    state        = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN 'in_flight'           ELSE idempotency_keys.state        END,
    expires_at   = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN EXCLUDED.expires_at   ELSE idempotency_keys.expires_at   END,
    -- The re-acquire branch MUST clear the previous outcome. Measured: without
    -- these four the new claim inherits the expired row's status_code and a later
    -- replay answers with a response this request never produced.
    status_code  = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN NULL ELSE idempotency_keys.status_code  END,
    content_type = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN NULL ELSE idempotency_keys.content_type END,
    headers      = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN NULL ELSE idempotency_keys.headers      END,
    body         = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN NULL ELSE idempotency_keys.body         END,
    -- Amendment 3: without this the reclaimed row reports a new fence against the
    -- timestamp of a claim several reclaims ago.
    claimed_at   = CASE WHEN idempotency_keys.expires_at <= now() AND idempotency_keys.fingerprint = EXCLUDED.fingerprint THEN EXCLUDED.claimed_at   ELSE idempotency_keys.claimed_at   END
RETURNING (xmax = 0) AS inserted, state, fingerprint, claim_token,
          status_code, content_type, headers, body;
```

`fingerprint` is assigned its own stored value rather than left out of the `SET`
list: a `DO UPDATE` must assign something, and assigning the stored value is what
makes the mismatch survive into `RETURNING`. An expired row whose fingerprint
differs is therefore untouched by the reclaim — same token, same state, same
outcome columns — and the caller reads the stored fingerprint and answers
`Mismatched`. An expired row whose fingerprint matches reclaims exactly as before;
both halves measured.

**The deciding column is `claim_token`, not `state`.** `xmax = 0` separates a fresh
insert from a conflict resolution, but `state` and `fingerprint` alone cannot
separate the two conflict outcomes that matter most: a claim blocked by a live lease
and a claim that *reclaimed* an expired one both return `state = 'in_flight'` with
the same stored fingerprint. Measured — the only difference is whose token came
back:

| Case | `inserted` | `state` | `claim_token` returned | Outcome |
|---|---|---|---|---|
| No row | `t` | `in_flight` | **this call's** | `Acquired` |
| Live lease held by another | `f` | `in_flight` | the **holder's** | `InFlight` |
| Expired lease, reclaimed | `f` | `in_flight` | **this call's** | `Acquired` |
| Completed, unexpired | `f` | `completed` | the completer's | `Completed` (replay the four response columns) |
| Completed with no response | `f` | `unreplayable` | the completer's | `Unreplayable` |
| Any of the above, different fingerprint | — | — | — | `Mismatched`, which wins over all of them |

So the store compares the `claim_token` the statement returned against the one it
generated for this call: **equal means this caller owns the claim** — whether by
insert or by reclaim — and anything else means someone else does. That is the same
ownership-by-identity test `InMemoryIdempotencyStore` already performs with
`ReferenceEquals`; the durable store performs it with a token because it has no
object to compare. `TryClaimAsync` takes no caller-supplied token precisely so this
comparison cannot be skipped at a call site: only the store knows the value it
minted.

### Amendment 3 — `claimed_at` on reclaim, and the outcome CHECK (2026-08-28)

Two corrections found by measuring Amendment 2's statement against the shipped
table.

**`claimed_at` is never refreshed.** The `DO UPDATE SET` list replaces the row's
whole identity on the reclaim branch — `fingerprint`, `claim_token`, `state`,
`expires_at`, and all four response columns NULLed — and does not touch
`claimed_at`, whose only writer is the initial insert's `DEFAULT now()`. A
reclaimed row therefore reports a new fence, a new lease and a new request against
the timestamp of a claim several reclaims ago. Nothing reads the column yet, which
is exactly why it is cheap to fix and easy to leave wrong: it is the column an
operator traces a duplicate side effect with. The `SET` list gains one line, in the
same shape as every other:

```sql
    claimed_at   = CASE WHEN idempotency_keys.expires_at <= now() THEN EXCLUDED.claimed_at ELSE idempotency_keys.claimed_at END,
```

**The state and the response columns are one fact, and the database now says so.**
Nothing tied `state` to `status_code` / `content_type` / `headers` / `body`: a
`completed` row could carry all four NULL, and the claim statement would report it
as `Completed`, which its own outcome table defines as "replay the four response
columns" — the caller then replays a response that does not exist. The reverse was
equally free: an `unreplayable` tombstone could carry a full `201` body. The
canonical DDL and the shipped migration both gain

```sql
    CONSTRAINT ck_idempotency_keys_outcome CHECK (
        (state =  'completed' AND status_code IS NOT NULL AND body IS NOT NULL)
     OR (state <> 'completed' AND status_code IS NULL AND content_type IS NULL
                              AND headers IS NULL AND body IS NULL))
```

`content_type` stays free in the completed arm, because the port defines it as null
for an empty body. The constraint matches the reclaim branch above, which already
NULLs all four alongside `state = 'in_flight'`.

Neither changes what this ADR decides. `PostgresIdempotencyStore` does not exist
yet — it is demand-gated on the trigger Amendment 1 names — so both land while the
table has no rows and the constraint validates instantly.

### Amendment 4 — the port takes `TenantId`, one packet later than promised (2026-09-03)

§ What we punted on said of the raw `Guid` tenant: "The strongly-typed `TenantId`
lands with the tenancy schema in Packet 6, **and both move together**." They did
not. Packet 6 typed `ITenantContext.TenantId`; `IIdempotencyStore` kept the raw
`Guid` through Packet 6 and most of Packet 7, and `IdempotentAttribute` carried a
comment naming the seam it crossed at a single call site. Nothing recorded the
divergence, which is how a promise like this is usually discovered — three packets
later, by someone who trusted it.

**What ships.** `TryClaimAsync`, `CompleteAsync` and `AbandonAsync` take
`TenantId`; the store's internal key becomes `(TenantId, string)` and its census
`Dictionary<TenantId, int>`. The single unwrapping site in `IdempotentAttribute`
is gone, so the port is now what [Standards 02](../standards/02-backend-coding.md)
requires of a public surface rather than the exception the original text
acknowledged.

**What changed in the guard, and why it is not cosmetic.** The refusal was
`tenantId == Guid.Empty`. A typed id has two ways to be unassigned — a Vogen value
nobody constructed, and one constructed from the all-zero `Guid` — and reading
`.Value` on the first throws from inside the id type, which is neither this
guard's contract nor a message a caller can act on. The guard now tests
`IsInitialized()` first and both sentinels after, matching `AuditInput.EnsureValid`
and `NpgsqlUnitOfWork`'s setter. Its test drives the unassigned value from an array
element, because `default(TenantId)` does not compile — Vogen's VOG009 analyzer
prohibits it — and an array slot is also how the value reaches production.

**What did not change.** The key space, the contract's states, the fingerprint
rule, and Amendment 1's gating of the durable store. This is the type of one
parameter, not a change to what the port does.

## References

- [ADR-0035: Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [ADR-0036: Trusted Inputs for Tenant and Organization
  Resolution](0036-tenant-resolution-trusted-inputs.md)
- [ADR-0032: Exception Handling, Logging and
  Observability](0032-exception-handling-logging-and-observability.md)
- [ADR-0017: Tenant and Organization Hierarchy](0017-tenant-organization-hierarchy.md)
- [Standards 04 § Idempotency](../standards/04-api-design.md)
- [Standards 09 § Result Type](../standards/09-error-handling.md)
- [Phase 02a: Kernel and Tenancy](../roadmap/phase-02a-kernel-tenancy.md)
