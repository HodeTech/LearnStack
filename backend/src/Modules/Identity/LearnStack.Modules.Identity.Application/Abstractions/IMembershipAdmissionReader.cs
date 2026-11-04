using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Loads only the caller's Active scope-covering Membership root, without profile or Role navigations.</summary>
public interface IMembershipAdmissionReader
{
    Task<Membership?> ReadCurrentAsync(CancellationToken cancellationToken = default);
}
