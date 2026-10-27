using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Contracts.Bindings;

/// <summary>Existing-binding normalization only; the caller independently verifies the provider credential.</summary>
public interface IIdentityBindingReader
{
    Task<IdentityBindingResult> ResolveExistingAsync(GlobalIdentitySubject subject, UserId? claimedActor = null,
        CancellationToken cancellationToken = default);
}
