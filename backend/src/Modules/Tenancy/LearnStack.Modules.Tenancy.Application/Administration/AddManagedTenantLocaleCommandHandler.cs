using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Administration;

public sealed class AddManagedTenantLocaleCommandHandler(ITenantWriteStore store, ITenancyAdministrationReader reader,
    TenancyAdministrationAuthorization authorization, ITenantContext context, IAuditSubject subject, IClock clock, IGuidFactory guids)
    : TenantAdministrationCommandHandlerBase(store, reader, authorization, context, subject, clock, guids),
        IRequestHandler<AddManagedTenantLocaleCommand, Result<long>>
{
    public Task<Result<long>> Handle(AddManagedTenantLocaleCommand request, CancellationToken cancellationToken) =>
        ExecuteAsync(request, cancellationToken);
}
