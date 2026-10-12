namespace LearnStack.SharedKernel.Messaging;

/// <summary>Persists an integration fact on the enrolled producer's business transaction.</summary>
/// <remarks>
/// Enqueue validates and inserts immediately. It opens no connection, commits nothing,
/// and does not publish to the transport. Successful inserts share the owner's outcome.
/// </remarks>
public interface IOutbox
{
    /// <summary>Inserts one event, preserving its identity and declared routing.</summary>
    Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}
