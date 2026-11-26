using System.Reflection;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountClosureStorageTests
{
    /// <summary>
    /// ADR-0107 § 8's platform grants, read from the attribute ACLs: column SELECT on <c>users</c> and <c>memberships</c>,
    /// the last-administrator predicate's columns of the assignment, Role and grant tables, UPDATE of <c>status</c> and
    /// <c>provider_subject</c> on <c>users</c>, and EXECUTE on the two lock observers; nothing table-wide, nothing on the
    /// profile or the authority anchor. With them the role reads a person's participant groups in every tenant, a Revoked
    /// tombstone's included, through the Membership user index, and nothing more.
    /// </summary>
    [Fact]
    public async Task The_platform_role_holds_exactly_the_closed_columns_and_the_two_lock_observers()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await ArrangeAsync(database);
        await using var owner = await OpenAsync(database.MigrationConnectionString);
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT c.relname || '.' || a.attname || ':' || x.privilege_type
            FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            CROSS JOIN LATERAL aclexplode(a.attacl) x
            WHERE c.relnamespace = 'public'::regnamespace
              AND c.relname IN ('users','memberships','membership_profiles','membership_role_assignments','roles',
                  'role_permission_grants','identity_authority')
              AND x.grantee = 'learnstack_platform'::regrole
            ORDER BY 1
            """)).Should().Equal(
            "membership_role_assignments.membership_id:SELECT", "membership_role_assignments.role_id:SELECT",
            "membership_role_assignments.tenant_id:SELECT",
            "memberships.deleted_at:SELECT", "memberships.id:SELECT", "memberships.organization_id:SELECT",
            "memberships.status:SELECT", "memberships.tenant_id:SELECT", "memberships.user_id:SELECT",
            "role_permission_grants.permission_key:SELECT", "role_permission_grants.permission_scope:SELECT",
            "role_permission_grants.role_id:SELECT", "role_permission_grants.tenant_id:SELECT",
            "roles.built_in_key:SELECT", "roles.deleted_at:SELECT", "roles.deleted_by:SELECT", "roles.id:SELECT",
            "roles.permission_scope:SELECT", "roles.tenant_id:SELECT",
            "users.id:SELECT", "users.provider_subject:SELECT", "users.provider_subject:UPDATE", "users.status:SELECT",
            "users.status:UPDATE", "users.subject_key:SELECT");
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT c.relname || ':' || x.privilege_type
            FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) x
            WHERE c.relnamespace = 'public'::regnamespace
              AND c.relname IN ('users','memberships','membership_profiles','membership_role_assignments','roles',
                  'role_permission_grants','identity_authority')
              AND x.grantee = 'learnstack_platform'::regrole
            """)).Should().BeEmpty("no table-wide privilege: the role bypasses row security, so its column grants are its whole bound");
        (await SchemaQueries.ReadStringsAsync(owner, """
            SELECT p.oid::regprocedure::text
            FROM pg_proc p CROSS JOIN LATERAL aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) x
            WHERE p.pronamespace = 'public'::regnamespace AND p.proname LIKE 'fn_identity_%'
              AND x.grantee = 'learnstack_platform'::regrole AND x.privilege_type = 'EXECUTE'
            ORDER BY 1
            """)).Should().Equal("fn_identity_contextual_lock_key(text,uuid)", "fn_identity_lock_held(text,uuid,boolean)");

        await using var platform = await OpenAsync(database.PlatformConnectionString);
        (await ScalarAsync<string>(platform, null, """
            SELECT string_agg(grp, ',' ORDER BY grp COLLATE "C") FROM (SELECT DISTINCT member.tenant_id::text || '/'
                || COALESCE(member.organization_id::text, '-') AS grp FROM public.memberships AS member WHERE member.user_id=@user) AS groups
            """, ("user", PersonA))).Should().Be(string.Join(',', new[]
            {
                $"{SchemaFixture.TenantA:D}/-", $"{SchemaFixture.TenantA:D}/{SchemaFixture.OrgA1:D}",
                $"{SchemaFixture.TenantA:D}/{SchemaFixture.OrgA2:D}", $"{SchemaFixture.TenantB:D}/-",
            }.Order(StringComparer.Ordinal)), "every group of the person in every tenant, the Revoked tombstone's included, with no announcement");
        (await ScalarAsync<string>(platform, null, """
            SELECT status || ':' || (provider_subject IS NOT NULL)::text || ':' || octet_length(subject_key)::text
            FROM public.users WHERE id=@user
            """, ("user", PersonA))).Should().Be("Active:true:32");

        foreach (var statement in new[]
        {
            "SELECT * FROM public.users", "SELECT authority_id FROM public.users", "SELECT row_version FROM public.users",
            "SELECT * FROM public.memberships", "SELECT created_by FROM public.memberships", "SELECT deleted_by FROM public.memberships",
            "SELECT count(*) FROM public.membership_profiles", "SELECT organization_id FROM public.membership_role_assignments",
            "SELECT created_at FROM public.roles", "SELECT count(*) FROM public.identity_authority",
            "UPDATE public.users SET row_version=0", "INSERT INTO public.users(id) VALUES(uuidv7())", "DELETE FROM public.users",
            "UPDATE public.memberships SET status='Active'", "DELETE FROM public.memberships", "UPDATE public.roles SET built_in_key=built_in_key",
            "DELETE FROM public.membership_role_assignments", "INSERT INTO public.role_permission_grants(tenant_id) VALUES(uuidv7())",
        })
        {
            await using var transaction = await platform.BeginTransactionAsync();
            var act = () => ExecuteAsync(platform, transaction, statement);
            (await act.Should().ThrowAsync<PostgresException>(statement)).Which.SqlState.Should().Be("42501", statement);
        }

        // The participant read is served by the Membership user index, as are the trigger's and the guard's fence checks.
        await using (var transaction = await platform.BeginTransactionAsync())
        {
            await ExecuteAsync(platform, transaction, "SET LOCAL enable_seqscan = off");
            (await ScalarAsync<string>(platform, transaction, """
                EXPLAIN (FORMAT JSON) SELECT 1 FROM public.memberships AS member WHERE member.user_id = @user GROUP BY member.tenant_id
                """, ("user", PersonA))).Should().Contain("ix_memberships_user_id");
        }
    }

    /// <summary>
    /// The last-administrator judgment of a participant tenant runs on the platform connection over exactly the predicate of
    /// the deferred complete-graph guard, less <c>member.deleted_by IS NULL</c>, which <c>ck_memberships_lifecycle</c> makes
    /// redundant beside <c>member.status = 'Active'</c> and which the role cannot read; it answers as the application role's
    /// tenant-announced read answers.
    /// </summary>
    [Fact]
    public async Task The_last_administrator_predicate_reads_the_same_answer_on_the_platform_connection()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var standing = (string)typeof(DataRequestStore).GetField("AdministratorStanding", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        const string Redundant = " AND member.deleted_by IS NULL";
        standing.IndexOf(Redundant, StringComparison.Ordinal).Should().BeGreaterThan(0)
            .And.Be(standing.LastIndexOf(Redundant, StringComparison.Ordinal), "the predicate names the Membership's deleter exactly once");
        var platformStanding = standing.Replace(Redundant, string.Empty, StringComparison.Ordinal);

        await using var platform = await OpenAsync(database.PlatformConnectionString);
        await using (var transaction = await platform.BeginTransactionAsync())
        {
            var verbatim = () => StandingAsync(platform, transaction, standing, SchemaFixture.TenantA, AdminA);
            (await verbatim.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
                "the deleter of a Membership is not among the role's columns");
        }

        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        foreach (var (tenant, subject, expected) in new[]
        {
            (SchemaFixture.TenantA, AdminA, "True:False"), (SchemaFixture.TenantA, PersonA, "False:True"),
            (SchemaFixture.TenantB, AdminB, "True:False"), (SchemaFixture.TenantB, PersonB, "False:True"),
        })
        {
            await using (var read = await app.BeginTransactionAsync())
            {
                await ScopeAsync(app, read, tenant);
                (await StandingAsync(app, read, standing, tenant, subject)).Should().Be(expected);
            }

            await using var transaction = await platform.BeginTransactionAsync();
            (await StandingAsync(platform, transaction, platformStanding, tenant, subject)).Should().Be(expected,
                "the platform connection reads the application's answer for every tenant without announcing any");
        }
    }

    private static async Task<string> StandingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid tenant, Guid subject)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("subject", subject);
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetBoolean(0) ? "True" : "False") + ":" + (row.GetBoolean(1) ? "True" : "False");
    }
}
