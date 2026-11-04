using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Pagination;

namespace LearnStack.Api.Identity;

public sealed record InvitationResponse(string InvitationId, [property: PiiSensitive] string Email, string? OrganizationId,
    string RoleId, string RoleVersion, string Status, string ExpiresAt, string Version, string DeliveryStatus)
{
    public override string ToString() => nameof(InvitationResponse);
}
public sealed record InvitationPage(IReadOnlyList<InvitationResponse> Items, PageInfo PageInfo);
public sealed record InvitableRoleDirectory(IReadOnlyList<RoleSummaryResponse> Items);
public sealed record InvitationPolicyResponse(int Days, bool Present, string? Version, bool CanEdit);
