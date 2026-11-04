using LearnStack.Application.Authorization;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Authorization;

public sealed class OperationPolicyCombinationTests
{
    private static readonly PermissionDefinition TenantPair = new(PermissionScope.Tenant, "identity.membership.admin");
    private static readonly PermissionDefinition OrganizationPair = new(PermissionScope.Organization, TenantPair.Key);
    private static readonly PermissionDefinition ProfilePair = new(PermissionScope.Tenant, "identity.membership_profile.write");

    [Fact]
    public void Source_arrays_are_snapshotted_and_single_permission_compatibility_never_reports_a_combination_maximum()
    {
        var permissions = new[] { TenantPair };
        var wide = new OperationPolicyClause(permissions, OperationReadScope.Tenant);
        permissions[0] = ProfilePair;
        var clauses = new[] { wide, new OperationPolicyClause([OrganizationPair]) };
        var policy = OperationPolicy.ForProtected<Operation>("identity.test.combination", clauses);
        clauses[0] = new([ProfilePair]);
        Assert.Same(TenantPair, Assert.Single(wide.RequiredPermissions));
        Assert.Same(wide, policy.Alternatives[0]);
        Assert.Null(policy.RequiredPermission);
        Assert.Equal(OperationReadScope.Scoped, policy.ReadScope);
        var legacy = OperationPolicy.ForProtected<Operation>("identity.test.single", TenantPair, OperationReadScope.Tenant);
        Assert.Same(TenantPair, legacy.RequiredPermission);
        Assert.Equal(OperationReadScope.Tenant, legacy.ReadScope);
        Assert.Equal(OperationReadScope.Tenant, Assert.Single(legacy.Alternatives).ReadScope);
        Assert.Equal(nameof(OperationPolicyClause), wide.ToString());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("platform")]
    [InlineData("organization-wide")]
    [InlineData("too-many")]
    public void A_clause_accepts_only_a_finite_nonempty_conjunction_of_distinct_tenant_or_organization_pairs(string variant)
    {
        PermissionDefinition[] permissions = variant switch
        {
            "empty" => [],
            "null" => [null!],
            "duplicate" => [TenantPair, new(TenantPair.Scope, TenantPair.Key, isActive: false)],
            "platform" => [new(PermissionScope.Platform, "platform.tenant.admin")],
            "organization-wide" => [OrganizationPair],
            _ => Enumerable.Range(0, 9).Select(index => new PermissionDefinition(PermissionScope.Tenant, $"identity.item{index}.read")).ToArray(),
        };
        Assert.Throws<ArgumentException>(() => new OperationPolicyClause(permissions,
            variant == "organization-wide" ? OperationReadScope.Tenant : OperationReadScope.Scoped));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("duplicate-order")]
    [InlineData("too-many")]
    [InlineData("wide-write")]
    [InlineData("contradictory-scope")]
    public void Policy_alternatives_are_finite_unambiguous_and_never_widen_orgless_writes(string variant)
    {
        var clause = new OperationPolicyClause([TenantPair]);
        OperationPolicyClause[] clauses = variant switch
        {
            "empty" => [],
            "null" => [null!],
            "duplicate" => [clause, new([TenantPair], OperationReadScope.Tenant)],
            "duplicate-order" => [new([TenantPair, ProfilePair]), new([ProfilePair, TenantPair])],
            "wide-write" => [new([TenantPair], OperationReadScope.Tenant), new([OrganizationPair])],
            "too-many" => Enumerable.Range(0, 9).Select(index => new OperationPolicyClause(
                [new(PermissionScope.Tenant, $"identity.item{index}.read")])).ToArray(),
            _ => [clause],
        };
        Assert.Throws<ArgumentException>(() => OperationPolicy.ForProtected<Operation>("identity.test.combination", clauses,
            requiresOrglessWrite: variant is "wide-write" or "contradictory-scope", requiresOrganization: variant == "contradictory-scope"));
    }

    [Theory]
    [InlineData("missing-alternative")]
    [InlineData("missing-conjunct")]
    [InlineData("virtual")]
    [InlineData("mismatched-active-state")]
    public void Catalog_validates_every_scoped_pair_in_every_alternative(string variant)
    {
        var policy = OperationPolicy.ForProtected<Operation>("identity.test.combination",
            [new([TenantPair, ProfilePair]), new([OrganizationPair])]);
        var definitions = new List<PermissionRegistration> { new(TenantPair, true), new(ProfilePair, true), new(OrganizationPair, true) };
        switch (variant)
        {
            case "missing-alternative": definitions.RemoveAt(2); break;
            case "missing-conjunct": definitions.RemoveAt(1); break;
            case "virtual": definitions[2] = new(OrganizationPair, false); break;
            case "mismatched-active-state": definitions[2] = new(new(OrganizationPair.Scope, OrganizationPair.Key, isActive: false), true); break;
        }
        Assert.Throws<InvalidOperationException>(() => new OperationPolicyCatalog([new Source(definitions, [policy])]));
    }

    [Fact]
    public void Production_sources_add_only_the_exact_three_assignable_Tenancy_pairs_and_preserve_v1_and_virtual_public_definitions()
    {
        var tenancy = new TenancyOperationPolicySource();
        var catalog = new OperationPolicyCatalog([tenancy, new IdentityOperationPolicySource(),
            new CustomizationOperationPolicySource(), new EducationOperationPolicySource()]);
        Assert.Equal(IdentityFoundationManifest.Version1.Definitions.OrderBy(pair => pair.Scope).ThenBy(pair => pair.Key, StringComparer.Ordinal),
            catalog.Definitions.Where(registration => registration.Assignable && registration.Definition.Key.StartsWith("identity.", StringComparison.Ordinal))
                .Select(registration => registration.Definition));
        Assert.Equal(BoundedTenancyAdministrationPermissions.Definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            tenancy.Definitions.Where(registration => registration.Assignable).Select(registration => registration.Definition).OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(8, catalog.Definitions.Count(registration => registration.Assignable));
        Assert.Equal(3, catalog.Definitions.Count(registration => !registration.Assignable));
        Assert.All(BoundedTenancyAdministrationPermissions.Definitions, pair => Assert.Equal(PermissionScope.Tenant, pair.Scope));
        Assert.Equal(["tenancy.locale.write", "tenancy.organization.admin", "tenancy.setting.write"],
            BoundedTenancyAdministrationPermissions.Definitions.Select(pair => pair.Key).Order());
        var human = tenancy.Policies.Where(policy => policy.Kind == OperationPolicyKind.Protected).ToArray();
        Assert.Equal(13, human.Length);
        Assert.All(human, policy =>
        {
            Assert.Equal(OperationReadScope.Scoped, policy.ReadScope);
            Assert.False(policy.RequiresOrganization);
            Assert.Single(policy.Alternatives);
            var pair = Assert.Single(policy.Alternatives[0].RequiredPermissions);
            Assert.Contains(pair, BoundedTenancyAdministrationPermissions.Definitions);
            Assert.Equal(PermissionScope.Tenant, pair.Scope);
        });
        Assert.Equal(9, human.Count(policy => policy.RequiresOrglessWrite));
    }

    [Fact]
    public void Existing_public_account_native_and_trusted_kinds_have_no_grant_clause_or_read_widening()
    {
        var publicPair = TenancyOperationPolicySource.PublicSite;
        var policies = new[]
        {
            OperationPolicy.ForPublic<Operation>("identity.test.public", publicPair),
            OperationPolicy.ForAccount<Operation>("identity.test.account"),
            OperationPolicy.ForNativeSessionAdmission<Operation>("identity.test.native"),
            OperationPolicy.ForTrusted<Operation>("identity.test.trusted", AuthorizationSourceKind.Seed),
        };
        foreach (var policy in policies)
        {
            Assert.Empty(policy.Alternatives);
            Assert.Equal(OperationReadScope.Scoped, policy.ReadScope);
            Assert.False(policy.RequiresOrglessWrite);
            Assert.False(policy.RequiresOrganization);
        }
        Assert.Same(publicPair, policies[0].RequiredPermission);
        Assert.All(policies.Skip(1), policy => Assert.Null(policy.RequiredPermission));
        Assert.Equal(AuthorizationSourceKind.Seed, Assert.Single(policies[3].AllowedSources));
    }

    private sealed record Operation;
    private sealed class Source(IEnumerable<PermissionRegistration> definitions, IEnumerable<OperationPolicy> policies) : IOperationPolicySource
    {
        public IEnumerable<PermissionRegistration> Definitions => definitions;
        public IEnumerable<OperationPolicy> Policies => policies;
    }
}
