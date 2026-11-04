using System.Diagnostics;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using OpenTelemetry;

namespace LearnStack.Api.Composition;

/// <summary>Named owning-port storage boundaries run before any trace exporter.</summary>
internal sealed class IdentityStorageSpanProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        IdentityStorageTraceBoundary.ApplyBeforeExport(data);
        TenantLocaleReadTraceBoundary.ApplyBeforeExport(data);
        TenancyAdministrationTraceBoundary.ApplyBeforeExport(data);
    }
}
