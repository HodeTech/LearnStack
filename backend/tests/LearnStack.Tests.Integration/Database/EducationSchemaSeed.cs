using System.Data.Common;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Populated positive controls for all four Education tables. These are schema
/// fixtures, independent of the command-driven production seed in P02d-2.
/// </summary>
internal static class EducationSchemaSeed
{
    internal static readonly string[] Tables =
        ["courses", "lessons", "course_translations", "lesson_translations"];

    internal static readonly Scope[] Scopes =
    [
        new(SchemaFixture.TenantA, null,
            Guid.Parse("c0000001-0000-7000-8000-000000000001"),
            Guid.Parse("d0000001-0000-7000-8000-000000000001"), "alpha-wide"),
        new(SchemaFixture.TenantA, SchemaFixture.OrgA1,
            Guid.Parse("c0000002-0000-7000-8000-000000000002"),
            Guid.Parse("d0000002-0000-7000-8000-000000000002"), "alpha-main"),
        new(SchemaFixture.TenantA, SchemaFixture.OrgA2,
            Guid.Parse("c0000003-0000-7000-8000-000000000003"),
            Guid.Parse("d0000003-0000-7000-8000-000000000003"), "alpha-branch"),
        new(SchemaFixture.TenantB, null,
            Guid.Parse("c0000004-0000-7000-8000-000000000004"),
            Guid.Parse("d0000004-0000-7000-8000-000000000004"), "beta-wide"),
        new(SchemaFixture.TenantB, SchemaFixture.OrgB1,
            Guid.Parse("c0000005-0000-7000-8000-000000000005"),
            Guid.Parse("d0000005-0000-7000-8000-000000000005"), "beta-main"),
    ];

    internal sealed record Scope(
        Guid TenantId, Guid? OrganizationId, Guid CourseId, Guid LessonId, string Slug);

    internal static Scope Find(Guid tenantId, Guid? organizationId) =>
        Scopes.Single(scope => scope.TenantId == tenantId && scope.OrganizationId == organizationId);

    internal static async Task SeedAsync(string appConnectionString)
    {
        await using var app = await PostgresFixture.OpenAsync(appConnectionString);
        foreach (var scope in Scopes)
        {
            await using var transaction = await app.BeginTransactionAsync();
            await AnnounceAsync(app, transaction, scope.TenantId, scope.OrganizationId);
            foreach (var table in Tables)
            {
                await InsertAsync(app, transaction, table, scope.TenantId, scope.OrganizationId,
                    table == "courses" ? scope.CourseId : scope.LessonId,
                    table == "lesson_translations" ? scope.LessonId : scope.CourseId,
                    scope.Slug);
            }

            await transaction.CommitAsync();
        }
    }

    internal static async Task AnnounceAsync(
        DbConnection connection, DbTransaction transaction, Guid tenantId, Guid? organizationId,
        bool tenantReadHatch = false)
    {
        await SchemaQueries.SetTenantAsync(connection, transaction, tenantId);
        await SchemaQueries.SetSettingAsync(connection, transaction,
            "app.organization_id", organizationId?.ToString() ?? "");
        await SchemaQueries.SetSettingAsync(connection, transaction,
            "app.scope", tenantReadHatch ? "tenant" : "");
    }

    internal static async Task InsertAsync(
        DbConnection connection, DbTransaction transaction, string table,
        Guid tenantId, Guid? organizationId, Guid id, Guid parentId,
        string? slug = null, string locale = "en")
    {
        await using var command = new NpgsqlCommand(
            InsertSql(table), (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("organization", NpgsqlTypes.NpgsqlDbType.Uuid,
            organizationId is { } value ? value : DBNull.Value);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("parent", parentId);
        command.Parameters.AddWithValue("slug", slug ?? "proof-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("locale", locale);
        command.Parameters.AddWithValue("actor", SchemaFixture.Actor);
        await command.ExecuteNonQueryAsync();
    }

    internal static string InsertSql(string table) => table switch
    {
        "courses" =>
            """
            INSERT INTO courses
                (id, tenant_id, organization_id, slug_key, status, created_at, created_by, row_version)
            VALUES (@id, @tenant, @organization, @slug, 'draft', now(), @actor, 0)
            """,
        "lessons" =>
            """
            INSERT INTO lessons
                (id, tenant_id, organization_id, course_id, sort, status,
                 content_type_key, content_type_schema_version, created_at, created_by, row_version)
            VALUES (@id, @tenant, @organization, @parent, 0, 'draft',
                    'topic', 1, now(), @actor, 0)
            """,
        "course_translations" =>
            """
            INSERT INTO course_translations
                (course_id, tenant_id, organization_id, locale, title, summary, slug)
            VALUES (@parent, @tenant, @organization, @locale, 'Fixture title', NULL, @slug)
            """,
        "lesson_translations" =>
            """
            INSERT INTO lesson_translations
                (lesson_id, tenant_id, organization_id, locale, title, slug, body)
            VALUES (@parent, @tenant, @organization, @locale, 'Fixture title', @slug, '{}')
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Not an Education table."),
    };
}
