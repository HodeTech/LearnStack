using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using TenantRoot = LearnStack.Modules.Tenancy.Domain.Tenant;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class SeedAdministrationOriginTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("01930000-0000-7000-8000-000000000001"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("01930000-0000-7000-8000-0000000000a1"));
    private const string Host = "manage.demo-english.learnstack.local";
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_closed_manifest_contains_only_the_two_approved_protocol_pins()
    {
        SeedAdministrationOriginManifest.All.Select(declaration =>
                (declaration.TenantId.Value, declaration.TenantSlug, declaration.Host))
            .Should().Equal(
                (Guid.Parse("01930000-0000-7000-8000-000000000001"), "demo-english", "manage.demo-english.learnstack.local"),
                (Guid.Parse("01930000-0000-7000-8000-000000000002"), "demo-yoga", "manage.demo-yoga.learnstack.local"));
        SeedAdministrationOriginManifest.Find(TenantId.From(Guid.CreateVersion7())).Should().BeNull();
    }

    [Fact]
    public async Task Ambient_system_attribution_without_the_actual_current_seed_invocation_touches_no_owner()
    {
        using var fixture = new Fixture();
        (await fixture.Handle(new(fixture.Root.Version))).IsFailure.Should().BeTrue();
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().FindForUpdateAsync(Tenant);
        await fixture.Mappings.DidNotReceiveWithAnyArgs().FindForUpdateAsync(null!);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("actor")]
    [InlineData("declaration")]
    [InlineData("reserved")]
    [InlineData("other_request")]
    public async Task Scope_source_and_exact_declaration_refuse_before_any_lock(string invalid)
    {
        using var fixture = new Fixture();
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        if (invalid == "organization") fixture.Context.OrganizationId.Returns(Organization);
        if (invalid == "actor") fixture.Context.UserId.Returns(UserId.From(Guid.CreateVersion7()));
        if (invalid == "declaration") fixture.Admission.ForTenant(Tenant).Returns(new SeedAdministrationOriginDeclaration(Tenant, "demo-english", "other.example"));
        if (invalid == "reserved") fixture.Reserved.IsReserved(Host).Returns(true);
        fixture.Enter(invalid == "other_request" ? command with { } : command);
        (await fixture.Handle(command)).IsFailure.Should().BeTrue();
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().FindForUpdateAsync(Tenant);
    }

    [Fact]
    public async Task An_absent_mapping_is_inserted_after_fresh_locks_and_before_the_single_Tenant_transition()
    {
        using var fixture = new Fixture();
        var events = new List<string>();
        fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("fence"); return Task.CompletedTask; });
        fixture.Tenants.FindForUpdateAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("tenant"); return Task.FromResult<TenantRoot?>(fixture.Root); });
        fixture.Mappings.FindForUpdateAsync(Host, Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("projection_lock"); return Task.FromResult<PlatformHostMapping?>(null); });
        fixture.Completion.VerifyAsync(Arg.Any<AdoptSeedAdministrationOriginCommand>(),
                Arg.Any<SeedAdministrationOriginPreparedTarget>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                events.Add("completion");
                var prepared = call.Arg<SeedAdministrationOriginPreparedTarget>();
                prepared.TenantId.Should().Be(fixture.Root.Id);
                prepared.TenantSlug.Should().Be(fixture.Root.Slug);
                prepared.TenantVersion.Should().Be(fixture.Root.Version);
                prepared.Marker.Should().Be(0);
                prepared.DefaultOrganizationId.Should().Be(Organization);
                prepared.Mapping.Should().BeNull();
                return Task.FromResult(true);
            });
        fixture.Mappings.AddAsync(Arg.Any<PlatformHostMapping>(), Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("insert_projection"); fixture.Root.AdoptedSeedOriginVersion.Should().Be(0); return Task.CompletedTask; });
        fixture.Tenants.UpdateAsync(fixture.Root, Arg.Any<CancellationToken>())
            .Returns(_ => { events.Add("update_tenant"); fixture.Root.AdoptedSeedOriginVersion.Should().Be(2); return Task.CompletedTask; });

        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        var result = await fixture.Handle(command);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(command.ExpectedVersion + 1);
        events.Should().Equal("fence", "tenant", "projection_lock", "completion", "insert_projection", "update_tenant");
        await fixture.Locks.Received(1).AcquireAsync(Arg.Is<IdentityMutationLockPlan>(plan =>
            plan.UserIds.IsEmpty && plan.TenantId == Tenant && plan.TenantMode == IdentityTenantLockMode.Exclusive), Arg.Any<CancellationToken>());
        fixture.Subject.Received(1).Designate(fixture.Root);
        fixture.Subject.Received(1).DesignateProjection(Arg.Is<PlatformHostMapping>(mapping =>
            mapping.Host == Host && mapping.TenantId == Tenant && mapping.OrganizationId == null && mapping.IsActive && mapping.IsPubliclyLive));
    }

    [Fact]
    public async Task The_exact_existing_mapping_is_designated_without_a_duplicate_insert()
    {
        using var fixture = new Fixture();
        var mapping = PlatformHostMapping.Create(Host, Tenant, isActive: true, isPubliclyLive: true);
        fixture.Mappings.FindForUpdateAsync(Host, Arg.Any<CancellationToken>()).Returns(mapping);
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).IsSuccess.Should().BeTrue();
        fixture.Subject.Received(1).DesignateProjection(mapping);
        await fixture.Mappings.DidNotReceiveWithAnyArgs().AddAsync(null!);
        await fixture.Tenants.Received(1).UpdateAsync(fixture.Root, Arg.Any<CancellationToken>());
        await fixture.Cache.DidNotReceiveWithAnyArgs().InvalidateAsync(null!);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("not_live")]
    [InlineData("organization")]
    [InlineData("foreign")]
    public async Task A_conflicting_existing_projection_is_never_overwritten(string conflict)
    {
        using var fixture = new Fixture();
        var mapping = PlatformHostMapping.Create(Host,
            conflict == "foreign" ? TenantId.From(Guid.CreateVersion7()) : Tenant,
            conflict == "organization" ? Organization : null,
            isActive: conflict != "disabled", isPubliclyLive: conflict is not ("disabled" or "not_live"));
        fixture.Mappings.FindForUpdateAsync(Host, Arg.Any<CancellationToken>()).Returns(mapping);
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).IsFailure.Should().BeTrue();
        fixture.Root.AdoptedSeedOriginVersion.Should().Be(0);
        await fixture.Mappings.DidNotReceiveWithAnyArgs().AddAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Marker_two_refuses_a_new_command_without_completion_or_a_second_business_write(bool present)
    {
        using var fixture = new Fixture();
        fixture.Root.AdoptSeedAdministrationOrigin(Clock, UserId.SystemActor);
        if (present) fixture.Mappings.FindForUpdateAsync(Host, Arg.Any<CancellationToken>())
            .Returns(PlatformHostMapping.Create(Host, Tenant, isActive: true, isPubliclyLive: true));
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).IsFailure.Should().BeTrue();
        fixture.Root.Version.Should().Be(command.ExpectedVersion);
        await fixture.Mappings.DidNotReceiveWithAnyArgs().AddAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        await fixture.Completion.DidNotReceiveWithAnyArgs().VerifyAsync(null!, null!, null!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_marker_one_requires_an_exact_mapping_and_completed_foundation(bool complete)
    {
        using var fixture = new Fixture();
        typeof(TenantRoot).GetProperty(nameof(TenantRoot.AdoptedSeedOriginVersion))!.SetValue(fixture.Root, 1);
        var mapping = PlatformHostMapping.Create(Host, Tenant, isActive: true, isPubliclyLive: true);
        fixture.Mappings.FindForUpdateAsync(Host, Arg.Any<CancellationToken>()).Returns(mapping);
        fixture.Completion.VerifyAsync(Arg.Any<AdoptSeedAdministrationOriginCommand>(),
                Arg.Any<SeedAdministrationOriginPreparedTarget>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(complete);
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).IsSuccess.Should().Be(complete);
        fixture.Root.AdoptedSeedOriginVersion.Should().Be(complete ? 2 : 1);
        await fixture.Completion.Received(1).VerifyAsync(command,
            Arg.Is<SeedAdministrationOriginPreparedTarget>(prepared => prepared.Marker == 1
                && prepared.Mapping != null && prepared.Mapping.Host == Host && prepared.Mapping.TenantId == Tenant
                && prepared.Mapping.OrganizationId == null && prepared.Mapping.IsActive && prepared.Mapping.IsPubliclyLive),
            Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        await fixture.Mappings.DidNotReceiveWithAnyArgs().AddAsync(null!);
    }

    [Fact]
    public async Task Legacy_marker_one_with_missing_mapping_refuses_before_completion()
    {
        using var fixture = new Fixture();
        typeof(TenantRoot).GetProperty(nameof(TenantRoot.AdoptedSeedOriginVersion))!.SetValue(fixture.Root, 1);
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).IsFailure.Should().BeTrue();
        fixture.Root.AdoptedSeedOriginVersion.Should().Be(1);
        await fixture.Completion.DidNotReceiveWithAnyArgs().VerifyAsync(null!, null!, null!);
        await fixture.Mappings.DidNotReceiveWithAnyArgs().AddAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_completion_or_expired_source_after_verification_never_changes_the_prepared_target(bool expire)
    {
        using var fixture = new Fixture();
        fixture.Completion.VerifyAsync(Arg.Any<AdoptSeedAdministrationOriginCommand>(),
                Arg.Any<SeedAdministrationOriginPreparedTarget>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (expire) fixture.Source.Dispose();
                return Task.FromResult(expire);
            });
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).IsFailure.Should().BeTrue();
        fixture.Root.AdoptedSeedOriginVersion.Should().Be(0);
        await fixture.Mappings.DidNotReceiveWithAnyArgs().AddAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task A_stale_expected_version_refuses_after_the_root_lock_before_projection_access()
    {
        using var fixture = new Fixture();
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version - 1);
        fixture.Enter(command);
        (await fixture.Handle(command)).Error!.Code.Should().Be("concurrency_conflict");
        await fixture.Mappings.DidNotReceiveWithAnyArgs().FindForUpdateAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task A_foreign_unique_host_collision_is_an_opaque_refusal_and_poisons_the_owner()
    {
        using var fixture = new Fixture();
        fixture.Mappings.AddAsync(Arg.Any<PlatformHostMapping>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new AggregateConflictException("fixed host conflict", "pk_platform_host_to_tenant")));
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).Error!.Code.Should().Be("business_rule_violation");
        fixture.Unit.Received(1).MarkRollbackOnly();
        fixture.Root.AdoptedSeedOriginVersion.Should().Be(0);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task Revoked_source_after_the_fence_wait_refuses_before_reading_the_projection()
    {
        using var fixture = new Fixture();
        fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { fixture.Source.Dispose(); return Task.CompletedTask; });
        var command = new AdoptSeedAdministrationOriginCommand(fixture.Root.Version);
        fixture.Enter(command);
        (await fixture.Handle(command)).IsFailure.Should().BeTrue();
        await fixture.Mappings.DidNotReceiveWithAnyArgs().FindForUpdateAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly InvocationSource _invocations = new();
        public ITenantContext Context { get; } = Substitute.For<ITenantContext>();
        public ITenantWriteStore Tenants { get; } = Substitute.For<ITenantWriteStore>();
        public IPlatformHostMappingStore Mappings { get; } = Substitute.For<IPlatformHostMappingStore>();
        public ISeedAdministrationOriginAdmission Admission { get; } = Substitute.For<ISeedAdministrationOriginAdmission>();
        public IFixedSeedOriginCompletionVerifier Completion { get; } = Substitute.For<IFixedSeedOriginCompletionVerifier>();
        public IIdentityMutationLocks Locks { get; } = Substitute.For<IIdentityMutationLocks>();
        public IOperationInvocationContext Execution { get; } = Substitute.For<IOperationInvocationContext>();
        public IAuditSubject Subject { get; } = Substitute.For<IAuditSubject>();
        public IUnitOfWork Unit { get; } = Substitute.For<IUnitOfWork>();
        public IHostResolutionInvalidator Cache { get; } = Substitute.For<IHostResolutionInvalidator>();
        public IReservedHostRegistry Reserved { get; } = Substitute.For<IReservedHostRegistry>();
        public TenantRoot Root { get; }
        public TrustedSource Source { get; } = new();

        public Fixture()
        {
            Context.IsResolved.Returns(true);
            Context.TenantId.Returns(Tenant);
            Context.Origin.Returns(TenantContextOrigin.Ambient);
            Admission.ForTenant(Tenant).Returns(new SeedAdministrationOriginDeclaration(Tenant, "demo-english", Host));
            Completion.VerifyAsync(Arg.Any<AdoptSeedAdministrationOriginCommand>(),
                    Arg.Any<SeedAdministrationOriginPreparedTarget>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
                .Returns(true);
            Root = TenantRoot.Create(Tenant, "demo-english", "An edited mutable name", Clock, UserId.SystemActor);
            Root.AssignDefaultOrganization(Organization, Clock, new SystemGuidFactory(), UserId.SystemActor);
            Tenants.FindForUpdateAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Root);
            var reader = Substitute.For<ISeedStateReader>();
            reader.ReadOrganizationAsync(Organization, Arg.Any<CancellationToken>()).Returns(
                new OrganizationSeedDto(Organization, Tenant, "kadikoy", "Edited default name", "Active"));
            var services = new ServiceCollection();
            services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(ITenantWriteStore).Assembly));
            services.AddSingleton(Context);
            services.AddSingleton(Tenants);
            services.AddSingleton(Mappings);
            services.AddSingleton(reader);
            services.AddSingleton(Admission);
            services.AddSingleton(Completion);
            services.AddSingleton(Locks);
            services.AddSingleton(Execution);
            services.AddSingleton(Subject);
            services.AddSingleton(Unit);
            services.AddSingleton(Cache);
            services.AddSingleton(Reserved);
            services.AddSingleton<IClock>(Clock);
            _provider = services.BuildServiceProvider();
        }

        public void Enter(AdoptSeedAdministrationOriginCommand command)
        {
            var invocation = _invocations.Mint(command, Context, Source.Mint(Context));
            Execution.CurrentInvocation.Returns(invocation);
        }

        public Task<Result<long>> Handle(AdoptSeedAdministrationOriginCommand command) =>
            _provider.GetRequiredService<IRequestHandler<AdoptSeedAdministrationOriginCommand, Result<long>>>()
                .Handle(command, CancellationToken.None);

        public void Dispose()
        {
            Source.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class TrustedSource : TrustedOperationDescriptor.MintingSource
    {
        public TrustedOperationDescriptor Mint(ITenantContext context) =>
            ForOperation(AuthorizationSourceKind.Seed, typeof(AdoptSeedAdministrationOriginCommand), context);
        protected override bool IsEvidenceCurrent(object? evidence) => true;
    }

    private sealed class InvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Mint(AdoptSeedAdministrationOriginCommand command, ITenantContext context,
            TrustedOperationDescriptor source) => _current = ForInvocation(command,
                OperationPolicy.ForTrusted<AdoptSeedAdministrationOriginCommand>("tenancy.seed.adopt_administration_origin", AuthorizationSourceKind.Seed),
                context, null, source, CancellationToken.None);
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }
}
