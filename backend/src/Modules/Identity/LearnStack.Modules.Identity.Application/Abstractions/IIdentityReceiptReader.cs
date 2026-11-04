using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Exact historical roots only; no Profile, Assignments or Grants are loaded or disclosed.</summary>
public interface IIdentityReceiptReader
{
    Task<Membership?> ReadMembershipAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
    Task<Role?> ReadRoleAsync(RoleId roleId, CancellationToken cancellationToken = default);
}
