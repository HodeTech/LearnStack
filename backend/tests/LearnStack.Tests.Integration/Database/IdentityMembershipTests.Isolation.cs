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
        // This disposable control isolates WITH CHECK from the earlier lock and
        // User admission guards. The dirty twin changes only that policy clause.
        await ExecuteAsync(owner, null, $"ALTER TABLE public.{table} DISABLE TRIGGER tg_{table}_mutation_guard; ALTER POLICY {table}_isolation ON public.{table} USING (true)");
        if (table is "memberships" or "membership_role_assignments")
            await ExecuteAsync(owner, null, $"ALTER TABLE public.{table} DISABLE TRIGGER tg_{table}_admission");
        try
        {
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
                await dirty.RollbackAsync();
            }
        }
        finally
        {
            await ExecuteAsync(owner, null, $"ALTER POLICY {table}_isolation ON public.{table} USING ({usingClause}) WITH CHECK ({checkClause}); ALTER TABLE public.{table} ENABLE TRIGGER tg_{table}_mutation_guard");
            if (table is "memberships" or "membership_role_assignments")
                await ExecuteAsync(owner, null, $"ALTER TABLE public.{table} ENABLE TRIGGER tg_{table}_admission");
        }
        (await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid=@table::regclass AND polname=@policy",
            ("table", "public." + table), ("policy", table + "_isolation"))).Should().Be(checkClause);
    }
}
