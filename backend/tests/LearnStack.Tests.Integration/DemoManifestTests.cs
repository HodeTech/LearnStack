using System.Text.Json;
using FluentAssertions;
using LearnStack.Tools.Seeder;
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
                lane.ExcludedContent.Should().NotBeEmpty().And.NotIntersectWith(lane.Fields.Select(row => row.Value));
                foreach (var hidden in curriculum.Courses.Where(row => row.ContentAccess != "public")
                    .SelectMany(row => row.Lessons).SelectMany(row => row.Translations))
                    lane.ExcludedContent.Should().Contain(hidden.Title,
                        "restricted lesson identity is different from published course marketing");
                foreach (var hidden in course.Lessons.Where(row => row.Status != "Published")
                    .SelectMany(row => row.Translations))
                    lane.ExcludedContent.Should().Contain(hidden.Title, "draft lessons stay outside the public outline");
            }
        }
        manifest.Select(row => row.Fields.Count).Distinct().Should().HaveCount(2,
            "the browser proof must observe two genuinely distinct authored shapes");
    }
}
