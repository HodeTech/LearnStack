using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Exact scoped fresh graph after the Member lock; preserves unrelated tracked aggregates.</summary>
public interface IMembershipCustomFieldStore : IAggregateWriteStore<Membership, MembershipId>
{
    Task LockAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
    Task<Membership?> ReadForWriteAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
}
