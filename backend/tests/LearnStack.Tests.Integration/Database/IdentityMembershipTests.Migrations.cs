using FluentAssertions;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
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
        await using var database = await CreateHistoricalIdentityDatabaseAsync(Predecessor);
        await SeedHistoricalIdentityGraphAsync(database);
        await using var context = HistoricalIdentityMigration(database);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var predecessor = await ReadIdentityFunctionsAsync(database);
        predecessor.Should().HaveCountGreaterThan(1, "the replacement must preserve actual sibling functions");
        await AssertParentMirrorFailureAsync(database, "P0304", null);

        await context.GetService<IMigrator>().MigrateAsync(Correction);
        var corrected = await ReadIdentityFunctionsAsync(database);
        await AssertParentMirrorFailureAsync(database, "23514", "membership_parent_scope");
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
        await MigrationChains.ApplyAllAsync(database.MigrationConnectionString);
        await AssertFreshIdentityChainAsync(database);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task Admission_parent_correction_preserves_its_definer_boundary_and_converges_with_the_fresh_chain()
    {
        const string Predecessor = "20261026035353_correct_identity_parent_mirror_sqlstate";
        const string Correction = "20261026043921_correct_identity_admission_parent_sqlstate";
        const string Guard = "fn_identity_membership_admission_guard()";
        await using var database = await CreateHistoricalIdentityDatabaseAsync(Predecessor);
        await SeedHistoricalIdentityGraphAsync(database);
        await using var context = HistoricalIdentityMigration(database);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var predecessor = await ReadIdentityFunctionsAsync(database);
        await AssertAdmissionParentFailureAsync(database, corrected: false);

        await context.GetService<IMigrator>().MigrateAsync(Correction);
        var corrected = await ReadIdentityFunctionsAsync(database);
        await AssertAdmissionParentFailureAsync(database, corrected: true);
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
        await MigrationChains.ApplyAllAsync(database.MigrationConnectionString);
        await AssertFreshIdentityChainAsync(database);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private async Task<DisposableSchemaDatabase> CreateHistoricalIdentityDatabaseAsync(string predecessor)
    {
        var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres, applyMigrations: false);
        try
        {
            // Only Identity's prerequisites precede the historical target. The
            // canonical inventory still owns installation of every complete chain.
            await MigrationChains.ApplyTenancyAsync(database.MigrationConnectionString);
            await using (var audit = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
                .UseNpgsql(database.MigrationConnectionString, options =>
                    options.MigrationsHistoryTable(AuditDbContextFactory.HistoryTable)).Options,
                StaticTenantContextAccessor.Unresolved))
                await audit.Database.MigrateAsync();
            await using var identity = HistoricalIdentityMigration(database);
            await identity.GetService<IMigrator>().MigrateAsync(predecessor);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static IdentityDbContext HistoricalIdentityMigration(DisposableSchemaDatabase database) => new(
        new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(database.MigrationConnectionString, options =>
            options.MigrationsHistoryTable(IdentityDbContextFactory.HistoryTable)).Options,
        StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);

    private static async Task SeedHistoricalIdentityGraphAsync(DisposableSchemaDatabase database)
    {
        // The current installer also owns the later creation-capacity table.
        // This old-schema fixture installs only its then-existing immutable anchor.
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
            await ExecuteAsync(owner, null, """
                INSERT INTO public.identity_authority(id,authority_id,issuer) VALUES(1,@authority,@issuer)
                """, ("authority", Authority.AuthorityId.Value), ("issuer", Authority.Issuer));
        foreach (var actor in new[] { AdminA, PersonA, AdminB, PersonB }) await InsertUserAsync(database.AppConnectionString, actor);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        foreach (var tenant in new[] { SchemaFixture.TenantA, SchemaFixture.TenantB })
            foreach (var template in IdentityFoundationManifest.Version1.Templates)
            {
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, tenant);
                await LockAsync(app, transaction, tenant, null, false);
                var role = RoleId(tenant, template.BuiltInKey, template.Scope);
                await ExecuteAsync(app, transaction, """
                INSERT INTO public.roles(id,tenant_id,built_in_key,permission_scope,created_at,created_by)
                VALUES(@id,@tenant,@key,@scope,clock_timestamp(),@actor)
                """, ("id", role), ("tenant", tenant), ("key", template.BuiltInKey.ToString()),
                    ("scope", template.Scope.ToString()), ("actor", UserId.SystemActor.Value));
                foreach (var permission in template.Grants)
                    await ExecuteAsync(app, transaction, """
                    INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                    VALUES(@tenant,@role,@scope,@key)
                    """, ("tenant", tenant), ("role", role), ("scope", permission.Scope.ToString()), ("key", permission.Key));
                await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
                await transaction.CommitAsync();
            }
        foreach (var target in new (Guid Tenant, Guid Member, Guid Actor, Guid? Organization, BuiltInRoleKey Role)[]
        {
            (SchemaFixture.TenantA, AdminMembershipA, AdminA, null, BuiltInRoleKey.TenantAdmin),
            (SchemaFixture.TenantA, LearnerMembershipA, PersonA, null, BuiltInRoleKey.Learner),
            (SchemaFixture.TenantA, BranchMembershipA1, PersonA, SchemaFixture.OrgA1, BuiltInRoleKey.Learner),
            (SchemaFixture.TenantA, BranchMembershipA2, PersonA, SchemaFixture.OrgA2, BuiltInRoleKey.Learner),
            (SchemaFixture.TenantB, AdminMembershipB, AdminB, null, BuiltInRoleKey.TenantAdmin),
            (SchemaFixture.TenantB, BranchMembershipB, PersonB, SchemaFixture.OrgB1, BuiltInRoleKey.Learner),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, target.Tenant, target.Organization);
            await LockAsync(app, transaction, target.Tenant, target.Actor, target.Role == BuiltInRoleKey.TenantAdmin);
            await InsertMemberAsync(app, transaction, target.Tenant, target.Organization, target.Member, target.Actor,
                RoleId(target.Tenant, target.Role, target.Organization is null ? PermissionScope.Tenant : PermissionScope.Organization));
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }
    }

    private async Task AssertFreshIdentityChainAsync(DisposableSchemaDatabase database)
    {
        await using var fresh = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        var expected = await ReadIdentityFunctionsAsync(fresh);
        var actual = await ReadIdentityFunctionsAsync(database);
        // OIDs are database-local. All function bodies, identities, owners, ACLs,
        // execution modes, volatility and search paths must converge exactly.
        actual.Select(function => function with { Oid = 0 }).Should()
            .Equal(expected.Select(function => function with { Oid = 0 }));
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaTableClasses.AssertCompleteAsync(owner);
    }

    private static async Task AssertParentLifecycleCorrectionAsync(DisposableSchemaDatabase database,
        IdentityDbContext migration, IReadOnlyList<IdentityFunction> predecessor)
    {
        await migration.GetService<IMigrator>().MigrateAsync("20261026102944_serialize_identity_parent_lifecycle");
        var corrected = await ReadIdentityFunctionsAsync(database);
        var currentGuard = corrected.Except(predecessor).Should().ContainSingle().Which;
        var previousGuard = predecessor.Except(corrected).Should().ContainSingle().Which;
        currentGuard.Identity.Should().Be("fn_identity_contextual_mutation_guard()");
        (currentGuard with { Definition = previousGuard.Definition }).Should().Be(previousGuard,
            "the forward replacement preserves function OID, identity, owner, ACL, volatility and execution mode");
        currentGuard.Definition.Should().Be(previousGuard.Definition.Replace(
            "target_member FOR KEY SHARE;", "target_member FOR NO KEY UPDATE;", StringComparison.Ordinal));
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
