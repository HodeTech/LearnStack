using LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Messaging;

namespace LearnStack.Modules.Identity.Application.ProviderEvents;

/// <summary>One stable identifier-only intent and its enqueue mark on the captured owner's transaction.</summary>
public sealed class ProviderEventTargetPublisher(IProviderEventTargetStore store, IOutbox outbox, IClock clock)
    : IProviderEventTargetPublisher
{
    public async Task ProduceAsync(ProviderEventRecipient recipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        await store.RequireClaimAsync(recipient, cancellationToken);
        await outbox.EnqueueAsync(new UserProviderEventObservedIntegrationEventV1
        {
            EventId = recipient.EventId,
            TenantId = recipient.TenantId.Value,
            OccurredAt = clock.UtcNow,
            TargetId = recipient.TargetId,
        }, cancellationToken);
        await store.MarkEnqueuedAsync(recipient, cancellationToken);
    }
}
