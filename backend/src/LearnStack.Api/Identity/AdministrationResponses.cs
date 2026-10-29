using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Pagination;

namespace LearnStack.Api.Identity;

public sealed record AdministrationVersionReceipt(string Version);

public sealed record MembershipDetailResponse(string MembershipId, string UserId, string? OrganizationId,
    string Status, [property: PiiSensitive] string? DisplayName, [property: PiiSensitive] string? PreferredLocale,
    IReadOnlyList<string> RoleIds, string Version)
{
    public override string ToString() => nameof(MembershipDetailResponse);
}

public sealed record OwnMembershipProfileResponse([property: PiiSensitive] string? DisplayName,
    [property: PiiSensitive] string? PreferredLocale, string Version)
{
    public override string ToString() => nameof(OwnMembershipProfileResponse);
}

public sealed record RoleSummaryResponse(string RoleId, string BuiltInKey, string CapabilityScope, string Version);
public sealed record RoleDirectoryResponse(IReadOnlyList<RoleSummaryResponse> Items);
public sealed record AdministrationGrantResponse(string Scope, string Key);
public sealed record RoleDetailResponse(string RoleId, string BuiltInKey, string CapabilityScope, string Version,
    IReadOnlyList<AdministrationGrantResponse> Grants, int AdoptedReleaseVersion, string AdoptedReleaseFingerprint);
public sealed record PermissionRegistryItemResponse(string Scope, string Key, string LabelKey, bool CanGrant);
public sealed record RoleReleaseResponse(int Version, string Fingerprint, IReadOnlyList<AdministrationGrantResponse> TenantAdminDelta);
public sealed record PermissionRegistryResponse(IReadOnlyList<PermissionRegistryItemResponse> Items, RoleReleaseResponse Release);

public sealed record OwnMembershipContextResponse(string TenantId, string? OrganizationId,
    [property: PiiSensitive] string TenantName, [property: PiiSensitive] string? OrganizationName,
    string EntryOrigin, bool CanUseCurrentHost)
{
    public override string ToString() => nameof(OwnMembershipContextResponse);
}
public sealed record OwnMembershipContextPage(IReadOnlyList<OwnMembershipContextResponse> Items, PageInfo PageInfo)
{
    public override string ToString() => nameof(OwnMembershipContextPage);
}

public sealed record OrganizationResponse(string OrganizationId, string Slug, [property: PiiSensitive] string DisplayName,
    string Status, bool IsDefault, string Version)
{
    public override string ToString() => nameof(OrganizationResponse);
}
public sealed record OrganizationDirectoryResponse(IReadOnlyList<OrganizationResponse> Items,
    string? DefaultOrganizationId, string TenantVersion, PageInfo PageInfo)
{
    public override string ToString() => nameof(OrganizationDirectoryResponse);
}
