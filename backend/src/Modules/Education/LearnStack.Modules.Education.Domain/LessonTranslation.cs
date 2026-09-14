using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Education.Domain;

/// <summary>Contained translation, identified by its parent and canonical locale.</summary>
[TenantOwned]
[OrganizationScoped]
public sealed class LessonTranslation : IOrganizationScoped
{
    internal LessonTranslation(Lesson parent, string locale, string title, string slug, string body)
    {
        LessonId = parent.Id;
        TenantId = parent.TenantId;
        OrganizationId = parent.OrganizationId;
        Locale = locale;
        Title = title;
        Slug = slug;
        Body = body;
    }

    // EF materialization populates these mapped properties.
    private LessonTranslation()
    {
        Locale = null!;
        Title = null!;
        Slug = null!;
        Body = null!;
    }

    public LessonId LessonId { get; private set; }
    public TenantId TenantId { get; private set; }
    public OrganizationId? OrganizationId { get; private set; }
    public string Locale { get; private set; }
    public string Title { get; private set; }
    public string Slug { get; private set; }
    public string Body { get; private set; }
}
