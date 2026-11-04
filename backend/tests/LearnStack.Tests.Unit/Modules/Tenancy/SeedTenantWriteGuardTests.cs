using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using System.Data.Common;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Seeding;
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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using TenantRoot = LearnStack.Modules.Tenancy.Domain.Tenant;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class SeedTenantWriteGuardTests
{
    private const string Theme = """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""";
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 28, 1, 0, 0, TimeSpan.Zero));
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Default = OrganizationId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Second = OrganizationId.From(Guid.CreateVersion7());
    private static readonly Guid ThemeId = Guid.CreateVersion7();
    private static readonly SeedTenantDeclaration Declaration = new(Tenant, "declared", "Declared",
        new(Default, "first", "First"), new(Second, "second", "Second"),
        [new("en", true, true, 0), new("tr", true, false, 1)], "declared.example.test", Default, ThemeId, Theme);

    [Fact]
    public async Task Direct_handler_without_a_current_finite_invocation_never_locks_reads_or_writes()
    {
        using var fixture = new Fixture(locales: 1);
        var result = await fixture.Handler<AddTenantLocaleCommand, TenantLocalesDto>().Handle(fixture.NextLocale(), default);
        Assert.Equal("business_rule_violation", result.Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.State.DidNotReceiveWithAnyArgs().ReadTenantAsync(default);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().FindForUpdateAsync(Tenant);
    }

    [Fact]
    public async Task A_current_seed_invocation_with_an_unknown_declaration_still_denies_before_locks_or_reads()
    {
        using var fixture = new Fixture(locales: 1, admitted: false);
        var result = await fixture.SendAsync<AddTenantLocaleCommand, TenantLocalesDto>(fixture.NextLocale());
        Assert.Equal("business_rule_violation", result.Error!.Code);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.State.DidNotReceiveWithAnyArgs().ReadTenantAsync(default);
    }

    [Fact]
    public async Task Only_the_next_original_locale_and_exact_expected_root_version_can_continue()
    {
        using var fixture = new Fixture(locales: 1);
        var version = fixture.Root.Version;
        Assert.True((await fixture.SendAsync<AddTenantLocaleCommand, TenantLocalesDto>(fixture.NextLocale())).IsSuccess);
        Assert.Equal(version + 1, fixture.Root.Version);
        Assert.Equal(2, fixture.Root.Locales.Count);
        await fixture.Tenants.Received(1).FindForUpdateAsync(Tenant, Arg.Any<CancellationToken>());
        await fixture.Tenants.Received(1).UpdateAsync(fixture.Root, Arg.Any<CancellationToken>());
        Assert.Empty(fixture.HeldPlan!.UserIds);
        Assert.Equal(IdentityTenantLockMode.Exclusive, fixture.HeldPlan.TenantMode);
        Assert.Equal(Tenant, fixture.HeldPlan.TenantId);
    }

    [Theory]
    [InlineData("out-of-order")]
    [InlineData("stale")]
    [InlineData("flags")]
    [InlineData("completed")]
    public async Task Locale_seed_refuses_nonprefix_or_changed_values_without_mutating(string variant)
    {
        using var fixture = new Fixture(locales: variant == "completed" ? 2 : variant == "out-of-order" ? 0 : 1);
        var request = fixture.NextLocale() with
        {
            ExpectedVersion = variant == "stale" ? fixture.Root.Version - 1 : fixture.Root.Version,
            IsEnabled = variant != "flags",
        };
        var version = fixture.Root.Version;
        var result = await fixture.SendAsync<AddTenantLocaleCommand, TenantLocalesDto>(request);
        Assert.Equal(variant == "flags" ? "business_rule_violation" : "concurrency_conflict", result.Error!.Code);
        Assert.Equal(version, fixture.Root.Version);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Fact]
    public async Task A_change_committed_while_waiting_is_read_after_the_exclusive_fence_and_refused()
    {
        using var fixture = new Fixture(locales: 1);
        var request = fixture.NextLocale();
        fixture.AfterLock = () => fixture.Root.SetDefaultLocale("en", Clock, UserId.SystemActor);
        var result = await fixture.SendAsync<AddTenantLocaleCommand, TenantLocalesDto>(request);
        Assert.Equal("concurrency_conflict", result.Error!.Code);
        Assert.Single(fixture.Root.Locales);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        Received.InOrder(() =>
        {
            fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
            fixture.State.ReadTenantAsync(Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_canonical_competitor_is_a_fresh_verification_conflict_only_while_the_original_source_remains_current(bool sourceExpires)
    {
        using var fixture = new Fixture(second: false, host: false);
        var request = new CreateOrganizationCommand(Second, Declaration.SecondOrganization.Slug, Declaration.SecondOrganization.DisplayName);
        fixture.AfterLock = () => fixture.State.ReadOrganizationAsync(Second, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (sourceExpires) fixture.RevokeSource();
            return new OrganizationSeedDto(Second, Tenant, Declaration.SecondOrganization.Slug,
                Declaration.SecondOrganization.DisplayName, "Active", CreatedAt: Clock.UtcNow, CreatedBy: UserId.SystemActor);
        });
        var result = await fixture.SendAsync<CreateOrganizationCommand, OrganizationDto>(request);
        Assert.Equal(sourceExpires ? "business_rule_violation" : "concurrency_conflict", result.Error!.Code);
        await fixture.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        await fixture.Organizations.DidNotReceiveWithAnyArgs().AddAsync(null!);
        await fixture.Prerequisites.DidNotReceiveWithAnyArgs().VerifyAsync(null!, null!);
    }

    [Fact]
    public async Task Losing_the_exact_plan_after_the_locked_root_read_prevents_locale_mutation()
    {
        using var fixture = new Fixture(locales: 1);
        fixture.Tenants.FindForUpdateAsync(Tenant, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fixture.HeldPlan = null;
            return fixture.Root;
        });
        var version = fixture.Root.Version;
        var result = await fixture.SendAsync<AddTenantLocaleCommand, TenantLocalesDto>(fixture.NextLocale());
        Assert.Equal("dependency_unavailable", result.Error!.Code);
        Assert.Equal(version, fixture.Root.Version);
        fixture.Unit.Received().MarkRollbackOnly();
    }

    [Fact]
    public async Task Locale_prerequisites_are_read_after_target_lock_and_a_later_Identity_prefix_refuses_the_write()
    {
        using var fixture = new Fixture(locales: 1);
        fixture.Prerequisites.VerifyAsync(Arg.Any<object>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var version = fixture.Root.Version;
        var request = fixture.NextLocale();
        var result = await fixture.SendAsync<AddTenantLocaleCommand, TenantLocalesDto>(request);
        Assert.Equal("business_rule_violation", result.Error!.Code);
        Assert.Equal(version, fixture.Root.Version);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        await fixture.Locks.Received(1).AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            fixture.Tenants.FindForUpdateAsync(Tenant, Arg.Any<CancellationToken>());
            fixture.State.ReadTenantAsync(Arg.Any<CancellationToken>());
            fixture.Prerequisites.VerifyAsync(request, fixture.HeldPlan!, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Branding_prerequisites_follow_the_setting_probe_and_refusal_preserves_absence()
    {
        using var fixture = new Fixture(locales: 2);
        fixture.Prerequisites.VerifyAsync(Arg.Any<object>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var request = new SetTenantBrandingCommand(ThemeId, Theme, null);
        var result = await fixture.SendAsync<SetTenantBrandingCommand, TenantBrandingDto>(request);
        Assert.Equal("business_rule_violation", result.Error!.Code);
        await fixture.Settings.DidNotReceiveWithAnyArgs().AddAsync(null!);
        Received.InOrder(() =>
        {
            fixture.Settings.FindForUpdateAsync(TenantSettingId.From(ThemeId), Arg.Any<CancellationToken>());
            fixture.State.ReadTenantAsync(Arg.Any<CancellationToken>());
            fixture.Customization.IsCompleteAsync(fixture.HeldPlan!, Arg.Any<CancellationToken>());
            fixture.Prerequisites.VerifyAsync(request, fixture.HeldPlan!, Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Branding_requires_completed_Customization_and_current_admission_after_its_read(bool sourceExpires)
    {
        using var fixture = new Fixture(locales: 2);
        fixture.Customization.IsCompleteAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (sourceExpires) fixture.RevokeSource();
                return sourceExpires;
            });
        var result = await fixture.SendAsync<SetTenantBrandingCommand, TenantBrandingDto>(new(ThemeId, Theme, null));
        Assert.Equal(sourceExpires ? "business_rule_violation" : "concurrency_conflict", result.Error!.Code);
        await fixture.Settings.DidNotReceiveWithAnyArgs().AddAsync(null!);
        await fixture.Prerequisites.DidNotReceiveWithAnyArgs().VerifyAsync(null!, null!);
        await fixture.Customization.Received(1).IsCompleteAsync(fixture.HeldPlan!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Provisioning_checks_only_the_exact_declaration_and_fence_then_keeps_INSERT_first()
    {
        using var fixture = new Fixture(resolved: false);
        fixture.State.ReadTenantAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<TenantSeedDto?>(
            new InvalidOperationException("Provisioning must not SELECT a Tenant.")));
        var request = new ProvisionTenantCommand(Tenant, Declaration.Slug, Declaration.DisplayName,
            Default, Declaration.DefaultOrganization.Slug, Declaration.DefaultOrganization.DisplayName);
        var result = await fixture.SendAsync<ProvisionTenantCommand, ProvisionedTenantDto>(request);
        Assert.True(result.IsSuccess);
        await fixture.State.DidNotReceiveWithAnyArgs().ReadTenantAsync(default);
        Received.InOrder(() =>
        {
            fixture.Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>());
            fixture.Tenants.AddAsync(Arg.Any<TenantRoot>(), Arg.Any<CancellationToken>());
            fixture.Organizations.AddAsync(Arg.Any<Organization>(), Arg.Any<CancellationToken>());
            fixture.Tenants.UpdateAsync(Arg.Any<TenantRoot>(), Arg.Any<CancellationToken>());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_organization_creation_requires_the_untouched_first_stage_and_exact_name(bool laterStage)
    {
        using var fixture = new Fixture(second: false, host: laterStage);
        var request = new CreateOrganizationCommand(Second, Declaration.SecondOrganization.Slug, Declaration.SecondOrganization.DisplayName);
        var result = await fixture.SendAsync<CreateOrganizationCommand, OrganizationDto>(request);
        Assert.Equal(!laterStage, result.IsSuccess);
        if (laterStage) await fixture.Organizations.DidNotReceiveWithAnyArgs().AddAsync(null!);
        else await fixture.Organizations.Received(1).AddAsync(Arg.Is<Organization>(row => row.Id == Second), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_host_mapping_requires_the_original_scope_flags_and_previous_organization_stage(bool altered)
    {
        using var fixture = new Fixture(host: false);
        var request = new MapHostToTenantCommand(Declaration.PublicHost, Default, true, !altered);
        var result = await fixture.SendAsync<MapHostToTenantCommand, HostMappingDto>(request);
        Assert.Equal(!altered, result.IsSuccess);
        if (altered)
        {
            await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
            await fixture.Hosts.DidNotReceiveWithAnyArgs().AddAsync(null!);
        }
        else await fixture.Hosts.Received(1).AddAsync(Arg.Is<PlatformHostMapping>(row => row.OrganizationId == Default
            && row.IsActive && row.IsPubliclyLive), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(0L, false, false)]
    [InlineData(null, true, false)]
    public async Task Branding_is_exact_fixed_create_only_and_a_retained_root_can_never_be_overwritten(long? version, bool existing, bool allowed)
    {
        using var fixture = new Fixture(locales: 2);
        if (existing)
            fixture.Settings.FindForUpdateAsync(TenantSettingId.From(ThemeId), Arg.Any<CancellationToken>()).Returns(
                TenantSetting.Create(TenantSettingId.From(ThemeId), Tenant, null, BrandingThemeRegistry.SettingKey, Theme, Clock, UserId.SystemActor));
        var result = await fixture.SendAsync<SetTenantBrandingCommand, TenantBrandingDto>(new(ThemeId, Theme, version));
        Assert.Equal(allowed, result.IsSuccess);
        await fixture.Settings.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        if (allowed) await fixture.Settings.Received(1).AddAsync(Arg.Any<TenantSetting>(), Arg.Any<CancellationToken>());
        else await fixture.Settings.DidNotReceiveWithAnyArgs().AddAsync(null!);
    }

    [Fact]
    public async Task Undeclared_seed_default_promotion_is_always_a_no_write_refusal()
    {
        using var fixture = new Fixture(locales: 2);
        var version = fixture.Root.Version;
        var result = await fixture.SendAsync<SetDefaultTenantLocaleCommand, TenantLocalesDto>(new(version, "tr"));
        Assert.Equal("business_rule_violation", result.Error!.Code);
        Assert.Equal(version, fixture.Root.Version);
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing-locale", false)]
    [InlineData("edited-tenant", false)]
    [InlineData("edited-organization", false)]
    [InlineData("inactive-host", false)]
    [InlineData("edited-theme", false)]
    [InlineData("foreign-theme-creator", false)]
    public async Task Identity_continuation_uses_fresh_full_original_Tenancy_evidence_on_its_existing_owner(string variant, bool allowed)
    {
        using var fixture = new Fixture(locales: variant == "missing-locale" ? 1 : 2);
        fixture.State.ReadSettingAsync(TenantSettingId.From(ThemeId), Arg.Any<CancellationToken>()).Returns(
            new SettingSeedDto(ThemeId, Tenant, null, BrandingThemeRegistry.SettingKey, Theme, variant == "edited-theme" ? 1 : 0,
                CreatedAt: Clock.UtcNow, CreatedBy: variant == "foreign-theme-creator" ? UserId.From(Guid.CreateVersion7()) : UserId.SystemActor));
        if (variant == "edited-tenant") fixture.Root.SetDefaultLocale("en", Clock, UserId.SystemActor);
        if (variant == "edited-organization")
            fixture.State.ReadOrganizationAsync(Second, Arg.Any<CancellationToken>()).Returns(
                new OrganizationSeedDto(Second, Tenant, Declaration.SecondOrganization.Slug, "Edited", "Active",
                    CreatedAt: Clock.UtcNow, CreatedBy: UserId.SystemActor));
        if (variant == "inactive-host")
            fixture.State.ReadHostMappingAsync(Declaration.PublicHost, Arg.Any<CancellationToken>()).Returns(
                new HostMappingSeedDto(Declaration.PublicHost, Tenant, Default, false, true));
        Assert.Equal(allowed, await fixture.IdentityContinuationAsync());
        // The Identity caller owns its complete User-before-Tenant plan; this contract opens no second owner or plan.
        await fixture.Locks.DidNotReceiveWithAnyArgs().AcquireAsync(null!);
        await fixture.Tenants.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
        await fixture.Organizations.DidNotReceiveWithAnyArgs().UpdateAsync(null!);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly TrustedSource _source;
        private readonly OperationAuthorizationCoordinator _coordinator;
        public Fixture(int locales = 0, bool second = true, bool host = true, bool resolved = true, bool admitted = true)
        {
            var context = Substitute.For<ITenantContext>();
            context.IsResolved.Returns(resolved);
            context.TenantId.Returns(Tenant);
            context.Origin.Returns(resolved ? TenantContextOrigin.Ambient : null);
            context.UserId.Returns(UserId.SystemActor);
            var guids = new SystemGuidFactory();
            Root = TenantRoot.Create(Tenant, Declaration.Slug, Declaration.DisplayName, Clock, UserId.SystemActor);
            Root.AssignDefaultOrganization(Default, Clock, guids, UserId.SystemActor);
            foreach (var locale in Declaration.OriginalLocales.Take(locales))
                Root.AddLocale(locale.Locale, locale.IsDefault, Clock, UserId.SystemActor, locale.IsEnabled, locale.Sort);
            var transaction = Substitute.For<DbTransaction>();
            Unit.HasActiveTransaction.Returns(true);
            Unit.Mode.Returns(TransactionMode.ReadWrite);
            Unit.Transaction.Returns(transaction);
            Unit.GetTenantContextAnnouncement(transaction).Returns(new TenantContextAnnouncement(1, Tenant, null));
            State.ReadTenantAsync(Arg.Any<CancellationToken>()).Returns(_ => Snapshot(Root));
            State.ReadOrganizationAsync(Default, Arg.Any<CancellationToken>()).Returns(Organization(Declaration.DefaultOrganization));
            State.ReadOrganizationAsync(Second, Arg.Any<CancellationToken>()).Returns(second ? Organization(Declaration.SecondOrganization) : null);
            State.ReadHostMappingAsync(Declaration.PublicHost, Arg.Any<CancellationToken>()).Returns(host
                ? new HostMappingSeedDto(Declaration.PublicHost, Tenant, Default, true, true) : null);
            Tenants.FindForUpdateAsync(Tenant, Arg.Any<CancellationToken>()).Returns(Root);
            Locks.AcquireAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                HeldPlan = call.Arg<IdentityMutationLockPlan>();
                AfterLock?.Invoke();
                return Task.CompletedTask;
            });
            Locks.When(value => value.RequireCurrent(Arg.Any<IdentityMutationLockPlan>())).Do(call =>
            {
                if (!ReferenceEquals(HeldPlan, call.Arg<IdentityMutationLockPlan>())) throw new IdentityMutationLockException();
            });
            var declarations = Substitute.For<ISeedTenantContinuationSource>();
            declarations.GetDeclaration(Arg.Any<SeedTenantContinuationAct>()).Returns(call =>
                admitted && call.Arg<SeedTenantContinuationAct>().TenantId == Tenant ? Declaration : null);
            OperationAuthorizationCoordinator? current = null;
            var execution = Substitute.For<IOperationInvocationContext>();
            execution.CurrentInvocation.Returns(_ => current?.CurrentInvocation);
            Prerequisites.VerifyAsync(Arg.Any<object>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(true);
            Customization.IsCompleteAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(true);
            Guard = new SeedTenantWriteGuard(declarations, State, Locks, execution, context, Unit, Prerequisites, Customization);
            _source = new(context);
            var evidence = new AuthorizationEvidenceCoordinator(Substitute.For<IAuditStore>(), new AuditStateCapture(), Unit,
                Clock, guids, () => current!, [], NullLogger<AuthorizationEvidenceCoordinator>.Instance);
            _coordinator = current = new(new OperationPolicyCatalog([new TenancyOperationPolicySource(), new IdentityOperationPolicySource()]),
                Substitute.For<IProtectedOperationAuthorizer>(), context, [], [_source], evidence);
            var services = new ServiceCollection();
            services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(ITenantWriteStore).Assembly));
            services.AddSingleton(context);
            services.AddSingleton(Tenants);
            services.AddSingleton(Settings);
            services.AddSingleton(Organizations);
            services.AddSingleton(Hosts);
            services.AddSingleton(Unit);
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IGuidFactory>(guids);
            services.AddSingleton(Substitute.For<IAuditSubject>());
            var tenants = Substitute.For<ITenantExistenceReader>();
            tenants.ExistsAsync(Arg.Any<CancellationToken>()).Returns(true);
            services.AddSingleton(tenants);
            var organizations = Substitute.For<IOrganizationScopeValidator>();
            organizations.BelongsToTenantAsync(Tenant, Default, Arg.Any<CancellationToken>()).Returns(true);
            services.AddSingleton(organizations);
            services.AddSingleton<IReservedHostRegistry>(NoReservedHosts.Instance);
            services.AddSingleton<IHostResolutionInvalidator>(NullHostResolutionInvalidator.Instance);
            services.AddSingleton(Guard);
            _provider = services.BuildServiceProvider();
        }
        public ISeedCustomizationPrerequisiteReader Customization { get; } = Substitute.For<ISeedCustomizationPrerequisiteReader>();
        public SeedTenantWriteGuard Guard { get; }
        public IFixedSeedPrerequisiteVerifier Prerequisites { get; } = Substitute.For<IFixedSeedPrerequisiteVerifier>();
        public TenantRoot Root { get; }
        public ISeedStateReader State { get; } = Substitute.For<ISeedStateReader>();
        public IIdentityMutationLocks Locks { get; } = Substitute.For<IIdentityMutationLocks>();
        public IUnitOfWork Unit { get; } = Substitute.For<IUnitOfWork>();
        public ITenantWriteStore Tenants { get; } = Substitute.For<ITenantWriteStore>();
        public ITenantSettingWriteStore Settings { get; } = Substitute.For<ITenantSettingWriteStore>();
        public IOrganizationWriteStore Organizations { get; } = Substitute.For<IOrganizationWriteStore>();
        public IPlatformHostMappingStore Hosts { get; } = Substitute.For<IPlatformHostMappingStore>();
        public IdentityMutationLockPlan? HeldPlan { get; set; }
        public Action? AfterLock { get; set; }
        public void RevokeSource() => _source.Dispose();
        public AddTenantLocaleCommand NextLocale() => new(Root.Version, "tr", true, false, 1);
        public IRequestHandler<TRequest, Result<T>> Handler<TRequest, T>() where TRequest : notnull, IRequest<Result<T>> =>
            _provider.GetRequiredService<IRequestHandler<TRequest, Result<T>>>();
        public Task<Result<T>> SendAsync<TRequest, T>(TRequest request) where TRequest : notnull, IRequest<Result<T>> =>
            _coordinator.ExecuteRequestAsync<TRequest, Result<T>>(request, () => Handler<TRequest, T>().Handle(request, default));
        public async Task<bool> IdentityContinuationAsync()
        {
            var allowed = false;
            var request = new InitializeSeedRoleCommand(Guid.CreateVersion7(), BuiltInRoleKey.Instructor, PermissionScope.Tenant, 1, "test");
            var result = await _coordinator.ExecuteRequestAsync<InitializeSeedRoleCommand, Result<None>>(request, async () =>
            {
                allowed = await Guard.AllowsIdentityContinuationAsync(Tenant);
                return Result.Ok(None.Value);
            });
            Assert.True(result.IsSuccess);
            return allowed;
        }
        public void Dispose()
        {
            _coordinator.Dispose();
            _source.Dispose();
            _provider.Dispose();
        }
        private static OrganizationSeedDto Organization(SeedOrganizationDeclaration declaration) =>
            new(declaration.Id, Tenant, declaration.Slug, declaration.DisplayName, "Active", CreatedAt: Clock.UtcNow, CreatedBy: UserId.SystemActor);
        private static TenantSeedDto Snapshot(TenantRoot root) => new(root.Id, root.Slug, root.DisplayName, root.Status.ToString(),
            root.DefaultOrganizationId, root.Version, [.. root.Locales.Select(locale => new TenantLocaleSeedDto(locale.Locale,
                locale.IsEnabled, locale.IsDefault, locale.Sort))], root.CreatedAt, root.CreatedBy, root.UpdatedAt, root.UpdatedBy,
            root.DeletedAt, root.DeletedBy, root.AdoptedSeedOriginVersion);
    }

    private sealed class TrustedSource(ITenantContext context) : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext current) =>
            CanIssue && ReferenceEquals(context, current) ? ForOperation(AuthorizationSourceKind.Seed, requestType, current) : null;
        protected override bool IsEvidenceCurrent(object? evidence) => true;
    }
}

/// <summary>Test-only exact declaration and real invocation boundary; it supplies no production fallback.</summary>
internal sealed class TenancySeedHandlerOwner : IDisposable
{
    internal TenancySeedHandlerOwner(ITenantContext context, IUnitOfWork unit, IClock clock)
    {
        var catalog = new OperationPolicyCatalog([new TenancyOperationPolicySource()]);
        _source = new FixtureTrustedSource(context, catalog);
        OperationAuthorizationCoordinator? coordinator = null;
        var evidence = new AuthorizationEvidenceCoordinator(Substitute.For<IAuditStore>(), new AuditStateCapture(), unit,
            clock, new SystemGuidFactory(), () => coordinator!, [], NullLogger<AuthorizationEvidenceCoordinator>.Instance);
        Coordinator = coordinator = new(catalog, Substitute.For<IProtectedOperationAuthorizer>(), context, [], [_source], evidence);
    }

    private readonly FixtureTrustedSource _source;
    internal OperationAuthorizationCoordinator Coordinator { get; }

    internal Task<Result<T>> ExecuteAsync<TRequest, T>(TRequest request, IRequestHandler<TRequest, Result<T>> handler,
        CancellationToken cancellationToken = default) where TRequest : notnull, IRequest<Result<T>> =>
        Coordinator.ExecuteRequestAsync<TRequest, Result<T>>(request, () => handler.Handle(request, cancellationToken), cancellationToken);

    internal static void Register(ServiceCollection services, ITenantContext context, SeedTenantDeclaration declaration,
        ISeedStateReader state, IClock clock, IUnitOfWork? unit = null)
    {
        unit ??= Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<DbTransaction>();
        unit.HasActiveTransaction.Returns(true);
        unit.Transaction.Returns(transaction);
        unit.Mode.Returns(TransactionMode.ReadWrite);
        var announcement = new TenantContextAnnouncement(1, declaration.TenantId,
            context.IsResolved ? context.OrganizationId : null);
        unit.GetTenantContextAnnouncement(transaction).Returns(announcement);
        var locks = new FixtureLocks();
        services.AddSingleton(context);
        services.AddSingleton(unit);
        services.AddSingleton(state);
        services.AddSingleton<IIdentityMutationLocks>(locks);
        services.AddSingleton<ISeedTenantContinuationSource>(new FixtureDeclarations(declaration));
        services.AddSingleton(provider => new TenancySeedHandlerOwner(context, unit, clock));
        services.AddSingleton<IOperationInvocationContext>(provider => provider.GetRequiredService<TenancySeedHandlerOwner>().Coordinator);
        var prerequisites = Substitute.For<IFixedSeedPrerequisiteVerifier>();
        prerequisites.VerifyAsync(Arg.Any<object>(), Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(true);
        services.AddSingleton(prerequisites);
        var customization = Substitute.For<ISeedCustomizationPrerequisiteReader>();
        customization.IsCompleteAsync(Arg.Any<IdentityMutationLockPlan>(), Arg.Any<CancellationToken>()).Returns(true);
        services.AddSingleton(customization);
        services.AddSingleton<SeedTenantWriteGuard>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TenancySeedInvocationBehavior<,>));
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        _source.Dispose();
    }

    private sealed class FixtureLocks : IIdentityMutationLocks
    {
        private IdentityMutationLockPlan? _plan;
        public Task AcquireAsync(IdentityMutationLockPlan plan, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!plan.UserIds.IsEmpty || plan.TenantMode != IdentityTenantLockMode.Exclusive)
                throw new IdentityMutationLockException();
            _plan = plan;
            return Task.CompletedTask;
        }
        public void RequireCurrent(IdentityMutationLockPlan plan)
        {
            if (!ReferenceEquals(_plan, plan)) throw new IdentityMutationLockException();
        }
    }

    private sealed class FixtureDeclarations(SeedTenantDeclaration declaration) : ISeedTenantContinuationSource
    {
        public SeedTenantDeclaration? GetDeclaration(SeedTenantContinuationAct act) => act.TenantId == declaration.TenantId
            && (act.Operation switch
            {
                SeedTenantContinuationOperation.ProvisionTenant or SeedTenantContinuationOperation.IdentityFoundation =>
                    act.TargetId is null && act.Locale is null && act.Host is null,
                SeedTenantContinuationOperation.CreateOrganization => act.TargetId == declaration.SecondOrganization.Id.Value
                    && act.Locale is null && act.Host is null,
                SeedTenantContinuationOperation.MapHost => act.Host == declaration.PublicHost && act.TargetId is null && act.Locale is null,
                SeedTenantContinuationOperation.AddLocale => act.TargetId is null && act.Host is null
                    && declaration.OriginalLocales.Any(locale => locale.Locale == act.Locale),
                SeedTenantContinuationOperation.SetBranding => act.TargetId == declaration.ThemeId && act.TargetId is not null
                    && act.Locale is null && act.Host is null,
                _ => false,
            }) ? declaration : null;
    }

    private sealed class FixtureTrustedSource(ITenantContext context, IOperationPolicyCatalog catalog)
        : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
    {
        public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext current) =>
            CanIssue && ReferenceEquals(context, current) && catalog.TryGet(requestType, out var policy)
            && policy.Kind == OperationPolicyKind.FiniteTrusted && policy.AllowedSources.Length == 1
            && policy.AllowedSources[0] == AuthorizationSourceKind.Seed
                ? ForOperation(AuthorizationSourceKind.Seed, requestType, current) : null;
        protected override bool IsEvidenceCurrent(object? evidence) => true;
    }
}

internal sealed class TenancySeedInvocationBehavior<TRequest, TResponse>(TenancySeedHandlerOwner owner)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull where TResponse : IResultBase
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        owner.Coordinator.ExecuteRequestAsync(request, () => next(), cancellationToken);
}
