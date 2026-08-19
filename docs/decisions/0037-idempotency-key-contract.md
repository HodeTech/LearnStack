# ADR-0037: What an Idempotency Key Identifies, Owns, and Replays

## Status

Proposed

**Date:** 2026-08-20 **Deciders:** @platform

## Decision Drivers

- **The mechanism ships before its first consumer, and its failures are silent.**
  [Phase 02a Packet 4](../roadmap/phase-02a-kernel-tenancy.md) lands `[Idempotent]`
  with no endpoint carrying it; [Standards 04 § Idempotency](../standards/04-api-design.md)
  names the first ones as payment operations, webhook processing, notification sending
  and recording start/stop. Every way this mechanism can be wrong ends with a caller
  receiving a normal `2xx` — a duplicate charge, or a stale answer about a payload the
  client did not send. A subtly wrong idempotency layer is worse than none, because the
  client stops defending itself against the thing it no longer believes can happen.
- **Standards 04's rules were written for the happy path.** They fix the header, the
  400 shape, the 24-hour window and the tenant scoping. They do not say what happens
  when the same key arrives for a *different request*, when an attempt outlives its own
  claim, when the client disconnects between the work completing and the answer being
  recorded, or which parts of a response a replay has to reproduce. Each of those is a
  decision, and each was being made implicitly by the implementation.
- **The durable store is a schema change, so its contract has to be right first.** The
  in-memory default is instance-local and dies with the process. The Postgres-backed
  implementation lands with the tenancy schema in Packet 6, and its table columns are
  this ADR's decisions in DDL form. Getting the port shape wrong now means a migration
  later against rows that already exist.

## Considered Options

1. **The key is one input to a request identity, and the claim is owned** (chosen).
   `(tenant, key)` addresses the record; a fingerprint of everything narrower decides
   whether replaying it answers the question that was asked; a fencing token decides who
   may write or release it.
2. **The key alone is the identity** (rejected). `(tenant, key)` addresses the record and
   any request presenting it gets the stored response.
3. **The key plus the endpoint** (rejected). A middle position: bind the key to the
   method and path, but not to the principal or the payload.
4. **Wait for the in-flight attempt instead of answering** (rejected). A duplicate that
   arrives while the first is running is held until the first finishes, then served its
   response.

## Decision

An `Idempotency-Key` is a **client-chosen nonce within a tenant's key space**. It is not
an identity on its own, it does not confer ownership of the record it creates, and the
answer it replays is the whole response, not its status line.

- **Identity.** A stored record is addressed by `(tenant, key)` and carries a
  **fingerprint** of the acting principal, the HTTP method, the path with its query, and
  the request body. A key presented with a different fingerprint is **409
  `idempotency_key_reuse`** — neither replayed nor run.
- **Ownership.** A claim issues a **fencing token**. Recording a response or releasing a
  key requires it, and a caller that no longer owns the key is ignored rather than
  obeyed.
- **Concurrency.** A duplicate arriving while the first attempt is still running is
  **409 `request_in_progress`** — a distinct code from `concurrency_conflict`, because
  the two ask the client for opposite things.
- **Replay fidelity.** A replay reproduces the status, the content type, the body, and
  every response header that describes the **outcome**. Headers that describe the
  *exchange* — framing, `Date`, `Server`, `Set-Cookie`, the correlation id — are not
  replayed.
- **What is recorded.** An outcome is recorded; a condition is released. 2xx, 3xx and
  deterministic 4xx are recorded. Thrown attempts, 5xx, `408`, `425` and `429` release
  the key. A response larger than the replay cap releases the key and logs an error.
- **Crash boundary.** The guarantee is **exactly-once per recorded outcome and
  at-least-once across process death**. The response is recorded *before* it is
  delivered, and on a token that does not follow the connection, so a client disconnect
  is not a loss. A process that dies between the operation committing and the record
  being written releases the key, and the retry runs the operation again.
- **Ceilings.** The store's entry ceiling evicts **completed records only**, oldest
  first, against a **per-tenant allowance** before the global one. A live in-flight claim
  is never evicted.

Port `IIdempotencyStore` and record `IdempotentResponse` live in
`LearnStack.SharedKernel.Idempotency`; the HTTP-surface rules live in
`LearnStack.Api.Idempotency`; the default `InMemoryIdempotencyStore` lives in
`LearnStack.Infrastructure.Idempotency`.

## Context

### Why the key alone is not an identity (option 2 rejected)

The key is chosen by the client, which makes it the least trustworthy part of the
request. Three consequences follow, and all three were reachable:

- **A tenant is not a principal.** Two users under one tenant can pick the same key —
  by collision, or because one of them went looking. The second is then handed the
  first's response body, which is a cross-user read inside a boundary the corpus
  otherwise defends at four layers.
- **A key is not an endpoint.** The same key sent to a second `[Idempotent]` endpoint
  replays the first endpoint's answer and silently skips the second operation. The
  client is told something succeeded that never ran.
- **A key is not a payload.** The classic client bug is a key reused after the request
  was edited — the amount changed, the key did not. Replaying reports success about the
  amount that was *not* sent. Stripe's API refuses this case by fingerprint for the same
  reason.

Option 3 (key plus endpoint) closes the second of these and neither of the others, at
the same implementation cost as closing all three.

### Why a mismatch is refused rather than resolved

Two resolutions are available and both are silent failures: replay the stored response
(answering a question the caller did not ask) or run the operation (defeating the key).
Refusing is the only outcome that tells the client something it can act on. **409**
rather than 400, because the request is well-formed and conflicts with state the server
holds; `idempotency_key_reuse` rather than `validation_failed`, because the remedy is a
new key, not a corrected field.

### Why the in-flight case is not `concurrency_conflict`

Standards 09 defines `concurrency_conflict` as an optimistic-concurrency token
mismatch, and [Standards 04 § Optimistic Concurrency](../standards/04-api-design.md)
tells a client receiving one to re-read the resource and re-submit against the current
version. A client receiving the in-flight case must do the opposite: retry **the same
request with the same key**, changing nothing. One code cannot carry both instructions,
and the generated SDK has one branch per code.

### Why waiting is rejected (option 4)

Holding the connection open occupies a server thread and a client timeout for work the
server cannot speed up, and it converts one slow operation into two stalled requests. It
also has no answer for the case that matters — the first attempt dying — where the waiter
waits out the full claim timeout and then gets nothing. Answering immediately is honest
and costs the client one retry.

### Why the claim is owned

Without a fencing token, an attempt that overran its claim timeout can still call back.
Its `Complete` overwrites the record of the attempt that replaced it — the newer answer
silently replaced by the older one for the rest of the retention window — and its
release deletes the successor's live claim, so a third request runs the operation
alongside the second. The token makes both calls no-ops. The same field is what a
durable implementation needs as a `WHERE claim_token = $1` predicate, which is why it
belongs to the port rather than to one implementation.

### Why a live claim is never evicted

Evicting a live claim releases a key whose operation is still running, so the next retry
executes it again. That trades a bounded memory overshoot for a duplicated side effect,
which is the wrong direction for a mechanism whose entire purpose is the opposite.
In-flight claims are bounded by the requests concurrently in flight, which the server
bounds independently; completed records are the unbounded part, and they are the part
the ceiling collects. The per-tenant allowance is what keeps the global ceiling from
being a shared resource with no owner — without it, one tenant minting keys in a loop
revokes every other tenant's records.

### What we punted on, and what would change our minds

- **The body is hashed in full on every `[Idempotent]` request.** For the payload sizes
  these endpoints carry this is not measurable. If an `[Idempotent]` endpoint ever
  accepts a large upload, hashing it twice — once here, once by the handler — becomes a
  real cost, and the answer is to fingerprint a declared subset of the request rather
  than to drop the payload from the identity.
- **At-least-once across process death is accepted, not solved.** Closing it means the
  record and the business transaction committing together, which needs the durable store
  and the outbox in the same database — available from Packet 6, and worth revisiting
  when the first payment endpoint is designed rather than now.
- **The replay cap is a rule for endpoint authors, not a truncation.** An `[Idempotent]`
  endpoint answering with more than the cap is a design mistake; the key is released and
  logged as an error rather than a partial body stored.

## Consequences

### Positive

- The three cross-request leaks a client-chosen key enables — cross-user, cross-endpoint,
  cross-payload — are closed by one mechanism with one error code.
- A replayed `201` still says where the resource is. A replay is the same answer, not the
  same status.
- A disconnect at the moment the work completes — the case that produces the retry — no
  longer loses the record.
- The durable implementation's columns are decided before the table exists.

### Negative

- Every `[Idempotent]` request pays a SHA-256 pass over its body and a buffered read.
- The port carries a fencing token that the in-memory default needs only for correctness
  under clock skew, and that reads as ceremony until the durable store uses it as a SQL
  predicate.
- Two new error codes on a surface whose whole argument is that it has few.

### Neutral

- The client contract gains one rule — *a key belongs to one request* — that well-behaved
  clients already follow and careless ones were previously not told about.

## Implementation Notes

- Architecture test `Idempotent_Endpoints_Are_Unsafe_Methods` — an `[Idempotent]` safe
  method has no side effect to protect, and only makes a read fail for clients that did
  not send a header no read needs. Catalogued in
  [Standards 21](../standards/21-architecture-tests-catalogue.md).
- Error codes `request_in_progress` and `idempotency_key_reuse` are added to the
  [Standards 09](../standards/09-error-handling.md) table and to `HttpStatusMap`; both
  are **409**.
- **Port, default, phase, trigger** per [ADR-0035](0035-demand-gated-infrastructure.md):
  port `IIdempotencyStore`; default `InMemoryIdempotencyStore`; owning phase
  [Phase 02a Packet 6](../roadmap/phase-02a-kernel-tenancy.md); trigger **the first
  endpoint carrying `[Idempotent]`, or the first deployment running more than one
  instance — whichever comes first**. The default is correct for one instance and wrong
  for two, and says so on its own type.

## References

- [ADR-0035: Demand-Gated Infrastructure](0035-demand-gated-infrastructure.md)
- [ADR-0032: Exception Handling, Logging and Observability](0032-exception-handling-logging-and-observability.md)
- [Standards 04 § Idempotency](../standards/04-api-design.md)
- [Standards 09 § Result Type](../standards/09-error-handling.md)
- [Phase 02a: Kernel and Tenancy](../roadmap/phase-02a-kernel-tenancy.md)
