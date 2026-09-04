using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Both migration chains applied and then reversed against a real database.
/// </summary>
/// <remarks>
/// <para>
/// Database Standards § Migrations says reversal is expected for a non-destructive
/// migration, and both chains here only create. Neither reversed as first shipped:
/// the tenancy <c>Down()</c> aborted on its first statement, because
/// <c>DROP FUNCTION fn_organization_id_immutable()</c> fails while the trigger on
/// <c>tenant_settings</c> depends on it, and would have aborted again on
/// <c>DROP TABLE organizations</c>, which three foreign keys reference.
/// <c>DropTable</c> emits a bare <c>DROP TABLE</c> with no <c>CASCADE</c>, so the
/// alphabetical order EF scaffolds is not a working order.
/// </para>
/// <para>
/// The point of the case is that it is measured rather than reasoned about. A
/// comment asserting the ordering is correct is exactly what shipped the broken
/// one.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MigrationRollbackTests : IClassFixture<MigrationRollbackFixture>
{
    private readonly MigrationRollbackFixture _fixture;

    public MigrationRollbackTests(MigrationRollbackFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task BothChainsReverseToAnEmptySchema()
    {
        await using var connection = await PostgresFixture.OpenAsync(
            _fixture.Postgres.MigrationConnectionString);

        // Applied state first, so a rollback that reversed nothing because nothing
        // was there cannot pass.
        (await CountAsync(connection, TablesQuery)).Should().Be(12L,
            "eight tenancy tables, two platform tables, and the two history tables");
        (await CountAsync(connection, FunctionQuery)).Should().Be(1L,
            "fn_organization_id_immutable backs the tenant_settings trigger");

        await _fixture.RollBackAsync();

        // The history tables survive: `database update 0` empties them, it does not
        // drop them. Everything the two migrations created is gone.
        (await CountAsync(connection, TablesQuery)).Should().Be(2L);
        (await CountAsync(connection, FunctionQuery)).Should().Be(0L);
        (await CountAsync(connection, PolicyQuery)).Should().Be(0L);
    }

    private const string TablesQuery =
        "SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'";

    private const string FunctionQuery =
        "SELECT count(*) FROM pg_proc WHERE proname = 'fn_organization_id_immutable'";

    private const string PolicyQuery =
        "SELECT count(*) FROM pg_policies WHERE schemaname = 'public'";

    private static async Task<long> CountAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// Its own container: this fixture destroys the schema, so it cannot share one
/// with the cases that read it.
/// </summary>
public sealed class MigrationRollbackFixture : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();

        await using var tenancy = CreateTenancy();
        await tenancy.Database.MigrateAsync();

        await using var platform = CreatePlatform();
        await platform.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await Postgres.DisposeAsync();

    /// <summary>
    /// Reverses both chains, platform first — the order a developer undoing a
    /// packet would use, and the one that proves neither chain depends on the
    /// other's tables.
    /// </summary>
    public async Task RollBackAsync()
    {
        await using (var platform = CreatePlatform())
        {
            await platform.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        }

        await using var tenancy = CreateTenancy();
        await tenancy.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
    }

    private TenancyDbContext CreateTenancy() =>
        new(
            new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);

    private PlatformDbContext CreatePlatform() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(Postgres.MigrationConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable))
            .Options);
}
