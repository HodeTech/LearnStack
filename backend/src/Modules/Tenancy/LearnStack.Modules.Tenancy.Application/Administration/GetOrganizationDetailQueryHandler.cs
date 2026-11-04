using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Administration;

public sealed class GetOrganizationDetailQueryHandler(ITenancyAdministrationReader reader,
    TenancyAdministrationAuthorization authorization, ITenantContext context, IAuditSubject subject)
    : IRequestHandler<GetOrganizationDetailQuery, Result<OrganizationDetailDto>>
{
    public async Task<Result<OrganizationDetailDto>> Handle(GetOrganizationDetailQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var admission = await authorization.AdmitReadAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<OrganizationDetailDto>.Fail(admission.Error);
            var detail = await reader.ReadOrganizationDetailAsync(request.OrganizationId, cancellationToken);
            var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
            if (check.IsFailure) return Result<OrganizationDetailDto>.Fail(check.Error);
            var organization = detail?.Organization;
            if (organization is null || organization.Id != request.OrganizationId || organization.TenantId != context.TenantId
                || organization.IsDeleted || organization.DeletedBy is not null)
                return TenancyAdministrationFailures.Code<OrganizationDetailDto>("lockey_not_found");
            subject.Designate(organization);
            var status = organization.Status switch
            {
                OrganizationStatus.Active => ManagedOrganizationStatus.Active,
                OrganizationStatus.Suspended => ManagedOrganizationStatus.Suspended,
                OrganizationStatus.Archived => ManagedOrganizationStatus.Archived,
                _ => throw new InvalidOperationException("The Organization lifecycle state is invalid."),
            };
            return Result.Ok(new OrganizationDetailDto(organization.Id, organization.Slug, organization.DisplayName,
                status, detail!.IsDefault, organization.Version));
        }
        catch (TenancyAdministrationStorageException failure)
        {
            return authorization.StorageFailure<OrganizationDetailDto>(failure);
        }
    }
}
