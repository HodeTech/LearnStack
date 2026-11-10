using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Exact scoped fresh graph after the Member lock; preserves unrelated tracked aggregates.</summary>
public interface IMembershipCustomFieldStore
{
    Task LockAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
    Task<Membership?> ReadForWriteAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
    Task SaveAsync(CancellationToken cancellationToken = default);
}
