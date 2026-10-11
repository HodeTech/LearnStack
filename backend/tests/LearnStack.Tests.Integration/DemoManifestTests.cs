using System.Text.Json;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration;

public sealed class DemoManifestTests
{
    [Fact]
    public void Demo_manifest_covers_every_enabled_seed_lane_and_nine_distinct_real_content_urls()
    {
        var manifest = DemoManifest.Create();
        manifest.Should().HaveCount(3);
        manifest.SelectMany(row => new[] { row.CatalogUrl, row.CourseUrl, row.LessonUrl })
            .Distinct(StringComparer.Ordinal).Should().HaveCount(9);
        foreach (var tenant in SeedData.All)
        {
            var curriculum = tenant.Curriculum!;
            var lanes = manifest.Where(row => row.Host == tenant.Host).ToArray();
            lanes.Select(row => row.Locale).Should().BeEquivalentTo(curriculum.Locales.Where(row => row.IsEnabled).Select(row => row.Locale));
            foreach (var lane in lanes)
            {
                lane.DisplayName.Should().Be(tenant.DisplayName);
                var course = curriculum.Courses.Single(row => row.Translations.Any(value => value.Title == lane.CourseTitle));
                course.Status.Should().Be("Published");
                course.ContentAccess.Should().Be("public");
                course.OrganizationId.Should().Be(tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null);
                var translation = course.Translations.Single(row => row.Locale == lane.Locale);
                lane.CourseUrl.Should().Be(lane.CatalogUrl + "/" + translation.Slug);
                var lesson = course.Lessons.Where(row => row.Status == "Published").MinBy(row => row.Sort)!;
                var text = lesson.Translations.Single(row => row.Locale == lane.Locale);
                lane.LessonUrl.Should().Be(lane.CourseUrl + "/lessons/" + text.Slug);
                lane.LessonTitle.Should().Be(text.Title);
                using var schema = JsonDocument.Parse(curriculum.ContentType.JsonSchema);
                using var body = JsonDocument.Parse(text.Body);
                lane.Fields.Should().NotBeEmpty();
                lane.Fields.Select(row => row.Name).Should().Equal(schema.RootElement.GetProperty("x-fields")
                    .EnumerateArray().Select(row => row.GetProperty("name").GetString()));
                foreach (var field in lane.Fields)
                    field.Value.Should().Be(body.RootElement.GetProperty(field.Name).GetString());
                lane.Theme.Should().HaveCount(4);
                lane.ExcludedTitles.Should().NotBeEmpty().And.NotContain(lane.CourseTitle);
                var visibleMarketing = curriculum.Courses.Where(row => row.Status == "Published"
                    && (row.OrganizationId is null || row.OrganizationId == course.OrganizationId))
                    .SelectMany(row => row.Translations.Select(value => value.Title));
                lane.ExcludedTitles.Should().NotIntersectWith(visibleMarketing,
                    "published restricted courses retain public marketing under ADR-0050");
            }
        }
        manifest.Select(row => row.Fields.Count).Distinct().Should().HaveCount(2,
            "the browser proof must observe two genuinely distinct authored shapes");
    }

    [Theory]
    [InlineData("Default", "Username=learnstack_platform")]
    [InlineData("Migration", "Username=learnstack_app")]
    [InlineData("Default", "Host=remote.example")]
    [InlineData("Default", "Port=5433")]
    [InlineData("Default", "Database=other")]
    [InlineData("Default", "Password=stale")]
    public void Demo_configuration_refuses_stale_or_privileged_connection_values(string key, string replacement)
    {
        var values = Configuration();
        DemoDatabaseConfiguration.IsValid(values).Should().BeTrue();
        values[$"ConnectionStrings__{key}"] += ";" + replacement;
        DemoDatabaseConfiguration.IsValid(values).Should().BeFalse();
    }

    [Fact]
    public void Demo_configuration_refuses_missing_and_malformed_values()
    {
        DemoDatabaseConfiguration.IsValid(new Dictionary<string, string>()).Should().BeFalse();
        var values = Configuration();
        values["ConnectionStrings__Default"] = "not a connection string";
        DemoDatabaseConfiguration.IsValid(values).Should().BeFalse();
    }

    private static Dictionary<string, string> Configuration()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["POSTGRES_DB"] = "demo" };
        foreach (var (suffix, role, key) in new[]
        {
            ("Migration", "learnstack_migration", "LEARNSTACK_MIGRATION_PW"),
            ("Default", "learnstack_app", "LEARNSTACK_APP_PW"),
            ("PlatformAdmin", "learnstack_platform", "LEARNSTACK_PLATFORM_PW"),
            ("OutboxDispatcher", "learnstack_outbox_admin", "LEARNSTACK_OUTBOX_PW"),
        })
        {
            // Ephemeral parser inputs only: no service uses these credentials.
            var password = Guid.NewGuid().ToString("N");
            values[key] = password;
            values[$"ConnectionStrings__{suffix}"] = new NpgsqlConnectionStringBuilder
            {
                Host = "localhost",
                Database = "demo",
                Username = role,
                Password = password,
            }.ConnectionString;
        }
        return values;
    }
}
