using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Modules.Tenancy.Infrastructure.Jobs;

/// <summary>Declares the read-model's meanings; tenantless orchestration constructs no module service.</summary>
public static class BackgroundJobLifecycleProjectionDeclaration
{
    public static BackgroundJobLifecycleProjection Create() =>
        new(nameof(TenantStatus.Trial), nameof(TenantStatus.Active), nameof(TenantStatus.Suspended), nameof(OrganizationStatus.Active));
}
