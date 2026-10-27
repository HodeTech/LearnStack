using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Fact]
    public async Task Parent_mirror_correction_replaces_only_its_function_and_reapplies_the_fresh_chain_exactly()
    {
        const string Predecessor = "20261025232309_add_contextual_identity";
        const string Correction = "20261026035353_correct_identity_parent_mirror_sqlstate";
        const string Guard = "fn_identity_contextual_mutation_guard()";
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var context = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(database.MigrationConnectionString, options =>
                options.MigrationsHistoryTable(IdentityDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var fresh = await ReadIdentityFunctionsAsync(database);
        fresh.Should().HaveCountGreaterThan(1, "the replacement must preserve actual sibling functions");
        await context.GetService<IMigrator>().MigrateAsync(Correction);
        var corrected = await ReadIdentityFunctionsAsync(database);
        await AssertParentMirrorFailureAsync(database, "23514", "membership_parent_scope");

        await context.GetService<IMigrator>().MigrateAsync(Predecessor);
        var predecessor = await ReadIdentityFunctionsAsync(database);
        await AssertParentMirrorFailureAsync(database, "P0304", null);
        var previousGuard = predecessor.Except(corrected).Should().ContainSingle().Which;
        var correctedGuard = corrected.Except(predecessor).Should().ContainSingle().Which;
        previousGuard.Identity.Should().Be(Guard);
        correctedGuard.Identity.Should().Be(Guard);
        (correctedGuard with { Definition = previousGuard.Definition }).Should().Be(previousGuard,
            "CREATE OR REPLACE retains the same function OID, owner, ACL, volatility, invoker mode and search path");
        correctedGuard.Owner.Should().Be("learnstack_migration");
        correctedGuard.SecurityDefiner.Should().BeFalse();
        correctedGuard.Volatility.Should().Be("v");
        correctedGuard.Configuration.Should().Be("search_path=pg_catalog");
        previousGuard.Definition.Should().NotContain("membership_parent_scope");
        correctedGuard.Definition.Should().Contain("ERRCODE = '23514', CONSTRAINT = 'membership_parent_scope'");

        await context.GetService<IMigrator>().MigrateAsync(Correction);
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(corrected);
        await AssertParentMirrorFailureAsync(database, "23514", "membership_parent_scope");
        await context.GetService<IMigrator>().MigrateAsync(Predecessor);
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(predecessor);
        await AssertParentMirrorFailureAsync(database, "P0304", null);
        await context.GetService<IMigrator>().MigrateAsync(Correction);
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(corrected);
        await AssertParentMirrorFailureAsync(database, "23514", "membership_parent_scope");
        await context.Database.MigrateAsync();
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(fresh);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task Admission_parent_correction_preserves_its_definer_boundary_and_converges_with_the_fresh_chain()
    {
        const string Predecessor = "20261026035353_correct_identity_parent_mirror_sqlstate";
        const string Correction = "20261026043921_correct_identity_admission_parent_sqlstate";
        const string Guard = "fn_identity_membership_admission_guard()";
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await IdentityMembershipFixture.SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var context = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(database.MigrationConnectionString, options =>
                options.MigrationsHistoryTable(IdentityDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var fresh = await ReadIdentityFunctionsAsync(database);
        await context.GetService<IMigrator>().MigrateAsync(Correction);
        var corrected = await ReadIdentityFunctionsAsync(database);
        await AssertAdmissionParentFailureAsync(database, corrected: true);

        await context.GetService<IMigrator>().MigrateAsync(Predecessor);
        var predecessor = await ReadIdentityFunctionsAsync(database);
        await AssertAdmissionParentFailureAsync(database, corrected: false);
        var previousGuard = predecessor.Except(corrected).Should().ContainSingle().Which;
        var correctedGuard = corrected.Except(predecessor).Should().ContainSingle().Which;
        previousGuard.Identity.Should().Be(Guard);
        correctedGuard.Identity.Should().Be(Guard);
        (correctedGuard with { Definition = previousGuard.Definition }).Should().Be(previousGuard,
            "the diagnostic correction retains function identity, ownership, ACL and the existing definer boundary");
        correctedGuard.Owner.Should().Be("learnstack_migration");
        correctedGuard.SecurityDefiner.Should().BeTrue();
        correctedGuard.Volatility.Should().Be("v");
        correctedGuard.Configuration.Should().Be("search_path=pg_catalog");
        previousGuard.Definition.Should().NotContain("membership_parent_scope");
        correctedGuard.Definition.Should().Contain("ERRCODE = '23514', CONSTRAINT = 'membership_parent_scope'")
            .And.Contain("IF eligible IS NOT TRUE THEN\n        RAISE EXCEPTION 'identity membership target is unavailable' USING ERRCODE = 'P0302';");

        await context.GetService<IMigrator>().MigrateAsync(Correction);
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(corrected);
        await AssertAdmissionParentFailureAsync(database, corrected: true);
        await context.GetService<IMigrator>().MigrateAsync(Predecessor);
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(predecessor);
        await AssertAdmissionParentFailureAsync(database, corrected: false);
        await context.GetService<IMigrator>().MigrateAsync(Correction);
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(corrected);
        await AssertAdmissionParentFailureAsync(database, corrected: true);
        await context.Database.MigrateAsync();
        (await ReadIdentityFunctionsAsync(database)).Should().Equal(fresh);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static async Task AssertAdmissionParentFailureAsync(DisposableSchemaDatabase database, bool corrected)
    {
        foreach (var parent in new[] { "missing", "hidden" })
        {
            var failure = await ParentMirrorStackFailureAsync(database.AppConnectionString, "assignment", parent);
            failure.SqlState.Should().Be(corrected ? "23514" : "P0302");
            failure.ConstraintName.Should().Be(corrected ? "membership_parent_scope" : null);
            failure.MessageText.Should().Be(corrected ? "identity graph is invalid" : "identity membership target is unavailable");
            failure.Detail.Should().BeNull();
        }
    }

    private static async Task AssertParentMirrorFailureAsync(DisposableSchemaDatabase database,
        string sqlState, string? constraint)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
        var write = () => ExecuteAsync(app, transaction,
            "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,organization_id,role_id) VALUES(@tenant,@member,@organization,@role)",
            ("tenant", SchemaFixture.TenantA), ("member", LearnerMembershipA), ("organization", SchemaFixture.OrgA1),
            ("role", RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Organization)));
        var failure = (await write.Should().ThrowAsync<PostgresException>()).Which;
        failure.SqlState.Should().Be(sqlState);
        failure.ConstraintName.Should().Be(constraint);
        failure.MessageText.Should().Be("identity graph is invalid");
        failure.Detail.Should().BeNull();
    }

    private static async Task<IReadOnlyList<IdentityFunction>> ReadIdentityFunctionsAsync(DisposableSchemaDatabase database)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var query = new NpgsqlCommand("""
            SELECT proc.oid::regprocedure::text, proc.oid::bigint, pg_get_userbyid(proc.proowner),
                proc.prosecdef, proc.provolatile::text, COALESCE(array_to_string(proc.proconfig, ','), ''),
                COALESCE(proc.proacl::text, ''), pg_get_functiondef(proc.oid)
            FROM pg_proc AS proc
            WHERE proc.pronamespace = 'public'::regnamespace AND proc.proname LIKE 'fn_identity_%'
            ORDER BY proc.oid::regprocedure::text
            """, owner);
        await using var reader = await query.ExecuteReaderAsync();
        var functions = new List<IdentityFunction>();
        while (await reader.ReadAsync())
            functions.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return functions;
    }

    private sealed record IdentityFunction(string Identity, long Oid, string Owner, bool SecurityDefiner,
        string Volatility, string Configuration, string Acl, string Definition);
}
