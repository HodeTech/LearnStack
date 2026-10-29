using System.Collections.Immutable;

namespace LearnStack.SharedKernel.Permissions;

/// <summary>The exact v2 Tenancy additions shared by its definition owner and the finite Role release.</summary>
public static class BoundedTenancyAdministrationPermissions
{
    public static PermissionDefinition OrganizationAdministration { get; } = new(PermissionScope.Tenant, "tenancy.organization.admin");
    public static PermissionDefinition LocaleWrite { get; } = new(PermissionScope.Tenant, "tenancy.locale.write");
    public static PermissionDefinition SettingWrite { get; } = new(PermissionScope.Tenant, "tenancy.setting.write");
    public static ImmutableArray<PermissionDefinition> Definitions { get; } = [OrganizationAdministration, LocaleWrite, SettingWrite];
}
