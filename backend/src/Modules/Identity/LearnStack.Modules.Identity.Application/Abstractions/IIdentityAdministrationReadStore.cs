using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Current scoped subjects on the announced ordinary MUST-read owner.</summary>
public interface IIdentityAdministrationReadStore
{
    Task<IReadOnlyList<MembershipDirectoryItem>> ReadOrganizationMembersAsync(MembershipId? after, int take,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Role>> ReadRolesAsync(CancellationToken cancellationToken = default);
    Task<Role?> ReadRoleAsync(RoleId roleId, CancellationToken cancellationToken = default);
}
