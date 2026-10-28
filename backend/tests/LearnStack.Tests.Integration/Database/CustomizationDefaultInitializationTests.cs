using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CustomizationDefaultInitializationTests(SchemaFixture schema, ConstructionOnlyApiFactory factory)
    : IClassFixture<ConstructionOnlyApiFactory>
{
    private ComponentAuthorizationEvidence _evidence = null!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_untouched_concept_is_created_active_with_one_save_generation_and_must_audit(bool taxonomy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        var saves = new SaveProbe();
        await using var host = Host(database, saves);

        (await InitializeAsync(host.Services, tenant, taxonomy)).IsSuccess.Should().BeTrue();

        saves.Statuses.Should().Equal("Active");
        var state = await StateAsync(source, tenant, taxonomy);
        state.Generation.Should().Be(1);
        var root = JsonNode.Parse(state.Roots.Should().ContainSingle().Which)!;
        var id = Guid.Parse(root["id"]!.GetValue<string>());
        id.Version.Should().Be(7);
        root["key"]!.GetValue<string>().Should().Be(Key(taxonomy));
        root["tenant_id"]!.GetValue<string>().Should().Be(tenant.TenantId.Value.ToString());
        root["status"]!.GetValue<string>().Should().Be("Active");
        root["schema_version"]!.GetValue<int>().Should().Be(1);
        root["created_by"]!.GetValue<string>().Should().Be(UserId.SystemActor.Value.ToString());
        root["updated_by"]!.GetValue<string>().Should().Be(UserId.SystemActor.Value.ToString());
        root["deleted_at"].Should().BeNull();
        root["deleted_by"].Should().BeNull();
        AssertNames(root["display_name"]!, taxonomy ? BuiltInCustomizations.Plain.DisplayName : BuiltInCustomizations.Card.DisplayName);
        if (taxonomy)
        {
            root["schema_revision"]!.GetValue<int>().Should().Be(3);
            root["row_version"]!.GetValue<long>().Should().Be(4);
            state.Items.Should().HaveCount(3);
            foreach (var (itemJson, band) in state.Items.Zip(BuiltInCustomizations.Plain.Bands))
            {
                var item = JsonNode.Parse(itemJson)!;
                item["key"]!.GetValue<string>().Should().Be(band.Key);
                item["taxonomy_key"]!.GetValue<string>().Should().Be("plain");
                item["schema_version"]!.GetValue<int>().Should().Be(1);
                item["sort"]!.GetValue<int>().Should().Be(band.Sort);
                item["metadata"].Should().BeNull();
                AssertNames(item["display_name"]!, band.DisplayName);
            }
        }
        else
        {
            root["schema_revision"]!.GetValue<int>().Should().Be(0);
            root["row_version"]!.GetValue<long>().Should().Be(1);
            root["renderer_key"]!.GetValue<string>().Should().Be(BuiltInCustomizations.Card.RendererKey);
            JsonNode.DeepEquals(root["json_schema"], JsonNode.Parse(BuiltInCustomizations.Card.JsonSchema)).Should().BeTrue();
            state.Items.Should().BeEmpty();
        }

        var audit = (await AuditsAsync(source, tenant, taxonomy)).Should().ContainSingle().Which;
        audit.OperationType.Should().Be("Create");
        audit.Class.Should().Be("Must");
        audit.Outcome.Should().Be("success");
        audit.EntityType.Should().Be(taxonomy ? nameof(TenantLevelTaxonomy) : nameof(TenantContentType));
        audit.EntityId.Should().Be(id.ToString());
        audit.Actor.Should().Be(UserId.SystemActor.Value);
        audit.Before.Should().BeNull();
        JsonNode.Parse(audit.After!)!["Status"]!.GetValue<string>().Should().Be("Active");
        JsonNode.Parse(audit.Changes!)!.AsArray().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(false, "Draft")]
    [InlineData(false, "Active")]
    [InlineData(false, "Deprecated")]
    [InlineData(false, "Deleted")]
    [InlineData(false, "Successor")]
    [InlineData(true, "Draft")]
    [InlineData(true, "Active")]
    [InlineData(true, "Deprecated")]
    [InlineData(true, "Deleted")]
    [InlineData(true, "Successor")]
    public async Task Any_concept_history_preserves_every_field_and_generation_and_classifies_a_no_change_attempt(bool taxonomy, string history)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        await ArrangeHistoryAsync(source, tenant, taxonomy, history);
        var before = await StateAsync(source, tenant, taxonomy);
        var saves = new SaveProbe();
        await using var host = Host(database, saves);

        (await InitializeAsync(host.Services, tenant, taxonomy)).IsSuccess.Should().BeTrue();

        var after = await StateAsync(source, tenant, taxonomy);
        after.Roots.Should().Equal(before.Roots);
        after.Items.Should().Equal(before.Items);
        after.Generation.Should().Be(before.Generation);
        saves.Statuses.Should().BeEmpty();
        var audit = (await AuditsAsync(source, tenant, taxonomy)).Should().ContainSingle().Which;
        audit.Class.Should().Be("Must");
        audit.Outcome.Should().Be("success");
        audit.EntityId.Should().BeNull();
        audit.Before.Should().BeNull();
        audit.After.Should().BeNull();
        audit.Changes.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Must_audit_refusal_rolls_back_the_active_root_and_generation(bool taxonomy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await using var revoke = new NpgsqlCommand("REVOKE INSERT ON audit_log FROM learnstack_app", (NpgsqlConnection)owner);
            await revoke.ExecuteNonQueryAsync();
        }
        var saves = new SaveProbe();
        await using var host = Host(database, saves);
        Func<Task> initialize = async () => await InitializeAsync(host.Services, tenant, taxonomy);
        await initialize.Should().ThrowAsync<AuditWriteFailedException>();
        saves.Statuses.Should().Equal(["Active"], "the real save happened before audit refused the owning transaction");
        var state = await StateAsync(source, tenant, taxonomy);
        state.Roots.Should().BeEmpty();
        state.Items.Should().BeEmpty();
        state.Generation.Should().Be(0);
        (await AuditsAsync(source, tenant, taxonomy)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    public async Task Registration_and_initialization_share_the_concept_lock_in_both_winning_orders(bool taxonomy, bool initializeFirst, int registrationVersion)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var tenant = await ProvisionAsync(source);
        var gate = new ConceptGate();
        await using var host = Host(database, gate: gate);
        var authoredId = Guid.CreateVersion7();
        async Task<IResultBase> Initialize() => await InitializeAsync(host.Services, tenant, taxonomy);
        async Task<IResultBase> Register() => await RegisterAsync(host.Services, tenant, taxonomy, authoredId, registrationVersion);
        var first = initializeFirst ? Initialize() : Register();
        await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var second = initializeFirst ? Register() : Initialize();
        try
        {
            var waitingPid = await gate.Contender.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await AssertAdvisoryWaitAsync(source, waitingPid);
            second.IsCompleted.Should().BeFalse("the actual PostgreSQL transaction lock holds the competitor");
        }
        finally { gate.Release.TrySetResult(); }
        (await first).IsSuccess.Should().BeTrue();
        var secondResult = await second;
        secondResult.IsSuccess.Should().Be(!initializeFirst || registrationVersion == 2);
        var state = await StateAsync(source, tenant, taxonomy);
        state.Generation.Should().Be(initializeFirst && registrationVersion == 2 ? 2 : 1);
        state.Roots.Should().HaveCount(initializeFirst && registrationVersion == 2 ? 2 : 1);
        var roots = state.Roots.Select(json => JsonNode.Parse(json)!).ToArray();
        if (initializeFirst)
        {
            roots.Single(root => root["schema_version"]!.GetValue<int>() == 1)["status"]!.GetValue<string>().Should().Be("Active");
            if (registrationVersion == 2)
                roots.Single(root => root["schema_version"]!.GetValue<int>() == 2)["id"]!.GetValue<string>().Should().Be(authoredId.ToString());
        }
        else
        {
            roots.Single()["id"]!.GetValue<string>().Should().Be(authoredId.ToString());
            roots.Single()["status"]!.GetValue<string>().Should().Be("Draft");
            roots.Single()["schema_version"]!.GetValue<int>().Should().Be(registrationVersion);
        }
        var audit = (await AuditsAsync(source, tenant, taxonomy)).Should().ContainSingle().Which;
        audit.Outcome.Should().Be("success");
        if (initializeFirst) audit.After.Should().NotBeNull();
        else
        {
            audit.EntityId.Should().BeNull();
            audit.After.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_tenants_same_key_history_does_not_suppress_initialization(bool taxonomy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(database);
        _evidence = evidence;
        var first = await ProvisionAsync(source);
        var second = await ProvisionAsync(source);
        await ArrangeHistoryAsync(source, first, taxonomy, "Deleted");
        var before = await StateAsync(source, first, taxonomy);
        await using var host = Host(database);
        (await InitializeAsync(host.Services, second, taxonomy)).IsSuccess.Should().BeTrue();
        (await StateAsync(source, second, taxonomy)).Roots.Should().ContainSingle();
        (await StateAsync(source, first, taxonomy)).Roots.Should().Equal(before.Roots);
    }

    private ServiceProvider BuildProvider(NpgsqlDataSource source, ITenantContext? context)
    {
        var services = ComponentOperationRegistration.CreateDatabaseServices(source, context);
        AddComponentAuthority(services);
        _evidence.AddTo(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    // Explicit admission for this component mechanism; this is not a live HTTP authorization proof.
    private static void AddComponentAuthority(IServiceCollection services) =>
        ComponentOperationRegistration.AddExactComponentAuthority(services,
            OperationPolicy.ForTrusted<InitializeTenantContentTypeDefaultCommand>("tests.component.defaults.initialize_content", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<InitializeTenantLevelTaxonomyDefaultCommand>("tests.component.defaults.initialize_taxonomy", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<RegisterTenantContentTypeCommand>("tests.component.defaults.register_content", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<RegisterTenantLevelTaxonomyCommand>("tests.component.defaults.register_taxonomy", AuthorizationSourceKind.Seed),
            OperationPolicy.ForTrusted<ProvisionTenantCommand>("tests.component.defaults.provision_tenant", AuthorizationSourceKind.Seed));

    private WebApplicationFactory<Program> Host(DisposableSchemaDatabase database, SaveProbe? saves = null, ConceptGate? gate = null) =>
        factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services =>
            {
                AddComponentAuthority(services);
                _evidence.AddTo(services);
            })
            .ConfigureServices(services =>
            {
                if (saves is not null) services.AddSingleton<ISaveChangesInterceptor>(saves);
                if (gate is null) return;
                services.AddScoped<ITenantContentTypeStore>(provider => new GatedContentStore(
                    new TenantContentTypeStore(provider.GetRequiredService<CustomizationDbContext>(), provider.GetRequiredService<CustomizationReadState>()),
                    provider.GetRequiredService<IUnitOfWork>(), gate));
                services.AddScoped<ITenantLevelTaxonomyStore>(provider => new GatedTaxonomyStore(
                    new TenantLevelTaxonomyStore(provider.GetRequiredService<CustomizationDbContext>(), provider.GetRequiredService<CustomizationReadState>()),
                    provider.GetRequiredService<IUnitOfWork>(), gate));
            }));

    private async Task<Context> ProvisionAsync(NpgsqlDataSource source)
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        await using var provider = BuildProvider(source, null);
        (await SendAsync(provider, null, new ProvisionTenantCommand(tenant, "default-" + tenant.Value.ToString("N"), "Default proof",
            OrganizationId.From(Guid.CreateVersion7()), "main", "Main"))).IsSuccess.Should().BeTrue();
        return new(tenant);
    }

    private static async Task<Result<T>> SendAsync<T>(IServiceProvider services, ITenantContext? tenant, IRequest<Result<T>> command)
    {
        await using var scope = services.CreateAsyncScope();
        if (tenant is not null) scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = tenant;
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }

    private static Task<Result<None>> InitializeAsync(IServiceProvider services, ITenantContext tenant, bool taxonomy) => taxonomy
        ? SendAsync(services, tenant, new InitializeTenantLevelTaxonomyDefaultCommand())
        : SendAsync(services, tenant, new InitializeTenantContentTypeDefaultCommand());

    private static async Task<IResultBase> RegisterAsync(IServiceProvider services, ITenantContext? tenant, bool taxonomy, Guid id, int version) => taxonomy
        ? await SendAsync(services, tenant, new RegisterTenantLevelTaxonomyCommand(id, "plain", version,
            new Dictionary<string, string> { ["en"] = "Authored taxonomy" },
            [new("custom", new Dictionary<string, string> { ["en"] = "Authored band" }, 9, "{\"color\":\"custom\"}")]))
        : await SendAsync(services, tenant, new RegisterTenantContentTypeCommand(id, "card", version,
            new Dictionary<string, string> { ["en"] = "Authored content" }, BuiltInCustomizations.Card.JsonSchema, BuiltInCustomizations.Card.RendererKey));

    private async Task ArrangeHistoryAsync(NpgsqlDataSource source, Context tenant, bool taxonomy, string history)
    {
        var id = Guid.CreateVersion7();
        await using var provider = BuildProvider(source, tenant);
        // This composition fixes its tenant; no accessor write or privileged setup is needed.
        (await RegisterAsync(provider, null, taxonomy, id, history == "Successor" ? 2 : 1)).IsSuccess.Should().BeTrue();
        if (history is "Draft" or "Successor") return;
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(tenant);
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        if (taxonomy)
        {
            var store = scope.ServiceProvider.GetRequiredService<ITenantLevelTaxonomyStore>();
            var root = (await store.FindAsync(TenantLevelTaxonomyId.From(id)))!;
            root.Publish(clock, UserId.SystemActor);
            if (history == "Deprecated") root.Deprecate(clock, UserId.SystemActor);
            if (history == "Deleted") root.SoftDelete(clock.UtcNow, UserId.SystemActor);
            await store.UpdateAsync(root);
        }
        else
        {
            var store = scope.ServiceProvider.GetRequiredService<ITenantContentTypeStore>();
            var root = (await store.FindAsync(TenantContentTypeId.From(id)))!;
            root.Publish(clock, UserId.SystemActor);
            if (history == "Deprecated") root.Deprecate(clock, UserId.SystemActor);
            if (history == "Deleted") root.SoftDelete(clock.UtcNow, UserId.SystemActor);
            await store.UpdateAsync(root);
        }
        await frame.CompleteAsync();
    }

    private static string Key(bool taxonomy) => taxonomy ? "plain" : "card";

    private static async Task<State> StateAsync(NpgsqlDataSource source, ITenantContext tenant, bool taxonomy)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant.TenantId.Value);
        var table = taxonomy ? "tenant_level_taxonomies" : "tenant_content_types";
        var roots = await RowsAsync($"SELECT to_jsonb(t)::text FROM {table} t WHERE tenant_id = @tenant AND key = @key ORDER BY schema_version");
        var items = taxonomy
            ? await RowsAsync("SELECT to_jsonb(t)::text FROM tenant_level_taxonomy_items t WHERE tenant_id = @tenant AND taxonomy_key = @key ORDER BY schema_version, sort")
            : [];
        await using var generation = new NpgsqlCommand("SELECT COALESCE((SELECT generation FROM customization_generations WHERE tenant_id = @tenant), 0)", connection, transaction);
        generation.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        return new(roots, items, (long)(await generation.ExecuteScalarAsync())!);

        async Task<List<string>> RowsAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
            command.Parameters.AddWithValue("key", Key(taxonomy));
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }
    }

    private static async Task<List<Audit>> AuditsAsync(NpgsqlDataSource source, ITenantContext tenant, bool taxonomy)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, tenant.TenantId.Value);
        await using var command = new NpgsqlCommand("""
            SELECT operation_type, operation_class, outcome, entity_type, entity_id, actor_user_id,
                   before_state::text, after_state::text, changes::text
            FROM audit_log WHERE tenant_id = @tenant AND operation = @operation
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("operation", taxonomy ? "customization.level_taxonomy.initialize" : "customization.content_type.initialize");
        var rows = new List<Audit>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(3), Text(4), reader.GetGuid(5), Text(6), Text(7), Text(8)));
        return rows;
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static void AssertNames(JsonNode actual, IReadOnlyDictionary<string, string> expected)
    {
        actual.AsObject().Count.Should().Be(expected.Count);
        foreach (var (locale, text) in expected) actual[locale]!.GetValue<string>().Should().Be(text);
    }

    private static async Task AssertAdvisoryWaitAsync(NpgsqlDataSource source, int pid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = await source.OpenConnectionAsync(timeout.Token);
        while (!timeout.IsCancellationRequested)
        {
            await using var query = new NpgsqlCommand("SELECT wait_event FROM pg_stat_activity WHERE pid = @pid", connection);
            query.Parameters.AddWithValue("pid", pid);
            if (await query.ExecuteScalarAsync(timeout.Token) is "advisory") return;
            await Task.Delay(20, timeout.Token);
        }
        throw new InvalidOperationException("The competing writer never waited on the concept lock.");
    }

    private sealed record State(List<string> Roots, List<string> Items, long Generation);
    private sealed record Context(TenantId TenantId) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public string? CorrelationId => null;
        public string? ModuleName => "customization";
    }
    private sealed record Audit(string OperationType, string Class, string Outcome, string? EntityType,
        string? EntityId, Guid Actor, string? Before, string? After, string? Changes);

    private sealed class SaveProbe : SaveChangesInterceptor
    {
        internal List<string> Statuses { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is CustomizationDbContext db)
            {
                var roots = db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added)
                    .Select(entry => entry.Entity).Where(entity => entity is TenantContentType or TenantLevelTaxonomy).ToArray();
                Statuses.AddRange(roots.Select(root => root is TenantContentType content ? content.Status.ToString() : ((TenantLevelTaxonomy)root).Status.ToString()));
                roots.Should().ContainSingle("each initializer saves one complete aggregate root");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ConceptGate
    {
        private int _calls;
        internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Contender { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task LockAsync(Func<Task> acquire, IUnitOfWork unit, CancellationToken cancellationToken)
        {
            var first = Interlocked.Increment(ref _calls) == 1;
            if (!first) Contender.TrySetResult(((NpgsqlConnection)unit.Connection).ProcessID);
            await acquire();
            if (!first) return;
            Held.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class GatedContentStore(ITenantContentTypeStore inner, IUnitOfWork unit, ConceptGate gate) : ITenantContentTypeStore
    {
        public Task LockRegistrationsAsync(CancellationToken cancellationToken = default) => inner.LockRegistrationsAsync(cancellationToken);
        public Task<ContentTypeQuotaUsage> ReadQuotaUsageAsync(string key, CancellationToken cancellationToken = default) => inner.ReadQuotaUsageAsync(key, cancellationToken);
        public Task LockConceptAsync(string key, CancellationToken cancellationToken = default) => gate.LockAsync(() => inner.LockConceptAsync(key, cancellationToken), unit, cancellationToken);
        public Task<bool> HasConceptHistoryAsync(string key, CancellationToken cancellationToken = default) => inner.HasConceptHistoryAsync(key, cancellationToken);
        public Task<TenantContentType?> FindAsync(TenantContentTypeId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public Task<TenantContentType?> FindActiveAsync(string key, CancellationToken cancellationToken = default) => inner.FindActiveAsync(key, cancellationToken);
        public Task AddAsync(TenantContentType root, CancellationToken cancellationToken = default) => inner.AddAsync(root, cancellationToken);
        public Task UpdateAsync(TenantContentType root, CancellationToken cancellationToken = default) => inner.UpdateAsync(root, cancellationToken);
    }

    private sealed class GatedTaxonomyStore(ITenantLevelTaxonomyStore inner, IUnitOfWork unit, ConceptGate gate) : ITenantLevelTaxonomyStore
    {
        public Task LockConceptAsync(string key, CancellationToken cancellationToken = default) => gate.LockAsync(() => inner.LockConceptAsync(key, cancellationToken), unit, cancellationToken);
        public Task<bool> HasConceptHistoryAsync(string key, CancellationToken cancellationToken = default) => inner.HasConceptHistoryAsync(key, cancellationToken);
        public Task<TenantLevelTaxonomy?> FindAsync(TenantLevelTaxonomyId id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public Task<TenantLevelTaxonomy?> FindActiveAsync(string key, CancellationToken cancellationToken = default) => inner.FindActiveAsync(key, cancellationToken);
        public Task AddAsync(TenantLevelTaxonomy root, CancellationToken cancellationToken = default) => inner.AddAsync(root, cancellationToken);
        public Task UpdateAsync(TenantLevelTaxonomy root, CancellationToken cancellationToken = default) => inner.UpdateAsync(root, cancellationToken);
    }
}
