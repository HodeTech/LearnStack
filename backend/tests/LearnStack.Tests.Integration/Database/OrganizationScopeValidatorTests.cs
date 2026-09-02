using FluentAssertions;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// <c>OrganizationScopeValidator</c> against a real database — the seventh sanctioned
/// setter of <c>app.tenant_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connected as <c>learnstack_app</c>.</b> A test connected as the table owner or
/// as a <c>BYPASSRLS</c> role passes with every policy inert and therefore proves
/// nothing — which is the failure mode ADR-0003 names by hand and the reason this
/// suite's connection string is the application one.
/// </para>
/// <para>
/// <b>What is actually under test is the policy, not the <c>WHERE</c> clause.</b> The
/// interesting case is the one where a well-formed query would happily return
/// another tenant's row: <c>pk_organizations</c> is the surrogate id alone, so an
/// organization id is globally unique and a lookup by it succeeds. The belonging has
/// to be decided by the announcement plus <c>organizations_isolation</c>, and the
/// cases below are chosen so that a validator which forgot either would answer
/// differently.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class OrganizationScopeValidatorTests
{
    private readonly SchemaFixture _schema;

    public OrganizationScopeValidatorTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task An_Organization_Of_The_Tenant_Belongs_To_It()
    {
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var belongs = await Build(dataSource).BelongsToTenantAsync(
            TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));

        belongs.Should().BeTrue();
    }

    [Fact]
    public async Task Another_Tenants_Organization_Does_Not()
    {
        // The case the whole port exists for: a valid organization id from another
        // tenant is a mismatch, not an override. OrgB1 exists, and its id is enough
        // to find it by primary key — so an implementation that read by id and then
        // compared the tenant column in application code would also answer false
        // here. What separates the two is the next case.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);

        var belongs = await Build(dataSource).BelongsToTenantAsync(
            TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgB1));

        belongs.Should().BeFalse();
    }

    [Fact]
    public async Task Without_The_Announcement_The_Row_Is_Invisible()
    {
        // The mechanism, asserted directly. Run the validator's own query on a
        // connection that never issued set_config('app.tenant_id', …) and the policy
        // predicate is NULL, so the row the previous case found is not there at all.
        // This is what makes the port's answer a property of Row Level Security
        // rather than of its WHERE clause — and it is the case that fails if the
        // announcement is ever dropped as redundant.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var read = new NpgsqlCommand(
            """
            SELECT 1 FROM organizations
            WHERE tenant_id = @tenant AND id = @organization AND deleted_at IS NULL
            """,
            connection,
            transaction);
        read.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        read.Parameters.AddWithValue("organization", SchemaFixture.OrgA1);

        (await read.ExecuteScalarAsync()).Should().BeNull(
            "with app.tenant_id unset the policy predicate is NULL and the row is filtered out — "
            + "fail-closed, and the reason the announcement is the mechanism");
    }

    [Fact]
    public async Task Each_Call_Leaves_No_Setting_Behind_On_The_Pooled_Connection()
    {
        // set_config(..., true) is SET LOCAL's function form and is discarded at
        // COMMIT. A session-level write would survive on a pooled connection into
        // whatever borrowed it next — which, on this path, is another tenant's
        // request.
        //
        // NoResetOnClose is what makes this case mean anything. Measured: without it
        // the mutation to set_config(..., false) passes, because Npgsql sends
        // DISCARD ALL when a connection returns to the pool and cleans up after the
        // bug. That is the driver's behaviour, not this code's, and it is exactly
        // what a PgBouncer in transaction-pooling mode does not do — the deployment
        // the corpus keeps naming. With the reset suppressed and the pool held to one
        // connection, the second borrow provably gets the same physical connection
        // the validator just released, in the state the validator left it.
        var builder = new NpgsqlDataSourceBuilder(_schema.Postgres.AppConnectionString);
        builder.ConnectionStringBuilder.MaxPoolSize = 1;
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        await using var dataSource = builder.Build();

        await Build(dataSource).BelongsToTenantAsync(
            TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var read = new NpgsqlCommand(
            "SELECT NULLIF(current_setting('app.tenant_id', true), '')", connection);

        var leftBehind = await read.ExecuteScalarAsync();

        (leftBehind is null or DBNull).Should().BeTrue(
            "the setting was transaction-local and the transaction is over");
    }

    [Fact]
    public async Task The_Validators_Own_Statement_Sequence_Cannot_Write()
    {
        // Four carriers call this a "short READ-ONLY transaction" — the port's doc, the
        // Standards 11 setter table, the glossary and ADR-0040 Amendment 3 — and
        // learnstack_app holds INSERT/UPDATE/DELETE on organizations, so one statement
        // is the whole of what makes the claim true. Measured: deleting it left all
        // five other cases here green, which makes it exactly the line a later refactor
        // of the shared connection boilerplate drops without noticing.
        //
        // WHAT THIS PROVES, AND WHAT IT DOES NOT. The validator's connection and
        // transaction are private locals with no seam, so this reproduces its sequence
        // rather than intercepting it — which means it establishes that the statement
        // has the effect claimed, and NOT that the validator issues it. Measured:
        // deleting the statement from production left this case green, which is the
        // failure this packet keeps finding, so it is named here rather than left for
        // the next reader to discover. The other half — that the production file issues
        // it, before the announcement — is asserted by
        // Organizations_Are_Read_By_Composite_Key, which already scans this file's SQL.
        // Neither leg is sufficient alone.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var readOnly = new NpgsqlCommand(
            "SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync();
        }

        await using (var announce = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            announce.Parameters.AddWithValue("tenant", SchemaFixture.TenantA.ToString());
            await announce.ExecuteNonQueryAsync();
        }

        await using var write = new NpgsqlCommand(
            "UPDATE organizations SET slug = slug WHERE tenant_id = @tenant AND id = @id",
            connection,
            transaction);
        write.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        write.Parameters.AddWithValue("id", SchemaFixture.OrgA1);

        var act = async () => await write.ExecuteNonQueryAsync();

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be("25006", "read_only_sql_transaction — the announcement is a "
                + "read's setup and must not also license a write");
    }

    [Fact]
    public async Task A_Soft_Deleted_Organization_Does_Not_Belong()
    {
        // deleted_at is the corpus's soft-delete column and the policy does not read
        // it, so this is the validator's own clause. An organization someone removed
        // must not keep vouching for a claim that names it.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        var validator = Build(dataSource);
        var organization = OrganizationId.From(SchemaFixture.OrgA2);

        (await validator.BelongsToTenantAsync(TenantId.From(SchemaFixture.TenantA), organization))
            .Should().BeTrue("precondition: it belongs before it is deleted");

        await SetDeletedAsync(dataSource, SchemaFixture.TenantA, SchemaFixture.OrgA2, deleted: true);

        try
        {
            (await validator.BelongsToTenantAsync(TenantId.From(SchemaFixture.TenantA), organization))
                .Should().BeFalse();
        }
        finally
        {
            await SetDeletedAsync(dataSource, SchemaFixture.TenantA, SchemaFixture.OrgA2, deleted: false);
        }
    }

    /// <summary>
    /// Flips <c>deleted_at</c> under the tenant's own context, because the table's
    /// policies are qualified <c>TO learnstack_app</c> and the update's
    /// <c>WITH CHECK</c> requires <c>app.tenant_id</c> to be the row's tenant.
    /// </summary>
    private static async Task SetDeletedAsync(
        NpgsqlDataSource dataSource, Guid tenant, Guid organization, bool deleted)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var announce = new NpgsqlCommand(
            "SELECT set_config('app.tenant_id', @tenant, true)", connection, transaction))
        {
            // ToString: set_config is (text, text, boolean) and has no uuid overload.
            announce.Parameters.AddWithValue("tenant", tenant.ToString());
            await announce.ExecuteNonQueryAsync();
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE organizations SET deleted_at = @at WHERE tenant_id = @tenant AND id = @id",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue(
                "at", deleted ? new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero) : DBNull.Value);
            update.Parameters.AddWithValue("tenant", tenant);
            update.Parameters.AddWithValue("id", organization);
            await update.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static OrganizationScopeValidator Build(NpgsqlDataSource dataSource) =>
        new(new Lazy<NpgsqlDataSource>(() => dataSource));
}
