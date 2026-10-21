using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Tools.Seeder;

/// <summary>Projects development identity facts without credentials or persistence access.</summary>
public sealed record SeedIdentityManifest(IReadOnlyList<IdentityManifestUser> Users)
{
    public static SeedIdentityManifest Create()
    {
        var users = SeedData.IdentityUsers;
        if (users.Length != 4 || users.Select(user => user.UserId).Distinct().Count() != users.Length
            || users.Select(user => user.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() != users.Length
            || users.Any(user => !user.UserId.IsInitialized() || user.UserId.Value == Guid.Empty
                || user.UserId == UserId.SystemActor || !SeedData.All.Contains(user.Tenant)
                || user.AccountCapability != new SeedAccountCapability("account", "manage-account")))
        {
            throw new InvalidOperationException("Development identity declaration is inconsistent.");
        }

        return new(users.Select(user => new IdentityManifestUser(user.UserId.Value,
            user.Tenant.TenantId.Value,
            user.Tenant.MapHostToDefaultOrganization ? user.Tenant.DefaultOrganization.OrganizationId.Value : null,
            user.Tenant.Host, user.Username, user.Username, user.FirstName, user.LastName,
            user.RealmRole, user.AccountCapability, user.LegacyTenantAttribute)).ToArray());
    }
}

/// <summary>The credential-free wire projection consumed by development realm tooling.</summary>
public sealed record IdentityManifestUser(Guid UserId, Guid TenantId, Guid? OrganizationId,
    string Host, string Username, string Email, string FirstName, string LastName,
    string RealmRole, SeedAccountCapability AccountCapability, string? LegacyTenantAttribute);
