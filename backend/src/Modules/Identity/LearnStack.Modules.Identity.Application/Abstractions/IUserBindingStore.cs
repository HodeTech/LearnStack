using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Exact self-binding access on the already prepared global owner, never a directory.</summary>
public interface IUserBindingStore
{
    Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
}
