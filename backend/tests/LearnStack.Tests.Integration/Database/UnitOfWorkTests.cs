using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The ambient unit of work against a real database: the connection it owns, the
/// session variables it issues, the context it enlists, and what a nested frame
/// can and cannot do.
/// </summary>
/// <remarks>
/// <para>
/// The container is shared with the schema cases, so every test here either rolls
/// back or removes what it committed. The one that commits does so on
/// <c>outbox_messages</c> and deletes the row through <c>learnstack_platform</c>
/// in a <c>finally</c>, because a row left behind changes a count another case
/// asserts.
/// </para>
/// <para>
/// <b>What this cannot prove.</b>
/// <see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// § What Packet 6 can and cannot prove says the multi-context property needs a
/// second <b>module</b> context, which Phase 03 ships. What is provable now is the
/// half that makes it work: a context resolved through the shared helper reads
/// rows written on the ambient connection inside the same uncommitted
/// transaction, which is only true if it enlisted rather than opening its own.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class UnitOfWorkTests
{
    private readonly SchemaFixture _schema;

    public UnitOfWorkTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task A_resolved_context_becomes_the_session_variables()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(
            Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

        (await ReadAsync(unitOfWork, "SELECT current_setting('app.tenant_id', true)"))
            .Should().Be(SchemaFixture.TenantA.ToString());
        (await ReadAsync(unitOfWork, "SELECT current_setting('app.organization_id', true)"))
            .Should().Be(SchemaFixture.OrgA1.ToString());

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task An_unresolved_context_leaves_every_tenant_owned_table_empty()
    {
        // Between Packet 6 and Packet 7 every request runs against
        // UnresolvedTenantContext, and this is what that costs: the GUCs are set
        // to the empty string, NULLIF turns them into NULL, and a NULL predicate
        // is false for USING and WITH CHECK alike. Fail-closed by construction,
        // not by a filter that does not exist yet.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(UnresolvedTenantContext.Instance);

        (await ReadAsync(unitOfWork, "SELECT current_setting('app.tenant_id', true)"))
            .Should().BeEmpty();

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        (await context.Tenants.CountAsync()).Should().Be(0);
        (await context.Organizations.CountAsync()).Should().Be(0);

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_module_context_enlists_in_the_ambient_transaction()
    {
        // The property the shared registration helper exists for. The row is
        // written on the raw ambient connection and never committed; a context
        // that had opened its own connection could not see it, and a context that
        // saw the connection but not the transaction could not either.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

        await ExecuteAsync(unitOfWork,
            """
            INSERT INTO organizations
                (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
            VALUES (uuidv7(), @tenant, 'enlisted', 'Enlisted', 'Active', now(), @actor, 0)
            """,
            ("tenant", SchemaFixture.TenantA), ("actor", SchemaFixture.Actor));

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        (await context.Organizations.CountAsync()).Should().Be(3,
            "the two seeded organizations plus the uncommitted one on this transaction");

        await unitOfWork.RollbackAsync();

        // And the rollback is real: a fresh scope sees the seeded two.
        await using var after = provider.CreateAsyncScope();
        var afterUnitOfWork = after.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await afterUnitOfWork.BeginTransactionAsync();
        await afterUnitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

        (await after.ServiceProvider.GetRequiredService<TenancyDbContext>()
            .Organizations.CountAsync()).Should().Be(2);

        await afterUnitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_context_resolved_outside_the_transaction_fails_loudly()
    {
        // The alternative is a context that reads zero rows from every
        // tenant-owned table and cannot say why — indistinguishable from "there
        // is no data" at the call site.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside the ambient transaction*");
    }

    [Fact]
    public async Task A_nested_frame_joins_and_its_commit_is_not_a_commit()
    {
        // An application contract reaching a second handler through ISender is
        // how this happens. The inner frame's commit must not make the outer
        // frame's work durable.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var outer = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));
        var transaction = unitOfWork.Transaction;

        var inner = await unitOfWork.BeginTransactionAsync();
        inner.IsOwner.Should().BeFalse();
        outer.IsOwner.Should().BeTrue();
        unitOfWork.Transaction.Should().BeSameAs(transaction, "a nested begin joins, it does not nest");

        await inner.CompleteAsync();

        unitOfWork.HasActiveTransaction.Should().BeTrue(
            "the inner frame resolved; the transaction is still the outer frame's");

        await unitOfWork.RollbackAsync();
        unitOfWork.HasActiveTransaction.Should().BeFalse();
    }

    [Fact]
    public async Task A_nested_failure_makes_the_outer_commit_impossible()
    {
        // ADR-0040 § Nesting: an inner failure marks the transaction
        // rollback-only, and the outer commit throws rather than committing a
        // partial unit.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.RollbackAsync();

        var commit = async () => await unitOfWork.CommitAsync();

        (await commit.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*rollback-only*");
        unitOfWork.HasActiveTransaction.Should().BeFalse("the failed unit was rolled back");
    }

    [Fact]
    public async Task Disposing_with_a_live_transaction_rolls_it_back()
    {
        await using var provider = BuildProvider();
        Guid id;

        await using (var scope = provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginTransactionAsync();
            await unitOfWork.SetTenantContextAsync(Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

            id = Guid.CreateVersion7();
            await ExecuteAsync(unitOfWork,
                """
                INSERT INTO organizations
                    (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
                VALUES (@id, @tenant, 'abandoned', 'Abandoned', 'Active', now(), @actor, 0)
                """,
                ("id", id), ("tenant", SchemaFixture.TenantA), ("actor", SchemaFixture.Actor));

            // No commit, no rollback: the scope simply ends. Committing here would
            // commit work nobody claimed was finished.
        }

        await using var platform = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        await using var check = new NpgsqlCommand(
            "SELECT count(*) FROM organizations WHERE id = @id", (NpgsqlConnection)platform);
        check.Parameters.AddWithValue("id", id);

        (await check.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Fact]
    public async Task A_committed_unit_survives_the_scope()
    {
        // The other half of the previous case, and the only test here that
        // commits — on outbox_messages, and cleaned up in a finally, because the
        // fixture's row counts are what the schema cases assert.
        await using var provider = BuildProvider();
        var correlation = $"00-uow-{Guid.CreateVersion7():N}";

        try
        {
            await using (var scope = provider.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await unitOfWork.BeginTransactionAsync();
                await unitOfWork.SetTenantContextAsync(
                    Resolved(SchemaFixture.TenantA, SchemaFixture.OrgA1));

                await ExecuteAsync(unitOfWork,
                    """
                    INSERT INTO outbox_messages
                        (tenant_id, correlation_id, type, topic, partition_key, payload)
                    VALUES (@tenant, @correlation, 'T', 'learnstack.tenancy.tenant', 'k', '{}')
                    """,
                    ("tenant", SchemaFixture.TenantA), ("correlation", correlation));

                await unitOfWork.CommitAsync();
                unitOfWork.HasActiveTransaction.Should().BeFalse();
            }

            (await CountOutboxAsync(correlation)).Should().Be(1L);
        }
        finally
        {
            await using var platform = await PostgresFixture.OpenAsync(
                _schema.Postgres.PlatformConnectionString);
            await using var cleanup = new NpgsqlCommand(
                "DELETE FROM outbox_messages WHERE correlation_id = @correlation",
                (NpgsqlConnection)platform);
            cleanup.Parameters.AddWithValue("correlation", correlation);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ServiceProvider BuildProvider()
    {
        // The real registration path: the shared helper, the real
        // NpgsqlUnitOfWork, and the application role's data source. Only the
        // connection string differs from the composition root, because the
        // fixture's container is not the one appsettings names.
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();

        return services.BuildServiceProvider();
    }

    private async Task<long> CountOutboxAsync(string correlation)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM outbox_messages WHERE correlation_id = @correlation",
            (NpgsqlConnection)platform);
        command.Parameters.AddWithValue("correlation", correlation);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadAsync(IUnitOfWork unitOfWork, string sql)
    {
        await using var command = unitOfWork.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = unitOfWork.Transaction;

        return (await command.ExecuteScalarAsync()) as string ?? string.Empty;
    }

    private static async Task ExecuteAsync(
        IUnitOfWork unitOfWork, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = (NpgsqlCommand)unitOfWork.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction?)unitOfWork.Transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static StubTenantContext Resolved(Guid tenant, Guid organization) =>
        new(tenant, organization);

    /// <summary>
    /// A resolved context, standing in for what Packet 7's
    /// <c>TenantResolverMiddleware</c> will populate.
    /// </summary>
    private sealed class StubTenantContext(Guid tenant, Guid organization) : ITenantContext
    {
        public bool IsResolved => true;

        public Guid TenantId => tenant;

        public Guid? OrganizationId => organization;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "tenancy";
    }
}
