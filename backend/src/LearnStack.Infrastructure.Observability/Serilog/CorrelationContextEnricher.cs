using LearnStack.SharedKernel.Tenancy;
using Serilog.Core;
using Serilog.Events;

namespace LearnStack.Infrastructure.Observability.Serilog;

/// <summary>
/// Serilog enricher that copies the cross-cutting correlation tags from
/// <see cref="ITenantContextAccessor"/> onto every <see cref="LogEvent"/>:
/// <c>tenant.id</c>, <c>organization.id</c>, <c>user.id</c>,
/// <c>correlation.id</c>, <c>module</c>. Per ADR-0032 § Sub-decision 8 the
/// Serilog implementation owns the cross-cutting log shape; the same five
/// fields ride on every OTel span via
/// <c>TenantContextSpanProcessor</c>.
/// </summary>
/// <remarks>
/// The accessor is queried on every event. When no scope has populated
/// the accessor (warm-up logs at process start, background tasks before
/// any handler scope opens), the enricher no-ops — matching the same
/// no-throw contract the OTel processor guarantees per ADR-0032
/// § Sub-decision 10.
/// </remarks>
public sealed class CorrelationContextEnricher(ITenantContextAccessor accessor) : ILogEventEnricher
{
    private readonly ITenantContextAccessor _accessor = accessor
        ?? throw new ArgumentNullException(nameof(accessor));

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var context = _accessor.Current;
        if (context is null) return;

        if (context.IsResolved)
        {
            logEvent.AddOrUpdateProperty(
                propertyFactory.CreateProperty("tenant.id", context.TenantId.ToString()));

            if (context.OrganizationId is { } orgId)
            {
                logEvent.AddOrUpdateProperty(
                    propertyFactory.CreateProperty("organization.id", orgId.ToString()));
            }

            // IsInitialized() before Value - see TenantContextSpanProcessor;
            // an enricher that throws takes down the log line it enriches.
            if (context.UserId is { } userId && userId.IsInitialized())
            {
                logEvent.AddOrUpdateProperty(
                    propertyFactory.CreateProperty("user.id", userId.Value.ToString()));
            }
        }

        if (!string.IsNullOrWhiteSpace(context.CorrelationId))
        {
            logEvent.AddOrUpdateProperty(
                propertyFactory.CreateProperty("correlation.id", context.CorrelationId));
        }

        if (!string.IsNullOrWhiteSpace(context.ModuleName))
        {
            logEvent.AddOrUpdateProperty(
                propertyFactory.CreateProperty("module", context.ModuleName));
        }
    }
}
