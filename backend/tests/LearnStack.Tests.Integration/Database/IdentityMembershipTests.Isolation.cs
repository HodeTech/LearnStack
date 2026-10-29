using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    public static IReadOnlyDictionary<string, string> WithCheckProofs { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["memberships"] = nameof(Contextual_WITH_CHECK_rejects_foreign_writes_and_its_dirty_twin_admits_them),
            ["membership_profiles"] = nameof(Contextual_WITH_CHECK_rejects_foreign_writes_and_its_dirty_twin_admits_them),
            ["membership_role_assignments"] = nameof(Contextual_WITH_CHECK_rejects_foreign_writes_and_its_dirty_twin_admits_them),
            ["roles"] = nameof(Contextual_WITH_CHECK_rejects_foreign_writes_and_its_dirty_twin_admits_them),
            ["role_permission_grants"] = nameof(Contextual_WITH_CHECK_rejects_foreign_writes_and_its_dirty_twin_admits_them),
        };

    [Theory]
    [InlineData("memberships")]
    [InlineData("membership_profiles")]
    [InlineData("membership_role_assignments")]
    [InlineData("roles")]
    [InlineData("role_permission_grants")]
    public async Task Contextual_WITH_CHECK_rejects_foreign_writes_and_its_dirty_twin_admits_them(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var usingClause = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polqual,polrelid) FROM pg_policy WHERE polrelid=@table::regclass AND polname=@policy",
            ("table", "public." + table), ("policy", table + "_isolation"));
        var checkClause = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid=@table::regclass AND polname=@policy",
            ("table", "public." + table), ("policy", table + "_isolation"));
        const string triggerStateSql = """
            SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname)
            FROM pg_catalog.pg_trigger WHERE tgrelid=@table::regclass
            """;
        var triggerState = await ScalarAsync<string>(owner, null, triggerStateSql, ("table", "public." + table));
        string[] guards = table switch
        {
            "memberships" or "membership_role_assignments" => [$"tg_{table}_mutation_guard", $"tg_{table}_admission"],
            "role_permission_grants" => [$"tg_{table}_mutation_guard", "tg_role_permission_grants_root_lock"],
            _ => [$"tg_{table}_mutation_guard"],
        };
        foreach (var guard in guards)
            (await ScalarAsync<string>(owner, null, """
                SELECT tgenabled::text FROM pg_catalog.pg_trigger
                WHERE tgrelid=@table::regclass AND tgname=@trigger
                """, ("table", "public." + table), ("trigger", guard))).Should().Be("O");
        // This disposable control isolates WITH CHECK from independent BEFORE
        // guards, including the newer Role-root fence. The dirty twin changes
        // only WITH CHECK between its two actual application-role statements.
        try
        {
            foreach (var guard in guards)
                await ExecuteAsync(owner, null, $"ALTER TABLE public.{table} DISABLE TRIGGER {guard}");
            await ExecuteAsync(owner, null, $"ALTER POLICY {table}_isolation ON public.{table} USING (true)");
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            var mutation = table switch
            {
                "memberships" => "UPDATE public.memberships SET row_version=row_version+1 WHERE id=@member",
                "membership_profiles" => "UPDATE public.membership_profiles SET display_name='foreign-policy-control' WHERE membership_id=@member",
                "roles" => "UPDATE public.roles SET row_version=row_version+1 WHERE id=@role",
                "membership_role_assignments" => "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id) VALUES(@tenant,@member,@role)",
                _ => "INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key) VALUES(@tenant,@role,'Tenant',@key)",
            };
            (string Name, object? Value)[] values = [("member", AdminMembershipB), ("tenant", SchemaFixture.TenantB),
                ("role", RoleId(SchemaFixture.TenantB, BuiltInRoleKey.Instructor, PermissionScope.Tenant)), ("key", IdentityFoundationManifest.ProfileWrite)];
            await using (var clean = await app.BeginTransactionAsync())
            {
                await ScopeAsync(app, clean, SchemaFixture.TenantA);
                var denied = () => ExecuteAsync(app, clean, mutation, values);
                (await denied.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
                await clean.RollbackAsync();
            }
            await ExecuteAsync(owner, null, $"ALTER POLICY {table}_isolation ON public.{table} WITH CHECK (true)");
            await using (var dirty = await app.BeginTransactionAsync())
            {
                await ScopeAsync(app, dirty, SchemaFixture.TenantA);
                await ExecuteAsync(app, dirty, mutation, values);
                if (table == "role_permission_grants")
                    (await ScalarAsync<long>(app, dirty, """
                        SELECT count(*) FROM public.role_permission_grants
                        WHERE tenant_id=@tenant AND role_id=@role AND permission_key=@key
                        """, values)).Should().Be(1, "the dirty WITH CHECK twin admits the actual foreign grant row");
                await dirty.RollbackAsync();
            }
        }
        finally
        {
            await ExecuteAsync(owner, null, $"ALTER POLICY {table}_isolation ON public.{table} USING ({usingClause}) WITH CHECK ({checkClause})");
            foreach (var guard in guards)
                await ExecuteAsync(owner, null, $"ALTER TABLE public.{table} ENABLE TRIGGER {guard}");
        }
        (await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polqual,polrelid) FROM pg_policy WHERE polrelid=@table::regclass AND polname=@policy",
            ("table", "public." + table), ("policy", table + "_isolation"))).Should().Be(usingClause);
        (await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid=@table::regclass AND polname=@policy",
            ("table", "public." + table), ("policy", table + "_isolation"))).Should().Be(checkClause);
        (await ScalarAsync<string>(owner, null, triggerStateSql, ("table", "public." + table))).Should().Be(triggerState,
            "the fixture restores every original trigger state after isolating WITH CHECK");
    }
}
