using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Fixtures;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The pieces an ADR-0106/0107 data-request proof needs outside a request pipeline: an announced application-role unit of
/// work over one group, the Identity context on it, the fixture's export key ring, and the connections, disposable
/// databases and job claims the proofs share.
/// </summary>
internal static class DataExportTestKit
{
    internal static string KeyRing => InvitationEmailFixtureConfiguration.Values[DataExportCipher.KeyRingSecret]!;

    internal static DataExportCipher Cipher() => new(new FixedSecrets(KeyRing));

    /// <summary>A cipher over another key ring, such as one a key has left.</summary>
    internal static DataExportCipher Cipher(string ring) => new(new FixedSecrets(ring));

    /// <summary>A connection as <c>learnstack_app</c>: a proof that runs as any other role proves nothing about the policies.</summary>
    internal static async Task<NpgsqlConnection> OpenAppAsync(string appConnectionString)
    {
        var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(appConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        return app;
    }

    /// <summary>A disposable copy of the seeded schema with both seeded tenants and the Identity fixture rows.</summary>
    internal static async Task<DisposableSchemaDatabase> CreateSeededDatabaseAsync(PostgresFixture postgres)
    {
        var database = await DisposableSchemaDatabase.CreateAsync(postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        return database;
    }

    /// <summary>
    /// Runs <paramref name="act"/> behind a savepoint, expects the database to refuse it, and rolls the savepoint back so
    /// the transaction can carry on.
    /// </summary>
    internal static async Task<PostgresException> RefuseAsync(NpgsqlTransaction transaction, Func<Task> act)
    {
        await transaction.SaveAsync("probe");
        var refusal = (await act.Should().ThrowAsync<PostgresException>()).Which;
        await transaction.RollbackAsync("probe");
        return refusal;
    }

    /// <summary>An application-role transaction announced for one group, with the group's Identity context.</summary>
    internal static async Task<GroupScope> OpenAsync(string appConnectionString, Guid tenant, Guid? organization,
        IEnumerable<Guid>? userFences = null)
    {
        var source = ApplicationDataSource.Build(appConnectionString);
        var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        var frame = await unit.BeginTransactionAsync();
        var context = new SeedTenantContext(TenantId.From(tenant), organization is { } org ? OrganizationId.From(org) : null);
        await unit.SetTenantContextAsync(context);
        await new IdentityMutationLocks(unit).AcquireAsync(new((userFences ?? []).Select(UserId.From), context.TenantId,
            IdentityTenantLockMode.Shared));
        var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(unit.Connection).Options,
            new StaticTenantContextAccessor(context), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(unit.Transaction);
        return new GroupScope(source, unit, frame, context, db);
    }

    internal sealed class GroupScope(NpgsqlDataSource source, NpgsqlUnitOfWork unit, IUnitOfWorkScope frame, ITenantContext context,
        IdentityDbContext db) : IAsyncDisposable
    {
        public NpgsqlUnitOfWork Unit => unit;
        public IUnitOfWorkScope Frame => frame;
        public ITenantContext Context => context;
        public IdentityDbContext Db => db;
        public NpgsqlConnection Connection => (NpgsqlConnection)unit.Connection;
        public NpgsqlTransaction Transaction => (NpgsqlTransaction)unit.Transaction!;

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await frame.DisposeAsync();
            await unit.DisposeAsync();
            await source.DisposeAsync();
        }
    }

    /// <summary>The audit capture is the pipeline's; outside it a designation binds nothing.</summary>
    internal sealed class NoAuditSubject : IAuditSubject
    {
        public void Designate<TId>(IAggregateRoot<TId> aggregate) where TId : struct, IStronglyTypedId<Guid>
        {
        }
    }

    /// <summary>The routing claim the job runtime hands a worker, read back from the stored job by the dispatcher role.</summary>
    internal sealed class RoutingClaim : BackgroundJobRoutingClaim.MintingSource
    {
        internal static async Task<BackgroundJobRoutingClaim> ClaimAsync(string connectionString, BackgroundJobId job)
        {
            await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_token=@token,ownership_kind='routing',
                    ownership_until=clock_timestamp()+interval '4 minutes' WHERE id=@job
                RETURNING id,tenant_id,organization_id,definition_key,queue,wake_generation,ownership_token,
                    ownership_until,original_due_at,profile,phase,clock_timestamp()
                """, dispatcher);
            command.Parameters.AddWithValue("job", job.Value);
            command.Parameters.AddWithValue("token", Guid.NewGuid());
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            return MintClaim(row);
        }
    }

    private sealed class FixedSecrets(string ring) : ISecretProvider
    {
        public string? GetSecret(string key) => key == DataExportCipher.KeyRingSecret ? ring : null;
    }
}
