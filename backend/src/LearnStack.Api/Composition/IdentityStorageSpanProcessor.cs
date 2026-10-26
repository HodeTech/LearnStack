using System.Diagnostics;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using OpenTelemetry;

namespace LearnStack.Api.Composition;

/// <summary>G19's named Identity boundary runs before any trace exporter.</summary>
internal sealed class IdentityStorageSpanProcessor : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data) => IdentityStorageTraceBoundary.ApplyBeforeExport(data);
}
