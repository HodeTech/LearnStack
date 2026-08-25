namespace LearnStack.SharedKernel.Tenancy;

using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;

/// <summary>
/// The tenant context a consumer runs under, rebuilt from the event it is
/// handling.
/// </summary>
/// <remarks>
/// A consumer runs outside the request that produced the fact, so there is no
/// ambient context to inherit — which is exactly why
/// <see cref="IIntegrationEvent.TenantId"/> travels on the event. Restoring it
/// before a handler runs is what makes the query filters and the Row Level
/// Security policies evaluate against the right tenant; a transport that skipped
/// it would run every consumer against nothing.
/// </remarks>
public sealed class EventTenantContext : ITenantContext
{
    private EventTenantContext(Guid tenantId, string? correlationId)
    {
        TenantId = tenantId;
        CorrelationId = correlationId;
    }

    /// <inheritdoc />
    public bool IsResolved => true;

    /// <inheritdoc />
    public Guid TenantId { get; }

    /// <summary>
    /// Always <c>null</c>: an integration event carries the tenant, not an
    /// organization.
    /// </summary>
    /// <remarks>
    /// Deliberate rather than missing. A fact crossing a module boundary is a
    /// tenant-level fact, and inventing an organization scope for the consumer
    /// would narrow queries the producer never narrowed — the failure would be
    /// silently missing rows, not an error.
    /// </remarks>
    public Guid? OrganizationId => null;

    /// <summary>Always <c>null</c>: a consumer acts as the system, not as a user.</summary>
    public UserId? UserId => null;

    /// <inheritdoc />
    public string? CorrelationId { get; }

    /// <inheritdoc />
    public string? ModuleName => null;

    /// <summary>Builds the context a handler for <paramref name="event"/> runs under.</summary>
    public static EventTenantContext FromEvent(IIntegrationEvent @event, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return new EventTenantContext(@event.TenantId, correlationId);
    }
}
