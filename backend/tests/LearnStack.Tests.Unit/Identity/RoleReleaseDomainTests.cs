using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class RoleReleaseDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());

    [Fact]
    public void Current_registry_adds_only_the_three_Tenant_Tenancy_pairs_without_changing_initial_Roles()
    {
        var initial = IdentityFoundationManifest.Version1;
        var release = IdentityRoleRelease.Version2;
        Assert.Equal(5, initial.Definitions.Length);
        Assert.Equal(8, initial.Templates.Length);
        Assert.Equal(2, initial.TenantAdminKernel.Length);
        Assert.Equal(8, release.Definitions.Length);
        Assert.Equal(BoundedTenancyAdministrationPermissions.Definitions, release.TenantAdminDelta);
        Assert.Equal(initial.Definitions.Concat(release.TenantAdminDelta), release.Definitions);
        Assert.NotEqual(initial.Fingerprint, release.Fingerprint);
        foreach (var template in initial.Templates)
        {
            var role = Create(template.BuiltInKey, template.Scope);
            Assert.True(role.MatchesInitialTemplate());
            Assert.Equal(1, role.AdoptedReleaseVersion);
            Assert.Equal(initial.Fingerprint, role.AdoptedReleaseFingerprint);
            Assert.DoesNotContain(role.Grants, grant => grant.PermissionKey.StartsWith("tenancy.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void First_adoption_unions_only_new_keys_and_preserves_removed_profile_and_kernel_instances()
    {
        var role = Create();
        var initial = IdentityFoundationManifest.Version1;
        Assert.True(role.ReplaceGrants(initial.TenantAdminKernel, At, Actor).IsSuccess);
        var retained = role.Grants.ToArray();
        var release = IdentityRoleRelease.Version2;
        Assert.True(role.AdoptRelease(release.Version, release.Fingerprint, At.AddSeconds(1), Actor).IsSuccess);
        Assert.Equal(2, role.Version);
        Assert.Equal(2, role.AdoptedReleaseVersion);
        Assert.Equal(release.Fingerprint, role.AdoptedReleaseFingerprint);
        Assert.False(role.MatchesInitialTemplate());
        Assert.DoesNotContain(role.Grants, grant => grant.PermissionKey == IdentityFoundationManifest.ProfileWrite);
        Assert.Equal(initial.TenantAdminKernel.Concat(release.TenantAdminDelta).OrderBy(pair => pair.Key),
            role.Grants.Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).OrderBy(pair => pair.Key));
        Assert.All(retained, grant => Assert.Contains(role.Grants, current => ReferenceEquals(grant, current)));
    }

    [Fact]
    public void Removing_an_adopted_key_retains_the_release_and_repeat_adoption_cannot_restore_it()
    {
        var role = Create();
        var release = IdentityRoleRelease.Version2;
        Assert.True(role.AdoptRelease(release.Version, release.Fingerprint, At, Actor).IsSuccess);
        var removed = release.TenantAdminDelta[0];
        var remaining = role.Grants.Where(grant => grant.PermissionKey != removed.Key)
            .Select(grant => new PermissionDefinition(grant.PermissionScope, grant.PermissionKey)).ToArray();
        Assert.True(role.ReplaceGrants(remaining, At.AddSeconds(1), Actor).IsSuccess);
        Assert.True(role.AdoptRelease(release.Version, release.Fingerprint, At.AddSeconds(2), Actor).IsFailure);
        Assert.Equal(2, role.Version);
        Assert.Equal(2, role.AdoptedReleaseVersion);
        Assert.Equal(release.Fingerprint, role.AdoptedReleaseFingerprint);
        Assert.DoesNotContain(role.Grants, grant => grant.PermissionKey == removed.Key);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Historical_unknown_or_mismatched_release_cannot_advance_or_change_grants(int version, bool exactFingerprint)
    {
        var role = Create();
        var before = role.Grants.ToArray();
        var fingerprint = exactFingerprint ? IdentityRoleRelease.Version2.Fingerprint : IdentityFoundationManifest.Version1.Fingerprint;
        Assert.True(role.AdoptRelease(version, fingerprint, At, Actor).IsFailure);
        Assert.Equal(0, role.Version);
        Assert.Equal(1, role.AdoptedReleaseVersion);
        Assert.Equal(before, role.Grants);
    }

    [Fact]
    public void First_adoption_never_expands_another_finite_template()
    {
        var release = IdentityRoleRelease.Version2;
        foreach (var template in IdentityFoundationManifest.Version1.Templates.Where(template => template.BuiltInKey != BuiltInRoleKey.TenantAdmin))
        {
            var role = Create(template.BuiltInKey, template.Scope);
            Assert.True(role.AdoptRelease(release.Version, release.Fingerprint, At, Actor).IsFailure);
            Assert.Equal(0, role.Version);
            Assert.True(role.MatchesInitialTemplate());
        }
    }

    private static Role Create(BuiltInRoleKey key = BuiltInRoleKey.TenantAdmin, PermissionScope scope = PermissionScope.Tenant) =>
        Role.Create(RoleId.From(Guid.CreateVersion7()), TenantId.From(Guid.CreateVersion7()), key, scope, At, Actor).Value!;
}
