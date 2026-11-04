using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Contracts.Entry;

/// <summary>Server-only identity comparison data, never a scope or authorization capability.</summary>
public sealed record NativeSessionAssociation(IdentityAuthorityId AuthorityId, IdentitySubjectKey SubjectKey)
{
    public override string ToString() => nameof(NativeSessionAssociation);
}

public interface INativeSessionAssociationSource
{
    NativeSessionAssociation? Capture(UserId actor, TenantId tenant, OrganizationId? organization);
}
