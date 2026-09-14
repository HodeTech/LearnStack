using System.Data.Common;
using FluentAssertions;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Education's database invariants through an actual application-role login.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class EducationConstraintTests(SchemaFixture schema)
{
    private static readonly string[] PublicationStates = ["draft", "published"];

    private static readonly EducationSchemaSeed.Scope TenantWide =
        EducationSchemaSeed.Find(SchemaFixture.TenantA, null);

    public static TheoryData<string, string, bool> SlugCases()
    {
        var data = new TheoryData<string, string, bool>();
        (string Value, bool Valid)[] candidates =
        [
            ("a", true), ("lesson-12-unit", true), (new string('a', 160), true),
            (new string('a', 80) + "-" + new string('b', 79), true),
            ("", false), (new string('a', 161), false), ("Uppercase", false),
            (" leading", false), ("trailing ", false), ("two words", false),
            ("final-newline\n", false), ("tab\t", false), ("öğren", false),
            ("-leading", false), ("trailing-", false), ("two--hyphens", false),
            ("under_score", false), ("dot.name", false),
            ("0123456789abcdef0123456789abcdef", false),
            ("01234567-89ab-cdef-0123-456789abcdef", false),
        ];
        foreach (var table in new[] { "courses", "course_translations", "lesson_translations" })
        {
            foreach (var candidate in candidates)
            {
                data.Add(table, candidate.Value, candidate.Valid);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SlugCases))]
    public Task Slug_storage_and_the_shared_predicate_agree_at_URL_boundaries(
        string table, string slug, bool valid) => InScopeAsync(async (connection, transaction) =>
    {
        EducationSlug.IsValid(slug).Should().Be(valid);
        var parent = table == "lesson_translations" ? TenantWide.LessonId : TenantWide.CourseId;
        var insert = () => EducationSchemaSeed.InsertAsync(connection, transaction,
            table, TenantWide.TenantId, null, Guid.NewGuid(), parent, slug, "de");
        if (valid)
        {
            await insert.Should().NotThrowAsync();
        }
        else
        {
            // A valid control on the same table precedes the rejected value. Each
            // transaction rolls back, including successful control rows.
            await EducationSchemaSeed.InsertAsync(connection, transaction,
                table, TenantWide.TenantId, null, Guid.NewGuid(), parent, "valid-control", "fr");
            var failure = await insert.Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be(slug.Length > 160
                ? PostgresErrorCodes.StringDataRightTruncation : PostgresErrorCodes.CheckViolation);
            if (slug.Length <= 160)
            {
                failure.Which.ConstraintName.Should().Be(table == "courses"
                    ? "ck_courses_slug_key_format" : $"ck_{table}_slug_format");
            }
        }
    });

    [Theory]
    [InlineData("courses", "Draft")]
    [InlineData("courses", "archived")]
    [InlineData("lessons", "Published")]
    [InlineData("lessons", "archived")]
    public Task Publication_columns_accept_both_lowercase_states_and_refuse_other_values(
        string table, string invalid) => InScopeAsync(async (connection, transaction) =>
    {
        using var context = new EducationDbContext(
            new DbContextOptionsBuilder<EducationDbContext>()
                .UseNpgsql(schema.Postgres.AppConnectionString).Options,
            StaticTenantContextAccessor.Unresolved);
        var entity = context.Model.FindEntityType(table == "courses" ? typeof(Course) : typeof(Lesson))!;
        var converter = entity.FindProperty(nameof(Course.Status))!.GetTypeMapping().Converter!;
        var states = Enum.GetValues<PublicationStatus>()
            .Select(status => (Status: status, Stored: (string)converter.ConvertToProvider(status)!))
            .ToArray();
        states.Select(state => state.Stored).Should().BeEquivalentTo(PublicationStates,
            "ADR-0048 fixes exactly two lowercase stored states for both roots");

        var id = table == "courses" ? TenantWide.CourseId : TenantWide.LessonId;
        foreach (var (status, stored) in states)
        {
            stored.Should().Be(status.ToString().ToLowerInvariant());
            converter.ConvertFromProvider(stored).Should().Be(status);
            (await UpdateAsync(connection, transaction, table, id, "status = @value", stored))
                .Should().Be(1);
        }

        await CheckFailureAsync(
            () => UpdateAsync(connection, transaction, table, id, "status = @value", invalid),
            $"ck_{table}_status");
    });

    [Fact]
    public Task Lesson_sort_permits_zero_positive_values_and_ties_but_not_negative_values() =>
        InScopeAsync(async (connection, transaction) =>
        {
            await EducationSchemaSeed.InsertAsync(connection, transaction, "lessons",
                TenantWide.TenantId, null, Guid.NewGuid(), TenantWide.CourseId);
            await EducationSchemaSeed.InsertAsync(connection, transaction, "lessons",
                TenantWide.TenantId, null, Guid.NewGuid(), TenantWide.CourseId);
            // Both controls insert sort 0 beside the already-seeded sort 0 lesson.
            (await UpdateAsync(connection, transaction, "lessons", TenantWide.LessonId,
                "sort = @value", 3)).Should().Be(1);
            await CheckFailureAsync(() => UpdateAsync(connection, transaction, "lessons",
                TenantWide.LessonId, "sort = @value", -1), "ck_lessons_sort");
        });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public Task Content_type_revision_is_positive(int invalid) => InScopeAsync(async (connection, transaction) =>
    {
        (await UpdateAsync(connection, transaction, "lessons", TenantWide.LessonId,
            "content_type_schema_version = @value", 2)).Should().Be(1);
        await CheckFailureAsync(() => UpdateAsync(connection, transaction, "lessons",
            TenantWide.LessonId, "content_type_schema_version = @value", invalid),
            "ck_lessons_content_type_schema_version");
    });

    public static TheoryData<string?, int?, string?, bool> LevelCases() => new()
    {
        { null, null, null, true },
        { "levels", 1, "first", true },
        { "levels", null, null, false },
        { null, 1, null, false },
        { null, null, "first", false },
        { "levels", 1, null, false },
        { "levels", null, "first", false },
        { null, 1, "first", false },
        { "levels", 0, "first", false },
        { "levels", -1, "first", false },
    };

    [Theory]
    [MemberData(nameof(LevelCases))]
    public Task Course_level_pin_is_absent_or_complete_with_a_positive_revision(
        string? taxonomy, int? version, string? band, bool valid) =>
        InScopeAsync(async (connection, transaction) =>
        {
            // PostgreSQL CHECK accepts UNKNOWN; every partially-null triple must
            // therefore be exercised, not only the easy zero-version violation.
            await SetLevelAsync(connection, transaction, "levels", 2, "second");
            if (valid)
            {
                (await SetLevelAsync(connection, transaction, taxonomy, version, band)).Should().Be(1);
            }
            else
            {
                await CheckFailureAsync(() => SetLevelAsync(connection, transaction, taxonomy, version, band),
                    "ck_courses_level_reference");
            }
        });

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"text\":\"Unicode içerik\"}", true)]
    [InlineData("[]", false)]
    [InlineData("[1]", false)]
    [InlineData("\"scalar\"", false)]
    [InlineData("42", false)]
    [InlineData("true", false)]
    [InlineData("null", false)]
    public Task Translated_body_is_a_JSON_object(string body, bool valid) =>
        InScopeAsync(async (connection, transaction) =>
        {
            await EducationSchemaSeed.InsertAsync(connection, transaction, "lesson_translations",
                TenantWide.TenantId, null, Guid.NewGuid(), TenantWide.LessonId, "body-control", "fr");
            var insert = () => SchemaQueries.ExecuteAsync(connection, transaction,
                """
                INSERT INTO lesson_translations
                    (lesson_id, tenant_id, organization_id, locale, title, slug, body)
                VALUES (@parent, @tenant, NULL, 'de', 'Body proof', 'body-proof', @body::jsonb)
                """, ("parent", TenantWide.LessonId), ("tenant", TenantWide.TenantId), ("body", body));
            if (valid)
            {
                await insert.Should().NotThrowAsync();
            }
            else
            {
                await CheckFailureAsync(insert, "ck_lesson_translations_body_object");
            }
        });

    [Theory]
    [InlineData("lessons", "content_type_key")]
    [InlineData("courses", "level_taxonomy_key")]
    [InlineData("courses", "level_band_key")]
    public Task Customization_pin_keys_have_their_own_100_character_storage_bound(
        string table, string column) => InScopeAsync(async (connection, transaction) =>
    {
        await SetLevelAsync(connection, transaction, "levels", 1, "first");
        var id = table == "courses" ? TenantWide.CourseId : TenantWide.LessonId;
        (await UpdateAsync(connection, transaction, table, id, $"{column} = @value", new string('a', 100)))
            .Should().Be(1);
        var update = () => UpdateAsync(connection, transaction, table, id,
            $"{column} = @value", new string('a', 101));
        var failure = await update.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be(PostgresErrorCodes.StringDataRightTruncation);
    });

    [Theory]
    [InlineData("course_translations", false)]
    [InlineData("course_translations", true)]
    [InlineData("lesson_translations", false)]
    [InlineData("lesson_translations", true)]
    public Task Localized_slug_uniqueness_spans_parents_and_organizations(string table, bool otherOrganization) =>
        InScopeAsync(async (connection, transaction) =>
        {
            var first = await CreateParentAsync(connection, transaction, table, TenantWide);
            await InsertTranslationAsync(connection, transaction, table, TenantWide, first, "reserved");
            var secondScope = otherOrganization
                ? EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA1) : TenantWide;
            await EducationSchemaSeed.AnnounceAsync(connection, transaction,
                secondScope.TenantId, secondScope.OrganizationId);
            var second = await CreateParentAsync(connection, transaction, table, secondScope);
            await InsertTranslationAsync(connection, transaction, table, secondScope, second, "other-slug", "fr");
            await UniqueFailureAsync(() => InsertTranslationAsync(connection, transaction,
                table, secondScope, second, "reserved"), $"ux_{table}_tenant_id_locale_slug");
        });

    [Theory]
    [InlineData("course_translations")]
    [InlineData("lesson_translations")]
    public Task Localized_slug_namespace_is_independent_between_tenants_and_locales(string table) =>
        InScopeAsync(async (connection, transaction) =>
        {
            var first = await CreateParentAsync(connection, transaction, table, TenantWide);
            await InsertTranslationAsync(connection, transaction, table, TenantWide, first, "shared-slug");
            await InsertTranslationAsync(connection, transaction, table, TenantWide, first, "shared-slug", "tr-TR");
            var otherTenant = EducationSchemaSeed.Find(SchemaFixture.TenantB, null);
            await EducationSchemaSeed.AnnounceAsync(connection, transaction, otherTenant.TenantId, null);
            var second = await CreateParentAsync(connection, transaction, table, otherTenant);
            await InsertTranslationAsync(connection, transaction, table, otherTenant, second, "shared-slug");
        });

    [Theory]
    [InlineData("course_translations")]
    [InlineData("lesson_translations")]
    public Task A_parent_has_one_translation_per_locale_even_with_distinct_slugs(string table) =>
        InScopeAsync(async (connection, transaction) =>
        {
            var parent = await CreateParentAsync(connection, transaction, table, TenantWide);
            await InsertTranslationAsync(connection, transaction, table, TenantWide, parent, "first-slug");
            await UniqueFailureAsync(() => InsertTranslationAsync(connection, transaction,
                table, TenantWide, parent, "second-slug"), $"pk_{table}");
        });

    [Theory]
    [InlineData("course_translations", "courses")]
    [InlineData("lesson_translations", "lessons")]
    public Task Soft_deleting_the_parent_does_not_release_a_translated_slug(string table, string rootTable) =>
        InScopeAsync(async (connection, transaction) =>
        {
            var first = await CreateParentAsync(connection, transaction, table, TenantWide);
            await InsertTranslationAsync(connection, transaction, table, TenantWide, first, "still-reserved");
            (await UpdateAsync(connection, transaction, rootTable, first,
                "deleted_at = @value", DateTimeOffset.UtcNow)).Should().Be(1);
            var second = await CreateParentAsync(connection, transaction, table, TenantWide);
            await UniqueFailureAsync(() => InsertTranslationAsync(connection, transaction,
                table, TenantWide, second, "still-reserved"), $"ux_{table}_tenant_id_locale_slug");
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Course_authoring_handle_is_unique_only_among_live_rows(bool softDelete) =>
        InScopeAsync(async (connection, transaction) =>
        {
            var id = Guid.NewGuid();
            await EducationSchemaSeed.InsertAsync(connection, transaction, "courses",
                TenantWide.TenantId, null, id, id, "stable-handle");
            if (softDelete)
            {
                (await UpdateAsync(connection, transaction, "courses", id,
                    "deleted_at = @value", DateTimeOffset.UtcNow)).Should().Be(1);
            }

            var insert = () => EducationSchemaSeed.InsertAsync(connection, transaction,
                "courses", TenantWide.TenantId, null, Guid.NewGuid(), Guid.NewGuid(), "stable-handle");
            if (softDelete)
            {
                await insert.Should().NotThrowAsync();
            }
            else
            {
                await UniqueFailureAsync(insert, "ux_courses_tenant_id_slug_key");
            }
        });

    private async Task InScopeAsync(Func<DbConnection, DbTransaction, Task> body)
    {
        await using var connection = await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var transaction = await connection.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(connection, transaction, TenantWide.TenantId, null);
        await body(connection, transaction);
        // Disposal rolls back every row and context announcement, including the
        // positive controls; the shared fixture's committed seed never changes.
    }

    private static async Task<Guid> CreateParentAsync(
        DbConnection connection, DbTransaction transaction, string translationTable,
        EducationSchemaSeed.Scope scope)
    {
        var course = Guid.NewGuid();
        await EducationSchemaSeed.InsertAsync(connection, transaction, "courses",
            scope.TenantId, scope.OrganizationId, course, course);
        if (translationTable == "course_translations")
        {
            return course;
        }

        // Each lesson has a different course, so the flat lesson slug proof
        // catches an accidental course_id term in the unique key as well.
        var lesson = Guid.NewGuid();
        await EducationSchemaSeed.InsertAsync(connection, transaction, "lessons",
            scope.TenantId, scope.OrganizationId, lesson, course);
        return lesson;
    }

    private static Task InsertTranslationAsync(
        DbConnection connection, DbTransaction transaction, string table,
        EducationSchemaSeed.Scope scope, Guid parent, string slug, string locale = "en") =>
        EducationSchemaSeed.InsertAsync(connection, transaction, table,
            scope.TenantId, scope.OrganizationId, Guid.NewGuid(), parent, slug, locale);

    private static async Task<int> UpdateAsync(
        DbConnection connection, DbTransaction transaction, string table, Guid id, string assignment, object value)
    {
        // Identifiers and assignments come only from the closed test cases above.
        await using var command = new NpgsqlCommand($"UPDATE {table} SET {assignment} WHERE id = @id",
            (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("value", value);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> SetLevelAsync(
        DbConnection connection, DbTransaction transaction, string? taxonomy, int? version, string? band)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE courses SET level_taxonomy_key = @taxonomy,
                level_taxonomy_schema_version = @version, level_band_key = @band
            WHERE id = @id
            """, (NpgsqlConnection)connection, (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("id", TenantWide.CourseId);
        command.Parameters.AddWithValue("taxonomy", NpgsqlDbType.Varchar, (object?)taxonomy ?? DBNull.Value);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Integer, (object?)version ?? DBNull.Value);
        command.Parameters.AddWithValue("band", NpgsqlDbType.Varchar, (object?)band ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task CheckFailureAsync(Func<Task> action, string constraint)
    {
        var failure = await action.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        failure.Which.ConstraintName.Should().Be(constraint);
    }

    private static async Task UniqueFailureAsync(Func<Task> action, string constraint)
    {
        var failure = await action.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        failure.Which.ConstraintName.Should().Be(constraint);
    }
}
