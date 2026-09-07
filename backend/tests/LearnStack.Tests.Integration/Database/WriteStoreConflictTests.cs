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
/// What <see cref="WriteStoreTracking"/> turns a real database failure into, as
/// <c>learnstack_app</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Against the real provider, because that is the only thing that can produce
/// these.</b> Both translations exist so a handler can answer a caller without
/// naming a database, and both are keyed on something only PostgreSQL and EF
/// decide: a <c>23505</c> carried inside a <c>DbUpdateException</c>, and an
/// <c>UPDATE</c> that matched no row because the concurrency token moved. A fake
/// store throwing the translated type proves the handler's arm; only this proves
/// the arm is ever entered.
/// </para>
/// <para>
/// <b>The setup write commits; the write under test does not.</b> A real 23505 and
/// a real stale token both need a row another transaction can already see, so the
/// probe row is committed on purpose — which is why each case ends by deleting it
/// under the announcement its policy requires, rather than by rolling anything
/// back. The container is shared and the schema cases assert exact counts for the
/// tenant these probes belong to.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class WriteStoreConflictTests
{
    private static readonly FixedClock Clock = new(
        new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero));

    private readonly SchemaFixture _schema;

    public WriteStoreConflictTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task A_stale_concurrency_token_becomes_the_port_s_concurrency_type()
    {
        // Two scopes, one row. The first advances row_version and commits nothing;
        // the second holds the value it read before that and its UPDATE matches
        // nothing. Untranslated this is a DbUpdateConcurrencyException, which
        // HttpStatusMap has no arm for — a 500 for the outcome the token exists to
        // report.
        await using var provider = BuildProvider();

        var id = TenantContentTypeId.From(Guid.CreateVersion7());
        await CommitAsync(provider, async store =>
            await store.AddAsync(Draft(id, "stale-probe")));

        try
        {
            await using var winner = provider.CreateAsyncScope();
            await using var loser = provider.CreateAsyncScope();

            var held = await LoadAsync(loser, id);
            var moved = await LoadAsync(winner, id);

            // The winner's write lands and is committed, so the loser's original
            // row_version is genuinely behind rather than merely different in memory.
            moved!.Rename(LocalizedText.From(("en", "moved")), Clock, UserId.SystemActor);
            await Store(winner).UpdateAsync(moved);
            await Commit(winner);

            held!.Rename(LocalizedText.From(("en", "held")), Clock, UserId.SystemActor);
            var stale = async () =>
                await Store(loser).UpdateAsync(held);

            await stale.Should().ThrowAsync<AggregateConcurrencyException>(
                "an UPDATE that matched no row is the token working, not a fault");
        }
        finally
        {
            await DeleteAsync(id);
        }
    }

    [Fact]
    public async Task A_uniqueness_violation_still_becomes_the_port_s_conflict_type()
    {
        // The arm the concurrency catch sits in front of. Asserted here as well as
        // there, because a catch ordered or filtered wrongly would swallow this one
        // and nothing else in the suite drives a real 23505 through these stores.
        await using var provider = BuildProvider();

        var first = TenantContentTypeId.From(Guid.CreateVersion7());
        await CommitAsync(provider, async store =>
            await store.AddAsync(Draft(first, "collide-probe")));

        try
        {
            await using var scope = provider.CreateAsyncScope();
            await BeginAsync(scope);

            var duplicate = Draft(TenantContentTypeId.From(Guid.CreateVersion7()), "collide-probe");
            var collide = async () =>
                await Store(scope).AddAsync(duplicate);

            var thrown = await collide.Should().ThrowAsync<AggregateConflictException>();
            thrown.Which.ConstraintName
                .Should().Be("ux_tenant_content_types_tenant_id_key_schema_version");
        }
        finally
        {
            await DeleteAsync(first);
        }
    }

    private static TenantContentType Draft(TenantContentTypeId id, string key) =>
        TenantContentType.Create(
            id,
            TenantId.From(SchemaFixture.TenantA),
            key,
            1,
            LocalizedText.From(("en", "Probe")),
            """{"type":"object"}""",
            "default-card",
            Clock,
            UserId.From(SchemaFixture.Actor));

    private static ITenantContentTypeStore Store(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITenantContentTypeStore>();

    private static async Task<TenantContentType?> LoadAsync(
        AsyncServiceScope scope, TenantContentTypeId id)
    {
        await BeginAsync(scope);
        return await Store(scope).FindAsync(id);
    }

    private static async Task BeginAsync(AsyncServiceScope scope)
    {
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        if (unitOfWork.HasActiveTransaction)
        {
            return;
        }

        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        var context = new AnnouncedContext(SchemaFixture.TenantA);
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
        await unitOfWork.SetTenantContextAsync(context);
    }

    private static Task Commit(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .CommitAsync(CancellationToken.None);

    /// <summary>Writes through the real store and commits, so a second scope sees it.</summary>
    private static async Task CommitAsync(
        ServiceProvider provider, Func<ITenantContentTypeStore, Task> write)
    {
        await using var scope = provider.CreateAsyncScope();
        await BeginAsync(scope);
        await write(Store(scope));
        await Commit(scope);
    }

    /// <summary>
    /// Removes what a case committed, under the announcement its policy requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The announcement is not optional, and the owner does not escape it.</b>
    /// These tables are under <c>FORCE ROW LEVEL SECURITY</c>, so
    /// <c>learnstack_migration</c> is subject to its own policy — and <c>USING</c>
    /// is the only gate a <c>DELETE</c> has. Measured: with no
    /// <c>app.tenant_id</c> the statement reports <c>DELETE 0</c> and the row
    /// stays; with it, <c>DELETE 1</c>. A cleanup that silently removes nothing is
    /// worse than none, because the probe rows belong to
    /// <see cref="SchemaFixture.TenantA"/> — the tenant whose exact row counts other
    /// cases in this shared container assert.
    /// </para>
    /// <para>
    /// One transaction, committed: the row has to be gone for the next case, not
    /// rolled back with the read that removed it.
    /// </para>
    /// </remarks>
    private async Task DeleteAsync(TenantContentTypeId id)
    {
        await using var owner = await PostgresFixture.OpenAsync(
            _schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(owner, transaction, SchemaFixture.TenantA);
        await SchemaQueries.ExecuteAsync(owner, transaction,
            "DELETE FROM tenant_content_types WHERE id = @id", ("id", id.Value));

        await transaction.CommitAsync();
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor, FlowingAccessor>();
        services.AddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<CustomizationDbContext>();
        services.AddScoped<ITenantContentTypeStore, TenantContentTypeStore>();
        return services.BuildServiceProvider();
    }

    private sealed class FlowingAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }

    private sealed class AnnouncedContext(Guid tenant) : ITenantContext
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
