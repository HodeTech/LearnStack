using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Education.Domain;

/// <summary>Contained translation, identified by its parent and canonical locale.</summary>
[TenantOwned]
[OrganizationScoped]
public sealed class CourseTranslation : IOrganizationScoped
{
    internal CourseTranslation(Course parent, string locale, string title, string slug, string? summary)
    {
        CourseId = parent.Id;
        TenantId = parent.TenantId;
        OrganizationId = parent.OrganizationId;
        Locale = locale;
        Title = title;
        Slug = slug;
        Summary = summary;
    }

    // EF materialization populates these mapped properties.
    private CourseTranslation()
    {
        Locale = null!;
        Title = null!;
        Slug = null!;
    }

    public CourseId CourseId { get; private set; }
    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public string Locale { get; private set; }
    public string Title { get; private set; }
    public string Slug { get; private set; }
    public string? Summary { get; private set; }
}
