using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Contracts.Seeding;

/// <summary>Fixed development adoption, independently gated; possession of subject facts grants no authority.</summary>
public interface ISeedIdentityBindingAdopter
{
    Task<IdentityBindingResult> AdoptAsync(GlobalIdentitySubject subject, UserId declaredActor,
        CancellationToken cancellationToken = default);
}

/// <summary>The confined external seed owner supplies verified provider facts and exact declared actors.</summary>
public interface ISeedIdentityFoundationAdmission
{
    ValueTask<bool> AllowsAsync(GlobalIdentitySubject subject, UserId declaredActor,
        CancellationToken cancellationToken = default);
}

public sealed class DenyAllSeedIdentityFoundationAdmission : ISeedIdentityFoundationAdmission
{
    public ValueTask<bool> AllowsAsync(GlobalIdentitySubject subject, UserId declaredActor,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}
