using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Actual per-act SeedComposition ownership, without opening a database connection.</summary>
public sealed class SeedOperationSourceTests
{
    [Fact]
    public async Task Actual_seed_composition_captures_the_exact_finite_manifest_and_closes_it_with_its_scope()
    {
        await using var data = Source();
        var context = new SeedTenantContext(TenantId.From(Guid.CreateVersion7()), OrganizationId.From(Guid.CreateVersion7()));
        await using var provider = SeedComposition.Build(data, context, NullLoggerFactory.Instance);
        var scope = provider.CreateAsyncScope();
        var expected = ModuleSources().SelectMany(source => source.Policies)
            .Where(policy => policy.AllowedSources.Contains(AuthorizationSourceKind.Seed)).Select(policy => policy.RequestType).ToArray();
        expected.Should().HaveCount(34).And.OnlyHaveUniqueItems();
        var sources = scope.ServiceProvider.GetServices<ITrustedOperationSource>().ToArray();
        var captured = expected.Select(type => sources.Select(source => source.Capture(type, context))
            .Where(descriptor => descriptor is not null).Cast<TrustedOperationDescriptor>().Should().ContainSingle().Subject).ToArray();
        captured.Should().OnlyContain(descriptor => descriptor.IsActive && descriptor.SourceKind == AuthorizationSourceKind.Seed);
        foreach (var descriptor in captured)
        {
            descriptor.Matches(descriptor.RequestType, context).Should().BeTrue();
            descriptor.Matches(descriptor.RequestType, new SeedTenantContext(context.TenantId, context.OrganizationId)).Should().BeFalse();
        }
        foreach (var type in new[] { typeof(GetPublicSiteQuery), typeof(GetAccountSiteQuery), typeof(SeedOperationSourceTests) })
            sources.Select(source => source.Capture(type, context)).Should().OnlyContain(descriptor => descriptor == null);
        await scope.DisposeAsync();
        captured.Should().OnlyContain(descriptor => !descriptor.IsActive);
        sources.Select(source => source.Capture(typeof(GetTenantSeedStateQuery), context))
            .Should().OnlyContain(descriptor => descriptor == null);
        await using var next = provider.CreateAsyncScope();
        next.ServiceProvider.GetServices<ITrustedOperationSource>().Select(source => source.Capture(typeof(GetTenantSeedStateQuery), context))
            .Should().ContainSingle(descriptor => descriptor != null);
        captured.Should().OnlyContain(descriptor => !descriptor.IsActive, "a later scope cannot revive the disposed owner's evidence");
    }

    [Fact]
    public async Task Only_the_provisioning_operation_receives_unresolved_seed_authority_and_a_system_UUID_alone_is_insufficient()
    {
        await using var data = Source();
        await using var provider = SeedComposition.Build(data, null, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var sources = scope.ServiceProvider.GetServices<ITrustedOperationSource>().ToArray();
        sources.Select(source => source.Capture(typeof(ProvisionTenantCommand), context)).Should().ContainSingle(descriptor => descriptor != null);
        sources.Select(source => source.Capture(typeof(GetTenantSeedStateQuery), context)).Should().OnlyContain(descriptor => descriptor == null);
        var forged = new SeedIdentityTenantContext(TenantId.From(Guid.CreateVersion7()), null);
        sources.Select(source => source.Capture(typeof(GetTenantSeedStateQuery), forged)).Should().OnlyContain(descriptor => descriptor == null);
    }

    private static NpgsqlDataSource Source() => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Database = "seed_operation_no_io",
        Username = "learnstack_app"
    }.ConnectionString);

    private static IEnumerable<IOperationPolicySource> ModuleSources() => new[]
    {
        typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker).Assembly,
        typeof(LearnStack.Modules.Customization.Application.AssemblyMarker).Assembly,
        typeof(LearnStack.Modules.Education.Application.AssemblyMarker).Assembly,
        typeof(LearnStack.Modules.Identity.Application.AssemblyMarker).Assembly,
    }.SelectMany(assembly => assembly.GetTypes()).Where(type => typeof(IOperationPolicySource).IsAssignableFrom(type)
        && type is { IsClass: true, IsAbstract: false })
        .Select(type => (IOperationPolicySource)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("The actual module policy source was not constructed.")));
}
