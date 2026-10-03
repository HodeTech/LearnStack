using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Settings;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantSettingsAccessorTests(SchemaFixture schema)
{
    private const string Theme = """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""";
    private static readonly TenantSettingKey<Pair> PairKey = new("test.read-pair");

    [Theory]
    [InlineData(false, "Europe/Istanbul")]
    [InlineData(true, null)]
    public async Task Registered_reads_keep_tenant_data_separate(bool foreign, string? expected)
    {
        var context = new Context(TenantId.From(foreign ? SchemaFixture.TenantB : SchemaFixture.TenantA));
        await ReadInScopeAsync(context, async (db, unit, services) =>
        {
            await AssertAppRoleAsync(unit);
            var token = new TenantSettingKey<Text>("tz");
            var other = new TenantSettingKey<Text>("beta-only");
            var reader = new TenantSettingsAccessor(db, context, unit, new TenantSettingRegistry(
            [new TenantSettingRegistration<Text>(token, true, ReadText),
             new TenantSettingRegistration<Text>(other, true, ReadText)]));
            (await reader.ReadAsync(token)).Value!.Value.Should().Be(expected is null ? null : new Text(expected));
            (await reader.ReadAsync(other)).Value!.Value.Should().Be(foreign ? new Text("visible to beta alone") : null);
        });
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "main")]
    [InlineData(2, "branch")]
    public async Task Organization_scope_selects_only_its_own_override_even_with_the_tenant_hatch(
        int organization, string? expected)
    {
        var org = organization switch
        {
            1 => OrganizationId.From(SchemaFixture.OrgA1),
            2 => OrganizationId.From(SchemaFixture.OrgA2),
            _ => (OrganizationId?)null
        };
        var context = new Context(TenantId.From(SchemaFixture.TenantA), org);
        await ReadInScopeAsync(context, async (db, unit, services) =>
        {
            await using var hatch = new NpgsqlCommand("SELECT set_config('app.scope', 'tenant', true)",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
            await hatch.ExecuteNonQueryAsync();
            // Positive control: RLS now admits both sibling rows, still as learnstack_app.
            await using var control = new NpgsqlCommand("SELECT count(*) FROM tenant_settings WHERE key = 'theme'",
                (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
            ((long)(await control.ExecuteScalarAsync())!).Should().Be(2);
            var token = new TenantSettingKey<Text>("theme");
            var reader = new TenantSettingsAccessor(db, context, unit, new TenantSettingRegistry(
            [new TenantSettingRegistration<Text>(token, true, ReadText)]));
            (await reader.ReadAsync(token)).Value!.Value.Should().Be(expected is null ? null : new Text(expected));
        });
    }

    [Fact]
    public async Task Override_is_a_whole_value_and_invalid_selected_data_is_not_tenant_fallback()
    {
        var context = new Context(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));
        await ReadInScopeAsync(context, async (db, unit, services) =>
        {
            // Fixture preparation obeys exact write scope; the read still runs as its org.
            await SchemaQueries.SetSettingAsync(unit.Connection, unit.Transaction!, "app.organization_id", "");
            var tenant = Add(db, context, PairKey.Value, """{"left":"tenant","right":"base"}""", null);
            await db.SaveChangesAsync();
            await SchemaQueries.SetSettingAsync(unit.Connection, unit.Transaction!, "app.organization_id", context.OrganizationId!.Value.Value.ToString());
            var organization = Add(db, context, PairKey.Value, """{"left":"organization","right":"own"}""", context.OrganizationId);
            await db.SaveChangesAsync();
            var reader = PairReader(db, context, unit);
            (await reader.ReadAsync(PairKey)).Value!.Value.Should().Be(new Pair("organization", "own"));
            organization.SetValue("""{"left":"partial"}""", new SystemClock(), UserId.SystemActor);
            await db.SaveChangesAsync();
            var invalid = await reader.ReadAsync(PairKey);
            invalid.Error!.Code.Should().Be("validation_failed");
            invalid.Error.Details!["Setting"].Single().Key.Should().Be("lockey_invalid_value");
            tenant.Value.Should().Contain("base");
        });
    }

    [Fact]
    public async Task Reads_are_uncached_observe_saved_changes_and_exclude_soft_deleted_rows()
    {
        var context = new Context(TenantId.From(SchemaFixture.TenantA));
        await ReadInScopeAsync(context, async (db, unit, services) =>
        {
            var reader = PairReader(db, context, unit);
            (await reader.ReadAsync(PairKey)).Value!.IsPresent.Should().BeFalse();
            var setting = Add(db, context, PairKey.Value, """{"left":"first","right":"whole"}""", null);
            await db.SaveChangesAsync();
            (await reader.ReadAsync(PairKey)).Value!.Value.Should().Be(new Pair("first", "whole"));
            setting.SetValue("""{"left":"second","right":"changed"}""", new SystemClock(), UserId.SystemActor);
            await db.SaveChangesAsync();
            (await reader.ReadAsync(PairKey)).Value!.Value.Should().Be(new Pair("second", "changed"));
            setting.SoftDelete(new SystemClock().UtcNow, UserId.SystemActor);
            await db.SaveChangesAsync();
            (await reader.ReadAsync(PairKey)).Value!.IsPresent.Should().BeFalse();
            (await reader.ReadAsync(new TenantSettingKey<Pair>("tz"))).IsFailure.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Branding_registration_stays_tenant_wide_and_returns_a_complete_validated_palette()
    {
        var context = new Context(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));
        await ReadInScopeAsync(context, async (db, unit, services) =>
        {
            await SchemaQueries.SetSettingAsync(unit.Connection, unit.Transaction!, "app.organization_id", "");
            var tenant = Add(db, context, BrandingThemeRegistry.SettingKey, Theme, null);
            await db.SaveChangesAsync();
            await SchemaQueries.SetSettingAsync(unit.Connection, unit.Transaction!, "app.organization_id", context.OrganizationId!.Value.Value.ToString());
            Add(db, context, BrandingThemeRegistry.SettingKey, "{}", context.OrganizationId);
            await db.SaveChangesAsync();
            // Resolve the actual Seeder composition registration, not a second test graph.
            var reader = services.GetRequiredService<ITenantSettingsAccessor>();
            (await reader.ReadAsync(TenantSettingKeys.BrandingTheme)).Value!.Value.Should()
                .Be(new BrandingTheme("#2345aa", "#ffffff", "#111111", "#555555"));
            await SchemaQueries.SetSettingAsync(unit.Connection, unit.Transaction!, "app.organization_id", "");
            tenant.SetValue("{}", new SystemClock(), UserId.SystemActor);
            await db.SaveChangesAsync();
            await SchemaQueries.SetSettingAsync(unit.Connection, unit.Transaction!, "app.organization_id", context.OrganizationId!.Value.Value.ToString());
            (await reader.ReadAsync(TenantSettingKeys.BrandingTheme)).IsFailure.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Malformed_key_values_are_refused_and_a_detached_context_is_not_admitted()
    {
        var context = new Context(TenantId.From(SchemaFixture.TenantA));
        await ReadInScopeAsync(context, async (db, unit, services) =>
        {
            var reader = services.GetRequiredService<ITenantSettingsAccessor>();
            foreach (var value in new[] { null, "" })
            {
                var refused = await reader.ReadAsync(new TenantSettingKey<BrandingTheme>(value!));
                refused.Error!.Code.Should().Be("validation_failed");
                refused.Error.Details!["Setting"].Single().Key.Should().Be("lockey_invalid_value");
            }
            await using var detached = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(unit.Connection).Options, new StaticTenantContextAccessor(context));
            var detachedReader = new TenantSettingsAccessor(detached, context, unit, TenantSettingRegistry.Default);
            Func<Task> read = () => detachedReader.ReadAsync(TenantSettingKeys.BrandingTheme);
            await read.Should().ThrowAsync<TenantContextMissingException>();
            // Positive control: the same announced unit admits its enlisted context.
            (await reader.ReadAsync(TenantSettingKeys.BrandingTheme)).IsSuccess.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Missing_announcement_and_cancellation_are_loud_before_a_settings_query()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        var context = new Context(TenantId.From(SchemaFixture.TenantA));
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        var reader = scope.ServiceProvider.GetRequiredService<ITenantSettingsAccessor>();
        Func<Task> read = () => reader.ReadAsync(TenantSettingKeys.BrandingTheme);
        await read.Should().ThrowAsync<TenantContextMissingException>();
        await unit.SetTenantContextAsync(context);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        read = () => reader.ReadAsync(TenantSettingKeys.BrandingTheme, cancelled.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        await frame.FailAsync();
        read = () => reader.ReadAsync(TenantSettingKeys.BrandingTheme);
        await read.Should().ThrowAsync<TenantContextMissingException>();
    }

    private async Task ReadInScopeAsync(Context context, Func<TenancyDbContext, IUnitOfWork, IServiceProvider, Task> act)
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await act(db, unit, scope.ServiceProvider);
        await frame.FailAsync();
    }

    private static TenantSetting Add(TenancyDbContext db, Context context, string key, string value, OrganizationId? org)
    {
        var setting = TenantSetting.Create(TenantSettingId.From(Guid.CreateVersion7()), context.TenantId, org,
            key, value, new SystemClock(), UserId.SystemActor);
        db.TenantSettings.Add(setting);
        return setting;
    }

    private static TenantSettingsAccessor PairReader(TenancyDbContext db, Context context, IUnitOfWork unit) =>
        new(db, context, unit, new TenantSettingRegistry(
        [new TenantSettingRegistration<Pair>(PairKey, true, ReadPair)]));

    private static Result<Pair> ReadPair(string value)
    {
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        return root.TryGetProperty("left", out var left) && root.TryGetProperty("right", out var right)
            ? Result.Ok(new Pair(left.GetString()!, right.GetString()!))
            : Result<Pair>.Fail(new Error(new LocalizedMessage("lockey_validation_failed")));
    }

    private static Result<Text> ReadText(string value) => Result.Ok(new Text(JsonSerializer.Deserialize<string>(value)!));

    private static async Task AssertAppRoleAsync(IUnitOfWork unit)
    {
        await using var command = new NpgsqlCommand("SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be("learnstack_app");
        row.GetBoolean(1).Should().BeFalse();
        row.GetBoolean(2).Should().BeFalse();
    }

    private sealed record Pair(string Left, string Right);
    private sealed record Text(string Value);
    private sealed record Context(TenantId TenantId, OrganizationId? OrganizationId = null) : ITenantContext
    {
        public bool IsResolved => true;
        public UserId? UserId => null;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => "tenancy";
    }
}
