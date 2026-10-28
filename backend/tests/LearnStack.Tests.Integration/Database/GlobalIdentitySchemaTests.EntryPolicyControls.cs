using FluentAssertions;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentitySchemaTests
{
    // Independent Standard 05 contract oracle. Do not load the expected predicate
    // from the migration under test: deleting the same term from a migration and
    // its inferred expectation must not make this proof pass.
    private const string ExpectedMemberAppUsing = """
        tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
        AND (organization_id IS NULL
            OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid
            OR NULLIF(current_setting('app.scope',true),'') = 'tenant')
        """;
    private const string ExpectedMemberOwnerUsing = """
        current_user = 'learnstack_migration' AND status = 'Active' AND deleted_at IS NULL
        AND EXISTS (SELECT FROM public.users u WHERE u.id = public.memberships.user_id
            AND u.authority_id::text = NULLIF(current_setting('app.identity_authority_id',true),'')
            AND encode(u.subject_key,'hex') = NULLIF(current_setting('app.identity_subject_key',true),'')
            AND u.status = 'Active')
        """;
    private const string ExpectedMemberCheck = """
        tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
        AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
            OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid)
        """;

    [Fact]
    public async Task Applied_membership_discovery_policy_matches_the_independent_owner_and_unchanged_app_contract()
    {
        // The shared least-privilege database deliberately revokes TEMP. Use
        // this test's disposable migrated database for the parser-only oracle;
        // no application/migration grant is widened to support the test.
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        var inspection = await InspectMemberEntryPolicyAsync(database.MigrationConnectionString);
        inspection.Violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("owner-role")]
    [InlineData("wrong-user")]
    [InlineData("membership-active")]
    [InlineData("user-active")]
    [InlineData("deleted")]
    public async Task Applied_membership_owner_policy_detects_committed_predicate_faults_and_restores_a_clean_twin(string fault)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await SuspendPolicyControlBranchAsync(database.AppConnectionString);
        var original = await InspectMemberEntryPolicyAsync(database.MigrationConnectionString);
        original.Violations.Should().BeEmpty();
        var own = MemberPolicySubject(IdentityMembershipFixture.PersonA);
        Guid[] ownActive = [IdentityMembershipFixture.LearnerMembershipA, IdentityMembershipFixture.BranchMembershipA1];
        (await ReadPolicyMembershipsAsync(database.MigrationConnectionString, own)).Should().BeEquivalentTo(ownActive);
        await AssertUnchangedMemberAppScopeAsync(database.AppConnectionString, original.Actual.Check!);

        var ownerArm = fault switch
        {
            "owner-role" => ExpectedMemberOwnerUsing.Replace("current_user = 'learnstack_migration'", "true", StringComparison.Ordinal),
            "wrong-user" => ExpectedMemberOwnerUsing.Replace("u.id = public.memberships.user_id", "u.id <> public.memberships.user_id", StringComparison.Ordinal),
            "membership-active" => ExpectedMemberOwnerUsing.Replace("AND status = 'Active'", "AND status IN ('Active','Suspended')", StringComparison.Ordinal),
            "user-active" => ExpectedMemberOwnerUsing.Replace("AND u.status = 'Active'", "AND true", StringComparison.Ordinal),
            "deleted" => ExpectedMemberOwnerUsing.Replace("AND deleted_at IS NULL", "AND true", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        ownerArm.Should().NotBe(ExpectedMemberOwnerUsing);
        try
        {
            await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
                await ExecuteAsync(owner, null, $"ALTER POLICY memberships_isolation ON public.memberships USING (({ExpectedMemberAppUsing}) OR ({ownerArm}))");

            // The inspector and behavioral readers open independent physical
            // connections after the ALTER POLICY has committed.
            var dirty = await InspectMemberEntryPolicyAsync(database.MigrationConnectionString);
            dirty.Violations.Should().ContainSingle().Which.Should().Be("memberships_isolation:using");
            dirty.Actual.Check.Should().Be(original.Actual.Check);
            await AssertUnchangedMemberAppScopeAsync(database.AppConnectionString, dirty.Actual.Check!);
            switch (fault)
            {
                case "owner-role":
                    (await ReadPolicyMembershipsAsync(database.AppConnectionString, own)).Should().BeEquivalentTo(ownActive,
                        "removing the role qualification really exposes self Memberships without an app tenant announcement");
                    break;
                case "wrong-user":
                    (await ReadPolicyMembershipsAsync(database.MigrationConnectionString, own)).Should().BeEquivalentTo(
                        new[] { IdentityMembershipFixture.AdminMembershipA, IdentityMembershipFixture.AdminMembershipB,
                            IdentityMembershipFixture.BranchMembershipB },
                        "the committed reversed correlation selects real foreign actors rather than this subject");
                    break;
                case "membership-active":
                    (await ReadPolicyMembershipsAsync(database.MigrationConnectionString, own)).Should().BeEquivalentTo(
                        ownActive.Append(IdentityMembershipFixture.BranchMembershipA2),
                        "a valid Suspended Membership demonstrates the owner status predicate independently of the function's own WHERE");
                    break;
                default:
                    // These exact-predicate dirty twins prove the applied-policy
                    // inspector, not a runtime leak from an impossible fixture.
                    // The lifecycle CHECK forbids Active+deleted Memberships;
                    // no production User Closing writer is admitted yet.
                    (await ReadPolicyMembershipsAsync(database.MigrationConnectionString, own)).Should().BeEquivalentTo(ownActive);
                    break;
            }
            (await ReadPolicyMembershipsAsync(database.MigrationConnectionString, null)).Should().BeEmpty();
            (await ReadPolicyMembershipsAsync(database.MigrationConnectionString,
                new GlobalIdentitySubject(own.AuthorityId, own.Issuer, "absent-policy-subject"))).Should().BeEmpty();
        }
        finally
        {
            await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
                await ExecuteAsync(owner, null, $"""
                    ALTER POLICY memberships_isolation ON public.memberships
                        USING ({original.Actual.Using}) WITH CHECK ({original.Actual.Check});
                    """);
            (await InspectMemberEntryPolicyAsync(database.MigrationConnectionString)).Violations.Should().BeEmpty();
            (await ReadPolicyMembershipsAsync(database.MigrationConnectionString, own)).Should().BeEquivalentTo(ownActive,
                "restoring the owned policy must restore the nonempty clean positive, not just hide every row");
            (await ReadPolicyMembershipsAsync(database.AppConnectionString, own)).Should().BeEmpty();
        }
    }

    private static async Task<MemberPolicyInspection> InspectMemberEntryPolicyAsync(string connectionString)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        var actual = await ReadMemberPolicySnapshotAsync(owner, "public.memberships", "memberships_isolation");
        // Let PostgreSQL canonicalize the independent expected expression, so
        // harmless parser/deparser whitespace or cast rendering is not guessed by
        // a regex. Only the deliberately different outer relation name changes.
        var expectedOwner = ExpectedMemberOwnerUsing.Replace("public.memberships.user_id",
            "entry_policy_expected.user_id", StringComparison.Ordinal);
        await ExecuteAsync(owner, null, $"""
            CREATE TEMP TABLE entry_policy_expected (
                tenant_id uuid,organization_id uuid,user_id uuid,status text,deleted_at timestamptz);
            CREATE POLICY entry_policy_expected ON entry_policy_expected
                USING (({ExpectedMemberAppUsing}) OR ({expectedOwner})) WITH CHECK ({ExpectedMemberCheck});
            """);
        var expected = await ReadMemberPolicySnapshotAsync(owner, "pg_temp.entry_policy_expected", "entry_policy_expected");
        var violations = new List<string>();
        if (actual.Using != expected.Using.Replace("entry_policy_expected.", "memberships.", StringComparison.Ordinal))
            violations.Add("memberships_isolation:using");
        if (actual.Check != expected.Check) violations.Add("memberships_isolation:with-check");
        if (actual.Command != "*" || !actual.Permissive || !actual.Roles.SequenceEqual(["PUBLIC"]))
            violations.Add("memberships_isolation:command-or-roles");
        if (!actual.Rls || !actual.Force) violations.Add("memberships:force-rls");
        if (!actual.PolicyNames.SequenceEqual(["memberships_isolation", "memberships_org_delete_guard", "memberships_org_write_guard"]))
            violations.Add("memberships:policy-census");
        return new(actual, violations);
    }

    private static async Task<MemberPolicySnapshot> ReadMemberPolicySnapshotAsync(NpgsqlConnection connection,
        string relation, string policy)
    {
        await using var read = new NpgsqlCommand("""
            SELECT pg_catalog.pg_get_expr(p.polqual,p.polrelid),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid),
                p.polcmd::text,p.polpermissive,
                ARRAY(SELECT CASE WHEN role_oid=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(role_oid)::text END
                    FROM unnest(p.polroles) AS role_oid ORDER BY role_oid),c.relrowsecurity,c.relforcerowsecurity,
                ARRAY(SELECT other.polname::text FROM pg_catalog.pg_policy other
                    WHERE other.polrelid=p.polrelid ORDER BY other.polname)
            FROM pg_catalog.pg_policy p JOIN pg_catalog.pg_class c ON c.oid=p.polrelid
            WHERE p.polrelid=to_regclass(@relation) AND p.polname=@policy
            """, connection);
        read.Parameters.AddWithValue("relation", relation);
        read.Parameters.AddWithValue("policy", policy);
        await using var row = await read.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue("the exact deployed relation and policy must exist");
        var result = new MemberPolicySnapshot(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1),
            row.GetString(2), row.GetBoolean(3), row.GetFieldValue<string[]>(4), row.GetBoolean(5),
            row.GetBoolean(6), row.GetFieldValue<string[]>(7));
        (await row.ReadAsync()).Should().BeFalse();
        return result;
    }

    private static async Task SuspendPolicyControlBranchAsync(string connectionString)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA2);
        await IdentityMembershipFixture.LockAsync(app, transaction, SchemaFixture.TenantA, IdentityMembershipFixture.PersonA, false);
        await using var update = new NpgsqlCommand("""
            UPDATE public.memberships SET status='Suspended',row_version=row_version+1 WHERE id=@member;
            SET CONSTRAINTS ALL IMMEDIATE;
            """, app, transaction);
        update.Parameters.AddWithValue("member", IdentityMembershipFixture.BranchMembershipA2);
        await update.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static GlobalIdentitySubject MemberPolicySubject(Guid user) => new(
        IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer,
        "fixture-subject-" + user.ToString("N"));

    private static async Task<Guid[]> ReadPolicyMembershipsAsync(string connectionString, GlobalIdentitySubject? subject,
        Guid? tenant = null, Guid? organization = null, bool tenantRead = false)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await ClearAsync(connection, transaction);
        if (subject is not null) await AnnounceAsync(connection, transaction, subject);
        if (tenant is { } selected)
            await IdentityMembershipFixture.ScopeAsync(connection, transaction, selected, organization);
        if (tenantRead) await ExecuteAsync(connection, transaction, "SELECT set_config('app.scope','tenant',true)");
        await using var read = new NpgsqlCommand("SELECT id FROM public.memberships ORDER BY id", connection, transaction);
        await using var rows = await read.ExecuteReaderAsync();
        var values = new List<Guid>();
        while (await rows.ReadAsync()) values.Add(rows.GetGuid(0));
        return values.ToArray();
    }

    private static async Task AssertUnchangedMemberAppScopeAsync(string connectionString, string appliedCheck)
    {
        (await ReadPolicyMembershipsAsync(connectionString, null)).Should().BeEmpty();
        (await ReadPolicyMembershipsAsync(connectionString, null, SchemaFixture.TenantA, SchemaFixture.OrgA1))
            .Should().BeEquivalentTo(new[] { IdentityMembershipFixture.AdminMembershipA,
                IdentityMembershipFixture.LearnerMembershipA, IdentityMembershipFixture.BranchMembershipA1 });
        (await ReadPolicyMembershipsAsync(connectionString, null, SchemaFixture.TenantA, SchemaFixture.OrgA1, tenantRead: true))
            .Should().BeEquivalentTo(new[] { IdentityMembershipFixture.AdminMembershipA,
                IdentityMembershipFixture.LearnerMembershipA, IdentityMembershipFixture.BranchMembershipA1,
                IdentityMembershipFixture.BranchMembershipA2 });
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ClearAsync(app, transaction);
        foreach (var branch in new Guid?[] { null, SchemaFixture.OrgA1 })
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA, branch);
            foreach (var candidate in new[]
            {
                (Tenant: SchemaFixture.TenantA, Org: (Guid?)null),
                (Tenant: SchemaFixture.TenantA, Org: (Guid?)SchemaFixture.OrgA1),
                (Tenant: SchemaFixture.TenantA, Org: (Guid?)SchemaFixture.OrgA2),
                (Tenant: SchemaFixture.TenantB, Org: (Guid?)null),
            })
            {
                // Evaluate the actual applied WITH CHECK independently of the
                // earlier mutation/admission triggers; no malformed row is stored.
                await using var evaluate = new NpgsqlCommand($"""
                    SELECT ({appliedCheck}) IS TRUE
                    FROM (VALUES (@tenant::uuid,@organization::uuid)) AS memberships(tenant_id,organization_id)
                    """, app, transaction);
                evaluate.Parameters.AddWithValue("tenant", candidate.Tenant);
                evaluate.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)candidate.Org ?? DBNull.Value);
                (await evaluate.ExecuteScalarAsync()).Should().Be(candidate.Tenant == SchemaFixture.TenantA && candidate.Org == branch);
            }
        }
    }

    private sealed record MemberPolicySnapshot(string Using, string? Check, string Command, bool Permissive,
        string[] Roles, bool Rls, bool Force, string[] PolicyNames);
    private sealed record MemberPolicyInspection(MemberPolicySnapshot Actual, IReadOnlyList<string> Violations);
}
