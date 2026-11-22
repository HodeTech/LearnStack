using LearnStack.Infrastructure.DataProtection;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
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
/// The pieces an ADR-0107 export proof needs outside a request pipeline: an announced application-role unit of
/// work over one group, the Identity context on it, and the fixture's export key ring.
/// </summary>
internal static class DataExportTestKit
{
    internal static string KeyRing => InvitationEmailFixtureConfiguration.Values[DataExportCipher.KeyRingSecret]!;

    internal static DataExportCipher Cipher() => new(new FixedSecrets(KeyRing));

    /// <summary>A cipher over another key ring, such as one a key has left.</summary>
    internal static DataExportCipher Cipher(string ring) => new(new FixedSecrets(ring));

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

    private sealed class FixedSecrets(string ring) : ISecretProvider
    {
        public string? GetSecret(string key) => key == DataExportCipher.KeyRingSecret ? ring : null;
    }
}
