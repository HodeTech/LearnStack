using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.Messaging;

/// <summary>The process-local durable acceptance transport.</summary>
/// <remarks>
/// Publication persists every required subscription in one fresh application-role
/// transaction. Business construction and execution belong to the delivery executor.
/// </remarks>
public sealed class InProcessEventBus(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor tenantAccessor,
    IPartitionSerializer partitions,
    OutboxEventRegistry registry) : IEventBus
{
    public const string ActivitySourceName = "learnstack.messaging";

    public Task PublishAsync(
        IntegrationEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        var serialized = OutboxEventContract.Serialize(envelope.Event);
        var resolution = registry.Resolve(serialized.TypeName);
        if (resolution.EventType != envelope.Event.GetType()
            || resolution.Disposition is not (OutboxEventDisposition.Active
                or OutboxEventDisposition.IntentionallyUnsubscribed))
        {
            throw new InvalidOperationException("Publication requires an Active or intentionally unsubscribed contract.");
        }

        // Validate the complete delivery scope even for intentional absence.
        var context = EventTenantContext.FromEnvelope(envelope);
        return partitions.RunSequentiallyFor(serialized.PartitionKey,
            () => AcceptAsync(envelope, serialized, resolution, context, cancellationToken));
    }

    private async Task AcceptAsync(
        IntegrationEventEnvelope envelope,
        OutboxEventContract.SerializedEvent serialized,
        OutboxEventResolution resolution,
        EventTenantContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = tenantAccessor.Current;
        tenantAccessor.Current = context;
        try
        {
            if (resolution.Disposition == OutboxEventDisposition.IntentionallyUnsubscribed)
            {
                return;
            }

            // Restore before scope creation or service resolution. No handler is
            // resolved, and no unit from the publisher's scope can be joined.
            var scope = scopeFactory.CreateAsyncScope();
            var failed = false;
            try
            {
                await scope.ServiceProvider.GetRequiredService<PostgresDeliveryAcceptance>()
                    .AcceptAsync(envelope, serialized, resolution.Subscriptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                try
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
                catch when (failed)
                {
                    // Preserve the acceptance/COMMIT failure for producer retry.
                }
            }
        }
        finally
        {
            tenantAccessor.Current = previous;
        }
    }
}
