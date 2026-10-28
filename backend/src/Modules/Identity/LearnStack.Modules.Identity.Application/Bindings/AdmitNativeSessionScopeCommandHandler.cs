using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Bindings;

public sealed class AdmitNativeSessionScopeCommandHandler(ITenantContext context, IMembershipAdmissionReader reader,
    IOperationInvocationContext operations, IAuditSubject subject, INativeSessionAssociationSource associations)
    : IRequestHandler<AdmitNativeSessionScopeCommand, Result<NativeSessionScopeAdmission>>
{
    public async Task<Result<NativeSessionScopeAdmission>> Handle(AdmitNativeSessionScopeCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var valid = context.IsResolved && context.Origin == TenantContextOrigin.HostAndNativeSessionScope
            && context.UserId is { } actor && StronglyTypedId.IsAssigned(actor) && actor != UserId.SystemActor
            && StronglyTypedId.IsAssigned(context.TenantId) && context.TenantId != TenantId.PlatformSentinel
            && context.HostScope is { } host && host.TenantId == context.TenantId
            && (host.OrganizationId is null || host.OrganizationId == context.OrganizationId);
        if (!valid) return Result.Fail<NativeSessionScopeAdmission>(TenantContextFactory.Refused);
        var member = await reader.ReadCurrentAsync(cancellationToken);
        if (member is null || operations.CurrentDecision is not { IsActive: true, Outcome: ProtectedOperationOutcome.Allowed } decision
            || !decision.MembershipIds.Contains(member.Id.Value))
            return Result.Fail<NativeSessionScopeAdmission>(TenantContextFactory.Refused);
        subject.Designate(member);
        var association = associations.Capture(context.UserId!.Value, context.TenantId, context.OrganizationId);
        return association is null ? Result.Fail<NativeSessionScopeAdmission>(TenantContextFactory.Refused)
            : Result.Ok(new NativeSessionScopeAdmission(context.UserId.Value, context.TenantId, context.OrganizationId,
                association.AuthorityId.Value.ToString("D"), association.SubjectKey.Hex));
    }
}
