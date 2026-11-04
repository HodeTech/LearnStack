using FluentValidation;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Memberships;

/// <summary>Exact original Membership attribution, with no historical profile or assignment disclosure.</summary>
public abstract class MembershipReceiptQueryHandlerBase(IIdentityReceiptReader reader,
    IdentityReceiptAuthorization authorization, ITenantContext context, IAuditSubject subject)
{
    protected async Task<Result<None>> ExecuteAsync(object request, Guid id, bool own, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<None>.Fail(admission.Error);
        var target = await reader.ReadMembershipAsync(MembershipId.From(id), cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return check;
        if (target is null || target.Id.Value != id || target.TenantId != context.TenantId
            || target.OrganizationId != context.OrganizationId || own && target.UserId != context.UserId)
            return IdentityReceiptAuthorization.Fail<None>("lockey_not_found");
        // Terminal targets remain historical subjects only. The independent
        // current authorization above may come from a current successor.
        subject.Designate(target);
        return Result.Ok(None.Value);
    }
}

public sealed class ReadMembershipReceiptQueryHandler(IIdentityReceiptReader reader, IdentityReceiptAuthorization authorization,
    ITenantContext context, IAuditSubject subject) : MembershipReceiptQueryHandlerBase(reader, authorization, context, subject),
    IRequestHandler<ReadMembershipReceiptQuery, Result<None>>
{
    public Task<Result<None>> Handle(ReadMembershipReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(request, request.MembershipId, false, cancellationToken);
    }
}

public sealed class ReadMemberProfileReceiptQueryHandler(IIdentityReceiptReader reader, IdentityReceiptAuthorization authorization,
    ITenantContext context, IAuditSubject subject) : MembershipReceiptQueryHandlerBase(reader, authorization, context, subject),
    IRequestHandler<ReadMemberProfileReceiptQuery, Result<None>>
{
    public Task<Result<None>> Handle(ReadMemberProfileReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(request, request.MembershipId, false, cancellationToken);
    }
}

public sealed class ReadOwnMembershipProfileReceiptQueryHandler(IIdentityReceiptReader reader, IdentityReceiptAuthorization authorization,
    ITenantContext context, IAuditSubject subject) : MembershipReceiptQueryHandlerBase(reader, authorization, context, subject),
    IRequestHandler<ReadOwnMembershipProfileReceiptQuery, Result<None>>
{
    public Task<Result<None>> Handle(ReadOwnMembershipProfileReceiptQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(request, request.MembershipId, true, cancellationToken);
    }
}

internal sealed class ReadMembershipReceiptQueryValidator : AbstractValidator<ReadMembershipReceiptQuery>
{
    public ReadMembershipReceiptQueryValidator() => RuleFor(query => query.MembershipId)
        .Must(id => id != Guid.Empty && id.Version == 7).WithErrorCode("lockey_identifier_required");
}

internal sealed class ReadMemberProfileReceiptQueryValidator : AbstractValidator<ReadMemberProfileReceiptQuery>
{
    public ReadMemberProfileReceiptQueryValidator() => RuleFor(query => query.MembershipId)
        .Must(id => id != Guid.Empty && id.Version == 7).WithErrorCode("lockey_identifier_required");
}

internal sealed class ReadOwnMembershipProfileReceiptQueryValidator : AbstractValidator<ReadOwnMembershipProfileReceiptQuery>
{
    public ReadOwnMembershipProfileReceiptQueryValidator() => RuleFor(query => query.MembershipId)
        .Must(id => id != Guid.Empty && id.Version == 7).WithErrorCode("lockey_identifier_required");
}
