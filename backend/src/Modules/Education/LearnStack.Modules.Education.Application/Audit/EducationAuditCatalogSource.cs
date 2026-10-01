using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Education.Application.Audit;

/// <summary>Trusted verification reads only; writer classifications land with Step 3.</summary>
public sealed class EducationAuditCatalogSource : IAuditCatalogSource
{
    public string ModuleName => "education";

    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Off<GetCourseSeedStateQuery>();
        builder.Off<GetLessonSeedStateQuery>();
    }
}
