using System.Diagnostics.Metrics;
using System.Globalization;
using FluentAssertions;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// <c>CachedHostToTenantResolver</c> against a real database: the one read that
/// happens <b>before</b> any tenant context exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connected as <c>learnstack_app</c>.</b> The policy on
/// <c>platform_host_to_tenant</c> is qualified <c>TO learnstack_app</c> and admits
/// exactly the row the resolver announces through <c>app.resolving_host</c>. A
/// test connected as the owner or as a <c>BYPASSRLS</c> role would pass with the
/// announcement removed and prove nothing — the announcement is the mechanism.
/// </para>
/// <para>
/// Each case builds its own resolver rather than resolving the singleton, because
/// the singleton's caches would carry answers between cases and the cases are
/// about what the database returns.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class HostResolutionTests
{
    private static readonly ServiceProvider MeterServices = new ServiceCollection()
        .AddMetrics()
        .BuildServiceProvider();

    private static readonly DateTimeOffset Origin =
        new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly SchemaFixture _schema;

    public HostResolutionTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task Host_Resolves_With_No_Tenant_Context_Under_Rls()
    {
        // The property the whole resolver exists for. app.tenant_id is never set —
        // there is no tenant yet, that is what is being asked — so the policy's
        // tenant branch is NULL and only the app.resolving_host branch can admit
        // the row. If the announcement were dropped, both branches would be NULL
        // and this would come back empty.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var resolver = BuildResolver(dataSource);

        var resolution = await resolver.ResolveAsync(SchemaFixture.HostA);

        resolution.Should().NotBeNull();
        resolution!.TenantId.Value.Should().Be(SchemaFixture.TenantA);
        resolution.OrganizationId.Should().NotBeNull();
        resolution.OrganizationId!.Value.Value.Should().Be(SchemaFixture.OrgA1);
    }

    [Fact]
    public async Task A_Read_Only_Transaction_Admits_The_Announcement_And_Refuses_A_Write()
    {
        // Why the resolver's lookup can be read-only at all, which is not obvious: it
        // announces `app.resolving_host`, and an announcement looks like a write. It is
        // not — `set_config(..., true)` is permitted inside a READ ONLY transaction — so
        // the resolver gives up nothing by taking the restriction, and `learnstack_app`
        // keeps write grants on this table, so the transaction is what refuses.
        //
        // This is the PostgreSQL fact the design rests on, driven on a transaction built
        // the way the resolver builds its own. What it deliberately does NOT claim is
        // that the resolver issues the statement: a replica cannot say that about the
        // original, and asserting it here would be a test agreeing with itself. The
        // source scan `Out_Of_Band_Setters_Open_Read_Only_Transactions` carries that half.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await using var probe = await dataSource.OpenConnectionAsync();
        await using var transaction = await probe.BeginTransactionAsync();

        // First statement, before the announcement. SET TRANSACTION must precede any
        // other statement or PostgreSQL refuses it outright, which is why the order in
        // the resolver is part of what the scan checks.
        await using (var readOnly = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY", probe, transaction))
        {
            await readOnly.ExecuteNonQueryAsync();
        }

        await using (var announce = new NpgsqlCommand(
            "SELECT set_config('app.resolving_host', @host, true)", probe, transaction))
        {
            announce.Parameters.AddWithValue("host", SchemaFixture.HostA);
            await announce.ExecuteNonQueryAsync();
        }

        await using (var setting = new NpgsqlCommand(
            "SELECT current_setting('transaction_read_only')", probe, transaction))
        {
            (await setting.ExecuteScalarAsync()).Should().Be("on",
                "the announcement did not force the transaction to be writable");
        }

        // And the read the resolver actually performs still succeeds under it.
        await using (var read = new NpgsqlCommand(
            "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host", probe, transaction))
        {
            read.Parameters.AddWithValue("host", SchemaFixture.HostA);
            (await read.ExecuteScalarAsync()).Should().Be(1L);
        }

        var write = async () =>
        {
            await using var refused = new NpgsqlCommand(
                "INSERT INTO platform_host_to_tenant (host, tenant_id, is_active, "
                + "is_publicly_live) VALUES ('x.example.com', @tenant, true, true)",
                probe,
                transaction);
            refused.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            await refused.ExecuteNonQueryAsync();
        };

        (await write.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("25006",
                "read_only_sql_transaction — learnstack_app still holds the grant, so the "
                + "restriction is what refuses rather than the privilege");

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Invalidation_Clears_The_Positive_Entry_Not_Only_The_Negative_One()
    {
        // The direction that matters. Clearing only the negative cache covers activation —
        // a host that starts resolving — which is the harmless half. A host deactivated,
        // released, or re-pointed at a different tenant keeps serving the PREVIOUS
        // tenant's answer for the whole positive TTL otherwise: a cross-tenant answer
        // coming from a cache rather than from a policy.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var resolver = BuildResolver(dataSource);

        (await resolver.ResolveAsync(SchemaFixture.HostA)).Should().NotBeNull();

        // Cached now: a second lookup must not reach the database, which is what the
        // sibling case pins. Re-point the row underneath it, as a host lifecycle would.
        await using (var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString))
        await using (var repoint = new NpgsqlCommand(
            "UPDATE platform_host_to_tenant SET is_publicly_live = false WHERE host = @host",
            (NpgsqlConnection)platform))
        {
            repoint.Parameters.AddWithValue("host", SchemaFixture.HostA);
            await repoint.ExecuteNonQueryAsync();
        }

        try
        {
            // Still served from the positive cache — the row changed, the answer did not.
            (await resolver.ResolveAsync(SchemaFixture.HostA)).Should().NotBeNull(
                "the positive entry is still warm, which is what makes invalidation matter");

            await ((IHostResolutionInvalidator)resolver).InvalidateAsync(SchemaFixture.HostA);

            (await resolver.ResolveAsync(SchemaFixture.HostA)).Should().BeNull(
                "invalidation cleared the positive entry, so the next lookup re-read the "
                + "row and found it no longer publicly live");
        }
        finally
        {
            await using var platform = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var restore = new NpgsqlCommand(
                "UPDATE platform_host_to_tenant SET is_publicly_live = true WHERE host = @host",
                (NpgsqlConnection)platform);
            restore.Parameters.AddWithValue("host", SchemaFixture.HostA);
            await restore.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task An_Unmapped_Host_Resolves_To_Nothing()
    {
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var resolver = BuildResolver(dataSource);

        (await resolver.ResolveAsync("nobody.example.com")).Should().BeNull();
    }

    [Theory]
    [InlineData(false, true, "an inactive mapping is not a tenant's host yet")]
    [InlineData(true, false, "a mapping that is not publicly live may not answer an anonymous request")]
    [InlineData(false, false, "neither flag set is not half a host")]
    public async Task Both_Flags_Gate_The_Answer(bool isActive, bool isPubliclyLive, string because)
    {
        // The two flags are distinct states and the row exists from submission
        // onward, before DNS points anywhere. Reading only one of them is how a
        // guessed hostname serves an unlaunched tenant's catalog to a stranger, or
        // how a released-then-re-registered domain keeps serving the previous
        // tenant. ADR-0036 invalidates this resolver's cache on the transaction
        // that flips EITHER flag, which is only meaningful if both feed the answer.
        const string Host = "staged.example.com";

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        try
        {
            await SeedHostAsync(dataSource, Host, isActive, isPubliclyLive);

            var resolver = BuildResolver(dataSource);

            (await resolver.ResolveAsync(Host)).Should().BeNull(because);
        }
        finally
        {
            await RemoveHostAsync(Host);
        }
    }

    [Fact]
    public async Task A_Row_With_Both_Flags_Set_Resolves()
    {
        // The companion the three cases above need: without it they would pass
        // against a resolver that never returns anything, and the theory would be
        // asserting the absence of a feature rather than the presence of a gate.
        const string Host = "staged-live.example.com";

        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        try
        {
            await SeedHostAsync(dataSource, Host, isActive: true, isPubliclyLive: true);

            var resolution = await BuildResolver(dataSource).ResolveAsync(Host);

            resolution.Should().NotBeNull();
            resolution!.TenantId.Value.Should().Be(SchemaFixture.TenantA);
            resolution.OrganizationId.Should().BeNull("this row is tenant-wide");
        }
        finally
        {
            await RemoveHostAsync(Host);
        }
    }

    [Fact]
    public async Task A_Second_Lookup_Of_An_Unmapped_Host_Does_Not_Reach_The_Database()
    {
        // The negative cache, observed through the only thing a caller can see: a
        // disposed data source. If the second lookup went to the database it would
        // throw rather than answer.
        var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var resolver = BuildResolver(dataSource);

        (await resolver.ResolveAsync("gone.example.com")).Should().BeNull();

        await dataSource.DisposeAsync();

        (await resolver.ResolveAsync("gone.example.com")).Should().BeNull(
            "the second answer comes from the negative cache, not from a connection");
    }

    [Fact]
    public async Task A_Second_Lookup_Of_A_Mapped_Host_Does_Not_Reach_The_Database()
    {
        var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var resolver = BuildResolver(dataSource);

        (await resolver.ResolveAsync(SchemaFixture.HostA)).Should().NotBeNull();

        await dataSource.DisposeAsync();

        (await resolver.ResolveAsync(SchemaFixture.HostA)).Should().NotBeNull(
            "the second answer comes from ICacheService");
    }

    [Fact]
    public async Task Concurrent_First_Lookups_Of_One_Host_Open_One_Connection()
    {
        // The coalescing had no test at any layer: deleting it left 1003/1003
        // green. It exists because the split that sends positives and negatives to
        // different structures forfeits GetOrSetAsync's single flight, and the
        // flight it replaces is a PostgreSQL transaction opened on an anonymous,
        // pre-authentication path — N simultaneous first requests for one cold
        // host would otherwise be N transactions.
        //
        // Counted at the physical-connection initializer, which is the only place
        // that sees a real open and is a seam the data source already exposes.
        var opens = 0;

        var builder = new NpgsqlDataSourceBuilder(_schema.Postgres.AppConnectionString);
        builder.UsePhysicalConnectionInitializer(
            _ => Interlocked.Increment(ref opens),
            _ => { Interlocked.Increment(ref opens); return Task.CompletedTask; });

        await using var dataSource = builder.Build();
        var resolver = BuildResolver(dataSource);

        // Warm the pool first, so the count below is about flights and not about
        // however many physical connections the pool happened to need.
        (await resolver.ResolveAsync(SchemaFixture.HostB)).Should().NotBeNull();
        var warm = opens;

        using var release = new ManualResetEventSlim(false);

        var callers = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            release.Wait();
            return await resolver.ResolveAsync(SchemaFixture.HostA);
        })).ToArray();

        release.Set();
        var resolutions = await Task.WhenAll(callers);

        resolutions.Should().OnlyContain(resolution => resolution != null);
        (opens - warm).Should().BeLessThanOrEqualTo(1,
            "twelve simultaneous first lookups of one cold host are one round trip");
    }

    [Fact]
    public async Task A_Cancelled_Caller_Still_Leaves_The_Answer_Published()
    {
        // The flight runs on CancellationToken.None precisely so one caller
        // hanging up does not cancel the lookup others wait on — but if the cache
        // write lived in the caller's tail, a lookup whose only caller cancelled
        // would complete and then throw its answer away, and the next request
        // would pay for the same round trip.
        //
        // The window is a real one: an ACCESS EXCLUSIVE lock on the table blocks
        // the resolver's SELECT, so the caller can be cancelled while its flight
        // is demonstrably mid-read. The first version of this case cancelled the
        // token BEFORE calling and then polled with uncancelled reads, which is
        // two false negatives in one: InMemoryCacheService.GetAsync throws on a
        // cancelled token as its second statement, so no flight was ever created,
        // and each poll published the answer itself. Measured — with the write
        // moved back into the caller's tail, the whole file stayed green.
        await using var lockHolder = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var publishes = new PublishCountingCache(NewCache());
        var resolver = BuildResolver(dataSource, publishes);

        await using var blocking = await lockHolder.OpenConnectionAsync();
        await using var holdingTransaction = await blocking.BeginTransactionAsync();
        await using (var takeLock = new NpgsqlCommand(
            "LOCK TABLE platform_host_to_tenant IN ACCESS EXCLUSIVE MODE", blocking, holdingTransaction))
        {
            await takeLock.ExecuteNonQueryAsync();
        }

        using var hangUp = new CancellationTokenSource();
        var caller = resolver.ResolveAsync(SchemaFixture.HostA, hangUp.Token);

        await WaitUntilBlockedOnTheTableAsync(lockHolder);

        // The caller goes away with its flight still inside the SELECT.
        await hangUp.CancelAsync();
        var act = async () => await caller;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Release the read, and the flight — which nobody is waiting on any more —
        // must still publish. Waited for on the COUNTER and never by resolving
        // again: a poll that calls ResolveAsync publishes the answer itself, which
        // is exactly how the first version of this case passed against the shape
        // it was written to reject.
        await holdingTransaction.CommitAsync();

        for (var attempt = 0; attempt < 100 && !publishes.Observed; attempt++)
        {
            await Task.Delay(50);
        }

        publishes.Observed.Should().BeTrue(
            "the flight publishes its answer even though its only caller had gone");
        publishes.Count.Should().Be(1);

        await dataSource.DisposeAsync();

        (await resolver.ResolveAsync(SchemaFixture.HostA)).Should().NotBeNull(
            "with the database gone, only a published answer can serve this");
    }

    [Fact]
    public async Task Twelve_Waiters_On_One_Cold_Host_Publish_One_Answer()
    {
        // The other half of "published inside the flight": once, however many
        // waiters there are. Counting physical connections proves the flight ran
        // once; counting writes proves the ANSWER was written once, and by the
        // flight rather than by each waiter's tail. Measured, the write moved into
        // the caller's tail leaves every connection-counting case green — this is
        // the assertion that separates the two shapes.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var publishes = new PublishCountingCache(NewCache());
        var resolver = BuildResolver(dataSource, publishes);

        using var release = new ManualResetEventSlim(false);

        var callers = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            release.Wait();
            return await resolver.ResolveAsync(SchemaFixture.HostA);
        })).ToArray();

        release.Set();
        var resolutions = await Task.WhenAll(callers);

        resolutions.Should().OnlyContain(resolution => resolution != null);
        publishes.Count.Should().Be(1,
            "twelve waiters share one flight, and the flight publishes once");
    }

    /// <summary>
    /// Blocks until a backend is waiting on the table lock, so the caller can be
    /// cancelled while its flight is provably mid-read.
    /// </summary>
    private static async Task WaitUntilBlockedOnTheTableAsync(NpgsqlDataSource observer)
    {
        // pg_stat_activity rather than a delay: a sleep long enough to be reliable
        // on a loaded CI machine is a sleep every run pays for, and one short
        // enough not to be is a flake.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = observer.CreateCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock'
                  AND query LIKE '%platform_host_to_tenant%'
                """);

            if (Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException(
            "The resolver never blocked on the table lock, so the flight was never "
            + "caught mid-read and this case would prove nothing.");
    }

    private static InMemoryCacheService NewCache() =>
        new(new FixedClock(Origin), MeterServices.GetRequiredService<IMeterFactory>());

    private static CachedHostToTenantResolver BuildResolver(
        NpgsqlDataSource dataSource, ICacheService? cache = null)
    {
        var clock = new FixedClock(Origin);

        return new CachedHostToTenantResolver(
            cache ?? NewCache(),
            new UnknownHostCache(clock, new UnknownHostCacheOptions()),
            new HostResolutionOptions(),
            new Lazy<NpgsqlDataSource>(() => dataSource));
    }

    /// <summary>
    /// Counts what the resolver publishes, so a case can assert <b>who</b> wrote
    /// the answer and <b>how many times</b> — neither of which a connection count
    /// or a later cache hit can distinguish.
    /// </summary>
    private sealed class PublishCountingCache(ICacheService inner) : ICacheService
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public bool Observed => Count > 0;

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
            inner.GetAsync<T>(key, cancellationToken);

        public Task SetAsync<T>(
            string key, T value, CacheOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return inner.SetAsync(key, value, options, cancellationToken);
        }

        public Task<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, Task<T>> factory,
            CacheOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetOrSetAsync(key, factory, options, cancellationToken);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
            inner.RemoveAsync(key, cancellationToken);
    }

    /// <summary>
    /// Writes a host row and commits it, because the resolver reads on a
    /// connection of its own and cannot see an uncommitted one.
    /// </summary>
    /// <remarks>
    /// As <c>learnstack_app</c> under the tenant's own context: the table's
    /// policies are qualified <c>TO learnstack_app</c>, so the owner is denied, and
    /// the insert's <c>WITH CHECK</c> requires <c>app.tenant_id</c> to be the row's
    /// tenant. The seed does the same, and this is the one table where that is not
    /// optional.
    /// </remarks>
    private static async Task SeedHostAsync(
        NpgsqlDataSource dataSource, string host, bool isActive, bool isPubliclyLive)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var context = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("tenant", SchemaFixture.TenantA.ToString());
            await context.ExecuteNonQueryAsync();
        }

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO platform_host_to_tenant (host, tenant_id, organization_id, is_active, is_publicly_live)
            VALUES (@host, @tenant, NULL, @active, @live)
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("host", host);
            insert.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            insert.Parameters.AddWithValue("active", isActive);
            insert.Parameters.AddWithValue("live", isPubliclyLive);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// Removes the row through <c>learnstack_platform</c>, which bypasses the
    /// policy — the cleanup must not depend on the thing under test.
    /// </summary>
    private async Task RemoveHostAsync(string host)
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM platform_host_to_tenant WHERE host = @host";
        var parameter = delete.CreateParameter();
        parameter.ParameterName = "host";
        parameter.Value = host;
        delete.Parameters.Add(parameter);
        await delete.ExecuteNonQueryAsync();
    }
}
