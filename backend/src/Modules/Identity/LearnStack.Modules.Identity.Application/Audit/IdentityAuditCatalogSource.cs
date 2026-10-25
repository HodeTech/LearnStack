using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Identity.Application.Audit;

public sealed class IdentityAuditCatalogSource : IAuditCatalogSource
{
    public string ModuleName => "identity";

    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.DeclareOffPath("identity.user.create", OperationType.SecurityEvent, OperationClass.Must, typeof(User));
    }
}
