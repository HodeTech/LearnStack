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
            // Value.ToString(), not the id's own ToString(). Same reason the
            // UserId branch below already reads Value: measured on Vogen 7, an
            // uninitialized id's ToString() is the literal "[UNINITIALIZED]"
            // while interpolating the same value gives "" — so the id's own
            // formatting is not a wire format, and this tag is one.
            // IsInitialized() on the tenant id too, not only on the two below
            // it. This processor runs inside Activity.Start() for every span and
            // must never throw; before the ids became value objects this was a
            // Guid read that could not, and the asymmetry with the sibling
            // branches otherwise reads as an oversight rather than as reliance
            // on ITenantContext's IsResolved-implies-initialized invariant.
            if (context.TenantId.IsInitialized())
            {
                data.SetTag("tenant.id", context.TenantId.Value.ToString());
            }

            if (context.OrganizationId is { } orgId && orgId.IsInitialized())
            {
                data.SetTag("organization.id", orgId.Value.ToString());
            }

            // IsInitialized() before Value: UserId? being non-null says a
            // UserId struct is there, not that it was ever assigned one, and
            // reading Value on an unassigned Vogen id throws. This processor
            // runs inside Activity.Start() for every span and must never throw.
            if (context.UserId is { } userId && userId.IsInitialized())
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
