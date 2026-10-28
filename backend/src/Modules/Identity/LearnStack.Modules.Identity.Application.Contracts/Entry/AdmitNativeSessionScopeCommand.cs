using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Entry;

public sealed record NativeSessionScopeAdmission(UserId ActorId, TenantId TenantId, OrganizationId? OrganizationId,
    string AuthorityId, string SubjectKey)
{
    public override string ToString() => nameof(NativeSessionScopeAdmission);
}

/// <summary>Audits one explicit scope publication choice, never ordinary refresh or a cookie-delivery acknowledgement.</summary>
public sealed record AdmitNativeSessionScopeCommand : IRequest<Result<NativeSessionScopeAdmission>>;
