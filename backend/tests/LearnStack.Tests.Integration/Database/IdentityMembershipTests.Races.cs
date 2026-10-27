using System.Diagnostics;
using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Theory]
    [InlineData(false, "creation", UserStatus.Closing)]
    [InlineData(true, "creation", UserStatus.Closing)]
    [InlineData(false, "reactivation", UserStatus.Closing)]
    [InlineData(false, "assignment", UserStatus.Closing)]
    [InlineData(false, "creation", UserStatus.Closed)]
    [InlineData(false, "reactivation", UserStatus.Closed)]
    [InlineData(false, "assignment", UserStatus.Closed)]
    public async Task Actual_User_fence_orders_Membership_against_a_committing_closure_and_freshly_reads_status(bool membershipWins, string operation, UserStatus targetStatus)
    {
        if (targetStatus is not (UserStatus.Closing or UserStatus.Closed)) throw new ArgumentOutOfRangeException(nameof(targetStatus));
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var original = await UserControlCensusAsync(owner);
        var check = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
        // ADR-0074 permits this exact fixture-only accommodation. Neither missing
        // app grants nor FORCE RLS alone makes the migration owner a closure writer.
        await ExecuteAsync(owner, null, $"""
            ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
            ALTER POLICY users_isolation ON public.users WITH CHECK (({check}) OR
                (current_user='learnstack_migration' AND id='{PersonB:D}'::uuid AND status='{targetStatus}'
                 AND deleted_at IS NULL AND deleted_by IS NULL));
            """);
        try
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            var tenant = operation == "creation" ? SchemaFixture.TenantA : SchemaFixture.TenantB;
            Guid? organization = operation == "creation" ? null : SchemaFixture.OrgB1;
            if (operation == "reactivation")
            {
                await using var suspend = await app.BeginTransactionAsync();
                await ScopeAsync(app, suspend, tenant, organization);
                await LockAsync(app, suspend, tenant, null, false);
                await ExecuteAsync(app, suspend, "UPDATE public.memberships SET status='Suspended' WHERE id=@id", ("id", BranchMembershipB));
                await ExecuteAsync(app, suspend, "SET CONSTRAINTS ALL IMMEDIATE");
                await suspend.CommitAsync();
            }
            await using var admission = await app.BeginTransactionAsync();
            await ScopeAsync(app, admission, tenant, organization);
            var member = Guid.CreateVersion7();
            if (membershipWins)
            {
                await LockAsync(app, admission, SchemaFixture.TenantA, PersonB, false);
                await InsertMemberAsync(app, admission, SchemaFixture.TenantA, null, member, PersonB,
                    RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
                await ExecuteAsync(app, admission, "SET CONSTRAINTS ALL IMMEDIATE");
                var close = CloseUserAsync(owner, targetStatus);
                await WaitForAdvisoryWaitAsync(observer, owner.ProcessID);
                close.IsCompleted.Should().BeFalse();
                await admission.CommitAsync();
                await close;
                await using var read = await app.BeginTransactionAsync();
                await ScopeAsync(app, read, SchemaFixture.TenantA);
                (await ScalarAsync<long>(app, read, "SELECT count(*) FROM public.memberships WHERE id=@id", ("id", member))).Should().Be(1);
                await read.RollbackAsync();
            }
            else
            {
                await using var close = await owner.BeginTransactionAsync();
                await ClosureLocksAsync(owner, close);
                await TransitionUserAsync(owner, close, targetStatus);
                var waiting = LockAsync(app, admission, tenant, PersonB, false);
                await WaitForAdvisoryWaitAsync(observer, app.ProcessID);
                waiting.IsCompleted.Should().BeFalse();
                await close.CommitAsync();
                await waiting;
                Func<Task> mutate = operation switch
                {
                    "reactivation" => () => ExecuteAsync(app, admission, "UPDATE public.memberships SET status='Active' WHERE id=@id", ("id", BranchMembershipB)),
                    "assignment" => () => ExecuteAsync(app, admission,
                        "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,organization_id,role_id) VALUES(@tenant,@member,@organization,@role)",
                        ("tenant", tenant), ("member", BranchMembershipB), ("organization", organization),
                        ("role", RoleId(tenant, BuiltInRoleKey.OrgInstructor, PermissionScope.Organization))),
                    _ => () => InsertMemberAsync(app, admission, tenant, organization, member, PersonB,
                        RoleId(tenant, BuiltInRoleKey.Learner, PermissionScope.Tenant)),
                };
                var refused = (await mutate.Should().ThrowAsync<PostgresException>()).Which;
                refused.SqlState.Should().Be("P0302");
                refused.MessageText.Should().Be("identity membership target is unavailable");
                await admission.RollbackAsync();
            }
            (await ScalarAsync<string>(owner, null, "SELECT status FROM public.users WHERE id=@user", ("user", PersonB))).Should().Be(targetStatus.ToString());
            if (targetStatus == UserStatus.Closed)
                (await ScalarAsync<bool>(owner, null, "SELECT provider_subject IS NULL FROM public.users WHERE id=@user", ("user", PersonB))).Should().BeTrue();
        }
        finally
        {
            await ExecuteAsync(owner, null, $"ALTER POLICY users_isolation ON public.users WITH CHECK ({check}); ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update");
        }
        (await UserControlCensusAsync(owner)).Should().Be(original, "the fixture restores exact bodies, ACLs, owner, policy and trigger census");
        var ownerUpdate = () => ExecuteAsync(owner, null, "UPDATE public.users SET status=status WHERE id=@user", ("user", PersonB));
        (await ownerUpdate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Shared_writers_coexist_but_exclusive_writer_waits_until_real_commit()
    {
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();
        await using var observer = await OpenAsync();
        await using var left = await first.BeginTransactionAsync();
        await using var right = await second.BeginTransactionAsync();
        await ScopeAsync(first, left, SchemaFixture.TenantA);
        await ScopeAsync(second, right, SchemaFixture.TenantA);
        await LockAsync(first, left, SchemaFixture.TenantA, null, false);
        await LockAsync(second, right, SchemaFixture.TenantA, null, false);
        await ExecuteAsync(second, right, "UPDATE public.membership_profiles SET display_name=display_name WHERE membership_id=@id", ("id", LearnerMembershipA));
        await ExecuteAsync(second, right, "SET CONSTRAINTS ALL IMMEDIATE");
        await right.RollbackAsync();
        await using var exclusive = await second.BeginTransactionAsync();
        await ScopeAsync(second, exclusive, SchemaFixture.TenantA);
        var waiting = LockAsync(second, exclusive, SchemaFixture.TenantA, null, true);
        await WaitForAdvisoryWaitAsync(observer, second.ProcessID);
        await left.CommitAsync();
        await waiting;
        (await ScalarAsync<bool>(second, exclusive, "SELECT public.fn_identity_lock_held('tenant',@id,true)", ("id", SchemaFixture.TenantA))).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_lock_timeout_or_cancellation_releases_transaction_without_partial_graph(bool cancel)
    {
        await using var blocker = await OpenAsync();
        await using var waiting = await OpenAsync();
        await using var observer = await OpenAsync();
        await using var held = await blocker.BeginTransactionAsync();
        await LockAsync(blocker, held, SchemaFixture.TenantA, PersonB, false);
        await using var work = await waiting.BeginTransactionAsync();
        await ScopeAsync(waiting, work, SchemaFixture.TenantA);
        await ExecuteAsync(waiting, work, "SET LOCAL lock_timeout='200ms'");
        using var cancellation = new CancellationTokenSource();
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", waiting, work);
        command.Parameters.AddWithValue("key", IdentityAdvisoryLockKey.ForUser(UserId.From(PersonB)));
        var pending = command.ExecuteNonQueryAsync(cancellation.Token);
        await WaitForAdvisoryWaitAsync(observer, waiting.ProcessID);
        if (cancel)
        {
            cancellation.Cancel();
            await FluentActions.Awaiting(async () => await pending).Should().ThrowAsync<OperationCanceledException>();
        }
        else (await FluentActions.Awaiting(async () => await pending).Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55P03");
        await work.RollbackAsync();
        (await ScalarAsync<bool>(waiting, null, "SELECT public.fn_identity_lock_held('user',@id,true)", ("id", PersonB))).Should().BeFalse();
    }

    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task Contextual_guards_refuse_incompatible_snapshot_isolation(System.Data.IsolationLevel isolation)
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync(isolation);
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        var update = () => ExecuteAsync(app, transaction, "UPDATE public.membership_profiles SET display_name=display_name WHERE membership_id=@id", ("id", LearnerMembershipA));
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
    }

    [Fact]
    public async Task Competing_administrator_retirements_wait_for_commit_then_preserve_the_last_admin()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var first = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var second = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        var secondAdmin = Guid.CreateVersion7();
        await using (var seed = await first.BeginTransactionAsync())
        {
            await ScopeAsync(first, seed, SchemaFixture.TenantA);
            await LockAsync(first, seed, SchemaFixture.TenantA, PersonB, true);
            await InsertMemberAsync(first, seed, SchemaFixture.TenantA, null, secondAdmin, PersonB,
                RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant));
            await ExecuteAsync(first, seed, "SET CONSTRAINTS ALL IMMEDIATE");
            await seed.CommitAsync();
        }
        await using var retirement = await first.BeginTransactionAsync();
        await ScopeAsync(first, retirement, SchemaFixture.TenantA);
        await LockAsync(first, retirement, SchemaFixture.TenantA, null, true);
        await ExecuteAsync(first, retirement, "UPDATE public.memberships SET status='Suspended' WHERE id=@id", ("id", AdminMembershipA));
        await ExecuteAsync(first, retirement, "SET CONSTRAINTS ALL IMMEDIATE");
        await using var competing = await second.BeginTransactionAsync();
        await ScopeAsync(second, competing, SchemaFixture.TenantA);
        var waiting = LockAsync(second, competing, SchemaFixture.TenantA, null, true);
        await WaitForAdvisoryWaitAsync(observer, second.ProcessID);
        await retirement.CommitAsync();
        await waiting;
        await ExecuteAsync(second, competing, "UPDATE public.memberships SET status='Suspended' WHERE id=@id", ("id", secondAdmin));
        var finish = () => ExecuteAsync(second, competing, "SET CONSTRAINTS ALL IMMEDIATE");
        (await finish.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0303");
        await competing.RollbackAsync();
    }

    private static async Task CloseUserAsync(NpgsqlConnection owner, UserStatus targetStatus)
    {
        await using var transaction = await owner.BeginTransactionAsync();
        await ClosureLocksAsync(owner, transaction);
        await TransitionUserAsync(owner, transaction, targetStatus);
        await transaction.CommitAsync();
    }

    private static Task TransitionUserAsync(NpgsqlConnection owner, NpgsqlTransaction transaction, UserStatus targetStatus) =>
        ExecuteAsync(owner, transaction,
            "UPDATE public.users SET status=@status,provider_subject=CASE WHEN @status='Closed' THEN NULL ELSE provider_subject END WHERE id=@user",
            ("status", targetStatus.ToString()), ("user", PersonB));

    private static async Task ClosureLocksAsync(NpgsqlConnection owner, NpgsqlTransaction transaction)
    {
        await ExecuteAsync(owner, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(PersonB))));
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB }.OrderBy(id => IdentityAdvisoryLockKey.ForTenant(TenantId.From(id))))
            await ExecuteAsync(owner, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(tenant))));
    }

    private static async Task WaitForAdvisoryWaitAsync(NpgsqlConnection observer, int pid)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await ScalarAsync<bool>(observer, null, "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid=@pid AND locktype='advisory' AND NOT granted)", ("pid", pid))) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The fixture did not observe the required competing advisory wait.");
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
