using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenancyWriterTests(SchemaFixture schema, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Theme = """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""";
    private const string OtherTheme = """{"primary":"#663399","background":"#ffffff","foreground":"#000000","muted":"#444444"}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_unique_constraints_remain_database_faults_and_roll_back(bool branding)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var foreign = await ProvisionAsync(source);
        var initial = await ReadTenantAsync(source, context);
        (await SendAsync(source, context, new AddTenantLocaleCommand(initial.Version, "en", true, true, 0))).IsSuccess.Should().BeTrue();
        if (branding)
        {
            (await SendAsync(source, foreign, new SetTenantBrandingCommand(Guid.CreateVersion7(), Theme, null)))
                .IsSuccess.Should().BeTrue();
        }

        await using (var owner = new NpgsqlConnection(database.MigrationConnectionString))
        await using (var index = new NpgsqlCommand(branding
            ? "CREATE UNIQUE INDEX ux_test_unowned ON tenant_settings (value)"
            : "CREATE UNIQUE INDEX ux_test_unowned ON tenant_locales (sort)", owner))
        {
            await owner.OpenAsync();
            await index.ExecuteNonQueryAsync();
        }

        var before = await ReadTenantAsync(source, context);
        var settingId = Guid.CreateVersion7();
        Func<Task> send = branding
            ? async () => { await SendAsync(source, context, new SetTenantBrandingCommand(settingId, Theme, null)); }
        : async () => { await SendAsync(source, context, new AddTenantLocaleCommand(before.Version, "fr", true, false, 0)); };
        var fault = (await send.Should().ThrowAsync<DbUpdateException>()).Which;
        fault.InnerException.Should().BeOfType<PostgresException>().Which.ConstraintName.Should().Be("ux_test_unowned");
        var problem = LearnStack.Api.Common.ProblemDetailsFactory.For(fault);
        problem.Status.Should().Be(500);
        problem.Extensions["code"].Should().Be("internal_error");
        problem.Extensions.Should().NotContainKey("errors");
        (await ReadTenantAsync(source, context)).Should().BeEquivalentTo(before);
        (await SendAsync(source, context, new GetSettingSeedStateQuery(settingId))).Value!.State.Should().BeNull();
        (await CountSuccessfulWritesAsync(source, context, "tenancy.locale.write")).Should().Be(1);
        (await CountSuccessfulWritesAsync(source, context, "tenancy.setting.write")).Should().Be(0);
    }

    [Fact]
    public async Task Locale_commands_promote_first_enabled_then_switch_existing_and_new_defaults_atomically()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var before = await ReadTenantAsync(source, context);
        var disabled = await SendAsync(source, context, new AddTenantLocaleCommand(before.Version, "fr", false, false, 0));
        disabled.IsSuccess.Should().BeTrue();
        disabled.Value!.Locales.Should().ContainSingle().Which.IsDefault.Should().BeFalse();
        var enabled = await SendAsync(source, context, new AddTenantLocaleCommand(disabled.Value.Version, "EN-us", true, false, 1));
        enabled.Value!.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("en-US");
        var additional = await SendAsync(source, context, new AddTenantLocaleCommand(enabled.Value.Version, "tr-TR", true, false, 2));
        additional.IsSuccess.Should().BeTrue();
        var switched = await SendAsync(source, context, new SetDefaultTenantLocaleCommand(additional.Value!.Version, "TR-tr"));
        switched.Value!.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("tr-TR");
        // An Added default must be held back too; EF inserts before clearing existing rows.
        var addedDefault = await SendAsync(source, context, new AddTenantLocaleCommand(switched.Value.Version, "de", true, true, 3));
        addedDefault.IsSuccess.Should().BeTrue();
        var final = await ReadTenantAsync(source, context);
        final.Version.Should().Be(before.Version + 5);
        final.Locales.Should().HaveCount(4);
        final.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("de");
        (await CountSuccessfulWritesAsync(source, context, "tenancy.locale.write")).Should().Be(5);
        await AssertLocaleAuditAsync(source, context);

        var refused = await SendAsync(source, context, new SetDefaultTenantLocaleCommand(final.Version, "fr"));
        refused.Error!.Code.Should().Be("validation_failed");
        (await ReadTenantAsync(source, context)).Should().BeEquivalentTo(final);
        (await CountSuccessfulWritesAsync(source, context, "tenancy.locale.write")).Should().Be(5);
        var duplicate = await SendAsync(source, context, new AddTenantLocaleCommand(final.Version, "DE", true, false, 4));
        duplicate.Error!.Code.Should().Be("business_rule_violation");
        (await ReadTenantAsync(source, context)).Should().BeEquivalentTo(final);
    }

    [Fact]
    public async Task Branding_is_create_or_exact_replace_only_and_cannot_touch_foreign_or_generic_settings()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var foreign = await ProvisionAsync(source);
        var tenantBefore = await ReadTenantAsync(source, context);
        var organizationContext = context with { Organization = context.DefaultOrganization };
        (await SendAsync(source, organizationContext, new AddTenantLocaleCommand(tenantBefore.Version, "en", true, true, 0)))
            .Error!.Code.Should().Be("resource_scope_violation");
        (await SendAsync(source, organizationContext, new SetDefaultTenantLocaleCommand(tenantBefore.Version, "en")))
            .Error!.Code.Should().Be("resource_scope_violation");
        (await ReadTenantAsync(source, context)).Should().BeEquivalentTo(tenantBefore);
        var id = Guid.CreateVersion7();
        var foreignId = Guid.CreateVersion7();
        (await SendAsync(source, context, new SetTenantBrandingCommand(id, Theme, null))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, foreign, new SetTenantBrandingCommand(foreignId, OtherTheme, null))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new SetTenantBrandingCommand(foreignId, Theme, 0))).Error!.Code.Should().Be("not_found");
        (await SendAsync(source, context with { Organization = context.DefaultOrganization }, new SetTenantBrandingCommand(id, Theme, 0)))
            .Error!.Code.Should().Be("resource_scope_violation");
        (await SendAsync(source, context, new SetTenantBrandingCommand(id, OtherTheme, null))).Error!.Code.Should().Be("business_rule_violation");
        (await SendAsync(source, context, new SetTenantBrandingCommand(Guid.CreateVersion7(), Theme, null)))
            .Error!.Code.Should().Be("business_rule_violation");
        var changed = await SendAsync(source, context, new SetTenantBrandingCommand(id, OtherTheme, 0));
        changed.Value!.Version.Should().Be(1);
        (await SendAsync(source, context, new SetTenantBrandingCommand(id, Theme, 0))).Error!.Code.Should().Be("concurrency_conflict");
        (await SendAsync(source, context, new SetTenantBrandingCommand(id, "{}", 1))).Error!.Code.Should().Be("validation_failed");
        var state = (await SendAsync(source, context, new GetSettingSeedStateQuery(id))).Value!.State!;
        JsonNode.DeepEquals(JsonNode.Parse(state.Value), JsonNode.Parse(changed.Value.Theme)).Should().BeTrue();
        state.Version.Should().Be(1);
        (await CountSuccessfulWritesAsync(source, context, "tenancy.setting.write")).Should().Be(2);
        var foreignState = (await SendAsync(source, foreign, new GetSettingSeedStateQuery(foreignId))).Value!.State!;
        JsonNode.DeepEquals(JsonNode.Parse(foreignState.Value), JsonNode.Parse(OtherTheme)).Should().BeTrue();
        foreignState.Version.Should().Be(0);

        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        var generic = TenantSetting.Create(TenantSettingId.From(Guid.CreateVersion7()), context.TenantId, null,
            "tz", "\"Europe/Istanbul\"", new SystemClock(), UserId.SystemActor);
        await scope.ServiceProvider.GetRequiredService<ITenantSettingWriteStore>().AddAsync(generic);
        var refused = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new SetTenantBrandingCommand(generic.Id.Value, Theme, generic.Version));
        refused.Error!.Code.Should().Be("not_found");
        generic.Key.Should().Be("tz");
        generic.Value.Should().Be("\"Europe/Istanbul\"");
        await frame.FailAsync();
    }

    [Fact]
    public async Task Whole_json_value_is_redacted_in_the_capture_and_durable_setting_audit()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var id = Guid.CreateVersion7();
        (await SendAsync(source, context, new SetTenantBrandingCommand(id, Theme, null))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new SetTenantBrandingCommand(id, OtherTheme, 0))).IsSuccess.Should().BeTrue();
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await using (var command = new NpgsqlCommand("""
            SELECT before_state::text, after_state::text, changes::text, entity_type, entity_id
            FROM audit_log WHERE operation = 'tenancy.setting.write' AND outcome = 'success'
            ORDER BY timestamp, id
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!))
        await using (var rows = await command.ExecuteReaderAsync())
        {
            var count = 0;
            while (await rows.ReadAsync())
            {
                var after = rows.GetString(1);
                using var document = JsonDocument.Parse(after);
                document.RootElement.GetProperty("Value").GetString().Should().Be(SensitiveTokenCatalog.RedactedValue);
                if (!rows.IsDBNull(0))
                {
                    using var before = JsonDocument.Parse(rows.GetString(0));
                    before.RootElement.GetProperty("Value").GetString().Should().Be(SensitiveTokenCatalog.RedactedValue);
                }

                rows.GetString(2).Should().NotContain("#2345aa").And.NotContain("#663399");
                rows.GetString(3).Should().Be(nameof(TenantSetting));
                rows.GetString(4).Should().Be(id.ToString());
                count++;
            }

            count.Should().Be(2);
        }

        // Generic JSON has unknown fields: the marker must redact the entire value too.
        var generic = TenantSetting.Create(TenantSettingId.From(Guid.CreateVersion7()), context.TenantId, null,
            "arbitrary", """{"invented":"private@example.test","nested":{"note":"secret"}}""", new SystemClock(), UserId.SystemActor);
        await scope.ServiceProvider.GetRequiredService<ITenantSettingWriteStore>().AddAsync(generic);
        var capture = scope.ServiceProvider.GetRequiredService<IAuditStateCapture>().Changes.Single(change => change.EntityType == nameof(TenantSetting));
        capture.AfterJson.Should().Contain(SensitiveTokenCatalog.RedactedValue).And.NotContain("private@example.test").And.NotContain("secret");
        capture.Fields.Single(field => field.Path.EndsWith("/Value", StringComparison.Ordinal)).AfterJson
            .Should().Be(JsonSerializer.Serialize(SensitiveTokenCatalog.RedactedValue));
        await frame.FailAsync();
    }

    [Fact]
    public async Task A_failed_must_audit_rolls_back_the_already_saved_branding_root()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await using (var owner = new NpgsqlConnection(database.MigrationConnectionString))
        {
            await owner.OpenAsync();
            await using var revoke = new NpgsqlCommand("REVOKE INSERT ON audit_log FROM learnstack_app", owner);
            await revoke.ExecuteNonQueryAsync();
        }

        var id = Guid.CreateVersion7();
        var send = async () => await SendAsync(source, context, new SetTenantBrandingCommand(id, Theme, null));
        (await send.Should().ThrowAsync<AuditWriteFailedException>()).Which.Error.Code.Should().Be("audit_unavailable");
        (await SendAsync(source, context, new GetSettingSeedStateQuery(id))).Value!.State.Should().BeNull();
        (await CountSuccessfulWritesAsync(source, context, "tenancy.setting.write")).Should().Be(0);
    }

    [Fact]
    public async Task Second_default_save_failure_rolls_back_the_first_clear_and_root_stamp_from_a_fresh_scope()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var initial = await ReadTenantAsync(source, context);
        var first = await SendAsync(source, context, new AddTenantLocaleCommand(initial.Version, "tr-TR", true, true, 0));
        var second = await SendAsync(source, context, new AddTenantLocaleCommand(first.Value!.Version, "en-US", true, false, 1));
        var before = await ReadTenantAsync(source, context);
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services => services.AddScoped<ISaveChangesInterceptor, ThrowOnSecondTenancySave>()));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            var send = async () => await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new SetDefaultTenantLocaleCommand(second.Value!.Version, "en-US"));
            await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected second Tenancy save");
        }

        (await ReadTenantAsync(source, context)).Should().BeEquivalentTo(before);
        (await CountSuccessfulWritesAsync(source, context, "tenancy.locale.write")).Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_palette_replacements_cannot_mix_colors_or_commit_two_successful_versions()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var id = Guid.CreateVersion7();
        (await SendAsync(source, context, new SetTenantBrandingCommand(id, Theme, null))).IsSuccess.Should().BeTrue();
        var barrier = new ReadBarrier();
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services => services.AddScoped<ITenantSettingWriteStore>(provider =>
                new BarrierSettingStore(new TenantSettingWriteStore(provider.GetRequiredService<TenancyDbContext>()), barrier))));
        async Task<Result<TenantBrandingDto>> Replace(string theme)
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SetTenantBrandingCommand(id, theme, 0));
        }

        var results = await Task.WhenAll(Replace(OtherTheme), Replace(Theme));
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.IsFailure).Which.Error!.Code.Should().Be("concurrency_conflict");
        var winner = results.Single(result => result.IsSuccess).Value!;
        var state = (await SendAsync(source, context, new GetSettingSeedStateQuery(id))).Value!.State!;
        state.Version.Should().Be(1);
        JsonNode.DeepEquals(JsonNode.Parse(state.Value), JsonNode.Parse(winner.Theme)).Should().BeTrue();
        (await CountSuccessfulWritesAsync(source, context, "tenancy.setting.write")).Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_palette_creates_leave_one_setting_and_one_successful_audit()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var barrier = new ReadBarrier();
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services => services.AddScoped<ITenantSettingWriteStore>(provider =>
                new BarrierSettingStore(new TenantSettingWriteStore(provider.GetRequiredService<TenancyDbContext>()), barrier))));
        async Task<Result<TenantBrandingDto>> Create(string theme)
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            return await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new SetTenantBrandingCommand(Guid.CreateVersion7(), theme, null));
        }

        var results = await Task.WhenAll(Create(Theme), Create(OtherTheme));
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.IsFailure).Which.Error!.Code.Should().Be("business_rule_violation");
        var winner = results.Single(result => result.IsSuccess).Value!;
        var state = (await SendAsync(source, context, new GetSettingSeedStateQuery(winner.SettingId))).Value!.State!;
        state.Version.Should().Be(0);
        JsonNode.DeepEquals(JsonNode.Parse(state.Value), JsonNode.Parse(winner.Theme)).Should().BeTrue();
        (await CountSuccessfulWritesAsync(source, context, "tenancy.setting.write")).Should().Be(1);
    }

    [Fact]
    public async Task Disabled_legacy_default_is_not_implicitly_repaired_by_either_locale_writer()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var migration = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                options => options.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved))
        {
            await migration.GetService<IMigrator>().MigrateAsync("20260914114306_org_insert_scope_and_keyless_guard");
        }

        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var initial = await ReadTenantAsync(source, context);
        var first = await SendAsync(source, context, new AddTenantLocaleCommand(initial.Version, "en", true, true, 0));
        var second = await SendAsync(source, context, new AddTenantLocaleCommand(first.Value!.Version, "fr", true, false, 1));
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await using (var command = new NpgsqlCommand("UPDATE tenant_locales SET is_enabled = false WHERE is_default",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!))
        {
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var before = (await sender.Send(new GetTenantSeedStateQuery())).Value!.State!;
        (await sender.Send(new AddTenantLocaleCommand(second.Value!.Version, "de", true, true, 2))).Error!.Code.Should().Be("validation_failed");
        (await sender.Send(new SetDefaultTenantLocaleCommand(second.Value.Version, "fr"))).Error!.Code.Should().Be("validation_failed");
        (await sender.Send(new GetTenantSeedStateQuery())).Value!.State.Should().BeEquivalentTo(before);
        await frame.FailAsync();
    }

    [Fact]
    public async Task Enabled_without_default_is_refused_by_writers_before_root_or_navigation_mutation()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var initial = await ReadTenantAsync(source, context);
        var added = await SendAsync(source, context, new AddTenantLocaleCommand(initial.Version, "en", true, true, 0));
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await using (var command = new NpgsqlCommand("UPDATE tenant_locales SET is_default = false",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!))
        {
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var before = (await sender.Send(new GetTenantSeedStateQuery())).Value!.State!;
        (await sender.Send(new AddTenantLocaleCommand(added.Value!.Version, "fr", true, false, 1))).Error!.Code.Should().Be("validation_failed");
        (await sender.Send(new SetDefaultTenantLocaleCommand(added.Value.Version, "en"))).Error!.Code.Should().Be("validation_failed");
        (await sender.Send(new GetTenantSeedStateQuery())).Value!.State.Should().BeEquivalentTo(before);
        scope.ServiceProvider.GetRequiredService<IAuditStateCapture>().Changes.Should().BeEmpty();
        await frame.FailAsync();
    }

    private static async Task<Context> ProvisionAsync(NpgsqlDataSource source)
    {
        var id = TenantId.From(Guid.CreateVersion7());
        var organization = OrganizationId.From(Guid.CreateVersion7());
        var result = await SendAsync(source, null, new ProvisionTenantCommand(id, "writer-" + id.Value.ToString("N"),
            "Writer proof", organization, "main", "Main"));
        result.IsSuccess.Should().BeTrue();
        return new Context(id, organization);
    }

    private static async Task<Result<T>> SendAsync<T>(NpgsqlDataSource source, Context? context, IRequest<Result<T>> request)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private static async Task<TenantSeedDto> ReadTenantAsync(NpgsqlDataSource source, Context context) =>
        (await SendAsync(source, context, new GetTenantSeedStateQuery())).Value!.State!;

    private static async Task<long> CountSuccessfulWritesAsync(NpgsqlDataSource source, Context context, string operation)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE operation = @operation AND outcome = 'success'",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        command.Parameters.AddWithValue("operation", operation);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await frame.FailAsync();
        return count;
    }

    private static async Task AssertLocaleAuditAsync(NpgsqlDataSource source, Context context)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await using var command = new NpgsqlCommand("""
            SELECT entity_type, entity_id, after_state::text, changes::text FROM audit_log
            WHERE operation = 'tenancy.locale.write' AND outcome = 'success'
            ORDER BY timestamp DESC, id DESC LIMIT 1
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        await using (var row = await command.ExecuteReaderAsync())
        {
            (await row.ReadAsync()).Should().BeTrue();
            row.GetString(0).Should().Be(nameof(Tenant));
            row.GetString(1).Should().Be(context.TenantId.Value.ToString());
            using var after = JsonDocument.Parse(row.GetString(2));
            after.RootElement.GetProperty("Slug").GetString().Should().StartWith("writer-");
            // ADR-0044 records existing membership as unknown, even for an Include.
            // Individual contained changes carry the default switch, including both saves.
            after.RootElement.TryGetProperty("Locales", out _).Should().BeFalse();
            using var changes = JsonDocument.Parse(row.GetString(3));
            var prefix = "/Tenant/" + context.TenantId.Value + "/Locales/";
            var fields = changes.RootElement.EnumerateArray().ToArray();
            fields.Last(field => field.GetProperty("path").GetString() == prefix + "de/IsDefault")
                .GetProperty("after").GetBoolean().Should().BeTrue();
            var cleared = fields.Single(field => field.GetProperty("path").GetString() == prefix + "tr-TR/IsDefault");
            cleared.GetProperty("before").GetBoolean().Should().BeTrue();
            cleared.GetProperty("after").GetBoolean().Should().BeFalse();
        }

        await frame.FailAsync();
    }

    private sealed record Context(TenantId TenantId, OrganizationId DefaultOrganization, OrganizationId? Organization = null) : ITenantContext
    {
        public bool IsResolved => true;
        public OrganizationId? OrganizationId => Organization;
        public UserId? UserId => null;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => "tenancy";
    }

    private sealed class ThrowOnSecondTenancySave : SaveChangesInterceptor
    {
        private int _saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is TenancyDbContext && ++_saves == 2)
            {
                throw new InvalidOperationException("injected second Tenancy save");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReadBarrier
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readers;
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readers) == 2)
            {
                _both.TrySetResult();
            }

            await _both.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    private sealed class BarrierSettingStore(ITenantSettingWriteStore inner, ReadBarrier barrier) : ITenantSettingWriteStore
    {
        public async Task<TenantSetting?> FindAsync(TenantSettingId id, CancellationToken cancellationToken = default)
        {
            var setting = await inner.FindAsync(id, cancellationToken);
            await barrier.WaitAsync(cancellationToken);
            return setting;
        }

        public Task AddAsync(TenantSetting aggregate, CancellationToken cancellationToken = default) => inner.AddAsync(aggregate, cancellationToken);
        public Task UpdateAsync(TenantSetting aggregate, CancellationToken cancellationToken = default) => inner.UpdateAsync(aggregate, cancellationToken);
    }
}
