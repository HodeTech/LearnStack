using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Branding;

namespace LearnStack.Tools.Seeder;

/// <summary>Exact act postconditions; partial authoring is admitted only as declared data.</summary>
internal static class SeedVerification
{
    internal static void Require(bool condition, SeedTenant tenant, string act)
    {
        if (!condition)
            throw new InvalidOperationException($"Seed mismatch in {act} for '{tenant.Slug}': the row that holds the name is not this tenant's expected identity, scope, content or state. Resolve the mismatch and re-run; seed does not overwrite it.");
    }

    internal static void Declaration(SeedTenant tenant)
    {
        if (tenant.Curriculum is not { } curriculum) return;
        var locales = curriculum.Locales.Select(locale => locale.Locale).ToHashSet(StringComparer.Ordinal);
        Require(locales.Count == curriculum.Locales.Length && locales.Count > 0
            && curriculum.Locales.All(locale => locale.IsEnabled)
            && curriculum.Locales.Count(locale => locale.IsDefault) == 1, tenant, "locale declaration");
        foreach (var course in curriculum.Courses)
        {
            Require(course.Status is "Draft" or "Published"
                && course.ContentAccess is "public" or "enrollment_required"
                && course.Translations.Select(translation => translation.Locale).ToHashSet(StringComparer.Ordinal).SetEquals(locales)
                && course.Translations.Length == locales.Count, tenant, "course declaration");
            foreach (var lesson in course.Lessons)
                Require(lesson.Status is "Draft" or "Published"
                    && lesson.Translations.Select(translation => translation.Locale).ToHashSet(StringComparer.Ordinal).SetEquals(locales)
                    && lesson.Translations.Length == locales.Count, tenant, "lesson declaration");
        }
    }

    internal static bool Tenant(TenantSeedDto? row, SeedTenant expected)
    {
        if (row is null) return false;
        Require(row.Id == expected.TenantId && row.Slug == expected.Slug && row.DisplayName == expected.DisplayName
            && row.Status == "Trial" && row.DefaultOrganizationId == expected.DefaultOrganization.OrganizationId, expected, "tenant");
        if (expected.Curriculum is { } curriculum)
            Require(row.Locales.All(actual => curriculum.Locales.Any(locale => LocaleMatches(actual, locale))), expected, "tenant locales");
        return true;
    }

    internal static bool Organization(OrganizationSeedDto? row, SeedOrganization expected, SeedTenant tenant, string act)
    {
        if (row is null) return false;
        Require(row.Id == expected.OrganizationId && row.TenantId == tenant.TenantId && row.Slug == expected.Slug
            && row.DisplayName == expected.DisplayName && row.Status == "Active", tenant, act);
        return true;
    }

    internal static bool Host(HostMappingSeedDto? row, SeedTenant tenant)
    {
        if (row is null) return false;
        Require(row.Host == tenant.Host && row.TenantId == tenant.TenantId && row.OrganizationId ==
            (tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null)
            && row.IsActive && row.IsPubliclyLive, tenant, "host mapping");
        return true;
    }

    internal static bool Locale(TenantSeedDto? row, SeedLocale expected, SeedTenant tenant)
    {
        Require(Tenant(row, tenant), tenant, "tenant locales");
        return row is not null && row.Locales.Any(actual => LocaleMatches(actual, expected));
    }
    private static bool LocaleMatches(TenantLocaleSeedDto row, SeedLocale expected) =>
        row.Locale == expected.Locale && row.IsEnabled == expected.IsEnabled && row.IsDefault == expected.IsDefault && row.Sort == expected.Sort;

    internal static bool ContentType(ContentTypeSeedDto? row, SeedContentType expected, SeedTenant tenant, bool published)
    {
        if (row is null) return false;
        Require(row.Id == expected.Id && row.TenantId == tenant.TenantId && row.Key == expected.Key
            && row.SchemaVersion == expected.SchemaVersion && JsonEqual(row.DisplayNameJson, JsonSerializer.Serialize(expected.DisplayName))
            && JsonEqual(row.JsonSchema, expected.JsonSchema) && row.RendererKey == expected.RendererKey
            && row.Status is "Draft" or "Active", tenant, "content type");
        return !published || row.Status == "Active";
    }

    internal static bool Taxonomy(TaxonomySeedDto? row, SeedTaxonomy expected, SeedTenant tenant, bool published)
    {
        if (row is null) return false;
        Require(row.Id == expected.Id && row.TenantId == tenant.TenantId && row.Key == expected.Key
            && row.SchemaVersion == expected.SchemaVersion && JsonEqual(row.DisplayNameJson, JsonSerializer.Serialize(expected.DisplayName))
            && row.Status is "Draft" or "Active" && row.Items.Length == expected.Bands.Length
            && row.Items.All(actual => expected.Bands.Any(band => actual.Key == band.Key && actual.Sort == band.Sort
                && JsonEqual(actual.DisplayNameJson, JsonSerializer.Serialize(band.DisplayName)) && JsonEqual(actual.Metadata, band.Metadata))), tenant, "level taxonomy");
        return !published || row.Status == "Active";
    }

    internal static bool Theme(SettingSeedDto? row, SeedTheme expected, SeedTenant tenant)
    {
        if (row is null) return false;
        Require(row.Id == expected.Id && row.TenantId == tenant.TenantId && row.OrganizationId is null
            && row.Key == BrandingThemeRegistry.SettingKey && JsonEqual(row.Value, expected.Value), tenant, "branding");
        return true;
    }

    internal static bool Course(CourseSeedDto? row, SeedCourse expected, SeedTenant tenant)
    {
        if (row is null) return false;
        Require(row.Id == expected.Id && row.TenantId == tenant.TenantId && row.OrganizationId == expected.OrganizationId
            && row.SlugKey == expected.SlugKey && row.ContentAccess == expected.ContentAccess
            && row.LevelTaxonomyKey == expected.LevelTaxonomyKey && row.LevelTaxonomySchemaVersion == expected.LevelTaxonomySchemaVersion
            && row.LevelBandKey == expected.LevelBandKey && (row.Status == "Draft" || row.Status == expected.Status)
            && row.Translations.All(actual => expected.Translations.Any(translation => CourseTranslationMatches(actual, translation))), tenant, "course");
        return true;
    }

    internal static bool CourseTranslation(CourseSeedDto? row, SeedCourse course, SeedCourseTranslation expected, SeedTenant tenant)
    {
        Require(Course(row, course, tenant), tenant, "course translation");
        if (row is null) return false;
        if (row.Translations.Any(actual => CourseTranslationMatches(actual, expected))) return true;
        Require(row.Status == "Draft", tenant, "course translation");
        return false;
    }
    private static bool CourseTranslationMatches(CourseTranslationSeedDto row, SeedCourseTranslation expected) =>
        row.Locale == expected.Locale && row.Title == expected.Title && row.Summary == expected.Summary && row.Slug == expected.Slug;

    internal static bool CoursePublication(CourseSeedDto? row, SeedCourse expected, SeedTenant tenant)
    {
        Require(Course(row, expected, tenant) && row is not null && row.Translations.Length == expected.Translations.Length, tenant, "course publication");
        return row is not null && row.Status == expected.Status;
    }

    internal static bool Lesson(LessonSeedDto? row, SeedCourse parent, SeedLesson expected, SeedTenant tenant)
    {
        if (row is null) return false;
        Require(row.Id == expected.Id && row.TenantId == tenant.TenantId && row.OrganizationId == parent.OrganizationId
            && row.CourseId == parent.Id && row.Sort == expected.Sort && row.ContentTypeKey == expected.ContentTypeKey
            && row.ContentTypeSchemaVersion == expected.ContentTypeSchemaVersion && (row.Status == "Draft" || row.Status == expected.Status)
            && row.Translations.All(actual => expected.Translations.Any(translation => LessonTranslationMatches(actual, translation))), tenant, "lesson");
        return true;
    }

    internal static bool LessonTranslation(LessonSeedDto? row, SeedCourse parent, SeedLesson lesson, SeedLessonTranslation expected, SeedTenant tenant)
    {
        Require(Lesson(row, parent, lesson, tenant), tenant, "lesson translation");
        if (row is null) return false;
        if (row.Translations.Any(actual => LessonTranslationMatches(actual, expected))) return true;
        Require(row.Status == "Draft", tenant, "lesson translation");
        return false;
    }
    private static bool LessonTranslationMatches(LessonTranslationSeedDto row, SeedLessonTranslation expected) =>
        row.Locale == expected.Locale && row.Title == expected.Title && row.Slug == expected.Slug && JsonEqual(row.Body, expected.Body);

    internal static bool LessonPublication(LessonSeedDto? row, SeedCourse parent, SeedLesson expected, SeedTenant tenant)
    {
        Require(Lesson(row, parent, expected, tenant) && row is not null && row.Translations.Length == expected.Translations.Length, tenant, "lesson publication");
        return row is not null && row.Status == expected.Status;
    }

    internal static bool JsonEqual(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return false; }
    }
}
