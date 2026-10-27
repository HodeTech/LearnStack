using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("constraint")]
    [InlineData("cancel")]
    public async Task Contextual_whole_act_save_restores_EF_default_and_every_failure_poisons_the_owner(string outcome)
    {
        await using var source = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        var tenant = TenantId.From(outcome == "constraint" ? SchemaFixture.TenantA : Guid.CreateVersion7());
        var scope = new StaticTenantContextAccessor(new SeedTenantContext(tenant, null));
        await unit.SetProvisioningTenantContextAsync(tenant);
        var locks = new IdentityMutationLocks(unit);
        await locks.AcquireAsync(new([], tenant, IdentityTenantLockMode.Shared));
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(unit.Connection).Options,
            scope, EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(unit.Transaction);
        await using var ordinary = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(unit.Connection).Options, scope);
        db.Database.AutoSavepointsEnabled.Should().BeTrue();
        ordinary.Database.AutoSavepointsEnabled.Should().BeTrue();
        var control = new ContextualIdentityStorageControl(db, unit);
        var store = new RoleWriteStore(db, control);
        var id = LearnStack.Modules.Identity.Domain.RoleId.From(Guid.CreateVersion7());
        var role = Role.Create(id, tenant, BuiltInRoleKey.Instructor, PermissionScope.Tenant,
            DateTimeOffset.UtcNow, UserId.SystemActor).Value!;
        using var cancellation = new CancellationTokenSource();
        if (outcome == "cancel") cancellation.Cancel();
        var save = () => store.AddAsync(role, cancellation.Token);
        if (outcome == "success")
        {
            await save();
            await store.ValidateGraphAsync();
            unit.IsRollbackOnly.Should().BeFalse();
            (await ScalarAsync<bool>((NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!,
                "SELECT xmin=pg_current_xact_id()::text::xid FROM public.roles WHERE id=@id", ("id", id.Value))).Should().BeTrue();
        }
        else if (outcome == "constraint")
        {
            (await save.Should().ThrowAsync<IdentityFoundationStorageException>()).Which.Failure
                .Should().Be(IdentityFoundationStorageFailure.Conflict);
            unit.IsRollbackOnly.Should().BeTrue();
        }
        else
        {
            await save.Should().ThrowAsync<OperationCanceledException>();
            unit.IsRollbackOnly.Should().BeTrue();
        }
        db.Database.AutoSavepointsEnabled.Should().BeTrue("the global Identity adapter receives the unchanged default after every contextual exit");
        ordinary.Database.AutoSavepointsEnabled.Should().BeTrue("the contextual setting never alters a sibling module context");
        await frame.FailAsync();
        await using var read = await OpenAsync();
        await using var readTx = await read.BeginTransactionAsync();
        await ScopeAsync(read, readTx, tenant.Value);
        (await ScalarAsync<long>(read, readTx, "SELECT count(*) FROM public.roles WHERE id=@id", ("id", id.Value))).Should().Be(0);
    }
}
