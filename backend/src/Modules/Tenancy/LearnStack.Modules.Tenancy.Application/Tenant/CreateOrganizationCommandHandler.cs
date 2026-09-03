using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>
/// Adds a second (or third) organization to the ambient tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>The tenant comes from the context, never from the request.</b> That is the ordinary
/// rule provisioning is the single exception to: a request that named its own tenant would
/// let a caller authenticated for tenant A write into tenant B, and the policy would catch
/// it only because the announcement is A's. Taking it from the context means there is
/// nothing to catch.
/// </para>
/// <para>
/// <b>One aggregate, one port</b> — which is what keeps this handler off ADR-0042's
/// allow-list and the cross-aggregate rule at one entry.
/// </para>
/// </remarks>
internal sealed class CreateOrganizationCommandHandler(
    IOrganizationWriteStore organizations,
    ITenantContext tenantContext,
    IClock clock)
    : IRequestHandler<CreateOrganizationCommand, Result<OrganizationDto>>
{
    public async Task<Result<OrganizationDto>> Handle(
        CreateOrganizationCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // TenantContextBehavior has already refused an unresolved context for this
        // command — it carries no marker — so this is a guard against a future wiring
        // change rather than a reachable state, and it fails closed rather than writing
        // an organization under the all-zero tenant.
        if (!tenantContext.IsResolved)
        {
            return Result.FailFor<Result<OrganizationDto>>(
                new Error(new LocalizedMessage("lockey_tenant_context_missing")));
        }

        var organization = Organization.Create(
            request.OrganizationId,
            tenantContext.TenantId,
            request.Slug,
            request.DisplayName,
            clock,
            tenantContext.UserId ?? UserId.SystemActor);

        try
        {
            await organizations.AddAsync(organization, cancellationToken);
        }
        catch (AggregateConflictException conflict)
        {
            var (field, reason) = OrganizationConflict.For(conflict.ConstraintName);

            return Result.FailFor<Result<OrganizationDto>>(
                new Error(
                    new LocalizedMessage("lockey_business_rule_violation"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        [field] = [new LocalizedMessage(reason)],
                    }));
        }

        return Result.Ok(new OrganizationDto(
            organization.Id, organization.TenantId, organization.Slug));
    }
}

/// <summary>
/// Which uniqueness an organization write collided with, as an RFC 7807 entry.
/// </summary>
/// <remarks>
/// Shared with <c>ProvisionTenantCommandHandler</c>'s organization arms so the two answer
/// a caller identically. The top-level code stays <c>business_rule_violation</c>:
/// <c>HttpStatusMap</c> is a closed table of cross-cutting codes and a module-specific key
/// there falls through to 500 — measured, in the round that introduced four of them.
/// </remarks>
internal static class OrganizationConflict
{
    internal static (string Field, string Reason) For(string? constraintName) =>
        constraintName switch
        {
            "ux_organizations_tenant_id_slug" =>
                (nameof(CreateOrganizationCommand.Slug), "lockey_slug_taken"),
            "pk_organizations" or "ux_organizations_tenant_id_id" =>
                (nameof(CreateOrganizationCommand.OrganizationId), "lockey_identifier_taken"),
            _ => ("$", "lockey_business_rule_violation"),
        };
}
