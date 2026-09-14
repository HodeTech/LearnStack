using System.Data.Common;
using FluentAssertions;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Independent database controls, exercised by authenticated application sessions.
/// Deliberate control removal and extra privileges live only in disposable databases.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class EducationIsolationTests(SchemaFixture schema)
{
    public static TheoryData<string> Tables => new(EducationSchemaSeed.Tables);
    public static TheoryData<string> Children => new(
        "lessons", "course_translations", "lesson_translations");

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Raw_Reads_Respect_Each_Tenant_Organization_And_Unresolved_Session(string table)
    {
        await using var app = await OpenAppAsync(schema.Postgres.AppConnectionString);
        foreach (var caller in EducationSchemaSeed.Scopes)
        {
            foreach (var hatch in new[] { false, true })
            {
                await using var transaction = await app.BeginTransactionAsync();
                await EducationSchemaSeed.AnnounceAsync(app, transaction,
                    caller.TenantId, caller.OrganizationId, hatch);
                var seen = await SchemaQueries.ReadStringsAsync(app,
                    $"SELECT tenant_id::text || '/' || COALESCE(organization_id::text, '-') FROM public.{table}",
                    transaction);
                var expected = EducationSchemaSeed.Scopes.Where(row =>
                        row.TenantId == caller.TenantId
                        && (hatch || row.OrganizationId is null || row.OrganizationId == caller.OrganizationId))
                    .Select(row => $"{row.TenantId}/{row.OrganizationId?.ToString() ?? "-"}");
                seen.Should().BeEquivalentTo(expected,
                    "the fixture has tenant-wide, own, sibling and foreign rows in every table");
            }
        }

        // Every announcement above was LOCAL. A new transaction without one must be
        // empty, including after the reporting hatch was used on this same connection.
        await using var unresolved = await app.BeginTransactionAsync();
        (await SchemaQueries.CountAsync(app, $"SELECT count(*) FROM public.{table}", unresolved))
            .Should().Be(0L);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Ignoring_Ef_Filters_Still_Reads_Only_The_Announced_Scope(string table)
    {
        await using var app = await OpenAppAsync(schema.Postgres.AppConnectionString);
        foreach (var caller in EducationSchemaSeed.Scopes.Where(row => row.OrganizationId is not null))
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, caller.TenantId, caller.OrganizationId);
            await using var context = new EducationDbContext(
                new DbContextOptionsBuilder<EducationDbContext>().UseNpgsql(app).Options,
                StaticTenantContextAccessor.Unresolved);
            await context.Database.UseTransactionAsync(transaction);
            var rows = table switch
            {
                "courses" => await ReadIgnoringFiltersAsync<Course>(context),
                "lessons" => await ReadIgnoringFiltersAsync<Lesson>(context),
                "course_translations" => await ReadIgnoringFiltersAsync<CourseTranslation>(context),
                _ => await ReadIgnoringFiltersAsync<LessonTranslation>(context),
            };
            rows.Select(row => $"{row.TenantId.Value}/{row.OrganizationId?.Value.ToString() ?? "-"}")
                .Should().BeEquivalentTo(EducationSchemaSeed.Scopes
                    .Where(row => row.TenantId == caller.TenantId
                        && (row.OrganizationId is null || row.OrganizationId == caller.OrganizationId))
                    .Select(row => $"{row.TenantId}/{row.OrganizationId?.ToString() ?? "-"}"));
        }
    }

    private static async Task<IOrganizationScoped[]> ReadIgnoringFiltersAsync<TEntity>(EducationDbContext context)
        where TEntity : class, IOrganizationScoped
    {
        (await context.Set<TEntity>().CountAsync()).Should().Be(0,
            "the unresolved EF context must filter out even the rows admitted by RLS");
        return (await context.Set<TEntity>().IgnoreQueryFilters().ToListAsync())
            .Cast<IOrganizationScoped>().ToArray();
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Insert_Policy_Independently_Requires_Exact_Tenant_And_Nullable_Organization(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await EducationSchemaSeed.SeedAsync(database.AppConnectionString);
        if (table != "courses")
        {
            await SetupAsync(database, $"DROP TRIGGER tg_{table}_parent_scope ON public.{table};");
        }

        await using var app = await OpenAppAsync(database.AppConnectionString);
        foreach (var callerOrganization in new Guid?[] { null, SchemaFixture.OrgA1 })
        {
            foreach (var hatch in new[] { false, true })
            {
                foreach (var row in EducationSchemaSeed.Scopes)
                {
                    await using var transaction = await app.BeginTransactionAsync();
                    await EducationSchemaSeed.AnnounceAsync(app, transaction,
                        SchemaFixture.TenantA, callerOrganization, hatch);
                    var id = Guid.NewGuid();
                    var insert = async () => await EducationSchemaSeed.InsertAsync(app, transaction, table,
                        row.TenantId, row.OrganizationId, id, ParentId(table, row), locale: "tr");
                    if (row.TenantId == SchemaFixture.TenantA && row.OrganizationId == callerOrganization)
                    {
                        await insert();
                        var key = table.EndsWith("_translations", StringComparison.Ordinal)
                            ? "locale = 'tr'" : $"id = '{id}'";
                        (await SchemaQueries.CountAsync(app,
                            $"SELECT count(*) FROM public.{table} WHERE {key}", transaction)).Should().Be(1L);
                    }
                    else
                    {
                        var refusal = (await insert.Should().ThrowAsync<PostgresException>()).Which;
                        refusal.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
                        refusal.MessageText.Should().Contain("row-level security",
                            "the parent trigger was removed; actual grants and FKs remain intact");
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Reporting_Reads_Never_Widen_Update_Or_Delete_Scope(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await EducationSchemaSeed.SeedAsync(database.AppConnectionString);
        await GrantMutationProofsAsync(database);
        await using var app = await OpenAppAsync(database.AppConnectionString);
        foreach (var hatch in new[] { false, true })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction,
                SchemaFixture.TenantA, SchemaFixture.OrgA1, hatch);
            var mine = EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA1);
            foreach (var other in EducationSchemaSeed.Scopes.Where(row => row != mine))
            {
                (await MutateAsync(app, transaction, table, other, delete: false)).Should().Be(0);
                (await MutateAsync(app, transaction, table, other, delete: true)).Should().Be(0);
            }

            (await MutateAsync(app, transaction, table, mine, delete: false)).Should().Be(1,
                "the same statement must reach and update a row inside its own scope");
            if (table == "courses")
            {
                await SchemaQueries.ExecuteAsync(app, transaction,
                    "DELETE FROM public.lessons WHERE course_id = @course", ("course", mine.CourseId));
            }

            (await MutateAsync(app, transaction, table, mine, delete: true)).Should().Be(1,
                "a real DELETE grant and an in-scope row distinguish policy denial from missing privileges");
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task Organization_Changes_Reach_And_Fail_The_Immutable_Guard(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await EducationSchemaSeed.SeedAsync(database.AppConnectionString);
        await GrantMutationProofsAsync(database);
        await using var app = await OpenAppAsync(database.AppConnectionString);
        foreach (var (before, after) in new (Guid?, Guid?)[]
        {
            (null, SchemaFixture.OrgA1), (SchemaFixture.OrgA1, null),
            (SchemaFixture.OrgA1, SchemaFixture.OrgA2),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, before);
            var row = EducationSchemaSeed.Find(SchemaFixture.TenantA, before);
            (await MutateAsync(app, transaction, table, row, delete: false)).Should().Be(1,
                "USING must admit the row before the trigger can be tested");
            var change = async () => await SchemaQueries.ExecuteAsync(app, transaction,
                $"UPDATE public.{table} SET organization_id = @after WHERE {RowKey(table)} = @id",
                ("after", after is { } value ? value : DBNull.Value), ("id", RowId(table, row)));
            var refusal = (await change.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Contain("organization_id is immutable after insert");
        }
    }

    [Theory]
    [MemberData(nameof(Children))]
    public async Task Parent_Check_Refuses_Missing_Hidden_And_Mismatched_Scopes_Uniformly(string table)
    {
        await using var app = await OpenAppAsync(schema.Postgres.AppConnectionString);
        var own = EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA1);
        var wide = EducationSchemaSeed.Find(SchemaFixture.TenantA, null);
        var sibling = EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA2);
        var foreign = EducationSchemaSeed.Find(SchemaFixture.TenantB, null);
        foreach (var (callerOrganization, rowOrganization, parentId) in new (Guid?, Guid?, Guid)[]
        {
            (SchemaFixture.OrgA1, SchemaFixture.OrgA1, ParentId(table, wide)),
            (null, null, ParentId(table, own)),
            (SchemaFixture.OrgA1, SchemaFixture.OrgA1, ParentId(table, sibling)),
            (SchemaFixture.OrgA1, SchemaFixture.OrgA1, ParentId(table, foreign)),
            (SchemaFixture.OrgA1, SchemaFixture.OrgA1, Guid.NewGuid()),
            (SchemaFixture.OrgA1, null, ParentId(table, own)),
        })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, callerOrganization);
            var insert = async () => await EducationSchemaSeed.InsertAsync(app, transaction, table,
                SchemaFixture.TenantA, rowOrganization, Guid.NewGuid(), parentId, locale: "tr");
            var refusal = (await insert.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Be($"parent scope is unavailable or mismatched (table {table})");
        }

        foreach (var parent in new[] { own, wide })
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, parent.TenantId, parent.OrganizationId);
            await EducationSchemaSeed.InsertAsync(app, transaction, table,
                parent.TenantId, parent.OrganizationId, Guid.NewGuid(), ParentId(table, parent), locale: "tr");
        }
    }

    [Theory]
    [MemberData(nameof(Children))]
    public async Task Tenant_Composite_Foreign_Key_Refuses_A_Foreign_Parent_Independently(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await EducationSchemaSeed.SeedAsync(database.AppConnectionString);
        await SetupAsync(database, $"DROP TRIGGER tg_{table}_parent_scope ON public.{table};");
        await using var app = await OpenAppAsync(database.AppConnectionString);
        var own = EducationSchemaSeed.Find(SchemaFixture.TenantA, null);
        var foreign = EducationSchemaSeed.Find(SchemaFixture.TenantB, null);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, own.TenantId, null);
        await EducationSchemaSeed.InsertAsync(app, transaction, table,
            own.TenantId, null, Guid.NewGuid(), ParentId(table, own), locale: "tr");
        var insert = async () => await EducationSchemaSeed.InsertAsync(app, transaction, table,
            own.TenantId, null, Guid.NewGuid(), ParentId(table, foreign), locale: "tr");
        var refusal = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        refusal.ConstraintName.Should().Be(table switch
        {
            "lessons" => "fk_lessons_course",
            "course_translations" => "fk_course_translations_course",
            _ => "fk_lesson_translations_lesson",
        });
    }

    [Theory]
    [MemberData(nameof(Children))]
    public async Task Parent_Check_Cannot_Be_Shadowed_By_A_Temporary_Table(string table)
    {
        // The production database also denies TEMP. A disposable database with an
        // explicit test-only grant lets this proof reach schema qualification itself.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await EducationSchemaSeed.SeedAsync(database.AppConnectionString);
        var name = new NpgsqlConnectionStringBuilder(database.MigrationConnectionString).Database;
        await SetupAsync(database, $"GRANT TEMPORARY ON DATABASE \"{name}\" TO learnstack_app;");
        await using var app = await OpenAppAsync(database.AppConnectionString);
        var parentTable = table == "lesson_translations" ? "lessons" : "courses";
        var parent = EducationSchemaSeed.Find(SchemaFixture.TenantA, null);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await SchemaQueries.ExecuteAsync(app, transaction,
            $"CREATE TEMP TABLE {parentTable} (id uuid, tenant_id uuid, organization_id uuid) ON COMMIT DROP; "
            + $"INSERT INTO {parentTable} VALUES (@id, @tenant, @organization);",
            ("id", ParentId(table, parent)), ("tenant", SchemaFixture.TenantA), ("organization", SchemaFixture.OrgA1));
        var insert = async () => await EducationSchemaSeed.InsertAsync(app, transaction, table,
            SchemaFixture.TenantA, SchemaFixture.OrgA1, Guid.NewGuid(), ParentId(table, parent), locale: "tr");
        var refusal = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        refusal.MessageText.Should().Be($"parent scope is unavailable or mismatched (table {table})",
            "the real public parent is tenant-wide; the counterfeit temporary parent must be ignored");
    }

    [Fact]
    public async Task Education_Has_No_Unapproved_Column_Or_Public_Privileges()
    {
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        var offenders = await ColumnPrivilegeOffendersAsync(owner);
        offenders.Should().BeEmpty("a column grant must not widen the table grant matrix");
        (await SchemaQueries.CountAsync(owner,
            """
            SELECT count(*) FROM information_schema.column_privileges
            WHERE table_schema = 'public' AND grantee = 'learnstack_app' AND privilege_type = 'INSERT'
              AND table_name IN ('courses', 'lessons', 'course_translations', 'lesson_translations')
            """)).Should().BeGreaterThan(0, "the privilege inspection must see real granted columns");
        (await SchemaQueries.CountAsync(owner,
            """
            SELECT count(*) FROM pg_class AS relation
            CROSS JOIN LATERAL aclexplode(relation.relacl) AS privilege
            WHERE relation.relnamespace = 'public'::regnamespace
              AND relation.relname IN ('courses', 'lessons', 'course_translations', 'lesson_translations')
              AND privilege.grantee = 0
            """)).Should().Be(0, "PUBLIC receives no table privilege, including TRUNCATE or REFERENCES");
    }

    [Theory]
    [InlineData("learnstack_platform", "courses", "slug_key")]
    [InlineData("learnstack_app", "course_translations", "title")]
    [InlineData("PUBLIC", "lesson_translations", "title")]
    public async Task The_Privilege_Check_Detects_A_Column_Grant_Hidden_From_The_Table_Matrix(
        string role, string table, string column)
    {
        await using var owner = await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"GRANT UPDATE ({column}) ON public.{table} TO {role};");
        (await ColumnPrivilegeOffendersAsync(owner, transaction)).Should().Equal($"{role}/{table}/{column}/UPDATE");
        await SchemaQueries.ExecuteAsync(owner, transaction,
            $"REVOKE UPDATE ({column}) ON public.{table} FROM {role};");
        (await ColumnPrivilegeOffendersAsync(owner, transaction)).Should().BeEmpty();
    }

    private static Task<List<string>> ColumnPrivilegeOffendersAsync(DbConnection owner, DbTransaction? transaction = null) =>
        SchemaQueries.ReadStringsAsync(owner,
            """
            SELECT grantee || '/' || table_name || '/' || column_name || '/' || privilege_type
            FROM information_schema.column_privileges
            WHERE table_schema = 'public'
              AND table_name IN ('courses', 'lessons', 'course_translations', 'lesson_translations')
              AND grantee <> 'learnstack_migration'
              AND NOT (
                (grantee = 'learnstack_platform' AND privilege_type = 'SELECT')
                OR (grantee = 'learnstack_app' AND (
                  privilege_type IN ('SELECT', 'INSERT')
                  OR (table_name IN ('courses', 'lessons') AND privilege_type = 'UPDATE'))))
            """, transaction);

    private static async Task<DbConnection> OpenAppAsync(string connectionString)
    {
        var app = await PostgresFixture.OpenAsync(connectionString);
        try
        {
            var roles = await SchemaQueries.ReadStringsAsync(app,
                "SELECT session_user || '/' || current_user FROM pg_roles "
                + "WHERE rolname = current_user AND NOT rolsuper AND NOT rolbypassrls");
            roles.Should().Equal("learnstack_app/learnstack_app");
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task SetupAsync(DisposableSchemaDatabase database, string sql)
    {
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null, sql);
    }

    private static Task GrantMutationProofsAsync(DisposableSchemaDatabase database) => SetupAsync(database,
        "GRANT UPDATE, DELETE ON public.courses, public.lessons, "
        + "public.course_translations, public.lesson_translations TO learnstack_app;");

    private static string RowKey(string table) => table switch
    {
        "course_translations" => "course_id",
        "lesson_translations" => "lesson_id",
        _ => "id",
    };

    private static Guid RowId(string table, EducationSchemaSeed.Scope row) =>
        table is "courses" or "course_translations" ? row.CourseId : row.LessonId;

    private static Guid ParentId(string table, EducationSchemaSeed.Scope row) =>
        table == "lesson_translations" ? row.LessonId : row.CourseId;

    private static async Task<int> MutateAsync(DbConnection app, DbTransaction transaction,
        string table, EducationSchemaSeed.Scope row, bool delete)
    {
        var mutation = delete ? "DELETE FROM" : "UPDATE";
        var set = delete ? "" : table is "courses" or "lessons"
            ? "SET status = 'published'" : "SET title = 'Changed title'";
        await using var command = new NpgsqlCommand(
            $"{mutation} public.{table} {set} WHERE tenant_id = @tenant AND {RowKey(table)} = @id",
            (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("tenant", row.TenantId);
        command.Parameters.AddWithValue("id", RowId(table, row));
        return await command.ExecuteNonQueryAsync();
    }
}
