using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>The exact current assignable registry and the sole admitted first-adoption delta.</summary>
public sealed class IdentityRoleRelease
{
    public static IdentityRoleRelease Version2 { get; } = new();

    private IdentityRoleRelease()
    {
        TenantAdminDelta = BoundedTenancyAdministrationPermissions.Definitions;
        Definitions = [.. IdentityFoundationManifest.Version1.Definitions, .. TenantAdminDelta];
        Fingerprint = Hash(["learnstack.identity.role.release.v2", IdentityFoundationManifest.Version1.Fingerprint,
            "TenantAdmin:Tenant", .. TenantAdminDelta.Select(definition => $"{definition.Scope}:{definition.Key}:{definition.IsActive}")]);
    }

    public int Version { get; } = 2;
    public string Fingerprint { get; }
    public ImmutableArray<PermissionDefinition> Definitions { get; }
    public ImmutableArray<PermissionDefinition> TenantAdminDelta { get; }
    public string RegistrySqlValues => string.Join(", ", Definitions.Select(definition =>
        $"('{definition.Scope}', '{definition.Key}')"));
    public string TenantAdminDeltaSqlValues => string.Join(", ", TenantAdminDelta.Select(definition =>
        $"('{definition.Scope}', '{definition.Key}')"));

    public bool IsActive(PermissionScope scope, string key) =>
        Definitions.Any(definition => definition.IsActive && definition.Scope == scope && definition.Key == key);

    public bool IsExact(int version, string fingerprint) => version == Version && fingerprint == Fingerprint;

    public bool IsValidAdoptionState(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.AdoptedReleaseVersion == IdentityFoundationManifest.Version1.Version
                && role.AdoptedReleaseFingerprint == IdentityFoundationManifest.Version1.Fingerprint
            || role.BuiltInKey == BuiltInRoleKey.TenantAdmin && role.PermissionScope == PermissionScope.Tenant
                && IsExact(role.AdoptedReleaseVersion, role.AdoptedReleaseFingerprint);
    }

    public bool CanFirstAdopt(Role role, int version, string fingerprint) => IsExact(version, fingerprint)
        && role.IsTenantAdministrator && role.AdoptedReleaseVersion == IdentityFoundationManifest.Version1.Version
        && role.AdoptedReleaseFingerprint == IdentityFoundationManifest.Version1.Fingerprint;

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
