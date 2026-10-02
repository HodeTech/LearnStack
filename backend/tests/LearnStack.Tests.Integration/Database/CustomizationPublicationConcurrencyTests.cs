using FluentAssertions;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_competitor_publishing_between_draft_and_active_reads_is_typed_stale_not_self_retirement(bool taxonomy)
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
                    new TenantContentTypeStore(provider.GetRequiredService<CustomizationDbContext>()), gate));
                services.AddScoped<ITenantLevelTaxonomyStore>(provider => new ControlledTaxonomyStore(
                    new TenantLevelTaxonomyStore(provider.GetRequiredService<CustomizationDbContext>()), gate));
            }));
        async Task<Error?> LosingAttempt()
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            return taxonomy ? (await sender.Send(new PublishTenantLevelTaxonomyCommand(id))).Error
                : (await sender.Send(new PublishTenantContentTypeCommand(id))).Error;
        }
        var loser = LosingAttempt();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            if (taxonomy) (await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(id))).IsSuccess.Should().BeTrue();
            else (await SendAsync(source, context, new PublishTenantContentTypeCommand(id))).IsSuccess.Should().BeTrue();
        }
        finally { gate.Release.TrySetResult(); }
        (await loser).Should().NotBeNull().And.Match<Error>(error => error.Code == "concurrency_conflict");
        await AssertStateAsync(source, context, id, taxonomy, "Active", initialVersion + 1);
        (await GenerationAsync(database, context.TenantId)).Should().Be(2, "register plus exactly one successful publication");
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
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services =>
            {
                services.AddScoped<ITenantContentTypeStore>(provider => new ControlledContentStore(
                    new TenantContentTypeStore(provider.GetRequiredService<CustomizationDbContext>()), failure: concurrency));
                services.AddScoped<ITenantLevelTaxonomyStore>(provider => new ControlledTaxonomyStore(
                    new TenantLevelTaxonomyStore(provider.GetRequiredService<CustomizationDbContext>()), failure: concurrency));
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
    private static async Task RegisterAsync(NpgsqlDataSource source, ITenantContext context, Guid id, bool taxonomy, string key)
    {
        if (taxonomy) (await SendAsync(source, context, Taxonomy(id, key))).IsSuccess.Should().BeTrue();
        else (await SendAsync(source, context, ContentType(id, key))).IsSuccess.Should().BeTrue();
    }
    private static RegisterTenantContentTypeCommand ContentType(Guid id, string key) => new(id, key, 1,
        new Dictionary<string, string> { ["en"] = "Definition" }, BuiltInCustomizations.Card.JsonSchema, BuiltInCustomizations.Card.RendererKey);
    private static RegisterTenantLevelTaxonomyCommand Taxonomy(Guid id, string key) => new(id, key, 1,
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
    private static void Fail(bool concurrency)
    {
        if (concurrency) throw new AggregateConcurrencyException("Injected after real save.");
        throw new AggregateConflictException("Injected after real save.", "ux_tenant_content_types_tenant_id_key_active");
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
            if (failure is { } concurrency) Fail(concurrency);
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
            if (failure is { } concurrency) Fail(concurrency);
        }
    }
    public sealed record AbsorbingPublicationCommand(Guid Id, Guid LaterId, bool Taxonomy) : IRequest<Result<None>>;
    private sealed class AbsorbingPublicationHandler(ISender sender) : IRequestHandler<AbsorbingPublicationCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(AbsorbingPublicationCommand request, CancellationToken ct)
        {
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
            return Result.Ok(None.Value);
        }
    }
    private sealed class OuterAuditSource : IAuditCatalogSource
    {
        public string ModuleName => "test";
        public void Describe(IAuditCatalogBuilder builder) => builder.Off<AbsorbingPublicationCommand>();
    }
}
