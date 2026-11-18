namespace LearnStack.SharedKernel.Permissions;

/// <summary>The single registry-v4 pair (ADR-0099); registration grants it to no Role, adoption is explicit.</summary>
public static class FederationPermissions
{
    public static PermissionDefinition Definition { get; } =
        new(PermissionScope.Tenant, "identity.federation.admin");
}
