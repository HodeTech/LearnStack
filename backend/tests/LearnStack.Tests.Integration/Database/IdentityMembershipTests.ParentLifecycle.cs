using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assignment_addition_serializes_with_Membership_Revoke_and_the_predecessor_exposes_the_race(bool corrected)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var migration = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(database.MigrationConnectionString, options =>
                options.MigrationsHistoryTable(IdentityDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
        var currentFunctions = await ReadIdentityFunctionsAsync(database);
        await migration.GetService<IMigrator>().MigrateAsync("20261026043921_correct_identity_admission_parent_sqlstate");
        var predecessorFunctions = await ReadIdentityFunctionsAsync(database);
        var currentGuard = currentFunctions.Except(predecessorFunctions).Should().ContainSingle().Which;
        var previousGuard = predecessorFunctions.Except(currentFunctions).Should().ContainSingle().Which;
        currentGuard.Identity.Should().Be("fn_identity_contextual_mutation_guard()");
        (currentGuard with { Definition = previousGuard.Definition }).Should().Be(previousGuard,
            "the forward replacement preserves function identity, owner, ACL, volatility and execution mode");
        currentGuard.Definition.Should().Be(previousGuard.Definition.Replace(
            "target_member FOR KEY SHARE;", "target_member FOR NO KEY UPDATE;", StringComparison.Ordinal));
        if (corrected)
        {
            await migration.Database.MigrateAsync();
            (await ReadIdentityFunctionsAsync(database)).Should().Equal(currentFunctions);
        }
        migration.Database.HasPendingModelChanges().Should().BeFalse();

        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var revoker = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var retirement = await revoker.BeginTransactionAsync();
        var tenant = TenantId.From(SchemaFixture.TenantA);
        await revoker.SetProvisioningTenantContextAsync(tenant);
        await new IdentityMutationLocks(revoker).AcquireAsync(new([], tenant, IdentityTenantLockMode.Shared));
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(revoker.Connection).Options,
            new StaticTenantContextAccessor(new SeedTenantContext(tenant, null)), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(revoker.Transaction);
        var store = new MembershipWriteStore(db, new ContextualIdentityStorageControl(db, revoker));
        var membership = (await store.FindAsync(MembershipId.From(LearnerMembershipA)))!;
        membership.Revoke(DateTimeOffset.UtcNow, UserId.SystemActor).IsSuccess.Should().BeTrue();
        await store.UpdateAsync(membership);
        await store.ValidateGraphAsync();
        (await ScalarAsync<bool>((NpgsqlConnection)revoker.Connection, (NpgsqlTransaction)revoker.Transaction!,
            "SELECT public.fn_identity_lock_held('user',@user,true)", ("user", PersonA))).Should().BeFalse(
                "nonadministrator revocation keeps its accepted shared tenant plan without a new User fence");

        await using var inserter = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(inserter);
        await using var addition = await inserter.BeginTransactionAsync();
        await ScopeAsync(inserter, addition, tenant.Value);
        await LockAsync(inserter, addition, tenant.Value, PersonA, false);
        var instructor = RoleId(tenant.Value, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        var pending = ExecuteAsync(inserter, addition, """
            INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id)
            VALUES(@tenant,@member,@role)
            """, ("tenant", tenant.Value), ("member", LearnerMembershipA), ("role", instructor));
        if (corrected)
        {
            await WaitForBlockingBackendAsync(observer, inserter.ProcessID, ((NpgsqlConnection)revoker.Connection).ProcessID);
            pending.IsCompleted.Should().BeFalse();
            await retirement.CompleteAsync();
            (await FluentActions.Awaiting(() => pending).Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0304",
                "the locking read must see the committed Revoked lifecycle before admitting the child");
            await addition.RollbackAsync();
        }
        else
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await ExecuteAsync(inserter, addition, "SET CONSTRAINTS ALL IMMEDIATE");
            await addition.CommitAsync();
            await retirement.CompleteAsync();
        }
        await using var read = await inserter.BeginTransactionAsync();
        await ScopeAsync(inserter, read, tenant.Value);
        (await ScalarAsync<string>(inserter, read, "SELECT status FROM public.memberships WHERE id=@member",
            ("member", LearnerMembershipA))).Should().Be("Revoked");
        (await ScalarAsync<long>(inserter, read, "SELECT count(*) FROM public.membership_role_assignments WHERE membership_id=@member AND role_id=@role",
            ("member", LearnerMembershipA), ("role", instructor))).Should().Be(corrected ? 0 : 1);
    }

    private static async Task WaitForBlockingBackendAsync(NpgsqlConnection observer, int waiting, int blocker)
    {
        var budget = Stopwatch.StartNew();
        while (budget.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await ScalarAsync<bool>(observer, null, "SELECT @blocker=ANY(pg_catalog.pg_blocking_pids(@waiting))",
                ("blocker", blocker), ("waiting", waiting))) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The fixture did not observe the required competing backend wait.");
    }
}
