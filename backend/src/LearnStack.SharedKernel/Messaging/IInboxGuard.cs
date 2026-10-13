using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Messaging;

/// <summary>Claims a stable subscription/event identity on the transport's business transaction.</summary>
/// <remarks>
/// Only the transport calls this, before handler construction. A true result is
/// provisional until the owning business transaction commits; rollback removes it.
/// False means the identity already exists. A competing transaction must commit
/// before its claim can produce that result.
/// </remarks>
public interface IInboxGuard
{
    Task<bool> TryClaimAsync(
        TenantId tenantId,
        string subscriptionId,
        Guid eventId,
        CancellationToken cancellationToken = default);
}
