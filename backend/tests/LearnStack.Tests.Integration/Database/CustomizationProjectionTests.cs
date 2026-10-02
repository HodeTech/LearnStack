using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class CustomizationProjectionTests(SchemaFixture schema, WebApplicationFactory<Program> factory, ITestOutputHelper output)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Dictionary<string, string> Label = new() { ["en"] = "Label", ["tr"] = "Etiket" };
    private const string Profile = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
         "properties":{"a":{"type":"string"},"b":{"type":"string"}},"additionalProperties":false,
         "x-fields":[{"name":"b","label":{"zh-Hant":"Second"}},{"name":"a","label":{"en":"First"}}]}
        """;

    [Theory]
    [InlineData(false, "beginner")]
    [InlineData(true, "starter")]
    public async Task Composed_reader_returns_only_its_tenant_and_tracks_no_entities(bool foreign, string band)
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        var context = new SeedTenantContext(TenantId.From(foreign ? SchemaFixture.TenantB : SchemaFixture.TenantA), null);
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await AssertAppRoleAsync(unit);
        var reader = scope.ServiceProvider.GetRequiredService<ICustomizationDefinitionProjectionReader>();
        var result = await reader.ReadAsync(Request([new("announcement", 1), new("announcement", 9)], [new("proficiency", 1)]));
        result.IsSuccess.Should().BeTrue();
        result.Value!.Taxonomies[new("proficiency", 1)].Bands.Should().ContainSingle().Which.Key.Should().Be(band);
        result.Value.ContentTypes.Should().ContainSingle();
        result.Value.MissingContentTypes.Should().BeEquivalentTo([new DefinitionRevision("announcement", 9)]);
        scope.ServiceProvider.GetRequiredService<CustomizationDbContext>().ChangeTracker.Entries().Should().BeEmpty();
        await frame.FailAsync();
    }

    [Fact]
    public async Task Exact_batches_keep_deprecated_pins_and_distinguish_draft_deleted_absent_and_malformed()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await CreateRevisionAsync(source, context, 1);
        await CreateRevisionAsync(source, context, 2);
        await CreateRevisionAsync(source, context, 3, publish: false);
        var intactType = Guid.CreateVersion7();
        var intactTaxonomy = Guid.CreateVersion7();
        (await SendAsync(source, context, new RegisterTenantContentTypeCommand(intactType, "intact", 1, Label, Profile, "default-card")))
            .IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new PublishTenantContentTypeCommand(intactType))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new RegisterTenantLevelTaxonomyCommand(intactTaxonomy, "intact", 1, Label,
            [new("only", Label, 0)]))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(intactTaxonomy))).IsSuccess.Should().BeTrue();
        await using var read = await ReadSession.OpenAsync(source, context);
        var request = Request([new("profile", 1), new("profile", 2), new("profile", 3), new("profile", 9), new("intact", 1)],
            [new("levels", 1), new("levels", 2), new("levels", 3), new("levels", 9), new("intact", 1)]);
        var result = (await read.Reader.ReadAsync(request)).Value!;
        result.ContentTypes[new("profile", 1)].Status.Should().Be(DefinitionStatus.Deprecated);
        result.ContentTypes[new("profile", 2)].Status.Should().Be(DefinitionStatus.Active);
        result.Taxonomies[new("levels", 1)].Bands.Select(item => item.Key).Should().Equal("later", "first");
        result.MissingContentTypes.Should().BeEquivalentTo([new DefinitionRevision("profile", 3), new DefinitionRevision("profile", 9)]);
        result.MissingTaxonomies.Should().BeEquivalentTo([new DefinitionRevision("levels", 3), new DefinitionRevision("levels", 9)]);
        result.ContentTypes[new("profile", 1)].DisplayName.Locale.Should().Be("tr");
        result.ContentTypes[new("profile", 1)].Fields.Select(field => field.Name).Should().Equal("b", "a");
        result.ContentTypes[new("profile", 1)].Fields[0].Label.Locale.Should().Be("zh-Hant");
        result.ContentTypes[new("profile", 1)].Fields[1].Label.Locale.Should().Be("en");
        read.Observer.Selects.Should().Be(2, "one generation probe and one coherent batch regardless of pin count");

        await ExecuteAsync(read.Unit, """
            UPDATE tenant_content_types SET deleted_at = now() WHERE key = 'profile' AND schema_version = 1;
            UPDATE tenant_level_taxonomies SET deleted_at = now() WHERE key = 'levels' AND schema_version = 1;
            UPDATE tenant_content_types SET json_schema = '{"x-fields":42}'::jsonb WHERE key = 'profile' AND schema_version = 2;
            UPDATE tenant_level_taxonomies SET display_name = '{"en":42}'::jsonb
              WHERE key = 'levels' AND schema_version = 2;
            UPDATE customization_generations SET generation = generation + 1;
            """);
        var malformed = (await read.Reader.ReadAsync(request)).Value!;
        malformed.ContentTypes.Should().ContainSingle().Which.Key.Should().Be(new DefinitionRevision("intact", 1));
        malformed.Taxonomies.Should().ContainSingle().Which.Key.Should().Be(new DefinitionRevision("intact", 1));
        malformed.MissingContentTypes.Should().BeEquivalentTo(request.ContentTypes.Where(pin => pin.Key != "intact"));
        malformed.MissingTaxonomies.Should().BeEquivalentTo(request.Taxonomies.Where(pin => pin.Key != "intact"));
        read.Observer.Selects.Should().Be(4);
        read.Db.ChangeTracker.Entries().Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_counter_distinguishes_empty_from_corrupt_nonempty_configuration()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await using var cache = new CacheProbe();
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var empty = (await read.Reader.ReadAsync(Request([], []))).Value!;
        empty.Generation.Should().BeNull();
        empty.ContentTypes.Should().BeEmpty();
        empty.Taxonomies.Should().BeEmpty();
        await ExecuteAsync(read.Unit, """
            INSERT INTO tenant_content_types
              (id,tenant_id,key,schema_version,schema_revision,status,display_name,json_schema,renderer_key,created_at,created_by,row_version)
            VALUES (uuidv7(),NULLIF(current_setting('app.tenant_id',true),'')::uuid,'orphan',1,0,'Draft',
              '{"en":"Orphan"}','{"type":"object"}','default-card',now(),'00000000-0000-7000-8000-000000000001',0)
            """);
        var refusal = await read.Reader.ReadAsync(Request([new("orphan", 1)], []));
        refusal.Error!.Code.Should().Be("validation_failed");
        refusal.Error.Details!["Definition"].Single().Key.Should().Be("lockey_schema_extension_unresolved");
        cache.GetCalls.Should().Be(0);
        cache.SetCalls.Should().Be(0);
        await ExecuteAsync(read.Unit, """
            INSERT INTO customization_generations (tenant_id,generation)
            VALUES (NULLIF(current_setting('app.tenant_id',true),'')::uuid,1)
            """);
        var present = (await read.Reader.ReadAsync(Request([new("orphan", 1)], []))).Value!;
        present.Generation.Should().Be(1);
        present.ContentTypes.Should().BeEmpty();
        present.MissingContentTypes.Should().ContainSingle();
        cache.SetCalls.Should().Be(2, "a present counter with empty eligible families is cacheable");
    }

    [Fact]
    public async Task Publish_after_probe_with_a_partial_warm_hit_returns_the_loaded_generation_and_both_new_families()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await CreateRevisionAsync(source, context, 1);
        await CreateRevisionAsync(source, context, 2, publish: false);
        await using var cache = new CacheProbe();
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var request = Request([new("profile", 1), new("profile", 2)], [new("levels", 1), new("levels", 2)]);
        var probeGeneration = (await read.Reader.ReadAsync(request)).Value!.Generation!.Value;
        await cache.RemoveAsync(FamilyKey(context.TenantId, "taxonomies", probeGeneration));
        read.Observer.Reset();
        read.Observer.AfterProbe = async () =>
        {
            await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
            await using var scope = provider.CreateAsyncScope();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var frame = await unit.BeginTransactionAsync();
            await unit.SetTenantContextAsync(context);
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            (await sender.Send(new PublishTenantContentTypeCommand(await IdAsync(unit, "tenant_content_types", "profile", 2))))
                .IsSuccess.Should().BeTrue();
            (await sender.Send(new PublishTenantLevelTaxonomyCommand(await IdAsync(unit, "tenant_level_taxonomies", "levels", 2))))
                .IsSuccess.Should().BeTrue();
            await scope.ServiceProvider.GetRequiredService<IAuditStore>().WritePendingAsync(unit);
            await frame.CompleteAsync();
        };
        var projection = (await read.Reader.ReadAsync(request)).Value!;
        read.Observer.Intervened.Should().BeTrue();
        projection.Generation.Should().Be(probeGeneration + 2);
        projection.ContentTypes[new("profile", 1)].Status.Should().Be(DefinitionStatus.Deprecated);
        projection.ContentTypes[new("profile", 2)].Status.Should().Be(DefinitionStatus.Active);
        projection.Taxonomies[new("levels", 1)].Status.Should().Be(DefinitionStatus.Deprecated);
        projection.Taxonomies[new("levels", 2)].Status.Should().Be(DefinitionStatus.Active);
        read.Observer.Selects.Should().Be(2);
    }

    [Fact]
    public async Task Admission_rejects_unannounced_frames_bad_pins_and_bad_locales_and_propagates_cancellation()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var read = await ReadSession.OpenAsync(source, context, announce: false);
        var invoke = () => read.Reader.ReadAsync(Request([], []));
        await invoke.Should().ThrowAsync<TenantContextMissingException>();
        await read.Unit.SetTenantContextAsync(context);
        (await read.Reader.ReadAsync(Request([new("profile", 0)], []))).IsFailure.Should().BeTrue();
        (await read.Reader.ReadAsync(Request([], []) with { RequestedLocale = "tr_TR" })).IsFailure.Should().BeTrue();
        (await read.Reader.ReadAsync(Request([], []) with { ContentTypes = default })).IsFailure.Should().BeTrue();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var cancel = () => read.Reader.ReadAsync(Request([], []), canceled.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();
        read.Observer.Selects.Should().Be(0);
        await read.Frame.FailAsync();
        await invoke.Should().ThrowAsync<TenantContextMissingException>();
    }

    private static DefinitionProjectionRequest Request(
        System.Collections.Immutable.ImmutableArray<DefinitionRevision> types,
        System.Collections.Immutable.ImmutableArray<DefinitionRevision> taxonomies) => new(types, taxonomies, "zh-Hant-TW", "tr");

    private static async Task<SeedTenantContext> ProvisionAsync(NpgsqlDataSource source)
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        (await SendAsync(source, null, new ProvisionTenantCommand(tenant, "projection-proof", "Projection proof",
            OrganizationId.From(Guid.CreateVersion7()), "main", "Main"))).IsSuccess.Should().BeTrue();
        return new(tenant, null);
    }
    private static async Task<Result<T>> SendAsync<T>(NpgsqlDataSource source, ITenantContext? context, IRequest<Result<T>> command)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }
    private static async Task CreateRevisionAsync(NpgsqlDataSource source, ITenantContext context, int version, bool publish = true)
    {
        var type = Guid.CreateVersion7();
        var taxonomy = Guid.CreateVersion7();
        (await SendAsync(source, context, new RegisterTenantContentTypeCommand(type, "profile", version, Label, Profile, "default-card")))
            .IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new RegisterTenantLevelTaxonomyCommand(taxonomy, "levels", version, Label,
            [new("first", Label, 2), new("later", Label, 1)]))).IsSuccess.Should().BeTrue();
        if (!publish) return;
        (await SendAsync(source, context, new PublishTenantContentTypeCommand(type))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(taxonomy))).IsSuccess.Should().BeTrue();
    }
    private static async Task ExecuteAsync(IUnitOfWork unit, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> GenerationAsync(IUnitOfWork unit)
    {
        await using var command = new NpgsqlCommand("SELECT generation FROM customization_generations",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        return (long)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<Guid> IdAsync(IUnitOfWork unit, string table, string key, int version)
    {
        // Closed test-only table names; data remain parameters.
        await using var command = new NpgsqlCommand($"SELECT id FROM {table} WHERE key = @key AND schema_version = @version",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("version", version);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
    private static async Task AssertAppRoleAsync(IUnitOfWork unit)
    {
        await using var command = new NpgsqlCommand("SELECT current_user, rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("learnstack_app");
        reader.GetBoolean(1).Should().BeFalse();
    }

    internal sealed record CommandSample(string Sql, (string Name, object Value)[] Parameters);

    internal sealed class CommandObserver : DbCommandInterceptor
    {
        public int Selects { get; private set; }
        public bool Intervened { get; private set; }
        public Func<Task>? AfterProbe { get; set; }
        public CommandSample? Probe { get; private set; }
        public CommandSample? Snapshot { get; private set; }
        private static CommandSample Sample(DbCommand command) => new(command.CommandText,
            command.Parameters.Cast<DbParameter>().Select(parameter => (parameter.ParameterName, parameter.Value!)).ToArray());
        public void Reset() => Selects = 0;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            Selects++;
            if (command.CommandText.Contains("P02d-3 generation probe", StringComparison.Ordinal)) Probe = Sample(command);
            if (command.CommandText.Contains("P02d-3 definition snapshot", StringComparison.Ordinal)) Snapshot = Sample(command);
            if (AfterProbe is { } action && command.CommandText.Contains("P02d-3 generation probe", StringComparison.Ordinal))
            {
                AfterProbe = null;
                await action();
                Intervened = true;
            }
            return result;
        }
    }

    internal sealed class ReadSession : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private ReadSession(ServiceProvider provider, AsyncServiceScope scope, IUnitOfWork unit,
            IUnitOfWorkScope frame, CustomizationDbContext db, CommandObserver observer, ITenantContext context, CacheProbe? cache)
        {
            _provider = provider; _scope = scope; Unit = unit; Frame = frame; Db = db; Observer = observer;
            Reader = new CustomizationDefinitionProjectionReader(new DefinitionSnapshotStore(db), context, unit,
                cache is null ? scope.ServiceProvider.GetRequiredService<DefinitionFamilyCache>()
                    : new DefinitionFamilyCache(cache, State, unit, cache.Logger));
        }
        public IServiceProvider Services => _scope.ServiceProvider;
        public CustomizationReadState State => Services.GetRequiredService<CustomizationReadState>();
        public IUnitOfWork Unit { get; }
        public IUnitOfWorkScope Frame { get; }
        public CustomizationDbContext Db { get; }
        public CommandObserver Observer { get; }
        public ICustomizationDefinitionProjectionReader Reader { get; }
        public static async Task<ReadSession> OpenAsync(NpgsqlDataSource source, ITenantContext context, bool announce = true, CacheProbe? cache = null)
        {
            var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
            var scope = provider.CreateAsyncScope();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var frame = await unit.BeginTransactionAsync();
            if (announce) await unit.SetTenantContextAsync(context);
            var observer = new CommandObserver();
            var db = new CustomizationDbContext(new DbContextOptionsBuilder<CustomizationDbContext>()
                .UseNpgsql(unit.Connection).AddInterceptors(new TenantContextGuardInterceptor(unit), observer).Options,
                scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>());
            await db.Database.UseTransactionAsync(unit.Transaction);
            return new(provider, scope, unit, frame, db, observer, context, cache);
        }
        public async ValueTask DisposeAsync()
        {
            await Frame.DisposeAsync();
            await Db.DisposeAsync();
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
