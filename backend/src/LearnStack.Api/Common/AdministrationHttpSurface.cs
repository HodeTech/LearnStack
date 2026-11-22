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
            ("identity", "federation", 5) => true,
            ("identity", "federation", 6) => parts[5].ToLowerInvariant() is "configuration" or "secret-rotations" or "display-name"
                or "test-starts" or "test-results" or "activation" or "disablement" or "enablement" or "deletion" or "resumption",
            ("identity", "invitations" or "invitable-roles", 5) => true,
            ("identity", "invitations", 6) => true,
            ("identity", "invitation-entry", 6) => parts[5].ToLowerInvariant() is "inspections" or "acceptances" or "results",
            // ADR-0106 § 9: the retained own-scope native entry of the subject's data requests.
            ("identity", "data-request-entry", 6) => parts[5].ToLowerInvariant() is "scopes" or "requests" or "exports"
                or "erasures" or "cancellations" or "revocations" or "tickets" or "chunks",
            ("identity", "invitations", 7) => parts[6].ToLowerInvariant() is "revocations" or "reissues",
            ("tenancy", "settings", 6) => parts[5].Equals("invitation-policy", StringComparison.OrdinalIgnoreCase),
            ("identity", "organization-memberships" or "permissions" or "roles", 5) => true,
            ("identity", "memberships" or "roles", 6) => true,
            ("identity", "memberships", 7) => parts[6].ToLowerInvariant() is "profile" or "role-assignments" or "status" or "custom-fields",
            ("identity", "roles", 7) => parts[6].ToLowerInvariant() is "grants" or "manifest-adoptions",
            ("identity", "me", 6) => parts[5].ToLowerInvariant() is "profile" or "contexts" or "capabilities" or "custom-fields" or "data-requests",
            // ADR-0106, ADR-0107 § 4: the subject's own data requests, their artifact and its ticketed download.
            ("identity", "me", 7) => parts[5].Equals("data-requests", StringComparison.OrdinalIgnoreCase),
            ("identity", "me", 8) => parts[5].Equals("data-requests", StringComparison.OrdinalIgnoreCase)
                && parts[7].Equals("cancellations", StringComparison.OrdinalIgnoreCase),
            ("identity", "me", 9) => parts[5].Equals("data-requests", StringComparison.OrdinalIgnoreCase)
                && parts[7].Equals("artifact", StringComparison.OrdinalIgnoreCase)
                && parts[8].ToLowerInvariant() is "revocations" or "tickets" or "chunks",
            // The administrator's view of the group's data requests: status and counts, never content.
            ("identity", "data-requests", 5 or 6) => true,
            ("identity", "data-requests", 7) => parts[6].Equals("cancellations", StringComparison.OrdinalIgnoreCase),
            ("identity", "data-requests", 8) => parts[6].Equals("artifact", StringComparison.OrdinalIgnoreCase)
                && parts[7].Equals("revocations", StringComparison.OrdinalIgnoreCase),
            ("tenancy", "organizations" or "default-organization", 5) => true,
            ("tenancy", "organizations", 6) => true,
            ("tenancy", "organizations", 7) => parts[6].Equals("archive", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
