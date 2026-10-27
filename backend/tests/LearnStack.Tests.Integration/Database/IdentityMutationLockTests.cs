using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class IdentityMutationLockTests(SchemaFixture schema)
{
    private readonly SchemaFixture _schema = schema;
    private static readonly UserId User = UserId.From(Guid.Parse("01930000-0000-7000-8000-00000000a001"));
    private static readonly TenantId Tenant = TenantId.From(SchemaFixture.TenantA);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_plan_holds_actual_exclusive_user_then_required_tenant_mode_and_releases_at_transaction_end(bool rollback)
    {
        await using var source = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(Tenant);
        var locks = new IdentityMutationLocks(unit);
        var plan = new IdentityMutationLockPlan([User], Tenant, IdentityTenantLockMode.Shared);
        await locks.AcquireAsync(plan);
        locks.RequireCurrent(plan);
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForUser(User))).Should().Equal("ExclusiveLock");
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().Equal("ShareLock");
        await SqlAsync(unit, "SAVEPOINT after_admission");
        await SqlAsync(unit, "ROLLBACK TO SAVEPOINT after_admission");
        locks.RequireCurrent(plan);
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForUser(User))).Should().Equal("ExclusiveLock");
        if (rollback) await frame.FailAsync(); else await frame.CompleteAsync();
        // These queries use the same physical backend outside its completed transaction.
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().BeEmpty();
        var stale = () => locks.RequireCurrent(plan);
        stale.Should().Throw<IdentityMutationLockException>();
    }

    [Fact]
    public async Task Profile_or_removal_plan_has_no_user_hold_and_cannot_upgrade_or_reuse_a_generation()
    {
        await using var source = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        var first = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(Tenant);
        var locks = new IdentityMutationLocks(unit);
        var plan = new IdentityMutationLockPlan([], Tenant, IdentityTenantLockMode.Shared);
        await locks.AcquireAsync(plan);
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().Equal("ShareLock");
        await first.CompleteAsync();
        await using var second = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(Tenant);
        var stale = () => locks.RequireCurrent(plan);
        stale.Should().Throw<IdentityMutationLockException>();
        unit.IsRollbackOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData("unannounced")]
    [InlineData("foreign")]
    [InlineData("repeatable")]
    [InlineData("read-only")]
    public async Task Missing_or_incompatible_owner_refuses_before_any_advisory_hold(string defect)
    {
        await using var source = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        await using var frame = await unit.BeginTransactionAsync(defect == "read-only"
            ? TransactionMode.ReadOnly : TransactionMode.ReadWrite);
        if (defect == "repeatable") await SqlAsync(unit, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");
        if (defect != "unannounced")
            await unit.SetProvisioningTenantContextAsync(defect == "foreign" ? TenantId.From(SchemaFixture.TenantB) : Tenant);
        var locks = new IdentityMutationLocks(unit);
        await FluentActions.Awaiting(() => locks.AcquireAsync(new([User], Tenant, IdentityTenantLockMode.Exclusive)))
            .Should().ThrowAsync<IdentityMutationLockException>();
        unit.IsRollbackOnly.Should().BeTrue();
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_acquired_shared_plan_never_upgrades_or_adds_a_late_user(bool addUser)
    {
        await using var source = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(Tenant);
        var locks = new IdentityMutationLocks(unit);
        await locks.AcquireAsync(new([], Tenant, IdentityTenantLockMode.Shared));
        await FluentActions.Awaiting(() => locks.AcquireAsync(new(addUser ? [User] : [], Tenant,
            addUser ? IdentityTenantLockMode.Shared : IdentityTenantLockMode.Exclusive)))
            .Should().ThrowAsync<IdentityMutationLockException>();
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().Equal("ShareLock");
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
    }

    private static NpgsqlUnitOfWork Unit(NpgsqlDataSource source) => new(source, NullLogger<NpgsqlUnitOfWork>.Instance);

    private static async Task SqlAsync(NpgsqlUnitOfWork unit, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection,
            (NpgsqlTransaction?)unit.Transaction)
        { CommandTimeout = 10 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> HeldAsync(NpgsqlUnitOfWork unit, long key)
    {
        await using var command = new NpgsqlCommand("""
            SELECT mode FROM pg_catalog.pg_locks
            WHERE pid = pg_catalog.pg_backend_pid()
              AND database = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())
              AND locktype = 'advisory' AND granted AND objsubid = 1
              AND (classid::bigint << 32 | objid::bigint) = @key
            ORDER BY mode
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("key", key);
        var modes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) modes.Add(reader.GetString(0));
        return [.. modes];
    }
}
