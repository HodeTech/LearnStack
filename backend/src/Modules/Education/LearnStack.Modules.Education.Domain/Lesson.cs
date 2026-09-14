using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Education.Domain;

[TenantOwned]
[OrganizationScoped]
public sealed class Lesson : AuditableEntity<LessonId>, IAggregateRoot<LessonId>, IOrganizationScoped
{
    private readonly List<LessonTranslation> _translations = [];

    private Lesson(LessonId id, string contentTypeKey)
        : base(id) => ContentTypeKey = contentTypeKey;

    // EF materialization populates mapped properties.
    private Lesson() => ContentTypeKey = null!;

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public CourseId CourseId { get; private set; }
    public int Sort { get; private set; }
    public string ContentTypeKey { get; private set; }
    public int ContentTypeSchemaVersion { get; private set; }
    public PublicationStatus Status { get; private set; }
    public IReadOnlyCollection<LessonTranslation> Translations => _translations.AsReadOnly();

    /// <summary>Derives scope from its course; neither creation nor publication writes that root.</summary>
    public static Lesson Create(
        LessonId id,
        Course course,
        int sort,
        string contentTypeKey,
        int contentTypeSchemaVersion,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(course);
        if (!id.IsInitialized() || id.Value == Guid.Empty)
        {
            throw new ArgumentException("The lesson identifier must be assigned.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sort);
        EducationPinKey.EnsureValid(contentTypeKey, nameof(contentTypeKey));
        ArgumentOutOfRangeException.ThrowIfLessThan(contentTypeSchemaVersion, 1);

        var lesson = new Lesson(id, contentTypeKey)
        {
            TenantId = course.TenantId,
            OrganizationId = course.OrganizationId,
            CourseId = course.Id,
            Sort = sort,
            ContentTypeSchemaVersion = contentTypeSchemaVersion,
            Status = PublicationStatus.Draft,
        };
        lesson.MarkCreated(clock.UtcNow, createdBy);
        return lesson;
    }

    public Result<None> AddTranslation(
        string locale, string title, string slug, string body, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (IsDeleted || Status != PublicationStatus.Draft)
        {
            return EducationFailures.BusinessRule("Status", "lockey_education_translation_requires_draft");
        }

        var error = TranslationInput.Validate(locale, title, slug);
        if (error is not null)
        {
            return Result<None>.Fail(error);
        }

        if (!TranslationInput.IsObjectBody(body))
        {
            return Result<None>.Fail(EducationFailures.Validation("Body", "lockey_education_body_invalid"));
        }

        var canonicalLocale = LocaleTag.Canonicalize(locale);
        if (_translations.Exists(translation => translation.Locale == canonicalLocale))
        {
            return EducationFailures.BusinessRule("Locale", "lockey_education_locale_already_exists");
        }

        var translation = new LessonTranslation(this, canonicalLocale, title, slug, body);
        MarkUpdated(clock.UtcNow, updatedBy);
        _translations.Add(translation);
        return Result<None>.Ok(None.Value);
    }

    public Result<None> Publish(IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (IsDeleted || Status != PublicationStatus.Draft)
        {
            return EducationFailures.BusinessRule("Status", "lockey_education_publish_requires_draft");
        }

        MarkUpdated(clock.UtcNow, updatedBy);
        Status = PublicationStatus.Published;
        return Result<None>.Ok(None.Value);
    }
}
