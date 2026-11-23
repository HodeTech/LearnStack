using LearnStack.Modules.Identity.Application.Contracts.IntegrationEvents;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Audit.Application.ProviderEvents;

/// <summary>The required transport-owned inbox precedes this constructor and its central MUST observation.</summary>
/// <remarks>
/// A delivery whose affected person's User is no longer Active, as the fact reader judged it in the database under the
/// person's User fence, refuses the person (ADR-0107 §§ 7, 9): its observation is withheld, so the delivery completes as
/// a recorded no-op with its inbox row and no audit row, and is never retried. A replayed delivery runs the same rule.
/// </remarks>
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
        var evidence = Observe(delivery, fact.TargetId, fact.ReceiptId, fact.ProviderEventId, fact.AuthorityId,
            fact.RealmId, fact.AffectedUserId, fact.Kind, fact.SourceOccurredAt);
        if (fact.SubjectActive)
            capture.AttachProviderEvent(evidence);
        else
            capture.WithholdProviderEvent(evidence);
    }
}
