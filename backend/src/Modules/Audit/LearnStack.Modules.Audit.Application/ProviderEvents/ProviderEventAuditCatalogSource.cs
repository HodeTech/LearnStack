using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Audit.Application.ProviderEvents;

public sealed class ProviderEventAuditCatalogSource : IAuditCatalogSource
{
    public string ModuleName => "audit";
    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.MustAudit<ProviderEventObservationHandler>("audit.provider_event.observe", OperationType.SecurityEvent, typeof(ProviderEventAuditEvidence));
    }
}
