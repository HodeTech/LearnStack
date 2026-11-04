namespace LearnStack.Api.Common;

/// <summary>Response privacy for the finite administration routes, including routing and authorization failures.</summary>
internal static class AdministrationHttpSurface
{
    internal static bool IsPath(PathString path)
    {
        var parts = (path.Value ?? string.Empty).TrimEnd('/').Split('/');
        if (parts.Length < 5 || !parts[1].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("v1", StringComparison.OrdinalIgnoreCase)) return false;
        var module = parts[3].ToLowerInvariant();
        var resource = parts[4].ToLowerInvariant();
        return (module, resource, parts.Length) switch
        {
            ("identity", "organization-memberships" or "permissions" or "roles", 5) => true,
            ("identity", "memberships" or "roles", 6) => true,
            ("identity", "memberships", 7) => parts[6].ToLowerInvariant() is "profile" or "role-assignments" or "status",
            ("identity", "roles", 7) => parts[6].ToLowerInvariant() is "grants" or "manifest-adoptions",
            ("identity", "me", 6) => parts[5].ToLowerInvariant() is "profile" or "contexts" or "capabilities",
            ("tenancy", "organizations" or "default-organization", 5) => true,
            ("tenancy", "organizations", 6) => true,
            ("tenancy", "organizations", 7) => parts[6].Equals("archive", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
