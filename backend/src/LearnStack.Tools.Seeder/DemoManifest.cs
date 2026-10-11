using System.Text.Json;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Tools.Seeder;

/// <summary>Projects the declared seed into public demo expectations without a database.</summary>
public static class DemoManifest
{
    public static IReadOnlyList<DemoLane> Create()
    {
        var lanes = new List<DemoLane>();
        foreach (var tenant in SeedData.All)
        {
            var curriculum = tenant.Curriculum ?? throw new InvalidOperationException("Missing demo curriculum.");
            OrganizationId? hostOrganization = tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null;
            // Prefer the host's own scope so both tenant-wide and organization
            // admission are exercised. Other visible catalog entries remain data.
            var course = curriculum.Courses.Single(row => row.OrganizationId == hostOrganization
                && row.Status == "Published" && row.ContentAccess == "public");
            foreach (var locale in curriculum.Locales.Where(row => row.IsEnabled).OrderBy(row => row.Sort))
            {
                var translation = course.Translations.Single(row => row.Locale == locale.Locale);
                var lesson = course.Lessons.Where(row => row.Status == "Published").OrderBy(row => row.Sort).First();
                var lessonTranslation = lesson.Translations.Single(row => row.Locale == locale.Locale);
                var type = SeedData.ContentTypes(tenant).Single(row => row.Key == lesson.ContentTypeKey
                    && row.SchemaVersion == lesson.ContentTypeSchemaVersion);
                using var schema = JsonDocument.Parse(type.JsonSchema);
                using var body = JsonDocument.Parse(lessonTranslation.Body);
                var fields = schema.RootElement.GetProperty("x-fields").EnumerateArray().Select(field =>
                    new DemoField(field.GetProperty("name").GetString()!,
                        Label(field.GetProperty("label"), locale.Locale),
                        body.RootElement.GetProperty(field.GetProperty("name").GetString()!).GetString()!)).ToArray();
                var prefix = $"https://{tenant.Host}:3000/{locale.Locale}/courses";
                using var theme = JsonDocument.Parse(curriculum.Theme.Value);
                lanes.Add(new(tenant.Host, locale.Locale, tenant.DisplayName, translation.Title, lessonTranslation.Title,
                    prefix, $"{prefix}/{translation.Slug}", $"{prefix}/{translation.Slug}/lessons/{lessonTranslation.Slug}",
                    fields, theme.RootElement.EnumerateObject().ToDictionary(row => row.Name, row => row.Value.GetString()!),
                    // Restricted courses retain public marketing; only lesson
                    // bodies require access. Never classify their titles as leaks.
                    curriculum.Courses.Where(row => row.Status != "Published"
                        || (row.OrganizationId is not null && row.OrganizationId != hostOrganization))
                        .SelectMany(row => row.Translations.Select(value => value.Title))
                        .Concat(SeedData.All.Where(row => row.TenantId != tenant.TenantId)
                            .SelectMany(row => row.Curriculum!.Courses.SelectMany(value => value.Translations.Select(text => text.Title))))
                        .Distinct(StringComparer.Ordinal).ToArray()));
            }
        }
        if (lanes.Count != 3 || lanes.SelectMany(row => new[] { row.CatalogUrl, row.CourseUrl, row.LessonUrl })
                .Distinct(StringComparer.Ordinal).Count() != 9)
            throw new InvalidOperationException("The accepted demo requires three lanes and nine distinct URLs.");
        return lanes;
    }

    private static string Label(JsonElement labels, string locale) =>
        labels.TryGetProperty(locale, out var exact) ? exact.GetString()!
        : labels.GetProperty("en").GetString()!;
}

public sealed record DemoLane(string Host, string Locale, string DisplayName, string CourseTitle,
    string LessonTitle, string CatalogUrl, string CourseUrl, string LessonUrl,
    IReadOnlyList<DemoField> Fields, IReadOnlyDictionary<string, string> Theme, IReadOnlyList<string> ExcludedTitles);
public sealed record DemoField(string Name, string Label, string Value);
