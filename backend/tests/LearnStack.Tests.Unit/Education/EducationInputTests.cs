using FluentAssertions;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Education;

public sealed class EducationInputTests
{
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private static readonly UserId Actor = UserId.From(Guid.Parse("00000000-0000-7000-8000-000000000001"));
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));
    private static readonly CourseId CourseId = CourseId.From(Guid.Parse("cccccccc-1111-7111-8111-111111111111"));
    private static readonly LessonId LessonId = LessonId.From(Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111"));

    private static Course NewCourse(string slug = "course") =>
        Course.Create(CourseId, Tenant, null, slug, Clock, Actor);

    private static Lesson NewLesson() =>
        Lesson.Create(LessonId, NewCourse(), 0, "content", 1, Clock, Actor);

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("slug\n")]
    [InlineData("slug\r\n")]
    [InlineData("slug_name")]
    [InlineData("-slug")]
    [InlineData("slug-")]
    [InlineData("two--hyphens")]
    [InlineData("İçerik")]
    [InlineData("日本語")]
    [InlineData("123456781234123412341234567890ab")]
    [InlineData("12345678-1234-1234-1234-1234567890ab")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Slug_InvalidShapeRefusesInHandleAndBothTranslations(string slug)
    {
        EducationSlug.IsValid(slug).Should().BeFalse();
        var create = () => NewCourse(slug);
        create.Should().Throw<ArgumentException>();
        var course = NewCourse();
        var lesson = NewLesson();
        course.AddTranslation("en", "Title", null, slug, Clock, Actor).Error?.Code.Should().Be("validation_failed");
        lesson.AddTranslation("en", "Title", slug, "{}", Clock, Actor).Error?.Code.Should().Be("validation_failed");
        course.Translations.Should().BeEmpty();
        lesson.Translations.Should().BeEmpty();
        course.Version.Should().Be(0);
        lesson.Version.Should().Be(0);
    }

    [Fact]
    public void Slug_UsesEducationWidthAndPreservesValidValues()
    {
        var slug = new string('a', 160);
        NewCourse(slug).SlugKey.Should().Be(slug);
        var course = NewCourse();
        var lesson = NewLesson();
        course.AddTranslation("en", "Title", null, slug, Clock, Actor).IsSuccess.Should().BeTrue();
        lesson.AddTranslation("en", "Title", slug, "{}", Clock, Actor).IsSuccess.Should().BeTrue();
        course.Translations.Single().Slug.Should().Be(slug);
        lesson.Translations.Single().Slug.Should().Be(slug);
        EducationSlug.IsValid(slug + "a").Should().BeFalse();
        EducationSlug.IsValid("a").Should().BeTrue();
        EducationSlug.IsValid("0").Should().BeTrue();
        EducationSlug.IsValid("a-b-123").Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Definition")]
    [InlineData("definition\n")]
    [InlineData("definition:key")]
    [InlineData("定义")]
    [InlineData("two--hyphens")]
    public void PinKey_InvalidShapeRefusesInEveryPinPosition(string key)
    {
        var content = () => Lesson.Create(LessonId, NewCourse(), 0, key, 1, Clock, Actor);
        var taxonomy = () => Course.Create(CourseId, Tenant, null, "course", Clock, Actor, key, 1, "band");
        var band = () => Course.Create(CourseId, Tenant, null, "course", Clock, Actor, "taxonomy", 1, key);
        content.Should().Throw<ArgumentException>();
        taxonomy.Should().Throw<ArgumentException>();
        band.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PinKey_UsesDefinitionWidthAndDoesNotApplyRoutingUuidRule()
    {
        EducationPinKey.IsValid(new string('a', 100)).Should().BeTrue();
        EducationPinKey.IsValid(new string('a', 101)).Should().BeFalse();
        EducationPinKey.IsValid("123456781234123412341234567890ab").Should().BeTrue();
        var create = () => Lesson.Create(LessonId, NewCourse(), 0, new string('a', 101), 1, Clock, Actor);
        create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("en_US")]
    [InlineData("en\n")]
    [InlineData(" en")]
    [InlineData("en-1234-extra-extra-extra-extra-extra-extra")]
    public void Translation_InvalidLocaleRefusesWithoutMutation(string locale)
    {
        var course = NewCourse();
        var lesson = NewLesson();
        course.AddTranslation(locale, "Title", null, "course", Clock, Actor).Error?.Code.Should().Be("validation_failed");
        lesson.AddTranslation(locale, "Title", "lesson", "{}", Clock, Actor).Error?.Code.Should().Be("validation_failed");
        course.Version.Should().Be(0);
        lesson.Version.Should().Be(0);
        course.Translations.Should().BeEmpty();
        lesson.Translations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Title\0")]
    public void Translation_InvalidTitleRefusesWithoutMutation(string title)
    {
        var course = NewCourse();
        var lesson = NewLesson();
        course.AddTranslation("en", title, null, "course", Clock, Actor).IsFailure.Should().BeTrue();
        lesson.AddTranslation("en", title, "lesson", "{}", Clock, Actor).IsFailure.Should().BeTrue();
        course.Version.Should().Be(0);
        lesson.Version.Should().Be(0);
    }

    [Fact]
    public void Translation_TextHasNoArbitraryCapAndPreservesUnicodeWithoutNormalization()
    {
        var title = "日本語 e\u0301 🧘\n" + new string('a', 20_000);
        var course = NewCourse();
        var lesson = NewLesson();
        course.AddTranslation("en", title, title, "course", Clock, Actor).IsSuccess.Should().BeTrue();
        lesson.AddTranslation("en", title, "lesson", "{}", Clock, Actor).IsSuccess.Should().BeTrue();
        course.Translations.Single().Title.Should().Be(title);
        course.Translations.Single().Summary.Should().Be(title);
        lesson.Translations.Single().Title.Should().Be(title);
        var invalid = "Text" + (char)0xD800;
        NewCourse().AddTranslation("en", invalid, null, "course", Clock, Actor).IsFailure.Should().BeTrue();
        NewCourse().AddTranslation("en", "Title", invalid, "course", Clock, Actor).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    [InlineData("{\"a\":\"\\u0000\"}")]
    [InlineData("{\"\\u0000\":1}")]
    [InlineData("{\"a\":\"\\ud800\"}")]
    [InlineData("{\"a\":\"\\udc00\"}")]
    [InlineData("{\"a\":1e131072}")]
    [InlineData("{\"a\":1e-16384}")]
    public void Body_NonObjectOrPostgresUnstorableJsonRefusesAndPreservesPriorTranslation(string body)
    {
        var lesson = NewLesson();
        lesson.AddTranslation("en", "Original", "lesson", "{}", Clock, Actor).IsSuccess.Should().BeTrue();
        var result = lesson.AddTranslation("tr", "Yeni", "ders", body, Clock, Actor);
        result.Error?.Code.Should().Be("validation_failed");
        result.Error?.Details.Should().ContainKey("Body");
        lesson.Version.Should().Be(1);
        lesson.Translations.Should().ContainSingle().Which.Body.Should().Be("{}");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":\"🎼\"}")]
    [InlineData("{\"text\":\"\\\\u0000\"}")]
    [InlineData("{\"number\":1e131071}")]
    [InlineData("{\"number\":1e-16383}")]
    public void Body_StorableObjectRetainsAuthoredValues(string body)
    {
        var lesson = NewLesson();
        lesson.AddTranslation("en", "Title", "lesson", body, Clock, Actor).IsSuccess.Should().BeTrue();
        lesson.Translations.Single().Body.Should().Be(body);
    }

    [Fact]
    public void Body_DoesNotInheritCustomizationRowCap()
    {
        var body = "{\"text\":\"" + new string('a', 300_000) + "\"}";
        var lesson = NewLesson();
        lesson.AddTranslation("en", "Title", "lesson", body, Clock, Actor).IsSuccess.Should().BeTrue();
        lesson.Translations.Single().Body.Should().Be(body);
    }

    [Fact]
    public void Create_UnassignedOrEmptyScopeAndIdentifiersRefuse()
    {
        var id = (new CourseId[1])[0];
        var tenant = (new TenantId[1])[0];
        var organization = (new OrganizationId[1])[0];
        var invalidId = () => Course.Create(id, Tenant, null, "course", Clock, Actor);
        var invalidTenant = () => Course.Create(CourseId, tenant, null, "course", Clock, Actor);
        var invalidOrganization = () => Course.Create(CourseId, Tenant, organization, "course", Clock, Actor);
        var sentinel = () => Course.Create(CourseId, TenantId.PlatformSentinel, null, "course", Clock, Actor);
        var emptyId = () => Course.Create(CourseId.From(Guid.Empty), Tenant, null, "course", Clock, Actor);
        var emptyOrganization = () => Course.Create(CourseId, Tenant, OrganizationId.From(Guid.Empty), "course", Clock, Actor);
        var invalidLessonId = () => Lesson.Create((new LessonId[1])[0], NewCourse(), 0, "content", 1, Clock, Actor);
        invalidId.Should().Throw<ArgumentException>();
        invalidTenant.Should().Throw<ArgumentException>();
        invalidOrganization.Should().Throw<ArgumentException>();
        sentinel.Should().Throw<ArgumentException>();
        emptyId.Should().Throw<ArgumentException>();
        emptyOrganization.Should().Throw<ArgumentException>();
        invalidLessonId.Should().Throw<ArgumentException>();
    }
}
