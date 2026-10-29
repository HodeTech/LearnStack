using LearnStack.Application.Authorization;
using LearnStack.Application.Jobs;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Education.Application.Authorization;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.SharedKernel.Permissions;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Authorization;

public sealed class OperationPolicyCatalogTests
{
    [Fact]
    public void Real_sources_cover_every_actual_request_once_without_implicit_grants()
    {
        var catalog = ProductionCatalog();
        var assemblies = new[]
        {
            typeof(ReadBackgroundJobEnvelopeQuery).Assembly,
            typeof(LearnStack.Modules.Tenancy.Application.Contracts.Account.GetAccountSiteQuery).Assembly,
            typeof(LearnStack.Modules.Customization.Application.Contracts.Customization.InitializeTenantContentTypeDefaultCommand).Assembly,
            typeof(LearnStack.Modules.Education.Application.Contracts.PublicReads.GetPublicCoursesQuery).Assembly,
            typeof(ListMembershipsQuery).Assembly,
        };
        var actual = assemblies.SelectMany(assembly => assembly.GetTypes()).Where(type => !type.IsAbstract
            && type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IRequest<>)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        Assert.Equal(actual, catalog.All.Select(policy => policy.RequestType));
        Assert.Equal(57, actual.Length);
        Assert.Equal(4, catalog.All.Count(policy => policy.Kind == OperationPolicyKind.PublicRead));
        Assert.Single(catalog.All, policy => policy.Kind == OperationPolicyKind.AccountRead);
        Assert.Equal(8, catalog.All.Count(policy => policy.Kind == OperationPolicyKind.Protected));
        var ownProfile = Assert.Single(catalog.All, policy => policy.Kind == OperationPolicyKind.OwnMembershipRead);
        Assert.Equal(typeof(GetOwnMembershipProfileQuery), ownProfile.RequestType);
        Assert.Null(ownProfile.RequiredPermission);
        Assert.Empty(ownProfile.Alternatives);
        Assert.Equal(OperationReadScope.Scoped, ownProfile.ReadScope);
        Assert.Equal(42, catalog.All.Count(policy => policy.Kind == OperationPolicyKind.FiniteTrusted));
        var admission = Assert.Single(catalog.All, policy => policy.Kind == OperationPolicyKind.NativeSessionAdmission);
        Assert.Equal(typeof(AdmitNativeSessionScopeCommand), admission.RequestType);
        Assert.Null(admission.RequiredPermission);
        Assert.Equal(OperationReadScope.Scoped, admission.ReadScope);
        Assert.Equal(8, catalog.Definitions.Count(definition => definition.Assignable));
        Assert.Equal(3, catalog.Definitions.Count(definition => !definition.Assignable));
        Assert.DoesNotContain(catalog.Definitions, definition => definition.Definition.Scope == PermissionScope.Platform);
        foreach (var policy in catalog.All)
        {
            Assert.True(catalog.TryGet(policy.RequestType, out var frozen));
            Assert.Same(policy, frozen);
        }
        Assert.Equal(2, catalog.All.Count(policy => policy.TrustedEventHandlerType is not null));
    }

    [Theory]
    [InlineData("duplicate-request")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-definition")]
    [InlineData("unknown-definition")]
    [InlineData("public-assignable")]
    [InlineData("protected-virtual")]
    [InlineData("platform")]
    public void Conflicting_or_unregistered_metadata_never_freezes_as_authority(string variant)
    {
        var pair = new PermissionDefinition(PermissionScope.Tenant, "identity.example.read");
        var registration = new PermissionRegistration(pair, Assignable: true);
        var source = new Source([registration], [OperationPolicy.ForProtected<Example>("identity.example.one", pair)]);
        switch (variant)
        {
            case "duplicate-request": source.Policies = [.. source.Policies, OperationPolicy.ForProtected<Example>("identity.example.two", pair)]; break;
            case "duplicate-id": source.Policies = [.. source.Policies, OperationPolicy.ForProtected<Other>("identity.example.one", pair)]; break;
            case "duplicate-definition": source.Definitions = [registration, registration]; break;
            case "unknown-definition": source.Definitions = []; break;
            case "public-assignable": source.Policies = [OperationPolicy.ForPublic<Example>("identity.example.public", pair)]; break;
            case "protected-virtual": source.Definitions = [registration with { Assignable = false }]; break;
            case "platform": source.Definitions = [new(new(PermissionScope.Platform, "platform.example.read"), true)]; break;
        }
        Assert.Throws<InvalidOperationException>(() => new OperationPolicyCatalog([source]));
    }

    [Fact]
    public void Inactive_definitions_remain_registered_without_acquiring_grants()
    {
        var pair = new PermissionDefinition(PermissionScope.Tenant, "identity.example.read", isActive: false);
        var policy = OperationPolicy.ForProtected<Example>("identity.example.read", pair);
        var catalog = new OperationPolicyCatalog([new Source([new(pair, true)], [policy])]);
        Assert.True(catalog.TryGetDefinition(pair.Scope, pair.Key, out var definition));
        Assert.False(definition.Definition.IsActive);
        Assert.Same(policy, Assert.Single(catalog.All));
    }

    private static OperationPolicyCatalog ProductionCatalog() => new([
        new TenancyOperationPolicySource(), new CustomizationOperationPolicySource(),
        new EducationOperationPolicySource(), new IdentityOperationPolicySource(), new BackgroundJobOperationPolicySource()]);

    private sealed record Example : IRequest<LearnStack.SharedKernel.Results.Result<LearnStack.SharedKernel.Results.None>>;
    private sealed record Other : IRequest<LearnStack.SharedKernel.Results.Result<LearnStack.SharedKernel.Results.None>>;
    private sealed class Source(IEnumerable<PermissionRegistration> definitions, IEnumerable<OperationPolicy> policies) : IOperationPolicySource
    {
        public IEnumerable<PermissionRegistration> Definitions { get; set; } = definitions;
        public IEnumerable<OperationPolicy> Policies { get; set; } = policies;
    }
}
