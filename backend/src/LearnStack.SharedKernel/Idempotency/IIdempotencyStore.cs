namespace LearnStack.SharedKernel.Idempotency;

/// <summary>
/// A replayable record of a completed idempotent request.
/// </summary>
/// <param name="StatusCode">The status the first attempt answered with.</param>
/// <param name="ContentType">Its media type, or <c>null</c> for an empty body.</param>
/// <param name="Headers">
/// The response headers worth reproducing. A replay that drops them is not the
/// same response: a created resource's <c>Location</c> is the only thing a
/// <c>201</c> says, and a client that retried through a timeout would get the
/// status without the answer.
/// </param>
/// <param name="Body">Its body, verbatim.</param>
public sealed record IdempotentResponse(
    int StatusCode,
    string? ContentType,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headers,
    byte[] Body);

/// <summary>What a claim attempt found.</summary>
public enum IdempotencyClaim
{
    /// <summary>Nothing held this key; the caller owns it and must run the operation.</summary>
    Acquired,

    /// <summary>Another request holds it and has not finished. The caller must not run.</summary>
    InFlight,

    /// <summary>A previous attempt completed; its response is available to replay.</summary>
    Completed,

    /// <summary>
    /// The key is held, but by a <b>different request</b>. Replaying would
    /// answer a question the caller did not ask; running would defeat the key.
    /// Neither is safe, so the caller is told instead.
    /// </summary>
    Mismatched,
}

/// <summary>
/// The outcome of a claim attempt.
/// </summary>
/// <param name="Outcome">What the store found.</param>
/// <param name="Token">
/// The fencing token for this claim, or <see cref="System.Guid.Empty"/> when the
/// claim was not acquired. <see cref="IIdempotencyStore.CompleteAsync"/> and
/// <see cref="IIdempotencyStore.AbandonAsync"/> take it back and ignore a caller
/// that no longer owns the key — without it, an attempt that overran the claim
/// timeout can delete or overwrite the record of the attempt that replaced it.
/// </param>
/// <param name="Stored">The response to replay, set only for <see cref="IdempotencyClaim.Completed"/>.</param>
public sealed record IdempotencyClaimResult(
    IdempotencyClaim Outcome,
    Guid Token,
    IdempotentResponse? Stored);

/// <summary>
/// Stores <c>(idempotency key, response)</c> so a retried
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Idempotency</see> request answers with what the first attempt produced
/// instead of doing the work twice. The contract is
/// <see href="../../../../docs/decisions/0037-idempotency-key-contract.md">ADR-0037</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every key is scoped to a tenant, and that is not a detail.</b> The key is
/// chosen by the client — two tenants will eventually pick the same ULID, and a
/// flat key space would hand the second one the first one's response body. The
/// interface takes the tenant explicitly rather than reading ambient context so
/// the scoping cannot be forgotten at a call site.
/// </para>
/// <para>
/// <b>A key alone does not identify a request.</b> Everything narrower than the
/// tenant — which user, which method, which path, which body — arrives as the
/// <c>fingerprint</c>, and a key presented with a different one is
/// <see cref="IdempotencyClaim.Mismatched"/> rather than replayed. That is what
/// keeps a second user in the same tenant from collecting the first one's
/// response, and what keeps a client that reused a key after editing its payload
/// from being told its edit succeeded.
/// </para>
/// <para>
/// <b>Port, default, phase, trigger</b>, per
/// <see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>:
/// this interface is the port; <c>InMemoryIdempotencyStore</c> is the working
/// default; the durable Postgres-backed implementation is owned by
/// <see href="../../../../docs/roadmap/phase-02a-kernel-tenancy.md">Phase 02a
/// Packet 6</see>; and its trigger is <b>the first endpoint that carries
/// <c>[Idempotent]</c>, or the first deployment that runs more than one
/// instance — whichever comes first</b>. The in-memory default is correct for
/// one instance and wrong for two, which is stated on the implementation rather
/// than implied.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims the key for this request, or reports who already has it.
    /// </summary>
    /// <param name="tenantId">The tenant the key space belongs to.</param>
    /// <param name="key">The client-chosen key.</param>
    /// <param name="fingerprint">
    /// An opaque digest of everything about the request that must match for a
    /// replay to be the same answer. Compared by ordinal equality; the store
    /// never interprets it.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IdempotencyClaimResult> TryClaimAsync(
        Guid tenantId,
        string key,
        string fingerprint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the response a claimed key produced. A <paramref name="token"/>
    /// that no longer owns the key is ignored.
    /// </summary>
    Task CompleteAsync(
        Guid tenantId,
        string key,
        Guid token,
        IdempotentResponse response,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases a claimed key without recording a response, so a request that
    /// failed before producing one can be retried. A <paramref name="token"/>
    /// that no longer owns the key is ignored.
    /// </summary>
    /// <remarks>
    /// Without this, an operation that threw would hold its key until the claim
    /// timed out and every retry would answer <see cref="IdempotencyClaim.InFlight"/>
    /// — turning one transient failure into minutes of refusals.
    /// </remarks>
    Task AbandonAsync(Guid tenantId, string key, Guid token, CancellationToken cancellationToken);
}
