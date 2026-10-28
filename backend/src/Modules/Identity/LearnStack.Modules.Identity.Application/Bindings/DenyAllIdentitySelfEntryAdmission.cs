using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Bindings;

public sealed class DenyAllIdentitySelfEntryAdmission : IIdentitySelfEntryAdmission
{
    public bool CreationEnabled => false;
    public bool Allows(GlobalIdentitySubject subject, IdentitySelfOperation operation, UserId? claimedActor) => false;
}
