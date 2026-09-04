using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Api.Common;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
/// <see href="../../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>
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
        // schema would ever repair it.
        //
        // The failure is provoked by colliding the organization's id with a seeded row —
        // the unique index fires regardless of RLS, so the second write is refused while
        // the first has already succeeded. That is the state ADR-0042 exists to make
        // unobservable.
        //
        // It arrives as a Result rather than a throw, and that is the path worth testing:
        // TransactionBehavior calls FailAsync on a failure response, so the rollback here
        // is the one a business refusal takes, not the one an exception takes. A handler
        // that had written the tenant and then returned Result.Fail without the behavior
        // rolling back would leave exactly the orphan this ADR forbids.
        var command = Command() with
        {
            DefaultOrganizationId = OrganizationId.From(SchemaFixture.OrgA1),
        };

        try
        {
            var result = await ProvisionAsync(command);

            result.IsFailure.Should().BeTrue();
            result.Error!.Details.Should().ContainKey(
                nameof(ProvisionTenantCommand.DefaultOrganizationId),
                "the collision is on the organization's key, which proves the tenant "
                + "insert had already gone through when the second write was refused");
            HttpStatusMap.For(result.Error.Code).Should().Be(StatusCodes.Status409Conflict);

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

    [Theory]
    [InlineData("slug", "Slug", "lockey_slug_taken")]
    [InlineData("id", "TenantId", "lockey_identifier_taken")]
    public async Task A_name_already_taken_is_an_answer_rather_than_a_crash(
        string collideOn, string expectedField, string expectedReason)
    {
        // Reusing a slug is an ordinary thing a caller does, and untranslated it is a 500:
        // neither DbUpdateException nor PostgresException has an arm in HttpStatusMap, so
        // every one falls to InternalServerError — raised after ValidationBehavior passed
        // the command, after the transaction opened and after the tenant was announced.
        //
        // It cannot be pre-checked, either. Under the provisioning announcement a SELECT
        // over `tenants` returns zero rows by policy, so the database's answer is the only
        // one there is; the adapter translates it and the handler turns it into a Result.
        var first = Command();

        try
        {
            (await ProvisionAsync(first)).IsSuccess.Should().BeTrue();

            var second = collideOn == "slug"
                ? Command() with { Slug = first.Slug }
                : Command() with { TenantId = first.TenantId };

            var result = await ProvisionAsync(second);

            result.IsFailure.Should().BeTrue("a taken name is a refusal, not a fault");

            // The top-level code is what decides the HTTP status, and this is the leg
            // nothing checked before: four module-specific keys at the top level were
            // measured falling through HttpStatusMap's closed table to 500, which made a
            // "slug taken" answer worse than the generic one it replaced.
            HttpStatusMap.For(result.Error!.Code).Should().Be(
                StatusCodes.Status409Conflict,
                "a code absent from the map falls through to 500, and a module does not "
                + "grow the global table for its own vocabulary");

            // The specificity lives in the details, keyed by the field that collided —
            // a caller retrying blindly on the wrong half never succeeds.
            result.Error.Details.Should().ContainKey(expectedField)
                .WhoseValue.Should().ContainSingle()
                .Which.Key.Should().Be(expectedReason);

            (await ScalarAsPlatformAsync(
                "SELECT count(*) FROM tenants WHERE id = @id", second.TenantId.Value))
                .Should().Be(collideOn == "slug" ? 0L : 1L,
                    "the second provisioning rolled back; on the id collision the row that "
                    + "remains is the FIRST tenant's");

            await CleanUpAsync(second);
        }
        finally
        {
            await CleanUpAsync(first);
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

    [Fact]
    public async Task Updating_a_detached_aggregate_is_refused_rather_than_guessed_at()
    {
        // `DbSet.Update` traverses the graph and marks what it reaches: on a detached
        // aggregate every child with a set key becomes Modified and every child with an
        // unset key becomes Added. `Tenant` carries Locales and FeatureFlags, so the
        // obvious spelling of UpdateAsync re-UPDATEs every one of them from a stale
        // in-memory copy — overwriting anything written since the load.
        //
        // Marking only the detached root is no better, and this is the measurement that
        // settled it: `Version` is the concurrency token, EF takes a detached entity's
        // original values from its current ones, so a caller that mutated the root first
        // issues WHERE row_version = <the value it just incremented to>, matches nothing,
        // and gets DbUpdateConcurrencyException. There is no correct silent handling, so
        // the store refuses — loudly, at the call, naming the fix.
        //
        // Provisioning never hits this: its aggregate is tracked from the Add. The port is
        // the one six modules inherit, and this is the contract they inherit with it.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<ITenantContextAccessor>().Current =
            new ResolvedContext(SchemaFixture.TenantA, SchemaFixture.OrgA1);

        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(services.GetRequiredService<ITenantContext>());

        var db = services.GetRequiredService<TenancyDbContext>();
        var tenant = await db.Tenants
            .Include(candidate => candidate.Locales)
            .SingleAsync(candidate => candidate.Id == TenantId.From(SchemaFixture.TenantA));

        tenant.Locales.Should().NotBeEmpty(
            "a tenant with no children could not show the graph walk at all");

        // The graph goes detached — a request that loaded in one scope and saved in
        // another, or anything that round-tripped the aggregate.
        db.ChangeTracker.Clear();
        tenant.ChangeStatus(TenantStatus.Suspended, Clock, UserId.SystemActor);

        var save = () => services.GetRequiredService<ITenantWriteStore>().UpdateAsync(tenant);

        (await save.Should().ThrowAsync<InvalidOperationException>(
            "a detached aggregate is a programmer error with no safe default"))
            .WithMessage("*not tracked by this scope's context*");

        // And nothing was written on the way to the refusal.
        (await ReadAsync(
            unitOfWork,
            $"SELECT status FROM tenants WHERE id = '{SchemaFixture.TenantA}'"))
            .Should().Be(nameof(TenantStatus.Trial));

        await unitOfWork.RollbackAsync();
    }

    [Fact]
    public async Task A_refused_write_does_not_ride_along_on_the_next_save()
    {
        // EF keeps a failed entry in the state it had, so an Added row the database
        // refused stays Added. A caller that turns the conflict into Result.Fail and
        // carries on writing therefore has the rejected INSERT still queued, and the next
        // SaveChanges on the same context re-sends it — the row is gone from the database,
        // and the tracker is a claim that outlived its subject.
        //
        // Reachable through nesting, which ADR-0040 permits: an outer handler may absorb
        // an inner failure and keep going on the same scope, and the scope is one
        // DbContext. Driven directly here, because what is under test is the store's
        // cleanup and not the pipeline that would carry it.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<ITenantContextAccessor>().Current =
            new ResolvedContext(SchemaFixture.TenantA, SchemaFixture.OrgA1);

        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(services.GetRequiredService<ITenantContext>());

        var organizations = services.GetRequiredService<IOrganizationWriteStore>();

        // Refused: OrgA1 already exists under this tenant.
        var refused = Organization.Create(
            OrganizationId.From(SchemaFixture.OrgA1),
            TenantId.From(SchemaFixture.TenantA),
            "collides",
            "Collides",
            Clock,
            UserId.SystemActor);

        var conflict = async () => await organizations.AddAsync(refused);
        await conflict.Should().ThrowAsync<AggregateConflictException>();

        // The caller absorbs it and writes something else on the same context.
        var accepted = Organization.Create(
            OrganizationId.From(Guid.CreateVersion7()),
            TenantId.From(SchemaFixture.TenantA),
            $"after-{Guid.CreateVersion7():N}"[..20],
            "After",
            Clock,
            UserId.SystemActor);

        var second = async () => await organizations.AddAsync(accepted);

        await second.Should().NotThrowAsync(
            "the refused row was detached, so the second save carries only the new one");

        await unitOfWork.RollbackAsync();
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
        new(command.TenantId, command.Slug,
            command.DefaultOrganizationId, command.DefaultOrganizationSlug);

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
