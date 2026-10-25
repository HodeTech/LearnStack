using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Cross-cutting strongly-typed identifier for the platform-wide user.
/// Lives in <see cref="LearnStack.SharedKernel"/> (per ADR-0023's
/// cross-cutting value-object placement) so audit metadata and other
/// kernel surfaces can reference users without depending on the Identity
/// module. The Identity module consumes the same type; there is exactly one
/// <c>UserId</c> shape.
/// </summary>
/// <remarks>
/// There is no <c>UserId.New()</c> convenience: new <see cref="UserId"/>
/// values are minted by admitted owners through the injected
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
    /// The value is fixed rather than generated so it reads as deliberate in a
    /// database dump rather than as a stray identifier somebody forgot to
    /// replace — version 7 shape with an all-zero random section.
    /// </para>
    /// <para>
    /// <b>It is not a foreign key, and needs no <c>users</c> row.</b>
    /// <c>created_by</c>, <c>updated_by</c>, <c>deleted_by</c> and
    /// <c>audit_log.actor_user_id</c> carry no referential constraint anywhere in
    /// the schema, so an actor identifier can remain independently of account closure.
    /// ADR-0072 retains the global binding/tombstone rather than deleting that row.
    /// It preserves audit continuity but remains pseudonymous and may be linked to
    /// a person through other retained records; erasing the user row alone does not
    /// establish anonymization. An enforced foreign key would require an explicit
    /// deletion policy for these retained references. See
    /// <see href="../../../../docs/decisions/0038-cross-cutting-port-and-event-contracts.md">ADR-0038
    /// Amendment 1</see>; the <c>users</c> table itself is owned by Identity.
    /// </para>
    /// </remarks>
    public static UserId SystemActor { get; } =
        From(Guid.Parse("00000000-0000-7000-8000-000000000001"));
}
