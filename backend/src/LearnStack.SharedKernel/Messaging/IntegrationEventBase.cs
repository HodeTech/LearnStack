namespace LearnStack.SharedKernel.Messaging;

/// <summary>
/// The base every integration event inherits, carrying the identity, tenancy and
/// ordering fields the transport needs.
/// </summary>
/// <remarks>
/// <para>
/// A record, and JSON-serialisable: the outbox stores the payload as JSON and the
/// processor deserialises it, so an event that cannot round-trip through
/// <c>System.Text.Json</c> is an event that cannot be delivered.
/// </para>
/// <para>
/// <see cref="PartitionKey"/> is abstract rather than defaulted. A default would
/// have to be either the tenant id — which silently serialises a tenant's whole
/// stream onto one partition, a real throughput cost taken by accident — or
/// something arbitrary. Making each event state its own ordering domain is the
/// only version where the guarantee means what it says.
/// </para>
/// </remarks>
public abstract record IntegrationEventBase : IIntegrationEvent
{
    /// <inheritdoc />
    public required Guid EventId { get; init; }

    /// <inheritdoc />
    public required Guid TenantId { get; init; }

    /// <inheritdoc />
    public required DateTimeOffset OccurredAt { get; init; }

    /// <inheritdoc />
    public abstract string PartitionKey { get; }
}
