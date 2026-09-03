using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>
/// Creates the tenant and its default organization, in that order, on one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the design, not a preference.</b> Measured against the shipped
/// policies: the tenant row must exist before the organization, because
/// <c>organizations</c> has a composite foreign key to <c>(tenant_id, id)</c>; and the
/// back-reference has to be a separate update, because <c>tenants.default_organization_id</c>
/// points at a row that does not exist when the tenant is inserted. Three statements, one
/// transaction, one commit — which is the whole of what ADR-0042 sanctions and the reason
/// a second connection was not an option.
/// </para>
/// <para>
/// <b>It never announces anything.</b> <c>TransactionBehavior</c> has already announced
/// the tenant this command names, by reading <c>IProvisionsTenant</c> off the request at
/// step 6. A handler that announced would be an eighth setter of <c>app.tenant_id</c>
/// against a set two ADRs close at seven, and would hand every handler in the solution
/// the ability to move the ambient tenant.
/// </para>
/// <para>
/// <b>Two ports, and the rule counts them.</b> This is the only handler in the solution
/// permitted to take more than one <c>IAggregateWriteStore</c>, which is what
/// <c>Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning</c> asserts. A combined
/// port would hide the cross-aggregate write from the rule that exists to count it.
/// </para>
/// </remarks>
internal sealed class ProvisionTenantCommandHandler(
    ITenantWriteStore tenants,
    IOrganizationWriteStore organizations,
    IClock clock)
    : IRequestHandler<ProvisionTenantCommand, Result<ProvisionedTenantDto>>
{
    public async Task<Result<ProvisionedTenantDto>> Handle(
        ProvisionTenantCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The registry-assigned actor, not a resolved user: provisioning runs before any
        // membership exists, so there is nobody in the tenant to attribute it to. Phase
        // 03's permission check is what will identify the operator who asked.
        var actor = UserId.SystemActor;

        var tenant = Domain.Tenant.Create(
            request.TenantId, request.Slug, request.DisplayName, clock, actor);

        // First, and on its own: the organization's composite foreign key names
        // (tenant_id, id), so the tenant row has to be there before it.
        await tenants.AddAsync(tenant, cancellationToken);

        var organization = Organization.Create(
            request.DefaultOrganizationId,
            request.TenantId,
            request.DefaultOrganizationSlug,
            request.DefaultOrganizationDisplayName,
            clock,
            actor);

        await organizations.AddAsync(organization, cancellationToken);

        // Third, and it cannot be folded into the first: default_organization_id points
        // at a row that does not exist when the tenant is inserted, and the foreign key
        // behind it is MATCH SIMPLE — which skips the check only while the column is
        // null. Setting it on the insert would defeat that and fail.
        tenant.AssignDefaultOrganization(request.DefaultOrganizationId, clock, actor);
        await tenants.UpdateAsync(tenant, cancellationToken);

        return Result.Ok(new ProvisionedTenantDto(
            request.TenantId.Value,
            tenant.Slug,
            request.DefaultOrganizationId.Value,
            organization.Slug));
    }
}
