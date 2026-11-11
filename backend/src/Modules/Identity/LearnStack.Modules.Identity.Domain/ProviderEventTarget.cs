using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>Immutable tenant delivery evidence; the confined intake owns creation, not a user command.</summary>
[TenantOwned]
public sealed class ProviderEventTarget : ITenantOwned
{
    private ProviderEventTarget() { }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ReceiptId { get; private set; }
    public Guid EventId { get; private set; }
    public IdentityAuthorityId AuthorityId { get; private set; }
    public string RealmId { get; private set; } = null!;
    public Guid ProviderEventId { get; private set; }
    public UserId AffectedUserId { get; private set; }
    public byte[] SubjectKey { get; private set; } = null!;
    public DateTimeOffset SourceOccurredAt { get; private set; }
    public string SourceKind { get; private set; } = null!;
    public DateTimeOffset ReceivedAt { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTimeOffset? ClaimUntil { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? EnqueuedAt { get; private set; }

    public override string ToString() => nameof(ProviderEventTarget);
}
