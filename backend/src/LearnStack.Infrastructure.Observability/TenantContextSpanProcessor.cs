using System.Diagnostics;
using LearnStack.SharedKernel.Tenancy;
using OpenTelemetry;

namespace LearnStack.Infrastructure.Observability;

/// <summary>
/// Singleton OpenTelemetry span processor that enriches every started
/// <see cref="Activity"/> with the cross-cutting correlation tags
/// (<c>tenant.id</c>, <c>organization.id</c>, <c>user.id</c>,
/// <c>correlation.id</c>, <c>module</c>) read from the singleton
/// <see cref="ITenantContextAccessor"/>. Per ADR-0032 § Sub-decision 10 the
/// processor must <strong>never</strong> throw — auto-instrumentation
/// libraries create warm-up activities before any handler scope has set the
/// accessor.
/// </summary>
/// <remarks>
/// The architecture test
/// <c>OTel_Pipeline_Includes_TenantContextSpanProcessor</c> asserts the
/// processor is registered on the tracing pipeline. The unit test
/// <c>TenantContextSpanProcessor_DoesNotThrow_When_Context_Missing</c>
/// asserts the no-throw contract.
/// </remarks>
public sealed class TenantContextSpanProcessor(ITenantContextAccessor accessor)
    : BaseProcessor<Activity>
{
    private readonly ITenantContextAccessor _accessor = accessor
        ?? throw new ArgumentNullException(nameof(accessor));

    public override void OnStart(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var context = _accessor.Current;
        if (context is null)
        {
            return;
        }

        if (context.IsResolved)
        {
            // OTel attribute types are string / long / double / bool /
            // array. A bare Guid is ToString-projected at export time
            // with no contract on format (some exporters use "D", others
            // "N"). Pin the wire format here for parity with
            // SentryErrorTracker and Loki dashboards.
            data.SetTag("tenant.id", context.TenantId.ToString());
            if (context.OrganizationId is { } orgId)
            {
                data.SetTag("organization.id", orgId.ToString());
            }

            if (context.UserId is { } userId)
            {
                data.SetTag("user.id", userId.Value.ToString());
            }
        }

        if (!string.IsNullOrWhiteSpace(context.CorrelationId))
        {
            data.SetTag("correlation.id", context.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(context.ModuleName))
        {
            data.SetTag("module", context.ModuleName);
        }
    }
}
