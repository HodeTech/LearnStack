using FluentAssertions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Theory]
    [InlineData("memberships", "42501")]
    [InlineData("membership_profiles", "23514")]
    [InlineData("membership_role_assignments", "23514")]
    public async Task Restrictive_org_write_policy_filters_rows_before_independent_guards_and_its_dirty_twin_reaches_them(
        string table, string expectedState)
    {
        var (policy, mutation, targetColumn) = table switch
        {
            "memberships" => ("memberships_org_write_guard",
                "UPDATE public.memberships SET row_version=row_version WHERE id=@member RETURNING 1", "id"),
            "membership_profiles" => ("membership_profiles_org_write_guard",
                "UPDATE public.membership_profiles SET display_name=display_name WHERE membership_id=@member RETURNING 1", "membership_id"),
            "membership_role_assignments" => ("membership_role_assignments_org_delete_guard",
                "DELETE FROM public.membership_role_assignments WHERE membership_id=@member RETURNING 1", "membership_id"),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var original = await OrgWriteControlCensusAsync(owner);
        var predicate = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polqual,polrelid) FROM pg_policy WHERE polrelid=@table::regclass AND polname=@policy AND NOT polpermissive",
            ("table", "public." + table), ("policy", policy));
        predicate.Should().NotBeNullOrWhiteSpace("the actual restrictive policy is the isolated control");
        var countedMutation = "WITH changed AS (" + mutation + ") SELECT count(*) FROM changed";
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await AssertFilteredAsync();
        try
        {
            // The target restrictive USING clause alone changes. The ordinary
            // WITH CHECK, parent lock/read, triggers and grants remain intact.
            await ExecuteAsync(owner, null, $"ALTER POLICY {policy} ON public.{table} USING (true)");
            await using var dirty = await app.BeginTransactionAsync();
            await AnnounceOrgWriteControlAsync(app, dirty);
            Func<Task> write = () => ScalarAsync<long>(app, dirty, countedMutation, ("member", BranchMembershipA2));
            var refusal = (await write.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(expectedState);
            if (expectedState == "23514")
                refusal.ConstraintName.Should().Be("membership_parent_scope");
            await dirty.RollbackAsync();
        }
        finally
        {
            await ExecuteAsync(owner, null, $"ALTER POLICY {policy} ON public.{table} USING ({predicate})");
        }
        (await OrgWriteControlCensusAsync(owner)).Should().Be(original,
            "the fixture restores exact policies, triggers, function bodies and grants");
        await AssertFilteredAsync();

        async Task AssertFilteredAsync()
        {
            await using var clean = await app.BeginTransactionAsync();
            await AnnounceOrgWriteControlAsync(app, clean);
            (await ScalarAsync<long>(app, clean,
                $"SELECT count(*) FROM public.{table} WHERE {targetColumn}=@member",
                ("member", BranchMembershipA2))).Should().Be(1,
                "the tenant read hatch exposes the existing other-organization row");
            (await ScalarAsync<long>(app, clean, countedMutation, ("member", BranchMembershipA2))).Should().Be(0,
                "the restrictive policy filters the row before independent guards run");
            await clean.RollbackAsync();
        }
    }

    private static async Task AnnounceOrgWriteControlAsync(NpgsqlConnection app, NpgsqlTransaction transaction)
    {
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await ExecuteAsync(app, transaction, "SELECT set_config('app.scope','tenant',true)");
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
    }

    private static Task<string> OrgWriteControlCensusAsync(NpgsqlConnection owner) => ScalarAsync<string>(owner, null, """
        SELECT jsonb_build_object(
          'policies',(SELECT jsonb_agg(jsonb_build_array(polrelid,polname,polpermissive,polroles,polcmd,
              pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)) ORDER BY polrelid,polname)
            FROM pg_policy WHERE polrelid IN ('public.memberships'::regclass,
              'public.membership_profiles'::regclass,'public.membership_role_assignments'::regclass)),
          'triggers',(SELECT jsonb_agg(jsonb_build_array(tgrelid,tgname,tgenabled,pg_get_triggerdef(oid)) ORDER BY tgrelid,tgname)
            FROM pg_trigger WHERE NOT tgisinternal AND tgrelid IN ('public.memberships'::regclass,
              'public.membership_profiles'::regclass,'public.membership_role_assignments'::regclass)),
          'functions',(SELECT jsonb_agg(jsonb_build_array(proname,prosrc,prosecdef,provolatile,proconfig,proacl::text,proowner) ORDER BY proname)
            FROM pg_proc WHERE pronamespace='public'::regnamespace AND proname LIKE 'fn_identity_%'),
          'table_acl',(SELECT jsonb_agg(jsonb_build_array(oid,relacl::text) ORDER BY oid)
            FROM pg_class WHERE oid IN ('public.memberships'::regclass,
              'public.membership_profiles'::regclass,'public.membership_role_assignments'::regclass)))::text
        """);
}
