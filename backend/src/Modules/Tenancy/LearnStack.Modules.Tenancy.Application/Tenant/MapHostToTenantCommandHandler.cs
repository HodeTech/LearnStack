using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>
/// Writes the row that decides whose data an anonymous request sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant comes from the context.</b> A request that named its own tenant here
/// would be the sharpest privilege escalation in the module: the host mapping is what an
/// unauthenticated page load resolves through, so a caller who could name another tenant
/// could point that tenant's domain at their own content — or their domain at that
/// tenant's. The policy checks the row's tenant against the announcement, and the
/// announcement comes from the context.
/// </para>
/// <para>
/// <b>The host is normalized by the aggregate, not here.</b> The resolver compares
/// ordinally against <c>EffectiveHost.Normalize</c>'s output, so a row in any other
/// spelling matches nothing and the tenant 404s on its own domain.
/// </para>
/// </remarks>
internal sealed class MapHostToTenantCommandHandler(
    IPlatformHostMappingStore hosts,
    ITenantContext tenantContext)
    : IRequestHandler<MapHostToTenantCommand, Result<HostMappingDto>>
{
    public async Task<Result<HostMappingDto>> Handle(
        MapHostToTenantCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!tenantContext.IsResolved)
        {
            return Result.FailFor<Result<HostMappingDto>>(
                new Error(new LocalizedMessage("lockey_tenant_context_missing")));
        }

        var mapping = PlatformHostMapping.Create(
            request.Host,
            tenantContext.TenantId,
            request.OrganizationId,
            request.IsActive,
            request.IsPubliclyLive);

        try
        {
            await hosts.AddAsync(mapping, cancellationToken);
        }
        catch (AggregateConflictException)
        {
            // The primary key IS the host, and it is global: one answer per host across
            // every tenant. A collision therefore means the name is claimed — possibly by
            // another tenant, which is why the answer says only that it is taken. Naming
            // the holder would turn this endpoint into an oracle for which hostnames
            // belong to which customers.
            return Result.FailFor<Result<HostMappingDto>>(
                new Error(
                    new LocalizedMessage("lockey_business_rule_violation"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        [nameof(MapHostToTenantCommand.Host)] =
                            [new LocalizedMessage("lockey_host_taken")],
                    }));
        }

        return Result.Ok(new HostMappingDto(
            mapping.Host, mapping.TenantId, mapping.OrganizationId, mapping.IsPubliclyLive));
    }
}
