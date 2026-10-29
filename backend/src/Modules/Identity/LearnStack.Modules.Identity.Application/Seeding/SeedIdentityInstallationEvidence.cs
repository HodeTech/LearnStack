using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Application.Seeding;

/// <summary>Pure read disposition. Valid installed history grants no authority to resume a write.</summary>
public static class SeedIdentityInstallationEvidence
{
    public static bool RoleIsInstalled(RoleSeedDto row, Guid id, TenantId tenant, BuiltInRoleKey key, PermissionScope scope)
    {
        ArgumentNullException.ThrowIfNull(row);
        var release = IdentityRoleRelease.Version2;
        return row.Id == id && row.TenantId == tenant && row.BuiltInKey == key && row.PermissionScope == scope
            && row.GraphIsValid && ValidStamps(row.Evidence, row.Version, row.DeletedAt, row.DeletedBy)
            && (row.AdoptedReleaseVersion == 1 && row.AdoptedReleaseFingerprint == IdentityFoundationManifest.Version1.Fingerprint
                || key == BuiltInRoleKey.TenantAdmin && scope == PermissionScope.Tenant && row.Version > 0
                    && row.AdoptedReleaseVersion == release.Version && row.AdoptedReleaseFingerprint == release.Fingerprint)
            && (row.Version > 0 || RoleIsPristine(row));
    }

    public static bool RoleIsPristine(RoleSeedDto row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var template = IdentityFoundationManifest.Version1.FindTemplate(row.BuiltInKey, row.PermissionScope);
        return row.Version == 0 && row.DeletedAt is null && row.DeletedBy is null
            && ValidStamps(row.Evidence, row.Version, row.DeletedAt, row.DeletedBy)
            && row.AdoptedReleaseVersion == 1 && row.AdoptedReleaseFingerprint == IdentityFoundationManifest.Version1.Fingerprint
            && template is not null && row.Grants.Length == template.Grants.Length
            && row.Grants.ToHashSet().SetEquals(template.Grants);
    }

    public static bool MembershipIsInstalled(MembershipSeedDto row, Guid id, TenantId tenant,
        OrganizationId? organization, UserId user)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Id == id && row.TenantId == tenant && row.OrganizationId == organization && row.UserId == user
            && row.GraphIsValid && MembershipProfile.IsValid(row.DisplayName, row.PreferredLocale)
            && row.RoleIds.All(roleId => roleId != Guid.Empty && roleId.Version == 7)
            && row.RoleIds.Distinct().Count() == row.RoleIds.Length
            && ValidStamps(row.Evidence, row.Version, row.DeletedAt, row.DeletedBy)
            && (row.Status is "Active" or "Suspended" && row.DeletedAt is null && row.DeletedBy is null
                || row.Status == "Revoked" && row.DeletedAt is not null && row.DeletedBy is not null)
            && (row.Version > 0 || row.Status == "Active" && row.DisplayName is null && row.PreferredLocale is null);
    }

    public static bool MembershipIsPristine(MembershipSeedDto row, Guid originalRoleId, string displayName, string preferredLocale)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Status == "Active" && row.DeletedAt is null && row.DeletedBy is null
            && row.RoleIds.Length == 1 && row.RoleIds[0] == originalRoleId
            && ValidStamps(row.Evidence, row.Version, row.DeletedAt, row.DeletedBy)
            && (row.Version == 0 && row.DisplayName is null && row.PreferredLocale is null
                || row.Version == 1 && row.Evidence!.UpdatedBy == UserId.SystemActor
                    && row.DisplayName == displayName && row.PreferredLocale == preferredLocale);
    }

    private static bool ValidStamps(SeedRootEvidence? evidence, long version, DateTimeOffset? deletedAt, UserId? deletedBy) =>
        evidence is { CreatedAt: var created, CreatedBy: var actor } && created != default && actor == UserId.SystemActor
        && version >= 0 && (version == 0 ? evidence.UpdatedAt is null && evidence.UpdatedBy is null
            : evidence.UpdatedAt is { } updated && updated != default && evidence.UpdatedBy is { } editor
                && editor.IsInitialized() && editor.Value != Guid.Empty)
        && (deletedAt is null ? deletedBy is null : version > 0 && deletedAt is { } deleted && deleted != default
            && deletedBy is { } deleting && deleting.IsInitialized() && deleting.Value != Guid.Empty
            && evidence.UpdatedAt == deletedAt && evidence.UpdatedBy == deletedBy);
}
