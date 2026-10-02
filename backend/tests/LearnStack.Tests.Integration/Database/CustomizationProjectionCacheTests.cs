using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Caching;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Projections;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class CustomizationProjectionTests
{
    [Fact]
    public async Task Cold_warm_partial_and_locale_reads_use_two_or_one_select_and_never_a_shared_factory()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var cache = new CacheProbe();
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var request = Request([new("announcement", 1)], [new("proficiency", 1)]);
        var first = (await read.Reader.ReadAsync(request)).Value!;
        read.Observer.Selects.Should().Be(2);
        cache.GetCalls.Should().Be(2);
        cache.SetCalls.Should().Be(2);
        cache.Options.Should().OnlyContain(options => options == new CacheOptions(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(15)));
        var generation = first.Generation!.Value;
        cache.Keys.Should().BeEquivalentTo([FamilyKey(context.TenantId, "content-types", generation), FamilyKey(context.TenantId, "taxonomies", generation)]);
        var warm = (await read.Reader.ReadAsync(request with { RequestedLocale = "EN-us" })).Value!;
        warm.ContentTypes[new("announcement", 1)].DisplayName.Locale.Should().Be("en");
        read.Observer.Selects.Should().Be(3, "warm read still probes the durable generation, but never definitions");
        cache.SetCalls.Should().Be(2, "a hit does not extend the original TTL");
        await cache.RemoveAsync(FamilyKey(context.TenantId, "taxonomies", generation));
        (await read.Reader.ReadAsync(request)).Value!.Taxonomies.Should().ContainSingle();
        read.Observer.Selects.Should().Be(5, "a partial hit loads a coherent pair instead of merging generations");
        cache.FactoryCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("timeout")]
    public async Task Cache_faults_degrade_to_complete_database_results_with_bounded_diagnostics(string fault)
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var cache = new CacheProbe { Fault = fault };
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var result = await read.Reader.ReadAsync(Request([new("announcement", 1)], [new("proficiency", 1)]));
        result.IsSuccess.Should().BeTrue();
        result.Value!.ContentTypes.Should().ContainSingle();
        result.Value.Taxonomies.Should().ContainSingle();
        result.Value.MissingContentTypes.Should().BeEmpty();
        read.Observer.Selects.Should().Be(2);
        cache.Logger.Messages.Should().ContainSingle().Which.Should().NotContain("private-cache-data");
        cache.Logger.Exceptions.Should().BeEmpty();
        cache.FactoryCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_during_cache_get_or_set_propagates_without_factory_work(bool duringSet)
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        using var cancellation = new CancellationTokenSource();
        await using var cache = new CacheProbe { Cancel = cancellation, CancelDuringSet = duringSet };
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var invoke = () => read.Reader.ReadAsync(Request([new("announcement", 1)], []), cancellation.Token);
        await invoke.Should().ThrowAsync<OperationCanceledException>();
        cache.Tokens.Should().OnlyContain(token => token == cancellation.Token);
        cache.Logger.Messages.Should().BeEmpty();
        cache.FactoryCalls.Should().Be(0);
        read.Observer.Selects.Should().Be(duringSet ? 2 : 1);
    }

    [Fact]
    public async Task A_database_failure_is_not_converted_to_a_cache_miss_or_empty_projection()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await CreateRevisionAsync(source, context, 1);
        await using (var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var revoke = new NpgsqlCommand("REVOKE SELECT ON tenant_level_taxonomies FROM learnstack_app", (NpgsqlConnection)owner))
            await revoke.ExecuteNonQueryAsync();
        await using var cache = new CacheProbe();
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var invoke = () => read.Reader.ReadAsync(Request([new("profile", 1)], []));
        (await invoke.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        cache.SetCalls.Should().Be(0);
    }

    [Fact]
    public async Task Warm_cache_never_admits_closed_or_unannounced_transactions()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var cache = new CacheProbe();
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var request = Request([new("announcement", 1)], []);
        (await read.Reader.ReadAsync(request)).IsSuccess.Should().BeTrue();
        cache.Keys.Should().NotBeEmpty();
        var gets = cache.GetCalls;
        await read.Frame.FailAsync();
        var closed = () => read.Reader.ReadAsync(request);
        await closed.Should().ThrowAsync<TenantContextMissingException>();
        await using var unannounced = await ReadSession.OpenAsync(source, context, announce: false, cache: cache);
        await ((Func<Task<LearnStack.SharedKernel.Results.Result<DefinitionProjection>>>)(() => unannounced.Reader.ReadAsync(request)))
            .Should().ThrowAsync<TenantContextMissingException>();
        cache.GetCalls.Should().Be(gets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_mutation_bypasses_warm_cache_before_bump_and_rollback_reissued_generation_is_clean(bool taxonomy)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await CreateRevisionAsync(source, context, 1);
        await CreateRevisionAsync(source, context, 2, publish: false);
        await using var cache = new CacheProbe();
        var request = Request([new("profile", 1), new("profile", 2)], [new("levels", 1), new("levels", 2)]);
        long speculative;
        await using (var read = await ReadSession.OpenAsync(source, context, cache: cache))
        {
            var before = (await read.Reader.ReadAsync(request)).Value!;
            before.MissingContentTypes.Should().Contain(new DefinitionRevision("profile", 2));
            var gets = cache.GetCalls; var sets = cache.SetCalls;
            if (taxonomy)
            {
                var store = read.Services.GetRequiredService<ITenantLevelTaxonomyStore>();
                var current = (await store.FindActiveAsync("levels"))!;
                var successor = (await store.FindAsync(TenantLevelTaxonomyId.From(await IdAsync(read.Unit, "tenant_level_taxonomies", "levels", 2))))!;
                current.Deprecate(new SystemClock(), UserId.SystemActor);
                await store.UpdateAsync(current);
                successor.Publish(new SystemClock(), UserId.SystemActor);
                await store.UpdateAsync(successor);
            }
            else
            {
                var store = read.Services.GetRequiredService<ITenantContentTypeStore>();
                var current = (await store.FindActiveAsync("profile"))!;
                var successor = (await store.FindAsync(TenantContentTypeId.From(await IdAsync(read.Unit, "tenant_content_types", "profile", 2))))!;
                current.Deprecate(new SystemClock(), UserId.SystemActor);
                await store.UpdateAsync(current);
                successor.Publish(new SystemClock(), UserId.SystemActor);
                await store.UpdateAsync(successor);
            }
            read.State.IsDirty.Should().BeTrue();
            var between = (await read.Reader.ReadAsync(request)).Value!;
            between.Generation.Should().Be(before.Generation, "the supported store saved, but the generation has not advanced yet");
            if (taxonomy) between.Taxonomies[new("levels", 2)].Status.Should().Be(DefinitionStatus.Active);
            else between.ContentTypes[new("profile", 2)].Status.Should().Be(DefinitionStatus.Active);
            speculative = await read.Services.GetRequiredService<ICustomizationGenerationStore>().BumpAsync(context.TenantId);
            (await read.Reader.ReadAsync(request)).Value!.Generation.Should().Be(speculative);
            cache.GetCalls.Should().Be(gets);
            cache.SetCalls.Should().Be(sets, "uncommitted definitions must never fill the reissuable generation key");
            await read.Frame.FailAsync();
            read.State.IsDirty.Should().BeTrue("the DI-scope flag is never reset after rollback");
            await ((Func<Task>)(() => read.Unit.BeginTransactionAsync())).Should().ThrowAsync<InvalidOperationException>();
            cache.GetCalls.Should().Be(gets);
            cache.SetCalls.Should().Be(sets);
        }
        // A different committed Draft reuses the speculative number without granting access to v2.
        if (taxonomy)
            (await SendAsync(source, context, new RegisterTenantLevelTaxonomyCommand(Guid.CreateVersion7(), "levels", 3, Label,
                [new("only", Label, 0)]))).IsSuccess.Should().BeTrue();
        else
            (await SendAsync(source, context, new RegisterTenantContentTypeCommand(Guid.CreateVersion7(), "profile", 3, Label, Profile, "default-card")))
                .IsSuccess.Should().BeTrue();
        await using var fresh = await ReadSession.OpenAsync(source, context, cache: cache);
        var committed = (await fresh.Reader.ReadAsync(request)).Value!;
        committed.Generation.Should().Be(speculative);
        committed.ContentTypes[new("profile", 1)].Status.Should().Be(DefinitionStatus.Active);
        committed.Taxonomies[new("levels", 1)].Status.Should().Be(DefinitionStatus.Active);
        committed.MissingContentTypes.Should().Contain(new DefinitionRevision("profile", 2));
        committed.MissingTaxonomies.Should().Contain(new DefinitionRevision("levels", 2));
        fresh.Observer.Selects.Should().Be(2);
        fresh.State.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Rollback_only_without_a_customization_write_also_bypasses_warm_cache()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var cache = new CacheProbe();
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        var request = Request([new("announcement", 1)], []);
        (await read.Reader.ReadAsync(request)).IsSuccess.Should().BeTrue();
        var gets = cache.GetCalls; var sets = cache.SetCalls;
        read.State.IsDirty.Should().BeFalse();
        read.Unit.MarkRollbackOnly();
        (await read.Reader.ReadAsync(request)).IsSuccess.Should().BeTrue();
        read.Observer.Selects.Should().Be(4);
        cache.GetCalls.Should().Be(gets);
        cache.SetCalls.Should().Be(sets);
    }

    [Fact]
    public async Task Shared_warm_cache_alternates_tenants_without_crossing_definition_or_band_values()
    {
        await using var source = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var cache = new CacheProbe();
        for (var index = 0; index < 4; index++)
        {
            var foreign = index % 2 == 1;
            var context = new SeedTenantContext(TenantId.From(foreign ? SchemaFixture.TenantB : SchemaFixture.TenantA), null);
            await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
            await AssertAppRoleAsync(read.Unit);
            var result = (await read.Reader.ReadAsync(Request([new("announcement", 1)], [new("proficiency", 1)]))).Value!;
            result.Taxonomies[new("proficiency", 1)].Bands.Should().ContainSingle().Which.Key.Should().Be(foreign ? "starter" : "beginner");
            read.Observer.Selects.Should().Be(index < 2 ? 2 : 1);
        }
        cache.Keys.Should().HaveCount(4);
        cache.FactoryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Independent_process_L1_maps_follow_the_same_durable_generation_without_events()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await CreateRevisionAsync(source, context, 1);
        await using var firstCache = new CacheProbe();
        await using var secondCache = new CacheProbe();
        var request = Request([new("profile", 1), new("profile", 2)], [new("levels", 1), new("levels", 2)]);
        foreach (var cache in new[] { firstCache, secondCache })
        {
            await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
            (await read.Reader.ReadAsync(request)).Value!.MissingContentTypes.Should().Contain(new DefinitionRevision("profile", 2));
            var localized = (await read.Reader.ReadAsync(request with { RequestedLocale = "EN-us" })).Value!;
            localized.ContentTypes[new("profile", 1)].DisplayName.Should().Be(new LearnStack.SharedKernel.Localization.ResolvedLocalizedText("Label", "en"));
            read.Observer.Selects.Should().Be(3);
        }
        await CreateRevisionAsync(source, context, 2);
        foreach (var cache in new[] { firstCache, secondCache })
        {
            await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
            var changed = (await read.Reader.ReadAsync(request)).Value!;
            changed.ContentTypes[new("profile", 2)].Status.Should().Be(DefinitionStatus.Active);
            changed.Taxonomies[new("levels", 2)].Status.Should().Be(DefinitionStatus.Active);
            changed.ContentTypes[new("profile", 1)].Status.Should().Be(DefinitionStatus.Deprecated);
            (await read.Reader.ReadAsync(request)).IsSuccess.Should().BeTrue();
            read.Observer.Selects.Should().Be(3);
            cache.Keys.Should().HaveCount(4, "old committed keys remain stranded until TTL reclamation");
        }
    }

    private static string FamilyKey(TenantId tenant, string family, long generation) =>
        CacheKey.ForTenant(tenant.Value, "customization", family, "v" + generation);

    internal sealed class CacheProbe : ICacheService, IAsyncDisposable
    {
        private readonly ServiceProvider _owner;
        private readonly InMemoryCacheService _inner;
        private int _gets; private int _sets;
        public CacheProbe()
        {
            var services = new ServiceCollection(); services.AddMetrics();
            _owner = services.BuildServiceProvider();
            _inner = new InMemoryCacheService(new SystemClock(), _owner.GetRequiredService<IMeterFactory>());
        }
        public int GetCalls => _gets;
        public int SetCalls => _sets;
        public int FactoryCalls { get; private set; }
        public string? Fault { get; set; }
        public CancellationTokenSource? Cancel { get; set; }
        public bool CancelDuringSet { get; set; }
        public ConcurrentBag<string> Keys { get; } = [];
        public ConcurrentBag<CacheOptions?> Options { get; } = [];
        public ConcurrentBag<CancellationToken> Tokens { get; } = [];
        public CacheLogger Logger { get; } = new();
        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _gets); Tokens.Add(cancellationToken);
            if (!CancelDuringSet) Cancel?.Cancel();
            if (Fault == "get") throw new IOException("private-cache-data");
            if (Fault == "timeout") throw new OperationCanceledException("private-cache-data");
            return await _inner.GetAsync<T>(key, cancellationToken);
        }
        public Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            FactoryCalls++;
            throw new InvalidOperationException("An ambient loader must never enter a shared factory flight.");
        }
        public async Task SetAsync<T>(string key, T value, CacheOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _sets); Tokens.Add(cancellationToken); Options.Add(options);
            if (CancelDuringSet) Cancel?.Cancel();
            if (Fault == "set") throw new IOException("private-cache-data");
            await _inner.SetAsync(key, value, options, cancellationToken);
            if (!Keys.Contains(key)) Keys.Add(key);
        }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => _inner.RemoveAsync(key, cancellationToken);
        public ValueTask DisposeAsync() => _owner.DisposeAsync();
    }

    internal sealed class CacheLogger : ILogger<DefinitionFamilyCache>
    {
        public List<string> Messages { get; } = [];
        public List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null) Exceptions.Add(exception);
        }
    }
}
