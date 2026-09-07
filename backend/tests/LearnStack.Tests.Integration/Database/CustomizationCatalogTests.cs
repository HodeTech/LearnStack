using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The one query on the write path that only a provider can prove, as
/// <c>learnstack_app</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ITenantLevelTaxonomyCatalog</c> is what an <c>x-taxonomy</c> resolves
/// through, and it is the only shipped query nothing else executes: the built-in
/// seed's <c>card</c> declares no extension, so the seeder never reaches it with a
/// key. A LINQ expression no test runs is a translation nobody has checked —
/// <c>Contains</c> over an array becomes <c>= ANY</c> or it becomes an
/// <c>InvalidOperationException</c> on the first tenant that authors a level
/// reference, in Phase 02d.
/// </para>
/// <para>
/// <b>The setup write commits, and the case removes it.</b> The catalogue reads on
/// its own scope's connection, so a row left in an open transaction is a row it
/// cannot see. The cleanup runs under an announcement for the reason
/// <c>WriteStoreConflictTests</c> states: under <c>FORCE ROW LEVEL SECURITY</c> a
/// <c>DELETE</c> with no <c>app.tenant_id</c> reports <c>DELETE 0</c>, and these
/// probe rows belong to the tenant whose exact counts other cases assert.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CustomizationCatalogTests
{
    private static readonly FixedClock Clock = new(
        new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero));

    private readonly SchemaFixture _schema;

    public CustomizationCatalogTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task The_catalogue_answers_with_the_keys_the_tenant_declared()
    {
        await using var provider = BuildProvider();

        var id = TenantLevelTaxonomyId.From(Guid.CreateVersion7());

        await using (var writing = provider.CreateAsyncScope())
        {
            await BeginAsync(writing);
            await writing.ServiceProvider.GetRequiredService<ITenantLevelTaxonomyStore>()
                .AddAsync(Draft(id, "catalogue-probe"));
            await writing.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None);
        }

        try
        {
            await using var reading = provider.CreateAsyncScope();
            await BeginAsync(reading);

            var existing = await reading.ServiceProvider
                .GetRequiredService<ITenantLevelTaxonomyCatalog>()
                .ExistingAsync(["catalogue-probe", "nothing-declared-this"]);

            existing.Should().BeEquivalentTo(
                ["catalogue-probe"],
                "the answer is the subset that exists, and the key nobody declared "
                + "is what an unresolvable x-taxonomy looks like");
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [Fact]
    public async Task Two_overlapping_bumps_do_not_lose_one()
    {
        // The property the single statement exists for. A read-modify-write returns
        // the same number to both callers, and every cache key one of them meant to
        // strand stays reachable — measured as a mutation: turning the upsert into
        // C# read-then-write left the whole suite green, because nothing ran two of
        // them at once.
        // Two providers, so the two bumps are unambiguously two connections: one
        // provider handed both scopes the same open connection and the second
        // statement failed as "a command is already in progress" rather than
        // blocking, which is not the thing under test.
        await using var alpha = BuildProvider();
        await using var beta = BuildProvider();

        await using var first = alpha.CreateAsyncScope();
        await using var second = beta.CreateAsyncScope();

        await BeginAsync(first);
        await BeginAsync(second);

        var one = await Generations(first).BumpAsync(TenantId.From(SchemaFixture.TenantA));

        // The second bump blocks on the first's row lock until it commits — which is
        // the whole mechanism. Started before the commit, and then WAITED FOR: without
        // the wait this case passes either way, because a second bump that has not yet
        // read anything reads the committed value and is right by accident. A
        // read-modify-write survived it, measured, until the wait went in.
        var blocked = Task.Run(async () =>
            await Generations(second).BumpAsync(TenantId.From(SchemaFixture.TenantA)));

        await WaitUntilBlockedAsync();

        await first.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None);

        var two = await blocked;

        try
        {
            two.Should().Be(one + 1, "the second bump reads what the first committed");
        }
        finally
        {
            await second.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Blocks until a backend on this server is waiting on a lock.
    /// </summary>
    /// <remarks>
    /// The synchronisation point the case needs, read from the server rather than
    /// guessed with a sleep: a fixed delay is a flake on a slow machine and a
    /// false pass on a fast one.
    /// </remarks>
    private async Task WaitUntilBlockedAsync()
    {
        await using var watcher = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            // `pg_locks`, not `pg_stat_activity`: a non-superuser sees every row of
            // the latter but NULL in `wait_event_type` for a backend belonging to
            // another role, and the watcher connects as the owner while the blocked
            // backend is `learnstack_app`. Measured — the poll never fired.
            var waiting = await SchemaQueries.CountAsync(
                watcher, "SELECT count(*) FROM pg_locks WHERE NOT granted");

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException(
            "The second bump never blocked on the first's row lock. Either the lock is "
            + "not taken — which is the defect this case exists to catch — or the "
            + "statement finished before this loop observed it.");
    }

    private static ICustomizationGenerationStore Generations(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ICustomizationGenerationStore>();

    private static TenantLevelTaxonomy Draft(TenantLevelTaxonomyId id, string key) =>
        TenantLevelTaxonomy.Create(
            id,
            TenantId.From(SchemaFixture.TenantA),
            key,
            1,
            LocalizedText.From(("en", "Probe")),
            Clock,
            UserId.From(SchemaFixture.Actor));

    private static async Task BeginAsync(AsyncServiceScope scope)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        if (unitOfWork.HasActiveTransaction)
        {
            return;
        }

        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        var context = new AnnouncedTenant(SchemaFixture.TenantA);
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
        await unitOfWork.SetTenantContextAsync(context);
    }

    private async Task DeleteAsync(TenantLevelTaxonomyId id)
    {
        await using var owner = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(owner, transaction, SchemaFixture.TenantA);
        await SchemaQueries.ExecuteAsync(owner, transaction,
            "DELETE FROM tenant_level_taxonomies WHERE id = @id", ("id", id.Value));

        await transaction.CommitAsync();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor, FlowingTenant>();
        services.AddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<CustomizationDbContext>();
        services.AddScoped<ITenantLevelTaxonomyStore, TenantLevelTaxonomyStore>();
        services.AddScoped<ITenantLevelTaxonomyCatalog, TenantLevelTaxonomyCatalog>();
        services.AddScoped<ICustomizationGenerationStore, CustomizationGenerationStore>();
        return services.BuildServiceProvider();
    }

    private sealed class FlowingTenant : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }

    private sealed class AnnouncedTenant(Guid tenant) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId { get; } = TenantId.From(tenant);

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;

        public TenantContextOrigin? Origin => TenantContextOrigin.HostAndClaim;

        public string? CorrelationId => null;

        public string? ModuleName => "customization";
    }
}
