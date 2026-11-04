using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Administration;

public sealed class ReadOrganizationReceiptQueryHandler(ITenancyAdministrationReader reader,
    TenancyAdministrationAuthorization authorization, ITenantContext context, IAuditSubject subject)
    : IRequestHandler<ReadOrganizationReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadOrganizationReceiptQuery request, CancellationToken cancellationToken)
    {
        try
        {
            var admission = await authorization.AdmitReadAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<None>.Fail(admission.Error);
            var target = await reader.ReadOrganizationReceiptAsync(request.OrganizationId, cancellationToken);
            var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
            if (check.IsFailure) return check;
            if (target is null || target.Id != request.OrganizationId || target.TenantId != context.TenantId)
                return TenancyAdministrationFailures.Code<None>("lockey_not_found");
            subject.Designate(target);
            return Result.Ok(None.Value);
        }
        catch (TenancyAdministrationStorageException failure)
        {
            return authorization.StorageFailure<None>(failure);
        }
    }
}

public sealed class ReadDefaultOrganizationReceiptQueryHandler(ITenancyAdministrationReader reader,
    TenancyAdministrationAuthorization authorization, ITenantContext context, IAuditSubject subject)
    : IRequestHandler<ReadDefaultOrganizationReceiptQuery, Result<None>>
{
    public async Task<Result<None>> Handle(ReadDefaultOrganizationReceiptQuery request, CancellationToken cancellationToken)
    {
        try
        {
            var admission = await authorization.AdmitReadAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<None>.Fail(admission.Error);
            if (request.TenantId != context.TenantId) return TenancyAdministrationFailures.Code<None>("lockey_not_found");
            var target = await reader.ReadDefaultOrganizationReceiptAsync(request.TenantId, cancellationToken);
            var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
            if (check.IsFailure) return check;
            if (target is null || target.Id != request.TenantId || target.Id != context.TenantId)
                return TenancyAdministrationFailures.Code<None>("lockey_not_found");
            subject.Designate(target);
            return Result.Ok(None.Value);
        }
        catch (TenancyAdministrationStorageException failure)
        {
            return authorization.StorageFailure<None>(failure);
        }
    }
}

internal sealed class ReadOrganizationReceiptQueryValidator : AbstractValidator<ReadOrganizationReceiptQuery>
{
    public ReadOrganizationReceiptQueryValidator() => RuleFor(query => query.OrganizationId)
        .Must(TenancyAdministrationInput.ValidId).WithErrorCode("lockey_identifier_required");
}

internal sealed class ReadDefaultOrganizationReceiptQueryValidator : AbstractValidator<ReadDefaultOrganizationReceiptQuery>
{
    public ReadDefaultOrganizationReceiptQueryValidator() => RuleFor(query => query.TenantId)
        .Must(id => StronglyTypedId.IsAssigned(id) && id != TenantId.PlatformSentinel && id.Value.Version == 7)
        .WithErrorCode("lockey_identifier_required");
}
