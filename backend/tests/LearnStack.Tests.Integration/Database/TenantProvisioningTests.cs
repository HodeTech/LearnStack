using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Provisioning a tenant against the shipped policies, as <c>learnstack_app</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot be a unit test.</b> The whole of
/// <see href="../../../../docs/decisions/0042-cross-aggregate-provisioning-transaction.md">ADR-0042</see>
/// is a claim about what one transaction may write under Row Level Security. Against
/// fakes, every case below passes with the announcement deleted, with the writes split
/// across two transactions, and with the policies disabled.
/// </para>
/// <para>
/// <b>As <c>learnstack_app</c>, which is the point.</b> The role connects
/// <c>NOBYPASSRLS</c>, so a row that commits here commits because a policy permitted it.
/// Run as <c>learnstack_migration</c> or <c>learnstack_platform</c> these cases would
/// pass with every policy inert, which is the failure mode the repository's hard rules
/// name by role.
/// </para>
/// <para>
/// Each case removes what it committed: the container is shared with the schema cases,
/// several of which assert exact row counts. Cleanup runs as <c>learnstack_platform</c>
/// because the rows belong to tenants that no longer have a context to announce.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class TenantProvisioningTests
{
    private static readonly FixedClock Clock = new(
        new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero));

    private readonly SchemaFixture _schema;

    public TenantProvisioningTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task Provisioning_commits_the_tenant_its_default_organization_and_the_assignment()
    {
        // The claim ADR-0042 exists to make: three writes across two aggregate roots,
        // one transaction, one commit, and a tenant that is never observable without a
        // default organization.
        var command = Command();

        try
        {
            var result = await ProvisionAsync(command);

            result.IsSuccess.Should().BeTrue();

            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM tenants WHERE id = @id", command.TenantId.Value))
                .Should().Be(1L);
            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM organizations WHERE id = @id",
                command.DefaultOrganizationId.Value))
                .Should().Be(1L);
            (await ScalarAsPlatformAsync(
                """
                SELECT count(*) FROM tenants
                WHERE id = @id AND default_organization_id IS NOT NULL
                """,
                command.TenantId.Value))
                .Should().Be(1L, "the back-reference commits on the same transaction");
        }
        finally
        {
            await CleanUpAsync(command);
        }
    }

    [Fact]
    public async Task A_provisioning_that_fails_part_way_commits_nothing()
    {
        // The reason a second transaction was not an option. A tenant whose default
        // organization failed to commit is a tenant no request can serve: every
        // organization-scoped read filters on a column that is null, and nothing in the
        // schema would ever repair it. Measured here by colliding the organization's id
        // with a seeded row — the unique index fires regardless of RLS, so the second
        // write raises 23505 while the first has already succeeded.
        var command = Command() with { DefaultOrganizationId = OrganizationId.From(SchemaFixture.OrgA1) };

        try
        {
            var provision = () => ProvisionAsync(command);

            await provision.Should().ThrowAsync<Exception>();

            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM tenants WHERE id = @id", command.TenantId.Value))
                .Should().Be(0L,
                    "the tenant insert had already succeeded when the organization failed, "
                    + "and it must roll back with it");
        }
        finally
        {
            await CleanUpAsync(command);
        }
    }

    [Fact]
    public async Task The_announcement_confines_the_transaction_to_the_tenant_being_created()
    {
        // The announcement is not a formality that unlocks writing: it is a value the
        // policies compare against, so a provisioning transaction can write the tenant it
        // named and nothing else. Without this the seam would be a hole — any request
        // implementing IProvisionsTenant could open a transaction and write into a tenant
        // it chose.
        var command = Command();

        try
        {
            var write = () => ProvisionAsync(
                command,
                handler: async (services, _) =>
                {
                    var unitOfWork = services.GetRequiredService<IUnitOfWork>();
                    await ExecuteAsync(
                        unitOfWork,
                        """
                        INSERT INTO organizations
                            (id, tenant_id, slug, display_name, status,
                             created_at, created_by, row_version)
                        VALUES (uuidv7(), @tenant, 'smuggled', 'Smuggled', 'Active',
                                now(), @actor, 0)
                        """,
                        ("tenant", SchemaFixture.TenantA),
                        ("actor", SchemaFixture.Actor));

                    return Result.Ok(Provisioned(command));
                });

            (await write.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be("42501",
                    "the policy compares against the announced tenant, and TenantA is not it");

            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM organizations WHERE slug = 'smuggled'"))
                .Should().Be(0L);
        }
        finally
        {
            await CleanUpAsync(command);
        }
    }

    [Fact]
    public async Task The_announcement_does_not_survive_the_commit()
    {
        // The connection goes back to the pool the moment the scope disposes, and the
        // next request to draw it is an ordinary tenant-facing one. Were the announcement
        // session-scoped rather than transaction-scoped, that request would begin under
        // the provisioned tenant's id — and any read issued before its own announcement
        // would answer from the wrong tenant.
        //
        // NoResetOnClose and a pool of one are what make this case mean anything.
        // Measured: without them the mutation to set_config(..., false) PASSES, because
        // Npgsql sends DISCARD ALL when a connection returns to the pool and cleans up
        // after the bug. That is the driver's behaviour, not this code's, and it is
        // precisely what a PgBouncer in transaction-pooling mode does not do. With the
        // reset suppressed the second borrow is provably the same physical connection,
        // in the state provisioning left it.
        var builder = new NpgsqlDataSourceBuilder(_schema.Postgres.AppConnectionString);
        builder.ConnectionStringBuilder.MaxPoolSize = 1;
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        await using var dataSource = builder.Build();

        var command = Command();

        try
        {
            await using (var provider = BuildProvider(dataSource))
            {
                (await ProvisionAsync(provider, command)).IsSuccess.Should().BeTrue();
            }

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand(
                "SELECT NULLIF(current_setting('app.tenant_id', true), '')", connection);

            var leftBehind = await read.ExecuteScalarAsync();

            (leftBehind is null or DBNull).Should().BeTrue(
                "the announcement was transaction-local and the transaction is over");
        }
        finally
        {
            await CleanUpAsync(command);
        }
    }

    [Fact]
    public async Task A_request_that_does_not_provision_still_fails_closed_when_unresolved()
    {
        // The other half of the branch. Adding the provisioning path must not have
        // widened what an unresolved context can do generally: a request that does not
        // implement IProvisionsTenant still takes the ordinary path, which writes the
        // empty string, and every tenant-owned write is refused.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var behavior = new TransactionBehavior<Probe, Result<string>>(
            unitOfWork,
            UnresolvedTenantContext.Instance,
            NullLogger<TransactionBehavior<Probe, Result<string>>>.Instance);

        var write = () => behavior.Handle(
            new Probe(),
            async () =>
            {
                await ExecuteAsync(
                    unitOfWork,
                    """
                    INSERT INTO tenants (id, slug, display_name, status, created_at,
                                         created_by, row_version)
                    VALUES (uuidv7(), 'unannounced', 'Unannounced', 'Trial', now(),
                            @actor, 0)
                    """,
                    ("actor", SchemaFixture.Actor));

                return Result.Ok("unreachable");
            },
            CancellationToken.None);

        (await write.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be("42501");

        (await ScalarAsPlatformAsync("SELECT count(*) FROM tenants WHERE slug = 'unannounced'"))
            .Should().Be(0L);
    }

    [Fact]
    public async Task A_resolved_caller_cannot_provision_a_tenant_it_did_not_authenticate_for()
    {
        // The confused deputy, closed by the database rather than by a check. A caller
        // authenticated for TenantA who sends a provisioning command naming a new tenant
        // takes the ORDINARY path — the branch requires !IsResolved — so the transaction
        // is announced with A, and the insert of the new tenant's row is refused.
        var command = Command();

        try
        {
            var provision = () => ProvisionAsync(
                command, context: new ResolvedContext(SchemaFixture.TenantA, SchemaFixture.OrgA1));

            var failure = (await provision.Should().ThrowAsync<Exception>()).Which;

            // Unwrapped, because EF wraps a failing SaveChanges in DbUpdateException and
            // the SQLSTATE is the whole assertion — "some exception" would also pass with
            // the tenant announced correctly and the schema missing.
            Unwrap(failure).Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be("42501",
                    "the announcement is A's, and the tenants policy checks id = app.tenant_id");

            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM tenants WHERE id = @id", command.TenantId.Value))
                .Should().Be(0L);
        }
        finally
        {
            await CleanUpAsync(command);
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one provisioning through the real <see cref="TransactionBehavior{TRequest,TResponse}"/>,
    /// the real handler and the real write stores.
    /// </summary>
    /// <remarks>
    /// The behavior is constructed rather than resolved because the full MediatR pipeline
    /// would drag in seven other behaviors and prove nothing more; the handler IS
    /// resolved, because its discoverability by assembly scan is what the composition
    /// root depends on.
    /// </remarks>
    private async Task<Result<ProvisionedTenantDto>> ProvisionAsync(
        ProvisionTenantCommand command,
        ITenantContext? context = null,
        Func<IServiceProvider, ProvisionTenantCommand, Task<Result<ProvisionedTenantDto>>>? handler = null)
    {
        await using var provider = BuildProvider();
        return await ProvisionAsync(provider, command, context, handler);
    }

    private static async Task<Result<ProvisionedTenantDto>> ProvisionAsync(
        ServiceProvider provider,
        ProvisionTenantCommand command,
        ITenantContext? context = null,
        Func<IServiceProvider, ProvisionTenantCommand, Task<Result<ProvisionedTenantDto>>>? handler = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var behavior = new TransactionBehavior<ProvisionTenantCommand, Result<ProvisionedTenantDto>>(
            services.GetRequiredService<IUnitOfWork>(),
            context ?? UnresolvedTenantContext.Instance,
            NullLogger<TransactionBehavior<ProvisionTenantCommand, Result<ProvisionedTenantDto>>>
                .Instance);

        return await behavior.Handle(
            command,
            () => handler is null
                ? services
                    .GetRequiredService<IRequestHandler<ProvisionTenantCommand,
                        Result<ProvisionedTenantDto>>>()
                    .Handle(command, CancellationToken.None)
                : handler(services, command),
            CancellationToken.None);
    }

    private ServiceProvider BuildProvider(NpgsqlDataSource? dataSource = null)
    {
        // The composition root's shape on the fixture's container: the application
        // role's data source, the ambient unit of work, the module context enlisted on
        // it, and the two write stores the handler takes.
        var services = new ServiceCollection();
        // A caller-owned data source when a case needs to control pooling; otherwise one
        // per provider, disposed with it.
        services.AddSingleton(
            dataSource ?? NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor, MutableAccessor>();
        services.AddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddModuleDbContext<TenancyDbContext>();
        services.AddScoped<ITenantWriteStore, TenantWriteStore>();
        services.AddScoped<IOrganizationWriteStore, OrganizationWriteStore>();
        services.AddSingleton<IClock>(Clock);
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(
            typeof(ITenantWriteStore).Assembly));

        return services.BuildServiceProvider();
    }

    private static ProvisionTenantCommand Command()
    {
        // Version-7 ids, distinct per case, because the container is shared and a fixed
        // id would make two cases collide on a unique index rather than on the property
        // under test.
        var suffix = Guid.CreateVersion7().ToString("N")[..8];
        return new ProvisionTenantCommand(
            TenantId.From(Guid.CreateVersion7()),
            $"prov-{suffix}",
            "Provisioned",
            OrganizationId.From(Guid.CreateVersion7()),
            $"prov-org-{suffix}",
            "Head Office");
    }

    private static ProvisionedTenantDto Provisioned(ProvisionTenantCommand command) =>
        new(command.TenantId.Value, command.Slug,
            command.DefaultOrganizationId.Value, command.DefaultOrganizationSlug);

    /// <summary>The innermost exception, since EF wraps a failing SaveChanges.</summary>
    private static Exception Unwrap(Exception exception)
    {
        while (exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    private async Task CleanUpAsync(ProvisionTenantCommand command)
    {
        // As learnstack_platform: the rows belong to a tenant with no context to
        // announce, and the organization has to go first — its foreign key names the
        // tenant, and tenants.default_organization_id names it back.
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);

        foreach (var statement in new[]
        {
            "UPDATE tenants SET default_organization_id = NULL WHERE id = @tenant",
            "DELETE FROM organizations WHERE tenant_id = @tenant",
            "DELETE FROM tenants WHERE id = @tenant",
        })
        {
            await using var cleanup = new NpgsqlCommand(statement, (NpgsqlConnection)platform);
            cleanup.Parameters.AddWithValue("tenant", command.TenantId.Value);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> ScalarAsPlatformAsync(string sql, Guid? id = null)
    {
        await using var platform = await PostgresFixture.OpenAsync(
            _schema.Postgres.PlatformConnectionString);
        await using var query = new NpgsqlCommand(sql, (NpgsqlConnection)platform);

        if (id is not null)
        {
            query.Parameters.AddWithValue("id", id.Value);
        }

        return (long)(await query.ExecuteScalarAsync())!;
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

    private static async Task<string?> ReadAsync(IUnitOfWork unitOfWork, string sql)
    {
        await using var command = (NpgsqlCommand)unitOfWork.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = (NpgsqlTransaction?)unitOfWork.Transaction;
        return (await command.ExecuteScalarAsync()) as string;
    }

    /// <summary>A request that provisions nothing, for the fail-closed case.</summary>
    public sealed record Probe : IRequest<Result<string>>;

    private sealed class MutableAccessor : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; }
    }

    private sealed class ResolvedContext(Guid tenant, Guid organization) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => SharedKernel.Identifiers.TenantId.From(tenant);

        public OrganizationId? OrganizationId =>
            SharedKernel.Identifiers.OrganizationId.From(organization);

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "tenancy";
    }
}
