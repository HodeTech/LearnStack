using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;

/// <summary>Only the machine-claimed production act, not a business command or user directory.</summary>
public interface IProviderEventTargetPublisher
{
    Task ProduceAsync(ProviderEventRecipient recipient, CancellationToken cancellationToken = default);
}

/// <summary>The actual tenant delivery reads its exact enqueued immutable fact.</summary>
public interface IProviderEventAuditFactReader
{
    Task<ProviderEventAuditFact> ReadAsync(Guid targetId, CancellationToken cancellationToken = default);
}

public sealed record ProviderEventAuditFact(Guid TargetId, Guid ReceiptId, Guid EventId, TenantId TenantId,
    IdentityAuthorityId AuthorityId, string RealmId, Guid ProviderEventId, UserId AffectedUserId,
    DateTimeOffset SourceOccurredAt, IdentityProviderEventKind Kind)
{
    public override string ToString() => nameof(ProviderEventAuditFact);
}
