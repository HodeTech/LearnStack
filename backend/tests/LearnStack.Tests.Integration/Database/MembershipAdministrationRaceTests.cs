using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipAdministrationRaceTests(MembershipAdministrationFixture fixture)
    : IClassFixture<MembershipAdministrationFixture>
{
    [Fact]
    public async Task Current_Role_grants_are_reloaded_after_the_actual_referenced_Role_root_wait()
    {
        var member = await fixture.AddMemberAsync();
        var instructor = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        await using var harness = await fixture.CreateAsync("membership-role-postwait");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        await LockAsync(blocker, held, SchemaFixture.TenantA, null, false);
        Assert.Equal(instructor, await ScalarAsync<Guid>(blocker, held, "SELECT id FROM roles WHERE id=@role FOR NO KEY UPDATE", ("role", instructor)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = harness.Sender.Send(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [instructor]), deadline.Token);
        try
        {
            await fixture.ObserveWaitAsync(harness.ApplicationName, blocker.ProcessID, running, deadline.Token);
            await ExecuteAsync(blocker, held, """
                INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                VALUES(@tenant,@role,'Tenant','tenancy.locale.write');
                UPDATE roles SET row_version=row_version+1 WHERE id=@role;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("tenant", SchemaFixture.TenantA), ("role", instructor));
            await held.CommitAsync();
            Assert.Equal("forbidden", (await running).Error!.Code);
            Assert.Equal(0, (await fixture.ReadAsync(member.Membership)).Version);
        }
        finally
        {
            await StopAsync(held, running, deadline);
            await fixture.SetRoleGrantsAsync(BuiltInRoleKey.Instructor, PermissionScope.Tenant);
        }
    }

    [Fact]
    public async Task A_locale_disable_committed_during_the_Tenant_wait_refuses_the_profile_without_stamping()
    {
        var member = await fixture.AddMemberAsync();
        await using var harness = await fixture.CreateAsync("membership-locale-postwait");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        await LockAsync(blocker, held, SchemaFixture.TenantA, null, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = harness.Sender.Send(new SetMembershipProfileCommand(member.Membership, 0, "disabled-locale-canary", "tr"), deadline.Token);
        try
        {
            await fixture.ObserveWaitAsync(harness.ApplicationName, blocker.ProcessID, running, deadline.Token);
            await ExecuteAsync(blocker, held, """
                UPDATE tenant_locales SET is_enabled=false WHERE tenant_id=@tenant AND locale='tr';
                UPDATE tenants SET row_version=row_version+1 WHERE id=@tenant;
                """, ("tenant", SchemaFixture.TenantA));
            await held.CommitAsync();
            Assert.Equal("validation_failed", (await running).Error!.Code);
            Assert.Equal(0, (await fixture.ReadAsync(member.Membership)).Version);
        }
        finally
        {
            await StopAsync(held, running, deadline);
            await fixture.MutateAsync(SchemaFixture.TenantA, null, null, true, (app, transaction) =>
                ExecuteAsync(app, transaction, "UPDATE tenant_locales SET is_enabled=true WHERE tenant_id=@tenant AND locale='tr'", ("tenant", SchemaFixture.TenantA)));
        }
    }

    [Fact]
    public async Task Organization_lifecycle_committed_during_the_Tenant_wait_refuses_the_previously_admitted_branch_act()
    {
        // A2 is not the default; its terminal archive remains local to this class's owned database.
        var member = await fixture.AddMemberAsync(SchemaFixture.OrgA2);
        await using var harness = await fixture.CreateAsync("membership-lifecycle-postwait", organization: SchemaFixture.OrgA2);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        await LockAsync(blocker, held, SchemaFixture.TenantA, null, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = harness.Sender.Send(new SetMembershipProfileCommand(member.Membership, 0, "archived-branch-canary", null), deadline.Token);
        try
        {
            await fixture.ObserveWaitAsync(harness.ApplicationName, blocker.ProcessID, running, deadline.Token);
            await ExecuteAsync(blocker, held, "UPDATE organizations SET status='Archived',row_version=row_version+1 WHERE id=@id", ("id", SchemaFixture.OrgA2));
            await held.CommitAsync();
            Assert.Equal("not_found", (await running).Error!.Code);
            Assert.Equal(0, (await fixture.ReadAsync(member.Membership, SchemaFixture.OrgA2)).Version);
        }
        finally { await StopAsync(held, running, deadline); }
    }

    [Fact]
    public async Task Current_actor_profile_permission_is_rechecked_after_the_Tenant_wait()
    {
        var member = await fixture.AddMemberAsync();
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant);
        await using var harness = await fixture.CreateAsync("membership-source-postwait");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        await LockAsync(blocker, held, SchemaFixture.TenantA, null, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = harness.Sender.Send(new SetMembershipProfileCommand(member.Membership, 0, "revoked-source-canary", null), deadline.Token);
        try
        {
            await fixture.ObserveWaitAsync(harness.ApplicationName, blocker.ProcessID, running, deadline.Token);
            await ExecuteAsync(blocker, held, """
                DELETE FROM role_permission_grants WHERE role_id=@role AND permission_key=@key;
                UPDATE roles SET row_version=row_version+1 WHERE id=@role;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("role", role), ("key", IdentityFoundationManifest.ProfileWrite));
            await held.CommitAsync();
            Assert.Equal("forbidden", (await running).Error!.Code);
            Assert.Equal(0, (await fixture.ReadAsync(member.Membership)).Version);
        }
        finally
        {
            await StopAsync(held, running, deadline);
            await fixture.MutateAsync(SchemaFixture.TenantA, null, null, true, (app, transaction) => ExecuteAsync(app, transaction, """
                INSERT INTO role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                VALUES(@tenant,@role,'Tenant',@key) ON CONFLICT DO NOTHING;
                """, ("tenant", SchemaFixture.TenantA), ("role", role), ("key", IdentityFoundationManifest.ProfileWrite)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assignment_addition_and_reactivation_recheck_a_User_closure_committed_while_waiting_for_its_User_fence(bool reactivate)
    {
        var member = await fixture.AddMemberAsync();
        if (reactivate)
            Assert.True((await fixture.SendAsync(new ChangeMembershipStatusCommand(member.Membership, 0, ManagedMembershipStatus.Suspended),
                "membership-user-prepare-suspend")).IsSuccess);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        var original = await UserControlCensusAsync(owner);
        var check = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
        await ExecuteAsync(owner, null, $"""
            ALTER TABLE users DISABLE TRIGGER tg_users_no_update;
            ALTER POLICY users_isolation ON users WITH CHECK (({check}) OR
                (current_user='learnstack_migration' AND id='{member.Actor:D}'::uuid AND status='Closing'
                 AND deleted_at IS NULL AND deleted_by IS NULL));
            """);
        try
        {
            await using var harness = await fixture.CreateAsync("membership-user-postwait-" + reactivate);
            await using var held = await owner.BeginTransactionAsync();
            await LockAsync(owner, held, SchemaFixture.TenantA, member.Actor, true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var instructor = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Instructor, PermissionScope.Tenant);
            var running = reactivate
                ? harness.Sender.Send(new ChangeMembershipStatusCommand(member.Membership, 1, ManagedMembershipStatus.Active), deadline.Token)
                : harness.Sender.Send(new ReplaceMembershipRoleAssignmentsCommand(member.Membership, 0, [instructor]), deadline.Token);
            try
            {
                await fixture.ObserveWaitAsync(harness.ApplicationName, owner.ProcessID, running, deadline.Token);
                await ExecuteAsync(owner, held, "UPDATE users SET status='Closing' WHERE id=@user", ("user", member.Actor));
                await held.CommitAsync();
                Assert.Equal("forbidden", (await running).Error!.Code);
                var state = await fixture.ReadAsync(member.Membership);
                Assert.Equal(reactivate ? 1 : 0, state.Version);
                Assert.Equal(reactivate ? "Suspended" : "Active", state.Status);
                Assert.Equal(RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant), Assert.Single(state.Assignments).Key);
            }
            finally { await StopAsync(held, running, deadline); }
        }
        finally
        {
            await ExecuteAsync(owner, null, $"ALTER POLICY users_isolation ON users WITH CHECK ({check}); ALTER TABLE users ENABLE TRIGGER tg_users_no_update");
        }
        Assert.Equal(original, await UserControlCensusAsync(owner));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(owner, null,
            "UPDATE users SET status=status WHERE id=@user", ("user", member.Actor)));
        Assert.Equal("23514", refused.SqlState);
    }

    [Fact]
    public async Task Independent_profile_writers_share_the_Tenant_fence_and_never_serialize_on_their_common_Role()
    {
        var first = await fixture.AddMemberAsync();
        var second = await fixture.AddMemberAsync();
        await using var firstHarness = await fixture.CreateAsync("membership-concurrent-profile-first");
        await using var secondHarness = await fixture.CreateAsync("membership-concurrent-profile-second");
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var exclusive = (NpgsqlConnection)await PostgresFixture.OpenAsync(new NpgsqlConnectionStringBuilder(fixture.AppConnectionString)
        { ApplicationName = "membership-exclusive-contender" }.ConnectionString);
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, SchemaFixture.TenantA);
        await LockAsync(blocker, held, SchemaFixture.TenantA, null, false);
        Assert.Equal(first.Membership, await ScalarAsync<Guid>(blocker, held,
            "SELECT id FROM memberships WHERE id=@id FOR NO KEY UPDATE", ("id", first.Membership)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = firstHarness.Sender.Send(new SetMembershipProfileCommand(first.Membership, 0, "concurrent-first", null), deadline.Token);
        Task? exclusiveWait = null;
        await using var exclusiveFrame = await exclusive.BeginTransactionAsync();
        try
        {
            await fixture.ObserveWaitAsync(firstHarness.ApplicationName, blocker.ProcessID, running, deadline.Token);
            var independent = await secondHarness.Sender.Send(new SetMembershipProfileCommand(second.Membership, 0, "concurrent-second", null), deadline.Token);
            Assert.True(independent.IsSuccess);
            Assert.False(running.IsCompleted);
            await ScopeAsync(exclusive, exclusiveFrame, SchemaFixture.TenantA);
            exclusiveWait = LockAsync(exclusive, exclusiveFrame, SchemaFixture.TenantA, null, true);
            await fixture.ObserveWaitAsync("membership-exclusive-contender", blocker.ProcessID, exclusiveWait, deadline.Token);
            Assert.False(exclusiveWait.IsCompleted);
            await held.CommitAsync();
            Assert.True((await running).IsSuccess);
            await exclusiveWait;
            Assert.Equal("concurrent-second", (await fixture.ReadAsync(second.Membership)).DisplayName);
        }
        finally
        {
            await StopAsync(held, running, deadline);
            if (exclusiveWait is { IsCompleted: false })
            {
                await exclusive.CloseAsync();
                try { await exclusiveWait; } catch (NpgsqlException) { }
            }
        }
    }

    private static async Task StopAsync(NpgsqlTransaction held, Task running, CancellationTokenSource deadline)
    {
        // Dispose rolls back only a still-open frame and also accepts a
        // completed COMMIT; Connection is not a completion indicator.
        await held.DisposeAsync();
        if (!running.IsCompleted) deadline.Cancel();
        try { await running; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }

    private static Task<string> UserControlCensusAsync(NpgsqlConnection owner) => ScalarAsync<string>(owner, null, """
        SELECT jsonb_build_object(
          'functions',(SELECT jsonb_agg(jsonb_build_array(p.proname,p.prosrc,p.prosecdef,p.provolatile,p.proconfig,p.proacl::text,p.proowner) ORDER BY p.proname)
             FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname LIKE 'fn_identity_%'),
          'triggers',(SELECT jsonb_agg(jsonb_build_array(tgname,tgenabled,pg_get_triggerdef(oid)) ORDER BY tgname)
             FROM pg_trigger WHERE tgrelid='public.users'::regclass AND NOT tgisinternal),
          'policies',(SELECT jsonb_agg(jsonb_build_array(polname,polpermissive,polroles,pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)) ORDER BY polname)
             FROM pg_policy WHERE polrelid='public.users'::regclass),
          'table_acl',(SELECT relacl::text FROM pg_class WHERE oid='public.users'::regclass))::text
        """);
}
