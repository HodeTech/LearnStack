using System.Data.Common;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0003 Amendment 6 and Standards 05/08: these inspect the applied schema as
/// owner. The independent Education behavioral suite authenticates as learnstack_app.
/// Every mutation below rolls back on the shared schema.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class EducationStructureTests
{
    private readonly SchemaFixture _schema;

    public EducationStructureTests(SchemaFixture schema) => _schema = schema;

    /// <summary>Standards 21's canonical rule; ADR-0003 Amendment 6 defines its subjects.</summary>
    [Fact]
    public async Task Every_Organization_Mirroring_Child_Has_A_Parent_Scope_Guard()
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        (await ParentGuardOffendersAsync(owner)).Should().BeEmpty(
            "Fix: classify every model/applied organization-scoped relation and attach the "
            + "unconditional invoker BEFORE INSERT OR UPDATE parent guard from Standards 05");
    }

    /// <summary>Standards 21's canonical rule; ADR-0008 and the Education spec define Pattern A.</summary>
    [Fact]
    public async Task Pattern_A_Content_Uses_Translation_Satellites()
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        (await PatternOffendersAsync(owner)).Should().BeEmpty(
            "Fix: keep localized content on plain, independently scoped satellites with a "
            + "(parent, locale) key, flat (tenant, locale, slug) uniqueness and CASCADE parent FK");
    }

    [Theory]
    [InlineData("lessons")]
    [InlineData("course_translations")]
    [InlineData("lesson_translations")]
    public async Task The_Parent_Guard_Rejects_Each_Missing_Trigger_And_Relation(string table)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var trigger = await DefinitionAsync(owner, transaction,
            $"SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'tg_{table}_parent_scope'");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"DROP TRIGGER tg_{table}_parent_scope ON {table}");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().Contain($"{table}: parent guard");
        await SchemaQueries.ExecuteAsync(owner, transaction, trigger);
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

        var relation = ParentRelations.Single(relation => relation.Child == table);
        var foreignKey = (await ReadForeignKeysAsync(owner, transaction))
            .Single(key => key.Child == table && key.Parent == relation.Parent);
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"ALTER TABLE {table} DROP CONSTRAINT {foreignKey.Name}");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().Contain($"{table}: missing parent FK");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"ALTER TABLE {table} ADD CONSTRAINT {foreignKey.Name} {foreignKey.Definition}");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();

        (await ParentGuardOffendersAsync(owner, transaction,
            ParentRelations.Where(item => item.Child != table).ToArray()))
            .Should().Contain($"{table}: undeclared model relation");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_Parent_Guard_Rejects_A_New_Unclassified_Relation()
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction,
            """
            CREATE TABLE parent_scope_probe (
                id uuid PRIMARY KEY, tenant_id uuid NOT NULL, organization_id uuid,
                course_id uuid NOT NULL,
                FOREIGN KEY (tenant_id, course_id) REFERENCES courses (tenant_id, id));
            """);
        (await ParentGuardOffendersAsync(owner, transaction))
            .Should().Contain("parent_scope_probe: undeclared applied relation");
        await SchemaQueries.ExecuteAsync(owner, transaction, "DROP TABLE parent_scope_probe");
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("after")]
    [InlineData("insert-only")]
    [InlineData("update-only")]
    [InlineData("statement")]
    [InlineData("conditional")]
    [InlineData("column-restricted")]
    public async Task The_Parent_Guard_Rejects_Inert_Trigger_Metadata(string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var original = await DefinitionAsync(owner, transaction,
            "SELECT pg_get_triggerdef(oid) FROM pg_trigger WHERE tgname = 'tg_lessons_parent_scope'");
        await SchemaQueries.ExecuteAsync(owner, transaction, "DROP TRIGGER tg_lessons_parent_scope ON lessons");
        if (defect != "missing")
        {
            var timing = defect == "after" ? "AFTER" : "BEFORE";
            var events = defect switch
            {
                "insert-only" => "INSERT",
                "update-only" => "UPDATE",
                "column-restricted" => "INSERT OR UPDATE OF course_id",
                _ => "INSERT OR UPDATE",
            };
            var level = defect == "statement" ? "STATEMENT" : "ROW";
            var condition = defect == "conditional" ? " WHEN (NEW.organization_id IS NOT NULL)" : "";
            await SchemaQueries.ExecuteAsync(owner, transaction,
                $"CREATE TRIGGER tg_lessons_parent_scope {timing} {events} ON lessons "
                + $"FOR EACH {level}{condition} EXECUTE FUNCTION public.fn_lessons_parent_scope()");
            if (defect == "disabled")
            {
                await SchemaQueries.ExecuteAsync(owner, transaction,
                    "ALTER TABLE lessons DISABLE TRIGGER tg_lessons_parent_scope");
            }
        }

        (await ParentGuardOffendersAsync(owner, transaction)).Should()
            .ContainSingle().Which.Should().Be("lessons: parent guard");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            "DROP TRIGGER IF EXISTS tg_lessons_parent_scope ON lessons; " + original);
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("security-definer")]
    [InlineData("search-path")]
    [InlineData("unqualified-parent")]
    [InlineData("no-lock")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-parent")]
    [InlineData("nullable-compare")]
    [InlineData("missing-parent-accepted")]
    [InlineData("wrong-sqlstate")]
    [InlineData("early-return")]
    public async Task The_Parent_Guard_Rejects_A_Weakened_Applied_Function(string defect)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var original = await DefinitionAsync(owner, transaction,
            "SELECT pg_get_functiondef('public.fn_lessons_parent_scope()'::regprocedure)");
        var body = await DefinitionAsync(owner, transaction,
            "SELECT prosrc FROM pg_proc WHERE oid = 'public.fn_lessons_parent_scope()'::regprocedure");
        var mutated = defect switch
        {
            "unqualified-parent" => body.Replace("public.courses", "courses", StringComparison.Ordinal),
            "no-lock" => body.Replace("FOR KEY SHARE", "", StringComparison.Ordinal),
            "wrong-tenant" => body.Replace("parent.tenant_id = NEW.tenant_id", "parent.tenant_id = parent.tenant_id", StringComparison.Ordinal),
            "wrong-parent" => body.Replace("parent.id = NEW.course_id", "parent.id = NEW.id", StringComparison.Ordinal),
            "nullable-compare" => body.Replace("IS DISTINCT FROM", "<>", StringComparison.Ordinal),
            "missing-parent-accepted" => body.Replace("NOT FOUND OR ", "", StringComparison.Ordinal),
            "wrong-sqlstate" => body.Replace("23514", "23503", StringComparison.Ordinal),
            "early-return" => body.Replace("BEGIN", "BEGIN RETURN NEW;", StringComparison.Ordinal),
            _ => body,
        };
        if (defect is not ("security-definer" or "search-path"))
        {
            mutated.Should().NotBe(body, "the planted violation must actually change the applied function");
        }

        var security = defect == "security-definer" ? "DEFINER" : "INVOKER";
        var path = defect == "search-path" ? "public, pg_catalog" : "pg_catalog";
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"CREATE OR REPLACE FUNCTION public.fn_lessons_parent_scope() RETURNS trigger "
            + $"LANGUAGE plpgsql SECURITY {security} SET search_path = {path} AS $probe${mutated}$probe$");
        (await ParentGuardOffendersAsync(owner, transaction)).Should()
            .ContainSingle().Which.Should().Be("lessons: parent guard");
        await SchemaQueries.ExecuteAsync(owner, transaction, original);
        (await ParentGuardOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("courses", "title")]
    [InlineData("courses", "summary")]
    [InlineData("courses", "description")]
    [InlineData("courses", "description_en")]
    [InlineData("lessons", "seo_description")]
    [InlineData("lessons", "body")]
    [InlineData("courses", "title_en")]
    [InlineData("lesson_translations", "title_en")]
    [InlineData("course_translations", "id")]
    [InlineData("course_translations", "deleted_at")]
    [InlineData("lesson_translations", "row_version")]
    public async Task The_Pattern_A_Guard_Rejects_Misplaced_Applied_Columns(string table, string column)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE {table} ADD COLUMN {column} text");
        (await PatternOffendersAsync(owner, transaction)).Should().Contain($"{table}: misplaced columns");
        await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE {table} DROP COLUMN {column}");
        (await PatternOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("course_translations", "organization_id")]
    [InlineData("course_translations", "course_id")]
    [InlineData("lesson_translations", "organization_id")]
    [InlineData("lesson_translations", "lesson_id")]
    public async Task The_Pattern_A_Guard_Rejects_A_Widened_Slug_Namespace(string table, string widening)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        var constraint = await DefinitionAsync(owner, transaction,
            $"SELECT conname FROM pg_constraint WHERE conrelid = '{table}'::regclass "
            + "AND contype = 'u' AND pg_get_constraintdef(oid) = 'UNIQUE (tenant_id, locale, slug)'");
        var original = await DefinitionAsync(owner, transaction,
            $"SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = '{table}'::regclass "
            + $"AND conname = '{constraint}'");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"ALTER TABLE {table} DROP CONSTRAINT {constraint}; "
            + $"ALTER TABLE {table} ADD CONSTRAINT {constraint} UNIQUE (tenant_id, locale, slug, {widening})");
        (await PatternOffendersAsync(owner, transaction)).Should().Contain($"{table}: slug namespace");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"ALTER TABLE {table} DROP CONSTRAINT {constraint}; "
            + $"ALTER TABLE {table} ADD CONSTRAINT {constraint} {original}");
        (await PatternOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("course_translations")]
    [InlineData("lesson_translations")]
    public async Task The_Pattern_A_Guard_Rejects_A_Missing_Satellite(string table)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE {table} RENAME TO missing_satellite_probe");
        (await PatternOffendersAsync(owner, transaction)).Should().Contain($"{table}: missing table");
        await SchemaQueries.ExecuteAsync(owner, transaction, $"ALTER TABLE missing_satellite_probe RENAME TO {table}");
        (await PatternOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    private static async Task<string> DefinitionAsync(
        DbConnection connection, DbTransaction transaction, string sql) =>
        (await SchemaQueries.ReadStringsAsync(connection, sql, transaction)).Single();
}
