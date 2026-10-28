using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Bindings;

public sealed class DenyAllNativeSessionAssociationSource : INativeSessionAssociationSource
{
    public NativeSessionAssociation? Capture(UserId actor, TenantId tenant, OrganizationId? organization) => null;
}
