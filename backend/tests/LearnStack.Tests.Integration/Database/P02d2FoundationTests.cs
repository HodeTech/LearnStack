using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class P02d2FoundationTests(SchemaFixture schema)
{
    private const string Profile = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
         "properties":{"a":{"type":"string"},"b":{"type":"string"}},
         "additionalProperties":false,
         "x-fields":[{"name":"b","label":{"en":"Second"}},{"name":"a","label":{"en":"First"}}]}
        """;

    private static readonly Dictionary<string, string> Label = new(StringComparer.Ordinal) { ["en"] = "Profile" };

    [Fact]
    public async Task Exact_reads_distinguish_new_bindings_from_pins_and_preserve_stored_descriptor_order()
    {
        // Audit reconciliation writes independently of a rolled-back business frame.
        // Isolate this writer proof from the shared fixture's exact audit row counts.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = ContentComponentFixture.CreateCustomizationProvider(dataSource, new Context(SchemaFixture.TenantA));
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(scope.ServiceProvider.GetRequiredService<ITenantContext>());
        await using (var setup = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@tenant, 'definition-proof', 'Definition', 'Trial', now(), @actor, 0)
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!))
        {
            setup.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            setup.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
            await setup.ExecuteNonQueryAsync();
        }

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().NotBeNull(
            "the finite seed component reaches the actual app-role SQL announcement");
        var reader = scope.ServiceProvider.GetRequiredService<IExactCustomizationDefinitionReader>();
        var first = Guid.CreateVersion7();
        var next = Guid.CreateVersion7();
        var key = "foundation-profile-" + Guid.NewGuid().ToString("N");
        (await sender.Send(new RegisterTenantContentTypeCommand(first, key, 1, Label, Profile, "default-card")))
            .IsSuccess.Should().BeTrue();
        (await reader.ReadContentTypeAsync(key, 1, DefinitionReadPurpose.NewBinding, default)).IsFailure.Should().BeTrue();
        (await reader.ReadContentTypeAsync(key, 1, DefinitionReadPurpose.ExistingPin, default)).IsFailure.Should().BeTrue();
        (await sender.Send(new PublishTenantContentTypeCommand(first))).IsSuccess.Should().BeTrue();
        var active = await reader.ReadContentTypeAsync(key, 1, DefinitionReadPurpose.NewBinding, default);
        active.IsSuccess.Should().BeTrue();
        active.Value!.Id.Should().Be(first);
        active.Value.Fields.Select(field => field.Name).Should().Equal("b", "a");
        (await sender.Send(new RegisterTenantContentTypeCommand(next, key, 2, Label, Profile, "default-card")))
            .IsSuccess.Should().BeTrue();
        (await sender.Send(new PublishTenantContentTypeCommand(next))).IsSuccess.Should().BeTrue();
        (await reader.ReadContentTypeAsync(key, 1, DefinitionReadPurpose.NewBinding, default)).IsFailure.Should().BeTrue();
        var pinned = await reader.ReadContentTypeAsync(key, 1, DefinitionReadPurpose.ExistingPin, default);
        pinned.IsSuccess.Should().BeTrue();
        pinned.Value!.Id.Should().Be(first);
        pinned.Value.Status.Should().Be(DefinitionStatus.Deprecated);
        (await reader.ReadContentTypeAsync(key, 2, DefinitionReadPurpose.NewBinding, default)).Value!.Id.Should().Be(next);
        (await reader.ReadContentTypeAsync(key, 3, DefinitionReadPurpose.ExistingPin, default)).IsFailure.Should().BeTrue();
        (await reader.ReadContentTypeAsync(key, 1, (DefinitionReadPurpose)99, default)).IsFailure.Should().BeTrue();
        (await sender.Send(new GetContentTypeSeedStateQuery(first))).Value!.State!.JsonSchema.Should().Contain("x-fields");
        var taxonomyId = Guid.CreateVersion7();
        var taxonomyNext = Guid.CreateVersion7();
        (await sender.Send(new RegisterTenantLevelTaxonomyCommand(taxonomyId, key, 1, Label,
            [new TaxonomyItemInput("intro", Label, 0)]))).IsSuccess.Should().BeTrue();
        (await reader.ReadTaxonomyAsync(key, 1, DefinitionReadPurpose.ExistingPin, default)).IsFailure.Should().BeTrue();
        (await sender.Send(new PublishTenantLevelTaxonomyCommand(taxonomyId))).IsSuccess.Should().BeTrue();
        (await reader.ReadTaxonomyAsync(key, 1, DefinitionReadPurpose.NewBinding, default))
            .Value!.Bands.Should().ContainSingle().Which.Key.Should().Be("intro");
        (await sender.Send(new RegisterTenantLevelTaxonomyCommand(taxonomyNext, key, 2, Label,
            [new TaxonomyItemInput("later", Label, 0)]))).IsSuccess.Should().BeTrue();
        (await sender.Send(new PublishTenantLevelTaxonomyCommand(taxonomyNext))).IsSuccess.Should().BeTrue();
        (await reader.ReadTaxonomyAsync(key, 1, DefinitionReadPurpose.NewBinding, default)).IsFailure.Should().BeTrue();
        (await reader.ReadTaxonomyAsync(key, 1, DefinitionReadPurpose.ExistingPin, default))
            .Value!.Bands.Should().ContainSingle().Which.Key.Should().Be("intro");
        (await sender.Send(new GetTaxonomySeedStateQuery(taxonomyId)))
            .Value!.State!.Items.Should().ContainSingle().Which.Key.Should().Be("intro");
        await frame.FailAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_seed_query_runs_on_the_composed_pipeline_and_hides_foreign_or_sibling_roots(bool scoped)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = SeedComposition.Build(dataSource,
            new Context(SchemaFixture.TenantA, scoped ? SchemaFixture.OrgA1 : null), NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var visible = EducationSchemaSeed.Find(SchemaFixture.TenantA, scoped ? SchemaFixture.OrgA1 : null);
        var sibling = EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA2);
        var foreign = EducationSchemaSeed.Find(SchemaFixture.TenantB, null);
        var tenant = (await sender.Send(new GetTenantSeedStateQuery())).Value!.State;
        tenant!.Id.Should().Be(TenantId.From(SchemaFixture.TenantA));
        tenant.Locales.Should().ContainSingle().Which.Locale.Should().Be("tr-TR");
        (await sender.Send(new GetOrganizationSeedStateQuery(OrganizationId.From(SchemaFixture.OrgA1))))
            .Value!.State!.TenantId.Should().Be(tenant.Id);
        (await sender.Send(new GetOrganizationSeedStateQuery(OrganizationId.From(SchemaFixture.OrgB1))))
            .Value!.State.Should().BeNull();
        (await sender.Send(new GetHostMappingSeedStateQuery(SchemaFixture.HostA))).Value!.State!.TenantId.Should().Be(tenant.Id);
        (await sender.Send(new GetHostMappingSeedStateQuery(SchemaFixture.HostB))).Value!.State.Should().BeNull();
        var tenantSetting = await ReadSettingFixtureAsync(dataSource, new Context(SchemaFixture.TenantA), "tz");
        var ownSetting = await ReadSettingFixtureAsync(dataSource, new Context(SchemaFixture.TenantA, SchemaFixture.OrgA1), "theme");
        var siblingSetting = await ReadSettingFixtureAsync(dataSource, new Context(SchemaFixture.TenantA, SchemaFixture.OrgA2), "theme");
        var foreignSetting = await ReadSettingFixtureAsync(dataSource, new Context(SchemaFixture.TenantB), "beta-only");
        (await sender.Send(new GetSettingSeedStateQuery(tenantSetting.Id))).Value!.State.Should().BeEquivalentTo(tenantSetting);
        if (scoped)
        {
            (await sender.Send(new GetSettingSeedStateQuery(ownSetting.Id))).Value!.State.Should().BeEquivalentTo(ownSetting);
        }
        else
        {
            (await sender.Send(new GetSettingSeedStateQuery(ownSetting.Id))).Value!.State.Should().BeNull();
        }

        foreach (var hiddenSetting in new[] { siblingSetting.Id, foreignSetting.Id, Guid.CreateVersion7() })
        {
            (await sender.Send(new GetSettingSeedStateQuery(hiddenSetting))).Value!.State.Should().BeNull();
        }
        (await sender.Send(new GetContentTypeSeedStateQuery(Guid.CreateVersion7()))).Value!.State.Should().BeNull();
        (await sender.Send(new GetTaxonomySeedStateQuery(Guid.CreateVersion7()))).Value!.State.Should().BeNull();
        (await sender.Send(new GetCourseSeedStateQuery(visible.CourseId))).Value!.State!.Id.Should().Be(visible.CourseId);
        (await sender.Send(new GetLessonSeedStateQuery(visible.LessonId))).Value!.State!.CourseId.Should().Be(visible.CourseId);
        foreach (var hidden in new[] { sibling, foreign })
        {
            (await sender.Send(new GetCourseSeedStateQuery(hidden.CourseId))).Value!.State.Should().BeNull();
            (await sender.Send(new GetLessonSeedStateQuery(hidden.LessonId))).Value!.State.Should().BeNull();
        }
    }

    [Fact]
    public async Task Locale_and_exact_definition_reads_use_the_same_announced_connection_and_do_not_fall_back()
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = SeedComposition.Build(dataSource, new Context(SchemaFixture.TenantA), NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(scope.ServiceProvider.GetRequiredService<ITenantContext>());
        var locales = scope.ServiceProvider.GetRequiredService<ITenantLocaleEligibilityReader>();
        (await locales.ReadEligibleAsync("TR-tr", default)).Value.Should().Be("tr-TR");
        (await locales.ReadEligibleAsync("en", default)).IsFailure.Should().BeTrue();
        (await locales.ReadEligibleAsync("en-US", default)).IsFailure.Should().BeTrue();
        (await locales.ReadEligibleAsync("tr_TR", default)).IsFailure.Should().BeTrue();
        var reader = scope.ServiceProvider.GetRequiredService<IExactCustomizationDefinitionReader>();
        // No implicit substitution of a different live definition.
        (await reader.ReadContentTypeAsync("card", 1, DefinitionReadPurpose.NewBinding, default)).IsFailure.Should().BeTrue();
        var absent = await reader.ReadTaxonomyAsync("absent", 1, DefinitionReadPurpose.ExistingPin, default);
        absent.IsFailure.Should().BeTrue();
        (await reader.ReadTaxonomyAsync("proficiency", 1, DefinitionReadPurpose.NewBinding, default))
            .Value!.Bands.Should().ContainSingle().Which.Key.Should().Be("beginner");
        await using (var other = SeedComposition.Build(dataSource, new Context(SchemaFixture.TenantB), NullLoggerFactory.Instance))
        await using (var otherScope = other.CreateAsyncScope())
        {
            var otherUnit = otherScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var otherFrame = await otherUnit.BeginTransactionAsync();
            await otherUnit.SetTenantContextAsync(otherScope.ServiceProvider.GetRequiredService<ITenantContext>());
            var otherLocales = otherScope.ServiceProvider.GetRequiredService<ITenantLocaleEligibilityReader>();
            (await otherLocales.ReadEligibleAsync("en-us", default)).Value.Should().Be("en-US");
            (await otherLocales.ReadEligibleAsync("tr-TR", default)).IsFailure.Should().BeTrue();
            var otherReader = otherScope.ServiceProvider.GetRequiredService<IExactCustomizationDefinitionReader>();
            var foreignType = (await otherReader.ReadContentTypeAsync("announcement", 1, DefinitionReadPurpose.NewBinding, default)).Value!;
            var foreignTaxonomy = (await otherReader.ReadTaxonomyAsync("proficiency", 1, DefinitionReadPurpose.NewBinding, default)).Value!;
            foreignTaxonomy.Bands.Should().ContainSingle().Which.Key.Should().Be("starter");
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            (await sender.Send(new GetContentTypeSeedStateQuery(foreignType.Id))).Value!.State.Should().BeNull();
            (await sender.Send(new GetTaxonomySeedStateQuery(foreignTaxonomy.Id))).Value!.State.Should().BeNull();
            await otherFrame.FailAsync();
        }

        await using (var legacy = new NpgsqlCommand("""
            INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
            VALUES (@tenant, 'en', false, false, 1);
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!))
        {
            legacy.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            await legacy.ExecuteNonQueryAsync();
        }

        // Disabled membership must fail even while the enabled default is valid.
        (await locales.ReadEligibleAsync("tr-TR", default)).Value.Should().Be("tr-TR");
        (await locales.ReadEligibleAsync("en", default)).IsFailure.Should().BeTrue();
        await using (var invalid = new NpgsqlCommand(
            "UPDATE tenant_locales SET is_default = false WHERE tenant_id = @tenant",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!))
        {
            invalid.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            await invalid.ExecuteNonQueryAsync();
        }

        // A supported locale does not conceal an invalid configuration.
        (await locales.ReadEligibleAsync("tr-TR", default)).IsFailure.Should().BeTrue();
        await frame.FailAsync();
    }

    [Fact]
    public async Task A_disabled_legacy_default_refuses_an_enabled_locale_independently_of_default_count()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres, applyMigrations: false);
        await using (var context = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                options => options.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved))
        {
            const string Predecessor = "20260914114306_org_insert_scope_and_keyless_guard";
            await context.GetService<IMigrator>().MigrateAsync(Predecessor);
            var history = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
            history.Last().Should().Be(Predecessor);
            // Model-compatible counterfactual at the historical locale constraint
            // boundary, not a supported current-runtime/old-schema deployment.
            // The current Tenant projection needs this unrelated scalar; no later
            // origin/default lifecycle guard or migration history is installed.
            await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            (await SchemaQueries.CountAsync(owner, """
                SELECT count(*) FROM information_schema.columns WHERE table_schema='public'
                  AND table_name='tenants' AND column_name='adopted_seed_origin_version'
                """)).Should().Be(0);
            await SchemaQueries.ExecuteAsync(owner, null,
                "ALTER TABLE public.tenants ADD COLUMN adopted_seed_origin_version integer NOT NULL DEFAULT 0");
            (await SchemaQueries.ReadStringsAsync(owner, """
                SELECT data_type||':'||is_nullable||':'||column_default FROM information_schema.columns
                WHERE table_schema='public' AND table_name='tenants' AND column_name='adopted_seed_origin_version'
                """)).Should().Equal("integer:NO:0");
            (await SchemaQueries.CountAsync(owner, """
                SELECT count(*) FROM pg_constraint WHERE conrelid='public.tenant_locales'::regclass
                  AND conname='ck_tenant_locales_default_enabled'
                """)).Should().Be(0);
            (await context.Database.GetAppliedMigrationsAsync()).Should().Equal(history);
        }

        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = SeedComposition.Build(dataSource, new Context(SchemaFixture.TenantA), NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(scope.ServiceProvider.GetRequiredService<ITenantContext>());
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().NotBeNull();
        await using (var legacy = new NpgsqlCommand("""
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@tenant, 'legacy-locale', 'Legacy', 'Trial', now(), @actor, 0);
            INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
            VALUES (@tenant, 'tr-TR', true, false, 0), (@tenant, 'en', false, true, 1);
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!))
        {
            legacy.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            legacy.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
            await legacy.ExecuteNonQueryAsync();
        }

        (await SchemaQueries.CountAsync(unit.Connection,
            "SELECT count(*) FROM tenant_locales WHERE is_default", unit.Transaction)).Should().Be(1);
        (await SchemaQueries.CountAsync(unit.Connection,
            "SELECT count(*) FROM tenant_locales WHERE locale='en' AND is_enabled", unit.Transaction)).Should().Be(1);
        var reader = scope.ServiceProvider.GetRequiredService<ITenantLocaleEligibilityReader>();
        (await reader.ReadEligibleAsync("en", default)).IsFailure.Should().BeTrue();
        // Same current reader, connection and owner: a valid twin proves that
        // schema/owner refusal cannot stand in for disabled-default validation.
        await SchemaQueries.ExecuteAsync(unit.Connection, unit.Transaction,
            "UPDATE tenant_locales SET is_enabled=true WHERE is_default");
        (await reader.ReadEligibleAsync("en", default)).Value.Should().Be("en");
        await SchemaQueries.ExecuteAsync(unit.Connection, unit.Transaction,
            "UPDATE tenant_locales SET is_enabled=false WHERE is_default");
        (await reader.ReadEligibleAsync("en", default)).IsFailure.Should().BeTrue();
        await frame.FailAsync();
        await using var check = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await check.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(check, transaction);
        await EducationSchemaSeed.AnnounceAsync(check, transaction, SchemaFixture.TenantA, null);
        (await SchemaQueries.CountAsync(check, "SELECT count(*) FROM tenants", transaction)).Should().Be(0);
        (await SchemaQueries.CountAsync(check, "SELECT count(*) FROM tenant_locales", transaction)).Should().Be(0);
    }

    private static async Task<SettingSeedDto> ReadSettingFixtureAsync(NpgsqlDataSource dataSource, Context context, string key)
    {
        await using var provider = SeedComposition.Build(dataSource, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        // Test discovery uses the same app role and declared scope as the production reader.
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
        await using var command = new NpgsqlCommand("""
            SELECT id, created_at, created_by, updated_at, updated_by, deleted_at, deleted_by
            FROM tenant_settings WHERE key = @key
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        command.Parameters.AddWithValue("key", key);
        Guid id;
        DateTimeOffset createdAt;
        await using (var row = await command.ExecuteReaderAsync())
        {
            (await row.ReadAsync()).Should().BeTrue();
            id = row.GetGuid(0);
            createdAt = row.GetFieldValue<DateTimeOffset>(1);
            createdAt.Should().NotBe(default(DateTimeOffset));
            row.GetGuid(2).Should().Be(UserId.SystemActor.Value);
            Enumerable.Range(3, 4).Should().OnlyContain(index => row.IsDBNull(index));
            (await row.ReadAsync()).Should().BeFalse("the fixture key has one root in the announced scope");
        }
        var state = (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetSettingSeedStateQuery(id))).Value!.State;
        state.Should().NotBeNull();
        state!.Id.Should().Be(id);
        state.TenantId.Should().Be(context.TenantId);
        state.OrganizationId.Should().Be(context.OrganizationId);
        state.Key.Should().Be(key);
        state.Value.Should().Be(key switch
        {
            "tz" => "\"Europe/Istanbul\"",
            "beta-only" => "\"visible to beta alone\"",
            _ => context.OrganizationId == OrganizationId.From(SchemaFixture.OrgA1) ? "\"main\"" : "\"branch\"",
        });
        state.Version.Should().Be(0);
        state.CreatedAt.Should().Be(createdAt, "the seed DTO retains the independently read creation stamp");
        state.CreatedBy.Should().Be(UserId.SystemActor);
        state.UpdatedAt.Should().BeNull();
        state.UpdatedBy.Should().BeNull();
        state.DeletedAt.Should().BeNull();
        state.DeletedBy.Should().BeNull();
        await frame.FailAsync();
        return state;
    }

    private sealed class Context(Guid tenantId, Guid? organizationId = null) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => TenantId.From(tenantId);
        public OrganizationId? OrganizationId => organizationId is { } value
            ? LearnStack.SharedKernel.Identifiers.OrganizationId.From(value) : null;
        public UserId? UserId => null;
        // These are finite seed/exact-reader components; no HTTP host or claim
        // admission proposition is under test here.
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }
}
