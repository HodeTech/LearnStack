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

/// <summary>A target's immutable fact, and whether its affected person may still be mirrored.</summary>
/// <param name="SubjectActive">
/// Whether the affected person's User is Active now, read in the database under the person's User fence held shared
/// (ADR-0107 §§ 7, 9): a person whose account is closing or closed is refused by the delayed mirror, as by intake.
/// </param>
public sealed record ProviderEventAuditFact(Guid TargetId, Guid ReceiptId, Guid EventId, TenantId TenantId,
    IdentityAuthorityId AuthorityId, string RealmId, Guid ProviderEventId, UserId AffectedUserId,
    DateTimeOffset SourceOccurredAt, IdentityProviderEventKind Kind, bool SubjectActive)
{
    public override string ToString() => nameof(ProviderEventAuditFact);
}
