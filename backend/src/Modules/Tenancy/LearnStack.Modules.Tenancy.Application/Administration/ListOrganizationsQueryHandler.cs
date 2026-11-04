using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Administration;

public sealed class ListOrganizationsQueryHandler(ITenancyAdministrationReader reader, TenancyAdministrationAuthorization authorization)
    : IRequestHandler<ListOrganizationsQuery, Result<OrganizationDirectoryPage>>
{
    public async Task<Result<OrganizationDirectoryPage>> Handle(ListOrganizationsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var admission = await authorization.AdmitReadAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<OrganizationDirectoryPage>.Fail(admission.Error);
            var page = await reader.ListOrganizationsAsync(request.Cursor, Math.Min(request.Limit, CursorPagination.MaxLimit), cancellationToken);
            var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
            if (check.IsFailure) return Result<OrganizationDirectoryPage>.Fail(check.Error);
            return page is null ? TenancyAdministrationFailures.Code<OrganizationDirectoryPage>("lockey_not_found") : Result.Ok(page);
        }
        catch (TenancyAdministrationStorageException failure)
        {
            return authorization.StorageFailure<OrganizationDirectoryPage>(failure);
        }
    }
}
