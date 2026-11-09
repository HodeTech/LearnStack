using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Permissions;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>Immutable registry releases and exact consecutive adoption deltas.</summary>
public sealed class IdentityRoleRelease
{
    public static IdentityRoleRelease Version2 { get; } = new(2,
        IdentityFoundationManifest.Version1.Fingerprint, IdentityFoundationManifest.Version1.Definitions,
        BoundedTenancyAdministrationPermissions.Definitions);
    public static IdentityRoleRelease Version3 { get; } = new(3,
        Version2.Fingerprint, Version2.Definitions, [BoundedCustomFieldPermissions.Definition]);
    public static IdentityRoleRelease Current => Version3;

    private IdentityRoleRelease(int version, string predecessorFingerprint,
        ImmutableArray<PermissionDefinition> precedingDefinitions, ImmutableArray<PermissionDefinition> delta)
    {
        Version = version;
        PredecessorFingerprint = predecessorFingerprint;
        TenantAdminDelta = delta;
        Definitions = [.. precedingDefinitions, .. TenantAdminDelta];
        Fingerprint = Hash([$"learnstack.identity.role.release.v{version}", predecessorFingerprint,
            "TenantAdmin:Tenant", .. TenantAdminDelta.Select(definition => $"{definition.Scope}:{definition.Key}:{definition.IsActive}")]);
    }

    public int Version { get; }
    private string PredecessorFingerprint { get; }
    public string Fingerprint { get; }
    public ImmutableArray<PermissionDefinition> Definitions { get; }
    public ImmutableArray<PermissionDefinition> TenantAdminDelta { get; }
    public string RegistrySqlValues => string.Join(", ", Definitions.Select(definition =>
        $"('{definition.Scope}', '{definition.Key}')"));
    public string TenantAdminDeltaSqlValues => string.Join(", ", TenantAdminDelta.Select(definition =>
        $"('{definition.Scope}', '{definition.Key}')"));

    public bool IsActive(PermissionScope scope, string key) =>
        Definitions.Any(definition => definition.IsActive && definition.Scope == scope && definition.Key == key);

    public bool IsExact(int version, string? fingerprint) => version == Version && fingerprint == Fingerprint;

    public static IdentityRoleRelease? Find(int version) => version switch
    {
        2 => Version2,
        3 => Version3,
        _ => null,
    };

    public static IdentityRoleRelease? NextFor(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.AdoptedReleaseVersion switch
        {
            1 when role.AdoptedReleaseFingerprint == IdentityFoundationManifest.Version1.Fingerprint => Version2,
            2 when Version2.IsExact(role.AdoptedReleaseVersion, role.AdoptedReleaseFingerprint) => Version3,
            _ => null,
        };
    }

    public bool IsValidAdoptionState(Role role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return IsValidAdoptionState(role.BuiltInKey, role.PermissionScope, role.AdoptedReleaseVersion, role.AdoptedReleaseFingerprint);
    }

    public bool IsValidAdoptionState(BuiltInRoleKey key, PermissionScope scope, int version, string? fingerprint) =>
        version == IdentityFoundationManifest.Version1.Version && fingerprint == IdentityFoundationManifest.Version1.Fingerprint
        || key == BuiltInRoleKey.TenantAdmin && scope == PermissionScope.Tenant && version <= Version
            && Find(version) is { } release && release.IsExact(version, fingerprint);

    public bool CanFirstAdopt(Role role, int version, string fingerprint) => IsExact(version, fingerprint)
        && role.IsTenantAdministrator && role.AdoptedReleaseVersion == Version - 1
        && role.AdoptedReleaseFingerprint == PredecessorFingerprint;

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
