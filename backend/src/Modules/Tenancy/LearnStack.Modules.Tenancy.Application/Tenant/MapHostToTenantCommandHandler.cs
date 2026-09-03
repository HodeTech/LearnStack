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
    IOrganizationScopeValidator organizations,
    IReservedHostRegistry reservedHosts,
    IHostResolutionInvalidator resolutionCache,
    ITenantContext tenantContext)
    : IRequestHandler<MapHostToTenantCommand, Result<HostMappingDto>>
{
    public async Task<Result<HostMappingDto>> Handle(
        MapHostToTenantCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // TenantContextBehavior has already refused an unresolved context for this
        // command — it carries no marker — so this is a backstop against a future wiring
        // change, not a reachable state. `tenant_mismatch` rather than a key of its own:
        // it is the code the pipeline itself returns for this condition, it is in
        // HttpStatusMap, and a module-specific key would fall through that closed table
        // to a 500 — which is what a fail-closed guard must not answer.
        if (!tenantContext.IsResolved)
        {
            return Result.FailFor<Result<HostMappingDto>>(TenantContextMissing);
        }

        // The organization must be one of this tenant's. Nothing else checks it before
        // the insert: the composite foreign key does, but it raises 23503, which has no
        // arm in HttpStatusMap and therefore answers 500 — after the transaction opened
        // and the tenant was announced. Reading it here turns a caller's mistake into a
        // refusal they can act on, and the foreign key stays as the layer that makes the
        // race impossible rather than merely unlikely.
        if (request.OrganizationId is { } organizationId
            && !await organizations.BelongsToTenantAsync(
                tenantContext.TenantId, organizationId, cancellationToken))
        {
            return Result.FailFor<Result<HostMappingDto>>(
                new Error(
                    new LocalizedMessage("lockey_business_rule_violation"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        [nameof(MapHostToTenantCommand.OrganizationId)] =
                            [new LocalizedMessage("lockey_organization_not_in_tenant")],
                    }));
        }

        var mapping = PlatformHostMapping.Create(
            request.Host,
            tenantContext.TenantId,
            request.OrganizationId,
            request.IsActive,
            request.IsPubliclyLive);

        // A host on Tenancy:PlatformHosts is classified before the resolver is called at
        // all, so a row naming it would be inert — never read, never logged, never
        // counted. ADR-0036 states that precedence is correct and that the losing row is
        // SILENT, and assigns the check to whichever packet builds this writer. Checked
        // after Create, because the comparison is against normalized hosts and Create is
        // what normalizes.
        if (reservedHosts.IsReserved(mapping.Host))
        {
            return Result.FailFor<Result<HostMappingDto>>(
                new Error(
                    new LocalizedMessage("lockey_business_rule_violation"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        [nameof(MapHostToTenantCommand.Host)] =
                            [new LocalizedMessage("lockey_host_reserved")],
                    }));
        }

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

        // The negative cache remembers hosts that resolved to nothing, and this host just
        // stopped being one. Without this call the TTL is the whole mechanism and a host
        // loaded once before it existed keeps its 404 for the rest of that window — which
        // is exactly what a developer meets after seeding.
        //
        // It runs BEFORE the commit, and the guarantee is correspondingly narrower than
        // "the window is closed". TransactionBehavior commits after the handler returns,
        // so a concurrent request arriving between this line and that COMMIT still misses
        // the uncommitted row and re-caches the miss with a fresh TTL. What this does
        // guarantee is the case that actually happens: the request AFTER the write, which
        // is the seeded-host-in-a-browser one.
        //
        // Closing the remainder needs a post-commit seam on IUnitOfWork, whose surface
        // [ADR-0040](../../../../../../docs/decisions/0040-ambient-unit-of-work.md)
        // governs — so it is an amendment, not an edit, and it is owed by the first
        // obligation that cannot tolerate the gap. The outbox dispatch in
        // [Phase 02b](../../../../../../docs/roadmap/phase-02b-events-auth.md) is that
        // obligation; this call joins it there. Until then the residual window is bounded
        // by the same TTL it exists to shorten, so the failure mode is the old one for a
        // few milliseconds rather than a new one.
        await resolutionCache.InvalidateAsync(mapping.Host, cancellationToken);

        return Result.Ok(new HostMappingDto(
            mapping.Host,
            mapping.TenantId,
            mapping.OrganizationId,
            mapping.IsActive,
            mapping.IsPubliclyLive));
    }

    /// <summary>
    /// The pipeline's own answer for an unresolved context, reused rather than restated.
    /// </summary>
    private static readonly Error TenantContextMissing =
        new(new LocalizedMessage("lockey_tenant_mismatch"));
}
