using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>One bounded ordered page on the current ordinary owner and its live tenant-read witness.</summary>
public interface IMembershipDirectoryReader
{
    Task<IReadOnlyList<MembershipDirectoryItem>> ReadAsync(MembershipId? after, int take,
        CancellationToken cancellationToken = default);
}
