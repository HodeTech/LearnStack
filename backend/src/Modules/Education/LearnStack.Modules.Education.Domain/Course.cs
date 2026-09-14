using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Education.Domain;

[TenantOwned]
[OrganizationScoped]
public sealed class Course : AuditableEntity<CourseId>, IAggregateRoot<CourseId>, IOrganizationScoped
{
    private readonly List<CourseTranslation> _translations = [];

    private Course(CourseId id, string slugKey)
        : base(id) => SlugKey = slugKey;

    // EF materialization populates mapped properties.
    private Course() => SlugKey = null!;

    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public string SlugKey { get; private set; }
    public string? LevelTaxonomyKey { get; private set; }
    public int? LevelTaxonomySchemaVersion { get; private set; }
    public string? LevelBandKey { get; private set; }
    public PublicationStatus Status { get; private set; }
    public IReadOnlyCollection<CourseTranslation> Translations => _translations.AsReadOnly();

    /// <summary>Builds a draft from validated application input, with no definition lookup.</summary>
    public static Course Create(
        CourseId id,
        TenantId tenantId,
        OrganizationId? organizationId,
        string slugKey,
        IClock clock,
        UserId createdBy,
        string? levelTaxonomyKey = null,
        int? levelTaxonomySchemaVersion = null,
        string? levelBandKey = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (!id.IsInitialized() || id.Value == Guid.Empty)
        {
            throw new ArgumentException("The course identifier must be assigned.", nameof(id));
        }

        TenantOwnership.EnsureRealTenant(tenantId, "A course belongs to a tenant.", nameof(tenantId));
        if (organizationId is { } organization
            && (!organization.IsInitialized() || organization.Value == Guid.Empty))
        {
            throw new ArgumentException("An organization identifier must be assigned or absent.", nameof(organizationId));
        }

        EducationSlug.EnsureValid(slugKey, nameof(slugKey));
        EnsureLevelPin(levelTaxonomyKey, levelTaxonomySchemaVersion, levelBandKey);

        var course = new Course(id, slugKey)
        {
            TenantId = tenantId,
            OrganizationId = organizationId,
            LevelTaxonomyKey = levelTaxonomyKey,
            LevelTaxonomySchemaVersion = levelTaxonomySchemaVersion,
            LevelBandKey = levelBandKey,
            Status = PublicationStatus.Draft,
        };
        course.MarkCreated(clock.UtcNow, createdBy);
        return course;
    }

    public Result<None> AddTranslation(
        string locale, string title, string? summary, string slug, IClock clock, UserId updatedBy)
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

        if (summary is not null && !JsonValue.IsStorableText(summary))
        {
            return Result<None>.Fail(EducationFailures.Validation("Summary", "lockey_education_summary_invalid"));
        }

        var canonicalLocale = LocaleTag.Canonicalize(locale);
        if (_translations.Exists(translation => translation.Locale == canonicalLocale))
        {
            return EducationFailures.BusinessRule("Locale", "lockey_education_locale_already_exists");
        }

        var translation = new CourseTranslation(this, canonicalLocale, title, slug, summary);
        // Stamp before attaching: an invalid clock or actor must leave the graph intact.
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

    private static void EnsureLevelPin(string? key, int? version, string? band)
    {
        if (key is null && version is null && band is null)
        {
            return;
        }

        if (key is null || version is null || band is null)
        {
            throw new ArgumentException("A level pin must include taxonomy key, schema version and band key.", nameof(key));
        }

        EducationPinKey.EnsureValid(key, nameof(key));
        EducationPinKey.EnsureValid(band, nameof(band));
        ArgumentOutOfRangeException.ThrowIfLessThan(version.Value, 1, nameof(version));
    }
}
