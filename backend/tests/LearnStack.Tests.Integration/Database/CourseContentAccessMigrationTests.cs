using System.Data.Common;
using FluentAssertions;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class CourseContentAccessMigrationTests(SchemaFixture schema)
{
    [Fact]
    public async Task Legacy_rows_are_restricted_without_changing_payload_pins_scope_or_publication_and_reapply_is_safe()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var context = new EducationDbContext(new DbContextOptionsBuilder<EducationDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                provider => provider.MigrationsHistoryTable(EducationDbContextFactory.HistoryTable)).Options,
            StaticTenantContextAccessor.Unresolved);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        var migrations = context.Database.GetMigrations().ToArray();
        var policyIndex = Array.IndexOf(migrations, "20261001233219_add_course_content_access");
        policyIndex.Should().BeGreaterThan(0, "the policy migration must exist and have a predecessor");
        var previous = migrations[policyIndex - 1];
        // Technical reversal is confined to this disposable database. ADR-0050
        // forbids using it as a live rollback with old anonymous public readers.
        await context.GetService<IMigrator>().MigrateAsync(previous);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await using var insert = new NpgsqlCommand(LegacyRows, (NpgsqlConnection)app);
            await insert.ExecuteNonQueryAsync();
        }

        var before = await SnapshotAsync(database.AppConnectionString);
        await context.Database.MigrateAsync();
        (await SnapshotAsync(database.AppConnectionString)).Should().Be(before);
        await AssertLegacyPoliciesAsync(database.AppConnectionString);

        await context.GetService<IMigrator>().MigrateAsync(previous);
        (await SnapshotAsync(database.AppConnectionString)).Should().Be(before);
        await context.Database.MigrateAsync();
        (await SnapshotAsync(database.AppConnectionString)).Should().Be(before);
        await AssertLegacyPoliciesAsync(database.AppConnectionString);
        context.Database.HasPendingModelChanges().Should().BeFalse();
        await AssertDefaultAndCheckAsync(database.AppConnectionString);
    }

    private static async Task AssertLegacyPoliciesAsync(string connectionString)
    {
        await using (var app = await PostgresFixture.OpenAsync(connectionString))
        {
            await using var transaction = await app.BeginTransactionAsync();
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await using var read = new NpgsqlCommand(
                "SELECT count(*) FROM courses WHERE content_access = 'enrollment_required'",
                (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
            (await read.ExecuteScalarAsync()).Should().Be(2L);
            await transaction.CommitAsync();
        }
    }

    private static async Task AssertDefaultAndCheckAsync(string connectionString)
    {
        await using var app = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        var course = Guid.CreateVersion7();
        await EducationSchemaSeed.InsertAsync(app, transaction, "courses", SchemaFixture.TenantA,
            null, course, course, "fresh-default");
        await using var read = new NpgsqlCommand("SELECT content_access FROM courses WHERE id = @id",
            (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        read.Parameters.AddWithValue("id", course);
        (await read.ExecuteScalarAsync()).Should().Be("enrollment_required");
        await using var change = new NpgsqlCommand("UPDATE courses SET content_access = @policy WHERE id = @id",
            (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
        change.Parameters.AddWithValue("policy", "public");
        change.Parameters.AddWithValue("id", course);
        (await change.ExecuteNonQueryAsync()).Should().Be(1);
        change.Parameters["policy"].Value = "PUBLIC";
        var rejected = async () => await change.ExecuteNonQueryAsync();
        var error = (await rejected.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.ConstraintName.Should().Be("ck_courses_content_access");
        await transaction.RollbackAsync();
    }

    private static async Task<string> SnapshotAsync(string connectionString)
    {
        await using var app = await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        var snapshots = new List<string>();
        foreach (var table in EducationSchemaSeed.Tables)
        {
            await using var read = new NpgsqlCommand(
                $"SELECT jsonb_agg(to_jsonb(row) - 'content_access' ORDER BY to_jsonb(row)::text)::text FROM {table} row",
                (NpgsqlConnection)app, (NpgsqlTransaction)transaction);
            snapshots.Add((string)(await read.ExecuteScalarAsync())!);
        }

        await transaction.CommitAsync();
        return string.Join('\n', snapshots);
    }

    private const string LegacyRows = """
        BEGIN;
        SET LOCAL app.tenant_id = '11111111-1111-7111-8111-111111111111';
        INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('11111111-1111-7111-8111-111111111111', 'policy-proof', 'Policy', 'Trial', now(),
                '00000000-0000-7000-8000-000000000001', 0);
        INSERT INTO organizations (id, tenant_id, slug, display_name, status, created_at, created_by, row_version)
        VALUES ('aaaaaaaa-1111-7111-8111-111111111111', '11111111-1111-7111-8111-111111111111',
                'main', 'Main', 'Active', now(), '00000000-0000-7000-8000-000000000001', 0);
        INSERT INTO courses (id, tenant_id, slug_key, status, level_taxonomy_key, level_taxonomy_schema_version,
                             level_band_key, created_at, created_by, row_version)
        VALUES ('cccccccc-0000-7000-8000-000000000001', '11111111-1111-7111-8111-111111111111',
                'legacy-wide', 'published', 'difficulty', 7, 'intro', now(), '00000000-0000-7000-8000-000000000001', 8);
        SET LOCAL app.organization_id = 'aaaaaaaa-1111-7111-8111-111111111111';
        INSERT INTO courses (id, tenant_id, organization_id, slug_key, status, created_at, created_by, row_version)
        VALUES ('cccccccc-0000-7000-8000-000000000002', '11111111-1111-7111-8111-111111111111',
                'aaaaaaaa-1111-7111-8111-111111111111', 'legacy-scoped', 'draft', now(),
                '00000000-0000-7000-8000-000000000001', 4);
        SET LOCAL app.organization_id = '';
        INSERT INTO course_translations (course_id, tenant_id, locale, title, summary, slug)
        VALUES ('cccccccc-0000-7000-8000-000000000001', '11111111-1111-7111-8111-111111111111',
                'en', 'Legacy course', 'Preserved summary', 'legacy-course');
        SET LOCAL app.organization_id = 'aaaaaaaa-1111-7111-8111-111111111111';
        INSERT INTO lessons (id, tenant_id, organization_id, course_id, sort, status,
                             content_type_key, content_type_schema_version, created_at, created_by, row_version)
        VALUES ('dddddddd-0000-7000-8000-000000000001', '11111111-1111-7111-8111-111111111111',
                'aaaaaaaa-1111-7111-8111-111111111111', 'cccccccc-0000-7000-8000-000000000002', 3,
                'published', 'text-card', 9, now(), '00000000-0000-7000-8000-000000000001', 2);
        INSERT INTO lesson_translations (lesson_id, tenant_id, organization_id, locale, title, slug, body)
        VALUES ('dddddddd-0000-7000-8000-000000000001', '11111111-1111-7111-8111-111111111111',
                'aaaaaaaa-1111-7111-8111-111111111111', 'en', 'Legacy lesson', 'legacy-lesson', '{"body":"Private 🧘"}');
        COMMIT;
        """;
}
