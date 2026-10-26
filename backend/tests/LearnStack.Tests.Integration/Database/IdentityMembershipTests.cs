using LearnStack.Modules.Identity.Domain;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class IdentityMembershipTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    [Fact]
    public async Task Populated_graphs_are_isolated_by_actual_app_RLS_and_parameterized_EF_scope()
    {
        await using var app = await OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.memberships")).Should().Be(0);
        var scope = new StaticTenantContextAccessor(new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null));
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(app).Options,
            scope, EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(transaction);
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        (await db.Memberships.CountAsync()).Should().Be(2);
        (await db.MembershipProfiles.CountAsync()).Should().Be(2);
        (await db.MembershipRoleAssignments.CountAsync()).Should().Be(2);
        (await db.Roles.CountAsync()).Should().Be(8);
        (await db.RolePermissionGrants.CountAsync()).Should().Be(7);
        await ScopeAsync(app, transaction, SchemaFixture.TenantB);
        (await db.Memberships.CountAsync()).Should().Be(0, "the independent EF filter still binds tenant A");
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.memberships")).Should().Be(1);
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.memberships")).Should().Be(3);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.memberships WHERE organization_id=@other", ("other", SchemaFixture.OrgA2))).Should().Be(0);
        await ExecuteAsync(app, transaction, "SELECT set_config('app.scope','tenant',true)");
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.memberships")).Should().Be(4);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        (await ScalarAsync<long>(app, transaction, "WITH changed AS (UPDATE public.membership_profiles SET display_name='forbidden' WHERE membership_id=@id RETURNING 1) SELECT count(*) FROM changed", ("id", BranchMembershipA2))).Should().Be(0);
        (await ScalarAsync<long>(app, transaction, "WITH changed AS (UPDATE public.membership_profiles SET display_name='forbidden' WHERE membership_id=@id RETURNING 1) SELECT count(*) FROM changed", ("id", AdminMembershipA))).Should().Be(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("shared")]
    [InlineData("wrong")]
    [InlineData("two-int")]
    [InlineData("foreign")]
    public async Task Actual_admission_requires_own_backend_exclusive_single_bigint_User_fence(string mode)
    {
        await using var app = await OpenAsync();
        await using var other = await OpenAsync();
        await using var otherTx = await other.BeginTransactionAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        var key = IdentityAdvisoryLockKey.ForUser(UserId.From(PersonB));
        if (mode == "shared") await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock_shared(@key)", ("key", key));
        if (mode == "wrong") await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", key ^ 1));
        if (mode == "two-int") await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock(@first,@second)", ("first", (int)(key >> 32)), ("second", unchecked((int)key)));
        if (mode == "foreign") await ExecuteAsync(other, otherTx, "SELECT pg_advisory_xact_lock(@key)", ("key", key));
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        await ExecuteAsync(app, transaction, "SAVEPOINT before_admission");
        var insert = () => InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, Guid.CreateVersion7(), PersonB,
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
        await ExecuteAsync(app, transaction, "ROLLBACK TO SAVEPOINT before_admission");
        (await ScalarAsync<bool>(app, transaction, "SELECT public.fn_identity_lock_held('user',@id,true)", ("id", PersonB))).Should().BeFalse();
    }

    [Theory]
    [InlineData("019a1929-5450-7bbb-8df1-516e8e002501", 6536232317004839961L, 8764202873088396187L)]
    [InlineData("01930000-0000-7000-8000-00000000a001", 7247098387317121321L, -9009402356915246337L)]
    [InlineData("ffffffff-ffff-7fff-bfff-ffffffffffff", -2062124468225165546L, 7755294501392127735L)]
    public async Task SQL_and_CSharp_User_and_Tenant_keys_agree_in_both_signed_ranges(string text, long user, long tenant)
    {
        var id = Guid.Parse(text);
        await using var app = await OpenAsync();
        IdentityAdvisoryLockKey.ForUser(UserId.From(id)).Should().Be(user);
        IdentityAdvisoryLockKey.ForTenant(TenantId.From(id)).Should().Be(tenant);
        (await ScalarAsync<long>(app, null, "SELECT public.fn_identity_contextual_lock_key('user',@id)", ("id", id))).Should().Be(user);
        (await ScalarAsync<long>(app, null, "SELECT public.fn_identity_contextual_lock_key('tenant',@id)", ("id", id))).Should().Be(tenant);
    }

    [Fact]
    public async Task Session_fence_demonstrates_observation_limit_and_transaction_fence_ends_on_rollback()
    {
        await using var app = await OpenAsync();
        var key = IdentityAdvisoryLockKey.ForUser(UserId.From(PersonB));
        await ExecuteAsync(app, null, "SELECT pg_advisory_lock(@key)", ("key", key));
        try
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
            await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, Guid.CreateVersion7(), PersonB,
                RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.RollbackAsync();
            (await ScalarAsync<bool>(app, null, "SELECT public.fn_identity_lock_held('user',@id,true)", ("id", PersonB))).Should().BeTrue();
        }
        finally { await ExecuteAsync(app, null, "SELECT pg_advisory_unlock(@key)", ("key", key)); }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", key));
            await transaction.RollbackAsync();
        }
        (await ScalarAsync<bool>(app, null, "SELECT public.fn_identity_lock_held('user',@id,true)", ("id", PersonB))).Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_target_refuses_before_FK_with_same_fixed_target_diagnostic()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        var unknown = Guid.CreateVersion7();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, unknown, false);
        var insert = () => InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, Guid.CreateVersion7(), unknown,
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        var failure = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        failure.SqlState.Should().Be("P0302");
        failure.MessageText.Should().Be("identity membership target is unavailable");
        failure.Detail.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_second_admin_graph_requires_preheld_exclusive_tenant_mode(bool exclusive)
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, PersonB, exclusive);
        var insert = () => InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, Guid.CreateVersion7(), PersonB,
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
        if (!exclusive) (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
        else
        {
            await insert();
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.memberships")).Should().Be(3);
        }
    }

    [Fact]
    public async Task Every_assignment_addition_including_empty_role_requires_User_but_profile_and_removal_do_not()
    {
        await using var app = await OpenAsync();
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
            var add = () => ExecuteAsync(app, transaction, "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id) VALUES(@tenant,@member,@role)",
                ("tenant", SchemaFixture.TenantA), ("member", LearnerMembershipA), ("role", RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant)));
            (await add.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
            await ExecuteAsync(app, transaction, "UPDATE public.membership_profiles SET display_name='changed' WHERE membership_id=@member; DELETE FROM public.membership_role_assignments WHERE membership_id=@member", ("member", LearnerMembershipA));
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            (await ScalarAsync<bool>(app, transaction, "SELECT public.fn_identity_lock_held('user',@id,true)", ("id", PersonA))).Should().BeFalse();
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
            await ExecuteAsync(app, transaction, "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id) VALUES(@tenant,@member,@role)",
                ("tenant", SchemaFixture.TenantA), ("member", LearnerMembershipA), ("role", RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant)));
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        }
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("revoke")]
    [InlineData("assignment")]
    [InlineData("kernel")]
    public async Task Final_administrator_changes_are_refused_by_complete_graph_preflight(string change)
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, true);
        var sql = change switch
        {
            "suspend" => "UPDATE public.memberships SET status='Suspended' WHERE id=@member",
            "revoke" => "UPDATE public.memberships SET status='Revoked',deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@member",
            "assignment" => "DELETE FROM public.membership_role_assignments WHERE membership_id=@member",
            _ => "DELETE FROM public.role_permission_grants WHERE role_id=@role AND permission_key=@key",
        };
        await ExecuteAsync(app, transaction, sql, ("member", AdminMembershipA), ("actor", UserId.SystemActor.Value),
            ("role", RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)), ("key", IdentityFoundationManifest.RoleAdmin));
        var validate = () => ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        (await validate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0303");
    }

    [Fact]
    public async Task Revocation_is_terminal_and_rejoin_uses_a_new_identity_with_NULL_scope_uniqueness()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
        await ExecuteAsync(app, transaction, "UPDATE public.memberships SET status='Revoked',deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@id", ("actor", UserId.SystemActor.Value), ("id", LearnerMembershipA));
        await ExecuteAsync(app, transaction, "SAVEPOINT retired");
        var revive = () => ExecuteAsync(app, transaction, "UPDATE public.memberships SET status='Active',deleted_at=NULL,deleted_by=NULL WHERE id=@id", ("id", LearnerMembershipA));
        (await revive.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0304");
        await ExecuteAsync(app, transaction, "ROLLBACK TO SAVEPOINT retired");
        await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, Guid.CreateVersion7(), PersonA,
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await ExecuteAsync(app, transaction, "SAVEPOINT joined");
        var duplicate = () => InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, Guid.CreateVersion7(), PersonA,
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        await ExecuteAsync(app, transaction, "ROLLBACK TO SAVEPOINT joined");
    }

    [Fact]
    public async Task NULL_parent_mirror_and_role_capability_are_checked_independently_of_RLS()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
        await ExecuteAsync(app, transaction, "SAVEPOINT clean");
        var mirror = () => ExecuteAsync(app, transaction, "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,organization_id,role_id) VALUES(@tenant,@member,@organization,@role)",
            ("tenant", SchemaFixture.TenantA), ("member", LearnerMembershipA), ("organization", SchemaFixture.OrgA1), ("role", RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Organization)));
        (await mirror.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0304");
        await ExecuteAsync(app, transaction, "ROLLBACK TO SAVEPOINT clean");
        var capability = () => ExecuteAsync(app, transaction, "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,organization_id,role_id) VALUES(@tenant,@member,@organization,@role)",
            ("tenant", SchemaFixture.TenantA), ("member", BranchMembershipA1), ("organization", SchemaFixture.OrgA1), ("role", RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant)));
        (await capability.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0304");
    }

    [Fact]
    public async Task Initial_Role_exception_admits_only_exact_fresh_graph_and_refuses_committed_repairs()
    {
        await using var app = await OpenAsync();
        var tenant = Guid.CreateVersion7();
        var role = Guid.CreateVersion7();
        await using (var bad = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, bad, tenant);
            await LockAsync(app, bad, tenant, null, false);
            await ExecuteAsync(app, bad,
                "INSERT INTO public.roles(id,tenant_id,built_in_key,permission_scope,created_at,created_by) VALUES(@role,@tenant,'Instructor','Tenant',clock_timestamp(),@actor); INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key) VALUES(@tenant,@role,'Tenant',@key)",
                ("role", role), ("tenant", tenant), ("actor", UserId.SystemActor.Value), ("key", IdentityFoundationManifest.ProfileWrite));
            var validate = () => ExecuteAsync(app, bad, "SET CONSTRAINTS ALL IMMEDIATE");
            (await validate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0304");
            await bad.RollbackAsync();
        }
        await using (var initial = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, initial, tenant);
            await LockAsync(app, initial, tenant, null, false);
            await ExecuteAsync(app, initial,
                "INSERT INTO public.roles(id,tenant_id,built_in_key,permission_scope,created_at,created_by) VALUES(@role,@tenant,'Instructor','Tenant',clock_timestamp(),@actor)",
                ("role", role), ("tenant", tenant), ("actor", UserId.SystemActor.Value));
            await ExecuteAsync(app, initial, "SET CONSTRAINTS ALL IMMEDIATE");
            await initial.CommitAsync();
        }
        await using var repair = await app.BeginTransactionAsync();
        await ScopeAsync(app, repair, tenant);
        await LockAsync(app, repair, tenant, null, false);
        (await ScalarAsync<long>(app, repair, "SELECT count(*) FROM public.memberships")).Should().Be(0);
        var change = () => ExecuteAsync(app, repair,
            "INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key) VALUES(@tenant,@role,'Tenant',@key)",
            ("role", role), ("tenant", tenant), ("key", IdentityFoundationManifest.ProfileWrite));
        (await change.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0304");
    }

    [Fact]
    public async Task Reactivation_requires_User_fence_and_revoked_history_remains_in_the_EF_model()
    {
        await using var app = await OpenAsync();
        await using (var withoutUser = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, withoutUser, SchemaFixture.TenantA);
            await LockAsync(app, withoutUser, SchemaFixture.TenantA, null, false);
            await ExecuteAsync(app, withoutUser, "UPDATE public.memberships SET status='Suspended' WHERE id=@id", ("id", LearnerMembershipA));
            var activate = () => ExecuteAsync(app, withoutUser, "UPDATE public.memberships SET status='Active' WHERE id=@id", ("id", LearnerMembershipA));
            (await activate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
            await withoutUser.RollbackAsync();
        }
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
        await ExecuteAsync(app, transaction, "UPDATE public.memberships SET status='Suspended' WHERE id=@id; UPDATE public.memberships SET status='Active' WHERE id=@id", ("id", LearnerMembershipA));
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await ExecuteAsync(app, transaction, "UPDATE public.memberships SET status='Revoked',deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@id", ("id", LearnerMembershipA), ("actor", UserId.SystemActor.Value));
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(app).Options,
            new StaticTenantContextAccessor(new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null)), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(transaction);
        (await db.Memberships.AnyAsync(member => member.Id == MembershipId.From(LearnerMembershipA)
            && member.Status == MembershipStatus.Revoked)).Should().BeTrue("history reads retain the terminal row under the unchanged tenant/org filter");
    }

    private async Task<NpgsqlConnection> OpenAsync() => (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
}
