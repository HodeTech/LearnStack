using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Administration;

public sealed class RenameOrganizationCommandHandler(IOrganizationWriteStore store, ITenancyAdministrationReader reader,
    TenancyAdministrationAuthorization authorization, ITenantContext context, IAuditSubject subject, IClock clock, IGuidFactory guids)
    : OrganizationAdministrationCommandHandlerBase(store, reader, authorization, context, subject, clock, guids),
        IRequestHandler<RenameOrganizationCommand, Result<OrganizationMutationResult>>
{
    public Task<Result<OrganizationMutationResult>> Handle(RenameOrganizationCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken);
}
