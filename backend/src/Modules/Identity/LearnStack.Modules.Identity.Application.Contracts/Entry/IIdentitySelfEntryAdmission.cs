using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Contracts.Entry;

public enum IdentitySelfOperation { State = 1, Bootstrap = 2, Contexts = 3 }

/// <summary>Closed transport provenance checked by each new global-self consumer; qualified subject facts alone are insufficient.</summary>
public interface IIdentitySelfEntryAdmission
{
    bool CreationEnabled { get; }
    bool Allows(GlobalIdentitySubject subject, IdentitySelfOperation operation, UserId? claimedActor);
}
