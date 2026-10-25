using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Identity.Application.Bindings;

/// <summary>Deferred tracked User creation; the common owner supplies identity and creation-audit attribution.</summary>
public sealed class CreateUserBindingHandler(IUserBindingStore store, IClock clock)
{
    public async Task<Result<UserId>> HandleAsync(GlobalIdentitySubject subject,
        GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(creation);
        var user = User.Create(subject, creation, clock.UtcNow, UserId.SystemActor);
        await store.AddAsync(user, cancellationToken);
        return Result<UserId>.Ok(user.Id);
    }
}
