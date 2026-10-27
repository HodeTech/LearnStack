using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Domain;

public sealed record BuiltInRoleTemplate(BuiltInRoleKey BuiltInKey, PermissionScope Scope,
    ImmutableArray<PermissionDefinition> Grants);

/// <summary>The retained, finite v1 registry, template grants and administrator minimum.</summary>
public sealed class IdentityFoundationManifest
{
    public const string MembershipAdmin = "identity.membership.admin";
    public const string RoleAdmin = "identity.role.admin";
    public const string ProfileWrite = "identity.membership_profile.write";

    public static IdentityFoundationManifest Version1 { get; } = new();

    private IdentityFoundationManifest()
    {
        var memberTenant = new PermissionDefinition(PermissionScope.Tenant, MembershipAdmin);
        var memberOrganization = new PermissionDefinition(PermissionScope.Organization, MembershipAdmin);
        var roleTenant = new PermissionDefinition(PermissionScope.Tenant, RoleAdmin);
        var profileTenant = new PermissionDefinition(PermissionScope.Tenant, ProfileWrite);
        var profileOrganization = new PermissionDefinition(PermissionScope.Organization, ProfileWrite);
        Definitions = [memberTenant, memberOrganization, roleTenant, profileTenant, profileOrganization];
        Templates =
        [
            new(BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant, [memberTenant, roleTenant, profileTenant]),
            new(BuiltInRoleKey.Instructor, PermissionScope.Tenant, []),
            new(BuiltInRoleKey.Learner, PermissionScope.Tenant, [profileTenant]),
            new(BuiltInRoleKey.Guardian, PermissionScope.Tenant, []),
            new(BuiltInRoleKey.Editor, PermissionScope.Tenant, []),
            new(BuiltInRoleKey.OrgAdmin, PermissionScope.Organization, [memberOrganization, profileOrganization]),
            new(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization, []),
            new(BuiltInRoleKey.Learner, PermissionScope.Organization, [profileOrganization]),
        ];
        TenantAdminKernel = [memberTenant, roleTenant];
        Fingerprint = Hash(["learnstack.identity.foundation.manifest.v1", .. Definitions.Select(DefinitionToken),
            .. Templates.Select(template => $"{template.BuiltInKey}:{template.Scope}:{string.Join(',', template.Grants.Select(DefinitionToken))}"),
            .. TenantAdminKernel.Select(DefinitionToken)]);
        TenantAdminKernelSqlValues = string.Join(", ", TenantAdminKernel.Select(definition =>
            $"('{definition.Scope}', '{definition.Key}')"));
    }

    public int Version { get; } = 1;
    public string Fingerprint { get; }
    public ImmutableArray<PermissionDefinition> Definitions { get; }
    public ImmutableArray<BuiltInRoleTemplate> Templates { get; }
    public ImmutableArray<PermissionDefinition> TenantAdminKernel { get; }
    // Generated from the same immutable values used by registry and seed verification.
    public string TenantAdminKernelSqlValues { get; }

    public string RolePairsSqlValues => string.Join(", ", Templates.Select(template =>
        $"('{template.BuiltInKey}', '{template.Scope}')"));
    public string RegistrySqlValues => string.Join(", ", Definitions.Select(definition =>
        $"('{definition.Scope}', '{definition.Key}')"));
    public string GrantSqlValues => string.Join(", ", Templates.SelectMany(template => template.Grants.Select(definition =>
        $"('{template.BuiltInKey}', '{template.Scope}', '{definition.Key}')")));
    public string KernelSqlValues => TenantAdminKernelSqlValues;

    public BuiltInRoleTemplate? FindTemplate(BuiltInRoleKey key, PermissionScope scope) =>
        Templates.SingleOrDefault(template => template.BuiltInKey == key && template.Scope == scope);

    public bool IsActive(PermissionScope scope, string key) =>
        Definitions.Any(definition => definition.IsActive && definition.Scope == scope && definition.Key == key);

    public string RoleFingerprint(TenantId tenantId, Guid roleId, BuiltInRoleKey key, PermissionScope scope) =>
        Hash([Fingerprint, "role", Id(tenantId.Value), Id(roleId), key.ToString(), scope.ToString()]);

    public string MembershipFingerprint(TenantId tenantId, OrganizationId? organizationId, Guid membershipId,
        UserId userId, Guid roleId) => Hash([Fingerprint, "membership", Id(tenantId.Value),
            organizationId is { } organization ? Id(organization.Value) : "", Id(membershipId), Id(userId.Value), Id(roleId)]);

    public string ProfileFingerprint(TenantId tenantId, OrganizationId? organizationId, Guid membershipId,
        string? displayName, string? preferredLocale) => Hash([Fingerprint, "profile", Id(tenantId.Value),
            organizationId is { } organization ? Id(organization.Value) : "", Id(membershipId),
            displayName is null ? "null" : "value:" + displayName,
            preferredLocale is null ? "null" : "value:" + preferredLocale]);

    private static string DefinitionToken(PermissionDefinition definition) =>
        $"{definition.Scope}:{definition.Key}:{definition.IsActive}";

    private static string Id(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string Hash(IEnumerable<string> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
