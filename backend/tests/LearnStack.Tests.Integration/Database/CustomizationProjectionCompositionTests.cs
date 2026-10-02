using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class CustomizationProjectionTests
{
    [Fact]
    public async Task Nested_commands_share_the_dirty_reader_scope_and_cannot_fill_speculative_generation_keys()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await using var cache = new CacheProbe();
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        (await read.Reader.ReadAsync(Request([], []))).Value!.Generation.Should().BeNull();
        var id = Guid.CreateVersion7();
        var sender = read.Services.GetRequiredService<ISender>();
        (await sender.Send(new RegisterTenantContentTypeCommand(id, "nested", 1, Label, Profile, "default-card"))).IsSuccess.Should().BeTrue();
        (await sender.Send(new PublishTenantContentTypeCommand(id))).IsSuccess.Should().BeTrue();
        var saved = (await read.Reader.ReadAsync(Request([new("nested", 1)], []))).Value!;
        saved.Generation.Should().Be(2);
        saved.ContentTypes[new("nested", 1)].Status.Should().Be(DefinitionStatus.Active);
        cache.GetCalls.Should().Be(0);
        cache.SetCalls.Should().Be(0);
        await read.Frame.FailAsync();
        await using var fresh = await ReadSession.OpenAsync(source, context, cache: cache);
        (await fresh.Reader.ReadAsync(Request([new("nested", 1)], []))).Value!.MissingContentTypes.Should().ContainSingle();
        fresh.State.IsDirty.Should().BeFalse();
        cache.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Api_and_seeder_roots_supply_the_same_scoped_guarded_contracts()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var seed = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var api = factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Default", schema.Postgres.AppConnectionString));
        foreach (var root in new[] { seed, api.Services })
        {
            CustomizationReadState? previous = null;
            for (var index = 0; index < 2; index++)
            {
                await using var scope = root.CreateAsyncScope();
                var services = scope.ServiceProvider;
                services.GetRequiredService<ITenantContextAccessor>().Current = context;
                var unit = services.GetRequiredService<IUnitOfWork>();
                await using var frame = await unit.BeginTransactionAsync();
                await unit.SetTenantContextAsync(context);
                await AssertAppRoleAsync(unit);
                var state = services.GetRequiredService<CustomizationReadState>();
                state.Should().NotBeSameAs(previous);
                state.Should().BeSameAs(services.GetRequiredService<CustomizationReadState>());
                state.IsDirty.Should().BeFalse();
                var reader = services.GetRequiredService<ICustomizationDefinitionProjectionReader>();
                var result = (await reader.ReadAsync(Request([new("announcement", 1)], [new("proficiency", 1)]))).Value!;
                result.Taxonomies[new("proficiency", 1)].Bands.Should().ContainSingle().Which.Key.Should().Be("beginner");
                var settings = services.GetRequiredService<ITenantSettingsAccessor>();
                (await settings.ReadAsync(TenantSettingKeys.BrandingTheme)).IsSuccess.Should().BeTrue();
                services.GetRequiredService<CustomizationDbContext>().ChangeTracker.Entries().Should().BeEmpty();
                await frame.FailAsync();
                await ((Func<Task<LearnStack.SharedKernel.Results.Result<DefinitionProjection>>>)(() => reader.ReadAsync(Request([], []))))
                    .Should().ThrowAsync<LearnStack.SharedKernel.Errors.TenantContextMissingException>();
                previous = state;
            }
        }
    }

    [Fact]
    public async Task Concurrent_cold_callers_keep_their_own_transaction_and_awaited_loader()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var cache = new CacheProbe();
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var first = await ReadSession.OpenAsync(source, context, cache: cache);
        await using var second = await ReadSession.OpenAsync(source, context, cache: cache);
        var arrived = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause()
        {
            if (Interlocked.Increment(ref arrived) == 2) barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        first.Observer.AfterProbe = Pause;
        second.Observer.AfterProbe = Pause;
        var request = Request([new("announcement", 1)], [new("proficiency", 1)]);
        var results = await Task.WhenAll(first.Reader.ReadAsync(request), second.Reader.ReadAsync(request));
        results.Should().OnlyContain(result => result.IsSuccess);
        first.Unit.Connection.Should().NotBeSameAs(second.Unit.Connection);
        first.Observer.Selects.Should().BeInRange(1, 2);
        second.Observer.Selects.Should().BeInRange(1, 2);
        cache.FactoryCalls.Should().Be(0);
        await first.Frame.FailAsync();
        (await second.Reader.ReadAsync(request)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Seeded_local_measurement_records_statement_plans_payload_volume_and_end_to_end_timings()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance), NullLogger<SeedRunner>.Instance);
        (await runner.RunAsync(CancellationToken.None)).Should().Be(0);
        await using var cache = new CacheProbe();
        var tenant = SeedData.English;
        var context = new SeedTenantContext(tenant.TenantId, null);
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        await AssertAppRoleAsync(read.Unit);
        var request = Request([.. SeedData.ContentTypes(tenant).Select(type => new DefinitionRevision(type.Key, type.SchemaVersion))],
            [.. SeedData.Taxonomies(tenant).Select(taxonomy => new DefinitionRevision(taxonomy.Key, taxonomy.SchemaVersion))]);
        var initial = (await read.Reader.ReadAsync(request)).Value!;
        initial.ContentTypes.Should().HaveCount(2);
        initial.Taxonomies.Should().HaveCount(2);
        var cold = new List<double>(); var warm = new List<double>(); var settings = new List<double>();
        var accessor = read.Services.GetRequiredService<ITenantSettingsAccessor>();
        (await accessor.ReadAsync(TenantSettingKeys.BrandingTheme)).Value!.IsPresent.Should().BeTrue();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            await cache.RemoveAsync(FamilyKey(context.TenantId, "content-types", initial.Generation!.Value));
            await cache.RemoveAsync(FamilyKey(context.TenantId, "taxonomies", initial.Generation.Value));
            read.Observer.Reset();
            var started = Stopwatch.GetTimestamp();
            (await read.Reader.ReadAsync(request)).IsSuccess.Should().BeTrue();
            cold.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            read.Observer.Selects.Should().Be(2);
            started = Stopwatch.GetTimestamp();
            (await read.Reader.ReadAsync(request)).IsSuccess.Should().BeTrue();
            warm.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            read.Observer.Selects.Should().Be(3);
            started = Stopwatch.GetTimestamp();
            (await accessor.ReadAsync(TenantSettingKeys.BrandingTheme)).Value!.IsPresent.Should().BeTrue();
            settings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        var probe = await ExplainAsync(read.Unit, read.Observer.Probe!);
        var snapshot = await ExplainAsync(read.Unit, read.Observer.Snapshot!);
        await using var payload = SampleCommand(read.Unit, read.Observer.Snapshot!);
        await using var rows = await payload.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue();
        var types = rows.GetString(rows.GetOrdinal("ContentTypes"));
        var taxonomies = rows.GetString(rows.GetOrdinal("Taxonomies"));
        var bytes = Encoding.UTF8.GetByteCount(types) + Encoding.UTF8.GetByteCount(taxonomies);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            Sample = "local Docker PostgreSQL, app role, 20 observations after warmup; not production p95",
            Tenant = tenant.Slug,
            Generation = initial.Generation,
            ContentTypes = initial.ContentTypes.Count,
            Taxonomies = initial.Taxonomies.Count,
            Bands = initial.Taxonomies.Values.Sum(taxonomy => taxonomy.Bands.Length),
            SnapshotJsonUtf8Bytes = bytes,
            ColdMs = Summary(cold),
            WarmMs = Summary(warm),
            SettingsMs = Summary(settings),
            ProbePlan = probe,
            SnapshotPlan = snapshot
        }));
        cache.FactoryCalls.Should().Be(0);
    }

    private static object Summary(List<double> samples) => new
    {
        Minimum = samples.Min(),
        Median = samples.Order().ElementAt(samples.Count / 2),
        Maximum = samples.Max()
    };
    private static NpgsqlCommand SampleCommand(IUnitOfWork unit, CommandSample sample)
    {
        var command = new NpgsqlCommand(sample.Sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        foreach (var (name, value) in sample.Parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }
    private static async Task<string> ExplainAsync(IUnitOfWork unit, CommandSample sample)
    {
        await using var command = SampleCommand(unit, sample);
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.CommandText;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
