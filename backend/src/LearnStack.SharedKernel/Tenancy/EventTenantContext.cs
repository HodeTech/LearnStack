namespace LearnStack.SharedKernel.Tenancy;

using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;

/// <summary>
/// The tenant context a consumer runs under, rebuilt from the envelope it is
/// handling.
/// </summary>
/// <remarks>
/// A consumer runs outside the request that produced the fact, so there is no
/// ambient context to inherit — which is why the tenant travels on the event and
/// the rest travels on the envelope. Restoring it before a handler runs is what
/// makes the query filters and the Row Level Security policies evaluate against
/// the right scope; a transport that skipped it would run every consumer against
/// nothing.
/// </remarks>
public sealed class EventTenantContext : ITenantContext
{
    private EventTenantContext(
        Guid tenantId,
        Guid? organizationId,
        UserId? causalActorUserId,
        string? correlationId,
        string? moduleName)
    {
        TenantId = tenantId;
        OrganizationId = organizationId;
        UserId = Identifiers.UserId.SystemActor;
        CausalActorUserId = causalActorUserId;
        CorrelationId = correlationId;
        ModuleName = moduleName;
    }

    /// <inheritdoc />
    public bool IsResolved => true;

    /// <inheritdoc />
    public Guid TenantId { get; }

    /// <summary>
    /// The organization the fact belongs to, when the envelope names one.
    /// </summary>
    /// <remarks>
    /// An earlier version hard-coded this to <c>null</c>, reasoning that a
    /// cross-module fact is tenant-level and that inventing an organization
    /// scope would narrow queries the producer never narrowed. Under the
    /// canonical Row Level Security policy
    /// (<see href="../../../../docs/standards/05-database.md">Standards 05</see>)
    /// the reasoning inverts: with <c>app.organization_id</c> unset, an
    /// organization-scoped row evaluates <c>false OR NULL OR NULL</c>, and a
    /// NULL policy result is false — so a hard <c>null</c> hides every
    /// organization-scoped row instead of widening to all of them, and
    /// <c>WITH CHECK</c> rejects writing one. Widening is the
    /// <c>app.scope = 'tenant'</c> hatch, not an absent value.
    /// </remarks>
    public Guid? OrganizationId { get; }

    /// <summary>Who the consumer's writes are attributed to.</summary>
    /// <remarks>
    /// Never <c>null</c>. <c>AuditableEntity.MarkCreated</c> refuses
    /// <c>default(UserId)</c> and <c>Guid.Empty</c>, so a null actor left every
    /// state-writing consumer with no value it could legally pass — it could not
    /// create an aggregate at all. This is always
    /// <see cref="UserId.SystemActor"/>; an envelope user remains separate as
    /// <see cref="CausalActorUserId"/> so the consumer does not impersonate the
    /// human who initiated asynchronous work.
    /// </remarks>
    public UserId? UserId { get; }

    /// <inheritdoc />
    public UserId? CausalActorUserId { get; }

    /// <inheritdoc />
    public string? CorrelationId { get; }

    /// <inheritdoc />
    public string? ModuleName { get; }

    /// <summary>Builds the context a handler for <paramref name="envelope"/> runs under.</summary>
    public static EventTenantContext FromEnvelope(
        IntegrationEventEnvelope envelope, string? moduleName = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // A confidently-resolved context for a tenant that does not exist is
        // worse than an unresolved one: once TransactionBehavior issues
        // SET LOCAL app.tenant_id, every query silently returns nothing instead
        // of failing. UnresolvedTenantContext.TenantId throws for the same
        // reason, and this is the path that would otherwise route around it.
        if (envelope.Event.TenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "An integration event carries no tenant. A consumer restored into "
                + "an all-zero tenant reads and writes nothing, silently.",
                nameof(envelope));
        }

        return new EventTenantContext(
            envelope.Event.TenantId,
            envelope.OrganizationId,
            envelope.ActorUserId,
            envelope.CorrelationId,
            moduleName);
    }
}
