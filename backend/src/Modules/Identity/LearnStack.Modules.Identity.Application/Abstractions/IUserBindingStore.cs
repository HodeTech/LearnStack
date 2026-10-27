using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Exact self-binding access on the already prepared global owner, never a directory.</summary>
public interface IUserBindingStore : IAggregateWriteStore<User, UserId>
{
    Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default);

    // The root remains countable; no User update owner or lifecycle grant is admitted.
    Task IAggregateWriteStore<User, UserId>.UpdateAsync(User aggregate, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Global identity binding updates are not admitted.");
}
