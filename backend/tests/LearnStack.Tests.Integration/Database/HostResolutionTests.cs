using System.Diagnostics.Metrics;
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

    private static CachedHostToTenantResolver BuildResolver(NpgsqlDataSource dataSource)
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero));
        var meterFactory = MeterServices.GetRequiredService<IMeterFactory>();

        return new CachedHostToTenantResolver(
            new InMemoryCacheService(clock, meterFactory),
            new UnknownHostCache(clock, new UnknownHostCacheOptions()),
            new HostResolutionOptions(),
            new Lazy<NpgsqlDataSource>(() => dataSource));
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
