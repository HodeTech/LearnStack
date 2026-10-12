using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Tenancy.Domain.Events;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>Translates the saved provisioning fact into one pending outbox row.</summary>
internal sealed class TenantCreatedDomainEventHandler(IOutbox outbox, IGuidFactory guids)
    : INotificationHandler<TenantCreatedDomainEvent>
{
    public Task Handle(TenantCreatedDomainEvent notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        return outbox.EnqueueAsync(new TenantCreatedIntegrationEventV1
        {
            EventId = guids.NewUuidV7(),
            TenantId = notification.TenantId.Value,
            OccurredAt = notification.OccurredAt,
        }, cancellationToken);
    }
}
