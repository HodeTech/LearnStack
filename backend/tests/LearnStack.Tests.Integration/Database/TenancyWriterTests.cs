using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Administration;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Writer mechanisms through the canonical human owner, with a fresh database for every case.</summary>
/// <remarks>
/// P03-3 confines the old commands to declared pristine Seed continuations. These proofs therefore
/// use the managed commands and their actual live Identity evaluation, never arbitrary Seed authority.
/// Historical CHECK application/refusal belongs to TenantLocaleEnabledMigrationTests; the malformed
/// row below deliberately removes and restores only that CHECK in this case's disposable database.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancyWriterTests : IAsyncLifetime
{
    private readonly TenancyAdministrationFixture _fixture = new();
    private const string Theme = """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""";
    private const string OtherTheme = """{"primary":"#663399","background":"#ffffff","foreground":"#000000","muted":"#444444"}""";
    private static readonly TenantId TenantA = TenantId.From(SchemaFixture.TenantA);

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_unique_constraints_remain_fixed_storage_faults_and_roll_back(bool branding)
    {
        if (branding)
            await InsertSettingAsync(SchemaFixture.TenantB, Guid.CreateVersion7(), "branding.theme", Theme);
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.OwnerConnectionString))
            await ExecuteAsync(owner, null, branding
                ? "CREATE UNIQUE INDEX ux_test_unowned ON tenant_settings (value)"
                : $"CREATE UNIQUE INDEX ux_test_unowned ON tenant_locales (sort) WHERE tenant_id='{SchemaFixture.TenantA:D}'");
        var before = await SnapshotAsync();
        var witness = new SaveFailureWitness();
        await using var harness = await _fixture.CreateAsync("writer-unowned", configure: services =>
            services.AddSingleton<ISaveChangesInterceptor>(witness));
        var result = branding
            ? await harness.Sender.Send(new SetManagedTenantBrandingCommand(Theme, null))
            : await harness.Sender.Send(new AddManagedTenantLocaleCommand(before.Version, "fr", true, false, 0));
        result.Error!.Code.Should().Be("dependency_unavailable");
        // An unowned 23505 is not a business conflict. The protected boundary exposes a fixed 503.
        var fault = witness.Failure.Should().BeOfType<DbUpdateException>().Which;
        fault.InnerException.Should().BeOfType<PostgresException>().Which.ConstraintName.Should().Be("ux_test_unowned");
        var problem = LearnStack.Api.Common.ProblemDetailsFactory.For(result.Error);
        problem.Status.Should().Be(503);
        problem.Extensions["code"].Should().Be("dependency_unavailable");
        problem.Extensions.Should().NotContainKey("errors");
        (await SnapshotAsync()).Should().BeEquivalentTo(before);
        (await CountSuccessfulWritesAsync(branding ? "tenancy.setting.write" : "tenancy.locale.write")).Should().Be(0);
    }

    [Fact]
    public async Task Locale_commands_promote_first_enabled_then_switch_existing_and_new_defaults_atomically()
    {
        await MutateAsync("DELETE FROM tenant_locales");
        var before = await SnapshotAsync();
        var disabled = await SendAsync(new AddManagedTenantLocaleCommand(before.Version, "fr", false, false, 0));
        disabled.IsSuccess.Should().BeTrue();
        (await SnapshotAsync()).Locales.Should().ContainSingle().Which.IsDefault.Should().BeFalse();
        var enabled = await SendAsync(new AddManagedTenantLocaleCommand(disabled.Value, "EN-us", true, false, 1));
        enabled.IsSuccess.Should().BeTrue();
        (await SnapshotAsync()).Locales.Single(locale => locale.IsDefault).Locale.Should().Be("en-US");
        var additional = await SendAsync(new AddManagedTenantLocaleCommand(enabled.Value, "tr-TR", true, false, 2));
        additional.IsSuccess.Should().BeTrue();
        var switched = await SendAsync(new SetManagedDefaultTenantLocaleCommand(additional.Value, "TR-tr"));
        switched.IsSuccess.Should().BeTrue();
        (await SnapshotAsync()).Locales.Single(locale => locale.IsDefault).Locale.Should().Be("tr-TR");
        // An Added incoming default must also survive EF's insert-before-clear ordering.
        var addedDefault = await _fixture.SendAsync(new AddManagedTenantLocaleCommand(switched.Value, "de", true, true, 3), "writer-default-added");
        addedDefault.IsSuccess.Should().BeTrue();
        var final = await SnapshotAsync();
        final.Version.Should().Be(before.Version + 5);
        final.Locales.Should().HaveCount(4);
        final.Locales.Single(locale => locale.IsDefault).Locale.Should().Be("de");
        (await CountSuccessfulWritesAsync("tenancy.locale.write")).Should().Be(5);
        await AssertLocaleAuditAsync("writer-default-added");
        (await SendAsync(new SetManagedDefaultTenantLocaleCommand(final.Version, "fr"))).Error!.Code.Should().Be("business_rule_violation");
        (await SendAsync(new AddManagedTenantLocaleCommand(final.Version, "DE", true, false, 4))).Error!.Code.Should().Be("business_rule_violation");
        (await SnapshotAsync()).Should().BeEquivalentTo(final);
        (await CountSuccessfulWritesAsync("tenancy.locale.write")).Should().Be(5);
    }

    [Fact]
    public async Task Branding_is_create_or_exact_replace_only_and_cannot_touch_foreign_or_generic_settings()
    {
        var foreignId = Guid.CreateVersion7();
        var genericId = Guid.CreateVersion7();
        await InsertSettingAsync(SchemaFixture.TenantB, foreignId, "branding.theme", OtherTheme);
        await InsertSettingAsync(SchemaFixture.TenantA, genericId, "tz", "\"Europe/Istanbul\"");
        var foreignBefore = await SnapshotAsync(SchemaFixture.TenantB);
        var tenantBefore = await SnapshotAsync();
        foreach (var command in new IRequest<Result<long>>[]
        {
            new AddManagedTenantLocaleCommand(tenantBefore.Version, "de", true, false, 7),
            new SetManagedDefaultTenantLocaleCommand(tenantBefore.Version, "en"),
            new SetManagedTenantBrandingCommand(Theme, null),
        })
            (await _fixture.SendAsync(command, "writer-branch-refusal", organization: SchemaFixture.OrgA1)).Error!.Code.Should().Be("forbidden");
        (await SnapshotAsync()).Should().BeEquivalentTo(tenantBefore);
        var created = await SendAsync(new SetManagedTenantBrandingCommand(Theme, null));
        created.IsSuccess.Should().BeTrue();
        created.Value.Should().Be(0);
        var original = (await SnapshotAsync()).Settings.Single(setting => setting.Key == "branding.theme");
        // The selected human contract owns the identifier. Clients cannot name another setting root.
        original.Id.Should().NotBe(foreignId).And.NotBe(genericId);
        (await SendAsync(new SetManagedTenantBrandingCommand(OtherTheme, null))).Error!.Code.Should().Be("concurrency_conflict");
        var changed = await SendAsync(new SetManagedTenantBrandingCommand(OtherTheme, created.Value));
        changed.IsSuccess.Should().BeTrue();
        changed.Value.Should().Be(1);
        (await SendAsync(new SetManagedTenantBrandingCommand(Theme, 0))).Error!.Code.Should().Be("concurrency_conflict");
        (await SendAsync(new SetManagedTenantBrandingCommand("{}", 1))).Error!.Code.Should().Be("validation_failed");
        var final = await SnapshotAsync();
        var state = final.Settings.Single(setting => setting.Key == "branding.theme");
        state.Id.Should().Be(original.Id);
        state.Version.Should().Be(1);
        JsonNode.DeepEquals(JsonNode.Parse(state.Value), JsonNode.Parse(OtherTheme)).Should().BeTrue();
        final.Settings.Single(setting => setting.Id == genericId).Should().BeEquivalentTo(tenantBefore.Settings.Single());
        (await SnapshotAsync(SchemaFixture.TenantB)).Should().BeEquivalentTo(foreignBefore);
        (await CountSuccessfulWritesAsync("tenancy.setting.write")).Should().Be(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Branding_refuses_missing_or_deleted_tenants_before_setting_access(bool missingTenant, bool replacement)
    {
        if (replacement) (await SendAsync(new SetManagedTenantBrandingCommand(Theme, null))).IsSuccess.Should().BeTrue();
        var before = await SnapshotAsync();
        var audits = await CountSuccessfulWritesAsync("tenancy.setting.write");
        if (!missingTenant)
            await MutateAsync("UPDATE tenants SET deleted_at=clock_timestamp(),deleted_by=@actor,row_version=row_version+1", ("actor", AdminA));
        var refusedBefore = await SnapshotAsync();
        await using var harness = missingTenant
            ? await CreateMissingTenantHarnessAsync()
            : await _fixture.CreateAsync("writer-deleted-branding", configure: services =>
                services.AddScoped<ITenantSettingWriteStore>(_ => new UnexpectedSettingAccess()));
        var result = await harness.Sender.Send(new SetManagedTenantBrandingCommand(OtherTheme, replacement ? 0 : null));
        result.Error!.Code.Should().Be("not_found");
        harness.Services.GetRequiredService<IAuditStateCapture>().Changes.Should().BeEmpty();
        (await SnapshotAsync()).Should().BeEquivalentTo(refusedBefore);
        (await SnapshotAsync()).Settings.Should().BeEquivalentTo(before.Settings);
        (await CountSuccessfulWritesAsync("tenancy.setting.write")).Should().Be(audits);
    }

    [Fact]
    public async Task Whole_json_value_is_redacted_in_the_capture_and_durable_setting_audit()
    {
        (await SendAsync(new SetManagedTenantBrandingCommand(Theme, null))).IsSuccess.Should().BeTrue();
        (await SendAsync(new SetManagedTenantBrandingCommand(OtherTheme, 0))).IsSuccess.Should().BeTrue();
        var id = (await SnapshotAsync()).Settings.Single().Id;
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using (var command = new NpgsqlCommand("""
            SELECT before_state::text,after_state::text,changes::text,entity_type,entity_id
            FROM audit_log WHERE operation='tenancy.setting.write' AND outcome='success' ORDER BY timestamp,id
            """, app, transaction))
        await using (var rows = await command.ExecuteReaderAsync())
        {
            var count = 0;
            while (await rows.ReadAsync())
            {
                using var after = JsonDocument.Parse(rows.GetString(1));
                after.RootElement.GetProperty("Value").GetString().Should().Be(SensitiveTokenCatalog.RedactedValue);
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
        await using var harness = await _fixture.CreateAsync("writer-generic-capture");
        await using var frame = await harness.Unit.BeginTransactionAsync();
        var context = harness.Services.GetRequiredService<ITenantContext>();
        await harness.Unit.SetTenantContextAsync(context);
        var generic = TenantSetting.Create(TenantSettingId.From(Guid.CreateVersion7()), TenantA, null,
            "arbitrary", """{"invented":"private@example.test","nested":{"note":"secret"}}""", new SystemClock(), UserId.From(AdminA));
        await harness.Services.GetRequiredService<ITenantSettingWriteStore>().AddAsync(generic);
        var capture = harness.Services.GetRequiredService<IAuditStateCapture>().Changes.Single(change => change.EntityType == nameof(TenantSetting));
        capture.AfterJson.Should().Contain(SensitiveTokenCatalog.RedactedValue).And.NotContain("private@example.test").And.NotContain("secret");
        capture.Fields.Single(field => field.Path.EndsWith("/Value", StringComparison.Ordinal)).AfterJson
            .Should().Be(JsonSerializer.Serialize(SensitiveTokenCatalog.RedactedValue));
        await frame.FailAsync();
    }

    [Fact]
    public async Task A_failed_must_audit_rolls_back_the_already_saved_branding_root()
    {
        var before = await SnapshotAsync();
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.OwnerConnectionString))
            await ExecuteAsync(owner, null, "REVOKE INSERT ON audit_log FROM learnstack_app");
        try
        {
            var send = async () => await SendAsync(new SetManagedTenantBrandingCommand(Theme, null));
            (await send.Should().ThrowAsync<AuditWriteFailedException>()).Which.Error.Code.Should().Be("audit_unavailable");
            (await SnapshotAsync()).Should().BeEquivalentTo(before);
            (await CountSuccessfulWritesAsync("tenancy.setting.write")).Should().Be(0);
        }
        finally
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.OwnerConnectionString);
            await ExecuteAsync(owner, null, "GRANT INSERT ON audit_log TO learnstack_app");
        }
    }

    [Fact]
    public async Task Second_default_save_failure_rolls_back_the_first_clear_and_root_stamp_from_a_fresh_scope()
    {
        var before = await SnapshotAsync();
        var failure = new ThrowOnSecondTenancySave();
        await using (var harness = await _fixture.CreateAsync("writer-second-save", configure: services =>
            services.AddSingleton<ISaveChangesInterceptor>(failure)))
        {
            var send = async () => await harness.Sender.Send(new SetManagedDefaultTenantLocaleCommand(before.Version, "tr"));
            await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected second Tenancy save");
            failure.Saves.Should().Be(2);
        }
        (await SnapshotAsync()).Should().BeEquivalentTo(before);
        (await CountSuccessfulWritesAsync("tenancy.locale.write")).Should().Be(0);
        // The absorbed programming/cancellation failure and rollback-only owner obligations are
        // additionally proved by TenancyAdministrationAdditionalProofTests.Second_locale_save_failure_poisoning_survives_an_outer_caller_absorbing_it.
    }

    [Fact]
    public async Task Concurrent_palette_replacements_cannot_mix_colors_or_commit_two_successful_versions()
    {
        (await SendAsync(new SetManagedTenantBrandingCommand(Theme, null))).IsSuccess.Should().BeTrue();
        var id = (await SnapshotAsync()).Settings.Single().Id;
        var gate = new SavedWriteGate();
        await using var winner = await _fixture.CreateAsync("writer-palette-winner", configure: services =>
            services.AddScoped<ITenantSettingWriteStore>(provider =>
                new GatedSettingStore(ActivatorUtilities.CreateInstance<TenantSettingWriteStore>(provider), gate)));
        await using var loser = await _fixture.CreateAsync("writer-palette-loser");
        var first = winner.Sender.Send(new SetManagedTenantBrandingCommand(OtherTheme, 0));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = loser.Sender.Send(new SetManagedTenantBrandingCommand(Theme, 0));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _fixture.Membership.ObserveWaitAsync(loser.ApplicationName, ((NpgsqlConnection)winner.Unit.Connection).ProcessID, second, deadline.Token);
            second.IsCompleted.Should().BeFalse();
        }
        finally { gate.Release.TrySetResult(); }
        (await first).IsSuccess.Should().BeTrue();
        (await second).Error!.Code.Should().Be("concurrency_conflict");
        var state = (await SnapshotAsync()).Settings.Single();
        state.Id.Should().Be(id);
        state.Version.Should().Be(1);
        JsonNode.DeepEquals(JsonNode.Parse(state.Value), JsonNode.Parse(OtherTheme)).Should().BeTrue();
        (await CountSuccessfulWritesAsync("tenancy.setting.write")).Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_palette_creates_leave_one_setting_and_one_successful_audit()
    {
        var gate = new SavedWriteGate();
        await using var winner = await _fixture.CreateAsync("writer-palette-create-winner", configure: services =>
            services.AddScoped<ITenantSettingWriteStore>(provider =>
                new GatedSettingStore(ActivatorUtilities.CreateInstance<TenantSettingWriteStore>(provider), gate)));
        await using var loser = await _fixture.CreateAsync("writer-palette-create-loser");
        var first = winner.Sender.Send(new SetManagedTenantBrandingCommand(Theme, null));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = loser.Sender.Send(new SetManagedTenantBrandingCommand(OtherTheme, null));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _fixture.Membership.ObserveWaitAsync(loser.ApplicationName, ((NpgsqlConnection)winner.Unit.Connection).ProcessID, second, deadline.Token);
            second.IsCompleted.Should().BeFalse();
        }
        finally { gate.Release.TrySetResult(); }
        (await first).IsSuccess.Should().BeTrue();
        (await second).Error!.Code.Should().Be("concurrency_conflict");
        var state = (await SnapshotAsync()).Settings.Should().ContainSingle().Which;
        state.Version.Should().Be(0);
        JsonNode.DeepEquals(JsonNode.Parse(state.Value), JsonNode.Parse(Theme)).Should().BeTrue();
        (await CountSuccessfulWritesAsync("tenancy.setting.write")).Should().Be(1);
    }

    [Fact]
    public async Task Disabled_legacy_default_is_not_implicitly_repaired_by_either_locale_writer()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, "ALTER TABLE tenant_locales DROP CONSTRAINT ck_tenant_locales_default_enabled");
        try
        {
            await MutateAsync("UPDATE tenant_locales SET is_enabled=false WHERE is_default");
            await AssertInvalidConfigurationRefusedAsync();
        }
        finally
        {
            await MutateAsync("UPDATE tenant_locales SET is_enabled=true WHERE is_default");
            await ExecuteAsync(owner, null, "ALTER TABLE tenant_locales ADD CONSTRAINT ck_tenant_locales_default_enabled CHECK (NOT is_default OR is_enabled)");
        }
        (await ScalarAsync<long>(owner, null, "SELECT count(*) FROM pg_constraint WHERE conrelid='tenant_locales'::regclass AND conname='ck_tenant_locales_default_enabled' AND convalidated")).Should().Be(1);
    }

    [Fact]
    public async Task Enabled_without_default_is_refused_by_writers_before_root_or_navigation_mutation()
    {
        await MutateAsync("UPDATE tenant_locales SET is_default=false");
        await AssertInvalidConfigurationRefusedAsync();
    }

    [Fact]
    public async Task Soft_deleted_tenants_are_not_write_targets_and_live_lookup_keeps_locales_and_flags()
    {
        (await SendAsync(new SetManagedTenantBrandingCommand(Theme, null))).IsSuccess.Should().BeTrue();
        await using (var harness = await _fixture.CreateAsync("writer-live-graph"))
        {
            await using var frame = await harness.Unit.BeginTransactionAsync();
            await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
            var store = harness.Services.GetRequiredService<ITenantWriteStore>();
            var tenant = (await store.FindAsync(TenantA))!;
            tenant.Locales.Should().HaveCount(2);
            tenant.SetFeatureFlag(LearnStack.SharedKernel.Entitlements.FeatureKeys.LessonPlayerV2, "true", new SystemClock(), UserId.From(AdminA));
            await store.UpdateAsync(tenant);
            await frame.CompleteAsync();
        }
        await using (var harness = await _fixture.CreateAsync("writer-live-graph-reload"))
        {
            await using var frame = await harness.Unit.BeginTransactionAsync();
            await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
            var tenant = (await harness.Services.GetRequiredService<ITenantWriteStore>().FindAsync(TenantA))!;
            tenant.Locales.Should().HaveCount(2);
            tenant.FeatureFlags.Should().ContainSingle().Which.Value.Should().Be("true");
            await frame.FailAsync();
        }
        await MutateAsync("UPDATE tenants SET deleted_at=clock_timestamp(),deleted_by=@actor,row_version=row_version+1", ("actor", AdminA));
        var before = await SnapshotAsync();
        await using (var harness = await _fixture.CreateAsync("writer-deleted-lookup"))
        {
            await using var frame = await harness.Unit.BeginTransactionAsync();
            await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
            (await harness.Services.GetRequiredService<ITenantWriteStore>().FindAsync(TenantA)).Should().BeNull();
            await frame.FailAsync();
        }
        foreach (var command in new IRequest<Result<long>>[]
        {
            new AddManagedTenantLocaleCommand(before.Version, "fr", true, false, 2),
            new SetManagedDefaultTenantLocaleCommand(before.Version, "tr"),
            new SetManagedTenantBrandingCommand(OtherTheme, null),
            new SetManagedTenantBrandingCommand(OtherTheme, 0),
        }) (await SendAsync(command)).Error!.Code.Should().Be("not_found");
        (await SnapshotAsync()).Should().BeEquivalentTo(before);
        (await CountSuccessfulWritesAsync("tenancy.locale.write")).Should().Be(0);
        (await CountSuccessfulWritesAsync("tenancy.setting.write")).Should().Be(1);
    }

    [Fact]
    public async Task Locale_writers_use_the_current_tenant_and_never_change_foreign_locales()
    {
        var foreignBefore = await SnapshotAsync(SchemaFixture.TenantB);
        foreignBefore.Locales.Should().HaveCount(2);
        var before = await SnapshotAsync();
        var added = await SendAsync(new AddManagedTenantLocaleCommand(before.Version, "fr", true, false, 2));
        added.IsSuccess.Should().BeTrue();
        (await SendAsync(new SetManagedDefaultTenantLocaleCommand(added.Value, "fr"))).IsSuccess.Should().BeTrue();
        (await SnapshotAsync(SchemaFixture.TenantB)).Should().BeEquivalentTo(foreignBefore);
        await using var harness = await _fixture.CreateAsync("writer-foreign-lookup");
        await using var frame = await harness.Unit.BeginTransactionAsync();
        await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
        (await harness.Services.GetRequiredService<ITenantWriteStore>().FindAsync(TenantId.From(SchemaFixture.TenantB))).Should().BeNull();
        await frame.FailAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_locale_changes_commit_only_one_exact_root_version(bool switchDefault)
    {
        if (switchDefault)
        {
            var firstVersion = await _fixture.TenantVersionAsync();
            var first = await SendAsync(new AddManagedTenantLocaleCommand(firstVersion, "fr", true, false, 2));
            first.IsSuccess.Should().BeTrue();
            (await SendAsync(new AddManagedTenantLocaleCommand(first.Value, "de", true, false, 3))).IsSuccess.Should().BeTrue();
        }
        var before = await SnapshotAsync();
        var audits = await CountSuccessfulWritesAsync("tenancy.locale.write");
        var gate = new SavedWriteGate();
        await using var winner = await _fixture.CreateAsync("writer-locale-winner", configure: services =>
            services.AddScoped<ITenantWriteStore>(provider =>
                new GatedTenantStore(ActivatorUtilities.CreateInstance<TenantWriteStore>(provider), gate)));
        await using var loser = await _fixture.CreateAsync("writer-locale-loser");
        IRequest<Result<long>> First() => switchDefault
            ? new SetManagedDefaultTenantLocaleCommand(before.Version, "fr")
            : new AddManagedTenantLocaleCommand(before.Version, "fr", true, false, 2);
        IRequest<Result<long>> Second() => switchDefault
            ? new SetManagedDefaultTenantLocaleCommand(before.Version, "de")
            : new AddManagedTenantLocaleCommand(before.Version, "de", true, false, 3);
        var firstTask = winner.Sender.Send(First());
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var secondTask = loser.Sender.Send(Second());
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _fixture.Membership.ObserveWaitAsync(loser.ApplicationName, ((NpgsqlConnection)winner.Unit.Connection).ProcessID, secondTask, deadline.Token);
            secondTask.IsCompleted.Should().BeFalse();
        }
        finally { gate.Release.TrySetResult(); }
        (await firstTask).IsSuccess.Should().BeTrue();
        (await secondTask).Error!.Code.Should().Be("concurrency_conflict");
        var final = await SnapshotAsync();
        final.Version.Should().Be(before.Version + 1);
        final.Locales.Should().HaveCount(before.Locales.Length + (switchDefault ? 0 : 1));
        LocaleSnapshot[] expected = switchDefault
            ? before.Locales.Select(locale => locale with { IsDefault = locale.Locale == "fr" }).ToArray()
            : [.. before.Locales, new LocaleSnapshot("fr", true, false, 2)];
        final.Locales.Should().BeEquivalentTo(expected);
        final.Locales.Should().ContainSingle(locale => locale.Locale == "fr");
        final.Locales.Should().ContainSingle(locale => locale.IsDefault).Which.Locale.Should().Be(switchDefault ? "fr" : "en");
        if (!switchDefault) final.Locales.Should().NotContain(locale => locale.Locale == "de");
        (await CountSuccessfulWritesAsync("tenancy.locale.write")).Should().Be(audits + 1);
    }

    private Task<Result<long>> SendAsync(IRequest<Result<long>> request) =>
        _fixture.SendAsync(request, "writer-" + Guid.NewGuid().ToString("N"));

    private Task MutateAsync(string sql, params (string Name, object? Value)[] values) =>
        _fixture.Membership.MutateAsync(SchemaFixture.TenantA, null, null, true,
            (app, transaction) => ExecuteAsync(app, transaction, sql, values));

    private Task InsertSettingAsync(Guid tenant, Guid id, string key, string value) =>
        _fixture.Membership.MutateAsync(tenant, null, null, false, (app, transaction) => ExecuteAsync(app, transaction, """
            INSERT INTO tenant_settings(id,tenant_id,organization_id,key,value,created_at,created_by,row_version)
            VALUES(@id,@tenant,NULL,@key,@value::jsonb,clock_timestamp(),@actor,0)
            """, ("id", id), ("tenant", tenant), ("key", key), ("value", value), ("actor", UserId.SystemActor.Value)));

    private async Task<Snapshot> SnapshotAsync(Guid? tenant = null)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant ?? SchemaFixture.TenantA);
        var version = await ScalarAsync<long>(app, transaction, "SELECT row_version FROM tenants");
        var root = await ScalarAsync<string>(app, transaction, "SELECT to_jsonb(t)::text FROM tenants t");
        var locales = new List<LocaleSnapshot>();
        await using (var command = new NpgsqlCommand("SELECT locale,is_enabled,is_default,sort FROM tenant_locales ORDER BY locale", app, transaction))
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) locales.Add(new(rows.GetString(0), rows.GetBoolean(1), rows.GetBoolean(2), rows.GetInt16(3)));
        var settings = new List<SettingSnapshot>();
        await using (var command = new NpgsqlCommand("SELECT id,key,value::text,row_version,to_jsonb(s)::text FROM tenant_settings s ORDER BY id", app, transaction))
        await using (var rows = await command.ExecuteReaderAsync())
            while (await rows.ReadAsync()) settings.Add(new(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetInt64(3), rows.GetString(4)));
        var flags = await ScalarAsync<string>(app, transaction, "SELECT COALESCE(jsonb_agg(to_jsonb(f) ORDER BY key),'[]'::jsonb)::text FROM tenant_feature_flags f");
        return new(version, root, locales.ToArray(), settings.ToArray(), flags);
    }

    private async Task<long> CountSuccessfulWritesAsync(string operation)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM audit_log WHERE operation=@operation AND outcome='success'", ("operation", operation));
    }

    private async Task AssertLocaleAuditAsync(string correlation)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using var command = new NpgsqlCommand("""
            SELECT entity_type,entity_id,after_state::text,changes::text FROM audit_log
            WHERE operation='tenancy.locale.write' AND outcome='success' AND correlation_id=@correlation
            """, app, transaction);
        command.Parameters.AddWithValue("correlation", correlation);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        row.GetString(0).Should().Be(nameof(Tenant));
        row.GetString(1).Should().Be(TenantA.Value.ToString());
        using var after = JsonDocument.Parse(row.GetString(2));
        after.RootElement.GetProperty("Slug").GetString().Should().StartWith("authorization-");
        after.RootElement.TryGetProperty("Locales", out _).Should().BeFalse();
        using var changes = JsonDocument.Parse(row.GetString(3));
        var prefix = "/Tenant/" + TenantA.Value + "/Locales/";
        var fields = changes.RootElement.EnumerateArray().ToArray();
        fields.Last(field => field.GetProperty("path").GetString() == prefix + "de/IsDefault")
            .GetProperty("after").GetBoolean().Should().BeTrue();
        var cleared = fields.Single(field => field.GetProperty("path").GetString() == prefix + "tr-TR/IsDefault");
        cleared.GetProperty("before").GetBoolean().Should().BeTrue();
        cleared.GetProperty("after").GetBoolean().Should().BeFalse();
        (await row.ReadAsync()).Should().BeFalse();
    }

    private async Task AssertInvalidConfigurationRefusedAsync()
    {
        var before = await SnapshotAsync();
        var reads = 0;
        foreach (var request in new IRequest<Result<long>>[]
        {
            new AddManagedTenantLocaleCommand(before.Version, "de", true, true, 2),
            new SetManagedDefaultTenantLocaleCommand(before.Version, "tr"),
        })
        {
            // A refused owner remains rollback-only. Each independently refused
            // request must use a fresh scope rather than recycle that failed owner.
            MutationRefusalStore? witness = null;
            await using var harness = await _fixture.CreateAsync("writer-invalid-locales", configure: services =>
                services.AddScoped<ITenantWriteStore>(provider => witness =
                    new MutationRefusalStore(ActivatorUtilities.CreateInstance<TenantWriteStore>(provider))));
            (await harness.Sender.Send(request)).Error!.Code.Should().Be("validation_failed");
            witness!.AssertUnchanged();
            witness.Reads.Should().Be(1);
            reads += witness.Reads;
            harness.Services.GetRequiredService<IAuditStateCapture>().Changes.Should().BeEmpty();
        }
        reads.Should().Be(2);
        (await SnapshotAsync()).Should().BeEquivalentTo(before);
        (await CountSuccessfulWritesAsync("tenancy.locale.write")).Should().Be(0);
    }

    private Task<RoleGrantPipelineHarness> CreateMissingTenantHarnessAsync() =>
        RoleGrantPipelineHarness.CreateAsync(_fixture.AppConnectionString, Authority, TenantId.From(Guid.CreateVersion7()),
            UserId.From(AdminA), "writer-missing-branding", configure: services =>
            {
                services.AddModuleDbContext<TenancyDbContext>();
                services.AddScoped<ITenancyAdministrationReader, TenancyAdministrationReader>();
                services.AddScoped<ITenantLocaleEligibilityReader, TenantLocaleEligibilityReader>();
                services.AddScoped<ITenantWriteStore, TenantWriteStore>();
                services.AddScoped<IOrganizationWriteStore, OrganizationWriteStore>();
                services.AddScoped<ITenantSettingWriteStore>(_ => new UnexpectedSettingAccess());
                services.AddScoped<TenancyAdministrationAuthorization>();
                services.AddSingleton<IAuditCatalogSource, TenancyAuditCatalogSource>();
                services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(CreateManagedOrganizationCommandHandler).Assembly));
            });

    private sealed record Snapshot(long Version, string Root, LocaleSnapshot[] Locales, SettingSnapshot[] Settings, string Flags);
    private sealed record LocaleSnapshot(string Locale, bool IsEnabled, bool IsDefault, short Sort);
    private sealed record SettingSnapshot(Guid Id, string Key, string Value, long Version, string Row);

    private sealed class ThrowOnSecondTenancySave : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is TenancyDbContext && ++Saves == 2)
                throw new InvalidOperationException("injected second Tenancy save");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SaveFailureWitness : SaveChangesInterceptor
    {
        public Exception? Failure { get; private set; }
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is TenancyDbContext) Failure = eventData.Exception;
            return Task.CompletedTask;
        }
    }

    private sealed class SavedWriteGate
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    private sealed class MutationRefusalStore(ITenantWriteStore inner) : ITenantWriteStore
    {
        private Tenant? _target;
        private long _version;
        private DateTimeOffset? _updatedAt;
        private UserId? _updatedBy;
        private LocaleSnapshot[] _locales = [];
        public int Reads { get; private set; }
        public Task<Tenant?> FindAsync(TenantId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public async Task<Tenant?> FindForUpdateAsync(TenantId id, CancellationToken cancellationToken = default)
        {
            _target = await inner.FindForUpdateAsync(id, cancellationToken);
            _target.Should().NotBeNull();
            _version = _target!.Version;
            _updatedAt = _target.UpdatedAt;
            _updatedBy = _target.UpdatedBy;
            _locales = _target.Locales.Select(locale => new LocaleSnapshot(locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort)).ToArray();
            Reads++;
            return _target;
        }
        public void AssertUnchanged()
        {
            _target!.Version.Should().Be(_version);
            _target.UpdatedAt.Should().Be(_updatedAt);
            _target.UpdatedBy.Should().Be(_updatedBy);
            _target.Locales.Select(locale => new LocaleSnapshot(locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort))
                .Should().BeEquivalentTo(_locales);
        }
        public Task AddAsync(Tenant aggregate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Invalid locale configuration must not create a Tenant.");
        public Task UpdateAsync(Tenant aggregate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Invalid locale configuration must not save or stamp a Tenant.");
    }

    private sealed class GatedTenantStore(ITenantWriteStore inner, SavedWriteGate gate) : ITenantWriteStore
    {
        public Task<Tenant?> FindAsync(TenantId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public Task<Tenant?> FindForUpdateAsync(TenantId id, CancellationToken cancellationToken = default) => inner.FindForUpdateAsync(id, cancellationToken);
        public Task AddAsync(Tenant aggregate, CancellationToken cancellationToken = default) => inner.AddAsync(aggregate, cancellationToken);
        public async Task UpdateAsync(Tenant aggregate, CancellationToken cancellationToken = default)
        {
            await inner.UpdateAsync(aggregate, cancellationToken);
            await gate.WaitAsync(cancellationToken);
        }
    }

    private sealed class GatedSettingStore(ITenantSettingWriteStore inner, SavedWriteGate gate) : ITenantSettingWriteStore
    {
        public Task<TenantSetting?> FindAsync(TenantSettingId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public Task<TenantSetting?> FindForUpdateAsync(TenantSettingId id, CancellationToken cancellationToken = default) => inner.FindForUpdateAsync(id, cancellationToken);
        public async Task AddAsync(TenantSetting aggregate, CancellationToken cancellationToken = default)
        {
            await inner.AddAsync(aggregate, cancellationToken);
            await gate.WaitAsync(cancellationToken);
        }
        public async Task UpdateAsync(TenantSetting aggregate, CancellationToken cancellationToken = default)
        {
            await inner.UpdateAsync(aggregate, cancellationToken);
            await gate.WaitAsync(cancellationToken);
        }
    }

    private sealed class UnexpectedSettingAccess : ITenantSettingWriteStore
    {
        public Task<TenantSetting?> FindAsync(TenantSettingId id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refused tenant must not load a setting.");
        public Task<TenantSetting?> FindForUpdateAsync(TenantSettingId id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refused tenant must not lock a setting.");
        public Task AddAsync(TenantSetting aggregate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refused tenant must not create a setting.");
        public Task UpdateAsync(TenantSetting aggregate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A refused tenant must not replace a setting.");
    }
}
