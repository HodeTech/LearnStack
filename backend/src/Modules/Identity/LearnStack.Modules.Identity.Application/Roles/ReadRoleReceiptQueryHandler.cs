using FluentValidation;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Roles;

public sealed class ReadRoleReceiptQueryHandler(IIdentityReceiptReader reader, IdentityReceiptAuthorization authorization,
    ITenantContext context, IAuditSubject subject) : IRequestHandler<ReadRoleReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadRoleReceiptQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<None>.Fail(admission.Error);
        var target = await reader.ReadRoleAsync(RoleId.From(request.RoleId), cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return check;
        if (target is null || target.Id.Value != request.RoleId || target.TenantId != context.TenantId)
            return IdentityReceiptAuthorization.Fail<None>("lockey_not_found");
        subject.Designate(target);
        return Result.Ok(None.Value);
    }
}

internal sealed class ReadRoleReceiptQueryValidator : AbstractValidator<ReadRoleReceiptQuery>
{
    public ReadRoleReceiptQueryValidator() => RuleFor(query => query.RoleId)
        .Must(id => id != Guid.Empty && id.Version == 7).WithErrorCode("lockey_identifier_required");
}
