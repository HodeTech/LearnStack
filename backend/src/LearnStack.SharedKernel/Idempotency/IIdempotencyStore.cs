namespace LearnStack.SharedKernel.Idempotency;

/// <summary>
/// A replayable record of a completed idempotent request.
/// </summary>
/// <param name="StatusCode">The status the first attempt answered with.</param>
/// <param name="ContentType">Its media type, or <c>null</c> for an empty body.</param>
/// <param name="Body">Its body, verbatim.</param>
public sealed record IdempotentResponse(int StatusCode, string? ContentType, byte[] Body);

/// <summary>What a claim attempt found.</summary>
public enum IdempotencyClaim
{
    /// <summary>Nothing held this key; the caller owns it and must run the operation.</summary>
    Acquired,

    /// <summary>Another request holds it and has not finished. The caller must not run.</summary>
    InFlight,

    /// <summary>A previous attempt completed; its response is available to replay.</summary>
    Completed,
}

/// <summary>
/// Stores <c>(idempotency key, response)</c> so a retried
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Idempotency</see> request answers with what the first attempt produced
/// instead of doing the work twice.
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
/// Not demand-gated infrastructure in
/// <see href="../../../../docs/decisions/0035-demand-gated-infrastructure.md">ADR-0035</see>'s
/// sense, and deliberately absent from its table: the durable implementation is
/// not a vendor adapter waiting on a trigger, it is a Postgres table that lands
/// with the rest of the schema in Packet 6. The in-memory default is correct
/// for a single instance and wrong for two, which is stated on the
/// implementation rather than implied.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims the key for this request, or reports who already has it.
    /// </summary>
    /// <returns>
    /// <see cref="IdempotencyClaim.Acquired"/> with a null response, or
    /// <see cref="IdempotencyClaim.Completed"/> with the stored one, or
    /// <see cref="IdempotencyClaim.InFlight"/> with null.
    /// </returns>
    Task<(IdempotencyClaim Claim, IdempotentResponse? Stored)> TryClaimAsync(
        Guid tenantId,
        string key,
        CancellationToken cancellationToken);

    /// <summary>Records the response a claimed key produced.</summary>
    Task CompleteAsync(
        Guid tenantId,
        string key,
        IdempotentResponse response,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases a claimed key without recording a response, so a request that
    /// failed before producing one can be retried.
    /// </summary>
    /// <remarks>
    /// Without this, an operation that threw would hold its key until the TTL
    /// expired and every retry would answer <see cref="IdempotencyClaim.InFlight"/>
    /// — turning one transient failure into 24 hours of refusals.
    /// </remarks>
    Task AbandonAsync(Guid tenantId, string key, CancellationToken cancellationToken);
}
