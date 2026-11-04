using FluentAssertions;
using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
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

public sealed class TenantWriterTests
{
    private const string Theme = """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""";
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 2, 1, 0, 0, TimeSpan.Zero));
    private static readonly TenantId TenantId = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId First = OrganizationId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Second = OrganizationId.From(Guid.CreateVersion7());
    private static readonly TenantSettingId ThemeId = TenantSettingId.From(Guid.CreateVersion7());

    [Theory]
    [InlineData(0, false, false, "tenant_mismatch")]
    [InlineData(1, false, false, "tenant_mismatch")]
    [InlineData(2, false, false, "tenant_mismatch")]
    [InlineData(0, true, true, "resource_scope_violation")]
    [InlineData(1, true, true, "resource_scope_violation")]
    [InlineData(2, true, true, "resource_scope_violation")]
    public async Task Tenant_wide_guards_precede_any_read_or_write(int operation, bool resolved, bool scoped, string code)
    {
        using var provider = Build(resolved, scoped);
        var result = await HandleAsync(provider, operation, 0);
        result.Error!.Code.Should().Be(code);
        await provider.GetRequiredService<ITenantWriteStore>().DidNotReceiveWithAnyArgs().FindAsync(TenantId);
        await provider.GetRequiredService<ITenantExistenceReader>().DidNotReceiveWithAnyArgs().ExistsAsync(default);
        await provider.GetRequiredService<ITenantSettingWriteStore>().DidNotReceiveWithAnyArgs().FindAsync(TenantSettingId.From(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Disabled_promotion_and_stale_versions_leave_root_and_capture_unchanged()
    {
        using var provider = Build();
        var tenant = Root();
        tenant.AddLocale("en", false, Clock, UserId.SystemActor, isEnabled: false);
        provider.GetRequiredService<ITenantWriteStore>().FindAsync(TenantId).Returns(tenant);
        var version = tenant.Version;
        var promote = () => tenant.SetDefaultLocale("en", Clock, UserId.SystemActor);
        promote.Should().Throw<InvalidOperationException>("a disabled locale cannot be promoted by any caller");
        tenant.Version.Should().Be(version);
        tenant.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("tr-TR");
        var result = await provider.GetRequiredService<ISender>().Send(new SetDefaultTenantLocaleCommand(version, "en"));
        result.Error!.Code.Should().Be("business_rule_violation", "there is no original seed promotion act");
        tenant.Version.Should().Be(version);
        var pristine = Root();
        provider.GetRequiredService<ITenantWriteStore>().FindAsync(TenantId).Returns(pristine);
        var stale = await provider.GetRequiredService<ISender>().Send(new AddTenantLocaleCommand(pristine.Version - 1, "en", true, false, 1));
        stale.Error!.Code.Should().Be("concurrency_conflict", "a valid declared act with a stale post-fence version refuses as changed evidence");
        pristine.Version.Should().Be(2);
        pristine.Locales.Should().ContainSingle().Which.Locale.Should().Be("tr-TR");
        await provider.GetRequiredService<ITenantWriteStore>().DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        provider.GetRequiredService<IUnitOfWork>().DidNotReceive().MarkRollbackOnly();
    }

    [Theory]
    [InlineData(false, "pk_tenant_locales")]
    [InlineData(false, "ux_tenant_locales_tenant_id_is_default")]
    [InlineData(true, null)]
    public async Task A_save_failure_after_locale_mutation_poison_marks_an_absorbable_nested_frame(bool stale, string? constraint)
    {
        using var provider = Build();
        var tenant = Root();
        var store = provider.GetRequiredService<ITenantWriteStore>();
        store.FindAsync(TenantId).Returns(tenant);
        Exception error = stale ? new AggregateConcurrencyException("race") : new AggregateConflictException("race", constraint);
        store.UpdateAsync(tenant).Returns(Task.FromException(error));
        var result = await provider.GetRequiredService<ISender>().Send(new AddTenantLocaleCommand(tenant.Version, "en", true, false, 1));
        result.Error!.Code.Should().Be(stale ? "concurrency_conflict" : "business_rule_violation");
        provider.GetRequiredService<IUnitOfWork>().Received(1).MarkRollbackOnly();
    }

    [Fact]
    public async Task Unexpected_store_fault_is_propagated_instead_of_becoming_a_business_refusal()
    {
        using var provider = Build();
        var tenant = Root();
        var store = provider.GetRequiredService<ITenantWriteStore>();
        store.FindAsync(TenantId).Returns(tenant);
        var error = new Microsoft.EntityFrameworkCore.DbUpdateException("unexpected constraint");
        store.UpdateAsync(tenant).Returns(Task.FromException(error));
        var send = async () => await provider.GetRequiredService<ISender>().Send(new AddTenantLocaleCommand(tenant.Version, "en", true, false, 1));
        (await send.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>()).Which.Should().BeSameAs(error);
    }

    [Fact]
    public async Task Invalid_theme_never_reaches_the_store()
    {
        // Even a mistakenly declared fixed palette must pass the handler's independent grammar.
        using var provider = Build(completeLocales: true, declaredTheme: "{}");
        var result = await provider.GetRequiredService<ISender>().Send(new SetTenantBrandingCommand(ThemeId.Value, "{}", null));
        result.Error!.Code.Should().Be("validation_failed");
        await provider.GetRequiredService<ITenantSettingWriteStore>().DidNotReceiveWithAnyArgs().FindAsync(TenantSettingId.From(Guid.CreateVersion7()));
        provider.GetRequiredService<IUnitOfWork>().DidNotReceive().MarkRollbackOnly();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pk_tenant_settings")]
    [InlineData("ux_tenant_settings_tenant_id_organization_id_key")]
    public async Task Failed_setting_creation_marks_the_unit_rollback_only(string? constraint)
    {
        using var provider = Build(completeLocales: true);
        var store = provider.GetRequiredService<ITenantSettingWriteStore>();
        Exception error = constraint is null ? new AggregateConcurrencyException("race") : new AggregateConflictException("race", constraint);
        store.AddAsync(Arg.Any<TenantSetting>()).Returns(Task.FromException(error));
        var result = await provider.GetRequiredService<ISender>().Send(new SetTenantBrandingCommand(ThemeId.Value, Theme, null));
        result.Error!.Code.Should().Be(constraint is null ? "concurrency_conflict" : "business_rule_violation");
        provider.GetRequiredService<IUnitOfWork>().Received(1).MarkRollbackOnly();
    }

    [Fact]
    public async Task Domain_theme_replacement_retains_stamping_but_the_seed_request_cannot_replace_a_stored_root()
    {
        using var provider = Build(completeLocales: true);
        var setting = TenantSetting.Create(ThemeId, TenantId, null, BrandingThemeRegistry.SettingKey, Theme, Clock, UserId.SystemActor);
        var version = setting.Version;
        var replacement = Theme.Replace("#2345aa", "#1d4ed8", StringComparison.Ordinal);
        var seed = await provider.GetRequiredService<ISender>().Send(new SetTenantBrandingCommand(ThemeId.Value, replacement, version));
        seed.Error!.Code.Should().Be("business_rule_violation");
        setting.Value.Should().Be(Theme);
        setting.Version.Should().Be(version);
        await provider.GetRequiredService<ITenantSettingWriteStore>().DidNotReceiveWithAnyArgs().UpdateAsync(setting);
        provider.GetRequiredService<IUnitOfWork>().DidNotReceive().MarkRollbackOnly();
        // The business operation remains available to the separately classified human writer.
        setting.SetValue(replacement, Clock, UserId.SystemActor);
        setting.Value.Should().Be(replacement);
        setting.Version.Should().Be(version + 1);
        setting.UpdatedAt.Should().Be(Clock.UtcNow);
        setting.UpdatedBy.Should().Be(UserId.SystemActor);
        setting.Id.Should().Be(ThemeId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tr_TR")]
    [InlineData("not a locale")]
    public void Locale_shape_is_refused_by_both_command_validators(string? locale)
    {
        using var provider = Build();
        provider.GetRequiredService<IValidator<AddTenantLocaleCommand>>().Validate(new(0, locale!, true, false, 0))
            .IsValid.Should().BeFalse();
        provider.GetRequiredService<IValidator<SetDefaultTenantLocaleCommand>>().Validate(new(0, locale!))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Negative_sort_disabled_default_and_unassigned_setting_identifier_are_validation_failures()
    {
        using var provider = Build();
        var validator = provider.GetRequiredService<IValidator<AddTenantLocaleCommand>>();
        validator.Validate(new(0, "en", true, false, -1)).IsValid.Should().BeFalse();
        validator.Validate(new(0, "en", false, true, 0)).IsValid.Should().BeFalse();
        validator.Validate(new(-1, "en", true, false, 0)).IsValid.Should().BeFalse();
        provider.GetRequiredService<IValidator<SetTenantBrandingCommand>>().Validate(new(Guid.Empty, Theme, null))
            .IsValid.Should().BeFalse();
        provider.GetRequiredService<IValidator<SetTenantBrandingCommand>>().Validate(new(Guid.CreateVersion7(), Theme, -1))
            .IsValid.Should().BeFalse();
    }

    private static TenantRoot Root()
    {
        var root = TenantRoot.Create(TenantId, "probe", "Probe", Clock, UserId.SystemActor);
        root.AssignDefaultOrganization(First, Clock, new SystemGuidFactory(), UserId.SystemActor);
        root.AddLocale("tr-TR", false, Clock, UserId.SystemActor);
        return root;
    }

    private static ServiceProvider Build(bool resolved = true, bool scoped = false, bool completeLocales = false, string declaredTheme = Theme)
    {
        var services = new ServiceCollection();
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(ITenantWriteStore).Assembly));
        services.AddValidatorsFromAssembly(typeof(ITenantWriteStore).Assembly, includeInternalTypes: true);
        var tenantStore = Substitute.For<ITenantWriteStore>();
        var settingStore = Substitute.For<ITenantSettingWriteStore>();
        var root = Root();
        if (completeLocales) root.AddLocale("en", false, Clock, UserId.SystemActor, sort: 1);
        tenantStore.FindAsync(TenantId).Returns(root);
        tenantStore.FindForUpdateAsync(TenantId, Arg.Any<CancellationToken>()).Returns(call =>
            tenantStore.FindAsync(TenantId, call.Arg<CancellationToken>()));
        settingStore.FindForUpdateAsync(ThemeId, Arg.Any<CancellationToken>()).Returns(call =>
            settingStore.FindAsync(ThemeId, call.Arg<CancellationToken>()));
        services.AddSingleton(tenantStore);
        services.AddSingleton(settingStore);
        var tenants = Substitute.For<ITenantExistenceReader>();
        tenants.ExistsAsync(Arg.Any<CancellationToken>()).Returns(true);
        services.AddSingleton(tenants);
        var unit = Substitute.For<IUnitOfWork>();
        services.AddSingleton(unit);
        services.AddSingleton(Substitute.For<IAuditSubject>());
        services.AddSingleton<IClock>(Clock);
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(resolved);
        context.Origin.Returns(resolved ? TenantContextOrigin.Ambient : null);
        context.TenantId.Returns(TenantId);
        context.OrganizationId.Returns(scoped ? OrganizationId.From(Guid.CreateVersion7()) : null);
        services.AddSingleton(context);
        var declaration = new SeedTenantDeclaration(TenantId, "probe", "Probe", new(First, "first", "First"), new(Second, "second", "Second"),
            [new("tr-TR", true, true, 0), new("en", true, false, 1)], "probe.example.test", null, ThemeId.Value, declaredTheme);
        var state = Substitute.For<ISeedStateReader>();
        state.ReadTenantAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var current = await tenantStore.FindAsync(TenantId, call.Arg<CancellationToken>());
            return current is null ? null : new TenantSeedDto(current.Id, current.Slug, current.DisplayName, current.Status.ToString(),
                current.DefaultOrganizationId, current.Version, [.. current.Locales.Select(locale => new TenantLocaleSeedDto(locale.Locale,
                    locale.IsEnabled, locale.IsDefault, locale.Sort))], current.CreatedAt, current.CreatedBy, current.UpdatedAt, current.UpdatedBy);
        });
        foreach (var organization in new[] { declaration.DefaultOrganization, declaration.SecondOrganization })
            state.ReadOrganizationAsync(organization.Id, Arg.Any<CancellationToken>()).Returns(new OrganizationSeedDto(
                organization.Id, TenantId, organization.Slug, organization.DisplayName, "Active", CreatedAt: Clock.UtcNow, CreatedBy: UserId.SystemActor));
        state.ReadHostMappingAsync(declaration.PublicHost, Arg.Any<CancellationToken>()).Returns(
            new HostMappingSeedDto(declaration.PublicHost, TenantId, null, true, true));
        TenancySeedHandlerOwner.Register(services, context, declaration, state, Clock, unit);
        return services.BuildServiceProvider();
    }

    private static async Task<IResultBase> HandleAsync(ServiceProvider provider, int operation, long version) => operation switch
    {
        0 => await provider.GetRequiredService<IRequestHandler<AddTenantLocaleCommand, Result<TenantLocalesDto>>>()
            .Handle(new(version, "en", true, false, 0), default),
        1 => await provider.GetRequiredService<IRequestHandler<SetDefaultTenantLocaleCommand, Result<TenantLocalesDto>>>()
            .Handle(new(version, "en"), default),
        _ => await provider.GetRequiredService<IRequestHandler<SetTenantBrandingCommand, Result<TenantBrandingDto>>>()
            .Handle(new(Guid.CreateVersion7(), Theme, null), default),
    };
}
