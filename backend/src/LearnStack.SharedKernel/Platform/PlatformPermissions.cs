using LearnStack.SharedKernel.Permissions;

namespace LearnStack.SharedKernel.Platform;

/// <summary>
/// The Platform permission catalogue: a closed set, kept apart from the tenant registry.
/// </summary>
/// <remarks>
/// <para>
/// A Platform definition is never assignable to a tenant Role and never enters the
/// administration grant union, so adding one changes neither the served tenant document nor
/// the OpenAPI gate (ADR-0101 § 5, ADR-0100 Amendment 1). The set grows one packet at a
/// time: a key is registered with the operation that has a denied proof for it, never
/// ahead of it.
/// </para>
/// <para>
/// Registration grants nothing. Authority is a grant held by LearnStack and evaluated per act
/// by <see cref="IPlatformOperatorGrantSource"/>.
/// </para>
/// </remarks>
public static class PlatformPermissions
{
    /// <summary>Toggle a declared killswitch.</summary>
    public static PermissionDefinition KillswitchWrite { get; } =
        new(PermissionScope.Platform, "platform.killswitch.write");

    /// <summary>Provision a Tenant and map its hosts (ADR-0102).</summary>
    public static PermissionDefinition TenantWrite { get; } =
        new(PermissionScope.Platform, "platform.tenant.write");

    /// <summary>Initialize Role templates, create the first administrator and recover an administrator (ADR-0102).</summary>
    public static PermissionDefinition TenantBootstrapWrite { get; } =
        new(PermissionScope.Platform, "platform.tenant_bootstrap.write");

    /// <summary>Rebind the identity authority's issuer through the offline tool (ADR-0104).</summary>
    public static PermissionDefinition IdentityAuthorityAdmin { get; } =
        new(PermissionScope.Platform, "platform.identity_authority.admin");

    /// <summary>Every registered Platform permission.</summary>
    public static IReadOnlyList<PermissionDefinition> All { get; } =
        [KillswitchWrite, TenantWrite, TenantBootstrapWrite, IdentityAuthorityAdmin];

    /// <summary>Finds a registered Platform permission by its key.</summary>
    public static bool TryGet(string? key, out PermissionDefinition definition)
    {
        definition = All.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.Ordinal))!;
        return definition is not null;
    }
}
