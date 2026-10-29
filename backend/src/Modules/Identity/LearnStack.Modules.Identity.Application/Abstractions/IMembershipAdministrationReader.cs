using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>Detached contextual Membership graphs on the current announced ordinary owner.</summary>
public interface IMembershipAdministrationReader
{
    Task<Membership?> ReadDetailAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
    /// <summary>Only the current actor's Active exact contextual Membership; organization absence never falls back.</summary>
    Task<Membership?> ReadOwnAsync(CancellationToken cancellationToken = default);
}
