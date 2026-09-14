using FluentAssertions;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Xunit;
using static LearnStack.Tests.Unit.Education.EducationAssertions;

namespace LearnStack.Tests.Unit.Education;

public sealed class EducationAggregateTests
{
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private static readonly FixedClock Later = new(Clock.UtcNow.AddHours(1));
    private static readonly UserId Actor = UserId.From(Guid.Parse("00000000-0000-7000-8000-000000000001"));
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("22222222-2222-7222-8222-222222222222"));
    private static readonly CourseId CourseId = CourseId.From(Guid.Parse("cccccccc-1111-7111-8111-111111111111"));
    private static readonly LessonId LessonId = LessonId.From(Guid.Parse("aaaaaaaa-1111-7111-8111-111111111111"));

    private static Course NewCourse(OrganizationId? organization = null, string slug = "course") =>
        Course.Create(CourseId, Tenant, organization, slug, Clock, Actor);

    private static Lesson NewLesson(Course? course = null, int sort = 0, string key = "content", int version = 1) =>
        Lesson.Create(LessonId, course ?? NewCourse(), sort, key, version, Clock, Actor);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_DerivesScopeAndStartsDraftWithoutMutatingCourse(bool scoped)
    {
        var course = NewCourse(scoped ? Organization : null);
        var lesson = NewLesson(course, sort: 5, key: "content-shape", version: 3);

        course.Id.Should().Be(CourseId);
        course.TenantId.Should().Be(Tenant);
        course.OrganizationId.Should().Be(scoped ? Organization : null);
        course.Status.Should().Be(PublicationStatus.Draft);
        course.Translations.Should().BeEmpty();
        course.Version.Should().Be(0);
        course.CreatedAt.Should().Be(Clock.UtcNow);
        course.CreatedBy.Should().Be(Actor);
        course.UpdatedAt.Should().BeNull();
        course.LevelTaxonomyKey.Should().BeNull();
        course.LevelTaxonomySchemaVersion.Should().BeNull();
        course.LevelBandKey.Should().BeNull();
        lesson.CourseId.Should().Be(course.Id);
        lesson.TenantId.Should().Be(course.TenantId);
        lesson.OrganizationId.Should().Be(course.OrganizationId);
        lesson.Sort.Should().Be(5);
        lesson.ContentTypeKey.Should().Be("content-shape");
        lesson.ContentTypeSchemaVersion.Should().Be(3);
        lesson.Status.Should().Be(PublicationStatus.Draft);
        lesson.Version.Should().Be(0);
        lesson.CreatedAt.Should().Be(Clock.UtcNow);
        lesson.CreatedBy.Should().Be(Actor);
        lesson.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void Publish_EmptyRootsSucceedIndependentlyAndRepeatedCallsPreserveState()
    {
        var course = NewCourse();
        var lesson = NewLesson(course);
        lesson.Publish(Later, Actor).IsSuccess.Should().BeTrue();
        course.Status.Should().Be(PublicationStatus.Draft);
        course.Version.Should().Be(0);
        lesson.Status.Should().Be(PublicationStatus.Published);
        lesson.Version.Should().Be(1);
        lesson.UpdatedAt.Should().Be(Later.UtcNow);
        AssertFailure(lesson.Publish(Clock, Actor),
            "business_rule_violation", "Status", "lockey_education_publish_requires_draft");
        lesson.Version.Should().Be(1);
        lesson.UpdatedAt.Should().Be(Later.UtcNow);

        course.Publish(Later, Actor).IsSuccess.Should().BeTrue();
        course.Status.Should().Be(PublicationStatus.Published);
        course.Version.Should().Be(1);
        AssertFailure(course.Publish(Clock, Actor),
            "business_rule_violation", "Status", "lockey_education_publish_requires_draft");
        course.Version.Should().Be(1);
        course.UpdatedAt.Should().Be(Later.UtcNow);
        lesson.Version.Should().Be(1);
        NewLesson(course).Status.Should().Be(PublicationStatus.Draft);
    }

    [Fact]
    public void Publish_CourseDoesNotPublishExistingLesson()
    {
        var course = NewCourse();
        var lesson = NewLesson(course);
        course.Publish(Later, Actor).IsSuccess.Should().BeTrue();
        lesson.Status.Should().Be(PublicationStatus.Draft);
        lesson.Version.Should().Be(0);
        lesson.UpdatedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddTranslation_CanonicalizesLocaleAndDerivesScopeWhileAdvancingOnlyItsRoot(bool scoped)
    {
        var course = NewCourse(scoped ? Organization : null);
        var lesson = NewLesson(course);
        course.AddTranslation("ZH-hANS-cn", "课程 🧘", "Özet\n日本語", "course-zh", Later, Actor).IsSuccess.Should().BeTrue();
        var translation = course.Translations.Should().ContainSingle().Subject;
        translation.CourseId.Should().Be(course.Id);
        translation.TenantId.Should().Be(course.TenantId);
        translation.OrganizationId.Should().Be(course.OrganizationId);
        translation.Locale.Should().Be("zh-Hans-CN");
        translation.Title.Should().Be("课程 🧘");
        translation.Summary.Should().Be("Özet\n日本語");
        course.Version.Should().Be(1);
        course.UpdatedAt.Should().Be(Later.UtcNow);
        course.UpdatedBy.Should().Be(Actor);
        lesson.Version.Should().Be(0);

        const string body = """{ "heading": "Ders 🎼", "count": 3 }""";
        lesson.AddTranslation("TR-tr", "Ders 🎼", "lesson-tr", body, Later, Actor).IsSuccess.Should().BeTrue();
        var localized = lesson.Translations.Should().ContainSingle().Subject;
        localized.LessonId.Should().Be(lesson.Id);
        localized.TenantId.Should().Be(lesson.TenantId);
        localized.OrganizationId.Should().Be(lesson.OrganizationId);
        localized.Locale.Should().Be("tr-TR");
        localized.Body.Should().Be(body);
        lesson.Version.Should().Be(1);
        lesson.UpdatedAt.Should().Be(Later.UtcNow);
        lesson.UpdatedBy.Should().Be(Actor);
        course.Version.Should().Be(1);
    }

    [Fact]
    public void AddTranslation_DuplicateCanonicalLocaleRefusesWithoutReplacingOriginalOrTouchingAudit()
    {
        var course = NewCourse();
        var lesson = NewLesson(course);
        course.AddTranslation("en-us", "Original", null, "original", Clock, Actor).IsSuccess.Should().BeTrue();
        lesson.AddTranslation("en-us", "Original", "original", "{}", Clock, Actor).IsSuccess.Should().BeTrue();
        AssertFailure(course.AddTranslation("EN-US", "Replacement", "summary", "replacement", Later, Actor),
            "business_rule_violation", "Locale", "lockey_education_locale_already_exists");
        AssertFailure(lesson.AddTranslation("EN-US", "Replacement", "replacement", "{\"a\":1}", Later, Actor),
            "business_rule_violation", "Locale", "lockey_education_locale_already_exists");
        course.Translations.Should().ContainSingle().Which.Title.Should().Be("Original");
        course.Translations.Single().Summary.Should().BeNull();
        lesson.Translations.Should().ContainSingle().Which.Body.Should().Be("{}");
        course.Version.Should().Be(1);
        lesson.Version.Should().Be(1);
        course.UpdatedAt.Should().Be(Clock.UtcNow);
        lesson.UpdatedAt.Should().Be(Clock.UtcNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddTranslation_PublishedOrDeletedRootRefusesAndPreservesGraph(bool deleted)
    {
        var course = NewCourse();
        var lesson = NewLesson(course);
        if (deleted)
        {
            course.SoftDelete(Clock.UtcNow, Actor);
            lesson.SoftDelete(Clock.UtcNow, Actor);
        }
        else
        {
            course.Publish(Clock, Actor).IsSuccess.Should().BeTrue();
            lesson.Publish(Clock, Actor).IsSuccess.Should().BeTrue();
        }

        AssertFailure(course.AddTranslation("en", "Title", null, "course", Later, Actor),
            "business_rule_violation", "Status", "lockey_education_translation_requires_draft");
        AssertFailure(lesson.AddTranslation("en", "Title", "lesson", "{}", Later, Actor),
            "business_rule_violation", "Status", "lockey_education_translation_requires_draft");
        AssertFailure(course.Publish(Later, Actor),
            "business_rule_violation", "Status", "lockey_education_publish_requires_draft");
        AssertFailure(lesson.Publish(Later, Actor),
            "business_rule_violation", "Status", "lockey_education_publish_requires_draft");
        course.Translations.Should().BeEmpty();
        lesson.Translations.Should().BeEmpty();
        course.Version.Should().Be(1);
        lesson.Version.Should().Be(1);
        course.UpdatedAt.Should().Be(Clock.UtcNow);
        lesson.UpdatedAt.Should().Be(Clock.UtcNow);
        course.Status.Should().Be(deleted ? PublicationStatus.Draft : PublicationStatus.Published);
        lesson.Status.Should().Be(course.Status);
    }

    [Fact]
    public void Mutations_InvalidAuditActorRefusesBeforeGraphOrPublicationChanges()
    {
        var course = NewCourse();
        var lesson = NewLesson(course);
        var unassigned = (new UserId[1])[0];
        var addCourse = () => course.AddTranslation("en", "Title", null, "course", Later, unassigned);
        var addLesson = () => lesson.AddTranslation("en", "Title", "lesson", "{}", Later, unassigned);
        var publishCourse = () => course.Publish(Later, unassigned);
        var publishLesson = () => lesson.Publish(Later, unassigned);
        addCourse.Should().Throw<ArgumentException>();
        addLesson.Should().Throw<ArgumentException>();
        publishCourse.Should().Throw<ArgumentException>();
        publishLesson.Should().Throw<ArgumentException>();
        course.Translations.Should().BeEmpty();
        lesson.Translations.Should().BeEmpty();
        course.Status.Should().Be(PublicationStatus.Draft);
        lesson.Status.Should().Be(PublicationStatus.Draft);
        course.Version.Should().Be(0);
        lesson.Version.Should().Be(0);
        course.UpdatedAt.Should().BeNull();
        lesson.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void Translations_ExposedCollectionCannotBeMutatedByDowncast()
    {
        var course = NewCourse();
        var lesson = NewLesson(course);
        ((ICollection<CourseTranslation>)course.Translations).IsReadOnly.Should().BeTrue();
        ((ICollection<LessonTranslation>)lesson.Translations).IsReadOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null, "band")]
    [InlineData(null, 1, null)]
    [InlineData("taxonomy", null, null)]
    [InlineData("taxonomy", 1, null)]
    [InlineData("taxonomy", null, "band")]
    [InlineData(null, 1, "band")]
    [InlineData("taxonomy", 0, "band")]
    [InlineData("taxonomy", -1, "band")]
    [InlineData("", 1, "band")]
    [InlineData("taxonomy", 1, "")]
    public void Create_PartialOrInvalidLevelPinRefuses(string? key, int? version, string? band)
    {
        var create = () => Course.Create(CourseId, Tenant, null, "course", Clock, Actor, key, version, band);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_CompleteLevelPinRemainsExactAcrossPublication()
    {
        var key = new string('k', 100);
        var band = new string('b', 100);
        var course = Course.Create(CourseId, Tenant, null, "course", Clock, Actor, key, 7, band);
        course.Publish(Later, Actor).IsSuccess.Should().BeTrue();
        course.LevelTaxonomyKey.Should().Be(key);
        course.LevelTaxonomySchemaVersion.Should().Be(7);
        course.LevelBandKey.Should().Be(band);
        var lesson = NewLesson(course, key: key, version: 9);
        lesson.Publish(Later, Actor).IsSuccess.Should().BeTrue();
        lesson.ContentTypeKey.Should().Be(key);
        lesson.ContentTypeSchemaVersion.Should().Be(9);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public void CreateLesson_InvalidOrderOrSchemaVersionRefuses(int sort, int version)
    {
        var create = () => NewLesson(sort: sort, version: version);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateLesson_EqualSortValuesArePermitted()
    {
        var course = NewCourse();
        var first = NewLesson(course, sort: 0);
        var second = Lesson.Create(LessonId.From(Guid.Parse("bbbbbbbb-1111-7111-8111-111111111111")), course, 0, "content", 1, Clock, Actor);
        first.Sort.Should().Be(second.Sort);
        course.Version.Should().Be(0);
    }
}
