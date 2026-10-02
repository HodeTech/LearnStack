using FluentAssertions;
using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
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
        var handler = provider.GetRequiredService<IRequestHandler<SetDefaultTenantLocaleCommand, Result<TenantLocalesDto>>>();
        var result = await handler.Handle(new(version, "en"), default);
        result.Error!.Code.Should().Be("validation_failed");
        tenant.Version.Should().Be(version);
        tenant.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("tr-TR");
        var stale = await handler.Handle(new(version - 1, "tr-TR"), default);
        stale.Error!.Code.Should().Be("concurrency_conflict");
        tenant.Version.Should().Be(version);
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
        var result = await provider.GetRequiredService<IRequestHandler<AddTenantLocaleCommand, Result<TenantLocalesDto>>>()
            .Handle(new(tenant.Version, "en", true, false, 1), default);
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
        var send = async () => await provider.GetRequiredService<IRequestHandler<AddTenantLocaleCommand, Result<TenantLocalesDto>>>()
            .Handle(new(tenant.Version, "en", true, false, 1), default);
        (await send.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>()).Which.Should().BeSameAs(error);
    }

    [Fact]
    public async Task Invalid_theme_never_reaches_the_store()
    {
        using var provider = Build();
        var result = await provider.GetRequiredService<IRequestHandler<SetTenantBrandingCommand, Result<TenantBrandingDto>>>()
            .Handle(new(Guid.CreateVersion7(), "{}", null), default);
        result.Error!.Code.Should().Be("validation_failed");
        await provider.GetRequiredService<ITenantSettingWriteStore>().DidNotReceiveWithAnyArgs().FindAsync(TenantSettingId.From(Guid.CreateVersion7()));
        provider.GetRequiredService<IUnitOfWork>().DidNotReceive().MarkRollbackOnly();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pk_tenant_settings")]
    [InlineData("ux_tenant_settings_tenant_id_organization_id_key")]
    public async Task Failed_setting_create_or_replace_marks_the_unit_rollback_only(string? constraint)
    {
        using var provider = Build();
        var store = provider.GetRequiredService<ITenantSettingWriteStore>();
        var id = TenantSettingId.From(Guid.CreateVersion7());
        Exception error = constraint is null ? new AggregateConcurrencyException("race") : new AggregateConflictException("race", constraint);
        long? version = null;
        if (constraint is null)
        {
            var setting = TenantSetting.Create(id, TenantId, null, BrandingThemeRegistry.SettingKey, Theme, Clock, UserId.SystemActor);
            store.FindAsync(id).Returns(setting);
            store.UpdateAsync(setting).Returns(Task.FromException(error));
            version = setting.Version;
        }
        else
        {
            store.AddAsync(Arg.Any<TenantSetting>()).Returns(Task.FromException(error));
        }

        var result = await provider.GetRequiredService<IRequestHandler<SetTenantBrandingCommand, Result<TenantBrandingDto>>>()
            .Handle(new(id.Value, Theme, version), default);
        result.Error!.Code.Should().Be(constraint is null ? "concurrency_conflict" : "business_rule_violation");
        provider.GetRequiredService<IUnitOfWork>().Received(1).MarkRollbackOnly();
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
        root.AddLocale("tr-TR", false, Clock, UserId.SystemActor);
        return root;
    }

    private static ServiceProvider Build(bool resolved = true, bool scoped = false)
    {
        var services = new ServiceCollection();
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(ITenantWriteStore).Assembly));
        services.AddValidatorsFromAssembly(typeof(ITenantWriteStore).Assembly, includeInternalTypes: true);
        services.AddSingleton(Substitute.For<ITenantWriteStore>());
        services.AddSingleton(Substitute.For<ITenantSettingWriteStore>());
        var tenants = Substitute.For<ITenantExistenceReader>();
        tenants.ExistsAsync(Arg.Any<CancellationToken>()).Returns(true);
        services.AddSingleton(tenants);
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IAuditSubject>());
        services.AddSingleton<IClock>(Clock);
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(resolved);
        context.TenantId.Returns(TenantId);
        context.OrganizationId.Returns(scoped ? OrganizationId.From(Guid.CreateVersion7()) : null);
        services.AddSingleton(context);
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
