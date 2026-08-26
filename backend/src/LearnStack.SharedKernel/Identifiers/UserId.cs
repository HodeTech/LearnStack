using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Cross-cutting strongly-typed identifier for the platform-wide user.
/// Lives in <see cref="LearnStack.SharedKernel"/> (per ADR-0023's
/// cross-cutting value-object placement) so audit metadata and other
/// kernel surfaces can reference users without depending on the Identity
/// module that lands in Phase 02b. The Identity module consumes the same
/// type once it ships; there is exactly one <c>UserId</c> shape.
/// </summary>
/// <remarks>
/// There is no <c>UserId.New()</c> convenience: new <see cref="UserId"/>
/// values are minted by aggregate methods through the injected
/// <c>IGuidFactory</c> (<c>UserId.From(guidFactory.NewUuidV7())</c>) so
/// tests can pin the value deterministically — see Standards 02 § Time.
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct UserId : IStronglyTypedId<Guid>
{
    /// <summary>
    /// The actor an integration-event consumer, a background job or any other
    /// non-request execution writes state as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A consumer runs outside the request that produced the fact, so there is
    /// no human to attribute its writes to — and
    /// <see href="../../../../docs/standards/18-audit-coverage.md">Standards 18</see>
    /// says such work is audited as an actor of type <c>system</c>. Without a
    /// concrete id that rule cannot be honoured: <c>AuditableEntity.MarkCreated</c>
    /// refuses <c>default(UserId)</c> and <c>Guid.Empty</c> alike, so a
    /// <c>null</c> actor left every state-writing consumer with no value it
    /// could legally pass and nothing to write at all.
    /// </para>
    /// <para>
    /// The value is fixed rather than generated, because it is a foreign key.
    /// Phase 02a Packet 6 owns the matching Tenancy seed and must create the
    /// <c>users</c> row before the first outbox consumer can write audit columns.
    /// No Tenancy schema exists before that packet. Version 7 shape with an all-zero random
    /// section, so it reads as deliberate in a database dump rather than as a
    /// stray identifier somebody forgot to replace.
    /// </para>
    /// </remarks>
    public static UserId SystemActor { get; } =
        From(Guid.Parse("00000000-0000-7000-8000-000000000001"));
}
