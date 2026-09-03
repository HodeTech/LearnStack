using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The single-default locale invariant, in the one place it holds across concurrent
/// transactions.
/// </summary>
/// <remarks>
/// <para>
/// <b>The aggregate guard and the index answer different questions, and only one of them
/// is a guarantee.</b> <c>Tenant.AddLocale</c> and <c>Tenant.SetDefaultLocale</c> clear
/// the incumbent before promoting, which produces a readable error for the caller that
/// asks twice in one unit of work. Two transactions each promoting a different locale
/// both pass that guard — neither can see the other's uncommitted row — and one of them
/// has to lose at the database. That is what these cases are about.
/// </para>
/// <para>
/// Connected as <c>learnstack_app</c>: the invariant has to hold for the role that
/// actually writes, and a bypass role would answer a different question.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantLocaleDefaultTests
{
    private readonly SchemaFixture _schema;

    public TenantLocaleDefaultTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task A_Second_Default_For_One_Tenant_Is_Refused()
    {
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await AnnounceAsync(connection, transaction, SchemaFixture.TenantA);

        // The fixture already publishes tr-TR as tenant A's default, so this IS the
        // second one — no setup needed, and using the seeded incumbent means the case
        // exercises the state a real tenant is in rather than one it builds for itself.
        var second = async () => await InsertLocaleAsync(
            connection, transaction, SchemaFixture.TenantA, "en-US", isDefault: true);

        (await second.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("23505",
                "ux_tenant_locales_tenant_id_is_default is what makes one default a "
                + "guarantee rather than a convention");
    }

    [Fact]
    public async Task A_Non_Default_Locale_Is_Not_Constrained()
    {
        // The index is PARTIAL, and the partiality is the point: a tenant publishes in
        // many locales and exactly one of them is the default. An unfiltered unique index
        // on tenant_id would allow one locale per tenant, which is not the invariant.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await AnnounceAsync(connection, transaction, SchemaFixture.TenantB);

        // Tenant B already publishes en-US as its default; these are additional
        // non-default locales beside it.
        await InsertLocaleAsync(connection, transaction, SchemaFixture.TenantB, "tr-TR", isDefault: false);
        await InsertLocaleAsync(connection, transaction, SchemaFixture.TenantB, "de-DE", isDefault: false);

        // No exception is the assertion; the count confirms both landed beside the seed.
        (await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM tenant_locales WHERE tenant_id = @tenant",
            SchemaFixture.TenantB)).Should().Be(3);
    }

    [Fact]
    public async Task Clearing_The_Incumbent_First_Is_What_Makes_A_Swap_Possible()
    {
        // The order the aggregate uses, and why it is not cosmetic. EF emits one UPDATE
        // per changed row, so a swap is two statements — and against a unique index the
        // order decides whether the second one is legal.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        await using var connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await AnnounceAsync(connection, transaction, SchemaFixture.TenantA);

        // tr-TR is the seeded default; en-US is the challenger.
        await InsertLocaleAsync(connection, transaction, SchemaFixture.TenantA, "en-US", isDefault: false);

        // New-first: refused.
        var newFirst = async () => await ExecuteAsync(connection, transaction,
            "UPDATE tenant_locales SET is_default = true WHERE tenant_id = @tenant AND locale = 'en-US'",
            SchemaFixture.TenantA);
        (await newFirst.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");

        await transaction.RollbackAsync(CancellationToken.None);

        // Old-first: succeeds. Same two statements, opposite order.
        await using var second = await connection.BeginTransactionAsync(CancellationToken.None);
        await AnnounceAsync(connection, second, SchemaFixture.TenantA);
        await InsertLocaleAsync(connection, second, SchemaFixture.TenantA, "en-US", isDefault: false);

        await ExecuteAsync(connection, second,
            "UPDATE tenant_locales SET is_default = false WHERE tenant_id = @tenant AND locale = 'tr-TR'",
            SchemaFixture.TenantA);
        await ExecuteAsync(connection, second,
            "UPDATE tenant_locales SET is_default = true WHERE tenant_id = @tenant AND locale = 'en-US'",
            SchemaFixture.TenantA);

        (await ScalarAsync<string>(connection, second,
            "SELECT locale FROM tenant_locales WHERE tenant_id = @tenant AND is_default",
            SchemaFixture.TenantA)).Should().Be("en-US");

        // Rolled back: the fixture seeds exact row counts other classes assert on.
        await second.RollbackAsync(CancellationToken.None);
    }

    private static async Task AnnounceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid tenant)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT set_config('app.tenant_id', @tenant, true)";
        command.Parameters.Add(new NpgsqlParameter("tenant", tenant.ToString()));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task InsertLocaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenant,
        string locale,
        bool isDefault)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO tenant_locales (tenant_id, locale, is_default, is_enabled, sort)
            VALUES (@tenant, @locale, @isDefault, true, 0)
            """;
        command.Parameters.Add(new NpgsqlParameter("tenant", tenant));
        command.Parameters.Add(new NpgsqlParameter("locale", locale));
        command.Parameters.Add(new NpgsqlParameter("isDefault", isDefault));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, Guid tenant)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("tenant", tenant));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Guid tenant)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenant;
        command.Parameters.Add(parameter);
        return (T)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
