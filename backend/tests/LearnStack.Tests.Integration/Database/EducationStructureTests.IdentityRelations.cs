using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class EducationStructureTests
{
    [Theory]
    [InlineData("membership_profiles")]
    [InlineData("membership_role_assignments")]
    public async Task The_Identity_Parent_Guard_Requires_Both_Model_And_Applied_Relations(string table)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

        var relation = IdentityParentRelations.Single(item => item.Child == table);
        var key = (await ReadForeignKeysAsync(owner, transaction))
            .Single(item => item.Child == table && item.Parent == relation.Parent);
        await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE {table} DROP CONSTRAINT {key.Name}");
        (await ParentGuardOffendersAsync(owner, transaction)).Should()
            .ContainSingle().Which.Should().Be($"{table}: missing parent FK");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"ALTER TABLE {table} ADD CONSTRAINT {key.Name} {key.Definition}");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

        foreach (var definition in new[]
        {
            "FOREIGN KEY (membership_id) REFERENCES memberships(id) ON DELETE RESTRICT",
            key.Definition.Replace("ON DELETE RESTRICT", "ON DELETE CASCADE", StringComparison.Ordinal),
            key.Definition + " DEFERRABLE INITIALLY IMMEDIATE",
            key.Definition + " NOT VALID",
        })
        {
            definition.Should().NotBe(key.Definition);
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"ALTER TABLE {table} DROP CONSTRAINT {key.Name}; "
                + $"ALTER TABLE {table} ADD CONSTRAINT {key.Name} {definition}");
            (await ParentGuardOffendersAsync(owner, transaction)).Should()
                .ContainSingle().Which.Should().Be($"{table}: tenant-composite FK");
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"ALTER TABLE {table} DROP CONSTRAINT {key.Name}; "
                + $"ALTER TABLE {table} ADD CONSTRAINT {key.Name} {key.Definition}");
            (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
        }

        (await ParentGuardOffendersAsync(owner, transaction,
            ParentRelations.Where(item => item.Child != table).ToArray())).Should().BeEquivalentTo(
                $"{table}: undeclared model relation", $"{table}: undeclared applied relation");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

        using var education = ModelOnly();
        using var identity = IdentityModelOnly();
        var models = ParentModelRelations(education.Model, identity.Model);
        models.Count(item => item.DeclaringEntityType.GetTableName() == table).Should().Be(1);
        (await ParentGuardOffendersAsync(owner, transaction,
            modelRelations: models.Where(item => item.DeclaringEntityType.GetTableName() != table).ToList()))
            .Should().ContainSingle().Which.Should().Be($"{table}: missing model relation");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("membership_profiles", "missing")]
    [InlineData("membership_profiles", "disabled")]
    [InlineData("membership_profiles", "after")]
    [InlineData("membership_role_assignments", "missing")]
    [InlineData("membership_role_assignments", "disabled")]
    [InlineData("membership_role_assignments", "after")]
    public async Task The_Identity_Parent_Guard_Rejects_Inert_Trigger_Metadata(string table, string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
        var name = $"tg_{table}_mutation_guard";
        var original = await DefinitionAsync(owner, transaction,
            $"SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = '{name}'");
        await SchemaQueries.ExecuteAsync(owner, transaction, $"DROP TRIGGER {name} ON {table}");
        if (defect != "missing")
        {
            var timing = defect == "after" ? "AFTER" : "BEFORE";
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"CREATE TRIGGER {name} {timing} INSERT OR UPDATE OR DELETE ON {table} "
                + "FOR EACH ROW EXECUTE FUNCTION public.fn_identity_contextual_mutation_guard()");
            if (defect == "disabled")
                await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE {table} DISABLE TRIGGER {name}");
        }
        (await ParentGuardOffendersAsync(owner, transaction)).Should()
            .ContainSingle().Which.Should().Be($"{table}: parent guard");
        await SchemaQueries.ExecuteAsync(owner, transaction, $"DROP TRIGGER IF EXISTS {name} ON {table}; " + original);
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("security-definer")]
    [InlineData("search-path")]
    [InlineData("stable")]
    [InlineData("unqualified-parent")]
    [InlineData("no-lock")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-parent")]
    [InlineData("nullable-compare")]
    [InlineData("missing-parent-accepted")]
    [InlineData("wrong-sqlstate")]
    [InlineData("wrong-constraint")]
    [InlineData("missing-constraint")]
    [InlineData("wrong-branch")]
    [InlineData("early-return")]
    public async Task The_Identity_Parent_Guard_Rejects_A_Weakened_Applied_Function(string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
        var original = await DefinitionAsync(owner, transaction,
            "SELECT pg_get_functiondef('public.fn_identity_contextual_mutation_guard()'::regprocedure)");
        var body = await DefinitionAsync(owner, transaction,
            "SELECT prosrc FROM pg_proc WHERE oid = 'public.fn_identity_contextual_mutation_guard()'::regprocedure");
        var mutated = defect switch
        {
            "unqualified-parent" => body.Replace("public.memberships AS member", "memberships AS member", StringComparison.Ordinal),
            "no-lock" => body.Replace("FOR KEY SHARE", "", StringComparison.Ordinal),
            "wrong-tenant" => body.Replace("member.tenant_id = target_tenant", "member.tenant_id = member.tenant_id", StringComparison.Ordinal),
            "wrong-parent" => body.Replace("member.id = target_member", "member.id = member.id", StringComparison.Ordinal),
            "nullable-compare" => body.Replace("parent_org IS DISTINCT FROM", "parent_org <>", StringComparison.Ordinal),
            "missing-parent-accepted" => body.Replace("NOT FOUND OR parent_org", "parent_org", StringComparison.Ordinal),
            "wrong-sqlstate" => body.Replace("ERRCODE = '23514'", "ERRCODE = 'P0304'", StringComparison.Ordinal),
            "wrong-constraint" => body.Replace("CONSTRAINT = 'membership_parent_scope'", "CONSTRAINT = 'unclassified_parent_scope'", StringComparison.Ordinal),
            "missing-constraint" => body.Replace(", CONSTRAINT = 'membership_parent_scope'", "", StringComparison.Ordinal),
            "wrong-branch" => body.Replace("ELSIF TG_TABLE_NAME IN ('membership_profiles','membership_role_assignments')",
                "ELSIF TG_TABLE_NAME IN ('membership_profiles')", StringComparison.Ordinal),
            "early-return" => body.Replace("BEGIN", "BEGIN RETURN NEW;", StringComparison.Ordinal),
            _ => body,
        };
        if (defect is not ("security-definer" or "search-path" or "stable"))
            mutated.Should().NotBe(body, "the planted violation must change the applied function");
        var security = defect == "security-definer" ? "DEFINER" : "INVOKER";
        var path = defect == "search-path" ? "public, pg_catalog" : "pg_catalog";
        var volatility = defect == "stable" ? "STABLE" : "VOLATILE";
        await SchemaQueries.ExecuteAsync(owner, transaction,
            "CREATE OR REPLACE FUNCTION public.fn_identity_contextual_mutation_guard() RETURNS trigger "
            + $"LANGUAGE plpgsql {volatility} SECURITY {security} SET search_path = {path} AS $probe${mutated}$probe$");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEquivalentTo(
            "membership_profiles: parent guard", "membership_role_assignments: parent guard");
        await SchemaQueries.ExecuteAsync(owner, transaction, original);
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
    }
}
