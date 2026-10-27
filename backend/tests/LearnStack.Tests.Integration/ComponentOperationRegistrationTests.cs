using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Bounds the explicit component authority; this supplies no production or HTTP permission proof.</summary>
public sealed class ComponentOperationRegistrationTests
{
    [Fact]
    public async Task Exact_component_authority_preserves_unlisted_policies_tenant_gates_and_scoped_provenance()
    {
        var original = new OperationPolicyCatalog([new OriginalPolicies()]);
        var services = new ServiceCollection();
        services.AddSingleton<IOperationPolicyCatalog>(original);
        var selected = OperationPolicy.ForTrusted<Selected>("tests.component.boundary.selected", AuthorizationSourceKind.Seed);
        OperationPolicy[] supplied = [selected];
        ComponentOperationRegistration.AddExactComponentAuthority(services, supplied);
        supplied[0] = OperationPolicy.ForTrusted<Unlisted>("tests.component.boundary.injected", AuthorizationSourceKind.Seed);
        await using var provider = services.BuildServiceProvider();
        var decorated = provider.GetRequiredService<IOperationPolicyCatalog>();
        decorated.TryGet(typeof(Selected), out var admitted).Should().BeTrue();
        admitted.Should().BeSameAs(selected, "registration freezes the explicitly supplied array");
        original.TryGet(typeof(Selected), out var untouchedSelected).Should().BeTrue();
        untouchedSelected.Kind.Should().Be(OperationPolicyKind.Protected, "the underlying catalogue is never changed");
        original.TryGet(typeof(Unlisted), out var untouchedOther).Should().BeTrue();
        decorated.TryGet(typeof(Unlisted), out var unlisted).Should().BeTrue();
        unlisted.Should().BeSameAs(untouchedOther);
        decorated.TryGet(typeof(Unknown), out _).Should().BeFalse();
        decorated.Definitions.Should().Equal(original.Definitions);
        decorated.All.Should().HaveCount(original.All.Count);
        decorated.TryGetDefinition(PermissionScope.Tenant, OriginalPolicies.Permission.Key, out var definition).Should().BeTrue();
        definition.Should().BeSameAs(original.Definitions.Single());

        var context = new MutableContext();
        TrustedOperationDescriptor captured;
        await using (var scope = provider.CreateAsyncScope())
        {
            var source = scope.ServiceProvider.GetRequiredService<ITrustedOperationSource>();
            source.Capture(typeof(Unlisted), UnresolvedTenantContext.Instance).Should().BeNull(
                "an unlisted request cannot bind the source before real context admission");
            captured = source.Capture(typeof(Selected), context)!;
            captured.Should().NotBeNull();
            captured.Matches(typeof(Selected), context).Should().BeTrue();
            captured.Matches(typeof(Unlisted), context).Should().BeFalse();
            source.Capture(typeof(Unknown), context).Should().BeNull();
            source.Capture(typeof(Selected), new MutableContext()).Should().BeNull(
                "equal scope values on another context are not the admitted context reference");
            captured.IsActive.Should().BeTrue();
            context.CorrelationId = "00-12345678901234567890123456789012-1234567890123456-01";
            captured.IsActive.Should().BeTrue("a nested logging span changes observability, not the admitted authority");
            context.OrganizationId = OrganizationId.From(Guid.CreateVersion7());
            captured.IsActive.Should().BeFalse();
            context.OrganizationId = null;
            captured.IsActive.Should().BeFalse("an observed invalidated scope cannot be revived by restoring its values");
            source.Capture(typeof(Selected), context).Should().BeNull();
        }
        captured.IsActive.Should().BeFalse();
        await using (var scope = provider.CreateAsyncScope())
        {
            var source = scope.ServiceProvider.GetRequiredService<ITrustedOperationSource>();
            captured = source.Capture(typeof(Selected), context)!;
            captured.IsActive.Should().BeTrue("a new scope has a new finite lifetime");
        }
        captured.IsActive.Should().BeFalse("disposing the actual scope revokes its shared descriptor");

        var entered = false;
        Task<Result<None>> Work()
        {
            entered = true;
            return Task.FromResult(Result.Ok(None.Value));
        }
        var unresolved = await new TenantContextBehavior<Selected, Result<None>>(UnresolvedTenantContext.Instance)
            .Handle(new Selected(), Work, CancellationToken.None);
        unresolved.Error!.Code.Should().Be("tenant_mismatch");
        entered.Should().BeFalse("component policy does not bypass the unresolved-tenant gate");
        var hostOnly = TenantContextFactory.Create(new TenantResolutionAttempt { HostTenantId = context.TenantId }).Value!;
        var ceiling = await new TenantContextBehavior<Selected, Result<None>>(hostOnly)
            .Handle(new Selected(), Work, CancellationToken.None);
        ceiling.Error!.Code.Should().Be("not_found");
        entered.Should().BeFalse("component policy does not bypass the authority ceiling");

        foreach (var invalid in new[]
                 {
                     OperationPolicy.ForTrusted<Selected>("tests.component.boundary.both", AuthorizationSourceKind.Seed, AuthorizationSourceKind.BackgroundJob),
                     OperationPolicy.ForProtected<Selected>("tests.component.boundary.protected", OriginalPolicies.Permission),
                     OperationPolicy.ForTrusted<Selected>("tests.noncomponent", AuthorizationSourceKind.Seed),
                 })
        {
            var invalidServices = new ServiceCollection();
            invalidServices.AddSingleton<IOperationPolicyCatalog>(original);
            var register = () => ComponentOperationRegistration.AddExactComponentAuthority(invalidServices, invalid);
            register.Should().Throw<InvalidOperationException>();
        }
        var duplicate = () => ComponentOperationRegistration.AddExactComponentAuthority(services, selected);
        duplicate.Should().Throw<InvalidOperationException>();

        // Construction proves these helpers retain actual registrations and every
        // canonical behavior, without opening a server connection or replacing completion.
        await using var data = NpgsqlDataSource.Create("Host=127.0.0.1;Username=learnstack_app;Database=component_no_io");
        var databaseServices = ComponentOperationRegistration.CreateDatabaseServices(data, context);
        ComponentOperationRegistration.AddExactComponentAuthority(databaseServices, selected);
        await using var database = databaseServices.BuildServiceProvider();
        await using var databaseScope = database.CreateAsyncScope();
        databaseScope.ServiceProvider.GetServices<IPipelineBehavior<Selected, Result<None>>>()
            .Select(behavior => behavior.GetType().GetGenericTypeDefinition())
            .Should().Equal(MediatRPipelineRegistration.CanonicalBehaviorOrder);
        databaseScope.ServiceProvider.GetRequiredService<IUnitOfWork>().Should().BeOfType<NpgsqlUnitOfWork>();
        databaseScope.ServiceProvider.GetRequiredService<IAuditStore>().Should().BeOfType<PostgresAuditStore>();
    }

    private sealed record Selected;
    private sealed record Unlisted;
    private sealed record Unknown;

    private sealed class OriginalPolicies : IOperationPolicySource
    {
        internal static PermissionDefinition Permission { get; } = new(PermissionScope.Tenant, "tests.component.read");
        public IEnumerable<PermissionRegistration> Definitions => [new(Permission, true)];
        public IEnumerable<OperationPolicy> Policies =>
        [OperationPolicy.ForProtected<Selected>("tests.original.selected", Permission),
         OperationPolicy.ForProtected<Unlisted>("tests.original.unlisted", Permission)];
    }

    private sealed class MutableContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId { get; } = TenantId.From(Guid.CreateVersion7());
        public OrganizationId? OrganizationId { get; set; }
        public UserId? UserId { get; } = LearnStack.SharedKernel.Identifiers.UserId.From(Guid.CreateVersion7());
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId { get; set; }
        public string? ModuleName => null;
    }
}
