using FluentAssertions;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.SharedKernel.Caching;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CustomizationPublicationConcurrencyTests(SchemaFixture schema, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_competitor_publishing_between_reads_is_refused_without_self_or_other_retirement(bool taxonomy, bool differentRevision)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var id = Guid.CreateVersion7();
        await RegisterAsync(source, context, id, taxonomy, "race-definition");
        var initialVersion = await VersionAsync(source, context, id, taxonomy);
        var gate = new ActiveReadGate();
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services =>
            {
                services.AddScoped<ITenantContentTypeStore>(provider => new ControlledContentStore(
                    new TenantContentTypeStore(provider.GetRequiredService<CustomizationDbContext>(), provider.GetRequiredService<CustomizationReadState>()), gate));
                services.AddScoped<ITenantLevelTaxonomyStore>(provider => new ControlledTaxonomyStore(
                    new TenantLevelTaxonomyStore(provider.GetRequiredService<CustomizationDbContext>(), provider.GetRequiredService<CustomizationReadState>()), gate));
            }));
        async Task<Error?> LosingAttempt()
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            return taxonomy ? (await sender.Send(new PublishTenantLevelTaxonomyCommand(id, RequireNoIncumbent: true))).Error
                : (await sender.Send(new PublishTenantContentTypeCommand(id, RequireNoIncumbent: true))).Error;
        }
        var loser = LosingAttempt();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var winnerId = differentRevision ? Guid.CreateVersion7() : id;
        var winnerVersion = initialVersion;
        try
        {
            if (differentRevision)
            {
                await RegisterAsync(source, context, winnerId, taxonomy, "race-definition", version: 2);
                winnerVersion = await VersionAsync(source, context, winnerId, taxonomy);
            }
            if (taxonomy) (await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(winnerId))).IsSuccess.Should().BeTrue();
            else (await SendAsync(source, context, new PublishTenantContentTypeCommand(winnerId))).IsSuccess.Should().BeTrue();
        }
        finally { gate.Release.TrySetResult(); }
        var error = await loser;
        error.Should().NotBeNull();
        error!.Code.Should().Be(differentRevision ? "business_rule_violation" : "concurrency_conflict");
        if (differentRevision) error.Details!["Key"].Should().ContainSingle(reason => reason.Key == "lockey_customization_key_already_live");
        await AssertStateAsync(source, context, id, taxonomy, differentRevision ? "Draft" : "Active", initialVersion + (differentRevision ? 0 : 1));
        if (differentRevision) await AssertStateAsync(source, context, winnerId, taxonomy, "Active", winnerVersion + 1);
        (await GenerationAsync(database, context.TenantId)).Should().Be(differentRevision ? 3 : 2,
            "only real registration and winner publication bump generation; the loser never retires an Active row");
        await using var connection = await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var audits = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE entity_id = @id AND operation IN ('customization.content_type.publish', 'customization.level_taxonomy.publish') AND outcome = 'success'", (NpgsqlConnection)connection);
        audits.Parameters.AddWithValue("id", id.ToString());
        ((long)(await audits.ExecuteScalarAsync())!).Should().Be(differentRevision ? 0 : 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_absorbed_first_publication_save_failure_cannot_commit_the_dirty_successor_or_later_write(bool taxonomy, bool concurrency)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var id = Guid.CreateVersion7();
        var later = Guid.CreateVersion7();
        await RegisterAsync(source, context, id, taxonomy, "first-definition");
        var initialVersion = await VersionAsync(source, context, id, taxonomy);
        await using var cache = new CustomizationProjectionTests.CacheProbe();
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services =>
            {
                services.AddScoped<ITenantContentTypeStore>(provider => new ControlledContentStore(
                    new TenantContentTypeStore(provider.GetRequiredService<CustomizationDbContext>(), provider.GetRequiredService<CustomizationReadState>()), failure: concurrency));
                services.AddScoped<ITenantLevelTaxonomyStore>(provider => new ControlledTaxonomyStore(
                    new TenantLevelTaxonomyStore(provider.GetRequiredService<CustomizationDbContext>(), provider.GetRequiredService<CustomizationReadState>()), failure: concurrency));
                services.AddSingleton<ICacheService>(cache);
                services.AddTransient<IRequestHandler<AbsorbingPublicationCommand, Result<None>>, AbsorbingPublicationHandler>();
                services.AddSingleton<IAuditCatalogSource, OuterAuditSource>();
            }));
        async Task<Result<None>> Outer()
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AbsorbingPublicationCommand(id, later, taxonomy));
        }
        (await ((Func<Task<Result<None>>>)Outer).Should().ThrowAsync<InvalidOperationException>()).WithMessage("*rollback-only*");
        await AssertStateAsync(source, context, id, taxonomy, "Draft", initialVersion);
        if (taxonomy) (await SendAsync(source, context, new GetTaxonomySeedStateQuery(later))).Value!.State.Should().BeNull();
        else (await SendAsync(source, context, new GetContentTypeSeedStateQuery(later))).Value!.State.Should().BeNull();
        (await GenerationAsync(database, context.TenantId)).Should().Be(1);
        cache.GetCalls.Should().Be(2);
        cache.SetCalls.Should().Be(2, "only the clean pre-write snapshot may fill; absorbed post-save refusal poisons the scope");
        cache.FactoryCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_publication_rolls_back_both_revisions_and_generation_when_must_audit_fails(bool taxonomy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var incumbent = Guid.CreateVersion7();
        var successor = Guid.CreateVersion7();
        await RegisterAsync(source, context, incumbent, taxonomy, "audit-replacement");
        if (taxonomy) (await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(incumbent))).IsSuccess.Should().BeTrue();
        else (await SendAsync(source, context, new PublishTenantContentTypeCommand(incumbent))).IsSuccess.Should().BeTrue();
        await RegisterAsync(source, context, successor, taxonomy, "audit-replacement", version: 2);
        var incumbentVersion = await VersionAsync(source, context, incumbent, taxonomy);
        var successorVersion = await VersionAsync(source, context, successor, taxonomy);
        var generation = await GenerationAsync(database, context.TenantId);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        {
            await using var revoke = new NpgsqlCommand("REVOKE INSERT ON audit_log FROM learnstack_app", (NpgsqlConnection)owner);
            await revoke.ExecuteNonQueryAsync();
        }

        async Task Publish()
        {
            if (taxonomy) await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(successor));
            else await SendAsync(source, context, new PublishTenantContentTypeCommand(successor));
        }
        (await ((Func<Task>)Publish).Should().ThrowAsync<AuditWriteFailedException>()).Which.Error.Code.Should().Be("audit_unavailable");
        await AssertStateAsync(source, context, incumbent, taxonomy, "Active", incumbentVersion);
        await AssertStateAsync(source, context, successor, taxonomy, "Draft", successorVersion);
        (await GenerationAsync(database, context.TenantId)).Should().Be(generation);
        await using var platform = await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var audit = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE entity_id = @id AND outcome = 'success'", (NpgsqlConnection)platform);
        audit.Parameters.AddWithValue("id", successor.ToString());
        ((long)(await audit.ExecuteScalarAsync())!).Should().Be(1, "only the earlier registration committed, not publication");
    }

    private static async Task<SeedTenantContext> ProvisionAsync(NpgsqlDataSource source)
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        (await SendAsync(source, null, new ProvisionTenantCommand(tenant, "publication-proof", "Publication proof",
            OrganizationId.From(Guid.CreateVersion7()), "main", "Main"))).IsSuccess.Should().BeTrue();
        return new(tenant, null);
    }
    private static async Task<Result<T>> SendAsync<T>(NpgsqlDataSource source, ITenantContext? context, IRequest<Result<T>> command)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }
    private static async Task RegisterAsync(NpgsqlDataSource source, ITenantContext context, Guid id, bool taxonomy, string key, int version = 1)
    {
        if (taxonomy) (await SendAsync(source, context, Taxonomy(id, key, version))).IsSuccess.Should().BeTrue();
        else (await SendAsync(source, context, ContentType(id, key, version))).IsSuccess.Should().BeTrue();
    }
    private static RegisterTenantContentTypeCommand ContentType(Guid id, string key, int version = 1) => new(id, key, version,
        new Dictionary<string, string> { ["en"] = "Definition" }, BuiltInCustomizations.Card.JsonSchema, BuiltInCustomizations.Card.RendererKey);
    private static RegisterTenantLevelTaxonomyCommand Taxonomy(Guid id, string key, int version = 1) => new(id, key, version,
        new Dictionary<string, string> { ["en"] = "Definition" }, [new("basic", new Dictionary<string, string> { ["en"] = "Basic" }, 0)]);
    private static async Task AssertStateAsync(NpgsqlDataSource source, ITenantContext context, Guid id, bool taxonomy, string status, long version)
    {
        if (taxonomy) (await SendAsync(source, context, new GetTaxonomySeedStateQuery(id))).Value!.State!.Status.Should().Be(status);
        else (await SendAsync(source, context, new GetContentTypeSeedStateQuery(id))).Value!.State!.Status.Should().Be(status);
        (await VersionAsync(source, context, id, taxonomy)).Should().Be(version);
    }
    private static async Task<long> VersionAsync(NpgsqlDataSource source, ITenantContext context, Guid id, bool taxonomy)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        var table = taxonomy ? "tenant_level_taxonomies" : "tenant_content_types";
        await using var query = new NpgsqlCommand($"SELECT row_version FROM {table} WHERE id = @id", (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        query.Parameters.AddWithValue("id", id);
        var version = (long)(await query.ExecuteScalarAsync())!;
        await frame.FailAsync();
        return version;
    }
    private static async Task<long> GenerationAsync(DisposableSchemaDatabase database, TenantId tenant)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var query = new NpgsqlCommand("SELECT generation FROM customization_generations WHERE tenant_id = @tenant", (NpgsqlConnection)connection);
        query.Parameters.AddWithValue("tenant", tenant.Value);
        return (long)(await query.ExecuteScalarAsync())!;
    }
    private sealed class ActiveReadGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task PauseAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }
    private static void Fail(bool concurrency, bool taxonomy)
    {
        if (concurrency) throw new AggregateConcurrencyException("Injected after real save.");
        throw new AggregateConflictException("Injected after real save.", taxonomy ? "ux_tenant_level_taxonomies_tenant_id_key_active" : "ux_tenant_content_types_tenant_id_key_active");
    }
    private sealed class ControlledContentStore(ITenantContentTypeStore inner, ActiveReadGate? gate = null, bool? failure = null) : ITenantContentTypeStore
    {
        public Task<TenantContentType?> FindAsync(TenantContentTypeId id, CancellationToken ct = default) => inner.FindAsync(id, ct);
        public async Task<TenantContentType?> FindActiveAsync(string key, CancellationToken ct = default)
        {
            if (gate is not null) await gate.PauseAsync(ct);
            return await inner.FindActiveAsync(key, ct);
        }
        public Task AddAsync(TenantContentType root, CancellationToken ct = default) => inner.AddAsync(root, ct);
        public async Task UpdateAsync(TenantContentType root, CancellationToken ct = default)
        {
            await inner.UpdateAsync(root, ct);
            if (failure is { } concurrency) Fail(concurrency, taxonomy: false);
        }
    }
    private sealed class ControlledTaxonomyStore(ITenantLevelTaxonomyStore inner, ActiveReadGate? gate = null, bool? failure = null) : ITenantLevelTaxonomyStore
    {
        public Task<TenantLevelTaxonomy?> FindAsync(TenantLevelTaxonomyId id, CancellationToken ct = default) => inner.FindAsync(id, ct);
        public async Task<TenantLevelTaxonomy?> FindActiveAsync(string key, CancellationToken ct = default)
        {
            if (gate is not null) await gate.PauseAsync(ct);
            return await inner.FindActiveAsync(key, ct);
        }
        public Task AddAsync(TenantLevelTaxonomy root, CancellationToken ct = default) => inner.AddAsync(root, ct);
        public async Task UpdateAsync(TenantLevelTaxonomy root, CancellationToken ct = default)
        {
            await inner.UpdateAsync(root, ct);
            if (failure is { } concurrency) Fail(concurrency, taxonomy: true);
        }
    }
    public sealed record AbsorbingPublicationCommand(Guid Id, Guid LaterId, bool Taxonomy) : IRequest<Result<None>>;
    private sealed class AbsorbingPublicationHandler(ISender sender, ICustomizationDefinitionProjectionReader reader,
        CustomizationReadState state, IUnitOfWork unit) : IRequestHandler<AbsorbingPublicationCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(AbsorbingPublicationCommand request, CancellationToken ct)
        {
            var projection = new DefinitionProjectionRequest([new("first-definition", 1)], [new("first-definition", 1)], "en", "en");
            (await reader.ReadAsync(projection, ct)).IsSuccess.Should().BeTrue();
            state.IsDirty.Should().BeFalse();
            if (request.Taxonomy)
            {
                (await sender.Send(new PublishTenantLevelTaxonomyCommand(request.Id), ct)).IsFailure.Should().BeTrue();
                (await sender.Send(Taxonomy(request.LaterId, "later-definition"), ct)).IsSuccess.Should().BeTrue();
            }
            else
            {
                (await sender.Send(new PublishTenantContentTypeCommand(request.Id), ct)).IsFailure.Should().BeTrue();
                (await sender.Send(ContentType(request.LaterId, "later-definition"), ct)).IsSuccess.Should().BeTrue();
            }
            unit.IsRollbackOnly.Should().BeTrue();
            state.IsDirty.Should().BeTrue();
            var saved = (await reader.ReadAsync(projection, ct)).Value!;
            if (request.Taxonomy) saved.Taxonomies[new("first-definition", 1)].Status.Should().Be(DefinitionStatus.Active);
            else saved.ContentTypes[new("first-definition", 1)].Status.Should().Be(DefinitionStatus.Active);
            return Result.Ok(None.Value);
        }
    }
    private sealed class OuterAuditSource : IAuditCatalogSource
    {
        public string ModuleName => "test";
        public void Describe(IAuditCatalogBuilder builder) => builder.Off<AbsorbingPublicationCommand>();
    }
}
