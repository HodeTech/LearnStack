using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Reversing <c>platform_entitlement_cache_valid_until_nullable</c> on a POPULATED table.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MigrationRollbackTests"/> reverses every chain against an empty schema, which
/// is why the first version of this migration's <c>Down()</c> passed it: the scaffolder's
/// backfill is an <c>UPDATE … WHERE valid_until IS NULL</c>, and as the
/// <c>NOBYPASSRLS</c> owner of a <c>FORCE ROW LEVEL SECURITY</c> table it matched zero rows
/// — so on a table holding a real projection the <c>SET NOT NULL</c> after it failed. The
/// documented backfill never ran. The review of Packet 9 measured it; this is that
/// measurement kept.
/// </para>
/// <para>
/// Its own container, because it moves the schema backwards.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class EntitlementExpiryReversalTests : IClassFixture<EntitlementExpiryReversalFixture>
{
    private const string Reversed = "20260910130327_platform_entitlement_cache_valid_until_nullable";
    private const string Previous = "20260908120441_tenants_reject_platform_sentinel";
    private static readonly Guid Tenant = Guid.Parse("33333333-3333-7333-8333-333333333333");

    private readonly EntitlementExpiryReversalFixture _fixture;

    public EntitlementExpiryReversalTests(EntitlementExpiryReversalFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_projection_with_no_expiry_refuses_the_reversal_and_says_why()
    {
        await SeedProjectionWithNoExpiryAsync();

        // Refused, with the reason and the way out — not the bare "contains null values" a
        // backfill that saw nothing left behind.
        var refused = async () => await MigrateTenancyAsync(Previous);

        // EF's execution strategy wraps what the server said; the server's words are the
        // assertion.
        var thrown = (await refused.Should().ThrowAsync<InvalidOperationException>())
            .WithInnerException<PostgresException>().Which;
        thrown.SqlState.Should().Be(PostgresErrorCodes.ObjectNotInPrerequisiteState);
        thrown.MessageText.Should().Contain("is refused").And.Contain("no scheduled expiry");
        thrown.Hint.Should().Contain("learnstack_platform");

        // And nothing moved: the migration is still applied, the column still nullable, the
        // projection still saying "no expiry" rather than "expired in year 1".
        (await IsAppliedAsync(Reversed)).Should().BeTrue();
        (await NullableAsync()).Should().Be("YES");
        (await NullExpiriesAsync()).Should().Be(1);

        // The documented remedy: the table is a cache the Hub re-sends. Emptied, the
        // reversal restores exactly Packet 6's column — NOT NULL and no default of its own,
        // which the first Down() added and nothing declared.
        await EmptyCacheAsync();
        await MigrateTenancyAsync(Previous);

        (await NullableAsync()).Should().Be("NO");
        (await DefaultAsync()).Should().BeNull();

        // And forward again, to the state the model snapshot describes.
        await MigrateTenancyAsync(target: null);

        (await IsAppliedAsync(Reversed)).Should().BeTrue();
        (await NullableAsync()).Should().Be("YES");
        (await DefaultAsync()).Should().BeNull();
    }

    /// <summary>One tenant and one projection with no scheduled expiry — a trial, say.</summary>
    /// <remarks>
    /// As the owner, with the tenant announced: the canonical tenant-owned policy carries no
    /// <c>TO</c> clause, so it binds the owner too, and this is the per-tenant shape § Data
    /// Migrations prescribes.
    /// </remarks>
    private async Task SeedProjectionWithNoExpiryAsync()
    {
        await using var owner = await PostgresFixture.OpenAsync(_fixture.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();

        await SchemaQueries.SetTenantAsync(owner, transaction, Tenant);
        await SchemaQueries.ExecuteAsync(owner, transaction,
            """
            INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (@tenant, 'gamma', 'Gamma', 'Trial', now(), '00000000-0000-7000-8000-000000000001', 0)
            """, ("tenant", Tenant));
        await SchemaQueries.ExecuteAsync(owner, transaction,
            """
            INSERT INTO platform_entitlement_cache
                (tenant_id, plan_code, features, limits, compliance, valid_until, source)
            VALUES (@tenant, 'trial', '{}', '{}', '{}', NULL, 'hub')
            """, ("tenant", Tenant));

        await transaction.CommitAsync();
    }

    private async Task EmptyCacheAsync()
    {
        await using var platform = await PostgresFixture.OpenAsync(_fixture.Postgres.PlatformConnectionString);
        await SchemaQueries.ExecuteAsync(platform, null, "DELETE FROM platform_entitlement_cache");
    }

    private async Task MigrateTenancyAsync(string? target)
    {
        await using var tenancy = new TenancyDbContext(
            new DbContextOptionsBuilder<TenancyDbContext>()
                .UseNpgsql(_fixture.Postgres.MigrationConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(TenancyDbContextFactory.HistoryTable))
                .Options,
            StaticTenantContextAccessor.Unresolved);

        await tenancy.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task<bool> IsAppliedAsync(string migration) =>
        await ScalarAsync<long>(
            $"SELECT count(*) FROM \"{TenancyDbContextFactory.HistoryTable}\" WHERE \"MigrationId\" = @id",
            ("id", migration)) == 1;

    private Task<string> NullableAsync() =>
        ScalarAsync<string>(
            "SELECT is_nullable FROM information_schema.columns "
            + "WHERE table_name = 'platform_entitlement_cache' AND column_name = 'valid_until'");

    private async Task<string?> DefaultAsync()
    {
        var value = await ScalarAsync<object>(
            "SELECT column_default FROM information_schema.columns "
            + "WHERE table_name = 'platform_entitlement_cache' AND column_name = 'valid_until'");

        return value is DBNull ? null : (string)value;
    }

    /// <summary>Counted as the platform role, which is the only one that sees every row.</summary>
    private async Task<long> NullExpiriesAsync()
    {
        await using var platform = await PostgresFixture.OpenAsync(_fixture.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM platform_entitlement_cache WHERE valid_until IS NULL",
            (NpgsqlConnection)platform);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var owner = await PostgresFixture.OpenAsync(_fixture.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)owner);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>A container of its own, every chain applied.</summary>
public sealed class EntitlementExpiryReversalFixture : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();
        await MigrationChains.ApplyAllAsync(Postgres.MigrationConnectionString);
    }

    public async Task DisposeAsync() => await Postgres.DisposeAsync();
}
