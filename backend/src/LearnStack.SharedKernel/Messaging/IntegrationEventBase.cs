using System.Text.Json;

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

    /// <summary>
    /// Serialises this event for storage, by its runtime type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a convenience — the one way to write a payload that does not lose
    /// data.</b> Measured: <c>JsonSerializer.Serialize(@event)</c> where the
    /// declared type is <see cref="IIntegrationEvent"/> — which it is at every
    /// dispatch boundary, because ADR-0014 Amendment 2 made the port non-generic
    /// precisely so it would be — emits only the four members declared on the
    /// interface and silently drops every field the concrete event added. No
    /// exception, valid JSON. The row commits inside the business transaction
    /// that reported success, and the loss surfaces later as a
    /// <c>JsonException</c> on every dispatch attempt until the message
    /// dead-letters.
    /// </para>
    /// <para>
    /// Non-virtual and sealed by being non-virtual: an override could
    /// reintroduce exactly the bug it exists to prevent.
    /// </para>
    /// </remarks>
    public string ToPayloadJson(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(this, GetType(), options ?? PayloadJsonOptions);

    /// <summary>
    /// The serializer options the payload is written and read with.
    /// </summary>
    /// <remarks>
    /// Named and fixed, because they are part of the wire contract rather than a
    /// formatting preference: measured, a payload written with
    /// <see cref="JsonSerializerDefaults.Web"/> and read with the default
    /// options fails on every member, since one camel-cases and the other does
    /// not. A writer and a reader that disagree here dead-letter everything.
    /// </remarks>
    public static JsonSerializerOptions PayloadJsonOptions { get; } = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };
}
