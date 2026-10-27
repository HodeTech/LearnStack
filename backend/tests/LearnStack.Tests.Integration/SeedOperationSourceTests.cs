using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_seed_owner_keeps_a_missing_actor_null_in_its_frozen_provenance_without_database_IO(bool provisioning)
    {
        await using var data = Source();
        var tenant = TenantId.From(Guid.CreateVersion7());
        var supplied = provisioning ? null : new SeedTenantContext(tenant, null);
        await using var provider = SeedComposition.Build(data, supplied, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<ITenantContext>();
        var capture = services.GetRequiredService<IAuditStateCapture>();
        capture.OpenFrame();
        capture.DeclareIntent(new AuditIntent(AuditEntryId.From(Guid.CreateVersion7()), tenant, null, null, null,
            "tenancy", "tenancy.seed.provenance_probe", OperationType.SecurityEvent, OperationClass.Must, null, DateTimeOffset.UtcNow));
        OperationAuthorizationCoordinator? owner = null;
        using var evidence = new AuthorizationEvidenceCoordinator(services.GetRequiredService<IAuditStore>(), capture,
            services.GetRequiredService<IUnitOfWork>(), new SystemClock(), new SystemGuidFactory(), () => owner!, [],
            NullLogger<AuthorizationEvidenceCoordinator>.Instance);
        using var coordinator = owner = new OperationAuthorizationCoordinator(new OperationPolicyCatalog(ModuleSources()),
            new NoHumanReads(), context, [], services.GetServices<ITrustedOperationSource>(), evidence);
        // This isolates source/provenance. It neither dispatches a business handler
        // nor claims the manually declared fixture intent became durable.
        if (provisioning)
        {
            var request = new ProvisionTenantCommand(tenant, "seed", "Seed", OrganizationId.From(Guid.CreateVersion7()), "default", "Default");
            var result = await coordinator.ExecuteRequestAsync<ProvisionTenantCommand, Result<ProvisionedTenantDto>>(request,
                () => Task.FromResult(Result.Ok(new ProvisionedTenantDto(tenant, "seed", request.DefaultOrganizationId, "default"))));
            result.IsSuccess.Should().BeTrue();
        }
        else
        {
            var result = await coordinator.ExecuteRequestAsync<GetTenantSeedStateQuery, Result<SeedLookup<TenantSeedDto>>>(new(),
                () => Task.FromResult(Result.Ok(new SeedLookup<TenantSeedDto>(null))));
            result.IsSuccess.Should().BeTrue();
        }
        capture.CloseFrame(AuditIntentResult.Succeeded);
        var draft = AuditDraftComposer.InTransaction(capture.Intents.Single(), []);
        draft.ActorUserId.Should().BeNull("the finite source does not invent SystemActor for an absent principal");
        capture.Intents.Single().Authorization.Should().NotBeNull();
        capture.Intents.Single().Authorization!.SourceKind.Should().Be(AuthorizationSourceKind.Seed);
        draft.Metadata.Should().Contain("\"source\":\"seed\"");
        services.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
        capture.Clear();
    }

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

    private sealed class NoHumanReads : IProtectedOperationAuthorizer
    {
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Finite seed provenance cannot invoke human authorization.");
    }

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
