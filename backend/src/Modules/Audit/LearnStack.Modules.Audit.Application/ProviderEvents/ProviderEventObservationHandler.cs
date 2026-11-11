using LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Audit.Application.ProviderEvents;

/// <summary>The required transport-owned inbox precedes this constructor and its central MUST observation.</summary>
public sealed class ProviderEventObservationHandler(IProviderEventAuditFactReader facts, IAuditStateCapture capture, ITenantContext context)
    : ProviderEventAuditEvidence.MintingSource, IIntegrationEventHandler<UserProviderEventObservedIntegrationEventV1>
{
    public async Task HandleAsync(UserProviderEventObservedIntegrationEventV1 @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (context is not EventTenantContext delivery || delivery.EventId != @event.EventId
            || delivery.TenantId.Value != @event.TenantId)
            throw new InvalidOperationException("Provider observation has no matching delivery context.");
        var fact = await facts.ReadAsync(@event.TargetId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (fact.TargetId != @event.TargetId || fact.EventId != @event.EventId || fact.TenantId != delivery.TenantId)
            throw new InvalidOperationException("Provider observation does not match its retained target.");
        capture.AttachProviderEvent(Observe(delivery, fact.TargetId, fact.ReceiptId, fact.ProviderEventId, fact.AuthorityId,
            fact.RealmId, fact.AffectedUserId, fact.Kind, fact.SourceOccurredAt));
    }
}
