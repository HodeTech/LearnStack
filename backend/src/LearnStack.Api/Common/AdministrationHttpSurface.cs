namespace LearnStack.Api.Common;

/// <summary>Response privacy for the finite administration and invitation-entry routes, including routing and authorization failures.</summary>
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
            ("customization", "custom-field-definitions", 5 or 6) => true,
            ("customization", "custom-field-definitions", 7) => parts[6].ToLowerInvariant() is "revisions" or "publication" or "retirement" or "discard",
            ("tenancy", "membership-field-consents", 6) => parts[5].ToLowerInvariant() is "own" or "grants" or "withdrawals",
            ("tenancy", "membership-field-consents", 7) => parts[5].Equals("own", StringComparison.OrdinalIgnoreCase)
                && parts[6].ToLowerInvariant() is "scopes" or "history",
            ("identity", "invitations" or "invitable-roles", 5) => true,
            ("identity", "invitations", 6) => true,
            ("identity", "invitation-entry", 6) => parts[5].ToLowerInvariant() is "inspections" or "acceptances" or "results",
            ("identity", "invitations", 7) => parts[6].ToLowerInvariant() is "revocations" or "reissues",
            ("tenancy", "settings", 6) => parts[5].Equals("invitation-policy", StringComparison.OrdinalIgnoreCase),
            ("identity", "organization-memberships" or "permissions" or "roles", 5) => true,
            ("identity", "memberships" or "roles", 6) => true,
            ("identity", "memberships", 7) => parts[6].ToLowerInvariant() is "profile" or "role-assignments" or "status" or "custom-fields",
            ("identity", "roles", 7) => parts[6].ToLowerInvariant() is "grants" or "manifest-adoptions",
            ("identity", "me", 6) => parts[5].ToLowerInvariant() is "profile" or "contexts" or "capabilities" or "custom-fields",
            ("tenancy", "organizations" or "default-organization", 5) => true,
            ("tenancy", "organizations", 6) => true,
            ("tenancy", "organizations", 7) => parts[6].Equals("archive", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
