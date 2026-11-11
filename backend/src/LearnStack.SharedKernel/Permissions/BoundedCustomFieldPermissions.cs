namespace LearnStack.SharedKernel.Permissions;

/// <summary>The single registry-v3 pair shared by its owning module and Role adoption.</summary>
public static class BoundedCustomFieldPermissions
{
    public static PermissionDefinition Definition { get; } =
        new(PermissionScope.Tenant, "customization.custom_field.admin");
}
